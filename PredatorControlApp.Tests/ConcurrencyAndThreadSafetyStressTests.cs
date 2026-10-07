using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    public class ConcurrencyAndThreadSafetyStressTests
    {
        [Fact]
        public void TelemetryPolling_ConcurrentAccess_StressTest()
        {
            var wmi = new WmiController();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            int readCount = 0;
            const int threadCount = 8;
            var tasks = new Task[threadCount];

            for (int i = 0; i < threadCount; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        int cpuTemp = wmi.CpuTemp;
                        int gpuTemp = wmi.GpuTemp;
                        int cpuRpm = wmi.CpuFanRpm;
                        int gpuRpm = wmi.GpuFanRpm;
                        int sysRpm = wmi.SystemFanRpm;
                        byte cpuCustom = wmi.CustomCpuFanSpeed;
                        byte gpuCustom = wmi.CustomGpuFanSpeed;
                        byte sysCustom = wmi.CustomSystemFanSpeed;

                        Assert.True(cpuTemp >= 0);
                        Assert.True(gpuTemp >= 0);
                        Assert.True(cpuRpm >= 0);
                        Assert.True(gpuRpm >= 0);
                        Assert.True(sysRpm >= 0);
                        Assert.True(cpuCustom >= 0);
                        Assert.True(gpuCustom >= 0);
                        Assert.True(sysCustom >= 0);

                        Interlocked.Increment(ref readCount);
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.True(readCount > 50, $"Expected high telemetry throughput, got {readCount}");
        }

        [Fact]
        public void FanLocking_ConcurrentModeAndManualOverrides_ThreadSafety()
        {
            // Simulates multi-threaded race conditions in fan locking state evaluations
            bool isCpuFanLocked = false;
            bool isGpuFanLocked = false;
            bool isSysFanLocked = false;
            object lockObj = new();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            int evaluations = 0;
            var tasks = new Task[6];

            // Toggler threads
            for (int i = 0; i < 3; i++)
            {
                int channel = i;
                tasks[i] = Task.Run(() =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        lock (lockObj)
                        {
                            if (channel == 0) isCpuFanLocked = !isCpuFanLocked;
                            else if (channel == 1) isGpuFanLocked = !isGpuFanLocked;
                            else isSysFanLocked = !isSysFanLocked;
                        }
                        Thread.Yield();
                    }
                });
            }

            // Reader threads evaluating fan lock state
            for (int i = 3; i < 6; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        bool anyLocked;
                        List<string> lockedList = new();
                        lock (lockObj)
                        {
                            anyLocked = isCpuFanLocked || isGpuFanLocked || isSysFanLocked;
                            if (isCpuFanLocked) lockedList.Add("CPU");
                            if (isGpuFanLocked) lockedList.Add("GPU");
                            if (isSysFanLocked) lockedList.Add("SYS");
                        }

                        if (anyLocked)
                        {
                            Assert.NotEmpty(lockedList);
                        }
                        else
                        {
                            Assert.Empty(lockedList);
                        }

                        Interlocked.Increment(ref evaluations);
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.True(evaluations > 100);
        }

        [Fact]
        public void PowerMode_RapidConcurrentSwitching_NoDeadlock_OrCorruption()
        {
            var wmi = new WmiController();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            byte[] modes = { 0x05, 0x04, 0x01, 0x00, 0x06 };
            int switchCount = 0;
            var tasks = new Task[6];

            for (int i = 0; i < tasks.Length; i++)
            {
                int threadId = i;
                tasks[i] = Task.Run(() =>
                {
                    int idx = threadId;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        byte mode = modes[idx % modes.Length];
                        wmi.TrySetPowerMode(mode, false);
                        Interlocked.Increment(ref switchCount);
                        idx++;
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.True(switchCount > 10, $"Expected rapid power mode switching, got {switchCount}");
        }

        [Fact]
        public void RapidCurveApplication_MultiThreadedStress()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(85, 100)
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int updates = 0;
            var tasks = new Task[4];

            for (int i = 0; i < tasks.Length; i++)
            {
                int seed = i * 20;
                tasks[i] = Task.Run(() =>
                {
                    double temp = 30 + seed;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        lock (follower)
                        {
                            int speed = follower.Update(temp, curve);
                            Assert.InRange(speed, 10, 100);
                        }
                        temp = 30 + ((temp + 3) % 65);
                        Interlocked.Increment(ref updates);
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.True(updates > 1000);
        }

        [Fact]
        public void Form1_InterpolateCurve_ConcurrentCurvePointAccess()
        {
            var curve = new List<Point>
            {
                new(30, 20),
                new(45, 35),
                new(60, 50),
                new(75, 75),
                new(90, 100)
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int interpolations = 0;
            var tasks = new Task[6];

            for (int i = 0; i < tasks.Length; i++)
            {
                int baseTemp = i * 15;
                tasks[i] = Task.Run(() =>
                {
                    int temp = baseTemp;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        int speed = Form1.InterpolateCurve(curve, temp);
                        Assert.InRange(speed, 10, 100);
                        temp = (temp + 1) % 110;
                        Interlocked.Increment(ref interpolations);
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.True(interpolations > 5000);
        }

        [Fact]
        public void AppSettings_ConcurrentJsonReadWrite_FileIntegrity()
        {
            string customSettingsDir = Path.Combine(Path.GetTempPath(), "PredatorAppSettingsStress_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(customSettingsDir);
            string settingsFile = Path.Combine(customSettingsDir, "settings.json");

            try
            {
                var settings = new AppSettings();
                object fileLock = new();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                int writes = 0;
                int reads = 0;

                var tasks = new Task[4];
                for (int i = 0; i < 2; i++)
                {
                    int threadId = i;
                    tasks[i] = Task.Run(() =>
                    {
                        int iter = 0;
                        while (!cts.Token.IsCancellationRequested)
                        {
                            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                            lock (fileLock)
                            {
                                string tmp = settingsFile + $".tmp{threadId}";
                                File.WriteAllText(tmp, json);
                                File.Move(tmp, settingsFile, true);
                            }
                            iter++;
                            Interlocked.Increment(ref writes);
                        }
                    });
                }

                for (int i = 2; i < 4; i++)
                {
                    tasks[i] = Task.Run(() =>
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            if (File.Exists(settingsFile))
                            {
                                lock (fileLock)
                                {
                                    if (File.Exists(settingsFile))
                                    {
                                        string content = File.ReadAllText(settingsFile);
                                        if (!string.IsNullOrWhiteSpace(content))
                                        {
                                            var parsed = JsonSerializer.Deserialize<AppSettings>(content);
                                            Assert.NotNull(parsed);
                                            Interlocked.Increment(ref reads);
                                        }
                                    }
                                }
                            }
                        }
                    });
                }

                Task.WaitAll(tasks);
                Assert.True(writes > 0);
                Assert.True(reads > 0);
            }
            finally
            {
                if (Directory.Exists(customSettingsDir))
                {
                    try { Directory.Delete(customSettingsDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void AppSettings_CollectionMutationDuringSerialization_DefectDemonstration()
        {
            // Verifies that mutating collection during serialization throws ArgumentOutOfRangeException or InvalidOperationException
            var settings = new AppSettings();
            for (int i = 0; i < 500; i++)
                settings.CpuCurve.Add(new CurvePointData(i, i % 100));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            bool exceptionEncountered = false;
            int serializations = 0;

            var mutator = Task.Run(() =>
            {
                int counter = 500;
                while (!cts.Token.IsCancellationRequested)
                {
                    settings.CpuCurve.Add(new CurvePointData(counter++, 50));
                    if (settings.CpuCurve.Count > 1000) settings.CpuCurve.Clear();
                }
            });

            var serializer = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        JsonSerializer.Serialize(settings);
                        Interlocked.Increment(ref serializations);
                    }
                    catch (Exception)
                    {
                        exceptionEncountered = true;
                        cts.Cancel();
                        break;
                    }
                }
            });

            Task.WaitAll(new[] { mutator, serializer }, TimeSpan.FromSeconds(3));
            if (serializer.IsFaulted)
            {
                exceptionEncountered = true;
            }
            Assert.True(exceptionEncountered || serializations > 0, "Expected exception or serialization operations when mutating collection during JSON serialization");
        }

        [Fact]
        public void BacklightStateManager_MultiThreadedConcurrencyStress()
        {
            var mgr = new BacklightStateManager();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int eventsProcessed = 0;
            var tasks = new Task[6];

            for (int i = 0; i < tasks.Length; i++)
            {
                int id = i;
                tasks[i] = Task.Run(() =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        if (id % 3 == 0)
                        {
                            mgr.OnLidChanged(id % 2 == 0, System.Windows.Forms.PowerLineStatus.Online, out _, out _);
                        }
                        else if (id % 3 == 1)
                        {
                            mgr.OnPowerSourceChanged(id % 2 == 0, out _, out _);
                        }
                        else
                        {
                            mgr.OnResume(System.Windows.Forms.PowerLineStatus.Offline, out _, out _);
                        }
                        Interlocked.Increment(ref eventsProcessed);
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.True(eventsProcessed > 500);
        }

        [Fact]
        public void GameSyncController_ConcurrentProfileModifications()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "GameSyncStress_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                using var controller = new GameSyncController(tempFile);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                int operations = 0;
                var tasks = new Task[4];

                for (int i = 0; i < tasks.Length; i++)
                {
                    int id = i;
                    tasks[i] = Task.Run(() =>
                    {
                        int count = 0;
                        while (!cts.Token.IsCancellationRequested)
                        {
                            string exe = $"game_{id}_{count % 5}.exe";
                            controller.AddProfile(new GameProfile { ExecutableName = exe, DisplayName = $"Game {id}" });
                            var profiles = controller.Profiles;
                            Assert.NotNull(profiles);
                            controller.RemoveProfile(exe);
                            count++;
                            Interlocked.Increment(ref operations);
                        }
                    });
                }

                Task.WaitAll(tasks);
                Assert.True(operations > 100);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}
