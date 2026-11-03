using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class CpuPowerLimitControllerTests
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

        [Fact]
        public void ZeroAssumptionRule_OnStartup_DoesNotWriteToHardware_WhenCustomLimitsDisabled()
        {
            using var backend = CreateTritonBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = TritonSignature,
                EnableCustomPowerLimits = false,
                BalancedPl1Watts = 35,
                BalancedPl2Watts = 55
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            controller.InitializeHardwareFingerprintAndBaseline(TritonSignature);

            // Hardware scan succeeded
            Assert.True(controller.Capabilities.IsSupported);
            Assert.Equal(8, settings.ScannedMinPowerWatts);
            Assert.Equal(65, settings.ScannedMaxPl2Watts);

            // Zero-assumption rule: No WriteMsr operations dispatched during startup!
            var writeOps = backend.History.Where(h => h.Op.StartsWith("WriteMsr")).ToList();
            Assert.Empty(writeOps);
        }

        [Fact]
        public void ZeroAssumptionRule_OnStartup_AppliesLimits_WhenCustomLimitsEnabled()
        {
            using var backend = CreateTritonBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = TritonSignature,
                EnableCustomPowerLimits = true,
                PowerMode = (byte)OperatingMode.Balanced,
                BalancedPl1Watts = 35,
                BalancedPl2Watts = 55
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            controller.InitializeHardwareFingerprintAndBaseline(TritonSignature);

            // Custom power limits were enabled, so WriteMsr should have been dispatched with 35W / 55W
            var writeOps = backend.History.Where(h => h.Op.StartsWith("WriteMsr")).ToList();
            Assert.NotEmpty(writeOps);

            // Verify hardware state
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong msr);
            uint units = 8;
            int pl1 = (int)((msr & 0x7FFFUL) / units);
            int pl2 = (int)(((msr >> 32) & 0x7FFFUL) / units);
            Assert.Equal(35, pl1);
            Assert.Equal(55, pl2);
        }

        [Fact]
        public void HardwareSignatureChange_InvalidatesStaleOverridesAndScansNewBounds()
        {
            using var tritonBackend = CreateTritonBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = TritonSignature,
                HasScannedHardwarePowerBounds = true,
                ScannedMinPowerWatts = 8,
                ScannedMaxPl1Watts = 55,
                ScannedMaxPl2Watts = 65,
                ScannedCpuTdpWatts = 28,
                BalancedPl1Watts = 30,
                BalancedPl2Watts = 45
            };

            // Motherboard / CPU upgraded to Helios 18 HX
            using var heliosBackend = CreateHeliosBackend();
            using var controller = new CpuPowerLimitController(settings, heliosBackend);

            controller.InitializeHardwareFingerprintAndBaseline(HeliosSignature);

            // Stale Triton overrides reset
            Assert.Equal(-1, settings.BalancedPl1Watts);
            Assert.Equal(-1, settings.BalancedPl2Watts);
            Assert.Equal(HeliosSignature, settings.ScannedHardwareSignature);

            // New Helios bounds scanned
            Assert.Equal(15, settings.ScannedMinPowerWatts);
            Assert.Equal(175, settings.ScannedMaxPl2Watts);
            Assert.Equal(55, settings.ScannedCpuTdpWatts);
        }

        [Fact]
        public async Task CaptureStockModeBaseline_CapturesStockMSR_AndAppliesCustomLimitsIfEnabled()
        {
            using var backend = CreateTritonBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = TritonSignature,
                EnableCustomPowerLimits = true,
                TurboPl1Watts = 40,
                TurboPl2Watts = 60
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            controller.InitializeHardwareFingerprintAndBaseline(TritonSignature);

            // Simulate EC switching to Turbo stock limits: 45W PL1, 65W PL2
            backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT,
                (ulong)(45 * 8) | (1UL << 15) | (1UL << 16) | ((ulong)(65 * 8) << 32) | (1UL << 47) | (1UL << 48));

            // Settle delay = 0 for instant test execution
            bool captured = await controller.CaptureStockModeBaselineIfNeededAsync(OperatingMode.Turbo, settleDelayMs: 0);

            Assert.True(captured);
            // Stock baseline recorded
            Assert.Equal((45, 65), settings.GetDefaultPowerLimitsForMode(OperatingMode.Turbo));

            // Custom limits applied since EnableCustomPowerLimits == true
            var (dispPl1, dispPl2, isCustom) = controller.GetDisplayPowerLimits(OperatingMode.Turbo);
            Assert.Equal(40, dispPl1);
            Assert.Equal(60, dispPl2);
            Assert.True(isCustom);
        }

        [Fact]
        public void ResetModeToStockDefaults_RestoresSingleModeStockBaseline()
        {
            using var backend = CreateTritonBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = TritonSignature,
                EnableCustomPowerLimits = true,
                PowerMode = (byte)OperatingMode.Performance
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            controller.InitializeHardwareFingerprintAndBaseline(TritonSignature);

            // Record baseline
            settings.RecordScannedDefaultsForMode(OperatingMode.Performance, 35, 50);

            // User customized Performance limits
            controller.SetPowerLimitsForMode(OperatingMode.Performance, 40, 58);
            var (customPl1, customPl2, isCustom) = controller.GetDisplayPowerLimits(OperatingMode.Performance);
            Assert.Equal(40, customPl1);
            Assert.Equal(58, customPl2);
            Assert.True(isCustom);

            // Reset Performance mode
            var resetResult = controller.ResetModeToStockDefaults(OperatingMode.Performance);
            Assert.True(resetResult.IsSuccess);

            var (restoredPl1, restoredPl2, isStillCustom) = controller.GetDisplayPowerLimits(OperatingMode.Performance);
            Assert.Equal(35, restoredPl1);
            Assert.Equal(50, restoredPl2);
            Assert.False(isStillCustom);
        }

        [Fact]
        public void PeriodicVerifyPowerLimits_DetectsEcResetAndRestoresUserLimits()
        {
            using var backend = CreateTritonBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = TritonSignature,
                EnableCustomPowerLimits = true,
                PowerMode = (byte)OperatingMode.Balanced,
                BalancedPl1Watts = 32,
                BalancedPl2Watts = 50
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            controller.InitializeHardwareFingerprintAndBaseline(TritonSignature);

            // Hardware currently has 32W / 50W
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong initialMsr);
            uint units = 8;
            Assert.Equal(32, (int)((initialMsr & 0x7FFFUL) / units));

            // Simulate Acer EC resetting MSR 0x610 back to 28W / 45W behind the OS's back
            backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT,
                (ulong)(28 * 8) | (1UL << 15) | (1UL << 16) | ((ulong)(45 * 8) << 32) | (1UL << 47) | (1UL << 48));

            // Telemetry loop runs PeriodicVerifyPowerLimits
            controller.PeriodicVerifyPowerLimits(OperatingMode.Balanced);

            // Controller must have detected mismatch and re-applied 32W / 50W
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong restoredMsr);
            int restoredPl1 = (int)((restoredMsr & 0x7FFFUL) / units);
            int restoredPl2 = (int)(((restoredMsr >> 32) & 0x7FFFUL) / units);
            Assert.Equal(32, restoredPl1);
            Assert.Equal(50, restoredPl2);
        }

        [Fact]
        public void ProportionalDisplayEstimation_WorksAcrossAllFiveModes()
        {
            var settings = new AppSettings
            {
                ScannedCpuTdpWatts = 45,
                ScannedMinPowerWatts = 10,
                ScannedMaxPl1Watts = 115,
                ScannedMaxPl2Watts = 140
            };

            using var backend = new MockPawnIoBackend();
            using var controller = new CpuPowerLimitController(settings, backend);

            // Verify each operating mode gets a distinct estimated limit
            var eco = controller.GetDisplayPowerLimits(OperatingMode.Eco);
            var quiet = controller.GetDisplayPowerLimits(OperatingMode.Quiet);
            var balanced = controller.GetDisplayPowerLimits(OperatingMode.Balanced);
            var perf = controller.GetDisplayPowerLimits(OperatingMode.Performance);
            var turbo = controller.GetDisplayPowerLimits(OperatingMode.Turbo);

            Assert.True(eco.pl1 < quiet.pl1);
            Assert.True(quiet.pl1 <= balanced.pl1);
            Assert.True(balanced.pl1 <= perf.pl1);
            Assert.True(perf.pl1 <= turbo.pl1);

            Assert.True(eco.pl2 >= eco.pl1);
            Assert.True(quiet.pl2 >= quiet.pl1);
            Assert.True(balanced.pl2 >= balanced.pl1);
            Assert.True(perf.pl2 >= perf.pl1);
            Assert.True(turbo.pl2 >= turbo.pl1);
        }
    }
}
