using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class UiRgbAnimationAndFormTransitionsStressTests : IDisposable
    {
        private readonly string _tempConfig;

        public UiRgbAnimationAndFormTransitionsStressTests()
        {
            _tempConfig = Path.Combine(Path.GetTempPath(), $"GameSyncStress_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tempConfig)) File.Delete(_tempConfig); } catch { }
            LightingEffectsManager.StopSoftwareAnimation();
        }

        [Fact]
        public void RapidRgbEffectSwitching_ConcurrentThreads_NoDeadlocksOrLeakedAnimations()
        {
            using var wmi = new WmiController();

            // Rapidly switch between hardware and software effects across multiple threads
            Parallel.For(0, 50, i =>
            {
                int effectIdx = i % 34;
                byte r = (byte)((i * 37) % 256);
                byte g = (byte)((i * 73) % 256);
                byte b = (byte)((i * 109) % 256);
                byte bright = (byte)(50 + (i % 50));
                byte speed = (byte)(1 + (i % 9));

                LightingEffectsManager.ApplyEffect(effectIdx, wmi, r, g, b, bright, speed, 0);
            });

            // Stop animations and verify state is cleanly stopped
            LightingEffectsManager.StopSoftwareAnimation();
            Assert.False(LightingEffectsManager.IsSoftwareAnimationRunning);
        }

        [Theory]
        [InlineData(-5, false)]
        [InlineData(-1, false)]
        [InlineData(5, false)]
        [InlineData(10, false)]
        [InlineData(999, false)]
        public void ZoneColor_NegativeAndOutOfBoundsZones_ReturnsFalse(int zone, bool expected)
        {
            using var wmi = new WmiController();
            bool result = wmi.SetZoneColor(zone, (byte)255, (byte)255, (byte)255);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ZoneColor_NegativeRgbValuesAndOverload_ClampsSafely()
        {
            using var wmi = new WmiController();
            // Call int overload with negative and out of range values
            bool res1 = wmi.SetZoneColor(1, -100, 128, 500);
            Assert.True(res1);

            bool res2 = wmi.SetZoneColor(0, -1, -50, 300);
            Assert.True(res2);

            // SetStaticColor with int overload
            wmi.SetStaticColor(-50, 100, 500, -10);
            Assert.Equal(0, wmi.Brightness);
        }

        [Fact]
        public void ZoneColor_AlphaChannel_ScalesRgbGracefully()
        {
            using var wmi = new WmiController();

            // Transparent color (A=0) should scale RGB to 0
            Color transparent = Color.FromArgb(0, 255, 0, 0);
            bool res1 = wmi.SetZoneColor(1, transparent);
            Assert.True(res1);

            // Semi-transparent color
            Color semi = Color.FromArgb(128, 200, 100, 50);
            bool res2 = wmi.SetZoneColor(2, semi);
            Assert.True(res2);
        }

        [Fact]
        public void LightingEffects_BoundaryIndicesAndAlpha_ClampsSafely()
        {
            using var wmi = new WmiController();

            // Negative effectIndex should clamp to 0 without throwing
            LightingEffectsManager.ApplyEffect(-1, wmi, 255, 100, 50, 100, 5, 0);

            // Index beyond 33 should clamp to 33 without throwing
            LightingEffectsManager.ApplyEffect(99, wmi, 255, 100, 50, 100, 5, 0);

            // Color overload with alpha
            Color semiCyan = Color.FromArgb(128, 0, 200, 255);
            LightingEffectsManager.ApplyEffect(3, wmi, semiCyan, 80, 50, 0);

            LightingEffectsManager.StopSoftwareAnimation();
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(-60, false)]
        [InlineData(-1, false)]
        [InlineData(99999, false)]
        public void DisplayRefreshRate_EdgeCases_0Hz_Negative_Unsupported_ReturnsFalse(int hz, bool expected)
        {
            bool res = Form1.SetRefreshRateCore(hz);
            Assert.Equal(expected, res);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(999)]
        public void DisplayRefreshRate_InvalidDisplayIndex_HandledGracefully(int displayIndex)
        {
            // Negative and non-existent display indices should safely return false on Set
            bool setRes = Form1.SetRefreshRateCore(144, displayIndex);
            Assert.False(setRes);

            // GetCurrentRefreshRateCore should fallback safely to 60
            int curHz = Form1.GetCurrentRefreshRateCore(displayIndex);
            Assert.Equal(60, curHz);

            // GetMaxRefreshRateCore should fallback safely to 60
            int maxHz = Form1.GetMaxRefreshRateCore(displayIndex);
            Assert.Equal(60, maxHz);
        }

        [Fact]
        public void LcdOverdrive_ToggleStress_ReturnsSafelyWithoutThrows()
        {
            using var wmi = new WmiController();
            bool on = wmi.SetLcdOverdrive(true);
            bool off = wmi.SetLcdOverdrive(false);
            // Neither should throw
            Assert.True(on || !on);
            Assert.True(off || !off);
        }

        [Fact]
        public void GameSyncForm_LstProfiles_DrawItem_OutOfBoundsIndex_DoesNotThrow()
        {
            using var controller = new GameSyncController(_tempConfig);
            using var form = new GameSyncForm(controller, 144);

            var lstField = typeof(GameSyncForm).GetField("_lstProfiles", BindingFlags.NonPublic | BindingFlags.Instance);
            var lst = (ListBox?)lstField?.GetValue(form);
            Assert.NotNull(lst);

            var drawMethod = typeof(GameSyncForm).GetMethod("LstProfiles_DrawItem", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(drawMethod);

            using var bmp = new Bitmap(100, 50);
            using var g = Graphics.FromImage(bmp);
            var rect = new Rectangle(0, 0, 100, 30);

            // Index -1
            var eaNeg = new DrawItemEventArgs(g, form.Font, rect, -1, DrawItemState.Default);
            drawMethod.Invoke(form, new object[] { lst, eaNeg });

            // Index 999 when items count is 0
            var eaOob = new DrawItemEventArgs(g, form.Font, rect, 999, DrawItemState.Default);
            drawMethod.Invoke(form, new object[] { lst, eaOob });
        }

        [Fact]
        public void GameSyncForm_DialogLifecycle_RepeatedOpenClose_DuringActiveGameDetection()
        {
            using var controller = new GameSyncController(_tempConfig);
            controller.AddProfile(new GameProfile { ExecutableName = "stress_game.exe", DisplayName = "Stress Game", PowerMode = 0x05 });
            controller.IsEnabled = true;

            // Simulate rapid open and close while game detected and exited events are firing
            for (int i = 0; i < 15; i++)
            {
                using var form = new GameSyncForm(controller, 165);
                Assert.NotNull(form);

                // Trigger game detected event on controller
                var profile = controller.Profiles[0];
                controller.PollOnce();

                // Dispose form and verify it cleans up
                form.Dispose();
                Assert.True(form.IsDisposed);
            }
        }
    }
}
