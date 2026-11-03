using System;
using System.Drawing;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    public class PerZonePersistenceAndDynamicOverhaulTests
    {
        [Fact]
        public void Test_LightingEffectsManager_TwinklingRemoved_CountIs33()
        {
            Assert.Equal(33, LightingEffectsManager.EffectNames.Length);
            Assert.DoesNotContain("Twinkling", LightingEffectsManager.EffectNames);
            Assert.Equal("Static", LightingEffectsManager.EffectNames[0]);
            Assert.Equal("Breathing", LightingEffectsManager.EffectNames[1]);
            Assert.Equal("Neon", LightingEffectsManager.EffectNames[2]);
            Assert.Equal("Wave", LightingEffectsManager.EffectNames[3]);
            Assert.Equal("Meteor", LightingEffectsManager.EffectNames[6]);
            Assert.Equal("Snake", LightingEffectsManager.EffectNames[7]);
            Assert.Equal("Blasting", LightingEffectsManager.EffectNames[32]);
        }

        [Fact]
        public void Test_LightingEffectsManager_GetEffectIndex_Resolutions()
        {
            Assert.Equal(0, LightingEffectsManager.GetEffectIndex("Off"));
            Assert.Equal(0, LightingEffectsManager.GetEffectIndex("Off (Static)"));
            Assert.Equal(0, LightingEffectsManager.GetEffectIndex("Static"));
            Assert.Equal(0, LightingEffectsManager.GetEffectIndex("Twinkling")); // Migrated/fallback to 0
            Assert.Equal(1, LightingEffectsManager.GetEffectIndex("Breathing"));
            Assert.Equal(2, LightingEffectsManager.GetEffectIndex("Neon"));
            Assert.Equal(3, LightingEffectsManager.GetEffectIndex("Wave"));
            Assert.Equal(6, LightingEffectsManager.GetEffectIndex("Meteor"));
            Assert.Equal(7, LightingEffectsManager.GetEffectIndex("Snake"));
            Assert.Equal(32, LightingEffectsManager.GetEffectIndex("Blasting"));
        }

        [Fact]
        public void Test_AppSettings_TwinklingMigratedToOff_OnSanitize()
        {
            var settings = new AppSettings
            {
                DynamicLightingEffectName = "Twinkling"
            };
            settings.Sanitize();
            Assert.Equal("Off", settings.DynamicLightingEffectName);
        }

        [Fact]
        public void Test_AppSettings_PerZoneColors_SeparatedFromDynamicEffect()
        {
            var settings = new AppSettings
            {
                StaticZone1ColorArgb = Color.Red.ToArgb(),
                StaticZone2ColorArgb = Color.Green.ToArgb(),
                StaticZone3ColorArgb = Color.Blue.ToArgb(),
                StaticZone4ColorArgb = Color.Yellow.ToArgb(),
                DynamicLightingEffectName = "Wave",
                DynamicEffectPrimaryColorArgb = Color.Magenta.ToArgb(),
                DynamicEffectSecondaryColorArgb = Color.Cyan.ToArgb(),
                DynamicEffectSpeed = 75,
                DynamicEffectDirection = 1,
                KeyboardBrightnessLevel = 80
            };

            settings.Sanitize();

            // Verify StaticZone1..4 remain distinct and untouched by dynamic effect state
            Assert.Equal(Color.Red.ToArgb(), settings.StaticZone1ColorArgb);
            Assert.Equal(Color.Green.ToArgb(), settings.StaticZone2ColorArgb);
            Assert.Equal(Color.Blue.ToArgb(), settings.StaticZone3ColorArgb);
            Assert.Equal(Color.Yellow.ToArgb(), settings.StaticZone4ColorArgb);

            // Verify Dynamic Effect settings are maintained independently
            Assert.Equal("Wave", settings.DynamicLightingEffectName);
            Assert.Equal(Color.Magenta.ToArgb(), settings.DynamicEffectPrimaryColorArgb);
            Assert.Equal(Color.Cyan.ToArgb(), settings.DynamicEffectSecondaryColorArgb);
            Assert.Equal(75, settings.DynamicEffectSpeed);
            Assert.Equal(1, settings.DynamicEffectDirection);
            Assert.Equal(80, settings.KeyboardBrightnessLevel);
        }

        [Fact]
        public void Test_AppSettings_Clone_PreservesPerZoneAndDynamicIsolation()
        {
            var original = new AppSettings
            {
                StaticZone1ColorArgb = Color.DarkOrange.ToArgb(),
                StaticZone2ColorArgb = Color.Purple.ToArgb(),
                StaticZone3ColorArgb = Color.Pink.ToArgb(),
                StaticZone4ColorArgb = Color.Lime.ToArgb(),
                StaticZoneColorMapJson = "{\"custom\":true}",
                DynamicLightingEffectName = "Neon",
                DynamicEffectPrimaryColorArgb = Color.White.ToArgb(),
                DynamicEffectSecondaryColorArgb = Color.Black.ToArgb(),
                DynamicEffectSpeed = 60,
                DynamicEffectDirection = 0,
                KeyboardBrightnessLevel = 90
            };

            var clone = original.Clone();

            Assert.Equal(original.StaticZone1ColorArgb, clone.StaticZone1ColorArgb);
            Assert.Equal(original.StaticZone2ColorArgb, clone.StaticZone2ColorArgb);
            Assert.Equal(original.StaticZone3ColorArgb, clone.StaticZone3ColorArgb);
            Assert.Equal(original.StaticZone4ColorArgb, clone.StaticZone4ColorArgb);
            Assert.Equal(original.StaticZoneColorMapJson, clone.StaticZoneColorMapJson);
            Assert.Equal(original.DynamicLightingEffectName, clone.DynamicLightingEffectName);
            Assert.Equal(original.DynamicEffectPrimaryColorArgb, clone.DynamicEffectPrimaryColorArgb);
            Assert.Equal(original.DynamicEffectSecondaryColorArgb, clone.DynamicEffectSecondaryColorArgb);
            Assert.Equal(original.DynamicEffectSpeed, clone.DynamicEffectSpeed);
            Assert.Equal(original.DynamicEffectDirection, clone.DynamicEffectDirection);
            Assert.Equal(original.KeyboardBrightnessLevel, clone.KeyboardBrightnessLevel);

            // Mutating clone dynamic settings must not affect original static colors
            clone.DynamicLightingEffectName = "Off";
            clone.DynamicEffectPrimaryColorArgb = Color.Gold.ToArgb();
            clone.StaticZone1ColorArgb = Color.Silver.ToArgb();

            Assert.Equal(Color.DarkOrange.ToArgb(), original.StaticZone1ColorArgb);
            Assert.Equal("Neon", original.DynamicLightingEffectName);
        }
    }
}
