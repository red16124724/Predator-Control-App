using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public sealed class HardwareCapabilitiesForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        #region Theme Colors & Fonts

        private static readonly Color FormBg = Color.FromArgb(22, 22, 26);
        private static readonly Color TitleBarBg = Color.FromArgb(18, 18, 21);
        private static readonly Color CardBg = Color.FromArgb(28, 28, 33);
        private static readonly Color CardBorder = Color.FromArgb(44, 44, 52);
        private static readonly Color SeparatorColor = Color.FromArgb(40, 40, 46);
        private static readonly Color HeaderColor = Color.FromArgb(130, 130, 145);
        private static readonly Color SubHeaderColor = Color.FromArgb(105, 105, 118);
        private static readonly Color AccentColor = Color.FromArgb(0, 200, 160);
        private static readonly Color TextColor = Color.FromArgb(220, 220, 226);
        private static readonly Color SuccessGreen = Color.FromArgb(0, 230, 140);
        private static readonly Color WarningAmber = Color.FromArgb(245, 158, 11);

        private static readonly Font FontTitle = new("Segoe UI", 10f, FontStyle.Bold);
        private static readonly Font FontSection = new("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Font FontBody = new("Segoe UI", 9f, FontStyle.Regular);
        private static readonly Font FontBodyBold = new("Segoe UI", 9f, FontStyle.Bold);
        private static readonly Font FontMono = new("Consolas", 8.5f, FontStyle.Regular);
        private static readonly Font FontClose = new("Arial", 12f);
        private static readonly Font FontSubHeader = new("Segoe UI", 7.8f, FontStyle.Regular);

        #endregion

        private readonly WmiController? _wmi;
        private readonly DeviceCapabilities _capabilities;
        private readonly Action<DeviceCapabilities>? _onOverridesSaved;
        private float _dpiScale = 1.0f;
        private int _formW;
        private Bitmap? _appIconBitmap;

        private DarkScrollPanel _contentPanel = null!;
        private PredatorDropDown _cboCoolBoost = null!;
        private PredatorDropDown _cboOperatingModes = null!;
        private PredatorDropDown _cboGpuMode = null!;
        private PredatorDropDown _cboThirdFan = null!;
        private PredatorDropDown _cboFanTable = null!;
        private PredatorDropDown _cboUsbCharging = null!;
        private PredatorDropDown _cboBatteryCalibration = null!;

        public HardwareCapabilitiesForm(DeviceCapabilities capabilities, Action? onOverridesSaved = null)
            : this(null, capabilities, onOverridesSaved != null ? _ => onOverridesSaved() : null)
        {
        }

        public HardwareCapabilitiesForm(DeviceCapabilities capabilities, Action<DeviceCapabilities>? onOverridesSaved)
            : this(null, capabilities, onOverridesSaved)
        {
        }

        public HardwareCapabilitiesForm(WmiController? wmi, DeviceCapabilities capabilities, Action? onOverridesSaved = null)
            : this(wmi, capabilities, onOverridesSaved != null ? _ => onOverridesSaved() : null)
        {
        }

        public HardwareCapabilitiesForm(WmiController? wmi, DeviceCapabilities capabilities, Action<DeviceCapabilities>? onOverridesSaved)
        {
            _wmi = wmi;
            _capabilities = capabilities;
            _onOverridesSaved = onOverridesSaved;

            using (var g = CreateGraphics()) _dpiScale = g.DpiX / 96f;

            InitializeLayout();
        }

        private int S(int pixels) => (int)Math.Round(pixels * _dpiScale);

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _dpiScale = e.DeviceDpiNew / 96f;
        }

        private void InitializeLayout()
        {
            this.Controls.Clear();
            this.BackColor = FormBg;
            this.ForeColor = TextColor;
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ShowInTaskbar = false;

            _formW = S(580);
            int workH = Screen.PrimaryScreen?.WorkingArea.Height ?? S(900);
            int formH = Math.Min(S(760), workH - S(60));
            this.ClientSize = new Size(_formW, formH);

            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int pad = S(20);
            int contentW = _formW - pad * 2;
            int y = 0;

            // Custom Title Bar
            var pnlTitle = new Panel
            {
                Name = "pnlTitle",
                Height = S(42),
                Width = _formW,
                BackColor = TitleBarBg,
                Location = new Point(0, 0)
            };
            pnlTitle.MouseDown += TitleBar_MouseDown;
            this.Controls.Add(pnlTitle);

            var picIcon = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(S(18), S(18)),
                Location = new Point(pad, S(12)),
                BackColor = Color.Transparent
            };
            try
            {
                using var extIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extIcon != null)
                {
                    _appIconBitmap = extIcon.ToBitmap();
                    picIcon.Image = _appIconBitmap;
                }
            }
            catch { }
            picIcon.MouseDown += TitleBar_MouseDown;
            pnlTitle.Controls.Add(picIcon);

            var lblTitle = new Label
            {
                Text = "Predator Hardware & SMBIOS Capabilities",
                ForeColor = Color.White,
                Font = FontTitle,
                AutoSize = true,
                Location = new Point(pad + S(26), S(11)),
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
            lblTitle.MouseDown += TitleBar_MouseDown;
            pnlTitle.Controls.Add(lblTitle);

            var lblClose = new Label
            {
                Text = "●",
                ForeColor = Color.FromArgb(255, 95, 86),
                Font = FontClose,
                AutoSize = true,
                Location = new Point(_formW - pad - S(6), S(10)),
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
            lblClose.Click += (s, e) => this.Close();
            pnlTitle.Controls.Add(lblClose);

            y = pnlTitle.Bottom;

            _contentPanel = new DarkScrollPanel
            {
                Location = new Point(0, y),
                Size = new Size(_formW + DarkScrollPanel.NativeBarWidth, this.ClientSize.Height - y),
                BackColor = FormBg
            };
            _contentPanel.SetDpiScale(_dpiScale);
            this.Controls.Add(_contentPanel);

            int curY = S(16);

            // ==========================================
            // Section 1: Detected Hardware Identity & Bus
            // ==========================================
            MakeSectionHeader("DETECTED HARDWARE & SMBIOS IDENTITY", pad, curY);
            curY += S(22);

            string model = HardwareIdentity.ReadModel();
            string bios = HardwareIdentity.ReadBiosVersion();
            string sn = HardwareIdentity.ReadSerialNumber();
            string snid = HardwareIdentity.ComputeSnid(sn) ?? "N/A";
            var smbios = AcerSmbios.Read();
            bool hasEcHid = (_wmi != null && _wmi.EcHid != null && _wmi.EcHid.IsOpen) || _capabilities.HasEcHid;
            string protocolName = hasEcHid ? (_wmi?.EcHid?.Version != null ? $"Direct EC HID (v{_wmi.EcHid.Version.Value.Major}.{_wmi.EcHid.Version.Value.Minor})" : "Direct EC HID Protocol") : "Acer ACPI WMI";
            string gamingVer = smbios.GamingVersion.HasValue ? smbios.GamingVersion.Value.ToString("0.00") : "Absent";

            var cardInfo = CreateCard(pad, curY, contentW, S(168));
            int cy = S(10);
            int col1 = S(14), col2 = contentW / 2 + S(6);

            AddCardKeyValue(cardInfo, "Hardware Model:", model, col1, cy, S(105));
            AddCardKeyValue(cardInfo, "BIOS Version:", bios, col2, cy, S(95));
            cy += S(24);

            AddCardKeyValue(cardInfo, "Serial Number:", sn, col1, cy, S(105));
            AddCardKeyValue(cardInfo, "Computed SNID:", snid, col2, cy, S(95));
            cy += S(24);

            AddCardKeyValue(cardInfo, "Active Protocol:", protocolName, col1, cy, S(105), hasEcHid ? SuccessGreen : AccentColor);
            AddCardKeyValue(cardInfo, "SMBIOS Gaming Ver:", gamingVer, col2, cy, S(110));
            cy += S(24);

            string sensorsStr = _capabilities.Sensors.Count > 0 ? string.Join(", ", _capabilities.Sensors.Select(s => s.ToString())) : "None";
            AddCardKeyValue(cardInfo, "Probed Sensors:", sensorsStr, col1, cy, S(105));
            cy += S(24);

            string fansStr = string.Join(", ", _capabilities.Fans.Select(f => f.Name));
            bool muxSupported = _wmi != null ? _wmi.IsGpuModeSwitchSupported() : _capabilities.GpuModeSwitch;
            AddCardKeyValue(cardInfo, "Probed Fans:", fansStr, col1, cy, S(105));
            AddCardKeyValue(cardInfo, "Hardware MUX Switch:", muxSupported ? "Detected / Supported" : "Not Detected", col2, cy, S(130), muxSupported ? SuccessGreen : SubHeaderColor);
            cy += S(24);

            AddCardKeyValue(cardInfo, "Physical Mode Key:", _capabilities.ModeKey ? "Present" : "Not Present", col1, cy, S(115));
            AddCardKeyValue(cardInfo, "Battery Calibration:", _capabilities.BatteryCalibration ? "Hardware Supported" : "Not Available", col2, cy, S(125));

            _contentPanel.Controls.Add(cardInfo);
            curY += cardInfo.Height + S(18);

            // ==========================================
            // Section 2: Capability Overrides (Persistence)
            // ==========================================
            MakeSectionHeader("FIRMWARE CAPABILITY OVERRIDES (REGISTRY PERSISTENCE)", pad, curY);
            curY += S(20);

            var lblDesc = new Label
            {
                Text = "Force hardware features on or off. 'Auto-Detect' defers to low-level SMBIOS/EC probing.",
                ForeColor = SubHeaderColor,
                Font = FontBody,
                Location = new Point(pad, curY),
                AutoSize = true,
                UseMnemonic = false
            };
            _contentPanel.Controls.Add(lblDesc);
            curY += S(24);

            var curOverrides = CapabilityOverrides.LoadFromRegistry();

            int cardOverridesH = S(346);
            var cardOverrides = CreateCard(pad, curY, contentW, cardOverridesH);
            int oy = S(12);

            _cboCoolBoost = AddOverrideRow(cardOverrides, "Acer CoolBoost", "Unlocks maximum hardware fan boost curves", curOverrides.CoolBoost, _capabilities.CoolBoost, oy);
            oy += S(44);

            _cboOperatingModes = AddOverrideRow(cardOverrides, "Operating Power Modes", "Quiet, Balanced, Performance, Turbo, Eco", curOverrides.OperatingModes, _capabilities.OperatingModes.Count > 0, oy);
            oy += S(44);

            _cboGpuMode = AddOverrideRow(cardOverrides, "MUX Switch / GPU Working Mode", "3-Mode MUX: Auto (Optimus/DDS), iGPU Only, and dGPU Only", curOverrides.GpuModeSwitch, _capabilities.GpuModeSwitch, oy);
            oy += S(44);

            _cboThirdFan = AddOverrideRow(cardOverrides, "3rd System / Chassis Fan", "Triple-fan cooling speed slider and RPM readout", curOverrides.ThirdFan, _capabilities.HasThirdFan, oy);
            oy += S(44);


            _cboFanTable = AddOverrideRow(cardOverrides, "Factory EC Fan Tables", "Standard, Faster, Fastest hardware lookup tables", curOverrides.FanTable, _capabilities.FanTable, oy);
            oy += S(44);

            _cboUsbCharging = AddOverrideRow(cardOverrides, "Power-Off USB Charging", "Allow charging external devices while laptop is off", curOverrides.UsbCharging, _capabilities.UsbCharging, oy);
            oy += S(44);

            _cboBatteryCalibration = AddOverrideRow(cardOverrides, "Hardware Battery Calibration", "Automated fuel-gauge recalibration cycle", curOverrides.BatteryCalibration, _capabilities.BatteryCalibration, oy);

            _contentPanel.Controls.Add(cardOverrides);
            curY += cardOverrides.Height + S(16);

            // ==========================================
            // Section 3: Action Buttons
            // ==========================================
            int btnH = S(34);
            int btnGap = S(8);
            int actionBtnW = (contentW - btnGap * 2) / 3;

            var btnSave = new PredatorButton
            {
                Text = "💾  Save & Apply",
                Location = new Point(pad, curY),
                Size = new Size(actionBtnW, btnH)
            };
            btnSave.Click += BtnSave_Click;
            _contentPanel.Controls.Add(btnSave);

            var btnReset = new PredatorButton
            {
                Text = "↺  Reset Auto-Detect",
                Location = new Point(pad + actionBtnW + btnGap, curY),
                Size = new Size(actionBtnW, btnH)
            };
            btnReset.Click += BtnReset_Click;
            _contentPanel.Controls.Add(btnReset);

            var btnCopy = new PredatorButton
            {
                Text = "📋  Copy HW Report",
                Location = new Point(pad + (actionBtnW + btnGap) * 2, curY),
                Size = new Size(actionBtnW, btnH)
            };
            btnCopy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(_capabilities.Diagnostics);
                    MessageBox.Show(this, "Hardware & SMBIOS diagnostics report copied to clipboard!", "Predator Hardware", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Failed to copy: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            _contentPanel.Controls.Add(btnCopy);

            curY += btnH + S(20);

            // ==========================================
            // Section 4: Raw SMBIOS & Firmware Diagnostics Log
            // ==========================================
            MakeSectionHeader("RAW FIRMWARE PROBE & SMBIOS DUMP", pad, curY);
            curY += S(22);

            var txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(16, 16, 19),
                ForeColor = Color.FromArgb(180, 220, 200),
                Font = FontMono,
                Location = new Point(pad, curY),
                Size = new Size(contentW, S(160)),
                BorderStyle = BorderStyle.FixedSingle,
                Text = _capabilities.Diagnostics
            };
            _contentPanel.Controls.Add(txtLog);
            curY += txtLog.Height + S(24);

            _contentPanel.AutoScrollMinSize = new Size(0, curY);
        }

        private PredatorDropDown AddOverrideRow(Panel parent, string title, string subtitle, bool? currentOverride, bool probedValue, int y)
        {
            int padX = S(14);
            int dropW = S(170);
            int dropH = S(28);

            var lblTitle = new Label
            {
                Text = title,
                Font = FontBodyBold,
                ForeColor = Color.White,
                Location = new Point(padX, y),
                AutoSize = true,
                UseMnemonic = false
            };
            parent.Controls.Add(lblTitle);

            string probedStatus = probedValue ? "probed: YES" : "probed: NO";
            var lblSub = new Label
            {
                Text = $"{subtitle} ({probedStatus})",
                Font = FontSubHeader,
                ForeColor = SubHeaderColor,
                Location = new Point(padX, y + S(18)),
                AutoSize = true,
                UseMnemonic = false
            };
            parent.Controls.Add(lblSub);

            var drop = new PredatorDropDown
            {
                Location = new Point(parent.Width - dropW - padX, y + S(2)),
                Size = new Size(dropW, dropH)
            };
            drop.Items.AddRange(new[]
            {
                "Auto-Detect (Probe)",
                "Force Enabled",
                "Force Disabled"
            });

            drop.SelectedIndex = currentOverride switch
            {
                true => 1,
                false => 2,
                _ => 0
            };

            parent.Controls.Add(drop);
            return drop;
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            try
            {
                bool? GetVal(PredatorDropDown cbo) => cbo.SelectedIndex switch
                {
                    1 => true,
                    2 => false,
                    _ => null
                };

                var overrides = new CapabilityOverrides
                {
                    CoolBoost = GetVal(_cboCoolBoost),
                    OperatingModes = GetVal(_cboOperatingModes),
                    GpuModeSwitch = GetVal(_cboGpuMode),
                    ThirdFan = GetVal(_cboThirdFan),
                    FanTable = GetVal(_cboFanTable),
                    UsbCharging = GetVal(_cboUsbCharging),
                    BatteryCalibration = GetVal(_cboBatteryCalibration)
                };

                overrides.SaveToRegistry();
                var updated = overrides.Apply(_capabilities);
                _onOverridesSaved?.Invoke(updated);

                MessageBox.Show(this, "Hardware capability overrides saved to registry! Active features updated.", "Capabilities Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save overrides: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnReset_Click(object? sender, EventArgs e)
        {
            try
            {
                _cboCoolBoost.SelectedIndex = 0;
                _cboOperatingModes.SelectedIndex = 0;
                _cboGpuMode.SelectedIndex = 0;
                _cboThirdFan.SelectedIndex = 0;
                _cboFanTable.SelectedIndex = 0;
                _cboUsbCharging.SelectedIndex = 0;
                _cboBatteryCalibration.SelectedIndex = 0;

                var empty = new CapabilityOverrides();
                empty.SaveToRegistry();
                var updated = empty.Apply(_capabilities);
                _onOverridesSaved?.Invoke(updated);

                MessageBox.Show(this, "All overrides reset to firmware auto-detection.", "Capabilities Reset", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to reset: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MakeSectionHeader(string label, int x, int y)
        {
            var lbl = new Label
            {
                Text = label,
                Location = new Point(x, y),
                AutoSize = true,
                Font = FontSection,
                ForeColor = HeaderColor,
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
            _contentPanel.Controls.Add(lbl);
        }

        private Panel CreateCard(int x, int y, int width, int height)
        {
            var pnl = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = CardBg
            };
            pnl.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, width - 1, height - 1);
            };
            return pnl;
        }

        private void AddCardKeyValue(Panel card, string key, string value, int x, int y, int keyWidth, Color? valColor = null)
        {
            var lblKey = new Label
            {
                Text = key,
                Font = FontBody,
                ForeColor = SubHeaderColor,
                Location = new Point(x, y),
                AutoSize = true,
                UseMnemonic = false
            };
            card.Controls.Add(lblKey);

            var lblVal = new Label
            {
                Text = value,
                Font = FontBodyBold,
                ForeColor = valColor ?? Color.White,
                Location = new Point(x + keyWidth, y),
                AutoSize = true,
                UseMnemonic = false
            };
            card.Controls.Add(lblVal);
        }

        private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }
    
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { Icon?.Dispose(); } catch { }
                try { _appIconBitmap?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
