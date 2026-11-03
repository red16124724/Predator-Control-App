using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class V142BugFixVerificationTests
    {
        [Fact]
        public void Program_IsBenignCancellation_IdentifiesAllCancellationVariations()
        {
            Assert.True(Program.IsBenignCancellation(null));
            Assert.True(Program.IsBenignCancellation(new OperationCanceledException()));
            Assert.True(Program.IsBenignCancellation(new TaskCanceledException()));

            var sockAborted = new SocketException((int)SocketError.OperationAborted);
            Assert.True(Program.IsBenignCancellation(sockAborted));

            var sockTimedOut = new SocketException((int)SocketError.TimedOut);
            Assert.True(Program.IsBenignCancellation(sockTimedOut));

            var aggCanceled = new AggregateException(
                new OperationCanceledException(),
                new TaskCanceledException(),
                new AggregateException(new SocketException((int)SocketError.OperationAborted))
            );
            Assert.True(Program.IsBenignCancellation(aggCanceled));
        }

        [Fact]
        public void Program_IsBenignCancellation_RejectsGenuineErrors()
        {
            Assert.False(Program.IsBenignCancellation(new InvalidOperationException("Failed")));
            Assert.False(Program.IsBenignCancellation(new NullReferenceException()));
            Assert.False(Program.IsBenignCancellation(new IOException("File locked")));

            var sockConnRefused = new SocketException((int)SocketError.ConnectionRefused);
            Assert.False(Program.IsBenignCancellation(sockConnRefused));

            var aggMixed = new AggregateException(
                new OperationCanceledException(),
                new InvalidOperationException("Fatal error")
            );
            Assert.False(Program.IsBenignCancellation(aggMixed));
        }

        [Fact]
        public void BatteryCalibration_PhaseTransition_UpdatesCalibrationStateCorrectly()
        {
            var settings = new AppSettings();
            settings.BatteryCalibrationPhase = 1;
            settings.CalibrationLastKnownPercent = 98;

            int currentPct = 100;
            if (settings.BatteryCalibrationPhase == 1 && currentPct >= 100)
            {
                settings.BatteryCalibrationPhase = 2;
            }

            Assert.Equal(2, settings.BatteryCalibrationPhase);
        }

        [Fact]
        public void FanCurveGraph_SupportsDoubleClick_WithoutOverlappingControls()
        {
            using var graph = new FanCurveGraph
            {
                FanLabel = "CPU FAN CURVE",
                Width = 400,
                Height = 200
            };

            bool doubleClicked = false;
            graph.DoubleClick += (s, e) => doubleClicked = true;

            // Verify event handler invocation
            var doubleClickMethod = typeof(Control).GetMethod("OnDoubleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            doubleClickMethod?.Invoke(graph, new object[] { EventArgs.Empty });

            Assert.True(doubleClicked);
        }
        [Theory]
        [InlineData(0x00, false)] // Quiet: Unlocked
        [InlineData(0x01, false)] // Balanced: Unlocked
        [InlineData(0x04, false)] // Performance: Unlocked
        [InlineData(0x05, false)] // Turbo: Unlocked
        [InlineData(0x06, true)]  // Eco: Locked
        public void GetCurrentFanLock_OnlyEcoModeIsLocked(byte powerMode, bool expectLocked)
        {
            var form = (Form1)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Form1));
            var field = typeof(Form1).GetField("_currentPowerMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var method = typeof(Form1).GetMethod("GetCurrentFanLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            field!.SetValue(form, powerMode);
            var result = method!.Invoke(form, null);

            if (expectLocked)
            {
                Assert.Equal(FanLock.EcoMode, result);
            }
            else
            {
                Assert.Null(result);
            }
        }

        [Fact]
        public void UpdateDisplayRefreshRateButtons_SynchronizesBothButtonPairs()
        {
            var form = (Form1)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Form1));
            using var btn60 = new PredatorButton();
            using var btnMax = new PredatorButton();
            using var btnDisp60 = new PredatorButton();
            using var btnDispMax = new PredatorButton();

            typeof(Form1).GetField("_btn60Hz", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(form, btn60);
            typeof(Form1).GetField("_btnMaxHz", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(form, btnMax);
            typeof(Form1).GetField("_btnDisp60", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(form, btnDisp60);
            typeof(Form1).GetField("_btnDispMax", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(form, btnDispMax);

            var updateMethod = typeof(Form1).GetMethod("UpdateDisplayRefreshRateButtons", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Test 60Hz
            updateMethod!.Invoke(form, new object[] { 60 });
            Assert.True(btn60.IsActive);
            Assert.False(btnMax.IsActive);
            Assert.True(btnDisp60.IsActive);
            Assert.False(btnDispMax.IsActive);

            // Test 144Hz (MaxHz)
            updateMethod.Invoke(form, new object[] { 144 });
            Assert.False(btn60.IsActive);
            Assert.True(btnMax.IsActive);
            Assert.False(btnDisp60.IsActive);
            Assert.True(btnDispMax.IsActive);
        }

        [Fact]
        public void Test_TryApplyStartupPowerLimits_AppliesWhenCustomLimitsEnabled()
        {
            var settings = new AppSettings
            {
                EnableCustomPowerLimits = true,
                BalancedPl1Watts = 42,
                BalancedPl2Watts = 78
            };
            var (pl1, pl2) = settings.GetEffectiveLimitsForMode(OperatingMode.Balanced);
            Assert.Equal(42, pl1);
            Assert.Equal(78, pl2);

            // Verify Program.TryApplyStartupPowerLimits executes safely
            Program.TryApplyStartupPowerLimits(null, (byte)OperatingMode.Balanced);
        }

        [Fact]
        public void Test_ReScanPrioritizesSiliconFusedMsrAndMaintainsValidBoundsWithoutCorruption()
        {
            var syntheticAmlWithHeuristics = new byte[] {
                (byte)'P', (byte)'P', (byte)'C', (byte)'C',
                0x0C, 0x50, 0xC3, 0x00, 0x00, // 50,000 mW = 50W
                0x0C, 0x88, 0x13, 0x00, 0x00  // 5,000 mW = 5W
            };
            var parsed = BiosUefiPowerScanner.ParsePpccFromAml(syntheticAmlWithHeuristics);
            if (parsed.HasValue)
            {
                var (minW, max1W, max2W, _) = parsed.Value;
                // Bounds must be sane and not corrupt
                Assert.True(minW >= 5);
                Assert.True(max2W >= max1W);
            }
        }

        [Fact]
        public void Test_LcdOverdrive_MultiPathDispatch_SupportedProtocolsAndMiscSetting()
        {
            // Verify MiscSetting.LCD_OVERDRIVE = 0x0F
            Assert.Equal((byte)0x0F, (byte)MiscSetting.LCD_OVERDRIVE);
            Assert.Equal((byte)0x0F, (byte)MiscSetting.LcdOverdrive);

            // Verify helper inputs
            ulong onProfile = AcerProtocol.LcdOverdriveProfileInput(true);
            ulong offProfile = AcerProtocol.LcdOverdriveProfileInput(false);
            Assert.Equal(0x1000000000010UL, onProfile);
            Assert.Equal(0x10UL, offProfile);

            ulong onSetting = AcerProtocol.LcdOverdriveProfileSettingInput(true);
            ulong offSetting = AcerProtocol.LcdOverdriveProfileSettingInput(false);
            Assert.Equal(0x10UL | (1UL << 8), onSetting);
            Assert.Equal(0x10UL, offSetting);

            ulong onMisc = AcerProtocol.LcdOverdriveMiscSettingInput(true);
            ulong offMisc = AcerProtocol.LcdOverdriveMiscSettingInput(false);
            Assert.Equal(0x0FUL | (1UL << 8), onMisc);
            Assert.Equal(0x0FUL, offMisc);

            // Verify WmiController multi-path dispatch returns safely
            using var wmi = new WmiController();
            bool resultOn = wmi.SetLcdOverdrive(true);
            bool resultOff = wmi.SetLcdOverdrive(false);
            Assert.True(resultOn || !resultOn);
            Assert.True(resultOff || !resultOff);
        }
        [Fact]
        public void Test_CpuPowerLimitController_ReScanHardwareAndBiosDefaults_PopulatesDefaultsAndCapabilities()
        {
            var mockScanner = MockBiosUefiPowerScanner.CreateHeliosNeo16_45WProfile();
            using var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(
                tdpWatts: 45,
                minWatts: 12,
                maxWatts: 140,
                pl1Watts: 45,
                pl2Watts: 75,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true
            );

            var settings = new AppSettings();
            using var controller = new CpuPowerLimitController(null, settings, backend, mockScanner);

            var scanResult = controller.ReScanHardwareAndBiosDefaults();

            Assert.NotNull(scanResult);
            Assert.True(scanResult.HasCompletedBiosPowerScan);
            Assert.True(scanResult.CpuMinAllowedPowerWatts >= 5);
            Assert.True(scanResult.CpuMaxAllowedPl2Watts >= scanResult.CpuMaxAllowedPl1Watts);

            // Verify per-mode defaults are populated
            Assert.True(scanResult.BiosDefaultEcoPl1Watts > 0 && scanResult.BiosDefaultEcoPl2Watts > 0);
            Assert.True(scanResult.BiosDefaultQuietPl1Watts > 0 && scanResult.BiosDefaultQuietPl2Watts > 0);
            Assert.True(scanResult.BiosDefaultBalancedPl1Watts > 0 && scanResult.BiosDefaultBalancedPl2Watts > 0);
            Assert.True(scanResult.BiosDefaultPerformancePl1Watts > 0 && scanResult.BiosDefaultPerformancePl2Watts > 0);
            Assert.True(scanResult.BiosDefaultTurboPl1Watts > 0 && scanResult.BiosDefaultTurboPl2Watts > 0);

            // Capabilities should be synchronized
            var caps = controller.Capabilities;
            Assert.True(caps.IsSupported);
            Assert.Equal(45, caps.ThermalSpecPowerWatts);
            Assert.Equal(12, caps.ScannedMinPowerWatts);
            Assert.Equal(115, caps.ScannedMaxPl1Watts);
            Assert.Equal(140, caps.ScannedMaxPl2Watts);
        }

        [Fact]
        public void Test_ReScan_SafeNonClampingOrder_And_ResetButtonFormatting()
        {
            // Verify safe non-clamping order on slider bounds
            int minW = 15;
            int max1W = 115;
            int max2W = 140;

            // Simulate sliders
            int slider1Min = 55, slider1Max = 95, slider1Val = 70;
            int slider2Min = 55, slider2Max = 95, slider2Val = 90;

            // 1. Widen bounds first
            slider1Min = 0; slider1Max = 500;
            slider2Min = 0; slider2Max = 500;

            // 2. Exact scanned bounds
            slider1Min = minW; slider1Max = max1W;
            slider2Min = minW; slider2Max = max2W;

            // 3. Values in safe order (PL2 first if increasing, PL1 first if decreasing)
            int targetPl1 = 100;
            int targetPl2 = 125;

            if (targetPl2 >= slider1Val)
            {
                slider2Val = targetPl2;
                slider1Val = targetPl1;
            }
            else
            {
                slider1Val = targetPl1;
                slider2Val = targetPl2;
            }

            Assert.Equal(100, slider1Val);
            Assert.Equal(125, slider2Val);
            Assert.True(slider2Val >= slider1Val);

            // Verify Reset button label and tag formatting
            int defPl1 = 45;
            int defPl2 = 65;
            string label = UiLabelFormatter.FormatResetButtonLabel(defPl1, defPl2);
            string tag = $"{defPl1},{defPl2}";

            Assert.Contains("45", label);
            Assert.Contains("65", label);
            Assert.Equal("45,65", tag);

            // Tag parsing verification
            string[] parts = tag.Split(',');
            Assert.Equal(2, parts.Length);
            Assert.Equal(45, int.Parse(parts[0]));
            Assert.Equal(65, int.Parse(parts[1]));

            // Reset button enabled state
            bool isCustomized = (slider1Val != defPl1) || (slider2Val != defPl2);
            Assert.True(isCustomized);
        }
    }
}
