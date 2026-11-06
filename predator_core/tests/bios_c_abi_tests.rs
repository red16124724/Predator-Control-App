use predator_core::*;

static BIOS_TEST_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

#[test]
fn test_bios_c_abi_probe_and_counts() {
    let _guard = BIOS_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    unsafe {
        let mut support_mode: i32 = -1;
        let mut model_buf = [0u8; 128];

        // 1. Probe support mode and model name
        let probe_res = predator_bios_probe_support(
            &mut support_mode,
            model_buf.as_mut_ptr(),
            model_buf.len(),
        );
        assert_eq!(probe_res, 0, "Probe support must succeed");
        assert!(
            support_mode >= 1 && support_mode <= 3,
            "Support mode must be in range 1..=3, got: {}",
            support_mode
        );

        let model_str = std::ffi::CStr::from_ptr(model_buf.as_ptr() as *const i8)
            .to_str()
            .unwrap();
        assert!(!model_str.is_empty(), "Model name must not be empty");

        // 2. Settings count
        let count = predator_bios_get_settings_count();
        assert!(count >= 7, "Must report at least 7 settings in fallback/native mode, got: {}", count);

        // 3. Null pointer safety
        let null_probe = predator_bios_probe_support(
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            0,
        );
        assert_eq!(null_probe, 0, "Null pointers for optional probe outputs must succeed");
    }
}

#[test]
fn test_bios_c_abi_get_setting_info() {
    let _guard = BIOS_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    unsafe {
        let count = predator_bios_get_settings_count() as usize;
        assert!(count > 0);

        let mut name_buf = [0u8; 128];
        let mut val_buf = [0u8; 128];
        let mut opt_buf = [0u8; 256];
        let mut risk: i32 = -1;
        let mut kind: i32 = -1;

        // Verify all settings can be retrieved
        for idx in 0..count {
            let res = predator_bios_get_setting_info(
                idx,
                name_buf.as_mut_ptr(),
                val_buf.as_mut_ptr(),
                opt_buf.as_mut_ptr(),
                &mut risk,
                &mut kind,
                128,
            );
            assert_eq!(res, 0, "Getting setting info for index {} must succeed", idx);

            let name = std::ffi::CStr::from_ptr(name_buf.as_ptr() as *const i8).to_str().unwrap();
            let val = std::ffi::CStr::from_ptr(val_buf.as_ptr() as *const i8).to_str().unwrap();

            assert!(!name.is_empty(), "Setting {} name must not be empty", idx);
            assert!(!val.is_empty(), "Setting {} value must not be empty", idx);
            assert!(risk == 0 || risk == 1, "Risk must be 0 (Normal) or 1 (Dangerous)");
            assert!(kind >= 1 && kind <= 3, "Kind must be 1..=3");
        }

        // Out of bounds index must return -4 or -1 (error)
        let oob_res = predator_bios_get_setting_info(
            count + 100,
            name_buf.as_mut_ptr(),
            val_buf.as_mut_ptr(),
            opt_buf.as_mut_ptr(),
            &mut risk,
            &mut kind,
            128,
        );
        assert!(oob_res < 0, "Out of bounds index must return error code");
    }
}

