use predator_core::*;
static TEST_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

#[test]
fn test_pawnio_c_abi_lifecycle_and_direct_limits() {
    unsafe {
        // 1. Open driver
        let open_res = predator_pawnio_open_driver();
        assert!(
            open_res == 0 || open_res == -1,
            "Open driver must return 0 (mock/active) or -1"
        );

        let is_open = predator_pawnio_is_driver_open();
        if open_res == 0 {
            assert_eq!(
                is_open, 1,
                "PawnIO driver must report open when open succeeds"
            );
        } else {
            assert_eq!(
                is_open, 0,
                "PawnIO driver must report closed when driver unavailable"
            );
        }

        // 2. Direct apply limits with mirrored output
        let mut mirrored: i32 = 0;
        let apply_res = predator_pawnio_apply_mode_limits_direct(65, 125, &mut mirrored);
        assert!(
            apply_res == 0 || apply_res <= -1,
            "apply_mode_limits_direct must return code or 0"
        );

        // Null pointer handling for out_mirrored is safe (optional out param)
        let apply_null = predator_pawnio_apply_mode_limits_direct(55, 105, std::ptr::null_mut());
        assert!(
            apply_null == 0 || apply_null <= -1,
            "Null out_mirrored pointer must not crash"
        );

        // 3. Close driver
        predator_pawnio_close_driver();
        let is_open_after = predator_pawnio_is_driver_open();
        assert_eq!(
            is_open_after, 0,
            "PawnIO driver must report closed after close_driver"
        );
    }
}

#[test]
fn test_antithrottle_c_abi_tripwire_lockout() {
    unsafe {
        let mut power_cap: i32 = 0;
        let mut tripwire_locked: i32 = 0;

        // 1. Safe condition: 60°C < 85°C throttle threshold
        let now = 10_000i64;
        let res = predator_antithrottle_evaluate(
            60.0,
            now,
            85.0,
            95.0,
            100.0,
            65,
            55,
            15,
            3000,
            &mut power_cap,
            &mut tripwire_locked,
        );
        assert_eq!(res, 0);
        assert_eq!(power_cap, -1, "Normal temperature must not cap power");
        assert_eq!(tripwire_locked, 0, "Tripwire must not be locked");

        // 2. Critical hit: 102°C >= 100°C critical threshold
        let now_crit = 11_000i64;
        let res_crit = predator_antithrottle_evaluate(
            102.0,
            now_crit,
            85.0,
            95.0,
            100.0,
            65,
            55,
            15,
            3000,
            &mut power_cap,
            &mut tripwire_locked,
        );
        assert_eq!(res_crit, 0);
        assert_eq!(
            power_cap, 15,
            "Critical temperature must drop to minimum watts"
        );
        assert_eq!(tripwire_locked, 1, "Tripwire must be locked");

        // 3. Hold lockout: 1,500ms later at 90°C (within 3000ms hold window and > 85°C throttle)
        let now_hold = 12_500i64;
        let res_hold = predator_antithrottle_evaluate(
            90.0,
            now_hold,
            85.0,
            95.0,
            100.0,
            65,
            55,
            15,
            3000,
            &mut power_cap,
            &mut tripwire_locked,
        );
        assert_eq!(res_hold, 0);
        assert_eq!(
            power_cap, 15,
            "Power must remain capped during tripwire hold"
        );
        assert_eq!(tripwire_locked, 1, "Tripwire must still be locked");

        // 4. Lockout expired: 3,500ms after critical hit (now 14,500ms > 11,000 + 3,000)
        let now_released = 14_500i64;
        let res_rel = predator_antithrottle_evaluate(
            88.0,
            now_released,
            85.0,
            95.0,
            100.0,
            65,
            55,
            15,
            3000,
            &mut power_cap,
            &mut tripwire_locked,
        );
        assert_eq!(res_rel, 0);
        assert!(power_cap > 15, "Power cap must relax after hold expires");
        assert_eq!(tripwire_locked, 0, "Tripwire must be released");

        // 5. Null pointer safety
        let null_res = predator_antithrottle_evaluate(
            90.0,
            now_released,
            85.0,
            95.0,
            100.0,
            65,
            55,
            15,
            3000,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
        );
        assert_eq!(null_res, -1, "Null out pointers must return -1");
    }
}

