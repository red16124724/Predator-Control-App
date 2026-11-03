using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Xunit;
using PredatorControlApp;
using PredatorControlApp.Channels;

namespace PredatorControlApp.Tests
{
    public class AdversarialStressTestSuite
    {
        #region 1. High-Frequency Concurrent Power Mode, Fan Mode, & Fan Curve Mutations (1000+ Iterations)

        [Fact]
        public void HighFrequency_ConcurrentModeAndCurveMutations_32Threads_1000Iterations_NoCorruptions()
        {
            var settings = new AppSettings();
            for (int i = 0; i < 8; i++)
            {
                settings.CpuCurve.Add(new CurvePointData(30 + i * 10, 20 + i * 10));
                settings.GpuCurve.Add(new CurvePointData(30 + i * 10, 20 + i * 10));
            }

            int threadCount = 32;
            int iterationsPerThread = 1000;
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];

            using var barrier = new Barrier(threadCount);

            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    var rnd = new Random(tid * 397 + 17);

                    for (int iter = 0; iter < iterationsPerThread; iter++)
                    {
                        try
                        {
                            int action = (tid + iter) % 6;
                            switch (action)
                            {
                                case 0:
                                    settings.PowerMode = (byte)rnd.Next(0, 7);
                                    settings.PowerModeAC = (byte)rnd.Next(0, 7);
                                    settings.PowerModeBattery = (byte)rnd.Next(0, 7);
                                    settings.AutoPowerAC = rnd.Next(0, 5);
                                    break;

                                case 1:
                                    settings.FanMode = (byte)rnd.Next(1, 4);
                                    settings.FanSpeedCpu = rnd.Next(-10, 150);
                                    settings.FanSpeedGpu = rnd.Next(-10, 150);
                                    settings.FanSpeedSys = rnd.Next(-10, 150);
                                    settings.FanCurveEnabled = (iter % 2 == 0);
                                    break;

                                case 2:
                                    lock (settings.CpuCurve)
                                    {
                                        if (settings.CpuCurve.Count > 16)
                                        {
                                            settings.CpuCurve.Clear();
                                        }
                                        settings.CpuCurve.Add(new CurvePointData(rnd.Next(0, 120), rnd.Next(0, 120)));
                                    }
                                    break;

                                case 3:
                                    lock (settings.GpuCurve)
                                    {
                                        if (settings.GpuCurve.Count > 16)
                                        {
                                            settings.GpuCurve.Clear();
                                        }
                                        settings.GpuCurve.Add(new CurvePointData(rnd.Next(0, 120), rnd.Next(0, 120)));
                                    }
                                    break;

                                case 4:
                                    var snap = settings.CreateSnapshot();
                                    Assert.NotNull(snap);
                                    string json = JsonSerializer.Serialize(snap);
                                    Assert.NotEmpty(json);
                                    break;

                                case 5:
                                    settings.Sanitize();
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);

            settings.Sanitize();
            Assert.InRange(settings.PowerMode, (byte)0, (byte)6);
            Assert.InRange(settings.FanMode, (byte)1, (byte)3);
            Assert.InRange(settings.FanSpeedCpu, 0, 100);
            Assert.InRange(settings.FanSpeedGpu, 0, 100);
            Assert.InRange(settings.FanSpeedSys, 0, 100);
            Assert.True(settings.CpuCurve.Count <= 16);
            Assert.True(settings.GpuCurve.Count <= 16);
        }

        [Fact]
        public void CurveFollower_HighFrequencyDynamicMutations_1000Iterations_ThreadSafe()
        {
            var follower = new CurveFollower(hysteresisDegrees: 2);
            int threadCount = 16;
            int iterations = 1000;
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    var rnd = new Random(tid * 101);
                    var pts = new List<Point>
                    {
                        new(30, 20), new(50, 40), new(70, 60), new(90, 100)
                    };

                    for (int i = 0; i < iterations; i++)
                    {
                        try
                        {
                            double temp = rnd.NextDouble() * 120.0 - 10.0;
                            int floor = rnd.Next(0, 101);

                            if (i % 50 == 0)
                            {
                                follower.HysteresisDegrees = rnd.Next(0, 11);
                            }

                            int speed = follower.Update(temp, pts, floor);
                            Assert.InRange(speed, 0, 100);
                            Assert.True(speed >= floor, $"Speed {speed} fell below safety floor {floor} at temp {temp}");
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);
        }

        [Fact]
        public void Form1_InterpolateCurve_DegeneratePointSetsAndExtremes_NeverThrows()
        {
            // Null list
            Assert.Equal(50, Form1.InterpolateCurve(null, 50));

            // Empty list
            Assert.Equal(50, Form1.InterpolateCurve(new List<Point>(), 50));

            // Single point
            Assert.Equal(40, Form1.InterpolateCurve(new List<Point> { new(50, 40) }, 20));
            Assert.Equal(40, Form1.InterpolateCurve(new List<Point> { new(50, 40) }, 80));

            // Scrambled out of order points
            var scrambled = new List<Point> { new(90, 100), new(30, 10), new(60, 50) };
            Assert.Equal(10, Form1.InterpolateCurve(scrambled, 20));
            Assert.Equal(100, Form1.InterpolateCurve(scrambled, 100));

            // Duplicate X coordinates
            var dupes = new List<Point> { new(50, 20), new(50, 80), new(90, 100) };
            int res = Form1.InterpolateCurve(dupes, 50);
            Assert.InRange(res, 10, 100);

            // Extreme temperature values
            Assert.Equal(10, Form1.InterpolateCurve(scrambled, int.MinValue));
            Assert.Equal(100, Form1.InterpolateCurve(scrambled, int.MaxValue));
        }

        #endregion

        #region 2. AntiThrottle Safety Stress Under Erratic/Extreme Temperatures

        [Theory]
        [InlineData(-1000.0)]
        [InlineData(-50.0)]
        [InlineData(0.0)]
        [InlineData(double.NaN)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(300.0)]
        [InlineData(10000.0)]
        public void AntiThrottleSafety_ExtremeTemperatures_NeverThrows(double temp)
        {
            var limits = ThermalLimits.CpuDefault;
            var safety = new AntiThrottleSafety(limits, enabled: true);

            int boost = safety.CalculateBoost(temp);
            Assert.InRange(boost, 0, 100);

            var result = safety.Evaluate(ChipTarget.Cpu, temp);
            Assert.Equal(ChipTarget.Cpu, result.Chip);
            Assert.InRange(result.TargetFloorPercent, 0, 100);

            if (temp >= 100.0 && !double.IsNaN(temp) && !double.IsInfinity(temp))
            {
                Assert.Equal(100, result.TargetFloorPercent);
                Assert.True(result.IsActive);
                Assert.True(result.IsTripwireLocked);
            }
        }

        [Fact]
        public void AntiThrottleSafety_RapidThermalSpikesAndDrops_50000Cycles()
        {
            var service = new ThermalSafetyService(ThermalLimits.CpuDefault, ThermalLimits.GpuDefault, enabled: true);
            var follower = new CurveFollower(hysteresisDegrees: 2);
            var curve = FanCurveGraph.DefaultCpuPoints;

            double[] testTemps = { -50.0, 45.0, 85.0, 98.0, 105.0, 300.0, 20.0, -10.0, 92.0, 75.0 };

            for (int i = 0; i < 50_000; i++)
            {
                double cpuTemp = testTemps[i % testTemps.Length];
                double gpuTemp = testTemps[(i + 3) % testTemps.Length];

                var status = service.EvaluateStatus(cpuTemp, gpuTemp);
                var (safeCpu, safeGpu) = service.ApplyAntiThrottle(cpuTemp, gpuTemp, 20, 20);

                Assert.InRange(safeCpu, 20, 100);
                Assert.InRange(safeGpu, 20, 100);

                if (cpuTemp >= 100.0 && cpuTemp <= 300.0)
                {
                    Assert.Equal(100, safeCpu);
                    Assert.True(status.IsCpuCritical);
                }

                int followerSpeed = follower.Update(cpuTemp, curve, status.CpuSpeedFloor);
                Assert.InRange(followerSpeed, 0, 100);
            }
        }

        [Fact]
        public void AntiThrottleSafety_DisabledSafety_ReturnsZeroBoostAndPassThrough()
        {
            var service = new ThermalSafetyService(enabled: false);
            var (safeCpu, safeGpu) = service.ApplyAntiThrottle(105.0, 95.0, 25, 30);

            Assert.Equal(25, safeCpu);
            Assert.Equal(30, safeGpu);

            var status = service.EvaluateStatus(105.0, 95.0);
            Assert.False(status.AntiThrottleActive);
            Assert.Equal(0, status.CpuSpeedFloor);
            Assert.Equal(0, status.GpuSpeedFloor);
        }

        [Fact]
        public void CurveFollower_EmergencySafetyFloorBypassesDeadbandAndZero()
        {
            var follower = new CurveFollower(hysteresisDegrees: 5);
            var curve = new List<Point> { new(30, 20), new(100, 20) }; // Flat 20% curve

            // Normal update: speed is 20%
            int normalSpeed = follower.Update(50.0, curve, safetyFloorPercent: 0);
            Assert.Equal(20, normalSpeed);

            // Emergency safety floor demands 100%
            int emergencySpeed = follower.Update(50.0, curve, safetyFloorPercent: 100);
            Assert.Equal(100, emergencySpeed);

            // Safety floor of 75% forces speed to 75%
            int floorSpeed = follower.Update(50.0, curve, safetyFloorPercent: 75);
            Assert.Equal(75, floorSpeed);
        }

        #endregion

        #region 3. Channel Router Fallback Stress (Simulated Failures & Transitions)

        private sealed class MockOperatingChannel : IOperatingModeChannel
        {
            public ModeTransportKind Transport => ModeTransportKind.ActionFunction7;
            public Func<IReadOnlyList<OperatingMode>>? SupportedModesGetter { get; set; }

            private IReadOnlyList<OperatingMode>? _modes;
            public IReadOnlyList<OperatingMode> SupportedModes
            {
                get
                {
                    if (SupportedModesGetter != null) return SupportedModesGetter();
                    return _modes ?? new[]
                    {
                        OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo
                    };
                }
                set => _modes = value;
            }

            public bool ShouldThrow { get; set; }
            public bool ShouldFail { get; set; }
            public OperatingMode? CurrentMode { get; set; } = OperatingMode.Balanced;

            public OperatingMode? Read()
            {
                if (ShouldThrow) throw new InvalidOperationException("Simulated hardware bus error");
                if (ShouldFail) return null;
                return CurrentMode;
            }

            public bool Write(OperatingMode mode)
            {
                if (ShouldThrow) throw new IOException("Simulated write timeout");
                if (ShouldFail) return false;
                CurrentMode = mode;
                return true;
            }

            public bool TryGetOperatingMode(out OperatingMode mode)
            {
                if (ShouldThrow) throw new TimeoutException("Simulated bus timeout");
                if (ShouldFail)
                {
                    mode = OperatingMode.Balanced;
                    return false;
                }
                mode = CurrentMode ?? OperatingMode.Balanced;
                return true;
            }

            public bool TrySetOperatingMode(OperatingMode mode)
            {
                if (ShouldThrow) throw new InvalidOperationException("Simulated fault");
                if (ShouldFail) return false;
                CurrentMode = mode;
                return true;
            }
        }

        [Fact]
        public void OperatingModeChannelRouter_PrimaryFailure_FallsBackSeamlessly()
        {
            var primary = new MockOperatingChannel { ShouldThrow = true };
            var fallback = new MockOperatingChannel { CurrentMode = OperatingMode.Performance };

            var router = new OperatingModeChannelRouter(primary, fallback);

            var readMode = router.Read();
            Assert.Equal(OperatingMode.Performance, readMode);

            bool writeOk = router.Write(OperatingMode.Turbo);
            Assert.True(writeOk);
            Assert.Equal(OperatingMode.Turbo, fallback.CurrentMode);

            bool getOk = router.TryGetOperatingMode(out var outMode);
            Assert.True(getOk);
            Assert.Equal(OperatingMode.Turbo, outMode);

            bool setOk = router.TrySetOperatingMode(OperatingMode.Quiet);
            Assert.True(setOk);
            Assert.Equal(OperatingMode.Quiet, fallback.CurrentMode);
        }

        [Fact]
        public void OperatingModeChannelRouter_BothChannelsFail_ReturnsSafeDefaultsNeverThrows()
        {
            var primary = new MockOperatingChannel { ShouldThrow = true };
            var fallback = new MockOperatingChannel { ShouldThrow = true };

            var router = new OperatingModeChannelRouter(primary, fallback);

            Assert.Null(router.Read());
            Assert.False(router.Write(OperatingMode.Turbo));
            Assert.False(router.TryGetOperatingMode(out var mode));
            Assert.Equal(OperatingMode.Balanced, mode);
            Assert.False(router.TrySetOperatingMode(OperatingMode.Quiet));
        }

        [Fact]
        public void OperatingModeChannelRouter_ConcurrentTransitions_32Threads_1000Iterations()
        {
            var primary = new MockOperatingChannel();
            var fallback = new MockOperatingChannel();
            var router = new OperatingModeChannelRouter(primary, fallback);

            int threadCount = 32;
            int iterations = 1000;
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    var rnd = new Random(tid * 71);
                    for (int i = 0; i < iterations; i++)
                    {
                        try
                        {
                            if (i % 20 == 0)
                            {
                                primary.ShouldThrow = (rnd.Next(0, 2) == 1);
                                primary.ShouldFail = (rnd.Next(0, 2) == 1);
                            }

                            var targetMode = (OperatingMode)(rnd.Next(0, 4) + 1);
                            router.TrySetOperatingMode(targetMode);
                            router.TryGetOperatingMode(out _);
                            router.Read();
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);
        }

        #endregion

        #region 4. Battery Calibration State Machine Fuzzing

        [Fact]
        public void BatteryCalibrationEta_ErraticCapacityDropsAndNegativeRates_NeverThrows()
        {
            var calc = new BatteryCalibrationEtaCalculator();

            calc.UpdateCapacity(50000, DateTime.UtcNow);
            calc.UpdateCapacity(0, DateTime.UtcNow.AddSeconds(10));
            calc.UpdateCapacity(-1000, DateTime.UtcNow.AddSeconds(20));
            calc.UpdateCapacity(int.MinValue, DateTime.UtcNow.AddSeconds(30));
            calc.UpdateCapacity(int.MaxValue, DateTime.UtcNow.AddSeconds(40));

            calc.UpdateDirectRate(-500.0);
            calc.UpdateDirectRate(0.0);
            calc.UpdateDirectRate(double.NaN);
            calc.UpdateDirectRate(double.PositiveInfinity);
            calc.UpdateDirectRate(double.NegativeInfinity);
            calc.UpdateDirectRate(1e12);

            calc.UpdatePercentage(-10, DateTime.UtcNow);
            calc.UpdatePercentage(150, DateTime.UtcNow.AddSeconds(10));
            calc.UpdatePercentage(int.MinValue, DateTime.UtcNow.AddSeconds(20));
            calc.UpdatePercentage(int.MaxValue, DateTime.UtcNow.AddSeconds(30));

            var eta1 = calc.CalculateEta(phase: 1, remainingCapacity: -500, fullCapacity: 0, directRate: -100);
            Assert.Null(eta1);

            var eta2 = calc.CalculateEta(phase: 2, remainingCapacity: 0, fullCapacity: 50000, directRate: 0);
            Assert.Equal(TimeSpan.Zero, eta2);

            var eta3 = calc.CalculateEta(phase: 999, remainingCapacity: 5000, fullCapacity: 50000);
            Assert.Null(eta3);

            var fallbackEta = BatteryCalibrationEtaCalculator.GetWarmupFallbackEta(1, -50);
            Assert.NotNull(fallbackEta);
            Assert.True(fallbackEta.Value > TimeSpan.Zero);
        }

        [Fact]
        public void BatteryCalibrationEta_ConcurrentFuzzing_32Threads_1000Iterations()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            int threadCount = 32;
            int iterations = 1000;
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    var rnd = new Random(tid * 53);
                    var now = DateTime.UtcNow;

                    for (int i = 0; i < iterations; i++)
                    {
                        try
                        {
                            int op = rnd.Next(0, 5);
                            switch (op)
                            {
                                case 0:
                                    calc.UpdateDirectRate(rnd.NextDouble() * 100000.0 - 20000.0);
                                    break;
                                case 1:
                                    calc.UpdateCapacity(rnd.Next(-10000, 80000), now.AddSeconds(i * 10));
                                    break;
                                case 2:
                                    calc.UpdatePercentage(rnd.Next(-20, 120), now.AddMinutes(i));
                                    break;
                                case 3:
                                    calc.CalculateEta(
                                        phase: rnd.Next(0, 4),
                                        remainingCapacity: rnd.Next(-1000, 70000),
                                        fullCapacity: rnd.Next(-1000, 70000),
                                        directRate: rnd.NextDouble() * 50000.0 - 10000.0,
                                        allowWarmupFallback: rnd.Next(0, 2) == 1,
                                        batteryPercent: rnd.Next(-10, 110));
                                    break;
                                case 4:
                                    if (i % 200 == 0) calc.Reset();
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);
        }

        [Fact]
        public void BacklightStateManager_SuddenPlugUnplugOscillation_1000Iterations_ThreadSafe()
        {
            var mgr = new BacklightStateManager();
            int threadCount = 16;
            int iterations = 1000;
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    var rnd = new Random(tid * 23);
                    for (int i = 0; i < iterations; i++)
                    {
                        try
                        {
                            bool pluggedIn = (rnd.Next(0, 2) == 1);
                            mgr.OnPowerSourceChanged(pluggedIn, out bool turnOff, out int targetBrightness);
                            Assert.InRange(targetBrightness, 0, 100);

                            if (i % 10 == 0)
                            {
                                bool lidOpen = (rnd.Next(0, 2) == 1);
                                mgr.OnLidChanged(lidOpen, pluggedIn ? PowerLineStatus.Online : PowerLineStatus.Offline, out _, out _);
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);
        }

        #endregion

        #region 5. Settings JSON Corruptions & Registry Security Fuzzing

        [Fact]
        public void AppSettings_CorruptJsonFuzzing_GracefullyFallsBackToDefaults()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "CorruptSettings_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testFile = Path.Combine(tempDir, "settings.json");

            try
            {
                AppSettings.CustomFilePath = testFile;

                string[] corruptedPayloads =
                {
                    "",
                    "{",
                    "{\"Theme\": \"Dark\", ",
                    "{\"PowerMode\": \"INVALID_STRING\", \"FanMode\": true}",
                    "{\"CpuCurve\": [null, {}, {\"X\": -99999, \"Y\": 99999}]}",
                    "{\"CpuCurve\": \"NOT_AN_ARRAY\"}",
                    new string('A', 1024 * 1024 + 100),
                    "{\"Theme\": \"../../etc/passwd\"}",
                    "\0\0\0\0\0\0\0\0\0\0",
                    "{\"FanSpeedCpu\": 99999999999999999999999999999999999999999999999999999999999}"
                };

                foreach (var payload in corruptedPayloads)
                {
                    File.WriteAllText(testFile, payload);
                    var settings = AppSettings.Load();

                    Assert.NotNull(settings);
                    Assert.NotNull(settings.CpuCurve);
                    Assert.NotNull(settings.GpuCurve);
                    Assert.InRange(settings.PowerMode, (byte)0, (byte)6);
                    Assert.InRange(settings.FanMode, (byte)1, (byte)3);
                    Assert.InRange(settings.FanSpeedCpu, 0, 100);
                    Assert.InRange(settings.FanSpeedGpu, 0, 100);
                }
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void RegistrySafety_SanitizeSubKeyName_FuzzAdversarialInputs()
        {
            string[] adversarialNames =
            {
                "",
                "   ",
                null!,
                "../../../Windows/System32",
                @"..\..\Windows\System32",
                "....",
                ". . . .",
                "Game\0WithNull",
                new string('X', 500),
                "\U0001F3AE Apex Legends \u2122 (2024) [v1.0]",
                "COM1",
                "CON",
                "PRN",
                "AUX",
                "NUL",
                "<script>alert(1)</script>",
                "Profile/../../Overrides/AdminKey"
            };

            foreach (var input in adversarialNames)
            {
                string sanitized = RegistrySafety.SanitizeSubKeyName(input);
                Assert.NotNull(sanitized);
                Assert.NotEmpty(sanitized);
                Assert.DoesNotContain("/", sanitized);
                Assert.DoesNotContain(@"\", sanitized);
                Assert.DoesNotContain("..", sanitized);
                Assert.True(sanitized.Length <= 250);
            }
        }

        [Fact]
        public void RegistrySafety_IsSafePath_AdversarialPaths_RejectsUnsafe()
        {
            string[] unsafePaths =
            {
                "",
                "   ",
                null!,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\PredatorControl\..\Microsoft\Windows",
                @"SOFTWARE\PredatorControl/../../Windows",
                @"SOFTWARE\PredatorControlSubvert",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\PredatorControl",
                @"SOFTWARE\PredatorControl\Profiles\..\..\..",
                @"SOFTWARE\PredatorControl\Foo\Bar\..\..\..\Windows"
            };

            foreach (var path in unsafePaths)
            {
                bool isSafe = RegistrySafety.IsSafePath(path);
                Assert.False(isSafe, $"Expected path to be rejected: {path}");
            }

            string[] safePaths =
            {
                @"SOFTWARE\PredatorControl",
                @"SOFTWARE\PredatorControl\Profiles",
                @"SOFTWARE\PredatorControl\Profiles\Game1",
                @"SOFTWARE\PredatorControl\Overrides"
            };

            foreach (var path in safePaths)
            {
                bool isSafe = RegistrySafety.IsSafePath(path);
                Assert.True(isSafe, $"Expected path to be accepted: {path}");
            }
        }

        [Fact]
        public void RegistrySafety_OpenSafeSubKey_UnauthorizedPath_ThrowsSecurityException()
        {
            Assert.Throws<SecurityException>(() =>
            {
                RegistrySafety.OpenSafeSubKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            });

            Assert.Throws<SecurityException>(() =>
            {
                RegistrySafety.CreateSafeSubKey(Registry.CurrentUser, @"SOFTWARE\PredatorControl\..\Windows");
            });
        }

        [Fact]
        public void GameProfile_ValidateAndSanitize_AdversarialExecutables_Blocked()
        {
            var blockedNames = new[]
            {
                "svchost.exe", "cmd.exe", "powershell.exe", "explorer.exe", "taskmgr.exe",
                "rundll32.exe", "regedit.exe", "PredatorControlApp.exe"
            };

            foreach (var exe in blockedNames)
            {
                var profile = new GameProfile { ExecutableName = exe };
                bool valid = profile.Validate(out string error);
                Assert.False(valid, $"Expected blocked executable '{exe}' to fail validation.");
                Assert.NotEmpty(error);
            }

            var traversalProfile = new GameProfile
            {
                ExecutableName = "game.exe",
                ExecutablePath = @"C:\Windows\System32\cmd.exe"
            };
            Assert.False(traversalProfile.Validate(out _));

            var relativePathProfile = new GameProfile
            {
                ExecutableName = "game.exe",
                ExecutablePath = @"..\..\games\game.exe"
            };
            Assert.False(relativePathProfile.Validate(out _));
        }

        #endregion

        #region 6. Vulnerability & Edge Case Bug Detection Probes

        [Fact]
        public void OperatingModeChannelRouter_SupportedModes_PrimaryThrows_GracefulFallback()
        {
            var primary = new MockOperatingChannel
            {
                SupportedModesGetter = () => throw new InvalidOperationException("EC-HID communication error")
            };
            var fallback = new MockOperatingChannel
            {
                SupportedModes = new[] { OperatingMode.Quiet, OperatingMode.Balanced }
            };
            var router = new OperatingModeChannelRouter(primary, fallback);

            // Verified: When primary throws an exception, router gracefully catches it and returns fallback modes
            var modes = router.SupportedModes;
            Assert.NotNull(modes);
            Assert.Equal(2, modes.Count);
            Assert.Contains(OperatingMode.Quiet, modes);
            Assert.Contains(OperatingMode.Balanced, modes);
        }

        [Fact]
        public void CurveFollower_NaN_SafelyEnforcesSafetyFloor()
        {
            var follower = new CurveFollower();
            var curve = new List<Point> { new(30, 20), new(100, 100) };

            // Verified: When temperature is NaN, CurveFollower safely honors safety floor
            int speed = follower.Update(double.NaN, curve, safetyFloorPercent: 100);
            Assert.Equal(100, speed);

            int speed75 = follower.Update(double.NaN, curve, safetyFloorPercent: 75);
            Assert.Equal(100, speed75); // Retains peak 100

            follower.Reset();
            int resetSpeed50 = follower.Update(double.NaN, curve, safetyFloorPercent: 50);
            Assert.Equal(50, resetSpeed50);
        }

        [Fact]
        public void AntiThrottleSafety_PositiveInfinity_SafelyRejectsCorruptedReading()
        {
            var safety = new AntiThrottleSafety(ThermalLimits.CpuDefault, true);
            var result = safety.Evaluate(ChipTarget.Cpu, double.PositiveInfinity);

            // Verified: PositiveInfinity is safely rejected as corrupt sensor reading with 0 floor
            Assert.Equal(0, result.TargetFloorPercent);
            Assert.False(result.IsActive);
            Assert.False(result.IsTripwireLocked);
        }

        [Fact]
        public void RegistrySafety_OpenSafeSubKey_OverlongSubKey_SafelyRejected()
        {
            // Subkey name > 255 characters
            string overlongPath = @"SOFTWARE\PredatorControl\" + new string('A', 300);

            // Correctly rejected by IsSafePath because subkey length exceeds Win32 limit (255)
            bool isSafe = RegistrySafety.IsSafePath(overlongPath);
            Assert.False(isSafe);

            // Verified: OpenSafeSubKey throws SecurityException instead of crashing with ArgumentException
            Assert.Throws<SecurityException>(() =>
            {
                RegistrySafety.OpenSafeSubKey(Registry.CurrentUser, overlongPath);
            });
        }

        [Fact]
        public void GameSyncController_ImportProfiles_CorruptJson_ThrowsInvalidDataException()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "corrupt_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(tempFile, "{ \"Enabled\": true, \"Profiles\": [ invalid json payload");

            try
            {
                using var ctrl = new GameSyncController();

                // Verified: Corrupted JSON is safely wrapped and thrown as InvalidDataException with inner JsonException
                var ex = Assert.Throws<InvalidDataException>(() =>
                {
                    ctrl.ImportProfiles(tempFile);
                });
                Assert.IsAssignableFrom<JsonException>(ex.InnerException);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void BatteryCalibrationEta_Alpha_NaN_FallsBackToDefaultAlpha()
        {
            var calc = new BatteryCalibrationEtaCalculator();

            // Verified: Setting Alpha to NaN safely resets to DefaultAlpha (0.2)
            calc.Alpha = double.NaN;
            Assert.Equal(BatteryCalibrationEtaCalculator.DefaultAlpha, calc.Alpha);

            calc.Alpha = double.PositiveInfinity;
            Assert.Equal(BatteryCalibrationEtaCalculator.DefaultAlpha, calc.Alpha);

            calc.Alpha = -5.0;
            Assert.Equal(0.01, calc.Alpha);

            calc.Alpha = 15.0;
            Assert.Equal(1.0, calc.Alpha);
        }

        [Fact]
        public void BatteryCalibrationEta_ExtremePercent_HandledGracefullyWithoutCrashing()
        {
            var calc = new BatteryCalibrationEtaCalculator();

            // When batteryPercent is fuzzed (e.g. int.MaxValue), double-to-int conversion saturates to int.MaxValue
            int fullCap = 50000;
            int fuzzedPercent = int.MaxValue;
            double rawCalc = fullCap * (fuzzedPercent / 100.0);
            int saturatedRemaining = (int)Math.Round(rawCalc);

            Assert.Equal(int.MaxValue, saturatedRemaining);

            // CalculateEta with this extreme percent returns TimeSpan.Zero because remaining >= fullCapacity
            var eta = calc.CalculateEta(phase: 1, remainingCapacity: 0, fullCapacity: fullCap, directRate: 15000.0, batteryPercent: fuzzedPercent);
            Assert.Equal(TimeSpan.Zero, eta);
        }

        #endregion
    }
}
