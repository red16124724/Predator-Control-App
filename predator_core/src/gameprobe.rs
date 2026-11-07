//! RawInput packet classification, key debounce and media-poll cache policy.
//!
//! Ports the pure decision logic from `RawInputKeyWatcher` (Acer vendor packet
//! signatures, 250 ms debounce) and `MediaPlaybackDetector` (1 s poll cache).
//! Window registration, `GetRawInputData`, and the WASAPI COM session probe
//! stay managed.

pub const RAW_NONE: i32 = 0;
pub const RAW_NITRO_SENSE: i32 = 1;
pub const RAW_MODE_KEY: i32 = 2;

fn byte_at(bytes: &[u8], index: usize) -> u8 {
    bytes.get(index).copied().unwrap_or(0)
}

/// Classifies an Acer vendor HID payload. Handles both 0-indexed payloads and
/// 1-indexed payloads that start with a report ID.
pub fn classify_rawinput(bytes: &[u8]) -> i32 {
    if bytes.len() < 2 {
        return RAW_NONE;
    }

    let nitro = (byte_at(bytes, 0) == 0x81
        && matches!(byte_at(bytes, 1), 0xFF | 0x05 | 0x01))
        || (bytes.len() >= 3
            && byte_at(bytes, 1) == 0x81
            && matches!(byte_at(bytes, 2), 0xFF | 0x05 | 0x01));

    let mode = (byte_at(bytes, 0) == 0x81
        && matches!(byte_at(bytes, 1), 0x82 | 0x86 | 0x87))
        || (bytes.len() >= 3
            && byte_at(bytes, 1) == 0x81
            && matches!(byte_at(bytes, 2), 0x82 | 0x86 | 0x87));

    if nitro {
        RAW_NITRO_SENSE
    } else if mode {
        RAW_MODE_KEY
    } else {
        RAW_NONE
    }
}

/// True when enough time passed since the previous accepted key event.
pub fn should_fire(last_tick: i64, now_tick: i64, threshold_ms: i64) -> bool {
    now_tick.saturating_sub(last_tick) >= threshold_ms
}

/// True when the cached media state is stale and the COM probe should run.
pub fn should_query_media(last_ms: i64, now_ms: i64, cache_ms: i64) -> bool {
    now_ms.saturating_sub(last_ms) >= cache_ms
}

/// First index in a NUL-separated list of process names that matches `target`
/// case-insensitively, or -1.
pub fn select_profile_index(names_blob: &[u8], target: &str) -> i32 {
    if target.is_empty() {
        return -1;
    }
    let target_upper = target.to_ascii_uppercase();
    for (index, raw) in names_blob.split(|b| *b == 0).enumerate() {
        if raw.is_empty() {
            continue;
        }
        if let Ok(name) = std::str::from_utf8(raw) {
            let name = name.trim().trim_matches('"').trim_matches('\'');
            if name.eq_ignore_ascii_case(&target_upper) {
                return index as i32;
            }
        }
    }
    -1
}

// ---------------------------------------------------------------------------
// C-ABI exports
// ---------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn predator_gameprobe_classify_rawinput(bytes: *const u8, len: usize) -> i32 {
    crate::safe_ffi(RAW_NONE, || {
        if len < 2 || !crate::is_valid_buffer(bytes) {
            return RAW_NONE;
        }
        let slice = unsafe { std::slice::from_raw_parts(bytes, len) };
        classify_rawinput(slice)
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_gameprobe_should_fire(last_tick: i64, now_tick: i64, threshold_ms: i64) -> i32 {
    crate::safe_ffi(0, || {
        if should_fire(last_tick, now_tick, threshold_ms) {
            1
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_gameprobe_should_query_media(last_ms: i64, now_ms: i64, cache_ms: i64) -> i32 {
    crate::safe_ffi(1, || {
        if should_query_media(last_ms, now_ms, cache_ms) {
            1
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_gameprobe_select_profile(
    blob: *const u8,
    blob_len: usize,
    target: *const u8,
    target_len: usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        if blob_len == 0 || !crate::is_valid_buffer(blob) || !crate::is_valid_buffer(target) {
            return -1;
        }
        let blob_slice = unsafe { std::slice::from_raw_parts(blob, blob_len) };
        let target_slice = if target_len == 0 {
            &[][..]
        } else {
            unsafe { std::slice::from_raw_parts(target, target_len) }
        };
        match std::str::from_utf8(target_slice) {
            Ok(name) => select_profile_index(blob_slice, name),
            Err(_) => -1,
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn classifies_both_indexing_conventions() {
        assert_eq!(classify_rawinput(&[0x81, 0xFF]), RAW_NITRO_SENSE);
        assert_eq!(classify_rawinput(&[0x81, 0x05]), RAW_NITRO_SENSE);
        assert_eq!(classify_rawinput(&[0x81, 0x01]), RAW_NITRO_SENSE);
        assert_eq!(classify_rawinput(&[0x00, 0x81, 0xFF]), RAW_NITRO_SENSE);

        assert_eq!(classify_rawinput(&[0x81, 0x82]), RAW_MODE_KEY);
        assert_eq!(classify_rawinput(&[0x81, 0x86]), RAW_MODE_KEY);
        assert_eq!(classify_rawinput(&[0x81, 0x87]), RAW_MODE_KEY);
        assert_eq!(classify_rawinput(&[0x07, 0x81, 0x87]), RAW_MODE_KEY);
    }

    #[test]
    fn ignores_unrelated_and_short_payloads() {
        assert_eq!(classify_rawinput(&[]), RAW_NONE);
        assert_eq!(classify_rawinput(&[0x81]), RAW_NONE);
        assert_eq!(classify_rawinput(&[0x81, 0x00]), RAW_NONE);
        assert_eq!(classify_rawinput(&[0x10, 0x20, 0x30]), RAW_NONE);
    }

    #[test]
    fn nitro_takes_precedence_over_mode() {
        assert_eq!(classify_rawinput(&[0x81, 0xFF, 0x86]), RAW_NITRO_SENSE);
    }

    #[test]
    fn debounce_threshold_is_inclusive() {
        assert!(!should_fire(1000, 1249, 250));
        assert!(should_fire(1000, 1250, 250));
        assert!(should_fire(0, 250, 250));
    }

    #[test]
    fn debounce_handles_clock_going_backwards() {
        assert!(!should_fire(5000, 10, 250));
    }

    #[test]
    fn media_cache_expires_at_threshold() {
        assert!(!should_query_media(1000, 1999, 1000));
        assert!(should_query_media(1000, 2000, 1000));
        assert!(should_query_media(i64::MIN, 0, 1000));
    }

    #[test]
    fn profile_selection_matches_nul_separated_names() {
        let blob = b"game.exe\0Other.EXE\0third.exe\0";
        assert_eq!(select_profile_index(blob, "game.exe"), 0);
        assert_eq!(select_profile_index(blob, "other.exe"), 1);
        assert_eq!(select_profile_index(blob, "THIRD.EXE"), 2);
        assert_eq!(select_profile_index(blob, "missing.exe"), -1);
        assert_eq!(select_profile_index(blob, ""), -1);
        assert_eq!(select_profile_index(b"\"quoted.exe\"\0", "quoted.exe"), 0);
    }
}
