using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class PredatorSlider : Control
    {
        private int _value = 100;
        private int _minimum = 0;
        private int _maximum = 100;
        private bool _isDragging;

        private static readonly Color TrackBg = Color.FromArgb(55, 55, 62);
        private static readonly Color FillColor = Color.FromArgb(0, 200, 160);
        private static readonly Color ThumbColor = Color.FromArgb(0, 200, 160);
        private static readonly Color GlowColor = Color.FromArgb(50, 0, 200, 160);

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

        public PredatorSlider()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.Selectable, true);

            Size = new Size(300, 28);
            Cursor = Cursors.Hand;
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
            g.Clear(Parent?.BackColor ?? Color.FromArgb(30, 30, 30));

            int cy = Height / 2;
            int trackY = cy - TrackHeight / 2;
            int fillWidth = ThumbX - TrackLeft;

            Color currentFill = Enabled ? FillColor : Color.FromArgb(50, 50, 55);
            Color currentThumb = Enabled ? ThumbColor : Color.FromArgb(80, 80, 85);

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
                using var innerBrush = new SolidBrush(Color.FromArgb(80, 255, 255, 255));
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
            if (_isDragging && Enabled) UpdateValueFromMouse(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                Capture = false;
                if (Enabled) ValueCommitted?.Invoke(this, EventArgs.Empty);
            }
            base.OnMouseUp(e);
        }

        private void UpdateValueFromMouse(int mouseX)
        {
            if (TrackWidth <= 0) return;
            float fraction = (float)(mouseX - TrackLeft) / TrackWidth;
            fraction = Math.Clamp(fraction, 0f, 1f);
            int newValue = _minimum + (int)Math.Round(fraction * (_maximum - _minimum));
            if (newValue != _value)
            {
                _value = newValue;
                ValueChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!Enabled) return;
            int step = 1;
            int oldVal = _value;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down)
            {
                Value = Math.Max(_minimum, _value - step);
                if (_value != oldVal)
                {
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                    ValueCommitted?.Invoke(this, EventArgs.Empty);
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up)
            {
                Value = Math.Min(_maximum, _value + step);
                if (_value != oldVal)
                {
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                    ValueCommitted?.Invoke(this, EventArgs.Empty);
                }
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }
    }
}
