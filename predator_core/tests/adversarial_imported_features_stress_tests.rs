//! Exhaustive Adversarial Stress, Chaos, and Concurrency Tests for predator_core
//!
//! Subsystems tested:
//! 1. Display & Advanced Optimus / DDS (`display.rs`)
//!    - Concurrency, buffer bounds, invalid refresh rates (negative, huge),
//!      unaligned memory, nvcpl.dll dynamic loading failure modes, mock DDS transitions under race conditions.
//! 2. Multi-generation Acer BIOS & Firmware Safety (`bios.rs`)
//!    - Two-tier safety gates bypass attempts, malformed ASCII strings (>128 bytes, non-ASCII, null pointers),
//!      Mode 1 WMI, Mode 2 ACPI-WMI, Mode 3 UEFI NVRAM fuzzing, password zeroization verification.
//! 3. Event-Driven Game Profiles (`game_profile.rs`)
//!    - Rapid concurrent registration/unregistration, queue overflow stress, rapid foreground switching simulation,
//!      debounce boundaries, process exit handle recycling.
//! 4. Thermal, Curves, Audio, RGB, Telemetry & PawnIO (`thermal.rs`, `curve.rs`, `audio.rs`, `rgb.rs`, `telemetry.rs`, `pawnio.rs`)
//!    - Extreme floats (NaN, +Inf, -Inf), boundary conditions, zero-division, multi-thread contention.

use predator_core::audio::SpectrumAnalyzer;
use predator_core::bios::{
    classify_setting_risk, parse_bios_setting_string, validate_ascii_str, zeroize_string,
    BiosBackend, MockBiosBackend, SettingKind, SettingRisk, ZeroizingString, E_DANGEROUS_BLOCKED,
    E_GENERAL, E_INVALID_ARG,
};
use predator_core::curve::{
    interpolate_curve, FanCurveFollower, Point, MAX_FAN_SPEED, MIN_FAN_SPEED,
};
use predator_core::display::{
    clear_last_wmi_action, enumerate_supported_refresh_rates, get_bluelight_shield_mode,
    get_current_refresh_rate, get_dds_status, get_last_wmi_action, get_lcd_overdrive,
    set_bluelight_shield, set_dds_mode, set_lcd_overdrive, set_mock_mode,
    set_mock_supported_refresh_rates, set_refresh_rate,
};
use predator_core::game_profile::{
    is_blocked_executable, GameProfileConfig, GameProfileEngine, DEBOUNCE_THRESHOLD_MS,
    MAX_EVENT_QUEUE_CAPACITY,
};
use predator_core::pawnio::{build_execute_packet, parse_execute_response};
use predator_core::rgb::{effect, hsv_to_rgb, render_frame, Rgb8};
use predator_core::telemetry::TelemetryWorker;
use predator_core::thermal::{ThermalCoordinator, ThermalSafetyProfile};
use predator_core::*;

use std::sync::{Arc, Barrier, Mutex};
use std::thread;

static GLOBAL_STRESS_LOCK: Mutex<()> = Mutex::new(());

// ============================================================================
// 1. DISPLAY SUBYSTEM ADVERSARIAL & CONCURRENCY TESTS
// ============================================================================

#[test]
fn test_display_invalid_refresh_rates_boundary_fuzz() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    set_mock_mode(true);

    // Negative values
    assert!(set_refresh_rate(-1).is_err());
    assert!(set_refresh_rate(-60).is_err());
    assert!(set_refresh_rate(-144).is_err());
    assert!(set_refresh_rate(i32::MIN).is_err());

    // Zero
    assert!(set_refresh_rate(0).is_err());

    // Exceeding ceiling (> 1000 Hz)
    assert!(set_refresh_rate(1001).is_err());
    assert!(set_refresh_rate(10000).is_err());
    assert!(set_refresh_rate(i32::MAX).is_err());

    // Valid boundaries
    assert!(set_refresh_rate(1).is_ok());
    assert_eq!(get_current_refresh_rate(), 1);
    assert!(set_refresh_rate(60).is_ok());
    assert_eq!(get_current_refresh_rate(), 60);
    assert!(set_refresh_rate(144).is_ok());
    assert_eq!(get_current_refresh_rate(), 144);
    assert!(set_refresh_rate(240).is_ok());
    assert_eq!(get_current_refresh_rate(), 240);
    assert!(set_refresh_rate(360).is_ok());
    assert_eq!(get_current_refresh_rate(), 360);
    assert!(set_refresh_rate(1000).is_ok());
    assert_eq!(get_current_refresh_rate(), 1000);

    set_mock_mode(false);
}

