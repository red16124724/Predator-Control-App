using System;

namespace PredatorControlApp
{
    public sealed class ThermalSafetyService : IThermalSafetyService
    {
        private readonly object _syncLock = new();
        private readonly AntiThrottleSafety _cpuSafety;
        private readonly AntiThrottleSafety _gpuSafety;
        private bool _antiThrottleEnabled = true;

        public ThermalLimits CpuLimits { get { lock (_syncLock) return _cpuSafety.Limits; } }
        public ThermalLimits GpuLimits { get { lock (_syncLock) return _gpuSafety.Limits; } }

        public int TargetPl1Watts { get; set; } = 45;
        public int MinPowerWatts { get; set; } = 15;
        public int? BaseTdpWatts { get; set; } = null;

        public bool AntiThrottleEnabled
        {
            get { lock (_syncLock) return _antiThrottleEnabled; }
            set
            {
                lock (_syncLock)
                {
                    _antiThrottleEnabled = value;
                    _cpuSafety.Enabled = value;
                    _gpuSafety.Enabled = value;
                }
            }
        }

        public ThermalSafetyService(ThermalLimits? cpuLimits = null, ThermalLimits? gpuLimits = null, bool enabled = true)
        {
            _antiThrottleEnabled = enabled;
            _cpuSafety = new AntiThrottleSafety(cpuLimits ?? ThermalLimits.CpuDefault, enabled);
            _gpuSafety = new AntiThrottleSafety(gpuLimits ?? ThermalLimits.GpuDefault, enabled);
        }

        public AntiThrottleResult EvaluateCpu(double cpuTemp)
        {
            lock (_syncLock)
            {
                return _cpuSafety.Evaluate(ChipTarget.Cpu, cpuTemp);
            }
        }

        public AntiThrottleResult EvaluateGpu(double gpuTemp)
        {
            lock (_syncLock)
            {
                return _gpuSafety.Evaluate(ChipTarget.Gpu, gpuTemp);
            }
        }

        public ThermalSafetyStatus EvaluateStatus(double cpuTemp, double gpuTemp)
        {
            lock (_syncLock)
            {
                double safeCpuTemp = double.IsNaN(cpuTemp) || double.IsInfinity(cpuTemp) || cpuTemp < 0 ? 0.0 : cpuTemp;
                double safeGpuTemp = double.IsNaN(gpuTemp) || double.IsInfinity(gpuTemp) || gpuTemp < 0 ? 0.0 : gpuTemp;

                var cpuRes = _cpuSafety.Evaluate(ChipTarget.Cpu, safeCpuTemp);
                var gpuRes = _gpuSafety.Evaluate(ChipTarget.Gpu, safeGpuTemp);

                bool active = _antiThrottleEnabled && (cpuRes.IsActive || gpuRes.IsActive);
                bool cpuCrit = _antiThrottleEnabled && (cpuRes.IsTripwireLocked || (safeCpuTemp > 0 && safeCpuTemp >= CpuLimits.CriticalThresholdCelsius));
                bool gpuCrit = _antiThrottleEnabled && (gpuRes.IsTripwireLocked || (safeGpuTemp > 0 && safeGpuTemp >= GpuLimits.CriticalThresholdCelsius));

                int safeCpuFloor = !_antiThrottleEnabled ? 0 : (cpuCrit ? 100 : cpuRes.TargetFloorPercent);
                int safeGpuFloor = !_antiThrottleEnabled ? 0 : (gpuCrit ? 100 : gpuRes.TargetFloorPercent);
                int targetPl1 = TargetPl1Watts > 0 ? TargetPl1Watts : 45;
                int minW = MinPowerWatts > 0 ? MinPowerWatts : 15;
                int? cpuPowerCap = _antiThrottleEnabled && cpuRes.IsActive
                    ? _cpuSafety.CalculatePowerCapWatts(safeCpuTemp, targetPl1, minW, BaseTdpWatts)
                    : null;

                return new ThermalSafetyStatus(
                    safeCpuTemp,
                    safeGpuTemp,
                    cpuCrit,
                    gpuCrit,
                    active || cpuCrit || gpuCrit,
                    safeCpuFloor,
                    safeGpuFloor,
                    cpuPowerCap
                );
            }
        }

        public (int SafeCpuSpeed, int SafeGpuSpeed) ApplyAntiThrottle(double cpuTemp, double gpuTemp, int targetCpuSpeed, int targetGpuSpeed)
        {
            lock (_syncLock)
            {
                if (!_antiThrottleEnabled)
                    return (targetCpuSpeed, targetGpuSpeed);

                var status = EvaluateStatus(cpuTemp, gpuTemp);
                int safeCpu = Math.Clamp(Math.Max(targetCpuSpeed, status.CpuSpeedFloor), 0, 100);
                int safeGpu = Math.Clamp(Math.Max(targetGpuSpeed, status.GpuSpeedFloor), 0, 100);
                return (safeCpu, safeGpu);
            }
        }

        public int? CalculateCpuPowerCap(double cpuTemp, int targetPl1Watts, int minPowerWatts = 15, int? baseTdpWatts = null)
        {
            lock (_syncLock)
            {
                if (!_antiThrottleEnabled) return null;
                return _cpuSafety.CalculatePowerCapWatts(cpuTemp, targetPl1Watts, minPowerWatts, baseTdpWatts);
            }
        }

        public void UpdateCpuPowerSafety(double cpuTemp, CpuPowerLimitController? powerController, int targetPl1Watts, int minPowerWatts = 15, int? baseTdpWatts = null)
        {
            if (powerController == null) return;
            lock (_syncLock)
            {
                TargetPl1Watts = targetPl1Watts;
                MinPowerWatts = minPowerWatts;
                BaseTdpWatts = baseTdpWatts;
            }
            int? cap = CalculateCpuPowerCap(cpuTemp, targetPl1Watts, minPowerWatts, baseTdpWatts);
            if (cap.HasValue)
            {
                powerController.SetThermalSafetyCap(cap.Value);
            }
            else
            {
                powerController.ClearThermalSafetyCap();
            }
        }
    }
}
