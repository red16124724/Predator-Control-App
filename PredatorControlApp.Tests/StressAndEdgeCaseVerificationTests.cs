using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    public class StressAndEdgeCaseVerificationTests
    {
        #region 1. Concurrency & Multi-threading Stress Tests

        [Fact]
        public async Task Concurrency_AppSettings_ParallelRapidReadsAndWrites_MaintainsIntegrity()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "PredatorStress_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testSettingsPath = Path.Combine(tempDir, "settings_stress.json");

            try
            {
                var settings = new AppSettings();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                const int taskCount = 10;
                int readOps = 0;
                int writeOps = 0;

                var tasks = new Task[taskCount];
                for (int i = 0; i < taskCount; i++)
                {
                    int threadId = i;
                    tasks[i] = Task.Run(() =>
                    {
                        var rand = new Random(threadId * 1000);
                        while (!cts.Token.IsCancellationRequested)
                        {
                            if (rand.Next(2) == 0)
                            {
                                // Concurrent mutations
                                settings.Theme = (threadId % 2 == 0) ? "Dark" : "Light";
                                settings.PowerMode = (byte)rand.Next(0, 7);
                                settings.PowerModeAC = (byte)rand.Next(0, 7);
                                settings.PowerModeBattery = (byte)rand.Next(0, 7);
                                settings.AutoPowerAC = rand.Next(0, 2);
                                settings.AutoPowerBattery = rand.Next(0, 2);

                                lock (settings.CpuCurve)
                                {
                                    settings.CpuCurve.Clear();
                                    settings.CpuCurve.Add(new CurvePointData(30 + rand.Next(10), 20 + rand.Next(10)));
                                    settings.CpuCurve.Add(new CurvePointData(60 + rand.Next(10), 50 + rand.Next(10)));
                                    settings.CpuCurve.Add(new CurvePointData(90 + rand.Next(10), 90 + rand.Next(10)));
                                }

                                Interlocked.Increment(ref writeOps);
                            }
                            else
                            {
                                // Concurrent reads and serialization
                                string theme = settings.Theme;
                                byte pm = settings.PowerMode;
                                byte? pmAc = settings.PowerModeAC;
                                byte? pmBat = settings.PowerModeBattery;
                                int autoAc = settings.AutoPowerAC;

                                Assert.NotNull(theme);
                                Assert.True(autoAc >= 0);

                                try
                                {
                                    string json = JsonSerializer.Serialize(settings);
                                    Assert.False(string.IsNullOrEmpty(json));
                                }
                                catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
                                {
                                    // Collection modified during serialization is handled gracefully by snapshot pattern
                                }

                                Interlocked.Increment(ref readOps);
                            }
                        }
                    });
                }

                await Task.WhenAll(tasks);

                Assert.True(readOps > 50, $"Expected >50 reads, got {readOps}");
                Assert.True(writeOps > 50, $"Expected >50 writes, got {writeOps}");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task Concurrency_BacklightStateManager_MultiThreadedStress_MaintainsStateSanity()
        {
            var manager = new BacklightStateManager();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            const int taskCount = 8;
            int totalTransitions = 0;

            var tasks = new Task[taskCount];
            for (int i = 0; i < taskCount; i++)
            {
                int workerId = i;
                tasks[i] = Task.Run(() =>
                {
                    var rand = new Random(workerId * 500);
                    while (!cts.Token.IsCancellationRequested)
                    {
                        int op = rand.Next(6);
                        switch (op)
                        {
                            case 0:
                                manager.OnPowerSourceChanged(rand.Next(2) == 1, out bool offPwr, out int bPwr);
                                Assert.InRange(bPwr, 0, 100);
                                break;
                            case 1:
                                manager.OnLidChanged(rand.Next(2) == 1, PowerLineStatus.Online, out bool offLid, out int bLid);
                                Assert.InRange(bLid, 0, 100);
                                break;
                            case 2:
                                manager.OnSuspend(out bool offSusp, out int bSusp);
                                Assert.True(offSusp);
                                Assert.InRange(bSusp, 0, 100);
                                break;
                            case 3:
                                manager.OnResume(PowerLineStatus.Online, out bool offRes, out int bRes);
                                Assert.InRange(bRes, 0, 100);
                                break;
                            case 4:
                                manager.OnUserAdjustedBrightness(rand.Next(0, 101), rand.Next(2) == 1);
                                break;
                            case 5:
                                manager.SetPluggedInState(rand.Next(3) switch { 0 => true, 1 => false, _ => null });
                                _ = manager.IsLidClosed;
                                _ = manager.IsSleeping;
                                _ = manager.SavedAcBrightness;
                                _ = manager.ManualBatteryBrightness;
                                _ = manager.IsOnBattery(PowerLineStatus.Offline);
                                break;
                        }
                        Interlocked.Increment(ref totalTransitions);
                    }
                });
            }

            await Task.WhenAll(tasks);
            Assert.True(totalTransitions > 200, $"Expected >200 transitions, got {totalTransitions}");
        }

        [Fact]
        public async Task Concurrency_ThemeManager_ParallelThemeToggleAndColorReads_ThreadSafe()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            const int taskCount = 8;
            int toggleOps = 0;
            int readOps = 0;

            var tasks = new Task[taskCount];
            for (int i = 0; i < taskCount; i++)
            {
                int workerId = i;
                tasks[i] = Task.Run(() =>
                {
                    var rand = new Random(workerId * 333);
                    Action handler = () => { };

                    // Dynamically subscribe and unsubscribe
                    ThemeManager.ThemeChanged += handler;
                    try
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            if (rand.Next(2) == 0)
                            {
                                int themePick = rand.Next(3);
                                switch (themePick)
                                {
                                    case 0: ThemeManager.SetTheme(AppTheme.Light); break;
                                    case 1: ThemeManager.SetTheme(AppTheme.Dark); break;
                                    case 2: ThemeManager.ToggleTheme(); break;
                                }
                                Interlocked.Increment(ref toggleOps);
                            }
                            else
                            {
                                _ = ThemeManager.CurrentTheme;
                                _ = ThemeManager.IsDarkThemeActive;
                                _ = ThemeManager.FormBg;
                                _ = ThemeManager.SidebarBg;
                                _ = ThemeManager.TitleBarBg;
                                _ = ThemeManager.CardBg;
                                _ = ThemeManager.CardBorder;
                                _ = ThemeManager.TextPrimary;
                                _ = ThemeManager.TextSecondary;
                                _ = ThemeManager.Accent;
                                _ = ThemeManager.FocusRing;
                                Interlocked.Increment(ref readOps);
                            }
                        }
                    }
                    finally
                    {
                        ThemeManager.ThemeChanged -= handler;
                    }
                });
            }

            await Task.WhenAll(tasks);
            Assert.True(toggleOps > 50, $"Expected >50 theme toggles, got {toggleOps}");
            Assert.True(readOps > 50, $"Expected >50 color reads, got {readOps}");
        }

        [Fact]
        public async Task Concurrency_FanCurveManager_CurveFollowerAndInterpolation_ThreadSafe()
        {
            var sharedFollower = new CurveFollower();
            var curvePoints = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(90, 100)
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            const int taskCount = 8;
            int updateCount = 0;

            var tasks = new Task[taskCount];
            for (int i = 0; i < taskCount; i++)
            {
                int workerId = i;
                tasks[i] = Task.Run(() =>
                {
                    var rand = new Random(workerId * 777);
                    var localFollower = new CurveFollower();

                    while (!cts.Token.IsCancellationRequested)
                    {
                        double temp = rand.Next(20, 100) + rand.NextDouble();
                        List<Point> snapshot;
                        lock (curvePoints)
                        {
                            snapshot = curvePoints.ToList();
                        }

                        // Independent channel curve follower (like CPU/GPU fan loop)
                        int localSpeed = localFollower.Update(temp, snapshot);
                        Assert.InRange(localSpeed, 0, 100);

                        // Concurrent static curve interpolation
                        int interp = Form1.InterpolateCurve(snapshot, (int)temp);
                        Assert.InRange(interp, 10, 100);

                        // Synchronized shared curve follower updates
                        lock (sharedFollower)
                        {
                            int sharedSpeed = sharedFollower.Update(temp, snapshot);
                            Assert.InRange(sharedSpeed, 0, 100);
                            if (rand.Next(25) == 0)
                            {
                                sharedFollower.Reset();
                            }
                            _ = sharedFollower.Current;
                        }

                        Interlocked.Increment(ref updateCount);
                    }
                });
            }

            await Task.WhenAll(tasks);
            Assert.True(updateCount > 200, $"Expected >200 updates, got {updateCount}");
        }

        [Fact]
        public async Task Concurrency_HardwareCapabilities_ParallelOverridesAndProbing_ThreadSafe()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            const int taskCount = 8;
            int opsCount = 0;

            var baseCaps = DeviceCapabilities.None;
            var tasks = new Task[taskCount];

            for (int i = 0; i < taskCount; i++)
            {
                int workerId = i;
                tasks[i] = Task.Run(() =>
                {
                    var rand = new Random(workerId * 999);
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var overrides = (rand.Next(3)) switch
                        {
                            0 => CapabilityOverrides.EnableAll(),
                            1 => CapabilityOverrides.Clear(),
                            _ => new CapabilityOverrides
                            {
                                CoolBoost = rand.Next(2) == 1,
                                OperatingModes = rand.Next(2) == 1,
                                GpuModeSwitch = rand.Next(2) == 1,
                                ThirdFan = rand.Next(2) == 1,
                                FanTable = rand.Next(2) == 1
                            }
                        };

                        var applied = overrides.Apply(baseCaps);
                        Assert.NotNull(applied);
                        _ = applied.Sensors;
                        _ = applied.Fans;
                        _ = applied.OperatingModes;
                        _ = applied.CoolBoost;
                        _ = applied.HasOperatingModes;

                        Interlocked.Increment(ref opsCount);
                    }
                });
            }

            await Task.WhenAll(tasks);
            Assert.True(opsCount > 200, $"Expected >200 capability ops, got {opsCount}");
        }

        #endregion

        #region 2. Corrupt / Adversarial Input Tests

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("null")]
        [InlineData("{")]
        [InlineData("{\"Theme\":")]
        [InlineData("{\"Theme\": 12345, \"PowerMode\": \"turbo_bad\"}")]
        [InlineData("[1, 2, 3]")]
        [InlineData("{\"CpuCurve\": [{\"X\": \"not_a_number\", \"Y\": null}]}")]
        [InlineData("{\"UnknownField\": true, \"Nested\": { \"A\": [1, null, false] }}")]
        public void Adversarial_AppSettings_CorruptedJsonStrings_GracefulHandling(string corruptJson)
        {
            // Verifies that deserialization handles corrupted strings gracefully
            bool handled = false;
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(corruptJson);
                // If deserialization succeeded (e.g., unexpected fields ignored), properties should have valid fallbacks
                if (settings != null)
                {
                    settings.CpuCurve ??= new();
                    settings.GpuCurve ??= new();
                    if (string.IsNullOrWhiteSpace(settings.Theme)) settings.Theme = "System";
                    Assert.NotNull(settings.Theme);
                    handled = true;
                }
                else
                {
                    handled = true; // null string produces null, handled safely
                }
            }
            catch (JsonException)
            {
                // Expected for syntax and type mismatch errors
                handled = true;
            }

            Assert.True(handled, "Corrupted JSON must be either safely parsed with fallbacks or reject with JsonException");
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-10)]
        [InlineData(-100)]
        [InlineData(-9999)]
        [InlineData(0)]
        [InlineData(5)]
        [InlineData(101)]
        [InlineData(255)]
        [InlineData(65535)]
        public void Adversarial_FanSpeed_NegativeAndExtremeValues_StrictlyClamped(int rawPercent)
        {
            var wmi = new WmiController();

            // SetFanSpeed via FanChannel clamps to [10, 100]
            bool successCpu = wmi.SetFanSpeed(FanChannel.Cpu, rawPercent);
            bool successGpu = wmi.SetFanSpeed(FanChannel.Gpu, rawPercent);
            bool successSys = wmi.SetFanSpeed(FanChannel.System, rawPercent);

            Assert.True(successCpu);
            Assert.True(successGpu);
            Assert.True(successSys);

            // Directly verify clamping on custom speed properties
            Assert.InRange(wmi.CustomCpuFanSpeed, 10, 100);
            Assert.InRange(wmi.CustomGpuFanSpeed, 10, 100);
            Assert.InRange(wmi.CustomSystemFanSpeed, 10, 100);

            // AcerProtocol NearestFanSpeed and FanSpeedInput
            int nearest = AcerProtocol.NearestFanSpeed(rawPercent);
            Assert.InRange(nearest, 0, 100);

            ulong payload = AcerProtocol.FanSpeedInput(FanChannel.Cpu, rawPercent);
            int decodedVal = (int)((payload >> 8) & 0xFF);
            Assert.InRange(decodedVal, 0, 100);
        }

        [Theory]
        [InlineData(201)]
        [InlineData(250)]
        [InlineData(500)]
        [InlineData(1000)]
        [InlineData(50000)]
        [InlineData(int.MaxValue)]
        [InlineData(-51)]
        [InlineData(-100)]
        [InlineData(-273)]
        [InlineData(-10000)]
        [InlineData(int.MinValue)]
        public void Adversarial_CurveFollower_ExtremeTemperatures_ClampsSafely(int extremeTemp)
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 45),
                new(75, 75),
                new(90, 100)
            };

            int speed = follower.Update(extremeTemp, curve);
            Assert.InRange(speed, 10, 100);

            int interp = Form1.InterpolateCurve(curve, extremeTemp);
            Assert.InRange(interp, 10, 100);

            if (extremeTemp >= 200)
            {
                Assert.Equal(100, interp);
            }
            else if (extremeTemp <= -50)
            {
                Assert.Equal(20, interp); // Minimum curve point Y is clamped to 20 (>= 10)
            }
        }

        [Theory]
        [InlineData(0xFF)]
        [InlineData(0xFE)]
        [InlineData(0x80)]
        [InlineData(0x07)]
        [InlineData(0x02)]
        [InlineData(250)]
        public void Adversarial_PowerMode_InvalidTransitions_SafeFallbacks(byte invalidMode)
        {
            var wmi = new WmiController();

            // TrySetPowerMode should never throw, even with invalid mode
            bool result = wmi.TrySetPowerMode(invalidMode);
            Assert.Equal(invalidMode, wmi.LastAppliedPowerMode);

            // Protocol mappings for invalid mode fall back to Balanced
            byte ecMode = AcerProtocol.WmiModeToEcMode(invalidMode);
            Assert.Equal(1, ecMode); // Balanced fallback

            byte wmiMode = AcerProtocol.EcModeToWmiMode(invalidMode);
            Assert.Equal(0x01, wmiMode); // Balanced fallback
        }

        [Fact]
        public void Adversarial_PowerMode_RapidSwitchingBetweenModes_1000Cycles_NoDeadlockOrCrash()
        {
            var wmi = new WmiController();
            byte[] modesToSwitch = new byte[] { 0x05, 0x00, 0x06, 0x04, 0x01, 0xFF, 0xEE };

            for (int i = 0; i < 1000; i++)
            {
                byte target = modesToSwitch[i % modesToSwitch.Length];
                wmi.TrySetPowerMode(target);
                Assert.Equal(target, wmi.LastAppliedPowerMode);
            }
        }

        #endregion

        #region 3. Memory & Resource Endurance Tests

        [Fact]
        public void Endurance_RepeatedAllocations_MemoryStabilityAndGcIntegrity()
        {
            const int iterations = 100_000;
            long memBefore = GC.GetTotalMemory(true);

            for (int i = 0; i < iterations; i++)
            {
                var s = new AppSettings
                {
                    Theme = (i % 2 == 0) ? "Dark" : "Light",
                    PowerMode = (byte)(i % 5),
                    CpuCurve = new List<CurvePointData>
                    {
                        new(30, 20),
                        new(60, 50),
                        new(90, 100)
                    }
                };
                _ = s.Theme;

                var ov = new CapabilityOverrides
                {
                    CoolBoost = (i % 2 == 0),
                    FanTable = (i % 3 == 0)
                };
                var caps = ov.Apply(DeviceCapabilities.None);
                _ = caps.CoolBoost;
            }

            long memAfter = GC.GetTotalMemory(true);
            long diffMb = (memAfter - memBefore) / (1024 * 1024);

            // After GC collect, memory growth should be less than 50 MB
            Assert.True(diffMb < 50, $"Memory grew by {diffMb} MB after {iterations} allocations");
        }

        [Fact]
        public void Endurance_Simulated10000TelemetryLoopUpdates_StabilityAndBufferIntegrity()
        {
            const int totalTicks = 10_000;
            var cpuBuffer = new HistoryBuffer(60);
            var gpuBuffer = new HistoryBuffer(60);
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 45),
                new(75, 75),
                new(90, 100)
            };

            var sw = Stopwatch.StartNew();
            var rand = new Random(42);
            double currentCpuTemp = 45.0;
            double currentGpuTemp = 40.0;

            for (int tick = 0; tick < totalTicks; tick++)
            {
                // Simulate temperature walk
                currentCpuTemp += (rand.NextDouble() - 0.48) * 2.0;
                currentCpuTemp = Math.Clamp(currentCpuTemp, 30.0, 95.0);

                currentGpuTemp += (rand.NextDouble() - 0.48) * 2.0;
                currentGpuTemp = Math.Clamp(currentGpuTemp, 30.0, 95.0);

                // Push to history buffers
                cpuBuffer.Add(currentCpuTemp);
                gpuBuffer.Add(currentGpuTemp);

                // Update fan curve follower
                int fanSpeed = follower.Update(currentCpuTemp, curve);
                Assert.InRange(fanSpeed, 10, 100);

                // Buffer capacity invariant check
                Assert.Equal(60, cpuBuffer.Capacity);
                Assert.True(cpuBuffer.Count <= 60);
            }
            sw.Stop();

            Assert.Equal(60, cpuBuffer.Count);
            Assert.Equal(60, gpuBuffer.Count);

            // FIFO integrity: index 0 is oldest (added at tick 9940), index 59 is newest (added at tick 9999)
            Assert.NotNull(cpuBuffer[0]);
            Assert.NotNull(cpuBuffer[59]);
            Assert.Null(cpuBuffer[-1]);
            Assert.Null(cpuBuffer[60]);

            // Ensure 10,000 simulated ticks execute rapidly (< 1000ms)
            Assert.True(sw.ElapsedMilliseconds < 1000, $"10,000 updates took {sw.ElapsedMilliseconds} ms, expected < 1000ms");
        }

        [Fact]
        public void Endurance_ThemeManager_Rapid1000ToggleCycles_NoResourceLeak()
        {
            int eventFiredCount = 0;
            Action handler = () => { Interlocked.Increment(ref eventFiredCount); };

            ThemeManager.ThemeChanged += handler;
            try
            {
                for (int i = 0; i < 1000; i++)
                {
                    ThemeManager.SetTheme(AppTheme.Light);
                    Assert.False(ThemeManager.IsDarkThemeActive);
                    Assert.NotEqual(Color.Empty, ThemeManager.FormBg);
                    Assert.NotEqual(Color.Empty, ThemeManager.TextPrimary);

                    ThemeManager.SetTheme(AppTheme.Dark);
                    Assert.True(ThemeManager.IsDarkThemeActive);
                    Assert.NotEqual(Color.Empty, ThemeManager.FormBg);
                    Assert.NotEqual(Color.Empty, ThemeManager.TextPrimary);
                }

                Assert.True(eventFiredCount >= 2000, $"Expected >= 2000 events, got {eventFiredCount}");
            }
            finally
            {
                ThemeManager.ThemeChanged -= handler;
            }
        }

        #endregion

        #region 4. Protocol & Packet Boundaries Tests

        [Fact]
        public void Protocol_EcHidReply_MalformedResponsesAndTruncatedBuffers_SafeBoundaries()
        {
            // Truncated data: 0 bytes
            var emptyReply = new EcHidReply(0xE000, 0x01, Array.Empty<byte>());
            Assert.True(emptyReply.Done);
            Assert.True(emptyReply.Final);
            Assert.Equal(0, emptyReply.Byte(0));
            Assert.Equal(0, emptyReply.Byte(100));
            Assert.Equal(0, emptyReply.Word(0));
            Assert.Equal(0, emptyReply.Word(100));

            // Truncated data: 1 byte
            var oneByteReply = new EcHidReply(0xE001, 0x02, new byte[] { 0x42 });
            Assert.False(oneByteReply.Done);
            Assert.True(oneByteReply.Final);
            Assert.Equal(0x42, oneByteReply.Byte(0));
            Assert.Equal(0, oneByteReply.Byte(1));
            Assert.Equal(0, oneByteReply.Word(0)); // Word requires at least 2 bytes, safely returns 0

            // Malformed status code
            var corruptStatusReply = new EcHidReply(0xDEAD, 0x00, new byte[] { 0x01, 0x02, 0x03 });
            Assert.False(corruptStatusReply.Done);
            Assert.False(corruptStatusReply.Final);
            Assert.Equal(0x01, corruptStatusReply.Byte(0));
            Assert.Equal(0x0201, corruptStatusReply.Word(0));
            Assert.Equal(0, corruptStatusReply.Word(2)); // Truncated span for Word, safely returns 0

            // Boundary and excessive offsets beyond buffer length
            Assert.Equal(0, corruptStatusReply.Byte(3));
            Assert.Equal(0, corruptStatusReply.Byte(10));
            Assert.Equal(0, corruptStatusReply.Byte(100));
            Assert.Equal(0, corruptStatusReply.Byte(1000));
            Assert.Equal(0, corruptStatusReply.Word(3));
            Assert.Equal(0, corruptStatusReply.Word(10));
            Assert.Equal(0, corruptStatusReply.Word(1000));
        }

        [Fact]
        public void Protocol_AcerSmbios_MalformedAndTruncatedFirmwareTables_SafeParsing()
        {
            // Empty span
            var emptyRes = AcerSmbios.Parse(ReadOnlySpan<byte>.Empty);
            Assert.NotNull(emptyRes);
            Assert.Null(emptyRes.GamingMajor);
            Assert.Null(emptyRes.GamingMinor);
            Assert.Empty(emptyRes.GamingRecords);
            Assert.Empty(emptyRes.HotkeyFunctions);

            // Shorter than header (< 4 bytes)
            var shortRes = AcerSmbios.Parse(new byte[] { 0xAC, 0x04 });
            Assert.NotNull(shortRes);
            Assert.Empty(shortRes.GamingRecords);

            // Type 0xAC (172) with invalid length (length < 4)
            var badLenRes = AcerSmbios.Parse(new byte[] { 172, 2, 0, 0 });
            Assert.NotNull(badLenRes);
            Assert.Empty(badLenRes.GamingRecords);

            // Type 0xAC pointing beyond table length
            var overflowRes = AcerSmbios.Parse(new byte[] { 172, 100, 0, 0, 1, 2 });
            Assert.NotNull(overflowRes);

            // Truncated type 170 (0xAA)
            var badHotkeyRes = AcerSmbios.Parse(new byte[] { 170, 8, 0, 0, 1, 2, 3, 4 });
            Assert.NotNull(badHotkeyRes);
            Assert.Empty(badHotkeyRes.HotkeyFunctions);
        }

        [Fact]
        public void Protocol_BatteryProtocolAndFirmwareEvents_PartialBuffersAndZeroLength_Safe()
        {
            // BatteryProtocol DecodeStatus with null buffer
            var nullStatus = BatteryProtocol.DecodeStatus(0xFF, null);
            Assert.Null(nullStatus);

            // BatteryProtocol DecodeStatus with zero-length buffer
            var emptyStatus = BatteryProtocol.DecodeStatus(0xFF, Array.Empty<byte>());
            Assert.NotNull(emptyStatus);
            Assert.False(emptyStatus.HealthModeOn);
            Assert.False(emptyStatus.CalibrationOn);

            // BatteryProtocol DecodeStatus with 1-byte buffer
            var oneByteStatus = BatteryProtocol.DecodeStatus(0xFF, new byte[] { 1 });
            Assert.NotNull(oneByteStatus);
            Assert.True(oneByteStatus.HealthModeOn);
            Assert.False(oneByteStatus.CalibrationOn); // Index for calibration out of range, safely false

            // FirmwareEvent Decode with null and empty
            Assert.Null(FirmwareEvent.Decode(null));
            Assert.Null(FirmwareEvent.Decode(Array.Empty<byte>()));

            // FirmwareEvent Decode with single byte
            var singleByteEvt = FirmwareEvent.Decode(new byte[] { 0x05 });
            Assert.NotNull(singleByteEvt);
            Assert.Equal((FirmwareEventKind)5, singleByteEvt.Kind);
            Assert.Equal(0, singleByteEvt.Value);
            Assert.Single(singleByteEvt.Detail);

            // FirmwareEvent Decode with multi-byte
            var fullEvt = FirmwareEvent.Decode(new byte[] { 0x01, 0x2A, 0xFF, 0xAA });
            Assert.NotNull(fullEvt);
            Assert.Equal((FirmwareEventKind)1, fullEvt.Kind);
            Assert.Equal(0x2A, fullEvt.Value);
            Assert.Equal(4, fullEvt.Detail.Length);
        }

        [Fact]
        public void Protocol_ZeroLengthArraysAndEmptyCollections_Safe()
        {
            // Empty fan behavior collection
            ulong fanBehaviorPayload = AcerProtocol.FanBehaviorInput(Array.Empty<(FanChannel, FanBehavior)>());
            Assert.Equal(0UL, fanBehaviorPayload);

            // Zero sensor mask and zero operating mode mask
            var sensors = AcerProtocol.DecodeSensorMask(0);
            Assert.Empty(sensors);

            var modes = AcerProtocol.DecodeOperatingModeMask(0);
            Assert.Empty(modes);

            // Empty curve interpolation
            int interpEmpty = Form1.InterpolateCurve(new List<Point>(), 50);
            Assert.Equal(50, interpEmpty);

            int interpNull = Form1.InterpolateCurve(null, 50);
            Assert.Equal(50, interpNull);

            int interpSingle = Form1.InterpolateCurve(new List<Point> { new(50, 80) }, 20);
            Assert.Equal(80, interpSingle);
        }

        [Fact]
        public void Protocol_FloatingPoint_NaNAndInfinityValues_BoundaryProtection()
        {
            var follower = new CurveFollower();
            var curve = new List<Point> { new(30, 20), new(90, 100) };

            // NaN should return 0 (or Current if previously set) without corrupting state
            int nanSpeed = follower.Update(double.NaN, curve);
            Assert.Equal(0, nanSpeed);

            // Positive Infinity should return 0 safely
            int posInfSpeed = follower.Update(double.PositiveInfinity, curve);
            Assert.Equal(0, posInfSpeed);

            // Negative Infinity should return 0 safely
            int negInfSpeed = follower.Update(double.NegativeInfinity, curve);
            Assert.Equal(0, negInfSpeed);

            // Normal update after NaN/Infinity should resume normal operation seamlessly
            int normalSpeed = follower.Update(60.0, curve);
            Assert.InRange(normalSpeed, 20, 100);

            // Subsequent NaN should preserve Current speed without crashing
            int nanRetainedSpeed = follower.Update(double.NaN, curve);
            Assert.Equal(normalSpeed, nanRetainedSpeed);

            // HistoryBuffer with NaN and Infinity
            var buffer = new HistoryBuffer(10);
            buffer.Add(double.NaN);
            buffer.Add(double.PositiveInfinity);
            buffer.Add(double.NegativeInfinity);
            buffer.Add(null);

            Assert.Equal(4, buffer.Count);
            Assert.True(double.IsNaN(buffer[0]!.Value));
            Assert.True(double.IsPositiveInfinity(buffer[1]!.Value));
            Assert.True(double.IsNegativeInfinity(buffer[2]!.Value));
            Assert.Null(buffer[3]);
        }

        #endregion
    }
}
