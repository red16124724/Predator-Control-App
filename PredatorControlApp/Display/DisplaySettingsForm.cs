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

        private Label _lblCurrentHz = null!;
        private ComboBox _cboDdsMode = null!;
        private Label _lblDdsStatus = null!;
        private Label _lblDdsBlocking = null!;
        private PredatorSwitch _switchOverdrive = null!;
        private PredatorButton _btnApplyDds = null!;

        private readonly Action _themeHandler;

        public DisplaySettingsForm(DisplayManager? displayManager = null, AppSettings? settings = null)
        {
            _displayManager = displayManager ?? DisplayManager.Instance;
            _settings = settings ?? AppSettings.Load();

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
            this.Size = new Size(720, 580);
            this.MinimumSize = new Size(680, 540);
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
                Text = "Advanced Optimus (DDS), Refresh Rate & Overdrive",
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

            var btn60Hz = new PredatorButton
            {
                Text = "60 Hz",
                Location = new Point(16, 64),
                Size = new Size(140, 32)
            };
            btn60Hz.Click += (s, e) => ApplyRefreshRate(60);
            cardRefresh.Controls.Add(btn60Hz);

            int maxHz = _displayManager.GetMaxRefreshRate();
            var btnMaxHz = new PredatorButton
            {
                Text = $"{maxHz} Hz (Maximum)",
                Location = new Point(170, 64),
                Size = new Size(160, 32)
            };
            btnMaxHz.Click += (s, e) => ApplyRefreshRate(maxHz);
            cardRefresh.Controls.Add(btnMaxHz);

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

            // Card 3: LCD Overdrive
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
            _lblCurrentHz.Text = $"Current: {_displayManager.CurrentRefreshRate} Hz";

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

            // LCD Overdrive
            _switchOverdrive.Checked = _displayManager.IsLcdOverdriveEnabled;
        }

        private void ApplyRefreshRate(int targetHz)
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
            }
            base.Dispose(disposing);
        }
    }
}
