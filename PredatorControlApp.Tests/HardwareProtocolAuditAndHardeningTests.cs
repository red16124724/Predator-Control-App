using System;
using System.Collections.Generic;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("NoParallel")]
    public class HardwareProtocolAuditAndHardeningTests
    {
        [Theory]
        [InlineData(new byte[] { 0x50, 0x50, 0x43, 0x43 })] // "PPCC" only
        [InlineData(new byte[] { 0x50, 0x50, 0x43, 0x43, 0x12 })] // PPCC + PackageOp only
        [InlineData(new byte[] { 0x50, 0x50, 0x43, 0x43, 0x12, 0x45 })] // Truncated length
        [InlineData(new byte[] { 0x50, 0x50, 0x43, 0x43, 0x12, 0xC0, 0x80 })] // Multi-byte truncated length
        [InlineData(new byte[] { 0x50, 0x50, 0x43, 0x43, 0x12, 0x80, 0xFF, 0xFF, 0xFF })] // Huge length
        [InlineData(new byte[] { 0x50, 0x50, 0x43, 0x43, 0x12, 0x0A, 0x02, 0x12, 0x04 })] // Truncated inner package
        [InlineData(new byte[] { 0x50, 0x50, 0x43, 0x43, 0x12, 0x20, 0x02, 0x12, 0x0C, 0x06, 0x0C, 0xFF })] // Corrupted integer opcodes
        public void AmlParser_TruncatedAndMalformedPpcc_DoesNotThrow(byte[] malformedAml)
        {
            var exception = Record.Exception(() =>
            {
                var entries = BiosUefiPowerScanner.ExtractPpccEntriesFromAml(malformedAml);
                Assert.NotNull(entries);
            });

            Assert.Null(exception);
        }

        [Fact]
        public void AmlParser_MalformedIntegerOpcodes_RecoversGracefully()
        {
            // Build pseudo PPCC with invalid / truncated integer opcodes (0x0A, 0x0B, 0x0C, 0x0E without operands)
            byte[] aml = new byte[]
            {
                0x50, 0x50, 0x43, 0x43, // PPCC
                0x12, 0x15, // PackageOp length 21
                0x02,       // NumElements 2
                0x12, 0x08, // Inner PackageOp length 8
                0x06,       // 6 elements
                0x0A,       // BytePrefix with no operand (EOF)
            };

            var exception = Record.Exception(() =>
            {
                var entries = BiosUefiPowerScanner.ExtractPpccEntriesFromAml(aml);
                Assert.NotNull(entries);
            });

            Assert.Null(exception);
        }

        [Fact]
        public void PawnIoController_DynamicMmioAddressCalculation_ResolvesCorrectOffset()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            // Default fallback with MchbarAddress == 0
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_LIMIT_ADDRESS, controller.GetMmioPackageRaplAddress());

            // With simulated standard MCHBAR at 0xFED10000 -> 0xFED10000 + 0x59A0 = 0xFED159A0
            mock.MchbarAddress = 0xFED10000UL;
            Assert.Equal(0xFED159A0UL, controller.GetMmioPackageRaplAddress());

            // With alternative base 0xFE000000 -> 0xFE000000 + 0x59A0 = 0xFE0059A0
            mock.MchbarAddress = 0xFE000000UL;
            Assert.Equal(0xFE0059A0UL, controller.GetMmioPackageRaplAddress());

            // With unaligned base (e.g. 0xFE003123) -> base is masked to 16KB alignment (~0x3FFFUL): 0xFE000000 + 0x59A0
            mock.MchbarAddress = 0xFE003123UL;
            Assert.Equal(0xFE0059A0UL, controller.GetMmioPackageRaplAddress());
        }

        [Fact]
        public void PawnIoController_ApplyPowerLimits_WhenLocked_FailsGracefullyWithoutWriting()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            mock.ConfigureIntelBaseline(tdpWatts: 55, locked: true);

            var res = controller.ApplyPowerLimits(65, 115);
            Assert.False(res.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.BiosLocked, res.Status);

            // Verify no MSR write was recorded to modify power limits
            ulong currentMsr = mock.GetMsr(PawnIoController.MSR_PKG_POWER_LIMIT);
            Assert.NotEqual(0UL, currentMsr & (1UL << 63)); // Lock bit preserved
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(0xFFFFUL)]
        [InlineData(0xFFFFFFFFUL)]
        [InlineData(0xFFFFFFFFFFFFFFFFUL)]
        public void WmiController_DecodeFanSpeed_RejectsDisconnectedAndFloatingMarkers(ulong raw)
        {
            int rpm = WmiController.DecodeFanSpeed(raw);
            Assert.Equal(0, rpm);
        }

        [Fact]
        public void WmiController_DecodeFanSpeed_HandlesValidDirectAndShiftedRpm()
        {
            // Valid direct tachometer readings <= 0x1FFF
            Assert.Equal(3500, WmiController.DecodeFanSpeed(3500));
            Assert.Equal(4800, WmiController.DecodeFanSpeed(4800));

            // Valid shifted ACPI packet: (4200 << 8) | 0x00
            ulong shiftedPacket = (4200UL << 8) | 0x00UL;
            Assert.Equal(4200, WmiController.DecodeFanSpeed(shiftedPacket));
        }

        [Fact]
        public void RawInputKeyWatcher_Dispose_DoesNotThrowOnRepeatedDisposals()
        {
            // Verify safe handle cleanup and multiple dispose calls without exception
            var watcher = new RawInputKeyWatcher();
            var ex1 = Record.Exception(() => watcher.Dispose());
            Assert.Null(ex1);

            var ex2 = Record.Exception(() => watcher.Dispose());
            Assert.Null(ex2);
        }
    }
}
