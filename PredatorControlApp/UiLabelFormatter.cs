using System.Windows.Forms;
using System;
using System.Text.RegularExpressions;

namespace PredatorControlApp
{
    public static class UiLabelFormatter
    {
        private static readonly Regex UnderscoreRegex = new(@"_+", RegexOptions.Compiled);
        private static readonly Regex ExtraSpaceRegex = new(@"[ ]{2,}", RegexOptions.Compiled);

        /// <summary>
        /// Replaces all raw underscores ('_') with clean spaces (' ') and collapses redundant consecutive spaces.
        /// </summary>
        public static string CleanUnderscores(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            string replaced = UnderscoreRegex.Replace(text, " ");
            return ExtraSpaceRegex.Replace(replaced, " ").Trim();
        }

        /// <summary>
        /// Replaces raw underscores with clean spaces and translates internal coding jargon,
        /// abbreviations, and hardware code words into clear, natural user-friendly language.
        /// </summary>
        public static string FormatLabel(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            string cleaned = CleanUnderscores(text);

            // Replace coding terms and abbreviations with user-friendly language
            cleaned = cleaned
                .Replace("D3Cold", "Sleep")
                .Replace("d3cold", "sleep")
                .Replace("dGPU", "Dedicated Graphics")
                .Replace("DGPU", "Dedicated Graphics")
                .Replace("iGPU", "Integrated Graphics")
                .Replace("IGPU", "Integrated Graphics")
                .Replace("MUX Switch", "Graphics Mode Switch")
                .Replace("MUX switch", "graphics mode switch")
                .Replace("Direct EC HID", "Direct Hardware Controller")
                .Replace("Acer ACPI WMI", "Acer System Management Driver")
                .Replace("Acer WMI", "Acer System Driver")
                .Replace("EC HID", "Direct Hardware")
                .Replace("FanLock", "Fan Lock")
                .Replace("OEM LOOKUP TABLES", "HARDWARE PRESETS")
                .Replace("OEM Lookup Tables", "Hardware Presets")
                .Replace("EC Fan Table", "Fan Speed Table")
                .Replace("EC Curve", "Preset Fan Curve")
                .Replace("TurboToggle", "Turbo Toggle");

            return cleaned;
        }

        /// <summary>
        /// Formats button text to ensure natural language with no coding jargon or technical abbreviations.
        /// </summary>
        public static string FormatButton(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            string cleaned = CleanUnderscores(text);

            // Check exact standalone button labels
            if (string.Equals(cleaned, "Perf", StringComparison.OrdinalIgnoreCase))
                return "Performance";
            if (string.Equals(cleaned, "Bal", StringComparison.OrdinalIgnoreCase))
                return "Balanced";
            if (string.Equals(cleaned, "🔓 Lock SYS", StringComparison.OrdinalIgnoreCase))
                return "🔓 Lock System";
            if (string.Equals(cleaned, "🔒 SYS Locked", StringComparison.OrdinalIgnoreCase))
                return "🔒 System Locked";

            return FormatLabel(cleaned);
        }

        /// <summary>
        /// Formats temperature hysteresis degrees into a user-friendly label.
        /// Returns "Hysteresis: 0°C (Instant)" when 0, and "Hysteresis: {degrees}°C" otherwise.
        /// </summary>
        public static string FormatHysteresisLabel(int degrees)
        {
            return degrees == 0 ? "Hysteresis: 0°C (Instant)" : $"Hysteresis: {degrees}°C";
        }

        /// <summary>
        /// Combines base text flags with TextFormatFlags.NoPrefix when useMnemonic is false to prevent mnemonic ampersand underscores.
        /// </summary>
        public static TextFormatFlags GetTextFormatFlags(TextFormatFlags baseFlags = TextFormatFlags.Default, bool useMnemonic = false)
        {
            return useMnemonic ? baseFlags : (baseFlags | TextFormatFlags.NoPrefix);
        }

        /// <summary>
        /// Formats battery calibration remaining time ETA.
        /// When eta is null, returns "Calculating remaining time...".
        /// When charging, returns "Estimated Time Remaining (Charging): {hh}h {mm}m".
        /// When discharging, returns "Estimated Time Remaining (Discharging): {hh}h {mm}m".
        /// </summary>
        public static string FormatCalibrationEta(TimeSpan? eta, bool isCharging)
        {
            if (!eta.HasValue || eta.Value < TimeSpan.Zero)
            {
                return "Calculating remaining time...";
            }

            var ts = eta.Value;
            int totalHours = (int)ts.TotalHours;
            int mins = ts.Minutes;

            string phasePrefix = isCharging ? "Estimated Time Remaining (Charging)" : "Estimated Time Remaining (Discharging)";
            return $"{phasePrefix}: {totalHours:D2}h {mins:D2}m";
        }

