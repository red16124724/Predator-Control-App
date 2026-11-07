pub mod acpi;
pub mod audio;
pub mod backlight;
pub mod battery;
pub mod bios;
pub mod capability;
pub mod power_limits;
pub mod service_proto;
pub mod settings_policy;
pub mod theme_policy;
pub mod wmi_payload;
pub mod catalog_policy;
pub mod cpu_spec;
pub mod curve;
pub mod display;
pub mod ec_hid;
pub mod game_profile;
pub mod gameprobe;
pub mod graphproj;
pub mod history;
pub mod hook;
pub mod lighting_rules;
pub mod migration_exports;
pub mod model_catalog;
pub mod nvapi;
pub mod pawnio;
pub mod pipeguard;
pub mod procsync;
pub mod protocol_migration;
pub mod rgb;
pub mod shortcuts;
pub mod smbios;
pub mod telemetry;
pub mod thermal;
pub mod uefi;
pub mod wmi_codec;

use acpi::{AcpiTable, PpccEntry, PpccSummary};
use audio::SpectrumAnalyzer;
use curve::{ControlPoint, FanCurveFollower};
use ec_hid::EcHid;
use pawnio::{MchbarWritePlan, PawnIoDriver};
use rgb::{Rgb8, RgbAnimator};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::Mutex;
use telemetry::{NativeTelemetrySnapshot, TelemetryWorker};
use thermal::{NativeThermalDecision, ThermalCoordinator, ThermalSafetyProfile};

static GLOBAL_PAWNIO: Mutex<Option<PawnIoDriver>> = Mutex::new(None);
static GLOBAL_TELEMETRY: Mutex<Option<TelemetryWorker>> = Mutex::new(None);
static GLOBAL_ECHID: Mutex<Option<EcHid>> = Mutex::new(None);

/// Serializes init/cleanup so a concurrent init cannot interleave with a cleanup.
static LIFECYCLE: Mutex<()> = Mutex::new(());

/// Ensures no panic unwinds across the C-ABI FFI boundary, preventing undefined behavior.
/// Ensures no panic unwinds across the C-ABI FFI boundary, preventing undefined behavior.
#[inline]
fn safe_ffi<F, R>(default: R, f: F) -> R
where
    F: FnOnce() -> R,
{
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(ret) => ret,
        Err(_) => default,
    }
}

pub(crate) const MIN_USER_ADDR: usize = 0x10000;
pub(crate) const MAX_USER_ADDR: usize = 0x0000_7FFF_FFFF_0000;

/// Returns true when `ptr` is within valid Windows 64-bit user-mode address space
/// (outside NULL-pointer low partition and kernel partition) and satisfies natural alignment for `T`.
#[inline]
#[allow(dead_code)]
pub(crate) fn is_valid_ptr<T>(ptr: *const T) -> bool {
    let addr = ptr as usize;
    addr >= MIN_USER_ADDR && addr <= MAX_USER_ADDR && (addr % std::mem::align_of::<T>() == 0)
}

/// Returns true when `ptr` is within valid Windows 64-bit user-mode address space
/// (outside NULL-pointer low partition and kernel partition) and satisfies natural alignment for `T`.
#[inline]
pub(crate) fn is_valid_mut_ptr<T>(ptr: *mut T) -> bool {
    let addr = ptr as usize;
    addr >= MIN_USER_ADDR && addr <= MAX_USER_ADDR && (addr % std::mem::align_of::<T>() == 0)
}

/// Returns true when `ptr` is within valid Windows 64-bit user-mode address space.
/// Used for byte buffers or foreign buffers read via unaligned loads.
#[inline]
pub(crate) fn is_valid_buffer<T>(ptr: *const T) -> bool {
    let addr = ptr as usize;
    addr >= MIN_USER_ADDR && addr <= MAX_USER_ADDR
}

/// Returns true when `ptr` is within valid Windows 64-bit user-mode address space.
/// Used for byte buffers or foreign buffers written via unaligned stores.
#[inline]
pub(crate) fn is_valid_mut_buffer<T>(ptr: *mut T) -> bool {
    let addr = ptr as usize;
    addr >= MIN_USER_ADDR && addr <= MAX_USER_ADDR
}

/// Returns true when a buffer range of `count` elements of type `T` starting at `ptr`
/// lies entirely within valid user-mode space without integer wrap-around.
#[inline]
#[allow(dead_code)]
pub(crate) fn is_valid_buffer_range<T>(ptr: *const T, count: usize) -> bool {
    let addr = ptr as usize;
    if addr < MIN_USER_ADDR || addr > MAX_USER_ADDR {
        return false;
    }
    match count.checked_mul(std::mem::size_of::<T>()) {
        Some(byte_len) => {
            if byte_len > isize::MAX as usize {
                return false;
            }
            match addr.checked_add(byte_len) {
                Some(end) => end <= MAX_USER_ADDR + 0x10000,
                None => false,
            }
        }
        None => false,
    }
}

#[inline]
#[allow(dead_code)]
pub(crate) fn is_valid_mut_buffer_range<T>(ptr: *mut T, count: usize) -> bool {
    is_valid_buffer_range(ptr as *const T, count)
}

/// Helper executing an operation on the global PawnIoDriver with poison recovery.
fn with_pawnio<F, R>(default: R, f: F) -> R
where
    F: FnOnce(&mut PawnIoDriver) -> R,
{
    let mut guard = GLOBAL_PAWNIO.lock().unwrap_or_else(|e| e.into_inner());
    if guard.is_none() {
        *guard = Some(PawnIoDriver::new());
    }
    if let Some(driver) = guard.as_mut() {
        f(driver)
    } else {
        default
    }
}

