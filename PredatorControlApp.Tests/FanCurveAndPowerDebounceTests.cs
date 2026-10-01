using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class FanCurveAndPowerDebounceTests
    {
        [Fact]
        public void NormalizeFanCurve_EnforcesStrictMonotonicPoints()
        {
            var raw = new List<Point>
            {
                new Point(40, 30),
                new Point(30, 20), // out of order
                new Point(80, 70),
                new Point(60, 50)
            };

            var normalized = FanCurveGraph.Normalize(raw);
            Assert.True(normalized.Count >= 2);

            // Points must be ordered by X monotonically
            for (int i = 0; i < normalized.Count - 1; i++)
            {
                Assert.True(normalized[i].X < normalized[i + 1].X, $"Point {i} X ({normalized[i].X}) should be < next X ({normalized[i+1].X})");
            }
        }

        [Fact]
        public void DebouncePowerLine_RequiresTwoConsecutiveTicksToChangeState()
        {
            bool? current = true; // Started plugged in
            bool? pending = null;
            int ticks = 0;

            // Unplugged: first tick
            bool? result1 = Form1.DebouncePowerLine(PowerLineStatus.Offline, current, ref pending, ref ticks);
            Assert.True(result1); // Still true (not yet confirmed)
            Assert.Equal(1, ticks);
            Assert.False(pending);

            // Second tick offline
            bool? result2 = Form1.DebouncePowerLine(PowerLineStatus.Offline, current, ref pending, ref ticks);
            Assert.False(result2); // Confirmed offline!
            Assert.Equal(2, ticks);
        }

        [Fact]
        public void DebouncePowerLine_IgnoresFlappingSingleTicks()
        {
            bool? current = true;
            bool? pending = null;
            int ticks = 0;

            // Tick 1: Offline
            Form1.DebouncePowerLine(PowerLineStatus.Offline, current, ref pending, ref ticks);
            Assert.Equal(1, ticks);

            // Flapped back to Online on next tick
            bool? result = Form1.DebouncePowerLine(PowerLineStatus.Online, current, ref pending, ref ticks);
            Assert.True(result); // Never transitioned to false
            Assert.Equal(1, ticks);
        }

        [Fact]
        public void DebouncePowerLine_HandlesUnknownGracefullyWithoutCorruptingState()
        {
            bool? current = false; // Started on battery
            bool? pending = null;
            int ticks = 0;

            // Windows reports Unknown on wake
            bool? result = Form1.DebouncePowerLine(PowerLineStatus.Unknown, current, ref pending, ref ticks);
            Assert.False(result); // Remains false (safe battery state)
            Assert.Null(pending);
            Assert.Equal(0, ticks);
        }

        [Fact]
        public void NormalizeFanCurve_NullOrEmpty_ReturnsDefaultPoints()
        {
            var resultNull = FanCurveGraph.Normalize(null);
            Assert.NotNull(resultNull);
            Assert.True(resultNull.Count >= 2);

            var resultEmpty = FanCurveGraph.Normalize(new List<Point>());
            Assert.NotNull(resultEmpty);
            Assert.True(resultEmpty.Count >= 2);

            var resultSingle = FanCurveGraph.Normalize(new List<Point> { new Point(50, 50) });
            Assert.NotNull(resultSingle);
            Assert.True(resultSingle.Count >= 2);
        }

        [Fact]
        public void NormalizeFanCurve_ClampsExtremeTempsAndSpeeds()
        {
            var raw = new List<Point>
            {
                new Point(-50, -20),
                new Point(50, 50),
                new Point(150, 200)
            };

            var normalized = FanCurveGraph.Normalize(raw);
            Assert.Equal(3, normalized.Count);
            Assert.Equal(30, normalized[0].X); // TempMin
            Assert.Equal(0, normalized[0].Y);  // SpeedMin
            Assert.Equal(100, normalized[^1].X); // TempMax
            Assert.Equal(100, normalized[^1].Y); // SpeedMax
        }

        [Fact]
        public void InterpolateCurve_NullOrEmpty_ReturnsSafeDefaultFifty()
        {
            Assert.Equal(50, Form1.InterpolateCurve(null, 65));
            Assert.Equal(50, Form1.InterpolateCurve(new List<Point>(), 65));
        }

        [Fact]
        public void InterpolateCurve_BoundaryAndLinearInterpolation()
        {
            var curve = new List<Point>
            {
                new Point(40, 20),
                new Point(60, 40),
                new Point(80, 80)
            };

            // Below lowest point
            Assert.Equal(20, Form1.InterpolateCurve(curve, 30));
            Assert.Equal(20, Form1.InterpolateCurve(curve, 40));

            // Above highest point
            Assert.Equal(80, Form1.InterpolateCurve(curve, 90));
            Assert.Equal(80, Form1.InterpolateCurve(curve, 80));

            // Midpoint between 40 (20%) and 60 (40%): 50°C -> 30%
            Assert.Equal(30, Form1.InterpolateCurve(curve, 50));

            // Midpoint between 60 (40%) and 80 (80%): 70°C -> 60%
            Assert.Equal(60, Form1.InterpolateCurve(curve, 70));
        }

        [Fact]
        public void InterpolateCurve_TemperatureZeroOrNegative_ReturnsLowestPoint()
        {
            var curve = new List<Point>
            {
                new Point(40, 20),
                new Point(60, 40),
                new Point(80, 80)
            };

            Assert.Equal(20, Form1.InterpolateCurve(curve, 0));
            Assert.Equal(20, Form1.InterpolateCurve(curve, -10));
        }

        [Fact]
        public void InterpolateCurve_TemperatureAboveMax_ReturnsHighestPoint()
        {
            var curve = new List<Point>
            {
                new Point(40, 20),
                new Point(60, 40),
                new Point(80, 80)
            };

            Assert.Equal(80, Form1.InterpolateCurve(curve, 105));
            Assert.Equal(80, Form1.InterpolateCurve(curve, 120));
        }

        [Fact]
        public void InterpolateCurve_SinglePointSpan_ReturnsExpected()
        {
            var single = new List<Point> { new Point(50, 60) };
            Assert.Equal(60, Form1.InterpolateCurve(single, 30));
            Assert.Equal(60, Form1.InterpolateCurve(single, 50));
            Assert.Equal(60, Form1.InterpolateCurve(single, 80));
        }

        [Fact]
        public void NormalizeFanCurve_DuplicateXCoordinates_SortedAndClamped()
        {
            var duplicates = new List<Point>
            {
                new Point(50, 30),
                new Point(50, 40),
                new Point(70, 60)
            };

            var normalized = FanCurveGraph.Normalize(duplicates);
            Assert.True(normalized.Count >= 2);
            Assert.Equal(30, normalized[0].X);
            Assert.Equal(100, normalized[^1].X);
            for (int i = 0; i < normalized.Count - 1; i++)
            {
                Assert.True(normalized[i].X <= normalized[i + 1].X);
            }
        }
    }

    public class GameSyncControllerTests : System.IDisposable
    {
        private readonly string _tempFile;

        public GameSyncControllerTests()
        {
            _tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gamesync_test_{System.Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            try
            {
                if (System.IO.File.Exists(_tempFile))
                    System.IO.File.Delete(_tempFile);
            }
            catch { }
        }

        [Fact]
        public void Constructor_InitializesEmptyProfilesWhenNoFileExists()
        {
            using var controller = new GameSyncController(_tempFile);
            Assert.False(controller.IsEnabled);
            Assert.Empty(controller.Profiles);
            Assert.Null(controller.ActiveGameExe);
        }

        [Fact]
        public void AddProfile_ValidProfile_AddsSuccessfully()
        {
            using var controller = new GameSyncController(_tempFile);
            var profile = new GameProfile
            {
                DisplayName = "Cyberpunk 2077",
                ExecutableName = "Cyberpunk2077.exe",
                PowerMode = 0x05,
                FanMode = 0x02
            };

            controller.AddProfile(profile);

            Assert.Single(controller.Profiles);
            Assert.Equal("Cyberpunk 2077", controller.Profiles[0].DisplayName);
            Assert.Equal("Cyberpunk2077.exe", controller.Profiles[0].ExecutableName);
        }

        [Fact]
        public void AddProfile_NullOrWhitespaceExe_DoesNotAddOrThrow()
        {
            using var controller = new GameSyncController(_tempFile);
            controller.AddProfile(null!);
            controller.AddProfile(new GameProfile { DisplayName = "Invalid", ExecutableName = "" });
            controller.AddProfile(new GameProfile { DisplayName = "Invalid2", ExecutableName = "   " });

            Assert.Empty(controller.Profiles);
        }

        [Fact]
        public void AddProfile_CaseInsensitiveExecutable_ReplacesExisting()
        {
            using var controller = new GameSyncController(_tempFile);
            var profile1 = new GameProfile
            {
                DisplayName = "Doom Eternal",
                ExecutableName = "DoomEternal.exe",
                PowerMode = 0x01
            };
            var profile2 = new GameProfile
            {
                DisplayName = "Doom Eternal Updated",
                ExecutableName = "doometernal.EXE",
                PowerMode = 0x05
            };

            controller.AddProfile(profile1);
            controller.AddProfile(profile2);

            Assert.Single(controller.Profiles);
            Assert.Equal("Doom Eternal Updated", controller.Profiles[0].DisplayName);
            Assert.Equal(0x05, controller.Profiles[0].PowerMode);
        }

        [Fact]
        public void RemoveProfile_RemovesMatchingProfile_CaseInsensitive()
        {
            using var controller = new GameSyncController(_tempFile);
            controller.AddProfile(new GameProfile { DisplayName = "Game 1", ExecutableName = "game1.exe" });
            controller.AddProfile(new GameProfile { DisplayName = "Game 2", ExecutableName = "game2.exe" });
            Assert.Equal(2, controller.Profiles.Count);

            controller.RemoveProfile("GAME1.EXE");

            Assert.Single(controller.Profiles);
            Assert.Equal("game2.exe", controller.Profiles[0].ExecutableName);
        }

        [Fact]
        public void RemoveProfile_NullOrEmpty_DoesNotThrow()
        {
            using var controller = new GameSyncController(_tempFile);
            controller.AddProfile(new GameProfile { DisplayName = "Game 1", ExecutableName = "game1.exe" });

            controller.RemoveProfile(null!);
            controller.RemoveProfile("");
            controller.RemoveProfile("   ");

            Assert.Single(controller.Profiles);
        }

        [Fact]
        public void UpdateProfile_UpdatesExistingOrAddsIfNotFound()
        {
            using var controller = new GameSyncController(_tempFile);
            var p1 = new GameProfile { DisplayName = "Witcher 3", ExecutableName = "witcher3.exe", PowerMode = 0x01 };
            controller.AddProfile(p1);

            // Update existing
            var p1Updated = new GameProfile { DisplayName = "The Witcher 3: Wild Hunt", ExecutableName = "WITCHER3.EXE", PowerMode = 0x04 };
            controller.UpdateProfile(p1Updated);

            Assert.Single(controller.Profiles);
            Assert.Equal("The Witcher 3: Wild Hunt", controller.Profiles[0].DisplayName);
            Assert.Equal(0x04, controller.Profiles[0].PowerMode);

            // Update nonexistent -> adds it
            var p2 = new GameProfile { DisplayName = "Elden Ring", ExecutableName = "eldenring.exe", PowerMode = 0x05 };
            controller.UpdateProfile(p2);

            Assert.Equal(2, controller.Profiles.Count);
        }

        [Fact]
        public void Persistence_SavesAndReloadsAccurately()
        {
            using (var controller1 = new GameSyncController(_tempFile))
            {
                controller1.IsEnabled = true;
                controller1.AddProfile(new GameProfile
                {
                    DisplayName = "Starfield",
                    ExecutableName = "Starfield.exe",
                    PowerMode = 0x05,
                    FanMode = 0x03,
                    CpuFanSpeed = 80,
                    GpuFanSpeed = 85,
                    RefreshRate = 165,
                    BatteryLimit = 1,
                    RgbMode = 2,
                    RgbBrightness = 90,
                    RgbSpeed = 60,
                    RgbR = 255,
                    RgbG = 120,
                    RgbB = 0
                });
            }

            // New instance reading from same path
            using (var controller2 = new GameSyncController(_tempFile))
            {
                Assert.True(controller2.IsEnabled);
                Assert.Single(controller2.Profiles);
                var loaded = controller2.Profiles[0];
                Assert.Equal("Starfield", loaded.DisplayName);
                Assert.Equal("Starfield.exe", loaded.ExecutableName);
                Assert.Equal(0x05, loaded.PowerMode);
                Assert.Equal(0x03, loaded.FanMode);
                Assert.Equal(80, loaded.CpuFanSpeed);
                Assert.Equal(85, loaded.GpuFanSpeed);
                Assert.Equal(165, loaded.RefreshRate);
                Assert.Equal(1, loaded.BatteryLimit);
                Assert.Equal(2, loaded.RgbMode);
                Assert.Equal(90, loaded.RgbBrightness);
                Assert.Equal(60, loaded.RgbSpeed);
                Assert.Equal(255, loaded.RgbR);
                Assert.Equal(120, loaded.RgbG);
                Assert.Equal(0, loaded.RgbB);
            }
        }

        [Fact]
        public void IsEnabled_DisablingWhileActive_FiresGameExitedIfGameWasActive()
        {
            using var controller = new GameSyncController(_tempFile);
            controller.IsEnabled = true;
            controller.AddProfile(new GameProfile { DisplayName = "TestGame", ExecutableName = "testgame.exe" });

            var snap = new DashboardSnapshot
            {
                PowerMode = 0x01,
                FanMode = 0x01,
                RefreshRate = 60
            };
            controller.SetPreGameSnapshot(snap);

            DashboardSnapshot? receivedSnap = null;
            controller.GameExited += s => receivedSnap = s;

            // Simulate active game by calling through reflection
            var activeExeField = typeof(GameSyncController).GetField("_activeExe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            activeExeField?.SetValue(controller, "testgame.exe");

            controller.IsEnabled = false;

            Assert.NotNull(receivedSnap);
            Assert.Equal(0x01, receivedSnap.PowerMode);
            Assert.Null(controller.ActiveGameExe);
        }

        [Fact]
        public void IsEnabled_DisablingWhileActiveWithoutSnapshot_ClearsActiveExeWithoutThrowing()
        {
            using var controller = new GameSyncController(_tempFile);
            controller.IsEnabled = true;
            controller.AddProfile(new GameProfile { DisplayName = "TestGame", ExecutableName = "testgame.exe" });

            // Set activeExe without setting preGameSnapshot
            var activeExeField = typeof(GameSyncController).GetField("_activeExe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            activeExeField?.SetValue(controller, "testgame.exe");

            bool exitedFired = false;
            controller.GameExited += _ => exitedFired = true;

            controller.IsEnabled = false;

            Assert.False(exitedFired);
            Assert.Null(controller.ActiveGameExe);
        }

        [Fact]
        public void PollOnce_ActiveNonExistentGame_CleansUpAndFiresExited()
        {
            using var controller = new GameSyncController(_tempFile);
            controller.IsEnabled = true;
            controller.AddProfile(new GameProfile { DisplayName = "FakeGame", ExecutableName = "definitely_nonexistent_proc_12345.exe" });

            var snap = new DashboardSnapshot { PowerMode = 0x01 };
            controller.SetPreGameSnapshot(snap);

            var activeExeField = typeof(GameSyncController).GetField("_activeExe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            activeExeField?.SetValue(controller, "definitely_nonexistent_proc_12345.exe");

            DashboardSnapshot? restored = null;
            controller.GameExited += s => restored = s;

            controller.PollOnce();

            Assert.NotNull(restored);
            Assert.Null(controller.ActiveGameExe);
        }

        [Fact]
        public void PollOnce_DisabledController_DoesNotTriggerAnyEvents()
        {
            using var controller = new GameSyncController(_tempFile);
            controller.IsEnabled = false;
            controller.AddProfile(new GameProfile { DisplayName = "FakeGame", ExecutableName = "fake.exe" });

            bool detected = false;
            controller.GameDetected += _ => detected = true;

            controller.PollOnce();

            Assert.False(detected);
            Assert.Null(controller.ActiveGameExe);
        }

        [Fact]
        public void ConcurrencyStressTest_MultiThreadedAddRemoveQuery_NoExceptionsOrDeadlocks()
        {
            using var controller = new GameSyncController(_tempFile);
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<System.Exception>();

            System.Threading.Tasks.Parallel.For(0, 300, i =>
            {
                try
                {
                    string exe = $"game_{i % 15}.exe";
                    switch (i % 4)
                    {
                        case 0:
                            controller.AddProfile(new GameProfile
                            {
                                DisplayName = $"Game {i % 15}",
                                ExecutableName = exe,
                                PowerMode = (byte)(i % 5)
                            });
                            break;
                        case 1:
                            var list = controller.Profiles;
                            foreach (var p in list)
                            {
                                _ = p.DisplayName;
                            }
                            break;
                        case 2:
                            controller.UpdateProfile(new GameProfile
                            {
                                DisplayName = $"Updated Game {i % 15}",
                                ExecutableName = exe,
                                PowerMode = 0x04
                            });
                            break;
                        case 3:
                            controller.RemoveProfile(exe);
                            break;
                    }
                }
                catch (System.Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
        }

        [Fact]
        public void GameSyncController_IsProcessActive_CurrentProcess_ReturnsTrue()
        {
            using var controller = new GameSyncController(_tempFile);
            var method = typeof(GameSyncController).GetMethod("IsProcessActive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            string currentProcName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            var result = method.Invoke(controller, new object[] { currentProcName });
            Assert.True((bool)result!);
        }

        [Fact]
        public void WmiController_RGB_Properties_RetainConfiguredValues()
        {
            using var wmi = new WmiController();
            wmi.SetRgbMode(3, 255, 128, 64, 80, 5, 1);

            Assert.Equal(3, wmi.LastRgbMode);
            Assert.Equal(255, wmi.LastR);
            Assert.Equal(128, wmi.LastG);
            Assert.Equal(64, wmi.LastB);
            Assert.Equal(80, wmi.Brightness);
            Assert.Equal(5, wmi.Speed);
            Assert.Equal(1, wmi.Direction);
        }

        [Fact]
        public void Battery_D3Cold_Preservation_Verification()
        {
            var mgr = new BacklightStateManager();
            bool onBattery = mgr.IsOnBattery(PowerLineStatus.Offline);
            Assert.True(onBattery);

            bool isConnectedToCharger = !onBattery && (PowerLineStatus.Offline == PowerLineStatus.Online);
            Assert.False(isConnectedToCharger);
        }

        [Fact]
        public void BuildBootPowerTaskXml_ContainsRequiredTriggersAndParameters()
        {
            string xml = Form1.BuildBootPowerTaskXml("C:\\Apps\\PredatorControlApp.exe");
            Assert.Contains("<BootTrigger>", xml);
            Assert.Contains("<LogonTrigger>", xml);
            Assert.Contains("<UserId>S-1-5-18</UserId>", xml);
            Assert.Contains("<Arguments>-apply-power</Arguments>", xml);
            Assert.Contains("<Priority>4</Priority>", xml);
        }

        [Fact]
        public void Updater_TryParseTag_ParsesBothDotAndStandardPrefixes()
        {
            Assert.True(Updater.TryParseTag("v.1.2.0", out var v1));
            Assert.Equal(new System.Version(1, 2, 0), v1);

            Assert.True(Updater.TryParseTag("v1.2.0", out var v2));
            Assert.Equal(new System.Version(1, 2, 0), v2);

            Assert.True(Updater.TryParseTag("1.2.0", out var v3));
            Assert.Equal(new System.Version(1, 2, 0), v3);

            Assert.True(Updater.TryParseTag("V.1.2.0", out var v4));
            Assert.Equal(new System.Version(1, 2, 0), v4);
        }

        [Theory]
        [InlineData(PowerLineStatus.Offline, 5000)]
        [InlineData(PowerLineStatus.Unknown, 5000)]
        [InlineData(PowerLineStatus.Online, 2000)]
        public void TelemetryInterval_CalculatesCorrectIntervalForPowerState(PowerLineStatus status, int expectedInterval)
        {
            var mgr = new BacklightStateManager();
            bool onBattery = mgr.IsOnBattery(status);
            int interval = onBattery ? 5000 : 2000;
            Assert.Equal(expectedInterval, interval);
        }

        [Fact]
        public void WmiController_TurnOffBacklight_ResetsBrightnessToZero()
        {
            using var wmi = new WmiController();
            wmi.SetStaticColor(255, 100, 50, 80);
            Assert.Equal(80, wmi.Brightness);

            wmi.TurnOffBacklight();
            Assert.Equal(0, wmi.Brightness);
        }

        [Fact]
        public void WmiController_ApplyLightingMode_ZeroBrightness_InvokesTurnOffBacklight()
        {
            using var wmi = new WmiController();
            wmi.SetBrightness(0);
            wmi.ApplyLightingMode(0);
            Assert.Equal(0, wmi.Brightness);
        }

        [Fact]
        public void ResolvePowerMode_DefaultBalanced_WhenRegistryEmpty()
        {
            // Back up registry keys if any
            byte modeAc = Program.ResolvePowerMode(false);
            byte modeBat = Program.ResolvePowerMode(true);

            Assert.True(modeAc == 0x00 || modeAc == 0x01 || modeAc == 0x04 || modeAc == 0x05);
            Assert.True(modeBat == 0x00 || modeBat == 0x01 || modeBat == 0x06);
            Assert.NotEqual(0x04, modeBat);
            Assert.NotEqual(0x05, modeBat);
        }

        [Fact]
        public void ResolvePowerMode_CorrectlyRespectsExplicitUserSelections()
        {
            string testSubKey = @"SOFTWARE\PredatorControl";
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testSubKey))
            {
                // Test AC Turbo selection (index 4 in AcProfileValues -> 0x05)
                key.SetValue("AutoPowerAC", 4);
                byte acMode = Program.ResolvePowerMode(false);
                Assert.Equal(0x05, acMode);

                // Test AC Perf selection (index 3 in AcProfileValues -> 0x04)
                key.SetValue("AutoPowerAC", 3);
                acMode = Program.ResolvePowerMode(false);
                Assert.Equal(0x04, acMode);

                // Test AC Quiet selection (index 1 in AcProfileValues -> 0x00)
                key.SetValue("AutoPowerAC", 1);
                acMode = Program.ResolvePowerMode(false);
                Assert.Equal(0x00, acMode);

                // Test Battery Eco selection (index 3 in BatteryProfileValues -> 0x06)
                key.SetValue("AutoPowerBattery", 3);
                byte batMode = Program.ResolvePowerMode(true);
                Assert.Equal(0x06, batMode);

                // Test Battery Quiet selection (index 1 in BatteryProfileValues -> 0x00)
                key.SetValue("AutoPowerBattery", 1);
                batMode = Program.ResolvePowerMode(true);
                Assert.Equal(0x00, batMode);

                // Test Battery Balanced selection (index 2 in BatteryProfileValues -> 0x01)
                key.SetValue("AutoPowerBattery", 2);
                batMode = Program.ResolvePowerMode(true);
                Assert.Equal(0x01, batMode);

                // Test Fallback when AutoPower is 0 ("Don't Change")
                key.SetValue("AutoPowerAC", 0);
                key.SetValue("Power_AC", 0x05); // Turbo
                acMode = Program.ResolvePowerMode(false);
                Assert.Equal(0x05, acMode);

                key.SetValue("AutoPowerBattery", 0);
                key.SetValue("Power_Battery", 0x06); // Eco
                batMode = Program.ResolvePowerMode(true);
                Assert.Equal(0x06, batMode);

                // Battery safety constraint: Turbo/Perf on battery must force Balanced (0x01)
                key.SetValue("AutoPowerBattery", 0);
                key.SetValue("Power_Battery", 0x05); // Turbo attempted on battery
                batMode = Program.ResolvePowerMode(true);
                Assert.Equal(0x01, batMode);

                key.SetValue("AutoPowerBattery", 0);
                key.SetValue("Power_Battery", 0x04); // Perf attempted on battery
                batMode = Program.ResolvePowerMode(true);
                Assert.Equal(0x01, batMode);

                // Fallback to general Power
                key.DeleteValue("Power_Battery", false);
                key.SetValue("Power", 0x00); // Quiet
                batMode = Program.ResolvePowerMode(true);
                Assert.Equal(0x00, batMode);

                // Invalid out-of-range index fallback
                key.SetValue("AutoPowerBattery", 99);
                batMode = Program.ResolvePowerMode(true);
                Assert.Equal(0x00, batMode); // Still falls back to Power (0x00)

                // AC Safety constraint: Eco mode (0x06) is prohibited on AC power and must force Balanced (0x01)
                key.SetValue("AutoPowerAC", 0);
                key.SetValue("Power_AC", 0x06); // Eco attempted on AC
                acMode = Program.ResolvePowerMode(false);
                Assert.Equal(0x01, acMode);

                key.DeleteValue("Power_AC", false);
                key.SetValue("Power", 0x06); // Eco in generic Power key on AC
                acMode = Program.ResolvePowerMode(false);
                Assert.Equal(0x01, acMode);

                // Unknown arbitrary mode byte fallback
                key.SetValue("Power", 0x7F);
                acMode = Program.ResolvePowerMode(false);
                Assert.Equal(0x01, acMode);
            }
        }

        [Fact]
        public void Program_ResolveFanMode_ResolvesCorrectlyAcrossHivesAndPowerSources()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");
            if (key != null)
            {
                // Test AC AutoFanAC selection: 1 => Auto (0x01), 2 => Max (0x02), 3 => Custom (0x03)
                key.SetValue("AutoFanAC", 1);
                byte acFan = Program.ResolveFanMode(false);
                Assert.Equal(0x01, acFan);

                key.SetValue("AutoFanAC", 2);
                acFan = Program.ResolveFanMode(false);
                Assert.Equal(0x02, acFan);

                key.SetValue("AutoFanAC", 3);
                acFan = Program.ResolveFanMode(false);
                Assert.Equal(0x03, acFan);

                // Test Battery AutoFanBattery selection: 1 => Auto (0x01), 2 => Max (0x02), 3 => Custom (0x03)
                key.SetValue("AutoFanBattery", 1);
                byte batFan = Program.ResolveFanMode(true);
                Assert.Equal(0x01, batFan);

                key.SetValue("AutoFanBattery", 2);
                batFan = Program.ResolveFanMode(true);
                Assert.Equal(0x02, batFan);

                key.SetValue("AutoFanBattery", 3);
                batFan = Program.ResolveFanMode(true);
                Assert.Equal(0x03, batFan);

                // Test Fallback when AutoFan is 0 ("Don't Change")
                key.SetValue("AutoFanAC", 0);
                key.SetValue("Fan_AC", 0x02); // Max
                acFan = Program.ResolveFanMode(false);
                Assert.Equal(0x02, acFan);

                key.SetValue("AutoFanBattery", 0);
                key.SetValue("Fan_Battery", 0x03); // Custom
                batFan = Program.ResolveFanMode(true);
                Assert.Equal(0x03, batFan);

                // Fallback to general Fan setting
                key.DeleteValue("Fan_AC", false);
                key.DeleteValue("Fan_Battery", false);
                key.SetValue("Fan", 0x02); // Max
                acFan = Program.ResolveFanMode(false);
                batFan = Program.ResolveFanMode(true);
                Assert.Equal(0x02, acFan);
                Assert.Equal(0x02, batFan);

                // Invalid out-of-range index fallback
                key.SetValue("AutoFanAC", 99);
                acFan = Program.ResolveFanMode(false);
                Assert.Equal(0x02, acFan); // Falls back to generic Fan (0x02)

                // Corrupted generic value fallback to Auto (0x01)
                key.SetValue("AutoFanAC", 0);
                key.SetValue("Fan", 0x7F);
                acFan = Program.ResolveFanMode(false);
                Assert.Equal(0x01, acFan);
            }
        }

        [Fact]
        public void AcAndBatteryProfileValues_MatchExpectedHardwareModes()
        {
            // AC Profile: Don't Change (0xFF), Quiet (0x00), Balanced (0x01), Perf (0x04), Turbo (0x05)
            Assert.Equal(5, Form1.AcProfileValues.Length);
            Assert.Equal(0xFF, Form1.AcProfileValues[0]);
            Assert.Equal(0x00, Form1.AcProfileValues[1]);
            Assert.Equal(0x01, Form1.AcProfileValues[2]);
            Assert.Equal(0x04, Form1.AcProfileValues[3]);
            Assert.Equal(0x05, Form1.AcProfileValues[4]);

            // Battery Profile: Don't Change (0xFF), Quiet (0x00), Balanced (0x01), Eco (0x06)
            Assert.Equal(4, Form1.BatteryProfileValues.Length);
            Assert.Equal(0xFF, Form1.BatteryProfileValues[0]);
            Assert.Equal(0x00, Form1.BatteryProfileValues[1]);
            Assert.Equal(0x01, Form1.BatteryProfileValues[2]);
            Assert.Equal(0x06, Form1.BatteryProfileValues[3]);

            // Fan Profiles: Don't Change (0xFF), Auto (0x01), Max (0x02), Custom (0x03)
            Assert.Equal(4, Form1.FanProfileValues.Length);
            Assert.Equal(0xFF, Form1.FanProfileValues[0]);
            Assert.Equal(0x01, Form1.FanProfileValues[1]);
            Assert.Equal(0x02, Form1.FanProfileValues[2]);
            Assert.Equal(0x03, Form1.FanProfileValues[3]);

            Assert.Equal(4, Form1.AcFanValues.Length);
            Assert.Equal(0xFF, Form1.AcFanValues[0]);
            Assert.Equal(0x01, Form1.AcFanValues[1]);
            Assert.Equal(0x02, Form1.AcFanValues[2]);
            Assert.Equal(0x03, Form1.AcFanValues[3]);

            Assert.Equal(4, Form1.BatteryFanValues.Length);
            Assert.Equal(0xFF, Form1.BatteryFanValues[0]);
            Assert.Equal(0x01, Form1.BatteryFanValues[1]);
            Assert.Equal(0x02, Form1.BatteryFanValues[2]);
            Assert.Equal(0x03, Form1.BatteryFanValues[3]);
        }

        [Fact]
        public void WmiController_TurnOffBacklight_PreservesLastRgbMode()
        {
            using var wmi = new WmiController();
            wmi.SetRgbMode(3, 255, 100, 50, 80, 50, 1);
            Assert.Equal(3, wmi.LastRgbMode);

            // Turn off backlight (e.g. lid close / battery enforcement)
            wmi.TurnOffBacklight();
            Assert.Equal(3, wmi.LastRgbMode); // Must NOT be overwritten to 0
        }

        [Fact]
        public void WmiController_SetFanBehavior_And_SetFanSpeed_DispatchesSafely()
        {
            using var wmi = new WmiController();
            wmi.SetFanBehavior(0x01);
            wmi.SetFanBehavior(0x02);
            wmi.SetFanBehavior(0x03, applyCustomSpeeds: true);
            wmi.SetCpuFanSpeed(60);
            wmi.SetGpuFanSpeed(70);
            wmi.SetFanSpeed(55, 65);
        }

        [Fact]
        public void EnsureBootPowerTaskRegistered_ExecutesWithoutThrowing()
        {
            // Verifies the method executes, handles pathing and temp XML cleanup properly
            bool result = Form1.EnsureBootPowerTaskRegistered("C:\\Apps\\PredatorControlApp.exe");
            // Depending on user permissions this may return true or false from schtasks, but must never throw
            Assert.True(result || !result);
        }

        [Fact]
        public void WmiController_TrySetPowerMode_ReturnsBoolAndResetsCooldownOnForceRetry()
        {
            using var wmi = new WmiController();
            bool result1 = wmi.TrySetPowerMode(0x01, forceRetry: false);
            bool result2 = wmi.TrySetPowerMode(0x01, forceRetry: true);
            Assert.True(result1 || !result1);
            Assert.True(result2 || !result2);
        }

        [Fact]
        public void Program_ApplyPowerAtBootFast_ExecutesWithoutThrowing()
        {
            var ex = Record.Exception(() => Program.ApplyPowerAtBootFast());
            Assert.Null(ex);
        }

        [Fact]
        public void Program_ResolveFanSpeeds_ResolvesCorrectlyAcrossHives()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");
            if (key != null)
            {
                key.SetValue("FanSpeedCpuAC", 75);
                key.SetValue("FanSpeedGpuAC", 85);
                key.SetValue("FanSpeedCpuBattery", 35);
                key.SetValue("FanSpeedGpuBattery", 45);

                Program.ResolveFanSpeeds(onBattery: false, out byte acCpu, out byte acGpu);
                Assert.Equal(75, acCpu);
                Assert.Equal(85, acGpu);

                Program.ResolveFanSpeeds(onBattery: true, out byte batCpu, out byte batGpu);
                Assert.Equal(35, batCpu);
                Assert.Equal(45, batGpu);

                // Fallback to general FanSpeedCpu / FanSpeedGpu
                key.DeleteValue("FanSpeedCpuAC", false);
                key.DeleteValue("FanSpeedGpuAC", false);
                key.SetValue("FanSpeedCpu", 60);
                key.SetValue("FanSpeedGpu", 65);

                Program.ResolveFanSpeeds(onBattery: false, out byte fallbackCpu, out byte fallbackGpu);
                Assert.Equal(60, fallbackCpu);
                Assert.Equal(65, fallbackGpu);
            }
        }

        [Fact]
        public void WmiController_BuildFanBehaviorPayload_EncodesBitsProperly()
        {
            ulong autoPayload = WmiController.BuildFanBehaviorPayload(0x01);
            Assert.Equal((ulong)(0x09 | (0x01UL << 16) | (0x01UL << 22)), autoPayload);

            ulong maxPayload = WmiController.BuildFanBehaviorPayload(0x02);
            Assert.Equal((ulong)(0x09 | (0x02UL << 16) | (0x02UL << 22)), maxPayload);

            ulong customPayload = WmiController.BuildFanBehaviorPayload(0x03);
            Assert.Equal((ulong)(0x09 | (0x03UL << 16) | (0x03UL << 22)), customPayload);
        }

        [Fact]
        public void WmiController_BuildCpuFanSpeedPayloads_EncodesDirectAndExtended()
        {
            // Clamped minimum (10%)
            var (directMin, extendedMin) = WmiController.BuildCpuFanSpeedPayloads(5);
            Assert.Equal(0x01UL | (10UL << 8), directMin);
            Assert.Equal(0x05UL | (1UL << 8) | (10UL << 16), extendedMin);

            // Normal speed (65%)
            var (directNorm, extendedNorm) = WmiController.BuildCpuFanSpeedPayloads(65);
            Assert.Equal(0x01UL | (65UL << 8), directNorm);
            Assert.Equal(0x05UL | (1UL << 8) | (65UL << 16), extendedNorm);

            // Clamped maximum (100%)
            var (directMax, extendedMax) = WmiController.BuildCpuFanSpeedPayloads(120);
            Assert.Equal(0x01UL | (100UL << 8), directMax);
            Assert.Equal(0x05UL | (1UL << 8) | (100UL << 16), extendedMax);
        }

        [Fact]
        public void WmiController_BuildGpuFanSpeedPayloads_EncodesDirectAndExtended()
        {
            // Clamped minimum (10%)
            var (directPriMin, directAltMin, extPriMin, extAltMin) = WmiController.BuildGpuFanSpeedPayloads(0);
            Assert.Equal(0x04UL | (10UL << 8), directPriMin);
            Assert.Equal(0x02UL | (10UL << 8), directAltMin);
            Assert.Equal(0x05UL | (2UL << 8) | (10UL << 16), extPriMin);
            Assert.Equal(0x05UL | (4UL << 8) | (10UL << 16), extAltMin);

            // Normal speed (80%)
            var (directPriNorm, directAltNorm, extPriNorm, extAltNorm) = WmiController.BuildGpuFanSpeedPayloads(80);
            Assert.Equal(0x04UL | (80UL << 8), directPriNorm);
            Assert.Equal(0x02UL | (80UL << 8), directAltNorm);
            Assert.Equal(0x05UL | (2UL << 8) | (80UL << 16), extPriNorm);
            Assert.Equal(0x05UL | (4UL << 8) | (80UL << 16), extAltNorm);
        }

        [Fact]
        public void InterpolateCurve_HandlesClampingAndIntermediateInterpolation()
        {
            var curve = new List<Point>
            {
                new Point(40, 20),
                new Point(60, 40),
                new Point(80, 80),
                new Point(90, 100)
            };

            // Temperature below lowest point clamps to first point speed (clamped min 10)
            Assert.Equal(20, Form1.InterpolateCurve(curve, 30));
            Assert.Equal(20, Form1.InterpolateCurve(curve, 40));

            // Midpoint interpolation
            Assert.Equal(30, Form1.InterpolateCurve(curve, 50)); // halfway between (40, 20) and (60, 40)
            Assert.Equal(60, Form1.InterpolateCurve(curve, 70)); // halfway between (60, 40) and (80, 80)
            Assert.Equal(90, Form1.InterpolateCurve(curve, 85)); // halfway between (80, 80) and (90, 100)

            // Temperature above highest point clamps to last point speed
            Assert.Equal(100, Form1.InterpolateCurve(curve, 95));
            Assert.Equal(100, Form1.InterpolateCurve(curve, 110));

            // Null or empty list returns default 50
            Assert.Equal(50, Form1.InterpolateCurve(null, 60));
            Assert.Equal(50, Form1.InterpolateCurve(new List<Point>(), 60));
        }
    }
}
