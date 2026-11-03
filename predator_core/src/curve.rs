//! Fan curve piecewise linear interpolation, temperature damping, and hysteresis follower.
//!
//! Designed for predator_core with zero heap allocations in critical evaluation paths.

/// Control point on a temperature-to-fan-speed curve.
#[repr(C)]
#[derive(Copy, Clone, Debug, Default, PartialEq, Eq, Hash)]
pub struct Point {
    /// Temperature in degrees Celsius.
    pub temp: i32,
    /// Target fan speed percentage (10% to 100%).
    pub speed: i32,
}

/// Backwards compatibility alias for `Point`.
pub type ControlPoint = Point;
pub type CurveHandle = FanCurveFollower;

// Hysteresis configuration boundaries matching Predator Control specification
pub const MIN_HYSTERESIS: i32 = 0;
pub const MAX_HYSTERESIS: i32 = 15;
pub const DEFAULT_HYSTERESIS: i32 = 2;

// Legacy alias constants
pub const MIN_HYSTERESIS_DEGREES: i32 = MIN_HYSTERESIS;
pub const MAX_HYSTERESIS_DEGREES: i32 = MAX_HYSTERESIS;
pub const DEFAULT_HYSTERESIS_DEGREES: i32 = DEFAULT_HYSTERESIS;

/// Minimum speed delta required to trigger an EC fan update outside boundary values.
pub const MIN_CHANGE_PERCENT: i32 = 2;

/// Minimum operational fan speed percentage enforced on Predator hardware.
pub const MIN_FAN_SPEED: i32 = 10;
/// Maximum operational fan speed percentage.
pub const MAX_FAN_SPEED: i32 = 100;
/// Default fallback fan speed when curve points are missing or empty.
pub const DEFAULT_FALLBACK_SPEED: i32 = 50;

/// Maximum control points handled without heap allocation if sorting is needed.
const STACK_SORT_CAPACITY: usize = 64;

/// Fan channel identifier for independent state tracking.
#[repr(C)]
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
pub enum FanChannel {
    Cpu = 0,
    Gpu = 1,
    System = 2,
}

/// Zero-allocation fixed-size rolling damping buffer for temperature smoothing.
///
/// Implements a ring buffer without heap queue allocation.
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct RollingDampingBuffer<const N: usize = 3> {
    samples: [i32; N],
    count: usize,
    head: usize,
}

impl<const N: usize> Default for RollingDampingBuffer<N> {
    fn default() -> Self {
        Self::new()
    }
}

impl<const N: usize> RollingDampingBuffer<N> {
    /// Create a new empty rolling damping buffer.
    pub const fn new() -> Self {
        assert!(N > 0, "Damping buffer capacity must be greater than zero");
        Self {
            samples: [0; N],
            count: 0,
            head: 0,
        }
    }

    /// Reset and clear the buffer contents.
    pub fn reset(&mut self) {
        self.samples = [0; N];
        self.count = 0;
        self.head = 0;
    }

    /// Push an effective temperature sample clamped to `1..=130°C` and return the smoothed integer average.
    pub fn push(&mut self, temp: i32) -> i32 {
        if N == 0 {
            return temp.clamp(1, 130);
        }
        let clamped = temp.clamp(1, 130);
        self.samples[self.head] = clamped;
        self.head = (self.head + 1) % N;
        if self.count < N {
            self.count += 1;
        }

        if self.count == 0 {
            return clamped;
        }
        let sum: i32 = self.samples.iter().take(self.count).sum();
        ((sum as f64) / (self.count as f64)).round() as i32
    }

    /// Current rolling average if at least one sample is present.
    pub fn average(&self) -> Option<i32> {
        if self.count == 0 {
            None
        } else {
            let sum: i32 = self.samples.iter().take(self.count).sum();
            Some(((sum as f64) / (self.count as f64)).round() as i32)
        }
    }

    /// Number of samples currently recorded in the buffer.
    pub const fn len(&self) -> usize {
        self.count
    }

    /// Whether the buffer has no samples.
    pub const fn is_empty(&self) -> bool {
        self.count == 0
    }

    /// Whether the buffer has filled its capacity.
    pub const fn is_full(&self) -> bool {
        self.count == N
    }
}

