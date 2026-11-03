//! Multi-stage thermal coordinator, EMA smoothing, hysteresis latching, and CPU power capping.
//!
//! Designed for predator_core with zero heap allocations in critical evaluation paths.

/// Thermal evaluation decision output with zero heap allocation.
#[repr(C)]
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct ThermalDecision {
    /// Active thermal severity stage (0 = Normal, 1 = Warm, 2 = Ceiling, 3 = Critical).
    pub stage: i32,
    /// Whether the thermal guard is actively intervening (fan floor or power cap applied).
    pub active: bool,
    /// Whether temperature reached critical limit.
    pub critical: bool,
    /// Enforced minimum fan speed percentage for CPU fan.
    pub cpu_fan_floor: i32,
    /// Enforced minimum fan speed percentage for GPU fan.
    pub gpu_fan_floor: i32,
    /// Dynamic CPU package PL1 power cap in Watts (-1 if no cap applied).
    pub cpu_power_cap_watts: i32,
    /// Filtered smoothed CPU temperature reading in Celsius.
    pub cpu_temp: f64,
    /// Filtered smoothed GPU temperature reading in Celsius.
    pub gpu_temp: f64,
}

/// Backwards compatibility alias for `ThermalDecision`.
pub type NativeThermalDecision = ThermalDecision;
pub type CoordinatorHandle = ThermalCoordinator;

impl Default for ThermalDecision {
    fn default() -> Self {
        Self::idle()
    }
}

impl ThermalDecision {
    /// Initial idle state when thermal guard is inactive.
    pub const fn idle() -> Self {
        Self {
            stage: 0,
            active: false,
            critical: false,
            cpu_fan_floor: 0,
            gpu_fan_floor: 0,
            cpu_power_cap_watts: -1,
            cpu_temp: 0.0,
            gpu_temp: 0.0,
        }
    }

    /// Human-readable label for the current thermal stage.
    pub const fn stage_label(&self) -> &'static str {
        match self.stage {
            3 => "CRITICAL",
            2 => "CEILING",
            1 => "WARM",
            _ => "NORMAL",
        }
    }

    /// Whether an active CPU power limit cap is recommended.
    pub const fn has_power_cap(&self) -> bool {
        self.cpu_power_cap_watts > 0
    }

    /// Optional CPU power limit cap in Watts.
    pub const fn power_cap(&self) -> Option<i32> {
        if self.cpu_power_cap_watts > 0 {
            Some(self.cpu_power_cap_watts)
        } else {
            None
        }
    }
}

/// Severity stage for multi-stage thermal coordination.
#[repr(C)]
#[derive(Copy, Clone, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum ThermalStage {
    Normal = 0,
    Warm = 1,
    Ceiling = 2,
    Critical = 3,
}

impl From<i32> for ThermalStage {
    fn from(val: i32) -> Self {
        match val {
            3 => ThermalStage::Critical,
            2 => ThermalStage::Ceiling,
            1 => ThermalStage::Warm,
            _ => ThermalStage::Normal,
        }
    }
}

/// Default Exponential Moving Average smoothing factor.
pub const DEFAULT_EMA_ALPHA: f64 = 0.35;
/// Maximum step percentage increase permitted per evaluation tick to prevent EC spamming.
pub const MAX_FLOOR_STEP_PER_TICK: i32 = 12;
/// Minimum wall-clock interval between two upward fan-floor steps (milliseconds).
pub const MIN_FLOOR_STEP_INTERVAL_MS: u64 = 250;
/// Consecutive invalid sensor samples before the EMA is decayed toward zero.
pub const EMA_MISS_DECAY_THRESHOLD: u32 = 3;
/// Fan floor percentage held while operating within the hysteresis release band.
pub const HYSTERESIS_HOLD_FLOOR_PERCENT: i32 = 15;

/// Configurable Exponential Moving Average filter for temperature telemetry smoothing.
///
/// Smooths instantaneous thermal spikes without heap allocation.
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct EmaFilter {
    alpha: f64,
    value: f64,
    initialized: bool,
}

impl Default for EmaFilter {
    fn default() -> Self {
        Self::new(DEFAULT_EMA_ALPHA)
    }
}

