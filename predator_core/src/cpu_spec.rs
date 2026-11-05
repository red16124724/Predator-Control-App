//! CPU architecture detection and default power limits engine ported from AcerHardwareProtocols.cs.

/// CPU architecture classification matching Acer hardware specification.
#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum CpuArchitectureFamily {
    #[default]
    IntelCoreUltraSeries1 = 0,
    IntelCoreUltraSeries2 = 1,
    IntelCoreHx = 2,
    IntelCoreH = 3,
    IntelCoreU = 4,
    IntelCoreDesktop = 5,
    AmdRyzenHx = 6,
    AmdRyzenHs = 7,
    AmdRyzenH = 8,
    AmdRyzenU = 9,
    GenericIntel = 10,
    GenericAmd = 11,
    Unknown = 12,
}

/// Power limit pair for sustained (PL1) and burst (PL2) limits in Watts.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PowerPair {
    pub pl1_watts: i32,
    pub pl2_watts: i32,
}

/// Hardware specification and mode default power limits for a detected CPU.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct CpuHardwareSpec {
    pub family: CpuArchitectureFamily,
    pub base_tdp_watts: i32,
    pub min_power_watts: i32,
    pub max_pl1_watts: i32,
    pub max_pl2_watts: i32,
    pub eco: PowerPair,         // Mode 0x06
    pub quiet: PowerPair,       // Mode 0x00
    pub balanced: PowerPair,    // Mode 0x01
    pub performance: PowerPair, // Mode 0x04
    pub turbo: PowerPair,       // Mode 0x05
}

/// Compatibility alias.
pub type NativeCpuSpec = CpuHardwareSpec;

impl CpuHardwareSpec {
    /// Retrieves (PL1, PL2) for a given Acer mode byte.
    /// Mode mapping:
    /// - 0x00: Quiet
    /// - 0x01: Balanced
    /// - 0x04: Performance
    /// - 0x05: Turbo
    /// - 0x06: Eco
    pub fn get_mode_limits(&self, mode: u8) -> (i32, i32) {
        match mode {
            0x00 => (self.quiet.pl1_watts, self.quiet.pl2_watts),
            0x01 => (self.balanced.pl1_watts, self.balanced.pl2_watts),
            0x04 => (self.performance.pl1_watts, self.performance.pl2_watts),
            0x05 => (self.turbo.pl1_watts, self.turbo.pl2_watts),
            0x06 => (self.eco.pl1_watts, self.eco.pl2_watts),
            _ => (self.balanced.pl1_watts, self.balanced.pl2_watts),
        }
    }
}

/// Computes Acer standard mode defaults based on base TDP and safety power limits.
/// Returns (eco, quiet, balanced, performance, turbo).
pub fn compute_mode_defaults(
    base_tdp: i32,
    min_power: i32,
    max_pl1: i32,
    max_pl2: i32,
) -> (PowerPair, PowerPair, PowerPair, PowerPair, PowerPair) {
    let mut safe_max_pl2 = min_power.max(max_pl2);
    let safe_max_pl1 = max_pl1.clamp(min_power, safe_max_pl2);
    safe_max_pl2 = safe_max_pl2.max(safe_max_pl1);

    let eco_pl1 = ((base_tdp as f64 * 0.55).round() as i32).clamp(min_power, safe_max_pl1);
    let eco_pl2 = ((base_tdp as f64 * 0.75).round() as i32).clamp(eco_pl1, safe_max_pl2);

    let quiet_pl1 = ((base_tdp as f64 * 0.75).round() as i32).clamp(min_power, safe_max_pl1);
    let quiet_pl2 = base_tdp.clamp(quiet_pl1, safe_max_pl2);

    let balanced_pl1 = base_tdp.clamp(min_power, safe_max_pl1);
    let balanced_pl2 = ((base_tdp as f64 * 1.45).round() as i32).clamp(balanced_pl1, safe_max_pl2);

    let perf_pl1 = ((base_tdp as f64 * 1.55).round() as i32).clamp(balanced_pl1, safe_max_pl1);
    let perf_pl2 = ((base_tdp as f64 * 2.00).round() as i32).clamp(perf_pl1, safe_max_pl2);

    let turbo_pl1 = ((base_tdp as f64 * 2.00).round() as i32).clamp(perf_pl1, safe_max_pl1);
    let turbo_pl2 = turbo_pl1.max(safe_max_pl2);

    (
        PowerPair {
            pl1_watts: eco_pl1,
            pl2_watts: eco_pl2,
        },
        PowerPair {
            pl1_watts: quiet_pl1,
            pl2_watts: quiet_pl2,
        },
        PowerPair {
            pl1_watts: balanced_pl1,
            pl2_watts: balanced_pl2,
        },
        PowerPair {
            pl1_watts: perf_pl1,
            pl2_watts: perf_pl2,
        },
        PowerPair {
            pl1_watts: turbo_pl1,
            pl2_watts: turbo_pl2,
        },
    )
}

