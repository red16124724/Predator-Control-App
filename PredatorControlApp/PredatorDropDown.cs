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
        private Form? _parentFormRef;
        private EventHandler? _parentStateChanged;
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
                if (IsHandleCreated && InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) SelectedIndex = value; })); } catch { }
                    return;
                }
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

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsOpen => _isOpen;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool DroppedDown => _isOpen;

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
                ControlStyles.Selectable |
                ControlStyles.ResizeRedraw, true);

            TabStop = true;
            Font = s_defaultFont;
            Size = new Size(180, 34);
            Cursor = Cursors.Hand;

            _themeHandler = () =>
            {
                if (!IsDisposed && !Disposing && IsHandleCreated)
                {
                    try
                    {
                        if (InvokeRequired)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (IsDisposed || Disposing) return;
                                if (_isOpen) ClosePopup();
                                try { Invalidate(); } catch { }
                            }));
                        }
                        else
                        {
                            if (_isOpen) ClosePopup();
                            Invalidate();
                        }
                    }
                    catch { }
                }
            };
            ThemeManager.ThemeChanged += _themeHandler;
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(new Rectangle(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)));
                return path;
            }
            if (bounds.Width < 6 || bounds.Height < 6)
            {
                path.AddRectangle(bounds);
                return path;
            }
            int maxRadius = Math.Max(1, Math.Min(bounds.Width, bounds.Height) / 2);
            int r = Math.Clamp(radius, 1, maxRadius);
            int d = r * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 4 || Height <= 4) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Color parentBg = ThemeManager.CardBg;
            try { parentBg = Parent?.BackColor ?? ThemeManager.CardBg; } catch { }
            if (parentBg.A < 255) parentBg = ThemeManager.CardBg;
            g.Clear(parentBg);

            var rect = new Rectangle(1, 1, Width - 3, Height - 3);
            using var path = RoundedRect(rect, 6);

            Color bg = !Enabled ? ThemeManager.ControlDisabled : (_isHover || _isOpen ? BgHover : BgNormal);
            Color border = !Enabled ? ThemeManager.BorderDisabled : (_isHover || _isOpen ? BorderHover : BorderNormal);
            Color textColor = !Enabled ? ThemeManager.TextMuted : TextNormal;
            Color arrowColor = !Enabled ? ThemeManager.TextMuted : ThemeManager.TextSecondary;

            using (var bgBrush = new SolidBrush(bg))
                g.FillPath(bgBrush, path);

            using (var pen = new Pen(border, 1f))
                g.DrawPath(pen, path);

            float dpi = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
            int padLeft = (int)Math.Round(12 * dpi);
            int arrowMarginRight = (int)Math.Round(22 * dpi);
            int padRight = arrowMarginRight + (int)Math.Round(8 * dpi);

            string displayText = SelectedText;
            if (string.IsNullOrEmpty(displayText)) displayText = "Select...";
            var textRect = new Rectangle(padLeft, 0, Math.Max(1, Width - padLeft - padRight), Math.Max(1, Height));
            var textFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            if (!_useMnemonic) textFlags |= TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, displayText, Font, textRect, textColor, textFlags);

            int arrowHalf = Math.Max(3, (int)Math.Round(5 * dpi));
            int arrowH = Math.Max(2, (int)Math.Round(4 * dpi));
            if (Width > arrowMarginRight && Height > arrowH * 2)
            {
                int arrowX = Width - arrowMarginRight;
                int arrowY = Height / 2 - arrowH / 2;
                using var arrowPen = new Pen(arrowColor, Math.Max(1.5f, 1.5f * dpi));
                arrowPen.StartCap = LineCap.Round;
                arrowPen.EndCap = LineCap.Round;
                g.DrawLine(arrowPen, arrowX, arrowY, arrowX + arrowHalf, arrowY + arrowH);
                g.DrawLine(arrowPen, arrowX + arrowHalf, arrowY + arrowH, arrowX + arrowHalf * 2, arrowY);
            }

            if (Focused && ShowFocusCues && Width > 6 && Height > 6)
            {
                using var focusPen = new Pen(ThemeManager.FocusRing, 1f) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(focusPen, 3, 3, Math.Max(1, Width - 7), Math.Max(1, Height - 7));
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
            if (!Enabled) { base.OnKeyDown(e); return; }
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || (e.KeyCode == Keys.Down && e.Alt))
            {
                if (_isOpen) ClosePopup();
                else OpenPopup();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                if (SelectedIndex > 0) SelectedIndex--;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down)
            {
                if (SelectedIndex < _items.Count - 1) SelectedIndex++;
                e.Handled = true;
            }
            base.OnKeyDown(e);
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

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible && _isOpen) ClosePopup();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            if (!Enabled && _isOpen) ClosePopup();
            Invalidate();
            base.OnEnabledChanged(e);
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
            if (_items.Count == 0 || _isOpen || !IsHandleCreated) return;

            _isOpen = true;
            _hoverIndex = -1;
            Invalidate();

            float dpi = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
            int itemHeight = Math.Max((int)Math.Round(30 * dpi), this.Font.Height + (int)Math.Round(10 * dpi));
            int visibleCount = Math.Min(_items.Count, 9);
            int popupHeight = visibleCount * itemHeight;

            var listBox = new ListBox
            {
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = itemHeight,
                BackColor = DropBg,
                ForeColor = TextNormal,
                BorderStyle = BorderStyle.None,
                IntegralHeight = true,
                Font = this.Font,
                Dock = DockStyle.Fill
            };

            foreach (var item in _items) listBox.Items.Add(item);
            if (_selectedIndex >= 0 && _selectedIndex < listBox.Items.Count)
            {
                listBox.SelectedIndex = _selectedIndex;
                if (_items.Count <= 9) listBox.TopIndex = 0;
            }

            listBox.DrawItem += ListBox_DrawItem;
            listBox.MouseMove += ListBox_MouseMove;
            listBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    ClosePopup();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    int idx = listBox.SelectedIndex;
                    if (idx >= 0 && idx < _items.Count)
                    {
                        bool changed = (_selectedIndex != idx);
                        _selectedIndex = idx;
                        Invalidate();
                        ClosePopup();
                        if (changed)
                        {
                            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    else
                    {
                        ClosePopup();
                    }
                    e.Handled = true;
                }
            };

            listBox.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                int idx = listBox.IndexFromPoint(e.Location);
                if (idx >= 0 && idx < _items.Count)
                {
                    bool changed = (_selectedIndex != idx);
                    _selectedIndex = idx;
                    Invalidate();
                    ClosePopup();
                    if (changed)
                    {
                        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            };

            Point screenPt;
            try
            {
                screenPt = this.PointToScreen(new Point(0, Height));
                var screen = Screen.FromControl(this);
                if (screen != null)
                {
                    if (screenPt.Y + popupHeight > screen.WorkingArea.Bottom)
                    {
                        screenPt = this.PointToScreen(new Point(0, -popupHeight));
                    }
                    int clampedX = Math.Clamp(screenPt.X, screen.WorkingArea.Left, Math.Max(screen.WorkingArea.Left, screen.WorkingArea.Right - this.Width));
                    int clampedY = Math.Clamp(screenPt.Y, screen.WorkingArea.Top, Math.Max(screen.WorkingArea.Top, screen.WorkingArea.Bottom - popupHeight));
                    screenPt = new Point(clampedX, clampedY);
                }
            }
            catch
            {
                ClosePopup();
                return;
            }

            var popup = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = screenPt,
                Size = new Size(Math.Max(50, this.Width), popupHeight),
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
                if (popup.Width > 1 && popup.Height > 1)
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

            _parentFormRef = parentForm;
            _parentStateChanged = (s, e) =>
            {
                if (!Visible || parentForm.WindowState == FormWindowState.Minimized || !parentForm.Visible)
                {
                    ClosePopup();
                }
            };
            parentForm.Resize += _parentStateChanged;
            parentForm.VisibleChanged += _parentStateChanged;

            try
            {
                if (!parentForm.Visible || !parentForm.IsHandleCreated || parentForm.WindowState == FormWindowState.Minimized)
                {
                    popup.Show();
                }
                else
                {
                    popup.Show(parentForm);
                }
                listBox.Focus();
                if (_items.Count <= 9 && listBox.Items.Count > 0) listBox.TopIndex = 0;
            }
            catch
            {
                ClosePopup();
            }
        }

        private void ClosePopup()
        {
            if (_isClosingPopup) return;
            if (IsHandleCreated && InvokeRequired)
            {
                try { BeginInvoke(new Action(ClosePopup)); } catch { }
                return;
            }
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

                if (_parentFormRef != null && _parentStateChanged != null)
                {
                    try
                    {
                        _parentFormRef.Resize -= _parentStateChanged;
                        _parentFormRef.VisibleChanged -= _parentStateChanged;
                    }
                    catch { }
                    _parentFormRef = null;
                    _parentStateChanged = null;
                }

                if (lb != null)
                {
                    lb.DrawItem -= ListBox_DrawItem;
                    lb.MouseMove -= ListBox_MouseMove;
                }

                if (popup != null)
                {
                    popup.Close();
                    popup.Dispose();
                    if (!IsDisposed && !Disposing && IsHandleCreated)
                    {
                        try { this.Focus(); } catch { }
                    }
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
            if (e.Index < 0 || e.Index >= _items.Count || e.Bounds.Width <= 0 || e.Bounds.Height <= 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool isHovered = e.Index == _hoverIndex;
            bool isSelected = e.Index == _selectedIndex;

            Color bg = isHovered ? DropHover : DropBg;
            Color textCol = isSelected ? Accent : TextNormal;

            using (var bgBrush = new SolidBrush(bg))
                g.FillRectangle(bgBrush, e.Bounds);

            float dpi = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
            int itemPadLeft = (int)Math.Round(12 * dpi);
            int itemPadRight = (int)Math.Round(36 * dpi);

            string text = _items[e.Index] ?? "";
            var textRect = new Rectangle(e.Bounds.X + itemPadLeft, e.Bounds.Y, Math.Max(1, e.Bounds.Width - itemPadLeft - itemPadRight), Math.Max(1, e.Bounds.Height));
            var itemFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
            if (!_useMnemonic) itemFlags |= TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, text, Font, textRect, textCol, itemFlags);

            if (isSelected && e.Bounds.Width >= 30)
            {
                int checkX = e.Bounds.Right - (int)Math.Round(24 * dpi);
                int checkY = e.Bounds.Y + e.Bounds.Height / 2;
                using var checkPen = new Pen(Accent, Math.Max(1.8f, 1.8f * dpi));
                checkPen.StartCap = LineCap.Round;
                checkPen.EndCap = LineCap.Round;
                int chkW = Math.Max(3, (int)Math.Round(4 * dpi));
                int chkH = Math.Max(7, (int)Math.Round(10 * dpi));
                g.DrawLine(checkPen, checkX, checkY, checkX + chkW, checkY + chkW);
                g.DrawLine(checkPen, checkX + chkW, checkY + chkW, checkX + chkH, checkY - chkW);
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
