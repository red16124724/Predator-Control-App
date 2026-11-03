using System.Drawing;
using System.Text.Json;
using Xunit;

namespace PredatorControlApp.Tests;

[Collection("AcerServiceClientTests")]
public sealed class LightingServiceOrderingTests
{
    [Theory]
    [InlineData(100, 5, false)]
    [InlineData(100, 5, true)]
    [InlineData(0, 0, false)]
    [InlineData(0, 0, true)]
    public async Task NewestKeyboardRequest_WinsAcrossZoneAndEffectQueues(int finalBrightness, int expectedServiceBrightness, bool finalIsEffect)
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var sent = new List<int>();
        int calls = 0;
        Task<bool>? first = null;
        Task<bool>? obsolete = null;
        Task<bool>? newest = null;
        AcerServiceClient.CommandInterceptor = (_, json) =>
        {
            using var payload = JsonDocument.Parse(json);
            int brightness = payload.RootElement.GetProperty("Parameter").GetProperty("brightness").GetInt32();
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
            lock (sent) sent.Add(brightness);
        };

        try
        {
            first = AcerServiceClient.SetZoneLightingAsync(new[] { Color.White }, 20);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)), "First request did not reach the controlled send boundary");

            obsolete = AcerServiceClient.SetKeyboardLightingAsync(1, "WAVE", null, 40, 50, perKey: false);
            newest = finalIsEffect
                ? AcerServiceClient.SetKeyboardLightingAsync(1, "STATIC", null, finalBrightness, 50, perKey: false)
                : AcerServiceClient.SetZoneLightingAsync(new[] { Color.White }, finalBrightness);
            releaseFirst.Set();

            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(await obsolete.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await newest.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(new[] { 1, expectedServiceBrightness }, sent);
        }
        finally
        {
            releaseFirst.Set();
            await Drain(first, obsolete, newest);
            AcerServiceClient.CommandInterceptor = null;
        }
    }

    [Fact]
    public async Task QueuedZoneRequest_SnapshotsColorsBeforeCallerMutation()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var sentColors = new List<string>();
        int calls = 0;
        Task<bool>? first = null;
        Task<bool>? newest = null;
        AcerServiceClient.CommandInterceptor = (_, json) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
            using var payload = JsonDocument.Parse(json);
            string color = payload.RootElement.GetProperty("Parameter").GetProperty("LEDs")[0].GetProperty("color").GetString()!;
            lock (sentColors) sentColors.Add(color);
        };

        try
        {
            first = AcerServiceClient.SetZoneLightingAsync(new[] { Color.Red }, 20);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            var colors = new[] { Color.FromArgb(17, 34, 255) };
            newest = AcerServiceClient.SetZoneLightingAsync(colors, 100);
            colors[0] = Color.Black;
            releaseFirst.Set();

            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await newest.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(new[] { "#FF0000", "#1122FF" }, sentColors);
        }
        finally
        {
            releaseFirst.Set();
            await Drain(first, newest);
            AcerServiceClient.CommandInterceptor = null;
        }
    }

    [Fact]
    public async Task HardwareBarrier_FinishesActiveSend_AndSupersedesAcceptedOldRequests()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var snapshotEntered = new ManualResetEventSlim();
        using var releaseSnapshot = new ManualResetEventSlim();
        var sent = new List<int>();
        int calls = 0;
        Task<bool>? first = null;
        Task<bool>? queued = null;
        Task<bool>? newest = null;
        Task? barrier = null;
        AcerServiceClient.CommandInterceptor = (_, json) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
            using var payload = JsonDocument.Parse(json);
            int brightness = payload.RootElement.GetProperty("Parameter").GetProperty("brightness").GetInt32();
            lock (sent) sent.Add(brightness);
        };

        try
        {
            first = AcerServiceClient.SetZoneLightingAsync(new[] { Color.Red }, 20);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            var delayedColors = new DelayedSnapshotColors(snapshotEntered, releaseSnapshot);
            queued = Task.Run(() => AcerServiceClient.SetZoneLightingAsync(delayedColors, 40));
            Assert.True(snapshotEntered.Wait(TimeSpan.FromSeconds(10)));

            barrier = Task.Run(AcerServiceClient.SupersedeKeyboardLighting);
            releaseFirst.Set();
            await barrier.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));

            releaseSnapshot.Set();
            Assert.False(await queued.WaitAsync(TimeSpan.FromSeconds(10)));
            newest = AcerServiceClient.SetZoneLightingAsync(new[] { Color.White }, 100);
            Assert.True(await newest.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(new[] { 1, 5 }, sent);
        }
        finally
        {
            releaseFirst.Set();
            releaseSnapshot.Set();
            if (barrier != null)
            {
                try { await barrier.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            await Drain(first, queued, newest);
            AcerServiceClient.CommandInterceptor = null;
        }
    }

    private sealed class DelayedSnapshotColors(ManualResetEventSlim entered, ManualResetEventSlim release) : IReadOnlyList<Color>
    {
        public int Count => 1;
        public Color this[int index] => index == 0 ? Color.Blue : throw new IndexOutOfRangeException();

        public IEnumerator<Color> GetEnumerator()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Snapshot was not released");
            yield return Color.Blue;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static async Task Drain(params Task<bool>?[] requests)
    {
        foreach (Task<bool>? request in requests)
        {
            if (request != null)
            {
                try { await request.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { }
            }
        }
    }
}
