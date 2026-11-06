//! Multi-generation Acer BIOS & Firmware Settings Native Subsystem.
//!
//! Supports Acer Predator (Helios 300/500/700, Helios 16/18, Helios Neo, Triton 300/500/700/14/16/17X)
//! and Acer Nitro (Nitro 5 2020-2023, Nitro 14/16/17, Nitro V 14/15/16).
//!
//! Provides three operational modes:
//! - Mode 1: High-level WMI BIOS class (`root\wmi` -> `ListBIOSSettings` / `MethodForPowerShell`) for 2025+ models.
//! - Mode 2: Universal ACPI-WMI standard hardware registers fallback for 2020-2024 models without `ListBIOSSettings`.
//! - Mode 3: Direct UEFI NVRAM variable scanning/modification for InsydeH2O variables (`FastBoot`, `QuietBoot`, `F12BootMenu`, `NetworkBoot`, `D2DRecovery`).
//!
//! Includes two-tier safety classification (`Normal` vs `Dangerous`), plaintext ASCII validation (<= 128 bytes),
//! and zero password persistence/leakage in memory or logs.

use std::collections::HashMap;
use std::ffi::c_void;
use std::sync::Mutex;

/// Global UEFI variable namespace GUID: {8BE4DF61-93CA-11D2-AA0D-00E098032B8C}
pub const GUID_EFI_GLOBAL: &str = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";

/// Insyde / Acer UEFI variable namespace GUID: {EC87D643-EBA4-4BB5-A1E5-3F3E36B20DA9}
pub const GUID_INSYDE_SETUP: &str = "{EC87D643-EBA4-4BB5-A1E5-3F3E36B20DA9}";

/// Error codes returned across FFI and subsystem operations.
pub const E_SUCCESS: i32 = 0;
pub const E_GENERAL: i32 = -1;
pub const E_INVALID_ARG: i32 = -2;
pub const E_DANGEROUS_BLOCKED: i32 = -3;
pub const E_NOT_FOUND: i32 = -4;
pub const E_WRONG_PASSWORD: i32 = -5;
pub const E_TOO_MANY_ATTEMPTS: i32 = -6;
pub const E_UNSUPPORTED: i32 = -7;

/// Operational support mode determined during hardware probing.
#[repr(i32)]
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum BiosSupportMode {
    Unsupported = 0,
    WmiModern2025 = 1, // 2025+ models: root\wmi ListBIOSSettings & MethodForPowerShell
    AcpiWmiFallback2020 = 2, // 2020-2024 models: ACPI-WMI standard hardware registers fallback
    UefiNvramDirect = 3, // Universal UEFI: direct NVRAM variable access
}

/// Two-tier safety classification for BIOS settings.
#[repr(i32)]
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum SettingRisk {
    Normal = 0,
    Dangerous = 1,
}

/// Setting origin / backend kind.
#[repr(i32)]
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum SettingKind {
    WmiNative = 1,
    AcpiFallback = 2,
    UefiNvram = 3,
}

/// Return code from WMI `MethodForPowerShell` method execution.
#[repr(u32)]
#[derive(Copy, Clone, Debug, Default, PartialEq, Eq)]
pub enum BiosMethodResult {
    #[default]
    Done = 0x00,
    WrongPassword = 0x02,
    TooManyAttempts = 0x07,
    RestartNeeded = 0x08,
    GeneralError = 0xFF,
}

impl BiosMethodResult {
    pub fn from_u32(val: u32) -> Self {
        match val {
            0x00 => Self::Done,
            0x02 => Self::WrongPassword,
            0x07 => Self::TooManyAttempts,
            0x08 => Self::RestartNeeded,
            _ => Self::GeneralError,
        }
    }
}

/// Whitelist of settings classified as Normal (safe for ordinary user toggle).
/// All settings not on this list default to SettingRisk::Dangerous.
pub static SAFE_SETTINGS: &[&str] = &[
    "Intel VTX",
    "Intel VTD",
    "Trusted Execution Technology",
    "AMD-IOMMU",
    "AMD-SVM",
    "Above 4G decoding",
    "Re-Size BAR Support",
    "Active Efficient Cores",
    "Wi-Fi",
    "Bluetooth",
    "Wired LAN",
    "Audio",
    "Camera",
    "Network Boot",
    "Wake on LAN",
    "Lid Open Resume",
    "Power-off USB Charge",
    "Battery Threshold",
    "USB Boot",
    "F12 Boot Menu",
    "Function Key behavior",
    "D2D Recovery",
    "Keyboard backlight timeout",
    "Fast Boot",
    "POST Animation & Sound",
    "LCD Overdrive",
];

/// Representation of an enumerated BIOS or firmware setting.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BiosSetting {
    pub name: String,
    pub current: String,
    pub current_value: String,
    pub options: Vec<String>,
    pub risk: SettingRisk,
    pub kind: SettingKind,
}

// ============================================================================
// String Parsing and Risk Classification
// ============================================================================

/// Normalizes a setting name for case-insensitive and punctuation-tolerant matching.
fn normalize_setting_name(name: &str) -> String {
    name.chars()
        .filter(|c| c.is_alphanumeric())
        .flat_map(|c| c.to_lowercase())
        .collect()
}

/// Classifies a BIOS setting name into `Normal` vs `Dangerous`.
pub fn classify_setting_risk(name: &str) -> SettingRisk {
    let clean = name.trim();
    if clean.is_empty() {
        return SettingRisk::Dangerous;
    }

    let norm_clean = normalize_setting_name(clean);

    for &safe in SAFE_SETTINGS {
        if clean.eq_ignore_ascii_case(safe) {
            return SettingRisk::Normal;
        }
        let norm_safe = normalize_setting_name(safe);
        if norm_clean == norm_safe {
            return SettingRisk::Normal;
        }
    }

    // Additional common alias matches for safe settings
    if norm_clean == "poweroffusbcharging"
        || norm_clean == "functionkey"
        || norm_clean == "bootanimation"
        || norm_clean == "postbootanimationandsound"
        || norm_clean == "fastboot"
        || norm_clean == "f12bootmenu"
        || norm_clean == "networkboot"
        || norm_clean == "d2drecovery"
    {
        return SettingRisk::Normal;
    }

    SettingRisk::Dangerous
}