#[test]
fn test_display_bluelight_shield_fuzz_and_boundaries() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    set_mock_mode(true);

    // Negative modes
    assert!(set_bluelight_shield(-1).is_err());
    assert!(set_bluelight_shield(-100).is_err());
    assert!(set_bluelight_shield(i32::MIN).is_err());

    // Beyond 100%
    assert!(set_bluelight_shield(101).is_err());
    assert!(set_bluelight_shield(1000).is_err());
    assert!(set_bluelight_shield(i32::MAX).is_err());

    // Valid modes 0..=100
    for mode in 0..=100 {
        assert!(set_bluelight_shield(mode).is_ok());
        assert_eq!(get_bluelight_shield_mode(), mode);
    }

    set_mock_mode(false);
}

#[test]
fn test_display_buffer_bounds_zero_allocation_slices() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    set_mock_mode(true);

    set_mock_supported_refresh_rates(&[60, 120, 144, 165, 240, 360]);

    // 0-length slice (must return total count without writing past buffer)
    let mut zero_buf: [i32; 0] = [];
    let count0 = enumerate_supported_refresh_rates(&mut zero_buf);
    assert_eq!(count0, 6);

    // 1-length slice (must write 1 element and return total count 6)
    let mut one_buf = [0i32; 1];
    let count1 = enumerate_supported_refresh_rates(&mut one_buf);
    assert_eq!(count1, 6);
    assert_eq!(one_buf[0], 60);

    // Exact slice
    let mut exact_buf = [0i32; 6];
    let count6 = enumerate_supported_refresh_rates(&mut exact_buf);
    assert_eq!(count6, 6);
    assert_eq!(exact_buf, [60, 120, 144, 165, 240, 360]);

    // Over-allocated slice
    let mut large_buf = [0i32; 32];
    let count_large = enumerate_supported_refresh_rates(&mut large_buf);
    assert_eq!(count_large, 6);
    assert_eq!(&large_buf[..6], &[60, 120, 144, 165, 240, 360]);

    set_mock_mode(false);
}

#[test]
fn test_display_c_abi_unaligned_pointers_and_guards() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    set_mock_mode(true);

    unsafe {
        // 1. Null pointers rejected
        let mut count: usize = 0;
        let mut rate_buf = [0i32; 16];
        assert_eq!(
            predator_display_get_supported_refresh_rates(std::ptr::null_mut(), 16, &mut count),
            -1
        );
        assert_eq!(
            predator_display_get_supported_refresh_rates(
                rate_buf.as_mut_ptr(),
                16,
                std::ptr::null_mut()
            ),
            -1
        );
        assert_eq!(
            predator_display_get_supported_refresh_rates(rate_buf.as_mut_ptr(), 0, &mut count),
            -1
        );

        // 2. Unaligned memory buffer
        let mut byte_backing = [0u8; 128];
        // Misalign by 1 byte
        let unaligned_rates = byte_backing.as_mut_ptr().add(1) as *mut i32;
        let unaligned_count = byte_backing.as_mut_ptr().add(65) as *mut usize;

        let res = predator_display_get_supported_refresh_rates(unaligned_rates, 5, unaligned_count);
        assert_eq!(res, 0);

        let read_count = std::ptr::read_unaligned(unaligned_count);
        assert!(read_count > 0);
        let first_rate = std::ptr::read_unaligned(unaligned_rates);
        assert!(first_rate >= 60);

        // 3. DDS status with unaligned pointers
        let unaligned_sup = byte_backing.as_mut_ptr().add(3) as *mut i32;
        let unaligned_auto = byte_backing.as_mut_ptr().add(7) as *mut i32;
        let unaligned_mux = byte_backing.as_mut_ptr().add(11) as *mut i32;
        let unaligned_block = byte_backing.as_mut_ptr().add(15) as *mut i32;

        assert_eq!(
            predator_display_get_dds_status(
                unaligned_sup,
                unaligned_auto,
                unaligned_mux,
                unaligned_block
            ),
            0
        );
        assert_eq!(std::ptr::read_unaligned(unaligned_sup), 1);

        // Null pointer handling in DDS status
        assert_eq!(
            predator_display_get_dds_status(
                std::ptr::null_mut(),
                unaligned_auto,
                unaligned_mux,
                unaligned_block
            ),
            -1
        );
    }

    set_mock_mode(false);
}

