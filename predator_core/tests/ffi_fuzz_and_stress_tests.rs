use predator_core::curve::ControlPoint;
use predator_core::thermal::NativeThermalDecision;
use predator_core::{
    predator_acpi_ppcc_entries, predator_acpi_ppcc_summary, predator_acpi_scan_tables,
    predator_audio_beat_strength, predator_audio_create, predator_audio_destroy,
    predator_audio_get_bands, predator_audio_get_level, predator_audio_is_beat,
    predator_audio_push, predator_audio_render_zones, predator_audio_reset, predator_core_cleanup,
    predator_core_init, predator_curve_create, predator_curve_destroy, predator_curve_interpolate,
    predator_curve_reset, predator_curve_update, predator_echid_close, predator_echid_enumerate,
    predator_echid_exchange, predator_echid_is_open, predator_echid_open,
    predator_echid_report_length, predator_nvapi_get_gpu_count, predator_nvapi_get_gpu_load,
    predator_nvapi_get_gpu_temperature, predator_nvapi_is_available, predator_nvapi_shutdown,
    predator_pawnio_apply_power_limits, predator_pawnio_is_available, predator_pawnio_read_mmio32,
    predator_pawnio_read_msr, predator_pawnio_write_mmio32, predator_pawnio_write_msr,
    predator_rgb_apply_brightness, predator_rgb_blend, predator_rgb_create, predator_rgb_delay_ms,
    predator_rgb_destroy, predator_rgb_frame_delay_ms, predator_rgb_hsv_to_rgb,
    predator_rgb_next_frame, predator_rgb_render_frame, predator_rgb_reset,
    predator_rgb_set_brightness, predator_telemetry_get_snapshot, predator_telemetry_set_interval,
    predator_thermal_create, predator_thermal_destroy, predator_thermal_evaluate,
    predator_thermal_reset, predator_thermal_set_profile,
};
use std::ptr::{null, null_mut};
use std::sync::Arc;
use std::thread;

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
        let pts = [ControlPoint {
            temp: 40,
            speed: 30,
        }];
        assert_eq!(predator_curve_update(handle, 50.0, pts.as_ptr(), 0, 0), 50);

        // Null handle with valid points
        assert_eq!(
            predator_curve_update(null_mut(), 50.0, pts.as_ptr(), pts.len(), 0),
            50
        );

        // Reset and destroy with null handle must not panic or fault
        predator_curve_reset(null_mut());
        predator_curve_destroy(null_mut());

        predator_curve_destroy(handle);

        // Interpolate with null pointers
        assert_eq!(predator_curve_interpolate(null(), 0, 50), 50);
        assert_eq!(predator_curve_interpolate(null(), 5, 50), 50);

        // Thermal evaluate with null handle / null decision pointer
        assert_eq!(
            predator_thermal_evaluate(null_mut(), 50.0, 50.0, 45, 15, 45, null_mut()),
            -1
        );
        let thermal = predator_thermal_create();
        assert_eq!(
            predator_thermal_evaluate(thermal, 50.0, 50.0, 45, 15, 45, null_mut()),
            -1
        );
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
            ControlPoint {
                temp: 30,
                speed: 20,
            },
            ControlPoint {
                temp: 50,
                speed: 40,
            },
            ControlPoint {
                temp: 70,
                speed: 70,
            },
            ControlPoint {
                temp: 90,
                speed: 100,
            },
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
        let temps = [
            -50.0,
            0.0,
            40.0,
            85.0,
            92.0,
            98.0,
            105.0,
            f64::NAN,
            f64::INFINITY,
        ];

        for &cpu in &temps {
            for &gpu in &temps {
                let res = predator_thermal_evaluate(
                    thermal,
                    cpu,
                    gpu,
                    55,
                    15,
                    45,
                    &mut decision as *mut _,
                );
                assert_eq!(res, 0);
                assert!((0..=3).contains(&decision.stage));
                assert!((0..=100).contains(&decision.cpu_fan_floor));
                assert!((0..=100).contains(&decision.gpu_fan_floor));
            }
        }

        assert_eq!(predator_thermal_reset(thermal), 0);
        assert_eq!(
            predator_thermal_set_profile(
                thermal, 1, 70.0, 90.0, 98.0, 65.0, 82.0, 88.0, 2.0, 90, 40, 12, 1, 0.4
            ),
            0
        );
        assert_eq!(
            predator_thermal_evaluate(thermal, 99.0, 50.0, 55, 15, 45, &mut decision as *mut _),
            0
        );
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
        ControlPoint {
            temp: 20,
            speed: 10,
        },
        ControlPoint {
            temp: 40,
            speed: 30,
        },
        ControlPoint {
            temp: 60,
            speed: 50,
        },
        ControlPoint {
            temp: 80,
            speed: 80,
        },
        ControlPoint {
            temp: 100,
            speed: 100,
        },
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
        let pts = [ControlPoint {
            temp: 40,
            speed: 20,
        }];
        assert_eq!(predator_curve_interpolate(pts.as_ptr(), usize::MAX, 50), 50);

        let handle = predator_curve_create(2);
        assert_eq!(
            predator_curve_update(handle, 50.0, pts.as_ptr(), usize::MAX, 0),
            50
        );

        // 2. Unaligned point buffer
        let pts_array = [
            ControlPoint {
                temp: 30,
                speed: 20,
            },
            ControlPoint {
                temp: 60,
                speed: 50,
            },
            ControlPoint {
                temp: 90,
                speed: 90,
            },
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
            ControlPoint {
                temp: 95,
                speed: 100,
            },
            ControlPoint {
                temp: 80,
                speed: 80,
            },
            ControlPoint {
                temp: 60,
                speed: 50,
            },
            ControlPoint {
                temp: 40,
                speed: 20,
            },
        ];
        assert_eq!(predator_curve_interpolate(inv.as_ptr(), inv.len(), 30), 20);
        assert_eq!(predator_curve_interpolate(inv.as_ptr(), inv.len(), 50), 35);
        assert_eq!(predator_curve_interpolate(inv.as_ptr(), inv.len(), 70), 65);
        assert_eq!(
            predator_curve_interpolate(inv.as_ptr(), inv.len(), 100),
            100
        );

        // Duplicate points with step jump
        let dup = [
            ControlPoint {
                temp: 50,
                speed: 20,
            },
            ControlPoint {
                temp: 50,
                speed: 80,
            },
        ];
        // At 50°C, fan jumps to 80% for safety
        assert_eq!(predator_curve_interpolate(dup.as_ptr(), dup.len(), 50), 80);
        assert_eq!(predator_curve_interpolate(dup.as_ptr(), dup.len(), 40), 20);
        assert_eq!(predator_curve_interpolate(dup.as_ptr(), dup.len(), 60), 80);

        // Single point
        let single = [ControlPoint {
            temp: 55,
            speed: 45,
        }];
        assert_eq!(
            predator_curve_interpolate(single.as_ptr(), single.len(), 20),
            45
        );
        assert_eq!(
            predator_curve_interpolate(single.as_ptr(), single.len(), 55),
            45
        );
        assert_eq!(
            predator_curve_interpolate(single.as_ptr(), single.len(), 90),
            45
        );

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
            (45, 90),   // Normal condition: PL1 <= PL2
            (120, 115), // Hazard condition: New PL1 > Old PL2
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
            let speed =
                predator_curve_update(handle, temp as f64, dense_pts.as_ptr(), dense_pts.len(), 0);
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
        assert_eq!(
            predator_rgb_render_frame(1, 10, 255, 0, 0, 255, frame_buf.as_mut_ptr(), 4),
            4
        );
        assert!(predator_rgb_frame_delay_ms(50) > 0);
        predator_rgb_set_brightness(anim, 200);
        predator_rgb_reset(anim);
        predator_rgb_destroy(anim);

        // RGB helpers
        let mut out_rgb = [0u8; 3];
        assert_eq!(
            predator_rgb_hsv_to_rgb(180.0, 1.0, 1.0, out_rgb.as_mut_ptr()),
            0
        );
        let mut cur_buf = [10u8; 12];
        let tgt_buf = [20u8; 12];
        assert_eq!(
            predator_rgb_blend(cur_buf.as_mut_ptr(), tgt_buf.as_ptr(), 4, 0.5),
            0
        );
        assert_eq!(
            predator_rgb_apply_brightness(cur_buf.as_mut_ptr(), 4, 128),
            0
        );

        // 2. Audio
        let audio = predator_audio_create(44100, 256, 8);
        if !audio.is_null() {
            let samples = [0.1f32, 0.5f32, -0.3f32, 0.0f32, f32::NAN, f32::INFINITY];
            assert_eq!(
                predator_audio_push(audio, samples.as_ptr(), samples.len(), 2),
                0
            );
            let mut bands = [0.0f32; 8];
            let bands_n = predator_audio_get_bands(audio, bands.as_mut_ptr(), 8);
            assert!(bands_n >= 0);
            let _ = predator_audio_get_level(audio);
            let _ = predator_audio_is_beat(audio);
            let _ = predator_audio_beat_strength(audio);
            let mut render_buf = [0u8; 24];
            let _ =
                predator_audio_render_zones(audio, 255, 255, 255, 100, render_buf.as_mut_ptr(), 8);
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
        let _ = predator_acpi_ppcc_entries(
            dummy_aml.as_ptr(),
            dummy_aml.len(),
            entries.as_mut_ptr(),
            4,
        );
        let mut summary = predator_core::acpi::PpccSummary::default();
        let _ = predator_acpi_ppcc_summary(dummy_aml.as_ptr(), dummy_aml.len(), &mut summary);
        let mut tables = [predator_core::acpi::AcpiTable::default(); 4];
        let _ =
            predator_acpi_scan_tables(dummy_aml.as_ptr(), dummy_aml.len(), tables.as_mut_ptr(), 4);
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
    use predator_core::hook::ShortcutEntry;
    use predator_core::migration_exports::*;

    unsafe {
        // 1. Misaligned handle safety (must never crash, segfault, or panic)
        let bad_rgb = 0x1 as *mut predator_core::rgb::RgbAnimator;
        let mut rgb_buf = [0u8; 12];
        assert_eq!(
            predator_rgb_next_frame(bad_rgb, rgb_buf.as_mut_ptr(), 4),
            -1
        );
        assert_eq!(predator_rgb_delay_ms(bad_rgb), 0);
        predator_rgb_set_brightness(bad_rgb, 50);
        predator_rgb_reset(bad_rgb);
        predator_rgb_destroy(bad_rgb);

        let bad_audio = 0x3 as *mut predator_core::audio::SpectrumAnalyzer;
        let mut bands_buf = [0.0f32; 16];
        assert_eq!(
            predator_audio_push(bad_audio, bands_buf.as_ptr(), 16, 2),
            -1
        );
        assert_eq!(
            predator_audio_get_bands(bad_audio, bands_buf.as_mut_ptr(), 16),
            -1
        );
        assert_eq!(predator_audio_get_level(bad_audio), 0.0);
        assert_eq!(predator_audio_is_beat(bad_audio), 0);
        assert_eq!(predator_audio_beat_strength(bad_audio), 0.0);
        assert_eq!(
            predator_audio_render_zones(bad_audio, 255, 0, 0, 100, rgb_buf.as_mut_ptr(), 4),
            -1
        );
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
        assert_eq!(
            predator_history_snapshot(bad_history, hist_snap.as_mut_ptr(), 8),
            0
        );
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
        assert_eq!(predator_hook_set_shortcuts(std::ptr::null(), 0), 0);
        predator_core::hook::with_hook((), |state| {
            assert_eq!(state.shortcut_count(), 0);
        });
        assert_eq!(predator_hook_set_shortcuts(unaligned_sc_ptr, 1), 1);
        predator_core::hook::with_hook((), |state| {
            assert_eq!(state.shortcut_count(), 1);
        });
        assert_eq!(predator_hook_set_shortcuts(unaligned_sc_ptr, 0), 0);
        predator_core::hook::with_hook((), |state| {
            assert_eq!(state.shortcut_count(), 0);
        });

        // 5. Unaligned samples for audio push
        let audio_handle = predator_audio_create(48000, 512, 16);
        assert!(!audio_handle.is_null());
        let mut unaligned_audio_mem = [0u8; 128];
        let unaligned_audio_ptr = unaligned_audio_mem.as_mut_ptr().add(1) as *mut f32;
        for i in 0..16 {
            std::ptr::write_unaligned(unaligned_audio_ptr.add(i), 0.5f32);
        }
        assert_eq!(
            predator_audio_push(audio_handle, unaligned_audio_ptr, 16, 1),
            0
        );
        predator_audio_destroy(audio_handle);

        // 6. Non-ASCII and invalid UTF-8 stress through procsync & pipeguard FFI
        let invalid_bytes = [0xFF, 0xFE, 0xFD, 0x80];
        let mut out_str_buf = [0u8; 32];
        assert_eq!(
            predator_procsync_executable_name(
                invalid_bytes.as_ptr(),
                invalid_bytes.len() as i32,
                out_str_buf.as_mut_ptr(),
                32
            ),
            0
        );
        assert_eq!(
            predator_procsync_process_name(
                invalid_bytes.as_ptr(),
                invalid_bytes.len() as i32,
                out_str_buf.as_mut_ptr(),
                32
            ),
            0
        );
        assert_eq!(
            predator_pipeguard_is_valid(invalid_bytes.as_ptr(), invalid_bytes.len() as i32),
            0
        );

        // Multibyte process name
        let unicode_proc = "a🎮b.exe".as_bytes();
        let written = predator_procsync_process_name(
            unicode_proc.as_ptr(),
            unicode_proc.len() as i32,
            out_str_buf.as_mut_ptr(),
            32,
        );
        assert!(written > 0);
        let extracted = std::str::from_utf8(&out_str_buf[..written as usize]).unwrap();
        assert_eq!(extracted, "a🎮b");
    }
}

