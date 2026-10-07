using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class BugFixAuditAndVerificationTests
    {
        #region Bug Fix 1: UI Text Rendering (Mnemonic Ampersand Underscores Prevention)

        [Fact]
        public void PredatorButton_UseMnemonic_DefaultsToFalse()
        {
            using var btn = new PredatorButton { Text = "Power & Cooling" };
            Assert.False(btn.UseMnemonic);
        }

        [Fact]
        public void PredatorButton_UseMnemonic_CanBeToggled()
        {
            using var btn = new PredatorButton { Text = "Save & Apply" };
            Assert.False(btn.UseMnemonic);

            btn.UseMnemonic = true;
            Assert.True(btn.UseMnemonic);

            btn.UseMnemonic = false;
            Assert.False(btn.UseMnemonic);
        }

        [Fact]
        public void PredatorDropDown_UseMnemonic_DefaultsToFalse()
        {
            using var dd = new PredatorDropDown();
            Assert.False(dd.UseMnemonic);

            dd.UseMnemonic = true;
            Assert.True(dd.UseMnemonic);
        }

        [Fact]
        public void UiLabelFormatter_GetTextFormatFlags_IncludesNoPrefix_WhenMnemonicDisabled()
        {
            var flags = UiLabelFormatter.GetTextFormatFlags(TextFormatFlags.Left | TextFormatFlags.VerticalCenter, useMnemonic: false);
            Assert.True((flags & TextFormatFlags.NoPrefix) != 0, "NoPrefix flag should be set when useMnemonic is false");
        }

        [Fact]
        public void UiLabelFormatter_GetTextFormatFlags_ExcludesNoPrefix_WhenMnemonicEnabled()
        {
            var flags = UiLabelFormatter.GetTextFormatFlags(TextFormatFlags.Left | TextFormatFlags.VerticalCenter, useMnemonic: true);
            Assert.False((flags & TextFormatFlags.NoPrefix) != 0, "NoPrefix flag should not be set when useMnemonic is true");
        }

        [Fact]
        public void UiLabelFormatter_DisableMnemonic_DisablesMnemonicAcrossControls()
        {
            using var lbl = new Label { UseMnemonic = true, Text = "Fans & Sensors" };
            var returnedLbl = UiLabelFormatter.DisableMnemonic(lbl);
            Assert.False(returnedLbl.UseMnemonic);

            using var btn = new Button { UseMnemonic = true, Text = "Reset & Reload" };
            var returnedBtn = UiLabelFormatter.DisableMnemonic(btn);
            Assert.False(returnedBtn.UseMnemonic);

            using var pb = new PredatorButton { UseMnemonic = true, Text = "Turbo & Max" };
            var returnedPb = UiLabelFormatter.DisableMnemonic(pb);
            Assert.False(returnedPb.UseMnemonic);

            using var pdd = new PredatorDropDown { UseMnemonic = true };
            var returnedPdd = UiLabelFormatter.DisableMnemonic(pdd);
            Assert.False(returnedPdd.UseMnemonic);
        }

        [Fact]
        public void PredatorButton_Paint_RendersAmpersandLiterallyWithoutException()
        {
            using var btn = new PredatorButton
            {
                Text = "CPU & GPU Control",
                Width = 140,
                Height = 40,
                UseMnemonic = false
            };

            using var bmp = new Bitmap(140, 40);
            using var g = Graphics.FromImage(bmp);
            btn.DrawToBitmap(bmp, new Rectangle(0, 0, 140, 40));

            // Must execute successfully without throwing
            Assert.Equal("CPU & GPU Control", btn.Text);
            Assert.False(btn.UseMnemonic);
        }

        [Fact]
        public void WinFormsLabel_UseMnemonicFalse_PreservesAmpersandLiterally()
        {
            using var lbl = new Label
            {
                Text = "Lighting & Effects",
                UseMnemonic = false
            };

            Assert.False(lbl.UseMnemonic);
            Assert.Equal("Lighting & Effects", lbl.Text);
        }

        #endregion

        #region Bug Fix 2: Fan Curves Strictly 8 Points & Normalization

        [Fact]
        public void FanCurve_ControlPointCount_IsStrictlyEight()
        {
            Assert.Equal(8, FanCurveGraph.ControlPointCount);
        }

        [Fact]
        public void FanCurve_DefaultCpuPoints_MatchesExactEightTargetPoints()
        {
            var expected = new List<Point>
            {
                new(30, 0),
                new(50, 10),
                new(60, 20),
                new(70, 25),
                new(77, 30),
                new(85, 45),
                new(90, 60),
                new(100, 100)
            };

            var actual = FanCurveGraph.DefaultCpuPoints;
            Assert.Equal(8, actual.Count);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void FanCurve_Form1_DefaultCpuCurve_MatchesExactEightTargetPoints()
        {
            var expected = new List<Point>
            {
                new(30, 0),
                new(50, 10),
                new(60, 20),
                new(70, 25),
                new(77, 30),
                new(85, 45),
                new(90, 60),
                new(100, 100)
            };

            var actual = Form1.DefaultCpuCurve;
            Assert.Equal(8, actual.Count);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void FanCurve_Normalize_NullInput_ReturnsStrictlyEightPointsMatchingDefaultCpu()
        {
            var result = FanCurveGraph.Normalize(null);
            Assert.NotNull(result);
            Assert.Equal(8, result.Count);
            Assert.Equal(FanCurveGraph.DefaultCpuPoints, result);
        }

        [Fact]
        public void FanCurve_Normalize_EmptyInput_ReturnsStrictlyEightPointsMatchingDefaultCpu()
        {
            var result = FanCurveGraph.Normalize(new List<Point>());
            Assert.NotNull(result);
            Assert.Equal(8, result.Count);
            Assert.Equal(FanCurveGraph.DefaultCpuPoints, result);
        }

        [Fact]
        public void FanCurve_Normalize_SinglePointCorrupted_NormalizesUpToStrictlyEightPoints()
        {
            var single = new List<Point> { new(50, 60) };
            var result = FanCurveGraph.Normalize(single);
            Assert.NotNull(result);
            Assert.Equal(8, result.Count);
            Assert.Equal(FanCurveGraph.DefaultCpuPoints, result);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void FanCurve_Normalize_FewerThanEightPoints_NormalizesUpToStrictlyEightPoints(int count)
        {
            var rand = new Random(42 + count);
            var points = new List<Point>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new Point(30 + i * (70 / Math.Max(1, count - 1)), rand.Next(0, 101)));
            }

            var result = FanCurveGraph.Normalize(points);
            Assert.NotNull(result);
            Assert.Equal(8, result.Count);
            Assert.Equal(30, result[0].X);
            Assert.Equal(100, result[^1].X);

            for (int i = 0; i < result.Count - 1; i++)
            {
                Assert.True(result[i].X < result[i + 1].X, $"Point {i} X ({result[i].X}) must be strictly < next X ({result[i + 1].X})");
                Assert.InRange(result[i].X, 30, 100);
                Assert.InRange(result[i].Y, 0, 100);
            }
        }

        [Theory]
        [InlineData(9)]
        [InlineData(12)]
        [InlineData(16)]
        [InlineData(25)]
        [InlineData(100)]
        public void FanCurve_Normalize_MoreThanEightPoints_NormalizesDownToStrictlyEightPoints(int count)
        {
            var rand = new Random(1000 + count);
            var points = new List<Point>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new Point(rand.Next(20, 110), rand.Next(-20, 120)));
            }

            var result = FanCurveGraph.Normalize(points);
            Assert.NotNull(result);
            Assert.Equal(8, result.Count);
            Assert.Equal(30, result[0].X);
            Assert.Equal(100, result[^1].X);

            for (int i = 0; i < result.Count - 1; i++)
            {
                Assert.True(result[i].X < result[i + 1].X, $"Point {i} X ({result[i].X}) must be strictly < next X ({result[i + 1].X})");
                Assert.InRange(result[i].X, 30, 100);
                Assert.InRange(result[i].Y, 0, 100);
            }
        }

        [Fact]
        public void FanCurve_Normalize_DisorderedAndNegativeCoordinates_ClampedAndSortedToStrictlyEightPoints()
        {
            var corrupted = new List<Point>
            {
                new(-50, -30),
                new(150, 250),
                new(40, 10),
                new(80, 70),
                new(20, -10),
                new(95, 90)
            };

            var result = FanCurveGraph.Normalize(corrupted);
            Assert.NotNull(result);
            Assert.Equal(8, result.Count);
            Assert.Equal(30, result[0].X);
            Assert.Equal(100, result[^1].X);

            for (int i = 0; i < result.Count - 1; i++)
            {
                Assert.True(result[i].X < result[i + 1].X);
                Assert.InRange(result[i].X, 30, 100);
                Assert.InRange(result[i].Y, 0, 100);
            }
        }

        [Fact]
        public void FanCurve_Normalize_DuplicateXCoordinates_DeduplicatesAndNormalizesToStrictlyEightPoints()
        {
            var duplicates = new List<Point>
            {
                new(50, 20),
                new(50, 40),
                new(50, 60),
                new(70, 70)
            };

            var result = FanCurveGraph.Normalize(duplicates);
            Assert.NotNull(result);
            Assert.Equal(8, result.Count);
            Assert.Equal(30, result[0].X);
            Assert.Equal(100, result[^1].X);

            for (int i = 0; i < result.Count - 1; i++)
            {
                Assert.True(result[i].X < result[i + 1].X);
            }
        }

        [Fact]
        public void FanCurve_Normalize_ValidEightPoints_PreservesAllPoints()
        {
            var validCustom = new List<Point>
            {
                new(30, 5),
                new(45, 15),
                new(55, 25),
                new(65, 35),
                new(75, 45),
                new(85, 60),
                new(95, 80),
                new(100, 100)
            };

            var result = FanCurveGraph.Normalize(validCustom);
            Assert.NotNull(result);
            Assert.Equal(8, result.Count);
            Assert.Equal(validCustom, result);
        }

        #endregion

        #region Bug Fix 3: Power Mode Pill Color Helper

        [Fact]
        public void PowerModePillColor_Quiet_0x00_MapsToExactHexAndRgb()
        {
            var expected = Color.FromArgb(255, 255, 255); // #FFFFFF (White)
            var color = ThemeManager.GetPowerModeColor(0x00);
            var pillColor = ThemeManager.GetPowerModePillColor(0x00);

            Assert.Equal(expected, color);
            Assert.Equal(expected, pillColor);
            Assert.Equal(255, color.R);
            Assert.Equal(255, color.G);
            Assert.Equal(255, color.B);
            Assert.Equal("#FFFFFF", ThemeManager.GetPowerModePillColorHex(0x00));
        }

        [Fact]
        public void PowerModePillColor_Balanced_0x01_MapsToExactHexAndRgb()
        {
            var expected = Color.FromArgb(0x00, 0x96, 0xFF); // #0096FF (Blue)
            var color = ThemeManager.GetPowerModeColor(0x01);
            var pillColor = ThemeManager.GetPowerModePillColor(0x01);

            Assert.Equal(expected, color);
            Assert.Equal(expected, pillColor);
            Assert.Equal(0, color.R);
            Assert.Equal(150, color.G);
            Assert.Equal(255, color.B);
            Assert.Equal("#0096FF", ThemeManager.GetPowerModePillColorHex(0x01));
        }

        [Fact]
        public void PowerModePillColor_Performance_0x04_MapsToExactHexAndRgb()
        {
            var expected = Color.FromArgb(168, 85, 247); // #A855F7 (Purple)
            var color = ThemeManager.GetPowerModeColor(0x04);
            var pillColor = ThemeManager.GetPowerModePillColor(0x04);

            Assert.Equal(expected, color);
            Assert.Equal(expected, pillColor);
            Assert.Equal(168, color.R);
            Assert.Equal(85, color.G);
            Assert.Equal(247, color.B);
            Assert.Equal("#A855F7", ThemeManager.GetPowerModePillColorHex(0x04));
        }

        [Fact]
        public void PowerModePillColor_Turbo_0x05_MapsToExactHexAndRgb()
        {
            var expected = Color.FromArgb(255, 45, 135); // #FF2D87 (Pink)
            var color = ThemeManager.GetPowerModeColor(0x05);
            var pillColor = ThemeManager.GetPowerModePillColor(0x05);

            Assert.Equal(expected, color);
            Assert.Equal(expected, pillColor);
            Assert.Equal(255, color.R);
            Assert.Equal(45, color.G);
            Assert.Equal(135, color.B);
            Assert.Equal("#FF2D87", ThemeManager.GetPowerModePillColorHex(0x05));
        }

        [Fact]
        public void PowerModePillColor_Eco_0x06_MapsToExactHexAndRgb()
        {
            var expected = Color.FromArgb(0x00, 0xFF, 0x78); // #00FF78 (Green)
            var color = ThemeManager.GetPowerModeColor(0x06);
            var pillColor = ThemeManager.GetPowerModePillColor(0x06);

            Assert.Equal(expected, color);
            Assert.Equal(expected, pillColor);
            Assert.Equal(0, color.R);
            Assert.Equal(255, color.G);
            Assert.Equal(120, color.B);
            Assert.Equal("#00FF78", ThemeManager.GetPowerModePillColorHex(0x06));
        }

        [Fact]
        public void PowerModePillColor_InvalidMode_FallsBackToThemeAccent()
        {
            var color = ThemeManager.GetPowerModeColor(0xFF);
            var pillColor = ThemeManager.GetPowerModePillColor(0xFF);

            Assert.Equal(ThemeManager.Accent, color);
            Assert.Equal(ThemeManager.Accent, pillColor);
        }

        [Fact]
        public void Form1_GetPowerModePillColor_DelegatesCorrectlyToThemeManager()
        {
            byte[] modes = { 0x00, 0x01, 0x04, 0x05, 0x06, 0x99 };
            foreach (byte mode in modes)
            {
                Assert.Equal(ThemeManager.GetPowerModePillColor(mode), Form1.GetPowerModePillColor(mode));
                Assert.Equal(ThemeManager.GetPowerModePillColorHex(mode), Form1.GetPowerModePillColorHex(mode));
            }
        }

        #endregion
    }
}
