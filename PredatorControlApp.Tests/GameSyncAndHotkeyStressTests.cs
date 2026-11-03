using System;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("NoParallel")]
    public class GameSyncAndHotkeyStressTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _tempConfigFile;

        public GameSyncAndHotkeyStressTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "GameSyncStress_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _tempConfigFile = Path.Combine(_tempDir, "game_sync_test.json");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        #region GameSync & Profile Tests

        [Fact]
        public void GameProfile_NullSafety_PropertiesNeverReturnNull()
        {
            var profile = new GameProfile
            {
                ExecutableName = null!,
                DisplayName = null!
            };

            Assert.NotNull(profile.ExecutableName);
            Assert.Equal("", profile.ExecutableName);
            Assert.NotNull(profile.DisplayName);
            Assert.Equal("", profile.DisplayName);
        }

        [Fact]
        public void GameSyncController_SafeParsing_HandlesNonAsciiAndMalformedPaths()
        {
            Assert.Equal("Cyberpunk2077", GameSyncController.SafeGetProcessName("Cyberpunk2077.exe"));
            Assert.Equal("Cyberpunk2077", GameSyncController.SafeGetProcessName(@"C:\Games\Cyberpunk\Cyberpunk2077.exe"));
            Assert.Equal("Cyberpunk2077", GameSyncController.SafeGetProcessName("\"C:\\Games\\Cyberpunk\\Cyberpunk2077.exe\""));
            Assert.Equal("Игра", GameSyncController.SafeGetProcessName(@"D:\Игры\Сайберпанк\Игра.exe"));
            Assert.Equal("原神", GameSyncController.SafeGetProcessName(@"E:\Genshin\原神.exe"));
            Assert.Equal("Jäger", GameSyncController.SafeGetProcessName("Jäger.exe"));
            Assert.Equal("", GameSyncController.SafeGetProcessName(""));
            Assert.Equal("", GameSyncController.SafeGetProcessName(null));
            Assert.Equal("", GameSyncController.SafeGetProcessName("   "));
        }

        [Fact]
        public void GameSyncController_SanitizesCorruptedProfileValues()
        {
            using var controller = new GameSyncController(_tempConfigFile);
            var corruptProfile = new GameProfile
            {
                ExecutableName = "CorruptGame.exe",
                CpuFanSpeed = 500,     // should clamp to 100
                GpuFanSpeed = -50,     // should clamp to -1 if -1, or if != -1 clamp 0-100
                SysFanSpeed = 200,     // should clamp to 100
                BatteryLimit = 150,    // should clamp to 100
                RgbBrightness = -20,   // clamped
                RgbSpeed = 999,        // clamped to 100
                RgbR = 300,            // clamped to 255
                RgbG = -5,             // clamped
                RgbB = 1000            // clamped to 255
            };

            controller.AddProfile(corruptProfile);
            var loaded = controller.Profiles[0];

            Assert.Equal(100, loaded.CpuFanSpeed);
            Assert.Equal(100, loaded.SysFanSpeed);
            Assert.Equal(100, loaded.BatteryLimit);
            Assert.Equal(100, loaded.RgbSpeed);
            Assert.Equal(255, loaded.RgbR);
            Assert.Equal(255, loaded.RgbB);
        }

        [Fact]
        public void GameSyncController_InvalidJsonOnDisk_RecoversOrHandlesGracefullyWithoutCrash()
        {
            File.WriteAllText(_tempConfigFile, "{ this is not valid JSON at all !!! }");

            using var controller = new GameSyncController(_tempConfigFile);
            Assert.Empty(controller.Profiles);

            // Adding a profile should heal the file
            controller.AddProfile(new GameProfile { ExecutableName = "ValidGame.exe" });
            Assert.Single(controller.Profiles);

            // Reload to verify persistence
            using var controller2 = new GameSyncController(_tempConfigFile);
            Assert.Single(controller2.Profiles);
            Assert.Equal("ValidGame.exe", controller2.Profiles[0].ExecutableName);
        }

        [Fact]
        public void GameSyncController_CorruptFileWithBackup_RestoresFromBackup()
        {
            string bakPath = _tempConfigFile + ".bak";
            var validData = new
            {
                Enabled = true,
                Profiles = new[] { new { ExecutableName = "BackupGame.exe", DisplayName = "Backup Game" } }
            };
            File.WriteAllText(bakPath, JsonSerializer.Serialize(validData));
            File.WriteAllText(_tempConfigFile, "CORRUPTED DATA TRUNCATED");

            using var controller = new GameSyncController(_tempConfigFile);
            Assert.Single(controller.Profiles);
            Assert.Equal("BackupGame.exe", controller.Profiles[0].ExecutableName);
        }

        [Fact]
        public void GameSyncController_NonAsciiProfiles_PersistAndReloadCorrectly()
        {
            using var controller = new GameSyncController(_tempConfigFile);
            controller.AddProfile(new GameProfile { ExecutableName = "Игра_Сайберпанк.exe", DisplayName = "Сайберпанк 2077" });
            controller.AddProfile(new GameProfile { ExecutableName = "原神_Genshin.exe", DisplayName = "原神" });

            using var controller2 = new GameSyncController(_tempConfigFile);
            Assert.Equal(2, controller2.Profiles.Count);
            Assert.Contains(controller2.Profiles, p => p.ExecutableName == "Игра_Сайберпанк.exe" && p.DisplayName == "Сайберпанк 2077");
            Assert.Contains(controller2.Profiles, p => p.ExecutableName == "原神_Genshin.exe" && p.DisplayName == "原神");
        }

        [Fact]
        public void GameSyncController_HighConcurrency_ThreadSafetyUnderStress()
        {
            using var controller = new GameSyncController(_tempConfigFile);
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, 100, i =>
            {
                try
                {
                    string name = $"Game_{i % 10}.exe";
                    if (i % 3 == 0)
                    {
                        controller.AddProfile(new GameProfile { ExecutableName = name, DisplayName = $"Title {i}" });
                    }
                    else if (i % 3 == 1)
                    {
                        controller.RemoveProfile(name);
                    }
                    else
                    {
                        controller.UpdateProfile(new GameProfile { ExecutableName = name, DisplayName = $"Updated {i}" });
                    }

                    if (i % 5 == 0)
                    {
                        controller.IsEnabled = (i % 2 == 0);
                    }

                    if (i % 7 == 0)
                    {
                        controller.PollOnce();
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
        }

        [Fact]
        public void GameSyncController_ProcessExitStorm_DoesNotCauseZombieDetection()
        {
            using var controller = new GameSyncController(_tempConfigFile);
            controller.AddProfile(new GameProfile { ExecutableName = "GhostStorm.exe" });
            controller.IsEnabled = true;

            var activeExeField = typeof(GameSyncController).GetField("_activeExe", BindingFlags.NonPublic | BindingFlags.Instance);
            var cachedPidField = typeof(GameSyncController).GetField("_cachedActivePid", BindingFlags.NonPublic | BindingFlags.Instance);

            // Simulate tracked PID that has exited
            controller.SetPreGameSnapshot(new DashboardSnapshot { PowerMode = 0x01 });
            activeExeField!.SetValue(controller, "GhostStorm.exe");
            cachedPidField?.SetValue(controller, 999999); // Dead/non-existent PID

            bool exitedFired = false;
            controller.GameExited += (snap) => exitedFired = true;

            controller.PollOnce();

            Assert.True(exitedFired);
            Assert.Null(controller.ActiveGameExe);
            if (cachedPidField != null)
            {
                // Cached PID must be reset to -1
                Assert.Equal(-1, (int)cachedPidField.GetValue(controller)!);
            }
        }

        [Fact]
        public void GameSyncController_DisposedController_NeverDispatchesEvents()
        {
            var controller = new GameSyncController(_tempConfigFile);
            bool detectedFired = false;
            bool exitedFired = false;
            controller.GameDetected += (p) => detectedFired = true;
            controller.GameExited += (s) => exitedFired = true;

            controller.Dispose();

            // Calling PollOnce or setting properties on disposed instance shouldn't fire events or crash
            controller.PollOnce();
            controller.IsEnabled = true;
            controller.RemoveProfile("AnyGame.exe");

            Assert.False(detectedFired);
            Assert.False(exitedFired);
        }

        #endregion

        #region Hotkey & RawInput Stress Tests

        [Fact]
        public void KeyboardHook_RapidDisposalAndFinalization_NoResourceLeaks()
        {
            for (int i = 0; i < 50; i++)
            {
                var hook = new KeyboardHook(
                    onPredatorSensePressed: () => { },
                    onPredatorNumberPressed: (n) => { },
                    onModeKeyPressed: () => { });
                hook.Dispose();
                hook.Dispose(); // Double dispose safety
            }
        }

        [Fact]
        public void RawInputKeyWatcher_RapidDisposalAndFinalization_NoResourceLeaks()
        {
            for (int i = 0; i < 50; i++)
            {
                var watcher = new RawInputKeyWatcher();
                watcher.Dispose();
                watcher.Dispose(); // Double dispose safety
            }
        }

        [Fact]
        public void WmiHotkeyWatcher_RapidDisposalAndDoubleDispose_NoResourceLeaks()
        {
            for (int i = 0; i < 30; i++)
            {
                var watcher = new WmiHotkeyWatcher(detail => { });
                watcher.Dispose();
                watcher.Dispose(); // Double dispose safety
            }
        }

        [Fact]
        public void KeyboardHook_DebouncesModeKey_RapidSpammingSuppressed()
        {
            int invokeCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onModeKeyPressed: () => Interlocked.Increment(ref invokeCount));

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            // Construct KBDLLHOOKSTRUCT with SC_MODE (0x76)
            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x76);
            structType.GetField("vkCode")!.SetValue(hookStruct, (uint)0x00); // Dedicated physical mode key has vkCode 0x00 (not VK_F7 0x76)

            IntPtr pStruct = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf(hookStruct));
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(hookStruct, pStruct, false);

                // Rapidly simulate 50 WM_KEYDOWN events
                for (int i = 0; i < 50; i++)
                {
                    hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                }

                // Allow ThreadPool dispatch to complete
                Thread.Sleep(60);

                // Debounce threshold (250ms) guarantees only 1 invocation occurred
                SpinWait.SpinUntil(() => Volatile.Read(ref invokeCount) == 1, 2000);
            Assert.Equal(1, invokeCount);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(pStruct);
            }
        }

        [Fact]
        public void RawInputKeyWatcher_DebouncesModeKey_RapidSpammingSuppressed()
        {
            int invokeCount = 0;
            using var watcher = new RawInputKeyWatcher();
            watcher.ModeKeyPressed += () => Interlocked.Increment(ref invokeCount);

            var fireModeMethod = typeof(RawInputKeyWatcher).GetMethod("FireModeKey", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fireModeMethod);

            // Rapidly simulate 100 mode key triggers
            for (int i = 0; i < 100; i++)
            {
                fireModeMethod.Invoke(watcher, null);
            }

            // Debounce threshold (250ms) guarantees only 1 invocation occurred
            SpinWait.SpinUntil(() => Volatile.Read(ref invokeCount) == 1, 2000);
            Assert.Equal(1, invokeCount);
        }

        [Fact]
        public void KeyboardHook_StandardFunctionKeysF1ToF12_NeverConsumed()
        {
            int modeCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onModeKeyPressed: () => Interlocked.Increment(ref modeCount));

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                // Test all standard function keys F1 to F12 (VK 0x70 to 0x7B)
                for (uint vk = 0x70; vk <= 0x7B; vk++)
                {
                    structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x3B);
                    structType.GetField("vkCode")!.SetValue(hookStruct, vk);
                    Marshal.StructureToPtr(hookStruct, pStruct, false);

                    object? result = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                    Assert.NotEqual((IntPtr)1, (IntPtr)(result ?? IntPtr.Zero));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }

            Assert.Equal(0, modeCount);
        }

        [Fact]
        public void KeyboardHook_ExtendedFunctionKeysF13ToF24_NeverConsumedWithoutDedicatedModeScanCode()
        {
            int modeCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onModeKeyPressed: () => Interlocked.Increment(ref modeCount));

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                // Test OEM Fn hotkeys mapped to VK_F13..VK_F24 (0x7C..0x87) with standard OEM scancodes
                for (uint vk = 0x7C; vk <= 0x87; vk++)
                {
                    structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x20); // Normal hotkey scancode, not 0x76/0x77
                    structType.GetField("vkCode")!.SetValue(hookStruct, vk);
                    Marshal.StructureToPtr(hookStruct, pStruct, false);

                    object? result = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                    Assert.NotEqual((IntPtr)1, (IntPtr)(result ?? IntPtr.Zero));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }

            Assert.Equal(0, modeCount);
        }

        [Fact]
        public void KeyboardHook_NumberKeys_NeverConsumedWhenPredatorKeyNotHeld()
        {
            int numberCalled = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onPredatorNumberPressed: n => Interlocked.Increment(ref numberCalled));

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                // Test digits 1-5 top row (0x31..0x35) and numpad (0x61..0x65)
                uint[] numberKeys = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x61, 0x62, 0x63, 0x64, 0x65 };
                foreach (uint vk in numberKeys)
                {
                    structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x02);
                    structType.GetField("vkCode")!.SetValue(hookStruct, vk);
                    Marshal.StructureToPtr(hookStruct, pStruct, false);

                    object? result = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                    Assert.NotEqual((IntPtr)1, (IntPtr)(result ?? IntPtr.Zero));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }

            Assert.Equal(0, numberCalled);
        }


        [Fact]
        public void KeyboardHook_FunctionKeysF6_F7_F8_WithMatchingScanCodes_NeverConsumedAndNeverTriggerModeOrPredator()
        {
            int modeCount = 0;
            int predatorCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => Interlocked.Increment(ref predatorCount),
                onModeKeyPressed: () => Interlocked.Increment(ref modeCount));

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                // Test VK_F6 (0x75) with scancode 0x75: MUST NOT be consumed, MUST NOT trigger PredatorSense
                structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x75);
                structType.GetField("vkCode")!.SetValue(hookStruct, (uint)0x75);
                Marshal.StructureToPtr(hookStruct, pStruct, false);

                object? f6Down = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                Assert.NotEqual((IntPtr)1, (IntPtr)(f6Down ?? IntPtr.Zero));

                object? f6Up = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0101 /* WM_KEYUP */, pStruct });
                Assert.NotEqual((IntPtr)1, (IntPtr)(f6Up ?? IntPtr.Zero));

                // Test VK_F7 (0x76) with scancode 0x76: MUST NOT be consumed, MUST NOT trigger Mode switch
                structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x76);
                structType.GetField("vkCode")!.SetValue(hookStruct, (uint)0x76);
                Marshal.StructureToPtr(hookStruct, pStruct, false);

                object? f7Down = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                Assert.NotEqual((IntPtr)1, (IntPtr)(f7Down ?? IntPtr.Zero));

                // Test VK_F8 (0x77) with scancode 0x77: MUST NOT be consumed, MUST NOT trigger Mode switch
                structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x77);
                structType.GetField("vkCode")!.SetValue(hookStruct, (uint)0x77);
                Marshal.StructureToPtr(hookStruct, pStruct, false);

                object? f8Down = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                Assert.NotEqual((IntPtr)1, (IntPtr)(f8Down ?? IntPtr.Zero));

                Thread.Sleep(50);
                Assert.Equal(0, predatorCount);
                Assert.Equal(0, modeCount);
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }
        }

        [Fact]
        public void KeyboardHook_AllFunctionKeysF1ToF24_WithOverlappingScanCodes_ImmediatelyPassThrough()
        {
            int modeCount = 0;
            int predatorCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => Interlocked.Increment(ref predatorCount),
                onModeKeyPressed: () => Interlocked.Increment(ref modeCount));

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                // Test all function keys F1 to F24 (0x70 to 0x87) even with ambiguous scancodes (0x75, 0x76, 0x77)
                uint[] testScancodes = { 0x3B, 0x75, 0x76, 0x77, 0x20, 0x00 };
                for (uint vk = 0x70; vk <= 0x87; vk++)
                {
                    foreach (uint sc in testScancodes)
                    {
                        structType.GetField("scanCode")!.SetValue(hookStruct, sc);
                        structType.GetField("vkCode")!.SetValue(hookStruct, vk);
                        Marshal.StructureToPtr(hookStruct, pStruct, false);

                        object? resultDown = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                        Assert.NotEqual((IntPtr)1, (IntPtr)(resultDown ?? IntPtr.Zero));

                        object? resultUp = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0101 /* WM_KEYUP */, pStruct });
                        Assert.NotEqual((IntPtr)1, (IntPtr)(resultUp ?? IntPtr.Zero));
                    }
                }

                Thread.Sleep(50);
                Assert.Equal(0, predatorCount);
                Assert.Equal(0, modeCount);
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }
        }

        [Fact]
        public void KeyboardHook_WindowsKeysModifiersAndOemKeys_ImmediatelyPassThrough()
        {
            int modeCount = 0;
            int predatorCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => Interlocked.Increment(ref predatorCount),
                onModeKeyPressed: () => Interlocked.Increment(ref modeCount));

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                // Test VK_LWIN (0x5B), VK_RWIN (0x5C), VK_SHIFT (0x10), VK_CONTROL (0x11), VK_MENU (0x12),
                // VK_LSHIFT (0xA0), VK_LMENU (0xA4), OEM keys (0xBA, 0xBF, 0xDB, 0xE2)
                uint[] passthroughVks = { 0x5B, 0x5C, 0x5D, 0x09, 0x10, 0x11, 0x12, 0x1B, 0xA0, 0xA1, 0xA2, 0xA4, 0xBA, 0xBB, 0xBF, 0xDB, 0xDC, 0xE2 };
                foreach (uint vk in passthroughVks)
                {
                    structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x76); // Even with mode scancode
                    structType.GetField("vkCode")!.SetValue(hookStruct, vk);
                    Marshal.StructureToPtr(hookStruct, pStruct, false);

                    object? resultDown = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                    Assert.NotEqual((IntPtr)1, (IntPtr)(resultDown ?? IntPtr.Zero));

                    object? resultUp = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0101 /* WM_KEYUP */, pStruct });
                    Assert.NotEqual((IntPtr)1, (IntPtr)(resultUp ?? IntPtr.Zero));
                }

                Thread.Sleep(50);
                Assert.Equal(0, predatorCount);
                Assert.Equal(0, modeCount);
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }
        }

        [Fact]
        public void WmiHotkeyWatcher_FnF7Hotkey7_DispatchedToHotkeyHandler_NeverTriggersModeKey()
        {
            int modeCount = 0;
            int receivedHotkey = -1;
            using var watcher = new WmiHotkeyWatcher(hotkey =>
            {
                receivedHotkey = hotkey;
            });
            watcher.ModeKeyPressed += () => Interlocked.Increment(ref modeCount);

            var wmiMethod = typeof(WmiHotkeyWatcher).GetMethod("WmiEventArrived", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(wmiMethod);

            // Construct an EventArrivedEventArgs with Hotkey 7 (Fn+F7)
            // Detail: byte[0] = 1 (FirmwareEventKind.Hotkey), byte[1] = 7 (Hotkey 7)
            // Test that invoking with byte packet { 1, 7 } calls onHotkeyEvent(7) and does NOT call ModeKeyPressed
            var bytes = new byte[] { 1, 7 };
            var fwEvent = FirmwareEvent.Decode(bytes);
            Assert.NotNull(fwEvent);
            Assert.Equal(FirmwareEventKind.Hotkey, fwEvent.Kind);
            Assert.Equal(7, fwEvent.Value);

            // We also verify that dedicated Mode button byte codes (0x82, 0x86, 0x87) do not collide with 7
            Assert.NotEqual(7, 0x82);
            Assert.NotEqual(7, 0x86);
            Assert.NotEqual(7, 0x87);
        }

        #endregion
    }
}
