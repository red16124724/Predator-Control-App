//! Model-catalog policy: heuristic capability classification used when a
//! model string is not an exact catalog entry. The full 92-row product table
//! stays managed for now; the decision rules that were previously inline
//! (`PTX17` triple-fan, `PHN`/`AN` four-zone, Nitro V single-zone, 14-inch
//! numpad inference and the generic fallback) live here.

/// Lighting tiers, matching `LightingTier` in `model_catalog.rs`.
pub const TIER_UNAVAILABLE: i32 = 0;
pub const TIER_SINGLE: i32 = 1;
pub const TIER_THREE: i32 = 3;
pub const TIER_FOUR: i32 = 4;
pub const TIER_PER_KEY: i32 = 5;

pub const FANS_DUAL: i32 = 2;
pub const FANS_TRIPLE: i32 = 3;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Heuristic {
    pub tier: i32,
    pub fans: i32,
    pub numpad: bool,
    pub lightbar: bool,
    pub cpu_pl_control: bool,
}

/// Classifies an unrecognised model string, mirroring the managed fallback
/// chain. Returns `None` for an empty input.
pub fn classify(model: &str) -> Option<Heuristic> {
    let upper = model.trim().to_uppercase();
    if upper.is_empty() {
        return None;
    }

    let has = |needle: &str| upper.contains(needle);

    if has("PH16") || has("PH18") || has("PTX17") {
        let triple = has("PTX17");
        return Some(Heuristic {
            tier: TIER_PER_KEY,
            fans: if triple { FANS_TRIPLE } else { FANS_DUAL },
            numpad: true,
            lightbar: !triple,
            cpu_pl_control: true,
        });
    }

    if has("PT314") || has("PT516") || has("PT14") || has("PT316") {
        let triple = has("PT516") || has("PT316");
        return Some(Heuristic {
            tier: TIER_THREE,
            fans: if triple { FANS_TRIPLE } else { FANS_DUAL },
            numpad: true,
            lightbar: false,
            cpu_pl_control: true,
        });
    }

    if has("ANV15") || has("ANV16") || has("ANV14") || has("NITRO V") {
        return Some(Heuristic {
            tier: TIER_SINGLE,
            fans: FANS_DUAL,
            numpad: true,
            lightbar: false,
            cpu_pl_control: false,
        });
    }

    if has("PHN")
        || has("AN14")
        || has("AN16")
        || has("AN17")
        || has("AN18")
        || has("PH315")
        || has("PH317")
        || has("NITRO")
        || has("AN515")
        || has("AN517")
    {
        return Some(Heuristic {
            tier: TIER_FOUR,
            fans: FANS_DUAL,
            numpad: true,
            lightbar: false,
            cpu_pl_control: false,
        });
    }

    Some(generic(&upper))
}

/// The generic fallback entry: four-zone lighting, dual fan, no PL control,
/// and numpad omitted for small "Neo/Triton/Nitro/PHN/PT/AN" 14-inch bodies.
pub fn generic(model: &str) -> Heuristic {
    let upper = model.trim().to_uppercase();
    let fourteen_inch = upper.contains("14")
        && (upper.contains("NEO")
            || upper.contains("TRITON")
            || upper.contains("NITRO")
            || upper.contains("PHN")
            || upper.contains("PT")
            || upper.contains("AN"));

    Heuristic {
        tier: TIER_FOUR,
        fans: FANS_DUAL,
        numpad: !fourteen_inch,
        lightbar: false,
        cpu_pl_control: false,
    }
}

/// Returns 1 and fills the out-params when the model has a heuristic class,
/// 0 when the input is empty, and -1 on invalid arguments.
#[no_mangle]
pub unsafe extern "C" fn predator_catalog_heuristic(
    model_ptr: *const u8,
    model_len: usize,
    out_tier: *mut i32,
    out_fans: *mut i32,
    out_numpad: *mut i32,
    out_lightbar: *mut i32,
    out_pl: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !crate::is_valid_mut_ptr(out_tier)
            || !crate::is_valid_mut_ptr(out_fans)
            || !crate::is_valid_mut_ptr(out_numpad)
            || !crate::is_valid_mut_ptr(out_lightbar)
            || !crate::is_valid_mut_ptr(out_pl)
        {
            return -1;
        }
        if model_len > 0
            && (model_ptr.is_null() || !crate::is_valid_buffer_range(model_ptr, model_len))
        {
            return -1;
        }
        let bytes = if model_len == 0 {
            &[][..]
        } else {
            unsafe { std::slice::from_raw_parts(model_ptr, model_len) }
        };
        let text = match std::str::from_utf8(bytes) {
            Ok(value) => value,
            Err(_) => return -2,
        };
        match classify(text) {
            Some(h) => {
                unsafe {
                    *out_tier = h.tier;
                    *out_fans = h.fans;
                    *out_numpad = if h.numpad { 1 } else { 0 };
                    *out_lightbar = if h.lightbar { 1 } else { 0 };
                    *out_pl = if h.cpu_pl_control { 1 } else { 0 };
                }
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
    fn empty_model_has_no_class() {
        assert!(classify("").is_none());
        assert!(classify("   ").is_none());
    }

    #[test]
    fn ptx17_is_per_key_triple_fan() {
        let h = classify("Predator Helios Neo PTX17-71").unwrap();
        assert_eq!(h.tier, TIER_PER_KEY);
        assert_eq!(h.fans, FANS_TRIPLE);
        assert!(!h.lightbar);
    }

    #[test]
    fn ph18_is_per_key_dual_fan() {
        let h = classify("PH18-71").unwrap();
        assert_eq!(h.tier, TIER_PER_KEY);
        assert_eq!(h.fans, FANS_DUAL);
        assert!(h.lightbar);
    }

    #[test]
    fn pt516_is_three_zone_triple_fan() {
        let h = classify("Predator Triton PT516-52s").unwrap();
        assert_eq!(h.tier, TIER_THREE);
        assert_eq!(h.fans, FANS_TRIPLE);
    }

    #[test]
    fn nitro_v_is_single_zone() {
        let h = classify("ANV15-51").unwrap();
        assert_eq!(h.tier, TIER_SINGLE);
        assert!(!h.cpu_pl_control);
    }

    #[test]
    fn phn_is_four_zone() {
        let h = classify("PHN16-71").unwrap();
        assert_eq!(h.tier, TIER_FOUR);
        assert_eq!(h.fans, FANS_DUAL);
    }

    #[test]
    fn unknown_model_falls_back_to_generic() {
        let h = classify("Some Unknown Laptop 9000").unwrap();
        assert_eq!(h.tier, TIER_FOUR);
        assert_eq!(h.fans, FANS_DUAL);
        assert!(h.numpad);
        assert!(!h.cpu_pl_control);
    }

    #[test]
    fn small_neo_body_loses_the_numpad() {
        assert!(!generic("Predator Triton Neo 14").numpad);
        assert!(generic("Predator Helios 16").numpad);
    }
}
