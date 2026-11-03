using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("SingleInstanceTests")]
    public class BiosUefiPowerLimitsPersistenceAndLifecycleTests
    {
        private const string TritonFingerprint = "Acer_Predator_PT14-51_V1.08_2024-01-15_Core_Ultra_7_155H";
        private const string HeliosFingerprint = "Acer_Predator_PH18-72_V1.12_2024-04-10_Core_i9-14900HX";

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

        #region 1. AppSettings Properties and Backward Compatibility Aliases

        [Fact]
        public void AppSettings_PropertiesAndAliases_BiDirectionalConsistency()
        {
            var settings = new AppSettings();

            // Hardware identity & bounds
            settings.BiosHardwareFingerprint = TritonFingerprint;
            Assert.Equal(TritonFingerprint, settings.ScannedHardwareSignature);

            settings.ScannedHardwareSignature = HeliosFingerprint;
            Assert.Equal(HeliosFingerprint, settings.BiosHardwareFingerprint);

            settings.HasCompletedBiosPowerScan = true;
            Assert.True(settings.HasScannedHardwarePowerBounds);

            settings.HasScannedHardwarePowerBounds = false;
            Assert.False(settings.HasCompletedBiosPowerScan);

            settings.CpuThermalSpecTdpWatts = 55;
            Assert.Equal(55, settings.ScannedCpuTdpWatts);

            settings.CpuMinAllowedPowerWatts = 15;
            Assert.Equal(15, settings.ScannedMinPowerWatts);

            settings.CpuMaxAllowedPl1Watts = 125;
            Assert.Equal(125, settings.ScannedMaxPl1Watts);

            settings.CpuMaxAllowedPl2Watts = 175;
            Assert.Equal(175, settings.ScannedMaxPl2Watts);

            // Per-mode BIOS scanned defaults
            settings.BiosDefaultEcoPl1Watts = 20;
            settings.BiosDefaultEcoPl2Watts = 30;
            Assert.Equal(20, settings.DefaultEcoPl1Watts);
            Assert.Equal(30, settings.DefaultEcoPl2Watts);

            settings.DefaultQuietPl1Watts = 35;
            settings.DefaultQuietPl2Watts = 50;
            Assert.Equal(35, settings.BiosDefaultQuietPl1Watts);
            Assert.Equal(50, settings.BiosDefaultQuietPl2Watts);

            settings.BiosDefaultBalancedPl1Watts = 55;
            settings.BiosDefaultBalancedPl2Watts = 95;
            Assert.Equal(55, settings.DefaultBalancedPl1Watts);
            Assert.Equal(95, settings.DefaultBalancedPl2Watts);

            settings.BiosDefaultPerformancePl1Watts = 95;
            settings.BiosDefaultPerformancePl2Watts = 140;
            Assert.Equal(95, settings.DefaultPerformancePl1Watts);
            Assert.Equal(140, settings.DefaultPerformancePl2Watts);

            settings.BiosDefaultTurboPl1Watts = 125;
            settings.BiosDefaultTurboPl2Watts = 175;
            Assert.Equal(125, settings.DefaultTurboPl1Watts);
            Assert.Equal(175, settings.DefaultTurboPl2Watts);

            // User-configured custom limits
            settings.CustomEcoPl1Watts = 25;
            settings.CustomEcoPl2Watts = 35;
            Assert.Equal(25, settings.EcoPl1Watts);
            Assert.Equal(35, settings.EcoPl2Watts);

            settings.QuietPl1Watts = 40;
            settings.QuietPl2Watts = 55;
            Assert.Equal(40, settings.CustomQuietPl1Watts);
            Assert.Equal(55, settings.CustomQuietPl2Watts);

            settings.CustomBalancedPl1Watts = 60;
            settings.CustomBalancedPl2Watts = 100;
            Assert.Equal(60, settings.BalancedPl1Watts);
            Assert.Equal(100, settings.BalancedPl2Watts);

            settings.PerformancePl1Watts = 105;
            settings.PerformancePl2Watts = 150;
            Assert.Equal(105, settings.CustomPerformancePl1Watts);
            Assert.Equal(150, settings.CustomPerformancePl2Watts);

            settings.CustomTurboPl1Watts = 135;
            settings.CustomTurboPl2Watts = 180;
            Assert.Equal(135, settings.TurboPl1Watts);
            Assert.Equal(180, settings.TurboPl2Watts);
        }

        #endregion

        #region 2. AppSettings Helper Methods

        [Fact]
        public void AppSettings_GetBiosDefaultLimits_ReturnsVerifiedStatusAndValues()
        {
            var settings = new AppSettings
            {
                CpuThermalSpecTdpWatts = 45,
                CpuMinAllowedPowerWatts = 10,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140
            };

            // Unverified mode returns proportional estimate with IsVerifiedBiosScan == false
            var (unverifiedPl1, unverifiedPl2, isVerified) = settings.GetBiosDefaultLimitsForMode(OperatingMode.Quiet);
            Assert.False(isVerified);
            Assert.True(unverifiedPl1 > 0);
            Assert.True(unverifiedPl2 >= unverifiedPl1);
            Assert.False(settings.HasVerifiedBiosDefaultForMode(OperatingMode.Quiet));

            // Record verified BIOS default
            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Quiet, 32, 48, isVerifiedBiosScan: true);

            var (defPl1, defPl2, isNowVerified) = settings.GetBiosDefaultLimitsForMode(OperatingMode.Quiet);
            Assert.True(isNowVerified);
            Assert.Equal(32, defPl1);
            Assert.Equal(48, defPl2);
            Assert.True(settings.HasVerifiedBiosDefaultForMode(OperatingMode.Quiet));
        }

        [Fact]
        public void AppSettings_SetCustomLimitsForMode_EnforcesPL2GtePL1_AndClampsToBounds()
        {
            var settings = new AppSettings
            {
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 125,
                CpuMaxAllowedPl2Watts = 175
            };

            // Case A: Input below minimum allowed power -> clamped to Min
            settings.SetCustomLimitsForMode(OperatingMode.Eco, 5, 10);
            Assert.Equal(15, settings.CustomEcoPl1Watts);
            Assert.Equal(15, settings.CustomEcoPl2Watts); // PL2 clamped to >= PL1

            // Case B: PL2 < PL1 input -> PL2 raised to at least PL1
            settings.SetCustomLimitsForMode(OperatingMode.Balanced, 60, 40);
            Assert.Equal(60, settings.CustomBalancedPl1Watts);
            Assert.Equal(60, settings.CustomBalancedPl2Watts);

            // Case C: Input above maximum allowed bounds -> clamped to Max bounds
            settings.SetCustomLimitsForMode(OperatingMode.Turbo, 250, 300);
            Assert.Equal(125, settings.CustomTurboPl1Watts); // clamped to MaxPl1
            Assert.Equal(175, settings.CustomTurboPl2Watts); // clamped to MaxPl2
        }

        [Fact]
        public void AppSettings_ResetModeToBiosDefault_RestoresEffectiveToScannedDefault()
        {
            var settings = new AppSettings
            {
                CpuThermalSpecTdpWatts = 55,
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 125,
                CpuMaxAllowedPl2Watts = 175
            };

            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Performance, 85, 120, isVerifiedBiosScan: true);
            settings.SetCustomLimitsForMode(OperatingMode.Performance, 95, 135);

            var (effCustom1, effCustom2) = settings.GetEffectiveLimitsForMode(OperatingMode.Performance);
            Assert.Equal(95, effCustom1);
            Assert.Equal(135, effCustom2);

            // Reset mode
            settings.ResetModeToBiosDefault(OperatingMode.Performance);
            Assert.Equal(-1, settings.CustomPerformancePl1Watts);
            Assert.Equal(-1, settings.CustomPerformancePl2Watts);

            var (effStock1, effStock2) = settings.GetEffectiveLimitsForMode(OperatingMode.Performance);
            Assert.Equal(85, effStock1);
            Assert.Equal(120, effStock2);
        }

        [Fact]
        public void AppSettings_InvalidateIfBiosFingerprintChanged_InvalidatesStaleData()
        {
            var settings = new AppSettings
            {
                BiosHardwareFingerprint = TritonFingerprint,
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 28,
                CpuMinAllowedPowerWatts = 8,
                CpuMaxAllowedPl1Watts = 65,
                CpuMaxAllowedPl2Watts = 115,
                CustomBalancedPl1Watts = 35,
                CustomBalancedPl2Watts = 55
            };

            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Balanced, 28, 45, isVerifiedBiosScan: true);

            // Same fingerprint -> no-op
            settings.InvalidateIfBiosFingerprintChanged(TritonFingerprint);
            Assert.True(settings.HasCompletedBiosPowerScan);
            Assert.Equal(35, settings.CustomBalancedPl1Watts);

            // Changed fingerprint -> invalidate
            settings.InvalidateIfBiosFingerprintChanged(HeliosFingerprint);
            Assert.Equal(HeliosFingerprint, settings.BiosHardwareFingerprint);
            Assert.False(settings.HasCompletedBiosPowerScan);
            Assert.Equal(0, settings.VerifiedBiosModesBitmask);
            Assert.Equal(0, settings.CpuThermalSpecTdpWatts);
            Assert.Equal(-1, settings.CustomBalancedPl1Watts);
            Assert.Equal(-1, settings.BiosDefaultBalancedPl1Watts);
        }

        #endregion

        #region 3. Registry Safety Roundtrip

        [Fact]
        public void RegistrySafety_SaveCpuPowerLimits_And_LoadCpuPowerLimits_RoundtripsSuccessfully()
        {
            var settings = new AppSettings
            {
                EnableCustomPowerLimits = true,
                BiosHardwareFingerprint = HeliosFingerprint,
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 55,
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 140,
                CpuMaxAllowedPl2Watts = 190,
                VerifiedBiosModesBitmask = 0x1F,

                BiosDefaultEcoPl1Watts = 25,
                BiosDefaultEcoPl2Watts = 35,
                BiosDefaultQuietPl1Watts = 40,
                BiosDefaultQuietPl2Watts = 60,
                BiosDefaultBalancedPl1Watts = 55,
                BiosDefaultBalancedPl2Watts = 95,
                BiosDefaultPerformancePl1Watts = 95,
                BiosDefaultPerformancePl2Watts = 140,
                BiosDefaultTurboPl1Watts = 125,
                BiosDefaultTurboPl2Watts = 175,

                CustomEcoPl1Watts = 28,
                CustomEcoPl2Watts = 38,
                CustomQuietPl1Watts = 45,
                CustomQuietPl2Watts = 65,
                CustomBalancedPl1Watts = 60,
                CustomBalancedPl2Watts = 100,
                CustomPerformancePl1Watts = 105,
                CustomPerformancePl2Watts = 150,
                CustomTurboPl1Watts = 130,
                CustomTurboPl2Watts = 180
            };

            lock (RegistrySafety.RegistryLock)
            {
                RegistrySafety.SaveCpuPowerLimits(settings);

                var loaded = new AppSettings();
                RegistrySafety.LoadCpuPowerLimits(loaded);

            Assert.True(loaded.EnableCustomPowerLimits);
            Assert.Equal(HeliosFingerprint, loaded.BiosHardwareFingerprint);
            Assert.True(loaded.HasCompletedBiosPowerScan);
            Assert.Equal(55, loaded.CpuThermalSpecTdpWatts);
            Assert.Equal(15, loaded.CpuMinAllowedPowerWatts);
            Assert.Equal(140, loaded.CpuMaxAllowedPl1Watts);
            Assert.Equal(190, loaded.CpuMaxAllowedPl2Watts);
            Assert.Equal(0x1F, loaded.VerifiedBiosModesBitmask);

            Assert.Equal(25, loaded.BiosDefaultEcoPl1Watts);
            Assert.Equal(35, loaded.BiosDefaultEcoPl2Watts);
            Assert.Equal(55, loaded.BiosDefaultBalancedPl1Watts);
            Assert.Equal(95, loaded.BiosDefaultBalancedPl2Watts);
            Assert.Equal(125, loaded.BiosDefaultTurboPl1Watts);
            Assert.Equal(175, loaded.BiosDefaultTurboPl2Watts);

            Assert.Equal(28, loaded.CustomEcoPl1Watts);
            Assert.Equal(38, loaded.CustomEcoPl2Watts);
            Assert.Equal(60, loaded.CustomBalancedPl1Watts);
            Assert.Equal(100, loaded.CustomBalancedPl2Watts);
            Assert.Equal(130, loaded.CustomTurboPl1Watts);
            Assert.Equal(180, loaded.CustomTurboPl2Watts);
            }
        }

        #endregion

        #region 4. CpuPowerLimitController & IBiosUefiPowerScanner Lifecycle

        [Fact]
        public void CpuPowerLimitController_DependencyInjection_WithMockScannerProfiles()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateHelios18Hx55WProfile();
            using var backend = CreateHeliosBackend();
            var settings = new AppSettings();

            using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);
            Assert.Same(mockScanner, controller.BiosScanner);

            var scanResult = controller.InitializeWithBiosScanner(mockScanner);
            Assert.True(scanResult.HasCompletedBiosPowerScan);
            Assert.Equal(55, settings.CpuThermalSpecTdpWatts);
            Assert.Equal(15, settings.CpuMinAllowedPowerWatts);
            Assert.Equal(175, settings.CpuMaxAllowedPl1Watts);
            Assert.Equal(190, settings.CpuMaxAllowedPl2Watts);

            Assert.True(settings.HasVerifiedBiosDefaultForMode(OperatingMode.Balanced));
            Assert.Equal((55, 95), settings.GetDefaultPowerLimitsForMode(OperatingMode.Balanced));
            Assert.Equal((125, 175), settings.GetDefaultPowerLimitsForMode(OperatingMode.Turbo));
        }

        [Fact]
        public async Task CpuPowerLimitController_PreOverrideScan_SettleDelay_AndHardwareWriteRules()
        {
            using var backend = CreateTritonBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = TritonFingerprint,
                EnableCustomPowerLimits = true,
                CustomQuietPl1Watts = 22,
                CustomQuietPl2Watts = 33
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            controller.InitializeHardwareFingerprintAndBaseline(TritonFingerprint);

            // Settle delay mode switch to Quiet mode with native MSR set to 20W / 30W
            backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT,
                (ulong)(20 * 8) | (1UL << 15) | (1UL << 16) | ((ulong)(30 * 8) << 32) | (1UL << 47) | (1UL << 48));

            await controller.OnPowerModeChanged(OperatingMode.Quiet, settleDelayMs: 10);

            // Verified stock default captured from live MSR
            Assert.True(settings.HasVerifiedBiosDefaultForMode(OperatingMode.Quiet));
            var (def1, def2, isVerified) = settings.GetBiosDefaultLimitsForMode(OperatingMode.Quiet);
            Assert.True(isVerified);
            Assert.Equal(20, def1);
            Assert.Equal(30, def2);

            // Custom limits applied to hardware because EnableCustomPowerLimits == true
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong msr);
            int pl1 = (int)((msr & 0x7FFFUL) / 8);
            int pl2 = (int)(((msr >> 32) & 0x7FFFUL) / 8);
            Assert.Equal(22, pl1);
            Assert.Equal(33, pl2);
        }

        [Fact]
        public void CpuPowerLimitController_ResetModeToStockDefaults_RestoresStockAndWritesHardware()
        {
            using var backend = CreateTritonBackend();
            var settings = new AppSettings
            {
                ScannedHardwareSignature = TritonFingerprint,
                EnableCustomPowerLimits = true,
                PowerMode = (byte)OperatingMode.Balanced
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            controller.InitializeHardwareFingerprintAndBaseline(TritonFingerprint);

            // Record stock baseline for Balanced mode
            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Balanced, 28, 45, isVerifiedBiosScan: true);

            // Set user custom limits
            controller.SetPowerLimitsForMode(OperatingMode.Balanced, 35, 55);
            var (eff1, eff2, isCustom) = controller.GetDisplayPowerLimits(OperatingMode.Balanced);
            Assert.Equal(35, eff1);
            Assert.Equal(55, eff2);
            Assert.True(isCustom);

            // Reset mode to stock defaults
            var resetResult = controller.ResetModeToStockDefaults(OperatingMode.Balanced);
            Assert.True(resetResult.IsSuccess);

            // Custom limits reset in settings and display reports stock baseline
            var (rest1, rest2, isStillCustom) = controller.GetDisplayPowerLimits(OperatingMode.Balanced);
            Assert.False(isStillCustom);
            Assert.Equal(28, rest1);
            Assert.Equal(45, rest2);

            // Hardware MSR written back to stock baseline
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong msr);
            int hwPl1 = (int)((msr & 0x7FFFUL) / 8);
            int hwPl2 = (int)(((msr >> 32) & 0x7FFFUL) / 8);
            Assert.Equal(28, hwPl1);
            Assert.Equal(45, hwPl2);
        }

        #endregion
    }
}
