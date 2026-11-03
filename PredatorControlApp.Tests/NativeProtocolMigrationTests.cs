using Xunit;

namespace PredatorControlApp.Tests;

public sealed class NativeProtocolMigrationTests
{
    private static ulong Native(uint op, ulong a = 0, ulong b = 0, ulong c = 0, ulong d = 0)
    {
        Assert.True(PredatorNativeCore.IsNativeCoreAvailable);
        Assert.True(NativeAcerProtocol.TryEncode(op, a, b, c, d, out ulong result));
        return result;
    }

    [Fact]
    public void NativeProtocol_MatchesOriginalEncodingAcrossInputs()
    {
        var random = new Random(7123);
        int[] numbers = { int.MinValue, -1000, -1, 0, 1, 5, 9, 10, 95, 100, 101, 255, 256, int.MaxValue };
        foreach (int n in numbers)
        foreach (int m in numbers)
        {
            byte id = (byte)m;
            ulong arg = NativeAcerProtocol.Signed(n);
            Assert.Equal(1UL | ((ulong)id << 8), Native(0, id));
            Assert.Equal((ulong)id | ((ulong)Math.Clamp(n, 0, 100) << 8), Native(2, id, arg));
            Assert.Equal((ulong)((int)Math.Round(Math.Clamp(n, 0, 100) / 10.0, MidpointRounding.AwayFromZero) * 10), Native(3, arg));
            Assert.Equal(unchecked(4UL | (15UL << 8) | ((ulong)n << 16)), Native(6, 1, arg));
            ulong expected = 2UL | ((ulong)id << 8) | 0x80000UL | ((ulong)Math.Clamp(n, 0, 100) << 32) | ((ulong)Math.Clamp(m, 0, 255) << 40);
            Assert.Equal(expected, Native(12, id, arg, NativeAcerProtocol.Signed(m), 0x80000));
        }

        for (int i = 0; i < 1000; i++)
        {
            ulong value = ((ulong)(uint)random.NextInt64(0, 1L << 32) << 32) | (uint)random.NextInt64(0, 1L << 32);
            ulong mask = ((ulong)(uint)random.NextInt64(0, 1L << 32) << 32) | (uint)random.NextInt64(0, 1L << 32);
            Assert.Equal((value >> 8) & 0xffff, Native(1, value));
            Assert.Equal(value | mask, Native(8, value, mask, 1));
            Assert.Equal(value & ~mask, Native(8, value, mask, 0));
            Assert.Equal(value >> 24, Native(21, value));
            Assert.Equal((value >> 8) & 0xffff, Native(22, value));
            Assert.Equal((value >> 32) & 0xff, Native(13, value));
            Assert.Equal((value >> 40) & 0xff, Native(14, value));
            Assert.Equal((ulong)AcerProtocol.SensorValue(value), Native(1, value));
        }
    }

    [Fact]
    public void NativeFanPackets_PreserveDotNetShiftSemantics()
    {
        for (int bit = 0; bit < 24; bit++)
        for (int behavior = 0; behavior < 4; behavior++)
        {
            ulong expected = (1UL << bit) | ((ulong)behavior << (16 + 2 * bit));
            Assert.Equal(expected, Native(18, (ulong)bit, (ulong)behavior));
            var channel = new FanChannel(FanId.Cpu, "Test", FanChip.Cpu, SensorId.CpuFanSpeed, bit, 1);
            Assert.Equal(expected, AcerProtocol.FanBehaviorInput(new[] { (channel, (FanBehavior)behavior) }));
        }
        Assert.False(NativeAcerProtocol.TryEncode(18, 24, 0, 0, 0, out _));
        Assert.False(NativeAcerProtocol.TryEncode(999, 0, 0, 0, 0, out _));
    }

    [Fact]
    public void NativeModes_MatchOriginalAcrossAllByteValuesAndModeCounts()
    {
        foreach (int count in new[] { int.MinValue, -1, 0, 3, 4, 5, 255, int.MaxValue })
        for (int i = 0; i <= byte.MaxValue; i++)
        {
            byte mode = (byte)i;
            byte expected = mode switch
            {
                5 => 0,
                4 => (byte)(count >= 4 ? 1 : 0),
                1 => (byte)(count >= 4 ? 2 : 1),
                0 => (byte)(count >= 4 ? 3 : 2),
                6 => unchecked((byte)Math.Max(0, count - 1)),
                _ => 1,
            };
            bool expectedOk = mode is 5 or 4 or 1 or 0 or 6;
            ulong encoded = Native(16, mode, NativeAcerProtocol.Signed(count));
            Assert.Equal(expected, (byte)encoded);
            Assert.Equal(expectedOk, (encoded & 0x100) != 0);
            Assert.Equal(expectedOk, AcerProtocol.TryWmiModeToEcMode(mode, out byte actual, count));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void NativeBatteryAndCharging_RespectUnknownAndTruncatedValues()
    {
        foreach (ulong flags in new ulong[] { 0, 1, 2, 3, ulong.MaxValue })
        foreach (byte[] data in new[] { Array.Empty<byte>(), new byte[] { 1 }, new byte[] { 2, 1 }, new byte[] { 1, 1 } })
        {
            var actual = BatteryProtocol.DecodeStatus(flags, data)!;
            Assert.Equal((flags & 1) != 0, actual.HealthModeSupported);
            Assert.Equal((flags & 2) != 0, actual.CalibrationSupported);
            Assert.Equal(data.Length > 0 && data[0] == 1, actual.HealthModeOn);
            Assert.Equal(data.Length > 1 && data[1] == 1, actual.CalibrationOn);
        }
        Assert.Null(BatteryProtocol.DecodeStatus(3, null));
        foreach (int floor in new[] { 0, 10, 20, 30, 40, 255 })
        {
            ulong packet = 4UL | (15UL << 8) | ((ulong)floor << 16);
            var actual = AcerProtocol.UsbChargingValue(packet);
            Assert.True(actual.Enabled);
            Assert.Equal(floor is 10 or 20 or 30 ? floor : null, actual.FloorPercent);
        }
    }

    [Fact]
    public void ChargingFloor_UsesManagedFallbackWhenPublicFloorArrayIsChanged()
    {
        int previous = AcerProtocol.UsbChargingFloors[0];
        try
        {
            AcerProtocol.UsbChargingFloors[0] = 40;
            var result = AcerProtocol.UsbChargingValue(4UL | (15UL << 8) | (40UL << 16));
            Assert.True(result.Enabled);
            Assert.Equal(40, result.FloorPercent);
        }
        finally { AcerProtocol.UsbChargingFloors[0] = previous; }
    }
}
