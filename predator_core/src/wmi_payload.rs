//! Acer WMI fan payload construction: fan-behaviour masks and the direct /
//! extended fan-speed frames. These are pure bit layouts; the WMI calls that
//! consume them stay managed.

/// `SetGamingFanBehavior`: CPU bit 0, system bit 1 and GPU bit 3 are set in the
/// low byte, and the mode is repeated at shifts 16, 18 and 22.
pub fn fan_behavior_payload(mode: u8) -> u64 {
    0x0B | ((mode as u64) << 16) | ((mode as u64) << 18) | ((mode as u64) << 22)
}

/// CPU fan speed frames: the direct 0x01 frame and the extended 0x05/group-1
/// frame, both clamped to the firmware's 10..100 range.
pub fn cpu_fan_speed_payloads(percentage: u8) -> (u64, u64) {
    let clamped = percentage.clamp(10, 100) as u64;
    let direct = 0x01 | (clamped << 8);
    let extended = 0x05 | (1u64 << 8) | (clamped << 16);
    (direct, extended)
}

/// GPU fan speed frames: two direct 0x04 frames and the extended group-2 /
/// group-4 frames.
pub fn gpu_fan_speed_payloads(percentage: u8) -> (u64, u64, u64, u64) {
    let clamped = percentage.clamp(10, 100) as u64;
    let direct = 0x04 | (clamped << 8);
    let extended_primary = 0x05 | (2u64 << 8) | (clamped << 16);
    let extended_alt = 0x05 | (4u64 << 8) | (clamped << 16);
    (direct, direct, extended_primary, extended_alt)
}

#[no_mangle]
pub unsafe extern "C" fn predator_wmi_fan_behavior_payload(mode: i32) -> u64 {
    crate::safe_ffi(0, || fan_behavior_payload(mode.clamp(0, 255) as u8))
}

/// Writes both CPU frames. Returns 0 on success.
#[no_mangle]
pub unsafe extern "C" fn predator_wmi_cpu_fan_payloads(
    percentage: i32,
    out_direct: *mut u64,
    out_extended: *mut u64,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !crate::is_valid_mut_ptr(out_direct) || !crate::is_valid_mut_ptr(out_extended) {
            return -1;
        }
        let (direct, extended) = cpu_fan_speed_payloads(percentage.clamp(0, 255) as u8);
        unsafe {
            *out_direct = direct;
            *out_extended = extended;
        }
        0
    })
}

/// Writes all four GPU frames. Returns 0 on success.
#[no_mangle]
pub unsafe extern "C" fn predator_wmi_gpu_fan_payloads(
    percentage: i32,
    out_direct_primary: *mut u64,
    out_direct_alt: *mut u64,
    out_extended_primary: *mut u64,
    out_extended_alt: *mut u64,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !crate::is_valid_mut_ptr(out_direct_primary)
            || !crate::is_valid_mut_ptr(out_direct_alt)
            || !crate::is_valid_mut_ptr(out_extended_primary)
            || !crate::is_valid_mut_ptr(out_extended_alt)
        {
            return -1;
        }
        let (a, b, c, d) = gpu_fan_speed_payloads(percentage.clamp(0, 255) as u8);
        unsafe {
            *out_direct_primary = a;
            *out_direct_alt = b;
            *out_extended_primary = c;
            *out_extended_alt = d;
        }
        0
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn behavior_sets_all_three_group_bits() {
        assert_eq!(fan_behavior_payload(1), 0x0B | (1 << 16) | (1 << 18) | (1 << 22));
        assert_eq!(fan_behavior_payload(0), 0x0B);
    }

    #[test]
    fn cpu_payload_clamps_low_values() {
        let (direct, extended) = cpu_fan_speed_payloads(3);
        assert_eq!(direct, 0x01 | (10 << 8));
        assert_eq!(extended, 0x05 | (1 << 8) | (10 << 16));
    }

    #[test]
    fn cpu_payload_preserves_in_range_value() {
        let (direct, _) = cpu_fan_speed_payloads(100);
        assert_eq!(direct, 0x01 | (100 << 8));
    }

    #[test]
    fn gpu_payload_order_matches_managed_frames() {
        let (dp, da, ep, ea) = gpu_fan_speed_payloads(50);
        assert_eq!(dp, da);
        assert_eq!(dp, 0x04 | (50 << 8));
        assert_eq!(ep, 0x05 | (2 << 8) | (50 << 16));
        assert_eq!(ea, 0x05 | (4 << 8) | (50 << 16));
    }
}
