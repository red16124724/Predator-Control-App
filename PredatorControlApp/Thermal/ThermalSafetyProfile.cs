using System;

namespace PredatorControlApp
{
    /// <summary>
    /// User-visible thermal guard configuration. Defaults follow this app's
    /// existing 75/95/100 CPU and 70/87/90 GPU limits, so enabling the guard
    /// changes nothing until the user tunes it.
    /// </summary>
    public sealed record ThermalSafetyProfile
    {
        public bool Enabled { get; init; } = true;

        public double CpuWarnCelsius { get; init; } = 75.0;
        public double CpuCeilingCelsius { get; init; } = 95.0;
        public double CpuCriticalCelsius { get; init; } = 100.0;

        public double GpuWarnCelsius { get; init; } = 70.0;
        public double GpuCeilingCelsius { get; init; } = 87.0;
        public double GpuCriticalCelsius { get; init; } = 90.0;

        /// <summary>Temperature the chip must fall below before the guard releases.</summary>
        public double HysteresisCelsius { get; init; } = 3.0;

        public int MaxFanFloorPercent { get; init; } = 100;

        /// <summary>Watt ceiling applied at the thermal ceiling.</summary>
        public int CpuPowerCapWatts { get; init; } = 45;

        /// <summary>Watt floor applied at the critical trip point.</summary>
        public int MinPowerCapWatts { get; init; } = 15;

        /// <summary>At the critical trip point, drive every fan to 100%.</summary>
        public bool CriticalFansFullSpeed { get; init; } = true;

        /// <summary>
        /// Optional audible/visual alert at the critical trip point. The app
        /// does not shut the machine down; user data is never at risk from a
        /// thermal guard decision.
        /// </summary>
        public bool CriticalAlertEnabled { get; init; } = true;

        public ThermalLimits ToCpuLimits()
        {
            var (warn, ceiling, critical) = NormalizeTriplet(CpuWarnCelsius, CpuCeilingCelsius, CpuCriticalCelsius, 100.0);
            return new ThermalLimits(warn, ceiling, critical);
        }

        public ThermalLimits ToGpuLimits()
        {
            var (warn, ceiling, critical) = NormalizeTriplet(GpuWarnCelsius, GpuCeilingCelsius, GpuCriticalCelsius, 95.0);
            return new ThermalLimits(warn, ceiling, critical);
        }

        public ThermalSafetyProfile Normalize()
        {
            var (cw, cc, cx) = NormalizeTriplet(CpuWarnCelsius, CpuCeilingCelsius, CpuCriticalCelsius, 100.0);
            var (gw, gc, gx) = NormalizeTriplet(GpuWarnCelsius, GpuCeilingCelsius, GpuCriticalCelsius, 95.0);

            return this with
            {
                CpuWarnCelsius = cw,
                CpuCeilingCelsius = cc,
                CpuCriticalCelsius = cx,
                GpuWarnCelsius = gw,
                GpuCeilingCelsius = gc,
                GpuCriticalCelsius = gx,
                HysteresisCelsius = Math.Clamp(Safe(HysteresisCelsius, 3.0), 0.0, 15.0),
                MaxFanFloorPercent = Math.Clamp(MaxFanFloorPercent, 40, 100),
                CpuPowerCapWatts = Math.Clamp(CpuPowerCapWatts, 5, 250),
                MinPowerCapWatts = Math.Clamp(MinPowerCapWatts, 5, 250)
            };
        }

        public static ThermalSafetyProfile Default => new();

        private static (double Warn, double Ceiling, double Critical) NormalizeTriplet(
            double warn, double ceiling, double critical, double hardMax)
        {
            warn = Math.Clamp(Safe(warn, 75.0), 30.0, hardMax - 2.0);
            ceiling = Math.Clamp(Safe(ceiling, 95.0), warn + 1.0, hardMax);
            critical = Math.Clamp(Safe(critical, hardMax), ceiling, Math.Max(ceiling, hardMax));
            return (warn, ceiling, critical);
        }

        private static double Safe(double value, double fallback)
            => double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
    }
}
