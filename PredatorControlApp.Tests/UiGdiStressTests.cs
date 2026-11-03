using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("NoParallel")]
    public class UiGdiStressTests
    {
        [Fact]
        public void PredatorSlider_HandlesInvertedMinMax_WithoutException()
        {
            using var slider = new PredatorSlider();
            slider.Minimum = 100;
            slider.Maximum = 20; // Inverted
            slider.Value = 50;

            Assert.True(slider.Value >= 20 && slider.Value <= 100);

            // Test painting with inverted bounds and standard bitmap
            using var bmp = new Bitmap(100, 30);
            using var g = Graphics.FromImage(bmp);
            var paintMethod = typeof(PredatorSlider).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(paintMethod);
            paintMethod.Invoke(slider, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 100, 30)) });
        }

        [Fact]
        public void PredatorSlider_HandlesZeroAndNegativeBounds_WithoutException()
        {
            using var slider = new PredatorSlider();
            slider.Size = new Size(0, 0);

            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            var paintMethod = typeof(PredatorSlider).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(paintMethod);

            // Zero bounds
            paintMethod.Invoke(slider, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 0, 0)) });
            // Negative bounds
            slider.Size = new Size(-10, -5);
            paintMethod.Invoke(slider, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, -10, -5)) });
        }

        [Fact]
        public void PredatorButton_HandlesDegenerateBounds_WithoutException()
        {
            using var btn = new PredatorButton();
            using var bmp = new Bitmap(10, 10);
            using var g = Graphics.FromImage(bmp);
            var paintMethod = typeof(PredatorButton).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(paintMethod);

            int[] testSizes = { 0, 1, 2, 3, 4, 5, 8, 9, 10 };
            foreach (int sz in testSizes)
            {
                btn.Size = new Size(sz, sz);
                paintMethod.Invoke(btn, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, sz, sz)) });
            }
        }

        [Fact]
        public void PredatorToggle_LerpColor_SanitizesNaNAndInfinity()
        {
            var lerpMethod = typeof(PredatorToggle).GetMethod("LerpColor", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(lerpMethod);

            Color c1 = Color.Black;
            Color c2 = Color.White;

            Color resNaN = (Color)lerpMethod.Invoke(null, new object[] { c1, c2, float.NaN })!;
            Assert.False(resNaN.IsEmpty);

            Color resInf = (Color)lerpMethod.Invoke(null, new object[] { c1, c2, float.PositiveInfinity })!;
            Assert.False(resInf.IsEmpty);

            Color resNegInf = (Color)lerpMethod.Invoke(null, new object[] { c1, c2, float.NegativeInfinity })!;
            Assert.False(resNegInf.IsEmpty);
        }

        [Fact]
        public void PredatorSwitch_LerpColor_SanitizesNaNAndInfinity()
        {
            var lerpMethod = typeof(PredatorSwitch).GetMethod("LerpColor", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(lerpMethod);

            Color c1 = Color.Red;
            Color c2 = Color.Blue;

            Color resNaN = (Color)lerpMethod.Invoke(null, new object[] { c1, c2, float.NaN })!;
            Assert.False(resNaN.IsEmpty);

            Color resInf = (Color)lerpMethod.Invoke(null, new object[] { c1, c2, float.PositiveInfinity })!;
            Assert.False(resInf.IsEmpty);
        }

        [Fact]
        public void FanCurveGraph_SanitizesNaNAndInfinityCoordinates()
        {
            using var graph = new FanCurveGraph();
            graph.Size = new Size(300, 200);

            var xToTemp = typeof(FanCurveGraph).GetMethod("XToTemp", BindingFlags.NonPublic | BindingFlags.Instance);
            var yToSpeed = typeof(FanCurveGraph).GetMethod("YToSpeed", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(xToTemp);
            Assert.NotNull(yToSpeed);

            int tempNaN = (int)xToTemp.Invoke(graph, new object[] { float.NaN })!;
            Assert.InRange(tempNaN, 30, 100);

            int speedNaN = (int)yToSpeed.Invoke(graph, new object[] { float.NaN })!;
            Assert.InRange(speedNaN, 0, 100);

            int tempInf = (int)xToTemp.Invoke(graph, new object[] { float.PositiveInfinity })!;
            Assert.InRange(tempInf, 30, 100);

            int speedInf = (int)yToSpeed.Invoke(graph, new object[] { float.NegativeInfinity })!;
            Assert.InRange(speedInf, 0, 100);
        }

        [Fact]
        public void FanCurveGraph_PaintZeroBounds_NoLinearGradientCrash()
        {
            using var graph = new FanCurveGraph();
            graph.Size = new Size(0, 0);

            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            var paintMethod = typeof(FanCurveGraph).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(paintMethod);

            paintMethod.Invoke(graph, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 0, 0)) });
        }

        [Fact]
        public void DarkScrollPanel_HandlesZeroAndNegativeSizes()
        {
            using var panel = new DarkScrollPanel();
            panel.Size = new Size(0, 0);

            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            var paintMethod = typeof(DarkScrollPanel).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(paintMethod);

            paintMethod.Invoke(panel, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 0, 0)) });
        }

        [Fact]
        public void PredatorDropDown_HandlesZeroAndTinySizes()
        {
            using var dd = new PredatorDropDown();
            dd.Size = new Size(0, 0);

            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            var paintMethod = typeof(PredatorDropDown).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(paintMethod);

            paintMethod.Invoke(dd, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 0, 0)) });

            dd.Size = new Size(10, 10);
            paintMethod.Invoke(dd, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 10, 10)) });
        }

        [Fact]
        public void TelemetryHistory_DrawSeries_HandlesZeroHeight_WithoutCrash()
        {
            using var graph = new HistoryGraphControl();
            graph.Size = new Size(100, 0);

            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            var paintMethod = typeof(HistoryGraphControl).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(paintMethod);

            paintMethod.Invoke(graph, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 100, 0)) });
        }

        [Fact]
        public void PredatorDropDown_DisabledState_RendersWithoutException()
        {
            using var dd = new PredatorDropDown();
            dd.Size = new Size(150, 30);
            dd.Items.Add("Option 1");
            dd.SelectedIndex = 0;
            dd.Enabled = false;

            using var bmp = new Bitmap(150, 30);
            using var g = Graphics.FromImage(bmp);
            var paintMethod = typeof(PredatorDropDown).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(paintMethod);
            paintMethod.Invoke(dd, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 150, 30)) });
        }

        [Fact]
        public void PredatorButton_DisabledNavButton_RendersWithoutException()
        {
            using var btn = new PredatorButton { Text = "Dashboard", IsNavButton = true, Enabled = false, Size = new Size(120, 40) };

            using var bmp = new Bitmap(120, 40);
            using var g = Graphics.FromImage(bmp);
            var paintMethod = typeof(PredatorButton).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(paintMethod);
            paintMethod.Invoke(btn, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 120, 40)) });
        }

        [Fact]
        public void DarkScrollPanel_PreFilterMessage_DisposingGuard_NoCrash()
        {
            using var panel = new DarkScrollPanel();
            var m = Message.Create(IntPtr.Zero, 0x020A /* WM_MOUSEWHEEL */, IntPtr.Zero, IntPtr.Zero);
            bool handled = ((IMessageFilter)panel).PreFilterMessage(ref m);
            Assert.False(handled);
        }
    }
}
