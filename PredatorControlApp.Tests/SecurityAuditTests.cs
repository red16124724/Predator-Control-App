using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using Microsoft.Win32;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class SecurityAuditTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _testJsonPath;

        public SecurityAuditTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "SecurityAudit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _testJsonPath = Path.Combine(_tempDir, "games.json");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, true);
            }
            catch { }
        }

        #region GameProfile Security & Validation Tests

        [Theory]
        [InlineData("../evil.exe")]
        [InlineData("..\\evil.exe")]
        [InlineData("sub/folder/game.exe")]
        [InlineData("sub\\folder\\game.exe")]
        [InlineData("game?.exe")]
        [InlineData("game*.exe")]
        [InlineData("game|.exe")]
        [InlineData("")]
        [InlineData("   ")]
        public void GameProfile_Validate_RejectsPathTraversalAndInvalidFilenames(string invalidExe)
        {
            var profile = new GameProfile { ExecutableName = invalidExe };
            bool valid = profile.Validate(out string error);
            Assert.False(valid);
            Assert.NotEmpty(error);
        }

        [Theory]
        [InlineData("svchost.exe")]
        [InlineData("cmd.exe")]
        [InlineData("powershell.exe")]
        [InlineData("pwsh.exe")]
        [InlineData("explorer.exe")]
        [InlineData("taskmgr.exe")]
        [InlineData("csrss.exe")]
        [InlineData("services.exe")]
        [InlineData("lsass.exe")]
        [InlineData("PredatorControlApp.exe")]
        public void GameProfile_Validate_RejectsBlockedSystemExecutables(string blockedExe)
        {
            var profile = new GameProfile { ExecutableName = blockedExe };
            bool valid = profile.Validate(out string error);
            Assert.False(valid);
            Assert.Contains("prohibited", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GameProfile_Validate_RejectsExecutablePathInSystemDirectory()
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sys32Exe = Path.Combine(winDir, "System32", "notepad.exe");

            var profile = new GameProfile
            {
                ExecutableName = "notepad.exe",
                ExecutablePath = sys32Exe
            };

            bool valid = profile.Validate(out string error);
            Assert.False(valid);
            Assert.Contains("system", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GameProfile_Validate_RejectsExecutablePathWithTraversal()
        {
            var profile = new GameProfile
            {
                ExecutableName = "game.exe",
                ExecutablePath = @"C:\Games\..\Windows\System32\evil.exe"
            };

            bool valid = profile.Validate(out string error);
            Assert.False(valid);
            Assert.Contains("traversal", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GameProfile_Sanitize_ClampsHardwareValues()
        {
            var profile = new GameProfile
            {
                ExecutableName = @"C:\Games\Doom.exe",
                DisplayName = "Doom\0\r\nSpecial\u0001",
                CpuFanSpeed = 250,
                GpuFanSpeed = -99,
                SysFanSpeed = 500,
                BatteryLimit = 200,
                RgbSpeed = 999,
                RgbBrightness = 150,
                RgbR = 999,
                RgbG = -10,
                RgbB = 300
            };

            profile.Sanitize();

            Assert.Equal("Doom.exe", profile.ExecutableName);
            Assert.Equal("DoomSpecial", profile.DisplayName);
            Assert.Equal(100, profile.CpuFanSpeed);
            Assert.Equal(-1, profile.GpuFanSpeed);
            Assert.Equal(100, profile.SysFanSpeed);
            Assert.Equal(100, profile.BatteryLimit);
            Assert.Equal(100, profile.RgbSpeed);
            Assert.Equal(100, profile.RgbBrightness);
            Assert.Equal(255, profile.RgbR);
            Assert.Equal(-1, profile.RgbG);
            Assert.Equal(255, profile.RgbB);
        }

        #endregion

        #region GameSync Export / Import Security Tests

        [Fact]
        public void GameSyncController_ExportProfiles_RejectsPathTraversal()
        {
            using var controller = new GameSyncController(_testJsonPath);
            controller.AddProfile(new GameProfile { ExecutableName = "ValidGame.exe" });

            string maliciousExport = Path.Combine(_tempDir, "..", "..", "malicious_export.json");
            Assert.Throws<SecurityException>(() => controller.ExportProfiles(maliciousExport));
        }

        [Theory]
        [InlineData("export.exe")]
        [InlineData("export.bat")]
        [InlineData("export.cmd")]
        [InlineData("export.ps1")]
        [InlineData("export.dll")]
        public void GameSyncController_ExportProfiles_RejectsNonJsonExtension(string invalidExt)
        {
            using var controller = new GameSyncController(_testJsonPath);
            string target = Path.Combine(_tempDir, invalidExt);
            Assert.Throws<ArgumentException>(() => controller.ExportProfiles(target));
        }

        [Fact]
        public void GameSyncController_ImportProfiles_RejectsNonJsonExtension()
        {
            using var controller = new GameSyncController(_testJsonPath);
            string badFile = Path.Combine(_tempDir, "test.txt");
            File.WriteAllText(badFile, "dummy");

            Assert.Throws<ArgumentException>(() => controller.ImportProfiles(badFile));
        }

        [Fact]
        public void GameSyncController_ImportProfiles_RejectsOversizedFile()
        {
            using var controller = new GameSyncController(_testJsonPath);
            string hugeFile = Path.Combine(_tempDir, "huge.json");

            // Create 6MB file (> 5MB limit)
            byte[] buffer = new byte[1024 * 1024];
            using (var fs = File.Create(hugeFile))
            {
                for (int i = 0; i < 6; i++)
                    fs.Write(buffer, 0, buffer.Length);
            }

            Assert.Throws<InvalidDataException>(() => controller.ImportProfiles(hugeFile));
        }

        [Fact]
        public void GameSyncController_ImportProfiles_DiscardsMaliciousProfiles()
        {
            using var controller = new GameSyncController(_testJsonPath);
            string json = """
            {
                "Enabled": true,
                "Profiles": [
                    { "ExecutableName": "GoodGame.exe", "DisplayName": "Good Game", "CpuFanSpeed": 80 },
                    { "ExecutableName": "../evil.exe", "DisplayName": "Traversal" },
                    { "ExecutableName": "cmd.exe", "DisplayName": "Blocked System" },
                    { "ExecutableName": "powershell.exe", "DisplayName": "Blocked System 2" },
                    { "ExecutableName": "AnotherGood.exe", "DisplayName": "Second Good", "CpuFanSpeed": 999 }
                ]
            }
            """;

            string importPath = Path.Combine(_tempDir, "import_test.json");
            File.WriteAllText(importPath, json);

            int count = controller.ImportProfiles(importPath, merge: false);

            Assert.Equal(2, count);
            Assert.Equal(2, controller.Profiles.Count);
            Assert.Contains(controller.Profiles, p => p.ExecutableName == "GoodGame.exe");
            Assert.Contains(controller.Profiles, p => p.ExecutableName == "AnotherGood.exe" && p.CpuFanSpeed == 100);
            Assert.DoesNotContain(controller.Profiles, p => p.ExecutableName == "cmd.exe");
            Assert.DoesNotContain(controller.Profiles, p => p.ExecutableName == "powershell.exe");
            Assert.DoesNotContain(controller.Profiles, p => p.ExecutableName.Contains("evil"));
        }

        [Fact]
        public void GameSyncController_ExportAndImport_RoundTripSucceeds()
        {
            using var controller = new GameSyncController(_testJsonPath);
            controller.AddProfile(new GameProfile
            {
                ExecutableName = "Cyberpunk2077.exe",
                DisplayName = "Cyberpunk 2077",
                PowerMode = 0x04,
                FanMode = 0x01,
                CpuFanSpeed = 85,
                GpuFanSpeed = 90
            });

            string exportFile = Path.Combine(_tempDir, "export_roundtrip.json");
            controller.ExportProfiles(exportFile);

            Assert.True(File.Exists(exportFile));

            using var controller2 = new GameSyncController(Path.Combine(_tempDir, "empty.json"));
            int imported = controller2.ImportProfiles(exportFile);

            Assert.Equal(1, imported);
            Assert.Single(controller2.Profiles);
            Assert.Equal("Cyberpunk2077.exe", controller2.Profiles[0].ExecutableName);
            Assert.Equal(85, controller2.Profiles[0].CpuFanSpeed);
            Assert.Equal(90, controller2.Profiles[0].GpuFanSpeed);
        }

        #endregion

        #region KeyboardHook Security & Debounce Tests

        [Fact]
        public void KeyboardHook_InjectedKeys_BlockedByDefault()
        {
            int invokeCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onModeKeyPressed: () => Interlocked.Increment(ref invokeCount));

            Assert.False(hook.AllowInjectedKeys);

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x76); // SC_MODE
            structType.GetField("vkCode")!.SetValue(hookStruct, (uint)0x76);
            structType.GetField("flags")!.SetValue(hookStruct, (uint)0x10); // LLKHF_INJECTED

            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                Marshal.StructureToPtr(hookStruct, pStruct, false);

                // Call hook with injected event
                hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });

                Thread.Sleep(50);

                // Must be blocked
                Assert.Equal(0, invokeCount);

                // Enable injected keys and verify opt-in allows it
                hook.AllowInjectedKeys = true;
                hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });

                Thread.Sleep(50);
                Assert.Equal(1, invokeCount);
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }
        }

        [Fact]
        public void KeyboardHook_NullLParam_DoesNotCrash()
        {
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onModeKeyPressed: () => { });

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            // Passing IntPtr.Zero must not throw
            var res = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100, IntPtr.Zero });
            Assert.NotNull(res);
        }

        #endregion

        #region RegistrySafety Tests

        [Theory]
        [InlineData(@"..\..\Run\Evil", "Run_Evil")]
        [InlineData(@"sub\key\name", "sub_key_name")]
        [InlineData(@"evil/slash/game", "evil_slash_game")]
        [InlineData(@"normal_game", "normal_game")]
        [InlineData(@"Cyberpunk 2077 (v1.0)", "Cyberpunk 2077 (v1.0)")]
        [InlineData("", "Profile")]
        [InlineData(null, "Profile")]
        public void RegistrySafety_SanitizeSubKeyName_EliminatesTraversalAndSeparators(string? input, string expectedSubstring)
        {
            string sanitized = RegistrySafety.SanitizeSubKeyName(input);
            Assert.DoesNotContain("/", sanitized);
            Assert.DoesNotContain("\\", sanitized);
            Assert.DoesNotContain("..", sanitized);
            Assert.Contains(expectedSubstring, sanitized);
        }

        [Fact]
        public void RegistrySafety_SaveModeKeyAction_ClampsToValidRange()
        {
            RegistrySafety.SaveModeKeyAction(-5);
            Assert.Equal(0, RegistrySafety.LoadModeKeyAction());
            RegistrySafety.SaveModeKeyAction(99);
            Assert.Equal(1, RegistrySafety.LoadModeKeyAction());
        }

        [Fact]
        public void RegistrySafety_IsSafePath_EnforcesScope()
        {
            Assert.True(RegistrySafety.IsSafePath(@"SOFTWARE\PredatorControl"));
            Assert.True(RegistrySafety.IsSafePath(@"SOFTWARE\PredatorControl\Profiles"));
            Assert.True(RegistrySafety.IsSafePath(@"SOFTWARE\PredatorControl\Profiles\Game1"));
            Assert.False(RegistrySafety.IsSafePath(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));
            Assert.False(RegistrySafety.IsSafePath(@"SOFTWARE\PredatorControl\..\Windows"));
            Assert.False(RegistrySafety.IsSafePath(""));
        }

        #endregion
    }
}
