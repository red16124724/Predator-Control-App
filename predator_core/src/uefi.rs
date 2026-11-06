//! Native UEFI NVRAM variable reader and firmware power limit parser.
//!
//! Provides zero-driver access to UEFI environment variables via kernel32
//! `GetFirmwareEnvironmentVariableExW`, along with zero-copy AML and UEFI NVRAM
//! variable parsers for extracting CPU PL1/PL2 power thresholds and overclocking
//! lock bits.

use std::ffi::c_void;

/// Global UEFI variable namespace GUID: {8BE4DF61-93CA-11D2-AA0D-00E098032B8C}
pub const GUID_EFI_GLOBAL: &str = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";

/// Intel DPTF UEFI variable namespace GUID: {A04A27F4-DF00-4D42-B552-39511302113D}
pub const GUID_INTEL_DPTF: &str = "{A04A27F4-DF00-4D42-B552-39511302113D}";

/// Insyde / Acer power limit settings GUID: {EC87D643-EBA4-4BB5-A1E5-3F3E36B20DA9}
pub const GUID_INSYDE_SETUP: &str = "{EC87D643-EBA4-4BB5-A1E5-3F3E36B20DA9}";

/// Initial buffer size for variable querying (4 KiB).
const INITIAL_BUFFER_SIZE: usize = 4096;

/// Extended buffer size on ERROR_INSUFFICIENT_BUFFER (64 KiB).
const LARGE_BUFFER_SIZE: usize = 65536;

/// Windows error code ERROR_INSUFFICIENT_BUFFER (122).
const ERROR_INSUFFICIENT_BUFFER: u32 = 122;

/// SeSystemEnvironmentPrivilege id in ntdll.
const SE_SYSTEM_ENVIRONMENT_PRIVILEGE: u32 = 22;

#[link(name = "kernel32")]
extern "system" {
    fn GetFirmwareEnvironmentVariableExW(
        lp_name: *const u16,
        lp_guid: *const u16,
        p_buffer: *mut c_void,
        n_size: u32,
        pdw_attributes: *mut u32,
    ) -> u32;
    fn GetLastError() -> u32;
}

#[link(name = "ntdll")]
extern "system" {
    fn RtlAdjustPrivilege(privilege: u32, enable: u8, current_thread: u8, enabled: *mut u8) -> i32;
}

/// Synthesized UEFI/AML power configuration and silicon overclocking lock bits.
pub type NativeUefiPowerConfig = UefiPowerConfig;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct UefiPowerConfig {
    pub pl1_milliwatts: u32,
    pub pl2_milliwatts: u32,
    pub pl1_watts: u32,
    pub pl2_watts: u32,
    pub overclocking_lock: bool,
    pub cfg_lock: bool,
}

impl UefiPowerConfig {
    /// Constructs a configuration from raw milliwatt values and lock states,
    /// computing standard rounded watt values.
    pub fn new(
        pl1_milliwatts: u32,
        pl2_milliwatts: u32,
        overclocking_lock: bool,
        cfg_lock: bool,
    ) -> Self {
        let pl1_watts = (pl1_milliwatts as f64 / 1000.0).round() as u32;
        let pl2_watts = (pl2_milliwatts as f64 / 1000.0).round() as u32;
        Self {
            pl1_milliwatts,
            pl2_milliwatts,
            pl1_watts,
            pl2_watts,
            overclocking_lock,
            cfg_lock,
        }
    }
}

