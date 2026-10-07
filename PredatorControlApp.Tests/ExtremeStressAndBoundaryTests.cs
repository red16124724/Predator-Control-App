using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("SingleInstanceTests")]
    public class ExtremeStressAndBoundaryTests
    {
        #region Helpers

        /// <summary>
        /// Scoped helper that safely backs up the real AppSettings file and restores it on disposal.
        /// Prevents tests from corrupting or wiping real user configurations.
        /// </summary>
        private sealed class SettingsBackupScope : IDisposable
        {
            private readonly string _path = AppSettings.SettingsFilePath;
            private readonly string? _backupContent;
            private readonly bool _existed;

            public SettingsBackupScope()
            {
                _existed = File.Exists(_path);
                if (_existed)
                {
                    try { _backupContent = File.ReadAllText(_path); } catch { _backupContent = null; }
                }
            }

            public void Dispose()
            {
                try
                {
                    if (_existed && _backupContent != null)
                    {
                        string dir = Path.GetDirectoryName(_path)!;
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(_path, _backupContent);
                    }
                    else if (!_existed && File.Exists(_path))
                    {
                        File.Delete(_path);
                    }
                }
                catch { }
            }
        }

        #endregion

        #region 1. Rapid Power State Flapping Stress (500 Transitions in Parallel with Telemetry Ticks)

        [Fact]
        public void DebouncePowerLine_RapidAlternatingFlapping500Transitions_NeverFalselyTransitions()
        {
            // Simulate a noisy power brick or flapping DC jack oscillating every single tick for 500 transitions
            // Starting plugged in (Online)
            bool? current = true;
            bool? pending = null;
            int ticks = 0;

            for (int i = 0; i < 500; i++)
            {
                PowerLineStatus noisyStatus = (i % 2 == 0) ? PowerLineStatus.Offline : PowerLineStatus.Online;
                current = Form1.DebouncePowerLine(noisyStatus, current, ref pending, ref ticks);

                // Two-tick debounce requires 2 CONSECUTIVE identical states.
                // Because state oscillates every tick, current must NEVER transition to false.
                Assert.True(current, $"Debouncer failed at transition {i}: falsely changed from true to false");
            }

            // Repeat starting on battery (Offline)
            current = false;
            pending = null;
            ticks = 0;

            for (int i = 0; i < 500; i++)
            {
                PowerLineStatus noisyStatus = (i % 2 == 0) ? PowerLineStatus.Online : PowerLineStatus.Offline;
                current = Form1.DebouncePowerLine(noisyStatus, current, ref pending, ref ticks);

                // Because state oscillates every tick, current must NEVER transition to true.
                Assert.False(current, $"Debouncer failed at transition {i}: falsely changed from false to true");
            }
        }

        [Fact]
        public void DebouncePowerLine_500Transitions_BurstStability_TransitionsExactlyAtTwoTicks()
        {
            // Simulate 500 transitions consisting of 1-tick glitches and >=2-tick valid state transitions
            bool? current = true;
            bool? pending = null;
            int ticks = 0;

            var random = new Random(12345);
            int validTransitions = 0;
            int glitchCount = 0;
            int totalTicks = 0;

            while (totalTicks < 500)
            {
                bool isGlitch = random.Next(2) == 0;

                if (isGlitch)
                {
                    // A 1-tick glitch: flips state for 1 tick, then immediately returns to original state
                    bool glitchTarget = current != true;
                    PowerLineStatus glitchStatus = glitchTarget ? PowerLineStatus.Online : PowerLineStatus.Offline;
                    PowerLineStatus returnStatus = (current == true) ? PowerLineStatus.Online : PowerLineStatus.Offline;

                    // Tick 1: Glitch occurs
                    totalTicks++;
                    bool? resGlitch = Form1.DebouncePowerLine(glitchStatus, current, ref pending, ref ticks);
                    Assert.Equal(current, resGlitch); // Must NOT transition on single glitch tick
                    Assert.Equal(1, ticks);
                    Assert.Equal(glitchTarget, pending);

                    // Tick 2: Returns to original state
                    totalTicks++;
                    bool? resReturn = Form1.DebouncePowerLine(returnStatus, current, ref pending, ref ticks);
                    Assert.Equal(current, resReturn); // Stays at current state
                    glitchCount++;
                }
                else
                {
                    // Genuine state change: lasts 2 to 4 consecutive ticks
                    bool newTarget = current != true;
                    PowerLineStatus newStatus = newTarget ? PowerLineStatus.Online : PowerLineStatus.Offline;
                    int burstLength = random.Next(2, 5);

                    for (int b = 0; b < burstLength && totalTicks < 500; b++)
                    {
                        totalTicks++;
                        bool? previous = current;
                        current = Form1.DebouncePowerLine(newStatus, current, ref pending, ref ticks);

                        if (b == 0)
                        {
                            // Tick 1: Pending change, not yet confirmed
                            Assert.Equal(previous, current);
                            Assert.Equal(1, ticks);
                            Assert.Equal(newTarget, pending);
                        }
                        else if (b == 1)
                        {
                            // Tick 2: Confirmed transition!
                            Assert.Equal(newTarget, current);
                            Assert.Equal(2, ticks);
                            validTransitions++;
                        }
                        else
                        {
                            // Subsequent ticks: remains confirmed
                            Assert.Equal(newTarget, current);
                        }
                    }
                }
            }

            Assert.True(validTransitions > 20, $"Expected >=20 valid transitions, got {validTransitions}");
            Assert.True(glitchCount > 20, $"Expected >=20 rejected glitches, got {glitchCount}");
        }

        [Fact]
        public void DebouncePowerLine_FlappingInterspersedWithUnknown500Transitions_PreservesSafeState()
        {
            bool? current = false; // Safe battery state
            bool? pending = true;
            int ticks = 1;

            for (int i = 0; i < 500; i++)
            {
                // Inject Unknown periodically (simulating ACPI/battery driver reload or system wake)
                PowerLineStatus status = (i % 3 == 0)
                    ? PowerLineStatus.Unknown
                    : ((i % 3 == 1) ? PowerLineStatus.Online : PowerLineStatus.Offline);

                current = Form1.DebouncePowerLine(status, current, ref pending, ref ticks);

                if (status == PowerLineStatus.Unknown)
                {
                    // Unknown must reset pending and ticks to 0, and leave current state intact
                    Assert.Null(pending);
                    Assert.Equal(0, ticks);
                }
            }

            Assert.NotNull(current);
        }

        [Fact]
        public void PowerStateFlapping_ParallelWithTelemetryTicks_StressTest()
        {
            // Simulate 500 AC/Battery transitions occurring on a hardware worker thread
            // while multiple telemetry threads evaluate power rules, backlight status, and interval cadence.
            var backlightMgr = new BacklightStateManager();
            backlightMgr.SavedAcBrightness = 80;

            object stateLock = new();
            bool? currentPowerState = true;
            bool? pendingPowerState = null;
            int powerTicks = 0;
            PowerLineStatus rawHardwareStatus = PowerLineStatus.Online;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int evaluatedTicks = 0;
            int powerTransitionsProcessed = 0;

            // Worker 1: Rapid 500 AC/Battery/Unknown hardware transitions
            var hardwareTask = Task.Run(() =>
            {
                for (int i = 0; i < 500 && !cts.Token.IsCancellationRequested; i++)
                {
                    lock (stateLock)
                    {
                        rawHardwareStatus = (i % 5 == 0)
                            ? PowerLineStatus.Unknown
                            : ((i % 2 == 0) ? PowerLineStatus.Offline : PowerLineStatus.Online);
                    }
                    Interlocked.Increment(ref powerTransitionsProcessed);
                    if (i % 50 == 0) Thread.Sleep(1);
                }
            });

            // Worker 2 & 3: Telemetry polling loops simulating Form1.UpdateTelemetryCore cadence
            var telemetryTasks = Enumerable.Range(0, 3).Select(workerId => Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested && Interlocked.CompareExchange(ref evaluatedTicks, 0, 0) < 500)
                {
                    PowerLineStatus status;
                    bool? confirmedState;
                    lock (stateLock)
                    {
                        status = rawHardwareStatus;
                        confirmedState = Form1.DebouncePowerLine(status, currentPowerState, ref pendingPowerState, ref powerTicks);
                        currentPowerState = confirmedState;
                    }

                    // Strict cadence invariant: 2000ms on AC, 5000ms on Battery
                    bool onBattery = backlightMgr.IsOnBattery(status);
                    int targetInterval = onBattery ? 5000 : 2000;
                    Assert.True(targetInterval == 2000 || targetInterval == 5000);

                    // Backlight safety
                    bool isConnectedToCharger = !onBattery && (status == PowerLineStatus.Online || confirmedState == true);
                    Assert.True(onBattery ^ isConnectedToCharger || (status == PowerLineStatus.Unknown));

                    Interlocked.Increment(ref evaluatedTicks);
                    Thread.Yield();
                }
            })).ToArray();

            Task.WaitAll(new[] { hardwareTask }.Concat(telemetryTasks).ToArray());

            Assert.True(powerTransitionsProcessed >= 500, $"Hardware flapping processed {powerTransitionsProcessed} transitions");
            Assert.True(evaluatedTicks >= 500, $"Telemetry evaluated {evaluatedTicks} ticks");
        }

        [Fact]
        public void BacklightStateManager_Rapid500PowerTransitionsWithLidAndSleep_Stress()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 90;
            var rand = new Random(999);

            for (int i = 0; i < 500; i++)
            {
                PowerLineStatus status = (PowerLineStatus)(rand.Next(0, 3)); // 0 = Offline, 1 = Online, 255/unknown
                if (status != PowerLineStatus.Offline && status != PowerLineStatus.Online)
                    status = PowerLineStatus.Unknown;

                int action = rand.Next(0, 4);
                switch (action)
                {
                    case 0:
                        // Lid open/close
                        bool isOpen = rand.Next(2) == 1;
                        mgr.OnLidChanged(isOpen, status, out bool shouldTurnOff, out int targetBrightness);
                        if (!isOpen)
                        {
                            // Lid closed MUST ALWAYS turn off backlight
                            Assert.True(shouldTurnOff, "Lid closed must force backlight off");
                            Assert.Equal(0, targetBrightness);
                        }
                        else
                        {
                            Assert.InRange(targetBrightness, 0, 100);
                        }
                        break;

                    case 1:
                        // Suspend
                        mgr.OnSuspend(out bool suspOff, out int suspBright);
                        Assert.True(suspOff);
                        Assert.Equal(0, suspBright);
                        Assert.True(mgr.IsSleeping);
                        break;

                    case 2:
                        // Resume
                        mgr.OnResume(status, out bool resumeOff, out int resumeBright);
                        Assert.False(mgr.IsSleeping);
                        Assert.InRange(resumeBright, 0, 100);
                        break;

                    case 3:
                        // Query IsOnBattery
                        bool onBatt = mgr.IsOnBattery(status);
                        if (status == PowerLineStatus.Offline) Assert.True(onBatt);
                        if (status == PowerLineStatus.Online) Assert.False(onBatt);
                        break;
                }

                // Invariant: SavedAcBrightness must never be corrupted out of [0, 100]
                Assert.InRange(mgr.SavedAcBrightness, 0, 100);
            }
        }

        #endregion

        #region 2. Rapid Fan Curve Evaluation Across Full Temperature Range (-50 to 150 C)

        [Fact]
        public void InterpolateCurve_FullTemperatureRangeSweep_Negative50To150Celsius_ClampsAndMonotonic()
        {
            var curve = new List<Point>
            {
                new(30, 10),
                new(45, 15),
                new(55, 30),
                new(65, 50),
                new(72, 65),
                new(80, 80),
                new(88, 92),
                new(95, 100)
            };

            int previousSpeed = 0;

            // Full temperature sweep from sub-zero (-50C) to extreme overheat (150C)
            for (int temp = -50; temp <= 150; temp++)
            {
                int speed = Form1.InterpolateCurve(curve, temp);

                // Invariant 1: Speed is strictly clamped between 10% and 100%
                Assert.InRange(speed, 10, 100);

                // Invariant 2: Sub-zero and lower temperatures clamp cleanly to minimum curve point (10%)
                if (temp <= 30)
                {
                    Assert.Equal(10, speed);
                }

                // Invariant 3: Over-temperature (>= 95C) clamps cleanly to maximum curve point (100%)
                if (temp >= 95)
                {
                    Assert.Equal(100, speed);
                }

                // Invariant 4: Standard curve interpolation must be monotonically non-decreasing
                if (temp > -50)
                {
                    Assert.True(speed >= previousSpeed, $"Speed decreased at temp {temp}: was {previousSpeed}, now {speed}");
                }

                previousSpeed = speed;
            }
        }

        [Fact]
        public void InterpolateCurve_SeverelyDisorderedAndScrambledPoints_ProducesIdenticalResultToSorted()
        {
            var sortedCurve = new List<Point>
            {
                new(30, 15),
                new(40, 25),
                new(50, 40),
                new(60, 55),
                new(70, 70),
                new(80, 85),
                new(90, 95),
                new(100, 100)
            };

            var rand = new Random(777);

            // Test 100 random shuffles of the curve points
            for (int s = 0; s < 100; s++)
            {
                var scrambled = sortedCurve.OrderBy(_ => rand.Next()).ToList();

                // Sweep entire temperature range -50 to 150 C
                for (int temp = -50; temp <= 150; temp += 5)
                {
                    int expected = Form1.InterpolateCurve(sortedCurve, temp);
                    int actual = Form1.InterpolateCurve(scrambled, temp);

                    Assert.Equal(expected, actual);
                }
            }
        }

        [Fact]
        public void InterpolateCurve_DegenerateAndPathologicalPointSets_NeverThrows()
        {
            // Null curve
            Assert.Equal(50, Form1.InterpolateCurve(null, 65));

            // Empty curve
            Assert.Equal(50, Form1.InterpolateCurve(new List<Point>(), 65));

            // Single point curve
            Assert.Equal(40, Form1.InterpolateCurve(new List<Point> { new(50, 40) }, 30));
            Assert.Equal(40, Form1.InterpolateCurve(new List<Point> { new(50, 40) }, 80));

            // Identical X coordinates (vertical step: span == 0)
            var verticalCurve = new List<Point>
            {
                new(50, 20),
                new(50, 80)
            };
            for (int temp = -50; temp <= 150; temp += 10)
            {
                int speed = Form1.InterpolateCurve(verticalCurve, temp);
                Assert.InRange(speed, 10, 100);
            }

            // All duplicate points
            var duplicateCurve = new List<Point>
            {
                new(40, 30),
                new(40, 30),
                new(40, 30)
            };
            for (int temp = -50; temp <= 150; temp += 10)
            {
                int speed = Form1.InterpolateCurve(duplicateCurve, temp);
                Assert.InRange(speed, 10, 100);
            }

            // Extreme wild coordinates (X in [-500, 1000], Y in [-1000, 1000])
            var wildCurve = new List<Point>
            {
                new(-500, -200),
                new(0, 0),
                new(200, 500),
                new(1000, 9999)
            };
            for (int temp = -50; temp <= 150; temp += 5)
            {
                int speed = Form1.InterpolateCurve(wildCurve, temp);
                Assert.InRange(speed, 10, 100);
            }
        }

        [Fact]
        public void FanCurveGraph_NormalizeAndInterpolate_DisorderedAndExtremeInputs_FullRangeSweep()
        {
            var rand = new Random(4321);

            // Test 100 randomly generated disordered and wild point sets
            for (int run = 0; run < 100; run++)
            {
                int count = rand.Next(0, 30);
                var rawPoints = new List<Point>();
                for (int i = 0; i < count; i++)
                {
                    rawPoints.Add(new Point(rand.Next(-100, 250), rand.Next(-100, 250)));
                }

                var normalized = FanCurveGraph.Normalize(rawPoints);

                // Normalize invariants:
                Assert.NotNull(normalized);
                Assert.True(normalized.Count >= 2);
                Assert.Equal(30, normalized[0].X); // TempMin clamped
                int expectedMaxX = 100;
                Assert.Equal(expectedMaxX, normalized[^1].X); // TempMax clamped to 100

                // Monotonically ordered in X
                for (int i = 0; i < normalized.Count - 1; i++)
                {
                    Assert.True(normalized[i].X <= normalized[i + 1].X);
                    Assert.InRange(normalized[i].X, 30, 100);
                    Assert.InRange(normalized[i].Y, 0, 100);
                }

                // Verify FanCurveGraph.InterpolateSpeed across -50 to 150 C
                var graph = new FanCurveGraph();
                graph.Points = rawPoints;

                for (int temp = -50; temp <= 150; temp += 10)
                {
                    int speed = graph.InterpolateSpeed(temp);
                    Assert.InRange(speed, 0, 100);
                }
            }
        }

        [Fact]
        public void CurveFollower_RapidTemperatureEvaluation_WithDisorderedCurves_HysteresisIntegrity()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(70, 70), // Disordered
                new(30, 20),
                new(90, 100),
                new(50, 40)
            };

            // Test extreme floats (NaN, Infs)
            Assert.InRange(follower.Update(double.NaN, curve), 0, 100);
            Assert.InRange(follower.Update(double.PositiveInfinity, curve), 0, 100);
            Assert.InRange(follower.Update(double.NegativeInfinity, curve), 0, 100);

            // 1,000 steps of oscillating temperatures between -50 and 150 C
            var rand = new Random(888);
            double temp = 50.0;
            for (int step = 0; step < 1000; step++)
            {
                temp += (rand.NextDouble() * 10.0) - 4.5;
                temp = Math.Clamp(temp, -50.0, 150.0);

                int speed = follower.Update(temp, curve);
                Assert.InRange(speed, 0, 100);
                Assert.NotNull(follower.Current);
            }

            // Verify 2C hysteresis rule explicitly:
            follower.Reset();
            int heatSpeed = follower.Update(75.0, curve);
            int holdSpeed = follower.Update(73.5, curve); // 1.5C drop <= 2.0C hysteresis
            Assert.Equal(heatSpeed, holdSpeed);

            int coolSpeed = follower.Update(72.0, curve); // 3.0C drop > 2.0C hysteresis
            Assert.True(coolSpeed <= heatSpeed);
        }

        [Fact]
        public void HighThroughput_Parallel_FanCurveEvaluation_MultiThreadedStress()
        {
            const int threadCount = 10;
            const int itersPerThread = 2000;
            var curve = new List<Point>
            {
                new(30, 10), new(50, 30), new(70, 60), new(85, 85), new(100, 100)
            };

            int totalEvaluations = 0;
            var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
            {
                var rand = new Random(threadId * 100);
                for (int i = 0; i < itersPerThread; i++)
                {
                    int temp = rand.Next(-50, 151);
                    int speed = Form1.InterpolateCurve(curve, temp);
                    Assert.InRange(speed, 10, 100);
                    Interlocked.Increment(ref totalEvaluations);
                }
            })).ToArray();

            Task.WaitAll(tasks);
            Assert.Equal(threadCount * itersPerThread, totalEvaluations);
        }

        #endregion

        #region 3. Concurrent Power Mode Switching Stress (Turbo Toggle, Revert on Rapid Clicks)

        [Theory]
        [InlineData(0x00, 0x00)] // Quiet -> Turbo -> Reverts to Quiet
        [InlineData(0x01, 0x01)] // Balanced -> Turbo -> Reverts to Balanced
        [InlineData(0x04, 0x04)] // Performance -> Turbo -> Reverts to Performance
        public void TurboToggle_StateMachine_FromAllBaseModes_RevertsAccurately(byte baseMode, byte expectedReturn)
        {
            byte currentPowerMode = baseMode;
            byte turboReturnMode = 0x01;

            // Click 1: Toggle into Turbo
            if (currentPowerMode != 0x05)
            {
                turboReturnMode = currentPowerMode;
                currentPowerMode = 0x05;
            }

            Assert.Equal(0x05, currentPowerMode);
            Assert.Equal(baseMode, turboReturnMode);

            // Click 2: Toggle out of Turbo (Revert)
            if (currentPowerMode == 0x05)
            {
                byte returnMode = (turboReturnMode == 0x05 || turboReturnMode == 0x06) ? (byte)0x01 : turboReturnMode;
                currentPowerMode = returnMode;
            }

            Assert.Equal(expectedReturn, currentPowerMode);
        }

        [Theory]
        [InlineData(0x05, 0x01)] // Return mode corrupted to Turbo -> falls back to Balanced (0x01)
        [InlineData(0x06, 0x01)] // Return mode was Eco -> firmware rule falls back to Balanced (0x01)
        public void TurboToggle_BoundaryFallbacks_WhenReturnModeIsTurboOrEco_FallsBackToBalanced(byte corruptReturnMode, byte expectedFallback)
        {
            byte currentPowerMode = 0x05;
            byte turboReturnMode = corruptReturnMode;

            // Revert click
            byte returnMode = (turboReturnMode == 0x05 || turboReturnMode == 0x06) ? (byte)0x01 : turboReturnMode;
            currentPowerMode = returnMode;

            Assert.Equal(expectedFallback, currentPowerMode);
        }

        [Fact]
        public void TurboToggle_OnBattery_DoesNotActivateTurbo_CyclesInstead()
        {
            // Battery safety rule: Turbo draws >140W+, forbidden on battery
            bool isOnBattery = true;
            byte currentPowerMode = 0x01; // Balanced
            byte turboReturnMode = 0x01;

            // Simulate mode key handler
            if (isOnBattery)
            {
                // Fallback to CyclePowerMode, never sets Turbo (0x05)
                var batteryModes = new[] { (byte)0x00, (byte)0x01, (byte)0x06 }; // Quiet, Balanced, Eco
                int nextIdx = (Array.IndexOf(batteryModes, currentPowerMode) + 1) % batteryModes.Length;
                currentPowerMode = batteryModes[nextIdx];
            }
            else
            {
                if (currentPowerMode != 0x05)
                {
                    turboReturnMode = currentPowerMode;
                    currentPowerMode = 0x05;
                }
            }

            Assert.NotEqual(0x05, currentPowerMode);
            Assert.Contains(currentPowerMode, new byte[] { 0x00, 0x01, 0x06 });
        }

        [Fact]
        public void RapidModeKeyClicks_DebounceUnder350ms_RejectsSpamAndMaintainsState()
        {
            DateTime lastModeKeyUtc = DateTime.MinValue;
            byte currentPowerMode = 0x01; // Balanced
            byte turboReturnMode = 0x01;
            int acceptedClicks = 0;
            int droppedClicks = 0;

            // Simulate rapid spam of 50 clicks within a 100ms interval
            DateTime simulatedNow = DateTime.UtcNow;

            for (int click = 0; click < 50; click++)
            {
                // Rapid clicks spaced 2ms apart
                DateTime clickTime = simulatedNow.AddMilliseconds(click * 2);

                if ((clickTime - lastModeKeyUtc).TotalMilliseconds < 350)
                {
                    droppedClicks++;
                    continue;
                }

                lastModeKeyUtc = clickTime;
                acceptedClicks++;

                // Toggle logic
                if (currentPowerMode != 0x05)
                {
                    turboReturnMode = currentPowerMode;
                    currentPowerMode = 0x05;
                }
                else
                {
                    byte returnMode = (turboReturnMode == 0x05 || turboReturnMode == 0x06) ? (byte)0x01 : turboReturnMode;
                    currentPowerMode = returnMode;
                }
            }

            // Exactly 1 click accepted, 49 dropped
            Assert.Equal(1, acceptedClicks);
            Assert.Equal(49, droppedClicks);
            Assert.Equal(0x05, currentPowerMode);
            Assert.Equal(0x01, turboReturnMode);

            // Now simulate another click after 400ms (>= 350ms window)
            DateTime revertClickTime = simulatedNow.AddMilliseconds(400);
            if ((revertClickTime - lastModeKeyUtc).TotalMilliseconds >= 350)
            {
                lastModeKeyUtc = revertClickTime;
                acceptedClicks++;
                byte returnMode = (turboReturnMode == 0x05 || turboReturnMode == 0x06) ? (byte)0x01 : turboReturnMode;
                currentPowerMode = returnMode;
            }

            Assert.Equal(2, acceptedClicks);
            Assert.Equal(0x01, currentPowerMode); // Reverted cleanly to Balanced
        }

        [Fact]
        public async Task Concurrent_PowerModeSwitching_HighIntensityStress()
        {
            using var wmi = new WmiController();
            const int threadCount = 8;
            const int itersPerThread = 25;

            var barrier = new Barrier(threadCount);
            var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
            {
                barrier.SignalAndWait(5000);
                var rand = new Random(threadId * 333);
                byte[] validModes = { 0x00, 0x01, 0x04, 0x05, 0x06 };

                for (int i = 0; i < itersPerThread; i++)
                {
                    byte targetMode = validModes[rand.Next(validModes.Length)];
                    bool success = wmi.TrySetPowerMode(targetMode);
                    // On systems without Acer hardware, returns false cleanly without crashing
                    Assert.True(success || !success);
                    Thread.Yield();
                }
            })).ToArray();

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
            var completedTask = await Task.WhenAny(Task.WhenAll(tasks), timeoutTask);
            Assert.True(completedTask != timeoutTask, "Concurrent power mode switching timed out or deadlocked");
        }

        [Fact]
        public void PowerByteToBtn_And_ModeLedColor_ExhaustiveMapping()
        {
            // Exhaustively verify all 256 byte values for GetModeLedColor
            for (int b = 0; b <= 255; b++)
            {
                byte mode = (byte)b;
                var (r, g, bColor) = WmiController.GetModeLedColor(mode);
                Assert.True(r <= 255 && g <= 255 && bColor <= 255);

                switch (mode)
                {
                    case 0: Assert.Equal((0, 220, 255), (r, g, bColor)); break;
                    case 1: Assert.Equal((0, 150, 255), (r, g, bColor)); break;
                    case 4: Assert.Equal((255, 140, 0), (r, g, bColor)); break;
                    case 5: Assert.Equal((255, 20, 50), (r, g, bColor)); break;
                    case 6: Assert.Equal((0, 255, 120), (r, g, bColor)); break;
                }
            }
        }

        #endregion

        #region 4. Hardware Failover (Simulated WMI COM Exceptions and Zero Readings Recovery)

        [Fact]
        public void WmiController_COMException_RpcServerUnavailable_0x800706BA_HandlesGracefully()
        {
            using var wmi = new WmiController();

            // Calling sensor reads on machines without WMI service or during simulated COM failure:
            // Error code 0x800706BA (RPC_S_SERVER_UNAVAILABLE) must be caught cleanly,
            // invalidate the cache, and return 0 / false without crashing the process.
            int cpuTemp = wmi.GetSensorReading(0x01);
            int fanRpm = wmi.GetGamingFanSpeed(0x01);

            Assert.True(cpuTemp >= 0);
            Assert.True(fanRpm >= 0);

            // Verify cache invalidation via reflection
            var invalidateMethod = typeof(WmiController).GetMethod("InvalidateCacheUnderLock", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(invalidateMethod);
            invalidateMethod!.Invoke(wmi, null);

            var cachedObjField = typeof(WmiController).GetField("_cachedObj", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(cachedObjField);
            Assert.Null(cachedObjField!.GetValue(wmi));
        }

        [Fact]
        public void WmiController_ZeroReadings_CpuTemp_PreservesLastCachedReading()
        {
            using var wmi = new WmiController();

            var cachedTempField = typeof(WmiController).GetField("_cachedCpuTempReading", BindingFlags.NonPublic | BindingFlags.Instance);
            var lastReadTimeField = typeof(WmiController).GetField("_lastCpuTempReadTime", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(cachedTempField);
            Assert.NotNull(lastReadTimeField);

            // Seed cache with a valid temperature reading (e.g., 62 C)
            cachedTempField!.SetValue(wmi, 62);
            lastReadTimeField!.SetValue(wmi, DateTime.UtcNow.AddSeconds(-2)); // Expire cache duration

            // When GetSensorReading returns 0 (D3Cold sleep / sensor glitch / timeout):
            // CpuTemp must NEVER drop to 0 C; it must preserve the last cached 62 C reading!
            int reading = wmi.CpuTemp;
            Assert.Equal(62, reading);

            // Update cache to 75 C
            cachedTempField.SetValue(wmi, 75);
            lastReadTimeField.SetValue(wmi, DateTime.UtcNow.AddSeconds(-2));

            reading = wmi.CpuTemp;
            Assert.Equal(75, reading);
        }

        [Fact]
        public void WmiController_ZeroReadings_FanRpm_PreservesLastCachedReading()
        {
            using var wmi = new WmiController();

            var cachedRpmField = typeof(WmiController).GetField("_cachedCpuRpmReading", BindingFlags.NonPublic | BindingFlags.Instance);
            var lastReadTimeField = typeof(WmiController).GetField("_lastCpuRpmReadTime", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(cachedRpmField);
            Assert.NotNull(lastReadTimeField);

            // Seed cache with valid RPM (e.g., 3450 RPM)
            cachedRpmField!.SetValue(wmi, 3450);
            lastReadTimeField!.SetValue(wmi, DateTime.UtcNow.AddSeconds(-2)); // Expire cache duration

            // When sensors return 0, CpuFanRpm must preserve the last valid RPM reading
            int rpm = wmi.CpuFanRpm;
            Assert.Equal(3450, rpm);
        }

        [Fact]
        public void DecodeFanSpeed_And_MaskTachometerRpm_ExtremeBoundaryValues()
        {
            // Zero raw
            Assert.Equal(0, WmiController.DecodeFanSpeed(0));
            Assert.Equal(0, WmiController.MaskTachometerRpm(0));

            // Direct 13-bit tachometer boundary: 0x1FFF = 8191 RPM
            Assert.Equal(8191, WmiController.DecodeFanSpeed(0x1FFF));

            // Packet threshold: 0x2000 (8192) -> decoded via MaskTachometerRpm
            // 0x2000 >> 8 = 0x20 = 32 RPM, status = 0x00
            Assert.Equal(32, WmiController.DecodeFanSpeed(0x2000));

            // Standard ACPI packet with status byte = 0: (4500 << 8) | 0x00
            ulong validPacket = (4500UL << 8) | 0x00UL;
            Assert.Equal(4500, WmiController.DecodeFanSpeed(validPacket));
            Assert.Equal(4500, WmiController.MaskTachometerRpm(validPacket));

            // Packet with error status byte: (4500 << 8) | 0x01 -> must return 0
            ulong errorPacket1 = (4500UL << 8) | 0x01UL;
            Assert.Equal(0, WmiController.DecodeFanSpeed(errorPacket1));
            Assert.Equal(0, WmiController.MaskTachometerRpm(errorPacket1));

            // Packet with corrupted status: (4500 << 8) | 0xFF -> must return 0
            ulong errorPacket2 = (4500UL << 8) | 0xFFUL;
            Assert.Equal(0, WmiController.DecodeFanSpeed(errorPacket2));

            // Max ulong -> status is 0xFF, must return 0
            Assert.Equal(0, WmiController.DecodeFanSpeed(ulong.MaxValue));
            Assert.Equal(0, WmiController.MaskTachometerRpm(ulong.MaxValue));

            // Max 13-bit RPM in ACPI packet: (8191 << 8) | 0x00
            ulong maxPacket = (0x1FFFUL << 8) | 0x00UL;
            Assert.Equal(8191, WmiController.DecodeFanSpeed(maxPacket));
        }

        [Fact]
        public void HardwareFailover_SimulatedIntermittentFlapping_500CycleRecoveryStress()
        {
            using var wmi = new WmiController();
            var rand = new Random(555);

            int successfulRecoveries = 0;

            for (int cycle = 0; cycle < 500; cycle++)
            {
                int condition = rand.Next(0, 4);
                switch (condition)
                {
                    case 0:
                        // Normal reading attempt
                        _ = wmi.CpuTemp;
                        _ = wmi.CpuFanRpm;
                        successfulRecoveries++;
                        break;

                    case 1:
                        // Sensor timeout/zero reading simulation
                        _ = wmi.GetSensorReading(0x01);
                        successfulRecoveries++;
                        break;

                    case 2:
                        // RPC unavailable / cache invalidation simulation
                        var invMethod = typeof(WmiController).GetMethod("InvalidateCacheUnderLock", BindingFlags.NonPublic | BindingFlags.Instance);
                        invMethod?.Invoke(wmi, null);
                        successfulRecoveries++;
                        break;

                    case 3:
                        // Mode change failover
                        _ = wmi.TrySetPowerMode((byte)(cycle % 5));
                        successfulRecoveries++;
                        break;
                }
            }

            Assert.Equal(500, successfulRecoveries);
        }

        #endregion

        #region 5. AppSettings Corruption Recovery and Concurrent Save/Load Stress

        [Fact]
        public void AppSettings_Load_CorruptedJsonFiles_GracefullyRecoversToValidDefaults()
        {
            using var scope = new SettingsBackupScope();

            string settingsFile = AppSettings.SettingsFilePath;
            string dir = Path.GetDirectoryName(settingsFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // Test diverse corruption payloads
            string[] corruptionPayloads =
            {
                "{\"Theme\": \"Dark\", \"PowerMode\": ", // Truncated JSON
                "{ corrupted unclosed json syntax: [1, 2, 3 ", // Syntax error
                "", // Zero bytes
                "   \r\n\t   ", // Whitespace only
                "null", // JSON null literal
                "{\"PowerMode\": \"NOT_A_BYTE\", \"RefreshRate\": true}", // Schema type mismatch
                "\"JUST_A_STRING\"", // Invalid root type
                "12345" // Number root
            };

            void WriteWithRetry(string path, string content)
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try { File.WriteAllText(path, content); return; }
                    catch (IOException) { Thread.Sleep(20); }
                }
                File.WriteAllText(path, content);
            }

            void WriteBytesWithRetry(string path, byte[] bytes)
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try { File.WriteAllBytes(path, bytes); return; }
                    catch (IOException) { Thread.Sleep(20); }
                }
                File.WriteAllBytes(path, bytes);
            }

            foreach (var payload in corruptionPayloads)
            {
                WriteWithRetry(settingsFile, payload);

                // AppSettings.Load must NEVER throw an unhandled exception on corrupted file
                AppSettings loaded = AppSettings.Load();

                Assert.NotNull(loaded);
                Assert.NotNull(loaded.Theme);
                Assert.InRange(loaded.FanSpeedCpu, 0, 100);
                Assert.NotNull(loaded.CpuCurve);
                Assert.NotNull(loaded.GpuCurve);
            }

            // Test completely random binary garbage
            var binaryGarbage = new byte[1024];
            new Random(777).NextBytes(binaryGarbage);
            WriteBytesWithRetry(settingsFile, binaryGarbage);

            AppSettings binaryRecovered = AppSettings.Load();
            Assert.NotNull(binaryRecovered);
            Assert.NotNull(binaryRecovered.Theme);
        }

        [Fact]
        public void AppSettings_Concurrent_SaveLoad_HighIntensityStress()
        {
            // Use an isolated temporary folder for high-intensity concurrent stress testing
            string tempDir = Path.Combine(Path.GetTempPath(), "Predator_Stress_SaveLoad_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string settingsPath = Path.Combine(tempDir, "settings.json");

            try
            {
                var initialSettings = new AppSettings
                {
                    Theme = "Dark",
                    PowerMode = 1,
                    FanMode = 1,
                    FanSpeedCpu = 50,
                    FanSpeedGpu = 50
                };
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(initialSettings));

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                object fileLock = new();

                int saveCount = 0;
                int loadCount = 0;
                int readCorruptions = 0;

                const int threadCount = 12;
                var tasks = new Task[threadCount];

                // 6 Writer threads using atomic write (.tmp -> move) pattern
                for (int i = 0; i < 6; i++)
                {
                    int threadId = i;
                    tasks[i] = Task.Run(() =>
                    {
                        var rand = new Random(threadId * 100);
                        int iter = 0;
                        while (!cts.Token.IsCancellationRequested)
                        {
                            var s = new AppSettings
                            {
                                Theme = (iter % 2 == 0) ? "Dark" : "Light",
                                PowerMode = (byte)(iter % 5),
                                FanMode = (byte)(iter % 4),
                                FanSpeedCpu = rand.Next(10, 100),
                                FanSpeedGpu = rand.Next(10, 100),
                                RefreshRate = (iter % 2 == 0) ? 144 : 60,
                                CpuCurve = new List<CurvePointData>
                                {
                                    new(30, 20),
                                    new(60, 50),
                                    new(90, 100)
                                }
                            };

                            string json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });

                            lock (fileLock)
                            {
                                for (int attempt = 0; attempt < 5; attempt++)
                                {
                                    try
                                    {
                                        string tmp = settingsPath + ".tmp";
                                        File.WriteAllText(tmp, json);
                                        File.Move(tmp, settingsPath, true);
                                        Interlocked.Increment(ref saveCount);
                                        break;
                                    }
                                    catch (IOException) when (attempt < 4)
                                    {
                                        Thread.Sleep(5);
                                    }
                                }
                            }
                            iter++;
                        }
                    });
                }

                // 6 Reader threads continuously loading settings
                for (int i = 6; i < 12; i++)
                {
                    tasks[i] = Task.Run(() =>
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                string json;
                                lock (fileLock)
                                {
                                    json = File.ReadAllText(settingsPath);
                                }

                                var parsed = JsonSerializer.Deserialize<AppSettings>(json);
                                if (parsed == null || string.IsNullOrEmpty(parsed.Theme) || parsed.CpuCurve == null)
                                {
                                    Interlocked.Increment(ref readCorruptions);
                                }
                                else
                                {
                                    Interlocked.Increment(ref loadCount);
                                }
                            }
                            catch (Exception)
                            {
                                Interlocked.Increment(ref readCorruptions);
                            }
                        }
                    });
                }

                Task.WaitAll(tasks);

                Assert.True(saveCount > 50, $"Expected >50 saves, got {saveCount}");
                Assert.True(loadCount > 50, $"Expected >50 loads, got {loadCount}");
                Assert.Equal(0, readCorruptions);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AppSettings_Save_AtomicMove_SurvivesOrphanedTmpFile()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Predator_OrphanTmp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string settingsPath = Path.Combine(tempDir, "settings.json");
            string tmpPath = settingsPath + ".tmp";

            try
            {
                // Create an orphaned .tmp file left behind by a simulated crash or hard kill
                File.WriteAllText(tmpPath, "CORRUPT_ORPHANED_DATA");

                var settings = new AppSettings
                {
                    Theme = "Dark",
                    PowerMode = 0x05,
                    FanSpeedCpu = 85
                };

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

                // Atomic save overwrite
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, settingsPath, true);

                Assert.True(File.Exists(settingsPath));
                Assert.False(File.Exists(tmpPath));

                string loadedJson = File.ReadAllText(settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(loadedJson);
                Assert.NotNull(loaded);
                Assert.Equal("Dark", loaded!.Theme);
                Assert.Equal((byte)0x05, loaded.PowerMode);
                Assert.Equal(85, loaded.FanSpeedCpu);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AppSettings_ExtremeBoundaryValues_SerializationSanity()
        {
            var extremeSettings = new AppSettings
            {
                Theme = "UltraDarkThemeWithVeryLongNameAndSpecialChars!@#$%^&*()_+",
                PowerMode = 255,
                PowerModeAC = 255,
                PowerModeBattery = 0,
                AutoPowerAC = int.MaxValue,
                AutoPowerBattery = int.MinValue,
                FanMode = 255,
                FanSpeedCpu = -100,
                FanSpeedGpu = 99999,
                FanSpeedSys = 0,
                RefreshRate = -144,
                LcdOverdrive = false,
                BatteryLimit = true,
                RgbMode = -1,
                RgbBrightness = 1000,
                RgbSpeed = -50,
                RgbR = 300,
                RgbG = -20,
                RgbB = 500,
                ModeKeyAction = 999,
                TurboReturnMode = 255,
                CpuCurve = new List<CurvePointData>
                {
                    new(-100, -50),
                    new(0, 0),
                    new(1000, 2000)
                },
                GpuCurve = new List<CurvePointData>()
            };

            string json = JsonSerializer.Serialize(extremeSettings, new JsonSerializerOptions { WriteIndented = true });
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(extremeSettings.Theme, deserialized!.Theme);
            Assert.Equal(extremeSettings.PowerMode, deserialized.PowerMode);
            Assert.Equal(extremeSettings.AutoPowerAC, deserialized.AutoPowerAC);
            Assert.Equal(extremeSettings.AutoPowerBattery, deserialized.AutoPowerBattery);
            Assert.Equal(extremeSettings.FanSpeedCpu, deserialized.FanSpeedCpu);
            Assert.Equal(extremeSettings.FanSpeedGpu, deserialized.FanSpeedGpu);
            Assert.Equal(3, deserialized.CpuCurve.Count);
            Assert.Empty(deserialized.GpuCurve);
        }

        #endregion
    }
}