#[test]
fn test_ffi_null_partition_pointer_protection() {
    use predator_core::migration_exports::*;
    use predator_core::thermal::NativeThermalDecision;

    unsafe {
        let low_ptrs = [0x8usize, 0x10, 0x20, 0x100, 0x1000, 0xFFF8];

        for &addr in &low_ptrs {
            // Curve follower
            let curve_handle = addr as *mut predator_core::curve::FanCurveFollower;
            let pts = [ControlPoint {
                temp: 40,
                speed: 20,
            }];
            assert_eq!(
                predator_curve_update(curve_handle, 50.0, pts.as_ptr(), pts.len(), 0),
                50
            );
            assert_eq!(
                predator_curve_update(
                    std::ptr::null_mut(),
                    50.0,
                    addr as *const ControlPoint,
                    1,
                    0
                ),
                50
            );
            assert_eq!(
                predator_curve_interpolate(addr as *const ControlPoint, 1, 50),
                50
            );
            predator_curve_reset(curve_handle);
            predator_curve_destroy(curve_handle);

            // Thermal coordinator
            let th_handle = addr as *mut predator_core::thermal::ThermalCoordinator;
            assert_eq!(
                predator_thermal_evaluate(th_handle, 50.0, 50.0, 45, 15, 45, std::ptr::null_mut()),
                -1
            );
            let real_th = predator_thermal_create();
            assert_eq!(
                predator_thermal_evaluate(
                    real_th,
                    50.0,
                    50.0,
                    45,
                    15,
                    45,
                    addr as *mut NativeThermalDecision
                ),
                -1
            );
            predator_thermal_destroy(real_th);
            assert_eq!(predator_thermal_reset(th_handle), -1);
            predator_thermal_destroy(th_handle);

            // RGB engine
            let rgb_handle = addr as *mut predator_core::rgb::RgbAnimator;
            assert_eq!(predator_rgb_next_frame(rgb_handle, addr as *mut u8, 4), -1);
            assert_eq!(predator_rgb_delay_ms(rgb_handle), 0);
            predator_rgb_set_brightness(rgb_handle, 100);
            predator_rgb_reset(rgb_handle);
            predator_rgb_destroy(rgb_handle);
            assert_eq!(predator_rgb_hsv_to_rgb(0.0, 1.0, 1.0, addr as *mut u8), -1);
            assert_eq!(
                predator_rgb_blend(addr as *mut u8, addr as *const u8, 4, 0.5),
                -1
            );
            assert_eq!(predator_rgb_apply_brightness(addr as *mut u8, 4, 128), -1);

            // Audio analyzer
            let audio_handle = addr as *mut predator_core::audio::SpectrumAnalyzer;
            assert_eq!(
                predator_audio_push(audio_handle, addr as *const f32, 16, 2),
                -1
            );
            assert_eq!(
                predator_audio_get_bands(audio_handle, addr as *mut f32, 16),
                -1
            );
            assert_eq!(predator_audio_get_level(audio_handle), 0.0);
            assert_eq!(predator_audio_is_beat(audio_handle), 0);
            assert_eq!(predator_audio_beat_strength(audio_handle), 0.0);
            assert_eq!(
                predator_audio_render_zones(audio_handle, 255, 255, 255, 100, addr as *mut u8, 4),
                -1
            );
            predator_audio_reset(audio_handle);
            predator_audio_destroy(audio_handle);

            // History buffer
            let hist_handle = addr as *mut std::ffi::c_void;
            predator_history_add(hist_handle, 42.0, 1);
            assert_eq!(predator_history_count(hist_handle), 0);
            assert_eq!(predator_history_get(hist_handle, 0, addr as *mut f64), 0);
            assert_eq!(predator_history_min(hist_handle, addr as *mut f64), 0);
            assert_eq!(predator_history_max(hist_handle, addr as *mut f64), 0);
            assert_eq!(predator_history_average(hist_handle, addr as *mut f64), 0);
            assert_eq!(predator_history_latest(hist_handle, addr as *mut f64), 0);
            assert_eq!(
                predator_history_snapshot(hist_handle, addr as *mut f64, 8),
                0
            );
            predator_history_clear(hist_handle);
            predator_history_destroy(hist_handle);

            // PawnIO & Telemetry & NVAPI out pointers
            assert_eq!(predator_pawnio_read_msr(0x610, addr as *mut u64), -1);
            assert_eq!(
                predator_pawnio_read_mmio32(0xFED159A0, addr as *mut u32),
                -1
            );
            assert_eq!(
                predator_telemetry_get_snapshot(
                    addr as *mut predator_core::telemetry::NativeTelemetrySnapshot
                ),
                -1
            );
            assert_eq!(predator_nvapi_get_gpu_load(addr as *mut f64), -1);
            assert_eq!(predator_nvapi_get_gpu_temperature(addr as *mut f64), -1);
        }
    }
}

