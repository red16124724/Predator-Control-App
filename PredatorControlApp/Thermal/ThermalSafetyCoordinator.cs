using System;

namespace PredatorControlApp
{
    public readonly record struct ThermalDecision(
        int Stage,
        bool Active,
        bool Critical,
        int CpuFanFloor,
        int GpuFanFloor,
        int? CpuPowerCapWatts,
        double CpuTemp,
        double GpuTemp,
        string Reason)
    {
        public static ThermalDecision Idle { get; } = new(
            0, false, false, 0, 0, null, 0, 0, "Normal");

        public string StageLabel => Stage switch
        {
            3 => "CRITICAL",
            2 => "CEILING",
            1 => "WARM",
            _ => "NORMAL"
        };
    }

    /// <summary>
    /// Optimized thermal safety layer for Predator Control App.
    ///
    /// Compared with a plain hard ceiling (the PreySense approach), this adds:
    ///  - separate warn/ceiling/critical stages for CPU and GPU;
    ///  - hysteresis so the fan floor does not chatter around one threshold;
    ///  - EMA smoothing so a single bad sample cannot slam the fans;
    ///  - rate-limited floor increases so the EC is not spammed;
    ///  - a proportional CPU watt cap instead of a binary cut;
    ///  - persistence through ThermalSafetyProfile.
    ///
    /// The guard never shuts the machine down. At the critical stage it pins
    /// fans to 100% (optional) and drops the CPU cap to the configured floor.
    /// </summary>
    public sealed class ThermalSafetyCoordinator : IDisposable
    {
        private const double EmaAlpha = 0.35;
        private const int MaxFloorStepPerTick = 12;
        private const int MinFloorStepIntervalMs = 250;

        /// <summary>
        /// Floor held while the chip sits inside the hysteresis band (below the
        /// warn threshold but above warn - hysteresis). Without this the fan
        /// floor would fall straight to zero and the latch would have no effect.
        /// </summary>
        private const int HysteresisHoldFloorPercent = 15;

        private readonly object _sync = new();
        private readonly IntPtr _thermalHandle;
        private bool _native;
        private bool _disposed;
        private ThermalSafetyProfile _profile;
        private double _cpuEma;
        private double _gpuEma;
        private int _cpuMisses;
        private int _gpuMisses;
        private bool _cpuLatched;
        private bool _gpuLatched;
        private int _cpuFloor;
        private int _gpuFloor;
        private int _lastStage;
        private DateTime _lastCpuFloorStepUtc = DateTime.MinValue;
        private DateTime _lastGpuFloorStepUtc = DateTime.MinValue;
        private DateTime _lastEvalUtc = DateTime.MinValue;

        public event EventHandler<ThermalDecision>? CriticalEntered;

        /// <summary>
        /// Whether this coordinator is executing the Rust policy in <c>thermal.rs</c>.
        /// When native code is unavailable the managed policy below is used instead.
        /// </summary>
        internal bool NativeAccelerated => _native;

        public ThermalSafetyCoordinator(ThermalSafetyProfile? profile = null, bool? preferNative = null)
        {
            _profile = (profile ?? ThermalSafetyProfile.Default).Normalize();
            if (preferNative ?? PredatorNativeCore.IsNativeCoreAvailable)
            {
                _thermalHandle = PredatorNativeCore.ThermalCreate();
                _native = _thermalHandle != IntPtr.Zero;
            }
            if (_native && !PushProfileToNative())
            {
                // The native coordinator refused the profile; stay on the managed policy
                // rather than evaluating against different thresholds.
                _native = false;
                PredatorNativeCore.ThermalDestroy(_thermalHandle);
            }
        }

        /// <summary>
        /// Releases this instance's native coordinator handle. The managed policy stays
        /// usable, so evaluation falls back to it after disposal.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _native = false;
            if (_thermalHandle != IntPtr.Zero) PredatorNativeCore.ThermalDestroy(_thermalHandle);
            GC.SuppressFinalize(this);
        }

