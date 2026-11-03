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

        private float DpiScale => DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
        private int TrackHeight => Math.Max(2, (int)Math.Round(4 * DpiScale));
        private int ThumbRadius => Math.Max(5, (int)Math.Round(7 * DpiScale));

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                int min = Math.Min(_minimum, _maximum);
                int max = Math.Max(_minimum, _maximum);
                int clamped = Math.Clamp(value, min, max);
                if (clamped != _value)
                {
                    _value = clamped;
                    if (IsHandleCreated && InvokeRequired)
                    {
                        try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) { Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); } })); } catch { }
                    }
                    else
                    {
                        Invalidate();
                        ValueChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Minimum
        {
            get => _minimum;
            set
            {
                if (_minimum == value) return;
                if (IsHandleCreated && InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) Minimum = value; })); } catch { }
                    return;
                }
                _minimum = value;
                if (_maximum < _minimum) _maximum = _minimum;
                int clamped = Math.Clamp(_value, _minimum, _maximum);
                bool valChanged = (clamped != _value);
                _value = clamped;
                Invalidate();
                if (valChanged) ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Maximum
        {
            get => _maximum;
            set
            {
                if (_maximum == value) return;
                if (IsHandleCreated && InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) Maximum = value; })); } catch { }
                    return;
                }
                _maximum = value;
                if (_minimum > _maximum) _minimum = _maximum;
                int clamped = Math.Clamp(_value, _minimum, _maximum);
                bool valChanged = (clamped != _value);
                _value = clamped;
                Invalidate();
                if (valChanged) ValueChanged?.Invoke(this, EventArgs.Empty);
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
            TabStop = true;

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

        private int GlowOffset => Math.Max(2, (int)Math.Round(3 * DpiScale));
        private int TrackLeft => ThumbRadius + GlowOffset;
        private int TrackRight => Math.Max(ThumbRadius + GlowOffset + 1, Width - ThumbRadius - GlowOffset);
        private int TrackWidth => Math.Max(1, TrackRight - TrackLeft);
                private float Fraction
        {
            get
            {
                int range = Math.Max(1, Maximum - Minimum);
                float f = (float)(_value - Minimum) / range;
                return (float.IsNaN(f) || float.IsInfinity(f)) ? 0f : Math.Clamp(f, 0f, 1f);
            }
        }
        private int ThumbX => TrackLeft + (int)(TrackWidth * Fraction);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= ThumbRadius * 2 || Height <= TrackHeight) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color parentBg = ThemeManager.CardBg;
            try { parentBg = Parent?.BackColor ?? ThemeManager.CardBg; } catch { }
            if (parentBg.A < 255) parentBg = ThemeManager.CardBg;
            g.Clear(parentBg);

            int cy = Height / 2;
            int trackY = cy - TrackHeight / 2;
            int fillWidth = Math.Clamp(ThumbX - TrackLeft, 0, TrackWidth);

            Color currentFill = Enabled ? FillColor : ThemeManager.ControlDisabled;
            Color currentThumb = Enabled ? ThumbColor : ThemeManager.TextMuted;

            using (var brush = new SolidBrush(TrackBg))
                g.FillRectangle(brush, TrackLeft, trackY, TrackWidth, TrackHeight);

            if (fillWidth > 0)
            {
                using var brush = new SolidBrush(currentFill);
                g.FillRectangle(brush, TrackLeft, trackY, fillWidth, TrackHeight);
            }

            int glowOffset = Math.Max(2, (int)Math.Round(3 * DpiScale));
            if (Enabled)
            {
                using var glowBrush = new SolidBrush(GlowColor);
                int glowR = ThumbRadius + glowOffset;
                g.FillEllipse(glowBrush, ThumbX - glowR, cy - glowR, glowR * 2, glowR * 2);
            }

            using (var thumbBrush = new SolidBrush(currentThumb))
                g.FillEllipse(thumbBrush, ThumbX - ThumbRadius, cy - ThumbRadius, ThumbRadius * 2, ThumbRadius * 2);

            int innerR = ThumbRadius - glowOffset;
            if (innerR > 0 && Enabled)
            {
                using var innerBrush = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
                g.FillEllipse(innerBrush, ThumbX - innerR, cy - innerR, innerR * 2, innerR * 2);
            }

            if (Focused && ShowFocusCues && Width > 6 && Height > 6)
            {
                using var focusPen = new Pen(ThemeManager.FocusRing, 1f) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(focusPen, 1, 1, Width - 3, Height - 3);
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                Capture = false;
                ValueCommitted?.Invoke(this, EventArgs.Empty);
            }
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _isDragging)
            {
                _isDragging = false;
                ValueCommitted?.Invoke(this, EventArgs.Empty);
                Invalidate();
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
            if (TrackRight <= TrackLeft || Width <= ThumbRadius * 2 || TrackWidth <= 0) return;
            int minBound = Math.Min(Minimum, Maximum);
            int maxBound = Math.Max(Minimum, Maximum);
            int range = Math.Max(1, maxBound - minBound);
            float rawFrac = (float)(mouseX - TrackLeft) / TrackWidth;
            if (float.IsNaN(rawFrac) || float.IsInfinity(rawFrac)) rawFrac = 0f;
            float fraction = Math.Clamp(rawFrac, 0f, 1f);
            int newVal = Math.Clamp(minBound + (int)Math.Round(fraction * range), minBound, maxBound);
            if (newVal != _value)
            {
                _value = newVal;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Enabled) return;
            int minBound = Math.Min(Minimum, Maximum);
            int maxBound = Math.Max(Minimum, Maximum);
            int range = Math.Max(1, maxBound - minBound);
            int step = Math.Max(1, range / 20);
            int delta = e.Delta > 0 ? step : -step;
            int newVal = Math.Clamp(_value + delta, minBound, maxBound);
            if (newVal != _value)
            {
                _value = newVal;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
                ValueCommitted?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                    return true;
                default:
                    return base.IsInputKey(keyData);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Modifiers == Keys.None && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right ||
                e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) && ProcessDialogKey(e.KeyCode))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (!Enabled) return base.ProcessDialogKey(keyData);

            int minBound = Math.Min(Minimum, Maximum);
            int maxBound = Math.Max(Minimum, Maximum);
            int range = Math.Max(1, maxBound - minBound);
            int step = Math.Max(1, range / 20);
            int target = _value;

            switch (keyData)
            {
                case Keys.Left:
                case Keys.Down:
                    target = Math.Max(minBound, _value - step);
                    break;
                case Keys.Right:
                case Keys.Up:
                    target = Math.Min(maxBound, _value + step);
                    break;
                case Keys.Home:
                    target = minBound;
                    break;
                case Keys.End:
                    target = maxBound;
                    break;
                case Keys.PageUp:
                    target = Math.Min(maxBound, _value + step * 2);
                    break;
                case Keys.PageDown:
                    target = Math.Max(minBound, _value - step * 2);
                    break;
                default:
                    return base.ProcessDialogKey(keyData);
            }

            if (target != _value)
            {
                _value = target;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
                ValueCommitted?.Invoke(this, EventArgs.Empty);
            }
            return true;
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
