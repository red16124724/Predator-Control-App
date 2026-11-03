using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PredatorControlApp.Tests;

public sealed class FanCommandQueueTests
{
    [Fact]
    public async Task FailedWriteDoesNotStopFollowingWrite()
    {
        var queue = new FanCommandQueue();
        int writes = 0;
        Task failed = queue.Enqueue(FanCommandKind.Speeds, () => throw new System.InvalidOperationException("simulated failure"));
        Task next = queue.Enqueue(FanCommandKind.Cpu, () => Interlocked.Increment(ref writes));
        await Assert.ThrowsAsync<System.InvalidOperationException>(() => failed);
        await next.WaitAsync(System.TimeSpan.FromSeconds(5));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task NewModeSupersedesQueuedCurveAndWaitsForActiveWrite()
    {
        var queue = new FanCommandQueue();
        var writes = new ConcurrentQueue<string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task active = queue.Enqueue(FanCommandKind.Mode, () => { writes.Enqueue("old-start"); entered.Set(); release.Wait(); writes.Enqueue("old-end"); });
        Assert.True(entered.Wait(5000));
        Task stale = queue.Enqueue(FanCommandKind.Mode, () => writes.Enqueue("stale-curve"));
        Task latest = queue.Enqueue(FanCommandKind.Mode, () => writes.Enqueue("auto"));
        release.Set();
        await Task.WhenAll(active, stale, latest).WaitAsync(System.TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "old-start", "old-end", "auto" }, writes.ToArray());
    }

    [Fact]
    public async Task SpeedBatchPreservesPendingModeAndIndependentSliders()
    {
        var queue = new FanCommandQueue();
        var writes = new ConcurrentQueue<string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task active = queue.Enqueue(FanCommandKind.Mode, () => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(5000));
        Task mode = queue.Enqueue(FanCommandKind.Mode, () => writes.Enqueue("custom"));
        Task batch = queue.Enqueue(FanCommandKind.Speeds, () => writes.Enqueue("profile"));
        Task cpuOld = queue.Enqueue(FanCommandKind.Cpu, () => writes.Enqueue("cpu-old"));
        Task gpu = queue.Enqueue(FanCommandKind.Gpu, () => writes.Enqueue("gpu"));
        Task cpuNew = queue.Enqueue(FanCommandKind.Cpu, () => writes.Enqueue("cpu-new"));
        release.Set();
        await Task.WhenAll(active, mode, batch, cpuOld, gpu, cpuNew).WaitAsync(System.TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "custom", "profile", "gpu", "cpu-new" }, writes.ToArray());
    }
}
