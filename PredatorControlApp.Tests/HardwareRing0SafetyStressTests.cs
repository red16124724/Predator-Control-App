using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("SingleInstanceTests")]
    [SupportedOSPlatform("windows")]
    public class HardwareRing0SafetyStressTests : IDisposable
    {
        private readonly string _tempSettingsPath;

        public HardwareRing0SafetyStressTests()
        {
            _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"PredatorHwRing0_{Guid.NewGuid():N}.json");
            AppSettings.CustomFilePath = _tempSettingsPath;
        }

        public void Dispose()
        {
            AppSettings.CustomFilePath = null;
            try
            {
                if (File.Exists(_tempSettingsPath))
                {
                    File.Delete(_tempSettingsPath);
                }
            }
            catch { }
        }

        #region 1. WMI Subsystem & Safe Conversions

        [Fact]
        public void WmiSafeConvertToUInt64_MalformedAndExtremeInputs_NeverThrows()
        {
            // Signed negative numbers (common hardware/COM error codes like -1, -2147483648)
            Assert.True(WmiController.SafeConvertToUInt64((int)-1, out ulong r1));
            Assert.Equal(0xFFFFFFFFFFFFFFFFUL, r1);

            Assert.True(WmiController.SafeConvertToUInt64((long)-1, out ulong r2));
            Assert.Equal(0xFFFFFFFFFFFFFFFFUL, r2);

            Assert.True(WmiController.SafeConvertToUInt64((short)-1, out ulong r3));
            Assert.Equal(0xFFFFUL, r3);

            Assert.True(WmiController.SafeConvertToUInt64((sbyte)-1, out ulong r4));
            Assert.Equal(0xFFUL, r4);

            // Byte arrays (endianness and lengths)
            byte[] eightBytes = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0 };
            Assert.True(WmiController.SafeConvertToUInt64(eightBytes, out ulong r5));
            Assert.Equal(BitConverter.ToUInt64(eightBytes, 0), r5);

            byte[] fourBytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };
            Assert.True(WmiController.SafeConvertToUInt64(fourBytes, out ulong r6));
            Assert.Equal(0x40302010UL, r6);

            byte[] emptyBytes = Array.Empty<byte>();
            Assert.False(WmiController.SafeConvertToUInt64(emptyBytes, out _));

            // Strings (decimal, hex "0x...", invalid)
            Assert.True(WmiController.SafeConvertToUInt64("12345678", out ulong r7));
            Assert.Equal(12345678UL, r7);

            Assert.True(WmiController.SafeConvertToUInt64("0x1A2B", out ulong r8));
            Assert.Equal(0x1A2BUL, r8);

            Assert.False(WmiController.SafeConvertToUInt64("malformed_text_data", out _));

            // Null input
            Assert.False(WmiController.SafeConvertToUInt64(null, out _));
        }

        [Fact]
        public void WmiSafeConvertToUInt32_SignedErrorCodesAndByteArrays_NeverThrows()
        {
            Assert.True(WmiController.SafeConvertToUInt32((int)-1, out uint u1));
            Assert.Equal(0xFFFFFFFFU, u1);

            Assert.True(WmiController.SafeConvertToUInt32((long)-1, out uint u2));
            Assert.Equal(0xFFFFFFFFU, u2);

            byte[] fourBytes = new byte[] { 0x78, 0x56, 0x34, 0x12 };
            Assert.True(WmiController.SafeConvertToUInt32(fourBytes, out uint u3));
            Assert.Equal(0x12345678U, u3);

            Assert.False(WmiController.SafeConvertToUInt32(null, out _));
            Assert.False(WmiController.SafeConvertToUInt32(Array.Empty<byte>(), out _));
        }

        [Fact]
        public async Task WmiController_ConcurrentCommandSpamming_NoDeadlock()
        {
            using var wmi = new WmiController(enableHardware: false);
            var commandsExecuted = new ConcurrentBag<string>();

            wmi.CommandInterceptor = (method, input) =>
            {
                commandsExecuted.Add(method);
            };

            const int ThreadCount = 16;
            const int CallsPerThread = 25;

            var tasks = Enumerable.Range(0, ThreadCount).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < CallsPerThread; i++)
                {
                    wmi.GetGamingSysInfo(AcerProtocol.GamingSysInfoConfigQuery);
                    wmi.SetGamingSysInfo(AcerProtocol.GamingSysInfoWinKeyBit);
                    wmi.SetCoolBoost(i % 2 == 0);
                    wmi.SetUsbCharging(true, 20);
                }
            })).ToArray();

            // Must finish without timing out or deadlocking within 5 seconds
            var allDone = Task.WhenAll(tasks);
            var completed = await Task.WhenAny(allDone, Task.Delay(5000));
            Assert.Same(allDone, completed);
            Assert.NotEmpty(commandsExecuted);
        }

        #endregion

        #region 2. Ring-0 MCHBAR MMIO & PawnIO Safety

        [Fact]
        public void PawnIo_ApplyPowerLimits_OutOfRangeAndExtremeInputs_ClampsSafely()
        {
            var backend = new MockPawnIoBackend(isLoaded: true);
            backend.ConfigureIntelBaseline(tdpWatts: 45, minWatts: 10, maxWatts: 120, pl1Watts: 45, pl2Watts: 65);

            using var controller = new PawnIoController(backend);

            // Negative watts must clamp to minimum floor (5W)
            var resNegative = controller.ApplyPowerLimits(-50, -20);
            Assert.True(resNegative.IsSuccess);
            Assert.Equal(5, resNegative.AppliedPl1Watts);
            Assert.Equal(5, resNegative.AppliedPl2Watts);

            // Inverted inputs (PL2 < PL1) must promote PL2 to >= PL1
            var resInverted = controller.ApplyPowerLimits(80, 45);
            Assert.True(resInverted.IsSuccess);
            Assert.Equal(80, resInverted.AppliedPl1Watts);
            Assert.Equal(80, resInverted.AppliedPl2Watts);

            // Extreme high watts must not overflow the 15-bit RAPL register
            var resExtreme = controller.ApplyPowerLimits(5000, 10000);
            Assert.True(resExtreme.IsSuccess);
            Assert.InRange(resExtreme.AppliedPl1Watts, 5, 10000);
            Assert.True((resExtreme.RawMsrWritten & (1UL << 63)) == 0, "MSR lock bit 63 must never be set");
        }

        [Fact]
        public void PawnIo_WriteMchbarPowerLimits_ZeroUnitsPerWatt_GuardsAgainstStall()
        {
            var backend = new MockPawnIoBackend(isLoaded: true);
            backend.ConfigureIntelBaseline(pl1Watts: 45, pl2Watts: 65);
            using var controller = new PawnIoController(backend);

            // Passing unitsPerWatt = 0 should safely fallback to default 8 units/watt instead of zeroing registers
            bool written = controller.WriteMchbarPowerLimits(45, 65, unitsPerWatt: 0, out ulong rawMmio);
            Assert.True(written);
            Assert.NotEqual(0UL, rawMmio);
            Assert.True((rawMmio & 0x7FFFUL) > 0, "PL1 raw limit must not be 0W");
        }

        [Fact]
        public void PawnIo_DriverLifecycle_ConcurrentCloseAndDispose_Resilient()
        {
            for (int trial = 0; trial < 10; trial++)
            {
                var backend = new MockPawnIoBackend(isLoaded: true);
                var controller = new PawnIoController(backend);

                var tasks = new List<Task>();
                for (int t = 0; t < 10; t++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        controller.EnsureInitialized();
                        controller.ReadPowerUnits(out _, out _);
                        controller.Dispose();
                    }));
                }

                Task.WaitAll(tasks.ToArray());
                // After disposal, EnsureInitialized reports driver not installed without throwing
                Assert.Equal(PowerLimitApplyStatus.DriverNotInstalled, controller.EnsureInitialized());
            }
        }

        #endregion

        #region 3. Fan Command Queue Saturation & Extreme Thermal Inputs

        [Fact]
        public async Task FanCommandQueue_BurstSpeedsSpamming_CoalescesAndBoundsQueue()
        {
            var queue = new FanCommandQueue();
            var appliedSpeeds = new ConcurrentQueue<int>();
            using var holdDrain = new ManualResetEventSlim(false);

            // Gate drain on first command to simulate slow hardware bus
            Task first = queue.Enqueue(FanCommandKind.Speeds, () =>
            {
                holdDrain.Wait();
                appliedSpeeds.Enqueue(1);
            });

            // Burst spam 200 Speeds commands while drain is blocked
            var tasks = new List<Task>();
            for (int i = 2; i <= 200; i++)
            {
                int val = i;
                tasks.Add(queue.Enqueue(FanCommandKind.Speeds, () => appliedSpeeds.Enqueue(val)));
            }

            // Release drain
            holdDrain.Set();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

            // Queue must have coalesced: far fewer than 200 writes must have hit the hardware
            Assert.True(appliedSpeeds.Count < 20, $"Expected coalesced writes < 20, but executed {appliedSpeeds.Count}");
        }

        [Fact]
        public void CurveFollower_ExtremeThermalInputs_ClampsCleanly()
        {
            var follower = new CurveFollower(hysteresisDegrees: 2);
            var curve = new List<Point>
            {
                new(30, 20),
                new(60, 50),
                new(90, 100)
            };

            // Extreme sub-zero / disconnected sensor (<= 0°C) activates hardware safety fallback (>= 20%)
            int coldSpeed = follower.Update(-40.0, curve);
            Assert.True(coldSpeed >= 20);

            // Freezing (0°C) activates hardware safety fallback (>= 20%)
            int zeroSpeed = follower.Update(0.0, curve);
            Assert.True(zeroSpeed >= 20);

            // Extreme overheat (105°C) clamps to maximum curve point (100%)
            int hotSpeed = follower.Update(105.0, curve);
            Assert.Equal(100, hotSpeed);

            // NaN retains previous valid speed without throwing
            int nanSpeed = follower.Update(double.NaN, curve);
            Assert.Equal(100, nanSpeed);

            // Positive infinity retains speed without throwing
            int infSpeed = follower.Update(double.PositiveInfinity, curve);
            Assert.Equal(100, infSpeed);
        }

        [Fact]
        public void Form1_InterpolateCurve_SubZeroAndOverheatTemperatures_SafeClamping()
        {
            var curve = new List<Point>
            {
                new(40, 20),
                new(70, 60),
                new(90, 100)
            };

            Assert.Equal(20, Form1.InterpolateCurve(curve, -40));
            Assert.Equal(20, Form1.InterpolateCurve(curve, 0));
            Assert.Equal(20, Form1.InterpolateCurve(curve, 35));
            Assert.Equal(100, Form1.InterpolateCurve(curve, 105));
            Assert.Equal(100, Form1.InterpolateCurve(curve, 150));
        }

        #endregion

        #region 4. Battery Calibration Recovery & 5-Minute Sleep Threshold

        [Fact]
        public void BatteryCalibrationEtaCalculator_SleepGapAboveFiveMinutes_Rebaselines()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            calc.UpdatePercentage(80, t0);
            calc.UpdateCapacity(60000, t0);

            // 6 minutes later (e.g. system was in sleep or Modern Standby)
            var t1 = t0.AddMinutes(6);
            calc.UpdatePercentage(70, t1);
            calc.UpdateCapacity(52000, t1);

            // Must re-baseline rather than deriving a distorted instant discharge spike
            Assert.Null(calc.SmoothedPercentPerMinute);
            Assert.Equal(70, calc.LastPercent);
            Assert.Equal(52000, calc.LastCapacity);
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_SuddenAcDisconnectInPhase1_Rebaselines()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            // Phase 1 (charging)
            calc.CalculateEta(1, 40000, 50000, 25000, allowWarmupFallback: false, batteryPercent: 80);

            calc.UpdatePercentage(80, t0);
            var t1 = t0.AddSeconds(30);

            // Abrupt disconnect: battery dropped from 80% to 79% while in Phase 1
            calc.UpdatePercentage(79, t1);

            // Re-baselined to 79% without calculating invalid negative charging rate
            Assert.Equal(79, calc.LastPercent);
        }

        #endregion

        #region 5. 4-Zone RGB Brightness Scaling & Packet Boundaries

        [Fact]
        public void SetZoneColors_ZeroBrightness_TurnsAllLedsCompletelyOff()
        {
            using var wmi = new WmiController(enableHardware: false);
            var ledArrayPackets = new List<byte[]>();
            var ledCommands = new List<(string Method, ulong Input)>();

            wmi.LedArrayCommandInterceptor = (method, payload) =>
            {
                ledArrayPackets.Add((byte[])payload.Clone());
            };
            wmi.CommandInterceptor = (method, input) =>
            {
                ledCommands.Add((method, input));
            };

            var colors = new[]
            {
                Color.FromArgb(255, 0, 0),
                Color.FromArgb(0, 255, 0),
                Color.FromArgb(0, 0, 255),
                Color.FromArgb(255, 255, 0)
            };

            // Set 0% brightness
            bool ok = wmi.SetZoneColors(colors, brightness: 0);
            Assert.True(ok);

            // Verify SetGamingLEDColor calls pass zeroed color payloads
            var colorCmds = ledCommands.Where(c => c.Method == "SetGamingLEDColor").ToList();
            foreach (var cmd in colorCmds)
            {
                // Upper 24 bits are B, G, R
                ulong rgb = (cmd.Input >> 8) & 0xFFFFFFUL;
                Assert.Equal(0UL, rgb);
            }

            // Verify 16-byte LED packets have brightness = 0 and zeroed color bytes
            foreach (var pkt in ledArrayPackets)
            {
                Assert.Equal(16, pkt.Length);
                Assert.Equal(0, pkt[2]); // Brightness byte
                Assert.Equal(0, pkt[5]); // Red
                Assert.Equal(0, pkt[6]); // Green
                Assert.Equal(0, pkt[7]); // Blue
            }
        }

        [Fact]
        public void SetZoneColors_NegativeBrightness_ClampsToZeroOff()
        {
            using var wmi = new WmiController(enableHardware: false);
            var ledCommands = new List<(string Method, ulong Input)>();
            wmi.CommandInterceptor = (method, input) => ledCommands.Add((method, input));

            var colors = new[] { Color.Red, Color.Green, Color.Blue, Color.Yellow };
            bool ok = wmi.SetZoneColors(colors, brightness: -25);
            Assert.True(ok);

            var colorCmds = ledCommands.Where(c => c.Method == "SetGamingLEDColor").ToList();
            foreach (var cmd in colorCmds)
            {
                ulong rgb = (cmd.Input >> 8) & 0xFFFFFFUL;
                Assert.Equal(0UL, rgb);
            }
        }

        [Fact]
        public void SetZoneColors_OneTwoThreeFourZones_PacketBoundariesMaintained()
        {
            using var wmi = new WmiController(enableHardware: false);
            var ledArrayPackets = new List<byte[]>();
            wmi.LedArrayCommandInterceptor = (method, payload) => ledArrayPackets.Add((byte[])payload.Clone());

            // 1-zone
            wmi.SetZoneColors(new[] { Color.Cyan }, brightness: 80);
            // 2-zone
            wmi.SetZoneColors(new[] { Color.Red, Color.Blue }, brightness: 80);
            // 3-zone
            wmi.SetZoneColors(new[] { Color.Red, Color.Green, Color.Blue }, brightness: 80);
            // 4-zone
            wmi.SetZoneColors(new[] { Color.Red, Color.Green, Color.Blue, Color.White }, brightness: 80);

            // Every intercepted packet must strictly be 16 bytes
            Assert.NotEmpty(ledArrayPackets);
            foreach (var pkt in ledArrayPackets)
            {
                Assert.Equal(16, pkt.Length);
            }
        }

        #endregion
    }
}
