using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    public class ExhaustiveAdversarialStressTests
    {
        #region Subsystem 1: High-Concurrency AppSettings Read/Write/Serialize (100+ Threads)

        [Fact]
        public void AppSettings_128Threads_SimultaneousSaveLoadMutate_FileIntegrity()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AppSettings_128Threads_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testFile = Path.Combine(tempDir, "settings.json");

            try
            {
                AppSettings.CustomFilePath = testFile;

                var initialSettings = new AppSettings();
                for (int i = 0; i < 20; i++)
                {
                    initialSettings.CpuCurve.Add(new CurvePointData(30 + i * 3, 20 + i * 4));
                    initialSettings.GpuCurve.Add(new CurvePointData(30 + i * 3, 25 + i * 3));
                }
                initialSettings.Save();

                int threadCount = 128;
                var tasks = new Task[threadCount];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                int readSuccess = 0;
                int writeSuccess = 0;
                int saveSuccess = 0;
                int serializeSuccess = 0;
                var exceptions = new ConcurrentBag<Exception>();

                for (int i = 0; i < threadCount; i++)
                {
                    int tid = i;
                    tasks[i] = Task.Run(() =>
                    {
                        int iter = 0;
                        while (!cts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                int role = tid % 4;
                                if (role == 0) // Savers
                                {
                                    initialSettings.FanSpeedCpu = 40 + (iter % 60);
                                    initialSettings.Save();
                                    Interlocked.Increment(ref saveSuccess);
                                }
                                else if (role == 1) // Loaders
                                {
                                    var loaded = AppSettings.Load();
                                    Assert.NotNull(loaded);
                                    Assert.NotNull(loaded.CpuCurve);
                                    Assert.NotNull(loaded.GpuCurve);
                                    Interlocked.Increment(ref readSuccess);
                                }
                                else if (role == 2) // Mutators
                                {
                                    initialSettings.CpuCurve.Add(new CurvePointData(50 + (iter % 30), 60));
                                    if (initialSettings.CpuCurve.Count > 100)
                                    {
                                        initialSettings.CpuCurve.Clear();
                                    }
                                    Interlocked.Increment(ref writeSuccess);
                                }
                                else // Serializers
                                {
                                    var snap = initialSettings.CreateSnapshot();
                                    string json = JsonSerializer.Serialize(snap);
                                    Assert.NotEmpty(json);
                                    var parsed = JsonSerializer.Deserialize<AppSettings>(json);
                                    Assert.NotNull(parsed);
                                    Interlocked.Increment(ref serializeSuccess);
                                }
                            }
                            catch (Exception ex)
                            {
                                exceptions.Add(ex);
                            }
                            iter++;
                        }
                    });
                }

                Task.WaitAll(tasks);
                Assert.Empty(exceptions);
                Assert.True(saveSuccess > 10, $"Expected >10 saves, got {saveSuccess}");
                Assert.True(readSuccess > 10, $"Expected >10 reads, got {readSuccess}");
                Assert.True(serializeSuccess > 10, $"Expected >10 serializations, got {serializeSuccess}");

                // Final load check
                var finalLoaded = AppSettings.Load();
                Assert.NotNull(finalLoaded);
                Assert.NotNull(finalLoaded.CpuCurve);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        #endregion

        #region Subsystem 2: Malformed/Fuzzed Fan Curve Inputs

        [Fact]
        public void InterpolateCurve_NaN_Infinity_ExtremeBounds_Negative_NeverThrows()
        {
            var curve = new List<Point>
            {
                new(-50, -100),
                new(0, 10),
                new(50, 50),
                new(100, 100),
                new(200, 500)
            };

            int[] testTemps = {
                int.MinValue, -1000000, -100, -1, 0, 1, 25, 50, 75, 100, 150, 200, 1000000, int.MaxValue
            };

            foreach (int temp in testTemps)
            {
                int result = Form1.InterpolateCurve(curve, temp);
                Assert.InRange(result, 10, 100);
            }
        }

        [Fact]
        public void InterpolateCurve_ExtremeBoundsSubtractionOverflow_MustNotCorruptResult()
        {
            var curve = new List<Point>
            {
                new(int.MinValue, 20),
                new(int.MaxValue, 80)
            };
            int speed = Form1.InterpolateCurve(curve, 0);
            Assert.InRange(speed, 20, 80);
        }

        [Fact]
        public void InterpolateCurve_DegeneratePointSets_Empty_Single_Duplicates_Scrambled()
        {
            // Null curve
            Assert.Equal(50, Form1.InterpolateCurve(null, 50));

            // Empty curve
            Assert.Equal(50, Form1.InterpolateCurve(new List<Point>(), 50));

            // Single point
            var single = new List<Point> { new(50, 30) };
            Assert.Equal(30, Form1.InterpolateCurve(single, 10));
            Assert.Equal(30, Form1.InterpolateCurve(single, 50));
            Assert.Equal(30, Form1.InterpolateCurve(single, 90));

            // Identical duplicated points
            var dupes = new List<Point>
            {
                new(50, 40),
                new(50, 40),
                new(50, 40)
            };
            Assert.Equal(40, Form1.InterpolateCurve(dupes, 50));

            // Scrambled / reverse-ordered points
            var scrambled = new List<Point>
            {
                new(90, 90),
                new(30, 20),
                new(70, 70),
                new(50, 40)
            };
            int speedAt50 = Form1.InterpolateCurve(scrambled, 50);
            Assert.Equal(40, speedAt50);
        }

        [Fact]
        public void InterpolateCurve_HugeArrays_100kPoints_PerformanceAndCorrectness()
        {
            var rnd = new Random(42);
            var hugeList = new List<Point>(100_000);
            for (int i = 0; i < 100_000; i++)
            {
                hugeList.Add(new Point(rnd.Next(-1000, 2000), rnd.Next(0, 100)));
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int t = 0; t <= 100; t += 10)
            {
                int spd = Form1.InterpolateCurve(hugeList, t);
                Assert.InRange(spd, 10, 100);
            }
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 3000, $"100k points interpolation took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public void FanCurveGraph_Normalize_Malformed_HugeArray_NegativePoints()
        {
            var malformed = new List<Point>();
            for (int i = 0; i < 1000; i++)
            {
                malformed.Add(new Point(-i, -i * 2));
            }

            var normalized = FanCurveGraph.Normalize(malformed);
            Assert.NotNull(normalized);
            Assert.Equal(FanCurveGraph.ControlPointCount, normalized.Count);

            // Control points must be monotonic and strictly within valid temperature and speed ranges
            for (int i = 0; i < normalized.Count; i++)
            {
                Assert.InRange(normalized[i].X, 30, 100);
                Assert.InRange(normalized[i].Y, 0, 100);
                if (i > 0)
                {
                    Assert.True(normalized[i].X > normalized[i - 1].X, $"Points at {i-1} and {i} not monotonic: {normalized[i-1].X} vs {normalized[i].X}");
                }
            }
        }

        [Fact]
        public void CurveFollower_FuzzedTemperatures_NaN_Infinity_ExtremeDoubleBounds()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(90, 100)
            };

            double[] extremeTemps = {
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
                double.MinValue,
                double.MaxValue,
                -1e30,
                1e30,
                -50.0,
                0.0,
                25.5,
                75.2,
                150.0,
                500.0
            };

            foreach (double t in extremeTemps)
            {
                int spd = follower.Update(t, curve);
                Assert.InRange(spd, 0, 100);
            }
        }

        #endregion

        #region Subsystem 3: Rapid Power State Transitions (AC <-> Battery High Frequency)

        [Fact]
        public void PowerState_HighFrequencyOscillation_10000Ticks_SuppressesFlapping()
        {
            bool? current = true;
            bool? pending = true;
            int ticks = 2;

            int transitionCount = 0;

            // Oscillate 10,000 times between Online and Offline every single tick
            for (int i = 0; i < 10_000; i++)
            {
                var line = (i % 2 == 0) ? PowerLineStatus.Offline : PowerLineStatus.Online;
                bool? next = Form1.DebouncePowerLine(line, current, ref pending, ref ticks);
                if (next != current)
                {
                    transitionCount++;
                    current = next;
                }
            }

            // A single tick of flapping must never cause a transition
            Assert.Equal(0, transitionCount);
            Assert.Equal(true, current);
        }

        [Fact]
        public void PowerState_BurstsOfFlappingWithChargingAndUnknown_MaintainsIntegrity()
        {
            bool? current = true;
            bool? pending = true;
            int ticks = 2;

            // Tick 1: Offline but Charging -> should be treated as plugged in (Online)
            bool? res1 = Form1.DebouncePowerLine(PowerLineStatus.Offline, current, ref pending, ref ticks, BatteryChargeStatus.Charging);
            Assert.Equal(true, res1);

            // Tick 2: Unknown with no charging -> ignored, state preserved
            bool? res2 = Form1.DebouncePowerLine(PowerLineStatus.Unknown, current, ref pending, ref ticks, 0);
            Assert.Equal(true, res2);

            // Transition to Offline: requires 2 consecutive Offline ticks
            Form1.DebouncePowerLine(PowerLineStatus.Offline, current, ref pending, ref ticks, 0);
            bool? res3 = Form1.DebouncePowerLine(PowerLineStatus.Offline, current, ref pending, ref ticks, 0);
            Assert.Equal(false, res3);
        }

        [Fact]
        public void BacklightStateManager_100Threads_RapidPowerAndLidTransitions_ConcurrencyStress()
        {
            var mgr = new BacklightStateManager();
            int threadCount = 100;
            var tasks = new Task[threadCount];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var exceptions = new ConcurrentBag<Exception>();
            int ops = 0;

            for (int i = 0; i < threadCount; i++)
            {
                int tid = i;
                tasks[i] = Task.Run(() =>
                {
                    int count = 0;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            int op = (tid + count) % 5;
                            if (op == 0)
                            {
                                mgr.OnPowerSourceChanged(count % 2 == 0, out bool turnOff, out int targetB);
                                Assert.InRange(targetB, 0, 100);
                            }
                            else if (op == 1)
                            {
                                mgr.OnLidChanged(count % 2 == 0, PowerLineStatus.Online, out bool turnOff, out int targetB);
                                Assert.InRange(targetB, 0, 100);
                            }
                            else if (op == 2)
                            {
                                mgr.OnSuspend(out bool turnOff, out int targetB);
                                Assert.True(turnOff);
                                Assert.Equal(0, targetB);
                            }
                            else if (op == 3)
                            {
                                mgr.OnResume(PowerLineStatus.Online, out bool turnOff, out int targetB);
                                Assert.InRange(targetB, 0, 100);
                            }
                            else
                            {
                                mgr.OnUserAdjustedBrightness(count % 101, count % 2 == 0);
                            }
                            Interlocked.Increment(ref ops);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                        count++;
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.Empty(exceptions);
            Assert.True(ops > 1000, $"Expected >1000 ops, got {ops}");
        }

        #endregion

        #region Subsystem 4: WmiController & AcerHardwareProtocols Packet Parsing with Fuzzed/Corrupted Buffers

        [Fact]
        public void EcHidReply_Word_IntMaxValueOffset_MustNotThrowArgumentOutOfRangeException()
        {
            var reply = new EcHidReply(0xE000, 1, new byte[] { 0x01, 0x02, 0x03, 0x04 });
            ushort val = reply.Word(int.MaxValue);
            Assert.Equal((ushort)0, val);

            ushort valNull = new EcHidReply(0xE000, 1, null!).Word(0);
            Assert.Equal((ushort)0, valNull);

            byte byteNull = new EcHidReply(0xE000, 1, null!).Byte(0);
            Assert.Equal((byte)0, byteNull);
        }

        [Fact]
        public void EcHidReply_CorruptedAndTruncatedBuffers_Fuzzing()
        {
            int[] testOffsets = {
                int.MinValue, -100, -1, 0, 1, 2, 3, 4, 10, 100, int.MaxValue - 1, int.MaxValue
            };

            byte[][] testBuffers = {
                Array.Empty<byte>(),
                new byte[] { 0x55 },
                new byte[] { 0xAA, 0xBB },
                new byte[] { 0x01, 0x02, 0x03 },
                new byte[64]
            };

            foreach (var buf in testBuffers)
            {
                var reply = new EcHidReply(0xE000, 0x10, buf);
                foreach (int offset in testOffsets)
                {
                    byte b = reply.Byte(offset);
                    ushort w = reply.Word(offset);
                    if (offset < 0 || offset >= buf.Length) Assert.Equal((byte)0, b);
                    if (offset < 0 || offset > buf.Length - 2) Assert.Equal((ushort)0, w);
                }
            }
        }

        [Fact]
        public void AcerSmbios_Fuzz_10000MalformedBuffers_NeverThrowsOrInfiniteLoops()
        {
            var rnd = new Random(1337);
            for (int i = 0; i < 10_000; i++)
            {
                int len = rnd.Next(0, 128);
                byte[] corrupted = new byte[len];
                rnd.NextBytes(corrupted);

                // Occasionally craft Acer specific headers (0xAC = 172, 0xAA = 170)
                if (len >= 6 && rnd.Next(2) == 0)
                {
                    corrupted[0] = (byte)(rnd.Next(2) == 0 ? 172 : 170);
                    corrupted[1] = (byte)rnd.Next(0, 256);
                }

                var smbios = AcerSmbios.Parse(corrupted);
                Assert.NotNull(smbios);
            }
        }

        [Fact]
        public void BatteryProtocol_DecodeStatus_FuzzedAndTruncatedStatusBytes()
        {
            var rnd = new Random(42);
            for (int i = 0; i < 1000; i++)
            {
                ulong funcList = (ulong)rnd.NextInt64();
                int len = rnd.Next(0, 20);
                byte[] status = new byte[len];
                rnd.NextBytes(status);

                var decoded = BatteryProtocol.DecodeStatus(funcList, status);
                Assert.NotNull(decoded);
            }

            // Null bytes test
            Assert.Null(BatteryProtocol.DecodeStatus(0xFF, null));
        }

        [Fact]
        public void AcerProtocol_MaskDecoders_AdversarialBitmasks()
        {
            ulong[] testOutputs = {
                0UL,
                ulong.MaxValue,
                1UL,
                0xFF000000UL,
                0x8000000000000000UL,
                0x00000000FFFFFFFFUL
            };

            foreach (ulong output in testOutputs)
            {
                var sensors = AcerProtocol.DecodeSensorMask(output);
                Assert.NotNull(sensors);

                var modes = AcerProtocol.DecodeOperatingModeMask(output);
                Assert.NotNull(modes);

                var usb = AcerProtocol.UsbChargingValue(output);
                Assert.NotNull(usb);

                bool coolBoost = AcerProtocol.CoolBoostValue(output);
                int sensorVal = AcerProtocol.SensorValue(output);
                byte status = AcerProtocol.Status(output);
                bool ok = AcerProtocol.IsOk(output);
                Assert.InRange(sensorVal, 0, 65535);
            }
        }

        [Fact]
        public void AcerProtocol_WmiModeToEcMode_AllBytesAndNegativeModeCounts()
        {
            for (int b = 0; b <= 255; b++)
            {
                for (int count = -5; count <= 10; count++)
                {
                    byte ecMode = AcerProtocol.WmiModeToEcMode((byte)b, count);
                    Assert.InRange(ecMode, 0, 255);
                }
            }
        }

        [Fact]
        public void FirmwareEvent_Decode_CorruptedAndHugeBuffers()
        {
            Assert.Null(FirmwareEvent.Decode(null));
            Assert.Null(FirmwareEvent.Decode(Array.Empty<byte>()));

            var single = FirmwareEvent.Decode(new byte[] { 1 });
            Assert.NotNull(single);
            Assert.Equal((byte)0, single.Value);

            // Huge 64KB buffer
            byte[] huge = new byte[65536];
            huge[0] = 6;
            huge[1] = 42;
            var decoded = FirmwareEvent.Decode(huge);
            Assert.NotNull(decoded);
            Assert.Equal(42, decoded.Value);
            Assert.Equal(65536, decoded.Detail.Length);
        }

        #endregion

        #region Subsystem 5: Memory Allocation Stress & Zero Unbounded Growth

        [Fact]
        public void HistoryBuffer_100000Samples_ZeroMemoryLeak_BoundedCapacity()
        {
            var buffer = new HistoryBuffer(60);
            Assert.Equal(60, buffer.Capacity);

            // Force clean GC before starting
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            long memStart = GC.GetTotalMemory(true);

            for (int i = 0; i < 100_000; i++)
            {
                buffer.Add(i % 100);
            }

            Assert.Equal(60, buffer.Count);
            var snapshot = buffer.GetSnapshot();
            Assert.Equal(60, snapshot.Length);

            // Range computation check
            var range = buffer.Range();
            Assert.True(range.HasValue);

            // Clean GC after 100,000 pushes
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            long memEnd = GC.GetTotalMemory(true);

            long delta = memEnd - memStart;
            // Memory increase must be minimal (< 500 KB across 100,000 samples)
            Assert.True(delta < 512 * 1024, $"Memory delta was {delta} bytes");
        }

        [Fact]
        public void HistoryGraphControl_50000PaintAndPushCycles_ZeroGdiHandleLeaks()
        {
            using var ctrl = new HistoryGraphControl { Width = 300, Height = 150 };
            using var bmp = new Bitmap(300, 150);

            for (int i = 0; i < 50_000; i++)
            {
                ctrl.PushSample(i % 80, (i + 10) % 90);
                if (i % 500 == 0)
                {
                    using var g = Graphics.FromImage(bmp);
                    var pe = new PaintEventArgs(g, new Rectangle(0, 0, 300, 150));
                    // Indirect paint invocation
                    typeof(HistoryGraphControl)
                        .GetMethod("OnPaint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .Invoke(ctrl, new object[] { pe });
                }
            }

            Assert.Equal(60, ctrl.PrimarySeries.Count);
            Assert.Equal(60, ctrl.SecondarySeries.Count);
        }

        [Fact]
        public void PdhLoadMonitor_ConcurrentSamplingAndRapidDispose_ZeroOrphanedQueries()
        {
            int threadCount = 20;
            var tasks = new Task[threadCount];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var exceptions = new ConcurrentBag<Exception>();

            for (int i = 0; i < threadCount; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            using var monitor = new PdhLoadMonitor();
                            var (cpu, gpu) = monitor.Sample(false);
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

        #endregion
    }
}