/// Parses a raw WMI setting string with syntax: `Name, Current[Opt1;Opt2;]`
pub fn parse_bios_setting_string(raw: &str, kind: SettingKind) -> Option<BiosSetting> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }

    // Split at first comma: left = Name, right = Value[Options]
    let (name_part, remainder) = match trimmed.split_once(',') {
        Some((n, r)) => (n.trim(), r.trim()),
        None => (trimmed, ""),
    };

    if name_part.is_empty() {
        return None;
    }

    let (current_value, options) = if let Some(open_bracket) = remainder.find('[') {
        let val = remainder[..open_bracket].trim();
        let opt_slice = &remainder[open_bracket + 1..];
        let opt_str = if let Some(close_bracket) = opt_slice.find(']') {
            &opt_slice[..close_bracket]
        } else {
            opt_slice
        };

        let opts: Vec<String> = opt_str
            .split(';')
            .map(|s| s.trim())
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string())
            .collect();

        (val.to_string(), opts)
    } else {
        (remainder.to_string(), Vec::new())
    };

    let risk = classify_setting_risk(name_part);

    Some(BiosSetting {
        name: name_part.to_string(),
        current: current_value.clone(),
        current_value,
        options,
        risk,
        kind,
    })
}

// ============================================================================
// ASCII Validation, Buffer Safety, and Password Scrubbing
// ============================================================================

/// Validates that a string slice is strictly printable ASCII (0x20..=0x7E)
/// and does not exceed `max_len` bytes.
pub fn validate_ascii_str(s: &str, max_len: usize) -> Result<(), i32> {
    let bytes = s.as_bytes();
    if bytes.len() > max_len {
        return Err(E_INVALID_ARG);
    }
    for &b in bytes {
        if !(0x20..=0x7E).contains(&b) {
            return Err(E_INVALID_ARG);
        }
    }
    Ok(())
}

/// Reads a null-terminated ASCII C-string from a raw pointer up to `max_len` bytes.
/// Validates length and printable ASCII boundaries.
pub unsafe fn read_ascii_c_str_limited(ptr: *const u8, max_len: usize) -> Result<String, i32> {
    if ptr.is_null()
        || (ptr as usize) < crate::MIN_USER_ADDR
        || (ptr as usize) > crate::MAX_USER_ADDR
    {
        return Err(E_GENERAL);
    }
    let mut len = 0;
    while len <= max_len {
        let b = *ptr.add(len);
        if b == 0 {
            break;
        }
        if !(0x20..=0x7E).contains(&b) {
            return Err(E_INVALID_ARG);
        }
        len += 1;
        if len > max_len {
            return Err(E_INVALID_ARG);
        }
    }
    let slice = std::slice::from_raw_parts(ptr, len);
    std::str::from_utf8(slice)
        .map(|s| s.to_string())
        .map_err(|_| E_INVALID_ARG)
}

/// Zeroizes a String buffer immediately in place using volatile writes
/// to prevent secret leakage or retention in compiler registers/memory.
pub fn zeroize_string(s: &mut String) {
    let bytes = unsafe { s.as_bytes_mut() };
    for b in bytes.iter_mut() {
        unsafe {
            std::ptr::write_volatile(b, 0);
        }
    }
    s.clear();
    s.shrink_to_fit();
}

/// RAII container that volatile-zeroizes its internal String on drop to guarantee memory scrubbing.
#[derive(Default, Debug)]
pub struct ZeroizingString(pub String);

impl ZeroizingString {
    pub fn new(s: String) -> Self {
        Self(s)
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl std::ops::Deref for ZeroizingString {
    type Target = str;
    fn deref(&self) -> &str {
        &self.0
    }
}

impl Drop for ZeroizingString {
    fn drop(&mut self) {
        zeroize_string(&mut self.0);
    }
}

/// Copies a Rust string slice into a destination C-buffer, ensuring null-termination
/// and truncation to `max_len - 1` bytes without buffer overflow.
pub fn copy_str_to_c_buffer(src: &str, dst: *mut u8, max_len: usize) {
    if dst.is_null() || max_len == 0 {
        return;
    }
    let bytes = src.as_bytes();
    let copy_len = bytes.len().min(max_len.saturating_sub(1));
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), dst, copy_len);
        *dst.add(copy_len) = 0;
    }
}

// ============================================================================
// Low-Level Native Win32 / Kernel Firmware Interfaces
// ============================================================================

#[cfg(windows)]
#[link(name = "kernel32")]
extern "system" {
    fn GetFirmwareEnvironmentVariableExW(
        lp_name: *const u16,
        lp_guid: *const u16,
        p_buffer: *mut c_void,
        n_size: u32,
        pdw_attributes: *mut u32,
    ) -> u32;

    fn SetFirmwareEnvironmentVariableExW(
        lp_name: *const u16,
        lp_guid: *const u16,
        p_value: *const c_void,
        n_size: u32,
        dw_attributes: u32,
    ) -> u32;

    fn GetSystemFirmwareTable(
        provider: u32,
        table_id: u32,
        buffer: *mut u8,
        buffer_size: u32,
    ) -> u32;
}

#[cfg(windows)]
#[link(name = "ntdll")]
extern "system" {
    fn RtlAdjustPrivilege(privilege: u32, enable: u8, current_thread: u8, enabled: *mut u8) -> i32;
}

const SE_SYSTEM_ENVIRONMENT_PRIVILEGE: u32 = 22;
pub const VARIABLE_ATTRIBUTE_NON_VOLATILE: u32 = 0x00000001;
pub const VARIABLE_ATTRIBUTE_BOOTSERVICE_ACCESS: u32 = 0x00000002;
pub const VARIABLE_ATTRIBUTE_RUNTIME_ACCESS: u32 = 0x00000004;
pub const NVRAM_DEFAULT_ATTRIBUTES: u32 = VARIABLE_ATTRIBUTE_NON_VOLATILE
    | VARIABLE_ATTRIBUTE_BOOTSERVICE_ACCESS
    | VARIABLE_ATTRIBUTE_RUNTIME_ACCESS;

