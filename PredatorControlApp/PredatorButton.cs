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
    public class PredatorButton : Control
    {
        private bool _isHover;
        private bool _isActive;
        private bool _isNavButton;
        private Color? _customActiveColor;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; Invalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsNavButton
        {
            get => _isNavButton;
            set { _isNavButton = value; Invalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color? CustomActiveColor
        {
            get => _customActiveColor;
            set { _customActiveColor = value; Invalidate(); }
        }

        private bool _useMnemonic = false;

        [DefaultValue(false)]
        public bool UseMnemonic
        {
            get => _useMnemonic;
            set { _useMnemonic = value; Invalidate(); }
        }

        private static readonly Font s_defaultFont = new("Segoe UI", 9.25f, FontStyle.Regular);
        private Font? _boldFont;

        private Font GetBoldFont()
        {
            if (_boldFont == null || _boldFont.FontFamily.Name != Font.FontFamily.Name || Math.Abs(_boldFont.Size - Font.Size) > 0.01f)
            {
                _boldFont?.Dispose();
                _boldFont = new Font(Font, FontStyle.Bold);
            }
            return _boldFont;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            _boldFont?.Dispose();
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
                if (!IsDisposed && IsHandleCreated)
                {
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

            Color parentBg = Parent?.BackColor ?? ThemeManager.CardBg;
            g.Clear(parentBg);

            var rect = new Rectangle(1, 1, Width - 3, Height - 3);

            if (_isNavButton)
            {
                // Sleek OpenSense-style navigation rail button
                Color navBg = _isActive
                    ? ThemeManager.ControlActive
                    : (_isHover ? ThemeManager.ControlHover : Color.Transparent);

                Color navText = _isActive
                    ? (_customActiveColor ?? (ThemeManager.IsDarkThemeActive ? ThemeManager.Accent : Color.White))
                    : (_isHover ? ThemeManager.TextPrimary : ThemeManager.TextSecondary);

                using (var path = RoundedRect(new Rectangle(4, 2, Width - 8, Height - 4), 6))
                {
                    if (navBg != Color.Transparent)
                    {
                        using var brush = new SolidBrush(navBg);
                        g.FillPath(brush, path);
                    }

                    if (_isActive)
                    {
                        // Left vertical accent indicator bar
                        using var accBrush = new SolidBrush(ThemeManager.Accent);
                        g.FillRectangle(accBrush, 4, 6, 3, Height - 12);
                    }
                }

                Font navFont = _isActive ? GetBoldFont() : Font;
                var navFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
                if (!_useMnemonic) navFlags |= TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(g, Text, navFont,
                    new Rectangle(20, 0, Width - 24, Height), navText,
                    navFlags);

                if (Focused && Enabled)
                {
                    using var focusPen = new Pen(ThemeManager.FocusRing, 1.2f) { DashStyle = DashStyle.Dot };
                    g.DrawRectangle(focusPen, 6, 4, Width - 12, Height - 8);
                }
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

            if (Focused && Enabled)
            {
                using var focusPen = new Pen(ThemeManager.FocusRing, 1.2f) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(focusPen, 4, 4, Width - 9, Height - 9);
            }

            Font btnFont = _isActive ? GetBoldFont() : Font;
            var btnFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            if (!_useMnemonic) btnFlags |= TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, Text, btnFont, ClientRectangle, textColor,
                btnFlags);
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
                _boldFont?.Dispose();
                _boldFont = null;
            }
            base.Dispose(disposing);
        }
    }
}
