using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PredatorControlApp
{
    public sealed class HistoryBuffer
    {
        private readonly double?[] _values;
        private readonly object _lock = new();
        private int _next;
        private int _count;

        public int Capacity { get; }
        public int Count { get { lock (_lock) return _count; } }

        public HistoryBuffer(int capacity = 60)
        {
            Capacity = Math.Max(capacity, 2);
            _values = new double?[Capacity];
        }

        public double? this[int index]
        {
            get
            {
                lock (_lock)
                {
                    if (index < 0 || index >= _count) return null;
                    int idx = ((_next - _count + index) % Capacity + Capacity) % Capacity;
                    return _values[idx];
                }
            }
        }

        public void Add(double? value)
        {
            lock (_lock)
            {
                _values[_next] = value;
                _next = (_next + 1) % Capacity;
                _count = Math.Min(_count + 1, Capacity);
            }
        }

        public (double Min, double Max)? Range()
        {
            lock (_lock)
            {
                double? min = null, max = null;
                for (int i = 0; i < _count; i++)
                {
                    int idx = ((_next - _count + i) % Capacity + Capacity) % Capacity;
                    var val = _values[idx];
                    if (val.HasValue && !double.IsNaN(val.Value) && !double.IsInfinity(val.Value))
                    {
                        min = Math.Min(min ?? val.Value, val.Value);
                        max = Math.Max(max ?? val.Value, val.Value);
                    }
                }
                if (min.HasValue && max.HasValue)
                    return (min.Value, max.Value);
                return null;
            }
        }

        public double? Min() => Range()?.Min;

        public double? Max() => Range()?.Max;

        public double? Average()
        {
            lock (_lock)
            {
                if (_count == 0) return null;
                double sum = 0;
                long validCount = 0;
                for (int i = 0; i < _count; i++)
                {
                    int idx = ((_next - _count + i) % Capacity + Capacity) % Capacity;
                    var val = _values[idx];
                    if (val.HasValue && !double.IsNaN(val.Value) && !double.IsInfinity(val.Value))
                    {
                        sum += val.Value;
                        validCount++;
                    }
                }
                if (validCount > 0)
                    return sum / validCount;
                return null;
            }
        }

        public double?[] GetSnapshot()
        {
            lock (_lock)
            {
                var snapshot = new double?[_count];
                for (int i = 0; i < _count; i++)
                {
                    int idx = ((_next - _count + i) % Capacity + Capacity) % Capacity;
                    snapshot[i] = _values[idx];
                }
                return snapshot;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_values, 0, _values.Length);
                _next = 0;
                _count = 0;
            }
        }
    }

    public sealed class HistoryGraphControl : Control
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public HistoryBuffer PrimarySeries { get; } = new(60);

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public HistoryBuffer SecondarySeries { get; } = new(60);

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double Minimum { get; set; } = 0.0;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double Maximum { get; set; } = 100.0;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Title { get; set; } = "";

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Unit { get; set; } = "°C";

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double? LatestValue => PrimarySeries.Count > 0 ? PrimarySeries[PrimarySeries.Count - 1] : null;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double? PeakValue => PrimarySeries.Range()?.Max;

        private static Color GridColor => ThemeManager.IsDarkThemeActive ? Color.FromArgb(50, 60, 80) : Color.FromArgb(200, 210, 225);
        private static Color BorderColor => ThemeManager.CardBorder;
        private static Color BgColor => ThemeManager.CardBg;
        private static readonly Font s_boundFont = new("Segoe UI", 6.5f, FontStyle.Regular);
        private static readonly Font s_headerFont = new("Segoe UI", 7.5f, FontStyle.Bold);
        private readonly Action _themeHandler;

        public HistoryGraphControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = BgColor;

            _themeHandler = () =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BackColor = BgColor;
                    Invalidate();
                }
            };
            ThemeManager.ThemeChanged += _themeHandler;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;
            }
            base.Dispose(disposing);
        }

        public void PushSample(double? primaryValue, double? secondaryValue = null)
        {
            PrimarySeries.Add(primaryValue);
            SecondarySeries.Add(secondaryValue);
            if (!IsDisposed && IsHandleCreated && Visible)
            {
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int w = Width;
            int h = Height;
            if (w < 10 || h < 10) return;

            // Background
            using (var bgBrush = new SolidBrush(BgColor))
                g.FillRectangle(bgBrush, 0, 0, w, h);

            // Grid lines (25%, 50%, 75%) with high-contrast pen
            using (var gridPen = new Pen(GridColor, 1f) { DashStyle = DashStyle.Dash })
            {
                for (int i = 1; i <= 3; i++)
                {
                    float y = h * i / 4f;
                    g.DrawLine(gridPen, 0, y, w, y);
                }
            }

            // Min and Max bounds labels along Y-axis
            using (var boundBrush = new SolidBrush(ThemeManager.TextSecondary))
            {
                string maxStr = $"{Maximum:0}{Unit}";
                string minStr = $"{Minimum:0}{Unit}";
                var maxSz = g.MeasureString(maxStr, s_boundFont);
                var minSz = g.MeasureString(minStr, s_boundFont);
                g.DrawString(maxStr, s_boundFont, boundBrush, w - maxSz.Width - 4, 3);
                g.DrawString(minStr, s_boundFont, boundBrush, w - minSz.Width - 4, h - minSz.Height - 3);
            }

            // Draw Secondary Series (Load / High Contrast Series)
            Color secCol = ThemeManager.IsDarkThemeActive ? Color.FromArgb(140, 185, 255) : Color.FromArgb(30, 95, 205);
            DrawSeries(g, SecondarySeries, secCol, false, 1.5f);

            // Draw Primary Series (Temperature / Solid Gradient Area with high-contrast accent)
            DrawSeries(g, PrimarySeries, ThemeManager.Accent, true, 2.2f);

            // Border
            using (var borderPen = new Pen(BorderColor, 1.2f))
                g.DrawRectangle(borderPen, 0, 0, w - 1, h - 1);

            // Live numeric temperature text (e.g. CPU: 65°C | Peak: 72°C)
            string headerText = Title;
            if (PrimarySeries.Count > 0)
            {
                double? latest = PrimarySeries[PrimarySeries.Count - 1];
                var range = PrimarySeries.Range();
                if (latest.HasValue)
                {
                    double peak = range?.Max ?? latest.Value;
                    string prefix = !string.IsNullOrEmpty(Title) ? Title : "Temp";
                    headerText = $"{prefix}: {latest.Value:0}{Unit} | Peak: {peak:0}{Unit}";
                }
            }

            if (!string.IsNullOrEmpty(headerText))
            {
                using var brush = new SolidBrush(ThemeManager.TextPrimary);
                g.DrawString(headerText, s_headerFont, brush, 5, 4);
            }
        }

        private void DrawSeries(Graphics g, HistoryBuffer buffer, Color lineColor, bool fillGradient, float strokeWidth)
        {
            var snapshot = buffer.GetSnapshot();
            if (snapshot.Length < 2) return;

            int w = Width;
            int h = Height;
            float stepX = (float)w / Math.Max(buffer.Capacity - 1, 1);
            float startX = (buffer.Capacity - snapshot.Length) * stepX;

            double min = Minimum;
            double max = Maximum;
            double range = max - min;
            if (range <= 0.0001 || double.IsNaN(range) || double.IsInfinity(range))
            {
                range = 1.0;
            }

            var points = new PointF[snapshot.Length];
            for (int i = 0; i < snapshot.Length; i++)
            {
                var rawVal = snapshot[i];
                double val = (rawVal.HasValue && !double.IsNaN(rawVal.Value) && !double.IsInfinity(rawVal.Value))
                    ? rawVal.Value
                    : min;
                float norm = (float)Math.Clamp((val - min) / range, 0.0, 1.0);
                if (float.IsNaN(norm) || float.IsInfinity(norm)) norm = 0f;

                float x = startX + (i * stepX);
                float y = h - (norm * (h - 6f)) - 3f;
                if (float.IsNaN(x) || float.IsInfinity(x)) x = 0f;
                if (float.IsNaN(y) || float.IsInfinity(y)) y = (float)h / 2f;

                points[i] = new PointF(x, y);
            }

            if (fillGradient && points.Length >= 2)
            {
                using var path = new GraphicsPath();
                path.AddLines(points);
                path.AddLine(points[^1], new PointF(points[^1].X, h));
                path.AddLine(new PointF(points[^1].X, h), new PointF(points[0].X, h));
                path.CloseFigure();

                using var gradBrush = new LinearGradientBrush(
                    new Point(0, 0), new Point(0, Math.Max(h, 1)),
                    Color.FromArgb(80, lineColor.R, lineColor.G, lineColor.B),
                    Color.FromArgb(5, lineColor.R, lineColor.G, lineColor.B));

                g.FillPath(gradBrush, path);
            }

            using var pen = new Pen(lineColor, strokeWidth);
            g.DrawLines(pen, points);
        }
    }
}