/// Helper converting a Rust string slice into a null-terminated UTF-16 wide string.
fn to_wide_null(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Formats a GUID string to ensure standard Windows '{...}' enclosing braces.
fn normalize_guid(guid: &str) -> String {
    let trimmed = guid.trim();
    if trimmed.starts_with('{') && trimmed.ends_with('}') {
        trimmed.to_string()
    } else {
        format!("{{{trimmed}}}")
    }
}

/// Attempts to enable `SeSystemEnvironmentPrivilege` if running in an elevated process.
fn try_enable_env_privilege() {
    let mut prev = 0u8;
    unsafe {
        let _ = RtlAdjustPrivilege(SE_SYSTEM_ENVIRONMENT_PRIVILEGE, 1, 0, &mut prev);
    }
}

/// Reads a UEFI environment variable by name and GUID from kernel32.
///
/// Dynamically scales buffer capacity: tries 4096 bytes initially, and re-queries
/// with 65536 bytes if kernel32 returns `ERROR_INSUFFICIENT_BUFFER` (122).
///
/// Returns `Some(Vec<u8>)` containing the exact payload bytes on success,
/// or `None` if the variable is not found, privilege is lacking, or an error occurs.
pub fn read_variable(name: &str, guid: &str) -> Option<Vec<u8>> {
    if name.is_empty() || guid.is_empty() {
        return None;
    }

    try_enable_env_privilege();

    let name_w = to_wide_null(name);
    let guid_w = to_wide_null(&normalize_guid(guid));
    let mut attr = 0u32;

    let mut buf = vec![0u8; INITIAL_BUFFER_SIZE];

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

        let err = GetLastError();
        if err == ERROR_INSUFFICIENT_BUFFER {
            let mut large_buf = vec![0u8; LARGE_BUFFER_SIZE];
            let large_read = GetFirmwareEnvironmentVariableExW(
                name_w.as_ptr(),
                guid_w.as_ptr(),
                large_buf.as_mut_ptr() as *mut c_void,
                large_buf.len() as u32,
                &mut attr,
            );

            if large_read > 0 {
                large_buf.truncate(large_read as usize);
                return Some(large_buf);
            }
        }
    }

    None
}

/// Scans an ACPI AML byte stream (such as SSDT / DSDT or an extracted AML blob)
/// for PPCC (Package Power Control Capabilities) and lock attributes.
pub fn parse_aml_power_limits(aml: &[u8]) -> Option<UefiPowerConfig> {
    if aml.len() < 4 {
        return None;
    }

    // Pattern match PPCC NameString: b"PPCC"
    let ppcc_pattern = b"PPCC";
    let mut ppcc_pos = None;
    for i in 0..=aml.len().saturating_sub(4) {
        if &aml[i..i + 4] == ppcc_pattern {
            ppcc_pos = Some(i);
            break;
        }
    }

    // Look for lock patterns in the entire AML stream (e.g. OCLK, CFGL, or lock opcode bytes)
    let oc_lock = contains_lock_token(aml, b"OCLK")
        || contains_lock_token(aml, b"LOCK")
        || scan_lock_bits_in_aml(aml);

    let cfg_lock = contains_lock_token(aml, b"CFGL") || contains_lock_token(aml, b"CFGK");

    if let Some(pos) = ppcc_pos {
        let entries = crate::acpi::extract_ppcc_entries(&aml[pos..]);
        if !entries.is_empty() {
            let pl1_entry = entries
                .iter()
                .find(|e| e.index == 0)
                .or_else(|| entries.first());
            let pl2_entry = entries
                .iter()
                .find(|e| e.index == 1)
                .or_else(|| entries.get(1));

            if let Some(pl1) = pl1_entry {
                let pl1_mw = (pl1.max_mw as u32).max(pl1.min_mw as u32);
                let pl2_mw = if let Some(pl2) = pl2_entry {
                    (pl2.max_mw as u32).max(pl1_mw)
                } else {
                    pl1_mw
                };

                return Some(UefiPowerConfig::new(pl1_mw, pl2_mw, oc_lock, cfg_lock));
            }
        }

        // Fallback: search adjacent integers after PPCC NameString
        let search_start = pos + 4;
        let search_end = aml.len().min(search_start + 256);
        let integers = scan_aml_integers(&aml[search_start..search_end]);
        let mw_values: Vec<u32> = integers
            .into_iter()
            .filter(|&v| (5_000..=350_000).contains(&v))
            .collect();

        if mw_values.len() >= 2 {
            let pl1_mw = mw_values[0];
            let pl2_mw = mw_values[1].max(pl1_mw);
            return Some(UefiPowerConfig::new(pl1_mw, pl2_mw, oc_lock, cfg_lock));
        }
    }

    None
}

