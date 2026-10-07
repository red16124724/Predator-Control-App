using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class DarkScrollPanel : Panel, IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;

        public static int NativeBarWidth => SystemInformation.VerticalScrollBarWidth;

        private float _dpi = 1f;

        private readonly Action _themeHandler;

        public DarkScrollPanel()
        {
            AutoScroll = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            _themeHandler = () =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BackColor = ThemeManager.FormBg;
                    Invalidate();
                    InvalidateScrollBar();
                }
            };
            ThemeManager.ThemeChanged += _themeHandler;
        }

        public void SetDpiScale(float dpi) => _dpi = dpi <= 0 ? 1f : dpi;
        private int S(int v) => Math.Max(1, (int)(v * _dpi));

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Application.AddMessageFilter(this);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Application.RemoveMessageFilter(this);
            base.OnHandleDestroyed(e);
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            if (IsDisposed || !IsHandleCreated || !Visible || !VerticalScroll.Visible) return false;

            var form = FindForm();
            if (form == null || Form.ActiveForm != form) return false;
            if (!RectangleToScreen(ClientRectangle).Contains(Cursor.Position)) return false;

            int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
            if (delta == 0) return false;

            ScrollByWheel(delta);
            return true;
        }

        private void ScrollByWheel(int delta)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            int step = lines < 0 ? ClientSize.Height : Math.Max(1, lines) * S(20);

            int top = -AutoScrollPosition.Y - Math.Sign(delta) * step;
            AutoScrollPosition = new Point(-AutoScrollPosition.X, top);
            InvalidateScrollBar();
        }

        private bool _dragging;
        private int _dragOffset;
        private Rectangle ThumbRect()
        {
            var content = DisplayRectangle;
            int viewH = ClientSize.Height, contentH = content.Height;
            if (viewH <= 0 || contentH <= viewH) return Rectangle.Empty;

            int barW = S(5);
            int maxThumb = Math.Max(S(30), viewH * 2 / 3);
            int thumbH = Math.Clamp((int)((long)viewH * viewH / contentH), S(30), maxThumb);
            int scrollPos = Math.Clamp(-content.Y, 0, contentH - viewH);
            int thumbY = (int)((float)scrollPos / (contentH - viewH) * (viewH - thumbH));
            return new Rectangle(ClientSize.Width - barW - S(3), thumbY, barW, thumbH);
        }

        private Rectangle BarHitRect()
        {
            int strip = S(14);
            return new Rectangle(ClientSize.Width - strip, 0, strip, ClientSize.Height);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !VerticalScroll.Visible) return;
            if (!BarHitRect().Contains(e.Location)) return;

            var thumb = ThumbRect();
            if (thumb.IsEmpty) return;

            _dragging = true;
            _dragOffset = thumb.Contains(thumb.X, e.Y) ? e.Y - thumb.Y : thumb.Height / 2;
            ScrollThumbTo(e.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) ScrollThumbTo(e.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        private void ScrollThumbTo(int mouseY)
        {
            var content = DisplayRectangle;
            int viewH = ClientSize.Height, contentH = content.Height;
            if (viewH <= 0 || contentH <= viewH) return;

            var thumb = ThumbRect();
            int maxThumbY = viewH - thumb.Height;
            if (maxThumbY <= 0) return;

            int targetThumbY = Math.Clamp(mouseY - _dragOffset, 0, maxThumbY);
            float ratio = (float)targetThumbY / maxThumbY;
            int targetScroll = (int)(ratio * (contentH - viewH));

            AutoScrollPosition = new Point(-AutoScrollPosition.X, targetScroll);
            InvalidateScrollBar();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            InvalidateScrollBar();
        }

        private void InvalidateScrollBar()
        {
            int strip = S(10);
            Invalidate(new Rectangle(ClientSize.Width - strip, 0, strip, ClientSize.Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var rect = ThumbRect();
            if (rect.IsEmpty) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color trackColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(24, 27, 36) : Color.FromArgb(235, 237, 242);
            Color thumbColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(65, 72, 92) : Color.FromArgb(180, 186, 200);

            using (var trackBrush = new SolidBrush(trackColor))
                g.FillRectangle(trackBrush, rect.X - S(1), 0, rect.Width + S(2), ClientSize.Height);

            int r = Math.Max(1, rect.Width / 2);
            using var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
            path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();

            using (var thumbBrush = new SolidBrush(thumbColor))
                g.FillPath(thumbBrush, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;
                Application.RemoveMessageFilter(this);
            }
            base.Dispose(disposing);
        }
    }
}
