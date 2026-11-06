using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class PredatorButton : Control, IButtonControl
    {
        private bool _isHover;
        private bool _isActive;
        private bool _isNavButton;
        private Color? _customActiveColor;
        private DialogResult _dialogResult = DialogResult.None;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DialogResult DialogResult
        {
            get => _dialogResult;
            set => _dialogResult = value;
        }

        public void NotifyDefault(bool value)
        {
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (IsHandleCreated && InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) IsActive = value; })); } catch { }
                    return;
                }
                _isActive = value;
                Invalidate();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsNavButton
        {
            get => _isNavButton;
            set
            {
                if (IsHandleCreated && InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) IsNavButton = value; })); } catch { }
                    return;
                }
                _isNavButton = value;
                Invalidate();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color? CustomActiveColor
        {
            get => _customActiveColor;
            set
            {
                if (IsHandleCreated && InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) CustomActiveColor = value; })); } catch { }
                    return;
                }
                _customActiveColor = value;
                Invalidate();
            }
        }

        private bool _useMnemonic = false;

        [DefaultValue(false)]
        public bool UseMnemonic
        {
            get => _useMnemonic;
            set
            {
                if (IsHandleCreated && InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) UseMnemonic = value; })); } catch { }
                    return;
                }
                _useMnemonic = value;
                Invalidate();
            }
        }

        private static readonly Font s_defaultFont = new("Segoe UI", 9.25f, FontStyle.Regular);
        private Font? _boldFont;

        private Font GetBoldFont()
        {
            if (_boldFont == null || _boldFont.FontFamily.Name != Font.FontFamily.Name || Math.Abs(_boldFont.Size - Font.Size) > 0.01f)
            {
                if (_boldFont != null && _boldFont != Font)
                {
                    _boldFont.Dispose();
                }
                try
                {
                    _boldFont = new Font(Font, FontStyle.Bold);
                }
                catch
                {
                    _boldFont = Font;
                }
            }
            return _boldFont;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            if (_boldFont != null && _boldFont != Font)
            {
                _boldFont.Dispose();
            }
            _boldFont = null;
            base.OnFontChanged(e);
        }

        private readonly Action _themeHandler;

        public PredatorButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable, true);

            TabStop = true;
            Font = s_defaultFont;
            Size = new Size(96, 40);
            Cursor = Cursors.Hand;

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

            if (_isNavButton)
            {
                // Sleek OpenSense-style navigation rail button
                Color navBg = !Enabled
                    ? ThemeManager.ControlDisabled
                    : (_isActive
                        ? ThemeManager.ControlActive
                        : (_isHover ? ThemeManager.ControlHover : Color.Transparent));

                Color navText = !Enabled
                    ? ThemeManager.TextMuted
                    : (_isActive
                        ? (_customActiveColor ?? (ThemeManager.IsDarkThemeActive ? ThemeManager.Accent : Color.White))
                        : (_isHover ? ThemeManager.TextPrimary : ThemeManager.TextSecondary));

                float dpi = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
                int barPadTop = (int)Math.Round(6 * dpi);
                int barW = Math.Max(2, (int)Math.Round(3 * dpi));
                int barX = (int)Math.Round(4 * dpi);

                using (var path = RoundedRect(new Rectangle(barX, (int)Math.Round(2 * dpi), Math.Max(1, Width - barX * 2), Math.Max(1, Height - (int)Math.Round(4 * dpi))), Math.Max(2, (int)Math.Round(6 * dpi))))
                {
                    if (navBg != Color.Transparent)
                    {
                        using var brush = new SolidBrush(navBg);
                        g.FillPath(brush, path);
                    }

                    if (_isActive && Height > barPadTop * 2)
                    {
                        // Left vertical accent indicator bar
                        using var accBrush = new SolidBrush(ThemeManager.Accent);
                        g.FillRectangle(accBrush, barX, barPadTop, barW, Height - barPadTop * 2);
                    }
                }

                Font navFont = _isActive ? GetBoldFont() : Font;
                var navFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                if (!_useMnemonic) navFlags |= TextFormatFlags.NoPrefix;
                int textLeft = Math.Max(barX + barW + (int)Math.Round(8 * dpi), (int)Math.Round(20 * dpi));
                int textW = Math.Max(0, Width - textLeft - (int)Math.Round(4 * dpi));
                TextRenderer.DrawText(g, Text, navFont,
                    new Rectangle(textLeft, 0, textW, Math.Max(0, Height)), navText,
                    navFlags);

                if (Focused && ShowFocusCues && Enabled && Width > 12 && Height > 8)
                {
                    using var focusPen = new Pen(ThemeManager.FocusRing, 1.2f) { DashStyle = DashStyle.Dot };
                    g.DrawRectangle(focusPen, 6, 4, Width - 12, Height - 8);
                }
                base.OnPaint(e);
                return;
            }

            // Standard card/dialog button with rounded borders
            using var pathBtn = RoundedRect(rect, 7);

            Color bg, border, textColor;
            float borderWidth;

            if (!Enabled)
            {
                bg = ThemeManager.ControlDisabled;
                border = ThemeManager.BorderDisabled;
                textColor = ThemeManager.TextMuted;
                borderWidth = 1f;
            }
            else if (_isActive)
            {
                bg = ThemeManager.ControlActive;
                border = _customActiveColor ?? ThemeManager.Accent;
                textColor = _customActiveColor ?? (ThemeManager.IsDarkThemeActive ? ThemeManager.Accent : Color.White);
                borderWidth = 2.0f;
            }
            else if (_isHover)
            {
                bg = ThemeManager.ControlHover;
                border = ThemeManager.BorderHover;
                textColor = ThemeManager.TextPrimary;
                borderWidth = 1f;
            }
            else
            {
                bg = ThemeManager.ControlBg;
                border = ThemeManager.ControlBorder;
                textColor = ThemeManager.TextPrimary;
                borderWidth = 1f;
            }

            using (var bgBrush = new SolidBrush(bg))
                g.FillPath(bgBrush, pathBtn);

            using (var pen = new Pen(border, borderWidth))
                g.DrawPath(pen, pathBtn);

            if (_isActive && Enabled)
            {
                using var glowPen = new Pen(Color.FromArgb(70, border), 3.0f);
                g.DrawPath(glowPen, pathBtn);
            }

            if (Focused && ShowFocusCues && Enabled && Width > 8 && Height > 8)
            {
                using var focusPen = new Pen(ThemeManager.FocusRing, 1.2f) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(focusPen, 4, 4, Math.Max(1, Width - 9), Math.Max(1, Height - 9));
            }

            Font btnFont = _isActive ? GetBoldFont() : Font;
            var btnFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            if (!_useMnemonic) btnFlags |= TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, Text, btnFont, ClientRectangle, textColor,
                btnFlags);

            base.OnPaint(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        public void PerformClick()
        {
            if (CanSelect)
            {
                OnClick(EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (_dialogResult != DialogResult.None && FindForm() is Form f)
            {
                f.DialogResult = _dialogResult;
            }
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

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Enabled && CanFocus)
            {
                Focus();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            if (Enabled) { _isHover = true; Invalidate(); }
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _isHover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) { _isHover = false; Cursor = Cursors.Default; }
            else { Cursor = Cursors.Hand; }
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;
                if (_boldFont != null && _boldFont != Font)
                {
                    _boldFont.Dispose();
                }
                _boldFont = null;
            }
            base.Dispose(disposing);
        }
    }
}
