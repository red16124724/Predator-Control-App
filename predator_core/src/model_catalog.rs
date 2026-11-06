//! Laptop model catalog for Acer Predator and Nitro laptop product families.
//!
//! Provides static lookup and heuristic classification for hardware capabilities:
//! - Keyboard lighting tier (SingleZone, ThreeZone, FourZone, PerKey)
//! - Fan cooling topology (DualFan, TripleFan)
//! - Launch year

#[repr(i32)]
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum LightingTier {
    Unavailable = 0,
    SingleZone = 1,
    ThreeZone = 3,
    FourZone = 4,
    PerKey = 5,
}

#[repr(i32)]
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum FanTopology {
    DualFan = 2,
    TripleFan = 3,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ModelEntry {
    pub pattern: &'static str,
    pub marketing_name: &'static str,
    pub launch_year: i32,
    pub tier: LightingTier,
    pub fans: FanTopology,
    pub rear_lightbar: bool,
    pub cpu_pl_control: bool,
}

pub static CATALOG_ENTRIES: &[ModelEntry] = &[
    // Predator Helios 16
    ModelEntry {
        pattern: "PH16-71",
        marketing_name: "Predator Helios 16 (2023)",
        launch_year: 2023,
        tier: LightingTier::PerKey,
        fans: FanTopology::DualFan,
        rear_lightbar: true,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PH16-72",
        marketing_name: "Predator Helios 16 (2024)",
        launch_year: 2024,
        tier: LightingTier::PerKey,
        fans: FanTopology::DualFan,
        rear_lightbar: true,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PH16-73",
        marketing_name: "Predator Helios 16 (2025)",
        launch_year: 2025,
        tier: LightingTier::PerKey,
        fans: FanTopology::DualFan,
        rear_lightbar: true,
        cpu_pl_control: true,
    },
    // Predator Helios 18
    ModelEntry {
        pattern: "PH18-71",
        marketing_name: "Predator Helios 18 (2023)",
        launch_year: 2023,
        tier: LightingTier::PerKey,
        fans: FanTopology::DualFan,
        rear_lightbar: true,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PH18-72",
        marketing_name: "Predator Helios 18 (2024)",
        launch_year: 2024,
        tier: LightingTier::PerKey,
        fans: FanTopology::DualFan,
        rear_lightbar: true,
        cpu_pl_control: true,
    },
    // Predator Helios 300
    ModelEntry {
        pattern: "PH315-53",
        marketing_name: "Predator Helios 300 (2020)",
        launch_year: 2020,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PH315-54",
        marketing_name: "Predator Helios 300 (2021)",
        launch_year: 2021,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PH315-55",
        marketing_name: "Predator Helios 300 (2022)",
        launch_year: 2022,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: true,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PH317-54",
        marketing_name: "Predator Helios 300 17\" (2020)",
        launch_year: 2020,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PH317-55",
        marketing_name: "Predator Helios 300 17\" (2021)",
        launch_year: 2021,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PH317-56",
        marketing_name: "Predator Helios 300 17\" (2022)",
        launch_year: 2022,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: true,
        cpu_pl_control: true,
    },
    // Predator Helios Neo
    ModelEntry {
        pattern: "PHN16-71",
        marketing_name: "Predator Helios Neo 16 (2023)",
        launch_year: 2023,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PHN16-72",
        marketing_name: "Predator Helios Neo 16 (2024)",
        launch_year: 2024,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PHN18-71",
        marketing_name: "Predator Helios Neo 18 (2024)",
        launch_year: 2024,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PHN14-51",
        marketing_name: "Predator Helios Neo 14 (2024)",
        launch_year: 2024,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    // Predator Triton
    ModelEntry {
        pattern: "PT314-51",
        marketing_name: "Predator Triton 300 SE 14\" (2021)",
        launch_year: 2021,
        tier: LightingTier::ThreeZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PT314-52",
        marketing_name: "Predator Triton 300 SE 14\" (2022)",
        launch_year: 2022,
        tier: LightingTier::ThreeZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PT316-51",
        marketing_name: "Predator Triton 300 SE 16\" (2022)",
        launch_year: 2022,
        tier: LightingTier::ThreeZone,
        fans: FanTopology::TripleFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PT516-51",
        marketing_name: "Predator Triton 500 SE (2021)",
        launch_year: 2021,
        tier: LightingTier::ThreeZone,
        fans: FanTopology::TripleFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PT516-52",
        marketing_name: "Predator Triton 500 SE (2022)",
        launch_year: 2022,
        tier: LightingTier::ThreeZone,
        fans: FanTopology::TripleFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PT14-51",
        marketing_name: "Predator Triton 14 (2023)",
        launch_year: 2023,
        tier: LightingTier::ThreeZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PT16-71",
        marketing_name: "Predator Triton 16 (2023)",
        launch_year: 2023,
        tier: LightingTier::PerKey,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "PTX17-71",
        marketing_name: "Predator Triton 17 X (2023)",
        launch_year: 2023,
        tier: LightingTier::PerKey,
        fans: FanTopology::TripleFan,
        rear_lightbar: true,
        cpu_pl_control: true,
    },
    // Acer Nitro 5 / 16 / 17
    ModelEntry {
        pattern: "AN515-55",
        marketing_name: "Nitro 5 (2020)",
        launch_year: 2020,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "AN515-56",
        marketing_name: "Nitro 5 (2021)",
        launch_year: 2021,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "AN515-57",
        marketing_name: "Nitro 5 (2021)",
        launch_year: 2021,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "AN515-58",
        marketing_name: "Nitro 5 (2022)",
        launch_year: 2022,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "AN517-54",
        marketing_name: "Nitro 5 17\" (2021)",
        launch_year: 2021,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "AN517-55",
        marketing_name: "Nitro 5 17\" (2022)",
        launch_year: 2022,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "AN16-41",
        marketing_name: "Nitro 16 AMD (2023)",
        launch_year: 2023,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: false,
    },
    ModelEntry {
        pattern: "AN16-51",
        marketing_name: "Nitro 16 Intel (2023)",
        launch_year: 2023,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "AN17-41",
        marketing_name: "Nitro 17 AMD (2023)",
        launch_year: 2023,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: false,
    },
    ModelEntry {
        pattern: "AN17-51",
        marketing_name: "Nitro 17 Intel (2023)",
        launch_year: 2023,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "AN14-41",
        marketing_name: "Nitro 14 AMD (2024)",
        launch_year: 2024,
        tier: LightingTier::FourZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: false,
    },
    // Nitro V (SingleZone)
    ModelEntry {
        pattern: "ANV15-51",
        marketing_name: "Nitro V 15 (2023)",
        launch_year: 2023,
        tier: LightingTier::SingleZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: true,
    },
    ModelEntry {
        pattern: "ANV16-41",
        marketing_name: "Nitro V 16 (2024)",
        launch_year: 2024,
        tier: LightingTier::SingleZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: false,
    },
    ModelEntry {
        pattern: "ANV14-61",
        marketing_name: "Nitro V 14 (2024)",
        launch_year: 2024,
        tier: LightingTier::SingleZone,
        fans: FanTopology::DualFan,
        rear_lightbar: false,
        cpu_pl_control: false,
    },
];

/// Looks up an Acer Predator or Nitro laptop model string and returns (tier, fans, launch_year).
pub fn lookup(model_name: &str) -> (LightingTier, FanTopology, i32) {
    let clean = model_name.trim();
    if clean.is_empty() {
        return (LightingTier::Unavailable, FanTopology::DualFan, 2024);
    }

    let upper = clean.to_uppercase();

    // 1. Exact catalog match via pattern substring
    for entry in CATALOG_ENTRIES {
        let pattern_alt = entry.pattern.replace('-', "_");
        if upper.contains(entry.pattern) || upper.contains(&pattern_alt) {
            return (entry.tier, entry.fans, entry.launch_year);
        }
    }

    // 2. Heuristic rules for unrecognized / future revisions
    if upper.contains("PH16") || upper.contains("PH18") || upper.contains("PTX17") {
        let is_triple = upper.contains("PTX17");
        return (
            LightingTier::PerKey,
            if is_triple {
                FanTopology::TripleFan
            } else {
                FanTopology::DualFan
            },
            2024,
        );
    }

    if upper.contains("PT314")
        || upper.contains("PT516")
        || upper.contains("PT14")
        || upper.contains("PT316")
    {
        let is_triple = upper.contains("PT516") || upper.contains("PT316");
        return (
            LightingTier::ThreeZone,
            if is_triple {
                FanTopology::TripleFan
            } else {
                FanTopology::DualFan
            },
            2024,
        );
    }

    if upper.contains("ANV15")
        || upper.contains("ANV16")
        || upper.contains("ANV14")
        || upper.contains("NITRO V")
    {
        return (LightingTier::SingleZone, FanTopology::DualFan, 2024);
    }

    if upper.contains("PHN")
        || upper.contains("AN14")
        || upper.contains("AN16")
        || upper.contains("AN17")
        || upper.contains("AN18")
        || upper.contains("PH315")
        || upper.contains("PH317")
        || upper.contains("NITRO")
        || upper.contains("AN515")
        || upper.contains("AN517")
    {
        return (LightingTier::FourZone, FanTopology::DualFan, 2024);
    }

    (LightingTier::FourZone, FanTopology::DualFan, 2024)
}

#[no_mangle]
pub unsafe extern "C" fn predator_model_catalog_lookup(
    model_ptr: *const u8,
    model_len: usize,
    out_tier: *mut i32,
    out_fans: *mut i32,
    out_year: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if model_ptr.is_null()
            || model_len == 0
            || model_len > 4096
            || !crate::is_valid_buffer_range(model_ptr, model_len)
            || !crate::is_valid_mut_ptr(out_tier)
            || !crate::is_valid_mut_ptr(out_fans)
            || !crate::is_valid_mut_ptr(out_year)
        {
            return -1;
        }

        let slice = unsafe { std::slice::from_raw_parts(model_ptr, model_len) };
        let model_str = match std::str::from_utf8(slice) {
            Ok(s) => s,
            Err(_) => return -2,
        };

        let (tier, fans, year) = lookup(model_str);

        unsafe {
            std::ptr::write_unaligned(out_tier, tier as i32);
            std::ptr::write_unaligned(out_fans, fans as i32);
            std::ptr::write_unaligned(out_year, year);
        }

        0
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_model_catalog_exact_lookups() {
        // Helios 16
        let (tier, fans, year) = lookup("Predator Helios 16 PH16-71");
        assert_eq!(tier, LightingTier::PerKey);
        assert_eq!(fans, FanTopology::DualFan);
        assert_eq!(year, 2023);

        let (tier, fans, year) = lookup("PH16-72");
        assert_eq!(tier, LightingTier::PerKey);
        assert_eq!(fans, FanTopology::DualFan);
        assert_eq!(year, 2024);

        // Triton 17 X
        let (tier, fans, year) = lookup("Predator Triton 17 X PTX17-71");
        assert_eq!(tier, LightingTier::PerKey);
        assert_eq!(fans, FanTopology::TripleFan);
        assert_eq!(year, 2023);

        // Triton 300 SE 14"
        let (tier, fans, year) = lookup("Predator Triton PT314-51");
        assert_eq!(tier, LightingTier::ThreeZone);
        assert_eq!(fans, FanTopology::DualFan);
        assert_eq!(year, 2021);

        // Nitro V 15
        let (tier, fans, year) = lookup("Nitro V 15 ANV15-51");
        assert_eq!(tier, LightingTier::SingleZone);
        assert_eq!(fans, FanTopology::DualFan);
        assert_eq!(year, 2023);

        // Nitro 5
        let (tier, fans, year) = lookup("Nitro 5 AN515-58");
        assert_eq!(tier, LightingTier::FourZone);
        assert_eq!(fans, FanTopology::DualFan);
        assert_eq!(year, 2022);
    }

    #[test]
    fn test_model_catalog_heuristics() {
        // Future unknown PH16 model
        let (tier, fans, _) = lookup("Future Predator PH16-99");
        assert_eq!(tier, LightingTier::PerKey);
        assert_eq!(fans, FanTopology::DualFan);

        // Unknown Triton model
        let (tier, fans, _) = lookup("Predator PT314-99");
        assert_eq!(tier, LightingTier::ThreeZone);
        assert_eq!(fans, FanTopology::DualFan);

        // Empty string
        let (tier, _, _) = lookup("");
        assert_eq!(tier, LightingTier::Unavailable);
    }

    #[test]
    fn test_model_catalog_ffi() {
        let model = b"Predator Helios 16 PH16-71";
        let mut tier = 0i32;
        let mut fans = 0i32;
        let mut year = 0i32;

        let res = unsafe {
            predator_model_catalog_lookup(
                model.as_ptr(),
                model.len(),
                &mut tier,
                &mut fans,
                &mut year,
            )
        };
        assert_eq!(res, 0);
        assert_eq!(tier, 5); // PerKey
        assert_eq!(fans, 2); // DualFan
        assert_eq!(year, 2023);

        // Null pointer check
        let res_null = unsafe {
            predator_model_catalog_lookup(
                std::ptr::null(),
                model.len(),
                &mut tier,
                &mut fans,
                &mut year,
            )
        };
        assert_eq!(res_null, -1);
    }
}
