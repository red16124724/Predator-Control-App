using System;
using System.Linq;
using Xunit;
using PredatorControlApp;
using PredatorControlApp.Display;
using PredatorControlApp.Bios;

namespace PredatorControlApp.Tests
{
    public class ImportedFeaturesTests
    {
        [Fact]
        public void DisplayManager_GetMaxRefreshRate_ReturnsPositiveRate()
        {
            var orig = Form1.SetRefreshRateOverride;
            try
            {
                Form1.SetRefreshRateOverride = hz => hz >= 30 && hz <= 1000;
                var mgr = new DisplayManager();
                int maxHz = mgr.GetMaxRefreshRate();

                Assert.True(maxHz >= 60);
                Assert.True(mgr.SetRefreshRate60());
                Assert.Equal(60, mgr.CurrentRefreshRate);
                Assert.True(mgr.SetRefreshRateMax());
                Assert.Equal(maxHz, mgr.CurrentRefreshRate);
            }
            finally
            {
                Form1.SetRefreshRateOverride = orig;
            }
        }

        [Fact]
        public void DisplayManager_SetRefreshRate_RejectsInvalidHz()
        {
            var mgr = new DisplayManager();
            Assert.False(mgr.SetRefreshRate(0));
            Assert.False(mgr.SetRefreshRate(29));
            Assert.False(mgr.SetRefreshRate(-144));
        }

        [Fact]
        public void DisplayManager_SetDdsMode_ValidatesRange()
        {
            var mgr = new DisplayManager();
            int firedMode = -1;
            mgr.DdsModeChanged += (m) => firedMode = m;

            Assert.True(mgr.SetDdsMode(0)); // Optimus
            Assert.Equal(0, mgr.CurrentDdsMode);
            Assert.Equal(0, firedMode);

            Assert.True(mgr.SetDdsMode(1)); // Auto
            Assert.Equal(1, mgr.CurrentDdsMode);

            Assert.True(mgr.SetDdsMode(2)); // dGPU
            Assert.Equal(2, mgr.CurrentDdsMode);

            Assert.False(mgr.SetDdsMode(-1));
            Assert.False(mgr.SetDdsMode(3));
        }

        [Fact]
        public void DisplayManager_SetLcdOverdrive_TogglesState()
        {
            var mgr = new DisplayManager();
            bool firedState = false;
            mgr.LcdOverdriveChanged += (s) => firedState = s;

            Assert.True(mgr.SetLcdOverdrive(true));
            Assert.True(mgr.IsLcdOverdriveEnabled);
            Assert.True(firedState);

            Assert.True(mgr.SetLcdOverdrive(false));
            Assert.False(mgr.IsLcdOverdriveEnabled);
            Assert.False(firedState);
        }

        [Fact]
        public void BiosManager_ProbeAndLoad_PopulatesCategoriesAndSettings()
        {
            var mgr = new BiosManager();
            mgr.ProbeAndLoad();

            Assert.NotNull(mgr.Settings);
            Assert.NotEmpty(mgr.Settings);
            Assert.False(string.IsNullOrWhiteSpace(mgr.ModelName));
            Assert.False(string.IsNullOrWhiteSpace(mgr.GetSupportModeDescription()));

            // Verify categorized settings exist
            Assert.Contains(mgr.Settings, s => s.Category == BiosCategory.System);
            Assert.Contains(mgr.Settings, s => s.Category == BiosCategory.PowerBattery);
            Assert.Contains(mgr.Settings, s => s.Category == BiosCategory.DisplayPerformance);
            Assert.Contains(mgr.Settings, s => s.Category == BiosCategory.BootSetup);
        }

