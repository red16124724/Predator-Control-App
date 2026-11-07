//! WMI payload codec and anti-blinking decision engine.
//!
//! Ports the pure halves of `WmiController`: tachometer/fan decoding, the mode
//! LED palette, the non-destructive mode payload builder, the system fan
//! extended payload and the "confirmed mode" short-circuit that suppresses
//! redundant EC/WMI reassertions. The WMI COM calls themselves stay managed.

/// Decodes the ACPI packet form `(rpm << 8) | status_byte`.
pub fn decode_tachometer(raw: u64) -> i32 {
    if (raw & 0xFF) == 0 {
        ((raw >> 8) & 0x1FFF) as i32
    } else {
        0
    }
}

/// Decodes either a direct 13-bit tachometer reading or an ACPI packet.
pub fn decode_fan_speed(raw: u64) -> i32 {
    if raw == 0 || raw == 0xFFFF || raw == 0xFFFF_FFFF || raw == 0xFFFF_FFFF_FFFF_FFFF {
        return 0;
    }
    if raw > 0x1FFF {
        decode_tachometer(raw)
    } else {
        (raw & 0x1FFF) as i32
    }
}

/// Mode key LED colour as `0x00RRGGBB`.
pub fn mode_led_color(mode: u8) -> u32 {
    match mode {
        0x00 => 0x00_DCFF, // Quiet: cyan
        0x01 => 0x00_96FF, // Balanced: blue
        0x04 => 0xFF_8C00, // Performance: orange
        0x05 => 0xFF_1432, // Turbo: red
        0x06 => 0x00_FF78, // Eco: green
        _ => 0x00_96FF,
    }
}

/// Builds the non-destructive `SetGamingMiscSetting` payload: preserves bit 0
/// (Fn/media key mode) and the vendor flags, replaces the mode byte and keeps
/// the `0x0B` vendor marker when the low byte would otherwise be zero.
pub fn build_mode_payload(current_misc: u64, mode: u8) -> u64 {
    if current_misc != 0 {
        let mut payload = (current_misc & !0xFF00) | ((mode as u64) << 8);
        if (payload & 0xFF) == 0 {
            payload |= 0x0B;
        }
        payload
    } else {
        0x0B | ((mode as u64) << 8)
    }
}

/// Extended `SetGamingFanSpeed` payload used for the system fan (`0x05 | 3<<8 | pct<<16`).
pub fn build_system_fan_payload(percent: u8) -> u64 {
    0x05 | (3u64 << 8) | ((percent.min(100) as u64) << 16)
}

/// Mirrors `AcerProtocol.FanSpeedInput` for the WMI fallback path:
/// `speed_id | (clamped_percent << 8)`.
pub fn fan_speed_payload(speed_id: u8, percent: i32) -> u64 {
    let clamped = percent.clamp(0, 100) as u64;
    (speed_id as u64) | (clamped << 8)
}

/// True when the requested mode must be written. A mode is redundant only after
/// the hardware accepted that exact mode and the caller did not force a retry.
pub fn should_suppress_mode_write(has_confirmed: bool, confirmed: u8, requested: u8, force: bool) -> bool {
    !force && has_confirmed && confirmed == requested
}

/// Nearest ten-percent fan step, rounding half away from zero (mirrors
/// `Math.Round(x / 10.0, MidpointRounding.AwayFromZero) * 10` for 0..100).
pub fn nearest_fan_speed(percent: i32) -> i32 {
    let clamped = percent.clamp(0, 100) as f64;
    (((clamped / 10.0) + 0.5).floor() * 10.0) as i32
}

// ---------------------------------------------------------------------------
// C-ABI exports
// ---------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn predator_wmi_decode_tachometer(raw: u64) -> i32 {
    crate::safe_ffi(0, || decode_tachometer(raw))
}

#[no_mangle]
pub unsafe extern "C" fn predator_wmi_decode_fan_speed(raw: u64) -> i32 {
    crate::safe_ffi(0, || decode_fan_speed(raw))
}

#[no_mangle]
pub unsafe extern "C" fn predator_wmi_mode_led_color(mode: u32) -> u32 {
    crate::safe_ffi(0x00_96FF, || mode_led_color(mode as u8))
}

#[no_mangle]
pub unsafe extern "C" fn predator_wmi_build_mode_payload(current_misc: u64, mode: u32) -> u64 {
    crate::safe_ffi(0x0B, || build_mode_payload(current_misc, mode as u8))
}

#[no_mangle]
pub unsafe extern "C" fn predator_wmi_build_system_fan_payload(percent: u32) -> u64 {
    crate::safe_ffi(0x0305, || build_system_fan_payload(percent as u8))
}