/// Zero-allocation piecewise linear interpolation over control points.
///
/// Guaranteed zero heap allocation. Evaluates `temp` against arbitrary sorted points.
/// Clamps output to `[MIN_FAN_SPEED, MAX_FAN_SPEED]`.
pub fn interpolate_curve(points: &[Point], temp: i32) -> i32 {
    if points.is_empty() {
        return DEFAULT_FALLBACK_SPEED;
    }

    // Fast-path: check if slice is already sorted by temp (standard case)
    let is_sorted = points.windows(2).all(|w| w[0].temp <= w[1].temp);

    if is_sorted {
        interpolate_sorted_slice(points, temp)
    } else if points.len() <= STACK_SORT_CAPACITY {
        // Zero-allocation stack sort for small unsorted inputs
        let mut stack_buf = [Point::default(); STACK_SORT_CAPACITY];
        let len = points.len();
        stack_buf[..len].copy_from_slice(points);
        stack_buf[..len].sort_by_key(|p| p.temp);
        interpolate_sorted_slice(&stack_buf[..len], temp)
    } else {
        let mut sorted = points.to_vec();
        sorted.sort_by_key(|p| p.temp);
        interpolate_sorted_slice(&sorted, temp)
    }
}

/// Helper performing linear interpolation across a verified sorted slice without any heap allocation.
fn interpolate_sorted_slice(pts: &[Point], temp: i32) -> i32 {
    if pts.is_empty() {
        return DEFAULT_FALLBACK_SPEED;
    }
    if pts.len() == 1 {
        return pts[0].speed.clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
    }

    if temp < pts[0].temp {
        return pts[0].speed.clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
    }

    let last = pts.len() - 1;
    if temp >= pts[last].temp {
        return pts[last].speed.clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
    }

    // Division-by-zero guard for identical endpoints (x1 == x0)
    if pts[0].temp == pts[last].temp {
        return pts.iter().map(|p| p.speed).max().unwrap_or(pts[0].speed).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
    }

    for window in pts.windows(2) {
        let p1 = window[0];
        let p2 = window[1];
        if temp >= p1.temp && temp < p2.temp {
            let t1 = p1.temp as f64;
            let t2 = p2.temp as f64;
            let span = t2 - t1;
            if span <= f64::EPSILON {
                return p1.speed.max(p2.speed).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
            }
            let t = ((temp as f64) - t1) / span;
            let s1 = p1.speed as f64;
            let s2 = p2.speed as f64;
            let interp = s1 + t * (s2 - s1);
            if interp.is_nan() || interp.is_infinite() {
                return p1.speed.max(p2.speed).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
            }
            return (interp.round_ties_even() as i32).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
        }
    }

    pts[last].speed.clamp(MIN_FAN_SPEED, MAX_FAN_SPEED)
}

/// Helper performing linear interpolation across a slice with f64 temperature.
pub fn interpolate_curve_f64(pts: &[Point], temp: f64) -> i32 {
    if pts.is_empty() {
        return DEFAULT_FALLBACK_SPEED;
    }
    if temp.is_nan() {
        return DEFAULT_FALLBACK_SPEED;
    }
    if pts.len() == 1 {
        return pts[0].speed.clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
    }
    let is_sorted = pts.windows(2).all(|w| w[0].temp <= w[1].temp);
    if is_sorted {
        interpolate_sorted_slice_f64(pts, temp)
    } else if pts.len() <= STACK_SORT_CAPACITY {
        let mut stack_buf = [Point::default(); STACK_SORT_CAPACITY];
        let len = pts.len();
        stack_buf[..len].copy_from_slice(pts);
        stack_buf[..len].sort_by_key(|p| p.temp);
        interpolate_sorted_slice_f64(&stack_buf[..len], temp)
    } else {
        let mut sorted = pts.to_vec();
        sorted.sort_by_key(|p| p.temp);
        interpolate_sorted_slice_f64(&sorted, temp)
    }
}

