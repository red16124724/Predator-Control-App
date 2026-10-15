using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("SingleInstanceTests")]
    public class AppSettingsAndUIEnhancementTests
    {
        [Fact]
        public void AppSettings_Serialization_PreservesAllFields()
        {
            var original = new AppSettings
            {
                Theme = "Dark",
                PowerMode = 0x05,
                PowerModeAC = 0x04,
                PowerModeBattery = 0x00,
                AutoPowerAC = 3,
                AutoPowerBattery = 1,
                FanMode = 0x03,
                FanCurveEnabled = true,
                FanSpeedCpu = 75,
                FanSpeedGpu = 80,
                FanSpeedSys = 60,
                RefreshRate = 165,
                LcdOverdrive = true,
                BatteryLimit = true,
                RgbMode = 5,
                RgbBrightness = 85,
                RgbSpeed = 60,
                RgbR = 10,
                RgbG = 200,
                RgbB = 250,
                CpuCurve = new List<CurvePointData>
                {
                    new(30, 20),
                    new(50, 40),
                    new(80, 85),
                    new(95, 100)
                },
                GpuCurve = new List<CurvePointData>
                {
                    new(35, 25),
                    new(60, 50),
                    new(85, 90),
                    new(100, 100)
                }
            };

            string json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(deserialized);
            Assert.Equal("Dark", deserialized!.Theme);
            Assert.Equal((byte)0x05, deserialized.PowerMode);
            Assert.Equal((byte)0x04, deserialized.PowerModeAC);
            Assert.Equal((byte)0x00, deserialized.PowerModeBattery);
            Assert.Equal(3, deserialized.AutoPowerAC);
            Assert.Equal(1, deserialized.AutoPowerBattery);
            Assert.Equal((byte)0x03, deserialized.FanMode);
            Assert.True(deserialized.FanCurveEnabled);
            Assert.Equal(75, deserialized.FanSpeedCpu);
            Assert.Equal(80, deserialized.FanSpeedGpu);
            Assert.Equal(60, deserialized.FanSpeedSys);
            Assert.Equal(165, deserialized.RefreshRate);
            Assert.True(deserialized.LcdOverdrive);
            Assert.True(deserialized.BatteryLimit);
            Assert.Equal(5, deserialized.RgbMode);
            Assert.Equal(85, deserialized.RgbBrightness);
            Assert.Equal(60, deserialized.RgbSpeed);
            Assert.Equal(10, deserialized.RgbR);
            Assert.Equal(200, deserialized.RgbG);
            Assert.Equal(250, deserialized.RgbB);
            Assert.Equal(4, deserialized.CpuCurve.Count);
            Assert.Equal(30, deserialized.CpuCurve[0].X);
            Assert.Equal(20, deserialized.CpuCurve[0].Y);
            Assert.Equal(4, deserialized.GpuCurve.Count);
            Assert.Equal(100, deserialized.GpuCurve[3].X);
            Assert.Equal(100, deserialized.GpuCurve[3].Y);
        }

        [Fact]
        public void CurvePointData_Conversion_ToAndFromPoint()
        {
            var p = new Point(45, 65);
            var data = CurvePointData.FromPoint(p);
            Assert.Equal(45, data.X);
            Assert.Equal(65, data.Y);

            var roundTrip = data.ToPoint();
            Assert.Equal(p, roundTrip);
        }

        [Fact]
        public void ThemeManager_DarkPalette_MatchesExactHexColors()
        {
            ThemeManager.SetTheme(AppTheme.Dark);

            Assert.Equal(Color.FromArgb(0x12, 0x18, 0x2B), ThemeManager.FormBg);
            Assert.Equal(Color.FromArgb(0x0E, 0x14, 0x22), ThemeManager.TitleBarBg);
            Assert.Equal(Color.FromArgb(0x1E, 0x26, 0x40), ThemeManager.CardBg);
            Assert.Equal(Color.FromArgb(0x2E, 0x3A, 0x5C), ThemeManager.CardBorder);
            Assert.Equal(Color.FromArgb(0x25, 0x2D, 0x47), ThemeManager.ControlBg);
            Assert.Equal(Color.FromArgb(0x0B, 0x3D, 0x34), ThemeManager.ControlActive);
            Assert.Equal(Color.FromArgb(0x3B, 0x4A, 0x72), ThemeManager.ControlBorder);
            Assert.Equal(Color.FromArgb(0x00, 0xE5, 0xB8), ThemeManager.Accent);
            Assert.Equal(Color.FromArgb(0xE8, 0xEE, 0xF8), ThemeManager.TextPrimary);
            Assert.Equal(Color.FromArgb(0x8E, 0x9D, 0xC0), ThemeManager.TextSecondary);
        }

        [Fact]
        public void ThemeManager_LightPalette_MatchesExactHexColors()
        {
            ThemeManager.SetTheme(AppTheme.Light);

            Assert.Equal(Color.FromArgb(0xF4, 0xEF, 0xE6), ThemeManager.FormBg);
            Assert.Equal(Color.FromArgb(0xE6, 0xDF, 0xD1), ThemeManager.TitleBarBg);
            Assert.Equal(Color.FromArgb(0xFA, 0xF6, 0xEE), ThemeManager.CardBg);
            Assert.Equal(Color.FromArgb(0xCF, 0xC8, 0xB4), ThemeManager.CardBorder);
            Assert.Equal(Color.FromArgb(0xED, 0xE6, 0xD8), ThemeManager.ControlBg);
            Assert.Equal(Color.FromArgb(0x00, 0x7A, 0x60), ThemeManager.ControlActive);
            Assert.Equal(Color.FromArgb(0xC0, 0xB4, 0x9C), ThemeManager.ControlBorder);
            Assert.Equal(Color.FromArgb(0x00, 0x7A, 0x60), ThemeManager.Accent);
            Assert.Equal(Color.FromArgb(0x2A, 0x1F, 0x0F), ThemeManager.TextPrimary);
            Assert.Equal(Color.FromArgb(0x5C, 0x4F, 0x38), ThemeManager.TextSecondary);

            // Restore dark theme after test
            ThemeManager.SetTheme(AppTheme.Dark);
        }

        [Fact]
        public void HistoryGraphControl_LiveTrackingAndSamplePushing()
        {
            var graph = new HistoryGraphControl
            {
                Title = "CPU",
                Unit = "°C",
                Minimum = 20,
                Maximum = 100
            };

            graph.PushSample(55, 30);
            graph.PushSample(72, 60);

            Assert.Equal(72, graph.LatestValue);
            Assert.Equal(72, graph.PeakValue);

            graph.PushSample(60, 40);
            Assert.Equal(60, graph.LatestValue);
            Assert.Equal(72, graph.PeakValue); // Peak remains highest seen
        }

        [Fact]
        public void PredatorButton_IsActiveStateToggle()
        {
            var btn = new PredatorButton { Text = "Test Mode" };
            Assert.False(btn.IsActive);

            btn.IsActive = true;
            Assert.True(btn.IsActive);

            btn.IsActive = false;
            Assert.False(btn.IsActive);
        }

        [Fact]
        public void AppSettings_ModeKeyAction_And_TurboReturnMode_Serialization()
        {
            var settings = new AppSettings
            {
                ModeKeyAction = 1,
                TurboReturnMode = 0x04
            };

            string json = JsonSerializer.Serialize(settings);
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(1, deserialized!.ModeKeyAction);
            Assert.Equal((byte)0x04, deserialized.TurboReturnMode);
        }

        [Fact]
        public void TitleBarButton_Properties_And_FocusSuppression()
        {
            var btn = new TitleBarButton();
            Assert.False(btn.TabStop);
        }

        [Theory]
        [InlineData(0x01, 0x05, 0x01)] // Balanced -> Turbo -> returns to Balanced
        [InlineData(0x00, 0x05, 0x00)] // Quiet -> Turbo -> returns to Quiet
        [InlineData(0x04, 0x05, 0x04)] // Perf -> Turbo -> returns to Perf
        public void ModeKeyAction_TurboToggle_StateSimulation(byte initialMode, byte turboMode, byte expectedReturn)
        {
            byte currentMode = initialMode;
            byte turboReturnMode = 0x01;

            // Press 1: Toggle into Turbo
            if (currentMode != 0x05)
            {
                turboReturnMode = currentMode;
                currentMode = 0x05;
            }

            Assert.Equal(turboMode, currentMode);
            Assert.Equal(initialMode, turboReturnMode);

            // Press 2: Toggle out of Turbo
            if (currentMode == 0x05)
            {
                byte returnMode = (turboReturnMode == 0x05 || turboReturnMode == 0x06) ? (byte)0x01 : turboReturnMode;
                currentMode = returnMode;
            }

            Assert.Equal(expectedReturn, currentMode);
        }

        [Theory]
        [InlineData(true, 0x00, 0x00)]  // AC: Quiet stays Quiet
        [InlineData(true, 0x01, 0x01)]  // AC: Balanced stays Balanced
        [InlineData(true, 0x04, 0x04)]  // AC: Perf stays Perf
        [InlineData(true, 0x05, 0x05)]  // AC: Turbo stays Turbo
        [InlineData(true, 0x06, 0x01)]  // AC: Eco clamped to Balanced
        [InlineData(false, 0x00, 0x00)] // Battery: Quiet stays Quiet
        [InlineData(false, 0x01, 0x01)] // Battery: Balanced stays Balanced
        [InlineData(false, 0x06, 0x06)] // Battery: Eco stays Eco
        [InlineData(false, 0x04, 0x01)] // Battery: Perf clamped to Balanced
        [InlineData(false, 0x05, 0x01)] // Battery: Turbo clamped to Balanced
        public void AutoPowerRules_DontChange_SafetyClamping(bool isPluggedIn, byte currentMode, byte expectedMode)
        {
            byte resolvedMode;
            if (isPluggedIn)
            {
                resolvedMode = currentMode == 0x06 ? (byte)0x01 : currentMode;
            }
            else
            {
                resolvedMode = (currentMode == 0x04 || currentMode == 0x05) ? (byte)0x01 : currentMode;
            }

            Assert.Equal(expectedMode, resolvedMode);
        }

        #region UI Label Formatting Tests

        [Fact]
        public void UiLabelFormatter_CleanUnderscores_NormalizesProperly()
        {
            Assert.Equal(string.Empty, UiLabelFormatter.CleanUnderscores(null));
            Assert.Equal(string.Empty, UiLabelFormatter.CleanUnderscores(string.Empty));
            Assert.Equal("FAN MODE", UiLabelFormatter.CleanUnderscores("FAN_MODE"));
            Assert.Equal("CUSTOM FAN SPEED", UiLabelFormatter.CleanUnderscores("CUSTOM__FAN___SPEED"));
            Assert.Equal("HELLO WORLD", UiLabelFormatter.CleanUnderscores("   _HELLO_WORLD_  "));
            Assert.Equal("NO UNDERSCORES HERE", UiLabelFormatter.CleanUnderscores("NO UNDERSCORES HERE"));
        }

        [Theory]
        [InlineData("GPU Sleep State: D3Cold", "GPU Sleep State: Sleep")]
        [InlineData("Enable dGPU Mode", "Enable Dedicated Graphics Mode")]
        [InlineData("Switch to iGPU Only", "Switch to Integrated Graphics Only")]
        [InlineData("Configure MUX Switch", "Configure Graphics Mode Switch")]
        [InlineData("Link: Direct EC HID", "Link: Direct Hardware Controller")]
        [InlineData("Provider: Acer WMI", "Provider: Acer System Driver")]
        [InlineData("Current FanLock Status", "Current Fan Lock Status")]
        [InlineData("TABLE: OEM LOOKUP TABLES", "TABLE: HARDWARE PRESETS")]
        [InlineData("Active EC Fan Table", "Active Fan Speed Table")]
        [InlineData("Apply EC Curve", "Apply Preset Fan Curve")]
        public void UiLabelFormatter_FormatLabel_ReplacesTechnicalJargon(string input, string expected)
        {
            Assert.Equal(expected, UiLabelFormatter.FormatLabel(input));
        }

        [Theory]
        [InlineData("Perf", "Performance")]
        [InlineData("perf", "Performance")]
        [InlineData("Bal", "Balanced")]
        [InlineData("bal", "Balanced")]
        [InlineData("🔓 Lock SYS", "🔓 Lock System")]
        [InlineData("🔒 SYS Locked", "🔒 System Locked")]
        [InlineData("CUSTOM_BUTTON", "CUSTOM BUTTON")]
        public void UiLabelFormatter_FormatButton_NormalizesButtons(string input, string expected)
        {
            Assert.Equal(expected, UiLabelFormatter.FormatButton(input));
        }

        #endregion

        #region Theme Persistence Tests

        [Fact]
        public void ThemePersistence_SetTheme_PersistsAndLoadsAcrossRestarts()
        {
            var initialTheme = ThemeManager.CurrentTheme;
            try
            {
                // 1. Choose Light Theme
                ThemeManager.SetTheme(AppTheme.Light);
                Assert.Equal(AppTheme.Light, ThemeManager.CurrentTheme);
                Assert.False(ThemeManager.IsDarkThemeActive);

                // Reload from persistence
                ThemeManager.LoadThemeFromRegistry();
                Assert.Equal(AppTheme.Light, ThemeManager.CurrentTheme);

                // 2. Choose Dark Theme
                ThemeManager.SetTheme(AppTheme.Dark);
                Assert.Equal(AppTheme.Dark, ThemeManager.CurrentTheme);
                Assert.True(ThemeManager.IsDarkThemeActive);

                // Reload from persistence
                ThemeManager.LoadThemeFromRegistry();
                Assert.Equal(AppTheme.Dark, ThemeManager.CurrentTheme);
            }
            finally
            {
                ThemeManager.SetTheme(initialTheme);
            }
        }

        [Fact]
        public void ThemePersistence_NeverRevertsToSystem_WhenExplicitChoiceExists()
        {
            var initialTheme = ThemeManager.CurrentTheme;
            try
            {
                ThemeManager.SetTheme(AppTheme.Dark);

                // Explicit choice exists; reload must not revert to System
                ThemeManager.LoadThemeFromRegistry();
                Assert.NotEqual(AppTheme.System, ThemeManager.CurrentTheme);
                Assert.Equal(AppTheme.Dark, ThemeManager.CurrentTheme);

                ThemeManager.SetTheme(AppTheme.Light);
                ThemeManager.LoadThemeFromRegistry();
                Assert.NotEqual(AppTheme.System, ThemeManager.CurrentTheme);
                Assert.Equal(AppTheme.Light, ThemeManager.CurrentTheme);
            }
            finally
            {
                ThemeManager.SetTheme(initialTheme);
            }
        }

        #region UI Enhancement & Power Mode Colors Tests

        [Fact]
        public void ThemeManager_GetPowerModeColor_MatchesAcerPresets()
        {
            // Quiet (0x00): White (#FFFFFF)
            var quiet = ThemeManager.GetPowerModeColor(0x00);
            Assert.Equal(Color.FromArgb(255, 255, 255), quiet);

            // Balanced (0x01): Blue (#0096FF)
            var bal = ThemeManager.GetPowerModeColor(0x01);
            Assert.Equal(Color.FromArgb(0, 150, 255), bal);

            // Performance (0x04): Purple (#A855F7)
            var perf = ThemeManager.GetPowerModeColor(0x04);
            Assert.Equal(Color.FromArgb(168, 85, 247), perf);

            // Turbo (0x05): Pink (#FF2D87)
            var turbo = ThemeManager.GetPowerModeColor(0x05);
            Assert.Equal(Color.FromArgb(255, 45, 135), turbo);

            // Eco (0x06): Green (#00FF78)
            var eco = ThemeManager.GetPowerModeColor(0x06);
            Assert.Equal(Color.FromArgb(0, 255, 120), eco);

            // Fallback: ThemeManager.Accent
            var fallback = ThemeManager.GetPowerModeColor(0xFF);
            Assert.Equal(ThemeManager.Accent, fallback);
        }

        [Fact]
        public void ThemeManager_ApplyToControls_PreservesCustomColorTag()
        {
            using var form = new System.Windows.Forms.Form();
            var customLabel = new System.Windows.Forms.Label
            {
                Text = "Custom",
                Tag = "custom_color",
                ForeColor = Color.FromArgb(0, 220, 255)
            };
            var accentLabel = new System.Windows.Forms.Label
            {
                Text = "Accent",
                Tag = "accent",
                ForeColor = Color.White
            };
            form.Controls.Add(customLabel);
            form.Controls.Add(accentLabel);

            ThemeManager.ApplyToControls(form.Controls);

            // custom_color must NOT be overwritten by accent
            Assert.Equal(Color.FromArgb(0, 220, 255), customLabel.ForeColor);
            // accent must be overwritten with ThemeManager.Accent
            Assert.Equal(ThemeManager.Accent, accentLabel.ForeColor);
        }

        #endregion

        #region Fan Hysteresis Persistence and UI Tests

        [Fact]
        public void AppSettings_FanHysteresisDegrees_DefaultsToTwo()
        {
            var settings = new AppSettings();
            Assert.Equal(CurveFollower.DefaultHysteresisDegrees, settings.FanHysteresisDegrees);
            Assert.Equal(2, settings.FanHysteresisDegrees);
        }

        [Fact]
        public void AppSettings_FanHysteresisDegrees_SerializationAndSnapshotRoundTrip()
        {
            var original = new AppSettings
            {
                FanHysteresisDegrees = 6
            };

            // Test CreateSnapshot
            var snapshot = original.CreateSnapshot();
            Assert.Equal(6, snapshot.FanHysteresisDegrees);

            // Test JSON serialization
            string json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);
            Assert.NotNull(deserialized);
            Assert.Equal(6, deserialized!.FanHysteresisDegrees);
        }

        [Theory]
        [InlineData(-10, 0)]
        [InlineData(-1, 0)]
        [InlineData(0, 0)]
        [InlineData(5, 5)]
        [InlineData(10, 10)]
        [InlineData(15, 10)]
        public void AppSettings_FanHysteresisDegrees_Sanitize_ClampsOutOfRange(int input, int expected)
        {
            var settings = new AppSettings
            {
                FanHysteresisDegrees = input
            };
            settings.Sanitize();
            Assert.Equal(expected, settings.FanHysteresisDegrees);
        }

        [Fact]
        public void UiLabelFormatter_FormatHysteresisLabel_FormatsCorrectly()
        {
            Assert.Equal("Hysteresis: 0°C (Instant)", UiLabelFormatter.FormatHysteresisLabel(0));
            Assert.Equal("Hysteresis: 1°C", UiLabelFormatter.FormatHysteresisLabel(1));
            Assert.Equal("Hysteresis: 2°C", UiLabelFormatter.FormatHysteresisLabel(2));
            Assert.Equal("Hysteresis: 5°C", UiLabelFormatter.FormatHysteresisLabel(5));
            Assert.Equal("Hysteresis: 10°C", UiLabelFormatter.FormatHysteresisLabel(10));
        }

        [Fact]
        public void FanCurveForm_HysteresisControl_BindsToAppSettingsAndFiresApply()
        {
            using var form = new FanCurveForm();

            // Default hysteresis
            Assert.Equal(CurveFollower.DefaultHysteresisDegrees, form.HysteresisDegrees);
            Assert.Equal(CurveFollower.DefaultHysteresisDegrees, form.HysteresisSlider.Value);
            Assert.Equal("Hysteresis: 2°C", form.HysteresisLabel.Text);

            // Change hysteresis via SetHysteresis
            form.SetHysteresis(5);
            Assert.Equal(5, form.HysteresisDegrees);
            Assert.Equal(5, form.HysteresisSlider.Value);
            Assert.Equal("Hysteresis: 5°C", form.HysteresisLabel.Text);

            // Change hysteresis to 0
            form.SetHysteresis(0);
            Assert.Equal(0, form.HysteresisDegrees);
            Assert.Equal(0, form.HysteresisSlider.Value);
            Assert.Equal("Hysteresis: 0°C (Instant)", form.HysteresisLabel.Text);

            // Slider value change event
            form.HysteresisSlider.Value = 8;
            Assert.Equal(8, form.HysteresisDegrees);
            Assert.Equal("Hysteresis: 8°C", form.HysteresisLabel.Text);

            // ApplyClicked event fires with updated HysteresisDegrees
            FanCurveApplyEventArgs? receivedArgs = null;
            form.ApplyClicked += (s, args) =>
            {
                receivedArgs = args;
                args.Success = true;
            };

            form.ApplyCurves();
            Assert.NotNull(receivedArgs);
            Assert.Equal(8, receivedArgs!.HysteresisDegrees);

            // Reset restores default hysteresis (2°C)
            form.ResetDefaults();
            Assert.Equal(CurveFollower.DefaultHysteresisDegrees, form.HysteresisDegrees);
            Assert.Equal(CurveFollower.DefaultHysteresisDegrees, form.HysteresisSlider.Value);
            Assert.Equal("Hysteresis: 2°C", form.HysteresisLabel.Text);
        }

        [Fact]
        public void FanCurveForm_Hysteresis_AutoScrollAndLayout_Verified()
        {
            using var form = new FanCurveForm();
            Assert.True(form.AutoScroll, "FanCurveForm must have AutoScroll enabled to prevent high-DPI clipping.");
            Assert.True(form.AutoScrollMinSize.Height >= 580);
            Assert.NotNull(form.HysteresisSlider);
            Assert.NotNull(form.HysteresisLabel);
            Assert.True(form.Controls.Contains(form.HysteresisSlider));
            Assert.True(form.Controls.Contains(form.HysteresisLabel));
        }

        [Fact]
        public void Form1_EmbeddedHysteresisSlider_PresentAndBoundToSettingsAndFollowers()
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new Form1();
                    Assert.NotNull(form.EmbeddedHysteresisSlider);
                    Assert.NotNull(form.EmbeddedHysteresisLabel);

                    var slider = form.EmbeddedHysteresisSlider!;
                    var label = form.EmbeddedHysteresisLabel!;

                    // Range
                    Assert.Equal(CurveFollower.MinHysteresisDegrees, slider.Minimum);
                    Assert.Equal(CurveFollower.MaxHysteresisDegrees, slider.Maximum);

                    // Value reflects settings
                    Assert.Equal(form.Settings.FanHysteresisDegrees, slider.Value);
                    Assert.Contains("Hysteresis", label.Text);

                    // Changing value updates label and settings
                    slider.Value = 6;
                    Assert.Equal(6, form.Settings.FanHysteresisDegrees);
                    Assert.Equal("Hysteresis: 6°C", label.Text);

                    slider.Value = 0;
                    Assert.Equal(0, form.Settings.FanHysteresisDegrees);
                    Assert.Equal("Hysteresis: 0°C (Instant)", label.Text);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            bool finished = thread.Join(10000);
            Assert.True(finished, "Form1 inspection timed out.");
            if (error != null) throw error;
        }

        #endregion

        #region Battery Calibration Settings & UI Formatter Tests

        [Fact]
        public void AppSettings_CalibrationFields_Defaults()
        {
            var settings = new AppSettings();
            Assert.Equal(0, settings.LastBatteryCalibrationUtcTicks);
            Assert.Equal(0, settings.BatteryCalibrationPhase);
            Assert.Equal(-1, settings.CalibrationLastKnownPercent);
            Assert.False(settings.CalibrationPreChargeLimitEnabled);
        }

        [Fact]
        public void AppSettings_CalibrationFields_SerializationRoundTrip()
        {
            long fixedTicks = new DateTime(2026, 10, 14, 15, 30, 0, DateTimeKind.Utc).Ticks;
            var original = new AppSettings
            {
                LastBatteryCalibrationUtcTicks = fixedTicks,
                BatteryCalibrationPhase = 2,
                CalibrationLastKnownPercent = 14,
                CalibrationPreChargeLimitEnabled = true
            };

            string json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(fixedTicks, deserialized!.LastBatteryCalibrationUtcTicks);
            Assert.Equal(2, deserialized.BatteryCalibrationPhase);
            Assert.Equal(14, deserialized.CalibrationLastKnownPercent);
            Assert.True(deserialized.CalibrationPreChargeLimitEnabled);
        }

        [Fact]
        public void AppSettings_CalibrationFields_CreateSnapshot_ClonesIndependently()
        {
            var original = new AppSettings
            {
                LastBatteryCalibrationUtcTicks = 123456789L,
                BatteryCalibrationPhase = 1,
                CalibrationLastKnownPercent = 75,
                CalibrationPreChargeLimitEnabled = true
            };

            var snapshot = original.CreateSnapshot();

            Assert.Equal(123456789L, snapshot.LastBatteryCalibrationUtcTicks);
            Assert.Equal(1, snapshot.BatteryCalibrationPhase);
            Assert.Equal(75, snapshot.CalibrationLastKnownPercent);
            Assert.True(snapshot.CalibrationPreChargeLimitEnabled);

            original.LastBatteryCalibrationUtcTicks = 987654321L;
            original.BatteryCalibrationPhase = 0;
            original.CalibrationLastKnownPercent = 10;
            original.CalibrationPreChargeLimitEnabled = false;

            Assert.Equal(123456789L, snapshot.LastBatteryCalibrationUtcTicks);
            Assert.Equal(1, snapshot.BatteryCalibrationPhase);
            Assert.Equal(75, snapshot.CalibrationLastKnownPercent);
            Assert.True(snapshot.CalibrationPreChargeLimitEnabled);
        }

        [Theory]
        [InlineData(-500L, 0L)]
        [InlineData(0L, 0L)]
        [InlineData(100000L, 100000L)]
        public void AppSettings_Sanitize_ClampsLastBatteryCalibrationUtcTicks(long inputTicks, long expectedTicks)
        {
            var settings = new AppSettings { LastBatteryCalibrationUtcTicks = inputTicks };
            settings.Sanitize();
            Assert.Equal(expectedTicks, settings.LastBatteryCalibrationUtcTicks);
        }

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 2)]
        [InlineData(99, 2)]
        public void AppSettings_Sanitize_ClampsBatteryCalibrationPhase(int inputPhase, int expectedPhase)
        {
            var settings = new AppSettings { BatteryCalibrationPhase = inputPhase };
            settings.Sanitize();
            Assert.Equal(expectedPhase, settings.BatteryCalibrationPhase);
        }

        [Theory]
        [InlineData(-10, -1)]
        [InlineData(-1, -1)]
        [InlineData(0, 0)]
        [InlineData(50, 50)]
        [InlineData(100, 100)]
        [InlineData(101, 100)]
        [InlineData(999, 100)]
        public void AppSettings_Sanitize_ClampsCalibrationLastKnownPercent(int inputPercent, int expectedPercent)
        {
            var settings = new AppSettings { CalibrationLastKnownPercent = inputPercent };
            settings.Sanitize();
            Assert.Equal(expectedPercent, settings.CalibrationLastKnownPercent);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void UiLabelFormatter_FormatCalibrationEta_NullOrNegative_ReturnsCalculating(bool isCharging)
        {
            Assert.Equal("Calculating remaining time...", UiLabelFormatter.FormatCalibrationEta(null, isCharging));
            Assert.Equal("Calculating remaining time...", UiLabelFormatter.FormatCalibrationEta(TimeSpan.FromSeconds(-1), isCharging));
            Assert.Equal("Calculating remaining time...", UiLabelFormatter.FormatCalibrationEta(TimeSpan.FromMinutes(-30), isCharging));
        }

        [Theory]
        [InlineData(95, true, "Estimated Time Remaining (Charging): 01h 35m")]
        [InlineData(5, true, "Estimated Time Remaining (Charging): 00h 05m")]
        [InlineData(0, true, "Estimated Time Remaining (Charging): 00h 00m")]
        [InlineData(130, false, "Estimated Time Remaining (Discharging): 02h 10m")]
        [InlineData(45, false, "Estimated Time Remaining (Discharging): 00h 45m")]
        public void UiLabelFormatter_FormatCalibrationEta_ValidPhases_FormatsCorrectly(int totalMinutes, bool isCharging, string expected)
        {
            var eta = TimeSpan.FromMinutes(totalMinutes);
            string formatted = UiLabelFormatter.FormatCalibrationEta(eta, isCharging);
            Assert.Equal(expected, formatted);
        }

        [Fact]
        public void UiLabelFormatter_FormatCalibrationEta_Over24Hours_CorrectlyFormatsTotalHours()
        {
            var eta = TimeSpan.FromHours(27).Add(TimeSpan.FromMinutes(15));
            string formatted = UiLabelFormatter.FormatCalibrationEta(eta, isCharging: true);
            Assert.Equal("Estimated Time Remaining (Charging): 27h 15m", formatted);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(-1L)]
        [InlineData(-100000L)]
        public void UiLabelFormatter_FormatLastCalibrated_ZeroOrNegative_ReturnsNever(long ticks)
        {
            Assert.Equal("Last Calibrated: Never", UiLabelFormatter.FormatLastCalibrated(ticks));
        }

        [Fact]
        public void UiLabelFormatter_FormatLastCalibrated_ValidTicks_FormatsLocalTime()
        {
            var utcTime = new DateTime(2026, 10, 14, 12, 30, 0, DateTimeKind.Utc);
            var localTime = utcTime.ToLocalTime();
            string expected = $"Last Calibrated: {localTime:yyyy-MM-dd HH:mm}";

            string actual = UiLabelFormatter.FormatLastCalibrated(utcTime.Ticks);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void UiLabelFormatter_FormatLastCalibrated_CorruptedTicks_SafelyReturnsNeverWithoutThrowing()
        {
            string actual = UiLabelFormatter.FormatLastCalibrated(long.MaxValue);
            Assert.Equal("Last Calibrated: Never", actual);
        }

        #endregion
        #endregion
    }
}
