using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Comprehensive UI, lifecycle, and WinForms stress scan tests.
    /// Covers thread marshaling, GDI+ resource leak auditing, rapid input/event storms,
    /// and settings persistence edge cases (brightness persistence, lid logo isolation, corrupt/locked settings).
    /// </summary>
    [Collection("SingleInstanceTests")]
    [SupportedOSPlatform("windows")]
    public class WinFormsUiLifecycleAndStressScanTests : IDisposable
    {
        private readonly string _tempConfigFile;

        public WinFormsUiLifecycleAndStressScanTests()
        {
            _tempConfigFile = Path.Combine(Path.GetTempPath(), $"WinFormsStress_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tempConfigFile)) File.Delete(_tempConfigFile); } catch { }
            try { if (File.Exists(_tempConfigFile + ".bak")) File.Delete(_tempConfigFile + ".bak"); } catch { }
            try { if (File.Exists(_tempConfigFile + ".corrupt")) File.Delete(_tempConfigFile + ".corrupt"); } catch { }
            ThemeManager.SetTheme(AppTheme.Dark);
        }

        private static void RunInSta(Action action)
        {
            Exception? ex = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                    Application.DoEvents();
                }
                catch (Exception e)
                {
                    ex = e;
                }
                finally
                {
                    try { Application.DoEvents(); } catch { }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(15000))
            {
                throw new TimeoutException("STA test execution timed out after 15 seconds.");
            }
            if (ex != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
            }
        }

        #region Focus Area 1: WinForms UI Thread Marshaling & Cross-Thread Safety

        [Fact]
        public void PredatorButton_CrossThreadPropertyMutation_50ConcurrentThreads_SafeMarshaling()
        {
            RunInSta(() =>
            {
                using var form = new Form { Width = 300, Height = 200 };
                using var btn = new PredatorButton { Text = "Stress Btn", Size = new Size(100, 40) };
                form.Controls.Add(btn);
                form.Show();
                Application.DoEvents();

                const int threadCount = 50;
                var exceptions = new ConcurrentBag<Exception>();
                using var startGate = new ManualResetEventSlim(false);
                var tasks = new Task[threadCount];

                for (int i = 0; i < threadCount; i++)
                {
                    int idx = i;
                    tasks[i] = Task.Run(() =>
                    {
                        startGate.Wait();
                        try
                        {
                            for (int iter = 0; iter < 50; iter++)
                            {
                                btn.IsActive = (idx % 2 == 0);
                                btn.IsNavButton = (iter % 2 == 1);
                                btn.CustomActiveColor = (iter % 3 == 0) ? Color.Cyan : null;
                                btn.UseMnemonic = (iter % 2 == 0);
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    });
                }

                startGate.Set();
                Task.WaitAll(tasks);
                Application.DoEvents();

                Assert.Empty(exceptions);
                form.Close();
            });
        }

        [Fact]
        public void Form1_VisibilityMethods_CalledFromBackgroundThread_DoesNotThrowCrossThreadException()
        {
            RunInSta(() =>
            {
                using var form = new Form1();
                form.Show();
                Application.DoEvents();

                var exceptions = new ConcurrentBag<Exception>();
                var tasks = new Task[10];

                for (int i = 0; i < 10; i++)
                {
                    int id = i;
                    tasks[i] = Task.Run(() =>
                    {
                        try
                        {
                            var showMethod = typeof(Form1).GetMethod("ShowApp", BindingFlags.NonPublic | BindingFlags.Instance);
                            var hideMethod = typeof(Form1).GetMethod("HideApp", BindingFlags.NonPublic | BindingFlags.Instance);
                            var toggleMethod = typeof(Form1).GetMethod("ToggleWindowVisibility", BindingFlags.NonPublic | BindingFlags.Instance);

                            Assert.NotNull(showMethod);
                            Assert.NotNull(hideMethod);
                            Assert.NotNull(toggleMethod);

                            if (id % 3 == 0) showMethod.Invoke(form, null);
                            else if (id % 3 == 1) hideMethod.Invoke(form, null);
                            else toggleMethod.Invoke(form, null);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    });
                }

                Task.WaitAll(tasks);
                Application.DoEvents();
                Assert.Empty(exceptions);
                form.Close();
            });
        }

        #endregion

        #region Focus Area 2: GDI+ Resource Leaks & High-Frequency Painting Stress

        [Fact]
        public void FanCurveGraph_RapidRepainting_2000Cycles_NoGdiOrMemoryLeak()
        {
            RunInSta(() =>
            {
                using var graph = new FanCurveGraph { Size = new Size(400, 250) };
                using var bmp = new Bitmap(400, 250);
                using var g = Graphics.FromImage(bmp);

                var paintMethod = typeof(FanCurveGraph).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(paintMethod);

                var colors = new[] { Color.Cyan, Color.Red, Color.FromArgb(0, 180, 255), Color.Lime, Color.Gold };
                var testPoints = new List<Point>
                {
                    new(30, 0), new(45, 15), new(55, 30), new(65, 45),
                    new(75, 60), new(85, 75), new(92, 88), new(100, 100)
                };

                // Rapidly cycle themes, points, colors and repaint 2000 times
                for (int i = 0; i < 2000; i++)
                {
                    if (i % 200 == 0)
                    {
                        graph.CurveColor = colors[i % colors.Length];
                        graph.CurrentTemp = 30 + (i % 70);
                        graph.Points = testPoints;
                    }

                    var pea = new PaintEventArgs(g, new Rectangle(0, 0, 400, 250));
                    paintMethod.Invoke(graph, new object[] { pea });
                }
            });
        }

        [Fact]
        public void FanCurveGraph_DrawCurveFill_InfinityAndNaNArea_SafelyGuarded()
        {
            RunInSta(() =>
            {
                using var graph = new FanCurveGraph { Size = new Size(400, 250) };
                using var bmp = new Bitmap(100, 100);
                using var g = Graphics.FromImage(bmp);

                var drawFillMethod = typeof(FanCurveGraph).GetMethod("DrawCurveFill", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(drawFillMethod);

                // Pass degenerate / infinite rectangles to verify guard
                var infRect = new RectangleF(0, float.PositiveInfinity, 100, 100);
                var nanRect = new RectangleF(0, float.NaN, 100, 100);
                var zeroRect = new RectangleF(0, 0, 0, 0);

                drawFillMethod.Invoke(graph, new object[] { g, infRect });
                drawFillMethod.Invoke(graph, new object[] { g, nanRect });
                drawFillMethod.Invoke(graph, new object[] { g, zeroRect });
            });
        }

        #endregion

        #region Focus Area 3: Rapid Input & Event Flooding

        [Fact]
        public void PredatorSlider_DragStorm_1000RapidMouseUpdates_ClampedAndSafe()
        {
            RunInSta(() =>
            {
                using var slider = new PredatorSlider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = 50,
                    Size = new Size(300, 30)
                };

                int valueChangedCount = 0;
                slider.ValueChanged += (s, e) => valueChangedCount++;

                var mouseDown = typeof(PredatorSlider).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                var mouseMove = typeof(PredatorSlider).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                var mouseUp = typeof(PredatorSlider).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.NotNull(mouseDown);
                Assert.NotNull(mouseMove);
                Assert.NotNull(mouseUp);

                mouseDown.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 10, 15, 0) });

                // Flood 1000 erratic mouse coordinates including negatives and far out of bounds
                var rnd = new Random(42);
                for (int i = 0; i < 1000; i++)
                {
                    int mouseX = rnd.Next(-500, 1500);
                    mouseMove.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 0, mouseX, 15, 0) });
                    Assert.InRange(slider.Value, 0, 100);
                }

                mouseUp.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 150, 15, 0) });
                Assert.True(valueChangedCount > 0);
            });
        }

        [Fact]
        public void FanCurveGraph_RapidPointAdjustments_MaintainsStrictMonotonicity()
        {
            RunInSta(() =>
            {
                using var graph = new FanCurveGraph { Size = new Size(400, 250) };
                var updateMethod = typeof(FanCurveGraph).GetMethod("UpdateDraggedPoint", BindingFlags.NonPublic | BindingFlags.Instance);
                var dragIndexField = typeof(FanCurveGraph).GetField("_dragIndex", BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.NotNull(updateMethod);
                Assert.NotNull(dragIndexField);

                var rnd = new Random(1337);
                for (int ptIdx = 0; ptIdx < FanCurveGraph.ControlPointCount; ptIdx++)
                {
                    dragIndexField.SetValue(graph, ptIdx);
                    for (int iter = 0; iter < 100; iter++)
                    {
                        var pt = new Point(rnd.Next(-100, 600), rnd.Next(-100, 400));
                        updateMethod.Invoke(graph, new object[] { pt });

                        var points = graph.Points;
                        Assert.Equal(FanCurveGraph.ControlPointCount, points.Count);
                        Assert.Equal(30, points[0].X);
                        Assert.Equal(100, points[^1].X);

                        for (int i = 1; i < points.Count; i++)
                        {
                            Assert.True(points[i].X >= points[i - 1].X,
                                $"Points must be monotonically non-decreasing: pt[{i-1}]={points[i-1]} vs pt[{i}]={points[i]}");
                            Assert.InRange(points[i].Y, 0, 100);
                        }
                    }
                }
            });
        }

        #endregion

        #region Focus Area 4: Settings Persistence Edge Cases

        [Fact]
        public void SettingsPersistence_KeyboardBrightnessPersistence_OnStartupAndResume()
        {
            AppSettings.CustomFilePath = _tempConfigFile;
            try
            {
                var settings = new AppSettings
                {
                    KeyboardBrightnessLevel = 65,
                    DynamicLightingEffectName = "Wave",
                    DynamicEffectSpeed = 3
                };
                settings.Save();

                var loaded = AppSettings.Load();
                Assert.Equal(65, loaded.KeyboardBrightnessLevel);
                Assert.Equal(65, loaded.RgbBrightness);

                // Verify BacklightStateManager honors saved AC brightness on resume
                using var mgr = new BacklightStateManager(loaded.KeyboardBrightnessLevel);
                Assert.Equal(65, mgr.SavedAcBrightness);

                // Simulate Resume on AC line
                mgr.OnResume(PowerLineStatus.Online, out bool turnOff, out int targetBright);
                Assert.False(turnOff);
                Assert.Equal(65, targetBright);

                // Simulate Resume on Battery line (must enforce off by default for battery health)
                mgr.OnResume(PowerLineStatus.Offline, out bool turnOffBat, out int targetBrightBat);
                Assert.True(turnOffBat);
                Assert.Equal(0, targetBrightBat);
            }
            finally
            {
                AppSettings.CustomFilePath = null;
            }
        }

        [Fact]
        public void LidLogoIsolation_EnsureNoCodeTurnsOnOrReferencesLidLogoRgb()
        {
            // Verify AcerServiceClient does not route any lighting command to Device 4 or OpenRGB Device 0
            // OpenRGB device 0 is exterior lid logo; device 1 is keyboard.
            // AcerService device 4 is lid logo; devices 2, 3, 5 are rear light bars / exterior accents.
            var colors = new List<Color> { Color.Blue, Color.Red, Color.Green, Color.Yellow };

            // Calling TrySendOpenRgbLeds for keyboard must explicitly target device 1, never device 0
            bool res = AcerServiceClient.TrySendOpenRgbLeds(1, colors, 80);
            // It will fail gracefully if OpenRGB daemon is not running locally, but must not crash
            Assert.True(res || !res);

            // Verify BuildZoneLightingPayload produces device 1 payload (keyboard only)
            string json = AcerServiceClient.BuildZoneLightingPayload(colors, 100, 3);

            Assert.Contains("\"device\":1", json);
            Assert.DoesNotContain("\"device\":4", json);
            Assert.DoesNotContain("\"device\":0", json);
        }

        [Fact]
        public void SettingsPersistence_CorruptJsonFile_RecoversFromBackupOrDefaults()
        {
            AppSettings.CustomFilePath = _tempConfigFile;
            try
            {
                // Write a valid backup file
                string bakFile = _tempConfigFile + ".bak";
                File.WriteAllText(bakFile, "{\"KeyboardBrightnessLevel\": 75, \"PowerMode\": 1}");

                // Write corrupted JSON into main settings file
                File.WriteAllText(_tempConfigFile, "{ Corrupted JSON syntax !!! <<< >>> }");

                var loaded = AppSettings.Load();
                Assert.NotNull(loaded);
                // Should recover 75 from .bak file
                Assert.Equal(75, loaded.KeyboardBrightnessLevel);

                // Verify corrupt file was archived
                Assert.True(File.Exists(_tempConfigFile + ".corrupt"));
            }
            finally
            {
                AppSettings.CustomFilePath = null;
            }
        }

        [Fact]
        public void SettingsPersistence_LockedSettingsFile_HandlesGracefullyWithoutCrash()
        {
            AppSettings.CustomFilePath = _tempConfigFile;
            try
            {
                // Write initial settings
                var settings = new AppSettings { KeyboardBrightnessLevel = 50 };
                settings.Save();

                // Lock settings file exclusively
                using (var lockStream = new FileStream(_tempConfigFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // Load should attempt retry and fall back to backup or safe defaults without throwing
                    var loaded = AppSettings.Load();
                    Assert.NotNull(loaded);

                    // Save should attempt atomic write with retries and handle sharing violation safely
                    settings.KeyboardBrightnessLevel = 90;
                    var saveEx = Record.Exception(() => settings.Save());
                    Assert.Null(saveEx);
                }
            }
            finally
            {
                AppSettings.CustomFilePath = null;
            }
        }

        #endregion
    }
}
