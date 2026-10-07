using System;
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
            cachedPidField!.SetValue(controller, 999999); // Dead/non-existent PID

            bool exitedFired = false;
            controller.GameExited += (snap) => exitedFired = true;

            controller.PollOnce();

            Assert.True(exitedFired);
            Assert.Null(controller.ActiveGameExe);
            // Cached PID must be reset to -1
            Assert.Equal(-1, (int)cachedPidField.GetValue(controller)!);
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
            structType.GetField("vkCode")!.SetValue(hookStruct, (uint)0x76);

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
            Assert.Equal(1, invokeCount);
        }

        #endregion
    }
}