fn interpolate_sorted_slice_f64(pts: &[Point], temp: f64) -> i32 {
    if pts.is_empty() {
        return DEFAULT_FALLBACK_SPEED;
    }
    if pts.len() == 1 {
        return pts[0].speed.clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
    }
    if temp.is_nan() {
        return DEFAULT_FALLBACK_SPEED;
    }
    if temp < pts[0].temp as f64 {
        return pts[0].speed.clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
    }
    let last = pts.len() - 1;
    if temp >= pts[last].temp as f64 {
        return pts[last].speed.clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
    }

    // Division-by-zero guard for identical endpoints (x1 == x0)
    if (pts[last].temp as f64 - pts[0].temp as f64).abs() <= f64::EPSILON {
        return pts.iter().map(|p| p.speed).max().unwrap_or(pts[0].speed).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
    }

    for window in pts.windows(2) {
        let p1 = window[0];
        let p2 = window[1];
        let t1 = p1.temp as f64;
        let t2 = p2.temp as f64;
        if temp >= t1 && temp < t2 {
            let span = t2 - t1;
            if span <= f64::EPSILON {
                return p1.speed.max(p2.speed).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
            }
            let t = (temp - t1) / span;
            let s1 = p1.speed as f64;
            let s2 = p2.speed as f64;
            let interp = s1 + t * (s2 - s1);
            if interp.is_nan() || interp.is_infinite() {
                return p1.speed.max(p2.speed).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
            }
            return (interp.round_ties_even() as i32).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
        }
    }
    pts[last].speed.clamp(MIN_FAN_SPEED, MAX_FAN_SPEED)
}


/// Curve follower for a single fan channel tracking temperature hysteresis and deadband.
#[derive(Debug, Clone, PartialEq)]

pub struct FanCurveFollower {
    /// Hysteresis deadband in degrees Celsius (1 to 15, default 3).
    pub hysteresis_degrees: i32,
    /// Last commanded fan speed.
    pub current_speed: Option<i32>,
    /// Peak temperature held during cooling hysteresis.
    pub peak_temperature: Option<f64>,
    /// 3-sample rolling average buffer without heap queue allocation.
    pub damping: RollingDampingBuffer<3>,
}

impl Default for FanCurveFollower {
    fn default() -> Self {
        Self::new(DEFAULT_HYSTERESIS)
    }
}

impl FanCurveFollower {
    /// Create a new follower with configured hysteresis in degrees Celsius.
    pub fn new(hysteresis_degrees: i32) -> Self {
        Self {
            hysteresis_degrees: hysteresis_degrees.clamp(MIN_HYSTERESIS, MAX_HYSTERESIS),
            current_speed: None,
            peak_temperature: None,
            damping: RollingDampingBuffer::new(),
        }
    }

    /// Reset internal state, clearing peak hold, damping queue, and previous speed.
    pub fn reset(&mut self) {
        self.current_speed = None;
        self.peak_temperature = None;
        self.damping.reset();
    }

    /// Set hysteresis degrees, clamped to valid boundaries.
    pub fn set_hysteresis(&mut self, hysteresis: i32) {
        self.hysteresis_degrees = hysteresis.clamp(MIN_HYSTERESIS, MAX_HYSTERESIS);
    }

    /// Push temperature sample into the 3-sample rolling damping buffer.
    pub fn push_and_smooth_temp(&mut self, temp: i32) -> i32 {
        self.damping.push(temp)
    }

    /// Forwarding helper matching legacy naming.
    pub fn interpolate(points: &[Point], temp: i32) -> i32 {
        interpolate_curve(points, temp)
    }

