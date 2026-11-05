//! Zero-copy ACPI / AML parser.
//!
//! Ported from `BiosUefiPowerScanner`: firmware table enumeration over the raw
//! `GetSystemFirmwareTable('RSMB')` blob, plus the AML bytecode walker that finds
//! the `PPCC` (Package Power Control Capabilities) package and extracts the
//! platform power-limit boundaries.
//!
//! Everything operates on borrowed `&[u8]` slices — no intermediate `byte[]`
//! copies, no per-element boxing, no LINQ allocations. All offsets are bounds
//! checked, so a hostile or truncated firmware table cannot panic or over-read.

/// DSDT/SSDT AML opcodes used by the PPCC walker.
pub const AML_ZERO_OP: u8 = 0x00;
pub const AML_ONE_OP: u8 = 0x01;
pub const AML_ONES_OP: u8 = 0xFF;
pub const AML_BYTE_PREFIX: u8 = 0x0A;
pub const AML_WORD_PREFIX: u8 = 0x0B;
pub const AML_DWORD_PREFIX: u8 = 0x0C;
pub const AML_QWORD_PREFIX: u8 = 0x0E;
pub const AML_PACKAGE_OP: u8 = 0x12;
pub const AML_VAR_PACKAGE_OP: u8 = 0x13;

const PPCC_PATTERN: [u8; 4] = [b'P', b'P', b'C', b'C'];
/// Heuristic fallback search window after the `PPCC` NameString.
const HEURISTIC_WINDOW: usize = 256;
/// Plausible milliwatt range for a laptop power limit (5 W .. 350 W).
const MIN_MW: i64 = 5_000;
const MAX_MW: i64 = 350_000;

/// One parsed `PPCC` power-limit element.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PpccEntry {
    pub index: i32,
    pub min_mw: i32,
    pub max_mw: i32,
    pub min_time_window_ms: i32,
    pub max_time_window_ms: i32,
    pub step_mw: i32,
}

/// Synthesized platform boundaries, matching the managed `ParsePpccFromAml` tuple.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PpccSummary {
    pub min_power_w: i32,
    pub max_pl1_w: i32,
    pub max_pl2_w: i32,
    pub base_tdp_w: i32,
}

/// One ACPI firmware table header found in an RSMB blob.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct AcpiTable {
    pub signature: [u8; 4],
    pub oem_table_id: [u8; 8],
    pub length: u32,
    pub revision: u8,
    /// Byte offset of the header inside the scanned blob.
    pub offset: u32,
}

impl AcpiTable {
    pub fn signature_str(&self) -> String {
        String::from_utf8_lossy(&self.signature).trim_end().to_string()
    }

    pub fn oem_table_id_str(&self) -> String {
        String::from_utf8_lossy(&self.oem_table_id)
            .trim_end_matches('\0')
            .trim()
            .to_string()
    }
}

/// Reads an AML PkgLength, advancing `pos` past it. `None` when truncated/invalid.
pub fn read_pkg_length(aml: &[u8], pos: &mut usize) -> Option<usize> {
    if *pos >= aml.len() {
        return None;
    }
    let lead = aml[*pos];
    *pos += 1;
    let byte_count = (lead >> 6) & 0x03;

    if byte_count == 0 {
        return Some((lead & 0x3F) as usize);
    }
    if *pos + byte_count as usize > aml.len() {
        *pos = aml.len();
        return None;
    }
    match byte_count {
        1 => {
            let val = (lead & 0x0F) as usize | ((aml[*pos] as usize) << 4);
            *pos += 1;
            if val >= 2 { Some(val) } else { None }
        }
        2 => {
            let b1 = aml[*pos] as usize;
            let b2 = aml[*pos + 1] as usize;
            *pos += 2;
            let val = (lead & 0x0F) as usize | (b1 << 4) | (b2 << 12);
            if val >= 3 { Some(val) } else { None }
        }
        _ => {
            let b1 = aml[*pos] as usize;
            let b2 = aml[*pos + 1] as usize;
            let b3 = aml[*pos + 2] as usize;
            *pos += 3;
            let val = ((lead & 0x0F) as usize | (b1 << 4) | (b2 << 12) | ((b3 & 0x0F) << 20)) & 0x0FFF_FFFF;
            if val >= 4 { Some(val) } else { None }
        }
    }
}

