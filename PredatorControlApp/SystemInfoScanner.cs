using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    /// <summary>
    /// Thread-safe, non-allocating scanner for gathering telemetry snapshots
    /// across CPU/GPU temperatures, frequencies, fan RPMs, and power metrics.
    /// Resilient to sensor disconnects, hardware timeouts, and WMI drops.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class SystemInfoScanner
    {
        private readonly object _sync = new();
        private int _consecutiveSensorMisses;

        public int ConsecutiveSensorMisses
        {
            get { lock (_sync) return _consecutiveSensorMisses; }
        }

        /// <summary>
        /// Captures a complete hardware telemetry snapshot.
        /// </summary>
        public TelemetrySnapshot Scan(
            WmiController? wmi,
            PdhLoadMonitor? pdhMonitor,
            bool isWindowVisible,
            bool onBattery,
            byte currentPowerMode,
            double cpuFrequencyMhz = 0,
            double gpuFrequencyMhz = 0,
            double cpuPowerWatts = 0,
            double gpuPowerWatts = 0)
        {
            if (wmi == null)
            {
                return TelemetrySnapshot.Empty with { TimestampUtc = DateTime.UtcNow };
            }

            int cpuTemp = 0;
            int gpuTemp = 0;
            int cpuRpm = 0;
            int sysRpm = 0;
            int gpuRpm = 0;
            double? cpuLoad = null;
            double? gpuLoad = null;
            bool gpuAsleep = onBattery;
            bool hasHwMode = false;
            byte probedHwMode = 0;

            try
            {
                cpuTemp = wmi.CpuTemp;
            }
            catch { }

            lock (_sync)
            {
                if (cpuTemp <= 0)
                {
                    _consecutiveSensorMisses++;
                }
                else
                {
                    _consecutiveSensorMisses = 0;
                }
            }

            if (isWindowVisible)
            {
                try { cpuRpm = wmi.CpuFanRpm; } catch { }
                try { sysRpm = wmi.SystemFanRpm; } catch { }
            }

            bool queryGpu = !onBattery;
            if (queryGpu)
            {
                try
                {
                    gpuTemp = wmi.GpuTemp;
                    if (isWindowVisible)
                    {
                        gpuRpm = wmi.GpuFanRpm;
                    }
                }
                catch { }
            }

            if (isWindowVisible && pdhMonitor != null)
            {
                try
                {
                    var (sampledCpu, sampledGpu) = pdhMonitor.Sample(queryGpu);
                    cpuLoad = sampledCpu;
                    gpuLoad = sampledGpu;
                }
                catch { }
            }

            try
            {
                hasHwMode = wmi.TryGetPowerMode(out probedHwMode);
            }
            catch { }

            var snapshot = new TelemetrySnapshot(
                cpuTemp,
                gpuTemp,
                cpuRpm,
                gpuRpm,
                sysRpm,
                cpuLoad,
                gpuLoad,
                gpuAsleep,
                onBattery,
                hasHwMode,
                probedHwMode,
                cpuFrequencyMhz,
                gpuFrequencyMhz,
                cpuPowerWatts,
                gpuPowerWatts,
                DateTime.UtcNow
            );

            return snapshot.Sanitize();
        }

        public void ResetMissCounter()
        {
            lock (_sync)
            {
                _consecutiveSensorMisses = 0;
            }
        }
    }
}
