use std::ptr::{null, null_mut};
use std::sync::Arc;
use std::thread;
use predator_core::curve::ControlPoint;
use predator_core::{
    predator_core_init, predator_core_cleanup,
    predator_curve_create, predator_curve_destroy, predator_curve_interpolate,
    predator_curve_reset, predator_curve_update,
    predator_pawnio_is_available, predator_pawnio_apply_power_limits,
    predator_pawnio_read_mmio32, predator_pawnio_write_mmio32,
    predator_pawnio_read_msr, predator_pawnio_write_msr,
    predator_telemetry_get_snapshot, predator_telemetry_set_interval,
    predator_thermal_evaluate, predator_thermal_create, predator_thermal_destroy,
    predator_thermal_reset, predator_thermal_set_profile,
    predator_rgb_create, predator_rgb_next_frame, predator_rgb_render_frame, predator_rgb_frame_delay_ms, predator_rgb_delay_ms,
    predator_rgb_set_brightness, predator_rgb_reset, predator_rgb_destroy, predator_rgb_hsv_to_rgb,
    predator_rgb_blend, predator_rgb_apply_brightness,
    predator_audio_create, predator_audio_destroy, predator_audio_push, predator_audio_get_bands,
    predator_audio_get_level, predator_audio_is_beat, predator_audio_beat_strength,
    predator_audio_render_zones, predator_audio_reset,
    predator_nvapi_is_available, predator_nvapi_get_gpu_load, predator_nvapi_get_gpu_temperature,
    predator_nvapi_get_gpu_count, predator_nvapi_shutdown,
    predator_acpi_ppcc_entries, predator_acpi_ppcc_summary, predator_acpi_scan_tables,
    predator_echid_open, predator_echid_close, predator_echid_is_open, predator_echid_report_length,
    predator_echid_exchange, predator_echid_enumerate,
};
use predator_core::thermal::NativeThermalDecision;

#[test]
fn test_ffi_null_safety_guards() {
    unsafe {
        // Curve operations with null pointers
        assert_eq!(predator_curve_update(null_mut(), 50.0, null(), 0, 0), 50);

        let handle = predator_curve_create(2);
        assert!(!handle.is_null());

        // Null points pointer with positive count
        assert_eq!(predator_curve_update(handle, 50.0, null(), 4, 0), 50);

        // Zero point count with valid pointer
        let pts = [ControlPoint { temp: 40, speed: 30 }];
        assert_eq!(predator_curve_update(handle, 50.0, pts.as_ptr(), 0, 0), 50);

        // Null handle with valid points
        assert_eq!(predator_curve_update(null_mut(), 50.0, pts.as_ptr(), pts.len(), 0), 50);

        // Reset and destroy with null handle must not panic or fault
        predator_curve_reset(null_mut());
        predator_curve_destroy(null_mut());

        predator_curve_destroy(handle);

        // Interpolate with null pointers
        assert_eq!(predator_curve_interpolate(null(), 0, 50), 50);
        assert_eq!(predator_curve_interpolate(null(), 5, 50), 50);

        // Thermal evaluate with null handle / null decision pointer
        assert_eq!(predator_thermal_evaluate(null_mut(), 50.0, 50.0, 45, 15, 45, null_mut()), -1);
        let thermal = predator_thermal_create();
        assert_eq!(predator_thermal_evaluate(thermal, 50.0, 50.0, 45, 15, 45, null_mut()), -1);
        assert_eq!(predator_thermal_reset(null_mut()), -1);
        predator_thermal_destroy(null_mut());
        predator_thermal_destroy(thermal);

        // PawnIO with null pointers
        assert_eq!(predator_pawnio_read_msr(0x610, null_mut()), -1);
        assert_eq!(predator_pawnio_read_mmio32(0xFED159A0, null_mut()), -1);
        // A null output pointer does not prevent writes; do not apply limits here.

        // Telemetry with null snapshot pointer
        assert_eq!(predator_telemetry_get_snapshot(null_mut()), -1);
    }
}