/// Estimates proportional limits for any TDP and mode byte.
pub fn estimate_proportional_limits(tdp_watts: i32, mode: u8) -> (i32, i32) {
    let tdp = tdp_watts.clamp(5, 250);
    let min_p = 5.max((tdp as f64 * 0.20).round() as i32);
    let max_p2 = (tdp as f64 * 2.55).round() as i32;
    let max_p1 = (max_p2 as f64 * 0.85).round() as i32;
    let (eco, quiet, balanced, perf, turbo) = compute_mode_defaults(tdp, min_p, max_p1, max_p2);
    match mode {
        0x00 => (quiet.pl1_watts, quiet.pl2_watts),
        0x01 => (balanced.pl1_watts, balanced.pl2_watts),
        0x04 => (perf.pl1_watts, perf.pl2_watts),
        0x05 => (turbo.pl1_watts, turbo.pl2_watts),
        0x06 => (eco.pl1_watts, eco.pl2_watts),
        _ => (tdp, (tdp as f64 * 1.35).round() as i32),
    }
}

pub fn estimate_pl1(tdp_watts: i32, mode: u8) -> i32 {
    estimate_proportional_limits(tdp_watts, mode).0
}

pub fn estimate_pl2(tdp_watts: i32, mode: u8) -> i32 {
    estimate_proportional_limits(tdp_watts, mode).1
}

// --- Fast Substring and Token Matching Utilities ---

fn is_word_char(c: u8) -> bool {
    c.is_ascii_alphanumeric() || c == b'_'
}

fn contains_word(text: &str, token: &str) -> bool {
    let b = text.as_bytes();
    let tb = token.as_bytes();
    if tb.is_empty() || b.len() < tb.len() {
        return false;
    }
    for i in 0..=(b.len() - tb.len()) {
        if &b[i..i + tb.len()] == tb {
            let left_ok = i == 0 || !is_word_char(b[i - 1]);
            let right_ok = (i + tb.len() == b.len()) || !is_word_char(b[i + tb.len()]);
            if left_ok && right_ok {
                return true;
            }
        }
    }
    false
}

/// Matches model numbers ending with the given suffix (e.g. "14900HX", "7840HS", "13700H").
fn has_model_suffix(text: &str, suffix: &str) -> bool {
    for part in
        text.split(|c: char| c.is_whitespace() || c == '(' || c == ')' || c == ',' || c == '/')
    {
        let p = part.trim_start_matches(['i', 'I', '-']);
        if let Some(prefix) = p.strip_suffix(suffix) {
            let num_part = prefix.trim_start_matches(['3', '5', '7', '9', '-']);
            if num_part.chars().any(|c| c.is_ascii_digit()) {
                return true;
            }
        }
    }
    false
}

