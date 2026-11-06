using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using PredatorControlApp;
using PredatorControlApp.Bios;
using PredatorControlApp.Display;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class BiosSettingsAndHardwareMuxTests
    {
        [Fact]
        public void AppSettings_BiosSettingsValues_GetAndSetWorkCorrectly()
        {
            var settings = new AppSettings();
            Assert.NotNull(settings.BiosSettingsValues);

            // Test default fallback
            Assert.Equal("DefaultValue", settings.GetBiosSettingValue("NonExistent", "DefaultValue"));

            // Test set and get case-insensitively
            settings.SetBiosSettingValue("Keyboard Backlight Timeout", "Disabled");
            Assert.Equal("Disabled", settings.GetBiosSettingValue("keyboard backlight timeout", "Default"));
            Assert.Equal("Disabled", settings.GetBiosSettingValue("KEYBOARD BACKLIGHT TIMEOUT", "Default"));

            // Test clone preserves BiosSettingsValues
            var clone = settings.Clone();
            Assert.NotNull(clone.BiosSettingsValues);
            Assert.Equal("Disabled", clone.GetBiosSettingValue("Keyboard Backlight Timeout", ""));

            // Ensure modifying clone does not mutate original
            clone.SetBiosSettingValue("Keyboard Backlight Timeout", "30 Seconds");
            Assert.Equal("30 Seconds", clone.GetBiosSettingValue("Keyboard Backlight Timeout", ""));
            Assert.Equal("Disabled", settings.GetBiosSettingValue("Keyboard Backlight Timeout", ""));
        }

        [Fact]
        public void AppSettings_BiosSettingsValues_JsonRoundtripPreservesSettings()
        {
            var original = new AppSettings();
            original.SetBiosSettingValue("Function Key behavior", "Standard Function Key");
            original.SetBiosSettingValue("Power-off USB Charge", "Enabled (30%)");
            original.SetBiosSettingValue("Battery Threshold (80% Limit)", "Enabled");
            original.SetBiosSettingValue("Display MUX (Advanced Optimus / dGPU)", "Discrete GPU only");

            string json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(deserialized);
            Assert.Equal("Standard Function Key", deserialized.GetBiosSettingValue("Function Key behavior", ""));
            Assert.Equal("Enabled (30%)", deserialized.GetBiosSettingValue("Power-off USB Charge", ""));
            Assert.Equal("Enabled", deserialized.GetBiosSettingValue("Battery Threshold (80% Limit)", ""));
            Assert.Equal("Discrete GPU only", deserialized.GetBiosSettingValue("Display MUX (Advanced Optimus / dGPU)", ""));
        }

        [Fact]
        public void BiosManager_RememberedSettings_LoadsAndAppliesWithAppSettings()
        {
            var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"predator_test_settings_{Guid.NewGuid():N}.json");
            AppSettings.CustomFilePath = tempSettingsPath;
            try
            {
                var settings = AppSettings.Load();
                settings.BiosModificationsEnabled = true;
                settings.BiosDangerousAllowed = true;
                settings.SetBiosSettingValue("Keyboard backlight timeout", "Disabled");
                settings.SetBiosSettingValue("Power-off USB Charge", "Enabled (20%)");
                settings.Save();

                var manager = BiosManager.Instance;
                manager.ModificationsEnabled = true;
                manager.DangerousAllowed = true;
                manager.ProbeAndLoad();

                var kbItem = manager.Settings.FirstOrDefault(s => s.Name.Equals("Keyboard backlight timeout", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(kbItem);
                Assert.Equal("Disabled", kbItem.CurrentValue);

                var usbItem = manager.Settings.FirstOrDefault(s => s.Name.Equals("Power-off USB Charge", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(usbItem);
                Assert.Equal("Enabled (20%)", usbItem.CurrentValue);

                // Now apply a new setting and verify it persists immediately
                usbItem.PendingValue = "Enabled (10%)";
                bool applied = manager.ApplySetting(usbItem, out string error);
                Assert.True(applied, error);
                Assert.Equal("Enabled (10%)", usbItem.CurrentValue);

                var reloadedSettings = AppSettings.Load();
                Assert.Equal("Enabled (10%)", reloadedSettings.GetBiosSettingValue("Power-off USB Charge", ""));
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath); } catch { }
            }
        }

        [Fact]
        public void DisplaySettingsForm_HardwareMuxSwitchUI_CorrectButtonsAndLabels()
        {
            StaTestRunner.Run(() =>
            {
                using var form = new DisplaySettingsForm();
                form.CreateControl();

                var btnAuto = typeof(DisplaySettingsForm).GetField("_btnMuxAuto", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as PredatorButton;
                var btnIntegrated = typeof(DisplaySettingsForm).GetField("_btnMuxIntegrated", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as PredatorButton;
                var btnDiscrete = typeof(DisplaySettingsForm).GetField("_btnMuxDiscrete", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as PredatorButton;
                var lblStatus = typeof(DisplaySettingsForm).GetField("_lblMuxStatus", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as Label;
                var lblNotice = typeof(DisplaySettingsForm).GetField("_lblMuxNotice", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as Label;

                Assert.NotNull(btnAuto);
                Assert.NotNull(btnIntegrated);
                Assert.NotNull(btnDiscrete);
                Assert.NotNull(lblStatus);
                Assert.NotNull(lblNotice);

                Assert.Equal("Automatic (Optimus MUX)", btnAuto.Text);
                Assert.Equal("Integrated Only (iGPU MUX)", btnIntegrated.Text);
                Assert.Equal("Discrete Only (dGPU MUX)", btnDiscrete.Text);

                Assert.Contains("MUX", lblStatus.Text);
                Assert.Contains("restart", lblNotice.Text, StringComparison.OrdinalIgnoreCase);
            });
        }

        [Fact]
        public void BiosSettingsForm_RowCurrentLabelAndPersistence()
        {
            StaTestRunner.Run(() =>
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"predator_test_biosform_{Guid.NewGuid():N}.json");
                AppSettings.CustomFilePath = tempPath;
                try
                {
                    var settings = AppSettings.Load();
                    settings.BiosModificationsEnabled = true;
                    settings.SetBiosSettingValue("Keyboard backlight timeout", "Disabled");
                    settings.Save();

                    using var form = new BiosSettingsForm(BiosManager.Instance, settings);
                    form.CreateControl();

                    // Verify settings populated with remembered value
                    var kbItem = BiosManager.Instance.Settings.FirstOrDefault(s => s.Name.Contains("backlight timeout", StringComparison.OrdinalIgnoreCase));
                    Assert.NotNull(kbItem);
                    Assert.Equal("Disabled", kbItem.CurrentValue);
                }
                finally
                {
                    AppSettings.CustomFilePath = null;
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            });
        }
    }
}