impl EmaFilter {
    /// Create a new EMA filter with configured smoothing factor alpha `(0.0, 1.0]`.
    pub fn new(alpha: f64) -> Self {
        let safe_alpha = if alpha.is_nan() || alpha.is_infinite() || alpha <= 0.0 {
            DEFAULT_EMA_ALPHA
        } else {
            alpha.clamp(0.01, 1.0)
        };
        Self {
            alpha: safe_alpha,
            value: 0.0,
            initialized: false,
        }
    }

    /// Reset filter state to uninitialized zero.
    pub fn reset(&mut self) {
        self.value = 0.0;
        self.initialized = false;
    }

    /// Set alpha smoothing factor.
    pub fn set_alpha(&mut self, alpha: f64) {
        if !alpha.is_nan() && !alpha.is_infinite() && alpha > 0.0 {
            self.alpha = alpha.clamp(0.01, 1.0);
        }
    }

    /// Current alpha factor.
    pub const fn alpha(&self) -> f64 {
        self.alpha
    }

    /// Current smoothed value.
    pub const fn value(&self) -> f64 {
        self.value
    }

    /// Overwrite the smoothed value directly (used by sensor-miss decay).
    pub fn set_value(&mut self, value: f64) {
        if value.is_finite() && value >= 0.0 {
            self.value = value;
            if value > 0.0 {
                self.initialized = true;
            }
        }
    }

    /// Whether at least one valid sample has been incorporated.
    pub const fn is_initialized(&self) -> bool {
        self.initialized
    }

    /// Update filter with a new raw temperature sample.
    ///
    /// Discards NaN, Inf, non-positive, or out-of-range (>150°C) readings.
    /// Cold-start: the first valid sample initializes the filter directly without lag.
    pub fn update(&mut self, sample: f64) -> f64 {
        if sample.is_nan() || sample.is_infinite() || sample <= 0.0 || sample > 150.0 {
            return self.value;
        }

        if !self.initialized || self.value <= 0.0 {
            self.value = sample;
            self.initialized = true;
        } else {
            self.value = (sample * self.alpha) + (self.value * (1.0 - self.alpha));
        }

        self.value
    }
}

/// Thermal threshold limits specifying warn, ceiling, and critical trip points.
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct ThermalLimits {
    pub throttle_threshold_celsius: f64,
    pub ceiling_threshold_celsius: f64,
    pub critical_threshold_celsius: f64,
}

impl ThermalLimits {
    pub const DEFAULT_INTEL_CPU_TJ_MAX: f64 = 100.0;
    pub const DEFAULT_AMD_CPU_TJ_MAX: f64 = 95.0;
    pub const DEFAULT_GPU_LIMIT: f64 = 87.0;

    pub const fn new(throttle: f64, ceiling: f64, critical: f64) -> Self {
        Self {
            throttle_threshold_celsius: throttle,
            ceiling_threshold_celsius: ceiling,
            critical_threshold_celsius: critical,
        }
    }

    pub const fn cpu_default() -> Self {
        Self::new(75.0, 95.0, 100.0)
    }

    pub const fn gpu_default() -> Self {
        Self::new(70.0, 87.0, 90.0)
    }

    /// Calculate dynamic thresholds from CPU TjMax with delta offsets.
    pub fn from_tj_max(tj_max: f64, delta_start: f64, delta_ceil: f64) -> Self {
        if tj_max.is_nan() || tj_max.is_infinite() || tj_max <= 0.0 {
            return Self::cpu_default();
        }
        let d_start = if delta_start.is_nan() || delta_start.is_infinite() || delta_start < 0.0 {
            12.0
        } else {
            delta_start.clamp(1.0, 50.0)
        };
        let d_ceil = if delta_ceil.is_nan() || delta_ceil.is_infinite() || delta_ceil < 0.0 {
            6.0
        } else {
            delta_ceil.clamp(1.0, 30.0)
        };

        let clamped_tj = tj_max.clamp(30.0, 200.0);
        let throttle = (clamped_tj - d_start).clamp(0.0, 198.0);
        let ceiling = (clamped_tj - d_ceil).max(throttle + 1.0).clamp(throttle + 1.0, 199.0);
        let critical = clamped_tj.max(ceiling).clamp(ceiling, 200.0);
        Self::new(throttle, ceiling, critical)
    }

