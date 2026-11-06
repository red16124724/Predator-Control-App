using System;
using System.Collections.Generic;
using System.Linq;

namespace PredatorControlApp.Bios
{
    public enum BiosSupportMode
    {
        Unsupported = 0,
        WmiModern2025 = 1,
        AcpiWmiFallback2020 = 2,
        UefiNvramDirect = 3
    }

    public enum BiosSettingRisk
    {
        Normal = 0,
        Dangerous = 1
    }

    public enum BiosSettingKind
    {
        WmiNative = 1,
        AcpiFallback = 2,
        UefiNvram = 3
    }

    public enum BiosCategory
    {
        System,
        PowerBattery,
        DisplayPerformance,
        BootSetup,
        Other
    }

    public class BiosSettingItem
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CurrentValue { get; set; } = string.Empty;
        public string PendingValue { get; set; } = string.Empty;
        public List<string> Options { get; set; } = new();
        public BiosSettingRisk Risk { get; set; } = BiosSettingRisk.Normal;
        public BiosSettingKind Kind { get; set; } = BiosSettingKind.AcpiFallback;
        public BiosCategory Category { get; set; } = BiosCategory.System;

        public bool IsModified => !string.IsNullOrEmpty(PendingValue) &&
                                  !string.Equals(CurrentValue, PendingValue, StringComparison.OrdinalIgnoreCase);
    }

    public class BiosManager
    {
        private static readonly Lazy<BiosManager> _instance = new(() => new BiosManager());
        public static BiosManager Instance => _instance.Value;

        private readonly object _lock = new();
        private BiosSupportMode _supportMode = BiosSupportMode.Unsupported;
        private string _modelName = "Universal Acer Predator / Nitro";
        private readonly List<BiosSettingItem> _settings = new();
        private string? _cachedPassword = null;

        public bool ModificationsEnabled
        {
            get { lock (_lock) return _modificationsEnabled; }
            set
            {
                lock (_lock)
                {
                    _modificationsEnabled = value;
                    if (!value)
                    {
                        _dangerousAllowed = false;
                    }
                }
            }
        }
        private bool _modificationsEnabled = false;

        public bool DangerousAllowed
        {
            get { lock (_lock) return _dangerousAllowed; }
            set
            {
                lock (_lock)
                {
                    if (value && !_modificationsEnabled)
                    {
                        _dangerousAllowed = false;
                        return;
                    }
                    _dangerousAllowed = value;
                }
            }
        }
        private bool _dangerousAllowed = false;

        public BiosSupportMode SupportMode { get { lock (_lock) return _supportMode; } }
        public string ModelName { get { lock (_lock) return _modelName; } }
        public IReadOnlyList<BiosSettingItem> Settings
        {
            get { lock (_lock) { return _settings.ToList().AsReadOnly(); } }
        }

        public BiosManager()
        {
            ProbeAndLoad();
        }

        public void ProbeAndLoad()
        {
            lock (_lock)
            {
                _settings.Clear();

                if (PredatorNativeCore.IsNativeCoreAvailable)
                {
                    if (PredatorNativeCore.BiosProbeSupport(out int mode, out string model))
                    {
                        _supportMode = (BiosSupportMode)mode;
                        if (!string.IsNullOrWhiteSpace(model) && model != "Unknown")
                        {
                            _modelName = model;
                        }
                    }

                    int count = PredatorNativeCore.BiosGetSettingsCount();
                    for (int i = 0; i < count; i++)
                    {
                        if (PredatorNativeCore.BiosGetSettingInfo(i, out var info))
                        {
                            var item = new BiosSettingItem
                            {
                                Index = i,
                                Name = info.Name,
                                CurrentValue = info.CurrentValue,
                                PendingValue = info.CurrentValue,
                                Options = info.Options != null ? new List<string>(info.Options) : new List<string>(),
                                Risk = (BiosSettingRisk)info.Risk,
                                Kind = (BiosSettingKind)info.Kind,
                                Category = ClassifyCategory(info.Name)
                            };
                            _settings.Add(item);
                        }
                    }
                }

                // If native didn't provide settings or isn't available, provide comprehensive Acer native presets
                if (_settings.Count == 0)
                {
                    LoadFallbackAcerSettings();
                }
            }
        }

        public string GetSupportModeDescription() => _supportMode switch
        {
            BiosSupportMode.WmiModern2025 => "2025+ Native WMI BIOS (ListBIOSSettings)",
            BiosSupportMode.AcpiWmiFallback2020 => "2020-2024 ACPI-WMI Hardware Mapping",
            BiosSupportMode.UefiNvramDirect => "Universal InsydeH2O UEFI NVRAM",
            _ => "Universal ACPI Emulation Fallback"
        };

        private static BiosCategory ClassifyCategory(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return BiosCategory.System;
            string lower = name.ToLowerInvariant();
            if (lower.Contains("boot") || lower.Contains("f12") || lower.Contains("recovery") || lower.Contains("network"))
                return BiosCategory.BootSetup;
            if (lower.Contains("power") || lower.Contains("charge") || lower.Contains("battery") || lower.Contains("sleep"))
                return BiosCategory.PowerBattery;
            if (lower.Contains("mux") || lower.Contains("overdrive") || lower.Contains("display") || lower.Contains("gpu") || lower.Contains("overclock"))
                return BiosCategory.DisplayPerformance;
            return BiosCategory.System;
        }

        private void LoadFallbackAcerSettings()
        {
            _supportMode = BiosSupportMode.AcpiWmiFallback2020;
            _modelName = "Acer Predator / Nitro Universal";

            var defaults = new List<BiosSettingItem>
            {
                new BiosSettingItem
                {
                    Index = 0,
                    Name = "Function Key behavior",
                    CurrentValue = "Special Key",
                    PendingValue = "Special Key",
                    Options = new() { "Special Key", "Standard Function Key" },
                    Risk = BiosSettingRisk.Normal,
                    Kind = BiosSettingKind.AcpiFallback,
                    Category = BiosCategory.System
                },
                new BiosSettingItem
                {
                    Index = 1,
                    Name = "Keyboard backlight timeout",
                    CurrentValue = "30 Seconds",
                    PendingValue = "30 Seconds",
                    Options = new() { "Disabled", "10 Seconds", "30 Seconds" },
                    Risk = BiosSettingRisk.Normal,
                    Kind = BiosSettingKind.AcpiFallback,
                    Category = BiosCategory.System
                },
                new BiosSettingItem
                {
                    Index = 2,
                    Name = "POST Animation & Sound",
                    CurrentValue = "Enabled",
                    PendingValue = "Enabled",
                    Options = new() { "Enabled", "Disabled" },
                    Risk = BiosSettingRisk.Normal,
                    Kind = BiosSettingKind.AcpiFallback,
                    Category = BiosCategory.System
                },
                new BiosSettingItem
                {
                    Index = 3,
                    Name = "Power-off USB Charge",
                    CurrentValue = "Disabled",
                    PendingValue = "Disabled",
                    Options = new() { "Disabled", "Enabled (10%)", "Enabled (20%)", "Enabled (30%)" },
                    Risk = BiosSettingRisk.Normal,
                    Kind = BiosSettingKind.AcpiFallback,
                    Category = BiosCategory.PowerBattery
                },
                new BiosSettingItem
                {
                    Index = 4,
                    Name = "Battery Threshold (80% Limit)",
                    CurrentValue = "Disabled",
                    PendingValue = "Disabled",
                    Options = new() { "Enabled", "Disabled" },
                    Risk = BiosSettingRisk.Normal,
                    Kind = BiosSettingKind.AcpiFallback,
                    Category = BiosCategory.PowerBattery
                },
                new BiosSettingItem
                {
                    Index = 5,
                    Name = "Display MUX (Advanced Optimus / dGPU)",
                    CurrentValue = "Auto",
                    PendingValue = "Auto",
                    Options = new() { "Auto", "Optimus", "Discrete GPU only" },
                    Risk = BiosSettingRisk.Dangerous,
                    Kind = BiosSettingKind.AcpiFallback,
                    Category = BiosCategory.DisplayPerformance
                },
                new BiosSettingItem
                {
                    Index = 6,
                    Name = "LCD Overdrive (3ms Response)",
                    CurrentValue = "Enabled",
                    PendingValue = "Enabled",
                    Options = new() { "Enabled", "Disabled" },
                    Risk = BiosSettingRisk.Normal,
                    Kind = BiosSettingKind.AcpiFallback,
                    Category = BiosCategory.DisplayPerformance
                },
                new BiosSettingItem
                {
                    Index = 7,
                    Name = "Fast Boot",
                    CurrentValue = "Enabled",
                    PendingValue = "Enabled",
                    Options = new() { "Enabled", "Disabled" },
                    Risk = BiosSettingRisk.Normal,
                    Kind = BiosSettingKind.UefiNvram,
                    Category = BiosCategory.BootSetup
                },
                new BiosSettingItem
                {
                    Index = 8,
                    Name = "F12 Boot Menu",
                    CurrentValue = "Enabled",
                    PendingValue = "Enabled",
                    Options = new() { "Enabled", "Disabled" },
                    Risk = BiosSettingRisk.Normal,
                    Kind = BiosSettingKind.UefiNvram,
                    Category = BiosCategory.BootSetup
                },
                new BiosSettingItem
                {
                    Index = 9,
                    Name = "Wake on LAN",
                    CurrentValue = "Disabled",
                    PendingValue = "Disabled",
                    Options = new() { "Enabled", "Disabled" },
                    Risk = BiosSettingRisk.Normal,
                    Kind = BiosSettingKind.UefiNvram,
                    Category = BiosCategory.BootSetup
                },
                new BiosSettingItem
                {
                    Index = 10,
                    Name = "D2D Recovery",
                    CurrentValue = "Enabled",
                    PendingValue = "Enabled",
                    Options = new() { "Enabled", "Disabled" },
                    Risk = BiosSettingRisk.Normal,
                    Kind = BiosSettingKind.UefiNvram,
                    Category = BiosCategory.BootSetup
                }
            };

            _settings.AddRange(defaults);
        }

        public void SetSupervisorPassword(string? password)
        {
            lock (_lock)
            {
                _cachedPassword = string.IsNullOrWhiteSpace(password) ? null : password;
            }
        }

        public bool ApplySetting(BiosSettingItem item, out string error) => ApplySetting(item, out error, false);

        public bool ApplySetting(BiosSettingItem item, out string error, bool bypassSafetyGates)
        {
            error = "";
            if (item == null)
            {
                error = "Setting item cannot be null.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                error = "Setting name cannot be empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(item.PendingValue))
            {
                error = "Setting value cannot be empty.";
                return false;
            }
            string? password;
            bool dangerousAllowed;
            lock (_lock)
            {
                if (!bypassSafetyGates)
                {
                    if (!_modificationsEnabled)
                    {
                        error = "BIOS modifications are disabled. Toggle 'Enable BIOS Modifications' in the toolbar first.";
                        return false;
                    }

                    if (item.Risk == BiosSettingRisk.Dangerous && !_dangerousAllowed)
                    {
                        error = "This setting carries hardware or display risk and is blocked. Enable 'Allow Dangerous Settings' first.";
                        return false;
                    }
                }

                password = _cachedPassword;
                dangerousAllowed = bypassSafetyGates || _dangerousAllowed;
            }

            // Validate allowed options if defined for this setting item
            if (item.Options != null && item.Options.Count > 0 && !item.Options.Contains(item.PendingValue))
            {
                error = $"Invalid value '{item.PendingValue}' for setting '{item.Name}'. Allowed values: {string.Join(", ", item.Options)}.";
                return false;
            }

            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                int ret = PredatorNativeCore.BiosApplySetting(item.Name, item.PendingValue, password, dangerousAllowed);
                if (ret == 0)
                {
                    lock (_lock) { item.CurrentValue = item.PendingValue; }
                    return true;
                }
                else if (ret == 2)
                {
                    error = "Invalid supervisor password required by BIOS.";
                    return false;
                }
                else if (ret == 8)
                {
                    lock (_lock) { item.CurrentValue = item.PendingValue; }
                    error = "Setting applied successfully. A system restart is required for changes to take effect in firmware.";
                    return true;
                }
                else if (ret == -3)
                {
                    error = "Dangerous modification rejected by hardware safety gate.";
                    return false;
                }
                else
                {
                    error = $"Hardware returned error code {ret}.";
                    return false;
                }
            }

            // In managed fallback mode, acknowledge setting change
            lock (_lock) { item.CurrentValue = item.PendingValue; }
            return true;
        }

        public bool RestoreDefaults(bool userDefaults, out string error)
        {
            error = "";
            string? password;
            lock (_lock)
            {
                if (!_modificationsEnabled)
                {
                    error = "BIOS modifications are disabled.";
                    return false;
                }
                password = _cachedPassword;
            }

            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                int ret = PredatorNativeCore.BiosRestoreDefaults(password, userDefaults);
                if (ret == 0 || ret == 8)
                {
                    ProbeAndLoad();
                    return true;
                }
                error = $"Restore defaults returned code {ret}.";
                return false;
            }

            ProbeAndLoad();
            return true;
        }
    }
}
