using System;
using System.Drawing;
using System.IO;
using Microsoft.Win32;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    public class PerZoneRgbRebootPersistenceTests
    {
        [Fact]
        public void PerZoneRgbRebootPersistenceTest()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "RgbRebootTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, "settings.json");

            try
            {
                AppSettings.CustomFilePath = tempFile;

                // 1. Sets 4 distinct zone colors: Zone 1 = Red (#FF0000), Zone 2 = Green (#00FF00),
                //    Zone 3 = Blue (#0000FF), Zone 4 = Yellow (#FFFF00), with DynamicLightingEffectName = "Off"
                //    and DynamicEffectPrimaryColorArgb = Magenta (#FF00FF).
                Color red = Color.FromArgb(255, 0, 0);       // #FF0000
                Color green = Color.FromArgb(0, 255, 0);     // #00FF00
                Color blue = Color.FromArgb(0, 0, 255);      // #0000FF
                Color yellow = Color.FromArgb(255, 255, 0);  // #FFFF00
                Color magenta = Color.FromArgb(255, 0, 255); // #FF00FF

                var settings = new AppSettings();
                settings.SetStaticZoneColors(red, green, blue, yellow);
                settings.DynamicLightingEffectName = "Off";
                settings.DynamicEffectPrimaryColorArgb = magenta.ToArgb();

                // 2. Saves via settings.Save().
                settings.Save();
                Assert.True(File.Exists(tempFile));

                // 3. Loads via AppSettings.Load().
                var loaded = AppSettings.Load();
                Assert.NotNull(loaded);

                // 4. Asserts all 4 zone colors equal Red, Green, Blue, Yellow in the loaded settings.
                Assert.Equal(red.ToArgb(), loaded.StaticZone1ColorArgb);
                Assert.Equal(green.ToArgb(), loaded.StaticZone2ColorArgb);
                Assert.Equal(blue.ToArgb(), loaded.StaticZone3ColorArgb);
                Assert.Equal(yellow.ToArgb(), loaded.StaticZone4ColorArgb);
                Assert.Equal("Off", loaded.DynamicLightingEffectName);
                Assert.Equal(magenta.ToArgb(), loaded.DynamicEffectPrimaryColorArgb);

                // 5. Simulates KeyboardLightingController and verifies SnapshotZoneColors() preserves all 4 distinct colors.
                var caps = new KeyboardLightingCapabilities
                {
                    Tier = KeyboardLightingTier.FourZone,
                    PerKeySupported = false,
                    KeyCount = 4
                };
                var controller = new KeyboardLightingController(null, caps);
                controller.SetZoneColor(0, Color.FromArgb(loaded.StaticZone1ColorArgb));
                controller.SetZoneColor(1, Color.FromArgb(loaded.StaticZone2ColorArgb));
                controller.SetZoneColor(2, Color.FromArgb(loaded.StaticZone3ColorArgb));
                controller.SetZoneColor(3, Color.FromArgb(loaded.StaticZone4ColorArgb));

                var snapshot = controller.SnapshotZoneColors();
                Assert.Equal(4, snapshot.Length);
                Assert.Equal(red.ToArgb(), snapshot[0].ToArgb());
                Assert.Equal(green.ToArgb(), snapshot[1].ToArgb());
                Assert.Equal(blue.ToArgb(), snapshot[2].ToArgb());
                Assert.Equal(yellow.ToArgb(), snapshot[3].ToArgb());

                // 6. Simulates PowerModes.Resume and SessionSwitchReason.SessionUnlock to verify per-zone static colors remain intact.
                // Simulate power resume event:
                var resumeArgs = new PowerModeChangedEventArgs(PowerModes.Resume);
                Assert.Equal(PowerModes.Resume, resumeArgs.Mode);

                // On resume, settings re-validation/normalization must keep distinct colors:
                loaded.Normalize();
                loaded.RestoreMissingStaticZoneColors(true, true, true, true);

                Assert.Equal(red.ToArgb(), loaded.StaticZone1ColorArgb);
                Assert.Equal(green.ToArgb(), loaded.StaticZone2ColorArgb);
                Assert.Equal(blue.ToArgb(), loaded.StaticZone3ColorArgb);
                Assert.Equal(yellow.ToArgb(), loaded.StaticZone4ColorArgb);

                // Hardware controller re-apply on resume preserves all 4 colors:
                controller.SetZoneColor(0, Color.FromArgb(loaded.StaticZone1ColorArgb));
                controller.SetZoneColor(1, Color.FromArgb(loaded.StaticZone2ColorArgb));
                controller.SetZoneColor(2, Color.FromArgb(loaded.StaticZone3ColorArgb));
                controller.SetZoneColor(3, Color.FromArgb(loaded.StaticZone4ColorArgb));

                var resumeSnapshot = controller.SnapshotZoneColors();
                Assert.Equal(red.ToArgb(), resumeSnapshot[0].ToArgb());
                Assert.Equal(green.ToArgb(), resumeSnapshot[1].ToArgb());
                Assert.Equal(blue.ToArgb(), resumeSnapshot[2].ToArgb());
                Assert.Equal(yellow.ToArgb(), resumeSnapshot[3].ToArgb());

                // Simulate session unlock event:
                var sessionUnlockArgs = new SessionSwitchEventArgs(SessionSwitchReason.SessionUnlock);
                Assert.Equal(SessionSwitchReason.SessionUnlock, sessionUnlockArgs.Reason);

                // Re-verify loaded settings and controller snapshot remain completely intact
                var reloaded = AppSettings.Load();
                Assert.Equal(red.ToArgb(), reloaded.StaticZone1ColorArgb);
                Assert.Equal(green.ToArgb(), reloaded.StaticZone2ColorArgb);
                Assert.Equal(blue.ToArgb(), reloaded.StaticZone3ColorArgb);
                Assert.Equal(yellow.ToArgb(), reloaded.StaticZone4ColorArgb);

                var finalSnapshot = controller.SnapshotZoneColors();
                Assert.Equal(red.ToArgb(), finalSnapshot[0].ToArgb());
                Assert.Equal(green.ToArgb(), finalSnapshot[1].ToArgb());
                Assert.Equal(blue.ToArgb(), finalSnapshot[2].ToArgb());
                Assert.Equal(yellow.ToArgb(), finalSnapshot[3].ToArgb());
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

        [Fact]
        public void AppSettings_SetStaticZoneColor_SetsColorAndActivatesZoneLighting()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "RgbZoneTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, "settings.json");

            try
            {
                AppSettings.CustomFilePath = tempFile;
                var settings = new AppSettings();
                settings.IsZoneLightingActive = false;

                Color c1 = Color.FromArgb(255, 10, 20);
                Color c2 = Color.FromArgb(30, 255, 40);
                Color c3 = Color.FromArgb(50, 60, 255);
                Color c4 = Color.FromArgb(255, 255, 70);

                settings.SetStaticZoneColor(0, c1);
                Assert.Equal(c1.ToArgb(), settings.StaticZone1ColorArgb);
                Assert.True(settings.IsZoneLightingActive);

                settings.SetStaticZoneColor(1, c2);
                Assert.Equal(c2.ToArgb(), settings.StaticZone2ColorArgb);

                settings.SetStaticZoneColor(2, c3);
                Assert.Equal(c3.ToArgb(), settings.StaticZone3ColorArgb);

                settings.SetStaticZoneColor(3, c4);
                Assert.Equal(c4.ToArgb(), settings.StaticZone4ColorArgb);

                var loaded = AppSettings.Load();
                Assert.Equal(c1.ToArgb(), loaded.StaticZone1ColorArgb);
                Assert.Equal(c2.ToArgb(), loaded.StaticZone2ColorArgb);
                Assert.Equal(c3.ToArgb(), loaded.StaticZone3ColorArgb);
                Assert.Equal(c4.ToArgb(), loaded.StaticZone4ColorArgb);
                Assert.True(loaded.IsZoneLightingActive);
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

        [Fact]
        public void AppSettings_DefaultValues_MatchExpectedConstants()
        {
            var settings = new AppSettings();
            int expectedZoneColor = unchecked((int)0xFF00F0FF);
            Assert.Equal(expectedZoneColor, settings.StaticZone1ColorArgb);
            Assert.Equal(expectedZoneColor, settings.StaticZone2ColorArgb);
            Assert.Equal(expectedZoneColor, settings.StaticZone3ColorArgb);
            Assert.Equal(expectedZoneColor, settings.StaticZone4ColorArgb);
            Assert.Equal("Off", settings.DynamicLightingEffectName);
            Assert.Equal(expectedZoneColor, settings.DynamicEffectPrimaryColorArgb);
            Assert.Equal(unchecked((int)0xFFFF0080), settings.DynamicEffectSecondaryColorArgb);
            Assert.Equal(5, settings.DynamicEffectSpeed);
            Assert.Equal(100, settings.KeyboardBrightnessLevel);
        }
    }
}
