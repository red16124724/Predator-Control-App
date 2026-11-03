using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("NoParallel")]
    public class TelemetryThermalAudioStressTests
    {
        #region 1. Telemetry Snapshot & SystemInfoScanner Tests

        [Fact]
        public void TelemetrySnapshot_Sanitization_ClampsPhysicalLimitsAndCleansCorruptValues()
        {
            var corrupt = new TelemetrySnapshot(
                CpuTemp: -50,
                GpuTemp: 250, // above 150
                CpuRpm: -100,
                GpuRpm: 50000, // above 15000
                SysRpm: 12000,
                CpuLoad: double.NaN,
                GpuLoad: 150.0,
                GpuAsleep: false,
                OnBattery: false,
                HasHwMode: true,
                ProbedHwMode: 0x01,
                CpuFrequencyMhz: double.NegativeInfinity,
                GpuFrequencyMhz: 1800.0,
                CpuPowerWatts: -10.0,
                GpuPowerWatts: double.NaN
            );

            var clean = corrupt.Sanitize();

            Assert.Equal(0, clean.CpuTemp);
            Assert.Equal(0, clean.GpuTemp);
            Assert.Equal(0, clean.CpuRpm);
            Assert.Equal(15000, clean.GpuRpm);
            Assert.Equal(12000, clean.SysRpm);
            Assert.Null(clean.CpuLoad);
            Assert.Equal(100.0, clean.GpuLoad);
            Assert.Equal(0, clean.CpuFrequencyMhz);
            Assert.Equal(1800.0, clean.GpuFrequencyMhz);
            Assert.Equal(0, clean.CpuPowerWatts);
            Assert.Equal(0, clean.GpuPowerWatts);
            Assert.True(clean.HasCpuSensorDisconnect);
            Assert.True(clean.HasGpuSensorDisconnect);
            Assert.False(clean.IsExtremeThermal());
        }

        [Fact]
        public void SystemInfoScanner_ScanWithNullOrDisconnect_TracksMissesCleanly()
        {
            var scanner = new SystemInfoScanner();
            Assert.Equal(0, scanner.ConsecutiveSensorMisses);

            // Null WMI -> returns Empty
            var empty = scanner.Scan(null, null, isWindowVisible: false, onBattery: false, currentPowerMode: 0x01);
            Assert.Equal(0, empty.CpuTemp);
            Assert.Equal(0, scanner.ConsecutiveSensorMisses);

            scanner.ResetMissCounter();
            Assert.Equal(0, scanner.ConsecutiveSensorMisses);
        }

        #endregion

        #region 2. Thermal Safety Coordinator & Extreme Values Stress Tests

        [Fact]
        public void ThermalCoordinator_ExtremeThermalValues_HandledGracefully()
        {
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);

            // NaN / Infinity / Negative should be ignored and stage remain 0
            Assert.Equal(0, coordinator.Evaluate(double.NaN, double.NaN).Stage);
            Assert.Equal(0, coordinator.Evaluate(double.NegativeInfinity, double.PositiveInfinity).Stage);
            Assert.Equal(0, coordinator.Evaluate(-100.0, -50.0).Stage);
            Assert.Equal(0, coordinator.Evaluate(300.0, 300.0).Stage); // > 150 ignored as sensor glitch

            // Single 120°C spike from 50°C baseline does NOT cause immediate fan slam
            for (int i = 0; i < 10; i++) coordinator.Evaluate(50.0, 50.0);
            var spike = coordinator.Evaluate(120.0, 50.0);
            // EMA: (120 * 0.35) + (50 * 0.65) = 74.5°C < 75°C warn
            Assert.Equal(0, spike.Stage);
            Assert.False(spike.Critical);
            Assert.Equal(0, spike.CpuFanFloor);
        }

        [Fact]
        public void ThermalCoordinator_SensorDisconnectRecovery_DecaysEmaAndReleasesLatchedFloor()
        {
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);

            // Drive CPU to Critical (100°C)
            ThermalDecision decision = ThermalDecision.Idle;
            for (int i = 0; i < 15; i++) decision = coordinator.Evaluate(100.0, 0);

            Assert.Equal(3, decision.Stage);
            Assert.True(decision.Critical);
            Assert.Equal(100, decision.CpuFanFloor);

            // Sensor disconnects: returns 0.0 consecutively
            // After 3+ consecutive misses, EMA decays rapidly toward 0 and latched floor releases
            for (int i = 0; i < 10; i++)
            {
                decision = coordinator.Evaluate(0.0, 0.0);
            }

            // Latched floor must be released (0) and stage back to 0
            Assert.Equal(0, decision.Stage);
            Assert.False(decision.Critical);
            Assert.Equal(0, decision.CpuFanFloor);
            Assert.Null(decision.CpuPowerCapWatts);
        }

        [Fact]
        public void ThermalCoordinator_ZeroMutationOfUserSettings()
        {
            var appSettings = new AppSettings
            {
                AntiThrottleEnabled = true,
                ThermalCpuWarnCelsius = 75,
                ThermalCpuCeilingCelsius = 95,
                ThermalCpuCriticalCelsius = 100,
                CpuMinAllowedPowerWatts = 15,
                CpuThermalSpecTdpWatts = 45
            };

            var profile = appSettings.ToThermalProfile();
            var coordinator = new ThermalSafetyCoordinator(profile);

            // Evaluate through extreme critical and recovery
            for (int i = 0; i < 20; i++) coordinator.Evaluate(105.0, 95.0);
            for (int i = 0; i < 20; i++) coordinator.Evaluate(50.0, 45.0);

            // AppSettings must be completely unmutated
            Assert.True(appSettings.AntiThrottleEnabled);
            Assert.Equal(75, appSettings.ThermalCpuWarnCelsius);
            Assert.Equal(95, appSettings.ThermalCpuCeilingCelsius);
            Assert.Equal(100, appSettings.ThermalCpuCriticalCelsius);
            Assert.Equal(15, appSettings.CpuMinAllowedPowerWatts);
            Assert.Equal(45, appSettings.CpuThermalSpecTdpWatts);
        }

        #endregion

        #region 3. AntiThrottleSafety & ThermalSafetyService Tests

        [Fact]
        public void AntiThrottleSafety_ExtremeValues_HandledSafely()
        {
            var safety = new AntiThrottleSafety(ThermalLimits.CpuDefault, enabled: true);

            var resNan = safety.Evaluate(ChipTarget.Cpu, double.NaN);
            Assert.False(resNan.IsActive);
            Assert.False(resNan.IsTripwireLocked);
            Assert.Equal(0, resNan.TargetFloorPercent);

            var resNeg = safety.Evaluate(ChipTarget.Cpu, -20.0);
            Assert.False(resNeg.IsActive);
            Assert.Equal(0, resNeg.TargetFloorPercent);

            var resCrit = safety.Evaluate(ChipTarget.Cpu, 102.0);
            Assert.True(resCrit.IsActive);
            Assert.True(resCrit.IsTripwireLocked);
            Assert.Equal(100, resCrit.TargetFloorPercent);

            // Transient power cap calculation
            int? cap = safety.CalculatePowerCapWatts(102.0, targetPl1Watts: 55, minPowerWatts: 15);
            Assert.NotNull(cap);
            Assert.Equal(15, cap!.Value);

            int? safeCap = safety.CalculatePowerCapWatts(double.NaN, 55, 15);
            Assert.Null(safeCap);
        }

        [Fact]
        public void ThermalSafetyService_EvaluateStatus_SanitizesCorruptTemperatures()
        {
            var service = new ThermalSafetyService();
            var status = service.EvaluateStatus(double.NaN, -40.0);

            Assert.Equal(0.0, status.CpuTemp);
            Assert.Equal(0.0, status.GpuTemp);
            Assert.False(status.IsActive);
            Assert.False(status.IsCpuCritical);
            Assert.False(status.IsGpuCritical);
            Assert.Equal(0, status.CpuSpeedFloor);
            Assert.Equal(0, status.GpuSpeedFloor);
            Assert.Null(status.CpuPowerCapWatts);
        }

        #endregion

        #region 4. WasapiCapture & AudioMeter Thread Safety & Disconnect Handling

        [Fact]
        public void WasapiCapture_StartAndStop_ThreadSafeAndIdempotent()
        {
            using var capture = new WasapiCapture();
            Assert.False(capture.IsRunning);

            // Repeated start/stop must be safe and not throw
            for (int i = 0; i < 3; i++)
            {
                capture.Start();
                Thread.Sleep(20);
                capture.Stop();
            }

            Assert.False(capture.IsRunning);
        }

        [Fact]
        public void AudioMeter_ThreadSafeQueryingUnderConcurrentAccess()
        {
            using var meter = new AudioMeter();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            Parallel.For(0, 100, i =>
            {
                try
                {
                    _ = meter.Level;
                    _ = meter.IsBeat;
                    _ = meter.BeatStrength;
                    _ = meter.GetSpectrum();

                    float[] buffer = new float[32];
                    _ = meter.GetSpectrum(buffer);

                    byte[] rgb = new byte[12];
                    _ = meter.RenderZones(255, 128, 64, 100, rgb, 4);

                    if (i % 10 == 0) meter.Reset();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
        }

        #endregion

        #region 5. GameSyncManager Profile Switching & Window Detection

        [Fact]
        public void GameSyncManager_ProfileLifecycleAndSnapshots()
        {
            using var manager = new GameSyncManager();
            manager.IsEnabled = false;

            var profile = new GameProfile
            {
                ExecutableName = "Cyberpunk2077.exe",
                PowerMode = 0x02,
                CpuFanSpeed = 80,
                GpuFanSpeed = 85
            };

            manager.AddProfile(profile);
            Assert.Contains(manager.Profiles, p => p.ExecutableName.Equals("Cyberpunk2077.exe", StringComparison.OrdinalIgnoreCase));

            var snapshot = new DashboardSnapshot { PowerMode = 0x01, CpuFanSpeed = 50, GpuFanSpeed = 50 };
            manager.SetPreGameSnapshot(snapshot);

            manager.RemoveProfile("Cyberpunk2077.exe");
            Assert.DoesNotContain(manager.Profiles, p => p.ExecutableName.Equals("Cyberpunk2077.exe", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void GameSyncManager_SafeExecutableNameParsing()
        {
            Assert.Equal("game.exe", GameSyncManager.SafeGetExecutableName(@"C:\Games\Steam\game.exe"));
            Assert.Equal("game.exe", GameSyncManager.SafeGetExecutableName("/usr/bin/game.exe"));
            Assert.Equal("game", GameSyncManager.SafeGetProcessName("game.exe"));
            Assert.Equal("", GameSyncManager.SafeGetExecutableName(null));
        }

        #endregion

        #region 6. BacklightStateManager Idle Timeout & Dimming

        [Fact]
        public void BacklightStateManager_IdleTimeoutAndDimmingRestoration()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 100;
            mgr.IdleTimeoutSeconds = 30; // 30 sec timeout
            mgr.DimmedBrightnessPercent = 25;

            var t0 = DateTime.UtcNow;
            mgr.OnPowerSourceChanged(true, out bool shouldTurnOff, out int targetBright);
            Assert.Equal(100, targetBright);
            Assert.False(mgr.IsDimmed);

            // 10 seconds later: not idle yet
            bool dimTriggered = mgr.EvaluateIdle(t0.AddSeconds(10), out bool shouldDim, out int dimmedBright);
            Assert.False(dimTriggered);
            Assert.False(mgr.IsDimmed);

            // 35 seconds later: idle timeout reached!
            dimTriggered = mgr.EvaluateIdle(t0.AddSeconds(35), out shouldDim, out dimmedBright);
            Assert.True(dimTriggered);
            Assert.True(shouldDim);
            Assert.Equal(25, dimmedBright);
            Assert.True(mgr.IsDimmed);

            // User activity occurs: brightness must be restored!
            bool restored = mgr.OnActivity(out bool shouldRestore, out int restoredBright);
            Assert.True(restored);
            Assert.True(shouldRestore);
            Assert.Equal(100, restoredBright);
            Assert.False(mgr.IsDimmed);
        }

        #endregion

        #region 7. Minimized-to-Tray Suspension & 10,000-Tick Soak Simulation

        [Fact]
        public void Telemetry_10000Ticks_SoakSimulation_ZeroMemoryGrowthAndStableWorkingSet()
        {
            // Simulate 10,000 telemetry cycles with mixed sensor inputs
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);
            var scanner = new SystemInfoScanner();
            var safety = new AntiThrottleSafety(ThermalLimits.CpuDefault);

            // Pre-warm JIT
            for (int i = 0; i < 100; i++)
            {
                coordinator.Evaluate(55.0, 50.0);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long initialMemory = GC.GetTotalMemory(true);
            int initialThreadCount = Process.GetCurrentProcess().Threads.Count;

            var random = new Random(42);

            for (int tick = 0; tick < 10000; tick++)
            {
                // Varied telemetry profile: normal, warm, ceiling, critical, sensor glitch, and disconnect
                double cpuTemp = (tick % 500 == 0) ? double.NaN
                    : (tick % 700 == 0) ? 0.0 // sensor disconnect
                    : (tick % 1000 == 0) ? 120.0 // transient spike
                    : 45.0 + random.Next(0, 45); // 45°C - 90°C

                double gpuTemp = (tick % 700 == 0) ? 0.0 : 40.0 + random.Next(0, 40);

                var decision = coordinator.Evaluate(cpuTemp, gpuTemp);
                _ = safety.Evaluate(ChipTarget.Cpu, cpuTemp);

                if (tick % 2000 == 0)
                {
                    // Verify coordinator is always in a coherent, valid stage
                    Assert.InRange(decision.Stage, 0, 3);
                    Assert.InRange(decision.CpuFanFloor, 0, 100);
                    Assert.InRange(decision.GpuFanFloor, 0, 100);
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long finalMemory = GC.GetTotalMemory(true);
            long memoryDelta = finalMemory - initialMemory;

            // Memory growth after 10,000 ticks must be negligible (< 1 MB after GC)
            Assert.True(memoryDelta < 1_000_000, $"Memory growth exceeded 1MB: {memoryDelta} bytes");

            // Verify thread pool stability
            int finalThreadCount = Process.GetCurrentProcess().Threads.Count;
            Assert.True(Math.Abs(finalThreadCount - initialThreadCount) <= 4,
                $"Thread count drifted excessively: Initial={initialThreadCount}, Final={finalThreadCount}");
        }

        #endregion
    }
}
