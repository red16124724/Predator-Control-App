using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("SingleInstanceTests")]
    public class HardwareProtocolsAndBackgroundServicesStressTests
    {
        #region 1. PdhLoadMonitor Concurrency & Bug Hunting Tests

        [Fact]
        public void PdhLoadMonitor_ConcurrentSampling_ThreadSafety()
        {
            using var monitor = new PdhLoadMonitor();
            var exceptions = new List<Exception>();
            var threads = new List<Thread>();

            for (int i = 0; i < 16; i++)
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        for (int j = 0; j < 25; j++)
                        {
                            var (cpu, gpu) = monitor.Sample(j % 2 == 0);
                            if (cpu.HasValue)
                            {
                                Assert.False(double.IsNaN(cpu.Value), "CPU load must not be NaN");
                                Assert.False(double.IsInfinity(cpu.Value), "CPU load must not be Infinity");
                                Assert.InRange(cpu.Value, 0.0, 100.0);
                            }
                            if (gpu.HasValue)
                            {
                                Assert.False(double.IsNaN(gpu.Value), "GPU load must not be NaN");
                                Assert.False(double.IsInfinity(gpu.Value), "GPU load must not be Infinity");
                                Assert.InRange(gpu.Value, 0.0, 100.0);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                });
                threads.Add(t);
            }

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Empty(exceptions);
        }

        [Fact]
        public void PdhLoadMonitor_PostDispose_SafeAndIdempotent()
        {
            var monitor = new PdhLoadMonitor();
            monitor.Dispose();
            monitor.Dispose(); // Multiple dispose must not throw

            var (cpu, gpu) = monitor.Sample();
            Assert.Null(cpu);
            Assert.Null(gpu);
        }

        #endregion

        #region 2. WmiController Exception Handling & Power Mode Retries

        [Fact]
        public void WmiController_IsComOrWmiException_IdentifiesAllFailureModes()
        {
            var method = typeof(WmiController).GetMethod("IsComOrWmiException", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            // Direct COMException
            var comEx = new COMException("RPC failed", unchecked((int)0x800706BA));
            Assert.True((bool)method!.Invoke(null, new object[] { comEx })!);

            // Direct ManagementException
            var mgmtEx = new ManagementException("WMI query failed");
            Assert.True((bool)method!.Invoke(null, new object[] { mgmtEx })!);

            // Inner COMException
            var wrappedEx = new InvalidOperationException("Wrapper", new COMException("Inner RPC failure", unchecked((int)0x80010108)));
            Assert.True((bool)method!.Invoke(null, new object[] { wrappedEx })!);

            // Inner ManagementException
            var wrappedMgmt = new AggregateException(new ManagementException("Inner WMI failure"));
            Assert.True((bool)method!.Invoke(null, new object[] { wrappedMgmt.InnerException! })!);

            // Normal exception should be false
            var argEx = new ArgumentNullException("arg");
            Assert.False((bool)method!.Invoke(null, new object[] { argEx })!);
        }

        [Fact]
        public void WmiController_ConcurrentTelemetryAccess_NoDeadlock()
        {
            using var controller = new WmiController();
            var exceptions = new List<Exception>();
            var threads = new List<Thread>();

            for (int i = 0; i < 16; i++)
            {
                int tid = i;
                var t = new Thread(() =>
                {
                    try
                    {
                        for (int j = 0; j < 30; j++)
                        {
                            switch ((tid + j) % 6)
                            {
                                case 0:
                                    _ = controller.CpuTemp;
                                    break;
                                case 1:
                                    _ = controller.GpuTemp;
                                    break;
                                case 2:
                                    _ = controller.CpuFanRpm;
                                    break;
                                case 3:
                                    _ = controller.GpuFanRpm;
                                    break;
                                case 4:
                                    _ = controller.SystemFanRpm;
                                    break;
                                case 5:
                                    controller.UpdateModeKeyLed((byte)(j % 5));
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                });
                threads.Add(t);
            }

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Empty(exceptions);
        }

        #endregion

        #region 3. AcerHardwareProtocols Stress & Boundary Tests

        [Fact]
        public void AcerProtocol_DecodeSensorMask_HandlesEdgeCasesAndInvalidBits()
        {
            // Full mask
            var allSensors = AcerProtocol.DecodeSensorMask(0xFFFFFFFF_00000000UL);
            Assert.NotEmpty(allSensors);

            // Empty mask
            var noSensors = AcerProtocol.DecodeSensorMask(0UL);
            Assert.Empty(noSensors);

            // Mask with bits beyond 64 should not crash or overflow
            var customMask = AcerProtocol.DecodeSensorMask(1UL << 24); // bit 0 (CpuTemperature)
            Assert.Contains(SensorId.CpuTemperature, customMask);
        }

        [Fact]
        public void EcHidDevice_DisposedInstance_SafeImmediateReturn()
        {
            var dev = new EcHidDevice();
            dev.Dispose();

            // All methods must return null/false when disposed without reopening
            Assert.False(dev.IsOpen);
            Assert.Null(dev.ReadVersion());
            Assert.Null(dev.ReadMode());
            Assert.False(dev.WriteMode(1));
            Assert.Null(dev.ReadBacklightTimeout());
            Assert.False(dev.WriteBacklightTimeout(100, 30));
            Assert.Null(dev.Exchange(new byte[64]));
        }

        #endregion

        #region 4. CurveFollower Stress & Boundary Tests

        [Fact]
        public void CurveFollower_ExtremeTemperatures_NoOverflowOrCrash()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(40, 20),
                new(60, 40),
                new(80, 80),
                new(95, 100)
            };

            // Extreme high / low
            int pctHigh = follower.Update(1e20, curve);
            Assert.InRange(pctHigh, 0, 100);

            int pctLow = follower.Update(-1e20, curve);
            Assert.InRange(pctLow, 0, 100);

            int pctZero = follower.Update(0, curve);
            Assert.InRange(pctZero, 0, 100);

            // NaN / Infinity must not corrupt follower state
            int pctNan = follower.Update(double.NaN, curve);
            Assert.Equal(pctZero, pctNan);

            int pctInf = follower.Update(double.PositiveInfinity, curve);
            Assert.Equal(pctZero, pctInf);
        }

        [Fact]
        public void CurveFollower_ConcurrentUpdate_ThreadSafety()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(40, 20),
                new(60, 40),
                new(80, 80),
                new(95, 100)
            };

            var exceptions = new List<Exception>();
            var threads = new List<Thread>();

            for (int i = 0; i < 16; i++)
            {
                int tid = i;
                var t = new Thread(() =>
                {
                    try
                    {
                        for (int j = 0; j < 50; j++)
                        {
                            double temp = 30.0 + (tid * 3) + (j % 20);
                            int speed = follower.Update(temp, curve);
                            Assert.InRange(speed, 0, 100);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                });
                threads.Add(t);
            }

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Empty(exceptions);
        }

        #endregion

        #region 5. BacklightStateManager Concurrency & Resume Tests

        [Fact]
        public void BacklightStateManager_OnResume_WithClosedLid_ResetsBatteryOverride()
        {
            var manager = new BacklightStateManager();

            // Set manual override on battery
            manager.OnUserAdjustedBrightness(80, onBattery: true);
            Assert.True(manager.ManualBacklightOnBattery);
            Assert.Equal(80, manager.ManualBatteryBrightness);

            // Close lid
            manager.OnLidChanged(false, System.Windows.Forms.PowerLineStatus.Offline, out bool turnOff, out int target);
            Assert.True(turnOff);
            Assert.Equal(0, target);
            Assert.False(manager.ManualBacklightOnBattery);

            // Re-arm override while lid is hypothetically set
            manager.ManualBacklightOnBattery = true;
            manager.ManualBatteryBrightness = 60;

            // Resume with lid still closed
            manager.OnResume(System.Windows.Forms.PowerLineStatus.Offline, out bool resumeTurnOff, out int resumeTarget);
            Assert.True(resumeTurnOff);
            Assert.Equal(0, resumeTarget);
            Assert.False(manager.ManualBacklightOnBattery, "Manual battery backlight must be reset on resume when lid is closed");
            Assert.Equal(0, manager.ManualBatteryBrightness);
        }

        [Fact]
        public void BacklightStateManager_ConcurrentAccess_ThreadSafety()
        {
            var manager = new BacklightStateManager();
            var exceptions = new List<Exception>();
            var threads = new List<Thread>();

            for (int i = 0; i < 16; i++)
            {
                int tid = i;
                var t = new Thread(() =>
                {
                    try
                    {
                        for (int j = 0; j < 50; j++)
                        {
                            switch ((tid + j) % 5)
                            {
                                case 0:
                                    manager.OnLidChanged(j % 2 == 0, System.Windows.Forms.PowerLineStatus.Online, out _, out _);
                                    break;
                                case 1:
                                    manager.OnPowerSourceChanged(j % 2 == 0, out _, out _);
                                    break;
                                case 2:
                                    manager.OnSuspend(out _, out _);
                                    break;
                                case 3:
                                    manager.OnResume(System.Windows.Forms.PowerLineStatus.Offline, out _, out _);
                                    break;
                                case 4:
                                    manager.OnUserAdjustedBrightness(j * 2, onBattery: j % 2 == 1);
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                });
                threads.Add(t);
            }

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Empty(exceptions);
        }

        #endregion

        #region 6. SecureNamedPipeIpc Robustness Tests

        [Fact]
        public void PipeServer_Start_IdempotentUnderConcurrency()
        {
            int messageCount = 0;
            using var server = new SecureNamedPipeIpc.PipeServer(msg => Interlocked.Increment(ref messageCount));

            var exceptions = new List<Exception>();
            var threads = new List<Thread>();

            // Concurrent calls to Start()
            for (int i = 0; i < 10; i++)
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        server.Start();
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                });
                threads.Add(t);
            }

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Empty(exceptions);
        }

        [Fact]
        public async Task SendMessageWithVerificationAsync_EmptyOrNull_ReturnsFalse()
        {
            bool resNull = await SecureNamedPipeIpc.SendMessageWithVerificationAsync(null!, timeoutMs: 100);
            Assert.False(resNull);

            bool resEmpty = await SecureNamedPipeIpc.SendMessageWithVerificationAsync("", timeoutMs: 100);
            Assert.False(resEmpty);
        }

        [Fact]
        public async Task SendMessageWithVerificationAsync_NegativeTimeout_ClampedGracefully()
        {
            // Negative timeout should not throw ArgumentOutOfRangeException
            string nonExistentPipe = "PredatorControlPipe_NonExistent_" + Guid.NewGuid().ToString("N");
            bool res = await SecureNamedPipeIpc.SendMessageWithVerificationAsync("TestMessage", timeoutMs: -50, pipeName: nonExistentPipe);
            // Result is false because no server is listening, but it must not throw
            Assert.False(res);
        }

        #endregion
    }
}