/// Reads one AML integer object, advancing `pos`. `limit` bounds the read window.
pub fn read_aml_integer(aml: &[u8], pos: &mut usize, limit: usize) -> i64 {
    let max_limit = if limit <= aml.len() { limit } else { aml.len() };
    if *pos >= max_limit {
        return 0;
    }
    let op = aml[*pos];
    *pos += 1;

    match op {
        AML_ZERO_OP => 0,
        AML_ONE_OP => 1,
        AML_ONES_OP => -1,
        AML_BYTE_PREFIX => {
            if *pos < max_limit {
                let v = aml[*pos] as i64;
                *pos += 1;
                v
            } else {
                0
            }
        }
        AML_WORD_PREFIX => {
            if *pos + 2 <= max_limit {
                let v = u16::from_le_bytes([aml[*pos], aml[*pos + 1]]) as i64;
                *pos += 2;
                v
            } else {
                *pos = max_limit;
                0
            }
        }
        AML_DWORD_PREFIX => {
            if *pos + 4 <= max_limit {
                let v = u32::from_le_bytes([aml[*pos], aml[*pos + 1], aml[*pos + 2], aml[*pos + 3]]) as i64;
                *pos += 4;
                v
            } else {
                *pos = max_limit;
                0
            }
        }
        AML_QWORD_PREFIX => {
            if *pos + 8 <= max_limit {
                let mut bytes = [0u8; 8];
                bytes.copy_from_slice(&aml[*pos..*pos + 8]);
                *pos += 8;
                (u64::from_le_bytes(bytes) & 0x7FFF_FFFF_FFFF_FFFF) as i64
            } else {
                *pos = max_limit;
                0
            }
        }
        _ => op as i64,
    }
}

/// Scans a byte span for AML integer objects (the managed `ExtractAmlIntegers`).
pub fn extract_aml_integers(aml: &[u8]) -> Vec<i64> {
    let mut out = Vec::new();
    let mut i = 0;
    while i < aml.len() {
        let op = aml[i];
        i += 1;
        match op {
            AML_ZERO_OP => out.push(0),
            AML_ONE_OP => out.push(1),
            AML_ONES_OP => out.push(-1),
            AML_BYTE_PREFIX => {
                if i < aml.len() {
                    out.push(aml[i] as i64);
                    i += 1;
                }
            }
            AML_WORD_PREFIX => {
                if i + 1 < aml.len() {
                    out.push(u16::from_le_bytes([aml[i], aml[i + 1]]) as i64);
                    i += 2;
                } else {
                    break;
                }
            }
            AML_DWORD_PREFIX => {
                if i + 3 < aml.len() {
                    out.push(u32::from_le_bytes([aml[i], aml[i + 1], aml[i + 2], aml[i + 3]]) as i64);
                    i += 4;
                } else {
                    break;
                }
            }
            AML_QWORD_PREFIX => {
                if i + 7 < aml.len() {
                    let mut b = [0u8; 8];
                    b.copy_from_slice(&aml[i..i + 8]);
                    i += 8;
                    out.push((u64::from_le_bytes(b) & 0x7FFF_FFFF_FFFF_FFFF) as i64);
                } else {
                    break;
                }
            }
            _ => {}
        }
    }
    out
}