#[test]
fn test_audio_band_edges_exhaustive_monotonicity() {
    use predator_core::audio::SpectrumAnalyzer;

    let fft_sizes = [64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384];
    for &fft_size in &fft_sizes {
        for band_count in 1..=64 {
            let analyzer = SpectrumAnalyzer::new(48000, fft_size, band_count);
            assert!(
                analyzer.is_some(),
                "Failed for fft_size: {}, band_count: {}",
                fft_size,
                band_count
            );
            let mut analyzer = analyzer.unwrap();
            let bands = analyzer.bands();
            assert_eq!(bands.len(), band_count);
        }
    }
}

#[test]
fn test_history_buffer_extreme_capacity_clamp() {
    use predator_core::history::HistoryBuffer;

    // Huge capacity must be clamped to MAX_CAPACITY without OOM or crash
    let huge = HistoryBuffer::new(i32::MAX);
    assert_eq!(huge.capacity(), 100_000);

    // Negative capacity must be clamped to minimum (2)
    let tiny = HistoryBuffer::new(-50);
    assert_eq!(tiny.capacity(), 2);
}

#[test]
fn test_curve_point_count_excessive_length() {
    unsafe {
        let pts = [ControlPoint {
            temp: 40,
            speed: 20,
        }];
        let handle = predator_curve_create(2);
        assert!(!handle.is_null());

        // Oversized point count (> 1024) must return safe fallback without executing
        let fallback = predator_curve_update(handle, 50.0, pts.as_ptr(), 1025, 30);
        assert_eq!(fallback, 50);

        let interp = predator_curve_interpolate(pts.as_ptr(), 2000, 50);
        assert_eq!(interp, 50);

        predator_curve_destroy(handle);
    }
}