    /// Calculate dynamic thresholds from GPU target temperature.
    pub fn from_gpu_target(gpu_target: f64, delta_start: f64) -> Self {
        if gpu_target.is_nan() || gpu_target.is_infinite() || gpu_target <= 0.0 {
            return Self::gpu_default();
        }
        let d_start = if delta_start.is_nan() || delta_start.is_infinite() || delta_start < 0.0 {
            6.0
        } else {
            delta_start.clamp(1.0, 50.0)
        };

        let clamped_target = gpu_target.clamp(30.0, 197.0);
        let throttle = (clamped_target - d_start).clamp(0.0, 198.0);
        let ceiling = clamped_target.max(throttle + 1.0).clamp(throttle + 1.0, 199.0);
        let critical = (clamped_target + 3.0).max(ceiling).clamp(ceiling, 200.0);
        Self::new(throttle, ceiling, critical)
    }
}

/// User and system configuration profile for thermal safety coordination.
#[derive(Clone, Debug, PartialEq)]
pub struct ThermalSafetyProfile {
    pub enabled: bool,
    pub cpu_warn: f64,
    pub cpu_ceiling: f64,
    pub cpu_critical: f64,
    pub gpu_warn: f64,
    pub gpu_ceiling: f64,
    pub gpu_critical: f64,
    pub hysteresis: f64,
    pub max_fan_floor: i32,
    pub cpu_power_cap_watts: i32,
    pub min_power_cap_watts: i32,
    pub critical_fans_full_speed: bool,
    pub ema_alpha: f64,
}

impl Default for ThermalSafetyProfile {
    fn default() -> Self {
        Self {
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
            ema_alpha: DEFAULT_EMA_ALPHA,
        }
    }
}

impl ThermalSafetyProfile {
    /// Normalize profile parameters ensuring safety invariants.
    pub fn normalize(mut self) -> Self {
        let (cw, cc, cx) = Self::normalize_triplet(self.cpu_warn, self.cpu_ceiling, self.cpu_critical, 100.0);
        let (gw, gc, gx) = Self::normalize_triplet(self.gpu_warn, self.gpu_ceiling, self.gpu_critical, 95.0);

        self.cpu_warn = cw;
        self.cpu_ceiling = cc;
        self.cpu_critical = cx;
        self.gpu_warn = gw;
        self.gpu_ceiling = gc;
        self.gpu_critical = gx;
        self.hysteresis = if self.hysteresis.is_finite() { self.hysteresis } else { 3.0 }.clamp(0.0, 15.0);
        self.max_fan_floor = self.max_fan_floor.clamp(40, 100);
        self.cpu_power_cap_watts = self.cpu_power_cap_watts.clamp(5, 250);
        self.min_power_cap_watts = self.min_power_cap_watts.clamp(5, 250);
        self.ema_alpha = if self.ema_alpha.is_finite() { self.ema_alpha } else { DEFAULT_EMA_ALPHA }.clamp(0.01, 1.0);
        self
    }

    fn normalize_triplet(warn: f64, ceiling: f64, critical: f64, hard_max: f64) -> (f64, f64, f64) {
        let w = (if warn.is_finite() { warn } else { 75.0 }).clamp(30.0, hard_max - 2.0);
        let c = (if ceiling.is_finite() { ceiling } else { 95.0 }).clamp(w + 1.0, hard_max);
        let x = (if critical.is_finite() { critical } else { hard_max }).clamp(c, hard_max.max(c));
        (w, c, x)
    }

    pub fn to_cpu_limits(&self) -> ThermalLimits {
        let (warn, ceiling, critical) = Self::normalize_triplet(self.cpu_warn, self.cpu_ceiling, self.cpu_critical, 100.0);
        ThermalLimits::new(warn, ceiling, critical)
    }

    pub fn to_gpu_limits(&self) -> ThermalLimits {
        let (warn, ceiling, critical) = Self::normalize_triplet(self.gpu_warn, self.gpu_ceiling, self.gpu_critical, 95.0);
        ThermalLimits::new(warn, ceiling, critical)
    }
}

/// Multi-stage thermal coordinator maintaining EMA smoothing, hysteresis latching,
/// rate-limited fan floors, and proportional CPU power limits.
#[derive(Debug, Clone, PartialEq)]
pub struct ThermalCoordinator {
    pub profile: ThermalSafetyProfile,
    pub cpu_ema: EmaFilter,
    pub gpu_ema: EmaFilter,
    pub cpu_latched: bool,
    pub gpu_latched: bool,
    pub cpu_floor: i32,
    pub gpu_floor: i32,
    pub last_stage: i32,
    /// Consecutive invalid CPU sensor samples (drives EMA decay on disconnect).
    pub cpu_misses: u32,
    /// Consecutive invalid GPU sensor samples.
    pub gpu_misses: u32,
    /// Monotonic timestamp of the last accepted upward CPU floor step (`None` = never).
    pub last_cpu_step_ms: Option<u64>,
    /// Monotonic timestamp of the last accepted upward GPU floor step.
    pub last_gpu_step_ms: Option<u64>,
}

