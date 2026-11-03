using System;

namespace PredatorControlApp
{
    public sealed record ThermalSafetyStatus(
        double CpuTemp,
        double GpuTemp,
        bool IsCpuCritical,
        bool IsGpuCritical,
        bool AntiThrottleActive,
        int CpuSpeedFloor,
        int GpuSpeedFloor,
        int? CpuPowerCapWatts = null
    )
    {
        public bool IsActive => AntiThrottleActive;
    }

    public interface IThermalSafetyService
    {
        ThermalLimits CpuLimits { get; }
        ThermalLimits GpuLimits { get; }
        bool AntiThrottleEnabled { get; set; }
        AntiThrottleResult EvaluateCpu(double cpuTemp);
        AntiThrottleResult EvaluateGpu(double gpuTemp);
        ThermalSafetyStatus EvaluateStatus(double cpuTemp, double gpuTemp);
        (int SafeCpuSpeed, int SafeGpuSpeed) ApplyAntiThrottle(double cpuTemp, double gpuTemp, int targetCpuSpeed, int targetGpuSpeed);
        int? CalculateCpuPowerCap(double cpuTemp, int targetPl1Watts, int minPowerWatts = 15, int? baseTdpWatts = null);
        void UpdateCpuPowerSafety(double cpuTemp, CpuPowerLimitController? powerController, int targetPl1Watts, int minPowerWatts = 15, int? baseTdpWatts = null);
    }
}