/// Scans a binary UEFI NVRAM variable payload (such as `PowerLimitSettings`,
/// `DptfSetup`, or custom OEM NVRAM tables) for PL1/PL2 thresholds and lock flags.
pub fn parse_uefi_variable_power(data: &[u8]) -> Option<UefiPowerConfig> {
    if data.len() < 8 {
        return None;
    }

    // 1. Check for standard structured power payload with signature:
    // Header format:
    // [0..4]: Signature 'PWRL' (0x4C525750) or 'DPTF' (0x46545044)
    // [4..8]: PL1 milliwatts (u32 LE)
    // [8..12]: PL2 milliwatts (u32 LE)
    // [12]: Lock flags (Bit 0 = Overclocking Lock, Bit 1 = CFG Lock)
    if data.len() >= 13 {
        let sig = &data[0..4];
        if sig == b"PWRL" || sig == b"DPTF" || sig == b"ACER" {
            let pl1_mw = u32::from_le_bytes(data[4..8].try_into().unwrap());
            let pl2_mw = u32::from_le_bytes(data[8..12].try_into().unwrap());
            let flags = data[12];
            let oc_lock = (flags & 0x01) != 0;
            let cfg_lock = (flags & 0x02) != 0;
            return Some(UefiPowerConfig::new(pl1_mw, pl2_mw, oc_lock, cfg_lock));
        }
    }

    // 2. Check for MSR-style 64-bit register representation (8 bytes)
    // Bit 0..14: PL1 raw power
    // Bit 32..46: PL2 raw power
    // Bit 63: Lock bit (Package Power Limit Lock / Overclocking Lock)
    if data.len() == 8 {
        let raw = u64::from_le_bytes(data[0..8].try_into().unwrap());
        let oc_lock = (raw & (1 << 63)) != 0;
        let pl1_raw = (raw & 0x7FFF) as u32;
        let pl2_raw = ((raw >> 32) & 0x7FFF) as u32;

        // If encoded as 1/8W units (standard Intel RAPL MSR unit 8)
        let pl1_mw = pl1_raw * 125;
        let pl2_mw = pl2_raw * 125;
        if (5_000..=350_000).contains(&pl1_mw) {
            return Some(UefiPowerConfig::new(
                pl1_mw,
                pl2_mw.max(pl1_mw),
                oc_lock,
                false,
            ));
        }
    }

    // 3. Fallback: scan for 32-bit LE integers within plausible milliwatt range (5,000 to 350,000)
    let mut mw_candidates = Vec::new();
    let mut step = 0;
    while step + 4 <= data.len() {
        let val = u32::from_le_bytes(data[step..step + 4].try_into().unwrap());
        if (5_000..=350_000).contains(&val) {
            mw_candidates.push((step, val));
        }
        step += 2;
    }

    if mw_candidates.len() >= 2 {
        let pl1_mw = mw_candidates[0].1;
        let pl2_mw = mw_candidates[1].1.max(pl1_mw);

        // Check lock byte immediately following or in header
        let oc_lock = scan_uefi_lock_flags(data);
        let cfg_lock = (data.iter().any(|&b| b == 0xCF)) && oc_lock;

        return Some(UefiPowerConfig::new(pl1_mw, pl2_mw, oc_lock, cfg_lock));
    }

    None
}

/// Unified AML and UEFI variable power parser scanning extracted byte buffers
/// for PL1 / PL2 values and overclocking lock bits.
pub fn scan_power_and_locks(buffer: &[u8]) -> Option<UefiPowerConfig> {
    if buffer.len() < 4 {
        return None;
    }

    // First try AML parsing if PPCC pattern is present
    if buffer.windows(4).any(|w| w == b"PPCC") {
        if let Some(config) = parse_aml_power_limits(buffer) {
            return Some(config);
        }
    }

    // Next try UEFI variable binary descriptor parsing
    if let Some(config) = parse_uefi_variable_power(buffer) {
        return Some(config);
    }

    // Fallback: heuristic scan over raw bytes
    parse_aml_power_limits(buffer)
}

fn contains_lock_token(data: &[u8], token: &[u8]) -> bool {
    data.windows(token.len()).any(|w| w == token)
}

fn scan_lock_bits_in_aml(aml: &[u8]) -> bool {
    // Check for common AML lock bit patterns: e.g. Store(0x01, OCLK)
    for i in 0..aml.len().saturating_sub(6) {
        if &aml[i..i + 4] == b"OCLK" || &aml[i..i + 4] == b"LOCK" {
            if i + 5 < aml.len() && aml[i + 4] == 0x01 {
                return true;
            }
        }
    }
    false
}

fn scan_uefi_lock_flags(data: &[u8]) -> bool {
    // Scan for lock indicator byte or 64-bit lock bit 63
    if data.len() >= 8 {
        for chunk in data.chunks_exact(8) {
            let val = u64::from_le_bytes(chunk.try_into().unwrap());
            if (val & (1 << 63)) != 0 {
                return true;
            }
        }
    }
    // Check for explicit lock indicator flags byte (0x01 or 0x03)
    data.iter().any(|&b| b == 0x01 || b == 0x03)
}

