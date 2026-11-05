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
            bool queryGpu = !onBattery;

            // 1. Native hardware telemetry consolidation (low overhead Ring-0 / NVAPI)
            if (PredatorNativeCore.IsNativeCoreAvailable &&
                PredatorNativeCore.TelemetryGetSnapshot(out var snap) &&
                snap.TimestampMs > 0)
            {
                if (snap.CpuTemp > 0) cpuTemp = (int)Math.Round(snap.CpuTemp);
                if (queryGpu && snap.GpuTemp > 0) gpuTemp = (int)Math.Round(snap.GpuTemp);
                if (isWindowVisible)
                {
                    if (snap.CpuFanRpm > 0) cpuRpm = (int)snap.CpuFanRpm;
                    if (queryGpu && snap.GpuFanRpm > 0) gpuRpm = (int)snap.GpuFanRpm;
                }
                if (snap.CpuLoad >= 0) cpuLoad = Math.Clamp(snap.CpuLoad, 0.0, 100.0);
                if (queryGpu && snap.GpuLoad >= 0) gpuLoad = Math.Clamp(snap.GpuLoad, 0.0, 100.0);
            }

            // 2. Managed WMI fallbacks
            if (cpuTemp <= 0)
            {
                try { cpuTemp = wmi.CpuTemp; }
                catch { }
            }

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
                if (cpuRpm <= 0) { try { cpuRpm = wmi.CpuFanRpm; } catch { } }
                try { sysRpm = wmi.SystemFanRpm; } catch { }
            }

            if (queryGpu)
            {
                if (gpuTemp <= 0)
                {
                    try { gpuTemp = wmi.GpuTemp; } catch { }
                }
                if (isWindowVisible && gpuRpm <= 0)
                {
                    try { gpuRpm = wmi.GpuFanRpm; } catch { }
                }
            }

            if (isWindowVisible && pdhMonitor != null && (cpuLoad == null || (queryGpu && gpuLoad == null)))
            {
                try
                {
                    var (sampledCpu, sampledGpu) = pdhMonitor.Sample(queryGpu);
                    cpuLoad ??= sampledCpu;
                    gpuLoad ??= sampledGpu;
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
