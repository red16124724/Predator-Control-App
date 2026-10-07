using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class UltraChaosAndStressTests
    {
        #region 1. AppSettings Concurrency & Chaos Fuzzing

        [Fact]
        public void AppSettings_UltraConcurrency_50Threads_NoCorruption()
        {
            var settings = new AppSettings();
            var exceptions = new ConcurrentBag<Exception>();
            const int threadCount = 50;
            const int iterations = 100;

            Parallel.For(0, threadCount, t =>
            {
                try
                {
                    var rng = new Random(t * 1337);
                    for (int i = 0; i < iterations; i++)
                    {
                        switch (i % 6)
                        {
                            case 0:
                                settings.PowerMode = (byte)rng.Next(0, 10);
                                settings.FanMode = (byte)rng.Next(0, 10);
                                settings.CoolBoost = (i % 2 == 0);
                                break;
                            case 1:
                                var copy = settings.CreateSnapshot();
                                Assert.NotNull(copy);
                                break;
                            case 2:
                                settings.Sanitize();
                                break;
                            case 3:
                                var safeCpu = AppSettings.SafeCopyCurve(settings.CpuCurve);
                                Assert.NotNull(safeCpu);
                                break;
                            case 4:
                                lock (settings.CpuCurve)
                                {
                                    settings.CpuCurve.Add(new CurvePointData(rng.Next(-50, 150), rng.Next(-50, 150)));
                                    if (settings.CpuCurve.Count > 20)
                                        settings.CpuCurve.RemoveAt(0);
                                }
                                break;
                            case 5:
                                settings.FanSpeedCpu = rng.Next(-200, 200);
                                settings.FanSpeedGpu = rng.Next(-200, 200);
                                settings.Sanitize();
                                Assert.InRange(settings.FanSpeedCpu, 0, 100);
                                Assert.InRange(settings.FanSpeedGpu, 0, 100);
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
            settings.Sanitize();
            Assert.InRange(settings.PowerMode, (byte)0, (byte)6);
            Assert.InRange(settings.FanMode, (byte)0, (byte)3);
        }

        #endregion

        #region 2. Rapid Power and Fan Mode Switching Races

        [Fact]
        public async Task RapidModeSwitching_ConcurrentWithCurveEvaluation_NoDeadlocks()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var token = cts.Token;
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 20),
                new(45, 35),
                new(60, 50),
                new(75, 70),
                new(90, 85),
                new(100, 100)
            };

            var exceptions = new ConcurrentBag<Exception>();
            byte activePowerMode = 0x01;
            byte activeFanMode = 0x01;
            object modeLock = new();

            // Task 1: Rapid power mode flipper
            var powerTask = Task.Run(() =>
            {
                byte[] modes = { 0x00, 0x01, 0x04, 0x05, 0x06 };
                int idx = 0;
                while (!token.IsCancellationRequested)
                {
                    lock (modeLock)
                    {
                        activePowerMode = modes[idx % modes.Length];
                        idx++;
                    }
                    Thread.SpinWait(50);
                }
            }, token);

            // Task 2: Rapid fan mode flipper
            var fanTask = Task.Run(() =>
            {
                byte[] fanModes = { 0x00, 0x01, 0x02, 0x03 };
                int idx = 0;
                while (!token.IsCancellationRequested)
                {
                    lock (modeLock)
                    {
                        activeFanMode = fanModes[idx % fanModes.Length];
                        idx++;
                    }
                    Thread.SpinWait(50);
                }
            }, token);

            // Task 3: Rapid fan curve evaluation
            var curveTask = Task.Run(() =>
            {
                var rng = new Random();
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        double temp = rng.NextDouble() * 120.0;
                        int speed = follower.Update(temp, curve);
                        Assert.InRange(speed, 0, 100);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }, token);

            await Task.Delay(1500);
            cts.Cancel();

            try { await Task.WhenAll(powerTask, fanTask, curveTask); }
            catch (OperationCanceledException) { }

            Assert.Empty(exceptions);
        }

        #endregion

        #region 3. Extreme Boundary Fuzzing (NaN, Infinity, Extreme Temperatures)

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(double.MinValue)]
        [InlineData(double.MaxValue)]
        [InlineData(-999999.0)]
        [InlineData(999999.0)]
        [InlineData(0.0)]
        public void CurveFollower_ExtremeTemperatureFuzzing_OutputAlwaysClamped(double temp)
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(100, 100)
            };

            int speed = follower.Update(temp, curve);
            Assert.InRange(speed, 0, 100);
        }

        [Fact]
        public void FanCurveGraph_DegeneratePointLists_NeverThrows()
        {
            // Null points
            var n1 = FanCurveGraph.Normalize(null);
            Assert.NotNull(n1);
            Assert.Equal(FanCurveGraph.ControlPointCount, n1.Count);

            // Empty points
            var n2 = FanCurveGraph.Normalize(new List<Point>());
            Assert.NotNull(n2);
            Assert.Equal(FanCurveGraph.ControlPointCount, n2.Count);

            // Reversed temperatures
            var reversed = new List<Point>
            {
                new(100, 100),
                new(90, 80),
                new(70, 50),
                new(30, 10)
            };
            var n3 = FanCurveGraph.Normalize(reversed);
            Assert.NotNull(n3);
            Assert.Equal(FanCurveGraph.ControlPointCount, n3.Count);
            // Must be strictly monotonic in X
            for (int i = 1; i < n3.Count; i++)
            {
                Assert.True(n3[i].X >= n3[i - 1].X);
            }

            // Extreme out of bounds coordinates
            var outOfBounds = new List<Point>
            {
                new(-500, -100),
                new(1000, 2000),
                new(50, 50)
            };
            var n4 = FanCurveGraph.Normalize(outOfBounds);
            Assert.NotNull(n4);
            foreach (var pt in n4)
            {
                Assert.InRange(pt.X, 30, 100);
                Assert.InRange(pt.Y, 0, 100);
            }
        }

        #endregion

        #region 4. 16:9 Maximize Calculation Fuzzing across 100+ Resolutions

        [Fact]
        public void Calculate16x9MaximizedBounds_Fuzzing150Resolutions_AlwaysValid16x9()
        {
            // Test standard resolutions
            var testCases = new List<Rectangle>
            {
                new(0, 0, 1920, 1080),  // 1080p 16:9
                new(0, 0, 2560, 1440),  // 1440p 16:9
                new(0, 0, 3840, 2160),  // 4K 16:9
                new(0, 0, 7680, 4320),  // 8K 16:9
                new(0, 0, 1280, 720),   // 720p 16:9
                new(0, 0, 3440, 1440),  // Ultrawide 21:9
                new(0, 0, 5120, 1440),  // Super Ultrawide 32:9
                new(0, 0, 2560, 1080),  // Ultrawide
                new(0, 0, 1080, 1920),  // Portrait 9:16
                new(0, 0, 1440, 2560),  // Portrait
                new(0, 0, 1920, 1200),  // 16:10
                new(0, 0, 2560, 1600),  // 16:10
                new(0, 0, 1024, 768),   // 4:3
                new(0, 0, 1280, 1024),  // 5:4
                new(0, 0, 3000, 2000),  // 3:2
                new(100, 100, 1920, 1040), // Taskbar offset
                new(-1920, 0, 1920, 1080), // Secondary monitor left
                new(1920, 50, 2560, 1390), // Secondary monitor right
            };

            // Fuzz with random widths and heights
            var rng = new Random(42);
            for (int i = 0; i < 150; i++)
            {
                int x = rng.Next(-3000, 3000);
                int y = rng.Next(-1000, 1000);
                int w = rng.Next(100, 8000);
                int h = rng.Next(100, 5000);
                testCases.Add(new Rectangle(x, y, w, h));
            }

            foreach (var wa in testCases)
            {
                var bounds = Form1.Calculate16x9MaximizedBounds(wa);

                // Assert bounds fit strictly within working area
                Assert.True(bounds.Width <= wa.Width, $"Width {bounds.Width} exceeds wa.Width {wa.Width}");
                Assert.True(bounds.Height <= wa.Height, $"Height {bounds.Height} exceeds wa.Height {wa.Height}");

                // Assert bounds are centered
                int expectedX = wa.X + (wa.Width - bounds.Width) / 2;
                int expectedY = wa.Y + (wa.Height - bounds.Height) / 2;
                Assert.Equal(expectedX, bounds.X);
                Assert.Equal(expectedY, bounds.Y);

                // Assert positive dimensions
                Assert.True(bounds.Width > 0);
                Assert.True(bounds.Height > 0);

                // If working area is at least 1024x640, aspect ratio should be 16:9
                if (wa.Width >= 1024 && wa.Height >= 576)
                {
                    // Target height is (width * 9) / 16
                    int expectedHeightFromWidth = (bounds.Width * 9) / 16;
                    Assert.InRange(Math.Abs(bounds.Height - expectedHeightFromWidth), 0, 1);
                }
            }
        }

        [Theory]
        [InlineData(0, 0, 1, 1)]
        [InlineData(0, 0, 10, 10)]
        [InlineData(0, 0, 50, 100)]
        [InlineData(-500, -500, 200, 200)]
        public void Calculate16x9MaximizedBounds_ExtremeTinyResolutions_DoesNotCrash(int x, int y, int w, int h)
        {
            var wa = new Rectangle(x, y, w, h);
            var bounds = Form1.Calculate16x9MaximizedBounds(wa);
            Assert.True(bounds.Width > 0);
            Assert.True(bounds.Height > 0);
            Assert.True(bounds.Width <= wa.Width);
            Assert.True(bounds.Height <= wa.Height);
        }

        #endregion

        #region 5. RegistrySafety Path Validation

        [Theory]
        [InlineData("SOFTWARE\\PredatorControl", true)]
        [InlineData("SOFTWARE\\PredatorControl\\Profiles", true)]
        [InlineData("SOFTWARE\\PredatorControl\\Profiles\\MyGame", true)]
        [InlineData("SOFTWARE\\PredatorControlEvil", false)]
        [InlineData("SOFTWARE\\PredatorControl_Hacked", false)]
        [InlineData("SOFTWARE\\PredatorControl\\..\\Windows", false)]
        [InlineData("SOFTWARE\\PredatorControl/../Other", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("   ", false)]
        public void RegistrySafety_IsSafePath_StrictContainment(string? path, bool expectedSafe)
        {
            bool safe = RegistrySafety.IsSafePath(path!);
            Assert.Equal(expectedSafe, safe);
        }

        #endregion

        #region 6. HistoryBuffer Concurrency & NaN Resilience

        [Fact]
        public async Task HistoryBuffer_UltraConcurrency_50Threads_NoNaNLeak()
        {
            var buffer = new HistoryBuffer(120);
            const int threads = 50;
            const int pushesPerThread = 500;

            var tasks = new Task[threads];
            for (int t = 0; t < threads; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    var rng = new Random(tid);
                    for (int i = 0; i < pushesPerThread; i++)
                    {
                        double? val = (i % 5 == 0) ? double.NaN : ((i % 7 == 0) ? double.PositiveInfinity : rng.Next(20, 95));
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

                        var avg = buffer.Average();
                        if (avg.HasValue)
                        {
                            Assert.False(double.IsNaN(avg.Value));
                            Assert.False(double.IsInfinity(avg.Value));
                        }
                    }
                });
            }

            await Task.WhenAll(tasks);
            var snapshot = buffer.GetSnapshot();
            Assert.True(snapshot.Length <= 120);
        }

        #endregion

        #region 7. SecureNamedPipeIpc Rapid Flood & Fuzzing

        [Fact]
        public async Task SecureNamedPipeIpc_NullOrEmptyMessages_SafelyHandled()
        {
            bool r1 = await SecureNamedPipeIpc.SendMessageWithVerificationAsync(null!, 100);
            Assert.False(r1);

            bool r2 = await SecureNamedPipeIpc.SendMessageWithVerificationAsync("", 100);
            Assert.False(r2);
        }

        #endregion
    }
}