fn scan_aml_integers(bytes: &[u8]) -> Vec<u32> {
    let mut results = Vec::new();
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            0x0A if i + 1 < bytes.len() => {
                results.push(bytes[i + 1] as u32);
                i += 2;
            }
            0x0B if i + 2 < bytes.len() => {
                results.push(u16::from_le_bytes([bytes[i + 1], bytes[i + 2]]) as u32);
                i += 3;
            }
            0x0C if i + 4 < bytes.len() => {
                let v =
                    u32::from_le_bytes([bytes[i + 1], bytes[i + 2], bytes[i + 3], bytes[i + 4]]);
                results.push(v);
                i += 5;
            }
            _ => i += 1,
        }
    }
    results
}

// ============================================================================
// Direct Win32 Kernel ACPI / UEFI Table Acquisition
// ============================================================================

#[cfg(windows)]
#[link(name = "kernel32")]
extern "system" {
    fn GetSystemFirmwareTable(
        provider: u32,
        table_id: u32,
        buffer: *mut u8,
        buffer_size: u32,
    ) -> u32;
    fn EnumSystemFirmwareTables(provider: u32, buffer: *mut u8, buffer_size: u32) -> u32;
}

pub fn fetch_and_scan_power() -> Option<UefiPowerConfig> {
    #[cfg(windows)]
    {
        // 1. Scan ACPI tables via GetSystemFirmwareTable
        let acpi_provider = 0x41435049; // 'ACPI'
        let enum_size = unsafe { EnumSystemFirmwareTables(acpi_provider, std::ptr::null_mut(), 0) };
        if enum_size >= 4 && enum_size <= 1024 * 1024 {
            let mut id_buf = vec![0u8; enum_size as usize];
            let read_ids =
                unsafe { EnumSystemFirmwareTables(acpi_provider, id_buf.as_mut_ptr(), enum_size) };
            if read_ids >= 4 {
                let table_count = (read_ids / 4) as usize;
                for i in 0..table_count {
                    let table_id = u32::from_le_bytes([
                        id_buf[i * 4],
                        id_buf[i * 4 + 1],
                        id_buf[i * 4 + 2],
                        id_buf[i * 4 + 3],
                    ]);
                    let table_size = unsafe {
                        GetSystemFirmwareTable(acpi_provider, table_id, std::ptr::null_mut(), 0)
                    };
                    if table_size >= 36 && table_size <= 16 * 1024 * 1024 {
                        let mut table_buf = vec![0u8; table_size as usize];
                        let table_read = unsafe {
                            GetSystemFirmwareTable(
                                acpi_provider,
                                table_id,
                                table_buf.as_mut_ptr(),
                                table_size,
                            )
                        };
                        if table_read >= 36 {
                            if let Some(config) =
                                parse_aml_power_limits(&table_buf[..table_read as usize])
                            {
                                return Some(config);
                            }
                        }
                    }
                }
            }
        }

        // 2. Scan UEFI NVRAM variables
        let dptf_var = read_variable("DptfPwr", GUID_INTEL_DPTF);
        if let Some(buf) = dptf_var {
            if let Some(cfg) = parse_aml_power_limits(&buf) {
                return Some(cfg);
            }
        }

        let setup_var = read_variable("Setup", GUID_INSYDE_SETUP);
        if let Some(buf) = setup_var {
            if let Some(cfg) = parse_aml_power_limits(&buf) {
                return Some(cfg);
            }
        }

        None
    }
    #[cfg(not(windows))]
    {
        None
    }
}

