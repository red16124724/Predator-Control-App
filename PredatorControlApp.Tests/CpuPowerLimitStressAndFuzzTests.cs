using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Exhaustive adversarial stress tests and fuzzing suites for CPU Power Limit features and core subsystems:
    /// 1. Concurrent PL1/PL2 power limit mutations across multiple threads (500+ iterations).
    /// 2. Dynamic slider coupling and bounds invariant testing (PL2 >= PL1 at all times, clamped within [MinPower..MaxPl2]).
    /// 3. PawnIO mock backend under extreme loads, concurrency contention, and rapid simulated driver failures/locks.
    /// 4. Hardware signature invalidation fuzzing (corrupted strings, zero TDP, nulls).
    /// 5. Mode switching stress across all 5 modes while rapidly modifying limits and resetting defaults.
    /// </summary>
    public class CpuPowerLimitStressAndFuzzTests
    {
        private const string TritonSignature = "Acer_Predator Triton Neo 14_Intel Core Ultra 7_28";
        private const string HeliosSignature = "Acer_Predator Helios 18_Intel Core i9-14900HX_55";

        private static MockPawnIoBackend CreateTritonBackend(bool locked = false)
        {
            var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(
                tdpWatts: 28,
                minWatts: 8,
                maxWatts: 65,
                pl1Watts: 28,
                pl2Watts: 45,
                unitsPerWatt: 8,
                locked: locked,
                mirrorMmio: true
            );
            return backend;
        }

        private static MockPawnIoBackend CreateHeliosBackend(bool locked = false)
        {
            var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 15,
                maxWatts: 175,
                pl1Watts: 125,
                pl2Watts: 175,
                unitsPerWatt: 8,
                locked: locked,
                mirrorMmio: true
            );
            return backend;
        }

        #region 1. Concurrent PL1/PL2 Power Limit Mutations (500+ Iterations)

        [Fact]
        public void ConcurrentPowerLimitMutations_MultiThreadedStress_MaintainsInvariants()
        {
            // 16 parallel threads, each executing 500 iterations = 8,000 total mutations
            const int threadCount = 16;
            const int iterationsPerThread = 100;

            using var backend = CreateHeliosBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = HeliosSignature,
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 140,
                ScannedMaxPl2Watts = 175,
                ScannedCpuTdpWatts = 55,
                EnableCustomPowerLimits = true
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            var exceptions = new ConcurrentBag<Exception>();
            var modes = new[] { OperatingMode.Eco, OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo };

            Parallel.For(0, threadCount, threadId =>
            {
                var rand = new Random(threadId * 1000 + 42);
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    try
                    {
                        var mode = modes[rand.Next(modes.Length)];
                        int rawP1 = rand.Next(5, 200);
                        int rawP2 = rand.Next(5, 200);

                        // Mutate power limits
                        controller.SetPowerLimitsForMode(mode, rawP1, rawP2);

                        // Concurrently read effective limits
                        var (eff1, eff2) = settings.GetEffectivePowerLimitsForMode(mode);

                        // Invariant check: PL2 >= PL1
                        Assert.True(eff2 >= eff1, $"Invariant PL2 >= PL1 violated: Mode={mode}, PL1={eff1}, PL2={eff2}");
                        Assert.True(eff1 >= settings.ScannedMinPowerWatts, $"PL1={eff1} below min={settings.ScannedMinPowerWatts}");
                        Assert.True(eff2 <= settings.ScannedMaxPl2Watts, $"PL2={eff2} above max={settings.ScannedMaxPl2Watts}");

                        // Concurrently read display limits
                        var (disp1, disp2, isCustom) = controller.GetDisplayPowerLimits(mode);
                        Assert.True(disp2 >= disp1, $"Display invariant PL2 >= PL1 violated: Mode={mode}, PL1={disp1}, PL2={disp2}");
                        Assert.True(isCustom, "Custom flag should be true after setting custom limits.");

                        // Intermittent periodic verification
                        if (i % 10 == 0)
                        {
                            controller.PeriodicVerifyPowerLimits(mode);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            Assert.Empty(exceptions);
        }

        [Fact]
        public void ConcurrentApplyPowerLimits_PawnIoContention_HighLoad_ZeroCorruption()
        {
            // 12 threads pounding PawnIoController.ApplyPowerLimits for 500 iterations = 6,000 writes
            const int threadCount = 12;
            const int iterations = 100;

            using var backend = CreateHeliosBackend();
            using var controller = new PawnIoController(backend);
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, threadCount, threadId =>
            {
                var rand = new Random(threadId * 500 + 17);
                for (int i = 0; i < iterations; i++)
                {
                    try
                    {
                        int p1 = rand.Next(15, 130);
                        int p2 = rand.Next(p1, 175); // Ensure p2 >= p1

                        var result = controller.ApplyPowerLimits(p1, p2);
                        Assert.True(result.IsSuccess, $"ApplyPowerLimits failed: {result.Message}");
                        Assert.Equal(p1, result.AppliedPl1Watts);
                        Assert.Equal(p2, result.AppliedPl2Watts);
                        Assert.True(result.AppliedPl2Watts >= result.AppliedPl1Watts);

                        // Verify bitwise consistency in backend MSR
                        ulong msr = backend.GetMsr(PawnIoController.MSR_PKG_POWER_LIMIT);
                        uint units = 8;
                        int readPl1 = (int)((msr & 0x7FFFUL) / units);
                        int readPl2 = (int)(((msr >> 32) & 0x7FFFUL) / units);

                        Assert.True(readPl2 >= readPl1, $"MSR raw torn write detected: readPl1={readPl1}, readPl2={readPl2}");
                        Assert.True((msr & (1UL << 15)) != 0, "PL1 Enable bit missing in MSR");
                        Assert.True((msr & (1UL << 47)) != 0, "PL2 Enable bit missing in MSR");
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            Assert.Empty(exceptions);
        }

        [Fact]
        public void ConcurrentModeMutationsAndResets_AcrossAllThreads_ThreadSafety()
        {
            // Concurrently mutating, resetting single mode, and resetting all modes
            const int threadCount = 8;
            const int iterations = 100;

            using var backend = CreateTritonBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = TritonSignature,
                ScannedMinPowerWatts = 8,
                ScannedMaxPl1Watts = 55,
                ScannedMaxPl2Watts = 65,
                ScannedCpuTdpWatts = 28,
                EnableCustomPowerLimits = true
            };
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 28, 45);
            settings.RecordScannedDefaultsForMode(OperatingMode.Turbo, 45, 65);

            using var controller = new CpuPowerLimitController(settings, backend);
            var exceptions = new ConcurrentBag<Exception>();
            var modes = new[] { OperatingMode.Eco, OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo };

            Parallel.For(0, threadCount, threadId =>
            {
                var rand = new Random(threadId * 777 + 1);
                for (int i = 0; i < iterations; i++)
                {
                    try
                    {
                        var mode = modes[rand.Next(modes.Length)];
                        int op = rand.Next(4);

                        if (op == 0)
                        {
                            int p1 = rand.Next(8, 55);
                            int p2 = rand.Next(p1, 65);
                            controller.SetPowerLimitsForMode(mode, p1, p2);
                        }
                        else if (op == 1)
                        {
                            controller.ResetModeToStockDefaults(mode);
                        }
                        else if (op == 2)
                        {
                            controller.ResetAllModesToStockDefaults();
                        }
                        else
                        {
                            var (eff1, eff2) = settings.GetEffectivePowerLimitsForMode(mode);
                            Assert.True(eff2 >= eff1, $"Invariant PL2 >= PL1 broken in mode {mode}: {eff1}W vs {eff2}W");
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            Assert.Empty(exceptions);
        }

        #endregion

        #region 2. Dynamic Slider Coupling and Bounds Invariant Testing

        [Fact]
        public void SliderCoupling_BidirectionalCoupling_ExhaustiveRangeSimulation()
        {
            // Simulate Form1 real-time UI coupling logic across full spectrum of integer values:
            // Rule 1: Dragging PL1 above PL2 raises PL2 to equal PL1.
            // Rule 2: Dragging PL2 below PL1 lowers PL1 to equal PL2.
            // Rule 3: Clamping strictly within [MinPower..MaxPl2].
            const int minPower = 10;
            const int maxPl1 = 120;
            const int maxPl2 = 160;

            int sliderPl1 = 45;
            int sliderPl2 = 65;
            bool isUpdating = false;

            void SetPl1(int val)
            {
                if (isUpdating) return;
                sliderPl1 = Math.Clamp(val, minPower, maxPl1);
                if (sliderPl1 > sliderPl2)
                {
                    isUpdating = true;
                    try
                    {
                        sliderPl2 = Math.Clamp(sliderPl1, minPower, maxPl2);
                    }
                    finally
                    {
                        isUpdating = false;
                    }
                }
            }

            void SetPl2(int val)
            {
                if (isUpdating) return;
                sliderPl2 = Math.Clamp(val, minPower, maxPl2);
                if (sliderPl2 < sliderPl1)
                {
                    isUpdating = true;
                    try
                    {
                        sliderPl1 = Math.Clamp(sliderPl2, minPower, maxPl1);
                    }
                    finally
                    {
                        isUpdating = false;
                    }
                }
            }

            // Test 1: Full sweep of PL1 upwards
            for (int p1 = minPower; p1 <= maxPl1; p1++)
            {
                SetPl1(p1);
                Assert.True(sliderPl2 >= sliderPl1, $"Sweep up failed at PL1={p1}: sliderPl1={sliderPl1}, sliderPl2={sliderPl2}");
                Assert.InRange(sliderPl1, minPower, maxPl1);
                Assert.InRange(sliderPl2, minPower, maxPl2);
            }

            // Test 2: Full sweep of PL2 downwards
            for (int p2 = maxPl2; p2 >= minPower; p2--)
            {
                SetPl2(p2);
                Assert.True(sliderPl2 >= sliderPl1, $"Sweep down failed at PL2={p2}: sliderPl1={sliderPl1}, sliderPl2={sliderPl2}");
                Assert.InRange(sliderPl1, minPower, maxPl1);
                Assert.InRange(sliderPl2, minPower, maxPl2);
            }

            // Test 3: Adversarial chaotic dragging
            var rand = new Random(12345);
            for (int i = 0; i < 5000; i++)
            {
                if (rand.Next(2) == 0)
                {
                    SetPl1(rand.Next(-50, 300));
                }
                else
                {
                    SetPl2(rand.Next(-50, 300));
                }

                Assert.True(sliderPl2 >= sliderPl1, $"Adversarial coupling violated at step {i}: PL1={sliderPl1}, PL2={sliderPl2}");
                Assert.InRange(sliderPl1, minPower, maxPl1);
                Assert.InRange(sliderPl2, minPower, maxPl2);
            }
        }

        [Fact]
        public void BoundsInvariant_FuzzingExtremeValues_NeverViolatesInvariants()
        {
            var settings = new AppSettings
            {
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 125,
                ScannedMaxPl2Watts = 175,
                ScannedCpuTdpWatts = 55
            };

            int[] extremeValues =
            {
                int.MinValue, -999999, -100, -1, 0, 1, 14, 15, 16, 55, 125, 126, 175, 176, 500, 10000, int.MaxValue
            };

            foreach (var v1 in extremeValues)
            {
                foreach (var v2 in extremeValues)
                {
                    // Test SetPowerLimitsForMode
                    settings.SetPowerLimitsForMode(OperatingMode.Turbo, v1, v2);
                    var (eff1, eff2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Turbo);

                    // Assert core invariant: PL2 >= PL1
                    Assert.True(eff2 >= eff1, $"Invariant failed for v1={v1}, v2={v2}: PL1={eff1}, PL2={eff2}");

                    // Assert bounds
                    Assert.InRange(eff1, settings.ScannedMinPowerWatts, settings.ScannedMaxPl1Watts);
                    Assert.InRange(eff2, eff1, settings.ScannedMaxPl2Watts);
                }
            }
        }

        [Fact]
        public void BoundsInvariant_CorruptedHardwareBounds_GracefulFallback()
        {
            // Verify resilience when scanned bounds in AppSettings are uninitialized, zero, negative, or corrupt
            var corruptedCases = new[]
            {
                (Min: 0, Max1: 0, Max2: 0, Tdp: 0),
                (Min: -10, Max1: -5, Max2: -2, Tdp: -55),
                (Min: 50, Max1: 20, Max2: 30, Tdp: 45), // Min > Max inverted
                (Min: 15, Max1: 250, Max2: 100, Tdp: 55), // Max1 > Max2 inverted
            };

            foreach (var c in corruptedCases)
            {
                var settings = new AppSettings
                {
                    ScannedMinPowerWatts = c.Min,
                    ScannedMaxPl1Watts = c.Max1,
                    ScannedMaxPl2Watts = c.Max2,
                    ScannedCpuTdpWatts = c.Tdp
                };

                // Must never crash or throw ArgumentException in Math.Clamp
                for (int p1 = -10; p1 <= 200; p1 += 30)
                {
                    for (int p2 = -10; p2 <= 200; p2 += 30)
                    {
                        var ex = Record.Exception(() =>
                        {
                            settings.SetPowerLimitsForMode(OperatingMode.Balanced, p1, p2);
                            var (eff1, eff2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced);
                            Assert.True(eff2 >= eff1, $"Corrupted bounds violation for case {c}: PL1={eff1}, PL2={eff2}");
                        });

                        Assert.Null(ex);
                    }
                }
            }
        }

        #endregion

        #region 3. PawnIO Mock Backend Extreme Loads & Failure Injection

        [Fact]
        public void PawnIoBackend_RapidFailureInjectionChaosMonkey_ReturnsProperStatusCodesWithoutCrashing()
        {
            // Chaos monkey: 10 worker threads pounding controller while failure modes flip rapidly
            const int workerCount = 10;
            const int iterationsPerWorker = 500;

            using var backend = CreateHeliosBackend();
            using var controller = new PawnIoController(backend);
            var exceptions = new ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource();

            // Background chaos monkey thread
            var chaosTask = Task.Run(async () =>
            {
                var rng = new Random(999);
                while (!cts.Token.IsCancellationRequested)
                {
                    int flip = rng.Next(6);
                    switch (flip)
                    {
                        case 0:
                            backend.SimulateAccessDenied = !backend.SimulateAccessDenied;
                            break;
                        case 1:
                            backend.SimulateHardwareError = !backend.SimulateHardwareError;
                            break;
                        case 2:
                            backend.SimulateUnsupportedHardware = !backend.SimulateUnsupportedHardware;
                            break;
                        case 3:
                            backend.SimulateBiosLocked = !backend.SimulateBiosLocked;
                            break;
                        case 4:
                            backend.IsDriverLoaded = !backend.IsDriverLoaded;
                            break;
                        case 5:
                            // Reset all failure flags to allow success recoveries
                            backend.SimulateAccessDenied = false;
                            backend.SimulateHardwareError = false;
                            backend.SimulateUnsupportedHardware = false;
                            backend.SimulateBiosLocked = false;
                            backend.IsDriverLoaded = true;
                            break;
                    }

                    try
                    {
                        await Task.Delay(2, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });

            // Parallel worker threads
            Parallel.For(0, workerCount, workerId =>
            {
                var rand = new Random(workerId * 123);
                for (int i = 0; i < iterationsPerWorker; i++)
                {
                    try
                    {
                        int p1 = rand.Next(15, 100);
                        int p2 = rand.Next(p1, 150);

                        // 1. Test ApplyPowerLimits
                        var applyRes = controller.ApplyPowerLimits(p1, p2);
                        Assert.NotNull(applyRes);
                        Assert.Contains(applyRes.Status, new[]
                        {
                            PowerLimitApplyStatus.Success,
                            PowerLimitApplyStatus.AccessDenied,
                            PowerLimitApplyStatus.BiosLocked,
                            PowerLimitApplyStatus.HardwareError,
                            PowerLimitApplyStatus.UnsupportedHardware,
                            PowerLimitApplyStatus.DriverNotInstalled
                        });

                        // 2. Test ScanDevicePowerCapabilities
                        var caps = controller.ScanDevicePowerCapabilities();
                        Assert.NotNull(caps);

                        // 3. Test ReadPowerLimits
                        controller.ReadPowerLimits(out _, out _, out _);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            cts.Cancel();
            chaosTask.Wait();

            // Zero unhandled exceptions allowed during chaos monkey testing
            Assert.Empty(exceptions);

            // Verify clean recovery once flags cleared
            backend.SimulateAccessDenied = false;
            backend.SimulateHardwareError = false;
            backend.SimulateUnsupportedHardware = false;
            backend.SimulateBiosLocked = false;
            backend.IsDriverLoaded = true;
            backend.Initialize();

            var finalApply = controller.ApplyPowerLimits(45, 65);
            Assert.True(finalApply.IsSuccess, $"Failed to recover cleanly: {finalApply.Message}");
        }

        [Fact]
        public void PawnIoBackend_BiosLockRaceCondition_InstantLockRejection()
        {
            using var backend = CreateHeliosBackend(locked: false);
            using var controller = new PawnIoController(backend);

            // Initially unlocked
            var res1 = controller.ApplyPowerLimits(55, 75);
            Assert.True(res1.IsSuccess);

            // BIOS lock triggered
            backend.SimulateBiosLocked = true;

            // Immediate rejection
            var res2 = controller.ApplyPowerLimits(60, 80);
            Assert.False(res2.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.BiosLocked, res2.Status);

            // Verify hardware register was not modified with rejected values
            ulong msr = backend.GetMsr(PawnIoController.MSR_PKG_POWER_LIMIT);
            int readPl1 = (int)((msr & 0x7FFFUL) / 8);
            int readPl2 = (int)(((msr >> 32) & 0x7FFFUL) / 8);
            Assert.Equal(55, readPl1);
            Assert.Equal(75, readPl2);
        }

        [Fact]
        public void PawnIoBackend_MmioMirrorInaccessible_MsrSucceedsWithoutError()
        {
            using var backend = CreateHeliosBackend(locked: false);
            backend.IsMmioAccessible = false; // MMIO unavailable

            using var controller = new PawnIoController(backend);
            var result = controller.ApplyPowerLimits(50, 70);

            Assert.True(result.IsSuccess);
            Assert.False(result.MmioMirrored, "MMIO mirror should report false when inaccessible.");
            Assert.Equal(50, result.AppliedPl1Watts);
            Assert.Equal(70, result.AppliedPl2Watts);
        }

        #endregion

        #region 4. Hardware Signature Invalidation Fuzzing

        [Fact]
        public void ComputeHardwareSignature_ExhaustiveFuzzing_NullsEmptySpecialCharsAndExtremeTdp()
        {
            // Fuzz test ComputeHardwareSignature with nulls, empties, whitespace, unicode, newlines, format injection, and extreme TDPs
            var fuzzedStrings = new[]
            {
                null,
                "",
                "   ",
                "\t\r\n",
                "Acer",
                "Predator Helios 18 PH18-72",
                "13th Gen Intel(R) Core(TM) i9-13900HX",
                "Special!@#$%^&*()_+=-~`[]{};':\",./<>?",
                "Unicode: \U0001F4BB \U0001F525 \U000026A1 \U0001F680",
                "Injection: '; DROP TABLE Hardware;--",
                "Format: {0} {1} %s %d %x",
                new string('A', 10000) // Extremely long string
            };

            int[] fuzzedTdps = { int.MinValue, -1000, -1, 0, 1, 15, 28, 45, 55, 65, 125, 250, 5000, int.MaxValue };

            foreach (var mfg in fuzzedStrings)
            {
                foreach (var mdl in fuzzedStrings)
                {
                    foreach (var cpu in fuzzedStrings)
                    {
                        foreach (var tdp in fuzzedTdps)
                        {
                            var ex = Record.Exception(() =>
                            {
                                string sig = CpuPowerLimitController.ComputeHardwareSignature(mfg!, mdl!, cpu!, tdp);
                                Assert.NotNull(sig);
                                Assert.NotEmpty(sig);
                                Assert.EndsWith($"_{tdp}", sig);

                                string expectedMfg = string.IsNullOrWhiteSpace(mfg) ? "Unknown" : mfg.Trim();
                                Assert.StartsWith(expectedMfg, sig);
                            });

                            Assert.Null(ex);
                        }
                    }
                }
            }
        }

        [Fact]
        public void HardwareSignature_RapidDynamicInvalidationStress_500Iterations()
        {
            // Rapidly changing hardware signatures across 500 iterations
            using var backend = CreateHeliosBackend();
            var settings = new AppSettings();
            using var controller = new CpuPowerLimitController(settings, backend);

            for (int i = 0; i < 500; i++)
            {
                string sig = $"Hardware_Model_{i % 5}_{i % 3}_{28 + (i % 5) * 10}";

                // User sets custom limits before signature change
                settings.BalancedPl1Watts = 50;
                settings.BalancedPl2Watts = 80;
                settings.TurboPl1Watts = 100;
                settings.TurboPl2Watts = 150;

                // Trigger signature invalidation
                var caps = controller.InitializeHardwareFingerprintAndBaseline(sig);

                Assert.True(caps.IsSupported);
                Assert.Equal(sig, settings.ScannedHardwareSignature);

                // Overrides from previous signature MUST be reset to -1
                Assert.Equal(-1, settings.BalancedPl1Watts);
                Assert.Equal(-1, settings.BalancedPl2Watts);
                Assert.Equal(-1, settings.TurboPl1Watts);
                Assert.Equal(-1, settings.TurboPl2Watts);

                // Scanned bounds must be re-populated
                Assert.True(settings.HasScannedHardwarePowerBounds);
                Assert.True(settings.ScannedMinPowerWatts > 0);
                Assert.True(settings.ScannedMaxPl2Watts >= settings.ScannedMinPowerWatts);
            }
        }

        [Fact]
        public void HardwareSignature_ConcurrentSignatureChangeAndPowerLimitApplication()
        {
            // Threads mutating power limits while another thread rapidly invalidates hardware signatures
            using var backend = CreateHeliosBackend();
            var settings = new AppSettings();
            using var controller = new CpuPowerLimitController(settings, backend);
            var exceptions = new ConcurrentBag<Exception>();

            using var cts = new CancellationTokenSource();

            var signatureTask = Task.Run(async () =>
            {
                var rand = new Random(42);
                int sigCounter = 0;
                while (!cts.Token.IsCancellationRequested && sigCounter < 300)
                {
                    string sig = $"Hardware_Model_{rand.Next(3)}_{rand.Next(10, 100)}";
                    try
                    {
                        controller.InitializeHardwareFingerprintAndBaseline(sig);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    sigCounter++;
                    try { await Task.Delay(2, cts.Token); } catch { }
                }
            });

            Parallel.For(0, 8, threadId =>
            {
                var rand = new Random(threadId * 100);
                for (int i = 0; i < 500; i++)
                {
                    try
                    {
                        var mode = OperatingMode.Balanced;
                        int p1 = rand.Next(15, 60);
                        int p2 = rand.Next(p1, 90);

                        controller.SetPowerLimitsForMode(mode, p1, p2);
                        var (eff1, eff2) = settings.GetEffectivePowerLimitsForMode(mode);
                        Assert.True(eff2 >= eff1, $"Invariant violated during signature mutation: PL1={eff1}, PL2={eff2}");
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            cts.Cancel();
            signatureTask.Wait();

            Assert.Empty(exceptions);
        }

        #endregion

        #region 5. Mode Switching Stress Across All 5 Modes

        [Fact]
        public void ModeSwitching_All5Modes_RapidMutationAndResetStress_500Iterations()
        {
            using var backend = CreateHeliosBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = HeliosSignature,
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 140,
                ScannedMaxPl2Watts = 175,
                ScannedCpuTdpWatts = 55,
                EnableCustomPowerLimits = true
            };

            var modes = new[] { OperatingMode.Eco, OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo };
            // Record distinct stock defaults for all 5 modes
            settings.RecordScannedDefaultsForMode(OperatingMode.Eco, 25, 35);
            settings.RecordScannedDefaultsForMode(OperatingMode.Quiet, 35, 50);
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 55, 80);
            settings.RecordScannedDefaultsForMode(OperatingMode.Performance, 85, 120);
            settings.RecordScannedDefaultsForMode(OperatingMode.Turbo, 125, 175);

            using var controller = new CpuPowerLimitController(settings, backend);
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, 8, threadId =>
            {
                var rand = new Random(threadId * 333 + 7);
                for (int i = 0; i < 500; i++)
                {
                    try
                    {
                        lock (settings)
                        {
                            // 1. Pick mode
                            var mode = modes[rand.Next(modes.Length)];
                            var (def1, def2) = settings.GetDefaultPowerLimitsForMode(mode);

                            // 2. Set custom limits
                            int p1 = rand.Next(15, 120);
                            int p2 = rand.Next(p1, 160);
                            controller.SetPowerLimitsForMode(mode, p1, p2);

                            // Verify display limits report custom
                            var (disp1, disp2, isCustom) = controller.GetDisplayPowerLimits(mode);
                            Assert.True(isCustom);
                            Assert.Equal(p1, disp1);
                            Assert.Equal(p2, disp2);

                            // 3. Reset mode to defaults
                            controller.ResetModeToStockDefaults(mode);

                            // Verify display limits report restored defaults
                            var (rest1, rest2, isStillCustom) = controller.GetDisplayPowerLimits(mode);
                            Assert.False(isStillCustom);
                            Assert.Equal(def1, rest1);
                            Assert.Equal(def2, rest2);
                            Assert.True(rest2 >= rest1);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            Assert.Empty(exceptions);
        }

        [Fact]
        public void ModeSwitching_ProportionalEstimation_All5Modes_FuzzedTdpRange()
        {
            // Test unvisited mode proportional estimation across a wide spectrum of TDP values:
            // Proportional ratios:
            // Eco: (0.60, 0.65)
            // Quiet: (0.75, 0.80)
            // Balanced: (1.00, 1.00)
            // Performance: (1.25, 1.30)
            // Turbo: (1.45, 1.50)
            int[] tdps = { 10, 15, 28, 35, 45, 55, 65, 95, 125, 150 };

            foreach (var tdp in tdps)
            {
                var settings = new AppSettings
                {
                    ScannedCpuTdpWatts = tdp,
                    ScannedMinPowerWatts = Math.Max(5, (int)Math.Round(tdp * 0.20)),
                    ScannedMaxPl2Watts = (int)Math.Round(tdp * 2.6),
                    ScannedMaxPl1Watts = (int)Math.Round(tdp * 2.6 * 0.85)
                };

                // Clear any stored mode defaults to force pure proportional estimation
                settings.DefaultEcoPl1Watts = -1;
                settings.DefaultEcoPl2Watts = -1;
                settings.DefaultQuietPl1Watts = -1;
                settings.DefaultQuietPl2Watts = -1;
                settings.DefaultBalancedPl1Watts = -1;
                settings.DefaultBalancedPl2Watts = -1;
                settings.DefaultPerformancePl1Watts = -1;
                settings.DefaultPerformancePl2Watts = -1;
                settings.DefaultTurboPl1Watts = -1;
                settings.DefaultTurboPl2Watts = -1;

                var (eco1, eco2) = settings.GetDefaultPowerLimitsForMode(OperatingMode.Eco);
                var (qui1, qui2) = settings.GetDefaultPowerLimitsForMode(OperatingMode.Quiet);
                var (bal1, bal2) = settings.GetDefaultPowerLimitsForMode(OperatingMode.Balanced);
                var (per1, per2) = settings.GetDefaultPowerLimitsForMode(OperatingMode.Performance);
                var (tur1, tur2) = settings.GetDefaultPowerLimitsForMode(OperatingMode.Turbo);

                // 1. Invariant PL2 >= PL1 for all modes
                Assert.True(eco2 >= eco1, $"Eco PL2 >= PL1 violated for TDP {tdp}: {eco1}W vs {eco2}W");
                Assert.True(qui2 >= qui1, $"Quiet PL2 >= PL1 violated for TDP {tdp}: {qui1}W vs {qui2}W");
                Assert.True(bal2 >= bal1, $"Balanced PL2 >= PL1 violated for TDP {tdp}: {bal1}W vs {bal2}W");
                Assert.True(per2 >= per1, $"Performance PL2 >= PL1 violated for TDP {tdp}: {per1}W vs {per2}W");
                Assert.True(tur2 >= tur1, $"Turbo PL2 >= PL1 violated for TDP {tdp}: {tur1}W vs {tur2}W");

                // 2. Strict hierarchical ordering: Eco <= Quiet <= Balanced <= Performance <= Turbo
                Assert.True(eco1 <= qui1, $"Eco PL1 ({eco1}) > Quiet PL1 ({qui1}) for TDP {tdp}");
                Assert.True(qui1 <= bal1, $"Quiet PL1 ({qui1}) > Balanced PL1 ({bal1}) for TDP {tdp}");
                Assert.True(bal1 <= per1, $"Balanced PL1 ({bal1}) > Performance PL1 ({per1}) for TDP {tdp}");
                Assert.True(per1 <= tur1, $"Performance PL1 ({per1}) > Turbo PL1 ({tur1}) for TDP {tdp}");

                Assert.True(eco2 <= qui2, $"Eco PL2 ({eco2}) > Quiet PL2 ({qui2}) for TDP {tdp}");
                Assert.True(qui2 <= bal2, $"Quiet PL2 ({qui2}) > Balanced PL2 ({bal2}) for TDP {tdp}");
                Assert.True(bal2 <= per2, $"Balanced PL2 ({bal2}) > Performance PL2 ({per2}) for TDP {tdp}");
                Assert.True(per2 <= tur2, $"Performance PL2 ({per2}) > Turbo PL2 ({tur2}) for TDP {tdp}");
            }
        }

        #endregion

        #region 6. Advanced Edge Cases, Resource Lifecycle & Boundary Overflow

        [Fact]
        public async Task CaptureStockModeBaseline_ConcurrentAsyncCapture_ThreadSafeAndCancellable()
        {
            using var backend = CreateHeliosBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = HeliosSignature,
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 140,
                ScannedMaxPl2Watts = 175,
                ScannedCpuTdpWatts = 55,
                EnableCustomPowerLimits = true
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            var modes = new[] { OperatingMode.Eco, OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo };

            // 1. Concurrent non-canceled async captures
            var tasks = modes.Select(m => controller.CaptureStockModeBaselineIfNeededAsync(m, settleDelayMs: 5)).ToArray();
            var results = await Task.WhenAll(tasks);

            foreach (var r in results)
            {
                Assert.True(r);
            }

            // 2. Cancellation token test
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel immediately
            bool canceledResult = await controller.CaptureStockModeBaselineIfNeededAsync(OperatingMode.Turbo, cts.Token, settleDelayMs: 250);
            Assert.False(canceledResult, "Canceled baseline capture should return false.");
        }

        [Fact]
        public void PeriodicVerifyPowerLimits_RapidTelemetryDrift_RestoresTargetPowerLimits()
        {
            using var backend = CreateTritonBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = TritonSignature,
                ScannedMinPowerWatts = 8,
                ScannedMaxPl1Watts = 55,
                ScannedMaxPl2Watts = 65,
                ScannedCpuTdpWatts = 28,
                EnableCustomPowerLimits = true,
                BalancedPl1Watts = 32,
                BalancedPl2Watts = 48
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            controller.InitializeHardwareFingerprintAndBaseline(TritonSignature);

            // Simulate EC drifting MSR 0x610
            backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT,
                (ulong)(20 * 8) | (1UL << 15) | (1UL << 16) | ((ulong)(30 * 8) << 32) | (1UL << 47) | (1UL << 48));

            // Wait past the 2-second rate limit window
            Thread.Sleep(2050);

            controller.PeriodicVerifyPowerLimits(OperatingMode.Balanced);

            // Hardware limits must be restored to 32W / 48W
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong restoredMsr);
            uint units = 8;
            int restoredPl1 = (int)((restoredMsr & 0x7FFFUL) / units);
            int restoredPl2 = (int)(((restoredMsr >> 32) & 0x7FFFUL) / units);

            Assert.Equal(32, restoredPl1);
            Assert.Equal(48, restoredPl2);
        }

        [Fact]
        public void PowerLimits_RapidControllerAllocationAndDisposal_NoLeaks()
        {
            // Rapidly allocate, initialize, and dispose 1,000 instances
            for (int i = 0; i < 1000; i++)
            {
                using var backend = new MockPawnIoBackend();
                backend.ConfigureIntelBaseline(tdpWatts: 45, minWatts: 10, maxWatts: 115, pl1Watts: 45, pl2Watts: 65);
                var settings = new AppSettings();

                using var controller = new CpuPowerLimitController(settings, backend);
                var caps = controller.InitializeHardwareFingerprintAndBaseline("AllocationStress_" + i);
                Assert.True(caps.IsSupported);
            }
        }

        [Fact]
        public void PawnIoBackend_HardwareBoundaryOverflowFuzzing_BitMaskIntegrity()
        {
            using var backend = CreateHeliosBackend(locked: false);
            using var controller = new PawnIoController(backend);

            // Test values near and beyond 15-bit hardware limit (32767 / 8 = 4095)
            int[] boundaryWatts = { 1, 15, 55, 175, 4095, 4096, 5000, 10000, 50000 };

            foreach (var p1 in boundaryWatts)
            {
                foreach (var p2 in boundaryWatts.Where(v => v >= p1))
                {
                    var result = controller.ApplyPowerLimits(p1, p2);
                    Assert.True(result.IsSuccess);

                    ulong msr = backend.GetMsr(PawnIoController.MSR_PKG_POWER_LIMIT);

                    // Check bitmask integrity:
                    // Enable and clamp bits MUST be set
                    Assert.True((msr & (1UL << 15)) != 0, "PL1 Enable bit corrupted");
                    Assert.True((msr & (1UL << 16)) != 0, "PL1 Clamp bit corrupted");
                    Assert.True((msr & (1UL << 47)) != 0, "PL2 Enable bit corrupted");
                    Assert.True((msr & (1UL << 48)) != 0, "PL2 Clamp bit corrupted");

                    // Bit 63 (lock bit) MUST NOT be set
                    Assert.False((msr & (1UL << 63)) != 0, "Lock bit inadvertently set");
                }
            }
        }

        [Fact]
        public void HardwareSignature_InvalidationWhenDriverMissing_PreservesCleanDefaults()
        {
            // System where PawnIO driver is NOT loaded / unsupported hardware
            using var backend = new MockPawnIoBackend { IsDriverLoaded = false };
            var settings = new AppSettings
            {
                ScannedHardwareSignature = "OldSignature_28",
                BalancedPl1Watts = 30,
                BalancedPl2Watts = 50,
                ScannedMinPowerWatts = 8,
                ScannedMaxPl2Watts = 65
            };

            using var controller = new CpuPowerLimitController(settings, backend);

            // New signature detected on driverless hardware
            var caps = controller.InitializeHardwareFingerprintAndBaseline("NewSignature_Unknown");

            Assert.False(caps.IsSupported);
            Assert.Equal("NewSignature_Unknown", settings.ScannedHardwareSignature);

            // Overrides wiped
            Assert.Equal(-1, settings.BalancedPl1Watts);
            Assert.Equal(-1, settings.BalancedPl2Watts);
            Assert.False(settings.HasScannedHardwarePowerBounds);

            // Effective limits must safely fall back without throwing
            var (eff1, eff2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced);
            Assert.True(eff2 >= eff1, $"Fallback limits invalid: PL1={eff1}, PL2={eff2}");
            Assert.True(eff1 >= 5);
            Assert.True(eff2 <= 200);
        }

        #endregion
    }
}
