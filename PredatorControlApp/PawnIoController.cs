using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PredatorControlApp
{
    public static class MsrRegisters
    {
        public const uint MSR_RAPL_POWER_UNIT = PawnIoController.MSR_RAPL_POWER_UNIT;
        public const uint MSR_PKG_POWER_LIMIT = PawnIoController.MSR_PKG_POWER_LIMIT;
        public const uint MSR_PKG_POWER_INFO = PawnIoController.MSR_PKG_POWER_INFO;
        public const uint MSR_TEMPERATURE_TARGET = 0x1A2;
        public const uint MSR_TURBO_RATIO_LIMIT = 0x1AD;

        public const ulong PL1_ENABLE_BIT = 1UL << 15;
        public const ulong PL1_CLAMP_BIT = 1UL << 16;
        public const ulong PL2_ENABLE_BIT = 1UL << 47;
        public const ulong PL2_CLAMP_BIT = 1UL << 48;
        public const ulong MSR_PKG_POWER_LIMIT_LOCK_BIT = 1UL << 63;
        public const ulong MMIO_PKG_RAPL_LIMIT_ADDRESS = PawnIoController.MMIO_PKG_RAPL_LIMIT_ADDRESS;
        public const ulong MMIO_PKG_RAPL_PL1_ADDRESS = PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS;
        public const ulong MMIO_PKG_RAPL_PL2_ADDRESS = PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS;
    }

    #region Enums & Result Models

    /// <summary>
    /// Status code resulting from power limit query or application operations.
    /// </summary>
    public enum PowerLimitApplyStatus
    {
        Success,
        DriverNotInstalled,
        AccessDenied,
        BiosLocked,
        UnsupportedHardware,
        HardwareError
    }

    /// <summary>
    /// Structured result returned from power limit configuration operations.
    /// </summary>
    public sealed record PowerLimitApplyResult
    {
        public PowerLimitApplyStatus Status { get; init; }
        public bool IsSuccess => Status == PowerLimitApplyStatus.Success;
        public string Message { get; init; } = string.Empty;
        public int AppliedPl1Watts { get; init; }
        public int AppliedPl2Watts { get; init; }
        public bool MmioMirrored { get; init; }
        public ulong RawMsrWritten { get; init; }
        public ulong RawMmioWritten { get; init; }

        public static PowerLimitApplyResult Success(
            int pl1Watts,
            int pl2Watts,
            bool mmioMirrored = false,
            ulong rawMsr = 0,
            ulong rawMmio = 0,
            string message = "Power limits applied successfully.") =>
            new()
            {
                Status = PowerLimitApplyStatus.Success,
                AppliedPl1Watts = pl1Watts,
                AppliedPl2Watts = pl2Watts,
                MmioMirrored = mmioMirrored,
                RawMsrWritten = rawMsr,
                RawMmioWritten = rawMmio,
                Message = message
            };

        public static PowerLimitApplyResult DriverNotInstalled(string message = "PawnIO kernel driver is not installed or service is not running.") =>
            new() { Status = PowerLimitApplyStatus.DriverNotInstalled, Message = message };

        public static PowerLimitApplyResult AccessDenied(string message = "Access denied opening PawnIO device handle. Administrative privileges required.") =>
            new() { Status = PowerLimitApplyStatus.AccessDenied, Message = message };

        public static PowerLimitApplyResult BiosLocked(string message = "MSR_PKG_POWER_LIMIT lock bit (bit 63) is set by UEFI/BIOS.") =>
            new() { Status = PowerLimitApplyStatus.BiosLocked, Message = message };

        public static PowerLimitApplyResult UnsupportedHardware(string message = "Target processor does not support Intel RAPL MSRs or valid power units.") =>
            new() { Status = PowerLimitApplyStatus.UnsupportedHardware, Message = message };

        public static PowerLimitApplyResult HardwareError(string message = "Hardware I/O error occurred during MSR/MMIO operation.") =>
            new() { Status = PowerLimitApplyStatus.HardwareError, Message = message };

        public override string ToString() =>
            $"[PowerLimitApplyResult: {Status}] {Message} (PL1={AppliedPl1Watts}W, PL2={AppliedPl2Watts}W, MMIO={MmioMirrored})";
    }

    /// <summary>
    /// Detailed hardware scan result for PawnIO kernel driver and service detection.
    /// </summary>
    public sealed record PawnIoScanResult
    {
        public bool IsInstalled { get; init; }
        public bool IsRunning { get; init; }
        public bool IsAccessible { get; init; }
        public bool RequiresElevation { get; init; }
        public string ServicePath { get; init; } = string.Empty;
        private int _errorCode;
        public int ErrorCode { get => _errorCode; init => _errorCode = value; }
        public int LastWin32Error { get => _errorCode; init => _errorCode = value; }
        public string DiagnosticMessage { get; init; } = string.Empty;
    }

    /// <summary>
    /// Comprehensive scanned baseline capabilities of the CPU package power limit system.
    /// </summary>
    public sealed record DevicePowerCapabilities
    {
        public bool IsSupported { get; init; }
        public bool IsLocked { get; init; }
        public bool IsMmioAccessible { get; init; }
        public uint UnitsPerWatt { get; init; }
        public int ThermalSpecPowerWatts { get; init; }
        public int MinimumPowerWatts { get; init; }
        public int MaximumPowerWatts { get; init; }
        public int CurrentPl1Watts { get; init; }
        public int CurrentPl2Watts { get; init; }
        public int ScannedMinPowerWatts { get; init; }
        public int ScannedMaxPl1Watts { get; init; }
        public int ScannedMaxPl2Watts { get; init; }
        public ulong RawPowerUnit { get; init; }
        public ulong RawPowerInfo { get; init; }
        public ulong RawPowerLimit { get; init; }
        public ulong RawMmioPowerLimit { get; init; }
                public PowerLimitApplyStatus BaselineStatus { get; init; } = PowerLimitApplyStatus.Success;
        public PowerLimitApplyStatus Status { get => BaselineStatus; init => BaselineStatus = value; }
        public string Diagnostics { get; init; } = string.Empty;
        public string StatusMessage { get => Diagnostics; init => Diagnostics = value; }
        public BiosPowerScanResult? BiosPowerScan { get; init; }

        public static DevicePowerCapabilities Unsupported(PowerLimitApplyStatus status, string message) =>
            new()
            {
                IsSupported = false,
                BaselineStatus = status,
                Diagnostics = message
            };
    }

    #endregion

    #region Backend Abstraction Interface

    /// <summary>
    /// Hardware access abstraction interface for Ring-0 MSR and MMIO operations.
    /// Enables 100% zero-driver testability and mockable CI execution.
    /// </summary>
    public interface IPawnIoBackend : IDisposable
    {
        ulong MchbarAddress => 0;
        bool IsDriverLoaded { get; }
        bool IsOpen => IsDriverLoaded;
        bool IsDriverInstalled => IsDriverLoaded;
        string DevicePath => @"\\.\PawnIO";

        PowerLimitApplyStatus Initialize() => IsDriverLoaded ? PowerLimitApplyStatus.Success : PowerLimitApplyStatus.DriverNotInstalled;
        bool ReadMsr(uint msrIndex, out ulong value);
        bool WriteMsr(uint msrIndex, ulong value);
        bool ReadMmio32(ulong physicalAddress, out uint value) { value = 0; return false; }
        bool WriteMmio32(ulong physicalAddress, uint value) => false;
        bool ReadMmio64(ulong physicalAddress, out ulong value) { value = 0; return false; }
        bool WriteMmio64(ulong physicalAddress, ulong value) => false;
        PawnIoScanResult ScanDevice() => new()
        {
            IsInstalled = IsDriverInstalled,
            IsRunning = IsDriverLoaded,
            IsAccessible = IsOpen,
            RequiresElevation = false,
            ServicePath = string.Empty,
            ErrorCode = 0,
            DiagnosticMessage = IsOpen ? "Driver active and accessible." : "Driver not loaded."
        };
        void Close() => Dispose();
    }

    #endregion

    #region Mock Backend for Zero-Driver Testability

    /// <summary>
    /// High-fidelity in-memory mock backend for zero-driver testability and automated CI suites.
    /// Simulates Intel RAPL MSRs, MMIO registers, lock bits, access denials, and hardware errors.
    /// </summary>
    public sealed class MockPawnIoBackend : IPawnIoBackend
    {
        private readonly ConcurrentDictionary<uint, ulong> _msrs = new();
        private readonly ConcurrentDictionary<ulong, ulong> _mmio = new();
        private readonly List<(string Op, ulong Target, ulong Value)> _history = new();
        private readonly object _historyLock = new();
        private bool _isOpen;

        public bool IsOpen => _isOpen && IsDriverInstalled && !SimulateAccessDenied && !SimulateUnsupportedHardware && !SimulateHardwareError;
        public ulong MchbarAddress { get; set; } = 0;
        public bool IsDriverLoaded { get; set; } = true;
        public bool IsDriverInstalled { get => IsDriverLoaded; set => IsDriverLoaded = value; }
        public string DevicePath => @"\\.\MockPawnIO";

        // Simulation toggles for failure mode testing
        public bool SimulateAccessDenied { get; set; }
        public bool SimulateHardwareError { get; set; }
        public bool SimulateUnsupportedHardware { get; set; }
        public bool SimulateBiosLocked { get; set; }
        public bool IsMmioAccessible { get; set; } = true;

        public PawnIoScanResult ScanDevice() => new()
        {
            IsInstalled = IsDriverInstalled,
            IsRunning = IsDriverLoaded && !SimulateHardwareError,
            IsAccessible = IsOpen,
            RequiresElevation = SimulateAccessDenied,
            ServicePath = @"C:\Windows\System32\drivers\pawnio.sys",
            LastWin32Error = SimulateAccessDenied ? 5 : 0,
            DiagnosticMessage = IsOpen
                ? "Mock PawnIO active and accessible."
                : (SimulateAccessDenied ? "Mock PawnIO requires administrator elevation." : "Mock PawnIO not installed.")
        };

        public IReadOnlyList<(string Op, ulong Target, ulong Value)> History
        {
            get
            {
                lock (_historyLock)
                {
                    return _history.ToArray();
                }
            }
        }

        public MockPawnIoBackend(bool isLoaded = true)
        {
            _isOpen = isLoaded;
            IsDriverLoaded = isLoaded;
            ConfigureDefaultIntelBaseline();
        }

        /// <summary>
        /// Configures a realistic Intel CPU RAPL baseline environment.
        /// </summary>
        public void ConfigureIntelBaseline(
            int tdpWatts = 55,
            int minWatts = 0,
            int maxWatts = 157,
            int pl1Watts = 55,
            int pl2Watts = 157,
            uint unitsPerWatt = 8,
            bool locked = false,
            bool mirrorMmio = true)
        {
            // MSR_RAPL_POWER_UNIT (0x606): UnitsPerWatt = 1 << (raw & 0xF)
            // If unitsPerWatt == 8, exponent is 3 (1 << 3 = 8). Default Intel Power Unit: 0x000A0E03
            int powerUnitExponent = (int)Math.Round(Math.Log2(unitsPerWatt));
            ulong powerUnitRaw = 0x000A0E00UL | ((ulong)powerUnitExponent & 0x0FUL);
            SetMsr(PawnIoController.MSR_RAPL_POWER_UNIT, powerUnitRaw);

            // MSR_PKG_POWER_INFO (0x614): ThermalSpec (14:0), MinPower (30:16), MaxPower (46:32)
            ulong rawThermalSpec = ((ulong)Math.Round((double)tdpWatts * unitsPerWatt)) & 0x7FFFUL;
            ulong rawMin = ((ulong)Math.Round((double)minWatts * unitsPerWatt)) & 0x7FFFUL;
            ulong rawMax = ((ulong)Math.Round((double)maxWatts * unitsPerWatt)) & 0x7FFFUL;
            ulong powerInfoRaw = rawThermalSpec | (rawMin << 16) | (rawMax << 32) | (0x28UL << 48);
            SetMsr(PawnIoController.MSR_PKG_POWER_INFO, powerInfoRaw);

            // MSR_PKG_POWER_LIMIT (0x610): PL1 (14:0), Enable(15), Clamp(16), Tau1(23:17), PL2(46:32), Enable(47), Clamp(48), Tau2(55:49), Lock(63)
            ulong rawPl1 = ((ulong)Math.Round((double)pl1Watts * unitsPerWatt)) & 0x7FFFUL;
            ulong rawPl2 = ((ulong)Math.Round((double)pl2Watts * unitsPerWatt)) & 0x7FFFUL;
            ulong enableAndClamp = (1UL << 15) | (1UL << 16) | (1UL << 47) | (1UL << 48);
            ulong tau1 = 0x28UL << 17; // ~28s default Intel time window
            ulong tau2 = 0x10UL << 49; // ~2.44ms default Intel time window
            ulong lockBit = (locked || SimulateBiosLocked) ? (1UL << 63) : 0UL;

            ulong powerLimitRaw = rawPl1 | enableAndClamp | tau1 | (rawPl2 << 32) | tau2 | lockBit;
            SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT, powerLimitRaw);

            if (mirrorMmio && IsMmioAccessible)
            {
                SetMmio(PawnIoController.MMIO_PKG_RAPL_LIMIT_ADDRESS, powerLimitRaw);
            }
        }

        private void ConfigureDefaultIntelBaseline()
        {
            ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 0,
                maxWatts: 157,
                pl1Watts: 55,
                pl2Watts: 157,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true);
        }

        public void SetMsr(uint msr, ulong value) => _msrs[msr] = value;
        public ulong GetMsr(uint msr) => _msrs.TryGetValue(msr, out var val) ? val : 0UL;

        public void SetMmio(ulong physicalAddress, ulong value)
        {
            _mmio[physicalAddress] = value;
            if ((physicalAddress & 4) == 0)
            {
                _mmio[physicalAddress + 4] = (uint)(value >> 32);
            }
        }

        public ulong GetMmio(ulong physicalAddress)
        {
            if (_mmio.TryGetValue(physicalAddress, out var val))
            {
                if ((val & 0xFFFFFFFF00000000UL) == 0 && _mmio.TryGetValue(physicalAddress + 4, out var high) && high != 0)
                {
                    return (val & 0xFFFFFFFFUL) | ((high & 0xFFFFFFFFUL) << 32);
                }
                return val;
            }
            return 0UL;
        }

        public PowerLimitApplyStatus Initialize()
        {
            if (!IsDriverInstalled)
                return PowerLimitApplyStatus.DriverNotInstalled;

            if (SimulateAccessDenied)
                return PowerLimitApplyStatus.AccessDenied;

            if (SimulateHardwareError)
                return PowerLimitApplyStatus.HardwareError;

            if (SimulateUnsupportedHardware)
            {
                _msrs[PawnIoController.MSR_RAPL_POWER_UNIT] = 0;
                _msrs[PawnIoController.MSR_PKG_POWER_INFO] = 0;
                _msrs[PawnIoController.MSR_PKG_POWER_LIMIT] = 0;
                return PowerLimitApplyStatus.UnsupportedHardware;
            }

            _isOpen = true;
            return PowerLimitApplyStatus.Success;
        }

        public bool ReadMsr(uint msrIndex, out ulong value)
        {
            if (SimulateHardwareError || !_isOpen)
            {
                value = 0;
                return false;
            }

            if (SimulateUnsupportedHardware)
            {
                value = 0;
                return true;
            }

            RecordHistory("ReadMsr", msrIndex, 0);

            if (_msrs.TryGetValue(msrIndex, out value))
            {
                if (msrIndex == PawnIoController.MSR_PKG_POWER_LIMIT && SimulateBiosLocked)
                {
                    value |= (1UL << 63);
                }
                return true;
            }

            value = 0;
            return false;
        }

        public bool WriteMsr(uint msrIndex, ulong value)
        {
            if (SimulateHardwareError || !_isOpen)
                return false;

            // Enforce BIOS Lock simulation on write
            if (msrIndex == PawnIoController.MSR_PKG_POWER_LIMIT)
            {
                if (SimulateBiosLocked || (_msrs.TryGetValue(msrIndex, out var current) && (current & (1UL << 63)) != 0))
                {
                    RecordHistory("WriteMsr_BlockedBiosLock", msrIndex, value);
                    return false;
                }
            }

            _msrs[msrIndex] = value;
            RecordHistory("WriteMsr", msrIndex, value);
            return true;
        }

        public bool ReadMmio32(ulong physicalAddress, out uint value)
        {
            if (SimulateHardwareError || !_isOpen || !IsMmioAccessible)
            {
                value = 0;
                return false;
            }

            RecordHistory("ReadMmio32", physicalAddress, 0);

            if ((physicalAddress & 4) != 0)
            {
                if (_mmio.TryGetValue(physicalAddress - 4, out var base64))
                {
                    value = (uint)(base64 >> 32);
                    return true;
                }
                if (_mmio.TryGetValue(physicalAddress, out var exact))
                {
                    value = (uint)(exact & 0xFFFFFFFFU);
                    return true;
                }
            }
            else
            {
                if (_mmio.TryGetValue(physicalAddress, out var val64))
                {
                    value = (uint)(val64 & 0xFFFFFFFFU);
                    return true;
                }
            }

            value = 0;
            return false;
        }

        public bool WriteMmio32(ulong physicalAddress, uint value)
        {
            if (SimulateHardwareError || !_isOpen || !IsMmioAccessible)
                return false;

            if ((physicalAddress & 4) != 0)
            {
                ulong baseAddress = physicalAddress - 4;
                _mmio.AddOrUpdate(baseAddress, (ulong)value << 32, (_, current) => (current & 0x00000000FFFFFFFFUL) | ((ulong)value << 32));
                _mmio[physicalAddress] = value;
            }
            else
            {
                _mmio.AddOrUpdate(physicalAddress, (ulong)value, (_, current) => (current & 0xFFFFFFFF00000000UL) | (ulong)value);
            }

            RecordHistory("WriteMmio32", physicalAddress, value);
            return true;
        }

        public bool ReadMmio64(ulong physicalAddress, out ulong value)
        {
            if (SimulateHardwareError || !_isOpen || !IsMmioAccessible)
            {
                value = 0;
                return false;
            }

            RecordHistory("ReadMmio64", physicalAddress, 0);
            if (_mmio.TryGetValue(physicalAddress, out value))
            {
                return true;
            }
            if (_mmio.TryGetValue(physicalAddress, out var low) && _mmio.TryGetValue(physicalAddress + 4, out var high))
            {
                value = (low & 0xFFFFFFFFUL) | ((high & 0xFFFFFFFFUL) << 32);
                return true;
            }

            value = 0;
            return false;
        }

        public bool WriteMmio64(ulong physicalAddress, ulong value)
        {
            if (SimulateHardwareError || !_isOpen || !IsMmioAccessible)
                return false;

            _mmio[physicalAddress] = value;
            if ((physicalAddress & 4) == 0)
            {
                _mmio[physicalAddress + 4] = (uint)(value >> 32);
            }
            RecordHistory("WriteMmio64", physicalAddress, value);
            return true;
        }

        public void Close()
        {
            _isOpen = false;
        }

        public void Dispose()
        {
            Close();
            _msrs.Clear();
            _mmio.Clear();
        }

        private void RecordHistory(string op, ulong target, ulong value)
        {
            lock (_historyLock)
            {
                _history.Add((op, target, value));
            }
        }
    }

    #endregion

    #region Native Win32 / PawnIO Backend Implementation

    /// <summary>
    /// Native Ring-0 hardware backend interfacing with the genuine \\.\PawnIO kernel driver.
    /// Implements standard PawnIO protocol with bytecode module loading (IOCTL 0xA1B22084),
    /// bytecode function execution (IOCTL 0xA1B22104), embedded IntelMSR.bin & IntelMCHBAR.bin,
    /// global inter-process mutex synchronization, and comprehensive privilege diagnostics.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class NativePawnIoBackend : IPawnIoBackend
    {
        #region Constants & IOCTL Definitions

        public const string PrimaryDevicePath = @"\\.\PawnIO";
        public const string LowercaseDevicePath = @"\\.\pawnio";
        public const string SecondaryDevicePath = @"\\.\PawnIo";
        public const string GlobalDevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
        public const string GlobalLowercaseDevicePath = @"\\?\GLOBALROOT\Device\pawnio";
        public const string GlobalDotDevicePath = @"\\.\GLOBALROOT\Device\PawnIO";
        public const string GlobalDotLowercaseDevicePath = @"\\.\GLOBALROOT\Device\pawnio";

        public static readonly string[] CandidateDevicePaths = new[]
        {
            PrimaryDevicePath,
            LowercaseDevicePath,
            SecondaryDevicePath,
            GlobalDevicePath,
            GlobalLowercaseDevicePath,
            GlobalDotDevicePath,
            GlobalDotLowercaseDevicePath
        };

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        private const int ERROR_SUCCESS = 0;
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_INVALID_HANDLE = 6;
        private const int ERROR_DEV_NOT_EXIST = 55;

        // Genuine PawnIO Device Type and IOCTL codes
        private const uint DEV_TYPE = 41394u << 16;                                      // 0xA1B20000
        public const uint IOCTL_PAWNIO_LOAD_BINARY = DEV_TYPE | (0x821 << 2);           // 0xA1B22084 (2712805508)
        public const uint IOCTL_PAWNIO_EXECUTE = DEV_TYPE | (0x841 << 2);               // 0xA1B22104 (2712805636)

        // Function identifiers exported by IntelMSR.bin and IntelMCHBAR.bin
        private const string FN_READ_MSR = "ioctl_read_msr";
        private const string FN_WRITE_MSR = "ioctl_write_msr";
        private const string FN_GET_MCHBAR = "ioctl_get_mchbar_addr";
        private const string FN_READ_DWORD = "ioctl_read_dword";
        private const string FN_READ_QWORD = "ioctl_read_qword";

        // Global synchronization mutexes (identical to UXTU and PreySense)
        private const string MSR_MUTEX_NAME = @"Global\Access_MSR";
        private const string PCI_MUTEX_NAME = @"Global\Access_PCI";

        #endregion

        #region P/Invoke Signatures

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            [In] byte[]? lpInBuffer,
            uint nInBufferSize,
            [Out] byte[]? lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            [In] byte[]? lpInBuffer,
            uint nInBufferSize,
            [Out] byte[]? lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const uint SERVICE_START = 0x0010;

        private const uint SERVICE_STOPPED = 1;
        private const uint SERVICE_START_PENDING = 2;
        private const uint SERVICE_STOP_PENDING = 3;
        private const uint SERVICE_RUNNING = 4;
        private const uint SERVICE_CONTINUE_PENDING = 5;
        private const uint SERVICE_PAUSE_PENDING = 6;
        private const uint SERVICE_PAUSED = 7;

        #endregion

        private SafeFileHandle? _deviceHandle;
        private SafeFileHandle? _mchbarDeviceHandle;
        private string _activeDevicePath = string.Empty;
        private readonly object _ioLock = new();
        private bool _isModuleLoaded;
        private bool _isMchbarLoaded;
        private ulong _cachedMchbarAddress;
        private bool _isDisposed;

        private Mutex? _cachedMsrMutex;
        private Mutex? _cachedPciMutex;
        private readonly object _mutexLock = new();

        private Mutex? GetOrCreateMsrMutex()
        {
            if (_cachedMsrMutex != null) return _cachedMsrMutex;
            lock (_mutexLock)
            {
                if (_cachedMsrMutex != null) return _cachedMsrMutex;
                try
                {
                    if (!Mutex.TryOpenExisting(MSR_MUTEX_NAME, out _cachedMsrMutex))
                    {
                        try { _cachedMsrMutex = new Mutex(false, MSR_MUTEX_NAME); } catch { }
                    }
                }
                catch { }
                return _cachedMsrMutex;
            }
        }

        private Mutex? GetOrCreatePciMutex()
        {
            if (_cachedPciMutex != null) return _cachedPciMutex;
            lock (_mutexLock)
            {
                if (_cachedPciMutex != null) return _cachedPciMutex;
                try
                {
                    if (!Mutex.TryOpenExisting(PCI_MUTEX_NAME, out _cachedPciMutex))
                    {
                        try { _cachedPciMutex = new Mutex(false, PCI_MUTEX_NAME); } catch { }
                    }
                }
                catch { }
                return _cachedPciMutex;
            }
        }

        public bool IsOpen => _deviceHandle != null && !_deviceHandle.IsInvalid && !_deviceHandle.IsClosed && _isModuleLoaded;
        public ulong MchbarAddress => _cachedMchbarAddress;
        public bool IsDriverLoaded => IsOpen;
        public bool IsDriverInstalled { get; private set; }
        public string DevicePath => _activeDevicePath;
        public bool HasMchbar => _mchbarDeviceHandle != null && !_mchbarDeviceHandle.IsInvalid && _isMchbarLoaded;
        public string LastDiagnostic { get; private set; } = string.Empty;

        public PowerLimitApplyStatus Initialize()
        {
            lock (_ioLock)
            {
                if (IsOpen)
                    return PowerLimitApplyStatus.Success;

                string[] candidatePaths = {
                    GlobalDevicePath,
                    PrimaryDevicePath,
                    SecondaryDevicePath,
                    LowercaseDevicePath,
                    GlobalLowercaseDevicePath,
                    GlobalDotDevicePath,
                    GlobalDotLowercaseDevicePath
                };
                int lastWin32Error = 0;
                SafeFileHandle? candidateHandle = null;

                foreach (var path in candidatePaths)
                {
                    try
                    {
                        IntPtr raw = CreateFileW(
                            path,
                            GENERIC_READ | GENERIC_WRITE,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            IntPtr.Zero,
                            OPEN_EXISTING,
                            FILE_ATTRIBUTE_NORMAL,
                            IntPtr.Zero);

                        if (raw != IntPtr.Zero && raw.ToInt64() != -1)
                        {
                            candidateHandle = new SafeFileHandle(raw, ownsHandle: true);
                            _activeDevicePath = path;
                            IsDriverInstalled = true;
                            break;
                        }

                        lastWin32Error = Marshal.GetLastWin32Error();
                        if (lastWin32Error == ERROR_ACCESS_DENIED)
                        {
                            IsDriverInstalled = true;
                            _activeDevicePath = path;
                            LastDiagnostic = "Access denied opening PawnIO device handle. Administrator privileges required.";
                            return PowerLimitApplyStatus.AccessDenied;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[NativePawnIoBackend] Error probing {path}: {ex.Message}");
                    }
                }

                if (candidateHandle == null || candidateHandle.IsInvalid)
                {
                    bool isServicePresent = ProbePawnIoServiceOrFile(out string servicePath, out bool isRunning);
                    IsDriverInstalled = isServicePresent;

                    if (lastWin32Error == ERROR_ACCESS_DENIED)
                    {
                        LastDiagnostic = "Access denied opening PawnIO device handle. Administrator privileges required.";
                        return PowerLimitApplyStatus.AccessDenied;
                    }

                    // Check Windows vulnerable driver blocklist
                    if (IsDriverBlocklistEnabled())
                    {
                        LastDiagnostic = "Windows VulnerableDriverBlocklist is enabled; PawnIO driver execution may be restricted by HVCI.";
                    }
                    else
                    {
                        LastDiagnostic = isServicePresent
                            ? (isRunning ? "PawnIO service running but device handle inaccessible." : "PawnIO service is registered but stopped.")
                            : "PawnIO kernel driver is not installed or service is not running.";
                    }

                    return PowerLimitApplyStatus.DriverNotInstalled;
                }

                // 2. Load IntelMSR.bin module into PawnIO driver via IOCTL 0xA1B22084
                byte[]? msrBytes = LoadModuleBytes("IntelMSR.bin");
                if (msrBytes == null || msrBytes.Length == 0)
                {
                    candidateHandle.Dispose();
                    LastDiagnostic = "IntelMSR.bin module payload could not be loaded from assembly resources or disk.";
                    return PowerLimitApplyStatus.HardwareError;
                }

                bool loadSuccess = DeviceIoControl(
                    candidateHandle,
                    IOCTL_PAWNIO_LOAD_BINARY,
                    msrBytes,
                    (uint)msrBytes.Length,
                    null,
                    0,
                    out _,
                    IntPtr.Zero);

                if (!loadSuccess)
                {
                    int err = Marshal.GetLastWin32Error();
                    candidateHandle.Dispose();
                    LastDiagnostic = $"PawnIO LoadBinary IOCTL (0xA1B22084) failed. Win32Error: {err}";
                    return PowerLimitApplyStatus.HardwareError;
                }

                _deviceHandle = candidateHandle;
                _isModuleLoaded = true;

                // 3. Optional: Try loading IntelMCHBAR.bin into secondary handle for MMIO support
                TryInitializeMchbarModule();

                LastDiagnostic = "PawnIO driver connected and IntelMSR module loaded successfully.";
                return PowerLimitApplyStatus.Success;
            }
        }

        private void TryInitializeMchbarModule()
        {
            try
            {
                byte[]? mchbarBytes = LoadModuleBytes("IntelMCHBAR.bin");
                if (mchbarBytes == null || mchbarBytes.Length == 0)
                    return;

                string path = !string.IsNullOrEmpty(_activeDevicePath) ? _activeDevicePath : PrimaryDevicePath;
                IntPtr mchbarRaw = CreateFileW(
                    path,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                if (mchbarRaw == IntPtr.Zero || mchbarRaw.ToInt64() == -1)
                    return;

                var safeRaw = new SafeFileHandle(mchbarRaw, ownsHandle: true);
                if (!DeviceIoControl(safeRaw, IOCTL_PAWNIO_LOAD_BINARY, mchbarBytes, (uint)mchbarBytes.Length, null, 0, out _, IntPtr.Zero))
                {
                    safeRaw.Dispose();
                    return;
                }
                _mchbarDeviceHandle = safeRaw;
                _isMchbarLoaded = true;

                    // Query MCHBAR base physical address under PCI mutex lock
                    var pciMutex = GetOrCreatePciMutex();
                    bool pciAcquired = false;
                    try
                    {
                        if (pciMutex != null)
                        {
                            try { pciAcquired = pciMutex.WaitOne(150); }
                            catch (AbandonedMutexException) { pciAcquired = true; }
                        }

                        if (pciMutex == null || pciAcquired)
                        {
                            var outVal = new ulong[1];
                            if (ExecuteOnHandle(_mchbarDeviceHandle, FN_GET_MCHBAR, null, outVal))
                            {
                                _cachedMchbarAddress = outVal[0];
                            }
                        }
                    }
                    finally
                    {
                        if (pciAcquired && pciMutex != null)
                        {
                            try { pciMutex.ReleaseMutex(); } catch { }
                        }
                    }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativePawnIoBackend] Optional MCHBAR initialization skipped: {ex.Message}");
            }
        }

        private static byte[]? LoadModuleBytes(string fileName)
        {
            var assembly = typeof(NativePawnIoBackend).Assembly;
            string simpleName = Path.GetFileName(fileName);

            // 1. Search embedded resources
            foreach (var resName in assembly.GetManifestResourceNames())
            {
                if (resName.EndsWith(simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = assembly.GetManifestResourceStream(resName);
                    if (stream != null)
                    {
                        using var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }

            // 2. Search local directories on disk
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidateFiles =
            {
                Path.Combine(baseDir, "Pawn", simpleName),
                Path.Combine(baseDir, simpleName),
                Path.Combine(baseDir, "Assets", "Intel", "PawnIO", simpleName),
                Path.Combine(Environment.CurrentDirectory, "Pawn", simpleName),
                Path.Combine(Environment.CurrentDirectory, simpleName)
            };

            foreach (var path in candidateFiles)
            {
                if (File.Exists(path))
                {
                    try { return File.ReadAllBytes(path); } catch { }
                }
            }

            return null;
        }

        private static bool IsDriverBlocklistEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Config");
                if (key != null)
                {
                    var val = key.GetValue("VulnerableDriverBlocklistEnable");
                    if (val != null && (val.ToString() == "1" || val.Equals(1)))
                        return true;
                }
            }
            catch { }
            return false;
        }

        public static bool ProbePawnIoServiceOrFile(out string servicePath, out bool isRunning)
        {
            servicePath = string.Empty;
            isRunning = false;
            try
            {
                // 1. Registry service checks (both case variants)
                string[] serviceKeys = { @"SYSTEM\CurrentControlSet\Services\PawnIO", @"SYSTEM\CurrentControlSet\Services\pawnio" };
                foreach (var sKey in serviceKeys)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(sKey);
                    if (key != null)
                    {
                        var img = key.GetValue("ImagePath")?.ToString();
                        if (!string.IsNullOrEmpty(img))
                        {
                            servicePath = img;
                        }
                        break;
                    }
                }

                // 2. Query Service Manager via Advapi32
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
                        if (scm != IntPtr.Zero)
                        {
                            try
                            {
                                string[] svcNames = { "PawnIO", "pawnio" };
                                foreach (var name in svcNames)
                                {
                                    IntPtr svc = OpenServiceW(scm, name, SERVICE_QUERY_STATUS | SERVICE_START);
                                    if (svc == IntPtr.Zero && Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED)
                                    {
                                        svc = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
                                    }

                                    if (svc != IntPtr.Zero)
                                    {
                                        try
                                        {
                                            if (QueryServiceStatus(svc, out var status))
                                            {
                                                if (status.dwCurrentState == SERVICE_RUNNING)
                                                {
                                                    isRunning = true;
                                                    return true;
                                                }
                                                else if (status.dwCurrentState == SERVICE_STOPPED)
                                                {
                                                    // Service exists and is stopped; attempt to start it
                                                    if (StartServiceW(svc, 0, IntPtr.Zero))
                                                    {
                                                        Thread.Sleep(50);
                                                        if (QueryServiceStatus(svc, out var startStatus) &&
                                                            (startStatus.dwCurrentState == SERVICE_RUNNING || startStatus.dwCurrentState == SERVICE_START_PENDING))
                                                        {
                                                            isRunning = true;
                                                        }
                                                    }
                                                    return true;
                                                }
                                                else if (status.dwCurrentState == SERVICE_START_PENDING)
                                                {
                                                    isRunning = true;
                                                    return true;
                                                }
                                                return true;
                                            }
                                        }
                                        finally
                                        {
                                            CloseServiceHandle(svc);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                CloseServiceHandle(scm);
                            }
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(servicePath))
                    return true;

                // 3. Uninstall Keys (both native 64-bit and WOW6432Node)
                string[] uninstallKeys = {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"
                };
                foreach (var uKey in uninstallKeys)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(uKey);
                    if (key != null) return true;
                }

                // 4. System drivers directory
                string[] driverPaths = {
                    Path.Combine(Environment.SystemDirectory, "drivers", "pawnio.sys"),
                    Path.Combine(Environment.SystemDirectory, "drivers", "PawnIO.sys")
                };
                foreach (var p in driverPaths)
                {
                    if (File.Exists(p))
                    {
                        servicePath = p;
                        return true;
                    }
                }

                // 5. DriverStore FileRepository
                string driverStore = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
                if (Directory.Exists(driverStore))
                {
                    var dirs = Directory.GetDirectories(driverStore, "pawnio.inf*");
                    if (dirs.Length > 0)
                    {
                        foreach (var d in dirs)
                        {
                            string f = Path.Combine(d, "PawnIO.sys");
                            if (File.Exists(f)) { servicePath = f; return true; }
                            f = Path.Combine(d, "pawnio.sys");
                            if (File.Exists(f)) { servicePath = f; return true; }
                        }
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool ProbePawnIoServiceOrFile() => ProbePawnIoServiceOrFile(out _, out _);

        public PawnIoScanResult ScanDevice()
        {
            bool isServicePresent = ProbePawnIoServiceOrFile(out string servicePath, out bool isRunning);
            int lastWin32Error = 0;
            string matchedPath = string.Empty;
            bool isAccessible = false;
            bool requiresElevation = false;

            string[] candidatePaths = {
                GlobalDevicePath,
                PrimaryDevicePath,
                SecondaryDevicePath,
                LowercaseDevicePath,
                GlobalLowercaseDevicePath,
                GlobalDotDevicePath,
                GlobalDotLowercaseDevicePath
            };

            foreach (var path in candidatePaths)
            {
                try
                {
                    IntPtr handle = CreateFileW(
                        path,
                        GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        FILE_ATTRIBUTE_NORMAL,
                        IntPtr.Zero);

                    if (handle != IntPtr.Zero && handle.ToInt64() != -1)
                    {
                        matchedPath = path;
                        isAccessible = true;
                        CloseHandle(handle);
                        break;
                    }

                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_ACCESS_DENIED)
                    {
                        lastWin32Error = err;
                        requiresElevation = true;
                        matchedPath = path;
                        break;
                    }
                }
                catch { }
            }

            bool isInstalled = isServicePresent || isAccessible || requiresElevation;

            string message;
            if (isAccessible)
            {
                message = $"PawnIO kernel driver detected and accessible via {matchedPath}.";
            }
            else if (requiresElevation)
            {
                message = $"PawnIO detected at {matchedPath}, but access was denied (Win32Error 5). Run app as Administrator to unlock power limits.";
            }
            else if (isInstalled)
            {
                message = isRunning
                    ? $"PawnIO service is running ({servicePath}), but kernel device symlink is not accessible."
                    : $"PawnIO service is registered ({servicePath}), but the driver is not currently running.";
            }
            else
            {
                message = "PawnIO driver not detected on system (checked Services, DriverStore, drivers directory, and kernel device symlinks).";
            }

            return new PawnIoScanResult
            {
                IsInstalled = isInstalled,
                IsRunning = isRunning || isAccessible || requiresElevation,
                IsAccessible = isAccessible,
                RequiresElevation = requiresElevation,
                ServicePath = servicePath,
                ErrorCode = lastWin32Error,
                DiagnosticMessage = message
            };
        }

        public bool ReadMsr(uint msrIndex, out ulong value)
        {
            value = 0;
            if (!IsOpen || _deviceHandle == null || !_isModuleLoaded)
                return false;

            lock (_ioLock)
            {
                var mutex = GetOrCreateMsrMutex();
                bool acquired = false;
                try
                {
                    if (mutex != null)
                    {
                        try
                        {
                            acquired = mutex.WaitOne(150);
                        }
                        catch (AbandonedMutexException)
                        {
                            acquired = true;
                        }
                    }

                    if (mutex != null && !acquired)
                    {
                        Debug.WriteLine("[ReadMsr] Timed out waiting for Global\\Access_MSR. Aborting read to prevent collision.");
                        return false;
                    }

                    var output = new ulong[1];
                    if (ExecuteOnHandle(_deviceHandle, FN_READ_MSR, new ulong[] { (ulong)msrIndex }, output))
                    {
                        value = output[0];
                        return true;
                    }
                    return false;
                }
                finally
                {
                    if (acquired && mutex != null)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }

        public bool WriteMsr(uint msrIndex, ulong value)
        {
            if (!IsOpen || _deviceHandle == null || !_isModuleLoaded)
                return false;

            // Disallow setting lock bit (bit 63) on MSR 0x610 to avoid permanently locking the register
            if (msrIndex == PawnIoController.MSR_PKG_POWER_LIMIT && (value & (1UL << 63)) != 0)
            {
                Debug.WriteLine("[WriteMsr] Refusing to set bit 63 (lock bit) on MSR 0x610.");
                return false;
            }

            lock (_ioLock)
            {
                var mutex = GetOrCreateMsrMutex();
                bool acquired = false;
                try
                {
                    if (mutex != null)
                    {
                        try
                        {
                            acquired = mutex.WaitOne(150);
                        }
                        catch (AbandonedMutexException)
                        {
                            acquired = true;
                        }
                    }

                    if (mutex != null && !acquired)
                    {
                        Debug.WriteLine("[WriteMsr] Timed out waiting for Global\\Access_MSR. Aborting write to prevent collision.");
                        return false;
                    }

                    // If writing to MSR 0x610, verify it is not already locked by UEFI/BIOS while holding MSR mutex
                    if (msrIndex == PawnIoController.MSR_PKG_POWER_LIMIT)
                    {
                        var checkOut = new ulong[1];
                        if (ExecuteOnHandle(_deviceHandle, FN_READ_MSR, new ulong[] { (ulong)msrIndex }, checkOut))
                        {
                            if ((checkOut[0] & (1UL << 63)) != 0)
                            {
                                Debug.WriteLine("[WriteMsr] MSR 0x610 is locked by BIOS (bit 63 set). Aborting write.");
                                return false;
                            }
                        }
                    }

                    return ExecuteOnHandle(_deviceHandle, FN_WRITE_MSR, new ulong[] { (ulong)msrIndex, value }, null);
                }
                finally
                {
                    if (acquired && mutex != null)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }

        public bool ReadMmio32(ulong physicalAddress, out uint value)
        {
            value = 0;
            if (_mchbarDeviceHandle != null && !_mchbarDeviceHandle.IsInvalid && _isMchbarLoaded)
            {
                var mutex = GetOrCreatePciMutex();
                bool acquired = false;
                try
                {
                    if (mutex != null)
                    {
                        try { acquired = mutex.WaitOne(150); }
                        catch (AbandonedMutexException) { acquired = true; }
                    }

                    if (mutex != null && !acquired)
                    {
                        Debug.WriteLine("[ReadMmio32] Timed out waiting for Global\\Access_PCI. Aborting read to prevent collision.");
                        return false;
                    }

                    var output = new ulong[1];
                    if (ExecuteOnHandle(_mchbarDeviceHandle, FN_READ_DWORD, new ulong[] { physicalAddress }, output))
                    {
                        value = (uint)(output[0] & 0xFFFFFFFFU);
                        return true;
                    }
                }
                finally
                {
                    if (acquired && mutex != null)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
            return false;
        }

        public bool WriteMmio32(ulong physicalAddress, uint value)
        {
            if (!PredatorNativeCore.IsLoaded) return false;
            lock (_ioLock)
            {
                var mutex = GetOrCreatePciMutex();
                bool acquired = false;
                try
                {
                    if (mutex != null)
                    {
                        try { acquired = mutex.WaitOne(150); }
                        catch (AbandonedMutexException) { acquired = true; }
                    }
                    if (mutex != null && !acquired)
                    {
                        Debug.WriteLine("[WriteMmio32] Timed out waiting for Global\\Access_PCI. Aborting write to prevent collision.");
                        return false;
                    }
                    return PredatorNativeCore.PawnIoWriteMmio32(physicalAddress, value);
                }
                finally
                {
                    if (acquired && mutex != null)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }

        public bool ReadMmio64(ulong physicalAddress, out ulong value)
        {
            value = 0;
            if (_mchbarDeviceHandle != null && !_mchbarDeviceHandle.IsInvalid && _isMchbarLoaded)
            {
                var mutex = GetOrCreatePciMutex();
                bool acquired = false;
                try
                {
                    if (mutex != null)
                    {
                        try { acquired = mutex.WaitOne(150); }
                        catch (AbandonedMutexException) { acquired = true; }
                    }

                    if (mutex != null && !acquired)
                    {
                        Debug.WriteLine("[ReadMmio64] Timed out waiting for Global\\Access_PCI. Aborting read to prevent collision.");
                        return false;
                    }

                    var output = new ulong[1];
                    if (ExecuteOnHandle(_mchbarDeviceHandle, FN_READ_QWORD, new ulong[] { physicalAddress }, output))
                    {
                        value = output[0];
                        return true;
                    }
                }
                finally
                {
                    if (acquired && mutex != null)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
            return false;
        }

        public bool WriteMmio64(ulong physicalAddress, ulong value)
        {
            lock (_ioLock)
            {
                uint low = (uint)(value & 0xFFFFFFFF);
                uint high = (uint)(value >> 32);

                // Directional hazard check:
                // If New PL1 exceeds Old PL2 (or low > oldHigh), writing low first causes an intermediate
                // hardware violation where PL1 > PL2, causing CPU PCU/EC rejection.
                ulong oldVal = 0;
                bool readSuccess = ReadMmio64(physicalAddress, out oldVal);
                if (!readSuccess)
                {
                    if (ReadMmio32(physicalAddress, out uint oldLow) && ReadMmio32(physicalAddress + 4, out uint oldHigh))
                    {
                        oldVal = (ulong)oldLow | ((ulong)oldHigh << 32);
                        readSuccess = true;
                    }
                }
                if (readSuccess && (low & 0x7FFF) > (uint)((oldVal >> 32) & 0x7FFF))
                {
                    // Hazard-free ordering: Write upper 32-bit (PL2) first, then lower 32-bit (PL1) second.
                    bool pl2Ok = WriteMmio32(physicalAddress + 4, high);
                    bool pl1Ok = WriteMmio32(physicalAddress, low);
                    if (pl2Ok && !pl1Ok)
                    {
                        // Rollback PL2 to avoid leaving hardware in inconsistent split state
                        WriteMmio32(physicalAddress + 4, (uint)(oldVal >> 32));
                        return false;
                    }
                    return pl2Ok && pl1Ok;
                }
                else
                {
                    // Normal ordering: Write lower 32-bit (PL1) first, then upper 32-bit (PL2) second.
                    bool pl1Ok = WriteMmio32(physicalAddress, low);
                    bool pl2Ok = WriteMmio32(physicalAddress + 4, high);
                    if (pl1Ok && !pl2Ok && oldVal != 0)
                    {
                        // Rollback PL1 to avoid leaving hardware in inconsistent split state
                        WriteMmio32(physicalAddress, (uint)(oldVal & 0xFFFFFFFF));
                        return false;
                    }
                    return pl1Ok && pl2Ok;
                }
            }
        }

        private bool ExecuteOnHandle(SafeFileHandle handle, string functionName, ulong[]? input, ulong[]? output)
        {
            if (handle.IsInvalid || handle.IsClosed)
                return false;

            const int FN_LEN = 32;
            byte[] nameBytes = Encoding.ASCII.GetBytes(functionName);
            int inputCount = input?.Length ?? 0;
            byte[] inBuffer = new byte[FN_LEN + inputCount * 8];
            Buffer.BlockCopy(nameBytes, 0, inBuffer, 0, Math.Min(nameBytes.Length, FN_LEN - 1));

            if (input != null && inputCount > 0)
            {
                byte[] inputBytes = new byte[inputCount * 8];
                Buffer.BlockCopy(input, 0, inputBytes, 0, inputBytes.Length);
                Buffer.BlockCopy(inputBytes, 0, inBuffer, FN_LEN, inputBytes.Length);
            }

            int outputCount = output?.Length ?? 0;
            // Always provide at least 8 bytes so driver return status never faults with buffer too small
            int allocOutputCount = Math.Max(1, outputCount);
            byte[] outBuffer = new byte[allocOutputCount * 8];

            bool success = DeviceIoControl(
                handle,
                IOCTL_PAWNIO_EXECUTE,
                inBuffer,
                (uint)inBuffer.Length,
                outBuffer,
                (uint)outBuffer.Length,
                out uint bytesReturned,
                IntPtr.Zero);

            if (!success)
            {
                int err = Marshal.GetLastWin32Error();
                // 6 = ERROR_INVALID_HANDLE, 31 = ERROR_GEN_FAILURE, 55 = ERROR_DEV_NOT_EXIST, 1167 = ERROR_DEVICE_NOT_CONNECTED
                if (err == 6 || err == 31 || err == 55 || err == 1167)
                {
                    Close();
                }
                return false;
            }

            if (output != null && outputCount > 0)
            {
                Array.Clear(output, 0, output.Length);
                int maxValidBytes = Math.Min(outBuffer.Length, outputCount * 8);
                int copyBytes = (int)Math.Min((long)bytesReturned, (long)maxValidBytes);
                if (copyBytes > 0)
                {
                    Buffer.BlockCopy(outBuffer, 0, output, 0, copyBytes);
                }
            }

            return true;
        }

        public void Close()
        {
            lock (_ioLock)
            {
                _isModuleLoaded = false;
                _isMchbarLoaded = false;
                if (_deviceHandle != null && !_deviceHandle.IsClosed)
                {
                    _deviceHandle.Dispose();
                    _deviceHandle = null;
                }
                if (_mchbarDeviceHandle != null && !_mchbarDeviceHandle.IsClosed)
                {
                    _mchbarDeviceHandle.Dispose();
                    _mchbarDeviceHandle = null;
                }
                _activeDevicePath = string.Empty;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            Close();
            lock (_mutexLock)
            {
                _cachedMsrMutex?.Dispose();
                _cachedMsrMutex = null;
                _cachedPciMutex?.Dispose();
                _cachedPciMutex = null;
            }
        }
    }

    #endregion

    #region Main PawnIoController Implementation

    /// <summary>
    /// Ring-0 Intel RAPL MSR & MMIO power limit controller and baseline device capability scanner.
    /// Operates on MSR_RAPL_POWER_UNIT (0x606), MSR_PKG_POWER_INFO (0x614), MSR_PKG_POWER_LIMIT (0x610),
    /// and mirrors power limit adjustments to MMIO package RAPL (0xFED159A0) when accessible.
    /// </summary>
    public sealed class PawnIoController : IDisposable
    {
        private static readonly Lazy<PawnIoController> s_instance = new(() => new PawnIoController());
        public static PawnIoController Instance => s_instance.Value;
        #region Standard Intel RAPL & MMIO Addresses

        /// <summary>
        /// MSR 0x606: RAPL Power Units (Power, Energy, Time units)
        /// Power units = 1 / (2 ^ (raw & 0xF)) Watts -> UnitsPerWatt = 1 << (raw & 0xF).
        /// </summary>
        public const uint MSR_RAPL_POWER_UNIT = 0x606;

        /// <summary>
        /// MSR 0x610: Package Power Limit (PL1, PL2, Enable, Clamp, Tau time windows, Lock bit 63).
        /// </summary>
        public const uint MSR_PKG_POWER_LIMIT = 0x610;

        /// <summary>
        /// MSR 0x614: Package Power Info (Thermal Spec Power, Minimum Power, Maximum Power, Max Time Window).
        /// </summary>
        public const uint MSR_PKG_POWER_INFO = 0x614;

        /// <summary>
        /// Physical MMIO register address for Package RAPL Limit mirror (MCHBAR + 0x59A0).
        /// </summary>
        public const ulong MMIO_PKG_RAPL_LIMIT_ADDRESS = 0xFED159A0;
        public const ulong MMIO_PKG_RAPL_PL1_ADDRESS = 0xFED159A0;
        public const ulong MMIO_PKG_RAPL_PL2_ADDRESS = 0xFED159A4;

        #endregion

        private readonly IPawnIoBackend _backend;
        private readonly object _syncLock = new();
        private bool _isDisposed;

        public IPawnIoBackend Backend => _backend;
        public bool IsAvailable => _backend.IsOpen;

        /// <summary>
        /// Calculates the dynamic physical MMIO address for Package RAPL Limit.
        /// Uses MCHBAR base address + 0x59A0 when MCHBAR is queried, or falls back to standard 0xFED159A0.
        /// </summary>
        public ulong GetMmioPackageRaplAddress() => GetMmioPackageRaplPl1Address();

        /// <summary>
        /// Calculates the dynamic physical MMIO address for Package RAPL PL1 (lower 32 bits).
        /// </summary>
        public ulong GetMmioPackageRaplPl1Address()
        {
            ulong mchbar = _backend.MchbarAddress;
            // Verify MCHBAR base: filter out 0, 0xFFFFFFFF and ulong.MaxValue PCI bus abort values.
            // Mask off low 14 bits (16KB MCHBAR alignment: base & ~0x3FFFUL, which also strips enable bit 0).
            if (mchbar != 0 && (uint)mchbar != 0xFFFFFFFFU && mchbar != ulong.MaxValue)
            {
                ulong baseAddr = mchbar & ~0x3FFFUL;
                if (baseAddr >= 0x100000UL && baseAddr < (1UL << 48))
                {
                    return baseAddr + 0x59A0;
                }
            }
            return MMIO_PKG_RAPL_LIMIT_ADDRESS;
        }

        /// <summary>
        /// Calculates the dynamic physical MMIO address for Package RAPL PL2 (upper 32 bits, offset +0x4).
        /// </summary>
        public ulong GetMmioPackageRaplPl2Address() => GetMmioPackageRaplPl1Address() + 4;

        public PawnIoController(IPawnIoBackend? backend = null)
        {
            if (backend != null)
            {
                _backend = backend;
            }
            else
            {
                if (OperatingSystem.IsWindows())
                {
                    _backend = new NativePawnIoBackend();
                }
                else
                {
                    _backend = new MockPawnIoBackend();
                }
            }
        }

        /// <summary>
        /// Ensures the underlying driver backend is opened and initialized.
        /// </summary>
        public PowerLimitApplyStatus EnsureInitialized()
        {
            lock (_syncLock)
            {
                if (_backend.IsOpen)
                    return PowerLimitApplyStatus.Success;

                return _backend.Initialize();
            }
        }

        /// <summary>
        /// Ensures the underlying driver backend is loaded and initialized.
        /// </summary>
        public PowerLimitApplyStatus EnsureDriverLoaded() => EnsureInitialized();

        /// <summary>
        /// Dynamically scans the user's system for PawnIO installation, service state, and kernel accessibility.
        /// </summary>
        public PawnIoScanResult ScanDeviceForPawnIo()
        {
            lock (_syncLock)
            {
                return _backend.ScanDevice();
            }
        }

        #region RAPL MSR Operations

        /// <summary>
        /// Reads MSR_RAPL_POWER_UNIT (0x606) and calculates UnitsPerWatt = 1 << (raw & 0xF).
        /// Synchronized under _syncLock to prevent register read/write interleaving.
        /// </summary>
        public bool ReadPowerUnits(out uint unitsPerWatt, out ulong rawUnit)
        {
            lock (_syncLock)
            {
                unitsPerWatt = 8; // Default fallback: 8 units per watt (1/8 W = 0.125W)
                rawUnit = 0;

                if (!_backend.ReadMsr(MSR_RAPL_POWER_UNIT, out rawUnit))
                    return false;

                // Bits 3:0 specify power units: 1 / (2^PU) watts -> UnitsPerWatt = 1 << (raw & 0xF)
                uint pu = (uint)(rawUnit & 0x0FUL);

                // Valid power unit exponent for Intel RAPL is typically 3 (1/8W) or 4 (1/16W).
                // A rawUnit of 0 indicates unsupported MSR / virtualized environment without RAPL.
                if (pu == 0 && rawUnit == 0)
                    return false;

                unitsPerWatt = 1U << (int)pu;
                return unitsPerWatt > 0;
            }
        }

        /// <summary>
        /// Reads MSR_PKG_POWER_INFO (0x614) to obtain ThermalSpecPowerWatts (bits 14:0),
        /// MinimumPowerWatts (bits 30:16), and MaximumPowerWatts (bits 46:32).
        /// Synchronized under _syncLock to prevent register read/write interleaving.
        /// </summary>
        public bool ReadPowerInfo(
            uint unitsPerWatt,
            out int thermalSpecPowerWatts,
            out int minimumPowerWatts,
            out int maximumPowerWatts,
            out ulong rawInfo)
        {
            lock (_syncLock)
            {
                thermalSpecPowerWatts = 0;
                minimumPowerWatts = 0;
                maximumPowerWatts = 0;
                rawInfo = 0;

                if (unitsPerWatt == 0)
                    return false;

                if (!_backend.ReadMsr(MSR_PKG_POWER_INFO, out rawInfo))
                    return false;

                ulong rawThermal = rawInfo & 0x7FFFUL;
                ulong rawMin = (rawInfo >> 16) & 0x7FFFUL;
                ulong rawMax = (rawInfo >> 32) & 0x7FFFUL;

                thermalSpecPowerWatts = (int)Math.Round(rawThermal / (double)unitsPerWatt);
                minimumPowerWatts = (int)Math.Round(rawMin / (double)unitsPerWatt);
                maximumPowerWatts = (int)Math.Round(rawMax / (double)unitsPerWatt);

                return true;
            }
        }

        /// <summary>
        /// Reads MSR_PKG_POWER_LIMIT (0x610) to obtain Current PL1 (bits 14:0),
        /// Current PL2 (bits 46:32), and the BIOS lock check bit (bit 63).
        /// Synchronized under _syncLock to prevent register read/write interleaving.
        /// </summary>
        public bool ReadPowerLimits(
            uint unitsPerWatt,
            out int currentPl1Watts,
            out int currentPl2Watts,
            out bool isLocked,
            out ulong rawLimit)
        {
            lock (_syncLock)
            {
                currentPl1Watts = 0;
                currentPl2Watts = 0;
                isLocked = false;
                rawLimit = 0;

                if (unitsPerWatt == 0)
                    return false;

                if (!_backend.ReadMsr(MSR_PKG_POWER_LIMIT, out rawLimit))
                    return false;

                isLocked = (rawLimit & (1UL << 63)) != 0;

                ulong rawPl1 = rawLimit & 0x7FFFUL;
                ulong rawPl2 = (rawLimit >> 32) & 0x7FFFUL;

                currentPl1Watts = (int)Math.Round(rawPl1 / (double)unitsPerWatt);
                currentPl2Watts = (int)Math.Round(rawPl2 / (double)unitsPerWatt);

                return true;
            }
        }

        public bool ReadPowerLimits(out int currentPl1Watts, out int currentPl2Watts, out bool isLocked)
        {
            lock (_syncLock)
            {
                if (!ReadPowerUnits(out uint unitsPerWatt, out _))
                {
                    currentPl1Watts = 0;
                    currentPl2Watts = 0;
                    isLocked = false;
                    return false;
                }
                return ReadPowerLimits(unitsPerWatt, out currentPl1Watts, out currentPl2Watts, out isLocked, out _);
            }
        }

        #endregion

        #region Device Baseline Scanner

        /// <summary>
        /// Scans hardware baseline power capabilities across MSRs and MMIO.
        /// Applies algorithmic baseline derivations:
        /// - ScannedMinPowerWatts = MinimumPowerWatts > 0 ? MinimumPowerWatts : Math.Max(5, (int)Math.Round(ThermalSpecPowerWatts * 0.20)).
        /// - ScannedMaxPl2Watts = MaximumPowerWatts > 0 && MaximumPowerWatts >= CurrentPl2Watts ? MaximumPowerWatts : Math.Max(CurrentPl2Watts, (int)Math.Round(ThermalSpecPowerWatts * 2.6)).
        /// - ScannedMaxPl1Watts = Math.Max(CurrentPl1Watts, (int)Math.Round(ScannedMaxPl2Watts * 0.85)).
        /// </summary>
        public DevicePowerCapabilities ScanDevicePowerCapabilities()
        {
            lock (_syncLock)
            {
                var initStatus = EnsureInitialized();
                if (initStatus != PowerLimitApplyStatus.Success)
                {
                    return DevicePowerCapabilities.Unsupported(
                        initStatus,
                        $"Driver initialization failed with status: {initStatus}");
                }

                // 1. Read Power Units (MSR 0x606)
                if (!ReadPowerUnits(out uint unitsPerWatt, out ulong rawUnit))
                {
                    return DevicePowerCapabilities.Unsupported(
                        PowerLimitApplyStatus.UnsupportedHardware,
                        "Failed to read MSR_RAPL_POWER_UNIT (0x606) or CPU does not support RAPL.");
                }

                // 2. Read Power Info (MSR 0x614)
                bool hasPowerInfo = ReadPowerInfo(
                    unitsPerWatt,
                    out int thermalSpecPowerWatts,
                    out int minimumPowerWatts,
                    out int maximumPowerWatts,
                    out ulong rawInfo);

                // 3. Read Current Limits & Lock (MSR 0x610)
                if (!ReadPowerLimits(
                    unitsPerWatt,
                    out int currentPl1Watts,
                    out int currentPl2Watts,
                    out bool isLocked,
                    out ulong rawLimit))
                {
                    return DevicePowerCapabilities.Unsupported(
                        PowerLimitApplyStatus.HardwareError,
                        "Failed to read MSR_PKG_POWER_LIMIT (0x610).");
                }

                // Fallback for ThermalSpecPowerWatts if CPU did not report MSR 0x614
                if (thermalSpecPowerWatts <= 0)
                {
                    thermalSpecPowerWatts = currentPl1Watts > 0 ? currentPl1Watts : 45;
                }

                // 4. Algorithmic Baseline Scans (Requirement 4)
                int scannedMinPowerWatts = minimumPowerWatts > 0
                    ? minimumPowerWatts
                    : Math.Max(5, (int)Math.Round(thermalSpecPowerWatts * 0.20));

                int scannedMaxPl2Watts = maximumPowerWatts > 0 && maximumPowerWatts >= currentPl2Watts
                    ? maximumPowerWatts
                    : Math.Max(currentPl2Watts, (int)Math.Round(thermalSpecPowerWatts * 2.6));

                int scannedMaxPl1Watts = Math.Max(currentPl1Watts, (int)Math.Round(scannedMaxPl2Watts * 0.85));

                // 5. Inspect MMIO Package RAPL mirror if accessible
                bool isMmioAccessible = false;
                ulong rawMmioLimit = 0;
                ulong mmioAddress = GetMmioPackageRaplAddress();
                if (_backend.ReadMmio64(mmioAddress, out rawMmioLimit))
                {
                    isMmioAccessible = true;
                }

                string diag = $"RAPL Units: {unitsPerWatt} U/W (raw=0x{rawUnit:X}); " +
                              $"TDP: {thermalSpecPowerWatts}W, Min: {minimumPowerWatts}W, Max: {maximumPowerWatts}W; " +
                              $"Current: PL1={currentPl1Watts}W, PL2={currentPl2Watts}W (Locked={isLocked}); " +
                              $"Scanned: Min={scannedMinPowerWatts}W, MaxPL1={scannedMaxPl1Watts}W, MaxPL2={scannedMaxPl2Watts}W; " +
                              $"MMIO 0x{mmioAddress:X}: {(isMmioAccessible ? $"Accessible (raw=0x{rawMmioLimit:X})" : "Unavailable")}.";

                return new DevicePowerCapabilities
                {
                    IsSupported = true,
                    IsLocked = isLocked,
                    IsMmioAccessible = isMmioAccessible,
                    UnitsPerWatt = unitsPerWatt,
                    ThermalSpecPowerWatts = thermalSpecPowerWatts,
                    MinimumPowerWatts = minimumPowerWatts,
                    MaximumPowerWatts = maximumPowerWatts,
                    CurrentPl1Watts = currentPl1Watts,
                    CurrentPl2Watts = currentPl2Watts,
                    ScannedMinPowerWatts = scannedMinPowerWatts,
                    ScannedMaxPl1Watts = scannedMaxPl1Watts,
                    ScannedMaxPl2Watts = scannedMaxPl2Watts,
                    RawPowerUnit = rawUnit,
                    RawPowerInfo = rawInfo,
                    RawPowerLimit = rawLimit,
                    RawMmioPowerLimit = rawMmioLimit,
                    BaselineStatus = PowerLimitApplyStatus.Success,
                    Diagnostics = diag
                };
            }
        }

        #endregion

        #region MSR and MMIO Write Operations

        /// <summary>
        /// Writes MSR_PKG_POWER_LIMIT (0x610) atomically in a single 64-bit QWORD write.
        /// Preserves BIOS-programmed Time Window (tau) bits [23:17] and [55:49].
        /// Sets Enable bits (15, 47) and Clamping bits (16, 48), with bit 63 kept cleared.
        /// </summary>
        public bool WriteMsrPowerLimits(ulong rawPl1, ulong rawPl2, ulong currentMsrLimit, out ulong writtenMsr)
        {
            lock (_syncLock)
            {
                writtenMsr = 0;

                // Verify MSR_PKG_POWER_LIMIT (0x610) lock bit (bit 63)
                if ((currentMsrLimit & (1UL << 63)) != 0)
                {
                    Debug.WriteLine("[WriteMsrPowerLimits] MSR 0x610 is locked by BIOS (bit 63 set). Aborting write.");
                    return false;
                }

                // Payload construction is owned by pawnio.rs; the managed composer below
                // is only used when native code is unavailable.
                if (!PredatorNativeCore.PawnIoPlanMsrWrite(currentMsrLimit, rawPl1, rawPl2, out ulong newMsrValue))
                {
                    newMsrValue = ComposeMsrPowerLimitManaged(currentMsrLimit, rawPl1, rawPl2);
                }

                // Write MSR_PKG_POWER_LIMIT (0x610) atomically via single wrmsr call
                if (!_backend.WriteMsr(MSR_PKG_POWER_LIMIT, newMsrValue))
                {
                    Debug.WriteLine("[WriteMsrPowerLimits] Backend WriteMsr failed for MSR 0x610.");
                    return false;
                }

                writtenMsr = newMsrValue;
                return true;
            }
        }

        /// <summary>
        /// Managed fallback for <see cref="WriteMsrPowerLimits"/> used only when
        /// <c>predator_core.dll</c> is unavailable. Mirrors <c>plan_msr_power_limit</c>.
        /// </summary>
        private static ulong ComposeMsrPowerLimitManaged(ulong currentMsrLimit, ulong rawPl1, ulong rawPl2)
        {
            ulong safePl1 = rawPl1 & 0x7FFFUL;
            ulong safePl2 = rawPl2 & 0x7FFFUL;
            if (safePl2 < safePl1) safePl2 = safePl1;
            if (safePl1 == 0 && safePl2 > 0) safePl1 = safePl2;

            ulong tau1 = currentMsrLimit & (0x7FUL << 17);
            ulong tau2 = currentMsrLimit & (0x7FUL << 49);
            if (tau1 == 0) tau1 = 0x28UL << 17;
            if (tau2 == 0) tau2 = 0x10UL << 49;

            ulong enableAndClamp = (1UL << 15) | (1UL << 16) | (1UL << 47) | (1UL << 48);
            return (safePl1 | enableAndClamp | tau1 | (safePl2 << 32) | tau2) & ~(1UL << 63);
        }

        /// <summary>
        /// Reads current MSR 0x610 and writes target PL1/PL2 atomically via a single 64-bit wrmsr write.
        /// </summary>
        public bool WriteMsrPowerLimits(ulong rawPl1, ulong rawPl2, out ulong writtenMsr)
        {
            lock (_syncLock)
            {
                writtenMsr = 0;
                if (!_backend.ReadMsr(MSR_PKG_POWER_LIMIT, out ulong currentMsrLimit))
                    return false;

                return WriteMsrPowerLimits(rawPl1, rawPl2, currentMsrLimit, out writtenMsr);
            }
        }

        /// <summary>
        /// Directly writes a preconstructed 64-bit value to MSR_PKG_POWER_LIMIT (0x610), ensuring lock bit 63 is never set.
        /// </summary>
        public bool WriteMsrPowerLimits(ulong newMsrValue)
        {
            lock (_syncLock)
            {
                ulong safeValue = newMsrValue & ~(1UL << 63);
                return _backend.WriteMsr(MSR_PKG_POWER_LIMIT, safeValue);
            }
        }

        /// <summary>
        /// Writes target PL1 and PL2 power limits to MMIO (0xFED159A0 / 0xFED159A4) with hazard-free ordering.
        /// If newRawPl1 > oldRawPl2, writes the upper 32-bit PL2 register (0xFED159A4) FIRST, then lower 32-bit PL1 register (0xFED159A0) SECOND.
        /// Otherwise (newRawPl1 <= oldRawPl2), writes lower 32-bit PL1 register (0xFED159A0) FIRST, then upper 32-bit PL2 register (0xFED159A4) SECOND.
        /// Prevents an illegal intermediate hardware state where PL1 > PL2, preventing CPU PCU or EC from rejecting or clamping the write.
        /// </summary>
        public bool WriteMchbarPowerLimits(ulong rawPl1, ulong rawPl2, out ulong rawMmioWritten)
        {
            return WriteMchbarPowerLimits(rawPl1, rawPl2, 0x28UL << 17, 0x10UL << 49, out rawMmioWritten);
        }

        /// <summary>
        /// Writes target PL1 and PL2 power limits to MMIO (0xFED159A0 / 0xFED159A4) with hazard-free ordering,
        /// using specified fallback Tau time windows if not present in current MMIO hardware register.
        /// </summary>
        public bool WriteMchbarPowerLimits(ulong rawPl1, ulong rawPl2, ulong fallbackTau1, ulong fallbackTau2, out ulong rawMmioWritten)
        {
            lock (_syncLock)
            {
                rawMmioWritten = 0;
                ulong mmioPl1Address = GetMmioPackageRaplPl1Address();
                ulong mmioPl2Address = GetMmioPackageRaplPl2Address();

                // Step 1: Read current hardware state to determine OldPl1 and OldPl2
                ulong currentMmio = 0;
                uint currentPl1Dword = 0;
                uint currentPl2Dword = 0;
                bool readSuccess = false;

                if (_backend.ReadMmio64(mmioPl1Address, out currentMmio))
                {
                    readSuccess = true;
                    currentPl1Dword = (uint)(currentMmio & 0xFFFFFFFFU);
                    currentPl2Dword = (uint)(currentMmio >> 32);
                }
                else if (_backend.ReadMmio32(mmioPl1Address, out currentPl1Dword) && _backend.ReadMmio32(mmioPl2Address, out currentPl2Dword))
                {
                    readSuccess = true;
                    currentMmio = (ulong)currentPl1Dword | ((ulong)currentPl2Dword << 32);
                }

                if (!readSuccess)
                {
                    Debug.WriteLine($"[WriteMchbarPowerLimits] Failed to read current MMIO registers at 0x{mmioPl1Address:X}.");
                    return false;
                }

                // Step 2: Ask the native planner for the ordered writes. pawnio.rs is the
                // single source of truth for hazard ordering and Tau preservation; the
                // managed composer below is only a fallback for a missing DLL.
                NativeMchbarWritePlan plan;
                if (!PredatorNativeCore.PawnIoPlanMchbarWrite(
                        currentMmio, true, rawPl1, rawPl2, fallbackTau1, fallbackTau2,
                        mmioPl1Address, mmioPl2Address, out plan))
                {
                    if (!TryComposeMchbarPlanManaged(
                            currentMmio, rawPl1, rawPl2, fallbackTau1, fallbackTau2,
                            mmioPl1Address, mmioPl2Address, out plan))
                    {
                        Debug.WriteLine($"[WriteMchbarPowerLimits] MMIO Package RAPL at 0x{mmioPl1Address:X} is locked by BIOS (bit 63 set).");
                        return false;
                    }
                }

                // Step 3: Execute the plan in order, rolling back the first store when the
                // second fails so the register never keeps a partial update.
                bool firstOk = _backend.WriteMmio32(plan.FirstAddress, plan.FirstValue);
                if (firstOk)
                {
                    bool secondOk = _backend.WriteMmio32(plan.SecondAddress, plan.SecondValue);
                    if (!secondOk)
                    {
                        _backend.WriteMmio32(plan.FirstAddress, plan.FirstPreviousValue);
                    }

                    if (secondOk)
                    {
                        rawMmioWritten = plan.MirroredValue;
                        return true;
                    }
                }

                // Fallback: If 32-bit writes are unsupported on the backend, attempt atomic 64-bit write
                if (_backend.WriteMmio64(mmioPl1Address, plan.MirroredValue))
                {
                    rawMmioWritten = plan.MirroredValue;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Managed fallback planner used only when <c>predator_core.dll</c> is unavailable.
        /// Mirrors <c>plan_mchbar_power_write</c> in <c>pawnio.rs</c>.
        /// </summary>
        private static bool TryComposeMchbarPlanManaged(
            ulong currentMmio,
            ulong rawPl1,
            ulong rawPl2,
            ulong fallbackTau1,
            ulong fallbackTau2,
            ulong mmioPl1Address,
            ulong mmioPl2Address,
            out NativeMchbarWritePlan plan)
        {
            plan = default;
            uint currentPl1Dword = (uint)(currentMmio & 0xFFFFFFFFU);
            uint currentPl2Dword = (uint)(currentMmio >> 32);

            if ((currentMmio & (1UL << 63)) != 0 || (currentPl2Dword & (1U << 31)) != 0)
            {
                return false;
            }

            ulong oldRawPl2 = currentPl2Dword & 0x7FFFUL;

            uint mmioTau1 = currentPl1Dword & (0x7FU << 17);
            if (currentPl1Dword == 0 && mmioTau1 == 0) mmioTau1 = (uint)(fallbackTau1 & (0x7FUL << 17));
            if (currentPl1Dword == 0 && mmioTau1 == 0) mmioTau1 = 0x28U << 17;

            uint mmioTau2 = currentPl2Dword & (0x7FU << 17);
            if (currentPl2Dword == 0 && mmioTau2 == 0)
            {
                ulong shiftedTau = (fallbackTau2 >> 32) != 0 ? (fallbackTau2 >> 32) : fallbackTau2;
                mmioTau2 = (uint)(shiftedTau & (0x7FUL << 17));
            }
            if (currentPl2Dword == 0 && mmioTau2 == 0) mmioTau2 = 0x10U << 17;

            ulong safePl1 = rawPl1 & 0x7FFFUL;
            ulong safePl2 = rawPl2 & 0x7FFFUL;
            if (safePl2 < safePl1) safePl2 = safePl1;
            if (safePl1 == 0 && safePl2 > 0) safePl1 = safePl2;

            uint enableAndClamp = (1U << 15) | (1U << 16);
            uint pl1Dword = (uint)(safePl1 | enableAndClamp | mmioTau1);
            uint pl2Dword = (uint)((safePl2 | enableAndClamp | mmioTau2) & ~(1U << 31));

            bool hazard = safePl1 > oldRawPl2;
            plan = new NativeMchbarWritePlan
            {
                MirroredValue = (ulong)pl1Dword | ((ulong)pl2Dword << 32),
                FirstAddress = hazard ? mmioPl2Address : mmioPl1Address,
                SecondAddress = hazard ? mmioPl1Address : mmioPl2Address,
                FirstValue = hazard ? pl2Dword : pl1Dword,
                SecondValue = hazard ? pl1Dword : pl2Dword,
                FirstPreviousValue = hazard ? currentPl2Dword : currentPl1Dword,
                Status = NativeMchbarWritePlan.StatusOk,
            };
            return true;
        }

        /// <summary>
        /// Writes target PL1 and PL2 power limits in Watts to MMIO with hazard-free ordering.
        /// </summary>
        public bool WriteMchbarPowerLimits(int pl1Watts, int pl2Watts, uint unitsPerWatt, out ulong rawMmioWritten)
        {
            int safePl1 = Math.Max(5, pl1Watts);
            int safePl2 = Math.Max(safePl1, pl2Watts);

            ulong rawPl1 = Math.Min(0x7FFFUL, (ulong)Math.Max(0, Math.Round((double)safePl1 * unitsPerWatt)));
            ulong rawPl2 = Math.Min(0x7FFFUL, (ulong)Math.Max(0, Math.Round((double)safePl2 * unitsPerWatt)));

            return WriteMchbarPowerLimits(rawPl1, rawPl2, out rawMmioWritten);
        }

        #endregion

        #region Apply Power Limits Operation

        /// <summary>
        /// Applies target PL1 and PL2 power limits via Ring-0 MSR and mirrors to MMIO if accessible.
        /// - Checks MSR_PKG_POWER_LIMIT (0x610) lock bit (bit 63). If locked, returns BiosLocked.
        /// - Encodes PL1 (bits 14:0) and PL2 (bits 46:32).
        /// - Preserves Tau time windows: PL1 Tau (bits 23:17) and PL2 Tau (bits 55:49).
        /// - Sets Enable bits (15 for PL1, 47 for PL2) and Clamping bits (16 for PL1, 48 for PL2).
        /// - Synchronously updates MMIO Package RAPL (0xFED159A0 / 0xFED159A4) mirror using hazard-free write ordering.
        /// </summary>
        public PowerLimitApplyResult ApplyPowerLimits(int targetPl1Watts, int targetPl2Watts)
        {
            lock (_syncLock)
            {
                var initStatus = EnsureInitialized();
                if (initStatus != PowerLimitApplyStatus.Success)
                {
                    return initStatus switch
                    {
                        PowerLimitApplyStatus.DriverNotInstalled => PowerLimitApplyResult.DriverNotInstalled(),
                        PowerLimitApplyStatus.AccessDenied => PowerLimitApplyResult.AccessDenied(),
                        PowerLimitApplyStatus.UnsupportedHardware => PowerLimitApplyResult.UnsupportedHardware(),
                        _ => PowerLimitApplyResult.HardwareError($"Backend initialization failed with status: {initStatus}")
                    };
                }

                // Step 1: Read Power Units (MSR 0x606)
                if (!ReadPowerUnits(out uint unitsPerWatt, out ulong rawUnit))
                {
                    return PowerLimitApplyResult.HardwareError(
                        "Failed to read MSR_RAPL_POWER_UNIT (0x606).");
                }

                uint pu = (uint)(rawUnit & 0x0FUL);
                if (pu == 0 && rawUnit == 0)
                {
                    return PowerLimitApplyResult.UnsupportedHardware(
                        "Target processor does not support Intel RAPL MSRs (MSR 0x606 returned 0).");
                }

                // Step 2: Read Current MSR_PKG_POWER_LIMIT (0x610)
                if (!_backend.ReadMsr(MSR_PKG_POWER_LIMIT, out ulong currentMsrLimit))
                {
                    return PowerLimitApplyResult.HardwareError(
                        "Failed to read current MSR_PKG_POWER_LIMIT (0x610).");
                }

                // Step 3: Bit 63 lock check
                bool isLocked = (currentMsrLimit & (1UL << 63)) != 0;
                if (isLocked)
                {
                    return PowerLimitApplyResult.BiosLocked(
                        "MSR_PKG_POWER_LIMIT (0x610) bit 63 is locked by system UEFI/BIOS. Power limits cannot be overridden.");
                }

                // Step 4: Validate and clamp target limits (minimum architectural floor is 5W, PL2 >= PL1)
                int safePl1 = Math.Max(5, targetPl1Watts);
                int safePl2 = Math.Max(safePl1, targetPl2Watts);

                ulong rawPl1 = Math.Min(0x7FFFUL, (ulong)Math.Max(0, Math.Round((double)safePl1 * unitsPerWatt)));
                ulong rawPl2 = Math.Min(0x7FFFUL, (ulong)Math.Max(0, Math.Round((double)safePl2 * unitsPerWatt)));

                // Step 5: Atomically write MSR_PKG_POWER_LIMIT (0x610)
                if (!WriteMsrPowerLimits(rawPl1, rawPl2, currentMsrLimit, out ulong newMsrValue))
                {
                    return PowerLimitApplyResult.HardwareError(
                        "Failed to write new power limits to MSR_PKG_POWER_LIMIT (0x610).");
                }

                // Step 6: Mirror to MMIO Package RAPL if accessible with hazard-free ordering
                bool mmioMirrored = false;
                ulong rawMmioWritten = 0;

                try
                {
                    ulong tau1 = currentMsrLimit & (0x7FUL << 17);
                    ulong tau2 = currentMsrLimit & (0x7FUL << 49);
                    mmioMirrored = WriteMchbarPowerLimits(rawPl1, rawPl2, tau1, tau2, out rawMmioWritten);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PawnIoController] MMIO mirror exception: {ex.Message}");
                }

                return PowerLimitApplyResult.Success(
                    safePl1,
                    safePl2,
                    mmioMirrored,
                    newMsrValue,
                    rawMmioWritten,
                    $"Power limits applied successfully: PL1={safePl1}W, PL2={safePl2}W (MMIO Mirrored: {mmioMirrored}).");
            }
        }

        #endregion

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _backend.Dispose();
        }
    }

    #endregion
}