    /// Update fan curve calculation with rolling damping, hysteresis deadband, and safety floor enforcement.
    ///
    /// # Parameters
    /// - `raw_temp`: Raw sensor temperature reading in Celsius.
    /// - `points`: Piecewise curve points (temp °C -> speed %).
    /// - `safety_floor`: Active thermal guard floor (e.g. from ThermalDecision).
    ///
    /// # Returns
    /// Commanded fan speed percentage clamped between 10% and 100%.
    pub fn update(&mut self, raw_temp: i32, points: &[Point], safety_floor: i32) -> i32 {
        let clamped_floor = safety_floor.clamp(0, 100);

        // Filter invalid sensor readings
        if raw_temp <= 0 {
            let safe = self.current_speed.unwrap_or(DEFAULT_FALLBACK_SPEED).max(clamped_floor).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
            self.current_speed = Some(safe);
            return safe;
        }

        let smoothed_temp = self.push_and_smooth_temp(raw_temp);
        let temp_f64 = smoothed_temp as f64;

        // Temperature-based hysteresis:
        // Heating (temp >= peak) tracks immediately.
        // Cooling holds peak until temperature drops past hysteresis degrees.
        let temp_to_use = if self.hysteresis_degrees <= 0 {
            self.peak_temperature = Some(temp_f64);
            temp_f64
        } else {
            match self.peak_temperature {
                None => {
                    self.peak_temperature = Some(temp_f64);
                    temp_f64
                }
                Some(peak) => {
                    if temp_f64 >= peak {
                        self.peak_temperature = Some(temp_f64);
                        temp_f64
                    } else if (peak - temp_f64) >= (self.hysteresis_degrees as f64) {
                        self.peak_temperature = Some(temp_f64);
                        temp_f64
                    } else {
                        peak
                    }
                }
            }
        };

        let mut calculated = interpolate_curve_f64(points, temp_to_use);

        // Emergency safety override: when safety floor demands 100%, immediately bypass deadband hold
        if clamped_floor >= 100 {
            self.peak_temperature = Some(temp_f64);
            self.current_speed = Some(100);
            return 100;
        }

        // Speed deadband hold: prevent oscillation when change is below MIN_CHANGE_PERCENT (2%),
        // unless operating at curve boundaries (<= 10% or >= 100%) or when safety floor is actively overriding.
        let is_safety_overriding = clamped_floor > calculated;
        if self.hysteresis_degrees > 0 && !is_safety_overriding {
            if let Some(cur) = self.current_speed {
                let within_deadband = (calculated - cur).abs() < MIN_CHANGE_PERCENT;
                let is_boundary = calculated <= MIN_FAN_SPEED || calculated >= MAX_FAN_SPEED;

                if within_deadband && !is_boundary {
                    calculated = cur;
                }
            }
        }

        // Enforce safety floor: final speed cannot fall below active safety floor
        let final_speed = calculated.max(clamped_floor).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
        self.current_speed = Some(final_speed);
        final_speed
    }

    /// Update fan curve calculation with precise floating-point temperature without extra damping.
    pub fn update_f64(&mut self, temp: f64, points: &[Point], safety_floor: i32) -> i32 {
        let clamped_floor = safety_floor.clamp(0, 100);

        // Filter invalid sensor readings (<= 0, NaN, Inf) matching integer update
        if temp.is_nan() || temp.is_infinite() || temp <= 0.0 {
            let safe = self.current_speed.unwrap_or(DEFAULT_FALLBACK_SPEED).max(clamped_floor).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
            self.current_speed = Some(safe);
            return safe;
        }

        let temp_f64 = temp.clamp(1.0, 150.0);

        let temp_to_use = if self.hysteresis_degrees <= 0 {
            self.peak_temperature = Some(temp_f64);
            temp_f64
        } else {
            match self.peak_temperature {
                None => {
                    self.peak_temperature = Some(temp_f64);
                    temp_f64
                }
                Some(peak) => {
                    if temp_f64 >= peak {
                        self.peak_temperature = Some(temp_f64);
                        temp_f64
                    } else if (peak - temp_f64) >= (self.hysteresis_degrees as f64) {
                        self.peak_temperature = Some(temp_f64);
                        temp_f64
                    } else {
                        peak
                    }
                }
            }
        };

        let mut calculated = interpolate_curve_f64(points, temp_to_use);

        if clamped_floor >= 100 {
            self.peak_temperature = Some(temp_f64);
            self.current_speed = Some(100);
            return 100;
        }

        let is_safety_overriding = clamped_floor > calculated;
        if self.hysteresis_degrees > 0 && !is_safety_overriding {
            if let Some(cur) = self.current_speed {
                let within_deadband = (calculated - cur).abs() < MIN_CHANGE_PERCENT;
                let is_boundary = calculated <= MIN_FAN_SPEED || calculated >= MAX_FAN_SPEED;

                if within_deadband && !is_boundary {
                    calculated = cur;
                }
            }
        }

        let final_speed = calculated.max(clamped_floor).clamp(MIN_FAN_SPEED, MAX_FAN_SPEED);
        self.current_speed = Some(final_speed);
        final_speed
    }
}

