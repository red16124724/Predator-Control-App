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
        private readonly BiosManager _biosManager;
        private readonly AppSettings _settings;

        private Label _lblModelBadge = null!;
        private Label _lblModeBadge = null!;
        private CheckBox _chkEnableModifications = null!;
        private CheckBox _chkAllowDangerous = null!;
        private PredatorButton _btnPassword = null!;
        private PredatorButton _btnRestoreDefaults = null!;
        private TabControl _tabControl = null!;

        public BiosSettingsForm(BiosManager? biosManager = null, AppSettings? settings = null)
        {
            _biosManager = biosManager ?? BiosManager.Instance;
            _settings = settings ?? AppSettings.Load();

            InitializeComponent();
            ApplyTheme();
            PopulateCategories();
        }

        private void InitializeComponent()
        {
            this.Text = "Acer BIOS & Firmware Settings";
            this.Size = new Size(680, 720);
            this.MinimumSize = new Size(620, 600);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

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
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                Location = new Point(16, y),
                AutoSize = true
            };
            mainPanel.Controls.Add(lblTitle);
            y += 30;

            // Badges row
            _lblModelBadge = new Label
            {
                Text = $"Model: {_biosManager.ModelName}",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = ThemeManager.Accent,
                Location = new Point(18, y),
                AutoSize = true
            };
            mainPanel.Controls.Add(_lblModelBadge);

            _lblModeBadge = new Label
            {
                Text = $"[{_biosManager.GetSupportModeDescription()}]",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                ForeColor = ThemeManager.TextSecondary,
                Location = new Point(240, y),
                AutoSize = true
            };
            mainPanel.Controls.Add(_lblModeBadge);
            y += 28;

            // Safety Interlock Card
            var cardSafety = new Panel
            {
                Location = new Point(16, y),
                Size = new Size(630, 75),
                BackColor = ThemeManager.CardBg,
                BorderStyle = BorderStyle.None
            };
            cardSafety.Paint += (s, e) =>
            {
                using var pen = new Pen(ThemeManager.CardBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, cardSafety.Width - 1, cardSafety.Height - 1);
            };
            mainPanel.Controls.Add(cardSafety);

            var lblSafetyTitle = new Label
            {
                Text = "Safety Interlocks & Access Control",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
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
                Location = new Point(210, 38),
                AutoSize = true,
                ForeColor = ThemeManager.YellowStatus,
                Checked = _biosManager.DangerousAllowed,
                Enabled = _chkEnableModifications.Checked
            };
            _chkAllowDangerous.CheckedChanged += OnDangerousToggled;
            cardSafety.Controls.Add(_chkAllowDangerous);

            _btnPassword = new PredatorButton
            {
                Text = "Supervisor Pwd",
                Location = new Point(505, 34),
                Size = new Size(115, 28)
            };
            _btnPassword.Click += OnPasswordClicked;
            cardSafety.Controls.Add(_btnPassword);

            y += 85;

            // TabControl for categories
            _tabControl = new TabControl
            {
                Location = new Point(16, y),
                Size = new Size(630, 480)
            };
            mainPanel.Controls.Add(_tabControl);

            y += 490;

            // Bottom action buttons
            _btnRestoreDefaults = new PredatorButton
            {
                Text = "Restore Factory Defaults",
                Location = new Point(16, y),
                Size = new Size(180, 34)
            };
            _btnRestoreDefaults.Click += OnRestoreDefaultsClicked;
            mainPanel.Controls.Add(_btnRestoreDefaults);

            var btnRefresh = new PredatorButton
            {
                Text = "Re-scan BIOS",
                Location = new Point(205, y),
                Size = new Size(130, 34)
            };
            btnRefresh.Click += (s, e) =>
            {
                _biosManager.ProbeAndLoad();
                PopulateCategories();
            };
            mainPanel.Controls.Add(btnRefresh);

            var btnClose = new PredatorButton
            {
                Text = "Close",
                Location = new Point(526, y),
                Size = new Size(120, 34)
            };
            btnClose.Click += (s, e) => this.Close();
            mainPanel.Controls.Add(btnClose);
        }

        private void ApplyTheme()
        {
            this.BackColor = ThemeManager.FormBg;
            this.ForeColor = ThemeManager.TextPrimary;
        }

        private void PopulateCategories()
        {
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
                        ForeColor = ThemeManager.TextSecondary
                    };
                    scrollPanel.Controls.Add(lblNone);
                }
                else
                {
                    foreach (var item in items)
                    {
                        var row = CreateSettingRow(item, rowY, 580);
                        scrollPanel.Controls.Add(row);
                        rowY += 68;
                    }
                }

                _tabControl.TabPages.Add(tab);
            }
        }

        private Panel CreateSettingRow(BiosSettingItem item, int y, int width)
        {
            var panel = new Panel
            {
                Location = new Point(10, y),
                Size = new Size(width, 60),
                BackColor = ThemeManager.CardBg
            };
            panel.Paint += (s, e) =>
            {
                using var pen = new Pen(ThemeManager.CardBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, width - 1, 59);
            };

            // Setting Name
            var lblName = new Label
            {
                Text = item.Name,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = ThemeManager.TextPrimary,
                Location = new Point(12, 10),
                AutoSize = true
            };
            panel.Controls.Add(lblName);

            // Risk badge
            var lblRisk = new Label
            {
                Text = item.Risk == BiosSettingRisk.Dangerous ? "[DANGEROUS]" : "[SAFE]",
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = item.Risk == BiosSettingRisk.Dangerous ? ThemeManager.RedStatus : ThemeManager.SuccessGreen,
                Location = new Point(lblName.Right + 10, 12),
                AutoSize = true
            };
            panel.Controls.Add(lblRisk);

            // Options ComboBox
            var cbo = new ComboBox
            {
                Location = new Point(12, 30),
                Size = new Size(240, 26),
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
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = ThemeManager.TextSecondary,
                Location = new Point(270, 33),
                AutoSize = true
            };
            panel.Controls.Add(lblCurrent);

            // Apply Button
            var btnApply = new PredatorButton
            {
                Text = "Apply",
                Location = new Point(485, 26),
                Size = new Size(80, 28),
                Enabled = _biosManager.ModificationsEnabled
            };
            btnApply.Click += (s, e) =>
            {
                if (cbo.SelectedItem == null) return;
                item.PendingValue = cbo.SelectedItem.ToString() ?? "";
                bool ok = _biosManager.ApplySetting(item, out string msg);
                if (ok)
                {
                    lblCurrent.Text = $"Current: {item.CurrentValue}";
                    MessageBox.Show(this, string.IsNullOrEmpty(msg) ? $"Successfully applied {item.Name} = {item.CurrentValue}." : msg,
                        "BIOS Updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, msg, "BIOS Apply Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            panel.Controls.Add(btnApply);

            return panel;
        }

        private void OnModificationsToggled(object? sender, EventArgs e)
        {
            _biosManager.ModificationsEnabled = _chkEnableModifications.Checked;
            _chkAllowDangerous.Enabled = _chkEnableModifications.Checked;
            if (!_chkEnableModifications.Checked)
            {
                _chkAllowDangerous.Checked = false;
                _biosManager.DangerousAllowed = false;
            }
            PopulateCategories();
        }

        private void OnDangerousToggled(object? sender, EventArgs e)
        {
            if (_chkAllowDangerous.Checked)
            {
                var dr = MessageBox.Show(this,
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

        private void OnPasswordClicked(object? sender, EventArgs e)
        {
            using var form = new Form
            {
                Text = "BIOS Supervisor Password",
                Size = new Size(360, 160),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = ThemeManager.FormBg,
                ForeColor = ThemeManager.TextPrimary
            };

            var lbl = new Label
            {
                Text = "Enter BIOS Supervisor Password (if set):",
                Location = new Point(15, 15),
                AutoSize = true
            };
            form.Controls.Add(lbl);

            var txt = new TextBox
            {
                Location = new Point(15, 40),
                Size = new Size(310, 26),
                PasswordChar = '*'
            };
            form.Controls.Add(txt);

            var btnOk = new PredatorButton
            {
                Text = "Save",
                Location = new Point(150, 75),
                Size = new Size(85, 30)
            };
            btnOk.Click += (s, ev) =>
            {
                form.DialogResult = DialogResult.OK;
                form.Close();
            };
            form.Controls.Add(btnOk);

            var btnClear = new PredatorButton
            {
                Text = "Clear",
                Location = new Point(240, 75),
                Size = new Size(85, 30)
            };
            btnClear.Click += (s, ev) =>
            {
                _biosManager.SetSupervisorPassword(null);
                form.Close();
            };
            form.Controls.Add(btnClear);

            if (form.ShowDialog(this) == DialogResult.OK)
            {
                _biosManager.SetSupervisorPassword(txt.Text);
            }
        }

        private void OnRestoreDefaultsClicked(object? sender, EventArgs e)
        {
            var dr = MessageBox.Show(this,
                "Are you sure you want to restore all BIOS settings to factory defaults?",
                "Restore Defaults",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (dr == DialogResult.Yes)
            {
                bool ok = _biosManager.RestoreDefaults(userDefaults: false, out string msg);
                if (ok)
                {
                    MessageBox.Show(this, "Factory defaults restored successfully.", "BIOS Defaults", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    PopulateCategories();
                }
                else
                {
                    MessageBox.Show(this, msg, "Restore Defaults Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }
}
