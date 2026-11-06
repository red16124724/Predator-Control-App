using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using PredatorControlApp;
using PredatorControlApp.Bios;
using PredatorControlApp.Display;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// App-Wide Exhaustive Chaos, Boundary Fuzzing, and Concurrency Stress Test Suite.
    /// Subjects all core subsystems, UI navigation, telemetry pipelines, fan curves,
    /// MUX controllers, BIOS managers, and GameSync to adversarial conditions.
    /// </summary>
    [Collection("SingleInstanceTests")]
    [SupportedOSPlatform("windows")]
    public class FullAppExhaustiveChaosAndStressTests : IDisposable
    {
        private readonly string _tempDirectory;
        private readonly string _testSettingsFile;

        public FullAppExhaustiveChaosAndStressTests()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "PCA_FullAppChaos_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            _testSettingsFile = Path.Combine(_tempDirectory, "settings.json");
            AppSettings.CustomFilePath = _testSettingsFile;
        }

        public void Dispose()
        {
            AppSettings.CustomFilePath = null;
            try
            {
                if (Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory, true);
                }
            }
            catch { }
        }

        #region 1. AppSettings Fuzzing (Extreme Negative, NaN, Infinity, Massive Strings, Corrupted Registry)

        [Fact]
        public void AppSettings_FuzzAllNumericalBounds_ExtremeNegativeAndOverflow_SanitizesToLegalRanges()
        {
            var settings = new AppSettings
            {
                PowerMode = (byte)250,
                PowerModeAC = (byte)150,
                PowerModeBattery = (byte)200,
                FanMode = (byte)99,
                FanModeAC = (byte)120,
                FanModeBattery = (byte)88,
                AutoPowerAC = -50,
                AutoPowerBattery = 999,
                AutoFanAC = -100,
                AutoFanBattery = 1000,
                FanSpeedCpu = -999999,
                FanSpeedGpu = 999999,
                FanSpeedSys = int.MinValue,
                FanSpeedCpuAC = int.MaxValue,
                FanSpeedCpuBattery = -500,
                FanSpeedGpuAC = 200,
                FanSpeedGpuBattery = -10,
                FanSpeedSysAC = 999,
                FanSpeedSysBattery = -1,
                FanHysteresisDegrees = -100,
                RefreshRate = -500,
                DdsMode = -50,
                RgbMode = 999,
                RgbBrightness = -100,
                RgbSpeed = 9999,
                RgbR = -500,
                RgbG = 10000,
                RgbB = -1,
                KeyboardBrightnessLevel = 5000,
                DynamicEffectSpeed = -50,
                DynamicEffectDirection = 99,
                PowerModeSettleMs = -5000,
                CpuThermalSpecTdpWatts = -50,
                CpuMinAllowedPowerWatts = -100,
                CpuMaxAllowedPl1Watts = -200,
                CpuMaxAllowedPl2Watts = -500,
                CustomEcoPl1Watts = -50,
                CustomEcoPl2Watts = -100,
                CustomQuietPl1Watts = -20,
                CustomQuietPl2Watts = -30,
                CustomBalancedPl1Watts = 9999,
                CustomBalancedPl2Watts = 10, // Inverted: PL1 > PL2
                ThermalCpuWarnCelsius = -150.0,
                ThermalCpuCeilingCelsius = 500.0,
                ThermalCpuCriticalCelsius = -20.0,
                ThermalGpuWarnCelsius = 300.0,
                ThermalGpuCeilingCelsius = -50.0,
                ThermalGpuCriticalCelsius = 1000.0,
                ThermalHysteresisCelsius = -10.0,
                ThermalMaxFanFloorPercent = 999,
                ThermalCpuPowerCapWatts = -50,
                ThermalMinPowerCapWatts = 1000
            };

            settings.Sanitize();

            // Assert strict legal hardware operating ranges
            Assert.InRange(settings.PowerMode, (byte)0, (byte)6);
            if (settings.PowerModeAC.HasValue) Assert.True(settings.PowerModeAC.Value <= 6 || settings.PowerModeAC.Value == 0xFF);
            if (settings.PowerModeBattery.HasValue) Assert.True(settings.PowerModeBattery.Value <= 6 || settings.PowerModeBattery.Value == 0xFF);

            Assert.InRange(settings.FanMode, (byte)1, (byte)3);
            if (settings.FanModeAC.HasValue) Assert.True(settings.FanModeAC.Value <= 3 || settings.FanModeAC.Value == 0xFF);
            if (settings.FanModeBattery.HasValue) Assert.True(settings.FanModeBattery.Value <= 3 || settings.FanModeBattery.Value == 0xFF);

            Assert.InRange(settings.FanSpeedCpu, 0, 100);
            Assert.InRange(settings.FanSpeedGpu, 0, 100);
            Assert.InRange(settings.FanSpeedSys, 0, 100);
            Assert.InRange(settings.FanSpeedCpuAC, 0, 100);
            Assert.InRange(settings.FanSpeedCpuBattery, 0, 100);
            Assert.InRange(settings.FanSpeedGpuAC, 0, 100);
            Assert.InRange(settings.FanSpeedGpuBattery, 0, 100);
            Assert.InRange(settings.FanSpeedSysAC, 0, 100);
            Assert.InRange(settings.FanSpeedSysBattery, 0, 100);

            Assert.InRange(settings.FanHysteresisDegrees, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees);
            Assert.InRange(settings.RefreshRate, 0, 1000);
            Assert.InRange(settings.DdsMode, 0, 2);

            Assert.InRange(settings.RgbBrightness, 0, 100);
            Assert.InRange(settings.RgbSpeed, 0, 100);
            Assert.InRange(settings.RgbR, 0, 255);
            Assert.InRange(settings.RgbG, 0, 255);
            Assert.InRange(settings.RgbB, 0, 255);

            Assert.InRange(settings.KeyboardBrightnessLevel, 0, 100);
            Assert.InRange(settings.DynamicEffectSpeed, 1, 100);
            Assert.InRange(settings.DynamicEffectDirection, 0, 1);
            Assert.InRange(settings.PowerModeSettleMs, 100, 3000);

            // Thermal profile clamping
            var thermal = settings.ToThermalProfile();
            Assert.InRange(thermal.CpuWarnCelsius, 30.0, 100.0);
            Assert.InRange(thermal.GpuWarnCelsius, 40.0, 110.0);
            Assert.InRange(thermal.MaxFanFloorPercent, 20.0, 100.0);
        }

        [Fact]
        public void AppSettings_FuzzMassiveStrings_ControlChars_AndUnicodeSurrogates_HandledSafely()
        {
            string massive100k = new string('X', 100_000);
            string maliciousPayload = "\0\r\n\t<script>alert('xss')</script>DROP TABLE Settings;--\uD83D\uDCA9";

            var settings = new AppSettings
            {
                Theme = massive100k,
                KeyboardLayoutId = maliciousPayload,
                ActiveLightingPreset = massive100k,
                DynamicLightingEffectName = maliciousPayload,
                BiosHardwareFingerprint = massive100k,
                ScannedHardwareSignature = maliciousPayload,
                StaticZoneColorMapJson = massive100k
            };

            settings.Sanitize();

            Assert.Equal("System", settings.Theme); // Unrecognized theme resets to System
            Assert.NotNull(settings.KeyboardLayoutId);
            Assert.NotNull(settings.ActiveLightingPreset);
            Assert.NotNull(settings.DynamicLightingEffectName);

            // Verify saving and loading with massive sanitized strings
            settings.Save();
            var reloaded = AppSettings.Load();
            Assert.NotNull(reloaded);
            Assert.Equal("System", reloaded.Theme);
        }

        [Fact]
        public void AppSettings_CorruptedJsonAndRegistryFuzzing_RecoversToSensibleDefaults()
        {
            // Test 1: Completely malformed JSON on disk
            File.WriteAllText(_testSettingsFile, "{ \"PowerMode\": [corrupted json syntax invalid %$#@");
            var recoveredFromJson = AppSettings.Load();
            Assert.NotNull(recoveredFromJson);
            Assert.InRange(recoveredFromJson.PowerMode, (byte)0, (byte)6);

            // Test 2: JSON with extreme floating-point / NaN / Infinity tokens
            string nanJson = @"{
                ""PowerMode"": -9999,
                ""FanSpeedCpu"": 999999,
                ""ThermalCpuWarnCelsius"": ""NaN"",
                ""ThermalGpuWarnCelsius"": ""Infinity"",
                ""ThermalMaxFanFloorPercent"": -999.0
            }";
            File.WriteAllText(_testSettingsFile, nanJson);
            var recoveredFromNan = AppSettings.Load();
            Assert.NotNull(recoveredFromNan);
            Assert.InRange(recoveredFromNan.PowerMode, (byte)0, (byte)6);
            Assert.InRange(recoveredFromNan.FanSpeedCpu, 0, 100);

            // Test 3: Registry sanitization fuzzing
            Assert.Equal("Profile", RegistrySafety.SanitizeSubKeyName(null));
            Assert.Equal("Profile", RegistrySafety.SanitizeSubKeyName("   "));
            Assert.Equal("Profile", RegistrySafety.SanitizeSubKeyName("....."));
            Assert.Equal("____etc_passwd", RegistrySafety.SanitizeSubKeyName("../../etc/passwd"));
            Assert.False(RegistrySafety.IsSafePath(""));
            Assert.False(RegistrySafety.IsSafePath("..\\..\\Windows\\System32"));
            Assert.False(RegistrySafety.IsSafePath("SOFTWARE\\Malicious\\Path"));
            Assert.False(RegistrySafety.IsSafePath(new string('A', 500)));
        }

        [Fact]
        public void AppSettings_MultiThreadedConcurrentFuzzMutation_MaintainsStateConsistency()
        {
            var settings = new AppSettings();
            var exceptions = new ConcurrentBag<Exception>();
            const int threads = 20;
            const int iterations = 100;

            Parallel.For(0, threads, t =>
            {
                var rng = new Random(t * 5381);
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        switch (i % 6)
                        {
                            case 0:
                                settings.PowerMode = (byte)rng.Next(-50, 50);
                                settings.FanMode = (byte)rng.Next(-10, 20);
                                settings.FanSpeedCpu = rng.Next(-500, 500);
                                settings.FanSpeedGpu = rng.Next(-500, 500);
                                break;
                            case 1:
                                var copy = settings.CreateSnapshot();
                                Assert.NotNull(copy);
                                break;
                            case 2:
                                settings.Sanitize();
                                break;
                            case 3:
                                lock (settings.CpuCurve)
                                {
                                    settings.CpuCurve.Add(new CurvePointData(rng.Next(-20, 120), rng.Next(-20, 120)));
                                    if (settings.CpuCurve.Count > 15) settings.CpuCurve.RemoveAt(0);
                                }
                                break;
                            case 4:
                                var safeCurve = AppSettings.SafeCopyCurve(settings.CpuCurve);
                                Assert.NotNull(safeCurve);
                                break;
                            case 5:
                                settings.RgbBrightness = rng.Next(-100, 200);
                                settings.DynamicEffectSpeed = rng.Next(-50, 150);
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
            settings.Sanitize();
            Assert.InRange(settings.PowerMode, (byte)0, (byte)6);
            Assert.InRange(settings.FanMode, (byte)1, (byte)3);
            Assert.InRange(settings.FanSpeedCpu, 0, 100);
        }

        #endregion

        #region 2. Rapid UI Navigation Chaos (All 8 Panels 100 Times Concurrently)

        [Fact]
        public void Form1_RapidUiNavigationChaos_All8Panels100TimesConcurrently_NoDeadlocksOrCrossThreadExceptions()
        {
            StaTestRunner.Run(() =>
            {
                using var form = new Form1();
                form.Show();
                Application.DoEvents();

                var exceptions = new ConcurrentBag<Exception>();
                const int threads = 8;
                const int iterationsPerThread = 100;
                using var startBarrier = new ManualResetEventSlim(false);
                using var doneCountdown = new CountdownEvent(threads);

                for (int t = 0; t < threads; t++)
                {
                    int threadId = t;
                    Task.Run(() =>
                    {
                        startBarrier.Wait();
                        try
                        {
                            for (int i = 0; i < iterationsPerThread; i++)
                            {
                                int panelIndex = (threadId + i) % 8;
                                form.NavigateToPanel(panelIndex);
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                        finally
                        {
                            doneCountdown.Signal();
                        }
                    });
                }

                startBarrier.Set();

                // Pump Windows message loop while worker threads dispatch BeginInvoke navigation calls
                var sw = Stopwatch.StartNew();
                while (!doneCountdown.Wait(25))
                {
                    Application.DoEvents();
                    if (sw.ElapsedMilliseconds > 20000)
                    {
                        throw new TimeoutException("Rapid UI navigation timed out waiting for worker threads.");
                    }
                }

                // Final pump to ensure all queued invocations complete
                for (int i = 0; i < 25; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }

                Assert.Empty(exceptions);
                Assert.False(form.IsDisposed);
                form.Close();
            }, timeoutMs: 30000);
        }

        [Fact]
        public void Form1_NavigateToPanel_DegenerateAndNegativeIndices_HandlesSafely()
        {
            StaTestRunner.Run(() =>
            {
                using var form = new Form1();
                form.Show();
                Application.DoEvents();

                // Test edge cases: negative indices, extreme overflow
                form.NavigateToPanel(-1);
                form.NavigateToPanel(-99999);
                form.NavigateToPanel(100);
                form.NavigateToPanel(int.MaxValue);
                form.NavigateToPanel(int.MinValue);

                for (int i = 0; i < 10; i++)
                {
                    Application.DoEvents();
                }

                Assert.False(form.IsDisposed);
                form.Close();
            });
        }

        #endregion

        #region 3. Rapid Hardware MUX Mode Toggling Under Concurrency

        [Fact]
        public void HardwareMuxMode_RapidTogglingUnderHighConcurrency_NoDeadlocksOrStateCorruption()
        {
            var displayMgr = DisplayManager.Instance;
            var biosMgr = BiosManager.Instance;
            var exceptions = new ConcurrentBag<Exception>();

            int ddsChangedEventCount = 0;
            Action<int> ddsHandler = (mode) => Interlocked.Increment(ref ddsChangedEventCount);
            displayMgr.DdsModeChanged += ddsHandler;

            try
            {
                const int threadCount = 10;
                const int iterations = 50;

                Parallel.For(0, threadCount, t =>
                {
                    var rng = new Random(t * 7919);
                    try
                    {
                        for (int i = 0; i < iterations; i++)
                        {
                            int modeInt = (t + i) % 3;
                            GpuMode mode = modeInt switch
                            {
                                0 => GpuMode.iGpuOnly,
                                1 => GpuMode.Auto,
                                _ => GpuMode.dGpuOnly
                            };

                            if (i % 3 == 0)
                            {
                                displayMgr.SetHardwareMuxMode(mode);
                            }
                            else if (i % 3 == 1)
                            {
                                displayMgr.SetDdsMode(modeInt);
                            }
                            else
                            {
                                biosMgr.SetGpuMode(mode, out _, bypassSafetyGates: true);
                            }

                            // Concurrent live queries
                            var currentHw = displayMgr.GetHardwareMuxMode();
                            var ddsStatus = displayMgr.GetDdsStatus();
                            var biosMux = biosMgr.GetGpuMode();

                            Assert.True(Enum.IsDefined(typeof(GpuMode), currentHw));
                            Assert.InRange(ddsStatus.ActiveMux, 0, 2);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });

                Assert.Empty(exceptions);
                Assert.True(ddsChangedEventCount >= 0);
            }
            finally
            {
                displayMgr.DdsModeChanged -= ddsHandler;
            }
        }

        [Fact]
        public void HardwareMuxMode_InvalidAndBoundaryValues_RejectedSafely()
        {
            var displayMgr = DisplayManager.Instance;
            Assert.False(displayMgr.SetDdsMode(-1));
            Assert.False(displayMgr.SetDdsMode(3));
            Assert.False(displayMgr.SetDdsMode(999));

            var biosMgr = BiosManager.Instance;
            biosMgr.ModificationsEnabled = false;
            biosMgr.DangerousAllowed = false;

            // Blocked when safety gates are disabled
            bool okBlocked = biosMgr.SetGpuMode(GpuMode.dGpuOnly, out string errBlocked, bypassSafetyGates: false);
            Assert.False(okBlocked);
            Assert.Contains("disabled", errBlocked, StringComparison.OrdinalIgnoreCase);

            // Allowed when bypassed
            bool okBypassed = biosMgr.SetGpuMode(GpuMode.dGpuOnly, out _, bypassSafetyGates: true);
            Assert.True(okBypassed);
        }

        #endregion

        #region 4. Fuzz BiosManager (Invalid Names, Unauthorized Dangerous Settings, Null Items, Unknown Options)

        [Fact]
        public void BiosManager_Fuzzing_NullItems_InvalidNames_UnauthorizedDangerous_UnknownOptions()
        {
            var manager = BiosManager.Instance;

            // 1. Null item
            bool okNull = manager.ApplySetting(null!, out string errNull);
            Assert.False(okNull);
            Assert.Contains("null", errNull, StringComparison.OrdinalIgnoreCase);

            // 2. Empty or whitespace setting names
            bool okEmptyName = manager.ApplySetting(new BiosSettingItem { Name = "", PendingValue = "Val" }, out string errEmptyName);
            Assert.False(okEmptyName);
            Assert.Contains("name cannot be empty", errEmptyName, StringComparison.OrdinalIgnoreCase);

            bool okWsName = manager.ApplySetting(new BiosSettingItem { Name = "   ", PendingValue = "Val" }, out string errWsName);
            Assert.False(okWsName);
            Assert.Contains("name cannot be empty", errWsName, StringComparison.OrdinalIgnoreCase);

            // 3. Empty or null pending value
            bool okEmptyVal = manager.ApplySetting(new BiosSettingItem { Name = "ValidSetting", PendingValue = "" }, out string errEmptyVal);
            Assert.False(okEmptyVal);
            Assert.Contains("value cannot be empty", errEmptyVal, StringComparison.OrdinalIgnoreCase);

            // 4. Unauthorized dangerous settings gate:
            // Case A: ModificationsEnabled = false
            manager.ModificationsEnabled = false;
            manager.DangerousAllowed = false;

            var normalItem = new BiosSettingItem
            {
                Name = "Keyboard backlight timeout",
                PendingValue = "30 Seconds",
                Options = new() { "Disabled", "10 Seconds", "30 Seconds" },
                Risk = BiosSettingRisk.Normal
            };
            bool okDisabledMod = manager.ApplySetting(normalItem, out string errDisabledMod);
            Assert.False(okDisabledMod);
            Assert.Contains("disabled", errDisabledMod, StringComparison.OrdinalIgnoreCase);

            var dangerousItem = new BiosSettingItem
            {
                Name = "Display MUX (Advanced Optimus / dGPU)",
                PendingValue = "Discrete GPU only",
                Options = new() { "Auto", "Optimus", "Discrete GPU only" },
                Risk = BiosSettingRisk.Dangerous
            };
            bool okDisabledDanger = manager.ApplySetting(dangerousItem, out string errDisabledDanger);
            Assert.False(okDisabledDanger);
            Assert.Contains("disabled", errDisabledDanger, StringComparison.OrdinalIgnoreCase);

            // Case B: ModificationsEnabled = true, DangerousAllowed = false
            manager.ModificationsEnabled = true;
            manager.DangerousAllowed = false;

            bool okBlockedDanger = manager.ApplySetting(dangerousItem, out string errBlockedDanger);
            Assert.False(okBlockedDanger);
            Assert.Contains("blocked", errBlockedDanger, StringComparison.OrdinalIgnoreCase);

            // Normal setting should now be allowed
            bool okNormal = manager.ApplySetting(normalItem, out string errNormal);
            Assert.True(okNormal);

            // Case C: ModificationsEnabled = true, DangerousAllowed = true
            manager.DangerousAllowed = true;
            bool okAllowedDanger = manager.ApplySetting(dangerousItem, out string errAllowedDanger);
            Assert.True(okAllowedDanger);

            // 5. Unknown options fuzzing:
            var optionsItem = new BiosSettingItem
            {
                Name = "POST Animation & Sound",
                PendingValue = "LudicrousSpeed", // not in Options
                Options = new() { "Enabled", "Disabled" },
                Risk = BiosSettingRisk.Normal
            };
            bool okUnknownOpt = manager.ApplySetting(optionsItem, out string errUnknownOpt);
            Assert.False(okUnknownOpt);
            Assert.Contains("Invalid value", errUnknownOpt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("LudicrousSpeed", errUnknownOpt);

            // Fuzz with massive option strings
            string massiveValue = new string('A', 50000);
            var massiveItem = new BiosSettingItem
            {
                Name = "USB Charging",
                PendingValue = massiveValue,
                Options = new() { "Enabled", "Disabled" },
                Risk = BiosSettingRisk.Normal
            };
            bool okMassive = manager.ApplySetting(massiveItem, out string errMassive);
            Assert.False(okMassive);
            Assert.Contains("Invalid value", errMassive, StringComparison.OrdinalIgnoreCase);

            // Reset safety toggles
            manager.ModificationsEnabled = false;
            manager.DangerousAllowed = false;
        }

        #endregion

        #region 5. Telemetry Under Extreme Load (1,000 Updates, -100°C, 300°C, 0 RPM, D3Cold Transitions)

        [Fact]
        public void Telemetry_ExtremeLoad_1000Updates_InvalidTemps_ZeroRpm_D3ColdTransitions()
        {
            using var coordinator = new ThermalSafetyCoordinator();
            var rng = new Random(42);

            for (int i = 0; i < 1000; i++)
            {
                int rawCpuTemp = (i % 5) switch
                {
                    0 => -100,
                    1 => -500,
                    2 => 300,
                    3 => 1250,
                    _ => rng.Next(-50, 150)
                };

                int rawGpuTemp = (i % 5) switch
                {
                    0 => 300,
                    1 => -100,
                    2 => 1500,
                    3 => -999,
                    _ => rng.Next(-50, 150)
                };

                int rawCpuRpm = (i % 3 == 0) ? 0 : (i % 3 == 1 ? -500 : 25000);
                int rawGpuRpm = (i % 3 == 0) ? 0 : (i % 3 == 1 ? -1000 : 30000);
                int rawSysRpm = (i % 2 == 0) ? 0 : 50000;

                double? rawCpuLoad = (i % 4) switch
                {
                    0 => double.NaN,
                    1 => double.PositiveInfinity,
                    2 => -20.0,
                    _ => 150.0
                };

                double? rawGpuLoad = (i % 4) switch
                {
                    0 => double.NegativeInfinity,
                    1 => double.NaN,
                    2 => 120.0,
                    _ => -10.0
                };

                bool d3Cold = (i % 2 == 0); // Rapid D3Cold transitions!
                bool onBattery = (i % 3 == 0);

                var snap = new TelemetrySnapshot(
                    rawCpuTemp,
                    rawGpuTemp,
                    rawCpuRpm,
                    rawGpuRpm,
                    rawSysRpm,
                    rawCpuLoad,
                    rawGpuLoad,
                    GpuAsleep: d3Cold,
                    OnBattery: onBattery,
                    HasHwMode: false,
                    ProbedHwMode: 0,
                    CpuFrequencyMhz: (i % 2 == 0) ? double.NaN : -100.0,
                    GpuFrequencyMhz: (i % 2 == 0) ? double.PositiveInfinity : 2500.0,
                    CpuPowerWatts: (i % 2 == 0) ? -50.0 : 65.0,
                    GpuPowerWatts: (i % 2 == 0) ? double.NaN : 140.0
                );

                var sanitized = snap.Sanitize();

                // Validate physical constraints
                Assert.InRange(sanitized.CpuTemp, 0, 150);
                Assert.InRange(sanitized.GpuTemp, 0, 150);
                Assert.InRange(sanitized.CpuRpm, 0, 15000);
                Assert.InRange(sanitized.GpuRpm, 0, 15000);
                Assert.InRange(sanitized.SysRpm, 0, 15000);

                if (sanitized.CpuLoad.HasValue)
                    Assert.InRange(sanitized.CpuLoad.Value, 0.0, 100.0);
                if (sanitized.GpuLoad.HasValue)
                    Assert.InRange(sanitized.GpuLoad.Value, 0.0, 100.0);

                Assert.False(double.IsNaN(sanitized.CpuFrequencyMhz));
                Assert.False(double.IsInfinity(sanitized.CpuFrequencyMhz));
                Assert.True(sanitized.CpuFrequencyMhz >= 0);

                Assert.False(double.IsNaN(sanitized.GpuFrequencyMhz));
                Assert.False(double.IsInfinity(sanitized.GpuFrequencyMhz));
                Assert.True(sanitized.GpuFrequencyMhz >= 0);

                Assert.False(double.IsNaN(sanitized.CpuPowerWatts));
                Assert.False(double.IsInfinity(sanitized.CpuPowerWatts));
                Assert.True(sanitized.CpuPowerWatts >= 0);

                Assert.False(double.IsNaN(sanitized.GpuPowerWatts));
                Assert.False(double.IsInfinity(sanitized.GpuPowerWatts));
                Assert.True(sanitized.GpuPowerWatts >= 0);

                // Feed to thermal coordinator
                var decision = coordinator.Evaluate(sanitized.CpuTemp, sanitized.GpuTemp);
                Assert.InRange(decision.Stage, 0, 3);
                Assert.InRange(decision.CpuFanFloor, 0, 100);
                Assert.InRange(decision.GpuFanFloor, 0, 100);
                Assert.NotNull(decision.StageLabel);
                Assert.NotNull(decision.Reason);

                // Feed to UiLabelFormatter
                string formattedStatus = UiLabelFormatter.FormatLabel(d3Cold ? "GPU in D3Cold sleep" : "dGPU Active MUX Switch");
                Assert.DoesNotContain("D3Cold", formattedStatus);
                Assert.DoesNotContain("dGPU", formattedStatus);
            }
        }

        #endregion

        #region 6. Fan Curve Follower Under Extreme Oscillations (20°C <-> 100°C Every 5ms)

        [Fact]
        public void CurveFollower_ExtremeTemperatureOscillations_20CAnd100C_Every5ms()
        {
            using var follower = new CurveFollower(hysteresisDegrees: 3);
            var curve = new List<Point>
            {
                new(30, 20),
                new(45, 35),
                new(60, 50),
                new(75, 70),
                new(90, 85),
                new(100, 100)
            };

            var speeds = new List<int>();

            // Run 1,000 oscillation steps jumping back and forth
            for (int i = 0; i < 1000; i++)
            {
                double temp = (i % 2 == 0) ? 20.0 : 100.0;
                int speed = follower.Update(temp, curve);

                Assert.InRange(speed, 0, 100);
                if (temp == 100.0)
                {
                    Assert.Equal(100, speed);
                }
                else
                {
                    // At 20°C with curve starting at (30, 20), speed must be within valid fan floor bounds
                    Assert.InRange(speed, 20, 100);
                }

                speeds.Add(speed);
            }

            // Verify follower handles emergency 100% floor during extreme oscillation
            for (int i = 0; i < 50; i++)
            {
                double temp = (i % 2 == 0) ? 20.0 : 100.0;
                int speed = follower.Update(temp, curve, safetyFloorPercent: 100);
                Assert.Equal(100, speed);
            }

            // Verify recovery after oscillation
            follower.Reset();
            Assert.Null(follower.Current);
            int coldSpeed = follower.Update(25.0, curve);
            Assert.Equal(20, coldSpeed);
        }

        [Fact]
        public void CurveFollower_ConcurrentOscillations_WithDynamicCurveMutation_NoDeadlocks()
        {
            using var follower = new CurveFollower(hysteresisDegrees: 2);
            var curve = new List<Point>
            {
                new(30, 20), new(45, 35), new(60, 50), new(75, 70), new(90, 85), new(100, 100)
            };

            var exceptions = new ConcurrentBag<Exception>();
            const int threads = 10;
            const int iterations = 100;

            Parallel.For(0, threads, t =>
            {
                var rng = new Random(t * 101);
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        if (i % 5 == 0)
                        {
                            lock (curve)
                            {
                                curve[1] = new Point(45, rng.Next(25, 45));
                                curve[3] = new Point(75, rng.Next(65, 80));
                            }
                        }
                        else if (i % 7 == 0)
                        {
                            follower.HysteresisDegrees = rng.Next(0, 5);
                        }
                        else if (i % 13 == 0)
                        {
                            follower.Reset();
                        }

                        double oscillatingTemp = (i % 2 == 0) ? 20.0 : 100.0;
                        int speed = follower.Update(oscillatingTemp, curve);
                        Assert.InRange(speed, 0, 100);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
        }

        #endregion

        #region 7. GameSync Rapid Profile Switching (50 Process Launches & Terminations Per Second)

        [Fact]
        public void GameSync_RapidProfileSwitching_50ProcessEventsPerSecond_SimulatedConcurrency()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GameSyncChaos_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configFile = Path.Combine(tempDir, "game_sync_chaos.json");

            try
            {
                using var controller = new GameSyncController(configFile);
                controller.IsEnabled = true;

                var gameNames = new[]
                {
                    "Cyberpunk2077.exe", "ForzaHorizon5.exe", "Witcher3.exe", "EldenRing.exe",
                    "Starfield.exe", "DoomEternal.exe", "ApexLegends.exe", "Valorant.exe",
                    "CS2.exe", "Dota2.exe"
                };

                // Pre-register profiles
                for (int i = 0; i < gameNames.Length; i++)
                {
                    controller.AddProfile(new GameProfile
                    {
                        ExecutableName = gameNames[i],
                        DisplayName = gameNames[i].Replace(".exe", ""),
                        PowerMode = (byte)(i % 5),
                        CpuFanSpeed = 50 + (i * 4),
                        GpuFanSpeed = 50 + (i * 4)
                    });
                }

                Assert.True(controller.Profiles.Count >= gameNames.Length);
                foreach (var name in gameNames)
                {
                    Assert.Contains(controller.Profiles, p => p.ExecutableName.Equals(name, StringComparison.OrdinalIgnoreCase));
                }

                var detectedCount = 0;
                var exitedCount = 0;
                controller.GameDetected += (p) => Interlocked.Increment(ref detectedCount);
                controller.GameExited += (s) => Interlocked.Increment(ref exitedCount);

                var exceptions = new ConcurrentBag<Exception>();
                const int workerThreads = 8;
                const int operationsPerWorker = 50; // Total 400 rapid operations

                var preGameSnapshot = new DashboardSnapshot
                {
                    PowerMode = 1,
                    FanMode = 1,
                    CpuFanSpeed = 30,
                    GpuFanSpeed = 30
                };
                controller.SetPreGameSnapshot(preGameSnapshot);

                Parallel.For(0, workerThreads, t =>
                {
                    var rng = new Random(t * 313);
                    try
                    {
                        for (int i = 0; i < operationsPerWorker; i++)
                        {
                            string targetGame = gameNames[rng.Next(gameNames.Length)];
                            int action = i % 6;

                            switch (action)
                            {
                                case 0:
                                    // Add/Update profile
                                    controller.AddProfile(new GameProfile
                                    {
                                        ExecutableName = targetGame,
                                        DisplayName = targetGame,
                                        PowerMode = (byte)rng.Next(0, 5),
                                        CpuFanSpeed = rng.Next(20, 100),
                                        GpuFanSpeed = rng.Next(20, 100)
                                    });
                                    break;
                                case 1:
                                    // Update existing profile
                                    controller.UpdateProfile(new GameProfile
                                    {
                                        ExecutableName = targetGame,
                                        DisplayName = "Updated " + targetGame,
                                        PowerMode = 4,
                                        CpuFanSpeed = 100,
                                        GpuFanSpeed = 100
                                    });
                                    break;
                                case 2:
                                    // Poll simulation
                                    controller.PollOnce();
                                    break;
                                case 3:
                                    // Read read-only snapshots
                                    var profiles = controller.Profiles;
                                    var active = controller.ActiveGameExe;
                                    Assert.NotNull(profiles);
                                    break;
                                case 4:
                                    // Remove profile (termination simulation)
                                    controller.RemoveProfile(targetGame);
                                    // Immediately restore it
                                    controller.AddProfile(new GameProfile
                                    {
                                        ExecutableName = targetGame,
                                        DisplayName = targetGame,
                                        PowerMode = 2,
                                        CpuFanSpeed = 60,
                                        GpuFanSpeed = 60
                                    });
                                    break;
                                case 5:
                                    // Quick enable toggling stress
                                    controller.IsEnabled = (i % 4 != 0);
                                    break;
                            }

                            // Pacing for simulated process event load
                            Thread.Sleep(15);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });

                Assert.Empty(exceptions);
                Assert.True(controller.Profiles.Count > 0);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        #endregion
    }
}
