using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Regression coverage for custom CPU power limits (PL1/PL2) being lost when the operating
    /// mode changes: the Acer EC rewrites stock BIOS limits 250ms-1200ms after a mode switch, so
    /// the controller has to invalidate its cache and re-apply across the staggered settle passes.
    /// </summary>
    public class ModeSwitchPowerLimitEnforcementTests
    {
        private const uint UnitsPerWatt = 8;

        private static MockPawnIoBackend CreateBackend()
        {
            var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 10,
                maxWatts: 140,
                pl1Watts: 45,
                pl2Watts: 65,
                unitsPerWatt: UnitsPerWatt,
                locked: false,
                mirrorMmio: true);
            return backend;
        }

        private static AppSettings CreateSettings() => new AppSettings
        {
            PowerMode = (byte)OperatingMode.Balanced,
            EnableCustomPowerLimits = true,
            CpuMinAllowedPowerWatts = 10,
            CpuMaxAllowedPl1Watts = 140,
            CpuMaxAllowedPl2Watts = 140,
            CpuThermalSpecTdpWatts = 55,
            QuietPl1Watts = 25,
            QuietPl2Watts = 35,
            BalancedPl1Watts = 45,
            BalancedPl2Watts = 65,
            PerformancePl1Watts = 75,
            PerformancePl2Watts = 100,
            TurboPl1Watts = 95,
            TurboPl2Watts = 115
        };

        private static (int Pl1, int Pl2) ReadMsrLimits(MockPawnIoBackend backend)
        {
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong msr);
            return ((int)((msr & 0x7FFFUL) / UnitsPerWatt),
                    (int)(((msr >> 32) & 0x7FFFUL) / UnitsPerWatt));
        }

        private static (int Pl1, int Pl2) ReadMmioLimits(MockPawnIoBackend backend)
        {
            backend.ReadMmio32(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, out uint low);
            backend.ReadMmio32(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, out uint high);
            return ((int)((low & 0x7FFF) / UnitsPerWatt),
                    (int)((high & 0x7FFF) / UnitsPerWatt));
        }

        [Fact]
        public void InvalidateAppliedLimitsCache_ResetsLastAppliedWattage()
        {
            using var backend = CreateBackend();
            var settings = CreateSettings();
            using var controller = new CpuPowerLimitController(settings, backend);

            controller.ApplyPowerLimitsForMode(OperatingMode.Balanced, forceHardwareApply: true);
            Assert.Equal((45, 65), controller.LastAppliedLimits);

            controller.InvalidateAppliedLimitsCache();

            Assert.Equal((-1, -1), controller.LastAppliedLimits);
        }

        [Fact]
        public async Task ModeSwitch_ToPerformance_AppliesCustomLimitsAcrossStaggeredPasses()
        {
            using var backend = CreateBackend();
            var settings = CreateSettings();
            using var controller = new CpuPowerLimitController(settings, backend);

            // App startup in Balanced.
            controller.ApplyPowerLimitsForMode(OperatingMode.Balanced, forceHardwareApply: true);
            Assert.Equal((45, 65), ReadMsrLimits(backend));

            int writesBefore = backend.History.Count(h => h.Op == "WriteMsr");

            await controller.ApplyForModeSwitchAsync("Performance", CancellationToken.None);

            int writesAfter = backend.History.Count(h => h.Op == "WriteMsr");

            Assert.True(writesAfter - writesBefore >= 4,
                $"expected the 0/250/600/1200ms passes to write, saw {writesAfter - writesBefore} MSR writes");
            Assert.Equal((75, 100), ReadMsrLimits(backend));
            Assert.Equal((75, 100), ReadMmioLimits(backend));
            Assert.Equal((75, 100), controller.LastAppliedLimits);
            Assert.True(settings.HardwareHasActiveCustomPowerOverride);
        }

        [Fact]
        public async Task ModeSwitch_DelayedEcOverwriteAt150ms_IsCorrectedByLaterSettlePasses()
        {
            using var backend = CreateBackend();
            var settings = CreateSettings();
            using var controller = new CpuPowerLimitController(settings, backend);

            controller.ApplyPowerLimitsForMode(OperatingMode.Balanced, forceHardwareApply: true);

            var switchTask = controller.ApplyForModeSwitchAsync("Performance", CancellationToken.None);

            // The EC finishes loading the Performance thermal table and restores stock BIOS limits.
            await Task.Delay(150);
            backend.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 10,
                maxWatts: 140,
                pl1Watts: 45,
                pl2Watts: 65,
                unitsPerWatt: UnitsPerWatt,
                locked: false,
                mirrorMmio: true);
            Assert.Equal((45, 65), ReadMsrLimits(backend));

            await switchTask;

            Assert.Equal((75, 100), ReadMsrLimits(backend));
            Assert.Equal((75, 100), ReadMmioLimits(backend));
            Assert.Equal((75, 100), controller.LastAppliedLimits);
        }

        [Fact]
        public async Task HardwareModeKeySwitch_QuietToTurbo_WritesPl2BeforePl1InMmio()
        {
            using var backend = CreateBackend();
            var settings = CreateSettings();
            using var controller = new CpuPowerLimitController(settings, backend);

            // Quiet (25W / 35W) is active, so PL2 is 35W when the Mode key is pressed.
            controller.ApplyPowerLimitsForMode(OperatingMode.Quiet, forceHardwareApply: true);
            Assert.Equal((25, 35), ReadMmioLimits(backend));

            int historyStart = backend.History.Count;

            await controller.ApplyForModeSwitchAsync("Turbo", CancellationToken.None);

            var mmioWrites = backend.History.Skip(historyStart)
                .Where(h => h.Op == "WriteMmio32")
                .ToList();

            Assert.True(mmioWrites.Count >= 2, "expected both MMIO dwords to be written");
            // New PL1 (95W) > old PL2 (35W): the PCU rejects PL1 > PL2, so PL2 must go first.
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, mmioWrites[0].Target);
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, mmioWrites[1].Target);

            Assert.Equal((95, 115), ReadMsrLimits(backend));
            Assert.Equal((95, 115), ReadMmioLimits(backend));
        }

        [Fact]
        public async Task ModeSwitch_ToModeWithoutCustomLimits_LeavesStockLimitsAndClearsOverrideFlag()
        {
            using var backend = CreateBackend();
            var settings = CreateSettings();
            settings.QuietPl1Watts = 0;
            settings.QuietPl2Watts = 0;

            using var controller = new CpuPowerLimitController(settings, backend);

            controller.ApplyPowerLimitsForMode(OperatingMode.Balanced, forceHardwareApply: true);
            Assert.True(settings.HardwareHasActiveCustomPowerOverride);

            int writesBefore = backend.History.Count(h => h.Op == "WriteMsr");

            await controller.ApplyForModeSwitchAsync("Quiet", CancellationToken.None);

            Assert.Equal(writesBefore, backend.History.Count(h => h.Op == "WriteMsr"));
            Assert.False(settings.HardwareHasActiveCustomPowerOverride);
        }

        [Fact]
        public async Task RapidModeSwitches_NewestSwitchWins()
        {
            using var backend = CreateBackend();
            var settings = CreateSettings();
            using var controller = new CpuPowerLimitController(settings, backend);

            controller.ApplyPowerLimitsForMode(OperatingMode.Balanced, forceHardwareApply: true);

            var performance = controller.ApplyForModeSwitchAsync("Performance", CancellationToken.None);
            var turbo = controller.ApplyForModeSwitchAsync("Turbo", CancellationToken.None);

            await Task.WhenAll(performance, turbo);

            Assert.Equal((95, 115), ReadMsrLimits(backend));
            Assert.Equal((95, 115), ReadMmioLimits(backend));
        }
    }
}
