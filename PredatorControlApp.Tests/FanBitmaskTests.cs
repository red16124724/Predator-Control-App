using Xunit;

namespace PredatorControlApp.Tests
{
    public class FanBitmaskTests
    {
        [Fact]
        public void MaskTachometerRpm_ZeroStatusBits_ReturnsExactRpm()
        {
            // raw: (3904 << 8) | 0x00 -> 0x000F4000
            ulong raw = 0x000F4000UL;
            int rpm = WmiController.MaskTachometerRpm(raw);
            Assert.Equal(3904, rpm);
        }

        [Theory]
        [InlineData(0x2000UL, 3500)] // Status bit 0x2000
        [InlineData(0x4000UL, 4200)] // Status bit 0x4000
        [InlineData(0x8000UL, 2800)] // Status bit 0x8000
        [InlineData(0xE000UL, 4500)] // All upper 3 status bits set
        public void MaskTachometerRpm_WithHardwareStatusFlags_StripsUpperThreeBits(ulong statusFlags, int expectedRpm)
        {
            // Construct raw with hardware status flags in upper 3 bits of the 16-bit payload
            ulong payload = (statusFlags | (ulong)(uint)expectedRpm);
            ulong raw = (payload << 8) | 0x00UL;

            // Without 0x1FFF mask, old code ((raw >> 8) & 0xFFFF) would yield:
            int oldCorruptedRpm = (int)((raw >> 8) & 0xFFFF);
            Assert.NotEqual(expectedRpm, oldCorruptedRpm);

            // With new 0x1FFF mask, upper status flags are stripped:
            int maskedRpm = WmiController.MaskTachometerRpm(raw);
            Assert.Equal(expectedRpm, maskedRpm);
        }

        [Fact]
        public void MaskTachometerRpm_NonZeroStatusByte_ReturnsZero()
        {
            // When (raw & 0xFF) != 0, WMI call failed or returned error status
            ulong rawFailure = 0x000F4001UL;
            int rpm = WmiController.MaskTachometerRpm(rawFailure);
            Assert.Equal(0, rpm);
        }

        [Fact]
        public void MaskTachometerRpm_ZeroRaw_ReturnsZero()
        {
            int rpm = WmiController.MaskTachometerRpm(0UL);
            Assert.Equal(0, rpm);
        }

        [Fact]
        public void MaskTachometerRpm_Max13BitRpm_Returns8191()
        {
            // 0x1FFF is 8191 RPM (maximum representable in 13-bit tachometer)
            ulong raw = (0x1FFFUL << 8) | 0x00UL;
            int rpm = WmiController.MaskTachometerRpm(raw);
            Assert.Equal(8191, rpm);
        }

        [Theory]
        [InlineData(2048)] // 2048 % 256 == 0
        [InlineData(2560)] // 2560 % 256 == 0
        [InlineData(3072)] // 3072 % 256 == 0
        [InlineData(3584)] // 3584 % 256 == 0
        [InlineData(4096)] // 4096 % 256 == 0
        [InlineData(5120)] // 5120 % 256 == 0
        public void DecodeFanSpeed_DirectRpmMultipleOf256_DoesNotCorrupt(ulong directRpm)
        {
            // Direct readings from Method 17 are <= 0x1FFF
            // Old buggy behavior with MaskTachometerRpm saw low byte 0x00 and shifted right by 8
            int decoded = WmiController.DecodeFanSpeed(directRpm);
            Assert.Equal((int)directRpm, decoded);
        }

        [Theory]
        [InlineData(1500)]
        [InlineData(2750)]
        [InlineData(3999)]
        [InlineData(4800)]
        [InlineData(8191)]
        public void DecodeFanSpeed_Direct13BitValues_ReturnsExact(ulong directRpm)
        {
            int decoded = WmiController.DecodeFanSpeed(directRpm);
            Assert.Equal((int)directRpm, decoded);
        }

        [Theory]
        [InlineData(3200)]
        [InlineData(4500)]
        [InlineData(5200)]
        public void DecodeFanSpeed_ShiftedPacketFormat_ExtractsAccurately(int expectedRpm)
        {
            // Shifted ACPI packet: (rpm << 8) | 0x00
            ulong raw = ((ulong)(uint)expectedRpm << 8) | 0x00UL;
            int decoded = WmiController.DecodeFanSpeed(raw);
            Assert.Equal(expectedRpm, decoded);
        }

        [Fact]
        public void DecodeFanSpeed_ShiftedPacketWithErrorByte_ReturnsZero()
        {
            ulong rawError = (4000UL << 8) | 0x05UL;
            int decoded = WmiController.DecodeFanSpeed(rawError);
            Assert.Equal(0, decoded);
        }

        [Fact]
        public void DecodeFanSpeed_ZeroRaw_ReturnsZero()
        {
            Assert.Equal(0, WmiController.DecodeFanSpeed(0UL));
        }
    }
}
