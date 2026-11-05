using System;

namespace PredatorControlApp
{
    public enum ChipTarget
    {
        Cpu,
        Gpu
    }

    public readonly record struct AntiThrottleResult(
        bool IsEnabled,
        bool IsActive,
        bool IsTripwireLocked,
        int TargetFloorPercent,
        double CurrentTemp,
        double ThermalLimit,
        ChipTarget Chip
    );

    public sealed class AntiThrottleSafety
    {
        public const long DefaultTripwireHoldDurationMs = 3000;

        private ThermalLimits _limits = ThermalLimits.CpuDefault;
        private long _tripwireLockedUntilTick;
        private readonly object _syncLock = new();

        public bool Enabled { get; set; } = true;
        public long TripwireHoldDurationMs { get; set; } = DefaultTripwireHoldDurationMs;

        public ThermalLimits Limits
        {
            get => _limits;
            set => _limits = value ?? ThermalLimits.CpuDefault;
        }

        public AntiThrottleSafety() { }

        public AntiThrottleSafety(ThermalLimits limits, bool enabled = true)
        {
            Limits = limits ?? ThermalLimits.CpuDefault;
            Enabled = enabled;
        }

        public void ResetTripwire()
        {
            lock (_syncLock)
            {
                _tripwireLockedUntilTick = 0;
            }
        }

        public int CalculateBoost(double temperature)
        {
            if (!Enabled) return 0;
            if (double.IsNaN(temperature) || double.IsInfinity(temperature) || temperature <= 0) return 0;

            double clampedTemp = Math.Clamp(temperature, 0.0, 350.0);

            if (clampedTemp <= Limits.ThrottleThresholdCelsius)
                return 0;

            if (clampedTemp >= Limits.CeilingThresholdCelsius)
                return 100;

            double span = Limits.CeilingThresholdCelsius - Limits.ThrottleThresholdCelsius;
            if (span <= 0) return 100;

            double progress = (clampedTemp - Limits.ThrottleThresholdCelsius) / span;
            return Math.Clamp((int)Math.Round(progress * 100.0), 0, 100);
        }

        public AntiThrottleResult Evaluate(ChipTarget chip, double temperature)
        {
            lock (_syncLock)
            {
                if (!Enabled)
                {
                    _tripwireLockedUntilTick = 0;
                    return new AntiThrottleResult(
                        IsEnabled: false,
                        IsActive: false,
                        IsTripwireLocked: false,
                        TargetFloorPercent: 0,
                        CurrentTemp: double.IsNaN(temperature) || double.IsInfinity(temperature) ? 0 : temperature,
                        ThermalLimit: Limits.CeilingThresholdCelsius,
                        Chip: chip
                    );
                }

                if (double.IsNaN(temperature) || double.IsInfinity(temperature) || temperature <= 0)
                {
                    long nowTick = Environment.TickCount64;
                    bool lockedDuringBlackout = _tripwireLockedUntilTick > 0 && nowTick < _tripwireLockedUntilTick;
                    return new AntiThrottleResult(
                        IsEnabled: Enabled,
                        IsActive: lockedDuringBlackout,
                        IsTripwireLocked: lockedDuringBlackout,
                        TargetFloorPercent: lockedDuringBlackout ? 100 : 0,
                        CurrentTemp: 0,
                        ThermalLimit: Limits.CeilingThresholdCelsius,
                        Chip: chip
                    );
                }

                if (PredatorNativeCore.IsNativeCoreAvailable)
                {
                    long nowMs = Environment.TickCount64;
                    long hold = _tripwireLockedUntilTick > 0 ? TripwireHoldDurationMs : -TripwireHoldDurationMs;
                    int rc = PredatorNativeCore.AntiThrottleEvaluate(
                        temperature,
                        nowMs,
                        Limits.ThrottleThresholdCelsius,
                        Limits.CeilingThresholdCelsius,
                        Limits.CriticalThresholdCelsius,
                        45,
                        45,
                        15,
                        hold,
                        out int powerCap,
                        out int tripwireLocked);

                    if (rc >= 0)
                    {
                        int nBoost = CalculateBoost(temperature);
                        bool nativeTripwireActive = tripwireLocked != 0;
                        if (nativeTripwireActive)
                        {
                            _tripwireLockedUntilTick = nowMs + TripwireHoldDurationMs;
                        }
                        else if (_tripwireLockedUntilTick > 0)
                        {
                            if (nowMs < _tripwireLockedUntilTick && temperature > Limits.ThrottleThresholdCelsius)
                            {
                                nativeTripwireActive = true;
                            }
                            else
                            {
                                _tripwireLockedUntilTick = 0;
                            }
                        }

                        int nativeFloor = nativeTripwireActive ? 100 : nBoost;
                        bool nativeActive = nativeTripwireActive || (nBoost > 0);

                        return new AntiThrottleResult(
                            IsEnabled: Enabled,
                            IsActive: nativeActive,
                            IsTripwireLocked: nativeTripwireActive,
                            TargetFloorPercent: nativeFloor,
                            CurrentTemp: temperature,
                            ThermalLimit: Limits.CeilingThresholdCelsius,
                            Chip: chip
                        );
                    }
                }

                long now = Environment.TickCount64;
                int boost = CalculateBoost(temperature);
                bool criticalHit = temperature >= Limits.CriticalThresholdCelsius || boost >= 100;

                if (criticalHit)
                {
                    _tripwireLockedUntilTick = now + TripwireHoldDurationMs;
                }

                bool isTripwireActive = criticalHit;
                if (!isTripwireActive && _tripwireLockedUntilTick > 0)
                {
                    if (now < _tripwireLockedUntilTick && temperature > Limits.ThrottleThresholdCelsius)
                    {
                        isTripwireActive = true;
                    }
                    else
                    {
                        _tripwireLockedUntilTick = 0;
                    }
                }

                int floor = isTripwireActive ? 100 : boost;
                bool isActive = isTripwireActive || (boost > 0);

                return new AntiThrottleResult(
                    IsEnabled: Enabled,
                    IsActive: isActive,
                    IsTripwireLocked: isTripwireActive,
                    TargetFloorPercent: floor,
                    CurrentTemp: temperature,
                    ThermalLimit: Limits.CeilingThresholdCelsius,
                    Chip: chip
                );
            }
        }

