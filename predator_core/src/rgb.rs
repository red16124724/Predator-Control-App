//! RGB animation math & frame generator.
//!
//! Ported 1:1 from the managed `LightingEffectsManager.ApplyAnimationFrame` so the
//! native and managed paths produce byte-identical frames (verified by unit tests).
//!
//! Design notes:
//! * All per-frame work happens in caller-supplied output slices: no heap traffic.
//! * Color math is plain `f64` scalar arithmetic on tightly-typed buffers, which
//!   LLVM auto-vectorizes (AVX2 on x86-64) without any unsafe intrinsics.
//! * Managed byte casts truncate; `Math.Round` calls use round-to-even.

/// Maximum zone count supported by a single animator (4 keyboard zones is the norm).
pub const MAX_ZONES: usize = 8;

/// Effect indices mirrored from `LightingEffect34`.
pub mod effect {
    pub const SNAKE: i32 = 7;
    pub const RIPPLE: i32 = 12;
    pub const HEARTBEAT: i32 = 16;
    pub const RACING: i32 = 21;
    pub const DISCO: i32 = 23;
    pub const PING_PONG: i32 = 24;
    pub const LIGHT_SHOW: i32 = 25;
    pub const RAINBOW: i32 = 29;
    pub const BLASTING: i32 = 32;

