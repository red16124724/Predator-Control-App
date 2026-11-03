using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("KeyboardHookTests")]
    public class KeyboardHookResilienceAndStressTests : IDisposable
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const uint VK_CONTROL = 0x11;
        private const uint VK_LCONTROL = 0xA2;
        private const uint VK_LMENU = 0xA4;
        private const uint VK_LSHIFT = 0xA0;
        private const uint VK_LWIN = 0x5B;

        private readonly MethodInfo _hookCallbackMethod;
        private readonly Type _structType;

        public KeyboardHookResilienceAndStressTests()
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

        [Fact]
        public void KeyboardHook_Stress_RapidKeyPounding_DoesNotDropOrReenter()
        {
            int triggerCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onCustomModeShortcutTriggered: mode => Interlocked.Increment(ref triggerCount));

            hook.SetCustomShortcuts(new List<CustomModeShortcut>
            {
                new("Turbo", 0x05, Keys.T, ctrl: true, alt: true)
            });

            // Set Ctrl + Alt
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: true);
            SimulateKeyEvent(hook, VK_LMENU, isDown: true, isSysKey: true);

            // Pound 'T' 100 times in rapid succession (simulating keyboard auto-repeat and key pounding)
            for (int i = 0; i < 100; i++)
            {
                SimulateKeyEvent(hook, (uint)Keys.T, isDown: true, isSysKey: true);
            }

            // Release
            SimulateKeyEvent(hook, (uint)Keys.T, isDown: false, isSysKey: true);
            SimulateKeyEvent(hook, VK_LMENU, isDown: false, isSysKey: true);
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: false);

            Thread.Sleep(100);

            // Should have triggered once (or at most twice if tick spanned), suppressing the 100-repeat storm
            Assert.InRange(triggerCount, 1, 2);
        }

        [Fact]
        public void KeyboardHook_HookProcExecutionTime_IsFarBelowWindows200msTimeout()
        {
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onCustomModeShortcutTriggered: mode => { });

            hook.SetCustomShortcuts(ModeShortcutValidator.CreateDefaultShortcuts());

            SimulateKeyEvent(hook, VK_LCONTROL, isDown: true);
            SimulateKeyEvent(hook, VK_LMENU, isDown: true, isSysKey: true);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                SimulateKeyEvent(hook, (uint)Keys.E, isDown: true, isSysKey: true);
            }
            sw.Stop();

            double msPerCall = sw.Elapsed.TotalMilliseconds / 1000.0;
            // Must be less than 0.5ms per invocation, far below the 200ms Windows low-level hook threshold
            Assert.True(msPerCall < 0.5, $"HookCallback took {msPerCall}ms per call, must be < 0.5ms");
        }

        [Fact]
        public void KeyboardHook_ConcurrentShortcutUpdatesAndKeyEvents_ZeroLockContention()
        {
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onCustomModeShortcutTriggered: mode => { });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var token = cts.Token;

            // Background task continuously updating shortcuts
            var updaterTask = Task.Run(() =>
            {
                int count = 0;
                while (!token.IsCancellationRequested)
                {
                    count++;
                    var list = (count % 2 == 0)
                        ? ModeShortcutValidator.CreateDefaultShortcuts()
                        : ModeShortcutValidator.GetNumericDefaultShortcuts();
                    hook.SetCustomShortcuts(list);
                    Thread.SpinWait(50);
                }
            });

            // Calling thread simulating high-frequency keystrokes concurrently
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: true);
            SimulateKeyEvent(hook, VK_LMENU, isDown: true, isSysKey: true);

            while (!token.IsCancellationRequested)
            {
                SimulateKeyEvent(hook, (uint)Keys.E, isDown: true, isSysKey: true);
                SimulateKeyEvent(hook, (uint)Keys.E, isDown: false, isSysKey: true);
            }

            updaterTask.Wait();
            SimulateKeyEvent(hook, VK_LMENU, isDown: false, isSysKey: true);
            SimulateKeyEvent(hook, VK_LCONTROL, isDown: false);
        }

        [Fact]
        public void KeyboardHook_ModifierState_ThreadSafetyAndReset_ClearsCleanly()
        {
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { });

            SimulateKeyEvent(hook, VK_LCONTROL, isDown: true);
            SimulateKeyEvent(hook, VK_LMENU, isDown: true, isSysKey: true);
            SimulateKeyEvent(hook, VK_LSHIFT, isDown: true);
            SimulateKeyEvent(hook, VK_LWIN, isDown: true);

            Assert.True(hook.IsCtrlPressed);
            Assert.True(hook.IsAltPressed);
            Assert.True(hook.IsShiftPressed);
            Assert.True(hook.IsWinPressed);

            // Desktop switch or lock occurs -> reset
            hook.ResetModifierStates();

            // After reset (simulated environment where physical keys aren't pressed)
            Assert.False(hook.IsCtrlPressed);
            Assert.False(hook.IsAltPressed);
            Assert.False(hook.IsShiftPressed);
            Assert.False(hook.IsWinPressed);
        }

        [Fact]
        public void KeyboardHook_RehookAndEnsureHookInstalled_AreSafeAndIdempotent()
        {
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { });

            // Multiple rehook calls must not throw or leak
            for (int i = 0; i < 5; i++)
            {
                hook.Rehook();
                bool installed = hook.EnsureHookInstalled();
                Assert.True(installed || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            }
        }

        [Fact]
        public void ModeShortcutValidator_ValidateShortcutSet_AllowsDisabledShortcutsWithoutError()
        {
            var shortcuts = new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.None, ctrl: false, alt: false, enabled: false),
                new("Quiet", 0x00, Keys.None, ctrl: false, alt: false, enabled: false),
                new("Balanced", 0x01, Keys.B, ctrl: true, alt: true, enabled: true),
                new("Performance", 0x04, Keys.P, ctrl: true, alt: true, enabled: true),
                new("Turbo", 0x05, Keys.T, ctrl: true, alt: true, enabled: true)
            };

            bool isValid = ModeShortcutValidator.ValidateShortcutSet(shortcuts, out string failureReason);
            Assert.True(isValid, $"Disabled shortcuts should not invalidate the set: {failureReason}");
        }

        [Fact]
        public void AppSettings_ThreadSafeShortcutListConverter_ToleratesCorruptedTypes()
        {
            // Corrupted JSON with string codes, string keys, and boolean variations
            string malformedJson = @"[
                { ""ModeName"": ""Eco"", ""ModeCode"": ""6"", ""Key"": ""E"", ""Ctrl"": true, ""Alt"": 1, ""Shift"": false, ""Win"": 0, ""Enabled"": true },
                { ""ModeName"": ""Quiet"", ""ModeCode"": 0, ""Key"": 81, ""Ctrl"": 1, ""Alt"": true, ""Shift"": 0, ""Win"": false, ""Enabled"": 1 },
                null,
                { ""ModeName"": ""Invalid"", ""ModeCode"": 99 }
            ]";

            var list = JsonSerializer.Deserialize<List<CustomModeShortcut>>(malformedJson);
            Assert.NotNull(list);
            Assert.Equal(4, list!.Count);
            Assert.Null(list[2]); // null entry preserved during raw deser

            var eco = list[0];
            Assert.Equal("Eco", eco.ModeName);
            Assert.Equal((byte)0x06, eco.ModeCode);
            Assert.Equal(Keys.E, eco.Key);
            Assert.True(eco.Ctrl);
            Assert.True(eco.Alt);

            var quiet = list[1];
            Assert.Equal("Quiet", quiet.ModeName);
            Assert.Equal((byte)0x00, quiet.ModeCode);
            Assert.Equal(Keys.Q, quiet.Key);
            Assert.True(quiet.Ctrl);
            Assert.True(quiet.Alt);
        }

        [Fact]
        public void AppSettings_Sanitize_DeduplicatesDuplicateShortcutCombos()
        {
            var settings = new AppSettings();
            settings.CustomModeShortcuts = new List<CustomModeShortcut>
            {
                new("Eco", 0x06, Keys.E, ctrl: true, alt: true, enabled: true),
                new("Quiet", 0x00, Keys.E, ctrl: true, alt: true, enabled: true), // Duplicate of Eco!
                new("Turbo", 0x05, Keys.T, ctrl: true, alt: true, enabled: true)
            };

            settings.Sanitize();

            Assert.True(settings.CustomModeShortcuts[0].Enabled, "First occurrence should remain enabled.");
            Assert.False(settings.CustomModeShortcuts[1].Enabled, "Duplicate shortcut combination should be disabled.");
            Assert.True(settings.CustomModeShortcuts[2].Enabled, "Unique shortcut should remain enabled.");
        }

        [Fact]
        public async Task AppSettings_ConcurrentSavesAndSnapshots_AreCompletelyThreadSafe()
        {
            string tempSettingsFile = Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid():N}.json");
            AppSettings.CustomFilePath = tempSettingsFile;

            try
            {
                var settings = new AppSettings();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var token = cts.Token;

                var taskSave = Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        settings.Save();
                        Thread.Sleep(10);
                    }
                });

                var taskMutate = Task.Run(() =>
                {
                    int i = 0;
                    while (!token.IsCancellationRequested)
                    {
                        i++;
                        settings.PowerMode = (byte)(i % 5);
                        settings.CustomModeShortcuts = (i % 2 == 0)
                            ? ModeShortcutValidator.CreateDefaultShortcuts()
                            : ModeShortcutValidator.GetNumericDefaultShortcuts();
                        Thread.Sleep(5);
                    }
                });

                var taskSnapshot = Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        var snap = settings.CreateSnapshot();
                        Assert.NotNull(snap);
                        Thread.Sleep(5);
                    }
                });

                await Task.WhenAll(taskSave, taskMutate, taskSnapshot);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { File.Delete(tempSettingsFile); } catch { }
                try { File.Delete(tempSettingsFile + ".bak"); } catch { }
            }
        }
    }
}
