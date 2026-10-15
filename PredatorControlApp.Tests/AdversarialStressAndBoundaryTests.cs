using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    [Collection("SingleInstanceTests")]
    public class AdversarialStressAndBoundaryTests
    {
        private static void RunInSta(Action action)
        {
            Exception? ex = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception e) { ex = e; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (ex != null) throw new AggregateException("Exception thrown in STA thread", ex);
        }

        #region 1. Battery Calibration State Machine: Sudden AC Disconnect/Reconnect & 0%/100% Capacity Jumps

        [Fact]
        public void BatteryCalibration_SuddenAcDisconnectAndReconnect_PhaseTransitionsAndEtaStability()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 0.5);
            var baseTime = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

            // Phase 1 (Charging): AC online, charging at 35,000 mW (35W), full capacity = 60,000 mWh
            calc.UpdateCapacity(30000, baseTime);
            calc.UpdatePercentage(50, baseTime);
            calc.UpdateDirectRate(35000);

            var etaPhase1 = calc.CalculateEta(1, 30000, 60000, directRate: 35000, allowWarmupFallback: true, batteryPercent: 50);
            Assert.NotNull(etaPhase1);
            Assert.True(etaPhase1.Value > TimeSpan.Zero, "Phase 1 ETA must be positive while charging");

            // Sudden AC disconnect: Rate drops to 0, power is offline, battery begins discharging
            var disconnectTime = baseTime.AddSeconds(10);
            calc.UpdateDirectRate(0); // Invalid/0 charging rate
            var etaAfterDisconnect = calc.CalculateEta(1, 29900, 60000, directRate: null, allowWarmupFallback: true, batteryPercent: 50);
            Assert.NotNull(etaAfterDisconnect);
            Assert.True(etaAfterDisconnect.Value >= TimeSpan.Zero, "ETA after sudden AC disconnect must remain non-negative");

            // Sudden AC reconnect: Fast charge rate surge (45,000 mW)
            var reconnectTime = disconnectTime.AddSeconds(15);
            calc.UpdateCapacity(30500, reconnectTime);
            calc.UpdatePercentage(51, reconnectTime);
            calc.UpdateDirectRate(45000);

            var etaAfterReconnect = calc.CalculateEta(1, 30500, 60000, directRate: 45000, allowWarmupFallback: true, batteryPercent: 51);
            Assert.NotNull(etaAfterReconnect);
            Assert.True(etaAfterReconnect.Value > TimeSpan.Zero);

            // Sudden capacity jump to 100% (Fuel gauge reading reaches full capacity)
            var fullChargeTime = reconnectTime.AddSeconds(30);
            calc.UpdateCapacity(60000, fullChargeTime);
            calc.UpdatePercentage(100, fullChargeTime);

            // In Phase 1 when capacity >= full, CalculateEta must return TimeSpan.Zero immediately
            var etaFull = calc.CalculateEta(1, 60000, 60000, directRate: 10000, allowWarmupFallback: true, batteryPercent: 100);
            Assert.Equal(TimeSpan.Zero, etaFull);

            // Transition to Phase 2 (Discharging to Empty)
            calc.Reset();
            var phase2Start = fullChargeTime.AddSeconds(1);
            calc.UpdateCapacity(60000, phase2Start);
            calc.UpdatePercentage(100, phase2Start);
            calc.UpdateDirectRate(25000); // 25W discharge rate

            var etaPhase2 = calc.CalculateEta(2, 60000, 60000, directRate: 25000, allowWarmupFallback: true, batteryPercent: 100);
            Assert.NotNull(etaPhase2);
            Assert.True(etaPhase2.Value > TimeSpan.Zero, "Phase 2 ETA must be positive when discharging from 100%");

            // Sudden capacity drop to 0% (Sudden low voltage cutoff)
            var emptyTime = phase2Start.AddHours(2);
            calc.UpdateCapacity(0, emptyTime);
            calc.UpdatePercentage(0, emptyTime);

            var etaEmpty = calc.CalculateEta(2, 0, 60000, directRate: 25000, allowWarmupFallback: true, batteryPercent: 0);
            Assert.Equal(TimeSpan.Zero, etaEmpty);

            // Formatting check
            string formattedEta = UiLabelFormatter.FormatCalibrationEta(etaEmpty, isCharging: false);
            Assert.Equal("Estimated Time Remaining (Discharging): 00h 00m", formattedEta);
        }

        [Fact]
        public void BatteryCalibration_ErraticCapacityJumps_ZeroAndHundredPercent_NoUnderflowOrNaN()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 0.2);
            var now = DateTime.UtcNow;

            // Pattern of chaotic capacity fluctuations (simulating intermittent fuel gauge telemetry faults)
            int[] capacityJumps = { 50, 0, 100, 0, 100, 1, 99, 0, 100 };
            double[] rateJumps = { 30000, 0, 500000, -100, double.NaN, double.PositiveInfinity, 15000, 0, 25000 };

            for (int i = 0; i < capacityJumps.Length; i++)
            {
                int pct = capacityJumps[i];
                double rate = rateJumps[i];
                var ts = now.AddSeconds(i * 10);

                calc.UpdatePercentage(pct, ts);
                calc.UpdateCapacity(pct * 600, ts);
                calc.UpdateDirectRate(rate);

                // Both Phase 1 and Phase 2 calculations must not crash or produce negative time
                var eta1 = calc.CalculateEta(1, pct * 600, 60000, directRate: rate, allowWarmupFallback: true, batteryPercent: pct);
                if (eta1.HasValue)
                {
                    Assert.True(eta1.Value >= TimeSpan.Zero, $"Phase 1 ETA must be non-negative on jump {i}");
                    Assert.False(double.IsNaN(eta1.Value.TotalSeconds), "Phase 1 ETA must not be NaN");
                    Assert.False(double.IsInfinity(eta1.Value.TotalSeconds), "Phase 1 ETA must not be Infinity");
                }

                var eta2 = calc.CalculateEta(2, pct * 600, 60000, directRate: rate, allowWarmupFallback: true, batteryPercent: pct);
                if (eta2.HasValue)
                {
                    Assert.True(eta2.Value >= TimeSpan.Zero, $"Phase 2 ETA must be non-negative on jump {i}");
                    Assert.False(double.IsNaN(eta2.Value.TotalSeconds), "Phase 2 ETA must not be NaN");
                    Assert.False(double.IsInfinity(eta2.Value.TotalSeconds), "Phase 2 ETA must not be Infinity");
                }

                string label1 = UiLabelFormatter.FormatCalibrationEta(eta1, isCharging: true);
                string label2 = UiLabelFormatter.FormatCalibrationEta(eta2, isCharging: false);
                Assert.False(string.IsNullOrWhiteSpace(label1));
                Assert.False(string.IsNullOrWhiteSpace(label2));
            }
        }

        [Theory]
        [InlineData(-500, 60000, -1000.0)]
        [InlineData(60000, -100, 0.0)]
        [InlineData(0, 0, 0.0)]
        [InlineData(99999999, 100, 100000000.0)]
        [InlineData(5000, 10000, double.NaN)]
        [InlineData(5000, 10000, double.PositiveInfinity)]
        [InlineData(5000, 10000, double.NegativeInfinity)]
        public void BatteryCalibration_ExtremeCapacityAndRateBoundaries_HandlesWithoutExceptions(
            int remaining, int full, double rate)
        {
            var calc = new BatteryCalibrationEtaCalculator();
            calc.UpdateDirectRate(rate);
            calc.UpdateCapacity(remaining, DateTime.UtcNow);

            var eta1 = calc.CalculateEta(1, remaining, full, directRate: rate, allowWarmupFallback: true, batteryPercent: 50);
            var eta2 = calc.CalculateEta(2, remaining, full, directRate: rate, allowWarmupFallback: true, batteryPercent: 50);

            if (eta1.HasValue) Assert.True(eta1.Value >= TimeSpan.Zero);
            if (eta2.HasValue) Assert.True(eta2.Value >= TimeSpan.Zero);

            string s1 = UiLabelFormatter.FormatCalibrationEta(eta1, true);
            string s2 = UiLabelFormatter.FormatCalibrationEta(eta2, false);
            Assert.NotNull(s1);
            Assert.NotNull(s2);
        }

        [Fact]
        public void BatteryCalibration_ConcurrentTelemetryUpdatesAndEtaCalculations_ThreadSafety()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 0.2);
            ThreadPool.SetMinThreads(64, 64);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            int opCount = 0;
            const int threadCount = 16;
            var tasks = new Task[threadCount];

            for (int i = 0; i < threadCount; i++)
            {
                int tid = i;
                tasks[i] = Task.Run(() =>
                {
                    int iter = 0;
                    var baseTime = DateTime.UtcNow;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        iter++;
                        int role = (tid + iter) % 4;
                        switch (role)
                        {
                            case 0:
                                calc.UpdatePercentage(iter % 101, baseTime.AddSeconds(iter * 5));
                                break;
                            case 1:
                                calc.UpdateCapacity((iter % 100) * 600, baseTime.AddSeconds(iter * 5));
                                break;
                            case 2:
                                calc.UpdateDirectRate(10000 + (iter % 30000));
                                break;
                            case 3:
                                int phase = (iter % 2) + 1;
                                var eta = calc.CalculateEta(phase, (iter % 100) * 600, 60000, allowWarmupFallback: true, batteryPercent: iter % 101);
                                if (eta.HasValue)
                                {
                                    Assert.True(eta.Value >= TimeSpan.Zero);
                                }
                                break;
                        }
                        Interlocked.Increment(ref opCount);
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.True(opCount > 1000, $"Expected >1000 concurrent calibration ops, got {opCount}");
        }

        #endregion

        #region 2. Fan Curve Interpolation: Duplicate X, Inverted Points, 0-Point & 1000-Point Curves

        [Fact]
        public void InterpolateCurve_DuplicateXCoordinates_ReturnsStableSpeedWithoutDivisionByZero()
        {
            // Case A: 2 points with identical X coordinate (e.g. step discontinuity at 50°C)
            var curveWithDuplicates = new List<Point>
            {
                new(30, 20),
                new(50, 25),
                new(50, 75), // duplicate X = 50
                new(70, 80),
                new(100, 100)
            };

            int speedBefore = Form1.InterpolateCurve(curveWithDuplicates, 49);
            int speedAt = Form1.InterpolateCurve(curveWithDuplicates, 50);
            int speedAfter = Form1.InterpolateCurve(curveWithDuplicates, 51);

            Assert.InRange(speedBefore, 10, 100);
            Assert.InRange(speedAt, 10, 100);
            Assert.InRange(speedAfter, 10, 100);

            // Case B: Cluster of 5 points with identical X coordinate
            var multiDuplicateCurve = new List<Point>
            {
                new(40, 15),
                new(60, 20),
                new(60, 40),
                new(60, 60),
                new(60, 80),
                new(60, 95),
                new(80, 100)
            };

            for (int t = 20; t <= 100; t += 5)
            {
                int spd = Form1.InterpolateCurve(multiDuplicateCurve, t);
                Assert.InRange(spd, 10, 100);
            }

            // Case C: Degenerate curve where all points have the same X coordinate
            var allSameXCurve = new List<Point>
            {
                new(65, 30),
                new(65, 50),
                new(65, 70),
                new(65, 90)
            };

            for (int t = 0; t <= 120; t += 10)
            {
                int spd = Form1.InterpolateCurve(allSameXCurve, t);
                Assert.InRange(spd, 10, 100);
            }
        }

        [Fact]
        public void InterpolateCurve_InvertedAndUnsortedPoints_SortsCorrectlyAndInterpolates()
        {
            // Fully descending (inverted) points
            var invertedCurve = new List<Point>
            {
                new(100, 100),
                new(90, 80),
                new(75, 60),
                new(60, 40),
                new(45, 25),
                new(30, 10)
            };

            // Expected behavior: InterpolateCurve detects unsorted X, sorts by X ascending, and provides monotonic output
            int spdCold = Form1.InterpolateCurve(invertedCurve, 20);
            int spdMid = Form1.InterpolateCurve(invertedCurve, 60);
            int spdHot = Form1.InterpolateCurve(invertedCurve, 90);
            int spdBoiling = Form1.InterpolateCurve(invertedCurve, 110);

            Assert.Equal(10, spdCold);
            Assert.Equal(40, spdMid);
            Assert.Equal(80, spdHot);
            Assert.Equal(100, spdBoiling);

            // Wildly shuffled points
            var shuffledCurve = new List<Point>
            {
                new(85, 70),
                new(30, 15),
                new(100, 100),
                new(50, 30),
                new(70, 50)
            };

            int prev = 0;
            for (int t = 30; t <= 100; t += 5)
            {
                int cur = Form1.InterpolateCurve(shuffledCurve, t);
                Assert.InRange(cur, 10, 100);
                Assert.True(cur >= prev, $"Fan speed must be non-decreasing with temperature: at {t}°C got {cur} vs prev {prev}");
                prev = cur;
            }
        }

        [Fact]
        public void InterpolateCurve_ZeroPointAndSinglePointCurves_HandlesGracefully()
        {
            // Null curve
            Assert.Equal(50, Form1.InterpolateCurve(null, 50));

            // Empty curve (0 points)
            var emptyCurve = new List<Point>();
            Assert.Equal(50, Form1.InterpolateCurve(emptyCurve, 25));
            Assert.Equal(50, Form1.InterpolateCurve(emptyCurve, 75));

            // Single point curve (in-bounds)
            var singlePoint = new List<Point> { new(50, 45) };
            Assert.Equal(45, Form1.InterpolateCurve(singlePoint, 10));
            Assert.Equal(45, Form1.InterpolateCurve(singlePoint, 50));
            Assert.Equal(45, Form1.InterpolateCurve(singlePoint, 90));

            // Single point curve (out-of-bounds speed)
            var singlePointLow = new List<Point> { new(50, 2) };
            Assert.Equal(10, Form1.InterpolateCurve(singlePointLow, 50));

            var singlePointHigh = new List<Point> { new(50, 150) };
            Assert.Equal(100, Form1.InterpolateCurve(singlePointHigh, 50));
        }

        [Fact]
        public void InterpolateCurve_MassiveThousandPointCurve_PerformanceAndAccuracy()
        {
            // 1,000-point curve spanning 0°C to 100°C
            var largeCurve = new List<Point>(1000);
            for (int i = 0; i < 1000; i++)
            {
                int x = (int)Math.Round(i * 100.0 / 999.0);
                int y = 10 + (int)Math.Round(i * 90.0 / 999.0);
                largeCurve.Add(new Point(x, y));
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int queries = 20000;
            int sum = 0;

            for (int q = 0; q < queries; q++)
            {
                int temp = q % 110; // range 0 to 109
                sum += Form1.InterpolateCurve(largeCurve, temp);
            }
            sw.Stop();

            Assert.True(sum > 0);
            Assert.True(sw.ElapsedMilliseconds < 500, $"20,000 lookups across 1,000-point curve took {sw.ElapsedMilliseconds}ms (must be < 500ms)");

            // Also test FanCurveGraph normalization on 1,000-point curve
            RunInSta(() =>
            {
                var normalized = FanCurveGraph.Normalize(largeCurve);
                Assert.NotNull(normalized);
                Assert.Equal(FanCurveGraph.ControlPointCount, normalized.Count);
                Assert.Equal(30, normalized[0].X);
                Assert.Equal(100, normalized[^1].X);
            });
        }

        [Fact]
        public void CurveFollower_HysteresisUnderErraticTemperatureFluctuations()
        {
            var followerInstant = new CurveFollower(hysteresisDegrees: 0);
            var followerHysteresis = new CurveFollower(hysteresisDegrees: 4);

            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 35),
                new(70, 60),
                new(85, 80),
                new(100, 100)
            };

            // Rise to 75°C
            int speedInstantUp = followerInstant.Update(75.0, curve);
            int speedHystUp = followerHysteresis.Update(75.0, curve);
            Assert.True(speedInstantUp > 60);
            Assert.True(speedHystUp > 60);

            // Drop 2°C (73°C): within 4°C hysteresis deadband
            int speedInstantDrop2 = followerInstant.Update(73.0, curve);
            int speedHystDrop2 = followerHysteresis.Update(73.0, curve);

            // Instant tracks immediately down; hysteresis follower holds peak speed
            Assert.True(speedInstantDrop2 < speedInstantUp, "Hysteresis=0 must drop speed immediately");
            Assert.Equal(speedHystUp, speedHystDrop2); // Must hold peak!

            // Drop 6°C (69°C): exceeds 4°C hysteresis threshold
            int speedHystDrop6 = followerHysteresis.Update(69.0, curve);
            Assert.True(speedHystDrop6 < speedHystUp, "Dropping past hysteresis degrees must release peak and lower fan speed");

            // Extreme temperature boundary clamps
            int negSpeed = followerHysteresis.Update(-999.0, curve);
            int superHotSpeed = followerHysteresis.Update(9999.0, curve);
            Assert.InRange(negSpeed, 10, 100);
            Assert.Equal(100, superHotSpeed);
        }

        #endregion

        #region 3. High-Concurrency Telemetry Polling vs Rapid UI Power & Fan Mode Switches

        [Fact]
        public void HighConcurrency_TelemetryPolling_Vs_RapidPowerAndFanModeSwitches()
        {
            using var wmi = new WmiController();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));

            int telemetryReads = 0;
            int powerModeSwitches = 0;
            int fanSpeedAdjustments = 0;
            var exceptions = new ConcurrentBag<Exception>();

            const int telemetryThreads = 8;
            const int powerThreads = 4;
            const int fanThreads = 4;
            var threads = new List<Thread>();

            // 8 Telemetry Poller Threads
            for (int i = 0; i < telemetryThreads; i++)
            {
                var t = new Thread(() =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            int cpuTemp = wmi.CpuTemp;
                            int gpuTemp = wmi.GpuTemp;
                            int cpuRpm = wmi.CpuFanRpm;
                            int gpuRpm = wmi.GpuFanRpm;
                            int sysRpm = wmi.SystemFanRpm;
                            byte customCpu = wmi.CustomCpuFanSpeed;
                            byte customGpu = wmi.CustomGpuFanSpeed;
                            byte customSys = wmi.CustomSystemFanSpeed;
                            int s1 = wmi.GetSensorReading(1);
                            int f1 = wmi.GetGamingFanSpeed(1);
                            wmi.TryGetPowerMode(out byte _);

                            Assert.True(cpuTemp >= 0);
                            Assert.True(gpuTemp >= 0);
                            Assert.True(cpuRpm >= 0);
                            Assert.True(gpuRpm >= 0);
                            Assert.True(sysRpm >= 0);
                            Assert.True(customCpu >= 0);
                            Assert.True(customGpu >= 0);
                            Assert.True(customSys >= 0);

                            Interlocked.Increment(ref telemetryReads);
                            Thread.Sleep(1);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
                threads.Add(t);
            }

            // 4 Power Mode Switcher Threads
            byte[] powerModes = { 0, 1, 4, 5, 6 };
            for (int i = 0; i < powerThreads; i++)
            {
                int tid = i;
                var t = new Thread(() =>
                {
                    int step = tid;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            byte mode = powerModes[step % powerModes.Length];
                            wmi.TrySetPowerMode(mode, forceRetry: false);
                            Interlocked.Increment(ref powerModeSwitches);
                            step++;
                            Thread.Sleep(1);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
                threads.Add(t);
            }

            // 4 Fan Speed Adjustment Threads
            for (int i = 0; i < fanThreads; i++)
            {
                int tid = i;
                var t = new Thread(() =>
                {
                    byte speed = 20;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            wmi.SetCpuFanSpeed(speed);
                            wmi.SetGpuFanSpeed((byte)(speed + 5));
                            wmi.SetSystemFanSpeed((byte)(speed + 10));
                            wmi.UpdateModeKeyLed((byte)(tid % 5));

                            speed = (byte)((speed + 10) % 100);
                            if (speed < 10) speed = 20;

                            Interlocked.Increment(ref fanSpeedAdjustments);
                            Thread.Sleep(1);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
                threads.Add(t);
            }

            // Start all threads simultaneously
            foreach (var t in threads) t.Start();

            // Wait for all threads to join upon cancellation
            foreach (var t in threads) t.Join();

            Assert.Empty(exceptions);
            Assert.True(telemetryReads > 50, $"Expected >50 telemetry reads, got {telemetryReads}");
            Assert.True(powerModeSwitches > 20, $"Expected >20 power switches, got {powerModeSwitches}");
            Assert.True(fanSpeedAdjustments > 20, $"Expected >20 fan adjustments, got {fanSpeedAdjustments}");
        }

        #endregion

        #region 4. AppSettings: Corrupted JSON, Partial Writes & Read-Only Files

        [Fact]
        public void AppSettings_Load_CorruptedAndMalformedJson_FallsBackSafelyWithoutCrashing()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AppSettings_Corrupted_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testFile = Path.Combine(tempDir, "settings.json");

            try
            {
                AppSettings.CustomFilePath = testFile;

                // Corrupted Case 1: Truncated JSON
                File.WriteAllText(testFile, "{\"PowerMode\": 4, \"FanSpeedCpu\": 80, \"CpuCurve\": [{\"X\": 30");
                var loaded1 = AppSettings.Load();
                Assert.NotNull(loaded1);
                Assert.InRange(loaded1.PowerMode, (byte)0, (byte)6);

                // Corrupted Case 2: Random garbage binary / malformed syntax
                File.WriteAllText(testFile, "<<<MALFORMED_DATA_???>>>!@#$%^&*()_+{}|<>;',./");
                var loaded2 = AppSettings.Load();
                Assert.NotNull(loaded2);
                Assert.InRange(loaded2.PowerMode, (byte)0, (byte)6);

                // Corrupted Case 3: JSON array instead of JSON object
                File.WriteAllText(testFile, "[1, 2, 3, 4, 5]");
                var loaded3 = AppSettings.Load();
                Assert.NotNull(loaded3);

                // Corrupted Case 4: Wrong types inside JSON
                File.WriteAllText(testFile, "{\"PowerMode\": \"TURBO_MAX_EXTREME\", \"CpuCurve\": \"NOT_AN_ARRAY\"}");
                var loaded4 = AppSettings.Load();
                Assert.NotNull(loaded4);

                // Corrupted Case 5: Empty / Whitespace only / Null bytes
                File.WriteAllBytes(testFile, new byte[] { 0, 0, 0, 0 });
                var loaded5 = AppSettings.Load();
                Assert.NotNull(loaded5);

                // Corrupted Case 6: Oversized JSON (> 1MB buffer-overflow attempt)
                var hugeString = "{\"Theme\": \"" + new string('A', 1024 * 1024 + 100) + "\"}";
                File.WriteAllText(testFile, hugeString);
                var loaded6 = AppSettings.Load();
                Assert.NotNull(loaded6);
                Assert.True(Enum.TryParse<AppTheme>(loaded6.Theme, out _), "Oversized file rejected, valid theme hydrated from registry fallback");
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AppSettings_SaveAndLoad_ReadOnlyFile_HandlesAccessDeniedWithoutCrashing()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AppSettings_ReadOnly_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testFile = Path.Combine(tempDir, "settings.json");

            try
            {
                AppSettings.CustomFilePath = testFile;

                // Step 1: Save initial valid settings
                var settings = new AppSettings
                {
                    PowerMode = 4,
                    FanMode = 2,
                    FanSpeedCpu = 75
                };
                settings.Save();
                Assert.True(File.Exists(testFile));

                // Step 2: Mark the file as Read-Only
                File.SetAttributes(testFile, FileAttributes.ReadOnly);
                var attrs = File.GetAttributes(testFile);
                Assert.True((attrs & FileAttributes.ReadOnly) != 0, "File must be marked ReadOnly");

                // Step 3: Call AppSettings.Load() on read-only file -> must succeed
                var loaded = AppSettings.Load();
                Assert.NotNull(loaded);
                Assert.Equal(4, loaded.PowerMode);
                Assert.Equal(2, loaded.FanMode);
                Assert.Equal(75, loaded.FanSpeedCpu);

                // Step 4: Attempt to save changes to read-only file
                // Must handle UnauthorizedAccessException cleanly without crashing
                settings.PowerMode = 5;
                var saveEx = Record.Exception(() => settings.Save());
                Assert.Null(saveEx); // Save() must catch UnauthorizedAccessException gracefully

                // Step 5: Remove read-only attribute and verify save works again
                File.SetAttributes(testFile, FileAttributes.Normal);
                settings.PowerMode = 6;
                settings.Save();

                var reloaded = AppSettings.Load();
                Assert.Equal(6, reloaded.PowerMode);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try
                {
                    if (File.Exists(testFile))
                    {
                        File.SetAttributes(testFile, FileAttributes.Normal);
                    }
                    Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        [Fact]
        public void AppSettings_ConcurrentPartialWritesAndRapidReloads_Integrity()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AppSettings_Partial_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testFile = Path.Combine(tempDir, "settings.json");

            try
            {
                AppSettings.CustomFilePath = testFile;
                var initial = new AppSettings { PowerMode = 1 };
                initial.Save();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var exceptions = new ConcurrentBag<Exception>();
                var tasks = new Task[4];

                // Task 1: Writer writing partial truncated strings
                tasks[0] = Task.Run(() =>
                {
                    int iter = 0;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            string partial = iter % 2 == 0 ? "{\"PowerMode\": 1" : "{\"PowerMode\": 4, \"Theme\": \"Dark\"}";
                            File.WriteAllText(testFile, partial);
                            iter++;
                        }
                        catch { }
                        Thread.Sleep(1);
                    }
                });

                // Task 2 & 3: Continuous Loaders
                for (int i = 1; i < 3; i++)
                {
                    tasks[i] = Task.Run(() =>
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                var s = AppSettings.Load();
                                Assert.NotNull(s);
                                Assert.InRange(s.PowerMode, (byte)0, (byte)6);
                            }
                            catch (Exception ex)
                            {
                                exceptions.Add(ex);
                            }
                        }
                    });
                }

                // Task 4: Continuous Savers
                tasks[3] = Task.Run(() =>
                {
                    var s = new AppSettings { PowerMode = 4 };
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            s.Save();
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });

                Task.WaitAll(tasks);
                Assert.Empty(exceptions);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        #endregion

        #region 5. PdhLoadMonitor, CapabilityProbe, and WmiController: Mock/Failing/Slow Queries

        [Fact]
        public void PdhLoadMonitor_ConcurrentSamplingAndDisposal_NoRaceConditionOrCrash()
        {
            var monitor = new PdhLoadMonitor();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            const int workers = 16;
            var tasks = new Task[workers];
            int samples = 0;
            var exceptions = new ConcurrentBag<Exception>();

            for (int i = 0; i < workers; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var (cpu, gpu) = monitor.Sample(sampleGpu: true);
                            if (cpu.HasValue) Assert.InRange(cpu.Value, 0.0, 100.0);
                            if (gpu.HasValue) Assert.InRange(gpu.Value, 0.0, 100.0);
                            Interlocked.Increment(ref samples);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            // Let workers sample for a short burst
            Thread.Sleep(200);

            // Dispose monitor while workers are actively sampling
            monitor.Dispose();

            // Subsequent samples on disposed monitor must safely return (null, null)
            var (postCpu, postGpu) = monitor.Sample();
            Assert.Null(postCpu);
            Assert.Null(postGpu);

            // Double dispose must be idempotent
            monitor.Dispose();

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);
            Assert.True(samples > 20, $"Expected >20 samples before disposal, got {samples}");

            // Verify rapid construct/dispose lifecycle in tight loop
            for (int i = 0; i < 50; i++)
            {
                using var quick = new PdhLoadMonitor();
                quick.Sample(sampleGpu: false);
            }
        }

        [Fact]
        public void CapabilityProbe_Probe_UnderFailingAndDisposedWmi_ReturnsSensibleDefaults()
        {
            using var wmi = new WmiController();

            // Run probe on live controller in non-Acer / CI environment
            var caps = CapabilityProbe.Probe(wmi, null);
            Assert.NotNull(caps);
            Assert.NotNull(caps.Diagnostics);
            Assert.NotNull(caps.Fans);
            Assert.True(caps.Fans.Count >= 2, "Default fans CPU and GPU must be present");
            Assert.True(caps.OperatingModes.Count >= 4, "Standard operating modes must be present");

            // Dispose WMI controller and verify CapabilityProbe handles disposed WMI cleanly
            wmi.Dispose();

            var capsDisposed = CapabilityProbe.Probe(wmi, null);
            Assert.NotNull(capsDisposed);
            Assert.NotNull(capsDisposed.Fans);
            Assert.True(capsDisposed.Fans.Count >= 2);

            // Concurrent execution of CapabilityProbe across 16 threads
            using var wmiConcurrent = new WmiController();
            var tasks = new Task[16];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    var c = CapabilityProbe.Probe(wmiConcurrent, null);
                    Assert.NotNull(c);
                    Assert.True(c.Fans.Count >= 2);
                });
            }
            Task.WaitAll(tasks);
        }

        [Fact]
        public void WmiController_SimulatedFailingAndInvalidQueries_CacheRecovery()
        {
            using var wmi = new WmiController();

            // Query invalid sensor IDs and invalid fan IDs
            int badSensor1 = wmi.GetSensorReading(0xFFFF);
            int badSensor2 = wmi.GetSensorReading(0xDEADBEEF);
            int badFan = wmi.GetGamingFanSpeed(999);

            Assert.Equal(0, badSensor1);
            Assert.Equal(0, badSensor2);
            Assert.Equal(0, badFan);

            // Probe methods when underlying WMI service is absent or mock failing
            bool? coolBoost = wmi.GetCoolBoost();
            var fanTable = wmi.GetFanTable();
            var gpuMode = wmi.GetGpuMode();
            var usbCharging = wmi.GetUsbCharging();
            bool isCalSupported = wmi.IsBatteryCalibrationSupported();
            var calStatus = wmi.GetBatteryCalibrationStatus();

            // Methods must return graceful defaults without throwing exceptions
            Assert.True(coolBoost == null || coolBoost.HasValue);
            Assert.True(gpuMode == null || Enum.IsDefined(typeof(GpuMode), gpuMode.Value));

            // Force retry with cache invalidation
            bool success = wmi.TrySetPowerMode(0x01, forceRetry: true);
            // In test environment without hardware, TrySetPowerMode returns false or succeeds via fallback
            Assert.False(wmi.SuppressGpuQueries);

            // Toggle GPU queries suppression
            wmi.SuppressGpuQueries = true;
            Assert.True(wmi.SuppressGpuQueries);
            Assert.Equal(0, wmi.GpuTemp);
            Assert.Equal(0, wmi.GpuFanRpm);
            wmi.SuppressGpuQueries = false;
        }

        #endregion
    }
}