/// Structured `PPCC` package parse, matching `TryParseStructuredPpcc`.
fn try_parse_structured(aml: &[u8], pos: &mut usize) -> Vec<PpccEntry> {
    let mut list = Vec::new();

    let max_scan = (aml.len()).min(*pos + 16);
    while *pos < max_scan && aml[*pos] != AML_PACKAGE_OP && aml[*pos] != AML_VAR_PACKAGE_OP {
        *pos += 1;
    }
    if *pos >= aml.len() || (aml[*pos] != AML_PACKAGE_OP && aml[*pos] != AML_VAR_PACKAGE_OP) {
        return list;
    }

    *pos += 1; // past PackageOp
    let pkg_start = *pos;
    let pkg_length = match read_pkg_length(aml, pos) {
        Some(v) if v > 0 && v <= aml.len() - pkg_start => v,
        _ => return list,
    };
    let pkg_end = aml.len().min(pkg_start + pkg_length);
    if *pos >= pkg_end {
        return list;
    }

    let num_elements = aml[*pos];
    *pos += 1;

    for elem in 0..num_elements {
        if *pos >= pkg_end {
            break;
        }
        let elem_start = *pos;

        // Element 0 is the revision integer.
        if elem == 0 && aml[*pos] != AML_PACKAGE_OP && aml[*pos] != AML_VAR_PACKAGE_OP {
            let _ = read_aml_integer(aml, pos, pkg_end);
            continue;
        }

        if aml[*pos] == AML_PACKAGE_OP || aml[*pos] == AML_VAR_PACKAGE_OP {
            *pos += 1;
            let inner_start = *pos;
            let inner_length = match read_pkg_length(aml, pos) {
                Some(v) if v > 0 && v <= pkg_end - inner_start => v,
                _ => break,
            };
            let inner_end = pkg_end.min(inner_start + inner_length);
            if *pos >= inner_end {
                break;
            }
            let inner_count = aml[*pos];
            *pos += 1;

            let mut inner = Vec::with_capacity(inner_count as usize);
            for _ in 0..inner_count {
                if *pos >= inner_end {
                    break;
                }
                inner.push(read_aml_integer(aml, pos, inner_end));
            }

            if inner.len() >= 3 {
                let raw_index = inner[0];
                let raw_min = inner[1];
                let raw_max = inner[2];
                if (0..=10).contains(&raw_index)
                    && (0..=500_000).contains(&raw_min)
                    && raw_max >= raw_min
                    && (1_000..=500_000).contains(&raw_max)
                {
                    let min_tw = if inner.len() > 3 { inner[3].clamp(1, 1_000_000) as i32 } else { 1000 };
                    let max_tw = if inner.len() > 4 { inner[4].clamp(1, 1_000_000) as i32 } else { 32000 };
                    let step = if inner.len() > 5 { inner[5].clamp(1, 50_000) as i32 } else { 500 };
                    list.push(PpccEntry {
                        index: raw_index as i32,
                        min_mw: raw_min as i32,
                        max_mw: raw_max as i32,
                        min_time_window_ms: min_tw,
                        max_time_window_ms: max_tw,
                        step_mw: step,
                    });
                }
            }

            if *pos < inner_end {
                *pos = inner_end;
            }
        } else {
            let _ = read_aml_integer(aml, pos, pkg_end);
            if *pos == elem_start && *pos < pkg_end {
                *pos += 1;
            }
        }
    }

    list
}

/// Extracts all `PPCC` entries from an AML stream (structured first, heuristic fallback).
pub fn extract_ppcc_entries(aml: &[u8]) -> Vec<PpccEntry> {
    let mut results = Vec::new();
    if aml.len() < 4 {
        return results;
    }

    let mut index = 0;
    while index + 4 <= aml.len() {
        if aml[index..index + 4] == PPCC_PATTERN {
            let mut pos = index + 4;
            let structured = try_parse_structured(aml, &mut pos);
            if !structured.is_empty() {
                return structured;
            }

            let search_start = index + 4;
            let search_end = aml.len().min(search_start + HEURISTIC_WINDOW);
            let numbers = extract_aml_integers(&aml[search_start..search_end]);
            let mut mw: Vec<i64> = numbers
                .into_iter()
                .filter(|n| (MIN_MW..=MAX_MW).contains(n))
                .collect();

            if mw.len() >= 2 {
                mw.sort_unstable();
                let min_mw = mw[0];
                let max_mw = *mw.last().unwrap();
                let pl1_max = if mw.len() >= 2 {
                    mw[mw.len() - 2]
                } else {
                    max_mw
                };

                results.push(PpccEntry {
                    index: 0,
                    min_mw: min_mw as i32,
                    max_mw: pl1_max as i32,
                    min_time_window_ms: 1000,
                    max_time_window_ms: 32000,
                    step_mw: 500,
                });
                results.push(PpccEntry {
                    index: 1,
                    min_mw: min_mw as i32,
                    max_mw: max_mw as i32,
                    min_time_window_ms: 28,
                    max_time_window_ms: 32,
                    step_mw: 500,
                });
                return results;
            }
        }
        index += 1;
    }

    results
}

