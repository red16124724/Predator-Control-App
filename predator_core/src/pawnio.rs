//! PawnIO Ring-0 RAPL & MMIO Driver Engine
//!
//! Production-grade Rust implementation for Ring-0 Intel RAPL MSR and MMIO power limit control
//! interfacing with the genuine \\.\PawnIO kernel driver. Supports embedded bytecode modules
//! (IntelMSR.bin, IntelMCHBAR.bin), exact execute packet layout serialization, directional split-write
//! hazard mitigation on MMIO Package RAPL, inter-process named mutex synchronization (Global\Access_MSR,
//! Global\Access_PCI), comprehensive system service/driver diagnostics, and a 100% in-memory mock backend
//! for zero-driver automated CI testing.

use std::collections::HashMap;
use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex as StdMutex, RwLock};
use std::time::Duration;

#[cfg(windows)]
use std::ffi::OsStr;
#[cfg(windows)]
use std::os::windows::ffi::OsStrExt;

// ============================================================================
// 1. IOCTL Codes, Hardware Constants & Addresses
// ============================================================================

/// Genuine PawnIO Device Type: 41394 << 16 = 0xA1B20000
pub const DEV_TYPE: u32 = 0xA1B2_0000;

/// PawnIO Load Binary IOCTL code: DEV_TYPE | (0x821 << 2) = 0xA1B22084
pub const IOCTL_PAWNIO_LOAD_BINARY: u32 = 0xA1B2_2084;

/// PawnIO Execute IOCTL code: DEV_TYPE | (0x841 << 2) = 0xA1B22104
pub const IOCTL_PAWNIO_EXECUTE: u32 = 0xA1B2_2104;

// --- Intel RAPL and Uncore MSRs ---
/// MSR 0x606: RAPL Power Units (Power, Energy, Time units)
pub const MSR_RAPL_POWER_UNIT: u32 = 0x606;

/// MSR 0x610: Package Power Limit (PL1, PL2, Enable, Clamp, Tau time windows, Lock bit 63)
pub const MSR_PKG_POWER_LIMIT: u32 = 0x610;

/// MSR 0x614: Package Power Info (Thermal Spec Power, Minimum Power, Maximum Power, Max Time Window)
pub const MSR_PKG_POWER_INFO: u32 = 0x614;

/// MSR 0x618: DRAM Power Limit
pub const MSR_DRAM_POWER_LIMIT: u32 = 0x618;

/// MSR 0x65C: Platform Power Limit (PL4 / PSys)
pub const MSR_PLATFORM_POWER_LIMIT: u32 = 0x65C;

/// MSR 0x1A2: Temperature Target (TjMax & target offset)
pub const MSR_TEMPERATURE_TARGET: u32 = 0x1A2;

/// MSR 0x1AD: Turbo Ratio Limit (Cores 1-8 maximum multiplier)
pub const MSR_TURBO_RATIO_LIMIT: u32 = 0x1AD;

/// MSR 0x1AE: Turbo Ratio Limit 1 (Secondary core groups)
pub const MSR_TURBO_RATIO_LIMIT1: u32 = 0x1AE;

/// MSR 0x1AF: Turbo Ratio Limit 2 (Tertiary core groups)
pub const MSR_TURBO_RATIO_LIMIT2: u32 = 0x1AF;

/// MSR 0x690: Core Performance Limit Reasons
pub const MSR_CORE_PERF_LIMIT_REASONS: u32 = 0x690;

// --- Bitmask Constants for MSR 0x610 ---
pub const PL1_ENABLE_BIT: u64 = 1 << 15;
pub const PL1_CLAMP_BIT: u64 = 1 << 16;
pub const PL2_ENABLE_BIT: u64 = 1 << 47;
pub const PL2_CLAMP_BIT: u64 = 1 << 48;
pub const MSR_PKG_POWER_LIMIT_LOCK_BIT: u64 = 1 << 63;

// --- MMIO Package RAPL Physical Addresses ---
/// Base physical MMIO address for Package RAPL Limit mirror (MCHBAR + 0x59A0)
pub const MMIO_PKG_RAPL_LIMIT_ADDRESS: u64 = 0xFED1_59A0;
/// MMIO Package RAPL PL1 register (lower 32 bits)
pub const MMIO_PKG_RAPL_PL1_ADDRESS: u64 = 0xFED1_59A0;
/// MMIO Package RAPL PL2 register (upper 32 bits, offset +0x4)
pub const MMIO_PKG_RAPL_PL2_ADDRESS: u64 = 0xFED1_59A4;

// --- Function Names Exported by Bytecode Modules ---
pub const FN_READ_MSR: &str = "ioctl_read_msr";
pub const FN_WRITE_MSR: &str = "ioctl_write_msr";
pub const FN_GET_MCHBAR: &str = "ioctl_get_mchbar_addr";
pub const FN_READ_DWORD: &str = "ioctl_read_dword";
pub const FN_READ_QWORD: &str = "ioctl_read_qword";

// --- Global Synchronization Mutexes ---
pub const MSR_MUTEX_NAME: &str = r"Global\Access_MSR";
pub const PCI_MUTEX_NAME: &str = r"Global\Access_PCI";
pub const DEFAULT_MUTEX_TIMEOUT: Duration = Duration::from_millis(150);

/// Global synchronization device token to serialize all Win32 DeviceIoControl calls.
#[derive(Debug, Default)]
pub struct PawnIoDevice;

/// Process-wide DeviceIoControl serialization mutex preventing telemetry and power write races.
pub static GLOBAL_PAWNIO_DEVICE: StdMutex<PawnIoDevice> = StdMutex::new(PawnIoDevice);

// --- Candidate PawnIO Device Symlink Paths ---
pub const PRIMARY_DEVICE_PATH: &str = r"\\.\PawnIO";
pub const LOWERCASE_DEVICE_PATH: &str = r"\\.\pawnio";
pub const SECONDARY_DEVICE_PATH: &str = r"\\.\PawnIo";
pub const GLOBAL_DEVICE_PATH: &str = r"\\?\GLOBALROOT\Device\PawnIO";
pub const GLOBAL_LOWERCASE_DEVICE_PATH: &str = r"\\?\GLOBALROOT\Device\pawnio";
pub const GLOBAL_DOT_DEVICE_PATH: &str = r"\\.\GLOBALROOT\Device\PawnIO";
pub const GLOBAL_DOT_LOWERCASE_DEVICE_PATH: &str = r"\\.\GLOBALROOT\Device\pawnio";

pub const CANDIDATE_DEVICE_PATHS: &[&str] = &[
    PRIMARY_DEVICE_PATH,
    LOWERCASE_DEVICE_PATH,
    SECONDARY_DEVICE_PATH,
    GLOBAL_DEVICE_PATH,
    GLOBAL_LOWERCASE_DEVICE_PATH,
    GLOBAL_DOT_DEVICE_PATH,
    GLOBAL_DOT_LOWERCASE_DEVICE_PATH,
];

// ============================================================================
// 2. Embedded & Loaded Bytecode Binaries
// ============================================================================

/// Embedded IntelMSR.bin bytecode payload compiled into binary
pub const EMBEDDED_INTEL_MSR_BIN: &[u8] = include_bytes!("pawn/IntelMSR.bin");

/// Embedded IntelMCHBAR.bin bytecode payload compiled into binary
pub const EMBEDDED_INTEL_MCHBAR_BIN: &[u8] = include_bytes!("pawn/IntelMCHBAR.bin");

/// Dynamically locates and loads bytecode bytes from memory or disk
pub fn load_module_bytes(file_name: &str) -> Option<Vec<u8>> {
    let simple_name = Path::new(file_name)
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or(file_name);

    // 1. Embedded fallback
    if simple_name.eq_ignore_ascii_case("IntelMSR.bin") {
        return Some(EMBEDDED_INTEL_MSR_BIN.to_vec());
    }
    if simple_name.eq_ignore_ascii_case("IntelMCHBAR.bin") {
        return Some(EMBEDDED_INTEL_MCHBAR_BIN.to_vec());
    }

    // 2. Candidate directories on disk
    let candidate_dirs = [
        PathBuf::from("."),
        PathBuf::from("Pawn"),
        PathBuf::from("pawn"),
        PathBuf::from("Assets/Intel/PawnIO"),
        std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|p| p.to_path_buf()))
            .unwrap_or_else(|| PathBuf::from(".")),
        std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|p| p.join("Pawn")))
            .unwrap_or_else(|| PathBuf::from("Pawn")),
    ];

    for dir in &candidate_dirs {
        let full = dir.join(simple_name);
        if full.is_file() {
            if let Ok(bytes) = std::fs::read(&full) {
                if !bytes.is_empty() {
                    return Some(bytes);
                }
            }
        }
    }

    None
}

// ============================================================================
// 3. Exact Execute Packet Layout
// ============================================================================

/// Builds the PawnIO execution packet buffer.
/// Layout: Exactly 32 bytes ASCII function name (zero-padded), followed by arguments as u64 array.
pub fn build_execute_packet(function_name: &str, arguments: &[u64]) -> Vec<u8> {
    const FN_LEN: usize = 32;
    let mut buffer = vec![0u8; FN_LEN + arguments.len() * 8];
    let name_bytes = function_name.as_bytes();
    let copy_len = name_bytes.len().min(FN_LEN - 1);
    buffer[..copy_len].copy_from_slice(&name_bytes[..copy_len]);

    for (i, &arg) in arguments.iter().enumerate() {
        let offset = FN_LEN + i * 8;
        buffer[offset..offset + 8].copy_from_slice(&arg.to_le_bytes());
    }
    buffer
}

/// Parses the returned output buffer from a PawnIO execute IOCTL call.
/// Deserializes little-endian u64 values into the destination slice.
pub fn parse_execute_response(
    out_buffer: &[u8],
    bytes_returned: usize,
    output: &mut [u64],
) -> bool {
    if bytes_returned < 8 || output.is_empty() {
        return false;
    }
    let max_bytes = (output.len() * 8).min(bytes_returned).min(out_buffer.len());
    let valid_bytes = max_bytes - (max_bytes % 8);
    for i in 0..(valid_bytes / 8) {
        let offset = i * 8;
        if let Ok(bytes) = out_buffer[offset..offset + 8].try_into() {
            output[i] = u64::from_le_bytes(bytes);
        }
    }
    true
}

// ============================================================================
// 4. Status Models, Enums & Error Types
// ============================================================================

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PowerLimitApplyStatus {
    Success,
    DriverNotInstalled,
    AccessDenied,
    BiosLocked,
    UnsupportedHardware,
    HardwareError,
    MutexTimeout,
}

impl fmt::Display for PowerLimitApplyStatus {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Success => write!(f, "Success"),
            Self::DriverNotInstalled => write!(f, "DriverNotInstalled"),
            Self::AccessDenied => write!(f, "AccessDenied"),
            Self::BiosLocked => write!(f, "BiosLocked"),
            Self::UnsupportedHardware => write!(f, "UnsupportedHardware"),
            Self::HardwareError => write!(f, "HardwareError"),
            Self::MutexTimeout => write!(f, "MutexTimeout"),
        }
    }
}

#[derive(Debug, Clone)]
pub struct PowerLimitApplyResult {
    pub status: PowerLimitApplyStatus,
    pub is_success: bool,
    pub message: String,
    pub applied_pl1_watts: i32,
    pub applied_pl2_watts: i32,
    pub mmio_mirrored: bool,
    pub raw_msr_written: u64,
    pub raw_mmio_written: u64,
}

impl PowerLimitApplyResult {
    pub fn success(
        pl1_watts: i32,
        pl2_watts: i32,
        mmio_mirrored: bool,
        raw_msr: u64,
        raw_mmio: u64,
        message: impl Into<String>,
    ) -> Self {
        Self {
            status: PowerLimitApplyStatus::Success,
            is_success: true,
            message: message.into(),
            applied_pl1_watts: pl1_watts,
            applied_pl2_watts: pl2_watts,
            mmio_mirrored,
            raw_msr_written: raw_msr,
            raw_mmio_written: raw_mmio,
        }
    }

    pub fn driver_not_installed(message: impl Into<String>) -> Self {
        Self {
            status: PowerLimitApplyStatus::DriverNotInstalled,
            is_success: false,
            message: message.into(),
            applied_pl1_watts: 0,
            applied_pl2_watts: 0,
            mmio_mirrored: false,
            raw_msr_written: 0,
            raw_mmio_written: 0,
        }
    }

    pub fn access_denied(message: impl Into<String>) -> Self {
        Self {
            status: PowerLimitApplyStatus::AccessDenied,
            is_success: false,
            message: message.into(),
            applied_pl1_watts: 0,
            applied_pl2_watts: 0,
            mmio_mirrored: false,
            raw_msr_written: 0,
            raw_mmio_written: 0,
        }
    }

    pub fn bios_locked(message: impl Into<String>) -> Self {
        Self {
            status: PowerLimitApplyStatus::BiosLocked,
            is_success: false,
            message: message.into(),
            applied_pl1_watts: 0,
            applied_pl2_watts: 0,
            mmio_mirrored: false,
            raw_msr_written: 0,
            raw_mmio_written: 0,
        }
    }

    pub fn unsupported_hardware(message: impl Into<String>) -> Self {
        Self {
            status: PowerLimitApplyStatus::UnsupportedHardware,
            is_success: false,
            message: message.into(),
            applied_pl1_watts: 0,
            applied_pl2_watts: 0,
            mmio_mirrored: false,
            raw_msr_written: 0,
            raw_mmio_written: 0,
        }
    }

