//! Parser for Acer's SMBIOS gaming (type 172) and hotkey (type 170) records.

#[derive(Debug, PartialEq, Eq)]
struct Parsed {
    version: Option<(u8, u8)>,
    records: Vec<u32>,
}

fn parse(table: &[u8]) -> Parsed {
    let mut result = Parsed {
        version: None,
        records: Vec::new(),
    };
    let mut i = 0usize;
    while table.len().saturating_sub(i) >= 4 {
        let kind = table[i];
        let length = table[i + 1] as usize;
        if length < 4 || length > table.len() - i {
            break;
        }
        let data = &table[i..i + length];
        if kind == 172 && length >= 6 {
            result.version = Some((data[4], data[5]));
            for chunk in data[6..].chunks_exact(3) {
                result.records.push(
                    u32::from(chunk[0])
                        | (u32::from(u16::from_le_bytes([chunk[1], chunk[2]])) << 8),
                );
            }
        } else if kind == 170 && length >= 14 {
            for chunk in data[14..].chunks_exact(4) {
                if chunk[1] == 2 {
                    result.records.push(
                        0x0100_0000
                            | u32::from(chunk[0])
                            | (u32::from(u16::from_le_bytes([chunk[2], chunk[3]])) << 8),
                    );
                }
            }
        }
        let mut next = i + length;
        while next + 1 < table.len() && (table[next] != 0 || table[next + 1] != 0) {
            next += 1;
        }
        i = next + 2;
        if kind == 127 {
            break;
        }
    }
    result
}

/// Returns the number of packed records, or -1 on invalid pointers/capacity/panic.
/// A version of -1 means that no gaming version was found.
#[no_mangle]
pub unsafe extern "C" fn predator_smbios_parse(
    input: *const u8,
    len: usize,
    output: *mut u32,
    capacity: usize,
    version: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if len == 0
            || len > isize::MAX as usize
            || capacity > isize::MAX as usize / 4
            || !crate::is_valid_buffer_range(input, len)
            || !crate::is_valid_mut_buffer_range(output, capacity)
            || !crate::is_valid_mut_buffer(version)
        {
            return -1;
        }
        let table = unsafe { std::slice::from_raw_parts(input, len) };
        let parsed = parse(table);
        if parsed.records.len() > capacity || parsed.records.len() > i32::MAX as usize {
            return -1;
        }
        let ver = parsed.version.map_or(-1, |(major, minor)| {
            (i32::from(major) << 8) | i32::from(minor)
        });
        unsafe {
            std::ptr::write_unaligned(version, ver);
        }
        for (i, value) in parsed.records.iter().enumerate() {
            unsafe {
                std::ptr::write_unaligned(output.add(i), *value);
            }
        }
        parsed.records.len() as i32
    })
}

// ============================================================================
// Direct Win32 Kernel RSMB Firmware Table Acquisition
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
}

pub fn fetch_and_parse_smbios() -> Option<(u32, u32, u8, u16)> {
    #[cfg(windows)]
    {
        let rsmb_provider = 0x52534D42; // 'RSMB'
        let size = unsafe { GetSystemFirmwareTable(rsmb_provider, 0, std::ptr::null_mut(), 0) };
        if size < 8 || size > 16 * 1024 * 1024 {
            return None;
        }
        let mut buf = vec![0u8; size as usize];
        let bytes_read = unsafe { GetSystemFirmwareTable(rsmb_provider, 0, buf.as_mut_ptr(), size) };
        if bytes_read < 8 || bytes_read > size {
            return None;
        }

        let declared_len = u32::from_le_bytes([buf[4], buf[5], buf[6], buf[7]]) as usize;
        let len = declared_len.min((bytes_read as usize).saturating_sub(8));
        if len == 0 {
            return None;
        }

        let parsed = parse(&buf[8..8 + len]);
        let gaming_count = parsed.records.iter().filter(|r| (*r & 0x0100_0000) == 0).count() as u32;
        let hotkey_count = parsed.records.iter().filter(|r| (*r & 0x0100_0000) != 0).count() as u32;
        let dgpu_flag = if parsed.records.iter().any(|r| (*r & 0xFF) == 0x09) { 1u8 } else { 0u8 };
        let version = parsed.version.map_or(0u16, |(maj, min)| ((maj as u16) << 8) | (min as u16));
        Some((gaming_count, hotkey_count, dgpu_flag, version))
    }
    #[cfg(not(windows))]
    {
        None
    }
}

#[no_mangle]
pub unsafe extern "C" fn predator_smbios_fetch_and_parse(
    out_gaming: *mut u32,
    out_hotkey: *mut u32,
    out_dgpu: *mut u8,
    out_version: *mut u16,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !out_gaming.is_null() && !crate::is_valid_mut_ptr(out_gaming) {
            return -1;
        }
        if !out_hotkey.is_null() && !crate::is_valid_mut_ptr(out_hotkey) {
            return -1;
        }
        if !out_dgpu.is_null() && !crate::is_valid_mut_ptr(out_dgpu) {
            return -1;
        }
        if !out_version.is_null() && !crate::is_valid_mut_ptr(out_version) {
            return -1;
        }

        if let Some((gaming, hotkeys, dgpu, ver)) = fetch_and_parse_smbios() {
            unsafe {
                if !out_gaming.is_null() {
                    std::ptr::write_unaligned(out_gaming, gaming);
                }
                if !out_hotkey.is_null() {
                    std::ptr::write_unaligned(out_hotkey, hotkeys);
                }
                if !out_dgpu.is_null() {
                    std::ptr::write_unaligned(out_dgpu, dgpu);
                }
                if !out_version.is_null() {
                    std::ptr::write_unaligned(out_version, ver);
                }
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
    fn parses_gaming_and_hotkeys_and_stops_at_end_marker() {
        let mut table = vec![172, 9, 0, 0, 4, 12, 7, 0x34, 0x12, 0, 0];
        table.extend_from_slice(&[
            170, 22, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 2, 0x78, 0x56, 6, 1, 0, 0, 0, 0,
        ]);
        table.extend_from_slice(&[127, 4, 0, 0, 0, 0, 172, 6, 0, 0, 9, 9, 0, 0]);
        assert_eq!(
            parse(&table),
            Parsed {
                version: Some((4, 12)),
                records: vec![0x123407, 0x01567805]
            }
        );
    }

    #[test]
    fn handles_truncation_and_last_version() {
        let table = [
            172, 6, 0, 0, 1, 2, 0, 0, 172, 6, 0, 0, 3, 4, 0, 0, 170, 255, 0, 0,
        ];
        for end in 0..=table.len() {
            let _ = parse(&table[..end]);
        }
        assert_eq!(parse(&table).version, Some((3, 4)));
        assert!(parse(&[172, 3, 0, 0]).records.is_empty());
    }

    #[test]
    fn ffi_rejects_undersized_buffer_without_partial_write() {
        let table = [172, 9, 0, 0, 1, 2, 7, 0x34, 0x12, 0, 0];
        let mut output = 0xdeadbeef;
        let mut version = 99;
        let result = unsafe {
            predator_smbios_parse(table.as_ptr(), table.len(), &mut output, 0, &mut version)
        };
        assert_eq!(result, -1);
        assert_eq!(output, 0xdeadbeef);
        assert_eq!(version, 99);
    }
}
