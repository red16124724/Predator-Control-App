using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PredatorControlApp;
using MockPawnIoBackend = PredatorControlApp.MockPawnIoBackend;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class PawnIoControllerTests
    {
        [Fact]
        public void PowerUnits_UnitsPerWatt_MatchesFormulaOneShiftRawAnd0xF()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            // Test PU = 3 (1/8 W = 0.125 W) -> UnitsPerWatt = 1 << 3 = 8
            mock.SetMsr(PawnIoController.MSR_RAPL_POWER_UNIT, 0x000A0E03UL);
            Assert.True(controller.ReadPowerUnits(out uint units3, out ulong raw3));
            Assert.Equal(8U, units3);
            Assert.Equal(0x000A0E03UL, raw3);

            // Test PU = 4 (1/16 W = 0.0625 W) -> UnitsPerWatt = 1 << 4 = 16
            mock.SetMsr(PawnIoController.MSR_RAPL_POWER_UNIT, 0x000A0E04UL);
            Assert.True(controller.ReadPowerUnits(out uint units4, out _));
            Assert.Equal(16U, units4);

            // Test PU = 0 -> UnitsPerWatt = 1 << 0 = 1
            mock.SetMsr(PawnIoController.MSR_RAPL_POWER_UNIT, 0x000A0E00UL);
            Assert.True(controller.ReadPowerUnits(out uint units0, out _));
            Assert.Equal(1U, units0);

            // Test completely zero register (unsupported)
            mock.SetMsr(PawnIoController.MSR_RAPL_POWER_UNIT, 0UL);
            Assert.False(controller.ReadPowerUnits(out _, out _));
        }

        [Fact]
        public void PowerInfo_UnpacksThermalSpecMinMaxWatts_Accurately()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            uint units = 8;
            // ThermalSpec = 55W (440 units = 0x01B8), Min = 15W (120 units = 0x0078), Max = 175W (1400 units = 0x0578)
            ulong rawInfo = (1400UL << 32) | (120UL << 16) | 440UL;
            mock.SetMsr(PawnIoController.MSR_PKG_POWER_INFO, rawInfo);

            bool ok = controller.ReadPowerInfo(units, out int thermalSpec, out int minWatts, out int maxWatts, out ulong readRaw);
            Assert.True(ok);
            Assert.Equal(55, thermalSpec);
            Assert.Equal(15, minWatts);
            Assert.Equal(175, maxWatts);
            Assert.Equal(rawInfo, readRaw);
        }

        [Fact]
        public void PowerLimits_UnpacksPl1Pl2AndLockBit_Accurately()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            uint units = 8;
            // PL1 = 55W (440 units), PL2 = 115W (920 units = 0x0398), Lock = 1
            ulong rawLimit = 440UL | (1UL << 15) | (1UL << 16) | (0x28UL << 17) |
                             (920UL << 32) | (1UL << 47) | (1UL << 48) | (0x10UL << 49) |
                             (1UL << 63);
            mock.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT, rawLimit);

            bool ok = controller.ReadPowerLimits(units, out int pl1, out int pl2, out bool locked, out ulong readRaw);
            Assert.True(ok);
            Assert.Equal(55, pl1);
            Assert.Equal(115, pl2);
            Assert.True(locked);
            Assert.Equal(rawLimit, readRaw);
        }

        [Fact]
        public void ScanDevicePowerCapabilities_Tdp55_Min0_Max157_CalculatesExpectedFormulas()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            mock.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 0,
                maxWatts: 157,
                pl1Watts: 55,
                pl2Watts: 157,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true);

            var caps = controller.ScanDevicePowerCapabilities();

            Assert.True(caps.IsSupported);
            Assert.False(caps.IsLocked);
            Assert.True(caps.IsMmioAccessible);
            Assert.Equal(8U, caps.UnitsPerWatt);
            Assert.Equal(55, caps.ThermalSpecPowerWatts);
            Assert.Equal(0, caps.MinimumPowerWatts);
            Assert.Equal(157, caps.MaximumPowerWatts);
            Assert.Equal(55, caps.CurrentPl1Watts);
            Assert.Equal(157, caps.CurrentPl2Watts);

            // Formula checks:
            // ScannedMinPowerWatts = MinimumPowerWatts > 0 ? MinimumPowerWatts : Math.Max(5, (int)Math.Round(ThermalSpecPowerWatts * 0.20))
            // 55 * 0.20 = 11.0 -> Math.Max(5, 11) = 11
            Assert.Equal(11, caps.ScannedMinPowerWatts);

            // ScannedMaxPl2Watts = MaximumPowerWatts > 0 && MaximumPowerWatts >= CurrentPl2Watts ? MaximumPowerWatts : Math.Max(CurrentPl2Watts, (int)Math.Round(ThermalSpecPowerWatts * 2.6))
            // 157 > 0 && 157 >= 157 -> 157
            Assert.Equal(157, caps.ScannedMaxPl2Watts);

            // ScannedMaxPl1Watts = Math.Max(CurrentPl1Watts, (int)Math.Round(ScannedMaxPl2Watts * 0.85))
            // Math.Max(55, (int)Math.Round(157 * 0.85 = 133.45 -> 133)) -> 133
            Assert.Equal(133, caps.ScannedMaxPl1Watts);
        }

        [Fact]
        public void ScanDevicePowerCapabilities_WithExplicitMinPower_UsesExplicitMin()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            mock.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 25,
                maxWatts: 157,
                pl1Watts: 55,
                pl2Watts: 157,
                unitsPerWatt: 8);

            var caps = controller.ScanDevicePowerCapabilities();

            // Explicit Min > 0 should be used directly
            Assert.Equal(25, caps.ScannedMinPowerWatts);
        }

        [Fact]
        public void ScanDevicePowerCapabilities_WithZeroMaxPower_CalculatesFromTdp2Point6()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            mock.ConfigureIntelBaseline(
                tdpWatts: 45,
                minWatts: 0,
                maxWatts: 0,
                pl1Watts: 45,
                pl2Watts: 90,
                unitsPerWatt: 8);

            var caps = controller.ScanDevicePowerCapabilities();

            // ScannedMin = Math.Max(5, Round(45 * 0.20 = 9)) = 9
            Assert.Equal(9, caps.ScannedMinPowerWatts);

            // ScannedMaxPl2: Max is 0, so Math.Max(90, Round(45 * 2.6 = 117)) = 117
            Assert.Equal(117, caps.ScannedMaxPl2Watts);

            // ScannedMaxPl1: Math.Max(45, Round(117 * 0.85 = 99.45 -> 99)) = 99
            Assert.Equal(99, caps.ScannedMaxPl1Watts);
        }

        [Fact]
        public void ScanDevicePowerCapabilities_WithMaxPowerLessThanCurrentPl2_ClampsToCurrentOrFormula()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            // MaximumPowerWatts (100) < CurrentPl2Watts (140)
            mock.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 0,
                maxWatts: 100,
                pl1Watts: 55,
                pl2Watts: 140,
                unitsPerWatt: 8);

            var caps = controller.ScanDevicePowerCapabilities();

            // Since MaximumPowerWatts (100) >= CurrentPl2Watts (140) is false:
            // Math.Max(140, Round(55 * 2.6 = 143)) = 143
            Assert.Equal(143, caps.ScannedMaxPl2Watts);
            // Math.Max(55, Round(143 * 0.85 = 121.55 -> 122)) = 122
            Assert.Equal(122, caps.ScannedMaxPl1Watts);
        }

        [Fact]
        public void ApplyPowerLimits_Success_PreservesTau_SetsEnableAndClamping_MirrorsMmio()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            mock.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 15,
                maxWatts: 157,
                pl1Watts: 55,
                pl2Watts: 157,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true);

            // Apply new power limits: PL1 = 65W, PL2 = 115W
            var result = controller.ApplyPowerLimits(65, 115);

            Assert.True(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.Success, result.Status);
            Assert.Equal(65, result.AppliedPl1Watts);
            Assert.Equal(115, result.AppliedPl2Watts);
            Assert.True(result.MmioMirrored);

            // Verify written MSR value:
            ulong writtenMsr = mock.GetMsr(PawnIoController.MSR_PKG_POWER_LIMIT);
            Assert.Equal(result.RawMsrWritten, writtenMsr);

            // PL1 = 65 * 8 = 520 (0x0208)
            ulong pl1Bits = writtenMsr & 0x7FFFUL;
            Assert.Equal(520UL, pl1Bits);

            // PL2 = 115 * 8 = 920 (0x0398)
            ulong pl2Bits = (writtenMsr >> 32) & 0x7FFFUL;
            Assert.Equal(920UL, pl2Bits);

            // Enable bits (15 and 47) must be set
            Assert.NotEqual(0UL, writtenMsr & (1UL << 15));
            Assert.NotEqual(0UL, writtenMsr & (1UL << 47));

            // Clamping bits (16 and 48) must be set
            Assert.NotEqual(0UL, writtenMsr & (1UL << 16));
            Assert.NotEqual(0UL, writtenMsr & (1UL << 48));

            // Tau bits (23:17 and 55:49) must be preserved from baseline
            ulong tau1Bits = writtenMsr & (0x7FUL << 17);
            ulong tau2Bits = writtenMsr & (0x7FUL << 49);
            Assert.Equal(0x28UL << 17, tau1Bits);
            Assert.Equal(0x10UL << 49, tau2Bits);

            // Lock bit (63) must NOT be set
            Assert.Equal(0UL, writtenMsr & (1UL << 63));

            // Verify MMIO mirror at 0xFED159A0
            ulong writtenMmio = mock.GetMmio(PawnIoController.MMIO_PKG_RAPL_LIMIT_ADDRESS);
            Assert.Equal(result.RawMmioWritten, writtenMmio);
            Assert.Equal(writtenMsr, writtenMmio);
        }

        [Fact]
        public void ApplyPowerLimits_WhenBiosLocked_ReturnsBiosLocked_DoesNotWrite()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            mock.ConfigureIntelBaseline(locked: true);

            var result = controller.ApplyPowerLimits(60, 90);

            Assert.False(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.BiosLocked, result.Status);
            Assert.Contains("locked", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ApplyPowerLimits_WhenMmioInaccessible_StillSucceedsWithMsrOnly()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            mock.ConfigureIntelBaseline(mirrorMmio: false);
            mock.IsMmioAccessible = false;

            var result = controller.ApplyPowerLimits(60, 90);

            Assert.True(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.Success, result.Status);
            Assert.False(result.MmioMirrored);
            Assert.NotEqual(0UL, result.RawMsrWritten);
        }

        [Fact]
        public void ApplyPowerLimits_WhenDriverNotInstalled_ReturnsDriverNotInstalled()
        {
            using var mock = new MockPawnIoBackend();
            mock.IsDriverInstalled = false;
            using var controller = new PawnIoController(mock);

            var result = controller.ApplyPowerLimits(60, 90);

            Assert.False(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.DriverNotInstalled, result.Status);
        }

        [Fact]
        public void ApplyPowerLimits_WhenAccessDenied_ReturnsAccessDenied()
        {
            using var mock = new MockPawnIoBackend();
            mock.SimulateAccessDenied = true;
            using var controller = new PawnIoController(mock);

            var result = controller.ApplyPowerLimits(60, 90);

            Assert.False(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.AccessDenied, result.Status);
        }

        [Fact]
        public void ApplyPowerLimits_WhenUnsupportedHardware_ReturnsUnsupportedHardware()
        {
            using var mock = new MockPawnIoBackend();
            mock.SimulateUnsupportedHardware = true;
            using var controller = new PawnIoController(mock);

            var result = controller.ApplyPowerLimits(60, 90);

            Assert.False(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.UnsupportedHardware, result.Status);
        }

        [Fact]
        public void ApplyPowerLimits_WhenHardwareError_ReturnsHardwareError()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            // Simulate hardware fault on writes
            mock.SimulateHardwareError = true;

            var result = controller.ApplyPowerLimits(60, 90);

            Assert.False(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.HardwareError, result.Status);
        }

        [Fact]
        public void ThreadSafety_ConcurrentScansAndApplies_ExecuteSafely()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            mock.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 10,
                maxWatts: 160,
                pl1Watts: 55,
                pl2Watts: 120,
                unitsPerWatt: 8);

            var tasks = new List<Task>();

            for (int i = 0; i < 20; i++)
            {
                int pl1 = 45 + (i % 10);
                int pl2 = 90 + (i % 10);
                tasks.Add(Task.Run(() =>
                {
                    var res = controller.ApplyPowerLimits(pl1, pl2);
                    Assert.True(res.IsSuccess);
                }));

                tasks.Add(Task.Run(() =>
                {
                    var caps = controller.ScanDevicePowerCapabilities();
                    Assert.True(caps.IsSupported);
                    Assert.True(caps.ScannedMinPowerWatts >= 5);
                }));
            }

            Task.WaitAll(tasks.ToArray());
        }

        [Fact]
        public void ScanDeviceForPawnIo_WhenAccessible_ReturnsAccessibleAndRunning()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            var scan = controller.ScanDeviceForPawnIo();

            Assert.True(scan.IsInstalled);
            Assert.True(scan.IsRunning);
            Assert.True(scan.IsAccessible);
            Assert.False(scan.RequiresElevation);
            Assert.Contains("active and accessible", scan.DiagnosticMessage);
        }

        [Fact]
        public void ScanDeviceForPawnIo_WhenAccessDenied_ReturnsRequiresElevationAndInstalled()
        {
            using var mock = new MockPawnIoBackend();
            mock.SimulateAccessDenied = true;
            using var controller = new PawnIoController(mock);

            var scan = controller.ScanDeviceForPawnIo();

            Assert.True(scan.IsInstalled);
            Assert.False(scan.IsAccessible);
            Assert.True(scan.RequiresElevation);
            Assert.Equal(5, scan.LastWin32Error);
            Assert.Contains("administrator elevation", scan.DiagnosticMessage);
        }

        [Fact]
        public void ScanDeviceForPawnIo_WhenNotInstalled_ReturnsNotInstalled()
        {
            using var mock = new MockPawnIoBackend();
            mock.IsDriverInstalled = false;
            using var controller = new PawnIoController(mock);

            var scan = controller.ScanDeviceForPawnIo();

            Assert.False(scan.IsInstalled);
            Assert.False(scan.IsAccessible);
            Assert.Contains("not installed", scan.DiagnosticMessage);
        }

        [Fact]
        public void EnsureInitialized_DoesNotShortCircuit_WhenIsDriverInstalledInitiallyFalse()
        {
            using var mock = new InitializingMockBackend();
            using var controller = new PawnIoController(mock);

            // Initially IsDriverInstalled is false
            Assert.False(mock.IsDriverInstalled);

            // Calling EnsureInitialized invokes Initialize() which initializes and sets IsDriverInstalled to true
            var status = controller.EnsureInitialized();

            Assert.Equal(PowerLimitApplyStatus.Success, status);
            Assert.True(mock.IsDriverInstalled);
            Assert.True(mock.IsOpen);
        }

        private sealed class InitializingMockBackend : IPawnIoBackend
        {
            private bool _installed;
            private bool _open;

            public bool IsDriverLoaded => _open;
            public bool IsOpen => _open;
            public bool IsDriverInstalled => _installed;
            public string DevicePath => @"\\.\MockPawnIO";

            public PowerLimitApplyStatus Initialize()
            {
                _installed = true;
                _open = true;
                return PowerLimitApplyStatus.Success;
            }

            public bool ReadMsr(uint msrIndex, out ulong value) { value = 0; return true; }
            public bool WriteMsr(uint msrIndex, ulong value) => true;
            public void Dispose() { _open = false; }
        }

        [Fact]
        public void EnsureDriverLoaded_AliasForEnsureInitialized()
        {
            using var mock = new InitializingMockBackend();
            using var controller = new PawnIoController(mock);

            var status = controller.EnsureDriverLoaded();
            Assert.Equal(PowerLimitApplyStatus.Success, status);
            Assert.True(mock.IsDriverInstalled);
        }

        [Fact]
        public void CandidateDevicePaths_ContainsAllRequiredSymlinkAndCaseVariants()
        {
            var expectedPaths = new[]
            {
                @"\\.\PawnIO",
                @"\\.\pawnio",
                @"\\.\PawnIo",
                @"\\?\GLOBALROOT\Device\PawnIO",
                @"\\?\GLOBALROOT\Device\pawnio",
                @"\\.\GLOBALROOT\Device\PawnIO",
                @"\\.\GLOBALROOT\Device\pawnio"
            };

            foreach (var expected in expectedPaths)
            {
                Assert.Contains(expected, NativePawnIoBackend.CandidateDevicePaths);
            }
            Assert.Equal(7, NativePawnIoBackend.CandidateDevicePaths.Length);
        }
        [Fact]
        public void WriteMchbarPowerLimits_WhenNewPl1GreaterThanOldPl2_WritesPl2UpperRegisterFirst()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            // Baseline: PL1 = 45W (360 units), PL2 = 65W (520 units)
            mock.ConfigureIntelBaseline(
                tdpWatts: 45,
                minWatts: 15,
                maxWatts: 115,
                pl1Watts: 45,
                pl2Watts: 65,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true);

            // Target limits: PL1 = 75W (600 units), PL2 = 115W (920 units)
            // Notice: newRawPl1 (600) > oldRawPl2 (520)
            ulong newRawPl1 = 75UL * 8UL; // 600
            ulong newRawPl2 = 115UL * 8UL; // 920

            bool ok = controller.WriteMchbarPowerLimits(newRawPl1, newRawPl2, out ulong rawMmioWritten);
            Assert.True(ok);
            Assert.NotEqual(0UL, rawMmioWritten);

            // Verify write order in history:
            var writes = new List<(string Op, ulong Target, ulong Value)>();
            foreach (var h in mock.History)
            {
                if (h.Op == "WriteMmio32")
                    writes.Add(h);
            }

            Assert.Equal(2, writes.Count);
            // Upper 32-bit register (0xFED159A4, PL2) must be written FIRST
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, writes[0].Target);
            Assert.Equal((ulong)(uint)(newRawPl2 | (1UL << 15) | (1UL << 16) | (0x10UL << 17)), writes[0].Value);

            // Lower 32-bit register (0xFED159A0, PL1) must be written SECOND
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, writes[1].Target);
            Assert.Equal((ulong)(uint)(newRawPl1 | (1UL << 15) | (1UL << 16) | (0x28UL << 17)), writes[1].Value);

            // Verify synthesized 64-bit MMIO value
            ulong finalMmio = mock.GetMmio(PawnIoController.MMIO_PKG_RAPL_LIMIT_ADDRESS);
            Assert.Equal(rawMmioWritten, finalMmio);
        }

        [Fact]
        public void WriteMchbarPowerLimits_WhenNewPl1LessThanOrEqualToOldPl2_WritesPl1LowerRegisterFirst()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            // Baseline: PL1 = 75W (600 units), PL2 = 115W (920 units)
            mock.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 15,
                maxWatts: 157,
                pl1Watts: 75,
                pl2Watts: 115,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true);

            // Target limits: PL1 = 35W (280 units), PL2 = 55W (440 units)
            // Notice: newRawPl1 (280) <= oldRawPl2 (920)
            ulong newRawPl1 = 35UL * 8UL; // 280
            ulong newRawPl2 = 55UL * 8UL; // 440

            bool ok = controller.WriteMchbarPowerLimits(newRawPl1, newRawPl2, out ulong rawMmioWritten);
            Assert.True(ok);
            Assert.NotEqual(0UL, rawMmioWritten);

            // Verify write order in history:
            var writes = new List<(string Op, ulong Target, ulong Value)>();
            foreach (var h in mock.History)
            {
                if (h.Op == "WriteMmio32")
                    writes.Add(h);
            }

            Assert.Equal(2, writes.Count);
            // Lower 32-bit register (0xFED159A0, PL1) must be written FIRST
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, writes[0].Target);
            Assert.Equal((ulong)(uint)(newRawPl1 | (1UL << 15) | (1UL << 16) | (0x28UL << 17)), writes[0].Value);

            // Upper 32-bit register (0xFED159A4, PL2) must be written SECOND
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, writes[1].Target);
            Assert.Equal((ulong)(uint)(newRawPl2 | (1UL << 15) | (1UL << 16) | (0x10UL << 17)), writes[1].Value);

            // Verify synthesized 64-bit MMIO value
            ulong finalMmio = mock.GetMmio(PawnIoController.MMIO_PKG_RAPL_LIMIT_ADDRESS);
            Assert.Equal(rawMmioWritten, finalMmio);
        }

        [Fact]
        public void WriteMchbarPowerLimits_WhenNewPl1EqualsOldPl2_WritesPl1LowerRegisterFirst()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            // Baseline: PL1 = 45W, PL2 = 60W (480 units)
            mock.ConfigureIntelBaseline(
                tdpWatts: 45,
                minWatts: 15,
                maxWatts: 115,
                pl1Watts: 45,
                pl2Watts: 60,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true);

            // Target limits: PL1 = 60W (480 units), PL2 = 90W (720 units)
            // Notice: newRawPl1 (480) == oldRawPl2 (480)
            ulong newRawPl1 = 60UL * 8UL;
            ulong newRawPl2 = 90UL * 8UL;

            bool ok = controller.WriteMchbarPowerLimits(newRawPl1, newRawPl2, out ulong rawMmioWritten);
            Assert.True(ok);

            var writes = new List<(string Op, ulong Target, ulong Value)>();
            foreach (var h in mock.History)
            {
                if (h.Op == "WriteMmio32")
                    writes.Add(h);
            }

            Assert.Equal(2, writes.Count);
            // Lower 32-bit register (0xFED159A0, PL1) must be written FIRST
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, writes[0].Target);
            // Upper 32-bit register (0xFED159A4, PL2) must be written SECOND
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, writes[1].Target);
        }

        [Fact]
        public void ApplyPowerLimits_HazardCondition_WritesPl2UpperRegisterFirst()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            // Baseline: PL1 = 45W, PL2 = 65W
            mock.ConfigureIntelBaseline(
                tdpWatts: 45,
                minWatts: 15,
                maxWatts: 115,
                pl1Watts: 45,
                pl2Watts: 65,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true);

            // Apply limits: PL1 = 70W, PL2 = 100W (new PL1 70W > old PL2 65W)
            var result = controller.ApplyPowerLimits(70, 100);
            Assert.True(result.IsSuccess);
            Assert.True(result.MmioMirrored);

            var writes = new List<(string Op, ulong Target, ulong Value)>();
            foreach (var h in mock.History)
            {
                if (h.Op == "WriteMmio32")
                    writes.Add(h);
            }

            Assert.Equal(2, writes.Count);
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, writes[0].Target);
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, writes[1].Target);
        }

        [Fact]
        public void WriteMsrPowerLimits_PreservesTauBits_AndWritesAtomically()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            // Baseline with custom Tau values: Tau1 = 0x35, Tau2 = 0x1A
            ulong customMsr = 440UL | (1UL << 15) | (1UL << 16) | (0x35UL << 17) |
                             (920UL << 32) | (1UL << 47) | (1UL << 48) | (0x1AUL << 49);
            mock.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT, customMsr);

            ulong newPl1 = 520UL; // 65W * 8
            ulong newPl2 = 1000UL; // 125W * 8

            bool ok = controller.WriteMsrPowerLimits(newPl1, newPl2, customMsr, out ulong writtenMsr);
            Assert.True(ok);

            // PL1 bits
            Assert.Equal(newPl1, writtenMsr & 0x7FFFUL);
            // PL2 bits
            Assert.Equal(newPl2, (writtenMsr >> 32) & 0x7FFFUL);

            // Enable & clamp bits
            Assert.NotEqual(0UL, writtenMsr & (1UL << 15));
            Assert.NotEqual(0UL, writtenMsr & (1UL << 16));
            Assert.NotEqual(0UL, writtenMsr & (1UL << 47));
            Assert.NotEqual(0UL, writtenMsr & (1UL << 48));

            // Preserved Tau bits
            Assert.Equal(0x35UL << 17, writtenMsr & (0x7FUL << 17));
            Assert.Equal(0x1AUL << 49, writtenMsr & (0x7FUL << 49));

            // Lock bit 63 must be 0
            Assert.Equal(0UL, writtenMsr & (1UL << 63));
        }

        [Fact]
        public void WriteMsrPowerLimits_WhenBiosLocked_ReturnsFalse()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            ulong lockedMsr = 440UL | (1UL << 63);
            mock.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT, lockedMsr);

            bool ok = controller.WriteMsrPowerLimits(500UL, 800UL, lockedMsr, out ulong writtenMsr);
            Assert.False(ok);
            Assert.Equal(0UL, writtenMsr);
        }

        [Fact]
        public void WriteMchbarPowerLimits_WhenMmioLocked_ReturnsFalse()
        {
            using var mock = new MockPawnIoBackend();
            using var controller = new PawnIoController(mock);

            // Locked MMIO (bit 63 set)
            ulong lockedMmio = 440UL | (1UL << 63);
            mock.SetMmio(PawnIoController.MMIO_PKG_RAPL_LIMIT_ADDRESS, lockedMmio);

            bool ok = controller.WriteMchbarPowerLimits(500UL, 800UL, out ulong rawMmioWritten);
            Assert.False(ok);
            Assert.Equal(0UL, rawMmioWritten);
        }
    }
}