        /// <summary>
        /// Formats last calibrated timestamp from UTC ticks.
        /// Returns "Last Calibrated: Never" if ticks <= 0.
        /// Otherwise returns "Last Calibrated: {yyyy-MM-dd HH:mm}".
        /// </summary>
        public static string FormatLastCalibrated(long utcTicks)
        {
            if (utcTicks <= 0) return "Last Calibrated: Never";
            try
            {
                var dt = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();
                return $"Last Calibrated: {dt:yyyy-MM-dd HH:mm}";
            }
            catch
            {
                return "Last Calibrated: Never";
            }
        }

        /// <summary>
        /// Disables mnemonic processing on supported WinForms controls so ampersands ('&') render as literal characters without underscores.
        /// </summary>
        public static T DisableMnemonic<T>(T control) where T : Control
        {
            if (control is Label lbl) lbl.UseMnemonic = false;
            else if (control is Button btn) btn.UseMnemonic = false;
            else if (control is PredatorButton pb) pb.UseMnemonic = false;
            else if (control is PredatorDropDown pdd) pdd.UseMnemonic = false;
            return control;
        }

        /// <summary>
        /// Formats PL1 label with user watts and laptop default watts.
        /// </summary>
        public static string FormatPl1Label(int watts, int defaultWatts, bool isScanned)
        {
            string defaultPart = isScanned && defaultWatts > 0 ? $" (Laptop Default: {defaultWatts} W)" : (defaultWatts > 0 ? $" (Default: ~{defaultWatts} W)" : "");
            return $"PL1 (Sustained): {watts} W{defaultPart}";
        }

        /// <summary>
        /// Formats PL2 label with user watts and laptop default watts.
        /// </summary>
        public static string FormatPl2Label(int watts, int defaultWatts, bool isScanned)
        {
            string defaultPart = isScanned && defaultWatts > 0 ? $" (Laptop Default: {defaultWatts} W)" : (defaultWatts > 0 ? $" (Default: ~{defaultWatts} W)" : "");
            return $"PL2 (Turbo Burst): {watts} W{defaultPart}";
        }

        /// <summary>
        /// Formats summary of scanned hardware power limits and detected CPU TDP.
        /// </summary>
        public static string FormatHardwarePowerBoundsSummary(int minW, int maxPl1W, int maxPl2W, int tdpW, string cpuModel)
        {
            string cpuPart = string.IsNullOrWhiteSpace(cpuModel) ? "" : $" [{cpuModel}]";
            return $"Scanned Device Limits: Min {minW}W | Max PL1 {maxPl1W}W | Max PL2 {maxPl2W}W (CPU TDP {tdpW}W){cpuPart}";
        }

        /// <summary>
        /// Formats reset button text with default PL1/PL2 values.
        /// </summary>
        public static string FormatResetButtonLabel(int defaultPl1, int defaultPl2)
        {
            if (defaultPl1 > 0 && defaultPl2 > 0)
                return $"Reset to Laptop Default ({defaultPl1}W / {defaultPl2}W)";
            return "Reset to Laptop Default";
        }
        /// <summary>
        /// Formats PL1 label with active watts and scanned native BIOS default watts.
        /// </summary>
        public static string FormatPl1Label(int activeWatts, int biosDefaultWatts)
        {
            string biosPart = biosDefaultWatts > 0 ? $"  [BIOS Default: {biosDefaultWatts} W]" : "";
            return $"PL1 (Sustained): {activeWatts} W{biosPart}";
        }

        /// <summary>
        /// Formats PL2 label with active watts and scanned native BIOS default watts.
        /// </summary>
        public static string FormatPl2Label(int activeWatts, int biosDefaultWatts)
        {
            string biosPart = biosDefaultWatts > 0 ? $"  [BIOS Default: {biosDefaultWatts} W]" : "";
            return $"PL2 (Turbo Burst): {activeWatts} W{biosPart}";
        }

        /// <summary>
        /// Formats header summary of scanned native BIOS/UEFI power capabilities and CPU bounds.
        /// </summary>
        public static string FormatCpuPowerCapabilitiesHeader(int minW, int tdpW, int maxPl1W, int maxPl2W)
        {
            return $"BIOS/UEFI Scanned Limits: Min {minW}W | Base TDP {tdpW}W | Max PL1 {maxPl1W}W | Max PL2 {maxPl2W}W";
        }

        /// <summary>
        /// Formats reset button text with operating mode and scanned BIOS default PL1/PL2 values.
        /// </summary>
        public static string FormatResetToBiosDefaultButton(OperatingMode mode, int biosPl1, int biosPl2)
        {
            if (biosPl1 > 0 && biosPl2 > 0)
                return $"Reset {mode} to BIOS Default ({biosPl1}W / {biosPl2}W)";
            return $"Reset {mode} to BIOS Default";
        }
    }
}