#[test]
fn test_ffi_extreme_fuzz_temperatures() {
    unsafe {
        let handle = predator_curve_create(3);
        assert!(!handle.is_null());

        let pts = [
            ControlPoint { temp: 30, speed: 20 },
            ControlPoint { temp: 50, speed: 40 },
            ControlPoint { temp: 70, speed: 70 },
            ControlPoint { temp: 90, speed: 100 },
        ];

        let extremes = [
            f64::NAN,
            f64::INFINITY,
            f64::NEG_INFINITY,
            -1000.0,
            -50.0,
            0.0,
            0.0001,
            29.999,
            50.0,
            50.5,
            69.0,
            70.0,
            89.9,
            90.0,
            90.1,
            150.0,
            1000.0,
            1e9,
        ];

        for &temp in &extremes {
            let speed = predator_curve_update(handle, temp, pts.as_ptr(), pts.len(), 0);
            assert!(
                (10..=100).contains(&speed),
                "Speed {} out of bounds for temperature {}",
                speed,
                temp
            );
        }

        predator_curve_destroy(handle);
    }
}

#[test]
fn test_ffi_thermal_coordinator_fuzzing() {
    unsafe {
        let thermal = predator_thermal_create();
        assert!(!thermal.is_null());
        let mut decision = NativeThermalDecision::idle();
        let temps = [-50.0, 0.0, 40.0, 85.0, 92.0, 98.0, 105.0, f64::NAN, f64::INFINITY];

        for &cpu in &temps {
            for &gpu in &temps {
                let res = predator_thermal_evaluate(thermal, cpu, gpu, 55, 15, 45, &mut decision as *mut _);
                assert_eq!(res, 0);
                assert!((0..=3).contains(&decision.stage));
                assert!((0..=100).contains(&decision.cpu_fan_floor));
                assert!((0..=100).contains(&decision.gpu_fan_floor));
            }
        }

        assert_eq!(predator_thermal_reset(thermal), 0);
        assert_eq!(predator_thermal_set_profile(thermal, 1, 70.0, 90.0, 98.0, 65.0, 82.0, 88.0, 2.0, 90, 40, 12, 1, 0.4), 0);
        assert_eq!(predator_thermal_evaluate(thermal, 99.0, 50.0, 55, 15, 45, &mut decision as *mut _), 0);
        assert_eq!(decision.stage, 3);
        predator_thermal_destroy(thermal);
    }
}

#[test]
fn test_ffi_concurrent_init_cleanup_no_deadlock() {
    // Regression: init used to hold PAWNIO then TELEMETRY, while cleanup held
    // TELEMETRY then PAWNIO, which deadlocked when both ran concurrently.
    let mut handles = Vec::new();
    for _ in 0..3 {
        handles.push(thread::spawn(|| {
            for _ in 0..10 {
                unsafe { predator_core_init() };
            }
        }));
        handles.push(thread::spawn(|| {
            for _ in 0..10 {
                unsafe { predator_core_cleanup() };
            }
        }));
    }
    for h in handles {
        h.join().expect("init/cleanup deadlocked or panicked");
    }
    unsafe { predator_core_cleanup() };
}

#[test]
fn test_ffi_concurrent_multithreaded_stress() {
    const NUM_THREADS: usize = 8;
    const ITERATIONS: usize = 1000;

    let pts = Arc::new(vec![
        ControlPoint { temp: 20, speed: 10 },
        ControlPoint { temp: 40, speed: 30 },
        ControlPoint { temp: 60, speed: 50 },
        ControlPoint { temp: 80, speed: 80 },
        ControlPoint { temp: 100, speed: 100 },
    ]);

    let mut handles = Vec::with_capacity(NUM_THREADS);

    for t in 0..NUM_THREADS {
        let pts_clone = Arc::clone(&pts);
        handles.push(thread::spawn(move || {
            unsafe {
                let curve_handle = predator_curve_create(t as i32 % 5);
                assert!(!curve_handle.is_null());
                // Per-thread thermal state: handles must not share latches or floors.
                let thermal_handle = predator_thermal_create();
                assert!(!thermal_handle.is_null());

                for i in 0..ITERATIONS {
                    let temp = 20.0 + ((i + t * 13) % 90) as f64;
                    let speed = predator_curve_update(
                        curve_handle,
                        temp,
                        pts_clone.as_ptr(),
                        pts_clone.len(),
                        (i % 50) as i32,
                    );
                    assert!((10..=100).contains(&speed));

                    let interp = predator_curve_interpolate(
                        pts_clone.as_ptr(),
                        pts_clone.len(),
                        temp as i32,
                    );
                    assert!((10..=100).contains(&interp));

                    let mut dec = NativeThermalDecision::idle();
                    let th_res = predator_thermal_evaluate(
                        thermal_handle,
                        temp,
                        temp - 5.0,
                        55,
                        15,
                        45,
                        &mut dec as *mut _,
                    );
                    assert_eq!(th_res, 0);
                }

                predator_thermal_destroy(thermal_handle);
                predator_curve_destroy(curve_handle);
            }
        }));
    }

    for h in handles {
        h.join().expect("Thread panicked in stress test");
    }
}


