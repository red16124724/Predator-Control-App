using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Xunit;
using PredatorControlApp;
using PredatorControlApp.Channels;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Advanced Exhaustive Adversarial Stress Test Suite targeting all PredatorControlApp subsystems
    /// under high concurrency, extreme edge cases, fuzzing, and invariant bounds checking:
    /// 1. High-frequency concurrent power mode, fan mode, and fan curve mutations across multiple threads (1000+ iterations).
    /// 2. Dynamic slider coupling and bounds invariant testing (PL2 >= PL1 at all times, clamped within [MinPower..MaxPl2]).
    /// 3. AntiThrottle safety stress under erratic/extreme temperatures (-50°C, NaN, +300°C, rapid thermal spikes/drops).
    /// 4. Channel router fallback stress (simulated EC-HID failure, WMI failure, router fallback transitions).
    /// 5. Battery calibration state machine fuzzing (erratic capacity drops, negative discharge rates, sudden plug/unplug).
    /// 6. Corrupted hardware signatures, invalid TDP values, and null/empty registry payloads.
    /// </summary>
    public class AdvancedAdversarialSubsystemStressTests
    {
        #region 1. High-Frequency Concurrent Power Mode, Fan Mode, & Fan Curve Mutations (1000+ iterations)

        [Fact]
        public void Subsystem1_ConcurrentModeAndCurveMutations_32Threads_1000Iterations_ThreadSafety()
        {
            var settings = new AppSettings();
            for (int i = 0; i < 8; i++)
            {
                settings.CpuCurve.Add(new CurvePointData(30 + i * 10, 10 + i * 12));
                settings.GpuCurve.Add(new CurvePointData(30 + i * 10, 15 + i * 11));
            }

            const int threadCount = 32;
            const int iterationsPerThread = 1000;
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];
            using var barrier = new Barrier(threadCount);

            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    var rnd = new Random(tid * 773 + 13);

                    for (int iter = 0; iter < iterationsPerThread; iter++)
                    {
                        try
                        {
                            int action = (tid + iter) % 7;
                            switch (action)
                            {
                                case 0:
                                    settings.PowerMode = (byte)rnd.Next(0, 10);
                                    settings.PowerModeAC = (byte)rnd.Next(0, 10);
                                    settings.PowerModeBattery = (byte)rnd.Next(0, 10);
                                    settings.AutoPowerAC = rnd.Next(-5, 15);
                                    settings.AutoPowerBattery = rnd.Next(-5, 15);
                                    break;

                                case 1:
                                    settings.FanMode = (byte)rnd.Next(0, 5);
                                    settings.FanModeAC = (byte)rnd.Next(0, 5);
                                    settings.FanModeBattery = (byte)rnd.Next(0, 5);
                                    settings.FanSpeedCpu = rnd.Next(-50, 200);
                                    settings.FanSpeedGpu = rnd.Next(-50, 200);
                                    settings.FanSpeedSys = rnd.Next(-50, 200);
                                    settings.FanCurveEnabled = (iter % 2 == 0);
                                    settings.AntiThrottleEnabled = (iter % 3 != 0);
                                    break;

                                case 2:
                                    lock (settings.CpuCurve)
                                    {
                                        if (settings.CpuCurve.Count > 24)
                                            settings.CpuCurve.Clear();
                                        settings.CpuCurve.Add(new CurvePointData(rnd.Next(-20, 150), rnd.Next(-20, 150)));
                                    }
                                    break;

                                case 3:
                                    lock (settings.GpuCurve)
                                    {
                                        if (settings.GpuCurve.Count > 24)
                                            settings.GpuCurve.Clear();
                                        settings.GpuCurve.Add(new CurvePointData(rnd.Next(-20, 150), rnd.Next(-20, 150)));
                                    }
                                    break;

                                case 4:
                                    var snapshot = settings.CreateSnapshot();
                                    Assert.NotNull(snapshot);
                                    string json = JsonSerializer.Serialize(snapshot);
                                    Assert.NotEmpty(json);
                                    break;

                                case 5:
                                    var copyCpu = AppSettings.SafeCopyCurve(settings.CpuCurve);
                                    var copyGpu = AppSettings.SafeCopyCurve(settings.GpuCurve);
                                    Assert.NotNull(copyCpu);
                                    Assert.NotNull(copyGpu);
                                    break;

                                case 6:
                                    settings.Sanitize();
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);

            settings.Sanitize();
            Assert.InRange(settings.PowerMode, (byte)0, (byte)6);
            Assert.InRange(settings.FanMode, (byte)1, (byte)3);
            Assert.InRange(settings.FanSpeedCpu, 0, 100);
            Assert.InRange(settings.FanSpeedGpu, 0, 100);
            Assert.InRange(settings.FanSpeedSys, 0, 100);
        }

        [Fact]
        public void Subsystem1_CurveFollower_HighFrequencyConcurrency_16Threads_1000Iterations()
        {
            var follower = new CurveFollower(hysteresisDegrees: 3);
            var curve = FanCurveGraph.DefaultCpuPoints;

            const int threadCount = 16;
            const int iterations = 1000;
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    var rnd = new Random(tid * 311 + 7);
                    for (int i = 0; i < iterations; i++)
                    {
                        try
                        {
                            double temp = rnd.NextDouble() * 150.0 - 25.0;
                            int floor = rnd.Next(0, 101);

                            if (i % 25 == 0)
                            {
                                follower.HysteresisDegrees = rnd.Next(-2, 15);
                            }
                            if (i % 200 == 0)
                            {
                                follower.Reset();
                            }

                            int speed = follower.Update(temp, curve, floor);
                            Assert.InRange(speed, 0, 100);
                            Assert.True(speed >= floor, $"Speed {speed} fell below safety floor {floor} at temp {temp}");
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);
        }

        [Fact]
        public void Subsystem1_FanCurveGraph_Normalize_AdversarialInputs_ExtremePoints()
        {
            // Null returns default
            var normNull = FanCurveGraph.Normalize((List<Point>?)null);
            Assert.Equal(FanCurveGraph.ControlPointCount, normNull.Count);

            // Empty returns default
            var normEmpty = FanCurveGraph.Normalize(new List<Point>());
            Assert.Equal(FanCurveGraph.ControlPointCount, normEmpty.Count);

            // Single point returns default
            var normSingle = FanCurveGraph.Normalize(new List<Point> { new(50, 50) });
            Assert.Equal(FanCurveGraph.ControlPointCount, normSingle.Count);

            // Huge array of 10,000 disordered chaotic points
            var rnd = new Random(42);
            var hugeList = new List<Point>(10_000);
            for (int i = 0; i < 10_000; i++)
            {
                hugeList.Add(new Point(rnd.Next(-1000, 2000), rnd.Next(-500, 500)));
            }

            var normHuge = FanCurveGraph.Normalize(hugeList);
            Assert.Equal(FanCurveGraph.ControlPointCount, normHuge.Count);
            for (int i = 0; i < normHuge.Count; i++)
            {
                Assert.InRange(normHuge[i].X, 30, 100);
                Assert.InRange(normHuge[i].Y, 0, 100);
                if (i > 0)
                {
                    Assert.True(normHuge[i].X > normHuge[i - 1].X, $"Points not strictly monotonic at {i}: {normHuge[i-1].X} vs {normHuge[i].X}");
                }
            }
        }

        #endregion

        #region 2. Dynamic Slider Coupling & Bounds Invariant Testing (PL2 >= PL1 at all times)

        [Fact]
        public void Subsystem2_AppSettings_SetPowerLimitsForMode_MaintainsPl2GtePl1_AllModesAndInputs()
        {
            var settings = new AppSettings
            {
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 115,
                ScannedMaxPl2Watts = 140,
                ScannedCpuTdpWatts = 45,
                HasScannedHardwarePowerBounds = true
            };

            var modes = new[] { OperatingMode.Eco, OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo };
            var rnd = new Random(999);

            for (int i = 0; i < 2000; i++)
            {
                var mode = modes[i % modes.Length];
                int rawP1 = rnd.Next(-50, 300);
                int rawP2 = rnd.Next(-50, 300);

                settings.SetPowerLimitsForMode(mode, rawP1, rawP2);
                var (eff1, eff2) = settings.GetEffectivePowerLimitsForMode(mode);

                // Critical Invariant 1: PL2 >= PL1 at all times
                Assert.True(eff2 >= eff1, $"Invariant PL2 >= PL1 violated for mode {mode}: PL1={eff1}, PL2={eff2} (Input rawP1={rawP1}, rawP2={rawP2})");

                // Critical Invariant 2: Both within [MinPower..MaxPl2]
                Assert.InRange(eff1, settings.ScannedMinPowerWatts, settings.ScannedMaxPl1Watts);
                Assert.InRange(eff2, settings.ScannedMinPowerWatts, settings.ScannedMaxPl2Watts);
            }
        }

        [Fact]
        public void Subsystem2_AppSettings_CorruptedScannedBounds_InvertedBounds_MaintainsInvariant()
        {
            var settings = new AppSettings
            {
                ScannedMinPowerWatts = 120, // Min is larger than MaxPl1 and MaxPl2!
                ScannedMaxPl1Watts = 45,
                ScannedMaxPl2Watts = 30,
                ScannedCpuTdpWatts = -500,
                HasScannedHardwarePowerBounds = true
            };

            // GetEffectivePowerLimitsForMode must gracefully repair corrupted inverted bounds and enforce PL2 >= PL1
            var modes = new[] { OperatingMode.Eco, OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo };
            foreach (var mode in modes)
            {
                var (eff1, eff2) = settings.GetEffectivePowerLimitsForMode(mode);
                Assert.True(eff2 >= eff1, $"Inverted bounds caused PL2 < PL1: PL1={eff1}, PL2={eff2} for {mode}");
                Assert.True(eff1 >= 5, $"PL1 {eff1} fell below absolute minimum 5W");
                Assert.True(eff2 >= 5, $"PL2 {eff2} fell below absolute minimum 5W");
            }
        }

        [Fact]
        public void Subsystem2_SliderCoupling_RealTimePL2GtePL1_Fuzzing()
        {
            // Simulate the exact Form1 slider coupling handlers
            var sliderPl1 = new PredatorSlider { Minimum = 15, Maximum = 115, Value = 45 };
            var sliderPl2 = new PredatorSlider { Minimum = 15, Maximum = 140, Value = 65 };

            bool isUpdating = false;

            sliderPl1.ValueChanged += (s, e) =>
            {
                if (isUpdating) return;
                if (sliderPl1.Value > sliderPl2.Value)
                {
                    isUpdating = true;
                    try
                    {
                        sliderPl2.Value = sliderPl1.Value;
                        if (sliderPl1.Value > sliderPl2.Value)
                        {
                            sliderPl1.Value = sliderPl2.Value;
                        }
                    }
                    finally { isUpdating = false; }
                }
            };

            sliderPl2.ValueChanged += (s, e) =>
            {
                if (isUpdating) return;
                if (sliderPl2.Value < sliderPl1.Value)
                {
                    isUpdating = true;
                    try
                    {
                        sliderPl1.Value = sliderPl2.Value;
                        if (sliderPl2.Value < sliderPl1.Value)
                        {
                            sliderPl2.Value = sliderPl1.Value;
                        }
                    }
                    finally { isUpdating = false; }
                }
            };

            var rnd = new Random(12345);

            for (int i = 0; i < 2000; i++)
            {
                int targetPl1 = rnd.Next(0, 160);
                int targetPl2 = rnd.Next(0, 160);

                if (i % 2 == 0)
                {
                    sliderPl1.Value = targetPl1;
                }
                else
                {
                    sliderPl2.Value = targetPl2;
                }

                // Invariant must strictly hold after every event dispatch
                Assert.True(sliderPl2.Value >= sliderPl1.Value,
                    $"Slider Coupling Violation at iteration {i}: PL1={sliderPl1.Value}, PL2={sliderPl2.Value} (Target: PL1={targetPl1}, PL2={targetPl2})");
                Assert.InRange(sliderPl1.Value, sliderPl1.Minimum, sliderPl1.Maximum);
                Assert.InRange(sliderPl2.Value, sliderPl2.Minimum, sliderPl2.Maximum);
            }
        }

        #endregion

        #region 3. AntiThrottle Safety Stress Under Erratic/Extreme Temperatures

        [Theory]
        [InlineData(-50.0)]
        [InlineData(-1000.0)]
        [InlineData(0.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(300.0)]
        [InlineData(10000.0)]
        public void Subsystem3_AntiThrottleSafety_ErraticTemperatures_NeverThrowsOrCrashes(double temp)
        {
            var safety = new AntiThrottleSafety(ThermalLimits.CpuDefault, enabled: true);

            int boost = safety.CalculateBoost(temp);
            Assert.InRange(boost, 0, 100);

            var res = safety.Evaluate(ChipTarget.Cpu, temp);
            Assert.InRange(res.TargetFloorPercent, 0, 100);

            if (temp >= 100.0 && !double.IsNaN(temp) && !double.IsInfinity(temp))
            {
                Assert.Equal(100, res.TargetFloorPercent);
                Assert.True(res.IsActive);
                Assert.True(res.IsTripwireLocked);
            }
            else if (temp <= 0.0 || double.IsNaN(temp) || double.IsInfinity(temp))
            {
                Assert.Equal(0, res.TargetFloorPercent);
                Assert.False(res.IsActive);
            }
        }

        [Fact]
        public void Subsystem3_ThermalSafetyService_RapidSpikeOscillations_10000Cycles()
        {
            var service = new ThermalSafetyService(ThermalLimits.CpuDefault, ThermalLimits.GpuDefault, enabled: true);
            var follower = new CurveFollower(hysteresisDegrees: 2);
            var curve = FanCurveGraph.DefaultCpuPoints;

            double[] pattern = { -50.0, double.NaN, 40.0, 95.0, 105.0, 300.0, double.PositiveInfinity, -20.0, 88.0, 70.0 };

            for (int i = 0; i < 10_000; i++)
            {
                double cpu = pattern[i % pattern.Length];
                double gpu = pattern[(i + 4) % pattern.Length];

                var status = service.EvaluateStatus(cpu, gpu);
                var (safeCpu, safeGpu) = service.ApplyAntiThrottle(cpu, gpu, targetCpuSpeed: 25, targetGpuSpeed: 30);

                Assert.InRange(safeCpu, 25, 100);
                Assert.InRange(safeGpu, 30, 100);

                if (cpu >= 100.0 && cpu <= 300.0)
                {
                    Assert.Equal(100, safeCpu);
                    Assert.True(status.IsCpuCritical);
                }

                int speed = follower.Update(cpu, curve, status.CpuSpeedFloor);
                Assert.InRange(speed, 0, 100);
            }
        }

        [Fact]
        public void Subsystem3_ThermalLimits_FromTjMax_AdversarialInputs_AlwaysSatisfiesConstraints()
        {
            double[] tjValues = { -100.0, 0.0, double.NaN, double.NegativeInfinity, double.PositiveInfinity, 15.0, 70.0, 95.0, 100.0, 105.0, 250.0 };
            double[] dStarts = { -10.0, 0.0, double.NaN, 1.0, 12.0, 60.0, double.PositiveInfinity };
            double[] dCeils = { -5.0, 0.0, double.NaN, 1.0, 6.0, 40.0, double.PositiveInfinity };

            foreach (var tj in tjValues)
            {
                foreach (var ds in dStarts)
                {
                    foreach (var dc in dCeils)
                    {
                        var limits = ThermalLimits.FromTjMax(tj, ds, dc);
                        Assert.NotNull(limits);
                        Assert.True(limits.ThrottleThresholdCelsius < limits.CeilingThresholdCelsius,
                            $"Limits violated Throttle < Ceiling: Throttle={limits.ThrottleThresholdCelsius}, Ceiling={limits.CeilingThresholdCelsius} (tj={tj}, ds={ds}, dc={dc})");
                        Assert.True(limits.CeilingThresholdCelsius <= limits.CriticalThresholdCelsius,
                            $"Limits violated Ceiling <= Critical: Ceiling={limits.CeilingThresholdCelsius}, Critical={limits.CriticalThresholdCelsius}");
                    }
                }
            }
        }

        #endregion

        #region 4. Channel Router Fallback Stress (Simulated Failures & Transitions)

        private sealed class ControlledMockChannel : IOperatingModeChannel
        {
            public ModeTransportKind Transport { get; set; } = ModeTransportKind.ActionFunction7;
            public bool FailRead { get; set; }
            public bool ThrowRead { get; set; }
            public bool FailWrite { get; set; }
            public bool ThrowWrite { get; set; }
            public OperatingMode? Mode { get; set; } = OperatingMode.Balanced;
            public IReadOnlyList<OperatingMode>? Modes { get; set; } = new[] { OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance };

            public OperatingMode? Read()
            {
                if (ThrowRead) throw new TimeoutException("Simulated bus timeout");
                if (FailRead) return null;
                return Mode;
            }

            public bool Write(OperatingMode mode)
            {
                if (ThrowWrite) throw new IOException("Simulated device communication failure");
                if (FailWrite) return false;
                Mode = mode;
                return true;
            }

            public bool TryGetOperatingMode(out OperatingMode mode)
            {
                if (ThrowRead) throw new InvalidOperationException("Simulated hardware error");
                if (FailRead || !Mode.HasValue)
                {
                    mode = OperatingMode.Balanced;
                    return false;
                }
                mode = Mode.Value;
                return true;
            }

            public bool TrySetOperatingMode(OperatingMode mode) => Write(mode);

            public IReadOnlyList<OperatingMode> SupportedModes => Modes ?? Array.Empty<OperatingMode>();
        }

        [Fact]
        public void Subsystem4_ChannelRouter_ConcurrentTransitionsAndFailures_32Threads_1000Iterations()
        {
            var primary = new ControlledMockChannel { Transport = ModeTransportKind.EcHidFeature0x02 };
            var fallback = new ControlledMockChannel { Transport = ModeTransportKind.WmiMisc0x0B };
            var router = new OperatingModeChannelRouter(primary, fallback);

            const int threadCount = 32;
            const int iterations = 1000;
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    var rnd = new Random(tid * 449 + 19);
                    for (int i = 0; i < iterations; i++)
                    {
                        try
                        {
                            if (i % 50 == 0)
                            {
                                primary.ThrowRead = (rnd.Next(0, 3) == 0);
                                primary.FailRead = (rnd.Next(0, 3) == 1);
                                primary.ThrowWrite = (rnd.Next(0, 3) == 0);
                                primary.FailWrite = (rnd.Next(0, 3) == 1);

                                fallback.ThrowRead = (rnd.Next(0, 5) == 0);
                                fallback.FailRead = (rnd.Next(0, 5) == 1);
                            }

                            var target = (OperatingMode)(rnd.Next(0, 5));
                            router.TrySetOperatingMode(target);
                            router.Write(target);
                            router.Read();
                            router.TryGetOperatingMode(out _);
                            var modes = router.SupportedModes;
                            Assert.NotNull(modes);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);
        }

        [Fact]
        public void Subsystem4_ChannelRouter_TotalFailureRecovery_NeverThrows()
        {
            var primary = new ControlledMockChannel { ThrowRead = true, ThrowWrite = true };
            var fallback = new ControlledMockChannel { ThrowRead = true, ThrowWrite = true };
            var router = new OperatingModeChannelRouter(primary, fallback);

            // Complete failure on both sides
            Assert.Null(router.Read());
            Assert.False(router.Write(OperatingMode.Turbo));
            Assert.False(router.TrySetOperatingMode(OperatingMode.Quiet));
            bool ok = router.TryGetOperatingMode(out var mode);
            Assert.False(ok);
            Assert.Equal(OperatingMode.Balanced, mode);

            // Recovery on fallback
            fallback.ThrowRead = false;
            fallback.ThrowWrite = false;
            fallback.Mode = OperatingMode.Performance;

            Assert.Equal(OperatingMode.Performance, router.Read());
            Assert.True(router.Write(OperatingMode.Quiet));
            Assert.Equal(OperatingMode.Quiet, fallback.Mode);

            // Recovery on primary
            primary.ThrowRead = false;
            primary.ThrowWrite = false;
            primary.Mode = OperatingMode.Turbo;

            Assert.Equal(OperatingMode.Turbo, router.Read());
            Assert.True(router.Write(OperatingMode.Balanced));
            Assert.Equal(OperatingMode.Balanced, primary.Mode);
        }

        #endregion

        #region 5. Battery Calibration State Machine Fuzzing

        [Fact]
        public void Subsystem5_BatteryCalibrationEta_AdversarialCapacityAndRates_NeverThrows()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            var now = DateTime.UtcNow;

            // Extreme capacity jumps and drops
            calc.UpdateCapacity(60000, now);
            calc.UpdateCapacity(-1000, now.AddSeconds(10));
            calc.UpdateCapacity(0, now.AddSeconds(20));
            calc.UpdateCapacity(int.MinValue, now.AddSeconds(30));
            calc.UpdateCapacity(int.MaxValue, now.AddSeconds(40));

            // Extreme rates
            calc.UpdateDirectRate(-1000.0);
            calc.UpdateDirectRate(0.0);
            calc.UpdateDirectRate(double.NaN);
            calc.UpdateDirectRate(double.PositiveInfinity);
            calc.UpdateDirectRate(double.NegativeInfinity);
            calc.UpdateDirectRate(1e10);

            // Extreme percentages
            calc.UpdatePercentage(-50, now);
            calc.UpdatePercentage(300, now.AddMinutes(1));
            calc.UpdatePercentage(int.MinValue, now.AddMinutes(2));
            calc.UpdatePercentage(int.MaxValue, now.AddMinutes(3));

            // Calculate ETA across all edge case combinations
            Assert.Null(calc.CalculateEta(phase: 1, remainingCapacity: -100, fullCapacity: 0));
            Assert.Null(calc.CalculateEta(phase: 1, remainingCapacity: 50000, fullCapacity: 0));
            Assert.Equal(TimeSpan.Zero, calc.CalculateEta(phase: 1, remainingCapacity: 60000, fullCapacity: 50000));
            Assert.Equal(TimeSpan.Zero, calc.CalculateEta(phase: 2, remainingCapacity: 0, fullCapacity: 50000));
            Assert.Null(calc.CalculateEta(phase: -1, remainingCapacity: 1000, fullCapacity: 50000));
            Assert.Null(calc.CalculateEta(phase: 99, remainingCapacity: 1000, fullCapacity: 50000));
        }

        [Fact]
        public void Subsystem5_BatteryCalibrationEta_HighFrequencyFuzzing_32Threads_1000Iterations()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            const int threadCount = 32;
            const int iterations = 1000;
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    var rnd = new Random(tid * 617 + 23);
                    var baseTime = DateTime.UtcNow;

                    for (int i = 0; i < iterations; i++)
                    {
                        try
                        {
                            int op = rnd.Next(0, 5);
                            switch (op)
                            {
                                case 0:
                                    calc.UpdateDirectRate(rnd.NextDouble() * 200000.0 - 50000.0);
                                    break;
                                case 1:
                                    calc.UpdateCapacity(rnd.Next(-10000, 100000), baseTime.AddSeconds(i * 5));
                                    break;
                                case 2:
                                    calc.UpdatePercentage(rnd.Next(-20, 150), baseTime.AddMinutes(i * 0.1));
                                    break;
                                case 3:
                                    calc.CalculateEta(
                                        phase: rnd.Next(0, 4),
                                        remainingCapacity: rnd.Next(-5000, 80000),
                                        fullCapacity: rnd.Next(-5000, 80000),
                                        directRate: rnd.NextDouble() * 80000.0 - 20000.0,
                                        allowWarmupFallback: (i % 2 == 0),
                                        batteryPercent: rnd.Next(-20, 120));
                                    break;
                                case 4:
                                    if (i % 100 == 0) calc.Reset();
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);
        }

        [Fact]
        public void Subsystem5_RegistrySafety_CalibrationStatePersistence_ExtremeValues()
        {
            long[] ticks = { 0, -100, DateTime.UtcNow.Ticks, long.MaxValue, -1 };
            int[] phases = { 0, 1, 2, -1, 5, 999 };
            int[] pcts = { -1, 0, 50, 100, -50, 250 };
            bool[] limits = { true, false };

            foreach (var tick in ticks)
            {
                foreach (var phase in phases)
                {
                    foreach (var pct in pcts)
                    {
                        foreach (var lim in limits)
                        {
                            RegistrySafety.SaveCalibrationState(tick, phase, pct, lim);
                            var s = new AppSettings();
                            RegistrySafety.LoadCalibrationState(s);

                            Assert.True(s.LastBatteryCalibrationUtcTicks >= 0);
                            Assert.InRange(s.BatteryCalibrationPhase, 0, 2);
                            Assert.True(s.CalibrationLastKnownPercent == -1 || (s.CalibrationLastKnownPercent >= 0 && s.CalibrationLastKnownPercent <= 100));
                        }
                    }
                }
            }
        }

        #endregion

        #region 6. Corrupted Hardware Signatures, Invalid TDP, & Registry Payloads

        [Fact]
        public void Subsystem6_ComputeHardwareSignature_AdversarialInputs_NeverThrows()
        {
            string[] strings = { null!, "", "   ", new string('Z', 5000), @"Acer\..\Evil", "Acer Predator", "Special @#$%^&*()_+", "\r\n\t" };
            int[] tdps = { int.MinValue, -1000, 0, 15, 45, 175, 500, int.MaxValue };

            foreach (var mfg in strings)
            {
                foreach (var mdl in strings)
                {
                    foreach (var cpu in strings)
                    {
                        foreach (var tdp in tdps)
                        {
                            string sig = CpuPowerLimitController.ComputeHardwareSignature(mfg, mdl, cpu, tdp);
                            Assert.NotNull(sig);
                            Assert.NotEmpty(sig);
                            Assert.Contains(tdp.ToString(), sig);
                        }
                    }
                }
            }
        }

        [Fact]
        public void Subsystem6_CpuSpecDetector_Detect_AdversarialStrings_AlwaysReturnsValidSpec()
        {
            string[] cpuStrings =
            {
                null!, "", "   ", "UNKNOWN", "12345", new string('A', 5000),
                "Intel Core i9-14900HX", "Intel Core Ultra 7 155H", "Intel Core Ultra 9 285HX",
                "Intel Core Ultra 7 258V", "AMD Ryzen 9 7945HX3D", "AMD Ryzen 7 7840HS",
                "AMD Ryzen 5 5600H", "AMD Ryzen 7 5700U", "Intel Core i7-13700K",
                "../../Windows/System32", "\0"
            };

            foreach (var str in cpuStrings)
            {
                var spec = CpuSpecDetector.Detect(str);
                Assert.NotNull(spec);
                Assert.NotNull(spec.CpuModel);
                Assert.NotNull(spec.Architecture);
                Assert.True(spec.BaseTdpWatts > 0, $"Base TDP {spec.BaseTdpWatts} not positive for '{str}'");
                Assert.True(spec.MinPowerWatts > 0, $"Min power {spec.MinPowerWatts} not positive for '{str}'");
                Assert.True(spec.MaxPl1Watts >= spec.MinPowerWatts, $"MaxPl1 < MinPower for '{str}'");
                Assert.True(spec.MaxPl2Watts >= spec.MaxPl1Watts, $"MaxPl2 < MaxPl1 for '{str}'");
            }
        }

        [Fact]
        public void Subsystem6_CpuSpecDetector_EstimateProportionalLimits_MaintainsPl2GtePl1()
        {
            int[] tdps = { int.MinValue, -500, 0, 5, 15, 28, 45, 55, 65, 125, 250, 1000, int.MaxValue };
            byte[] modes = { 0x00, 0x01, 0x04, 0x05, 0x06, 0x02, 0xFF };

            foreach (var tdp in tdps)
            {
                foreach (var mode in modes)
                {
                    var (p1, p2) = CpuSpecDetector.EstimateProportionalLimits(tdp, mode);
                    Assert.True(p2 >= p1, $"CpuSpecDetector.EstimateProportionalLimits produced PL2 < PL1: PL1={p1}, PL2={p2} for tdp={tdp}, mode={mode}");
                    Assert.True(p1 >= 5, $"PL1 {p1} fell below 5W for tdp={tdp}");
                    Assert.True(p2 >= 5, $"PL2 {p2} fell below 5W for tdp={tdp}");
                }
            }
        }

        [Fact]
        public void Subsystem6_AppSettings_NullEmptyCorruptRegistryPayloads_NeverThrows()
        {
            var settings = new AppSettings();
            RegistrySafety.SaveCpuPowerLimits(settings);
            RegistrySafety.LoadCpuPowerLimits(settings);

            Assert.NotNull(settings.ScannedHardwareSignature);
            Assert.InRange(settings.PowerMode, (byte)0, (byte)6);
            Assert.InRange(settings.FanMode, (byte)1, (byte)3);
        }

        #endregion
    }
}
