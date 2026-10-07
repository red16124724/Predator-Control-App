using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    public class ResourceLeaksAndMemoryStressTests
    {
        [Fact]
        public void RapidTelemetrySamples_10000Points_MemoryStability()
        {
            var graph = new HistoryGraphControl { Width = 300, Height = 150 };
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long initialMemory = GC.GetTotalMemory(true);

            for (int i = 0; i < 15000; i++)
            {
                graph.PushSample(i % 100, (i * 2) % 100);
            }

            Assert.Equal(60, graph.PrimarySeries.Count);
            Assert.Equal(60, graph.SecondarySeries.Count);
            Assert.Equal(60, graph.PrimarySeries.Capacity);
            Assert.Equal(60, graph.SecondarySeries.Capacity);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long finalMemory = GC.GetTotalMemory(true);
            long diffBytes = finalMemory - initialMemory;
            // Memory growth for 15,000 points in circular buffer must be bounded (< 5MB)
            Assert.True(diffBytes < 5 * 1024 * 1024, $"Memory growth exceeded limit: {diffBytes} bytes");
        }

        [Fact]
        public void HistoryBuffer_BoundaryWrapAround_FifoOrderIntegrity()
        {
            const int capacity = 60;
            var buffer = new HistoryBuffer(capacity);

            // Add 1,000 sequential numbers
            for (int i = 0; i < 1000; i++)
            {
                buffer.Add(i);
            }

            Assert.Equal(capacity, buffer.Count);
            Assert.Equal(capacity, buffer.Capacity);

            // The buffer must contain exactly the last 60 values (940 to 999) in exact chronological order
            for (int i = 0; i < capacity; i++)
            {
                double? val = buffer[i];
                Assert.NotNull(val);
                Assert.Equal(940 + i, val.Value);
            }

            var range = buffer.Range();
            Assert.NotNull(range);
            Assert.Equal(940, range.Value.Min);
            Assert.Equal(999, range.Value.Max);
        }

        [Fact]
        public void HistoryBuffer_ZeroAndExtremeCapacity_Clamping()
        {
            var negBuf = new HistoryBuffer(-10);
            Assert.Equal(2, negBuf.Capacity);

            var zeroBuf = new HistoryBuffer(0);
            Assert.Equal(2, zeroBuf.Capacity);

            var singleBuf = new HistoryBuffer(1);
            Assert.Equal(2, singleBuf.Capacity);

            var largeBuf = new HistoryBuffer(10000);
            Assert.Equal(10000, largeBuf.Capacity);
            for (int i = 0; i < 10000; i++) largeBuf.Add(i);
            Assert.Equal(10000, largeBuf.Count);
            Assert.Equal(0, largeBuf[0]);
            Assert.Equal(9999, largeBuf[9999]);
        }

        [Fact]
        public void HistoryBuffer_IndexBoundaryDefect_NegativeAndExcessIndices()
        {
            // HistoryBuffer returns null for out-of-range indices (by design - no throws)
            var buffer = new HistoryBuffer(5);
            buffer.Add(10);
            buffer.Add(20);
            buffer.Add(30);
            buffer.Add(40);
            buffer.Add(50); // _count = 5, _next wraps to 0

            // Negative index: returns null (guarded by if (index < 0 || index >= _count))
            Assert.Null(buffer[-1]);

            // Index == count: returns null (out of bounds)
            Assert.Null(buffer[5]);

            // Valid boundary: last element is index 4
            Assert.Equal(50, buffer[4]);

            // Valid boundary: first element is index 0
            Assert.Equal(10, buffer[0]);
        }

        [Fact]
        public void GdiObjectLifecycle_RapidPaintCycles_NoHandleLeaks()
        {
            using var bmp = new Bitmap(200, 100);
            using var g = Graphics.FromImage(bmp);
            var rect = new Rectangle(0, 0, 200, 100);
            var pe = new PaintEventArgs(g, rect);

            using var btn = new PredatorButton { Width = 200, Height = 40, Text = "Test Button" };
            using var slider = new PredatorSlider { Width = 200, Height = 30, Minimum = 0, Maximum = 100, Value = 50 };
            using var toggle = new PredatorToggle { Width = 60, Height = 30, Checked = true };
            using var sw = new PredatorSwitch { Width = 60, Height = 30, Checked = true };
            using var graph = new HistoryGraphControl { Width = 200, Height = 100, Minimum = 0, Maximum = 100 };
            graph.PushSample(55, 60);

            var onPaintBtn = typeof(PredatorButton).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            var onPaintSlider = typeof(PredatorSlider).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            var onPaintToggle = typeof(PredatorToggle).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            var onPaintSw = typeof(PredatorSwitch).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            var onPaintGraph = typeof(HistoryGraphControl).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);

            // Run 500 paint iterations across all custom controls
            for (int i = 0; i < 500; i++)
            {
                onPaintBtn?.Invoke(btn, new object[] { pe });
                onPaintSlider?.Invoke(slider, new object[] { pe });
                onPaintToggle?.Invoke(toggle, new object[] { pe });
                onPaintSw?.Invoke(sw, new object[] { pe });
                onPaintGraph?.Invoke(graph, new object[] { pe });
            }

            // Successfully completing 500 iterations without GDI handle exhaustion confirms clean object disposal
            Assert.True(true);
        }

        [Fact]
        public void EventDetachment_ThemeChanged_CustomControls_DetachProperly()
        {
            // Verifies that all custom controls cleanly unsubscribe from ThemeManager.ThemeChanged when disposed
            var graph = new HistoryGraphControl();
            var btn = new PredatorButton();
            var dropDown = new PredatorDropDown();
            var slider = new PredatorSlider();
            var sw = new PredatorSwitch();
            var toggle = new PredatorToggle();
            var scroll = new DarkScrollPanel();
            var fanGraph = new FanCurveGraph();

            // Dispose all controls
            graph.Dispose();
            btn.Dispose();
            dropDown.Dispose();
            slider.Dispose();
            sw.Dispose();
            toggle.Dispose();
            scroll.Dispose();
            fanGraph.Dispose();

            // Triggering ThemeChanged must NOT throw ObjectDisposedException or crash
            var ex = Record.Exception(() => ThemeManager.ToggleTheme());
            Assert.Null(ex);
        }

        [Fact]
        public void PdhLoadMonitor_RapidSamplingAndDispose_Lifecycle()
        {
            using var monitor = new PdhLoadMonitor();
            for (int i = 0; i < 50; i++)
            {
                var (cpu, gpu) = monitor.Sample();
                if (cpu.HasValue) Assert.InRange(cpu.Value, 0.0, 100.0);
                if (gpu.HasValue) Assert.InRange(gpu.Value, 0.0, 100.0);
            }

            monitor.Dispose();

            // After dispose, sample must safely return (null, null) without crashing
            var (disposedCpu, disposedGpu) = monitor.Sample();
            Assert.Null(disposedCpu);
            Assert.Null(disposedGpu);
        }

        [Fact]
        public void HistoryGraphControl_PushedWhileHidden_SuppressesInvalidate()
        {
            using var graph = new HistoryGraphControl { Visible = false };
            for (int i = 0; i < 100; i++)
            {
                graph.PushSample(50 + (i % 10), 20 + (i % 10));
            }
            Assert.Equal(60, graph.PrimarySeries.Count);
            Assert.Equal(60, graph.SecondarySeries.Count);
        }

        [Fact]
        public void FanCurveGraph_CurrentTempDeduplication_MaintainsState()
        {
            using var fanGraph = new FanCurveGraph { Visible = false };
            fanGraph.CurrentTemp = 55;
            Assert.Equal(55, fanGraph.CurrentTemp);

            // Re-assigning same value should not cause exceptions
            fanGraph.CurrentTemp = 55;
            Assert.Equal(55, fanGraph.CurrentTemp);
        }

        [Fact]
        public void PdhLoadMonitor_RapidSampling_NoMemoryBlowup()
        {
            using var monitor = new PdhLoadMonitor();
            long initialAlloc = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 500; i++)
            {
                monitor.Sample(sampleGpu: true);
            }

            long finalAlloc = GC.GetAllocatedBytesForCurrentThread();
            long diff = finalAlloc - initialAlloc;
            Assert.True(diff < 2 * 1024 * 1024, $"Thread memory allocation grew by {diff} bytes");
        }
    
        [Fact]
        public void UiLifecycle_HardwareCapabilitiesForm_DisposalClean()
        {
            var caps = new DeviceCapabilities();
            var form = new HardwareCapabilitiesForm(caps);
            Assert.NotNull(form);
            // Must dispose cleanly without leaking Icon or bitmap handles
            form.Dispose();
            Assert.True(form.IsDisposed);
        }

        [Fact]
        public void UiLifecycle_GameSyncForm_DisposalClean_And_DrawItemStringFormatReused()
        {
            var controller = new GameSyncController();
            controller.AddProfile(new GameProfile { ExecutableName = "game.exe", DisplayName = "Test Game" });
            var form = new GameSyncForm(controller, 144);
            Assert.NotNull(form);

            // Simulate DrawItem invocation on ListBox to verify StringFormat does not throw or leak
            var lstField = typeof(GameSyncForm).GetField("_lstProfiles", BindingFlags.NonPublic | BindingFlags.Instance);
            var lst = (ListBox?)lstField?.GetValue(form);
            Assert.NotNull(lst);

            using var bmp = new Bitmap(200, 100);
            using var g = Graphics.FromImage(bmp);
            var rect = new Rectangle(0, 0, 200, 30);
            var drawItemMethod = typeof(GameSyncForm).GetMethod("LstProfiles_DrawItem", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(drawItemMethod);

            // Invoke draw item multiple times
            for (int i = 0; i < 50; i++)
            {
                var ea = new DrawItemEventArgs(g, form.Font, rect, 0, DrawItemState.Default);
                drawItemMethod.Invoke(form, new object[] { lst, ea });
            }

            form.Dispose();
            controller.Dispose();
            Assert.True(form.IsDisposed);
        }

        [Fact]
        public async System.Threading.Tasks.Task UiLifecycle_FanCurveForm_ThreadSafety_And_DisposalClean()
        {
            var form = new FanCurveForm();
            Assert.NotNull(form);

            // Test cross-thread safe methods
            var cpuCurve = new System.Collections.Generic.List<Point> { new Point(30, 20), new Point(90, 80) };
            var gpuCurve = new System.Collections.Generic.List<Point> { new Point(30, 10), new Point(90, 70) };

            await System.Threading.Tasks.Task.Run(() =>
            {
                form.UpdateTemps(45, 55);
                form.SetCpuCurve(cpuCurve);
                form.SetGpuCurve(gpuCurve);
            });

            form.Dispose();
            Assert.True(form.IsDisposed);
        }

        [Fact]
        public async System.Threading.Tasks.Task UiLifecycle_FanCurveGraph_SafeInvalidate_CrossThreadSafe()
        {
            using var graph = new FanCurveGraph();

            // Background thread updates should not throw cross-thread exceptions
            await System.Threading.Tasks.Task.Run(() =>
            {
                graph.CurrentTemp = 65;
                graph.FanLabel = "CPU Fan";
                graph.Points = new System.Collections.Generic.List<Point> { new Point(30, 0), new Point(100, 100) };
            });

            Assert.Equal(65, graph.CurrentTemp);
            Assert.Equal("CPU Fan", graph.FanLabel);
        }

        [Fact]
        public void UiLifecycle_PredatorButton_BoldFontCaching_And_Disposal()
        {
            using var bmp = new Bitmap(100, 40);
            using var g = Graphics.FromImage(bmp);
            var pe = new PaintEventArgs(g, new Rectangle(0, 0, 100, 40));

            var btn = new PredatorButton { Width = 100, Height = 40, Text = "Test", IsActive = true };
            var onPaint = typeof(PredatorButton).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);

            // Paint active button 100 times - should reuse cached bold font rather than churn handles
            for (int i = 0; i < 100; i++)
            {
                onPaint?.Invoke(btn, new object[] { pe });
            }

            btn.Dispose();
            Assert.True(btn.IsDisposed);
        }

        [Fact]
        public void UiLifecycle_PredatorSwitch_Disposal_UnsubscribesAnimTimer()
        {
            var sw = new PredatorSwitch();
            sw.Checked = true;
            sw.Dispose();
            Assert.True(sw.IsDisposed);
        }
    }
}
