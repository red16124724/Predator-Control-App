//! Acer capability probe policy and fan-table rules.
//!
//! Ports the pure decision logic from `AcerHardwareProtocols` (factory EC fan
//! table detection, fan-table payload clamping/decoding) and
//! `DeviceCapabilitiesManager` (capability-override merge policy, mode default
//! finalisation). Registry access, hardware probing and the WinForms surface
//! stay managed.

/// Models whose EC exposes the factory fan table (`AcerProtocol.UsesFanTable`).
pub const FAN_TABLE_MODELS: [&str; 13] = [
    "AN515-46", "AN515-47", "AN515-58", "AN517-42", "AN517-43", "AN517-55", "AN16", "AN17", "AN18",
    "AN14", "ANV14", "ANV15", "ANV16",
];

/// Bit layout shared with the managed `CapabilityOverrides.Apply` merge.
pub const CAP_COOLBOOST: u32 = 1 << 0;
pub const CAP_GPU_MODE_SWITCH: u32 = 1 << 1;
pub const CAP_THIRD_FAN: u32 = 1 << 2;
pub const CAP_FAN_TABLE: u32 = 1 << 3;
pub const CAP_USB_CHARGING: u32 = 1 << 4;
pub const CAP_BATTERY_CALIBRATION: u32 = 1 << 5;
pub const CAP_HAS_EC_HID: u32 = 1 << 6;
pub const CAP_MODE_KEY: u32 = 1 << 7;
pub const CAP_HAS_THIRD_FAN: u32 = 1 << 8;
pub const CAP_OVERRIDE_COUNT: usize = 8;

/// Case-insensitive substring match against the fan-table model list.
pub fn uses_fan_table(model: &str) -> bool {
    if model.is_empty() {
        return false;
    }
    let upper = model.to_ascii_uppercase();
    FAN_TABLE_MODELS.iter().any(|m| upper.contains(m))
}

/// Fan-table input payload value, clamped to the defined 1..=3 range.
pub fn fan_table_input(value: u64) -> u64 {
    if !(1..=3).contains(&value) {
        1
    } else {
        value
    }
}

/// Decodes the mode byte from a `GetFanTable` output; 0 means "no table".
pub fn fan_table_value(output: u64) -> i32 {
    let value = (output >> 8) & 0xFF;
    if (1..=3).contains(&value) {
        value as i32
    } else {
        0
    }
}

/// Fills missing mode defaults and enforces `PL2 >= PL1`.
pub fn finalize_mode_default(pl1: i32, pl2: i32, tdp: i32) -> (i32, i32) {
    let base = if tdp > 0 { tdp } else { 45 };
    let mut effective_pl1 = pl1;
    let mut effective_pl2 = pl2;
    if effective_pl1 <= 0 {
        effective_pl1 = base;
    }
    if effective_pl2 <= 0 {
        effective_pl2 = ((base as f64) * 1.35).round() as i32;
    }
    if effective_pl2 < effective_pl1 {
        effective_pl2 = effective_pl1;
    }
    (effective_pl1, effective_pl2)
}

/// Applies tri-state overrides (`-1` keep, `0` disable, `1` enable) to a
/// baseline capability bitfield and derives `HasThirdFan` from the merged
/// third-fan bit.
pub fn apply_overrides(baseline: u32, overrides: &[i32]) -> u32 {
    let mut bits = baseline;
    for index in 0..CAP_OVERRIDE_COUNT {
        let override_value = overrides.get(index).copied().unwrap_or(-1);
        if override_value > 0 {
            bits |= 1 << index;
        } else if override_value == 0 {
            bits &= !(1 << index);
        }
    }
    if bits & CAP_THIRD_FAN != 0 {
        bits |= CAP_HAS_THIRD_FAN;
    } else {
        bits &= !CAP_HAS_THIRD_FAN;
    }
    bits
}