/// Synthesized boundaries, matching the managed `ParsePpccFromAml` fallbacks.
pub fn parse_ppcc_summary(aml: &[u8]) -> Option<PpccSummary> {
    if aml.len() < 4 {
        return None;
    }

    let entries = extract_ppcc_entries(aml);
    if entries.len() >= 2 {
        let pl1 = entries.iter().find(|e| e.index == 0);
        let pl2 = entries.iter().find(|e| e.index == 1);
        if let (Some(pl1), Some(pl2)) = (pl1, pl2) {
            if entries.iter().any(|e| e.max_mw > 0) {
                let min_w = (pl1.min_mw as f64 / 1000.0).round() as i32;
                let mut max_pl1_w = (pl1.max_mw as f64 / 1000.0).round() as i32;
                let mut max_pl2_w = (pl2.max_mw as f64 / 1000.0).round() as i32;
                if max_pl2_w < max_pl1_w {
                    max_pl2_w = max_pl1_w;
                }
                if max_pl1_w < min_w {
                    max_pl1_w = min_w;
                }
                return Some(PpccSummary {
                    min_power_w: min_w,
                    max_pl1_w,
                    max_pl2_w,
                    base_tdp_w: max_pl1_w,
                });
            }
        }
    }

    // Flat PPCC stream fallback: (Min, TDP, MaxPL1, MaxPL2) in milliwatts.
    let mut index = 0;
    while index + 4 <= aml.len() {
        if aml[index..index + 4] == PPCC_PATTERN {
            let search_start = index + 4;
            let search_end = aml.len().min(search_start + HEURISTIC_WINDOW);
            let numbers = extract_aml_integers(&aml[search_start..search_end]);
            let mw: Vec<i64> = numbers
                .into_iter()
                .filter(|n| (MIN_MW..=MAX_MW).contains(n))
                .collect();

            let round_w = |v: i64| (v as f64 / 1000.0).round() as i32;

            if mw.len() >= 4 {
                let mut min_w = round_w(mw[0]);
                let mut tdp_w = round_w(mw[1]);
                let mut max_pl1_w = round_w(mw[2]);
                let mut max_pl2_w = round_w(mw[3]);
                if max_pl2_w < max_pl1_w {
                    max_pl2_w = max_pl1_w;
                }
                if max_pl1_w < min_w {
                    max_pl1_w = min_w;
                }
                if tdp_w < min_w {
                    tdp_w = min_w;
                }
                min_w = min_w.max(0);
                return Some(PpccSummary {
                    min_power_w: min_w,
                    max_pl1_w,
                    max_pl2_w,
                    base_tdp_w: tdp_w,
                });
            } else if mw.len() >= 2 {
                let mut sorted = mw.clone();
                sorted.sort_unstable();
                let min_w = round_w(sorted[0]);
                let max_pl2_w = round_w(*sorted.last().unwrap());
                let max_pl1_w = if sorted.len() >= 3 {
                    round_w(sorted[sorted.len() - 2])
                } else {
                    max_pl2_w
                };
                return Some(PpccSummary {
                    min_power_w: min_w,
                    max_pl1_w,
                    max_pl2_w,
                    base_tdp_w: min_w,
                });
            }
        }
        index += 1;
    }

    None
}

/// Walks an RSMB firmware-table blob and validates every ACPI table header.
pub fn scan_acpi_tables(blob: &[u8]) -> Vec<AcpiTable> {
    let mut tables = Vec::new();
    let mut offset = 0;
    while offset + 36 <= blob.len() {
        let header = &blob[offset..offset + 36];
        let signature = [header[0], header[1], header[2], header[3]];
        // All four signature bytes must be printable ASCII.
        if !signature.iter().all(|b| b.is_ascii_graphic()) {
            offset += 4;
            continue;
        }
        let mut length_bytes = [0u8; 4];
        length_bytes.copy_from_slice(&header[4..8]);
        let length = u32::from_le_bytes(length_bytes);
        if length < 36 || offset + length as usize > blob.len() {
            offset += 4;
            continue;
        }
        // ACPI checksum: the whole table must sum to 0 mod 256.
        let sum = blob[offset..offset + length as usize]
            .iter()
            .fold(0u8, |acc, b| acc.wrapping_add(*b));
        if sum != 0 {
            offset += 4;
            continue;
        }

        let mut oem = [0u8; 8];
        oem.copy_from_slice(&header[16..24]);
        tables.push(AcpiTable {
            signature,
            oem_table_id: oem,
            length,
            revision: header[8],
            offset: offset as u32,
        });
        offset += length as usize;
    }
    tables
}

