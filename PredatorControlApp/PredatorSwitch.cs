#pragma warning disable WFO1000 

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Windows.Forms;

namespace PredatorControlApp
{
    public class PredatorSwitch : Control
    {
        private bool _checked;
        private bool _isHovered;

        private float _knobProgress;          
        private float _targetProgress;
        private readonly System.Windows.Forms.Timer _animTimer;
        private const int AnimIntervalMs = 12; 
        private const float AnimSpeed = 0.12f; 

        public event EventHandler? CheckedChanged;

        [DefaultValue(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool Checked
        {
            get => _checked;
            set
            {
                if (IsHandleCreated && InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) Checked = value; })); } catch { }
                    return;
                }
                if (_checked != value)
                {
                    _checked = value;
                    _targetProgress = value ? 1f : 0f;
                    if (!IsHandleCreated)
                    {
                        _knobProgress = _targetProgress;
                    }
                    else
                    {
                        _animTimer.Start();
                    }
                    CheckedChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public void SetCheckedImmediate(bool value)
        {
            if (IsHandleCreated && InvokeRequired)
            {
                try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) SetCheckedImmediate(value); })); } catch { }
                return;
            }
            try { _animTimer.Stop(); } catch { }
            _checked = value;
            _targetProgress = value ? 1f : 0f;
            _knobProgress = _targetProgress;
            try { Invalidate(); } catch { }
        }

        private readonly Action _themeHandler;

        public PredatorSwitch()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.Selectable, true);

            this.DoubleBuffered = true;
            this.TabStop = true;
            this.Size = new Size(60, 30);
            this.Cursor = Cursors.Hand;

            _knobProgress = 0f;
            _targetProgress = 0f;

            _animTimer = new System.Windows.Forms.Timer { Interval = AnimIntervalMs };
            _animTimer.Tick += OnAnimTick;

            _themeHandler = () =>
            {
                if (!IsDisposed && !Disposing && IsHandleCreated)
                {
                    try
                    {
                        if (InvokeRequired)
                            BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) Invalidate(); }));
                        else
                            Invalidate();
                    }
                    catch { }
                }
            };
            ThemeManager.ThemeChanged += _themeHandler;
        }

        private void OnAnimTick(object? sender, EventArgs e)
        {
            if (IsDisposed || Disposing || !IsHandleCreated)
            {
                try { _animTimer.Stop(); } catch { }
                return;
            }
            if (float.IsNaN(_knobProgress) || float.IsInfinity(_knobProgress))
                _knobProgress = _targetProgress;
            float diff = _targetProgress - _knobProgress;
            if (float.IsNaN(diff) || float.IsInfinity(diff) || MathF.Abs(diff) < 0.005f)
            {
                _knobProgress = _targetProgress;
                try { _animTimer.Stop(); } catch { }
            }
            else
            {
                _knobProgress += diff * AnimSpeed;
                _knobProgress = (float.IsNaN(_knobProgress) || float.IsInfinity(_knobProgress)) ? _targetProgress : Math.Clamp(_knobProgress, 0f, 1f);
            }
            try { Invalidate(); } catch { }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _isHovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _isHovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled && ClientRectangle.Contains(e.Location))
            {
                Checked = !Checked;
            }
            base.OnMouseUp(e);
        }

        private static Color LerpColor(Color a, Color b, float t)
        {
            if (float.IsNaN(t) || float.IsInfinity(t)) t = 0f;
            t = Math.Clamp(t, 0f, 1f);
            int r = Math.Clamp((int)Math.Round(a.R + (b.R - a.R) * t), 0, 255);
            int g = Math.Clamp((int)Math.Round(a.G + (b.G - a.G) * t), 0, 255);
            int blue = Math.Clamp((int)Math.Round(a.B + (b.B - a.B) * t), 0, 255);
            return Color.FromArgb(r, g, blue);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 8 || Height <= 8) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color parentBg = ThemeManager.CardBg;
            try { parentBg = Parent?.BackColor ?? ThemeManager.CardBg; } catch { }
            if (parentBg.A < 255) parentBg = ThemeManager.CardBg;
            g.Clear(parentBg);

            float t = _knobProgress;
            if (float.IsNaN(t) || float.IsInfinity(t)) t = _targetProgress;
            t = Math.Clamp(t, 0f, 1f); 

            Color trackOff = ThemeManager.ControlBg;
            Color trackOn = ThemeManager.ControlActive;
            Color trackDisabled = ThemeManager.ControlDisabled;

            Color knobOff = ThemeManager.TextSecondary;
            Color knobOn = ThemeManager.Accent;
            Color knobDisabled = ThemeManager.TextMuted;

            Color trackColor = Enabled ? LerpColor(trackOff, trackOn, t) : trackDisabled;
            Color knobColor = Enabled ? LerpColor(knobOff, knobOn, t) : knobDisabled;

            Color borderColor = Enabled
                ? (_isHovered ? ThemeManager.BorderHover : ThemeManager.ControlBorder)
                : ThemeManager.BorderDisabled;

            using (var path = GetRoundedRectPath(new Rectangle(1, 1, Width - 3, Height - 3), Height / 2))
            {
                using var brush = new SolidBrush(trackColor);
                g.FillPath(brush, path);
                using var pen = new Pen(borderColor, 1f);
                g.DrawPath(pen, path);
            }

            float dpi = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
            float knobPad = Math.Max(2f, (float)Math.Round(3f * dpi));
            float knobSize = Math.Max(1f, Height - knobPad * 2f);
            float knobMinX = knobPad;
            float knobMaxX = Math.Max(knobMinX, Width - knobSize - knobPad);
            float knobX = knobMinX + (knobMaxX - knobMinX) * t;
            float knobY = knobPad;

            using (var knobBrush = new SolidBrush(knobColor))
            {
                g.FillEllipse(knobBrush, knobX, knobY, knobSize, knobSize);
            }

            float cx = knobX + knobSize / 2f;
            float cy = knobY + knobSize / 2f;

            if (t > 0.5f)
            {
                int iconAlpha = Math.Clamp((int)Math.Round((t - 0.5f) * 2f * 255f), 0, 255);
                float r = knobSize * 0.23f;
                PointF[] shieldPoints = new PointF[]
                {
                    new PointF(cx - r, cy - r),
                    new PointF(cx + r, cy - r),
                    new PointF(cx + r, cy - r * 0.1f),
                    new PointF(cx, cy + r * 1.1f),
                    new PointF(cx - r, cy - r * 0.1f),
                };

                using var pen = new Pen(Color.FromArgb(Math.Clamp(iconAlpha, 0, 255), 255, 255, 255), 1.5f);
                pen.LineJoin = LineJoin.Round;
                g.DrawPolygon(pen, shieldPoints);
                g.DrawLine(pen, cx, cy - r, cx, cy + r * 0.5f);
            }
            else
            {
                int iconAlpha = Math.Clamp((int)Math.Round((0.5f - t) * 2f * 255f), 0, 255);
                float h = knobSize * 0.28f;
                float w = knobSize * 0.18f;
                PointF[] boltPoints = new PointF[]
                {
                    new PointF(cx + w * 0.2f, cy - h),
                    new PointF(cx - w, cy + h * 0.1f),
                    new PointF(cx - w * 0.2f, cy + h * 0.1f),
                    new PointF(cx - w * 0.4f, cy + h),
                    new PointF(cx + w, cy - h * 0.1f),
                    new PointF(cx + w * 0.2f, cy - h * 0.1f)
                };

                using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(iconAlpha, 0, 255), 255, 215, 0));
                g.FillPolygon(brush, boltPoints);
            }

            if (Focused && ShowFocusCues && Width > 4 && Height > 4)
            {
                using var focusPen = new Pen(ThemeManager.FocusRing, 1f) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(focusPen, 0, 0, Width - 1, Height - 1);
            }

            base.OnPaint(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                Checked = !Checked;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        private static GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                path.AddRectangle(new Rectangle(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height)));
                return path;
            }
            if (rect.Width < 6 || rect.Height < 6)
            {
                path.AddRectangle(rect);
                return path;
            }
            int maxRadius = Math.Max(1, Math.Min(rect.Width, rect.Height) / 2);
            radius = Math.Clamp(radius, 1, maxRadius);
            int diameter = radius * 2;
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;
                _animTimer.Stop();
                _animTimer.Tick -= OnAnimTick;
                _animTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