#[test]
fn test_ffi_unaligned_and_extreme_buffer_stress() {
    unsafe {
        // 1. Extreme buffer length (usize::MAX) must be rejected safely without crash
        let pts = [ControlPoint { temp: 40, speed: 20 }];
        assert_eq!(predator_curve_interpolate(pts.as_ptr(), usize::MAX, 50), 50);

        let handle = predator_curve_create(2);
        assert_eq!(predator_curve_update(handle, 50.0, pts.as_ptr(), usize::MAX, 0), 50);

        // 2. Unaligned point buffer
        let pts_array = [
            ControlPoint { temp: 30, speed: 20 },
            ControlPoint { temp: 60, speed: 50 },
            ControlPoint { temp: 90, speed: 90 },
        ];
        let mut unaligned_mem = [0u8; 64];
        let unaligned_ptr = unaligned_mem.as_mut_ptr().add(1) as *mut ControlPoint;
        std::ptr::copy_nonoverlapping(
            pts_array.as_ptr() as *const u8,
            unaligned_ptr as *mut u8,
            std::mem::size_of_val(&pts_array),
        );

        let res = predator_curve_interpolate(unaligned_ptr, pts_array.len(), 45);
        assert_eq!(res, 35);

        let res_update = predator_curve_update(handle, 45.0, unaligned_ptr, pts_array.len(), 0);
        assert_eq!(res_update, 35);

        predator_curve_destroy(handle);
    }
}

#[test]
fn test_ffi_inverted_and_duplicate_curve_stress() {
    unsafe {
        // Inverted points
        let inv = [
            ControlPoint { temp: 95, speed: 100 },
            ControlPoint { temp: 80, speed: 80 },
            ControlPoint { temp: 60, speed: 50 },
            ControlPoint { temp: 40, speed: 20 },
        ];
        assert_eq!(predator_curve_interpolate(inv.as_ptr(), inv.len(), 30), 20);
        assert_eq!(predator_curve_interpolate(inv.as_ptr(), inv.len(), 50), 35);
        assert_eq!(predator_curve_interpolate(inv.as_ptr(), inv.len(), 70), 65);
        assert_eq!(predator_curve_interpolate(inv.as_ptr(), inv.len(), 100), 100);

        // Duplicate points with step jump
        let dup = [
            ControlPoint { temp: 50, speed: 20 },
            ControlPoint { temp: 50, speed: 80 },
        ];
        // At 50°C, fan jumps to 80% for safety
        assert_eq!(predator_curve_interpolate(dup.as_ptr(), dup.len(), 50), 80);
        assert_eq!(predator_curve_interpolate(dup.as_ptr(), dup.len(), 40), 20);
        assert_eq!(predator_curve_interpolate(dup.as_ptr(), dup.len(), 60), 80);

        // Single point
        let single = [ControlPoint { temp: 55, speed: 45 }];
        assert_eq!(predator_curve_interpolate(single.as_ptr(), single.len(), 20), 45);
        assert_eq!(predator_curve_interpolate(single.as_ptr(), single.len(), 55), 45);
        assert_eq!(predator_curve_interpolate(single.as_ptr(), single.len(), 90), 45);

        // Empty points
        assert_eq!(predator_curve_interpolate(single.as_ptr(), 0, 50), 50);
    }
}


