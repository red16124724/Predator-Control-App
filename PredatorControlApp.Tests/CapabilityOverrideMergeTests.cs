using System.Linq;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Pins the native capability-override merge contract. The tri-state array
    /// order must stay aligned with `capability::CAP_*`: CoolBoost, GpuModeSwitch,
    /// ThirdFan, FanTable, UsbCharging, BatteryCalibration, HasEcHid, ModeKey.
    /// A misalignment previously applied ModeKey's override to the HasEcHid slot.
    /// </summary>
    public class CapabilityOverrideMergeTests
    {
        [Fact]
        public void Apply_ModeKeyEnabledOverride_IsHonored()
        {
            var caps = new DeviceCapabilities { ModeKey = false };

            var merged = new CapabilityOverrides { ModeKey = true }.Apply(caps);

            Assert.True(merged.ModeKey);
        }

        [Fact]
        public void Apply_ModeKeyDisabledOverride_IsHonored()
        {
            var caps = new DeviceCapabilities { ModeKey = true };

            var merged = new CapabilityOverrides { ModeKey = false }.Apply(caps);

            Assert.False(merged.ModeKey);
        }

        [Fact]
        public void Apply_UnspecifiedOverrides_PreserveEveryBaselineCapability()
        {
            var caps = new DeviceCapabilities
            {
                CoolBoost = true,
                FanTable = true,
                GpuModeSwitch = true,
                UsbCharging = true,
                BatteryCalibration = true,
                ModeKey = true,
                HasEcHid = true
            };

            var merged = new CapabilityOverrides().Apply(caps);

            Assert.Equal(caps.CoolBoost, merged.CoolBoost);
            Assert.Equal(caps.FanTable, merged.FanTable);
            Assert.Equal(caps.GpuModeSwitch, merged.GpuModeSwitch);
            Assert.Equal(caps.UsbCharging, merged.UsbCharging);
            Assert.Equal(caps.BatteryCalibration, merged.BatteryCalibration);
            Assert.Equal(caps.ModeKey, merged.ModeKey);
            Assert.Equal(caps.HasEcHid, merged.HasEcHid);
        }

        [Fact]
        public void Apply_ThirdFanOverride_AddsAndRemovesSystemFan()
        {
            var caps = new DeviceCapabilities();

            var withFan = new CapabilityOverrides { ThirdFan = true }.Apply(caps);
            Assert.Contains(withFan.Fans, f => f.Id == FanId.System);
            Assert.True(withFan.HasThirdFan);

            var withoutFan = new CapabilityOverrides { ThirdFan = false }.Apply(withFan);
            Assert.DoesNotContain(withoutFan.Fans, f => f.Id == FanId.System || f.Id == FanId.Gpu2);
            Assert.False(withoutFan.HasThirdFan);
        }

        [Fact]
        public void Apply_NullHasEcHidOverride_KeepsUnknownStateUnknown()
        {
            var caps = new DeviceCapabilities { HasEcHid = null };

            var merged = new CapabilityOverrides { CoolBoost = true }.Apply(caps);

            Assert.Null(merged.HasEcHid);
            Assert.True(merged.CoolBoost);
        }
    }
}
