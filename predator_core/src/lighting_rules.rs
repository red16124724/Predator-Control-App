//! Pure lighting capability and palette rules used by the managed UI.

pub fn select_tier(
    override_tier: i32,
    per_key: bool,
    three: bool,
    single: bool,
    service: bool,
    wmi: bool,
) -> i32 {
    let available = service || wmi;
    match override_tier {
        2 => 4,
        1 => {
            if available {
                3
            } else {
                0
            }
        }
        3 => {
            if available {
                1
            } else {
                0
            }
        }
        4 => {
            if available {
                2
            } else {
                0
            }
        }
        _ if per_key && service => 4,
        _ if per_key => 3,
        _ if three && available => 2,
        _ if single && available => 1,
        _ if available => 3,
        _ => 0,
    }
}

pub fn parse_hex(bytes: &[u8]) -> Option<u32> {
    let text = std::str::from_utf8(bytes).ok()?;
    let trimmed = text.trim();
    if trimmed.is_empty() || !trimmed.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    let value = match text.len() {
        3 => {
            let mut n = 0u32;
            for c in text.chars() {
                let x = c.to_digit(16)?;
                n = (n << 8) | (x << 4) | x;
            }
            0xff00_0000 | n
        }
        6 => 0xff00_0000 | u32::from_str_radix(text.trim(), 16).ok()?,
        8 => u32::from_str_radix(text.trim(), 16).ok()?,
        _ => return None,
    };
    Some(value)
}

pub fn blend(a: u32, b: u32, amount: f64) -> Option<u32> {
    if !amount.is_finite() {
        return None;
    }
    let t = amount.clamp(0.0, 1.0);
    let mut result = 0u32;
    for shift in [24, 16, 8, 0] {
        let x = ((a >> shift) & 255) as f64;
        let y = ((b >> shift) & 255) as f64;
        result |= ((x + (y - x) * t).round_ties_even() as u32) << shift;
    }
    Some(result)
}

pub fn service_level(value: i32, speed: bool) -> i32 {
    if speed {
        let clamped = value.clamp(1, 100);
        (((clamped as f64 / 100.0) * 5.0).round_ties_even() as i32).clamp(1, 5)
    } else if value <= 0 {
        0
    } else if value >= 100 {
        5
    } else {
        (value + 19) / 20
    }
}

#[no_mangle]
pub extern "C" fn predator_lighting_service_level(value: i32, is_speed: i32) -> i32 {
    crate::safe_ffi(-1, || service_level(value, is_speed != 0))
}

#[no_mangle]
pub extern "C" fn predator_lighting_select_tier(
    override_tier: i32,
    per_key: i32,
    three: i32,
    single: i32,
    service: i32,
    wmi: i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        select_tier(
            override_tier,
            per_key != 0,
            three != 0,
            single != 0,
            service != 0,
            wmi != 0,
        )
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_lighting_parse_hex(
    ptr: *const u8,
    len: usize,
    out: *mut u32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_buffer(ptr) || !crate::is_valid_mut_buffer(out) || len == 0 || len > 8 {
            return 0;
        }
        let bytes = unsafe { std::slice::from_raw_parts(ptr, len) };
        match parse_hex(bytes) {
            Some(value) => {
                unsafe { std::ptr::write_unaligned(out, value) };
                1
            }
            None => 0,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_lighting_blend(
    a: u32,
    b: u32,
    amount: f64,
    out: *mut u32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_buffer(out) {
            return 0;
        }
        match blend(a, b, amount) {
            Some(value) => {
                unsafe { std::ptr::write_unaligned(out, value) };
                1
            }
            None => 0,
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn tier_priority_and_unavailable() {
        assert_eq!(select_tier(2, false, false, false, false, false), 4);
        assert_eq!(select_tier(1, true, true, true, false, false), 0);
        assert_eq!(select_tier(0, true, true, true, false, false), 3);
        assert_eq!(select_tier(0, false, true, true, true, false), 2);
        assert_eq!(select_tier(0, false, false, true, false, true), 1);
        assert_eq!(select_tier(0, false, false, false, false, false), 0);
    }
    #[test]
    fn rgb_and_argb_roundtrip() {
        assert_eq!(parse_hex(b"abc"), Some(0xffaa_bbcc));
        assert_eq!(parse_hex(b"ff0099"), Some(0xffff_0099));
        assert_eq!(parse_hex(b"80112233"), Some(0x8011_2233));
        assert_eq!(parse_hex(b"zzzzzz"), None);
        assert_eq!(parse_hex(b"1234"), None);
        assert_eq!(parse_hex(b"+12345"), None);
        assert_eq!(parse_hex(b" 12345"), Some(0xff01_2345));
    }
    #[test]
    fn blend_preserves_alpha_and_bankers_rounding() {
        assert_eq!(blend(0xff00_0000, 0xffff_ffff, 0.5), Some(0xff80_8080));
        assert_eq!(blend(0x0000_0000, 0x0101_0101, 0.5), Some(0));
        assert_eq!(blend(0x8011_2233, 0xffff_ffff, -1.0), Some(0x8011_2233));
        assert_eq!(blend(0x8011_2233, 0xffff_ffff, 2.0), Some(0xffff_ffff));
        assert_eq!(blend(0, 0, f64::NAN), None);
    }
    #[test]
    fn service_brightness_and_speed_boundaries() {
        for (percent, expected) in [
            (-10, 0),
            (0, 0),
            (1, 1),
            (20, 1),
            (21, 2),
            (80, 4),
            (100, 5),
            (i32::MAX, 5),
        ] {
            assert_eq!(service_level(percent, false), expected);
        }
        for (percent, expected) in [
            (-10, 1),
            (1, 1),
            (30, 2),
            (50, 2),
            (70, 4),
            (100, 5),
            (i32::MAX, 5),
        ] {
            assert_eq!(service_level(percent, true), expected);
        }
    }
    #[test]
    fn ffi_rejects_null_or_oversized_inputs() {
        unsafe {
            let mut v = 0;
            assert_eq!(predator_lighting_parse_hex(std::ptr::null(), 3, &mut v), 0);
            assert_eq!(
                predator_lighting_parse_hex(b"abcdefghijk".as_ptr(), 11, &mut v),
                0
            );
            assert_eq!(predator_lighting_blend(0, 0, 0.5, std::ptr::null_mut()), 0);
        }
    }
}
