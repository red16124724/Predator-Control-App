using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using PredatorControlApp;
using PredatorControlApp.Bios;
using PredatorControlApp.Display;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class BiosSettingsPersistenceAndHardwareMuxTests
    {
        [Fact]
        public void BiosSettings_ModifiedViaBiosManager_RememberedByAppSettingsAndReloadedOnManagerInit()
        {
            var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"predator_mux_persist_{Guid.NewGuid():N}.json");
            AppSettings.CustomFilePath = tempSettingsPath;
            try
            {
                var mgr = BiosManager.Instance;
                mgr.ModificationsEnabled = true;
                mgr.DangerousAllowed = true;

                // 1. Modify a standard safe setting: POST Animation & Sound
                var postItem = mgr.Settings.FirstOrDefault(s => s.Name.Contains("POST Animation", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(postItem);
                postItem.PendingValue = "Disabled";
                bool postApplied = mgr.ApplySetting(postItem, out string postErr);
                Assert.True(postApplied, postErr);
                Assert.Equal("Disabled", postItem.CurrentValue);

                // 2. Modify a power setting: Power-off USB Charge
                var usbItem = mgr.Settings.FirstOrDefault(s => s.Name.Contains("Power-off USB Charge", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(usbItem);
                usbItem.PendingValue = "Enabled (30%)";
                bool usbApplied = mgr.ApplySetting(usbItem, out string usbErr);
                Assert.True(usbApplied, usbErr);
                Assert.Equal("Enabled (30%)", usbItem.CurrentValue);

                // 3. Verify AppSettings remembers both values immediately
                var loadedSettings = AppSettings.Load();
                Assert.Equal("Disabled", loadedSettings.GetBiosSettingValue(postItem.Name, ""));
                Assert.Equal("Enabled (30%)", loadedSettings.GetBiosSettingValue(usbItem.Name, ""));

                // 4. Initialize a brand new BiosManager instance and verify persisted settings are rehydrated
                var freshManager = new BiosManager();
                var freshPost = freshManager.Settings.FirstOrDefault(s => s.Name.Contains("POST Animation", StringComparison.OrdinalIgnoreCase));
                var freshUsb = freshManager.Settings.FirstOrDefault(s => s.Name.Contains("Power-off USB Charge", StringComparison.OrdinalIgnoreCase));

                Assert.NotNull(freshPost);
                Assert.NotNull(freshUsb);
                Assert.Equal("Disabled", freshPost.CurrentValue);
                Assert.Equal("Enabled (30%)", freshUsb.CurrentValue);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath); } catch { }
            }
        }

        [Fact]
        public void WmiControllerAndBiosManager_QueryLiveBiosMuxMode_Accurately()
        {
            var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"predator_wmi_mux_query_{Guid.NewGuid():N}.json");
            AppSettings.CustomFilePath = tempSettingsPath;
            try
            {
                var mgr = BiosManager.Instance;
                mgr.ModificationsEnabled = true;
                mgr.DangerousAllowed = true;
                var wmi = WmiController.Instance;

                // Test each GPU Mode: Auto, iGpuOnly (Optimus), dGpuOnly (Discrete)
                var modesToTest = new[]
                {
                    (GpuMode.dGpuOnly, "Discrete GPU only"),
                    (GpuMode.iGpuOnly, "Optimus"),
                    (GpuMode.Auto, "Auto")
                };

                foreach (var (targetMode, expectedBiosStr) in modesToTest)
                {
                    bool applied = mgr.SetGpuMode(targetMode, out string err, bypassSafetyGates: true);
                    Assert.True(applied, err);

                    // 1. BiosManager queries live mode
                    var biosGpu = mgr.GetGpuMode();
                    Assert.NotNull(biosGpu);
                    Assert.Equal(targetMode, biosGpu.Value);

                    // 2. Mux setting item matches expected string
                    var muxItem = mgr.GetMuxSetting();
                    Assert.NotNull(muxItem);
                    Assert.Equal(expectedBiosStr, muxItem.CurrentValue);

                    // 3. WmiController queries live mode from BIOS
                    var wmiGpu = wmi.GetGpuMode();
                    Assert.NotNull(wmiGpu);
                    Assert.Equal(targetMode, wmiGpu.Value);
                }
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath); } catch { }
            }
        }

        [Fact]
        public void HardwareMuxSwitch_ChangingGpuMode_SetsBiosSettingAndRequiresSystemRestart()
        {
            var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"predator_mux_restart_{Guid.NewGuid():N}.json");
            AppSettings.CustomFilePath = tempSettingsPath;
            try
            {
                var mgr = BiosManager.Instance;
                mgr.ModificationsEnabled = true;
                mgr.DangerousAllowed = true;
                var wmi = WmiController.Instance;

                // 1. Change mode to dGpuOnly via BiosManager
                bool applied = mgr.SetGpuMode(GpuMode.dGpuOnly, out string error, bypassSafetyGates: true);
                Assert.True(applied);
                // System restart must be explicitly communicated
                Assert.Contains("restart", error, StringComparison.OrdinalIgnoreCase);

                // Verify BIOS setting updated
                var muxItem = mgr.GetMuxSetting();
                Assert.NotNull(muxItem);
                Assert.Equal("Discrete GPU only", muxItem.CurrentValue);

                // Verify persisted in AppSettings
                var settings = AppSettings.Load();
                Assert.Equal("Discrete GPU only", settings.GetBiosSettingValue(muxItem.Name, ""));
                Assert.Equal((int)GpuMode.dGpuOnly, settings.HardwareGpuMuxMode);

                // 2. Change mode to iGpuOnly via WmiController
                bool wmiSet = wmi.SetGpuMode(GpuMode.iGpuOnly);
                Assert.True(wmiSet);
                Assert.Equal(GpuMode.iGpuOnly, mgr.GetGpuMode());
                Assert.Equal("Optimus", muxItem.CurrentValue);

                var reloaded = AppSettings.Load();
                Assert.Equal("Optimus", reloaded.GetBiosSettingValue(muxItem.Name, ""));
                Assert.Equal((int)GpuMode.iGpuOnly, reloaded.HardwareGpuMuxMode);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath); } catch { }
            }
        }

        [Fact]
        public void SafetyInterlocks_InvalidOrDangerousSettings_BlockedWhenInterlocksDisabled()
        {
            var mgr = BiosManager.Instance;

            // Scenario 1: All modifications disabled
            mgr.ModificationsEnabled = false;
            mgr.DangerousAllowed = false;
            Assert.False(mgr.ModificationsEnabled);
            Assert.False(mgr.DangerousAllowed);

            var safeItem = mgr.Settings.FirstOrDefault(s => s.Risk == BiosSettingRisk.Normal);
            Assert.NotNull(safeItem);
            safeItem.PendingValue = safeItem.Options.Last();
            bool appliedWhenDisabled = mgr.ApplySetting(safeItem, out string errDisabled);
            Assert.False(appliedWhenDisabled);
            Assert.Contains("disabled", errDisabled, StringComparison.OrdinalIgnoreCase);

            bool setGpuWhenDisabled = mgr.SetGpuMode(GpuMode.dGpuOnly, out string errGpuDisabled);
            Assert.False(setGpuWhenDisabled);
            Assert.Contains("disabled", errGpuDisabled, StringComparison.OrdinalIgnoreCase);

            // Scenario 2: Modifications enabled, but dangerous disallowed
            mgr.ModificationsEnabled = true;
            mgr.DangerousAllowed = false;

            var dangerousMuxItem = mgr.GetMuxSetting();
            Assert.NotNull(dangerousMuxItem);
            Assert.Equal(BiosSettingRisk.Dangerous, dangerousMuxItem.Risk);

            dangerousMuxItem.PendingValue = "Discrete GPU only";
            bool appliedDangerous = mgr.ApplySetting(dangerousMuxItem, out string errDangerous);
            Assert.False(appliedDangerous);
            Assert.Contains("blocked", errDangerous, StringComparison.OrdinalIgnoreCase);

            bool setGpuDangerous = mgr.SetGpuMode(GpuMode.dGpuOnly, out string errSetDangerous, bypassSafetyGates: false);
            Assert.False(setGpuDangerous);
            Assert.Contains("blocked", errSetDangerous, StringComparison.OrdinalIgnoreCase);

            // Scenario 3: Invalid option rejected even when modifications and dangerous are enabled
            mgr.DangerousAllowed = true;
            dangerousMuxItem.PendingValue = "InvalidNonExistentOption_12345";
            bool appliedInvalid = mgr.ApplySetting(dangerousMuxItem, out string errInvalid);
            Assert.False(appliedInvalid);
            Assert.Contains("Invalid value", errInvalid, StringComparison.OrdinalIgnoreCase);

            // Scenario 4: Null or empty setting item rejected
            bool nullRejected = mgr.ApplySetting(null!, out string errNull);
            Assert.False(nullRejected);
            Assert.NotEmpty(errNull);

            var emptyItem = new BiosSettingItem { Name = "", PendingValue = "Test" };
            bool emptyRejected = mgr.ApplySetting(emptyItem, out string errEmpty);
            Assert.False(emptyRejected);
            Assert.NotEmpty(errEmpty);

            // Reset
            mgr.ModificationsEnabled = false;
        }

        [Fact]
        public void RestoreDefaults_ResetsHardwareManagerAndPersistentSettings()
        {
            var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"predator_restore_test_{Guid.NewGuid():N}.json");
            AppSettings.CustomFilePath = tempSettingsPath;
            try
            {
                var mgr = BiosManager.Instance;
                mgr.ModificationsEnabled = true;
                mgr.DangerousAllowed = true;

                // Apply custom settings
                var postItem = mgr.Settings.FirstOrDefault(s => s.Name.Contains("POST Animation", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(postItem);
                postItem.PendingValue = "Disabled";
                mgr.ApplySetting(postItem, out _);

                var usbItem = mgr.Settings.FirstOrDefault(s => s.Name.Contains("Power-off USB Charge", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(usbItem);
                usbItem.PendingValue = "Enabled (30%)";
                mgr.ApplySetting(usbItem, out _);

                mgr.SetGpuMode(GpuMode.dGpuOnly, out _, bypassSafetyGates: true);

                // Verify persistent settings recorded
                var settingsBefore = AppSettings.Load();
                Assert.True(settingsBefore.BiosSettingsValues.Count >= 3);

                // Restore factory defaults
                bool restored = mgr.RestoreDefaults(userDefaults: false, out string restoreErr);
                Assert.True(restored, restoreErr);

                // Verify persistent settings cleared
                var settingsAfter = AppSettings.Load();
                Assert.Empty(settingsAfter.BiosSettingsValues);

                // Verify manager settings reloaded to factory defaults
                var restoredPost = mgr.Settings.FirstOrDefault(s => s.Name.Contains("POST Animation", StringComparison.OrdinalIgnoreCase));
                var restoredUsb = mgr.Settings.FirstOrDefault(s => s.Name.Contains("Power-off USB Charge", StringComparison.OrdinalIgnoreCase));
                var restoredMux = mgr.GetMuxSetting();

                Assert.NotNull(restoredPost);
                Assert.NotNull(restoredUsb);
                Assert.NotNull(restoredMux);

                Assert.Equal("Enabled", restoredPost.CurrentValue);
                Assert.Equal("Disabled", restoredUsb.CurrentValue);
                Assert.Equal("Auto", restoredMux.CurrentValue);
                Assert.Equal(GpuMode.Auto, mgr.GetGpuMode());
                Assert.Equal(GpuMode.Auto, WmiController.Instance.GetGpuMode());
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath); } catch { }
            }
        }
    }
}
