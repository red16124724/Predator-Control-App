using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class ModeShortcutValidatorTests
    {
        [Fact]
        public void DefaultShortcuts_AreAllValid_AndDistinct()
        {
            var shortcuts = ModeShortcutValidator.GetDefaultShortcuts();

            Assert.Equal(5, shortcuts.Count);
            Assert.True(ModeShortcutValidator.ValidateShortcutSet(shortcuts, out string failureReason), failureReason);

            // Verify expected modes and codes
            Assert.Contains(shortcuts, s => s.ModeName == "Eco" && s.ModeCode == 0x06 && s.Key == Keys.E && s.Ctrl && s.Alt && !s.Shift && !s.Win);
            Assert.Contains(shortcuts, s => s.ModeName == "Quiet" && s.ModeCode == 0x00 && s.Key == Keys.Q && s.Ctrl && s.Alt && !s.Shift && !s.Win);
            Assert.Contains(shortcuts, s => s.ModeName == "Balanced" && s.ModeCode == 0x01 && s.Key == Keys.B && s.Ctrl && s.Alt && !s.Shift && !s.Win);
            Assert.Contains(shortcuts, s => s.ModeName == "Performance" && s.ModeCode == 0x04 && s.Key == Keys.P && s.Ctrl && s.Alt && !s.Shift && !s.Win);
            Assert.Contains(shortcuts, s => s.ModeName == "Turbo" && s.ModeCode == 0x05 && s.Key == Keys.T && s.Ctrl && s.Alt && !s.Shift && !s.Win);

            // Verify ToString
            Assert.Equal("Ctrl+Alt+E", shortcuts.First(s => s.ModeName == "Eco").ToString());
            Assert.Equal("Ctrl+Alt+Q", shortcuts.First(s => s.ModeName == "Quiet").ToString());
            Assert.Equal("Ctrl+Alt+B", shortcuts.First(s => s.ModeName == "Balanced").ToString());
            Assert.Equal("Ctrl+Alt+P", shortcuts.First(s => s.ModeName == "Performance").ToString());
            Assert.Equal("Ctrl+Alt+T", shortcuts.First(s => s.ModeName == "Turbo").ToString());
        }

        [Fact]
        public void NumericDefaultShortcuts_AreAllValid_AndDistinct()
        {
            var shortcuts = ModeShortcutValidator.GetNumericDefaultShortcuts();

            Assert.Equal(5, shortcuts.Count);
            Assert.True(ModeShortcutValidator.ValidateShortcutSet(shortcuts, out string failureReason), failureReason);

            Assert.Contains(shortcuts, s => s.ModeName == "Quiet" && s.Key == Keys.D1 && s.ToString() == "Ctrl+Alt+1");
            Assert.Contains(shortcuts, s => s.ModeName == "Balanced" && s.Key == Keys.D2 && s.ToString() == "Ctrl+Alt+2");
            Assert.Contains(shortcuts, s => s.ModeName == "Performance" && s.Key == Keys.D3 && s.ToString() == "Ctrl+Alt+3");
            Assert.Contains(shortcuts, s => s.ModeName == "Turbo" && s.Key == Keys.D4 && s.ToString() == "Ctrl+Alt+4");
            Assert.Contains(shortcuts, s => s.ModeName == "Eco" && s.Key == Keys.D5 && s.ToString() == "Ctrl+Alt+5");
        }

        [Theory]
        [InlineData(Keys.F1)]
        [InlineData(Keys.F2)]
        [InlineData(Keys.F3)]
        [InlineData(Keys.F4)]
        [InlineData(Keys.F5)]
        [InlineData(Keys.F6)]
        [InlineData(Keys.F7)]
        [InlineData(Keys.F8)]
        [InlineData(Keys.F9)]
        [InlineData(Keys.F10)]
        [InlineData(Keys.F11)]
        [InlineData(Keys.F12)]
        [InlineData(Keys.F13)]
        [InlineData(Keys.F24)]
        public void FunctionKeys_AreForbidden_WithAnyOrNoModifiers(Keys fKey)
        {
            // Bare key
            Assert.False(ModeShortcutValidator.IsValid(fKey, false, false, false, false, out var err1));
            Assert.Contains("Function keys (F1-F24)", err1);

            // With Ctrl
            Assert.False(ModeShortcutValidator.IsValid(fKey, true, false, false, false, out var err2));
            Assert.Contains("Function keys (F1-F24)", err2);

            // With Alt
            Assert.False(ModeShortcutValidator.IsValid(fKey, false, true, false, false, out var err3));
            Assert.Contains("Function keys (F1-F24)", err3);

            // With Ctrl+Alt
            Assert.False(ModeShortcutValidator.IsValid(fKey, true, true, false, false, out var err4));
            Assert.Contains("Function keys (F1-F24)", err4);

            // With Ctrl+Alt+Shift
            Assert.False(ModeShortcutValidator.IsValid(fKey, true, true, true, false, out var err5));
            Assert.Contains("Function keys (F1-F24)", err5);
        }

        [Theory]
        [InlineData(Keys.L, false, false, false, true)] // Win+L
        [InlineData(Keys.D, false, false, false, true)] // Win+D
        [InlineData(Keys.E, false, false, false, true)] // Win+E
        [InlineData(Keys.R, false, false, false, true)] // Win+R
        [InlineData(Keys.X, false, false, false, true)] // Win+X
        [InlineData(Keys.S, false, false, false, true)] // Win+S
        [InlineData(Keys.A, false, false, false, true)] // Win+A
        [InlineData(Keys.V, false, false, false, true)] // Win+V
        [InlineData(Keys.P, false, false, false, true)] // Win+P
        [InlineData(Keys.I, false, false, false, true)] // Win+I
        [InlineData(Keys.Tab, false, false, false, true)] // Win+Tab
        [InlineData(Keys.S, false, false, true, true)] // Win+Shift+S
        [InlineData(Keys.LWin, false, false, false, false)] // Win key alone
        [InlineData(Keys.RWin, false, false, false, false)] // Win key alone
        public void WindowsKeyCombinations_AreForbidden(Keys key, bool ctrl, bool alt, bool shift, bool win)
        {
            Assert.False(ModeShortcutValidator.IsValid(key, ctrl, alt, shift, win, out var err));
            Assert.Contains("Windows key", err);
        }

        [Theory]
        [InlineData(Keys.Tab, false, true, false, false)] // Alt+Tab
        [InlineData(Keys.Tab, false, true, true, false)]  // Alt+Shift+Tab
        [InlineData(Keys.F4, false, true, false, false)]   // Alt+F4
        [InlineData(Keys.Escape, false, true, false, false)] // Alt+Esc
        [InlineData(Keys.Space, false, true, false, false)]  // Alt+Space
        [InlineData(Keys.Delete, true, true, false, false)]  // Ctrl+Alt+Del
        [InlineData(Keys.Escape, true, false, true, false)]  // Ctrl+Shift+Esc
        public void DefaultWindowsShortcuts_AreForbidden(Keys key, bool ctrl, bool alt, bool shift, bool win)
        {
            Assert.False(ModeShortcutValidator.IsValid(key, ctrl, alt, shift, win, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Theory]
        [InlineData(Keys.C)]
        [InlineData(Keys.V)]
        [InlineData(Keys.X)]
        [InlineData(Keys.Z)]
        [InlineData(Keys.Y)]
        [InlineData(Keys.A)]
        [InlineData(Keys.S)]
        [InlineData(Keys.P)]
        [InlineData(Keys.O)]
        [InlineData(Keys.N)]
        [InlineData(Keys.W)]
        [InlineData(Keys.F)]
        [InlineData(Keys.H)]
        [InlineData(Keys.T)]
        [InlineData(Keys.D)]
        [InlineData(Keys.R)]
        [InlineData(Keys.J)]
        [InlineData(Keys.Tab)]
        public void StandardWindowsEditingShortcuts_WithCtrlOnly_AreForbidden(Keys key)
        {
            // Ctrl without Alt
            Assert.False(ModeShortcutValidator.IsValid(key, ctrl: true, alt: false, shift: false, win: false, out var err1));
            Assert.False(string.IsNullOrEmpty(err1));

            // Ctrl+Shift without Alt (e.g. Ctrl+Shift+T, Ctrl+Shift+Z, Ctrl+Shift+Tab)
            Assert.False(ModeShortcutValidator.IsValid(key, ctrl: true, alt: false, shift: true, win: false, out var err2));
            Assert.False(string.IsNullOrEmpty(err2));
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
        public void SystemKeys_AreForbidden_UnderAnyCombination(Keys key)
        {
            Assert.False(ModeShortcutValidator.IsValid(key, false, false, false, false, out _));
            Assert.False(ModeShortcutValidator.IsValid(key, true, true, false, false, out _));
            Assert.False(ModeShortcutValidator.IsValid(key, true, false, true, false, out _));
        }

        [Theory]
        [InlineData(Keys.None, false, false, false, false)]
        [InlineData(Keys.ControlKey, false, false, false, false)]
        [InlineData(Keys.Menu, false, false, false, false)]
        [InlineData(Keys.ShiftKey, false, false, false, false)]
        [InlineData(Keys.A, false, false, false, false)] // Bare letter without modifier
        [InlineData(Keys.D1, false, false, false, false)] // Bare number without modifier
        public void ModifierOnly_AndBareKeys_AreForbidden(Keys key, bool ctrl, bool alt, bool shift, bool win)
        {
            Assert.False(ModeShortcutValidator.IsValid(key, ctrl, alt, shift, win, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Theory]
        [InlineData(Keys.E, true, true, false, false, "Ctrl+Alt+E")]
        [InlineData(Keys.Q, true, true, false, false, "Ctrl+Alt+Q")]
        [InlineData(Keys.B, true, true, false, false, "Ctrl+Alt+B")]
        [InlineData(Keys.P, true, true, false, false, "Ctrl+Alt+P")]
        [InlineData(Keys.T, true, true, false, false, "Ctrl+Alt+T")]
        [InlineData(Keys.D1, true, false, true, false, "Ctrl+Shift+1")]
        [InlineData(Keys.NumPad1, true, true, false, false, "Ctrl+Alt+NumPad 1")]
        public void ValidCustomCombinations_Pass(Keys key, bool ctrl, bool alt, bool shift, bool win, string expectedFormat)
        {
            Assert.True(ModeShortcutValidator.IsValid(key, ctrl, alt, shift, win, out string failureReason), failureReason);
            Assert.Equal(expectedFormat, ModeShortcutValidator.FormatShortcut(key, ctrl, alt, shift, win));
        }

        [Fact]
        public void ValidateShortcutSet_DetectsDuplicateShortcuts()
        {
            var shortcuts = new List<CustomModeShortcut>
            {
                new CustomModeShortcut("Eco", 0x06, Keys.E, ctrl: true, alt: true),
                new CustomModeShortcut("Quiet", 0x00, Keys.E, ctrl: true, alt: true) // Duplicate of Eco!
            };

            Assert.False(ModeShortcutValidator.ValidateShortcutSet(shortcuts, out var err));
            Assert.Contains("multiple modes", err);
        }

        [Fact]
        public void CustomModeShortcut_CloneAndEquality_OperateCorrectly()
        {
            var orig = new CustomModeShortcut("Eco", 0x06, Keys.E, ctrl: true, alt: true, shift: false, win: false);
            var clone = orig.Clone();

            Assert.Equal(orig, clone);
            Assert.True(orig == clone);
            Assert.False(orig != clone);
            Assert.Equal(orig.GetHashCode(), clone.GetHashCode());

            // Modify clone
            clone.Alt = false;
            Assert.NotEqual(orig, clone);
            Assert.True(orig != clone);
        }

        [Fact]
        public void TryFindMatchingMode_FindsConfiguredShortcut()
        {
            var shortcuts = ModeShortcutValidator.GetDefaultShortcuts();

            bool found = ModeShortcutValidator.TryFindMatchingMode(
                shortcuts,
                Keys.E,
                ctrl: true,
                alt: true,
                shift: false,
                win: false,
                out var matched);

            Assert.True(found);
            Assert.NotNull(matched);
            Assert.Equal("Eco", matched.ModeName);
            Assert.Equal(0x06, matched.ModeCode);
        }
    }
}