        /// <summary>
        /// Calculates a transient thermal safety power limit cap in watts based on temperature and power boundaries.
        /// Returns null if temperatures are safe (anti-throttle not active).
        /// </summary>
        public int? CalculatePowerCapWatts(double temperature, int targetPl1Watts, int minPowerWatts = 15, int? baseTdpWatts = null)
        {
            if (!Enabled) return null;

            lock (_syncLock)
            {
                long now = Environment.TickCount64;
                bool isTripwireLocked = _tripwireLockedUntilTick > 0 && now < _tripwireLockedUntilTick;
                int safeTarget = Math.Max(5, targetPl1Watts);
                int minW = Math.Max(5, minPowerWatts);
                if (double.IsNaN(temperature) || double.IsInfinity(temperature) || temperature <= 0)
                    return null;
                isTripwireLocked = isTripwireLocked && temperature > Limits.ThrottleThresholdCelsius;

                if (!isTripwireLocked && temperature <= Limits.ThrottleThresholdCelsius)
                    return null;
                int effectiveMin = Math.Min(minW, safeTarget);
                int tdp = baseTdpWatts ?? Math.Max(effectiveMin, safeTarget);

                if (temperature >= Limits.CriticalThresholdCelsius || isTripwireLocked)
                {
                    return effectiveMin;
                }

                int targetFloor = safeTarget > tdp ? tdp : effectiveMin;
                if (temperature >= Limits.CeilingThresholdCelsius)
                {
                    return Math.Max(effectiveMin, targetFloor);
                }

                double span = Limits.CeilingThresholdCelsius - Limits.ThrottleThresholdCelsius;
                if (span <= 0) return Math.Max(effectiveMin, targetFloor);

                double progress = Math.Clamp((temperature - Limits.ThrottleThresholdCelsius) / span, 0.0, 1.0);
                int reductionRange = Math.Max(0, safeTarget - targetFloor);
                int capped = safeTarget - (int)Math.Round(progress * reductionRange);
                return Math.Clamp(capped, effectiveMin, safeTarget);
            }
        }

        public static AntiThrottleResult EvaluateStatic(
            ChipTarget chip,
            double temperature,
            ThermalLimits limits,
            bool enabled = true)
        {
            var safety = new AntiThrottleSafety(limits, enabled);
            return safety.Evaluate(chip, temperature);
        }
    }
}
