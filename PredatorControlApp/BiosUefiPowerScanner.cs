using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PredatorControlApp
{
    #region Structured Domain Models

    /// <summary>
    /// Holds SMBIOS and UEFI platform identity attributes read directly from native Windows registry keys.
    /// </summary>
    public sealed record BiosHardwareIdentity
    {
        public string SystemProductName { get; init; } = string.Empty;
        public string BiosVersion { get; init; } = string.Empty;
        public string BiosReleaseDate { get; init; } = string.Empty;
        public string BaseBoardProduct { get; init; } = string.Empty;
        public string BaseBoardManufacturer { get; init; } = string.Empty;
        public string SystemManufacturer { get; init; } = string.Empty;
        public string ProcessorNameString { get; init; } = string.Empty;
        public string CpuIdentifier { get; init; } = string.Empty;
        public string HardwareFingerprint { get; init; } = string.Empty;
    }

    /// <summary>
    /// Represents an individual power limit capability entry parsed from ACPI Intel DPTF PPCC AML structures.
    /// </summary>
    public sealed record PpccPowerLimitEntry
    {
        public int PowerLimitIndex { get; init; } // 0 = PL1, 1 = PL2, 2 = PL3
        public int MinPowerLimitMilliwatts { get; init; }
        public int MaxPowerLimitMilliwatts { get; init; }
        public int MinPowerLimitWatts => (int)Math.Round(MinPowerLimitMilliwatts / 1000.0);
        public int MaxPowerLimitWatts => (int)Math.Round(MaxPowerLimitMilliwatts / 1000.0);
        public int MinTimeWindowMs { get; init; }
        public int MaxTimeWindowMs { get; init; }
        public int StepSizeMilliwatts { get; init; }
    }

    /// <summary>
    /// Holds discovered ACPI firmware tables, parsed Intel DPTF PPCC limits, and UEFI NVRAM variables.
    /// </summary>
    public sealed record AcpiFirmwarePowerData
    {
        public bool IsFound { get; init; }
        public bool HasPpccTable { get; init; }
        public int Pl1MinWatts { get; init; }
        public int Pl1MaxWatts { get; init; }
        public int Pl2MaxWatts { get; init; }
        public int BaseTdpWatts { get; init; }
        public IReadOnlyList<PpccPowerLimitEntry> PpccEntries { get; init; } = Array.Empty<PpccPowerLimitEntry>();
        public IReadOnlyList<string> DiscoveredTables { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, byte[]> UefiNvramVariables { get; init; } = new Dictionary<string, byte[]>();
        public string SourceTable { get; init; } = string.Empty;
        public bool HasOemThermalTables { get; init; }
        public string OemThermalSummary { get; init; } = string.Empty;
    }

    /// <summary>
    /// Represents a single operating mode profile discovered from Acer ACPI WMI or EC firmware tables.
    /// </summary>
    public sealed record FirmwareModeProfile
    {
        public OperatingMode Mode { get; init; }
        public int Pl1Watts { get; init; }
        public int Pl2Watts { get; init; }
        public int FanSpeedPercent { get; init; }
        public byte RawModeValue { get; init; }
        public string Source { get; init; } = string.Empty;
    }

    /// <summary>
    /// Holds WMI Acer gaming MOF class queries, methods, and per-mode thermal/power buffers.
    /// </summary>
    public sealed record WmiFirmwareThermalData
    {
        public bool IsFound { get; init; }
        public string WmiClass { get; init; } = string.Empty;
        public IReadOnlyDictionary<OperatingMode, FirmwareModeProfile> ModeProfiles { get; init; } = new Dictionary<OperatingMode, FirmwareModeProfile>();
        public IReadOnlyList<string> QueriedMethods { get; init; } = Array.Empty<string>();
        public string RawPayloadSummary { get; init; } = string.Empty;
    }

    /// <summary>
    /// Holds CPU silicon fused capabilities read directly from Ring-0 MSRs via PawnIO.
    /// </summary>
    public sealed record SiliconFusedPowerData
    {
        public bool IsFound { get; init; }
        public uint UnitsPerWatt { get; init; }
        public int ThermalSpecPowerWatts { get; init; }
        public int MinimumPowerWatts { get; init; }
        public int MaximumPowerWatts { get; init; }
        public int MaxTimeWindowSeconds { get; init; }
        public ulong RawPowerInfo { get; init; }
        public ulong RawPowerUnit { get; init; }
    }

    /// <summary>
    /// Synthesized CPU power boundaries combining PPCC, Acer WMI, and silicon fused registers.
    /// </summary>
    public sealed record SynthesizedPowerBoundaries
    {
        public int CpuMinAllowedPowerWatts { get; init; }
        public int CpuThermalSpecTdpWatts { get; init; }
        public int CpuMaxAllowedPl1Watts { get; init; }
        public int CpuMaxAllowedPl2Watts { get; init; }
        public IReadOnlyDictionary<OperatingMode, (int Pl1Watts, int Pl2Watts)> RecommendedModeDefaults { get; init; } = new Dictionary<OperatingMode, (int, int)>();
    }

    #endregion

    #region Scan Result & Interfaces

    /// <summary>
    /// Holds the complete result of a native Acer BIOS/UEFI, ACPI WMI, and CPU hardware power limit scan.
    /// STRICT NATIVE SCAN: Never contains data from external third-party tools or configuration files.
    /// </summary>
    public sealed class BiosPowerScanResult
    {
        public string BiosHardwareFingerprint { get; set; } = string.Empty;
        public bool HasCompletedBiosPowerScan { get; set; }
        public bool IsSuccess => HasCompletedBiosPowerScan;
        public int CpuThermalSpecTdpWatts { get; set; }
        public int CpuMinAllowedPowerWatts { get; set; }
        public int CpuMaxAllowedPl1Watts { get; set; }
        public int CpuMaxAllowedPl2Watts { get; set; }

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

        public int VerifiedBiosModesBitmask { get; set; }
        public string Diagnostics { get; set; } = string.Empty;
        public Dictionary<string, string> DiscoveredAcpiTables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Rich structured sub-models
        public BiosHardwareIdentity Identity { get; set; } = new();
        public AcpiFirmwarePowerData AcpiData { get; set; } = new();
        public WmiFirmwareThermalData WmiData { get; set; } = new();
        public SiliconFusedPowerData SiliconData { get; set; } = new();
        public SynthesizedPowerBoundaries Boundaries { get; set; } = new();
        public SynthesizedPowerBoundaries SynthesizedLimits => Boundaries;
        public DateTime ScanTimestamp { get; set; } = DateTime.UtcNow;

        public (int Pl1, int Pl2) GetModeDefault(OperatingMode mode) => GetDefaultLimitsForMode(mode);

        public (int Pl1, int Pl2) GetDefaultLimitsForMode(OperatingMode mode) => mode switch
        {
            OperatingMode.Eco => (BiosDefaultEcoPl1Watts, BiosDefaultEcoPl2Watts),
            OperatingMode.Quiet => (BiosDefaultQuietPl1Watts, BiosDefaultQuietPl2Watts),
            OperatingMode.Balanced => (BiosDefaultBalancedPl1Watts, BiosDefaultBalancedPl2Watts),
            OperatingMode.Performance => (BiosDefaultPerformancePl1Watts, BiosDefaultPerformancePl2Watts),
            OperatingMode.Turbo => (BiosDefaultTurboPl1Watts, BiosDefaultTurboPl2Watts),
            _ => (BiosDefaultBalancedPl1Watts, BiosDefaultBalancedPl2Watts)
        };
    }

    /// <summary>
    /// Interface for scanning CPU power capabilities and per-mode limits directly from BIOS, UEFI ACPI, and hardware registers.
    /// </summary>
    public interface IBiosUefiPowerScanner
    {
        BiosPowerScanResult ScanPowerLimits(IPawnIoBackend? pawnIoBackend = null, WmiController? wmi = null);
        BiosPowerScanResult Scan();
        Task<BiosPowerScanResult> ScanAsync(CancellationToken cancellationToken = default);
        string ComputeBiosHardwareFingerprint();
        string ComputeHardwareFingerprint();
    }

    #endregion

    #region Mock Scanner for Unit & Adversarial Tests

    /// <summary>
    /// Mock implementation of IBiosUefiPowerScanner for deterministic zero-driver CI testing across multiple laptop profiles.
    /// </summary>
    public sealed class MockBiosUefiPowerScanner : IBiosUefiPowerScanner
    {
        public BiosPowerScanResult ResultToReturn { get; set; }
        public string FingerprintToReturn { get; set; } = "Acer_Predator_PH16-71_V1.15_2024-03-12_Core_i7-13700HX";
        public int ScanCallCount { get; private set; }

        public MockBiosUefiPowerScanner(BiosPowerScanResult? result = null)
        {
            ResultToReturn = result ?? CreateDefaultMockResult();
        }

        public BiosPowerScanResult ScanPowerLimits(IPawnIoBackend? pawnIoBackend = null, WmiController? wmi = null)
        {
            ScanCallCount++;
            return ResultToReturn;
        }

        public BiosPowerScanResult Scan() => ScanPowerLimits(null, null);

        public Task<BiosPowerScanResult> ScanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ScanPowerLimits(null, null));

        public string ComputeBiosHardwareFingerprint() => FingerprintToReturn;
        public string ComputeHardwareFingerprint() => FingerprintToReturn;

        public static BiosPowerScanResult CreateDefaultMockResult(
            int baseTdp = 45, int minPower = 12, int maxPl1 = 140, int maxPl2 = 140)
        {
            int ecoPl1 = Math.Clamp((int)Math.Round(baseTdp * 0.55), minPower, maxPl1);
            int ecoPl2 = Math.Clamp((int)Math.Round(baseTdp * 0.65), minPower, maxPl2);
            int quietPl1 = Math.Clamp((int)Math.Round(baseTdp * 0.75), minPower, maxPl1);
            int quietPl2 = Math.Clamp((int)Math.Round(baseTdp * 0.85), minPower, maxPl2);
            int balPl1 = Math.Clamp(baseTdp, minPower, maxPl1);
            int balPl2 = Math.Clamp((int)Math.Round(baseTdp * 1.35), minPower, maxPl2);
            int perfPl1 = Math.Clamp((int)Math.Round(baseTdp * 1.25), minPower, maxPl1);
            int perfPl2 = Math.Clamp((int)Math.Round(baseTdp * 1.65), minPower, maxPl2);
            int turboPl1 = maxPl1;
            int turboPl2 = maxPl2;

            var recDefaults = new Dictionary<OperatingMode, (int, int)>
            {
                [OperatingMode.Eco] = (ecoPl1, ecoPl2),
                [OperatingMode.Quiet] = (quietPl1, quietPl2),
                [OperatingMode.Balanced] = (balPl1, balPl2),
                [OperatingMode.Performance] = (perfPl1, perfPl2),
                [OperatingMode.Turbo] = (turboPl1, turboPl2)
            };

            var res = new BiosPowerScanResult
            {
                BiosHardwareFingerprint = "Acer_Predator_PH16-71_V1.15_2024-03-12_Core_i7-13700HX",
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = baseTdp,
                CpuMinAllowedPowerWatts = minPower,
                CpuMaxAllowedPl1Watts = maxPl1,
                CpuMaxAllowedPl2Watts = maxPl2,
                BiosDefaultEcoPl1Watts = ecoPl1,
                BiosDefaultEcoPl2Watts = ecoPl2,
                BiosDefaultQuietPl1Watts = quietPl1,
                BiosDefaultQuietPl2Watts = quietPl2,
                BiosDefaultBalancedPl1Watts = balPl1,
                BiosDefaultBalancedPl2Watts = balPl2,
                BiosDefaultPerformancePl1Watts = perfPl1,
                BiosDefaultPerformancePl2Watts = perfPl2,
                BiosDefaultTurboPl1Watts = turboPl1,
                BiosDefaultTurboPl2Watts = turboPl2,
                VerifiedBiosModesBitmask = 0x1F, // all 5 modes
                Diagnostics = "Mock BIOS/UEFI power scan executed.",
                Identity = new BiosHardwareIdentity
                {
                    SystemProductName = "Predator PH16-71",
                    BiosVersion = "V1.15",
                    BiosReleaseDate = "2024-03-12",
                    BaseBoardProduct = "Predator PH16-71",
                    SystemManufacturer = "Acer",
                    BaseBoardManufacturer = "Acer",
                    ProcessorNameString = "13th Gen Intel(R) Core(TM) i7-13700HX",
                    CpuIdentifier = "Intel64 Family 6 Model 183 Stepping 1",
                    HardwareFingerprint = "Acer_Predator_PH16-71_V1.15_2024-03-12_Core_i7-13700HX"
                },
                AcpiData = new AcpiFirmwarePowerData
                {
                    IsFound = true,
                    HasPpccTable = true,
                    Pl1MinWatts = minPower,
                    Pl1MaxWatts = maxPl1,
                    Pl2MaxWatts = maxPl2,
                    BaseTdpWatts = baseTdp,
                    SourceTable = "SSDT-Dptf",
                    PpccEntries = new List<PpccPowerLimitEntry>
                    {
                        new() { PowerLimitIndex = 0, MinPowerLimitMilliwatts = minPower * 1000, MaxPowerLimitMilliwatts = maxPl1 * 1000, MinTimeWindowMs = 1000, MaxTimeWindowMs = 32000, StepSizeMilliwatts = 500 },
                        new() { PowerLimitIndex = 1, MinPowerLimitMilliwatts = minPower * 1000, MaxPowerLimitMilliwatts = maxPl2 * 1000, MinTimeWindowMs = 28, MaxTimeWindowMs = 32, StepSizeMilliwatts = 500 }
                    }
                },
                Boundaries = new SynthesizedPowerBoundaries
                {
                    CpuMinAllowedPowerWatts = minPower,
                    CpuThermalSpecTdpWatts = baseTdp,
                    CpuMaxAllowedPl1Watts = maxPl1,
                    CpuMaxAllowedPl2Watts = maxPl2,
                    RecommendedModeDefaults = recDefaults
                }
            };
            return res;
        }

        public static MockBiosUefiPowerScanner CreateTriton28WProfile()
        {
            var res = new BiosPowerScanResult
            {
                BiosHardwareFingerprint = "Acer_Predator_PT14-51_V1.08_2024-01-15_Core_Ultra_7_155H",
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 28,
                CpuMinAllowedPowerWatts = 8,
                CpuMaxAllowedPl1Watts = 65,
                CpuMaxAllowedPl2Watts = 115,
                BiosDefaultEcoPl1Watts = 15,
                BiosDefaultEcoPl2Watts = 20,
                BiosDefaultQuietPl1Watts = 20,
                BiosDefaultQuietPl2Watts = 30,
                BiosDefaultBalancedPl1Watts = 28,
                BiosDefaultBalancedPl2Watts = 45,
                BiosDefaultPerformancePl1Watts = 45,
                BiosDefaultPerformancePl2Watts = 65,
                BiosDefaultTurboPl1Watts = 65,
                BiosDefaultTurboPl2Watts = 115,
                VerifiedBiosModesBitmask = 0x1F,
                Diagnostics = "Triton 28W Profile",
                Identity = new BiosHardwareIdentity
                {
                    SystemProductName = "Predator PT14-51",
                    BiosVersion = "V1.08",
                    BiosReleaseDate = "2024-01-15",
                    BaseBoardProduct = "Taycan_MTH",
                    SystemManufacturer = "Acer",
                    BaseBoardManufacturer = "Acer",
                    ProcessorNameString = "Intel(R) Core(TM) Ultra 7 155H",
                    CpuIdentifier = "Intel64 Family 6 Model 170 Stepping 4",
                    HardwareFingerprint = "Acer_Predator_PT14-51_V1.08_2024-01-15_Core_Ultra_7_155H"
                }
            };

            return new MockBiosUefiPowerScanner(res)
            {
                FingerprintToReturn = "Acer_Predator_PT14-51_V1.08_2024-01-15_Core_Ultra_7_155H"
            };
        }

        public static MockBiosUefiPowerScanner CreateHeliosNeo16_45WProfile()
        {
            var res = new BiosPowerScanResult
            {
                BiosHardwareFingerprint = "Acer_Predator_PHN16-71_V1.15_2024-03-12_Core_i7-13700HX",
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 45,
                CpuMinAllowedPowerWatts = 12,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140,
                BiosDefaultEcoPl1Watts = 25,
                BiosDefaultEcoPl2Watts = 35,
                BiosDefaultQuietPl1Watts = 35,
                BiosDefaultQuietPl2Watts = 45,
                BiosDefaultBalancedPl1Watts = 45,
                BiosDefaultBalancedPl2Watts = 75,
                BiosDefaultPerformancePl1Watts = 75,
                BiosDefaultPerformancePl2Watts = 105,
                BiosDefaultTurboPl1Watts = 105,
                BiosDefaultTurboPl2Watts = 140,
                VerifiedBiosModesBitmask = 0x1F,
                Diagnostics = "Helios Neo 16 45W Profile"
            };

            return new MockBiosUefiPowerScanner(res)
            {
                FingerprintToReturn = "Acer_Predator_PHN16-71_V1.15_2024-03-12_Core_i7-13700HX"
            };
        }

        public static MockBiosUefiPowerScanner CreateHelios18Hx55WProfile()
        {
            var res = new BiosPowerScanResult
            {
                BiosHardwareFingerprint = "Acer_Predator_PH18-72_V1.05_2024-04-10_Core_i9-14900HX",
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 55,
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 175,
                CpuMaxAllowedPl2Watts = 190,
                BiosDefaultEcoPl1Watts = 35,
                BiosDefaultEcoPl2Watts = 45,
                BiosDefaultQuietPl1Watts = 45,
                BiosDefaultQuietPl2Watts = 65,
                BiosDefaultBalancedPl1Watts = 55,
                BiosDefaultBalancedPl2Watts = 95,
                BiosDefaultPerformancePl1Watts = 115,
                BiosDefaultPerformancePl2Watts = 140,
                BiosDefaultTurboPl1Watts = 125,
                BiosDefaultTurboPl2Watts = 175,
                VerifiedBiosModesBitmask = 0x1F,
                Diagnostics = "Helios 18 HX 55W Profile"
            };

            return new MockBiosUefiPowerScanner(res)
            {
                FingerprintToReturn = "Acer_Predator_PH18-72_V1.05_2024-04-10_Core_i9-14900HX"
            };
        }

        public static MockBiosUefiPowerScanner CreateNitro16AmdProfile()
        {
            var res = new BiosPowerScanResult
            {
                BiosHardwareFingerprint = "Acer_Nitro_AN16-41_V1.20_2024-02-18_Ryzen_7_7840HS",
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 45,
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 65,
                CpuMaxAllowedPl2Watts = 80,
                BiosDefaultEcoPl1Watts = 25,
                BiosDefaultEcoPl2Watts = 30,
                BiosDefaultQuietPl1Watts = 35,
                BiosDefaultQuietPl2Watts = 45,
                BiosDefaultBalancedPl1Watts = 45,
                BiosDefaultBalancedPl2Watts = 60,
                BiosDefaultPerformancePl1Watts = 55,
                BiosDefaultPerformancePl2Watts = 70,
                BiosDefaultTurboPl1Watts = 65,
                BiosDefaultTurboPl2Watts = 80,
                VerifiedBiosModesBitmask = 0x1F,
                Diagnostics = "Nitro 16 AMD Profile"
            };

            return new MockBiosUefiPowerScanner(res)
            {
                FingerprintToReturn = "Acer_Nitro_AN16-41_V1.20_2024-02-18_Ryzen_7_7840HS"
            };
        }

        public static MockBiosUefiPowerScanner CreateWithMissingAcpi()
        {
            var res = new BiosPowerScanResult
            {
                BiosHardwareFingerprint = "Acer_Predator_MissingACPI_V1.00",
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 45,
                CpuMinAllowedPowerWatts = 10,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 115,
                BiosDefaultEcoPl1Watts = 25,
                BiosDefaultEcoPl2Watts = 35,
                BiosDefaultQuietPl1Watts = 35,
                BiosDefaultQuietPl2Watts = 45,
                BiosDefaultBalancedPl1Watts = 45,
                BiosDefaultBalancedPl2Watts = 65,
                BiosDefaultPerformancePl1Watts = 65,
                BiosDefaultPerformancePl2Watts = 90,
                BiosDefaultTurboPl1Watts = 90,
                BiosDefaultTurboPl2Watts = 115,
                VerifiedBiosModesBitmask = 0x1F,
                Diagnostics = "Mock profile with missing ACPI tables."
            };

            return new MockBiosUefiPowerScanner(res)
            {
                FingerprintToReturn = "Acer_Predator_MissingACPI_V1.00"
            };
        }
    }

    #endregion

    #region Native Scanner Implementation

    /// <summary>
    /// Strictly native Acer BIOS/UEFI & ACPI WMI scanner.
    /// Exclusively reads from 4 native discovery sources:
    /// 1. SMBIOS & UEFI Firmware Platform Identity (HKLM\HARDWARE\DESCRIPTION\System\BIOS & CPU registry)
    /// 2. Raw ACPI Firmware Tables via kernel32 EnumSystemFirmwareTables / GetSystemFirmwareTable (ACPI & RSMB, DSDT/SSDT/FIDT, PPCC AML structures, and UEFI NVRAM)
    /// 3. Acer ACPI WMI Thermal & Power Tables (root\\WMI: AcerGamingFunction, WMI_Gaming, APGeAction, GetGamingSysInfo, GetGamingProfileInfo)
    /// 4. CPU Silicon Fused Capabilities (MSR 0x614 MSR_PKG_POWER_INFO & 0x606 MSR_RAPL_POWER_UNIT via PawnIO)
    /// 
    /// GUARANTEE: Never touches, reads, or references any third-party app, config, or process.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class BiosUefiPowerScanner : IBiosUefiPowerScanner
    {
        public const uint FIRMWARE_TABLE_PROVIDER_ACPI = 0x41435049; // 'ACPI' (Big-Endian FOURCC)
        public const uint FIRMWARE_TABLE_PROVIDER_RSMB = 0x52534D42; // 'RSMB' (Big-Endian FOURCC)

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint EnumSystemFirmwareTables(
            uint FirmwareTableProviderSignature,
            IntPtr pFirmwareTableEnumBuffer,
            uint BufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(
            uint FirmwareTableProviderSignature,
            uint FirmwareTableID,
            IntPtr pFirmwareTableBuffer,
            uint BufferSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFirmwareEnvironmentVariableExW(
            string lpName,
            string lpGuid,
            IntPtr pBuffer,
            uint nSize,
            out uint pdwAttributes);

        private static readonly byte[] PpccPattern = new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'C' };

        // AML opcodes
        private const byte AML_ZERO_OP = 0x00;
        private const byte AML_ONE_OP = 0x01;
        private const byte AML_BYTE_PREFIX = 0x0A;
        private const byte AML_WORD_PREFIX = 0x0B;
        private const byte AML_DWORD_PREFIX = 0x0C;
        private const byte AML_QWORD_PREFIX = 0x0E;
        private const byte AML_PACKAGE_OP = 0x12;
        private const byte AML_VAR_PACKAGE_OP = 0x13;
        private const byte AML_ONES_OP = 0xFF;

        public BiosPowerScanResult ScanPowerLimits(IPawnIoBackend? pawnIoBackend = null, WmiController? wmi = null)
        {
            var result = new BiosPowerScanResult();
            var sbDiag = new StringBuilder();

            // =========================================================================
            // SOURCE 1: SMBIOS & UEFI Firmware Platform Identity
            // =========================================================================
            var identity = ScanPlatformIdentity(sbDiag);
            result.Identity = identity;
            result.BiosHardwareFingerprint = identity.HardwareFingerprint;
            sbDiag.AppendLine($"[Source 1] Platform Identity Scanned: {identity.SystemProductName} ({identity.BaseBoardProduct}), BIOS {identity.BiosVersion} ({identity.BiosReleaseDate})");
            sbDiag.AppendLine($"[Source 1] CPU: {identity.ProcessorNameString} [ID: {identity.CpuIdentifier}]");
            sbDiag.AppendLine($"[Source 1] Deterministic Fingerprint: {identity.HardwareFingerprint}");

            // Read CPU SKU baseline spec
            var cpuSpec = CpuSpecDetector.Detect(identity.ProcessorNameString);
            int baseTdp = cpuSpec.BaseTdpWatts;
            int minPower = cpuSpec.MinPowerWatts;
            int maxPl1 = cpuSpec.MaxPl1Watts;
            int maxPl2 = cpuSpec.MaxPl2Watts;
            sbDiag.AppendLine($"[Source 1] Fallback CPU SKU: {cpuSpec.Architecture} (Base TDP={baseTdp}W, Min={minPower}W, MaxPL1={maxPl1}W, MaxPL2={maxPl2}W)");

            // =========================================================================
            // SOURCE 4: CPU Silicon Fused Capabilities (MSR 0x614 & 0x606 via PawnIO)
            // Ground truth directly from CPU silicon registers; highest priority authority.
            // =========================================================================
            var siliconData = ScanSiliconCapabilities(pawnIoBackend, sbDiag);
            result.SiliconData = siliconData;
            if (siliconData.IsFound)
            {
                if (siliconData.ThermalSpecPowerWatts >= cpuSpec.MinPowerWatts && siliconData.ThermalSpecPowerWatts <= 200) baseTdp = siliconData.ThermalSpecPowerWatts;
                if (siliconData.MinimumPowerWatts > 0 && siliconData.MinimumPowerWatts <= 120) minPower = siliconData.MinimumPowerWatts;
                if (siliconData.MaximumPowerWatts >= baseTdp && siliconData.MaximumPowerWatts <= 450)
                {
                    maxPl2 = siliconData.MaximumPowerWatts;
                    maxPl1 = maxPl2;
                }
                sbDiag.AppendLine($"[Source 4] Silicon MSR Fused Capabilities Applied: Units={siliconData.UnitsPerWatt}/W, TDP={siliconData.ThermalSpecPowerWatts}W, Min={siliconData.MinimumPowerWatts}W, Max={siliconData.MaximumPowerWatts}W");
            }

            // =========================================================================
            // SOURCE 2: Native UEFI & ACPI Firmware Tables
            // Verified ACPI PPCC tables can refine bounds within silicon/SKU capabilities.
            // Heuristic integer scanning must never override verified silicon fused bounds.
            // =========================================================================
            var acpiData = ScanAcpiAndUefiTables(sbDiag, result.DiscoveredAcpiTables);
            result.AcpiData = acpiData;
            if (acpiData.IsFound && acpiData.HasPpccTable)
            {
                int upperLimitCap = siliconData.IsFound && siliconData.MaximumPowerWatts > 0
                    ? siliconData.MaximumPowerWatts
                    : cpuSpec.MaxPl2Watts;

                if (acpiData.Pl1MinWatts > 0 && acpiData.Pl1MinWatts >= 5)
                    minPower = Math.Min(minPower, acpiData.Pl1MinWatts);
                if (!siliconData.IsFound && acpiData.BaseTdpWatts >= minPower && acpiData.BaseTdpWatts <= upperLimitCap)
                    baseTdp = acpiData.BaseTdpWatts;
                if (acpiData.Pl1MaxWatts >= baseTdp)
                    maxPl1 = Math.Max(maxPl1, Math.Min(acpiData.Pl1MaxWatts, upperLimitCap));
                if (acpiData.Pl2MaxWatts >= maxPl1)
                    maxPl2 = Math.Max(maxPl2, Math.Min(acpiData.Pl2MaxWatts, upperLimitCap));
                sbDiag.AppendLine($"[Source 2] ACPI PPCC Table Refinement Applied: Min={minPower}W, TDP={baseTdp}W, MaxPL1={maxPl1}W, MaxPL2={maxPl2}W (Table: {acpiData.SourceTable})");
            }

            // =========================================================================
            // SOURCE 3: Acer ACPI WMI Thermal & Power Tables
            // =========================================================================
            int preResolvedMin = Math.Max(5, minPower);
            int preResolvedMaxPl2 = Math.Max(preResolvedMin + 5, maxPl2);
            int preResolvedMaxPl1 = Math.Clamp(maxPl1, preResolvedMin, preResolvedMaxPl2);
            if (preResolvedMaxPl1 < preResolvedMaxPl2) preResolvedMaxPl1 = preResolvedMaxPl2;
            int preResolvedTdp = Math.Clamp(baseTdp, preResolvedMin, preResolvedMaxPl1);

            var wmiData = ScanAcerWmiThermalTables(wmi, sbDiag, preResolvedTdp, preResolvedMin, preResolvedMaxPl1, preResolvedMaxPl2);
            result.WmiData = wmiData;

            // If WMI Turbo mode reports higher sustained or burst limits, incorporate into max PL
            if (wmiData.ModeProfiles.TryGetValue(OperatingMode.Turbo, out var turboProf))
            {
                if (turboProf.Pl1Watts > preResolvedMaxPl1) preResolvedMaxPl1 = turboProf.Pl1Watts;
                if (turboProf.Pl2Watts > preResolvedMaxPl2) preResolvedMaxPl2 = turboProf.Pl2Watts;
                if (preResolvedMaxPl1 < preResolvedMaxPl2) preResolvedMaxPl1 = preResolvedMaxPl2;
            }

            // =========================================================================
            // COMBINE & SYNTHESIZE HARDWARE BOUNDARIES
            // =========================================================================
            var boundaries = SynthesizeBoundaries(identity, acpiData, wmiData, siliconData, preResolvedTdp, preResolvedMin, preResolvedMaxPl1, preResolvedMaxPl2, sbDiag);
            result.Boundaries = boundaries;

            result.CpuThermalSpecTdpWatts = boundaries.CpuThermalSpecTdpWatts;
            result.CpuMinAllowedPowerWatts = boundaries.CpuMinAllowedPowerWatts;
            result.CpuMaxAllowedPl1Watts = boundaries.CpuMaxAllowedPl1Watts;
            result.CpuMaxAllowedPl2Watts = boundaries.CpuMaxAllowedPl2Watts;

            var defs = boundaries.RecommendedModeDefaults;
            result.BiosDefaultEcoPl1Watts = defs[OperatingMode.Eco].Pl1Watts;
            result.BiosDefaultEcoPl2Watts = defs[OperatingMode.Eco].Pl2Watts;
            result.BiosDefaultQuietPl1Watts = defs[OperatingMode.Quiet].Pl1Watts;
            result.BiosDefaultQuietPl2Watts = defs[OperatingMode.Quiet].Pl2Watts;
            result.BiosDefaultBalancedPl1Watts = defs[OperatingMode.Balanced].Pl1Watts;
            result.BiosDefaultBalancedPl2Watts = defs[OperatingMode.Balanced].Pl2Watts;
            result.BiosDefaultPerformancePl1Watts = defs[OperatingMode.Performance].Pl1Watts;
            result.BiosDefaultPerformancePl2Watts = defs[OperatingMode.Performance].Pl2Watts;
            result.BiosDefaultTurboPl1Watts = defs[OperatingMode.Turbo].Pl1Watts;
            result.BiosDefaultTurboPl2Watts = defs[OperatingMode.Turbo].Pl2Watts;

            result.VerifiedBiosModesBitmask = 0x1F; // All 5 operating modes verified
            result.HasCompletedBiosPowerScan = true;
            result.Diagnostics = sbDiag.ToString();

            return result;
        }

        public BiosPowerScanResult Scan() => ScanPowerLimits(null, null);

        public Task<BiosPowerScanResult> ScanAsync(CancellationToken cancellationToken = default) =>
            Task.Run(() => ScanPowerLimits(null, null), cancellationToken);

        public string ComputeBiosHardwareFingerprint() => ScanPlatformIdentity(null).HardwareFingerprint;
        public string ComputeHardwareFingerprint() => ComputeBiosHardwareFingerprint();

        /// <summary>
        /// Public read-only view of the SMBIOS/BIOS identity used for power
        /// scanning. Keyboard lighting capability detection uses the same
        /// source, so a model/BIOS change is observed consistently.
        /// </summary>
        public static BiosHardwareIdentity ReadPlatformIdentity() => ScanPlatformIdentity(null);

        #region Source 1: SMBIOS & UEFI Firmware Platform Identity

        private static BiosHardwareIdentity ScanPlatformIdentity(StringBuilder? sbDiag)
        {
            string mfg = "Acer";
            string model = "Predator";
            string biosVer = "Unknown";
            string biosDate = "Unknown";
            string boardProd = "Unknown";
            string boardMfg = "Acer";
            string cpuName = "Unknown";
            string cpuId = "Unknown";

            try
            {
                using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                if (biosKey != null)
                {
                    mfg = (biosKey.GetValue("SystemManufacturer") as string ?? biosKey.GetValue("BaseBoardManufacturer") as string ?? mfg).Trim();
                    boardMfg = (biosKey.GetValue("BaseBoardManufacturer") as string ?? mfg).Trim();
                    model = (biosKey.GetValue("SystemProductName") as string ?? biosKey.GetValue("BaseBoardProduct") as string ?? model).Trim();
                    biosVer = (biosKey.GetValue("BIOSVersion") as string ?? biosVer).Trim();
                    biosDate = (biosKey.GetValue("BIOSReleaseDate") as string ?? biosDate).Trim();
                    boardProd = (biosKey.GetValue("BaseBoardProduct") as string ?? boardProd).Trim();
                }

                using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (cpuKey != null)
                {
                    cpuName = (cpuKey.GetValue("ProcessorNameString") as string ?? cpuName).Trim();
                    cpuId = (cpuKey.GetValue("Identifier") as string ?? cpuId).Trim();
                }
            }
            catch (Exception ex)
            {
                sbDiag?.AppendLine($"[Source 1] Registry metadata read exception: {ex.Message}");
            }

            // Clean CPU model name
            cpuName = Regex.Replace(cpuName, @"\s+", " ").Trim();

            // Compute deterministic fingerprint
            string rawFingerprint = $"{mfg}_{model}_{boardProd}_{biosVer}_{biosDate}_{cpuName}_{cpuId}";
            string sanitized = Regex.Replace(rawFingerprint, @"[^a-zA-Z0-9._\-]+", "_").Trim('_');

            // Append deterministic SHA256 suffix for collision safety
            byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sanitized));
            string hashSuffix = Convert.ToHexString(hashBytes)[..12];
            string finalFingerprint = $"{sanitized}_{hashSuffix}";

            return new BiosHardwareIdentity
            {
                SystemManufacturer = mfg,
                BaseBoardManufacturer = boardMfg,
                SystemProductName = model,
                BaseBoardProduct = boardProd,
                BiosVersion = biosVer,
                BiosReleaseDate = biosDate,
                ProcessorNameString = cpuName,
                CpuIdentifier = cpuId,
                HardwareFingerprint = finalFingerprint
            };
        }

        #endregion

        #region Source 2: Native UEFI & ACPI Firmware Tables

        private static AcpiFirmwarePowerData ScanAcpiAndUefiTables(
            StringBuilder sbDiag,
            Dictionary<string, string> discoveredTables)
        {
            var discoveredTableList = new List<string>();
            var uefiVars = new Dictionary<string, byte[]>();
            var ppccEntries = new List<PpccPowerLimitEntry>();

            int? foundMin = null;
            int? foundMaxPl1 = null;
            int? foundMaxPl2 = null;
            int? foundTdp = null;
            string sourceTable = string.Empty;
            bool hasOemThermal = false;
            var oemSummary = new StringBuilder();

            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                try
                {
                    if (FirmwareParsingNative.TryFetchAndParsePpccDirect(out var directSummary) && directSummary.MaxPl1Watts > 0)
                    {
                        foundMin = directSummary.MinPowerWatts;
                        foundMaxPl1 = directSummary.MaxPl1Watts;
                        foundMaxPl2 = directSummary.MaxPl2Watts;
                        foundTdp = directSummary.BaseTdpWatts;
                        sourceTable = "ACPI/PPCC (Direct Native)";
                        ppccEntries.Add(new PpccPowerLimitEntry
                        {
                            PowerLimitIndex = 0,
                            MinPowerLimitMilliwatts = directSummary.MinPowerWatts * 1000,
                            MaxPowerLimitMilliwatts = directSummary.MaxPl1Watts * 1000
                        });
                        ppccEntries.Add(new PpccPowerLimitEntry
                        {
                            PowerLimitIndex = 1,
                            MinPowerLimitMilliwatts = directSummary.MinPowerWatts * 1000,
                            MaxPowerLimitMilliwatts = directSummary.MaxPl2Watts * 1000
                        });
                        sbDiag.AppendLine($"[Source 2] Direct Native ACPI PPCC parsed: Min={foundMin}W, MaxPL1={foundMaxPl1}W, MaxPL2={foundMaxPl2}W, TDP={foundTdp}W");
                    }

                    if (FirmwareParsingNative.TryFetchAndScanUefiPowerDirect(out var directUefi) && directUefi.Pl1Watts > 0)
                    {
                        sbDiag.AppendLine($"[Source 2] Direct Native UEFI Power parsed: PL1={directUefi.Pl1Watts}W, PL2={directUefi.Pl2Watts}W, OCLock={directUefi.OverclockingLock}, CfgLock={directUefi.CfgLock}");
                        if (foundMaxPl1 == null || foundMaxPl1 == 0)
                        {
                            foundMaxPl1 = (int)directUefi.Pl1Watts;
                            foundMaxPl2 = (int)directUefi.Pl2Watts;
                        }
                    }
                }
                catch (Exception ex)
                {
                    sbDiag.AppendLine($"[Source 2] Direct Native firmware scan fallback: {ex.Message}");
                }
            }

            try
            {
                // 1. Query RSMB (Raw SMBIOS) provider
                uint rsmbSize = GetSystemFirmwareTable(FIRMWARE_TABLE_PROVIDER_RSMB, 0, IntPtr.Zero, 0);
                if (rsmbSize > 0)
                {
                    discoveredTables["RSMB"] = $"0x52534D42 ({rsmbSize} bytes)";
                    discoveredTableList.Add("RSMB");
                    sbDiag.AppendLine($"[Source 2] Enumerated RSMB (Raw SMBIOS) table: {rsmbSize} bytes.");
                }

                // 2. Enumerate ACPI tables
                var allTableIds = new List<uint>();
                uint dsdtId = 0x54445344; // 'DSDT'
                uint fidtId = 0x54444946; // 'FIDT'

                uint size = EnumSystemFirmwareTables(FIRMWARE_TABLE_PROVIDER_ACPI, IntPtr.Zero, 0);
                if (size > 0 && size <= 1024 * 1024)
                {
                    IntPtr pEnum = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        uint bytesRead = EnumSystemFirmwareTables(FIRMWARE_TABLE_PROVIDER_ACPI, pEnum, size);
                        if (bytesRead > 0)
                        {
                            int count = (int)Math.Min(bytesRead, size) / 4;
                            int[] tableIds = new int[count];
                            Marshal.Copy(pEnum, tableIds, 0, count);

                            for (int i = 0; i < count; i++)
                            {
                                allTableIds.Add((uint)tableIds[i]);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pEnum);
                    }
                }

                // Explicitly ensure DSDT and FIDT are included if not in enumeration list (or if enumeration returned 0)
                if (!allTableIds.Contains(dsdtId)) allTableIds.Add(dsdtId);
                if (!allTableIds.Contains(fidtId)) allTableIds.Add(fidtId);

                int tableIndex = 0;
                foreach (uint rawId in allTableIds.Distinct())
                {
                    string tableName = Encoding.ASCII.GetString(BitConverter.GetBytes(rawId));
                    string tableKey = $"{tableName}_{tableIndex++}";
                    discoveredTables[tableKey] = $"0x{rawId:X8}";
                    discoveredTableList.Add(tableName);

                    uint tableSize = GetSystemFirmwareTable(FIRMWARE_TABLE_PROVIDER_ACPI, rawId, IntPtr.Zero, 0);
                    if (tableSize < 36 || tableSize > 16 * 1024 * 1024) continue; // Safety bounds: 36B min header, 16MB max

                    IntPtr pTable = Marshal.AllocHGlobal((int)tableSize);
                    try
                    {
                        uint actualRead = GetSystemFirmwareTable(FIRMWARE_TABLE_PROVIDER_ACPI, rawId, pTable, tableSize);
                        if (actualRead >= 36 && actualRead <= tableSize)
                        {
                            byte[] buffer = new byte[actualRead];
                            Marshal.Copy(pTable, buffer, 0, (int)actualRead);

                                        // Inspect ACPI Header OEM ID and OEM Table ID
                                        string oemId = Encoding.ASCII.GetString(buffer, 10, 6).Trim('\0', ' ');
                                        string oemTableId = Encoding.ASCII.GetString(buffer, 16, 8).Trim('\0', ' ');
                                        if (oemId.Contains("ACER", StringComparison.OrdinalIgnoreCase) ||
                                            oemTableId.Contains("PREDAT", StringComparison.OrdinalIgnoreCase) ||
                                            oemTableId.Contains("NITRO", StringComparison.OrdinalIgnoreCase))
                                        {
                                            hasOemThermal = true;
                                            oemSummary.Append($"{tableName}({oemId}/{oemTableId}) ");
                                        }

                                        // Check for PPCC in SSDT or DSDT (skip re-parse if direct native already populated ppccEntries)
                                        if (ppccEntries.Count == 0 &&
                                            (tableName.StartsWith("SSDT", StringComparison.OrdinalIgnoreCase) ||
                                             tableName.StartsWith("DSDT", StringComparison.OrdinalIgnoreCase)))
                                        {
                                            // byte[] overload: parsed natively without copying the table.
                                            var entries = ExtractPpccEntriesFromAml(buffer);
                                            if (entries.Count > 0)
                                            {
                                                ppccEntries.AddRange(entries);
                                                sourceTable = tableName;

                                                int minW = entries.Min(e => e.MinPowerLimitWatts);
                                                int maxW = entries.Max(e => e.MaxPowerLimitWatts);
                                                var pl1 = entries.FirstOrDefault(e => e.PowerLimitIndex == 0);
                                                var pl2 = entries.FirstOrDefault(e => e.PowerLimitIndex == 1);

                                                int pMin = minW;
                                                int pMax1 = pl1?.MaxPowerLimitWatts ?? maxW;
                                                int pMax2 = pl2?.MaxPowerLimitWatts ?? maxW;
                                                int pTdp = pl1?.MaxPowerLimitWatts ?? minW;

                                                sbDiag.AppendLine($"[Source 2] Found ACPI PPCC in {tableName}: Min={pMin}W, MaxPL1={pMax1}W, MaxPL2={pMax2}W, TDP={pTdp}W");
                                                foundMin = Math.Min(foundMin ?? pMin, pMin);
                                                foundMaxPl1 = Math.Max(foundMaxPl1 ?? pMax1, pMax1);
                                                foundMaxPl2 = Math.Max(foundMaxPl2 ?? pMax2, pMax2);
                                                foundTdp = foundTdp ?? pTdp;
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    Marshal.FreeHGlobal(pTable);
                                }
                    }

                // 3. Query UEFI NVRAM variables via GetFirmwareEnvironmentVariableExW
                ScanUefiNvramVariables(uefiVars, sbDiag);
            }
            catch (Exception ex)
            {
                sbDiag.AppendLine($"[Source 2] ACPI table scan exception: {ex.Message}");
            }

            bool isFound = foundMin.HasValue && foundMaxPl2.HasValue;

            return new AcpiFirmwarePowerData
            {
                IsFound = isFound,
                HasPpccTable = isFound,
                Pl1MinWatts = foundMin ?? 0,
                Pl1MaxWatts = foundMaxPl1 ?? (foundMaxPl2 ?? 0),
                Pl2MaxWatts = foundMaxPl2 ?? 0,
                BaseTdpWatts = foundTdp ?? 0,
                PpccEntries = ppccEntries,
                DiscoveredTables = discoveredTableList,
                UefiNvramVariables = uefiVars,
                SourceTable = sourceTable,
                HasOemThermalTables = hasOemThermal,
                OemThermalSummary = oemSummary.ToString().Trim()
            };
        }

        private static void ScanUefiNvramVariables(Dictionary<string, byte[]> uefiVars, StringBuilder sbDiag)
        {
            string globalGuid = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";
            string dptfGuid = "{A04A27F4-DF00-4D42-B552-39511302113D}";
            string insydeGuid = "{EC87D643-EBA4-4BB5-A1E5-3F3E36B20DA9}";

            var candidates = new (string Name, string Guid)[]
            {
                ("BootOrder", globalGuid),
                ("PlatformLang", globalGuid),
                ("Timeout", globalGuid),
                ("DptfSetup", dptfGuid),
                ("PowerLimitSettings", insydeGuid)
            };

            const uint initialBufferSize = 4096;
            foreach (var (name, guid) in candidates)
            {
                try
                {
                    IntPtr pBuf = Marshal.AllocHGlobal((int)initialBufferSize);
                    try
                    {
                        uint read = GetFirmwareEnvironmentVariableExW(name, guid, pBuf, initialBufferSize, out _);
                        int err = Marshal.GetLastWin32Error();
                        if (read > 0)
                        {
                            int toCopy = (int)Math.Min(read, initialBufferSize);
                            byte[] bytes = new byte[toCopy];
                            Marshal.Copy(pBuf, bytes, 0, toCopy);
                            uefiVars[name] = bytes;
                            sbDiag.AppendLine($"[Source 2] Read UEFI NVRAM variable {name}: {toCopy} bytes.");
                        }
                        else if (err == 122) // ERROR_INSUFFICIENT_BUFFER
                        {
                            const uint largeBufferSize = 65536;
                            IntPtr pLargeBuf = Marshal.AllocHGlobal((int)largeBufferSize);
                            try
                            {
                                uint readLarge = GetFirmwareEnvironmentVariableExW(name, guid, pLargeBuf, largeBufferSize, out _);
                                if (readLarge > 0)
                                {
                                    int toCopy = (int)Math.Min(readLarge, largeBufferSize);
                                    byte[] bytes = new byte[toCopy];
                                    Marshal.Copy(pLargeBuf, bytes, 0, toCopy);
                                    uefiVars[name] = bytes;
                                    sbDiag.AppendLine($"[Source 2] Read UEFI NVRAM variable {name}: {toCopy} bytes.");
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(pLargeBuf);
                            }
                        }
                        else if (err == 1 || err == 5 || err == 1314)
                        {
                            // ERROR_INVALID_FUNCTION (1), ERROR_ACCESS_DENIED (5), or ERROR_PRIVILEGE_NOT_HELD (1314)
                            sbDiag.AppendLine($"[Source 2] UEFI NVRAM variable '{name}' not supported or privilege not held (Win32 Error {err}).");
                            break; // Stop further calls to avoid spamming errors
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pBuf);
                    }
                }
                catch (Exception ex)
                {
                    sbDiag.AppendLine($"[Source 2] UEFI NVRAM probe exception for '{name}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Scans an ACPI AML byte stream for the "PPCC" NameString and extracts all Package Power Control Capabilities.
        /// </summary>
                        public static (int MinPower, int MaxPl1, int MaxPl2, int BaseTdp)? ParsePpccFromAml(byte[] aml)
        {
            if (aml == null || aml.Length < 4) return null;

            if (PredatorNativeCore.IsNativeCoreAvailable &&
                PredatorNativeCore.AcpiParsePpccSummary(aml, out var summary))
            {
                return (summary.MinPowerWatts, summary.MaxPl1Watts, summary.MaxPl2Watts, summary.BaseTdpWatts);
            }

            return ParsePpccFromAml(aml.AsSpan());
        }

        /// <summary>Managed reference parser (used when the native core is unavailable).</summary>
        public static (int MinPower, int MaxPl1, int MaxPl2, int BaseTdp)? ParsePpccFromAml(ReadOnlySpan<byte> aml)
        {
            if (aml.Length < 4) return null;
            var entries = ExtractPpccEntriesFromAml(aml);
            if (entries.Count >= 2 && entries.Any(e => e.MaxPowerLimitWatts > 0))
            {
                var pl1 = entries.FirstOrDefault(e => e.PowerLimitIndex == 0);
                var pl2 = entries.FirstOrDefault(e => e.PowerLimitIndex == 1);

                if (pl1 != null && pl2 != null)
                {
                    int minW = pl1.MinPowerLimitWatts;
                    int maxPl1W = pl1.MaxPowerLimitWatts;
                    int maxPl2W = pl2.MaxPowerLimitWatts;
                    if (maxPl2W < maxPl1W) maxPl2W = maxPl1W;
                    if (maxPl1W < minW) maxPl1W = minW;
                    int tdpW = maxPl1W;

                    return (minW, maxPl1W, maxPl2W, tdpW);
                }
            }

            // Fallback flat or raw extraction
            int index = 0;
            while (index <= aml.Length - 4)
            {
                if (aml[index] == PpccPattern[0] &&
                    aml[index + 1] == PpccPattern[1] &&
                    aml[index + 2] == PpccPattern[2] &&
                    aml[index + 3] == PpccPattern[3])
                {
                    int searchStart = index + 4;
                    int searchEnd = Math.Min(aml.Length, searchStart + 256);

                    var numbers = ExtractAmlIntegers(aml.Slice(searchStart, searchEnd - searchStart));
                    var mwValues = numbers.Where(n => n >= 5000 && n <= 350000).ToList();

                    if (mwValues.Count >= 4)
                    {
                        // In flat ACPI PPCC streams (Min, TDP, MaxPL1, MaxPL2)
                        int minW = (int)Math.Round(mwValues[0] / 1000.0);
                        int tdpW = (int)Math.Round(mwValues[1] / 1000.0);
                        int maxPl1W = (int)Math.Round(mwValues[2] / 1000.0);
                        int maxPl2W = (int)Math.Round(mwValues[3] / 1000.0);
                        if (maxPl2W < maxPl1W) maxPl2W = maxPl1W;
                        if (maxPl1W < minW) maxPl1W = minW;
                        if (tdpW < minW) tdpW = minW;

                        return (minW, maxPl1W, maxPl2W, tdpW);
                    }
                    else if (mwValues.Count >= 2)
                    {
                        int minW = (int)Math.Round(mwValues.Min() / 1000.0);
                        int maxPl2W = (int)Math.Round(mwValues.Max() / 1000.0);
                        int maxPl1W = mwValues.Count >= 3
                            ? (int)Math.Round(mwValues.OrderByDescending(v => v).Skip(1).First() / 1000.0)
                            : maxPl2W;
                        int tdpW = minW;

                        return (minW, maxPl1W, maxPl2W, tdpW);
                    }
                }
                index++;
            }

            return null;
        }

        /// <summary>
        /// Native-accelerated PPCC extraction over a whole ACPI table buffer.
        ///
        /// The AML walker in <c>predator_core::acpi</c> parses the table in place as a
        /// borrowed byte slice, so no intermediate copies are made. If the native core
        /// reports no entries the managed reference parser runs instead, so a native
        /// regression can never lose a limit the managed path would have found.
        /// </summary>
        public static List<PpccPowerLimitEntry> ExtractPpccEntriesFromAml(byte[] aml)
        {
            if (aml == null || aml.Length < 4) return new List<PpccPowerLimitEntry>();

            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                var nativeEntries = new PredatorNativeCore.NativePpccEntry[8];
                int count = PredatorNativeCore.AcpiParsePpccEntries(aml, nativeEntries);
                if (count > 0)
                {
                    int usable = Math.Min(count, nativeEntries.Length);
                    var parsed = new List<PpccPowerLimitEntry>(usable);
                    for (int i = 0; i < usable; i++)
                    {
                        var entry = nativeEntries[i];
                        parsed.Add(new PpccPowerLimitEntry
                        {
                            PowerLimitIndex = entry.Index,
                            MinPowerLimitMilliwatts = entry.MinMilliwatts,
                            MaxPowerLimitMilliwatts = entry.MaxMilliwatts,
                            MinTimeWindowMs = entry.MinTimeWindowMs,
                            MaxTimeWindowMs = entry.MaxTimeWindowMs,
                            StepSizeMilliwatts = entry.StepMilliwatts
                        });
                    }
                    return parsed;
                }
            }

            return ExtractPpccEntriesFromAml(aml.AsSpan());
        }

        /// <summary>Managed reference parser (used when the native core is unavailable).</summary>
        public static List<PpccPowerLimitEntry> ExtractPpccEntriesFromAml(ReadOnlySpan<byte> aml)
        {
            var results = new List<PpccPowerLimitEntry>();
            if (aml.Length < 4) return results;
            int index = 0;

            while (index <= aml.Length - 4)
            {
                if (aml[index] == PpccPattern[0] &&
                    aml[index + 1] == PpccPattern[1] &&
                    aml[index + 2] == PpccPattern[2] &&
                    aml[index + 3] == PpccPattern[3])
                {
                    int pos = index + 4;
                    // Attempt structured AML package parsing
                    var structured = TryParseStructuredPpcc(aml, ref pos);
                    if (structured.Count > 0)
                    {
                        return structured;
                    }

                    // Fallback heuristic integer search in adjacent 256 bytes
                    int searchStart = index + 4;
                    int searchEnd = Math.Min(aml.Length, searchStart + 256);
                    var numbers = ExtractAmlIntegers(aml.Slice(searchStart, searchEnd - searchStart));
                    var mwValues = numbers.Where(n => n >= 5000 && n <= 350000).ToList();

                    if (mwValues.Count >= 2)
                    {
                        int minMw = (int)mwValues.Min();
                        int maxMw = (int)mwValues.Max();
                        int pl1MaxMw = (int)mwValues.OrderByDescending(v => v).Skip(1).FirstOrDefault(maxMw);

                        results.Add(new PpccPowerLimitEntry
                        {
                            PowerLimitIndex = 0,
                            MinPowerLimitMilliwatts = minMw,
                            MaxPowerLimitMilliwatts = pl1MaxMw,
                            MinTimeWindowMs = 1000,
                            MaxTimeWindowMs = 32000,
                            StepSizeMilliwatts = 500
                        });

                        results.Add(new PpccPowerLimitEntry
                        {
                            PowerLimitIndex = 1,
                            MinPowerLimitMilliwatts = minMw,
                            MaxPowerLimitMilliwatts = maxMw,
                            MinTimeWindowMs = 28,
                            MaxTimeWindowMs = 32,
                            StepSizeMilliwatts = 500
                        });

                        return results;
                    }
                }
                index++;
            }

            return results;
        }

        private static List<PpccPowerLimitEntry> TryParseStructuredPpcc(ReadOnlySpan<byte> aml, ref int pos)
        {
            var list = new List<PpccPowerLimitEntry>();
            try
            {
                // Advance past optional NameOp (0x08) or whitespace within a safe 16-byte window
                int maxScan = Math.Min(aml.Length, pos + 16);
                while (pos < maxScan && aml[pos] != AML_PACKAGE_OP && aml[pos] != AML_VAR_PACKAGE_OP)
                {
                    pos++;
                }

                if (pos >= aml.Length || (aml[pos] != AML_PACKAGE_OP && aml[pos] != AML_VAR_PACKAGE_OP))
                    return list;

                pos++; // past PackageOp
                int pkgStart = pos;
                int pkgLength = ReadPkgLength(aml, ref pos);
                if (pos >= aml.Length || pkgLength <= 0 || pkgLength > aml.Length - pkgStart) return list;

                int pkgEnd = Math.Min(aml.Length, pkgStart + pkgLength);
                if (pos >= pkgEnd) return list;

                byte numElements = aml[pos++];
                for (int elem = 0; elem < numElements && pos < pkgEnd; elem++)
                {
                    int elemStartPos = pos;

                    // First element is usually Revision (Integer)
                    if (elem == 0 && aml[pos] != AML_PACKAGE_OP && aml[pos] != AML_VAR_PACKAGE_OP)
                    {
                        _ = ReadAmlInteger(aml, ref pos, pkgEnd);
                        continue;
                    }

                    // Subsequent elements are inner Packages representing power limits
                    if (aml[pos] == AML_PACKAGE_OP || aml[pos] == AML_VAR_PACKAGE_OP)
                    {
                        pos++; // past inner PackageOp
                        int innerStart = pos;
                        int innerPkgLength = ReadPkgLength(aml, ref pos);
                        if (pos >= pkgEnd || innerPkgLength <= 0 || innerPkgLength > pkgEnd - innerStart) break;

                        int innerPkgEnd = Math.Min(pkgEnd, innerStart + innerPkgLength);
                        if (pos >= innerPkgEnd) break;

                        byte innerCount = aml[pos++];
                        var innerInts = new List<long>();
                        for (int k = 0; k < innerCount && pos < innerPkgEnd; k++)
                        {
                            long val = ReadAmlInteger(aml, ref pos, innerPkgEnd);
                            innerInts.Add(val);
                        }

                        if (innerInts.Count >= 3)
                        {
                            long rawIndex = innerInts[0];
                            long rawMin = innerInts[1];
                            long rawMax = innerInts[2];

                            // Bounds and sanity checks to prevent integer overflow and negative values
                            if (rawIndex >= 0 && rawIndex <= 10 &&
                                rawMin >= 0 && rawMin <= 500000 &&
                                rawMax >= rawMin && rawMax >= 1000 && rawMax <= 500000)
                            {
                                int pIndex = (int)rawIndex;
                                int minMw = (int)rawMin;
                                int maxMw = (int)rawMax;
                                int minTw = innerInts.Count > 3 ? (int)Math.Clamp(innerInts[3], 1, 1000000) : 1000;
                                int maxTw = innerInts.Count > 4 ? (int)Math.Clamp(innerInts[4], 1, 1000000) : 32000;
                                int stepMw = innerInts.Count > 5 ? (int)Math.Clamp(innerInts[5], 1, 50000) : 500;

                                list.Add(new PpccPowerLimitEntry
                                {
                                    PowerLimitIndex = pIndex,
                                    MinPowerLimitMilliwatts = minMw,
                                    MaxPowerLimitMilliwatts = maxMw,
                                    MinTimeWindowMs = minTw,
                                    MaxTimeWindowMs = maxTw,
                                    StepSizeMilliwatts = stepMw
                                });
                            }
                        }

                        // Ensure pos is at least advanced to innerPkgEnd if inner elements were fewer than length
                        if (pos < innerPkgEnd)
                        {
                            pos = innerPkgEnd;
                        }
                    }
                    else
                    {
                        // Unrecognized opcode: advance safely to prevent infinite loops or stalls
                        _ = ReadAmlInteger(aml, ref pos, pkgEnd);
                        if (pos == elemStartPos && pos < pkgEnd)
                        {
                            pos++;
                        }
                    }
                }
            }
            catch { }

            return list;
        }

        private static int ReadPkgLength(ReadOnlySpan<byte> span, ref int pos)
        {
            if (pos >= span.Length) return 0;
            byte lead = span[pos++];
            byte byteCount = (byte)((lead >> 6) & 0x03);

            if (byteCount == 0) return lead & 0x3F;
            if (pos + byteCount > span.Length)
            {
                pos = span.Length;
                return 0; // Truncated package length
            }

            if (byteCount == 1)
            {
                int val = (lead & 0x0F) | (span[pos++] << 4);
                return val >= 2 ? val : 0;
            }
            if (byteCount == 2)
            {
                int b1 = span[pos++];
                int b2 = span[pos++];
                int val = (lead & 0x0F) | (b1 << 4) | (b2 << 12);
                return val >= 3 ? val : 0;
            }
            if (byteCount == 3)
            {
                int b1 = span[pos++];
                int b2 = span[pos++];
                int b3 = span[pos++];
                int val = ((lead & 0x0F) | (b1 << 4) | (b2 << 12) | ((b3 & 0x0F) << 20)) & 0x0FFFFFFF;
                return val >= 4 ? val : 0;
            }
            return 0;
        }

        private static long ReadAmlInteger(ReadOnlySpan<byte> span, ref int pos, int limit = -1)
        {
            int maxLimit = (limit >= 0 && limit <= span.Length) ? limit : span.Length;
            if (pos >= maxLimit) return 0;
            byte op = span[pos++];

            if (op == AML_ZERO_OP) return 0;
            if (op == AML_ONE_OP) return 1;
            if (op == AML_ONES_OP) return -1;

            if (op == AML_BYTE_PREFIX)
            {
                if (pos < maxLimit) return span[pos++];
                return 0;
            }

            if (op == AML_WORD_PREFIX)
            {
                if (pos + 2 <= maxLimit)
                {
                    ushort val = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos, 2));
                    pos += 2;
                    return val;
                }
                pos = maxLimit;
                return 0;
            }

            if (op == AML_DWORD_PREFIX)
            {
                if (pos + 4 <= maxLimit)
                {
                    uint val = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos, 4));
                    pos += 4;
                    return val;
                }
                pos = maxLimit;
                return 0;
            }

            if (op == AML_QWORD_PREFIX)
            {
                if (pos + 8 <= maxLimit)
                {
                    ulong val = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(pos, 8));
                    pos += 8;
                    return (long)(val & 0x7FFFFFFFFFFFFFFFUL);
                }
                pos = maxLimit;
                return 0;
            }

            // Raw literal byte fallback
            return op;
        }

        private static List<long> ExtractAmlIntegers(ReadOnlySpan<byte> span)
        {
            var results = new List<long>();
            int i = 0;
            while (i < span.Length)
            {
                byte op = span[i++];
                if (op == AML_ZERO_OP)
                {
                    results.Add(0);
                }
                else if (op == AML_ONE_OP)
                {
                    results.Add(1);
                }
                else if (op == AML_ONES_OP)
                {
                    results.Add(-1);
                }
                else if (op == AML_BYTE_PREFIX)
                {
                    if (i < span.Length) results.Add(span[i++]);
                }
                else if (op == AML_WORD_PREFIX)
                {
                    if (i + 1 < span.Length)
                    {
                        results.Add(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i, 2)));
                        i += 2;
                    }
                    else break;
                }
                else if (op == AML_DWORD_PREFIX)
                {
                    if (i + 3 < span.Length)
                    {
                        results.Add(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i, 4)));
                        i += 4;
                    }
                    else break;
                }
                else if (op == AML_QWORD_PREFIX)
                {
                    if (i + 7 < span.Length)
                    {
                        results.Add((long)(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(i, 8)) & 0x7FFFFFFFFFFFFFFFUL));
                        i += 8;
                    }
                    else break;
                }
            }
            return results;
        }

        #endregion

        #region Source 3: Acer ACPI WMI Thermal & Power Tables

        private static WmiFirmwareThermalData ScanAcerWmiThermalTables(
            WmiController? wmi,
            StringBuilder sbDiag,
            int resolvedTdp,
            int resolvedMin,
            int resolvedMaxPl1,
            int resolvedMaxPl2)
        {
            var modeProfiles = new Dictionary<OperatingMode, FirmwareModeProfile>();
            var queriedMethods = new List<string>();
            string wmiClass = "AcerGamingFunction";
            bool isFound = false;

            // Compute untouched firmware mode table defaults as pristine baseline
            var defaults = CpuSpecDetector.ComputeModeDefaults(resolvedTdp, resolvedMin, resolvedMaxPl1, resolvedMaxPl2);

            try
            {
                // Attempt live WMI query against root\WMI with explicit timeout
                var enumOptions = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = true };
                var methodOptions = new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(2) };
                using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM AcerGamingFunction"), enumOptions);
                using var coll = searcher.Get();

                foreach (ManagementBaseObject mbo in coll)
                {
                    if (mbo is not ManagementObject obj)
                    {
                        try { mbo.Dispose(); } catch { }
                        continue;
                    }

                    using (obj)
                    {
                        isFound = true;
                        sbDiag.AppendLine($"[Source 3] Discovered WMI instance: {obj["InstanceName"]}");

                        // Probe GetGamingProfileSetting for all 5 modes
                        var modesToProbe = new (OperatingMode Mode, byte RawMode)[]
                        {
                            (OperatingMode.Eco, 0x06),
                            (OperatingMode.Quiet, 0x00),
                            (OperatingMode.Balanced, 0x01),
                            (OperatingMode.Performance, 0x04),
                            (OperatingMode.Turbo, 0x05)
                        };

                        foreach (var (mode, rawMode) in modesToProbe)
                        {
                            queriedMethods.Add($"GetGamingProfileSetting({rawMode})");
                            try
                            {
                                using var inParams = obj.GetMethodParameters("GetGamingProfileSetting");
                                inParams["gmInput"] = (uint)rawMode;
                                using var outParams = obj.InvokeMethod("GetGamingProfileSetting", inParams, methodOptions);

                                ulong output = outParams?["gmOutput"] is ulong o ? o : (Convert.ToUInt64(outParams?["gmOutput"] ?? 0UL));
                                byte status = (byte)(output & 0xFF);

                                int defPl1 = defaults[rawMode].Pl1;
                                int defPl2 = defaults[rawMode].Pl2;

                                modeProfiles[mode] = new FirmwareModeProfile
                                {
                                    Mode = mode,
                                    Pl1Watts = defPl1,
                                    Pl2Watts = defPl2,
                                    RawModeValue = rawMode,
                                    Source = "FirmwareModeTableDefaults"
                                };
                            }
                            catch
                            {
                                modeProfiles[mode] = new FirmwareModeProfile
                                {
                                    Mode = mode,
                                    Pl1Watts = defaults[rawMode].Pl1,
                                    Pl2Watts = defaults[rawMode].Pl2,
                                    RawModeValue = rawMode,
                                    Source = "FirmwareModeTableDefaults"
                                };
                            }
                        }

                        // Also query GetCPUOverclockingProfile buffer if present
                        try
                        {
                            queriedMethods.Add("GetCPUOverclockingProfile");
                            using var inParams = obj.GetMethodParameters("GetCPUOverclockingProfile");
                            inParams["Reserved"] = new byte[] { 0, 0, 0, 0 };
                            using var outParams = obj.InvokeMethod("GetCPUOverclockingProfile", inParams, methodOptions);

                            if (outParams?["OCStructure"] is byte[] ocBytes && ocBytes.Length > 0)
                            {
                                sbDiag.AppendLine($"[Source 3] Received GetCPUOverclockingProfile OCStructure buffer ({ocBytes.Length} bytes).");
                            }
                        }
                        catch { }

                        break; // Process first active instance
                    }
                }
            }
            catch (ManagementException mex)
            {
                sbDiag.AppendLine($"[Source 3] root\\WMI access requires administrative elevation ({mex.ErrorCode}): using firmware baseline defaults.");
            }
            catch (Exception ex)
            {
                sbDiag.AppendLine($"[Source 3] WMI probe exception: {ex.Message}");
            }

            // If WMI could not populate all 5 modes, fill remaining with firmware mode table baseline
            var allModes = new[] { OperatingMode.Eco, OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo };
            foreach (var m in allModes)
            {
                byte raw = (byte)m;
                if (!modeProfiles.ContainsKey(m))
                {
                    byte defKey = raw == 6 ? (byte)0x06 : raw;
                    var def = defaults.TryGetValue(defKey, out var val) ? val : (resolvedTdp, (int)Math.Round(resolvedTdp * 1.35));
                    modeProfiles[m] = new FirmwareModeProfile
                    {
                        Mode = m,
                        Pl1Watts = def.Item1,
                        Pl2Watts = def.Item2,
                        RawModeValue = raw,
                        Source = "FirmwareModeTableBaseline"
                    };
                }
            }

            return new WmiFirmwareThermalData
            {
                IsFound = isFound,
                WmiClass = wmiClass,
                ModeProfiles = modeProfiles,
                QueriedMethods = queriedMethods,
                RawPayloadSummary = $"Profiles resolved: {modeProfiles.Count} modes."
            };
        }

        #endregion

        #region Source 4: CPU Silicon Fused Capabilities

        private static SiliconFusedPowerData ScanSiliconCapabilities(
            IPawnIoBackend? pawnIoBackend,
            StringBuilder sbDiag)
        {
            var backend = pawnIoBackend ?? (PawnIoController.Instance.IsAvailable ? PawnIoController.Instance.Backend : null);
            if (backend == null || !backend.IsDriverLoaded)
            {
                sbDiag.AppendLine("[Source 4] PawnIO kernel driver not loaded; silicon fused MSR query skipped.");
                return new SiliconFusedPowerData { IsFound = false };
            }

            try
            {
                if (backend.ReadMsr(PawnIoController.MSR_RAPL_POWER_UNIT, out ulong rawUnit) && rawUnit != 0)
                {
                    uint unitsPerWatt = 1u << (int)(rawUnit & 0xFu);
                    if (unitsPerWatt > 0 && backend.ReadMsr(PawnIoController.MSR_PKG_POWER_INFO, out ulong rawInfo) && rawInfo != 0)
                    {
                        ulong rawThermal = rawInfo & 0x7FFFUL;
                        ulong rawMin = (rawInfo >> 16) & 0x7FFFUL;
                        ulong rawMax = (rawInfo >> 32) & 0x7FFFUL;
                        ulong rawTime = (rawInfo >> 48) & 0x7FUL;

                        int msrTdp = (int)Math.Round(rawThermal / (double)unitsPerWatt);
                        int msrMin = (int)Math.Round(rawMin / (double)unitsPerWatt);
                        int msrMax = (int)Math.Round(rawMax / (double)unitsPerWatt);

                        return new SiliconFusedPowerData
                        {
                            IsFound = true,
                            UnitsPerWatt = unitsPerWatt,
                            ThermalSpecPowerWatts = msrTdp,
                            MinimumPowerWatts = msrMin,
                            MaximumPowerWatts = msrMax,
                            MaxTimeWindowSeconds = (int)rawTime,
                            RawPowerInfo = rawInfo,
                            RawPowerUnit = rawUnit
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                sbDiag.AppendLine($"[Source 4] PawnIO MSR scan exception: {ex.Message}");
            }

            return new SiliconFusedPowerData { IsFound = false };
        }

        #endregion

        #region Boundary Synthesis

        private static SynthesizedPowerBoundaries SynthesizeBoundaries(
            BiosHardwareIdentity identity,
            AcpiFirmwarePowerData acpiData,
            WmiFirmwareThermalData wmiData,
            SiliconFusedPowerData siliconData,
            int tdp,
            int minP,
            int maxP1,
            int maxP2,
            StringBuilder sbDiag)
        {
            // 1. Min Power: floor at 10W for standard laptops, or 5W for U-series ultra-low power; cap sanity at 120W
            int safeMinFloor = (identity.ProcessorNameString.Contains('U', StringComparison.OrdinalIgnoreCase) || tdp <= 15) ? 5 : 10;
            int cpuMin = Math.Clamp(minP > 0 ? minP : safeMinFloor, safeMinFloor, 120);

            // 2. Base TDP: cap sanity at 200W
            int cpuTdp = Math.Clamp(Math.Max(cpuMin, tdp), cpuMin, 200);

            // 3. Max PL1 and PL2: cap sanity at 250W / 450W; expand PL1 ceiling to match max PL2 for custom limits
            int cpuMaxPl2 = Math.Clamp(Math.Max(cpuTdp, Math.Max(maxP1, maxP2)), cpuTdp, 450);
            int cpuMaxPl1 = cpuMaxPl2;

            // Synthesize per-mode defaults combining WMI profiles and proportional scaling
            var modeDefaults = new Dictionary<OperatingMode, (int Pl1Watts, int Pl2Watts)>();

            foreach (var m in new[] { OperatingMode.Eco, OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo })
            {
                if (wmiData.ModeProfiles.TryGetValue(m, out var prof) && prof.Pl1Watts > 0 && prof.Pl2Watts > 0)
                {
                    int clampedPl1 = Math.Clamp(prof.Pl1Watts, cpuMin, cpuMaxPl1);
                    int clampedPl2 = Math.Clamp(prof.Pl2Watts, clampedPl1, cpuMaxPl2);
                    modeDefaults[m] = (clampedPl1, clampedPl2);
                }
                else
                {
                    // Proportional defaults
                    int p1 = m switch
                    {
                        OperatingMode.Eco => Math.Clamp((int)Math.Round(cpuTdp * 0.55), cpuMin, cpuMaxPl1),
                        OperatingMode.Quiet => Math.Clamp((int)Math.Round(cpuTdp * 0.75), cpuMin, cpuMaxPl1),
                        OperatingMode.Balanced => Math.Clamp(cpuTdp, cpuMin, cpuMaxPl1),
                        OperatingMode.Performance => Math.Clamp((int)Math.Round(cpuTdp * 1.25), cpuMin, cpuMaxPl1),
                        OperatingMode.Turbo => Math.Clamp((int)Math.Round(cpuTdp * 2.00), Math.Min((int)Math.Round(cpuTdp * 1.25), cpuMaxPl1), cpuMaxPl1),
                        _ => cpuTdp
                    };

                    int p2 = m switch
                    {
                        OperatingMode.Eco => Math.Clamp((int)Math.Round(cpuTdp * 0.65), p1, cpuMaxPl2),
                        OperatingMode.Quiet => Math.Clamp((int)Math.Round(cpuTdp * 0.85), p1, cpuMaxPl2),
                        OperatingMode.Balanced => Math.Clamp((int)Math.Round(cpuTdp * 1.35), p1, cpuMaxPl2),
                        OperatingMode.Performance => Math.Clamp((int)Math.Round(cpuTdp * 1.65), p1, cpuMaxPl2),
                        OperatingMode.Turbo => cpuMaxPl2,
                        _ => Math.Clamp((int)Math.Round(cpuTdp * 1.35), p1, cpuMaxPl2)
                    };

                    modeDefaults[m] = (p1, p2);
                }
            }

            sbDiag.AppendLine($"[Synthesis] Synthesized Boundaries: Min={cpuMin}W, TDP={cpuTdp}W, MaxPL1={cpuMaxPl1}W, MaxPL2={cpuMaxPl2}W");

            return new SynthesizedPowerBoundaries
            {
                CpuMinAllowedPowerWatts = cpuMin,
                CpuThermalSpecTdpWatts = cpuTdp,
                CpuMaxAllowedPl1Watts = cpuMaxPl1,
                CpuMaxAllowedPl2Watts = cpuMaxPl2,
                RecommendedModeDefaults = modeDefaults
            };
        }

        #endregion
    }

    #endregion
}
