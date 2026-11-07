//! CPU power-limit policy: clamp chains, proportional per-mode estimates,
//! factory mode defaults, MMIO RAPL decode and deviation checks.
//!
//! These mirror the managed reference implementations in `AppSettings` and
//! `AcerHardwareProtocols` exactly, including .NET banker's rounding. All
//! functions are pure; the MSR/MMIO reads and writes stay managed.

/// .NET `Math.Round(double)` semantics: round half to even.
fn round_half_even(v: f64) -> i32 {
    v.round_ties_even() as i32
}

/// Mode index used by this module: 0 = Eco, 1 = Quiet, 2 = Balanced,
/// 3 = Performance, 4 = Turbo.
pub const MODE_COUNT: usize = 5;

fn normalize_max(max: i32, fallback: i32) -> i32 {
    if max > 0 {
        max
    } else {
        fallback
    }
}

/// Clamps a PL1/PL2 pair the way `AppSettings.GetClamped*` does: the min is
/// never above PL1, PL1 never above PL2, and PL2 never below PL1.
pub fn clamp_limits(pl1: i32, pl2: i32, min: i32, max_pl1: i32, max_pl2: i32) -> (i32, i32) {
    let min = if min > 0 { min } else { 5 };
    let mut m1 = normalize_max(max_pl1, 200);
    let mut m2 = normalize_max(max_pl2, 200);

    if m1 < min {
        m1 = min;
    }
    if m2 < m1 {
        m2 = m1;
    }

    let p1 = pl1.clamp(min, m1);
    let p2 = pl2.clamp(p1.max(min), m2);
    (p1, p2)
}

/// Ratios used by `AppSettings.EstimateProportionalDefaultsForMode`.
pub fn mode_ratios(mode: i32) -> (f64, f64) {
    match mode {
        0 => (0.60, 0.65),
        1 => (0.75, 0.80),
        3 => (1.25, 1.30),
        4 => (1.45, 1.50),
        _ => (1.00, 1.00),
    }
}

/// Proportional estimate for a mode from a balanced anchor pair.
pub fn estimate_mode(
    anchor_pl1: f64,
    anchor_pl2: f64,
    mode: i32,
    min: i32,
    max_pl1: i32,
    max_pl2: i32,
) -> (i32, i32) {
    let (ratio1, ratio2) = mode_ratios(mode);
    let est1 = round_half_even(anchor_pl1 * ratio1);
    let est2 = round_half_even(anchor_pl2 * ratio2);
    clamp_limits(est1, est2, min, max_pl1, max_pl2)
}

/// Factory per-mode defaults from base TDP, matching
/// `AcerHardwareProtocols.ComputeModeDefaults` (Eco, Quiet, Balanced,
/// Performance, Turbo).
pub fn mode_defaults(tdp: i32, min: i32, max_pl1: i32, max_pl2: i32) -> [(i32, i32); MODE_COUNT] {
    let min = if min > 0 { min } else { 5 };
    let safe_max_pl2 = max_pl2.max(min);
    let safe_max_pl1 = max_pl1.clamp(min, safe_max_pl2);
    let safe_max_pl2 = safe_max_pl1.max(safe_max_pl2);

    let eco_pl1 = round_half_even(tdp as f64 * 0.55).clamp(min, safe_max_pl1);
    let eco_pl2 = round_half_even(tdp as f64 * 0.75).clamp(eco_pl1, safe_max_pl2);

    let quiet_pl1 = round_half_even(tdp as f64 * 0.75).clamp(min, safe_max_pl1);
    let quiet_pl2 = tdp.clamp(quiet_pl1, safe_max_pl2);

    let bal_pl1 = tdp.clamp(min, safe_max_pl1);
    let bal_pl2 = round_half_even(tdp as f64 * 1.45).clamp(bal_pl1, safe_max_pl2);

    let perf_pl1 = round_half_even(tdp as f64 * 1.55).clamp(bal_pl1, safe_max_pl1);
    let perf_pl2 = round_half_even(tdp as f64 * 2.00).clamp(perf_pl1, safe_max_pl2);

    let turbo_pl1 = round_half_even(tdp as f64 * 2.00).clamp(perf_pl1, safe_max_pl1);
    let turbo_pl2 = turbo_pl1.max(safe_max_pl2);

    [
        (eco_pl1, eco_pl2),
        (quiet_pl1, quiet_pl2),
        (bal_pl1, bal_pl2),
        (perf_pl1, perf_pl2),
        (turbo_pl1, turbo_pl2),
    ]
}

/// Decodes the MMIO RAPL mirror into watts. Returns `None` when the source is
/// unusable: zero scale or the firmware lock bit (PL2 dword bit 31).
pub fn decode_mmio_limits(low: u32, high: u32, units_per_watt: u32) -> Option<(i32, i32)> {
    if units_per_watt == 0 || (high & (1u32 << 31)) != 0 {
        return None;
    }
    let pl1 = round_half_even((low & 0x7FFF) as f64 / units_per_watt as f64);
    let pl2 = round_half_even((high & 0x7FFF) as f64 / units_per_watt as f64);
    Some((pl1, pl2))
}

/// A 1 W tolerance, matching `MmioPowerLimitsDeviate`.
pub fn mmio_deviation(sample_pl1: i32, sample_pl2: i32, target_pl1: i32, target_pl2: i32) -> bool {
    (sample_pl1 - target_pl1).abs() >= 1 || (sample_pl2 - target_pl2).abs() >= 1
}

// ---------------------------------------------------------------------------
// C ABI
// ---------------------------------------------------------------------------

