//! Settings policy: range clamping and sanitisation of persisted values.
//! Registry/file persistence stays managed; only the acceptance rules live
//! here so they are testable without touching user state.

/// Fan/backlight/brightness percentages.
pub fn clamp_percent(value: i32) -> i32 {
    value.clamp(0, 100)
}

/// Power-mode byte range accepted by the WMI/EC mode writers (0x00..=0x06).
pub fn clamp_power_mode(value: i32) -> u8 {
    value.clamp(0x00, 0x06) as u8
}

/// Fan-mode byte range (0x00..=0x03).
pub fn clamp_fan_mode(value: i32) -> u8 {
    value.clamp(0x00, 0x03) as u8
}

/// A generic inclusive clamp used by numeric settings with explicit bounds.
pub fn clamp_to(value: i32, lo: i32, hi: i32) -> i32 {
    let hi = if hi < lo { lo } else { hi };
    value.clamp(lo, hi)
}

/// Normalises the three allowed-limit fields the way `AppSettings` does on
/// load: negatives mean "unknown" (0), and PL1 never exceeds PL2.
pub fn sanitize_allowed_limits(min_watts: i32, max_pl1: i32, max_pl2: i32) -> (i32, i32, i32) {
    let min = if min_watts > 0 {
        min_watts.clamp(5, 200)
    } else {
        0
    };
    let mut max1 = if max_pl1 > 0 {
        max_pl1.clamp(min.max(5), 350)
    } else {
        0
    };
    let max2 = if max_pl2 > 0 {
        max_pl2.clamp(5, 400)
    } else {
        0
    };
    // The firmware PL1 ceiling may never exceed PL2.
    if max2 > 0 && (max1 <= 0 || max1 > max2) {
        max1 = max2;
    }
    (min, max1, max2)
}

/// TDP plausibility window applied when a scan result is loaded.
pub fn sanitize_tdp(tdp: i32) -> i32 {
    if tdp > 0 {
        tdp.clamp(5, 250)
    } else {
        0
    }
}

#[no_mangle]
pub unsafe extern "C" fn predator_settings_clamp_percent(value: i32) -> i32 {
    crate::safe_ffi(value, || clamp_percent(value))
}

#[no_mangle]
pub unsafe extern "C" fn predator_settings_clamp_power_mode(value: i32) -> i32 {
    crate::safe_ffi(value, || clamp_power_mode(value) as i32)
}

#[no_mangle]
pub unsafe extern "C" fn predator_settings_clamp_fan_mode(value: i32) -> i32 {
    crate::safe_ffi(value, || clamp_fan_mode(value) as i32)
}

#[no_mangle]
pub unsafe extern "C" fn predator_settings_sanitize_allowed_limits(
    min_watts: i32,
    max_pl1: i32,
    max_pl2: i32,
    out_min: *mut i32,
    out_max1: *mut i32,
    out_max2: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !crate::is_valid_mut_ptr(out_min)
            || !crate::is_valid_mut_ptr(out_max1)
            || !crate::is_valid_mut_ptr(out_max2)
        {
            return -1;
        }
        let (min, max1, max2) = sanitize_allowed_limits(min_watts, max_pl1, max_pl2);
        unsafe {
            *out_min = min;
            *out_max1 = max1;
            *out_max2 = max2;
        }
        0
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_settings_sanitize_tdp(tdp: i32) -> i32 {
    crate::safe_ffi(0, || sanitize_tdp(tdp))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn percent_is_bounded() {
        assert_eq!(clamp_percent(-5), 0);
        assert_eq!(clamp_percent(50), 50);
        assert_eq!(clamp_percent(150), 100);
    }

    #[test]
    fn mode_bytes_match_hardware_ranges() {
        assert_eq!(clamp_power_mode(-1), 0);
        assert_eq!(clamp_power_mode(9), 6);
        assert_eq!(clamp_fan_mode(9), 3);
    }

    #[test]
    fn clamp_to_tolerates_inverted_bounds() {
        assert_eq!(clamp_to(10, 20, 5), 20);
        assert_eq!(clamp_to(7, 5, 9), 7);
    }

    #[test]
    fn allowed_limits_treat_negatives_as_unknown() {
        assert_eq!(sanitize_allowed_limits(-1, -50, -3), (0, 0, 0));
    }

    #[test]
    fn allowed_limits_keep_pl1_under_pl2() {
        let (min, max1, max2) = sanitize_allowed_limits(15, 300, 100);
        assert_eq!(min, 15);
        assert_eq!(max1, 100);
        assert_eq!(max2, 100);
    }

    #[test]
    fn tdp_window_is_plausible() {
        assert_eq!(sanitize_tdp(0), 0);
        assert_eq!(sanitize_tdp(2), 5);
        assert_eq!(sanitize_tdp(400), 250);
        assert_eq!(sanitize_tdp(45), 45);
    }
}
