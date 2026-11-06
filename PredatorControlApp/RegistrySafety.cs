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
        public static readonly object RegistryLock = new();
        private static object _regLock => RegistryLock;

        /// <summary>
        /// Explicit thread-safe registry flush ensuring all pending writes under BaseKeyPath are flushed to disk.
        /// </summary>
        public static void Flush()
        {
            lock (RegistryLock)
            {
                try
                {
                    using var hkcu = OpenSafeSubKey(Registry.CurrentUser, BaseKeyPath, writable: true);
                    hkcu?.Flush();
                }
                catch { }

                try
                {
                    using var hklm = OpenSafeSubKey(Registry.LocalMachine, BaseKeyPath, writable: true);
                    hklm?.Flush();
                }
                catch { }
            }
        }

        /// <summary>
        /// Thread-safe flush on a specific registry key under RegistryLock.
        /// </summary>
        public static void Flush(RegistryKey? key)
        {
            if (key == null) return;
            lock (RegistryLock)
            {
                try
                {
                    key.Flush();
                }
                catch { }
            }
        }

        public static string SanitizeSubKeyName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Profile";

            string sanitized = name.Replace('/', '_')
                                   .Replace('\\', '_');
            while (sanitized.Contains(".."))
            {
                sanitized = sanitized.Replace("..", "_");
            }
            sanitized = sanitized.Trim('.', ' ', '\t', '\r', '\n');

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
            if (result.Length == 0 || result.All(c => c == '_' || c == '.')) result = "Profile";
            if (result.Length > 100) result = result.Substring(0, 100);
            return result;
        }

        public static bool IsSafePath(string subKeyPath)
        {
            if (string.IsNullOrWhiteSpace(subKeyPath)) return false;
            string normalized = subKeyPath.Replace('/', '\\').Trim().TrimStart('\\');
            if (normalized.Contains("..")) return false;
            if (normalized.IndexOf('\0') >= 0) return false;
            if (normalized.Any(char.IsControl)) return false;
            if (normalized.Length > 255) return false;
            if (!normalized.Equals(BaseKeyPath, StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith(BaseKeyPath + @"\", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public static RegistryKey? OpenSafeSubKey(RegistryKey root, string relativePath, bool writable = false)
        {
            if (root == null) return null;
            string normalized = relativePath?.Replace('/', '\\').Trim().TrimStart('\\') ?? "";
            if (!IsSafePath(normalized))
                throw new SecurityException($"Registry path '{relativePath}' is outside authorized scope '{BaseKeyPath}'.");

            try
            {
                return root.OpenSubKey(normalized, writable);
            }
            catch (SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
            catch { return null; }
        }

        public static RegistryKey? CreateSafeSubKey(RegistryKey root, string relativePath)
        {
            if (root == null) return null;
            string normalized = relativePath?.Replace('/', '\\').Trim().TrimStart('\\') ?? "";
            if (!IsSafePath(normalized))
                throw new SecurityException($"Registry path '{relativePath}' is outside authorized scope '{BaseKeyPath}'.");

            try
            {
                return root.CreateSubKey(normalized);
            }
            catch (SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
            catch { return null; }
        }

                public static bool TryGetRegistryInt(RegistryKey? primary, string name, out int val)
        {
            val = 0;
            if (primary == null) return false;
            try
            {
                var raw = primary.GetValue(name);
                if (raw == null) return false;
                if (raw is int i) { val = i; return true; }
                if (raw is string s && int.TryParse(s, out var ps)) { val = ps; return true; }
                val = Convert.ToInt32(raw);
                return true;
            }
            catch { return false; }
        }

        public static bool TryGetRegistryInt(RegistryKey? primary, RegistryKey? fallback, string name, out int val)
        {
            return TryGetRegistryInt(primary, name, out val) || TryGetRegistryInt(fallback, name, out val);
        }

        public static bool TryGetRegistryLong(RegistryKey? primary, RegistryKey? fallback, string name, out long val)
        {
            val = 0;
            if (primary != null)
            {
                try
                {
                    var raw = primary.GetValue(name);
                    if (raw != null)
                    {
                        if (raw is long l) { val = l; return true; }
                        if (raw is int i) { val = i; return true; }
                        if (raw is string s && long.TryParse(s, out var pl)) { val = pl; return true; }
                        val = Convert.ToInt64(raw);
                        return true;
                    }
                }
                catch { }
            }
            if (fallback != null)
            {
                try
                {
                    var raw = fallback.GetValue(name);
                    if (raw != null)
                    {
                        if (raw is long l) { val = l; return true; }
                        if (raw is int i) { val = i; return true; }
                        if (raw is string s && long.TryParse(s, out var pl)) { val = pl; return true; }
                        val = Convert.ToInt64(raw);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        public static bool TryGetRegistryBool(RegistryKey? primary, RegistryKey? fallback, string name, out bool val)
        {
            val = false;
            if (TryGetRegistryInt(primary, fallback, name, out int iVal))
            {
                val = iVal == 1;
                return true;
            }
            return false;
        }

        public static void SaveModeKeyAction(int action)
        {
            lock (_regLock)
            {
                int safeAction = Math.Clamp(action, 0, 1);
                try
                {
                    using var hkcu = CreateSafeSubKey(Registry.CurrentUser, BaseKeyPath);
                    hkcu?.SetValue("ModeKeyAction", safeAction, RegistryValueKind.DWord);
                    hkcu?.Flush();
                }
                catch { }

                try
                {
                    using var hklm = CreateSafeSubKey(Registry.LocalMachine, BaseKeyPath);
                    hklm?.SetValue("ModeKeyAction", safeAction, RegistryValueKind.DWord);
                    hklm?.Flush();
                }
                catch { }
            }
        }

        public static int LoadModeKeyAction(int fallback = 0)
        {
            lock (_regLock)
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
        }

        public static void SaveGameProfile(GameProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.ExecutableName)) return;
            string keyName = SanitizeSubKeyName(profile.ExecutableName);
            string profilePath = $@"{ProfilesSubKey}\{keyName}";

            lock (RegistryLock)
            {
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
                key.SetValue("ApplyCpuPowerLimits", profile.ApplyCpuPowerLimits ? 1 : 0);
                key.SetValue("CpuPl1Watts", profile.CpuPl1Watts);
                key.SetValue("CpuPl2Watts", profile.CpuPl2Watts);
                key.SetValue("DdsMode", profile.DdsMode);
                key.SetValue("LcdOverdrive", profile.LcdOverdrive);
                    key.Flush();
                }
                catch { }
            }
        }


        public static void DeleteGameProfile(string? exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return;
            string keyName = SanitizeSubKeyName(exeName);
            if (string.IsNullOrWhiteSpace(keyName)) return;

            lock (RegistryLock)
            {
                try
                {
                    using var root = OpenSafeSubKey(Registry.CurrentUser, ProfilesSubKey, writable: true);
                    if (root == null) return;
                    if (root.GetSubKeyNames().Any(k => k.Equals(keyName, StringComparison.OrdinalIgnoreCase)))
                    {
                        root.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                        root.Flush();
                    }
                }
                catch { }
            }
        }
        public static List<GameProfile> LoadGameProfiles()
        {
            var list = new List<GameProfile>();
            lock (RegistryLock)
            {
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
                            ApplyCpuPowerLimits = key.GetValue("ApplyCpuPowerLimits") is int acpl && acpl == 1,
                            CpuPl1Watts = key.GetValue("CpuPl1Watts") is int cpl1 ? cpl1 : -1,
                            CpuPl2Watts = key.GetValue("CpuPl2Watts") is int cpl2 ? cpl2 : -1,
                            DdsMode = key.GetValue("DdsMode") is int dds ? dds : -1,
                            LcdOverdrive = key.GetValue("LcdOverdrive") is int lod ? lod : -1,
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
            }
            return list;
        }

        public static void SaveCalibrationState(long utcTicks, int phase, int lastKnownPercent, bool preChargeLimitEnabled)
        {
            lock (RegistryLock)
            {
                try
                {
                    using var hkcu = CreateSafeSubKey(Registry.CurrentUser, BaseKeyPath);
                    if (hkcu != null)
                    {
                        hkcu.SetValue("LastBatteryCalibrationUtcTicks", Math.Max(0, utcTicks), RegistryValueKind.QWord);
                        hkcu.SetValue("BatteryCalibrationPhase", Math.Clamp(phase, 0, 2), RegistryValueKind.DWord);
                        hkcu.SetValue("CalibrationLastKnownPercent", lastKnownPercent < 0 ? -1 : Math.Clamp(lastKnownPercent, 0, 100), RegistryValueKind.DWord);
                        hkcu.SetValue("CalibrationPreChargeLimitEnabled", preChargeLimitEnabled ? 1 : 0, RegistryValueKind.DWord);
                        hkcu.Flush();
                    }
                }
                catch { }
            }
        }

        private static object? GetValueWithFallback(RegistryKey? primary, RegistryKey? fallback, string name)
        {
            return primary?.GetValue(name) ?? fallback?.GetValue(name);
        }

        public static void LoadCalibrationState(AppSettings settings)
        {
            if (settings == null) return;
            lock (RegistryLock)
            {
                lock (settings)
                {
                    try
                    {
                        using var hkcu = OpenSafeSubKey(Registry.CurrentUser, BaseKeyPath);
                using var hklm = OpenSafeSubKey(Registry.LocalMachine, BaseKeyPath);
                if (hkcu != null || hklm != null)
                {
                    object? ticksVal = GetValueWithFallback(hkcu, hklm, "LastBatteryCalibrationUtcTicks");
                    if (ticksVal is long ticks)
                        settings.LastBatteryCalibrationUtcTicks = ticks;
                    else if (ticksVal is string ticksStr && long.TryParse(ticksStr, out long parsedTicks))
                        settings.LastBatteryCalibrationUtcTicks = parsedTicks;

                    if (GetValueWithFallback(hkcu, hklm, "BatteryCalibrationPhase") is int phase)
                        settings.BatteryCalibrationPhase = phase;

                    if (GetValueWithFallback(hkcu, hklm, "CalibrationLastKnownPercent") is int pct)
                        settings.CalibrationLastKnownPercent = pct;

                    if (GetValueWithFallback(hkcu, hklm, "CalibrationPreChargeLimitEnabled") is int preLimit)
                        settings.CalibrationPreChargeLimitEnabled = preLimit == 1;
                }
                }
                    catch { }
                    settings.Sanitize();
                }
            }
        }
    
        public static void SaveCpuPowerLimits(AppSettings settings)
        {
            if (settings == null) return;
            var snap = settings.CreateSnapshot();
            lock (_regLock)
            {
                try
                {
                    using var key = CreateSafeSubKey(Registry.CurrentUser, BaseKeyPath);
                    if (key != null)
                    {
                        key.SetValue("EnableCustomPowerLimits", snap.EnableCustomPowerLimits ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("HardwareHasActiveCustomPowerOverride", snap.HardwareHasActiveCustomPowerOverride ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("BiosHardwareFingerprint", snap.BiosHardwareFingerprint ?? "", RegistryValueKind.String);
                        key.SetValue("ScannedHardwareSignature", snap.BiosHardwareFingerprint ?? "", RegistryValueKind.String);
                        key.SetValue("HasCompletedBiosPowerScan", snap.HasCompletedBiosPowerScan ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("HasScannedHardwarePowerBounds", snap.HasCompletedBiosPowerScan ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("CpuThermalSpecTdpWatts", snap.CpuThermalSpecTdpWatts, RegistryValueKind.DWord);
                        key.SetValue("ScannedCpuTdpWatts", snap.CpuThermalSpecTdpWatts, RegistryValueKind.DWord);
                        key.SetValue("CpuMinAllowedPowerWatts", snap.CpuMinAllowedPowerWatts, RegistryValueKind.DWord);
                        key.SetValue("ScannedMinPowerWatts", snap.CpuMinAllowedPowerWatts, RegistryValueKind.DWord);
                        key.SetValue("CpuMaxAllowedPl1Watts", snap.CpuMaxAllowedPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("ScannedMaxPl1Watts", snap.CpuMaxAllowedPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("CpuMaxAllowedPl2Watts", snap.CpuMaxAllowedPl2Watts, RegistryValueKind.DWord);
                        key.SetValue("ScannedMaxPl2Watts", snap.CpuMaxAllowedPl2Watts, RegistryValueKind.DWord);
                        key.SetValue("VerifiedBiosModesBitmask", snap.VerifiedBiosModesBitmask, RegistryValueKind.DWord);

                        key.SetValue("BiosDefaultEcoPl1Watts", snap.BiosDefaultEcoPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("DefaultEcoPl1Watts", snap.BiosDefaultEcoPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("BiosDefaultEcoPl2Watts", snap.BiosDefaultEcoPl2Watts, RegistryValueKind.DWord);
                        key.SetValue("DefaultEcoPl2Watts", snap.BiosDefaultEcoPl2Watts, RegistryValueKind.DWord);

                        key.SetValue("BiosDefaultQuietPl1Watts", snap.BiosDefaultQuietPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("DefaultQuietPl1Watts", snap.BiosDefaultQuietPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("BiosDefaultQuietPl2Watts", snap.BiosDefaultQuietPl2Watts, RegistryValueKind.DWord);
                        key.SetValue("DefaultQuietPl2Watts", snap.BiosDefaultQuietPl2Watts, RegistryValueKind.DWord);

                        key.SetValue("BiosDefaultBalancedPl1Watts", snap.BiosDefaultBalancedPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("DefaultBalancedPl1Watts", snap.BiosDefaultBalancedPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("BiosDefaultBalancedPl2Watts", snap.BiosDefaultBalancedPl2Watts, RegistryValueKind.DWord);
                        key.SetValue("DefaultBalancedPl2Watts", snap.BiosDefaultBalancedPl2Watts, RegistryValueKind.DWord);

                        key.SetValue("BiosDefaultPerformancePl1Watts", snap.BiosDefaultPerformancePl1Watts, RegistryValueKind.DWord);
                        key.SetValue("DefaultPerformancePl1Watts", snap.BiosDefaultPerformancePl1Watts, RegistryValueKind.DWord);
                        key.SetValue("BiosDefaultPerformancePl2Watts", snap.BiosDefaultPerformancePl2Watts, RegistryValueKind.DWord);
                        key.SetValue("DefaultPerformancePl2Watts", snap.BiosDefaultPerformancePl2Watts, RegistryValueKind.DWord);

                        key.SetValue("BiosDefaultTurboPl1Watts", snap.BiosDefaultTurboPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("DefaultTurboPl1Watts", snap.BiosDefaultTurboPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("BiosDefaultTurboPl2Watts", snap.BiosDefaultTurboPl2Watts, RegistryValueKind.DWord);
                        key.SetValue("DefaultTurboPl2Watts", snap.BiosDefaultTurboPl2Watts, RegistryValueKind.DWord);

                        key.SetValue("CustomEcoPl1Watts", snap.CustomEcoPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("EcoPl1Watts", snap.CustomEcoPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("CustomEcoPl2Watts", snap.CustomEcoPl2Watts, RegistryValueKind.DWord);
                        key.SetValue("EcoPl2Watts", snap.CustomEcoPl2Watts, RegistryValueKind.DWord);

                        key.SetValue("CustomQuietPl1Watts", snap.CustomQuietPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("QuietPl1Watts", snap.CustomQuietPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("CustomQuietPl2Watts", snap.CustomQuietPl2Watts, RegistryValueKind.DWord);
                        key.SetValue("QuietPl2Watts", snap.CustomQuietPl2Watts, RegistryValueKind.DWord);

                        key.SetValue("CustomBalancedPl1Watts", snap.CustomBalancedPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("BalancedPl1Watts", snap.CustomBalancedPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("CustomBalancedPl2Watts", snap.CustomBalancedPl2Watts, RegistryValueKind.DWord);
                        key.SetValue("BalancedPl2Watts", snap.CustomBalancedPl2Watts, RegistryValueKind.DWord);

                        key.SetValue("CustomPerformancePl1Watts", snap.CustomPerformancePl1Watts, RegistryValueKind.DWord);
                        key.SetValue("PerformancePl1Watts", snap.CustomPerformancePl1Watts, RegistryValueKind.DWord);
                        key.SetValue("CustomPerformancePl2Watts", snap.CustomPerformancePl2Watts, RegistryValueKind.DWord);
                        key.SetValue("PerformancePl2Watts", snap.CustomPerformancePl2Watts, RegistryValueKind.DWord);

                        key.SetValue("CustomTurboPl1Watts", snap.CustomTurboPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("TurboPl1Watts", snap.CustomTurboPl1Watts, RegistryValueKind.DWord);
                        key.SetValue("CustomTurboPl2Watts", snap.CustomTurboPl2Watts, RegistryValueKind.DWord);
                        key.SetValue("TurboPl2Watts", snap.CustomTurboPl2Watts, RegistryValueKind.DWord);

                        key.Flush();
                    }
                }
                catch { }
            }
        }

        public static void LoadCpuPowerLimits(AppSettings settings)
        {
            if (settings == null) return;
            var local = new AppSettings();
            bool loaded = false;

            lock (_regLock)
            {
                try
                {
                    using var hkcu = OpenSafeSubKey(Registry.CurrentUser, BaseKeyPath);
                    using var hklm = OpenSafeSubKey(Registry.LocalMachine, BaseKeyPath);
                    if (hkcu != null || hklm != null)
                    {
                        object? val = GetValueWithFallback(hkcu, hklm, "EnableCustomPowerLimits");
                        if (val is int ecpl) local.EnableCustomPowerLimits = ecpl == 1;
                        else if (val is string secpl && int.TryParse(secpl, out int pecpl)) local.EnableCustomPowerLimits = pecpl == 1;

                        object? valOverride = GetValueWithFallback(hkcu, hklm, "HardwareHasActiveCustomPowerOverride");
                        if (valOverride is int hhacpo) local.HardwareHasActiveCustomPowerOverride = hhacpo == 1;
                        else if (valOverride is string shhacpo && int.TryParse(shhacpo, out int phhacpo)) local.HardwareHasActiveCustomPowerOverride = phhacpo == 1;

                        string? fp = GetValueWithFallback(hkcu, hklm, "BiosHardwareFingerprint") as string ?? GetValueWithFallback(hkcu, hklm, "ScannedHardwareSignature") as string;
                        if (!string.IsNullOrEmpty(fp)) local.BiosHardwareFingerprint = fp;

                        if (GetValueWithFallback(hkcu, hklm, "HasCompletedBiosPowerScan") is int hcbps) local.HasCompletedBiosPowerScan = hcbps == 1;
                        else if (GetValueWithFallback(hkcu, hklm, "HasScannedHardwarePowerBounds") is int hshpb) local.HasCompletedBiosPowerScan = hshpb == 1;

                        if (GetValueWithFallback(hkcu, hklm, "CpuThermalSpecTdpWatts") is int cttdp) local.CpuThermalSpecTdpWatts = cttdp;
                        else if (GetValueWithFallback(hkcu, hklm, "ScannedCpuTdpWatts") is int sctdp) local.CpuThermalSpecTdpWatts = sctdp;

                        if (GetValueWithFallback(hkcu, hklm, "CpuMinAllowedPowerWatts") is int cmapw) local.CpuMinAllowedPowerWatts = cmapw;
                        else if (GetValueWithFallback(hkcu, hklm, "ScannedMinPowerWatts") is int smpw) local.CpuMinAllowedPowerWatts = smpw;

                        if (GetValueWithFallback(hkcu, hklm, "CpuMaxAllowedPl1Watts") is int cmapl1) local.CpuMaxAllowedPl1Watts = cmapl1;
                        else if (GetValueWithFallback(hkcu, hklm, "ScannedMaxPl1Watts") is int smpl1) local.CpuMaxAllowedPl1Watts = smpl1;

                        if (GetValueWithFallback(hkcu, hklm, "CpuMaxAllowedPl2Watts") is int cmapl2) local.CpuMaxAllowedPl2Watts = cmapl2;
                        else if (GetValueWithFallback(hkcu, hklm, "ScannedMaxPl2Watts") is int smpl2) local.CpuMaxAllowedPl2Watts = smpl2;

                        if (GetValueWithFallback(hkcu, hklm, "VerifiedBiosModesBitmask") is int vbmb) local.VerifiedBiosModesBitmask = vbmb;

                        if (GetValueWithFallback(hkcu, hklm, "BiosDefaultEcoPl1Watts") is int bdep1) local.BiosDefaultEcoPl1Watts = bdep1;
                        else if (GetValueWithFallback(hkcu, hklm, "DefaultEcoPl1Watts") is int dep1) local.BiosDefaultEcoPl1Watts = dep1;

                        if (GetValueWithFallback(hkcu, hklm, "BiosDefaultEcoPl2Watts") is int bdep2) local.BiosDefaultEcoPl2Watts = bdep2;
                        else if (GetValueWithFallback(hkcu, hklm, "DefaultEcoPl2Watts") is int dep2) local.BiosDefaultEcoPl2Watts = dep2;

                        if (GetValueWithFallback(hkcu, hklm, "BiosDefaultQuietPl1Watts") is int bdqp1) local.BiosDefaultQuietPl1Watts = bdqp1;
                        else if (GetValueWithFallback(hkcu, hklm, "DefaultQuietPl1Watts") is int dqp1) local.BiosDefaultQuietPl1Watts = dqp1;

                        if (GetValueWithFallback(hkcu, hklm, "BiosDefaultQuietPl2Watts") is int bdqp2) local.BiosDefaultQuietPl2Watts = bdqp2;
                        else if (GetValueWithFallback(hkcu, hklm, "DefaultQuietPl2Watts") is int dqp2) local.BiosDefaultQuietPl2Watts = dqp2;

                        if (GetValueWithFallback(hkcu, hklm, "BiosDefaultBalancedPl1Watts") is int bdbp1) local.BiosDefaultBalancedPl1Watts = bdbp1;
                        else if (GetValueWithFallback(hkcu, hklm, "DefaultBalancedPl1Watts") is int dbp1) local.BiosDefaultBalancedPl1Watts = dbp1;

                        if (GetValueWithFallback(hkcu, hklm, "BiosDefaultBalancedPl2Watts") is int bdbp2) local.BiosDefaultBalancedPl2Watts = bdbp2;
                        else if (GetValueWithFallback(hkcu, hklm, "DefaultBalancedPl2Watts") is int dbp2) local.BiosDefaultBalancedPl2Watts = dbp2;

                        if (GetValueWithFallback(hkcu, hklm, "BiosDefaultPerformancePl1Watts") is int bdpp1) local.BiosDefaultPerformancePl1Watts = bdpp1;
                        else if (GetValueWithFallback(hkcu, hklm, "DefaultPerformancePl1Watts") is int dpp1) local.BiosDefaultPerformancePl1Watts = dpp1;

                        if (GetValueWithFallback(hkcu, hklm, "BiosDefaultPerformancePl2Watts") is int bdpp2) local.BiosDefaultPerformancePl2Watts = bdpp2;
                        else if (GetValueWithFallback(hkcu, hklm, "DefaultPerformancePl2Watts") is int dpp2) local.BiosDefaultPerformancePl2Watts = dpp2;

                        if (GetValueWithFallback(hkcu, hklm, "BiosDefaultTurboPl1Watts") is int bdtp1) local.BiosDefaultTurboPl1Watts = bdtp1;
                        else if (GetValueWithFallback(hkcu, hklm, "DefaultTurboPl1Watts") is int dtp1) local.BiosDefaultTurboPl1Watts = dtp1;

                        if (GetValueWithFallback(hkcu, hklm, "BiosDefaultTurboPl2Watts") is int bdtp2) local.BiosDefaultTurboPl2Watts = bdtp2;
                        else if (GetValueWithFallback(hkcu, hklm, "DefaultTurboPl2Watts") is int dtp2) local.BiosDefaultTurboPl2Watts = dtp2;

                        if (GetValueWithFallback(hkcu, hklm, "CustomEcoPl1Watts") is int cep1) local.CustomEcoPl1Watts = cep1;
                        else if (GetValueWithFallback(hkcu, hklm, "EcoPl1Watts") is int ep1) local.CustomEcoPl1Watts = ep1;

                        if (GetValueWithFallback(hkcu, hklm, "CustomEcoPl2Watts") is int cep2) local.CustomEcoPl2Watts = cep2;
                        else if (GetValueWithFallback(hkcu, hklm, "EcoPl2Watts") is int ep2) local.CustomEcoPl2Watts = ep2;

                        if (GetValueWithFallback(hkcu, hklm, "CustomQuietPl1Watts") is int cqp1) local.CustomQuietPl1Watts = cqp1;
                        else if (GetValueWithFallback(hkcu, hklm, "QuietPl1Watts") is int qp1) local.CustomQuietPl1Watts = qp1;

                        if (GetValueWithFallback(hkcu, hklm, "CustomQuietPl2Watts") is int cqp2) local.CustomQuietPl2Watts = cqp2;
                        else if (GetValueWithFallback(hkcu, hklm, "QuietPl2Watts") is int qp2) local.CustomQuietPl2Watts = qp2;

                        if (GetValueWithFallback(hkcu, hklm, "CustomBalancedPl1Watts") is int cbp1) local.CustomBalancedPl1Watts = cbp1;
                        else if (GetValueWithFallback(hkcu, hklm, "BalancedPl1Watts") is int bp1) local.CustomBalancedPl1Watts = bp1;

                        if (GetValueWithFallback(hkcu, hklm, "CustomBalancedPl2Watts") is int cbp2) local.CustomBalancedPl2Watts = cbp2;
                        else if (GetValueWithFallback(hkcu, hklm, "BalancedPl2Watts") is int bp2) local.CustomBalancedPl2Watts = bp2;

                        if (GetValueWithFallback(hkcu, hklm, "CustomPerformancePl1Watts") is int cpp1) local.CustomPerformancePl1Watts = cpp1;
                        else if (GetValueWithFallback(hkcu, hklm, "PerformancePl1Watts") is int pp1) local.CustomPerformancePl1Watts = pp1;

                        if (GetValueWithFallback(hkcu, hklm, "CustomPerformancePl2Watts") is int cpp2) local.CustomPerformancePl2Watts = cpp2;
                        else if (GetValueWithFallback(hkcu, hklm, "PerformancePl2Watts") is int pp2) local.CustomPerformancePl2Watts = pp2;

                        if (GetValueWithFallback(hkcu, hklm, "CustomTurboPl1Watts") is int ctp1) local.CustomTurboPl1Watts = ctp1;
                        else if (GetValueWithFallback(hkcu, hklm, "TurboPl1Watts") is int tp1) local.CustomTurboPl1Watts = tp1;

                        if (GetValueWithFallback(hkcu, hklm, "CustomTurboPl2Watts") is int ctp2) local.CustomTurboPl2Watts = ctp2;
                        else if (GetValueWithFallback(hkcu, hklm, "TurboPl2Watts") is int tp2) local.CustomTurboPl2Watts = tp2;

                        loaded = local.HasCompletedBiosPowerScan || local.CpuThermalSpecTdpWatts > 0 || !string.IsNullOrEmpty(local.BiosHardwareFingerprint);
                    }
                }
                catch { }
            }

            if (loaded)
            {
                lock (settings)
                {
                    settings.EnableCustomPowerLimits = local.EnableCustomPowerLimits;
                    settings.HardwareHasActiveCustomPowerOverride = local.HardwareHasActiveCustomPowerOverride;
                    if (!string.IsNullOrEmpty(local.BiosHardwareFingerprint))
                        settings.BiosHardwareFingerprint = local.BiosHardwareFingerprint;
                    settings.HasCompletedBiosPowerScan = local.HasCompletedBiosPowerScan;
                    settings.CpuThermalSpecTdpWatts = local.CpuThermalSpecTdpWatts;
                    settings.CpuMinAllowedPowerWatts = local.CpuMinAllowedPowerWatts;
                    settings.CpuMaxAllowedPl1Watts = local.CpuMaxAllowedPl1Watts;
                    settings.CpuMaxAllowedPl2Watts = local.CpuMaxAllowedPl2Watts;
                    settings.VerifiedBiosModesBitmask = local.VerifiedBiosModesBitmask;

                    settings.BiosDefaultEcoPl1Watts = local.BiosDefaultEcoPl1Watts;
                    settings.BiosDefaultEcoPl2Watts = local.BiosDefaultEcoPl2Watts;
                    settings.BiosDefaultQuietPl1Watts = local.BiosDefaultQuietPl1Watts;
                    settings.BiosDefaultQuietPl2Watts = local.BiosDefaultQuietPl2Watts;
                    settings.BiosDefaultBalancedPl1Watts = local.BiosDefaultBalancedPl1Watts;
                    settings.BiosDefaultBalancedPl2Watts = local.BiosDefaultBalancedPl2Watts;
                    settings.BiosDefaultPerformancePl1Watts = local.BiosDefaultPerformancePl1Watts;
                    settings.BiosDefaultPerformancePl2Watts = local.BiosDefaultPerformancePl2Watts;
                    settings.BiosDefaultTurboPl1Watts = local.BiosDefaultTurboPl1Watts;
                    settings.BiosDefaultTurboPl2Watts = local.BiosDefaultTurboPl2Watts;

                    settings.CustomEcoPl1Watts = local.CustomEcoPl1Watts;
                    settings.CustomEcoPl2Watts = local.CustomEcoPl2Watts;
                    settings.CustomQuietPl1Watts = local.CustomQuietPl1Watts;
                    settings.CustomQuietPl2Watts = local.CustomQuietPl2Watts;
                    settings.CustomBalancedPl1Watts = local.CustomBalancedPl1Watts;
                    settings.CustomBalancedPl2Watts = local.CustomBalancedPl2Watts;
                    settings.CustomPerformancePl1Watts = local.CustomPerformancePl1Watts;
                    settings.CustomPerformancePl2Watts = local.CustomPerformancePl2Watts;
                    settings.CustomTurboPl1Watts = local.CustomTurboPl1Watts;
                    settings.CustomTurboPl2Watts = local.CustomTurboPl2Watts;

                    settings.Sanitize();
                }
            }
        }
    }
}