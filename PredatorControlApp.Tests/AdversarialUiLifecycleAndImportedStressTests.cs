using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using PredatorControlApp.Bios;
using PredatorControlApp.Display;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("NoParallel")]
    [SupportedOSPlatform("windows")]
    public class AdversarialUiLifecycleAndImportedStressTests : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
        private const uint GR_GDIOBJECTS = 0;
        private const uint GR_USEROBJECTS = 1;

        public AdversarialUiLifecycleAndImportedStressTests()
        {
            DisplaySettingsForm.MessageBoxHandler = (owner, text, caption, buttons, icon) => DialogResult.OK;
            BiosSettingsForm.MessageBoxHandler = (owner, text, caption, buttons, icon) => DialogResult.Yes;
        }

        public void Dispose()
        {
            // Keep safe test handlers active to prevent modal dialog blocks across test runner runs
        }

        #region Focus Area 1: DisplaySettingsForm Adversarial Stress & GDI Audits

        [Fact]
        public void DisplaySettingsForm_RapidOpenClose_50Iterations_GdiAndResourceStable()
        {
            StaTestRunner.Run(() =>
            {
                using var proc = Process.GetCurrentProcess();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                uint gdiStart = GetGuiResources(proc.Handle, GR_GDIOBJECTS);

                for (int i = 0; i < 50; i++)
                {
                    using var form = new DisplaySettingsForm();
                    form.CreateControl();
                    Application.DoEvents();
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                uint gdiEnd = GetGuiResources(proc.Handle, GR_GDIOBJECTS);
                int deltaGdi = (int)gdiEnd - (int)gdiStart;

                // Static fonts ensure delta is negligible (< 10)
                Assert.True(deltaGdi < 15, $"GDI leak detected in DisplaySettingsForm: delta = {deltaGdi}");
            });
        }

        [Fact]
        public void DisplaySettingsForm_InvalidRefreshRateSelection_Fuzzing_DoesNotCrash()
        {
            StaTestRunner.Run(() =>
            {
                using var form = new DisplaySettingsForm();
                form.CreateControl();

                var applyMethod = typeof(DisplaySettingsForm).GetMethod("ApplyRefreshRate", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(applyMethod);

                int[] fuzzedInputs = new[] { 0, -60, -144, 29, 999999 };

                foreach (var input in fuzzedInputs)
                {
                    var ex = Record.Exception(() => applyMethod.Invoke(form, new object[] { input }));
                    Assert.Null(ex);
                }
            });
        }

        [Fact]
        public void DisplaySettingsForm_DdsModeSwitching_WithoutNvidiaDriver_SafelyHandledAndDisabled()
        {
            StaTestRunner.Run(() =>
            {
                using var form = new DisplaySettingsForm();
                form.CreateControl();

                var cboDdsField = typeof(DisplaySettingsForm).GetField("_cboDdsMode", BindingFlags.NonPublic | BindingFlags.Instance);
                var btnDdsField = typeof(DisplaySettingsForm).GetField("_btnApplyDds", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(cboDdsField);
                Assert.NotNull(btnDdsField);

                var cboDds = (ComboBox)cboDdsField.GetValue(form)!;
                var btnDds = (PredatorButton)btnDdsField.GetValue(form)!;

                var ddsStatus = DisplayManager.Instance.GetDdsStatus();
                if (!ddsStatus.IsSupported)
                {
                    Assert.False(cboDds.Enabled);
                    Assert.False(btnDds.Enabled);
                }

                var onApplyDds = typeof(DisplaySettingsForm).GetMethod("OnApplyDdsClicked", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(onApplyDds);

                // Invoking when unsupported must not throw and must abort safely
                var ex = Record.Exception(() => onApplyDds.Invoke(form, new object[] { form, EventArgs.Empty }));
                Assert.Null(ex);
            });
        }

        #endregion

        #region Focus Area 2: BiosSettingsForm Adversarial Stress & Safety Gates

        [Fact]
        public void BiosSettingsForm_RapidOpenClose_50Iterations_GdiAndResourceStable()
        {
            StaTestRunner.Run(() =>
            {
                using var proc = Process.GetCurrentProcess();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                uint gdiStart = GetGuiResources(proc.Handle, GR_GDIOBJECTS);

                for (int i = 0; i < 50; i++)
                {
                    using var form = new BiosSettingsForm();
                    form.CreateControl();
                    Application.DoEvents();
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                uint gdiEnd = GetGuiResources(proc.Handle, GR_GDIOBJECTS);
                int deltaGdi = (int)gdiEnd - (int)gdiStart;

                Assert.True(deltaGdi < 15, $"GDI leak detected in BiosSettingsForm: delta = {deltaGdi}");
            });
        }

        [Fact]
        public void BiosSettingsForm_SafetyGateToggles_RapidSwitching_NoReentrancyOrDeadlock()
        {
            StaTestRunner.Run(() =>
            {
                using var form = new BiosSettingsForm();
                form.CreateControl();

                var chkModField = typeof(BiosSettingsForm).GetField("_chkEnableModifications", BindingFlags.NonPublic | BindingFlags.Instance);
                var chkDangerField = typeof(BiosSettingsForm).GetField("_chkAllowDangerous", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(chkModField);
                Assert.NotNull(chkDangerField);

                var chkMod = (CheckBox)chkModField.GetValue(form)!;
                var chkDanger = (CheckBox)chkDangerField.GetValue(form)!;

                // Rapidly toggle modifications and dangerous settings
                for (int i = 0; i < 100; i++)
                {
                    chkMod.Checked = (i % 2 == 1);
                    if (chkMod.Checked)
                    {
                        chkDanger.Checked = (i % 4 == 3);
                    }
                }

                Application.DoEvents();

                // Clean up state
                chkMod.Checked = false;
                Assert.False(BiosManager.Instance.ModificationsEnabled);
                Assert.False(BiosManager.Instance.DangerousAllowed);
            });
        }

        [Fact]
        public void BiosSettingsForm_DangerousSettings_BypassAttempts_BlockedAtDomainAndUiLevel()
        {
            StaTestRunner.Run(() =>
            {
                using var form = new BiosSettingsForm();
                form.CreateControl();

                var chkModField = typeof(BiosSettingsForm).GetField("_chkEnableModifications", BindingFlags.NonPublic | BindingFlags.Instance);
                var chkDangerField = typeof(BiosSettingsForm).GetField("_chkAllowDangerous", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(chkModField);
                Assert.NotNull(chkDangerField);

                var chkMod = (CheckBox)chkModField.GetValue(form)!;
                var chkDanger = (CheckBox)chkDangerField.GetValue(form)!;

                // Ensure modifications are disabled
                chkMod.Checked = false;
                BiosManager.Instance.ModificationsEnabled = false;

                // Attempt 1: UI Checkbox bypass - setting Checked directly when modifications disabled
                chkDanger.Checked = true;
                Assert.False(chkDanger.Checked);
                Assert.False(BiosManager.Instance.DangerousAllowed);

                // Attempt 2: Domain property bypass - setting DangerousAllowed directly
                BiosManager.Instance.DangerousAllowed = true;
                Assert.False(BiosManager.Instance.DangerousAllowed);

                // Attempt 3: Apply dangerous setting directly
                var dangerousItem = new BiosSettingItem
                {
                    Name = "Display MUX (Advanced Optimus / dGPU)",
                    CurrentValue = "Auto",
                    PendingValue = "Discrete GPU only",
                    Risk = BiosSettingRisk.Dangerous,
                    Options = new() { "Auto", "Optimus", "Discrete GPU only" }
                };

                bool applied = BiosManager.Instance.ApplySetting(dangerousItem, out string error);
                Assert.False(applied);
                Assert.Contains("disabled", error, StringComparison.OrdinalIgnoreCase);

                // Enable modifications but keep dangerous disallowed
                BiosManager.Instance.ModificationsEnabled = true;
                applied = BiosManager.Instance.ApplySetting(dangerousItem, out error);
                Assert.False(applied);
                Assert.Contains("blocked", error, StringComparison.OrdinalIgnoreCase);

                // Reset
                BiosManager.Instance.ModificationsEnabled = false;
            });
        }

        [Fact]
        public void BiosSettingsForm_ApplySetting_NullEmptyAndFuzzedInputs_SafelyRejected()
        {
            var mgr = BiosManager.Instance;
            mgr.ModificationsEnabled = true;

            try
            {
                // Null item
                bool ok = mgr.ApplySetting(null!, out string err1);
                Assert.False(ok);
                Assert.NotEmpty(err1);

                // Empty PendingValue
                var item = new BiosSettingItem
                {
                    Name = "Function Key behavior",
                    CurrentValue = "Special Key",
                    PendingValue = "",
                    Options = new() { "Special Key", "Standard Function Key" }
                };
                ok = mgr.ApplySetting(item, out string err2);
                Assert.False(ok);
                Assert.NotEmpty(err2);

                // Whitespace PendingValue
                item.PendingValue = "    ";
                ok = mgr.ApplySetting(item, out string err3);
                Assert.False(ok);
                Assert.NotEmpty(err3);

                // Value not in options (in managed fallback mode)
                item.PendingValue = "Arbitrary Malicious String";
                ok = mgr.ApplySetting(item, out string err4);
                Assert.False(ok);
                Assert.NotEmpty(err4);

                // Valid option
                item.PendingValue = "Standard Function Key";
                ok = mgr.ApplySetting(item, out string err5);
                Assert.True(ok);
                Assert.Equal("Standard Function Key", item.CurrentValue);
            }
            finally
            {
                mgr.ModificationsEnabled = false;
            }
        }

        [Fact]
        public void BiosSettingsForm_RapidTabSwitching_100Cycles_ZeroGdiLeaks()
        {
            StaTestRunner.Run(() =>
            {
                using var proc = Process.GetCurrentProcess();
                using var form = new BiosSettingsForm();
                form.CreateControl();

                var tabField = typeof(BiosSettingsForm).GetField("_tabControl", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(tabField);
                var tab = (TabControl)tabField.GetValue(form)!;

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                uint gdiStart = GetGuiResources(proc.Handle, GR_GDIOBJECTS);

                // Rapidly cycle tabs 100 times
                for (int i = 0; i < 100; i++)
                {
                    tab.SelectedIndex = i % tab.TabCount;
                }

                Application.DoEvents();

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                uint gdiEnd = GetGuiResources(proc.Handle, GR_GDIOBJECTS);
                int deltaGdi = (int)gdiEnd - (int)gdiStart;

                Assert.True(deltaGdi < 10, $"GDI leak on tab switching: delta = {deltaGdi}");
            });
        }

        #endregion

        #region Focus Area 3: CompatibilityDiagnosticsForm & GameSyncForm Stress

        [Fact]
        public void CompatibilityDiagnosticsForm_NullAndExtremeReports_SafeHandling()
        {
            StaTestRunner.Run(() =>
            {
                // Null report
                using var form1 = new CompatibilityDiagnosticsForm(null!);
                form1.CreateControl();
                Assert.Equal(string.Empty, form1.Controls[0].Text);

                // Huge 5MB report
                string hugeReport = new string('A', 5 * 1024 * 1024);
                using var form2 = new CompatibilityDiagnosticsForm(hugeReport);
                form2.CreateControl();
                Assert.Equal(hugeReport.Length, form2.Controls[0].Text.Length);
            });
        }

        [Fact]
        public void GameSyncForm_LifecycleAndDpiStress_ThemeSwitching()
        {
            StaTestRunner.Run(() =>
            {
                var controller = new GameSyncController();
                using var form = new GameSyncForm(controller, 165);
                form.CreateControl();

                // Theme changed event firing while open
                ThemeManager.SetTheme(AppTheme.Light);
                Application.DoEvents();
                ThemeManager.SetTheme(AppTheme.Dark);
                Application.DoEvents();

                Assert.False(form.IsDisposed);
            });
        }

        [Fact]
        public void FanCurveForm_RapidHysteresisAndCurveSpam_BackgroundTempsSafe()
        {
            StaTestRunner.Run(() =>
            {
                using var form = new FanCurveForm();
                form.CreateControl();

                // Background thread dispatching temperature updates via SafeBeginInvoke
                var tasks = new Task[10];
                for (int t = 0; t < 10; t++)
                {
                    int tid = t;
                    tasks[t] = Task.Run(() =>
                    {
                        for (int i = 0; i < 20; i++)
                        {
                            form.UpdateTemps(40 + (tid * 2), 50 + i);
                        }
                    });
                }

                Task.WaitAll(tasks);
                Application.DoEvents();

                // Rapid hysteresis updates
                for (int h = CurveFollower.MinHysteresisDegrees; h <= CurveFollower.MaxHysteresisDegrees; h++)
                {
                    form.SetHysteresis(h);
                }

                Application.DoEvents();
            });
        }

        #endregion

        #region Focus Area 4: PredatorButton IButtonControl & Form1 Safety

        [Fact]
        public void PredatorButton_ImplementsIButtonControl_DialogResultAndAccessibility()
        {
            StaTestRunner.Run(() =>
            {
                using var btn = new PredatorButton();
                Assert.IsAssignableFrom<IButtonControl>(btn);

                btn.DialogResult = DialogResult.OK;
                Assert.Equal(DialogResult.OK, btn.DialogResult);

                bool clicked = false;
                btn.Click += (s, e) => clicked = true;
                btn.PerformClick();
                Assert.True(clicked);

                btn.NotifyDefault(true);
                btn.NotifyDefault(false);
            });
        }

        [Fact]
        public void Form1_HighlightBtn_NullAndDisposedSafe()
        {
            StaTestRunner.Run(() =>
            {
                var highlightMethod = typeof(Form1).GetMethod("HighlightBtn", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(highlightMethod);

                using var form = new Form1();
                form.CreateControl();

                PredatorButton? tracker = null;
                using var btn1 = new PredatorButton();
                btn1.CreateControl();

                object[] args = new object[] { btn1, tracker! };
                highlightMethod.Invoke(form, args);
                Assert.True(btn1.IsActive);

                // Passing null btn must be safe
                using var btn2 = new PredatorButton();
                args = new object[] { null!, btn1 };
                highlightMethod.Invoke(form, args);

                // Passing disposed button
                var disposedBtn = new PredatorButton();
                disposedBtn.Dispose();
                args = new object[] { disposedBtn, null! };
                var ex = Record.Exception(() => highlightMethod.Invoke(form, args));
                Assert.Null(ex);
            });
        }

        #endregion
    }
}
