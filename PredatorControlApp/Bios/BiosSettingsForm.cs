using System;
using System.Drawing;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PredatorControlApp.Bios
{
    [SupportedOSPlatform("windows")]
    public class BiosSettingsForm : Form
    {
        public static Func<IWin32Window?, string, string, MessageBoxButtons, MessageBoxIcon, DialogResult> MessageBoxHandler { get; set; } =
            (owner, text, caption, buttons, icon) => MessageBox.Show(owner, text, caption, buttons, icon);

        private DialogResult ShowMessage(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBoxHandler(this, text, caption, buttons, icon);
        }

        private readonly BiosManager _biosManager;
        private readonly AppSettings _settings;

        private static readonly Font s_fontMain = new("Segoe UI", 9.5f, FontStyle.Regular);
        private static readonly Font s_fontTitle = new("Segoe UI", 15f, FontStyle.Bold);
        private static readonly Font s_fontModelBadge = new("Segoe UI", 9f, FontStyle.Bold);
        private static readonly Font s_fontModeBadge = new("Segoe UI", 8.5f, FontStyle.Italic);
        private static readonly Font s_fontSafetyTitle = new("Segoe UI", 9f, FontStyle.Bold);
        private static readonly Font s_fontSettingName = new("Segoe UI", 9.5f, FontStyle.Bold);
        private static readonly Font s_fontRiskBadge = new("Segoe UI", 7.5f, FontStyle.Bold);
        private static readonly Font s_fontCurrentVal = new("Segoe UI", 8.5f);

        private Label _lblModelBadge = null!;
        private Label _lblModeBadge = null!;
        private CheckBox _chkEnableModifications = null!;
        private CheckBox _chkAllowDangerous = null!;
        private PredatorButton _btnRestoreDefaults = null!;
        private TabControl _tabControl = null!;

        private readonly Action _themeHandler;
        private bool _isUpdatingSafetyGates;

        public BiosSettingsForm(BiosManager? biosManager = null, AppSettings? settings = null)
        {
            _biosManager = biosManager ?? BiosManager.Instance;
            _settings = settings ?? AppSettings.Load();

            InitializeComponent();
            ApplyTheme();
            PopulateCategories();

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
            this.Text = "Acer BIOS & Firmware Settings";
            this.Size = new Size(820, 800);
            this.MinimumSize = new Size(760, 740);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.AutoScaleMode = AutoScaleMode.None;
            this.Font = s_fontMain;

            var mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16)
            };
            this.Controls.Add(mainPanel);

            int y = 10;

            // Header Title
            var lblTitle = new Label
            {
                Text = "BIOS & Firmware Settings",
                Font = s_fontTitle,
                Location = new Point(16, y),
                AutoSize = true
            };
            mainPanel.Controls.Add(lblTitle);
            y += 30;

            // Badges row
            _lblModelBadge = new Label
            {
                Text = _biosManager.BiosBadgeText,
                Font = s_fontModelBadge,
                ForeColor = ThemeManager.Accent,
                Location = new Point(18, y),
                AutoSize = true
            };
            mainPanel.Controls.Add(_lblModelBadge);

            _lblModeBadge = new Label
            {
                Text = $"[{_biosManager.GetSupportModeDescription()}]",
                Font = s_fontModeBadge,
                ForeColor = ThemeManager.TextSecondary,
                Location = new Point(_lblModelBadge.Right + 12, y),
                AutoSize = true
            };
            mainPanel.Controls.Add(_lblModeBadge);
            y += 28;

            // Safety Interlock Card
            var cardSafety = new Panel
            {
                Location = new Point(16, y),
                Size = new Size(770, 75),
                BackColor = ThemeManager.CardBg,
                BorderStyle = BorderStyle.None
            };
            cardSafety.Paint += (s, e) =>
            {
                if (cardSafety.Width <= 1 || cardSafety.Height <= 1) return;
                using var pen = new Pen(ThemeManager.CardBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, cardSafety.Width - 1, cardSafety.Height - 1);
            };
            mainPanel.Controls.Add(cardSafety);

            var lblSafetyTitle = new Label
            {
                Text = "Safety Interlocks & Access Control",
                Font = s_fontSafetyTitle,
                ForeColor = ThemeManager.TextPrimary,
                Location = new Point(12, 8),
                AutoSize = true
            };
            cardSafety.Controls.Add(lblSafetyTitle);

            _chkEnableModifications = new CheckBox
            {
                Text = "Enable BIOS Modifications",
                Location = new Point(14, 38),
                AutoSize = true,
                ForeColor = ThemeManager.TextPrimary,
                Checked = _biosManager.ModificationsEnabled
            };
            _chkEnableModifications.CheckedChanged += OnModificationsToggled;
            cardSafety.Controls.Add(_chkEnableModifications);

            _chkAllowDangerous = new CheckBox
            {
                Text = "Allow Dangerous Settings (GPU MUX / Boot)",
                Location = new Point(260, 38),
                AutoSize = true,
                ForeColor = ThemeManager.YellowStatus,
                Checked = _biosManager.DangerousAllowed,
                Enabled = _chkEnableModifications.Checked
            };
            _chkAllowDangerous.CheckedChanged += OnDangerousToggled;
            cardSafety.Controls.Add(_chkAllowDangerous);



            y += 85;

            // TabControl for categories
            _tabControl = new TabControl
            {
                Location = new Point(16, y),
                Size = new Size(770, 560)
            };
            mainPanel.Controls.Add(_tabControl);

            y += 570;

            // Bottom action buttons
            _btnRestoreDefaults = new PredatorButton
            {
                Text = "Restore Factory Defaults",
                Location = new Point(16, y),
                Size = new Size(200, 34)
            };
            _btnRestoreDefaults.Click += OnRestoreDefaultsClicked;
            mainPanel.Controls.Add(_btnRestoreDefaults);

            var btnRefresh = new PredatorButton
            {
                Text = "Re-scan BIOS",
                Location = new Point(230, y),
                Size = new Size(140, 34)
            };
            btnRefresh.Click += (s, e) =>
            {
                _biosManager.ProbeAndLoad();
                _lblModelBadge.Text = _biosManager.BiosBadgeText;
                _lblModeBadge.Text = $"[{_biosManager.GetSupportModeDescription()}]";
                _lblModeBadge.Location = new Point(_lblModelBadge.Right + 12, _lblModelBadge.Top);
                PopulateCategories();
            };
            mainPanel.Controls.Add(btnRefresh);

            var btnClose = new PredatorButton
            {
                Text = "Close",
                Location = new Point(656, y),
                Size = new Size(130, 34)
            };
            btnClose.Click += (s, e) => this.Close();
            mainPanel.Controls.Add(btnClose);
            this.CancelButton = btnClose;
        }

        private void ApplyTheme()
        {
            this.BackColor = ThemeManager.FormBg;
            this.ForeColor = ThemeManager.TextPrimary;
            if (_tabControl != null)
            {
                foreach (TabPage tab in _tabControl.TabPages)
                {
                    tab.BackColor = ThemeManager.FormBg;
                }
            }
        }

        private void PopulateCategories()
        {
            if (_tabControl == null) return;

            int prevIdx = _tabControl.SelectedIndex;

            foreach (TabPage oldTab in _tabControl.TabPages)
            {
                try { oldTab.Dispose(); } catch { }
            }
            _tabControl.TabPages.Clear();

            var categories = new[]
            {
                (BiosCategory.System, "System"),
                (BiosCategory.PowerBattery, "Power & Battery"),
                (BiosCategory.DisplayPerformance, "Display & GPU"),
                (BiosCategory.BootSetup, "Boot & Setup")
            };

            foreach (var (cat, title) in categories)
            {
                var tab = new TabPage(title)
                {
                    BackColor = ThemeManager.FormBg,
                    Padding = new Padding(10)
                };

                var scrollPanel = new DarkScrollPanel
                {
                    Dock = DockStyle.Fill,
                    AutoScroll = true
                };
                tab.Controls.Add(scrollPanel);

                var items = _biosManager.Settings.Where(s => s.Category == cat).ToList();
                int rowY = 10;

                if (items.Count == 0)
                {
                    var lblNone = new Label
                    {
                        Text = "No settings available in this category for this laptop model.",
                        Location = new Point(15, 20),
                        AutoSize = true,
                        ForeColor = ThemeManager.TextSecondary,
                        Font = s_fontCurrentVal
                    };
                    scrollPanel.Controls.Add(lblNone);
                }
                else
                {
                    foreach (var item in items)
                    {
                        var row = CreateSettingRow(item, rowY, 680);
                        scrollPanel.Controls.Add(row);
                        rowY += 80;
                    }
                }

                _tabControl.TabPages.Add(tab);
            }

            if (prevIdx >= 0 && prevIdx < _tabControl.TabCount)
            {
                _tabControl.SelectedIndex = prevIdx;
            }
        }

        private Panel CreateSettingRow(BiosSettingItem item, int y, int width)
        {
            var panel = new Panel
            {
                Location = new Point(10, y),
                Size = new Size(width, 74),
                BackColor = ThemeManager.CardBg
            };
            panel.Paint += (s, e) =>
            {
                if (panel.Width <= 1 || panel.Height <= 1) return;
                using var pen = new Pen(ThemeManager.CardBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, 73);
            };

            // Setting Name
            var lblName = new Label
            {
                Text = item.Name,
                Font = s_fontSettingName,
                ForeColor = ThemeManager.TextPrimary,
                Location = new Point(12, 10),
                AutoSize = true
            };
            panel.Controls.Add(lblName);

            // Risk badge
            var lblRisk = new Label
            {
                Text = item.Risk == BiosSettingRisk.Dangerous ? "[DANGEROUS]" : "[SAFE]",
                Font = s_fontRiskBadge,
                ForeColor = item.Risk == BiosSettingRisk.Dangerous ? ThemeManager.RedStatus : ThemeManager.SuccessGreen,
                Location = new Point(lblName.Right + 10, 12),
                AutoSize = true
            };
            panel.Controls.Add(lblRisk);

            // Options ComboBox
            var cbo = new ComboBox
            {
                Location = new Point(12, 36),
                Size = new Size(320, 28),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var opt in item.Options)
            {
                cbo.Items.Add(opt);
            }

            int selectIdx = item.Options.IndexOf(item.CurrentValue);
            if (selectIdx >= 0) cbo.SelectedIndex = selectIdx;
            else if (cbo.Items.Count > 0) cbo.SelectedIndex = 0;

            panel.Controls.Add(cbo);

            // Current value indicator
            var lblCurrent = new Label
            {
                Text = $"Current: {item.CurrentValue}",
                Font = s_fontCurrentVal,
                ForeColor = ThemeManager.TextSecondary,
                Location = new Point(350, 36),
                AutoSize = true
            };
            panel.Controls.Add(lblCurrent);

            // Apply Button
            bool isDangerous = item.Risk == BiosSettingRisk.Dangerous;
            bool canApply = _biosManager.ModificationsEnabled && (!isDangerous || _biosManager.DangerousAllowed);

            var btnApply = new PredatorButton
            {
                Text = "Apply",
                Location = new Point(605, 28),
                Size = new Size(95, 30),
                Enabled = canApply
            };
            btnApply.Click += (s, e) =>
            {
                if (item == null) return;
                if (!_biosManager.ModificationsEnabled)
                {
                    ShowMessage("BIOS modifications are disabled.", "Safety Gate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (item.Risk == BiosSettingRisk.Dangerous && !_biosManager.DangerousAllowed)
                {
                    ShowMessage("Dangerous settings modification is disabled. Enable 'Allow Dangerous Settings' first.", "Safety Gate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (cbo.SelectedItem == null) return;
                string val = cbo.SelectedItem.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(val)) return;

                item.PendingValue = val;
                bool ok = _biosManager.ApplySetting(item, out string msg);
                if (ok)
                {
                    lblCurrent.Text = $"Current: {item.CurrentValue}";
                    ShowMessage(string.IsNullOrEmpty(msg) ? $"Successfully applied {item.Name} = {item.CurrentValue}." : msg,
                        "BIOS Updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    ShowMessage(msg, "BIOS Apply Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            panel.Controls.Add(btnApply);

            return panel;
        }

        private void OnModificationsToggled(object? sender, EventArgs e)
        {
            if (_isUpdatingSafetyGates) return;
            _isUpdatingSafetyGates = true;
            try
            {
                bool enabled = _chkEnableModifications.Checked;
                _biosManager.ModificationsEnabled = enabled;
                _chkAllowDangerous.Enabled = enabled;
                if (!enabled)
                {
                    _chkAllowDangerous.Checked = false;
                    _biosManager.DangerousAllowed = false;
                }
                PopulateCategories();
            }
            finally
            {
                _isUpdatingSafetyGates = false;
            }
        }

        private void OnDangerousToggled(object? sender, EventArgs e)
        {
            if (_isUpdatingSafetyGates) return;
            _isUpdatingSafetyGates = true;
            try
            {
                if (_chkAllowDangerous.Checked)
                {
                    if (!_chkEnableModifications.Checked || !_biosManager.ModificationsEnabled)
                    {
                        _chkAllowDangerous.Checked = false;
                        _biosManager.DangerousAllowed = false;
                        return;
                    }

                    var dr = ShowMessage(
                        "WARNING: Modifying dangerous firmware options (such as Display MUX or direct UEFI variables) can cause display blanking or system boot failure if configured incorrectly.\n\nAre you sure you want to unlock dangerous settings?",
                        "Hardware Safety Confirmation",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (dr != DialogResult.Yes)
                    {
                        _chkAllowDangerous.Checked = false;
                        _biosManager.DangerousAllowed = false;
                        return;
                    }
                }
                _biosManager.DangerousAllowed = _chkAllowDangerous.Checked;
                PopulateCategories();
            }
            finally
            {
                _isUpdatingSafetyGates = false;
            }
        }

        private void OnRestoreDefaultsClicked(object? sender, EventArgs e)
        {
            var dr = ShowMessage(
                "Are you sure you want to restore all BIOS settings to factory defaults?",
                "Restore Defaults",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (dr == DialogResult.Yes)
            {
                bool ok = _biosManager.RestoreDefaults(userDefaults: false, out string msg);
                if (ok)
                {
                    ShowMessage("Factory defaults restored successfully.", "BIOS Defaults", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    PopulateCategories();
                }
                else
                {
                    ShowMessage(msg, "Restore Defaults Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;
                if (_tabControl != null)
                {
                    foreach (TabPage tab in _tabControl.TabPages)
                    {
                        try { tab.Dispose(); } catch { }
                    }
                    _tabControl.TabPages.Clear();
                }
            }
            base.Dispose(disposing);
        }
    }
}
