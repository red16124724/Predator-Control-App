//! Series-to-pixel projection for the telemetry history graph.
//!
//! Ports the per-frame loop in `HistoryGraphControl.DrawSeries`. Only the
//! arithmetic moves here; GDI+ drawing, brushes and fonts stay managed.

use std::slice;

/// Projects one normalised value onto a pixel height.
///
/// Mirrors the managed clamp chain: non-finite or zero ranges collapse to a
/// unit range, values are clamped to `[0, 1]`, and the result is inset by the
/// graph's 3 px vertical padding.
pub fn project_y(value: f64, min: f64, max: f64, height: f32) -> f32 {
    // A non-finite or degenerate height has no meaningful projection.
    if !height.is_finite() || height <= 0.0 {
        return 0.0;
    }

    let lo = if min.is_finite() { min.min(max) } else { 0.0 };
    let hi = if max.is_finite() { max.max(min) } else { 1.0 };
    let mut range = hi - lo;
    if !range.is_finite() || range <= 0.0001 {
        range = 1.0;
    }

    let v = if value.is_finite() { value } else { lo };
    let norm = ((v - lo) / range).clamp(0.0, 1.0) as f32;
    let norm = if norm.is_finite() { norm } else { 0.0 };

    let y = height - (norm * (height - 6.0)) - 3.0;
    if y.is_finite() {
        y
    } else {
        height / 2.0
    }
}

/// Horizontal spacing between adjacent samples.
pub fn step_x(width: f32, capacity: i32) -> f32 {
    let divisor = capacity.max(1) as f32 - 1.0;
    let step = width / divisor.max(1.0);
    if step.is_finite() {
        step
    } else {
        0.0
    }
}

/// Left edge of the first sample when the buffer is not yet full.
pub fn start_x(capacity: i32, sample_count: i32, step: f32) -> f32 {
    let missing = (capacity - sample_count).max(0) as f32;
    let x = missing * step;
    if x.is_finite() {
        x
    } else {
        0.0
    }
}

/// Fills `out` with interleaved `(x, y)` pairs for one series.
///
/// `samples` holds `count` values oldest-first with `NaN` marking gaps.
/// Returns the number of points written.
///
/// # Safety
/// `out` must be valid for `2 * count` writes.
pub unsafe fn project_series(
    samples: *const f64,
    count: i32,
    capacity: i32,
    width: f32,
    height: f32,
    min: f64,
    max: f64,
    out: *mut f32,
) -> i32 {
    if count <= 0
        || count > 100_000
        || width <= 0.0
        || height <= 0.0
        || !width.is_finite()
        || !height.is_finite()
        || !crate::is_valid_buffer_range(samples, count as usize)
        || !crate::is_valid_mut_buffer_range(out, (count as usize).saturating_mul(2))
    {
        return 0;
    }

    let n = count as usize;
    let step = step_x(width, capacity);
    let origin = start_x(capacity, count, step);
    let lo = if min.is_finite() { min } else { 0.0 };

    let samples_aligned = (samples as usize) % std::mem::align_of::<f64>() == 0;
    let out_aligned = (out as usize) % std::mem::align_of::<f32>() == 0;

    if samples_aligned && out_aligned {
        let input = slice::from_raw_parts(samples, n);
        let output = slice::from_raw_parts_mut(out, n * 2);
        for (i, raw) in input.iter().enumerate() {
            let value = if raw.is_finite() { *raw } else { lo };
            let x = origin + (i as f32 * step);
            let y = project_y(value, min, max, height);
            output[i * 2] = if x.is_finite() { x } else { 0.0 };
            output[i * 2 + 1] = y;
        }
    } else {
        for i in 0..n {
            let raw = std::ptr::read_unaligned(samples.add(i));
            let value = if raw.is_finite() { raw } else { lo };
            let x = origin + (i as f32 * step);
            let y = project_y(value, min, max, height);
            std::ptr::write_unaligned(out.add(i * 2), if x.is_finite() { x } else { 0.0 });
            std::ptr::write_unaligned(out.add(i * 2 + 1), y);
        }
    }

    count
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_range_endpoints() {
        // Range 0..100 in a 103 px box: max sits 3 px down, min 3 px up.
        assert!((project_y(100.0, 0.0, 100.0, 103.0) - 3.0).abs() < 0.01);
        assert!((project_y(0.0, 0.0, 100.0, 103.0) - 100.0).abs() < 0.01);
    }

    #[test]
    fn clamps_out_of_range_values() {
        assert!((project_y(500.0, 0.0, 100.0, 103.0) - 3.0).abs() < 0.01);
        assert!((project_y(-500.0, 0.0, 100.0, 103.0) - 100.0).abs() < 0.01);
    }

    #[test]
    fn degenerate_range_does_not_divide_by_zero() {
        let y = project_y(5.0, 5.0, 5.0, 100.0);
        assert!(y.is_finite());
        let y = project_y(5.0, -1.0, -1.0, 100.0);
        assert!(y.is_finite());
    }

    #[test]
    fn non_finite_inputs_are_stable() {
        assert!(project_y(f64::NAN, 0.0, 100.0, 80.0).is_finite());
        assert!(project_y(50.0, f64::NAN, f64::NAN, 80.0).is_finite());
        assert!(project_y(50.0, 0.0, 100.0, f32::NAN).is_finite());
    }

    #[test]
    fn step_and_origin_account_for_partial_buffers() {
        // 60 slots across 590 px -> 10 px apart.
        assert!((step_x(590.0, 60) - 10.0).abs() < 0.001);
        // 30 samples in a 60-slot buffer start half way across.
        assert!((start_x(60, 30, 10.0) - 300.0).abs() < 0.001);
        assert_eq!(start_x(60, 60, 10.0), 0.0);
    }

    #[test]
    fn projects_a_series_left_to_right() {
        let samples = [0.0, 50.0, 100.0];
        let mut out = [0.0f32; 6];
        let n = unsafe {
            project_series(
                samples.as_ptr(),
                samples.len() as i32,
                3,
                100.0,
                103.0,
                0.0,
                100.0,
                out.as_mut_ptr(),
            )
        };
        assert_eq!(n, 3);
        // x increases left to right.
        assert!(out[0] < out[2] && out[2] < out[4]);
        // y decreases as value increases.
        assert!(out[1] > out[3] && out[3] > out[5]);
    }

    #[test]
    fn gaps_render_without_panicking() {
        let samples = [10.0, f64::NAN, 30.0];
        let mut out = [0.0f32; 6];
        let n = unsafe {
            project_series(
                samples.as_ptr(),
                3,
                3,
                200.0,
                100.0,
                0.0,
                100.0,
                out.as_mut_ptr(),
            )
        };
        assert_eq!(n, 3);
        assert!(out.iter().all(|v| v.is_finite()));
    }

    #[test]
    fn rejects_null_and_empty_input() {
        let mut out = [0.0f32; 4];
        let samples = [1.0];
        unsafe {
            assert_eq!(
                project_series(
                    std::ptr::null(),
                    1,
                    1,
                    10.0,
                    10.0,
                    0.0,
                    1.0,
                    out.as_mut_ptr()
                ),
                0
            );
            assert_eq!(
                project_series(
                    samples.as_ptr(),
                    0,
                    1,
                    10.0,
                    10.0,
                    0.0,
                    1.0,
                    out.as_mut_ptr()
                ),
                0
            );
            assert_eq!(
                project_series(
                    samples.as_ptr(),
                    1,
                    1,
                    10.0,
                    10.0,
                    0.0,
                    1.0,
                    std::ptr::null_mut()
                ),
                0
            );
        }
    }
}
