using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class ComprehensiveStabilityStressTests
    {
        [Fact]
        public async Task Concurrency_MultiThreaded_WmiController_Calls_DoNotDeadlock()
        {
            using var wmi = new WmiController();
            const int threadCount = 4;
            const int iterationsPerThread = 10;
            var barrier = new Barrier(threadCount);

            var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
            {
                barrier.SignalAndWait(5000);
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    switch (i % 6)
                    {
                        case 0:
                            wmi.SetPowerMode((byte)(i % 5));
                            break;
                        case 1:
                            wmi.SetFanSpeed((byte)(10 + (i % 90)), (byte)(10 + ((i * 2) % 90)));
                            break;
                        case 2:
                            wmi.SetFanBehavior((byte)(1 + (i % 3)));
                            break;
                        case 3:
                            wmi.SetRgbMode(i % 4, (byte)(i % 255), (byte)((i * 3) % 255), (byte)((i * 7) % 255), (byte)(i % 100), 5, 0);
                            break;
                        case 4:
                            wmi.SetZoneColor((i % 4) + 1, (byte)(i % 255), (byte)((i * 2) % 255), (byte)((i * 4) % 255));
                            break;
                        case 5:
                            _ = wmi.CpuTemp;
                            _ = wmi.GpuTemp;
                            _ = wmi.CpuFanRpm;
                            break;
                    }
                }
            })).ToArray();

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
            var completedTask = await Task.WhenAny(Task.WhenAll(tasks), timeoutTask);
            Assert.True(completedTask != timeoutTask, "Multi-threaded WMI controller operations deadlocked or exceeded timeout.");
        }

        [Fact]
        public void CurveFollower_FuzzedInputs_HandlesNaN_Infinity_And_ExtremeTemps()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(90, 100)
            };

            // Test NaN
            int resNan = follower.Update(double.NaN, curve);
            Assert.InRange(resNan, 0, 100);

            // Test Positive and Negative Infinity
            int resPosInf = follower.Update(double.PositiveInfinity, curve);
            Assert.InRange(resPosInf, 0, 100);

            int resNegInf = follower.Update(double.NegativeInfinity, curve);
            Assert.InRange(resNegInf, 0, 100);

            // Test Sub-zero and over-temperature
            int resFreeze = follower.Update(-50.0, curve);
            Assert.InRange(resFreeze, 0, 100);

            int resBoil = follower.Update(300.0, curve);
            Assert.InRange(resBoil, 0, 100);

            // Test rapid resets
            follower.Reset();
            Assert.Null(follower.Current);
        }

        [Fact]
        public void FanCurveInterpolation_FuzzedPoints_NeverThrowsOrCrashes()
        {
            var fuzzedPoints = new List<Point>
            {
                new(0, 0),
                new(20, 100),
                new(10, 50),
                new(-30, 200),
                new(120, -50),
                new(60, 40)
            };

            for (int t = -100; t <= 200; t += 5)
            {
                int speed = Form1.InterpolateCurve(fuzzedPoints, t);
                Assert.InRange(speed, 0, 100);
            }
        }

        [Theory]
        [InlineData(-100, 0x01)]
        [InlineData(-1, 0x01)]
        [InlineData(0, 0x01)]
        [InlineData(1, 0x01)]
        [InlineData(2, 0x02)]
        [InlineData(3, 0x03)]
        [InlineData(4, 0x01)]
        [InlineData(255, 0x01)]
        [InlineData(256, 0x01)]
        [InlineData(int.MaxValue, 0x01)]
        [InlineData(int.MinValue, 0x01)]
        public void FinalizeFanMode_BoundaryValues_AlwaysReturnValidFanMode(int input, byte expected)
        {
            // Reflection test of private FinalizeFanMode in Program
            var method = typeof(Program).GetMethod("FinalizeFanMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            byte result = (byte)method.Invoke(null, new object[] { input })!;
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(0x00, false, 0x00)] // Quiet on AC
        [InlineData(0x00, true, 0x00)]  // Quiet on Battery
        [InlineData(0x01, false, 0x01)] // Balanced on AC
        [InlineData(0x01, true, 0x01)]  // Balanced on Battery
        [InlineData(0x04, false, 0x04)] // Performance on AC
        [InlineData(0x04, true, 0x01)]  // Performance on Battery -> Balanced
        [InlineData(0x05, false, 0x05)] // Turbo on AC
        [InlineData(0x05, true, 0x01)]  // Turbo on Battery -> Balanced
        [InlineData(0x06, false, 0x01)] // Eco on AC -> Balanced
        [InlineData(0x06, true, 0x06)]  // Eco on Battery
        [InlineData(-1, false, 0x01)]   // Invalid -> Balanced
        [InlineData(999, true, 0x01)]   // Out of range -> Balanced
        public void FinalizeMode_BoundaryValues_EnforcesACAndBatteryRules(int mode, bool onBattery, byte expected)
        {
            var method = typeof(Program).GetMethod("FinalizeMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            byte result = (byte)method.Invoke(null, new object[] { mode, onBattery })!;
            Assert.Equal(expected, result);
        }

        [Fact]
        public void HistoryGraphControl_Disposal_UnregistersThemeHandlerWithoutLeaks()
        {
            var graph = new HistoryGraphControl();
            graph.PushSample(55.0, 75.0);
            graph.PushSample(null, null);
            graph.Dispose();

            // Fire ThemeChanged to ensure disposed control does not throw ObjectDisposedException
            ThemeManager.SetTheme(AppTheme.Light);
            ThemeManager.SetTheme(AppTheme.Dark);
        }

        [Fact]
        public void DarkScrollPanel_Disposal_CleansUpMessageFilterWithoutExceptions()
        {
            var panel = new DarkScrollPanel();
            panel.SetDpiScale(1.5f);
            panel.Dispose();

            // Changing theme after disposal should not throw
            ThemeManager.SetTheme(AppTheme.Light);
            ThemeManager.SetTheme(AppTheme.Dark);
        }

        [Fact]
        public void Tachometer_DecodingFuzzing_HandlesCorruptedAndOverflowReadings()
        {
            // Direct 13-bit tachometer readings (< 8192)
            Assert.Equal(0, WmiController.DecodeFanSpeed(0));
            Assert.Equal(2500, WmiController.DecodeFanSpeed(2500));
            Assert.Equal(8191, WmiController.DecodeFanSpeed(8191));

            // Encoded ACPI packet ((rpm << 8) | status)
            // (3000 << 8) | 0x00 = 768000
            ulong validPacket = (3000UL << 8);
            Assert.Equal(3000, WmiController.DecodeFanSpeed(validPacket));

            // Corrupted status byte (non-zero status) returns 0 RPM
            ulong errorPacket = (3000UL << 8) | 0xFFUL;
            Assert.Equal(0, WmiController.DecodeFanSpeed(errorPacket));

            // Overflow bits
            ulong overflowPacket = (99999UL << 8);
            Assert.InRange(WmiController.DecodeFanSpeed(overflowPacket), 0, 8191);
        }

        [Fact]
        public void LightingEffects34_ApplyEffect_All34Effects_ExecuteWithoutCrashing()
        {
            using var wmi = new WmiController();

            for (int i = 0; i <= 33; i++)
            {
                LightingEffectsManager.ApplyEffect(i, wmi, 100, 150, 200, 80, 5, 0, OperatingMode.Balanced);
            }

            LightingEffectsManager.StopSoftwareAnimation();
        }

        [Fact]
        public void EcHidProtocol_RoundTrip_CommandsAndReply()
        {
            var reply = new EcHidReply(0xE000, 0x0001, new byte[] { 10, 20, 30, 40 });
            Assert.True(reply.Done);
            Assert.True(reply.Final);
            Assert.Equal(10, reply.Byte(0));
            Assert.Equal(20, reply.Byte(1));
            Assert.Equal((ushort)(10 | (20 << 8)), reply.Word(0));

            var failedReply = new EcHidReply(0xE002, 0x0001, Array.Empty<byte>());
            Assert.False(failedReply.Done);
            Assert.False(failedReply.Final);
        }
    }
}
