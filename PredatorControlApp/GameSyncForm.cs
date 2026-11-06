using System.Drawing.Drawing2D;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class GameSyncForm : Form
    {
        #region Theme Colors

        private static Color FormBg => ThemeManager.FormBg;
        private static Color PanelBg => ThemeManager.CardBg;
        private static Color BorderColor => ThemeManager.CardBorder;
        private static Color HeaderColor => ThemeManager.HeaderText;
        private static Color SubHeaderColor => ThemeManager.TextSecondary;
        private static Color AccentColor => ThemeManager.Accent;
        private static Color TextColor => ThemeManager.TextPrimary;

        private static readonly Font FontTitle = new("Segoe UI", 11f, FontStyle.Bold);
        private static readonly Font FontSection = new("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Font FontBody = new("Segoe UI", 9.5f, FontStyle.Regular);
        private static readonly Font FontClose = new("Arial", 12f);

        #endregion

        #region Fields

        private readonly GameSyncController _controller;
        private readonly int _maxHz;
        private readonly Action _themeHandler;
        private float _dpiScale = 1f;
        private int S(int px) => px == 0 ? 0 : Math.Max(1, (int)Math.Round(px * _dpiScale));

        private ListBox _lstProfiles = null!;
        private Panel _pnlEditor = null!;

        private Label _lblExeName = null!;
        private PredatorDropDown _cboPower = null!, _cboFan = null!, _cboRefresh = null!;
        private PredatorDropDown _cboBattery = null!, _cboRgbMode = null!;
        private PredatorSlider _trkBrightness = null!, _trkSpeed = null!;
        private PredatorSlider _trkCpuFan = null!, _trkGpuFan = null!;
        private Label _lblBrightVal = null!, _lblSpeedVal = null!;
        private Label _lblCpuFanHdr = null!, _lblGpuFanHdr = null!;
        private PredatorButton _btnColorPick = null!;
        private Color _selectedColor = Color.FromArgb(0, 200, 160);
        private PredatorButton _btnSave = null!, _btnRemove = null!, _btnAdd = null!;

        private GameProfile? _editingProfile;

        private static readonly string[] PowerModeNames = { "Quiet", "Balanced", "Performance", "Turbo", "Eco" };
        private static readonly byte[] PowerModeValues = { 0x00, 0x01, 0x04, 0x05, 0x06 };
        private static readonly string[] FanModeNames = { "Auto", "Max", "Custom" };
        private static readonly byte[] FanModeValues = { 0x01, 0x02, 0x03 };
        private static readonly string[] RgbModeNames = { "Don't Change", "Static", "Breathing", "Neon", "Wave", "Shifting", "Zoom", "Meteor" };
        private static readonly StringFormat s_sfCenter = new() { LineAlignment = StringAlignment.Center };

        #endregion

        #region Constructor

        public GameSyncForm(GameSyncController controller, int maxHz)
        {
            _controller = controller;
            _maxHz = maxHz;

            this.Text = "Game Sync Configuration";
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = FormBg;
            this.ForeColor = TextColor;
            _dpiScale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
            int workH = Screen.PrimaryScreen?.WorkingArea.Height ?? 800;
            int formH = Math.Min(S(800), Math.Max(S(500), workH - S(40)));
            this.ClientSize = new Size(S(780), formH);
            this.DoubleBuffered = true;
            this.ShowInTaskbar = false;
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _themeHandler = () =>
            {
                if (IsDisposed || Disposing) return;
                try
                {
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(ApplyThemeColors));
                    }
                    else
                    {
                        ApplyThemeColors();
                    }
                }
                catch { }
            };
            ThemeManager.ThemeChanged += _themeHandler;

            BuildUI();
            PopulateList();
        }

        private void ApplyThemeColors()
        {
            if (IsDisposed || Disposing) return;
            this.BackColor = FormBg;
            this.ForeColor = TextColor;
            if (_lstProfiles != null && !_lstProfiles.IsDisposed)
            {
                _lstProfiles.BackColor = PanelBg;
                _lstProfiles.ForeColor = TextColor;
                _lstProfiles.Invalidate();
            }
            if (_pnlEditor != null && !_pnlEditor.IsDisposed)
            {
                _pnlEditor.BackColor = FormBg;
                _pnlEditor.Invalidate();
            }
            ThemeManager.ApplyTheme(this);
            Invalidate(true);
        }

        #endregion

        #region Window Dragging

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0); }
        }

        #endregion

        #region UI Building

        private void BuildUI()
        {
            int pad = S(24);

            var pnlTitle = new Panel { Height = S(42), Dock = DockStyle.Top, BackColor = Color.FromArgb(18, 18, 21) };
            pnlTitle.MouseDown += TitleBar_MouseDown;
            this.Controls.Add(pnlTitle);

            var lblTitle = new Label { Text = "🎮  Game Sync Configuration", Font = FontTitle, ForeColor = TextColor, AutoSize = true, Location = new Point(pad, S(11)), BackColor = Color.Transparent };
            lblTitle.MouseDown += TitleBar_MouseDown;
            pnlTitle.Controls.Add(lblTitle);

            var lblClose = new Label { Text = "●", ForeColor = Color.FromArgb(255, 95, 86), Font = FontClose, AutoSize = true, Cursor = Cursors.Hand, BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            lblClose.Location = new Point(this.ClientSize.Width - pad - lblClose.PreferredWidth - S(4), S(9));
            lblClose.Click += (s, e) => this.Close();
            pnlTitle.Controls.Add(lblClose);

            int contentY = pnlTitle.Bottom + pad;
            int bottomPad = pad;

            int listW = S(220);
            int listAreaH = this.ClientSize.Height - contentY - bottomPad;

            MakeLabel("CONFIGURED APPS", pad, contentY, FontSection, HeaderColor);

            int btnAddH = S(34);
            int listH = listAreaH - S(26) - (btnAddH * 3) - S(28); 

            _lstProfiles = new ListBox
            {
                Location = new Point(pad, contentY + S(24)),
                Size = new Size(listW, listH),
                BackColor = PanelBg,
                ForeColor = TextColor,
                Font = FontBody,
                BorderStyle = BorderStyle.FixedSingle,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = S(32)
            };
            _lstProfiles.DrawItem += LstProfiles_DrawItem;
            _lstProfiles.SelectedIndexChanged += LstProfiles_SelectedIndexChanged;
            this.Controls.Add(_lstProfiles);

            _btnAdd = MakeButton("＋  Browse for .exe", pad, _lstProfiles.Bottom + S(8), listW, btnAddH);
            _btnAdd.Click += BtnAdd_Click;

            var btnPick = MakeButton("⚡  Pick Running Process", pad, _lstProfiles.Bottom + S(8) + btnAddH + S(6), listW, btnAddH);
            btnPick.Click += BtnPickRunning_Click;

            int editorX = pad + listW + pad;
            int editorW = this.ClientSize.Width - editorX - pad;
            int editorH = listAreaH;

            _pnlEditor = new Panel
            {
                Location = new Point(editorX, contentY),
                Size = new Size(editorW, editorH),
                BackColor = FormBg,
                AutoScroll = true,
                Visible = false
            };
            this.Controls.Add(_pnlEditor);

            var lblEmpty = new Label
            {
                Text = "Select an application from the list\nor add a new one to get started.",
                ForeColor = SubHeaderColor,
                Font = FontBody,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Name = "lblEmpty"
            };

            int ey = 0;
            int sectionGap = S(50); 

            _lblExeName = new Label { Text = "", Font = FontTitle, ForeColor = AccentColor, AutoSize = true, Location = new Point(0, ey), MaximumSize = new Size(editorW, 0), BackColor = Color.Transparent };
            _pnlEditor.Controls.Add(_lblExeName);
            ey += S(34);

            _pnlEditor.Controls.Add(new Panel { Location = new Point(0, ey), Size = new Size(editorW, 1), BackColor = BorderColor });
            ey += S(20);

            MakeLabelIn(_pnlEditor, "POWER MODE", 0, ey, FontSection, HeaderColor);
            ey += S(24);
            _cboPower = MakeComboIn(_pnlEditor, PowerModeNames, 0, ey, editorW);
            ey += sectionGap;

            MakeLabelIn(_pnlEditor, "FAN MODE", 0, ey, FontSection, HeaderColor);
            ey += S(24);
            _cboFan = MakeComboIn(_pnlEditor, FanModeNames, 0, ey, editorW);
            ey += sectionGap;

            int sliderW = (editorW - S(12)) / 2;
            _lblCpuFanHdr = MakeLabelIn(_pnlEditor, "CPU FAN: 50%", 0, ey, FontSection, HeaderColor);
            _lblGpuFanHdr = MakeLabelIn(_pnlEditor, "GPU FAN: 50%", sliderW + S(12), ey, FontSection, HeaderColor);
            ey += S(22);
            _trkCpuFan = new PredatorSlider
            {
                Location = new Point(0, ey),
                Size = new Size(sliderW, Math.Max(28, S(28))),
                Minimum = 10, Maximum = 100, Value = 50
            };
            _trkGpuFan = new PredatorSlider
            {
                Location = new Point(sliderW + S(12), ey),
                Size = new Size(sliderW, Math.Max(28, S(28))),
                Minimum = 10, Maximum = 100, Value = 50
            };
            _pnlEditor.Controls.Add(_trkCpuFan);
            _pnlEditor.Controls.Add(_trkGpuFan);
            _trkCpuFan.ValueChanged += (s, e) => _lblCpuFanHdr.Text = $"CPU FAN: {_trkCpuFan.Value}%";
            _trkGpuFan.ValueChanged += (s, e) => _lblGpuFanHdr.Text = $"GPU FAN: {_trkGpuFan.Value}%";
            ey += sectionGap;

            _lblCpuFanHdr.Visible = false;
            _lblGpuFanHdr.Visible = false;
            _trkCpuFan.Visible = false;
            _trkGpuFan.Visible = false;
            _cboFan.SelectedIndexChanged += (s, e) =>
            {
                bool isCustom = _cboFan.SelectedIndex == 2; 
                _lblCpuFanHdr.Visible = isCustom;
                _lblGpuFanHdr.Visible = isCustom;
                _trkCpuFan.Visible = isCustom;
                _trkGpuFan.Visible = isCustom;
            };

            MakeLabelIn(_pnlEditor, "REFRESH RATE", 0, ey, FontSection, HeaderColor);
            ey += S(24);
            _cboRefresh = MakeComboIn(_pnlEditor, new[] { "Don't Change", "60 Hz", $"{_maxHz} Hz (Max)" }, 0, ey, editorW);
            ey += sectionGap;

            MakeLabelIn(_pnlEditor, "BATTERY CHARGE LIMIT", 0, ey, FontSection, HeaderColor);
            ey += S(24);
            _cboBattery = MakeComboIn(_pnlEditor, new[] { "Don't Change", "Full Charge (100%)", "Limit to 80% (Health)" }, 0, ey, editorW);
            ey += sectionGap;

            MakeLabelIn(_pnlEditor, "RGB KEYBOARD", 0, ey, FontSection, HeaderColor);
            ey += S(24);
            _cboRgbMode = MakeComboIn(_pnlEditor, RgbModeNames, 0, ey, editorW);
            ey += sectionGap;

            int halfW = (editorW - S(20)) / 2;

            MakeLabelIn(_pnlEditor, "BRIGHTNESS", 0, ey, FontSection, HeaderColor);
            _lblBrightVal = MakeLabelIn(_pnlEditor, "100%", halfW - S(40), ey, FontBody, SubHeaderColor);
            MakeLabelIn(_pnlEditor, "EFFECT SPEED", halfW + S(20), ey, FontSection, HeaderColor);
            _lblSpeedVal = MakeLabelIn(_pnlEditor, "50%", editorW - S(40), ey, FontBody, SubHeaderColor);
            ey += S(24);

            _trkBrightness = MakeSliderIn(_pnlEditor, 0, ey, halfW, 0, 100, 100);
            _trkBrightness.ValueChanged += (s, e) => _lblBrightVal.Text = $"{_trkBrightness.Value}%";
            _trkSpeed = MakeSliderIn(_pnlEditor, halfW + S(20), ey, halfW, 1, 100, 50);
            _trkSpeed.ValueChanged += (s, e) => _lblSpeedVal.Text = $"{_trkSpeed.Value}%";
            ey += sectionGap;

            MakeLabelIn(_pnlEditor, "COLOR CUSTOMIZATION", 0, ey, FontSection, HeaderColor);
            ey += S(24);

            _btnColorPick = new PredatorButton { Text = "    Choose Custom Color", Location = new Point(0, ey), Size = new Size(editorW, S(36)) };
            _btnColorPick.Paint += (s, pe) =>
            {
                if (_btnColorPick.Width <= S(16) || _btnColorPick.Height <= S(16)) return;
                var g = pe.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int cy = _btnColorPick.Height / 2;
                int dotOffset = (int)Math.Round(70 * _dpiScale);
                int cx = Math.Max(12, _btnColorPick.Width / 2 - dotOffset);
                using var glowBrush = new SolidBrush(Color.FromArgb(100, _selectedColor));
                g.FillEllipse(glowBrush, cx - S(8), cy - S(8), S(16), S(16));
                using var brush = new SolidBrush(_selectedColor);
                g.FillEllipse(brush, cx - S(6), cy - S(6), S(12), S(12));
            };
            _btnColorPick.Click += (s, ev) =>
            {
                using var dlg = new ColorDialog { FullOpen = true, Color = _selectedColor };
                var settings = AppSettings.Load();
                if (settings.CustomColors != null && settings.CustomColors.Length > 0)
                {
                    dlg.CustomColors = (int[])settings.CustomColors.Clone();
                }
                var res = (!this.IsDisposed && this.IsHandleCreated) ? dlg.ShowDialog(this) : dlg.ShowDialog();
                if (dlg.CustomColors != null)
                {
                    settings.CustomColors = (int[])dlg.CustomColors.Clone();
                    settings.Save();
                }
                if (res == DialogResult.OK)
                {
                    _selectedColor = dlg.Color;
                    _btnColorPick.Invalidate();
                }
            };
            _pnlEditor.Controls.Add(_btnColorPick);

            ey += sectionGap;

            int btnW = (editorW - S(16)) / 2;
            _btnSave = new PredatorButton { Text = "💾  Save Profile", Location = new Point(0, ey), Size = new Size(btnW, S(38)) };
            _pnlEditor.Controls.Add(_btnSave);
            _btnSave.Click += BtnSave_Click;

            _btnRemove = new PredatorButton { Text = "🗑  Remove", Location = new Point(btnW + S(12), ey), Size = new Size(btnW, S(38)) };
            _pnlEditor.Controls.Add(_btnRemove);
            _btnRemove.Click += BtnRemove_Click;

            _pnlEditor.AutoScrollMinSize = new Size(0, ey + S(38) + S(20));
        }

        #endregion

        #region UI Helpers

        private Label MakeLabel(string text, int x, int y, Font font, Color color)
        {
            var lbl = new Label { Text = text, Location = new Point(x, y), AutoSize = true, Font = font, ForeColor = color, BackColor = Color.Transparent };
            this.Controls.Add(lbl);
            return lbl;
        }

        private Label MakeLabelIn(Panel parent, string text, int x, int y, Font font, Color color)
        {
            var lbl = new Label { Text = text, Location = new Point(x, y), AutoSize = true, Font = font, ForeColor = color, BackColor = Color.Transparent };
            parent.Controls.Add(lbl);
            return lbl;
        }

        private PredatorButton MakeButton(string text, int x, int y, int w, int h)
        {
            var btn = new PredatorButton { Text = text, Location = new Point(x, y), Size = new Size(w, h) };
            this.Controls.Add(btn);
            return btn;
        }

        private PredatorDropDown MakeComboIn(Panel parent, string[] items, int x, int y, int w)
        {
            var dd = new PredatorDropDown
            {
                Location = new Point(x, y),
                Size = new Size(w, S(34))
            };
            foreach (var item in items) dd.Items.Add(item);
            if (dd.Items.Count > 0) dd.SelectedIndex = 0;
            parent.Controls.Add(dd);
            return dd;
        }

        private PredatorSlider MakeSliderIn(Panel parent, int x, int y, int w, int min, int max, int val)
        {
            var slider = new PredatorSlider
            {
                Location = new Point(x, y),
                Size = new Size(w, Math.Max(28, S(28))),
                Minimum = min,
                Maximum = max,
                Value = val
            };
            parent.Controls.Add(slider);
            return slider;
        }



        #endregion

        #region List Drawing

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _controller.GameDetected += OnControllerGameDetected;
            _controller.GameExited += OnControllerGameExited;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _controller.GameDetected -= OnControllerGameDetected;
            _controller.GameExited -= OnControllerGameExited;
            base.OnHandleDestroyed(e);
        }

        private void OnControllerGameDetected(GameProfile profile)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (IsDisposed || Disposing || _lstProfiles.IsDisposed) return;
                        _lstProfiles.Invalidate();
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void OnControllerGameExited(DashboardSnapshot snap)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (IsDisposed || Disposing || _lstProfiles.IsDisposed) return;
                        _lstProfiles.Invalidate();
                    }
                    catch { }
                }));
            }
            catch { }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            float factor = e.DeviceDpiOld > 0 ? (float)e.DeviceDpiNew / e.DeviceDpiOld : 1.0f;
            _dpiScale = e.DeviceDpiNew / 96f;
            if (Math.Abs(factor - 1.0f) > 0.001f)
            {
                try
                {
                    this.Scale(new SizeF(factor, factor));
                    RescaleControlFonts(this, factor);
                }
                catch { }
            }
        }

        private static void RescaleControlFonts(Control root, float factor)
        {
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

        private void LstProfiles_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _lstProfiles.Items.Count || e.Bounds.Width <= 0 || e.Bounds.Height <= 0) return;
            e.DrawBackground();

            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var bgBrush = new SolidBrush(selected ? (ThemeManager.IsDarkThemeActive ? Color.FromArgb(20, 50, 45) : Color.FromArgb(200, 240, 230)) : PanelBg);
            e.Graphics.FillRectangle(bgBrush, e.Bounds);

            string text = _lstProfiles.Items[e.Index]?.ToString() ?? "";
            using var textBrush = new SolidBrush(selected ? AccentColor : TextColor);
            var textRect = new Rectangle(e.Bounds.X + S(10), e.Bounds.Y, Math.Max(1, e.Bounds.Width - S(10)), Math.Max(1, e.Bounds.Height));
            e.Graphics.DrawString(text, FontBody, textBrush, textRect, s_sfCenter);

            if (selected)
            {
                using var accentPen = new Pen(AccentColor, 2f);
                e.Graphics.DrawLine(accentPen, e.Bounds.X, e.Bounds.Y, e.Bounds.X, e.Bounds.Bottom);
            }
        }

        #endregion

        #region Populate & Select

        private void PopulateList()
        {
            _lstProfiles.Items.Clear();
            foreach (var p in _controller.Profiles)
            {
                _lstProfiles.Items.Add(string.IsNullOrEmpty(p.DisplayName) ? p.ExecutableName : p.DisplayName);
            }
        }

        private void LstProfiles_SelectedIndexChanged(object? sender, EventArgs e)
        {
            int idx = _lstProfiles.SelectedIndex;
            if (idx < 0 || idx >= _controller.Profiles.Count)
            {
                _pnlEditor.Visible = false;
                _editingProfile = null;
                return;
            }

            _editingProfile = _controller.Profiles[idx];
            LoadProfileToEditor(_editingProfile);
            _pnlEditor.Visible = true;
        }

        private void LoadProfileToEditor(GameProfile p)
        {
            _lblExeName.Text = p.ExecutableName;

            int powerIdx = Array.IndexOf(PowerModeValues, p.PowerMode);
            _cboPower.SelectedIndex = powerIdx >= 0 ? powerIdx : 1;

            int fanIdx = Array.IndexOf(FanModeValues, p.FanMode);
            _cboFan.SelectedIndex = fanIdx >= 0 ? fanIdx : 0;

            bool isCustomFan = p.FanMode == 0x03;
            _trkCpuFan.Value = p.CpuFanSpeed >= 10 ? Math.Clamp(p.CpuFanSpeed, 10, 100) : 50;
            _trkGpuFan.Value = p.GpuFanSpeed >= 10 ? Math.Clamp(p.GpuFanSpeed, 10, 100) : 50;
            _lblCpuFanHdr.Text = $"CPU FAN: {_trkCpuFan.Value}%";
            _lblGpuFanHdr.Text = $"GPU FAN: {_trkGpuFan.Value}%";
            _lblCpuFanHdr.Visible = isCustomFan;
            _lblGpuFanHdr.Visible = isCustomFan;
            _trkCpuFan.Visible = isCustomFan;
            _trkGpuFan.Visible = isCustomFan;

            if (p.RefreshRate == -1) _cboRefresh.SelectedIndex = 0;
            else if (p.RefreshRate <= 60) _cboRefresh.SelectedIndex = 1;
            else _cboRefresh.SelectedIndex = 2;

            if (p.BatteryLimit == -1) _cboBattery.SelectedIndex = 0;
            else if (p.BatteryLimit == 0) _cboBattery.SelectedIndex = 1;
            else _cboBattery.SelectedIndex = 2;

            if (p.RgbMode == -1) _cboRgbMode.SelectedIndex = 0;
            else _cboRgbMode.SelectedIndex = Math.Clamp(p.RgbMode + 1, 0, RgbModeNames.Length - 1);

            _trkBrightness.Value = p.RgbBrightness >= 0 ? Math.Clamp(p.RgbBrightness, 0, 100) : 100;
            _trkSpeed.Value = p.RgbSpeed >= 0 ? Math.Clamp(p.RgbSpeed, 1, 100) : 50;

            int r = p.RgbR >= 0 ? Math.Clamp(p.RgbR, 0, 255) : 0;
            int g = p.RgbG >= 0 ? Math.Clamp(p.RgbG, 0, 255) : 200;
            int b = p.RgbB >= 0 ? Math.Clamp(p.RgbB, 0, 255) : 160;
            _selectedColor = Color.FromArgb(r, g, b);
            _btnColorPick.Invalidate();
        }

        private GameProfile EditorToProfile()
        {
            int powerIdx = Math.Clamp(_cboPower.SelectedIndex, 0, PowerModeValues.Length - 1);
            int fanIdx = Math.Clamp(_cboFan.SelectedIndex, 0, FanModeValues.Length - 1);

            var p = new GameProfile
            {
                ExecutableName = _editingProfile?.ExecutableName ?? "",
                DisplayName = _editingProfile?.DisplayName ?? "",
                PowerMode = PowerModeValues[powerIdx],
                FanMode = FanModeValues[fanIdx],
                CpuFanSpeed = fanIdx == 2 ? _trkCpuFan.Value : -1,
                GpuFanSpeed = fanIdx == 2 ? _trkGpuFan.Value : -1,
            };

            p.RefreshRate = _cboRefresh.SelectedIndex switch
            {
                1 => 60,
                2 => _maxHz,
                _ => -1
            };

            p.BatteryLimit = _cboBattery.SelectedIndex switch
            {
                1 => 0,
                2 => 1,
                _ => -1
            };

            p.RgbMode = _cboRgbMode.SelectedIndex <= 0 ? -1 : _cboRgbMode.SelectedIndex - 1;
            p.RgbBrightness = _trkBrightness.Value;
            p.RgbSpeed = _trkSpeed.Value;
            p.RgbR = _selectedColor.R;
            p.RgbG = _selectedColor.G;
            p.RgbB = _selectedColor.B;

            return p;
        }

        #endregion

        #region Actions

        private void BtnAdd_Click(object? sender, EventArgs e)
        {
            try
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Select an Application",
                    Filter = "Executables (*.exe)|*.exe",
                    CheckFileExists = true
                };

                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    string exeName = Path.GetFileName(ofd.FileName);
                    string displayName = Path.GetFileNameWithoutExtension(ofd.FileName);
                    TryAddProfile(exeName, displayName);
                }
            }
            catch
            {
                MessageBox.Show(this,
                    "Could not browse to that location.\n\nYou can find the process name in Task Manager while the game is running, and use 'Pick Running Process' to add it.",
                    "Browse Failed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void BtnPickRunning_Click(object? sender, EventArgs e)
        {
            var systemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "svchost", "csrss", "smss", "wininit", "winlogon", "services", "lsass",
                "fontdrvhost", "dwm", "conhost", "dllhost", "taskhostw", "sihost",
                "ctfmon", "spoolsv", "SearchIndexer", "WmiPrvSE", "RuntimeBroker",
                "ShellExperienceHost", "StartMenuExperienceHost", "TextInputHost",
                "SecurityHealthSystray", "SecurityHealthService", "MsMpEng",
                "NisSrv", "SgrmBroker", "audiodg", "PredatorControlApp", "System",
                "Registry", "Idle", "Memory Compression"
            };

            List<string> allProcs;
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var running = GameSyncController.GetRunningProcessNames();
                foreach (var exeName in running)
                {
                    string nameNoExt = Path.GetFileNameWithoutExtension(exeName);
                    if (!systemNames.Contains(nameNoExt))
                        names.Add(exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe");
                }
                allProcs = names.OrderBy(n => n).ToList();
            }
            catch
            {
                allProcs = new List<string>();
            }

            if (allProcs.Count == 0)
            {
                MessageBox.Show(this, "No running processes found.", "Pick Process",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var dlgBg    = ThemeManager.FormBg;
            var dlgTitle = ThemeManager.TitleBarBg;
            var dlgBorder= ThemeManager.CardBorder;
            var dlgText  = ThemeManager.TextPrimary;
            var dlgSub   = ThemeManager.TextSecondary;
            using var titleFont = new Font("Segoe UI", 10f, FontStyle.Bold);
            using var bodyFont  = new Font("Segoe UI", 9.25f);

            int dlgW = S(360);
            int dlgH = S(480);
            int titleH = S(40);
            int pad = S(14);

            using var dlg = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition   = FormStartPosition.CenterParent,
                ClientSize      = new Size(dlgW, dlgH),
                BackColor       = dlgBg,
                ForeColor       = dlgText
            };

            bool dragging = false; Point dragStart = Point.Empty;

            var pnlTitle = new Panel { Dock = DockStyle.Top, Height = titleH, BackColor = dlgTitle };
            pnlTitle.MouseDown += (s, ev) => { if (ev.Button == MouseButtons.Left) { dragging = true; dragStart = ev.Location; } };
            pnlTitle.MouseMove += (s, ev) => { if (dragging) dlg.Location = new Point(dlg.Left + ev.X - dragStart.X, dlg.Top + ev.Y - dragStart.Y); };
            pnlTitle.MouseUp   += (s, ev) => dragging = false;
            dlg.Controls.Add(pnlTitle);

            var lblTitle = new Label
            {
                Text = "Pick a Running Process",
                Font = titleFont, ForeColor = dlgText, AutoSize = true,
                Location = new Point(pad, (titleH - titleFont.Height) / 2), BackColor = Color.Transparent
            };
            lblTitle.MouseDown += (s, ev) => { if (ev.Button == MouseButtons.Left) { dragging = true; dragStart = ev.Location; } };
            pnlTitle.Controls.Add(lblTitle);

            var lblClose = new Label
            {
                Text = "●", ForeColor = Color.FromArgb(255, 95, 86),
                Font = FontClose, AutoSize = true,
                Cursor = Cursors.Hand, BackColor = Color.Transparent,
                Location = new Point(dlg.ClientSize.Width - S(24), S(10))
            };
            lblClose.Click += (s, ev) => dlg.Close();
            pnlTitle.Controls.Add(lblClose);

            dlg.Paint += (s, pe) =>
            {
                if (dlg.Width > 1 && dlg.Height > 1)
                {
                    using var pen = new Pen(dlgBorder, 1f);
                    pe.Graphics.DrawRectangle(pen, 0, 0, dlg.Width - 1, dlg.Height - 1);
                }
            };

            int searchH = S(30);
            int searchY = titleH + S(10);
            int innerW = dlgW - pad * 2;

            var pnlSearch = new Panel
            {
                Location = new Point(pad, searchY), Size = new Size(innerW, searchH),
                BackColor = dlgBorder
            };
            dlg.Controls.Add(pnlSearch);

            var txtSearch = new TextBox
            {
                Location = new Point(1, 1), Size = new Size(innerW - 2, searchH - 2),
                BackColor = ThemeManager.ControlBg, ForeColor = dlgText,
                Font = bodyFont, BorderStyle = BorderStyle.None,
                PlaceholderText = "Search..."
            };
            pnlSearch.Controls.Add(txtSearch);

            int btnH = S(34);
            int btnY = dlgH - pad - btnH;
            int listY = searchY + searchH + S(10);
            int listH = btnY - listY - S(10);

            var lstProcs = new ListBox
            {
                Location = new Point(pad, listY), Size = new Size(innerW, listH),
                BackColor = ThemeManager.ControlBg, ForeColor = dlgText,
                Font = bodyFont, BorderStyle = BorderStyle.None,
                SelectionMode = SelectionMode.One
            };
            dlg.Controls.Add(lstProcs);

            void RefreshList(string filter)
            {
                lstProcs.BeginUpdate();
                lstProcs.Items.Clear();
                var filtered = string.IsNullOrWhiteSpace(filter)
                    ? allProcs
                    : allProcs.Where(p => p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                foreach (var p in filtered) lstProcs.Items.Add(p);
                lstProcs.EndUpdate();
            }

            RefreshList("");
            txtSearch.TextChanged += (s, ev) => RefreshList(txtSearch.Text);

            var btnAdd2 = new PredatorButton
            {
                Text = "Add Selected", Location = new Point(pad, btnY), Size = new Size(innerW, btnH)
            };
            dlg.Controls.Add(btnAdd2);

            DialogResult result = DialogResult.Cancel;
            string? picked = null;

            void DoAdd()
            {
                if (lstProcs.SelectedItem is string sel)
                {
                    picked = sel;
                    result = DialogResult.OK;
                    dlg.Close();
                }
            }

            btnAdd2.Click     += (s, ev) => DoAdd();
            lstProcs.DoubleClick += (s, ev) => DoAdd();
            txtSearch.KeyDown += (s, ke) =>
            {
                if (ke.KeyCode == Keys.Down && lstProcs.Items.Count > 0)
                {
                    lstProcs.SelectedIndex = 0;
                    lstProcs.Focus();
                    ke.Handled = true;
                }
                else if (ke.KeyCode == Keys.Escape) dlg.Close();
            };
            lstProcs.KeyDown += (s, ke) =>
            {
                if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; DoAdd(); }
                else if (ke.KeyCode == Keys.Escape) dlg.Close();
            };

            IWin32Window? owner = (!this.IsDisposed && this.IsHandleCreated) ? this : null;
            if (owner != null) dlg.ShowDialog(owner); else dlg.ShowDialog();

            if (result == DialogResult.OK && picked != null)
            {
                string displayName = Path.GetFileNameWithoutExtension(picked);
                TryAddProfile(picked, displayName);
            }
        }

        private void TryAddProfile(string exeName, string displayName)
        {
            foreach (var existing in _controller.Profiles)
            {
                if (existing.ExecutableName.Equals(exeName, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, $"\"{exeName}\" is already configured.", "Duplicate", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            var profile = new GameProfile
            {
                ExecutableName = exeName,
                DisplayName = displayName,
                PowerMode = 0x01,
                FanMode = 0x01,
                RefreshRate = -1,
                BatteryLimit = -1,
                RgbMode = -1,
                RgbBrightness = -1,
                RgbSpeed = -1,
                RgbR = -1,
                RgbG = -1,
                RgbB = -1,
            };

            _controller.AddProfile(profile);
            PopulateList();
            _lstProfiles.SelectedIndex = _lstProfiles.Items.Count - 1;
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            if (_editingProfile == null) return;

            var updated = EditorToProfile();
            _controller.UpdateProfile(updated);

            int idx = _lstProfiles.SelectedIndex;
            PopulateList();
            if (idx >= 0 && idx < _lstProfiles.Items.Count)
                _lstProfiles.SelectedIndex = idx;
        }

        private void BtnRemove_Click(object? sender, EventArgs e)
        {
            if (_editingProfile == null) return;

            var result = MessageBox.Show(this,
                $"Remove \"{_editingProfile.ExecutableName}\" from Game Sync?",
                "Confirm Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                _controller.RemoveProfile(_editingProfile.ExecutableName);
                _editingProfile = null;
                _pnlEditor.Visible = false;
                PopulateList();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;
                _controller.GameDetected -= OnControllerGameDetected;
                _controller.GameExited -= OnControllerGameExited;
                try { Icon?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
