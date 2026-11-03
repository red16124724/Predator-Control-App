using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PredatorControlApp;
using PredatorControlApp.Channels;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class HardwareWmiChannelStressTests
    {
        [Fact]
        public void WmiController_SafeDisposeManagementObject_HandlesRepeatedDisposalsCleanly()
        {
            var wmi = new WmiController();
            wmi.Dispose();
            // Repeated disposal must be completely safe
            wmi.Dispose();
        }

        [Fact]
        public void OperatingModeChannelRouter_ConcurrentAccess_ThreadSafe()
        {
            var primary = new MockOperatingModeChannel { Supported = new[] { OperatingMode.Quiet, OperatingMode.Balanced } };
            var fallback = new MockOperatingModeChannel { Supported = new[] { OperatingMode.Balanced, OperatingMode.Turbo } };
            var router = new OperatingModeChannelRouter(primary, fallback);

            Parallel.For(0, 500, i =>
            {
                if (i % 3 == 0)
                {
                    _ = router.SupportedModes;
                }
                else if (i % 3 == 1)
                {
                    _ = router.Read();
                }
                else
                {
                    _ = router.Write(OperatingMode.Performance);
                }
            });
        }

        [Fact]
        public void ActionOperatingModeChannel_WhenDelegateThrows_ReturnsNullAndFalseSafely()
        {
            var channel = new ActionOperatingModeChannel((cmd, payload) => throw new InvalidOperationException("Hardware fault"));
            Assert.Null(channel.Read());
            Assert.False(channel.Write(OperatingMode.Turbo));
            Assert.False(channel.TryGetOperatingMode(out var mode));
            Assert.Equal(OperatingMode.Balanced, mode);
        }

        [Fact]
        public void WmiOperatingModeChannel_WhenDelegatesThrow_RecoversGracefully()
        {
            var channel = new WmiOperatingModeChannel(
                (cmd, payload) => throw new System.Runtime.InteropServices.COMException("COM disconnected"),
                () => throw new System.Management.ManagementException("WMI timeout"),
                mode => throw new Exception("Callback error")
            );

            Assert.Null(channel.Read());
            Assert.False(channel.Write(OperatingMode.Turbo));
            Assert.False(channel.TryGetOperatingMode(out var mode));
            Assert.Equal(OperatingMode.Balanced, mode);
        }

        [Fact]
        public void WmiFanControlChannel_NullChannelHandling_DoesNotThrow()
        {
            var channel = new WmiFanControlChannel((m, a) => true, (t, s, f) => true);
            Assert.False(channel.TrySetFanSpeed(null!, 50));
            Assert.Equal(0, channel.GetFanRpm(null!));
        }

        [Fact]
        public void AcerProtocol_NullSettingsHandling_Safe()
        {
            Assert.Equal(0UL, AcerProtocol.FanBehaviorInput(null!));
            Assert.Throws<ArgumentNullException>(() => AcerProtocol.FanSpeedInput(null!, 50));
        }

        private class MockOperatingModeChannel : IOperatingModeChannel
        {
            public ModeTransportKind Transport => ModeTransportKind.Composite;
            public IReadOnlyList<OperatingMode> Supported { get; set; } = Array.Empty<OperatingMode>();
            public IReadOnlyList<OperatingMode> SupportedModes => Supported;
            public OperatingMode? CurrentMode { get; set; } = OperatingMode.Balanced;

            public OperatingMode? Read() => CurrentMode;
            public bool Write(OperatingMode mode) { CurrentMode = mode; return true; }
            public bool TryGetOperatingMode(out OperatingMode mode) { mode = CurrentMode ?? OperatingMode.Balanced; return true; }
            public bool TrySetOperatingMode(OperatingMode mode) => Write(mode);
        }
    }
}