// ============================================================================
// Direct Win32 Kernel Firmware Table Acquisition
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
    fn EnumSystemFirmwareTables(
        provider: u32,
        buffer: *mut u8,
        buffer_size: u32,
    ) -> u32;
}

pub fn fetch_and_parse_ppcc() -> Option<PpccSummary> {
    #[cfg(windows)]
    {
        // 1. Try RSMB table (Raw SMBIOS firmware table)
        let rsmb_provider = 0x52534D42; // 'RSMB'
        let size = unsafe { GetSystemFirmwareTable(rsmb_provider, 0, std::ptr::null_mut(), 0) };
        if size >= 8 && size <= 16 * 1024 * 1024 {
            let mut buf = vec![0u8; size as usize];
            let read = unsafe { GetSystemFirmwareTable(rsmb_provider, 0, buf.as_mut_ptr(), size) };
            if read >= 8 && read <= size {
                if let Some(summary) = parse_ppcc_summary(&buf[8..read as usize]) {
                    return Some(summary);
                }
            }
        }

        // 2. Try ACPI provider
        let acpi_provider = 0x41435049; // 'ACPI'
        let enum_size = unsafe { EnumSystemFirmwareTables(acpi_provider, std::ptr::null_mut(), 0) };
        if enum_size >= 4 && enum_size <= 1024 * 1024 {
            let mut id_buf = vec![0u8; enum_size as usize];
            let read_ids = unsafe { EnumSystemFirmwareTables(acpi_provider, id_buf.as_mut_ptr(), enum_size) };
            if read_ids >= 4 {
                let table_count = (read_ids / 4) as usize;
                for i in 0..table_count {
                    let table_id = u32::from_le_bytes([
                        id_buf[i * 4],
                        id_buf[i * 4 + 1],
                        id_buf[i * 4 + 2],
                        id_buf[i * 4 + 3],
                    ]);
                    let table_size = unsafe { GetSystemFirmwareTable(acpi_provider, table_id, std::ptr::null_mut(), 0) };
                    if table_size >= 36 && table_size <= 16 * 1024 * 1024 {
                        let mut table_buf = vec![0u8; table_size as usize];
                        let table_read = unsafe { GetSystemFirmwareTable(acpi_provider, table_id, table_buf.as_mut_ptr(), table_size) };
                        if table_read >= 36 {
                            if let Some(summary) = parse_ppcc_summary(&table_buf[..table_read as usize]) {
                                return Some(summary);
                            }
                        }
                    }
                }
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
pub unsafe extern "C" fn predator_acpi_fetch_and_parse_ppcc(
    out_summary: *mut PpccSummary,
) -> i32 {
    crate::safe_ffi(-1, || {
        if out_summary.is_null() || !crate::is_valid_mut_ptr(out_summary) {
            return -1;
        }
        if let Some(summary) = fetch_and_parse_ppcc() {
            unsafe {
                std::ptr::write_unaligned(out_summary, summary);
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

    /// Builds an AML package: [op][pkgLen][numElements][content...].
    fn package(op: u8, content: &[u8]) -> Vec<u8> {
        // Length covers the length byte(s) plus the content.
        let mut body = Vec::new();
        body.extend_from_slice(content);
        let total = body.len() + 1;
        assert!(total < 0x40, "test packages stay single-byte length");
        let mut out = vec![op, total as u8];
        out.extend_from_slice(&body);
        out
    }

    fn int_word(v: u16) -> Vec<u8> {
        let mut out = vec![AML_WORD_PREFIX];
        out.extend_from_slice(&v.to_le_bytes());
        out
    }

    fn int_dword(v: u32) -> Vec<u8> {
        let mut out = vec![AML_DWORD_PREFIX];
        out.extend_from_slice(&v.to_le_bytes());
        out
    }

    fn build_ppcc(min_mw: u16, pl1_mw: u32, pl2_mw: u32) -> Vec<u8> {
        let mut inner0 = vec![3u8]; // 3 elements
        inner0.push(AML_ZERO_OP); // index 0
        inner0.extend_from_slice(&int_word(min_mw));
        inner0.extend_from_slice(&int_dword(pl1_mw));
        let inner0 = package(AML_PACKAGE_OP, &inner0);

        let mut inner1 = vec![3u8];
        inner1.push(AML_ONE_OP); // index 1
        inner1.extend_from_slice(&int_word(min_mw));
        inner1.extend_from_slice(&int_dword(pl2_mw));
        let inner1 = package(AML_PACKAGE_OP, &inner1);

        let mut outer = vec![3u8]; // revision + 2 limit packages
        outer.push(AML_BYTE_PREFIX);
        outer.push(2); // revision 2
        outer.extend_from_slice(&inner0);
        outer.extend_from_slice(&inner1);
        let outer = package(AML_PACKAGE_OP, &outer);

        let mut aml = vec![0x08, b'P', b'P', b'C', b'C'];
        aml.extend_from_slice(&outer);
        aml
    }

    #[test]
    fn structured_ppcc_is_parsed() {
        let aml = build_ppcc(15_000, 45_000, 90_000);
        let entries = extract_ppcc_entries(&aml);
        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].index, 0);
        assert_eq!(entries[0].min_mw, 15_000);
        assert_eq!(entries[0].max_mw, 45_000);
        assert_eq!(entries[0].min_time_window_ms, 1000);
        assert_eq!(entries[1].index, 1);
        assert_eq!(entries[1].max_mw, 90_000);
    }

    #[test]
    fn summary_matches_platform_boundaries() {
        let aml = build_ppcc(15_000, 45_000, 90_000);
        let s = parse_ppcc_summary(&aml).expect("summary");
        assert_eq!(s.min_power_w, 15);
        assert_eq!(s.max_pl1_w, 45);
        assert_eq!(s.max_pl2_w, 90);
        assert_eq!(s.base_tdp_w, 45);
    }

    #[test]
    fn pl2_below_pl1_is_promoted() {
        let aml = build_ppcc(10_000, 60_000, 40_000);
        let s = parse_ppcc_summary(&aml).unwrap();
        assert_eq!(s.max_pl1_w, 60);
        assert_eq!(s.max_pl2_w, 60, "PL2 must never be below PL1");
    }

    #[test]
    fn flat_stream_fallback_works_without_packages() {
        // "PPCC" followed by bare little-endian milliwatt integers.
        let mut aml = vec![b'P', b'P', b'C', b'C'];
        for v in [15_000u32, 45_000, 55_000, 90_000] {
            aml.extend_from_slice(&int_dword(v));
        }
        // The heuristic entry builder runs first (exactly like the managed
        // `ParsePpccFromAml`), so PL1 is the second-largest limit and TDP follows PL1.
        let s = parse_ppcc_summary(&aml).expect("flat summary");
        assert_eq!(s.min_power_w, 15);
        assert_eq!(s.max_pl1_w, 55);
        assert_eq!(s.max_pl2_w, 90);
        assert_eq!(s.base_tdp_w, 55);
    }

    #[test]
    fn no_ppcc_yields_none() {
        assert!(parse_ppcc_summary(b"DSDT nothing here").is_none());
        assert!(parse_ppcc_summary(b"PPC").is_none());
        assert!(parse_ppcc_summary(&[]).is_none());
    }

    #[test]
    fn truncated_package_does_not_panic() {
        let aml = build_ppcc(15_000, 45_000, 90_000);
        for cut in 0..aml.len() {
            let _ = extract_ppcc_entries(&aml[..cut]);
            let _ = parse_ppcc_summary(&aml[..cut]);
        }
    }

    #[test]
    fn pkg_length_decoding_handles_all_widths() {
        // 1-byte: 0x1C -> 28
        let mut pos = 0;
        assert_eq!(read_pkg_length(&[0x1C], &mut pos), Some(28));
        // 2-byte: lead 0x40 | 0x0, next 0x01 -> 0x10 = 16
        let mut pos = 0;
        assert_eq!(read_pkg_length(&[0x40, 0x01], &mut pos), Some(16));
        // Truncated multi-byte length
        let mut pos = 0;
        assert_eq!(read_pkg_length(&[0x40], &mut pos), None);
        // Values below the minimum package size are rejected
        let mut pos = 0;
        assert_eq!(read_pkg_length(&[0x41, 0x00], &mut pos), None);
    }

    #[test]
    fn integer_prefixes_decode() {
        let mut pos = 0;
        assert_eq!(read_aml_integer(&[AML_ZERO_OP], &mut pos, 1), 0);
        let mut pos = 0;
        assert_eq!(read_aml_integer(&[AML_ONE_OP], &mut pos, 1), 1);
        let mut pos = 0;
        assert_eq!(read_aml_integer(&[AML_ONES_OP], &mut pos, 1), -1);
        let mut pos = 0;
        assert_eq!(read_aml_integer(&[AML_BYTE_PREFIX, 0x7F], &mut pos, 2), 127);
        let mut pos = 0;
        assert_eq!(read_aml_integer(&[AML_WORD_PREFIX, 0x88, 0x13], &mut pos, 3), 5000);
        let mut pos = 0;
        assert_eq!(
            read_aml_integer(&[AML_DWORD_PREFIX, 0xE8, 0xFD, 0x00, 0x00], &mut pos, 5),
            65_000
        );
    }

    fn acpi_table(sig: &[u8; 4], oem: &[u8; 8], body: &[u8]) -> Vec<u8> {
        let length = 36 + body.len();
        let mut t = Vec::with_capacity(length);
        t.extend_from_slice(sig);
        t.extend_from_slice(&(length as u32).to_le_bytes());
        t.push(2); // revision
        t.push(0); // checksum placeholder
        t.extend_from_slice(b"ACER  ");
        t.extend_from_slice(oem);
        t.extend_from_slice(&1u32.to_le_bytes()); // oem revision
        t.extend_from_slice(b"CRTR"); // creator
        t.extend_from_slice(&1u32.to_le_bytes());
        t.extend_from_slice(body);
        let sum = t.iter().fold(0u8, |acc, b| acc.wrapping_add(*b));
        t[9] = 0u8.wrapping_sub(sum);
        t
    }

    #[test]
    fn acpi_table_scan_finds_dsdt_and_ssdt() {
        let mut blob = Vec::new();
        blob.extend_from_slice(&acpi_table(b"DSDT", b"ACERDSDT", &[0x12, 0x02]));
        blob.extend_from_slice(&acpi_table(b"SSDT", b"ACERSSD1", &[0x10, 0x20, 0x30]));
        // Trailing junk must not be mistaken for a table.
        blob.extend_from_slice(b"\0\0\0\0junkjunk");

        let tables = scan_acpi_tables(&blob);
        assert_eq!(tables.len(), 2);
        assert_eq!(tables[0].signature_str(), "DSDT");
        assert_eq!(tables[0].oem_table_id_str(), "ACERDSDT");
        assert_eq!(tables[0].offset, 0);
        assert_eq!(tables[1].signature_str(), "SSDT");
        assert_eq!(tables[1].offset, tables[0].length as u32);
    }

    #[test]
    fn corrupted_checksum_is_rejected() {
        let mut table = acpi_table(b"FACP", b"ACERFACP", &[1, 2, 3]);
        let last = table.len() - 1;
        table[last] = table[last].wrapping_add(7);
        assert!(scan_acpi_tables(&table).is_empty());
    }

    #[test]
    fn truncated_blob_scan_is_safe() {
        let blob = acpi_table(b"DSDT", b"ACERDSDT", &[0u8; 32]);
        for cut in 0..blob.len().min(60) {
            let _ = scan_acpi_tables(&blob[..cut]);
        }
    }
}
