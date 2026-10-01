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
        Custom = 2,
        Max = 3
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
        OperatingMode = 11
    }

    public enum GpuMode : byte
    {
        Hybrid = 1,
        Discrete = 2
    }

    public enum DustDefenderStart
    {
        Started,
        Busy,
        Failed,
        Running
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
        DustDefender
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
        Version = 1,
        Adapter = 2,
        ModeLimit = 3,
        BatteryBoost = 4,
        UsbCAdapter = 5
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
        public bool? DustDefenderRunning
        {
            get
            {
                if (Kind != FirmwareEventKind.Thermal || Value != 1 || Detail.Length <= 2) return null;
                return Detail[2] == 1;
            }
        }

        public static FirmwareEvent? Decode(byte[]? detail)
        {
            if (detail == null || detail.Length == 0) return null;
            return new FirmwareEvent((FirmwareEventKind)detail[0], (byte)(detail.Length > 1 ? detail[1] : 0), detail.ToArray());
        }

        public override string ToString() => $"{Kind} Value={Value} Data={Convert.ToHexString(Detail)}";
    }

    public readonly record struct EcHidVersion(byte Major, byte Minor)
    {
        public bool EchoesStatusType => Major > 1 || (Major == 1 && Minor >= 2);
    }

    public sealed record EcHidReply(ushort Command, ushort Status, byte[] Data)
    {
        public bool Done => Status == 0xE000;
        public bool Final => Done || Status == 0xE001;
        public byte Byte(int offset) => offset < Data.Length ? Data[offset] : (byte)0;
        public ushort Word(int offset) => offset + 1 < Data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(offset)) : (ushort)0;
    }

    public sealed record FanChannel(FanId Id, string Name, FanChip Chip, SensorId RpmSensor, int? GroupBit = null, byte? SpeedId = null)
    {
        public bool Controllable => GroupBit.HasValue && SpeedId.HasValue;

        public static readonly FanChannel Cpu = new(FanId.Cpu, "CPU", FanChip.Cpu, SensorId.CpuFanSpeed, 0, 1);
        public static readonly FanChannel Gpu = new(FanId.Gpu, "GPU", FanChip.Gpu, SensorId.GpuFanSpeed, 3, 4);
        public static readonly FanChannel Gpu2 = new(FanId.Gpu2, "GPU 2", FanChip.Gpu, SensorId.Gpu2FanSpeed, 4, 5);
        public static readonly FanChannel System = new(FanId.System, "System", FanChip.System, SensorId.SystemFanSpeed, 1, 2);
        public static readonly FanChannel System2 = new(FanId.System2, "System 2", FanChip.System, SensorId.System2FanSpeed, 2, 3);

        public static readonly IReadOnlyList<FanChannel> Known = new[] { Cpu, Gpu, Gpu2, System, System2 };

        public static FanChannel Get(FanId id) => Known.First(f => f.Id == id);
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
        public const uint DustDefenderQuery = 263u;
        public const uint DustDefenderStatusQuery = 775u;
        public const ulong DustDefenderStartInput = 263uL;
        public const byte DustDefenderBusy = 229;
        public const uint UsbChargingQuery = 4u;
        public const int DefaultUsbChargingFloor = 30;
        public const uint BatteryStatusQuery = 2u;

        public static readonly int[] UsbChargingFloors = { 10, 20, 30 };
        private static readonly string[] FanTableModels = { "AN515-46", "AN515-47", "AN515-58", "AN517-42", "AN517-43", "AN517-55" };

        public static byte Status(ulong output) => (byte)(output & 0xFF);
        public static bool IsOk(ulong output) => Status(output) == 0;

        public static uint SensorReadInput(SensorId id) => 1u | ((uint)id << 8);
        public static int SensorValue(ulong output) => (int)((output >> 8) & 0xFFFF);

        public static IReadOnlyList<SensorId> DecodeSensorMask(ulong output)
        {
            ulong mask = output >> 24;
            var list = new List<SensorId>();
            foreach (SensorId id in Enum.GetValues<SensorId>())
            {
                if ((mask & (1UL << ((int)id - 1))) != 0)
                    list.Add(id);
            }
            return list;
        }

        public static ulong FanBehaviorInput(IEnumerable<(FanChannel Fan, FanBehavior Behavior)> settings)
        {
            ulong mask = 0UL;
            ulong val = 0UL;
            foreach (var s in settings)
            {
                int bit = s.Fan.GroupBit ?? 0;
                mask |= 1UL << bit;
                val |= (ulong)s.Behavior << (16 + 2 * bit);
            }
            return mask | val;
        }

        public static ulong FanSpeedInput(FanChannel fan, int percent)
        {
            byte speedId = fan.SpeedId ?? 1;
            return (ulong)(speedId | ((long)Math.Clamp(percent, 0, 100) << 8));
        }

        public static int NearestFanSpeed(int percent)
        {
            return (int)Math.Round(Math.Clamp(percent, 0, 100) / 10.0, MidpointRounding.AwayFromZero) * 10;
        }

        public static ulong FanTableInput(FanTable table) => (ulong)table;

        public static FanTable? FanTableValue(ulong output)
        {
            byte val = (byte)(output >> 8);
            return Enum.IsDefined(typeof(FanTable), val) ? (FanTable)val : null;
        }

        public static bool UsesFanTable(string? model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            return FanTableModels.Any(m => model.Contains(m, StringComparison.OrdinalIgnoreCase));
        }

        public static ulong CoolBoostSetInput(bool on) => 7UL | ((ulong)(on ? 1 : 0) << 16);
        public static bool CoolBoostValue(ulong output) => ((output >> 8) & 0xFF) == 1;

        public static bool DustDefenderValue(ulong output) => ((output >> 24) & 0xFF) == 1;

        public static ulong UsbChargingInput(bool on, int floor)
        {
            byte flag = (byte)(on ? 15 : 31);
            return 4UL | ((ulong)flag << 8) | ((ulong)floor << 16);
        }

        public static UsbChargingState UsbChargingValue(ulong output)
        {
            bool enabled = ((output >> 8) & 0xFF) == 15;
            int floor = (int)((output >> 16) & 0xFF);
            return new UsbChargingState(enabled, UsbChargingFloors.Contains(floor) ? floor : null);
        }

        public static uint MiscGetInput(MiscSetting setting) => (uint)setting;
        public static ulong MiscSetInput(MiscSetting setting, byte value) => (ulong)setting | ((ulong)value << 8);
        public static byte MiscValue(ulong output) => (byte)((output >> 8) & 0xFF);

        public static IReadOnlyList<OperatingMode> DecodeOperatingModeMask(ulong output)
        {
            ulong mask = (output >> 8) & 0xFFFF;
            var list = new List<OperatingMode>();
            foreach (OperatingMode m in Enum.GetValues<OperatingMode>())
            {
                if ((mask & (1UL << (int)m)) != 0)
                    list.Add(m);
            }
            return list;
        }

        public static uint BacklightTimeoutQuery(byte hotkey) => 1u | ((uint)hotkey << 8) | 0x80000u;
        public static ulong BacklightTimeoutInput(byte hotkey, int brightnessPercent, int timeoutSeconds)
        {
            byte b = (byte)Math.Clamp(brightnessPercent, 0, 100);
            byte t = (byte)Math.Clamp(timeoutSeconds, 0, 255);
            return 2UL | ((ulong)hotkey << 8) | 0x80000UL | ((ulong)b << 32) | ((ulong)t << 40);
        }
        public static int BacklightBrightnessValue(ulong output) => (int)((output >> 32) & 0xFF);
        public static int BacklightTimeoutValue(ulong output) => (int)((output >> 40) & 0xFF);
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
                        IntPtr pBqi = Marshal.AllocHGlobal(bqiSize);
                        IntPtr pBi = Marshal.AllocHGlobal(biSize);
                        try
                        {
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
                            Marshal.FreeHGlobal(pBqi);
                            Marshal.FreeHGlobal(pBi);
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
            IntPtr pBqi = Marshal.AllocHGlobal(bqiSize);
            IntPtr pOut = Marshal.AllocHGlobal(256);
            try
            {
                Marshal.StructureToPtr(bqi, pBqi, false);
                if (DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, pBqi, (uint)bqiSize, pOut, 256, out uint returned, IntPtr.Zero) && returned > 0)
                {
                    return Marshal.PtrToStringUni(pOut)?.Trim('\0', ' ');
                }
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(pBqi);
                Marshal.FreeHGlobal(pOut);
            }
            return null;
        }

        private static List<string> GetBatteryDevicePaths()
        {
            var paths = new List<string>();
            Guid guid = GUID_DEVICE_BATTERY;
            IntPtr hDevInfo = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
            if (hDevInfo == (IntPtr)(-1)) return paths;

            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                uint index = 0;
                while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref guid, index++, ref did))
                {
                    SetupDiGetDeviceInterfaceDetail(hDevInfo, ref did, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
                    if (requiredSize == 0) continue;

                    IntPtr pDetail = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        Marshal.WriteInt32(pDetail, IntPtr.Size == 8 ? 8 : 6); // cbSize
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
                if (size == 0) return Empty;

                byte[] buf = new byte[size];
                GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    if (GetSystemFirmwareTable(rsmb, 0, pin.AddrOfPinnedObject(), size) != size)
                        return Empty;
                }
                finally { pin.Free(); }

                int len = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)), (uint)(buf.Length - 8));
                return Parse(buf.AsSpan(8, len));
            }
            catch { return Empty; }
        }

        internal static AcerSmbios Parse(ReadOnlySpan<byte> table)
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

    [SupportedOSPlatform("windows")]
    public static class HardwareIdentity
    {
        public static string ReadModel()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                foreach (var obj in searcher.Get())
                {
                    string mfg = obj["Manufacturer"]?.ToString() ?? "";
                    string model = obj["Model"]?.ToString() ?? "";
                    return $"{mfg} {model}".Trim();
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
                foreach (var obj in searcher.Get())
                {
                    return obj["SMBIOSBIOSVersion"]?.ToString() ?? "Unknown";
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
                foreach (var obj in searcher.Get())
                {
                    string sn = obj["SerialNumber"]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(sn)) return sn;
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

        public bool IsOpen => _deviceHandle != null && !_deviceHandle.IsInvalid;
        public EcHidVersion? Version { get; private set; }

        public static EcHidDevice? TryOpen()
        {
            try
            {
                var paths = FindDevicePaths();
                foreach (var path in paths)
                {
                    var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                    if (!handle.IsInvalid)
                    {
                        Mutex? mtx = null;
                        try { mtx = new Mutex(false, MutexName); } catch { }
                        var dev = new EcHidDevice { _deviceHandle = handle, _globalLock = mtx };
                        if (dev.ReadVersion() != null) return dev;
                        dev.Dispose();
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
            if (rep != null && rep.Done && rep.Command == (ushort)EcHidCommand.Mode)
                return rep.Byte(4);
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
            if (rep != null && rep.Done && rep.Command == (ushort)EcHidCommand.Device && rep.Byte(5) == 2)
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

        public EcHidReply? Exchange(byte[] request)
        {
            if (_deviceHandle == null || _deviceHandle.IsInvalid) return null;
            bool lockTaken = false;
            try
            {
                if (_globalLock != null) lockTaken = _globalLock.WaitOne(500);

                if (!SetFeature(_deviceHandle, request)) return null;
                Thread.Sleep(20);

                byte[] replyBuf = new byte[ReportLength];
                replyBuf[0] = ReportId;
                if (!GetFeature(_deviceHandle, replyBuf)) return null;

                if (replyBuf.Length >= 6 && replyBuf[0] == ReportId)
                {
                    byte[] data = replyBuf.AsSpan(1).ToArray();
                    return new EcHidReply(BinaryPrimitives.ReadUInt16LittleEndian(data), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)), data);
                }
            }
            catch { }
            finally
            {
                if (lockTaken) _globalLock?.ReleaseMutex();
            }
            return null;
        }

        private static byte[] BuildRequest(EcHidCommand cmd, byte func, params byte[] data)
        {
            byte[] buf = new byte[ReportLength];
            buf[0] = ReportId;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(1), RequestHeader);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(3), (ushort)cmd);
            buf[5] = func;
            if (data != null && data.Length > 0)
                data.CopyTo(buf.AsSpan(6));
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
            var matches = new List<string>();
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (hDevInfo == (IntPtr)(-1)) return matches;

            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                uint index = 0;
                while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, index++, ref did))
                {
                    SetupDiGetDeviceInterfaceDetail(hDevInfo, ref did, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;

                    IntPtr pDetail = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(pDetail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref did, pDetail, reqSize, out _, IntPtr.Zero))
                        {
                            IntPtr pPath = (IntPtr)((long)pDetail + 4);
                            string? path = Marshal.PtrToStringAuto(pPath);
                            if (!string.IsNullOrEmpty(path))
                            {
                                using var dev = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                                if (!dev.IsInvalid)
                                {
                                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                                    if (HidD_GetAttributes(dev, ref attr))
                                    {
                                        if (attr.VendorID == VendorId && attr.ProductID == ProductId)
                                        {
                                            bool matchesUsage = true;
                                            if (HidD_GetPreparsedData(dev, out IntPtr pData))
                                            {
                                                try
                                                {
                                                    var caps = new HIDP_CAPS();
                                                    if (HidP_GetCaps(pData, ref caps) >= 0)
                                                    {
                                                        matchesUsage = (caps.UsagePage == UsagePage && caps.Usage == Usage);
                                                    }
                                                }
                                                finally
                                                {
                                                    HidD_FreePreparsedData(pData);
                                                }
                                            }

                                            if (matchesUsage)
                                                matches.Add(path);
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
            return matches;
        }

        private static bool SetFeature(SafeFileHandle h, byte[] buf) => HidD_SetFeature(h, buf, buf.Length);
        private static bool GetFeature(SafeFileHandle h, byte[] buf) => HidD_GetFeature(h, buf, buf.Length);

        public void Dispose()
        {
            try { _deviceHandle?.Dispose(); } catch { }
            _deviceHandle = null;
            try { _globalLock?.Dispose(); } catch { }
            _globalLock = null;
        }
    }

    #endregion

    #region GPU Power State Awareness (D3Cold / Sleep)

    [SupportedOSPlatform("windows")]
    public static class GpuPowerMonitor
    {
        private const uint CR_SUCCESS = 0;
        private static readonly Guid GUID_DEVCLASS_DISPLAY = new("4d36e968-e325-11ce-bfc1-08002be10318");

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        private static readonly DEVPROPKEY PKEY_Device_PowerData = new()
        {
            fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"),
            pid = 15
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct CM_POWER_DATA
        {
            public uint PD_Size;
            public int PD_MostRecentPowerState; // 1 = D0, 2 = D1, 3 = D2, 4 = D3
            public uint PD_Capabilities;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public int[] PD_PowerStateMapping;
            public int PD_D1Latency;
            public int PD_D2Latency;
            public int PD_D3Latency;
        }

        [DllImport("cfgmgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID_List_Size(out uint pulLen, string? pszFilter, uint ulFlags);

        [DllImport("cfgmgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID_List(string? pszFilter, IntPtr Buffer, uint BufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint CM_Locate_DevNode(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern uint CM_Get_DevNode_Property(
            uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType,
            IntPtr PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

        public static bool IsGpuAsleep()
        {
            try
            {
                string filter = GUID_DEVCLASS_DISPLAY.ToString("B");
                if (CM_Get_Device_ID_List_Size(out uint len, filter, 0x300) != CR_SUCCESS || len == 0)
                    return false;

                IntPtr pBuf = Marshal.AllocHGlobal((int)len * 2);
                try
                {
                    if (CM_Get_Device_ID_List(filter, pBuf, len, 0x300) != CR_SUCCESS)
                        return false;

                    string raw = Marshal.PtrToStringUni(pBuf, (int)len);
                    var ids = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);

                    foreach (var id in ids)
                    {
                        if (id.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ||
                            (id.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) && !id.Contains("DEV_1638", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (CM_Locate_DevNode(out uint devInst, id, 0) == CR_SUCCESS)
                            {
                                int size = Marshal.SizeOf<CM_POWER_DATA>();
                                IntPtr pData = Marshal.AllocHGlobal(size);
                                try
                                {
                                    uint bufSize = (uint)size;
                                    var propKey = PKEY_Device_PowerData;
                                    if (CM_Get_DevNode_Property(devInst, ref propKey, out _, pData, ref bufSize, 0) == CR_SUCCESS)
                                    {
                                        var pd = Marshal.PtrToStructure<CM_POWER_DATA>(pData);
                                        return pd.PD_MostRecentPowerState >= 2;
                                    }
                                }
                                finally { Marshal.FreeHGlobal(pData); }
                            }
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(pBuf); }
            }
            catch { }
            return false;
        }
    }

    #endregion
}
