using System;
using System.Collections.Generic;
using System.Drawing;

namespace PredatorControlApp
{
    public sealed class CurveFollower
    {
        private const int MinChangePercent = 2;
        private const double HysteresisDegrees = 2.0;
        private readonly object _syncLock = new();
        private double? _peakTemperature;

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
                // Rises immediately on heating. On cooling, holds peak temperature until temperature
                // drops by more than HysteresisDegrees (2°C) below the peak to prevent fan speed hunting/oscillation.
                if (!_peakTemperature.HasValue || temperature >= _peakTemperature.Value)
                {
                    _peakTemperature = temperature;
                }
                else if (temperature < _peakTemperature.Value - HysteresisDegrees)
                {
                    _peakTemperature = temperature;
                }

                double tempToUse = _peakTemperature ?? temperature;

                int calculatedPercent = Form1.InterpolateCurve(curvePoints, (int)Math.Round(tempToUse));
                calculatedPercent = Math.Clamp(calculatedPercent, 0, 100);

                if (_current.HasValue)
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