#[test]
fn test_multithreaded_audio_spectrum_analyzer_high_load() {
    unsafe {
        let audio = predator_audio_create(48000, 1024, 16);
        assert!(!audio.is_null());

        let audio_addr = audio as usize;
        let running = Arc::new(std::sync::atomic::AtomicBool::new(true));

        let mut handles = Vec::new();

        // 4 Producer threads pushing audio samples concurrently
        for t in 0..4 {
            let running_clone = Arc::clone(&running);
            handles.push(thread::spawn(move || {
                let handle = audio_addr as *mut predator_core::audio::SpectrumAnalyzer;
                let mut samples = [0.0f32; 512];
                for i in 0..512 {
                    samples[i] = ((i + t * 30) as f32 * 0.1).sin();
                }
                while running_clone.load(std::sync::atomic::Ordering::Relaxed) {
                    assert_eq!(
                        predator_audio_push(handle, samples.as_ptr(), samples.len(), 2),
                        0
                    );
                    thread::yield_now();
                }
            }));
        }

        // 4 Consumer threads polling bands, level, and zones
        for _ in 0..4 {
            let running_clone = Arc::clone(&running);
            handles.push(thread::spawn(move || {
                let handle = audio_addr as *mut predator_core::audio::SpectrumAnalyzer;
                let mut bands = [0.0f32; 16];
                let mut zones = [0u8; 16 * 3];
                while running_clone.load(std::sync::atomic::Ordering::Relaxed) {
                    let n = predator_audio_get_bands(handle, bands.as_mut_ptr(), 16);
                    assert!(n >= 0);
                    let _ = predator_audio_get_level(handle);
                    let _ = predator_audio_is_beat(handle);
                    let _ = predator_audio_beat_strength(handle);
                    let z = predator_audio_render_zones(
                        handle,
                        255,
                        128,
                        64,
                        80,
                        zones.as_mut_ptr(),
                        16,
                    );
                    assert!(z >= 0);
                    thread::yield_now();
                }
            }));
        }

        // 1 Reset thread periodically resetting
        {
            let running_clone = Arc::clone(&running);
            handles.push(thread::spawn(move || {
                let handle = audio_addr as *mut predator_core::audio::SpectrumAnalyzer;
                while running_clone.load(std::sync::atomic::Ordering::Relaxed) {
                    predator_audio_reset(handle);
                    thread::sleep(std::time::Duration::from_millis(5));
                }
            }));
        }

        thread::sleep(std::time::Duration::from_millis(150));
        running.store(false, std::sync::atomic::Ordering::Relaxed);

        for h in handles {
            h.join()
                .expect("Audio analyzer thread panicked under high concurrent load!");
        }

        predator_audio_destroy(audio);
    }
}

#[test]
fn test_multithreaded_thermal_history_ring_buffer_high_load() {
    use predator_core::migration_exports::*;

    unsafe {
        let hist = predator_history_create(100);
        assert!(!hist.is_null());

        let hist_addr = hist as usize;
        let running = Arc::new(std::sync::atomic::AtomicBool::new(true));

        let mut handles = Vec::new();

        // 4 Producer threads adding samples concurrently
        for t in 0..4 {
            let running_clone = Arc::clone(&running);
            handles.push(thread::spawn(move || {
                let handle = hist_addr as *mut std::ffi::c_void;
                let mut counter = 0.0;
                while running_clone.load(std::sync::atomic::Ordering::Relaxed) {
                    predator_history_add(handle, counter + (t as f64 * 10.0), 1);
                    counter += 0.5;
                    thread::yield_now();
                }
            }));
        }

        // 4 Consumer threads reading stats & snapshots
        for _ in 0..4 {
            let running_clone = Arc::clone(&running);
            handles.push(thread::spawn(move || {
                let handle = hist_addr as *mut std::ffi::c_void;
                let mut snap = [0.0f64; 32];
                let mut val = 0.0f64;
                while running_clone.load(std::sync::atomic::Ordering::Relaxed) {
                    let _ = predator_history_count(handle);
                    let _ = predator_history_min(handle, &mut val);
                    let _ = predator_history_max(handle, &mut val);
                    let _ = predator_history_average(handle, &mut val);
                    let _ = predator_history_latest(handle, &mut val);
                    let n = predator_history_snapshot(handle, snap.as_mut_ptr(), snap.len() as i32);
                    assert!(n >= 0);
                    thread::yield_now();
                }
            }));
        }

        // 1 Cleaner thread periodically clearing
        {
            let running_clone = Arc::clone(&running);
            handles.push(thread::spawn(move || {
                let handle = hist_addr as *mut std::ffi::c_void;
                while running_clone.load(std::sync::atomic::Ordering::Relaxed) {
                    predator_history_clear(handle);
                    thread::sleep(std::time::Duration::from_millis(10));
                }
            }));
        }

        thread::sleep(std::time::Duration::from_millis(150));
        running.store(false, std::sync::atomic::Ordering::Relaxed);

        for h in handles {
            h.join()
                .expect("History ring buffer thread panicked under high concurrent load!");
        }

        predator_history_destroy(hist);
    }
}

