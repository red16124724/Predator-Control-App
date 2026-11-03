using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public sealed class CustomShortcutsForm : Form
    {
        private readonly AppSettings _settings;
        private readonly KeyboardHook? _hook;
        private readonly Action<byte>? _onModeApply;
        private readonly List<CustomModeShortcut> _editingShortcuts = new();

        private Panel _pnlHeader = null!;
        private Label _lblTitle = null!;
        private Label _lblSubtitle = null!;
        private Panel _pnlContainer = null!;
        private Label _lblValidationWarning = null!;
        private PredatorButton _btnSave = null!;
        private PredatorButton _btnCancel = null!;
        private PredatorButton _btnReset = null!;

        private readonly List<ShortcutRowControls> _rows = new();

        private float _dpiScale = 1.0f;
        private int S(int px) => px == 0 ? 0 : Math.Max(1, (int)Math.Round(px * _dpiScale));

        private Font? _fontTitle;
        private Font? _fontSubtitle;
        private Font? _fontBody;
        private Font? _fontBold;
        private Font? _fontDot;
        private Font? _fontWarning;

        private readonly Action _themeHandler;

        private sealed class ShortcutRowControls
        {
            public CustomModeShortcut Shortcut { get; }
            public Label LblMode { get; }
            public Label LblBadge { get; }
            public TextBox TxtShortcut { get; }
            public PredatorButton BtnRecord { get; }
            public PredatorButton BtnClear { get; }
            public bool IsRecording { get; set; }

            public ShortcutRowControls(
                CustomModeShortcut shortcut,
                Label lblMode,
                Label lblBadge,
                TextBox txtShortcut,
                PredatorButton btnRecord,
                PredatorButton btnClear)
            {
                Shortcut = shortcut;
                LblMode = lblMode;
                LblBadge = lblBadge;
                TxtShortcut = txtShortcut;
                BtnRecord = btnRecord;
                BtnClear = btnClear;
            }
        }

        public CustomShortcutsForm(AppSettings settings, KeyboardHook? hook = null, Action<byte>? onModeApply = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _hook = hook;
            _onModeApply = onModeApply;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _dpiScale = DeviceDpi > 0 ? DeviceDpi / 96f : 1.0f;
            AutoScaleMode = AutoScaleMode.None;

            // Clone shortcuts for editing
            var list = _settings.CustomModeShortcuts ?? ModeShortcutValidator.CreateDefaultShortcuts();
            foreach (var sc in list)
            {
                if (sc != null) _editingShortcuts.Add(sc.Clone());
            }

            // Ensure all 5 modes exist
            EnsureAllModesPresent();

            InitFonts();
            InitializeUi();

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
        }

        private void InitFonts()
        {
            _fontTitle?.Dispose();
            _fontSubtitle?.Dispose();
            _fontBody?.Dispose();
            _fontBold?.Dispose();
            _fontDot?.Dispose();
            _fontWarning?.Dispose();

            _fontTitle = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            _fontSubtitle = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            _fontBody = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            _fontBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _fontDot = new Font("Segoe UI", 12f, FontStyle.Bold);
            _fontWarning = new Font("Segoe UI", 8.75f, FontStyle.Bold);

            Font = _fontBody;
        }

        private void EnsureAllModesPresent()
        {
            var required = new (string Name, byte Code, Keys DefaultKey)[]
            {
                ("Eco", 0x06, Keys.D1),
                ("Quiet", 0x00, Keys.D2),
                ("Balanced", 0x01, Keys.D3),
                ("Performance", 0x04, Keys.D4),
                ("Turbo", 0x05, Keys.D5)
            };

            foreach (var req in required)
            {
                if (!_editingShortcuts.Any(s => s.ModeCode == req.Code))
                {
                    _editingShortcuts.Add(new CustomModeShortcut(req.Name, req.Code, req.DefaultKey, ctrl: true, alt: true));
                }
            }
        }

        private void InitializeUi()
        {
            Text = "Custom Mode Shortcuts";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            // Header
            _pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                BackColor = ThemeManager.CardBg
            };
            _lblTitle = new Label
            {
                Text = "⌨  CUSTOM MODE SHORTCUTS",
                Font = _fontTitle,
                ForeColor = ThemeManager.HeaderText,
                AutoSize = true,
                BackColor = Color.Transparent
            };
            _lblSubtitle = new Label
            {
                Text = "Configure custom keyboard shortcuts for changing power modes. Windows shortcuts and F1–F24 are banned.",
                Font = _fontSubtitle,
                ForeColor = ThemeManager.SubHeaderText,
                AutoSize = false,
                BackColor = Color.Transparent
            };
            _pnlHeader.Controls.Add(_lblTitle);
            _pnlHeader.Controls.Add(_lblSubtitle);
            Controls.Add(_pnlHeader);

            // Container for rows
            _pnlContainer = new Panel
            {
                BackColor = ThemeManager.CardBg
            };
            _pnlContainer.Paint += (s, e) =>
            {
                using var p = new Pen(ThemeManager.CardBorder, 1f);
                if (_pnlContainer.Width > 1 && _pnlContainer.Height > 1)
                    e.Graphics.DrawRectangle(p, 0, 0, _pnlContainer.Width - 1, _pnlContainer.Height - 1);
            };
            Controls.Add(_pnlContainer);

            // Build Rows
            foreach (var shortcut in _editingShortcuts)
            {
                AddShortcutRow(shortcut);
            }

            // Inline warning label
            _lblValidationWarning = new Label
            {
                Font = _fontWarning,
                ForeColor = Color.FromArgb(255, 80, 80),
                Text = string.Empty,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
                AutoEllipsis = true
            };
            Controls.Add(_lblValidationWarning);

            // Bottom action buttons
            _btnReset = new PredatorButton { Text = "↺  Reset Defaults" };
            _btnReset.Click += (s, e) => ResetToDefaults();
            Controls.Add(_btnReset);

            _btnSave = new PredatorButton { Text = "💾  Save & Apply" };
            _btnSave.Click += (s, e) => SaveAndClose();
            Controls.Add(_btnSave);

            _btnCancel = new PredatorButton { Text = "Cancel" };
            _btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            Controls.Add(_btnCancel);

            ApplyThemeColors();
            UpdateLayout();
        }

        private void AddShortcutRow(CustomModeShortcut shortcut)
        {
            Color modeColor = shortcut.ModeCode switch
            {
                0x06 => Color.FromArgb(38, 166, 154),  // Eco (Teal)
                0x00 => Color.FromArgb(41, 128, 185),  // Quiet (Blue)
                0x01 => Color.FromArgb(39, 174, 96),   // Balanced (Green)
                0x04 => Color.FromArgb(230, 126, 34),  // Performance (Orange)
                0x05 => Color.FromArgb(231, 76, 60),   // Turbo (Red)
                _ => Color.FromArgb(140, 140, 140)
            };

            var lblBadge = new Label
            {
                Text = "●",
                ForeColor = modeColor,
                Font = _fontDot,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _pnlContainer.Controls.Add(lblBadge);

            var lblMode = new Label
            {
                Text = $"{shortcut.ModeName} Mode",
                Font = _fontBold,
                ForeColor = ThemeManager.TextPrimary,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _pnlContainer.Controls.Add(lblMode);

            var txtShortcut = new TextBox
            {
                Text = shortcut.ToString(),
                ReadOnly = true,
                BackColor = ThemeManager.WindowBg,
                ForeColor = shortcut.Enabled ? ThemeManager.TextPrimary : ThemeManager.SubHeaderText,
                TextAlign = HorizontalAlignment.Center,
                Font = _fontBold,
                BorderStyle = BorderStyle.FixedSingle
            };
            _pnlContainer.Controls.Add(txtShortcut);

            var btnRecord = new PredatorButton { Text = "Record" };
            var btnClear = new PredatorButton { Text = "Clear" };

            var row = new ShortcutRowControls(shortcut, lblMode, lblBadge, txtShortcut, btnRecord, btnClear);
            _rows.Add(row);

            btnRecord.Click += (s, e) => ToggleRecording(row);
            btnClear.Click += (s, e) => ClearShortcut(row);

            txtShortcut.PreviewKeyDown += (s, e) =>
            {
                if (row.IsRecording)
                {
                    e.IsInputKey = true;
                }
            };

            txtShortcut.KeyDown += (s, e) =>
            {
                if (row.IsRecording)
                {
                    HandleKeyRecord(row, e);
                }
            };

            _pnlContainer.Controls.Add(btnRecord);
            _pnlContainer.Controls.Add(btnClear);
        }

        private void UpdateLayout()
        {
            int baseW = S(600);
            int pad = S(20);
            int headerH = S(72);

            _pnlHeader.Height = headerH;
            _lblTitle.Location = new Point(S(18), S(12));
            _lblSubtitle.Location = new Point(S(19), S(38));
            _lblSubtitle.Size = new Size(baseW - S(38), S(28));

            int containerY = headerH + S(12);
            int rowH = S(60);
            int containerH = _rows.Count * rowH + S(16);
            int containerW = baseW - pad * 2;

            _pnlContainer.Location = new Point(pad, containerY);
            _pnlContainer.Size = new Size(containerW, containerH);

            int y = S(10);
            int badgeW = S(18), badgeH = S(24);
            int modeW = S(135), modeH = S(24);
            int txtW = S(180), txtH = S(26);
            int recW = S(84), clrW = S(72), btnH = S(28);

            foreach (var row in _rows)
            {
                row.LblBadge.Location = new Point(S(14), y + S(4));
                row.LblBadge.Size = new Size(badgeW, badgeH);

                row.LblMode.Location = new Point(S(36), y + S(6));
                row.LblMode.Size = new Size(modeW, modeH);

                row.TxtShortcut.Location = new Point(S(175), y + S(5));
                row.TxtShortcut.Size = new Size(txtW, txtH);

                row.BtnRecord.Location = new Point(containerW - recW - clrW - S(16), y + S(4));
                row.BtnRecord.Size = new Size(recW, btnH);

                row.BtnClear.Location = new Point(containerW - clrW - S(10), y + S(4));
                row.BtnClear.Size = new Size(clrW, btnH);

                y += rowH;
            }

            int warningY = containerY + containerH + S(8);
            int warningH = S(34);
            _lblValidationWarning.Location = new Point(pad, warningY);
            _lblValidationWarning.Size = new Size(containerW, warningH);

            int btnRowY = warningY + warningH + S(10);
            int actionBtnH = S(36);
            int resetW = S(140);
            int saveW = S(135);
            int cancelW = S(110);

            _btnReset.Location = new Point(pad, btnRowY);
            _btnReset.Size = new Size(resetW, actionBtnH);

            _btnSave.Location = new Point(baseW - pad - saveW - cancelW - S(10), btnRowY);
            _btnSave.Size = new Size(saveW, actionBtnH);

            _btnCancel.Location = new Point(baseW - pad - cancelW, btnRowY);
            _btnCancel.Size = new Size(cancelW, actionBtnH);

            int totalH = btnRowY + actionBtnH + pad;
            ClientSize = new Size(baseW, totalH);
        }

        private void ApplyThemeColors()
        {
            if (IsDisposed || Disposing) return;
            BackColor = ThemeManager.WindowBg;
            ForeColor = ThemeManager.TextPrimary;
            _pnlHeader.BackColor = ThemeManager.CardBg;
            _lblTitle.ForeColor = ThemeManager.HeaderText;
            _lblSubtitle.ForeColor = ThemeManager.SubHeaderText;
            _pnlContainer.BackColor = ThemeManager.CardBg;

            foreach (var row in _rows)
            {
                row.LblMode.ForeColor = ThemeManager.TextPrimary;
                row.TxtShortcut.BackColor = ThemeManager.WindowBg;
                row.TxtShortcut.ForeColor = row.Shortcut.Enabled ? ThemeManager.TextPrimary : ThemeManager.SubHeaderText;
            }

            _pnlContainer.Invalidate();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _dpiScale = e.DeviceDpiNew / 96f;
            UpdateLayout();
        }

        private void ApplyFonts()
        {
            _lblTitle.Font = _fontTitle;
            _lblSubtitle.Font = _fontSubtitle;
            _lblValidationWarning.Font = _fontWarning;

            foreach (var row in _rows)
            {
                row.LblBadge.Font = _fontDot;
                row.LblMode.Font = _fontBold;
                row.TxtShortcut.Font = _fontBold;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                var activeRec = _rows.FirstOrDefault(r => r.IsRecording);
                if (activeRec != null)
                {
                    StopRecording(activeRec);
                    _lblValidationWarning.Text = "Recording cancelled.";
                    _lblValidationWarning.ForeColor = ThemeManager.SubHeaderText;
                    e.Handled = true;
                    return;
                }
                DialogResult = DialogResult.Cancel;
                Close();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private void ToggleRecording(ShortcutRowControls row)
        {
            if (row.IsRecording)
            {
                StopRecording(row);
                return;
            }

            // Stop other recording rows
            foreach (var r in _rows)
            {
                if (r.IsRecording) StopRecording(r);
            }

            row.IsRecording = true;
            row.BtnRecord.Text = "Press keys...";
            row.TxtShortcut.Text = "Listening...";
            row.TxtShortcut.Focus();
            _lblValidationWarning.Text = "Press your shortcut combo (e.g. Ctrl+Alt+1). F1–F24 and Windows shortcuts are blocked.";
            _lblValidationWarning.ForeColor = Color.FromArgb(0, 180, 255);
        }

        private void StopRecording(ShortcutRowControls row)
        {
            row.IsRecording = false;
            row.BtnRecord.Text = "Record";
            row.TxtShortcut.Text = row.Shortcut.ToString();
        }

        private void ClearShortcut(ShortcutRowControls row)
        {
            StopRecording(row);
            row.Shortcut.Enabled = false;
            row.Shortcut.Key = Keys.None;
            row.Shortcut.Ctrl = false;
            row.Shortcut.Alt = false;
            row.Shortcut.Shift = false;
            row.Shortcut.Win = false;
            row.TxtShortcut.Text = "Disabled";
            row.TxtShortcut.ForeColor = ThemeManager.SubHeaderText;
            _lblValidationWarning.Text = string.Empty;
        }

        private void HandleKeyRecord(ShortcutRowControls row, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;

            Keys key = e.KeyCode;

            // Ignore pure modifier presses
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
            {
                return;
            }

            bool ctrl = e.Control;
            bool alt = e.Alt;
            bool shift = e.Shift;
            bool win = (KeyboardHook.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (KeyboardHook.GetAsyncKeyState(0x5C) & 0x8000) != 0;

            if (!ModeShortcutValidator.IsValid(key, ctrl, alt, shift, win, out string failureReason))
            {
                _lblValidationWarning.Text = $"⚠ {failureReason}";
                _lblValidationWarning.ForeColor = Color.FromArgb(255, 80, 80);
                return;
            }

            // Check for duplicate shortcuts within the form
            foreach (var other in _rows)
            {
                if (other != row && other.Shortcut.Enabled && other.Shortcut.Key == key &&
                    other.Shortcut.Ctrl == ctrl && other.Shortcut.Alt == alt && other.Shortcut.Shift == shift &&
                    other.Shortcut.Win == win)
                {
                    _lblValidationWarning.Text = $"⚠ Shortcut already assigned to {other.Shortcut.ModeName} Mode.";
                    _lblValidationWarning.ForeColor = Color.FromArgb(255, 80, 80);
                    return;
                }
            }

            // Apply to shortcut
            row.Shortcut.Key = key;
            row.Shortcut.Ctrl = ctrl;
            row.Shortcut.Alt = alt;
            row.Shortcut.Shift = shift;
            row.Shortcut.Win = win;
            row.Shortcut.Enabled = true;

            StopRecording(row);
            row.TxtShortcut.Text = row.Shortcut.ToString();
            row.TxtShortcut.ForeColor = ThemeManager.TextPrimary;

            _lblValidationWarning.Text = $"✓ Shortcut set to {row.Shortcut}";
            _lblValidationWarning.ForeColor = Color.FromArgb(46, 204, 113);
        }

        private void ResetToDefaults()
        {
            var defaults = ModeShortcutValidator.CreateDefaultShortcuts();
            foreach (var row in _rows)
            {
                var def = defaults.FirstOrDefault(d => d.ModeCode == row.Shortcut.ModeCode);
                if (def != null)
                {
                    row.Shortcut.Key = def.Key;
                    row.Shortcut.Ctrl = def.Ctrl;
                    row.Shortcut.Alt = def.Alt;
                    row.Shortcut.Shift = def.Shift;
                    row.Shortcut.Win = def.Win;
                    row.Shortcut.Enabled = def.Enabled;
                }
                StopRecording(row);
                row.TxtShortcut.Text = row.Shortcut.ToString();
                row.TxtShortcut.ForeColor = row.Shortcut.Enabled ? ThemeManager.TextPrimary : ThemeManager.SubHeaderText;
            }
            _lblValidationWarning.Text = "Defaults restored (Ctrl+Alt+1..5). Click 'Save & Apply' to confirm.";
            _lblValidationWarning.ForeColor = Color.FromArgb(0, 180, 255);
        }

        private void SaveAndClose()
        {
            // Validate all enabled shortcuts
            foreach (var row in _rows)
            {
                if (row.Shortcut.Enabled && !ModeShortcutValidator.IsValid(row.Shortcut, out string reason))
                {
                    _lblValidationWarning.Text = $"⚠ Invalid shortcut on {row.Shortcut.ModeName}: {reason}";
                    _lblValidationWarning.ForeColor = Color.FromArgb(255, 80, 80);
                    return;
                }
            }

            // Commit to AppSettings
            _settings.CustomModeShortcuts = _rows.Select(r => r.Shortcut.Clone()).ToList();
            _settings.Save();

            // Update live KeyboardHook
            _hook?.SetCustomShortcuts(_settings.CustomModeShortcuts);

            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeManager.ThemeChanged -= _themeHandler;

                _fontTitle?.Dispose();
                _fontSubtitle?.Dispose();
                _fontBody?.Dispose();
                _fontBold?.Dispose();
                _fontDot?.Dispose();
                _fontWarning?.Dispose();

                try { Icon?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}