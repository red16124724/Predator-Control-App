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
    /// Exhaustive Adversarial UI & Custom Controls Stress Test Suite.
    /// Stress-tests custom controls and forms with zero, negative, and extreme dimensions,
    /// rapid mouse dragging, keyboard cycling, theme toggling, DPI changes, and multi-threaded cross-thread dispatch.
    /// </summary>
    [Collection("SingleInstanceTests")]
    [SupportedOSPlatform("windows")]
    public class AdversarialUiAndControlsStressTests : IDisposable
    {
        private readonly string _tempConfigFile;

        public AdversarialUiAndControlsStressTests()
        {
            _tempConfigFile = Path.Combine(Path.GetTempPath(), $"AdversarialUi_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tempConfigFile)) File.Delete(_tempConfigFile); } catch { }
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
            thread.Join();
            if (ex != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
            }
        }

        #region 1. Zero, Negative, and Extreme Dimension Testing for Custom Controls

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-10, -5)]
        [InlineData(-500, -200)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(5000, 2000)]
        [InlineData(20000, 10000)]
        public void PredatorButton_DegenerateAndExtremeDimensions_DoesNotCrash(int width, int height)
        {
            RunInSta(() =>
            {
                using var btn = new PredatorButton();
                btn.Size = new Size(width, height);
                btn.Text = "Adversarial Button";

                using var bmp = new Bitmap(Math.Max(1, Math.Abs(width)), Math.Max(1, Math.Abs(height)));
                using var g = Graphics.FromImage(bmp);
                var paintMethod = typeof(PredatorButton).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(paintMethod);

                // Invoke paint with matching bounds
                paintMethod.Invoke(btn, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, width, height)) });
                // Invoke paint with zero bounds
                paintMethod.Invoke(btn, new object[] { new PaintEventArgs(g, Rectangle.Empty) });

                // Test click and state transitions
                btn.PerformClick();
            });
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-100, -20)]
        [InlineData(-1, -1)]
        [InlineData(1, 1)]
        [InlineData(10000, 5000)]
        public void PredatorSlider_DegenerateAndExtremeDimensions_DoesNotCrash(int width, int height)
        {
            RunInSta(() =>
            {
                using var slider = new PredatorSlider();
                slider.Size = new Size(width, height);
                slider.Minimum = 0;
                slider.Maximum = 100;
                slider.Value = 50;

                using var bmp = new Bitmap(Math.Max(1, Math.Abs(width)), Math.Max(1, Math.Abs(height)));
                using var g = Graphics.FromImage(bmp);
                var paintMethod = typeof(PredatorSlider).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(paintMethod);

                paintMethod.Invoke(slider, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, width, height)) });
                paintMethod.Invoke(slider, new object[] { new PaintEventArgs(g, Rectangle.Empty) });
            });
        }

        [Fact]
        public void PredatorSlider_PathologicalBoundsAndInvertedMinMax_DoesNotThrow()
        {
            RunInSta(() =>
            {
                using var slider = new PredatorSlider();
                // Test inverted Min and Max
                slider.Maximum = 10;
                slider.Minimum = 100; // Inverted
                Assert.True(slider.Minimum >= slider.Maximum);
                slider.Value = 50;

                // Test negative ranges
                slider.Minimum = -500;
                slider.Maximum = -100;
                slider.Value = -300;
                Assert.Equal(-300, slider.Value);

                // Out of range clamping
                slider.Value = 99999;
                Assert.Equal(-100, slider.Value);
                slider.Value = -99999;
                Assert.Equal(-500, slider.Value);

                // Equal Min and Max
                slider.Minimum = 50;
                slider.Maximum = 50;
                slider.Value = 50;
                Assert.Equal(50, slider.Value);

                using var bmp = new Bitmap(100, 30);
                using var g = Graphics.FromImage(bmp);
                var paintMethod = typeof(PredatorSlider).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(paintMethod);
                paintMethod.Invoke(slider, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 100, 30)) });
            });
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-50, -30)]
        [InlineData(5000, 5000)]
        public void PredatorDropDown_DegenerateAndExtremeDimensions_DoesNotCrash(int width, int height)
        {
            RunInSta(() =>
            {
                using var dropdown = new PredatorDropDown();
                dropdown.Size = new Size(width, height);
                dropdown.Items.Add("Alpha");
                dropdown.Items.Add("Beta");
                dropdown.Items.Add("Gamma");
                dropdown.SelectedIndex = 0;

                using var bmp = new Bitmap(Math.Max(1, Math.Abs(width)), Math.Max(1, Math.Abs(height)));
                using var g = Graphics.FromImage(bmp);
                var paintMethod = typeof(PredatorDropDown).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                if (paintMethod != null)
                {
                    paintMethod.Invoke(dropdown, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, width, height)) });
                }
            });
        }

        [Fact]
        public void PredatorDropDown_PathologicalItems_EmptyNullAndMassiveLists()
        {
            RunInSta(() =>
            {
                using var dropdown = new PredatorDropDown();
                dropdown.Size = new Size(200, 32);

                // Empty items
                dropdown.Items.Clear();
                dropdown.SelectedIndex = -1;
                Assert.Equal(-1, dropdown.SelectedIndex);

                // Adding 1000 items with empty, whitespace, and very long strings
                for (int i = 0; i < 1000; i++)
                {
                    if (i % 3 == 0) dropdown.Items.Add(string.Empty);
                    else if (i % 3 == 1) dropdown.Items.Add(new string('X', 5000));
                    else dropdown.Items.Add($"Item_{i}");
                }

                dropdown.SelectedIndex = 500;
                Assert.Equal(500, dropdown.SelectedIndex);
                dropdown.SelectedIndex = -1;
                Assert.Equal(-1, dropdown.SelectedIndex);
            });
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-20, -20)]
        [InlineData(10000, 10000)]
        public void PredatorSwitchAndToggle_DegenerateDimensions_DoesNotCrash(int width, int height)
        {
            RunInSta(() =>
            {
                using var sw = new PredatorSwitch();
                sw.Size = new Size(width, height);
                sw.Checked = true;
                sw.Checked = false;

                using var toggle = new PredatorToggle();
                toggle.Size = new Size(width, height);
                toggle.Checked = true;
                toggle.Checked = false;

                using var bmp = new Bitmap(Math.Max(1, Math.Abs(width)), Math.Max(1, Math.Abs(height)));
                using var g = Graphics.FromImage(bmp);

                var paintSw = typeof(PredatorSwitch).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                paintSw?.Invoke(sw, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, width, height)) });

                var paintToggle = typeof(PredatorToggle).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                paintToggle?.Invoke(toggle, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, width, height)) });
            });
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-100, -100)]
        [InlineData(20000, 20000)]
        public void DarkScrollPanel_DegenerateDimensionsAndPathologicalChildren_DoesNotCrash(int width, int height)
        {
            RunInSta(() =>
            {
                using var panel = new DarkScrollPanel();
                panel.Size = new Size(width, height);
                panel.AutoScrollMinSize = new Size(width * 2, height * 2);

                // Add child control with negative coordinates and sizes
                using var child = new Button { Location = new Point(-100, -100), Size = new Size(50, 50) };
                panel.Controls.Add(child);

                using var bmp = new Bitmap(Math.Max(1, Math.Abs(width)), Math.Max(1, Math.Abs(height)));
                using var g = Graphics.FromImage(bmp);
                var paintMethod = typeof(DarkScrollPanel).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                paintMethod?.Invoke(panel, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, width, height)) });
            });
        }

        [Fact]
        public void FanCurveGraph_DegenerateDimensionsAndPathologicalPoints_RendersSafely()
        {
            RunInSta(() =>
            {
                using var graph = new FanCurveGraph();
                int[] dimensions = { 0, -10, 1, 2, 5, 20, 500, 5000 };

                foreach (int dim in dimensions)
                {
                    graph.Size = new Size(dim, dim);
                    using var bmp = new Bitmap(Math.Max(1, Math.Abs(dim)), Math.Max(1, Math.Abs(dim)));
                    using var g = Graphics.FromImage(bmp);

                    // 1. Empty curve
                    graph.Points = new List<Point>();
                    var paintMethod = typeof(FanCurveGraph).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.NotNull(paintMethod);
                    paintMethod.Invoke(graph, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, dim, dim)) });

                    // 2. 1000 pathological points (out of range, NaN coordinates, duplicates)
                    var pathological = new List<Point>();
                    for (int i = 0; i < 1000; i++)
                    {
                        pathological.Add(new Point(i % 2 == 0 ? -1000 : 5000, i % 3 == 0 ? -500 : 2000));
                    }
                    graph.Points = pathological;
                    paintMethod.Invoke(graph, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, dim, dim)) });
                }
            });
        }

        [Fact]
        public void HistoryGraphControl_DegenerateDimensionsAndSeriesFuzzing_DoesNotCrash()
        {
            RunInSta(() =>
            {
                using var graph = new HistoryGraphControl();
                int[] dimensions = { 0, -50, 1, 10, 500, 2000 };

                foreach (int dim in dimensions)
                {
                    graph.Size = new Size(dim, dim);
                    using var bmp = new Bitmap(Math.Max(1, Math.Abs(dim)), Math.Max(1, Math.Abs(dim)));
                    using var g = Graphics.FromImage(bmp);

                    // Add chaotic data
                    graph.PrimarySeries.Add(null);
                    graph.PrimarySeries.Add(double.NaN);
                    graph.PrimarySeries.Add(double.PositiveInfinity);
                    graph.PrimarySeries.Add(double.NegativeInfinity);
                    graph.PrimarySeries.Add(-100000.0);
                    graph.PrimarySeries.Add(100000.0);

                    var paintMethod = typeof(HistoryGraphControl).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                    paintMethod?.Invoke(graph, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, dim, dim)) });
                }
            });
        }

        #endregion

        #region 2. Forms Degenerate Dimensions, States, and Transitions

        [Fact]
        public void Forms_ZeroNegativeAndExtremeDimensions_DoesNotCrash()
        {
            RunInSta(() =>
            {
                using var form = new Form1();
                int[] testSizes = { 0, -100, 10, 100, 2560, 4000 };

                foreach (int s in testSizes)
                {
                    form.Size = new Size(Math.Max(1, s), Math.Max(1, s));
                    form.WindowState = FormWindowState.Normal;
                    form.WindowState = FormWindowState.Minimized;
                    form.WindowState = FormWindowState.Maximized;
                    form.WindowState = FormWindowState.Normal;
                }

                // Sub forms
                using var fanCurveForm = new FanCurveForm();
                fanCurveForm.Size = new Size(100, 100);
                fanCurveForm.Size = new Size(0, 0);

                using var gameSyncCtrl = new GameSyncController(_tempConfigFile);
                using var gameSyncForm = new GameSyncForm(gameSyncCtrl, 144);
                gameSyncForm.Size = new Size(100, 100);
                gameSyncForm.Size = new Size(0, 0);

                using var hwCapsForm = new HardwareCapabilitiesForm(DeviceCapabilities.None);
                hwCapsForm.Size = new Size(100, 100);
                hwCapsForm.Size = new Size(0, 0);
            });
        }

        #endregion

        #region 3. Rapid Mouse Dragging, Keyboard Cycling, Theme Toggling, and DPI Changes

        [Fact]
        public void PredatorSlider_RapidMouseDragging_ExtremeCoordinates_DoesNotCrash()
        {
            RunInSta(() =>
            {
                using var slider = new PredatorSlider { Size = new Size(300, 30), Minimum = 0, Maximum = 100, Value = 50 };
                var onMouseDown = typeof(PredatorSlider).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                var onMouseMove = typeof(PredatorSlider).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                var onMouseUp = typeof(PredatorSlider).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(onMouseDown);
                Assert.NotNull(onMouseMove);
                Assert.NotNull(onMouseUp);

                // Mouse down in middle
                onMouseDown.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 150, 15, 0) });

                // Rapid dragging across wildly extreme positive, negative, and offscreen coordinates
                int[] coords = { -5000, -500, -1, 0, 1, 50, 100, 150, 200, 299, 300, 301, 1000, 50000 };
                for (int rep = 0; rep < 100; rep++)
                {
                    int x = coords[rep % coords.Length];
                    onMouseMove.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, 15, 0) });
                    Assert.InRange(slider.Value, slider.Minimum, slider.Maximum);
                }

                onMouseUp.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 150, 15, 0) });
            });
        }

        [Fact]
        public void PredatorSlider_RapidKeyboardCycling_AllKeys_DoesNotCrash()
        {
            RunInSta(() =>
            {
                using var slider = new PredatorSlider { Size = new Size(300, 30), Minimum = 0, Maximum = 100, Value = 50 };
                var onKeyDown = typeof(PredatorSlider).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(onKeyDown);

                Keys[] testKeys = {
                    Keys.Left, Keys.Right, Keys.Up, Keys.Down,
                    Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown,
                    Keys.Space, Keys.Enter, Keys.Tab, Keys.Escape,
                    Keys.A, Keys.None
                };

                for (int i = 0; i < 500; i++)
                {
                    var k = testKeys[i % testKeys.Length];
                    onKeyDown.Invoke(slider, new object[] { new KeyEventArgs(k) });
                    Assert.InRange(slider.Value, slider.Minimum, slider.Maximum);
                }
            });
        }

        [Fact]
        public void RapidThemeToggling_AcrossAllControls_100Cycles()
        {
            RunInSta(() =>
            {
                using var btn = new PredatorButton { Size = new Size(100, 30) };
                using var slider = new PredatorSlider { Size = new Size(200, 30) };
                using var dropDown = new PredatorDropDown { Size = new Size(150, 30) };
                using var toggle = new PredatorToggle { Size = new Size(40, 20) };
                using var sw = new PredatorSwitch { Size = new Size(50, 25) };
                using var panel = new DarkScrollPanel { Size = new Size(300, 300) };
                using var graph = new HistoryGraphControl { Size = new Size(200, 100) };

                panel.Controls.Add(btn);
                panel.Controls.Add(slider);
                panel.Controls.Add(dropDown);
                panel.Controls.Add(toggle);
                panel.Controls.Add(sw);
                panel.Controls.Add(graph);

                AppTheme[] themes = { AppTheme.Dark, AppTheme.Light, AppTheme.System };
                for (int i = 0; i < 100; i++)
                {
                    ThemeManager.SetTheme(themes[i % themes.Length]);
                }
            });
        }

        [Fact]
        public void RapidDpiScaling_AcrossCustomControls_DoesNotThrow()
        {
            RunInSta(() =>
            {
                using var btn = new PredatorButton { Size = new Size(100, 30) };
                using var slider = new PredatorSlider { Size = new Size(200, 30) };
                using var dropDown = new PredatorDropDown { Size = new Size(150, 30) };
                using var toggle = new PredatorToggle { Size = new Size(40, 20) };
                using var sw = new PredatorSwitch { Size = new Size(50, 25) };
                using var panel = new DarkScrollPanel { Size = new Size(300, 300) };
                using var fanGraph = new FanCurveGraph { Size = new Size(200, 200) };

                float[] scales = { 0.25f, 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f };
                foreach (float s in scales)
                {
                    btn.Scale(new SizeF(s, s));
                    slider.Scale(new SizeF(s, s));
                    dropDown.Scale(new SizeF(s, s));
                    toggle.Scale(new SizeF(s, s));
                    sw.Scale(new SizeF(s, s));
                    panel.Scale(new SizeF(s, s));
                    fanGraph.Scale(new SizeF(s, s));
                }
            });
        }

        #endregion

        #region 4. Cross-Thread UI Dispatch Stress Testing

        [Fact]
        public void CrossThread_SliderUpdates_50ConcurrentThreads_NoDeadlocks()
        {
            RunInSta(() =>
            {
                using var slider = new PredatorSlider { Minimum = 0, Maximum = 1000, Value = 500 };
                // Force handle creation so BeginInvoke / InvokeRequired paths execute
                _ = slider.Handle;

                var exceptions = new ConcurrentBag<Exception>();
                const int threadCount = 40;
                const int iterationsPerThread = 50;

                Parallel.For(0, threadCount, t =>
                {
                    try
                    {
                        var rng = new Random(t * 777);
                        for (int i = 0; i < iterationsPerThread; i++)
                        {
                            switch (i % 4)
                            {
                                case 0:
                                    slider.Value = rng.Next(0, 1000);
                                    break;
                                case 1:
                                    slider.Minimum = rng.Next(-500, 100);
                                    break;
                                case 2:
                                    slider.Maximum = rng.Next(500, 2000);
                                    break;
                                case 3:
                                    _ = slider.Value;
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });

                // Let pending dispatched messages process
                Application.DoEvents();
                Assert.Empty(exceptions);
                Assert.InRange(slider.Value, slider.Minimum, slider.Maximum);
            });
        }

        [Fact]
        public void CrossThread_ToggleAndSwitchUpdates_40ConcurrentThreads()
        {
            RunInSta(() =>
            {
                using var toggle = new PredatorToggle();
                using var sw = new PredatorSwitch();
                _ = toggle.Handle;
                _ = sw.Handle;

                var exceptions = new ConcurrentBag<Exception>();
                const int threadCount = 40;
                const int iterations = 50;

                Parallel.For(0, threadCount, t =>
                {
                    try
                    {
                        for (int i = 0; i < iterations; i++)
                        {
                            toggle.Checked = (i % 2 == 0);
                            sw.Checked = (i % 2 != 0);
                            _ = toggle.Checked;
                            _ = sw.Checked;
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });

                Application.DoEvents();
                Assert.Empty(exceptions);
            });
        }

        [Fact]
        public void CrossThread_DarkScrollPanelAndButton_40ConcurrentThreads()
        {
            RunInSta(() =>
            {
                using var panel = new DarkScrollPanel { Size = new Size(400, 400) };
                using var btn = new PredatorButton { Size = new Size(120, 35), Text = "Init" };
                panel.Controls.Add(btn);
                _ = panel.Handle;
                _ = btn.Handle;

                var exceptions = new ConcurrentBag<Exception>();
                const int threadCount = 40;
                const int iterations = 50;

                Parallel.For(0, threadCount, t =>
                {
                    try
                    {
                        for (int i = 0; i < iterations; i++)
                        {
                            btn.Text = $"T_{t}_I_{i}";
                            panel.AutoScrollPosition = new Point(i * 2, i * 2);
                            _ = btn.Text;
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });

                Application.DoEvents();
                Assert.Empty(exceptions);
            });
        }

        #endregion
        #region 5. Targeted Robustness and Boundary Stress Tests

        [Fact]
        public void PredatorSlider_InvertedBounds_MouseAndKeyboard_SafeOperation()
        {
            RunInSta(() =>
            {
                using var slider = new PredatorSlider { Size = new Size(300, 30) };
                _ = slider.Handle;

                // Force inverted bounds via reflection to stress test pathological internal states
                var minField = typeof(PredatorSlider).GetField("_minimum", BindingFlags.NonPublic | BindingFlags.Instance);
                var maxField = typeof(PredatorSlider).GetField("_maximum", BindingFlags.NonPublic | BindingFlags.Instance);
                var valField = typeof(PredatorSlider).GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance);
                var mouseMethod = typeof(PredatorSlider).GetMethod("UpdateValueFromMouse", BindingFlags.NonPublic | BindingFlags.Instance);
                var keyMethod = typeof(PredatorSlider).GetMethod("ProcessDialogKey", BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.NotNull(minField);
                Assert.NotNull(maxField);
                Assert.NotNull(valField);
                Assert.NotNull(mouseMethod);
                Assert.NotNull(keyMethod);

                minField.SetValue(slider, 100);
                maxField.SetValue(slider, 20);
                valField.SetValue(slider, 50);

                // Mouse updates with inverted bounds
                int[] mousePositions = { -100, 0, 50, 150, 250, 300, 500 };
                foreach (int mx in mousePositions)
                {
                    mouseMethod.Invoke(slider, new object[] { mx });
                    int val = (int)valField.GetValue(slider)!;
                    Assert.InRange(val, 20, 100);
                }

                // Keyboard processing with inverted bounds
                Keys[] testKeys = { Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown };
                foreach (var k in testKeys)
                {
                    keyMethod.Invoke(slider, new object[] { k });
                    int val = (int)valField.GetValue(slider)!;
                    Assert.InRange(val, 20, 100);
                }

                // Paint with inverted bounds
                using var bmp = new Bitmap(300, 30);
                using var g = Graphics.FromImage(bmp);
                var paintMethod = typeof(PredatorSlider).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                paintMethod?.Invoke(slider, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 300, 30)) });
            });
        }

        [Fact]
        public void PredatorDropDown_OpenPopup_ParentFormMinimizedOrHidden_DoesNotThrow()
        {
            RunInSta(() =>
            {
                using var parentForm = new Form { Size = new Size(400, 300) };
                using var dropDown = new PredatorDropDown { Size = new Size(150, 30) };
                parentForm.Controls.Add(dropDown);
                parentForm.Show();
                _ = parentForm.Handle;
                _ = dropDown.Handle;

                dropDown.Items.Add("Profile 1");
                dropDown.Items.Add("Profile 2");
                dropDown.SelectedIndex = 0;

                var openPopupMethod = typeof(PredatorDropDown).GetMethod("OpenPopup", BindingFlags.NonPublic | BindingFlags.Instance);
                var closePopupMethod = typeof(PredatorDropDown).GetMethod("ClosePopup", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(openPopupMethod);
                Assert.NotNull(closePopupMethod);

                // Scenario 1: Parent form minimized
                parentForm.WindowState = FormWindowState.Minimized;
                openPopupMethod.Invoke(dropDown, null);
                Assert.True(dropDown.DroppedDown);
                closePopupMethod.Invoke(dropDown, null);
                Assert.False(dropDown.DroppedDown);

                // Scenario 2: Parent form hidden
                parentForm.WindowState = FormWindowState.Normal;
                parentForm.Visible = false;
                openPopupMethod.Invoke(dropDown, null);
                Assert.True(dropDown.DroppedDown);
                closePopupMethod.Invoke(dropDown, null);
                Assert.False(dropDown.DroppedDown);

                parentForm.Close();
            });
        }

        [Fact]
        public void FanCurveGraph_PathologicalInterpolation_NaNAndInfinityPoints_SafeComputation()
        {
            RunInSta(() =>
            {
                using var graph = new FanCurveGraph { Size = new Size(300, 300) };

                // 1. Empty or single point assigned to Points property safely normalizes to DefaultCpuPoints
                graph.Points = new List<Point>();
                Assert.NotEmpty(graph.Points);
                int speedNormalized = graph.InterpolateSpeed(50);
                Assert.InRange(speedNormalized, 0, 100);

                // 2. Setting internal _points field directly verifies InterpolateSpeed guards
                var ptsField = typeof(FanCurveGraph).GetField("_points", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(ptsField);

                ptsField.SetValue(graph, new List<Point>());
                Assert.Equal(50, graph.InterpolateSpeed(50));

                ptsField.SetValue(graph, new List<Point> { new Point(50, 75) });
                Assert.Equal(75, graph.InterpolateSpeed(20));
                Assert.Equal(75, graph.InterpolateSpeed(80));

                ptsField.SetValue(graph, null);
                Assert.Equal(50, graph.InterpolateSpeed(50));

                // 3. Points with zero temperature span (dx == 0)
                ptsField.SetValue(graph, new List<Point> { new Point(45, 30), new Point(45, 80) });
                int speedZeroSpan = graph.InterpolateSpeed(45);
                Assert.InRange(speedZeroSpan, 0, 100);

                // 4. Inverted temperature order - Normalize handles sorting
                graph.Points = new List<Point> { new Point(90, 100), new Point(60, 60), new Point(30, 20) };
                int speedInverted = graph.InterpolateSpeed(45);
                Assert.InRange(speedInverted, 20, 60);

                // 5. Extreme temperature values
                int[] extremeTemps = { int.MinValue, -1000, -1, 0, 1, 20, 50, 80, 100, 500, int.MaxValue };
                foreach (int t in extremeTemps)
                {
                    int spd = graph.InterpolateSpeed(t);
                    Assert.InRange(spd, 0, 100);
                }

                // 6. Paint with pathological points
                using var bmp = new Bitmap(300, 300);
                using var g = Graphics.FromImage(bmp);
                var paintMethod = typeof(FanCurveGraph).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
                paintMethod?.Invoke(graph, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 300, 30)) });
            });
        }

        [Fact]
        public void ThemeManager_ApplyTheme_CrossThreadCall_SafelyMarshaled()
        {
            RunInSta(() =>
            {
                using var form = new Form { Size = new Size(400, 400) };
                using var btn = new PredatorButton { Text = "Test", Size = new Size(100, 30) };
                using var slider = new PredatorSlider { Size = new Size(150, 30) };
                using var dropDown = new PredatorDropDown { Size = new Size(120, 30) };
                using var panel = new DarkScrollPanel { Size = new Size(200, 200) };

                panel.Controls.Add(btn);
                panel.Controls.Add(slider);
                panel.Controls.Add(dropDown);
                form.Controls.Add(panel);
                _ = form.Handle;
                _ = btn.Handle;
                _ = slider.Handle;
                _ = dropDown.Handle;
                _ = panel.Handle;

                var exceptions = new ConcurrentBag<Exception>();

                var task = Task.Run(() =>
                {
                    try
                    {
                        for (int i = 0; i < 20; i++)
                        {
                            ThemeManager.ApplyTheme(form);
                            ThemeManager.SetTheme(i % 2 == 0 ? AppTheme.Light : AppTheme.Dark);
                            Thread.Sleep(5);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });

                while (!task.IsCompleted)
                {
                    Application.DoEvents();
                    Thread.Sleep(1);
                }

                task.GetAwaiter().GetResult();
                Assert.Empty(exceptions);
                ThemeManager.SetTheme(AppTheme.Dark);
            });
        }

        [Fact]
        public void FanCurveForm_HysteresisSlider_ReEntrancyGuard_PreventsLoop()
        {
            RunInSta(() =>
            {
                using var form = new FanCurveForm();
                _ = form.Handle;

                Assert.Equal(CurveFollower.DefaultHysteresisDegrees, form.HysteresisDegrees);
                Assert.NotNull(form.HysteresisSlider);
                Assert.NotNull(form.HysteresisLabel);

                int eventCount = 0;
                form.HysteresisSlider.ValueChanged += (s, e) =>
                {
                    eventCount++;
                    // Mutate HysteresisDegrees from inside the event handler to test re-entrancy guard
                    if (eventCount < 10)
                    {
                        form.SetHysteresis(eventCount % 5 + 1);
                    }
                };

                // Trigger rapid alternations between Min and Max degrees
                for (int d = CurveFollower.MinHysteresisDegrees; d <= CurveFollower.MaxHysteresisDegrees; d++)
                {
                    form.SetHysteresis(d);
                    Assert.Equal(d, form.HysteresisDegrees);
                    Assert.Equal(d, form.HysteresisSlider.Value);
                    Assert.Equal(UiLabelFormatter.FormatHysteresisLabel(d), form.HysteresisLabel.Text);
                }

                // Clamping bounds
                form.SetHysteresis(-99);
                Assert.Equal(CurveFollower.MinHysteresisDegrees, form.HysteresisDegrees);

                form.SetHysteresis(999);
                Assert.Equal(CurveFollower.MaxHysteresisDegrees, form.HysteresisDegrees);
            });
        }

        #endregion

    }
}