#[test]
fn test_ffi_exhaustive_low_partition_guards() {
    use predator_core::lighting_rules::*;
    use predator_core::migration_exports::*;
    use predator_core::protocol_migration::*;
    use predator_core::smbios::*;

    unsafe {
        let low_addrs = [0x1usize, 0x8, 0x10, 0x100, 0x1000, 0xFFF0];

        for &addr in &low_addrs {
            eprintln!("addr: {:x}", addr);
            // Lighting rules
            let mut out_u32 = 0u32;
            eprintln!("L1");
            assert_eq!(
                predator_lighting_parse_hex(addr as *const u8, 4, &mut out_u32),
                0
            );
            eprintln!("L2");
            assert_eq!(
                predator_lighting_parse_hex(b"ff00".as_ptr(), 4, addr as *mut u32),
                0
            );
            eprintln!("L3");
            assert_eq!(
                predator_lighting_blend(0xFF000000, 0xFFFFFFFF, 0.5, addr as *mut u32),
                0
            );

            // Protocol migration
            eprintln!("PM1");
            assert_eq!(
                predator_protocol_encode(0, 10, 0, 0, 0, addr as *mut u64),
                0
            );

            // SMBIOS
            eprintln!("SM1");
            let mut smbios_out = [0u32; 10];
            let mut smbios_ver = 0i32;
            let dummy_table = [172u8, 6, 0, 0, 1, 2, 0, 0];
            assert_eq!(
                predator_smbios_parse(
                    addr as *const u8,
                    dummy_table.len(),
                    smbios_out.as_mut_ptr(),
                    10,
                    &mut smbios_ver
                ),
                -1
            );
            eprintln!("SM2");
            assert_eq!(
                predator_smbios_parse(
                    dummy_table.as_ptr(),
                    dummy_table.len(),
                    addr as *mut u32,
                    10,
                    &mut smbios_ver
                ),
                -1
            );
            eprintln!("SM3");
            assert_eq!(
                predator_smbios_parse(
                    dummy_table.as_ptr(),
                    dummy_table.len(),
                    smbios_out.as_mut_ptr(),
                    10,
                    addr as *mut i32
                ),
                -1
            );

            // ACPI
            eprintln!("AC1");
            let mut ppcc_entry = predator_core::acpi::PpccEntry::default();
            assert_eq!(
                predator_acpi_ppcc_entries(addr as *const u8, 10, &mut ppcc_entry, 1),
                -1
            );
            eprintln!("AC2");
            assert_eq!(
                predator_acpi_ppcc_entries(
                    dummy_table.as_ptr(),
                    0,
                    addr as *mut predator_core::acpi::PpccEntry,
                    1
                ),
                -1
            );
            eprintln!("AC3");
            assert_eq!(
                predator_acpi_ppcc_summary(addr as *const u8, 10, std::ptr::null_mut()),
                -1
            );
            eprintln!("AC4");
            let mut table_entry = predator_core::acpi::AcpiTable::default();
            assert_eq!(
                predator_acpi_scan_tables(addr as *const u8, 10, &mut table_entry, 1),
                -1
            );

            // ECHID
            eprintln!("EC1");
            let mut rep = [0u8; 16];
            assert_eq!(
                predator_echid_exchange(addr as *const u8, 16, rep.as_mut_ptr(), 16),
                -1
            );
            eprintln!("EC2");
            let req = [1u8, 2];
            assert_eq!(
                predator_echid_exchange(req.as_ptr(), req.len(), addr as *mut u8, 16),
                -1
            );

            // Graph projection
            eprintln!("GP1");
            let samples = [10.0f64, 20.0];
            let mut xy_out = [0.0f32; 4];
            assert_eq!(
                predator_graph_project_series(
                    addr as *const f64,
                    2,
                    10,
                    100.0,
                    100.0,
                    0.0,
                    50.0,
                    xy_out.as_mut_ptr()
                ),
                0
            );
            eprintln!("GP2");
            assert_eq!(
                predator_graph_project_series(
                    samples.as_ptr(),
                    2,
                    10,
                    100.0,
                    100.0,
                    0.0,
                    50.0,
                    addr as *mut f32
                ),
                0
            );

            // Procsync
            eprintln!("PS1");
            let mut str_buf = [0u8; 32];
            assert_eq!(
                predator_procsync_executable_name(addr as *const u8, 10, str_buf.as_mut_ptr(), 32),
                0
            );
            eprintln!("PS2");
            assert_eq!(
                predator_procsync_executable_name(b"game.exe".as_ptr(), 8, addr as *mut u8, 32),
                -1
            );
            eprintln!("PS3");
            assert_eq!(
                predator_procsync_process_name(addr as *const u8, 10, str_buf.as_mut_ptr(), 32),
                0
            );
            eprintln!("PS4");
            assert_eq!(
                predator_procsync_process_name(b"game.exe".as_ptr(), 8, addr as *mut u8, 32),
                -1
            );
            eprintln!("PS5");
            assert_eq!(
                predator_procsync_same_process(addr as *const u8, 10, b"game.exe".as_ptr(), 8),
                0
            );
            eprintln!("PS6");
            assert_eq!(
                predator_procsync_matches_running(addr as *const u8, 10, b"game.exe".as_ptr(), 8),
                0
            );

            // Pipeguard
            eprintln!("PG1");
            assert_eq!(
                predator_pipeguard_sanitize(addr as *const u8, 10, str_buf.as_mut_ptr(), 32),
                0
            );
            eprintln!("PG2");
            assert_eq!(
                predator_pipeguard_sanitize(b"msg".as_ptr(), 3, addr as *mut u8, 32),
                -1
            );
            eprintln!("PG3");
            assert_eq!(predator_pipeguard_is_valid(addr as *const u8, 10), 0);

            // Hook
            eprintln!("HK1");
            assert_eq!(
                predator_hook_set_shortcuts(addr as *const predator_core::hook::ShortcutEntry, 5),
                0
            );
            eprintln!("DONE addr");
        }
    }
}

