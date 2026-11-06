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

        public BiosSettingItem Clone() => new BiosSettingItem
        {
            Index = Index,
            Name = Name,
            CurrentValue = CurrentValue,
            PendingValue = PendingValue,
            Options = Options != null ? new List<string>(Options) : new List<string>(),
            Risk = Risk,
            Kind = Kind,
            Category = Category
        };
    }

    public class BiosManager
    {
        private static readonly Lazy<BiosManager> _instance = new(() => new BiosManager());
        public static BiosManager Instance => _instance.Value;

        private readonly object _lock = new();
        private BiosSupportMode _supportMode = BiosSupportMode.Unsupported;
        private string _modelName = "Universal Acer Predator / Nitro";
        private readonly List<BiosSettingItem> _settings = new();
        
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
                try
                {
                    var appSettings = AppSettings.Load();
                    appSettings.BiosModificationsEnabled = value;
                    if (!value) appSettings.BiosDangerousAllowed = false;
                    appSettings.Save();
                }
                catch { }
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
                try
                {
                    var appSettings = AppSettings.Load();
                    appSettings.BiosDangerousAllowed = value;
                    appSettings.Save();
                }
                catch { }
            }
        }
        private bool _dangerousAllowed = false;

        private DeviceBiosInfo _biosInfo = HardwareIdentity.GetDeviceBiosInfo();
        public DeviceBiosInfo BiosInfo { get { lock (_lock) return _biosInfo; } }
        public string BiosBadgeText { get { lock (_lock) return _biosInfo.ToBadgeString(); } }

        public BiosSupportMode SupportMode { get { lock (_lock) return _supportMode; } }
        public string ModelName { get { lock (_lock) return _modelName; } }
        public IReadOnlyList<BiosSettingItem> Settings
        {
            get { lock (_lock) { return _settings.ToList().AsReadOnly(); } }
        }

        public BiosManager()
        {
            try
            {
                var appSettings = AppSettings.Load();
                _modificationsEnabled = appSettings.BiosModificationsEnabled;
                _dangerousAllowed = appSettings.BiosDangerousAllowed && _modificationsEnabled;
            }
            catch { }

            _biosInfo = HardwareIdentity.GetDeviceBiosInfo();
            if (!string.IsNullOrWhiteSpace(_biosInfo.FullModelName) && _biosInfo.FullModelName != "Unknown System")
            {
                _modelName = _biosInfo.FullModelName;
            }
            ProbeAndLoad();
        }

        public void ProbeAndLoad()
        {
            var newSettings = new List<BiosSettingItem>();
            var biosInfo = HardwareIdentity.GetDeviceBiosInfo(refresh: true);
            string modelName = (!string.IsNullOrWhiteSpace(biosInfo.FullModelName) && biosInfo.FullModelName != "Unknown System")
                ? biosInfo.FullModelName
                : "Universal Acer Predator / Nitro";
            var supportMode = BiosSupportMode.Unsupported;

            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                if (PredatorNativeCore.BiosProbeSupport(out int mode, out string model))
                {
                    supportMode = (BiosSupportMode)mode;
                    if (!string.IsNullOrWhiteSpace(model) && model != "Unknown")
                    {
                        modelName = model;
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
                        newSettings.Add(item);
                    }
                }
            }

            // If native didn't provide settings or isn't available, provide comprehensive Acer native presets
            if (newSettings.Count == 0)
            {
                supportMode = BiosSupportMode.AcpiWmiFallback2020;
                if (string.IsNullOrWhiteSpace(modelName) || modelName == "Universal Acer Predator / Nitro")
                {
                    modelName = !string.IsNullOrWhiteSpace(biosInfo.FullModelName) && biosInfo.FullModelName != "Unknown System"
                        ? biosInfo.FullModelName
                        : "Acer Predator / Nitro Universal";
                }
                LoadFallbackAcerSettings(newSettings);
            }

            // Query live hardware state directly from WmiController and SMBIOS (outside lock to prevent deadlocks)
            RefreshLiveHardwareValues(newSettings);

            // Hydrate remembered values from AppSettings
            try
            {
                var appSettings = AppSettings.Load();
                foreach (var item in newSettings)
                {
                    string remembered = appSettings.GetBiosSettingValue(item.Name, "");
                    if (!string.IsNullOrEmpty(remembered) && (item.Options == null || item.Options.Count == 0 || item.Options.Contains(remembered)))
                    {
                        item.CurrentValue = remembered;
                        item.PendingValue = remembered;
                    }
                }
            }
            catch { }

            lock (_lock)
            {
                _settings.Clear();
                _settings.AddRange(newSettings);
                _biosInfo = biosInfo;
                _modelName = modelName;
                _supportMode = supportMode;
            }
        }

        private void RefreshLiveHardwareValues(List<BiosSettingItem>? targetSettings = null)
        {
            var wmi = WmiController.Instance;
            var list = targetSettings ?? _settings;
            foreach (var item in list)
            {
                string norm = item.Name.ToLowerInvariant();
                try
                {
                    if (norm.Contains("function key") || norm.Contains("functionkey"))
                    {
                        var fn = wmi.GetFunctionKeyBehavior();
                        if (fn.HasValue)
                        {
                            item.CurrentValue = fn.Value ? "Standard Function Key" : "Special Key";
                            item.PendingValue = item.CurrentValue;
                        }
                    }
                    else if (norm.Contains("backlight") && norm.Contains("timeout"))
                    {
                        var bl = wmi.GetBacklightTimeout();
                        if (bl.HasValue)
                        {
                            string val = bl.Value.TimeoutSeconds switch
                            {
                                10 => "10 Seconds",
                                30 => "30 Seconds",
                                60 => "60 Seconds",
                                _ => "Disabled"
                            };
                            item.CurrentValue = val;
                            item.PendingValue = val;
                        }
                    }
                    else if (norm.Contains("animation") || (norm.Contains("post") && norm.Contains("sound")))
                    {
                        var anim = wmi.GetBootAnimation();
                        if (anim.HasValue)
                        {
                            string val = anim.Value ? "Enabled" : "Disabled";
                            item.CurrentValue = val;
                            item.PendingValue = val;
                        }
                    }
                    else if (norm.Contains("usb") && norm.Contains("charg"))
                    {
                        var usb = wmi.GetUsbCharging();
                        if (usb != null)
                        {
                            string val;
                            if (!usb.Enabled) val = "Disabled";
                            else if (usb.FloorPercent.HasValue) val = $"Enabled ({usb.FloorPercent.Value}%)";
                            else val = "Enabled (10%)";
                            item.CurrentValue = val;
                            item.PendingValue = val;
                        }
                    }
                    else if (norm.Contains("battery") || norm.Contains("threshold"))
                    {
                        var bat = wmi.GetBatteryHealthStatus();
                        if (bat != null)
                        {
                            string val = bat.HealthModeOn ? "Enabled" : "Disabled";
                            item.CurrentValue = val;
                            item.PendingValue = val;
                        }
                    }
                    else if (norm.Contains("overdrive"))
                    {
                        var od = wmi.GetLcdOverdrive();
                        if (od.HasValue)
                        {
                            string val = od.Value ? "Enabled" : "Disabled";
                            item.CurrentValue = val;
                            item.PendingValue = val;
                        }
                        else
                        {
                            string val = Display.DisplayManager.Instance.IsLcdOverdriveEnabled ? "Enabled" : "Disabled";
                            item.CurrentValue = val;
                            item.PendingValue = val;
                        }
                    }
                    else if (norm.Contains("mux") || norm.Contains("gpu"))
                    {
                        var gpu = wmi.GetGpuMode();
                        if (gpu.HasValue)
                        {
                            string val = gpu.Value switch
                            {
                                GpuMode.dGpuOnly => "Discrete GPU only",
                                GpuMode.iGpuOnly => "Optimus",
                                _ => "Auto"
                            };
                            item.CurrentValue = val;
                            item.PendingValue = val;
                        }
                    }
                    else
                    {
                        // Check WMI ListBIOSSettings or AcerBiosConfigurationTool
                        var wmiVal = wmi.QueryBiosSettingViaWmi(item.Name);
                        if (!string.IsNullOrEmpty(wmiVal))
                        {
                            item.CurrentValue = wmiVal;
                            item.PendingValue = wmiVal;
                        }
                    }
                }
                catch { }
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
            if (lower.Contains("mux") || lower.Contains("overdrive") || lower.Contains("display") || lower.Contains("gpu") )
                return BiosCategory.DisplayPerformance;
            return BiosCategory.System;
        }

        private void LoadFallbackAcerSettings(List<BiosSettingItem>? targetSettings = null)
        {
            var list = targetSettings ?? _settings;
            _supportMode = BiosSupportMode.AcpiWmiFallback2020;
            if (string.IsNullOrWhiteSpace(_modelName) || _modelName == "Universal Acer Predator / Nitro")
            {
                _modelName = !string.IsNullOrWhiteSpace(_biosInfo.FullModelName) && _biosInfo.FullModelName != "Unknown System"
                    ? _biosInfo.FullModelName
                    : "Acer Predator / Nitro Universal";
            }

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

            foreach (var item in defaults) list.Add(item);
        }

        public void SetSupervisorPassword(string? password)
        {
            lock (_lock)
            {
                _cachedPassword = string.IsNullOrWhiteSpace(password) ? null : password;
            }
        }
        private string? _cachedPassword = null;

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

                dangerousAllowed = bypassSafetyGates || _dangerousAllowed;
            }

            // Validate allowed options if defined for this setting item
            if (item.Options != null && item.Options.Count > 0 && !item.Options.Contains(item.PendingValue))
            {
                error = $"Invalid value '{item.PendingValue}' for setting '{item.Name}'. Allowed values: {string.Join(", ", item.Options)}.";
                return false;
            }

            bool restartRequired = false;
            string norm = item.Name.ToLowerInvariant();
            var wmi = WmiController.Instance;

            // 1. Dispatch setting changes to real Acer WMI/ACPI methods
            try
            {
                if (norm.Contains("mux") || norm.Contains("gpu"))
                {
                    GpuMode mode = item.PendingValue switch
                    {
                        "Discrete GPU only" => GpuMode.dGpuOnly,
                        "Optimus" => GpuMode.iGpuOnly,
                        _ => GpuMode.Auto
                    };
                    wmi.SetGpuMode(mode);
                    restartRequired = true;
                }
                else if (norm.Contains("function key") || norm.Contains("functionkey"))
                {
                    bool standardFn = item.PendingValue.Equals("Standard Function Key", StringComparison.OrdinalIgnoreCase);
                    wmi.SetFunctionKeyBehavior(standardFn);
                }
                else if (norm.Contains("backlight") && norm.Contains("timeout"))
                {
                    int sec = 0;
                    if (item.PendingValue.Contains("10")) sec = 10;
                    else if (item.PendingValue.Contains("30")) sec = 30;
                    else if (item.PendingValue.Contains("60")) sec = 60;
                    wmi.SetGamingKBBacklightTimeout(sec);
                }
                else if (norm.Contains("animation") || (norm.Contains("post") && norm.Contains("sound")))
                {
                    bool enabled = item.PendingValue.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                    wmi.SetBootAnimation(enabled);
                }
                else if (norm.Contains("usb") && norm.Contains("charg"))
                {
                    bool enabled = !item.PendingValue.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
                    int floor = 10;
                    if (item.PendingValue.Contains("20")) floor = 20;
                    else if (item.PendingValue.Contains("30")) floor = 30;
                    wmi.SetUsbCharging(enabled, floor);
                }
                else if (norm.Contains("battery") || norm.Contains("threshold"))
                {
                    bool enabled = item.PendingValue.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                    wmi.SetBatteryChargeLimit(enabled);
                }
                else if (norm.Contains("overdrive"))
                {
                    bool enabled = item.PendingValue.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                    wmi.SetLcdOverdrive(enabled);
                    Display.DisplayManager.Instance.SetLcdOverdrive(enabled);
                }
                else
                {
                    // InsydeH2O UEFI variables / WMI AcerBiosConfigurationTool / ListBIOSSettings
                    wmi.SetBiosSettingViaWmi(item.Name, item.PendingValue, _cachedPassword);
                }
            }
            catch { }

            // 2. Also dispatch through native core if available
            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                int ret = PredatorNativeCore.BiosApplySetting(item.Name, item.PendingValue, _cachedPassword, dangerousAllowed);
                if (ret == 8)
                {
                    restartRequired = true;
                }
                else if (ret == -3)
                {
                    error = "Dangerous modification rejected by hardware safety gate.";
                    return false;
                }
            }

            // 3. Update current value in-memory
            lock (_lock)
            {
                item.CurrentValue = item.PendingValue;
                var match = _settings.FirstOrDefault(s => s.Index == item.Index || s.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    match.CurrentValue = item.PendingValue;
                    match.PendingValue = item.PendingValue;
                }
            }

            // 4. Permanently remember applied BIOS setting across app restarts
            try
            {
                var appSettings = AppSettings.Load();
                appSettings.SetBiosSettingValue(item.Name, item.CurrentValue);
                if (norm.Contains("mux") || norm.Contains("gpu"))
                {
                    appSettings.HardwareGpuMuxMode = item.CurrentValue switch
                    {
                        "Optimus" => 1,
                        "Discrete GPU only" => 2,
                        _ => 0
                    };
                    appSettings.GpuMode = appSettings.HardwareGpuMuxMode;
                }
                appSettings.Save();
            }
            catch { }

            if (restartRequired || norm.Contains("mux") || norm.Contains("gpu"))
            {
                error = "Setting applied successfully. A system restart is required for changes to take effect in firmware.";
            }

            return true;
        }

        public bool RestoreDefaults(bool userDefaults, out string error)
        {
            error = "";
            lock (_lock)
            {
                if (!_modificationsEnabled)
                {
                    error = "BIOS modifications are disabled.";
                    return false;
                }
            }

            try
            {
                var appSettings = AppSettings.Load();
                appSettings.ClearBiosSettings();
                appSettings.GpuMode = 0;
                appSettings.HardwareGpuMuxMode = 0;
                appSettings.Save();
            }
            catch { }

            try
            {
                var mux = GetMuxSetting();
                if (mux != null)
                {
                    mux.CurrentValue = "Auto";
                    mux.PendingValue = "Auto";
                }
            }
            catch { }

            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                int ret = PredatorNativeCore.BiosRestoreDefaults(null, userDefaults);
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

        public void HydrateFromSettings(AppSettings? settings = null)
        {
            try
            {
                var appSettings = settings ?? AppSettings.Load();
                lock (_lock)
                {
                    foreach (var item in _settings)
                    {
                        string remembered = appSettings.GetBiosSettingValue(item.Name, "");
                        if (!string.IsNullOrEmpty(remembered) && (item.Options == null || item.Options.Count == 0 || item.Options.Contains(remembered)))
                        {
                            item.CurrentValue = remembered;
                            item.PendingValue = remembered;
                        }
                    }
                }
            }
            catch { }
        }

        public void UpdateMuxSetting(string option)
        {
            lock (_lock)
            {
                var mux = _settings.FirstOrDefault(s => s.Name.Contains("Display MUX", StringComparison.OrdinalIgnoreCase) ||
                                                        s.Name.Contains("MUX", StringComparison.OrdinalIgnoreCase));
                if (mux != null)
                {
                    mux.CurrentValue = option;
                    mux.PendingValue = option;
                }
            }
        }

        public BiosSettingItem? GetMuxSetting()
        {
            lock (_lock)
            {
                return _settings.FirstOrDefault(s => s.Name.Contains("Display MUX", StringComparison.OrdinalIgnoreCase) ||
                                                     s.Name.Contains("MUX", StringComparison.OrdinalIgnoreCase));
            }
        }

        public GpuMode? GetGpuMode()
        {
            // Query live BIOS MUX mode directly from WmiController
            var hwGpu = WmiController.Instance.GetGpuMode();
            if (hwGpu.HasValue) return hwGpu.Value;

            var item = GetMuxSetting();
            if (item == null || string.IsNullOrWhiteSpace(item.CurrentValue)) return null;

            string val = item.CurrentValue.Trim();
            if (val.Equals("Optimus", StringComparison.OrdinalIgnoreCase) || val.Equals("iGPU", StringComparison.OrdinalIgnoreCase))
                return GpuMode.iGpuOnly;
            if (val.Equals("Discrete GPU only", StringComparison.OrdinalIgnoreCase) || val.Equals("dGPU", StringComparison.OrdinalIgnoreCase) || val.Equals("Discrete", StringComparison.OrdinalIgnoreCase))
                return GpuMode.dGpuOnly;
            if (val.Equals("Auto", StringComparison.OrdinalIgnoreCase) || val.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
                return GpuMode.Auto;

            return null;
        }

        public bool SetGpuMode(GpuMode mode, out string error, bool bypassSafetyGates = false)
        {
            error = "";
            string option = mode switch
            {
                GpuMode.Auto => "Auto",
                GpuMode.iGpuOnly => "Optimus",
                GpuMode.dGpuOnly => "Discrete GPU only",
                _ => "Auto"
            };

            var item = GetMuxSetting();
            if (item != null)
            {
                item.PendingValue = option;
                bool ok = ApplySetting(item, out error, bypassSafetyGates);
                return ok;
            }

            bool wmiOk = WmiController.Instance.SetGpuMode(mode);
            error = "Hardware MUX switch set in BIOS. A system restart is required for changes to take effect in firmware.";
            return wmiOk;
        }
    }
}
