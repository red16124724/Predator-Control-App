using System;
using System.Drawing;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PredatorControlApp.Display
{
    [SupportedOSPlatform("windows")]
    public class DisplaySettingsForm : Form
    {
        private readonly DisplayManager _displayManager;
        private readonly AppSettings _settings;

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

        public DisplaySettingsForm(DisplayManager? displayManager = null, AppSettings? settings = null)
        {
            _displayManager = displayManager ?? DisplayManager.Instance;
            _settings = settings ?? AppSettings.Load();

            InitializeComponent();
            ApplyTheme();
            LoadCurrentValues();
        }

        private void InitializeComponent()
        {
            this.Text = "Display & GPU Switching";
            this.Size = new Size(580, 660);
            this.MinimumSize = new Size(540, 600);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

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
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                Location = new Point(20, y),
                AutoSize = true
            };
            mainPanel.Controls.Add(lblTitle);
            y += 32;

            var lblSubtitle = new Label
            {
                Text = "Advanced Optimus (DDS), Dynamic Refresh Rate, BlueLight Shield™ & Overdrive",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                Location = new Point(22, y),
                AutoSize = true,
                ForeColor = ThemeManager.TextSecondary
            };
            mainPanel.Controls.Add(lblSubtitle);
            y += 35;

            // Card 1: Refresh Rate
            var cardRefresh = CreateCard(20, y, 520, 110, "Display Refresh Rate");
            mainPanel.Controls.Add(cardRefresh);

            _lblCurrentHz = new Label
            {
                Text = $"Current: {_displayManager.CurrentRefreshRate} Hz",
                Location = new Point(16, 32),
                AutoSize = true,
                ForeColor = ThemeManager.Accent,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
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
                Size = new Size(130, 28),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cardRefresh.Controls.Add(_cboRefreshRates);

            _btnApplyRefresh = new PredatorButton
            {
                Text = "Apply Hz",
                Location = new Point(260, 63),
                Size = new Size(110, 30)
            };
            _btnApplyRefresh.Click += OnApplyRefreshClicked;
            cardRefresh.Controls.Add(_btnApplyRefresh);

            y += 125;

            // Card 2: NVIDIA Advanced Optimus / DDS
            var cardDds = CreateCard(20, y, 520, 150, "NVIDIA Advanced Optimus (Dynamic Display Switch)");
            mainPanel.Controls.Add(cardDds);

            _lblDdsStatus = new Label
            {
                Text = "Status: Detecting DDS hardware...",
                Location = new Point(16, 32),
                AutoSize = true,
                ForeColor = ThemeManager.TextPrimary,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
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
                Size = new Size(240, 28),
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
                Location = new Point(365, 63),
                Size = new Size(135, 30)
            };
            _btnApplyDds.Click += OnApplyDdsClicked;
            cardDds.Controls.Add(_btnApplyDds);

            _lblDdsBlocking = new Label
            {
                Text = "Note: Switching GPU mode takes effect immediately without reboot.",
                Location = new Point(16, 108),
                Size = new Size(485, 34),
                ForeColor = ThemeManager.TextSecondary,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Italic)
            };
            cardDds.Controls.Add(_lblDdsBlocking);

            y += 165;

            // Card 3: BlueLight Shield
            var cardBlueLight = CreateCard(20, y, 520, 130, "Acer BlueLight Shield™ (Hardware Gamma Ramp)");
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
                Size = new Size(360, 45),
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
                Text = "Off       Low (18%)       High (36%)       Ultra (54%)",
                Location = new Point(20, 102),
                AutoSize = true,
                ForeColor = ThemeManager.TextMuted,
                Font = new Font("Segoe UI", 8f)
            };
            cardBlueLight.Controls.Add(lblLevels);

            var btnResetBlue = new PredatorButton
            {
                Text = "Turn Off",
                Location = new Point(395, 62),
                Size = new Size(105, 30)
            };
            btnResetBlue.Click += (s, e) => _sliderBlueLight.Value = 0;
            cardBlueLight.Controls.Add(btnResetBlue);

            y += 145;

            // Card 4: LCD Overdrive
            var cardOverdrive = CreateCard(20, y, 520, 80, "Panel Response Time Acceleration");
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
                Location = new Point(445, 32),
                Size = new Size(50, 26)
            };
            _switchOverdrive.CheckedChanged += OnOverdriveChanged;
            cardOverdrive.Controls.Add(_switchOverdrive);

            y += 95;

            // Bottom Close Button
            var btnClose = new PredatorButton
            {
                Text = "Close",
                Location = new Point(415, y),
                Size = new Size(125, 36)
            };
            btnClose.Click += (s, e) => this.Close();
            mainPanel.Controls.Add(btnClose);
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
                using var pen = new Pen(ThemeManager.CardBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, width - 1, height - 1);
            };

            var lblCardTitle = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
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
            }
            else
            {
                _lblDdsStatus.Text = "Status: Advanced Optimus / DDS not detected or unsupported on current display.";
                _lblDdsStatus.ForeColor = ThemeManager.TextSecondary;
                _cboDdsMode.SelectedIndex = 1;
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
            if (_cboRefreshRates.SelectedItem == null) return;
            string sel = _cboRefreshRates.SelectedItem.ToString() ?? "";
            string num = new string(sel.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(num, out int targetHz))
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
                    MessageBox.Show(this, $"Failed to apply refresh rate {targetHz} Hz.", "Display Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void OnApplyDdsClicked(object? sender, EventArgs e)
        {
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
                MessageBox.Show(this, "Failed to switch GPU display mode. Ensure NVIDIA drivers are active.", "GPU Switch Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnBlueLightChanged(object? sender, EventArgs e)
        {
            int val = _sliderBlueLight.Value;
            UpdateBlueLightLabel(val);
            _displayManager.SetBlueLightShield(val);
            _settings.BlueLightShieldMode = val;
            _settings.Save();
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
    }
}
