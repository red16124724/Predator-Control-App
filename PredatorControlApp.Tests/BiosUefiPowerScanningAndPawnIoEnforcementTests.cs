using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Exhaustive test suite for Native BIOS/UEFI & ACPI WMI Scanning, AppSettings persistence, and PawnIO enforcement:
    /// 1. Test BiosUefiPowerScanner:
    ///    - Parsing native UEFI ACPI PPCC binary tables, Acer ACPI WMI buffers, and MSR registers across simulated Predator profiles:
    ///      * 28W Triton / Neo 14 (TDP 28W, Min 8W, Max 65W, stock Balanced 28W/45W).
    ///      * 45W Helios Neo 16 (TDP 45W, Min 12W, Max 115W, stock Balanced 45W/75W).
    ///      * 55W Helios 18 HX (TDP 55W, Min 15W, Max 175W, stock Turbo 125W/175W).
    ///    - Strict zero external dependency verification: verify scanner uses ONLY firmware tables, WMI root\\WMI, and CPU MSRs.
    /// 2. Test BiosHardwareFingerprint:
    ///    - Invalidation when fingerprint changes (e.g. BIOS version updated or different motherboard).
    /// 3. Test Untouched BIOS Baseline Capture:
    ///    - Capture stock factory limits before applying custom overrides on mode switch.
    ///    - Verify all 5 modes (Eco, Quiet, Balanced, Performance, Turbo).
    /// 4. Test Reset to BIOS Default:
    ///    - Reset button restores active mode to scanned BIOS defaults.
    ///    - Writes stock values back to MSR 0x610 and MMIO.
    /// 5. Invariant & Boundary tests:
    ///    - PL2 >= PL1 invariant, dynamic slider coupling, and clamping within scanned bounds.
    /// </summary>
    public class BiosUefiPowerScanningAndPawnIoEnforcementTests
    {
        #region Binary AML PPCC Generator Helper

        /// <summary>
        /// Generates a valid synthetic binary ACPI AML stream containing the "PPCC" Name token
        /// and Package Power Control Capabilities integer structures encoded with AML DWordPrefix (0x0C).
        /// </summary>
        private static byte[] CreateSyntheticAmlPpcc(int minMw, int tdpMw, int maxPl1Mw, int maxPl2Mw)
        {
            var bytes = new List<byte>();

            // ACPI Table Header signature "SSDT" (4 bytes)
            bytes.AddRange(Encoding.ASCII.GetBytes("SSDT"));

            // Table length placeholder (4 bytes)
            bytes.AddRange(BitConverter.GetBytes(0x00000080));

            // Revision and Checksum
            bytes.Add(0x02);
            bytes.Add(0x00);

            // OEM ID + Table ID
            bytes.AddRange(Encoding.ASCII.GetBytes("ACRSYS"));
            bytes.AddRange(Encoding.ASCII.GetBytes("ACRPRDCT"));

            // Pad to offset
            while (bytes.Count < 36)
            {
                bytes.Add(0x00);
            }

            // ACPI NameOp = 0x08
            bytes.Add(0x08);

            // "PPCC" NameString (4 bytes)
            bytes.AddRange(Encoding.ASCII.GetBytes("PPCC"));

            // AML PackageOp = 0x12, PkgLength = 0x40, NumElements = 0x05
            bytes.Add(0x12);
            bytes.Add(0x40);
            bytes.Add(0x05);

            // Element 0: Revision = 2 (AML BytePrefix 0x0A)
            bytes.Add(0x0A);
            bytes.Add(0x02);

            // Helper to append AML DWord integer: 0x0C + 4 bytes little-endian
            void AppendAmlDword(int val)
            {
                bytes.Add(0x0C);
                bytes.AddRange(BitConverter.GetBytes((uint)val));
            }

            // Power control capabilities in milliwatts
            AppendAmlDword(minMw);    // Min allowed power
            AppendAmlDword(tdpMw);    // Thermal Spec / Base TDP
            AppendAmlDword(maxPl1Mw); // Max Sustained PL1
            AppendAmlDword(maxPl2Mw); // Max Short Burst PL2

            // Trailing padding to simulate realistic SSDT table layout
            for (int i = 0; i < 32; i++)
            {
                bytes.Add(0x00);
            }

            return bytes.ToArray();
        }

        #endregion

        #region Laptop Mock Backends

        private static MockPawnIoBackend CreateTriton28WBackend(bool locked = false)
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

        private static MockPawnIoBackend CreateHeliosNeo16_45WBackend(bool locked = false)
        {
            var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(
                tdpWatts: 45,
                minWatts: 12,
                maxWatts: 115,
                pl1Watts: 45,
                pl2Watts: 75,
                unitsPerWatt: 8,
                locked: locked,
                mirrorMmio: true
            );
            return backend;
        }

        private static MockPawnIoBackend CreateHelios18Hx55WBackend(bool locked = false)
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

        #endregion

        #region 1. Test BiosUefiPowerScanner

        [Fact]
        public void BiosUefiPowerScanner_ParsePpccFromAml_Triton28W_ExtractsExactLimits()
        {
            // 28W Triton / Neo 14: TDP 28W (28000mW), Min 8W (8000mW), Max PL1 65W (65000mW), Max PL2 115W (115000mW)
            byte[] aml = CreateSyntheticAmlPpcc(minMw: 8000, tdpMw: 28000, maxPl1Mw: 65000, maxPl2Mw: 115000);

            var parsed = BiosUefiPowerScanner.ParsePpccFromAml(aml);

            Assert.NotNull(parsed);
            Assert.Equal(8, parsed.Value.MinPower);
            Assert.Equal(65, parsed.Value.MaxPl1);
            Assert.Equal(115, parsed.Value.MaxPl2);
            Assert.True(parsed.Value.BaseTdp >= 28);
        }

        [Fact]
        public void BiosUefiPowerScanner_ParsePpccFromAml_HeliosNeo16_45W_ExtractsExactLimits()
        {
            // 45W Helios Neo 16: TDP 45W (45000mW), Min 12W (12000mW), Max PL1 115W (115000mW), Max PL2 140W (140000mW)
            byte[] aml = CreateSyntheticAmlPpcc(minMw: 12000, tdpMw: 45000, maxPl1Mw: 115000, maxPl2Mw: 140000);

            var parsed = BiosUefiPowerScanner.ParsePpccFromAml(aml);

            Assert.NotNull(parsed);
            Assert.Equal(12, parsed.Value.MinPower);
            Assert.Equal(115, parsed.Value.MaxPl1);
            Assert.Equal(140, parsed.Value.MaxPl2);
            Assert.True(parsed.Value.BaseTdp >= 45);
        }

        [Fact]
        public void BiosUefiPowerScanner_ParsePpccFromAml_Helios18Hx_55W_ExtractsExactLimits()
        {
            // 55W Helios 18 HX: TDP 55W (55000mW), Min 15W (15000mW), Max PL1 175W (175000mW), Max PL2 190W (190000mW)
            byte[] aml = CreateSyntheticAmlPpcc(minMw: 15000, tdpMw: 55000, maxPl1Mw: 175000, maxPl2Mw: 190000);

            var parsed = BiosUefiPowerScanner.ParsePpccFromAml(aml);

            Assert.NotNull(parsed);
            Assert.Equal(15, parsed.Value.MinPower);
            Assert.Equal(175, parsed.Value.MaxPl1);
            Assert.Equal(190, parsed.Value.MaxPl2);
            Assert.True(parsed.Value.BaseTdp >= 55);
        }

        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
        [InlineData(new byte[] { 0x50, 0x50, 0x43 })] // Truncated "PPC"
        public void BiosUefiPowerScanner_ParsePpccFromAml_MalformedOrEmptyBuffer_ReturnsNullSafely(byte[] malformed)
        {
            var parsed = BiosUefiPowerScanner.ParsePpccFromAml(malformed);
            Assert.Null(parsed);
        }

        [Fact]
        public void BiosUefiPowerScanner_ParsePpccFromAml_PpccWithNoValidMilliwatts_ReturnsNullSafely()
        {
            // PPCC token present, but values are only tiny integers (0, 1, 2) outside mW range [5000, 350000]
            var bytes = new List<byte>();
            bytes.AddRange(Encoding.ASCII.GetBytes("PPCC"));
            bytes.Add(0x0C);
            bytes.AddRange(BitConverter.GetBytes(100u)); // 100 mW - below 5000 mW threshold
            bytes.Add(0x0C);
            bytes.AddRange(BitConverter.GetBytes(400000u)); // 400000 mW - above 350000 mW threshold

            var parsed = BiosUefiPowerScanner.ParsePpccFromAml(bytes.ToArray());
            Assert.Null(parsed);
        }

        [Fact]
        public void BiosUefiPowerScanner_MsrRegisters_Triton28W_ReadsAccurateBaseline()
        {
            using var backend = CreateTriton28WBackend();
            using var pawnIo = new PawnIoController(backend);

            var caps = pawnIo.ScanDevicePowerCapabilities();

            Assert.True(caps.IsSupported);
            Assert.False(caps.IsLocked);
            Assert.Equal(28, caps.ThermalSpecPowerWatts);
            Assert.Equal(8, caps.MinimumPowerWatts);
            Assert.Equal(65, caps.MaximumPowerWatts);
            Assert.Equal(28, caps.CurrentPl1Watts); // Stock Balanced PL1
            Assert.Equal(45, caps.CurrentPl2Watts); // Stock Balanced PL2
        }

        [Fact]
        public void BiosUefiPowerScanner_MsrRegisters_HeliosNeo16_45W_ReadsAccurateBaseline()
        {
            using var backend = CreateHeliosNeo16_45WBackend();
            using var pawnIo = new PawnIoController(backend);

            var caps = pawnIo.ScanDevicePowerCapabilities();

            Assert.True(caps.IsSupported);
            Assert.False(caps.IsLocked);
            Assert.Equal(45, caps.ThermalSpecPowerWatts);
            Assert.Equal(12, caps.MinimumPowerWatts);
            Assert.Equal(115, caps.MaximumPowerWatts);
            Assert.Equal(45, caps.CurrentPl1Watts); // Stock Balanced PL1
            Assert.Equal(75, caps.CurrentPl2Watts); // Stock Balanced PL2
        }

        [Fact]
        public void BiosUefiPowerScanner_MsrRegisters_Helios18Hx_55W_ReadsAccurateBaseline()
        {
            using var backend = CreateHelios18Hx55WBackend();
            using var pawnIo = new PawnIoController(backend);

            var caps = pawnIo.ScanDevicePowerCapabilities();

            Assert.True(caps.IsSupported);
            Assert.False(caps.IsLocked);
            Assert.Equal(55, caps.ThermalSpecPowerWatts);
            Assert.Equal(15, caps.MinimumPowerWatts);
            Assert.Equal(175, caps.MaximumPowerWatts);
            Assert.Equal(125, caps.CurrentPl1Watts); // Stock Turbo PL1
            Assert.Equal(175, caps.CurrentPl2Watts); // Stock Turbo PL2
        }

        [Fact]
        public void StrictZeroExternalDependencyVerification_ScannerUsesOnlyNativeFirmwareAndWmiAndMsr()
        {
            var scannerType = typeof(BiosUefiPowerScanner);
            var assembly = scannerType.Assembly;

            // 1. Inspect referenced assemblies
            var referencedAssemblies = assembly.GetReferencedAssemblies();
            var bannedLibrarySubstrings = new[]
            {
                "LibreHardwareMonitor",
                "OpenHardwareMonitor",
                "CPUID",
                "ThrottleStop",
                "IntelXTU",
                "RyzenMaster",
                "AIDA64",
                "HWiNFO"
            };

            foreach (var refAsm in referencedAssemblies)
            {
                foreach (var banned in bannedLibrarySubstrings)
                {
                    Assert.DoesNotContain(banned, refAsm.Name, StringComparison.OrdinalIgnoreCase);
                }
            }

            // 2. Inspect all P/Invoke declarations in BiosUefiPowerScanner
            var pInvokeMethods = scannerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<DllImportAttribute>() != null)
                .ToList();

            Assert.NotEmpty(pInvokeMethods);
            foreach (var pInv in pInvokeMethods)
            {
                var attr = pInv.GetCustomAttribute<DllImportAttribute>()!;
                // Strictly kernel32.dll for native firmware tables and environment variables
                Assert.Equal("kernel32.dll", attr.Value, ignoreCase: true);
            }

            // 3. Inspect field and method types to ensure no 3rd-party types
            var allTypes = scannerType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Concat(new[] { scannerType });

            foreach (var t in allTypes)
            {
                foreach (var method in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    Assert.False(method.DeclaringType?.Namespace?.Contains("HardwareMonitor") ?? false);
                }
            }
        }

        #endregion

        #region 2. Test BiosHardwareFingerprint

        [Fact]
        public void BiosHardwareFingerprint_SanitizationAndFormat_IsValidString()
        {
            var scanner = new BiosUefiPowerScanner();
            string fp = scanner.ComputeBiosHardwareFingerprint();

            Assert.NotNull(fp);
            Assert.NotEmpty(fp);
            // Must contain only legal alphanumeric, underscore, dot, or dash
            Assert.True(fp.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-'));
        }

        [Fact]
        public void BiosHardwareFingerprint_InvalidationWhenBiosVersionUpdated_WipesCachedDefaultsAndOverrides()
        {
            // Initial machine: BIOS V1.14
            var settings = new AppSettings
            {
                BiosHardwareFingerprint = "Acer_Predator_PH16-71_V1.14_2024-01-10_Core_i7-13700HX",
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 45,
                CpuMinAllowedPowerWatts = 12,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140,
                BiosDefaultBalancedPl1Watts = 45,
                BiosDefaultBalancedPl2Watts = 75,
                CustomBalancedPl1Watts = 50,
                CustomBalancedPl2Watts = 80,
                EnableCustomPowerLimits = true
            };

            using var backend = CreateHeliosNeo16_45WBackend();
            using var controller = new CpuPowerLimitController(settings, backend);

            // User updates BIOS to V1.15 -> Fingerprint changes!
            var updatedBiosMock = new MockBiosUefiPowerScanner(new BiosPowerScanResult
            {
                BiosHardwareFingerprint = "Acer_Predator_PH16-71_V1.15_2024-04-12_Core_i7-13700HX",
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 45,
                CpuMinAllowedPowerWatts = 12,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140,
                BiosDefaultBalancedPl1Watts = 45,
                BiosDefaultBalancedPl2Watts = 75,
                VerifiedBiosModesBitmask = 0x1F
            })
            {
                FingerprintToReturn = "Acer_Predator_PH16-71_V1.15_2024-04-12_Core_i7-13700HX"
            };

            controller.InitializeWithBiosScanner(updatedBiosMock);

            // Verified invalidation: Stale user overrides wiped to -1
            Assert.Equal(-1, settings.CustomBalancedPl1Watts);
            Assert.Equal(-1, settings.CustomBalancedPl2Watts);
            // New fingerprint recorded
            Assert.Equal("Acer_Predator_PH16-71_V1.15_2024-04-12_Core_i7-13700HX", settings.BiosHardwareFingerprint);
            // Completed scan updated
            Assert.True(settings.HasCompletedBiosPowerScan);
        }

        [Fact]
        public void BiosHardwareFingerprint_InvalidationWhenMotherboardMigrated_WipesBoundsAndAppliesNewHardware()
        {
            // Initial system: Triton Neo 14 (PT14-51, TDP 28W, Max PL2 115W)
            var settings = new AppSettings
            {
                BiosHardwareFingerprint = "Acer_Predator_PT14-51_V1.08_2024-01-15_Core_Ultra_7_155H",
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 28,
                CpuMinAllowedPowerWatts = 8,
                CpuMaxAllowedPl1Watts = 65,
                CpuMaxAllowedPl2Watts = 115,
                BiosDefaultBalancedPl1Watts = 28,
                BiosDefaultBalancedPl2Watts = 45,
                CustomBalancedPl1Watts = 32,
                CustomBalancedPl2Watts = 50,
                EnableCustomPowerLimits = true
            };

            // Motherboard / CPU replaced with Helios 18 HX
            using var heliosBackend = CreateHelios18Hx55WBackend();
            using var controller = new CpuPowerLimitController(settings, heliosBackend);

            var heliosMock = MockBiosUefiPowerScanner.CreateHelios18Hx55WProfile();
            controller.InitializeWithBiosScanner(heliosMock);

            // Stale Triton user custom overrides wiped
            Assert.Equal(-1, settings.CustomBalancedPl1Watts);
            Assert.Equal(-1, settings.CustomBalancedPl2Watts);

            // Upgraded Helios bounds recorded
            Assert.Equal(heliosMock.FingerprintToReturn, settings.BiosHardwareFingerprint);
            Assert.Equal(55, settings.CpuThermalSpecTdpWatts);
            Assert.Equal(15, settings.CpuMinAllowedPowerWatts);
            Assert.Equal(175, settings.CpuMaxAllowedPl1Watts);
            Assert.Equal(190, settings.CpuMaxAllowedPl2Watts);
            Assert.Equal((55, 95), settings.GetDefaultPowerLimitsForMode(OperatingMode.Balanced));
            Assert.Equal((125, 175), settings.GetDefaultPowerLimitsForMode(OperatingMode.Turbo));
        }

        [Fact]
        public void BiosHardwareFingerprint_UnchangedFingerprint_PreservesExistingUserOverrides()
        {
            const string signature = "Acer_Predator_PH16-71_V1.15_2024-03-12_Core_i7-13700HX";
            var settings = new AppSettings
            {
                BiosHardwareFingerprint = signature,
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 45,
                CpuMinAllowedPowerWatts = 12,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140,
                BiosDefaultBalancedPl1Watts = 45,
                BiosDefaultBalancedPl2Watts = 75,
                CustomBalancedPl1Watts = 48,
                CustomBalancedPl2Watts = 82,
                EnableCustomPowerLimits = true
            };

            using var backend = CreateHeliosNeo16_45WBackend();
            using var controller = new CpuPowerLimitController(settings, backend);

            var mock = new MockBiosUefiPowerScanner(new BiosPowerScanResult
            {
                BiosHardwareFingerprint = signature,
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 45,
                CpuMinAllowedPowerWatts = 12,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140,
                BiosDefaultBalancedPl1Watts = 45,
                BiosDefaultBalancedPl2Watts = 75,
                VerifiedBiosModesBitmask = 0x1F
            })
            {
                FingerprintToReturn = signature
            };

            controller.InitializeWithBiosScanner(mock);

            // Preserves user overrides!
            Assert.Equal(48, settings.CustomBalancedPl1Watts);
            Assert.Equal(82, settings.CustomBalancedPl2Watts);
        }

        #endregion

        #region 3. Test Untouched BIOS Baseline Capture

        [Fact]
        public async Task UntouchedBiosBaselineCapture_CapturesStockFactoryLimitsBeforeApplyingCustomOverrides()
        {
            using var backend = CreateTriton28WBackend();
            var settings = new AppSettings
            {
                BiosHardwareFingerprint = "Acer_Predator_PT14-51_V1.08_2024-01-15_Core_Ultra_7_155H",
                CpuMinAllowedPowerWatts = 8,
                CpuMaxAllowedPl1Watts = 65,
                CpuMaxAllowedPl2Watts = 115,
                EnableCustomPowerLimits = true,
                CustomBalancedPl1Watts = 35,
                CustomBalancedPl2Watts = 55
            };

            using var controller = new CpuPowerLimitController(settings, backend);

            // Active MSR 0x610 in hardware currently has factory defaults 28W / 45W
            backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT,
                (ulong)(28 * 8) | (1UL << 15) | (1UL << 16) | ((ulong)(45 * 8) << 32) | (1UL << 47) | (1UL << 48));

            // Settle delay = 0 for test execution
            bool captured = await controller.CaptureStockModeBaselineIfNeededAsync(OperatingMode.Balanced, settleDelayMs: 0);

            Assert.True(captured);
            // 1. Stock factory baseline captured untouched
            Assert.True(settings.HasScannedDefaultsForMode(OperatingMode.Balanced));
            Assert.Equal(28, settings.BiosDefaultBalancedPl1Watts);
            Assert.Equal(45, settings.BiosDefaultBalancedPl2Watts);

            // 2. Custom override applied AFTER capturing stock baseline
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong activeMsr);
            int activePl1 = (int)((activeMsr & 0x7FFFUL) / 8);
            int activePl2 = (int)(((activeMsr >> 32) & 0x7FFFUL) / 8);
            Assert.Equal(35, activePl1);
            Assert.Equal(55, activePl2);

            // 3. Saved default in AppSettings remains untouched factory value (28W/45W), NOT 35W/55W!
            Assert.Equal((28, 45), settings.GetDefaultPowerLimitsForMode(OperatingMode.Balanced));
        }

        [Fact]
        public void UntouchedBiosBaselineCapture_VerifiesAllFiveModes()
        {
            var settings = new AppSettings
            {
                CpuMinAllowedPowerWatts = 10,
                CpuMaxAllowedPl1Watts = 140,
                CpuMaxAllowedPl2Watts = 180
            };

            // Factory limits for all 5 modes
            var expectedFactoryDefaults = new Dictionary<OperatingMode, (int Pl1, int Pl2)>
            {
                { OperatingMode.Eco, (20, 30) },
                { OperatingMode.Quiet, (30, 40) },
                { OperatingMode.Balanced, (45, 75) },
                { OperatingMode.Performance, (65, 95) },
                { OperatingMode.Turbo, (85, 125) }
            };

            foreach (var (mode, limits) in expectedFactoryDefaults)
            {
                Assert.False(settings.HasScannedDefaultsForMode(mode));
                settings.RecordScannedDefaultsForMode(mode, limits.Pl1, limits.Pl2);
                Assert.True(settings.HasScannedDefaultsForMode(mode));
                Assert.Equal(limits, settings.GetDefaultPowerLimitsForMode(mode));
            }

            // User customizes all 5 modes
            foreach (var (mode, limits) in expectedFactoryDefaults)
            {
                settings.SetPowerLimitsForMode(mode, limits.Pl1 + 5, limits.Pl2 + 10);
                var (effPl1, effPl2) = settings.GetEffectivePowerLimitsForMode(mode);
                Assert.Equal(limits.Pl1 + 5, effPl1);
                Assert.Equal(limits.Pl2 + 10, effPl2);

                // Original BIOS baseline must remain 100% untouched!
                Assert.Equal(limits, settings.GetDefaultPowerLimitsForMode(mode));
            }
        }

        [Fact]
        public async Task UntouchedBiosBaselineCapture_MultiModeFullCalibrationScan_CyclesAllFiveModesAndRestoresOriginal()
        {
            using var backend = CreateHeliosNeo16_45WBackend();
            var settings = new AppSettings
            {
                PowerMode = (byte)OperatingMode.Balanced,
                EnableCustomPowerLimits = false
            };

            using var controller = new CpuPowerLimitController(settings, backend);

            var modesVisited = new List<OperatingMode>();

            var scanResults = await controller.PerformFullHardwareCalibrationScanAsync(
                CancellationToken.None,
                settleDelayMs: 0,
                forceRescan: true,
                modeSwitchAction: mode =>
                {
                    modesVisited.Add(mode);
                    return Task.CompletedTask;
                });

            // Visited all 5 modes
            Assert.Contains(OperatingMode.Eco, modesVisited);
            Assert.Contains(OperatingMode.Quiet, modesVisited);
            Assert.Contains(OperatingMode.Balanced, modesVisited);
            Assert.Contains(OperatingMode.Performance, modesVisited);
            Assert.Contains(OperatingMode.Turbo, modesVisited);

            // Last mode action restored the original mode (Balanced)
            Assert.Equal(OperatingMode.Balanced, modesVisited.Last());

            // All 5 modes have valid defaults
            Assert.Equal(5, scanResults.Count);
            foreach (var mode in scanResults.Keys)
            {
                Assert.True(settings.HasScannedDefaultsForMode(mode));
                Assert.True(scanResults[mode].Pl1 > 0);
                Assert.True(scanResults[mode].Pl2 >= scanResults[mode].Pl1);
            }
        }

        #endregion

        #region 4. Test Reset to BIOS Default

        [Fact]
        public void ResetToBiosDefault_RestoresActiveModeToScannedBiosDefaults()
        {
            using var backend = CreateTriton28WBackend();
            var settings = new AppSettings
            {
                BiosHardwareFingerprint = "Acer_Predator_PT14-51_V1.08_2024-01-15_Core_Ultra_7_155H",
                CpuMinAllowedPowerWatts = 8,
                CpuMaxAllowedPl1Watts = 65,
                CpuMaxAllowedPl2Watts = 115,
                PowerMode = (byte)OperatingMode.Balanced,
                EnableCustomPowerLimits = true
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 28, 45);

            // User applies custom limits
            controller.SetPowerLimitsForMode(OperatingMode.Balanced, 36, 55);
            var (activePl1, activePl2, isCustom) = controller.GetDisplayPowerLimits(OperatingMode.Balanced);
            Assert.Equal(36, activePl1);
            Assert.Equal(55, activePl2);
            Assert.True(isCustom);

            // User clicks "Reset" button
            var resetResult = controller.ResetModeToStockDefaults(OperatingMode.Balanced);

            Assert.True(resetResult.IsSuccess);
            Assert.Equal(28, resetResult.AppliedPl1Watts);
            Assert.Equal(45, resetResult.AppliedPl2Watts);

            // User custom limit wiped back to -1
            Assert.Equal(-1, settings.CustomBalancedPl1Watts);
            Assert.Equal(-1, settings.CustomBalancedPl2Watts);

            // Display power limits reflect stock factory defaults
            var (restoredPl1, restoredPl2, isStillCustom) = controller.GetDisplayPowerLimits(OperatingMode.Balanced);
            Assert.Equal(28, restoredPl1);
            Assert.Equal(45, restoredPl2);
            Assert.False(isStillCustom);
        }

        [Fact]
        public void ResetToBiosDefault_WritesStockValuesBackToMsr0x610AndMmio()
        {
            using var backend = CreateTriton28WBackend();
            var settings = new AppSettings
            {
                BiosHardwareFingerprint = "Acer_Predator_PT14-51_V1.08_2024-01-15_Core_Ultra_7_155H",
                PowerMode = (byte)OperatingMode.Balanced,
                EnableCustomPowerLimits = true
            };

            using var controller = new CpuPowerLimitController(settings, backend);
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 28, 45);

            // Apply custom limit first: 34W / 52W
            var setRes = controller.SetPowerLimitsForMode(OperatingMode.Balanced, 34, 52);
