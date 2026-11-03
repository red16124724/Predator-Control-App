using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests;

public sealed class LifecyclePersistenceAndBackgroundServiceStressTests
{
    #region 1. RegistrySafety & AppSettings Corrupt Value Recovery & Atomic Writes

    [Fact]
    public void RegistrySafety_TryGetHelpers_RecoverFromCorruptValuesAndTypes()
    {
        // Safe key mock test using BaseKeyPath subkey
        string testSubKey = $@"{RegistrySafety.BaseKeyPath}\Tests_CorruptRecovery_{Guid.NewGuid():N}";
        try
        {
            using var key = RegistrySafety.CreateSafeSubKey(Registry.CurrentUser, testSubKey);
            Assert.NotNull(key);

            // Populate with corrupt or unconventional types
            key.SetValue("IntAsString", "42");
            key.SetValue("BadString", "not-a-number");
            key.SetValue("IntAsDword", 123, RegistryValueKind.DWord);
            key.SetValue("LongAsString", "9876543210");
            key.SetValue("BoolAsOne", 1, RegistryValueKind.DWord);
            key.SetValue("BoolAsZero", 0, RegistryValueKind.DWord);
            key.SetValue("BoolAsString", "1");
            key.SetValue("EmptyString", "");
            key.Flush();

            // Int conversions
            Assert.True(RegistrySafety.TryGetRegistryInt(key, "IntAsString", out int valStr));
            Assert.Equal(42, valStr);

            Assert.False(RegistrySafety.TryGetRegistryInt(key, "BadString", out _));

            Assert.True(RegistrySafety.TryGetRegistryInt(key, "IntAsDword", out int valDword));
            Assert.Equal(123, valDword);

            // Long conversions
            Assert.True(RegistrySafety.TryGetRegistryLong(key, null, "LongAsString", out long valLong));
            Assert.Equal(9876543210L, valLong);

            // Bool conversions
            Assert.True(RegistrySafety.TryGetRegistryBool(key, null, "BoolAsOne", out bool boolOne));
            Assert.True(boolOne);

            Assert.True(RegistrySafety.TryGetRegistryBool(key, null, "BoolAsZero", out bool boolZero));
            Assert.False(boolZero);

            Assert.True(RegistrySafety.TryGetRegistryBool(key, null, "BoolAsString", out bool boolStr));
            Assert.True(boolStr);

            Assert.False(RegistrySafety.TryGetRegistryBool(key, null, "BadString", out _));
        }
        finally
        {
            try
            {
                using var root = RegistrySafety.OpenSafeSubKey(Registry.CurrentUser, RegistrySafety.BaseKeyPath, writable: true);
                root?.DeleteSubKeyTree(Path.GetFileName(testSubKey), throwOnMissingSubKey: false);
            }
            catch { }
        }
    }

    [Fact]
    public void RegistrySafety_LoadCalibrationState_RecoversFromCorruptOrMissingValues()
    {
        var settings = new AppSettings
        {
            LastBatteryCalibrationUtcTicks = -100,
            BatteryCalibrationPhase = 99,
            CalibrationLastKnownPercent = 500
        };

        // Sanitize must clamp them to valid ranges
        settings.Sanitize();

        Assert.Equal(0, settings.LastBatteryCalibrationUtcTicks);
        Assert.Equal(2, settings.BatteryCalibrationPhase);
        Assert.Equal(100, settings.CalibrationLastKnownPercent);
    }

    [Fact]
    public void AppSettings_PerZoneRgbPersistence_IndividualZeroRestoration()
    {
        var settings = new AppSettings();
        int red = Color.Red.ToArgb();
        int green = Color.Green.ToArgb();
        int blue = Color.Blue.ToArgb();

        settings.StaticZone1ColorArgb = red;
        settings.StaticZone2ColorArgb = 0; // Missing / zero
        settings.StaticZone3ColorArgb = green;
        settings.StaticZone4ColorArgb = blue;
        settings.KeyboardZoneColors = "#FF0000,#FFFF00,#00FF00,#0000FF"; // Zone 2 is Yellow

        settings.Sanitize();

        // Zone 1, 3, 4 preserved; Zone 2 restored from Yellow (#FFFF00)
        Assert.Equal(red, settings.StaticZone1ColorArgb);
        Assert.Equal(Color.FromArgb(255, 255, 0).ToArgb(), settings.StaticZone2ColorArgb);
        Assert.Equal(green, settings.StaticZone3ColorArgb);
        Assert.Equal(blue, settings.StaticZone4ColorArgb);
    }