#[no_mangle]
pub unsafe extern "C" fn predator_uefi_fetch_and_scan_power(
    out_config: *mut NativeUefiPowerConfig,
) -> i32 {
    crate::safe_ffi(-1, || {
        if out_config.is_null() || !crate::is_valid_mut_ptr(out_config) {
            return -1;
        }
        if let Some(cfg) = fetch_and_scan_power() {
            unsafe {
                std::ptr::write_unaligned(out_config, cfg);
            }
            0
        } else {
            -1
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_structured_uefi_variable_power_payload() {
        let mut buffer = Vec::new();
        buffer.extend_from_slice(b"PWRL"); // Signature
        buffer.extend_from_slice(&45000u32.to_le_bytes()); // PL1 = 45,000 mW (45W)
        buffer.extend_from_slice(&75000u32.to_le_bytes()); // PL2 = 75,000 mW (75W)
        buffer.push(0x03); // Flags: Bit 0 = OC Lock, Bit 1 = CFG Lock

        let config = scan_power_and_locks(&buffer).expect("Should parse structured UEFI payload");
        assert_eq!(config.pl1_milliwatts, 45000);
        assert_eq!(config.pl2_milliwatts, 75000);
        assert_eq!(config.pl1_watts, 45);
        assert_eq!(config.pl2_watts, 75);
        assert!(config.overclocking_lock);
        assert!(config.cfg_lock);
    }

    #[test]
    fn parses_msr_style_64bit_lock_buffer() {
        // MSR 0x610 format with lock bit 63 set
        // PL1 = 45W = 360 units of 1/8W = 0x0168
        // PL2 = 65W = 520 units of 1/8W = 0x0208
        let val: u64 = 0x0168 | ((0x0208 as u64) << 32) | (1u64 << 63);
        let buffer = val.to_le_bytes();

        let config = parse_uefi_variable_power(&buffer).expect("Should parse MSR 64-bit payload");
        assert_eq!(config.pl1_milliwatts, 45000);
        assert_eq!(config.pl2_milliwatts, 65000);
        assert_eq!(config.pl1_watts, 45);
        assert_eq!(config.pl2_watts, 65);
        assert!(config.overclocking_lock);
    }

    fn int_word(v: u16) -> Vec<u8> {
        let mut out = vec![crate::acpi::AML_WORD_PREFIX];
        out.extend_from_slice(&v.to_le_bytes());
        out
    }

    fn int_dword(v: u32) -> Vec<u8> {
        let mut out = vec![crate::acpi::AML_DWORD_PREFIX];
        out.extend_from_slice(&v.to_le_bytes());
        out
    }

    fn make_package(content: &[u8]) -> Vec<u8> {
        let total = content.len() + 1;
        let mut out = vec![crate::acpi::AML_PACKAGE_OP, total as u8];
        out.extend_from_slice(content);
        out
    }

    #[test]
    fn parses_aml_ppcc_buffer_with_locks() {
        let mut inner0 = vec![3u8];
        inner0.push(crate::acpi::AML_ZERO_OP); // index 0
        inner0.extend_from_slice(&int_word(15_000)); // min
        inner0.extend_from_slice(&int_dword(55_000)); // max pl1
        let inner0 = make_package(&inner0);

        let mut inner1 = vec![3u8];
        inner1.push(crate::acpi::AML_ONE_OP); // index 1
        inner1.extend_from_slice(&int_word(15_000)); // min
        inner1.extend_from_slice(&int_dword(95_000)); // max pl2
        let inner1 = make_package(&inner1);

        let mut outer = vec![3u8]; // revision + 2 limit packages
        outer.push(crate::acpi::AML_BYTE_PREFIX);
        outer.push(2); // revision 2
        outer.extend_from_slice(&inner0);
        outer.extend_from_slice(&inner1);
        let outer = make_package(&outer);

        let mut aml = vec![0x08, b'P', b'P', b'C', b'C'];
        aml.extend_from_slice(&outer);
        aml.extend_from_slice(b"OCLK");

        let config = scan_power_and_locks(&aml).expect("Should parse AML PPCC package");
        assert_eq!(config.pl1_milliwatts, 55000);
        assert_eq!(config.pl2_milliwatts, 95000);
        assert_eq!(config.pl1_watts, 55);
        assert_eq!(config.pl2_watts, 95);
        assert!(config.overclocking_lock);
    }

    #[test]
    fn rejects_truncated_or_empty_buffers() {
        assert!(scan_power_and_locks(&[]).is_none());
        assert!(scan_power_and_locks(&[1, 2, 3]).is_none());
        assert!(parse_aml_power_limits(&[0x00, 0x01]).is_none());
        assert!(parse_uefi_variable_power(&[0xFF; 5]).is_none());
    }

    #[test]
    fn test_guid_normalization() {
        assert_eq!(
            normalize_guid("8BE4DF61-93CA-11D2-AA0D-00E098032B8C"),
            "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}"
        );
        assert_eq!(
            normalize_guid("{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}"),
            "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}"
        );
    }

    #[test]
    fn read_variable_handles_missing_var_gracefully() {
        // Querying non-existent variable returns None without crashing or panicking
        let result = read_variable("NonExistentVar999", GUID_EFI_GLOBAL);
        assert!(result.is_none());
    }
}
