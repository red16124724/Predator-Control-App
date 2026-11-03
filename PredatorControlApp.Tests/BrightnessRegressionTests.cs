using System.Drawing;
using Xunit;

namespace PredatorControlApp.Tests;

public sealed class BrightnessRegressionTests
{
    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 128, 64),
        Color.FromArgb(17, 255, 1),
        Color.FromArgb(1, 33, 255),
        Color.FromArgb(255, 255, 255)
    };

    public static IEnumerable<object[]> LayoutBrightnessCases()
    {
        foreach (int zones in new[] { 1, 2, 3, 4 })
        foreach (int brightness in new[] { 0, 1, 50, 100 })
            yield return new object[] { zones, brightness };
    }

    [Theory]
    [MemberData(nameof(LayoutBrightnessCases))]
    public void ZonePackets_ApplyBrightnessOnce_ForEveryLayout(int zones, int brightness)
    {
        using var wmi = new WmiController(enableHardware: false);
        Assert.Null(wmi.EcHid);
        var arrays = new List<(string Method, byte[] Payload)>();
        var legacy = new List<ulong>();
        wmi.LedArrayCommandInterceptor = (method, payload) =>
            arrays.Add((method, (byte[])payload.Clone()));
        wmi.CommandInterceptor = (method, payload) =>
        {
            if (method == "SetGamingRgbKb") legacy.Add(payload);
        };

        Color[] colors = Palette.Take(zones).ToArray();
        Assert.True(wmi.SetZoneColors(colors, brightness));
        Assert.Equal(brightness, wmi.Brightness);
        Assert.NotEmpty(arrays);
        Assert.NotEmpty(legacy);

        foreach (string method in new[] { "SetGamingKBBacklight", "SetGamingLED" })
        {
            var colorPackets = arrays.Where(command => command.Method == method &&
                command.Payload[9] != 0 &&
                (command.Payload[5] != 0 || command.Payload[6] != 0 || command.Payload[7] != 0)).ToArray();

            if (brightness == 0)
            {
                Assert.All(arrays.Where(command => command.Method == method),
                    command => Assert.Equal(0, command.Payload[2]));
            }
            else
            {
                Assert.NotEmpty(colorPackets);
                foreach (var (_, packet) in colorPackets)
                {
                    Color expected = colors[zones == 1 ? 0 : zones == 2 ? (packet[9] - 1) / 2 : packet[9] - 1];
                    Assert.Equal(brightness, packet[2]);
                    Assert.Equal(expected.R, packet[5]);
                    Assert.Equal(expected.G, packet[6]);
                    Assert.Equal(expected.B, packet[7]);
                }
            }
        }

        foreach (ulong payload in legacy)
        {
            byte mask = (byte)payload;
            Color expected = zones switch
            {
                1 => colors[0],
                2 => colors[mask <= 2 ? 0 : 1],
                _ => colors[mask switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => throw new InvalidOperationException("Unexpected zone mask") }]
            };
            Assert.Equal((brightness == 0 ? (byte)0 : expected.R), (byte)(payload >> 8));
            Assert.Equal((brightness == 0 ? (byte)0 : expected.G), (byte)(payload >> 16));
            Assert.Equal((brightness == 0 ? (byte)0 : expected.B), (byte)(payload >> 24));
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 50)]
    [InlineData(0, 100)]
    [InlineData(1, 1)]
    [InlineData(1, 50)]
    [InlineData(1, 100)]
    [InlineData(3, 1)]
    [InlineData(3, 50)]
    [InlineData(3, 100)]
    [InlineData(6, 1)]
    [InlineData(6, 50)]
    [InlineData(6, 100)]
    public void HardwareEffectPackets_PreserveColor_AndCarryBrightnessSeparately(int mode, int brightness)
    {
        using var wmi = new WmiController(enableHardware: false);
        var arrays = new List<(string Method, byte[] Payload)>();
        wmi.LedArrayCommandInterceptor = (method, payload) =>
            arrays.Add((method, (byte[])payload.Clone()));

        wmi.SetRgbMode(mode, 255, 128, 64, (byte)brightness, 5, 0);

        foreach (string method in new[] { "SetGamingKBBacklight", "SetGamingLED" })
        {
            var packet = arrays.Last(command => command.Method == method).Payload;
            Assert.Equal(mode, packet[0]);
            Assert.Equal(brightness, packet[2]);
            Assert.Equal(255, packet[5]);
            Assert.Equal(128, packet[6]);
            Assert.Equal(64, packet[7]);
        }
    }

    [Fact]
    public void MovingBrightnessSlider_ToZero_TurnsOffBothArrayProtocols()
    {
        using var wmi = new WmiController(enableHardware: false);
        var arrays = new List<byte[]>();
        wmi.LedArrayCommandInterceptor = (_, payload) => arrays.Add((byte[])payload.Clone());
        wmi.SetStaticColor(255, 128, 64, 100);
        arrays.Clear();

        wmi.SetBrightness(0);

        Assert.Equal(0, wmi.Brightness);
        Assert.NotEmpty(arrays);
        Assert.All(arrays, packet => Assert.Equal(0, packet[2]));
    }

    [Fact]
    public void ThreeZoneColors_MappedToFourZoneFirmware_KeepFullColorAtHalfBrightness()
    {
        using var wmi = new WmiController(enableHardware: false);
        var arrays = new List<byte[]>();
        var legacy = new List<ulong>();
        wmi.LedArrayCommandInterceptor = (_, payload) => arrays.Add((byte[])payload.Clone());
        wmi.CommandInterceptor = (method, payload) =>
        {
            if (method == "SetGamingRgbKb") legacy.Add(payload);
        };

        Assert.True(wmi.SetZoneColors(Palette.Take(3).ToArray(), 50, isNativeThreeZone: false));

        Assert.Equal(new byte[] { 1, 6, 8 }, legacy.Select(payload => (byte)payload).ToArray());
        var colored = arrays.Where(packet => packet[5] != 0 || packet[6] != 0 || packet[7] != 0).ToArray();
        Assert.NotEmpty(colored);
        Assert.All(colored, packet => Assert.Equal(50, packet[2]));
        Assert.Contains(colored, packet => packet[5] == 255 && packet[6] == 128 && packet[7] == 64);
        Assert.Contains(colored, packet => packet[5] == 17 && packet[6] == 255 && packet[7] == 1);
        Assert.Contains(colored, packet => packet[5] == 1 && packet[6] == 33 && packet[7] == 255);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void LegacyOnlyFirmware_ScalesColorOnceWhenGlobalBrightnessIsUnsupported(int brightness)
    {
        using var wmi = new WmiController(enableHardware: false);
        wmi.LedArrayCommandResultOverride = (_, _) => false;
        var commands = new List<ulong>();
        wmi.CommandInterceptor = (method, value) =>
        {
            if (method == "SetGamingRgbKb") commands.Add(value);
        };
        wmi.SetBrightness((byte)brightness, applyMode: false);
        commands.Clear();

        Assert.True(wmi.SetZoneColor(1, 255, 128, 64));

        ulong packet = Assert.Single(commands);
        Assert.Equal(Scaled(255), (packet >> 8) & 0xFF);
        Assert.Equal(Scaled(128), (packet >> 16) & 0xFF);
        Assert.Equal(Scaled(64), (packet >> 24) & 0xFF);
        ulong Scaled(int channel) => (ulong)(brightness == 0 ? 0 : Math.Max(1, channel * brightness / 100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void RejectedGlobalBrightnessUpdate_DoesNotWriteMisleadingLegacyColor(int previousBrightness)
    {
        using var wmi = new WmiController(enableHardware: false);
        wmi.SetZoneColors(new[] { Color.White }, previousBrightness);
        wmi.LedArrayCommandResultOverride = (_, _) => false;
        var commands = new List<ulong>();
        wmi.CommandInterceptor = (method, value) =>
        {
            if (method == "SetGamingRgbKb") commands.Add(value);
        };
        wmi.SetBrightness(100, applyMode: false);

        Assert.False(wmi.SetZoneColor(1, 255, 255, 255));
        Assert.Empty(commands);
    }

    [Fact]
    public void OffThenRejectedBrightnessRestore_RemembersAcknowledgedZero()
    {
        using var wmi = new WmiController(enableHardware: false);
        wmi.TurnOffBacklight();
        wmi.LedArrayCommandResultOverride = (_, _) => false;
        wmi.SetBrightness(100, applyMode: false);

        Assert.False(wmi.SetZoneColor(1, 255, 255, 255));
    }

    [Fact]
    public async Task SuccessfulNativeZoneWrite_DoesNotDispatchQuantizedServiceBrightness()
    {
        using var wmi = new WmiController(enableHardware: false);
        var caps = KeyboardLightingDetector.Classify("Predator PH315-54", "V1", null, null,
            serviceReachable: true, wmiZonePath: true, tierOverride: 0);
        int serviceCalls = 0;
        using var controller = new KeyboardLightingController(wmi, caps,
            zoneWriter: (_, _, _, _) => { serviceCalls++; return Task.FromResult(true); });
        controller.Brightness = 37;

        Assert.True((await controller.ApplyAsync(KeyboardLightingScope.Zones)).Success);

        Assert.Equal(0, serviceCalls);
        Assert.Equal(37, wmi.Brightness);
    }
}