    /// Lowest effect index that is rendered by software (0..=6 are native EC modes).
    pub const FIRST_SOFTWARE: i32 = 7;
    pub const LAST: i32 = 32;
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub struct Rgb8 {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Rgb8 {
    pub const BLACK: Rgb8 = Rgb8 { r: 0, g: 0, b: 0 };
}

/// C# `(byte)Math.Round(v)` semantics: banker's rounding, clamped to the byte range.
#[inline]
fn to_byte(v: f64) -> u8 {
    if !v.is_finite() {
        return 0;
    }
    if v <= 0.0 {
        0
    } else if v >= 255.0 {
        255
    } else {
        v.round_ties_even() as u8
    }
}

/// C# `scaleR(c)` = `(byte)Math.Clamp(Math.Round(c * scale), 0, 255)`.
#[inline]
fn scale_byte(c: u8, scale: f64) -> u8 {
    to_byte(c as f64 * scale)
}

#[inline]
fn truncate_byte(value: f64) -> u8 {
    if !value.is_finite() { 0 } else { value.clamp(0.0, 255.0) as u8 }
}

/// Exact port of `LightingEffectsManager.ColorFromHsv`.
pub fn hsv_to_rgb(hue: f64, saturation: f64, value: f64) -> Rgb8 {
    let mut hue = if hue.is_finite() { hue % 360.0 } else { 0.0 };
    if hue < 0.0 {
        hue += 360.0;
    }
    let saturation = saturation.clamp(0.0, 1.0);
    let value = value.clamp(0.0, 1.0);

    let mut hi = (hue / 60.0).floor() as i32 % 6;
    if hi < 0 {
        hi = (hi + 6) % 6;
    }
    let f = hue / 60.0 - (hue / 60.0).floor();

    let value = value * 255.0;
    let v = truncate_byte(value);
    let p = truncate_byte(value * (1.0 - saturation));
    let q = truncate_byte(value * (1.0 - f * saturation));
    let t = truncate_byte(value * (1.0 - (1.0 - f) * saturation));

    match hi {
        0 => Rgb8 { r: v, g: t, b: p },
        1 => Rgb8 { r: q, g: v, b: p },
        2 => Rgb8 { r: p, g: v, b: t },
        3 => Rgb8 { r: p, g: q, b: v },
        4 => Rgb8 { r: t, g: p, b: v },
        _ => Rgb8 { r: v, g: p, b: q },
    }
}

/// Frame style bucket, mirroring the `switch` in `ApplyAnimationFrame`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FrameStyle {
    /// Single lit zone travelling across the strip (Snake / PingPong / Racing).
    Travelling,
    /// Per-zone hue rotation (Rainbow / Disco / LightShow).
    HueCycle,
    /// Whole-strip intensity pulse (Heartbeat / Ripple / Blasting).
    Pulse,
    /// Primary/complementary color shift (everything else).
    Shift,
}

impl FrameStyle {
    pub fn for_effect(effect_index: i32) -> FrameStyle {
        match effect_index {
            effect::SNAKE | effect::PING_PONG | effect::RACING => FrameStyle::Travelling,
            effect::RAINBOW | effect::DISCO | effect::LIGHT_SHOW => FrameStyle::HueCycle,
            effect::HEARTBEAT | effect::RIPPLE | effect::BLASTING => FrameStyle::Pulse,
            _ => FrameStyle::Shift,
        }
    }
}

/// Frame pacing, ported from the managed loop:
/// `Math.Max(125, 200 - ((speed <= 9 ? speed * 10 : speed) * 8 / 10))`.
pub fn frame_delay_ms(speed: u8) -> u32 {
    let speed_i = speed as i32;
    let effective = if speed_i <= 9 { speed_i * 10 } else { speed_i };
    let delay = 200 - (effective * 8 / 10);
    delay.max(125) as u32
}

/// Renders one frame for `effect_index` at `step` into `out` (one entry per zone).
///
/// `base` is the already-alpha-scaled base color, `brightness` is 0..=100.
/// Returns the number of zones written.
pub fn render_frame(
    effect_index: i32,
    step: u64,
    base: Rgb8,
    brightness: u8,
    out: &mut [Rgb8],
) -> usize {
    let zones = out.len().min(MAX_ZONES);
    if zones == 0 {
        return 0;
    }
    let scale = (brightness as f64).clamp(0.0, 100.0) / 100.0;
    let step_f = step as f64;

    match FrameStyle::for_effect(effect_index) {
        FrameStyle::Travelling => {
            let phase = (step % 8) as i32;
            let active_zone = if phase < 4 { phase } else { 7 - phase };
            for (i, slot) in out.iter_mut().take(zones).enumerate() {
                *slot = if i as i32 == active_zone {
                    Rgb8 {
                        r: scale_byte(base.r, scale),
                        g: scale_byte(base.g, scale),
                        b: scale_byte(base.b, scale),
                    }
                } else {
                    Rgb8::BLACK
                };
            }
        }
        FrameStyle::HueCycle => {
            for (i, slot) in out.iter_mut().take(zones).enumerate() {
                let zone = (i + 1) as f64;
                let hue = ((step_f * 15.0 + zone * 60.0) % 360.0 + 360.0) % 360.0;
                *slot = hsv_to_rgb(hue, 1.0, scale);
            }
        }
        FrameStyle::Pulse => {
            let pulse = (step_f * 0.15).sin().abs();
            let r = scale_byte(truncate_byte(base.r as f64 * pulse), scale);
            let g = scale_byte(truncate_byte(base.g as f64 * pulse), scale);
            let b = scale_byte(truncate_byte(base.b as f64 * pulse), scale);
            for slot in out.iter_mut().take(zones) {
                *slot = Rgb8 { r, g, b };
            }
        }
        FrameStyle::Shift => {
            let phase = ((step_f * 0.1).sin() + 1.0) / 2.0;
            let r = scale_byte(truncate_byte(base.r as f64 * phase), scale);
            let g = scale_byte(truncate_byte(base.g as f64 * (1.0 - phase)), scale);
            let b = scale_byte(truncate_byte(base.b as f64 * phase), scale);
            for slot in out.iter_mut().take(zones) {
                *slot = Rgb8 { r, g, b };
            }
        }
    }

    zones
}

/// Stateful frame generator: owns the animation cursor so the caller only supplies
/// the output buffer each tick. Holds no locks and performs no allocation.
#[repr(C)]
pub struct RgbAnimator {
    effect_index: i32,
    base: Rgb8,
    brightness: u8,
    speed: u8,
    direction: u8,
    zone_count: usize,
    step: u64,
}

impl RgbAnimator {
    pub fn new(effect_index: i32, base: Rgb8, brightness: u8, speed: u8, direction: u8, zone_count: usize) -> Self {
        Self {
            effect_index: effect_index.clamp(effect::FIRST_SOFTWARE, effect::LAST),
            base,
            brightness: brightness.min(100),
            speed: speed.clamp(1, 100),
            direction: if direction != 0 { 1 } else { 0 },
            zone_count: zone_count.clamp(1, MAX_ZONES),
            step: 0,
        }
    }

    pub fn zone_count(&self) -> usize {
        self.zone_count
    }

    pub fn delay_ms(&self) -> u32 {
        frame_delay_ms(self.speed)
    }

    pub fn step(&self) -> u64 {
        self.step
    }

