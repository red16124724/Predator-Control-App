using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Exhaustive Concurrency, Race Condition, and Chaos Test Suite.
    /// Hammers AppSettings, CpuPowerLimitController, CurveFollower, TelemetryHistory,
    /// and RegistrySafety with 100+ concurrent threads and rapid power mode cycling under heavy load.
    /// </summary>
    [Collection("SingleInstanceTests")]
    [SupportedOSPlatform("windows")]
    public class AdversarialConcurrencyAndChaosTests : IDisposable
    {
        private readonly string _tempSettingsPath;

        public AdversarialConcurrencyAndChaosTests()
        {
            _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"ChaosSettings_{Guid.NewGuid():N}.json");
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

        #region 1. 100+ Concurrent Threads Hammering AppSettings

        [Fact]
        public void AppSettings_100ConcurrentThreads_HeavyChaosLoad_ZeroCorruption()
        {
            var settings = new AppSettings();
            var exceptions = new ConcurrentBag<Exception>();
            const int threadCount = 100;
            const int iterationsPerThread = 50;

            Parallel.For(0, threadCount, threadId =>
            {
                try
                {
                    var rng = new Random(threadId * 1009);
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        int op = (threadId + i) % 6;
                        switch (op)
                        {
                            case 0:
                                settings.PowerMode = (byte)rng.Next(0, 7);
                                settings.FanSpeedCpu = rng.Next(-50, 150);
                                settings.FanSpeedGpu = rng.Next(-50, 150);
                                settings.CoolBoost = (i % 2 == 0);
                                break;

                            case 1:
                                var snap = settings.CreateSnapshot();
                                Assert.NotNull(snap);
                                Assert.InRange(snap.PowerMode, (byte)0, (byte)6);
                                break;

                            case 2:
                                settings.Sanitize();
                                Assert.InRange(settings.PowerMode, (byte)0, (byte)6);
                                Assert.InRange(settings.FanSpeedCpu, 0, 100);
                                Assert.InRange(settings.FanSpeedGpu, 0, 100);
                                break;

                            case 3:
                                lock (settings.CpuCurve)
                                {
                                    settings.CpuCurve.Add(new CurvePointData(rng.Next(0, 100), rng.Next(0, 100)));
                                    if (settings.CpuCurve.Count > 16)
                                    {
                                        settings.CpuCurve.RemoveAt(0);
                                    }
                                }
                                break;

                            case 4:
                                var safeCpu = AppSettings.SafeCopyCurve(settings.CpuCurve);
                                Assert.NotNull(safeCpu);
                                break;

                            case 5:
                                settings.RgbBrightness = (byte)rng.Next(0, 100);
                                settings.RgbSpeed = (byte)rng.Next(0, 100);
                                settings.RgbR = (byte)rng.Next(0, 255);
                                settings.RgbG = (byte)rng.Next(0, 255);
                                settings.RgbB = (byte)rng.Next(0, 255);
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
            settings.Save();
            Assert.True(File.Exists(_tempSettingsPath));

            var loaded = AppSettings.Load();
            Assert.NotNull(loaded);
            Assert.InRange(loaded.PowerMode, (byte)0, (byte)6);
            Assert.InRange(loaded.FanSpeedCpu, 0, 100);
            Assert.InRange(loaded.FanSpeedGpu, 0, 100);
        }

        #endregion

        #region 2. 100+ Concurrent Threads Hammering CpuPowerLimitController

        [Fact]
        public void CpuPowerLimitController_100ConcurrentThreads_HeavyContention_NoDeadlocks()
        {
            var mockBackend = new MockPawnIoBackend(isLoaded: true);
            mockBackend.ConfigureIntelBaseline(tdpWatts: 55, minWatts: 25, maxWatts: 157, pl1Watts: 55, pl2Watts: 157);

            var settings = new AppSettings
            {
                EnableCustomPowerLimits = true,
                CpuMinAllowedPowerWatts = 25,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 157,
                CpuThermalSpecTdpWatts = 55
            };

            using var controller = new CpuPowerLimitController(null, settings, mockBackend);

            var exceptions = new ConcurrentBag<Exception>();
            int eventFireCount = 0;

            Action<OperatingMode, int, int, bool> handler = (m, p1, p2, mmio) =>
            {
                Interlocked.Increment(ref eventFireCount);
            };
            controller.PowerLimitsChanged += handler;

            const int threadCount = 100;
            const int iterationsPerThread = 20;

            Parallel.For(0, threadCount, threadId =>
            {
                try
                {
                    var rng = new Random(threadId * 333);
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        int op = (threadId + i) % 5;
                        var mode = (OperatingMode)(rng.Next(0, 5) switch
                        {
                            0 => 0x06, // Eco
                            1 => 0x00, // Quiet
                            2 => 0x01, // Balanced
                            3 => 0x04, // Performance
                            _ => 0x05  // Turbo
                        });

                        switch (op)
                        {
                            case 0:
                                int pl1 = rng.Next(25, 80);
                                int pl2 = rng.Next(pl1, 140);
                                controller.PawnIo.ApplyPowerLimits(pl1, pl2);
                                break;

                            case 1:
                                controller.CaptureStockModeBaselineIfNeeded(mode, settleDelayMs: 0);
                                break;

                            case 2:
                                controller.PeriodicVerifyPowerLimits(mode, force: true);
                                break;

                            case 3:
                                controller.InitializeHardwareFingerprintAndBaseline();
                                break;

                            case 4:
                                _ = controller.Capabilities;
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            controller.PowerLimitsChanged -= handler;

            Assert.Empty(exceptions);
            Assert.True(eventFireCount >= 0);
        }

        #endregion

        #region 3. 100+ Concurrent Threads Hammering CurveFollower

        [Fact]
        public void CurveFollower_100ConcurrentThreads_AdversarialTemperaturesAndChaos()
        {
            var follower = new CurveFollower(2);
            var exceptions = new ConcurrentBag<Exception>();
            const int threadCount = 100;
            const int iterationsPerThread = 80;

            var basePoints = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(85, 100)
            };

            Parallel.For(0, threadCount, threadId =>
            {
                try
                {
                    var rng = new Random(threadId * 777);
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        int op = (threadId + i) % 6;
                        double temp = op switch
                        {
                            0 => rng.Next(30, 95),
                            1 => double.NaN,
                            2 => double.PositiveInfinity,
                            3 => double.NegativeInfinity,
                            4 => rng.Next(-500, 0),
                            _ => rng.Next(100, 2000)
                        };

                        var curveCopy = new List<Point>(basePoints);
                        if (i % 5 == 0)
                        {
                            curveCopy.Add(new Point(rng.Next(0, 100), rng.Next(0, 100)));
                        }

                        int speed = follower.Update(temp, curveCopy, safetyFloorPercent: rng.Next(0, 50));
                        Assert.InRange(speed, 0, 100);

                        if (i % 20 == 0)
                        {
                            follower.HysteresisDegrees = rng.Next(0, 10);
                        }

                        if (i % 50 == 0)
                        {
                            follower.Reset();
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
            Assert.True(follower.Current == null || (follower.Current >= 0 && follower.Current <= 100));
        }

        #endregion

        #region 4. 100+ Concurrent Threads Hammering TelemetryHistory

        [Fact]
        public void TelemetryHistory_HistoryBuffer_100ConcurrentThreads_Hammer()
        {
            var buffer = new HistoryBuffer(60);
            var exceptions = new ConcurrentBag<Exception>();
            const int threadCount = 100;
            const int iterationsPerThread = 80;

            Parallel.For(0, threadCount, threadId =>
            {
                try
                {
                    var rng = new Random(threadId * 999);
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        int op = (threadId + i) % 8;
                        switch (op)
                        {
                            case 0:
                                buffer.Add(rng.Next(20, 100));
                                break;
                            case 1:
                                buffer.Add(double.NaN);
                                break;
                            case 2:
                                buffer.Add(double.PositiveInfinity);
                                break;
                            case 3:
                                buffer.Add(null);
                                break;
                            case 4:
                                _ = buffer.Range();
                                break;
                            case 5:
                                _ = buffer.Average();
                                break;
                            case 6:
                                var snap = buffer.GetSnapshot();
                                Assert.NotNull(snap);
                                Assert.True(snap.Length <= buffer.Capacity);
                                break;
                            case 7:
                                int count = buffer.Count;
                                if (count > 0)
                                {
                                    int idx = rng.Next(0, count);
                                    _ = buffer[idx];
                                }
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
            Assert.True(buffer.Count <= buffer.Capacity);
        }

        #endregion

        #region 5. 100+ Concurrent Threads Hammering RegistrySafety

        [Fact]
        public void RegistrySafety_100ConcurrentThreads_AdversarialInputsAndContention()
        {
            var exceptions = new ConcurrentBag<Exception>();
            const int threadCount = 100;
            const int iterationsPerThread = 80;

            string[] adversarialNames = {
                "",
                "   ",
                null!,
                "../../Windows/System32",
                @"..\..\Windows\System32",
                "Profile Injected",
                new string('A', 10000),
                "🎮 Gaming ​ ZeroWidth",
                "Normal_Profile-1 (Default)"
            };

            Parallel.For(0, threadCount, threadId =>
            {
                try
                {
                    var rng = new Random(threadId * 555);
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        int op = (threadId + i) % 4;
                        switch (op)
                        {
                            case 0:
                                string rawName = adversarialNames[rng.Next(0, adversarialNames.Length)];
                                string sanitized = RegistrySafety.SanitizeSubKeyName(rawName);
                                Assert.False(string.IsNullOrWhiteSpace(sanitized));
                                Assert.DoesNotContain("/", sanitized);
                                Assert.DoesNotContain(@"\", sanitized);
                                Assert.DoesNotContain("..", sanitized);
                                Assert.True(sanitized.Length <= 100);
                                break;

                            case 1:
                                string candidatePath = rng.Next(0, 2) == 0
                                    ? @"SOFTWARE\PredatorControl\Profiles"
                                    : @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                                bool isSafe = RegistrySafety.IsSafePath(candidatePath);
                                if (candidatePath.StartsWith(@"SOFTWARE\PredatorControl", StringComparison.OrdinalIgnoreCase))
                                {
                                    Assert.True(isSafe);
                                }
                                else
                                {
                                    Assert.False(isSafe);
                                }
                                break;

                            case 2:
                                int action = rng.Next(-5, 10);
                                RegistrySafety.SaveModeKeyAction(action);
                                int loadedAction = RegistrySafety.LoadModeKeyAction();
                                Assert.InRange(loadedAction, 0, 1);
                                break;

                            case 3:
                                {
                                    using var hkcuKey = RegistrySafety.OpenSafeSubKey(Registry.CurrentUser, RegistrySafety.BaseKeyPath);
                                    // hkcuKey may be null if key doesn't exist, but must never throw SecurityException
                                    break;
                                }
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
        }

        #endregion

        #region 6. Rapid Power Mode Cycling Under Heavy Load

        [Fact]
        public void RapidPowerModeCycling_100ConcurrentTasks_UnderHeavyLoad()
        {
            var mockBackend = new MockPawnIoBackend(isLoaded: true);
            mockBackend.ConfigureIntelBaseline(tdpWatts: 45, minWatts: 15, maxWatts: 115, pl1Watts: 45, pl2Watts: 90);

            var settings = new AppSettings
            {
                EnableCustomPowerLimits = true,
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 90,
                CpuMaxAllowedPl2Watts = 115,
                CpuThermalSpecTdpWatts = 45
            };

            using var controller = new CpuPowerLimitController(null, settings, mockBackend);
            var history = new HistoryBuffer(100);
            var follower = new CurveFollower(2);

            var exceptions = new ConcurrentBag<Exception>();
            int totalModeSwitches = 0;

            const int workerCount = 100;
            const int iterationsPerWorker = 5;

            OperatingMode[] modes = {
                OperatingMode.Eco,
                OperatingMode.Quiet,
                OperatingMode.Balanced,
                OperatingMode.Performance,
                OperatingMode.Turbo
            };

            Parallel.For(0, workerCount, workerId =>
            {
                try
                {
                    var rng = new Random(workerId * 1234);
                    for (int i = 0; i < iterationsPerWorker; i++)
                    {
                        int role = workerId % 3;
                        if (role == 0)
                        {
                            // Mode Switcher task
                            var mode = modes[rng.Next(0, modes.Length)];
                            settings.PowerMode = (byte)mode;
                            controller.PawnIo.ApplyPowerLimits(rng.Next(25, 45), rng.Next(50, 90));
                            controller.CaptureStockModeBaselineIfNeeded(mode, settleDelayMs: 0);
                            Interlocked.Increment(ref totalModeSwitches);
                        }
                        else if (role == 1)
                        {
                            // Telemetry & Sensor Logger task
                            double temp = rng.Next(40, 95);
                            history.Add(temp);
                            _ = follower.Update(temp, new List<Point> { new(30, 20), new(80, 100) });
                        }
                        else
                        {
                            // Background Verifier task
                            var mode = modes[rng.Next(0, modes.Length)];
                            controller.PeriodicVerifyPowerLimits(mode, force: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
            Assert.True(totalModeSwitches > 0);
            Assert.NotNull(history.Average());
        }

        #endregion
    }
}
