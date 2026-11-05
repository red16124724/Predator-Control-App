//! Battery calibration ETA estimation.
//!
//! Ports `BatteryCalibrationEtaCalculator` from the managed application. All
//! smoothing, phase handling and plausibility clamping lives here; the OS
//! query that supplies `BatteryLifeRemaining` stays in C# because it is a
//! WinForms `SystemInformation` call, not arithmetic.

use std::sync::Mutex;

/// Phase 1: charging to full.
pub const PHASE_CHARGING: i32 = 1;
/// Phase 2: discharging to empty.
pub const PHASE_DISCHARGING: i32 = 2;

/// `seconds` holds a computed estimate.
pub const RESULT_ESTIMATE: i32 = 1;
/// No estimate is available yet.
pub const RESULT_NONE: i32 = 0;
/// The caller must apply its warm-up fallback (needs an OS power query).
pub const RESULT_WARMUP: i32 = 2;

/// Default EMA smoothing factor.
pub const DEFAULT_ALPHA: f64 = 0.2;

const MAX_RATE_MW: f64 = 500_000.0;
const MIN_RATE_MW: f64 = 500.0;
const MAX_RATE_MW_LIMIT: f64 = 350_000.0;
const MAX_PERCENT_PER_MINUTE: f64 = 30.0;

fn finite(v: f64) -> bool {
    v.is_finite()
}

/// Exponentially smoothed battery rate estimator.
#[derive(Debug)]
pub struct EtaCalculator {
    alpha: f64,
    smoothed_rate: Option<f64>,
    smoothed_percent_per_minute: Option<f64>,
    last_percent: Option<i32>,
    last_percent_ms: Option<i64>,
    last_capacity: Option<i32>,
    last_capacity_ms: Option<i64>,
    last_phase: i32,
}

impl Default for EtaCalculator {
    fn default() -> Self {
        Self::new(DEFAULT_ALPHA)
    }
}

impl EtaCalculator {
    pub fn new(alpha: f64) -> Self {
        Self {
            alpha: sanitize_alpha(alpha),
            smoothed_rate: None,
            smoothed_percent_per_minute: None,
            last_percent: None,
            last_percent_ms: None,
            last_capacity: None,
            last_capacity_ms: None,
            last_phase: -1,
        }
    }

    pub fn set_alpha(&mut self, alpha: f64) {
        self.alpha = sanitize_alpha(alpha);
    }

    pub fn alpha(&self) -> f64 {
        self.alpha
    }

    pub fn smoothed_rate(&self) -> Option<f64> {
        self.smoothed_rate
    }

    pub fn smoothed_percent_per_minute(&self) -> Option<f64> {
        self.smoothed_percent_per_minute
    }

    pub fn last_percent(&self) -> Option<i32> {
        self.last_percent
    }

    pub fn last_capacity(&self) -> Option<i32> {
        self.last_capacity
    }

    pub fn reset(&mut self) {
        self.smoothed_rate = None;
        self.smoothed_percent_per_minute = None;
        self.last_percent = None;
        self.last_percent_ms = None;
        self.last_capacity = None;
        self.last_capacity_ms = None;
        self.last_phase = -1;
    }

    /// Folds a direct mW reading into the smoothed rate.
    pub fn update_direct_rate(&mut self, raw: f64) {
        let raw = raw.abs();
        if !finite(raw) || raw <= 0.0 || raw >= 0x0FFF_FFFF as f64 || raw > MAX_RATE_MW {
            return;
        }
        self.smoothed_rate = Some(match self.smoothed_rate {
            None => raw,
            Some(prev) => (self.alpha * raw) + ((1.0 - self.alpha) * prev),
        });
    }

