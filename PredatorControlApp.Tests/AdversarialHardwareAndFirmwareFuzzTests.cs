using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using PredatorControlApp.Channels;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Exhaustive Adversarial Hardware, Firmware, and Protocol Fuzzing Test Suite.
    /// Fuzzes BiosUefiPowerScanner, PawnIoController, WMI controllers, and EC Channels
    /// under corrupt ACPI tables, randomized byte buffers, malformed AML payloads,
    /// out-of-range MSR registers, and simulated hardware/driver communication failures.
    /// </summary>
    [Collection("SingleInstanceTests")]
    [SupportedOSPlatform("windows")]
    public class AdversarialHardwareAndFirmwareFuzzTests
    {
        #region 1. BiosUefiPowerScanner AML & ACPI Buffer Fuzzing

        [Fact]
        public void ExtractPpccEntriesFromAml_FuzzWith200RandomizedBuffers_NeverHangsOrThrows()
        {
            var rng = new Random(42);
            int[] bufferSizes = { 0, 1, 2, 3, 4, 7, 8, 15, 16, 31, 32, 64, 128, 256, 512, 1024, 4096, 65536 };

            foreach (int size in bufferSizes)
            {
                for (int iter = 0; iter < 15; iter++)
                {
                    byte[] randomBuffer = new byte[size];
                    rng.NextBytes(randomBuffer);

                    // Call ExtractPpccEntriesFromAml - must handle arbitrary chaos safely
                    var entries = BiosUefiPowerScanner.ExtractPpccEntriesFromAml(randomBuffer);
                    Assert.NotNull(entries);

                    // Also test ParsePpccFromAml
                    var tupleResult = BiosUefiPowerScanner.ParsePpccFromAml(randomBuffer);
                    if (tupleResult.HasValue)
                    {
                        var (min, pl1, pl2, tdp) = tupleResult.Value;
                        Assert.True(min >= 0 && pl1 >= min && pl2 >= pl1 && tdp >= min);
                    }
                }
            }
        }

        [Fact]
        public void ExtractPpccEntriesFromAml_FuzzMalformedAmlPayloads_HandlesCorruptOpcodesSafely()
        {
            // Inject "PPCC" pattern followed by corrupted AML structures
            byte[] ppccHeader = Encoding.ASCII.GetBytes("PPCC");

            var testCases = new List<byte[]>();

            // Case 1: PPCC followed by nothing
            testCases.Add(ppccHeader);

            // Case 2: PPCC followed by incomplete PackageOp
            testCases.Add(ppccHeader.Concat(new byte[] { 0x12 }).ToArray());

            // Case 3: PPCC followed by PackageOp with length 0xFF (truncated)
            testCases.Add(ppccHeader.Concat(new byte[] { 0x12, 0xFF }).ToArray());

            // Case 4: PPCC with 4-byte package length descriptor that points past buffer length
            testCases.Add(ppccHeader.Concat(new byte[] { 0x12, 0x8F, 0xFF, 0xFF, 0xFF, 0x02 }).ToArray());

            // Case 5: PPCC with NumElements 255 but only 2 bytes following
            testCases.Add(ppccHeader.Concat(new byte[] { 0x12, 0x20, 0xFF, 0x0A, 0x01 }).ToArray());

            // Case 6: PPCC with nested package having corrupt inner package length
            testCases.Add(ppccHeader.Concat(new byte[] {
                0x12, 0x20, 0x02, // PackageOp, len 32, 2 elements
                0x0C, 0x02, 0x00, 0x00, 0x00, // Element 0: DWord 2 (Revision)
                0x12, 0xFF, 0x06 // Element 1: Inner Package with invalid length 0xFF
            }).ToArray());

            // Case 7: PPCC with inner package having negative or out-of-range milliwatt integers
            var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(ppccHeader);
                writer.Write((byte)0x12); // PackageOp
                writer.Write((byte)40);   // Length
                writer.Write((byte)2);    // 2 elements (Rev + 1 subpackage)
                writer.Write((byte)0x0C); writer.Write((uint)2); // Revision 2

                // Inner subpackage
                writer.Write((byte)0x12); // Inner PackageOp
                writer.Write((byte)30);
                writer.Write((byte)6);    // 6 elements

                // PowerLimitIndex = 0
                writer.Write((byte)0x0C); writer.Write((uint)0);
                // MinPower = 999,999,999 mW (insane)
                writer.Write((byte)0x0C); writer.Write((uint)999999999);
                // MaxPower = 1 mW (< MinPower)
                writer.Write((byte)0x0C); writer.Write((uint)1);
                // Time windows & step
                writer.Write((byte)0x0C); writer.Write((uint)0);
                writer.Write((byte)0x0C); writer.Write((uint)0);
                writer.Write((byte)0x0C); writer.Write((uint)0);
            }
            testCases.Add(ms.ToArray());

            // Case 8: Repeated invalid opcodes attempting infinite parser recursion
            byte[] recursiveTraps = ppccHeader.Concat(Enumerable.Repeat((byte)0x12, 100)).ToArray();
            testCases.Add(recursiveTraps);

            foreach (var testCase in testCases)
            {
                var entries = BiosUefiPowerScanner.ExtractPpccEntriesFromAml(testCase);
                Assert.NotNull(entries);

                var parsed = BiosUefiPowerScanner.ParsePpccFromAml(testCase);
                if (parsed.HasValue)
                {
                    Assert.InRange(parsed.Value.MinPower, 0, 500);
                    Assert.InRange(parsed.Value.MaxPl1, 0, 500);
                    Assert.InRange(parsed.Value.MaxPl2, 0, 500);
                }
            }
        }

        #endregion

        #region 2. MSR Out-of-Range & Silicon Fuzzing

        [Fact]
        public void BiosPowerScanner_FuzzMsrRaplUnits_ExtremeBitShifts_NoOverflowOrDivideByZero()
        {
            var scanner = new BiosUefiPowerScanner();

            // RAPL power units raw values testing bit-shift overflows and division by zero:
            // bits 3:0 specify power unit exponent (1 / 2^PU).
            ulong[] chaoticUnits = {
                0UL,                                      // PU = 0 -> unitsPerWatt = 1
                1UL,                                      // PU = 1 -> unitsPerWatt = 2
                3UL,                                      // PU = 3 -> unitsPerWatt = 8
                15UL,                                     // PU = 15 -> unitsPerWatt = 32768
                16UL,                                     // PU = 16 -> masked to 0
                31UL,                                     // PU = 31 -> 1 << 31
                32UL,                                     // PU = 32 -> shift overflow test
                63UL,                                     // PU = 63 -> shift overflow test
                0xFFFFFFFFUL,                             // Maximum 32-bit
                ulong.MaxValue                            // All bits set
            };

            foreach (ulong rawUnit in chaoticUnits)
            {
                var mockBackend = new MockPawnIoBackend(isLoaded: true);
                mockBackend.WriteMsr(PawnIoController.MSR_RAPL_POWER_UNIT, rawUnit);

                // Normal PKG Power info (TDP = 55W, Min = 25W, Max = 157W) with 8 units/W
                ulong rawInfo = ((ulong)(55 * 8) & 0x7FFFUL) |
                                (((ulong)(25 * 8) & 0x7FFFUL) << 16) |
                                (((ulong)(157 * 8) & 0x7FFFUL) << 32);
                mockBackend.WriteMsr(PawnIoController.MSR_PKG_POWER_INFO, rawInfo);

                var scanResult = scanner.ScanPowerLimits(mockBackend);
                Assert.NotNull(scanResult);
                Assert.NotNull(scanResult.SiliconData);

                if (scanResult.SiliconData.IsFound)
                {
                    Assert.True(scanResult.SiliconData.UnitsPerWatt > 0);
                    Assert.True(scanResult.SiliconData.ThermalSpecPowerWatts >= 0);
                }
            }
        }

        [Fact]
        public void BiosPowerScanner_FuzzMsrPkgPowerInfo_ExtremeAndInvertedValues()
        {
            var scanner = new BiosUefiPowerScanner();
            var mockBackend = new MockPawnIoBackend(isLoaded: true);
            mockBackend.ConfigureIntelBaseline();

            ulong[] chaoticInfos = {
                0UL,                                      // All zeros
                ulong.MaxValue,                           // All bits set
                0x7FFF7FFF7FFF7FFFUL,                     // Max 15-bit fields
                1UL << 63,                                // Lock bit only
                ((ulong)100 << 16) | ((ulong)10 << 32),   // Inverted: Min = 100W, Max = 10W
                0x7FFFUL,                                 // Thermal spec only
            };

            foreach (ulong rawInfo in chaoticInfos)
            {
                mockBackend.WriteMsr(PawnIoController.MSR_PKG_POWER_INFO, rawInfo);

                var scanResult = scanner.ScanPowerLimits(mockBackend);
                Assert.NotNull(scanResult);
                Assert.NotNull(scanResult);
                // Synthesized limits must remain bounded by architectural sanity invariants
                Assert.True(scanResult.CpuMinAllowedPowerWatts >= 0);
                Assert.True(scanResult.CpuMaxAllowedPl2Watts >= 0);
            }
        }

        #endregion

        #region 3. PawnIoController Driver Failure & Lock Bit Fuzzing

        [Fact]
        public void PawnIoController_FuzzSimulatedDriverErrors_ReturnsAccurateStatuses()
        {
            var mockBackend = new MockPawnIoBackend(isLoaded: true);
            var controller = new PawnIoController(mockBackend);

            // 1. Hardware Error
            mockBackend.SimulateHardwareError = true;
            var resHw = controller.ApplyPowerLimits(55, 115);
            Assert.Equal(PowerLimitApplyStatus.HardwareError, resHw.Status);
            Assert.False(resHw.IsSuccess);
            mockBackend.SimulateHardwareError = false;

            // 2. Access Denied
            mockBackend.SimulateAccessDenied = true;
            var resAccess = controller.ApplyPowerLimits(55, 115);
            Assert.Equal(PowerLimitApplyStatus.AccessDenied, resAccess.Status);
            mockBackend.SimulateAccessDenied = false;

            // 3. Driver Not Installed
            mockBackend.IsDriverLoaded = false;
            var resUnloaded = controller.ApplyPowerLimits(55, 115);
            Assert.Equal(PowerLimitApplyStatus.DriverNotInstalled, resUnloaded.Status);
            mockBackend.IsDriverLoaded = true;

            // 4. Unsupported Hardware
            mockBackend.SimulateUnsupportedHardware = true;
            var resUnsupported = controller.ApplyPowerLimits(55, 115);
            Assert.Equal(PowerLimitApplyStatus.UnsupportedHardware, resUnsupported.Status);
            mockBackend.SimulateUnsupportedHardware = false;
        }

        [Fact]
        public void PawnIoController_LockedMsr_Bit63Set_ReturnsBiosLocked()
        {
            var mockBackend = new MockPawnIoBackend(isLoaded: true);
            mockBackend.ConfigureIntelBaseline();
            var controller = new PawnIoController(mockBackend);

            // Simulate lock bit set on MSR 0x610
            mockBackend.SimulateBiosLocked = true;

            var res = controller.ApplyPowerLimits(65, 120);
            Assert.Equal(PowerLimitApplyStatus.BiosLocked, res.Status);
            Assert.False(res.IsSuccess);
            Assert.Contains("lock", res.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(-50, -100)]
        [InlineData(0, 0)]
        [InlineData(999999, 999999)]
        [InlineData(150, 45)] // Inverted: PL1 > PL2
        public void PawnIoController_AdversarialPowerLimitParameters_ClampsGracefully(int pl1, int pl2)
        {
            var mockBackend = new MockPawnIoBackend(isLoaded: true);
            mockBackend.ConfigureIntelBaseline();
            var controller = new PawnIoController(mockBackend);

            var res = controller.ApplyPowerLimits(pl1, pl2);
            // Must succeed or return valid status, clamped safely
            Assert.True(res.Status == PowerLimitApplyStatus.Success || res.Status == PowerLimitApplyStatus.UnsupportedHardware);
            if (res.IsSuccess)
            {
                Assert.True(res.AppliedPl1Watts >= 1);
                Assert.True(res.AppliedPl2Watts >= res.AppliedPl1Watts);
            }
        }

        #endregion

        #region 4. WMI & EC Channel Error Recovery & Fallback Fuzzing

        private sealed class FailingOperatingModeChannel : IOperatingModeChannel
        {
            public ModeTransportKind Transport => ModeTransportKind.EcHidFeature0x02;
            public IReadOnlyList<OperatingMode> SupportedModes => new[] { OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo };
            public bool ShouldThrow { get; set; } = true;

            public OperatingMode? Read()
            {
                if (ShouldThrow) throw new TimeoutException("Simulated EC HID Read Timeout");
                return null;
            }

            public bool Write(OperatingMode mode)
            {
                if (ShouldThrow) throw new InvalidOperationException("Simulated EC HID Write Failure");
                return false;
            }

            public bool TryGetOperatingMode(out OperatingMode mode)
            {
                mode = OperatingMode.Balanced;
                if (ShouldThrow) return false;
                return true;
            }

            public bool TrySetOperatingMode(OperatingMode mode)
            {
                if (ShouldThrow) return false;
                return true;
            }
        }

        private sealed class WorkingOperatingModeChannel : IOperatingModeChannel
        {
            public ModeTransportKind Transport => ModeTransportKind.WmiMisc0x0B;
            public IReadOnlyList<OperatingMode> SupportedModes => new[] { OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo, OperatingMode.Eco };
            public OperatingMode CurrentMode { get; set; } = OperatingMode.Performance;

            public OperatingMode? Read() => CurrentMode;
            public bool Write(OperatingMode mode) { CurrentMode = mode; return true; }
            public bool TryGetOperatingMode(out OperatingMode mode) { mode = CurrentMode; return true; }
            public bool TrySetOperatingMode(OperatingMode mode) { CurrentMode = mode; return true; }
        }

        [Fact]
        public void OperatingModeChannelRouter_PrimaryThrowsOrFails_SeamlesslyFallsBackToSecondary()
        {
            var failingPrimary = new FailingOperatingModeChannel();
            var workingSecondary = new WorkingOperatingModeChannel();

            var router = new OperatingModeChannelRouter(failingPrimary, workingSecondary);

            // 1. Read() should catch TimeoutException from primary and read from secondary
            var mode = router.Read();
            Assert.Equal(OperatingMode.Performance, mode);

            // 2. Write() should catch exception and write to secondary
            bool writeSuccess = router.Write(OperatingMode.Turbo);
            Assert.True(writeSuccess);
            Assert.Equal(OperatingMode.Turbo, workingSecondary.CurrentMode);

            // 3. TryGetOperatingMode should fallback to secondary
            bool getSuccess = router.TryGetOperatingMode(out var outMode);
            Assert.True(getSuccess);
            Assert.Equal(OperatingMode.Turbo, outMode);

            // 4. TrySetOperatingMode should fallback to secondary
            bool setSuccess = router.TrySetOperatingMode(OperatingMode.Quiet);
            Assert.True(setSuccess);
            Assert.Equal(OperatingMode.Quiet, workingSecondary.CurrentMode);
        }

        [Fact]
        public void OperatingModeChannelRouter_BothChannelsFail_ReturnsSafeFalseOrNullWithoutCrashing()
        {
            var failingPrimary = new FailingOperatingModeChannel { ShouldThrow = true };
            var failingSecondary = new FailingOperatingModeChannel { ShouldThrow = true };

            var router = new OperatingModeChannelRouter(failingPrimary, failingSecondary);

            // Must not throw unhandled exceptions
            Assert.Null(router.Read());
            Assert.False(router.Write(OperatingMode.Performance));
            Assert.False(router.TryGetOperatingMode(out _));
            Assert.False(router.TrySetOperatingMode(OperatingMode.Performance));
        }

        [Fact]
        public void WmiFanControlChannel_SimulatedCommandFailure_HandlesGracefully()
        {
            // Create a channel where underlying functions throw or return false
            var channel = new WmiFanControlChannel(
                setBehavior: (mode, apply) => false,
                setSpeed: (fan, pct, force) => false,
                setChannelSpeed: (ch, pct) => false,
                getRpm: (ch) => -1
            );

            // Calling operations must catch exceptions and return false
            Assert.False(channel.SetFanBehavior(0x01, true));
            Assert.False(channel.TrySetFanBehavior(0x01));
            Assert.False(channel.SetFanSpeed(1, 50, false));
            Assert.False(channel.TrySetFanSpeed(FanChannel.Cpu, 50));
            Assert.Equal(-1, channel.GetFanRpm(FanChannel.Cpu));
        }

        #endregion
    }
}
