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
        private int _next;
        private int _count;

        public int Capacity { get; }
        public int Count => _count;

        public HistoryBuffer(int capacity = 60)
        {
            Capacity = Math.Max(capacity, 2);
            _values = new double?[Capacity];
        }

        public double? this[int index] => _values[(_next - _count + index + Capacity) % Capacity];

        public void Add(double? value)
        {
            _values[_next] = value;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
        }

        public (double Min, double Max)? Range()
        {
            double? min = null, max = null;
            for (int i = 0; i < _count; i++)
            {
                var val = this[i];
                if (val.HasValue)
                {
                    min = Math.Min(min ?? val.Value, val.Value);
                    max = Math.Max(max ?? val.Value, val.Value);
                }
            }
            if (min.HasValue && max.HasValue)
                return (min.Value, max.Value);
            return null;
        }

        public void Clear()
        {
            Array.Clear(_values, 0, _values.Length);
            _next = 0;
            _count = 0;
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

        private static readonly Color GridColor = Color.FromArgb(36, 40, 52);
        private static readonly Color BorderColor = Color.FromArgb(48, 54, 70);
        private static readonly Color BgColor = Color.FromArgb(16, 18, 24);

        public HistoryGraphControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = BgColor;
        }

        public void PushSample(double? primaryValue, double? secondaryValue = null)
        {
            PrimarySeries.Add(primaryValue);
            SecondarySeries.Add(secondaryValue);
            Invalidate();
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
            using (var bgBrush = new SolidBrush(BackColor))
                g.FillRectangle(bgBrush, 0, 0, w, h);

            // Grid lines (25%, 50%, 75%)
            using (var gridPen = new Pen(GridColor, 1f) { DashStyle = DashStyle.Dash })
            {
                for (int i = 1; i <= 3; i++)
                {
                    float y = h * i / 4f;
                    g.DrawLine(gridPen, 0, y, w, y);
                }
            }

            // Draw Secondary Series (Load / Dotted Line)
            DrawSeries(g, SecondarySeries, Color.FromArgb(100, 160, 180, 200), false, 1.2f);

            // Draw Primary Series (Temperature / Solid Gradient Area)
            DrawSeries(g, PrimarySeries, Color.FromArgb(0, 200, 160), true, 2.0f);

            // Border
            using (var borderPen = new Pen(BorderColor, 1f))
                g.DrawRectangle(borderPen, 0, 0, w - 1, h - 1);

            // Header text
            if (!string.IsNullOrEmpty(Title))
            {
                using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
                using var brush = new SolidBrush(Color.FromArgb(150, 160, 175));
                g.DrawString(Title, font, brush, 4, 3);
            }
        }

        private void DrawSeries(Graphics g, HistoryBuffer buffer, Color lineColor, bool fillGradient, float strokeWidth)
        {
            if (buffer.Count < 2) return;

            int w = Width;
            int h = Height;
            float stepX = (float)w / (buffer.Capacity - 1);
            float startX = (buffer.Capacity - buffer.Count) * stepX;

            var points = new PointF[buffer.Count];
            for (int i = 0; i < buffer.Count; i++)
            {
                var val = buffer[i] ?? Minimum;
                float norm = (float)Math.Clamp((val - Minimum) / (Maximum - Minimum), 0.0, 1.0);
                float x = startX + (i * stepX);
                float y = h - (norm * (h - 6f)) - 3f;
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
                    new Point(0, 0), new Point(0, h),
                    Color.FromArgb(90, lineColor.R, lineColor.G, lineColor.B),
                    Color.FromArgb(5, lineColor.R, lineColor.G, lineColor.B));

                g.FillPath(gradBrush, path);
            }

            using var pen = new Pen(lineColor, strokeWidth);
            g.DrawLines(pen, points);
        }
    }
}
