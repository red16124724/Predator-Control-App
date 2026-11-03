using System;

namespace PredatorControlApp
{
    /// <summary>
    /// Thread-safe, immutable snapshot of full hardware telemetry metrics.
    /// Reused across UI rendering, logging, IPC, and thermal safety evaluation.
    /// </summary>
    public readonly record struct TelemetrySnapshot(
        int CpuTemp,
        int GpuTemp,
        int CpuRpm,
        int GpuRpm,
        int SysRpm,
        double? CpuLoad,
        double? GpuLoad,
        bool GpuAsleep,
        bool OnBattery,
        bool HasHwMode,
        byte ProbedHwMode,
        double CpuFrequencyMhz = 0,
        double GpuFrequencyMhz = 0,
        double CpuPowerWatts = 0,
        double GpuPowerWatts = 0,
        DateTime TimestampUtc = default)
    {
        public static TelemetrySnapshot Empty => new(
            0, 0, 0, 0, 0, null, null, false, false, false, 0);

        /// <summary>
        /// Returns a sanitized snapshot with all temperatures, loads, and power metrics clamped to realistic physical limits.
        /// </summary>
        public TelemetrySnapshot Sanitize()
        {
            int safeCpuTemp = (CpuTemp < 0 || CpuTemp > 150) ? 0 : CpuTemp;
            int safeGpuTemp = (GpuTemp < 0 || GpuTemp > 150) ? 0 : GpuTemp;
            int safeCpuRpm = Math.Max(0, Math.Min(CpuRpm, 15000));
            int safeGpuRpm = Math.Max(0, Math.Min(GpuRpm, 15000));
            int safeSysRpm = Math.Max(0, Math.Min(SysRpm, 15000));

            double? safeCpuLoad = CpuLoad.HasValue && !double.IsNaN(CpuLoad.Value) && !double.IsInfinity(CpuLoad.Value)
                ? Math.Clamp(CpuLoad.Value, 0.0, 100.0)
                : null;

            double? safeGpuLoad = GpuLoad.HasValue && !double.IsNaN(GpuLoad.Value) && !double.IsInfinity(GpuLoad.Value)
                ? Math.Clamp(GpuLoad.Value, 0.0, 100.0)
                : null;

            double safeCpuFreq = double.IsNaN(CpuFrequencyMhz) || double.IsInfinity(CpuFrequencyMhz) || CpuFrequencyMhz < 0 ? 0 : CpuFrequencyMhz;
            double safeGpuFreq = double.IsNaN(GpuFrequencyMhz) || double.IsInfinity(GpuFrequencyMhz) || GpuFrequencyMhz < 0 ? 0 : GpuFrequencyMhz;
            double safeCpuPower = double.IsNaN(CpuPowerWatts) || double.IsInfinity(CpuPowerWatts) || CpuPowerWatts < 0 ? 0 : CpuPowerWatts;
            double safeGpuPower = double.IsNaN(GpuPowerWatts) || double.IsInfinity(GpuPowerWatts) || GpuPowerWatts < 0 ? 0 : GpuPowerWatts;

            return this with
            {
                CpuTemp = safeCpuTemp,
                GpuTemp = safeGpuTemp,
                CpuRpm = safeCpuRpm,
                GpuRpm = safeGpuRpm,
                SysRpm = safeSysRpm,
                CpuLoad = safeCpuLoad,
                GpuLoad = safeGpuLoad,
                CpuFrequencyMhz = safeCpuFreq,
                GpuFrequencyMhz = safeGpuFreq,
                CpuPowerWatts = safeCpuPower,
                GpuPowerWatts = safeGpuPower,
                TimestampUtc = TimestampUtc == default ? DateTime.UtcNow : TimestampUtc
            };
        }

        public bool HasCpuSensorDisconnect => CpuTemp <= 0;
        public bool HasGpuSensorDisconnect => !GpuAsleep && !OnBattery && GpuTemp <= 0;
        public bool IsExtremeThermal(int thresholdCelsius = 100) => CpuTemp >= thresholdCelsius || GpuTemp >= thresholdCelsius;
    }
}
