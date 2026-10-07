using System;
using System.Collections.Generic;
using System.IO;

namespace PredatorControlApp
{
    public class GameProfile
    {
        private string _executableName = "";
        public string ExecutableName
        {
            get => _executableName;
            set => _executableName = value ?? "";
        }

        private string _executablePath = "";
        public string ExecutablePath
        {
            get => _executablePath;
            set => _executablePath = value ?? "";
        }

        private string _displayName = "";
        public string DisplayName
        {
            get => _displayName;
            set => _displayName = value ?? "";
        }

        public byte PowerMode { get; set; } = 0x01;
        public byte FanMode { get; set; } = 0x01;
        public int CpuFanSpeed { get; set; } = -1;
        public int GpuFanSpeed { get; set; } = -1;
        public int SysFanSpeed { get; set; } = -1;

        public int RefreshRate { get; set; } = -1;

        public int BatteryLimit { get; set; } = -1;

        public int RgbMode { get; set; } = -1;
        public int RgbBrightness { get; set; } = -1;
        public int RgbSpeed { get; set; } = -1;
        public int RgbR { get; set; } = -1;
        public int RgbG { get; set; } = -1;
        public int RgbB { get; set; } = -1;

        public static readonly HashSet<string> BlockedExecutableNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "svchost.exe", "csrss.exe", "smss.exe", "wininit.exe", "winlogon.exe", "services.exe",
            "lsass.exe", "fontdrvhost.exe", "dwm.exe", "conhost.exe", "dllhost.exe", "taskhostw.exe",
            "sihost.exe", "ctfmon.exe", "spoolsv.exe", "SearchIndexer.exe", "WmiPrvSE.exe",
            "RuntimeBroker.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
            "TextInputHost.exe", "SecurityHealthSystray.exe", "SecurityHealthService.exe",
            "MsMpEng.exe", "NisSrv.exe", "SgrmBroker.exe", "audiodg.exe", "PredatorControlApp.exe",
            "System.exe", "Registry.exe", "Idle.exe", "Memory Compression.exe", "explorer.exe",
            "cmd.exe", "powershell.exe", "pwsh.exe", "taskmgr.exe", "rundll32.exe", "cscript.exe",
            "wscript.exe", "mshta.exe", "regedit.exe"
        };