impl Default for ThermalCoordinator {
    fn default() -> Self {
        Self::new(ThermalSafetyProfile::default())
    }
}

impl ThermalCoordinator {
    /// Create a new coordinator with given safety profile.
    pub fn new(profile: ThermalSafetyProfile) -> Self {
        let normalized = profile.normalize();
        let alpha = normalized.ema_alpha;
        Self {
            profile: normalized,
            cpu_ema: EmaFilter::new(alpha),
            gpu_ema: EmaFilter::new(alpha),
            cpu_latched: false,
            gpu_latched: false,
            cpu_floor: 0,
            gpu_floor: 0,
            last_stage: 0,
            cpu_misses: 0,
            gpu_misses: 0,
            last_cpu_step_ms: None,
            last_gpu_step_ms: None,
        }
    }

    /// Reset internal state, latches, floor history, and filters.
    pub fn reset(&mut self) {
        self.cpu_ema.reset();
        self.gpu_ema.reset();
        self.cpu_latched = false;
        self.gpu_latched = false;
        self.cpu_floor = 0;
        self.gpu_floor = 0;
        self.last_stage = 0;
        self.cpu_misses = 0;
        self.gpu_misses = 0;
        self.last_cpu_step_ms = None;
        self.last_gpu_step_ms = None;
    }

    /// Replace the active safety profile (normalized on assignment).
    pub fn set_profile(&mut self, profile: ThermalSafetyProfile) {
        let normalized = profile.normalize();
        self.cpu_ema.set_alpha(normalized.ema_alpha);
        self.gpu_ema.set_alpha(normalized.ema_alpha);
        self.profile = normalized;
        if !self.profile.enabled {
            self.reset();
        }
    }

    /// Configure EMA alpha for both CPU and GPU channels.
    pub fn set_ema_alpha(&mut self, alpha: f64) {
        if !alpha.is_finite() || alpha <= 0.0 { return; }
        self.profile.ema_alpha = alpha.clamp(0.01, 1.0);
        self.cpu_ema.set_alpha(alpha);
        self.gpu_ema.set_alpha(alpha);
    }

    /// Rate-limit fan floor increases to avoid spamming EC firmware while allowing instant cooling drops.
    ///
    /// Upward steps are limited to [`MAX_FLOOR_STEP_PER_TICK`] percentage points and are further
    /// gated to at most one step every [`MIN_FLOOR_STEP_INTERVAL_MS`] milliseconds. Downward
    /// transitions and the 100 % critical trip are always applied immediately.
    fn rate_limit_floor(
        current: i32,
        target: i32,
        last_step_ms: &mut Option<u64>,
        now_ms: u64,
    ) -> i32 {
        if target == current {
            return target;
        }
        // Critical trip (100% floor) is never throttled.
        if target >= 100 {
            return 100;
        }
        if target <= current {
            return target;
        }
        if let Some(last) = *last_step_ms {
            if now_ms.saturating_sub(last) < MIN_FLOOR_STEP_INTERVAL_MS {
                return current;
            }
        }
        *last_step_ms = Some(now_ms);
        target.min(current + MAX_FLOOR_STEP_PER_TICK)
    }

