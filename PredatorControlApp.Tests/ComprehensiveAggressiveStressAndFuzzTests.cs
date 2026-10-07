using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class ComprehensiveAggressiveStressAndFuzzTests
    {
        #region Category 1: High-Concurrency Multi-Threaded Access

        [Fact]
        public void WmiController_MultiThreaded_HighContention_HammerTest()
        {
            using var wmi = new WmiController();
            var exceptions = new ConcurrentBag<Exception>();
            const int threadCount = 16;
            const int iterationsPerThread = 120;

            Parallel.For(0, threadCount, threadId =>
            {
                try
                {
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        switch ((threadId + i) % 10)
                        {
                            case 0:
                                _ = wmi.CpuTemp;
                                _ = wmi.GpuTemp;
                                break;
                            case 1:
                                _ = wmi.CpuFanRpm;
                                _ = wmi.GpuFanRpm;
                                _ = wmi.SystemFanRpm;
                                break;
                            case 2:
                                wmi.SetPowerMode((byte)(i % 7));
                                break;
                            case 3:
                                wmi.SetFanSpeed((byte)(i % 101), (byte)((i * 3) % 101));
                                break;
                            case 4:
                                wmi.SetCpuFanSpeed((byte)(i % 101));
                                wmi.SetGpuFanSpeed((byte)((i + 10) % 101));
                                wmi.SetSystemFanSpeed((byte)((i + 20) % 101));
                                break;
                            case 5:
                                wmi.SetFanBehavior((byte)(1 + (i % 3)));
                                break;
                            case 6:
                                wmi.SetRgbMode(i % 5, (byte)(i % 256), (byte)((i * 2) % 256), (byte)((i * 3) % 256), (byte)((i * 10) % 101), (byte)((i * 20) % 101), 0);
                                wmi.SetBrightness((byte)((i * 15) % 101));
                                break;
                            case 7:
                                wmi.SetStaticColor((byte)(i % 256), (byte)((i * 2) % 256), (byte)((i * 3) % 256), (byte)100);
                                wmi.SetZoneColor(i % 4, (i * 4) % 256, (i * 5) % 256, (i * 6) % 256);
                                break;
                            case 8:
                                wmi.TurnOffBacklight();
                                _ = wmi.CustomCpuFanSpeed;
                                _ = wmi.CustomGpuFanSpeed;
                                _ = wmi.CustomSystemFanSpeed;
                                break;
                            case 9:
                                _ = WmiController.GetOverlayForMode((byte)(i % 7));
                                wmi.SetBatteryChargeLimit(i % 2 == 0);
                                wmi.SetLcdOverdrive(i % 2 == 1);
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
            Assert.InRange(wmi.Brightness, 0, 100);
            Assert.InRange(wmi.CustomCpuFanSpeed, (byte)0, (byte)100);
            Assert.InRange(wmi.CustomGpuFanSpeed, (byte)0, (byte)100);
        }

        [Fact]
        public void AppSettings_Concurrent_ReadWriteSnapshot_HighContention()
        {
            var settings = new AppSettings();
            var exceptions = new ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            const int workerCount = 16;
            var tasks = new Task[workerCount];

            for (int t = 0; t < workerCount; t++)
            {
                int workerId = t;
                tasks[t] = Task.Run(() =>
                {
                    int iteration = 0;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        iteration++;
                        try
                        {
                            switch (workerId % 5)
                            {
                                case 0:
                                    settings.PowerMode = (byte)(iteration % 7);
                                    settings.FanSpeedCpu = iteration % 101;
                                    settings.FanSpeedGpu = (iteration * 2) % 101;
                                    settings.RgbBrightness = (iteration * 3) % 101;
                                    settings.RgbR = iteration % 256;
                                    break;
                                case 1:
                                    lock (settings.CpuCurve)
                                    {
                                        if (settings.CpuCurve.Count > 10)
                                            settings.CpuCurve.Clear();
                                        settings.CpuCurve.Add(new CurvePointData(iteration % 100, (iteration * 5) % 100));
                                    }
                                    break;
                                case 2:
                                    lock (settings.GpuCurve)
                                    {
                                        if (settings.GpuCurve.Count > 10)
                                            settings.GpuCurve.Clear();
                                        settings.GpuCurve.Add(new CurvePointData(iteration % 100, (iteration * 7) % 100));
                                    }
                                    break;
                                case 3:
                                    var snapshot = settings.CreateSnapshot();
                                    Assert.NotNull(snapshot);
                                    Assert.NotNull(snapshot.CpuCurve);
                                    Assert.NotNull(snapshot.GpuCurve);
                                    break;
                                case 4:
                                    string json = JsonSerializer.Serialize(settings);
                                    Assert.False(string.IsNullOrWhiteSpace(json));
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
        }

        [Fact]
        public void BacklightStateManager_HighConcurrency_ChaosThreadTest()
        {
            var mgr = new BacklightStateManager();
            var exceptions = new ConcurrentBag<Exception>();
            const int threadCount = 16;
            const int iterationsPerThread = 150;

            Parallel.For(0, threadCount, threadId =>
            {
                try
                {
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        int op = (threadId + i) % 6;
                        switch (op)
                        {
                            case 0:
                                mgr.OnPowerSourceChanged(i % 2 == 0, out bool turnOffPs, out int brightPs);
                                Assert.InRange(brightPs, 0, 100);
                                if (turnOffPs) Assert.True(brightPs <= 100);
                                break;
                            case 1:
                                bool isOpen = (i % 3 != 0);
                                mgr.OnLidChanged(
                                    isOpen: isOpen,
                                    lineStatus: (PowerLineStatus)(i % 3),
                                    out bool turnOffLid,
                                    out int brightLid,
                                    chargeStatus: (BatteryChargeStatus)(i % 4));
                                Assert.InRange(brightLid, 0, 100);
                                if (!isOpen)
                                {
                                    Assert.True(turnOffLid);
                                    Assert.Equal(0, brightLid);
                                }
                                break;
                            case 2:
                                mgr.OnSuspend(out bool turnOffSus, out int brightSus);
                                Assert.True(turnOffSus);
                                Assert.Equal(0, brightSus);
                                break;
                            case 3:
                                mgr.OnResume(
                                    (PowerLineStatus)(i % 3),
                                    out bool turnOffRes,
                                    out int brightRes,
                                    (BatteryChargeStatus)(i % 4));
                                Assert.InRange(brightRes, 0, 100);
                                break;
                            case 4:
                                mgr.OnUserAdjustedBrightness((i * 10) % 150, onBattery: i % 2 == 0);
                                Assert.InRange(mgr.ManualBatteryBrightness, 0, 100);
                                Assert.InRange(mgr.SavedAcBrightness, 0, 100);
                                break;
                            case 5:
                                _ = mgr.IsOnBattery((PowerLineStatus)(i % 3), (BatteryChargeStatus)(i % 4));
                                mgr.SetPluggedInState(i % 3 == 0 ? null : (i % 3 == 1));
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
            Assert.InRange(mgr.SavedAcBrightness, 0, 100);
            Assert.InRange(mgr.ManualBatteryBrightness, 0, 100);
        }

        [Fact]
        public void GameSyncController_HighConcurrency_ProfileModificationsAndStateToggles()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"gamesync_stress_{Guid.NewGuid():N}.json");
            try
            {
                using var controller = new GameSyncController(tempFile);
                var exceptions = new ConcurrentBag<Exception>();
                const int threadCount = 12;
                const int iterationsPerThread = 60;

                Parallel.For(0, threadCount, threadId =>
                {
                    try
                    {
                        for (int i = 0; i < iterationsPerThread; i++)
                        {
                            string exeName = $"game_{(threadId + i) % 8}.exe";
                            switch ((threadId + i) % 6)
                            {
                                case 0:
                                    controller.AddProfile(new GameProfile
                                    {
                                        ExecutableName = exeName,
                                        CpuFanSpeed = (i * 10) % 101,
                                        GpuFanSpeed = (i * 12) % 101,
                                        PowerMode = (byte)(i % 7)
                                    });
                                    break;
                                case 1:
                                    controller.UpdateProfile(new GameProfile
                                    {
                                        ExecutableName = exeName,
                                        CpuFanSpeed = (i * 5) % 101,
                                        GpuFanSpeed = (i * 7) % 101,
                                        RefreshRate = 144
                                    });
                                    break;
                                case 2:
                                    controller.RemoveProfile(exeName);
                                    break;
                                case 3:
                                    var profiles = controller.Profiles;
                                    Assert.NotNull(profiles);
                                    foreach (var p in profiles)
                                    {
                                        Assert.NotNull(p);
                                        Assert.False(string.IsNullOrWhiteSpace(p.ExecutableName));
                                    }
                                    break;
                                case 4:
                                    _ = controller.ActiveGameExe;
                                    controller.SetPreGameSnapshot(new DashboardSnapshot());
                                    break;
                                case 5:
                                    controller.IsEnabled = (i % 2 == 0);
                                    _ = controller.IsEnabled;
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });

                Assert.Empty(exceptions);
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        [Fact]
        public void TelemetryHistory_HistoryBuffer_HighConcurrency_UnderExtremeLoad()
        {
            var bufferSmall = new HistoryBuffer(3);
            var bufferNormal = new HistoryBuffer(60);
            var exceptions = new ConcurrentBag<Exception>();

            double?[] testValues = new double?[]
            {
                null, 0.0, 45.5, 99.9, -10.0, double.NaN, double.PositiveInfinity,
                double.NegativeInfinity, 1000.0, -999.0, 72.3, 50.0
            };

            Parallel.For(0, 16, threadId =>
            {
                try
                {
                    for (int i = 0; i < 200; i++)
                    {
                        double? val = testValues[(threadId + i) % testValues.Length];
                        var target = (i % 2 == 0) ? bufferSmall : bufferNormal;

                        target.Add(val);
                        _ = target.Count;
                        _ = target.Capacity;
                        _ = target[i % target.Capacity];
                        _ = target.Range();
                        _ = target.Min();
                        _ = target.Max();
                        _ = target.Average();

                        var snapshot = target.GetSnapshot();
                        Assert.NotNull(snapshot);
                        Assert.InRange(snapshot.Length, 0, target.Capacity);

                        if (i % 50 == 0)
                        {
                            target.Clear();
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
            Assert.InRange(bufferSmall.Count, 0, bufferSmall.Capacity);
            Assert.InRange(bufferNormal.Count, 0, bufferNormal.Capacity);
        }

        [Fact]
        public void CurveFollower_HighConcurrency_DynamicTemperatureAndCurveUpdates()
        {
            var follower = new CurveFollower();
            var curvePoints = new List<Point>
            {
                new(30, 0), new(50, 20), new(70, 50), new(85, 80), new(100, 100)
            };

            var exceptions = new ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            const int workers = 12;
            var tasks = new Task[workers];

            for (int t = 0; t < workers; t++)
            {
                int workerId = t;
                tasks[t] = Task.Run(() =>
                {
                    double temp = 25.0;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            if (workerId % 4 == 0)
                            {
                                temp = (temp + 1.5) % 110.0;
                                int speed = follower.Update(temp, curvePoints);
                                Assert.InRange(speed, 0, 100);
                            }
                            else if (workerId % 4 == 1)
                            {
                                double[] weirdTemps = { double.NaN, double.PositiveInfinity, -50.0, 150.0, 0.0 };
                                foreach (var wt in weirdTemps)
                                {
                                    int speed = follower.Update(wt, curvePoints);
                                    Assert.InRange(speed, 0, 100);
                                }
                            }
                            else if (workerId % 4 == 2)
                            {
                                lock (curvePoints)
                                {
                                    curvePoints[1] = new Point(50, (curvePoints[1].Y + 5) % 100);
                                }
                            }
                            else
                            {
                                _ = follower.Current;
                                if (temp > 100.0) follower.Reset();
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

        #endregion

        #region Category 2: Corrupted/Malformed JSON & Extreme Boundary Values

        [Theory]
        [InlineData("")]
        [InlineData("   \r\n\t  ")]
        [InlineData("NOT_JSON_AT_ALL")]
        [InlineData("{\"Theme\": \"Dark\", \"PowerMode\": ")]
        [InlineData("{\"Theme\": 12345, \"PowerMode\": \"NON_BYTE\"}")]
        [InlineData("{\"CpuCurve\": \"NOT_AN_ARRAY\"}")]
        [InlineData("[1, 2, 3]")]
        [InlineData("null")]
        [InlineData("{\"PowerMode\": -999, \"FanSpeedCpu\": 999999}")]
        [InlineData("{\"CpuCurve\": [{\"X\": -500, \"Y\": 9999}], \"GpuCurve\": [null]}")]
        public void AppSettings_Load_FuzzingCorruptedJsonInputs_RecoversSafely(string corruptedJson)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"settings_corrupt_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, "settings.json");

            try
            {
                File.WriteAllText(tempFile, corruptedJson);
                AppSettings.CustomFilePath = tempFile;

                var loaded = AppSettings.Load();
                Assert.NotNull(loaded);
                Assert.NotNull(loaded.CpuCurve);
                Assert.NotNull(loaded.GpuCurve);
                Assert.InRange(loaded.PowerMode, (byte)0, (byte)6);
                Assert.InRange(loaded.FanSpeedCpu, 0, 100);
                Assert.InRange(loaded.FanSpeedGpu, 0, 100);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AppSettings_Load_BinaryGarbageFile_RecoversSafely()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"settings_bin_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, "settings.json");

            try
            {
                byte[] junk = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00, 0xFF, 0xFE, 0xFD };
                File.WriteAllBytes(tempFile, junk);
                AppSettings.CustomFilePath = tempFile;

                var loaded = AppSettings.Load();
                Assert.NotNull(loaded);
                Assert.InRange(loaded.PowerMode, (byte)0, (byte)6);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AppSettings_Sanitize_ExtremeBoundaryValues_ClampsAccurately()
        {
            var settings = new AppSettings
            {
                Theme = "INVALID_THEME_NAME",
                PowerMode = 255,
                PowerModeAC = 100,
                PowerModeBattery = 99,
                AutoPowerAC = -5,
                AutoPowerBattery = 20,
                FanMode = 99,
                FanModeAC = 50,
                FanModeBattery = 75,
                AutoFanAC = -10,
                AutoFanBattery = 50,
                FanSpeedCpu = -100,
                FanSpeedGpu = 999,
                FanSpeedSys = -50,
                FanSpeedCpuAC = 1000,
                FanSpeedCpuBattery = -1,
                FanSpeedGpuAC = 200,
                FanSpeedGpuBattery = -50,
                FanSpeedSysAC = 500,
                FanSpeedSysBattery = -20,
                RefreshRate = -60,
                RgbMode = 999,
                RgbBrightness = -10,
                RgbSpeed = 500,
                RgbR = -50,
                RgbG = 300,
                RgbB = -1,
                GpuMode = -1,
                ModeKeyAction = -5,
                TurboReturnMode = 99
            };

            for (int i = 0; i < 25; i++)
            {
                settings.CpuCurve.Add(new CurvePointData(-100 + i, 200 + i));
                settings.GpuCurve.Add(new CurvePointData(200 - i, -50 + i));
            }

            settings.Sanitize();

            Assert.Equal("System", settings.Theme);
            Assert.Equal(6, settings.PowerMode);
            Assert.Equal((byte)6, settings.PowerModeAC);
            Assert.Equal((byte)6, settings.PowerModeBattery);
            Assert.Equal(0, settings.AutoPowerAC);
            Assert.Equal(10, settings.AutoPowerBattery);

            Assert.Equal(3, settings.FanMode);
            Assert.Equal((byte)3, settings.FanModeAC);
            Assert.Equal((byte)3, settings.FanModeBattery);
            Assert.Equal(0, settings.AutoFanAC);
            Assert.Equal(10, settings.AutoFanBattery);

            Assert.Equal(0, settings.FanSpeedCpu);
            Assert.Equal(100, settings.FanSpeedGpu);
            Assert.Equal(0, settings.FanSpeedSys);
            Assert.Equal(100, settings.FanSpeedCpuAC);
            Assert.Equal(0, settings.FanSpeedCpuBattery);
            Assert.Equal(100, settings.FanSpeedGpuAC);
            Assert.Equal(0, settings.FanSpeedGpuBattery);
            Assert.Equal(100, settings.FanSpeedSysAC);
            Assert.Equal(0, settings.FanSpeedSysBattery);

            Assert.Equal(0, settings.RefreshRate);
            Assert.Equal(10, settings.RgbMode);
            Assert.Equal(0, settings.RgbBrightness);
            Assert.Equal(100, settings.RgbSpeed);
            Assert.Equal(0, settings.RgbR);
            Assert.Equal(255, settings.RgbG);
            Assert.Equal(0, settings.RgbB);
            Assert.Equal(0, settings.GpuMode);
            Assert.Equal(0, settings.ModeKeyAction);
            Assert.Equal((byte)6, settings.TurboReturnMode);

            Assert.True(settings.CpuCurve.Count <= 16);
            Assert.True(settings.GpuCurve.Count <= 16);

            foreach (var pt in settings.CpuCurve)
            {
                Assert.InRange(pt.X, 0, 100);
                Assert.InRange(pt.Y, 0, 100);
            }
            foreach (var pt in settings.GpuCurve)
            {
                Assert.InRange(pt.X, 0, 100);
                Assert.InRange(pt.Y, 0, 100);
            }
        }

        [Fact]
        public void CurveFollower_ExtremeBoundaryAndPathologicalInputs_DoesNotCrash()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 10), new(50, 30), new(70, 60), new(90, 90), new(100, 100)
            };

            double[] extremeTemps =
            {
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
                -273.15,
                -1000.0,
                0.0,
                1000.0,
                1e10,
                double.MinValue,
                double.MaxValue
            };

            foreach (var temp in extremeTemps)
            {
                int res = follower.Update(temp, curve);
                Assert.InRange(res, 0, 100);
            }

            for (int i = 0; i < 50; i++)
            {
                follower.Update(double.NaN, curve);
                int validSpeed = follower.Update(50.0, curve);
                Assert.InRange(validSpeed, 10, 100);
            }
        }

        [Fact]
        public void GameSyncProfile_SanitizeProfileValues_ExtremeInputs()
        {
            var profile = new GameProfile
            {
                ExecutableName = "test.exe",
                CpuFanSpeed = -50,
                GpuFanSpeed = 200,
                SysFanSpeed = -10,
                RefreshRate = 15,
                BatteryLimit = 30,
                RgbBrightness = 150,
                RgbSpeed = -1,
                RgbR = 500,
                RgbG = -20,
                RgbB = 255
            };

            GameSyncController.SanitizeProfileValues(profile);

            Assert.Equal(0, profile.CpuFanSpeed);
            Assert.Equal(100, profile.GpuFanSpeed);
            Assert.Equal(0, profile.SysFanSpeed);
            Assert.Equal(-1, profile.RefreshRate);
            Assert.Equal(50, profile.BatteryLimit);
            Assert.Equal(100, profile.RgbBrightness);
            Assert.Equal(-1, profile.RgbSpeed);
            Assert.Equal(255, profile.RgbR);
            Assert.Equal(0, profile.RgbG);
            Assert.Equal(255, profile.RgbB);
        }

        #endregion

        #region Category 3: Rapid Power State Transitions (AC <-> DC Rapid Cycling)

        [Fact]
        public void DebouncePowerLine_RapidChaoticTransitions_10000Cycles()
        {
            bool? current = true;
            bool? pending = null;
            int ticks = 0;

            var random = new Random(1337);
            PowerLineStatus[] statuses =
            {
                PowerLineStatus.Online,
                PowerLineStatus.Offline,
                PowerLineStatus.Unknown
            };

            for (int i = 0; i < 10000; i++)
            {
                var inputStatus = statuses[random.Next(statuses.Length)];
                bool? prevCurrent = current;
                bool? nextCurrent = Form1.DebouncePowerLine(inputStatus, current, ref pending, ref ticks);

                if (inputStatus == PowerLineStatus.Unknown)
                {
                    Assert.Equal(prevCurrent, nextCurrent);
                    Assert.Null(pending);
                    Assert.Equal(0, ticks);
                }

                current = nextCurrent;
                Assert.NotNull(current);
            }
        }

        [Fact]
        public void BacklightStateManager_RapidAC_DC_Lid_Sleep_MatrixCycle_2000Cycles()
        {
            var mgr = new BacklightStateManager();
            var rand = new Random(42);

            for (int i = 0; i < 2000; i++)
            {
                bool ac = rand.Next(2) == 0;
                bool lidOpen = rand.Next(4) != 0;
                bool sleep = rand.Next(10) == 0;

                if (sleep)
                {
                    mgr.OnSuspend(out bool turnOff, out int targetBrightness);
                    Assert.True(turnOff);
                    Assert.Equal(0, targetBrightness);
                }
                else if (!lidOpen)
                {
                    mgr.OnLidChanged(false, ac ? PowerLineStatus.Online : PowerLineStatus.Offline,
                        out bool turnOff, out int targetBrightness);
                    Assert.True(turnOff);
                    Assert.Equal(0, targetBrightness);
                }
                else
                {
                    if (mgr.IsSleeping)
                    {
                        mgr.OnResume(ac ? PowerLineStatus.Online : PowerLineStatus.Offline,
                            out bool turnOff, out int targetBrightness);
                        Assert.InRange(targetBrightness, 0, 100);
                    }
                    else
                    {
                        mgr.OnPowerSourceChanged(ac, out bool turnOff, out int targetBrightness);
                        Assert.InRange(targetBrightness, 0, 100);
                        if (!ac && !mgr.ManualBacklightOnBattery)
                        {
                            Assert.True(turnOff);
                            Assert.Equal(0, targetBrightness);
                        }
                    }
                }
            }
        }

        #endregion

        #region Category 4: Race Conditions During Rapid Fan Mode Toggles & Curve Updates

        [Fact]
        public void FanMode_RapidToggles_WithConcurrentCurveInterpolation_StressTest()
        {
            using var wmi = new WmiController();
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 0), new(50, 20), new(70, 45), new(85, 75), new(100, 100)
            };

            var exceptions = new ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            const int workers = 12;
            var tasks = new Task[workers];

            for (int t = 0; t < workers; t++)
            {
                int workerId = t;
                tasks[t] = Task.Run(() =>
                {
                    int cycle = 0;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        cycle++;
                        try
                        {
                            if (workerId % 3 == 0)
                            {
                                byte mode = (byte)(1 + (cycle % 3));
                                wmi.SetFanBehavior(mode);
                                wmi.SetFanSpeed((byte)((cycle * 7) % 101), (byte)((cycle * 11) % 101));
                            }
                            else if (workerId % 3 == 1)
                            {
                                double temp = 30.0 + (cycle % 70);
                                int interp = Form1.InterpolateCurve(curve, (int)temp);
                                Assert.InRange(interp, 0, 100);

                                int followerSpeed = follower.Update(temp, curve);
                                Assert.InRange(followerSpeed, 0, 100);
                            }
                            else
                            {
                                lock (curve)
                                {
                                    curve[2] = new Point(70, 30 + (cycle % 50));
                                }
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
        public void FanCurveGraph_Normalize_PathologicalCurves_Stress()
        {
            var normNull = FanCurveGraph.Normalize(null);
            Assert.NotNull(normNull);
            Assert.True(normNull.Count >= 2);

            var normEmpty = FanCurveGraph.Normalize(new List<Point>());
            Assert.NotNull(normEmpty);
            Assert.True(normEmpty.Count >= 2);

            var normSingle = FanCurveGraph.Normalize(new List<Point> { new(60, 40) });
            Assert.NotNull(normSingle);
            Assert.True(normSingle.Count >= 2);

            var disordered = new List<Point>
            {
                new(80, 90),
                new(50, 20),
                new(50, 40),
                new(30, 0),
                new(100, 100)
            };
            var normDisordered = FanCurveGraph.Normalize(disordered);
            Assert.NotNull(normDisordered);
            for (int i = 0; i < normDisordered.Count - 1; i++)
            {
                Assert.True(normDisordered[i].X < normDisordered[i + 1].X, "Points must be strictly monotonically increasing");
            }

            var extreme = new List<Point>
            {
                new(-500, -200),
                new(20, 10),
                new(50, 50),
                new(500, 1000)
            };
            var normExtreme = FanCurveGraph.Normalize(extreme);
            Assert.NotNull(normExtreme);
            Assert.Equal(30, normExtreme[0].X);
            Assert.Equal(0, normExtreme[0].Y);
            Assert.Equal(100, normExtreme[^1].X);
            Assert.Equal(100, normExtreme[^1].Y);
        }

        #endregion

        #region Category 5: Invalid or Unexpected WMI Responses

        [Fact]
        public void WmiController_TachometerDecoding_AdversarialBuffersAndBitmasks()
        {
            Assert.Equal(0, WmiController.MaskTachometerRpm(0x00));
            Assert.Equal(0, WmiController.MaskTachometerRpm(0x01));
            Assert.Equal(0, WmiController.MaskTachometerRpm((3000UL << 8) | 0x05));
            Assert.Equal(3000, WmiController.MaskTachometerRpm(3000UL << 8));
            Assert.Equal(0x1FFF, WmiController.MaskTachometerRpm((0x1FFFUL << 8)));
            Assert.Equal(0, WmiController.MaskTachometerRpm((0x2000UL << 8)));

            Assert.Equal(0, WmiController.DecodeFanSpeed(0));
            Assert.Equal(4500, WmiController.DecodeFanSpeed(4500));
            Assert.Equal(8191, WmiController.DecodeFanSpeed(0x1FFF));

            Assert.Equal(32, WmiController.DecodeFanSpeed(0x2000));
            Assert.Equal(0, WmiController.DecodeFanSpeed((4500UL << 8) | 0x01));
            Assert.Equal(4500, WmiController.DecodeFanSpeed(4500UL << 8));
            Assert.Equal(0, WmiController.DecodeFanSpeed(ulong.MaxValue));
        }

        [Theory]
        [InlineData(-1, false)]
        [InlineData(-256, false)]
        [InlineData(0, false)]
        [InlineData(256, true)]
        [InlineData(2560, true)]
        [InlineData(12800, true)]
        [InlineData(25600, true)]
        [InlineData(25856, false)]
        [InlineData(1500, false)]
        [InlineData(3500, false)]
        [InlineData(4501, false)]
        public void WmiController_IsDutyCyclePayload_Fuzzing(int payload, bool expected)
        {
            bool actual = WmiController.IsDutyCyclePayload(payload);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void WmiController_StaticHelpers_CorruptedAndUnexpectedInputs()
        {
            ulong payloadQuiet = WmiController.BuildFanBehaviorPayload(0x00);
            Assert.Equal(0x0BUL, payloadQuiet & 0xFFUL);

            ulong payloadTurbo = WmiController.BuildFanBehaviorPayload(0x05);
            Assert.Equal(0x0BUL, payloadTurbo & 0xFFUL);

            var colQuiet = WmiController.GetModeLedColor(0);
            var colBal = WmiController.GetModeLedColor(1);
            var colPerf = WmiController.GetModeLedColor(4);
            var colTurbo = WmiController.GetModeLedColor(5);
            var colEco = WmiController.GetModeLedColor(6);
            var colUnknown = WmiController.GetModeLedColor(0xFF);

            Assert.Equal(colBal, colUnknown);

            Assert.Equal(WmiController.OVERLAY_EFFICIENCY, WmiController.GetOverlayForMode(0x00));
            Assert.Equal(WmiController.OVERLAY_EFFICIENCY, WmiController.GetOverlayForMode(0x06));
            Assert.Equal(WmiController.OVERLAY_PERFORMANCE, WmiController.GetOverlayForMode(0x04));
            Assert.Equal(WmiController.OVERLAY_PERFORMANCE, WmiController.GetOverlayForMode(0x05));
            Assert.Equal(WmiController.OVERLAY_BALANCED, WmiController.GetOverlayForMode(0x01));
            Assert.Equal(WmiController.OVERLAY_BALANCED, WmiController.GetOverlayForMode(0x99));
        }

        [Fact]
        public void WmiController_SendCommands_GracefullyHandleWmiAbsenceAndFailures()
        {
            using var wmi = new WmiController();

            Assert.Equal(0, wmi.GetSensorReading((ulong)SensorId.CpuTemperature));
            Assert.Equal(0, wmi.GetSensorReading((ulong)SensorId.CpuFanSpeed));
            Assert.Equal(0, wmi.GetSensorReading(999UL));

            Assert.Equal(0, wmi.GetGamingFanSpeed(1UL));
            Assert.Equal(0, wmi.GetGamingFanSpeed(2UL));
            Assert.Equal(0, wmi.GetGamingFanSpeed(999UL));

            Assert.False(wmi.TrySetPowerMode(0x01));
            Assert.False(wmi.TrySetPowerMode(0xFF));

            wmi.SetFanSpeed(0, 0);
            wmi.SetFanSpeed(100, 100);
            wmi.SetFanSpeed(255, 255);

            wmi.SetBrightness(0);
            wmi.SetBrightness(100);
            wmi.TurnOffBacklight();
            wmi.SetRgbMode(0, 255, 255, 255, 100, 50, 0);
            wmi.SetStaticColor((byte)255, (byte)255, (byte)255, (byte)100);
            wmi.SetZoneColor(0, 255, 0, 0);
        }

        #endregion
    }
}
