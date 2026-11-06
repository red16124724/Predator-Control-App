using System;

using System.Runtime.Versioning;

using System.Threading;

using System.Threading.Tasks;

using Microsoft.Win32;



namespace PredatorControlApp

{

    /// <summary>

    /// Coordinates WmiController and PawnIoController to provide hardware-agnostic CPU PL1/PL2 power limit control.

    /// 

    /// Key Architecture Principles:

    /// 1. Non-Intrusive Zero-Assumption Rule: Never overwrite hardware on startup unless EnableCustomPowerLimits == true.

    /// 2. Hardware Fingerprint: Computes ScannedHardwareSignature ("{Manufacturer}_{Model}_{ProcessorName}_{CpuTdpWatts}").

    ///    Automatically invalidates stale cache and re-scans if the motherboard/CPU changes.

    /// 3. Per-Mode Stock Baseline Capture: 250ms settle delay after WMI mode switch, reads stock PL1/PL2 from MSR 0x610,

    ///    records in AppSettings as native baseline for the Reset button.

    /// 4. Dynamic Proportional Estimation: Supports Eco, Quiet, Balanced, Performance, Turbo with proportional defaults

    ///    for unvisited modes based on scanned TDP.

    /// 5. Lightweight Periodic Verification: Rate-limited verification hook for telemetry loop to detect EC resets without overhead.

    /// </summary>

    public sealed class CpuPowerLimitController : IDisposable

    {

        private readonly WmiController? _wmi;

        private readonly AppSettings _settings;

        private readonly PawnIoController _pawnIoController;

        private IBiosUefiPowerScanner _biosScanner;

        private readonly bool _ownsPawnIo;

        private readonly object _syncLock = new();



        private DevicePowerCapabilities _capabilities = DevicePowerCapabilities.Unsupported(

            PowerLimitApplyStatus.DriverNotInstalled, "Not initialized");



        private DateTime _lastVerifyTime = DateTime.MinValue;

        private static readonly TimeSpan VerifyInterval = TimeSpan.FromSeconds(2);

        private bool _disposed;

        private volatile bool _isCalibrating;

        private CancellationTokenSource? _modeTransitionCts;

        private readonly object _modeTransitionLock = new();



        public event Action<OperatingMode, int, int, bool>? PowerLimitsChanged;



        public PawnIoController PawnIo => _pawnIoController;

        public AppSettings Settings => _settings;

        public IBiosUefiPowerScanner BiosScanner => _biosScanner;

        public DevicePowerCapabilities Capabilities { get { lock (_syncLock) return _capabilities; } }

        public bool IsAvailable => _pawnIoController.IsAvailable;

        public bool IsLocked => _capabilities.IsLocked;



        private static volatile CpuPowerLimitController? _instance;

        private static readonly object _instanceLock = new();



        public static CpuPowerLimitController Instance

        {

            get

            {

                if (_instance == null)

                {

                    lock (_instanceLock)

                    {

                        _instance ??= new CpuPowerLimitController(AppSettings.Load());

                    }

                }

                return _instance;

            }

            set

            {

                lock (_instanceLock)

                {

                    _instance = value;

                }

            }

        }



        public CpuPowerLimitController(WmiController? wmi, AppSettings settings, PawnIoController? pawnIoController = null, IBiosUefiPowerScanner? biosScanner = null)

        {

            _wmi = wmi;

            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            if (pawnIoController != null)

            {

                _pawnIoController = pawnIoController;

                _ownsPawnIo = false;

            }

            else

            {

                _pawnIoController = PawnIoController.Instance;

                _ownsPawnIo = false;

            }

            _biosScanner = biosScanner ?? new BiosUefiPowerScanner();

        }



        public CpuPowerLimitController(WmiController? wmi, AppSettings settings, IPawnIoBackend backend, IBiosUefiPowerScanner? biosScanner = null)

        {

            _wmi = wmi;

            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _pawnIoController = new PawnIoController(backend ?? throw new ArgumentNullException(nameof(backend)));

            _ownsPawnIo = true;

            _biosScanner = biosScanner ?? new BiosUefiPowerScanner();

        }



        public CpuPowerLimitController(AppSettings settings, PawnIoController? pawnIoController = null, IBiosUefiPowerScanner? biosScanner = null)

            : this(null, settings, pawnIoController, biosScanner)

        {

        }



        public CpuPowerLimitController(AppSettings settings, IPawnIoBackend backend, IBiosUefiPowerScanner? biosScanner = null)

            : this(null, settings, backend, biosScanner)

        {

        }



        public CpuPowerLimitController(AppSettings settings, IBiosUefiPowerScanner biosScanner)

            : this(null, settings, (PawnIoController?)null, biosScanner)

        {

        }



        /// <summary>

        /// Computes a hardware fingerprint string formatted as "{Manufacturer}_{Model}_{ProcessorName}_{CpuTdpWatts}".

        /// </summary>

        public static string ComputeHardwareSignature(string manufacturer, string model, string processorName, int tdpWatts)

        {

            string mfg = string.IsNullOrWhiteSpace(manufacturer) ? "Unknown" : manufacturer.Trim();

            string mdl = string.IsNullOrWhiteSpace(model) ? "Unknown" : model.Trim();

            string cpu = string.IsNullOrWhiteSpace(processorName) ? "Unknown" : processorName.Trim();

            return $"{mfg}_{mdl}_{cpu}_{tdpWatts}";

        }



        /// <summary>

        /// Reads local machine BIOS and CPU registry keys to compute the hardware signature.

        /// </summary>

        public static string ComputeHardwareSignature(int tdpWatts = 0)

        {

            string manufacturer = "Acer";

            string model = "Predator";

            string processorName = "Intel Processor";



            if (OperatingSystem.IsWindows())

            {

                try

                {

                    using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");

                    if (biosKey != null)

                    {

                        string? mfg = biosKey.GetValue("SystemManufacturer") as string

                                      ?? biosKey.GetValue("BaseBoardManufacturer") as string;

                        if (!string.IsNullOrWhiteSpace(mfg))

                            manufacturer = mfg.Trim();



                        string? prod = biosKey.GetValue("SystemProductName") as string

                                       ?? biosKey.GetValue("BaseBoardProduct") as string;

                        if (!string.IsNullOrWhiteSpace(prod))

                            model = prod.Trim();

                    }



                    using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

                    if (cpuKey != null)

                    {

                        string? cpu = cpuKey.GetValue("ProcessorNameString") as string;

                        if (!string.IsNullOrWhiteSpace(cpu))

                            processorName = cpu.Trim();

                    }

                }

                catch

                {

                    // Fall back to defaults on restricted permissions or test environments

                }

            }



            return ComputeHardwareSignature(manufacturer, model, processorName, tdpWatts);

        }



        /// <summary>

        /// Initializes hardware detection, checks signature to invalidate stale data, scans power limits,

        /// and enforces the non-intrusive zero-assumption rule on startup.

        /// </summary>

                /// <summary>

        /// Ingests the result of an exhaustive native BIOS/UEFI & ACPI WMI power scan.

        /// Enforces fingerprint invalidation if motherboard or BIOS version changed,

        /// records stock per-mode power limits into AppSettings, and respects the non-intrusive zero-assumption rule.

        /// </summary>

        public void ApplyBiosPowerScanResult(BiosPowerScanResult scanResult)

