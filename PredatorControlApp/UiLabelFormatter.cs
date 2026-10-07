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
        /// Combines base text flags with TextFormatFlags.NoPrefix when useMnemonic is false to prevent mnemonic ampersand underscores.
        /// </summary>
        public static TextFormatFlags GetTextFormatFlags(TextFormatFlags baseFlags = TextFormatFlags.Default, bool useMnemonic = false)
        {
            return useMnemonic ? baseFlags : (baseFlags | TextFormatFlags.NoPrefix);
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
    }
}
