using System;
using System.Collections.Generic;
using System.Drawing;

namespace PredatorControlApp
{
    public sealed class CurveFollower : IDisposable
    {
        public const int MinHysteresisDegrees = 0;
        public const int MaxHysteresisDegrees = 10;
        public const int DefaultHysteresisDegrees = 2;
        private const int MinChangePercent = 2;

        private readonly object _syncLock = new();
        private double? _peakTemperature;
        private int _hysteresisDegrees = DefaultHysteresisDegrees;
        private IntPtr _nativeHandle = IntPtr.Zero;

        public CurveFollower()
        {
            InitNative();
        }

        public CurveFollower(int hysteresisDegrees)
        {
            HysteresisDegrees = hysteresisDegrees;
            InitNative();
        }

        public CurveFollower(double hysteresisDegrees)
            : this((int)Math.Round(double.IsNaN(hysteresisDegrees) || double.IsInfinity(hysteresisDegrees) ? DefaultHysteresisDegrees : hysteresisDegrees))
        {
        }

        private void InitNative()
        {
            lock (_syncLock)
            {
                if (PredatorNativeCore.IsNativeCoreAvailable && _nativeHandle == IntPtr.Zero)
                {
                    _nativeHandle = PredatorNativeCore.CurveCreate(_hysteresisDegrees);
                }
            }
        }

        public int HysteresisDegrees
        {
            get { lock (_syncLock) return _hysteresisDegrees; }
            set
            {
                lock (_syncLock)
                {
                    _hysteresisDegrees = Math.Clamp(value, MinHysteresisDegrees, MaxHysteresisDegrees);
                    if (_nativeHandle != IntPtr.Zero)
                    {
                        PredatorNativeCore.CurveDestroy(_nativeHandle);
                        _nativeHandle = PredatorNativeCore.CurveCreate(_hysteresisDegrees);
                    }
                }
            }
        }

        public int? Current
        {
            get { lock (_syncLock) return _current; }
            private set { lock (_syncLock) _current = value; }
        }
        private int? _current;

        private bool _disposed;
        public bool IsDisposed
        {
            get { lock (_syncLock) return _disposed; }
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                if (_disposed) return;
                _disposed = true;
                _peakTemperature = null;
                _current = null;
                if (_nativeHandle != IntPtr.Zero)
                {
                    PredatorNativeCore.CurveDestroy(_nativeHandle);
                    _nativeHandle = IntPtr.Zero;
                }
            }
        }

        public void Reset()
        {
            lock (_syncLock)
            {
                _peakTemperature = null;
                _current = null;
                if (_nativeHandle != IntPtr.Zero)
                {
                    PredatorNativeCore.CurveReset(_nativeHandle);
                }
            }
        }

        public int Update(double temperature, List<Point> curvePoints) => Update(temperature, curvePoints, 0);

        public int Update(double temperature, List<Point> curvePoints, AntiThrottleSafety? safety)
        {
            int boost = safety != null ? safety.CalculateBoost(temperature) : 0;
            return Update(temperature, curvePoints, boost);
        }

        public int Update(double temperature, List<Point> curvePoints, AntiThrottleResult result)
        {
            return Update(temperature, curvePoints, result.TargetFloorPercent);
        }

        public int Update(double temperature, List<Point> curvePoints, int safetyFloorPercent)
        {
            lock (_syncLock)
            {
                safetyFloorPercent = Math.Clamp(safetyFloorPercent, 0, 100);
                if (_disposed) return safetyFloorPercent;

                if (double.IsNaN(temperature) || double.IsInfinity(temperature))
                {
                    int safe = Math.Max(_current ?? safetyFloorPercent, safetyFloorPercent);
                    _current = safe;
                    return safe;
                }

                temperature = Math.Clamp(temperature, -50.0, 300.0);

                if (_peakTemperature.HasValue && (double.IsNaN(_peakTemperature.Value) || double.IsInfinity(_peakTemperature.Value)))
                {
                    _peakTemperature = null;
                }

                // Native Rust Acceleration path
                if (PredatorNativeCore.IsNativeCoreAvailable && _nativeHandle != IntPtr.Zero && curvePoints != null)
                {
                    NativeControlPoint[]? nativePts = null;
                    for (int retry = 0; retry < 5; retry++)
                    {
                        try
                        {
                            lock (curvePoints)
                            {
                                int len = curvePoints.Count;
                                if (len > 0)
                                {
                                    nativePts = new NativeControlPoint[len];
                                    for (int i = 0; i < len; i++)
                                    {
                                        nativePts[i] = new NativeControlPoint(curvePoints[i].X, curvePoints[i].Y);
                                    }
                                }
                            }
                            break;
                        }
                        catch
                        {
                            System.Threading.Thread.SpinWait(10);
                        }
                    }

                    if (nativePts != null && nativePts.Length > 0)
                    {
                        int nativeSpeed = PredatorNativeCore.CurveUpdate(_nativeHandle, temperature, nativePts, safetyFloorPercent);
                        _current = nativeSpeed;
                        return nativeSpeed;
                    }
                }

                // Managed Fallback:
                // Temperature-based hysteresis:
                if (_hysteresisDegrees <= 0)
                {
                    _peakTemperature = temperature;
                }
                else if (!_peakTemperature.HasValue || temperature >= _peakTemperature.Value)
                {
                    _peakTemperature = temperature;
                }
                else if (temperature < _peakTemperature.Value - _hysteresisDegrees)
                {
                    _peakTemperature = temperature;
                }

                double tempToUse = _peakTemperature ?? temperature;

                Point[]? safePoints = null;
                if (curvePoints != null)
                {
                    for (int retry = 0; retry < 5; retry++)
                    {
                        try
                        {
                            lock (curvePoints)
                            {
                                safePoints = curvePoints.ToArray();
                            }
                            break;
                        }
                        catch
                        {
                            System.Threading.Thread.SpinWait(10);
                        }
                    }
                }
                var pointsList = safePoints != null ? new List<Point>(safePoints) : curvePoints;
                int calculatedPercent = Form1.InterpolateCurve(pointsList, (int)Math.Round(tempToUse));
                calculatedPercent = Math.Clamp(calculatedPercent, 0, 100);

                // Emergency safety override: when safety floor demands 100%, immediately bypass deadband hold
                if (safetyFloorPercent >= 100)
                {
                    _peakTemperature = temperature;
                    _current = 100;
                    return 100;
                }

                // Deadband hold: bypassed when HysteresisDegrees == 0 or when safety floor is actively overriding
                bool isSafetyOverriding = safetyFloorPercent > calculatedPercent;
                if (_hysteresisDegrees > 0 && _current.HasValue && !isSafetyOverriding)
                {
                    int cur = _current.Value;
                    bool withinDeadband = Math.Abs(calculatedPercent - cur) < MinChangePercent;
                    bool isBoundary = calculatedPercent <= 10 || calculatedPercent == 100;

                    if (withinDeadband && !isBoundary)
                    {
                        calculatedPercent = cur;
                    }
                }

                // Apply safety floor: final speed cannot fall below AntiThrottle safety floor
                calculatedPercent = Math.Max(calculatedPercent, safetyFloorPercent);

                _current = calculatedPercent;
                return calculatedPercent;
            }
        }
    }
}