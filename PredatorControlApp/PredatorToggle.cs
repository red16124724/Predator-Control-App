#pragma warning disable WFO1000

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Windows.Forms;

namespace PredatorControlApp
{
    public class PredatorToggle : Control
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
                if (_checked != value)
                {
                    _checked = value;
                    _targetProgress = value ? 1f : 0f;
                    _animTimer.Start();
                    CheckedChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private readonly Action _themeHandler;

        public PredatorToggle()
        {
            this.DoubleBuffered = true;
            this.Size = new Size(48, 24);
            this.Cursor = Cursors.Hand;

            _knobProgress = 0f;
            _targetProgress = 0f;

            _animTimer = new System.Windows.Forms.Timer { Interval = AnimIntervalMs };
            _animTimer.Tick += OnAnimTick;

            _themeHandler = () =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    try { Invalidate(); } catch { }
                }
            };
            ThemeManager.ThemeChanged += _themeHandler;
        }

        private void OnAnimTick(object? sender, EventArgs e)
        {
            float diff = _targetProgress - _knobProgress;
            if (MathF.Abs(diff) < 0.005f)
            {
                _knobProgress = _targetProgress;
                _animTimer.Stop();
            }
            else
            {
                _knobProgress += diff * AnimSpeed;
                _knobProgress = Math.Clamp(_knobProgress, 0f, 1f);
            }
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _isHovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _isHovered = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled && ClientRectangle.Contains(e.Location))
                Checked = !Checked;
            base.OnMouseUp(e);
        }

        private static Color LerpColor(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ThemeManager.CardBg);

            float t = _knobProgress;

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

            int knobSize = Height - 6;
            float knobMinX = 3f;
            float knobMaxX = Width - knobSize - 3f;
            float knobX = knobMinX + (knobMaxX - knobMinX) * t;
            int knobY = 3;

            using (var knobBrush = new SolidBrush(knobColor))
                g.FillEllipse(knobBrush, knobX, knobY, knobSize, knobSize);

            float cx = knobX + knobSize / 2f;
            float cy = knobY + knobSize / 2f;
            float iconR = knobSize * 0.22f;

            if (t > 0.5f)
            {
                int alpha = (int)((t - 0.5f) * 2f * 255f);
                using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 255), 255, 255, 255), 1.8f);
                g.DrawLine(pen, cx, cy - iconR, cx, cy + iconR);
            }
            else
            {
                int alpha = (int)((0.5f - t) * 2f * 200f);
                Color iconOffColor = ThemeManager.IsDarkThemeActive
                    ? Color.FromArgb(Math.Clamp(alpha, 0, 255), 200, 200, 200)
                    : Color.FromArgb(Math.Clamp(alpha, 0, 255), 70, 75, 90);
                using var pen = new Pen(iconOffColor, 1.5f);
                g.DrawEllipse(pen, cx - iconR, cy - iconR, iconR * 2, iconR * 2);
            }
        }

        private static GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
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
                _animTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