    /// Folds a remaining-capacity reading taken at `ts_ms` (unix millis).
    pub fn update_capacity(&mut self, capacity: i32, ts_ms: i64) {
        if capacity <= 0 {
            return;
        }

        let Some(prev_ts) = self.last_capacity_ms else {
            self.last_capacity = Some(capacity);
            self.last_capacity_ms = Some(ts_ms);
            return;
        };

        if ts_ms < prev_ts {
            self.last_capacity = Some(capacity);
            self.last_capacity_ms = Some(ts_ms);
            return;
        }

        let Some(prev_cap) = self.last_capacity else {
            self.last_capacity = Some(capacity);
            self.last_capacity_ms = Some(ts_ms);
            return;
        };

        let seconds = (ts_ms - prev_ts) as f64 / 1000.0;

        // Sleep/hibernate or clock skew: re-baseline without corrupting the rate.
        if seconds > 300.0 {
            self.last_capacity = Some(capacity);
            self.last_capacity_ms = Some(ts_ms);
            return;
        }

        // A capacity move against the phase means AC was plugged or unplugged.
        if self.last_phase == PHASE_CHARGING && capacity < prev_cap {
            self.last_capacity = Some(capacity);
            self.last_capacity_ms = Some(ts_ms);
            return;
        }
        if self.last_phase == PHASE_DISCHARGING && capacity > prev_cap {
            self.last_capacity = Some(capacity);
            self.last_capacity_ms = Some(ts_ms);
            return;
        }

        if seconds >= 6.0 && capacity != prev_cap {
            let delta = (capacity - prev_cap).abs() as f64;
            let hours = seconds / 3600.0;
            if hours > 0.0 {
                let rate_mw = delta / hours;
                if finite(rate_mw) && (MIN_RATE_MW..=MAX_RATE_MW_LIMIT).contains(&rate_mw) {
                    self.update_direct_rate(rate_mw);
                }
            }
            self.last_capacity = Some(capacity);
            self.last_capacity_ms = Some(ts_ms);
        }
    }

    /// Folds a battery-percentage reading taken at `ts_ms` (unix millis).
    pub fn update_percentage(&mut self, percent: i32, ts_ms: i64) {
        if !(0..=100).contains(&percent) {
            return;
        }

        let Some(prev_ts) = self.last_percent_ms else {
            self.last_percent = Some(percent);
            self.last_percent_ms = Some(ts_ms);
            return;
        };

        if ts_ms < prev_ts {
            self.last_percent = Some(percent);
            self.last_percent_ms = Some(ts_ms);
            return;
        }

        let Some(prev_pct) = self.last_percent else {
            self.last_percent = Some(percent);
            self.last_percent_ms = Some(ts_ms);
            return;
        };

        let minutes = (ts_ms - prev_ts) as f64 / 60_000.0;

        // Sleep/hibernate or clock skew: re-baseline.
        if minutes > 120.0 {
            self.last_percent = Some(percent);
            self.last_percent_ms = Some(ts_ms);
            return;
        }

        if self.last_phase == PHASE_CHARGING && percent < prev_pct {
            self.last_percent = Some(percent);
            self.last_percent_ms = Some(ts_ms);
            return;
        }
        if self.last_phase == PHASE_DISCHARGING && percent > prev_pct {
            self.last_percent = Some(percent);
            self.last_percent_ms = Some(ts_ms);
            return;
        }

        if minutes >= 0.05 && percent != prev_pct {
            let delta = (percent - prev_pct).abs() as f64;
            let rate_per_min = (delta / minutes).min(MAX_PERCENT_PER_MINUTE);
            if finite(rate_per_min) && rate_per_min > 0.0 {
                self.smoothed_percent_per_minute = Some(match self.smoothed_percent_per_minute {
                    None => rate_per_min,
                    Some(prev) => (self.alpha * rate_per_min) + ((1.0 - self.alpha) * prev),
                });
                self.last_percent = Some(percent);
                self.last_percent_ms = Some(ts_ms);
            }
        }
    }

