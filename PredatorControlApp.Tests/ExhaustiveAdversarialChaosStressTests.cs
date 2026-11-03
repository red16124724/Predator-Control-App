using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("ExhaustiveChaosTests")]
    public class ExhaustiveAdversarialChaosStressTests : IDisposable
    {
        private readonly string _tempDirectory;
        private readonly string _testSettingsFile;

        public ExhaustiveAdversarialChaosStressTests()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "PCA_ExhaustiveStress_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            _testSettingsFile = Path.Combine(_tempDirectory, "settings.json");
            AppSettings.CustomFilePath = _testSettingsFile;
        }

        public void Dispose()
        {
            AppSettings.CustomFilePath = null;
            try
            {
                if (Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory, true);
                }
            }
            catch { }
        }

        #region 1. Full Keyboard Shortcut Validation Stress & Fuzzing

        [Fact]
        public void ValidateShortcutSet_WithNullElements_DoesNotThrow_AndReturnsFalse()
        {
            var shortcutsWithNull = new List<CustomModeShortcut?>
            {
                new CustomModeShortcut("Eco", 0x06, Keys.E, ctrl: true, alt: true),
                null,
                new CustomModeShortcut("Turbo", 0x05, Keys.T, ctrl: true, alt: true)
            };

            bool isValid = ModeShortcutValidator.ValidateShortcutSet(shortcutsWithNull!, out string failureReason);

            Assert.False(isValid);
            Assert.False(string.IsNullOrWhiteSpace(failureReason));
        }

        [Fact]
        public void TryFindMatchingMode_WithNullOrCorruptedCollections_HandlesSafely()
        {
            // Null collection
            bool foundNull = ModeShortcutValidator.TryFindMatchingMode(null!, Keys.E, ctrl: true, alt: true, shift: false, win: false, out var matchNull);
            Assert.False(foundNull);
            Assert.Null(matchNull);

            // Empty collection
            bool foundEmpty = ModeShortcutValidator.TryFindMatchingMode(new List<CustomModeShortcut>(), Keys.E, ctrl: true, alt: true, shift: false, win: false, out var matchEmpty);
            Assert.False(foundEmpty);
            Assert.Null(matchEmpty);

            // Collection with null elements and unconfigured keys
            var list = new List<CustomModeShortcut>
            {
                null!,
                new CustomModeShortcut("Eco", 0x06, Keys.None, ctrl: true, alt: true),
                new CustomModeShortcut("Balanced", 0x01, Keys.B, ctrl: true, alt: true, shift: false, win: false, enabled: false),
                new CustomModeShortcut("Turbo", 0x05, Keys.T, ctrl: true, alt: true, shift: false, win: false, enabled: true)
            };

            bool foundTurbo = ModeShortcutValidator.TryFindMatchingMode(list, Keys.T, ctrl: true, alt: true, shift: false, win: false, out var matchTurbo);
            Assert.True(foundTurbo);
            Assert.NotNull(matchTurbo);
            Assert.Equal("Turbo", matchTurbo!.ModeName);

            // Unbound search
            bool foundUnbound = ModeShortcutValidator.TryFindMatchingMode(list, Keys.Z, ctrl: true, alt: true, shift: false, win: false, out var matchUnbound);
            Assert.False(foundUnbound);
            Assert.Null(matchUnbound);
        }

        [Fact]
        public void CloneShortcuts_WithNullOrNullElements_HandlesSafelyWithoutThrowing()
        {
            var nullResult = ModeShortcutValidator.CloneShortcuts(null);
            Assert.NotNull(nullResult);
            Assert.Empty(nullResult);

            var listWithNulls = new List<CustomModeShortcut>
            {
                new CustomModeShortcut("Quiet", 0x00, Keys.Q, ctrl: true, alt: true),
                null!,
                new CustomModeShortcut("Performance", 0x04, Keys.P, ctrl: true, alt: true)
            };

            var cloned = ModeShortcutValidator.CloneShortcuts(listWithNulls);
            Assert.NotNull(cloned);
            Assert.Equal(2, cloned.Count);
            Assert.Contains(cloned, s => s.ModeName == "Quiet");
            Assert.Contains(cloned, s => s.ModeName == "Performance");
        }

        [Fact]
        public void ModeShortcutValidator_FuzzAllVirtualKeyBoundaries_NeverThrows()
        {
            int[] testVks = {
                int.MinValue, -1000, -1, 0,
                1, 8, 9, 13, 27, 32, 65, 90, 112, 123, 135, 140, 255,
                1000, 0x7FFF, 0xFFFF, 0x10000, int.MaxValue
            };

            bool[] bools = { false, true };

            foreach (int vk in testVks)
            {
                foreach (bool ctrl in bools)
                {
                    foreach (bool alt in bools)
                    {
                        foreach (bool shift in bools)
                        {
                            foreach (bool win in bools)
                            {
                                bool valid = ModeShortcutValidator.IsValid(vk, ctrl, alt, shift, win, out string failureReason);
                                if (!valid)
                                {
                                    Assert.False(string.IsNullOrWhiteSpace(failureReason));
                                }
                            }
                        }
                    }
                }
            }
        }

        [Fact]
        public void ModeShortcutValidator_FuzzFunctionKeyBoundaries_StrictRejectionAcrossAllModifiers()
        {
            bool[] bools = { false, true };

            for (int vk = ModeShortcutValidator.MinFunctionKeyVk; vk <= ModeShortcutValidator.MaxFunctionKeyVk; vk++)
            {
                foreach (bool ctrl in bools)
                {
                    foreach (bool alt in bools)
                    {
                        foreach (bool shift in bools)
                        {
                            foreach (bool win in bools)
                            {
                                bool valid = ModeShortcutValidator.IsValid(vk, ctrl, alt, shift, win, out string reason);
                                Assert.False(valid, $"VK 0x{vk:X2} (F{vk - 0x70 + 1}) must be rejected.");
                                Assert.Contains("Function keys (F1-F24) are not allowed", reason);
                            }
                        }
                    }
                }
            }

            int beforeF1 = ModeShortcutValidator.MinFunctionKeyVk - 1;
            int afterF24 = ModeShortcutValidator.MaxFunctionKeyVk + 1;

            ModeShortcutValidator.IsValid(beforeF1, ctrl: true, alt: true, shift: false, win: false, out string rBefore);
            Assert.DoesNotContain("Function keys", rBefore);

            ModeShortcutValidator.IsValid(afterF24, ctrl: true, alt: true, shift: false, win: false, out string rAfter);
            Assert.DoesNotContain("Function keys", rAfter);
        }

        [Fact]
        public void ModeShortcutValidator_FuzzWindowsKeyCombinations_StrictRejection()
        {
            Keys[] candidateKeys = { Keys.A, Keys.B, Keys.D, Keys.E, Keys.L, Keys.P, Keys.R, Keys.S, Keys.V, Keys.X, Keys.Tab, Keys.Space, Keys.D1 };

            foreach (var key in candidateKeys)
            {
                bool valid = ModeShortcutValidator.IsValid(key, ctrl: false, alt: false, shift: false, win: true, out string reason);
                Assert.False(valid);
                Assert.Contains("Windows key combinations are reserved", reason);

                bool validWithModifiers = ModeShortcutValidator.IsValid(key, ctrl: true, alt: true, shift: true, win: true, out string reasonMod);
                Assert.False(validWithModifiers);
                Assert.Contains("Windows key combinations are reserved", reasonMod);
            }
        }

        [Fact]
        public void ModeShortcutValidator_FuzzReservedSystemKeys_StrictRejection()
        {
            Keys[] reservedKeys = {
                Keys.Escape, Keys.Tab, Keys.PrintScreen, Keys.Pause,
                Keys.NumLock, Keys.CapsLock, Keys.Scroll, Keys.Apps, Keys.Sleep
            };

            foreach (var key in reservedKeys)
            {
                bool valid = ModeShortcutValidator.IsValid(key, ctrl: true, alt: true, shift: false, win: false, out string reason);
                Assert.False(valid, $"System key {key} must be rejected.");
                Assert.Contains("reserved system/hardware control key", reason);
            }
        }

        [Fact]
        public void ModeShortcutValidator_FuzzStandardCtrlEditingKeys_StrictRejectionWithoutAlt()
        {
            Keys[] editingKeys = {
                Keys.C, Keys.V, Keys.X, Keys.Z, Keys.Y, Keys.A, Keys.S,
                Keys.P, Keys.O, Keys.N, Keys.W, Keys.F, Keys.H, Keys.T,
                Keys.D, Keys.R, Keys.J
            };

            foreach (var key in editingKeys)
            {
                bool ctrlAlone = ModeShortcutValidator.IsValid(key, ctrl: true, alt: false, shift: false, win: false, out string reason);
                Assert.False(ctrlAlone, $"Ctrl+{key} must be rejected as standard editing shortcut.");
                Assert.Contains("reserved standard application shortcut", reason);

                bool ctrlShift = ModeShortcutValidator.IsValid(key, ctrl: true, alt: false, shift: true, win: false, out string reasonShift);
                Assert.False(ctrlShift, $"Ctrl+Shift+{key} must be rejected as standard editing shortcut.");
                Assert.Contains("reserved standard application shortcut", reasonShift);

                bool ctrlAlt = ModeShortcutValidator.IsValid(key, ctrl: true, alt: true, shift: false, win: false, out string okReason);
                Assert.True(ctrlAlt, $"Ctrl+Alt+{key} should be allowed. Reason: {okReason}");
            }
        }

        [Fact]
        public void CustomModeShortcut_ConstructorExtractsEmbeddedModifierBits()
        {
            var shortcut = new CustomModeShortcut("Performance", 0x04, Keys.Control | Keys.Alt | Keys.P);
            Assert.Equal(Keys.P, shortcut.Key);
            Assert.True(shortcut.Ctrl);
            Assert.True(shortcut.Alt);
            Assert.False(shortcut.Shift);

            Assert.True(shortcut.IsValid(out string err), err);
        }

        [Fact]
        public void CustomModeShortcut_PathologicalFuzzing_ToStringEqualsAndHashCode()
        {
            var s1 = new CustomModeShortcut(null!, 255, (Keys)(-1), ctrl: true, alt: true);
            Assert.NotNull(s1.ModeName);
            Assert.NotNull(s1.ToString());
            Assert.NotNull(s1.DisplayString);

            var s2 = s1.Clone();
            Assert.Equal(s1, s2);
            Assert.Equal(s1.GetHashCode(), s2.GetHashCode());
            Assert.True(s1 == s2);

            var set = new HashSet<CustomModeShortcut> { s1 };
            Assert.Contains(s2, set);

            Assert.False(s1.Equals(null));
            Assert.False(s1.Equals(new object()));
        }

        [Fact]
        public void ModeShortcutValidator_FormatKeyName_And_FormatShortcut_ExtremeValues_NeverThrow()
        {
            Keys[] testKeys = { (Keys)(-1), (Keys)0, (Keys)9999, (Keys)int.MaxValue, Keys.None, Keys.Space, Keys.D5, Keys.NumPad3 };

            foreach (var k in testKeys)
            {
                string name = ModeShortcutValidator.FormatKeyName(k);
                Assert.NotNull(name);

                string formatted = ModeShortcutValidator.FormatShortcut(k, ctrl: true, alt: true, shift: false, win: false);
                Assert.NotNull(formatted);
            }
        }

        [Fact]
        public void AppSettings_Sanitize_CustomModeShortcutsWithNullElements_CleansesSafely()
        {
            var settings = new AppSettings();
            settings.CustomModeShortcuts = new List<CustomModeShortcut>
            {
                null!,
                new CustomModeShortcut("Turbo", 0x05, Keys.T, ctrl: true, alt: true),
                null!
            };

            settings.Sanitize();

            Assert.NotNull(settings.CustomModeShortcuts);
            Assert.DoesNotContain(null!, settings.CustomModeShortcuts);
            Assert.Contains(settings.CustomModeShortcuts, s => s.ModeName == "Turbo");
        }

        #endregion

        #region 2. AppSettings Extreme Concurrency & File Corruption Resilience

        [Fact]
        public void AppSettings_ExtremeConcurrency_40Threads_SimultaneousLoadsSavesAndMutations()
        {
            var initialSettings = new AppSettings
            {
                Theme = "Dark",
                PowerMode = 0x01,
                FanMode = 0x01,
                FanSpeedCpu = 60,
                FanSpeedGpu = 60
            };
            initialSettings.Save();

            int threadCount = 40;
            var exceptions = new List<Exception>();
            using var barrier = new Barrier(threadCount);

            var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
            {
                barrier.SignalAndWait(5000);
                for (int iter = 0; iter < 15; iter++)
                {
                    try
                    {
                        if (threadId % 2 == 0)
                        {
                            var s = AppSettings.Load();
                            s.FanSpeedCpu = Math.Clamp((s.FanSpeedCpu + iter * 3) % 100, 10, 100);
                            s.PowerMode = (byte)(iter % 5 == 0 ? 0x05 : 0x01);
                            s.Save();
                        }
                        else
                        {
                            var s = AppSettings.Load();
                            var snap = s.CreateSnapshot();
                            Assert.NotNull(snap);
                            Assert.NotNull(snap.Theme);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                }
            })).ToArray();

            bool completed = Task.WaitAll(tasks, TimeSpan.FromSeconds(30));
            Assert.True(completed, "Concurrent stress test timed out or deadlocked.");
            Assert.Empty(exceptions);

            var finalSettings = AppSettings.Load();
            Assert.NotNull(finalSettings);
        }

        [Fact]
        public void AppSettings_FileCorruptionResilience_TruncatedJson_RecoversGracefully()
        {
            var valid = new AppSettings { Theme = "Light", PowerMode = 0x04 };
            valid.Save();

            File.WriteAllText(_testSettingsFile, "{\"Theme\": \"Dark\", \"PowerMode\": ");

            var loaded = AppSettings.Load();
            Assert.NotNull(loaded);
            Assert.True(loaded.Theme == "Light" || loaded.Theme == "System");
        }

        [Fact]
        public void AppSettings_FileCorruptionResilience_GarbageBinaryPayload_RecoversGracefully()
        {
            var valid = new AppSettings { Theme = "Dark", PowerMode = 0x05 };
            valid.Save();

            byte[] garbage = new byte[8192];
            new Random(42).NextBytes(garbage);
            File.WriteAllBytes(_testSettingsFile, garbage);

            var loaded = AppSettings.Load();
            Assert.NotNull(loaded);
            Assert.True(loaded.Theme == "Dark" || loaded.Theme == "System");
        }

        [Fact]
        public void AppSettings_FileCorruptionResilience_OversizedPayload_Exceeding1MB_SafelyIgnored()
        {
            var valid = new AppSettings { Theme = "Dark", PowerMode = 0x01 };
            valid.Save();

            using (var stream = new FileStream(_testSettingsFile, FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write("{\"Theme\": \"Dark\", \"Padding\": \"");
                for (int i = 0; i < 2_000_000; i++)
                {
                    writer.Write('A');
                }
                writer.Write("\"}");
            }

            var loaded = AppSettings.Load();
            Assert.NotNull(loaded);
            Assert.NotNull(loaded.Theme);
        }

        [Fact]
        public void AppSettings_FileCorruptionResilience_Empty0ByteFile_LoadsBackupOrDefaults()
        {
            var valid = new AppSettings { Theme = "Dark", PowerMode = 0x00 };
            valid.Save();

            File.WriteAllBytes(_testSettingsFile, Array.Empty<byte>());

            var loaded = AppSettings.Load();
            Assert.NotNull(loaded);
            Assert.NotNull(loaded.Theme);
        }

        [Fact]
        public void AppSettings_FileCorruptionResilience_BothMainAndBakCorrupted_RecoversToDefaults()
        {
            File.WriteAllText(_testSettingsFile, "INVALID_JSON_CORRUPTED_MAIN");
            File.WriteAllText(_testSettingsFile + ".bak", "INVALID_JSON_CORRUPTED_BAK");

            var loaded = AppSettings.Load();
            Assert.NotNull(loaded);
            Assert.NotNull(loaded.Theme);
            Assert.Equal("System", loaded.Theme);
            Assert.NotEmpty(loaded.CustomModeShortcuts);
        }

        [Fact]
        public void AppSettings_Save_WhenFileIsLockedExclusively_HandlesGracefullyWithoutCrash()
        {
            var settings = new AppSettings { Theme = "Dark", PowerMode = 0x01 };
            settings.Save();

            using (var lockStream = new FileStream(_testSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var ex = Record.Exception(() =>
                {
                    settings.PowerMode = 0x05;
                    settings.Save();
                });
                Assert.Null(ex);
            }
        }

        [Fact]
        public void AppSettings_Save_ReadOnlyFileAttribute_ClearsReadOnlyAndSucceeds()
        {
            var settings = new AppSettings { Theme = "Dark", PowerMode = 0x01 };
            settings.Save();

            File.SetAttributes(_testSettingsFile, FileAttributes.ReadOnly);
            if (File.Exists(_testSettingsFile + ".bak"))
            {
                File.SetAttributes(_testSettingsFile + ".bak", FileAttributes.ReadOnly);
            }

            settings.PowerMode = 0x05;
            settings.Save();

            var loaded = AppSettings.Load();
            Assert.Equal(0x05, loaded.PowerMode);
        }

        [Fact]
        public void AppSettings_Sanitize_PathologicalBoundaryValues_AllFieldsClampedSafely()
        {
            var settings = new AppSettings
            {
                PowerMode = 255,
                FanMode = 255,
                FanSpeedCpu = -100,
                FanSpeedGpu = 500,
                FanSpeedSys = -50,
                FanHysteresisDegrees = 9999,
                RefreshRate = -100,
                RgbMode = 999,
                RgbBrightness = -20,
                RgbSpeed = 300,
                PowerModeSettleMs = -500,
                CustomEcoPl1Watts = -50,
                CustomTurboPl2Watts = 9999,
                BiosHardwareFingerprint = null!,
                CustomColors = new int[5]
            };

            settings.Sanitize();

            Assert.InRange(settings.PowerMode, 0x00, 0x06);
            Assert.InRange(settings.FanMode, (byte)0x01, (byte)0x03);
            Assert.InRange(settings.FanSpeedCpu, 0, 100);
            Assert.InRange(settings.FanSpeedGpu, 0, 100);
            Assert.InRange(settings.FanSpeedSys, 0, 100);
            Assert.InRange(settings.FanHysteresisDegrees, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees);
            Assert.InRange(settings.RefreshRate, 0, 1000);
            Assert.InRange(settings.PowerModeSettleMs, 100, 3000);
            Assert.NotNull(settings.BiosHardwareFingerprint);
            Assert.Equal(16, settings.CustomColors.Length);
        }

        #endregion

        #region 3. Hardware Protocol Edge Cases

        [Fact]
        public void AcerProtocol_FanBehaviorInput_WithNullFan_DoesNotThrowNullReference()
        {
            var listWithNullFan = new (FanChannel Fan, FanBehavior Behavior)[]
            {
                (FanChannel.Cpu, FanBehavior.Auto),
                (null!, FanBehavior.Max),
                (FanChannel.Gpu, FanBehavior.Custom)
            };

            var ex = Record.Exception(() => AcerProtocol.FanBehaviorInput(listWithNullFan));
            Assert.Null(ex);
        }

        [Fact]
        public void AcerProtocol_FanBehaviorInput_InvalidEnumValues_ClampedSafely()
        {
            var list = new (FanChannel Fan, FanBehavior Behavior)[]
            {
                (FanChannel.Cpu, (FanBehavior)255),
                (FanChannel.Gpu, (FanBehavior)99)
            };

            ulong payload = AcerProtocol.FanBehaviorInput(list);
            Assert.True(payload > 0);
        }

        [Fact]
        public void AcerProtocol_FanSpeedInput_NullFan_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => AcerProtocol.FanSpeedInput(null!, 50));
        }

        [Fact]
        public void AcerProtocol_FanSpeedInput_ExtremePercentBoundaries_ClampsTo0And100()
        {
            ulong minPayload = AcerProtocol.FanSpeedInput(FanChannel.Cpu, int.MinValue);
            ulong maxPayload = AcerProtocol.FanSpeedInput(FanChannel.Cpu, int.MaxValue);

            int minSpeed = (int)((minPayload >> 8) & 0xFF);
            int maxSpeed = (int)((maxPayload >> 8) & 0xFF);

            Assert.Equal(0, minSpeed);
            Assert.Equal(100, maxSpeed);
        }

        [Fact]
        public void AcerProtocol_NearestFanSpeed_FuzzingAcrossExtremeRange()
        {
            for (int i = -200; i <= 300; i++)
            {
                int nearest = AcerProtocol.NearestFanSpeed(i);
                Assert.InRange(nearest, 0, 100);
                Assert.Equal(0, nearest % 10);
            }
        }

        [Fact]
        public void AcerProtocol_DecodeSensorMask_BoundaryOutputs_NeverThrows()
        {
            ulong[] testOutputs = { 0UL, 1UL, ulong.MaxValue, 0x8000000000000000UL, 0x0000000001000000UL };
            foreach (var outVal in testOutputs)
            {
                var sensors = AcerProtocol.DecodeSensorMask(outVal);
                Assert.NotNull(sensors);
            }
        }

        [Fact]
        public void AcerProtocol_DecodeOperatingModeMask_BoundaryOutputs_NeverThrows()
        {
            ulong[] testOutputs = { 0UL, 1UL, ulong.MaxValue, 0x000000000000FF00UL };
            foreach (var outVal in testOutputs)
            {
                var modes = AcerProtocol.DecodeOperatingModeMask(outVal);
                Assert.NotNull(modes);
            }
        }

        [Fact]
        public void AcerProtocol_OperatingModeConversions_BoundaryFuzzing()
        {
            for (int wmi = -50; wmi <= 300; wmi++)
            {
                for (int modeCount = -5; modeCount <= 10; modeCount++)
                {
                    byte ec = AcerProtocol.WmiModeToEcMode((byte)wmi, modeCount);
                    byte backWmi = AcerProtocol.EcModeToWmiMode(ec, modeCount);
                    Assert.InRange(ec, 0, 255);
                    Assert.InRange(backWmi, 0, 255);
                }
            }
        }

        [Fact]
        public void FirmwareEvent_ToString_WithNullDetail_DoesNotThrow()
        {
            var evt = new FirmwareEvent(FirmwareEventKind.Hotkey, 1, null!);
            string s = evt.ToString();
            Assert.NotNull(s);
            Assert.Contains("Hotkey", s);
        }

        [Fact]
        public void BatteryHealth_DivideByZeroAndNegativeBoundaries_WearLevelPercentSafe()
        {
            var zeroDesign = new BatteryHealth(0, 50000, 100, false);
            Assert.Equal(0.0, zeroDesign.WearLevelPercent);

            var negDesign = new BatteryHealth(-100, 50000, 100, false);
            Assert.Equal(0.0, negDesign.WearLevelPercent);

            var overCharge = new BatteryHealth(50000, 60000, 10, false);
            Assert.InRange(overCharge.WearLevelPercent, 0.0, 100.0);

            var negCharge = new BatteryHealth(50000, -1000, 10, false);
            Assert.InRange(negCharge.WearLevelPercent, 0.0, 100.0);
        }

        [Fact]
        public void WmiController_SetFanSpeed_NullFanChannel_ReturnsFalseWithoutThrowing()
        {
            using var wmi = new WmiController();
            bool result = wmi.SetFanSpeed(null!, 50);
            Assert.False(result);
        }

        [Fact]
        public void AppSettings_PowerLimits_InvalidOperatingModeEnums_HandlesGracefully()
        {
            var settings = new AppSettings();
            var invalidModes = new[] { (OperatingMode)99, unchecked((OperatingMode)255), (OperatingMode)2, (OperatingMode)3 };

            foreach (var m in invalidModes)
            {
                var defLimits = settings.GetDefaultPowerLimitsForMode(m);
                Assert.True(defLimits.pl1 > 0);
                Assert.True(defLimits.pl2 >= defLimits.pl1);

                var effLimits = settings.GetEffectivePowerLimitsForMode(m);
                Assert.True(effLimits.pl1 > 0);
                Assert.True(effLimits.pl2 >= effLimits.pl1);

                bool hasScanned = settings.HasScannedDefaultsForMode(m);
                Assert.False(hasScanned);

                settings.SetPowerLimitsForMode(m, 45, 65);
                var userLimits = settings.GetUserPowerLimitsForMode(m);
                Assert.Equal(45, userLimits.pl1);
                Assert.Equal(65, userLimits.pl2);
            }
        }

        [Fact]
        public void KeyboardLightingPalette_Fuzzing_CorruptHexTokens_NeverThrows()
        {
            string[] pathologicalTokens = {
                "", " ", "#", "#1", "#12", "#1234", "#12345", "#1234567", "#123456789",
                "#GGGGGG", "Red", "InvalidColorName", new string('F', 500), null!
            };

            foreach (var token in pathologicalTokens)
            {
                bool parsed = KeyboardLightingPalette.TryParse(token, out Color c);
                // c is parsed

                var colors = KeyboardLightingPalette.Parse(token, count: 4, fallback: Color.Red);
                Assert.Equal(4, colors.Length);
                // colors parsed
            }
        }

        [Fact]
        public void BacklightStateManager_CrossThreadStress_30Threads_NoDeadlocks()
        {
            var manager = new BacklightStateManager();
            int threadCount = 30;
            var exceptions = new List<Exception>();

            var tasks = Enumerable.Range(0, threadCount).Select(i => Task.Run(() =>
            {
                for (int iter = 0; iter < 100; iter++)
                {
                    try
                    {
                        if (iter % 3 == 0)
                        {
                            manager.OnLidChanged(iter % 2 == 0, PowerLineStatus.Online, out _, out _);
                        }
                        else if (iter % 3 == 1)
                        {
                            manager.OnSuspend(out _, out _);
                        }
                        else
                        {
                            manager.OnResume(PowerLineStatus.Offline, out _, out _);
                        }
                        _ = manager.IsOnBattery(PowerLineStatus.Offline);
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                }
            })).ToArray();

            bool finished = Task.WaitAll(tasks, TimeSpan.FromSeconds(15));
            Assert.True(finished, "BacklightStateManager cross-thread stress timed out.");
            Assert.Empty(exceptions);
        }

        #endregion

        #region 4. UI Thread Safety Simulation and Disposable Resources Lifecycle

        [Fact]
        public void PredatorButton_RapidCreationDisposalAndDoubleDispose_CleanLifecycle()
        {
            for (int i = 0; i < 50; i++)
            {
                var btn = new PredatorButton();
                btn.IsActive = (i % 2 == 0);
                btn.IsNavButton = (i % 3 == 0);
                btn.CustomActiveColor = Color.FromArgb(255, i % 255, 0, 100);

                btn.Dispose();
                btn.Dispose();
            }
        }

        [Fact]
        public void PredatorSlider_CrossThreadUpdatesAndDisposalLifecycle()
        {
            var slider = new PredatorSlider();
            slider.Minimum = 0;
            slider.Maximum = 100;

            var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            {
                for (int v = 0; v < 50; v++)
                {
                    try
                    {
                        slider.Value = v;
                    }
                    catch { }
                }
            })).ToArray();

            Task.WaitAll(tasks, TimeSpan.FromSeconds(5));

            slider.Dispose();
            slider.Dispose();
        }

        [Fact]
        public void PredatorSwitch_And_PredatorToggle_DisposalMidAnimation_SafelyUnsubscribes()
        {
            var pSwitch = new PredatorSwitch();
            var pToggle = new PredatorToggle();

            pSwitch.Checked = true;
            pToggle.Checked = true;

            pSwitch.Dispose();
            pToggle.Dispose();

            ThemeManager.SetTheme(AppTheme.Light);
            ThemeManager.SetTheme(AppTheme.Dark);
        }

        [Fact]
        public void PredatorDropDown_CrossThreadSelectedChange_AndDisposalSafety()
        {
            var dropDown = new PredatorDropDown();
            dropDown.Items.AddRange(new[] { "Eco", "Quiet", "Balanced", "Performance", "Turbo" });

            var tasks = Enumerable.Range(0, 5).Select(i => Task.Run(() =>
            {
                for (int idx = 0; idx < 5; idx++)
                {
                    try
                    {
                        dropDown.SelectedIndex = idx;
                    }
                    catch { }
                }
            })).ToArray();

            Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
            dropDown.Dispose();
            dropDown.Dispose();
        }

        [Fact]
        public void PowerWakeLock_ConcurrencyAndScopedDisposalStress_NoRefLeak()
        {
            PowerWakeLock.ResetForTesting();
            try
            {
                PowerWakeLock.ExecutionStateOverride = flags => 0x80000000;
                int threadCount = 20;

                var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        using (PowerWakeLock.AcquireScoped())
                        {
                            Assert.True(PowerWakeLock.IsLocked);
                        }
                    }
                })).ToArray();

                bool finished = Task.WaitAll(tasks, TimeSpan.FromSeconds(10));
                Assert.True(finished, "PowerWakeLock scoped stress timed out.");

                Assert.False(PowerWakeLock.IsLocked);
                Assert.Equal(PowerWakeLock.ES_CONTINUOUS, PowerWakeLock.CurrentFlags);
            }
            finally
            {
                PowerWakeLock.ResetForTesting();
            }
        }

        [Fact]
        public void DarkScrollPanel_RapidAddRemoveControls_AndDisposalLifecycle()
        {
            var panel = new DarkScrollPanel();
            panel.Size = new Size(300, 300);

            for (int i = 0; i < 30; i++)
            {
                var btn = new Button { Size = new Size(100, 30), Location = new Point(0, i * 35) };
                panel.Controls.Add(btn);
            }

            panel.AutoScrollPosition = new Point(0, 50);
            panel.Controls.Clear();
            panel.Dispose();
            panel.Dispose();
        }

        [Fact]
        public void FanCurveGraph_PathologicalPointsAndRapidDisposal_SafeOperation()
        {
            var graph = new FanCurveGraph();
            graph.Size = new Size(400, 300);

            var pathologicalPoints = new List<Point>
            {
                new Point(0, 0),
                new Point(-50, -50),
                new Point(1000, 1000),
                new Point(50, 50)
            };

            graph.Points = pathologicalPoints;
            graph.CurrentTemp = 75;

            graph.Dispose();
            graph.Dispose();
        }

        #endregion
    }
}