        ~ThermalSafetyCoordinator()
        {
            try
            {
                if (_thermalHandle != IntPtr.Zero) PredatorNativeCore.ThermalDestroy(_thermalHandle);
            }
            catch { }
        }

        /// <summary>
        /// Copies the active profile into the native coordinator so both implementations
        /// evaluate identical thresholds.
        /// </summary>
        private bool PushProfileToNative()
        {
            if (!_native || _disposed) return true;
            var p = _profile;
            return PredatorNativeCore.ThermalSetProfile(
                _thermalHandle,
                p.Enabled,
                p.CpuWarnCelsius,
                p.CpuCeilingCelsius,
                p.CpuCriticalCelsius,
                p.GpuWarnCelsius,
                p.GpuCeilingCelsius,
                p.GpuCriticalCelsius,
                p.HysteresisCelsius,
                p.MaxFanFloorPercent,
                p.CpuPowerCapWatts,
                p.MinPowerCapWatts,
                p.CriticalFansFullSpeed,
                EmaAlpha);
        }

        public ThermalSafetyProfile Profile
        {
            get { lock (_sync) return _profile; }
            set
            {
                lock (_sync)
                {
                    _profile = (value ?? ThermalSafetyProfile.Default).Normalize();
                    if (!_profile.Enabled)
                    {
                        _cpuLatched = false;
                        _gpuLatched = false;
                        _cpuFloor = 0;
                        _gpuFloor = 0;
                        _lastStage = 0;
                        _lastCpuFloorStepUtc = DateTime.MinValue;
                        _lastGpuFloorStepUtc = DateTime.MinValue;
                _cpuMisses = 0;
                _gpuMisses = 0;
                    }
                    PushProfileToNative();
                }
            }
        }

        private ThermalDecision _lastDecision = ThermalDecision.Idle;
        public ThermalDecision LastDecision
        {
            get { lock (_sync) return _lastDecision; }
            private set { _lastDecision = value; }
        }

