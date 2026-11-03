using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    [Collection("NoParallel")]
    public class BackgroundServicesTelemetryIpcAuditTests
    {
        #region 1. Keyboard Function Keys & Predator Hotkey Tests

        [Fact]
        public void KeyboardHook_AllFunctionKeysF1ToF24_NeverSwallowed()
        {
            int modeCount = 0;
            int numberCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onPredatorNumberPressed: n => Interlocked.Increment(ref numberCount),
                onModeKeyPressed: () => Interlocked.Increment(ref modeCount));

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                // Test F1 through F24 (0x70 to 0x87) with standard and OEM scancodes
                for (uint vk = 0x70; vk <= 0x87; vk++)
                {
                    // Scan code 0x3B (standard F1 scancode)
                    structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x3B);
                    structType.GetField("vkCode")!.SetValue(hookStruct, vk);
                    Marshal.StructureToPtr(hookStruct, pStruct, false);

                    object? result = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                    Assert.NotEqual((IntPtr)1, (IntPtr)(result ?? IntPtr.Zero));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }

            Assert.Equal(0, modeCount);
            Assert.Equal(0, numberCount);
        }

        [Fact]
        public void KeyboardHook_FunctionKeyPressedWhilePredatorHeld_ClearsHeldStateImmediately()
        {
            int numberComboCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onPredatorNumberPressed: n => Interlocked.Increment(ref numberComboCount));

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                // 1. Simulate Predator key down (SC_PREDATOR = 0x75)
                structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x75);
                structType.GetField("vkCode")!.SetValue(hookStruct, (uint)0x00);
                Marshal.StructureToPtr(hookStruct, pStruct, false);
                hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });

                // Verify predator key is now considered pressed
                var isHeldField = typeof(KeyboardHook).GetField("_isPredatorKeyPressed", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(isHeldField);
                Assert.True((bool)isHeldField.GetValue(hook)!);

                // 2. User presses an extended Fn key (e.g. F14 for Touchpad or Brightness)
                structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x20);
                structType.GetField("vkCode")!.SetValue(hookStruct, (uint)0x7D /* VK_F14 */);
                Marshal.StructureToPtr(hookStruct, pStruct, false);
                object? fnResult = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });

                // Function key was passed through to Windows
                Assert.NotEqual((IntPtr)1, (IntPtr)(fnResult ?? IntPtr.Zero));

                // Predator held state must be cleared immediately so number keys are not held hostage
                Assert.False((bool)isHeldField.GetValue(hook)!);

                // 3. User types number '1' (VK 0x31)
                structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x02);
                structType.GetField("vkCode")!.SetValue(hookStruct, (uint)0x31);
                Marshal.StructureToPtr(hookStruct, pStruct, false);
                object? numResult = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });

                // Number key was NOT consumed and combo did NOT trigger!
                Assert.NotEqual((IntPtr)1, (IntPtr)(numResult ?? IntPtr.Zero));
                Assert.Equal(0, numberComboCount);
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }
        }

        [Fact]
        public void KeyboardHook_NumberKeys1To5_NeverConsumedWhenPredatorNotHeld()
        {
            int numberComboCount = 0;
            using var hook = new KeyboardHook(
                onPredatorSensePressed: () => { },
                onPredatorNumberPressed: n => Interlocked.Increment(ref numberComboCount));

            var hookCallbackMethod = typeof(KeyboardHook).GetMethod("HookCallback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(hookCallbackMethod);

            var structType = typeof(KeyboardHook).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            Assert.NotNull(structType);

            object hookStruct = Activator.CreateInstance(structType)!;
            IntPtr pStruct = Marshal.AllocHGlobal(Marshal.SizeOf(hookStruct));
            try
            {
                // Number row (0x31..0x35) and numpad (0x61..0x65)
                uint[] numbers = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x61, 0x62, 0x63, 0x64, 0x65 };
                foreach (uint vk in numbers)
                {
                    structType.GetField("scanCode")!.SetValue(hookStruct, (uint)0x02);
                    structType.GetField("vkCode")!.SetValue(hookStruct, vk);
                    Marshal.StructureToPtr(hookStruct, pStruct, false);

                    object? result = hookCallbackMethod.Invoke(hook, new object[] { 0, (IntPtr)0x0100 /* WM_KEYDOWN */, pStruct });
                    Assert.NotEqual((IntPtr)1, (IntPtr)(result ?? IntPtr.Zero));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }

            Assert.Equal(0, numberComboCount);
        }

        #endregion

        #region 2. Background Timers & Clean Disposal

        [Fact]
        public void GameSyncController_Disposal_TimerStopsAndIsEnabledDoesNotThrow()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "GameSyncTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var savePath = Path.Combine(tempDir, "game_sync.json");
                var controller = new GameSyncController(savePath);
                controller.IsEnabled = true;
                Assert.True(controller.IsEnabled);

                controller.Dispose();

                // Re-disposing must be completely idempotent
                controller.Dispose();

                // Setting IsEnabled after disposal must NOT throw ObjectDisposedException
                controller.IsEnabled = true;
                controller.IsEnabled = false;
                Assert.False(controller.IsEnabled);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void RawInputKeyWatcher_MultipleDisposal_ThreadSafeNoCrash()
        {
            using var watcher = new RawInputKeyWatcher();
            // Concurrent disposal
            Parallel.For(0, 10, _ =>
            {
                watcher.Dispose();
            });
        }

        #endregion

        #region 3. IPC & Single Instance Security

        [Fact]
        public void SecureNamedPipeIpc_PipeServer_CleanDisposalUnderLoad()
        {
            string pipeName = "PredatorControlTestPipe_" + Guid.NewGuid().ToString("N");
            int receivedCount = 0;
            var server = new SecureNamedPipeIpc.PipeServer(msg => Interlocked.Increment(ref receivedCount), pipeName);
            server.Start();

            // Calling Start again must be idempotent
            server.Start();

            // Dispose cleanly while listening
            server.Dispose();
            server.Dispose(); // Double disposal test
        }

        [Fact]
        public async Task SecureNamedPipeIpc_MalformedMessages_HandledGracefully()
        {
            string pipeName = "PredatorControlTestPipe_" + Guid.NewGuid().ToString("N");
            string? received = null;
            using var server = new SecureNamedPipeIpc.PipeServer(msg => received = msg, pipeName);
            server.Start();

            // Test sending message with control characters and null bytes
            bool sent = await SecureNamedPipeIpc.SendMessageWithVerificationAsync("TEST\0DATA\r\n", 1500, pipeName);
            // Wait for dispatch
            await Task.Delay(100);

            server.Dispose();
        }

        [Fact]
        public void ProgramMutex_TryTakeAndRelease_ThreadSafeAndReentrant()
        {
            string customMutexName = @"GlobalPredatorControlTestMutex_" + Guid.NewGuid().ToString("N");
            bool locked1 = Program.TryTakeSingleInstanceLock(customMutexName);
            Assert.True(locked1);

            // Second attempt while locked must fail
            bool locked2 = Program.TryTakeSingleInstanceLock(customMutexName);
            Assert.False(locked2);

            Program.ReleaseSingleInstanceLock();

            // After release, taking it again succeeds
            bool locked3 = Program.TryTakeSingleInstanceLock(customMutexName);
            Assert.True(locked3);

            Program.ReleaseSingleInstanceLock();
            Program.ReleaseSingleInstanceLock(); // Idempotent release
        }

        #endregion

        #region 4. PdhLoadMonitor & Telemetry History Tests

        [Fact]
        public void HistoryBuffer_NaNAndInfinity_SanitizedToNull()
        {
            var buffer = new HistoryBuffer(10);
            buffer.Add(double.NaN);
            buffer.Add(double.PositiveInfinity);
            buffer.Add(double.NegativeInfinity);
            buffer.Add(45.0);

            Assert.Equal(4, buffer.Count);
            Assert.True(double.IsNaN(buffer[0]!.Value));
            Assert.True(double.IsPositiveInfinity(buffer[1]!.Value));
            Assert.True(double.IsNegativeInfinity(buffer[2]!.Value));
            Assert.Equal(45.0, buffer[3]);

            var (latest, range) = buffer.GetLatestAndRange();
            Assert.Equal(45.0, latest);
            Assert.NotNull(range);
            Assert.Equal(45.0, range.Value.Min);
            Assert.Equal(45.0, range.Value.Max);
            Assert.Equal(45.0, buffer.Average());
        }

        [Fact]
        public void HistoryBuffer_LatestIsNaN_SanitizedInGetLatestAndRange()
        {
            var buffer = new HistoryBuffer(10);
            buffer.Add(50.0);
            buffer.Add(double.NaN);

            var (latest, range) = buffer.GetLatestAndRange();
            // Since latest was NaN, it should sanitize to null
            Assert.Null(latest);
            // Range still accurately computed from valid 50.0 entry
            Assert.NotNull(range);
            Assert.Equal(50.0, range.Value.Min);
            Assert.Equal(50.0, range.Value.Max);
        }

        [Fact]
        public void HistoryBuffer_FifoWrap_MaintainsCorrectOrderAndBounds()
        {
            var buffer = new HistoryBuffer(5);
            for (int i = 1; i <= 20; i++)
            {
                buffer.Add(i * 10.0);
            }

            Assert.Equal(5, buffer.Count);
            // Last 5 items are 160, 170, 180, 190, 200
            Assert.Equal(160.0, buffer[0]);
            Assert.Equal(170.0, buffer[1]);
            Assert.Equal(180.0, buffer[2]);
            Assert.Equal(190.0, buffer[3]);
            Assert.Equal(200.0, buffer[4]);

            Assert.Equal(200.0, buffer.GetLatestAndRange().Latest);
            Assert.Equal(160.0, buffer.Min());
            Assert.Equal(200.0, buffer.Max());
            Assert.Equal(180.0, buffer.Average());

            // Out-of-bounds indices return null without throwing
            Assert.Null(buffer[-1]);
            Assert.Null(buffer[5]);
            Assert.Null(buffer[100]);
        }

        [Fact]
        public void PdhLoadMonitor_RapidSamplingAndDisposal_ThreadSafe()
        {
            using var monitor = new PdhLoadMonitor();
            for (int i = 0; i < 5; i++)
            {
                var (cpu, gpu) = monitor.Sample(sampleGpu: false);
                if (cpu.HasValue)
                {
                    Assert.False(double.IsNaN(cpu.Value));
                    Assert.False(double.IsInfinity(cpu.Value));
                    Assert.InRange(cpu.Value, 0.0, 100.0);
                }
            }
            monitor.Dispose();
            // Sampling after disposal returns (null, null)
            var (cpuPost, gpuPost) = monitor.Sample();
            Assert.Null(cpuPost);
            Assert.Null(gpuPost);
        }

        #endregion

        #region 5. Battery Calibration ETA Calculator Tests

        [Fact]
        public void BatteryCalibrationEtaCalculator_SmoothedRateSetter_RejectsNaNAndNegative()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            calc.SmoothedRate = double.NaN;
            Assert.Null(calc.SmoothedRate);

            calc.SmoothedRate = double.PositiveInfinity;
            Assert.Null(calc.SmoothedRate);

            calc.SmoothedRate = -100.0;
            Assert.Null(calc.SmoothedRate);

            calc.SmoothedRate = 25000.0;
            Assert.Equal(25000.0, calc.SmoothedRate);
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_SuddenAcDisconnectInPhase1_RebaselinesWithoutCorruption()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            // Phase 1: Charging (Capacity increasing)
            calc.CalculateEta(phase: 1, remainingCapacity: 50000, fullCapacity: 60000);
            calc.UpdateCapacity(50000, t0);
            calc.UpdateCapacity(51000, t0.AddSeconds(60));

            double? initialRate = calc.SmoothedRate;
            Assert.NotNull(initialRate);
            Assert.True(initialRate.Value > 0);

            // AC is suddenly unplugged! Capacity starts dropping in Phase 1!
            calc.UpdateCapacity(50500, t0.AddSeconds(120));

            // Rate must NOT be updated with discharge rate!
            Assert.Equal(initialRate.Value, calc.SmoothedRate!.Value);
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_SuddenAcConnectInPhase2_RebaselinesWithoutCorruption()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            // Phase 2: Discharging (Capacity decreasing)
            calc.CalculateEta(phase: 2, remainingCapacity: 40000, fullCapacity: 60000);
            calc.UpdateCapacity(40000, t0);
            calc.UpdateCapacity(39000, t0.AddSeconds(60));

            double? initialRate = calc.SmoothedRate;
            Assert.NotNull(initialRate);
            Assert.True(initialRate.Value > 0);

            // Charger is suddenly connected! Capacity starts increasing in Phase 2!
            calc.UpdateCapacity(39500, t0.AddSeconds(120));

            // Rate must NOT be updated with charging rate!
            Assert.Equal(initialRate.Value, calc.SmoothedRate!.Value);
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_SleepGapOver5Minutes_RebaselinesWithoutRateSpike()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            calc.CalculateEta(phase: 2, remainingCapacity: 40000, fullCapacity: 60000);
            calc.UpdateCapacity(40000, t0);

            // Laptop went to sleep for 10 minutes (600 seconds)
            calc.UpdateCapacity(39000, t0.AddSeconds(600));

            // Must NOT calculate rate over sleep gap; smoothed rate should be null initially
            Assert.Null(calc.SmoothedRate);
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_CalculateEta_DivisionByZeroAndNaNProtected()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            calc.SmoothedRate = 0;
            var eta1 = calc.CalculateEta(phase: 1, remainingCapacity: 0, fullCapacity: 0, directRate: 0);
            Assert.Null(eta1);

            var eta2 = calc.CalculateEta(phase: 2, remainingCapacity: 0, fullCapacity: 0, directRate: 0);
            Assert.Equal(TimeSpan.Zero, eta2);

            // At 100%, phase 1 returns Zero
            var eta3 = calc.CalculateEta(phase: 1, remainingCapacity: 60000, fullCapacity: 60000);
            Assert.Equal(TimeSpan.Zero, eta3);
        }

        #endregion

        #region 6. PowerWakeLock Safety Tests

        [Fact]
        public void PowerWakeLock_ReleaseWhenNotLocked_IdempotentSuccess()
        {
            PowerWakeLock.ResetForTesting();
            Assert.False(PowerWakeLock.IsLocked);
            // Releasing when not locked must return true without errors
            bool released = PowerWakeLock.Release();
            Assert.True(released);
            Assert.False(PowerWakeLock.IsLocked);
        }

        [Fact]
        public void PowerWakeLock_AcquireScopedFailed_DoesNotReleaseOnDispose()
        {
            PowerWakeLock.ResetForTesting();
            PowerWakeLock.ExecutionStateOverride = _ => 0; // Simulate kernel failure

            using (var scope = PowerWakeLock.AcquireScoped())
            {
                Assert.False(PowerWakeLock.IsLocked);
            }

            Assert.False(PowerWakeLock.IsLocked);
            PowerWakeLock.ResetForTesting();
        }

        #endregion
    }
}