#[test]
fn test_ffi_high_kernel_partition_pointer_protection() {
    use predator_core::curve::ControlPoint;
    use predator_core::lighting_rules::*;
    use predator_core::migration_exports::*;
    use predator_core::protocol_migration::*;
    use predator_core::smbios::*;
    use predator_core::thermal::NativeThermalDecision;

    unsafe {
        let high_addrs = [
            0x0000_8000_0000_0000usize,
            0x0000_FFFF_0000_0000usize,
            0xFFFF_0000_0000_0000usize,
            0xFFFF_FFFF_FFFF_0000usize,
            usize::MAX,
        ];

        for &addr in &high_addrs {
            // Curve follower & normalizer
            let curve_handle = addr as *mut predator_core::curve::FanCurveFollower;
            let pts = [ControlPoint { temp: 40, speed: 20 }];
            assert_eq!(predator_curve_update(curve_handle, 50.0, pts.as_ptr(), pts.len(), 0), 50);
            assert_eq!(predator_curve_update(std::ptr::null_mut(), 50.0, addr as *const ControlPoint, 1, 0), 50);
            assert_eq!(predator_curve_interpolate(addr as *const ControlPoint, 1, 50), 50);
            predator_curve_reset(curve_handle);
            predator_curve_destroy(curve_handle);

            let mut norm_pts = [ControlPoint::default(); 8];
            assert_eq!(predator_curve_normalize(addr as *const ControlPoint, 1, norm_pts.as_mut_ptr(), 8), 8);
            assert_eq!(predator_curve_normalize(pts.as_ptr(), 1, addr as *mut ControlPoint, 8), -1);

            // Thermal coordinator
            let th_handle = addr as *mut predator_core::thermal::ThermalCoordinator;
            assert_eq!(predator_thermal_evaluate(th_handle, 50.0, 50.0, 45, 15, 45, std::ptr::null_mut()), -1);
            let real_th = predator_thermal_create();
            assert_eq!(predator_thermal_evaluate(real_th, 50.0, 50.0, 45, 15, 45, addr as *mut NativeThermalDecision), -1);
            predator_thermal_destroy(real_th);
            assert_eq!(predator_thermal_reset(th_handle), -1);
            predator_thermal_destroy(th_handle);

            // RGB engine
            let rgb_handle = addr as *mut predator_core::rgb::RgbAnimator;
            assert_eq!(predator_rgb_next_frame(rgb_handle, addr as *mut u8, 4), -1);
            assert_eq!(predator_rgb_delay_ms(rgb_handle), 0);
            predator_rgb_set_brightness(rgb_handle, 100);
            predator_rgb_reset(rgb_handle);
            predator_rgb_destroy(rgb_handle);
            assert_eq!(predator_rgb_hsv_to_rgb(0.0, 1.0, 1.0, addr as *mut u8), -1);
            assert_eq!(predator_rgb_blend(addr as *mut u8, addr as *const u8, 4, 0.5), -1);
            assert_eq!(predator_rgb_apply_brightness(addr as *mut u8, 4, 128), -1);

            // Audio analyzer
            let audio_handle = addr as *mut predator_core::audio::SpectrumAnalyzer;
            assert_eq!(predator_audio_push(audio_handle, addr as *const f32, 16, 2), -1);
            assert_eq!(predator_audio_get_bands(audio_handle, addr as *mut f32, 16), -1);
            assert_eq!(predator_audio_get_level(audio_handle), 0.0);
            assert_eq!(predator_audio_is_beat(audio_handle), 0);
            assert_eq!(predator_audio_beat_strength(audio_handle), 0.0);
            assert_eq!(predator_audio_render_zones(audio_handle, 255, 255, 255, 100, addr as *mut u8, 4), -1);
            predator_audio_reset(audio_handle);
            predator_audio_destroy(audio_handle);

            // History buffer
            let hist_handle = addr as *mut std::ffi::c_void;
            predator_history_add(hist_handle, 42.0, 1);
            assert_eq!(predator_history_count(hist_handle), 0);
            assert_eq!(predator_history_get(hist_handle, 0, addr as *mut f64), 0);
            assert_eq!(predator_history_min(hist_handle, addr as *mut f64), 0);
            assert_eq!(predator_history_max(hist_handle, addr as *mut f64), 0);
            assert_eq!(predator_history_average(hist_handle, addr as *mut f64), 0);
            assert_eq!(predator_history_latest(hist_handle, addr as *mut f64), 0);
            assert_eq!(predator_history_snapshot(hist_handle, addr as *mut f64, 8), 0);
            predator_history_clear(hist_handle);
            predator_history_destroy(hist_handle);

            // Backlight manager
            let bl_handle = addr as *mut predator_core::backlight::BacklightStateManager;
            let mut out_i32 = 0i32;
            let mut out_i32_b = 0i32;
            assert_eq!(predator_backlight_evaluate_idle(bl_handle, 100, &mut out_i32, &mut out_i32_b), 0);
            assert_eq!(predator_backlight_on_activity(bl_handle, &mut out_i32, &mut out_i32_b), 0);
            assert_eq!(predator_backlight_is_on_battery(bl_handle, 1, 0), 0);
            assert_eq!(predator_backlight_on_lid_changed(bl_handle, 1, 1, 0, &mut out_i32, &mut out_i32_b), 0);
            assert_eq!(predator_backlight_on_suspend(bl_handle, &mut out_i32, &mut out_i32_b), 0);
            assert_eq!(predator_backlight_on_resume(bl_handle, 1, 0, &mut out_i32, &mut out_i32_b), 0);
            assert_eq!(predator_backlight_set_plugged_in(bl_handle, 1), 0);
            assert_eq!(predator_backlight_set_saved_ac(bl_handle, 50), 0);
            assert_eq!(predator_backlight_set_manual_battery(bl_handle, 1, 40), 0);
            assert_eq!(predator_backlight_set_idle_config(bl_handle, 60, 20), 0);
            assert_eq!(predator_backlight_record_activity(bl_handle), 0);
            predator_backlight_destroy(bl_handle);

            // CPU spec
            let mut spec = predator_core::cpu_spec::CpuHardwareSpec::default();
            assert_eq!(predator_cpuspec_detect(addr as *const u8, 10, &mut spec), 0);
            assert_eq!(predator_cpuspec_detect(b"Intel Core".as_ptr(), 10, addr as *mut _), -1);
            assert_eq!(predator_cpuspec_estimate_proportional_limits(45, 1, addr as *mut i32, &mut out_i32), -1);
            assert_eq!(predator_cpuspec_estimate_proportional_limits(45, 1, &mut out_i32, addr as *mut i32), -1);

            // Lighting rules
            let mut out_u32 = 0u32;
            assert_eq!(predator_lighting_parse_hex(addr as *const u8, 4, &mut out_u32), 0);
            assert_eq!(predator_lighting_parse_hex(b"ff00".as_ptr(), 4, addr as *mut u32), 0);
            assert_eq!(predator_lighting_blend(0xFF000000, 0xFFFFFFFF, 0.5, addr as *mut u32), 0);

            // Protocol migration
            assert_eq!(predator_protocol_encode(0, 10, 0, 0, 0, addr as *mut u64), 0);

            // SMBIOS
            let mut smbios_out = [0u32; 10];
            let mut smbios_ver = 0i32;
            let dummy_table = [172u8, 6, 0, 0, 1, 2, 0, 0];
            assert_eq!(predator_smbios_parse(addr as *const u8, dummy_table.len(), smbios_out.as_mut_ptr(), 10, &mut smbios_ver), -1);
            assert_eq!(predator_smbios_parse(dummy_table.as_ptr(), dummy_table.len(), addr as *mut u32, 10, &mut smbios_ver), -1);
            assert_eq!(predator_smbios_parse(dummy_table.as_ptr(), dummy_table.len(), smbios_out.as_mut_ptr(), 10, addr as *mut i32), -1);

            // ACPI
            let mut ppcc_entry = predator_core::acpi::PpccEntry::default();
            assert_eq!(predator_acpi_ppcc_entries(addr as *const u8, 10, &mut ppcc_entry, 1), -1);
            assert_eq!(predator_acpi_ppcc_entries(dummy_table.as_ptr(), 0, addr as *mut _, 1), -1);
            assert_eq!(predator_acpi_ppcc_summary(addr as *const u8, 10, std::ptr::null_mut()), -1);
            let mut table_entry = predator_core::acpi::AcpiTable::default();
            assert_eq!(predator_acpi_scan_tables(addr as *const u8, 10, &mut table_entry, 1), -1);
            assert_eq!(predator_acpi_scan_tables(dummy_table.as_ptr(), dummy_table.len(), addr as *mut _, 1), 0);

            // Battery ETA
            assert_eq!(predator_battery_eta_smoothed_rate(addr as *mut f64), 0);
            assert_eq!(predator_battery_eta_smoothed_percent_rate(addr as *mut f64), 0);
            assert_eq!(predator_battery_eta_calculate(1, 100, 100, 10.0, 1, 0, 100, 1, addr as *mut f64), 0);
            assert_eq!(predator_battery_eta_warmup(1, 100, addr as *mut f64), 0);

            // Procsync
            let mut str_buf = [0u8; 32];
            assert_eq!(predator_procsync_executable_name(addr as *const u8, 10, str_buf.as_mut_ptr(), 32), 0);
            assert_eq!(predator_procsync_executable_name(b"game.exe".as_ptr(), 8, addr as *mut u8, 32), -1);
            assert_eq!(predator_procsync_process_name(addr as *const u8, 10, str_buf.as_mut_ptr(), 32), 0);
            assert_eq!(predator_procsync_process_name(b"game.exe".as_ptr(), 8, addr as *mut u8, 32), -1);

            // Pipeguard
            assert_eq!(predator_pipeguard_sanitize(addr as *const u8, 10, str_buf.as_mut_ptr(), 32), 0);
            assert_eq!(predator_pipeguard_sanitize(b"msg".as_ptr(), 3, addr as *mut u8, 32), -1);
            assert_eq!(predator_pipeguard_is_valid(addr as *const u8, 10), 0);

            // Graph projection
            let samples = [10.0f64, 20.0];
            let mut xy_out = [0.0f32; 4];
            assert_eq!(predator_graph_project_series(addr as *const f64, 2, 10, 100.0, 100.0, 0.0, 50.0, xy_out.as_mut_ptr()), 0);
            assert_eq!(predator_graph_project_series(samples.as_ptr(), 2, 10, 100.0, 100.0, 0.0, 50.0, addr as *mut f32), 0);

            // Hook
            assert_eq!(predator_hook_set_shortcuts(addr as *const predator_core::hook::ShortcutEntry, 5), 0);

            // UEFI
            let mut uefi_cfg = predator_core::uefi::UefiPowerConfig::default();
            assert_eq!(predator_uefi_scan_power_and_locks(addr as *const u8, 10, &mut uefi_cfg), -1);
            assert_eq!(predator_uefi_scan_power_and_locks(dummy_table.as_ptr(), dummy_table.len(), addr as *mut _), -1);
            assert_eq!(predator_uefi_parse_variable_power(addr as *const u8, 10, &mut uefi_cfg), -1);
            assert_eq!(predator_uefi_parse_variable_power(dummy_table.as_ptr(), dummy_table.len(), addr as *mut _), -1);
            let mut raw_bat = predator_core::battery::RawBatteryInfo::default();
            assert_eq!(predator_battery_parse_info(addr as *const u8, 36, &mut raw_bat), -1);
            assert_eq!(predator_battery_parse_info(dummy_table.as_ptr(), 36, addr as *mut _), -1);
        }
    }
}

