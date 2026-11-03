using System;

namespace PredatorControlApp
{
    public sealed record ThermalLimits
    {
        public const double DefaultIntelCpuTjMax = 100.0;
        public const double DefaultAmdCpuTjMax = 95.0;
        public const double DefaultGpuLimit = 87.0;

        public double ThrottleThresholdCelsius { get; }
        public double CeilingThresholdCelsius { get; }
        public double CriticalThresholdCelsius { get; }

        public ThermalLimits(double throttleThresholdCelsius, double ceilingThresholdCelsius, double criticalThresholdCelsius)
        {
            if (double.IsNaN(throttleThresholdCelsius) || double.IsInfinity(throttleThresholdCelsius) ||
                double.IsNaN(ceilingThresholdCelsius) || double.IsInfinity(ceilingThresholdCelsius) ||
                double.IsNaN(criticalThresholdCelsius) || double.IsInfinity(criticalThresholdCelsius) ||
                throttleThresholdCelsius < 0 || ceilingThresholdCelsius < 0 || criticalThresholdCelsius < 0 ||
                throttleThresholdCelsius > 200 || ceilingThresholdCelsius > 200 || criticalThresholdCelsius > 200)
            {
                throw new ArgumentOutOfRangeException("Thresholds must be finite, non-negative, and <= 200°C.");
            }

            if (throttleThresholdCelsius >= ceilingThresholdCelsius || ceilingThresholdCelsius > criticalThresholdCelsius)
            {
                throw new ArgumentException("Thresholds must satisfy: ThrottleThreshold < CeilingThreshold <= CriticalThreshold.");
            }

            ThrottleThresholdCelsius = throttleThresholdCelsius;
            CeilingThresholdCelsius = ceilingThresholdCelsius;
            CriticalThresholdCelsius = criticalThresholdCelsius;
        }

        public static ThermalLimits CpuDefault => new(75.0, 95.0, 100.0);
        public static ThermalLimits GpuDefault => new(70.0, 87.0, 90.0);

        public static ThermalLimits FromTjMax(double tjMax, double deltaStart = 12.0, double deltaCeil = 6.0)
        {
            if (double.IsNaN(tjMax) || double.IsInfinity(tjMax) || tjMax <= 0) return CpuDefault;
            double dStart = (double.IsNaN(deltaStart) || double.IsInfinity(deltaStart) || deltaStart < 0) ? 12.0 : Math.Clamp(deltaStart, 1.0, 50.0);
            double dCeil = (double.IsNaN(deltaCeil) || double.IsInfinity(deltaCeil) || deltaCeil < 0) ? 6.0 : Math.Clamp(deltaCeil, 1.0, 30.0);

            double clampedTj = Math.Clamp(tjMax, 30.0, 200.0);
            double throttle = Math.Clamp(clampedTj - dStart, 0.0, 198.0);
            double ceiling = Math.Clamp(Math.Max(throttle + 1.0, clampedTj - dCeil), throttle + 1.0, 199.0);
            double critical = Math.Clamp(Math.Max(ceiling, clampedTj), ceiling, 200.0);
            return new ThermalLimits(throttle, ceiling, critical);
        }

        public static ThermalLimits FromGpuTarget(double gpuTarget, double deltaStart = 6.0)
        {
            if (double.IsNaN(gpuTarget) || double.IsInfinity(gpuTarget) || gpuTarget <= 0) return GpuDefault;
            double dStart = (double.IsNaN(deltaStart) || double.IsInfinity(deltaStart) || deltaStart < 0) ? 6.0 : Math.Clamp(deltaStart, 1.0, 50.0);

            double clampedTarget = Math.Clamp(gpuTarget, 30.0, 197.0);
            double throttle = Math.Clamp(clampedTarget - dStart, 0.0, 198.0);
            double ceiling = Math.Clamp(Math.Max(throttle + 1.0, clampedTarget), throttle + 1.0, 199.0);
            double critical = Math.Clamp(Math.Max(ceiling, clampedTarget + 3.0), ceiling, 200.0);
            return new ThermalLimits(throttle, ceiling, critical);
        }
    }
}
