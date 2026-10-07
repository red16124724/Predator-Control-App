using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PredatorControlApp.Tests
{
    public class ComprehensiveSystemStressTests
    {
        [Fact]
        public void BacklightStateManager_LidClosed_PowerTransitions_NeverTurnsOn()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 80;

            // Close lid
            mgr.OnLidChanged(false, PowerLineStatus.Online, out bool shouldTurnOff, out int targetBright, BatteryChargeStatus.High);
            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);

            // Rapid power state changes while lid is closed
            for (int i = 0; i < 50; i++)
            {
                bool isAc = (i % 2 == 0);
                mgr.OnPowerSourceChanged(isAc, out bool off, out int bright);
                Assert.True(off, $"Failed at iteration {i} (isAc={isAc}): shouldTurnOff must be true when lid is closed");
                Assert.Equal(0, bright);
            }
        }

        [Fact]
        public void BacklightStateManager_SleepState_PowerTransitions_NeverTurnsOn()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 75;

            // Put into sleep
            mgr.OnSuspend(out bool suspendOff, out int suspendBright);
            Assert.True(suspendOff);
            Assert.Equal(0, suspendBright);

            // Rapid power state changes while sleeping
            for (int i = 0; i < 50; i++)
            {
                bool isAc = (i % 2 == 0);
                mgr.OnPowerSourceChanged(isAc, out bool off, out int bright);
                Assert.True(off, $"Failed at iteration {i} (isAc={isAc}): shouldTurnOff must be true when sleeping");
                Assert.Equal(0, bright);
            }
        }

        [Fact]
        public void BacklightStateManager_AC_Battery_ChargeStatus_Invariants()
        {
            var mgr = new BacklightStateManager();

            // When charging, must never be considered strictly on battery even if PowerLineStatus is transiently offline
            Assert.False(mgr.IsOnBattery(PowerLineStatus.Offline, BatteryChargeStatus.Charging));
            Assert.False(mgr.IsOnBattery(PowerLineStatus.Unknown, BatteryChargeStatus.Charging));
            Assert.False(mgr.IsOnBattery(PowerLineStatus.Online, BatteryChargeStatus.High));

            // When offline and not charging, must be on battery
            Assert.True(mgr.IsOnBattery(PowerLineStatus.Offline, BatteryChargeStatus.High));
            Assert.True(mgr.IsOnBattery(PowerLineStatus.Offline, BatteryChargeStatus.Low));
            Assert.True(mgr.IsOnBattery(PowerLineStatus.Offline, BatteryChargeStatus.Critical));
        }

        [Fact]
        public void BacklightStateManager_Resume_LidAndBatteryInvariants()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 60;

            // Lid closed before sleep
            mgr.OnLidChanged(false, PowerLineStatus.Online, out _, out _, BatteryChargeStatus.High);
            mgr.OnSuspend(out _, out _);

            // Resume with lid closed: brightness must remain 0
            mgr.OnResume(PowerLineStatus.Online, out bool shouldTurnOffResume, out int targetBright, BatteryChargeStatus.High);
            Assert.True(shouldTurnOffResume);
            Assert.Equal(0, targetBright);

            // Open lid on AC: brightness must restore to 60
            mgr.OnLidChanged(true, PowerLineStatus.Online, out bool turnOffLid, out int brightLid, BatteryChargeStatus.High);
            Assert.False(turnOffLid);
            Assert.Equal(60, brightLid);

            // Open lid on Battery: should turn off backlight by default
            mgr.OnLidChanged(true, PowerLineStatus.Offline, out bool turnOffBat, out int brightBat, BatteryChargeStatus.High);
            Assert.True(turnOffBat);
            Assert.Equal(0, brightBat);
        }

        [Fact]
        public async Task AppSettings_ConcurrentLoadAndSave_Stress()
        {
            const int threadCount = 12;
            const int iterationsPerThread = 25;
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        var s = AppSettings.Load();
                        Assert.NotNull(s);

                        s.FanSpeedCpu = 30 + (threadId * 5 + i) % 70;
                        s.FanSpeedGpu = 40 + (threadId * 3 + i) % 60;
                        s.RgbBrightness = 10 + (threadId * 7 + i) % 90;
                        s.Save();

                        var loaded = AppSettings.Load();
                        Assert.NotNull(loaded);
                        Assert.InRange(loaded.FanSpeedCpu, 0, 100);
                        Assert.InRange(loaded.FanSpeedGpu, 0, 100);
                    }
                });
            }

            await Task.WhenAll(tasks);
        }

        [Fact]
        public async Task FanCurveGraph_ConcurrentPointsMutationAndInterpolation()
        {
            var graph = new FanCurveGraph();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var token = cts.Token;

            // Mutation task: continually modifies Points
            var mutationTask = Task.Run(() =>
            {
                int step = 0;
                while (!token.IsCancellationRequested)
                {
                    int offset = (step++ % 20);
                    graph.Points = new List<Point>
                    {
                        new(30, 10 + offset),
                        new(45, 20 + offset),
                        new(60, 40 + offset),
                        new(75, 60 + offset),
                        new(90, 80 + offset),
                        new(100, 100)
                    };
                    Thread.Sleep(2);
                }
            }, token);

            // Query tasks: 8 threads reading InterpolateSpeed concurrently
            const int readers = 8;
            var readerTasks = new Task[readers];
            for (int r = 0; r < readers; r++)
            {
                readerTasks[r] = Task.Run(() =>
                {
                    var rng = new Random();
                    while (!token.IsCancellationRequested)
                    {
                        int temp = rng.Next(-20, 140);
                        int speed = graph.InterpolateSpeed(temp);
                        Assert.InRange(speed, 0, 100);
                    }
                }, token);
            }

            await Task.Delay(1500);
            cts.Cancel();

            try { await Task.WhenAll(readerTasks.Concat(new[] { mutationTask })); }
            catch (OperationCanceledException) { }

            // Ensure graph still has valid points after mutation
            var pts = graph.Points;
            Assert.NotEmpty(pts);
            Assert.Equal(30, pts[0].X);
            Assert.Equal(100, pts[^1].X);
        }

        [Fact]
        public void CurveFollower_ExtremeTemperatureInputs_Robustness()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(95, 100)
            };

            double[] extremeTemps =
            {
                double.MinValue, -1000.0, -1.0, 0.0, 0.0001,
                29.9, 30.0, 50.0, 70.0, 95.0, 100.0,
                150.0, 10000.0, double.MaxValue,
                double.NaN, double.PositiveInfinity, double.NegativeInfinity
            };

            foreach (var t in extremeTemps)
            {
                int speed = follower.Update(t, curve);
                Assert.InRange(speed, 0, 100);
            }
        }

        [Fact]
        public void CurveFollower_DegenerateCurveList_NeverThrows()
        {
            var follower = new CurveFollower();

            // Null or empty list
            Assert.InRange(follower.Update(50, null!), 0, 100);
            Assert.InRange(follower.Update(50, new List<Point>()), 0, 100);

            // Single point list
            Assert.InRange(follower.Update(50, new List<Point> { new(50, 80) }), 0, 100);

            // Identical X points
            var identicalX = new List<Point>
            {
                new(50, 20),
                new(50, 80)
            };
            Assert.InRange(follower.Update(50, identicalX), 0, 100);
            Assert.InRange(follower.Update(30, identicalX), 0, 100);
            Assert.InRange(follower.Update(70, identicalX), 0, 100);
        }

        [Fact]
        public async Task ThemeManager_ConcurrentSetTheme_NoDeadlock()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var token = cts.Token;

            int themeChangedCount = 0;
            Action handler = () => Interlocked.Increment(ref themeChangedCount);
            ThemeManager.ThemeChanged += handler;

            try
            {
                var tasks = new Task[6];
                for (int i = 0; i < tasks.Length; i++)
                {
                    int idx = i;
                    tasks[i] = Task.Run(() =>
                    {
                        var themes = new[] { AppTheme.Dark, AppTheme.Light, AppTheme.System };
                        while (!token.IsCancellationRequested)
                        {
                            var t = themes[idx % themes.Length];
                            ThemeManager.SetTheme(t);
                            bool dark = ThemeManager.IsDarkThemeActive;
                            _ = ThemeManager.CardBg;
                            _ = ThemeManager.FormBg;
                        }
                    }, token);
                }

                await Task.Delay(1000);
                cts.Cancel();

                try { await Task.WhenAll(tasks); }
                catch (OperationCanceledException) { }

                Assert.True(themeChangedCount > 0);
            }
            finally
            {
                ThemeManager.ThemeChanged -= handler;
            }
        }

        [Fact]
        public async Task HistoryBuffer_ConcurrentPushes_NaN_Infinity_Stress()
        {
            var buffer = new HistoryBuffer(60);
            const int threads = 8;
            const int samplesPerThread = 1000;

            var tasks = new Task[threads];
            for (int t = 0; t < threads; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < samplesPerThread; i++)
                    {
                        double? val = (i % 6) switch
                        {
                            0 => null,
                            1 => double.NaN,
                            2 => double.PositiveInfinity,
                            3 => double.NegativeInfinity,
                            4 => -20.0 + (tid + i) % 120,
                            _ => 40.0 + (i % 40)
                        };
                        buffer.Add(val);

                        var range = buffer.Range();
                        if (range.HasValue)
                        {
                            Assert.False(double.IsNaN(range.Value.Min));
                            Assert.False(double.IsNaN(range.Value.Max));
                            Assert.False(double.IsInfinity(range.Value.Min));
                            Assert.False(double.IsInfinity(range.Value.Max));
                            Assert.True(range.Value.Min <= range.Value.Max);
                        }
                    }
                });
            }

            await Task.WhenAll(tasks);

            Assert.Equal(60, buffer.Count);
            var snapshot = buffer.GetSnapshot();
            Assert.Equal(60, snapshot.Length);
        }

        [Fact]
        public async Task EcHidDevice_LocalLock_ConcurrencySafety()
        {
            using var dev = new EcHidDevice();
            Assert.False(dev.IsOpen);

            // Concurrent calls to Exchange on closed device should safely return null without crashing
            var tasks = new Task[10];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    var buf = new byte[64];
                    for (int j = 0; j < 50; j++)
                    {
                        var rep = dev.Exchange(buf);
                        Assert.Null(rep);
                    }
                });
            }

            await Task.WhenAll(tasks);
        }
    }
}
