using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public sealed record DeviceCapabilities
    {
        public static DeviceCapabilities None { get; } = new();

        public IReadOnlyList<SensorId> Sensors { get; init; } = Array.Empty<SensorId>();
        public IReadOnlyList<FanChannel> Fans { get; init; } = new[] { FanChannel.Cpu, FanChannel.Gpu };
        public IReadOnlyList<OperatingMode> OperatingModes { get; init; } = new[]
        {
            OperatingMode.Quiet,
            OperatingMode.Balanced,
            OperatingMode.Performance,
            OperatingMode.Turbo
        };
        public IReadOnlyList<OperatingMode> FirmwareOperatingModes { get; init; } = Array.Empty<OperatingMode>();

        public bool CoolBoost { get; init; }
        public bool FirmwareCoolBoost { get; init; }
        public bool DustDefender { get; init; }
        public bool FanTable { get; init; }
        public bool GpuModeSwitch { get; init; }
        public bool UsbCharging { get; init; }
        public bool BatteryHealth { get; init; }
        public bool BatteryCalibration { get; init; }
        public bool HasEcHid { get; init; }
        public bool HasThirdFan { get; init; }
        public bool ModeKey { get; init; }

        public string Diagnostics { get; init; } = "";

        public bool HasSensor(SensorId id) => Sensors.Contains(id);
        public bool HasOperatingModes => OperatingModes.Count > 0;
    }

    [SupportedOSPlatform("windows")]
    public sealed record CapabilityOverrides
    {
        public bool? CoolBoost { get; init; }
        public bool? OperatingModes { get; init; }
        public bool? GpuModeSwitch { get; init; }
        public bool? ThirdFan { get; init; }
        public bool? DustDefender { get; init; }
        public bool? FanTable { get; init; }
        public bool? UsbCharging { get; init; }
        public bool? BatteryCalibration { get; init; }
        public bool? HasEcHid { get; init; }
        public bool? ModeKey { get; init; }

        public static CapabilityOverrides EnableAll() => new()
        {
            CoolBoost = true,
            OperatingModes = true,
            GpuModeSwitch = true,
            ThirdFan = true,
            DustDefender = true,
            FanTable = true,
            UsbCharging = true,
            BatteryCalibration = true,
            HasEcHid = true,
            ModeKey = true
        };

        public static CapabilityOverrides Clear() => new();

        public static CapabilityOverrides LoadFromRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\PredatorControl\Overrides");
                if (key != null)
                {
                    return new CapabilityOverrides
                    {
                        CoolBoost = ReadBool(key, "CoolBoost"),
                        OperatingModes = ReadBool(key, "OperatingModes"),
                        GpuModeSwitch = ReadBool(key, "GpuModeSwitch"),
                        ThirdFan = ReadBool(key, "ThirdFan"),
                        DustDefender = ReadBool(key, "DustDefender"),
                        FanTable = ReadBool(key, "FanTable"),
                        UsbCharging = ReadBool(key, "UsbCharging"),
                        BatteryCalibration = ReadBool(key, "BatteryCalibration"),
                        HasEcHid = ReadBool(key, "HasEcHid"),
                        ModeKey = ReadBool(key, "ModeKey")
                    };
                }
            }
            catch { }
            return new CapabilityOverrides();
        }

        public void SaveToRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl\Overrides");
                if (key != null)
                {
                    WriteBool(key, "CoolBoost", CoolBoost);
                    WriteBool(key, "OperatingModes", OperatingModes);
                    WriteBool(key, "GpuModeSwitch", GpuModeSwitch);
                    WriteBool(key, "ThirdFan", ThirdFan);
                    WriteBool(key, "DustDefender", DustDefender);
                    WriteBool(key, "FanTable", FanTable);
                    WriteBool(key, "UsbCharging", UsbCharging);
                    WriteBool(key, "BatteryCalibration", BatteryCalibration);
                    WriteBool(key, "HasEcHid", HasEcHid);
                    WriteBool(key, "ModeKey", ModeKey);
                }
            }
            catch { }
        }

        private static bool? ReadBool(RegistryKey key, string name)
        {
            var val = key.GetValue(name);
            return val is int i ? i == 1 : null;
        }

        private static void WriteBool(RegistryKey key, string name, bool? value)
        {
            if (value.HasValue) key.SetValue(name, value.Value ? 1 : 0);
            else key.DeleteValue(name, false);
        }

        public DeviceCapabilities Apply(DeviceCapabilities caps)
        {
            var opModes = OperatingModes.HasValue
                ? (OperatingModes.Value
                    ? (caps.FirmwareOperatingModes.Count > 0 ? caps.FirmwareOperatingModes : caps.OperatingModes)
                    : Array.Empty<OperatingMode>())
                : caps.OperatingModes;

            var fans = caps.Fans.ToList();
            if (ThirdFan.HasValue)
            {
                if (ThirdFan.Value && !fans.Any(f => f.Id == FanId.System || f.Id == FanId.Gpu2))
                    fans.Add(FanChannel.System);
                else if (!ThirdFan.Value)
                    fans.RemoveAll(f => f.Id == FanId.System || f.Id == FanId.Gpu2);
            }

            return caps with
            {
                OperatingModes = opModes,
                CoolBoost = CoolBoost ?? caps.CoolBoost,
                GpuModeSwitch = GpuModeSwitch ?? caps.GpuModeSwitch,
                DustDefender = DustDefender ?? caps.DustDefender,
                FanTable = FanTable ?? caps.FanTable,
                UsbCharging = UsbCharging ?? caps.UsbCharging,
                BatteryCalibration = BatteryCalibration ?? caps.BatteryCalibration,
                HasEcHid = HasEcHid ?? caps.HasEcHid,
                ModeKey = ModeKey ?? caps.ModeKey,
                Fans = fans,
                HasThirdFan = fans.Any(f => f.Id == FanId.System || f.Id == FanId.Gpu2)
            };
        }
    }

    [SupportedOSPlatform("windows")]
    public static class CapabilityProbe
    {
        public static DeviceCapabilities Probe(WmiController wmi, EcHidDevice? ecHid = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Predator Control Capability & Hardware Probe ===");

            string model = HardwareIdentity.ReadModel();
            string bios = HardwareIdentity.ReadBiosVersion();
            string sn = HardwareIdentity.ReadSerialNumber();
            string? snid = HardwareIdentity.ComputeSnid(sn);

            sb.AppendLine($"Hardware Model: {model}");
            sb.AppendLine($"BIOS Version:   {bios}");
            sb.AppendLine($"Serial Number:  {sn}");
            sb.AppendLine($"SNID:           {snid ?? "N/A"}");

            var smbios = AcerSmbios.Read();
            sb.AppendLine($"SMBIOS 0xAC Gaming Version: {(smbios.GamingVersion.HasValue ? smbios.GamingVersion.Value.ToString("0.00") : "Absent")}");
            sb.AppendLine($"SMBIOS Gaming Records: {string.Join(" ", smbios.GamingRecords.Select(r => $"{r.Id:X2}={r.Value:X}"))}");
            sb.AppendLine($"SMBIOS Hotkey Functions: {string.Join(" ", smbios.HotkeyFunctions.Select(r => $"{r.Id:X2}={r.Value:X}"))}");

            bool hasEcHid = ecHid != null && ecHid.IsOpen;
            sb.AppendLine($"EC HID Protocol: {(hasEcHid ? $"Active (v{ecHid!.Version?.Major}.{ecHid.Version?.Minor})" : "Not Available (Falling back to WMI)")}");

            // Sensors probe
            var sensors = new List<SensorId>();
            int cpuTemp = wmi.GetSensorReading(0x01);
            if (cpuTemp > 0) sensors.Add(SensorId.CpuTemperature);

            int cpuRpm = wmi.GetSensorReading(0x02);
            if (cpuRpm > 0 || wmi.GetGamingFanSpeed(1) > 0) sensors.Add(SensorId.CpuFanSpeed);

            int gpuTemp = wmi.GetSensorReading(0x0A);
            if (gpuTemp > 0) sensors.Add(SensorId.GpuTemperature);

            int gpuRpm = wmi.GetSensorReading(0x06);
            if (gpuRpm > 0 || wmi.GetGamingFanSpeed(4) > 0) sensors.Add(SensorId.GpuFanSpeed);

            int sysRpm = wmi.GetSensorReading((ulong)SensorId.SystemFanSpeed);
            if (sysRpm == 0) sysRpm = wmi.GetSensorReading((ulong)SensorId.System2FanSpeed);
            if (sysRpm == 0) sysRpm = wmi.GetGamingFanSpeed(2);
            if (sysRpm > 0 || wmi.GetSensorReading((ulong)SensorId.SystemTemperature) > 0 || wmi.GetSensorReading((ulong)SensorId.System2Temperature) > 0)
                sensors.Add(SensorId.SystemFanSpeed);

            sb.AppendLine($"Sensors Detected: {string.Join(", ", sensors)}");

            // Fans
            var fans = new List<FanChannel> { FanChannel.Cpu, FanChannel.Gpu };
            bool hasThirdFan = sensors.Contains(SensorId.SystemFanSpeed) || smbios.Gaming(0x05) == 1;
            if (hasThirdFan) fans.Add(FanChannel.System);
            sb.AppendLine($"Fans Detected: {string.Join(", ", fans.Select(f => f.Name))}");

            // Operating modes
            var opModes = new List<OperatingMode>
            {
                OperatingMode.Quiet,
                OperatingMode.Balanced,
                OperatingMode.Performance,
                OperatingMode.Turbo,
                OperatingMode.Eco
            };

            // CoolBoost
            bool coolBoost = wmi.GetCoolBoost() ?? true;
            sb.AppendLine($"CoolBoost Support: {coolBoost}");

            // DustDefender
            bool dustDefender = wmi.GetDustDefenderRunning() != null || AcerProtocol.UsesFanTable(model);
            sb.AppendLine($"DustDefender Support: {dustDefender}");

            // Fan Table
            bool fanTable = AcerProtocol.UsesFanTable(model) || wmi.GetFanTable() != null;
            sb.AppendLine($"Factory EC Fan Tables: {fanTable}");

            // GPU Mode Switch / MUX
            bool gpuModeSwitch = wmi.IsGpuModeSwitchSupported() || wmi.GetGpuMode() != null;
            sb.AppendLine($"MUX / GPU Mode Switch: {gpuModeSwitch}");

            // USB Charging
            bool usbCharging = wmi.GetUsbCharging() != null || true;
            sb.AppendLine($"Power-Off USB Charging: {usbCharging}");

            // Battery Control & Calibration
            bool batteryHealth = wmi.IsBatteryControlSupported() || WindowsBattery.Read() != null;
            bool batteryCalibration = wmi.IsBatteryCalibrationSupported() || batteryHealth;
            sb.AppendLine($"Battery Control: {batteryHealth}, Hardware Calibration: {batteryCalibration}");

            // Mode key
            bool modeKey = smbios.HasHotkey(7) || smbios.Gaming(7) == 1 || true;
            sb.AppendLine($"Physical Mode Key: {modeKey}");

            var rawCaps = new DeviceCapabilities
            {
                Sensors = sensors,
                Fans = fans,
                OperatingModes = opModes,
                FirmwareOperatingModes = opModes,
                CoolBoost = coolBoost,
                FirmwareCoolBoost = coolBoost,
                DustDefender = dustDefender,
                FanTable = fanTable,
                GpuModeSwitch = gpuModeSwitch,
                UsbCharging = usbCharging,
                BatteryHealth = batteryHealth,
                BatteryCalibration = batteryCalibration,
                HasEcHid = hasEcHid,
                HasThirdFan = hasThirdFan,
                ModeKey = modeKey,
                Diagnostics = sb.ToString()
            };

            var overrides = CapabilityOverrides.LoadFromRegistry();
            return overrides.Apply(rawCaps);
        }
    }
}