#[test]
fn test_ffi_misaligned_handles_backlight_and_curve() {
    use predator_core::curve::ControlPoint;
    use predator_core::migration_exports::*;

    unsafe {
        // Misaligned BacklightStateManager handles (>= 0x10000 but unaligned)
        let unaligned_bl_addrs = [0x10001usize, 0x10002, 0x10003, 0x10005, 0x10007];
        for &addr in &unaligned_bl_addrs {
            let bad_handle = addr as *mut predator_core::backlight::BacklightStateManager;
            let mut out1 = 0i32;
            let mut out2 = 0i32;
            assert_eq!(predator_backlight_evaluate_idle(bad_handle, 100, &mut out1, &mut out2), 0);
            assert_eq!(predator_backlight_on_activity(bad_handle, &mut out1, &mut out2), 0);
            assert_eq!(predator_backlight_is_on_battery(bad_handle, 1, 0), 0);
            assert_eq!(predator_backlight_on_lid_changed(bad_handle, 1, 1, 0, &mut out1, &mut out2), 0);
            assert_eq!(predator_backlight_on_suspend(bad_handle, &mut out1, &mut out2), 0);
            assert_eq!(predator_backlight_on_resume(bad_handle, 1, 0, &mut out1, &mut out2), 0);
            assert_eq!(predator_backlight_set_plugged_in(bad_handle, 1), 0);
            assert_eq!(predator_backlight_set_saved_ac(bad_handle, 50), 0);
            assert_eq!(predator_backlight_set_manual_battery(bad_handle, 1, 40), 0);
            assert_eq!(predator_backlight_set_idle_config(bad_handle, 60, 20), 0);
            assert_eq!(predator_backlight_record_activity(bad_handle), 0);
            // destroy on unaligned handle MUST NOT panic or free unaligned memory
            predator_backlight_destroy(bad_handle);
        }

        // Test curve normalize with unaligned input and output buffers
        let raw_pts = [
            ControlPoint { temp: 40, speed: 20 },
            ControlPoint { temp: 60, speed: 45 },
            ControlPoint { temp: 80, speed: 70 },
        ];
        let mut unaligned_in_mem = [0u8; 128];
        let unaligned_in_ptr = unaligned_in_mem.as_mut_ptr().add(1) as *mut ControlPoint;
        for i in 0..3 {
            std::ptr::write_unaligned(unaligned_in_ptr.add(i), raw_pts[i]);
        }

        let mut unaligned_out_mem = [0u8; 128];
        let unaligned_out_ptr = unaligned_out_mem.as_mut_ptr().add(1) as *mut ControlPoint;

        let res = predator_curve_normalize(unaligned_in_ptr, 3, unaligned_out_ptr, 8);
        assert_eq!(res, 8);
        for i in 0..8 {
            let p = std::ptr::read_unaligned(unaligned_out_ptr.add(i));
            assert!(p.temp >= 30 && p.temp <= 100);
            assert!(p.speed >= 0 && p.speed <= 100);
        }

        // Test predator_rgb_blend with identical/aliasing buffer
        let mut blend_mem = [128u8; 32];
        let blend_res = predator_rgb_blend(blend_mem.as_mut_ptr(), blend_mem.as_ptr(), 4, 0.5);
        assert_eq!(blend_res, 0);
    }
}

#[test]
fn test_ffi_integer_overflow_and_extreme_shift_limits() {
    use predator_core::migration_exports::*;
    use predator_core::protocol_migration::*;

    unsafe {
        // Protocol op 18 shift overflow tests
        let mut out_u64 = 0u64;
        assert_eq!(predator_protocol_encode(18, 0, 2, 0, 0, &mut out_u64), 1);
        assert_eq!(predator_protocol_encode(18, 23, 2, 0, 0, &mut out_u64), 1);
        // Exceeding fan index 23 must return 0 safely without panic
        assert_eq!(predator_protocol_encode(18, 24, 2, 0, 0, &mut out_u64), 0);
        assert_eq!(predator_protocol_encode(18, 64, 2, 0, 0, &mut out_u64), 0);
        assert_eq!(predator_protocol_encode(18, u64::MAX, 2, 0, 0, &mut out_u64), 0);

        // Direct internal encode tests
        assert!(encode(18, 64, 0, 0, 0).is_none());
        assert!(encode(18, u64::MAX, 0, 0, 0).is_none());

        // Graph projection with NaN / Inf dimensions
        let samples = [10.0f64, 20.0];
        let mut xy_out = [0.0f32; 4];
        assert_eq!(predator_graph_project_series(samples.as_ptr(), 2, 10, f32::NAN, 100.0, 0.0, 50.0, xy_out.as_mut_ptr()), 0);
        assert_eq!(predator_graph_project_series(samples.as_ptr(), 2, 10, 100.0, f32::NAN, 0.0, 50.0, xy_out.as_mut_ptr()), 0);
        assert_eq!(predator_graph_project_series(samples.as_ptr(), 2, 10, f32::INFINITY, 100.0, 0.0, 50.0, xy_out.as_mut_ptr()), 0);
        assert_eq!(predator_graph_project_series(samples.as_ptr(), 2, 10, -50.0, 100.0, 0.0, 50.0, xy_out.as_mut_ptr()), 0);

        // Curve normalizer with excessive length
        let mut norm_pts = [predator_core::curve::ControlPoint::default(); 8];
        assert_eq!(predator_curve_normalize(samples.as_ptr() as *const _, usize::MAX, norm_pts.as_mut_ptr(), 8), 8);
    }
}

