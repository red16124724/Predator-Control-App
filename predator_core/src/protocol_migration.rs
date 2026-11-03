//! Pure Acer packet encoding. These functions do not access hardware.

fn encode(op: u32, a: u64, b: u64, c: u64, d: u64) -> Option<u64> {
    let bool_value = a != 0;
    Some(match op {
        0 => 1 | ((a as u32 as u64) << 8),
        1 => (a >> 8) & 0xffff,
        2 => (a as u8 as u64) | (((b as i32).clamp(0, 100) as u64) << 8),
        3 => (((a as i32).clamp(0, 100) + 5) / 10 * 10) as u64,
        4 => 7 | ((bool_value as u64) << 16),
        5 => (((a >> 8) & 0xff) == 1) as u64,
        6 => 4 | ((if bool_value { 15_u64 } else { 31 }) << 8) | (b << 16),
        7 => a | ((b as u8 as u64) << 8),
        8 => if c != 0 { a | b } else { a & !b },
        9 => 4 | ((if bool_value { 15_u64 } else { 31 }) << 8) | (b << 16) | (c & 0xffffffffff000000),
        10 => 0x0f | ((bool_value as u64) << 8) | (b & 0xffffffffffff0000),
        11 => 1 | ((a as u8 as u64) << 8) | 0x80000,
        12 => 2 | ((a as u8 as u64) << 8) | ((d & 0xffff0000) | 0x80000)
            | (((b as i32).clamp(0, 100) as u64) << 32)
            | (((c as i32).clamp(0, 255) as u64) << 40),
        13 => (a >> 32) & 0xff,
        14 => (a >> 40) & 0xff,
        15 => a & 0xff,
        16 => {
            let modes = b as i32;
            let (ok, value): (bool, u8) = match a as u8 {
                5 => (true, 0),
                4 => (true, if modes >= 4 { 1 } else { 0 }),
                1 => (true, if modes >= 4 { 2 } else { 1 }),
                0 => (true, if modes >= 4 { 3 } else { 2 }),
                6 => (true, modes.wrapping_sub(1).max(0) as u8),
                _ => (false, 1),
            };
            value as u64 | ((ok as u64) << 8)
        }
        17 => {
            let modes = b as i32;
            let (ok, value): (bool, u8) = match a as u8 {
                0 => (true, 5),
                1 => (true, if modes >= 4 { 4 } else { 1 }),
                2 => (true, if modes >= 4 { 1 } else { 0 }),
                3 => (true, 0),
                4 => (true, 6),
                _ => (false, 1),
            };
            value as u64 | ((ok as u64) << 8)
        }
        18 => (1_u64 << (a as u32)) | ((b & 3).wrapping_shl(16 + 2 * a as u32)),
        19 => { let value = (a >> 8) & 0xff; if (1..=3).contains(&value) { value } else { 0 } },
        20 => { let floor = (a >> 16) & 0xff; ((((a >> 8) & 0xff) == 15) as u64) | ((if matches!(floor, 10 | 20 | 30) { floor } else { 0 }) << 8) },
        21 => a >> 24,
        22 => (a >> 8) & 0xffff,
        23 => (a >> 8) & 0xff,
        24 => if bool_value { 0x1000000000010 } else { 0x10 },
        25 => 0x10 | ((bool_value as u64) << 8),
        26 => (b as u8 as u64) | ((bool_value as u64) << 8),
        27 => a as u8 as u64,
        28 => a as u8 as u64,
        29 => {
            let supported = a & 3;
            let health_on = ((b & 0xff) == 1) as u64;
            let calibration_on = (((b >> 8) & 0xff) == 1) as u64;
            supported | (health_on << 2) | (calibration_on << 3)
        }
        _ => return None,
    })
}

/// Returns 1 on success, leaving the output untouched when an operation fails.
#[no_mangle]
pub unsafe extern "C" fn predator_protocol_encode(op: u32, a: u64, b: u64, c: u64, d: u64, out: *mut u64) -> i32 {
    crate::safe_ffi(0, || {
        if out.is_null() || (op == 18 && a >= 24) { return 0; }
        match encode(op, a, b, c, d) {
            Some(value) => { std::ptr::write_unaligned(out, value); 1 },
            None => 0,
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn encodes_sensors_fan_power_and_backlight() {
        assert_eq!(encode(0, 10, 0, 0, 0), Some(0x0a01));
        assert_eq!(encode(1, 0x12345600, 0, 0, 0), Some(0x3456));
        assert_eq!(encode(2, 4, 1000, 0, 0), Some(0x6404));
        assert_eq!(encode(3, 95, 0, 0, 0), Some(100));
        assert_eq!(encode(6, 1, 30, 0, 0), Some(0x1e0f04));
        assert_eq!(encode(9, 1, 30, 0xabcdef1200000000, 0), Some(0xabcdef12001e0f04));
        assert_eq!(encode(12, 2, 100, 30, 0x80000), Some(0x1e6400080202));
        assert_eq!(encode(18, 3, 2, 0, 0), Some(0x800008));
    }
    #[test]
    fn maps_modes_and_preserves_invalid_fallbacks() {
        assert_eq!(encode(16, 5, 5, 0, 0), Some(0x100));
        assert_eq!(encode(16, 6, 5, 0, 0), Some(0x104));
        assert_eq!(encode(16, 255, 5, 0, 0), Some(1));
        assert_eq!(encode(17, 1, 3, 0, 0), Some(0x101));
        assert_eq!(encode(17, 255, 5, 0, 0), Some(1));
    }
    #[test]
    fn rejects_invalid_operation_and_null_output() {
        let mut out = 123;
        assert_eq!(unsafe { predator_protocol_encode(18, 24, 0, 0, 0, &mut out) }, 0);
        assert_eq!(out, 123);
        assert_eq!(unsafe { predator_protocol_encode(100, 0, 0, 0, 0, &mut out) }, 0);
        assert_eq!(unsafe { predator_protocol_encode(0, 0, 0, 0, 0, std::ptr::null_mut()) }, 0);
        assert_eq!(out, 123);
    }
}