    /// Compute target fan floor with hysteresis latching.
    fn compute_floor(
        temp: f64,
        warn: f64,
        ceiling: f64,
        critical: f64,
        max_floor: i32,
        critical_full_speed: bool,
        latched: &mut bool,
        hysteresis: f64,
    ) -> i32 {
        if temp <= 0.0 {
            *latched = false;
            return 0;
        }

        // Hysteresis latching:
        // Latch triggers when temperature reaches or exceeds warn threshold.
        // Releases only when temperature drops below (warn - hysteresis).
        if *latched {
            if temp < (warn - hysteresis) {
                *latched = false;
                return 0;
            }
        } else if temp >= warn {
            *latched = true;
        }

        if !*latched {
            return 0;
        }

        // Critical ceiling reached
        if temp >= critical {
            return if critical_full_speed { 100 } else { max_floor };
        }

        // Upper ceiling reached
        if temp >= ceiling {
            return max_floor;
        }

        // Inside the hysteresis release band below warn
        if temp < warn {
            return (max_floor * HYSTERESIS_HOLD_FLOOR_PERCENT / 100).max(1);
        }

        // Proportional floor between warn and ceiling
        let span = (ceiling - warn).max(0.1);
        let progress = ((temp - warn) / span).clamp(0.0, 1.0);
        let interpolated = (HYSTERESIS_HOLD_FLOOR_PERCENT as f64)
            + progress * ((max_floor - HYSTERESIS_HOLD_FLOOR_PERCENT) as f64);
        (interpolated.round() as i32).clamp(0, max_floor)
    }

    /// Calculate proportional CPU PL1 power cap.
    ///
    /// Smoothly interpolates from `target_pl1` down to `ceiling_cap` as temperature
    /// rises from `cpu_warn` to `cpu_ceiling`, and drops to `floor` at `cpu_critical`.
    pub fn compute_power_cap(
        cpu_temp: f64,
        profile: &ThermalSafetyProfile,
        target_pl1_watts: i32,
        min_power_watts: i32,
        base_tdp_watts: i32,
    ) -> i32 {
        if cpu_temp <= 0.0 || cpu_temp < profile.cpu_warn {
            return -1;
        }

        let target = if target_pl1_watts > 0 {
            target_pl1_watts
        } else {
            min_power_watts.max(base_tdp_watts)
        };
        let configured_floor = 5.max(min_power_watts.min(profile.min_power_cap_watts));
        let floor = target.min(configured_floor);
        let ceiling_cap = profile.cpu_power_cap_watts.clamp(floor, target);

        if cpu_temp >= profile.cpu_critical {
            return floor;
        }
        if cpu_temp >= profile.cpu_ceiling {
            return ceiling_cap;
        }

        let span = (profile.cpu_ceiling - profile.cpu_warn).max(0.1);
        let progress = ((cpu_temp - profile.cpu_warn) / span).clamp(0.0, 1.0);
        let interpolated = (target as f64) - progress * ((target - ceiling_cap) as f64);
        (interpolated.round() as i32).clamp(floor, target)
    }

    /// Evaluate current telemetry against safety thresholds.
    ///
    /// # Parameters
    /// - `cpu_raw`: Raw CPU temperature in Celsius (or <= 0 if unavailable).
    /// - `gpu_raw`: Raw GPU temperature in Celsius (or <= 0 if asleep/unavailable).
    /// - `target_pl1`: Desired PL1 operating limit in Watts (e.g. 45-115W).
    /// - `min_power`: Hardware floor power limit in Watts (e.g. 15-25W).
    /// - `base_tdp`: Processor specification base TDP in Watts.
    ///
    /// # Returns
    /// Struct `ThermalDecision` containing stage, floors, and power cap with zero allocation.
    pub fn evaluate(
        &mut self,
        cpu_raw: f64,
        gpu_raw: f64,
        target_pl1: i32,
        min_power: i32,
        base_tdp: i32,
    ) -> ThermalDecision {
        let now = monotonic_ms();
        self.evaluate_at(cpu_raw, gpu_raw, target_pl1, min_power, base_tdp, now)
    }