#[test]
#[ignore = "writes real CPU power limits and registers; use designated test hardware"]
fn test_ffi_pawnio_fuzz_and_hazard_limits() {
    unsafe {
        assert_eq!(predator_core_init(), 0);
        let avail = predator_pawnio_is_available();
        assert!(avail == 0 || avail == 1);

        // Fuzz apply power limits with directional write hazards and extreme values
        let test_pairs: &[(u32, u32)] = &[
            (0, 0),
            (45, 90),     // Normal condition: PL1 <= PL2
            (120, 115),   // Hazard condition: New PL1 > Old PL2
            (150, 150),
            (250, 250),
            (10, 10),
            (u32::MAX, u32::MAX),
            (100, 0),
            (0, 100),
        ];

        for &(pl1, pl2) in test_pairs {
            let mut mmio_mirrored = 0i32;
            let res = predator_pawnio_apply_power_limits(pl1, pl2, &mut mmio_mirrored);
            // Returns 0 (success in mock/hardware) or hardware error code, never panics
            assert!(res <= 0 || res == 0);
        }

        // Fuzz MSR read/write
        let mut msr_val = 0u64;
        let _ = predator_pawnio_read_msr(0x606, &mut msr_val);
        let _ = predator_pawnio_read_msr(0x610, &mut msr_val);
        let _ = predator_pawnio_read_msr(0x1A2, &mut msr_val);
        let _ = predator_pawnio_write_msr(0x610, msr_val);

        // Fuzz MMIO read/write
        let mut mmio_val = 0u32;
        let _ = predator_pawnio_read_mmio32(0xFED159A0, &mut mmio_val);
        let _ = predator_pawnio_write_mmio32(0xFED159A0, mmio_val);

        predator_core_cleanup();
    }
}

#[test]
fn test_ffi_telemetry_stress_and_concurrency() {
    unsafe {
        assert_eq!(predator_core_init(), 0);
        predator_telemetry_set_interval(250);

        let mut handles = Vec::new();
        for _ in 0..4 {
            handles.push(thread::spawn(|| {
                for _ in 0..100 {
                    let mut snap = predator_core::telemetry::NativeTelemetrySnapshot::default();
                    let res = predator_telemetry_get_snapshot(&mut snap);
                    assert!(res == 0 || res == -2);
                }
            }));
        }

        for h in handles {
            h.join().unwrap();
        }

        predator_core_cleanup();
    }
}

#[test]
fn test_ffi_curve_fuzz_random_floats_and_densities() {
    unsafe {
        let handle = predator_curve_create(2);
        assert!(!handle.is_null());

        // Dense points array exceeding STACK_SORT_CAPACITY (64)
        let mut dense_pts = Vec::with_capacity(75);
        for i in 0..75 {
            dense_pts.push(ControlPoint {
                temp: 20 + i * 2,
                speed: 10 + (i * 90 / 75),
            });
        }

        // Fuzz interpolation across dense heap-sorted slice
        for temp in -50..=150 {
            let speed = predator_curve_update(handle, temp as f64, dense_pts.as_ptr(), dense_pts.len(), 0);
            assert!((10..=100).contains(&speed));

            let interp = predator_curve_interpolate(dense_pts.as_ptr(), dense_pts.len(), temp);
            assert!((10..=100).contains(&interp));
        }

        predator_curve_destroy(handle);
    }
}

#[test]
fn test_ffi_rgb_audio_nvapi_acpi_fuzz() {
    unsafe {
        // 1. RGB
        let anim = predator_rgb_create(0, 255, 0, 128, 255, 100, 1, 4);
        assert!(!anim.is_null());
        let mut frame_buf = [0u8; 32];
        let written = predator_rgb_next_frame(anim, frame_buf.as_mut_ptr(), 4);
        assert!(written >= 0);
        assert_eq!(predator_rgb_render_frame(1, 10, 255, 0, 0, 255, frame_buf.as_mut_ptr(), 4), 4);
        assert!(predator_rgb_frame_delay_ms(50) > 0);
        predator_rgb_set_brightness(anim, 200);
        predator_rgb_reset(anim);
        predator_rgb_destroy(anim);

        // RGB helpers
        let mut out_rgb = [0u8; 3];
        assert_eq!(predator_rgb_hsv_to_rgb(180.0, 1.0, 1.0, out_rgb.as_mut_ptr()), 0);
        let mut cur_buf = [10u8; 12];
        let tgt_buf = [20u8; 12];
        assert_eq!(predator_rgb_blend(cur_buf.as_mut_ptr(), tgt_buf.as_ptr(), 4, 0.5), 0);
        assert_eq!(predator_rgb_apply_brightness(cur_buf.as_mut_ptr(), 4, 128), 0);

        // 2. Audio
        let audio = predator_audio_create(44100, 256, 8);
        if !audio.is_null() {
            let samples = [0.1f32, 0.5f32, -0.3f32, 0.0f32, f32::NAN, f32::INFINITY];
            assert_eq!(predator_audio_push(audio, samples.as_ptr(), samples.len(), 2), 0);
            let mut bands = [0.0f32; 8];
            let bands_n = predator_audio_get_bands(audio, bands.as_mut_ptr(), 8);
            assert!(bands_n >= 0);
            let _ = predator_audio_get_level(audio);
            let _ = predator_audio_is_beat(audio);
            let _ = predator_audio_beat_strength(audio);
            let mut render_buf = [0u8; 24];
            let _ = predator_audio_render_zones(audio, 255, 255, 255, 100, render_buf.as_mut_ptr(), 8);
            predator_audio_reset(audio);
            predator_audio_destroy(audio);
        }

        // 3. NVAPI
        let _ = predator_nvapi_is_available();
        let mut gpu_load = 0.0;
        let _ = predator_nvapi_get_gpu_load(&mut gpu_load);
        let mut gpu_temp = 0.0;
        let _ = predator_nvapi_get_gpu_temperature(&mut gpu_temp);
        let _ = predator_nvapi_get_gpu_count();
        predator_nvapi_shutdown();

        // 4. ACPI
        let dummy_aml = [0x50, 0x50, 0x43, 0x43, 0x12, 0x0A, 0x04]; // PPCC signature prefix
        let mut entries = [predator_core::acpi::PpccEntry::default(); 4];
        let _ = predator_acpi_ppcc_entries(dummy_aml.as_ptr(), dummy_aml.len(), entries.as_mut_ptr(), 4);
        let mut summary = predator_core::acpi::PpccSummary::default();
        let _ = predator_acpi_ppcc_summary(dummy_aml.as_ptr(), dummy_aml.len(), &mut summary);
        let mut tables = [predator_core::acpi::AcpiTable::default(); 4];
        let _ = predator_acpi_scan_tables(dummy_aml.as_ptr(), dummy_aml.len(), tables.as_mut_ptr(), 4);

    }
}