        public bool Validate(out string errorMessage)
        {
            errorMessage = "";
            if (string.IsNullOrWhiteSpace(ExecutableName))
            {
                errorMessage = "Executable name cannot be empty.";
                return false;
            }

            string trimmedExe = ExecutableName.Trim();
            if (trimmedExe.Length > 260)
            {
                errorMessage = "Executable name exceeds maximum allowed length.";
                return false;
            }

            if (trimmedExe.Contains('/') || trimmedExe.Contains('\\') || trimmedExe.Contains(".."))
            {
                errorMessage = "Executable name must be a file name without directory separators or traversal sequences.";
                return false;
            }

            if (trimmedExe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                errorMessage = "Executable name contains invalid characters.";
                return false;
            }

            string exeWithExt = trimmedExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? trimmedExe
                : trimmedExe + ".exe";

            if (BlockedExecutableNames.Contains(exeWithExt))
            {
                errorMessage = $"Targeting system or protected executable '{exeWithExt}' is prohibited.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ExecutablePath))
            {
                string path = ExecutablePath.Trim();
                if (path.Contains(".."))
                {
                    errorMessage = "Executable path cannot contain directory traversal sequences.";
                    return false;
                }

                if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    errorMessage = "Executable path contains invalid characters.";
                    return false;
                }

                if (!Path.IsPathRooted(path))
                {
                    errorMessage = "Executable path must be an absolute path.";
                    return false;
                }

                try
                {
                    string fullPath = Path.GetFullPath(path);
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (!string.IsNullOrEmpty(winDir))
                    {
                        string sys32 = Path.Combine(winDir, "System32");
                        string sysWow = Path.Combine(winDir, "SysWOW64");
                        if (fullPath.StartsWith(sys32, StringComparison.OrdinalIgnoreCase) ||
                            fullPath.StartsWith(sysWow, StringComparison.OrdinalIgnoreCase))
                        {
                            errorMessage = "Executable path cannot target Windows system directories.";
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = $"Executable path is invalid: {ex.Message}";
                    return false;
                }
            }

            return true;
        }

        public void Sanitize()
        {
            if (!string.IsNullOrWhiteSpace(ExecutableName))
            {
                string cleanName = Path.GetFileName(ExecutableName.Trim());
                if (!cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    cleanName += ".exe";
                }
                ExecutableName = cleanName;
            }

            if (!string.IsNullOrWhiteSpace(ExecutablePath))
            {
                try
                {
                    ExecutablePath = Path.GetFullPath(ExecutablePath.Trim());
                }
                catch
                {
                    ExecutablePath = "";
                }
            }

            if (!string.IsNullOrWhiteSpace(DisplayName))
            {
                var sb = new System.Text.StringBuilder();
                foreach (char c in DisplayName.Trim())
                {
                    if (!char.IsControl(c)) sb.Append(c);
                }
                string sanitizedName = sb.ToString();
                DisplayName = sanitizedName.Length > 100 ? sanitizedName.Substring(0, 100) : sanitizedName;
            }
            else if (!string.IsNullOrEmpty(ExecutableName))
            {
                DisplayName = Path.GetFileNameWithoutExtension(ExecutableName);
            }

            if (CpuFanSpeed != -1) CpuFanSpeed = CpuFanSpeed < 0 ? -1 : Math.Clamp(CpuFanSpeed, 0, 100);
            if (GpuFanSpeed != -1) GpuFanSpeed = GpuFanSpeed < 0 ? -1 : Math.Clamp(GpuFanSpeed, 0, 100);
            if (SysFanSpeed != -1) SysFanSpeed = SysFanSpeed < 0 ? -1 : Math.Clamp(SysFanSpeed, 0, 100);

            if (RefreshRate != -1) RefreshRate = RefreshRate < 0 ? -1 : Math.Clamp(RefreshRate, 60, 480);
            if (BatteryLimit != -1) BatteryLimit = BatteryLimit < 0 ? -1 : Math.Clamp(BatteryLimit, 0, 100);

            if (RgbMode != -1) RgbMode = RgbMode < 0 ? -1 : Math.Clamp(RgbMode, 0, 15);
            if (RgbBrightness != -1) RgbBrightness = RgbBrightness < 0 ? -1 : Math.Clamp(RgbBrightness, 0, 100);
            if (RgbSpeed != -1) RgbSpeed = RgbSpeed < 0 ? -1 : Math.Clamp(RgbSpeed, 0, 100);
            if (RgbR != -1) RgbR = RgbR < 0 ? -1 : Math.Clamp(RgbR, 0, 255);
            if (RgbG != -1) RgbG = RgbG < 0 ? -1 : Math.Clamp(RgbG, 0, 255);
            if (RgbB != -1) RgbB = RgbB < 0 ? -1 : Math.Clamp(RgbB, 0, 255);
        }
    }

    public class DashboardSnapshot
    {
        public byte PowerMode { get; set; }
        public byte FanMode { get; set; }
        public int CpuFanSpeed { get; set; }
        public int GpuFanSpeed { get; set; }
        public int SysFanSpeed { get; set; }
        public bool FanCurveWasEnabled { get; set; }
        public int RefreshRate { get; set; }
        public int BatteryLimit { get; set; }
        public int RgbMode { get; set; }
        public int RgbBrightness { get; set; }
        public int RgbSpeed { get; set; }
        public int RgbR { get; set; }
        public int RgbG { get; set; }
        public int RgbB { get; set; }
    }
}
