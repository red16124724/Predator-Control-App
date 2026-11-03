using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Exhaustive regression test suite verifying equivalence, non-destructive behavior,
    /// and UI integrity between Startup scanning and subsequent Re-Scan operations.
    /// </summary>
    [Collection("NoParallel")]
    public class StartupVsReScanPowerLimitEquivalenceTests
    {
        private const string TritonFingerprint = "Acer_Predator_PT14-51_V1.08_2024-01-15_Core_Ultra_7_155H";
        private const string HeliosNeoFingerprint = "Acer_Predator_PHN16-71_V1.15_2024-03-12_Core_i7-13700HX";
        private const string Helios18Fingerprint = "Acer_Predator_PH18-72_V1.05_2024-04-10_Core_i9-14900HX";

        private static readonly OperatingMode[] AllModes = new[]
        {
            OperatingMode.Eco,
            OperatingMode.Quiet,
            OperatingMode.Balanced,
            OperatingMode.Performance,
            OperatingMode.Turbo
        };

        private static readonly byte[] AllModeBytes = new byte[]
        {
            0x06, // Eco
            0x00, // Quiet
            0x01, // Balanced
            0x04, // Performance
            0x05  // Turbo
        };

        private static void RunInStaThread(Action action)
        {
            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (caught != null)
            {
                throw new AggregateException("STA thread test failure", caught);
            }
        }

        #region 1. Test StartupVsReScanEquivalence

        [Fact]
        public void StartupVsReScanEquivalence_Triton28W_WithCustomLimitsAndDirtyMsr_RetainsIdenticalDefaultsAndBounds()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateTriton28WProfile();
            using var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(
                tdpWatts: 28,
                minWatts: 8,
                maxWatts: 65,
                pl1Watts: 28,
                pl2Watts: 45,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true
            );

            var settings = new AppSettings();
            using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);

            // 1. Simulate laptop startup scan
            var startupResult = controller.InitializeWithBiosScanner(mockScanner);
            Assert.True(startupResult.HasCompletedBiosPowerScan);

            // Record baseline snapshot
            int baselineMin = settings.CpuMinAllowedPowerWatts;
            int baselineMax1 = settings.CpuMaxAllowedPl1Watts;
            int baselineMax2 = settings.CpuMaxAllowedPl2Watts;
            int baselineTdp = settings.CpuThermalSpecTdpWatts;

            var baselineDefaults = new Dictionary<OperatingMode, (int Pl1, int Pl2)>();
            foreach (var mode in AllModes)
            {
                baselineDefaults[mode] = settings.GetRawScannedDefaultsForMode(mode);
            }

            Assert.Equal(8, baselineMin);
            Assert.Equal(65, baselineMax1);
            Assert.Equal(115, baselineMax2);
            Assert.Equal(28, baselineTdp);
            Assert.Equal((15, 20), baselineDefaults[OperatingMode.Eco]);
            Assert.Equal((20, 30), baselineDefaults[OperatingMode.Quiet]);
            Assert.Equal((28, 45), baselineDefaults[OperatingMode.Balanced]);
            Assert.Equal((45, 65), baselineDefaults[OperatingMode.Performance]);
            Assert.Equal((65, 115), baselineDefaults[OperatingMode.Turbo]);

            // 2. Apply custom PL1/PL2 limits & dirty MSR 0x610
            settings.EnableCustomPowerLimits = true;
            controller.SetPowerLimitsForMode(OperatingMode.Balanced, 22, 35);
            controller.SetPowerLimitsForMode(OperatingMode.Performance, 50, 70);

            // Dirty live MSR 0x610
            backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT,
                (ulong)(33 * 8) | (1UL << 15) | (1UL << 16) | ((ulong)(55 * 8) << 32) | (1UL << 47) | (1UL << 48));

            // Switch power modes
            settings.PowerMode = (byte)OperatingMode.Performance;

            // 3. Invoke ReScan multiple times consecutively
            for (int scanIter = 1; scanIter <= 5; scanIter++)
            {
                var rescanResult = controller.InitializeWithBiosScanner(mockScanner);
                Assert.True(rescanResult.HasCompletedBiosPowerScan);

                // 4. Assert that CPU bounds and all 5 modes' BiosDefault* limits remain 100% IDENTICAL before and after ReScan
                Assert.Equal(baselineMin, settings.CpuMinAllowedPowerWatts);
                Assert.Equal(baselineMax1, settings.CpuMaxAllowedPl1Watts);
                Assert.Equal(baselineMax2, settings.CpuMaxAllowedPl2Watts);
                Assert.Equal(baselineTdp, settings.CpuThermalSpecTdpWatts);

                Assert.Equal(baselineDefaults[OperatingMode.Eco], settings.GetRawScannedDefaultsForMode(OperatingMode.Eco));
                Assert.Equal(baselineDefaults[OperatingMode.Quiet], settings.GetRawScannedDefaultsForMode(OperatingMode.Quiet));
                Assert.Equal(baselineDefaults[OperatingMode.Balanced], settings.GetRawScannedDefaultsForMode(OperatingMode.Balanced));
                Assert.Equal(baselineDefaults[OperatingMode.Performance], settings.GetRawScannedDefaultsForMode(OperatingMode.Performance));
                Assert.Equal(baselineDefaults[OperatingMode.Turbo], settings.GetRawScannedDefaultsForMode(OperatingMode.Turbo));

                // Verify user custom limits were not corrupted by ReScan
                Assert.Equal((22, 35), settings.GetUserPowerLimitsForMode(OperatingMode.Balanced));
                Assert.Equal((50, 70), settings.GetUserPowerLimitsForMode(OperatingMode.Performance));
            }
        }

        [Fact]
        public void StartupVsReScanEquivalence_HeliosNeo16_45W_WithCustomLimitsAndDirtyMsr_RetainsIdenticalDefaultsAndBounds()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
            using var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(
                tdpWatts: 45,
                minWatts: 12,
                maxWatts: 140,
                pl1Watts: 45,
                pl2Watts: 75,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true
            );

            var settings = new AppSettings();
            using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);

            // 1. Startup scan
            var startupResult = controller.InitializeWithBiosScanner(mockScanner);
            Assert.True(startupResult.HasCompletedBiosPowerScan);

            // Record baseline
            int baselineMin = settings.CpuMinAllowedPowerWatts;
            int baselineMax1 = settings.CpuMaxAllowedPl1Watts;
            int baselineMax2 = settings.CpuMaxAllowedPl2Watts;
            int baselineTdp = settings.CpuThermalSpecTdpWatts;

            var baselineDefaults = new Dictionary<OperatingMode, (int Pl1, int Pl2)>();
            foreach (var mode in AllModes)
            {
                baselineDefaults[mode] = settings.GetRawScannedDefaultsForMode(mode);
            }

            Assert.Equal(12, baselineMin);
            Assert.Equal(115, baselineMax1);
            Assert.Equal(140, baselineMax2);
            Assert.Equal(45, baselineTdp);
            Assert.Equal((25, 35), baselineDefaults[OperatingMode.Eco]);
            Assert.Equal((35, 45), baselineDefaults[OperatingMode.Quiet]);
            Assert.Equal((45, 75), baselineDefaults[OperatingMode.Balanced]);
            Assert.Equal((75, 105), baselineDefaults[OperatingMode.Performance]);
            Assert.Equal((105, 140), baselineDefaults[OperatingMode.Turbo]);

            // 2. Apply custom limits PL1 = 30W, PL2 = 50W (as specified in prompt requirement)
            settings.EnableCustomPowerLimits = true;
            controller.SetPowerLimitsForMode(OperatingMode.Balanced, 30, 50);

            // Dirty live MSR 0x610
            backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT,
                (ulong)(30 * 8) | (1UL << 15) | (1UL << 16) | ((ulong)(50 * 8) << 32) | (1UL << 47) | (1UL << 48));

            // Switch mode to Turbo
            settings.PowerMode = (byte)OperatingMode.Turbo;

            // 3. Invoke ReScan multiple times consecutively
            for (int i = 0; i < 5; i++)
            {
                var rescanResult = controller.InitializeWithBiosScanner(mockScanner);
                Assert.True(rescanResult.HasCompletedBiosPowerScan);

                // 4. Bounds & defaults remain 100% IDENTICAL
                Assert.Equal(baselineMin, settings.CpuMinAllowedPowerWatts);
                Assert.Equal(baselineMax1, settings.CpuMaxAllowedPl1Watts);
                Assert.Equal(baselineMax2, settings.CpuMaxAllowedPl2Watts);
                Assert.Equal(baselineTdp, settings.CpuThermalSpecTdpWatts);

                Assert.Equal(baselineDefaults[OperatingMode.Eco], settings.GetRawScannedDefaultsForMode(OperatingMode.Eco));
                Assert.Equal(baselineDefaults[OperatingMode.Quiet], settings.GetRawScannedDefaultsForMode(OperatingMode.Quiet));
                Assert.Equal(baselineDefaults[OperatingMode.Balanced], settings.GetRawScannedDefaultsForMode(OperatingMode.Balanced));
                Assert.Equal(baselineDefaults[OperatingMode.Performance], settings.GetRawScannedDefaultsForMode(OperatingMode.Performance));
                Assert.Equal(baselineDefaults[OperatingMode.Turbo], settings.GetRawScannedDefaultsForMode(OperatingMode.Turbo));

                Assert.Equal((30, 50), settings.GetUserPowerLimitsForMode(OperatingMode.Balanced));
            }
        }

        [Fact]
        public void StartupVsReScanEquivalence_Helios18Hx55W_WithCustomLimitsAndDirtyMsr_RetainsIdenticalDefaultsAndBounds()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateHelios18Hx55WProfile();
            using var backend = new MockPawnIoBackend();
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

            var settings = new AppSettings();
            using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);

            // 1. Startup scan
            var startupResult = controller.InitializeWithBiosScanner(mockScanner);
            Assert.True(startupResult.HasCompletedBiosPowerScan);

            int baselineMin = settings.CpuMinAllowedPowerWatts;
            int baselineMax1 = settings.CpuMaxAllowedPl1Watts;
            int baselineMax2 = settings.CpuMaxAllowedPl2Watts;
            int baselineTdp = settings.CpuThermalSpecTdpWatts;

            var baselineDefaults = new Dictionary<OperatingMode, (int Pl1, int Pl2)>();
            foreach (var mode in AllModes)
            {
                baselineDefaults[mode] = settings.GetRawScannedDefaultsForMode(mode);
            }

            Assert.Equal(15, baselineMin);
            Assert.Equal(175, baselineMax1);
            Assert.Equal(190, baselineMax2);
            Assert.Equal(55, baselineTdp);
            Assert.Equal((35, 45), baselineDefaults[OperatingMode.Eco]);
            Assert.Equal((45, 65), baselineDefaults[OperatingMode.Quiet]);
            Assert.Equal((55, 95), baselineDefaults[OperatingMode.Balanced]);
            Assert.Equal((115, 140), baselineDefaults[OperatingMode.Performance]);
            Assert.Equal((125, 175), baselineDefaults[OperatingMode.Turbo]);

            // 2. Custom limits & dirty MSR
            settings.EnableCustomPowerLimits = true;
            controller.SetPowerLimitsForMode(OperatingMode.Balanced, 60, 90);
            controller.SetPowerLimitsForMode(OperatingMode.Turbo, 140, 185);

            backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT,
                (ulong)(60 * 8) | (1UL << 15) | (1UL << 16) | ((ulong)(90 * 8) << 32) | (1UL << 47) | (1UL << 48));

            // 3. Consecutive ReScans
            for (int i = 0; i < 5; i++)
            {
                var rescanResult = controller.InitializeWithBiosScanner(mockScanner);
                Assert.True(rescanResult.HasCompletedBiosPowerScan);

                // 4. Assert 100% equivalence
                Assert.Equal(baselineMin, settings.CpuMinAllowedPowerWatts);
                Assert.Equal(baselineMax1, settings.CpuMaxAllowedPl1Watts);
                Assert.Equal(baselineMax2, settings.CpuMaxAllowedPl2Watts);
                Assert.Equal(baselineTdp, settings.CpuThermalSpecTdpWatts);

                Assert.Equal(baselineDefaults[OperatingMode.Eco], settings.GetRawScannedDefaultsForMode(OperatingMode.Eco));
                Assert.Equal(baselineDefaults[OperatingMode.Quiet], settings.GetRawScannedDefaultsForMode(OperatingMode.Quiet));
                Assert.Equal(baselineDefaults[OperatingMode.Balanced], settings.GetRawScannedDefaultsForMode(OperatingMode.Balanced));
                Assert.Equal(baselineDefaults[OperatingMode.Performance], settings.GetRawScannedDefaultsForMode(OperatingMode.Performance));
                Assert.Equal(baselineDefaults[OperatingMode.Turbo], settings.GetRawScannedDefaultsForMode(OperatingMode.Turbo));

                Assert.Equal((60, 90), settings.GetUserPowerLimitsForMode(OperatingMode.Balanced));
                Assert.Equal((140, 185), settings.GetUserPowerLimitsForMode(OperatingMode.Turbo));
            }
        }

        [Fact]
        public void StartupVsReScanEquivalence_Form1Integration_ConsecutiveReScans_RetainsIdenticalBaseline()
        {
            RunInStaThread(() =>
            {
                var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
                var scanResult = mockScanner.ScanPowerLimits();
                using var backend = new MockPawnIoBackend();
                backend.ConfigureIntelBaseline(45, 12, 140, 45, 75, 8);

                using var form = new Form1();
                var settings = form.Settings;
                using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);
                CpuPowerLimitController.Instance = controller;
                controller.InitializeWithBiosScanner(mockScanner);

                // Establish verified startup DeviceCapabilities baseline in Form1
                var caps = new DeviceCapabilities
                {
                    CpuTdpWatts = scanResult.CpuThermalSpecTdpWatts,
                    ScannedMinPowerWatts = scanResult.CpuMinAllowedPowerWatts,
                    ScannedMaxPl1Watts = scanResult.CpuMaxAllowedPl1Watts,
                    ScannedMaxPl2Watts = scanResult.CpuMaxAllowedPl2Watts,
                    BiosHardwareFingerprint = scanResult.BiosHardwareFingerprint,
                    BiosPowerScan = scanResult,
                    PawnIoAvailable = true,
                    PawnIoInstalled = true
                };
                form.Capabilities = caps;
                form.UpdatePowerLimitsForMode(0x01); // Balanced

                int expectedMin = scanResult.CpuMinAllowedPowerWatts;
                int expectedMax1 = scanResult.CpuMaxAllowedPl1Watts;
                int expectedMax2 = scanResult.CpuMaxAllowedPl2Watts;
                int expectedTdp = scanResult.CpuThermalSpecTdpWatts;

                // Apply custom limits & dirty hardware
                settings.EnableCustomPowerLimits = true;
                controller.SetPowerLimitsForMode(OperatingMode.Balanced, 30, 50);
                backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT,
                    (ulong)(30 * 8) | (1UL << 15) | (1UL << 16) | ((ulong)(50 * 8) << 32) | (1UL << 47) | (1UL << 48));

                // Execute ReScan multiple times consecutively
                for (int i = 0; i < 4; i++)
                {
                    var powerCaps = controller.InitializeHardwareFingerprintAndBaseline();
                    var pawnScan = controller.ScanDeviceForPawnIo();
                    form.PerformReScanCore(scanResult, pawnScan, powerCaps);

                    // Assert Settings bounds & defaults
                    Assert.Equal(expectedMin, settings.CpuMinAllowedPowerWatts);
                    Assert.Equal(expectedMax1, settings.CpuMaxAllowedPl1Watts);
                    Assert.Equal(expectedMax2, settings.CpuMaxAllowedPl2Watts);
                    Assert.Equal(expectedTdp, settings.CpuThermalSpecTdpWatts);

                    Assert.Equal(25, settings.BiosDefaultEcoPl1Watts);
                    Assert.Equal(35, settings.BiosDefaultEcoPl2Watts);
                    Assert.Equal(35, settings.BiosDefaultQuietPl1Watts);
                    Assert.Equal(45, settings.BiosDefaultQuietPl2Watts);
                    Assert.Equal(45, settings.BiosDefaultBalancedPl1Watts);
                    Assert.Equal(75, settings.BiosDefaultBalancedPl2Watts);
                    Assert.Equal(75, settings.BiosDefaultPerformancePl1Watts);
                    Assert.Equal(105, settings.BiosDefaultPerformancePl2Watts);
                    Assert.Equal(105, settings.BiosDefaultTurboPl1Watts);
                    Assert.Equal(140, settings.BiosDefaultTurboPl2Watts);

                    // Assert Form1.Capabilities
                    Assert.Equal(expectedMin, form.Capabilities.CpuMinAllowedPowerWatts);
                    Assert.Equal(expectedMax1, form.Capabilities.CpuMaxAllowedPl1Watts);
                    Assert.Equal(expectedMax2, form.Capabilities.CpuMaxAllowedPl2Watts);
                    Assert.Equal(expectedTdp, form.Capabilities.CpuThermalSpecTdpWatts);

                    // Assert mode defaults retrieved through Form1.Capabilities
                    Assert.Equal(25, form.Capabilities.GetScannedPl1(0x06));
                    Assert.Equal(35, form.Capabilities.GetScannedPl2(0x06));
                    Assert.Equal(45, form.Capabilities.GetScannedPl1(0x01));
                    Assert.Equal(75, form.Capabilities.GetScannedPl2(0x01));
                    Assert.Equal(105, form.Capabilities.GetScannedPl1(0x05));
                    Assert.Equal(140, form.Capabilities.GetScannedPl2(0x05));
                }
            });
        }

        #endregion

        #region 2. Test NonDestructiveFailedOrBusyReScan

        [Fact]
        public void NonDestructiveFailedOrBusyReScan_DriverHardwareError_RetainsVerifiedStartupBaseline()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
            using var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(45, 12, 140, 45, 75, 8);

            var settings = new AppSettings();
            using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);

            // Startup scan establishing verified baseline
            controller.InitializeWithBiosScanner(mockScanner);
            var baselineCaps = controller.InitializeHardwareFingerprintAndBaseline();
            Assert.True(baselineCaps.IsSupported);
            Assert.Equal(12, baselineCaps.ScannedMinPowerWatts);
            Assert.Equal(115, baselineCaps.ScannedMaxPl1Watts);
            Assert.Equal(140, baselineCaps.ScannedMaxPl2Watts);
            Assert.Equal(45, baselineCaps.ThermalSpecPowerWatts);

            // Simulate transient hardware error during ReScan
            backend.SimulateHardwareError = true;

            // ReScan under error condition
            var rescanCaps = controller.InitializeHardwareFingerprintAndBaseline();

            // Assert that AppSettings retains verified baseline without corruption or zeroing out
            Assert.Equal(12, settings.CpuMinAllowedPowerWatts);
            Assert.Equal(115, settings.CpuMaxAllowedPl1Watts);
            Assert.Equal(140, settings.CpuMaxAllowedPl2Watts);
            Assert.Equal(45, settings.CpuThermalSpecTdpWatts);

            Assert.Equal(25, settings.BiosDefaultEcoPl1Watts);
            Assert.Equal(35, settings.BiosDefaultEcoPl2Watts);
            Assert.Equal(35, settings.BiosDefaultQuietPl1Watts);
            Assert.Equal(45, settings.BiosDefaultQuietPl2Watts);
            Assert.Equal(45, settings.BiosDefaultBalancedPl1Watts);
            Assert.Equal(75, settings.BiosDefaultBalancedPl2Watts);
            Assert.Equal(75, settings.BiosDefaultPerformancePl1Watts);
            Assert.Equal(105, settings.BiosDefaultPerformancePl2Watts);
            Assert.Equal(105, settings.BiosDefaultTurboPl1Watts);
            Assert.Equal(140, settings.BiosDefaultTurboPl2Watts);

            // Assert that DevicePowerCapabilities retains previously verified baseline
            Assert.Equal(12, controller.Capabilities.ScannedMinPowerWatts);
            Assert.Equal(115, controller.Capabilities.ScannedMaxPl1Watts);
            Assert.Equal(140, controller.Capabilities.ScannedMaxPl2Watts);
            Assert.Equal(45, controller.Capabilities.ThermalSpecPowerWatts);
            Assert.True(controller.Capabilities.IsSupported);
        }

        [Fact]
        public void NonDestructiveFailedOrBusyReScan_AccessDeniedRequiresElevation_RetainsVerifiedStartupBaseline()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateTriton28WProfile();
            using var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(28, 8, 115, 28, 45, 8);

            var settings = new AppSettings();
            using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);

            controller.InitializeWithBiosScanner(mockScanner);
            controller.InitializeHardwareFingerprintAndBaseline();

            // Simulate transient AccessDenied during ReScan
            backend.SimulateAccessDenied = true;

            var rescanCaps = controller.InitializeHardwareFingerprintAndBaseline();

            // AppSettings & Capabilities remain completely protected from degradation
            Assert.Equal(8, settings.CpuMinAllowedPowerWatts);
            Assert.Equal(65, settings.CpuMaxAllowedPl1Watts);
            Assert.Equal(115, settings.CpuMaxAllowedPl2Watts);
            Assert.Equal(28, settings.CpuThermalSpecTdpWatts);

            Assert.Equal(15, settings.BiosDefaultEcoPl1Watts);
            Assert.Equal(20, settings.BiosDefaultEcoPl2Watts);
            Assert.Equal(28, settings.BiosDefaultBalancedPl1Watts);
            Assert.Equal(45, settings.BiosDefaultBalancedPl2Watts);

            Assert.Equal(8, controller.Capabilities.ScannedMinPowerWatts);
            Assert.Equal(65, controller.Capabilities.ScannedMaxPl1Watts);
            Assert.Equal(115, controller.Capabilities.ScannedMaxPl2Watts);
        }

        [Fact]
        public void NonDestructiveFailedOrBusyReScan_BusyRegisterReturningZero_RetainsVerifiedStartupBaseline()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateHelios18Hx55WProfile();
            using var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(55, 15, 190, 55, 95, 8);

            var settings = new AppSettings();
            using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);

            controller.InitializeWithBiosScanner(mockScanner);
            controller.InitializeHardwareFingerprintAndBaseline();

            // Simulate hardware register temporarily returning 0 (e.g. MSR 0x614 and 0x610 reading 0 during busy EC/PCH cycle)
            backend.SetMsr(PawnIoController.MSR_PKG_POWER_INFO, 0);
            backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT, 0);

            var rescanCaps = controller.InitializeHardwareFingerprintAndBaseline();

            // Non-destructive protection prevents zeroing out
            Assert.Equal(15, settings.CpuMinAllowedPowerWatts);
            Assert.Equal(175, settings.CpuMaxAllowedPl1Watts);
            Assert.Equal(190, settings.CpuMaxAllowedPl2Watts);
            Assert.Equal(55, settings.CpuThermalSpecTdpWatts);

            Assert.Equal(55, settings.BiosDefaultBalancedPl1Watts);
            Assert.Equal(95, settings.BiosDefaultBalancedPl2Watts);

            Assert.Equal(15, controller.Capabilities.ScannedMinPowerWatts);
            Assert.Equal(175, controller.Capabilities.ScannedMaxPl1Watts);
            Assert.Equal(190, controller.Capabilities.ScannedMaxPl2Watts);
        }

        [Fact]
        public void NonDestructiveFailedOrBusyReScan_Form1_TransientFailure_PreservesUiAndSettingsBaseline()
        {
            RunInStaThread(() =>
            {
                var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
                var scanResult = mockScanner.ScanPowerLimits();
                using var backend = new MockPawnIoBackend();
                backend.ConfigureIntelBaseline(45, 12, 140, 45, 75, 8);

                using var form = new Form1();
                var settings = form.Settings;
                using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);
                CpuPowerLimitController.Instance = controller;
                controller.InitializeWithBiosScanner(mockScanner);

                var verifiedCaps = new DeviceCapabilities
                {
                    CpuTdpWatts = scanResult.CpuThermalSpecTdpWatts,
                    ScannedMinPowerWatts = scanResult.CpuMinAllowedPowerWatts,
                    ScannedMaxPl1Watts = scanResult.CpuMaxAllowedPl1Watts,
                    ScannedMaxPl2Watts = scanResult.CpuMaxAllowedPl2Watts,
                    BiosHardwareFingerprint = scanResult.BiosHardwareFingerprint,
                    BiosPowerScan = scanResult,
                    PawnIoAvailable = true,
                    PawnIoInstalled = true
                };
                form.Capabilities = verifiedCaps;
                form.SetCurrentPowerModeForTesting(0x01);
                form.UpdatePowerLimitsForMode(0x01);

                // Simulate failed ReScan passing null/uncompleted biosScan and unsupported powerCaps
                var failedPowerCaps = DevicePowerCapabilities.Unsupported(PowerLimitApplyStatus.HardwareError, "Hardware busy");
                var failedPawnScan = new PawnIoScanResult { IsInstalled = false, DiagnosticMessage = "Temporary bus conflict" };

                form.PerformReScanCore(null, failedPawnScan, failedPowerCaps);

                // Assert Form1.Capabilities retained verified baseline without zeroing out
                Assert.Equal(12, form.Capabilities.CpuMinAllowedPowerWatts);
                Assert.Equal(115, form.Capabilities.CpuMaxAllowedPl1Watts);
                Assert.Equal(140, form.Capabilities.CpuMaxAllowedPl2Watts);
                Assert.Equal(45, form.Capabilities.CpuThermalSpecTdpWatts);

                // Assert BiosPowerScan was not replaced with null
                Assert.NotNull(form.Capabilities.BiosPowerScan);
                Assert.Equal(scanResult.BiosHardwareFingerprint, form.Capabilities.BiosHardwareFingerprint);

                // Assert mode defaults retained
                Assert.Equal(45, form.Capabilities.GetScannedPl1(0x01));
                Assert.Equal(75, form.Capabilities.GetScannedPl2(0x01));

                // Assert sliders retained their valid range
                Assert.NotNull(form.SliderPl1);
                Assert.NotNull(form.SliderPl2);
                Assert.Equal(12, form.SliderPl1.Minimum);
                Assert.Equal(140, form.SliderPl1.Maximum); // Bound to CpuMaxAllowedPl2Watts per Step 5
                Assert.Equal(12, form.SliderPl2.Minimum);
                Assert.Equal(140, form.SliderPl2.Maximum);

                // Assert Reset button Tag remains consistent
                Assert.Equal("45,75", form.BtnResetPowerLimits?.Tag as string);
            });
        }

        #endregion

        #region 3. Test UiSliderAndResetButtonIntegrity

        [Fact]
        public void UiSlider_RangeRebinding_DoesNotClampIntermediateValues_SingleSlider()
        {
            RunInStaThread(() =>
            {
                using var slider = new PredatorSlider();
                slider.Minimum = 10;
                slider.Maximum = 100;
                slider.Value = 45; // Intermediate value

                Assert.Equal(45, slider.Value);

                // 1. Expand range outward: [5, 140]
                slider.Minimum = 5;
                slider.Maximum = 140;
                Assert.Equal(45, slider.Value);

                // 2. Shrink range inward (still containing 45): [20, 80]
                slider.Minimum = 20;
                slider.Maximum = 80;
                Assert.Equal(45, slider.Value);

                // 3. Shift range: [12, 115] (Helios Neo PL1 range)
                slider.Minimum = 12;
                slider.Maximum = 115;
                Assert.Equal(45, slider.Value);

                // 4. Test intermediate value near upper boundary: 75W
                slider.Value = 75;
                Assert.Equal(75, slider.Value);

                slider.Minimum = 15;
                slider.Maximum = 175;
                Assert.Equal(75, slider.Value);
            });
        }

        [Fact]
        public void UiSlider_RangeRebinding_FormPowerLimits_PreservesUserConfiguredValues()
        {
            RunInStaThread(() =>
            {
                var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
                var scanResult = mockScanner.ScanPowerLimits();
                using var backend = new MockPawnIoBackend();
                backend.ConfigureIntelBaseline(45, 12, 140, 45, 75, 8);

                using var form = new Form1();
                var settings = form.Settings;
                using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);
                CpuPowerLimitController.Instance = controller;
                controller.InitializeWithBiosScanner(mockScanner);

                form.Capabilities = new DeviceCapabilities
                {
                    CpuTdpWatts = 45,
                    ScannedMinPowerWatts = 12,
                    ScannedMaxPl1Watts = 115,
                    ScannedMaxPl2Watts = 140,
                    BiosHardwareFingerprint = scanResult.BiosHardwareFingerprint,
                    BiosPowerScan = scanResult
                };

                // User sets custom intermediate limits: PL1 = 30W, PL2 = 50W
                settings.EnableCustomPowerLimits = true;
                settings.SetPowerLimitsForMode(OperatingMode.Balanced, 30, 50);

                form.UpdatePowerLimitsForMode(0x01); // Balanced

                Assert.NotNull(form.SliderPl1);
                Assert.NotNull(form.SliderPl2);
                Assert.Equal(30, form.SliderPl1.Value);
                Assert.Equal(50, form.SliderPl2.Value);

                // Simulate hardware bounds re-binding on ReScan (e.g. range updated from [10, 100] to [12, 115] / [12, 140])
                var updatedCaps = form.Capabilities with
                {
                    ScannedMinPowerWatts = 12,
                    ScannedMaxPl1Watts = 115,
                    ScannedMaxPl2Watts = 140
                };
                form.Capabilities = updatedCaps;

                // Re-bind bounds
                form.UpdatePowerLimitsForMode(0x01);

                // Assert intermediate values 30W and 50W are strictly preserved
                Assert.Equal(30, form.SliderPl1.Value);
                Assert.Equal(50, form.SliderPl2.Value);
                Assert.Equal(12, form.SliderPl1.Minimum);
                Assert.Equal(140, form.SliderPl1.Maximum); // Bound to CpuMaxAllowedPl2Watts per Step 5
                Assert.Equal(12, form.SliderPl2.Minimum);
                Assert.Equal(140, form.SliderPl2.Maximum);
            });
        }

        [Fact]
        public void UiResetButton_TagIsConsistentString_AcrossAllModesAndReScans()
        {
            RunInStaThread(() =>
            {
                var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
                var scanResult = mockScanner.ScanPowerLimits();
                using var backend = new MockPawnIoBackend();
                backend.ConfigureIntelBaseline(45, 12, 140, 45, 75, 8);

                using var form = new Form1();
                var settings = form.Settings;
                using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);
                CpuPowerLimitController.Instance = controller;
                controller.InitializeWithBiosScanner(mockScanner);

                form.Capabilities = new DeviceCapabilities
                {
                    CpuTdpWatts = 45,
                    ScannedMinPowerWatts = 12,
                    ScannedMaxPl1Watts = 115,
                    ScannedMaxPl2Watts = 140,
                    BiosHardwareFingerprint = scanResult.BiosHardwareFingerprint,
                    BiosPowerScan = scanResult
                };

                var expectedTags = new Dictionary<byte, string>
                {
                    { 0x06, "25,35" },   // Eco
                    { 0x00, "35,45" },   // Quiet
                    { 0x01, "45,75" },   // Balanced
                    { 0x04, "75,105" },  // Performance
                    { 0x05, "105,140" }  // Turbo
                };

                foreach (var kvp in expectedTags)
                {
                    byte mode = kvp.Key;
                    string expectedTag = kvp.Value;

                    form.SetCurrentPowerModeForTesting(mode);
                    form.UpdatePowerLimitsForMode(mode);

                    Assert.NotNull(form.BtnResetPowerLimits);
                    Assert.NotNull(form.BtnResetPowerLimits.Tag);

                    // Must strictly be string type, never ValueTuple
                    Assert.IsType<string>(form.BtnResetPowerLimits.Tag);
                    Assert.Equal(expectedTag, form.BtnResetPowerLimits.Tag as string);

                    // ReScan should maintain consistent string format
                    var pawnScan = controller.ScanDeviceForPawnIo();
                    var powerCaps = controller.InitializeHardwareFingerprintAndBaseline();
                    form.PerformReScanCore(scanResult, pawnScan, powerCaps);

                    Assert.IsType<string>(form.BtnResetPowerLimits.Tag);
                    Assert.Equal(expectedTag, form.BtnResetPowerLimits.Tag as string);
                }
            });
        }

        [Fact]
        public void UiResetButton_ClickingReset_RestoresExactVerifiedStockDefaults()
        {
            RunInStaThread(() =>
            {
                var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
                var scanResult = mockScanner.ScanPowerLimits();
                using var backend = new MockPawnIoBackend();
                backend.ConfigureIntelBaseline(45, 12, 140, 45, 75, 8);

                using var form = new Form1();
                var settings = form.Settings;
                using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);
                CpuPowerLimitController.Instance = controller;
                controller.InitializeWithBiosScanner(mockScanner);

                form.Capabilities = new DeviceCapabilities
                {
                    CpuTdpWatts = 45,
                    ScannedMinPowerWatts = 12,
                    ScannedMaxPl1Watts = 115,
                    ScannedMaxPl2Watts = 140,
                    BiosHardwareFingerprint = scanResult.BiosHardwareFingerprint,
                    BiosPowerScan = scanResult
                };

                form.SetCurrentPowerModeForTesting(0x01); // Balanced mode (stock: 45W / 75W)
                form.UpdatePowerLimitsForMode(0x01);

                // Set user custom limits: 30W / 50W
                settings.EnableCustomPowerLimits = true;
                settings.SetPowerLimitsForMode(OperatingMode.Balanced, 30, 50);
                form.UpdatePowerLimitsForMode(0x01);

                Assert.NotNull(form.SliderPl1);
                Assert.NotNull(form.SliderPl2);
                Assert.NotNull(form.BtnResetPowerLimits);

                Assert.Equal(30, form.SliderPl1.Value);
                Assert.Equal(50, form.SliderPl2.Value);
                Assert.True(form.BtnResetPowerLimits.Enabled, "Reset button should be enabled when customized");
                Assert.Equal("45,75", form.BtnResetPowerLimits.Tag as string);

                // Click Reset button
                form.BtnResetPowerLimits_Click(form.BtnResetPowerLimits, EventArgs.Empty);

                // 1. Sliders restored to exact verified stock defaults
                Assert.Equal(45, form.SliderPl1.Value);
                Assert.Equal(75, form.SliderPl2.Value);

                // 2. AppSettings custom limits cleared
                var (userPl1, userPl2) = settings.GetUserPowerLimitsForMode(OperatingMode.Balanced);
                Assert.Equal(-1, userPl1);
                Assert.Equal(-1, userPl2);

                // 3. Effective limits match stock defaults
                var (effPl1, effPl2) = settings.GetEffectiveLimitsForMode(OperatingMode.Balanced);
                Assert.Equal(45, effPl1);
                Assert.Equal(75, effPl2);

                // 4. Reset button disabled because values match stock
                Assert.False(form.BtnResetPowerLimits.Enabled, "Reset button should be disabled when matching stock defaults");

                // 5. Hardware written back to stock defaults
                backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong msr);
                int hwPl1 = (int)((msr & 0x7FFFUL) / 8);
                int hwPl2 = (int)(((msr >> 32) & 0x7FFFUL) / 8);
                Assert.Equal(45, hwPl1);
                Assert.Equal(75, hwPl2);
            });
        }

        [Fact]
        public void UiResetButton_ClickingReset_RestoresExactVerifiedStockDefaults_AcrossAllFiveModes()
        {
            RunInStaThread(() =>
            {
                var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
                var scanResult = mockScanner.ScanPowerLimits();
                using var backend = new MockPawnIoBackend();
                backend.ConfigureIntelBaseline(45, 12, 140, 45, 75, 8);

                using var form = new Form1();
                var settings = form.Settings;
                using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);
                CpuPowerLimitController.Instance = controller;
                controller.InitializeWithBiosScanner(mockScanner);

                form.Capabilities = new DeviceCapabilities
                {
                    CpuTdpWatts = 45,
                    ScannedMinPowerWatts = 12,
                    ScannedMaxPl1Watts = 115,
                    ScannedMaxPl2Watts = 140,
                    BiosHardwareFingerprint = scanResult.BiosHardwareFingerprint,
                    BiosPowerScan = scanResult
                };

                var testModes = new (byte ModeByte, OperatingMode OpMode, int DefPl1, int DefPl2, int CustomPl1, int CustomPl2)[]
                {
                    (0x06, OperatingMode.Eco, 25, 35, 18, 22),
                    (0x00, OperatingMode.Quiet, 35, 45, 25, 30),
                    (0x01, OperatingMode.Balanced, 45, 75, 30, 50),
                    (0x04, OperatingMode.Performance, 75, 105, 60, 85),
                    (0x05, OperatingMode.Turbo, 105, 140, 90, 120)
                };

                settings.EnableCustomPowerLimits = true;

                foreach (var item in testModes)
                {
                    form.SetCurrentPowerModeForTesting(item.ModeByte);
                    form.UpdatePowerLimitsForMode(item.ModeByte);

                    // Set custom values
                    settings.SetPowerLimitsForMode(item.OpMode, item.CustomPl1, item.CustomPl2);
                    form.UpdatePowerLimitsForMode(item.ModeByte);

                    Assert.NotNull(form.SliderPl1);
                    Assert.NotNull(form.SliderPl2);
                    Assert.NotNull(form.BtnResetPowerLimits);

                    Assert.Equal(item.CustomPl1, form.SliderPl1.Value);
                    Assert.Equal(item.CustomPl2, form.SliderPl2.Value);
                    Assert.True(form.BtnResetPowerLimits.Enabled);
                    Assert.Equal($"{item.DefPl1},{item.DefPl2}", form.BtnResetPowerLimits.Tag as string);

                    // Click Reset
                    form.BtnResetPowerLimits_Click(form.BtnResetPowerLimits, EventArgs.Empty);

                    // Assert stock restored
                    Assert.Equal(item.DefPl1, form.SliderPl1.Value);
                    Assert.Equal(item.DefPl2, form.SliderPl2.Value);
                    Assert.False(form.BtnResetPowerLimits.Enabled);

                    var (effPl1, effPl2) = settings.GetEffectiveLimitsForMode(item.OpMode);
                    Assert.Equal(item.DefPl1, effPl1);
                    Assert.Equal(item.DefPl2, effPl2);
                }
            });
        }

        #endregion
    }
}
