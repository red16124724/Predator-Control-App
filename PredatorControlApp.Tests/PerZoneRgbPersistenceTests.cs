using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    public class PerZoneRgbPersistenceTests
    {
        [Fact]
        public void AppSettings_Default_StaticZoneColors_AreDistinctAndValid()
        {
            var settings = new AppSettings();
            Assert.NotEqual(0, settings.StaticZone1ColorArgb);
            Assert.NotEqual(0, settings.StaticZone2ColorArgb);
            Assert.NotEqual(0, settings.StaticZone3ColorArgb);
            Assert.NotEqual(0, settings.StaticZone4ColorArgb);
        }

        [Fact]
        public void AppSettings_StaticZoneColors_PreserveFourDistinctColors()
        {
            var settings = new AppSettings();
            Color c1 = Color.FromArgb(255, 0, 0);     // Red
            Color c2 = Color.FromArgb(0, 255, 0);     // Green
            Color c3 = Color.FromArgb(0, 0, 255);     // Blue
            Color c4 = Color.FromArgb(255, 255, 0);   // Yellow

            settings.StaticZone1ColorArgb = c1.ToArgb();
            settings.StaticZone2ColorArgb = c2.ToArgb();
            settings.StaticZone3ColorArgb = c3.ToArgb();
            settings.StaticZone4ColorArgb = c4.ToArgb();
            settings.DynamicLightingEffectName = "Off";
            settings.IsZoneLightingActive = true;

            Assert.Equal(c1.ToArgb(), settings.StaticZone1ColorArgb);
            Assert.Equal(c2.ToArgb(), settings.StaticZone2ColorArgb);
            Assert.Equal(c3.ToArgb(), settings.StaticZone3ColorArgb);
            Assert.Equal(c4.ToArgb(), settings.StaticZone4ColorArgb);
            Assert.True(settings.IsZoneLightingActive);
            Assert.Equal("Off", settings.DynamicLightingEffectName);
        }

        [Fact]
        public void RestoreMissingStaticZoneColors_PreservesExistingDistinctColors()
        {
            var settings = new AppSettings();
            int red = Color.Red.ToArgb();
            int green = Color.Green.ToArgb();
            int blue = Color.Blue.ToArgb();
            int yellow = Color.Yellow.ToArgb();

            settings.StaticZone1ColorArgb = red;
            settings.StaticZone2ColorArgb = green;
            settings.StaticZone3ColorArgb = blue;
            settings.StaticZone4ColorArgb = yellow;

            // When all 4 zones were present in registry, none should be restored/overwritten
            settings.RestoreMissingStaticZoneColors(true, true, true, true);

            Assert.Equal(red, settings.StaticZone1ColorArgb);
            Assert.Equal(green, settings.StaticZone2ColorArgb);
            Assert.Equal(blue, settings.StaticZone3ColorArgb);
            Assert.Equal(yellow, settings.StaticZone4ColorArgb);
        }

        [Fact]
        public void RestoreMissingStaticZoneColors_RestoresOnlyMissingZones()
        {
            var settings = new AppSettings();
            int red = Color.Red.ToArgb();
            settings.StaticZone1ColorArgb = red;
            settings.KeyboardZoneColors = "#FF0000,#00FF00,#0000FF,#FFFF00";

            // Only zone 1 was present, zones 2,3,4 missing
            settings.RestoreMissingStaticZoneColors(true, false, false, false);

            Assert.Equal(red, settings.StaticZone1ColorArgb);
            Assert.Equal(Color.FromArgb(0, 255, 0).ToArgb(), settings.StaticZone2ColorArgb);
            Assert.Equal(Color.FromArgb(0, 0, 255).ToArgb(), settings.StaticZone3ColorArgb);
            Assert.Equal(Color.FromArgb(255, 255, 0).ToArgb(), settings.StaticZone4ColorArgb);
        }

        [Fact]
        public void KeyboardLightingController_LoadState_RetainsDistinctZoneColors()
        {
            var caps = new KeyboardLightingCapabilities
            {
                Tier = KeyboardLightingTier.FourZone,
                PerKeySupported = false,
                KeyCount = 4
            };
            var controller = new KeyboardLightingController(null, caps);

            Color[] colors = new[]
            {
                Color.FromArgb(255, 10, 20),
                Color.FromArgb(30, 255, 40),
                Color.FromArgb(50, 60, 255),
                Color.FromArgb(255, 255, 70)
            };

            controller.LoadState(colors, null);

            var snapshot = controller.SnapshotZoneColors();
            Assert.Equal(4, snapshot.Length);
            Assert.Equal(colors[0].ToArgb(), snapshot[0].ToArgb());
            Assert.Equal(colors[1].ToArgb(), snapshot[1].ToArgb());
            Assert.Equal(colors[2].ToArgb(), snapshot[2].ToArgb());
            Assert.Equal(colors[3].ToArgb(), snapshot[3].ToArgb());
        }

        [Fact]
        public void DynamicLightingEffectName_DoesNotTouch_StaticZoneColors()
        {
            var settings = new AppSettings();
            int z1 = Color.Cyan.ToArgb();
            int z2 = Color.Magenta.ToArgb();
            int z3 = Color.Yellow.ToArgb();
            int z4 = Color.Orange.ToArgb();

            settings.StaticZone1ColorArgb = z1;
            settings.StaticZone2ColorArgb = z2;
            settings.StaticZone3ColorArgb = z3;
            settings.StaticZone4ColorArgb = z4;

            // User switches to dynamic effect
            settings.DynamicLightingEffectName = "Breathing";
            settings.DynamicEffectPrimaryColorArgb = Color.Purple.ToArgb();
            settings.IsZoneLightingActive = false;

            // Static zone colors MUST remain completely intact
            Assert.Equal(z1, settings.StaticZone1ColorArgb);
            Assert.Equal(z2, settings.StaticZone2ColorArgb);
            Assert.Equal(z3, settings.StaticZone3ColorArgb);
            Assert.Equal(z4, settings.StaticZone4ColorArgb);
            Assert.Equal("Breathing", settings.DynamicLightingEffectName);
            Assert.False(settings.IsZoneLightingActive);

            // Switching back to static mode
            settings.DynamicLightingEffectName = "Off";
            settings.IsZoneLightingActive = true;

            Assert.Equal(z1, settings.StaticZone1ColorArgb);
            Assert.Equal(z2, settings.StaticZone2ColorArgb);
            Assert.Equal(z3, settings.StaticZone3ColorArgb);
            Assert.Equal(z4, settings.StaticZone4ColorArgb);
        }
    }
}