/// Detects CPU architecture family and power limits from model name string.
pub fn detect_cpu_spec(model_str: &str) -> CpuHardwareSpec {
    let upper = model_str.trim().to_ascii_uppercase();

    // 1. AMD Ryzen architectures
    if upper.contains("RYZEN") || upper.contains("AMD") {
        let (family, base_tdp, min_power, max_pl1, max_pl2) = if upper.contains("HX3D")
            || upper.contains("AI 9 HX")
            || contains_word(&upper, "HX")
            || has_model_suffix(&upper, "HX")
            || has_model_suffix(&upper, "HX3D")
        {
            (CpuArchitectureFamily::AmdRyzenHx, 55, 15, 125, 140)
        } else if contains_word(&upper, "HS") || has_model_suffix(&upper, "HS") {
            (CpuArchitectureFamily::AmdRyzenHs, 35, 15, 65, 80)
        } else if contains_word(&upper, "H") || has_model_suffix(&upper, "H") {
            (CpuArchitectureFamily::AmdRyzenH, 45, 12, 80, 95)
        } else if contains_word(&upper, "U") || has_model_suffix(&upper, "U") {
            (CpuArchitectureFamily::AmdRyzenU, 15, 5, 30, 45)
        } else {
            (CpuArchitectureFamily::GenericAmd, 45, 12, 80, 95)
        };

        let (eco, quiet, balanced, performance, turbo) =
            compute_mode_defaults(base_tdp, min_power, max_pl1, max_pl2);

        return CpuHardwareSpec {
            family,
            base_tdp_watts: base_tdp,
            min_power_watts: min_power,
            max_pl1_watts: max_pl1,
            max_pl2_watts: max_pl2,
            eco,
            quiet,
            balanced,
            performance,
            turbo,
        };
    }

    // 2. Intel Core Ultra Series 2 & Series 1
    if upper.contains("ULTRA") {
        let is_series_2 = check_ultra_series_2(&upper);
        let is_series_1 = check_ultra_series_1(&upper);

        let (family, base_tdp, min_power, max_pl1, max_pl2) = if is_series_2 {
            if upper.contains('V') {
                // Lunar Lake (e.g. Ultra 7 258V, 268V, 256V, 226V)
                (CpuArchitectureFamily::IntelCoreUltraSeries2, 17, 8, 30, 37)
            } else if upper.contains("HX") {
                // Arrow Lake HX (e.g. Ultra 9 285HX, 275HX, 265HX)
                (
                    CpuArchitectureFamily::IntelCoreUltraSeries2,
                    55,
                    15,
                    157,
                    175,
                )
            } else if contains_word(&upper, "U") || has_model_suffix(&upper, "U") {
                // Arrow Lake U (e.g. Ultra 7 255U)
                (CpuArchitectureFamily::IntelCoreUltraSeries2, 15, 5, 28, 57)
            } else {
                // Arrow Lake H (e.g. Ultra 9 285H, Ultra 7 265H, Ultra 5 245H, Ultra 5 225H)
                (CpuArchitectureFamily::IntelCoreUltraSeries2, 28, 8, 64, 115)
            }
        } else if is_series_1 {
            if contains_word(&upper, "U") || has_model_suffix(&upper, "U") {
                // Meteor Lake U (e.g. Ultra 7 165U, 155U, 135U, 125U)
                (CpuArchitectureFamily::IntelCoreUltraSeries1, 15, 5, 28, 57)
            } else {
                // Meteor Lake H (e.g. Ultra 9 185H, 165H, 155H, 135H, 125H)
                (CpuArchitectureFamily::IntelCoreUltraSeries1, 28, 8, 64, 115)
            }
        } else {
            // Generic Core Ultra without explicit SKU
            (CpuArchitectureFamily::IntelCoreUltraSeries1, 28, 8, 64, 115)
        };

        let (eco, quiet, balanced, performance, turbo) =
            compute_mode_defaults(base_tdp, min_power, max_pl1, max_pl2);

        return CpuHardwareSpec {
            family,
            base_tdp_watts: base_tdp,
            min_power_watts: min_power,
            max_pl1_watts: max_pl1,
            max_pl2_watts: max_pl2,
            eco,
            quiet,
            balanced,
            performance,
            turbo,
        };
    }

    // 3. Intel 12th/13th/14th Gen HX (e.g. 14900HX, 13700HX, 12900HX)
    if contains_word(&upper, "HX") || has_model_suffix(&upper, "HX") {
        let is_i9 = upper.contains("I9")
            || upper.contains("14900")
            || upper.contains("13900")
            || upper.contains("13980")
            || upper.contains("13950")
            || upper.contains("12900");
        let max_pl2 = if is_i9 { 175 } else { 160 };

        let (eco, quiet, balanced, performance, turbo) =
            compute_mode_defaults(55, 15, 157, max_pl2);

        return CpuHardwareSpec {
            family: CpuArchitectureFamily::IntelCoreHx,
            base_tdp_watts: 55,
            min_power_watts: 15,
            max_pl1_watts: 157,
            max_pl2_watts: max_pl2,
            eco,
            quiet,
            balanced,
            performance,
            turbo,
        };
    }

    // 4. Intel 10th-14th Gen H / HK (e.g. 14700H, 13700H, 12700H, 11800H)
    if contains_word(&upper, "HK")
        || contains_word(&upper, "H")
        || has_model_suffix(&upper, "HK")
        || has_model_suffix(&upper, "H")
    {
        let is_high_tier = upper.contains("I9") || upper.contains("I7");
        let max_pl1 = if is_high_tier { 95 } else { 80 };
        let max_pl2 = if is_high_tier { 115 } else { 95 };

        let (eco, quiet, balanced, performance, turbo) =
            compute_mode_defaults(45, 12, max_pl1, max_pl2);

        return CpuHardwareSpec {
            family: CpuArchitectureFamily::IntelCoreH,
            base_tdp_watts: 45,
            min_power_watts: 12,
            max_pl1_watts: max_pl1,
            max_pl2_watts: max_pl2,
            eco,
            quiet,
            balanced,
            performance,
            turbo,
        };
    }

    // 5. Intel Desktop Unlocked (K / KF / KS)
    if contains_word(&upper, "K")
        || contains_word(&upper, "KF")
        || contains_word(&upper, "KS")
        || has_model_suffix(&upper, "K")
        || has_model_suffix(&upper, "KF")
        || has_model_suffix(&upper, "KS")
    {
        let (eco, quiet, balanced, performance, turbo) = compute_mode_defaults(125, 35, 253, 253);

        return CpuHardwareSpec {
            family: CpuArchitectureFamily::IntelCoreDesktop,
            base_tdp_watts: 125,
            min_power_watts: 35,
            max_pl1_watts: 253,
            max_pl2_watts: 253,
            eco,
            quiet,
            balanced,
            performance,
            turbo,
        };
    }

    // 6. Intel U-series / Thin Laptops (e.g. 1335U, 1235U, 1165G7)
    if contains_word(&upper, "U")
        || has_model_suffix(&upper, "U")
        || upper.contains("G7")
        || upper.contains("G4")
        || upper.contains("G1")
    {
        let (eco, quiet, balanced, performance, turbo) = compute_mode_defaults(15, 5, 30, 55);

        return CpuHardwareSpec {
            family: CpuArchitectureFamily::IntelCoreU,
            base_tdp_watts: 15,
            min_power_watts: 5,
            max_pl1_watts: 30,
            max_pl2_watts: 55,
            eco,
            quiet,
            balanced,
            performance,
            turbo,
        };
    }

    // 7. Generic Intel fallback
    let (eco, quiet, balanced, performance, turbo) = compute_mode_defaults(45, 12, 95, 115);

    CpuHardwareSpec {
        family: CpuArchitectureFamily::GenericIntel,
        base_tdp_watts: 45,
        min_power_watts: 12,
        max_pl1_watts: 95,
        max_pl2_watts: 115,
        eco,
        quiet,
        balanced,
        performance,
        turbo,
    }
}