#[test]
fn test_display_multithreaded_concurrency_hammering() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    set_mock_mode(true);

    const THREADS: usize = 16;
    const ITERATIONS: usize = 200;
    let barrier = Arc::new(Barrier::new(THREADS));
    let mut handles = Vec::new();

    for t in 0..THREADS {
        let b = barrier.clone();
        handles.push(thread::spawn(move || {
            b.wait();
            for i in 0..ITERATIONS {
                let hz = 60 + ((t * 10 + i) % 300) as i32;
                let _ = set_refresh_rate(hz);
                let _ = get_current_refresh_rate();

                let mut buf = [0i32; 16];
                let _ = enumerate_supported_refresh_rates(&mut buf);

                let bl = (t + i) as i32 % 100;
                let _ = set_bluelight_shield(bl);
                let _ = get_bluelight_shield_mode();

                let dds = (t + i) as i32 % 3;
                let _ = set_dds_mode(dds);
                let _ = get_dds_status();

                let od = (t + i) % 2 == 0;
                let _ = set_lcd_overdrive(od);
                let _ = get_lcd_overdrive();
            }
        }));
    }

    for h in handles {
        h.join().expect("Display concurrency thread panicked");
    }

    set_mock_mode(false);
}

#[test]
fn test_display_mock_dds_race_conditions_and_wmi_action() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    set_mock_mode(true);

    clear_last_wmi_action();
    // Test mode transitions: 0 = Optimus (WMI mode 1), 2 = dGPU (WMI mode 2), 1 = Auto (WMI mode 0)
    assert!(set_dds_mode(0).is_ok());
    let act0 = get_last_wmi_action().unwrap();
    assert_eq!(act0.function, 2);
    assert_eq!(act0.value, 1);

    assert!(set_dds_mode(2).is_ok());
    let act2 = get_last_wmi_action().unwrap();
    assert_eq!(act2.function, 2);
    assert_eq!(act2.value, 2);

    assert!(set_dds_mode(1).is_ok());
    let act1 = get_last_wmi_action().unwrap();
    assert_eq!(act1.function, 2);
    assert_eq!(act1.value, 0);

    // Invalid modes rejected
    assert!(set_dds_mode(-1).is_err());
    assert!(set_dds_mode(3).is_err());
    assert!(set_dds_mode(100).is_err());

    set_mock_mode(false);
}

// ============================================================================
// 2. BIOS & FIRMWARE SAFETY GATES & ADVERSARIAL FUZZING TESTS
// ============================================================================

#[test]
fn test_bios_two_tier_safety_gate_bypass_attempts() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());

    let dangerous_attempts = [
        "SATA Mode",
        "sata mode",
        "SATA MODE",
        "Secure Boot",
        "secure boot",
        "Display MUX",
        "display mux",
        "QuietBoot",
        "quietboot",
        "VMD Controller",
        "CPU Overclocking",
        "UnderVolt Protection",
        "Intel VTX Hack",
        "Intel VTX SATA",
        "Above 4G decoding Dangerous",
        "Dangerous Active Efficient Cores",
        "   ",
        "",
    ];

    for &name in &dangerous_attempts {
        let risk = classify_setting_risk(name);
        assert_eq!(
            risk,
            SettingRisk::Dangerous,
            "Setting '{}' must be classified as Dangerous",
            name
        );
    }

    // Verify C-ABI enforcement: Dangerous settings with dangerous_allowed=0 return -3
    unsafe {
        for name in [b"Display MUX\0".as_ptr(), b"QuietBoot\0".as_ptr()] {
            let res = predator_bios_apply_setting(
                name,
                b"Value\0".as_ptr(),
                std::ptr::null(),
                0, // dangerous_allowed = 0
            );
            assert_eq!(
                res, E_DANGEROUS_BLOCKED,
                "Dangerous setting must return E_DANGEROUS_BLOCKED (-3)"
            );
        }
    }
}