#[no_mangle]
pub unsafe extern "C" fn predator_core_init() -> i32 {
    safe_ffi(-1, || {
        let _lifecycle = LIFECYCLE.lock().unwrap_or_else(|e| e.into_inner());

        {
            let mut t_guard = GLOBAL_TELEMETRY.lock().unwrap_or_else(|e| e.into_inner());
            if t_guard.is_none() {
                let mut worker = TelemetryWorker::new();
                worker.start();
                *t_guard = Some(worker);
            }
        }

        0
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_core_cleanup() {
    safe_ffi((), || {
        let _lifecycle = LIFECYCLE.lock().unwrap_or_else(|e| e.into_inner());

        let worker_to_stop = {
            let mut t_guard = GLOBAL_TELEMETRY.lock().unwrap_or_else(|e| e.into_inner());
            t_guard.take()
        };
        if let Some(mut worker) = worker_to_stop {
            worker.stop();
        }

        {
            let mut p_guard = GLOBAL_PAWNIO.lock().unwrap_or_else(|e| e.into_inner());
            p_guard.take();
        }

        {
            let mut ec_guard = GLOBAL_ECHID.lock().unwrap_or_else(|e| e.into_inner());
            ec_guard.take();
        }

        ec_hid::predator_enek_close();
        audio::predator_audio_capture_stop();
        game_profile::shutdown();

        nvapi::shutdown();
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_is_available() -> i32 {
    safe_ffi(0, || {
        with_pawnio(0, |driver| if driver.is_available() { 1 } else { 0 })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_read_msr(msr: u32, out_val: *mut u64) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_val) {
            return -1;
        }
        with_pawnio(-7, |driver| match driver.read_msr(msr) {
            Ok(val) => {
                std::ptr::write_unaligned(out_val, val);
                0
            }
            Err(e) => e,
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_write_msr(msr: u32, val: u64) -> i32 {
    safe_ffi(-1, || {
        with_pawnio(-7, |driver| match driver.write_msr(msr, val) {
            Ok(()) => 0,
            Err(e) => e,
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_read_mmio32(addr: u64, out_val: *mut u32) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_val) {
            return -1;
        }
        with_pawnio(-7, |driver| match driver.read_mmio32(addr) {
            Ok(val) => {
                std::ptr::write_unaligned(out_val, val);
                0
            }
            Err(e) => e,
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_write_mmio32(addr: u64, val: u32) -> i32 {
    safe_ffi(-1, || {
        with_pawnio(-7, |driver| match driver.write_mmio32(addr, val) {
            Ok(()) => 0,
            Err(e) => e,
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_plan_msr_write(
    current_msr_limit: u64,
    raw_pl1: u64,
    raw_pl2: u64,
    out_value: *mut u64,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_value) {
            return -1;
        }
        match pawnio::PawnIoController::plan_msr_power_limit(current_msr_limit, raw_pl1, raw_pl2) {
            Ok(value) => {
                std::ptr::write_unaligned(out_value, value);
                0
            }
            Err(status) => status,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_plan_mchbar_write(
    current_mmio: u64,
    read_ok: i32,
    raw_pl1: u64,
    raw_pl2: u64,
    fallback_tau1: u64,
    fallback_tau2: u64,
    pl1_address: u64,
    pl2_address: u64,
    out_plan: *mut MchbarWritePlan,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_plan) {
            return -1;
        }
        let plan = pawnio::plan_mchbar_power_write(
            current_mmio,
            read_ok != 0,
            raw_pl1,
            raw_pl2,
            fallback_tau1,
            fallback_tau2,
            pl1_address,
            pl2_address,
        );
        std::ptr::write_unaligned(out_plan, plan);
        plan.status
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_apply_power_limits(
    target_pl1: u32,
    target_pl2: u32,
    out_mmio_mirrored: *mut i32,
) -> i32 {
    safe_ffi(-1, || {
        with_pawnio(-7, |driver| {
            match driver.write_rapl_limits_directional(target_pl1, target_pl2) {
                Ok(mirrored) => {
                    if is_valid_mut_buffer(out_mmio_mirrored) {
                        std::ptr::write_unaligned(out_mmio_mirrored, if mirrored { 1 } else { 0 });
                    }
                    0
                }
                Err(e) => e,
            }
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_telemetry_get_snapshot(
    out_snapshot: *mut NativeTelemetrySnapshot,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_snapshot) {
            return -1;
        }
        let guard = GLOBAL_TELEMETRY.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(worker) = guard.as_ref() {
            let snap = worker.get_snapshot();
            std::ptr::write_unaligned(out_snapshot, snap);
            0
        } else {
            -2
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_telemetry_set_interval(ms: u32) -> i32 {
    safe_ffi(-1, || {
        let guard = GLOBAL_TELEMETRY.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(worker) = guard.as_ref() {
            worker.set_interval(ms);
            0
        } else {
            -2
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_curve_create(hysteresis: i32) -> *mut FanCurveFollower {
    safe_ffi(std::ptr::null_mut(), || {
        let follower = Box::new(FanCurveFollower::new(hysteresis));
        Box::into_raw(follower)
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_curve_update(
    handle: *mut FanCurveFollower,
    temp: f64,
    points_ptr: *const ControlPoint,
    point_count: usize,
    safety_floor: i32,
) -> i32 {
    safe_ffi(safety_floor.clamp(0, 100).max(50), || {
        let fallback = safety_floor.clamp(0, 100).max(50);
        if !is_valid_mut_ptr(handle)
            || !is_valid_buffer(points_ptr)
            || point_count == 0
            || point_count > 1024
        {
            return fallback;
        }

        let follower = &mut *handle;

        if (points_ptr as usize) % std::mem::align_of::<ControlPoint>() == 0 {
            let points = std::slice::from_raw_parts(points_ptr, point_count);
            follower.update_f64(temp, points, safety_floor)
        } else {
            // Unaligned pointer from foreign runtime: read safely via read_unaligned
            if point_count <= 64 {
                let mut aligned = [ControlPoint::default(); 64];
                for i in 0..point_count {
                    aligned[i] = std::ptr::read_unaligned(points_ptr.add(i));
                }
                follower.update_f64(temp, &aligned[..point_count], safety_floor)
            } else {
                let mut aligned = Vec::with_capacity(point_count);
                for i in 0..point_count {
                    aligned.push(std::ptr::read_unaligned(points_ptr.add(i)));
                }
                follower.update_f64(temp, &aligned, safety_floor)
            }
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_curve_interpolate(
    points_ptr: *const ControlPoint,
    point_count: usize,
    temp: i32,
) -> i32 {
    safe_ffi(50, || {
        if !is_valid_buffer(points_ptr) || point_count == 0 || point_count > 1024 {
            return 50;
        }

        if (points_ptr as usize) % std::mem::align_of::<ControlPoint>() == 0 {
            let points = std::slice::from_raw_parts(points_ptr, point_count);
            curve::interpolate_curve(points, temp)
        } else {
            if point_count <= 64 {
                let mut aligned = [ControlPoint::default(); 64];
                for i in 0..point_count {
                    aligned[i] = std::ptr::read_unaligned(points_ptr.add(i));
                }
                curve::interpolate_curve(&aligned[..point_count], temp)
            } else {
                let mut aligned = Vec::with_capacity(point_count);
                for i in 0..point_count {
                    aligned.push(std::ptr::read_unaligned(points_ptr.add(i)));
                }
                curve::interpolate_curve(&aligned, temp)
            }
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_curve_reset(handle: *mut FanCurveFollower) {
    safe_ffi((), || {
        if is_valid_mut_ptr(handle) {
            let follower = &mut *handle;
            follower.reset();
        }
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_curve_destroy(handle: *mut FanCurveFollower) {
    safe_ffi((), || {
        if is_valid_mut_ptr(handle) {
            let _ = Box::from_raw(handle);
        }
    });
}

// ---------------------------------------------------------------------------
// Thermal coordinator handles (thermal.rs)
//
// The coordinator is per-instance state: latches, filtered temperatures, and
// fan-floor pacing all belong to one caller. A process-wide singleton made
// independent coordinators (and therefore independent tests) share state, so
// ownership follows the same handle pattern as the curve and RGB engines.
// ---------------------------------------------------------------------------

/// # Safety
/// The returned pointer must be released with [`predator_thermal_destroy`].
#[no_mangle]
pub unsafe extern "C" fn predator_thermal_create() -> *mut ThermalCoordinator {
    safe_ffi(std::ptr::null_mut(), || {
        Box::into_raw(Box::new(ThermalCoordinator::new(
            ThermalSafetyProfile::default(),
        )))
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_thermal_destroy(handle: *mut ThermalCoordinator) {
    safe_ffi((), || {
        if is_valid_mut_ptr(handle) {
            let _ = Box::from_raw(handle);
        }
    });
}

/// Runs `f` against a validated coordinator handle, returning `default` when the
/// pointer is null, misaligned, or the caller panics.
#[inline]
unsafe fn with_thermal<F, R>(handle: *mut ThermalCoordinator, default: R, f: F) -> R
where
    F: FnOnce(&mut ThermalCoordinator) -> R,
{
    if !is_valid_mut_ptr(handle) {
        return default;
    }
    safe_ffi(default, || f(&mut *handle))
}

#[no_mangle]
pub unsafe extern "C" fn predator_thermal_set_profile(
    handle: *mut ThermalCoordinator,
    enabled: i32,
    cpu_warn: f64,
    cpu_ceiling: f64,
    cpu_critical: f64,
    gpu_warn: f64,
    gpu_ceiling: f64,
    gpu_critical: f64,
    hysteresis: f64,
    max_fan_floor: i32,
    cpu_power_cap_watts: i32,
    min_power_cap_watts: i32,
    critical_fans_full_speed: i32,
    ema_alpha: f64,
) -> i32 {
    safe_ffi(-1, || {
        with_thermal(handle, -1, |coordinator| {
            coordinator.set_profile(ThermalSafetyProfile {
                enabled: enabled != 0,
                cpu_warn,
                cpu_ceiling,
                cpu_critical,
                gpu_warn,
                gpu_ceiling,
                gpu_critical,
                hysteresis,
                max_fan_floor,
                cpu_power_cap_watts,
                min_power_cap_watts,
                critical_fans_full_speed: critical_fans_full_speed != 0,
                ema_alpha,
            });
            0
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_thermal_reset(handle: *mut ThermalCoordinator) -> i32 {
    safe_ffi(-1, || {
        with_thermal(handle, -1, |coordinator| {
            coordinator.reset();
            0
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_thermal_evaluate(
    handle: *mut ThermalCoordinator,
    cpu_temp: f64,
    gpu_temp: f64,
    target_pl1: i32,
    min_power: i32,
    base_tdp: i32,
    out_decision: *mut NativeThermalDecision,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_decision) {
            return -1;
        }
        with_thermal(handle, -1, |coordinator| {
            let decision =
                coordinator.evaluate(cpu_temp, gpu_temp, target_pl1, min_power, base_tdp);
            std::ptr::write_unaligned(out_decision, decision);
            0
        })
    })
}

// ---------------------------------------------------------------------------
// RGB animation engine (LightingEffects34.cs)
// ---------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn predator_rgb_create(
    effect: i32,
    r: u8,
    g: u8,
    b: u8,
    brightness: u8,
    speed: u8,
    direction: u8,
    zones: usize,
) -> *mut RgbAnimator {
    safe_ffi(std::ptr::null_mut(), || {
        let anim = Box::new(RgbAnimator::new(
            effect,
            Rgb8 { r, g, b },
            brightness,
            speed,
            direction,
            zones,
        ));
        Box::into_raw(anim)
    })
}

/// Renders the next frame as `zones` interleaved RGB triples. Returns zones written.
#[no_mangle]
pub unsafe extern "C" fn predator_rgb_next_frame(
    handle: *mut RgbAnimator,
    out: *mut u8,
    zones: usize,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_ptr(handle) || !is_valid_mut_buffer(out) || zones == 0 {
            return -1;
        }
        let anim = &mut *handle;
        // Rgb8 is #[repr(C)] with three u8 fields: layout-compatible with RGB triples.
        let slice = std::slice::from_raw_parts_mut(out as *mut Rgb8, zones.min(rgb::MAX_ZONES));
        anim.next_frame(slice) as i32
    })
}

/// Stateless frame render for callers that own the cursor themselves.
#[no_mangle]
pub unsafe extern "C" fn predator_rgb_render_frame(
    effect: i32,
    step: u64,
    r: u8,
    g: u8,
    b: u8,
    brightness: u8,
    out: *mut u8,
    zones: usize,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(out) || zones == 0 {
            return -1;
        }
        let slice = std::slice::from_raw_parts_mut(out as *mut Rgb8, zones.min(rgb::MAX_ZONES));
        rgb::render_frame(effect, step, Rgb8 { r, g, b }, brightness, slice) as i32
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_rgb_frame_delay_ms(speed: u8) -> u32 {
    safe_ffi(125, || rgb::frame_delay_ms(speed))
}

#[no_mangle]
pub unsafe extern "C" fn predator_rgb_delay_ms(handle: *mut RgbAnimator) -> u32 {
    safe_ffi(0, || {
        if !is_valid_mut_ptr(handle) {
            return 0;
        }
        (*handle).delay_ms()
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_rgb_set_brightness(handle: *mut RgbAnimator, brightness: u8) {
    safe_ffi((), || {
        if is_valid_mut_ptr(handle) {
            (*handle).set_brightness(brightness);
        }
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_rgb_reset(handle: *mut RgbAnimator) {
    safe_ffi((), || {
        if is_valid_mut_ptr(handle) {
            (*handle).reset();
        }
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_rgb_destroy(handle: *mut RgbAnimator) {
    safe_ffi((), || {
        if is_valid_mut_ptr(handle) {
            let _ = Box::from_raw(handle);
        }
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_rgb_hsv_to_rgb(
    hue: f64,
    saturation: f64,
    value: f64,
    out: *mut u8,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(out) {
            return -1;
        }
        let c = rgb::hsv_to_rgb(hue, saturation, value);
        std::ptr::write_unaligned(out, c.r);
        std::ptr::write_unaligned(out.add(1), c.g);
        std::ptr::write_unaligned(out.add(2), c.b);
        0
    })
}

/// Cross-fades `zones` RGB triples in `current` toward `target` by `t` (0..=1).
#[no_mangle]
pub unsafe extern "C" fn predator_rgb_blend(
    current: *mut u8,
    target: *const u8,
    zones: usize,
    t: f32,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(current) || !is_valid_buffer(target) || zones == 0 {
            return -1;
        }
        let count = zones.min(rgb::MAX_ZONES);
        let mut tgt_copy = [Rgb8::default(); rgb::MAX_ZONES];
        let tgt_slice = std::slice::from_raw_parts(target as *const Rgb8, count);
        tgt_copy[..count].copy_from_slice(tgt_slice);

        let cur = std::slice::from_raw_parts_mut(current as *mut Rgb8, count);
        rgb::blend_zones(cur, &tgt_copy[..count], t);
        0
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_rgb_apply_brightness(
    zones: *mut u8,
    count: usize,
    brightness: u8,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(zones) || count == 0 {
            return -1;
        }
        let slice = std::slice::from_raw_parts_mut(zones as *mut Rgb8, count.min(rgb::MAX_ZONES));
        rgb::apply_brightness(slice, brightness);
        0
    })
}

// ---------------------------------------------------------------------------
// Audio spectrum analyzer (AudioMeter.cs)
// ---------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn predator_audio_create(
    sample_rate: u32,
    fft_size: usize,
    band_count: usize,
) -> *mut SpectrumAnalyzer {
    safe_ffi(std::ptr::null_mut(), || {
        match SpectrumAnalyzer::new(sample_rate, fft_size, band_count) {
            Some(analyzer) => Box::into_raw(Box::new(analyzer)),
            None => std::ptr::null_mut(),
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_destroy(handle: *mut SpectrumAnalyzer) {
    safe_ffi((), || {
        if is_valid_mut_ptr(handle) {
            let _ = Box::from_raw(handle);
        }
    });
}

/// Pushes `count` f32 samples (interleaved across `channels`).
#[no_mangle]
pub unsafe extern "C" fn predator_audio_push(
    handle: *mut SpectrumAnalyzer,
    samples: *const f32,
    count: usize,
    channels: usize,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_ptr(handle) || !is_valid_buffer(samples) || count == 0 {
            return -1;
        }
        let max_samples = count.min(audio::MAX_PUSH_FRAMES * 8);
        if (samples as usize) % std::mem::align_of::<f32>() == 0 {
            let data = std::slice::from_raw_parts(samples, max_samples);
            (*handle).push_interleaved(data, channels.max(1));
        } else {
            let mut aligned = Vec::with_capacity(max_samples);
            for i in 0..max_samples {
                aligned.push(std::ptr::read_unaligned(samples.add(i)));
            }
            (*handle).push_interleaved(&aligned, channels.max(1));
        }
        0
    })
}

/// Writes up to `cap` band energies (0..=1) and returns how many were written.
#[no_mangle]
pub unsafe extern "C" fn predator_audio_get_bands(
    handle: *mut SpectrumAnalyzer,
    out: *mut f32,
    cap: usize,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_ptr(handle) || !is_valid_mut_buffer(out) || cap == 0 {
            return -1;
        }
        let analyzer = &*handle;
        analyzer.copy_bands_unaligned(out, cap) as i32
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_get_level(handle: *mut SpectrumAnalyzer) -> f32 {
    safe_ffi(0.0, || {
        if !is_valid_mut_ptr(handle) {
            return 0.0;
        }
        (*handle).level()
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_is_beat(handle: *mut SpectrumAnalyzer) -> i32 {
    safe_ffi(0, || {
        if !is_valid_mut_ptr(handle) {
            return 0;
        }
        if (*handle).is_beat() {
            1
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_beat_strength(handle: *mut SpectrumAnalyzer) -> f32 {
    safe_ffi(0.0, || {
        if !is_valid_mut_ptr(handle) {
            return 0.0;
        }
        (*handle).beat_strength()
    })
}

/// Maps spectrum energy onto `zones` RGB triples driven by `base` color.
#[no_mangle]
pub unsafe extern "C" fn predator_audio_render_zones(
    handle: *mut SpectrumAnalyzer,
    r: u8,
    g: u8,
    b: u8,
    brightness: u8,
    out: *mut u8,
    zones: usize,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_ptr(handle) || !is_valid_mut_buffer(out) || zones == 0 {
            return -1;
        }
        let slice = std::slice::from_raw_parts_mut(out, zones.min(rgb::MAX_ZONES) * 3);
        (*handle).render_zones([r, g, b], brightness, slice) as i32
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_reset(handle: *mut SpectrumAnalyzer) {
    safe_ffi((), || {
        if is_valid_mut_ptr(handle) {
            (*handle).reset();
        }
    });
}

// ---------------------------------------------------------------------------
// NVAPI bridge (PdhLoadMonitor.cs GPU telemetry)
// ---------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn predator_nvapi_is_available() -> i32 {
    safe_ffi(0, || if nvapi::is_available() { 1 } else { 0 })
}

#[no_mangle]
pub unsafe extern "C" fn predator_nvapi_get_gpu_load(out_load: *mut f64) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_load) {
            return -1;
        }
        match nvapi::gpu_load() {
            Some(load) => {
                std::ptr::write_unaligned(out_load, load);
                0
            }
            None => -2,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_nvapi_get_gpu_temperature(out_temp: *mut f64) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_temp) {
            return -1;
        }
        match nvapi::gpu_temperature() {
            Some(temp) => {
                std::ptr::write_unaligned(out_temp, temp);
                0
            }
            None => -2,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_nvapi_get_gpu_count() -> u32 {
    safe_ffi(0, nvapi::gpu_count)
}

#[no_mangle]
pub unsafe extern "C" fn predator_nvapi_shutdown() {
    safe_ffi((), nvapi::shutdown);
}

// ---------------------------------------------------------------------------
// ACPI / AML parser (BiosUefiPowerScanner.cs)
// ---------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn predator_acpi_ppcc_entries(
    aml: *const u8,
    len: usize,
    out: *mut PpccEntry,
    cap: usize,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_buffer(aml) || len == 0 {
            return -1;
        }
        let slice = std::slice::from_raw_parts(aml, len);
        let entries = acpi::extract_ppcc_entries(slice);
        if !is_valid_mut_buffer(out) || cap == 0 {
            return entries.len() as i32;
        }
        let count = entries.len().min(cap);
        for (i, entry) in entries[..count].iter().enumerate() {
            std::ptr::write_unaligned(out.add(i), *entry);
        }
        count as i32
    })
}

/// Returns 1 and fills `out_summary` when a PPCC block was found, 0 when absent.
#[no_mangle]
pub unsafe extern "C" fn predator_acpi_ppcc_summary(
    aml: *const u8,
    len: usize,
    out_summary: *mut PpccSummary,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_buffer(aml) || len == 0 || !is_valid_mut_buffer(out_summary) {
            return -1;
        }
        let slice = std::slice::from_raw_parts(aml, len);
        match acpi::parse_ppcc_summary(slice) {
            Some(summary) => {
                std::ptr::write_unaligned(out_summary, summary);
                1
            }
            None => 0,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_acpi_scan_tables(
    blob: *const u8,
    len: usize,
    out: *mut AcpiTable,
    cap: usize,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_buffer(blob) || len == 0 {
            return -1;
        }
        let slice = std::slice::from_raw_parts(blob, len);
        let tables = acpi::scan_acpi_tables(slice);
        if !is_valid_mut_buffer(out) || cap == 0 {
            return tables.len() as i32;
        }
        let count = tables.len().min(cap);
        for (i, table) in tables[..count].iter().enumerate() {
            std::ptr::write_unaligned(out.add(i), *table);
        }
        count as i32
    })
}

// ---------------------------------------------------------------------------
// EC-HID transport (AcerHardwareProtocols.cs)
// ---------------------------------------------------------------------------

fn with_echid<F, R>(_default: R, f: F) -> R
where
    F: FnOnce(&mut Option<EcHid>) -> R,
{
    let mut guard = GLOBAL_ECHID.lock().unwrap_or_else(|e| e.into_inner());
    f(&mut guard)
}

#[no_mangle]
pub unsafe extern "C" fn predator_echid_open(
    vendor_id: u16,
    product_id: u16,
    usage_page: u16,
    usage: u16,
) -> i32 {
    safe_ffi(0, || {
        with_echid(0, |slot| {
            if let Some(existing) = slot.as_ref() {
                if existing.is_open() {
                    return 1;
                }
            }
            *slot = EcHid::open(vendor_id, product_id, usage_page, usage);
            if slot.is_some() {
                1
            } else {
                0
            }
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_echid_close() {
    safe_ffi((), || {
        with_echid((), |slot| {
            slot.take();
        })
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_echid_is_open() -> i32 {
    safe_ffi(0, || {
        with_echid(0, |slot| match slot.as_ref() {
            Some(dev) if dev.is_open() => 1,
            _ => 0,
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_echid_report_length() -> i32 {
    safe_ffi(0, || {
        with_echid(0, |slot| match slot.as_ref() {
            Some(dev) => dev.report_length() as i32,
            None => 0,
        })
    })
}

/// Sends a feature report and copies the reply report into `out`.
/// Returns the number of reply bytes written, or a negative error code.
#[no_mangle]
pub unsafe extern "C" fn predator_echid_exchange(
    request: *const u8,
    request_len: usize,
    out: *mut u8,
    out_cap: usize,
) -> i32 {
    safe_ffi(-1, || {
        if !is_valid_buffer(request)
            || request_len == 0
            || !is_valid_mut_buffer(out)
            || out_cap == 0
        {
            return -1;
        }
        let req = std::slice::from_raw_parts(request, request_len);
        let out_slice = std::slice::from_raw_parts_mut(out, out_cap);
        with_echid(-2, |slot| match slot.as_ref() {
            Some(dev) => match dev.exchange(req, out_slice) {
                Some(n) => n as i32,
                None => -3,
            },
            None => -2,
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_echid_enumerate(limit: usize) -> i32 {
    safe_ffi(0, || ec_hid::enumerate_hid_paths(limit) as i32)
}

// ============================================================================
// Phase 2: 7 Migrated Native Core Subsystems (PawnIO, Enek, AntiThrottle, Audio, ACPI, UEFI, Catalog)
// ============================================================================

// 1. PawnIO Ring-0 Driver Management
#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_open_driver() -> i32 {
    safe_ffi(-1, || {
        let mut guard = GLOBAL_PAWNIO.lock().unwrap_or_else(|e| e.into_inner());
        match PawnIoDriver::open() {
            Ok(driver) => {
                *guard = Some(driver);
                0
            }
            Err(e) => e,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_close_driver() {
    safe_ffi((), || {
        let mut guard = GLOBAL_PAWNIO.lock().unwrap_or_else(|e| e.into_inner());
        *guard = None;
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_is_driver_open() -> i32 {
    safe_ffi(0, || {
        let guard = GLOBAL_PAWNIO.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(ref d) = *guard {
            if d.is_available() {
                1
            } else {
                0
            }
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_apply_mode_limits_direct(
    target_pl1: u32,
    target_pl2: u32,
    out_mirrored: *mut i32,
) -> i32 {
    safe_ffi(-1, || {
        let mut guard = GLOBAL_PAWNIO.lock().unwrap_or_else(|e| e.into_inner());
        if guard.is_none() {
            if let Ok(driver) = PawnIoDriver::open() {
                *guard = Some(driver);
            } else {
                return -1;
            }
        }
        if let Some(ref mut driver) = *guard {
            let mut mirrored_val = 0i32;
            match driver.apply_mode_limits_direct(target_pl1, target_pl2, Some(&mut mirrored_val)) {
                Ok(_) => {
                    if !out_mirrored.is_null() && is_valid_mut_buffer(out_mirrored) {
                        std::ptr::write_unaligned(out_mirrored, mirrored_val);
                    }
                    0
                }
                Err(err) => err,
            }
        } else {
            -1
        }
    })
}

// 2. ENEK5130 RGB Device Transport
pub use ec_hid::{
    predator_enek_close, predator_enek_is_open, predator_enek_open, predator_enek_send_report,
    predator_enek_set_zones,
};

// 3. AntiThrottle Safety Evaluation
pub use thermal::predator_antithrottle_evaluate;

// 4. WASAPI Audio Loopback
pub use audio::{
    predator_audio_capture_get_bands, predator_audio_capture_get_level,
    predator_audio_capture_get_zones, predator_audio_capture_is_active,
    predator_audio_capture_start, predator_audio_capture_stop,
};

// 5. Direct ACPI / SMBIOS / UEFI Table Acquisition
pub use acpi::predator_acpi_fetch_and_parse_ppcc;
pub use smbios::predator_smbios_fetch_and_parse;
pub use uefi::predator_uefi_fetch_and_scan_power;

// 7. Native Model Catalog Lookup
pub use model_catalog::predator_model_catalog_lookup;

// 8. Event-Driven Game Profiles Native Engine
pub use migration_exports::{
    predator_game_profile_clear, predator_game_profile_get_active, predator_game_profile_init,
    predator_game_profile_poll_event, predator_game_profile_register,
    predator_game_profile_shutdown, predator_game_profile_unregister,
};

// Subsystem aliases and lifecycle controls for C-ABI compatibility
#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_open() -> i32 {
    predator_pawnio_open_driver()
}

#[no_mangle]
pub unsafe extern "C" fn predator_pawnio_is_service_running() -> i32 {
    predator_pawnio_is_driver_open()
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_capture_read(
    out_bands: *mut f32,
    band_count: usize,
) -> i32 {
    predator_audio_capture_get_bands(out_bands, band_count)
}

#[no_mangle]
pub unsafe extern "C" fn predator_telemetry_start() -> i32 {
    safe_ffi(-1, || {
        let mut guard = GLOBAL_TELEMETRY.lock().unwrap_or_else(|e| e.into_inner());
        if guard.is_none() {
            let mut worker = TelemetryWorker::new();
            worker.start();
            *guard = Some(worker);
        }
        0
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_telemetry_stop() {
    safe_ffi((), || {
        let mut guard = GLOBAL_TELEMETRY.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(mut worker) = guard.take() {
            worker.stop();
        }
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_telemetry_is_active() -> i32 {
    safe_ffi(0, || {
        let guard = GLOBAL_TELEMETRY.lock().unwrap_or_else(|e| e.into_inner());
        if guard.is_some() {
            1
        } else {
            0
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex as StdMutex;
    static TEST_MUTEX: StdMutex<()> = StdMutex::new(());

    #[test]
    fn test_curve_interpolation_and_deadband() {
        let mut follower = FanCurveFollower::new(2);
        let pts = [
            ControlPoint {
                temp: 40,
                speed: 20,
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
                temp: 95,
                speed: 100,
            },
        ];

        // Below lowest point clamped to lowest speed
        assert_eq!(FanCurveFollower::interpolate(&pts, 30), 20);
        // Above highest point clamped to highest speed
        assert_eq!(FanCurveFollower::interpolate(&pts, 100), 100);
        // Exact points
        assert_eq!(FanCurveFollower::interpolate(&pts, 40), 20);
        assert_eq!(FanCurveFollower::interpolate(&pts, 60), 50);
        // Midpoint linear interpolation (40->60 temp is 20-50 speed: 50 temp is 35 speed)
        assert_eq!(FanCurveFollower::interpolate(&pts, 50), 35);

        // Test update with safety floor
        let speed = follower.update(50, &pts, 60);
        assert_eq!(speed, 60); // Safety floor overrides 35
    }

    #[test]
    fn test_thermal_coordinator_stages_and_caps() {
        let profile = ThermalSafetyProfile {
            enabled: true,
            cpu_warn: 75.0,
            cpu_ceiling: 95.0,
            cpu_critical: 100.0,
            gpu_warn: 70.0,
            gpu_ceiling: 87.0,
            gpu_critical: 90.0,
            hysteresis: 3.0,
            max_fan_floor: 100,
            cpu_power_cap_watts: 45,
            min_power_cap_watts: 15,
            critical_fans_full_speed: true,
            ema_alpha: 0.35,
        };

        let mut coord = ThermalCoordinator::new(profile);

        // Normal temperature
        let d_normal = coord.evaluate(50.0, 50.0, 65, 15, 45);
        assert_eq!(d_normal.stage, 0);
        assert_eq!(d_normal.active, false);

        // Warm temperature sustained over several ticks (EMA convergence)
        let mut d_warm = d_normal;
        for _ in 0..10 {
            d_warm = coord.evaluate(85.0, 50.0, 65, 15, 45);
        }
        assert!(d_warm.stage >= 1);
        assert_eq!(d_warm.active, true);

        // Critical temperature sustained over several ticks
        let mut d_crit = d_warm;
        for _ in 0..10 {
            d_crit = coord.evaluate(105.0, 50.0, 65, 15, 45);
        }
        assert_eq!(d_crit.stage, 3);
        assert_eq!(d_crit.critical, true);
        assert_eq!(d_crit.cpu_fan_floor, 100);
        assert_eq!(d_crit.cpu_power_cap_watts, 15); // clamped to floor
    }

    /// Regression test: thermal FFI used to be a process-wide singleton, so a
    /// second coordinator inherited the first one's latches, filters, and floor.
    #[test]
    fn test_thermal_handles_are_independent() {
        let _test_guard = TEST_MUTEX.lock().unwrap_or_else(|e| e.into_inner());
        unsafe {
            let hot = predator_thermal_create();
            let cold = predator_thermal_create();
            assert!(!hot.is_null() && !cold.is_null());
            assert_ne!(hot, cold);

            let mut dec = NativeThermalDecision::default();
            assert_eq!(
                predator_thermal_evaluate(hot, 105.0, 95.0, 65, 15, 45, &mut dec),
                0
            );
            assert_eq!(dec.stage, 3);
            assert_eq!(dec.cpu_fan_floor, 100);

            // The untouched handle must still report a normal stage and no floor.
            assert_eq!(
                predator_thermal_evaluate(cold, 45.0, 40.0, 65, 15, 45, &mut dec),
                0
            );
            assert_eq!(dec.stage, 0);
            assert_eq!(dec.cpu_fan_floor, 0);
            assert!(!dec.active);

            // Resetting the hot handle must not disturb the cold one.
            assert_eq!(predator_thermal_reset(hot), 0);
            assert_eq!(
                predator_thermal_evaluate(hot, 45.0, 40.0, 65, 15, 45, &mut dec),
                0
            );
            assert_eq!(dec.stage, 0);

            predator_thermal_destroy(hot);
            predator_thermal_destroy(cold);
        }
    }

    #[test]
    fn test_pawnio_directional_split_write() {
        let mut driver = PawnIoDriver::new();
        assert!(driver.is_available());

        // Test reading mock MSR
        let units = driver.read_msr(pawnio::MSR_RAPL_POWER_UNIT).unwrap();
        assert_eq!(units, 0x000A0E03);

        // Test directional split write
        let res = driver.write_rapl_limits_directional(45, 90);
        assert!(res.is_ok());

        // Check new MSR 0x610 value
        let limit = driver.read_msr(pawnio::MSR_PKG_POWER_LIMIT).unwrap();
        let pl1_raw = limit & 0x7FFF;
        assert_eq!(pl1_raw, 45 * 8); // 45W * 8 units/W
    }

    #[test]
    fn test_c_abi_curve_exports() {
        let _test_guard = TEST_MUTEX.lock().unwrap_or_else(|e| e.into_inner());
        let handle = unsafe { predator_curve_create(2) };
        assert!(!handle.is_null());

        let pts = [
            ControlPoint {
                temp: 40,
                speed: 20,
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
                temp: 95,
                speed: 100,
            },
        ];

        // Test predator_curve_interpolate
        assert_eq!(
            unsafe { predator_curve_interpolate(std::ptr::null(), 0, 50) },
            50
        );
        assert_eq!(
            unsafe { predator_curve_interpolate(pts.as_ptr(), pts.len(), 30) },
            20
        );
        assert_eq!(
            unsafe { predator_curve_interpolate(pts.as_ptr(), pts.len(), 50) },
            35
        );
        assert_eq!(
            unsafe { predator_curve_interpolate(pts.as_ptr(), pts.len(), 100) },
            100
        );

        // Test predator_curve_update with f64 temp
        assert_eq!(
            unsafe {
                predator_curve_update(std::ptr::null_mut(), 50.0, pts.as_ptr(), pts.len(), 0)
            },
            50
        );
        assert_eq!(
            unsafe { predator_curve_update(handle, 50.0, std::ptr::null(), 0, 0) },
            50
        );

        let speed = unsafe { predator_curve_update(handle, 50.0, pts.as_ptr(), pts.len(), 0) };
        assert_eq!(speed, 35);

        let speed_floor =
            unsafe { predator_curve_update(handle, 50.0, pts.as_ptr(), pts.len(), 60) };
        assert_eq!(speed_floor, 60);

        unsafe {
            predator_curve_reset(handle);
            predator_curve_destroy(handle);
        }
    }

    #[test]
    fn test_ffi_null_pointers_robustness() {
        let _test_guard = TEST_MUTEX.lock().unwrap_or_else(|e| e.into_inner());
        unsafe {
            assert_eq!(predator_pawnio_read_msr(0x606, std::ptr::null_mut()), -1);
            assert_eq!(
                predator_pawnio_read_mmio32(0xFED159A0, std::ptr::null_mut()),
                -1
            );
            assert_eq!(predator_telemetry_get_snapshot(std::ptr::null_mut()), -1);
            assert_eq!(predator_curve_interpolate(std::ptr::null(), 0, 50), 50);
            assert_eq!(
                predator_curve_update(std::ptr::null_mut(), 50.0, std::ptr::null(), 0, 0),
                50
            );
            assert_eq!(
                predator_thermal_evaluate(
                    std::ptr::null_mut(),
                    50.0,
                    50.0,
                    45,
                    15,
                    45,
                    std::ptr::null_mut()
                ),
                -1
            );
            assert_eq!(
                predator_thermal_set_profile(
                    std::ptr::null_mut(),
                    1,
                    75.0,
                    95.0,
                    100.0,
                    70.0,
                    87.0,
                    90.0,
                    3.0,
                    100,
                    45,
                    15,
                    1,
                    0.35
                ),
                -1
            );
            assert_eq!(predator_thermal_reset(std::ptr::null_mut()), -1);
            predator_thermal_destroy(std::ptr::null_mut());
            predator_curve_reset(std::ptr::null_mut());
            predator_curve_destroy(std::ptr::null_mut());
        }
    }

    #[test]
    fn test_ffi_unaligned_pointers() {
        let _test_guard = TEST_MUTEX.lock().unwrap_or_else(|e| e.into_inner());
        unsafe {
            // Test unaligned out_val for read_msr
            let mut buf = [0u8; 16];
            let unaligned_u64 = buf.as_mut_ptr().add(1) as *mut u64;
            let res = predator_pawnio_read_msr(pawnio::MSR_RAPL_POWER_UNIT, unaligned_u64);
            assert_eq!(res, 0);
            let val = std::ptr::read_unaligned(unaligned_u64);
            assert_eq!(val, 0x000A0E03);

            // Test unaligned out_val for read_mmio32
            let unaligned_u32 = buf.as_mut_ptr().add(3) as *mut u32;
            let res_mmio =
                predator_pawnio_read_mmio32(pawnio::MMIO_PKG_RAPL_LIMIT_ADDRESS, unaligned_u32);
            assert_eq!(res_mmio, 0);

            // Test unaligned points buffer for curve interpolation
            let pts = [
                ControlPoint {
                    temp: 40,
                    speed: 20,
                },
                ControlPoint {
                    temp: 80,
                    speed: 80,
                },
            ];
            let mut byte_buf = [0u8; 32];
            let unaligned_pts = byte_buf.as_mut_ptr().add(1) as *mut ControlPoint;
            std::ptr::copy_nonoverlapping(
                pts.as_ptr() as *const u8,
                unaligned_pts as *mut u8,
                std::mem::size_of_val(&pts),
            );

            let interp = predator_curve_interpolate(unaligned_pts, 2, 60);
            assert_eq!(interp, 50);

            let handle = predator_curve_create(2);
            let update_res = predator_curve_update(handle, 60.0, unaligned_pts, 2, 0);
            assert_eq!(update_res, 50);
            predator_curve_destroy(handle);

            // Test unaligned thermal decision out pointer
            let thermal = predator_thermal_create();
            let mut dec_buf = [0u8; 64];
            let unaligned_dec = dec_buf.as_mut_ptr().add(1) as *mut NativeThermalDecision;
            let th_res = predator_thermal_evaluate(thermal, 50.0, 50.0, 45, 15, 45, unaligned_dec);
            assert_eq!(th_res, 0);
            let dec = std::ptr::read_unaligned(unaligned_dec);
            assert_eq!(dec.stage, 0);
            predator_thermal_destroy(thermal);
        }
    }

    #[test]
    fn test_numerical_robustness_extremes_and_invalids() {
        let _test_guard = TEST_MUTEX.lock().unwrap_or_else(|e| e.into_inner());
        let pts = [
            ControlPoint {
                temp: 40,
                speed: 20,
            },
            ControlPoint {
                temp: 60,
                speed: 50,
            },
            ControlPoint {
                temp: 80,
                speed: 80,
            },
        ];

        let handle = unsafe { predator_curve_create(2) };

        // 1. Extreme negative temperature (-1000°C) -> treated as invalid sensor reading, holds safe speed
        let s_neg = unsafe { predator_curve_update(handle, -1000.0, pts.as_ptr(), pts.len(), 0) };
        assert!(s_neg >= 10 && s_neg <= 100);

        // 2. Extreme high temperature (2000°C) -> clamped to max curve speed (80)
        let s_high = unsafe { predator_curve_update(handle, 2000.0, pts.as_ptr(), pts.len(), 0) };
        assert_eq!(s_high, 80);

        // 3. NaN temperature -> invalid, maintains safe speed
        let s_nan = unsafe { predator_curve_update(handle, f64::NAN, pts.as_ptr(), pts.len(), 0) };
        assert!(s_nan >= 10 && s_nan <= 100);

        // 4. +Infinity / -Infinity
        let s_inf =
            unsafe { predator_curve_update(handle, f64::INFINITY, pts.as_ptr(), pts.len(), 0) };
        assert!(s_inf >= 10 && s_inf <= 100);
        let s_ninf =
            unsafe { predator_curve_update(handle, f64::NEG_INFINITY, pts.as_ptr(), pts.len(), 0) };
        assert!(s_ninf >= 10 && s_ninf <= 100);

        // 5. Thermal evaluate at critical temperature (105°C) -> triggers critical stage 3
        let thermal = unsafe { predator_thermal_create() };
        let mut decision = NativeThermalDecision::default();
        let eval_res =
            unsafe { predator_thermal_evaluate(thermal, 105.0, 50.0, 65, 15, 45, &mut decision) };
        assert_eq!(eval_res, 0);
        assert_eq!(decision.stage, 3);
        assert!(decision.critical);
        assert_eq!(decision.cpu_fan_floor, 100);
        assert_eq!(decision.cpu_power_cap_watts, 15);
        unsafe {
            predator_thermal_destroy(thermal);
        }

        unsafe {
            predator_curve_destroy(handle);
        }
    }

    #[test]
    fn test_duplicate_and_inverted_curve_points() {
        let _test_guard = TEST_MUTEX.lock().unwrap_or_else(|e| e.into_inner());
        // Inverted points (descending order)
        let inv_pts = [
            ControlPoint {
                temp: 90,
                speed: 90,
            },
            ControlPoint {
                temp: 70,
                speed: 70,
            },
            ControlPoint {
                temp: 50,
                speed: 50,
            },
            ControlPoint {
                temp: 30,
                speed: 30,
            },
        ];
        assert_eq!(
            unsafe { predator_curve_interpolate(inv_pts.as_ptr(), inv_pts.len(), 20) },
            30
        );
        assert_eq!(
            unsafe { predator_curve_interpolate(inv_pts.as_ptr(), inv_pts.len(), 60) },
            60
        );
        assert_eq!(
            unsafe { predator_curve_interpolate(inv_pts.as_ptr(), inv_pts.len(), 100) },
            90
        );

        // Duplicate temperatures (step curve)
        let dup_pts = [
            ControlPoint {
                temp: 40,
                speed: 20,
            },
            ControlPoint {
                temp: 60,
                speed: 30,
            },
            ControlPoint {
                temp: 60,
                speed: 70,
            },
            ControlPoint {
                temp: 80,
                speed: 100,
            },
        ];
        // At 59°C: interpolating 40->60 (20->30)
        assert!(unsafe { predator_curve_interpolate(dup_pts.as_ptr(), dup_pts.len(), 59) } <= 30);
        // At 60°C: step jumps up to 70 for thermal safety!
        assert_eq!(
            unsafe { predator_curve_interpolate(dup_pts.as_ptr(), dup_pts.len(), 60) },
            70
        );
        // At 70°C: interpolating 60->80 (70->100) -> 85
        assert_eq!(
            unsafe { predator_curve_interpolate(dup_pts.as_ptr(), dup_pts.len(), 70) },
            85
        );
    }

    #[test]
    fn test_telemetry_timestamp_is_epoch_ms() {
        let _test_guard = TEST_MUTEX.lock().unwrap_or_else(|e| e.into_inner());
        let mut worker = TelemetryWorker::new();
        worker.start();
        std::thread::sleep(std::time::Duration::from_millis(50));
        let snap = worker.get_snapshot();
        worker.stop();

        // System epoch ms in 2026 is > 1_700_000_000_000 ms
        let now_ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis() as u64;
        assert!(
            snap.timestamp_ms > 1_700_000_000_000,
            "Timestamp must be real epoch ms, got {}",
            snap.timestamp_ms
        );
        assert!(snap.timestamp_ms <= now_ms);
    }
}

// 9. Universal BIOS and Firmware Settings Native Subsystem
pub use migration_exports::{
    predator_bios_apply_setting, predator_bios_get_setting_info, predator_bios_get_settings_count,
    predator_bios_probe_support, predator_bios_restore_defaults,
};

// 10. Display and Advanced Optimus (DDS) Native Engine
pub use migration_exports::{
    predator_display_get_current_refresh_rate, predator_display_get_dds_status,
    predator_display_get_supported_refresh_rates, predator_display_set_bluelight_shield,
    predator_display_set_dds_mode, predator_display_set_lcd_overdrive,
    predator_display_set_refresh_rate,
};
