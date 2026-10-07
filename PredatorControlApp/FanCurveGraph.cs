using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class FanCurveGraph : Control
    {
        public const int ControlPointCount = 8;

        public static IReadOnlyList<Point> DefaultCpuCurve => DefaultCpuPoints;

        public static readonly List<Point> DefaultCpuPoints = new()
        {
            new Point(30, 0),
            new Point(50, 10),
            new Point(60, 20),
            new Point(70, 25),
            new Point(77, 30),
            new Point(85, 45),
            new Point(90, 60),
            new Point(100, 100)
        };

        public static readonly List<Point> DefaultGpuPoints = new()
        {
            new Point(30, 0),
            new Point(50, 0),
            new Point(60, 20),
            new Point(70, 35),
            new Point(78, 45),
            new Point(85, 60),
            new Point(90, 75),
            new Point(100, 100)
        };

        private static readonly List<Point> _defaultPoints = DefaultCpuPoints;

        private static Color BackgroundColor => ThemeManager.IsDarkThemeActive ? Color.FromArgb(20, 24, 33) : Color.FromArgb(245, 248, 252);
        private static Color GridColor => ThemeManager.IsDarkThemeActive ? Color.FromArgb(40, 48, 64) : Color.FromArgb(215, 222, 235);
        private static Color LabelColor => ThemeManager.IsDarkThemeActive ? Color.FromArgb(140, 150, 170) : Color.FromArgb(90, 100, 120);
        private static Color BorderColor => ThemeManager.CardBorder;

        private static readonly Font s_fontAxis = new("Segoe UI", 7.5f);
        private static readonly Font s_fontTitle = new("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Font s_fontStatus = new("Segoe UI", 8f);
        private static readonly Font s_fontTooltip = new("Segoe UI", 7.5f);
        private static readonly StringFormat s_sfCenter = new() { Alignment = StringAlignment.Center };
        private static readonly StringFormat s_sfRight = new() { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        private static readonly StringFormat s_sfFar = new() { Alignment = StringAlignment.Far };

        private const int PadLeft = 45;
        private const int PadRight = 15;
        private const int PadTop = 30;
        private const int PadBottom = 30;

        private const int TempMin = 30;
        private const int TempMax = 100;
        private const int SpeedMin = 0;
        private const int SpeedMax = 100;
        private const int MinTempGap = 5;

        private const int PointRadius = 5;
        private const int PointRadiusHover = 7;
        private const int HitTestRadius = 10;

        private readonly object _pointsLock = new();
        private List<Point> _points;
        private Color _curveColor = Color.FromArgb(0, 180, 255);
        private int _currentTemp;
        private string _fanLabel = "FAN CURVE";
        private int _dragIndex = -1;
        private int _hoverIndex = -1;

        public event EventHandler? CurveChanged;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color CurveColor
        {
            get => _curveColor;
            set { _curveColor = value; if (Visible) SafeInvalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int CurrentTemp
        {
            get => _currentTemp;
            set
            {
                if (_currentTemp != value)
                {
                    _currentTemp = value;
                    if (Visible) SafeInvalidate();
                }
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string FanLabel
        {
            get => _fanLabel;
            set { _fanLabel = value; SafeInvalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public List<Point> Points
        {
            get
            {
                lock (_pointsLock)
                {
                    return new List<Point>(_points);
                }
            }
            set
            {
                var norm = Normalize(value);
                lock (_pointsLock)
                {
                    _points = norm;
                }
                SafeInvalidate();
            }
        }

        private void SafeInvalidate()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(Invalidate)); } catch { }
            }
            else
            {
                Invalidate();
            }
        }

        private static readonly int[] s_defaultTemps = [30, 50, 60, 70, 77, 85, 90, 100];

        public static List<Point> Normalize(IEnumerable<Point>? src)
        {
            if (src == null) return new List<Point>(DefaultCpuPoints);

            var raw = src.ToList();
            if (raw.Count < 2) return new List<Point>(DefaultCpuPoints);

            var clamped = raw
                .Select(p => new Point(Math.Clamp(p.X, TempMin, TempMax), Math.Clamp(p.Y, SpeedMin, SpeedMax)))
                .OrderBy(p => p.X)
                .ToList();

            if (clamped.Count == ControlPointCount)
            {
                var pts = new List<Point>(clamped);
                pts[0] = new Point(TempMin, pts[0].Y);
                pts[ControlPointCount - 1] = new Point(TempMax, pts[ControlPointCount - 1].Y);

                for (int i = 1; i < ControlPointCount - 1; i++)
                {
                    int minX = pts[i - 1].X + 1;
                    if (pts[i].X < minX)
                        pts[i] = new Point(minX, pts[i].Y);
                }
                for (int i = ControlPointCount - 2; i >= 1; i--)
                {
                    int maxX = pts[i + 1].X - 1;
                    if (pts[i].X > maxX)
                        pts[i] = new Point(maxX, pts[i].Y);
                }
                for (int i = 1; i < ControlPointCount - 1; i++)
                {
                    int minX = pts[i - 1].X + 1;
                    if (pts[i].X < minX)
                        pts[i] = new Point(minX, pts[i].Y);
                }

                return pts;
            }
            else
            {
                int InterpolateClamped(int temp)
                {
                    if (temp <= clamped[0].X) return clamped[0].Y;
                    if (temp >= clamped[^1].X) return clamped[^1].Y;

                    for (int i = 0; i < clamped.Count - 1; i++)
                    {
                        if (temp >= clamped[i].X && temp <= clamped[i + 1].X)
                        {
                            int span = clamped[i + 1].X - clamped[i].X;
                            if (span <= 0) return clamped[i + 1].Y;
                            float t = (float)(temp - clamped[i].X) / span;
                            return (int)Math.Round(clamped[i].Y + t * (clamped[i + 1].Y - clamped[i].Y));
                        }
                    }
                    return clamped[^1].Y;
                }

                var resampled = new List<Point>(ControlPointCount);
                foreach (int t in s_defaultTemps)
                {
                    int spd = Math.Clamp(InterpolateClamped(t), SpeedMin, SpeedMax);
                    resampled.Add(new Point(t, spd));
                }
                return resampled;
            }
        }

        public static List<Point> Normalize(List<Point>? src) => Normalize((IEnumerable<Point>?)src);

        public List<Point> DefaultPoints => _fanLabel.Contains("GPU", StringComparison.OrdinalIgnoreCase)
            ? new List<Point>(DefaultGpuPoints)
            : new List<Point>(DefaultCpuPoints);

        private readonly Action _themeHandler;

        public FanCurveGraph()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);

            Size = new Size(420, 220);
            _points = new List<Point>(_defaultPoints);
            Cursor = Cursors.Default;

            _themeHandler = () =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    Invalidate();
                }
            };
            ThemeManager.ThemeChanged += _themeHandler;
        }

        private RectangleF GraphArea => new(
            PadLeft, PadTop,
            Math.Max(1f, Width - PadLeft - PadRight),
            Math.Max(1f, Height - PadTop - PadBottom));

        private float TempToX(int temp)
        {
            var g = GraphArea;
            float frac = (float)(temp - TempMin) / (TempMax - TempMin);
            return g.Left + frac * g.Width;
        }

        private float SpeedToY(int speed)
        {
            var g = GraphArea;
            float frac = (float)(speed - SpeedMin) / (SpeedMax - SpeedMin);
            return g.Bottom - frac * g.Height;        
        }

        private int XToTemp(float x)
        {
            var g = GraphArea;
            if (g.Width <= 0) return TempMin;
            float frac = Math.Clamp((x - g.Left) / g.Width, 0f, 1f);
            return TempMin + (int)Math.Round(frac * (TempMax - TempMin));
        }

        private int YToSpeed(float y)
        {
            var g = GraphArea;
            if (g.Height <= 0) return SpeedMin;
            float frac = Math.Clamp((g.Bottom - y) / g.Height, 0f, 1f);
            return SpeedMin + (int)Math.Round(frac * (SpeedMax - SpeedMin));
        }

        private PointF CurvePointToPixel(Point cp) => new(TempToX(cp.X), SpeedToY(cp.Y));

        public int InterpolateSpeed(int temperature)
        {
            lock (_pointsLock)
            {
                if (_points == null || _points.Count == 0) return 50;
                if (_points.Count == 1) return Math.Clamp(_points[0].Y, SpeedMin, SpeedMax);
                if (temperature <= _points[0].X) return Math.Clamp(_points[0].Y, SpeedMin, SpeedMax);
                if (temperature >= _points[^1].X) return Math.Clamp(_points[^1].Y, SpeedMin, SpeedMax);

                for (int i = 0; i < _points.Count - 1; i++)
                {
                    if (temperature >= _points[i].X && temperature <= _points[i + 1].X)
                    {
                        float span = _points[i + 1].X - _points[i].X;
                        if (span <= 0) return Math.Clamp(_points[i].Y, SpeedMin, SpeedMax);
                        float t = (float)(temperature - _points[i].X) / span;
                        int result = (int)Math.Round(_points[i].Y + t * (_points[i + 1].Y - _points[i].Y));
                        return Math.Clamp(result, SpeedMin, SpeedMax);
                    }
                }
                return Math.Clamp(_points[^1].Y, SpeedMin, SpeedMax);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackgroundColor);

            var area = GraphArea;

            lock (_pointsLock)
            {
                DrawGrid(g, area);
                DrawAxisLabels(g, area);
                DrawTitle(g, area);
                DrawStatus(g, area);
                DrawCurveFill(g, area);
                DrawCurveLine(g);
                DrawCrosshair(g, area);
                DrawControlPoints(g);
                DrawDragTooltip(g);
            }

            using var borderPen = new Pen(BorderColor, 1f);
            g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
        }

        private void DrawGrid(Graphics g, RectangleF area)
        {
            using var pen = new Pen(GridColor, 1f);

            for (int temp = TempMin; temp <= TempMax; temp += 10)
            {
                float x = TempToX(temp);
                g.DrawLine(pen, x, area.Top, x, area.Bottom);
            }

            for (int speed = SpeedMin; speed <= SpeedMax; speed += 20)
            {
                float y = SpeedToY(speed);
                g.DrawLine(pen, area.Left, y, area.Right, y);
            }
        }

        private void DrawAxisLabels(Graphics g, RectangleF area)
        {
            using var brush = new SolidBrush(LabelColor);

            for (int temp = TempMin; temp <= TempMax; temp += 10)
            {
                float x = TempToX(temp);
                g.DrawString($"{temp}°", s_fontAxis, brush, x, area.Bottom + 4, s_sfCenter);
            }

            for (int speed = SpeedMin; speed <= SpeedMax; speed += 20)
            {
                float y = SpeedToY(speed);
                g.DrawString($"{speed}%", s_fontAxis, brush, area.Left - 4, y, s_sfRight);
            }
        }

        private void DrawTitle(Graphics g, RectangleF area)
        {
            using var brush = new SolidBrush(_curveColor);
            g.DrawString(_fanLabel, s_fontTitle, brush, area.Left, area.Top - 22);
        }

        private void DrawStatus(Graphics g, RectangleF area)
        {
            string status = _currentTemp > 0
                ? $"{_currentTemp}°C → {InterpolateSpeed(_currentTemp)}%"
                : (_fanLabel.Contains("GPU", StringComparison.OrdinalIgnoreCase) ? "Asleep (D3Cold)" : "Offline");

            using var brush = new SolidBrush(_curveColor);
            g.DrawString(status, s_fontStatus, brush, area.Right, area.Top - 22, s_sfFar);
        }

        private void DrawCurveFill(Graphics g, RectangleF area)
        {
            if (_points.Count < 2) return;

            using var path = new GraphicsPath();
            var pixels = _points.Select(CurvePointToPixel).ToArray();

            path.AddLines(pixels);
            path.AddLine(pixels[^1].X, pixels[^1].Y, pixels[^1].X, area.Bottom);
            path.AddLine(pixels[^1].X, area.Bottom, pixels[0].X, area.Bottom);
            path.CloseFigure();

            using var fillBrush = new LinearGradientBrush(
                new PointF(0, area.Top),
                new PointF(0, area.Bottom),
                Color.FromArgb(25, _curveColor),
                Color.FromArgb(5, _curveColor));

            g.FillPath(fillBrush, path);
        }

        private void DrawCurveLine(Graphics g)
        {
            if (_points.Count < 2) return;

            var pixels = _points.Select(CurvePointToPixel).ToArray();

            using var pen = new Pen(_curveColor, 2f);
            g.DrawLines(pen, pixels);
        }

        private void DrawCrosshair(Graphics g, RectangleF area)
        {
            if (_currentTemp < TempMin || _currentTemp > TempMax) return;

            float x = TempToX(_currentTemp);
            int speed = InterpolateSpeed(_currentTemp);
            float y = SpeedToY(speed);

            using var pen = new Pen(Color.FromArgb(100, _curveColor), 1f)
            {
                DashStyle = DashStyle.Dash
            };

            g.DrawLine(pen, x, area.Top, x, area.Bottom);
            g.DrawLine(pen, area.Left, y, area.Right, y);

            const float d = 4f;
            PointF[] diamond =
            {
                new(x, y - d),
                new(x + d, y),
                new(x, y + d),
                new(x - d, y)
            };
            using var fill = new SolidBrush(_curveColor);
            g.FillPolygon(fill, diamond);
        }

        private void DrawControlPoints(Graphics g)
        {
            for (int i = 0; i < _points.Count; i++)
            {
                var px = CurvePointToPixel(_points[i]);
                int r = (i == _hoverIndex || i == _dragIndex) ? PointRadiusHover : PointRadius;

                using var fill = new SolidBrush(_curveColor);
                g.FillEllipse(fill, px.X - r, px.Y - r, r * 2, r * 2);

                using var border = new Pen(Color.White, 1f);
                g.DrawEllipse(border, px.X - r, px.Y - r, r * 2, r * 2);
            }
        }

        private void DrawDragTooltip(Graphics g)
        {
            if (_dragIndex < 0 || _dragIndex >= _points.Count) return;

            var pt = _points[_dragIndex];
            var px = CurvePointToPixel(pt);
            string text = $"{pt.X}°C, {pt.Y}%";

            var sz = g.MeasureString(text, s_fontTooltip);

            float tx = px.X - sz.Width / 2;
            float ty = px.Y - PointRadiusHover - sz.Height - 6;

            tx = Math.Max(PadLeft, Math.Min(tx, Width - PadRight - sz.Width));
            ty = Math.Max(2, ty);

            using var bgBrush = new SolidBrush(Color.FromArgb(220, 22, 22, 26));
            using var fgBrush = new SolidBrush(_curveColor);
            g.FillRectangle(bgBrush, tx - 3, ty - 1, sz.Width + 6, sz.Height + 2);
            g.DrawString(text, s_fontTooltip, fgBrush, tx, ty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                int index = HitTestPoint(e.Location);
                if (index >= 0)
                {
                    _dragIndex = index;
                    Capture = true;
                    Cursor = Cursors.Hand;
                    Invalidate();
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragIndex >= 0 && Enabled)
            {
                UpdateDraggedPoint(e.Location);
            }
            else if (Enabled)
            {
                int newHover = HitTestPoint(e.Location);
                if (newHover != _hoverIndex)
                {
                    _hoverIndex = newHover;
                    Cursor = _hoverIndex >= 0 ? Cursors.Hand : Cursors.Default;
                    Invalidate();
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragIndex >= 0)
            {
                _dragIndex = -1;
                Capture = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverIndex >= 0)
            {
                _hoverIndex = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        private int HitTestPoint(Point mousePos)
        {
            lock (_pointsLock)
            {
                for (int i = 0; i < _points.Count; i++)
                {
                    var px = CurvePointToPixel(_points[i]);
                    float dx = mousePos.X - px.X;
                    float dy = mousePos.Y - px.Y;
                    if (dx * dx + dy * dy <= HitTestRadius * HitTestRadius)
                        return i;
                }
                return -1;
            }
        }

        private void UpdateDraggedPoint(Point mousePos)
        {
            bool changed = false;
            lock (_pointsLock)
            {
                if (_dragIndex < 0 || _dragIndex >= _points.Count) return;

                int temp = XToTemp(mousePos.X);
                int speed = YToSpeed(mousePos.Y);
                if ((ModifierKeys & Keys.Control) == Keys.Control)
                {
                    speed = (int)(Math.Round(speed / 5.0) * 5.0);
                }
                speed = Math.Clamp(speed, SpeedMin, SpeedMax);

                if (_dragIndex == 0)
                {
                    temp = TempMin;
                }
                else if (_dragIndex == _points.Count - 1)
                {
                    temp = TempMax;
                }
                else
                {
                    temp = Math.Clamp(temp, TempMin, TempMax);

                    int lo = _points[_dragIndex - 1].X + MinTempGap;
                    int hi = _points[_dragIndex + 1].X - MinTempGap;
                    temp = lo > hi ? (lo + hi) / 2 : Math.Clamp(temp, lo, hi);
                }

                var updated = new Point(temp, speed);
                if (_points[_dragIndex] != updated)
                {
                    _points[_dragIndex] = updated;
                    changed = true;
                }
            }

            if (changed)
            {
                CurveChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;
            }
            base.Dispose(disposing);
        }
    }
}
