using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class BiosUefiPowerScannerTests
    {
        [Fact]
        public void ComputeBiosHardwareFingerprint_ReturnsDeterministicSanitizedString()
        {
            var scanner = new BiosUefiPowerScanner();
            string fp1 = scanner.ComputeBiosHardwareFingerprint();
            string fp2 = scanner.ComputeBiosHardwareFingerprint();
            string fp3 = scanner.ComputeHardwareFingerprint();

            Assert.False(string.IsNullOrWhiteSpace(fp1));
            Assert.Equal(fp1, fp2);
            Assert.Equal(fp1, fp3);

            // Verifies no invalid characters in hardware fingerprint
            Assert.DoesNotContain(" ", fp1);
            Assert.DoesNotContain("\\", fp1);
            Assert.DoesNotContain("/", fp1);
        }

        [Fact]
        public void ParsePpccFromAml_WithValidSyntheticAmlPackage_ExtractsPowerLimitsCorrectly()
        {
            // Build synthetic AML buffer containing "PPCC" package
            // Package has Revision=2, SubPackage0 (PL1: 15,000 mW to 55,000 mW), SubPackage1 (PL2: 15,000 mW to 115,000 mW)
            var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Lead-in padding
            writer.Write(new byte[] { 0x08, 0x5C, 0x5F, 0x53, 0x42 }); // NameOp _SB
            
            // "PPCC"
            writer.Write(Encoding.ASCII.GetBytes("PPCC"));

            // AML PackageOp: 0x12
            writer.Write((byte)0x12);
            
            // Outer PkgLength: 59 bytes
            writer.Write((byte)59);
            // NumElements: 3 (Revision, PL1 Package, PL2 Package)
            writer.Write((byte)3);

            // Element 0: Revision (DWord: 0x00000002)
            writer.Write((byte)0x0C); // DWordPrefix
            writer.Write((uint)2);

            // Element 1: PL1 Inner Package
            writer.Write((byte)0x12); // PackageOp
            writer.Write((byte)28);   // PkgLength: 28 bytes
            writer.Write((byte)6);    // NumElements: Index, Min, Max, MinTime, MaxTime, Step
            // [0] Index: 0 (PL1)
            writer.Write((byte)0x00); // ZeroOp
            // [1] MinPower: 15,000 mW (0x00003A98)
            writer.Write((byte)0x0C);
            writer.Write((uint)15000);
            // [2] MaxPower: 55,000 mW (0x0000D6D8)
            writer.Write((byte)0x0C);
            writer.Write((uint)55000);
            // [3] MinTimeWindow: 1,000 ms
            writer.Write((byte)0x0C);
            writer.Write((uint)1000);
            // [4] MaxTimeWindow: 32,000 ms
            writer.Write((byte)0x0C);
            writer.Write((uint)32000);
            // [5] StepSize: 500 mW
            writer.Write((byte)0x0C);
            writer.Write((uint)500);

            // Element 2: PL2 Inner Package
            writer.Write((byte)0x12); // PackageOp
            writer.Write((byte)22);   // PkgLength: 22 bytes
            writer.Write((byte)6);    // NumElements: Index, Min, Max, MinTime, MaxTime, Step
            // [0] Index: 1 (PL2)
            writer.Write((byte)0x01); // OneOp
            // [1] MinPower: 15,000 mW
            writer.Write((byte)0x0C);
            writer.Write((uint)15000);
            // [2] MaxPower: 115,000 mW (0x0001C138)
            writer.Write((byte)0x0C);
            writer.Write((uint)115000);
            // [3] MinTimeWindow: 28 ms
            writer.Write((byte)0x0A);
            writer.Write((byte)28);
            // [4] MaxTimeWindow: 32 ms
            writer.Write((byte)0x0A);
            writer.Write((byte)32);
            // [5] StepSize: 500 mW
            writer.Write((byte)0x0C);
            writer.Write((uint)500);

            byte[] amlBytes = ms.ToArray();

            var parsed = BiosUefiPowerScanner.ParsePpccFromAml(amlBytes);
            Assert.NotNull(parsed);

            var (minW, maxPl1W, maxPl2W, baseTdpW) = parsed.Value;
            Assert.Equal(15, minW);     // 15,000 mW -> 15W
            Assert.Equal(55, maxPl1W);  // 55,000 mW -> 55W
            Assert.Equal(115, maxPl2W); // 115,000 mW -> 115W
            Assert.Equal(55, baseTdpW); // 55W base TDP
        }

        [Fact]
        public void ParsePpccFromAml_WithHeuristicIntegerLayout_RecoversPowerLimits()
        {
            // Synthetic layout with "PPCC" followed directly by DWord constants
            var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(Encoding.ASCII.GetBytes("HEADER_DSDT_TEST"));
            writer.Write(Encoding.ASCII.GetBytes("PPCC"));

            // Write 3 DWord integers within 256 bytes
            writer.Write((byte)0x0C);
            writer.Write((uint)20000); // 20,000 mW -> 20W Min

            writer.Write((byte)0x0C);
            writer.Write((uint)65000); // 65,000 mW -> 65W PL1 Max

            writer.Write((byte)0x0C);
            writer.Write((uint)140000); // 140,000 mW -> 140W PL2 Max

            byte[] amlBytes = ms.ToArray();

            var parsed = BiosUefiPowerScanner.ParsePpccFromAml(amlBytes);
            Assert.NotNull(parsed);

            var (minW, maxPl1W, maxPl2W, _) = parsed.Value;
            Assert.Equal(20, minW);
            Assert.Equal(65, maxPl1W);
            Assert.Equal(140, maxPl2W);
        }

        [Fact]
        public void ParsePpccFromAml_WithCorruptOrMissingData_ReturnsNullWithoutException()
        {
            byte[] empty = Array.Empty<byte>();
            Assert.Null(BiosUefiPowerScanner.ParsePpccFromAml(empty));

            byte[] noPpcc = Encoding.ASCII.GetBytes("SOME_ARBITRARY_ACPI_DATA_WITHOUT_SIGNATURE");
            Assert.Null(BiosUefiPowerScanner.ParsePpccFromAml(noPpcc));

            byte[] truncatedPpcc = Encoding.ASCII.GetBytes("PPC");
            Assert.Null(BiosUefiPowerScanner.ParsePpccFromAml(truncatedPpcc));
        }

        [Fact]
        public void MockBiosUefiPowerScanner_Profiles_ProvideAccurateFactoryBaselines()
        {
            // 1. Triton 28W Profile
            var tritonScanner = MockBiosUefiPowerScanner.CreateTriton28WProfile();
            var tritonRes = tritonScanner.Scan();
            Assert.Equal(28, tritonRes.CpuThermalSpecTdpWatts);
            Assert.Equal(8, tritonRes.CpuMinAllowedPowerWatts);
            Assert.Equal(65, tritonRes.CpuMaxAllowedPl1Watts);
            Assert.Equal(115, tritonRes.CpuMaxAllowedPl2Watts);
            Assert.Equal(15, tritonRes.BiosDefaultEcoPl1Watts);
            Assert.Equal(28, tritonRes.BiosDefaultBalancedPl1Watts);
            Assert.Equal(65, tritonRes.BiosDefaultTurboPl1Watts);

            // 2. Helios Neo 16 45W Profile
            var heliosNeoScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
            var heliosNeoRes = heliosNeoScanner.Scan();
            Assert.Equal(45, heliosNeoRes.CpuThermalSpecTdpWatts);
            Assert.Equal(12, heliosNeoRes.CpuMinAllowedPowerWatts);
            Assert.Equal(115, heliosNeoRes.CpuMaxAllowedPl1Watts);
            Assert.Equal(140, heliosNeoRes.CpuMaxAllowedPl2Watts);
            Assert.Equal(45, heliosNeoRes.BiosDefaultBalancedPl1Watts);

            // 3. Helios 18 HX 55W Profile
            var helios18Scanner = MockBiosUefiPowerScanner.CreateHelios18Hx55WProfile();
            var helios18Res = helios18Scanner.Scan();
            Assert.Equal(55, helios18Res.CpuThermalSpecTdpWatts);
            Assert.Equal(15, helios18Res.CpuMinAllowedPowerWatts);
            Assert.Equal(175, helios18Res.CpuMaxAllowedPl1Watts);
            Assert.Equal(190, helios18Res.CpuMaxAllowedPl2Watts);

            // 4. Nitro 16 AMD Profile
            var nitroScanner = MockBiosUefiPowerScanner.CreateNitro16AmdProfile();
            var nitroRes = nitroScanner.Scan();
            Assert.Equal(45, nitroRes.CpuThermalSpecTdpWatts);
            Assert.Equal(65, nitroRes.CpuMaxAllowedPl1Watts);
            Assert.Equal(80, nitroRes.CpuMaxAllowedPl2Watts);

            // 5. Missing ACPI Table Profile
            var missingAcpiScanner = MockBiosUefiPowerScanner.CreateWithMissingAcpi();
            var missingRes = missingAcpiScanner.Scan();
            Assert.True(missingRes.HasCompletedBiosPowerScan);
            Assert.Equal(45, missingRes.CpuThermalSpecTdpWatts);
        }

        [Fact]
        public async Task MockBiosUefiPowerScanner_ScanAsync_ExecutesAsynchronously()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateTriton28WProfile();
            var res = await mockScanner.ScanAsync();
            Assert.NotNull(res);
            Assert.True(res.HasCompletedBiosPowerScan);
            Assert.Equal("Acer_Predator_PT14-51_V1.08_2024-01-15_Core_Ultra_7_155H", res.BiosHardwareFingerprint);
        }

        [Fact]
        public void BiosPowerScanResult_GetDefaultLimitsForMode_ReturnsCorrectLimitsForAllModes()
        {
            var result = MockBiosUefiPowerScanner.CreateDefaultMockResult(baseTdp: 45, minPower: 12, maxPl1: 115, maxPl2: 140);

            var (ecoPl1, ecoPl2) = result.GetDefaultLimitsForMode(OperatingMode.Eco);
            var (quietPl1, quietPl2) = result.GetDefaultLimitsForMode(OperatingMode.Quiet);
            var (balPl1, balPl2) = result.GetDefaultLimitsForMode(OperatingMode.Balanced);
            var (perfPl1, perfPl2) = result.GetDefaultLimitsForMode(OperatingMode.Performance);
            var (turboPl1, turboPl2) = result.GetDefaultLimitsForMode(OperatingMode.Turbo);

            Assert.True(ecoPl1 <= quietPl1);
            Assert.True(quietPl1 <= balPl1);
            Assert.True(balPl1 <= perfPl1);
            Assert.True(perfPl1 <= turboPl1);

            Assert.True(ecoPl2 >= ecoPl1);
            Assert.True(quietPl2 >= quietPl1);
            Assert.True(balPl2 >= balPl1);
            Assert.True(perfPl2 >= perfPl1);
            Assert.True(turboPl2 >= turboPl1);
        }

        [Fact]
        public void NativeScanner_OnHostMachine_PerformsSafeNativeScan()
        {
            var scanner = new BiosUefiPowerScanner();
            var result = scanner.ScanPowerLimits();

            Assert.NotNull(result);
            Assert.True(result.HasCompletedBiosPowerScan);
            Assert.False(string.IsNullOrWhiteSpace(result.BiosHardwareFingerprint));

            // Validate Identity metadata
            Assert.False(string.IsNullOrWhiteSpace(result.Identity.ProcessorNameString));
            Assert.False(string.IsNullOrWhiteSpace(result.Identity.SystemProductName));

            // Validate hardware limits are non-zero and physically sane
            Assert.True(result.CpuThermalSpecTdpWatts >= 5, "TDP must be at least 5W");
            Assert.True(result.CpuMinAllowedPowerWatts >= 5, "Min power must be at least 5W");
            Assert.True(result.CpuMaxAllowedPl1Watts >= result.CpuMinAllowedPowerWatts, "Max PL1 >= Min Power");
            Assert.True(result.CpuMaxAllowedPl2Watts >= result.CpuMaxAllowedPl1Watts, "Max PL2 >= Max PL1");

            // Validate all 5 operating mode defaults are populated
            Assert.True(result.BiosDefaultEcoPl1Watts > 0);
            Assert.True(result.BiosDefaultQuietPl1Watts > 0);
            Assert.True(result.BiosDefaultBalancedPl1Watts > 0);
            Assert.True(result.BiosDefaultPerformancePl1Watts > 0);
            Assert.True(result.BiosDefaultTurboPl1Watts > 0);

            Assert.Equal(0x1F, result.VerifiedBiosModesBitmask);

            // Validate ACPI tables were discovered (host has ACPI firmware tables)
            Assert.NotEmpty(result.DiscoveredAcpiTables);
        }

        [Fact]
        public void CpuPowerLimitController_InitializeWithBiosScanner_AppliesAndPersistsLimits()
        {
            var settings = new AppSettings();
            var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
            using var mockBackend = new MockPawnIoBackend();
            using var controller = new CpuPowerLimitController(null, settings, mockBackend);

            var scanResult = controller.InitializeWithBiosScanner(mockScanner);

            Assert.NotNull(scanResult);
            Assert.Equal("Acer_Predator_PHN16-71_V1.15_2024-03-12_Core_i7-13700HX", settings.BiosHardwareFingerprint);
            Assert.Equal(45, settings.CpuThermalSpecTdpWatts);
            Assert.Equal(12, settings.CpuMinAllowedPowerWatts);
            Assert.Equal(115, settings.CpuMaxAllowedPl1Watts);
            Assert.Equal(140, settings.CpuMaxAllowedPl2Watts);

            Assert.Equal(45, settings.BiosDefaultBalancedPl1Watts);
            Assert.Equal(75, settings.BiosDefaultBalancedPl2Watts);
            Assert.Equal(105, settings.BiosDefaultTurboPl1Watts);
            Assert.Equal(140, settings.BiosDefaultTurboPl2Watts);

            Assert.True(settings.HasCompletedBiosPowerScan);
            Assert.Equal(0x1F, settings.VerifiedBiosModesBitmask);
        }
    }
}