/// Writes clamp_limits() into `out_pl1` / `out_pl2`. Returns 0 on success.
#[no_mangle]
pub unsafe extern "C" fn predator_power_clamp_limits(
    pl1: i32,
    pl2: i32,
    min: i32,
    max_pl1: i32,
    max_pl2: i32,
    out_pl1: *mut i32,
    out_pl2: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !crate::is_valid_mut_ptr(out_pl1) || !crate::is_valid_mut_ptr(out_pl2) {
            return -1;
        }
        let (p1, p2) = clamp_limits(pl1, pl2, min, max_pl1, max_pl2);
        unsafe {
            *out_pl1 = p1;
            *out_pl2 = p2;
        }
        0
    })
}

/// Writes estimate_mode() into `out_pl1` / `out_pl2`. Returns 0 on success.
#[no_mangle]
pub unsafe extern "C" fn predator_power_estimate_mode(
    anchor_pl1: f64,
    anchor_pl2: f64,
    mode: i32,
    min: i32,
    max_pl1: i32,
    max_pl2: i32,
    out_pl1: *mut i32,
    out_pl2: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !crate::is_valid_mut_ptr(out_pl1) || !crate::is_valid_mut_ptr(out_pl2) {
            return -1;
        }
        let (p1, p2) = estimate_mode(anchor_pl1, anchor_pl2, mode, min, max_pl1, max_pl2);
        unsafe {
            *out_pl1 = p1;
            *out_pl2 = p2;
        }
        0
    })
}

/// Writes all five mode defaults as interleaved pl1/pl2 pairs (`out` must hold
/// 10 i32 values). Returns 0 on success.
#[no_mangle]
pub unsafe extern "C" fn predator_power_mode_defaults(
    tdp: i32,
    min: i32,
    max_pl1: i32,
    max_pl2: i32,
    out: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        let bytes = MODE_COUNT * 2 * std::mem::size_of::<i32>();
        if !crate::is_valid_buffer_range(out as *const u8, bytes) {
            return -1;
        }
        let defaults = mode_defaults(tdp, min, max_pl1, max_pl2);
        for (i, (p1, p2)) in defaults.iter().enumerate() {
            unsafe {
                *out.add(i * 2) = *p1;
                *out.add(i * 2 + 1) = *p2;
            }
        }
        0
    })
}

/// Writes the decoded MMIO pair. Returns 0 on success, 1 when the source is
/// locked or unusable (out params untouched).
#[no_mangle]
pub unsafe extern "C" fn predator_power_decode_mmio(
    low: u32,
    high: u32,
    units_per_watt: u32,
    out_pl1: *mut i32,
    out_pl2: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !crate::is_valid_mut_ptr(out_pl1) || !crate::is_valid_mut_ptr(out_pl2) {
            return -1;
        }
        match decode_mmio_limits(low, high, units_per_watt) {
            Some((p1, p2)) => {
                unsafe {
                    *out_pl1 = p1;
                    *out_pl2 = p2;
                }
                0
            }
            None => 1,
        }
    })
}

/// Returns 1 when either sampled limit deviates from target by >= 1 W.
#[no_mangle]
pub unsafe extern "C" fn predator_power_mmio_deviation(
    sample_pl1: i32,
    sample_pl2: i32,
    target_pl1: i32,
    target_pl2: i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if mmio_deviation(sample_pl1, sample_pl2, target_pl1, target_pl2) {
            1
        } else {
            0
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clamp_keeps_pl1_at_or_below_pl2() {
        assert_eq!(clamp_limits(120, 60, 15, 140, 140), (120, 120));
        assert_eq!(clamp_limits(5, 5, 15, 140, 140), (15, 15));
    }

    #[test]
    fn clamp_uses_fallbacks_for_missing_bounds() {
        assert_eq!(clamp_limits(500, 900, 0, 0, 0), (200, 200));
    }

    #[test]
    fn estimate_matches_balanced_anchor() {
        assert_eq!(estimate_mode(45.0, 60.0, 2, 5, 200, 200), (45, 60));
    }

    #[test]
    fn estimate_scales_turbo() {
        assert_eq!(estimate_mode(45.0, 60.0, 4, 5, 200, 200), (65, 90));
    }

    #[test]
    fn estimate_uses_bankers_rounding() {
        // 16.5 -> 16 in .NET; a naive round() would give 17.
        assert_eq!(estimate_mode(30.0, 30.0, 0, 5, 200, 200), (18, 20));
        assert_eq!(estimate_mode(10.0, 10.0, 0, 5, 200, 200).0, 6);
    }

    #[test]
    fn mode_defaults_are_monotonic() {
        let d = mode_defaults(45, 12, 140, 140);
        assert_eq!(d.len(), 5);
        for pair in d.windows(2) {
            assert!(pair[0].0 <= pair[1].0, "PL1 must not decrease across modes");
            assert!(pair[0].1 <= pair[1].1, "PL2 must not decrease across modes");
        }
        assert!(d[0].0 >= 12);
        assert!(d[4].1 <= 140);
    }

    #[test]
    fn mode_defaults_are_pl2_at_least_pl1() {
        for (p1, p2) in mode_defaults(28, 5, 60, 60) {
            assert!(p2 >= p1);
        }
    }

    #[test]
    fn mmio_decode_rejects_locked_and_scaled_zero() {
        assert_eq!(decode_mmio_limits(100, 200, 0), None);
        assert_eq!(decode_mmio_limits(100, 200 | (1 << 31), 8), None);
    }

    #[test]
    fn mmio_decode_converts_raw_units() {
        assert_eq!(decode_mmio_limits(360, 480, 8), Some((45, 60)));
    }

    #[test]
    fn deviation_uses_one_watt_tolerance() {
        assert!(!mmio_deviation(45, 60, 45, 60));
        assert!(mmio_deviation(46, 60, 45, 60));
        assert!(mmio_deviation(45, 59, 45, 60));
    }
}