#[test]
fn test_ffi_history_multithreaded_hammering_concurrency() {
    use predator_core::migration_exports::*;
    use std::sync::atomic::{AtomicBool, Ordering};

    unsafe {
        let hist = predator_history_create(128);
        assert!(!hist.is_null());

        let running = Arc::new(AtomicBool::new(true));
        let hist_addr = hist as usize;
        let mut threads = Vec::new();

        // 8 writers
        for t in 0..8 {
            let r = Arc::clone(&running);
            threads.push(thread::spawn(move || {
                let h = hist_addr as *mut std::ffi::c_void;
                let mut val = (t * 100) as f64;
                while r.load(Ordering::Relaxed) {
                    predator_history_add(h, val, 1);
                    val += 1.0;
                    thread::yield_now();
                }
            }));
        }

        // 6 readers
        for _ in 0..6 {
            let r = Arc::clone(&running);
            threads.push(thread::spawn(move || {
                let h = hist_addr as *const std::ffi::c_void;
                let mut buf = [0.0f64; 64];
                let mut val = 0.0f64;
                while r.load(Ordering::Relaxed) {
                    let _ = predator_history_count(h);
                    let _ = predator_history_min(h, &mut val);
                    let _ = predator_history_max(h, &mut val);
                    let _ = predator_history_average(h, &mut val);
                    let _ = predator_history_latest(h, &mut val);
                    let n = predator_history_snapshot(h, buf.as_mut_ptr(), 64);
                    assert!(n >= 0);
                    thread::yield_now();
                }
            }));
        }

        // 2 clearers
        for _ in 0..2 {
            let r = Arc::clone(&running);
            threads.push(thread::spawn(move || {
                let h = hist_addr as *mut std::ffi::c_void;
                while r.load(Ordering::Relaxed) {
                    predator_history_clear(h);
                    thread::sleep(std::time::Duration::from_millis(5));
                }
            }));
        }

        thread::sleep(std::time::Duration::from_millis(200));
        running.store(false, Ordering::Relaxed);

        for t in threads {
            t.join().expect("History multithreaded hammering thread panicked!");
        }

        predator_history_destroy(hist);
    }
}

#[test]
fn test_ffi_concurrent_multithreaded_all_subsystems_hammering() {
    use predator_core::curve::ControlPoint;
    use predator_core::migration_exports::*;
    use predator_core::thermal::NativeThermalDecision;
    use std::sync::atomic::{AtomicBool, Ordering};

    unsafe {
        let running = Arc::new(AtomicBool::new(true));
        let mut threads = Vec::new();

        // Subsystem 1: Curve followers & normalizers
        for t in 0..4 {
            let r = Arc::clone(&running);
            threads.push(thread::spawn(move || {
                let handle = predator_curve_create(t % 4);
                let pts = [
                    ControlPoint { temp: 30, speed: 20 },
                    ControlPoint { temp: 50, speed: 40 },
                    ControlPoint { temp: 70, speed: 70 },
                    ControlPoint { temp: 90, speed: 100 },
                ];
                let mut norm = [ControlPoint::default(); 8];
                let mut temp = 25.0;
                while r.load(Ordering::Relaxed) {
                    let spd = predator_curve_update(handle, temp, pts.as_ptr(), pts.len(), 25);
                    assert!(spd >= 10 && spd <= 100);
                    let _ = predator_curve_normalize(pts.as_ptr(), pts.len(), norm.as_mut_ptr(), 8);
                    temp = (temp + 1.5) % 105.0;
                    thread::yield_now();
                }
                predator_curve_destroy(handle);
            }));
        }

        // Subsystem 2: Thermal coordinators
        for _ in 0..4 {
            let r = Arc::clone(&running);
            threads.push(thread::spawn(move || {
                let th = predator_thermal_create();
                let mut dec = NativeThermalDecision::idle();
                while r.load(Ordering::Relaxed) {
                    assert_eq!(predator_thermal_evaluate(th, 65.0, 60.0, 55, 15, 45, &mut dec), 0);
                    assert_eq!(predator_thermal_reset(th), 0);
                    thread::yield_now();
                }
                predator_thermal_destroy(th);
            }));
        }

        // Subsystem 3: Backlight state managers
        for _ in 0..4 {
            let r = Arc::clone(&running);
            threads.push(thread::spawn(move || {
                let bl = predator_backlight_create(80);
                let mut out1 = 0i32;
                let mut out2 = 0i32;
                let mut sec = 1000i64;
                while r.load(Ordering::Relaxed) {
                    let _ = predator_backlight_evaluate_idle(bl, sec, &mut out1, &mut out2);
                    let _ = predator_backlight_on_activity(bl, &mut out1, &mut out2);
                    let _ = predator_backlight_record_activity(bl);
                    sec += 10;
                    thread::yield_now();
                }
                predator_backlight_destroy(bl);
            }));
        }

        // Subsystem 4: Procsync and Pipeguard
        for _ in 0..4 {
            let r = Arc::clone(&running);
            threads.push(thread::spawn(move || {
                let mut buf = [0u8; 64];
                while r.load(Ordering::Relaxed) {
                    let n = predator_procsync_executable_name(b"C:\\Games\\Cyberpunk2077\\bin\\x64\\Cyberpunk2077.exe".as_ptr(), 50, buf.as_mut_ptr(), 64);
                    assert!(n > 0);
                    let s = predator_pipeguard_sanitize(b"STATUS\\r\\nOK\\0EXTRA".as_ptr(), 16, buf.as_mut_ptr(), 64);
                    assert!(s > 0);
                    thread::yield_now();
                }
            }));
        }

        thread::sleep(std::time::Duration::from_millis(250));
        running.store(false, Ordering::Relaxed);

        for t in threads {
            t.join().expect("Subsystem stress thread panicked!");
        }
    }
}
