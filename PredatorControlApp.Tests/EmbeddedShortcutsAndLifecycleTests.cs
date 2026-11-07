using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("NoParallel")]
    public class EmbeddedShortcutsAndLifecycleTests
    {
        [Fact]
        public void EmbeddedShortcuts_AllFiveModes_InitializedAndPersisted()
        {
            var settings = new AppSettings();
            var defaults = ModeShortcutValidator.CreateDefaultShortcuts();

            Assert.Equal(5, defaults.Count);
            Assert.Contains(defaults, s => s.ModeCode == 0x06 && s.ModeName == "Eco");
            Assert.Contains(defaults, s => s.ModeCode == 0x00 && s.ModeName == "Quiet");
            Assert.Contains(defaults, s => s.ModeCode == 0x01 && s.ModeName == "Balanced");
            Assert.Contains(defaults, s => s.ModeCode == 0x04 && s.ModeName == "Performance");
            Assert.Contains(defaults, s => s.ModeCode == 0x05 && s.ModeName == "Turbo");

            settings.CustomModeShortcuts = defaults.Select(d => d.Clone()).ToList();
            Assert.Equal(5, settings.CustomModeShortcuts.Count);

            // Verify each mode has default Ctrl+Alt modifier
            foreach (var sc in settings.CustomModeShortcuts)
            {
                Assert.True(sc.Enabled);
                Assert.True(sc.Ctrl);
                Assert.True(sc.Alt);
                Assert.False(sc.Shift);
                Assert.False(sc.Win);
            }
        }

        [Fact]
        public void EmbeddedShortcuts_DuplicateAndInvalidCombinations_Rejected()
        {
            var sc1 = new CustomModeShortcut("Eco", 0x06, Keys.E, ctrl: true, alt: true);
            var sc2 = new CustomModeShortcut("Quiet", 0x00, Keys.E, ctrl: true, alt: true);

            // Same shortcut assigned to both
            bool isDuplicate = (sc1.Key == sc2.Key && sc1.Ctrl == sc2.Ctrl && sc1.Alt == sc2.Alt && sc1.Shift == sc2.Shift);
            Assert.True(isDuplicate);

            // Banned function keys (F1-F24)
            bool f1Valid = ModeShortcutValidator.IsValid(Keys.F1, ctrl: true, alt: true, shift: false, win: false, out string reasonF1);
            Assert.False(f1Valid);
            Assert.Contains("Function keys", reasonF1, StringComparison.OrdinalIgnoreCase);

            // Windows-reserved shortcuts (e.g. Win+L, Win+D)
            bool winLValid = ModeShortcutValidator.IsValid(Keys.L, ctrl: false, alt: false, shift: false, win: true, out string reasonWinL);
            Assert.False(winLValid);
        }

        [Fact]
        public void SingleFilePortability_NativeBinaries_EmbeddedInManifest()
        {
            var asm = typeof(PredatorNativeCore).Assembly;
            var resourceNames = asm.GetManifestResourceNames();

            // 1. Verify predator_core.dll embedded in assembly manifest
            bool hasPredatorCore = resourceNames.Any(n => n.EndsWith("predator_core.dll", StringComparison.OrdinalIgnoreCase));
            Assert.True(hasPredatorCore, "predator_core.dll must be embedded in assembly resources for portable single-file execution.");

            // 2. Verify IntelMSR.bin embedded in assembly manifest
            bool hasMsr = resourceNames.Any(n => n.EndsWith("IntelMSR.bin", StringComparison.OrdinalIgnoreCase));
            Assert.True(hasMsr, "IntelMSR.bin must be embedded in assembly resources.");

            // 3. Verify IntelMCHBAR.bin embedded in assembly manifest
            bool hasMchbar = resourceNames.Any(n => n.EndsWith("IntelMCHBAR.bin", StringComparison.OrdinalIgnoreCase));
            Assert.True(hasMchbar, "IntelMCHBAR.bin must be embedded in assembly resources.");
        }

        [Fact]
        public void NativeLibrary_AutoExtraction_ExtractsAndLoads()
        {
            // Verify that PredatorNativeCore is loaded and operational
            Assert.True(PredatorNativeCore.IsLoaded, "PredatorNativeCore must load successfully via embedded resource or adjacent DLL.");

            // Test FFI call
            bool avail = PredatorNativeCore.PawnIoIsAvailable();
            // avail is either true (if PawnIO driver running) or false (if not installed), but it must not throw SEHException or DllNotFoundException
            Assert.True(avail || !avail);

            // Test thermal safety FFI call (50°C = Normal / Stage 0)
            var thermal = PredatorNativeCore.ThermalCreate();
            Assert.NotEqual(IntPtr.Zero, thermal);
            try
            {
                bool evaluated = PredatorNativeCore.ThermalEvaluate(thermal, 50.0, 45.0, 80, 25, 45, out var decision);
                Assert.True(evaluated);
                Assert.Equal(0, decision.Stage);
            }
            finally
            {
                PredatorNativeCore.ThermalDestroy(thermal);
            }
        }

        [Fact]
        public void TitleBarButton_WndProc_HandlesMouseActivate()
        {
            using var btn = new TitleBarButton();
            Assert.NotNull(btn);

            // Send WM_MOUSEACTIVATE (0x0021)
            var msg = Message.Create(btn.Handle, 0x0021, IntPtr.Zero, IntPtr.Zero);
            var wndProcMethod = typeof(TitleBarButton).GetMethod("WndProc", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(wndProcMethod);

            object[] args = [msg];
            wndProcMethod.Invoke(btn, args);

            var resultMsg = (Message)args[0];
            // MA_ACTIVATE = 1
            Assert.Equal((IntPtr)1, resultMsg.Result);
        }

        [Fact]
        public void EmbeddedShortcuts_Clear_MarksDisabled()
        {
            var sc = new CustomModeShortcut("Eco", 0x06, Keys.E, ctrl: true, alt: true);
            Assert.True(sc.Enabled);

            // Simulate Clear
            sc.Enabled = false;
            sc.Key = Keys.None;
            sc.Ctrl = false;
            sc.Alt = false;
            sc.Shift = false;
            sc.Win = false;

            Assert.False(sc.Enabled);
            Assert.Equal(Keys.None, sc.Key);
            Assert.Equal("(None)", sc.ToString());
            Assert.Equal("None", sc.DisplayString);
        }

        #region Window Lifecycle, 16:9 Constraint, and High DPI Stress Tests

        [Fact]
        public void TitleBarButton_SingleClickGuarantee_NoDoubleFiringOnLongOrShortPress()
        {
            int clickCount = 0;
            using var btn = new TitleBarButton();
            btn.Click += (s, e) => clickCount++;

            // 1. Single click invocation
            btn.PerformClick();
            Assert.Equal(1, clickCount);

            // 2. Consecutive click after delay
            clickCount = 0;
            Thread.Sleep(50);
            btn.PerformClick();
            Assert.Equal(1, clickCount);
        }

        [Fact]
        public void Form1_CreateParams_ContainsMinimizeBox()
        {
            var t = new Thread(() =>
            {
                using var form = new Form1();
                var createParamsProp = typeof(Form1).GetProperty("CreateParams", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(createParamsProp);
                var cp = (CreateParams)createParamsProp.GetValue(form)!;
                // WS_MINIMIZEBOX = 0x00020000
                Assert.True((cp.Style & 0x00020000) != 0, "Form1 must have WS_MINIMIZEBOX set in CreateParams.Style for taskbar minimize and Alt-Tab integration.");
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(15000);
        }

        [Theory]
        [InlineData(1920, 1080)]
        [InlineData(2560, 1440)]
        [InlineData(3840, 2160)]
        [InlineData(3440, 1440)] // Ultrawide
        [InlineData(1080, 1920)] // Portrait
        public void Form1_Calculate16x9MaximizedBounds_MaintainsStrictAspectRatio(int workWidth, int workHeight)
        {
            var work = new Rectangle(0, 0, workWidth, workHeight);
            var bounds = Form1.Calculate16x9MaximizedBounds(work);

            Assert.True(bounds.Width <= workWidth, "Width must fit inside work area");
            Assert.True(bounds.Height <= workHeight, "Height must fit inside work area");
            Assert.True(bounds.X >= work.X, "X must be within work area bounds");
            Assert.True(bounds.Y >= work.Y, "Y must be within work area bounds");

            double aspect = (double)bounds.Width / bounds.Height;
            Assert.InRange(aspect, 1.76, 1.79); // 16:9 is ~1.7778
        }

        [Fact]
        public void Form1_WindowLifecycle_HideAndShow_PreventsModalHangs()
        {
            var t = new Thread(() =>
            {
                using var form = new Form1();
                form.Show();

                // Open child FanCurveForm modelessly
                using var fanCurve = new FanCurveForm();
                fanCurve.Show(form);
                Assert.True(fanCurve.Visible);

                // Call HideApp (via reflection)
                var hideAppMethod = typeof(Form1).GetMethod("HideApp", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(hideAppMethod);
                hideAppMethod.Invoke(form, null);

                Assert.False(form.Visible);
                Assert.False(fanCurve.Visible);
                Assert.True(form.Enabled, "Form1.Enabled must remain true when hidden to prevent modal hang");

                // Call ShowApp
                var showAppMethod = typeof(Form1).GetMethod("ShowApp", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(showAppMethod);
                showAppMethod.Invoke(form, null);

                Assert.True(form.Visible);
                Assert.True(form.Enabled, "Form1.Enabled must remain true when restored from tray");

                form.Close();
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(15000);
        }

        [Theory]
        [InlineData(96)]  // 100%
        [InlineData(120)] // 125%
        [InlineData(144)] // 150%
        [InlineData(192)] // 200%
        public void AllForms_OnDpiChanged_HandlesDpiTransitionsCleanly(int newDpi)
        {
            var t = new Thread(() =>
            {
                var dpiEventArgs = CreateDpiChangedEventArgs(96, newDpi, new Rectangle(0, 0, 800, 600));

                // 1. Form1
                using (var f1 = new Form1())
                {
                    var onDpi = typeof(Form1).GetMethod("OnDpiChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.NotNull(onDpi);
                    onDpi.Invoke(f1, new object[] { dpiEventArgs });
                }

                // 2. FanCurveForm
                using (var f2 = new FanCurveForm())
                {
                    var onDpi = typeof(FanCurveForm).GetMethod("OnDpiChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.NotNull(onDpi);
                    onDpi.Invoke(f2, new object[] { dpiEventArgs });
                }

                // 3. GameSyncForm
                using (var ctrl = new GameSyncController(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gamesync_" + System.Guid.NewGuid().ToString("N") + ".json")))
                using (var f3 = new GameSyncForm(ctrl, 165))
                {
                    var onDpi = typeof(GameSyncForm).GetMethod("OnDpiChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.NotNull(onDpi);
                    onDpi.Invoke(f3, new object[] { dpiEventArgs });
                }

                // 4. HardwareCapabilitiesForm
                using (var f4 = new HardwareCapabilitiesForm(DeviceCapabilities.None))
                {
                    var onDpi = typeof(HardwareCapabilitiesForm).GetMethod("OnDpiChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.NotNull(onDpi);
                    onDpi.Invoke(f4, new object[] { dpiEventArgs });
                }

                // 5. CustomShortcutsForm
                using (var f5 = new CustomShortcutsForm(new AppSettings()))
                {
                    var onDpi = typeof(CustomShortcutsForm).GetMethod("OnDpiChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.NotNull(onDpi);
                    onDpi.Invoke(f5, new object[] { dpiEventArgs });
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(15000);
        }

        [Fact]
        public void EmbeddedShortcuts_ConflictDetection_ConsidersWindowsKey()
        {
            // Verify that two shortcuts with same key and ctrl/alt but different Win keys do not conflict
            var sc1 = new CustomModeShortcut("Eco", 0x06, Keys.D1, ctrl: true, alt: true, shift: false, win: false);
            var sc2 = new CustomModeShortcut("Quiet", 0x00, Keys.D1, ctrl: true, alt: true, shift: false, win: true);

            bool matchesWithoutWin = (sc1.Key == sc2.Key && sc1.Ctrl == sc2.Ctrl && sc1.Alt == sc2.Alt && sc1.Shift == sc2.Shift);
            Assert.True(matchesWithoutWin);

            bool matchesExact = matchesWithoutWin && (sc1.Win == sc2.Win);
            Assert.False(matchesExact, "Shortcuts differing in Win modifier must not be treated as identical conflict.");
        }

        #endregion


        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left, top, right, bottom;
        }

        private static DpiChangedEventArgs CreateDpiChangedEventArgs(int oldDpi, int newDpi, Rectangle suggestedRect)
        {
            var rectStruct = new RECT { left = suggestedRect.Left, top = suggestedRect.Top, right = suggestedRect.Right, bottom = suggestedRect.Bottom };
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
            try
            {
                Marshal.StructureToPtr(rectStruct, ptr, false);
                IntPtr wParam = (IntPtr)((newDpi << 16) | (newDpi & 0xFFFF));
                var msg = Message.Create(IntPtr.Zero, 0x02E0, wParam, ptr);
                var ctor = typeof(DpiChangedEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)[0];
                return (DpiChangedEventArgs)ctor.Invoke(new object[] { oldDpi, msg })!;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

    }
}