        /// <summary>
        /// One evaluation per telemetry tick. Pass 0/unknown temperatures when a
        /// sensor is unavailable; the guard ignores that channel.
        /// </summary>
        public ThermalDecision Evaluate(
            double cpuTemp,
            double gpuTemp,
            int targetPl1Watts = 45,
            int minPowerWatts = 15,
            int baseTdpWatts = 45)
        {
            ThermalDecision decision;
            bool fireCritical = false;
            lock (_sync)
            {
                var profile = _profile;
                if (!profile.Enabled)
                {
                    _cpuLatched = false;
                    _gpuLatched = false;
                    _cpuFloor = 0;
                    _gpuFloor = 0;
                    _lastStage = 0;
                    var idle = new ThermalDecision(0, false, false, 0, 0, null, Norm(cpuTemp), Norm(gpuTemp), "Guard disabled");
                    LastDecision = idle;
                    _lastEvalUtc = DateTime.UtcNow;
                    return idle;
                }

                // Rust policy in thermal.rs is authoritative when present; the managed
                // policy below is the tested fallback used only if native code is missing.
                if (_native && PredatorNativeCore.ThermalEvaluate(_thermalHandle, cpuTemp, gpuTemp, targetPl1Watts, minPowerWatts, baseTdpWatts, out var native))
                {
                    decision = new ThermalDecision(
                        native.Stage,
                        native.Active,
                        native.Critical,
                        native.CpuFanFloor,
                        native.GpuFanFloor,
                        native.CpuPowerCapWatts > 0 ? native.CpuPowerCapWatts : null,
                        native.CpuTemp,
                        native.GpuTemp,
                        BuildReason(profile, native.CpuTemp, native.GpuTemp, native.Stage));

                    LastDecision = decision;
                    _lastEvalUtc = DateTime.UtcNow;

                    if (native.Stage >= 3 && _lastStage < 3)
                    {
                        _lastStage = 3;
                        fireCritical = true;
                    }
                    else
                    {
                        _lastStage = native.Stage;
                    }
                }
                else
                {
                    double cpu = UpdateEma(ref _cpuEma, cpuTemp, ref _cpuMisses);
                    double gpu = UpdateEma(ref _gpuEma, gpuTemp, ref _gpuMisses);

                int cpuTarget = ComputeFloor(cpu, profile.CpuWarnCelsius, profile.CpuCeilingCelsius, profile.CpuCriticalCelsius,
                    profile.MaxFanFloorPercent, profile.CriticalFansFullSpeed, ref _cpuLatched, profile.HysteresisCelsius);
                int gpuTarget = ComputeFloor(gpu, profile.GpuWarnCelsius, profile.GpuCeilingCelsius, profile.GpuCriticalCelsius,
                    profile.MaxFanFloorPercent, profile.CriticalFansFullSpeed, ref _gpuLatched, profile.HysteresisCelsius);

                _cpuFloor = RateLimit(_cpuFloor, cpuTarget, ref _lastCpuFloorStepUtc);
                _gpuFloor = RateLimit(_gpuFloor, gpuTarget, ref _lastGpuFloorStepUtc);

                bool cpuCritical = cpu > 0 && cpu >= profile.CpuCriticalCelsius;
                bool gpuCritical = gpu > 0 && gpu >= profile.GpuCriticalCelsius;
                bool critical = cpuCritical || gpuCritical;

                bool cpuHot = cpu > 0 && cpu >= profile.CpuWarnCelsius;
                bool gpuHot = gpu > 0 && gpu >= profile.GpuWarnCelsius;
                bool active = cpuHot || gpuHot || _cpuFloor > 0 || _gpuFloor > 0;

                int stage = critical
                    ? 3
                    : (cpu >= profile.CpuCeilingCelsius || gpu >= profile.GpuCeilingCelsius) ? 2
                    : active ? 1 : 0;

                int? cap = ComputePowerCap(cpu, profile, targetPl1Watts, minPowerWatts, baseTdpWatts);

                string reason = BuildReason(profile, cpu, gpu, stage);

                decision = new ThermalDecision(
                    stage,
                    active,
                    critical,
                    _cpuFloor,
                    _gpuFloor,
                    cap,
                    Math.Round(cpu, 1),
                    Math.Round(gpu, 1),
                    reason);

                LastDecision = decision;
                _lastEvalUtc = DateTime.UtcNow;

                if (stage >= 3 && _lastStage < 3)
                {
                    _lastStage = 3;
                    fireCritical = true;
                }
                else
                {
                    _lastStage = stage;
                }
                }
            }

            if (fireCritical)
            {
                try { CriticalEntered?.Invoke(this, decision); } catch { }
            }

            return decision;
        }

        public bool IsDecisionFresh(TimeSpan maxAge)
        {
            lock (_sync)
            {
                return DateTime.UtcNow - _lastEvalUtc <= maxAge;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _cpuEma = 0;
                _gpuEma = 0;
                _cpuLatched = false;
                _gpuLatched = false;
                _cpuFloor = 0;
                _gpuFloor = 0;
                _lastStage = 0;
                _lastCpuFloorStepUtc = DateTime.MinValue;
                _lastGpuFloorStepUtc = DateTime.MinValue;
                _cpuMisses = 0;
                _gpuMisses = 0;
                LastDecision = ThermalDecision.Idle;
                if (_native && !_disposed) PredatorNativeCore.ThermalReset(_thermalHandle);
            }
        }

        private double UpdateEma(ref double ema, double sample, ref int misses)
        {
            if (double.IsNaN(sample) || double.IsInfinity(sample) || sample <= 0 || sample > 150)
            {
                misses++;
                if (misses >= 3 && ema > 0)
                {
                    // Persistent sensor disconnect / missing reading: decay EMA toward 0 to avoid permanent thermal lockup
                    ema *= 0.5;
                    if (ema < 1.0) ema = 0.0;
                }
                return ema;
            }

            misses = 0;
            ema = ema <= 0 ? sample : (sample * EmaAlpha) + (ema * (1.0 - EmaAlpha));
            return ema;
        }

