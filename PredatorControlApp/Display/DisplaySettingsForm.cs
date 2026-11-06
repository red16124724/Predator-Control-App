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
        private readonly WmiController _wmi = new();

        private static readonly Font s_fontMain = new("Segoe UI", 9.5f, FontStyle.Regular);
        private static readonly Font s_fontTitle = new("Segoe UI", 16f, FontStyle.Bold);
        private static readonly Font s_fontSubtitle = new("Segoe UI", 8.5f, FontStyle.Regular);
        private static readonly Font s_fontCardTitle = new("Segoe UI", 9.5f, FontStyle.Bold);
        private static readonly Font s_fontCurrentHz = new("Segoe UI", 10f, FontStyle.Bold);
        private static readonly Font s_fontDdsStatus = new("Segoe UI", 9.5f, FontStyle.Regular);
        private static readonly Font s_fontDdsNotice = new("Segoe UI", 8.25f, FontStyle.Italic);

        private Label _lblCurrentHz = null!;
        private Label _lblMuxStatus = null!;
        private Label _lblMuxNotice = null!;
        private PredatorButton _btnMuxAuto = null!;
        private PredatorButton _btnMuxIntegrated = null!;
        private PredatorButton _btnMuxDiscrete = null!;
        private PredatorSwitch _switchOverdrive = null!;

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
                Text = "Hardware MUX Switch (BIOS Restart Required), Refresh Rate & Overdrive",
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

            // Card 2: Hardware MUX Switch (BIOS Reboot Required)
            var cardMux = CreateCard(20, y, 660, 150, "HARDWARE MUX SWITCH (BIOS REBOOT REQUIRED)");
            mainPanel.Controls.Add(cardMux);

            _lblMuxStatus = new Label
            {
                Text = "Status: Querying BIOS hardware MUX switch...",
                Location = new Point(16, 32),
                AutoSize = true,
                ForeColor = ThemeManager.Accent,
                Font = s_fontDdsStatus
            };
            cardMux.Controls.Add(_lblMuxStatus);

            _btnMuxAuto = new PredatorButton
            {
                Text = "Automatic (Optimus MUX)",
                Location = new Point(16, 64),
                Size = new Size(195, 34)
            };
            _btnMuxAuto.Click += (s, e) => ApplyMuxMode(GpuMode.Auto);
            cardMux.Controls.Add(_btnMuxAuto);

            _btnMuxIntegrated = new PredatorButton
            {
                Text = "Integrated Only (iGPU MUX)",
                Location = new Point(220, 64),
                Size = new Size(205, 34)
            };
            _btnMuxIntegrated.Click += (s, e) => ApplyMuxMode(GpuMode.iGpuOnly);
            cardMux.Controls.Add(_btnMuxIntegrated);

            _btnMuxDiscrete = new PredatorButton
            {
                Text = "Discrete Only (dGPU MUX)",
                Location = new Point(435, 64),
                Size = new Size(195, 34)
            };
            _btnMuxDiscrete.Click += (s, e) => ApplyMuxMode(GpuMode.dGpuOnly);
            cardMux.Controls.Add(_btnMuxDiscrete);

            _lblMuxNotice = new Label
            {
                Text = "Note: Hardware MUX switch changes configure UEFI/BIOS routing and require a system restart to take effect.",
                Location = new Point(16, 112),
                Size = new Size(620, 32),
                ForeColor = ThemeManager.TextSecondary,
                Font = s_fontDdsNotice
            };
            cardMux.Controls.Add(_lblMuxNotice);

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

            // Query active BIOS MUX switch mode directly
            GpuMode? curGpu = _wmi.GetGpuMode();
            var biosItem = Bios.BiosManager.Instance.Settings.FirstOrDefault(s => s.Name.Contains("Display MUX", StringComparison.OrdinalIgnoreCase));
            if (!curGpu.HasValue && biosItem != null && !string.IsNullOrEmpty(biosItem.CurrentValue))
            {
                if (biosItem.CurrentValue.Contains("Discrete", StringComparison.OrdinalIgnoreCase)) curGpu = GpuMode.dGpuOnly;
                else if (biosItem.CurrentValue.Contains("Optimus", StringComparison.OrdinalIgnoreCase)) curGpu = GpuMode.iGpuOnly;
                else curGpu = GpuMode.Auto;
            }
            curGpu ??= (GpuMode)Math.Clamp(_settings.GpuMode, 0, 2);

            string activeMuxStr = curGpu switch
            {
                GpuMode.dGpuOnly => "Discrete Only (dGPU MUX Active - BIOS)",
                GpuMode.iGpuOnly => "Integrated Only (iGPU MUX Active - BIOS)",
                _ => "Automatic (Optimus MUX Active - BIOS)"
            };
            _lblMuxStatus.Text = $"Status: Active BIOS MUX Mode: {activeMuxStr}";
            _lblMuxStatus.ForeColor = ThemeManager.SuccessGreen;

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

        private void ApplyMuxMode(GpuMode mode)
        {
            string modeName = mode switch
            {
                GpuMode.Auto => "Automatic (Optimus MUX)",
                GpuMode.iGpuOnly => "Integrated Only (iGPU MUX)",
                GpuMode.dGpuOnly => "Discrete Only (dGPU MUX)",
                _ => mode.ToString()
            };

            GpuMode? curGpu = _wmi.GetGpuMode();
            var biosItem = Bios.BiosManager.Instance.Settings.FirstOrDefault(s => s.Name.Contains("Display MUX", StringComparison.OrdinalIgnoreCase));
            if (!curGpu.HasValue && biosItem != null && !string.IsNullOrEmpty(biosItem.CurrentValue))
            {
                if (biosItem.CurrentValue.Contains("Discrete", StringComparison.OrdinalIgnoreCase)) curGpu = GpuMode.dGpuOnly;
                else if (biosItem.CurrentValue.Contains("Optimus", StringComparison.OrdinalIgnoreCase)) curGpu = GpuMode.iGpuOnly;
                else curGpu = GpuMode.Auto;
            }

            if (curGpu.HasValue && curGpu.Value == mode)
            {
                _lblMuxStatus.Text = $"Status: Already set to {modeName} (Active in BIOS)";
                _lblMuxStatus.ForeColor = ThemeManager.SuccessGreen;
                return;
            }

            string biosOption = mode switch
            {
                GpuMode.Auto => "Auto",
                GpuMode.iGpuOnly => "Optimus",
                GpuMode.dGpuOnly => "Discrete GPU only",
                _ => "Auto"
            };

            if (biosItem != null)
            {
                biosItem.PendingValue = biosOption;
                Bios.BiosManager.Instance.ApplySetting(biosItem, out _, bypassSafetyGates: true);
            }

            _wmi.SetGpuMode(mode);

            _settings.GpuMode = (int)mode;
            _settings.SetBiosSettingValue("Display MUX (Advanced Optimus / dGPU)", biosOption);
            _settings.Save();

            _lblMuxStatus.Text = $"Status: Hardware MUX set to {modeName} (Reboot Required)";
            _lblMuxStatus.ForeColor = ThemeManager.YellowStatus;

            var res = ShowMessage(
                $"Hardware MUX switch set to {modeName} in BIOS.\r\n\r\nA system restart is required for the display multiplexer to re-route the internal display in firmware. Would you like to restart now?",
                "Hardware MUX Restart Required",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (res == DialogResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });
                }
                catch (Exception ex)
                {
                    Program.Report(ex, false);
                }
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