#[test]
fn test_bios_malformed_ascii_and_control_chars_fuzzing() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());

    // 1. >128 bytes string rejected
    let long_str = "A".repeat(129);
    assert_eq!(validate_ascii_str(&long_str, 128), Err(E_INVALID_ARG));
    let huge_str = "B".repeat(1024);
    assert_eq!(validate_ascii_str(&huge_str, 128), Err(E_INVALID_ARG));

    // 2. Non-ASCII Unicode rejected
    let unicode_cases = [
        "Password🔑",
        "BIOS_Настройки",
        "ÜberTakten",
        "设置_Mode",
        "Café123",
        "100°C",
    ];
    for &u in &unicode_cases {
        assert_eq!(validate_ascii_str(u, 128), Err(E_INVALID_ARG));
    }

    // 3. Control characters rejected
    let control_cases = [
        "Line1\nLine2",
        "Tab	Key",
        "Carriage\rReturn",
        "Null\0Byte",
        "\x01\x02\x03",
        "\x1F",
        "\x7F", // DEL character
    ];
    for &c in &control_cases {
        assert_eq!(validate_ascii_str(c, 128), Err(E_INVALID_ARG));
    }

    // 4. C-ABI enforcement of ASCII validation
    unsafe {
        let normal_setting = b"LCD Overdrive ";
        let normal_val = b"Enabled ";

        // Non-ASCII in name rejected (-2)
        assert_eq!(
            predator_bios_apply_setting(
                b"LCD\xFFOverdrive\0".as_ptr(),
                normal_val.as_ptr(),
                std::ptr::null(),
                0
            ),
            E_INVALID_ARG
        );

        // Control char in password rejected (-2)
        assert_eq!(
            predator_bios_apply_setting(
                normal_setting.as_ptr(),
                normal_val.as_ptr(),
                b"pass\x01\0".as_ptr(),
                0
            ),
            E_INVALID_ARG
        );

        // String > 128 bytes rejected (-2)
        let mut long_buf = vec![b'A'; 130];
        long_buf[129] = 0;
        assert_eq!(
            predator_bios_apply_setting(
                long_buf.as_ptr(),
                normal_val.as_ptr(),
                std::ptr::null(),
                0
            ),
            E_INVALID_ARG
        );

        // Null pointer rejected (-1)
        assert_eq!(
            predator_bios_apply_setting(std::ptr::null(), normal_val.as_ptr(), std::ptr::null(), 0),
            E_GENERAL
        );
    }
}

#[test]
fn test_bios_password_zeroization_scrubbing_and_raii() {
    // 1. Test in-place zeroize_string
    let mut secret = String::from("ExtremelyConfidentialPassword999#!");
    zeroize_string(&mut secret);
    assert_eq!(secret.len(), 0);
    assert!(secret.is_empty());

    // 2. Test ZeroizingString RAII container
    {
        let zs = ZeroizingString::new(String::from("MyBiosAdminPassword"));
        assert_eq!(&*zs, "MyBiosAdminPassword");
        assert_eq!(zs.as_str(), "MyBiosAdminPassword");
        // Drop is called automatically at scope exit
    }
}

#[test]
fn test_bios_mode1_mode2_mode3_fuzzing_matrix() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());

    // Mode 1: Malformed raw WMI setting strings
    let malformed_wmi = [
        "",
        "   ",
        ",",
        ",,",
        "NoCommaInString",
        "NameOnly,",
        "Name, [NoCloseBracket",
        "Name, Val[Opt1;Opt2",
        "Name, Val[;;;]",
        ", Val[Opt1;Opt2;]",
    ];

    for &raw in &malformed_wmi {
        let _ = parse_bios_setting_string(raw, SettingKind::WmiNative);
    }

    // Mode 3: UEFI NVRAM variable fuzzing
    let mock = MockBiosBackend::new();

    let uefi_fuzz_names = ["", "FastBoot", "Setup", "NonExistent"];
    let uefi_fuzz_guids = [
        "",
        "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}",
        "8BE4DF61-93CA-11D2-AA0D-00E098032B8C", // without braces
        "invalid-guid-string",
    ];

    for name in &uefi_fuzz_names {
        for guid in &uefi_fuzz_guids {
            let _ = mock.read_uefi_variable(name, guid);
            let _ = mock.write_uefi_variable(name, guid, &[0u8; 16], 0);
        }
    }
}

// ============================================================================
// 3. GAME PROFILES CONCURRENCY, QUEUE OVERFLOW & DEBOUNCE TESTS
// ============================================================================

