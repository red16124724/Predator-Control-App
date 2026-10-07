using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class PredatorDropDown : Control
    {
        private bool _isHover;
        private bool _isOpen;
        private bool _isClosingPopup; 
        private long _lastClosedTicks;
        private int _selectedIndex = -1;
        private readonly List<string> _items = new();
        private Form? _popup;
        private ListBox? _listBox;
        private int _hoverIndex = -1;
        private static readonly Font s_defaultFont = new("Segoe UI", 9.25f, FontStyle.Regular);
        private readonly Action _themeHandler;

        private static Color BgNormal => ThemeManager.ControlBg;
        private static Color BgHover => ThemeManager.ControlHover;
        private static Color BorderNormal => ThemeManager.ControlBorder;
        private static Color BorderHover => ThemeManager.BorderHover;
        private static Color Accent => ThemeManager.Accent;
        private static Color TextNormal => ThemeManager.TextPrimary;
        private static Color DropBg => ThemeManager.CardBg;
        private static Color DropHover => ThemeManager.ControlHover;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (value >= -1 && value < _items.Count && value != _selectedIndex)
                {
                    _selectedIndex = value;
                    Invalidate();
                    SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string SelectedText => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : "";

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public List<string> Items => _items;

        private bool _useMnemonic = false;

        [DefaultValue(false)]
        public bool UseMnemonic
        {
            get => _useMnemonic;
            set { _useMnemonic = value; Invalidate(); }
        }

        public event EventHandler? SelectedIndexChanged;

        public PredatorDropDown()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);

            Font = s_defaultFont;
            Size = new Size(180, 34);
            Cursor = Cursors.Hand;

            _themeHandler = () =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    if (_isOpen) ClosePopup();
                    try { Invalidate(); } catch { }
                }
            };
            ThemeManager.ThemeChanged += _themeHandler;
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? ThemeManager.CardBg);

            var rect = new Rectangle(1, 1, Width - 3, Height - 3);
            using var path = RoundedRect(rect, 6);

            Color bg = _isHover || _isOpen ? BgHover : BgNormal;
            Color border = _isHover || _isOpen ? BorderHover : BorderNormal;

            using (var bgBrush = new SolidBrush(bg))
                g.FillPath(bgBrush, path);

            using (var pen = new Pen(border, 1f))
                g.DrawPath(pen, path);

            string displayText = SelectedText;
            if (string.IsNullOrEmpty(displayText)) displayText = "Select...";
            var textRect = new Rectangle(12, 0, Width - 36, Height);
            var textFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            if (!_useMnemonic) textFlags |= TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, displayText, Font, textRect, TextNormal, textFlags);

            int arrowX = Width - 22;
            int arrowY = Height / 2 - 2;
            using var arrowPen = new Pen(ThemeManager.TextSecondary, 1.5f);
            arrowPen.StartCap = LineCap.Round;
            arrowPen.EndCap = LineCap.Round;
            g.DrawLine(arrowPen, arrowX, arrowY, arrowX + 5, arrowY + 4);
            g.DrawLine(arrowPen, arrowX + 5, arrowY + 4, arrowX + 10, arrowY);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _isHover = true; Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _isHover = false; Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (!Enabled) return;
            if (e.Button == MouseButtons.Left)
            {
                if (Environment.TickCount64 - _lastClosedTicks < 250) return;
                if (_isOpen) ClosePopup();
                else OpenPopup();
            }
            base.OnMouseClick(e);
        }

        private void OpenPopup()
        {
            if (_items.Count == 0 || _isOpen) return;

            _isOpen = true;
            _hoverIndex = -1;
            Invalidate();

            int itemHeight = 30;
            int popupHeight = Math.Min(_items.Count * itemHeight + 4, 300);

            var listBox = new ListBox
            {
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = itemHeight,
                BackColor = DropBg,
                ForeColor = TextNormal,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                Font = this.Font,
                Dock = DockStyle.Fill
            };

            foreach (var item in _items) listBox.Items.Add(item);
            if (_selectedIndex >= 0 && _selectedIndex < listBox.Items.Count) listBox.SelectedIndex = _selectedIndex;

            listBox.DrawItem += ListBox_DrawItem;
            listBox.MouseMove += ListBox_MouseMove;

            listBox.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                int idx = listBox.IndexFromPoint(e.Location);
                if (idx >= 0 && idx < _items.Count)
                {
                    _selectedIndex = idx;
                    Invalidate();
                    ClosePopup();
                    SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                }
            };

            var screenPt = this.PointToScreen(new Point(0, Height));

            var popup = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = screenPt,
                Size = new Size(this.Width, popupHeight),
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                BackColor = DropBg,
                TopMost = true
            };

            popup.Controls.Add(listBox);
            popup.Deactivate += (s, e) => ClosePopup();
            popup.Paint += (s, e) =>
            {
                using var pen = new Pen(ThemeManager.CardBorder, 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, popup.Width - 1, popup.Height - 1);
            };

            _popup = popup;
            _listBox = listBox;

            var parentForm = this.FindForm();
            if (parentForm == null || parentForm.IsDisposed)
            {
                _isOpen = false;
                _popup = null;
                _listBox = null;
                popup.Dispose();
                return;
            }
            popup.Show(parentForm);
            listBox.Focus();
        }

        private void ClosePopup()
        {
            if (_isClosingPopup) return;
            _isClosingPopup = true;

            try
            {
                _isOpen = false;
                _lastClosedTicks = Environment.TickCount64;
                Invalidate();

                var popup = _popup;
                var lb = _listBox;
                _popup = null;
                _listBox = null;

                if (lb != null)
                {
                    lb.DrawItem -= ListBox_DrawItem;
                    lb.MouseMove -= ListBox_MouseMove;
                }

                if (popup != null)
                {
                    popup.Close();
                    popup.Dispose();
                }
            }
            finally
            {
                _isClosingPopup = false;
            }
        }

        private void ListBox_MouseMove(object? sender, MouseEventArgs e)
        {
            if (sender is not ListBox lb) return;
            int idx = lb.IndexFromPoint(e.Location);
            if (idx != _hoverIndex)
            {
                _hoverIndex = idx;
                lb.Invalidate();
            }
        }

        private void ListBox_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _items.Count) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool isHovered = e.Index == _hoverIndex;
            bool isSelected = e.Index == _selectedIndex;

            Color bg = isHovered ? DropHover : DropBg;
            Color textCol = isSelected ? Accent : TextNormal;

            using (var bgBrush = new SolidBrush(bg))
                g.FillRectangle(bgBrush, e.Bounds);

            string text = _items[e.Index];
            var textRect = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 24, e.Bounds.Height);
            var itemFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
            if (!_useMnemonic) itemFlags |= TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, text, Font, textRect, textCol, itemFlags);

            if (isSelected)
            {
                int checkX = e.Bounds.Right - 24;
                int checkY = e.Bounds.Y + e.Bounds.Height / 2;
                using var checkPen = new Pen(Accent, 1.8f);
                checkPen.StartCap = LineCap.Round;
                checkPen.EndCap = LineCap.Round;
                g.DrawLine(checkPen, checkX, checkY, checkX + 4, checkY + 4);
                g.DrawLine(checkPen, checkX + 4, checkY + 4, checkX + 10, checkY - 4);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;
                ClosePopup();
            }
            base.Dispose(disposing);
        }
    }
}
