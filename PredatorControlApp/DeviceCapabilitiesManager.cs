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
        public bool FanTable { get; init; }
        public bool GpuModeSwitch { get; init; }
        public bool UsbCharging { get; init; }
        public bool BatteryHealth { get; init; }
        public bool BatteryCalibration { get; init; }
        public bool? HasEcHid { get; init; } = false;
        public bool HasThirdFan { get; init; }
        public bool ModeKey { get; init; }

        public bool PawnIoAvailable { get; init; } = true;
        public bool PawnIoInstalled { get; init; } = true;
        public bool PawnIoRequiresAdmin { get; init; } = false;
        public string PawnIoDiagnosticMessage { get; init; } = string.Empty;
        public bool IsMsr0x610Locked { get; init; } = false;

        public int CpuTdpWatts { get; init; } = 45;
        public int ScannedMinPowerWatts { get; init; } = 15;
        public int ScannedMaxPl1Watts { get; init; } = 115;
        public int ScannedMaxPl2Watts { get; init; } = 140;
        public string CpuModel { get; init; } = "Intel Core Processor";
        public string Architecture { get; init; } = "Intel 11th-14th Gen H";
        public CpuArchitectureFamily ArchitectureFamily { get; init; } = CpuArchitectureFamily.IntelCoreH;
        public IReadOnlyDictionary<byte, (int Pl1, int Pl2)> ScannedModeDefaults { get; init; } = new Dictionary<byte, (int, int)>
        {
            { 0x06, (25, 35) },
            { 0x00, (35, 45) },
            { 0x01, (45, 65) },
            { 0x04, (70, 90) },
            { 0x05, (90, 115) }
        };
        public AppSettings? Settings { get; init; }
        public BiosPowerScanResult? BiosPowerScan { get; init; }
        public string BiosHardwareFingerprint { get; init; } = string.Empty;
        public int CpuThermalSpecTdpWatts => CpuTdpWatts;
        public int CpuMinAllowedPowerWatts => ScannedMinPowerWatts;
        public int CpuMaxAllowedPl1Watts => ScannedMaxPl1Watts;
        public int CpuMaxAllowedPl2Watts => ScannedMaxPl2Watts;

        public int GetScannedPl1(byte mode)
        {
            var opMode = ByteToOperatingMode(mode);

            if (BiosPowerScan != null && opMode.HasValue)
            {
                var (bPl1, _) = BiosPowerScan.GetDefaultLimitsForMode(opMode.Value);
                if (bPl1 > 0) return bPl1;
            }

            try
            {
                if (Settings != null && opMode.HasValue)
                {
                    var (rawPl1, _) = Settings.GetRawScannedDefaultsForMode(opMode.Value);
                    if (rawPl1 > 0) return rawPl1;
                }
            }
            catch { }

            if (ScannedModeDefaults != null && ScannedModeDefaults.TryGetValue(mode, out var def) && def.Pl1 > 0)
                return def.Pl1;

            try
            {
                if (Settings != null && opMode.HasValue)
                {
                    var (propPl1, _) = Settings.EstimateProportionalDefaultsForMode(opMode.Value);
                    if (propPl1 > 0) return propPl1;
                }
            }
            catch { }

            return CpuTdpWatts;
        }

        public int GetScannedPl2(byte mode)
        {
            var opMode = ByteToOperatingMode(mode);

            if (BiosPowerScan != null && opMode.HasValue)
            {
                var (_, bPl2) = BiosPowerScan.GetDefaultLimitsForMode(opMode.Value);
                if (bPl2 > 0) return bPl2;
            }

            try
            {
                if (Settings != null && opMode.HasValue)
                {
                    var (_, rawPl2) = Settings.GetRawScannedDefaultsForMode(opMode.Value);
                    if (rawPl2 > 0) return rawPl2;
                }
            }
            catch { }

            if (ScannedModeDefaults != null && ScannedModeDefaults.TryGetValue(mode, out var def) && def.Pl2 > 0)
                return def.Pl2;

            try
            {
                if (Settings != null && opMode.HasValue)
                {
                    var (_, propPl2) = Settings.EstimateProportionalDefaultsForMode(opMode.Value);
                    if (propPl2 > 0) return propPl2;
                }
            }
            catch { }

            return (int)Math.Round(CpuTdpWatts * 1.35);
        }

        private static OperatingMode? ByteToOperatingMode(byte mode) => mode switch
        {
            0x00 => OperatingMode.Quiet,
            0x01 => OperatingMode.Balanced,
            0x04 => OperatingMode.Performance,
            0x05 => OperatingMode.Turbo,
            0x06 => OperatingMode.Eco,
            _ => null
        };

        public string Diagnostics { get; init; } = "";

        public bool HasOperatingModes => OperatingModes.Count > 0;
    }

    [SupportedOSPlatform("windows")]
    public sealed record CapabilityOverrides
    {
        public bool? CoolBoost { get; init; }
        public bool? OperatingModes { get; init; }
        public bool? GpuModeSwitch { get; init; }
        public bool? ThirdFan { get; init; }
        public bool? FanTable { get; init; }
        public bool? UsbCharging { get; init; }
        public bool? BatteryCalibration { get; init; }
        public bool? HasEcHid { get; init; }
        public bool? ModeKey { get; init; }

        public static CapabilityOverrides Clear() => new();

        /// <summary>Overrides that force every capability on (diagnostics/testing).</summary>
        public static CapabilityOverrides EnableAll() => new()
        {
            CoolBoost = true,
            OperatingModes = true,
            GpuModeSwitch = true,
            ThirdFan = true,
            FanTable = true,
            UsbCharging = true,
            BatteryCalibration = true,
            HasEcHid = true,
            ModeKey = true
        };

        public static CapabilityOverrides LoadFromRegistry()
        {
            try
            {
                lock (RegistrySafety.RegistryLock)
                {
                    using var hkcu = RegistrySafety.OpenSafeSubKey(Registry.CurrentUser, @"SOFTWARE\PredatorControl\Overrides");
                    using var hklm = RegistrySafety.OpenSafeSubKey(Registry.LocalMachine, @"SOFTWARE\PredatorControl\Overrides");
                    var key = hkcu ?? hklm;
                    if (key != null)
                    {
                        return new CapabilityOverrides
                        {
                            CoolBoost = ReadBool(key, "CoolBoost"),
                            OperatingModes = ReadBool(key, "OperatingModes"),
                            GpuModeSwitch = ReadBool(key, "GpuModeSwitch"),
                            ThirdFan = ReadBool(key, "ThirdFan"),
                            FanTable = ReadBool(key, "FanTable"),
                            UsbCharging = ReadBool(key, "UsbCharging"),
                            BatteryCalibration = ReadBool(key, "BatteryCalibration"),
                            HasEcHid = ReadBool(key, "HasEcHid"),
                            ModeKey = ReadBool(key, "ModeKey")
                        };
                    }
                }
            }
            catch { }
            return new CapabilityOverrides();
        }

        public void SaveToRegistry()
        {
            try
            {
                lock (RegistrySafety.RegistryLock)
                {
                    using var key = RegistrySafety.CreateSafeSubKey(Registry.CurrentUser, @"SOFTWARE\PredatorControl\Overrides");
                    if (key != null)
                    {
                        WriteBool(key, "CoolBoost", CoolBoost);
                        WriteBool(key, "OperatingModes", OperatingModes);
                        WriteBool(key, "GpuModeSwitch", GpuModeSwitch);
                        WriteBool(key, "ThirdFan", ThirdFan);
                        WriteBool(key, "FanTable", FanTable);
                        WriteBool(key, "UsbCharging", UsbCharging);
                        WriteBool(key, "BatteryCalibration", BatteryCalibration);
                        WriteBool(key, "HasEcHid", HasEcHid);
                        WriteBool(key, "ModeKey", ModeKey);
                        key.Flush();
                    }
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

        private static int TriState(bool? value) => value.HasValue ? (value.Value ? 1 : 0) : -1;

        private static uint CapabilityBits(DeviceCapabilities caps) =>
            (caps.CoolBoost ? 1u : 0u)
            | (caps.GpuModeSwitch ? 2u : 0u)
            | (caps.Fans.Any(f => f.Id == FanId.System || f.Id == FanId.Gpu2) ? 4u : 0u)
            | (caps.FanTable ? 8u : 0u)
            | (caps.UsbCharging ? 16u : 0u)
            | (caps.BatteryCalibration ? 32u : 0u)
            | (caps.HasEcHid == true ? 64u : 0u)
            | (caps.ModeKey ? 128u : 0u);

        public DeviceCapabilities Apply(DeviceCapabilities caps)
        {
            var opModes = OperatingModes.HasValue
                ? (OperatingModes.Value
                    ? (caps.FirmwareOperatingModes.Count > 0 ? caps.FirmwareOperatingModes : caps.OperatingModes)
                    : Array.Empty<OperatingMode>())
                : caps.OperatingModes;

            uint? mergedBits = PredatorNativeMigrations.ApplyCapabilityOverrides(
                CapabilityBits(caps),
                new[]
                {
                    TriState(CoolBoost), TriState(GpuModeSwitch), TriState(ThirdFan),
                    TriState(FanTable), TriState(UsbCharging), TriState(BatteryCalibration),
                    TriState(HasEcHid), TriState(ModeKey)
                });

            if (mergedBits.HasValue)
            {
                var nativeFans = caps.Fans.ToList();
                bool nativeThirdFan = (mergedBits.Value & 4u) != 0;
                if (nativeThirdFan)
                {
                    if (!nativeFans.Any(f => f.Id == FanId.System || f.Id == FanId.Gpu2))
                        nativeFans.Add(FanChannel.System);
                }
                else
                {
                    nativeFans.RemoveAll(f => f.Id == FanId.System || f.Id == FanId.Gpu2);
                }

                return caps with
                {
                    OperatingModes = opModes,
                    CoolBoost = (mergedBits.Value & 1u) != 0,
                    GpuModeSwitch = (mergedBits.Value & 2u) != 0,
                    FanTable = (mergedBits.Value & 8u) != 0,
                    UsbCharging = (mergedBits.Value & 16u) != 0,
                    BatteryCalibration = (mergedBits.Value & 32u) != 0,
                    HasEcHid = HasEcHid ?? caps.HasEcHid,
                    ModeKey = (mergedBits.Value & 128u) != 0,
                    Fans = nativeFans,
                    HasThirdFan = nativeThirdFan
                };
            }

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
        public static DeviceCapabilities Probe(WmiController wmi, EcHidDevice? ecHid = null, PawnIoController? pawnIo = null, AppSettings? settings = null)
        {
            if (wmi == null) return DeviceCapabilities.None with { Diagnostics = "WmiController is null" };

            var sb = new StringBuilder();
            sb.AppendLine("=== Predator Control Capability & Hardware Probe ===");

            string model = HardwareIdentity.ReadModel();
            string bios = HardwareIdentity.ReadBiosVersion();
            string sn = HardwareIdentity.ReadSerialNumber();
            string? snid = HardwareIdentity.ComputeSnid(sn);
            var catalogProfile = AcerLaptopModelCatalog.Lookup(model);

            sb.AppendLine($"Hardware Model: {model}");
            sb.AppendLine($"BIOS Version:   {bios}");
            sb.AppendLine($"Serial Number:  {sn}");
            sb.AppendLine($"SNID:           {snid ?? "N/A"}");
            sb.AppendLine($"Catalog Profile:{catalogProfile.MarketingName} (Launch {catalogProfile.LaunchYear}, {catalogProfile.DefaultLightingTier}, Fans={catalogProfile.FanTopology})");

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

            bool hasThirdFan = sysRpm > 0 || wmi.GetGamingFanSpeed(2) > 0 || smbios.Gaming(0x05) == 1;
            if (hasThirdFan)
            {
                sensors.Add(SensorId.SystemFanSpeed);
            }

            sb.AppendLine($"Sensors Detected: {string.Join(", ", sensors)}");

            // Fans
            var fans = new List<FanChannel> { FanChannel.Cpu, FanChannel.Gpu };
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
            bool coolBoost = wmi.GetCoolBoost() != null;
            sb.AppendLine($"CoolBoost Support: {coolBoost}");

            // Fan Table
            bool fanTable = AcerProtocol.UsesFanTable(model) || wmi.GetFanTable() != null;
            sb.AppendLine($"Factory EC Fan Tables: {fanTable}");

            // GPU Mode Switch / MUX
            bool gpuModeSwitch = wmi.IsGpuModeSwitchSupported() || wmi.GetGpuMode() != null;
            sb.AppendLine($"MUX / GPU Mode Switch: {gpuModeSwitch}");

            // USB Charging
            bool usbCharging = wmi.GetUsbCharging() != null;
            sb.AppendLine($"Power-Off USB Charging: {usbCharging}");

            // Battery Control & Calibration
            bool batteryHealth = wmi.IsBatteryControlSupported() || WindowsBattery.Read() != null;
            bool batteryCalibration = wmi.IsBatteryCalibrationSupported();
            sb.AppendLine($"Battery Control: {batteryHealth}, Hardware Calibration: {batteryCalibration}");

            // Mode key
            bool modeKey = smbios.HasHotkey(7) || smbios.Gaming(7) == 1;
            sb.AppendLine($"Physical Mode Key: {modeKey}");

            // CPU RAPL & Power Limits Probe dynamically populated from CpuSpecDetector, live PawnIO scan, and AppSettings
            string cpuModel = HardwareIdentity.ReadCpuModel();
            var cpuSpec = CpuSpecDetector.Detect(cpuModel);

            int cpuTdpWatts = cpuSpec.BaseTdpWatts;
            int minPowerWatts = cpuSpec.MinPowerWatts;
            int maxPl1Watts = cpuSpec.MaxPl1Watts;
            int maxPl2Watts = cpuSpec.MaxPl2Watts;

            bool pawnIoAvailable = false;
            bool pawnIoInstalled = false;
            bool pawnIoRequiresAdmin = false;
            string pawnIoDiag = string.Empty;
            bool isMsrLocked = false;

            var effectiveSettings = settings ?? CpuPowerLimitController.Instance?.Settings;
            bool hasVerifiedSettingsBounds = effectiveSettings != null &&
                (effectiveSettings.HasScannedHardwarePowerBounds ||
                 effectiveSettings.HasCompletedBiosPowerScan ||
                 effectiveSettings.CpuMaxAllowedPl1Watts > 0 ||
                 effectiveSettings.ScannedMaxPl1Watts > 0);

            if (hasVerifiedSettingsBounds)
            {
                if (effectiveSettings!.ScannedCpuTdpWatts > 0) cpuTdpWatts = effectiveSettings.ScannedCpuTdpWatts;
                if (effectiveSettings.ScannedMinPowerWatts > 0) minPowerWatts = effectiveSettings.ScannedMinPowerWatts;
                if (effectiveSettings.ScannedMaxPl1Watts > 0) maxPl1Watts = effectiveSettings.ScannedMaxPl1Watts;
                if (effectiveSettings.ScannedMaxPl2Watts > 0) maxPl2Watts = effectiveSettings.ScannedMaxPl2Watts;
            }

            try
            {
                var controller = pawnIo ?? CpuPowerLimitController.Instance?.PawnIo ?? PawnIoController.Instance;
                var scanResult = controller.ScanDeviceForPawnIo();
                pawnIoInstalled = scanResult.IsInstalled;
                pawnIoRequiresAdmin = scanResult.RequiresElevation;
                pawnIoDiag = scanResult.DiagnosticMessage;

                if (scanResult.IsAccessible || controller.IsAvailable)
                {
                    var powerCaps = controller.ScanDevicePowerCapabilities();
                    if (powerCaps != null && powerCaps.IsSupported)
                    {
                        pawnIoAvailable = true;
                        pawnIoInstalled = true;
                        isMsrLocked = powerCaps.IsLocked;
                        if (!hasVerifiedSettingsBounds)
                        {
                            if (powerCaps.ThermalSpecPowerWatts > 0) cpuTdpWatts = powerCaps.ThermalSpecPowerWatts;
                            if (powerCaps.ScannedMinPowerWatts > 0) minPowerWatts = powerCaps.ScannedMinPowerWatts;
                            if (powerCaps.ScannedMaxPl1Watts > 0) maxPl1Watts = powerCaps.ScannedMaxPl1Watts;
                            if (powerCaps.ScannedMaxPl2Watts > 0) maxPl2Watts = powerCaps.ScannedMaxPl2Watts;
                        }
                        pawnIoDiag = $"PawnIO Active (MSR & MMIO power limits unlocked): Units={powerCaps.UnitsPerWatt}U/W, TDP={cpuTdpWatts}W, Min={minPowerWatts}W, Max PL1={maxPl1Watts}W, Max PL2={maxPl2Watts}W";
                    }
                    else if (powerCaps != null)
                    {
                        pawnIoAvailable = controller.IsAvailable;
                        isMsrLocked = powerCaps.IsLocked;
                        if (powerCaps.BaselineStatus == PowerLimitApplyStatus.AccessDenied)
                        {
                            pawnIoRequiresAdmin = true;
                            pawnIoInstalled = true;
                        }
                        pawnIoDiag = !string.IsNullOrWhiteSpace(powerCaps.Diagnostics) ? powerCaps.Diagnostics : scanResult.DiagnosticMessage;
                    }
                }
                else
                {
                    pawnIoAvailable = false;
                    if (pawnIoRequiresAdmin)
                    {
                        pawnIoDiag = "PawnIO detected! Run app as Administrator to unlock power limits.";
                    }
                    else if (pawnIoInstalled)
                    {
                        pawnIoDiag = "PawnIO service installed but stopped.";
                    }
                    else
                    {
                        pawnIoDiag = "PawnIO driver not detected (Install PawnIO for CPU power limits).";
                    }
                }
            }
            catch (Exception ex)
            {
                pawnIoDiag = $"PawnIO probe exception: {ex.Message}";
            }

            var modeDefaults = new Dictionary<byte, (int Pl1, int Pl2)>();
            byte[] modesToPopulate = { 0x06, 0x00, 0x01, 0x04, 0x05 };
            foreach (byte m in modesToPopulate)
            {
                int p1 = 0, p2 = 0;
                var opMode = m switch
                {
                    0x00 => OperatingMode.Quiet,
                    0x01 => OperatingMode.Balanced,
                    0x04 => OperatingMode.Performance,
                    0x05 => OperatingMode.Turbo,
                    0x06 => OperatingMode.Eco,
                    _ => OperatingMode.Balanced
                };

                // Check authoritative BIOS scan result first
                var biosScan = CpuPowerLimitController.Instance?.Capabilities?.BiosPowerScan;
                if (biosScan != null)
                {
                    var (bPl1, bPl2) = biosScan.GetDefaultLimitsForMode(opMode);
                    if (bPl1 > 0) p1 = bPl1;
                    if (bPl2 > 0) p2 = bPl2;
                }

                // Check verified settings defaults
                if (effectiveSettings != null)
                {
                    var (sPl1, sPl2) = effectiveSettings.GetRawScannedDefaultsForMode(opMode);
                    if (sPl1 > 0 && p1 <= 0) p1 = sPl1;
                    if (sPl2 > 0 && p2 <= 0) p2 = sPl2;

                    // If still missing, check proportional extrapolation from verified modes
                    if (p1 <= 0 || p2 <= 0)
                    {
                        var (prop1, prop2) = effectiveSettings.EstimateProportionalDefaultsForMode(opMode);
                        if (prop1 > 0 && p1 <= 0) p1 = prop1;
                        if (prop2 > 0 && p2 <= 0) p2 = prop2;
                    }
                }

                // Fall back to CpuSpecDetector only if completely unscanned
                if (p1 <= 0 || p2 <= 0)
                {
                    var (est1, est2) = CpuSpecDetector.EstimateProportionalLimits(cpuTdpWatts, m);
                    if (p1 <= 0) p1 = est1;
                    if (p2 <= 0) p2 = est2;
                }

                if (PredatorNativeMigrations.FinalizeModeDefault(p1, p2, cpuTdpWatts, out int finalizedPl1, out int finalizedPl2))
                {
                    p1 = finalizedPl1;
                    p2 = finalizedPl2;
                }
                else if (p2 < p1) p2 = p1;
                modeDefaults[m] = (p1, p2);
            }

            sb.AppendLine("--- CPU RAPL Power Limit Capabilities ---");
            sb.AppendLine($"CPU Model:           {cpuModel}");
            sb.AppendLine($"CPU Rated TDP:       {cpuTdpWatts} W");
            sb.AppendLine($"PawnIO Driver:       {(pawnIoAvailable ? "Available / Loaded" : (pawnIoRequiresAdmin ? "Detected (Requires Administrator)" : (pawnIoInstalled ? "Detected (Service Stopped)" : "Not Detected")))}");
            sb.AppendLine($"PawnIO Diagnostics:  {pawnIoDiag}");
            sb.AppendLine($"MSR 0x610 Write-Lock: {(isMsrLocked ? "Locked (Bit 63 set)" : "Unlocked (Writable)")}");
            sb.AppendLine($"Scanned Hardware Bounds: Min {minPowerWatts}W, Max PL1 {maxPl1Watts}W, Max PL2 {maxPl2Watts}W");
            sb.AppendLine("Scanned Factory Mode Defaults:");
            sb.AppendLine($"  Eco (0x06):         PL1={modeDefaults[0x06].Pl1}W, PL2={modeDefaults[0x06].Pl2}W");
            sb.AppendLine($"  Quiet (0x00):       PL1={modeDefaults[0x00].Pl1}W, PL2={modeDefaults[0x00].Pl2}W");
            sb.AppendLine($"  Balanced (0x01):    PL1={modeDefaults[0x01].Pl1}W, PL2={modeDefaults[0x01].Pl2}W");
            sb.AppendLine($"  Performance (0x04): PL1={modeDefaults[0x04].Pl1}W, PL2={modeDefaults[0x04].Pl2}W");
            sb.AppendLine($"  Turbo (0x05):       PL1={modeDefaults[0x05].Pl1}W, PL2={modeDefaults[0x05].Pl2}W");

            var rawCaps = new DeviceCapabilities
            {
                Sensors = sensors,
                Fans = fans,
                OperatingModes = opModes,
                FirmwareOperatingModes = opModes,
                CoolBoost = coolBoost,
                FirmwareCoolBoost = coolBoost,
                FanTable = fanTable,
                GpuModeSwitch = gpuModeSwitch,
                UsbCharging = usbCharging,
                BatteryHealth = batteryHealth,
                BatteryCalibration = batteryCalibration,
                HasEcHid = hasEcHid,
                HasThirdFan = hasThirdFan,
                ModeKey = modeKey,
                PawnIoAvailable = pawnIoAvailable,
                PawnIoInstalled = pawnIoInstalled,
                PawnIoRequiresAdmin = pawnIoRequiresAdmin,
                PawnIoDiagnosticMessage = pawnIoDiag,
                IsMsr0x610Locked = isMsrLocked,
                CpuTdpWatts = cpuTdpWatts,
                ScannedMinPowerWatts = minPowerWatts,
                ScannedMaxPl1Watts = maxPl1Watts,
                ScannedMaxPl2Watts = maxPl2Watts,
                CpuModel = cpuModel,
                Architecture = cpuSpec.Architecture,
                ArchitectureFamily = cpuSpec.Family,
                ScannedModeDefaults = modeDefaults,
                Settings = effectiveSettings,
                BiosPowerScan = CpuPowerLimitController.Instance?.Capabilities?.BiosPowerScan,
                BiosHardwareFingerprint = effectiveSettings?.BiosHardwareFingerprint ?? string.Empty,
                Diagnostics = sb.ToString()
            };

            return rawCaps;
        }
    }
}