    /// Computes the estimated remaining time for `phase`.
    ///
    /// `direct_rate_mw` is the raw rate reported by the battery, if any.
    /// Returns `RESULT_ESTIMATE` with `out_seconds` set, `RESULT_NONE`, or
    /// `RESULT_WARMUP` when the caller should supply its own fallback.
    pub fn calculate(
        &mut self,
        phase: i32,
        remaining_capacity: i32,
        full_capacity: i32,
        direct_rate_mw: Option<f64>,
        allow_warmup_fallback: bool,
        battery_percent: Option<i32>,
    ) -> (i32, f64) {
        if self.last_phase != -1 && self.last_phase != phase {
            self.reset();
        }
        self.last_phase = phase;

        let mut remaining = remaining_capacity.max(0);
        let full = full_capacity;

        if full > 0 && remaining > full {
            remaining = full;
        }

        if remaining <= 0 {
            if let Some(pct) = battery_percent {
                if (1..=100).contains(&pct) && full > 100 {
                    remaining = (full as f64 * (pct.clamp(0, 100) as f64 / 100.0)).round() as i32;
                }
            }
        }

        if let Some(rate) = direct_rate_mw {
            if rate > 0.0 && rate < MAX_RATE_MW && rate != 4_294_967_295.0 {
                self.update_direct_rate(rate);
            }
        }

        // Infer a mW rate from the percentage slope when only that is known.
        if self.smoothed_rate.is_none() && full > 100 {
            if let Some(ppm) = self.smoothed_percent_per_minute {
                if ppm > 0.0001 {
                    let inferred = (ppm * 60.0 / 100.0) * full as f64;
                    if (MIN_RATE_MW..=MAX_RATE_MW_LIMIT).contains(&inferred) {
                        self.smoothed_rate = Some(inferred);
                    }
                }
            }
        }

        if phase == PHASE_CHARGING {
            if let Some(pct) = battery_percent {
                if pct >= 100 {
                    return (RESULT_ESTIMATE, 0.0);
                }
            }
            if full > 100 && remaining >= full {
                return (RESULT_ESTIMATE, 0.0);
            }

            if full > 100 && full > remaining {
                if let Some(rate) = self.smoothed_rate {
                    if finite(rate) && rate > 0.0001 {
                        let delta = (full - remaining).max(0) as f64;
                        if delta <= 0.0 {
                            return (RESULT_ESTIMATE, 0.0);
                        }
                        let hours = delta / rate;
                        if finite(hours) && (0.0..=72.0).contains(&hours) {
                            return (RESULT_ESTIMATE, hours * 3600.0);
                        }
                    }
                }
            }

            if let Some(ppm) = self.smoothed_percent_per_minute {
                if finite(ppm) && ppm > 0.0001 {
                    let current_pct = if full > 0 {
                        remaining as f64 * 100.0 / full as f64
                    } else {
                        self.last_percent.unwrap_or(0) as f64
                    };
                    if current_pct >= 100.0 {
                        return (RESULT_ESTIMATE, 0.0);
                    }
                    let remaining_pct = (100.0 - current_pct).clamp(0.0, 100.0);
                    if remaining_pct <= 0.0 {
                        return (RESULT_ESTIMATE, 0.0);
                    }
                    let mins = remaining_pct / ppm;
                    if finite(mins) && (0.0..=4320.0).contains(&mins) {
                        return (RESULT_ESTIMATE, mins * 60.0);
                    }
                }
            }
        } else if phase == PHASE_DISCHARGING {
            if let Some(pct) = battery_percent {
                if pct <= 0 {
                    return (RESULT_ESTIMATE, 0.0);
                }
            }
            if remaining <= 0 {
                return (RESULT_ESTIMATE, 0.0);
            }

            if full > 100 && remaining > 0 {
                if let Some(rate) = self.smoothed_rate {
                    if finite(rate) && rate > 0.0001 {
                        let hours = remaining as f64 / rate;
                        if finite(hours) && (0.0..=72.0).contains(&hours) {
                            return (RESULT_ESTIMATE, hours * 3600.0);
                        }
                    }
                }
            }

            if let Some(ppm) = self.smoothed_percent_per_minute {
                if finite(ppm) && ppm > 0.0001 {
                    let current_pct = if full > 0 {
                        remaining as f64 * 100.0 / full as f64
                    } else {
                        self.last_percent.unwrap_or(0) as f64
                    };
                    if current_pct <= 0.0 {
                        return (RESULT_ESTIMATE, 0.0);
                    }
                    let clamped = current_pct.clamp(0.0, 100.0);
                    let mins = clamped / ppm;
                    if finite(mins) && (0.0..=4320.0).contains(&mins) {
                        return (RESULT_ESTIMATE, mins * 60.0);
                    }
                }
            }
        }

        if allow_warmup_fallback {
            return (RESULT_WARMUP, 0.0);
        }

        (RESULT_NONE, 0.0)
    }

    /// Warm-up estimate for phase 1 only; phase 2 needs an OS power query.
    ///
    /// Returns `(some, seconds)`.
    pub fn warmup_estimate(phase: i32, battery_percent: i32) -> (bool, f64) {
        let pct = battery_percent.clamp(0, 100);
        if phase == PHASE_CHARGING {
            let remaining = (100 - pct).clamp(0, 100);
            if remaining <= 0 {
                return (true, 0.0);
            }
            // ~1.5 minutes per percent on a typical 60 Wh pack.
            return (true, remaining as f64 * 1.5 * 60.0);
        }
        (false, 0.0)
    }
}

fn sanitize_alpha(alpha: f64) -> f64 {
    if !finite(alpha) {
        DEFAULT_ALPHA
    } else {
        alpha.clamp(0.01, 1.0)
    }
}