#[test]
fn test_audio_capture_c_abi_lifecycle_and_zones() {
    let _guard = TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    unsafe {
        // Start capture
        let start_res = predator_audio_capture_start(48000, 2048, 8);
        assert_eq!(start_res, 0, "Audio capture start must succeed");

        let active = predator_audio_capture_is_active();
        assert!(
            active == 0 || active == 1,
            "Audio capture is_active returns binary state"
        );

        // Level
        let lvl = predator_audio_capture_get_level();
        assert!(lvl >= 0.0, "Audio level must be non-negative");

        // Render zones
        let mut zones = [0u8; 12]; // 4 zones * 3 (RGB)
        let z_res = predator_audio_capture_get_zones(255, 0, 0, 100, zones.as_mut_ptr(), 4);
        assert_eq!(z_res, 4, "get_zones must render 4 zones");

        // Get bands
        let mut bands = [0.0f32; 8];
        let b_res = predator_audio_capture_get_bands(bands.as_mut_ptr(), 8);
        assert_eq!(b_res, 8, "get_bands must copy 8 bands");

        // Stop capture
        predator_audio_capture_stop();
        let active_after = predator_audio_capture_is_active();
        assert_eq!(
            active_after, 0,
            "Audio capture must report inactive after stop"
        );

        // Null pointer safety
        let null_zones = predator_audio_capture_get_zones(255, 0, 0, 100, std::ptr::null_mut(), 4);
        assert_eq!(null_zones, -1, "Null out_zones must return -1");

        let null_bands = predator_audio_capture_get_bands(std::ptr::null_mut(), 8);
        assert_eq!(null_bands, -1, "Null out_bands must return -1");
    }
}

#[test]
fn test_model_catalog_c_abi_lookup() {
    unsafe {
        let mut tier: i32 = -1;
        let mut fans: i32 = -1;
        let mut year: i32 = -1;

        // 1. Predator Helios 16 PH16-71 -> PerKey (5), DualFan (2), 2023
        let m1 = b"PH16-71";
        let res1 =
            predator_model_catalog_lookup(m1.as_ptr(), m1.len(), &mut tier, &mut fans, &mut year);
        assert_eq!(res1, 0);
        assert_eq!(tier, 5, "PH16-71 must have PerKey lighting (5)");
        assert_eq!(fans, 2, "PH16-71 must have DualFan cooling (2)");
        assert_eq!(year, 2023, "PH16-71 launch year must be 2023");

        // 2. Predator Triton 500 SE PT516-52 -> ThreeZone (3), TripleFan (3), 2022
        let m2 = b"PT516-52";
        let res2 =
            predator_model_catalog_lookup(m2.as_ptr(), m2.len(), &mut tier, &mut fans, &mut year);
        assert_eq!(res2, 0);
        assert_eq!(tier, 3, "PT516-52 must have ThreeZone lighting (3)");
        assert_eq!(fans, 3, "PT516-52 must have TripleFan cooling (3)");
        assert_eq!(year, 2022, "PT516-52 launch year must be 2022");

        // 3. Acer Nitro 5 AN515-58 -> FourZone (4), DualFan (2), 2022
        let m3 = b"AN515-58";
        let res3 =
            predator_model_catalog_lookup(m3.as_ptr(), m3.len(), &mut tier, &mut fans, &mut year);
        assert_eq!(res3, 0);
        assert_eq!(tier, 4, "AN515-58 must have FourZone lighting (4)");
        assert_eq!(fans, 2, "AN515-58 must have DualFan cooling (2)");
        assert_eq!(year, 2022, "AN515-58 launch year must be 2022");

        // 4. Nitro V 15 ANV15-51 -> SingleZone (1), DualFan (2), 2023
        let m4 = b"ANV15-51";
        let res4 =
            predator_model_catalog_lookup(m4.as_ptr(), m4.len(), &mut tier, &mut fans, &mut year);
        assert_eq!(res4, 0);
        assert_eq!(tier, 1, "ANV15-51 must have SingleZone lighting (1)");
        assert_eq!(fans, 2, "ANV15-51 must have DualFan cooling (2)");
        assert_eq!(year, 2023, "ANV15-51 launch year must be 2023");

        // 5. Null pointer guards
        assert_eq!(
            predator_model_catalog_lookup(std::ptr::null(), 5, &mut tier, &mut fans, &mut year),
            -1
        );
        assert_eq!(
            predator_model_catalog_lookup(
                m1.as_ptr(),
                m1.len(),
                std::ptr::null_mut(),
                &mut fans,
                &mut year
            ),
            -1
        );
        assert_eq!(
            predator_model_catalog_lookup(
                m1.as_ptr(),
                m1.len(),
                &mut tier,
                std::ptr::null_mut(),
                &mut year
            ),
            -1
        );
        assert_eq!(
            predator_model_catalog_lookup(
                m1.as_ptr(),
                m1.len(),
                &mut tier,
                &mut fans,
                std::ptr::null_mut()
            ),
            -1
        );
    }
}

