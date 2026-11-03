using System;
using System.Management;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PredatorControlApp
{
    public sealed record BatteryTelemetrySnapshot(
        int RemainingCapacity,
        int FullChargedCapacity,
        double ChargeRate,
        double DischargeRate,
        int BatteryPercent,
        bool IsCharging,
        bool IsPowerOnline);

    [SupportedOSPlatform("windows")]
    public sealed class BatteryCalibrationEtaCalculator
    {
        public const double DefaultAlpha = 0.2;

        private readonly object _syncLock = new();
        private double? _smoothedRate;
        private int? _lastPercent;
        private DateTime? _lastPercentTimestamp;
        private double? _smoothedPercentPerMinute;
        private int? _lastCapacity;
        private DateTime? _lastCapacityTimestamp;
        private int _lastPhase = -1;

        private double _alpha = DefaultAlpha;
        public double Alpha
        {
            get { lock (_syncLock) return _alpha; }
            set { lock (_syncLock) _alpha = double.IsNaN(value) || double.IsInfinity(value) ? DefaultAlpha : Math.Clamp(value, 0.01, 1.0); }
        }

        public double? SmoothedRate
        {
            get { lock (_syncLock) return _smoothedRate; }
            set
            {
                lock (_syncLock)
                {
                    _smoothedRate = (value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value > 0)
                        ? value.Value
                        : null;
                }
            }
        }

        public double? SmoothedPercentPerMinute
        {
            get { lock (_syncLock) return _smoothedPercentPerMinute; }
        }

        public int? LastPercent
        {
            get { lock (_syncLock) return _lastPercent; }
        }

        public int? LastCapacity
        {
            get { lock (_syncLock) return _lastCapacity; }
        }

        public BatteryCalibrationEtaCalculator(double alpha = DefaultAlpha)
        {
            Alpha = Math.Clamp(alpha, 0.01, 1.0);
        }

        public void Reset()
        {
            PredatorNativeMigrations.BatteryEtaReset();
            lock (_syncLock)
            {
                _smoothedRate = null;
                _lastPercent = null;
                _lastPercentTimestamp = null;
                _smoothedPercentPerMinute = null;
                _lastCapacity = null;
                _lastCapacityTimestamp = null;
                _lastPhase = -1;
            }
        }

        public void UpdateDirectRate(double rawRate)
        {
            rawRate = Math.Abs(rawRate);
            if (rawRate <= 0 || double.IsNaN(rawRate) || double.IsInfinity(rawRate) || rawRate >= 0xFFFFFFF || rawRate > 500000)
                return;

            lock (_syncLock)
            {
                if (!_smoothedRate.HasValue)
                {
                    _smoothedRate = rawRate;
                }
                else
                {
                    _smoothedRate = (Alpha * rawRate) + ((1.0 - Alpha) * _smoothedRate.Value);
                }
            }
        }

        public void UpdateCapacity(int capacity, DateTime timestamp)
        {
            if (capacity <= 0)
                return;

            lock (_syncLock)
            {
                if (_lastCapacityTimestamp.HasValue && timestamp < _lastCapacityTimestamp.Value)
                {
                    _lastCapacity = capacity;
                    _lastCapacityTimestamp = timestamp;
                    return;
                }

                if (_lastCapacity.HasValue && _lastCapacityTimestamp.HasValue && timestamp > _lastCapacityTimestamp.Value)
                {
                    double seconds = (timestamp - _lastCapacityTimestamp.Value).TotalSeconds;
                    // Detect sleep/hibernate or clock skew gap: if more than 5 minutes elapsed, re-baseline without rate corruption
                    if (seconds > 300.0)
                    {
                        _lastCapacity = capacity;
                        _lastCapacityTimestamp = timestamp;
                        _smoothedPercentPerMinute = null;
                        return;
                    }

                    // Sudden AC plug/unplug check:
                    // In Phase 1 (charging), capacity should increase. If it decreased, charger was unplugged!
                    // In Phase 2 (discharging), capacity should decrease. If it increased, charger was plugged in!
                    if (_lastPhase == 1 && capacity < _lastCapacity.Value)
                    {
                        _lastCapacity = capacity;
                        _lastCapacityTimestamp = timestamp;
                        return;
                    }
                    if (_lastPhase == 2 && capacity > _lastCapacity.Value)
                    {
                        _lastCapacity = capacity;
                        _lastCapacityTimestamp = timestamp;
                        return;
                    }

                    if (seconds >= 6.0 && capacity != _lastCapacity.Value)
                    {
                        double delta = Math.Abs(capacity - _lastCapacity.Value);
                        double hours = seconds / 3600.0;
                        if (hours > 0)
                        {
                            double rateMw = delta / hours;

                            // Plausible laptop battery power rate: 500 mW to 350,000 mW (0.5W to 350W)
                            if (!double.IsNaN(rateMw) && !double.IsInfinity(rateMw) && rateMw >= 500 && rateMw <= 350000)
                            {
                                UpdateDirectRate(rateMw);
                            }
                        }
                        _lastCapacity = capacity;
                        _lastCapacityTimestamp = timestamp;
                    }
                }
                else if (!_lastCapacity.HasValue)
                {
                    _lastCapacity = capacity;
                    _lastCapacityTimestamp = timestamp;
                }
            }
        }

        public void UpdatePercentage(int percent, DateTime timestamp)
        {
            if (percent < 0 || percent > 100)
                return;

            lock (_syncLock)
            {
                if (_lastPercentTimestamp.HasValue && timestamp < _lastPercentTimestamp.Value)
                {
                    _lastPercent = percent;
                    _lastPercentTimestamp = timestamp;
                    return;
                }

                if (_lastPercent.HasValue && _lastPercentTimestamp.HasValue && timestamp > _lastPercentTimestamp.Value)
                {
                    double minutes = (timestamp - _lastPercentTimestamp.Value).TotalMinutes;
                    // Detect sleep/hibernate or clock skew gap: if more than 5 minutes elapsed, re-baseline without rate corruption
                    if (minutes > 120.0)
                    {
                        _lastPercent = percent;
                        _lastPercentTimestamp = timestamp;
                        return;
                    }

                    // Sudden AC plug/unplug check:
                    // In Phase 1 (charging), percent should increase. If it decreased, charger was unplugged!
                    // In Phase 2 (discharging), percent should decrease. If it increased, charger was plugged in!
                    if (_lastPhase == 1 && percent < _lastPercent.Value)
                    {
                        _lastPercent = percent;
                        _lastPercentTimestamp = timestamp;
                        return;
                    }
                    if (_lastPhase == 2 && percent > _lastPercent.Value)
                    {
                        _lastPercent = percent;
                        _lastPercentTimestamp = timestamp;
                        return;
                    }

                    if (minutes >= 0.05 && percent != _lastPercent.Value)
                    {
                        double delta = Math.Abs(percent - _lastPercent.Value);
                        double ratePerMin = delta / minutes;

                        if (!double.IsNaN(ratePerMin) && !double.IsInfinity(ratePerMin))
                        {
                            // Realistically, battery percentage change per minute cannot exceed 30.0%/min (full cycle in ~3.3 min)
                            ratePerMin = Math.Min(ratePerMin, 30.0);

                            if (ratePerMin > 0)
                            {
                                if (!_smoothedPercentPerMinute.HasValue)
                                {
                                    _smoothedPercentPerMinute = ratePerMin;
                                }
                                else
                                {
                                    _smoothedPercentPerMinute = (Alpha * ratePerMin) + ((1.0 - Alpha) * _smoothedPercentPerMinute.Value);
                                }

                                _lastPercent = percent;
                                _lastPercentTimestamp = timestamp;
                            }
                        }
                    }
                }
                else if (!_lastPercent.HasValue)
                {
                    _lastPercent = percent;
                    _lastPercentTimestamp = timestamp;
                }
            }
        }

        public static TimeSpan? GetWarmupFallbackEta(int phase, int batteryPercent)
        {
            batteryPercent = Math.Clamp(batteryPercent, 0, 100);

            if (phase == 1) // Charging to Full (100%)
            {
                int remainingPct = Math.Clamp(100 - batteryPercent, 0, 100);
                if (remainingPct <= 0) return TimeSpan.Zero;
                // Standard laptop calibration charging rate: ~1.5 minutes per 1% (~35-40W on 60Wh pack)
                return TimeSpan.FromMinutes(remainingPct * 1.5);
            }
            else if (phase == 2) // Discharging to Empty (0%)
            {
                // If Windows power subsystem has an active discharge estimate in seconds, use it
                try
                {
                    int sec = SystemInformation.PowerStatus.BatteryLifeRemaining;
                    if (sec > 0 && sec <= 86400)
                    {
                        return TimeSpan.FromSeconds(sec);
                    }
                }
                catch { }

                int remainingPct = Math.Clamp(batteryPercent, 0, 100);
                if (remainingPct <= 0) return TimeSpan.Zero;
                // Standard calibration discharging rate with wake lock and display awake: ~2.0 minutes per 1% (~20-25W load)
                return TimeSpan.FromMinutes(remainingPct * 2.0);
            }

            return null;
        }

        public TimeSpan? CalculateEta(int phase, int remainingCapacity, int fullCapacity, double? directRate = null, bool allowWarmupFallback = false, int? batteryPercent = null)
        {
            // The native core owns the smoothing, phase handling and plausibility
            // clamps. It returns 1 when it could not produce an estimate, in which
            // case the managed path below handles warm-up, re-baselining and the
            // "nothing known yet" case.
            double? rateForNative = directRate ?? _smoothedRate;
            int nativeResult = PredatorNativeMigrations.BatteryEtaCalculate(
                phase, remainingCapacity, fullCapacity, rateForNative,
                allowWarmupFallback, batteryPercent, out double nativeSeconds);
            if (nativeResult == 0)
            {
                return TimeSpan.FromSeconds(nativeSeconds);
            }

            lock (_syncLock)
            {
                if (_lastPhase != -1 && _lastPhase != phase)
                {
                    Reset();
                }
                _lastPhase = phase;

                remainingCapacity = Math.Max(0, remainingCapacity);
                if (fullCapacity > 0 && remainingCapacity > fullCapacity)
                {
                    remainingCapacity = fullCapacity;
                }

                if (remainingCapacity <= 0 && batteryPercent.HasValue && batteryPercent.Value > 0 && batteryPercent.Value <= 100 && fullCapacity > 100)
                {
                    remainingCapacity = (int)Math.Round(fullCapacity * (Math.Clamp(batteryPercent.Value, 0, 100) / 100.0));
                }

                if (directRate.HasValue && directRate.Value > 0 && directRate.Value < 500000 && directRate.Value != 0xFFFFFFFF)
                {
                    UpdateDirectRate(directRate.Value);
                }

                // If smoothed direct rate is not yet available but percentage rate is known with realistic capacity, infer direct rate
                if (!_smoothedRate.HasValue && _smoothedPercentPerMinute.HasValue && _smoothedPercentPerMinute.Value > 0.0001 && fullCapacity > 100)
                {
                    double inferredMw = (_smoothedPercentPerMinute.Value * 60.0 / 100.0) * fullCapacity;
                    if (inferredMw >= 500 && inferredMw <= 350000)
                    {
                        _smoothedRate = inferredMw;
                    }
                }

                // Phase 1: ChargingToFull
                if (phase == 1)
                {
                    if (batteryPercent.HasValue && batteryPercent.Value >= 100)
                    {
                        return TimeSpan.Zero;
                    }

                    if (fullCapacity > 100 && remainingCapacity >= fullCapacity)
                    {
                        return TimeSpan.Zero;
                    }

                    if (fullCapacity > 100 && fullCapacity > remainingCapacity && _smoothedRate.HasValue &&
                        !double.IsNaN(_smoothedRate.Value) && !double.IsInfinity(_smoothedRate.Value) && _smoothedRate.Value > 0.0001)
                    {
                        double delta = Math.Max(0, fullCapacity - remainingCapacity);
                        if (delta <= 0) return TimeSpan.Zero;
                        double hours = delta / _smoothedRate.Value;
                        if (!double.IsNaN(hours) && !double.IsInfinity(hours) && hours >= 0 && hours <= 72)
                        {
                            return TimeSpan.FromHours(hours);
                        }
                    }

                    // Fallback to percentage-over-time
                    if (_smoothedPercentPerMinute.HasValue && !double.IsNaN(_smoothedPercentPerMinute.Value) &&
                        !double.IsInfinity(_smoothedPercentPerMinute.Value) && _smoothedPercentPerMinute.Value > 0.0001)
                    {
                        double currentPct = fullCapacity > 0 ? (remainingCapacity * 100.0 / fullCapacity) : (_lastPercent ?? 0);
                        if (currentPct >= 100.0)
                        {
                            return TimeSpan.Zero;
                        }
                        double remainingPct = Math.Clamp(100.0 - currentPct, 0.0, 100.0);
                        if (remainingPct <= 0) return TimeSpan.Zero;
                        double mins = remainingPct / _smoothedPercentPerMinute.Value;
                        if (!double.IsNaN(mins) && !double.IsInfinity(mins) && mins >= 0 && mins <= 4320)
                        {
                            return TimeSpan.FromMinutes(mins);
                        }
                    }
                }
                // Phase 2: DischargingToEmpty
                else if (phase == 2)
                {
                    if (batteryPercent.HasValue && batteryPercent.Value <= 0)
                    {
                        return TimeSpan.Zero;
                    }

                    if (remainingCapacity <= 0)
                    {
                        return TimeSpan.Zero;
                    }

                    if (fullCapacity > 100 && remainingCapacity > 0 && _smoothedRate.HasValue &&
                        !double.IsNaN(_smoothedRate.Value) && !double.IsInfinity(_smoothedRate.Value) && _smoothedRate.Value > 0.0001)
                    {
                        double hours = (double)remainingCapacity / _smoothedRate.Value;
                        if (!double.IsNaN(hours) && !double.IsInfinity(hours) && hours >= 0 && hours <= 72)
                        {
                            return TimeSpan.FromHours(hours);
                        }
                    }

                    // Fallback to percentage-over-time
                    if (_smoothedPercentPerMinute.HasValue && !double.IsNaN(_smoothedPercentPerMinute.Value) &&
                        !double.IsInfinity(_smoothedPercentPerMinute.Value) && _smoothedPercentPerMinute.Value > 0.0001)
                    {
                        double currentPct = fullCapacity > 0 ? (remainingCapacity * 100.0 / fullCapacity) : (_lastPercent ?? 0);
                        if (currentPct <= 0.0)
                        {
                            return TimeSpan.Zero;
                        }
                        currentPct = Math.Clamp(currentPct, 0.0, 100.0);
                        double mins = currentPct / _smoothedPercentPerMinute.Value;
                        if (!double.IsNaN(mins) && !double.IsInfinity(mins) && mins >= 0 && mins <= 4320)
                        {
                            return TimeSpan.FromMinutes(mins);
                        }
                    }
                }

                if (allowWarmupFallback)
                {
                    int pct = batteryPercent ?? (fullCapacity > 0 ? (int)Math.Round(remainingCapacity * 100.0 / fullCapacity) : (_lastPercent ?? 50));
                    return GetWarmupFallbackEta(phase, pct);
                }

                return null;
            }
        }

        public static BatteryTelemetrySnapshot QueryTelemetry()
        {
            int remaining = 0;
            int full = 0;
            double chargeRate = 0;
            double dischargeRate = 0;
            bool isCharging = false;
            bool isOnline = false;
            int percent = 0;
            bool noBattery = false;

            try
            {
                var p = SystemInformation.PowerStatus;
                isOnline = p.PowerLineStatus == PowerLineStatus.Online;
                isCharging = (p.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                noBattery = (p.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0 ||
                            p.BatteryLifePercent > 1.0f ||
                            p.BatteryLifePercent < 0f ||
                            float.IsNaN(p.BatteryLifePercent);

                if (!noBattery)
                {
                    percent = (int)Math.Round(Math.Clamp(p.BatteryLifePercent, 0f, 1f) * 100.0);
                }
                else
                {
                    percent = -1;
                }
            }
            catch { }

            if (noBattery)
            {
                return new BatteryTelemetrySnapshot(0, 0, 0, 0, -1, false, isOnline);
            }

            try
            {
                using var searcherStatus = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM BatteryStatus WHERE Active = TRUE");
                using var collStatus = searcherStatus.Get();
                foreach (ManagementObject mo in collStatus)
                {
                    using (mo)
                    {
                        remaining = ParseWmiInt(mo["RemainingCapacity"]);
                        chargeRate = ParseWmiRate(mo["ChargeRate"]);
                        dischargeRate = ParseWmiRate(mo["DischargeRate"]);
                        if (mo["Charging"] is bool ch) isCharging = ch;
                        if (mo["PowerOnline"] is bool po) isOnline = po;
                    }
                    break;
                }
            }
            catch { }

            try
            {
                using var searcherFull = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM BatteryFullChargedCapacity WHERE Active = TRUE");
                using var collFull = searcherFull.Get();
                foreach (ManagementObject mo in collFull)
                {
                    using (mo)
                    {
                        full = ParseWmiInt(mo["FullChargedCapacity"]);
                    }
                    break;
                }
            }
            catch { }

            if (full <= 0)
            {
                try
                {
                    using var searcherBat = new ManagementObjectSearcher(@"root\cimv2", "SELECT * FROM Win32_Battery");
                    using var collBat = searcherBat.Get();
                    foreach (ManagementObject mo in collBat)
                    {
                        using (mo)
                        {
                            int dc = ParseWmiInt(mo["DesignCapacity"]);
                            if (dc > 0)
                            {
                                full = dc;
                                remaining = (int)Math.Round(dc * (percent / 100.0));
                            }
                        }
                        break;
                    }
                }
                catch { }
            }

            if (remaining <= 0 && full > 0 && percent > 0)
            {
                remaining = (int)Math.Round(full * (percent / 100.0));
            }

            // If WMI capacity is unavailable, scale arbitrary 100 units based on PowerStatus
            if (full <= 0 && percent >= 0)
            {
                full = 100;
                remaining = percent;
            }

            return new BatteryTelemetrySnapshot(remaining, full, chargeRate, dischargeRate, percent, isCharging, isOnline);
        }

        private static int ParseWmiInt(object? val)
        {
            if (val == null) return 0;
            try
            {
                long v = Convert.ToInt64(val);
                return (v > 0 && v < 0xFFFFFFF) ? (int)v : 0;
            }
            catch { return 0; }
        }

        private static double ParseWmiRate(object? val)
        {
            if (val == null) return 0;
            try
            {
                double d = Math.Abs(Convert.ToDouble(val));
                return (d > 0 && d < 500000 && d < 0xFFFFFFF) ? d : 0;
            }
            catch { return 0; }
        }
    }
}