    /// Time-injected evaluation used by deterministic tests and the FFI shim.
    pub fn evaluate_at(
        &mut self,
        cpu_raw: f64,
        gpu_raw: f64,
        target_pl1: i32,
        min_power: i32,
        base_tdp: i32,
        now_ms: u64,
    ) -> ThermalDecision {
        if !self.profile.enabled {
            self.cpu_latched = false;
            self.gpu_latched = false;
            self.cpu_floor = 0;
            self.gpu_floor = 0;
            self.last_stage = 0;
            self.cpu_misses = 0;
            self.gpu_misses = 0;
            return ThermalDecision {
                stage: 0,
                active: false,
                critical: false,
                cpu_fan_floor: 0,
                gpu_fan_floor: 0,
                cpu_power_cap_watts: -1,
                cpu_temp: if cpu_raw.is_finite() && cpu_raw > 0.0 { cpu_raw } else { 0.0 },
                gpu_temp: if gpu_raw.is_finite() && gpu_raw > 0.0 { gpu_raw } else { 0.0 },
            };
        }

        let cpu = Self::update_channel(&mut self.cpu_ema, &mut self.cpu_misses, cpu_raw);
        let gpu = Self::update_channel(&mut self.gpu_ema, &mut self.gpu_misses, gpu_raw);

        let cpu_target_floor = Self::compute_floor(
            cpu,
            self.profile.cpu_warn,
            self.profile.cpu_ceiling,
            self.profile.cpu_critical,
            self.profile.max_fan_floor,
            self.profile.critical_fans_full_speed,
            &mut self.cpu_latched,
            self.profile.hysteresis,
        );

        let gpu_target_floor = Self::compute_floor(
            gpu,
            self.profile.gpu_warn,
            self.profile.gpu_ceiling,
            self.profile.gpu_critical,
            self.profile.max_fan_floor,
            self.profile.critical_fans_full_speed,
            &mut self.gpu_latched,
            self.profile.hysteresis,
        );

        self.cpu_floor = Self::rate_limit_floor(
            self.cpu_floor,
            cpu_target_floor,
            &mut self.last_cpu_step_ms,
            now_ms,
        );
        self.gpu_floor = Self::rate_limit_floor(
            self.gpu_floor,
            gpu_target_floor,
            &mut self.last_gpu_step_ms,
            now_ms,
        );

        let cpu_critical = cpu > 0.0 && cpu >= self.profile.cpu_critical;
        let gpu_critical = gpu > 0.0 && gpu >= self.profile.gpu_critical;
        let is_critical = cpu_critical || gpu_critical;

        let cpu_ceiling = cpu > 0.0 && cpu >= self.profile.cpu_ceiling;
        let gpu_ceiling = gpu > 0.0 && gpu >= self.profile.gpu_ceiling;

        let cpu_warn = cpu > 0.0 && cpu >= self.profile.cpu_warn;
        let gpu_warn = gpu > 0.0 && gpu >= self.profile.gpu_warn;

        let active = cpu_warn || gpu_warn || self.cpu_floor > 0 || self.gpu_floor > 0;

        let stage = if is_critical {
            3
        } else if cpu_ceiling || gpu_ceiling {
            2
        } else if active {
            1
        } else {
            0
        };

        let cap = Self::compute_power_cap(cpu, &self.profile, target_pl1, min_power, base_tdp);
        self.last_stage = stage;

        ThermalDecision {
            stage,
            active,
            critical: is_critical,
            cpu_fan_floor: self.cpu_floor,
            gpu_fan_floor: self.gpu_floor,
            cpu_power_cap_watts: cap,
            cpu_temp: (cpu * 10.0).round() / 10.0,
            gpu_temp: (gpu * 10.0).round() / 10.0,
        }
    }

    /// Update one sensor channel, decaying the EMA toward zero after a run of invalid samples.
    ///
    /// A single bad reading is ignored; three or more consecutive invalid samples mean the
    /// sensor is gone, so the filtered value is halved each tick until it reaches zero. Without
    /// this a latched fan floor would persist forever after a sensor disconnect.
    fn update_channel(ema: &mut EmaFilter, misses: &mut u32, sample: f64) -> f64 {
        let valid = sample.is_finite() && sample > 0.0 && sample <= 150.0;
        if !valid {
            *misses = misses.saturating_add(1);
            if *misses >= EMA_MISS_DECAY_THRESHOLD && ema.value() > 0.0 {
                let decayed = ema.value() * 0.5;
                ema.set_value(if decayed < 1.0 { 0.0 } else { decayed });
            }
            return ema.value();
        }
        *misses = 0;
        ema.update(sample)
    }
}