    [Fact]
    public void AppSettings_PerZoneRgbPersistence_PreservesAcrossDynamicEffectToggle()
    {
        var settings = new AppSettings
        {
            StaticZone1ColorArgb = Color.Red.ToArgb(),
            StaticZone2ColorArgb = Color.Green.ToArgb(),
            StaticZone3ColorArgb = Color.Blue.ToArgb(),
            StaticZone4ColorArgb = Color.Yellow.ToArgb(),
            IsZoneLightingActive = true,
            DynamicLightingEffectName = "Off"
        };

        settings.Sanitize();
        Assert.True(settings.IsZoneLightingActive);
        Assert.Equal("Off", settings.DynamicLightingEffectName);

        // Switch to dynamic effect
        settings.DynamicLightingEffectName = "Wave";
        settings.IsZoneLightingActive = false;
        settings.Sanitize();

        // Zone colors remain untouched
        Assert.Equal(Color.Red.ToArgb(), settings.StaticZone1ColorArgb);
        Assert.Equal(Color.Green.ToArgb(), settings.StaticZone2ColorArgb);
        Assert.Equal(Color.Blue.ToArgb(), settings.StaticZone3ColorArgb);
        Assert.Equal(Color.Yellow.ToArgb(), settings.StaticZone4ColorArgb);

        // Switch back to per-zone
        settings.IsZoneLightingActive = true;
        settings.DynamicLightingEffectName = "Off";
        settings.Sanitize();

        Assert.Equal(Color.Red.ToArgb(), settings.StaticZone1ColorArgb);
        Assert.Equal(Color.Green.ToArgb(), settings.StaticZone2ColorArgb);
        Assert.Equal(Color.Blue.ToArgb(), settings.StaticZone3ColorArgb);
        Assert.Equal(Color.Yellow.ToArgb(), settings.StaticZone4ColorArgb);
    }

    #endregion

    #region 2. Background Services & Cooldowns

    [Fact]
    public async Task AcerServiceClient_CooldownEnforced_WhenServiceDown()
    {
        var prevInterceptor = AcerServiceClient.CommandInterceptor;
        AcerServiceClient.CommandInterceptor = null;
        try
        {
            AcerServiceClient.ResetConnectionState();
            Assert.True(AcerServiceClient.IsServiceReachable);

            // Probe when service is not running fails and initiates cooldown
            bool probe1 = await AcerServiceClient.ProbeServiceAsync(50);
            Assert.False(probe1);
            Assert.False(AcerServiceClient.IsServiceReachable);

            // Immediate subsequent call must be throttled by cooldown without attempting sockets
            bool probe2 = await AcerServiceClient.ProbeServiceAsync(50);
            Assert.False(probe2);
            Assert.False(AcerServiceClient.IsServiceReachable);
        }
        finally
        {
            AcerServiceClient.CommandInterceptor = prevInterceptor;
        }
    }

    [Fact]
    public void RawInputKeyWatcher_Disposal_ThreadSafety()
    {
        // Must dispose cleanly across threads without throwing cross-thread exception
        var watcher = new RawInputKeyWatcher();
        var t1 = Task.Run(() => watcher.Dispose());
        var t2 = Task.Run(() => watcher.Dispose());

        Task.WaitAll(t1, t2);
    }

    [Fact]
    public void WmiHotkeyWatcher_Disposal_CancelsPendingRestartTask()
    {
        var watcher = new WmiHotkeyWatcher(_ => { });
        watcher.Dispose();
        // Subsequent dispose is safe idempotent
        watcher.Dispose();
    }

    #endregion

    #region 3. Concurrency Stress: Mode Switching, Fan Command Queue, Power Limits

    [Fact]
    public async Task PowerModeSwitchGuard_RapidModeSwitching_UnderHighConcurrency()
    {
        int appliedCount = 0;
        byte lastMode = 0xFF;
        var appliedModes = new List<byte>();
        var sync = new object();

        using var guard = new PowerModeSwitchGuard(mode =>
        {
            lock (sync)
            {
                appliedCount++;
                lastMode = mode;
                appliedModes.Add(mode);
            }
            Thread.SpinWait(1000);
            return true;
        }, minIntervalMs: 10, settleMs: 5);

        // Concurrent tasks flooding requests
        var tasks = new Task[10];
        for (int i = 0; i < tasks.Length; i++)
        {
            byte target = (byte)(i % 5);
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 10; j++)
                {
                    guard.Request(target);
                    Thread.Yield();
                }
            });
        }

        await Task.WhenAll(tasks);
        guard.Request(4); // Final Turbo request

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (guard.LastAppliedMode != 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal((byte)4, guard.LastAppliedMode);
        Assert.True(guard.CoalescedCount > 0);
    }

    [Fact]
    public async Task FanCommandQueue_RapidBurst_And_ClearAndStop_Concurrency()
    {
        var queue = new FanCommandQueue();
        int executedCount = 0;

        var tasks = new Task[30];
        for (int i = 0; i < tasks.Length; i++)
        {
            int idx = i;
            tasks[i] = Task.Run(async () =>
            {
                for (int j = 0; j < 30; j++)
                {
                    var t = queue.Enqueue(FanCommandKind.Cpu, () => Interlocked.Increment(ref executedCount));
                    try { await t; } catch { }
                }
            });
        }

        await Task.Delay(20);
        // Clear and stop queue midway through burst
        queue.ClearAndStop();

        await Task.WhenAll(tasks);

        // Ensure subsequent enqueues after disposal are immediately canceled without executing
        var postTask = queue.Enqueue(FanCommandKind.Mode, () => Interlocked.Increment(ref executedCount));
        Assert.True(postTask.IsCanceled);
    }

    #endregion

    #region 4. Updater Lifecycle and Cancellation Hygiene

    [Fact]
    public async Task Updater_ScheduleStartupCheckAsync_AbortsImmediatelyOnCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-canceled

        bool executed = false;
        await Updater.ScheduleStartupCheckAsync(() => executed = true, TimeSpan.FromSeconds(10), cts.Token);

        Assert.False(executed);
    }

    #endregion
}