        {

            if (scanResult == null) throw new ArgumentNullException(nameof(scanResult));



            lock (_syncLock)

            {

                string currentFingerprint = scanResult.BiosHardwareFingerprint;

                bool fingerprintChanged = !string.IsNullOrWhiteSpace(_settings.BiosHardwareFingerprint) &&

                    !string.Equals(_settings.BiosHardwareFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase);



                if (fingerprintChanged)

                {

                    _settings.ResetAllPowerLimitsToScannedDefaults();

                    _settings.HasCompletedBiosPowerScan = false;

                    _settings.CpuThermalSpecTdpWatts = 0;

                    _settings.CpuMinAllowedPowerWatts = 0;

                    _settings.CpuMaxAllowedPl1Watts = 0;

                    _settings.CpuMaxAllowedPl2Watts = 0;

                    _settings.VerifiedBiosModesBitmask = 0;



                    _settings.BiosDefaultEcoPl1Watts = -1;

                    _settings.BiosDefaultEcoPl2Watts = -1;

                    _settings.BiosDefaultQuietPl1Watts = -1;

                    _settings.BiosDefaultQuietPl2Watts = -1;

                    _settings.BiosDefaultBalancedPl1Watts = -1;

                    _settings.BiosDefaultBalancedPl2Watts = -1;

                    _settings.BiosDefaultPerformancePl1Watts = -1;

                    _settings.BiosDefaultPerformancePl2Watts = -1;

                    _settings.BiosDefaultTurboPl1Watts = -1;

                    _settings.BiosDefaultTurboPl2Watts = -1;

                }



                _settings.BiosHardwareFingerprint = currentFingerprint;

                if (scanResult.CpuThermalSpecTdpWatts > 0 && _settings.CpuThermalSpecTdpWatts <= 0)

                    _settings.CpuThermalSpecTdpWatts = scanResult.CpuThermalSpecTdpWatts;

                if (scanResult.CpuMinAllowedPowerWatts > 0 && _settings.CpuMinAllowedPowerWatts <= 0)

                    _settings.CpuMinAllowedPowerWatts = scanResult.CpuMinAllowedPowerWatts;

                if (scanResult.CpuMaxAllowedPl2Watts > 0)
                    _settings.CpuMaxAllowedPl2Watts = scanResult.CpuMaxAllowedPl2Watts;

                // PL1 keeps its own firmware ceiling. Copying the PL2 ceiling into PL1 widened the
                // sustained limit beyond what the hardware reported and beyond the PL1 slider maximum.
                if (scanResult.CpuMaxAllowedPl1Watts > 0)
                    _settings.CpuMaxAllowedPl1Watts = scanResult.CpuMaxAllowedPl1Watts;
                else if (_settings.CpuMaxAllowedPl1Watts <= 0 && _settings.CpuMaxAllowedPl2Watts > 0)
                    _settings.CpuMaxAllowedPl1Watts = _settings.CpuMaxAllowedPl2Watts;

                if (_settings.CpuMaxAllowedPl2Watts > 0 &&
                    (_settings.CpuMaxAllowedPl1Watts <= 0 || _settings.CpuMaxAllowedPl1Watts > _settings.CpuMaxAllowedPl2Watts))
                    _settings.CpuMaxAllowedPl1Watts = _settings.CpuMaxAllowedPl2Watts;

                if (!_settings.HardwareHasActiveCustomPowerOverride)
                {
                    if (scanResult.BiosDefaultEcoPl1Watts > 0 && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Eco))
                        _settings.BiosDefaultEcoPl1Watts = scanResult.BiosDefaultEcoPl1Watts;
                    if (scanResult.BiosDefaultEcoPl2Watts > 0 && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Eco))
                        _settings.BiosDefaultEcoPl2Watts = scanResult.BiosDefaultEcoPl2Watts;

                    if (scanResult.BiosDefaultQuietPl1Watts > 0 && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Quiet))
                        _settings.BiosDefaultQuietPl1Watts = scanResult.BiosDefaultQuietPl1Watts;
                    if (scanResult.BiosDefaultQuietPl2Watts > 0 && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Quiet))
                        _settings.BiosDefaultQuietPl2Watts = scanResult.BiosDefaultQuietPl2Watts;