#[test]
#[ignore = "sends a report to an EC HID device; use designated test hardware"]
fn test_ffi_echid_hardware_exchange() {
    unsafe {
        let _ = predator_echid_open(0x04B4, 0x0100, 0xFF00, 0x0001);
        let _ = predator_echid_is_open();
        let _ = predator_echid_report_length();
        let mut req = [0x01, 0x02];
        let mut rep = [0u8; 64];
        let _ = predator_echid_exchange(req.as_mut_ptr(), req.len(), rep.as_mut_ptr(), rep.len());
        let _ = predator_echid_enumerate(10);
        predator_echid_close();
    }
}

#[test]
fn test_ffi_misaligned_handles_and_migration_stress() {
    use predator_core::migration_exports::*;
    use predator_core::hook::ShortcutEntry;

    unsafe {
        // 1. Misaligned handle safety (must never crash, segfault, or panic)
        let bad_rgb = 0x1 as *mut predator_core::rgb::RgbAnimator;
        let mut rgb_buf = [0u8; 12];
        assert_eq!(predator_rgb_next_frame(bad_rgb, rgb_buf.as_mut_ptr(), 4), -1);
        assert_eq!(predator_rgb_delay_ms(bad_rgb), 0);
        predator_rgb_set_brightness(bad_rgb, 50);
        predator_rgb_reset(bad_rgb);
        predator_rgb_destroy(bad_rgb);

        let bad_audio = 0x3 as *mut predator_core::audio::SpectrumAnalyzer;
        let mut bands_buf = [0.0f32; 16];
        assert_eq!(predator_audio_push(bad_audio, bands_buf.as_ptr(), 16, 2), -1);
        assert_eq!(predator_audio_get_bands(bad_audio, bands_buf.as_mut_ptr(), 16), -1);
        assert_eq!(predator_audio_get_level(bad_audio), 0.0);
        assert_eq!(predator_audio_is_beat(bad_audio), 0);
        assert_eq!(predator_audio_beat_strength(bad_audio), 0.0);
        assert_eq!(predator_audio_render_zones(bad_audio, 255, 0, 0, 100, rgb_buf.as_mut_ptr(), 4), -1);
        predator_audio_reset(bad_audio);
        predator_audio_destroy(bad_audio);

        let bad_history = 0x5 as *mut std::ffi::c_void;
        predator_history_add(bad_history, 42.0, 1);
        assert_eq!(predator_history_count(bad_history), 0);
        let mut out_val = 0.0;
        assert_eq!(predator_history_get(bad_history, 0, &mut out_val), 0);
        assert_eq!(predator_history_min(bad_history, &mut out_val), 0);
        assert_eq!(predator_history_max(bad_history, &mut out_val), 0);
        assert_eq!(predator_history_average(bad_history, &mut out_val), 0);
        assert_eq!(predator_history_latest(bad_history, &mut out_val), 0);
        let mut hist_snap = [0.0f64; 8];
        assert_eq!(predator_history_snapshot(bad_history, hist_snap.as_mut_ptr(), 8), 0);
        predator_history_clear(bad_history);
        predator_history_destroy(bad_history);

        // 2. Unaligned buffer stress for history snapshot
        let hist = predator_history_create(10);
        assert!(!hist.is_null());
        for i in 1..=5 {
            predator_history_add(hist, i as f64 * 10.0, 1);
        }
        let mut unaligned_hist_mem = [0u8; 128];
        let unaligned_hist_out = unaligned_hist_mem.as_mut_ptr().add(1) as *mut f64;
        let count_written = predator_history_snapshot(hist, unaligned_hist_out, 5);
        assert_eq!(count_written, 5);
        for i in 0..5 {
            let val = std::ptr::read_unaligned(unaligned_hist_out.add(i));
            assert_eq!(val, (i + 1) as f64 * 10.0);
        }
        predator_history_destroy(hist);

        // 3. Unaligned buffer stress for graph projection
        let samples = [10.0f64, 20.0, 30.0, 40.0];
        let mut unaligned_samples_mem = [0u8; 128];
        let unaligned_samples_ptr = unaligned_samples_mem.as_mut_ptr().add(3) as *mut f64;
        for i in 0..4 {
            std::ptr::write_unaligned(unaligned_samples_ptr.add(i), samples[i]);
        }
        let mut unaligned_xy_mem = [0u8; 128];
        let unaligned_xy_ptr = unaligned_xy_mem.as_mut_ptr().add(1) as *mut f32;
        let proj_count = predator_graph_project_series(
            unaligned_samples_ptr,
            4,
            10,
            100.0,
            100.0,
            0.0,
            50.0,
            unaligned_xy_ptr,
        );
        assert_eq!(proj_count, 4);

        // 4. Unaligned buffer stress for hook shortcuts
        let sc = ShortcutEntry {
            vk: 0x41, // 'A'
            mode_code: 1,
            ctrl: 1,
            alt: 0,
            shift: 0,
            win: 0,
            enabled: 1,
        };
        let mut unaligned_sc_mem = [0u8; 64];
        let unaligned_sc_ptr = unaligned_sc_mem.as_mut_ptr().add(1) as *mut ShortcutEntry;
        std::ptr::write_unaligned(unaligned_sc_ptr, sc);
        assert_eq!(predator_hook_set_shortcuts(unaligned_sc_ptr, 1), 1);

        // 5. Unaligned samples for audio push
        let audio_handle = predator_audio_create(48000, 512, 16);
        assert!(!audio_handle.is_null());
        let mut unaligned_audio_mem = [0u8; 128];
        let unaligned_audio_ptr = unaligned_audio_mem.as_mut_ptr().add(1) as *mut f32;
        for i in 0..16 {
            std::ptr::write_unaligned(unaligned_audio_ptr.add(i), 0.5f32);
        }
        assert_eq!(predator_audio_push(audio_handle, unaligned_audio_ptr, 16, 1), 0);
        predator_audio_destroy(audio_handle);

        // 6. Non-ASCII and invalid UTF-8 stress through procsync & pipeguard FFI
        let invalid_bytes = [0xFF, 0xFE, 0xFD, 0x80];
        let mut out_str_buf = [0u8; 32];
        assert_eq!(predator_procsync_executable_name(invalid_bytes.as_ptr(), invalid_bytes.len() as i32, out_str_buf.as_mut_ptr(), 32), 0);
        assert_eq!(predator_procsync_process_name(invalid_bytes.as_ptr(), invalid_bytes.len() as i32, out_str_buf.as_mut_ptr(), 32), 0);
        assert_eq!(predator_pipeguard_is_valid(invalid_bytes.as_ptr(), invalid_bytes.len() as i32), 0);

        // Multibyte process name
        let unicode_proc = "a🎮b.exe".as_bytes();
        let written = predator_procsync_process_name(unicode_proc.as_ptr(), unicode_proc.len() as i32, out_str_buf.as_mut_ptr(), 32);
        assert!(written > 0);
        let extracted = std::str::from_utf8(&out_str_buf[..written as usize]).unwrap();
        assert_eq!(extracted, "a🎮b");
    }
}
