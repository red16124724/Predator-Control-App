//! C-ABI exports for the migrated subsystems.
//!
//! Each function is a thin wrapper over a pure Rust module and is wrapped in
//! [`crate::safe_ffi`] so that no panic can unwind across the FFI boundary.
//! Out-parameters are written with `write_unaligned` because managed callers
//! may hand over buffers that are not naturally aligned.

use crate::{battery, graphproj, history, hook, pipeguard, procsync, shortcuts};

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
    crate::safe_ffi(0, || if shortcuts::is_reserved_ctrl_key(vk) { 1 } else { 0 })
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
        if out_rate.is_null() {
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
        if out_rate.is_null() {
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
            let (code, seconds) = calc.calculate(
                phase,
                remaining_capacity,
                full_capacity,
                rate,
                allow_warmup != 0,
                percent,
            );
            if !out_seconds.is_null() && code != battery::RESULT_NONE {
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
        if some && !out_seconds.is_null() {
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
            Some(v) if !out_value.is_null() => {
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
            Some((min, _)) if !out_value.is_null() => {
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
            Some((_, max)) if !out_value.is_null() => {
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
            Some(avg) if !out_value.is_null() => {
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
            Some(v) if !out_value.is_null() => {
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
            state.set_shortcuts(entries, count);
            state.shortcut_count() as i32
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
            if !out_value.is_null() {
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
        if input.is_null() || out.is_null() {
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
