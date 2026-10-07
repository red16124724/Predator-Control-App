using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
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
        #endregion
    }
}
