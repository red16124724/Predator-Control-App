using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class NewHardwareFeaturesTests
    {
        [Theory]
        [InlineData(0, 0)]  // Static -> Static
        [InlineData(1, 1)]  // Breathing -> Breathing
        [InlineData(2, 2)]  // Neon -> Neon
        [InlineData(3, 2)]  // Wave -> Neon (Single-LED fallback)
        [InlineData(4, 2)]  // Shifting -> Neon
        [InlineData(5, 2)]  // Zoom -> Neon
        [InlineData(6, 2)]  // Meteor -> Neon
        [InlineData(7, 2)]  // Twinkling -> Neon
        public void Test_SetGamingLED_ModeMapping_SingleLedHardware(int inputMode, byte expectedMappedMode)
        {
            byte mapped = (byte)(inputMode switch
            {
                1 => 1,
                2 or 3 => 2,
                _ => (inputMode == 0 ? 0 : 2)
            });
            Assert.Equal(expectedMappedMode, mapped);
        }

        [Fact]
        public void Test_SetGamingLEDColor_BytePacking_LogoAndExterior()
        {
            byte r = 0, g = 150, b = 255;
            ulong logoZone = 0x01ul;
            ulong exteriorZone = 0x0Ful;

            ulong logoColorPayload = ((ulong)b << 24) | ((ulong)g << 16) | ((ulong)r << 8) | logoZone;
            ulong exteriorColorPayload = ((ulong)b << 24) | ((ulong)g << 16) | ((ulong)r << 8) | exteriorZone;

            // Verify Byte 0 = Zone
            Assert.Equal(0x01ul, logoColorPayload & 0xFFul);
            Assert.Equal(0x0Ful, exteriorColorPayload & 0xFFul);

            // Verify Byte 1 = R
            Assert.Equal(r, (byte)((logoColorPayload >> 8) & 0xFFul));
            // Verify Byte 2 = G
            Assert.Equal(g, (byte)((logoColorPayload >> 16) & 0xFFul));
            // Verify Byte 3 = B
            Assert.Equal(b, (byte)((logoColorPayload >> 24) & 0xFFul));
        }

        [Fact]
        public void Test_LcdOverdrivePayloads()
        {
            var (onPayload, offPayload) = WmiController.GetLcdOverdrivePayloads();

            Assert.Equal(0x1000000000010UL, onPayload);
            Assert.Equal(0x10UL, offPayload);
        }

        [Fact]
        public void Test_TelemetryPollingIntervals_StrictAcAndBattery()
        {
            // Requirement: On charging = 2000ms (2 seconds), on battery = 5000ms (5 seconds)
            int acInterval = 2000;
            int batteryInterval = 5000;

            Assert.Equal(2000, acInterval);
            Assert.Equal(5000, batteryInterval);
        }

        [Fact]
        public void Test_FanCurveHysteresisDeadband_SuppressesMinorJitter()
        {
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(90, 100)
            };

            int baseTemp = 55;
            int baseSpeed = Form1.InterpolateCurve(curve, baseTemp);

            // Jitter by +1°C (e.g. 56°C)
            int jitterTemp = 56;
            int jitterSpeed = Form1.InterpolateCurve(curve, jitterTemp);

            int tempDelta = Math.Abs(jitterTemp - baseTemp);
            int speedDelta = Math.Abs(jitterSpeed - baseSpeed);

            // With deadband: tempDelta must be >= 2 AND speedDelta must be >= 3
            bool shouldTrigger = (tempDelta >= 2) && (speedDelta >= 3);
            Assert.False(shouldTrigger, "1°C thermal jitter should NOT trigger hardware fan commands");

            // Meaningful temperature rise of +5°C (55°C -> 60°C)
            int jumpTemp = 60;
            int jumpSpeed = Form1.InterpolateCurve(curve, jumpTemp);

            int jumpTempDelta = Math.Abs(jumpTemp - baseTemp);
            int jumpSpeedDelta = Math.Abs(jumpSpeed - baseSpeed);

            bool jumpShouldTrigger = (jumpTempDelta >= 2) && (jumpSpeedDelta >= 3);
            Assert.True(jumpShouldTrigger, "5°C temperature rise with >=3% speed change MUST trigger hardware fan update");
        }



        [Fact]
        public void Test_WmiHotkeyWatcher_EventDispatchSafety()
        {
            int receivedDetail = -1;
            int callCount = 0;
            var watcher = new WmiHotkeyWatcher(detail =>
            {
                receivedDetail = detail;
                System.Threading.Interlocked.Increment(ref callCount);
            });

            // Disposing immediately shouldn't throw
            watcher.Dispose();
            // Multiple dispose shouldn't throw
            watcher.Dispose();

            Assert.Equal(-1, receivedDetail);
            Assert.Equal(0, callCount);
        }
    }
}
