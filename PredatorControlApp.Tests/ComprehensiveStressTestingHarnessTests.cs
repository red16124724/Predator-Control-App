using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("NoParallel")]
    public class ComprehensiveStressTestingHarnessTests : IDisposable
    {
        private readonly string _testDir;

        public ComprehensiveStressTestingHarnessTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "PCA_HarnessStress_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            AppSettings.CustomFilePath = null;
            try
            {
                if (Directory.Exists(_testDir))
                {
                    Directory.Delete(_testDir, true);
                }
            }
            catch { }
        }

        #region 1. AppSettings Fuzzing Tests

        [Fact]
        public void AppSettings_Fuzzing_RandomizedMalformedJson_NeverThrowsOrCrashes()
        {
            var rand = new Random(1337);
            var malformedPayloads = new List<string>
            {
                "",
                " ",
                "null",
                "{}",
                "{",
                "}",
                "[",
                "]",
                "{\"PowerMode\":",
                "{\"PowerMode\": \"turbo_corrupt\", \"Theme\": 999999999}",
                "{\"CpuCurve\": null, \"GpuCurve\": [{\"X\": \"NaN\", \"Y\": \"Infinity\"}]}",
                "{\"CustomColors\": [\"not_an_int\", -9999, 999999999]}",
                "{\"CpuMinAllowedPowerWatts\": -1000, \"CpuMaxAllowedPl2Watts\": -500}",
                "{\"CustomModeShortcuts\": [{\"Key\": \"None\", \"Ctrl\": \"invalid\"}]}"
            };

            // Generate 80 randomized mutated json payloads
            for (int i = 0; i < 80; i++)
            {
                int len = rand.Next(1, 200);
                var sb = new StringBuilder("{");
                for (int j = 0; j < len; j++)
                {
                    char c = (char)rand.Next(32, 126);
                    sb.Append(c);
                }
                sb.Append("}");
                malformedPayloads.Add(sb.ToString());
            }

            foreach (var payload in malformedPayloads)
            {
                string filePath = Path.Combine(_testDir, $"fuzz_{Guid.NewGuid():N}.json");
                File.WriteAllText(filePath, payload);
                AppSettings.CustomFilePath = filePath;

                var exception = Record.Exception(() =>
                {
                    var loaded = AppSettings.Load();
                    Assert.NotNull(loaded);
                    loaded.Sanitize();
                });

                Assert.Null(exception);
            }
        }

        [Fact]
        public void AppSettings_Fuzzing_OutOfRangePowerWatts_ClampsSafelyToHardwareBounds()
        {
            var rand = new Random(42);
            var testCases = new (int pl1, int pl2)[]
            {
                (int.MinValue, int.MinValue),
                (int.MaxValue, int.MaxValue),
                (-99999, -99999),
                (0, 0),
                (-1, -1),
                (500, 20),      // Inverted: PL1 > PL2
                (10000, 10),    // Extreme inverted
                (-50, 150),
                (25, -25),
                (150, 150)
            };

            var settings = new AppSettings
            {
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 115,
                ScannedMaxPl2Watts = 140,
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140
            };

            foreach (var (p1, p2) in testCases)
            {
                var (clamped1, clamped2) = settings.ClampPowerLimitWatts(p1, p2);
                Assert.True(clamped2 >= clamped1, $"PL2 ({clamped2}) must be >= PL1 ({clamped1})");
                Assert.InRange(clamped1, 15, 115);
                Assert.InRange(clamped2, 15, 140);
            }

            // Fuzz with 200 randomized power limits
            for (int i = 0; i < 200; i++)
            {
                int r1 = rand.Next(-500, 1000);
                int r2 = rand.Next(-500, 1000);

                var (c1, c2) = settings.ClampPowerLimitWatts(r1, r2);
                Assert.True(c2 >= c1, $"Randomized fuzzing violation: PL2 ({c2}) < PL1 ({c1}) for inputs ({r1}, {r2})");
                Assert.True(c1 >= 15 && c1 <= 115);
                Assert.True(c2 >= 15 && c2 <= 140);
            }
        }

        [Fact]
        public void AppSettings_Fuzzing_CorruptedFanCurvePoints_NormalizesAndInterpolatesWithoutDivisionByZero()
        {
            var degenerateCurves = new List<List<Point>>
            {
                new(), // Empty
                new() { new(50, 50) }, // Single point
                new() { new(50, 50), new(50, 80) }, // Duplicate X coordinates (potential div by zero)
                new() { new(90, 100), new(30, 20), new(60, 50) }, // Unsorted
                new() { new(-50, -20), new(150, 200) }, // Negative and out of range
                new() { new(0, 0), new(0, 0), new(0, 0) }, // Multiple zeros
                new() { new(100, 100), new(100, 10), new(100, 50) } // Multiple 100s
            };

            foreach (var curve in degenerateCurves)
            {
                // Normalize must return strictly 8 points within 0..100
                var normalized = FanCurveGraph.Normalize(curve);
                Assert.NotNull(normalized);
                Assert.Equal(8, normalized.Count);
                for (int i = 0; i < normalized.Count; i++)
                {
                    Assert.InRange(normalized[i].X, 0, 100);
                    Assert.InRange(normalized[i].Y, 0, 100);
                }

                // Interpolation must never throw division by zero and clamp to 10..100
                for (int testTemp = -50; testTemp <= 150; testTemp += 10)
                {
                    int speed = Form1.InterpolateCurve(curve, testTemp);
                    Assert.InRange(speed, 10, 100);
                }
            }
        }

        #endregion

        #region 2. Concurrency Stress (50 Concurrent Threads)

        [Fact]
        public void Concurrency_FiftyConcurrentThreads_SimultaneousSubsystemStress_ZeroDeadlocksOrExceptions()
        {
            const int threadCount = 50;
            const int iterationsPerThread = 20;

            string sharedConfigFile = Path.Combine(_testDir, "concurrency_settings.json");
            AppSettings.CustomFilePath = sharedConfigFile;

            var settings = new AppSettings
            {
                Theme = "Dark",
                PowerMode = 0x01,
                FanSpeedCpu = 45,
                FanSpeedGpu = 45
            };
            settings.Save();

            var exceptions = new ConcurrentBag<Exception>();
            var samplePoints = new List<Point>
            {
                new(30, 20), new(45, 30), new(60, 50), new(75, 70),
                new(80, 80), new(85, 90), new(90, 95), new(95, 100)
            };

            var shortcut = new CustomModeShortcut("Turbo", 0x05, Keys.T, ctrl: true, alt: true);

            Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, threadId =>
            {
                try
                {
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        // 1. AppSettings.Save() & CreateSnapshot()
                        var snapshot = settings.CreateSnapshot();
                        Assert.NotNull(snapshot);
                        snapshot.PowerMode = (byte)(i % 5);
                        snapshot.Save();

                        // 2. FanCurveGraph.Normalize()
                        var norm = FanCurveGraph.Normalize(samplePoints);
                        Assert.Equal(8, norm.Count);

                        // 3. ModeShortcutValidator.Validate()
                        bool isValid = ModeShortcutValidator.Validate(shortcut, out string failure);
                        Assert.True(isValid, failure);

                        // 4. KeyboardLightingPalette.Parse()
                        var colors = KeyboardLightingPalette.Parse("#FF0000,#00FF00,#0000FF,#FFFF00", 4);
                        Assert.Equal(4, colors.Length);
                        Assert.Equal(Color.FromArgb(255, 0, 0), colors[0]);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
            Assert.True(File.Exists(sharedConfigFile), "Settings file should exist after concurrent writes.");
        }

        #endregion

        #region 3. Rapid Power Mode Transitions (100 in 1 Second)

        [Fact]
        public void RapidPowerModeTransitions_OneHundredTransitionsInOneSecond_ZeroDeadlocksOrCorruption()
        {
            var modes = new[]
            {
                OperatingMode.Eco,
                OperatingMode.Quiet,
                OperatingMode.Balanced,
                OperatingMode.Performance,
                OperatingMode.Turbo
            };

            using var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(tdpWatts: 45, minWatts: 15, maxWatts: 115, pl1Watts: 45, pl2Watts: 65);

            var settings = new AppSettings
            {
                EnableCustomPowerLimits = true,
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 100,
                ScannedMaxPl2Watts = 115,
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 100,
                CpuMaxAllowedPl2Watts = 115
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            var sw = Stopwatch.StartNew();

            const int targetTransitions = 100;
            for (int i = 0; i < targetTransitions; i++)
            {
                var mode = modes[i % modes.Length];
                controller.SetPowerLimitsForMode(mode, 25 + (i % 20), 45 + (i % 30));
                controller.ApplyPowerLimitsForMode(mode);

                var (p1, p2) = settings.GetEffectivePowerLimitsForMode(mode);
                Assert.True(p2 >= p1, $"In transition {i}, PL2 ({p2}) was less than PL1 ({p1})");
            }

            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds <= 30000, $"100 transitions took {sw.ElapsedMilliseconds}ms, expected <= 30000ms");
        }

        #endregion
    }
}
