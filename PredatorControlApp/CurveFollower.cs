using System;
using System.Collections.Generic;
using System.Drawing;

namespace PredatorControlApp
{
    public sealed class CurveFollower
    {
        public const int MinHysteresisDegrees = 0;
        public const int MaxHysteresisDegrees = 10;
        public const int DefaultHysteresisDegrees = 2;
        private const int MinChangePercent = 2;

        private readonly object _syncLock = new();
        private double? _peakTemperature;
        private int _hysteresisDegrees = DefaultHysteresisDegrees;

        public CurveFollower()
        {
        }

        public CurveFollower(int hysteresisDegrees)
        {
            HysteresisDegrees = hysteresisDegrees;
        }

        public CurveFollower(double hysteresisDegrees)
            : this((int)Math.Round(double.IsNaN(hysteresisDegrees) || double.IsInfinity(hysteresisDegrees) ? DefaultHysteresisDegrees : hysteresisDegrees))
        {
        }

        public int HysteresisDegrees
        {
            get { lock (_syncLock) return _hysteresisDegrees; }
            set
            {
                lock (_syncLock)
                {
                    _hysteresisDegrees = Math.Clamp(value, MinHysteresisDegrees, MaxHysteresisDegrees);
                }
            }
        }

        public int? Current
        {
            get { lock (_syncLock) return _current; }
            private set { lock (_syncLock) _current = value; }
        }
        private int? _current;

        public void Reset()
        {
            lock (_syncLock)
            {
                _peakTemperature = null;
                _current = null;
            }
        }

        public int Update(double temperature, List<Point> curvePoints)
        {
            if (double.IsNaN(temperature) || double.IsInfinity(temperature))
            {
                return Current ?? 0;
            }

            lock (_syncLock)
            {
                temperature = Math.Clamp(temperature, -100.0, 300.0);

                if (_peakTemperature.HasValue && (double.IsNaN(_peakTemperature.Value) || double.IsInfinity(_peakTemperature.Value)))
                {
                    _peakTemperature = null;
                }

                // Temperature-based hysteresis:
                // When HysteresisDegrees == 0, deadband hold is bypassed and fan speed tracks curve immediately on both rising and falling temperatures.
                // When > 0, rises immediately on heating. On cooling, holds peak temperature until temperature
                // drops by more than HysteresisDegrees below the peak to prevent fan speed hunting/oscillation.
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

                int calculatedPercent = Form1.InterpolateCurve(curvePoints, (int)Math.Round(tempToUse));
                calculatedPercent = Math.Clamp(calculatedPercent, 0, 100);

                // Deadband hold: bypassed when HysteresisDegrees == 0 for immediate responsive tracking.
                if (_hysteresisDegrees > 0 && _current.HasValue)
                {
                    int cur = _current.Value;
                    bool withinDeadband = Math.Abs(calculatedPercent - cur) < MinChangePercent;
                    bool isBoundary = calculatedPercent <= 10 || calculatedPercent == 100;

                    if (withinDeadband && !isBoundary)
                    {
                        calculatedPercent = cur;
                    }
                }

                _current = calculatedPercent;
                return calculatedPercent;
            }
        }
    }
}
