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

        private int _hysteresisDegrees = CurveFollower.DefaultHysteresisDegrees;

        private const int SidePad = 16;
        private const int GraphHeight = 220;
        private const int TitleBarHeight = 36;
        private const int SepHeight = 1;
        private const int HysteresisRowHeight = 58;
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

            int totalHeight = TitleBarHeight + SepHeight
                            + GraphHeight + SepHeight
                            + GraphHeight + SepHeight
                            + HysteresisRowHeight + SepHeight
                            + ButtonRowHeight;

            int totalWidth = 460;
            ClientSize = new Size(totalWidth, totalHeight);
            AutoScroll = true;
            AutoScrollMinSize = new Size(totalWidth, totalHeight);
            
            _flashTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _flashTimer.Tick += FlashTimer_Tick;

            _themeHandler = () =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        if (InvokeRequired)
                            BeginInvoke(new Action(ApplyThemeColors));
                        else
                            ApplyThemeColors();
                    }
                    catch { }
                }
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
                Height = TitleBarHeight,
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
                Location = new Point(SidePad, (TitleBarHeight - FontTitle.Height) / 2),
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
                ClientSize.Width - SidePad - _lblClose.PreferredWidth - 2,
                (TitleBarHeight - _lblClose.PreferredHeight) / 2);
            _lblClose.Click += (s, e) => Close();
            _lblClose.MouseEnter += (s, e) => _lblClose.ForeColor = CloseHoverColor;
            _lblClose.MouseLeave += (s, e) => _lblClose.ForeColor = CloseNormalColor;
            _pnlTitle.Controls.Add(_lblClose);

            y = TitleBarHeight;

            Controls.Add(MakeSeparator(y));
            y += SepHeight;

            _graphCpu = new FanCurveGraph
            {
                CurveColor = CpuCurveColor,
                FanLabel = "CPU FAN CURVE",
                Location = new Point(SidePad, y),
                Size = new Size(ClientSize.Width - SidePad * 2, GraphHeight),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(_graphCpu);
            y += GraphHeight;

            Controls.Add(MakeSeparator(y));
            y += SepHeight;

            _graphGpu = new FanCurveGraph
            {
                CurveColor = GpuCurveColor,
                FanLabel = "GPU FAN CURVE",
                Location = new Point(SidePad, y),
                Size = new Size(ClientSize.Width - SidePad * 2, GraphHeight),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(_graphGpu);
            y += GraphHeight;

            Controls.Add(MakeSeparator(y));
            y += SepHeight;

            _lblHysteresis = new Label
            {
                Text = UiLabelFormatter.FormatHysteresisLabel(_hysteresisDegrees),
                Font = FontHysteresis,
                ForeColor = TitleTextColor,
                AutoSize = true,
                Location = new Point(SidePad, y + 6),
                BackColor = Color.Transparent,
                UseMnemonic = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            Controls.Add(_lblHysteresis);

            _sliderHysteresis = new PredatorSlider
            {
                Minimum = CurveFollower.MinHysteresisDegrees,
                Maximum = CurveFollower.MaxHysteresisDegrees,
                Value = _hysteresisDegrees,
                Location = new Point(SidePad, y + 26),
                Size = new Size(ClientSize.Width - SidePad * 2, 26),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _sliderHysteresis.ValueChanged += (s, e) =>
            {
                _hysteresisDegrees = _sliderHysteresis.Value;
                _lblHysteresis.Text = UiLabelFormatter.FormatHysteresisLabel(_hysteresisDegrees);
            };
            Controls.Add(_sliderHysteresis);
            y += HysteresisRowHeight;

            Controls.Add(MakeSeparator(y));
            y += SepHeight;

            int btnWidth = 96;
            int btnHeight = 34;
            int btnY = y + (ButtonRowHeight - btnHeight) / 2;

            _btnReset = new PredatorButton
            {
                Text = "Reset",
                Size = new Size(btnWidth, btnHeight),
                Location = new Point(SidePad, btnY),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _btnReset.Click += BtnReset_Click;
            Controls.Add(_btnReset);

            _btnApply = new PredatorButton
            {
                Text = "Apply",
                Size = new Size(btnWidth, btnHeight),
                Location = new Point(ClientSize.Width - SidePad - btnWidth, btnY),
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
                Size = new Size(ClientSize.Width, SepHeight),
                BackColor = SeparatorColor,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _separators.Add(sep);
            return sep;
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
            _flashTimer.Stop();
            if (IsDisposed || _btnApply.IsDisposed) return;
            _btnApply.IsActive = false;
            _btnApply.CustomActiveColor = null;
        }

        #endregion

        #region Public Methods

        public void UpdateTemps(int cpuTemp, int gpuTemp)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => UpdateTemps(cpuTemp, gpuTemp))); } catch { }
                return;
            }
            _graphCpu.CurrentTemp = cpuTemp;
            _graphGpu.CurrentTemp = gpuTemp;
        }

        public void SetCpuCurve(List<Point> points)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => SetCpuCurve(points))); } catch { }
                return;
            }
            _graphCpu.Points = points != null && points.Count > 0 ? points : FanCurveGraph.DefaultCpuPoints;
        }

        public void SetGpuCurve(List<Point> points)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => SetGpuCurve(points))); } catch { }
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
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => SetHysteresis(degrees))); } catch { }
                return;
            }
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

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public PredatorSlider HysteresisSlider => _sliderHysteresis;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Label HysteresisLabel => _lblHysteresis;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public PredatorButton ResetButton => _btnReset;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public PredatorButton ApplyButton => _btnApply;

        public void ApplyCurves() => BtnApply_Click(this, EventArgs.Empty);

        public void ResetDefaults() => BtnReset_Click(this, EventArgs.Empty);

        #endregion

        #region Cleanup

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
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
