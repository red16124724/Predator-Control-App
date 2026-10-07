using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class HardwarePersistenceAndFanFixTests
    {
        [Fact]
        public void AppSettings_CoolBoost_SerializationAndCloning()
        {
            var original = new AppSettings
            {
                CoolBoost = true
            };

            var clone = original.CreateSnapshot();
            Assert.True(clone.CoolBoost);

            string json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);
            Assert.NotNull(deserialized);
            Assert.True(deserialized.CoolBoost);
        }

        [Fact]
        public void AppSettings_CustomFilePath_IsolationRoundTrip()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "SettingsTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, "isolated_settings.json");

            try
            {
                AppSettings.CustomFilePath = tempFile;

                var settings = new AppSettings
                {
                    CoolBoost = true,
                    FanCurveEnabled = true
                };
                settings.CpuCurve.Add(new CurvePointData(30, 0));
                settings.CpuCurve.Add(new CurvePointData(100, 100));
                settings.Save();

                Assert.True(File.Exists(tempFile));

                var loaded = AppSettings.Load();
                Assert.NotNull(loaded);
                Assert.True(loaded.CoolBoost);
                Assert.True(loaded.FanCurveEnabled);
                Assert.Equal(2, loaded.CpuCurve.Count);
                Assert.Equal(30, loaded.CpuCurve[0].X);
                Assert.Equal(0, loaded.CpuCurve[0].Y);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Theory]
        [InlineData(2560, true)]       // 10% duty cycle (10 << 8)
        [InlineData(5120, true)]       // 20% duty cycle (20 << 8)
        [InlineData(12800, true)]      // 50% duty cycle (50 << 8)
        [InlineData(25600, true)]      // 100% duty cycle (100 << 8)
        [InlineData(0, false)]         // 0 RPM
        [InlineData(2561, false)]      // Dynamic tachometer reading
        [InlineData(2559, false)]      // Dynamic tachometer reading
        [InlineData(4500, false)]      // Typical high fan RPM
        [InlineData(5800, false)]      // Typical max fan RPM
        public void WmiController_IsDutyCyclePayload_CorrectlyIdentifies(int value, bool expectedDuty)
        {
            bool isDuty = WmiController.IsDutyCyclePayload(value);
            Assert.Equal(expectedDuty, isDuty);
        }

        [Fact]
        public void FanCurveForm_SetCpuCurve_AppliesEvenWithoutHandleCreated()
        {
            using var form = new FanCurveForm();
            var customPoints = new List<Point>
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

            // Form handle not yet created (Show has not been called)
            Assert.False(form.IsHandleCreated);

            form.SetCpuCurve(customPoints);
            var retrieved = form.GetCpuCurve();

            Assert.Equal(8, retrieved.Count);
            Assert.Equal(30, retrieved[0].X);
            Assert.Equal(0, retrieved[0].Y);
            Assert.Equal(100, retrieved[7].X);
            Assert.Equal(100, retrieved[7].Y);
        }

        [Fact]
        public void PredatorSwitch_SetCheckedImmediate_SnapsKnobInstantly()
        {
            using var sw = new PredatorSwitch();
            Assert.False(sw.IsHandleCreated);

            sw.SetCheckedImmediate(true);
            Assert.True(sw.Checked);

            sw.SetCheckedImmediate(false);
            Assert.False(sw.Checked);
        }

        [Fact]
        public void PredatorSwitch_Checked_SnapsKnobWhenNoHandle()
        {
            using var sw = new PredatorSwitch();
            Assert.False(sw.IsHandleCreated);

            sw.Checked = true;
            Assert.True(sw.Checked);
        }

        [Fact]
        public void FanCurveGraph_Normalize_UserCustomCurve_RetainsExactEightPoints()
        {
            var userPoints = new List<Point>
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

            var normalized = FanCurveGraph.Normalize(userPoints);
            Assert.Equal(8, normalized.Count);
            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(userPoints[i].X, normalized[i].X);
                Assert.Equal(userPoints[i].Y, normalized[i].Y);
            }
        }

        [Theory]
        [InlineData(1920, 1080)]
        [InlineData(2560, 1440)]
        [InlineData(3840, 2160)]
        [InlineData(3440, 1440)]
        [InlineData(1920, 1200)]
        [InlineData(1080, 1920)]
        public void AspectRatio16x9_Calculation_FitsDisplayAndPreservesRatio(int screenW, int screenH)
        {
            int targetW = screenW;
            int targetH = (int)Math.Round(targetW * 9.0 / 16.0);
            if (targetH > screenH)
            {
                targetH = screenH;
                targetW = (int)Math.Round(targetH * 16.0 / 9.0);
            }

            Assert.True(targetW <= screenW, $"targetW {targetW} must be <= screenW {screenW}");
            Assert.True(targetH <= screenH, $"targetH {targetH} must be <= screenH {screenH}");

            double ratio = (double)targetW / targetH;
            Assert.InRange(ratio, 16.0 / 9.0 - 0.05, 16.0 / 9.0 + 0.05);

            int targetX = (screenW - targetW) / 2;
            int targetY = (screenH - targetH) / 2;
            Assert.True(targetX >= 0);
            Assert.True(targetY >= 0);
        }

        [Fact]
        public void AppSettings_BacklightTimeoutProperty_IsCompletelyRemoved()
        {
            var prop = typeof(AppSettings).GetProperty("BacklightTimeout");
            Assert.Null(prop);
        }
    }
}
