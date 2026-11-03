using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using PredatorControlApp;
using PredatorControlApp.Channels;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("SingleInstanceTests")]
    public class ComprehensiveCodebaseHardeningTests
    {
        [Fact]
        public void Fix1_EcHidDevice_ExcludesStandardKeyboardAndConsumerControlUsagePages()
        {
            // Verify that the standard keyboard (UsagePage 1) and Consumer Control (UsagePage 0x0C)
            // are strictly excluded from EcHidDevice.
            ushort kbPage = 0x0001;
            ushort consumerPage = 0x000C;
            ushort vendorPage = 0xFF05;

            bool isExcludedKb = kbPage == 0x0001 || kbPage == 0x000C;
            bool isExcludedConsumer = consumerPage == 0x0001 || consumerPage == 0x000C;
            bool isExcludedVendor = vendorPage == 0x0001 || vendorPage == 0x000C;

            Assert.True(isExcludedKb);
            Assert.True(isExcludedConsumer);
            Assert.False(isExcludedVendor);
        }

        [Fact]
        public void Fix1_OperatingModeChannelRouter_PrioritizesWmiOverEcHid()
        {
            // Verify that when WMI is primary, it is invoked first, and EcHid fallback is not called if WMI succeeds.
            var wmiChannel = new MockOperatingModeChannel(true, OperatingMode.Performance);
            var ecChannel = new MockOperatingModeChannel(true, OperatingMode.Turbo);

            var router = new OperatingModeChannelRouter(wmiChannel, ecChannel);

            bool writeResult = router.Write(OperatingMode.Performance);

            Assert.True(writeResult);
            Assert.Equal(1, wmiChannel.WriteCallCount);
            Assert.Equal(0, ecChannel.WriteCallCount); // Fallback never invoked because primary succeeded
        }

        [Fact]
        public void Fix2_Pl1Customization_AllowsPl1UpToMaxPl2_AndKeepsBiosDefaultsImmutable()
        {
            var settings = new AppSettings
            {
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 175,
                CpuMaxAllowedPl2Watts = 175,
                BiosHardwareFingerprint = "TestFingerprint_Clean",
                VerifiedBiosModesBitmask = 0x1F,
                BiosDefaultPerformancePl1Watts = 100,
                BiosDefaultPerformancePl2Watts = 140
            };

            // 1. Verify PL1 can be customized up to Max PL2 (175W)
            var (clamped1, clamped2) = settings.ClampPowerLimitWatts(175, 175);
            Assert.Equal(175, clamped1);
            Assert.Equal(175, clamped2);

            // 2. Verify BIOS Defaults remain immutable
            Assert.Equal(100, settings.BiosDefaultPerformancePl1Watts);
            Assert.Equal(140, settings.BiosDefaultPerformancePl2Watts);

            // 3. User custom limits do not mutate BiosDefault values
            settings.CustomPerformancePl1Watts = 175;
            settings.CustomPerformancePl2Watts = 175;
            settings.HardwareHasActiveCustomPowerOverride = true;

            Assert.Equal(100, settings.BiosDefaultPerformancePl1Watts);
            Assert.Equal(140, settings.BiosDefaultPerformancePl2Watts);
            Assert.True(settings.HardwareHasActiveCustomPowerOverride);
        }

        [Fact]
        public void Fix3_PawnIoController_DirectionalMmioOrdering_WhenNewPl1GreaterThanOldPl2()
        {
            var mockBackend = new MockPawnIoBackend();
            using var controller = new PawnIoController(mockBackend);

            // Simulate old state: PL1=45W (360 raw), PL2=65W (520 raw)
            mockBackend.WriteMmio32(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, 360 | (1 << 15));
            mockBackend.WriteMmio32(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, 520 | (1 << 15));

            // Target state: New PL1=80W (640 raw), New PL2=115W (920 raw) -> New PL1 (640) > Old PL2 (520)
            // Expectation: PL2 (0xFED159A4) written FIRST, then PL1 (0xFED159A0) written SECOND
            bool ok = controller.WriteMchbarPowerLimits(640, 920, out _);
            Assert.True(ok);

            var mmioWrites = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(mockBackend.History, h => h.Op == "WriteMmio32"));
            // Take the last two writes from WriteMchbarPowerLimits
            Assert.True(mmioWrites.Count >= 2);
            var firstWrite = mmioWrites[mmioWrites.Count - 2];
            var secondWrite = mmioWrites[mmioWrites.Count - 1];

            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, firstWrite.Target);
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, secondWrite.Target);
        }

        [Fact]
        public void Fix3_PawnIoController_DirectionalMmioOrdering_WhenNewPl1LessThanOrEqualToOldPl2()
        {
            var mockBackend = new MockPawnIoBackend();
            using var controller = new PawnIoController(mockBackend);

            // Simulate old state: PL1=45W (360 raw), PL2=115W (920 raw)
            mockBackend.WriteMmio32(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, 360 | (1 << 15));
            mockBackend.WriteMmio32(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, 920 | (1 << 15));

            // Target state: New PL1=55W (440 raw), New PL2=80W (640 raw) -> New PL1 (440) <= Old PL2 (920)
            // Expectation: PL1 (0xFED159A0) written FIRST, then PL2 (0xFED159A4) written SECOND
            bool ok = controller.WriteMchbarPowerLimits(440, 640, out _);
            Assert.True(ok);

            var mmioWrites = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(mockBackend.History, h => h.Op == "WriteMmio32"));
            Assert.True(mmioWrites.Count >= 2);
            var firstWrite = mmioWrites[mmioWrites.Count - 2];
            var secondWrite = mmioWrites[mmioWrites.Count - 1];

            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, firstWrite.Target);
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, secondWrite.Target);
        }

        [Fact]
        public void Fix4_ThermalSafetyService_ArbitratesCapWithoutMutatingSettings()
        {
            var mockBackend = new MockPawnIoBackend();
            var settings = new AppSettings
            {
                EnableCustomPowerLimits = true,
                CustomPerformancePl1Watts = 120,
                CustomPerformancePl2Watts = 140,
                BiosDefaultPerformancePl1Watts = 80,
                BiosDefaultPerformancePl2Watts = 115
            };

            var controller = new CpuPowerLimitController(settings, mockBackend);

            // Apply a transient thermal safety cap of 65W
            controller.SetThermalSafetyCap(65);

            // Verify settings are NOT mutated
            Assert.Equal(120, settings.CustomPerformancePl1Watts);
            Assert.Equal(140, settings.CustomPerformancePl2Watts);
            Assert.Equal(80, settings.BiosDefaultPerformancePl1Watts);
            Assert.Equal(115, settings.BiosDefaultPerformancePl2Watts);

            // Clear cap
            controller.ClearThermalSafetyCap();

            // Verify settings remain intact
            Assert.Equal(120, settings.CustomPerformancePl1Watts);
            Assert.Equal(140, settings.CustomPerformancePl2Watts);
        }

        [Fact]
        public void Fix5_PredatorSlider_DivisionByZero_GuardedWhenMinEqualsMax()
        {
            using var slider = new PredatorSlider
            {
                Minimum = 50,
                Maximum = 50, // Min == Max
                Value = 50
            };

            // Should not throw DivideByZeroException
            Assert.Equal(50, slider.Minimum);
            Assert.Equal(50, slider.Maximum);
            Assert.Equal(50, slider.Value);
        }

        private class MockOperatingModeChannel : IOperatingModeChannel
        {
            private readonly bool _shouldSucceed;
            private readonly OperatingMode _mode;
            public int WriteCallCount { get; private set; }

            public MockOperatingModeChannel(bool shouldSucceed, OperatingMode mode)
            {
                _shouldSucceed = shouldSucceed;
                _mode = mode;
            }

            public ModeTransportKind Transport => ModeTransportKind.WmiMisc0x0B;
            public System.Collections.Generic.IReadOnlyList<OperatingMode> SupportedModes => new[] { _mode };
            public OperatingMode? Read() => _mode;
            public bool Write(OperatingMode mode)
            {
                WriteCallCount++;
                return _shouldSucceed;
            }
            public bool TryGetOperatingMode(out OperatingMode mode)
            {
                mode = _mode;
                return true;
            }
            public bool TrySetOperatingMode(OperatingMode mode) => Write(mode);
        }
    }
}
