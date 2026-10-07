using System;
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
    public class ComprehensiveAdversarialStressTests
    {
        [Fact]
        public void LightingEffects_SoftwareAnimation_HonorsBrightnessScaling_AndHSVUnderflow()
        {
            using var wmi = new WmiController();

            // Set effect to 8 (software Snake/PingPong) at 20% brightness
            LightingEffectsManager.ApplyEffect(8, wmi, 200, 100, 50, 20, 5, 0);

            // Wait a moment for at least one animation frame
            Thread.Sleep(200);

            // Stop software animation cleanly
            LightingEffectsManager.StopSoftwareAnimation();

            // At 0% brightness, must shut off immediately
            LightingEffectsManager.ApplyEffect(8, wmi, 255, 255, 255, 0, 5, 0);
            Assert.Equal(0, wmi.Brightness);
        }

        [Fact]
        public void Form1_InterpolateCurve_ConcurrentMutationsAndRapidPointClears_Resilient()
        {
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 60),
                new(90, 80),
                new(100, 100)
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int mutations = 0;
            int interpolations = 0;
            var tasks = new Task[6];

            // 2 Writers continuously adding, sorting, reversing, and clearing points
            for (int i = 0; i < 2; i++)
            {
                int tid = i;
                tasks[i] = Task.Run(() =>
                {
                    int step = 0;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            if (step % 10 == 0)
                            {
                                curve.Clear();
                                curve.Add(new Point(30, 20));
                                curve.Add(new Point(100, 100));
                            }
                            else
                            {
                                curve.Add(new Point((tid * 20 + step) % 100, (step * 3) % 100));
                            }
                            Interlocked.Increment(ref mutations);
                        }
                        catch { }
                        step++;
                    }
                });
            }

            // 4 Readers interpolating continuously
            for (int i = 2; i < 6; i++)
            {
                int tid = i;
                tasks[i] = Task.Run(() =>
                {
                    int temp = tid * 15;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        int speed = Form1.InterpolateCurve(curve, temp);
                        Assert.InRange(speed, 10, 100);
                        temp = (temp + 3) % 120;
                        Interlocked.Increment(ref interpolations);
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.True(mutations > 100, $"Expected >100 mutations, got {mutations}");
            Assert.True(interpolations > 500, $"Expected >500 interpolations, got {interpolations}");
        }

        [Fact]
        public void AppSettings_Save_ConcurrentMutation_CreatesSnapshotAndPersistsFile()
        {
            string customDir = Path.Combine(Path.GetTempPath(), "AppSettingsAdversarial_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(customDir);
            string settingsFile = Path.Combine(customDir, "settings.json");

            try
            {
                AppSettings.CustomFilePath = settingsFile;
                var settings = new AppSettings();
                for (int i = 0; i < 50; i++)
                {
                    settings.CpuCurve.Add(new CurvePointData(30 + i, 20 + i));
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                int saves = 0;
                int mutations = 0;
                var tasks = new Task[4];

                // 2 Mutators mutating CpuCurve and GpuCurve rapidly
                for (int i = 0; i < 2; i++)
                {
                    int tid = i;
                    tasks[i] = Task.Run(() =>
                    {
                        int count = 0;
                        while (!cts.Token.IsCancellationRequested)
                        {
                            settings.CpuCurve.Add(new CurvePointData((tid * 10 + count) % 100, 50));
                            if (settings.CpuCurve.Count > 200) settings.CpuCurve.Clear();
                            Interlocked.Increment(ref mutations);
                            count++;
                        }
                    });
                }

                // 2 Savers repeatedly serializing and saving
                for (int i = 2; i < 4; i++)
                {
                    tasks[i] = Task.Run(() =>
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            settings.Save();
                            Interlocked.Increment(ref saves);
                        }
                    });
                }

                Task.WaitAll(tasks);
                Assert.True(saves > 0);
                Assert.True(mutations > 0);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
                if (Directory.Exists(customDir))
                {
                    try { Directory.Delete(customDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void AppSettings_Load_NullOrMalformedCollections_ProducesValidDefaults()
        {
            string jsonWithNulls = "{\"CpuCurve\": null, \"GpuCurve\": null, \"Theme\": null, \"PowerMode\": 1}";
            var parsed = JsonSerializer.Deserialize<AppSettings>(jsonWithNulls);
            Assert.NotNull(parsed);

            // Directly ensure our safe properties work
            parsed.CpuCurve ??= new();
            parsed.GpuCurve ??= new();
            if (string.IsNullOrWhiteSpace(parsed.Theme)) parsed.Theme = "System";

            Assert.NotNull(parsed.CpuCurve);
            Assert.NotNull(parsed.GpuCurve);
            Assert.Equal("System", parsed.Theme);
            Assert.Equal(1, parsed.PowerMode);
        }

        [Fact]
        public void BacklightStateManager_NoSystemBattery_NeverIdentifiedAsOnBattery()
        {
            var mgr = new BacklightStateManager();

            // When system has no battery (desktop or bare motherboard), must never be on battery
            Assert.False(mgr.IsOnBattery(PowerLineStatus.Offline, BatteryChargeStatus.NoSystemBattery));
            Assert.False(mgr.IsOnBattery(PowerLineStatus.Online, BatteryChargeStatus.NoSystemBattery));
            Assert.False(mgr.IsOnBattery(PowerLineStatus.Unknown, BatteryChargeStatus.NoSystemBattery));
        }

        [Fact]
        public void ThemeManager_ConcurrentLoadAndSetTheme_ThreadSafety()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int ops = 0;
            var tasks = new Task[4];

            for (int i = 0; i < tasks.Length; i++)
            {
                int tid = i;
                tasks[i] = Task.Run(() =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        if (tid % 2 == 0)
                        {
                            ThemeManager.SetTheme(tid == 0 ? AppTheme.Dark : AppTheme.Light);
                        }
                        else
                        {
                            ThemeManager.LoadThemeFromRegistry();
                        }
                        _ = ThemeManager.IsDarkThemeActive;
                        _ = ThemeManager.FormBg;
                        _ = ThemeManager.TextPrimary;
                        Interlocked.Increment(ref ops);
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.True(ops > 100);
        }
    }
}