#[no_mangle]
pub unsafe extern "C" fn predator_wmi_fan_speed_payload(speed_id: u32, percent: i32) -> u64 {
    crate::safe_ffi(0, || fan_speed_payload(speed_id as u8, percent))
}

#[no_mangle]
pub unsafe extern "C" fn predator_wmi_should_suppress_mode_write(
    has_confirmed: i32,
    confirmed: u32,
    requested: u32,
    force: i32,
) -> i32 {
    crate::safe_ffi(
        0,
        || {
            if should_suppress_mode_write(has_confirmed != 0, confirmed as u8, requested as u8, force != 0) {
                1
            } else {
                0
            }
        },
    )
}

#[no_mangle]
pub unsafe extern "C" fn predator_wmi_nearest_fan_speed(percent: i32) -> i32 {
    crate::safe_ffi(0, || nearest_fan_speed(percent))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tachometer_packet_decodes_rpm_shifted_by_eight() {
        assert_eq!(decode_tachometer((3200u64 << 8) | 0x00), 3200);
        assert_eq!(decode_tachometer((3200u64 << 8) | 0x01), 0);
        assert_eq!(decode_tachometer(0x1FFF_00), 0x1FFF);
    }

    #[test]
    fn fan_speed_handles_direct_packets_and_sentinels() {
        assert_eq!(decode_fan_speed(0), 0);
        assert_eq!(decode_fan_speed(0xFFFF), 0);
        assert_eq!(decode_fan_speed(0xFFFF_FFFF), 0);
        assert_eq!(decode_fan_speed(u64::MAX), 0);
        assert_eq!(decode_fan_speed(1800), 1800);
        assert_eq!(decode_fan_speed((2400u64 << 8) | 0x02), 0);
        assert_eq!(decode_fan_speed(2400u64 << 8), 2400);
    }

    #[test]
    fn mode_led_palette_matches_hardware_colors() {
        assert_eq!(mode_led_color(0), 0x00DCFF);
        assert_eq!(mode_led_color(1), 0x0096FF);
        assert_eq!(mode_led_color(4), 0xFF8C00);
        assert_eq!(mode_led_color(5), 0xFF1432);
        assert_eq!(mode_led_color(6), 0x00FF78);
        assert_eq!(mode_led_color(0xFF), 0x0096FF);
    }

    #[test]
    fn mode_payload_preserves_low_byte_and_sets_vendor_marker() {
        let preserved = build_mode_payload(0x1234_0B00, 0x05);
        assert_eq!(preserved & 0xFF, 0x0B);
        assert_eq!((preserved >> 8) & 0xFF, 0x05);
        assert_eq!(preserved >> 16, 0x1234);

        let recovered = build_mode_payload(0x0000_0B00, 0x04);
        assert_eq!(recovered & 0xFF, 0x0B);
        assert_eq!((recovered >> 8) & 0xFF, 0x04);

        assert_eq!(build_mode_payload(0, 0x01), 0x0B | (0x01 << 8));
    }

    #[test]
    fn system_fan_payload_matches_managed_encoding() {
        assert_eq!(build_system_fan_payload(60), 0x05 | (3 << 8) | (60 << 16));
        assert_eq!(build_system_fan_payload(255), 0x05 | (3 << 8) | (100 << 16));
    }

    #[test]
    fn fan_speed_payload_clamps_and_shifts() {
        assert_eq!(fan_speed_payload(4, 55), 4 | (55 << 8));
        assert_eq!(fan_speed_payload(2, -20), 2);
        assert_eq!(fan_speed_payload(2, 400), 2 | (100 << 8));
    }

    #[test]
    fn suppression_requires_confirmed_match_and_no_force() {
        assert!(should_suppress_mode_write(true, 0x01, 0x01, false));
        assert!(!should_suppress_mode_write(true, 0x01, 0x01, true));
        assert!(!should_suppress_mode_write(true, 0x00, 0x01, false));
        assert!(!should_suppress_mode_write(false, 0x01, 0x01, false));
    }

    #[test]
    fn nearest_fan_speed_rounds_away_from_zero_at_half_steps() {
        assert_eq!(nearest_fan_speed(0), 0);
        assert_eq!(nearest_fan_speed(4), 0);
        assert_eq!(nearest_fan_speed(5), 10);
        assert_eq!(nearest_fan_speed(54), 50);
        assert_eq!(nearest_fan_speed(55), 60);
        assert_eq!(nearest_fan_speed(100), 100);
        assert_eq!(nearest_fan_speed(-5), 0);
        assert_eq!(nearest_fan_speed(140), 100);
    }
}