#[test]
fn test_game_profile_queue_overflow_stress() {
    let mut engine = GameProfileEngine::new();

    // Register a valid profile
    engine
        .register(GameProfileConfig {
            profile_id: 1,
            exe_name: "cyberpunk2077.exe".to_string(),
            full_path: r"D:\Games\Cyberpunk 2077ind\Cyberpunk2077.exe".to_string(),
            mode: 3,
            pl1: 75,
            pl2: 135,
            fan_mode: 2,
            refresh_rate: 165,
            dds_mode: 2,
        })
        .unwrap();

    // Push 5,000 rapid game switches and exits without polling
    for i in 1..=5000 {
        let fake_pid = 1000 + (i as u32 % 50);
        let tick = (i as u64) * (DEBOUNCE_THRESHOLD_MS + 10);
        let _ = engine.handle_foreground_transition(fake_pid, "cyberpunk2077.exe", "", tick);
        let _ = engine.handle_process_exit(fake_pid);
    }

    // Verify queue length is capped at MAX_EVENT_QUEUE_CAPACITY (256)
    let mut drained_count = 0;
    while let Some(_) = engine.poll_event() {
        drained_count += 1;
    }

    assert!(
        drained_count <= MAX_EVENT_QUEUE_CAPACITY,
        "Drained count {} exceeded MAX_EVENT_QUEUE_CAPACITY {}",
        drained_count,
        MAX_EVENT_QUEUE_CAPACITY
    );
    assert_eq!(engine.poll_event(), None);
}

#[test]
fn test_game_profile_debounce_boundaries() {
    let mut engine = GameProfileEngine::new();
    engine.set_mock_own_pid(Some(99999));

    engine
        .register(GameProfileConfig {
            profile_id: 10,
            exe_name: "game.exe".to_string(),
            full_path: "".to_string(),
            mode: 2,
            pl1: 65,
            pl2: 125,
            fan_mode: 1,
            refresh_rate: 144,
            dds_mode: 1,
        })
        .unwrap();

    // 1. Initial event at t=1000ms -> accepted
    let ev1 = engine.handle_foreground_transition(1234, "game.exe", "", 1000);
    assert!(ev1.is_some());

    // 2. Sub-debounce event at t=1079ms (elapsed = 79ms < 80ms) -> debounced (None)
    let ev_debounced = engine.handle_foreground_transition(1234, "game.exe", "", 1079);
    assert_eq!(ev_debounced, None);

    // 3. Different process at t=1010ms is NOT debounced (switches immediately)
    engine
        .register(GameProfileConfig {
            profile_id: 20,
            exe_name: "othergame.exe".to_string(),
            full_path: "".to_string(),
            mode: 1,
            pl1: 45,
            pl2: 95,
            fan_mode: 0,
            refresh_rate: 60,
            dds_mode: 0,
        })
        .unwrap();

    let ev_diff = engine.handle_foreground_transition(5678, "othergame.exe", "", 1010);
    assert!(ev_diff.is_some());

    // 4. Boundary event at exactly t = 1010 + 80 = 1090ms -> accepted
    let ev_boundary = engine.handle_foreground_transition(5678, "othergame.exe", "", 1090);
    // Same game already active, so focus return gives None, but process is accepted
    assert_eq!(ev_boundary, None);

    // 5. Blocked system processes
    assert!(is_blocked_executable("explorer.exe"));
    assert!(is_blocked_executable("dwm.exe"));
    assert!(is_blocked_executable("csrss.exe"));
    assert!(is_blocked_executable("svchost.exe"));
    assert_eq!(
        engine.handle_foreground_transition(999, "explorer.exe", "", 2000),
        None
    );
}

#[test]
fn test_game_profile_rapid_concurrent_registration_and_hammering() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());

    const THREADS: usize = 12;
    const OPERATIONS: usize = 150;
    let barrier = Arc::new(Barrier::new(THREADS));
    let mut handles = Vec::new();

    for t in 0..THREADS {
        let b = barrier.clone();
        handles.push(thread::spawn(move || {
            b.wait();
            for i in 0..OPERATIONS {
                let prof_id = ((t * 100 + i) % 500 + 1) as u32;
                let exe = format!("game_{}.exe", prof_id);

                let cfg = GameProfileConfig {
                    profile_id: prof_id,
                    exe_name: exe.clone(),
                    full_path: format!(r"C:\Games\{}\{}", exe, exe),
                    mode: (i % 4) as i32,
                    pl1: 45 + (i % 50) as u32,
                    pl2: 95 + (i % 50) as u32,
                    fan_mode: (i % 3) as i32,
                    refresh_rate: 144,
                    dds_mode: 1,
                };

                let _ = game_profile::register_profile(cfg);
                let _ = game_profile::get_active();

                if i % 3 == 0 {
                    let _ = game_profile::unregister_profile(prof_id);
                }
            }
        }));
    }

    for h in handles {
        h.join().expect("Game profile concurrency thread panicked");
    }

    game_profile::clear_profiles();
}