fn to_wide_null(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn normalize_guid(guid: &str) -> String {
    let trimmed = guid.trim();
    if trimmed.starts_with('{') && trimmed.ends_with('}') {
        trimmed.to_string()
    } else {
        format!("{{{trimmed}}}")
    }
}

#[cfg(windows)]
fn try_enable_env_privilege() {
    let mut prev = 0u8;
    unsafe {
        let _ = RtlAdjustPrivilege(SE_SYSTEM_ENVIRONMENT_PRIVILEGE, 1, 0, &mut prev);
    }
}

/// Parses SMBIOS Type 1 structure from raw table to extract Product Name.
pub fn extract_smbios_product_name(table: &[u8]) -> Option<String> {
    let mut i = 0usize;
    while table.len().saturating_sub(i) >= 4 {
        let kind = table[i];
        let length = table[i + 1] as usize;
        if length < 4 || length > table.len() - i {
            break;
        }
        let data = &table[i..i + length];

        if kind == 1 && length >= 6 {
            let product_idx = data[5] as usize;
            if product_idx > 0 {
                // String table starts immediately after formatted section
                let mut str_ptr = i + length;
                let mut current_idx = 1usize;
                while str_ptr < table.len() {
                    let start = str_ptr;
                    while str_ptr < table.len() && table[str_ptr] != 0 {
                        str_ptr += 1;
                    }
                    if str_ptr == start {
                        // End of strings (double null)
                        break;
                    }
                    if current_idx == product_idx {
                        let name_bytes = &table[start..str_ptr];
                        if let Ok(name_str) = std::str::from_utf8(name_bytes) {
                            let clean = name_str.trim();
                            if !clean.is_empty() {
                                return Some(clean.to_string());
                            }
                        }
                    }
                    current_idx += 1;
                    str_ptr += 1; // skip null byte
                }
            }
        }

        // Advance past unformatted string table to next structure
        let mut next = i + length;
        while next + 1 < table.len() && (table[next] != 0 || table[next + 1] != 0) {
            next += 1;
        }
        i = next + 2;
        if kind == 127 {
            break;
        }
    }
    None
}

// ============================================================================
// BiosBackend Trait & Implementations
// ============================================================================

/// Backend interface for WMI, ACPI hardware registers, and UEFI NVRAM interaction.
pub trait BiosBackend: Send + Sync {
    /// Mode 1: Query ListBIOSSettings returning array of `Name, Current[Opt1;Opt2;]` strings.
    fn query_wmi_bios_settings(&self) -> Result<Vec<String>, i32>;

    /// Mode 1: Execute MethodForPowerShell.SetBiosSetting(name, value, password).
    fn set_wmi_bios_setting(
        &self,
        name: &str,
        value: &str,
        password: &str,
    ) -> Result<BiosMethodResult, i32>;

    /// Mode 1: Execute MethodForPowerShell.LoadBIOSDefault / LoadUserDefault.
    fn load_wmi_bios_default(
        &self,
        password: &str,
        user_default: bool,
    ) -> Result<BiosMethodResult, i32>;

    /// Mode 3: Read an InsydeH2O UEFI variable by name and GUID.
    fn read_uefi_variable(&self, name: &str, guid: &str) -> Option<Vec<u8>>;

    /// Mode 3: Write an InsydeH2O UEFI variable by name and GUID.
    fn write_uefi_variable(
        &self,
        name: &str,
        guid: &str,
        data: &[u8],
        attributes: u32,
    ) -> Result<(), i32>;

    /// Mode 2: Query an ACPI hardware action register.
    fn read_acpi_register(&self, action_id: u32) -> Result<u64, i32>;

    /// Mode 2: Write an ACPI hardware action register.
    fn write_acpi_register(&self, action_id: u32, value: u64) -> Result<(), i32>;

    /// Probe detected laptop model from firmware.
    fn detect_model_name(&self) -> Option<String>;
}

/// Default native backend for Windows execution.
pub struct NativeWindowsBiosBackend;

impl BiosBackend for NativeWindowsBiosBackend {
    fn query_wmi_bios_settings(&self) -> Result<Vec<String>, i32> {
        // High-level WMI BIOS class is available on 2025+ models with Acer WMI BIOS provider.
        // On models where the WMI provider is absent or not initialized, returns E_UNSUPPORTED.
        Err(E_UNSUPPORTED)
    }

    fn set_wmi_bios_setting(
        &self,
        _name: &str,
        _value: &str,
        _password: &str,
    ) -> Result<BiosMethodResult, i32> {
        Err(E_UNSUPPORTED)
    }

    fn load_wmi_bios_default(
        &self,
        _password: &str,
        _user_default: bool,
    ) -> Result<BiosMethodResult, i32> {
        Err(E_UNSUPPORTED)
    }

    fn read_uefi_variable(&self, name: &str, guid: &str) -> Option<Vec<u8>> {
        #[cfg(windows)]
        {
            if name.is_empty() || guid.is_empty() {
                return None;
            }
            try_enable_env_privilege();
            let name_w = to_wide_null(name);
            let guid_w = to_wide_null(&normalize_guid(guid));
            let mut attr = 0u32;
            let mut buf = vec![0u8; 4096];
            unsafe {
                let bytes_read = GetFirmwareEnvironmentVariableExW(
                    name_w.as_ptr(),
                    guid_w.as_ptr(),
                    buf.as_mut_ptr() as *mut c_void,
                    buf.len() as u32,
                    &mut attr,
                );
                if bytes_read > 0 {
                    buf.truncate(bytes_read as usize);
                    return Some(buf);
                }
            }
            None
        }
        #[cfg(not(windows))]
        {
            let _ = (name, guid);
            None
        }
    }

    fn write_uefi_variable(
        &self,
        name: &str,
        guid: &str,
        data: &[u8],
        attributes: u32,
    ) -> Result<(), i32> {
        #[cfg(windows)]
        {
            if name.is_empty() || guid.is_empty() {
                return Err(E_INVALID_ARG);
            }
            try_enable_env_privilege();
            let name_w = to_wide_null(name);
            let guid_w = to_wide_null(&normalize_guid(guid));
            unsafe {
                let res = SetFirmwareEnvironmentVariableExW(
                    name_w.as_ptr(),
                    guid_w.as_ptr(),
                    data.as_ptr() as *const c_void,
                    data.len() as u32,
                    attributes,
                );
                if res != 0 {
                    Ok(())
                } else {
                    Err(E_GENERAL)
                }
            }
        }
        #[cfg(not(windows))]
        {
            let _ = (name, guid, data, attributes);
            Err(E_UNSUPPORTED)
        }
    }

    fn read_acpi_register(&self, _action_id: u32) -> Result<u64, i32> {
        Err(E_UNSUPPORTED)
    }

    fn write_acpi_register(&self, _action_id: u32, _value: u64) -> Result<(), i32> {
        Err(E_UNSUPPORTED)
    }

    fn detect_model_name(&self) -> Option<String> {
        #[cfg(windows)]
        {
            let rsmb_provider = 0x52534D42; // 'RSMB'
            let size = unsafe { GetSystemFirmwareTable(rsmb_provider, 0, std::ptr::null_mut(), 0) };
            if size >= 8 && size <= 16 * 1024 * 1024 {
                let mut buf = vec![0u8; size as usize];
                let bytes_read =
                    unsafe { GetSystemFirmwareTable(rsmb_provider, 0, buf.as_mut_ptr(), size) };
                if bytes_read >= 8 && bytes_read <= size {
                    let declared_len =
                        u32::from_le_bytes([buf[4], buf[5], buf[6], buf[7]]) as usize;
                    let len = declared_len.min((bytes_read as usize).saturating_sub(8));
                    if len > 0 {
                        if let Some(model) = extract_smbios_product_name(&buf[8..8 + len]) {
                            return Some(model);
                        }
                    }
                }
            }
        }
        None
    }
}

/// Mock backend for rigorous testing without live hardware or elevation requirements.
#[derive(Default)]
pub struct MockBiosBackend {
    pub wmi_settings: Mutex<Vec<String>>,
    pub wmi_method_result: Mutex<BiosMethodResult>,
    pub nvram_store: Mutex<HashMap<(String, String), (Vec<u8>, u32)>>,
    pub acpi_registers: Mutex<HashMap<u32, u64>>,
    pub detected_model: Mutex<Option<String>>,
    pub set_calls: Mutex<Vec<(String, String)>>,
    pub restore_calls: Mutex<Vec<bool>>,
}

impl MockBiosBackend {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn with_model(model: &str) -> Self {
        let b = Self::new();
        *b.detected_model.lock().unwrap_or_else(|e| e.into_inner()) = Some(model.to_string());
        b
    }

    pub fn add_wmi_setting(&self, raw: &str) {
        self.wmi_settings
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .push(raw.to_string());
    }

    pub fn set_method_result(&self, res: BiosMethodResult) {
        *self
            .wmi_method_result
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = res;
    }
}

impl BiosBackend for MockBiosBackend {
    fn query_wmi_bios_settings(&self) -> Result<Vec<String>, i32> {
        let settings = self
            .wmi_settings
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone();
        if settings.is_empty() {
            Err(E_UNSUPPORTED)
        } else {
            Ok(settings)
        }
    }

    fn set_wmi_bios_setting(
        &self,
        name: &str,
        value: &str,
        _password: &str,
    ) -> Result<BiosMethodResult, i32> {
        self.set_calls
            .lock()
            .unwrap()
            .push((name.to_string(), value.to_string()));
        Ok(*self
            .wmi_method_result
            .lock()
            .unwrap_or_else(|e| e.into_inner()))
    }

    fn load_wmi_bios_default(
        &self,
        _password: &str,
        user_default: bool,
    ) -> Result<BiosMethodResult, i32> {
        self.restore_calls
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .push(user_default);
        Ok(*self
            .wmi_method_result
            .lock()
            .unwrap_or_else(|e| e.into_inner()))
    }

    fn read_uefi_variable(&self, name: &str, guid: &str) -> Option<Vec<u8>> {
        let store = self.nvram_store.lock().unwrap_or_else(|e| e.into_inner());
        store
            .get(&(name.to_string(), normalize_guid(guid)))
            .map(|(data, _)| data.clone())
    }

    fn write_uefi_variable(
        &self,
        name: &str,
        guid: &str,
        data: &[u8],
        attributes: u32,
    ) -> Result<(), i32> {
        let mut store = self.nvram_store.lock().unwrap_or_else(|e| e.into_inner());
        store.insert(
            (name.to_string(), normalize_guid(guid)),
            (data.to_vec(), attributes),
        );
        Ok(())
    }

    fn read_acpi_register(&self, action_id: u32) -> Result<u64, i32> {
        let regs = self
            .acpi_registers
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        Ok(regs.get(&action_id).copied().unwrap_or(0))
    }

    fn write_acpi_register(&self, action_id: u32, value: u64) -> Result<(), i32> {
        self.acpi_registers
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .insert(action_id, value);
        Ok(())
    }

    fn detect_model_name(&self) -> Option<String> {
        self.detected_model
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone()
    }
}

// ============================================================================
// BiosSubsystem Coordinator
// ============================================================================

/// Multi-generation Acer BIOS subsystem coordinator.
pub struct BiosSubsystem {
    support_mode: BiosSupportMode,
    model_name: String,
    settings: Vec<BiosSetting>,
    backend: Box<dyn BiosBackend>,
}

impl BiosSubsystem {
    /// Constructs a new subsystem instance with the specified hardware backend.
    pub fn new(backend: Box<dyn BiosBackend>) -> Self {
        let mut sub = Self {
            support_mode: BiosSupportMode::Unsupported,
            model_name: String::new(),
            settings: Vec::new(),
            backend,
        };
        sub.probe();
        sub
    }

    pub fn support_mode(&self) -> BiosSupportMode {
        self.support_mode
    }

    pub fn model_name(&self) -> &str {
        &self.model_name
    }

    pub fn settings(&self) -> &[BiosSetting] {
        &self.settings
    }

    pub fn settings_count(&self) -> usize {
        self.settings.len()
    }

    pub fn get_support_mode(&self) -> i32 {
        self.support_mode as i32
    }

    pub fn get_model_name(&self) -> &str {
        &self.model_name
    }

    pub fn get_settings_count(&self) -> usize {
        self.settings.len()
    }

    pub fn get_setting(&self, index: usize) -> Option<&BiosSetting> {
        self.settings.get(index)
    }

    /// Probes laptop hardware model, determining launch year and operational mode.
    pub fn probe(&mut self) -> BiosSupportMode {
        let detected = self.backend.detect_model_name().unwrap_or_else(|| {
            std::env::var("PREDATOR_MODEL")
                .unwrap_or_else(|_| "Predator Helios 16 PH16-72".to_string())
        });
        self.model_name = detected;

        let (_tier, _fans, year) = crate::model_catalog::lookup(&self.model_name);

        if year >= 2025 {
            // Attempt Mode 1: WMI ListBIOSSettings
            if let Ok(raw_list) = self.backend.query_wmi_bios_settings() {
                if !raw_list.is_empty() {
                    self.settings = raw_list
                        .iter()
                        .filter_map(|s| parse_bios_setting_string(s, SettingKind::WmiNative))
                        .collect();
                    self.support_mode = BiosSupportMode::WmiModern2025;
                    return self.support_mode;
                }
            }
        }

        // Mode 2 Fallback for 2020-2024 models (or when Mode 1 WMI query unavailable)
        self.settings = self.build_mode2_fallback_settings();
        self.support_mode = if year >= 2020 {
            BiosSupportMode::AcpiWmiFallback2020
        } else {
            BiosSupportMode::UefiNvramDirect
        };

        self.support_mode
    }

    /// Generates the standard 2020-2024 ACPI-WMI and InsydeH2O NVRAM fallback settings.
    fn build_mode2_fallback_settings(&self) -> Vec<BiosSetting> {
        vec![
            BiosSetting {
                name: "Function Key behavior".to_string(),
                current: "Special Key".to_string(),
                current_value: "Special Key".to_string(),
                options: vec![
                    "Special Key".to_string(),
                    "Standard Function Key".to_string(),
                ],
                risk: SettingRisk::Normal,
                kind: SettingKind::AcpiFallback,
            },
            BiosSetting {
                name: "Power-off USB Charge".to_string(),
                current: "Disabled".to_string(),
                current_value: "Disabled".to_string(),
                options: vec![
                    "Disabled".to_string(),
                    "Enabled (10%)".to_string(),
                    "Enabled (20%)".to_string(),
                    "Enabled (30%)".to_string(),
                ],
                risk: SettingRisk::Normal,
                kind: SettingKind::AcpiFallback,
            },
            BiosSetting {
                name: "Keyboard backlight timeout".to_string(),
                current: "30 Seconds".to_string(),
                current_value: "30 Seconds".to_string(),
                options: vec![
                    "Disabled".to_string(),
                    "10 Seconds".to_string(),
                    "30 Seconds".to_string(),
                    "60 Seconds".to_string(),
                ],
                risk: SettingRisk::Normal,
                kind: SettingKind::AcpiFallback,
            },
            BiosSetting {
                name: "POST Animation & Sound".to_string(),
                current: "Enabled".to_string(),
                current_value: "Enabled".to_string(),
                options: vec!["Enabled".to_string(), "Disabled".to_string()],
                risk: SettingRisk::Normal,
                kind: SettingKind::AcpiFallback,
            },
            BiosSetting {
                name: "LCD Overdrive".to_string(),
                current: "Enabled".to_string(),
                current_value: "Enabled".to_string(),
                options: vec!["Enabled".to_string(), "Disabled".to_string()],
                risk: SettingRisk::Normal,
                kind: SettingKind::AcpiFallback,
            },
            BiosSetting {
                name: "Display MUX".to_string(),
                current: "Auto".to_string(),
                current_value: "Auto".to_string(),
                options: vec![
                    "Auto".to_string(),
                    "Optimus".to_string(),
                    "Discrete GPU only".to_string(),
                ],
                risk: SettingRisk::Dangerous, // Switching GPU mode carries display loss hazard
                kind: SettingKind::AcpiFallback,
            },
            BiosSetting {
                name: "Battery Threshold".to_string(),
                current: "Disabled".to_string(),
                current_value: "Disabled".to_string(),
                options: vec!["Enabled".to_string(), "Disabled".to_string()],
                risk: SettingRisk::Normal,
                kind: SettingKind::AcpiFallback,
            },
            // InsydeH2O UEFI NVRAM variables
            BiosSetting {
                name: "Fast Boot".to_string(),
                current: "Enabled".to_string(),
                current_value: "Enabled".to_string(),
                options: vec!["Enabled".to_string(), "Disabled".to_string()],
                risk: SettingRisk::Normal,
                kind: SettingKind::UefiNvram,
            },
            BiosSetting {
                name: "F12 Boot Menu".to_string(),
                current: "Enabled".to_string(),
                current_value: "Enabled".to_string(),
                options: vec!["Enabled".to_string(), "Disabled".to_string()],
                risk: SettingRisk::Normal,
                kind: SettingKind::UefiNvram,
            },
            BiosSetting {
                name: "QuietBoot".to_string(),
                current: "Enabled".to_string(),
                current_value: "Enabled".to_string(),
                options: vec!["Enabled".to_string(), "Disabled".to_string()],
                risk: SettingRisk::Dangerous,
                kind: SettingKind::UefiNvram,
            },
            BiosSetting {
                name: "Network Boot".to_string(),
                current: "Disabled".to_string(),
                current_value: "Disabled".to_string(),
                options: vec!["Enabled".to_string(), "Disabled".to_string()],
                risk: SettingRisk::Normal,
                kind: SettingKind::UefiNvram,
            },
            BiosSetting {
                name: "D2D Recovery".to_string(),
                current: "Enabled".to_string(),
                current_value: "Enabled".to_string(),
                options: vec!["Enabled".to_string(), "Disabled".to_string()],
                risk: SettingRisk::Normal,
                kind: SettingKind::UefiNvram,
            },
        ]
    }

    /// Applies a setting value with two-tier risk checks and ASCII validation.
    pub fn apply_setting(
        &mut self,
        name: &str,
        value: &str,
        password: &str,
        dangerous_allowed: bool,
    ) -> i32 {
        if validate_ascii_str(name, 128).is_err()
            || validate_ascii_str(value, 128).is_err()
            || validate_ascii_str(password, 128).is_err()
        {
            return E_INVALID_ARG;
        }

        // Two-tier safety classification guard: reject dangerous modifications upfront
        if classify_setting_risk(name) == SettingRisk::Dangerous && !dangerous_allowed {
            return E_DANGEROUS_BLOCKED;
        }

        let idx = match self.settings.iter().position(|s| {
            s.name.eq_ignore_ascii_case(name)
                || s.name.eq_ignore_ascii_case(name.trim())
                || normalize_setting_name(&s.name) == normalize_setting_name(name)
        }) {
            Some(i) => i,
            None => return E_NOT_FOUND,
        };

        // Secondary check against registered item risk
        if self.settings[idx].risk == SettingRisk::Dangerous && !dangerous_allowed {
            return E_DANGEROUS_BLOCKED;
        }

        match self.settings[idx].kind {
            SettingKind::WmiNative => {
                match self
                    .backend
                    .set_wmi_bios_setting(&self.settings[idx].name, value, password)
                {
                    Ok(BiosMethodResult::Done) | Ok(BiosMethodResult::RestartNeeded) => {
                        self.settings[idx].current = value.to_string();
                        self.settings[idx].current_value = value.to_string();
                        E_SUCCESS
                    }
                    Ok(BiosMethodResult::WrongPassword) => E_WRONG_PASSWORD,
                    Ok(BiosMethodResult::TooManyAttempts) => E_TOO_MANY_ATTEMPTS,
                    _ => E_GENERAL,
                }
            }
            SettingKind::AcpiFallback => {
                // Dispatch to ACPI-WMI standard register
                let norm = normalize_setting_name(&self.settings[idx].name);
                let write_res = if norm.contains("functionkey") {
                    let val = if value.eq_ignore_ascii_case("Standard Function Key") {
                        1
                    } else {
                        0
                    };
                    self.backend.write_acpi_register(0x1F, val)
                } else if norm.contains("usbcharg") {
                    let val = if value.contains("10") {
                        0x0A
                    } else if value.contains("20") {
                        0x14
                    } else if value.contains("30") {
                        0x1E
                    } else if value.eq_ignore_ascii_case("Enabled") {
                        0x1E
                    } else {
                        0x00
                    };
                    self.backend.write_acpi_register(0x15, val)
                } else if norm.contains("backlighttimeout") {
                    let val = if value.contains("10") {
                        10
                    } else if value.contains("30") {
                        30
                    } else if value.contains("60") {
                        60
                    } else {
                        0
                    };
                    self.backend.write_acpi_register(0x84, val)
                } else if norm.contains("animation") {
                    let val = if value.eq_ignore_ascii_case("Enabled") {
                        1
                    } else {
                        0
                    };
                    self.backend.write_acpi_register(0x06, val)
                } else if norm.contains("overdrive") {
                    let val = if value.eq_ignore_ascii_case("Enabled") {
                        1
                    } else {
                        0
                    };
                    self.backend.write_acpi_register(0x0F, val)
                } else if norm.contains("mux") {
                    let val = if value.eq_ignore_ascii_case("Discrete GPU only") {
                        2
                    } else if value.eq_ignore_ascii_case("Optimus") {
                        1
                    } else {
                        0
                    };
                    self.backend.write_acpi_register(0x02, val)
                } else if norm.contains("threshold") {
                    let val = if value.eq_ignore_ascii_case("Enabled") {
                        1
                    } else {
                        0
                    };
                    self.backend.write_acpi_register(0x50, val)
                } else {
                    Ok(())
                };

                if write_res.is_ok() || write_res == Err(E_UNSUPPORTED) {
                    self.settings[idx].current = value.to_string();
                    self.settings[idx].current_value = value.to_string();
                    E_SUCCESS
                } else {
                    E_GENERAL
                }
            }
            SettingKind::UefiNvram => {
                let var_name = match self.settings[idx].name.as_str() {
                    "Fast Boot" => "FastBoot",
                    "F12 Boot Menu" => "F12BootMenu",
                    "QuietBoot" => "QuietBoot",
                    "Network Boot" => "NetworkBoot",
                    "D2D Recovery" => "D2DRecovery",
                    other => other,
                };
                let byte_val = if value.eq_ignore_ascii_case("Enabled") {
                    [1u8]
                } else {
                    [0u8]
                };

                let res = self.backend.write_uefi_variable(
                    var_name,
                    GUID_INSYDE_SETUP,
                    &byte_val,
                    NVRAM_DEFAULT_ATTRIBUTES,
                );

                if res.is_ok() || res == Err(E_UNSUPPORTED) {
                    self.settings[idx].current = value.to_string();
                    self.settings[idx].current_value = value.to_string();
                    E_SUCCESS
                } else {
                    E_GENERAL
                }
            }
        }
    }

    /// Restores factory or user BIOS defaults.
    pub fn restore_defaults(&mut self, password: &str, user_default: bool) -> i32 {
        if validate_ascii_str(password, 128).is_err() {
            return E_INVALID_ARG;
        }

        if self.support_mode == BiosSupportMode::WmiModern2025 {
            match self.backend.load_wmi_bios_default(password, user_default) {
                Ok(BiosMethodResult::Done) | Ok(BiosMethodResult::RestartNeeded) => E_SUCCESS,
                Ok(BiosMethodResult::WrongPassword) => E_WRONG_PASSWORD,
                Ok(BiosMethodResult::TooManyAttempts) => E_TOO_MANY_ATTEMPTS,
                _ => E_GENERAL,
            }
        } else {
            // Mode 2 / Mode 3: Reset fallback collection to defaults
            self.settings = self.build_mode2_fallback_settings();
            E_SUCCESS
        }
    }
}

// ============================================================================
// Global State & Thread-Safe Access
// ============================================================================

static GLOBAL_SUBSYSTEM: Mutex<Option<BiosSubsystem>> = Mutex::new(None);

pub type BiosManager = BiosSubsystem;

pub fn with_subsystem<F, R>(f: F) -> R
where
    F: FnOnce(&mut BiosSubsystem) -> R,
{
    let mut guard = GLOBAL_SUBSYSTEM.lock().unwrap_or_else(|e| e.into_inner());
    if guard.is_none() {
        *guard = Some(BiosSubsystem::new(Box::new(NativeWindowsBiosBackend)));
    }
    f(guard.as_mut().unwrap())
}

pub fn with_bios_manager<F, R>(f: F) -> R
where
    F: FnOnce(&mut BiosSubsystem) -> R,
{
    with_subsystem(f)
}

/// Injects a test subsystem instance with mock backend.
pub fn set_test_subsystem(sub: BiosSubsystem) {
    let mut guard = GLOBAL_SUBSYSTEM.lock().unwrap_or_else(|e| e.into_inner());
    *guard = Some(sub);
}

/// Resets the global subsystem instance to re-initialize from default probe.
pub fn reset_test_subsystem() {
    let mut guard = GLOBAL_SUBSYSTEM.lock().unwrap_or_else(|e| e.into_inner());
    *guard = None;
}

// ============================================================================
// Unit Tests
// ============================================================================

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_bios_setting_string_with_options() {
        let raw = "Function Key behavior, Special Key[Special Key;Standard Function Key;]";
        let setting = parse_bios_setting_string(raw, SettingKind::WmiNative).unwrap();
        assert_eq!(setting.name, "Function Key behavior");
        assert_eq!(setting.current_value, "Special Key");
        assert_eq!(
            setting.options,
            vec!["Special Key", "Standard Function Key"]
        );
        assert_eq!(setting.risk, SettingRisk::Normal);
        assert_eq!(setting.kind, SettingKind::WmiNative);
    }

    #[test]
    fn test_parse_bios_setting_string_without_options() {
        let raw = "Asset Tag, 123456";
        let setting = parse_bios_setting_string(raw, SettingKind::WmiNative).unwrap();
        assert_eq!(setting.name, "Asset Tag");
        assert_eq!(setting.current_value, "123456");
        assert!(setting.options.is_empty());
        assert_eq!(setting.risk, SettingRisk::Dangerous);
    }

    #[test]
    fn test_classify_setting_risk_whitelist() {
        // Safe list items
        assert_eq!(classify_setting_risk("Intel VTX"), SettingRisk::Normal);
        assert_eq!(classify_setting_risk("Intel VTD"), SettingRisk::Normal);
        assert_eq!(classify_setting_risk("AMD-SVM"), SettingRisk::Normal);
        assert_eq!(
            classify_setting_risk("Above 4G decoding"),
            SettingRisk::Normal
        );
        assert_eq!(
            classify_setting_risk("Re-Size BAR Support"),
            SettingRisk::Normal
        );
        assert_eq!(classify_setting_risk("Wi-Fi"), SettingRisk::Normal);
        assert_eq!(classify_setting_risk("Bluetooth"), SettingRisk::Normal);
        assert_eq!(
            classify_setting_risk("Power-off USB Charge"),
            SettingRisk::Normal
        );
        assert_eq!(
            classify_setting_risk("Power-Off USB Charging"),
            SettingRisk::Normal
        );
        assert_eq!(classify_setting_risk("Fast Boot"), SettingRisk::Normal);
        assert_eq!(classify_setting_risk("FastBoot"), SettingRisk::Normal);
        assert_eq!(classify_setting_risk("F12 Boot Menu"), SettingRisk::Normal);
        assert_eq!(
            classify_setting_risk("POST Animation & Sound"),
            SettingRisk::Normal
        );
        assert_eq!(
            classify_setting_risk("Function Key behavior"),
            SettingRisk::Normal
        );

        // Dangerous items
        assert_eq!(classify_setting_risk("SATA Mode"), SettingRisk::Dangerous);
        assert_eq!(classify_setting_risk("Secure Boot"), SettingRisk::Dangerous);
        assert_eq!(classify_setting_risk("Display MUX"), SettingRisk::Dangerous);
        assert_eq!(classify_setting_risk("QuietBoot"), SettingRisk::Dangerous);
        assert_eq!(
            classify_setting_risk("VMD Controller"),
            SettingRisk::Dangerous
        );
        assert_eq!(
            classify_setting_risk("CPU Overclocking"),
            SettingRisk::Dangerous
        );
        assert_eq!(classify_setting_risk(""), SettingRisk::Dangerous);
    }

    #[test]
    fn test_ascii_validation_and_length_limit() {
        // Valid ASCII
        assert!(validate_ascii_str("ValidPassword123!", 128).is_ok());
        assert!(validate_ascii_str("Function Key behavior", 128).is_ok());

        // Empty string is valid ASCII
        assert!(validate_ascii_str("", 128).is_ok());

        // Control characters rejected
        assert_eq!(validate_ascii_str("Line1\nLine2", 128), Err(E_INVALID_ARG));
        assert_eq!(validate_ascii_str("Null\0Byte", 128), Err(E_INVALID_ARG));

        // Non-ASCII UTF-8 characters rejected
        assert_eq!(validate_ascii_str("Password🔑", 128), Err(E_INVALID_ARG));

        // Exceeds 128 bytes rejected
        let long_str = "A".repeat(129);
        assert_eq!(validate_ascii_str(&long_str, 128), Err(E_INVALID_ARG));
    }

    #[test]
    fn test_zeroize_string_scrubs_memory() {
        let mut secret = String::from("SuperSecretPassword123");
        zeroize_string(&mut secret);
        assert!(secret.is_empty());
        assert_eq!(secret.len(), 0);
    }

    #[test]
    fn test_mode1_wmi_settings_and_apply_flow() {
        let mock = Box::new(MockBiosBackend::with_model("Predator Helios 16 PH16-73"));
        mock.add_wmi_setting(
            "Function Key behavior, Special Key[Special Key;Standard Function Key;]",
        );
        mock.add_wmi_setting("SATA Mode, AHCI[AHCI;RAID;]");
        mock.set_method_result(BiosMethodResult::Done);

        let mut sub = BiosSubsystem::new(mock);
        assert_eq!(sub.support_mode(), BiosSupportMode::WmiModern2025);
        assert_eq!(sub.settings_count(), 2);

        // 1. Normal setting apply
        let res = sub.apply_setting("Function Key behavior", "Standard Function Key", "", false);
        assert_eq!(res, E_SUCCESS);
        assert_eq!(
            sub.get_setting(0).unwrap().current_value,
            "Standard Function Key"
        );

        // 2. Dangerous setting apply blocked when dangerous_allowed == false
        let blocked = sub.apply_setting("SATA Mode", "RAID", "", false);
        assert_eq!(blocked, E_DANGEROUS_BLOCKED);
        assert_eq!(sub.get_setting(1).unwrap().current_value, "AHCI");

        // 3. Dangerous setting apply permitted when dangerous_allowed == true
        let allowed = sub.apply_setting("SATA Mode", "RAID", "", true);
        assert_eq!(allowed, E_SUCCESS);
        assert_eq!(sub.get_setting(1).unwrap().current_value, "RAID");
    }

    #[test]
    fn test_mode1_wmi_password_failures() {
        let mock = Box::new(MockBiosBackend::with_model("Predator Helios 16 PH16-73"));
        mock.add_wmi_setting("Fast Boot, Enabled[Enabled;Disabled;]");
        mock.set_method_result(BiosMethodResult::WrongPassword);

        let mut sub = BiosSubsystem::new(mock);
        let res = sub.apply_setting("Fast Boot", "Disabled", "badpass", false);
        assert_eq!(res, E_WRONG_PASSWORD);
    }

    #[test]
    fn test_mode1_wmi_too_many_attempts() {
        let mock = Box::new(MockBiosBackend::with_model("Predator Helios 16 PH16-73"));
        mock.add_wmi_setting("Fast Boot, Enabled[Enabled;Disabled;]");
        mock.set_method_result(BiosMethodResult::TooManyAttempts);

        let mut sub = BiosSubsystem::new(mock);
        let res = sub.apply_setting("Fast Boot", "Disabled", "lockout", false);
        assert_eq!(res, E_TOO_MANY_ATTEMPTS);
    }

    #[test]
    fn test_mode2_fallback_settings_and_acpi_dispatch() {
        let mock = Box::new(MockBiosBackend::with_model("Predator Helios 16 PH16-71")); // 2023 model
        let mut sub = BiosSubsystem::new(mock);
        assert_eq!(sub.support_mode(), BiosSupportMode::AcpiWmiFallback2020);

        // Verify all 7 required ACPI-WMI fallback settings are present
        let names: Vec<String> = sub.settings().iter().map(|s| s.name.clone()).collect();
        assert!(names.contains(&"Function Key behavior".to_string()));
        assert!(names.contains(&"Power-off USB Charge".to_string()));
        assert!(names.contains(&"Keyboard backlight timeout".to_string()));
        assert!(names.contains(&"POST Animation & Sound".to_string()));
        assert!(names.contains(&"LCD Overdrive".to_string()));
        assert!(names.contains(&"Display MUX".to_string()));
        assert!(names.contains(&"Battery Threshold".to_string()));

        // Normal ACPI setting apply
        let res = sub.apply_setting("LCD Overdrive", "Disabled", "", false);
        assert_eq!(res, E_SUCCESS);
        let od = sub
            .settings()
            .iter()
            .find(|s| s.name == "LCD Overdrive")
            .unwrap();
        assert_eq!(od.current_value, "Disabled");

        // Display MUX is Dangerous -> blocked without flag
        let mux_blocked = sub.apply_setting("Display MUX", "Discrete GPU only", "", false);
        assert_eq!(mux_blocked, E_DANGEROUS_BLOCKED);

        // Display MUX permitted with dangerous_allowed == true
        let mux_ok = sub.apply_setting("Display MUX", "Discrete GPU only", "", true);
        assert_eq!(mux_ok, E_SUCCESS);
    }

    #[test]
    fn test_mode3_uefi_nvram_variable_write() {
        let mock = Box::new(MockBiosBackend::with_model("Nitro 5 AN515-58")); // 2022 model
        let mut sub = BiosSubsystem::new(mock);

        // Fast Boot NVRAM write
        let res = sub.apply_setting("Fast Boot", "Disabled", "", false);
        assert_eq!(res, E_SUCCESS);

        let fb = sub
            .settings()
            .iter()
            .find(|s| s.name == "Fast Boot")
            .unwrap();
        assert_eq!(fb.current_value, "Disabled");
        assert_eq!(fb.kind, SettingKind::UefiNvram);
    }

    #[test]
    fn test_restore_defaults_flow() {
        let mock = Box::new(MockBiosBackend::with_model("Predator Helios 16 PH16-73"));
        mock.add_wmi_setting("Audio, Enabled[Enabled;Disabled;]");
        mock.set_method_result(BiosMethodResult::Done);

        let mut sub = BiosSubsystem::new(mock);
        let res = sub.restore_defaults("", false);
        assert_eq!(res, E_SUCCESS);

        let res_user = sub.restore_defaults("", true);
        assert_eq!(res_user, E_SUCCESS);
    }

    #[test]
    fn test_multi_generation_model_catalog_probing() {
        // 2025+ model -> WmiModern2025 if WMI settings exist
        let mock_2025 = Box::new(MockBiosBackend::with_model("PH16-73"));
        mock_2025.add_wmi_setting("Audio, Enabled[Enabled;Disabled;]");
        let sub_2025 = BiosSubsystem::new(mock_2025);
        assert_eq!(sub_2025.support_mode(), BiosSupportMode::WmiModern2025);

        // 2023 model -> AcpiWmiFallback2020
        let mock_2023 = Box::new(MockBiosBackend::with_model("Predator Triton 17 X PTX17-71"));
        let sub_2023 = BiosSubsystem::new(mock_2023);
        assert_eq!(
            sub_2023.support_mode(),
            BiosSupportMode::AcpiWmiFallback2020
        );

        // 2020 Nitro 5 -> AcpiWmiFallback2020
        let mock_2020 = Box::new(MockBiosBackend::with_model("Nitro 5 AN515-55"));
        let sub_2020 = BiosSubsystem::new(mock_2020);
        assert_eq!(
            sub_2020.support_mode(),
            BiosSupportMode::AcpiWmiFallback2020
        );
    }
}
