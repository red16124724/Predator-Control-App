using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PredatorControlApp
{
    public sealed class CurvePointData
    {
        public int X { get; set; }
        public int Y { get; set; }

        public CurvePointData() { }
        public CurvePointData(int x, int y) { X = x; Y = y; }
        public Point ToPoint() => new(X, Y);
        public static CurvePointData FromPoint(Point p) => new(p.X, p.Y);
    }

        public sealed class ThreadSafeCurveListConverter : JsonConverter<List<CurvePointData>>
    {
        public override List<CurvePointData>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
            var list = new List<CurvePointData>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                int x = 0;
                int y = 0;
                if (el.TryGetProperty("X", out var px))
                {
                    if (!px.TryGetInt32(out x) && px.TryGetDouble(out double dx))
                        x = (int)Math.Round(dx);
                    else if (px.ValueKind == JsonValueKind.String && int.TryParse(px.GetString(), out int sx))
                        x = sx;
                }
                if (el.TryGetProperty("Y", out var py))
                {
                    if (!py.TryGetInt32(out y) && py.TryGetDouble(out double dy))
                        y = (int)Math.Round(dy);
                    else if (py.ValueKind == JsonValueKind.String && int.TryParse(py.GetString(), out int sy))
                        y = sy;
                }
                list.Add(new CurvePointData(x, y));
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<CurvePointData> value, JsonSerializerOptions options)
        {
            CurvePointData[]? snapshot = null;
            if (value != null)
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        lock (value)
                        {
                            var arr = value.ToArray();
                            var list = new List<CurvePointData>(arr.Length);
                            for (int i = 0; i < arr.Length; i++)
                            {
                                var pt = arr[i];
                                if (pt != null) list.Add(new CurvePointData(pt.X, pt.Y));
                            }
                            snapshot = list.ToArray();
                        }
                        break;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException)
                    {
                        Thread.SpinWait(20);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            snapshot ??= Array.Empty<CurvePointData>();
            writer.WriteStartArray();
            foreach (var p in snapshot)
            {
                if (p != null)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("X", p.X);
                    writer.WriteNumber("Y", p.Y);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        }
    }

    public sealed class ThreadSafeShortcutListConverter : JsonConverter<List<CustomModeShortcut>>
    {
        public override List<CustomModeShortcut>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
            var list = new List<CustomModeShortcut>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                try
                {
                    string modeName = string.Empty;
                    if (el.TryGetProperty("ModeName", out var pName) && pName.ValueKind == JsonValueKind.String)
                    {
                        modeName = pName.GetString() ?? string.Empty;
                    }

                    byte modeCode = 0;
                    if (el.TryGetProperty("ModeCode", out var pCode) || el.TryGetProperty("ModeByteCode", out pCode))
                    {
                        if (pCode.ValueKind == JsonValueKind.Number && pCode.TryGetByte(out byte b)) modeCode = b;
                        else if (pCode.ValueKind == JsonValueKind.String && byte.TryParse(pCode.GetString(), out byte sb)) modeCode = sb;
                    }

                    Keys key = Keys.None;
                    if (el.TryGetProperty("Key", out var pKey))
                    {
                        if (pKey.ValueKind == JsonValueKind.Number && pKey.TryGetInt32(out int kInt))
                        {
                            key = (Keys)kInt;
                        }
                        else if (pKey.ValueKind == JsonValueKind.String)
                        {
                            string? kStr = pKey.GetString();
                            if (!string.IsNullOrEmpty(kStr) && Enum.TryParse<Keys>(kStr, true, out var parsedKey))
                            {
                                key = parsedKey;
                            }
                        }
                    }

                    bool ctrl = false;
                    if (el.TryGetProperty("Ctrl", out var pCtrl))
                    {
                        if (pCtrl.ValueKind is JsonValueKind.True or JsonValueKind.False) ctrl = pCtrl.GetBoolean();
                        else if (pCtrl.ValueKind == JsonValueKind.Number && pCtrl.TryGetInt32(out int ci)) ctrl = ci != 0;
                    }

                    bool alt = false;
                    if (el.TryGetProperty("Alt", out var pAlt))
                    {
                        if (pAlt.ValueKind is JsonValueKind.True or JsonValueKind.False) alt = pAlt.GetBoolean();
                        else if (pAlt.ValueKind == JsonValueKind.Number && pAlt.TryGetInt32(out int ai)) alt = ai != 0;
                    }

                    bool shift = false;
                    if (el.TryGetProperty("Shift", out var pShift))
                    {
                        if (pShift.ValueKind is JsonValueKind.True or JsonValueKind.False) shift = pShift.GetBoolean();
                        else if (pShift.ValueKind == JsonValueKind.Number && pShift.TryGetInt32(out int si)) shift = si != 0;
                    }

                    bool win = false;
                    if (el.TryGetProperty("Win", out var pWin))
                    {
                        if (pWin.ValueKind is JsonValueKind.True or JsonValueKind.False) win = pWin.GetBoolean();
                        else if (pWin.ValueKind == JsonValueKind.Number && pWin.TryGetInt32(out int wi)) win = wi != 0;
                    }

                    bool enabled = true;
                    if (el.TryGetProperty("Enabled", out var pEnabled))
                    {
                        if (pEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False) enabled = pEnabled.GetBoolean();
                        else if (pEnabled.ValueKind == JsonValueKind.Number && pEnabled.TryGetInt32(out int ei)) enabled = ei != 0;
                    }

                    list.Add(new CustomModeShortcut(modeName, modeCode, key, ctrl, alt, shift, win, enabled));
                }
                catch { }
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<CustomModeShortcut> value, JsonSerializerOptions options)
        {
            CustomModeShortcut[]? snapshot = null;
            if (value != null)
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        lock (value)
                        {
                            var arr = value.ToArray();
                            var list = new List<CustomModeShortcut>(arr.Length);
                            for (int i = 0; i < arr.Length; i++)
                            {
                                var s = arr[i];
                                if (s != null) list.Add(s.Clone());
                            }
                            snapshot = list.ToArray();
                        }
                        break;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException)
                    {
                        Thread.SpinWait(20);
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            snapshot ??= Array.Empty<CustomModeShortcut>();
            writer.WriteStartArray();
            foreach (var s in snapshot)
            {
                if (s != null)
                {
                    writer.WriteStartObject();
                    writer.WriteString("ModeName", s.ModeName);
                    writer.WriteNumber("ModeCode", s.ModeCode);
                    writer.WriteNumber("Key", (int)s.Key);
                    writer.WriteBoolean("Ctrl", s.Ctrl);
                    writer.WriteBoolean("Alt", s.Alt);
                    writer.WriteBoolean("Shift", s.Shift);
                    writer.WriteBoolean("Win", s.Win);
                    writer.WriteBoolean("Enabled", s.Enabled);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        }
    }

    [SupportedOSPlatform("windows")]
    public sealed class AppSettings
    {

        public string Theme { get; set; } = "System";
        public int[] CustomColors { get; set; } = new int[16];

        private List<CustomModeShortcut> _customModeShortcuts = ModeShortcutValidator.CreateDefaultShortcuts();

        [JsonConverter(typeof(ThreadSafeShortcutListConverter))]
        public List<CustomModeShortcut> CustomModeShortcuts
        {
            get => _customModeShortcuts;
            set => _customModeShortcuts = value ?? new();
        }

        // Scanned BIOS/UEFI Identity & Hardware Capabilities
        public string BiosHardwareFingerprint { get; set; } = string.Empty;
        public bool HasCompletedBiosPowerScan { get; set; } = false;
        public int CpuThermalSpecTdpWatts { get; set; } = 0;
        public int CpuMinAllowedPowerWatts { get; set; } = 0;
        public int CpuMaxAllowedPl1Watts { get; set; } = 0;
        public int CpuMaxAllowedPl2Watts { get; set; } = 0;

        // Scanned Acer BIOS/UEFI Default PL1 & PL2 Per Power Mode (Stored for the Reset Button; -1 = Unscanned)
        public int BiosDefaultEcoPl1Watts { get; set; } = -1;
        public int BiosDefaultEcoPl2Watts { get; set; } = -1;
        public int BiosDefaultQuietPl1Watts { get; set; } = -1;
        public int BiosDefaultQuietPl2Watts { get; set; } = -1;
        public int BiosDefaultBalancedPl1Watts { get; set; } = -1;
        public int BiosDefaultBalancedPl2Watts { get; set; } = -1;
        public int BiosDefaultPerformancePl1Watts { get; set; } = -1;
        public int BiosDefaultPerformancePl2Watts { get; set; } = -1;
        public int BiosDefaultTurboPl1Watts { get; set; } = -1;
        public int BiosDefaultTurboPl2Watts { get; set; } = -1;
        public int VerifiedBiosModesBitmask { get; set; } = 0;

        // User-Configured Per-Mode Active Power Limits (-1 = Use Scanned BIOS Default)
        public bool EnableCustomPowerLimits { get; set; } = false;
        public bool HardwareHasActiveCustomPowerOverride { get; set; } = false;
        public int CustomEcoPl1Watts { get; set; } = -1;
        public int CustomEcoPl2Watts { get; set; } = -1;
        public int CustomQuietPl1Watts { get; set; } = -1;
        public int CustomQuietPl2Watts { get; set; } = -1;
        public int CustomBalancedPl1Watts { get; set; } = -1;
        public int CustomBalancedPl2Watts { get; set; } = -1;
        public int CustomPerformancePl1Watts { get; set; } = -1;
        public int CustomPerformancePl2Watts { get; set; } = -1;
        public int CustomTurboPl1Watts { get; set; } = -1;
        public int CustomTurboPl2Watts { get; set; } = -1;

        // Backward compatibility aliases
        [JsonIgnore]
        public string ScannedHardwareSignature { get => BiosHardwareFingerprint; set => BiosHardwareFingerprint = value; }
        [JsonIgnore]
        public bool HasScannedHardwarePowerBounds { get => HasCompletedBiosPowerScan; set => HasCompletedBiosPowerScan = value; }
        [JsonIgnore]
        public int ScannedMinPowerWatts { get => CpuMinAllowedPowerWatts; set => CpuMinAllowedPowerWatts = value; }
        [JsonIgnore]
        public int ScannedMaxPl1Watts { get => CpuMaxAllowedPl1Watts; set => CpuMaxAllowedPl1Watts = value; }
        [JsonIgnore]
        public int ScannedMaxPl2Watts { get => CpuMaxAllowedPl2Watts; set => CpuMaxAllowedPl2Watts = value; }
        [JsonIgnore]
        public int ScannedCpuTdpWatts { get => CpuThermalSpecTdpWatts; set => CpuThermalSpecTdpWatts = value; }

        [JsonIgnore]
        public int DefaultEcoPl1Watts { get => BiosDefaultEcoPl1Watts; set => BiosDefaultEcoPl1Watts = value; }
        [JsonIgnore]
        public int DefaultEcoPl2Watts { get => BiosDefaultEcoPl2Watts; set => BiosDefaultEcoPl2Watts = value; }
        [JsonIgnore]
        public int DefaultQuietPl1Watts { get => BiosDefaultQuietPl1Watts; set => BiosDefaultQuietPl1Watts = value; }
        [JsonIgnore]
        public int DefaultQuietPl2Watts { get => BiosDefaultQuietPl2Watts; set => BiosDefaultQuietPl2Watts = value; }
        [JsonIgnore]
        public int DefaultBalancedPl1Watts { get => BiosDefaultBalancedPl1Watts; set => BiosDefaultBalancedPl1Watts = value; }
        [JsonIgnore]
        public int DefaultBalancedPl2Watts { get => BiosDefaultBalancedPl2Watts; set => BiosDefaultBalancedPl2Watts = value; }
        [JsonIgnore]
        public int DefaultPerformancePl1Watts { get => BiosDefaultPerformancePl1Watts; set => BiosDefaultPerformancePl1Watts = value; }
        [JsonIgnore]
        public int DefaultPerformancePl2Watts { get => BiosDefaultPerformancePl2Watts; set => BiosDefaultPerformancePl2Watts = value; }
        [JsonIgnore]
        public int DefaultTurboPl1Watts { get => BiosDefaultTurboPl1Watts; set => BiosDefaultTurboPl1Watts = value; }
        [JsonIgnore]
        public int DefaultTurboPl2Watts { get => BiosDefaultTurboPl2Watts; set => BiosDefaultTurboPl2Watts = value; }

        [JsonIgnore]
        public int EcoPl1Watts { get => CustomEcoPl1Watts; set => CustomEcoPl1Watts = value; }
        [JsonIgnore]
        public int EcoPl2Watts { get => CustomEcoPl2Watts; set => CustomEcoPl2Watts = value; }
        [JsonIgnore]
        public int QuietPl1Watts { get => CustomQuietPl1Watts; set => CustomQuietPl1Watts = value; }
        [JsonIgnore]
        public int QuietPl2Watts { get => CustomQuietPl2Watts; set => CustomQuietPl2Watts = value; }
        [JsonIgnore]
        public int BalancedPl1Watts { get => CustomBalancedPl1Watts; set => CustomBalancedPl1Watts = value; }
        [JsonIgnore]
        public int BalancedPl2Watts { get => CustomBalancedPl2Watts; set => CustomBalancedPl2Watts = value; }
        [JsonIgnore]
        public int PerformancePl1Watts { get => CustomPerformancePl1Watts; set => CustomPerformancePl1Watts = value; }
        [JsonIgnore]
        public int PerformancePl2Watts { get => CustomPerformancePl2Watts; set => CustomPerformancePl2Watts = value; }
        [JsonIgnore]
        public int TurboPl1Watts { get => CustomTurboPl1Watts; set => CustomTurboPl1Watts = value; }
        [JsonIgnore]
        public int TurboPl2Watts { get => CustomTurboPl2Watts; set => CustomTurboPl2Watts = value; }

        private byte _powerMode = 0x01;
        public byte PowerMode { get => _powerMode; set => _powerMode = Math.Clamp(value, (byte)0x00, (byte)0x06); }
        public byte? PowerModeAC { get; set; }
        public byte? PowerModeBattery { get; set; }
        public int AutoPowerAC { get; set; } = 0;
        public int AutoPowerBattery { get; set; } = 0;




        private byte _fanMode = 0x01;
        public byte FanMode { get => _fanMode; set => _fanMode = Math.Clamp(value, (byte)0x00, (byte)0x03); }
        public byte? FanModeAC { get; set; }
        public byte? FanModeBattery { get; set; }
        public int AutoFanAC { get; set; } = 0;
        public int AutoFanBattery { get; set; } = 0;
        public bool FanCurveEnabled { get; set; } = false;
        public bool AntiThrottleEnabled { get; set; } = true;


        private int _fanHysteresisDegrees = CurveFollower.DefaultHysteresisDegrees;
        public int FanHysteresisDegrees { get => _fanHysteresisDegrees; set => _fanHysteresisDegrees = Math.Clamp(value, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees); }

        private int _fanSpeedCpu = 50;
        public int FanSpeedCpu { get => _fanSpeedCpu; set => _fanSpeedCpu = Math.Clamp(value, 0, 100); }

        private int _fanSpeedGpu = 50;
        public int FanSpeedGpu { get => _fanSpeedGpu; set => _fanSpeedGpu = Math.Clamp(value, 0, 100); }

        private int _fanSpeedSys = 50;
        public int FanSpeedSys { get => _fanSpeedSys; set => _fanSpeedSys = Math.Clamp(value, 0, 100); }

        private int _fanSpeedCpuAC = 50;
        public int FanSpeedCpuAC { get => _fanSpeedCpuAC; set => _fanSpeedCpuAC = Math.Clamp(value, 0, 100); }

        private int _fanSpeedCpuBattery = 50;
        public int FanSpeedCpuBattery { get => _fanSpeedCpuBattery; set => _fanSpeedCpuBattery = Math.Clamp(value, 0, 100); }

        private int _fanSpeedGpuAC = 50;
        public int FanSpeedGpuAC { get => _fanSpeedGpuAC; set => _fanSpeedGpuAC = Math.Clamp(value, 0, 100); }

        private int _fanSpeedGpuBattery = 50;
        public int FanSpeedGpuBattery { get => _fanSpeedGpuBattery; set => _fanSpeedGpuBattery = Math.Clamp(value, 0, 100); }

        private int _fanSpeedSysAC = 50;
        public int FanSpeedSysAC { get => _fanSpeedSysAC; set => _fanSpeedSysAC = Math.Clamp(value, 0, 100); }

        private int _fanSpeedSysBattery = 50;
        public int FanSpeedSysBattery { get => _fanSpeedSysBattery; set => _fanSpeedSysBattery = Math.Clamp(value, 0, 100); }

        private List<CurvePointData> _cpuCurve = new();
        private List<CurvePointData> _gpuCurve = new();

        [JsonConverter(typeof(ThreadSafeCurveListConverter))]
        public List<CurvePointData> CpuCurve
        {
            get => _cpuCurve;
            set => _cpuCurve = value ?? new();
        }

        [JsonConverter(typeof(ThreadSafeCurveListConverter))]
        public List<CurvePointData> GpuCurve
        {
            get => _gpuCurve;
            set => _gpuCurve = value ?? new();
        }

        public int RefreshRate { get; set; } = 0;
        public bool LcdOverdrive { get; set; } = true;
        public int DdsMode { get; set; } = 1; // 0 = Optimus, 1 = Auto, 2 = dGPU
        public int HardwareGpuMuxMode
        {
            get => GpuMode;
            set => GpuMode = value;
        }
        private Dictionary<string, string> _biosSettingsValues = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> BiosSettingsValues
        {
            get
            {
                lock (this)
                {
                    return _biosSettingsValues;
                }
            }
            set
            {
                lock (this)
                {
                    _biosSettingsValues = value ?? new(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public string GetBiosSettingValue(string name, string defaultValue = "")
        {
            if (string.IsNullOrWhiteSpace(name)) return defaultValue;
            lock (this)
            {
                if (BiosSettingsValues != null && BiosSettingsValues.TryGetValue(name, out var val) && !string.IsNullOrEmpty(val))
                {
                    return val;
                }
            }
            return defaultValue;
        }

        public void SetBiosSettingValue(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            lock (this)
            {
                BiosSettingsValues ??= new(StringComparer.OrdinalIgnoreCase);
                BiosSettingsValues[name] = value ?? string.Empty;
            }
        }

        public void ClearBiosSettings()
        {
            lock (this)
            {
                BiosSettingsValues?.Clear();
                GpuMode = 0;
            }
        }
        public bool BiosModificationsEnabled { get; set; } = false;
        public bool BiosDangerousAllowed { get; set; } = false;
        public bool BatteryLimit { get; set; } = false;
        public long LastBatteryCalibrationUtcTicks { get; set; } = 0;
        public int BatteryCalibrationPhase { get; set; } = 0; // 0 = Idle, 1 = ChargingToFull, 2 = DischargingToEmpty
        public int CalibrationLastKnownPercent { get; set; } = -1;
        public bool CalibrationPreChargeLimitEnabled { get; set; } = false;
        public bool CoolBoost { get; set; } = false;

        public int RgbMode { get; set; } = 3;
        private int _keyboardBrightness = 100;
        public int RgbBrightness
        {
            get => _keyboardBrightness;
            set => _keyboardBrightness = Math.Clamp(value, 0, 100);
        }
        public int RgbSpeed { get; set; } = 50;
        public int RgbR { get; set; } = 0;
        public int RgbG { get; set; } = 150;
        public int RgbB { get; set; } = 255;

        // Keyboard lighting: 4-zone always, per-key only when the BIOS/service
        // capability probe confirms it on this exact machine.
        public int KeyboardLightingTierOverride { get; set; } = 0; // 0 auto, 1 four-zone, 2 per-key experimental, 3 single-zone, 4 three-zone
        public string KeyboardZoneColors { get; set; } = "#00A0FF,#00A0FF,#00A0FF,#00A0FF";
        public string KeyboardPerKeyColors { get; set; } = string.Empty;
        public bool IsZoneLightingActive { get; set; } = true;
        public string ActiveLightingPreset { get; set; } = "Custom";
        public int KeyboardZoneLayoutMode { get; set; } = 4;
        public bool KeyboardPerKeyEnabled { get; set; } = false;
        public string KeyboardLayoutId { get; set; } = KeyboardLayouts.AnsiTklId;
        public bool PerKeyCapabilityDetected { get; set; } = false;
        public string KeyboardCapabilityModel { get; set; } = string.Empty;
        public string KeyboardCapabilityNotes { get; set; } = string.Empty;

        // Dedicated Per-Zone Static Colors (never mutated by dynamic effects)
        public int StaticZone1ColorArgb { get; set; } = unchecked((int)0xFF00F0FF);
        public int StaticZone2ColorArgb { get; set; } = unchecked((int)0xFF00F0FF);
        public int StaticZone3ColorArgb { get; set; } = unchecked((int)0xFF00F0FF);
        public int StaticZone4ColorArgb { get; set; } = unchecked((int)0xFF00F0FF);
        public string StaticZoneColorMapJson { get; set; } = string.Empty;

        // Dedicated Dynamic RGB Effect Settings
        public string DynamicLightingEffectName { get; set; } = "Off";
        public int DynamicEffectPrimaryColorArgb { get; set; } = unchecked((int)0xFF00F0FF);
        public int DynamicEffectSecondaryColorArgb { get; set; } = unchecked((int)0xFFFF0080);
        public int DynamicEffectSpeed { get; set; } = 5;
        public int DynamicEffectDirection { get; set; } = 0;
        public int KeyboardBrightnessLevel
        {
            get => _keyboardBrightness;
            set => _keyboardBrightness = Math.Clamp(value, 0, 100);
        }

        public void SetStaticZoneColor(int zoneIndex, Color color)
        {
            switch (zoneIndex)
            {
                case 0:
                    StaticZone1ColorArgb = color.ToArgb();
                    break;
                case 1:
                    StaticZone2ColorArgb = color.ToArgb();
                    break;
                case 2:
                    StaticZone3ColorArgb = color.ToArgb();
                    break;
                case 3:
                    StaticZone4ColorArgb = color.ToArgb();
                    break;
                default:
                    return;
            }
            IsZoneLightingActive = true;
            Save();
        }

        public void SetStaticZoneColors(Color z1, Color z2, Color z3, Color z4)
        {
            StaticZone1ColorArgb = z1.ToArgb();
            StaticZone2ColorArgb = z2.ToArgb();
            StaticZone3ColorArgb = z3.ToArgb();
            StaticZone4ColorArgb = z4.ToArgb();
            IsZoneLightingActive = true;
            Save();
        }

        // Power mode switch smoothing: one mode write, deferred Windows overlay.
        public bool SmoothPowerModeSwitching { get; set; } = true;
        public bool WindowsOverlaySyncEnabled { get; set; } = true;
        public int PowerModeSettleMs { get; set; } = 350;

        // Thermal safety layer (PreySense-style ceiling, optimized for this app).
        public double ThermalCpuWarnCelsius { get; set; } = 75.0;
        public double ThermalCpuCeilingCelsius { get; set; } = 95.0;
        public double ThermalCpuCriticalCelsius { get; set; } = 100.0;
        public double ThermalGpuWarnCelsius { get; set; } = 70.0;
        public double ThermalGpuCeilingCelsius { get; set; } = 87.0;
        public double ThermalGpuCriticalCelsius { get; set; } = 90.0;
        public double ThermalHysteresisCelsius { get; set; } = 3.0;
        public int ThermalMaxFanFloorPercent { get; set; } = 100;
        public int ThermalCpuPowerCapWatts { get; set; } = 45;
        public int ThermalMinPowerCapWatts { get; set; } = 15;
        public bool ThermalCriticalAlertEnabled { get; set; } = true;

        public int GpuMode { get; set; } = 0;

        public int ModeKeyAction { get; set; } = 0; // 0 = Cycle, 1 = TurboToggle
        public byte TurboReturnMode { get; set; } = 0x01;

        #region CPU Power Limit Helpers

        public static int ModeToBitmask(OperatingMode mode) => mode switch
        {
            OperatingMode.Eco => 1 << 0,
            OperatingMode.Quiet => 1 << 1,
            OperatingMode.Balanced => 1 << 2,
            OperatingMode.Performance => 1 << 3,
            OperatingMode.Turbo => 1 << 4,
            _ => 1 << 2
        };

        public bool HasVerifiedBiosDefaultForMode(OperatingMode mode)
        {
            var (pl1, pl2) = GetRawScannedDefaultsForMode(mode);
            int bit = ModeToBitmask(mode);
            int enumBit = 1 << (int)mode;
            return ((VerifiedBiosModesBitmask & bit) != 0 || (VerifiedBiosModesBitmask & enumBit) != 0) && pl1 > 0 && pl2 > 0;
        }

        public bool HasScannedDefaultsForMode(OperatingMode mode)
        {
            var (pl1, pl2) = GetRawScannedDefaultsForMode(mode);
            return pl1 > 0 && pl2 > 0;
        }

        public (int pl1, int pl2) GetDefaultPowerLimitsForMode(OperatingMode mode)
        {
            var (pl1, pl2) = GetRawScannedDefaultsForMode(mode);
            if (pl1 > 0 && pl2 > 0)
            {
                return (pl1, pl2);
            }

            // Proportional display estimation for unvisited modes
            return EstimateProportionalDefaultsForMode(mode);
        }

        public (int ClampedPl1, int ClampedPl2) ClampPowerLimitWatts(int pl1, int pl2)
        {
            int min = CpuMinAllowedPowerWatts > 0 ? CpuMinAllowedPowerWatts : (ScannedMinPowerWatts > 0 ? ScannedMinPowerWatts : 5);
            int max2 = CpuMaxAllowedPl2Watts > 0 ? CpuMaxAllowedPl2Watts : (ScannedMaxPl2Watts > 0 ? ScannedMaxPl2Watts : 200);
            if (max2 < min) max2 = min;

            // PL1 has its own ceiling (CpuMaxAllowedPl1Watts). Widening it to the PL2 ceiling
            // made the field meaningless and let callers set a sustained limit above the maximum
            // the PL1 slider exposes; fall back to the PL2 ceiling only when PL1 has no bound.
            int max1 = CpuMaxAllowedPl1Watts > 0 ? CpuMaxAllowedPl1Watts : max2;
            if (max1 < min) max1 = min;
            if (max1 > max2) max1 = max2;

            int clampedPl1 = Math.Clamp(pl1, min, max1);
            int minPl2 = Math.Max(clampedPl1, min);
            int clampedPl2 = Math.Clamp(pl2, minPl2, max2);

            return (clampedPl1, clampedPl2);
        }

        public (int pl1, int pl2) GetEffectivePowerLimitsForMode(OperatingMode mode)
        {
            var (userPl1, userPl2) = GetUserPowerLimitsForMode(mode);
            var (defPl1, defPl2) = GetDefaultPowerLimitsForMode(mode);

            int effectivePl1 = userPl1 > 0 ? userPl1 : defPl1;
            int effectivePl2 = userPl2 > 0 ? userPl2 : defPl2;

            int min = CpuMinAllowedPowerWatts > 0 ? CpuMinAllowedPowerWatts : (ScannedMinPowerWatts > 0 ? ScannedMinPowerWatts : 5);
            int max2 = CpuMaxAllowedPl2Watts > 0 ? CpuMaxAllowedPl2Watts : (ScannedMaxPl2Watts > 0 ? ScannedMaxPl2Watts : 200);
            if (max2 < min) max2 = min;

            int max1 = CpuMaxAllowedPl1Watts > 0 ? CpuMaxAllowedPl1Watts : max2;
            // A firmware mode default may legitimately sit above the configured PL1 ceiling;
            // never clamp the BIOS's own default down, but still keep it inside the PL2 ceiling.
            if (defPl1 > max1) max1 = Math.Min(defPl1, max2);
            if (max1 > max2) max1 = max2;
            if (max1 < min) max1 = min;

            effectivePl1 = Math.Clamp(effectivePl1, min, max1);
            int minPl2 = Math.Max(effectivePl1, min);
            effectivePl2 = Math.Clamp(effectivePl2, minPl2, max2);

            return (effectivePl1, effectivePl2);
        }

        public void RecordScannedDefaultsForMode(OperatingMode mode, int pl1, int pl2, bool force = false)
        {
            lock (this)
            {
                if (pl1 <= 0 || pl2 <= 0)
                    return;

                if (!force && (HasVerifiedBiosDefaultForMode(mode) || HardwareHasActiveCustomPowerOverride))
                    return;

                if (!force && HasScannedDefaultsForMode(mode))
                    return;

                RecordBiosDefaultLimitsForMode(mode, pl1, pl2, isVerifiedBiosScan: true, forceCleanReset: force);
            }
        }

        public void SetPowerLimitsForMode(OperatingMode mode, int pl1, int pl2)
        {
            lock (this)
            {
                var (clampedPl1, clampedPl2) = ClampPowerLimitWatts(pl1, pl2);

                switch (mode)
                {
                    case OperatingMode.Eco:
                        EcoPl1Watts = clampedPl1;
                        EcoPl2Watts = clampedPl2;
                        break;
                    case OperatingMode.Quiet:
                        QuietPl1Watts = clampedPl1;
                        QuietPl2Watts = clampedPl2;
                        break;
                    case OperatingMode.Balanced:
                        BalancedPl1Watts = clampedPl1;
                        BalancedPl2Watts = clampedPl2;
                        break;
                    case OperatingMode.Performance:
                        PerformancePl1Watts = clampedPl1;
                        PerformancePl2Watts = clampedPl2;
                        break;
                    case OperatingMode.Turbo:
                        TurboPl1Watts = clampedPl1;
                        TurboPl2Watts = clampedPl2;
                        break;
                    default:
                        BalancedPl1Watts = clampedPl1;
                        BalancedPl2Watts = clampedPl2;
                        break;
                }
                EnableCustomPowerLimits = true;
            }
        }

        public void ResetPowerLimitsForMode(OperatingMode mode)
        {
            lock (this)
            {
                switch (mode)
                {
                    case OperatingMode.Eco:
                        EcoPl1Watts = -1;
                        EcoPl2Watts = -1;
                        break;
                    case OperatingMode.Quiet:
                        QuietPl1Watts = -1;
                        QuietPl2Watts = -1;
                        break;
                    case OperatingMode.Balanced:
                        BalancedPl1Watts = -1;
                        BalancedPl2Watts = -1;
                        break;
                    case OperatingMode.Performance:
                        PerformancePl1Watts = -1;
                        PerformancePl2Watts = -1;
                        break;
                    case OperatingMode.Turbo:
                        TurboPl1Watts = -1;
                        TurboPl2Watts = -1;
                        break;
                    default:
                        BalancedPl1Watts = -1;
                        BalancedPl2Watts = -1;
                        break;
                }
                if (!HasAnyCustomLimits())
                {
                    HardwareHasActiveCustomPowerOverride = false;
                }
            }
        }

        public (int DefaultPl1, int DefaultPl2, bool IsVerifiedBiosScan) GetBiosDefaultLimitsForMode(OperatingMode mode)
        {
            var (pl1, pl2) = GetRawScannedDefaultsForMode(mode);
            int bit = mode switch
            {
                OperatingMode.Eco => 1 << 0,
                OperatingMode.Quiet => 1 << 1,
                OperatingMode.Balanced => 1 << 2,
                OperatingMode.Performance => 1 << 3,
                OperatingMode.Turbo => 1 << 4,
                _ => 1 << 2
            };
            bool isVerified = (VerifiedBiosModesBitmask & bit) != 0 && pl1 > 0 && pl2 > 0;
            if (pl1 > 0 && pl2 > 0)
            {
                return (pl1, pl2, isVerified);
            }
            var est = EstimateProportionalDefaultsForMode(mode);
            return (est.pl1, est.pl2, false);
        }

        public void RecordBiosDefaultLimitsForMode(OperatingMode mode, int biosPl1, int biosPl2, bool isVerifiedBiosScan, bool forceCleanReset = false)
        {
            lock (this)
            {
                if (biosPl1 <= 0 || biosPl2 <= 0) return;
                if (!forceCleanReset && (HasVerifiedBiosDefaultForMode(mode) || HardwareHasActiveCustomPowerOverride)) return;
                int safePl1 = Math.Max(5, biosPl1);
                int safePl2 = Math.Max(safePl1, biosPl2);

                switch (mode)
                {
                    case OperatingMode.Eco:
                        BiosDefaultEcoPl1Watts = safePl1;
                        BiosDefaultEcoPl2Watts = safePl2;
                        break;
                    case OperatingMode.Quiet:
                        BiosDefaultQuietPl1Watts = safePl1;
                        BiosDefaultQuietPl2Watts = safePl2;
                        break;
                    case OperatingMode.Balanced:
                        BiosDefaultBalancedPl1Watts = safePl1;
                        BiosDefaultBalancedPl2Watts = safePl2;
                        break;
                    case OperatingMode.Performance:
                        BiosDefaultPerformancePl1Watts = safePl1;
                        BiosDefaultPerformancePl2Watts = safePl2;
                        break;
                    case OperatingMode.Turbo:
                        BiosDefaultTurboPl1Watts = safePl1;
                        BiosDefaultTurboPl2Watts = safePl2;
                        break;
                    default:
                        BiosDefaultBalancedPl1Watts = safePl1;
                        BiosDefaultBalancedPl2Watts = safePl2;
                        break;
                }

                if (isVerifiedBiosScan)
                {
                    int bit = mode switch
                    {
                        OperatingMode.Eco => 1 << 0,
                        OperatingMode.Quiet => 1 << 1,
                        OperatingMode.Balanced => 1 << 2,
                        OperatingMode.Performance => 1 << 3,
                        OperatingMode.Turbo => 1 << 4,
                        _ => 1 << 2
                    };
                    VerifiedBiosModesBitmask |= bit;
                }

                HasCompletedBiosPowerScan = true;
                // BIOS default limits must not cap or shrink the global maximum allowed ceilings, but the
                // PL1 ceiling is its own firmware value: only derive it from PL2 when it is genuinely unset.
                if (CpuMaxAllowedPl2Watts > 0 &&
                    (CpuMaxAllowedPl1Watts <= 0 || CpuMaxAllowedPl1Watts > CpuMaxAllowedPl2Watts))
                {
                    CpuMaxAllowedPl1Watts = CpuMaxAllowedPl2Watts;
                }
            }
        }

        public (int Pl1, int Pl2) GetEffectiveLimitsForMode(OperatingMode mode)
        {
            return GetEffectivePowerLimitsForMode(mode);
        }

        public void SetCustomLimitsForMode(OperatingMode mode, int pl1, int pl2)
        {
            SetPowerLimitsForMode(mode, pl1, pl2);
        }

        public void SetModePowerLimits(OperatingMode mode, int pl1, int pl2)
        {
            SetPowerLimitsForMode(mode, pl1, pl2);
        }

        public void ResetModeToBiosDefault(OperatingMode mode)
        {
            ResetPowerLimitsForMode(mode);
        }

        public static bool IsGenuineFingerprint(string? fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return false;
            var trimmed = fingerprint.Trim();
            if (trimmed.Length < 3) return false;
            if (string.Equals(trimmed, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "0", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        public void InvalidateIfBiosFingerprintChanged(string currentFingerprint)
        {
            if (string.IsNullOrWhiteSpace(currentFingerprint)) return;
            lock (this)
            {
                if (!string.Equals(BiosHardwareFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    ResetAllPowerLimitsToScannedDefaults();
                    HasCompletedBiosPowerScan = false;
                    CpuThermalSpecTdpWatts = 0;
                    CpuMinAllowedPowerWatts = 0;
                    CpuMaxAllowedPl1Watts = 0;
                    CpuMaxAllowedPl2Watts = 0;
                    VerifiedBiosModesBitmask = 0;

                    BiosDefaultEcoPl1Watts = -1; BiosDefaultEcoPl2Watts = -1;
                    BiosDefaultQuietPl1Watts = -1; BiosDefaultQuietPl2Watts = -1;
                    BiosDefaultBalancedPl1Watts = -1; BiosDefaultBalancedPl2Watts = -1;
                    BiosDefaultPerformancePl1Watts = -1; BiosDefaultPerformancePl2Watts = -1;
                    BiosDefaultTurboPl1Watts = -1; BiosDefaultTurboPl2Watts = -1;

                    CustomEcoPl1Watts = -1; CustomEcoPl2Watts = -1;
                    CustomQuietPl1Watts = -1; CustomQuietPl2Watts = -1;
                    CustomBalancedPl1Watts = -1; CustomBalancedPl2Watts = -1;
                    CustomPerformancePl1Watts = -1; CustomPerformancePl2Watts = -1;
                    CustomTurboPl1Watts = -1; CustomTurboPl2Watts = -1;
                }
                BiosHardwareFingerprint = currentFingerprint;
            }
        }

        public void ResetAllPowerLimitsToScannedDefaults()
        {
            lock (this)
            {
                EcoPl1Watts = -1; EcoPl2Watts = -1;
                QuietPl1Watts = -1; QuietPl2Watts = -1;
                BalancedPl1Watts = -1; BalancedPl2Watts = -1;
                PerformancePl1Watts = -1; PerformancePl2Watts = -1;
                TurboPl1Watts = -1; TurboPl2Watts = -1;
                HardwareHasActiveCustomPowerOverride = false;
            }
        }

        public bool HasAnyCustomLimits()
        {
            return (EcoPl1Watts > 0 && EcoPl2Watts > 0) ||
                   (QuietPl1Watts > 0 && QuietPl2Watts > 0) ||
                   (BalancedPl1Watts > 0 && BalancedPl2Watts > 0) ||
                   (PerformancePl1Watts > 0 && PerformancePl2Watts > 0) ||
                   (TurboPl1Watts > 0 && TurboPl2Watts > 0);
        }

        public (int pl1, int pl2) GetRawScannedDefaultsForMode(OperatingMode mode)
        {
            return mode switch
            {
                OperatingMode.Eco => (DefaultEcoPl1Watts, DefaultEcoPl2Watts),
                OperatingMode.Quiet => (DefaultQuietPl1Watts, DefaultQuietPl2Watts),
                OperatingMode.Balanced => (DefaultBalancedPl1Watts, DefaultBalancedPl2Watts),
                OperatingMode.Performance => (DefaultPerformancePl1Watts, DefaultPerformancePl2Watts),
                OperatingMode.Turbo => (DefaultTurboPl1Watts, DefaultTurboPl2Watts),
                _ => (DefaultBalancedPl1Watts, DefaultBalancedPl2Watts)
            };
        }

        public (int pl1, int pl2) GetUserPowerLimitsForMode(OperatingMode mode)
        {
            return mode switch
            {
                OperatingMode.Eco => (EcoPl1Watts, EcoPl2Watts),
                OperatingMode.Quiet => (QuietPl1Watts, QuietPl2Watts),
                OperatingMode.Balanced => (BalancedPl1Watts, BalancedPl2Watts),
                OperatingMode.Performance => (PerformancePl1Watts, PerformancePl2Watts),
                OperatingMode.Turbo => (TurboPl1Watts, TurboPl2Watts),
                _ => (BalancedPl1Watts, BalancedPl2Watts)
            };
        }

        public bool HasCustomPowerLimitsForMode(OperatingMode mode)
        {
            var (pl1, pl2) = GetUserPowerLimitsForMode(mode);
            return pl1 > 0 && pl2 > 0;
        }

        public (int pl1, int pl2) EstimateProportionalDefaultsForMode(OperatingMode mode)
        {
            double anchorPl1 = 0;
            double anchorPl2 = 0;

            if (DefaultBalancedPl1Watts > 0 && DefaultBalancedPl2Watts > 0)
            {
                anchorPl1 = DefaultBalancedPl1Watts;
                anchorPl2 = DefaultBalancedPl2Watts;
            }
            else if (DefaultPerformancePl1Watts > 0 && DefaultPerformancePl2Watts > 0)
            {
                anchorPl1 = DefaultPerformancePl1Watts / 1.25;
                anchorPl2 = DefaultPerformancePl2Watts / 1.30;
            }
            else if (DefaultQuietPl1Watts > 0 && DefaultQuietPl2Watts > 0)
            {
                anchorPl1 = DefaultQuietPl1Watts / 0.75;
                anchorPl2 = DefaultQuietPl2Watts / 0.80;
            }
            else if (DefaultTurboPl1Watts > 0 && DefaultTurboPl2Watts > 0)
            {
                anchorPl1 = DefaultTurboPl1Watts / 1.45;
                anchorPl2 = DefaultTurboPl2Watts / 1.50;
            }
            else if (DefaultEcoPl1Watts > 0 && DefaultEcoPl2Watts > 0)
            {
                anchorPl1 = DefaultEcoPl1Watts / 0.60;
                anchorPl2 = DefaultEcoPl2Watts / 0.65;
            }
            else
            {
                int tdp = ScannedCpuTdpWatts > 0 ? ScannedCpuTdpWatts : 45;
                anchorPl1 = tdp;
                anchorPl2 = Math.Round(tdp * 1.35);
            }

            (double ratioPl1, double ratioPl2) = mode switch
            {
                OperatingMode.Eco => (0.60, 0.65),
                OperatingMode.Quiet => (0.75, 0.80),
                OperatingMode.Balanced => (1.00, 1.00),
                OperatingMode.Performance => (1.25, 1.30),
                OperatingMode.Turbo => (1.45, 1.50),
                _ => (1.00, 1.00)
            };

            int estPl1 = (int)Math.Round(anchorPl1 * ratioPl1);
            int estPl2 = (int)Math.Round(anchorPl2 * ratioPl2);

            int min = ScannedMinPowerWatts > 0 ? ScannedMinPowerWatts : 5;
            int max1 = ScannedMaxPl1Watts > 0 ? ScannedMaxPl1Watts : 200;
            int max2 = ScannedMaxPl2Watts > 0 ? ScannedMaxPl2Watts : 200;

            if (max1 < min) max1 = min;
            if (max2 < max1) max2 = max1;

            estPl1 = Math.Clamp(estPl1, min, max1);
            estPl2 = Math.Clamp(estPl2, estPl1, Math.Max(estPl1, max2));

            return (estPl1, estPl2);
        }

        #endregion


        private static readonly object _pathLock = new();
        private static string? _customFilePath;
        [JsonIgnore]
        public static string? CustomFilePath
        {
            get { lock (_pathLock) return _customFilePath; }
            set
            {
                lock (_pathLock)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        _customFilePath = null;
                        return;
                    }
                    if (value.Contains("..") || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                        throw new ArgumentException("Invalid custom settings path: relative navigation not allowed");

                    string full = Path.GetFullPath(value);
                    if (!Path.IsPathRooted(full) || full.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                        throw new ArgumentException("Invalid custom settings path");

                    string ext = Path.GetExtension(full);
                    if (!string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Custom settings path must have a .json extension");

                    _customFilePath = full;
                }
            }
        }

        [JsonIgnore]
        public static string SettingsFilePath =>
            CustomFilePath ?? 
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PredatorControl", "settings.json");

        private static readonly object _fileLock = new();

        public static List<CustomModeShortcut> SafeCopyShortcuts(List<CustomModeShortcut>? src)
        {
            if (src == null) return new();
            for (int retry = 0; retry < 10; retry++)
            {
                try
                {
                    lock (src)
                    {
                        var arr = src.ToArray();
                        var list = new List<CustomModeShortcut>(arr.Length);
                        for (int i = 0; i < arr.Length; i++)
                        {
                            var item = arr[i];
                            if (item != null) list.Add(item.Clone());
                        }
                        return list;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    Thread.SpinWait(20);
                }
                catch
                {
                    return new();
                }
            }
            try
            {
                lock (src)
                {
                    var arr = new CustomModeShortcut[src.Count];
                    src.CopyTo(arr, 0);
                    return arr.Where(s => s != null).Select(s => s.Clone()).ToList();
                }
            }
            catch
            {
                return new();
            }
        }

        public static List<CurvePointData> SafeCopyCurve(List<CurvePointData>? src)
        {
            if (src == null) return new();
            for (int retry = 0; retry < 10; retry++)
            {
                try
                {
                    lock (src)
                    {
                        var arr = src.ToArray();
                        var list = new List<CurvePointData>(arr.Length);
                        for (int i = 0; i < arr.Length; i++)
                        {
                            var pt = arr[i];
                            if (pt != null) list.Add(new CurvePointData(pt.X, pt.Y));
                        }
                        return list;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    Thread.SpinWait(20);
                }
            }
            try
            {
                lock (src)
                {
                    var arr = new CurvePointData[src.Count];
                    src.CopyTo(arr, 0);
                    return arr.Where(p => p != null).Select(p => new CurvePointData(p.X, p.Y)).ToList();
                }
            }
            catch
            {
                return new();
            }
        }



        public AppSettings Clone() => CreateSnapshot();

        public AppSettings CreateSnapshot()
        {
            AppSettings snap;
            List<CurvePointData>? cpu;
            List<CurvePointData>? gpu;
            List<CustomModeShortcut>? shortcuts = null;
            lock (this)
            {
                snap = new AppSettings
                {
                    Theme = this.Theme,
                    CustomColors = (int[])(this.CustomColors?.Clone() ?? new int[16]),
                                        PowerMode = this.PowerMode,
                    PowerModeAC = this.PowerModeAC,
                    PowerModeBattery = this.PowerModeBattery,
                    AutoPowerAC = this.AutoPowerAC,
                    AutoPowerBattery = this.AutoPowerBattery,
                    FanMode = this.FanMode,
                    FanModeAC = this.FanModeAC,
                    FanModeBattery = this.FanModeBattery,
                    AutoFanAC = this.AutoFanAC,
                    AutoFanBattery = this.AutoFanBattery,
                    FanCurveEnabled = this.FanCurveEnabled,
                    FanHysteresisDegrees = this.FanHysteresisDegrees,
                    FanSpeedCpu = this.FanSpeedCpu,
                    FanSpeedGpu = this.FanSpeedGpu,
                    FanSpeedSys = this.FanSpeedSys,
                    FanSpeedCpuAC = this.FanSpeedCpuAC,
                    FanSpeedCpuBattery = this.FanSpeedCpuBattery,
                    FanSpeedGpuAC = this.FanSpeedGpuAC,
                    FanSpeedGpuBattery = this.FanSpeedGpuBattery,
                    FanSpeedSysAC = this.FanSpeedSysAC,
                    FanSpeedSysBattery = this.FanSpeedSysBattery,
                    RefreshRate = this.RefreshRate,
                    LcdOverdrive = this.LcdOverdrive,
                    DdsMode = this.DdsMode,
                    BiosModificationsEnabled = this.BiosModificationsEnabled,
                    BiosDangerousAllowed = this.BiosDangerousAllowed,
                    BatteryLimit = this.BatteryLimit,
                    LastBatteryCalibrationUtcTicks = this.LastBatteryCalibrationUtcTicks,
                    BatteryCalibrationPhase = this.BatteryCalibrationPhase,
                    CalibrationLastKnownPercent = this.CalibrationLastKnownPercent,
                    CalibrationPreChargeLimitEnabled = this.CalibrationPreChargeLimitEnabled,
                    CoolBoost = this.CoolBoost,
                    RgbMode = this.RgbMode,
                    RgbBrightness = this.RgbBrightness,
                    RgbSpeed = this.RgbSpeed,
                    RgbR = this.RgbR,
                    RgbG = this.RgbG,
                    RgbB = this.RgbB,
                    KeyboardLightingTierOverride = this.KeyboardLightingTierOverride,
                    KeyboardZoneColors = this.KeyboardZoneColors,
                    KeyboardPerKeyColors = this.KeyboardPerKeyColors,
                    IsZoneLightingActive = this.IsZoneLightingActive,
                    ActiveLightingPreset = this.ActiveLightingPreset,
                    KeyboardZoneLayoutMode = this.KeyboardZoneLayoutMode,
                    KeyboardPerKeyEnabled = this.KeyboardPerKeyEnabled,
                    KeyboardLayoutId = this.KeyboardLayoutId,
                    PerKeyCapabilityDetected = this.PerKeyCapabilityDetected,
                    KeyboardCapabilityModel = this.KeyboardCapabilityModel,
                    KeyboardCapabilityNotes = this.KeyboardCapabilityNotes,
                    StaticZone1ColorArgb = this.StaticZone1ColorArgb,
                    StaticZone2ColorArgb = this.StaticZone2ColorArgb,
                    StaticZone3ColorArgb = this.StaticZone3ColorArgb,
                    StaticZone4ColorArgb = this.StaticZone4ColorArgb,
                    StaticZoneColorMapJson = this.StaticZoneColorMapJson,
                    DynamicLightingEffectName = this.DynamicLightingEffectName,
                    DynamicEffectPrimaryColorArgb = this.DynamicEffectPrimaryColorArgb,
                    DynamicEffectSecondaryColorArgb = this.DynamicEffectSecondaryColorArgb,
                    DynamicEffectSpeed = this.DynamicEffectSpeed,
                    DynamicEffectDirection = this.DynamicEffectDirection,
                    KeyboardBrightnessLevel = this.KeyboardBrightnessLevel,
                    GpuMode = this.GpuMode,
                    ModeKeyAction = this.ModeKeyAction,
                    TurboReturnMode = this.TurboReturnMode,

                    BiosHardwareFingerprint = this.BiosHardwareFingerprint,
                    HasCompletedBiosPowerScan = this.HasCompletedBiosPowerScan,
                    CpuThermalSpecTdpWatts = this.CpuThermalSpecTdpWatts,
                    CpuMinAllowedPowerWatts = this.CpuMinAllowedPowerWatts,
                    CpuMaxAllowedPl1Watts = this.CpuMaxAllowedPl1Watts,
                    CpuMaxAllowedPl2Watts = this.CpuMaxAllowedPl2Watts,
                    VerifiedBiosModesBitmask = this.VerifiedBiosModesBitmask,
                    EnableCustomPowerLimits = this.EnableCustomPowerLimits,
                    HardwareHasActiveCustomPowerOverride = this.HardwareHasActiveCustomPowerOverride,

                    BiosDefaultEcoPl1Watts = this.BiosDefaultEcoPl1Watts,
                    BiosDefaultEcoPl2Watts = this.BiosDefaultEcoPl2Watts,
                    BiosDefaultQuietPl1Watts = this.BiosDefaultQuietPl1Watts,
                    BiosDefaultQuietPl2Watts = this.BiosDefaultQuietPl2Watts,
                    BiosDefaultBalancedPl1Watts = this.BiosDefaultBalancedPl1Watts,
                    BiosDefaultBalancedPl2Watts = this.BiosDefaultBalancedPl2Watts,
                    BiosDefaultPerformancePl1Watts = this.BiosDefaultPerformancePl1Watts,
                    BiosDefaultPerformancePl2Watts = this.BiosDefaultPerformancePl2Watts,
                    BiosDefaultTurboPl1Watts = this.BiosDefaultTurboPl1Watts,
                    BiosDefaultTurboPl2Watts = this.BiosDefaultTurboPl2Watts,

                    CustomEcoPl1Watts = this.CustomEcoPl1Watts,
                    CustomEcoPl2Watts = this.CustomEcoPl2Watts,
                    CustomQuietPl1Watts = this.CustomQuietPl1Watts,
                    CustomQuietPl2Watts = this.CustomQuietPl2Watts,
                    CustomBalancedPl1Watts = this.CustomBalancedPl1Watts,
                    CustomBalancedPl2Watts = this.CustomBalancedPl2Watts,
                    CustomPerformancePl1Watts = this.CustomPerformancePl1Watts,
                    CustomPerformancePl2Watts = this.CustomPerformancePl2Watts,
                    CustomTurboPl1Watts = this.CustomTurboPl1Watts,
                    CustomTurboPl2Watts = this.CustomTurboPl2Watts,

                    AntiThrottleEnabled = this.AntiThrottleEnabled,
                    SmoothPowerModeSwitching = this.SmoothPowerModeSwitching,
                    WindowsOverlaySyncEnabled = this.WindowsOverlaySyncEnabled,
                    PowerModeSettleMs = this.PowerModeSettleMs,
                    ThermalCpuWarnCelsius = this.ThermalCpuWarnCelsius,
                    ThermalCpuCeilingCelsius = this.ThermalCpuCeilingCelsius,
                    ThermalCpuCriticalCelsius = this.ThermalCpuCriticalCelsius,
                    ThermalGpuWarnCelsius = this.ThermalGpuWarnCelsius,
                    ThermalGpuCeilingCelsius = this.ThermalGpuCeilingCelsius,
                    ThermalGpuCriticalCelsius = this.ThermalGpuCriticalCelsius,
                    ThermalHysteresisCelsius = this.ThermalHysteresisCelsius,
                    ThermalMaxFanFloorPercent = this.ThermalMaxFanFloorPercent,
                    ThermalCpuPowerCapWatts = this.ThermalCpuPowerCapWatts,
                    ThermalMinPowerCapWatts = this.ThermalMinPowerCapWatts,
                    ThermalCriticalAlertEnabled = this.ThermalCriticalAlertEnabled
                };
                cpu = this.CpuCurve;
                gpu = this.GpuCurve;
                shortcuts = this.CustomModeShortcuts;
            }
            snap.CpuCurve = SafeCopyCurve(cpu);
            snap.GpuCurve = SafeCopyCurve(gpu);
            snap.CustomModeShortcuts = SafeCopyShortcuts(shortcuts);
            lock (this)
            {
                snap.BiosSettingsValues = this.BiosSettingsValues != null
                    ? new Dictionary<string, string>(this.BiosSettingsValues, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
            }
            return snap;
        }

        public ThermalSafetyProfile ToThermalProfile() => new ThermalSafetyProfile
        {
            Enabled = AntiThrottleEnabled,
            CpuWarnCelsius = ThermalCpuWarnCelsius,
            CpuCeilingCelsius = ThermalCpuCeilingCelsius,
            CpuCriticalCelsius = ThermalCpuCriticalCelsius,
            GpuWarnCelsius = ThermalGpuWarnCelsius,
            GpuCeilingCelsius = ThermalGpuCeilingCelsius,
            GpuCriticalCelsius = ThermalGpuCriticalCelsius,
            HysteresisCelsius = ThermalHysteresisCelsius,
            MaxFanFloorPercent = ThermalMaxFanFloorPercent,
            CpuPowerCapWatts = ThermalCpuPowerCapWatts,
            MinPowerCapWatts = ThermalMinPowerCapWatts,
            CriticalAlertEnabled = ThermalCriticalAlertEnabled
        }.Normalize();

        public void ApplyThermalProfile(ThermalSafetyProfile? profile)
        {
            if (profile == null) return;
            var p = profile.Normalize();
            AntiThrottleEnabled = p.Enabled;
            ThermalCpuWarnCelsius = p.CpuWarnCelsius;
            ThermalCpuCeilingCelsius = p.CpuCeilingCelsius;
            ThermalCpuCriticalCelsius = p.CpuCriticalCelsius;
            ThermalGpuWarnCelsius = p.GpuWarnCelsius;
            ThermalGpuCeilingCelsius = p.GpuCeilingCelsius;
            ThermalGpuCriticalCelsius = p.GpuCriticalCelsius;
            ThermalHysteresisCelsius = p.HysteresisCelsius;
            ThermalMaxFanFloorPercent = p.MaxFanFloorPercent;
            ThermalCpuPowerCapWatts = p.CpuPowerCapWatts;
            ThermalMinPowerCapWatts = p.MinPowerCapWatts;
            ThermalCriticalAlertEnabled = p.CriticalAlertEnabled;
        }

        private static string SanitizeColorList(string? value, int count, string? fallbackToken)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallbackToken == null
                    ? string.Empty
                    : string.Join(",", Enumerable.Repeat(fallbackToken, count));
            }

            var parsed = KeyboardLightingPalette.Parse(value, count, KeyboardLightingPalette.DefaultZoneColor);
            return KeyboardLightingPalette.Serialize(parsed);
        }

        public void Normalize() => Sanitize();

        public void Sanitize()
        {
            lock (this)
            {
                if (string.IsNullOrWhiteSpace(Theme) || !Enum.TryParse<AppTheme>(Theme, true, out _))
                    Theme = "System";

                if (CustomColors == null || CustomColors.Length != 16)
                {
                    var newArr = new int[16];
                    if (CustomColors != null)
                    {
                        Array.Copy(CustomColors, newArr, Math.Min(CustomColors.Length, 16));
                    }
                    CustomColors = newArr;
                }

                if (_customModeShortcuts == null || _customModeShortcuts.Count == 0)
                {
                    _customModeShortcuts = ModeShortcutValidator.CreateDefaultShortcuts();
                }
                else
                {
                    lock (_customModeShortcuts)
                    {
                        _customModeShortcuts.RemoveAll(s => s == null!);
                        if (_customModeShortcuts.Count == 0)
                        {
                            _customModeShortcuts = ModeShortcutValidator.CreateDefaultShortcuts();
                        }
                        else
                        {
                            for (int i = 0; i < _customModeShortcuts.Count; i++)
                            {
                                var s = _customModeShortcuts[i];
                                if (s == null) continue;
                                if (!ModeShortcutValidator.IsValid(s, out _))
                                {
                                    s.Enabled = false;
                                }
                            }

                            var seenCombos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < _customModeShortcuts.Count; i++)
                            {
                                var s = _customModeShortcuts[i];
                                if (s != null && s.Enabled && s.Key != Keys.None)
                                {
                                    string combo = $"{s.Key}:Ctrl={s.Ctrl}:Alt={s.Alt}:Shift={s.Shift}:Win={s.Win}";
                                    if (!seenCombos.Add(combo))
                                    {
                                        s.Enabled = false;
                                    }
                                }
                            }
                        }
                    }
                }

                PowerMode = Math.Clamp(PowerMode, (byte)0x00, (byte)0x06);
                if (PowerModeAC.HasValue)
                {
                    PowerModeAC = PowerModeAC.Value == 0xFF ? (byte)0xFF : Math.Clamp(PowerModeAC.Value, (byte)0x00, (byte)0x06);
                }
                if (PowerModeBattery.HasValue)
                {
                    PowerModeBattery = PowerModeBattery.Value == 0xFF ? (byte)0xFF : Math.Clamp(PowerModeBattery.Value, (byte)0x00, (byte)0x06);
                }

                AutoPowerAC = Math.Clamp(AutoPowerAC, 0, 10);
                AutoPowerBattery = Math.Clamp(AutoPowerBattery, 0, 10);

                FanMode = Math.Clamp(FanMode, (byte)0x01, (byte)0x03);
                if (FanModeAC.HasValue)
                {
                    FanModeAC = FanModeAC.Value == 0xFF ? (byte)0xFF : Math.Clamp(FanModeAC.Value, (byte)0x01, (byte)0x03);
                }
                if (FanModeBattery.HasValue)
                {
                    FanModeBattery = FanModeBattery.Value == 0xFF ? (byte)0xFF : Math.Clamp(FanModeBattery.Value, (byte)0x01, (byte)0x03);
                }

                AutoFanAC = Math.Clamp(AutoFanAC, 0, 10);
                AutoFanBattery = Math.Clamp(AutoFanBattery, 0, 10);
                FanHysteresisDegrees = Math.Clamp(FanHysteresisDegrees, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees);

            FanSpeedCpu = Math.Clamp(FanSpeedCpu, 0, 100);
            FanSpeedGpu = Math.Clamp(FanSpeedGpu, 0, 100);
            FanSpeedSys = Math.Clamp(FanSpeedSys, 0, 100);

            FanSpeedCpuAC = Math.Clamp(FanSpeedCpuAC, 0, 100);
            FanSpeedCpuBattery = Math.Clamp(FanSpeedCpuBattery, 0, 100);
            FanSpeedGpuAC = Math.Clamp(FanSpeedGpuAC, 0, 100);
            FanSpeedGpuBattery = Math.Clamp(FanSpeedGpuBattery, 0, 100);
            FanSpeedSysAC = Math.Clamp(FanSpeedSysAC, 0, 100);
            FanSpeedSysBattery = Math.Clamp(FanSpeedSysBattery, 0, 100);

            RefreshRate = Math.Clamp(RefreshRate, 0, 1000);
            DdsMode = Math.Clamp(DdsMode, 0, 2);

            RgbMode = Math.Clamp(RgbMode, 0, 33);
            RgbBrightness = Math.Clamp(RgbBrightness, 0, 100);
            RgbSpeed = Math.Clamp(RgbSpeed, 0, 100);
            RgbR = Math.Clamp(RgbR, 0, 255);
            RgbG = Math.Clamp(RgbG, 0, 255);
            RgbB = Math.Clamp(RgbB, 0, 255);

            KeyboardLightingTierOverride = Math.Clamp(KeyboardLightingTierOverride, 0, 4);
            KeyboardLayoutId = string.IsNullOrWhiteSpace(KeyboardLayoutId)
                ? KeyboardLayouts.AnsiTklId
                : KeyboardLayoutId.Trim();
            KeyboardZoneColors = SanitizeColorList(KeyboardZoneColors, 4, "#00A0FF");
            KeyboardPerKeyColors = SanitizeColorList(
                KeyboardPerKeyColors, KeyboardLayouts.AnsiTklKeyCount, fallbackToken: null);
            KeyboardCapabilityModel ??= string.Empty;
            KeyboardCapabilityNotes ??= string.Empty;
            ActiveLightingPreset = string.IsNullOrWhiteSpace(ActiveLightingPreset)
                ? "Custom"
                : ActiveLightingPreset.Trim();
            KeyboardZoneLayoutMode = KeyboardZoneLayoutMode switch { 0 or 1 or 3 or 4 => KeyboardZoneLayoutMode, _ => 4 };

            if (string.IsNullOrWhiteSpace(DynamicLightingEffectName) ||
                string.Equals(DynamicLightingEffectName.Trim(), "Twinkling", StringComparison.OrdinalIgnoreCase))
            {
                DynamicLightingEffectName = "Off";
            }
            else
            {
                DynamicLightingEffectName = DynamicLightingEffectName.Trim();
            }

            DynamicEffectSpeed = Math.Clamp(DynamicEffectSpeed, 1, 100);
            DynamicEffectDirection = Math.Clamp(DynamicEffectDirection, 0, 1);
            KeyboardBrightnessLevel = Math.Clamp(KeyboardBrightnessLevel, 0, 100);

            if (StaticZone1ColorArgb == 0 || StaticZone2ColorArgb == 0 || StaticZone3ColorArgb == 0 || StaticZone4ColorArgb == 0)
            {
                var parsedColors = KeyboardLightingPalette.Parse(KeyboardZoneColors, 4, Color.FromArgb(0, 160, 255));
                if (parsedColors.Length == 4)
                {
                    if (StaticZone1ColorArgb == 0) StaticZone1ColorArgb = parsedColors[0].ToArgb();
                    if (StaticZone2ColorArgb == 0) StaticZone2ColorArgb = parsedColors[1].ToArgb();
                    if (StaticZone3ColorArgb == 0) StaticZone3ColorArgb = parsedColors[2].ToArgb();
                    if (StaticZone4ColorArgb == 0) StaticZone4ColorArgb = parsedColors[3].ToArgb();
                }
            }

            StaticZoneColorMapJson ??= string.Empty;

            PowerModeSettleMs = Math.Clamp(PowerModeSettleMs, 100, 3000);

            var thermal = ToThermalProfile().Normalize();
            ApplyThermalProfile(thermal);

            GpuMode = Math.Clamp(GpuMode, 0, 5);
            ModeKeyAction = Math.Clamp(ModeKeyAction, 0, 1);
            TurboReturnMode = Math.Clamp(TurboReturnMode, (byte)0x00, (byte)0x06);

            BiosHardwareFingerprint ??= string.Empty;
            ScannedHardwareSignature ??= string.Empty;
            VerifiedBiosModesBitmask = Math.Clamp(VerifiedBiosModesBitmask, 0, 0x1F);

            if (CpuThermalSpecTdpWatts > 0)
                CpuThermalSpecTdpWatts = Math.Clamp(CpuThermalSpecTdpWatts, 5, 250);
            else if (CpuThermalSpecTdpWatts < 0)
                CpuThermalSpecTdpWatts = 0;

            if (CpuMinAllowedPowerWatts > 0)
                CpuMinAllowedPowerWatts = Math.Clamp(CpuMinAllowedPowerWatts, 5, 200);
            else if (CpuMinAllowedPowerWatts < 0)
                CpuMinAllowedPowerWatts = 0;

            if (CpuMaxAllowedPl1Watts > 0)
                CpuMaxAllowedPl1Watts = Math.Clamp(CpuMaxAllowedPl1Watts, Math.Max(5, CpuMinAllowedPowerWatts), 350);
            else if (CpuMaxAllowedPl1Watts < 0)
                CpuMaxAllowedPl1Watts = 0;

            if (CpuMaxAllowedPl2Watts > 0)
            {
                CpuMaxAllowedPl2Watts = Math.Clamp(CpuMaxAllowedPl2Watts, 5, 400);
                // Keep a narrower firmware PL1 ceiling instead of overwriting it with PL2 on every load.
                if (CpuMaxAllowedPl1Watts <= 0 || CpuMaxAllowedPl1Watts > CpuMaxAllowedPl2Watts)
                    CpuMaxAllowedPl1Watts = CpuMaxAllowedPl2Watts;
            }
            else if (CpuMaxAllowedPl2Watts < 0)
                CpuMaxAllowedPl2Watts = 0;
            if (ScannedMinPowerWatts > 0)
                ScannedMinPowerWatts = Math.Clamp(ScannedMinPowerWatts, 5, 200);
            if (ScannedMaxPl1Watts > 0)
                ScannedMaxPl1Watts = Math.Clamp(ScannedMaxPl1Watts, Math.Max(5, ScannedMinPowerWatts), 350);
            if (ScannedMaxPl2Watts > 0)
                ScannedMaxPl2Watts = Math.Clamp(ScannedMaxPl2Watts, Math.Max(5, ScannedMaxPl1Watts), 400);
            if (ScannedCpuTdpWatts > 0)
                ScannedCpuTdpWatts = Math.Clamp(ScannedCpuTdpWatts, 5, 250);

            static void SanitizePair(ref int pl1, ref int pl2, int minWatts, int maxPl1Watts, int maxPl2Watts)
            {
                if (pl1 != -1 && pl1 <= 0) pl1 = -1;
                if (pl2 != -1 && pl2 <= 0) pl2 = -1;

                if (minWatts > 0 && maxPl1Watts >= minWatts)
                {
                    int effectiveMaxPl2 = Math.Max(maxPl1Watts, maxPl2Watts > 0 ? maxPl2Watts : maxPl1Watts);
                    if (pl1 != -1) pl1 = Math.Clamp(pl1, minWatts, maxPl1Watts);
                    if (pl2 != -1) pl2 = Math.Clamp(pl2, pl1 > 0 ? pl1 : minWatts, effectiveMaxPl2);
                }
                else
                {
                    if (pl1 > 0 && pl2 > 0 && pl2 < pl1)
                    {
                        pl2 = pl1;
                    }
                }
            }

            int pMin = ScannedMinPowerWatts;
            int pMax1 = ScannedMaxPl1Watts;
            int pMax2 = ScannedMaxPl2Watts;

            int eco1 = CustomEcoPl1Watts, eco2 = CustomEcoPl2Watts;
            SanitizePair(ref eco1, ref eco2, pMin, pMax1, pMax2);
            CustomEcoPl1Watts = eco1; CustomEcoPl2Watts = eco2;

            int quiet1 = CustomQuietPl1Watts, quiet2 = CustomQuietPl2Watts;
            SanitizePair(ref quiet1, ref quiet2, pMin, pMax1, pMax2);
            CustomQuietPl1Watts = quiet1; CustomQuietPl2Watts = quiet2;

            int bal1 = CustomBalancedPl1Watts, bal2 = CustomBalancedPl2Watts;
            SanitizePair(ref bal1, ref bal2, pMin, pMax1, pMax2);
            CustomBalancedPl1Watts = bal1; CustomBalancedPl2Watts = bal2;

            int perf1 = CustomPerformancePl1Watts, perf2 = CustomPerformancePl2Watts;
            SanitizePair(ref perf1, ref perf2, pMin, pMax1, pMax2);
            CustomPerformancePl1Watts = perf1; CustomPerformancePl2Watts = perf2;

            int turbo1 = CustomTurboPl1Watts, turbo2 = CustomTurboPl2Watts;
            SanitizePair(ref turbo1, ref turbo2, pMin, pMax1, pMax2);
            CustomTurboPl1Watts = turbo1; CustomTurboPl2Watts = turbo2;

            if (BiosDefaultEcoPl1Watts != -1 && BiosDefaultEcoPl1Watts <= 0) BiosDefaultEcoPl1Watts = -1;
            if (BiosDefaultEcoPl2Watts != -1 && BiosDefaultEcoPl2Watts <= 0) BiosDefaultEcoPl2Watts = -1;
            if (BiosDefaultEcoPl1Watts > 0 && BiosDefaultEcoPl2Watts > 0 && BiosDefaultEcoPl2Watts < BiosDefaultEcoPl1Watts)
                BiosDefaultEcoPl2Watts = BiosDefaultEcoPl1Watts;

            if (BiosDefaultQuietPl1Watts != -1 && BiosDefaultQuietPl1Watts <= 0) BiosDefaultQuietPl1Watts = -1;
            if (BiosDefaultQuietPl2Watts != -1 && BiosDefaultQuietPl2Watts <= 0) BiosDefaultQuietPl2Watts = -1;
            if (BiosDefaultQuietPl1Watts > 0 && BiosDefaultQuietPl2Watts > 0 && BiosDefaultQuietPl2Watts < BiosDefaultQuietPl1Watts)
                BiosDefaultQuietPl2Watts = BiosDefaultQuietPl1Watts;

            if (BiosDefaultBalancedPl1Watts != -1 && BiosDefaultBalancedPl1Watts <= 0) BiosDefaultBalancedPl1Watts = -1;
            if (BiosDefaultBalancedPl2Watts != -1 && BiosDefaultBalancedPl2Watts <= 0) BiosDefaultBalancedPl2Watts = -1;
            if (BiosDefaultBalancedPl1Watts > 0 && BiosDefaultBalancedPl2Watts > 0 && BiosDefaultBalancedPl2Watts < BiosDefaultBalancedPl1Watts)
                BiosDefaultBalancedPl2Watts = BiosDefaultBalancedPl1Watts;

            if (BiosDefaultPerformancePl1Watts != -1 && BiosDefaultPerformancePl1Watts <= 0) BiosDefaultPerformancePl1Watts = -1;
            if (BiosDefaultPerformancePl2Watts != -1 && BiosDefaultPerformancePl2Watts <= 0) BiosDefaultPerformancePl2Watts = -1;
            if (BiosDefaultPerformancePl1Watts > 0 && BiosDefaultPerformancePl2Watts > 0 && BiosDefaultPerformancePl2Watts < BiosDefaultPerformancePl1Watts)
                BiosDefaultPerformancePl2Watts = BiosDefaultPerformancePl1Watts;

            if (BiosDefaultTurboPl1Watts != -1 && BiosDefaultTurboPl1Watts <= 0) BiosDefaultTurboPl1Watts = -1;
            if (BiosDefaultTurboPl2Watts != -1 && BiosDefaultTurboPl2Watts <= 0) BiosDefaultTurboPl2Watts = -1;
            if (BiosDefaultTurboPl1Watts > 0 && BiosDefaultTurboPl2Watts > 0 && BiosDefaultTurboPl2Watts < BiosDefaultTurboPl1Watts)
                BiosDefaultTurboPl2Watts = BiosDefaultTurboPl1Watts;

            var sanitizedThermal = ToThermalProfile().Normalize();
            ApplyThermalProfile(sanitizedThermal);
            PowerModeSettleMs = Math.Clamp(PowerModeSettleMs, 100, 2000);

            if (LastBatteryCalibrationUtcTicks < 0) LastBatteryCalibrationUtcTicks = 0;
            BatteryCalibrationPhase = Math.Clamp(BatteryCalibrationPhase, 0, 2);
            CalibrationLastKnownPercent = CalibrationLastKnownPercent < 0 ? -1 : Math.Clamp(CalibrationLastKnownPercent, 0, 100);

            if (!BiosModificationsEnabled) BiosDangerousAllowed = false;
                if (BiosSettingsValues == null)
                {
                    BiosSettingsValues = new(StringComparer.OrdinalIgnoreCase);
                }
                else if (BiosSettingsValues.Comparer != StringComparer.OrdinalIgnoreCase)
                {
                    BiosSettingsValues = new Dictionary<string, string>(BiosSettingsValues, StringComparer.OrdinalIgnoreCase);
                }
            }

            var cpu = _cpuCurve;
            if (cpu == null)
            {
                _cpuCurve = FanCurveGraph.DefaultCpuPoints.Select(p => new CurvePointData(p.X, p.Y)).ToList();
            }
            else
            {
                try
                {
                    lock (cpu)
                    {
                        var arr = cpu.ToArray();
                        var sanitized = new List<CurvePointData>(Math.Min(16, arr.Length));
                        for (int i = 0; i < arr.Length && sanitized.Count < 16; i++)
                        {
                            var pt = arr[i];
                            if (pt != null)
                            {
                                sanitized.Add(new CurvePointData(Math.Clamp(pt.X, 0, 100), Math.Clamp(pt.Y, 0, 100)));
                            }
                        }
                        if (sanitized.Count < 2)
                        {
                            sanitized = FanCurveGraph.DefaultCpuPoints.Select(p => new CurvePointData(p.X, p.Y)).ToList();
                        }
                        cpu.Clear();
                        cpu.AddRange(sanitized);
                    }
                }
                catch { }
            }

            var gpu = _gpuCurve;
            if (gpu == null)
            {
                _gpuCurve = FanCurveGraph.DefaultGpuPoints.Select(p => new CurvePointData(p.X, p.Y)).ToList();
            }
            else
            {
                try
                {
                    lock (gpu)
                    {
                        var arr = gpu.ToArray();
                        var sanitized = new List<CurvePointData>(Math.Min(16, arr.Length));
                        for (int i = 0; i < arr.Length && sanitized.Count < 16; i++)
                        {
                            var pt = arr[i];
                            if (pt != null)
                            {
                                sanitized.Add(new CurvePointData(Math.Clamp(pt.X, 0, 100), Math.Clamp(pt.Y, 0, 100)));
                            }
                        }
                        if (sanitized.Count < 2)
                        {
                            sanitized = FanCurveGraph.DefaultGpuPoints.Select(p => new CurvePointData(p.X, p.Y)).ToList();
                        }
                        gpu.Clear();
                        gpu.AddRange(sanitized);
                    }
                }
                catch { }
            }
        }

        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>
        /// Removes settings.json.&lt;guid&gt;.tmp files orphaned by an interrupted save (crash, forced kill).
        /// The save path deletes its own temp file in a finally block, so anything still present is
        /// debris from a process that never got to run it. Only files idle for over an hour are removed
        /// so an in-flight save can never be touched.
        /// </summary>
        internal static int CleanupStaleTempFilesIn(string path)
        {
            int removed = 0;

            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;

                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return 0;

                string pattern = Path.GetFileName(path) + ".*.tmp";
                foreach (var stale in dirInfo.GetFiles(pattern))
                {
                    try
                    {
                        if (DateTime.UtcNow - stale.LastWriteTimeUtc > TimeSpan.FromHours(1))
                        {
                            stale.Delete();
                            removed++;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return removed;
        }

        private static void CleanupOrphanedTempFiles()
        {
            CleanupStaleTempFilesIn(CustomFilePath ?? SettingsFilePath);
        }

        internal void RestoreMissingStaticZoneColors(bool zone1, bool zone2, bool zone3, bool zone4)
        {
            if (zone1 && zone2 && zone3 && zone4) return;
            var colors = KeyboardLightingPalette.Parse(KeyboardZoneColors, 4, Color.FromArgb(0, 160, 255));
            if (!zone1) StaticZone1ColorArgb = colors[0].ToArgb();
            if (!zone2) StaticZone2ColorArgb = colors[1].ToArgb();
            if (!zone3) StaticZone3ColorArgb = colors[2].ToArgb();
            if (!zone4) StaticZone4ColorArgb = colors[3].ToArgb();
        }

        internal void RestoreMissingStaticZoneColorsFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in doc.RootElement.EnumerateObject()) names.Add(property.Name);
                RestoreMissingStaticZoneColors(names.Contains(nameof(StaticZone1ColorArgb)), names.Contains(nameof(StaticZone2ColorArgb)),
                    names.Contains(nameof(StaticZone3ColorArgb)), names.Contains(nameof(StaticZone4ColorArgb)));
            }
            catch { }
        }

        private static bool TryGetRegistryInt(RegistryKey? key, string name, out int val)
        {
            val = 0;
            if (key == null) return false;
            var raw = key.GetValue(name);
            if (raw == null) return false;
            try
            {
                val = Convert.ToInt32(raw);
                return true;
            }
            catch { return false; }
        }

        public static AppSettings Load()
        {
            AppSettings settings = new();
            bool loadedFromFile = false;

            CleanupOrphanedTempFiles();

            string path = SettingsFilePath;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    lock (_fileLock)
                    {
                        if (File.Exists(path))
                        {
                            var fi = new FileInfo(path);
                            // Prevent unbounded read / DoS on oversized settings files (> 1MB)
                            // Also verify not a symlink / reparse point
                            if (fi.Length > 0 && fi.Length <= 1024 * 1024 && (fi.Attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                string json = File.ReadAllText(path);
                                if (!string.IsNullOrWhiteSpace(json))
                                {
                                    var parsed = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions);
                                    if (parsed != null)
                                    {
                                        if (parsed.BiosSettingsValues != null)
                                        {
                                            parsed.BiosSettingsValues = new Dictionary<string, string>(parsed.BiosSettingsValues, StringComparer.OrdinalIgnoreCase);
                                        }
                                        else
                                        {
                                            parsed.BiosSettingsValues = new(StringComparer.OrdinalIgnoreCase);
                                        }
                                        parsed.RestoreMissingStaticZoneColorsFromJson(json);
                                        parsed.Sanitize();
                                        settings = parsed;
                                        loadedFromFile = true;
                                    }
                                }
                            }
                        }
                    }
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    System.Threading.Thread.Sleep(25 * (attempt + 1));
                }
                catch (JsonException)
                {
                    try
                    {
                        lock (_fileLock)
                        {
                            if (File.Exists(path))
                            {
                                File.Copy(path, path + ".corrupt", true);
                            }
                        }
                    }
                    catch { }
                    break;
                }
                catch
                {
                    break;
                }
            }

            // Atomic backup recovery: if main file is missing, empty, or corrupted, load from backup
            if (!loadedFromFile)
            {
                string bakPath = path + ".bak";
                try
                {
                    lock (_fileLock)
                    {
                        if (File.Exists(bakPath))
                        {
                            var fi = new FileInfo(bakPath);
                            if (fi.Length > 0 && fi.Length <= 1024 * 1024 && (fi.Attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                string bakJson = File.ReadAllText(bakPath);
                                if (!string.IsNullOrWhiteSpace(bakJson))
                                {
                                    var parsed = JsonSerializer.Deserialize<AppSettings>(bakJson, s_jsonOptions);
                                    if (parsed != null)
                                    {
                                        if (parsed.BiosSettingsValues != null)
                                        {
                                            parsed.BiosSettingsValues = new Dictionary<string, string>(parsed.BiosSettingsValues, StringComparer.OrdinalIgnoreCase);
                                        }
                                        else
                                        {
                                            parsed.BiosSettingsValues = new(StringComparer.OrdinalIgnoreCase);
                                        }
                                        parsed.RestoreMissingStaticZoneColorsFromJson(bakJson);
                                        parsed.Sanitize();
                                        settings = parsed;
                                        loadedFromFile = true;
                                        try { File.Copy(bakPath, path, true); } catch { }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // If not loaded from file or partial, fall back / hydrate from Registry
            if (CustomFilePath == null)
            {
                lock (RegistrySafety.RegistryLock)
            {
                try
                {
                    using var hkcu = RegistrySafety.OpenSafeSubKey(Registry.CurrentUser, RegistrySafety.BaseKeyPath);
                    using var hklm = RegistrySafety.OpenSafeSubKey(Registry.LocalMachine, RegistrySafety.BaseKeyPath);
                    var key = hkcu ?? hklm;
                    if (key != null)
                    {
                        if (!loadedFromFile || string.IsNullOrWhiteSpace(settings.Theme))
                        {
                            if (key.GetValue("AppTheme") is int th && Enum.IsDefined(typeof(AppTheme), th) && (AppTheme)th != AppTheme.System)
                                settings.Theme = ((AppTheme)th).ToString();
                            else if (key.GetValue("Theme") is string thStr && Enum.TryParse<AppTheme>(thStr, out var thVal) && thVal != AppTheme.System)
                                settings.Theme = thVal.ToString();
                        }
                        if (!loadedFromFile)
                        {
                            if (key.GetValue("Power") is int p) settings.PowerMode = (byte)Math.Clamp(p, 0, 255);
                            if (key.GetValue("Power_AC") is int pac) settings.PowerModeAC = (byte)Math.Clamp(pac, 0, 255);
                            if (key.GetValue("Power_Battery") is int pbat) settings.PowerModeBattery = (byte)Math.Clamp(pbat, 0, 255);
                            if (key.GetValue("AutoPowerAC") is int apac) settings.AutoPowerAC = apac;
                            if (key.GetValue("AutoPowerBattery") is int apbat) settings.AutoPowerBattery = apbat;

                            if (key.GetValue("Fan") is int f) settings.FanMode = (byte)Math.Clamp(f, 0, 255);
                            if (key.GetValue("Fan_AC") is int fac) settings.FanModeAC = (byte)Math.Clamp(fac, 0, 255);
                            if (key.GetValue("Fan_Battery") is int fbat) settings.FanModeBattery = (byte)Math.Clamp(fbat, 0, 255);
                            if (key.GetValue("AutoFanAC") is int afac) settings.AutoFanAC = afac;
                            if (key.GetValue("AutoFanBattery") is int afbat) settings.AutoFanBattery = afbat;
                            if (key.GetValue("FanCurveEnabled") is int fce) settings.FanCurveEnabled = fce == 1;
                            if (key.GetValue("FanHysteresisDegrees") is int fhd) settings.FanHysteresisDegrees = Math.Clamp(fhd, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees);
                            else if (key.GetValue("HysteresisDegrees") is int hd) settings.FanHysteresisDegrees = Math.Clamp(hd, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees);
                            else if (key.GetValue("FanCurveHysteresis") is int fch) settings.FanHysteresisDegrees = Math.Clamp(fch, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees);

                            if (key.GetValue("FanSpeedCpu") is int fsc) settings.FanSpeedCpu = fsc;
                            if (key.GetValue("FanSpeedGpu") is int fsg) settings.FanSpeedGpu = fsg;
                            if (key.GetValue("FanSpeedSys") is int fss) settings.FanSpeedSys = fss;
                            if (key.GetValue("FanSpeedCpuAC") is int fscac) settings.FanSpeedCpuAC = fscac;
                            if (key.GetValue("FanSpeedCpuBattery") is int fscbat) settings.FanSpeedCpuBattery = fscbat;
                            if (key.GetValue("FanSpeedGpuAC") is int fsgac) settings.FanSpeedGpuAC = fsgac;
                            if (key.GetValue("FanSpeedGpuBattery") is int fsgbat) settings.FanSpeedGpuBattery = fsgbat;
                            if (key.GetValue("FanSpeedSysAC") is int fssac) settings.FanSpeedSysAC = fssac;
                            if (key.GetValue("FanSpeedSysBattery") is int fssbat) settings.FanSpeedSysBattery = fssbat;

                            if (key.GetValue("RefreshRate") is int rr) settings.RefreshRate = rr;
                            if (key.GetValue("LcdOverdrive") is int od) settings.LcdOverdrive = od == 1;
                            if (key.GetValue("DdsMode") is int dds) settings.DdsMode = dds;
                            if (key.GetValue("BiosModificationsEnabled") is int bme) settings.BiosModificationsEnabled = bme == 1;
                            if (key.GetValue("BiosDangerousAllowed") is int bda) settings.BiosDangerousAllowed = bda == 1;
                            if (key.GetValue("BatteryLimit") is int bl) settings.BatteryLimit = bl == 1;
                            if (key.GetValue("LastBatteryCalibrationUtcTicks") is long lbct) settings.LastBatteryCalibrationUtcTicks = lbct;
                            else if (key.GetValue("LastBatteryCalibrationUtcTicks") is string lbcts && long.TryParse(lbcts, out var parsedTicks)) settings.LastBatteryCalibrationUtcTicks = parsedTicks;
                            if (key.GetValue("BatteryCalibrationPhase") is int bcp) settings.BatteryCalibrationPhase = bcp;
                            if (key.GetValue("CalibrationLastKnownPercent") is int clkp) settings.CalibrationLastKnownPercent = clkp;
                            if (key.GetValue("CalibrationPreChargeLimitEnabled") is int cpcle) settings.CalibrationPreChargeLimitEnabled = cpcle == 1;
                            if (key.GetValue("CoolBoost") is int cb) settings.CoolBoost = cb == 1;

                            if (key.GetValue("RGB_Mode") is int rm) settings.RgbMode = rm;
                            bool hasKbl = TryGetRegistryInt(key, "KeyboardBrightnessLevel", out int kbl);
                            bool hasBr = TryGetRegistryInt(key, "Brightness", out int br);
                            if (hasKbl)
                            {
                                settings.KeyboardBrightnessLevel = kbl;
                                settings.RgbBrightness = kbl;
                            }
                            else if (hasBr)
                            {
                                settings.KeyboardBrightnessLevel = br;
                                settings.RgbBrightness = br;
                            }
                            if (key.GetValue("RGB_Speed") is int sp) settings.RgbSpeed = sp;
                            if (key.GetValue("RGB_R") is int r) settings.RgbR = r;
                            if (key.GetValue("RGB_G") is int g) settings.RgbG = g;
                            if (key.GetValue("RGB_B") is int b) settings.RgbB = b;

                            if (key.GetValue("CustomColors") is string ccStr && !string.IsNullOrWhiteSpace(ccStr))
                            {
                                var parts = ccStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                                var arr = new int[16];
                                for (int i = 0; i < Math.Min(16, parts.Length); i++)
                                {
                                    if (int.TryParse(parts[i], out int cVal)) arr[i] = cVal;
                                }
                                settings.CustomColors = arr;
                            }

                            if (key.GetValue("KeyboardLightingTierOverride") is int klto) settings.KeyboardLightingTierOverride = klto;
                            if (key.GetValue("KeyboardZoneColors") is string kzc && !string.IsNullOrWhiteSpace(kzc)) settings.KeyboardZoneColors = kzc;
                            if (key.GetValue("KeyboardPerKeyColors") is string kpkc && !string.IsNullOrWhiteSpace(kpkc)) settings.KeyboardPerKeyColors = kpkc;
                            if (key.GetValue("IsZoneLightingActive") is int izla) settings.IsZoneLightingActive = izla == 1;
                            if (key.GetValue("ActiveLightingPreset") is string alp && !string.IsNullOrWhiteSpace(alp)) settings.ActiveLightingPreset = alp;
                            if (key.GetValue("KeyboardZoneLayoutMode") is int kzlm) settings.KeyboardZoneLayoutMode = kzlm;
                            if (key.GetValue("KeyboardPerKeyEnabled") is int kpke) settings.KeyboardPerKeyEnabled = kpke == 1;
                            if (key.GetValue("KeyboardLayoutId") is string kli && !string.IsNullOrWhiteSpace(kli)) settings.KeyboardLayoutId = kli;

                            bool hasZ1 = TryGetRegistryInt(key, "StaticZone1ColorArgb", out var z1);
                            if (hasZ1) settings.StaticZone1ColorArgb = z1;
                            bool hasZ2 = TryGetRegistryInt(key, "StaticZone2ColorArgb", out var z2);
                            if (hasZ2) settings.StaticZone2ColorArgb = z2;
                            bool hasZ3 = TryGetRegistryInt(key, "StaticZone3ColorArgb", out var z3);
                            if (hasZ3) settings.StaticZone3ColorArgb = z3;
                            bool hasZ4 = TryGetRegistryInt(key, "StaticZone4ColorArgb", out var z4);
                            if (hasZ4) settings.StaticZone4ColorArgb = z4;
                            settings.RestoreMissingStaticZoneColors(hasZ1, hasZ2, hasZ3, hasZ4);
                            if (key.GetValue("StaticZoneColorMapJson") is string zcm && !string.IsNullOrWhiteSpace(zcm)) settings.StaticZoneColorMapJson = zcm;

                            if (key.GetValue("DynamicLightingEffectName") is string dlen && !string.IsNullOrWhiteSpace(dlen))
                            {
                                settings.DynamicLightingEffectName = string.Equals(dlen.Trim(), "Twinkling", StringComparison.OrdinalIgnoreCase) ? "Off" : dlen.Trim();
                            }
                            else if (!settings.IsZoneLightingActive && key.GetValue("RGB_Mode") is int rmLegacy)
                            {
                                if (rmLegacy == 0) settings.DynamicLightingEffectName = "Off";
                                else if (rmLegacy > 0 && rmLegacy < LightingEffectsManager.EffectNames.Length)
                                {
                                    string legacyName = LightingEffectsManager.EffectNames[rmLegacy];
                                    settings.DynamicLightingEffectName = string.Equals(legacyName, "Twinkling", StringComparison.OrdinalIgnoreCase) ? "Off" : legacyName;
                                }
                            }

                            if (key.GetValue("DynamicEffectPrimaryColorArgb") is int depc) settings.DynamicEffectPrimaryColorArgb = depc;
                            else if (key.GetValue("RGB_R") is int rgbR && key.GetValue("RGB_G") is int rgbG && key.GetValue("RGB_B") is int rgbB)
                            {
                                settings.DynamicEffectPrimaryColorArgb = Color.FromArgb(rgbR, rgbG, rgbB).ToArgb();
                            }

                            if (key.GetValue("DynamicEffectSecondaryColorArgb") is int desc) settings.DynamicEffectSecondaryColorArgb = desc;
                            if (key.GetValue("DynamicEffectSpeed") is int desp) settings.DynamicEffectSpeed = desp;
                            else if (key.GetValue("RGB_Speed") is int spLegacy) settings.DynamicEffectSpeed = spLegacy;

                            if (key.GetValue("DynamicEffectDirection") is int ded) settings.DynamicEffectDirection = ded;

                            if (key.GetValue("SmoothPowerModeSwitching") is int spms) settings.SmoothPowerModeSwitching = spms == 1;
                            if (key.GetValue("WindowsOverlaySyncEnabled") is int wose) settings.WindowsOverlaySyncEnabled = wose == 1;
                            if (key.GetValue("PowerModeSettleMs") is int pmsm) settings.PowerModeSettleMs = pmsm;

                            if (key.GetValue("ThermalCpuWarnCelsius") is int tcw) settings.ThermalCpuWarnCelsius = tcw;
                            if (key.GetValue("ThermalCpuCeilingCelsius") is int tcc) settings.ThermalCpuCeilingCelsius = tcc;
                            if (key.GetValue("ThermalCpuCriticalCelsius") is int tcx) settings.ThermalCpuCriticalCelsius = tcx;
                            if (key.GetValue("ThermalGpuWarnCelsius") is int tgw) settings.ThermalGpuWarnCelsius = tgw;
                            if (key.GetValue("ThermalGpuCeilingCelsius") is int tgc) settings.ThermalGpuCeilingCelsius = tgc;
                            if (key.GetValue("ThermalGpuCriticalCelsius") is int tgx) settings.ThermalGpuCriticalCelsius = tgx;
                            if (key.GetValue("ThermalHysteresisCelsius") is int thc) settings.ThermalHysteresisCelsius = thc;
                            if (key.GetValue("ThermalMaxFanFloorPercent") is int tmff) settings.ThermalMaxFanFloorPercent = tmff;
                            if (key.GetValue("ThermalCpuPowerCapWatts") is int tcpw) settings.ThermalCpuPowerCapWatts = tcpw;
                            if (key.GetValue("ThermalMinPowerCapWatts") is int tmpw) settings.ThermalMinPowerCapWatts = tmpw;
                            if (key.GetValue("ThermalCriticalAlertEnabled") is int tcae) settings.ThermalCriticalAlertEnabled = tcae == 1;
                            if (key.GetValue("ModeKeyAction") is int mka) settings.ModeKeyAction = mka;
                            if (key.GetValue("TurboReturnMode") is int trm) settings.TurboReturnMode = (byte)Math.Clamp(trm, 0, 255);

                            if (key.GetValue("EnableCustomPowerLimits") is int ecpl) settings.EnableCustomPowerLimits = ecpl == 1;
                            if (key.GetValue("HardwareHasActiveCustomPowerOverride") is int hhacpo) settings.HardwareHasActiveCustomPowerOverride = hhacpo == 1;
                            string? fp = key.GetValue("BiosHardwareFingerprint") as string ?? key.GetValue("ScannedHardwareSignature") as string;
                            if (!string.IsNullOrEmpty(fp)) settings.BiosHardwareFingerprint = fp;

                            if (key.GetValue("HasCompletedBiosPowerScan") is int hcbps) settings.HasCompletedBiosPowerScan = hcbps == 1;
                            else if (key.GetValue("HasScannedHardwarePowerBounds") is int hshpb) settings.HasCompletedBiosPowerScan = hshpb == 1;

                            if (key.GetValue("CpuThermalSpecTdpWatts") is int cttdp) settings.CpuThermalSpecTdpWatts = cttdp;
                            else if (key.GetValue("ScannedCpuTdpWatts") is int sctdp) settings.CpuThermalSpecTdpWatts = sctdp;

                            if (key.GetValue("CpuMinAllowedPowerWatts") is int cmapw) settings.CpuMinAllowedPowerWatts = cmapw;
                            else if (key.GetValue("ScannedMinPowerWatts") is int smpw) settings.CpuMinAllowedPowerWatts = smpw;

                            if (key.GetValue("CpuMaxAllowedPl1Watts") is int cmapl1) settings.CpuMaxAllowedPl1Watts = cmapl1;
                            else if (key.GetValue("ScannedMaxPl1Watts") is int smpl1) settings.CpuMaxAllowedPl1Watts = smpl1;

                            if (key.GetValue("CpuMaxAllowedPl2Watts") is int cmapl2) settings.CpuMaxAllowedPl2Watts = cmapl2;
                            else if (key.GetValue("ScannedMaxPl2Watts") is int smpl2) settings.CpuMaxAllowedPl2Watts = smpl2;

                            if (key.GetValue("VerifiedBiosModesBitmask") is int vbmb) settings.VerifiedBiosModesBitmask = vbmb;

                            if (key.GetValue("BiosDefaultEcoPl1Watts") is int bdep1) settings.BiosDefaultEcoPl1Watts = bdep1;
                            else if (key.GetValue("DefaultEcoPl1Watts") is int dep1) settings.DefaultEcoPl1Watts = dep1;

                            if (key.GetValue("BiosDefaultEcoPl2Watts") is int bdep2) settings.BiosDefaultEcoPl2Watts = bdep2;
                            else if (key.GetValue("DefaultEcoPl2Watts") is int dep2) settings.DefaultEcoPl2Watts = dep2;

                            if (key.GetValue("BiosDefaultQuietPl1Watts") is int bdqp1) settings.BiosDefaultQuietPl1Watts = bdqp1;
                            else if (key.GetValue("DefaultQuietPl1Watts") is int dqp1) settings.DefaultQuietPl1Watts = dqp1;

                            if (key.GetValue("BiosDefaultQuietPl2Watts") is int bdqp2) settings.BiosDefaultQuietPl2Watts = bdqp2;
                            else if (key.GetValue("DefaultQuietPl2Watts") is int dqp2) settings.DefaultQuietPl2Watts = dqp2;

                            if (key.GetValue("BiosDefaultBalancedPl1Watts") is int bdbp1) settings.BiosDefaultBalancedPl1Watts = bdbp1;
                            else if (key.GetValue("DefaultBalancedPl1Watts") is int dbp1) settings.BiosDefaultBalancedPl1Watts = dbp1;

                            if (key.GetValue("BiosDefaultBalancedPl2Watts") is int bdbp2) settings.BiosDefaultBalancedPl2Watts = bdbp2;
                            else if (key.GetValue("DefaultBalancedPl2Watts") is int dbp2) settings.DefaultBalancedPl2Watts = dbp2;

                            if (key.GetValue("BiosDefaultPerformancePl1Watts") is int bdpp1) settings.BiosDefaultPerformancePl1Watts = bdpp1;
                            else if (key.GetValue("DefaultPerformancePl1Watts") is int dpp1) settings.DefaultPerformancePl1Watts = dpp1;

                            if (key.GetValue("BiosDefaultPerformancePl2Watts") is int bdpp2) settings.BiosDefaultPerformancePl2Watts = bdpp2;
                            else if (key.GetValue("DefaultPerformancePl2Watts") is int dpp2) settings.DefaultPerformancePl2Watts = dpp2;

                            if (key.GetValue("BiosDefaultTurboPl1Watts") is int bdtp1) settings.BiosDefaultTurboPl1Watts = bdtp1;
                            else if (key.GetValue("DefaultTurboPl1Watts") is int dtp1) settings.DefaultTurboPl1Watts = dtp1;

                            if (key.GetValue("BiosDefaultTurboPl2Watts") is int bdtp2) settings.BiosDefaultTurboPl2Watts = bdtp2;
                            else if (key.GetValue("DefaultTurboPl2Watts") is int dtp2) settings.DefaultTurboPl2Watts = dtp2;

                            if (key.GetValue("CustomEcoPl1Watts") is int cep1) settings.CustomEcoPl1Watts = cep1;
                            else if (key.GetValue("EcoPl1Watts") is int ep1) settings.EcoPl1Watts = ep1;

                            if (key.GetValue("CustomEcoPl2Watts") is int cep2) settings.CustomEcoPl2Watts = cep2;
                            else if (key.GetValue("EcoPl2Watts") is int ep2) settings.EcoPl2Watts = ep2;

                            if (key.GetValue("CustomQuietPl1Watts") is int cqp1) settings.CustomQuietPl1Watts = cqp1;
                            else if (key.GetValue("QuietPl1Watts") is int qp1) settings.QuietPl1Watts = qp1;

                            if (key.GetValue("CustomQuietPl2Watts") is int cqp2) settings.CustomQuietPl2Watts = cqp2;
                            else if (key.GetValue("QuietPl2Watts") is int qp2) settings.QuietPl2Watts = qp2;

                            if (key.GetValue("CustomBalancedPl1Watts") is int cbp1) settings.CustomBalancedPl1Watts = cbp1;
                            else if (key.GetValue("BalancedPl1Watts") is int bp1) settings.BalancedPl1Watts = bp1;

                            if (key.GetValue("CustomBalancedPl2Watts") is int cbp2) settings.CustomBalancedPl2Watts = cbp2;
                            else if (key.GetValue("BalancedPl2Watts") is int bp2) settings.BalancedPl2Watts = bp2;

                            if (key.GetValue("CustomPerformancePl1Watts") is int cpp1) settings.CustomPerformancePl1Watts = cpp1;
                            else if (key.GetValue("PerformancePl1Watts") is int pp1) settings.PerformancePl1Watts = pp1;

                            if (key.GetValue("CustomPerformancePl2Watts") is int cpp2) settings.CustomPerformancePl2Watts = cpp2;
                            else if (key.GetValue("PerformancePl2Watts") is int pp2) settings.PerformancePl2Watts = pp2;

                            if (key.GetValue("CustomTurboPl1Watts") is int ctp1) settings.CustomTurboPl1Watts = ctp1;
                            else if (key.GetValue("TurboPl1Watts") is int tp1) settings.TurboPl1Watts = tp1;

                            if (key.GetValue("CustomTurboPl2Watts") is int ctp2) settings.CustomTurboPl2Watts = ctp2;
                            else if (key.GetValue("TurboPl2Watts") is int tp2) settings.TurboPl2Watts = tp2;
                        }
                    }
                }
                catch { }

                if (!loadedFromFile)
                {
                    RegistrySafety.LoadCpuPowerLimits(settings);
                    RegistrySafety.LoadCalibrationState(settings);
                }

                // Hydrate persistent BIOS settings from Registry HKCU\SOFTWARE\PredatorControl\BiosSettings
                try
                {
                    using var biosKey = RegistrySafety.OpenSafeSubKey(Registry.CurrentUser, @"SOFTWARE\PredatorControl\BiosSettings");
                    if (biosKey != null)
                    {
                        foreach (var valName in biosKey.GetValueNames())
                        {
                            if (!string.IsNullOrWhiteSpace(valName))
                            {
                                var valObj = biosKey.GetValue(valName);
                                if (valObj != null)
                                {
                                    string strVal = valObj.ToString() ?? "";
                                    if (!string.IsNullOrEmpty(strVal))
                                    {
                                        if (!settings.BiosSettingsValues.ContainsKey(valName))
                                        {
                                            settings.BiosSettingsValues[valName] = strVal;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            }

            settings.Sanitize();
            return settings;
        }

        public void Save()
        {
            AppSettings snapshot;
            lock (this)
            {
                try { Sanitize(); } catch { }
                snapshot = CreateSnapshot();
            }

            try
            {
                string path = SettingsFilePath;
                string dir = Path.GetDirectoryName(path) ?? Path.GetTempPath();
                if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();

                // Reparse point / directory junction defense:
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.Exists && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                lock (_fileLock)
                {
                    try
                    {
                        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        {
                            return;
                        }
                    }
                    catch (IOException) { }

                    string json;
                    try
                    {
                        json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    }
                    catch
                    {
                        json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                    }

                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
                        try
                        {
                            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                            using (var sw = new StreamWriter(fs, Encoding.UTF8))
                            {
                                sw.Write(json);
                                sw.Flush();
                                fs.Flush(true);
                            }
                            if (File.Exists(path))
                            {
                                var attrs = File.GetAttributes(path);
                                if ((attrs & FileAttributes.ReparsePoint) != 0)
                                {
                                    try { File.Delete(tmp); } catch { }
                                    return;
                                }
                                if ((attrs & FileAttributes.ReadOnly) != 0)
                                {
                                    try { File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly); } catch { }
                                }
                            }
                            string bak = path + ".bak";
                            if (File.Exists(path))
                            {
                                if (File.Exists(bak))
                                {
                                    var bakAttrs = File.GetAttributes(bak);
                                    if ((bakAttrs & FileAttributes.ReadOnly) != 0)
                                    {
                                        File.SetAttributes(bak, bakAttrs & ~FileAttributes.ReadOnly);
                                    }
                                }
                                try
                                {
                                    File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                                }
                                catch
                                {
                                    File.Copy(path, bak, true);
                                    File.Move(tmp, path, true);
                                }
                            }
                            else
                            {
                                File.Move(tmp, path, true);
                            }
                            break;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 7)
                        {
                            System.Threading.Thread.Sleep(30 * (attempt + 1));
                        }
                        finally
                        {
                            if (File.Exists(tmp))
                            {
                                try { File.Delete(tmp); } catch { }
                            }
                        }
                    }
                }
            }
            catch { }

            if (CustomFilePath != null) return;

            // Sync to Registry
            lock (RegistrySafety.RegistryLock)
            {
                try
                {
                    using var key = RegistrySafety.CreateSafeSubKey(Registry.CurrentUser, RegistrySafety.BaseKeyPath);
                    if (key != null)
                    {
                        var savedTheme = (!string.IsNullOrWhiteSpace(snapshot.Theme) && Enum.TryParse<AppTheme>(snapshot.Theme, true, out var t) && t != AppTheme.System)
                            ? snapshot.Theme
                            : (ThemeManager.CurrentTheme != AppTheme.System ? ThemeManager.CurrentTheme.ToString() : snapshot.Theme);

                        if (Enum.TryParse<AppTheme>(savedTheme, out var th))
                        {
                            key.SetValue("AppTheme", (int)th);
                            key.SetValue("Theme", savedTheme);
                        }

                        key.SetValue("Power", snapshot.PowerMode);
                        if (snapshot.PowerModeAC.HasValue) key.SetValue("Power_AC", snapshot.PowerModeAC.Value);
                        if (snapshot.PowerModeBattery.HasValue) key.SetValue("Power_Battery", snapshot.PowerModeBattery.Value);
                        key.SetValue("AutoPowerAC", snapshot.AutoPowerAC);
                        key.SetValue("AutoPowerBattery", snapshot.AutoPowerBattery);

                        key.SetValue("ModeKeyAction", snapshot.ModeKeyAction);
                        key.SetValue("TurboReturnMode", snapshot.TurboReturnMode);

                        key.SetValue("Fan", snapshot.FanMode);
                        if (snapshot.FanModeAC.HasValue) key.SetValue("Fan_AC", snapshot.FanModeAC.Value);
                        if (snapshot.FanModeBattery.HasValue) key.SetValue("Fan_Battery", snapshot.FanModeBattery.Value);
                        key.SetValue("AutoFanAC", snapshot.AutoFanAC);
                        key.SetValue("AutoFanBattery", snapshot.AutoFanBattery);
                        key.SetValue("FanCurveEnabled", snapshot.FanCurveEnabled ? 1 : 0);
                        key.SetValue("FanHysteresisDegrees", snapshot.FanHysteresisDegrees);
                        key.SetValue("HysteresisDegrees", snapshot.FanHysteresisDegrees);

                        key.SetValue("FanSpeedCpu", snapshot.FanSpeedCpu);
                        key.SetValue("FanSpeedGpu", snapshot.FanSpeedGpu);
                        key.SetValue("FanSpeedSys", snapshot.FanSpeedSys);
                        key.SetValue("FanSpeedCpuAC", snapshot.FanSpeedCpuAC);
                        key.SetValue("FanSpeedCpuBattery", snapshot.FanSpeedCpuBattery);
                        key.SetValue("FanSpeedGpuAC", snapshot.FanSpeedGpuAC);
                        key.SetValue("FanSpeedGpuBattery", snapshot.FanSpeedGpuBattery);
                        key.SetValue("FanSpeedSysAC", snapshot.FanSpeedSysAC);
                        key.SetValue("FanSpeedSysBattery", snapshot.FanSpeedSysBattery);

                        if (snapshot.RefreshRate > 0) key.SetValue("RefreshRate", snapshot.RefreshRate);
                        key.SetValue("LcdOverdrive", snapshot.LcdOverdrive ? 1 : 0);
                        key.SetValue("DdsMode", snapshot.DdsMode);
                        key.SetValue("BiosModificationsEnabled", snapshot.BiosModificationsEnabled ? 1 : 0);
                        key.SetValue("BiosDangerousAllowed", snapshot.BiosDangerousAllowed ? 1 : 0);
                        key.SetValue("BatteryLimit", snapshot.BatteryLimit ? 1 : 0);
                        key.SetValue("LastBatteryCalibrationUtcTicks", snapshot.LastBatteryCalibrationUtcTicks, RegistryValueKind.QWord);
                        key.SetValue("BatteryCalibrationPhase", snapshot.BatteryCalibrationPhase, RegistryValueKind.DWord);
                        key.SetValue("CalibrationLastKnownPercent", snapshot.CalibrationLastKnownPercent, RegistryValueKind.DWord);
                        key.SetValue("CalibrationPreChargeLimitEnabled", snapshot.CalibrationPreChargeLimitEnabled ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("CoolBoost", snapshot.CoolBoost ? 1 : 0);

                        key.SetValue("RGB_Mode", snapshot.RgbMode);
                        key.SetValue("Brightness", snapshot.RgbBrightness, RegistryValueKind.DWord);
                        key.SetValue("KeyboardBrightnessLevel", snapshot.KeyboardBrightnessLevel, RegistryValueKind.DWord);
                        key.SetValue("RGB_Speed", snapshot.RgbSpeed);
                        key.SetValue("RGB_R", snapshot.RgbR);
                        key.SetValue("RGB_G", snapshot.RgbG);
                        key.SetValue("RGB_B", snapshot.RgbB);

                        if (snapshot.CustomColors != null && snapshot.CustomColors.Length > 0)
                        {
                            key.SetValue("CustomColors", string.Join(",", snapshot.CustomColors));
                        }

                        key.SetValue("KeyboardLightingTierOverride", snapshot.KeyboardLightingTierOverride);
                        key.SetValue("KeyboardZoneColors", snapshot.KeyboardZoneColors ?? "");
                        key.SetValue("KeyboardPerKeyColors", snapshot.KeyboardPerKeyColors ?? "");
                        key.SetValue("IsZoneLightingActive", snapshot.IsZoneLightingActive ? 1 : 0);
                        key.SetValue("ActiveLightingPreset", snapshot.ActiveLightingPreset ?? "Custom");
                        key.SetValue("KeyboardZoneLayoutMode", snapshot.KeyboardZoneLayoutMode);
                        key.SetValue("KeyboardPerKeyEnabled", snapshot.KeyboardPerKeyEnabled ? 1 : 0);
                        key.SetValue("KeyboardLayoutId", snapshot.KeyboardLayoutId ?? "");

                        key.SetValue("StaticZone1ColorArgb", snapshot.StaticZone1ColorArgb, RegistryValueKind.DWord);
                        key.SetValue("StaticZone2ColorArgb", snapshot.StaticZone2ColorArgb, RegistryValueKind.DWord);
                        key.SetValue("StaticZone3ColorArgb", snapshot.StaticZone3ColorArgb, RegistryValueKind.DWord);
                        key.SetValue("StaticZone4ColorArgb", snapshot.StaticZone4ColorArgb, RegistryValueKind.DWord);
                        key.SetValue("StaticZoneColorMapJson", snapshot.StaticZoneColorMapJson ?? string.Empty);

                        key.SetValue("DynamicLightingEffectName", snapshot.DynamicLightingEffectName ?? "Off");
                        key.SetValue("DynamicEffectPrimaryColorArgb", snapshot.DynamicEffectPrimaryColorArgb, RegistryValueKind.DWord);
                        key.SetValue("DynamicEffectSecondaryColorArgb", snapshot.DynamicEffectSecondaryColorArgb, RegistryValueKind.DWord);
                        key.SetValue("DynamicEffectSpeed", snapshot.DynamicEffectSpeed, RegistryValueKind.DWord);
                        key.SetValue("DynamicEffectDirection", snapshot.DynamicEffectDirection, RegistryValueKind.DWord);

                        key.SetValue("SmoothPowerModeSwitching", snapshot.SmoothPowerModeSwitching ? 1 : 0);
                        key.SetValue("WindowsOverlaySyncEnabled", snapshot.WindowsOverlaySyncEnabled ? 1 : 0);
                        key.SetValue("PowerModeSettleMs", snapshot.PowerModeSettleMs);

                        key.SetValue("ThermalCpuWarnCelsius", (int)Math.Round(snapshot.ThermalCpuWarnCelsius));
                        key.SetValue("ThermalCpuCeilingCelsius", (int)Math.Round(snapshot.ThermalCpuCeilingCelsius));
                        key.SetValue("ThermalCpuCriticalCelsius", (int)Math.Round(snapshot.ThermalCpuCriticalCelsius));
                        key.SetValue("ThermalGpuWarnCelsius", (int)Math.Round(snapshot.ThermalGpuWarnCelsius));
                        key.SetValue("ThermalGpuCeilingCelsius", (int)Math.Round(snapshot.ThermalGpuCeilingCelsius));
                        key.SetValue("ThermalGpuCriticalCelsius", (int)Math.Round(snapshot.ThermalGpuCriticalCelsius));
                        key.SetValue("ThermalHysteresisCelsius", (int)Math.Round(snapshot.ThermalHysteresisCelsius));
                        key.SetValue("ThermalMaxFanFloorPercent", snapshot.ThermalMaxFanFloorPercent);
                        key.SetValue("ThermalCpuPowerCapWatts", snapshot.ThermalCpuPowerCapWatts);
                        key.SetValue("ThermalMinPowerCapWatts", snapshot.ThermalMinPowerCapWatts);
                        key.SetValue("ThermalCriticalAlertEnabled", snapshot.ThermalCriticalAlertEnabled ? 1 : 0);

                        key.SetValue("EnableCustomPowerLimits", snapshot.EnableCustomPowerLimits ? 1 : 0);
                        key.SetValue("HardwareHasActiveCustomPowerOverride", snapshot.HardwareHasActiveCustomPowerOverride ? 1 : 0);
                        key.SetValue("BiosHardwareFingerprint", snapshot.BiosHardwareFingerprint ?? "");
                        key.SetValue("ScannedHardwareSignature", snapshot.BiosHardwareFingerprint ?? "");
                        key.SetValue("HasCompletedBiosPowerScan", snapshot.HasCompletedBiosPowerScan ? 1 : 0);
                        key.SetValue("HasScannedHardwarePowerBounds", snapshot.HasCompletedBiosPowerScan ? 1 : 0);
                        key.SetValue("CpuThermalSpecTdpWatts", snapshot.CpuThermalSpecTdpWatts);
                        key.SetValue("ScannedCpuTdpWatts", snapshot.CpuThermalSpecTdpWatts);
                        key.SetValue("CpuMinAllowedPowerWatts", snapshot.CpuMinAllowedPowerWatts);
                        key.SetValue("ScannedMinPowerWatts", snapshot.CpuMinAllowedPowerWatts);
                        key.SetValue("CpuMaxAllowedPl1Watts", snapshot.CpuMaxAllowedPl1Watts);
                        key.SetValue("ScannedMaxPl1Watts", snapshot.CpuMaxAllowedPl1Watts);
                        key.SetValue("CpuMaxAllowedPl2Watts", snapshot.CpuMaxAllowedPl2Watts);
                        key.SetValue("ScannedMaxPl2Watts", snapshot.CpuMaxAllowedPl2Watts);
                        key.SetValue("VerifiedBiosModesBitmask", snapshot.VerifiedBiosModesBitmask);

                        key.SetValue("DefaultEcoPl1Watts", snapshot.DefaultEcoPl1Watts);
                        key.SetValue("DefaultEcoPl2Watts", snapshot.DefaultEcoPl2Watts);
                        key.SetValue("DefaultQuietPl1Watts", snapshot.DefaultQuietPl1Watts);
                        key.SetValue("DefaultQuietPl2Watts", snapshot.DefaultQuietPl2Watts);
                        key.SetValue("DefaultBalancedPl1Watts", snapshot.DefaultBalancedPl1Watts);
                        key.SetValue("DefaultBalancedPl2Watts", snapshot.DefaultBalancedPl2Watts);
                        key.SetValue("DefaultPerformancePl1Watts", snapshot.DefaultPerformancePl1Watts);
                        key.SetValue("DefaultPerformancePl2Watts", snapshot.DefaultPerformancePl2Watts);
                        key.SetValue("DefaultTurboPl1Watts", snapshot.DefaultTurboPl1Watts);
                        key.SetValue("DefaultTurboPl2Watts", snapshot.DefaultTurboPl2Watts);

                        key.SetValue("BiosDefaultEcoPl1Watts", snapshot.BiosDefaultEcoPl1Watts);
                        key.SetValue("BiosDefaultEcoPl2Watts", snapshot.BiosDefaultEcoPl2Watts);
                        key.SetValue("BiosDefaultQuietPl1Watts", snapshot.BiosDefaultQuietPl1Watts);
                        key.SetValue("BiosDefaultQuietPl2Watts", snapshot.BiosDefaultQuietPl2Watts);
                        key.SetValue("BiosDefaultBalancedPl1Watts", snapshot.BiosDefaultBalancedPl1Watts);
                        key.SetValue("BiosDefaultBalancedPl2Watts", snapshot.BiosDefaultBalancedPl2Watts);
                        key.SetValue("BiosDefaultPerformancePl1Watts", snapshot.BiosDefaultPerformancePl1Watts);
                        key.SetValue("BiosDefaultPerformancePl2Watts", snapshot.BiosDefaultPerformancePl2Watts);
                        key.SetValue("BiosDefaultTurboPl1Watts", snapshot.BiosDefaultTurboPl1Watts);
                        key.SetValue("BiosDefaultTurboPl2Watts", snapshot.BiosDefaultTurboPl2Watts);

                        key.SetValue("EcoPl1Watts", snapshot.EcoPl1Watts);
                        key.SetValue("EcoPl2Watts", snapshot.EcoPl2Watts);
                        key.SetValue("QuietPl1Watts", snapshot.QuietPl1Watts);
                        key.SetValue("QuietPl2Watts", snapshot.QuietPl2Watts);
                        key.SetValue("BalancedPl1Watts", snapshot.BalancedPl1Watts);
                        key.SetValue("BalancedPl2Watts", snapshot.BalancedPl2Watts);
                        key.SetValue("PerformancePl1Watts", snapshot.PerformancePl1Watts);
                        key.SetValue("PerformancePl2Watts", snapshot.PerformancePl2Watts);
                        key.SetValue("TurboPl1Watts", snapshot.TurboPl1Watts);
                        key.SetValue("TurboPl2Watts", snapshot.TurboPl2Watts);

                        key.SetValue("CustomEcoPl1Watts", snapshot.CustomEcoPl1Watts);
                        key.SetValue("CustomEcoPl2Watts", snapshot.CustomEcoPl2Watts);
                        key.SetValue("CustomQuietPl1Watts", snapshot.CustomQuietPl1Watts);
                        key.SetValue("CustomQuietPl2Watts", snapshot.CustomQuietPl2Watts);
                        key.SetValue("CustomBalancedPl1Watts", snapshot.CustomBalancedPl1Watts);
                        key.SetValue("CustomBalancedPl2Watts", snapshot.CustomBalancedPl2Watts);
                        key.SetValue("CustomPerformancePl1Watts", snapshot.CustomPerformancePl1Watts);
                        key.SetValue("CustomPerformancePl2Watts", snapshot.CustomPerformancePl2Watts);
                        key.SetValue("CustomTurboPl1Watts", snapshot.CustomTurboPl1Watts);
                        key.SetValue("CustomTurboPl2Watts", snapshot.CustomTurboPl2Watts);

                        // Persist all BIOS settings to Registry HKCU\SOFTWARE\PredatorControl\BiosSettings
                        try
                        {
                            using var biosKey = RegistrySafety.CreateSafeSubKey(Registry.CurrentUser, @"SOFTWARE\PredatorControl\BiosSettings");
                            if (biosKey != null && snapshot.BiosSettingsValues != null)
                            {
                                foreach (var kvp in snapshot.BiosSettingsValues)
                                {
                                    if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                                    {
                                        biosKey.SetValue(kvp.Key, kvp.Value, RegistryValueKind.String);
                                    }
                                }
                                biosKey.Flush();
                            }
                        }
                        catch { }

                        key.Flush();
                    }
                }
                catch { }

                try
                {
                    using var hklmKey = RegistrySafety.CreateSafeSubKey(Registry.LocalMachine, RegistrySafety.BaseKeyPath);
                    if (hklmKey != null)
                    {
                        hklmKey.SetValue("Power", snapshot.PowerMode);
                        hklmKey.SetValue("Fan", snapshot.FanMode);
                        hklmKey.SetValue("LcdOverdrive", snapshot.LcdOverdrive ? 1 : 0);
                        hklmKey.SetValue("StaticZone1ColorArgb", snapshot.StaticZone1ColorArgb, RegistryValueKind.DWord);
                        hklmKey.SetValue("StaticZone2ColorArgb", snapshot.StaticZone2ColorArgb, RegistryValueKind.DWord);
                        hklmKey.SetValue("StaticZone3ColorArgb", snapshot.StaticZone3ColorArgb, RegistryValueKind.DWord);
                        hklmKey.SetValue("StaticZone4ColorArgb", snapshot.StaticZone4ColorArgb, RegistryValueKind.DWord);
                        hklmKey.SetValue("DynamicLightingEffectName", snapshot.DynamicLightingEffectName ?? "Off");
                        hklmKey.SetValue("IsZoneLightingActive", snapshot.IsZoneLightingActive ? 1 : 0, RegistryValueKind.DWord);
                        hklmKey.SetValue("Brightness", snapshot.RgbBrightness, RegistryValueKind.DWord);
                        hklmKey.SetValue("KeyboardBrightnessLevel", snapshot.KeyboardBrightnessLevel, RegistryValueKind.DWord);
                        hklmKey.Flush();
                    }
                }
                catch { }
            }
        }
    }
}