// ---------------------------------------------------------------------------
// C-ABI exports
// ---------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn predator_capability_uses_fan_table(bytes: *const u8, len: usize) -> i32 {
    crate::safe_ffi(0, || {
        if len == 0 || !crate::is_valid_buffer(bytes) {
            return 0;
        }
        let slice = unsafe { std::slice::from_raw_parts(bytes, len) };
        match std::str::from_utf8(slice) {
            Ok(model) => {
                if uses_fan_table(model) {
                    1
                } else {
                    0
                }
            }
            Err(_) => 0,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_capability_fan_table_input(value: u64) -> u64 {
    crate::safe_ffi(1, || fan_table_input(value))
}

#[no_mangle]
pub unsafe extern "C" fn predator_capability_fan_table_value(output: u64) -> i32 {
    crate::safe_ffi(0, || fan_table_value(output))
}

#[no_mangle]
pub unsafe extern "C" fn predator_capability_finalize_mode_default(
    pl1: i32,
    pl2: i32,
    tdp: i32,
    out_pl1: *mut i32,
    out_pl2: *mut i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(out_pl1) || !crate::is_valid_mut_ptr(out_pl2) {
            return 0;
        }
        let (effective_pl1, effective_pl2) = finalize_mode_default(pl1, pl2, tdp);
        unsafe {
            out_pl1.write_unaligned(effective_pl1);
            out_pl2.write_unaligned(effective_pl2);
        }
        1
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_capability_apply_overrides(
    baseline: u32,
    overrides: *const i32,
    override_count: usize,
) -> u32 {
    crate::safe_ffi(baseline, || {
        if override_count == 0 || !crate::is_valid_buffer(overrides) {
            return apply_overrides(baseline, &[]);
        }
        let slice = unsafe { std::slice::from_raw_parts(overrides, override_count) };
        apply_overrides(baseline, slice)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fan_table_models_match_case_insensitively() {
        assert!(uses_fan_table("Nitro AN515-58"));
        assert!(uses_fan_table("an16-51"));
        assert!(uses_fan_table("Predator AN16-51"));
        assert!(!uses_fan_table("Aspire 5 A515"));
        assert!(uses_fan_table("ANV15-41"));
        assert!(!uses_fan_table("PH315-55"));
        assert!(!uses_fan_table(""));
    }

    #[test]
    fn fan_table_input_clamps_to_defined_enum() {
        assert_eq!(fan_table_input(0), 1);
        assert_eq!(fan_table_input(1), 1);
        assert_eq!(fan_table_input(2), 2);
        assert_eq!(fan_table_input(3), 3);
        assert_eq!(fan_table_input(4), 1);
        assert_eq!(fan_table_input(u64::MAX), 1);
    }

    #[test]
    fn fan_table_value_reads_shifted_mode_byte() {
        assert_eq!(fan_table_value(0), 0);
        assert_eq!(fan_table_value(1 << 8), 1);
        assert_eq!(fan_table_value(3 << 8), 3);
        assert_eq!(fan_table_value(9 << 8), 0);
        assert_eq!(fan_table_value(2), 0);
    }

    #[test]
    fn finalize_mode_default_enforces_pl2_at_least_pl1() {
        assert_eq!(finalize_mode_default(70, 40, 45), (70, 70));
        assert_eq!(finalize_mode_default(45, 65, 45), (45, 65));
        assert_eq!(finalize_mode_default(0, 0, 45), (45, 61));
        assert_eq!(finalize_mode_default(-5, 10, 100), (100, 100));
    }

    #[test]
    fn overrides_merge_and_derive_third_fan() {
        let baseline = CAP_COOLBOOST | CAP_FAN_TABLE;
        let keep = [-1; CAP_OVERRIDE_COUNT];
        assert_eq!(apply_overrides(baseline, &keep), baseline);

        let mut disable_fan_table = keep;
        disable_fan_table[3] = 0;
        assert_eq!(apply_overrides(baseline, &disable_fan_table), CAP_COOLBOOST);

        let mut enable_third_fan = keep;
        enable_third_fan[2] = 1;
        let merged = apply_overrides(baseline, &enable_third_fan);
        assert_ne!(merged & CAP_THIRD_FAN, 0);
        assert_ne!(merged & CAP_HAS_THIRD_FAN, 0);

        let mut disable_third_fan = keep;
        disable_third_fan[2] = 0;
        let cleared = apply_overrides(CAP_THIRD_FAN | CAP_HAS_THIRD_FAN, &disable_third_fan);
        assert_eq!(cleared & CAP_HAS_THIRD_FAN, 0);
    }

    #[test]
    fn overrides_with_short_slice_keep_remaining_bits() {
        let baseline = CAP_MODE_KEY | CAP_USB_CHARGING;
        let merged = apply_overrides(baseline, &[0]);
        // Index 0 (CoolBoost) was clear already, so both remaining bits survive.
        assert_eq!(merged, CAP_MODE_KEY | CAP_USB_CHARGING);
    }
}
