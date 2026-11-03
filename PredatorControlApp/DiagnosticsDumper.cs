using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PredatorControlApp
{
    public static class DiagnosticsDumper
    {
        public static string GenerateReport(
            WmiController wmi,
            DeviceCapabilities caps,
            (double? Cpu, double? Gpu) pdhLoad,
            bool gpuAsleep,
            int? thirdFanRpm = null,
            BiosPowerScanResult? biosScan = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=================================================");
            sb.AppendLine("   PREDATOR CONTROL APP - DIAGNOSTIC SYSTEM DUMP  ");
            sb.AppendLine($"   Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (Local)");
            sb.AppendLine("=================================================");
            sb.AppendLine();

            // 1. Hardware Identity
            sb.AppendLine("[HARDWARE IDENTITY & FIRMWARE]");
            string model = HardwareIdentity.ReadModel();
            string bios = HardwareIdentity.ReadBiosVersion();
            string sn = HardwareIdentity.ReadSerialNumber();
            string? snid = HardwareIdentity.ComputeSnid(sn);
            var catalogProfile = AcerLaptopModelCatalog.Lookup(model);

            sb.AppendLine($"Model:             {model}");
            sb.AppendLine($"BIOS Version:      {bios}");
            sb.AppendLine($"Serial Number:     {sn}");
            sb.AppendLine($"Acer SNID:         {snid ?? "N/A"}");
            sb.AppendLine($"Catalog Profile:   {catalogProfile.MarketingName} (Launch {catalogProfile.LaunchYear})");
            sb.AppendLine($"Hardware Lighting: {catalogProfile.DefaultLightingTier} ({catalogProfile.ZoneCount} Zones)");
            sb.AppendLine($"Fan Topology:      {catalogProfile.FanTopology}");
            sb.AppendLine();

            // 2. SMBIOS
            sb.AppendLine("[SMBIOS LOW-LEVEL ACER INTERFACES]");
            var smbios = AcerSmbios.Read();
            sb.AppendLine($"Gaming Version (0xAC): {(smbios.GamingVersion.HasValue ? smbios.GamingVersion.Value.ToString("0.00", CultureInfo.InvariantCulture) : "Not Present")}");
            sb.AppendLine($"Gaming Records (0xAC): {string.Join(" ", smbios.GamingRecords.Select(r => $"[{r.Id:X2}={r.Value:X}]"))}");
            sb.AppendLine($"Hotkey Functions (0xAA): {string.Join(" ", smbios.HotkeyFunctions.Select(r => $"[{r.Id:X2}={r.Value:X}]"))}");
            sb.AppendLine();

            // 3. Probed Capabilities
            sb.AppendLine("[HARDWARE CAPABILITIES & FEATURES]");
            sb.AppendLine($"Direct EC HID:          {caps.HasEcHid}");
            sb.AppendLine($"Operating Modes:        {string.Join(", ", caps.OperatingModes)}");
            sb.AppendLine($"CoolBoost Supported:    {caps.CoolBoost}");
            sb.AppendLine($"Factory Fan Tables:     {caps.FanTable}");
            sb.AppendLine($"MUX Switch / GPU Mode:  {caps.GpuModeSwitch}");
            sb.AppendLine($"Power-Off USB Charging: {caps.UsbCharging}");
            sb.AppendLine($"Battery Health Control: {caps.BatteryHealth}");
            sb.AppendLine($"Battery Calibration:    {caps.BatteryCalibration}");
            sb.AppendLine($"3rd / System Fan:       {caps.HasThirdFan}");
            sb.AppendLine($"Dedicated Mode Key:     {caps.ModeKey}");
            sb.AppendLine();

            // 4. Live Sensor Telemetry
            sb.AppendLine("[LIVE TELEMETRY]");
            int cpuTemp = wmi.CpuTemp;
            int gpuTemp = gpuAsleep ? 0 : wmi.GpuTemp;
            int cpuRpm = wmi.CpuFanRpm;
            int gpuRpm = gpuAsleep ? 0 : wmi.GpuFanRpm;

            sb.AppendLine($"CPU Temperature:   {(cpuTemp > 0 ? $"{cpuTemp}°C" : "N/A")}");
            sb.AppendLine($"GPU Temperature:   {(gpuAsleep ? "Asleep (D3Cold)" : (gpuTemp > 0 ? $"{gpuTemp}°C" : "N/A"))}");
            sb.AppendLine($"CPU Fan Tach:      {(cpuRpm > 0 ? $"{cpuRpm} RPM" : "N/A")}");
            sb.AppendLine($"GPU Fan Tach:      {(gpuRpm > 0 ? $"{gpuRpm} RPM" : "N/A")}");
            if (caps.HasThirdFan)
                sb.AppendLine($"System Fan Tach:   {(thirdFanRpm.HasValue && thirdFanRpm.Value > 0 ? $"{thirdFanRpm.Value} RPM" : "N/A")}");

            sb.AppendLine($"CPU Load (PDH):    {(pdhLoad.Cpu.HasValue ? $"{pdhLoad.Cpu.Value:F1}%" : "N/A")}");
            sb.AppendLine($"GPU 3D Load (PDH): {(pdhLoad.Gpu.HasValue ? $"{pdhLoad.Gpu.Value:F1}%" : "N/A")}");
            sb.AppendLine($"GPU Power State:   {(gpuAsleep ? "D3Cold / Sleep" : "D0 / Active")}");
            sb.AppendLine();

            // 5. Battery Telemetry
            sb.AppendLine("[BATTERY TELEMETRY & WEAR]");
            var batt = WindowsBattery.Read();
            if (batt != null)
            {
                sb.AppendLine($"Device Name:       {batt.Name ?? "Standard Battery"}");
                sb.AppendLine($"Manufacturer:      {batt.Manufacturer ?? "OEM"}");
                sb.AppendLine($"Designed Capacity: {batt.DesignCapacityMWh} mWh");
                sb.AppendLine($"Full Charge Cap:   {batt.FullChargeCapacityMWh} mWh");
                sb.AppendLine($"Cycle Count:       {(batt.CycleCount.HasValue ? batt.CycleCount.Value.ToString() : "N/A")}");
                sb.AppendLine($"Battery Wear Level:{batt.WearLevelPercent:F1}%");
                sb.AppendLine($"Hardware Calib:    {(batt.CalibrationActive ? "Active" : "Idle")}");
            }
            else
            {
                sb.AppendLine("Battery Information: Not available or AC only system.");
            }
            sb.AppendLine();

            // 6. USB Charging & Misc State
            sb.AppendLine("[MISCELLANEOUS HARDWARE CONFIGURATION]");
            var usb = wmi.GetUsbCharging();
            sb.AppendLine($"USB Power-Off Charge: {(usb != null ? $"Enabled={usb.Enabled}, Floor={usb.FloorPercent ?? 30}%" : "N/A")}");
            var cool = wmi.GetCoolBoost();
            sb.AppendLine($"CoolBoost Active:     {(cool.HasValue ? cool.Value.ToString() : "N/A")}");
            var gpuM = wmi.GetGpuMode();
            sb.AppendLine($"Current GPU Mode:     {(gpuM.HasValue ? gpuM.Value.ToString() : "Hybrid")}");
            var fanT = wmi.GetFanTable();
            sb.AppendLine($"Active Fan Table:     {(fanT.HasValue ? fanT.Value.ToString() : "OEM Default")}");
            sb.AppendLine();

            // 7. Native BIOS/UEFI & ACPI WMI Power Limits & PawnIO Diagnostics
            sb.AppendLine("[NATIVE BIOS/UEFI & ACPI WMI POWER SCAN REPORT]");
            var scan = biosScan ?? caps.BiosPowerScan;
            string fingerprint = !string.IsNullOrWhiteSpace(caps.BiosHardwareFingerprint)
                ? caps.BiosHardwareFingerprint
                : (scan?.BiosHardwareFingerprint ?? (caps.Settings?.ScannedHardwareSignature ?? "Scanned Acer BIOS/UEFI"));
            bool scanCompleted = scan?.HasCompletedBiosPowerScan ?? (caps.Settings?.HasCompletedBiosPowerScan ?? true);

            sb.AppendLine($"BIOS Fingerprint:      {fingerprint}");
            sb.AppendLine($"BIOS Scan Completed:   {scanCompleted}");
            sb.AppendLine($"CPU Model:             {caps.CpuModel}");
            sb.AppendLine($"CPU Architecture:      {caps.Architecture}");
            sb.AppendLine($"Rated CPU TDP:         {caps.CpuTdpWatts} W");
            sb.AppendLine($"CPU Min Allowed Power: {caps.CpuMinAllowedPowerWatts} W");
            sb.AppendLine($"CPU Max Allowed PL1:   {caps.CpuMaxAllowedPl1Watts} W");
            sb.AppendLine($"CPU Max Allowed PL2:   {caps.CpuMaxAllowedPl2Watts} W");
            sb.AppendLine($"Scanned Bounds Header: {UiLabelFormatter.FormatCpuPowerCapabilitiesHeader(caps.CpuMinAllowedPowerWatts, caps.CpuThermalSpecTdpWatts, caps.CpuMaxAllowedPl1Watts, caps.CpuMaxAllowedPl2Watts)}");
            sb.AppendLine("Native BIOS Mode Defaults (PL1 / PL2):");
            sb.AppendLine($"  - Eco (0x06):         PL1 = {caps.GetScannedPl1(0x06)} W, PL2 = {caps.GetScannedPl2(0x06)} W");
            sb.AppendLine($"  - Quiet (0x00):       PL1 = {caps.GetScannedPl1(0x00)} W, PL2 = {caps.GetScannedPl2(0x00)} W");
            sb.AppendLine($"  - Balanced (0x01):    PL1 = {caps.GetScannedPl1(0x01)} W, PL2 = {caps.GetScannedPl2(0x01)} W");
            sb.AppendLine($"  - Performance (0x04): PL1 = {caps.GetScannedPl1(0x04)} W, PL2 = {caps.GetScannedPl2(0x04)} W");
            sb.AppendLine($"  - Turbo (0x05):       PL1 = {caps.GetScannedPl1(0x05)} W, PL2 = {caps.GetScannedPl2(0x05)} W");
            if (scan != null)
            {
                sb.AppendLine($"Verified Modes Mask:   0x{scan.VerifiedBiosModesBitmask:X2}");
                if (scan.DiscoveredAcpiTables.Count > 0)
                {
                    sb.AppendLine($"Discovered ACPI Tables:{string.Join(", ", scan.DiscoveredAcpiTables.Keys)}");
                }
            }
            sb.AppendLine($"PawnIO Available:      {caps.PawnIoAvailable}");
            sb.AppendLine($"PawnIO Installed:      {caps.PawnIoInstalled}");
            sb.AppendLine($"Requires Elevation:    {caps.PawnIoRequiresAdmin}");
            sb.AppendLine($"PawnIO Diagnostics:    {caps.PawnIoDiagnosticMessage}");
            sb.AppendLine($"MSR 0x610 Lock Bit:    {(caps.IsMsr0x610Locked ? "Locked (Bit 63 set)" : "Unlocked")}");
            sb.AppendLine();

            sb.AppendLine("=================================================");
            sb.AppendLine("             END OF DIAGNOSTIC REPORT            ");
            sb.AppendLine("=================================================");

            return sb.ToString();
        }

        public static bool CopyToClipboard(string report)
        {
            try
            {
                if (string.IsNullOrEmpty(report)) return false;
                Clipboard.SetText(report);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