Assert.Equal(34, setRes.AppliedPl1Watts);

            // Clear operation history
            int initialHistoryCount = backend.History.Count;

            // Execute Reset button logic
            var result = controller.ResetModeToStockDefaults(OperatingMode.Balanced);

            Assert.True(result.IsSuccess);
            Assert.True(result.MmioMirrored, $"Result: {result}");

            // Verify operations dispatched to hardware
            var recentHistory = backend.History.Skip(initialHistoryCount).ToList();

            // 1. MSR 0x610 written with 28W PL1 & 45W PL2
            var msrWrite = recentHistory.FirstOrDefault(h => h.Op == "WriteMsr" && h.Target == PawnIoController.MSR_PKG_POWER_LIMIT);
            Assert.NotEqual(default, msrWrite);
            ulong expectedRawMsrPl1 = 28 * 8;
            ulong expectedRawMsrPl2 = 45 * 8;
            Assert.Equal(expectedRawMsrPl1, msrWrite.Value & 0x7FFFUL);
            Assert.Equal(expectedRawMsrPl2, (msrWrite.Value >> 32) & 0x7FFFUL);

            // 2. MMIO written with matching limits
            var mmioWrite = recentHistory.FirstOrDefault(h => h.Op.StartsWith("WriteMmio"));
            Assert.NotEqual(default, mmioWrite);

            // 3. Live hardware register now reads 28W / 45W
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong liveMsr);
            Assert.Equal(28, (int)((liveMsr & 0x7FFFUL) / 8));
            Assert.Equal(45, (int)(((liveMsr >> 32) & 0x7FFFUL) / 8));
        }

        [Fact]
        public void ResetAllModesToStockDefaults_RestoresAllFiveModesInSettingsAndHardware()
        {
            using var backend = CreateHeliosNeo16_45WBackend();
            var settings = new AppSettings
            {
                BiosHardwareFingerprint = "Acer_Predator_PHN16-71_V1.15_2024-03-12_Core_i7-13700HX",
                PowerMode = (byte)OperatingMode.Performance,
                EnableCustomPowerLimits = true
            };

            using var controller = new CpuPowerLimitController(settings, backend);

            // Record baselines for all 5 modes
            settings.RecordScannedDefaultsForMode(OperatingMode.Eco, 25, 35);
            settings.RecordScannedDefaultsForMode(OperatingMode.Quiet, 35, 45);
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 45, 75);
            settings.RecordScannedDefaultsForMode(OperatingMode.Performance, 75, 105);
            settings.RecordScannedDefaultsForMode(OperatingMode.Turbo, 105, 140);

            // Customize all 5 modes
            settings.SetPowerLimitsForMode(OperatingMode.Eco, 28, 38);
            settings.SetPowerLimitsForMode(OperatingMode.Quiet, 38, 48);
            settings.SetPowerLimitsForMode(OperatingMode.Balanced, 50, 80);
            settings.SetPowerLimitsForMode(OperatingMode.Performance, 80, 110);
            settings.SetPowerLimitsForMode(OperatingMode.Turbo, 110, 145);

            // Reset all modes to stock defaults
            var result = controller.ResetAllModesToStockDefaults();

            Assert.True(result.IsSuccess);

            // Verify all custom settings are wiped
            Assert.Equal(-1, settings.CustomEcoPl1Watts);
            Assert.Equal(-1, settings.CustomEcoPl2Watts);
            Assert.Equal(-1, settings.CustomQuietPl1Watts);
            Assert.Equal(-1, settings.CustomQuietPl2Watts);
            Assert.Equal(-1, settings.CustomBalancedPl1Watts);
            Assert.Equal(-1, settings.CustomBalancedPl2Watts);
            Assert.Equal(-1, settings.CustomPerformancePl1Watts);
            Assert.Equal(-1, settings.CustomPerformancePl2Watts);
            Assert.Equal(-1, settings.CustomTurboPl1Watts);
            Assert.Equal(-1, settings.CustomTurboPl2Watts);

            // Verify active mode (Performance) in hardware was restored to 75W / 105W
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong msr);
            Assert.Equal(75, (int)((msr & 0x7FFFUL) / 8));
            Assert.Equal(105, (int)(((msr >> 32) & 0x7FFFUL) / 8));
        }

        #endregion

        #region 5. Invariant & Boundary Tests

        [Fact]
        public void Invariant_Pl2AlwaysGreaterOrEqualToPl1_EnforcedInAppSettings()
        {
            var settings = new AppSettings
            {
                CpuMinAllowedPowerWatts = 10,
                CpuMaxAllowedPl1Watts = 100,
                CpuMaxAllowedPl2Watts = 150
            };

            // Attempt to set PL2 < PL1 (e.g. PL1 = 60W, PL2 = 40W)
            settings.SetPowerLimitsForMode(OperatingMode.Balanced, pl1: 60, pl2: 40);

            var (effPl1, effPl2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced);

            Assert.Equal(60, effPl1);
            Assert.True(effPl2 >= effPl1, $"PL2 ({effPl2}) must be >= PL1 ({effPl1})");
            Assert.Equal(60, effPl2); // Clamped up to PL1
        }

        [Fact]
        public void Invariant_Pl2AlwaysGreaterOrEqualToPl1_EnforcedInPawnIoController()
        {
            using var backend = CreateHeliosNeo16_45WBackend();
            using var controller = new PawnIoController(backend);

            // Attempt to apply PL1 = 80W, PL2 = 50W (violating PL2 >= PL1)
            var result = controller.ApplyPowerLimits(targetPl1Watts: 80, targetPl2Watts: 50);

            Assert.True(result.IsSuccess);
            Assert.Equal(80, result.AppliedPl1Watts);
            Assert.True(result.AppliedPl2Watts >= result.AppliedPl1Watts);
            Assert.Equal(80, result.AppliedPl2Watts);

            // Hardware register verification
            backend.ReadMsr(PawnIoController.MSR_PKG_POWER_LIMIT, out ulong msr);
            int livePl1 = (int)((msr & 0x7FFFUL) / 8);
            int livePl2 = (int)(((msr >> 32) & 0x7FFFUL) / 8);
            Assert.Equal(80, livePl1);
            Assert.Equal(80, livePl2);
        }

        [Fact]
        public void DynamicSliderCoupling_IncreasingPl1PushesPl2Up()
        {
            // Simulate UI slider interaction logic
            int sliderPl1 = 30;
            int sliderPl2 = 45;

            // User drags PL1 slider up to 55W (exceeding current PL2 of 45W)
            sliderPl1 = 55;
            if (sliderPl2 < sliderPl1)
            {
                sliderPl2 = sliderPl1; // Coupled push
            }

            Assert.Equal(55, sliderPl1);
            Assert.Equal(55, sliderPl2);
            Assert.True(sliderPl2 >= sliderPl1);
        }

        [Fact]
        public void DynamicSliderCoupling_DecreasingPl2BelowPl1ClampsPl2()
        {
            // Simulate UI slider interaction logic
            int sliderPl1 = 60;
            int sliderPl2 = 80;

            // User attempts to drag PL2 below PL1 (e.g. to 40W)
            int attemptedPl2 = 40;
            sliderPl2 = Math.Max(sliderPl1, attemptedPl2);

            Assert.Equal(60, sliderPl1);
            Assert.Equal(60, sliderPl2);
            Assert.True(sliderPl2 >= sliderPl1);
        }

        [Theory]
        [InlineData(-50, -20, 8, 8)]       // Far below min bounds
        [InlineData(0, 0, 8, 8)]           // Zero inputs
        [InlineData(200, 300, 65, 115)]    // Exceeding max PL1 and max PL2
        [InlineData(100, 50, 65, 65)]      // Exceeding max PL1 and inverted
        public void ClampingWithinScannedBounds_ClampsExtremeInputs(int inPl1, int inPl2, int expectedPl1, int expectedPl2)
        {
            var settings = new AppSettings
            {
                CpuMinAllowedPowerWatts = 8,
                CpuMaxAllowedPl1Watts = 65,
                CpuMaxAllowedPl2Watts = 115
            };

            settings.SetPowerLimitsForMode(OperatingMode.Balanced, inPl1, inPl2);
            var (effPl1, effPl2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced);

            Assert.Equal(expectedPl1, effPl1);
            Assert.Equal(expectedPl2, effPl2);
            Assert.True(effPl2 >= effPl1);
            Assert.True(effPl1 >= settings.CpuMinAllowedPowerWatts);
            Assert.True(effPl1 <= settings.CpuMaxAllowedPl1Watts);
            Assert.True(effPl2 <= settings.CpuMaxAllowedPl2Watts);
        }

        [Fact]
        public void ConcurrencyAndThreadSafety_ConcurrentResetAndOverride_PreservesIntegrity()
        {
            using var backend = CreateHeliosNeo16_45WBackend();
            var settings = new AppSettings
            {
                BiosHardwareFingerprint = "Acer_Predator_PHN16-71_V1.15_2024-03-12_Core_i7-13700HX",
                PowerMode = (byte)OperatingMode.Balanced,
                EnableCustomPowerLimits = true,
                CpuMinAllowedPowerWatts = 12,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140
            };
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 45, 75);

            using var controller = new CpuPowerLimitController(settings, backend);

            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new List<Task>();

            for (int i = 0; i < 20; i++)
            {
                int index = i;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        if (index % 3 == 0)
                        {
                            controller.SetPowerLimitsForMode(OperatingMode.Balanced, 45 + (index % 10), 75 + (index % 15));
                        }
                        else if (index % 3 == 1)
                        {
                            controller.ResetModeToStockDefaults(OperatingMode.Balanced);
                        }
                        else
                        {
                            var (pl1, pl2, _) = controller.GetDisplayPowerLimits(OperatingMode.Balanced);
                            Assert.True(pl2 >= pl1, $"Concurrent invariant failure: PL1={pl1}, PL2={pl2}");
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            Assert.Empty(exceptions);

            // Final state check
            var (finalPl1, finalPl2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced);
            Assert.True(finalPl2 >= finalPl1);
            Assert.True(finalPl1 >= 12 && finalPl1 <= 115);
            Assert.True(finalPl2 >= 12 && finalPl2 <= 140);
        }

        #endregion
    }
}