/// Monotonic milliseconds since this process first asked for the clock.
///
/// Used only for fan-floor step pacing; it never leaves the native module.
pub fn monotonic_ms() -> u64 {
    use std::sync::OnceLock;
    use std::time::Instant;
    static START: OnceLock<Instant> = OnceLock::new();
    START.get_or_init(Instant::now).elapsed().as_millis() as u64
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn non_finite_profile_values_use_managed_defaults() {
        let profile = ThermalSafetyProfile {
            cpu_warn: f64::NAN, cpu_ceiling: f64::INFINITY, cpu_critical: f64::NAN,
            gpu_warn: f64::NEG_INFINITY, gpu_ceiling: f64::NAN, gpu_critical: f64::INFINITY,
            hysteresis: f64::NAN, ema_alpha: f64::NAN, ..ThermalSafetyProfile::default()
        }.normalize();
        assert_eq!((profile.cpu_warn, profile.cpu_ceiling, profile.cpu_critical), (75.0, 95.0, 100.0));
        assert_eq!((profile.gpu_warn, profile.gpu_ceiling, profile.gpu_critical), (75.0, 95.0, 95.0));
        assert_eq!(profile.hysteresis, 3.0);
        assert_eq!(profile.ema_alpha, DEFAULT_EMA_ALPHA);
    }

    #[test]
    fn test_ema_filter_convergence_and_rejection() {
        let mut ema = EmaFilter::new(0.35);
        assert!(!ema.is_initialized());

        // Cold-start initializes immediately without lag
        assert_eq!(ema.update(80.0), 80.0);
        assert!(ema.is_initialized());

        // Step up
        let step = ema.update(90.0);
        // (90 * 0.35) + (80 * 0.65) = 31.5 + 52.0 = 83.5
        assert!((step - 83.5).abs() < 1e-4);

        // Discard invalid samples
        assert_eq!(ema.update(f64::NAN), step);
        assert_eq!(ema.update(f64::INFINITY), step);
        assert_eq!(ema.update(-5.0), step);
        assert_eq!(ema.update(200.0), step);
    }

    #[test]
    fn test_multi_stage_transitions() {
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
            ema_alpha: 1.0, // Instant response for unit testing
        };

        let mut coord = ThermalCoordinator::new(profile);

        // 1. Normal stage
        let d = coord.evaluate(50.0, 50.0, 65, 15, 45);
        assert_eq!(d.stage, 0);
        assert!(!d.active);
        assert!(!d.critical);
        assert_eq!(d.cpu_power_cap_watts, -1);

        // 2. Warm stage (CPU 80°C >= 75°C warn)
        let d = coord.evaluate(80.0, 50.0, 65, 15, 45);
        assert_eq!(d.stage, 1);
        assert!(d.active);
        assert!(!d.critical);
        assert!(d.cpu_fan_floor > 0);
        assert!(d.cpu_power_cap_watts > 0);

        // 3. Ceiling stage (CPU 96°C >= 95°C ceiling)
        let d = coord.evaluate(96.0, 50.0, 65, 15, 45);
        assert_eq!(d.stage, 2);
        assert!(d.active);
        assert!(!d.critical);
        assert_eq!(d.cpu_power_cap_watts, 45);

        // 4. Critical stage (CPU 101°C >= 100°C critical)
        let d = coord.evaluate(101.0, 50.0, 65, 15, 45);
        assert_eq!(d.stage, 3);
        assert!(d.active);
        assert!(d.critical);
        assert_eq!(d.cpu_fan_floor, 100);
        assert_eq!(d.cpu_power_cap_watts, 15);
    }

    #[test]
    fn test_hysteresis_latching_band() {
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
            ema_alpha: 1.0,
        };

        let mut coord = ThermalCoordinator::new(profile);

        // Trigger latch with 76 C at t=0 -> first floor step is paced to +12 %
        let d = coord.evaluate_at(76.0, 50.0, 65, 15, 45, 0);
        assert!(coord.cpu_latched);
        assert_eq!(d.cpu_fan_floor, 12);

        // Drop to 73 C (below warn 75 C, but above warn - hysteresis 72 C) in the
        // same pacing window: the latch holds and the floor does not decay yet.
        let d = coord.evaluate_at(73.0, 50.0, 65, 15, 45, 100);
        assert!(coord.cpu_latched); // Stays latched!
        assert!(d.cpu_fan_floor >= 12); // Holds hysteresis floor

        // Drop to 71.5 C (< 72 C) -> latch releases, downward moves are immediate
        let d = coord.evaluate_at(71.5, 50.0, 65, 15, 45, 200);
        assert!(!coord.cpu_latched);
        assert_eq!(d.cpu_fan_floor, 0);
        assert_eq!(d.stage, 0);
    }

    #[test]
    fn test_proportional_power_cap_interpolation() {
        let profile = ThermalSafetyProfile {
            cpu_warn: 75.0,
            cpu_ceiling: 95.0,
            cpu_critical: 100.0,
            cpu_power_cap_watts: 45,
            min_power_cap_watts: 15,
            ..Default::default()
        };

        // Below warn: no cap
        assert_eq!(ThermalCoordinator::compute_power_cap(70.0, &profile, 65, 15, 45), -1);

        // At warn (75°C): target watts 65
        assert_eq!(ThermalCoordinator::compute_power_cap(75.0, &profile, 65, 15, 45), 65);

        // Midpoint (85°C): halfway between target 65W and ceiling cap 45W = 55W
        assert_eq!(ThermalCoordinator::compute_power_cap(85.0, &profile, 65, 15, 45), 55);

        // At ceiling (95°C): ceiling cap 45W
        assert_eq!(ThermalCoordinator::compute_power_cap(95.0, &profile, 65, 15, 45), 45);

        // At critical (100°C): floor 15W
        assert_eq!(ThermalCoordinator::compute_power_cap(100.0, &profile, 65, 15, 45), 15);
    }

    #[test]
    fn test_c_abi_layout() {
        assert_eq!(std::mem::size_of::<ThermalDecision>(), 40);
        assert_eq!(std::mem::align_of::<ThermalDecision>(), 8);
    }

    #[test]
    fn test_rate_limit_is_time_gated() {
        let profile = ThermalSafetyProfile {
            ema_alpha: 1.0,
            ..Default::default()
        };
        let mut coord = ThermalCoordinator::new(profile);

        // 80 C -> target 36 %, first upward step is always allowed: 0 -> 12
        let d = coord.evaluate_at(80.0, 50.0, 65, 15, 45, 1_000);
        assert_eq!(d.cpu_fan_floor, 12);

        // 1 ms later the next upward step must be gated by the 250 ms window
        let d = coord.evaluate_at(82.0, 50.0, 65, 15, 45, 1_001);
        assert_eq!(d.cpu_fan_floor, 12);

        // After the pacing window a further +12 % is accepted
        let d = coord.evaluate_at(82.0, 50.0, 65, 15, 45, 1_250);
        assert_eq!(d.cpu_fan_floor, 24);
    }

    #[test]
    fn test_critical_floor_bypasses_rate_limit() {
        let profile = ThermalSafetyProfile {
            ema_alpha: 1.0,
            ..Default::default()
        };
        let mut coord = ThermalCoordinator::new(profile);
        coord.evaluate_at(80.0, 50.0, 65, 15, 45, 0);
        // 101 C is critical: fan floor jumps straight to 100 % in one tick
        let d = coord.evaluate_at(101.0, 50.0, 65, 15, 45, 0);
        assert_eq!(d.cpu_fan_floor, 100);
        assert!(d.critical);
    }

    #[test]
    fn test_sensor_miss_decay_after_three_invalid_samples() {
        let profile = ThermalSafetyProfile {
            ema_alpha: 1.0,
            ..Default::default()
        };
        let mut coord = ThermalCoordinator::new(profile);
        coord.evaluate_at(80.0, 50.0, 65, 15, 45, 0);
        assert_eq!(coord.cpu_ema.value(), 80.0);

        // First two misses only count; the filtered value is retained
        coord.evaluate_at(0.0, 50.0, 65, 15, 45, 0);
        coord.evaluate_at(0.0, 50.0, 65, 15, 45, 0);
        assert_eq!(coord.cpu_ema.value(), 80.0);

        // Third consecutive miss halves the EMA so the guard cannot latch forever
        coord.evaluate_at(0.0, 50.0, 65, 15, 45, 0);
        assert_eq!(coord.cpu_ema.value(), 40.0);
    }

    #[test]
    fn test_zero_temperature_clears_latch() {
        let mut latched = true;
        let floor = ThermalCoordinator::compute_floor(
            0.0, 75.0, 95.0, 100.0, 100, true, &mut latched, 3.0,
        );
        assert_eq!(floor, 0);
        assert!(!latched);
    }

    #[test]
    fn test_set_profile_applies_disabled_reset() {
        let mut coord = ThermalCoordinator::new(ThermalSafetyProfile::default());
        coord.evaluate_at(101.0, 95.0, 65, 15, 45, 0);
        assert_eq!(coord.last_stage, 3);

        coord.set_profile(ThermalSafetyProfile {
            enabled: false,
            ..Default::default()
        });
        assert_eq!(coord.cpu_floor, 0);
        assert_eq!(coord.gpu_floor, 0);
        assert_eq!(coord.last_stage, 0);
        assert!(!coord.cpu_latched);
    }
}
