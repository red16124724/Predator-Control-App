using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    [Collection("NoParallel")]
    public class PerformanceAndConcurrencyStressAuditTests
    {
        private const string TestFingerprint = "Acer_Predator Helios 16_Intel Core i7-13700HX_55";

        [Fact]
        public void ThermalSafetyService_SimulatedSpikesAndCoordination_TransientCapIntegrity()
        {
            var settings = new AppSettings
            {
                PowerMode = (byte)OperatingMode.Performance,
                BiosHardwareFingerprint = TestFingerprint,
                CpuThermalSpecTdpWatts = 55,
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140,
                EnableCustomPowerLimits = true
            };
            settings.SetPowerLimitsForMode(OperatingMode.Performance, 90, 110);
            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Performance, 55, 75, true);

            var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(55, 15, 140, 90, 110, unitsPerWatt: 8, mirrorMmio: true);
            using var pawnIo = new PawnIoController(backend);
            using var controller = new CpuPowerLimitController(null, settings, pawnIo);

            var safetyService = new ThermalSafetyService(new ThermalLimits(75, 88, 95), new ThermalLimits(70, 80, 85));

            // Initial state: Hardware has 90W PL1, 110W PL2
            var initialRes = controller.ApplyActiveModePowerLimits(OperatingMode.Performance);
            Assert.True(initialRes.IsSuccess);
            Assert.Equal(90, initialRes.AppliedPl1Watts);
            Assert.Equal(110, initialRes.AppliedPl2Watts);

            // Step 1: Simulate Normal Temp (60C) -> No Cap
            safetyService.UpdateCpuPowerSafety(60, controller, 90, 15, 55);
            Assert.Null(controller.ThermalSafetyCapWatts);
            var (pl1, pl2, isCustom) = controller.GetDisplayPowerLimits(OperatingMode.Performance);
            Assert.Equal(90, pl1);
            Assert.Equal(110, pl2);
            Assert.True(isCustom);

            // Step 2: Thermal Spike (90C - above 88C ceiling) -> Proportional Cap
            safetyService.UpdateCpuPowerSafety(90, controller, 90, 15, 55);
            Assert.NotNull(controller.ThermalSafetyCapWatts);
            int cap90 = controller.ThermalSafetyCapWatts!.Value;
            Assert.Equal(55, cap90);

            // Hardware must reflect cap
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong msrVal);
            int currentHwPl1 = (int)((msrVal & 0x7FFFUL) / 8);
            Assert.Equal(55, currentHwPl1);

            // AppSettings must NOT be corrupted!
            var (userPl1, userPl2) = settings.GetUserPowerLimitsForMode(OperatingMode.Performance);
            Assert.Equal(90, userPl1);
            Assert.Equal(110, userPl2);

            // Step 3: Critical Thermal Spike (98C - above 95C critical) -> Dropped to Min Floor
            safetyService.UpdateCpuPowerSafety(98, controller, 90, 15, 55);
            Assert.NotNull(controller.ThermalSafetyCapWatts);
            int capCrit = controller.ThermalSafetyCapWatts!.Value;
            Assert.Equal(15, capCrit);

            // AppSettings still uncorrupted!
            var (userPl1After, userPl2After) = settings.GetUserPowerLimitsForMode(OperatingMode.Performance);
            Assert.Equal(90, userPl1After);
            Assert.Equal(110, userPl2After);

            // Step 4: Rapid Cool Down (50C) -> Cap Cleared, Full Power Restored
            safetyService.UpdateCpuPowerSafety(50, controller, 90, 15, 55);
            Assert.Null(controller.ThermalSafetyCapWatts);

            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong restoredMsr);
            int restoredHwPl1 = (int)((restoredMsr & 0x7FFFUL) / 8);
            Assert.Equal(90, restoredHwPl1);
        }

        [Fact]
        public void PowerLimitController_ResetAllModes_EventOutsideLock_NoDeadlock()
        {
            var settings = new AppSettings
            {
                BiosHardwareFingerprint = TestFingerprint,
                CpuThermalSpecTdpWatts = 55,
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140,
                EnableCustomPowerLimits = true
            };
            var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(55, 15, 140, 55, 75, unitsPerWatt: 8, mirrorMmio: true);
            using var pawnIo = new PawnIoController(backend);
            using var controller = new CpuPowerLimitController(null, settings, pawnIo);

            bool callbackExecuted = false;
            int reentrancyGuard = 0;
            controller.PowerLimitsChanged += (mode, pl1, pl2, isCustom) =>
            {
                if (Interlocked.CompareExchange(ref reentrancyGuard, 1, 0) != 0) return;
                // Cross-thread call into controller now completes without deadlock because lock was released
                var t = Task.Run(() =>
                {
                    controller.ApplyActiveModePowerLimits();
                });
                Assert.True(t.Wait(TimeSpan.FromSeconds(2)), "Cross-thread re-entry must not deadlock!");
                callbackExecuted = true;
            };

            controller.ResetAllModesToStockDefaults();
            Assert.True(callbackExecuted);
        }

        [Fact]
        public async Task PowerModeSwitchGuard_RapidTransitionsStress_LatestWinsAndNoDeadlock()
        {
            var appliedModes = new ConcurrentBag<byte>();
            int hardwareApplyCalls = 0;

            using var guard = new PowerModeSwitchGuard(
                applyHardware: mode =>
                {
                    Interlocked.Increment(ref hardwareApplyCalls);
                    appliedModes.Add(mode);
                    Thread.Sleep(20);
                    return true;
                },
                minIntervalMs: 50,
                settleMs: 20
            );

            const int requestsPerThread = 20;
            var tasks = new Task[10];
            for (int t = 0; t < 10; t++)
            {
                byte targetMode = (byte)(t % 5);
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < requestsPerThread; i++)
                    {
                        guard.Request(targetMode);
                        Thread.Sleep(5);
                    }
                });
            }

            await Task.WhenAll(tasks);

            int retries = 0;
            while (guard.IsSwitching && retries++ < 100)
            {
                await Task.Delay(50);
            }

            Assert.False(guard.IsSwitching);
            Assert.True(guard.CompletedCount > 0);
            Assert.True(guard.CoalescedCount > 0, $"Expected coalesced requests under load, got {guard.CoalescedCount}");
            Assert.True(guard.FailedCount == 0);
        }

        [Fact]
        public void PdhLoadMonitor_ConcurrentSampleAndDispose_StressStability()
        {
            for (int run = 0; run < 5; run++)
            {
                var monitor = new PdhLoadMonitor();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                var token = cts.Token;
                var exceptions = new ConcurrentBag<Exception>();

                var tasks = new Task[8];
                for (int i = 0; i < 8; i++)
                {
                    tasks[i] = Task.Run(() =>
                    {
                        while (!token.IsCancellationRequested)
                        {
                            try
                            {
                                var (cpu, gpu) = monitor.Sample(false);
                            }
                            catch (Exception ex)
                            {
                                exceptions.Add(ex);
                            }
                        }
                    });
                }

                Thread.Sleep(100);
                monitor.Dispose();

                Task.WaitAll(tasks);
                Assert.Empty(exceptions);
            }
        }

        [Fact]
        public async Task ThermalSafetyCoordinator_RapidChaoticTemperatures_HighThroughputStability()
        {
            var coordinator = new ThermalSafetyCoordinator();
            var decisions = new ConcurrentBag<ThermalDecision>();

            var tasks = new Task[12];
            for (int i = 0; i < 12; i++)
            {
                int threadId = i;
                tasks[i] = Task.Run(() =>
                {
                    for (int step = 0; step < 200; step++)
                    {
                        double cpuTemp = (step % 5) switch
                        {
                            0 => double.NaN,
                            1 => -10.0,
                            2 => 65.0,
                            3 => 92.0,
                            _ => 102.0
                        };
                        double gpuTemp = 75.0 + (step % 15);
                        var d = coordinator.Evaluate(cpuTemp, gpuTemp, 45, 15, 45);
                        decisions.Add(d);
                    }
                });
            }

            await Task.WhenAll(tasks);
            Assert.Equal(2400, decisions.Count);
            Assert.Contains(decisions, d => d.Stage >= 2);
        }

        [Fact]
        public void PawnIoController_ConcurrentMsrAndPowerLimitsStress_NoDeadlocks()
        {
            var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(45, 15, 115, 45, 65, unitsPerWatt: 8, mirrorMmio: true);
            using var pawnIo = new PawnIoController(backend);

            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[16];

            for (int i = 0; i < 16; i++)
            {
                int threadId = i;
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 50; j++)
                    {
                        try
                        {
                            if (threadId % 2 == 0)
                            {
                                int pl1 = 30 + (j % 40);
                                int pl2 = pl1 + 15;
                                var res = pawnIo.ApplyPowerLimits(pl1, pl2);
                                Assert.True(res.IsSuccess);
                            }
                            else
                            {
                                bool ok = pawnIo.ReadPowerUnits(out uint pu, out _);
                                Assert.True(ok);
                                ok = pawnIo.ReadPowerLimits(pu, out int cur1, out int cur2, out _, out _);
                                Assert.True(ok);
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
        public async Task KeyboardLightingController_DisposeDuringApply_NoUnobservedTaskExceptions()
        {
            var caps = new KeyboardLightingCapabilities { Tier = KeyboardLightingTier.FourZone, ZoneCount = 4 };
            var controller = new KeyboardLightingController(null, caps);

            var applyTask = Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    _ = controller.ApplyAsync(KeyboardLightingScope.Zones);
                    await Task.Delay(5);
                }
            });

            await Task.Delay(30);
            controller.Dispose();

            await applyTask;
            // Verify calling ApplyAsync after dispose cleanly returns Idle and doesn't throw ObjectDisposedException
            var status = await controller.ApplyAsync(KeyboardLightingScope.Zones);
            Assert.Equal(KeyboardLightingStatus.Idle, status);
        }
    }
}
