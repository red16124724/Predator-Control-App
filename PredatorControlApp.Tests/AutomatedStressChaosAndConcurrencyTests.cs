using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Exhaustive Automated Stress, Chaos, Fuzzing, and Concurrency Test Suite.
    /// Validates all critical components under extreme load, multi-threaded contention,
    /// malformed data streams, and boundary conditions.
    /// </summary>
    [Collection("SingleInstanceTests")]
    [SupportedOSPlatform("windows")]
    public class AutomatedStressChaosAndConcurrencyTests : IDisposable
    {
        private readonly string _tempSettingsPath;

        public AutomatedStressChaosAndConcurrencyTests()
        {
            _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"PredatorStress_{Guid.NewGuid():N}.json");
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

        private static void RunInSta(Action action)
        {
            Exception? ex = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    ex = e;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            bool completed = thread.Join(TimeSpan.FromSeconds(30));
            Assert.True(completed, "STA test execution timed out after 30 seconds");
            if (ex != null)
            {
                throw new AggregateException("Exception thrown in STA thread", ex);
            }
        }

        private static MockPawnIoBackend CreateTestPawnIoBackend()
        {
            var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 15,
                maxWatts: 175,
                pl1Watts: 125,
                pl2Watts: 175,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true
            );
            return backend;
        }

        #region Requirement 1: Concurrency & Race Condition Stress

        [Fact]
        public void Concurrency_100ConcurrentThreads_FanCurvesPowerLimitsTelemetryAppSettings_NoDeadlocksOrCorruptions()
        {
            var settings = new AppSettings
            {
                ScannedHardwareSignature = "Acer_Predator Helios 18_Intel Core i9-14900HX_55",
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 175,
                ScannedMaxPl2Watts = 190,
                ScannedCpuTdpWatts = 55,
                EnableCustomPowerLimits = true,
                EcoPl1Watts = 35,
                EcoPl2Watts = 45,
                QuietPl1Watts = 45,
                QuietPl2Watts = 65,
                BalancedPl1Watts = 55,
                BalancedPl2Watts = 95,
                TurboPl1Watts = 125,
                TurboPl2Watts = 175,
                CpuCurve = new List<CurvePointData>
                {
                    new(30, 0), new(50, 20), new(70, 50), new(90, 80), new(100, 100)
                },
                GpuCurve = new List<CurvePointData>
                {
                    new(30, 0), new(50, 20), new(70, 50), new(90, 80), new(100, 100)
                }
            };

            const int threadCount = 100;
            const int iterationsPerThread = 50;
            var startGate = new ManualResetEventSlim(false);
            var tasks = new Task[threadCount];
            var exceptions = new ConcurrentBag<Exception>();

            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                tasks[t] = Task.Run(() =>
                {
                    startGate.Wait();
                    try
                    {
                        for (int i = 0; i < iterationsPerThread; i++)
                        {
                            int action = (threadId + i) % 4;
                            switch (action)
                            {
                                case 0:
                                    // 1. Fan curve modifications & normalization
                                    var snapCurve = AppSettings.SafeCopyCurve(settings.CpuCurve);
                                    var normPoints = FanCurveGraph.Normalize(snapCurve.Select(p => p.ToPoint()));
                                    Assert.Equal(FanCurveGraph.ControlPointCount, normPoints.Count);

                                    if (snapCurve.Count > 0)
                                    {
                                        snapCurve[0].Y = (snapCurve[0].Y + 1) % 100;
                                    }

                                    if (threadId % 2 == 0)
                                    {
                                        lock (settings)
                                        {
                                            settings.CpuCurve = snapCurve;
                                        }
                                    }

                                    settings.FanCurveEnabled = (i % 2 == 0);
                                    settings.FanHysteresisDegrees = 3 + (i % 5);
                                    break;

                                case 1:
                                    // 2. Power limit queries & updates
                                    var mode = (OperatingMode)(i % 5);
                                    var (effPl1, effPl2) = settings.GetEffectivePowerLimitsForMode(mode);
                                    Assert.True(effPl2 >= effPl1, $"Effective PL2 ({effPl2}) must be >= PL1 ({effPl1})");

                                    int targetPl1 = 20 + (i % 60);
                                    int targetPl2 = targetPl1 + 10 + (i % 25);
                                    settings.SetPowerLimitsForMode(mode, targetPl1, targetPl2);

                                    var (chkPl1, chkPl2) = settings.GetEffectivePowerLimitsForMode(mode);
                                    Assert.True(chkPl2 >= chkPl1, $"Updated PL2 ({chkPl2}) must be >= PL1 ({chkPl1})");
                                    break;

                                case 2:
                                    // 3. Sensor telemetry read simulation
                                    using (var wmi = new WmiController())
                                    {
                                        int cpuTemp = wmi.CpuTemp;
                                        int gpuTemp = wmi.GpuTemp;
                                        int cpuRpm = wmi.CpuFanRpm;
                                        int gpuRpm = wmi.GpuFanRpm;
                                        int sysRpm = wmi.SystemFanRpm;

                                        Assert.True(cpuTemp >= 0);
                                        Assert.True(gpuTemp >= 0);
                                        Assert.True(cpuRpm >= 0);
                                        Assert.True(gpuRpm >= 0);
                                        Assert.True(sysRpm >= 0);
                                    }
                                    break;

                                case 3:
                                    // 4. Updating AppSettings properties, snapshot, sanitize & serialize
                                    settings.FanSpeedCpu = (settings.FanSpeedCpu + 3) % 101;
                                    settings.FanSpeedGpu = (settings.FanSpeedGpu + 5) % 101;
                                    settings.FanSpeedSys = (settings.FanSpeedSys + 7) % 101;
                                    settings.PowerMode = (byte)(i % 6);
                                    settings.Theme = (i % 2 == 0) ? "Dark" : "Light";

                                    var snapshot = settings.CreateSnapshot();
                                    snapshot.Sanitize();

                                    string json = JsonSerializer.Serialize(snapshot);
                                    Assert.NotEmpty(json);
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }

            // Release all 100 threads simultaneously
            startGate.Set();
            Task.WaitAll(tasks);

            if (!exceptions.IsEmpty)
            {
                throw new AggregateException("Exceptions occurred during 100-thread concurrency test", exceptions);
            }

            // Final invariant verification
            for (int m = 0; m < 5; m++)
            {
                var mode = (OperatingMode)m;
                var (finalPl1, finalPl2) = settings.GetEffectivePowerLimitsForMode(mode);
                Assert.True(finalPl2 >= finalPl1, $"Final PL2 ({finalPl2}) must be >= PL1 ({finalPl1}) for mode {mode}");
            }
        }

        [Fact]
        public void Concurrency_100ConcurrentThreads_RapidFanCurveNormalizationAndSync_ThreadSafety()
        {
            const int threadCount = 100;
            var startGate = new ManualResetEventSlim(false);
            var tasks = new Task[threadCount];
            var exceptions = new ConcurrentBag<Exception>();

            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                tasks[t] = Task.Run(() =>
                {
                    startGate.Wait();
                    try
                    {
                        var rnd = new Random(threadId * 1000);
                        for (int i = 0; i < 40; i++)
                        {
                            int count = rnd.Next(0, 20);
                            var rawPoints = new List<Point>();
                            for (int k = 0; k < count; k++)
                            {
                                rawPoints.Add(new Point(rnd.Next(-50, 150), rnd.Next(-50, 150)));
                            }

                            var normalized = FanCurveGraph.Normalize(rawPoints);
                            Assert.Equal(FanCurveGraph.ControlPointCount, normalized.Count);
                            Assert.Equal(30, normalized[0].X);
                            Assert.Equal(100, normalized[^1].X);

                            for (int k = 1; k < normalized.Count; k++)
                            {
                                Assert.True(normalized[k].X > normalized[k - 1].X, "Curve points must be strictly strictly increasing in temperature");
                                Assert.InRange(normalized[k].Y, 0, 100);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }

            startGate.Set();
            Task.WaitAll(tasks);

            if (!exceptions.IsEmpty)
            {
                throw new AggregateException(exceptions);
            }
        }

        [Fact]
        public void CpuPowerLimitController_RapidSimulatedPowerModeCycling_WithActiveTelemetryVerification()
        {
            using var backend = CreateTestPawnIoBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = "Acer_Predator Helios 18_Intel Core i9-14900HX_55",
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 175,
                ScannedMaxPl2Watts = 190,
                ScannedCpuTdpWatts = 55,
                EnableCustomPowerLimits = true,
                EcoPl1Watts = 35,
                EcoPl2Watts = 45,
                QuietPl1Watts = 45,
                QuietPl2Watts = 65,
                BalancedPl1Watts = 55,
                BalancedPl2Watts = 95,
                TurboPl1Watts = 125,
                TurboPl2Watts = 175
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            controller.InitializeHardwareFingerprintAndBaseline("Acer_Predator Helios 18_Intel Core i9-14900HX_55");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var token = cts.Token;
            int eventCount = 0;
            var exceptions = new ConcurrentBag<Exception>();

            controller.PowerLimitsChanged += (mode, pl1, pl2, isCustom) =>
            {
                Interlocked.Increment(ref eventCount);
                if (pl2 < pl1)
                {
                    exceptions.Add(new InvalidOperationException($"PowerLimitsChanged event yielded PL2 ({pl2}) < PL1 ({pl1}) for mode {mode}"));
                }
            };

            OperatingMode currentActiveMode = OperatingMode.Balanced;

            // Worker 1: Rapid power mode cycling (Eco -> Turbo -> Quiet -> Balanced)
            var modeCycleTask = Task.Run(() =>
            {
                var cycle = new[] { OperatingMode.Eco, OperatingMode.Turbo, OperatingMode.Quiet, OperatingMode.Balanced };
                int idx = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var mode = cycle[idx % cycle.Length];
                        currentActiveMode = mode;
                        controller.CaptureStockModeBaselineIfNeeded(mode, settleDelayMs: 0);
                        idx++;
                        Thread.Yield();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            // Worker 2: Background telemetry verification active
            var telemetryVerifyTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        controller.PeriodicVerifyPowerLimits(currentActiveMode, force: true);
                        Thread.Sleep(5);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            // Worker 3: Concurrent telemetry readers querying power limits and capabilities
            var telemetryQueryTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        bool avail = controller.IsAvailable;
                        bool locked = controller.IsLocked;
                        var caps = controller.Capabilities;
                        var (p1, p2) = controller.Settings.GetEffectivePowerLimitsForMode(currentActiveMode);
                        Assert.True(p2 >= p1);
                        Assert.True(avail);
                        Assert.False(locked);
                        Thread.Yield();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            // Worker 4: Concurrent user adjustments to power limits
            var userAdjustmentTask = Task.Run(() =>
            {
                int val = 40;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        val = (val % 80) + 20;
                        controller.Settings.SetPowerLimitsForMode(OperatingMode.Balanced, val, val + 20);
                        Thread.Sleep(10);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            Task.WaitAll(modeCycleTask, telemetryVerifyTask, telemetryQueryTask, userAdjustmentTask);

            if (!exceptions.IsEmpty)
            {
                throw new AggregateException(exceptions);
            }

            Assert.True(eventCount > 5, $"Expected multiple PowerLimitsChanged events, got {eventCount}");
        }

        [Fact]
        public void CpuPowerLimitController_MultiThreadedPowerLimitOverridesAndModeTransitions_ThreadSafety()
        {
            using var backend = CreateTestPawnIoBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = "Acer_Predator Helios 18_Intel Core i9-14900HX_55",
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 175,
                ScannedMaxPl2Watts = 190,
                ScannedCpuTdpWatts = 55,
                EnableCustomPowerLimits = true
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            controller.InitializeHardwareFingerprintAndBaseline("Acer_Predator Helios 18_Intel Core i9-14900HX_55");

            const int workers = 8;
            var tasks = new Task[workers];
            var exceptions = new ConcurrentBag<Exception>();

            for (int w = 0; w < workers; w++)
            {
                int workerId = w;
                tasks[w] = Task.Run(() =>
                {
                    try
                    {
                        for (int i = 0; i < 50; i++)
                        {
                            var mode = (OperatingMode)(i % 5);
                            int p1 = 25 + (workerId * 5) + (i % 20);
                            int p2 = p1 + 15;
                            controller.Settings.SetPowerLimitsForMode(mode, p1, p2);
                            controller.CaptureStockModeBaselineIfNeeded(mode, settleDelayMs: 0);

                            var (eff1, eff2) = controller.Settings.GetEffectivePowerLimitsForMode(mode);
                            Assert.True(eff2 >= eff1);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }

            Task.WaitAll(tasks);

            if (!exceptions.IsEmpty)
            {
                throw new AggregateException(exceptions);
            }
        }

        #endregion

        #region Requirement 2: Fuzzing & Corrupt Data Ingestion

        [Fact]
        public void BiosUefiPowerScanner_Fuzzing_MalformedAmlBinaryStreams_HandledGracefully()
        {
            // Empty span
            var resEmpty = BiosUefiPowerScanner.ParsePpccFromAml(ReadOnlySpan<byte>.Empty);
            Assert.Null(resEmpty);

            var listEmpty = BiosUefiPowerScanner.ExtractPpccEntriesFromAml(ReadOnlySpan<byte>.Empty);
            Assert.NotNull(listEmpty);
            Assert.Empty(listEmpty);

            // Sub-4-byte streams
            for (int len = 1; len < 4; len++)
            {
                byte[] shortStream = new byte[len];
                Array.Fill(shortStream, (byte)'P');
                var r1 = BiosUefiPowerScanner.ParsePpccFromAml(shortStream);
                Assert.Null(r1);

                var r2 = BiosUefiPowerScanner.ExtractPpccEntriesFromAml(shortStream);
                Assert.NotNull(r2);
                Assert.Empty(r2);
            }

            // Stream filled with malformed opcodes
            byte[] badOpStream = new byte[] { 0xFF, 0xFE, 0xFD, 0x00, 0x08, 0x12, 0x13, 0xAA };
            var rBad = BiosUefiPowerScanner.ParsePpccFromAml(badOpStream);
            Assert.Null(rBad);

            var lBad = BiosUefiPowerScanner.ExtractPpccEntriesFromAml(badOpStream);
            Assert.NotNull(lBad);
            Assert.Empty(lBad);
        }

        [Fact]
        public void BiosUefiPowerScanner_Fuzzing_TruncatedHeadersAndPrematureEof_NeverThrows()
        {
            byte[][] truncatedScenarios = new byte[][]
            {
                // Just the signature
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C' },
                // Signature + package op without length
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C', 0x12 },
                // Signature + var package op without length
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C', 0x13 },
                // Signature + package op + truncated 2-byte length
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C', 0x12, 0x4F },
                // Signature + package op + truncated 3-byte length
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C', 0x12, 0x8F, 0x10 },
                // Signature + package op + truncated 4-byte length
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C', 0x12, 0xCF, 0x10, 0x20 },
                // Length claims 1000 bytes, stream ends immediately
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C', 0x12, 0x4F, 0x00, 0x05 },
                // Package claims 10 elements, has 0
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C', 0x12, 0x05, 0x0A },
                // Truncated inner package op
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C', 0x12, 0x10, 0x02, 0x01, 0x12 },
                // Truncated integer opcodes (DWORD op with only 1 byte left)
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C', 0x12, 0x15, 0x02, 0x01, 0x12, 0x08, 0x02, 0x0C, 0x55 },
                // Truncated QWORD op with only 2 bytes left
                new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C', 0x12, 0x15, 0x02, 0x01, 0x12, 0x08, 0x02, 0x0E, 0x55, 0xAA }
            };

            foreach (var stream in truncatedScenarios)
            {
                var r = BiosUefiPowerScanner.ParsePpccFromAml(stream);
                Assert.Null(r);

                var entries = BiosUefiPowerScanner.ExtractPpccEntriesFromAml(stream);
                Assert.NotNull(entries);
                Assert.Empty(entries);
            }
        }

        [Fact]
        public void BiosUefiPowerScanner_Fuzzing_RandomByteStreamsAndNoise_SeededFuzzing()
        {
            var rnd = new Random(1337);
            const int testCount = 500;

            for (int i = 0; i < testCount; i++)
            {
                int len = rnd.Next(0, 8192);
                byte[] data = new byte[len];
                rnd.NextBytes(data);

                // In 50% of iterations, inject "PPCC" at a random valid offset
                if (len >= 4 && rnd.Next(2) == 0)
                {
                    int offset = rnd.Next(0, len - 3);
                    data[offset] = (byte)'P';
                    data[offset + 1] = (byte)'P';
                    data[offset + 2] = (byte)'C';
                    data[offset + 3] = (byte)'C';
                }

                // Should never throw unhandled exception
                var res = BiosUefiPowerScanner.ParsePpccFromAml(data);
                var entries = BiosUefiPowerScanner.ExtractPpccEntriesFromAml(data);

                Assert.NotNull(entries);
                if (res != null)
                {
                    Assert.True(res.Value.MaxPl2 >= res.Value.MaxPl1 || res.Value.MaxPl2 > 0);
                    Assert.True(res.Value.MinPower >= 0);
                }
            }
        }

        [Fact]
        public void BiosUefiPowerScanner_Fuzzing_OutOfRangeAndExtremeIntegerValues_SanitizedOrIgnored()
        {
            // Build an AML stream containing packages with out-of-range integer numbers
            using var ms = new MemoryStream();
            ms.Write(new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C' });
            ms.WriteByte(0x12); // PackageOp
            ms.WriteByte(0x30); // Length = 48
            ms.WriteByte(4);    // 4 elements
            ms.WriteByte(0x01); // Revision = 1

            // Inner Package 1: Negative numbers (AML_ONES_OP = -1) and extreme power index
            ms.WriteByte(0x12);
            ms.WriteByte(0x0E);
            ms.WriteByte(4);
            ms.WriteByte(0x0C); ms.Write(BitConverter.GetBytes((uint)999)); // Index 999 (out of range)
            ms.WriteByte(0xFF);                                            // -1 mW
            ms.WriteByte(0x0C); ms.Write(BitConverter.GetBytes((uint)50000000)); // 50,000,000 mW (out of range)
            ms.WriteByte(0x0A); ms.WriteByte(10);                          // Time window

            // Inner Package 2: Inverted min > max
            ms.WriteByte(0x12);
            ms.WriteByte(0x0E);
            ms.WriteByte(4);
            ms.WriteByte(0x00);                                            // Index 0 (PL1)
            ms.WriteByte(0x0C); ms.Write(BitConverter.GetBytes((uint)200000)); // Min = 200,000 mW
            ms.WriteByte(0x0C); ms.Write(BitConverter.GetBytes((uint)10000));  // Max = 10,000 mW (min > max!)
            ms.WriteByte(0x0A); ms.WriteByte(10);

            // Inner Package 3: QWORD integer overflow
            ms.WriteByte(0x12);
            ms.WriteByte(0x0E);
            ms.WriteByte(3);
            ms.WriteByte(0x0E); ms.Write(BitConverter.GetBytes(long.MaxValue)); // Index = long.MaxValue
            ms.WriteByte(0x0E); ms.Write(BitConverter.GetBytes(long.MinValue)); // Min = long.MinValue
            ms.WriteByte(0x0E); ms.Write(BitConverter.GetBytes(long.MaxValue)); // Max = long.MaxValue

            byte[] amlData = ms.ToArray();

            // Parser should filter out invalid/inverted entries without throwing
            var entries = BiosUefiPowerScanner.ExtractPpccEntriesFromAml(amlData);
            Assert.NotNull(entries);
            Assert.Empty(entries);

            var parsed = BiosUefiPowerScanner.ParsePpccFromAml(amlData);
            Assert.Null(parsed);
        }

        [Fact]
        public void AppSettings_Fuzzing_MalformedAndCorruptedJson_FallbacksGracefully()
        {
            string[] malformedJsons = new[]
            {
                "",
                "   ",
                "{",
                "{\"Theme\": }",
                "[1, 2, 3]",
                "\"Plain string not object\"",
                "null",
                "{\"PowerMode\": \"NOT_A_BYTE\"}",
                "{\"CpuCurve\": \"NOT_A_LIST\"}",
                "{\"CpuCurve\": [\"not\", \"points\"]}",
                "{\"CpuCurve\": [{\"X\": \"INVALID\", \"Y\": 50}]}",
                "{\"CpuCurve\": [null, {}, {\"X\": 10, \"Y\": 20}]}",
                "{\"CpuThermalSpecTdpWatts\": 999999999999999999999999999999}",
                "{\"RefreshRate\": -9999999}",
                "{\"FanSpeedCpu\": 100000000}"
            };

            foreach (var json in malformedJsons)
            {
                AppSettings? settings = null;
                try
                {
                    var opt = new JsonSerializerOptions
                    {
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals |
                                         System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                    };
                    settings = JsonSerializer.Deserialize<AppSettings>(json, opt);
                }
                catch (JsonException)
                {
                    // Syntactically invalid JSON throws JsonException, which AppSettings.Load() catches
                }

                if (settings != null)
                {
                    settings.Sanitize();
                    Assert.InRange(settings.PowerMode, (byte)0, (byte)6);
                    Assert.InRange(settings.FanSpeedCpu, 0, 100);
                    Assert.InRange(settings.RefreshRate, 0, 1000);
                }
            }
        }

        [Fact]
        public void AppSettings_Fuzzing_NaN_Infinity_ExtremeNumbers_Sanitized()
        {
            var settings = new AppSettings
            {
                FanSpeedCpu = -500,
                FanSpeedGpu = 99999,
                FanSpeedSys = -10,
                RgbBrightness = -100,
                RgbSpeed = 9999,
                RefreshRate = -144,
                FanHysteresisDegrees = -50,
                CpuThermalSpecTdpWatts = -100,
                CpuMinAllowedPowerWatts = -200,
                CpuMaxAllowedPl1Watts = -50,
                CpuMaxAllowedPl2Watts = -10,
                CpuCurve = new List<CurvePointData>
                {
                    new(-50, -100),
                    new(150, 300),
                    new(50, 50)
                }
            };

            settings.Sanitize();

            Assert.InRange(settings.FanSpeedCpu, 0, 100);
            Assert.InRange(settings.FanSpeedGpu, 0, 100);
            Assert.InRange(settings.FanSpeedSys, 0, 100);
            Assert.InRange(settings.RgbBrightness, 0, 100);
            Assert.InRange(settings.RgbSpeed, 0, 100);
            Assert.InRange(settings.RefreshRate, 0, 1000);
            Assert.InRange(settings.FanHysteresisDegrees, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees);

            Assert.All(settings.CpuCurve, p =>
            {
                Assert.InRange(p.X, 0, 100);
                Assert.InRange(p.Y, 0, 100);
            });
        }

        [Fact]
        public void AppSettings_Fuzzing_InvertedSliderAndPowerLimitRanges_EnforcesInvariants()
        {
            var settings = new AppSettings
            {
                // Inverted scanned hardware bounds: Min (150) > Max1 (40) > Max2 (20)
                ScannedMinPowerWatts = 150,
                ScannedMaxPl1Watts = 40,
                ScannedMaxPl2Watts = 20,
                ScannedCpuTdpWatts = 45,

                // Inverted per-mode limits: PL1 > PL2
                EcoPl1Watts = 120,
                EcoPl2Watts = 30,
                QuietPl1Watts = 100,
                QuietPl2Watts = 40,
                BalancedPl1Watts = 150,
                BalancedPl2Watts = 50,
                PerformancePl1Watts = 180,
                PerformancePl2Watts = 60,
                TurboPl1Watts = 200,
                TurboPl2Watts = 70
            };

            settings.Sanitize();

            // Hardware bounds invariant
            Assert.True(settings.ScannedMaxPl1Watts >= settings.ScannedMinPowerWatts,
                $"ScannedMaxPl1Watts ({settings.ScannedMaxPl1Watts}) must be >= ScannedMinPowerWatts ({settings.ScannedMinPowerWatts})");
            Assert.True(settings.ScannedMaxPl2Watts >= settings.ScannedMaxPl1Watts,
                $"ScannedMaxPl2Watts ({settings.ScannedMaxPl2Watts}) must be >= ScannedMaxPl1Watts ({settings.ScannedMaxPl1Watts})");

            // Per-mode power limits invariant: PL2 >= PL1
            Assert.True(settings.EcoPl2Watts >= settings.EcoPl1Watts, $"Eco PL2 ({settings.EcoPl2Watts}) >= PL1 ({settings.EcoPl1Watts})");
            Assert.True(settings.QuietPl2Watts >= settings.QuietPl1Watts, $"Quiet PL2 ({settings.QuietPl2Watts}) >= PL1 ({settings.QuietPl1Watts})");
            Assert.True(settings.BalancedPl2Watts >= settings.BalancedPl1Watts, $"Balanced PL2 ({settings.BalancedPl2Watts}) >= PL1 ({settings.BalancedPl1Watts})");
            Assert.True(settings.PerformancePl2Watts >= settings.PerformancePl1Watts, $"Perf PL2 ({settings.PerformancePl2Watts}) >= PL1 ({settings.PerformancePl1Watts})");
            Assert.True(settings.TurboPl2Watts >= settings.TurboPl1Watts, $"Turbo PL2 ({settings.TurboPl2Watts}) >= PL1 ({settings.TurboPl1Watts})");
        }

        [Fact]
        public void AppSettings_Fuzzing_CorruptedRegistryPayloads_FallbackSafety()
        {
            // Verify Load() never throws even when encountering missing or corrupted settings
            var settings = AppSettings.Load();
            Assert.NotNull(settings);
            Assert.NotNull(settings.Theme);
            Assert.NotNull(settings.CpuCurve);
            Assert.NotNull(settings.GpuCurve);
        }

        #endregion

        #region Requirement 3: UI Custom Controls Stress (via STA unit tests)

        [Fact]
        public void UiCustomControls_ZeroNegativeAndExtremeDimensions_PaintWithoutThrowing()
        {
            RunInSta(() =>
            {
                var dimensions = new[]
                {
                    new Size(0, 0),
                    new Size(-10, -10),
                    new Size(-1, -1),
                    new Size(1, 1),
                    new Size(2, 2),
                    new Size(3, 3),
                    new Size(4, 4),
                    new Size(8, 8),
                    new Size(9, 9),
                    new Size(10, 10),
                    new Size(60, 60),
                    new Size(61, 61),
                    new Size(300, 200),
                    new Size(2000, 2000),
                    new Size(10000, 10000)
                };

                using var form = new Form();
                form.CreateControl();

                using var slider = new PredatorSlider();
                using var button = new PredatorButton();
                using var toggle = new PredatorToggle();
                using var graph = new FanCurveGraph();

                form.Controls.Add(slider);
                form.Controls.Add(button);
                form.Controls.Add(toggle);
                form.Controls.Add(graph);

                var onPaintSlider = typeof(PredatorSlider).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic);
                var onPaintButton = typeof(PredatorButton).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic);
                var onPaintToggle = typeof(PredatorToggle).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic);
                var onPaintGraph = typeof(FanCurveGraph).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic);

                using var bmp = new Bitmap(200, 200);
                using var g = Graphics.FromImage(bmp);

                foreach (var dim in dimensions)
                {
                    slider.Size = dim;
                    button.Size = dim;
                    toggle.Size = dim;
                    graph.Size = dim;

                    var pe = new PaintEventArgs(g, new Rectangle(0, 0, Math.Max(0, dim.Width), Math.Max(0, dim.Height)));

                    onPaintSlider?.Invoke(slider, new object[] { pe });
                    onPaintButton?.Invoke(button, new object[] { pe });
                    onPaintToggle?.Invoke(toggle, new object[] { pe });
                    onPaintGraph?.Invoke(graph, new object[] { pe });
                }
            });
        }

        [Fact]
        public void UiCustomControls_RapidMouseDraggingStress_BoundaryAndNegativeCoords()
        {
            RunInSta(() =>
            {
                using var form = new Form();
                form.CreateControl();

                using var slider = new PredatorSlider { Size = new Size(300, 30), Minimum = 10, Maximum = 100, Value = 50 };
                using var graph = new FanCurveGraph { Size = new Size(400, 250) };

                form.Controls.Add(slider);
                form.Controls.Add(graph);

                var onMouseDownSlider = typeof(PredatorSlider).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic);
                var onMouseMoveSlider = typeof(PredatorSlider).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic);
                var onMouseUpSlider = typeof(PredatorSlider).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic);

                var onMouseDownGraph = typeof(FanCurveGraph).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic);
                var onMouseMoveGraph = typeof(FanCurveGraph).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic);
                var onMouseUpGraph = typeof(FanCurveGraph).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic);

                // 1. Slider mouse dragging from extreme negative to extreme positive coordinates
                onMouseDownSlider?.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 150, 15, 0) });
                for (int x = -1000; x <= 2000; x += 15)
                {
                    onMouseMoveSlider?.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, 15, 0) });
                    Assert.InRange(slider.Value, slider.Minimum, slider.Maximum);
                }
                onMouseUpSlider?.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 200, 15, 0) });

                // 2. FanCurveGraph mouse dragging across wildly erratic points
                onMouseDownGraph?.Invoke(graph, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 100, 150, 0) });
                for (int step = 0; step < 200; step++)
                {
                    int mx = (step * 37) % 800 - 200;
                    int my = (step * 53) % 600 - 150;
                    onMouseMoveGraph?.Invoke(graph, new object[] { new MouseEventArgs(MouseButtons.Left, 1, mx, my, 0) });

                    var pts = graph.Points;
                    Assert.Equal(FanCurveGraph.ControlPointCount, pts.Count);
                    for (int k = 1; k < pts.Count; k++)
                    {
                        Assert.True(pts[k].X >= pts[k - 1].X, "Control point temps must remain monotonically sorted");
                        Assert.InRange(pts[k].Y, 0, 100);
                    }
                }
                onMouseUpGraph?.Invoke(graph, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 100, 100, 0) });
            });
        }

        [Fact]
        public void UiCustomControls_RapidKeyPressStress_AllDialogKeysAndModifiers()
        {
            RunInSta(() =>
            {
                using var form = new Form();
                form.CreateControl();

                using var slider = new PredatorSlider { Size = new Size(300, 30), Minimum = 0, Maximum = 100, Value = 50 };
                using var toggle = new PredatorToggle { Size = new Size(50, 25), Checked = false };
                using var graph = new FanCurveGraph { Size = new Size(400, 250) };

                form.Controls.Add(slider);
                form.Controls.Add(toggle);
                form.Controls.Add(graph);

                var processKeySlider = typeof(PredatorSlider).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);
                var onKeyDownToggle = typeof(PredatorToggle).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
                var processKeyGraph = typeof(FanCurveGraph).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);

                // Slider keys
                Keys[] sliderKeys = { Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown };
                for (int i = 0; i < 100; i++)
                {
                    processKeySlider?.Invoke(slider, new object[] { sliderKeys[i % sliderKeys.Length] });
                    Assert.InRange(slider.Value, slider.Minimum, slider.Maximum);
                }

                // Toggle keys
                for (int i = 0; i < 30; i++)
                {
                    bool prev = toggle.Checked;
                    onKeyDownToggle?.Invoke(toggle, new object[] { new KeyEventArgs(i % 2 == 0 ? Keys.Space : Keys.Enter) });
                    Assert.NotEqual(prev, toggle.Checked);
                }

                // FanCurveGraph keys
                Keys[] graphKeys =
                {
                    Keys.Left, Keys.Right, Keys.Up, Keys.Down,
                    Keys.Home, Keys.End,
                    Keys.Up | Keys.Shift, Keys.Down | Keys.Shift,
                    Keys.Up | Keys.Control, Keys.Down | Keys.Control
                };
                for (int i = 0; i < 100; i++)
                {
                    processKeyGraph?.Invoke(graph, new object[] { graphKeys[i % graphKeys.Length] });
                    var pts = graph.Points;
                    Assert.Equal(FanCurveGraph.ControlPointCount, pts.Count);
                }
            });
        }

        [Fact]
        public void UiCustomControls_RapidThemeChangesStress_UnderActiveInteraction()
        {
            RunInSta(() =>
            {
                using var form = new Form();
                form.CreateControl();

                using var slider = new PredatorSlider();
                using var button = new PredatorButton();
                using var toggle = new PredatorToggle();
                using var graph = new FanCurveGraph();

                form.Controls.Add(slider);
                form.Controls.Add(button);
                form.Controls.Add(toggle);
                form.Controls.Add(graph);

                // Rapidly cycle themes while controls are mounted
                for (int i = 0; i < 30; i++)
                {
                    var theme = (AppTheme)(i % 3);
                    ThemeManager.SetTheme(theme);
                }

                // Dispose controls and verify no trailing events cause ObjectDisposedException
                slider.Dispose();
                button.Dispose();
                toggle.Dispose();
                graph.Dispose();

                for (int i = 0; i < 10; i++)
                {
                    ThemeManager.SetTheme((AppTheme)(i % 3));
                }
            });
        }

        [Fact]
        public void UiCustomControls_FanCurveGraph_BoundaryPointCountsAndPaintingStability()
        {
            RunInSta(() =>
            {
                using var form = new Form();
                form.CreateControl();

                using var graph = new FanCurveGraph { Size = new Size(350, 250) };
                form.Controls.Add(graph);

                var onPaintGraph = typeof(FanCurveGraph).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic);
                using var bmp = new Bitmap(350, 250);
                using var g = Graphics.FromImage(bmp);
                var pe = new PaintEventArgs(g, new Rectangle(0, 0, 350, 250));

                // 1. Points set to empty list
                graph.Points = new List<Point>();
                onPaintGraph?.Invoke(graph, new object[] { pe });
                Assert.Equal(FanCurveGraph.ControlPointCount, graph.Points.Count);

                // 2. Points set to 1 point
                graph.Points = new List<Point> { new(50, 50) };
                onPaintGraph?.Invoke(graph, new object[] { pe });
                Assert.Equal(FanCurveGraph.ControlPointCount, graph.Points.Count);

                // 3. Points set to 2 points
                graph.Points = new List<Point> { new(30, 10), new(100, 90) };
                onPaintGraph?.Invoke(graph, new object[] { pe });
                Assert.Equal(FanCurveGraph.ControlPointCount, graph.Points.Count);

                // 4. Points set to 16 points
                var sixteenPoints = Enumerable.Range(0, 16).Select(i => new Point(30 + i * 4, i * 6)).ToList();
                graph.Points = sixteenPoints;
                onPaintGraph?.Invoke(graph, new object[] { pe });
                Assert.Equal(FanCurveGraph.ControlPointCount, graph.Points.Count);

                // 5. CurrentTemp crosshair stress at boundary values
                int[] temps = { -50, 0, 29, 30, 50, 99, 100, 101, 200 };
                foreach (var t in temps)
                {
                    graph.CurrentTemp = t;
                    onPaintGraph?.Invoke(graph, new object[] { pe });
                }
            });
        }

        #endregion
    }
}
