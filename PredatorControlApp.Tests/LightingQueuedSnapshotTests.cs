using System.Drawing;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests;

public class LightingQueuedSnapshotTests
{
    [Fact]
    public async Task WaitingApplyUsesLatestZoneColorsAfterEarlierWriteFinishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<int>();
        KeyboardZoneWriter writer = async (zones, brightness, speed, token) =>
        {
            writes.Add(zones[0].ToArgb());
            if (writes.Count == 1) { entered.SetResult(); await release.Task; }
            return true;
        };
        var caps = new KeyboardLightingCapabilities { Tier = KeyboardLightingTier.FourZone, ZoneCount = 4 };
        using var controller = new KeyboardLightingController(null, caps, null, writer);
        controller.SetZoneColor(0, Color.Red);
        Task first = controller.ApplyAsync(KeyboardLightingScope.Zones);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        controller.SetZoneColor(0, Color.Green);
        Task waiting = controller.ApplyAsync(KeyboardLightingScope.Zones);
        controller.SetZoneColor(0, Color.Blue);
        release.SetResult();
        await Task.WhenAll(first, waiting).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { Color.Red.ToArgb(), Color.Blue.ToArgb() }, writes);
    }
}
