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
            _dpi = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;

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
                                try
                                {
                                    if (IsDisposed || Disposing) return;
                                    BackColor = ThemeManager.FormBg;
                                    Invalidate();
                                    InvalidateScrollBar();
                                }
                                catch { }
                            }));
                        }
                        else
                        {
                            BackColor = ThemeManager.FormBg;
                            Invalidate();
                            InvalidateScrollBar();
                        }
                    }
                    catch { }
                }
            };
            ThemeManager.ThemeChanged += _themeHandler;
        }

        public void SetDpiScale(float dpi) => _dpi = dpi <= 0 ? 1f : dpi;
        private int S(int v) => Math.Max(1, (int)Math.Round(v * _dpi));

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            if (DeviceDpi > 0)
            {
                SetDpiScale(DeviceDpi / 96f);
            }
        }

        private bool _isFilterRegistered;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_isFilterRegistered)
            {
                Application.AddMessageFilter(this);
                _isFilterRegistered = true;
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_isFilterRegistered)
            {
                Application.RemoveMessageFilter(this);
                _isFilterRegistered = false;
            }
            base.OnHandleDestroyed(e);
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            if (IsDisposed || Disposing || !IsHandleCreated || !Visible || !VerticalScroll.Visible) return false;

            try
            {
                var form = FindForm();
                if (form == null || Form.ActiveForm != form) return false;
                if (!RectangleToScreen(ClientRectangle).Contains(Cursor.Position)) return false;

                var ptClient = PointToClient(Cursor.Position);
                var child = GetChildAtPoint(ptClient, GetChildAtPointSkip.Invisible | GetChildAtPointSkip.Transparent);
                if (child != null && child != this)
                {
                    if (child is PredatorSlider || child is ComboBox || child is TrackBar ||
                        child.GetType().Name.Contains("Slider") || child.GetType().Name.Contains("DropDown") || child.GetType().Name.Contains("Graph"))
                    {
                        return false;
                    }
                }

                int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                if (delta == 0) return false;

                ScrollByWheel(delta);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ScrollByWheel(int delta)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            int step = lines < 0 ? ClientSize.Height : Math.Max(1, lines) * S(20);

            var content = DisplayRectangle;
            int viewH = ClientSize.Height, contentH = content.Height;
            int maxScroll = Math.Max(0, contentH - viewH);

            int top = Math.Clamp(Math.Abs(AutoScrollPosition.Y) - Math.Sign(delta) * step, 0, maxScroll);
            AutoScrollPosition = new Point(Math.Abs(AutoScrollPosition.X), top);
            InvalidateScrollBar();
        }

        private bool _dragging;
        private int _dragOffset;

        private int VisibleWidth => Math.Max(0, (Parent != null && Parent.ClientSize.Width < ClientSize.Width)
            ? Parent.ClientSize.Width
            : ClientSize.Width);

        private Rectangle ThumbRect()
        {
            var content = DisplayRectangle;
            int viewH = ClientSize.Height, contentH = content.Height;
            if (viewH <= 0 || contentH <= viewH || VisibleWidth <= 0) return Rectangle.Empty;

            int barW = S(5);
            int minThumb = Math.Min(viewH, S(30));
            int maxThumb = Math.Max(minThumb, viewH * 2 / 3);
            int thumbH = Math.Clamp((int)((long)viewH * viewH / contentH), minThumb, maxThumb);
            int scrollPos = Math.Clamp(-content.Y, 0, contentH - viewH);
            int maxThumbY = Math.Max(0, viewH - thumbH);
            int thumbY = maxThumbY > 0 ? (int)((float)scrollPos / (contentH - viewH) * maxThumbY) : 0;
            int x = VisibleWidth - barW - S(3);
            if (x < 0) return Rectangle.Empty;
            return new Rectangle(x, thumbY, barW, thumbH);
        }

        private Rectangle BarHitRect()
        {
            int strip = S(14);
            int x = Math.Max(0, VisibleWidth - strip);
            return new Rectangle(x, 0, Math.Min(strip, VisibleWidth), Math.Max(0, ClientSize.Height));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !VerticalScroll.Visible) return;
            if (!BarHitRect().Contains(e.Location)) return;

            var thumb = ThumbRect();
            if (thumb.IsEmpty) return;

            _dragging = true;
            Capture = true;
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
            Capture = false;
        }

        protected override void OnLostFocus(EventArgs e)
        {
            _dragging = false;
            Capture = false;
            base.OnLostFocus(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled)
            {
                _dragging = false;
                Capture = false;
            }
            base.OnEnabledChanged(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _dragging)
            {
                _dragging = false;
                InvalidateScrollBar();
            }
        }

        private void ScrollThumbTo(int mouseY)
        {
            var content = DisplayRectangle;
            int viewH = ClientSize.Height, contentH = content.Height;
            if (viewH <= 0 || contentH <= viewH) return;

            var thumb = ThumbRect();
            if (thumb.IsEmpty) return;
            int maxThumbY = viewH - thumb.Height;
            if (maxThumbY <= 0) return;

            int targetThumbY = Math.Clamp(mouseY - _dragOffset, 0, maxThumbY);
            float ratio = (float)targetThumbY / maxThumbY;
            int targetScroll = (int)(ratio * (contentH - viewH));

            AutoScrollPosition = new Point(Math.Abs(AutoScrollPosition.X), targetScroll);
            InvalidateScrollBar();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            InvalidateScrollBar();
        }

        private void InvalidateScrollBar()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0 || VisibleWidth <= 0 || IsDisposed || Disposing) return;
            int strip = S(14);
            int x = Math.Max(0, VisibleWidth - strip);
            Invalidate(new Rectangle(x, 0, Math.Min(strip, VisibleWidth), ClientSize.Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0 || VisibleWidth <= 0 || IsDisposed || Disposing) return;

            var rect = ThumbRect();
            if (rect.IsEmpty) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color trackColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(24, 27, 36) : Color.FromArgb(235, 237, 242);
            Color thumbColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(65, 72, 92) : Color.FromArgb(180, 186, 200);

            using (var trackBrush = new SolidBrush(trackColor))
                g.FillRectangle(trackBrush, Math.Max(0, rect.X - S(1)), 0, rect.Width + S(2), ClientSize.Height);

            using var path = new GraphicsPath();
            if (rect.Width < 4 || rect.Height < 4)
            {
                path.AddRectangle(rect);
            }
            else
            {
                int maxR = Math.Max(1, Math.Min(rect.Width, rect.Height) / 2);
                int r = Math.Clamp(rect.Width / 2, 1, maxR);
                path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
                path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
                path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
            }

            using (var thumbBrush = new SolidBrush(thumbColor))
                g.FillPath(thumbBrush, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;
                if (_isFilterRegistered)
                {
                    Application.RemoveMessageFilter(this);
                    _isFilterRegistered = false;
                }
            }
            base.Dispose(disposing);
        }
    }
}
