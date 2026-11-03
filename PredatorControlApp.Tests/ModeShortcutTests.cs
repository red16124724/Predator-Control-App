using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("NoParallel")]
    public class ModeShortcutTests : IDisposable
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const uint VK_CONTROL = 0x11;
        private const uint VK_LCONTROL = 0xA2;
        private const uint VK_RCONTROL = 0xA3;
        private const uint VK_MENU = 0x12; // Alt
        private const uint VK_LMENU = 0xA4;
        private const uint VK_RMENU = 0xA5;
        private const uint VK_SHIFT = 0x10;
        private const uint VK_LSHIFT = 0xA0;
        private const uint VK_RSHIFT = 0xA1;
        private const uint VK_LWIN = 0x5B;
        private const uint VK_RWIN = 0x5C;

        private const uint LLKHF_INJECTED = 0x10;

        private readonly MethodInfo _hookCallbackMethod;
        private readonly Type _structType;

        public ModeShortcutTests()
        {
            _hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Could not find KeyboardHook.HookCallback method.");
            _structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find KeyboardHook.KBDLLHOOKSTRUCT type.");
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        #region Helper Methods for Keyboard Hook Simulation

        private void SimulateKeyEvent(KeyboardHook hook, uint vkCode, bool isDown, bool isSysKey = false, uint flags = 0, uint scanCode = 0)
        {
            object hookStruct = Activator.CreateInstance(_structType)!;
            _structType.GetField("vkCode")!.SetValue(hookStruct, vkCode);
            _structType.GetField("scanCode")!.SetValue(hookStruct, scanCode);
            _structType.GetField("flags")!.SetValue(hookStruct, flags);
            _structType.GetField("time")!.SetValue(hookStruct, (uint)Environment.TickCount);
            _structType.GetField("dwExtraInfo")!.SetValue(hookStruct, IntPtr.Zero);

            int wmMsg = isDown
                ? (isSysKey ? WM_SYSKEYDOWN : WM_KEYDOWN)
                : (isSysKey ? WM_SYSKEYUP : WM_KEYUP);

            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                Marshal.StructureToPtr(hookStruct, pStruct, false);
                _hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)wmMsg, pStruct });
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }
        }

        #endregion

        #region 1. ModeShortcutValidator Function Keys Tests

        [Fact]
        public void ModeShortcutValidator_AllFunctionKeysF1ToF24_Bare_AreRejectedWithExactErrorMessage()
        {
            for (int f = 1; f <= 24; f++)
            {
                Keys fKey = (Keys)Enum.Parse(typeof(Keys), $"F{f}");
                bool isValid = ModeShortcutValidator.IsValid(fKey, ctrl: false, alt: false, shift: false, win: false, out string failureReason);

                Assert.False(isValid, $"Function key F{f} bare should be rejected.");
                Assert.Contains("Function keys (F1-F24)", failureReason, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("not allowed", failureReason, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void ModeShortcutValidator_AllFunctionKeysF1ToF24_WithCtrl_AreRejectedWithExactErrorMessage()
        {
            for (int f = 1; f <= 24; f++)
            {
                Keys fKey = (Keys)Enum.Parse(typeof(Keys), $"F{f}");
                bool isValid = ModeShortcutValidator.IsValid(fKey, ctrl: true, alt: false, shift: false, win: false, out string failureReason);

                Assert.False(isValid, $"Function key F{f} with Ctrl should be rejected.");
                Assert.Contains("Function keys (F1-F24)", failureReason, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("not allowed", failureReason, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void ModeShortcutValidator_AllFunctionKeysF1ToF24_WithAlt_AreRejectedWithExactErrorMessage()
        {
            for (int f = 1; f <= 24; f++)
            {
                Keys fKey = (Keys)Enum.Parse(typeof(Keys), $"F{f}");
                bool isValid = ModeShortcutValidator.IsValid(fKey, ctrl: false, alt: true, shift: false, win: false, out string failureReason);

                Assert.False(isValid, $"Function key F{f} with Alt should be rejected.");
                Assert.Contains("Function keys (F1-F24)", failureReason, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("not allowed", failureReason, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void ModeShortcutValidator_AllFunctionKeysF1ToF24_WithShift_AreRejectedWithExactErrorMessage()
        {
            for (int f = 1; f <= 24; f++)
            {
                Keys fKey = (Keys)Enum.Parse(typeof(Keys), $"F{f}");
                bool isValid = ModeShortcutValidator.IsValid(fKey, ctrl: false, alt: false, shift: true, win: false, out string failureReason);

                Assert.False(isValid, $"Function key F{f} with Shift should be rejected.");
                Assert.Contains("Function keys (F1-F24)", failureReason, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("not allowed", failureReason, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void ModeShortcutValidator_AllFunctionKeysF1ToF24_WithWin_AreRejectedWithExactErrorMessage()
        {
            for (int f = 1; f <= 24; f++)
            {
                Keys fKey = (Keys)Enum.Parse(typeof(Keys), $"F{f}");
                bool isValid = ModeShortcutValidator.IsValid(fKey, ctrl: false, alt: false, shift: false, win: true, out string failureReason);

                Assert.False(isValid, $"Function key F{f} with Win should be rejected.");
                Assert.Contains("Function keys (F1-F24)", failureReason, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("not allowed", failureReason, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void ModeShortcutValidator_AllFunctionKeysF1ToF24_WithCtrlAltShiftWinCombinations_AreRejected()
        {
            for (int f = 1; f <= 24; f++)
            {
                Keys fKey = (Keys)Enum.Parse(typeof(Keys), $"F{f}");

                // Ctrl + Alt
                Assert.False(ModeShortcutValidator.IsValid(fKey, ctrl: true, alt: true, shift: false, win: false, out string err1));
                Assert.Contains("Function keys (F1-F24)", err1);

                // Ctrl + Shift
                Assert.False(ModeShortcutValidator.IsValid(fKey, ctrl: true, alt: false, shift: true, win: false, out string err2));
                Assert.Contains("Function keys (F1-F24)", err2);

                // Alt + Shift
                Assert.False(ModeShortcutValidator.IsValid(fKey, ctrl: false, alt: true, shift: true, win: false, out string err3));
                Assert.Contains("Function keys (F1-F24)", err3);

                // Ctrl + Alt + Shift + Win
                Assert.False(ModeShortcutValidator.IsValid(fKey, ctrl: true, alt: true, shift: true, win: true, out string err4));
                Assert.Contains("Function keys (F1-F24)", err4);
            }
        }

        [Fact]
        public void ModeShortcutValidator_FunctionKeyVirtualKeyCodes_DirectlyRejected()
        {
            // VK_F1 (0x70) to VK_F24 (0x87)
            for (int vk = 0x70; vk <= 0x87; vk++)
            {
                bool isValid = ModeShortcutValidator.IsValid(vk, ctrl: true, alt: true, shift: false, win: false, out string failureReason);
                Assert.False(isValid);
                Assert.Contains("Function keys (F1-F24)", failureReason);
                Assert.Contains("not allowed", failureReason);
            }
        }

        [Fact]
        public void ModeShortcutValidator_CustomModeShortcutInstance_WrappingFunctionKey_Rejected()
        {
            var shortcut = new CustomModeShortcut("Turbo", 0x05, Keys.F5, ctrl: true, alt: true);
            bool isValid = ModeShortcutValidator.IsValid(shortcut, out string failureReason);

            Assert.False(isValid);
            Assert.Contains("Function keys (F1-F24)", failureReason);
            Assert.Contains("not allowed", failureReason);
        }

        #endregion

        #region 2. ModeShortcutValidator Default Windows Shortcuts Tests

        [Theory]
        [InlineData(Keys.L)]
        [InlineData(Keys.D)]
        [InlineData(Keys.E)]
        [InlineData(Keys.R)]
        [InlineData(Keys.X)]
        [InlineData(Keys.S)]
        [InlineData(Keys.A)]
        [InlineData(Keys.V)]
        [InlineData(Keys.P)]
        [InlineData(Keys.I)]
        [InlineData(Keys.Tab)]
        [InlineData(Keys.M)]
        [InlineData(Keys.B)]
        [InlineData(Keys.K)]
        [InlineData(Keys.U)]
        public void ModeShortcutValidator_WindowsKeyCombinations_AreRejected(Keys key)
        {
            bool isValid = ModeShortcutValidator.IsValid(key, ctrl: false, alt: false, shift: false, win: true, out string failureReason);
            Assert.False(isValid, $"Win+{key} must be rejected.");
            Assert.Contains("Windows key", failureReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ModeShortcutValidator_WinShiftS_IsRejected()
        {
            bool isValid = ModeShortcutValidator.IsValid(Keys.S, ctrl: false, alt: false, shift: true, win: true, out string failureReason);
            Assert.False(isValid, "Win+Shift+S must be rejected.");
            Assert.Contains("Windows key", failureReason, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(Keys.Tab, "Alt+Tab")]
        [InlineData(Keys.Escape, "Alt+Esc")]
        [InlineData(Keys.Space, "Alt+Space")]
        [InlineData(Keys.Enter, "Alt+Enter")]
        public void ModeShortcutValidator_AltWindowManagementShortcuts_AreRejected(Keys key, string name)
        {
            bool isValid = ModeShortcutValidator.IsValid(key, ctrl: false, alt: true, shift: false, win: false, out string failureReason);
            Assert.False(isValid, $"{name} must be rejected.");
            Assert.False(string.IsNullOrEmpty(failureReason));
        }

        [Fact]
        public void ModeShortcutValidator_AltShiftTab_IsRejected()
        {
            bool isValid = ModeShortcutValidator.IsValid(Keys.Tab, ctrl: false, alt: true, shift: true, win: false, out string failureReason);
            Assert.False(isValid, "Alt+Shift+Tab must be rejected.");
        }

        [Fact]
        public void ModeShortcutValidator_AltF4_IsRejected()
        {
            bool isValid = ModeShortcutValidator.IsValid(Keys.F4, ctrl: false, alt: true, shift: false, win: false, out string failureReason);
            Assert.False(isValid, "Alt+F4 must be rejected.");
            Assert.Contains("Function keys", failureReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ModeShortcutValidator_CtrlAltDel_IsRejected()
        {
            bool isValid = ModeShortcutValidator.IsValid(Keys.Delete, ctrl: true, alt: true, shift: false, win: false, out string failureReason);
            Assert.False(isValid, "Ctrl+Alt+Del must be rejected.");
            Assert.Contains("Ctrl+Alt+Del", failureReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ModeShortcutValidator_CtrlShiftEsc_IsRejected()
        {
            bool isValid = ModeShortcutValidator.IsValid(Keys.Escape, ctrl: true, alt: false, shift: true, win: false, out string failureReason);
            Assert.False(isValid, "Ctrl+Shift+Esc must be rejected.");
            Assert.False(string.IsNullOrEmpty(failureReason));
        }

        [Theory]
        [InlineData(Keys.C, "Copy")]
        [InlineData(Keys.V, "Paste")]
        [InlineData(Keys.X, "Cut")]
        [InlineData(Keys.Z, "Undo")]
        [InlineData(Keys.Y, "Redo")]
        [InlineData(Keys.A, "Select All")]
        [InlineData(Keys.S, "Save")]
        [InlineData(Keys.P, "Print")]
        [InlineData(Keys.O, "Open")]
        [InlineData(Keys.N, "New")]
        [InlineData(Keys.W, "Close")]
        [InlineData(Keys.F, "Find")]
        [InlineData(Keys.H, "Replace/History")]
        [InlineData(Keys.T, "New Tab")]
        [InlineData(Keys.D, "Bookmark")]
        [InlineData(Keys.R, "Reload")]
        [InlineData(Keys.J, "Downloads")]
        [InlineData(Keys.Tab, "Switch Tab")]
        public void ModeShortcutValidator_StandardCtrlEditingShortcuts_WithoutAlt_AreRejected(Keys key, string description)
        {
            bool isValid = ModeShortcutValidator.IsValid(key, ctrl: true, alt: false, shift: false, win: false, out string failureReason);
            Assert.False(isValid, $"Ctrl+{key} ({description}) must be rejected.");
            Assert.Contains("reserved", failureReason, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(Keys.Escape)]
        [InlineData(Keys.Tab)]
        [InlineData(Keys.PrintScreen)]
        [InlineData(Keys.Pause)]
        [InlineData(Keys.NumLock)]
        [InlineData(Keys.CapsLock)]
        [InlineData(Keys.Scroll)]
        [InlineData(Keys.Apps)]
        [InlineData(Keys.Sleep)]
        public void ModeShortcutValidator_SystemControlKeys_AreRejected(Keys key)
        {
            // System keys with modifiers
            bool isValid = ModeShortcutValidator.IsValid(key, ctrl: true, alt: true, shift: false, win: false, out string failureReason);
            Assert.False(isValid, $"System key {key} must be rejected even with Ctrl+Alt.");
            Assert.Contains("reserved system", failureReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ModeShortcutValidator_WinKeyAsBaseKey_IsRejected()
        {
            Assert.False(ModeShortcutValidator.IsValid(Keys.LWin, ctrl: true, alt: false, shift: false, win: false, out _));
            Assert.False(ModeShortcutValidator.IsValid(Keys.RWin, ctrl: true, alt: false, shift: false, win: false, out _));
        }

        #endregion

        #region 3. ModeShortcutValidator Bare Keys Tests

        [Theory]
        [InlineData(Keys.E)]
        [InlineData(Keys.D1)]
        [InlineData(Keys.A)]
        [InlineData(Keys.Q)]
        [InlineData(Keys.B)]
        [InlineData(Keys.P)]
        [InlineData(Keys.T)]
        [InlineData(Keys.D0)]
        [InlineData(Keys.D9)]
        [InlineData(Keys.Space)]
        public void ModeShortcutValidator_SingleBareKeysWithoutModifiers_AreRejectedToProtectTyping(Keys key)
        {
            bool isValid = ModeShortcutValidator.IsValid(key, ctrl: false, alt: false, shift: false, win: false, out string failureReason);
            Assert.False(isValid, $"Bare key {key} without modifiers must be rejected.");
            Assert.Contains("at least one modifier", failureReason, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(Keys.ControlKey)]
        [InlineData(Keys.LControlKey)]
        [InlineData(Keys.RControlKey)]
        [InlineData(Keys.Menu)]
        [InlineData(Keys.LMenu)]
        [InlineData(Keys.RMenu)]
        [InlineData(Keys.ShiftKey)]
        [InlineData(Keys.LShiftKey)]
        [InlineData(Keys.RShiftKey)]
        public void ModeShortcutValidator_ModifierKeysAlone_AreRejected(Keys key)
        {
            bool isValid = ModeShortcutValidator.IsValid(key, ctrl: false, alt: false, shift: false, win: false, out string failureReason);
            Assert.False(isValid, $"Modifier key {key} alone must be rejected.");
        }

        [Fact]
        public void ModeShortcutValidator_KeysNone_IsRejected()
        {
            bool isValid = ModeShortcutValidator.IsValid(Keys.None, ctrl: true, alt: true, shift: false, win: false, out string failureReason);
            Assert.False(isValid, "Keys.None must be rejected.");
            Assert.Contains("No primary key", failureReason, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region 4. ModeShortcutValidator Valid Shortcuts Tests

        [Theory]
        [InlineData(Keys.E, true, true, false, false, "Ctrl+Alt+E")]
        [InlineData(Keys.Q, true, true, false, false, "Ctrl+Alt+Q")]
        [InlineData(Keys.B, true, true, false, false, "Ctrl+Alt+B")]
        [InlineData(Keys.P, true, true, false, false, "Ctrl+Alt+P")]
        [InlineData(Keys.T, true, true, false, false, "Ctrl+Alt+T")]
        [InlineData(Keys.D1, true, false, true, false, "Ctrl+Shift+1")]
        [InlineData(Keys.D2, true, false, true, false, "Ctrl+Shift+2")]
        [InlineData(Keys.D3, true, false, true, false, "Ctrl+Shift+3")]
        [InlineData(Keys.D4, true, false, true, false, "Ctrl+Shift+4")]
        [InlineData(Keys.D5, true, false, true, false, "Ctrl+Shift+5")]
        [InlineData(Keys.D1, true, true, false, false, "Ctrl+Alt+1")]
        [InlineData(Keys.D2, true, true, false, false, "Ctrl+Alt+2")]
        [InlineData(Keys.D3, true, true, false, false, "Ctrl+Alt+3")]
        [InlineData(Keys.D4, true, true, false, false, "Ctrl+Alt+4")]
        [InlineData(Keys.D5, true, true, false, false, "Ctrl+Alt+5")]
        [InlineData(Keys.NumPad1, true, true, false, false, "Ctrl+Alt+NumPad1")]
        [InlineData(Keys.NumPad5, true, true, false, false, "Ctrl+Alt+NumPad5")]
        [InlineData(Keys.M, true, true, false, false, "Ctrl+Alt+M")]
        [InlineData(Keys.K, true, true, false, false, "Ctrl+Alt+K")]
        [InlineData(Keys.E, true, true, true, false, "Ctrl+Alt+Shift+E")]
        public void ModeShortcutValidator_ValidShortcuts_AreAccepted(Keys key, bool ctrl, bool alt, bool shift, bool win, string expectedName)
        {
            bool isValid = ModeShortcutValidator.IsValid(key, ctrl, alt, shift, win, out string failureReason);
            Assert.True(isValid, $"Valid shortcut {expectedName} was rejected with reason: {failureReason}");
            Assert.Empty(failureReason);
        }

        [Fact]
        public void ModeShortcutValidator_FormatShortcutAndKeyName_OutputsCorrectFormat()
        {
            string s1 = ModeShortcutValidator.FormatShortcut(Keys.E, ctrl: true, alt: true, shift: false, win: false);
            Assert.Equal("Ctrl+Alt+E", s1);

            string s2 = ModeShortcutValidator.FormatShortcut(Keys.D1, ctrl: true, alt: false, shift: true, win: false);
            Assert.Equal("Ctrl+Shift+1", s2);

            string s3 = ModeShortcutValidator.FormatShortcut(Keys.NumPad3, ctrl: true, alt: true, shift: false, win: false);
            Assert.Equal("Ctrl+Alt+NumPad 3", s3);
        }

        #endregion

        #region 5. Modifier Combinations and Duplicate Detection Tests

        [Fact]
        public void ModeShortcutValidator_DefaultShortcuts_PassValidation()
        {
            var defaults = ModeShortcutValidator.CreateDefaultShortcuts();
            Assert.NotNull(defaults);
            Assert.Equal(5, defaults.Count);

            bool isValid = ModeShortcutValidator.ValidateShortcutSet(defaults, out string failureReason);
            Assert.True(isValid, $"Default shortcuts failed validation: {failureReason}");
            Assert.Empty(failureReason);
        }

        [Fact]
        public void ModeShortcutValidator_NumericDefaultShortcuts_PassValidation()
        {
            var numericDefaults = ModeShortcutValidator.GetNumericDefaultShortcuts();
            Assert.NotNull(numericDefaults);
            Assert.Equal(5, numericDefaults.Count);

            bool isValid = ModeShortcutValidator.ValidateShortcutSet(numericDefaults, out string failureReason);
            Assert.True(isValid, $"Numeric default shortcuts failed validation: {failureReason}");
            Assert.Empty(failureReason);
        }

        [Fact]
        public void ModeShortcutValidator_DuplicateShortcutAcrossModes_IsDetectedAndRejected()
        {
            var shortcuts = new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true),
                new("Quiet", 0x00, Keys.E, ctrl: true, alt: true) // Duplicate!
            };

            bool isValid = ModeShortcutValidator.ValidateShortcutSet(shortcuts, out string failureReason);
            Assert.False(isValid, "Duplicate shortcuts across modes must be rejected.");
            Assert.Contains("multiple modes", failureReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ModeShortcutValidator_NullOrEmptyShortcutSet_IsRejected()
        {
            Assert.False(ModeShortcutValidator.ValidateShortcutSet(null, out string err1));
            Assert.Contains("cannot be null", err1, StringComparison.OrdinalIgnoreCase);

            Assert.False(ModeShortcutValidator.ValidateShortcutSet(new List<CustomModeShortcut>(), out string err2));
            Assert.Contains("cannot be empty", err2, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ModeShortcutValidator_ShortcutSetContainingInvalidItem_IsRejected()
        {
            var shortcuts = new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true),
                new("Quiet", 0x00, Keys.F1, ctrl: true, alt: true) // Invalid function key!
            };

            bool isValid = ModeShortcutValidator.ValidateShortcutSet(shortcuts, out string failureReason);
            Assert.False(isValid);
            Assert.Contains("Function keys", failureReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CustomModeShortcut_HasSameCombination_MatchesCorrectly()
        {
            var s1 = new CustomModeShortcut("Eco", 0x06, Keys.E, ctrl: true, alt: true);
            var s2 = new CustomModeShortcut("DifferentName", 0x99, Keys.E, ctrl: true, alt: true);
            var s3 = new CustomModeShortcut("Eco", 0x06, Keys.E, ctrl: true, alt: false, shift: true);

            Assert.True(s1.HasSameCombination(s2));
            Assert.False(s1.HasSameCombination(s3));
            Assert.False(s1.HasSameCombination(null));
        }

        [Fact]
        public void ModeShortcutValidator_TryFindMatchingMode_FindsCorrectShortcut()
        {
            var shortcuts = ModeShortcutValidator.CreateDefaultShortcuts();

            // Match Ctrl+Alt+E (Eco, 0x06)
            bool found = ModeShortcutValidator.TryFindMatchingMode(shortcuts, Keys.E, ctrl: true, alt: true, shift: false, win: false, out var matched);
            Assert.True(found);
            Assert.NotNull(matched);
            Assert.Equal("Eco", matched!.ModeName);
            Assert.Equal((byte)0x06, matched.ModeCode);

            // Non-matching modifier
            bool notFound1 = ModeShortcutValidator.TryFindMatchingMode(shortcuts, Keys.E, ctrl: true, alt: false, shift: false, win: false, out _);
            Assert.False(notFound1);

            // Disabled shortcut
            matched.Enabled = false;
            bool notFound2 = ModeShortcutValidator.TryFindMatchingMode(shortcuts, Keys.E, ctrl: true, alt: true, shift: false, win: false, out _);
            Assert.False(notFound2);
        }

        #endregion

        #region 6. AppSettings Serialization and Persistence Tests

        [Fact]
        public void AppSettings_CustomModeShortcuts_SerializationRoundTrip_PreservesAllData()
        {
            var original = new AppSettings();
            original.CustomModeShortcuts = new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true, shift: false, win: false, enabled: true),
                new("Quiet", 0x00, Keys.Q, ctrl: true, alt: true, shift: false, win: false, enabled: true),
                new("Balanced", 0x01, Keys.D3, ctrl: true, alt: false, shift: true, win: false, enabled: true),
                new("Performance", 0x04, Keys.P, ctrl: true, alt: true, shift: true, win: false, enabled: false),
                new("Turbo", 0x05, Keys.T, ctrl: true, alt: true, shift: false, win: false, enabled: true)
            };

            string json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
            Assert.False(string.IsNullOrWhiteSpace(json));
            Assert.Contains("CustomModeShortcuts", json);

            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);
            Assert.NotNull(deserialized);
            Assert.NotNull(deserialized!.CustomModeShortcuts);
            Assert.Equal(5, deserialized.CustomModeShortcuts.Count);

            for (int i = 0; i < original.CustomModeShortcuts.Count; i++)
            {
                var exp = original.CustomModeShortcuts[i];
                var act = deserialized.CustomModeShortcuts[i];

                Assert.Equal(exp.ModeName, act.ModeName);
                Assert.Equal(exp.ModeCode, act.ModeCode);
                Assert.Equal(exp.Key, act.Key);
                Assert.Equal(exp.Ctrl, act.Ctrl);
                Assert.Equal(exp.Alt, act.Alt);
                Assert.Equal(exp.Shift, act.Shift);
                Assert.Equal(exp.Win, act.Win);
                Assert.Equal(exp.Enabled, act.Enabled);
            }
        }

        [Fact]
        public void AppSettings_Clone_DeepCopiesCustomModeShortcuts()
        {
            var original = new AppSettings();
            original.CustomModeShortcuts = ModeShortcutValidator.CreateDefaultShortcuts();

            var clone = original.CreateSnapshot();
            Assert.NotNull(clone.CustomModeShortcuts);
            Assert.Equal(original.CustomModeShortcuts.Count, clone.CustomModeShortcuts.Count);
            Assert.NotSame(original.CustomModeShortcuts, clone.CustomModeShortcuts);

            // Mutating clone must not affect original
            clone.CustomModeShortcuts[0].Key = Keys.Z;
            Assert.NotEqual(clone.CustomModeShortcuts[0].Key, original.CustomModeShortcuts[0].Key);
        }

        [Fact]
        public void AppSettings_Sanitize_WhenCustomModeShortcutsNull_InitializesDefaults()
        {
            var settings = new AppSettings();
            settings.CustomModeShortcuts = null!;

            settings.Sanitize();

            Assert.NotNull(settings.CustomModeShortcuts);
            Assert.Equal(5, settings.CustomModeShortcuts.Count);
            Assert.Contains(settings.CustomModeShortcuts, s => s.ModeCode == 0x06); // Eco
            Assert.Contains(settings.CustomModeShortcuts, s => s.ModeCode == 0x00); // Quiet
            Assert.Contains(settings.CustomModeShortcuts, s => s.ModeCode == 0x01); // Balanced
            Assert.Contains(settings.CustomModeShortcuts, s => s.ModeCode == 0x04); // Performance
            Assert.Contains(settings.CustomModeShortcuts, s => s.ModeCode == 0x05); // Turbo
        }

        [Fact]
        public void AppSettings_Sanitize_WhenCustomModeShortcutsEmpty_InitializesDefaults()
        {
            var settings = new AppSettings();
            settings.CustomModeShortcuts = new List<CustomModeShortcut>();

            settings.Sanitize();

            Assert.NotNull(settings.CustomModeShortcuts);
            Assert.Equal(5, settings.CustomModeShortcuts.Count);
        }

        [Fact]
        public void AppSettings_Sanitize_WhenShortcutInvalid_DisablesShortcut()
        {
            var settings = new AppSettings();
            settings.CustomModeShortcuts = new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true, enabled: true),
                new("Turbo", 0x05, Keys.F1, ctrl: true, alt: true, enabled: true), // Invalid F-key!
                new("Quiet", 0x00, Keys.Q, ctrl: false, alt: false, enabled: true) // Invalid bare key!
            };

            settings.Sanitize();

            Assert.True(settings.CustomModeShortcuts[0].Enabled, "Valid shortcut should remain enabled.");
            Assert.False(settings.CustomModeShortcuts[1].Enabled, "Function key shortcut should be disabled by Sanitize.");
            Assert.False(settings.CustomModeShortcuts[2].Enabled, "Bare key shortcut should be disabled by Sanitize.");
        }

        #endregion

        #region 7. Hook Triggering Simulation Tests

        [Fact]
        public void KeyboardHook_CustomModeShortcut_SimulateMatchingShortcut_InvokesCallbackWithCorrectModeByte()
        {
            var triggeredModes = new List<byte>();
            using var autoReset = new AutoResetEvent(false);

            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onCustomModeShortcutTriggered: mode =>
                {
                    lock (triggeredModes)
                    {
                        triggeredModes.Add(mode);
                    }
                    autoReset.Set();
                });

            // Configure standard shortcuts
            hook.SetCustomShortcuts(new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true),
                new("Quiet", 0x00, Keys.Q, ctrl: true, alt: true),
                new("Balanced", 0x01, Keys.B, ctrl: true, alt: true),
                new("Performance", 0x04, Keys.P, ctrl: true, alt: true),
                new("Turbo", 0x05, Keys.T, ctrl: true, alt: true)
            });

            // 1. Test Eco (0x06) via Ctrl+Alt+E
            // Press Ctrl
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: true);
            // Press Alt (WM_SYSKEYDOWN)
            SimulateKeyEvent(hook, VK_LMENU, isDown: true, isSysKey: true);
            // Press 'E'
            SimulateKeyEvent(hook, (uint)Keys.E, isDown: true, isSysKey: true);

            Assert.True(autoReset.WaitOne(1000), "Callback was not invoked for Eco (0x06).");
            lock (triggeredModes)
            {
                Assert.Single(triggeredModes);
                Assert.Equal((byte)0x06, triggeredModes.Last());
            }

            // Release 'E'
            SimulateKeyEvent(hook, (uint)Keys.E, isDown: false, isSysKey: true);

            // 2. Test Quiet (0x00) via Ctrl+Alt+Q
            SimulateKeyEvent(hook, (uint)Keys.Q, isDown: true, isSysKey: true);
            Assert.True(autoReset.WaitOne(1000), "Callback was not invoked for Quiet (0x00).");
            lock (triggeredModes)
            {
                Assert.Equal(2, triggeredModes.Count);
                Assert.Equal((byte)0x00, triggeredModes.Last());
            }
            SimulateKeyEvent(hook, (uint)Keys.Q, isDown: false, isSysKey: true);

            // 3. Test Balanced (0x01) via Ctrl+Alt+B
            SimulateKeyEvent(hook, (uint)Keys.B, isDown: true, isSysKey: true);
            Assert.True(autoReset.WaitOne(1000), "Callback was not invoked for Balanced (0x01).");
            lock (triggeredModes)
            {
                Assert.Equal(3, triggeredModes.Count);
                Assert.Equal((byte)0x01, triggeredModes.Last());
            }
            SimulateKeyEvent(hook, (uint)Keys.B, isDown: false, isSysKey: true);

            // 4. Test Performance (0x04) via Ctrl+Alt+P
            SimulateKeyEvent(hook, (uint)Keys.P, isDown: true, isSysKey: true);
            Assert.True(autoReset.WaitOne(1000), "Callback was not invoked for Performance (0x04).");
            lock (triggeredModes)
            {
                Assert.Equal(4, triggeredModes.Count);
                Assert.Equal((byte)0x04, triggeredModes.Last());
            }
            SimulateKeyEvent(hook, (uint)Keys.P, isDown: false, isSysKey: true);

            // 5. Test Turbo (0x05) via Ctrl+Alt+T
            SimulateKeyEvent(hook, (uint)Keys.T, isDown: true, isSysKey: true);
            Assert.True(autoReset.WaitOne(1000), "Callback was not invoked for Turbo (0x05).");
            lock (triggeredModes)
            {
                Assert.Equal(5, triggeredModes.Count);
                Assert.Equal((byte)0x05, triggeredModes.Last());
            }
            SimulateKeyEvent(hook, (uint)Keys.T, isDown: false, isSysKey: true);

            // Release modifiers
            SimulateKeyEvent(hook, VK_LMENU, isDown: false, isSysKey: true);
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: false);
        }

        [Fact]
        public void KeyboardHook_CustomModeShortcut_UnboundKey_DoesNotInvokeCallback()
        {
            var triggeredModes = new List<byte>();
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onCustomModeShortcutTriggered: mode => triggeredModes.Add(mode));

            hook.SetCustomShortcuts(new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true)
            });

            // Press Ctrl + Alt + Z (not bound)
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: true);
            SimulateKeyEvent(hook, VK_LMENU, isDown: true, isSysKey: true);
            SimulateKeyEvent(hook, (uint)Keys.Z, isDown: true, isSysKey: true);
            SimulateKeyEvent(hook, (uint)Keys.Z, isDown: false, isSysKey: true);
            SimulateKeyEvent(hook, VK_LMENU, isDown: false, isSysKey: true);
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: false);

            Thread.Sleep(50);
            Assert.Empty(triggeredModes);
        }

        [Fact]
        public void KeyboardHook_CustomModeShortcut_DisabledShortcut_DoesNotInvokeCallback()
        {
            var triggeredModes = new List<byte>();
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onCustomModeShortcutTriggered: mode => triggeredModes.Add(mode));

            hook.SetCustomShortcuts(new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true, enabled: false) // Disabled!
            });

            // Press Ctrl + Alt + E
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: true);
            SimulateKeyEvent(hook, VK_LMENU, isDown: true, isSysKey: true);
            SimulateKeyEvent(hook, (uint)Keys.E, isDown: true, isSysKey: true);
            SimulateKeyEvent(hook, (uint)Keys.E, isDown: false, isSysKey: true);
            SimulateKeyEvent(hook, VK_LMENU, isDown: false, isSysKey: true);
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: false);

            Thread.Sleep(50);
            Assert.Empty(triggeredModes);
        }

        [Fact]
        public void KeyboardHook_CustomModeShortcut_BareTypingWithoutModifiers_DoesNotInvokeCallback()
        {
            var triggeredModes = new List<byte>();
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onCustomModeShortcutTriggered: mode => triggeredModes.Add(mode));

            hook.SetCustomShortcuts(new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true)
            });

            // Simulate bare 'E' key press (no Ctrl or Alt held)
            SimulateKeyEvent(hook, (uint)Keys.E, isDown: true);
            SimulateKeyEvent(hook, (uint)Keys.E, isDown: false);

            Thread.Sleep(50);
            Assert.Empty(triggeredModes);
        }

        [Fact]
        public void KeyboardHook_CustomModeShortcut_InjectedKeystrokes_RejectedWhenAllowInjectedKeysFalse()
        {
            var triggeredModes = new List<byte>();
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onCustomModeShortcutTriggered: mode => triggeredModes.Add(mode))
            {
                AllowInjectedKeys = false
            };

            hook.SetCustomShortcuts(new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true)
            });

            // Press Ctrl and Alt
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: true);
            SimulateKeyEvent(hook, VK_LMENU, isDown: true, isSysKey: true);

            // Injected 'E' key press
            SimulateKeyEvent(hook, (uint)Keys.E, isDown: true, isSysKey: true, flags: LLKHF_INJECTED);
            SimulateKeyEvent(hook, (uint)Keys.E, isDown: false, isSysKey: true, flags: LLKHF_INJECTED);

            SimulateKeyEvent(hook, VK_LMENU, isDown: false, isSysKey: true);
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: false);

            Thread.Sleep(50);
            Assert.Empty(triggeredModes);
        }

        [Fact]
        public void KeyboardHook_CustomModeShortcut_InjectedKeystrokes_AcceptedWhenAllowInjectedKeysTrue()
        {
            var triggeredModes = new List<byte>();
            using var autoReset = new AutoResetEvent(false);

            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onCustomModeShortcutTriggered: mode =>
                {
                    lock (triggeredModes)
                    {
                        triggeredModes.Add(mode);
                    }
                    autoReset.Set();
                })
            {
                AllowInjectedKeys = true
            };

            hook.SetCustomShortcuts(new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true)
            });

            // Press Ctrl and Alt
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: true, flags: LLKHF_INJECTED);
            SimulateKeyEvent(hook, VK_LMENU, isDown: true, isSysKey: true, flags: LLKHF_INJECTED);

            // Injected 'E' key press
            SimulateKeyEvent(hook, (uint)Keys.E, isDown: true, isSysKey: true, flags: LLKHF_INJECTED);

            Assert.True(autoReset.WaitOne(1000), "Injected keystroke should trigger when AllowInjectedKeys=true.");
            lock (triggeredModes)
            {
                Assert.Single(triggeredModes);
                Assert.Equal((byte)0x06, triggeredModes[0]);
            }

            SimulateKeyEvent(hook, (uint)Keys.E, isDown: false, isSysKey: true, flags: LLKHF_INJECTED);
            SimulateKeyEvent(hook, VK_LMENU, isDown: false, isSysKey: true, flags: LLKHF_INJECTED);
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: false, flags: LLKHF_INJECTED);
        }

        #endregion
    }
}