static GLOBAL_ETA: Mutex<Option<EtaCalculator>> = Mutex::new(None);

/// Runs `f` against the global calculator, creating it on first use.
pub fn with_eta<F, R>(default: R, f: F) -> R
where
    F: FnOnce(&mut EtaCalculator) -> R,
{
    let mut guard = GLOBAL_ETA.lock().unwrap_or_else(|e| e.into_inner());
    if guard.is_none() {
        *guard = Some(EtaCalculator::default());
    }
    match guard.as_mut() {
        Some(calc) => f(calc),
        None => default,
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct RawBatteryInfo {
    pub capabilities: u32,
    pub technology: u8,
    pub designed_capacity: u32,
    pub full_charged_capacity: u32,
    pub cycle_count: u32,
    pub wear_level_percent: f64,
}

/// Decodes Windows SetupAPI BATTERY_INFORMATION (36 bytes) buffer.
pub fn parse_battery_info(bytes: &[u8]) -> Option<RawBatteryInfo> {
    if bytes.len() < 36 {
        return None;
    }

    let capabilities = u32::from_le_bytes(bytes[0..4].try_into().ok()?);
    let technology = bytes[4];
    let designed_capacity = u32::from_le_bytes(bytes[12..16].try_into().ok()?);
    let full_charged_capacity = u32::from_le_bytes(bytes[16..20].try_into().ok()?);
    let cycle_count = u32::from_le_bytes(bytes[32..36].try_into().ok()?);

    let wear_level_percent = if designed_capacity > 0 {
        let wear = (1.0 - (full_charged_capacity as f64 / designed_capacity as f64)) * 100.0;
        wear.clamp(0.0, 100.0)
    } else {
        0.0
    };

    Some(RawBatteryInfo {
        capabilities,
        technology,
        designed_capacity,
        full_charged_capacity,
        cycle_count,
        wear_level_percent,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn alpha_is_clamped_and_sanitized() {
        let c = EtaCalculator::new(5.0);
        assert_eq!(c.alpha(), 1.0);
        let c = EtaCalculator::new(0.0);
        assert_eq!(c.alpha(), 0.01);
        let c = EtaCalculator::new(f64::NAN);
        assert_eq!(c.alpha(), DEFAULT_ALPHA);
    }

    #[test]
    fn ignores_implausible_rates() {
        let mut c = EtaCalculator::new(1.0);
        // Zero, out-of-range and non-finite readings are rejected.
        c.update_direct_rate(0.0);
        c.update_direct_rate(600_000.0);
        c.update_direct_rate(f64::NAN);
        c.update_direct_rate(f64::INFINITY);
        assert!(c.smoothed_rate().is_none());
        // A negative reading is folded in by magnitude, matching the managed
        // implementation's `Math.Abs` behaviour.
        c.update_direct_rate(-5.0);
        assert_eq!(c.smoothed_rate(), Some(5.0));
        c.update_direct_rate(40_000.0);
        assert_eq!(c.smoothed_rate(), Some(40_000.0));
    }

    #[test]
    fn exceeds_no_capacity_estimate_from_rate() {
        let mut c = EtaCalculator::new(1.0);
        // 60 Wh pack, 30 Wh left, 30 W rate -> one hour.
        let (code, secs) = c.calculate(PHASE_CHARGING, 30_000, 60_000, Some(30_000.0), false, None);
        assert_eq!(code, RESULT_ESTIMATE);
        assert!((secs - 3600.0).abs() < 1.0, "got {secs}");
    }

    #[test]
    fn discharging_estimate_uses_remaining_capacity() {
        let mut c = EtaCalculator::new(1.0);
        let (code, secs) = c.calculate(
            PHASE_DISCHARGING,
            30_000,
            60_000,
            Some(30_000.0),
            false,
            None,
        );
        assert_eq!(code, RESULT_ESTIMATE);
        assert!((secs - 3600.0).abs() < 1.0, "got {secs}");
    }

    #[test]
    fn full_battery_returns_zero() {
        let mut c = EtaCalculator::new(1.0);
        let (code, secs) = c.calculate(PHASE_CHARGING, 60_000, 60_000, None, false, Some(100));
        assert_eq!(code, RESULT_ESTIMATE);
        assert_eq!(secs, 0.0);
    }

    #[test]
    fn requests_warmup_when_allowed() {
        let mut c = EtaCalculator::new(1.0);
        let (code, _) = c.calculate(PHASE_CHARGING, 30_000, 60_000, None, true, Some(50));
        assert_eq!(code, RESULT_WARMUP);
    }

    #[test]
    fn sleep_gap_rebaselines_without_rate() {
        let mut c = EtaCalculator::new(1.0);
        c.update_capacity(50_000, 0);
        // > 300 s gap -> re-baseline, no rate derived.
        c.update_capacity(40_000, 400_000);
        assert!(c.smoothed_rate().is_none());
        assert_eq!(c.last_capacity(), Some(40_000));
    }

    #[test]
    fn short_gap_derives_rate() {
        let mut c = EtaCalculator::new(1.0);
        c.update_capacity(50_000, 0);
        // 18 s, 600 mWh drop -> 120 W.
        c.update_capacity(50_600, 18_000);
        assert!(c.smoothed_rate().is_some());
    }

    #[test]
    fn percentage_rate_is_capped() {
        let mut c = EtaCalculator::new(1.0);
        c.update_percentage(50, 0);
        // 20 points in 6 s is absurd; capped at 30 %/min.
        c.update_percentage(70, 6_000);
        assert!(c.smoothed_percent_per_minute().unwrap() <= MAX_PERCENT_PER_MINUTE);
    }

    #[test]
    fn phase_change_resets_state() {
        let mut c = EtaCalculator::new(1.0);
        c.calculate(PHASE_CHARGING, 30_000, 60_000, Some(30_000.0), false, None);
        assert!(c.smoothed_rate().is_some());
        c.calculate(PHASE_DISCHARGING, 30_000, 60_000, None, false, None);
        // Rate survives only because it is re-derived; the reset cleared history.
        assert_eq!(c.last_capacity(), None);
    }

    #[test]
    fn warmup_charging_only() {
        let (some, secs) = EtaCalculator::warmup_estimate(PHASE_CHARGING, 50);
        assert!(some);
        assert!((secs - 4500.0).abs() < 1.0);
        let (some, _) = EtaCalculator::warmup_estimate(PHASE_DISCHARGING, 50);
        assert!(!some);
    }

    #[test]
    fn parses_raw_battery_info_correctly() {
        let mut buf = [0u8; 36];
        buf[0..4].copy_from_slice(&0x40000000u32.to_le_bytes()); // capabilities
        buf[4] = 1; // technology
        buf[8..12].copy_from_slice(b"LION"); // chemistry
        buf[12..16].copy_from_slice(&60000u32.to_le_bytes()); // designed capacity: 60,000 mWh
        buf[16..20].copy_from_slice(&54000u32.to_le_bytes()); // full charged capacity: 54,000 mWh (10% wear)
        buf[32..36].copy_from_slice(&125u32.to_le_bytes()); // cycle count: 125

        let info = parse_battery_info(&buf).expect("Should parse valid 36-byte battery info");
        assert_eq!(info.capabilities, 0x40000000);
        assert_eq!(info.technology, 1);
        assert_eq!(info.designed_capacity, 60000);
        assert_eq!(info.full_charged_capacity, 54000);
        assert_eq!(info.cycle_count, 125);
        assert!((info.wear_level_percent - 10.0).abs() < 0.001);
    }

    #[test]
    fn parses_raw_battery_info_zero_capacity_and_clamping() {
        let mut buf = [0u8; 36];
        // Zero designed capacity
        let info = parse_battery_info(&buf).expect("Should parse zero capacity");
        assert_eq!(info.designed_capacity, 0);
        assert_eq!(info.wear_level_percent, 0.0);

        // Full capacity greater than designed capacity (wear clamped to 0.0)
        buf[12..16].copy_from_slice(&50000u32.to_le_bytes());
        buf[16..20].copy_from_slice(&55000u32.to_le_bytes());
        let info = parse_battery_info(&buf).expect("Should parse overflow capacity");
        assert_eq!(info.wear_level_percent, 0.0);

        // Completely dead battery (full capacity 0 -> 100% wear)
        buf[16..20].copy_from_slice(&0u32.to_le_bytes());
        let info = parse_battery_info(&buf).expect("Should parse dead battery");
        assert_eq!(info.wear_level_percent, 100.0);
    }

    #[test]
    fn parse_battery_info_rejects_truncated() {
        assert!(parse_battery_info(&[]).is_none());
        assert!(parse_battery_info(&[0u8; 35]).is_none());
    }
}
