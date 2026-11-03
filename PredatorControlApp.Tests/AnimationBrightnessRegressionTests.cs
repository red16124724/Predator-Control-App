using System.Drawing;
using System.Reflection;
using Xunit;

namespace PredatorControlApp.Tests;

[Collection("NoParallel")]
public class AnimationBrightnessRegressionTests
{
    [Theory]
    [InlineData(20, false)]
    [InlineData(50, false)]
    [InlineData(100, false)]
    [InlineData(20, true)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    public void SoftwareFrame_AppliesSliderBrightnessOnce(int brightness, bool forceManaged)
    {
        if (!forceManaged) Assert.True(PredatorNativeCore.IsNativeCoreAvailable);
        using var wmi = new WmiController(enableHardware: false);
        var commands = new List<ulong>();
        var keyboardPackets = new List<byte[]>();
        wmi.LedArrayCommandInterceptor = (method, payload) =>
        {
            if (method == "SetGamingKBBacklight") keyboardPackets.Add((byte[])payload.Clone());
        };
        wmi.CommandInterceptor = (method, value) =>
        {
            if (method == "SetGamingRgbKb") commands.Add(value);
        };
        wmi.SetBrightness((byte)brightness, applyMode: false);
        LightingEffectsManager.StopSoftwareAnimation();
        int generation = (int)typeof(LightingEffectsManager)
            .GetField("_currentAnimationGeneration", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

        LightingEffectsManager.ApplyAnimationFrame(
            LightingEffect34.Snake, wmi, 0, 255, 100, 50,
            CancellationToken.None, generation, forceManaged);

        Assert.Equal(4, commands.Count);
        Assert.Equal(1UL, commands[0] & 0xFF);
        Assert.Equal(255UL, (commands[0] >> 8) & 0xFF);
        Assert.Equal(100UL, (commands[0] >> 16) & 0xFF);
        Assert.Equal(50UL, (commands[0] >> 24) & 0xFF);
        byte[] brightnessPacket = Assert.Single(keyboardPackets);
        Assert.Equal(brightness, brightnessPacket[2]);
        Assert.Equal((ulong)(255 * brightness / 100),
            ((commands[0] >> 8) & 0xFF) * brightnessPacket[2] / 100);
        Assert.All(commands.Skip(1), value => Assert.Equal(0UL, value >> 8));
    }

    [Fact]
    public void ColorEffect_AppliesAlphaToColorWithoutDimmingSliderAgain()
    {
        using var wmi = new WmiController(enableHardware: false);

        LightingEffectsManager.ApplyEffect(0, wmi, Color.FromArgb(128, 255, 100, 50), 80, 5);

        Assert.Equal(80, wmi.Brightness);
        Assert.Equal(128, wmi.LastR);
        Assert.Equal(50, wmi.LastG);
        Assert.Equal(25, wmi.LastB);
    }

    [Fact]
    public void TransparentColorEffect_LeavesBacklightOff()
    {
        using var wmi = new WmiController(enableHardware: false);

        LightingEffectsManager.ApplyEffect(0, wmi, Color.FromArgb(0, 255, 100, 50), 100, 5);

        Assert.Equal(0, wmi.Brightness);
    }

    [Fact]
    public void AudioFrame_UsesRequestedBrightnessOnceWithoutStartingCapture()
    {
        Assert.True(PredatorNativeCore.IsNativeCoreAvailable);
        using var referenceMeter = new AudioMeter();
        using var meter = new AudioMeter();
        using var wmi = new WmiController(enableHardware: false);
        var commands = new List<ulong>();
        var keyboardPackets = new List<byte[]>();
        wmi.LedArrayCommandInterceptor = (method, payload) =>
        {
            if (method == "SetGamingKBBacklight") keyboardPackets.Add((byte[])payload.Clone());
        };
        wmi.CommandInterceptor = (method, value) =>
        {
            if (method == "SetGamingRgbKb") commands.Add(value);
        };
        float[] samples = Enumerable.Range(0, 8192)
            .Select(i => (float)(0.8 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0)))
            .ToArray();
        MethodInfo push = typeof(AudioMeter).GetMethod(
            "OnSamplesAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        push.Invoke(referenceMeter, new object[] { samples, samples.Length, 1 });
        push.Invoke(meter, new object[] { samples, samples.Length, 1 });
        byte[] fullFrame = new byte[12];
        Assert.Equal(4, referenceMeter.RenderZones(255, 100, 50, 100, fullFrame, 4));
        Assert.Contains(fullFrame, value => value > 0);
        wmi.SetBrightness(17, applyMode: false);
        Assert.True(wmi.SetZoneColor(1, (byte)255, (byte)100, (byte)50));
        Assert.Equal(17, Assert.Single(keyboardPackets)[2]);
        commands.Clear();
        keyboardPackets.Clear();

        Assert.True(LightingEffectsManager.ApplyAudioReactiveFrame(meter, wmi, 255, 100, 50, 50));

        Assert.Equal(50, wmi.Brightness);
        Assert.Equal(4, commands.Count);
        Assert.Equal(50, Assert.Single(keyboardPackets)[2]);
        for (int zone = 0; zone < 4; zone++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                int full = fullFrame[zone * 3 + channel];
                Assert.Equal((ulong)full, (commands[zone] >> (8 * (channel + 1))) & 0xFF);
            }
        }
        Assert.False(meter.IsCapturing);
    }
}
