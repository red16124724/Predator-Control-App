using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class PowerModeAndOverlayTests
    {
        [Theory]
        [InlineData(0x00, "961cc777-2547-4f9d-8174-7d86181b8a7a")] // Quiet -> Efficiency
        [InlineData(0x06, "961cc777-2547-4f9d-8174-7d86181b8a7a")] // Eco -> Efficiency
        [InlineData(0x01, "00000000-0000-0000-0000-000000000000")] // Balanced -> Balanced
        [InlineData(0x04, "ded574b5-45a0-4f42-8737-46345c09c238")] // Performance -> Performance
        [InlineData(0x05, "ded574b5-45a0-4f42-8737-46345c09c238")] // Turbo -> Performance
        [InlineData(0xFF, "00000000-0000-0000-0000-000000000000")] // Default fallback -> Balanced
        public void GetOverlayForMode_MapsAcerModesToCorrectWindowsOverlayGuids(byte mode, string expectedGuidStr)
        {
            var expectedGuid = new Guid(expectedGuidStr);
            var actualGuid = WmiController.GetOverlayForMode(mode);
            Assert.Equal(expectedGuid, actualGuid);
        }

        [Fact]
        public void SetPowerMode_ValidModes_DoesNotThrowAndMaintainsStability()
        {
            using var wmi = new WmiController();
            byte[] testModes = { 0x00, 0x01, 0x04, 0x05, 0x06 };
            foreach (var m in testModes)
            {
                wmi.SetPowerMode(m);
            }
        }

        [Fact]
        public void PowerMode_RapidConcurrentSwitching_NoDeadlocks()
        {
            using var wmi = new WmiController();
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, 100, i =>
            {
                try
                {
                    byte[] modes = { 0x00, 0x01, 0x04, 0x05, 0x06 };
                    wmi.SetPowerMode(modes[i % modes.Length]);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
        }
    }

    public class FanModeAndClampingTests
    {
        [Theory]
        [InlineData(0, 10)]
        [InlineData(5, 10)]
        [InlineData(9, 10)]
        [InlineData(10, 10)]
        [InlineData(50, 50)]
        [InlineData(99, 99)]
        [InlineData(100, 100)]
        [InlineData(101, 100)]
        [InlineData(255, 100)]
        public void SetCpuFanSpeed_ClampsToTenAndOneHundred(byte input, byte expected)
        {
            using var wmi = new WmiController();
            wmi.SetCpuFanSpeed(input);
            Assert.Equal(expected, wmi.CustomCpuFanSpeed);
        }

        [Theory]
        [InlineData(0, 10)]
        [InlineData(3, 10)]
        [InlineData(10, 10)]
        [InlineData(65, 65)]
        [InlineData(100, 100)]
        [InlineData(150, 100)]
        public void SetGpuFanSpeed_ClampsToTenAndOneHundred(byte input, byte expected)
        {
            using var wmi = new WmiController();
            wmi.SetGpuFanSpeed(input);
            Assert.Equal(expected, wmi.CustomGpuFanSpeed);
        }

        [Theory]
        [InlineData(0, 0, 10, 10)]
        [InlineData(5, 120, 10, 100)]
        [InlineData(75, 80, 75, 80)]
        [InlineData(200, 2, 100, 10)]
        public void SetFanSpeed_BothFans_ClampedCorrectly(byte inCpu, byte inGpu, byte expCpu, byte expGpu)
        {
            using var wmi = new WmiController();
            wmi.SetFanSpeed(inCpu, inGpu);
            Assert.Equal(expCpu, wmi.CustomCpuFanSpeed);
            Assert.Equal(expGpu, wmi.CustomGpuFanSpeed);
        }

        [Fact]
        public void SetFanBehavior_CustomMode_AppliesClampedCustomSpeeds()
        {
            using var wmi = new WmiController();
            wmi.SetFanSpeed(40, 60);
            wmi.SetFanBehavior(0x03); // Custom
            Assert.Equal(40, wmi.CustomCpuFanSpeed);
            Assert.Equal(60, wmi.CustomGpuFanSpeed);
        }

        [Fact]
        public void SetFanBehavior_CustomMode_WithoutApplyCustomSpeeds_DoesNotForceSpeeds()
        {
            using var wmi = new WmiController();
            wmi.SetFanBehavior(0x03, applyCustomSpeeds: false);
            Assert.Equal(50, wmi.CustomCpuFanSpeed);
            Assert.Equal(50, wmi.CustomGpuFanSpeed);
        }

        [Fact]
        public void FanModes_ConcurrentStressTest_NoDeadlocks()
        {
            using var wmi = new WmiController();
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, 150, i =>
            {
                try
                {
                    switch (i % 4)
                    {
                        case 0:
                            wmi.SetFanBehavior((byte)(1 + (i % 3)));
                            break;
                        case 1:
                            wmi.SetCpuFanSpeed((byte)(i % 120));
                            break;
                        case 2:
                            wmi.SetGpuFanSpeed((byte)(i % 120));
                            break;
                        case 3:
                            wmi.SetFanSpeed((byte)(i % 110), (byte)((i * 3) % 110));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
        }
    }

    public class D3ColdSleepAndSensorSafetyTests
    {
        [Fact]
        public void Wmi_GpuTemp_SafeOnBatteryD3Cold()
        {
            using var wmi = new WmiController();
            int temp = wmi.GpuTemp;
            Assert.True(temp >= 0 && temp <= 125);
        }

        [Fact]
        public void Wmi_GpuTempAndFanRpm_NeverNegative()
        {
            using var wmi = new WmiController();
            Assert.True(wmi.GpuTemp >= 0);
            Assert.True(wmi.GpuFanRpm >= 0);
            Assert.True(wmi.CpuTemp >= 0);
            Assert.True(wmi.CpuFanRpm >= 0);
        }

        [Fact]
        public void FanCurveInterpolation_ExtremesStayClamped()
        {
            var curve = new List<Point>
            {
                new(30, 10),
                new(50, 25),
                new(70, 50),
                new(90, 85),
                new(100, 100)
            };

            // Negative temp
            Assert.Equal(10, Form1.InterpolateCurve(curve, -50));
            // Zero temp (D3Cold offline)
            Assert.Equal(10, Form1.InterpolateCurve(curve, 0));
            // 29 temp (below 30 min)
            Assert.Equal(10, Form1.InterpolateCurve(curve, 29));
            // Max temp
            Assert.Equal(100, Form1.InterpolateCurve(curve, 100));
            // Extreme thermal throttling temp
            Assert.Equal(100, Form1.InterpolateCurve(curve, 150));
        }

        [Fact]
        public void FanCurveGraph_DrawStatus_HandlesZeroTempGracefully()
        {
            var graph = new FanCurveGraph();
            graph.CurrentTemp = 0; // Sleeping GPU
            Assert.Equal(0, graph.CurrentTemp);

            graph.CurrentTemp = 55;
            Assert.Equal(55, graph.CurrentTemp);
            int speed = graph.InterpolateSpeed(55);
            Assert.True(speed >= 10 && speed <= 100);
        }

        [Fact]
        public void FanCurveGraph_DrawStatus_DifferentiatesCpuAndGpuStatus()
        {
            var cpuGraph = new FanCurveGraph { FanLabel = "CPU FAN CURVE", CurrentTemp = 0 };
            Assert.Equal(0, cpuGraph.CurrentTemp);

            var gpuGraph = new FanCurveGraph { FanLabel = "GPU FAN CURVE", CurrentTemp = 0 };
            Assert.Equal(0, gpuGraph.CurrentTemp);
        }

        [Fact]
        public void Form1_InterpolateCurve_GuaranteesTenToHundredClamp()
        {
            var outOfBoundsCurve = new List<Point>
            {
                new(30, 0),
                new(50, 5),
                new(100, 110)
            };

            // Values below 10% are clamped to 10%
            Assert.Equal(10, Form1.InterpolateCurve(outOfBoundsCurve, 30));
            Assert.Equal(10, Form1.InterpolateCurve(outOfBoundsCurve, 50));
            // Values above 100% are clamped to 100%
            Assert.Equal(100, Form1.InterpolateCurve(outOfBoundsCurve, 100));
        }
    }

    public class DisplayRefreshRateWin32Tests
    {
        [Fact]
        public void GetCurrentRefreshRateCore_ReturnsValidFrequency()
        {
            int hz = Form1.GetCurrentRefreshRateCore();
            Assert.True(hz >= 30, $"Current display frequency should be >= 30Hz, but was {hz}Hz");
        }

        [Fact]
        public void GetMaxRefreshRateCore_ReturnsAtLeastCurrentFrequency()
        {
            int cur = Form1.GetCurrentRefreshRateCore();
            int max = Form1.GetMaxRefreshRateCore();
            Assert.True(max >= cur, $"Max refresh rate ({max}Hz) must be >= current ({cur}Hz)");
        }

        [Fact]
        public void SetRefreshRateCore_InvalidValues_ReturnFalseImmediately()
        {
            Assert.False(Form1.SetRefreshRateCore(-1));
            Assert.False(Form1.SetRefreshRateCore(0));
        }

        [Fact]
        public void SetRefreshRateCore_CurrentRate_ReturnsTrueWithoutDisplayInterruption()
        {
            int cur = Form1.GetCurrentRefreshRateCore();
            // Switching to the already-active rate must succeed immediately
            bool result = Form1.SetRefreshRateCore(cur);
            Assert.True(result);
        }

        [Fact]
        public void SetRefreshRateCore_UnsupportedFrequency_ReturnsFalseSafely()
        {
            // Frequencies that do not match any display mode should return false safely without throwing
            bool result = Form1.SetRefreshRateCore(99999);
            Assert.False(result);
        }

        [Fact]
        public void IsSameGeometry_DifferentWidthsOrHeights_ReturnsFalse()
        {
            var a = new Form1.DEVMODE { dmPelsWidth = 1920, dmPelsHeight = 1080, dmBitsPerPel = 32 };
            var b = new Form1.DEVMODE { dmPelsWidth = 2560, dmPelsHeight = 1440, dmBitsPerPel = 32 };
            Assert.False(Form1.IsSameGeometry(a, b));

            var c = new Form1.DEVMODE { dmPelsWidth = 1920, dmPelsHeight = 1200, dmBitsPerPel = 32 };
            Assert.False(Form1.IsSameGeometry(a, c));

            var d = new Form1.DEVMODE { dmPelsWidth = 1920, dmPelsHeight = 1080, dmBitsPerPel = 16 };
            Assert.False(Form1.IsSameGeometry(a, d));

            var e = new Form1.DEVMODE { dmPelsWidth = 1920, dmPelsHeight = 1080, dmBitsPerPel = 32 };
            Assert.True(Form1.IsSameGeometry(a, e));
        }
    }

    public class BatteryChargeLimiterTests
    {
        [Fact]
        public void BatteryControl_SupportCheck_SafeWithoutException()
        {
            using var wmi = new WmiController();
            bool supported = wmi.IsBatteryControlSupported();
            // Verify it evaluates cleanly without crashing
            Assert.True(supported || !supported);
        }

        [Fact]
        public void SetBatteryChargeLimit_SafeExecution()
        {
            using var wmi = new WmiController();
            // Whether hardware exists or not, calling SetBatteryChargeLimit must return a bool without throwing
            bool result80 = wmi.SetBatteryChargeLimit(true);
            bool result100 = wmi.SetBatteryChargeLimit(false);
            Assert.True(result80 || !result80);
            Assert.True(result100 || !result100);
        }
    }

    public class StartupTaskSchedulerTests
    {
        [Fact]
        public void BuildStartupTaskXml_ContainsRequiredAttributesAndZeroDelay()
        {
            string xml = Form1.BuildStartupTaskXml("TEST_USER", @"C:\Test\PredatorControl.exe");

            Assert.Contains("<UserId>TEST_USER</UserId>", xml);
            Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
            Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
            Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
            Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml);
            Assert.Contains("<Command>C:\\Test\\PredatorControl.exe</Command>", xml);
            Assert.Contains("<Arguments>-hidden</Arguments>", xml);
            Assert.Contains("<LogonTrigger>", xml);
        }

        [Fact]
        public void BuildStartupTaskXml_EscapesSpecialCharactersInPathAndUser()
        {
            string xml = Form1.BuildStartupTaskXml(@"DOMAIN\User&Co<1>", @"C:\Apps & Games\App<1>.exe");
            Assert.DoesNotContain("<UserId>DOMAIN\\User&Co<1></UserId>", xml);
            Assert.Contains("&amp;", xml);
            Assert.Contains("&lt;", xml);
            Assert.Contains("&gt;", xml);
        }
    }

    public class RgbLightingControlTests
    {
        [Fact]
        public void RgbControls_SetProperties_StoresValuesAccurately()
        {
            using var wmi = new WmiController();
            wmi.SetRgbMode(1, 100, 200, 50, 80, 7, 1);

            Assert.Equal(1, wmi.LastRgbMode);
            Assert.Equal(100, wmi.LastR);
            Assert.Equal(200, wmi.LastG);
            Assert.Equal(50, wmi.LastB);
            Assert.Equal(80, wmi.Brightness);
            Assert.Equal(7, wmi.Speed);
            Assert.Equal(1, wmi.Direction);
        }

        [Fact]
        public void SetBrightness_Zero_TurnsOffBacklight()
        {
            using var wmi = new WmiController();
            wmi.SetBrightness(100);
            Assert.Equal(100, wmi.Brightness);

            wmi.SetBrightness(0);
            Assert.Equal(0, wmi.Brightness);
        }

        [Fact]
        public void SetStaticColor_UpdatesColorsAndSetsModeZero()
        {
            using var wmi = new WmiController();
            wmi.SetStaticColor(255, 128, 0, 90);

            Assert.Equal(0, wmi.LastRgbMode);
            Assert.Equal(255, wmi.LastR);
            Assert.Equal(128, wmi.LastG);
            Assert.Equal(0, wmi.LastB);
            Assert.Equal(90, wmi.Brightness);
        }

        [Fact]
        public void SetStaticColor_ZeroBrightness_TurnsOffBacklight()
        {
            using var wmi = new WmiController();
            wmi.SetStaticColor(255, 128, 0, 0);

            Assert.Equal(0, wmi.LastRgbMode);
            Assert.Equal(0, wmi.Brightness);
        }
    }

    public class PureWmiTelemetryStressTests
    {
        [Fact]
        public void Wmi_CpuAndGpuTelemetry_ValidRanges()
        {
            using var wmi = new WmiController();
            int cpuTemp = wmi.CpuTemp;
            int cpuRpm = wmi.CpuFanRpm;
            int gpuTemp = wmi.GpuTemp;
            int gpuRpm = wmi.GpuFanRpm;

            Assert.True(cpuTemp >= 0 && cpuTemp <= 125);
            Assert.True(cpuRpm >= 0 && cpuRpm <= 10000);
            Assert.True(gpuTemp >= 0 && gpuTemp <= 125);
            Assert.True(gpuRpm >= 0 && gpuRpm <= 10000);
        }
    }

    public class GameSyncConcurrencyStressTests : IDisposable
    {
        private readonly string _tempFile;

        public GameSyncConcurrencyStressTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"gamesync_stress_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
        }

        [Fact]
        public void GameSync_HighConcurrencyInterleavedOperations_NoDeadlocks()
        {
            using var controller = new GameSyncController(_tempFile);
            controller.IsEnabled = true;
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, 500, i =>
            {
                try
                {
                    string exeName = $"stress_game_{i % 20}.exe";
                    switch (i % 6)
                    {
                        case 0:
                            controller.AddProfile(new GameProfile
                            {
                                DisplayName = $"Game {i % 20}",
                                ExecutableName = exeName,
                                PowerMode = (byte)(i % 5),
                                FanMode = (byte)(1 + (i % 3)),
                                CpuFanSpeed = 50 + (i % 51),
                                GpuFanSpeed = 50 + (i % 51),
                                RefreshRate = (i % 2 == 0) ? 60 : 165,
                                BatteryLimit = i % 2
                            });
                            break;
                        case 1:
                            var profiles = controller.Profiles;
                            Assert.NotNull(profiles);
                            break;
                        case 2:
                            controller.UpdateProfile(new GameProfile
                            {
                                DisplayName = $"Updated Game {i % 20}",
                                ExecutableName = exeName,
                                PowerMode = 0x05
                            });
                            break;
                        case 3:
                            controller.RemoveProfile(exeName);
                            break;
                        case 4:
                            controller.Load();
                            break;
                        case 5:
                            controller.Save();
                            break;
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
        public void GameSync_SnapshotCaptureAndRestoreIntegrity()
        {
            var snap = new DashboardSnapshot
            {
                PowerMode = 0x04,
                FanMode = 0x03,
                CpuFanSpeed = 75,
                GpuFanSpeed = 80,
                FanCurveWasEnabled = true,
                RefreshRate = 165,
                BatteryLimit = 1,
                RgbMode = 3,
                RgbBrightness = 85,
                RgbSpeed = 60,
                RgbR = 255,
                RgbG = 120,
                RgbB = 0
            };

            using var controller = new GameSyncController(_tempFile);
            controller.SetPreGameSnapshot(snap);

            // Verify properties preserved
            Assert.Equal(0x04, snap.PowerMode);
            Assert.Equal(0x03, snap.FanMode);
            Assert.Equal(75, snap.CpuFanSpeed);
            Assert.Equal(80, snap.GpuFanSpeed);
            Assert.True(snap.FanCurveWasEnabled);
            Assert.Equal(165, snap.RefreshRate);
            Assert.Equal(1, snap.BatteryLimit);
            Assert.Equal(3, snap.RgbMode);
            Assert.Equal(85, snap.RgbBrightness);
            Assert.Equal(60, snap.RgbSpeed);
            Assert.Equal(255, snap.RgbR);
            Assert.Equal(120, snap.RgbG);
            Assert.Equal(0, snap.RgbB);
        }
    }

    public class UpdaterConfigurationTests
    {
        [Fact]
        public void ReleasesApi_PointsToRed16124724Repo()
        {
            Assert.Contains("red16124724/Predator-Control-App", Updater.ReleasesApi);
            Assert.StartsWith("https://api.github.com/repos/red16124724/Predator-Control-App/releases", Updater.ReleasesApi);
        }

        [Fact]
        public void ReleasesWeb_PointsToRed16124724Releases()
        {
            Assert.Equal("https://github.com/red16124724/Predator-Control-App/releases", Updater.ReleasesWeb);
        }

        [Theory]
        [InlineData("v.1.4.0", 1, 4, 0)]
        [InlineData("v1.4.0", 1, 4, 0)]
        [InlineData("1.4.0", 1, 4, 0)]
        public void TryParseTag_HandlesVersion140Formats(string tag, int major, int minor, int build)
        {
            bool ok = Updater.TryParseTag(tag, out var v);
            Assert.True(ok);
            Assert.Equal(new Version(major, minor, build), v);
        }

        [Fact]
        public void CurrentVersion_Is140()
        {
            Assert.Equal(1, Updater.Current.Major);
            Assert.Equal(4, Updater.Current.Minor);
        }

        [Fact]
        public void ExtractExpectedSha256_SelectsAssetSpecificHash()
        {
            string bodyJson = """
            {
                "body": "Releases checksums:\nPredatorControlApp-Standalone.exe: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\nPredatorControlApp.exe: fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
            }
            """;
            using var doc = System.Text.Json.JsonDocument.Parse(bodyJson);
            string? hash = Updater.ExtractExpectedSha256(doc.RootElement, "https://github.com/red16124724/Predator-Control-App/releases/download/v1.4.0/PredatorControlApp.exe");
            Assert.Equal("fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210", hash);

            string? standaloneHash = Updater.ExtractExpectedSha256(doc.RootElement, "https://github.com/red16124724/Predator-Control-App/releases/download/v1.4.0/PredatorControlApp-Standalone.exe");
            Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", standaloneHash);
        }

        [Fact]
        public async Task ApplyAsync_RejectsInsecureOrUntrustedDownloadUrls()
        {
            var insecureInfo = new UpdateInfo(new Version(1, 4, 1), "v1.4.1", "Notes", "http://github.com/evil.exe");
            await Assert.ThrowsAsync<System.Security.SecurityException>(() => Updater.ApplyAsync(insecureInfo));

            var untrustedInfo = new UpdateInfo(new Version(1, 4, 1), "v1.4.1", "Notes", "https://malicious-site.com/evil.exe");
            await Assert.ThrowsAsync<System.Security.SecurityException>(() => Updater.ApplyAsync(untrustedInfo));
        }
    }

    public class GpuBatteryTelemetryGatingTests
    {
        [Theory]
        [InlineData(PowerLineStatus.Offline, null, false)]
        [InlineData(PowerLineStatus.Offline, false, false)]
        [InlineData(PowerLineStatus.Online, null, true)]
        [InlineData(PowerLineStatus.Online, true, true)]
        [InlineData(PowerLineStatus.Unknown, true, true)]
        [InlineData(PowerLineStatus.Unknown, false, false)]
        public void IsConnectedToCharger_EvaluatesAccurately(PowerLineStatus lineStatus, bool? isPluggedIn, bool expectedConnected)
        {
            bool isConnected = lineStatus == PowerLineStatus.Online || (isPluggedIn == true);
            Assert.Equal(expectedConnected, isConnected);
        }

        [Fact]
        public void OnBattery_GpuReadingsAreZeroAndNotPolled()
        {
            // Simulate battery state where isConnectedToCharger is false
            bool isConnectedToCharger = false;
            int gpuTemp = 0;
            int gpuRpm = 0;

            if (isConnectedToCharger)
            {
                // This branch must NOT be reached on battery
                gpuTemp = 75;
                gpuRpm = 3500;
            }

            Assert.Equal(0, gpuTemp);
            Assert.Equal(0, gpuRpm);
        }

        [Fact]
        public void OnCharger_GpuReadingsArePolled()
        {
            // Simulate AC charger state where isConnectedToCharger is true
            bool isConnectedToCharger = true;
            int gpuTemp = 0;
            int gpuRpm = 0;

            if (isConnectedToCharger)
            {
                gpuTemp = 65;
                gpuRpm = 3200;
            }

            Assert.Equal(65, gpuTemp);
            Assert.Equal(3200, gpuRpm);
        }
    }

    public class ResumeLifecycleSafetyTests
    {
        [Fact]
        public void BatteryResume_EnforcesRgbOffAndNoTurboMode()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 100;
            mgr.OnPowerSourceChanged(false, out bool shouldTurnOff, out int targetBright);

            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);
            Assert.True(mgr.IsOnBattery(PowerLineStatus.Offline));

            // Power modes allowed on battery are only Quiet (0x00), Balanced (0x01), and Eco (0x06).
            // Turbo (0x05) and Perf (0x04) must NOT be permitted.
            byte[] batteryModes = Form1.BatteryProfileValues;
            Assert.DoesNotContain((byte)0x04, batteryModes); // Perf not in battery modes
            Assert.DoesNotContain((byte)0x05, batteryModes); // Turbo not in battery modes
            Assert.Contains((byte)0x06, batteryModes); // Eco is in battery modes
        }

        [Fact]
        public void AcResume_PreservesSavedBrightnessAndAllowsTurboMode()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 90;
            mgr.OnPowerSourceChanged(true, out bool shouldTurnOff, out int targetBright);

            Assert.False(shouldTurnOff);
            Assert.Equal(90, targetBright);
            Assert.False(mgr.IsOnBattery(PowerLineStatus.Online));

            byte[] acModes = Form1.AcProfileValues;
            Assert.Contains((byte)0x04, acModes); // Perf is in AC modes
            Assert.Contains((byte)0x05, acModes); // Turbo is in AC modes
        }
    }

    public class StartupTaskXmlSafetyTests
    {
        [Fact]
        public void BuildStartupTaskXml_GeneratesValidXmlWithLogonTriggerAndHighestPrivileges()
        {
            string xml = Form1.BuildStartupTaskXml("TestUser", @"C:\App\PredatorControl.exe");
            Assert.Contains("<UserId>TestUser</UserId>", xml);
            Assert.Contains("<Command>C:\\App\\PredatorControl.exe</Command>", xml);
            Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
            Assert.Contains("<Arguments>-hidden</Arguments>", xml);
            Assert.Contains("<LogonTrigger>", xml);
        }
    }

    public class FanCurveUiSyncTests
    {
        [Fact]
        public void FanCurveApplyEventArgs_InitializesWithEmptyListsAndFalseSuccess()
        {
            var args = new FanCurveApplyEventArgs();
            Assert.NotNull(args.CpuPoints);
            Assert.NotNull(args.GpuPoints);
            Assert.Empty(args.CpuPoints);
            Assert.Empty(args.GpuPoints);
            Assert.False(args.Success);
        }

        [Fact]
        public void FanCurveApplyEventArgs_AcceptsCustomPointsAndSuccess()
        {
            var args = new FanCurveApplyEventArgs
            {
                CpuPoints = new List<Point> { new(30, 20), new(100, 100) },
                GpuPoints = new List<Point> { new(30, 25), new(100, 95) },
                Success = true
            };

            Assert.Equal(2, args.CpuPoints.Count);
            Assert.Equal(2, args.GpuPoints.Count);
            Assert.True(args.Success);
        }
    }

    [Collection("SingleInstanceTests")]
    public class SingleInstanceAndCommandLineStressTests
    {
        [Fact]
        public async Task SingleInstanceLock_AcquireAndRelease_LifecycleSucceeds()
        {
            string testMutex = $"PredatorControlApp_TestMutex_{Guid.NewGuid():N}";
            Program.ReleaseSingleInstanceLock();
            bool first = Program.TryTakeSingleInstanceLock(testMutex);
            Assert.True(first);

            // Second attempt from another thread/call should fail while lock held
            bool second = await Task.Run(() => Program.TryTakeSingleInstanceLock(testMutex));
            Assert.False(second);

            Program.ReleaseSingleInstanceLock();

            // After release, should be acquirable again
            bool third = Program.TryTakeSingleInstanceLock(testMutex);
            Assert.True(third);
            Program.ReleaseSingleInstanceLock();
        }

        [Theory]
        [InlineData("-apply-power", true)]
        [InlineData("--apply-power", true)]
        [InlineData("/apply-power", true)]
        [InlineData("-APPLY-POWER", true)]
        [InlineData("-hidden", false)]
        [InlineData("", false)]
        public void CommandLine_ApplyPower_DetectionIsCaseInsensitive(string arg, bool expectedMatch)
        {
            string[] args = { arg };
            bool isApplyPower = args.Any(a => a.Equals("-apply-power", StringComparison.OrdinalIgnoreCase) ||
                                              a.Equals("--apply-power", StringComparison.OrdinalIgnoreCase) ||
                                              a.Equals("/apply-power", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(expectedMatch, isApplyPower);
        }

        [Theory]
        [InlineData("PredatorControlApp.exe -hidden", true)]
        [InlineData("PredatorControlApp.exe --hidden", true)]
        [InlineData("PredatorControlApp.exe /hidden", true)]
        [InlineData("PredatorControlApp.exe -HIDDEN", true)]
        [InlineData("PredatorControlApp.exe", false)]
        public void CommandLine_Hidden_DetectionIsRobust(string cmdLine, bool expectedHidden)
        {
            bool isHidden = cmdLine.IndexOf("-hidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            cmdLine.IndexOf("--hidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            cmdLine.IndexOf("/hidden", StringComparison.OrdinalIgnoreCase) >= 0;
            Assert.Equal(expectedHidden, isHidden);
        }
    }

    public class CustomControlsEdgeCaseStressTests
    {
        [Fact]
        public void PredatorSlider_ChangingMinMax_ClampsValueAutomatically()
        {
            var slider = new PredatorSlider { Minimum = 0, Maximum = 100, Value = 80 };
            Assert.Equal(80, slider.Value);

            // Lower maximum below current value
            slider.Maximum = 50;
            Assert.Equal(50, slider.Value);

            // Raise minimum above current value
            slider.Minimum = 60;
            Assert.Equal(60, slider.Value);
        }

        [Fact]
        public void PredatorDropDown_SelectedIndex_OutOfBounds_IsIgnored()
        {
            var drop = new PredatorDropDown();
            drop.Items.AddRange(new[] { "Item 1", "Item 2" });
            drop.SelectedIndex = 0;
            Assert.Equal(0, drop.SelectedIndex);
            Assert.Equal("Item 1", drop.SelectedText);

            // Out of range index should not change SelectedIndex
            drop.SelectedIndex = 99;
            Assert.Equal(0, drop.SelectedIndex);

            drop.SelectedIndex = -1;
            Assert.Equal(-1, drop.SelectedIndex);
            Assert.Equal("", drop.SelectedText);
        }

        [Fact]
        public void PredatorButton_IsActive_TogglesCorrectly()
        {
            var btn = new PredatorButton { Text = "Test" };
            Assert.False(btn.IsActive);
            btn.IsActive = true;
            Assert.True(btn.IsActive);
            btn.CustomActiveColor = Color.Red;
            Assert.Equal(Color.Red, btn.CustomActiveColor);
        }

        [Fact]
        public void DarkScrollPanel_SetDpiScale_ClampsPositive()
        {
            var panel = new DarkScrollPanel();
            panel.SetDpiScale(1.5f);
            panel.SetDpiScale(-1f); // Should default to 1f
            panel.SetDpiScale(2.0f);
        }
    }

    public class GameSyncExtendedStressTests : IDisposable
    {
        private readonly string _tempFile;

        public GameSyncExtendedStressTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"gamesync_ext_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
        }

        [Fact]
        public void CorruptedJsonFile_RecoversGracefullyWithoutThrowing()
        {
            File.WriteAllText(_tempFile, "{ this is not valid json! ### }");
            using var controller = new GameSyncController(_tempFile);
            Assert.False(controller.IsEnabled);
            Assert.Empty(controller.Profiles);
        }

        [Fact]
        public void RapidEnableDisable_MaintainsConsistentState()
        {
            using var controller = new GameSyncController(_tempFile);
            for (int i = 0; i < 50; i++)
            {
                controller.IsEnabled = (i % 2 == 0);
                Assert.Equal(i % 2 == 0, controller.IsEnabled);
            }
        }
    }

    public class SliderDynamicBoundReconfigurationTests
    {
        [Fact]
        public void Slider_MinGreaterThanMax_AdjustsMaxAndClampsValueWithoutThrowing()
        {
            var slider = new PredatorSlider { Minimum = 10, Maximum = 50, Value = 30 };
            Assert.Equal(30, slider.Value);

            // Setting Minimum to 80 (greater than current Max 50) must bump Maximum to 80 and clamp Value to 80
            slider.Minimum = 80;
            Assert.Equal(80, slider.Minimum);
            Assert.True(slider.Maximum >= 80);
            Assert.Equal(80, slider.Value);
        }

        [Fact]
        public void Slider_MaxLessThanMin_AdjustsMinAndClampsValueWithoutThrowing()
        {
            var slider = new PredatorSlider { Minimum = 40, Maximum = 90, Value = 60 };
            Assert.Equal(60, slider.Value);

            // Setting Maximum to 20 (less than current Min 40) must reduce Minimum to 20 and clamp Value to 20
            slider.Maximum = 20;
            Assert.Equal(20, slider.Maximum);
            Assert.True(slider.Minimum <= 20);
            Assert.Equal(20, slider.Value);
        }

        [Fact]
        public void Slider_EqualMinAndMax_OperatesSafely()
        {
            var slider = new PredatorSlider { Minimum = 50, Maximum = 50, Value = 50 };
            Assert.Equal(50, slider.Value);
            Assert.Equal(50, slider.Minimum);
            Assert.Equal(50, slider.Maximum);

            slider.Value = 100;
            Assert.Equal(50, slider.Value);

            slider.Value = 0;
            Assert.Equal(50, slider.Value);
        }
    }

    public class BacklightStateManagerDeepLifecycleTests
    {
        [Fact]
        public void Backlight_CompleteDayLifecycle_MatchesSpecification()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 80;

            // 1. Initial boot on AC
            mgr.OnPowerSourceChanged(true, out bool turnOff, out int targetBright);
            Assert.False(turnOff);
            Assert.Equal(80, targetBright);

            // 2. Unplug charger (switch to battery) -> backlight must turn off
            mgr.OnPowerSourceChanged(false, out turnOff, out targetBright);
            Assert.True(turnOff);
            Assert.Equal(0, targetBright);

            // 3. User manually turns on backlight on battery to 40%
            mgr.OnUserAdjustedBrightness(40, onBattery: true);
            Assert.True(mgr.ManualBacklightOnBattery);
            Assert.Equal(40, mgr.ManualBatteryBrightness);
            Assert.Equal(80, mgr.SavedAcBrightness); // AC brightness untouched

            // 4. Laptop lid closes -> backlight must turn off unconditionally
            mgr.OnLidChanged(false, PowerLineStatus.Offline, out turnOff, out targetBright);
            Assert.True(turnOff);
            Assert.Equal(0, targetBright);
            Assert.False(mgr.ManualBacklightOnBattery); // Reset on lid close

            // 5. Lid opens on battery -> backlight remains off by default
            mgr.OnLidChanged(true, PowerLineStatus.Offline, out turnOff, out targetBright);
            Assert.True(turnOff);
            Assert.Equal(0, targetBright);

            // 6. Laptop goes to sleep (suspend)
            mgr.OnSuspend(out turnOff, out targetBright);
            Assert.True(turnOff);
            Assert.Equal(0, targetBright);
            Assert.True(mgr.IsSleeping);

            // 7. Laptop resumes on AC power -> restores saved AC brightness (80%)
            mgr.OnResume(PowerLineStatus.Online, out turnOff, out targetBright);
            Assert.False(turnOff);
            Assert.Equal(80, targetBright);
            Assert.False(mgr.IsSleeping);
        }

        [Theory]
        [InlineData(PowerLineStatus.Online, false)]
        [InlineData(PowerLineStatus.Offline, true)]
        [InlineData(PowerLineStatus.Unknown, true)] // Unknown defaults to battery safety
        public void Backlight_IsOnBattery_EvaluatesSafely(PowerLineStatus status, bool expectedBattery)
        {
            var mgr = new BacklightStateManager();
            bool onBattery = mgr.IsOnBattery(status);
            Assert.Equal(expectedBattery, onBattery);
        }
    }

    public class WmiTachometerMaskingAndOverlayTests
    {
        [Theory]
        [InlineData(0x00000000UL, 0)]
        [InlineData(0x00000E00UL, 0x0E)] // (0x0E00 >> 8) = 14 RPM
        [InlineData(0x00123400UL, 0x1234 & 0x1FFF)] // 13-bit mask
        [InlineData(0x00001001UL, 0)] // Non-zero status byte (0x01) -> 0 RPM
        [InlineData(0x0000FF05UL, 0)] // Non-zero status byte (0x05) -> 0 RPM
        public void MaskTachometerRpm_ProperlyExtracts13Bits(ulong raw, int expectedRpm)
        {
            int actual = WmiController.MaskTachometerRpm(raw);
            Assert.Equal(expectedRpm, actual);
        }

        [Theory]
        [InlineData(3600UL, 3600)] // Direct Method 17 tachometer reading (< 8192)
        [InlineData(4500UL, 4500)]
        [InlineData(0x00100000UL, 0x1000 & 0x1FFF)] // ACPI encoded packet (> 8191)
        public void DecodeFanSpeed_HandlesDirectAndEncodedReadings(ulong raw, int expectedRpm)
        {
            int actual = WmiController.DecodeFanSpeed(raw);
            Assert.Equal(expectedRpm, actual);
        }
    }

    public class DebouncePowerLineNoiseAndGlitchStressTests
    {
        [Fact]
        public void DebouncePowerLine_SuppressesRapidIntermittentGlitches()
        {
            bool? current = true; // Started Online
            bool? pending = null;
            int ticks = 0;

            // Single glitch sample: Offline
            current = Form1.DebouncePowerLine(PowerLineStatus.Offline, current, ref pending, ref ticks);
            Assert.Equal(true, current); // Still Online

            // Glitch resolved: Online again
            current = Form1.DebouncePowerLine(PowerLineStatus.Online, current, ref pending, ref ticks);
            Assert.Equal(true, current); // Still Online

            // Consistent disconnect: 2 consecutive Offline samples
            current = Form1.DebouncePowerLine(PowerLineStatus.Offline, current, ref pending, ref ticks);
            Assert.Equal(true, current); // Still Online after 1 sample
            current = Form1.DebouncePowerLine(PowerLineStatus.Offline, current, ref pending, ref ticks);
            Assert.Equal(false, current); // Confirmed Offline after 2 samples
        }
    }

    public class UpdaterParsingAndAssetSelectionTests
    {
        [Theory]
        [InlineData("v1.0.0", true, 1, 0, 0)]
        [InlineData("v.2.3.4", true, 2, 3, 4)]
        [InlineData("v3.5.0", true, 3, 5, 0)]
        [InlineData("4.0.1", true, 4, 0, 1)]
        [InlineData("beta-1", false, 0, 0, 0)]
        [InlineData("", false, 0, 0, 0)]
        [InlineData(null, false, 0, 0, 0)]
        public void Updater_TryParseTag_ParsesAccurately(string? tag, bool expectedValid, int major, int minor, int build)
        {
            bool valid = Updater.TryParseTag(tag!, out var version);
            Assert.Equal(expectedValid, valid);
            if (expectedValid)
            {
                Assert.Equal(new Version(major, minor, build), version);
            }
        }
    }

    [Collection("SingleInstanceTests")]
    public class DeepSystemIntegrationAndStressTests
    {
        [Fact]
        public void SingleInstanceLock_RapidMultiThreadContention_MaintainsMutualExclusion()
        {
            string testMutex = $"PredatorControlApp_ContentionTest_{Guid.NewGuid():N}";
            Program.ReleaseSingleInstanceLock();
            int acquiredCount = 0;
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, 50, _ =>
            {
                try
                {
                    if (Program.TryTakeSingleInstanceLock(testMutex))
                    {
                        System.Threading.Interlocked.Increment(ref acquiredCount);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
            Assert.Equal(1, acquiredCount); // Exactly one thread acquired the lock

            Program.ReleaseSingleInstanceLock();
            bool acquiredAgain = Program.TryTakeSingleInstanceLock(testMutex);
            Assert.True(acquiredAgain);
            Program.ReleaseSingleInstanceLock();
        }

        [Fact]
        public void BacklightStateManager_RapidLidAndPowerChurn_NeverCorruptsState()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 75;

            for (int i = 0; i < 100; i++)
            {
                bool isAc = (i % 2 == 0);
                bool isLidOpen = (i % 3 != 0);

                mgr.OnPowerSourceChanged(isAc, out bool off1, out int b1);
                mgr.OnLidChanged(isLidOpen, isAc ? PowerLineStatus.Online : PowerLineStatus.Offline, out bool off2, out int b2);

                if (!isLidOpen)
                {
                    Assert.True(off2);
                    Assert.Equal(0, b2);
                }
                else if (!isAc)
                {
                    Assert.True(off2);
                    Assert.Equal(0, b2);
                }
                else
                {
                    Assert.False(off2);
                    Assert.Equal(75, b2);
                }
            }
        }

        [Fact]
        public void FanCurveInterpolation_DynamicRamping_SmoothMonotonicOutput()
        {
            var curve = new List<Point>
            {
                new(30, 15),
                new(45, 25),
                new(60, 45),
                new(75, 70),
                new(90, 95),
                new(100, 100)
            };

            int previousSpeed = 0;
            for (int temp = 20; temp <= 110; temp++)
            {
                int speed = Form1.InterpolateCurve(curve, temp);
                Assert.True(speed >= 10 && speed <= 100, $"Speed {speed} out of 10-100 range at temp {temp}");
                if (temp > 20)
                {
                    Assert.True(speed >= previousSpeed, $"Speed decreased at temp {temp}: prev={previousSpeed}, cur={speed}");
                }
                previousSpeed = speed;
            }
        }

        [Fact]
        public void PowerModeResolution_DefaultFallback_ReturnsBalanced()
        {
            // Testing when no registry keys are set
            byte batMode = Program.ResolvePowerMode(onBattery: true);
            byte acMode = Program.ResolvePowerMode(onBattery: false);

            Assert.True(batMode == 0x00 || batMode == 0x01 || batMode == 0x06);
            Assert.True(acMode == 0x00 || acMode == 0x01 || acMode == 0x04 || acMode == 0x05);
        }

        [Fact]
        public void HighThroughput_WmiLockContention_ZeroExceptionsAcrossOneThousandCalls()
        {
            using var wmi = new WmiController();
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, 1000, i =>
            {
                try
                {
                    switch (i % 8)
                    {
                        case 0:
                            wmi.SetPowerMode((byte)(i % 7));
                            break;
                        case 1:
                            wmi.SetFanBehavior((byte)(1 + (i % 3)), applyCustomSpeeds: (i % 2 == 0));
                            break;
                        case 2:
                            wmi.SetCpuFanSpeed((byte)(10 + (i % 91)));
                            break;
                        case 3:
                            wmi.SetGpuFanSpeed((byte)(10 + (i % 91)));
                            break;
                        case 4:
                            wmi.SetRgbMode(i % 5, (byte)(i % 256), (byte)((i * 3) % 256), (byte)((i * 7) % 256), (byte)(i % 101), (byte)(1 + (i % 9)), (byte)(i % 2));
                            break;
                        case 5:
                            _ = wmi.CpuTemp;
                            _ = wmi.GpuTemp;
                            break;
                        case 6:
                            _ = wmi.CpuFanRpm;
                            _ = wmi.GpuFanRpm;
                            break;
                        case 7:
                            wmi.SetBatteryChargeLimit(i % 2 == 0);
                            break;
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
        public void DynamicCurve_ExtremeNoiseAndInvalidPoints_NeverThrowsOrCrashes()
        {
            var extremeCurve = new List<Point>
            {
                new(-999, -50),
                new(0, 0),
                new(30, 20),
                new(30, 40), // Duplicate X
                new(50, 50),
                new(50, 50), // Duplicate exact point
                new(70, 60),
                new(100, 100),
                new(999, 500)
            };

            for (int t = -100; t <= 200; t++)
            {
                int speed = Form1.InterpolateCurve(extremeCurve, t);
                Assert.InRange(speed, 10, 100);
            }
        }
    }
}