        [Fact]
        public void BiosManager_ApplySetting_BlocksWhenModificationsDisabled()
        {
            var mgr = new BiosManager();
            mgr.ModificationsEnabled = false;

            var safeItem = mgr.Settings.FirstOrDefault(s => s.Risk == BiosSettingRisk.Normal);
            Assert.NotNull(safeItem);

            bool ok = mgr.ApplySetting(safeItem!, out string err);
            Assert.False(ok);
            Assert.Contains("disabled", err, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BiosManager_ApplySetting_BlocksDangerousWhenNotAllowed()
        {
            var mgr = new BiosManager();
            mgr.ModificationsEnabled = true;
            mgr.DangerousAllowed = false;

            var dangerousItem = mgr.Settings.FirstOrDefault(s => s.Risk == BiosSettingRisk.Dangerous);
            Assert.NotNull(dangerousItem);

            bool ok = mgr.ApplySetting(dangerousItem!, out string err);
            Assert.False(ok);
            Assert.Contains("blocked", err, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BiosManager_ApplySetting_AllowsSafeSettingWhenModificationsEnabled()
        {
            var mgr = new BiosManager();
            mgr.ModificationsEnabled = true;
            mgr.DangerousAllowed = false;

            var safeItem = mgr.Settings.FirstOrDefault(s => s.Risk == BiosSettingRisk.Normal);
            Assert.NotNull(safeItem);

            if (safeItem!.Options.Count > 1)
            {
                safeItem.PendingValue = safeItem.Options[1];
                bool ok = mgr.ApplySetting(safeItem, out string _);
                Assert.True(ok);
                Assert.Equal(safeItem.PendingValue, safeItem.CurrentValue);
            }
        }

        [Fact]
        public void GameProfile_NewFields_PreservedDuringClone()
        {
            var profile = new GameProfile
            {
                ExecutableName = "Cyberpunk2077.exe",
                DisplayName = "Cyberpunk 2077",
                PowerMode = 0x05, // Turbo
                FanMode = 0x02,   // Max
                RefreshRate = 240,
                CpuPl1Watts = 95,
                CpuPl2Watts = 135,
                DdsMode = 2,      // dGPU
                LcdOverdrive = 1, // On
                ProfileId = 42
            };

            var clone = profile.Clone();
            Assert.Equal("Cyberpunk2077.exe", clone.ExecutableName);
            Assert.Equal("Cyberpunk 2077", clone.DisplayName);
            Assert.Equal(0x05, clone.PowerMode);
            Assert.Equal(0x02, clone.FanMode);
            Assert.Equal(240, clone.RefreshRate);
            Assert.Equal(95, clone.CpuPl1Watts);
            Assert.Equal(135, clone.CpuPl2Watts);
            Assert.Equal(2, clone.DdsMode);
            Assert.Equal(1, clone.LcdOverdrive);
            Assert.Equal(42u, clone.ProfileId);
        }

        [Fact]
        public void DashboardSnapshot_NewFields_PreservedDuringClone()
        {
            var snap = new DashboardSnapshot
            {
                PowerMode = 0x01,
                FanMode = 0x01,
                RefreshRate = 165,
                DdsMode = 1,
                LcdOverdrive = 1
            };

            var clone = snap.Clone();
            Assert.Equal(165, clone.RefreshRate);
            Assert.Equal(1, clone.DdsMode);
            Assert.Equal(1, clone.LcdOverdrive);
        }

        [Fact]
        public void GameSyncController_SyncProfilesToNative_RegistersWithoutCrash()
        {
            using var controller = new GameSyncController(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gamesync_" + System.Guid.NewGuid().ToString("N") + ".json"));
            controller.AddProfile(new GameProfile
            {
                ExecutableName = "DoomEternal.exe",
                DisplayName = "Doom Eternal",
                PowerMode = 0x04,
                FanMode = 0x02,
                RefreshRate = 144,
                DdsMode = 2
            });

            // Sync should execute cleanly without error
            controller.SyncProfilesToNative();
            Assert.Contains(controller.Profiles, p => p.ExecutableName == "DoomEternal.exe");
        }
    }
}
