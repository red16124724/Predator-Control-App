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
    [Collection("NoParallel")]
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
        public void BiosManager_RememberedSettings_LoadsAndAppliesHardwareMuxSwitch_RejectsNonMux()
        {
            var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"predator_test_settings_{Guid.NewGuid():N}.json");
            AppSettings.CustomFilePath = tempSettingsPath;
            try
            {
                var settings = AppSettings.Load();
                settings.SetBiosSettingValue("Display MUX (Advanced Optimus / dGPU)", "Discrete GPU only");
                settings.Save();

                var manager = BiosManager.Instance;
                manager.ModificationsEnabled = true;
                manager.DangerousAllowed = true;
                manager.ProbeAndLoad();

                // 1. Hardware MUX switch item is loaded and active
                var muxItem = manager.GetMuxSetting();
                Assert.NotNull(muxItem);

                // 2. Applying Hardware MUX switch persists and updates value
                muxItem.PendingValue = "Optimus";
                bool applied = manager.ApplySetting(muxItem, out string error, bypassSafetyGates: true);
                Assert.True(applied, error);
                Assert.Equal("Optimus", muxItem.CurrentValue);

                var reloadedSettings = AppSettings.Load();
                Assert.Equal("Optimus", reloadedSettings.GetBiosSettingValue(muxItem.Name, ""));

                // 3. Applying arbitrary BIOS modifying settings is rejected
                var nonMuxItem = new BiosSettingItem
                {
                    Name = "Keyboard backlight timeout",
                    CurrentValue = "30 Seconds",
                    PendingValue = "Disabled",
                    Options = new() { "Disabled", "10 Seconds", "30 Seconds" }
                };
                manager.ModificationsEnabled = false;
                bool nonMuxApplied = manager.ApplySetting(nonMuxItem, out string nonMuxErr);
                Assert.False(nonMuxApplied);
                Assert.NotEmpty(nonMuxErr);
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
        public void BiosSettingsForm_LoadsHardwareMuxSettingOnly()
        {
            StaTestRunner.Run(() =>
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"predator_test_biosform_{Guid.NewGuid():N}.json");
                AppSettings.CustomFilePath = tempPath;
                try
                {
                    var settings = AppSettings.Load();
                    settings.SetBiosSettingValue("Display MUX (Advanced Optimus / dGPU)", "Optimus");
                    settings.Save();

                    using var form = new BiosSettingsForm(BiosManager.Instance, settings);
                    form.CreateControl();

                    var muxItem = BiosManager.Instance.GetMuxSetting();
                    Assert.NotNull(muxItem);
                    Assert.Contains(muxItem.CurrentValue, muxItem.Options);
                }
                finally
                {
                    AppSettings.CustomFilePath = null;
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            });
        }

        [Fact]
        public void Form1_BiosModifyingSettingsUI_Removed_And_HardwareMuxActiveInDisplaySettings()
        {
            StaTestRunner.Run(() =>
            {
                using var form = new Form1();
                form.CreateControl();

                // 1. Verify _pnlBios and _btnNavBios are removed from Form1
                var pnlBiosField = typeof(Form1).GetField("_pnlBios", BindingFlags.NonPublic | BindingFlags.Instance);
                var btnNavBiosField = typeof(Form1).GetField("_btnNavBios", BindingFlags.NonPublic | BindingFlags.Instance);
                var cardBiosField = typeof(Form1).GetField("_cardBios", BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.Null(pnlBiosField);
                Assert.Null(btnNavBiosField);
                Assert.Null(cardBiosField);

                // 2. Verify Display panel exists and contains the Hardware MUX Switch UI controls
                var pnlDisplayField = typeof(Form1).GetField("_pnlDisplay", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(pnlDisplayField);
                var pnlDisplay = pnlDisplayField.GetValue(form) as Control;
                Assert.NotNull(pnlDisplay);

                // 3. Verify Display Settings standalone form still contains active MUX controls
                using var displayForm = new DisplaySettingsForm();
                displayForm.CreateControl();
                var btnAuto = typeof(DisplaySettingsForm).GetField("_btnMuxAuto", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(displayForm) as PredatorButton;
                var btnDiscrete = typeof(DisplaySettingsForm).GetField("_btnMuxDiscrete", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(displayForm) as PredatorButton;
                Assert.NotNull(btnAuto);
                Assert.NotNull(btnDiscrete);
            });
        }
    }
}
