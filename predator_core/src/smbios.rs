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
        if (input as usize) < 0x10000
            || (output as usize) < 0x10000
            || (version as usize) < 0x10000
            || len == 0
            || len > isize::MAX as usize
            || capacity > isize::MAX as usize / 4
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