/// Multi-channel manager managing independent CPU, GPU, and System fan curve followers.
#[derive(Debug, Clone, PartialEq)]
pub struct FanCurveManager {
    pub cpu: FanCurveFollower,
    pub gpu: FanCurveFollower,
    pub system: FanCurveFollower,
    pub gpu_asleep: bool,
}

impl Default for FanCurveManager {
    fn default() -> Self {
        Self::new(DEFAULT_HYSTERESIS)
    }
}

impl FanCurveManager {
    /// Create manager for CPU, GPU, and System fan channels with configured hysteresis.
    pub fn new(hysteresis: i32) -> Self {
        Self {
            cpu: FanCurveFollower::new(hysteresis),
            gpu: FanCurveFollower::new(hysteresis),
            system: FanCurveFollower::new(hysteresis),
            gpu_asleep: false,
        }
    }

    /// Reset all fan followers and clear damping states.
    pub fn reset_all(&mut self) {
        self.cpu.reset();
        self.gpu.reset();
        self.system.reset();
    }

    /// Reset a specific fan channel.
    pub fn reset_channel(&mut self, channel: FanChannel) {
        match channel {
            FanChannel::Cpu => self.cpu.reset(),
            FanChannel::Gpu => self.gpu.reset(),
            FanChannel::System => self.system.reset(),
        }
    }

    /// Set hysteresis degrees across all fan channels.
    pub fn set_hysteresis(&mut self, hysteresis: i32) {
        self.cpu.set_hysteresis(hysteresis);
        self.gpu.set_hysteresis(hysteresis);
        self.system.set_hysteresis(hysteresis);
    }

    /// Set whether the discrete GPU is in low-power sleep / D3Cold.
    pub fn set_gpu_asleep(&mut self, asleep: bool) {
        self.gpu_asleep = asleep;
        if asleep {
            self.gpu.reset();
        }
    }

