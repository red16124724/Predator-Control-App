using System.Drawing;
using System.Text.Json;
using Xunit;

namespace PredatorControlApp.Tests;

public class ZoneRestoreRegressionTests
{
    [Fact]
    public void LegacyJsonRestoresEveryZoneAndPreservesExplicitNewFields()
    {
        const string json = "{\"KeyboardZoneColors\":\"#FF0000,#00FF00,#0000FF,#FFFFFF\",\"StaticZone2ColorArgb\":-16777216}";
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        settings.RestoreMissingStaticZoneColorsFromJson(json);
        settings.Sanitize();
        Assert.Equal(Color.Red.ToArgb(), settings.StaticZone1ColorArgb);
        Assert.Equal(Color.Black.ToArgb(), settings.StaticZone2ColorArgb);
        Assert.Equal(Color.Blue.ToArgb(), settings.StaticZone3ColorArgb);
        Assert.Equal(Color.White.ToArgb(), settings.StaticZone4ColorArgb);
    }

    [Theory]
    [InlineData(4, KeyboardLightingTier.PerKey, 4)]
    [InlineData(0, KeyboardLightingTier.PerKey, 0)]
    [InlineData(0, KeyboardLightingTier.FourZone, 4)]
    [InlineData(4, KeyboardLightingTier.ThreeZone, 3)]
    [InlineData(4, KeyboardLightingTier.SingleZone, 1)]
    public void DetectionPreservesCompatibleSavedLayout(int saved, KeyboardLightingTier tier, int expected)
        => Assert.Equal(expected, Form1.ResolveSavedZoneLayout(saved, tier));
}
