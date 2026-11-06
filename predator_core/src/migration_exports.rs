//! C-ABI exports for the migrated subsystems.
//!
//! Each function is a thin wrapper over a pure Rust module and is wrapped in
//! [`crate::safe_ffi`] so that no panic can unwind across the FFI boundary.
//! Out-parameters are written with `write_unaligned` because managed callers
//! may hand over buffers that are not naturally aligned.

use crate::{
    backlight, battery, bios, cpu_spec, curve, game_profile, graphproj, history, hook,
    is_valid_buffer, is_valid_mut_buffer, pipeguard, procsync, shortcuts, uefi,
};

// ============================================================================
// Shortcut validation (ModeShortcutValidator)
// ============================================================================

/// Validates a key/modifier combination. Returns a `shortcuts::E_*` rule id,
/// or 0 when the combination is allowed.
#[no_mangle]
pub unsafe extern "C" fn predator_shortcut_validate(
    key: u32,
    ctrl: i32,
    alt: i32,
    shift: i32,
    win: i32,
) -> u32 {
    crate::safe_ffi(shortcuts::E_NO_PRIMARY_KEY, || {
        shortcuts::validate(key, ctrl != 0, alt != 0, shift != 0, win != 0)
    })
}

/// Returns 1 when both combinations use the same key and modifiers.
#[no_mangle]
pub unsafe extern "C" fn predator_shortcut_same_combination(
    key_a: u32,
    ctrl_a: i32,
    alt_a: i32,
    shift_a: i32,
    win_a: i32,
    key_b: u32,
    ctrl_b: i32,
    alt_b: i32,
    shift_b: i32,
    win_b: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if shortcuts::same_combination(
            key_a,
            ctrl_a != 0,
            alt_a != 0,
            shift_a != 0,
            win_a != 0,
            key_b,
            ctrl_b != 0,
            alt_b != 0,
            shift_b != 0,
            win_b != 0,
        ) {
            1
        } else {
            0
        }
    })
}

/// Returns 1 when `vk` is a standalone modifier key.
#[no_mangle]
pub unsafe extern "C" fn predator_shortcut_is_modifier_key(vk: u32) -> i32 {
    crate::safe_ffi(0, || if shortcuts::is_modifier_key(vk) { 1 } else { 0 })
}

/// Returns 1 when `vk` is a reserved Ctrl editing/application chord.
#[no_mangle]
pub unsafe extern "C" fn predator_shortcut_is_reserved_ctrl_key(vk: u32) -> i32 {
    crate::safe_ffi(0, || {
        if shortcuts::is_reserved_ctrl_key(vk) {
            1
        } else {
            0
        }
    })
}

// ============================================================================
// Battery calibration ETA (BatteryCalibrationEtaCalculator)
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_battery_eta_reset() {
    crate::safe_ffi((), || battery::with_eta((), |calc| calc.reset()));
}

/// Sets the smoothing factor, returning the value actually applied.
#[no_mangle]
pub unsafe extern "C" fn predator_battery_eta_set_alpha(alpha: f64) -> f64 {
    crate::safe_ffi(battery::DEFAULT_ALPHA, || {
        battery::with_eta(battery::DEFAULT_ALPHA, |calc| {
            calc.set_alpha(alpha);
            calc.alpha()
        })
    })
}

/// Writes the smoothed direct rate. Returns 1 when a rate is known.
#[no_mangle]
pub unsafe extern "C" fn predator_battery_eta_smoothed_rate(out_rate: *mut f64) -> i32 {
    crate::safe_ffi(0, || {
        if !is_valid_mut_buffer(out_rate) {
            return 0;
        }
        battery::with_eta(0, |calc| match calc.smoothed_rate() {
            Some(rate) => {
                std::ptr::write_unaligned(out_rate, rate);
                1
            }
            None => 0,
        })
    })
}