        private int RateLimit(int current, int target, ref DateTime lastStepUtc)
        {
            if (target == current) return target;

            // Critical is not rate limited: a full-speed fan command must not be
            // spread over several telemetry ticks.
            if (target >= 100) return 100;

            // Downward transitions are immediate to honor cooldown and user fan curve changes
            if (target <= current) return target;

            DateTime now = DateTime.UtcNow;
            if ((now - lastStepUtc).TotalMilliseconds < MinFloorStepIntervalMs)
                return current;

            lastStepUtc = now;
            return Math.Min(target, current + MaxFloorStepPerTick);
        }

        private static int ComputeFloor(
            double temp,
            double warn,
            double ceiling,
            double critical,
            int maxFloor,
            bool criticalFullSpeed,
            ref bool latched,
            double hysteresis)
        {
            if (temp <= 0)
            {
                latched = false;
                return 0;
            }

            if (latched)
            {
                if (temp < warn - hysteresis)
                {
                    latched = false;
                    return 0;
                }
            }
            else if (temp >= warn)
            {
                latched = true;
            }

            if (!latched) return 0;

            if (temp >= critical)
                return criticalFullSpeed ? 100 : maxFloor;

            if (temp >= ceiling)
                return maxFloor;

            if (temp < warn)
            {
                // Inside the hysteresis band: hold a modest floor until the
                // temperature falls past warn - hysteresis.
                return Math.Max(1, maxFloor * HysteresisHoldFloorPercent / 100);
            }

            double span = Math.Max(0.1, ceiling - warn);
            double progress = Math.Clamp((temp - warn) / span, 0.0, 1.0);
            return Math.Clamp((int)Math.Round(HysteresisHoldFloorPercent + progress * (maxFloor - HysteresisHoldFloorPercent)), 0, maxFloor);
        }

        private static int? ComputePowerCap(
            double cpuTemp,
            ThermalSafetyProfile profile,
            int targetPl1Watts,
            int minPowerWatts,
            int baseTdpWatts)
        {
            if (cpuTemp <= 0 || cpuTemp < profile.CpuWarnCelsius) return null;

            int target = targetPl1Watts > 0 ? targetPl1Watts : Math.Max(minPowerWatts, baseTdpWatts);
            int configuredFloor = Math.Max(5, Math.Min(minPowerWatts, profile.MinPowerCapWatts));
            int floor = Math.Min(target, configuredFloor);
            int ceilingCap = Math.Clamp(profile.CpuPowerCapWatts, floor, target);

            if (cpuTemp >= profile.CpuCriticalCelsius) return floor;
            if (cpuTemp >= profile.CpuCeilingCelsius) return ceilingCap;

            double span = Math.Max(0.1, profile.CpuCeilingCelsius - profile.CpuWarnCelsius);
            double progress = Math.Clamp((cpuTemp - profile.CpuWarnCelsius) / span, 0.0, 1.0);
            int interpolated = target - (int)Math.Round(progress * (target - ceilingCap));
            return Math.Clamp(interpolated, floor, target);
        }

        private static string BuildReason(ThermalSafetyProfile profile, double cpu, double gpu, int stage)
        {
            if (stage >= 3)
            {
                if (cpu > 0 && cpu >= profile.CpuCriticalCelsius)
                    return $"CPU {cpu:0.0} C at critical {profile.CpuCriticalCelsius:0} C";
                return $"GPU {gpu:0.0} C at critical {profile.GpuCriticalCelsius:0} C";
            }
            if (stage == 2)
            {
                if (cpu > 0 && cpu >= profile.CpuCeilingCelsius)
                    return $"CPU {cpu:0.0} C at ceiling {profile.CpuCeilingCelsius:0} C";
                return $"GPU {gpu:0.0} C at ceiling {profile.GpuCeilingCelsius:0} C";
            }
            if (stage == 1)
            {
                if (cpu > 0 && cpu >= profile.CpuWarnCelsius)
                    return $"CPU {cpu:0.0} C above warn {profile.CpuWarnCelsius:0} C";
                return $"GPU {gpu:0.0} C above warn {profile.GpuWarnCelsius:0} C";
            }
            return "Normal";
        }

        private static double Norm(double value)
            => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
    }
}
