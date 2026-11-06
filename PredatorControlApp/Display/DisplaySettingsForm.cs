using System;
using System.Drawing;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PredatorControlApp.Display
{
    [SupportedOSPlatform("windows")]
    public class DisplaySettingsForm : Form
    {
        public static Func<IWin32Window?, string, string, MessageBoxButtons, MessageBoxIcon, DialogResult> MessageBoxHandler { get; set; } =
            (owner, text, caption, buttons, icon) => MessageBox.Show(owner, text, caption, buttons, icon);

        private DialogResult ShowMessage(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBoxHandler(this, text, caption, buttons, icon);
        }

        private readonly DisplayManager _displayManager;
        private readonly AppSettings _settings;

        private static readonly Font s_fontMain = new("Segoe UI", 9.5f, FontStyle.Regular);
        private static readonly Font s_fontTitle = new("Segoe UI", 16f, FontStyle.Bold);
        private static readonly Font s_fontSubtitle = new("Segoe UI", 8.5f, FontStyle.Regular);
        private static readonly Font s_fontCardTitle = new("Segoe UI", 9.5f, FontStyle.Bold);
        private static readonly Font s_fontCurrentHz = new("Segoe UI", 10f, FontStyle.Bold);
        private static readonly Font s_fontDdsStatus = new("Segoe UI", 9.5f, FontStyle.Regular);
        private static readonly Font s_fontDdsNotice = new("Segoe UI", 8.25f, FontStyle.Italic);
        private static readonly Font s_fontLevels = new("Segoe UI", 8f);

        private ComboBox _cboRefreshRates = null!;
        private Label _lblCurrentHz = null!;
        private ComboBox _cboDdsMode = null!;
        private Label _lblDdsStatus = null!;
        private Label _lblDdsBlocking = null!;
        private TrackBar _sliderBlueLight = null!;
        private Label _lblBlueLightVal = null!;
        private PredatorSwitch _switchOverdrive = null!;
        private PredatorButton _btnApplyRefresh = null!;
        private PredatorButton _btnApplyDds = null!;

        private readonly System.Windows.Forms.Timer _blueLightSaveTimer;
        private readonly Action _themeHandler;

        public DisplaySettingsForm(DisplayManager? displayManager = null, AppSettings? settings = null)
        {
            _displayManager = displayManager ?? DisplayManager.Instance;
            _settings = settings ?? AppSettings.Load();

            _blueLightSaveTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _blueLightSaveTimer.Tick += (s, e) =>
            {
                _blueLightSaveTimer.Stop();
                try
                {
                    if (_sliderBlueLight != null && !_sliderBlueLight.IsDisposed)
                    {
                        _settings.BlueLightShieldMode = _sliderBlueLight.Value;
                        _settings.Save();
                    }
                }
                catch { }
            };

            InitializeComponent();
            ApplyTheme();
            LoadCurrentValues();

            _themeHandler = () => SafeBeginInvoke(ApplyTheme);
            ThemeManager.ThemeChanged += _themeHandler;
        }

        private void SafeBeginInvoke(Action action)
        {
            try
            {
                if (IsDisposed || Disposing) return;
                if (IsHandleCreated && InvokeRequired)
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (!IsDisposed && !Disposing) action();
                        }
                        catch { }
                    }));
                }
                else
                {
                    if (!IsDisposed && !Disposing) action();
                }
            }
            catch { }
        }

        private void InitializeComponent()
        {
            this.Text = "Display & GPU Switching";
            this.Size = new Size(720, 720);
            this.MinimumSize = new Size(680, 680);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.AutoScaleMode = AutoScaleMode.None;
            this.Font = s_fontMain;

            var mainPanel = new DarkScrollPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
                AutoScroll = true
            };
            this.Controls.Add(mainPanel);

            int y = 15;

            // Header Title
            var lblTitle = new Label
            {
                Text = "Display & GPU Switching",
                Font = s_fontTitle,
                Location = new Point(20, y),
                AutoSize = true
            };
            mainPanel.Controls.Add(lblTitle);
            y += 32;

            var lblSubtitle = new Label
            {
                Text = "Advanced Optimus (DDS), Dynamic Refresh Rate, BlueLight Shield™ & Overdrive",
                Font = s_fontSubtitle,
                Location = new Point(22, y),
                AutoSize = true,
                ForeColor = ThemeManager.TextSecondary
            };
            mainPanel.Controls.Add(lblSubtitle);
            y += 35;

            // Card 1: Refresh Rate
            var cardRefresh = CreateCard(20, y, 660, 110, "Display Refresh Rate");
            mainPanel.Controls.Add(cardRefresh);

            _lblCurrentHz = new Label
            {
                Text = $"Current: {_displayManager.CurrentRefreshRate} Hz",
                Location = new Point(16, 32),
                AutoSize = true,
                ForeColor = ThemeManager.Accent,
                Font = s_fontCurrentHz
            };
            cardRefresh.Controls.Add(_lblCurrentHz);

            var lblSelectHz = new Label
            {
                Text = "Target Rate:",
                Location = new Point(16, 68),
                AutoSize = true
            };
            cardRefresh.Controls.Add(lblSelectHz);

            _cboRefreshRates = new ComboBox
            {
                Location = new Point(110, 64),
                Size = new Size(160, 28),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cardRefresh.Controls.Add(_cboRefreshRates);

            _btnApplyRefresh = new PredatorButton
            {
                Text = "Apply Hz",
                Location = new Point(290, 63),
                Size = new Size(120, 30)
            };
            _btnApplyRefresh.Click += OnApplyRefreshClicked;
            cardRefresh.Controls.Add(_btnApplyRefresh);

            y += 125;

            // Card 2: NVIDIA Advanced Optimus / DDS
            var cardDds = CreateCard(20, y, 660, 150, "NVIDIA Advanced Optimus (Dynamic Display Switch)");
            mainPanel.Controls.Add(cardDds);

            _lblDdsStatus = new Label
            {
                Text = "Status: Detecting DDS hardware...",
                Location = new Point(16, 32),
                AutoSize = true,
                ForeColor = ThemeManager.TextPrimary,
                Font = s_fontDdsStatus
            };
            cardDds.Controls.Add(_lblDdsStatus);

            var lblDdsMode = new Label
            {
                Text = "GPU Mode:",
                Location = new Point(16, 68),
                AutoSize = true
            };
            cardDds.Controls.Add(lblDdsMode);

            _cboDdsMode = new ComboBox
            {
                Location = new Point(110, 64),
                Size = new Size(350, 28),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cboDdsMode.Items.AddRange(new object[]
            {
                "Optimus (iGPU - Battery Saver)",
                "Automatic (Advanced Optimus - Seamless)",
                "NVIDIA GPU Only (Discrete dGPU - Max Performance)"
            });
            cardDds.Controls.Add(_cboDdsMode);

            _btnApplyDds = new PredatorButton
            {
                Text = "Switch Mode",
                Location = new Point(480, 63),
                Size = new Size(145, 30)
            };
            _btnApplyDds.Click += OnApplyDdsClicked;
            cardDds.Controls.Add(_btnApplyDds);

            _lblDdsBlocking = new Label
            {
                Text = "Note: Switching GPU mode takes effect immediately without reboot.",
                Location = new Point(16, 108),
                Size = new Size(610, 34),
                ForeColor = ThemeManager.TextSecondary,
                Font = s_fontDdsNotice
            };
            cardDds.Controls.Add(_lblDdsBlocking);

            y += 165;

            // Card 3: BlueLight Shield
            var cardBlueLight = CreateCard(20, y, 660, 130, "Acer BlueLight Shield™ (Hardware Gamma Ramp)");
            mainPanel.Controls.Add(cardBlueLight);

            _lblBlueLightVal = new Label
            {
                Text = "Level: Off (Natural Colors)",
                Location = new Point(16, 32),
                AutoSize = true,
                ForeColor = ThemeManager.TextPrimary
            };
            cardBlueLight.Controls.Add(_lblBlueLightVal);

            _sliderBlueLight = new TrackBar
            {
                Location = new Point(16, 62),
                Size = new Size(440, 45),
                Minimum = 0,
                Maximum = 3,
                Value = 0,
                TickStyle = TickStyle.BottomRight,
                TickFrequency = 1
            };
            _sliderBlueLight.ValueChanged += OnBlueLightChanged;
            cardBlueLight.Controls.Add(_sliderBlueLight);

            var lblLevels = new Label
            {
                Text = "Off             Low (18%)             High (36%)             Ultra (54%)",
                Location = new Point(20, 102),
                AutoSize = true,
                ForeColor = ThemeManager.TextMuted,
                Font = s_fontLevels
            };
            cardBlueLight.Controls.Add(lblLevels);

            var btnResetBlue = new PredatorButton
            {
                Text = "Turn Off",
                Location = new Point(480, 62),
                Size = new Size(105, 30)
            };
            btnResetBlue.Click += (s, e) => _sliderBlueLight.Value = 0;
            cardBlueLight.Controls.Add(btnResetBlue);

            y += 145;

            // Card 4: LCD Overdrive
            var cardOverdrive = CreateCard(20, y, 660, 80, "Panel Response Time Acceleration");
            mainPanel.Controls.Add(cardOverdrive);

            var lblOverdrive = new Label
            {
                Text = "LCD Overdrive (3ms Response Boost):",
                Location = new Point(16, 36),
                AutoSize = true
            };
            cardOverdrive.Controls.Add(lblOverdrive);

            _switchOverdrive = new PredatorSwitch
            {
                Location = new Point(570, 32),
                Size = new Size(50, 26)
            };
            _switchOverdrive.CheckedChanged += OnOverdriveChanged;
            cardOverdrive.Controls.Add(_switchOverdrive);

            y += 95;

            // Bottom Close Button
            var btnClose = new PredatorButton
            {
                Text = "Close",
                Location = new Point(535, y),
                Size = new Size(125, 36)
            };
            btnClose.Click += (s, e) => this.Close();
            mainPanel.Controls.Add(btnClose);
            this.CancelButton = btnClose;
        }

        private Panel CreateCard(int x, int y, int width, int height, string title)
        {
            var panel = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = ThemeManager.CardBg,
                BorderStyle = BorderStyle.None
            };
            panel.Paint += (s, e) =>
            {
                if (panel.Width <= 1 || panel.Height <= 1) return;
                using var pen = new Pen(ThemeManager.CardBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
            };

            var lblCardTitle = new Label
            {
                Text = title,
                Font = s_fontCardTitle,
                ForeColor = ThemeManager.Accent,
                Location = new Point(14, 10),
                AutoSize = true
            };
            panel.Controls.Add(lblCardTitle);

            return panel;
        }

        private void ApplyTheme()
        {
            this.BackColor = ThemeManager.FormBg;
            this.ForeColor = ThemeManager.TextPrimary;
        }

        private void LoadCurrentValues()
        {
            // Populate refresh rates
            var rates = _displayManager.GetSupportedRefreshRates();
            _cboRefreshRates.Items.Clear();
            foreach (var r in rates)
            {
                _cboRefreshRates.Items.Add($"{r} Hz");
            }

            int currentHz = _displayManager.CurrentRefreshRate;
            int selectIdx = Array.IndexOf(rates, currentHz);
            if (selectIdx >= 0 && selectIdx < _cboRefreshRates.Items.Count)
                _cboRefreshRates.SelectedIndex = selectIdx;
            else if (_cboRefreshRates.Items.Count > 0)
                _cboRefreshRates.SelectedIndex = 0;

            _lblCurrentHz.Text = $"Current: {currentHz} Hz";

            // Populate DDS Mode
            var ddsStatus = _displayManager.GetDdsStatus();
            if (ddsStatus.IsSupported)
            {
                _cboDdsMode.Enabled = true;
                _btnApplyDds.Enabled = true;

                string activeMuxStr = ddsStatus.ActiveMux switch
                {
                    1 => "Integrated (Optimus)",
                    2 => "Discrete (NVIDIA GPU)",
                    _ => "Unknown"
                };
                _lblDdsStatus.Text = $"Status: Supported | Active Display MUX: {activeMuxStr}";
                _lblDdsStatus.ForeColor = ThemeManager.SuccessGreen;

                int modeIdx = _displayManager.CurrentDdsMode;
                if (modeIdx >= 0 && modeIdx < _cboDdsMode.Items.Count)
                    _cboDdsMode.SelectedIndex = modeIdx;
                else
                    _cboDdsMode.SelectedIndex = 1;

                if (ddsStatus.HasBlockingApps)
                {
                    _lblDdsBlocking.Text = "Warning: Some active applications may block instant display switching.";
                    _lblDdsBlocking.ForeColor = ThemeManager.YellowStatus;
                }
                else
                {
                    _lblDdsBlocking.Text = "Note: Switching GPU mode takes effect immediately without reboot.";
                    _lblDdsBlocking.ForeColor = ThemeManager.TextSecondary;
                }
            }
            else
            {
                _lblDdsStatus.Text = "Status: Advanced Optimus / DDS not detected or unsupported on current display.";
                _lblDdsStatus.ForeColor = ThemeManager.TextSecondary;
                _cboDdsMode.Enabled = false;
                _btnApplyDds.Enabled = false;
                _cboDdsMode.SelectedIndex = 1;
                _lblDdsBlocking.Text = "Note: DDS switching requires hardware Dynamic Display Switch support and active NVIDIA drivers.";
                _lblDdsBlocking.ForeColor = ThemeManager.TextSecondary;
            }

            // BlueLight Shield
            int blMode = _displayManager.BlueLightMode;
            _sliderBlueLight.Value = Math.Clamp(blMode, 0, 3);
            UpdateBlueLightLabel(_sliderBlueLight.Value);

            // LCD Overdrive
            _switchOverdrive.Checked = _displayManager.IsLcdOverdriveEnabled;
        }

        private void OnApplyRefreshClicked(object? sender, EventArgs e)
        {
            if (_cboRefreshRates.SelectedItem == null)
            {
                ShowMessage("Please select a target refresh rate first.", "Display Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string sel = _cboRefreshRates.SelectedItem.ToString() ?? "";
            var match = Regex.Match(sel, @"\d+");
            if (match.Success && int.TryParse(match.Value, out int targetHz) && targetHz >= 30)
            {
                bool ok = _displayManager.SetRefreshRate(targetHz);
                if (ok)
                {
                    _lblCurrentHz.Text = $"Current: {targetHz} Hz";
                    _settings.RefreshRate = targetHz;
                    _settings.Save();
                }
                else
                {
                    ShowMessage($"Failed to apply refresh rate {targetHz} Hz.", "Display Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else
            {
                ShowMessage($"Invalid refresh rate selection '{sel}'. Please select a valid rate.", "Display Selection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnApplyDdsClicked(object? sender, EventArgs e)
        {
            var status = _displayManager.GetDdsStatus();
            if (!status.IsSupported)
            {
                ShowMessage("NVIDIA Advanced Optimus (DDS) is not supported or NVIDIA drivers are unavailable on this display.", "GPU Switch Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int mode = _cboDdsMode.SelectedIndex;
            if (mode < 0 || mode > 2) return;

            bool ok = _displayManager.SetDdsMode(mode);
            if (ok)
            {
                _settings.DdsMode = mode;
                _settings.Save();
                LoadCurrentValues();
            }
            else
            {
                ShowMessage("Failed to switch GPU display mode. Ensure NVIDIA drivers are active.", "GPU Switch Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnBlueLightChanged(object? sender, EventArgs e)
        {
            if (_sliderBlueLight == null) return;
            int val = _sliderBlueLight.Value;
            UpdateBlueLightLabel(val);
            _displayManager.SetBlueLightShield(val);
            _settings.BlueLightShieldMode = val;
            _blueLightSaveTimer.Stop();
            _blueLightSaveTimer.Start();
        }

        private void UpdateBlueLightLabel(int level)
        {
            _lblBlueLightVal.Text = level switch
            {
                0 => "Level: Off (Natural Colors - 0% Reduction)",
                1 => "Level: Low (Subtle Warmth - 18% Blue Reduction)",
                2 => "Level: High (Reduced Fatigue - 36% Blue Reduction)",
                3 => "Level: Ultra (Night Mode - 54% Blue Reduction)",
                _ => $"Level: Custom ({level}% Reduction)"
            };
        }

        private void OnOverdriveChanged(object? sender, EventArgs e)
        {
            bool enabled = _switchOverdrive.Checked;
            _displayManager.SetLcdOverdrive(enabled);
            _settings.LcdOverdrive = enabled;
            _settings.Save();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;
                if (_blueLightSaveTimer != null)
                {
                    _blueLightSaveTimer.Stop();
                    try
                    {
                        if (_sliderBlueLight != null && !_sliderBlueLight.IsDisposed)
                        {
                            _settings.BlueLightShieldMode = _sliderBlueLight.Value;
                            _settings.Save();
                        }
                    }
                    catch { }
                    _blueLightSaveTimer.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
