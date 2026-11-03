using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace PredatorControlApp
{
    internal static class NativeLightingRules
    {
        private const string Library = "predator_core.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_lighting_service_level(int value, int isSpeed);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_lighting_select_tier(int tierOverride, int perKey, int three, int single, int service, int wmi);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_lighting_parse_hex(byte[] text, UIntPtr length, out uint argb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_lighting_blend(uint a, uint b, double amount, out uint argb);

        internal static bool TryServiceLevel(int value, bool isSpeed, out int result)
        {
            result = 0;
            try
            {
                if (!PredatorNativeCore.IsNativeCoreAvailable) return false;
                int native = predator_lighting_service_level(value, isSpeed ? 1 : 0);
                if (native < 0 || native > 5 || (isSpeed && native == 0)) return false;
                result = native;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
        }

        internal static bool TrySelectTier(int tierOverride, bool perKey, bool three, bool single, bool service, bool wmi, out KeyboardLightingTier tier)
        {
            tier = default;
            try
            {
                if (!PredatorNativeCore.IsNativeCoreAvailable) return false;
                int value = predator_lighting_select_tier(tierOverride, perKey ? 1 : 0, three ? 1 : 0, single ? 1 : 0, service ? 1 : 0, wmi ? 1 : 0);
                if (value < 0 || value > 4) return false;
                tier = (KeyboardLightingTier)value;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
        }

        internal static bool TryParseHex(string value, out Color color)
        {
            color = default;
            try
            {
                if (!PredatorNativeCore.IsNativeCoreAvailable || value.Length is not (3 or 6 or 8)) return false;
                byte[] text = Encoding.UTF8.GetBytes(value);
                if (text.Length != value.Length || predator_lighting_parse_hex(text, (UIntPtr)text.Length, out uint argb) != 1) return false;
                color = Color.FromArgb(unchecked((int)argb));
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
        }

        internal static bool TryBlend(Color a, Color b, double amount, out Color color)
        {
            color = default;
            try
            {
                if (!PredatorNativeCore.IsNativeCoreAvailable || !double.IsFinite(amount)) return false;
                if (predator_lighting_blend(unchecked((uint)a.ToArgb()), unchecked((uint)b.ToArgb()), amount, out uint argb) != 1) return false;
                color = Color.FromArgb(unchecked((int)argb));
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
        }
    }
}