#[test]
fn test_firmware_tables_c_abi_null_guards() {
    unsafe {
        // Direct ACPI PPCC
        assert_eq!(predator_acpi_fetch_and_parse_ppcc(std::ptr::null_mut()), -1);

        // SMBIOS with invalid unaligned / high address pointers returns -1
        let invalid_ptr = 0x1 as *mut u32;
        assert_eq!(
            predator_smbios_fetch_and_parse(
                invalid_ptr,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut()
            ),
            -1
        );

        // Valid SMBIOS fetch
        let mut gaming: u32 = 0;
        let mut hotkey: u32 = 0;
        let mut dgpu: u8 = 0;
        let mut ver: u16 = 0;
        let smb_res =
            predator_smbios_fetch_and_parse(&mut gaming, &mut hotkey, &mut dgpu, &mut ver);
        assert!(smb_res == 0 || smb_res == -1);

        // UEFI Power Config
        assert_eq!(predator_uefi_fetch_and_scan_power(std::ptr::null_mut()), -1);
    }
}

#[test]
fn test_enek_c_abi_null_guards() {
    unsafe {
        assert_eq!(predator_enek_send_report(std::ptr::null(), 0), -1);
        assert_eq!(predator_enek_send_report(std::ptr::null(), 17), -1);
        // set_zones without hardware open returns -3 (driver not open)
        assert_eq!(
            predator_enek_set_zones(255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0, 100),
            -3
        );
    }
}

#[test]
fn test_telemetry_c_abi_hardware_metrics() {
    let _guard = TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    unsafe {
        // Test null pointer returns -1
        assert_eq!(predator_telemetry_get_snapshot(std::ptr::null_mut()), -1);

        // Initialize core
        assert_eq!(predator_core_init(), 0);
        std::thread::sleep(std::time::Duration::from_millis(60));

        let mut snap = telemetry::NativeTelemetrySnapshot::default();
        let res = predator_telemetry_get_snapshot(&mut snap);
        assert_eq!(res, 0);
        assert!(snap.timestamp_ms > 0);

        predator_core_cleanup();
    }
}

#[test]
fn test_all_7_subsystem_checklist_exports() {
    let _guard = TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    unsafe {
        // 1. PawnIO
        let p_open = predator_pawnio_open();
        assert!(p_open == 0 || p_open == -1);
        let p_srv = predator_pawnio_is_service_running();
        assert!(p_srv == 0 || p_srv == 1);
        let mut mirrored = 0i32;
        let _ = predator_pawnio_apply_mode_limits_direct(65, 125, &mut mirrored);

        // 2. EC HID
        let enek_open = predator_enek_open(0x048D, 0xCE00);
        assert!(enek_open == 0 || enek_open == -1);
        assert_eq!(predator_enek_send_report(std::ptr::null(), 0), -1);
        let _ = predator_enek_set_zones(255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 100);

        // 3. Thermal
        let mut cap = 0i32;
        let mut locked = 0i32;
        assert_eq!(
            predator_antithrottle_evaluate(
                70.0,
                1000,
                75.0,
                95.0,
                100.0,
                65,
                45,
                15,
                3000,
                &mut cap,
                &mut locked
            ),
            0
        );

        // 4. Audio
        assert_eq!(predator_audio_capture_start(48000, 2048, 8), 0);
        let active = predator_audio_capture_is_active();
        assert!(active == 0 || active == 1);
        let mut bands = [0.0f32; 8];
        assert_eq!(predator_audio_capture_read(bands.as_mut_ptr(), 8), 8);
        predator_audio_capture_stop();

        // 5. ACPI / SMBIOS / UEFI
        let _ = predator_acpi_fetch_and_parse_ppcc(std::ptr::null_mut());
        let mut g = 0u32;
        let mut h = 0u32;
        let mut d = 0u8;
        let mut v = 0u16;
        let _ = predator_smbios_fetch_and_parse(&mut g, &mut h, &mut d, &mut v);
        let _ = predator_uefi_fetch_and_scan_power(std::ptr::null_mut());

        // 6. Telemetry
        assert_eq!(predator_telemetry_start(), 0);
        assert_eq!(predator_telemetry_is_active(), 1);
        let mut snap = telemetry::NativeTelemetrySnapshot::default();
        let _ = predator_telemetry_get_snapshot(&mut snap);
        predator_telemetry_stop();
        assert_eq!(predator_telemetry_is_active(), 0);

        // 7. Model catalog
        let mut tier = 0i32;
        let mut fans = 0i32;
        let mut year = 0i32;
        let m = b"PH16-71";
        assert_eq!(
            predator_model_catalog_lookup(m.as_ptr(), m.len(), &mut tier, &mut fans, &mut year),
            0
        );
    }
}