fn check_ultra_series_2(upper: &str) -> bool {
    let bytes = upper.as_bytes();
    for i in 0..bytes.len() {
        if bytes[i] == b'2'
            && i + 2 < bytes.len()
            && bytes[i + 1].is_ascii_digit()
            && bytes[i + 2].is_ascii_digit()
        {
            let left_ok = i == 0 || !bytes[i - 1].is_ascii_alphanumeric();
            if left_ok {
                return true;
            }
        }
    }
    false
}

fn check_ultra_series_1(upper: &str) -> bool {
    let bytes = upper.as_bytes();
    for i in 0..bytes.len() {
        if bytes[i] == b'1'
            && i + 2 < bytes.len()
            && bytes[i + 1].is_ascii_digit()
            && bytes[i + 2].is_ascii_digit()
        {
            let left_ok = i == 0 || !bytes[i - 1].is_ascii_alphanumeric();
            if left_ok {
                return true;
            }
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_cpu_detection_intel_ultra_series_2() {
        // Lunar Lake
        let spec_lunar = detect_cpu_spec("Intel Core Ultra 7 258V");
        assert_eq!(
            spec_lunar.family,
            CpuArchitectureFamily::IntelCoreUltraSeries2
        );
        assert_eq!(spec_lunar.base_tdp_watts, 17);
        assert_eq!(spec_lunar.min_power_watts, 8);
        assert_eq!(spec_lunar.max_pl1_watts, 30);
        assert_eq!(spec_lunar.max_pl2_watts, 37);

        // Arrow Lake HX
        let spec_arrow_hx = detect_cpu_spec("Intel Core Ultra 9 285HX");
        assert_eq!(
            spec_arrow_hx.family,
            CpuArchitectureFamily::IntelCoreUltraSeries2
        );
        assert_eq!(spec_arrow_hx.base_tdp_watts, 55);
        assert_eq!(spec_arrow_hx.max_pl2_watts, 175);

        // Arrow Lake U
        let spec_arrow_u = detect_cpu_spec("Intel Core Ultra 7 255U");
        assert_eq!(
            spec_arrow_u.family,
            CpuArchitectureFamily::IntelCoreUltraSeries2
        );
        assert_eq!(spec_arrow_u.base_tdp_watts, 15);
        assert_eq!(spec_arrow_u.max_pl2_watts, 57);

        // Arrow Lake H
        let spec_arrow_h = detect_cpu_spec("Intel Core Ultra 7 265H");
        assert_eq!(
            spec_arrow_h.family,
            CpuArchitectureFamily::IntelCoreUltraSeries2
        );
        assert_eq!(spec_arrow_h.base_tdp_watts, 28);
        assert_eq!(spec_arrow_h.max_pl1_watts, 64);
        assert_eq!(spec_arrow_h.max_pl2_watts, 115);
    }

    #[test]
    fn test_cpu_detection_intel_ultra_series_1() {
        // Meteor Lake U
        let spec_meteor_u = detect_cpu_spec("Intel Core Ultra 7 155U");
        assert_eq!(
            spec_meteor_u.family,
            CpuArchitectureFamily::IntelCoreUltraSeries1
        );
        assert_eq!(spec_meteor_u.base_tdp_watts, 15);
        assert_eq!(spec_meteor_u.max_pl2_watts, 57);

        // Meteor Lake H
        let spec_meteor_h = detect_cpu_spec("Intel Core Ultra 9 185H");
        assert_eq!(
            spec_meteor_h.family,
            CpuArchitectureFamily::IntelCoreUltraSeries1
        );
        assert_eq!(spec_meteor_h.base_tdp_watts, 28);
        assert_eq!(spec_meteor_h.max_pl1_watts, 64);
        assert_eq!(spec_meteor_h.max_pl2_watts, 115);

        // Generic Core Ultra
        let spec_generic = detect_cpu_spec("Intel Core Ultra Processor");
        assert_eq!(
            spec_generic.family,
            CpuArchitectureFamily::IntelCoreUltraSeries1
        );
        assert_eq!(spec_generic.base_tdp_watts, 28);
    }

    #[test]
    fn test_cpu_detection_intel_hx_h_desktop_u() {
        // i9 HX
        let spec_i9_hx = detect_cpu_spec("13th Gen Intel Core i9-13900HX");
        assert_eq!(spec_i9_hx.family, CpuArchitectureFamily::IntelCoreHx);
        assert_eq!(spec_i9_hx.base_tdp_watts, 55);
        assert_eq!(spec_i9_hx.max_pl2_watts, 175);

        // i7 HX
        let spec_i7_hx = detect_cpu_spec("Intel Core i7-13700HX");
        assert_eq!(spec_i7_hx.family, CpuArchitectureFamily::IntelCoreHx);
        assert_eq!(spec_i7_hx.base_tdp_watts, 55);
        assert_eq!(spec_i7_hx.max_pl2_watts, 160);

        // i7 H
        let spec_i7_h = detect_cpu_spec("Intel Core i7-14700H");
        assert_eq!(spec_i7_h.family, CpuArchitectureFamily::IntelCoreH);
        assert_eq!(spec_i7_h.base_tdp_watts, 45);
        assert_eq!(spec_i7_h.max_pl1_watts, 95);
        assert_eq!(spec_i7_h.max_pl2_watts, 115);

        // i5 H
        let spec_i5_h = detect_cpu_spec("Intel Core i5-13500H");
        assert_eq!(spec_i5_h.family, CpuArchitectureFamily::IntelCoreH);
        assert_eq!(spec_i5_h.base_tdp_watts, 45);
        assert_eq!(spec_i5_h.max_pl1_watts, 80);
        assert_eq!(spec_i5_h.max_pl2_watts, 95);

        // Desktop K
        let spec_k = detect_cpu_spec("Intel Core i9-14900K");
        assert_eq!(spec_k.family, CpuArchitectureFamily::IntelCoreDesktop);
        assert_eq!(spec_k.base_tdp_watts, 125);
        assert_eq!(spec_k.max_pl2_watts, 253);

        // U-series
        let spec_u = detect_cpu_spec("Intel Core i7-1355U");
        assert_eq!(spec_u.family, CpuArchitectureFamily::IntelCoreU);
        assert_eq!(spec_u.base_tdp_watts, 15);
        assert_eq!(spec_u.max_pl2_watts, 55);
    }

    #[test]
    fn test_cpu_detection_amd_ryzen() {
        // Ryzen HX
        let spec_hx = detect_cpu_spec("AMD Ryzen 9 7945HX");
        assert_eq!(spec_hx.family, CpuArchitectureFamily::AmdRyzenHx);
        assert_eq!(spec_hx.base_tdp_watts, 55);
        assert_eq!(spec_hx.max_pl1_watts, 125);
        assert_eq!(spec_hx.max_pl2_watts, 140);

        // Ryzen HX3D
        let spec_hx3d = detect_cpu_spec("AMD Ryzen 9 7945HX3D");
        assert_eq!(spec_hx3d.family, CpuArchitectureFamily::AmdRyzenHx);

        // Ryzen AI 9 HX
        let spec_ai9 = detect_cpu_spec("AMD Ryzen AI 9 HX 370");
        assert_eq!(spec_ai9.family, CpuArchitectureFamily::AmdRyzenHx);

        // Ryzen HS
        let spec_hs = detect_cpu_spec("AMD Ryzen 7 7840HS");
        assert_eq!(spec_hs.family, CpuArchitectureFamily::AmdRyzenHs);
        assert_eq!(spec_hs.base_tdp_watts, 35);
        assert_eq!(spec_hs.max_pl1_watts, 65);
        assert_eq!(spec_hs.max_pl2_watts, 80);

        // Ryzen H
        let spec_h = detect_cpu_spec("AMD Ryzen 7 6800H");
        assert_eq!(spec_h.family, CpuArchitectureFamily::AmdRyzenH);
        assert_eq!(spec_h.base_tdp_watts, 45);

        // Ryzen U
        let spec_u = detect_cpu_spec("AMD Ryzen 7 7730U");
        assert_eq!(spec_u.family, CpuArchitectureFamily::AmdRyzenU);
        assert_eq!(spec_u.base_tdp_watts, 15);
        assert_eq!(spec_u.max_pl2_watts, 45);

        // Generic AMD
        let spec_gen = detect_cpu_spec("AMD Processor");
        assert_eq!(spec_gen.family, CpuArchitectureFamily::GenericAmd);
    }

    #[test]
    fn test_mode_defaults_monotonicity_and_modes() {
        let spec = detect_cpu_spec("Intel Core i7-13700H");

        // PL1 monotonicity
        assert!(spec.eco.pl1_watts <= spec.quiet.pl1_watts);
        assert!(spec.quiet.pl1_watts <= spec.balanced.pl1_watts);
        assert!(spec.balanced.pl1_watts <= spec.performance.pl1_watts);
        assert!(spec.performance.pl1_watts <= spec.turbo.pl1_watts);

        // PL2 monotonicity
        assert!(spec.eco.pl2_watts <= spec.quiet.pl2_watts);
        assert!(spec.quiet.pl2_watts <= spec.balanced.pl2_watts);
        assert!(spec.balanced.pl2_watts <= spec.performance.pl2_watts);
        assert!(spec.performance.pl2_watts <= spec.turbo.pl2_watts);

        // Mode getter checks
        assert_eq!(
            spec.get_mode_limits(0x00),
            (spec.quiet.pl1_watts, spec.quiet.pl2_watts)
        );
        assert_eq!(
            spec.get_mode_limits(0x01),
            (spec.balanced.pl1_watts, spec.balanced.pl2_watts)
        );
        assert_eq!(
            spec.get_mode_limits(0x04),
            (spec.performance.pl1_watts, spec.performance.pl2_watts)
        );
        assert_eq!(
            spec.get_mode_limits(0x05),
            (spec.turbo.pl1_watts, spec.turbo.pl2_watts)
        );
        assert_eq!(
            spec.get_mode_limits(0x06),
            (spec.eco.pl1_watts, spec.eco.pl2_watts)
        );
    }
}