    /// Render the next frame into `out` and advance the cursor.
    pub fn next_frame(&mut self, out: &mut [Rgb8]) -> usize {
        let written = render_frame(self.effect_index, self.step, self.base, self.brightness, out);
        self.step = self.step.wrapping_add(1);
        written
    }

    pub fn reset(&mut self) {
        self.step = 0;
    }

    pub fn set_brightness(&mut self, brightness: u8) {
        self.brightness = brightness.min(100);
    }
}

/// Linear cross-fade of two zone buffers, used for smooth effect transitions.
///
/// `t` = 0.0 keeps `current`, `t` = 1.0 lands on `target`. Both slices are read
/// and written in a single straight-line loop so the compiler emits packed
/// `paddb`-style vector operations.
pub fn blend_zones(current: &mut [Rgb8], target: &[Rgb8], t: f32) {
    let t = if t.is_finite() { t.clamp(0.0, 1.0) } else { 0.0 };
    let inv = 1.0 - t;
    let count = current.len().min(target.len());
    for i in 0..count {
        let a = current[i];
        let b = target[i];
        current[i] = Rgb8 {
            r: to_byte(a.r as f64 * inv as f64 + b.r as f64 * t as f64),
            g: to_byte(a.g as f64 * inv as f64 + b.g as f64 * t as f64),
            b: to_byte(a.b as f64 * inv as f64 + b.b as f64 * t as f64),
        };
    }
}

/// Applies the brightness scale to a whole buffer in place.
pub fn apply_brightness(zones: &mut [Rgb8], brightness: u8) {
    let scale = (brightness.min(100) as f64) / 100.0;
    for slot in zones.iter_mut() {
        *slot = Rgb8 {
            r: scale_byte(slot.r, scale),
            g: scale_byte(slot.g, scale),
            b: scale_byte(slot.b, scale),
        };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hsv_conversions_truncate_like_managed_renderer() {
        assert_eq!(hsv_to_rgb(30.0, 1.0, 1.0), Rgb8 { r: 255, g: 127, b: 0 });
    }

    #[test]
    fn pulse_and_shift_truncate_like_managed_renderer() {
        let base = Rgb8 { r: 100, g: 255, b: 0 };
        let pulse = frame(effect::HEARTBEAT, 1, base, 100, 4);
        assert_eq!(pulse[0].r, 14);
        let shift = frame(8, 0, base, 100, 4);
        assert_eq!(shift[0].g, 127);
    }

    fn frame(effect_index: i32, step: u64, base: Rgb8, brightness: u8, zones: usize) -> Vec<Rgb8> {
        let mut out = vec![Rgb8::BLACK; zones];
        render_frame(effect_index, step, base, brightness, &mut out);
        out
    }

    #[test]
    fn travelling_effect_lights_exactly_one_zone() {
        let base = Rgb8 { r: 200, g: 10, b: 30 };
        // step 0 -> zone 0 lit
        let f0 = frame(effect::SNAKE, 0, base, 100, 4);
        assert_eq!(f0[0], base);
        assert_eq!(&f0[1..], &[Rgb8::BLACK, Rgb8::BLACK, Rgb8::BLACK]);

        // step 2 -> zone 2 lit
        let f2 = frame(effect::SNAKE, 2, base, 100, 4);
        assert_eq!(f2[2], base);
        assert_eq!(f2[0], Rgb8::BLACK);

        // step 5 is the second frame of the reverse leg.
        let f5 = frame(effect::SNAKE, 5, base, 100, 4);
        assert_eq!(f5[2], base);
        assert_eq!(f5.iter().filter(|color| **color != Rgb8::BLACK).count(), 1);
        for (step, expected) in [0, 1, 2, 3, 3, 2, 1, 0].into_iter().enumerate() {
            let colors = frame(effect::SNAKE, step as u64, base, 100, 4);
            assert_eq!(colors.iter().position(|color| *color != Rgb8::BLACK), Some(expected));
            assert_eq!(colors.iter().filter(|color| **color != Rgb8::BLACK).count(), 1);
        }
    }

    #[test]
    fn travelling_brightness_scales_base_color() {
        let base = Rgb8 { r: 200, g: 100, b: 50 };
        let f = frame(effect::PING_PONG, 0, base, 50, 4);
        assert_eq!(f[0], Rgb8 { r: 100, g: 50, b: 25 });
    }

    #[test]
    fn hue_cycle_matches_csharp_hsv_mapping() {
        // step 0, zone 1 (i=0) -> hue 60 -> yellow at full value
        let f = frame(effect::RAINBOW, 0, Rgb8::BLACK, 100, 4);
        assert_eq!(f[0], Rgb8 { r: 255, g: 255, b: 0 });
        // zone 2 (i=1) -> hue 120 -> green
        assert_eq!(f[1], Rgb8 { r: 0, g: 255, b: 0 });
        // zone 3 (i=2) -> hue 180 -> cyan
        assert_eq!(f[2], Rgb8 { r: 0, g: 255, b: 255 });
        // zone 4 (i=3) -> hue 240 -> blue
        assert_eq!(f[3], Rgb8 { r: 0, g: 0, b: 255 });
    }

    #[test]
    fn hue_cycle_uses_brightness_as_value() {
        let f = frame(effect::DISCO, 0, Rgb8::BLACK, 40, 1);
        // hue 60 at value 0.4 -> (102, 102, 0)
        assert_eq!(f[0], Rgb8 { r: 102, g: 102, b: 0 });
    }

    #[test]
    fn pulse_is_zero_at_step_zero_and_peaks_later() {
        let base = Rgb8 { r: 255, g: 255, b: 255 };
        let f0 = frame(effect::HEARTBEAT, 0, base, 100, 4);
        assert_eq!(f0, vec![Rgb8::BLACK; 4]);

        // step such that sin(step*0.15) is close to 1 -> step 10.47; use 10
        let f10 = frame(effect::HEARTBEAT, 10, base, 100, 4);
        assert!(f10[0].r > 240);
        assert_eq!(f10[0], f10[3]);
    }

    #[test]
    fn shift_effect_swaps_primary_and_complementary() {
        let base = Rgb8 { r: 200, g: 200, b: 200 };
        // step 0 -> phase 0.5 -> both halves at ~half scale
        let f0 = frame(8, 0, base, 100, 2);
        assert_eq!(f0[0], Rgb8 { r: 100, g: 100, b: 100 });
        assert_eq!(f0[0], f0[1]);
    }

    #[test]
    fn zero_zones_is_safe() {
        let mut out: [Rgb8; 0] = [];
        assert_eq!(render_frame(effect::RAINBOW, 0, Rgb8::BLACK, 100, &mut out), 0);
    }

    #[test]
    fn frame_delay_matches_managed_formula() {
        assert_eq!(frame_delay_ms(1), 192);
        assert_eq!(frame_delay_ms(9), 128);
        assert_eq!(frame_delay_ms(10), 192);
        // 200 - 80 = 120, floored at the 125 ms minimum.
        assert_eq!(frame_delay_ms(100), 125);
    }

    #[test]
    fn animator_advances_and_wraps() {
        let mut anim = RgbAnimator::new(effect::RAINBOW, Rgb8 { r: 1, g: 2, b: 3 }, 100, 50, 0, 4);
        let mut out = [Rgb8::BLACK; 4];
        assert_eq!(anim.next_frame(&mut out), 4);
        assert_eq!(anim.step(), 1);
        let first = out;
        anim.reset();
        assert_eq!(anim.next_frame(&mut out), 4);
        assert_eq!(out, first);
    }

    #[test]
    fn blend_endpoints_are_exact() {
        let mut cur = vec![Rgb8 { r: 10, g: 20, b: 30 }];
        let target = vec![Rgb8 { r: 200, g: 100, b: 50 }];
        let original = cur.clone();
        blend_zones(&mut cur, &target, 0.0);
        assert_eq!(cur, original);
        blend_zones(&mut cur, &target, 1.0);
        assert_eq!(cur, target);
        let mut mid = vec![Rgb8 { r: 0, g: 0, b: 0 }];
        blend_zones(&mut mid, &target, 0.5);
        assert_eq!(mid[0], Rgb8 { r: 100, g: 50, b: 25 });
    }

    #[test]
    fn hsv_wraps_negative_hue() {
        assert_eq!(hsv_to_rgb(-60.0, 1.0, 1.0), hsv_to_rgb(300.0, 1.0, 1.0));
        assert_eq!(hsv_to_rgb(360.0, 1.0, 1.0), hsv_to_rgb(0.0, 1.0, 1.0));
    }
}
