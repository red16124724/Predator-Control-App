using System;
using System.Collections.Generic;
using System.Drawing;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace PredatorControlApp
{
    /// <summary>
    /// Click-and-drag keyboard map used by the per-key lighting editor.
    /// Painting is intentionally immediate in the UI; the controller debounces
    /// the hardware write so dragging cannot flood the EC.
    /// </summary>
    public sealed class KeyboardRgbMap : Control
    {
        private static readonly StringFormat s_centeredFormat = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        private IReadOnlyList<KeyboardKey> _keys;
        private double _totalWidthUnits;
        private Color[] _colors;
        private Font? _labelFont;
        private float _labelFontSize = 7f;
        private int _hoverIndex = -1;
        private int _selectedIndex = -1;
        private bool _painting;

        public event EventHandler<int>? KeyPainted;
        public event EventHandler<int>? KeySelected;
        public event EventHandler<int>? KeyRightClicked;

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color UnpaintedColor { get; set; } = Color.FromArgb(32, 36, 44);
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color BorderColor { get; set; } = Color.FromArgb(60, 66, 78);
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color TextColor { get; set; } = Color.FromArgb(210, 216, 226);
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color AccentColor { get; set; } = Color.FromArgb(0, 170, 255);
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color HoverColor { get; set; } = Color.FromArgb(90, 100, 116);

        public KeyboardRgbMap(string? layoutId = null)
        {
            string effectiveId = string.IsNullOrWhiteSpace(layoutId) ? KeyboardLayouts.FullAnsiId : layoutId;
            _keys = KeyboardLayouts.Get(effectiveId);
            _totalWidthUnits = string.Equals(effectiveId, KeyboardLayouts.AnsiTklId, StringComparison.OrdinalIgnoreCase)
                ? KeyboardLayouts.AnsiTklWidthUnits
                : KeyboardLayouts.FullAnsiWidthUnits;
            _colors = new Color[_keys.Count];
            for (int i = 0; i < _colors.Length; i++) _colors[i] = Color.Black;

            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(18, 20, 25);
            TabStop = true;
        }

        public int KeyCount => _keys.Count;

        public int SelectedIndex => _selectedIndex;

        public IReadOnlyList<KeyboardKey> Keys => _keys;

        public double TotalWidthUnits => _totalWidthUnits;

        public void SetLayout(IReadOnlyList<KeyboardKey> keys, double widthUnits)
        {
            if (keys == null || keys.Count == 0) return;
            _keys = keys;
            _totalWidthUnits = widthUnits > 0 ? widthUnits : KeyboardLayouts.FullAnsiWidthUnits;
            var newColors = new Color[_keys.Count];
            int count = Math.Min(_colors.Length, newColors.Length);
            for (int i = 0; i < count; i++) newColors[i] = _colors[i];
            for (int i = count; i < newColors.Length; i++) newColors[i] = Color.Black;
            _colors = newColors;
            if (_selectedIndex >= _keys.Count) _selectedIndex = -1;
            if (_hoverIndex >= _keys.Count) _hoverIndex = -1;
            Invalidate();
        }

        public void SetLayout(string? layoutId)
        {
            var keys = KeyboardLayouts.Get(layoutId);
            double width = string.Equals(layoutId, KeyboardLayouts.AnsiTklId, StringComparison.OrdinalIgnoreCase)
                ? KeyboardLayouts.AnsiTklWidthUnits
                : KeyboardLayouts.FullAnsiWidthUnits;
            SetLayout(keys, width);
        }

        public int KeyIndexAt(Point point) => HitTest(point);

        public int KeyIndexAt(int x, int y) => HitTest(new Point(x, y));

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color[] Colors
        {
            get => (Color[])_colors.Clone();
            set
            {
                if (value == null) return;
                int count = Math.Min(value.Length, _colors.Length);
                for (int i = 0; i < count; i++) _colors[i] = value[i];
                Invalidate();
            }
        }

        public void SetColor(int index, Color color)
        {
            if (index < 0 || index >= _colors.Length) return;
            _colors[index] = color;
            Invalidate(KeyBounds(index));
        }

        public void FillAll(Color color)
        {
            for (int i = 0; i < _colors.Length; i++) _colors[i] = color;
            Invalidate();
        }

        public void SelectKey(int index)
        {
            if (index < -1 || index >= _keys.Count) return;
            _selectedIndex = index;
            Invalidate();
            if (index >= 0) KeySelected?.Invoke(this, index);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var bg = new SolidBrush(BackColor);
            g.FillRectangle(bg, ClientRectangle);

            if (ClientSize.Width <= 4 || ClientSize.Height <= 4 || _totalWidthUnits <= 0 || _keys.Count == 0) return;

            int maxRow = _keys.Max(k => k.Row);
            float unitW = ClientSize.Width / (float)_totalWidthUnits;
            float rowH = ClientSize.Height / (float)(maxRow + 1);
            _labelFontSize = Math.Clamp(rowH * 0.26f, 5.5f, 9.5f);

            using var fillBrush = new SolidBrush(Color.Black);
            using var borderPen = new Pen(Color.Black);
            using var textBrush = new SolidBrush(Color.White);

            for (int i = 0; i < _keys.Count; i++)
            {
                var key = _keys[i];
                RectangleF rect = KeyRect(key, unitW, rowH);
                if (rect.Width <= 1 || rect.Height <= 1) continue;

                Color fill = _colors[i];
                bool isPainted = fill.A != 0 && (fill.R | fill.G | fill.B) != 0;
                Color drawFill = isPainted ? fill : UnpaintedColor;

                using (var path = RoundedRect(rect, Math.Min(4f, rect.Height * 0.18f)))
                {
                    fillBrush.Color = drawFill;
                    g.FillPath(fillBrush, path);

                    Color border = i == _selectedIndex
                        ? AccentColor
                        : (i == _hoverIndex ? HoverColor : BorderColor);
                    borderPen.Color = border;
                    borderPen.Width = i == _selectedIndex ? 2f : 1f;
                    g.DrawPath(borderPen, path);
                }

                if (rect.Height >= 12f && rect.Width >= 14f)
                {
                    string label = key.Label;
                    if (label.Equals("PredatorSense", StringComparison.OrdinalIgnoreCase))
                    {
                        label = "Sense";
                    }
                    else if (label.Length > 6)
                    {
                        label = label.Substring(0, 6);
                    }
                    textBrush.Color = ContrastText(drawFill);
                    g.DrawString(label, LabelFont, textBrush, rect, s_centeredFormat);
                }
            }
        }

        private Font LabelFont
        {
            get
            {
                if (_labelFont == null || Math.Abs(_labelFont.Size - _labelFontSize) > 0.2f)
                {
                    _labelFont?.Dispose();
                    _labelFont = new Font("Segoe UI", _labelFontSize, FontStyle.Bold, GraphicsUnit.Point);
                }
                return _labelFont;
            }
        }

        private static Color ContrastText(Color background)
        {
            double luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
            return luminance > 0.55 ? Color.FromArgb(18, 20, 25) : Color.FromArgb(235, 238, 244);
        }

        private RectangleF KeyRect(KeyboardKey key, float unitW, float rowH)
        {
            const float gap = 1.5f;
            return new RectangleF(
                (float)(key.XUnits * unitW) + gap,
                key.Row * rowH + gap,
                Math.Max(1f, (float)(key.WidthUnits * unitW) - gap * 2f),
                Math.Max(1f, rowH - gap * 2f));
        }

        private Rectangle KeyBounds(int index)
        {
            if (index < 0 || index >= _keys.Count || _totalWidthUnits <= 0) return Rectangle.Empty;
            int maxRow = _keys.Max(k => k.Row);
            float unitW = ClientSize.Width / (float)_totalWidthUnits;
            float rowH = ClientSize.Height / (float)(maxRow + 1);
            RectangleF rect = KeyRect(_keys[index], unitW, rowH);
            return Rectangle.Round(rect);
        }

        private int HitTest(Point point)
        {
            if (_keys.Count == 0 || _totalWidthUnits <= 0) return -1;
            int maxRow = _keys.Max(k => k.Row);
            float unitW = ClientSize.Width / (float)_totalWidthUnits;
            float rowH = ClientSize.Height / (float)(maxRow + 1);
            for (int i = 0; i < _keys.Count; i++)
            {
                if (KeyRect(_keys[i], unitW, rowH).Contains(point)) return i;
            }
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int index = HitTest(e.Location);
            if (index < 0) return;

            if (e.Button == MouseButtons.Right)
            {
                SelectKey(index);
                KeyRightClicked?.Invoke(this, index);
                return;
            }

            SelectKey(index);
            _painting = true;
            KeyPainted?.Invoke(this, index);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTest(e.Location);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }

            if (_painting && index >= 0)
            {
                KeyPainted?.Invoke(this, index);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _painting = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_keys.Count == 0) return;

            if (e.KeyCode == System.Windows.Forms.Keys.Left || e.KeyCode == System.Windows.Forms.Keys.Right)
            {
                int next = Math.Clamp(_selectedIndex + (e.KeyCode == System.Windows.Forms.Keys.Left ? -1 : 1), 0, _keys.Count - 1);
                SelectKey(next);
                e.Handled = true;
            }
            else if (e.KeyCode == System.Windows.Forms.Keys.Space || e.KeyCode == System.Windows.Forms.Keys.Enter)
            {
                if (_selectedIndex >= 0)
                {
                    KeyPainted?.Invoke(this, _selectedIndex);
                    e.Handled = true;
                }
            }
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0.1f)
            {
                path.AddRectangle(rect);
                return path;
            }

            float d = radius * 2f;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _labelFont?.Dispose();
                _labelFont = null;
            }
            base.Dispose(disposing);
        }
    }
}