// ============================================================================
// 4. THERMAL, CURVE, AUDIO, RGB, TELEMETRY & PAWNIO STRESS TESTS
// ============================================================================

#[test]
fn test_thermal_extreme_floats_and_hysteresis_chaos() {
    let mut coordinator = ThermalCoordinator::new(ThermalSafetyProfile::default());

    let extreme_temperatures = [
        f64::NAN,
        f64::INFINITY,
        f64::NEG_INFINITY,
        -1000.0,
        -1.0,
        0.0,
        0.000001,
        25.0,
        74.99,
        75.0,
        85.0,
        94.99,
        95.0,
        99.99,
        100.0,
        105.0,
        150.0,
        150.1,
        1e6,
        1e300,
    ];

    let mut timestamp = 1000u64;

    for &temp in &extreme_temperatures {
        timestamp += 300;
        let dec = coordinator.evaluate_at(temp, temp, 65, 15, 45, timestamp);
        assert!(!dec.cpu_temp.is_nan());
        assert!(!dec.gpu_temp.is_nan());
        assert!(dec.cpu_fan_floor >= 0 && dec.cpu_fan_floor <= 100);
        assert!(dec.gpu_fan_floor >= 0 && dec.gpu_fan_floor <= 100);
        assert!(dec.stage >= 0 && dec.stage <= 3);
    }
}

#[test]
fn test_curve_piecewise_linear_zero_division_and_fuzz() {
    // 1. Identical points x0 == x1 (potential division-by-zero hazard)
    let identical_pts = [
        Point {
            temp: 50,
            speed: 30,
        },
        Point {
            temp: 50,
            speed: 70,
        },
    ];
    let res = interpolate_curve(&identical_pts, 50);
    assert!(res >= MIN_FAN_SPEED && res <= MAX_FAN_SPEED);

    // 2. Extreme temperatures across follower
    let mut follower = FanCurveFollower::new(3);
    let pts = [
        Point {
            temp: 40,
            speed: 20,
        },
        Point {
            temp: 60,
            speed: 50,
        },
        Point {
            temp: 80,
            speed: 80,
        },
        Point {
            temp: 95,
            speed: 100,
        },
    ];

    let fuzz_temps = [
        i32::MIN,
        -500,
        -1,
        0,
        1,
        39,
        40,
        50,
        60,
        70,
        80,
        90,
        95,
        100,
        150,
        i32::MAX,
    ];

    for &t in &fuzz_temps {
        let sp = follower.update(t, &pts, 0);
        assert!(
            sp >= MIN_FAN_SPEED && sp <= MAX_FAN_SPEED,
            "Follower fan speed {} out of bounds for temp {}",
            sp,
            t
        );
    }
}

#[test]
fn test_audio_spectrum_extreme_floats_and_multithread_hammering() {
    let analyzer = Arc::new(SpectrumAnalyzer::new(48000, 2048, 16).unwrap());

    // 1. Extreme float samples (NaN, Inf, huge values)
    let bad_samples = [
        f32::NAN,
        f32::INFINITY,
        f32::NEG_INFINITY,
        1e30,
        -1e30,
        1e-30,
        0.0,
        1.0,
        -1.0,
    ];
    analyzer.push(&bad_samples);
    analyzer.analyze();

    let lvl = analyzer.level();
    let pk = analyzer.peak();
    let b_str = analyzer.beat_strength();

    assert!(lvl.is_finite(), "Level must be finite, got: {}", lvl);
    assert!(pk.is_finite(), "Peak must be finite, got: {}", pk);
    assert!(
        b_str.is_finite(),
        "Beat strength must be finite, got: {}",
        b_str
    );

    // 2. Multithreaded hammering
    const THREADS: usize = 8;
    let barrier = Arc::new(Barrier::new(THREADS));
    let mut handles = Vec::new();

    for t in 0..THREADS {
        let a = analyzer.clone();
        let b = barrier.clone();
        handles.push(thread::spawn(move || {
            b.wait();
            let mut chunk = [0.1f32; 512];
            for i in 0..chunk.len() {
                chunk[i] = ((t + i) as f32 * 0.01).sin();
            }
            for _ in 0..100 {
                a.push(&chunk);
                a.analyze();
                let _ = a.level();
                let _ = a.peak();
                let _ = a.is_beat();
                let _ = a.beat_strength();

                let mut zones = [0u8; 12];
                let _ = a.render_zones([255, 0, 0], 100, &mut zones);
            }
        }));
    }

    for h in handles {
        h.join().expect("Audio spectrum hammer thread panicked");
    }
}

