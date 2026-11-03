using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PredatorControlApp.Tests;

public sealed class PowerModeSwitchGuardStressTests
{
    [Fact]
    public async Task ConcurrentRequestsNeverOverlapHardwareWritesAndDrainToLatest()
    {
        int active = 0;
        int overlap = 0;
        using var guard = new PowerModeSwitchGuard(_ =>
        {
            if (Interlocked.Increment(ref active) != 1) Interlocked.Increment(ref overlap);
            Thread.SpinWait(3000);
            Interlocked.Decrement(ref active);
            return true;
        }, minIntervalMs: 0, settleMs: 0);

        var producers = new Task[8];
        for (int i = 0; i < producers.Length; i++)
        {
            int producer = i;
            producers[i] = Task.Run(() =>
            {
                for (int n = 0; n < 5000; n++)
                {
                    guard.Request((byte)((n + producer) % 5));
                    if ((n & 63) == 0) Thread.Yield();
                }
            });
        }
        await Task.WhenAll(producers);
        guard.Request(99);
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while ((guard.IsSwitching || guard.LastAppliedMode != 99) && DateTime.UtcNow < timeout)
            await Task.Delay(5);

        Assert.False(guard.IsSwitching);
        Assert.Equal((byte)99, guard.LastAppliedMode);
        Assert.Equal(0, overlap);
    }
}