/// Writes the smoothed percentage-per-minute rate. Returns 1 when known.
#[no_mangle]
pub unsafe extern "C" fn predator_battery_eta_smoothed_percent_rate(out_rate: *mut f64) -> i32 {
    crate::safe_ffi(0, || {
        if !is_valid_mut_buffer(out_rate) {
            return 0;
        }
        battery::with_eta(0, |calc| match calc.smoothed_percent_per_minute() {
            Some(rate) => {
                std::ptr::write_unaligned(out_rate, rate);
                1
            }
            None => 0,
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_battery_eta_update_capacity(capacity: i32, ts_ms: i64) {
    crate::safe_ffi((), || {
        battery::with_eta((), |calc| calc.update_capacity(capacity, ts_ms))
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_battery_eta_update_percent(percent: i32, ts_ms: i64) {
    crate::safe_ffi((), || {
        battery::with_eta((), |calc| calc.update_percentage(percent, ts_ms))
    });
}

/// Computes the ETA. Returns `RESULT_ESTIMATE` (1) or `RESULT_WARMUP` (2) and
/// writes seconds to `out_seconds`, or `RESULT_NONE` (0).
#[no_mangle]
pub unsafe extern "C" fn predator_battery_eta_calculate(
    phase: i32,
    remaining_capacity: i32,
    full_capacity: i32,
    direct_rate_mw: f64,
    has_direct_rate: i32,
    allow_warmup: i32,
    battery_percent: i32,
    has_percent: i32,
    out_seconds: *mut f64,
) -> i32 {
    crate::safe_ffi(battery::RESULT_NONE, || {
        let rate = if has_direct_rate != 0 {
            Some(direct_rate_mw)
        } else {
            None
        };
        let percent = if has_percent != 0 {
            Some(battery_percent)
        } else {
            None
        };
        battery::with_eta(battery::RESULT_NONE, |calc| {
            if !is_valid_mut_buffer(out_seconds) {
                return battery::RESULT_NONE;
            }
            let (code, seconds) = calc.calculate(
                phase,
                remaining_capacity,
                full_capacity,
                rate,
                allow_warmup != 0,
                percent,
            );
            if code != battery::RESULT_NONE {
                std::ptr::write_unaligned(out_seconds, seconds);
            }
            code
        })
    })
}

/// Phase-1 warm-up estimate. Returns 1 and writes seconds when available.
#[no_mangle]
pub unsafe extern "C" fn predator_battery_eta_warmup(
    phase: i32,
    battery_percent: i32,
    out_seconds: *mut f64,
) -> i32 {
    crate::safe_ffi(0, || {
        let (some, seconds) = battery::EtaCalculator::warmup_estimate(phase, battery_percent);
        if some && is_valid_mut_buffer(out_seconds) {
            std::ptr::write_unaligned(out_seconds, seconds);
            1
        } else {
            0
        }
    })
}

// ============================================================================
// Telemetry history ring buffer (HistoryBuffer)
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_history_create(capacity: i32) -> *mut std::ffi::c_void {
    crate::safe_ffi(std::ptr::null_mut(), || {
        history::create(capacity) as *mut std::ffi::c_void
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_history_destroy(handle: *mut std::ffi::c_void) {
    crate::safe_ffi((), || {
        history::destroy(handle as *mut history::HistoryHandle);
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_history_add(
    handle: *mut std::ffi::c_void,
    value: f64,
    has_value: i32,
) {
    crate::safe_ffi((), || {
        if let Some(buf) = history::as_mut(handle as *mut history::HistoryHandle) {
            buf.add(if has_value != 0 { Some(value) } else { None });
        }
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_history_count(handle: *const std::ffi::c_void) -> i32 {
    crate::safe_ffi(0, || {
        history::as_ref(handle as *const history::HistoryHandle)
            .map(|b| b.count() as i32)
            .unwrap_or(0)
    })
}

/// Reads one sample. Returns 1 and writes the value, or 0 for a gap.
#[no_mangle]
pub unsafe extern "C" fn predator_history_get(
    handle: *const std::ffi::c_void,
    index: i32,
    out_value: *mut f64,
) -> i32 {
    crate::safe_ffi(0, || {
        let Some(buf) = history::as_ref(handle as *const history::HistoryHandle) else {
            return 0;
        };
        match buf.get(index) {
            Some(v) if is_valid_mut_buffer(out_value) => {
                std::ptr::write_unaligned(out_value, v);
                1
            }
            _ => 0,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_history_min(
    handle: *const std::ffi::c_void,
    out_value: *mut f64,
) -> i32 {
    crate::safe_ffi(0, || {
        let Some(buf) = history::as_ref(handle as *const history::HistoryHandle) else {
            return 0;
        };
        match buf.range() {
            Some((min, _)) if is_valid_mut_buffer(out_value) => {
                std::ptr::write_unaligned(out_value, min);
                1
            }
            _ => 0,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_history_max(
    handle: *const std::ffi::c_void,
    out_value: *mut f64,
) -> i32 {
    crate::safe_ffi(0, || {
        let Some(buf) = history::as_ref(handle as *const history::HistoryHandle) else {
            return 0;
        };
        match buf.range() {
            Some((_, max)) if is_valid_mut_buffer(out_value) => {
                std::ptr::write_unaligned(out_value, max);
                1
            }
            _ => 0,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_history_average(
    handle: *const std::ffi::c_void,
    out_value: *mut f64,
) -> i32 {
    crate::safe_ffi(0, || {
        let Some(buf) = history::as_ref(handle as *const history::HistoryHandle) else {
            return 0;
        };
        match buf.average() {
            Some(avg) if is_valid_mut_buffer(out_value) => {
                std::ptr::write_unaligned(out_value, avg);
                1
            }
            _ => 0,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_history_latest(
    handle: *const std::ffi::c_void,
    out_value: *mut f64,
) -> i32 {
    crate::safe_ffi(0, || {
        let Some(buf) = history::as_ref(handle as *const history::HistoryHandle) else {
            return 0;
        };
        match buf.latest() {
            Some(v) if is_valid_mut_buffer(out_value) => {
                std::ptr::write_unaligned(out_value, v);
                1
            }
            _ => 0,
        }
    })
}

/// Writes all samples oldest-first, using NaN for gaps. Returns the count written.
#[no_mangle]
pub unsafe extern "C" fn predator_history_snapshot(
    handle: *const std::ffi::c_void,
    out_values: *mut f64,
    out_capacity: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        let Some(buf) = history::as_ref(handle as *const history::HistoryHandle) else {
            return 0;
        };
        history::write_snapshot(buf, out_values, out_capacity)
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_history_clear(handle: *mut std::ffi::c_void) {
    crate::safe_ffi((), || {
        if let Some(buf) = history::as_mut(handle as *mut history::HistoryHandle) {
            buf.clear();
        }
    });
}

// ============================================================================
// Graph projection (HistoryGraphControl.DrawSeries)
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_graph_project_y(
    value: f64,
    min: f64,
    max: f64,
    height: f32,
) -> f32 {
    crate::safe_ffi(0.0, || graphproj::project_y(value, min, max, height))
}

#[no_mangle]
pub unsafe extern "C" fn predator_graph_project_series(
    samples: *const f64,
    count: i32,
    capacity: i32,
    width: f32,
    height: f32,
    min: f64,
    max: f64,
    out_xy: *mut f32,
) -> i32 {
    crate::safe_ffi(0, || {
        graphproj::project_series(samples, count, capacity, width, height, min, max, out_xy)
    })
}

// ============================================================================
// Keyboard hook engine (KeyboardHook)
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_hook_reset() {
    crate::safe_ffi((), || hook::with_hook((), |state| state.reset()));
}

#[no_mangle]
pub unsafe extern "C" fn predator_hook_set_allow_injected(allow: i32) {
    crate::safe_ffi((), || {
        hook::with_hook((), |state| state.set_allow_injected(allow != 0))
    });
}

/// Replaces the custom shortcut table. Returns the number of shortcuts stored.
#[no_mangle]
pub unsafe extern "C" fn predator_hook_set_shortcuts(
    entries: *const hook::ShortcutEntry,
    count: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        hook::with_hook(0, |state| {
            if count <= 0 || !crate::is_valid_buffer_range(entries, count as usize) {
                state.set_shortcuts(std::ptr::null(), 0);
                0
            } else {
                state.set_shortcuts(entries, count);
                state.shortcut_count() as i32
            }
        })
    })
}

/// Decides what a keystroke means. Returns an `ACTION_*` id and writes the
/// associated value (mode code or digit) to `out_value`.
#[no_mangle]
pub unsafe extern "C" fn predator_hook_process(
    vk: u32,
    scan: u32,
    flags: u32,
    is_down: i32,
    now_ms: i64,
    out_value: *mut u32,
) -> i32 {
    crate::safe_ffi(hook::ACTION_PASS, || {
        hook::with_hook(hook::ACTION_PASS, |state| {
            let (action, value) = state.process(vk, scan, flags, is_down != 0, now_ms);
            if is_valid_mut_buffer(out_value) {
                std::ptr::write_unaligned(out_value, value);
            }
            action
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_hook_modifier_mask() -> u32 {
    crate::safe_ffi(0, || hook::with_hook(0, |state| state.modifier_mask()))
}

/// Live hook state: bit0-3 Ctrl/Alt/Shift/Win, bit4 Predator key held.
/// Lets the managed hook mirror the native engine for observers and tests.
#[no_mangle]
pub unsafe extern "C" fn predator_hook_state() -> u32 {
    crate::safe_ffi(0, || {
        hook::with_hook(0, |state| {
            let mut mask = state.modifier_mask();
            if state.predator_pressed() {
                mask |= 0x10;
            }
            mask
        })
    })
}

// ============================================================================
// Process matching for game sync (GameSyncController)
// ============================================================================

/// Extracts the executable file name. Returns bytes written.
#[no_mangle]
pub unsafe extern "C" fn predator_procsync_executable_name(
    input: *const u8,
    input_len: i32,
    out: *mut u8,
    out_capacity: i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        let text = procsync::read_str(input, input_len);
        procsync::copy_result(&procsync::executable_name(text), out, out_capacity)
    })
}

/// Extracts the process name (no `.exe`). Returns bytes written.
#[no_mangle]
pub unsafe extern "C" fn predator_procsync_process_name(
    input: *const u8,
    input_len: i32,
    out: *mut u8,
    out_capacity: i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        let text = procsync::read_str(input, input_len);
        procsync::copy_result(&procsync::process_name(text), out, out_capacity)
    })
}

/// Returns 1 when two executable names refer to the same process.
#[no_mangle]
pub unsafe extern "C" fn predator_procsync_same_process(
    a: *const u8,
    a_len: i32,
    b: *const u8,
    b_len: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        let left = procsync::read_str(a, a_len);
        let right = procsync::read_str(b, b_len);
        if procsync::same_process(left, right) {
            1
        } else {
            0
        }
    })
}

/// Returns 1 when the running process name matches a profile executable.
#[no_mangle]
pub unsafe extern "C" fn predator_procsync_matches_running(
    profile_executable: *const u8,
    profile_len: i32,
    running: *const u8,
    running_len: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        let profile = procsync::read_str(profile_executable, profile_len);
        let live = procsync::read_str(running, running_len);
        if procsync::matches_running(profile, live) {
            1
        } else {
            0
        }
    })
}

/// Applies every profile clamp. Returns 0 on success.
#[no_mangle]
pub unsafe extern "C" fn predator_procsync_sanitize_profile(
    input: *const procsync::ProfileValues,
    out: *mut procsync::ProfileValues,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_buffer(input) || !is_valid_mut_buffer(out) {
            return -1;
        }
        let src = std::ptr::read_unaligned(input);
        let sanitized = procsync::sanitize_profile(&src);
        std::ptr::write_unaligned(out, sanitized);
        0
    })
}

// ============================================================================
// Named-pipe message framing (SecureNamedPipeIpc)
// ============================================================================

/// Sanitises a message into `out`. Returns the byte length or -1.
#[no_mangle]
pub unsafe extern "C" fn predator_pipeguard_sanitize(
    input: *const u8,
    input_len: i32,
    out: *mut u8,
    out_capacity: i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        pipeguard::sanitize_into(input, input_len, out, out_capacity)
    })
}

/// Returns 1 when a received line is a well-formed message.
#[no_mangle]
pub unsafe extern "C" fn predator_pipeguard_is_valid(input: *const u8, input_len: i32) -> i32 {
    crate::safe_ffi(0, || {
        let text = procsync::read_str(input, input_len);
        if pipeguard::is_valid(text) {
            1
        } else {
            0
        }
    })
}

// ============================================================================
// Tier 1: 8-Point Fan Curve Normalizer (FanCurveGraph.Normalize)
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_curve_normalize(
    in_points: *const curve::ControlPoint,
    in_len: usize,
    out_points: *mut curve::ControlPoint,
    out_capacity: usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        if out_capacity < 8 || !is_valid_mut_buffer(out_points) {
            return -1;
        }
        let mut temp_buf = [curve::ControlPoint::default(); 128];
        let in_slice: &[curve::ControlPoint] =
            if in_len > 0 && in_len <= 128 && crate::is_valid_buffer_range(in_points, in_len) {
                if (in_points as usize) % std::mem::align_of::<curve::ControlPoint>() == 0 {
                    std::slice::from_raw_parts(in_points, in_len)
                } else {
                    for i in 0..in_len {
                        temp_buf[i] = std::ptr::read_unaligned(in_points.add(i));
                    }
                    &temp_buf[..in_len]
                }
            } else {
                &[]
            };
        let normalized = curve::normalize_curve(in_slice);
        for i in 0..8 {
            std::ptr::write_unaligned(out_points.add(i), normalized[i]);
        }
        8
    })
}

// ============================================================================
// Tier 1: CPU Architecture & TDP Power Limit Matrix (CpuSpecDetector)
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_cpuspec_detect(
    model_ptr: *const u8,
    model_len: i32,
    out_spec: *mut cpu_spec::CpuHardwareSpec,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_spec) {
            return -1;
        }
        let model = procsync::read_str(model_ptr, model_len);
        let spec = cpu_spec::detect_cpu_spec(model);
        std::ptr::write_unaligned(out_spec, spec);
        0
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_cpuspec_estimate_proportional_limits(
    tdp_watts: i32,
    mode: u8,
    out_pl1: *mut i32,
    out_pl2: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_pl1) || !is_valid_mut_buffer(out_pl2) {
            return -1;
        }
        let (pl1, pl2) = cpu_spec::estimate_proportional_limits(tdp_watts, mode);
        std::ptr::write_unaligned(out_pl1, pl1);
        std::ptr::write_unaligned(out_pl2, pl2);
        0
    })
}

// ============================================================================
// Tier 1: Backlight State Machine Engine (BacklightStateManager)
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_create(
    initial_ac_brightness: i32,
) -> *mut backlight::BacklightStateManager {
    crate::safe_ffi(std::ptr::null_mut(), || {
        Box::into_raw(Box::new(backlight::BacklightStateManager::new(
            initial_ac_brightness,
        )))
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_destroy(handle: *mut backlight::BacklightStateManager) {
    crate::safe_ffi((), || {
        if crate::is_valid_mut_ptr(handle) {
            drop(Box::from_raw(handle));
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_evaluate_idle(
    handle: *mut backlight::BacklightStateManager,
    now_sec: i64,
    out_should_dim: *mut i32,
    out_brightness: *mut i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle)
            || !is_valid_mut_buffer(out_should_dim)
            || !is_valid_mut_buffer(out_brightness)
        {
            return 0;
        }
        let mgr = &mut *handle;
        let (handled, should_dim, brightness) = mgr.evaluate_idle(now_sec);
        std::ptr::write_unaligned(out_should_dim, if should_dim { 1 } else { 0 });
        std::ptr::write_unaligned(out_brightness, brightness);
        if handled {
            1
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_on_activity(
    handle: *mut backlight::BacklightStateManager,
    out_should_restore: *mut i32,
    out_brightness: *mut i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle)
            || !is_valid_mut_buffer(out_should_restore)
            || !is_valid_mut_buffer(out_brightness)
        {
            return 0;
        }
        let mgr = &mut *handle;
        let (_handled, should_restore, brightness) = mgr.on_activity();
        std::ptr::write_unaligned(out_should_restore, if should_restore { 1 } else { 0 });
        std::ptr::write_unaligned(out_brightness, brightness);
        if should_restore {
            1
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_is_on_battery(
    handle: *mut backlight::BacklightStateManager,
    line_status: i32,
    charge_status: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle) {
            return 0;
        }
        let mgr = &*handle;
        if mgr.is_on_battery(backlight::PowerLineStatus::from(line_status), charge_status) {
            1
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_on_lid_changed(
    handle: *mut backlight::BacklightStateManager,
    is_open: i32,
    line_status: i32,
    charge_status: i32,
    out_turn_off: *mut i32,
    out_brightness: *mut i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle)
            || !is_valid_mut_buffer(out_turn_off)
            || !is_valid_mut_buffer(out_brightness)
        {
            return 0;
        }
        let mgr = &mut *handle;
        let (should_turn_off, brightness) = mgr.on_lid_changed(
            is_open != 0,
            backlight::PowerLineStatus::from(line_status),
            charge_status,
        );
        std::ptr::write_unaligned(out_turn_off, if should_turn_off { 1 } else { 0 });
        std::ptr::write_unaligned(out_brightness, brightness);
        1
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_on_suspend(
    handle: *mut backlight::BacklightStateManager,
    out_turn_off: *mut i32,
    out_brightness: *mut i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle)
            || !is_valid_mut_buffer(out_turn_off)
            || !is_valid_mut_buffer(out_brightness)
        {
            return 0;
        }
        let mgr = &mut *handle;
        let (should_turn_off, brightness) = mgr.on_suspend();
        std::ptr::write_unaligned(out_turn_off, if should_turn_off { 1 } else { 0 });
        std::ptr::write_unaligned(out_brightness, brightness);
        1
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_on_resume(
    handle: *mut backlight::BacklightStateManager,
    line_status: i32,
    charge_status: i32,
    out_turn_off: *mut i32,
    out_brightness: *mut i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle)
            || !is_valid_mut_buffer(out_turn_off)
            || !is_valid_mut_buffer(out_brightness)
        {
            return 0;
        }
        let mgr = &mut *handle;
        let (should_turn_off, brightness) =
            mgr.on_resume(backlight::PowerLineStatus::from(line_status), charge_status);
        std::ptr::write_unaligned(out_turn_off, if should_turn_off { 1 } else { 0 });
        std::ptr::write_unaligned(out_brightness, brightness);
        1
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_set_plugged_in(
    handle: *mut backlight::BacklightStateManager,
    plugged_in: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle) {
            return 0;
        }
        let mgr = &mut *handle;
        mgr.set_is_plugged_in(match plugged_in {
            1 => Some(true),
            0 => Some(false),
            _ => None,
        });
        1
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_set_saved_ac(
    handle: *mut backlight::BacklightStateManager,
    brightness: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle) {
            return 0;
        }
        let mgr = &mut *handle;
        mgr.set_saved_ac_brightness(brightness);
        1
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_set_manual_battery(
    handle: *mut backlight::BacklightStateManager,
    enabled: i32,
    brightness: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle) {
            return 0;
        }
        let mgr = &mut *handle;
        mgr.set_manual_backlight_on_battery(enabled != 0);
        mgr.set_manual_battery_brightness(brightness);
        1
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_set_idle_config(
    handle: *mut backlight::BacklightStateManager,
    timeout_sec: i32,
    dimmed_percent: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle) {
            return 0;
        }
        let mgr = &mut *handle;
        mgr.set_idle_timeout_seconds(timeout_sec);
        mgr.set_dimmed_brightness_percent(dimmed_percent);
        1
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_backlight_record_activity(
    handle: *mut backlight::BacklightStateManager,
) -> i32 {
    crate::safe_ffi(0, || {
        if !crate::is_valid_mut_ptr(handle) {
            return 0;
        }
        let mgr = &mut *handle;
        mgr.record_activity();
        1
    })
}

// ============================================================================
// Tier 2: Native Background Process Scanner (GameSyncController)
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_procsync_is_process_running(
    name_ptr: *const u8,
    name_len: i32,
) -> i32 {
    crate::safe_ffi(0, || {
        let name = procsync::read_str(name_ptr, name_len);
        if procsync::is_process_running(name) {
            1
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_procsync_get_foreground_process_name(
    out: *mut u8,
    out_capacity: i32,
) -> i32 {
    crate::safe_ffi(-1, || match procsync::get_foreground_process_name() {
        Some(name) => procsync::copy_result(&name, out, out_capacity),
        None => -1,
    })
}

// ============================================================================
// Tier 2: Battery Descriptor Parsing (WindowsBattery in AcerHardwareProtocols)
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_battery_parse_info(
    bytes: *const u8,
    len: usize,
    out_info: *mut battery::RawBatteryInfo,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_buffer(bytes) || !is_valid_mut_buffer(out_info) || len < 36 {
            return -1;
        }
        let slice = std::slice::from_raw_parts(bytes, len);
        match battery::parse_battery_info(slice) {
            Some(info) => {
                std::ptr::write_unaligned(out_info, info);
                0
            }
            None => -1,
        }
    })
}

// ============================================================================
// Tier 2: UEFI Firmware Variable Scanner (BiosUefiPowerScanner)
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_uefi_scan_power_and_locks(
    aml: *const u8,
    aml_len: usize,
    out_cfg: *mut uefi::UefiPowerConfig,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_buffer(aml) || !is_valid_mut_buffer(out_cfg) || aml_len == 0 {
            return -1;
        }
        let slice = std::slice::from_raw_parts(aml, aml_len);
        match uefi::scan_power_and_locks(slice) {
            Some(cfg) => {
                std::ptr::write_unaligned(out_cfg, cfg);
                0
            }
            None => -1,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_uefi_parse_variable_power(
    data: *const u8,
    data_len: usize,
    out_cfg: *mut uefi::UefiPowerConfig,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_buffer(data) || !is_valid_mut_buffer(out_cfg) || data_len == 0 {
            return -1;
        }
        let slice = std::slice::from_raw_parts(data, data_len);
        match uefi::parse_uefi_variable_power(slice) {
            Some(cfg) => {
                std::ptr::write_unaligned(out_cfg, cfg);
                0
            }
            None => -1,
        }
    })
}

// ============================================================================
// Event-Driven Game Profiles Native Engine (GameProfile)
// ============================================================================

/// Initializes the native game profile engine and Win32 foreground hook.
#[no_mangle]
pub unsafe extern "C" fn predator_game_profile_init() -> i32 {
    crate::safe_ffi(-1, || game_profile::init())
}

/// Shuts down the Win32 hook, stops monitoring, and releases resources.
#[no_mangle]
pub unsafe extern "C" fn predator_game_profile_shutdown() -> i32 {
    crate::safe_ffi(-1, || game_profile::shutdown())
}

/// Registers or updates a game profile in the match table.
///
/// `exe_name` must be a null-terminated UTF-8 string.
/// `full_path` is optional; may be null or a null-terminated UTF-8 string.
#[no_mangle]
pub unsafe extern "C" fn predator_game_profile_register(
    profile_id: u32,
    exe_name: *const u8,
    full_path: *const u8,
    mode: i32,
    pl1: u32,
    pl2: u32,
    fan_mode: i32,
    refresh_rate: i32,
    dds_mode: i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if profile_id == 0 || !is_valid_buffer(exe_name) {
            return -1;
        }

        // Read null-terminated exe_name
        let mut exe_len = 0;
        let mut found_null = false;
        while exe_len < 260 {
            let b = *exe_name.add(exe_len);
            if b == 0 {
                found_null = true;
                break;
            }
            exe_len += 1;
        }
        if !found_null || exe_len == 0 {
            return -1;
        }
        let exe_slice = std::slice::from_raw_parts(exe_name, exe_len);
        let exe_str = match std::str::from_utf8(exe_slice) {
            Ok(s) => s.trim().to_string(),
            Err(_) => return -1,
        };

        // Read optional null-terminated full_path
        let path_str = if is_valid_buffer(full_path) {
            let mut path_len = 0;
            let mut found_path_null = false;
            while path_len < 1024 {
                let b = *full_path.add(path_len);
                if b == 0 {
                    found_path_null = true;
                    break;
                }
                path_len += 1;
            }
            if found_path_null && path_len > 0 {
                let path_slice = std::slice::from_raw_parts(full_path, path_len);
                std::str::from_utf8(path_slice)
                    .unwrap_or("")
                    .trim()
                    .to_string()
            } else {
                String::new()
            }
        } else {
            String::new()
        };

        let config = game_profile::GameProfileConfig {
            profile_id,
            exe_name: exe_str,
            full_path: path_str,
            mode,
            pl1,
            pl2,
            fan_mode,
            refresh_rate,
            dds_mode,
        };

        match game_profile::register_profile(config) {
            Ok(()) => 0,
            Err(_) => -2,
        }
    })
}

/// Unregisters a profile by ID from the match table.
#[no_mangle]
pub unsafe extern "C" fn predator_game_profile_unregister(profile_id: u32) -> i32 {
    crate::safe_ffi(-1, || {
        if profile_id == 0 {
            return -1;
        }
        if game_profile::unregister_profile(profile_id) {
            0
        } else {
            -1
        }
    })
}

/// Clears all game profiles from the match table.
#[no_mangle]
pub unsafe extern "C" fn predator_game_profile_clear() -> i32 {
    crate::safe_ffi(-1, || {
        game_profile::clear_profiles();
        0
    })
}

/// Polls the next event from the game profile event queue.
///
/// Returns 1 if an event was popped and written to output pointers, 0 if queue is empty,
/// or -1 if any required output pointer is invalid.
#[no_mangle]
pub unsafe extern "C" fn predator_game_profile_poll_event(
    out_event_type: *mut i32,
    out_profile_id: *mut u32,
    out_pid: *mut u32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_event_type)
            || !is_valid_mut_buffer(out_profile_id)
            || !is_valid_mut_buffer(out_pid)
        {
            return -1;
        }

        match game_profile::poll_event() {
            Some(event) => {
                let ev_type = event.event_type();
                std::ptr::write_unaligned(out_event_type, ev_type);

                match event {
                    game_profile::ProfileEvent::GameStarted { profile_id, pid } => {
                        std::ptr::write_unaligned(out_profile_id, profile_id);
                        std::ptr::write_unaligned(out_pid, pid);
                    }
                    game_profile::ProfileEvent::GameExited { profile_id, pid } => {
                        std::ptr::write_unaligned(out_profile_id, profile_id);
                        std::ptr::write_unaligned(out_pid, pid);
                    }
                    game_profile::ProfileEvent::GameSwitched {
                        old_profile_id,
                        new_profile_id,
                    } => {
                        std::ptr::write_unaligned(out_profile_id, new_profile_id);
                        std::ptr::write_unaligned(out_pid, old_profile_id);
                    }
                }
                1
            }
            None => {
                std::ptr::write_unaligned(out_event_type, game_profile::EVENT_TYPE_NONE);
                std::ptr::write_unaligned(out_profile_id, 0);
                std::ptr::write_unaligned(out_pid, 0);
                0
            }
        }
    })
}

/// Retrieves the currently active game (profile_id, pid).
///
/// Returns 1 if an active game is currently running, 0 if no game is active,
/// or -1 if output pointers are invalid.
#[no_mangle]
pub unsafe extern "C" fn predator_game_profile_get_active(
    out_profile_id: *mut u32,
    out_pid: *mut u32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_profile_id) || !is_valid_mut_buffer(out_pid) {
            return -1;
        }

        match game_profile::get_active() {
            Some((profile_id, pid)) => {
                std::ptr::write_unaligned(out_profile_id, profile_id);
                std::ptr::write_unaligned(out_pid, pid);
                1
            }
            None => {
                std::ptr::write_unaligned(out_profile_id, 0);
                std::ptr::write_unaligned(out_pid, 0);
                0
            }
        }
    })
}

// ============================================================================
// Display & Advanced Optimus (DDS) Exports
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_display_get_supported_refresh_rates(
    out_rates: *mut i32,
    max_count: usize,
    out_count: *mut usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_rates) || !is_valid_mut_buffer(out_count) || max_count == 0 {
            return -1;
        }
        let rates = crate::display::get_supported_refresh_rates();
        let copy_count = rates.len().min(max_count);
        for i in 0..copy_count {
            std::ptr::write_unaligned(out_rates.add(i), rates[i]);
        }
        std::ptr::write_unaligned(out_count, copy_count);
        0
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_display_get_current_refresh_rate() -> i32 {
    crate::safe_ffi(-1, || crate::display::get_current_refresh_rate())
}

#[no_mangle]
pub unsafe extern "C" fn predator_display_set_refresh_rate(hz: i32) -> i32 {
    crate::safe_ffi(-1, || {
        if crate::display::set_refresh_rate(hz).is_ok() {
            0
        } else {
            -1
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_display_set_bluelight_shield(mode: i32) -> i32 {
    crate::safe_ffi(-1, || {
        if crate::display::set_bluelight_shield(mode).is_ok() {
            0
        } else {
            -1
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_display_get_dds_status(
    out_supported: *mut i32,
    out_auto: *mut i32,
    out_mux: *mut i32,
    out_blocking: *mut i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_mut_buffer(out_supported)
            || !is_valid_mut_buffer(out_auto)
            || !is_valid_mut_buffer(out_mux)
            || !is_valid_mut_buffer(out_blocking)
        {
            return -1;
        }
        let st = crate::display::get_dds_status();
        std::ptr::write_unaligned(out_supported, if st.is_supported { 1 } else { 0 });
        std::ptr::write_unaligned(out_auto, if st.is_automatic { 1 } else { 0 });
        std::ptr::write_unaligned(out_mux, st.active_mux as i32);
        std::ptr::write_unaligned(out_blocking, if st.has_blocking_apps { 1 } else { 0 });
        0
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_display_set_dds_mode(mode: i32) -> i32 {
    crate::safe_ffi(-1, || {
        if crate::display::set_dds_mode(mode).is_ok() {
            0
        } else {
            -1
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_display_set_lcd_overdrive(enabled: i32) -> i32 {
    crate::safe_ffi(-1, || {
        if crate::display::set_lcd_overdrive(enabled != 0).is_ok() {
            0
        } else {
            -1
        }
    })
}

// ============================================================================
// Universal BIOS & Firmware Settings Exports
// ============================================================================

#[no_mangle]
pub unsafe extern "C" fn predator_bios_probe_support(
    out_support_mode: *mut i32,
    out_model_name: *mut u8,
    max_len: usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !out_support_mode.is_null() && !is_valid_mut_buffer(out_support_mode) {
            return -1;
        }
        if !out_model_name.is_null() && !is_valid_mut_buffer(out_model_name) {
            return -1;
        }
        crate::bios::with_bios_manager(|mgr| {
            if !out_support_mode.is_null() {
                std::ptr::write_unaligned(out_support_mode, mgr.get_support_mode());
            }
            if !out_model_name.is_null() && is_valid_mut_buffer(out_model_name) && max_len > 0 {
                let name = mgr.get_model_name().as_bytes();
                let len = name.len().min(max_len - 1);
                for i in 0..len {
                    std::ptr::write_unaligned(out_model_name.add(i), name[i]);
                }
                std::ptr::write_unaligned(out_model_name.add(len), 0);
            }
            0
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_bios_get_settings_count() -> i32 {
    crate::safe_ffi(-1, || {
        crate::bios::with_bios_manager(|mgr| mgr.get_settings_count() as i32)
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_bios_get_setting_info(
    index: usize,
    out_name: *mut u8,
    out_val: *mut u8,
    out_options: *mut u8,
    out_risk: *mut i32,
    out_kind: *mut i32,
    max_len: usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        if max_len == 0 {
            return -1;
        }
        crate::bios::with_bios_manager(|mgr| {
            if let Some(s) = mgr.get_setting(index) {
                if is_valid_mut_buffer(out_name) {
                    let b = s.name.as_bytes();
                    let len = b.len().min(max_len - 1);
                    for i in 0..len {
                        std::ptr::write_unaligned(out_name.add(i), b[i]);
                    }
                    std::ptr::write_unaligned(out_name.add(len), 0);
                }
                if is_valid_mut_buffer(out_val) {
                    let b = s.current.as_bytes();
                    let len = b.len().min(max_len - 1);
                    for i in 0..len {
                        std::ptr::write_unaligned(out_val.add(i), b[i]);
                    }
                    std::ptr::write_unaligned(out_val.add(len), 0);
                }
                if is_valid_mut_buffer(out_options) {
                    let joined = s.options.join(";");
                    let b = joined.as_bytes();
                    let len = b.len().min(max_len - 1);
                    for i in 0..len {
                        std::ptr::write_unaligned(out_options.add(i), b[i]);
                    }
                    std::ptr::write_unaligned(out_options.add(len), 0);
                }
                if is_valid_mut_buffer(out_risk) {
                    std::ptr::write_unaligned(out_risk, s.risk as i32);
                }
                if is_valid_mut_buffer(out_kind) {
                    std::ptr::write_unaligned(out_kind, s.kind as i32);
                }
                0
            } else {
                -1
            }
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_bios_apply_setting(
    name: *const u8,
    value: *const u8,
    password: *const u8,
    dangerous_allowed: i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        if !is_valid_buffer(name) || !is_valid_buffer(value) {
            return -1;
        }

        let name_str = match bios::read_ascii_c_str_limited(name, 128) {
            Ok(s) => s,
            Err(e) => return e,
        };
        let val_str = match bios::read_ascii_c_str_limited(value, 128) {
            Ok(s) => s,
            Err(e) => return e,
        };
        let pwd_str = if is_valid_buffer(password) {
            match bios::read_ascii_c_str_limited(password, 128) {
                Ok(s) => bios::ZeroizingString::new(s),
                Err(e) => return e,
            }
        } else {
            bios::ZeroizingString::default()
        };

        crate::bios::with_bios_manager(|mgr| {
            mgr.apply_setting(&name_str, &val_str, &pwd_str, dangerous_allowed != 0) as i32
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_bios_restore_defaults(
    password: *const u8,
    user_defaults: i32,
) -> i32 {
    crate::safe_ffi(-1, || {
        let pwd_str = if is_valid_buffer(password) {
            match bios::read_ascii_c_str_limited(password, 128) {
                Ok(s) => bios::ZeroizingString::new(s),
                Err(e) => return e,
            }
        } else {
            bios::ZeroizingString::default()
        };

        crate::bios::with_bios_manager(|mgr| {
            mgr.restore_defaults(&pwd_str, user_defaults != 0) as i32
        })
    })
}
