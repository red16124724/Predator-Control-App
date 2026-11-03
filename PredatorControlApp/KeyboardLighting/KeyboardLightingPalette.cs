using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PredatorControlApp
{
    /// <summary>
    /// Persisted lighting palette helpers. Colors are stored as compact
    /// #RRGGBB tokens so settings.json stays readable and diffable.
    /// </summary>
    public static class KeyboardLightingPalette
    {
        public static readonly Color DefaultZoneColor = Color.FromArgb(0, 150, 255);
        public static readonly Color Off = Color.Black;

        public static string Serialize(IEnumerable<Color> colors)
        {
            if (colors == null) return string.Empty;
            return string.Join(",", colors.Select(ToHex));
        }

        public static string ToHex(Color color)
            => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        public static Color[] Parse(string? value, int count, Color? fallback = null)
        {
            Color fill = fallback ?? DefaultZoneColor;
            var result = new Color[Math.Max(0, count)];
            for (int i = 0; i < result.Length; i++) result[i] = fill;

            if (string.IsNullOrWhiteSpace(value) || result.Length == 0) return result;

            string[] parts = value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < result.Length && i < parts.Length; i++)
            {
                if (TryParse(parts[i], out Color parsed)) result[i] = parsed;
            }
            return result;
        }

        public static bool TryParse(string? token, out Color color)
        {
            color = DefaultZoneColor;
            if (string.IsNullOrWhiteSpace(token)) return false;

            string text = token.Trim();
            if (text.StartsWith("#", StringComparison.Ordinal)) text = text.Substring(1);

            if (NativeLightingRules.TryParseHex(text, out Color nativeColor))
            {
                color = nativeColor;
                return true;
            }

            if (text.Length == 8 &&
                uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
            {
                color = Color.FromArgb((int)argb);
                return true;
            }

            if (text.Length == 6 &&
                uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                color = Color.FromArgb(255, (int)((rgb >> 16) & 0xFF), (int)((rgb >> 8) & 0xFF), (int)(rgb & 0xFF));
                return true;
            }

            if (text.Length == 3)
            {
                var expanded = new StringBuilder(6);
                foreach (char c in text) { expanded.Append(c); expanded.Append(c); }
                if (uint.TryParse(expanded.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint shortRgb))
                {
                    color = Color.FromArgb(255, (int)((shortRgb >> 16) & 0xFF), (int)((shortRgb >> 8) & 0xFF), (int)(shortRgb & 0xFF));
                    return true;
                }
            }

            try
            {
                Color named = Color.FromName(text);
                if (named.IsKnownColor || named.A != 0)
                {
                    color = named;
                    return true;
                }
            }
            catch { }

            return false;
        }

        public static Color Blend(Color a, Color b, double amount)
        {
            if (NativeLightingRules.TryBlend(a, b, amount, out Color native)) return native;

            double t = Math.Clamp(amount, 0.0, 1.0);
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
    }
}