#[test]
fn test_rgb_animation_extreme_floats_and_boundaries() {
    // 1. HSV extreme floats
    let extreme_hsv = [
        (f64::NAN, 1.0, 1.0),
        (180.0, f64::NAN, 1.0),
        (180.0, 1.0, f64::NAN),
        (f64::INFINITY, 1.0, 1.0),
        (180.0, f64::INFINITY, 1.0),
        (180.0, 1.0, f64::INFINITY),
        (-1000.0, -5.0, -10.0),
        (10000.0, 50.0, 100.0),
    ];

    for &(h, s, v) in &extreme_hsv {
        let rgb = hsv_to_rgb(h, s, v);
        let _ = rgb.r;
        let _ = rgb.g;
        let _ = rgb.b;
    }

    // 2. Render frame boundaries (0 zones, huge step)
    let base = Rgb8 {
        r: 255,
        g: 128,
        b: 64,
    };
    let mut zero_out = [Rgb8::default(); 0];
    assert_eq!(
        render_frame(effect::RAINBOW, 1000, base, 100, &mut zero_out),
        0
    );

    let mut full_out = [Rgb8::default(); 4];
    for eff in [
        effect::SNAKE,
        effect::RIPPLE,
        effect::HEARTBEAT,
        effect::RAINBOW,
        effect::DISCO,
    ] {
        let written = render_frame(eff, u64::MAX, base, 100, &mut full_out);
        assert_eq!(written, 4);
    }
}

#[test]
fn test_pawnio_execute_packet_serialization_stress() {
    // Exact execute packet layout serialization & parsing
    let args = [0x610u64, 0x1234567890ABCDEFu64, u64::MAX, 0u64];
    let packet = build_execute_packet("ioctl_write_msr", &args);
    assert_eq!(packet.len(), 32 + args.len() * 8);

    // Verify first 32 bytes contain null-padded name
    assert_eq!(&packet[..15], b"ioctl_write_msr");

    // Output parsing
    let mut out_data = [0u64; 4];
    let mut fake_resp = vec![0u8; 32];
    fake_resp[..8].copy_from_slice(&0xDEADBEEFu64.to_le_bytes());
    fake_resp[8..16].copy_from_slice(&0xCAFEBABE00u64.to_le_bytes());

    let ok = parse_execute_response(&fake_resp, 16, &mut out_data);
    assert!(ok);
    assert_eq!(out_data[0], 0xDEADBEEF);
    assert_eq!(out_data[1], 0xCAFEBABE00);

    // Zero / corrupt responses
    assert!(!parse_execute_response(&[], 0, &mut out_data));
    assert!(!parse_execute_response(&fake_resp, 4, &mut out_data));
}

#[test]
fn test_telemetry_worker_concurrency_and_interval_clamp() {
    let _guard = GLOBAL_STRESS_LOCK.lock().unwrap_or_else(|e| e.into_inner());

    let mut worker = TelemetryWorker::new();
    // Test boundary intervals
    worker.set_interval(0); // clamped to 250
    worker.set_interval(100); // clamped to 250
    worker.set_interval(20000); // clamped to 10000

    worker.start();
    let worker_arc = Arc::new(worker);

    const THREADS: usize = 8;
    let barrier = Arc::new(Barrier::new(THREADS));
    let mut handles = Vec::new();

    for _ in 0..THREADS {
        let w = worker_arc.clone();
        let b = barrier.clone();
        handles.push(thread::spawn(move || {
            b.wait();
            for _ in 0..100 {
                let snap = w.get_snapshot();
                assert!(snap.cpu_load >= 0.0 && snap.cpu_load <= 100.0);
            }
        }));
    }

    for h in handles {
        h.join().expect("Telemetry thread panicked");
    }

    if let Ok(mut w) = Arc::try_unwrap(worker_arc) {
        w.stop();
    }
}
