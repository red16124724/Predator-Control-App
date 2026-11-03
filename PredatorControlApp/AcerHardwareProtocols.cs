using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PredatorControlApp
{
    #region Enums

    public enum SensorId : byte
    {
        CpuTemperature = 1,
        CpuFanSpeed = 2,
        SystemTemperature = 3,
        SystemFanSpeed = 4,
        FrostCore = 5,
        GpuFanSpeed = 6,
        System2Temperature = 7,
        System2FanSpeed = 8,
        Gpu2FanSpeed = 9,
        GpuTemperature = 10,
        Gpu2Temperature = 11
    }

    public enum FanBehavior : byte
    {
        Auto = 1,
        Max = 2,
        Custom = 3
    }

    public enum FanTable : byte
    {
        Standard = 1,
        Faster = 2,
        Fastest = 3
    }

    public enum MiscSetting : byte
    {
        GpuMode = 2,
        BootAnimation = 6,
        CustomBootLogo = 8,
        GpuModeSupport = 9,
        SupportedOperatingModes = 10,
        OperatingMode = 11,
        LCD_OVERDRIVE = 0x0F,
        LcdOverdrive = 0x0F
    }

    public enum GpuMode : byte
    {
        Auto = 0,
        iGpuOnly = 1,
        Hybrid = 1,
        dGpuOnly = 2,
        Discrete = 2
    }

    public enum BatteryFunction : byte
    {
        HealthMode = 1,
        Calibration = 2
    }

    public enum OperatingMode : byte
    {
        Quiet = 0,
        Balanced = 1,
        Performance = 4,
        Turbo = 5,
        Eco = 6
    }

    public enum FirmwareEventKind : byte
    {
        Hotkey = 1,
        HotkeyReleased = 2,
        Brightness = 4,
        Thermal = 6,
        ModeKey = 7,
        AcAdapter = 8,
        BatteryBoost = 9,
        SimCard = 10,
        BatteryCalibration = 11,
        BatteryCharging = 16
    }

    public enum FanLock
    {
        QuietMode,
        EcoMode,
        TurboMode
    }

    public enum FanId
    {
        Cpu,
        Gpu,
        Gpu2,
        System,
        System2
    }

    public enum FanChip
    {
        Cpu,
        Gpu,
        System
    }

    public enum EcHidCommand : ushort
    {
        Status = 0,
        Mode = 1,
        OverclockProfile = 2,
        Device = 10
    }

    public enum EcHidStatus : byte
    {
        Version = 0,
        BatteryBoost = 2,
        Adapter = 3,
        OverclockProfiles = 4,
        ModeCapability = 5,
        ModeLimit = 6,
        UsbCAdapter = 7
    }

    #endregion

    #region Records & Data Types

    public sealed record UsbChargingState(bool Enabled, int? FloorPercent);

    public sealed record BatteryHealth(int DesignCapacityMWh, int FullChargeCapacityMWh, int? CycleCount, bool CalibrationActive)
    {
        public double WearLevelPercent => DesignCapacityMWh > 0
            ? Math.Clamp((1.0 - ((double)FullChargeCapacityMWh / DesignCapacityMWh)) * 100.0, 0.0, 100.0)
            : 0.0;
        public string? Manufacturer { get; init; }
        public string? Name { get; init; }
    }

    public sealed record BatteryHealthStatus(bool HealthModeSupported, bool CalibrationSupported, bool HealthModeOn, bool CalibrationOn);

    public sealed record AcerSmbiosRecord(byte Id, ushort Value);

    public sealed record FirmwareEvent(FirmwareEventKind Kind, byte Value, byte[] Detail)
    {
        public static FirmwareEvent? Decode(byte[]? detail)
        {
            if (detail == null || detail.Length == 0) return null;
            FirmwareEventKind kind = (FirmwareEventKind)detail[0];
            byte val = (byte)(detail.Length > 1 ? detail[1] : 0);
            return new FirmwareEvent(kind, val, detail.ToArray());
        }

        public override string ToString() => $"{Kind} Value={Value} Data={(Detail != null ? Convert.ToHexString(Detail) : string.Empty)}";
    }

    public readonly record struct EcHidVersion(byte Major, byte Minor)
    {
        public bool EchoesStatusType => Major > 0 || Minor >= 6;
    }

    public sealed record EcHidReply(ushort Status, ushort Command, byte[] Data)
    {
        public bool Done => Status == 0xE000;
        public bool Final => Done || Status == 0xE001;
        public byte Byte(int offset) => (Data != null && (uint)offset < (uint)Data.Length) ? Data[offset] : (byte)0;
        public ushort Word(int offset) => (Data != null && offset >= 0 && offset <= Data.Length - 2) ? BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(offset)) : (ushort)0;
    }

    public sealed record FanChannel(FanId Id, string Name, FanChip Chip, SensorId RpmSensor, int? GroupBit = null, byte? SpeedId = null)
    {
        public bool Controllable => GroupBit.HasValue && SpeedId.HasValue;

        public static readonly FanChannel Cpu = new(FanId.Cpu, "CPU", FanChip.Cpu, SensorId.CpuFanSpeed, 0, 1);
        public static readonly FanChannel Gpu = new(FanId.Gpu, "GPU", FanChip.Gpu, SensorId.GpuFanSpeed, 3, 4);
        public static readonly FanChannel Gpu2 = new(FanId.Gpu2, "GPU 2", FanChip.Gpu, SensorId.Gpu2FanSpeed, 4, 5);
        public static readonly FanChannel System = new(FanId.System, "System", FanChip.System, SensorId.SystemFanSpeed, 1, 2);
        public static readonly FanChannel System2 = new(FanId.System2, "System 2", FanChip.System, SensorId.System2FanSpeed);

        public static readonly IReadOnlyList<FanChannel> Known = new[] { Cpu, Gpu, Gpu2, System, System2 };

        public static FanChannel Get(FanId id) => Known.FirstOrDefault(f => f.Id == id) ?? Cpu;
    }

    #endregion

    #region AcerProtocol

    public static class AcerProtocol
    {
        public const string GamingClass = "AcerGamingFunction";
        public const string ActionClass = "APGeAction";
        public const string BatteryClass = "BatteryControl";
        public const string EventClass = "APGeEvent";

        public const uint SupportedSensorsQuery = 0u;
        public const uint CoolBoostGetInput = 519u;
        public const uint UsbChargingQuery = 4u;
        public const int DefaultUsbChargingFloor = 30;
        public const uint BatteryStatusQuery = 2u;

        public static bool TryWmiModeToEcMode(byte wmiMode, out byte ecMode, int modeCount = 5)
        {
            if (NativeAcerProtocol.TryEncode(16, wmiMode, NativeAcerProtocol.Signed(modeCount), 0, 0, out ulong native))
            {
                ecMode = (byte)native;
                return (native & 0x100) != 0;
            }
            switch (wmiMode)
            {
                case 0x05:
                    ecMode = 0; // Turbo: highest
                    return true;
                case 0x04:
                    ecMode = (byte)(modeCount >= 4 ? 1 : 0); // Performance
                    return true;
                case 0x01:
                    ecMode = (byte)(modeCount >= 4 ? 2 : 1); // Balanced
                    return true;
                case 0x00:
                    ecMode = (byte)(modeCount >= 4 ? 3 : 2); // Quiet
                    return true;
                case 0x06:
                    ecMode = (byte)Math.Max(0, modeCount - 1); // Eco: lowest
                    return true;
                default:
                    ecMode = 1;
                    return false;
            }
        }

        public static byte WmiModeToEcMode(byte wmiMode, int modeCount = 5)
        {
            TryWmiModeToEcMode(wmiMode, out byte ecMode, modeCount);
            return ecMode;
        }

        public static bool TryEcModeToWmiMode(byte ecMode, out byte wmiMode, int modeCount = 5)
        {
            if (NativeAcerProtocol.TryEncode(17, ecMode, NativeAcerProtocol.Signed(modeCount), 0, 0, out ulong native))
            {
                wmiMode = (byte)native;
                return (native & 0x100) != 0;
            }
            switch (ecMode)
            {
                case 0:
                    wmiMode = 0x05; // Turbo
                    return true;
                case 1:
                    wmiMode = (byte)(modeCount >= 4 ? 0x04 : 0x01); // Performance if 4+ modes, else Balanced
                    return true;
                case 2:
                    wmiMode = (byte)(modeCount >= 4 ? 0x01 : 0x00); // Balanced if 4+ modes, else Quiet
                    return true;
                case 3:
                    wmiMode = 0x00; // Quiet
                    return true;
                case 4:
                    wmiMode = 0x06; // Eco
                    return true;
                default:
                    wmiMode = 0x01; // Balanced fallback
                    return false;
            }
        }

        public static byte EcModeToWmiMode(byte ecMode, int modeCount = 5)
        {
            TryEcModeToWmiMode(ecMode, out byte wmiMode, modeCount);
            return wmiMode;
        }

        public static readonly int[] UsbChargingFloors = { 10, 20, 30 };
        private static readonly string[] FanTableModels = { "AN515-46", "AN515-47", "AN515-58", "AN517-42", "AN517-43", "AN517-55" };

        public static byte Status(ulong output) => NativeAcerProtocol.TryEncode(15, output, 0, 0, 0, out ulong native) ? (byte)native : (byte)(output & 0xFF);
        public static bool IsOk(ulong output) => Status(output) == 0;

        public static uint SensorReadInput(SensorId id) => NativeAcerProtocol.TryEncode(0, (byte)id, 0, 0, 0, out ulong native) ? (uint)native : 1u | ((uint)id << 8);
        public static int SensorValue(ulong output) => NativeAcerProtocol.TryEncode(1, output, 0, 0, 0, out ulong native) ? (int)native : (int)((output >> 8) & 0xFFFF);

        public static IReadOnlyList<SensorId> DecodeSensorMask(ulong output)
        {
            ulong mask = NativeAcerProtocol.TryEncode(21, output, 0, 0, 0, out ulong native) ? native : output >> 24;
            var list = new List<SensorId>();
            foreach (SensorId id in Enum.GetValues<SensorId>())
            {
                int bit = (int)id - 1;
                if (bit >= 0 && bit < 64 && (mask & (1UL << bit)) != 0)
                    list.Add(id);
            }
            return list;
        }

        public static ulong FanBehaviorInput(IEnumerable<(FanChannel Fan, FanBehavior Behavior)> settings)
        {
            if (settings == null) return 0UL;
            ulong mask = 0UL;
            ulong val = 0UL;
            foreach (var s in settings)
            {
                if (s.Fan == null || !s.Fan.GroupBit.HasValue) continue;
                int bit = s.Fan.GroupBit.Value;
                if (bit < 0 || bit >= 24) continue;
                if (NativeAcerProtocol.TryEncode(18, (ulong)bit, (ulong)s.Behavior, 0, 0, out ulong native))
                {
                    val |= native;
                    continue;
                }
                mask |= 1UL << bit;
                val |= ((ulong)s.Behavior & 0x03UL) << (16 + 2 * bit);
            }
            return mask | val;
        }

        public static ulong FanSpeedInput(FanChannel fan, int percent)
        {
            if (fan == null) throw new ArgumentNullException(nameof(fan));
            if (!fan.SpeedId.HasValue)
                throw new ArgumentException($"Fan channel '{fan.Name}' has no hardware SpeedId.", nameof(fan));
            byte speedId = fan.SpeedId.Value;
            if (NativeAcerProtocol.TryEncode(2, speedId, NativeAcerProtocol.Signed(percent), 0, 0, out ulong native)) return native;
            return (ulong)(speedId | ((long)Math.Clamp(percent, 0, 100) << 8));
        }

        public static int NearestFanSpeed(int percent)
        {
            if (NativeAcerProtocol.TryEncode(3, NativeAcerProtocol.Signed(percent), 0, 0, 0, out ulong native)) return (int)native;
            return (int)Math.Round(Math.Clamp(percent, 0, 100) / 10.0, MidpointRounding.AwayFromZero) * 10;
        }

        public static ulong FanTableInput(FanTable table) => NativeAcerProtocol.TryEncode(27, (byte)table, 0, 0, 0, out ulong native) ? native : (ulong)table;

        public static FanTable? FanTableValue(ulong output)
        {
            if (NativeAcerProtocol.TryEncode(19, output, 0, 0, 0, out ulong native)) return native == 0 ? null : (FanTable)native;
            byte val = (byte)(output >> 8);
            return Enum.IsDefined(typeof(FanTable), val) ? (FanTable)val : null;
        }

        public static bool UsesFanTable(string? model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            return FanTableModels.Any(m => model.Contains(m, StringComparison.OrdinalIgnoreCase));
        }

        public static ulong CoolBoostSetInput(bool on) => NativeAcerProtocol.TryEncode(4, on ? 1UL : 0, 0, 0, 0, out ulong native) ? native : 7UL | ((ulong)(on ? 1 : 0) << 16);
        public static bool CoolBoostValue(ulong output) => NativeAcerProtocol.TryEncode(5, output, 0, 0, 0, out ulong native) ? native != 0 : ((output >> 8) & 0xFF) == 1;

        public static ulong UsbChargingInput(bool on, int floor)
        {
            if (NativeAcerProtocol.TryEncode(6, on ? 1UL : 0, NativeAcerProtocol.Signed(floor), 0, 0, out ulong native)) return native;
            byte flag = (byte)(on ? 15 : 31);
            return 4UL | ((ulong)flag << 8) | ((ulong)floor << 16);
        }

        public static UsbChargingState UsbChargingValue(ulong output)
        {
            if (UsbChargingFloors[0] == 10 && UsbChargingFloors[1] == 20 && UsbChargingFloors[2] == 30 &&
                NativeAcerProtocol.TryEncode(20, output, 0, 0, 0, out ulong native))
            {
                int nativeFloor = (int)(native >> 8);
                return new UsbChargingState((native & 1) != 0, nativeFloor == 0 ? null : nativeFloor);
            }
            bool enabled = ((output >> 8) & 0xFF) == 15;
            int floor = (int)((output >> 16) & 0xFF);
            return new UsbChargingState(enabled, UsbChargingFloors.Contains(floor) ? floor : null);
        }

        public static uint MiscGetInput(MiscSetting setting) => NativeAcerProtocol.TryEncode(28, (byte)setting, 0, 0, 0, out ulong native) ? (uint)native : (uint)setting;
        public static ulong MiscSetInput(MiscSetting setting, byte value) => NativeAcerProtocol.TryEncode(7, (ulong)setting, value, 0, 0, out ulong native) ? native : (ulong)setting | ((ulong)value << 8);
        public static ulong LcdOverdriveProfileInput(bool enable) => NativeAcerProtocol.TryEncode(24, enable ? 1UL : 0, 0, 0, 0, out ulong native) ? native : enable ? 0x1000000000010UL : 0x10UL;
        public static ulong LcdOverdriveProfileSettingInput(bool enable) => NativeAcerProtocol.TryEncode(25, enable ? 1UL : 0, 0, 0, 0, out ulong native) ? native : enable ? (0x10UL | (1UL << 8)) : 0x10UL;
        public static ulong LcdOverdriveMiscSettingInput(bool enable, byte settingId = 0x0F) => NativeAcerProtocol.TryEncode(26, enable ? 1UL : 0, settingId, 0, 0, out ulong native) ? native : (ulong)settingId | ((ulong)(enable ? 1 : 0) << 8);
        public static byte MiscValue(ulong output) => NativeAcerProtocol.TryEncode(23, output, 0, 0, 0, out ulong native) ? (byte)native : (byte)((output >> 8) & 0xFF);

        public static IReadOnlyList<OperatingMode> DecodeOperatingModeMask(ulong output)
        {
            ulong mask = NativeAcerProtocol.TryEncode(22, output, 0, 0, 0, out ulong native) ? native : (output >> 8) & 0xFFFF;
            var list = new List<OperatingMode>();
            foreach (OperatingMode m in Enum.GetValues<OperatingMode>())
            {
                int bit = (int)m;
                if (bit >= 0 && bit < 64 && (mask & (1UL << bit)) != 0)
                    list.Add(m);
            }
            return list;
        }

        public const uint GamingSysInfoConfigQuery = 0x0004u;
        public const ulong GamingSysInfoWinKeyBit = 0x01UL << 8;
        public const ulong GamingSysInfoUsbChargeBit = 0x04UL << 8;
        public const ulong GamingSysInfoLcdOverdriveBit = 0x0FUL << 8;

        /// <summary>
        /// Strict Read-Modify-Write helper for system config bits to preserve upper status bytes
        /// containing volatile Fn/Media Key mode bits so they are never zeroed.
        /// </summary>
        public static ulong ApplySystemConfigBitPreservingUpperBytes(ulong currentSysInfo, ulong featureBitMask, bool enable)
        {
            if (NativeAcerProtocol.TryEncode(8, currentSysInfo, featureBitMask, enable ? 1UL : 0, 0, out ulong native)) return native;
            ulong baseVal = currentSysInfo & ~featureBitMask;
            return enable ? (baseVal | featureBitMask) : baseVal;
        }

        public static ulong UsbChargingInputWithPreservedState(bool on, int floor, ulong currentSysInfo = 0)
        {
            if (NativeAcerProtocol.TryEncode(9, on ? 1UL : 0, NativeAcerProtocol.Signed(floor), currentSysInfo, 0, out ulong native)) return native;
            byte flag = (byte)(on ? 15 : 31);
            ulong basePayload = 4UL | ((ulong)flag << 8) | ((ulong)floor << 16);
            ulong preservedUpper = currentSysInfo & 0xFFFFFFFFFF000000UL;
            return basePayload | preservedUpper;
        }

        public static ulong LcdOverdriveInputWithPreservedState(bool enable, ulong currentSysInfo = 0)
        {
            if (NativeAcerProtocol.TryEncode(10, enable ? 1UL : 0, currentSysInfo, 0, 0, out ulong native)) return native;
            ulong basePayload = (ulong)MiscSetting.LcdOverdrive | ((ulong)(enable ? 1 : 0) << 8);
            ulong preservedUpper = currentSysInfo & 0xFFFFFFFFFFFF0000UL;
            return basePayload | preservedUpper;
        }

        public static uint BacklightTimeoutQuery(byte hotkey) => NativeAcerProtocol.TryEncode(11, hotkey, 0, 0, 0, out ulong native) ? (uint)native : 1u | ((uint)hotkey << 8) | 0x80000u;
        public static ulong BacklightTimeoutInput(byte hotkey, int brightnessPercent, int timeoutSeconds, ulong preservedUpperFlags = 0x80000UL)
        {
            if (NativeAcerProtocol.TryEncode(12, hotkey, NativeAcerProtocol.Signed(brightnessPercent), NativeAcerProtocol.Signed(timeoutSeconds), preservedUpperFlags, out ulong native)) return native;
            byte b = (byte)Math.Clamp(brightnessPercent, 0, 100);
            byte t = (byte)Math.Clamp(timeoutSeconds, 0, 255);
            ulong flags = (preservedUpperFlags & 0xFFFF0000UL) | 0x80000UL;
            return 2UL | ((ulong)hotkey << 8) | flags | ((ulong)b << 32) | ((ulong)t << 40);
        }
        public static int BacklightBrightnessValue(ulong output) => NativeAcerProtocol.TryEncode(13, output, 0, 0, 0, out ulong native) ? (int)native : (int)((output >> 32) & 0xFF);
        public static int BacklightTimeoutValue(ulong output) => NativeAcerProtocol.TryEncode(14, output, 0, 0, 0, out ulong native) ? (int)native : (int)((output >> 40) & 0xFF);
    }

    #endregion

    #region BatteryProtocol

    public static class BatteryProtocol
    {
        public static (string, object)[] StatusArguments() => new (string, object)[]
        {
            ("uBatteryNo", (byte)1),
            ("uFunctionQuery", (byte)1),
            ("uReserved", new byte[2])
        };

        public static (string, object)[] SetArguments(BatteryFunction function, bool on) => new (string, object)[]
        {
            ("uBatteryNo", (byte)1),
            ("uFunctionMask", (byte)function),
            ("uFunctionStatus", on ? (byte)1 : (byte)0),
            ("uReservedIn", new byte[5])
        };

        public static BatteryHealthStatus? DecodeStatus(ulong functionList, byte[]? statusBytes)
        {
            if (statusBytes == null) return null;
            ulong status = (statusBytes.Length > 0 ? (ulong)statusBytes[0] : 0) | (statusBytes.Length > 1 ? (ulong)statusBytes[1] << 8 : 0);
            if (NativeAcerProtocol.TryEncode(29, functionList, status, 0, 0, out ulong native))
                return new BatteryHealthStatus((native & 1) != 0, (native & 2) != 0, (native & 4) != 0, (native & 8) != 0);
            bool healthSupported = (functionList & (ulong)BatteryFunction.HealthMode) != 0;
            bool calSupported = (functionList & (ulong)BatteryFunction.Calibration) != 0;

            int healthIdx = BitOperations.TrailingZeroCount((uint)BatteryFunction.HealthMode);
            int calIdx = BitOperations.TrailingZeroCount((uint)BatteryFunction.Calibration);

            bool healthOn = healthIdx < statusBytes.Length && statusBytes[healthIdx] == 1;
            bool calOn = calIdx < statusBytes.Length && statusBytes[calIdx] == 1;

            return new BatteryHealthStatus(healthSupported, calSupported, healthOn, calOn);
        }
    }

    #endregion

    #region Native Win32 Battery Reader

    [SupportedOSPlatform("windows")]
    public static class WindowsBattery
    {
        private const uint IOCTL_BATTERY_QUERY_TAG = 0x00294040;
        private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x00294044;
        private static readonly Guid GUID_DEVICE_BATTERY = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_QUERY_INFORMATION
        {
            public uint BatteryTag;
            public int InformationLevel;
            public uint AtRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_INFORMATION
        {
            public uint Capabilities;
            public byte Technology;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public byte[] Reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] Chemistry;
            public uint DesignedCapacity;
            public uint FullChargedCapacity;
            public uint DefaultAlert1;
            public uint DefaultAlert2;
            public uint CriticalBias;
            public uint CycleCount;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        public static BatteryHealth? Read()
        {
            try
            {
                var paths = GetBatteryDevicePaths();
                foreach (var path in paths)
                {
                    using var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                    if (handle.IsInvalid) continue;

                    uint returned = 0;
                    IntPtr pTag = Marshal.AllocHGlobal(4);
                    try
                    {
                        Marshal.WriteInt32(pTag, 0);
                        if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, pTag, 4, pTag, 4, out returned, IntPtr.Zero) || returned == 0)
                            continue;

                        uint tag = (uint)Marshal.ReadInt32(pTag);
                        if (tag == 0) continue;

                        var bqi = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = 0, AtRate = 0 };
                        int bqiSize = Marshal.SizeOf<BATTERY_QUERY_INFORMATION>();
                        int biSize = Marshal.SizeOf<BATTERY_INFORMATION>();
                        IntPtr pBqi = IntPtr.Zero;
                        IntPtr pBi = IntPtr.Zero;
                        try
                        {
                            pBqi = Marshal.AllocHGlobal(bqiSize);
                            pBi = Marshal.AllocHGlobal(biSize);
                            Marshal.StructureToPtr(bqi, pBqi, false);
                            if (DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, pBqi, (uint)bqiSize, pBi, (uint)biSize, out returned, IntPtr.Zero) && returned > 0)
                            {
                                var bi = Marshal.PtrToStructure<BATTERY_INFORMATION>(pBi);
                                if (bi.DesignedCapacity > 0)
                                {
                                    string? mfg = QueryBatteryString(handle, tag, 6);
                                    string? dev = QueryBatteryString(handle, tag, 4);

                                    return new BatteryHealth(
                                        (int)Math.Min(bi.DesignedCapacity, int.MaxValue),
                                        (int)Math.Min(bi.FullChargedCapacity, int.MaxValue),
                                        bi.CycleCount > 0 && bi.CycleCount < uint.MaxValue ? (int)bi.CycleCount : null,
                                        (bi.Capabilities & 0x40000000) != 0)
                                    {
                                        Manufacturer = mfg,
                                        Name = dev
                                    };
                                }
                            }
                        }
                        finally
                        {
                            if (pBqi != IntPtr.Zero) Marshal.FreeHGlobal(pBqi);
                            if (pBi != IntPtr.Zero) Marshal.FreeHGlobal(pBi);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pTag);
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? QueryBatteryString(SafeFileHandle handle, uint tag, int level)
        {
            var bqi = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level };
            int bqiSize = Marshal.SizeOf<BATTERY_QUERY_INFORMATION>();
            IntPtr pBqi = IntPtr.Zero;
            IntPtr pOut = IntPtr.Zero;
            try
            {
                pBqi = Marshal.AllocHGlobal(bqiSize);
                pOut = Marshal.AllocHGlobal(256);
                Marshal.StructureToPtr(bqi, pBqi, false);
                if (DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, pBqi, (uint)bqiSize, pOut, 256, out uint returned, IntPtr.Zero) && returned > 0)
                {
                    int charCount = Math.Min((int)(returned / 2), 128);
                    return Marshal.PtrToStringUni(pOut, charCount)?.Trim('\0', ' ');
                }
            }
            catch { }
            finally
            {
                if (pBqi != IntPtr.Zero) Marshal.FreeHGlobal(pBqi);
                if (pOut != IntPtr.Zero) Marshal.FreeHGlobal(pOut);
            }
            return null;
        }

        private static List<string> GetBatteryDevicePaths()
        {
            var paths = new List<string>();
            Guid guid = GUID_DEVICE_BATTERY;
            IntPtr hDevInfo = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
            if (hDevInfo == (IntPtr)(-1) || hDevInfo == IntPtr.Zero) return paths;

            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                uint index = 0;
                while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref guid, index++, ref did))
                {
                    SetupDiGetDeviceInterfaceDetail(hDevInfo, ref did, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
                    int minDetailSize = IntPtr.Size == 8 ? 8 : 6;
                    if (requiredSize < (uint)minDetailSize) continue;

                    IntPtr pDetail = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        Marshal.WriteInt32(pDetail, minDetailSize); // cbSize
                        if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref did, pDetail, requiredSize, out _, IntPtr.Zero))
                        {
                            IntPtr pPath = (IntPtr)((long)pDetail + 4);
                            string? path = Marshal.PtrToStringAuto(pPath);
                            if (!string.IsNullOrEmpty(path)) paths.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pDetail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }
            return paths;
        }
    }

    #endregion

    #region AcerSmbios

    [SupportedOSPlatform("windows")]
    public sealed record AcerSmbios(byte? GamingMajor, byte? GamingMinor, IReadOnlyList<AcerSmbiosRecord> GamingRecords, IReadOnlyList<AcerSmbiosRecord> HotkeyFunctions)
    {
        public static AcerSmbios Empty { get; } = new(null, null, Array.Empty<AcerSmbiosRecord>(), Array.Empty<AcerSmbiosRecord>());

        public double? GamingVersion => (GamingMajor.HasValue && GamingMinor.HasValue)
            ? GamingMajor.Value + (GamingMinor.Value / 100.0)
            : null;

        public bool HasEcLightBars => Gaming(1) == 1;

        public ushort? Gaming(byte recordId) =>
            GamingRecords.FirstOrDefault(r => r.Id == recordId)?.Value;

        public bool HasHotkey(byte id) => HotkeyFunctions.Any(r => r.Id == id);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(uint ProviderSignature, uint TableID, IntPtr pFirmwareTableBuffer, uint BufferSize);

        public static AcerSmbios Read()
        {
            try
            {
                uint rsmb = 0x52534D42; // 'RSMB'
                uint size = GetSystemFirmwareTable(rsmb, 0, IntPtr.Zero, 0);
                if (size < 8 || size > 16 * 1024 * 1024) return Empty;

                byte[] buf = new byte[size];
                GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
                uint bytesRead = 0;
                try
                {
                    bytesRead = GetSystemFirmwareTable(rsmb, 0, pin.AddrOfPinnedObject(), size);
                    if (bytesRead < 8 || bytesRead > size)
                        return Empty;
                }
                finally { pin.Free(); }

                uint declaredLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
                int len = (int)Math.Min((long)declaredLen, (long)(bytesRead - 8));
                if (len <= 0) return Empty;
                return Parse(buf.AsSpan(8, len));
            }
            catch { return Empty; }
        }

        internal static AcerSmbios Parse(ReadOnlySpan<byte> table)
        {
            return FirmwareParsingNative.TryParseSmbios(table, out var native) ? native : ParseManaged(table);
        }

        internal static AcerSmbios ParseManaged(ReadOnlySpan<byte> table)
        {
            byte? major = null, minor = null;
            var gaming = new List<AcerSmbiosRecord>();
            var hotkeys = new List<AcerSmbiosRecord>();
            int i = 0;

            while (i + 4 <= table.Length)
            {
                byte type = table[i];
                byte length = table[i + 1];
                if (length < 4 || i + length > table.Length) break;

                var data = table.Slice(i, length);
                if (type == 172 && data.Length >= 6) // 0xAC
                {
                    major = data[4];
                    minor = data[5];
                    for (int k = 6; k + 3 <= data.Length; k += 3)
                        gaming.Add(new AcerSmbiosRecord(data[k], BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(k + 1))));
                }
                else if (type == 170) // 0xAA
                {
                    for (int k = 14; k + 4 <= data.Length; k += 4)
                    {
                        if (data[k + 1] == 2)
                            hotkeys.Add(new AcerSmbiosRecord(data[k], BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(k + 2))));
                    }
                }

                int next = i + length;
                while (next + 1 < table.Length && (table[next] != 0 || table[next + 1] != 0)) next++;
                i = next + 2;
                if (type == 127) break;
            }

            return new AcerSmbios(major, minor, gaming, hotkeys);
        }
    }

    #endregion

    #region SystemInfo & Hardware Identity

    public static class SystemInfo
    {
        public static string Model => HardwareIdentity.ReadModel();
    }

    [SupportedOSPlatform("windows")]
    public static class HardwareIdentity
    {
        public static string ReadCpuModel()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                string? name = key?.GetValue("ProcessorNameString") as string;
                if (!string.IsNullOrWhiteSpace(name))
                    return CleanCpuModelString(name);
            }
            catch { }

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, MaxClockSpeed FROM Win32_Processor");
                using var coll = searcher.Get();
                foreach (ManagementObject obj in coll)
                {
                    using (obj)
                    {
                        string? name = obj["Name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                            return CleanCpuModelString(name);
                    }
                }
            }
            catch { }

            return "Intel Core Processor";
        }

        private static string CleanCpuModelString(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Intel Core Processor";

            return Regex.Replace(name.Trim(), @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(50));
        }

        public static string ReadModel()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                using var coll = searcher.Get();
                foreach (ManagementObject obj in coll)
                {
                    using (obj)
                    {
                        string mfg = obj["Manufacturer"]?.ToString() ?? "";
                        string model = obj["Model"]?.ToString() ?? "";
                        return $"{mfg} {model}".Trim();
                    }
                }
            }
            catch { }
            return "Acer Predator Gaming Device";
        }

        public static string ReadBiosVersion()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
                using var coll = searcher.Get();
                foreach (ManagementObject obj in coll)
                {
                    using (obj)
                    {
                        return obj["SMBIOSBIOSVersion"]?.ToString() ?? "Unknown";
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        public static string ReadSerialNumber()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
                using var coll = searcher.Get();
                foreach (ManagementObject obj in coll)
                {
                    using (obj)
                    {
                        string sn = obj["SerialNumber"]?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(sn)) return sn;
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        public static string? ComputeSnid(string serialNumber)
        {
            if (string.IsNullOrEmpty(serialNumber) || serialNumber.Length != 22) return null;
            if (!int.TryParse(serialNumber.AsSpan(13, 5), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int hexVal))
                return null;

            char c = char.ToUpperInvariant(serialNumber[19]);
            int suffix = char.IsDigit(c) ? c - '0' : (c >= 'A' && c <= 'Z' ? c - 'A' + 10 : -1);
            if (suffix < 0) return null;

            return $"{serialNumber.Substring(10, 3)}{hexVal:D6}{serialNumber[18]}{suffix}";
        }
    }

    #endregion

    #region CPU Architecture & Specification Detection Engine

    public enum CpuArchitectureFamily
    {
        IntelCoreUltraSeries1, // Meteor Lake (e.g. Ultra 5/7/9 1xxH, Ultra 1xxU)
        IntelCoreUltraSeries2, // Arrow Lake / Lunar Lake (e.g. Ultra 2xxV, 2xxH, 2xxHX, 2xxU)
        IntelCoreHx,           // 12th/13th/14th Gen HX (e.g. 14900HX, 13700HX, 12900HX)
        IntelCoreH,            // 10th-14th Gen H / HK (e.g. 14700H, 13700H, 12700H, 11800H)
        IntelCoreU,            // U-series / Thin laptops (15W base TDP)
        IntelCoreDesktop,      // Desktop K / non-K
        AmdRyzenHx,            // Ryzen HX (55W)
        AmdRyzenHs,            // Ryzen HS (35W)
        AmdRyzenH,             // Ryzen H (45W)
        AmdRyzenU,             // Ryzen U (15W)
        GenericIntel,
        GenericAmd,
        Unknown
    }

    public sealed record CpuHardwareSpec
    {
        public string CpuModel { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public CpuArchitectureFamily Family { get; init; } = CpuArchitectureFamily.Unknown;
        public int BaseTdpWatts { get; init; }
        public int MinPowerWatts { get; init; }
        public int MaxPl1Watts { get; init; }
        public int MaxPl2Watts { get; init; }
        public bool IsDynamicDetection { get; init; }
        public IReadOnlyDictionary<byte, (int Pl1, int Pl2)> DefaultModePowerLimits { get; init; } = new Dictionary<byte, (int, int)>();
    }

    public sealed record CpuSpecInfo(
        string ModelName,
        int BaseTdpWatts,
        int MinPowerWatts,
        int MaxPl1Watts,
        int MaxPl2Watts
    );

    public static class CpuSpecDetector
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
        private static readonly Regex Ultra2Regex = new(@"\bULTRA\s+[3579]\s+(2\d{2})([A-Z]*)\b|\bULTRA\s+(2\d{2})([A-Z]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex Ultra1Regex = new(@"\bULTRA\s+[3579]\s+(1\d{2})([A-Z]*)\b|\bULTRA\s+(1\d{2})([A-Z]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex UltraGenericRegex = new(@"\bULTRA\s+[3579]\b|\bCORE\s+ULTRA\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

        private static readonly Regex IntelHxRegex = new(@"\b(?:\d{4,5}|[iI][3579]-\d{4,5})HX\b|\bHX\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex IntelHRegex = new(@"\b(?:\d{4,5}|[iI][3579]-\d{4,5})(?:H|HK)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex IntelURegex = new(@"\b(?:\d{4,5}|[iI][3579]-\d{4,5})U\b|\b\d{4}G[147]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex IntelDesktopKRegex = new(@"\b(?:\d{4,5}|[iI][3579]-\d{4,5})(?:K|KF|KS)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

        private static readonly Regex AmdHxRegex = new(@"\bHX3D\b|\bHX\b|\bAI\s+9\s+HX\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex AmdHsRegex = new(@"\bHS\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex AmdHRegex = new(@"\b\d{4}H\b|\bH\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex AmdURegex = new(@"\b\d{4}U\b|\bU\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

        public static CpuHardwareSpec DetectCpuSpec(string? cpuModel = null) => Detect(cpuModel);

        public static CpuHardwareSpec Detect(string? cpuModel = null)
        {
            string rawModel = !string.IsNullOrWhiteSpace(cpuModel)
                ? cpuModel.Trim()
                : (OperatingSystem.IsWindows() ? HardwareIdentity.ReadCpuModel() : "Intel Core Processor");

            string upper = rawModel.ToUpperInvariant();

            // 1. AMD Ryzen architectures
            if (upper.Contains("RYZEN") || upper.Contains("AMD"))
            {
                if (AmdHxRegex.IsMatch(upper))
                {
                    return CreateSpec(rawModel, "AMD Ryzen HX (High Performance Mobile)", CpuArchitectureFamily.AmdRyzenHx, 55, 15, 125, 140);
                }
                if (AmdHsRegex.IsMatch(upper))
                {
                    return CreateSpec(rawModel, "AMD Ryzen HS (Slim & High Performance)", CpuArchitectureFamily.AmdRyzenHs, 35, 15, 65, 80);
                }
                if (AmdHRegex.IsMatch(upper))
                {
                    return CreateSpec(rawModel, "AMD Ryzen H-Series (High Performance Mobile)", CpuArchitectureFamily.AmdRyzenH, 45, 12, 80, 95);
                }
                if (AmdURegex.IsMatch(upper))
                {
                    return CreateSpec(rawModel, "AMD Ryzen U-Series (Ultra Low Power Mobile)", CpuArchitectureFamily.AmdRyzenU, 15, 5, 30, 45);
                }

                return CreateSpec(rawModel, "AMD Ryzen Processor (Mobile)", CpuArchitectureFamily.GenericAmd, 45, 12, 80, 95);
            }

            // 2. Intel Core Ultra Series 2 (Arrow Lake / Lunar Lake)
            var mUltra2 = Ultra2Regex.Match(upper);
            if (mUltra2.Success)
            {
                string suffix = mUltra2.Groups[2].Value;
                if (string.IsNullOrEmpty(suffix) && mUltra2.Groups.Count > 4)
                    suffix = mUltra2.Groups[4].Value;

                if (suffix.Contains("V"))
                {
                    // Lunar Lake (e.g. Ultra 7 258V, 268V, 256V, 226V)
                    return CreateSpec(rawModel, "Intel Lunar Lake (Core Ultra Series 2 - V)", CpuArchitectureFamily.IntelCoreUltraSeries2, 17, 8, 30, 37);
                }
                if (suffix.Contains("HX"))
                {
                    // Arrow Lake HX (e.g. Ultra 9 285HX, 275HX, 265HX)
                    return CreateSpec(rawModel, "Intel Arrow Lake (Core Ultra Series 2 - HX)", CpuArchitectureFamily.IntelCoreUltraSeries2, 55, 15, 157, 175);
                }
                if (suffix.Contains("U"))
                {
                    // Arrow Lake U (e.g. Ultra 7 255U)
                    return CreateSpec(rawModel, "Intel Arrow Lake (Core Ultra Series 2 - U)", CpuArchitectureFamily.IntelCoreUltraSeries2, 15, 5, 28, 57);
                }

                // Arrow Lake H (e.g. Ultra 9 285H, Ultra 7 265H, Ultra 5 245H, Ultra 5 225H)
                return CreateSpec(rawModel, "Intel Arrow Lake (Core Ultra Series 2 - H)", CpuArchitectureFamily.IntelCoreUltraSeries2, 28, 8, 64, 115);
            }

            // 3. Intel Core Ultra Series 1 (Meteor Lake)
            var mUltra1 = Ultra1Regex.Match(upper);
            if (mUltra1.Success)
            {
                string suffix = mUltra1.Groups[2].Value;
                if (string.IsNullOrEmpty(suffix) && mUltra1.Groups.Count > 4)
                    suffix = mUltra1.Groups[4].Value;

                if (suffix.Contains("U"))
                {
                    // Meteor Lake U (e.g. Ultra 7 165U, Ultra 7 155U, Ultra 5 135U, Ultra 5 125U)
                    return CreateSpec(rawModel, "Intel Meteor Lake (Core Ultra Series 1 - U)", CpuArchitectureFamily.IntelCoreUltraSeries1, 15, 5, 28, 57);
                }

                // Meteor Lake H (e.g. Ultra 9 185H, Ultra 7 165H, Ultra 7 155H, Ultra 5 135H, Ultra 5 125H)
                return CreateSpec(rawModel, "Intel Meteor Lake (Core Ultra Series 1 - H)", CpuArchitectureFamily.IntelCoreUltraSeries1, 28, 8, 64, 115);
            }

            // Generic Core Ultra without SKU number (fallback)
            if (UltraGenericRegex.IsMatch(upper))
            {
                return CreateSpec(rawModel, "Intel Core Ultra Processor", CpuArchitectureFamily.IntelCoreUltraSeries1, 28, 8, 64, 115);
            }

            // 4. Intel 12th/13th/14th Gen HX (e.g. 14900HX, 13700HX, 12900HX)
            if (IntelHxRegex.IsMatch(upper))
            {
                bool isI9 = upper.Contains("I9") || upper.Contains("14900") || upper.Contains("13900") || upper.Contains("13980") || upper.Contains("13950") || upper.Contains("12900");
                int maxPl2 = isI9 ? 175 : 160;
                return CreateSpec(rawModel, "Intel Raptor Lake / Alder Lake HX", CpuArchitectureFamily.IntelCoreHx, 55, 15, 157, maxPl2);
            }

            // 5. Intel 10th-14th Gen H / HK (e.g. 14700H, 13700H, 12700H, 11800H)
            if (IntelHRegex.IsMatch(upper))
            {
                bool isHighTier = upper.Contains("I9") || upper.Contains("I7");
                int maxPl1 = isHighTier ? 95 : 80;
                int maxPl2 = isHighTier ? 115 : 95;
                return CreateSpec(rawModel, "Intel Core H-Series (High Performance Mobile)", CpuArchitectureFamily.IntelCoreH, 45, 12, maxPl1, maxPl2);
            }

            // 6. Intel Desktop Unlocked (K / KF / KS)
            if (IntelDesktopKRegex.IsMatch(upper))
            {
                return CreateSpec(rawModel, "Intel Core Desktop (Unlocked)", CpuArchitectureFamily.IntelCoreDesktop, 125, 35, 253, 253);
            }

            // 7. Intel U-series / Thin Laptops (e.g. 1335U, 1235U, 1165G7)
            if (IntelURegex.IsMatch(upper))
            {
                return CreateSpec(rawModel, "Intel Core U-Series (Ultra Low Power Mobile)", CpuArchitectureFamily.IntelCoreU, 15, 5, 30, 55);
            }

            // 8. Generic / Unknown Fallback
            return CreateSpec(rawModel, "Intel Core Processor (Mobile)", CpuArchitectureFamily.GenericIntel, 45, 12, 95, 115);
        }

        private static CpuHardwareSpec CreateSpec(string model, string arch, CpuArchitectureFamily family, int baseTdp, int minPower, int maxPl1, int maxPl2)
        {
            var modeDefaults = ComputeModeDefaults(baseTdp, minPower, maxPl1, maxPl2);
            return new CpuHardwareSpec
            {
                CpuModel = model,
                Architecture = arch,
                Family = family,
                BaseTdpWatts = baseTdp,
                MinPowerWatts = minPower,
                MaxPl1Watts = maxPl1,
                MaxPl2Watts = maxPl2,
                IsDynamicDetection = true,
                DefaultModePowerLimits = modeDefaults
            };
        }

        public static IReadOnlyDictionary<byte, (int Pl1, int Pl2)> ComputeModeDefaults(int baseTdp, int minPower, int maxPl1, int maxPl2)
        {
            int safeMaxPl2 = Math.Max(minPower, maxPl2);
            int safeMaxPl1 = Math.Clamp(maxPl1, minPower, safeMaxPl2);
            safeMaxPl2 = Math.Max(safeMaxPl1, safeMaxPl2);

            int ecoPl1 = Math.Clamp((int)Math.Round(baseTdp * 0.55), minPower, safeMaxPl1);
            int ecoPl2 = Math.Clamp((int)Math.Round(baseTdp * 0.75), ecoPl1, safeMaxPl2);

            int quietPl1 = Math.Clamp((int)Math.Round(baseTdp * 0.75), minPower, safeMaxPl1);
            int quietPl2 = Math.Clamp(baseTdp, quietPl1, safeMaxPl2);

            int balancedPl1 = Math.Clamp(baseTdp, minPower, safeMaxPl1);
            int balancedPl2 = Math.Clamp((int)Math.Round(baseTdp * 1.45), balancedPl1, safeMaxPl2);

            int perfPl1 = Math.Clamp((int)Math.Round(baseTdp * 1.55), balancedPl1, safeMaxPl1);
            int perfPl2 = Math.Clamp((int)Math.Round(baseTdp * 2.00), perfPl1, safeMaxPl2);

            int turboPl1 = Math.Clamp((int)Math.Round(baseTdp * 2.00), perfPl1, safeMaxPl1);
            int turboPl2 = Math.Max(turboPl1, safeMaxPl2);

            return new Dictionary<byte, (int, int)>
            {
                { 0x06, (ecoPl1, ecoPl2) },           // Eco
                { 0x00, (quietPl1, quietPl2) },       // Quiet
                { 0x01, (balancedPl1, balancedPl2) }, // Balanced
                { 0x04, (perfPl1, perfPl2) },         // Performance
                { 0x05, (turboPl1, turboPl2) }        // Turbo
            };
        }

        public static (int Pl1, int Pl2) EstimateProportionalLimits(int tdpWatts, byte mode)
        {
            int tdp = Math.Clamp(tdpWatts, 5, 250);
            int minP = Math.Max(5, (int)Math.Round(tdp * 0.20));
            int maxP2 = (int)Math.Round(tdp * 2.55);
            int maxP1 = (int)Math.Round(maxP2 * 0.85);
            var dict = ComputeModeDefaults(tdp, minP, maxP1, maxP2);
            if (dict.TryGetValue(mode, out var p)) return p;
            return (tdp, (int)Math.Round(tdp * 1.35));
        }

        public static int EstimatePl1(int tdpWatts, byte mode) => EstimateProportionalLimits(tdpWatts, mode).Pl1;
        public static int EstimatePl2(int tdpWatts, byte mode) => EstimateProportionalLimits(tdpWatts, mode).Pl2;

        public static IReadOnlyDictionary<byte, (int Pl1, int Pl2)> GetDefaultModeDictionary(int tdpWatts)
        {
            int tdp = Math.Clamp(tdpWatts, 5, 250);
            int minP = Math.Max(5, (int)Math.Round(tdp * 0.20));
            int maxP2 = (int)Math.Round(tdp * 2.55);
            int maxP1 = (int)Math.Round(maxP2 * 0.85);
            return ComputeModeDefaults(tdp, minP, maxP1, maxP2);
        }
    }

    #endregion

        #region Direct Embedded Controller (EC) HID Protocol

    [SupportedOSPlatform("windows")]
    public sealed class EcHidDevice : IDisposable
    {
        public const ushort VendorId = 0x1025; // 4133
        public const ushort ProductId = 0x174B; // 5963
        public const ushort UsagePage = 0xFF05; // 65285
        public const ushort Usage = 1;
        public const byte ReportId = 0xA0;
        public const int ReportLength = 65;
        public const ushort RequestHeader = 0xA000;

        public static string MutexName => $"Global\\ECHIDFeatureVID_{VendorId:X4}&PID_{ProductId:X4}";

        private SafeFileHandle? _deviceHandle;
        private Mutex? _globalLock;
        private int _reportLength = ReportLength;
        private readonly object _localLock = new();
        private DateTime _lastReopenAttempt = DateTime.MinValue;
        private static readonly TimeSpan ReopenCooldown = TimeSpan.FromMilliseconds(500);
        private bool _disposed;
        private bool _wasEverOpened;

        /// <summary>
        /// True when the device is owned by the native EC-HID transport
        /// (<c>predator_core::ec_hid</c>). The managed handle then stays closed, so the
        /// EC HID endpoint is never opened twice.
        /// </summary>
        private bool _usesNative;

        public bool IsOpen
        {
            get
            {
                lock (_localLock)
                {
                    if (_disposed) return false;
                    if (_usesNative) return PredatorNativeCore.EcHidIsOpen();
                    return _deviceHandle != null && !_deviceHandle.IsInvalid;
                }
            }
        }

        /// <summary>True when this instance is driven by the native transport.</summary>
        public bool UsesNativeTransport => _usesNative;
        public EcHidVersion? Version { get; private set; }
        public int ReportLengthActual => _reportLength;

        public static EcHidDevice? TryOpen()
        {
            // 1. Native transport first: one C-ABI call owns enumeration, CreateFile and
            //    the feature exchange, and its handle is closed by Rust's Drop impl.
            try
            {
                if (PredatorNativeCore.IsNativeCoreAvailable &&
                    PredatorNativeCore.EcHidOpen(VendorId, ProductId, UsagePage, Usage))
                {
                    int nativeReportLength = Math.Clamp(PredatorNativeCore.EcHidReportLength(), ReportLength, 4096);
                    var nativeDevice = new EcHidDevice
                    {
                        _usesNative = true,
                        _reportLength = nativeReportLength,
                        _wasEverOpened = true
                    };
                    if (nativeDevice.ReadVersion() != null) return nativeDevice;
                    nativeDevice.Dispose();
                }
            }
            catch { }

            // 2. Managed fallback.
            try
            {
                var paths = FindDevicePaths();
                foreach (var path in paths)
                {
                    var handle = CreateFile(path, 0xC0000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    }
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    try
                    {
                        int repLen = ReportLength;
                        if (HidD_GetPreparsedData(handle, out IntPtr pData))
                        {
                            try
                            {
                                var caps = new HIDP_CAPS();
                                if (HidP_GetCaps(pData, ref caps) >= 0 && caps.FeatureReportByteLength > 0)
                                {
                                    repLen = caps.FeatureReportByteLength;
                                }
                            }
                            finally
                            {
                                HidD_FreePreparsedData(pData);
                            }
                        }

                        Mutex? mtx = null;
                        try { mtx = new Mutex(false, MutexName); } catch { }
                        EcHidDevice? dev = null;
                        try
                        {
                            dev = new EcHidDevice { _deviceHandle = handle, _globalLock = mtx, _reportLength = repLen, _wasEverOpened = true };
                            if (dev.ReadVersion() != null) return dev;
                            dev.Dispose();
                            dev = null;
                        }
                        catch
                        {
                            dev?.Dispose();
                            mtx?.Dispose();
                            handle.Dispose();
                        }
                    }
                    catch
                    {
                        handle.Dispose();
                    }
                }
            }
            catch { }
            return null;
        }

        public EcHidVersion? ReadVersion()
        {
            for (int i = 0; i < 3; i++)
            {
                var rep = Exchange(BuildRequest(EcHidCommand.Status, (byte)EcHidStatus.Version));
                if (rep != null && rep.Done && rep.Command == 0)
                {
                    if (rep.Word(6) == ushort.MaxValue)
                        return Version = new EcHidVersion(rep.Byte(4), rep.Byte(5));
                    if (rep.Byte(4) == 0)
                        return Version = new EcHidVersion(rep.Byte(6), rep.Byte(7));
                }
                Thread.Sleep(20);
            }
            return null;
        }

        public byte? ReadMode()
        {
            var rep = Exchange(BuildRequest(EcHidCommand.Mode, 2));
            if (rep != null && rep.Done && rep.Command == (ushort)EcHidCommand.Mode && rep.Data != null && rep.Data.Length > 4)
            {
                if (Version?.EchoesStatusType == true || (rep.Byte(4) == 2 && rep.Data.Length > 5))
                    return rep.Byte(5);
                return rep.Byte(4);
            }
            return null;
        }

        public bool WriteMode(byte mode)
        {
            var rep = Exchange(BuildRequest(EcHidCommand.Mode, 1, mode));
            return rep != null && rep.Done && rep.Command == (ushort)EcHidCommand.Mode;
        }

        public (int Brightness, int TimeoutSeconds)? ReadBacklightTimeout()
        {
            var rep = Exchange(BuildRequest(EcHidCommand.Device, 2, 2));
            if (rep != null && rep.Done && rep.Command == (ushort)EcHidCommand.Device && rep.Byte(5) == 2 && rep.Data != null && rep.Data.Length >= 12)
                return (rep.Word(8), rep.Word(10));
            return null;
        }

        public bool WriteBacklightTimeout(int brightness, int timeoutSeconds)
        {
            byte[] data = new byte[7];
            data[0] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3), (ushort)Math.Clamp(brightness, 0, 100));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(5), (ushort)Math.Clamp(timeoutSeconds, 0, 65535));

            var rep = Exchange(BuildRequest(EcHidCommand.Device, 1, data));
            return rep != null && rep.Done && rep.Command == (ushort)EcHidCommand.Device;
        }

        private bool Reopen()
        {
            if (_disposed || !_wasEverOpened) return false;
            if ((DateTime.UtcNow - _lastReopenAttempt) < ReopenCooldown) return false;
            _lastReopenAttempt = DateTime.UtcNow;
            try
            {
                _deviceHandle?.Dispose();
                _deviceHandle = null;
                var paths = FindDevicePaths();
                foreach (var path in paths)
                {
                    var handle = CreateFile(path, 0xC0000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    }
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    try
                    {
                        int repLen = ReportLength;
                        if (HidD_GetPreparsedData(handle, out IntPtr pData))
                        {
                            try
                            {
                                var caps = new HIDP_CAPS();
                                if (HidP_GetCaps(pData, ref caps) >= 0 && caps.FeatureReportByteLength > 0)
                                {
                                    repLen = caps.FeatureReportByteLength;
                                }
                            }
                            finally
                            {
                                HidD_FreePreparsedData(pData);
                            }
                        }
                        _deviceHandle = handle;
                        _reportLength = repLen;
                        return true;
                    }
                    catch
                    {
                        handle.Dispose();
                    }
                }
            }
            catch { }
            return false;
        }

        public EcHidReply? Exchange(byte[] request)
        {
            if (_disposed) return null;

            if (_usesNative)
            {
                lock (_localLock)
                {
                    if (_disposed) return null;
                    return ExchangeNative(request);
                }
            }

            lock (_localLock)
            {
                if (_disposed) return null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(20);

                    if (_deviceHandle == null || _deviceHandle.IsInvalid)
                    {
                        if (!Reopen()) return null;
                    }

                    bool lockTaken = false;
                    bool needReopen = false;
                    try
                    {
                        if (_globalLock != null)
                        {
                            try { lockTaken = _globalLock.WaitOne(500); }
                            catch (AbandonedMutexException) { lockTaken = true; }
                        }
                        if (_globalLock != null && !lockTaken) continue;

                        // Call HidD_GetFeature before HidD_SetFeature to sync feature state
                        // and ensure volatile BIOS / EC mode bits (e.g. Media/Fn keys) are never clobbered.
                        int repLen = Math.Clamp(_reportLength > 0 ? _reportLength : ReportLength, 65, 4096);
                        byte[] preBuf = new byte[repLen];
                        preBuf[0] = ReportId;
                        GetFeature(_deviceHandle!, preBuf);

                        if (!SetFeature(_deviceHandle!, request))
                        {
                            needReopen = true;
                        }
                        else
                        {
                            Thread.Sleep(20);

                            repLen = Math.Clamp(_reportLength > 0 ? _reportLength : ReportLength, 65, 4096);
                            byte[] replyBuf = new byte[repLen];
                            replyBuf[0] = ReportId;
                            if (!GetFeature(_deviceHandle!, replyBuf))
                            {
                                needReopen = true;
                            }
                            else if (replyBuf.Length >= 6 && replyBuf[0] == ReportId)
                            {
                                byte[] data = replyBuf.AsSpan(1).ToArray();
                                return new EcHidReply(BinaryPrimitives.ReadUInt16LittleEndian(data), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)), data);
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        if (lockTaken)
                        {
                            try { _globalLock?.ReleaseMutex(); } catch { }
                        }
                    }

                    if (needReopen)
                    {
                        Reopen();
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// One exchange through the native transport. Returns null (and lets the caller
        /// retry) when the reply report is malformed or the EC did not answer.
        /// </summary>
        private EcHidReply? ExchangeNative(byte[] request)
        {
            if (request == null || request.Length == 0) return null;

            if (!PredatorNativeCore.EcHidIsOpen())
            {
                if (!ReopenNative()) return null;
            }

            int reportLength = Math.Clamp(PredatorNativeCore.EcHidReportLength(), ReportLength, 4096);
            if (reportLength <= 0) return null;

            byte[] reply = new byte[reportLength];
            int written = PredatorNativeCore.EcHidExchange(request, reply);
            if (written < 6 || reply[0] != ReportId) return null;

            int payloadLength = Math.Min(written, reportLength) - 1;
            if (payloadLength < 4) return null;

            byte[] data = reply.AsSpan(1, payloadLength).ToArray();
            return new EcHidReply(
                BinaryPrimitives.ReadUInt16LittleEndian(data),
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)),
                data);
        }

        private bool ReopenNative()
        {
            if (_disposed || !_wasEverOpened) return false;
            if ((DateTime.UtcNow - _lastReopenAttempt) < ReopenCooldown) return false;
            _lastReopenAttempt = DateTime.UtcNow;
            try
            {
                if (PredatorNativeCore.EcHidOpen(VendorId, ProductId, UsagePage, Usage))
                {
                    _reportLength = Math.Clamp(PredatorNativeCore.EcHidReportLength(), ReportLength, 4096);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private byte[] BuildRequest(EcHidCommand cmd, byte func, params byte[] data)
        {
            int targetLen = Math.Clamp(_reportLength > 0 ? _reportLength : ReportLength, 65, 4096);
            byte[] buf = new byte[targetLen];
            buf[0] = ReportId;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(1), RequestHeader);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(3), (ushort)cmd);
            buf[5] = func;
            if (data != null && data.Length > 0)
            {
                int maxPayload = Math.Max(0, targetLen - 6);
                int copyLen = Math.Min(data.Length, maxPayload);
                data.AsSpan(0, copyLen).CopyTo(buf.AsSpan(6));
            }
            return buf;
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(SafeFileHandle HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid HidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        private static List<string> FindDevicePaths()
        {
            var exactMatches = new List<string>();
            var candidateMatches = new List<string>();
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (hDevInfo == (IntPtr)(-1) || hDevInfo == IntPtr.Zero) return exactMatches;

            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                uint index = 0;
                while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, index++, ref did))
                {
                    SetupDiGetDeviceInterfaceDetail(hDevInfo, ref did, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                    int minDetailSize = IntPtr.Size == 8 ? 8 : 6;
                    if (reqSize < (uint)minDetailSize) continue;

                    IntPtr pDetail = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(pDetail, minDetailSize);
                        if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref did, pDetail, reqSize, out _, IntPtr.Zero))
                        {
                            IntPtr pPath = (IntPtr)((long)pDetail + 4);
                            string? path = Marshal.PtrToStringAuto(pPath);
                            if (!string.IsNullOrEmpty(path))
                            {
                                using var dev = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                                if (!dev.IsInvalid)
                                {
                                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                                    if (HidD_GetAttributes(dev, ref attr))
                                    {
                                        if (attr.VendorID == VendorId && attr.ProductID == ProductId)
                                        {
                                            bool isExact = false;
                                            bool isCandidate = false;
                                            if (HidD_GetPreparsedData(dev, out IntPtr pData))
                                            {
                                                try
                                                {
                                                    var caps = new HIDP_CAPS();
                                                    if (HidP_GetCaps(pData, ref caps) >= 0)
                                                    {
                                                        // Exclude standard keyboard (UsagePage == 0x0001, Usage == 0x0006) and consumer controls (UsagePage == 0x000C)
                                                        if ((caps.UsagePage == 0x0001 && caps.Usage == 0x0006) || caps.UsagePage == 0x000C || caps.UsagePage == 0x0001)
                                                        {
                                                            continue;
                                                        }

                                                        if (caps.UsagePage == UsagePage && caps.Usage == Usage && caps.FeatureReportByteLength >= ReportLength)
                                                        {
                                                            isExact = true;
                                                        }
                                                        else if (caps.UsagePage >= 0xFF00 && caps.FeatureReportByteLength >= ReportLength)
                                                        {
                                                            isCandidate = true;
                                                        }
                                                    }
                                                }
                                                finally
                                                {
                                                    HidD_FreePreparsedData(pData);
                                                }
                                            }

                                            if (isExact)
                                                exactMatches.Add(path);
                                            else if (isCandidate)
                                                candidateMatches.Add(path);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(pDetail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(hDevInfo); }
            exactMatches.AddRange(candidateMatches);
            return exactMatches;
        }

        public static bool IsStandardKeyboardOrConsumerCollection(ushort usagePage, ushort usage)
        {
            return (usagePage == 0x0001 && usage == 0x0006) || usagePage == 0x000C || usagePage == 0x0001;
        }

        private static bool SetFeature(SafeFileHandle h, byte[] buf) => HidD_SetFeature(h, buf, buf.Length);
        private static bool GetFeature(SafeFileHandle h, byte[] buf) => HidD_GetFeature(h, buf, buf.Length);

        public void Dispose()
        {
            lock (_localLock)
            {
                if (_disposed) return;
                _disposed = true;
                if (_usesNative)
                {
                    try { PredatorNativeCore.EcHidClose(); } catch { }
                    _usesNative = false;
                }
                try { _deviceHandle?.Dispose(); } catch { }
                _deviceHandle = null;
                try { _globalLock?.Dispose(); } catch { }
                _globalLock = null;
            }
            GC.SuppressFinalize(this);
        }

        ~EcHidDevice()
        {
            Dispose();
        }
    }

    #endregion

    #region GPU Power State Awareness (AC-Only Telemetry)

    [SupportedOSPlatform("windows")]
    public static class GpuPowerMonitor
    {
        private static volatile bool _isOnBattery = false;

        public static void SetPowerState(bool onBattery)
        {
            _isOnBattery = onBattery;
        }

        public static void ResetDebounce()
        {
        }

        public static bool IsGpuAsleep()
        {
            // GPU telemetry turns on ONLY when connected to AC power.
            // On battery, GPU telemetry stays in low power standby regardless of 3D workloads.
            return _isOnBattery;
        }
    }
    #endregion
}
