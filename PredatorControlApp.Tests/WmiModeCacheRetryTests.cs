using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class WmiModeCacheRetryTests
    {
        [Fact]
        public void FailedModeSwitch_IsNotReportedAsSuccessOnRetry()
        {
            using var wmi = new WmiController();

            // No EC/WMI transport is available in the test environment, so this write fails.
            bool first = wmi.TrySetPowerMode(0x01, forceRetry: false);
            Assert.False(first);

            // The requested mode is still tracked so TryGetPowerMode()/the UI keep reporting it.
            Assert.Equal((byte)0x01, wmi.LastAppliedPowerMode);

            // A repeat must retry the hardware, not short-circuit as if the mode had been applied.
            bool second = wmi.TrySetPowerMode(0x01, forceRetry: false);
            Assert.False(second);
            Assert.Equal((byte)0x01, wmi.LastAppliedPowerMode);
        }

        [Fact]
        public void FailedFanSpeedWrite_IsNotCachedAsApplied()
        {
            using var wmi = new WmiController();

            // No EC/WMI transport here, so every SetGamingFanSpeed command fails.
            wmi.SetCpuFanSpeed(60);

            var field = typeof(WmiController).GetField(
                "_lastAppliedCpuSpeed",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            Assert.NotNull(field);
            // A failed write must stay retryable instead of being remembered as applied.
            Assert.Null(field!.GetValue(wmi));
        }

        [Fact]
        public void RepeatedFailedModes_KeepRetryingAcrossDifferentModes()
        {
            using var wmi = new WmiController();

            Assert.False(wmi.TrySetPowerMode(0x06));
            Assert.False(wmi.TrySetPowerMode(0x06));
            Assert.False(wmi.TrySetPowerMode(0x04));
            Assert.False(wmi.TrySetPowerMode(0x04));
            Assert.Equal((byte)0x04, wmi.LastAppliedPowerMode);
        }
    }
}
