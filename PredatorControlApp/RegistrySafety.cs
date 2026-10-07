using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public static class RegistrySafety
    {
        public const string BaseKeyPath = @"SOFTWARE\PredatorControl";
        public const string ProfilesSubKey = @"SOFTWARE\PredatorControl\Profiles";
        public const string OverridesSubKey = @"SOFTWARE\PredatorControl\Overrides";

        public static string SanitizeSubKeyName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Profile";

            string sanitized = name.Replace('/', '_')
                                   .Replace('\\', '_')
                                   .Replace("..", "_")
                                   .Trim('.', ' ', '\t', '\r', '\n');

            var sb = new System.Text.StringBuilder();
            foreach (char c in sanitized)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ' ' || c == '(' || c == ')')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }

            string result = sb.ToString().Trim();
            if (result.Length == 0) result = "Profile";
            if (result.Length > 250) result = result.Substring(0, 250);
            return result;
        }

        public static bool IsSafePath(string subKeyPath)
        {
            if (string.IsNullOrWhiteSpace(subKeyPath)) return false;
            if (subKeyPath.Contains("..")) return false;
            if (!subKeyPath.Equals(BaseKeyPath, StringComparison.OrdinalIgnoreCase) &&
                !subKeyPath.StartsWith(BaseKeyPath + @"\", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public static RegistryKey? OpenSafeSubKey(RegistryKey root, string relativePath, bool writable = false)
        {
            if (!IsSafePath(relativePath))
                throw new SecurityException($"Registry path '{relativePath}' is outside authorized scope '{BaseKeyPath}'.");

            try
            {
                return root.OpenSubKey(relativePath, writable);
            }
            catch (SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static RegistryKey? CreateSafeSubKey(RegistryKey root, string relativePath)
        {
            if (!IsSafePath(relativePath))
                throw new SecurityException($"Registry path '{relativePath}' is outside authorized scope '{BaseKeyPath}'.");

            try
            {
                return root.CreateSubKey(relativePath);
            }
            catch (SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static void SaveModeKeyAction(int action)
        {
            int safeAction = Math.Clamp(action, 0, 1);
            try
            {
                using var hkcu = CreateSafeSubKey(Registry.CurrentUser, BaseKeyPath);
                hkcu?.SetValue("ModeKeyAction", safeAction, RegistryValueKind.DWord);
            }
            catch { }

            try
            {
                using var hklm = CreateSafeSubKey(Registry.LocalMachine, BaseKeyPath);
                hklm?.SetValue("ModeKeyAction", safeAction, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static int LoadModeKeyAction(int fallback = 0)
        {
            try
            {
                using var hkcu = OpenSafeSubKey(Registry.CurrentUser, BaseKeyPath);
                if (hkcu?.GetValue("ModeKeyAction") is int val)
                    return Math.Clamp(val, 0, 1);

                using var hklm = OpenSafeSubKey(Registry.LocalMachine, BaseKeyPath);
                if (hklm?.GetValue("ModeKeyAction") is int valHklm)
                    return Math.Clamp(valHklm, 0, 1);
            }
            catch { }
            return Math.Clamp(fallback, 0, 1);
        }

        public static void SaveGameProfile(GameProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.ExecutableName)) return;
            string keyName = SanitizeSubKeyName(profile.ExecutableName);
            string profilePath = $@"{ProfilesSubKey}\{keyName}";

            try
            {
                using var key = CreateSafeSubKey(Registry.CurrentUser, profilePath);
                if (key == null) return;

                key.SetValue("ExecutableName", profile.ExecutableName);
                if (!string.IsNullOrEmpty(profile.ExecutablePath))
                    key.SetValue("ExecutablePath", profile.ExecutablePath);
                key.SetValue("DisplayName", profile.DisplayName ?? "");
                key.SetValue("PowerMode", (int)profile.PowerMode);
                key.SetValue("FanMode", (int)profile.FanMode);
                key.SetValue("CpuFanSpeed", profile.CpuFanSpeed);
                key.SetValue("GpuFanSpeed", profile.GpuFanSpeed);
                key.SetValue("SysFanSpeed", profile.SysFanSpeed);
                key.SetValue("RefreshRate", profile.RefreshRate);
                key.SetValue("BatteryLimit", profile.BatteryLimit);
                key.SetValue("RgbMode", profile.RgbMode);
                key.SetValue("RgbBrightness", profile.RgbBrightness);
                key.SetValue("RgbSpeed", profile.RgbSpeed);
                key.SetValue("RgbR", profile.RgbR);
                key.SetValue("RgbG", profile.RgbG);
                key.SetValue("RgbB", profile.RgbB);
            }
            catch { }
        }

        public static List<GameProfile> LoadGameProfiles()
        {
            var list = new List<GameProfile>();
            try
            {
                using var root = OpenSafeSubKey(Registry.CurrentUser, ProfilesSubKey);
                if (root == null) return list;

                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var key = root.OpenSubKey(subName);
                        if (key == null) continue;

                        string exe = key.GetValue("ExecutableName") as string ?? "";
                        if (string.IsNullOrWhiteSpace(exe)) continue;

                        var profile = new GameProfile
                        {
                            ExecutableName = exe,
                            ExecutablePath = key.GetValue("ExecutablePath") as string ?? "",
                            DisplayName = key.GetValue("DisplayName") as string ?? "",
                            PowerMode = (byte)(key.GetValue("PowerMode") is int pm ? pm : 0x01),
                            FanMode = (byte)(key.GetValue("FanMode") is int fm ? fm : 0x01),
                            CpuFanSpeed = key.GetValue("CpuFanSpeed") is int cfs ? cfs : -1,
                            GpuFanSpeed = key.GetValue("GpuFanSpeed") is int gfs ? gfs : -1,
                            SysFanSpeed = key.GetValue("SysFanSpeed") is int sfs ? sfs : -1,
                            RefreshRate = key.GetValue("RefreshRate") is int rr ? rr : -1,
                            BatteryLimit = key.GetValue("BatteryLimit") is int bl ? bl : -1,
                            RgbMode = key.GetValue("RgbMode") is int rm ? rm : -1,
                            RgbBrightness = key.GetValue("RgbBrightness") is int rb ? rb : -1,
                            RgbSpeed = key.GetValue("RgbSpeed") is int rs ? rs : -1,
                            RgbR = key.GetValue("RgbR") is int r ? r : -1,
                            RgbG = key.GetValue("RgbG") is int g ? g : -1,
                            RgbB = key.GetValue("RgbB") is int b ? b : -1,
                        };

                        if (profile.Validate(out _))
                        {
                            profile.Sanitize();
                            list.Add(profile);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }
    }
}