                    if (scanResult.BiosDefaultBalancedPl1Watts > 0 && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Balanced))
                        _settings.BiosDefaultBalancedPl1Watts = scanResult.BiosDefaultBalancedPl1Watts;
                    if (scanResult.BiosDefaultBalancedPl2Watts > 0 && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Balanced))
                        _settings.BiosDefaultBalancedPl2Watts = scanResult.BiosDefaultBalancedPl2Watts;

                    if (scanResult.BiosDefaultPerformancePl1Watts > 0 && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Performance))
                        _settings.BiosDefaultPerformancePl1Watts = scanResult.BiosDefaultPerformancePl1Watts;
                    if (scanResult.BiosDefaultPerformancePl2Watts > 0 && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Performance))
                        _settings.BiosDefaultPerformancePl2Watts = scanResult.BiosDefaultPerformancePl2Watts;

                    if (scanResult.BiosDefaultTurboPl1Watts > 0 && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Turbo))
                        _settings.BiosDefaultTurboPl1Watts = scanResult.BiosDefaultTurboPl1Watts;
                    if (scanResult.BiosDefaultTurboPl2Watts > 0 && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Turbo))
                        _settings.BiosDefaultTurboPl2Watts = scanResult.BiosDefaultTurboPl2Watts;

                    if (scanResult.VerifiedBiosModesBitmask != 0)
                        _settings.VerifiedBiosModesBitmask |= scanResult.VerifiedBiosModesBitmask;
                }



                _settings.HasCompletedBiosPowerScan = true;



                _capabilities = _capabilities with

                {

                    IsSupported = true,

                    Status = PowerLimitApplyStatus.Success,

                    StatusMessage = "Supported",

                    BiosPowerScan = scanResult,

                    ScannedMinPowerWatts = _settings.CpuMinAllowedPowerWatts > 0 ? _settings.CpuMinAllowedPowerWatts : (scanResult.CpuMinAllowedPowerWatts > 0 ? scanResult.CpuMinAllowedPowerWatts : _capabilities.ScannedMinPowerWatts),

                    ScannedMaxPl1Watts = _settings.CpuMaxAllowedPl1Watts > 0 ? _settings.CpuMaxAllowedPl1Watts : (scanResult.CpuMaxAllowedPl1Watts > 0 ? scanResult.CpuMaxAllowedPl1Watts : _capabilities.ScannedMaxPl1Watts),

                    ScannedMaxPl2Watts = _settings.CpuMaxAllowedPl2Watts > 0 ? _settings.CpuMaxAllowedPl2Watts : (scanResult.CpuMaxAllowedPl2Watts > 0 ? scanResult.CpuMaxAllowedPl2Watts : _capabilities.ScannedMaxPl2Watts),

                    ThermalSpecPowerWatts = _settings.CpuThermalSpecTdpWatts > 0 ? _settings.CpuThermalSpecTdpWatts : (scanResult.CpuThermalSpecTdpWatts > 0 ? scanResult.CpuThermalSpecTdpWatts : _capabilities.ThermalSpecPowerWatts)

                };



                // Non-intrusive Zero-Assumption Rule: Never overwrite hardware on startup unless EnableCustomPowerLimits == true.

                if (_settings.EnableCustomPowerLimits && _pawnIoController.IsAvailable)

                {

                    OperatingMode activeMode = GetCurrentOperatingMode();

                    var (rawPl1, rawPl2) = _settings.GetEffectivePowerLimitsForMode(activeMode);

                    var (effPl1, effPl2) = ApplyThermalSafetyCap(rawPl1, rawPl2);

                    var res = _pawnIoController.ApplyPowerLimits(effPl1, effPl2);

                    if (res.IsSuccess && _settings.HasCustomPowerLimitsForMode(activeMode))

                    {

                        _settings.HardwareHasActiveCustomPowerOverride = true;

                        RegistrySafety.SaveCpuPowerLimits(_settings);

                    }

                }



                _settings.Save();

            }

        }



        /// <summary>

        /// Unified re-scan method that scans native BIOS/UEFI, ACPI tables, and live PawnIO hardware registers,

        /// applies the scan result, refreshes hardware capabilities baseline, and returns the validated BiosPowerScanResult.

        /// </summary>

        public BiosPowerScanResult ReScanHardwareAndBiosDefaults(string? overrideSignature = null)

        {

            lock (_syncLock)

            {

                _isCalibrating = true;

                try

                {

                    if (_settings.HardwareHasActiveCustomPowerOverride)

                    {

                        OperatingMode curMode = GetCurrentOperatingMode();

                        if (_wmi != null)

                        {

                            try

                            {

                                _wmi.ModeChannel?.TrySetOperatingMode(curMode);

                                _wmi.TrySetOperatingMode(curMode, forceRetry: true);

                            }

                            catch { }

                        }

                        // Allow Acer EC 300ms settle window to rewrite stock BIOS PL1/PL2 registers before sampling

                        Thread.Sleep(300);

                    }



                var caps = _pawnIoController.ScanDevicePowerCapabilities();

                if (caps.IsSupported)

                {

                    _capabilities = caps;

                }



                var scanResult = _biosScanner.ScanPowerLimits(

                    _pawnIoController.Backend,

                    _wmi);



                if (scanResult != null && scanResult.HasCompletedBiosPowerScan)

                {

                    ApplyBiosPowerScanResult(scanResult);

                    _capabilities = _capabilities with

                    {

                        IsSupported = true,

                        Status = PowerLimitApplyStatus.Success,

                        StatusMessage = "Supported",

                        BiosPowerScan = scanResult,

                        ScannedMinPowerWatts = _settings.CpuMinAllowedPowerWatts > 0 ? _settings.CpuMinAllowedPowerWatts : (scanResult.CpuMinAllowedPowerWatts > 0 ? scanResult.CpuMinAllowedPowerWatts : _capabilities.ScannedMinPowerWatts),

                        ScannedMaxPl1Watts = _settings.CpuMaxAllowedPl1Watts > 0 ? _settings.CpuMaxAllowedPl1Watts : (scanResult.CpuMaxAllowedPl1Watts > 0 ? scanResult.CpuMaxAllowedPl1Watts : _capabilities.ScannedMaxPl1Watts),

                        ScannedMaxPl2Watts = _settings.CpuMaxAllowedPl2Watts > 0 ? _settings.CpuMaxAllowedPl2Watts : (scanResult.CpuMaxAllowedPl2Watts > 0 ? scanResult.CpuMaxAllowedPl2Watts : _capabilities.ScannedMaxPl2Watts),

                        ThermalSpecPowerWatts = _settings.CpuThermalSpecTdpWatts > 0 ? _settings.CpuThermalSpecTdpWatts : (scanResult.CpuThermalSpecTdpWatts > 0 ? scanResult.CpuThermalSpecTdpWatts : _capabilities.ThermalSpecPowerWatts)

                    };

                }



                if (overrideSignature != null)

                {

                    _settings.BiosHardwareFingerprint = overrideSignature;

                }



                if (scanResult != null)

                {

                    scanResult.CpuMinAllowedPowerWatts = _settings.CpuMinAllowedPowerWatts;

                    scanResult.CpuMaxAllowedPl1Watts = _settings.CpuMaxAllowedPl1Watts;

                    scanResult.CpuMaxAllowedPl2Watts = _settings.CpuMaxAllowedPl2Watts;

                    scanResult.CpuThermalSpecTdpWatts = _settings.CpuThermalSpecTdpWatts;



                    var (eco1, eco2) = _settings.GetDefaultPowerLimitsForMode(OperatingMode.Eco);

                    var (quiet1, quiet2) = _settings.GetDefaultPowerLimitsForMode(OperatingMode.Quiet);

                    var (bal1, bal2) = _settings.GetDefaultPowerLimitsForMode(OperatingMode.Balanced);

                    var (perf1, perf2) = _settings.GetDefaultPowerLimitsForMode(OperatingMode.Performance);

                    var (turbo1, turbo2) = _settings.GetDefaultPowerLimitsForMode(OperatingMode.Turbo);



                    scanResult.BiosDefaultEcoPl1Watts = eco1;

                    scanResult.BiosDefaultEcoPl2Watts = eco2;

                    scanResult.BiosDefaultQuietPl1Watts = quiet1;

                    scanResult.BiosDefaultQuietPl2Watts = quiet2;

                    scanResult.BiosDefaultBalancedPl1Watts = bal1;

                    scanResult.BiosDefaultBalancedPl2Watts = bal2;

                    scanResult.BiosDefaultPerformancePl1Watts = perf1;

                    scanResult.BiosDefaultPerformancePl2Watts = perf2;

                    scanResult.BiosDefaultTurboPl1Watts = turbo1;

                    scanResult.BiosDefaultTurboPl2Watts = turbo2;

                    scanResult.HasCompletedBiosPowerScan = true;

                }



                if (_thermalSafetyCapWatts.HasValue && _pawnIoController.IsAvailable)
                {
                    OperatingMode curMode = GetCurrentOperatingMode();
                    var (r1, r2) = _settings.GetEffectivePowerLimitsForMode(curMode);
                    var (cap1, cap2) = ApplyThermalSafetyCap(r1, r2);
                    _pawnIoController.ApplyPowerLimits(cap1, cap2);
                }

                return scanResult ?? new BiosPowerScanResult();

                }

                finally

                {

                    _isCalibrating = false;

                }

            }

        }



        public BiosPowerScanResult InitializeWithBiosScanner(IBiosUefiPowerScanner scanner)

        {

            if (scanner == null) throw new ArgumentNullException(nameof(scanner));

            _biosScanner = scanner;

            var result = scanner.ScanPowerLimits(_pawnIoController.Backend, _wmi);

            ApplyBiosPowerScanResult(result);

            return result;

        }



        public DevicePowerCapabilities InitializeHardwareFingerprintAndBaseline(string? overrideSignature = null, int settleDelayMs = 0)
        {
            OperatingMode activeMode = GetCurrentOperatingMode();

            // Clean Hardware Reset Before Sampling Unverified BIOS Defaults (executed before lock to avoid lock inversion and sleeping under lock)
            if (_settings.HardwareHasActiveCustomPowerOverride && !_settings.HasVerifiedBiosDefaultForMode(activeMode))
            {
                if (_wmi != null)
                {
                    try
                    {
                        _wmi.ModeChannel?.TrySetOperatingMode(activeMode);
                        _wmi.TrySetOperatingMode(activeMode, forceRetry: true);
                    }
                    catch { }
                }

                if (settleDelayMs > 0)
                {
                    Thread.Sleep(settleDelayMs);
                }
            }

            lock (_syncLock)
            {
                string currentSignature = overrideSignature ?? _biosScanner.ComputeBiosHardwareFingerprint();
                _settings.InvalidateIfBiosFingerprintChanged(currentSignature);

                var caps = _pawnIoController.ScanDevicePowerCapabilities();

                if (caps.IsSupported && caps.ScannedMaxPl2Watts > 0)

                {

                    int preservedMaxPl2 = _settings.CpuMaxAllowedPl2Watts > 0 ? _settings.CpuMaxAllowedPl2Watts : caps.ScannedMaxPl2Watts;

                    int preservedMaxPl1 = _settings.CpuMaxAllowedPl1Watts > 0 ? _settings.CpuMaxAllowedPl1Watts : caps.ScannedMaxPl1Watts;

                    int preservedMin = _settings.CpuMinAllowedPowerWatts > 0 ? _settings.CpuMinAllowedPowerWatts : caps.ScannedMinPowerWatts;

                    int preservedTdp = _settings.CpuThermalSpecTdpWatts > 0 ? _settings.CpuThermalSpecTdpWatts : caps.ThermalSpecPowerWatts;



                    _capabilities = caps with

                    {

                        ScannedMaxPl2Watts = preservedMaxPl2,

                        ScannedMaxPl1Watts = preservedMaxPl1,

                        ScannedMinPowerWatts = preservedMin,

                        ThermalSpecPowerWatts = preservedTdp

                    };



                    _settings.CpuMinAllowedPowerWatts = preservedMin;

                    _settings.CpuMaxAllowedPl1Watts = preservedMaxPl1;

                    _settings.CpuMaxAllowedPl2Watts = preservedMaxPl2;

                    _settings.CpuThermalSpecTdpWatts = preservedTdp;

                    _settings.HasCompletedBiosPowerScan = true;

                }

                else if (!_capabilities.IsSupported)

                {

                    _capabilities = caps;

                }



                activeMode = GetCurrentOperatingMode();

                int tdp = caps.ThermalSpecPowerWatts > 0 ? caps.ThermalSpecPowerWatts : (_capabilities.ThermalSpecPowerWatts > 0 ? _capabilities.ThermalSpecPowerWatts : _settings.CpuThermalSpecTdpWatts);



                if (settleDelayMs > 0)

                {

                    Thread.Sleep(settleDelayMs);

                }



                // If untouched BIOS default for active mode not yet recorded and custom limits not enabled,

                // record genuine BIOS-applied PL1 & PL2 straight from MSR 0x610

                if (caps.IsSupported && !_settings.EnableCustomPowerLimits &&

                    !_settings.HasVerifiedBiosDefaultForMode(activeMode) &&

                    caps.CurrentPl1Watts > 0 && caps.CurrentPl2Watts > 0)

                {

                    int sampledPl1 = caps.CurrentPl1Watts;

                    int sampledPl2 = caps.CurrentPl2Watts;

                    if (IsDirtyCustomRegister(activeMode, sampledPl1, sampledPl2))

                    {

                        var clean = GetCleanBiosOrAcpiBaselineForMode(activeMode);

                        sampledPl1 = clean.Pl1;

                        sampledPl2 = clean.Pl2;

                    }

                    _settings.RecordBiosDefaultLimitsForMode(activeMode, sampledPl1, sampledPl2, isVerifiedBiosScan: true);

                }



                // Run BIOS scanner for unvisited mode defaults and ACPI discovery

                if (overrideSignature == null)

                {

                    var scanResult = _biosScanner.ScanPowerLimits(

                        _pawnIoController.IsAvailable ? _pawnIoController.Backend : null,

                        _wmi);



                    if (scanResult != null && scanResult.HasCompletedBiosPowerScan)

                    {

                        ApplyBiosPowerScanResult(scanResult);

                        _capabilities = _capabilities with

                        {

                            IsSupported = true,

                            Status = PowerLimitApplyStatus.Success,

                            StatusMessage = "Supported",

                            BiosPowerScan = scanResult,

                            ScannedMinPowerWatts = _settings.CpuMinAllowedPowerWatts > 0 ? _settings.CpuMinAllowedPowerWatts : (scanResult.CpuMinAllowedPowerWatts > 0 ? scanResult.CpuMinAllowedPowerWatts : _capabilities.ScannedMinPowerWatts),

                            ScannedMaxPl1Watts = _settings.CpuMaxAllowedPl1Watts > 0 ? _settings.CpuMaxAllowedPl1Watts : (scanResult.CpuMaxAllowedPl1Watts > 0 ? scanResult.CpuMaxAllowedPl1Watts : _capabilities.ScannedMaxPl1Watts),

                            ScannedMaxPl2Watts = _settings.CpuMaxAllowedPl2Watts > 0 ? _settings.CpuMaxAllowedPl2Watts : (scanResult.CpuMaxAllowedPl2Watts > 0 ? scanResult.CpuMaxAllowedPl2Watts : _capabilities.ScannedMaxPl2Watts),

                            ThermalSpecPowerWatts = _settings.CpuThermalSpecTdpWatts > 0 ? _settings.CpuThermalSpecTdpWatts : (scanResult.CpuThermalSpecTdpWatts > 0 ? scanResult.CpuThermalSpecTdpWatts : _capabilities.ThermalSpecPowerWatts)

                        };

                    }

                }

                else

                {

                    _settings.BiosHardwareFingerprint = overrideSignature;

                }



                if (caps.IsSupported)

                {

                    if (caps.ScannedMinPowerWatts > 0 && _settings.CpuMinAllowedPowerWatts <= 0) _settings.CpuMinAllowedPowerWatts = caps.ScannedMinPowerWatts;

                    if (caps.ScannedMaxPl1Watts > 0 && _settings.CpuMaxAllowedPl1Watts <= 0) _settings.CpuMaxAllowedPl1Watts = caps.ScannedMaxPl1Watts;

                    if (caps.ScannedMaxPl2Watts > 0 && _settings.CpuMaxAllowedPl2Watts <= 0) _settings.CpuMaxAllowedPl2Watts = caps.ScannedMaxPl2Watts;

                    if (caps.ThermalSpecPowerWatts > 0 && _settings.CpuThermalSpecTdpWatts <= 0) _settings.CpuThermalSpecTdpWatts = caps.ThermalSpecPowerWatts;

                    _settings.HasCompletedBiosPowerScan = true;







                    // Native pre-override scan:

                    // If untouched BIOS default for that mode not yet recorded (!HasVerifiedBiosDefaultForMode(mode)),

                    // read BIOS-applied PL1 & PL2 straight from MSR 0x610 / WMI and store into AppSettings.

                    if (activeMode == OperatingMode.Balanced && !_settings.HasVerifiedBiosDefaultForMode(OperatingMode.Balanced) && caps.CurrentPl1Watts > 0 && caps.CurrentPl2Watts > 0)

                    {

                        int sampledPl1 = caps.CurrentPl1Watts;

                        int sampledPl2 = caps.CurrentPl2Watts;

                        if (IsDirtyCustomRegister(OperatingMode.Balanced, sampledPl1, sampledPl2))

                        {

                            var clean = GetCleanBiosOrAcpiBaselineForMode(OperatingMode.Balanced);

                            sampledPl1 = clean.Pl1;

                            sampledPl2 = clean.Pl2;

                        }

                        _settings.RecordBiosDefaultLimitsForMode(OperatingMode.Balanced, sampledPl1, sampledPl2, isVerifiedBiosScan: true);

                    }

                }



                // NON-INTRUSIVE ZERO-ASSUMPTION RULE:

                // Never touch hardware on startup unless EnableCustomPowerLimits == true.

                // Only after native BIOS defaults recorded, if EnableCustomPowerLimits == true, apply custom limits via PawnIO.

                if (_settings.EnableCustomPowerLimits && caps.IsSupported && !caps.IsLocked)

                {

                    var (userPl1, userPl2) = _settings.GetUserPowerLimitsForMode(activeMode);

                    bool hasUserLimits = userPl1 > 0 && userPl2 > 0;

                    var (rawPl1, rawPl2) = _settings.GetEffectiveLimitsForMode(activeMode);

                    var (effPl1, effPl2) = ApplyThermalSafetyCap(rawPl1, rawPl2);

                    var res = _pawnIoController.ApplyPowerLimits(effPl1, effPl2);

                    if (res.IsSuccess && hasUserLimits)

                    {

                        _settings.HardwareHasActiveCustomPowerOverride = true;

                        RegistrySafety.SaveCpuPowerLimits(_settings);

                    }

                    PowerLimitsChanged?.Invoke(activeMode, effPl1, effPl2, hasUserLimits);

                }



                _settings.Save();

                return _capabilities;

            }

        }



        /// <summary>

        /// Reads the current operating mode from WmiController or AppSettings.

        /// </summary>

        public OperatingMode GetCurrentOperatingMode()

        {

            if (_wmi != null && _wmi.TryGetPowerMode(out OperatingMode wmiMode))

            {

                return wmiMode;

            }



            if (Enum.IsDefined(typeof(OperatingMode), _settings.PowerMode))

            {

                return (OperatingMode)_settings.PowerMode;

            }



            return OperatingMode.Balanced;

        }



        /// <summary>

        /// Scans the user's device for PawnIO kernel driver and service availability.

        /// </summary>

        public PawnIoScanResult ScanDeviceForPawnIo() => _pawnIoController.ScanDeviceForPawnIo();



        /// <summary>

        /// Handles non-blocking asynchronous 250ms settle delay for Acer EC/BIOS transition on power mode changes,

        /// captures stock factory PL1 & PL2 from live MSR 0x610 into AppSettings if not yet recorded,

        /// automatically writes effective target PL1 & PL2 to MSR 0x610 and MMIO 0xFED159A0 if custom limits are enabled,

        /// and fires the PowerLimitsChanged event so UI updates automatically.

        /// </summary>

        public async Task OnPowerModeChanged(OperatingMode mode, int settleDelayMs = 250, CancellationToken ct = default)

        {

            CancellationTokenSource linkedCts;

            lock (_modeTransitionLock)

            {

                try { _modeTransitionCts?.Cancel(); _modeTransitionCts?.Dispose(); } catch { }

                _modeTransitionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                linkedCts = _modeTransitionCts;

            }



            if (settleDelayMs > 0)

            {

                try

                {

                    await Task.Delay(settleDelayMs, linkedCts.Token).ConfigureAwait(false);

                }

                catch (OperationCanceledException)

                {

                    return;

                }

            }



            if (linkedCts.IsCancellationRequested) return;

            CaptureStockModeBaselineCore(mode);

        }



        /// <summary>

        /// Asynchronous alias for OnPowerModeChanged.

        /// </summary>

        public Task OnPowerModeChangedAsync(OperatingMode mode, CancellationToken ct = default, int settleDelayMs = 250)

            => OnPowerModeChanged(mode, settleDelayMs, ct);



        /// <summary>

        /// Waits 250ms for Acer EC settle delay after a mode switch, reads stock PL1/PL2 from MSR 0x610,

        /// records in AppSettings if not yet scanned, and applies custom limits if enabled.

        /// </summary>

        public async Task<bool> CaptureStockModeBaselineIfNeededAsync(OperatingMode mode, CancellationToken ct = default, int settleDelayMs = 250)

        {

            if (settleDelayMs > 0)

            {

                try

                {

                    await Task.Delay(settleDelayMs, ct).ConfigureAwait(false);

                }

                catch (OperationCanceledException)

                {

                    return false;

                }

            }



            return CaptureStockModeBaselineCore(mode);

        }



        /// <summary>

        /// Synchronous version of CaptureStockModeBaselineIfNeeded.

        /// </summary>

        public bool CaptureStockModeBaselineIfNeeded(OperatingMode mode, int settleDelayMs = 250)

        {

            if (settleDelayMs > 0)

            {

                Thread.Sleep(settleDelayMs);

            }



            return CaptureStockModeBaselineCore(mode);

        }



        private bool CaptureStockModeBaselineCore(OperatingMode mode)

        {

            int effectivePl1;

            int effectivePl2;

            bool isCustom;



            lock (_syncLock)

            {

                var (userPl1, userPl2) = _settings.GetUserPowerLimitsForMode(mode);

                bool hasUserLimits = userPl1 > 0 && userPl2 > 0;



                if (!_pawnIoController.IsAvailable)

                {

                    var (fallbackPl1, fallbackPl2) = _settings.GetEffectivePowerLimitsForMode(mode);

                    effectivePl1 = fallbackPl1;

                    effectivePl2 = fallbackPl2;

                    isCustom = hasUserLimits;

                }

                else

                {

                    if (!_pawnIoController.ReadPowerUnits(out uint unitsPerWatt, out _))

                        return false;



                    if (!_pawnIoController.ReadPowerLimits(unitsPerWatt, out int currentPl1, out int currentPl2, out bool isLocked, out _))

                        return false;



                    if (currentPl1 <= 0 || currentPl2 <= 0)

                        return false;



                    // Native pre-override baseline capture:

                    // If untouched BIOS default for that mode not yet recorded (!HasVerifiedBiosDefaultForMode(mode)),

                    // read BIOS-applied PL1 & PL2 straight from MSR 0x610 / WMI and store into AppSettings.

                    if (!_settings.HasVerifiedBiosDefaultForMode(mode))

                    {

                        if (IsDirtyCustomRegister(mode, currentPl1, currentPl2))

                        {

                            var clean = GetCleanBiosOrAcpiBaselineForMode(mode);

                            _settings.RecordBiosDefaultLimitsForMode(mode, clean.Pl1, clean.Pl2, isVerifiedBiosScan: true);

                        }

                        else

                        {

                            _settings.RecordBiosDefaultLimitsForMode(mode, currentPl1, currentPl2, isVerifiedBiosScan: true);

                        }

                        _settings.Save();

                    }



                    // Only after native BIOS defaults recorded, if EnableCustomPowerLimits == true (or custom limits exist for mode), apply custom limits via PawnIO

                    bool shouldApplyCustom = (_settings.EnableCustomPowerLimits || hasUserLimits) && !isLocked;

                    if (shouldApplyCustom)

                    {

                        var (rawPl1, rawPl2) = _settings.GetEffectiveLimitsForMode(mode);

                        var (targetPl1, targetPl2) = ApplyThermalSafetyCap(rawPl1, rawPl2);

                        if (targetPl1 != currentPl1 || targetPl2 != currentPl2)

                        {

                            var res = _pawnIoController.ApplyPowerLimits(targetPl1, targetPl2);

                            if (res.IsSuccess)

                            {

                                effectivePl1 = targetPl1;

                                effectivePl2 = targetPl2;

                                isCustom = hasUserLimits;

                                if (hasUserLimits)

                                {

                                    _settings.HardwareHasActiveCustomPowerOverride = true;

                                    _settings.Save();

                                    RegistrySafety.SaveCpuPowerLimits(_settings);

                                }

                            }

                            else

                            {

                                effectivePl1 = currentPl1;

                                effectivePl2 = currentPl2;

                                isCustom = false;

                            }

                        }

                        else

                        {

                            effectivePl1 = targetPl1;

                            effectivePl2 = targetPl2;

                            isCustom = hasUserLimits;

                        }

                    }

                    else

                    {

                        var (defPl1, defPl2, _) = _settings.GetBiosDefaultLimitsForMode(mode);

                        effectivePl1 = currentPl1 > 0 ? currentPl1 : defPl1;

                        effectivePl2 = currentPl2 > 0 ? currentPl2 : defPl2;

                        isCustom = false;

                    }

                }

            }



            // Fire event outside syncLock so UI updates automatically and thread-safely

            PowerLimitsChanged?.Invoke(mode, effectivePl1, effectivePl2, isCustom);

            return true;

        }



        /// <summary>

        /// Rate-limited periodic verification hook for telemetry loop (rate-limited to 2-3 seconds).

        /// Re-applies target power limits if EC or Windows power scheme modified MSR 0x610.

        /// </summary>

        public void PeriodicVerifyPowerLimits(OperatingMode currentMode, bool force = false)

        {

            if (_disposed || _isCalibrating) return;

            var (userPl1, userPl2) = _settings.GetUserPowerLimitsForMode(currentMode);

            bool hasCustom = _settings.EnableCustomPowerLimits && (userPl1 > 0 && userPl2 > 0);

            if (!hasCustom && !_thermalSafetyCapWatts.HasValue)

                return;



            DateTime now = DateTime.UtcNow;

            if (!force && (now - _lastVerifyTime < VerifyInterval))

                return;



            bool fireEvent = false;

            int eventPl1 = 0, eventPl2 = 0;



            lock (_syncLock)

            {

                if (_disposed || _isCalibrating) return;

                if (!force && (now - _lastVerifyTime < VerifyInterval))

                    return;

                _lastVerifyTime = now;



                if (!_pawnIoController.IsAvailable)

                    return;



                if (!_pawnIoController.ReadPowerUnits(out uint unitsPerWatt, out _))

                    return;



                if (!_pawnIoController.ReadPowerLimits(unitsPerWatt, out int currentPl1, out int currentPl2, out bool isLocked, out _))

                    return;



                if (isLocked)

                    return;



                var (rawPl1, rawPl2) = _settings.GetEffectivePowerLimitsForMode(currentMode);

                var (targetPl1, targetPl2) = ApplyThermalSafetyCap(rawPl1, rawPl2);



                if (Math.Abs(currentPl1 - targetPl1) > 1 || Math.Abs(currentPl2 - targetPl2) > 1)

                {

                    var res = _pawnIoController.ApplyPowerLimits(targetPl1, targetPl2);

                    if (res.IsSuccess)

                    {

                        _settings.HardwareHasActiveCustomPowerOverride = hasCustom;

                        _settings.Save();

                        RegistrySafety.SaveCpuPowerLimits(_settings);

                        fireEvent = true;

                        eventPl1 = targetPl1;

                        eventPl2 = targetPl2;

                    }

                }

            }



            if (fireEvent)

            {

                // Fire event outside syncLock to prevent cross-thread UI deadlocks

                PowerLimitsChanged?.Invoke(currentMode, eventPl1, eventPl2, true);

            }

        }



        /// <summary>

        /// Multi-mode full calibration scan: cycles or probes all 5 operating modes (Eco, Quiet, Balanced, Performance, Turbo),

        /// ensuring all 5 modes obtain true untouched factory defaults in AppSettings from live MSR 0x610,

        /// and safely restores the original operating mode.

        /// </summary>

        public async Task<IReadOnlyDictionary<OperatingMode, (int Pl1, int Pl2)>> PerformFullHardwareCalibrationScanAsync(

            CancellationToken ct = default,

            int settleDelayMs = 250,

            bool forceRescan = false,

            Func<OperatingMode, Task>? modeSwitchAction = null)

        {

            _isCalibrating = true;

            try

            {

            var allModes = new[]

            {

                OperatingMode.Eco,

                OperatingMode.Quiet,

                OperatingMode.Balanced,

                OperatingMode.Performance,

                OperatingMode.Turbo

            };



            if (!forceRescan && allModes.All(m => _settings.HasScannedDefaultsForMode(m)))

            {

                var cachedResults = new Dictionary<OperatingMode, (int Pl1, int Pl2)>();

                foreach (var m in allModes)

                {

                    cachedResults[m] = _settings.GetDefaultPowerLimitsForMode(m);

                }

                return cachedResults;

            }



            OperatingMode originalMode = GetCurrentOperatingMode();

            var results = new Dictionary<OperatingMode, (int Pl1, int Pl2)>();



            foreach (var m in allModes)

            {

                ct.ThrowIfCancellationRequested();



                if (!forceRescan && _settings.HasScannedDefaultsForMode(m))

                {

                    results[m] = _settings.GetDefaultPowerLimitsForMode(m);

                    continue;

                }



                if (modeSwitchAction != null)

                {

                    await modeSwitchAction(m).ConfigureAwait(false);

                }

                else if (_wmi != null)

                {

                    _wmi.TrySetOperatingMode(m, forceRetry: true);

                }



                if (settleDelayMs > 0)

                {

                    await Task.Delay(settleDelayMs, ct).ConfigureAwait(false);

                }



                lock (_syncLock)

                {

                    if (_pawnIoController.IsAvailable &&

                        _pawnIoController.ReadPowerUnits(out uint unitsPerWatt, out _) &&

                        _pawnIoController.ReadPowerLimits(unitsPerWatt, out int curPl1, out int curPl2, out _, out _))

                    {

                        if (curPl1 > 0 && curPl2 > 0)

                        {

                            if (IsDirtyCustomRegister(m, curPl1, curPl2))

                            {

                                var clean = GetCleanBiosOrAcpiBaselineForMode(m);

                                curPl1 = clean.Pl1;

                                curPl2 = clean.Pl2;

                            }

                            _settings.RecordScannedDefaultsForMode(m, curPl1, curPl2, force: forceRescan);

                            results[m] = (curPl1, curPl2);

                        }

                    }



                    if (!results.ContainsKey(m))

                    {

                        results[m] = _settings.GetDefaultPowerLimitsForMode(m);

                    }

                }

            }



            // Restore original operating mode

            if (modeSwitchAction != null)

            {

                try { await modeSwitchAction(originalMode).ConfigureAwait(false); } catch { }

            }

            else if (_wmi != null)

            {

                try { _wmi.TrySetOperatingMode(originalMode, forceRetry: true); } catch { }

            }



            if (settleDelayMs > 0)

            {

                try { await Task.Delay(settleDelayMs, ct).ConfigureAwait(false); } catch { }

            }



            lock (_syncLock)

            {

                _settings.Save();



                var (userPl1, userPl2) = _settings.GetUserPowerLimitsForMode(originalMode);

                bool hasUser = userPl1 > 0 && userPl2 > 0;

                if ((_settings.EnableCustomPowerLimits || hasUser) && _pawnIoController.IsAvailable)

                {

                    var (rawPl1, rawPl2) = _settings.GetEffectivePowerLimitsForMode(originalMode);

                    var (effPl1, effPl2) = ApplyThermalSafetyCap(rawPl1, rawPl2);

                    var res = _pawnIoController.ApplyPowerLimits(effPl1, effPl2);

                    if (res.IsSuccess && hasUser)

                    {

                        _settings.HardwareHasActiveCustomPowerOverride = true;

                        RegistrySafety.SaveCpuPowerLimits(_settings);

                    }

                }

            }



            var (finalPl1, finalPl2, finalCustom) = GetDisplayPowerLimits(originalMode);

            PowerLimitsChanged?.Invoke(originalMode, finalPl1, finalPl2, finalCustom);



            return results;

            }

            finally

            {

                _isCalibrating = false;

            }

        }



        public Task<IReadOnlyDictionary<OperatingMode, (int Pl1, int Pl2)>> PerformFullHardwareCalibrationScanAsync()

            => PerformFullHardwareCalibrationScanAsync(CancellationToken.None, 250, false, null);





        public PowerLimitApplyResult ApplyPowerLimitsForMode(OperatingMode mode, bool forceHardwareApply = false)

        {

            if (_disposed)

                return PowerLimitApplyResult.DriverNotInstalled("Controller disposed");

            PowerLimitApplyResult res;

            int effPl1, effPl2;
            int targetPl1 = 0, targetPl2 = 0;

            bool hasUserLimits;



            lock (_syncLock)

            {

                if (_disposed)

                    return PowerLimitApplyResult.DriverNotInstalled("Controller disposed");

                var (userPl1, userPl2) = _settings.GetUserPowerLimitsForMode(mode);

                hasUserLimits = userPl1 > 0 && userPl2 > 0;

                var (ePl1, ePl2) = _settings.GetEffectiveLimitsForMode(mode);
                targetPl1 = ePl1;
                targetPl2 = ePl2;

                (effPl1, effPl2) = ApplyThermalSafetyCap(ePl1, ePl2);



                if (_wmi != null)

                {

                    // The power mode switch guard owns the single mode write.

                    // Re-writing the mode here used to double the EC traffic on

                    // every switch and stuttered media playback.

                    _ = _wmi;

                }



                if ((_settings.EnableCustomPowerLimits || hasUserLimits || forceHardwareApply || _thermalSafetyCapWatts.HasValue) &&

                    (_pawnIoController.IsAvailable || _pawnIoController.EnsureInitialized() == PowerLimitApplyStatus.Success))

                {

                    res = _pawnIoController.ApplyPowerLimits(effPl1, effPl2);

                    if (res.IsSuccess && hasUserLimits)

                    {

                        _settings.HardwareHasActiveCustomPowerOverride = true;

                        _settings.Save();

                        RegistrySafety.SaveCpuPowerLimits(_settings);

                    }

                }

                else

                {

                    res = PowerLimitApplyResult.Success(effPl1, effPl2, false);

                }

            }



            // Fire event outside syncLock to prevent cross-thread UI deadlocks

            PowerLimitsChanged?.Invoke(mode, targetPl1, targetPl2, hasUserLimits);

            return res;

        }



        /// <summary>

        /// Immediately and reliably applies active profile CPU power limits (PL1/PL2 via PawnIO and Acer ACPI WMI)

        /// on startup/boot when custom limits are enabled, without requiring the user to switch power modes.

        /// </summary>

        public PowerLimitApplyResult ApplyActiveModePowerLimits(OperatingMode? mode = null)

        {

            OperatingMode targetMode = mode ?? GetCurrentOperatingMode();

            PowerLimitApplyResult res;

            int effPl1, effPl2;
            int targetPl1 = 0, targetPl2 = 0;

            bool hasCustom;



            lock (_syncLock)

            {

                var (ePl1, ePl2) = _settings.GetEffectiveLimitsForMode(targetMode);
                targetPl1 = ePl1;
                targetPl2 = ePl2;

                (effPl1, effPl2) = ApplyThermalSafetyCap(ePl1, ePl2);

                hasCustom = _settings.HasCustomPowerLimitsForMode(targetMode);



                if (effPl1 <= 0 || effPl2 <= 0)

                {

                    return PowerLimitApplyResult.HardwareError("Invalid effective power limits for mode.");

                }



                if (effPl2 < effPl1) effPl2 = effPl1;



                if (_wmi != null)

                {

                    // Mode write is owned by PowerModeSwitchGuard; see above.

                    _ = _wmi;

                }



                if (_pawnIoController.IsAvailable || _pawnIoController.EnsureInitialized() == PowerLimitApplyStatus.Success)

                {

                    res = _pawnIoController.ApplyPowerLimits(effPl1, effPl2);

                    if (res.IsSuccess && hasCustom)

                    {

                        _settings.HardwareHasActiveCustomPowerOverride = true;

                        _settings.Save();

                        RegistrySafety.SaveCpuPowerLimits(_settings);

                    }

                }

                else

                {

                    return PowerLimitApplyResult.DriverNotInstalled();

                }

            }



            // Fire event outside syncLock to prevent cross-thread UI deadlocks

            PowerLimitsChanged?.Invoke(targetMode, targetPl1, targetPl2, hasCustom);

            return res;

        }



        /// <summary>

        /// Alias for ApplyActiveModePowerLimits for startup initialization.

        /// </summary>

        public PowerLimitApplyResult ApplyStartupPowerLimits(OperatingMode? activeMode = null)

            => ApplyActiveModePowerLimits(activeMode);



        public PowerLimitApplyResult SetPowerLimitsForMode(OperatingMode mode, int pl1Watts, int pl2Watts)

        {

            OperatingMode activeMode = GetCurrentOperatingMode();

            PowerLimitApplyResult? res = null;

            int effPl1 = pl1Watts, effPl2 = pl2Watts;

            bool isActiveMode = false;



            lock (_syncLock)

            {

                _settings.EnableCustomPowerLimits = true;

                _settings.SetPowerLimitsForMode(mode, pl1Watts, pl2Watts);



                if (mode == activeMode)

                {

                    isActiveMode = true;

                    var (ePl1, ePl2) = _settings.GetEffectivePowerLimitsForMode(mode);

                    (effPl1, effPl2) = ApplyThermalSafetyCap(ePl1, ePl2);

                    res = _pawnIoController.ApplyPowerLimits(effPl1, effPl2);

                    if (res.IsSuccess)

                    {

                        _settings.HardwareHasActiveCustomPowerOverride = true;

                        _settings.Save();

                        RegistrySafety.SaveCpuPowerLimits(_settings);

                    }

                }

                else

                {

                    _settings.Save();

                    var (clamped1, clamped2) = _settings.ClampPowerLimitWatts(pl1Watts, pl2Watts);

                    res = PowerLimitApplyResult.Success(clamped1, clamped2, false);

                    effPl1 = clamped1;

                    effPl2 = clamped2;

                }

            }



            // Fire event outside syncLock to prevent cross-thread UI deadlocks

            if (isActiveMode)

            {

                PowerLimitsChanged?.Invoke(mode, effPl1, effPl2, true);

            }

            return res ?? PowerLimitApplyResult.Success(pl1Watts, pl2Watts, false);

        }



        /// <summary>

        /// Restores stock defaults for a single mode.

        /// </summary>

        public PowerLimitApplyResult ResetModeToStockDefaults(OperatingMode mode)

        {

            OperatingMode activeMode = GetCurrentOperatingMode();

            PowerLimitApplyResult? res = null;

            int defPl1, defPl2;

            bool isActiveMode = false;



            lock (_syncLock)

            {

                _settings.ResetPowerLimitsForMode(mode);

                if (!_settings.HasAnyCustomLimits())

                {

                    _settings.HardwareHasActiveCustomPowerOverride = false;

                }

                _settings.Save();

                RegistrySafety.SaveCpuPowerLimits(_settings);



                var (dPl1, dPl2) = _settings.GetDefaultPowerLimitsForMode(mode);

                defPl1 = dPl1;

                defPl2 = dPl2;



                if (mode == activeMode)

                {

                    isActiveMode = true;

                    var (cappedPl1, cappedPl2) = ApplyThermalSafetyCap(defPl1, defPl2);

                    res = _pawnIoController.ApplyPowerLimits(cappedPl1, cappedPl2);

                    if (!_settings.HasAnyCustomLimits())

                    {

                        _settings.HardwareHasActiveCustomPowerOverride = false;

                        RegistrySafety.SaveCpuPowerLimits(_settings);

                    }

                }

                else

                {

                    res = PowerLimitApplyResult.Success(defPl1, defPl2, false);

                }

            }



            // Fire event outside syncLock to prevent cross-thread UI deadlocks

            if (isActiveMode)

            {

                PowerLimitsChanged?.Invoke(mode, defPl1, defPl2, false);

            }

            return res ?? PowerLimitApplyResult.Success(defPl1, defPl2, false);

        }



        /// <summary>

        /// Restores stock defaults across all five operating modes.

        /// </summary>

        public PowerLimitApplyResult ResetAllModesToStockDefaults()

        {

            OperatingMode activeMode = GetCurrentOperatingMode();

            int defPl1, defPl2;

            PowerLimitApplyResult res;



            lock (_syncLock)

            {

                _settings.ResetAllPowerLimitsToScannedDefaults();

                _settings.HardwareHasActiveCustomPowerOverride = false;

                _settings.Save();

                RegistrySafety.SaveCpuPowerLimits(_settings);



                var (d1, d2) = _settings.GetDefaultPowerLimitsForMode(activeMode);

                defPl1 = d1;

                defPl2 = d2;



                var (cappedPl1, cappedPl2) = ApplyThermalSafetyCap(defPl1, defPl2);
                res = _pawnIoController.ApplyPowerLimits(cappedPl1, cappedPl2);

                _settings.HardwareHasActiveCustomPowerOverride = false;

                RegistrySafety.SaveCpuPowerLimits(_settings);

            }



            // Fire event outside syncLock to prevent cross-thread UI deadlocks

            PowerLimitsChanged?.Invoke(activeMode, defPl1, defPl2, false);

            return res;

        }



        /// <summary>

        /// Returns display power limits and whether they represent user-customized values.

        /// </summary>

        public (int pl1, int pl2, bool isCustom) GetDisplayPowerLimits(OperatingMode mode)

        {

            var (userPl1, userPl2) = _settings.GetUserPowerLimitsForMode(mode);

            var (effPl1, effPl2) = _settings.GetEffectivePowerLimitsForMode(mode);

            bool isCustom = userPl1 > 0 && userPl2 > 0;

            return (effPl1, effPl2, isCustom);

        }



        private bool IsDirtyCustomRegister(OperatingMode mode, int sampledPl1, int sampledPl2)

        {

            if (_thermalSafetyCapWatts.HasValue && (sampledPl1 == _thermalSafetyCapWatts.Value || sampledPl2 == _thermalSafetyCapWatts.Value))
            {
                return true;
            }

            var (userPl1, userPl2) = _settings.GetUserPowerLimitsForMode(mode);

            int baseTdp = _settings.CpuThermalSpecTdpWatts > 0 ? _settings.CpuThermalSpecTdpWatts : 45;

            double threshold = baseTdp * 1.5;



            if (_settings.HardwareHasActiveCustomPowerOverride && ((userPl1 > 0 && sampledPl1 == userPl1) || (userPl2 > 0 && sampledPl2 == userPl2)))

            {

                return true;

            }



            if (userPl1 > 0 && (sampledPl1 == userPl1 || sampledPl2 == userPl2) && sampledPl1 > threshold)

            {

                return true;

            }

// Removed false-positive dirty register check

            return false;

        }



        private (int Pl1, int Pl2) GetCleanBiosOrAcpiBaselineForMode(OperatingMode mode)

        {

            if (_capabilities.BiosPowerScan?.Boundaries?.RecommendedModeDefaults != null &&

                _capabilities.BiosPowerScan.Boundaries.RecommendedModeDefaults.TryGetValue(mode, out var rec) &&

                rec.Pl1Watts > 0 && rec.Pl2Watts > 0)

            {

                return (rec.Pl1Watts, rec.Pl2Watts);

            }



            int tdp = _settings.CpuThermalSpecTdpWatts > 0 ? _settings.CpuThermalSpecTdpWatts : 45;

            int min = _settings.CpuMinAllowedPowerWatts > 0 ? _settings.CpuMinAllowedPowerWatts : 12;

            int max1 = _settings.CpuMaxAllowedPl1Watts > 0 ? _settings.CpuMaxAllowedPl1Watts : 115;

            int max2 = _settings.CpuMaxAllowedPl2Watts > 0 ? _settings.CpuMaxAllowedPl2Watts : 140;



            var modeDefs = CpuSpecDetector.ComputeModeDefaults(tdp, min, max1, max2);

            byte modeByte = (byte)mode;

            if (modeDefs.TryGetValue(modeByte, out var p))

            {

                return p;

            }



            return _settings.EstimateProportionalDefaultsForMode(mode);

        }



        private int? _thermalSafetyCapWatts;

        public int? ThermalSafetyCapWatts { get { lock (_syncLock) return _thermalSafetyCapWatts; } }



        private (int EffPl1, int EffPl2) ApplyThermalSafetyCap(int pl1, int pl2)

        {

            int safePl1 = pl1;

            int safePl2 = Math.Max(safePl1, pl2);



            if (_thermalSafetyCapWatts.HasValue && _thermalSafetyCapWatts.Value > 0)

            {

                int minW = _settings.CpuMinAllowedPowerWatts > 0 ? _settings.CpuMinAllowedPowerWatts : 5;

                int cap = Math.Max(minW, _thermalSafetyCapWatts.Value);

                int cappedPl1 = Math.Min(safePl1, cap);

                int cappedPl2 = Math.Min(safePl2, cap);

                if (cappedPl2 < cappedPl1) cappedPl2 = cappedPl1;

                return (cappedPl1, cappedPl2);

            }

            return (safePl1, safePl2);

        }



        /// <summary>

        /// Sets a transient runtime thermal safety power limit cap in watts.

        /// Clamps EffectivePl1 and EffectivePl2 in hardware to Math.Min(TargetPl, _thermalSafetyCapWatts)

        /// WITHOUT modifying AppSettings.Custom{Mode}Pl1/Pl2Watts or AppSettings.BiosDefault{Mode}Pl1/Pl2Watts.

        /// </summary>

        public void SetThermalSafetyCap(int? capWatts)

        {

            OperatingMode activeMode;

            bool capChanged = false;

            lock (_syncLock)

            {

                if (_thermalSafetyCapWatts == capWatts)

                    return;



                _thermalSafetyCapWatts = capWatts;

                capChanged = true;

                activeMode = GetCurrentOperatingMode();

            }



            if (_settings.EnableCustomPowerLimits || _pawnIoController.IsAvailable)

            {

                ApplyPowerLimitsForMode(activeMode, forceHardwareApply: capChanged);

            }

        }



        public void SetThermalSafetyCap(int capWatts)

        {

            SetThermalSafetyCap(capWatts > 0 ? capWatts : null);

        }



        /// <summary>

        /// Clears transient runtime thermal safety cap, restoring the user's full configured TargetPl1 and TargetPl2.

        /// </summary>

        public void ClearThermalSafetyCap()

        {

            SetThermalSafetyCap(null);

        }



        public void Dispose()

        {

            if (_disposed)

                return;



            _disposed = true;

            lock (_modeTransitionLock)

            {

                try { _modeTransitionCts?.Cancel(); _modeTransitionCts?.Dispose(); _modeTransitionCts = null; } catch { }

            }

            if (_ownsPawnIo)

            {

                _pawnIoController.Dispose();

            }

        }

    }

}

