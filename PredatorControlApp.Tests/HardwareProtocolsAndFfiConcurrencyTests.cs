using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    public class HardwareProtocolsAndFfiConcurrencyTests
    {
        [Fact]
        public void CurveFollower_ConcurrentLockFreePointsAndHysteresisMutations_NoDeadlock()
        {
            using var follower = new CurveFollower(hysteresisDegrees: 3);
            var curvePoints = new List<Point>
            {
                new Point(40, 20),
                new Point(60, 45),
                new Point(80, 75),
                new Point(95, 100)
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var token = cts.Token;
            int updatesCompleted = 0;
            int hysteresisChanges = 0;
            const int updateThreads = 12;
            const int mutatorThreads = 4;
            var tasks = new Task[updateThreads + mutatorThreads];

            // Update workers: continuously update temperatures
            for (int i = 0; i < updateThreads; i++)
            {
                int threadId = i;
                tasks[i] = Task.Run(() =>
                {
                    double temp = 40.0 + (threadId * 5.0);
                    while (!token.IsCancellationRequested)
                    {
                        int speed = follower.Update(temp, curvePoints, 10);
                        Assert.InRange(speed, 0, 100);
                        Interlocked.Increment(ref updatesCompleted);
                        temp = (temp + 1.5);
                        if (temp > 105.0) temp = 40.0;
                    }
                });
            }

            // Mutator workers: mutate curvePoints and follower.HysteresisDegrees concurrently
            for (int i = 0; i < mutatorThreads; i++)
            {
                int mutatorId = i;
                tasks[updateThreads + i] = Task.Run(() =>
                {
                    int deg = 2;
                    while (!token.IsCancellationRequested)
                    {
                        // Mutate hysteresis degrees (takes _syncLock)
                        follower.HysteresisDegrees = (deg % 5) + 1;
                        deg++;
                        Interlocked.Increment(ref hysteresisChanges);

                        // Mutate curve points under curvePoints lock
                        lock (curvePoints)
                        {
                            curvePoints[1] = new Point(60, 40 + (deg % 10));
                        }

                        if (mutatorId == 0 && deg % 20 == 0)
                        {
                            follower.Reset();
                        }

                        Thread.Yield();
                    }
                });
            }

            bool completed = Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
            Assert.True(completed, "CurveFollower deadlocked under concurrent point/hysteresis mutations!");
            Assert.True(updatesCompleted > 1000, $"Expected >1000 updates, got {updatesCompleted}");
            Assert.True(hysteresisChanges > 50, $"Expected >50 hysteresis changes, got {hysteresisChanges}");
        }

        [Fact]
        public void PredatorNativeCore_PinAndUnpinCallback_ThreadSafetyAndLeakFree()
        {
            const int threadCount = 8;
            const int iterationsPerThread = 50;
            var tasks = new Task[threadCount];
            int totalPinned = 0;

            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        Action cb = () => { };
                        var handle = PredatorNativeCore.PinCallback(cb);
                        Assert.True(handle.IsAllocated);

                        // Test unpin via GCHandle
                        PredatorNativeCore.UnpinCallback(ref handle);
                        Assert.False(handle.IsAllocated);

                        // Test unpin via Delegate
                        Action cb2 = () => { };
                        var handle2 = PredatorNativeCore.PinCallback(cb2);
                        Assert.True(handle2.IsAllocated);
                        PredatorNativeCore.UnpinCallback(cb2);

                        Interlocked.Increment(ref totalPinned);
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Equal(threadCount * iterationsPerThread, totalPinned);

            // Null and invalid handle safety:
            PredatorNativeCore.UnpinCallback((Delegate)null!);
            PredatorNativeCore.UnpinCallback(default(GCHandle));
        }

        [Fact]
        public void GameSyncController_ProcessSnapshotStress_NoHandleLeaksOrCrashes()
        {
            // Rapid concurrent invocations of GetRunningProcessNames
            const int workers = 8;
            var tasks = new Task[workers];
            int scansCompleted = 0;

            for (int i = 0; i < workers; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    for (int iter = 0; iter < 20; iter++)
                    {
                        var procs = GameSyncController.GetRunningProcessNames();
                        Assert.NotNull(procs);
                        Assert.NotEmpty(procs);
                        Interlocked.Increment(ref scansCompleted);
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Equal(workers * 20, scansCompleted);
        }

        [Fact]
        public void GameSyncController_ConcurrentPollAndProfileLifecycle_ThreadSafe()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "GameSyncFFIStress_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                using var controller = new GameSyncController(tempFile);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var token = cts.Token;
                int detectedCount = 0;
                int exitedCount = 0;

                controller.GameDetected += _ => Interlocked.Increment(ref detectedCount);
                controller.GameExited += _ => Interlocked.Increment(ref exitedCount);

                controller.IsEnabled = true;

                // Concurrent poll thread
                var pollTask = Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        controller.PollOnce();
                        Thread.Yield();
                    }
                });

                // Concurrent mutator thread
                var mutateTask = Task.Run(() =>
                {
                    int counter = 0;
                    while (!token.IsCancellationRequested)
                    {
                        string exe = $"testgame_{counter % 4}.exe";
                        controller.AddProfile(new GameProfile { ExecutableName = exe, DisplayName = $"Test Game {counter}" });
                        controller.RemoveProfile(exe);
                        counter++;
                        Thread.Yield();
                    }
                });

                // Toggle enable thread
                var toggleTask = Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        controller.IsEnabled = !controller.IsEnabled;
                        Thread.Sleep(5);
                    }
                });

                Task.WaitAll(pollTask, mutateTask, toggleTask);
                controller.IsEnabled = false;
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }
    }
}
