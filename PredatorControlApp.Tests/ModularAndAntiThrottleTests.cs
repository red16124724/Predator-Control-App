using System;
using System.Collections.Generic;
using System.Drawing;
using PredatorControlApp;
using PredatorControlApp.Channels;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class ModularAndAntiThrottleTests
    {
        #region 1. ThermalLimits Tests

        [Fact]
        public void ThermalLimits_Defaults_HaveExpectedBoundaries()
        {
            var cpuLimits = ThermalLimits.CpuDefault;
            Assert.Equal(75.0, cpuLimits.ThrottleThresholdCelsius);
            Assert.Equal(95.0, cpuLimits.CeilingThresholdCelsius);
            Assert.Equal(100.0, cpuLimits.CriticalThresholdCelsius);

            var gpuLimits = ThermalLimits.GpuDefault;
            Assert.Equal(70.0, gpuLimits.ThrottleThresholdCelsius);
            Assert.Equal(87.0, gpuLimits.CeilingThresholdCelsius);
            Assert.Equal(90.0, gpuLimits.CriticalThresholdCelsius);
        }

        [Fact]
        public void ThermalLimits_FromTjMax_CalculatesOffsetsCorrectly()
        {
            var limits = ThermalLimits.FromTjMax(100.0);
            Assert.Equal(88.0, limits.ThrottleThresholdCelsius); // 100 - 12
            Assert.Equal(94.0, limits.CeilingThresholdCelsius);  // 100 - 6
            Assert.Equal(100.0, limits.CriticalThresholdCelsius); // 100 (TjMax)

            var customLimits = ThermalLimits.FromTjMax(105.0);
            Assert.Equal(93.0, customLimits.ThrottleThresholdCelsius);
            Assert.Equal(99.0, customLimits.CeilingThresholdCelsius);
            Assert.Equal(105.0, customLimits.CriticalThresholdCelsius);
        }

        [Fact]
        public void ThermalLimits_FromGpuTarget_CalculatesOffsetsCorrectly()
        {
            var limits = ThermalLimits.FromGpuTarget(87.0);
            Assert.Equal(81.0, limits.ThrottleThresholdCelsius); // 87 - 6
            Assert.Equal(87.0, limits.CeilingThresholdCelsius);  // 87
            Assert.Equal(90.0, limits.CriticalThresholdCelsius); // 87 + 3
        }

        [Fact]
        public void ThermalLimits_Constructor_ValidatesThresholdOrdering()
        {
            // Ceiling <= Throttle must fail
            Assert.Throws<ArgumentException>(() => new ThermalLimits(90, 85, 95));

            // Critical <= Ceiling must fail
            Assert.Throws<ArgumentException>(() => new ThermalLimits(70, 90, 85));

            // Out-of-range temperatures must fail
            Assert.Throws<ArgumentOutOfRangeException>(() => new ThermalLimits(-10, 80, 90));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ThermalLimits(70, 80, 201));
        }

        #endregion

        #region 2. AntiThrottleSafety Calculation Engine Tests

        [Fact]
        public void AntiThrottle_BelowThrottle_ReturnsZeroBoostAndInactive()
        {
            var limits = ThermalLimits.CpuDefault; // 75, 95, 100
            var result = AntiThrottleSafety.EvaluateStatic(ChipTarget.Cpu, 60.0, limits, enabled: true);

            Assert.False(result.IsActive);
            Assert.False(result.IsTripwireLocked);
            Assert.Equal(0, result.TargetFloorPercent);
        }

        [Fact]
        public void AntiThrottle_MidRange_CalculatesLinearInterpolation()
        {
            var limits = new ThermalLimits(80.0, 100.0, 105.0); // range = 20
            // At 90°C -> exactly 50% between 80 and 100
            var result = AntiThrottleSafety.EvaluateStatic(ChipTarget.Cpu, 90.0, limits, enabled: true);

            Assert.True(result.IsActive);
            Assert.False(result.IsTripwireLocked);
            Assert.Equal(50, result.TargetFloorPercent);
        }

        [Fact]
        public void AntiThrottle_AtOrAboveCeiling_Returns100PercentFloor()
        {
            var limits = new ThermalLimits(80.0, 90.0, 95.0);
            var result = AntiThrottleSafety.EvaluateStatic(ChipTarget.Cpu, 92.0, limits, enabled: true);

            Assert.True(result.IsActive);
            Assert.True(result.IsTripwireLocked);
            Assert.Equal(100, result.TargetFloorPercent);
        }

        [Fact]
        public void AntiThrottle_AtOrAboveCritical_TriggersCriticalTripwire()
        {
            var limits = new ThermalLimits(80.0, 90.0, 95.0);
            var result = AntiThrottleSafety.EvaluateStatic(ChipTarget.Cpu, 96.0, limits, enabled: true);

            Assert.True(result.IsActive);
            Assert.True(result.IsTripwireLocked);
            Assert.Equal(100, result.TargetFloorPercent);
        }

        [Fact]
        public void AntiThrottle_Disabled_AlwaysReturnsZeroFloor()
        {
            var limits = ThermalLimits.CpuDefault;
            // 99°C with disabled safety returns 0 floor
            var result = AntiThrottleSafety.EvaluateStatic(ChipTarget.Cpu, 99.0, limits, enabled: false);

            Assert.False(result.IsActive);
            Assert.False(result.IsTripwireLocked);
            Assert.Equal(0, result.TargetFloorPercent);
        }

        [Fact]
        public void AntiThrottle_HandlesExtremeAndCorruptedTemperaturesGracefully()
        {
            var limits = ThermalLimits.CpuDefault;

            var nanRes = AntiThrottleSafety.EvaluateStatic(ChipTarget.Cpu, double.NaN, limits, enabled: true);
            Assert.Equal(0, nanRes.TargetFloorPercent);

            var infRes = AntiThrottleSafety.EvaluateStatic(ChipTarget.Cpu, double.PositiveInfinity, limits, enabled: true);
            Assert.Equal(0, infRes.TargetFloorPercent); // Corrupt/infinity temperature safely rejected

            var negRes = AntiThrottleSafety.EvaluateStatic(ChipTarget.Cpu, -50.0, limits, enabled: true);
            Assert.Equal(0, negRes.TargetFloorPercent);
            Assert.False(negRes.IsActive);
        }

        #endregion

        #region 3. CurveFollower Integration Tests

        [Fact]
        public void CurveFollower_AppliesSafetyFloorOverUserCurve()
        {
            var points = new List<Point>
            {
                new(40, 20),
                new(60, 30),
                new(80, 40),
                new(100, 50)
            };
            var follower = new CurveFollower();

            // Temp = 80 -> user curve gives 40%.
            // Pass safety floor of 75% -> follower should lock at 75%.
            int duty = follower.Update(80.0, points, safetyFloorPercent: 75);
            Assert.Equal(75, duty);
            Assert.Equal(75, follower.Current);
        }

        [Fact]
        public void CurveFollower_CriticalSafetyTripwire_BypassesHysteresisDeadband()
        {
            var points = new List<Point>
            {
                new(40, 30),
                new(70, 50),
                new(100, 80)
            };
            var follower = new CurveFollower(hysteresisDegrees: 5);

            // Set steady state at 50%
            follower.Update(70.0, points, safetyFloorPercent: 50);
            Assert.Equal(50, follower.Current);

            // Now critical tripwire fires at high temp with 100% floor
            var criticalResult = new AntiThrottleResult(
                IsEnabled: true,
                IsActive: true,
                IsTripwireLocked: true,
                TargetFloorPercent: 100,
                CurrentTemp: 98.0,
                ThermalLimit: 95.0,
                Chip: ChipTarget.Cpu
            );
            int emergencyDuty = follower.Update(98.0, points, criticalResult);

            Assert.Equal(100, emergencyDuty);
            Assert.Equal(100, follower.Current);
        }

        [Fact]
        public void CurveFollower_ThermalRecovery_PreservesDownRampSmoothing()
        {
            var points = new List<Point>
            {
                new(40, 20),
                new(60, 40),
                new(80, 70),
                new(100, 100)
            };
            var follower = new CurveFollower(hysteresisDegrees: 5);

            // Ramp up with curve at 85°C (78%)
            follower.Update(85.0, points, safetyFloorPercent: 0);
            Assert.Equal(78, follower.Current);

            // Small drop within hysteresis down window (e.g. 84°C)
            // Hysteresis holds peak temp (85°C), keeping fan at 78% rather than dropping to 84°C curve point (76%)
            follower.Update(84.0, points, safetyFloorPercent: 0);
            Assert.Equal(78, follower.Current);
        }

        #endregion

        #region 4. Modular Hardware Channels & Router Tests

        [Fact]
        public void WmiOperatingModeChannel_DispatchesPayloadAndTracksMode()
        {
            string? executedCmd = null;
            ulong executedPayload = 0;

            var channel = new WmiOperatingModeChannel((cmd, payload) =>
            {
                executedCmd = cmd;
                executedPayload = payload;
                return (true, 0x01);
            });

            Assert.Equal(ModeTransportKind.WmiMisc0x0B, channel.Transport);

            bool ok = channel.TrySetOperatingMode(OperatingMode.Turbo);
            Assert.True(ok);
            Assert.Equal("SetGamingMiscSetting", executedCmd);
            Assert.Equal(0x0BUL | ((ulong)OperatingMode.Turbo << 8), executedPayload);
        }

        [Fact]
        public void OperatingModeChannelRouter_TriesPrimaryThenFallback()
        {
            bool fallbackCalled = false;
            OperatingMode lastFallbackMode = OperatingMode.Balanced;

            var mockPrimary = new ActionOperatingModeChannel((fn, arg) => (false, 0UL));
            var mockFallback = new ActionOperatingModeChannel((fn, arg) =>
            {
                fallbackCalled = true;
                lastFallbackMode = (OperatingMode)arg;
                return (true, 0UL);
            });

            var router = new OperatingModeChannelRouter(mockPrimary, mockFallback);
            Assert.Equal(ModeTransportKind.Composite, router.Transport);

            bool result = router.TrySetOperatingMode(OperatingMode.Performance);
            Assert.True(result);
            Assert.True(fallbackCalled);
            Assert.Equal(OperatingMode.Performance, lastFallbackMode);
        }

        [Fact]
        public void WmiFanControlChannel_DispatchesSpeedAndBehaviorCorrectly()
        {
            byte lastMode = 0;
            ulong lastFanType = 0;
            byte lastPercent = 0;

            var fanChannel = new WmiFanControlChannel(
                (m, apply) => { lastMode = m; return true; },
                (type, pct, force) => { lastFanType = type; lastPercent = pct; return true; },
                setChannelSpeed: null,
                getRpm: (ch) => ch.Id == FanId.Cpu ? 3500 : 0
            );

            Assert.True(fanChannel.TrySetFanBehavior(2));
            Assert.Equal(2, lastMode);

            Assert.True(fanChannel.TrySetFanSpeed(FanChannel.Cpu, 75));
            Assert.Equal(1UL, lastFanType);
            Assert.Equal(75, lastPercent);

            Assert.Equal(3500, fanChannel.GetFanRpm(FanChannel.Cpu));
            Assert.Equal(0, fanChannel.GetFanRpm(FanChannel.Gpu));
        }

        [Fact]
        public void EcHidFanControlChannel_DelegatesToFallbackSafely()
        {
            ulong lastFanType = 0;
            byte lastPercent = 0;

            var fallback = new WmiFanControlChannel(
                (m, apply) => true,
                (type, pct, force) => { lastFanType = type; lastPercent = pct; return true; }
            );

            // Channel delegates cleanly to fallback
            using var dummyDev = new EcHidDevice();
            var ecChannel = new EcHidFanControlChannel(dummyDev, fallback);

            bool ok = ecChannel.TrySetFanSpeed(FanChannel.System, 60);
            Assert.True(ok);
            Assert.Equal(2UL, lastFanType);
            Assert.Equal(60, lastPercent);
        }

        [Fact]
        public void ThermalSafetyService_EvaluatesMultiSensorFloors()
        {
            var service = new ThermalSafetyService(new ThermalLimits(70, 90, 95), new ThermalLimits(65, 85, 90));
            Assert.True(service.AntiThrottleEnabled);

            // Cold temps
            var coldCpu = service.EvaluateCpu(50.0);
            var coldGpu = service.EvaluateGpu(45.0);
            Assert.False(coldCpu.IsActive);
            Assert.False(coldGpu.IsActive);

            var coldStatus = service.EvaluateStatus(50.0, 45.0);
            Assert.False(coldStatus.IsActive);
            Assert.Equal(0, coldStatus.CpuSpeedFloor);
            Assert.Equal(0, coldStatus.GpuSpeedFloor);

            // Hot temps: CPU at 80°C (50% into 70-90°C range), GPU at 85°C (100% floor)
            var hotCpu = service.EvaluateCpu(80.0);
            var hotGpu = service.EvaluateGpu(85.0);
            Assert.Equal(50, hotCpu.TargetFloorPercent);
            Assert.Equal(100, hotGpu.TargetFloorPercent);

            var hotStatus = service.EvaluateStatus(80.0, 85.0);
            Assert.True(hotStatus.IsActive);
            Assert.Equal(50, hotStatus.CpuSpeedFloor);
            Assert.Equal(100, hotStatus.GpuSpeedFloor);

            var (safeCpu, safeGpu) = service.ApplyAntiThrottle(80.0, 85.0, targetCpuSpeed: 30, targetGpuSpeed: 40);
            Assert.Equal(50, safeCpu);
            Assert.Equal(100, safeGpu);
        }

        #endregion
    }
}
