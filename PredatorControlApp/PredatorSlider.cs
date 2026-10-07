using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class PredatorSlider : Control
    {
        private int _value = 100;
        private int _minimum = 0;
        private int _maximum = 100;
        private bool _isDragging;

        private static Color TrackBg => ThemeManager.ControlBorder;
        private static Color FillColor => ThemeManager.Accent;
        private static Color ThumbColor => ThemeManager.Accent;
        private static Color GlowColor => Color.FromArgb(40, ThemeManager.Accent);

        private const int TrackHeight = 4;
        private const int ThumbRadius = 7;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, _minimum, _maximum);
                if (clamped != _value) { _value = clamped; Invalidate(); }
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Minimum
        {
            get => _minimum;
            set
            {
                _minimum = value;
                if (_maximum < _minimum) _maximum = _minimum;
                _value = Math.Clamp(_value, _minimum, _maximum);
                Invalidate();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = value;
                if (_minimum > _maximum) _minimum = _maximum;
                _value = Math.Clamp(_value, _minimum, _maximum);
                Invalidate();
            }
        }

        public event EventHandler? ValueChanged;

        public event EventHandler? ValueCommitted;

        private readonly Action _themeHandler;

        public PredatorSlider()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.Selectable, true);

            Size = new Size(300, 28);
            Cursor = Cursors.Hand;

            _themeHandler = () =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    try { Invalidate(); } catch { }
                }
            };
            ThemeManager.ThemeChanged += _themeHandler;
        }

        private int TrackLeft => ThumbRadius;
        private int TrackRight => Width - ThumbRadius;
        private int TrackWidth => TrackRight - TrackLeft;
        private float Fraction => (_maximum > _minimum) ? (float)(_value - _minimum) / (_maximum - _minimum) : 0;
        private int ThumbX => TrackLeft + (int)(TrackWidth * Fraction);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ThemeManager.CardBg);

            int cy = Height / 2;
            int trackY = cy - TrackHeight / 2;
            int fillWidth = ThumbX - TrackLeft;

            Color currentFill = Enabled ? FillColor : ThemeManager.ControlDisabled;
            Color currentThumb = Enabled ? ThumbColor : ThemeManager.TextMuted;

            using (var brush = new SolidBrush(TrackBg))
                g.FillRectangle(brush, TrackLeft, trackY, TrackWidth, TrackHeight);

            if (fillWidth > 0)
            {
                using var brush = new SolidBrush(currentFill);
                g.FillRectangle(brush, TrackLeft, trackY, fillWidth, TrackHeight);
            }

            if (Enabled)
            {
                using var glowBrush = new SolidBrush(GlowColor);
                int glowR = ThumbRadius + 3;
                g.FillEllipse(glowBrush, ThumbX - glowR, cy - glowR, glowR * 2, glowR * 2);
            }

            using (var thumbBrush = new SolidBrush(currentThumb))
                g.FillEllipse(thumbBrush, ThumbX - ThumbRadius, cy - ThumbRadius, ThumbRadius * 2, ThumbRadius * 2);

            int innerR = ThumbRadius - 3;
            if (innerR > 0 && Enabled)
            {
                using var innerBrush = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
                g.FillEllipse(innerBrush, ThumbX - innerR, cy - innerR, innerR * 2, innerR * 2);
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                _isDragging = true;
                Capture = true;
                UpdateValueFromMouse(e.X);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isDragging && Enabled)
            {
                UpdateValueFromMouse(e.X);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                Capture = false;
                ValueCommitted?.Invoke(this, EventArgs.Empty);
            }
            base.OnMouseUp(e);
        }

        private void UpdateValueFromMouse(int mouseX)
        {
            if (TrackWidth <= 0) return;
            float fraction = Math.Clamp((float)(mouseX - TrackLeft) / TrackWidth, 0f, 1f);
            int newVal = _minimum + (int)Math.Round(fraction * (_maximum - _minimum));
            if (newVal != _value)
            {
                _value = newVal;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (!Enabled) return base.ProcessDialogKey(keyData);

            int step = Math.Max(1, (_maximum - _minimum) / 20);
            switch (keyData)
            {
                case Keys.Left:
                case Keys.Down:
                    Value = Math.Max(_minimum, _value - step);
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                    ValueCommitted?.Invoke(this, EventArgs.Empty);
                    return true;
                case Keys.Right:
                case Keys.Up:
                    Value = Math.Min(_maximum, _value + step);
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                    ValueCommitted?.Invoke(this, EventArgs.Empty);
                    return true;
                case Keys.Home:
                    Value = _minimum;
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                    ValueCommitted?.Invoke(this, EventArgs.Empty);
                    return true;
                case Keys.End:
                    Value = _maximum;
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                    ValueCommitted?.Invoke(this, EventArgs.Empty);
                    return true;
            }
            return base.ProcessDialogKey(keyData);
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
