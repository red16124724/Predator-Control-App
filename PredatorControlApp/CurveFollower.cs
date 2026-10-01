using System;
using System.Collections.Generic;
using System.Drawing;

namespace PredatorControlApp
{
    public sealed class CurveFollower
    {
        private const int MinChangePercent = 2;
        private double? _previous;

        public int? Current { get; private set; }

        public void Reset()
        {
            _previous = null;
            Current = null;
        }

        public int Update(double temperature, List<Point> curvePoints)
        {
            double tempToUse = _previous.HasValue
                ? Math.Max(_previous.Value, temperature)
                : temperature;

            _previous = temperature;

            int calculatedPercent = Form1.InterpolateCurve(curvePoints, (int)Math.Round(tempToUse));
            calculatedPercent = Math.Clamp(calculatedPercent, 0, 100);

            if (Current.HasValue)
            {
                int cur = Current.Value;
                bool withinDeadband = Math.Abs(calculatedPercent - cur) < MinChangePercent;
                bool isBoundary = calculatedPercent == 0 || calculatedPercent == 100;

                if (withinDeadband && !isBoundary)
                {
                    calculatedPercent = cur;
                }
            }

            Current = calculatedPercent;
            return calculatedPercent;
        }
    }
}
