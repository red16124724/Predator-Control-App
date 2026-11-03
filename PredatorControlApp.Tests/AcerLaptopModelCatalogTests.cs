using System;
using System.Linq;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class AcerLaptopModelCatalogTests
    {
        [Theory]
        // Helios
        [InlineData("Predator PH315-53", 2020, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("PH315-54", 2021, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Predator PH315-55", 2022, KeyboardLightingTier.FourZone, true, FanConfiguration.DualFan, true)]
        [InlineData("PH317-54", 2020, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("PH317-55", 2021, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("PH317-56", 2022, KeyboardLightingTier.FourZone, true, FanConfiguration.DualFan, true)]
        [InlineData("Predator Helios 16 PH16-71", 2023, KeyboardLightingTier.PerKey, true, FanConfiguration.DualFan, true)]
        [InlineData("Predator PH16-72", 2024, KeyboardLightingTier.PerKey, true, FanConfiguration.DualFan, true)]
        [InlineData("PH16-73", 2025, KeyboardLightingTier.PerKey, true, FanConfiguration.DualFan, true)]
        [InlineData("Predator Helios 18 PH18-71", 2023, KeyboardLightingTier.PerKey, true, FanConfiguration.DualFan, true)]
        [InlineData("PH18-72", 2024, KeyboardLightingTier.PerKey, true, FanConfiguration.DualFan, true)]
        [InlineData("PH18-73", 2025, KeyboardLightingTier.PerKey, true, FanConfiguration.DualFan, true)]
        public void HeliosModels_DetectCorrectProfiles(
            string model, int expectedYear, KeyboardLightingTier expectedTier,
            bool expectedLightbar, FanConfiguration expectedFans, bool expectedPl)
        {
            var profile = AcerLaptopModelCatalog.Lookup(model);
            Assert.True(profile.IsMatchedFromCatalog);
            Assert.Equal(expectedYear, profile.Year);
            Assert.Equal(expectedTier, profile.DefaultLightingTier);
            Assert.Equal(expectedLightbar, profile.HasRearLightbar);
            Assert.Equal(expectedFans, profile.FanConfiguration);
            Assert.Equal(expectedPl, profile.SupportsCpuPlControl);
        }

        [Theory]
        // Helios Neo
        [InlineData("Predator Helios Neo 14 PHN14-51", 2024, KeyboardLightingTier.ThreeZone, false, FanConfiguration.DualFan, true)]
        [InlineData("PHN16-71", 2023, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("PHN16-72", 2024, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("PHN16-73", 2025, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Predator Helios Neo 18 PHN18-71", 2024, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Predator Helios Neo 18 PHN18-72", 2024, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        public void HeliosNeoModels_DetectCorrectProfiles(
            string model, int expectedYear, KeyboardLightingTier expectedTier,
            bool expectedLightbar, FanConfiguration expectedFans, bool expectedPl)
        {
            var profile = AcerLaptopModelCatalog.Lookup(model);
            Assert.True(profile.IsMatchedFromCatalog);
            Assert.Equal(expectedYear, profile.Year);
            Assert.Equal(expectedTier, profile.DefaultLightingTier);
            Assert.Equal(expectedLightbar, profile.HasRearLightbar);
            Assert.Equal(expectedFans, profile.FanConfiguration);
            Assert.Equal(expectedPl, profile.SupportsCpuPlControl);
        }

        [Theory]
        // Triton
        [InlineData("PT314-51s", 2021, KeyboardLightingTier.ThreeZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Predator Triton 300 SE PT314-52s", 2022, KeyboardLightingTier.ThreeZone, false, FanConfiguration.DualFan, true)]
        [InlineData("PT315-52", 2020, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("PT315-53", 2021, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Predator Triton 500 PT515-52", 2020, KeyboardLightingTier.PerKey, false, FanConfiguration.TripleFan, true)]
        [InlineData("PT516-51s", 2021, KeyboardLightingTier.ThreeZone, false, FanConfiguration.TripleFan, true)]
        [InlineData("PT516-52s", 2022, KeyboardLightingTier.ThreeZone, false, FanConfiguration.TripleFan, true)]
        [InlineData("Predator Triton 14 PT14-51", 2023, KeyboardLightingTier.ThreeZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Predator Triton 17 X PTX17-71", 2023, KeyboardLightingTier.PerKey, true, FanConfiguration.TripleFan, true)]
        [InlineData("Predator Triton Neo 16 PTN16-51", 2024, KeyboardLightingTier.ThreeZone, false, FanConfiguration.DualFan, true)]
        public void TritonModels_DetectCorrectProfiles(
            string model, int expectedYear, KeyboardLightingTier expectedTier,
            bool expectedLightbar, FanConfiguration expectedFans, bool expectedPl)
        {
            var profile = AcerLaptopModelCatalog.Lookup(model);
            Assert.True(profile.IsMatchedFromCatalog);
            Assert.Equal(expectedYear, profile.Year);
            Assert.Equal(expectedTier, profile.DefaultLightingTier);
            Assert.Equal(expectedLightbar, profile.HasRearLightbar);
            Assert.Equal(expectedFans, profile.FanConfiguration);
            Assert.Equal(expectedPl, profile.SupportsCpuPlControl);
        }

        [Theory]
        // Nitro 5 AMD & Intel (All Generations: 2017 - 2023)
        [InlineData("Nitro AN515-41", 2017, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN515-42", 2018, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, false)]
        [InlineData("Nitro AN515-43", 2019, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, false)]
        [InlineData("Nitro AN515-44", 2020, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN515-45", 2021, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN515-46", 2022, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN515-47", 2023, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("Nitro AN515-51", 2017, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN515-52", 2018, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN515-53", 2018, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Nitro AN515-54", 2019, KeyboardLightingTier.ThreeZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN515-55", 2020, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN515-56", 2021, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN515-57", 2021, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Nitro AN515-58", 2022, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN517-41", 2021, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN517-42", 2022, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN517-51", 2019, KeyboardLightingTier.ThreeZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN517-52", 2020, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN517-53", 2021, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN517-54", 2021, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN517-55", 2022, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        public void Nitro5Models_DetectCorrectProfiles(
            string model, int expectedYear, KeyboardLightingTier expectedTier,
            bool expectedLightbar, FanConfiguration expectedFans, bool expectedPl)
        {
            var profile = AcerLaptopModelCatalog.Lookup(model);
            Assert.True(profile.IsMatchedFromCatalog);
            Assert.Equal(expectedYear, profile.Year);
            Assert.Equal(expectedTier, profile.DefaultLightingTier);
            Assert.Equal(expectedLightbar, profile.HasRearLightbar);
            Assert.Equal(expectedFans, profile.FanConfiguration);
            Assert.Equal(expectedPl, profile.SupportsCpuPlControl);
        }

        [Theory]
        // Nitro 16 / 17 / 18 / 14 (2023 - 2026+)
        [InlineData("Acer Nitro 16 AN16-41", 2023, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN16-42", 2024, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN16-43", 2025, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN16-51", 2023, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN16-71", 2024, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN16-72", 2025, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN16-73", 2025, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Nitro AN17-41", 2023, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN17-42", 2024, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN17-51", 2023, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN17-71", 2024, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("AN17-72", 2025, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Nitro 18 AN18-71", 2024, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Acer Nitro 18 AN18-72", 2025, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Nitro 14 AN14-41", 2024, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, false)]
        [InlineData("AN14-51", 2024, KeyboardLightingTier.FourZone, false, FanConfiguration.DualFan, true)]
        public void NitroModernModels_DetectCorrectProfiles(
            string model, int expectedYear, KeyboardLightingTier expectedTier,
            bool expectedLightbar, FanConfiguration expectedFans, bool expectedPl)
        {
            var profile = AcerLaptopModelCatalog.Lookup(model);
            Assert.True(profile.IsMatchedFromCatalog);
            Assert.Equal(expectedYear, profile.Year);
            Assert.Equal(expectedTier, profile.DefaultLightingTier);
            Assert.Equal(expectedLightbar, profile.HasRearLightbar);
            Assert.Equal(expectedFans, profile.FanConfiguration);
            Assert.Equal(expectedPl, profile.SupportsCpuPlControl);
        }

        [Theory]
        // Nitro V 14 / 15 / 16 (SingleZone Monochrome/White/Orange)
        [InlineData("Acer Nitro V 14 ANV14-61", 2024, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, false)]
        [InlineData("Nitro V 15 ANV15-41", 2024, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, false)]
        [InlineData("ANV15-51", 2023, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Acer Nitro V 15 ANV15-52", 2024, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, true)]
        [InlineData("ANV16-41", 2024, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, false)]
        [InlineData("ANV16-51", 2024, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, true)]
        [InlineData("Nitro V 16 ANV16-71", 2024, KeyboardLightingTier.SingleZone, false, FanConfiguration.DualFan, true)]
        public void NitroVModels_DetectCorrectProfiles(
            string model, int expectedYear, KeyboardLightingTier expectedTier,
            bool expectedLightbar, FanConfiguration expectedFans, bool expectedPl)
        {
            var profile = AcerLaptopModelCatalog.Lookup(model);
            Assert.True(profile.IsMatchedFromCatalog);
            Assert.Equal(expectedYear, profile.Year);
            Assert.Equal(expectedTier, profile.DefaultLightingTier);
            Assert.Equal(expectedLightbar, profile.HasRearLightbar);
            Assert.Equal(expectedFans, profile.FanConfiguration);
            Assert.Equal(expectedPl, profile.SupportsCpuPlControl);
        }

        [Theory]
        [InlineData("AN14-41", false)]
        [InlineData("AN14-51", false)]
        [InlineData("ANV14-61", false)]
        [InlineData("AN515-58", true)]
        [InlineData("AN16-41", true)]
        [InlineData("AN17-51", true)]
        [InlineData("AN18-71", true)]
        [InlineData("ANV15-51", true)]
        [InlineData("ANV16-41", true)]
        public void NumpadPresence_DetectsCorrectLayout(string model, bool expectedNumpad)
        {
            var profile = AcerLaptopModelCatalog.Lookup(model);
            Assert.Equal(expectedNumpad, profile.HasFullKeyboardWithNumpad);
        }

        [Theory]
        // 4-zone Nitro 5
        [InlineData("AN515-44", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN515-45", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN515-46", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN515-47", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN515-55", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN515-56", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN515-57", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN515-58", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN517-41", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN517-42", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN517-52", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN517-53", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN517-54", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN517-55", KeyboardLightingTier.FourZone, 4)]
        // 4-zone Nitro 14 / 16 / 17 / 18
        [InlineData("AN14-41", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN14-51", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN16-41", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN16-42", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN16-43", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN16-51", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN16-71", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN16-72", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN16-73", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN17-41", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN17-42", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN17-51", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN17-71", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN17-72", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN18-71", KeyboardLightingTier.FourZone, 4)]
        [InlineData("AN18-72", KeyboardLightingTier.FourZone, 4)]
        // 3-zone Nitro 5
        [InlineData("AN515-54", KeyboardLightingTier.ThreeZone, 3)]
        [InlineData("AN517-51", KeyboardLightingTier.ThreeZone, 3)]
        // 1-zone Legacy Nitro 5
        [InlineData("AN515-31", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("AN515-41", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("AN515-42", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("AN515-43", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("AN515-51", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("AN515-52", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("AN515-53", KeyboardLightingTier.SingleZone, 1)]
        // 1-zone Nitro V models
        [InlineData("ANV14-61", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("ANV15-41", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("ANV15-51", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("ANV15-52", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("ANV16-41", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("ANV16-51", KeyboardLightingTier.SingleZone, 1)]
        [InlineData("ANV16-71", KeyboardLightingTier.SingleZone, 1)]
        public void KeyboardLightingDetector_CorrectlyClassifiesNitroTiers(string model, KeyboardLightingTier expectedTier, int expectedZones)
        {
            var caps = KeyboardLightingDetector.Classify(model, "V1.0", "Mainboard", "fp-test", serviceReachable: true, wmiZonePath: true, tierOverride: 0);
            Assert.Equal(expectedTier, caps.Tier);
            Assert.Equal(expectedZones, caps.ZoneCount);
        }

        [Theory]
        [InlineData("AN14-41", false, KeyboardLayouts.AnsiTklId, KeyboardLayouts.AnsiTklKeyCount)]
        [InlineData("AN14-51", false, KeyboardLayouts.AnsiTklId, KeyboardLayouts.AnsiTklKeyCount)]
        [InlineData("ANV14-61", false, KeyboardLayouts.AnsiTklId, KeyboardLayouts.AnsiTklKeyCount)]
        [InlineData("AN515-45", true, KeyboardLayouts.FullAnsiId, KeyboardLayouts.FullAnsiKeyCount)]
        [InlineData("AN515-58", true, KeyboardLayouts.FullAnsiId, KeyboardLayouts.FullAnsiKeyCount)]
        [InlineData("AN16-41", true, KeyboardLayouts.FullAnsiId, KeyboardLayouts.FullAnsiKeyCount)]
        [InlineData("AN17-51", true, KeyboardLayouts.FullAnsiId, KeyboardLayouts.FullAnsiKeyCount)]
        [InlineData("ANV15-51", true, KeyboardLayouts.FullAnsiId, KeyboardLayouts.FullAnsiKeyCount)]
        [InlineData("ANV16-41", true, KeyboardLayouts.FullAnsiId, KeyboardLayouts.FullAnsiKeyCount)]
        public void NitroModels_DetectCorrectKeyboardLayoutAndKeyCount(
            string model, bool expectedNumpad, string expectedLayoutId, int expectedKeyCount)
        {
            var profile = AcerLaptopModelCatalog.Lookup(model);
            Assert.Equal(expectedNumpad, profile.HasFullKeyboardWithNumpad);

            var caps = KeyboardLightingDetector.Classify(model, "V1.0", null, null, serviceReachable: true, wmiZonePath: true, tierOverride: 0);
            Assert.Equal(expectedLayoutId, caps.LayoutId);
            Assert.Equal(expectedKeyCount, caps.KeyCount);
        }

        [Fact]
        public void HeuristicFallback_UnrecognizedPH16_ResolvesPerKey()
        {
            var profile = AcerLaptopModelCatalog.Lookup("Future Predator PH16-99");
            Assert.False(profile.IsMatchedFromCatalog);
            Assert.Equal(KeyboardLightingTier.PerKey, profile.DefaultLightingTier);
            Assert.True(profile.HasRearLightbar);
            Assert.Equal(FanConfiguration.DualFan, profile.FanConfiguration);
            Assert.True(profile.SupportsCpuPlControl);
        }

        [Fact]
        public void HeuristicFallback_UnrecognizedPHN_ResolvesFourZone()
        {
            var profile = AcerLaptopModelCatalog.Lookup("Predator PHN19-99");
            Assert.False(profile.IsMatchedFromCatalog);
            Assert.Equal(KeyboardLightingTier.FourZone, profile.DefaultLightingTier);
        }

        [Fact]
        public void HeuristicFallback_UnrecognizedPT314_ResolvesThreeZone()
        {
            var profile = AcerLaptopModelCatalog.Lookup("Predator PT314-99");
            Assert.False(profile.IsMatchedFromCatalog);
            Assert.Equal(KeyboardLightingTier.ThreeZone, profile.DefaultLightingTier);
        }

        [Fact]
        public void HeuristicFallback_UnrecognizedANV15_ResolvesSingleZone()
        {
            var profile = AcerLaptopModelCatalog.Lookup("Acer ANV15-99");
            Assert.False(profile.IsMatchedFromCatalog);
            Assert.Equal(KeyboardLightingTier.SingleZone, profile.DefaultLightingTier);
        }

        [Fact]
        public void HeuristicFallback_UnrecognizedAN18_ResolvesFourZone()
        {
            var profile = AcerLaptopModelCatalog.Lookup("Acer AN18-99");
            Assert.False(profile.IsMatchedFromCatalog);
            Assert.Equal(KeyboardLightingTier.FourZone, profile.DefaultLightingTier);
        }

        [Fact]
        public void GenericFallback_UnrecognizedAlienLaptop_ResolvesFourZoneDualFan()
        {
            var profile = AcerLaptopModelCatalog.Lookup("NonExistent OEM Brand 1234");
            Assert.False(profile.IsMatchedFromCatalog);
            Assert.Equal(KeyboardLightingTier.FourZone, profile.DefaultLightingTier);
            Assert.Equal(FanConfiguration.DualFan, profile.FanConfiguration);
            Assert.False(profile.SupportsCpuPlControl);
        }

        [Fact]
        public void BaseBoardProduct_MatchesWhenModelNameIsGeneric()
        {
            var profile = AcerLaptopModelCatalog.Lookup("Generic Device", "PH16-71");
            Assert.True(profile.IsMatchedFromCatalog);
            Assert.Equal(KeyboardLightingTier.PerKey, profile.DefaultLightingTier);
            Assert.Contains("Helios 16", profile.MarketingName);
        }

        [Fact]
        public void KeyboardLightingCapabilities_ContainsModelProfileAndRearLightbar()
        {
            var caps = KeyboardLightingDetector.Classify("PH16-72", "V1.0", "Mainboard", "fp-123", serviceReachable: true, wmiZonePath: true, tierOverride: 0);
            Assert.NotNull(caps.ModelProfile);
            Assert.Equal(KeyboardLightingTier.PerKey, caps.Tier);
            Assert.True(caps.HasRearLightbar);
            Assert.Equal(4, caps.ZoneCount);
        }
    }
}
