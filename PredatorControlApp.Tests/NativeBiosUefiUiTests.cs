using System;
using System.Drawing;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class NativeBiosUefiUiTests
    {
        [Theory]
        [InlineData(45, 45, "PL1 (Sustained): 45 W  [BIOS Default: 45 W]")]
        [InlineData(55, 45, "PL1 (Sustained): 55 W  [BIOS Default: 45 W]")]
        [InlineData(35, 45, "PL1 (Sustained): 35 W  [BIOS Default: 45 W]")]
        [InlineData(45, 0, "PL1 (Sustained): 45 W")]
        [InlineData(45, -1, "PL1 (Sustained): 45 W")]
        public void UiLabelFormatter_FormatPl1Label_BiosDefault(int activeWatts, int biosDefaultWatts, string expected)
        {
            string actual = UiLabelFormatter.FormatPl1Label(activeWatts, biosDefaultWatts);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(65, 65, "PL2 (Turbo Burst): 65 W  [BIOS Default: 65 W]")]
        [InlineData(90, 65, "PL2 (Turbo Burst): 90 W  [BIOS Default: 65 W]")]
        [InlineData(45, 65, "PL2 (Turbo Burst): 45 W  [BIOS Default: 65 W]")]
        [InlineData(65, 0, "PL2 (Turbo Burst): 65 W")]
        [InlineData(65, -1, "PL2 (Turbo Burst): 65 W")]
        public void UiLabelFormatter_FormatPl2Label_BiosDefault(int activeWatts, int biosDefaultWatts, string expected)
        {
            string actual = UiLabelFormatter.FormatPl2Label(activeWatts, biosDefaultWatts);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(12, 45, 115, 140, "BIOS/UEFI Scanned Limits: Min 12W | Base TDP 45W | Max PL1 115W | Max PL2 140W")]
        [InlineData(8, 28, 65, 115, "BIOS/UEFI Scanned Limits: Min 8W | Base TDP 28W | Max PL1 65W | Max PL2 115W")]
        [InlineData(15, 55, 125, 175, "BIOS/UEFI Scanned Limits: Min 15W | Base TDP 55W | Max PL1 125W | Max PL2 175W")]
        public void UiLabelFormatter_FormatCpuPowerCapabilitiesHeader_FormatsExpected(int minW, int tdpW, int maxPl1W, int maxPl2W, string expected)
        {
            string actual = UiLabelFormatter.FormatCpuPowerCapabilitiesHeader(minW, tdpW, maxPl1W, maxPl2W);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(OperatingMode.Eco, 25, 35, "Reset Eco to BIOS Default (25W / 35W)")]
        [InlineData(OperatingMode.Quiet, 35, 45, "Reset Quiet to BIOS Default (35W / 45W)")]
        [InlineData(OperatingMode.Balanced, 45, 65, "Reset Balanced to BIOS Default (45W / 65W)")]
        [InlineData(OperatingMode.Performance, 70, 90, "Reset Performance to BIOS Default (70W / 90W)")]
        [InlineData(OperatingMode.Turbo, 90, 115, "Reset Turbo to BIOS Default (90W / 115W)")]
        [InlineData(OperatingMode.Balanced, 0, 0, "Reset Balanced to BIOS Default")]
        public void UiLabelFormatter_FormatResetToBiosDefaultButton_FormatsExpected(OperatingMode mode, int pl1, int pl2, string expected)
        {
            string actual = UiLabelFormatter.FormatResetToBiosDefaultButton(mode, pl1, pl2);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ThemeManager_OperatingModeOverloads_ReturnExpectedColorsAndHex()
        {
            Assert.Equal(Color.FromArgb(255, 255, 255), ThemeManager.GetPowerModeColor(OperatingMode.Quiet));
            Assert.Equal(Color.FromArgb(0, 150, 255), ThemeManager.GetPowerModeColor(OperatingMode.Balanced));
            Assert.Equal(Color.FromArgb(168, 85, 247), ThemeManager.GetPowerModeColor(OperatingMode.Performance));
            Assert.Equal(Color.FromArgb(255, 45, 135), ThemeManager.GetPowerModeColor(OperatingMode.Turbo));
            Assert.Equal(Color.FromArgb(0, 255, 120), ThemeManager.GetPowerModeColor(OperatingMode.Eco));

            Assert.Equal("#FFFFFF", ThemeManager.GetPowerModePillColorHex(OperatingMode.Quiet));
            Assert.Equal("#0096FF", ThemeManager.GetPowerModePillColorHex(OperatingMode.Balanced));
            Assert.Equal("#A855F7", ThemeManager.GetPowerModePillColorHex(OperatingMode.Performance));
            Assert.Equal("#FF2D87", ThemeManager.GetPowerModePillColorHex(OperatingMode.Turbo));
            Assert.Equal("#00FF78", ThemeManager.GetPowerModePillColorHex(OperatingMode.Eco));
        }

        [Fact]
        public void DiagnosticsDumper_IncludesFullNativeBiosScanReportAcrossAll5Modes()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
            var scanResult = mockScanner.ScanPowerLimits();

            var caps = new DeviceCapabilities
            {
                CpuModel = "13th Gen Intel Core i7-13700HX",
                Architecture = "Intel Raptor Lake-HX",
                CpuTdpWatts = 45,
                ScannedMinPowerWatts = 12,
                ScannedMaxPl1Watts = 115,
                ScannedMaxPl2Watts = 140,
                BiosHardwareFingerprint = scanResult.BiosHardwareFingerprint,
                BiosPowerScan = scanResult
            };

            using var wmi = new WmiController();
            string report = DiagnosticsDumper.GenerateReport(
                wmi,
                caps,
                (Cpu: 15.2, Gpu: 3.5),
                gpuAsleep: false,
                thirdFanRpm: null,
                biosScan: scanResult);

            Assert.Contains("[NATIVE BIOS/UEFI & ACPI WMI POWER SCAN REPORT]", report);
            Assert.Contains("Acer_Predator_PHN16-71_V1.15_2024-03-12_Core_i7-13700HX", report);
            Assert.Contains("Eco (0x06):", report);
            Assert.Contains("Quiet (0x00):", report);
            Assert.Contains("Balanced (0x01):", report);
            Assert.Contains("Performance (0x04):", report);
            Assert.Contains("Turbo (0x05):", report);
            Assert.Contains("Scanned Bounds Header:", report);
            Assert.Contains("BIOS/UEFI Scanned Limits: Min 12W | Base TDP 45W | Max PL1 115W | Max PL2 140W", report);
        }

        [Fact]
        public void DeviceCapabilities_BiosScanResult_IntegratedInProperties()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateTriton28WProfile();
            var scan = mockScanner.ScanPowerLimits();

            var caps = new DeviceCapabilities
            {
                CpuTdpWatts = scan.CpuThermalSpecTdpWatts,
                ScannedMinPowerWatts = scan.CpuMinAllowedPowerWatts,
                ScannedMaxPl1Watts = scan.CpuMaxAllowedPl1Watts,
                ScannedMaxPl2Watts = scan.CpuMaxAllowedPl2Watts,
                BiosHardwareFingerprint = scan.BiosHardwareFingerprint,
                BiosPowerScan = scan
            };

            Assert.Equal(28, caps.CpuThermalSpecTdpWatts);
            Assert.Equal(8, caps.CpuMinAllowedPowerWatts);
            Assert.Equal(65, caps.CpuMaxAllowedPl1Watts);
            Assert.Equal(115, caps.CpuMaxAllowedPl2Watts);

            // Test 5 mode lookups from BiosPowerScan
            Assert.Equal(15, caps.GetScannedPl1(0x06)); // Eco
            Assert.Equal(20, caps.GetScannedPl2(0x06));
            Assert.Equal(20, caps.GetScannedPl1(0x00)); // Quiet
            Assert.Equal(30, caps.GetScannedPl2(0x00));
            Assert.Equal(28, caps.GetScannedPl1(0x01)); // Balanced
            Assert.Equal(45, caps.GetScannedPl2(0x01));
            Assert.Equal(45, caps.GetScannedPl1(0x04)); // Performance
            Assert.Equal(65, caps.GetScannedPl2(0x04));
            Assert.Equal(65, caps.GetScannedPl1(0x05)); // Turbo
            Assert.Equal(115, caps.GetScannedPl2(0x05));
        }
    }
}