    /// Update fan calculation for a given channel.
    ///
    /// If GPU is asleep, returns 0 (hardware off).
    pub fn update_channel(
        &mut self,
        channel: FanChannel,
        temp: i32,
        curve: &[Point],
        safety_floor: i32,
    ) -> i32 {
        match channel {
            FanChannel::Cpu => self.cpu.update(temp, curve, safety_floor),
            FanChannel::Gpu => {
                if self.gpu_asleep || temp <= 0 {
                    self.gpu.reset();
                    0
                } else {
                    self.gpu.update(temp, curve, safety_floor)
                }
            }
            FanChannel::System => self.system.update(temp, curve, safety_floor),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_zero_allocation_interpolation() {
        let pts = [
            Point { temp: 40, speed: 20 },
            Point { temp: 60, speed: 50 },
            Point { temp: 80, speed: 80 },
            Point { temp: 95, speed: 100 },
        ];

        // Empty points fallback
        assert_eq!(interpolate_curve(&[], 50), 50);

        // Single point
        assert_eq!(interpolate_curve(&[Point { temp: 50, speed: 45 }], 30), 45);

        // Below first point clamped
        assert_eq!(interpolate_curve(&pts, 20), 20);
        // Above last point clamped
        assert_eq!(interpolate_curve(&pts, 110), 100);

        // Exact points
        assert_eq!(interpolate_curve(&pts, 40), 20);
        assert_eq!(interpolate_curve(&pts, 60), 50);
        assert_eq!(interpolate_curve(&pts, 80), 80);
        assert_eq!(interpolate_curve(&pts, 95), 100);

        // Midpoint: 40 -> 60 is 20 -> 50, temp 50 -> speed 35
        assert_eq!(interpolate_curve(&pts, 50), 35);
        // 60 -> 80 is 50 -> 80, temp 70 -> speed 65
        assert_eq!(interpolate_curve(&pts, 70), 65);
    }

    #[test]
    fn test_unsorted_slice_fallback() {
        let unsorted = [
            Point { temp: 95, speed: 100 },
            Point { temp: 40, speed: 20 },
            Point { temp: 80, speed: 80 },
            Point { temp: 60, speed: 50 },
        ];
        assert_eq!(interpolate_curve(&unsorted, 50), 35);
        assert_eq!(interpolate_curve(&unsorted, 30), 20);
        assert_eq!(interpolate_curve(&unsorted, 100), 100);
    }

    #[test]
    fn test_rolling_damping_buffer() {
        let mut buf = RollingDampingBuffer::<3>::new();
        assert!(buf.is_empty());
        assert_eq!(buf.average(), None);

        // Push 1st sample
        assert_eq!(buf.push(50), 50);
        assert_eq!(buf.len(), 1);

        // Push 2nd sample
        assert_eq!(buf.push(60), 55); // (50 + 60) / 2 = 55
        assert_eq!(buf.len(), 2);

        // Push 3rd sample
        assert_eq!(buf.push(70), 60); // (50 + 60 + 70) / 3 = 60
        assert!(buf.is_full());

        // Push 4th sample (evicts 50)
        assert_eq!(buf.push(80), 70); // (60 + 70 + 80) / 3 = 70
        assert_eq!(buf.len(), 3);

        buf.reset();
        assert!(buf.is_empty());
    }

    #[test]
    fn test_hysteresis_tracking_and_safety_floor() {
        let mut follower = FanCurveFollower::new(3);
        let pts = [
            Point { temp: 40, speed: 20 },
            Point { temp: 60, speed: 50 },
            Point { temp: 80, speed: 80 },
            Point { temp: 100, speed: 100 },
        ];

        // 1. Initial sample heating
        let speed = follower.update(60, &pts, 0);
        assert_eq!(speed, 50);

        // 2. Further heating to 80 -> immediately rises
        let _speed_rise = follower.update(80, &pts, 0);
        assert_eq!(follower.damping.average(), Some(70)); // avg(60, 80) = 70 -> speed 65

        // Let damping settle at 80
        follower.update(80, &pts, 0);
        let settled_speed = follower.update(80, &pts, 0);
        assert_eq!(settled_speed, 80);

        // 3. Cooling slightly (79°C, delta 1 < hysteresis 3) -> holds peak speed
        let speed = follower.update(79, &pts, 0);
        assert_eq!(speed, 80);

        // 4. Safety floor override
        let speed = follower.update(50, &pts, 75);
        assert_eq!(speed, 75); // Floor overrides lower curve speed

        // 5. Emergency 100% floor bypass
        let speed = follower.update(40, &pts, 100);
        assert_eq!(speed, 100);
    }

    #[test]
    fn test_multi_channel_manager() {
        let mut mgr = FanCurveManager::new(3);
        let pts = [Point { temp: 40, speed: 20 }, Point { temp: 80, speed: 80 }];

        let cpu_speed = mgr.update_channel(FanChannel::Cpu, 60, &pts, 10);
        assert!(cpu_speed >= 20);

        // GPU asleep returns 0
        mgr.set_gpu_asleep(true);
        assert_eq!(mgr.update_channel(FanChannel::Gpu, 60, &pts, 10), 0);

        // Wake GPU
        mgr.set_gpu_asleep(false);
        let gpu_speed = mgr.update_channel(FanChannel::Gpu, 60, &pts, 10);
        assert!(gpu_speed >= 20);
    }

    #[test]
    fn test_extreme_integer_temperatures_and_speeds_do_not_overflow() {
        let pts = [
            Point { temp: i32::MIN, speed: i32::MIN },
            Point { temp: 0, speed: 50 },
            Point { temp: i32::MAX, speed: i32::MAX },
        ];
        // At midpoints, f64 arithmetic ensures no integer overflow in debug mode
        let s1 = interpolate_curve(&pts, -100);
        assert!((MIN_FAN_SPEED..=MAX_FAN_SPEED).contains(&s1));
        let s2 = interpolate_curve(&pts, 100);
        assert!((MIN_FAN_SPEED..=MAX_FAN_SPEED).contains(&s2));
        let s_min = interpolate_curve(&pts, i32::MIN);
        assert_eq!(s_min, MIN_FAN_SPEED);
        let s_max = interpolate_curve(&pts, i32::MAX);
        assert_eq!(s_max, MAX_FAN_SPEED);
    }
}
