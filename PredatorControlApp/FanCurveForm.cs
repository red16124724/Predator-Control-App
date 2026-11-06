using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    public class FanCurveApplyEventArgs : EventArgs
    {
        public List<Point> CpuPoints { get; set; } = new();
        public List<Point> GpuPoints { get; set; } = new();
        public int HysteresisDegrees { get; set; } = CurveFollower.DefaultHysteresisDegrees;
        public bool Success { get; set; }
    }

    [SupportedOSPlatform("windows")]
    public class FanCurveForm : Form
    {
        #region Theme Colors & Fonts

        private static Color FormBg => ThemeManager.FormBg;
        private static Color TitleBarBg => ThemeManager.TitleBarBg;
        private static Color SeparatorColor => ThemeManager.CardBorder;
        private static Color TitleTextColor => ThemeManager.TextPrimary;
        private static Color CloseNormalColor => ThemeManager.TextSecondary;
        private static readonly Color CloseHoverColor = Color.FromArgb(220, 50, 50);

        private static readonly Color CpuCurveColor = Color.FromArgb(0, 180, 255);
        private static readonly Color GpuCurveColor = Color.FromArgb(255, 77, 109);

        private static readonly Color FlashGreen = Color.FromArgb(0, 180, 80);
        private static readonly Color FlashRed = Color.FromArgb(220, 50, 50);

        private static readonly Font FontTitle = new("Segoe UI", 10f, FontStyle.Bold);
        private static readonly Font FontClose = new("Segoe UI", 10f, FontStyle.Bold);
        private static readonly Font FontHysteresis = new("Segoe UI", 9f, FontStyle.Bold);

        #endregion

        #region Fields

        private FanCurveGraph _graphCpu = null!;
        private FanCurveGraph _graphGpu = null!;
        private Label _lblHysteresis = null!;
        private PredatorSlider _sliderHysteresis = null!;
        private PredatorButton _btnReset = null!;
        private PredatorButton _btnApply = null!;
        private System.Windows.Forms.Timer _flashTimer = null!;
        private Panel _pnlTitle = null!;
        private Label _lblTitle = null!;
        private Label _lblClose = null!;
        private readonly List<Panel> _separators = new();
        private readonly Action _themeHandler;

        private bool _isClosing;
        private int _hysteresisDegrees = CurveFollower.DefaultHysteresisDegrees;
        private bool _updatingHysteresis;
        private float _dpiScale = 1f;
        private int S(int px) => px == 0 ? 0 : Math.Max(1, (int)Math.Round(px * _dpiScale));

        private const int SidePad = 16;
        private const int GraphHeight = 220;
        private const int TitleBarHeight = 36;
        private const int SepHeight = 1;
        private const int HysteresisRowHeight = 64;
        private const int ButtonRowHeight = 50;

        #endregion

        #region Events

        public event EventHandler<FanCurveApplyEventArgs>? ApplyClicked;

        #endregion

        #region Window Dragging

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        #endregion

        #region Constructor

        public FanCurveForm()
        {
            Text = "Fan Curve Editor";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = FormBg;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _dpiScale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
            int totalHeight = S(TitleBarHeight) + S(SepHeight)
                            + S(GraphHeight) + S(SepHeight)
                            + S(GraphHeight) + S(SepHeight)
                            + S(HysteresisRowHeight) + S(SepHeight)
                            + S(ButtonRowHeight);

            int totalWidth = S(460);
            ClientSize = new Size(totalWidth, totalHeight);
            AutoScroll = true;
            AutoScrollMinSize = new Size(totalWidth, totalHeight);
            
            _flashTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _flashTimer.Tick += FlashTimer_Tick;

            _themeHandler = () =>
            {
                SafeBeginInvoke(ApplyThemeColors);
            };
            ThemeManager.ThemeChanged += _themeHandler;

            BuildUI();
        }

        #endregion

        #region UI Building

        private void BuildUI()
        {
            int y = 0;

            _pnlTitle = new Panel
            {
                Height = S(TitleBarHeight),
                Dock = DockStyle.Top,
                BackColor = TitleBarBg
            };
            _pnlTitle.MouseDown += TitleBar_MouseDown;
            Controls.Add(_pnlTitle);

            _lblTitle = new Label
            {
                Text = "Fan Curve Editor",
                Font = FontTitle,
                ForeColor = TitleTextColor,
                AutoSize = true,
                Location = new Point(S(SidePad), (S(TitleBarHeight) - FontTitle.Height) / 2),
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
            _lblTitle.MouseDown += TitleBar_MouseDown;
            _pnlTitle.Controls.Add(_lblTitle);

            _lblClose = new Label
            {
                Text = "✕",
                Font = FontClose,
                ForeColor = CloseNormalColor,
                AutoSize = true,
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent,
                UseMnemonic = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _lblClose.Location = new Point(
                _pnlTitle.ClientSize.Width - S(SidePad) - _lblClose.PreferredWidth - S(2),
                (S(TitleBarHeight) - _lblClose.PreferredHeight) / 2);
            _lblClose.Click += (s, e) => Close();
            _lblClose.MouseEnter += (s, e) => _lblClose.ForeColor = CloseHoverColor;
            _lblClose.MouseLeave += (s, e) => _lblClose.ForeColor = CloseNormalColor;
            _pnlTitle.Controls.Add(_lblClose);

            y = S(TitleBarHeight);

            Controls.Add(MakeSeparator(y));
            y += S(SepHeight);

            _graphCpu = new FanCurveGraph
            {
                CurveColor = CpuCurveColor,
                FanLabel = "CPU FAN CURVE",
                Location = new Point(S(SidePad), y),
                Size = new Size(ClientSize.Width - S(SidePad) * 2, S(GraphHeight)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(_graphCpu);
            y += S(GraphHeight);

            Controls.Add(MakeSeparator(y));
            y += S(SepHeight);

            _graphGpu = new FanCurveGraph
            {
                CurveColor = GpuCurveColor,
                FanLabel = "GPU FAN CURVE",
                Location = new Point(S(SidePad), y),
                Size = new Size(ClientSize.Width - S(SidePad) * 2, S(GraphHeight)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(_graphGpu);
            y += S(GraphHeight);

            Controls.Add(MakeSeparator(y));
            y += S(SepHeight);

            _lblHysteresis = new Label
            {
                Text = UiLabelFormatter.FormatHysteresisLabel(_hysteresisDegrees),
                Font = FontHysteresis,
                ForeColor = TitleTextColor,
                AutoSize = true,
                Location = new Point(S(SidePad), y + S(4)),
                BackColor = Color.Transparent,
                UseMnemonic = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            Controls.Add(_lblHysteresis);

            int sliderH = Math.Max(28, S(28));
            _sliderHysteresis = new PredatorSlider
            {
                Minimum = CurveFollower.MinHysteresisDegrees,
                Maximum = CurveFollower.MaxHysteresisDegrees,
                Value = _hysteresisDegrees,
                Location = new Point(S(SidePad), y + S(28)),
                Size = new Size(ClientSize.Width - S(SidePad) * 2, sliderH),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _sliderHysteresis.ValueChanged += (s, e) =>
            {
                if (_updatingHysteresis) return;
                _updatingHysteresis = true;
                try
                {
                    _hysteresisDegrees = _sliderHysteresis.Value;
                    _lblHysteresis.Text = UiLabelFormatter.FormatHysteresisLabel(_hysteresisDegrees);
                }
                finally
                {
                    _updatingHysteresis = false;
                }
            };
            Controls.Add(_sliderHysteresis);
            y += S(HysteresisRowHeight);

            Controls.Add(MakeSeparator(y));
            y += S(SepHeight);

            int btnWidth = S(96);
            int btnHeight = S(34);
            int btnY = y + (S(ButtonRowHeight) - btnHeight) / 2;

            _btnReset = new PredatorButton
            {
                Text = "Reset",
                Size = new Size(btnWidth, btnHeight),
                Location = new Point(S(SidePad), btnY),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _btnReset.Click += BtnReset_Click;
            Controls.Add(_btnReset);

            _btnApply = new PredatorButton
            {
                Text = "Apply",
                Size = new Size(btnWidth, btnHeight),
                Location = new Point(ClientSize.Width - S(SidePad) - btnWidth, btnY),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnApply.Click += BtnApply_Click;
            Controls.Add(_btnApply);
        }

        private Panel MakeSeparator(int yPos)
        {
            var sep = new Panel
            {
                Location = new Point(0, yPos),
                Size = new Size(ClientSize.Width, S(SepHeight)),
                BackColor = SeparatorColor,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _separators.Add(sep);
            return sep;
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _dpiScale = e.DeviceDpiNew / 96f;
            float factor = e.DeviceDpiOld > 0 ? (float)e.DeviceDpiNew / e.DeviceDpiOld : 1.0f;
            if (Math.Abs(factor - 1.0f) > 0.001f)
            {
                try { RescaleControlFonts(this, factor); } catch { }
            }
            UpdateLayout();
        }

        private static void RescaleControlFonts(Control root, float factor)
        {
            if (root == null || root.IsDisposed) return;
            if (root.Font != null)
            {
                float newSize = Math.Max(6f, (float)Math.Round(root.Font.Size * factor, 2));
                if (Math.Abs(newSize - root.Font.Size) > 0.05f)
                {
                    root.Font = new Font(root.Font.FontFamily, newSize, root.Font.Style);
                }
            }
            foreach (Control child in root.Controls)
            {
                RescaleControlFonts(child, factor);
            }
        }

        private void UpdateLayout()
        {
            try
            {
                int totalHeight = S(TitleBarHeight) + S(SepHeight)
                                + S(GraphHeight) + S(SepHeight)
                                + S(GraphHeight) + S(SepHeight)
                                + S(HysteresisRowHeight) + S(SepHeight)
                                + S(ButtonRowHeight);
                int totalWidth = S(460);
                ClientSize = new Size(totalWidth, totalHeight);
                AutoScrollMinSize = new Size(totalWidth, totalHeight);

                int y = 0;
                if (_pnlTitle != null)
                {
                    _pnlTitle.Height = S(TitleBarHeight);
                    if (_lblTitle != null)
                        _lblTitle.Location = new Point(S(SidePad), (S(TitleBarHeight) - _lblTitle.Height) / 2);
                    if (_lblClose != null)
                        _lblClose.Location = new Point(_pnlTitle.ClientSize.Width - S(SidePad) - _lblClose.PreferredWidth - S(2),
                            (S(TitleBarHeight) - _lblClose.PreferredHeight) / 2);
                }
                y = S(TitleBarHeight);

                int sepIdx = 0;
                if (_separators.Count > sepIdx) { _separators[sepIdx].Location = new Point(0, y); _separators[sepIdx].Size = new Size(ClientSize.Width, S(SepHeight)); }
                y += S(SepHeight);
                sepIdx++;

                if (_graphCpu != null)
                {
                    _graphCpu.Location = new Point(S(SidePad), y);
                    _graphCpu.Size = new Size(ClientSize.Width - S(SidePad) * 2, S(GraphHeight));
                }
                y += S(GraphHeight);

                if (_separators.Count > sepIdx) { _separators[sepIdx].Location = new Point(0, y); _separators[sepIdx].Size = new Size(ClientSize.Width, S(SepHeight)); }
                y += S(SepHeight);
                sepIdx++;

                if (_graphGpu != null)
                {
                    _graphGpu.Location = new Point(S(SidePad), y);
                    _graphGpu.Size = new Size(ClientSize.Width - S(SidePad) * 2, S(GraphHeight));
                }
                y += S(GraphHeight);

                if (_separators.Count > sepIdx) { _separators[sepIdx].Location = new Point(0, y); _separators[sepIdx].Size = new Size(ClientSize.Width, S(SepHeight)); }
                y += S(SepHeight);
                sepIdx++;

                if (_lblHysteresis != null) _lblHysteresis.Location = new Point(S(SidePad), y + S(4));
                int sliderH = Math.Max(28, S(28));
                if (_sliderHysteresis != null)
                {
                    _sliderHysteresis.Location = new Point(S(SidePad), y + S(28));
                    _sliderHysteresis.Size = new Size(ClientSize.Width - S(SidePad) * 2, sliderH);
                }
                y += S(HysteresisRowHeight);

                if (_separators.Count > sepIdx) { _separators[sepIdx].Location = new Point(0, y); _separators[sepIdx].Size = new Size(ClientSize.Width, S(SepHeight)); }
                y += S(SepHeight);

                int btnWidth = S(96);
                int btnHeight = S(34);
                int btnY = y + (S(ButtonRowHeight) - btnHeight) / 2;
                if (_btnReset != null)
                {
                    _btnReset.Size = new Size(btnWidth, btnHeight);
                    _btnReset.Location = new Point(S(SidePad), btnY);
                }
                if (_btnApply != null)
                {
                    _btnApply.Size = new Size(btnWidth, btnHeight);
                    _btnApply.Location = new Point(ClientSize.Width - S(SidePad) - btnWidth, btnY);
                }
            }
            catch { }
        }

        private void ApplyThemeColors()
        {
            BackColor = FormBg;
            if (_pnlTitle != null) _pnlTitle.BackColor = TitleBarBg;
            if (_lblTitle != null) _lblTitle.ForeColor = TitleTextColor;
            if (_lblClose != null) _lblClose.ForeColor = CloseNormalColor;
            if (_lblHysteresis != null) _lblHysteresis.ForeColor = TitleTextColor;
            foreach (var sep in _separators)
            {
                if (!sep.IsDisposed) sep.BackColor = SeparatorColor;
            }
            Invalidate(true);
        }

        #endregion

        #region Button Handlers

        private void BtnReset_Click(object? sender, EventArgs e)
        {
            _graphCpu.Points = FanCurveGraph.DefaultCpuPoints;
            _graphGpu.Points = FanCurveGraph.DefaultGpuPoints;
            SetHysteresis(CurveFollower.DefaultHysteresisDegrees);
        }

        private void BtnApply_Click(object? sender, EventArgs e)
        {
            var args = new FanCurveApplyEventArgs
            {
                CpuPoints = new List<Point>(_graphCpu.Points),
                GpuPoints = new List<Point>(_graphGpu.Points),
                HysteresisDegrees = _hysteresisDegrees,
                Success = false
            };

            ApplyClicked?.Invoke(this, args);

            Color flashColor = args.Success ? FlashGreen : FlashRed;
            _btnApply.CustomActiveColor = flashColor;
            _btnApply.IsActive = true;

            _flashTimer.Stop();
            _flashTimer.Start();
        }

        private void FlashTimer_Tick(object? sender, EventArgs e)
        {
            try { _flashTimer?.Stop(); } catch { }
            if (IsDisposed || _btnApply.IsDisposed) return;
            _btnApply.IsActive = false;
            _btnApply.CustomActiveColor = null;
        }

        #endregion

        private void SafeBeginInvoke(Action action)
        {
            try
            {
                if (IsDisposed || Disposing || _isClosing) return;
                if (IsHandleCreated && InvokeRequired)
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (!IsDisposed && !Disposing && !_isClosing)
                            {
                                action();
                            }
                        }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                        catch { }
                    }));
                }
                else
                {
                    if (!IsDisposed && !Disposing && !_isClosing)
                        action();
                }
            }
            catch { }
        }

        #region Public Methods

        public void UpdateTemps(int cpuTemp, int gpuTemp)
        {
            if (IsDisposed || Disposing || _isClosing || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(() => UpdateTemps(cpuTemp, gpuTemp));
                return;
            }
            _graphCpu.CurrentTemp = cpuTemp;
            _graphGpu.CurrentTemp = gpuTemp;
        }

        public void SetCpuCurve(List<Point> points)
        {
            if (IsDisposed || Disposing || _isClosing) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(() => SetCpuCurve(points));
                return;
            }
            _graphCpu.Points = points != null && points.Count > 0 ? points : FanCurveGraph.DefaultCpuPoints;
        }

        public void SetGpuCurve(List<Point> points)
        {
            if (IsDisposed || Disposing || _isClosing) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(() => SetGpuCurve(points));
                return;
            }
            _graphGpu.Points = points != null && points.Count > 0 ? points : FanCurveGraph.DefaultGpuPoints;
        }

        public List<Point> GetCpuCurve() => new(_graphCpu.Points);

        public List<Point> GetGpuCurve() => new(_graphGpu.Points);

        public int InterpolateCpuSpeed(int temp) => _graphCpu.InterpolateSpeed(temp);

        public int InterpolateGpuSpeed(int temp) => _graphGpu.InterpolateSpeed(temp);

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int HysteresisDegrees
        {
            get => _hysteresisDegrees;
            set => SetHysteresis(value);
        }

        public void SetHysteresis(int degrees)
        {
            if (IsDisposed || Disposing || _isClosing) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(() => SetHysteresis(degrees));
                return;
            }
            if (_updatingHysteresis) return;
            _updatingHysteresis = true;
            try
            {
                _hysteresisDegrees = Math.Clamp(degrees, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees);
                if (_sliderHysteresis != null && _sliderHysteresis.Value != _hysteresisDegrees)
                {
                    _sliderHysteresis.Value = _hysteresisDegrees;
                }
                if (_lblHysteresis != null)
                {
                    _lblHysteresis.Text = UiLabelFormatter.FormatHysteresisLabel(_hysteresisDegrees);
                }
            }
            finally
            {
                _updatingHysteresis = false;
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public PredatorSlider HysteresisSlider => _sliderHysteresis;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Label HysteresisLabel => _lblHysteresis;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public PredatorButton ResetButton => _btnReset;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public PredatorButton ApplyButton => _btnApply;

        public void ApplyCurves()
        {
            if (IsDisposed || Disposing || _isClosing) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(ApplyCurves);
                return;
            }
            BtnApply_Click(this, EventArgs.Empty);
        }

        public void ResetDefaults()
        {
            if (IsDisposed || Disposing || _isClosing) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(ResetDefaults);
                return;
            }
            BtnReset_Click(this, EventArgs.Empty);
        }

        #endregion

        #region Cleanup

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _isClosing = true;
            try
            {
                if (_flashTimer != null)
                {
                    _flashTimer.Stop();
                    _flashTimer.Tick -= FlashTimer_Tick;
                    _flashTimer.Dispose();
                }
            }
            catch { }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _isClosing = true;
            ThemeManager.ThemeChanged -= _themeHandler;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _isClosing = true;
                ThemeManager.ThemeChanged -= _themeHandler;
                if (_flashTimer != null)
                {
                    _flashTimer.Stop();
                    _flashTimer.Tick -= FlashTimer_Tick;
                    _flashTimer.Dispose();
                }
                try { Icon?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
