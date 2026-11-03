using System;
using System.Drawing;
using Xunit;

namespace PredatorControlApp.Tests;

public sealed class LightingRulesRustParityTests
{
    [Fact]
    public void CapabilitySelection_MatchesManagedFallbackForEveryInputCombination()
    {
        Assert.True(PredatorNativeCore.IsNativeCoreAvailable);
        foreach (int tierOverride in new[] { -1, 0, 1, 2, 3, 4, 5 })
        for (int bits = 0; bits < 32; bits++)
        {
            bool perKey = (bits & 1) != 0, three = (bits & 2) != 0;
            bool single = (bits & 4) != 0, service = (bits & 8) != 0, wmi = (bits & 16) != 0;
            Assert.True(NativeLightingRules.TrySelectTier(tierOverride, perKey, three, single, service, wmi, out var actual));
            Assert.Equal(KeyboardLightingDetector.SelectTierManaged(tierOverride, perKey, three, single, service, wmi), actual);
        }
    }

    [Theory]
    [InlineData("abc", unchecked((int)0xffaabbcc))]
    [InlineData("00FF80", unchecked((int)0xff00ff80))]
    [InlineData("80112233", unchecked((int)0x80112233))]
    [InlineData("00000000", 0)]
    public void NativeHexPalette_ParsesRgbAndArgb(string text, int expected)
    {
        Assert.True(PredatorNativeCore.IsNativeCoreAvailable);
        Assert.True(NativeLightingRules.TryParseHex(text, out var parsed));
        Assert.Equal(expected, parsed.ToArgb());
        Assert.True(KeyboardLightingPalette.TryParse("#" + text, out var publicParsed));
        Assert.Equal(expected, publicParsed.ToArgb());
    }

    [Theory]
    [InlineData("zzzzzz")]
    [InlineData("1234")]
    [InlineData("\u00e9aa")]
    public void InvalidColors_KeepDefaultFallback(string text)
    {
        Assert.False(KeyboardLightingPalette.TryParse(text, out var value));
        Assert.Equal(KeyboardLightingPalette.DefaultZoneColor.ToArgb(), value.ToArgb());
    }

    [Fact]
    public void ServiceLevels_MatchManagedRulesAcrossBoundaries()
    {
        Assert.True(PredatorNativeCore.IsNativeCoreAvailable);
        for (int value = -200; value <= 200; value++)
        {
            int brightness = value <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(value / 20.0), 1, 5);
            int speed = Math.Clamp((int)Math.Round(Math.Clamp(value, 1, 100) / 100.0 * 5.0), 1, 5);
            Assert.True(NativeLightingRules.TryServiceLevel(value, false, out int actualBrightness));
            Assert.True(NativeLightingRules.TryServiceLevel(value, true, out int actualSpeed));
            Assert.Equal(brightness, actualBrightness);
            Assert.Equal(speed, actualSpeed);
            Assert.Equal(brightness, AcerServiceClient.ServiceBrightness(value));
            Assert.Equal(speed, AcerServiceClient.ServiceSpeed(value));
        }
        Assert.Equal(5, AcerServiceClient.ServiceBrightness(int.MaxValue));
        Assert.Equal(0, AcerServiceClient.ServiceBrightness(int.MinValue));
    }

    [Fact]
    public void ColorNames_ContinueUsingManagedParsing()
    {
        Assert.True(KeyboardLightingPalette.TryParse("Red", out var parsed));
        Assert.Equal(Color.Red.ToArgb(), parsed.ToArgb());
    }

    [Fact]
    public void ManagedFallback_WorksWhenNativeCoreIsUnavailable()
    {
        Assert.True(PredatorNativeCore.IsNativeCoreAvailable);
        var flag = typeof(PredatorNativeCore).GetField("_circuitBreakerTripped", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        flag.SetValue(null, true);
        try
        {
            Assert.False(NativeLightingRules.TryServiceLevel(60, false, out _));
            Assert.False(NativeLightingRules.TryParseHex("abcdef", out _));
            Assert.False(NativeLightingRules.TryBlend(Color.Black, Color.White, 0.5, out _));
            Assert.False(NativeLightingRules.TrySelectTier(0, false, false, false, true, false, out _));
            Assert.Equal(3, AcerServiceClient.ServiceBrightness(60));
            Assert.Equal(2, AcerServiceClient.ServiceSpeed(50));
            Assert.True(KeyboardLightingPalette.TryParse("#abcdef", out var parsed));
            Assert.Equal(unchecked((int)0xffabcdef), parsed.ToArgb());
            Assert.Equal(unchecked((int)0xff808080), KeyboardLightingPalette.Blend(Color.Black, Color.White, 0.5).ToArgb());
            var caps = KeyboardLightingDetector.Classify("Predator PH315-54", "V1", null, null, true, true, 0);
            Assert.Equal(KeyboardLightingTier.FourZone, caps.Tier);
        }
        finally { PredatorNativeCore.ResetCircuitBreakerForTesting(); }
    }

    [Fact]
    public void Blend_MatchesManagedFormulaAcrossAlphaAndClampedLevels()
    {
        Assert.True(PredatorNativeCore.IsNativeCoreAvailable);
        var random = new Random(219);
        double[] levels = { -1, 0, 0.001, 0.1, 0.5, 0.73, 1, 2 };
        for (int i = 0; i < 1000; i++)
        {
            Color a = Color.FromArgb(random.Next(256), random.Next(256), random.Next(256), random.Next(256));
            Color b = Color.FromArgb(random.Next(256), random.Next(256), random.Next(256), random.Next(256));
            double amount = levels[i % levels.Length];
            double t = Math.Clamp(amount, 0, 1);
            Color expected = Color.FromArgb((int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
            Assert.True(NativeLightingRules.TryBlend(a, b, amount, out var actual));
            Assert.Equal(expected.ToArgb(), actual.ToArgb());
            Assert.Equal(expected.ToArgb(), KeyboardLightingPalette.Blend(a, b, amount).ToArgb());
        }
        Assert.False(NativeLightingRules.TryBlend(Color.Red, Color.Blue, double.NaN, out _));
    }
}