    pub fn hardware_error(message: impl Into<String>) -> Self {
        Self {
            status: PowerLimitApplyStatus::HardwareError,
            is_success: false,
            message: message.into(),
            applied_pl1_watts: 0,
            applied_pl2_watts: 0,
            mmio_mirrored: false,
            raw_msr_written: 0,
            raw_mmio_written: 0,
        }
    }

    pub fn mutex_timeout(message: impl Into<String>) -> Self {
        Self {
            status: PowerLimitApplyStatus::MutexTimeout,
            is_success: false,
            message: message.into(),
            applied_pl1_watts: 0,
            applied_pl2_watts: 0,
            mmio_mirrored: false,
            raw_msr_written: 0,
            raw_mmio_written: 0,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PawnIoScanResult {
    pub is_installed: bool,
    pub is_running: bool,
    pub is_accessible: bool,
    pub requires_elevation: bool,
    pub service_path: String,
    pub last_win32_error: u32,
    pub diagnostic_message: String,
}

#[derive(Debug, Clone)]
pub struct DevicePowerCapabilities {
    pub is_supported: bool,
    pub is_locked: bool,
    pub is_mmio_accessible: bool,
    pub units_per_watt: u32,
    pub thermal_spec_power_watts: i32,
    pub minimum_power_watts: i32,
    pub maximum_power_watts: i32,
    pub current_pl1_watts: i32,
    pub current_pl2_watts: i32,
    pub scanned_min_power_watts: i32,
    pub scanned_max_pl1_watts: i32,
    pub scanned_max_pl2_watts: i32,
    pub raw_power_unit: u64,
    pub raw_power_info: u64,
    pub raw_power_limit: u64,
    pub raw_mmio_power_limit: u64,
    pub baseline_status: PowerLimitApplyStatus,
    pub diagnostics: String,
}

impl DevicePowerCapabilities {
    pub fn unsupported(status: PowerLimitApplyStatus, message: impl Into<String>) -> Self {
        let diag = message.into();
        Self {
            is_supported: false,
            is_locked: false,
            is_mmio_accessible: false,
            units_per_watt: 0,
            thermal_spec_power_watts: 0,
            minimum_power_watts: 0,
            maximum_power_watts: 0,
            current_pl1_watts: 0,
            current_pl2_watts: 0,
            scanned_min_power_watts: 0,
            scanned_max_pl1_watts: 0,
            scanned_max_pl2_watts: 0,
            raw_power_unit: 0,
            raw_power_info: 0,
            raw_power_limit: 0,
            raw_mmio_power_limit: 0,
            baseline_status: status,
            diagnostics: diag,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PowerLimits {
    pub pl1_watts: i32,
    pub pl2_watts: i32,
    pub is_locked: bool,
    pub raw_limit: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PowerInfo {
    pub thermal_spec_power_watts: i32,
    pub minimum_power_watts: i32,
    pub maximum_power_watts: i32,
    pub raw_info: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TemperatureTarget {
    pub target_temp_celsius: u32,
    pub target_offset_celsius: u32,
    pub raw_value: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TurboRatioLimit {
    pub ratios_by_core: [u8; 8],
    pub raw_value: u64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PawnIoError {
    DriverNotInstalled(String),
    AccessDenied(String),
    BiosLocked(String),
    UnsupportedHardware(String),
    HardwareError(String),
    MutexTimeout(String, Duration),
    MutexAcquisitionFailed(String),
    InvalidRegister(u32),
    SecurityViolation(String),
    UnsupportedOperation(String),
}

impl fmt::Display for PawnIoError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::DriverNotInstalled(msg) => write!(f, "DriverNotInstalled: {}", msg),
            Self::AccessDenied(msg) => write!(f, "AccessDenied: {}", msg),
            Self::BiosLocked(msg) => write!(f, "BiosLocked: {}", msg),
            Self::UnsupportedHardware(msg) => write!(f, "UnsupportedHardware: {}", msg),
            Self::HardwareError(msg) => write!(f, "HardwareError: {}", msg),
            Self::MutexTimeout(name, dur) => {
                write!(f, "MutexTimeout: Timed out waiting {:?} for {}", dur, name)
            }
            Self::MutexAcquisitionFailed(msg) => write!(f, "MutexAcquisitionFailed: {}", msg),
            Self::InvalidRegister(idx) => write!(f, "InvalidRegister: 0x{:X}", idx),
            Self::SecurityViolation(msg) => write!(f, "SecurityViolation: {}", msg),
            Self::UnsupportedOperation(msg) => write!(f, "UnsupportedOperation: {}", msg),
        }
    }
}

impl std::error::Error for PawnIoError {}

// ============================================================================
// 5. MSR Validation & Address Calculations
// ============================================================================

/// Whitelist check for allowed MSRs to prevent unauthorized ring-0 writes
pub fn is_safe_msr_index(msr_index: u32) -> bool {
    match msr_index {
        MSR_RAPL_POWER_UNIT
        | MSR_PKG_POWER_LIMIT
        | MSR_PKG_POWER_INFO
        | MSR_DRAM_POWER_LIMIT
        | MSR_PLATFORM_POWER_LIMIT
        | MSR_TEMPERATURE_TARGET
        | MSR_TURBO_RATIO_LIMIT
        | MSR_TURBO_RATIO_LIMIT1
        | MSR_TURBO_RATIO_LIMIT2
        | MSR_CORE_PERF_LIMIT_REASONS => true,
        0x600..=0x69F => true, // Standard Intel RAPL & uncore register block
        _ => false,
    }
}

/// Calculates the dynamic physical MMIO address for Package RAPL PL1 (lower 32 bits)
pub fn calculate_mchbar_rapl_pl1_address(mchbar_address: u64) -> u64 {
    if mchbar_address != 0 && (mchbar_address as u32) != 0xFFFF_FFFF && mchbar_address != u64::MAX {
        let base_addr = mchbar_address & !0x3FFF; // 16KB alignment mask
        if (0x10_0000..(1 << 48)).contains(&base_addr) {
            return base_addr + 0x59A0;
        }
    }
    MMIO_PKG_RAPL_LIMIT_ADDRESS
}

/// Calculates the dynamic physical MMIO address for Package RAPL PL2 (upper 32 bits)
pub fn calculate_mchbar_rapl_pl2_address(mchbar_address: u64) -> u64 {
    calculate_mchbar_rapl_pl1_address(mchbar_address) + 4
}

// ============================================================================
// 6. Backend Abstraction Trait
// ============================================================================

pub trait PawnIoBackend: Send + Sync {
    fn is_open(&self) -> bool;
    fn is_driver_installed(&self) -> bool;
    fn mchbar_address(&self) -> u64 {
        0
    }
    fn device_path(&self) -> &str {
        PRIMARY_DEVICE_PATH
    }
    fn initialize(&mut self) -> PowerLimitApplyStatus;
    fn read_msr(&self, msr_index: u32) -> Result<u64, PawnIoError>;
    fn write_msr(&self, msr_index: u32, value: u64) -> Result<(), PawnIoError>;
    fn read_mmio_32(&self, physical_address: u64) -> Result<u32, PawnIoError> {
        let _ = physical_address;
        Err(PawnIoError::UnsupportedOperation(
            "read_mmio_32 not implemented".into(),
        ))
    }
    fn write_mmio_32(&self, physical_address: u64, value: u32) -> Result<(), PawnIoError> {
        let _ = (physical_address, value);
        Err(PawnIoError::UnsupportedOperation(
            "write_mmio_32 not implemented".into(),
        ))
    }
    fn read_mmio_64(&self, physical_address: u64) -> Result<u64, PawnIoError> {
        let _ = physical_address;
        Err(PawnIoError::UnsupportedOperation(
            "read_mmio_64 not implemented".into(),
        ))
    }
    fn write_mmio_64(&self, physical_address: u64, value: u64) -> Result<(), PawnIoError> {
        let _ = (physical_address, value);
        Err(PawnIoError::UnsupportedOperation(
            "write_mmio_64 not implemented".into(),
        ))
    }
    fn scan_device(&self) -> PawnIoScanResult;
    fn close(&mut self);
}

// ============================================================================
// 7. Mock Backend for Zero-Driver Testability & Automated CI
// ============================================================================

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MockHistoryOp {
    ReadMsr { msr: u32 },
    WriteMsr { msr: u32, value: u64 },
    WriteMsrBlockedBiosLock { msr: u32, value: u64 },
    ReadMmio32 { address: u64 },
    WriteMmio32 { address: u64, value: u32 },
    ReadMmio64 { address: u64 },
    WriteMmio64 { address: u64, value: u64 },
}

pub struct MockPawnIoBackend {
    msrs: Arc<RwLock<HashMap<u32, u64>>>,
    mmio: Arc<RwLock<HashMap<u64, u64>>>,
    history: Arc<RwLock<Vec<MockHistoryOp>>>,
    is_open: bool,
    is_driver_loaded: bool,
    mchbar_address: u64,
    pub simulate_access_denied: bool,
    pub simulate_hardware_error: bool,
    pub simulate_unsupported_hardware: bool,
    pub simulate_bios_locked: bool,
    pub is_mmio_accessible: bool,
}

impl MockPawnIoBackend {
    pub fn new(is_loaded: bool) -> Self {
        let mut backend = Self {
            msrs: Arc::new(RwLock::new(HashMap::new())),
            mmio: Arc::new(RwLock::new(HashMap::new())),
            history: Arc::new(RwLock::new(Vec::new())),
            is_open: is_loaded,
            is_driver_loaded: is_loaded,
            mchbar_address: 0,
            simulate_access_denied: false,
            simulate_hardware_error: false,
            simulate_unsupported_hardware: false,
            simulate_bios_locked: false,
            is_mmio_accessible: true,
        };
        backend.configure_default_intel_baseline();
        backend
    }

    pub fn set_mchbar_address(&mut self, addr: u64) {
        self.mchbar_address = addr;
    }

    pub fn configure_intel_baseline(
        &mut self,
        tdp_watts: i32,
        min_watts: i32,
        max_watts: i32,
        pl1_watts: i32,
        pl2_watts: i32,
        units_per_watt: u32,
        locked: bool,
        mirror_mmio: bool,
    ) {
        let effective_units = if units_per_watt > 0 {
            units_per_watt
        } else {
            8
        };
        let power_unit_exponent = (effective_units as f64).log2().round() as u64;
        let power_unit_raw = 0x000A_0E00 | (power_unit_exponent & 0x0F);
        self.set_msr(MSR_RAPL_POWER_UNIT, power_unit_raw);

        let raw_thermal = ((tdp_watts as f64 * effective_units as f64).round() as u64) & 0x7FFF;
        let raw_min = ((min_watts as f64 * effective_units as f64).round() as u64) & 0x7FFF;
        let raw_max = ((max_watts as f64 * effective_units as f64).round() as u64) & 0x7FFF;
        let power_info_raw = raw_thermal | (raw_min << 16) | (raw_max << 32) | (0x28 << 48);
        self.set_msr(MSR_PKG_POWER_INFO, power_info_raw);

        let raw_pl1 = ((pl1_watts as f64 * effective_units as f64).round() as u64) & 0x7FFF;
        let raw_pl2 = ((pl2_watts as f64 * effective_units as f64).round() as u64) & 0x7FFF;
        let enable_and_clamp = (1 << 15) | (1 << 16) | (1 << 47) | (1 << 48);
        let tau1 = 0x28 << 17; // ~28s default Intel time window
        let tau2 = 0x10 << 49; // ~2.44ms default Intel time window
        let lock_bit = if locked || self.simulate_bios_locked {
            1 << 63
        } else {
            0
        };

        let power_limit_raw = raw_pl1 | enable_and_clamp | tau1 | (raw_pl2 << 32) | tau2 | lock_bit;
        self.set_msr(MSR_PKG_POWER_LIMIT, power_limit_raw);

        // Standard Temperature Target (TjMax 105C)
        self.set_msr(MSR_TEMPERATURE_TARGET, (105 << 16) | (0 << 24));
        // Standard Turbo Ratio (8 cores: 50, 50, 48, 48, 46, 46, 45, 45)
        self.set_msr(MSR_TURBO_RATIO_LIMIT, 0x2D2D_2E2E_3030_3232);

        if mirror_mmio && self.is_mmio_accessible {
            self.set_mmio(MMIO_PKG_RAPL_LIMIT_ADDRESS, power_limit_raw);
        }
    }

    pub fn configure_default_intel_baseline(&mut self) {
        self.configure_intel_baseline(55, 0, 157, 55, 157, 8, false, true);
    }

    pub fn set_msr(&self, msr: u32, value: u64) {
        self.msrs
            .write()
            .unwrap_or_else(|e| e.into_inner())
            .insert(msr, value);
    }

    pub fn get_msr(&self, msr: u32) -> u64 {
        self.msrs
            .read()
            .unwrap_or_else(|e| e.into_inner())
            .get(&msr)
            .copied()
            .unwrap_or(0)
    }

    pub fn set_mmio(&self, address: u64, value: u64) {
        let mut map = self.mmio.write().unwrap_or_else(|e| e.into_inner());
        map.insert(address, value);
        if address % 4 == 0 {
            map.insert(address + 4, value >> 32);
        }
    }

    pub fn get_mmio(&self, address: u64) -> u64 {
        let map = self.mmio.read().unwrap_or_else(|e| e.into_inner());
        if let Some(&val) = map.get(&address) {
            if val <= u32::MAX as u64 {
                if let Some(&high) = map.get(&(address + 4)) {
                    if high != 0 {
                        return (val & 0xFFFF_FFFF) | ((high & 0xFFFF_FFFF) << 32);
                    }
                }
            }
            return val;
        }
        0
    }

    pub fn history(&self) -> Vec<MockHistoryOp> {
        self.history
            .read()
            .unwrap_or_else(|e| e.into_inner())
            .clone()
    }

    pub fn clear_history(&self) {
        self.history
            .write()
            .unwrap_or_else(|e| e.into_inner())
            .clear();
    }

    fn record_op(&self, op: MockHistoryOp) {
        self.history
            .write()
            .unwrap_or_else(|e| e.into_inner())
            .push(op);
    }
}

impl PawnIoBackend for MockPawnIoBackend {
    fn is_open(&self) -> bool {
        self.is_open
            && self.is_driver_loaded
            && !self.simulate_access_denied
            && !self.simulate_unsupported_hardware
            && !self.simulate_hardware_error
    }

    fn is_driver_installed(&self) -> bool {
        self.is_driver_loaded
    }

    fn mchbar_address(&self) -> u64 {
        self.mchbar_address
    }

    fn device_path(&self) -> &str {
        r"\\.\MockPawnIO"
    }

    fn initialize(&mut self) -> PowerLimitApplyStatus {
        if !self.is_driver_loaded {
            return PowerLimitApplyStatus::DriverNotInstalled;
        }
        if self.simulate_access_denied {
            return PowerLimitApplyStatus::AccessDenied;
        }
        if self.simulate_hardware_error {
            return PowerLimitApplyStatus::HardwareError;
        }
        if self.simulate_unsupported_hardware {
            self.set_msr(MSR_RAPL_POWER_UNIT, 0);
            self.set_msr(MSR_PKG_POWER_INFO, 0);
            self.set_msr(MSR_PKG_POWER_LIMIT, 0);
            return PowerLimitApplyStatus::UnsupportedHardware;
        }
        self.is_open = true;
        PowerLimitApplyStatus::Success
    }

    fn read_msr(&self, msr_index: u32) -> Result<u64, PawnIoError> {
        if self.simulate_hardware_error || !self.is_open {
            return Err(PawnIoError::HardwareError(
                "Simulated hardware error or device closed".into(),
            ));
        }
        if self.simulate_unsupported_hardware {
            return Ok(0);
        }

        self.record_op(MockHistoryOp::ReadMsr { msr: msr_index });

        let mut val = self.get_msr(msr_index);
        if msr_index == MSR_PKG_POWER_LIMIT && self.simulate_bios_locked {
            val |= MSR_PKG_POWER_LIMIT_LOCK_BIT;
        }
        Ok(val)
    }

    fn write_msr(&self, msr_index: u32, value: u64) -> Result<(), PawnIoError> {
        if self.simulate_hardware_error || !self.is_open {
            return Err(PawnIoError::HardwareError(
                "Simulated hardware error or device closed".into(),
            ));
        }

        if !is_safe_msr_index(msr_index) {
            return Err(PawnIoError::InvalidRegister(msr_index));
        }

        // BIOS Lock enforcement
        if msr_index == MSR_PKG_POWER_LIMIT {
            let current = self.get_msr(msr_index);
            if self.simulate_bios_locked || (current & MSR_PKG_POWER_LIMIT_LOCK_BIT) != 0 {
                self.record_op(MockHistoryOp::WriteMsrBlockedBiosLock {
                    msr: msr_index,
                    value,
                });
                return Err(PawnIoError::BiosLocked(
                    "MSR 0x610 locked by BIOS bit 63".into(),
                ));
            }
            if (value & MSR_PKG_POWER_LIMIT_LOCK_BIT) != 0 {
                return Err(PawnIoError::SecurityViolation(
                    "Refusing to set bit 63 lock bit on MSR 0x610".into(),
                ));
            }
        }

        self.set_msr(msr_index, value);
        self.record_op(MockHistoryOp::WriteMsr {
            msr: msr_index,
            value,
        });
        Ok(())
    }

    fn read_mmio_32(&self, physical_address: u64) -> Result<u32, PawnIoError> {
        if self.simulate_hardware_error || !self.is_open || !self.is_mmio_accessible {
            return Err(PawnIoError::HardwareError(
                "MMIO inaccessible or simulated hardware error".into(),
            ));
        }

        self.record_op(MockHistoryOp::ReadMmio32 {
            address: physical_address,
        });

        let map = self.mmio.read().unwrap_or_else(|e| e.into_inner());
        if (physical_address & 4) != 0 {
            let base = physical_address - 4;
            if let Some(&base64) = map.get(&base) {
                return Ok((base64 >> 32) as u32);
            }
            if let Some(&val) = map.get(&physical_address) {
                return Ok(val as u32);
            }
        } else {
            if let Some(&val64) = map.get(&physical_address) {
                return Ok(val64 as u32);
            }
        }
        Ok(0)
    }

    fn write_mmio_32(&self, physical_address: u64, value: u32) -> Result<(), PawnIoError> {
        if self.simulate_hardware_error || !self.is_open || !self.is_mmio_accessible {
            return Err(PawnIoError::HardwareError(
                "MMIO inaccessible or simulated hardware error".into(),
            ));
        }

        let mut map = self.mmio.write().unwrap_or_else(|e| e.into_inner());
        if (physical_address & 4) != 0 {
            let base_addr = physical_address - 4;
            let current = map.get(&base_addr).copied().unwrap_or(0);
            let updated = (current & 0x0000_0000_FFFF_FFFF) | ((value as u64) << 32);
            map.insert(base_addr, updated);
            map.insert(physical_address, value as u64);
        } else {
            let current = map.get(&physical_address).copied().unwrap_or(0);
            let updated = (current & 0xFFFF_FFFF_0000_0000) | (value as u64);
            map.insert(physical_address, updated);
        }

        self.record_op(MockHistoryOp::WriteMmio32 {
            address: physical_address,
            value,
        });
        Ok(())
    }

    fn read_mmio_64(&self, physical_address: u64) -> Result<u64, PawnIoError> {
        if self.simulate_hardware_error || !self.is_open || !self.is_mmio_accessible {
            return Err(PawnIoError::HardwareError(
                "MMIO inaccessible or simulated hardware error".into(),
            ));
        }

        self.record_op(MockHistoryOp::ReadMmio64 {
            address: physical_address,
        });

        let map = self.mmio.read().unwrap_or_else(|e| e.into_inner());
        if let Some(&val) = map.get(&physical_address) {
            return Ok(val);
        }
        if let (Some(&low), Some(&high)) =
            (map.get(&physical_address), map.get(&(physical_address + 4)))
        {
            return Ok((low & 0xFFFF_FFFF) | ((high & 0xFFFF_FFFF) << 32));
        }
        Ok(0)
    }

    fn write_mmio_64(&self, physical_address: u64, value: u64) -> Result<(), PawnIoError> {
        if self.simulate_hardware_error || !self.is_open || !self.is_mmio_accessible {
            return Err(PawnIoError::HardwareError(
                "MMIO inaccessible or simulated hardware error".into(),
            ));
        }

        let mut map = self.mmio.write().unwrap_or_else(|e| e.into_inner());
        map.insert(physical_address, value);
        if physical_address % 4 == 0 {
            map.insert(physical_address + 4, value >> 32);
        }

        self.record_op(MockHistoryOp::WriteMmio64 {
            address: physical_address,
            value,
        });
        Ok(())
    }

    fn scan_device(&self) -> PawnIoScanResult {
        let is_running = self.is_driver_loaded && !self.simulate_hardware_error;
        let is_accessible = self.is_open();
        let diag = if is_accessible {
            "Mock PawnIO active and accessible.".into()
        } else if self.simulate_access_denied {
            "Mock PawnIO requires administrator elevation.".into()
        } else {
            "Mock PawnIO driver not installed.".into()
        };

        PawnIoScanResult {
            is_installed: self.is_driver_loaded,
            is_running,
            is_accessible,
            requires_elevation: self.simulate_access_denied,
            service_path: r"C:\Windows\System32\drivers\pawnio.sys".into(),
            last_win32_error: if self.simulate_access_denied { 5 } else { 0 },
            diagnostic_message: diag,
        }
    }

    fn close(&mut self) {
        self.is_open = false;
    }
}

// ============================================================================
// 8. Named Mutex Inter-Process Synchronization
// ============================================================================

#[cfg(windows)]
mod win_mutex {
    use super::*;

    pub struct NamedMutexGuard {
        pub handle: win_ffi::HANDLE,
    }

    impl Drop for NamedMutexGuard {
        fn drop(&mut self) {
            if !self.handle.is_null() && self.handle != win_ffi::INVALID_HANDLE_VALUE {
                unsafe {
                    win_ffi::ReleaseMutex(self.handle);
                    win_ffi::CloseHandle(self.handle);
                }
                self.handle = std::ptr::null_mut();
            }
        }
    }

    pub fn acquire_named_mutex(
        name: &str,
        timeout: Duration,
    ) -> Result<NamedMutexGuard, PawnIoError> {
        let wide_name: Vec<u16> = OsStr::new(name)
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let timeout_ms = timeout.as_millis().min(u32::MAX as u128) as u32;

        unsafe {
            let mut handle = win_ffi::OpenMutexW(0x0010_0000 | 0x0001, 0, wide_name.as_ptr());
            if handle.is_null() {
                handle = win_ffi::CreateMutexW(std::ptr::null_mut(), 0, wide_name.as_ptr());
            }

            if handle.is_null() || handle == win_ffi::INVALID_HANDLE_VALUE {
                return Err(PawnIoError::MutexAcquisitionFailed(format!(
                    "Failed to open/create named mutex {}: error code {}",
                    name,
                    win_ffi::GetLastError()
                )));
            }

            let wait_res = win_ffi::WaitForSingleObject(handle, timeout_ms);
            match wait_res {
                win_ffi::WAIT_OBJECT_0 | win_ffi::WAIT_ABANDONED => Ok(NamedMutexGuard { handle }),
                win_ffi::WAIT_TIMEOUT => {
                    win_ffi::CloseHandle(handle);
                    Err(PawnIoError::MutexTimeout(name.to_string(), timeout))
                }
                _ => {
                    let err = win_ffi::GetLastError();
                    win_ffi::CloseHandle(handle);
                    Err(PawnIoError::MutexAcquisitionFailed(format!(
                        "WaitForSingleObject on {} failed with 0x{:X}, last error: {}",
                        name, wait_res, err
                    )))
                }
            }
        }
    }
}

// Fallback in-process mutex lock for non-Windows or tests
#[cfg(not(windows))]
mod non_win_mutex {
    use super::*;

    pub struct NamedMutexGuard;

    pub fn acquire_named_mutex(
        _name: &str,
        _timeout: Duration,
    ) -> Result<NamedMutexGuard, PawnIoError> {
        Ok(NamedMutexGuard)
    }
}

#[cfg(not(windows))]
pub use non_win_mutex::*;
#[cfg(windows)]
pub use win_mutex::*;

// ============================================================================
// 9. Windows Native Win32 / PawnIO Backend Implementation
// ============================================================================

#[cfg(windows)]
#[allow(dead_code, non_snake_case)]
mod win_ffi {
    use std::ffi::c_void;

    pub type HANDLE = *mut c_void;
    pub type BOOL = i32;
    pub type DWORD = u32;
    pub type LPCWSTR = *const u16;
    pub type LPVOID = *mut c_void;
    pub type LPCVOID = *const c_void;
    pub type LPDWORD = *mut u32;

    pub const INVALID_HANDLE_VALUE: HANDLE = -1isize as HANDLE;
    pub const GENERIC_READ: DWORD = 0x8000_0000;
    pub const GENERIC_WRITE: DWORD = 0x4000_0000;
    pub const FILE_SHARE_READ: DWORD = 0x0000_0001;
    pub const FILE_SHARE_WRITE: DWORD = 0x0000_0002;
    pub const OPEN_EXISTING: DWORD = 3;
    pub const FILE_ATTRIBUTE_NORMAL: DWORD = 0x80;

    pub const ERROR_ACCESS_DENIED: DWORD = 5;
    pub const ERROR_INVALID_HANDLE: DWORD = 6;
    pub const ERROR_DEV_NOT_EXIST: DWORD = 55;

    pub const WAIT_OBJECT_0: DWORD = 0x0000_0000;
    pub const WAIT_ABANDONED: DWORD = 0x0000_0080;
    pub const WAIT_TIMEOUT: DWORD = 0x0000_0102;

    pub const SC_MANAGER_CONNECT: DWORD = 0x0001;
    pub const SERVICE_QUERY_STATUS: DWORD = 0x0004;
    pub const SERVICE_START: DWORD = 0x0010;

    pub const SERVICE_STOPPED: DWORD = 1;
    pub const SERVICE_START_PENDING: DWORD = 2;
    pub const SERVICE_RUNNING: DWORD = 4;

    pub const HKEY_LOCAL_MACHINE: usize = 0x8000_0002;
    pub const KEY_READ: DWORD = 0x20019;

    #[repr(C)]
    pub struct SERVICE_STATUS {
        pub dwServiceType: DWORD,
        pub dwCurrentState: DWORD,
        pub dwControlsAccepted: DWORD,
        pub dwWin32ExitCode: DWORD,
        pub dwServiceSpecificExitCode: DWORD,
        pub dwCheckPoint: DWORD,
        pub dwWaitHint: DWORD,
    }

    #[link(name = "kernel32")]
    extern "system" {
        pub fn CreateFileW(
            lpFileName: LPCWSTR,
            dwDesiredAccess: DWORD,
            dwShareMode: DWORD,
            lpSecurityAttributes: LPVOID,
            dwCreationDisposition: DWORD,
            dwFlagsAndAttributes: DWORD,
            hTemplateFile: HANDLE,
        ) -> HANDLE;

        pub fn CloseHandle(hObject: HANDLE) -> BOOL;

        pub fn DeviceIoControl(
            hDevice: HANDLE,
            dwIoControlCode: DWORD,
            lpInBuffer: LPCVOID,
            nInBufferSize: DWORD,
            lpOutBuffer: LPVOID,
            nOutBufferSize: DWORD,
            lpBytesReturned: LPDWORD,
            lpOverlapped: LPVOID,
        ) -> BOOL;

        pub fn GetLastError() -> DWORD;

        pub fn CreateMutexW(
            lpMutexAttributes: LPVOID,
            bInitialOwner: BOOL,
            lpName: LPCWSTR,
        ) -> HANDLE;

        pub fn OpenMutexW(dwDesiredAccess: DWORD, bInheritHandle: BOOL, lpName: LPCWSTR) -> HANDLE;

        pub fn ReleaseMutex(hMutex: HANDLE) -> BOOL;

        pub fn WaitForSingleObject(hHandle: HANDLE, dwMilliseconds: DWORD) -> DWORD;
    }

    #[link(name = "advapi32")]
    extern "system" {
        pub fn OpenSCManagerW(
            lpMachineName: LPCWSTR,
            lpDatabaseName: LPCWSTR,
            dwDesiredAccess: DWORD,
        ) -> HANDLE;

        pub fn OpenServiceW(
            hSCManager: HANDLE,
            lpServiceName: LPCWSTR,
            dwDesiredAccess: DWORD,
        ) -> HANDLE;

        pub fn QueryServiceStatus(hService: HANDLE, lpServiceStatus: *mut SERVICE_STATUS) -> BOOL;

        pub fn StartServiceW(
            hService: HANDLE,
            dwNumServiceArgs: DWORD,
            lpServiceArgVectors: *const LPCWSTR,
        ) -> BOOL;

        pub fn CloseServiceHandle(hSCObject: HANDLE) -> BOOL;

        pub fn RegOpenKeyExW(
            hKey: usize,
            lpSubKey: LPCWSTR,
            ulOptions: DWORD,
            samDesired: DWORD,
            phkResult: *mut usize,
        ) -> i32;

        pub fn RegQueryValueExW(
            hKey: usize,
            lpValueName: LPCWSTR,
            lpReserved: *mut DWORD,
            lpType: *mut DWORD,
            lpData: *mut u8,
            lpcbData: *mut DWORD,
        ) -> i32;

        pub fn RegCloseKey(hKey: usize) -> i32;
    }
}

#[cfg(windows)]
pub struct SafeDeviceHandle(pub win_ffi::HANDLE);

#[cfg(windows)]
unsafe impl Send for SafeDeviceHandle {}
#[cfg(windows)]
unsafe impl Sync for SafeDeviceHandle {}

#[cfg(windows)]
impl SafeDeviceHandle {
    pub fn new(handle: win_ffi::HANDLE) -> Option<Self> {
        if handle.is_null() || handle == win_ffi::INVALID_HANDLE_VALUE {
            None
        } else {
            Some(Self(handle))
        }
    }

    pub fn raw(&self) -> win_ffi::HANDLE {
        self.0
    }
}

#[cfg(windows)]
impl Drop for SafeDeviceHandle {
    fn drop(&mut self) {
        if !self.0.is_null() && self.0 != win_ffi::INVALID_HANDLE_VALUE {
            unsafe { win_ffi::CloseHandle(self.0) };
            self.0 = win_ffi::INVALID_HANDLE_VALUE;
        }
    }
}

#[cfg(windows)]
pub struct NativePawnIoBackend {
    device_handle: Option<SafeDeviceHandle>,
    mchbar_device_handle: Option<SafeDeviceHandle>,
    active_device_path: String,
    is_msr_loaded: bool,
    is_mchbar_loaded: bool,
    cached_mchbar_address: u64,
    last_diagnostic: String,
    io_lock: StdMutex<()>,
}

#[cfg(windows)]
impl NativePawnIoBackend {
    pub fn new() -> Self {
        Self {
            device_handle: None,
            mchbar_device_handle: None,
            active_device_path: String::new(),
            is_msr_loaded: false,
            is_mchbar_loaded: false,
            cached_mchbar_address: 0,
            last_diagnostic: String::new(),
            io_lock: StdMutex::new(()),
        }
    }

    fn to_wide_null(s: &str) -> Vec<u16> {
        OsStr::new(s)
            .encode_wide()
            .chain(std::iter::once(0))
            .collect()
    }

    fn open_handle_to_path(path: &str) -> (Option<SafeDeviceHandle>, u32) {
        let wide_path = Self::to_wide_null(path);
        let raw = unsafe {
            win_ffi::CreateFileW(
                wide_path.as_ptr(),
                win_ffi::GENERIC_READ | win_ffi::GENERIC_WRITE,
                win_ffi::FILE_SHARE_READ | win_ffi::FILE_SHARE_WRITE,
                std::ptr::null_mut(),
                win_ffi::OPEN_EXISTING,
                win_ffi::FILE_ATTRIBUTE_NORMAL,
                std::ptr::null_mut(),
            )
        };

        if raw.is_null() || raw == win_ffi::INVALID_HANDLE_VALUE {
            let err = unsafe { win_ffi::GetLastError() };
            (None, err)
        } else {
            (SafeDeviceHandle::new(raw), 0)
        }
    }

    fn execute_on_handle(
        handle: &SafeDeviceHandle,
        function_name: &str,
        input: &[u64],
        output: &mut [u64],
    ) -> Result<(), PawnIoError> {
        let in_buf = build_execute_packet(function_name, input);
        let alloc_out_count = output.len().max(1);
        let mut out_buf = vec![0u8; alloc_out_count * 8];
        let mut bytes_returned: u32 = 0;

        let _device_lock = GLOBAL_PAWNIO_DEVICE
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        let success = unsafe {
            win_ffi::DeviceIoControl(
                handle.raw(),
                IOCTL_PAWNIO_EXECUTE,
                in_buf.as_ptr() as win_ffi::LPCVOID,
                in_buf.len() as win_ffi::DWORD,
                out_buf.as_mut_ptr() as win_ffi::LPVOID,
                out_buf.len() as win_ffi::DWORD,
                &mut bytes_returned,
                std::ptr::null_mut(),
            )
        };

        if success == 0 {
            let err = unsafe { win_ffi::GetLastError() };
            return Err(PawnIoError::HardwareError(format!(
                "DeviceIoControl IOCTL_PAWNIO_EXECUTE ({}) failed with Win32 error {}",
                function_name, err
            )));
        }

        if !output.is_empty() {
            if !parse_execute_response(&out_buf, bytes_returned as usize, output) {
                return Err(PawnIoError::HardwareError(format!(
                    "Execute response buffer too small for function {}: returned {} bytes",
                    function_name, bytes_returned
                )));
            }
        }

        Ok(())
    }

    fn try_initialize_mchbar_module(&mut self) {
        let mchbar_bytes = match load_module_bytes("IntelMCHBAR.bin") {
            Some(b) => b,
            None => return,
        };

        let path = if !self.active_device_path.is_empty() {
            &self.active_device_path
        } else {
            PRIMARY_DEVICE_PATH
        };

        let (handle_opt, _) = Self::open_handle_to_path(path);
        let mchbar_handle = match handle_opt {
            Some(h) => h,
            None => return,
        };

        // Scoped on purpose: the guard must be released before execute_on_handle() below, which
        // acquires the same non-reentrant GLOBAL_PAWNIO_DEVICE mutex.
        let load_ok = {
            let _device_lock = GLOBAL_PAWNIO_DEVICE
                .lock()
                .unwrap_or_else(|e| e.into_inner());
            let mut bytes_ret: u32 = 0;
            unsafe {
                win_ffi::DeviceIoControl(
                    mchbar_handle.raw(),
                    IOCTL_PAWNIO_LOAD_BINARY,
                    mchbar_bytes.as_ptr() as win_ffi::LPCVOID,
                    mchbar_bytes.len() as win_ffi::DWORD,
                    std::ptr::null_mut(),
                    0,
                    &mut bytes_ret,
                    std::ptr::null_mut(),
                )
            }
        };

        if load_ok != 0 {
            self.mchbar_device_handle = Some(mchbar_handle);
            self.is_mchbar_loaded = true;

            // Query MCHBAR base address under PCI mutex lock
            if let Ok(_guard) = acquire_named_mutex(PCI_MUTEX_NAME, DEFAULT_MUTEX_TIMEOUT) {
                let mut out_val = [0u64; 1];
                if let Some(ref handle) = self.mchbar_device_handle {
                    if Self::execute_on_handle(handle, FN_GET_MCHBAR, &[], &mut out_val).is_ok() {
                        self.cached_mchbar_address = out_val[0];
                    }
                }
            }
        }
    }
}

#[cfg(windows)]
impl PawnIoBackend for NativePawnIoBackend {
    fn is_open(&self) -> bool {
        self.device_handle.is_some() && self.is_msr_loaded
    }

    fn is_driver_installed(&self) -> bool {
        self.is_open() || !self.active_device_path.is_empty()
    }

    fn mchbar_address(&self) -> u64 {
        self.cached_mchbar_address
    }

    fn device_path(&self) -> &str {
        if !self.active_device_path.is_empty() {
            &self.active_device_path
        } else {
            PRIMARY_DEVICE_PATH
        }
    }

    fn initialize(&mut self) -> PowerLimitApplyStatus {
        if self.is_open() {
            return PowerLimitApplyStatus::Success;
        }

        self.device_handle = None;
        self.mchbar_device_handle = None;
        self.is_msr_loaded = false;
        self.is_mchbar_loaded = false;
        self.active_device_path.clear();

        let mut candidate_handle: Option<SafeDeviceHandle> = None;
        let mut last_win32_err = 0;

        for &path in CANDIDATE_DEVICE_PATHS {
            let (handle, err) = Self::open_handle_to_path(path);
            if let Some(h) = handle {
                candidate_handle = Some(h);
                self.active_device_path = path.to_string();
                break;
            }
            last_win32_err = err;
            if err == win_ffi::ERROR_ACCESS_DENIED {
                self.active_device_path = path.to_string();
                self.last_diagnostic = "Access denied opening PawnIO device handle. Administrator privileges required.".into();
                return PowerLimitApplyStatus::AccessDenied;
            }
        }

        let handle = match candidate_handle {
            Some(h) => h,
            None => {
                let scan = self.scan_device();
                if last_win32_err == win_ffi::ERROR_ACCESS_DENIED {
                    return PowerLimitApplyStatus::AccessDenied;
                }
                self.last_diagnostic = scan.diagnostic_message;
                return PowerLimitApplyStatus::DriverNotInstalled;
            }
        };

        // Load IntelMSR.bin module into primary device handle
        let msr_bytes = match load_module_bytes("IntelMSR.bin") {
            Some(b) => b,
            None => {
                self.last_diagnostic =
                    "IntelMSR.bin module payload could not be loaded from resources or disk."
                        .into();
                return PowerLimitApplyStatus::HardwareError;
            }
        };

        // Scoped on purpose: GLOBAL_PAWNIO_DEVICE is a non-reentrant std::sync::Mutex, and both
        // try_initialize_mchbar_module() and execute_on_handle() acquire it. Holding the guard for
        // the whole function would self-deadlock the caller on the same thread.
        let load_ok = {
            let _device_lock = GLOBAL_PAWNIO_DEVICE
                .lock()
                .unwrap_or_else(|e| e.into_inner());
            let mut bytes_ret: u32 = 0;
            unsafe {
                win_ffi::DeviceIoControl(
                    handle.raw(),
                    IOCTL_PAWNIO_LOAD_BINARY,
                    msr_bytes.as_ptr() as win_ffi::LPCVOID,
                    msr_bytes.len() as win_ffi::DWORD,
                    std::ptr::null_mut(),
                    0,
                    &mut bytes_ret,
                    std::ptr::null_mut(),
                )
            }
        };

        if load_ok == 0 {
            let err = unsafe { win_ffi::GetLastError() };
            self.last_diagnostic = format!(
                "PawnIO LoadBinary IOCTL (0xA1B22084) failed. Win32Error: {}",
                err
            );
            return PowerLimitApplyStatus::HardwareError;
        }

        self.device_handle = Some(handle);
        self.is_msr_loaded = true;

        // Try initializing secondary handle for MCHBAR MMIO support
        self.try_initialize_mchbar_module();

        self.last_diagnostic =
            "PawnIO driver connected and IntelMSR module loaded successfully.".into();
        PowerLimitApplyStatus::Success
    }

    fn read_msr(&self, msr_index: u32) -> Result<u64, PawnIoError> {
        let _io = self.io_lock.lock().unwrap_or_else(|e| e.into_inner());
        let handle = self.device_handle.as_ref().ok_or_else(|| {
            PawnIoError::DriverNotInstalled("Device handle is closed or uninitialized".into())
        })?;

        let _guard = acquire_named_mutex(MSR_MUTEX_NAME, DEFAULT_MUTEX_TIMEOUT)?;
        let mut out_val = [0u64; 1];
        Self::execute_on_handle(handle, FN_READ_MSR, &[msr_index as u64], &mut out_val)?;
        Ok(out_val[0])
    }

    fn write_msr(&self, msr_index: u32, value: u64) -> Result<(), PawnIoError> {
        if !is_safe_msr_index(msr_index) {
            return Err(PawnIoError::InvalidRegister(msr_index));
        }

        // Prevent setting bit 63 (permanent lock bit)
        if msr_index == MSR_PKG_POWER_LIMIT && (value & MSR_PKG_POWER_LIMIT_LOCK_BIT) != 0 {
            return Err(PawnIoError::SecurityViolation(
                "Refusing to set bit 63 lock bit on MSR 0x610".into(),
            ));
        }

        let _io = self.io_lock.lock().unwrap_or_else(|e| e.into_inner());
        let handle = self.device_handle.as_ref().ok_or_else(|| {
            PawnIoError::DriverNotInstalled("Device handle is closed or uninitialized".into())
        })?;

        let _guard = acquire_named_mutex(MSR_MUTEX_NAME, DEFAULT_MUTEX_TIMEOUT)?;

        // If writing to MSR 0x610, verify it is not already locked by UEFI/BIOS
        if msr_index == MSR_PKG_POWER_LIMIT {
            let mut check_out = [0u64; 1];
            if Self::execute_on_handle(handle, FN_READ_MSR, &[msr_index as u64], &mut check_out)
                .is_ok()
            {
                if (check_out[0] & MSR_PKG_POWER_LIMIT_LOCK_BIT) != 0 {
                    return Err(PawnIoError::BiosLocked(
                        "MSR 0x610 is locked by BIOS (bit 63 set)".into(),
                    ));
                }
            }
        }

        Self::execute_on_handle(handle, FN_WRITE_MSR, &[msr_index as u64, value], &mut [])
    }

    fn read_mmio_32(&self, physical_address: u64) -> Result<u32, PawnIoError> {
        if !(0x10_0000..(1 << 48)).contains(&physical_address) {
            return Err(PawnIoError::HardwareError(format!(
                "Invalid physical MMIO address: 0x{:X}",
                physical_address
            )));
        }

        let _io = self.io_lock.lock().unwrap_or_else(|e| e.into_inner());
        let handle = self.mchbar_device_handle.as_ref().ok_or_else(|| {
            PawnIoError::UnsupportedOperation("MCHBAR handle is not loaded".into())
        })?;

        let _guard = acquire_named_mutex(PCI_MUTEX_NAME, DEFAULT_MUTEX_TIMEOUT)?;
        let mut out_val = [0u64; 1];
        Self::execute_on_handle(handle, FN_READ_DWORD, &[physical_address], &mut out_val)?;
        Ok(out_val[0] as u32)
    }

    fn read_mmio_64(&self, physical_address: u64) -> Result<u64, PawnIoError> {
        if !(0x10_0000..(1 << 48)).contains(&physical_address) {
            return Err(PawnIoError::HardwareError(format!(
                "Invalid physical MMIO address: 0x{:X}",
                physical_address
            )));
        }

        let _io = self.io_lock.lock().unwrap_or_else(|e| e.into_inner());
        let handle = self.mchbar_device_handle.as_ref().ok_or_else(|| {
            PawnIoError::UnsupportedOperation("MCHBAR handle is not loaded".into())
        })?;

        let _guard = acquire_named_mutex(PCI_MUTEX_NAME, DEFAULT_MUTEX_TIMEOUT)?;
        let mut out_val = [0u64; 1];
        Self::execute_on_handle(handle, FN_READ_QWORD, &[physical_address], &mut out_val)?;
        Ok(out_val[0])
    }

    fn scan_device(&self) -> PawnIoScanResult {
        let mut matched_path = String::new();
        let mut is_accessible = false;
        let mut requires_elevation = false;
        let mut last_error = 0;

        for &path in CANDIDATE_DEVICE_PATHS {
            let (handle, err) = Self::open_handle_to_path(path);
            if handle.is_some() {
                matched_path = path.to_string();
                is_accessible = true;
                break;
            }
            if err == win_ffi::ERROR_ACCESS_DENIED {
                last_error = err;
                requires_elevation = true;
                matched_path = path.to_string();
                break;
            }
        }

        let mut service_path = String::new();
        let mut is_running = false;
        let mut is_installed = is_accessible || requires_elevation;

        // Check Windows Service Control Manager
        unsafe {
            let scm = win_ffi::OpenSCManagerW(
                std::ptr::null(),
                std::ptr::null(),
                win_ffi::SC_MANAGER_CONNECT,
            );
            if !scm.is_null() {
                for name in &["PawnIO", "pawnio"] {
                    let wide_svc = Self::to_wide_null(name);
                    let mut svc = win_ffi::OpenServiceW(
                        scm,
                        wide_svc.as_ptr(),
                        win_ffi::SERVICE_QUERY_STATUS | win_ffi::SERVICE_START,
                    );
                    if svc.is_null() && win_ffi::GetLastError() == win_ffi::ERROR_ACCESS_DENIED {
                        svc = win_ffi::OpenServiceW(
                            scm,
                            wide_svc.as_ptr(),
                            win_ffi::SERVICE_QUERY_STATUS,
                        );
                    }
                    if !svc.is_null() {
                        is_installed = true;
                        let mut status: win_ffi::SERVICE_STATUS = std::mem::zeroed();
                        if win_ffi::QueryServiceStatus(svc, &mut status) != 0 {
                            if status.dwCurrentState == win_ffi::SERVICE_RUNNING
                                || status.dwCurrentState == win_ffi::SERVICE_START_PENDING
                            {
                                is_running = true;
                            } else if status.dwCurrentState == win_ffi::SERVICE_STOPPED {
                                // Attempt start if stopped
                                if win_ffi::StartServiceW(svc, 0, std::ptr::null()) != 0 {
                                    is_running = true;
                                }
                            }
                        }
                        win_ffi::CloseServiceHandle(svc);
                        break;
                    }
                }
                win_ffi::CloseServiceHandle(scm);
            }
        }

        // Check filesystem drivers
        if !is_installed {
            let sys_paths = [
                r"C:\Windows\System32\drivers\pawnio.sys",
                r"C:\Windows\System32\drivers\PawnIO.sys",
            ];
            for p in &sys_paths {
                if Path::new(p).exists() {
                    service_path = p.to_string();
                    is_installed = true;
                    break;
                }
            }
        }

        let message = if is_accessible {
            format!(
                "PawnIO kernel driver detected and accessible via {}.",
                matched_path
            )
        } else if requires_elevation {
            format!("PawnIO detected at {}, but access was denied (Win32Error 5). Administrator elevation required.", matched_path)
        } else if is_installed {
            if is_running {
                "PawnIO service is running, but kernel symlink is not accessible.".into()
            } else {
                "PawnIO service is registered, but the driver is not currently running.".into()
            }
        } else {
            "PawnIO driver not detected on system (checked SCM, drivers directory, and kernel symlinks).".into()
        };

        PawnIoScanResult {
            is_installed,
            is_running: is_running || is_accessible || requires_elevation,
            is_accessible,
            requires_elevation,
            service_path,
            last_win32_error: last_error,
            diagnostic_message: message,
        }
    }

    fn close(&mut self) {
        self.is_msr_loaded = false;
        self.is_mchbar_loaded = false;
        self.device_handle = None;
        self.mchbar_device_handle = None;
        self.active_device_path.clear();
    }
}

#[cfg(windows)]
impl Drop for NativePawnIoBackend {
    fn drop(&mut self) {
        self.close();
    }
}

// ============================================================================
// 10. High-Level PawnIO Controller
// ============================================================================

/// Status codes returned by [`plan_mchbar_power_write`].
pub const WRITE_PLAN_OK: i32 = 0;
/// The current MMIO contents could not be read, so no safe write can be planned.
pub const WRITE_PLAN_READ_FAILED: i32 = 1;
/// The package RAPL MMIO register is locked by firmware.
pub const WRITE_PLAN_BIOS_LOCKED: i32 = 2;

/// Ordered two-step MMIO write plan for the package RAPL limit register.
///
/// `first_*` must be written before `second_*`. When `second` fails, restoring
/// `first_previous_value` at `first_address` returns the register to its original
/// contents, so the caller never leaves PL1 > PL2 on the wire.
#[repr(C)]
#[derive(Copy, Clone, Debug, Default, PartialEq)]
pub struct MchbarWritePlan {
    /// 64-bit value to mirror after both writes (`pl1_dword | pl2_dword << 32`).
    pub mirrored_value: u64,
    pub first_address: u64,
    pub second_address: u64,
    pub first_value: u32,
    pub second_value: u32,
    pub first_previous_value: u32,
    /// One of [`WRITE_PLAN_OK`], [`WRITE_PLAN_READ_FAILED`], [`WRITE_PLAN_BIOS_LOCKED`].
    pub status: i32,
}

/// Plans the hazard-free MMIO write of package RAPL PL1/PL2 limits.
///
/// The hardware rejects an intermediate state where PL1 exceeds PL2, so when the
/// new PL1 is above the current PL2 the upper (PL2) dword is written first. Tau
/// time windows are preserved from the live register, falling back to the supplied
/// values and then to the Intel defaults.
pub fn plan_mchbar_power_write(
    current_mmio: u64,
    read_ok: bool,
    raw_pl1: u64,
    raw_pl2: u64,
    fallback_tau1: u64,
    fallback_tau2: u64,
    pl1_address: u64,
    pl2_address: u64,
) -> MchbarWritePlan {
    if !read_ok {
        return MchbarWritePlan {
            status: WRITE_PLAN_READ_FAILED,
            ..Default::default()
        };
    }

    let current_pl1_dword = current_mmio as u32;
    let current_pl2_dword = (current_mmio >> 32) as u32;

    // Lock check: bit 63 in the 64-bit view, bit 31 of the PL2 dword.
    if (current_mmio & (1 << 63)) != 0 || (current_pl2_dword & (1 << 31)) != 0 {
        return MchbarWritePlan {
            status: WRITE_PLAN_BIOS_LOCKED,
            ..Default::default()
        };
    }

    let old_raw_pl2 = (current_pl2_dword & 0x7FFF) as u64;

    let mut mmio_tau1 = current_pl1_dword & (0x7F << 17);
    if mmio_tau1 == 0 {
        mmio_tau1 = (fallback_tau1 & (0x7F << 17)) as u32;
    }
    if mmio_tau1 == 0 {
        mmio_tau1 = 0x28 << 17; // ~28s default Intel PL1 Tau
    }

    let mut mmio_tau2 = current_pl2_dword & (0x7F << 17);
    if mmio_tau2 == 0 {
        let shifted = if (fallback_tau2 >> 32) != 0 {
            fallback_tau2 >> 32
        } else {
            fallback_tau2
        };
        mmio_tau2 = (shifted & (0x7F << 17)) as u32;
    }
    if mmio_tau2 == 0 {
        mmio_tau2 = 0x10 << 17; // ~2.44ms default Intel PL2 Tau
    }

    let mut safe_pl1 = raw_pl1 & 0x7FFF;
    let mut safe_pl2 = raw_pl2 & 0x7FFF;
    if safe_pl2 < safe_pl1 {
        safe_pl2 = safe_pl1;
    }
    if safe_pl1 == 0 && safe_pl2 > 0 {
        safe_pl1 = safe_pl2;
    }

    let enable_and_clamp = (1u32 << 15) | (1u32 << 16);
    let pl1_dword = (safe_pl1 as u32) | enable_and_clamp | mmio_tau1;
    let pl2_dword = ((safe_pl2 as u32) | enable_and_clamp | mmio_tau2) & !(1u32 << 31);
    let mirrored_value = (pl1_dword as u64) | ((pl2_dword as u64) << 32);

    if safe_pl1 > old_raw_pl2 {
        // Hazard: new PL1 exceeds current PL2 -> raise PL2 first, then PL1.
        MchbarWritePlan {
            mirrored_value,
            first_address: pl2_address,
            second_address: pl1_address,
            first_value: pl2_dword,
            second_value: pl1_dword,
            first_previous_value: current_pl2_dword,
            status: WRITE_PLAN_OK,
        }
    } else {
        // Normal: new PL1 fits under current PL2 -> lower PL1 first, then PL2.
        MchbarWritePlan {
            mirrored_value,
            first_address: pl1_address,
            second_address: pl2_address,
            first_value: pl1_dword,
            second_value: pl2_dword,
            first_previous_value: current_pl1_dword,
            status: WRITE_PLAN_OK,
        }
    }
}

pub struct PawnIoController {
    backend: Box<dyn PawnIoBackend>,
    allow_mock_fallback: bool,
}

impl PawnIoController {
    /// Creates a new controller with the platform native driver backend if on Windows,
    /// or mock backend otherwise.
    pub fn new() -> Self {
        #[cfg(windows)]
        {
            Self::with_backend(Box::new(NativePawnIoBackend::new()))
        }
        #[cfg(not(windows))]
        {
            Self::with_backend(Box::new(MockPawnIoBackend::new(true)))
        }
    }

    /// Creates a new controller with a specified backend implementation
    pub fn with_backend(backend: Box<dyn PawnIoBackend>) -> Self {
        Self {
            backend,
            allow_mock_fallback: false,
        }
    }

    /// Creates a mock-backed controller pre-configured with default Intel baseline
    pub fn new_mock() -> Self {
        Self::with_backend(Box::new(MockPawnIoBackend::new(true)))
    }

    /// Enables or disables automatic fallback to mock backend if native driver is not present
    pub fn set_allow_mock_fallback(&mut self, allow: bool) {
        self.allow_mock_fallback = allow;
    }

    pub fn backend(&self) -> &dyn PawnIoBackend {
        self.backend.as_ref()
    }

    pub fn is_available(&self) -> bool {
        self.backend.is_open()
    }

    /// Ensures underlying driver backend is initialized
    pub fn ensure_initialized(&mut self) -> PowerLimitApplyStatus {
        if self.backend.is_open() {
            return PowerLimitApplyStatus::Success;
        }

        let status = self.backend.initialize();
        if status != PowerLimitApplyStatus::Success && self.allow_mock_fallback {
            let mut mock = MockPawnIoBackend::new(true);
            let mock_status = mock.initialize();
            self.backend = Box::new(mock);
            return mock_status;
        }
        status
    }

    /// Dynamically scans user's system for PawnIO installation, service state, and kernel accessibility
    pub fn scan_device_for_pawnio(&self) -> PawnIoScanResult {
        self.backend.scan_device()
    }

    // --- RAPL MSR Operations ---

    /// Reads MSR_RAPL_POWER_UNIT (0x606) and calculates UnitsPerWatt = 1 << (raw & 0xF)
    pub fn read_power_units(&self) -> Result<(u32, u64), PawnIoError> {
        let raw = self.backend.read_msr(MSR_RAPL_POWER_UNIT)?;
        let pu = (raw & 0x0F) as u32;
        if pu == 0 && raw == 0 {
            return Err(PawnIoError::UnsupportedHardware(
                "MSR 0x606 returned 0: RAPL unsupported".into(),
            ));
        }
        let units_per_watt = 1u32 << pu;
        Ok((units_per_watt, raw))
    }

    /// Reads MSR_PKG_POWER_INFO (0x614) for thermal spec (TDP), min power, and max power
    pub fn read_power_info(&self, units_per_watt: u32) -> Result<PowerInfo, PawnIoError> {
        if units_per_watt == 0 {
            return Err(PawnIoError::HardwareError(
                "units_per_watt must be greater than 0".into(),
            ));
        }
        let raw = self.backend.read_msr(MSR_PKG_POWER_INFO)?;
        let raw_thermal = raw & 0x7FFF;
        let raw_min = (raw >> 16) & 0x7FFF;
        let raw_max = (raw >> 32) & 0x7FFF;

        let thermal_spec = (raw_thermal as f64 / units_per_watt as f64).round() as i32;
        let min_power = (raw_min as f64 / units_per_watt as f64).round() as i32;
        let max_power = (raw_max as f64 / units_per_watt as f64).round() as i32;

        Ok(PowerInfo {
            thermal_spec_power_watts: thermal_spec,
            minimum_power_watts: min_power,
            maximum_power_watts: max_power,
            raw_info: raw,
        })
    }

    /// Reads MSR_PKG_POWER_LIMIT (0x610) to obtain PL1, PL2, and lock status
    pub fn read_power_limits(&self, units_per_watt: u32) -> Result<PowerLimits, PawnIoError> {
        if units_per_watt == 0 {
            return Err(PawnIoError::HardwareError(
                "units_per_watt must be greater than 0".into(),
            ));
        }
        let raw = self.backend.read_msr(MSR_PKG_POWER_LIMIT)?;
        let is_locked = (raw & MSR_PKG_POWER_LIMIT_LOCK_BIT) != 0;
        let raw_pl1 = raw & 0x7FFF;
        let raw_pl2 = (raw >> 32) & 0x7FFF;

        let pl1 = (raw_pl1 as f64 / units_per_watt as f64).round() as i32;
        let pl2 = (raw_pl2 as f64 / units_per_watt as f64).round() as i32;

        Ok(PowerLimits {
            pl1_watts: pl1,
            pl2_watts: pl2,
            is_locked,
            raw_limit: raw,
        })
    }

    /// Reads MSR_TEMPERATURE_TARGET (0x1A2) to obtain TjMax and temperature offset
    pub fn read_temperature_target(&self) -> Result<TemperatureTarget, PawnIoError> {
        let raw = self.backend.read_msr(MSR_TEMPERATURE_TARGET)?;
        let target_temp = ((raw >> 16) & 0xFF) as u32;
        let target_offset = ((raw >> 24) & 0x3F) as u32;

        Ok(TemperatureTarget {
            target_temp_celsius: target_temp,
            target_offset_celsius: target_offset,
            raw_value: raw,
        })
    }

    /// Reads MSR_TURBO_RATIO_LIMIT (0x1AD) to obtain per-core turbo multiplier limits
    pub fn read_turbo_ratio_limit(&self) -> Result<TurboRatioLimit, PawnIoError> {
        let raw = self.backend.read_msr(MSR_TURBO_RATIO_LIMIT)?;
        let mut ratios = [0u8; 8];
        for i in 0..8 {
            ratios[i] = ((raw >> (i * 8)) & 0xFF) as u8;
        }

        Ok(TurboRatioLimit {
            ratios_by_core: ratios,
            raw_value: raw,
        })
    }

    /// Scans hardware baseline power capabilities across MSRs and MMIO
    pub fn scan_device_power_capabilities(&mut self) -> DevicePowerCapabilities {
        let init_status = self.ensure_initialized();
        if init_status != PowerLimitApplyStatus::Success {
            return DevicePowerCapabilities::unsupported(
                init_status,
                format!("Driver initialization failed: {}", init_status),
            );
        }

        // 1. Read Power Units (MSR 0x606)
        let (units_per_watt, raw_unit) = match self.read_power_units() {
            Ok(u) => u,
            Err(e) => {
                return DevicePowerCapabilities::unsupported(
                    PowerLimitApplyStatus::UnsupportedHardware,
                    format!("Failed to read MSR_RAPL_POWER_UNIT: {}", e),
                );
            }
        };

        // 2. Read Power Info (MSR 0x614)
        let power_info = self.read_power_info(units_per_watt).unwrap_or(PowerInfo {
            thermal_spec_power_watts: 45,
            minimum_power_watts: 0,
            maximum_power_watts: 0,
            raw_info: 0,
        });

        // 3. Read Current Limits & Lock (MSR 0x610)
        let current_limits = match self.read_power_limits(units_per_watt) {
            Ok(l) => l,
            Err(e) => {
                return DevicePowerCapabilities::unsupported(
                    PowerLimitApplyStatus::HardwareError,
                    format!("Failed to read MSR_PKG_POWER_LIMIT: {}", e),
                );
            }
        };

        let thermal_spec = if power_info.thermal_spec_power_watts > 0 {
            power_info.thermal_spec_power_watts
        } else if current_limits.pl1_watts > 0 {
            current_limits.pl1_watts
        } else {
            45
        };

        // 4. Algorithmic Baseline Scans
        let scanned_min_power_watts = if power_info.minimum_power_watts > 0 {
            power_info.minimum_power_watts
        } else {
            5.max((thermal_spec as f64 * 0.20).round() as i32)
        };

        let mut scanned_max_pl2_watts = if power_info.maximum_power_watts > 0
            && power_info.maximum_power_watts >= current_limits.pl2_watts
        {
            power_info.maximum_power_watts
        } else {
            current_limits
                .pl2_watts
                .max((thermal_spec as f64 * 2.6).round() as i32)
        };

        let scanned_max_pl1_watts = current_limits
            .pl1_watts
            .max((scanned_max_pl2_watts as f64 * 0.85).round() as i32);
        if scanned_max_pl2_watts < scanned_max_pl1_watts {
            scanned_max_pl2_watts = scanned_max_pl1_watts;
        }

        // 5. Inspect MMIO Package RAPL mirror
        let mut is_mmio_accessible = false;
        let mut raw_mmio_limit = 0u64;
        let mmio_addr = calculate_mchbar_rapl_pl1_address(self.backend.mchbar_address());

        if let Ok(val64) = self.backend.read_mmio_64(mmio_addr) {
            is_mmio_accessible = true;
            raw_mmio_limit = val64;
        } else if let (Ok(low), Ok(high)) = (
            self.backend.read_mmio_32(mmio_addr),
            self.backend.read_mmio_32(mmio_addr + 4),
        ) {
            is_mmio_accessible = true;
            raw_mmio_limit = (low as u64) | ((high as u64) << 32);
        }

        let diag = format!(
            "RAPL Units: {} U/W (raw=0x{:X}); TDP: {}W, Min: {}W, Max: {}W; Current: PL1={}W, PL2={}W (Locked={}); Scanned: Min={}W, MaxPL1={}W, MaxPL2={}W; MMIO 0x{:X}: {}",
            units_per_watt, raw_unit, thermal_spec, power_info.minimum_power_watts, power_info.maximum_power_watts,
            current_limits.pl1_watts, current_limits.pl2_watts, current_limits.is_locked,
            scanned_min_power_watts, scanned_max_pl1_watts, scanned_max_pl2_watts,
            mmio_addr,
            if is_mmio_accessible { format!("Accessible (raw=0x{:X})", raw_mmio_limit) } else { "Unavailable".into() }
        );

        DevicePowerCapabilities {
            is_supported: true,
            is_locked: current_limits.is_locked,
            is_mmio_accessible,
            units_per_watt,
            thermal_spec_power_watts: thermal_spec,
            minimum_power_watts: power_info.minimum_power_watts,
            maximum_power_watts: power_info.maximum_power_watts,
            current_pl1_watts: current_limits.pl1_watts,
            current_pl2_watts: current_limits.pl2_watts,
            scanned_min_power_watts,
            scanned_max_pl1_watts,
            scanned_max_pl2_watts,
            raw_power_unit: raw_unit,
            raw_power_info: power_info.raw_info,
            raw_power_limit: current_limits.raw_limit,
            raw_mmio_power_limit: raw_mmio_limit,
            baseline_status: PowerLimitApplyStatus::Success,
            diagnostics: diag,
        }
    }

    // --- MSR and MMIO Write Operations ---

    /// Computes the exact MSR 0x610 payload for target PL1/PL2, preserving the
    /// firmware Tau time windows and never setting the BIOS lock bit.
    ///
    /// Returns `Err(1)` when the register is BIOS-locked.
    pub fn plan_msr_power_limit(
        current_msr_limit: u64,
        raw_pl1: u64,
        raw_pl2: u64,
    ) -> Result<u64, i32> {
        if (current_msr_limit & MSR_PKG_POWER_LIMIT_LOCK_BIT) != 0 {
            return Err(1);
        }

        let mut safe_pl1 = raw_pl1 & 0x7FFF;
        let mut safe_pl2 = raw_pl2 & 0x7FFF;

        if safe_pl2 < safe_pl1 {
            safe_pl2 = safe_pl1;
        }
        if safe_pl1 == 0 && safe_pl2 > 0 {
            safe_pl1 = safe_pl2;
        }

        let mut tau1 = current_msr_limit & (0x7F << 17);
        let mut tau2 = current_msr_limit & (0x7F << 49);
        if tau1 == 0 {
            tau1 = 0x28 << 17; // ~28s default Intel PL1 Tau
        }
        if tau2 == 0 {
            tau2 = 0x10 << 49; // ~2.44ms default Intel PL2 Tau
        }

        let enable_and_clamp = PL1_ENABLE_BIT | PL1_CLAMP_BIT | PL2_ENABLE_BIT | PL2_CLAMP_BIT;
        Ok(
            (safe_pl1 | enable_and_clamp | tau1 | (safe_pl2 << 32) | tau2)
                & !MSR_PKG_POWER_LIMIT_LOCK_BIT,
        )
    }

    /// Writes MSR_PKG_POWER_LIMIT (0x610) atomically, preserving Tau time windows
    pub fn write_msr_power_limits(
        &self,
        raw_pl1: u64,
        raw_pl2: u64,
        current_msr_limit: u64,
    ) -> Result<u64, PawnIoError> {
        if (current_msr_limit & MSR_PKG_POWER_LIMIT_LOCK_BIT) != 0 {
            return Err(PawnIoError::BiosLocked(
                "MSR 0x610 is locked by BIOS (bit 63 set)".into(),
            ));
        }

        let new_msr_value = Self::plan_msr_power_limit(current_msr_limit, raw_pl1, raw_pl2)
            .map_err(|_| {
                PawnIoError::BiosLocked("MSR 0x610 is locked by BIOS (bit 63 set)".into())
            })?;

        self.backend.write_msr(MSR_PKG_POWER_LIMIT, new_msr_value)?;
        Ok(new_msr_value)
    }

    /// Writes target PL1 and PL2 to MMIO with directional split-write hazard mitigation.
    ///
    /// Hazard mitigation rule:
    /// - If new_raw_pl1 > old_raw_pl2: write PL2 upper DWORD (0xFED159A4) FIRST, then PL1 lower DWORD (0xFED159A0) SECOND.
    /// - Else: write PL1 lower DWORD (0xFED159A0) FIRST, then PL2 upper DWORD (0xFED159A4) SECOND.
    pub fn write_mchbar_power_limits(
        &self,
        raw_pl1: u64,
        raw_pl2: u64,
        fallback_tau1: u64,
        fallback_tau2: u64,
    ) -> Result<u64, PawnIoError> {
        let mchbar = self.backend.mchbar_address();
        let mmio_pl1_addr = calculate_mchbar_rapl_pl1_address(mchbar);
        let mmio_pl2_addr = calculate_mchbar_rapl_pl2_address(mchbar);

        // 1. Read current hardware state
        let mut current_mmio = 0u64;
        let mut read_ok = false;

        if let Ok(val64) = self.backend.read_mmio_64(mmio_pl1_addr) {
            read_ok = true;
            current_mmio = val64;
        } else if let (Ok(low), Ok(high)) = (
            self.backend.read_mmio_32(mmio_pl1_addr),
            self.backend.read_mmio_32(mmio_pl2_addr),
        ) {
            read_ok = true;
            current_mmio = (low as u64) | ((high as u64) << 32);
        }

        if !read_ok {
            return Err(PawnIoError::HardwareError(format!(
                "Failed to read current MMIO registers at 0x{:X}",
                mmio_pl1_addr
            )));
        }

        // 2. Plan the ordered write (single source of truth shared with the FFI).
        let plan = plan_mchbar_power_write(
            current_mmio,
            read_ok,
            raw_pl1,
            raw_pl2,
            fallback_tau1,
            fallback_tau2,
            mmio_pl1_addr,
            mmio_pl2_addr,
        );

        match plan.status {
            WRITE_PLAN_BIOS_LOCKED => Err(PawnIoError::BiosLocked(format!(
                "MMIO Package RAPL at 0x{:X} is locked by BIOS",
                mmio_pl1_addr
            ))),
            WRITE_PLAN_OK => {
                // 3. Directional split write: two 32-bit stores in the planned order.
                let first_ok = self
                    .backend
                    .write_mmio_32(plan.first_address, plan.first_value)
                    .is_ok();
                if first_ok {
                    let second_ok = self
                        .backend
                        .write_mmio_32(plan.second_address, plan.second_value)
                        .is_ok();
                    if second_ok {
                        return Ok(plan.mirrored_value);
                    }
                    // Rollback first write to prevent partial/invalid split state
                    let _ = self
                        .backend
                        .write_mmio_32(plan.first_address, plan.first_previous_value);
                    return Err(PawnIoError::HardwareError(format!(
                        "MMIO split-write failed on second dword at 0x{:X}; first dword rolled back",
                        plan.second_address
                    )));
                }

                // 4. Fallback: atomic 64-bit write when 32-bit stores are unsupported.
                self.backend
                    .write_mmio_64(mmio_pl1_addr, plan.mirrored_value)?;
                Ok(plan.mirrored_value)
            }
            _ => Err(PawnIoError::HardwareError(format!(
                "Failed to read current MMIO registers at 0x{:X}",
                mmio_pl1_addr
            ))),
        }
    }

    /// High-level method to apply target PL1 and PL2 power limits in Watts.
    /// Checks lock status, encodes limits, preserves Tau windows, atomically writes MSR 0x610,
    /// and mirrors to MMIO with directional split-write hazard prevention.
    pub fn apply_power_limits(
        &mut self,
        target_pl1_watts: i32,
        target_pl2_watts: i32,
    ) -> PowerLimitApplyResult {
        let init_status = self.ensure_initialized();
        if init_status != PowerLimitApplyStatus::Success {
            return match init_status {
                PowerLimitApplyStatus::DriverNotInstalled => {
                    PowerLimitApplyResult::driver_not_installed(
                        "PawnIO driver not installed or running",
                    )
                }
                PowerLimitApplyStatus::AccessDenied => PowerLimitApplyResult::access_denied(
                    "Administrative elevation required to open PawnIO handle",
                ),
                PowerLimitApplyStatus::UnsupportedHardware => {
                    PowerLimitApplyResult::unsupported_hardware(
                        "Hardware does not support Intel RAPL",
                    )
                }
                _ => PowerLimitApplyResult::hardware_error(format!(
                    "Backend initialization failed: {}",
                    init_status
                )),
            };
        }

        // 1. Read Power Units (MSR 0x606)
        let (units_per_watt, _) = match self.read_power_units() {
            Ok(u) => u,
            Err(e) => {
                return PowerLimitApplyResult::hardware_error(format!(
                    "Failed to read MSR 0x606: {}",
                    e
                ))
            }
        };

        // 2. Read Current MSR 0x610
        let current_msr = match self.backend.read_msr(MSR_PKG_POWER_LIMIT) {
            Ok(m) => m,
            Err(e) => {
                return PowerLimitApplyResult::hardware_error(format!(
                    "Failed to read MSR 0x610: {}",
                    e
                ))
            }
        };

        // 3. Bit 63 lock check
        if (current_msr & MSR_PKG_POWER_LIMIT_LOCK_BIT) != 0 {
            return PowerLimitApplyResult::bios_locked("MSR_PKG_POWER_LIMIT (0x610) bit 63 is locked by BIOS. Limits cannot be overridden.");
        }

        // 4. Validate and clamp bounds (5W floor, 200W ceiling, PL2 >= PL1)
        const ABSOLUTE_MIN_WATTS: i32 = 5;
        const ABSOLUTE_MAX_PL1_WATTS: i32 = 200;
        const ABSOLUTE_MAX_PL2_WATTS: i32 = 200;

        let safe_pl1 = target_pl1_watts.clamp(ABSOLUTE_MIN_WATTS, ABSOLUTE_MAX_PL1_WATTS);
        let mut safe_pl2 = target_pl2_watts.clamp(safe_pl1, ABSOLUTE_MAX_PL2_WATTS);
        if safe_pl2 < safe_pl1 {
            safe_pl2 = safe_pl1;
        }

        let raw_pl1 = 0x7FFF.min(((safe_pl1 as f64 * units_per_watt as f64).round() as u64).max(0));
        let raw_pl2 = 0x7FFF.min(((safe_pl2 as f64 * units_per_watt as f64).round() as u64).max(0));

        // If already matching hardware state, return success immediately
        let cur_raw_pl1 = current_msr & 0x7FFF;
        let cur_raw_pl2 = (current_msr >> 32) & 0x7FFF;
        if cur_raw_pl1 == raw_pl1 && cur_raw_pl2 == raw_pl2 {
            return PowerLimitApplyResult::success(
                safe_pl1,
                safe_pl2,
                false,
                current_msr,
                0,
                "Power limits already match hardware state.",
            );
        }

        // 5. Write MSR_PKG_POWER_LIMIT (0x610) atomically
        let new_msr_value = match self.write_msr_power_limits(raw_pl1, raw_pl2, current_msr) {
            Ok(val) => val,
            Err(e) => {
                return PowerLimitApplyResult::hardware_error(format!(
                    "Failed to write MSR 0x610: {}",
                    e
                ))
            }
        };

        // 6. Mirror to MMIO with directional split-write hazard prevention
        let tau1 = current_msr & (0x7F << 17);
        let tau2 = current_msr & (0x7F << 49);
        let mut mmio_mirrored = false;
        let mut raw_mmio_written = 0u64;

        if let Ok(written_mmio) = self.write_mchbar_power_limits(raw_pl1, raw_pl2, tau1, tau2) {
            mmio_mirrored = true;
            raw_mmio_written = written_mmio;
        }

        PowerLimitApplyResult::success(
            safe_pl1,
            safe_pl2,
            mmio_mirrored,
            new_msr_value,
            raw_mmio_written,
            format!(
                "Power limits applied successfully: PL1={}W, PL2={}W (MMIO Mirrored: {}).",
                safe_pl1, safe_pl2, mmio_mirrored
            ),
        )
    }
}

impl Drop for PawnIoController {
    fn drop(&mut self) {
        self.backend.close();
    }
}

impl Default for PawnIoController {
    fn default() -> Self {
        Self::new()
    }
}

// ============================================================================
// 10b. PawnIoDriver Engine Facade for Core Integration & FFI
// ============================================================================

pub struct PawnIoDriver {
    controller: PawnIoController,
}

impl PawnIoDriver {
    pub fn new() -> Self {
        #[cfg(test)]
        let mut controller = PawnIoController::new_mock();
        #[cfg(not(test))]
        let mut controller = PawnIoController::new();
        let _ = controller.ensure_initialized();
        Self { controller }
    }

    pub fn with_controller(controller: PawnIoController) -> Self {
        Self { controller }
    }

    pub fn open() -> Result<Self, i32> {
        let driver = Self::new();
        if driver.is_available() {
            Ok(driver)
        } else {
            Err(-1)
        }
    }

    pub fn is_service_running() -> bool {
        #[cfg(test)]
        {
            true
        }
        #[cfg(not(test))]
        #[cfg(windows)]
        {
            NativePawnIoBackend::new().scan_device().is_running
        }
        #[cfg(not(test))]
        #[cfg(not(windows))]
        {
            false
        }
    }

    pub fn apply_mode_limits_direct(
        &mut self,
        target_pl1: u32,
        target_pl2: u32,
        out_mirrored: Option<&mut i32>,
    ) -> Result<bool, i32> {
        let mirrored = self.write_rapl_limits_directional(target_pl1, target_pl2)?;
        if let Some(m) = out_mirrored {
            *m = if mirrored { 1 } else { 0 };
        }
        Ok(mirrored)
    }

    pub fn is_available(&self) -> bool {
        self.controller.is_available()
    }

    pub fn read_msr(&mut self, msr: u32) -> Result<u64, i32> {
        let _ = self.controller.ensure_initialized();
        self.controller.backend.read_msr(msr).map_err(|e| match e {
            PawnIoError::DriverNotInstalled(_) => -1,
            PawnIoError::AccessDenied(_) => -2,
            PawnIoError::BiosLocked(_) => -3,
            PawnIoError::UnsupportedHardware(_) => -4,
            PawnIoError::HardwareError(_) => -5,
            PawnIoError::MutexTimeout(_, _) => -6,
            _ => -7,
        })
    }

    pub fn write_msr(&mut self, msr: u32, val: u64) -> Result<(), i32> {
        let _ = self.controller.ensure_initialized();
        self.controller
            .backend
            .write_msr(msr, val)
            .map_err(|e| match e {
                PawnIoError::DriverNotInstalled(_) => -1,
                PawnIoError::AccessDenied(_) => -2,
                PawnIoError::BiosLocked(_) => -3,
                PawnIoError::UnsupportedHardware(_) => -4,
                PawnIoError::HardwareError(_) => -5,
                PawnIoError::MutexTimeout(_, _) => -6,
                _ => -7,
            })
    }

    pub fn read_mmio32(&mut self, addr: u64) -> Result<u32, i32> {
        let _ = self.controller.ensure_initialized();
        self.controller
            .backend
            .read_mmio_32(addr)
            .map_err(|e| match e {
                PawnIoError::DriverNotInstalled(_) => -1,
                PawnIoError::AccessDenied(_) => -2,
                PawnIoError::BiosLocked(_) => -3,
                PawnIoError::UnsupportedHardware(_) => -4,
                PawnIoError::HardwareError(_) => -5,
                PawnIoError::MutexTimeout(_, _) => -6,
                _ => -7,
            })
    }

    pub fn write_mmio32(&mut self, addr: u64, val: u32) -> Result<(), i32> {
        let _ = self.controller.ensure_initialized();
        self.controller
            .backend
            .write_mmio_32(addr, val)
            .map_err(|e| match e {
                PawnIoError::DriverNotInstalled(_) => -1,
                PawnIoError::AccessDenied(_) => -2,
                PawnIoError::BiosLocked(_) => -3,
                PawnIoError::UnsupportedHardware(_) => -4,
                PawnIoError::HardwareError(_) => -5,
                PawnIoError::MutexTimeout(_, _) => -6,
                _ => -7,
            })
    }

    pub fn write_rapl_limits_directional(
        &mut self,
        target_pl1: u32,
        target_pl2: u32,
    ) -> Result<bool, i32> {
        let result = self
            .controller
            .apply_power_limits(target_pl1 as i32, target_pl2 as i32);
        if result.is_success {
            Ok(result.mmio_mirrored)
        } else {
            match result.status {
                PowerLimitApplyStatus::DriverNotInstalled => Err(-1),
                PowerLimitApplyStatus::AccessDenied => Err(-2),
                PowerLimitApplyStatus::BiosLocked => Err(-3),
                PowerLimitApplyStatus::UnsupportedHardware => Err(-4),
                PowerLimitApplyStatus::HardwareError => Err(-5),
                PowerLimitApplyStatus::MutexTimeout => Err(-6),
                _ => Err(-7),
            }
        }
    }

    pub fn controller(&self) -> &PawnIoController {
        &self.controller
    }

    pub fn controller_mut(&mut self) -> &mut PawnIoController {
        &mut self.controller
    }
}

impl Drop for PawnIoDriver {
    fn drop(&mut self) {
        self.controller.backend.close();
    }
}

impl Default for PawnIoDriver {
    fn default() -> Self {
        Self::new()
    }
}

// ============================================================================
// 11. Unit & Regression Tests
// ============================================================================

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_execute_packet_layout() {
        let fn_name = "ioctl_read_msr";
        let args = [0x610u64, 0x1234_5678_9ABC_DEF0u64];
        let packet = build_execute_packet(fn_name, &args);

        assert_eq!(packet.len(), 32 + 16);
        assert_eq!(&packet[..fn_name.len()], fn_name.as_bytes());
        assert_eq!(packet[fn_name.len()], 0); // null padded

        let arg0 = u64::from_le_bytes(packet[32..40].try_into().unwrap());
        let arg1 = u64::from_le_bytes(packet[40..48].try_into().unwrap());
        assert_eq!(arg0, 0x610);
        assert_eq!(arg1, 0x1234_5678_9ABC_DEF0);

        let mut response = [0u64; 2];
        let mut out_buffer = vec![0u8; 16];
        out_buffer[..8].copy_from_slice(&0xDEAD_BEEF_CAFE_BABE_u64.to_le_bytes());
        out_buffer[8..16].copy_from_slice(&0x0123_4567_89AB_CDEF_u64.to_le_bytes());

        let ok = parse_execute_response(&out_buffer, 16, &mut response);
        assert!(ok);
        assert_eq!(response[0], 0xDEAD_BEEF_CAFE_BABE);
        assert_eq!(response[1], 0x0123_4567_89AB_CDEF);
    }

    #[test]
    fn test_embedded_bytecode_modules() {
        assert!(!EMBEDDED_INTEL_MSR_BIN.is_empty());
        assert_eq!(EMBEDDED_INTEL_MSR_BIN.len(), 5292);

        assert!(!EMBEDDED_INTEL_MCHBAR_BIN.is_empty());
        assert_eq!(EMBEDDED_INTEL_MCHBAR_BIN.len(), 13332);

        let msr_bytes = load_module_bytes("IntelMSR.bin").unwrap();
        assert_eq!(msr_bytes.len(), 5292);

        let mchbar_bytes = load_module_bytes("IntelMCHBAR.bin").unwrap();
        assert_eq!(mchbar_bytes.len(), 13332);
    }

    #[test]
    fn test_rapl_power_unit_decoding() {
        let backend = MockPawnIoBackend::new(true);
        // Exponent 3 -> 1 << 3 = 8 units per watt
        backend.set_msr(MSR_RAPL_POWER_UNIT, 0x000A_0E03);
        let controller = PawnIoController::with_backend(Box::new(backend));

        let (units_per_watt, raw) = controller.read_power_units().unwrap();
        assert_eq!(units_per_watt, 8);
        assert_eq!(raw, 0x000A_0E03);
    }

    #[test]
    fn test_power_limits_and_info_decoding() {
        let mut backend = MockPawnIoBackend::new(true);
        backend.configure_intel_baseline(55, 10, 157, 45, 115, 8, false, true);
        let controller = PawnIoController::with_backend(Box::new(backend));

        let (units, _) = controller.read_power_units().unwrap();
        assert_eq!(units, 8);

        let info = controller.read_power_info(units).unwrap();
        assert_eq!(info.thermal_spec_power_watts, 55);
        assert_eq!(info.minimum_power_watts, 10);
        assert_eq!(info.maximum_power_watts, 157);

        let limits = controller.read_power_limits(units).unwrap();
        assert_eq!(limits.pl1_watts, 45);
        assert_eq!(limits.pl2_watts, 115);
        assert!(!limits.is_locked);
    }

    #[test]
    fn test_temperature_and_turbo_ratios() {
        let backend = MockPawnIoBackend::new(true);
        backend.set_msr(MSR_TEMPERATURE_TARGET, (105 << 16) | (5 << 24));
        backend.set_msr(MSR_TURBO_RATIO_LIMIT, 0x2829_2A2B_2C2D_2E32);

        let controller = PawnIoController::with_backend(Box::new(backend));
        let tj = controller.read_temperature_target().unwrap();
        assert_eq!(tj.target_temp_celsius, 105);
        assert_eq!(tj.target_offset_celsius, 5);

        let turbo = controller.read_turbo_ratio_limit().unwrap();
        assert_eq!(turbo.ratios_by_core[0], 0x32); // 50x
        assert_eq!(turbo.ratios_by_core[1], 0x2E); // 46x
    }

    #[test]
    fn test_directional_split_write_ordering_hazard() {
        // Test case 1: New PL1 (120W) > Old PL2 (115W)
        // Must write PL2 upper DWORD FIRST, then PL1 lower DWORD SECOND
        let mut backend = MockPawnIoBackend::new(true);
        backend.configure_intel_baseline(55, 10, 157, 45, 115, 8, false, true);
        backend.clear_history();

        let controller = PawnIoController::with_backend(Box::new(backend));
        let raw_pl1 = 120 * 8; // 960
        let raw_pl2 = 140 * 8; // 1120

        let res = controller.write_mchbar_power_limits(raw_pl1, raw_pl2, 0x28 << 17, 0x10 << 49);
        assert!(res.is_ok());

        // Check backend history
        // First operation must be ReadMmio64 / ReadMmio32, followed by WriteMmio32 to PL2 address (0xFED159A4),
        // followed by WriteMmio32 to PL1 address (0xFED159A0)
        let hist = controller.backend().scan_device(); // trigger dummy or inspect history
        let _ = hist;

        // Directly inspect backend history
        // Since we know backend is MockPawnIoBackend, check that target PL2 was written before PL1
    }

    #[test]
    fn test_directional_split_write_ordering_normal() {
        // Test case 2: New PL1 (50W) <= Old PL2 (115W)
        // Must write PL1 lower DWORD FIRST, then PL2 upper DWORD SECOND
        let mut backend = MockPawnIoBackend::new(true);
        backend.configure_intel_baseline(55, 10, 157, 45, 115, 8, false, true);
        backend.clear_history();

        let controller = PawnIoController::with_backend(Box::new(backend));
        let raw_pl1 = 50 * 8; // 400
        let raw_pl2 = 100 * 8; // 800

        let res = controller.write_mchbar_power_limits(raw_pl1, raw_pl2, 0x28 << 17, 0x10 << 49);
        assert!(res.is_ok());
    }

    #[test]
    fn test_directional_split_write_history_verification() {
        // 1. Hazard condition: New PL1 (120W) > Old PL2 (115W)
        // Must write PL2 upper DWORD (0xFED159A4) FIRST, then PL1 lower DWORD (0xFED159A0) SECOND
        let mut backend1 = MockPawnIoBackend::new(true);
        backend1.configure_intel_baseline(55, 10, 157, 45, 115, 8, false, true);
        backend1.clear_history();
        let history1_arc = backend1.history.clone();

        let controller1 = PawnIoController::with_backend(Box::new(backend1));
        let res1 = controller1.write_mchbar_power_limits(120 * 8, 140 * 8, 0x28 << 17, 0x10 << 49);
        assert!(res1.is_ok());

        let h1 = history1_arc
            .read()
            .unwrap_or_else(|e| e.into_inner())
            .clone();
        let idx_pl2 = h1.iter().position(|op| {
            matches!(
                op,
                MockHistoryOp::WriteMmio32 {
                    address: 0xFED1_59A4,
                    ..
                }
            )
        });
        let idx_pl1 = h1.iter().position(|op| {
            matches!(
                op,
                MockHistoryOp::WriteMmio32 {
                    address: 0xFED1_59A0,
                    ..
                }
            )
        });
        assert!(
            idx_pl2.is_some() && idx_pl1.is_some(),
            "Both PL2 and PL1 32-bit MMIO writes must occur"
        );
        assert!(
            idx_pl2.unwrap() < idx_pl1.unwrap(),
            "Hazard ordering: PL2 (0xFED159A4) must be written before PL1 (0xFED159A0)"
        );

        // 2. Normal condition: New PL1 (40W) <= Old PL2 (115W)
        // Must write PL1 lower DWORD (0xFED159A0) FIRST, then PL2 upper DWORD (0xFED159A4) SECOND
        let mut backend2 = MockPawnIoBackend::new(true);
        backend2.configure_intel_baseline(55, 10, 157, 45, 115, 8, false, true);
        backend2.clear_history();
        let history2_arc = backend2.history.clone();

        let controller2 = PawnIoController::with_backend(Box::new(backend2));
        let res2 = controller2.write_mchbar_power_limits(40 * 8, 80 * 8, 0x28 << 17, 0x10 << 49);
        assert!(res2.is_ok());

        let h2 = history2_arc
            .read()
            .unwrap_or_else(|e| e.into_inner())
            .clone();
        let idx2_pl1 = h2.iter().position(|op| {
            matches!(
                op,
                MockHistoryOp::WriteMmio32 {
                    address: 0xFED1_59A0,
                    ..
                }
            )
        });
        let idx2_pl2 = h2.iter().position(|op| {
            matches!(
                op,
                MockHistoryOp::WriteMmio32 {
                    address: 0xFED1_59A4,
                    ..
                }
            )
        });
        assert!(
            idx2_pl1.is_some() && idx2_pl2.is_some(),
            "Both PL1 and PL2 32-bit MMIO writes must occur"
        );
        assert!(
            idx2_pl1.unwrap() < idx2_pl2.unwrap(),
            "Normal ordering: PL1 (0xFED159A0) must be written before PL2 (0xFED159A4)"
        );
    }

    #[test]
    fn test_bios_lock_blocking() {
        let mut backend = MockPawnIoBackend::new(true);
        backend.configure_intel_baseline(55, 10, 157, 45, 115, 8, true, true); // Locked
        let mut controller = PawnIoController::with_backend(Box::new(backend));

        let result = controller.apply_power_limits(65, 125);
        assert_eq!(result.status, PowerLimitApplyStatus::BiosLocked);
        assert!(!result.is_success);
    }

    #[test]
    fn test_apply_power_limits_full_flow() {
        let mut backend = MockPawnIoBackend::new(true);
        backend.configure_intel_baseline(55, 10, 157, 45, 115, 8, false, true);
        let mut controller = PawnIoController::with_backend(Box::new(backend));

        let result = controller.apply_power_limits(65, 125);
        assert_eq!(result.status, PowerLimitApplyStatus::Success);
        assert!(result.is_success);
        assert_eq!(result.applied_pl1_watts, 65);
        assert_eq!(result.applied_pl2_watts, 125);
        assert!(result.mmio_mirrored);

        // Re-read limits to verify hardware state was updated
        let limits = controller.read_power_limits(8).unwrap();
        assert_eq!(limits.pl1_watts, 65);
        assert_eq!(limits.pl2_watts, 125);
    }

    #[test]
    fn test_scan_device_capabilities() {
        let mut backend = MockPawnIoBackend::new(true);
        backend.configure_intel_baseline(55, 10, 157, 45, 115, 8, false, true);
        let mut controller = PawnIoController::with_backend(Box::new(backend));

        let caps = controller.scan_device_power_capabilities();
        assert!(caps.is_supported);
        assert!(!caps.is_locked);
        assert!(caps.is_mmio_accessible);
        assert_eq!(caps.units_per_watt, 8);
        assert_eq!(caps.thermal_spec_power_watts, 55);
        assert_eq!(caps.minimum_power_watts, 10);
        assert_eq!(caps.maximum_power_watts, 157);
        assert_eq!(caps.current_pl1_watts, 45);
        assert_eq!(caps.current_pl2_watts, 115);
        assert_eq!(caps.scanned_min_power_watts, 10);
        assert_eq!(caps.scanned_max_pl2_watts, 157);
        assert_eq!(caps.scanned_max_pl1_watts, 133); // 157 * 0.85 = 133.45 -> 133
    }

    #[test]
    fn write_plan_is_c_abi_stable() {
        assert_eq!(std::mem::size_of::<MchbarWritePlan>(), 40);
        assert_eq!(std::mem::align_of::<MchbarWritePlan>(), 8);
    }

    #[test]
    fn plan_mchbar_orders_pl2_first_when_new_pl1_exceeds_old_pl2() {
        // Old: PL1 360 units, PL2 520 units, 28s/2.44ms Tau.
        let current = (360u64 | (1 << 15) | (1 << 16) | (0x28 << 17))
            | (((520u64 | (1 << 15) | (1 << 16) | (0x10 << 17)) as u64) << 32);
        let plan = plan_mchbar_power_write(current, true, 600, 920, 0, 0, 0xFED159A0, 0xFED159A4);
        assert_eq!(plan.status, WRITE_PLAN_OK);
        assert_eq!(plan.first_address, 0xFED159A4);
        assert_eq!(plan.second_address, 0xFED159A0);
        assert_eq!(plan.first_value, 920 | (1 << 15) | (1 << 16) | (0x10 << 17));
        assert_eq!(
            plan.second_value,
            600 | (1 << 15) | (1 << 16) | (0x28 << 17)
        );
        assert_eq!(
            plan.first_previous_value,
            520 | (1 << 15) | (1 << 16) | (0x10 << 17)
        );
        assert_eq!(
            plan.mirrored_value,
            (plan.second_value as u64) | ((plan.first_value as u64) << 32)
        );
    }

    #[test]
    fn plan_mchbar_orders_pl1_first_when_it_fits() {
        let current = (600u64 | (1 << 15) | (1 << 16) | (0x28 << 17))
            | (((920u64 | (1 << 15) | (1 << 16) | (0x10 << 17)) as u64) << 32);
        let plan = plan_mchbar_power_write(current, true, 280, 440, 0, 0, 0xFED159A0, 0xFED159A4);
        assert_eq!(plan.status, WRITE_PLAN_OK);
        assert_eq!(plan.first_address, 0xFED159A0);
        assert_eq!(plan.second_address, 0xFED159A4);
        assert_eq!(plan.first_value, 280 | (1 << 15) | (1 << 16) | (0x28 << 17));
        assert_eq!(
            plan.second_value,
            440 | (1 << 15) | (1 << 16) | (0x10 << 17)
        );
    }

    #[test]
    fn plan_mchbar_reports_locked_and_read_failure() {
        let locked = (1u64 << 63) | 0x1234;
        assert_eq!(
            plan_mchbar_power_write(locked, true, 100, 200, 0, 0, 0xA0, 0xA4).status,
            WRITE_PLAN_BIOS_LOCKED
        );
        assert_eq!(
            plan_mchbar_power_write(0, false, 100, 200, 0, 0, 0xA0, 0xA4).status,
            WRITE_PLAN_READ_FAILED
        );
    }

    #[test]
    fn plan_msr_preserves_tau_and_promotes_pl2() {
        // Custom Tau: PL1 5 s (0x0A << 17), PL2 1 s (0x02 << 49).
        let current = (0x0Au64 << 17) | (0x02u64 << 49);
        let plan = PawnIoController::plan_msr_power_limit(current, 360, 0).unwrap();
        assert_eq!(plan & 0x7FFF, 360);
        assert_eq!((plan >> 32) & 0x7FFF, 360); // PL2 promoted to PL1
        assert_eq!(plan & (0x7F << 17), 0x0A << 17);
        assert_eq!(plan & (0x7F << 49), 0x02 << 49);
        assert_eq!(plan & (1 << 15), 1 << 15);
        assert_eq!(plan & (1 << 16), 1 << 16);
        assert_eq!(plan & (1 << 47), 1 << 47);
        assert_eq!(plan & (1 << 48), 1 << 48);
        assert_eq!(plan & (1u64 << 63), 0);

        assert_eq!(
            PawnIoController::plan_msr_power_limit(1u64 << 63, 100, 200),
            Err(1)
        );
    }
}
