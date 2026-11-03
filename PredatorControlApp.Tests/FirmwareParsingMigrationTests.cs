using Xunit;

namespace PredatorControlApp.Tests;

[Collection("NoParallel")]
public sealed class FirmwareParsingMigrationTests
{
    private static readonly byte[] Valid =
    {
        172, 12, 0, 0, 3, 7, 1, 0x34, 0x12, 2, 0x78, 0x56, 0, 0,
        170, 22, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x81, 2, 4, 0, 0x82, 1, 5, 0, 0, 0,
        127, 4, 0, 0, 0, 0
    };

    [Fact]
    public void NativeSmbiosParser_ReturnsGamingAndHotkeyRecords()
    {
        Assert.True(FirmwareParsingNative.TryParseSmbios(Valid, out var result));
        Assert.Equal((byte)3, result.GamingMajor);
        Assert.Equal((byte)7, result.GamingMinor);
        Assert.Equal(new[] { new AcerSmbiosRecord(1, 0x1234), new AcerSmbiosRecord(2, 0x5678) }, result.GamingRecords);
        Assert.Equal(new[] { new AcerSmbiosRecord(0x81, 4) }, result.HotkeyFunctions);
    }

    [Fact]
    public void NativeSmbiosParser_MatchesManagedForTruncationAndMutations()
    {
        for (int length = 1; length <= Valid.Length; length++)
            AssertParity(Valid[..length]);

        var random = new Random(82414);
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            byte[] candidate = (byte[])Valid.Clone();
            for (int i = 0; i < 1 + iteration % 5; i++) candidate[random.Next(candidate.Length)] = (byte)random.Next(256);
            AssertParity(candidate);
        }
    }

    private static void AssertParity(byte[] table)
    {
        Assert.True(FirmwareParsingNative.TryParseSmbios(table, out var actual));
        var expected = AcerSmbios.ParseManaged(table);
        Assert.Equal(expected.GamingMajor, actual.GamingMajor);
        Assert.Equal(expected.GamingMinor, actual.GamingMinor);
        Assert.Equal(expected.GamingRecords, actual.GamingRecords);
        Assert.Equal(expected.HotkeyFunctions, actual.HotkeyFunctions);
        var production = AcerSmbios.Parse(table);
        Assert.Equal(actual.GamingRecords, production.GamingRecords);
        Assert.Equal(actual.HotkeyFunctions, production.HotkeyFunctions);
    }
}