#[test]
fn test_bios_c_abi_apply_setting_and_safety_guards() {
    let _guard = BIOS_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    unsafe {
        // 1. Normal setting apply with empty password (safe)
        let name_normal = b"LCD Overdrive\0";
        let val_normal = b"Enabled\0";
        let res_normal = predator_bios_apply_setting(
            name_normal.as_ptr(),
            val_normal.as_ptr(),
            std::ptr::null(),
            0,
        );
        assert_eq!(res_normal, 0, "Applying Normal setting must succeed");

        // 2. Dangerous setting apply BLOCKED when dangerous_allowed == 0
        let name_dangerous = b"Display MUX\0";
        let val_dangerous = b"Discrete GPU only\0";
        let res_blocked = predator_bios_apply_setting(
            name_dangerous.as_ptr(),
            val_dangerous.as_ptr(),
            std::ptr::null(),
            0,
        );
        assert_eq!(
            res_blocked, -3,
            "Dangerous setting without explicit authorization must return E_DANGEROUS_BLOCKED (-3)"
        );

        // 3. Dangerous setting apply SUCCEEDS when dangerous_allowed == 1
        let res_allowed = predator_bios_apply_setting(
            name_dangerous.as_ptr(),
            val_dangerous.as_ptr(),
            std::ptr::null(),
            1,
        );
        assert_eq!(
            res_allowed, 0,
            "Dangerous setting with explicit authorization must return 0"
        );

        // 4. Non-existent setting returns -4
        let name_unknown = b"NonExistentSetting123\0";
        let res_unknown = predator_bios_apply_setting(
            name_unknown.as_ptr(),
            val_normal.as_ptr(),
            std::ptr::null(),
            1,
        );
        assert_eq!(res_unknown, -4, "Unknown setting must return E_NOT_FOUND (-4)");

        // 5. Plaintext ASCII validation: control characters rejected with -2
        let name_invalid_ascii = b"LCD\nOverdrive\0";
        let res_invalid_ascii = predator_bios_apply_setting(
            name_invalid_ascii.as_ptr(),
            val_normal.as_ptr(),
            std::ptr::null(),
            0,
        );
        assert_eq!(
            res_invalid_ascii, -2,
            "Non-printable control characters must return E_INVALID_ARG (-2)"
        );

        // 6. Plaintext length check: > 128 bytes rejected with -2
        let mut long_buf = vec![b'A'; 130];
        long_buf[129] = 0;
        let res_long = predator_bios_apply_setting(
            long_buf.as_ptr(),
            val_normal.as_ptr(),
            std::ptr::null(),
            0,
        );
        assert_eq!(
            res_long, -2,
            "Strings exceeding 128 bytes must return E_INVALID_ARG (-2)"
        );

        // 7. Null pointer rejected with -1
        let res_null = predator_bios_apply_setting(
            std::ptr::null(),
            val_normal.as_ptr(),
            std::ptr::null(),
            0,
        );
        assert_eq!(res_null, -1, "Null pointer must return E_GENERAL (-1)");
    }
}

#[test]
fn test_bios_c_abi_restore_defaults() {
    let _guard = BIOS_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    unsafe {
        // Factory defaults with null password
        let res_factory = predator_bios_restore_defaults(std::ptr::null(), 0);
        assert_eq!(res_factory, 0, "Restore factory defaults must return 0");

        // User defaults with empty password
        let empty_pwd = b"\0";
        let res_user = predator_bios_restore_defaults(empty_pwd.as_ptr(), 1);
        assert_eq!(res_user, 0, "Restore user defaults must return 0");

        // Invalid password format (control char) returns -2
        let bad_pwd = b"pwd\x01\0";
        let res_bad = predator_bios_restore_defaults(bad_pwd.as_ptr(), 0);
        assert_eq!(res_bad, -2, "Invalid ASCII password must return -2");
    }
}

#[test]
fn test_bios_c_abi_mock_mode1_wmi_matrix() {
    let _guard = BIOS_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    unsafe {
        // Setup mock backend with 2025 Helios 16
        let mock = Box::new(bios::MockBiosBackend::with_model("PH16-73"));
        mock.add_wmi_setting("Function Key behavior, Special Key[Special Key;Standard Function Key;]");
        mock.add_wmi_setting("SATA Mode, AHCI[AHCI;RAID;]");
        mock.add_wmi_setting("Intel VTX, Enabled[Enabled;Disabled;]");
        mock.set_method_result(bios::BiosMethodResult::Done);

        let sub = bios::BiosSubsystem::new(mock);
        bios::set_test_subsystem(sub);

        // Verify probe mode
        let mut mode: i32 = -1;
        predator_bios_probe_support(&mut mode, std::ptr::null_mut(), 0);
        assert_eq!(mode, bios::BiosSupportMode::WmiModern2025 as i32);

        // Verify settings count
        assert_eq!(predator_bios_get_settings_count(), 3);

        // Apply normal setting
        let normal_setting = b"Function Key behavior\0";
        let normal_val = b"Standard Function Key\0";
        assert_eq!(
            predator_bios_apply_setting(normal_setting.as_ptr(), normal_val.as_ptr(), std::ptr::null(), 0),
            0
        );

        // Apply dangerous setting blocked
        let sata_setting = b"SATA Mode\0";
        let sata_val = b"RAID\0";
        assert_eq!(
            predator_bios_apply_setting(sata_setting.as_ptr(), sata_val.as_ptr(), std::ptr::null(), 0),
            -3
        );

        // Apply dangerous setting allowed
        assert_eq!(
            predator_bios_apply_setting(sata_setting.as_ptr(), sata_val.as_ptr(), std::ptr::null(), 1),
            0
        );

        // Reset subsystem back to default probe
        bios::reset_test_subsystem();
    }
}
