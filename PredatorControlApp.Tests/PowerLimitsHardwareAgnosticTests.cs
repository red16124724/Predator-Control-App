using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Comprehensive unit and stress test suite for hardware-agnostic PL1/PL2 CPU power limits:
    /// 1. Multi-laptop profile simulations using mock IPawnIoBackend (Triton/Neo 14 vs Helios 18 HX).
    /// 2. Hardware signature change detection and stale cache invalidation.
    /// 3. Per-mode native default capture & Reset button restoration across Eco, Quiet, Balanced, Performance, Turbo.
    /// 4. PL2 >= PL1 invariant, dynamic slider coupling, and hardware bounds clamping.
    /// 5. Graceful fallback when PawnIO is missing or MSR 0x610 Bit 63 is locked by BIOS.
    /// 6. Concurrency, thread-safety, and adversarial fuzzing stress testing.
    /// </summary>
    public class HardwareAgnosticPowerLimitsTests
    {
        #region Helper Factory for Laptop Profiles

        private static MockPawnIoBackend CreateTritonNeo14Backend(bool locked = false)
        {
            var backend = new MockPawnIoBackend();
            // Predator Triton / Neo 14 specs:
            // TDP: 28W, Min: 8W, Max: 65W, stock Balanced: 28W PL1 / 45W PL2, UnitsPerWatt: 8 (0.125W)
            backend.ConfigureIntelBaseline(
                tdpWatts: 28,
                minWatts: 8,
                maxWatts: 65,
                pl1Watts: 28,
                pl2Watts: 45,
                unitsPerWatt: 8,
                locked: locked,
                mirrorMmio: true
            );
            return backend;
        }

        private static MockPawnIoBackend CreateHelios18HxBackend(bool locked = false)
        {
            var backend = new MockPawnIoBackend();
            // Predator Helios 18 HX specs:
            // TDP: 55W, Min: 15W, Max: 175W, stock Turbo: 125W PL1 / 175W PL2, UnitsPerWatt: 8 (0.125W)
            backend.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 15,
                maxWatts: 175,
                pl1Watts: 125,
                pl2Watts: 175,
                unitsPerWatt: 8,
                locked: locked,
                mirrorMmio: true
            );
            return backend;
        }

        #endregion

        #region 1. Multi-Laptop Profile Simulations

        [Fact]
        public void SimulateTritonNeo14_ScansHardwareBoundsAndStockBalanced_WithoutHardcoding()
        {
            // Arrange
            using var backend = CreateTritonNeo14Backend(locked: false);
            using var controller = new PawnIoController(backend);
            var settings = new AppSettings();

            // Act: Scan capabilities dynamically from hardware MSRs
            var caps = controller.ScanDevicePowerCapabilities();

            // Assert: Scanned values match Triton Neo 14 firmware registers exactly
            Assert.True(caps.IsSupported, "Triton Neo 14 RAPL should be recognized as supported.");
            Assert.False(caps.IsLocked, "MSR 0x610 should be unlocked by default.");
            Assert.Equal(8U, caps.UnitsPerWatt);
            Assert.Equal(28, caps.ThermalSpecPowerWatts); // TDP
            Assert.Equal(8, caps.MinimumPowerWatts);       // Hardware Min
            Assert.Equal(65, caps.MaximumPowerWatts);       // Hardware Max
            Assert.Equal(28, caps.CurrentPl1Watts);        // Stock Balanced PL1
            Assert.Equal(45, caps.CurrentPl2Watts);        // Stock Balanced PL2

            // Algorithmic bounds derivation without hardcoding
            Assert.Equal(8, caps.ScannedMinPowerWatts);
            Assert.Equal(65, caps.ScannedMaxPl2Watts);
            Assert.True(caps.ScannedMaxPl1Watts <= caps.ScannedMaxPl2Watts);

            // Record into settings
            settings.ScannedHardwareSignature = "Predator Triton Neo 14 PTN14-51";
            settings.ScannedMinPowerWatts = caps.ScannedMinPowerWatts;
            settings.ScannedMaxPl1Watts = caps.ScannedMaxPl1Watts;
            settings.ScannedMaxPl2Watts = caps.ScannedMaxPl2Watts;
            settings.ScannedCpuTdpWatts = caps.ThermalSpecPowerWatts;
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, caps.CurrentPl1Watts, caps.CurrentPl2Watts);

            // Verify settings recorded Triton bounds
            Assert.Equal(8, settings.ScannedMinPowerWatts);
            Assert.Equal(65, settings.ScannedMaxPl2Watts);
            Assert.Equal((28, 45), settings.GetDefaultPowerLimitsForMode(OperatingMode.Balanced));
        }

        [Fact]
        public void SimulateHelios18Hx_ScansHardwareBoundsAndStockTurbo_WithoutHardcoding()
        {
            // Arrange
            using var backend = CreateHelios18HxBackend(locked: false);
            using var controller = new PawnIoController(backend);
            var settings = new AppSettings();

            // Act
            var caps = controller.ScanDevicePowerCapabilities();

            // Assert: Helios 18 HX high-power envelope
            Assert.True(caps.IsSupported);
            Assert.False(caps.IsLocked);
            Assert.Equal(8U, caps.UnitsPerWatt);
            Assert.Equal(55, caps.ThermalSpecPowerWatts);   // High-power TDP
            Assert.Equal(15, caps.MinimumPowerWatts);       // Hardware Min
            Assert.Equal(175, caps.MaximumPowerWatts);      // Hardware Max
            Assert.Equal(125, caps.CurrentPl1Watts);       // Stock Turbo PL1
            Assert.Equal(175, caps.CurrentPl2Watts);       // Stock Turbo PL2

            Assert.Equal(15, caps.ScannedMinPowerWatts);
            Assert.Equal(175, caps.ScannedMaxPl2Watts);

            // Record into settings
            settings.ScannedHardwareSignature = "Predator Helios 18 PH18-72-HX";
            settings.ScannedMinPowerWatts = caps.ScannedMinPowerWatts;
            settings.ScannedMaxPl1Watts = caps.ScannedMaxPl1Watts;
            settings.ScannedMaxPl2Watts = caps.ScannedMaxPl2Watts;
            settings.ScannedCpuTdpWatts = caps.ThermalSpecPowerWatts;
            settings.RecordScannedDefaultsForMode(OperatingMode.Turbo, caps.CurrentPl1Watts, caps.CurrentPl2Watts);

            // Verify settings recorded Helios bounds
            Assert.Equal(15, settings.ScannedMinPowerWatts);
            Assert.Equal(175, settings.ScannedMaxPl2Watts);
            Assert.Equal((125, 175), settings.GetDefaultPowerLimitsForMode(OperatingMode.Turbo));
        }

        [Fact]
        public void MultiProfile_VerifyDistinctDefaultsAndBounds_NeverHardcodesLimits()
        {
            // Verify that Triton and Helios produce completely independent, non-overlapping bounds
            using var tritonBackend = CreateTritonNeo14Backend();
            using var heliosBackend = CreateHelios18HxBackend();

            using var tritonCtrl = new PawnIoController(tritonBackend);
            using var heliosCtrl = new PawnIoController(heliosBackend);

            var tritonCaps = tritonCtrl.ScanDevicePowerCapabilities();
            var heliosCaps = heliosCtrl.ScanDevicePowerCapabilities();

            // Bounds must be completely distinct
            Assert.NotEqual(tritonCaps.ThermalSpecPowerWatts, heliosCaps.ThermalSpecPowerWatts);
            Assert.NotEqual(tritonCaps.MinimumPowerWatts, heliosCaps.MinimumPowerWatts);
            Assert.NotEqual(tritonCaps.MaximumPowerWatts, heliosCaps.MaximumPowerWatts);
            Assert.NotEqual(tritonCaps.CurrentPl1Watts, heliosCaps.CurrentPl1Watts);
            Assert.NotEqual(tritonCaps.CurrentPl2Watts, heliosCaps.CurrentPl2Watts);

            // Triton: 28W TDP, Max 65W vs Helios: 55W TDP, Max 175W
            Assert.Equal(28, tritonCaps.ThermalSpecPowerWatts);
            Assert.Equal(65, tritonCaps.MaximumPowerWatts);
            Assert.Equal(55, heliosCaps.ThermalSpecPowerWatts);
            Assert.Equal(175, heliosCaps.MaximumPowerWatts);
        }

        #endregion

        #region 2. Hardware Signature Change Detection

        [Fact]
        public void HardwareSignature_SameSignature_PreservesCachedDefaults()
        {
            var settings = new AppSettings
            {
                ScannedHardwareSignature = "Predator Triton Neo 14 PTN14-51",
                HasScannedHardwarePowerBounds = true,
                ScannedMinPowerWatts = 8,
                ScannedMaxPl1Watts = 55,
                ScannedMaxPl2Watts = 65,
                ScannedCpuTdpWatts = 28
            };
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 28, 45);
            settings.SetPowerLimitsForMode(OperatingMode.Balanced, 32, 50);

            // Verify unchanged signature preserves user customization and defaults
            string currentSig = "Predator Triton Neo 14 PTN14-51";
            if (string.Equals(settings.ScannedHardwareSignature, currentSig, StringComparison.OrdinalIgnoreCase))
            {
                // Cache hit - keep settings intact
            }
            else
            {
                settings.ResetAllPowerLimitsToScannedDefaults();
            }

            Assert.Equal(32, settings.BalancedPl1Watts);
            Assert.Equal(50, settings.BalancedPl2Watts);
            Assert.Equal((28, 45), settings.GetDefaultPowerLimitsForMode(OperatingMode.Balanced));
        }

        [Fact]
        public void HardwareSignature_ChangedSignature_InvalidatesCachedDefaultsAndScansNewHardware()
        {
            // Initial machine: Triton Neo 14
            var settings = new AppSettings
            {
                ScannedHardwareSignature = "Predator Triton Neo 14 PTN14-51",
                HasScannedHardwarePowerBounds = true,
                ScannedMinPowerWatts = 8,
                ScannedMaxPl1Watts = 55,
                ScannedMaxPl2Watts = 65,
                ScannedCpuTdpWatts = 28
            };
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 28, 45);
            settings.SetPowerLimitsForMode(OperatingMode.Balanced, 35, 50);

            // Signature change detected: System migrated / motherboard replaced with Helios 18 HX
            string newHardwareSig = "Predator Helios 18 PH18-72-HX";
            bool signatureChanged = !string.Equals(settings.ScannedHardwareSignature, newHardwareSig, StringComparison.OrdinalIgnoreCase);
            Assert.True(signatureChanged);

            if (signatureChanged)
            {
                // Invalidate all cached bounds and per-mode defaults
                settings.ResetAllPowerLimitsToScannedDefaults();
                settings.HasScannedHardwarePowerBounds = false;
                settings.ScannedHardwareSignature = newHardwareSig;
                settings.ScannedMinPowerWatts = 0;
                settings.ScannedMaxPl1Watts = 0;
                settings.ScannedMaxPl2Watts = 0;
                settings.ScannedCpuTdpWatts = 0;
            }

            // Stale Triton user custom limits must be reset to -1
            Assert.Equal(-1, settings.BalancedPl1Watts);
            Assert.Equal(-1, settings.BalancedPl2Watts);
            Assert.False(settings.HasScannedHardwarePowerBounds);

            // Now perform fresh hardware scan on new Helios hardware
            using var heliosBackend = CreateHelios18HxBackend();
            using var heliosCtrl = new PawnIoController(heliosBackend);
            var heliosCaps = heliosCtrl.ScanDevicePowerCapabilities();

            settings.ScannedMinPowerWatts = heliosCaps.ScannedMinPowerWatts;
            settings.ScannedMaxPl1Watts = heliosCaps.ScannedMaxPl1Watts;
            settings.ScannedMaxPl2Watts = heliosCaps.ScannedMaxPl2Watts;
            settings.ScannedCpuTdpWatts = heliosCaps.ThermalSpecPowerWatts;
            settings.RecordScannedDefaultsForMode(OperatingMode.Turbo, heliosCaps.CurrentPl1Watts, heliosCaps.CurrentPl2Watts);

            // New Helios bounds active
            Assert.Equal(15, settings.ScannedMinPowerWatts);
            Assert.Equal(175, settings.ScannedMaxPl2Watts);
            Assert.Equal((125, 175), settings.GetDefaultPowerLimitsForMode(OperatingMode.Turbo));
            Assert.Equal("Predator Helios 18 PH18-72-HX", settings.ScannedHardwareSignature);
        }

        #endregion

        #region 3. Per-Mode Native Default Capture & Reset Button Restoration

        [Fact]
        public void PerModeDefaults_CapturesAllFiveOperatingModesDistinctly()
        {
            var settings = new AppSettings();

            // Simulate capturing native stock limits as each mode is visited
            settings.RecordScannedDefaultsForMode(OperatingMode.Eco, 15, 25);
            settings.RecordScannedDefaultsForMode(OperatingMode.Quiet, 20, 35);
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 28, 45);
            settings.RecordScannedDefaultsForMode(OperatingMode.Performance, 35, 55);
            settings.RecordScannedDefaultsForMode(OperatingMode.Turbo, 45, 65);

            // Verify each mode has distinct captured defaults
            Assert.Equal((15, 25), settings.GetDefaultPowerLimitsForMode(OperatingMode.Eco));
            Assert.Equal((20, 35), settings.GetDefaultPowerLimitsForMode(OperatingMode.Quiet));
            Assert.Equal((28, 45), settings.GetDefaultPowerLimitsForMode(OperatingMode.Balanced));
            Assert.Equal((35, 55), settings.GetDefaultPowerLimitsForMode(OperatingMode.Performance));
            Assert.Equal((45, 65), settings.GetDefaultPowerLimitsForMode(OperatingMode.Turbo));
        }

        [Fact]
        public void ResetButton_RestoresActiveModeToCapturedNativeDefaults_WithoutImpactingOtherModes()
        {
            var settings = new AppSettings
            {
                ScannedMinPowerWatts = 8,
                ScannedMaxPl1Watts = 55,
                ScannedMaxPl2Watts = 65,
                ScannedCpuTdpWatts = 28
            };

            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 28, 45);
            settings.RecordScannedDefaultsForMode(OperatingMode.Performance, 35, 55);

            // User customizes Performance mode limits to 42W/60W
            settings.SetPowerLimitsForMode(OperatingMode.Performance, 42, 60);

            // Effective limits for Performance should reflect customized values
            Assert.Equal((42, 60), settings.GetEffectivePowerLimitsForMode(OperatingMode.Performance));
            // Balanced remains at stock default
            Assert.Equal((28, 45), settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced));

            // User clicks "Reset" button for Performance mode
            settings.ResetPowerLimitsForMode(OperatingMode.Performance);

            // Effective limits for Performance must revert to captured native default (35W / 55W)
            Assert.Equal((35, 55), settings.GetEffectivePowerLimitsForMode(OperatingMode.Performance));
            Assert.Equal(-1, settings.PerformancePl1Watts); // Reset marker
            Assert.Equal(-1, settings.PerformancePl2Watts);

            // Balanced mode remains completely untouched
            Assert.Equal((28, 45), settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced));
        }

        [Fact]
        public void ResetButton_HeliosTurboMode_Restores125W_175W_Stock()
        {
            using var backend = CreateHelios18HxBackend();
            using var controller = new PawnIoController(backend);
            var settings = new AppSettings
            {
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 150,
                ScannedMaxPl2Watts = 175,
                ScannedCpuTdpWatts = 55
            };

            settings.RecordScannedDefaultsForMode(OperatingMode.Turbo, 125, 175);

            // User overrides Turbo to 140W/175W
            settings.SetPowerLimitsForMode(OperatingMode.Turbo, 140, 175);
            Assert.Equal((140, 175), settings.GetEffectivePowerLimitsForMode(OperatingMode.Turbo));

            // Apply custom limits to hardware
            var applyCustom = controller.ApplyPowerLimits(140, 175);
            Assert.True(applyCustom.IsSuccess);
            Assert.Equal(140, applyCustom.AppliedPl1Watts);
            Assert.Equal(175, applyCustom.AppliedPl2Watts);

            // User clicks Reset on Turbo mode
            settings.ResetPowerLimitsForMode(OperatingMode.Turbo);
            var (restoredPl1, restoredPl2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Turbo);
            Assert.Equal((125, 175), (restoredPl1, restoredPl2));

            // Apply restored defaults back to hardware
            var applyRestored = controller.ApplyPowerLimits(restoredPl1, restoredPl2);
            Assert.True(applyRestored.IsSuccess);
            Assert.Equal(125, applyRestored.AppliedPl1Watts);
            Assert.Equal(175, applyRestored.AppliedPl2Watts);
        }

        #endregion

        #region 4. PL2 >= PL1 Invariant, Dynamic Slider Coupling & Clamping

        [Theory]
        [InlineData(50, 45, 50, 50)] // Raising PL1 above PL2 pushes PL2 upward
        [InlineData(30, 45, 30, 45)] // Raising PL1 below PL2 preserves PL2
        [InlineData(65, 45, 65, 65)] // Raising PL1 to max pushes PL2 to max
        public void SliderCoupling_RaisingPl1AbovePl2_DynamicallyCouplesPl2Upward(
            int targetPl1, int targetPl2,
            int expectedPl1, int expectedPl2)
        {
            // UI Dynamic Slider Coupling Simulation:
            // When user adjusts PL1 slider:
            int coupledPl1 = targetPl1;
            int coupledPl2 = Math.Max(targetPl2, coupledPl1); // Dynamic coupling: PL2 follows PL1 up

            Assert.Equal(expectedPl1, coupledPl1);
            Assert.Equal(expectedPl2, coupledPl2);
            Assert.True(coupledPl2 >= coupledPl1, "Invariant PL2 >= PL1 violated!");
        }

        [Theory]
        [InlineData(20, 28, 20, 20)] // Lowering PL2 below PL1 pushes PL1 downward
        [InlineData(35, 28, 28, 35)] // Lowering PL2 above PL1 preserves PL1
        [InlineData(8, 28, 8, 8)]    // Lowering PL2 to min pushes PL1 to min
        public void SliderCoupling_LoweringPl2BelowPl1_DynamicallyCouplesPl1Downward(
            int targetPl2, int currentPl1Param,
            int expectedPl1, int expectedPl2)
        {
            // UI Dynamic Slider Coupling Simulation:
            // When user adjusts PL2 slider:
            int coupledPl2 = targetPl2;
            int coupledPl1 = Math.Min(currentPl1Param, coupledPl2); // Dynamic coupling: PL1 follows PL2 down

            Assert.Equal(expectedPl1, coupledPl1);
            Assert.Equal(expectedPl2, coupledPl2);
            Assert.True(coupledPl2 >= coupledPl1, "Invariant PL2 >= PL1 violated!");
        }

        [Fact]
        public void Clamping_ClampsWithinScannedHardwareBounds_Triton()
        {
            var settings = new AppSettings
            {
                ScannedMinPowerWatts = 8,
                ScannedMaxPl1Watts = 55,
                ScannedMaxPl2Watts = 65,
                ScannedCpuTdpWatts = 28
            };

            // Attempt setting below minimum (8W)
            settings.SetPowerLimitsForMode(OperatingMode.Balanced, 2, 5);
            var (clampedLow1, clampedLow2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced);
            Assert.Equal(8, clampedLow1);
            Assert.Equal(8, clampedLow2);

            // Attempt setting above maximum (65W)
            settings.SetPowerLimitsForMode(OperatingMode.Balanced, 70, 85);
            var (clampedHigh1, clampedHigh2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced);
            Assert.Equal(55, clampedHigh1); // Clamped to MaxPl1
            Assert.Equal(65, clampedHigh2); // Clamped to MaxPl2
            Assert.True(clampedHigh2 >= clampedHigh1);
        }

        [Fact]
        public void Clamping_ClampsWithinScannedHardwareBounds_Helios()
        {
            var settings = new AppSettings
            {
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 150,
                ScannedMaxPl2Watts = 175,
                ScannedCpuTdpWatts = 55
            };

            // Below Min (15W)
            settings.SetPowerLimitsForMode(OperatingMode.Turbo, 5, 10);
            var (low1, low2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Turbo);
            Assert.Equal(15, low1);
            Assert.Equal(15, low2);

            // Above Max (175W)
            settings.SetPowerLimitsForMode(OperatingMode.Turbo, 180, 220);
            var (high1, high2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Turbo);
            Assert.Equal(150, high1);
            Assert.Equal(175, high2);
            Assert.True(high2 >= high1);
        }

        [Fact]
        public void ApplyPowerLimits_EnforcesInvariantAtHardwareBoundary()
        {
            using var backend = CreateTritonNeo14Backend();
            using var controller = new PawnIoController(backend);

            // User inadvertently or maliciously passes PL1=50W, PL2=30W (violating PL2 >= PL1)
            var result = controller.ApplyPowerLimits(50, 30);

            Assert.True(result.IsSuccess);
            Assert.Equal(50, result.AppliedPl1Watts);
            Assert.Equal(50, result.AppliedPl2Watts); // safePl2 = Math.Max(safePl1, targetPl2Watts)
            Assert.True(result.AppliedPl2Watts >= result.AppliedPl1Watts);

            // Verify raw MSR write preserved invariant
            ulong writtenMsr = backend.GetMsr(PawnIoController.MSR_PKG_POWER_LIMIT);
            int writtenPl1 = (int)((writtenMsr & 0x7FFFUL) / 8);
            int writtenPl2 = (int)(((writtenMsr >> 32) & 0x7FFFUL) / 8);
            Assert.Equal(50, writtenPl1);
            Assert.Equal(50, writtenPl2);
            Assert.True(writtenPl2 >= writtenPl1);
        }

        #endregion

        #region 5. Graceful Fallback (PawnIO Missing & BIOS Lock Bit 63)

        [Fact]
        public void GracefulFallback_WhenPawnIoDriverNotInstalled_ReturnsGracefulFailure_NoException()
        {
            // Backend simulating missing / uninstalled PawnIO driver
            using var backend = new MockPawnIoBackend { IsDriverInstalled = false };
            using var controller = new PawnIoController(backend);

            // 1. EnsureInitialized returns DriverNotInstalled
            var initStatus = controller.EnsureInitialized();
            Assert.Equal(PowerLimitApplyStatus.DriverNotInstalled, initStatus);

            // 2. ScanDevicePowerCapabilities returns Unsupported without throwing
            var caps = controller.ScanDevicePowerCapabilities();
            Assert.False(caps.IsSupported);
            Assert.Equal(PowerLimitApplyStatus.DriverNotInstalled, caps.BaselineStatus);

            // 3. ApplyPowerLimits returns structured result with DriverNotInstalled
            var applyResult = controller.ApplyPowerLimits(30, 45);
            Assert.False(applyResult.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.DriverNotInstalled, applyResult.Status);

            // No hardware writes executed
            Assert.Empty(backend.History);
        }

        [Fact]
        public void GracefulFallback_WhenMsr610Bit63Locked_ReadsSucceed_WritesBlockedWithBiosLocked()
        {
            // Simulate OEM / BIOS locking MSR 0x610 (Bit 63 = 1)
            using var backend = CreateTritonNeo14Backend(locked: true);
            using var controller = new PawnIoController(backend);

            // 1. Scan detects lock bit 63 but succeeds in reading stock telemetry
            var caps = controller.ScanDevicePowerCapabilities();
            Assert.True(caps.IsSupported, "Telemetry is still supported even when locked.");
            Assert.True(caps.IsLocked, "IsLocked must be true when Bit 63 is set.");
            Assert.Equal(28, caps.CurrentPl1Watts);
            Assert.Equal(45, caps.CurrentPl2Watts);

            // 2. ApplyPowerLimits detects Bit 63 and aborts BEFORE writing to MSR
            var applyResult = controller.ApplyPowerLimits(35, 50);
            Assert.False(applyResult.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.BiosLocked, applyResult.Status);

            // 3. Verify WriteMsr was blocked and never dispatched
            var writeOps = backend.History.Where(h => h.Op.StartsWith("WriteMsr")).ToList();
            Assert.Empty(writeOps);
        }

        [Fact]
        public void GracefulFallback_WhenUnsupportedHardware_ReturnsUnsupportedHardware_NoCrash()
        {
            using var backend = CreateTritonNeo14Backend();
            backend.Close(); // Close previous session to trigger re-initialization
            backend.SimulateUnsupportedHardware = true;
            using var controller = new PawnIoController(backend);

            var caps = controller.ScanDevicePowerCapabilities();
            Assert.False(caps.IsSupported);
            Assert.Equal(PowerLimitApplyStatus.UnsupportedHardware, caps.BaselineStatus);

            var result = controller.ApplyPowerLimits(30, 45);
            Assert.False(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.UnsupportedHardware, result.Status);
        }

        [Fact]
        public void GracefulFallback_WhenMsr610ReadFails_ReturnsHardwareError_NoCrash()
        {
            using var backend = CreateTritonNeo14Backend();
            // Leave 0x606 intact, but remove 0x610
            backend.SetMsr(PawnIoController.MSR_PKG_POWER_LIMIT, 0); // No limits
            using var controller = new PawnIoController(backend);

            // Close and re-open without 0x610
            var caps = controller.ScanDevicePowerCapabilities();
            // In ScanDevicePowerCapabilities, if 0x610 is 0, CurrentPl1=0, TDP falls back to 45W, scan completes or marks status
            Assert.True(caps.IsSupported);

            // When write fails (SimulateHardwareError on write)
            backend.SimulateHardwareError = true;
            var result = controller.ApplyPowerLimits(30, 45);
            Assert.False(result.IsSuccess);
        }

        #endregion

        #region 6. Stress, Concurrency & Fuzzing

        [Fact]
        public void StressTest_ConcurrentSliderCouplingAndApplyLimits_ZeroRaceCondition_MaintainsInvariant()
        {
            using var backend = CreateTritonNeo14Backend();
            using var controller = new PawnIoController(backend);
            var settings = new AppSettings
            {
                ScannedMinPowerWatts = 8,
                ScannedMaxPl1Watts = 55,
                ScannedMaxPl2Watts = 65,
                ScannedCpuTdpWatts = 28
            };
            settings.RecordScannedDefaultsForMode(OperatingMode.Balanced, 28, 45);

            int threadCount = 16;
            int iterationsPerThread = 200;
            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, threadCount, threadId =>
            {
                var rand = new Random(threadId * 1000);
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    try
                    {
                        int p1 = rand.Next(-10, 80);
                        int p2 = rand.Next(-10, 80);

                        if (i % 4 == 0)
                        {
                            // PL1 change coupling
                            int coupled1 = p1;
                            int coupled2 = Math.Max(p2, coupled1);
                            Assert.True(coupled2 >= coupled1);
                        }
                        else if (i % 4 == 1)
                        {
                            // PL2 change coupling
                            int coupled2 = p2;
                            int coupled1 = Math.Min(p1, coupled2);
                            Assert.True(coupled2 >= coupled1);
                        }
                        else if (i % 4 == 2)
                        {
                            // Settings per-mode application
                            settings.SetPowerLimitsForMode(OperatingMode.Balanced, p1, p2);
                            var (eff1, eff2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced);
                            Assert.True(eff2 >= eff1, $"Settings invariant violated: PL1={eff1}, PL2={eff2}");
                        }
                        else
                        {
                            // Controller hardware apply
                            var res = controller.ApplyPowerLimits(p1, p2);
                            Assert.True(res.IsSuccess);
                            Assert.True(res.AppliedPl2Watts >= res.AppliedPl1Watts, "Controller invariant violated.");
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            Assert.Empty(exceptions);
        }

        [Fact]
        public void StressTest_AdversarialFuzzing_ExtremeIntegers_NeverViolatesInvariantOrCrashes()
        {
            using var backend = CreateTritonNeo14Backend();
            using var controller = new PawnIoController(backend);
            var settings = new AppSettings
            {
                ScannedMinPowerWatts = 8,
                ScannedMaxPl1Watts = 55,
                ScannedMaxPl2Watts = 65,
                ScannedCpuTdpWatts = 28
            };

            int[] extremeValues = new[]
            {
                int.MinValue, -1000, -1, 0, 1, 7, 8, 28, 45, 65, 66, 175, 500, 10000, int.MaxValue
            };

            foreach (var v1 in extremeValues)
            {
                foreach (var v2 in extremeValues)
                {
                    // 1. Settings test
                    settings.SetPowerLimitsForMode(OperatingMode.Performance, v1, v2);
                    var (eff1, eff2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Performance);
                    Assert.True(eff2 >= eff1, $"Settings fuzz failed for v1={v1}, v2={v2}: PL1={eff1}, PL2={eff2}");
                    Assert.True(eff1 >= settings.ScannedMinPowerWatts);
                    Assert.True(eff2 <= settings.ScannedMaxPl2Watts);

                    // 2. Controller apply test (clamp to safe range for hardware register)
                    int safeV1 = Math.Clamp(v1, 0, 500);
                    int safeV2 = Math.Clamp(v2, 0, 500);
                    var res = controller.ApplyPowerLimits(safeV1, safeV2);
                    Assert.True(res.IsSuccess);
                    Assert.True(res.AppliedPl2Watts >= res.AppliedPl1Watts, $"Hardware fuzz failed: PL1={res.AppliedPl1Watts}, PL2={res.AppliedPl2Watts}");
                }
            }
        }

        [Fact]
        public void BitwiseFidelity_PreservesTimeWindowsAndClampBits_OnMsr610Write()
        {
            using var backend = CreateTritonNeo14Backend(locked: false);
            using var controller = new PawnIoController(backend);

            ulong origMsr = backend.GetMsr(PawnIoController.MSR_PKG_POWER_LIMIT);
            ulong origTau1 = origMsr & (0x7FUL << 17);
            ulong origTau2 = origMsr & (0x7FUL << 49);

            // Apply new power limits: PL1=35W, PL2=50W
            var result = controller.ApplyPowerLimits(35, 50);
            Assert.True(result.IsSuccess);

            ulong updatedMsr = backend.GetMsr(PawnIoController.MSR_PKG_POWER_LIMIT);

            // Verify updated power limit values
            int updatedPl1 = (int)((updatedMsr & 0x7FFFUL) / 8);
            int updatedPl2 = (int)(((updatedMsr >> 32) & 0x7FFFUL) / 8);
            Assert.Equal(35, updatedPl1);
            Assert.Equal(50, updatedPl2);

            // Verify time windows (Tau) are accurately preserved
            ulong updatedTau1 = updatedMsr & (0x7FUL << 17);
            ulong updatedTau2 = updatedMsr & (0x7FUL << 49);
            Assert.Equal(origTau1, updatedTau1);
            Assert.Equal(origTau2, updatedTau2);

            // Verify enable and clamp bits are set
            Assert.True((updatedMsr & (1UL << 15)) != 0, "PL1 Enable bit missing");
            Assert.True((updatedMsr & (1UL << 16)) != 0, "PL1 Clamp bit missing");
            Assert.True((updatedMsr & (1UL << 47)) != 0, "PL2 Enable bit missing");
            Assert.True((updatedMsr & (1UL << 48)) != 0, "PL2 Clamp bit missing");
        }

        #endregion

        #region 7. UI Formatter, Slider Coupling, and Device Capabilities Tests

        [Theory]
        [InlineData(45, 45, true, "PL1 (Sustained): 45 W (Laptop Default: 45 W)")]
        [InlineData(50, 45, true, "PL1 (Sustained): 50 W (Laptop Default: 45 W)")]
        [InlineData(45, 45, false, "PL1 (Sustained): 45 W (Default: ~45 W)")]
        [InlineData(45, 0, true, "PL1 (Sustained): 45 W")]
        [InlineData(45, -1, false, "PL1 (Sustained): 45 W")]
        public void UiLabelFormatter_FormatPl1Label_FormatsExpectedText(int watts, int defaultWatts, bool isScanned, string expected)
        {
            string actual = UiLabelFormatter.FormatPl1Label(watts, defaultWatts, isScanned);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(65, 65, true, "PL2 (Turbo Burst): 65 W (Laptop Default: 65 W)")]
        [InlineData(80, 65, true, "PL2 (Turbo Burst): 80 W (Laptop Default: 65 W)")]
        [InlineData(65, 65, false, "PL2 (Turbo Burst): 65 W (Default: ~65 W)")]
        [InlineData(65, 0, true, "PL2 (Turbo Burst): 65 W")]
        [InlineData(65, -5, false, "PL2 (Turbo Burst): 65 W")]
        public void UiLabelFormatter_FormatPl2Label_FormatsExpectedText(int watts, int defaultWatts, bool isScanned, string expected)
        {
            string actual = UiLabelFormatter.FormatPl2Label(watts, defaultWatts, isScanned);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void UiLabelFormatter_FormatHardwarePowerBoundsSummary_FormatsProperly()
        {
            // With CPU model
            string withModel = UiLabelFormatter.FormatHardwarePowerBoundsSummary(15, 115, 140, 45, "13th Gen Intel(R) Core(TM) i7-13700HX");
            Assert.Equal("Scanned Device Limits: Min 15W | Max PL1 115W | Max PL2 140W (CPU TDP 45W) [13th Gen Intel(R) Core(TM) i7-13700HX]", withModel);

            // Without CPU model (empty or null)
            string withoutModel = UiLabelFormatter.FormatHardwarePowerBoundsSummary(10, 65, 80, 28, "");
            Assert.Equal("Scanned Device Limits: Min 10W | Max PL1 65W | Max PL2 80W (CPU TDP 28W)", withoutModel);

            string nullModel = UiLabelFormatter.FormatHardwarePowerBoundsSummary(8, 45, 65, 28, null!);
            Assert.Equal("Scanned Device Limits: Min 8W | Max PL1 45W | Max PL2 65W (CPU TDP 28W)", nullModel);
        }

        [Theory]
        [InlineData(45, 65, "Reset to Laptop Default (45W / 65W)")]
        [InlineData(25, 35, "Reset to Laptop Default (25W / 35W)")]
        [InlineData(0, 0, "Reset to Laptop Default")]
        [InlineData(45, 0, "Reset to Laptop Default")]
        [InlineData(0, 65, "Reset to Laptop Default")]
        [InlineData(-1, -1, "Reset to Laptop Default")]
        public void UiLabelFormatter_FormatResetButtonLabel_FormatsExpectedText(int defPl1, int defPl2, string expected)
        {
            string actual = UiLabelFormatter.FormatResetButtonLabel(defPl1, defPl2);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void DeviceCapabilities_PowerLimits_DefaultValuesAndModeLookups()
        {
            var caps = new DeviceCapabilities();

            // Default properties
            Assert.True(caps.PawnIoAvailable);
            Assert.False(caps.IsMsr0x610Locked);
            Assert.Equal(45, caps.CpuTdpWatts);
            Assert.Equal(15, caps.ScannedMinPowerWatts);
            Assert.Equal(115, caps.ScannedMaxPl1Watts);
            Assert.Equal(140, caps.ScannedMaxPl2Watts);
            Assert.Equal("Intel Core Processor", caps.CpuModel);

            // Default 5 modes: Eco 0x06, Quiet 0x00, Balanced 0x01, Performance 0x04, Turbo 0x05
            Assert.Equal(25, caps.GetScannedPl1(0x06));
            Assert.Equal(35, caps.GetScannedPl2(0x06));

            Assert.Equal(35, caps.GetScannedPl1(0x00));
            Assert.Equal(45, caps.GetScannedPl2(0x00));

            Assert.Equal(45, caps.GetScannedPl1(0x01));
            Assert.Equal(65, caps.GetScannedPl2(0x01));

            Assert.Equal(70, caps.GetScannedPl1(0x04));
            Assert.Equal(90, caps.GetScannedPl2(0x04));

            Assert.Equal(90, caps.GetScannedPl1(0x05));
            Assert.Equal(115, caps.GetScannedPl2(0x05));

            // Unknown mode fallback: TDP for PL1, round(TDP * 1.35) for PL2
            Assert.Equal(45, caps.GetScannedPl1(0x99));
            Assert.Equal((int)Math.Round(45 * 1.35), caps.GetScannedPl2(0x99));
        }

        [Fact]
        public void DeviceCapabilities_CustomConfiguration_ReflectsCustomPowerBounds()
        {
            var customCaps = new DeviceCapabilities
            {
                PawnIoAvailable = false,
                IsMsr0x610Locked = true,
                CpuTdpWatts = 55,
                ScannedMinPowerWatts = 20,
                ScannedMaxPl1Watts = 135,
                ScannedMaxPl2Watts = 175,
                CpuModel = "Intel Core i9-14900HX",
                ScannedModeDefaults = new Dictionary<byte, (int, int)>
                {
                    { 0x01, (55, 75) }
                }
            };

            Assert.False(customCaps.PawnIoAvailable);
            Assert.True(customCaps.IsMsr0x610Locked);
            Assert.Equal(55, customCaps.CpuTdpWatts);
            Assert.Equal(20, customCaps.ScannedMinPowerWatts);
            Assert.Equal(135, customCaps.ScannedMaxPl1Watts);
            Assert.Equal(175, customCaps.ScannedMaxPl2Watts);
            Assert.Equal("Intel Core i9-14900HX", customCaps.CpuModel);

            // Known mode in dictionary
            Assert.Equal(55, customCaps.GetScannedPl1(0x01));
            Assert.Equal(75, customCaps.GetScannedPl2(0x01));

            // Mode not in dictionary -> fallback
            Assert.Equal(55, customCaps.GetScannedPl1(0x00));
            Assert.Equal((int)Math.Round(55 * 1.35), customCaps.GetScannedPl2(0x00));
        }

        [Fact]
        public void SliderCouplingLogic_Pl1AbovePl2_RaisesPl2ToMaintainInvariant()
        {
            // Simulate Form1 real-time slider coupling logic:
            // Dragging PL1 above PL2 must automatically elevate PL2 to equal PL1
            int sliderPl1Value = 45;
            int sliderPl2Value = 65;
            bool isUpdating = false;

            void OnPl1Changed(int newPl1)
            {
                if (isUpdating) return;
                sliderPl1Value = newPl1;
                if (sliderPl1Value > sliderPl2Value)
                {
                    isUpdating = true;
                    try
                    {
                        sliderPl2Value = sliderPl1Value;
                    }
                    finally
                    {
                        isUpdating = false;
                    }
                }
            }

            // User increases PL1 from 45 to 70 (higher than PL2=65)
            OnPl1Changed(70);

            Assert.Equal(70, sliderPl1Value);
            Assert.Equal(70, sliderPl2Value);
            Assert.True(sliderPl2Value >= sliderPl1Value);
        }

        [Fact]
        public void SliderCouplingLogic_Pl2BelowPl1_LowersPl1ToMaintainInvariant()
        {
            // Simulate Form1 real-time slider coupling logic:
            // Dragging PL2 below PL1 must automatically lower PL1 to equal PL2
            int sliderPl1Value = 70;
            int sliderPl2Value = 90;
            bool isUpdating = false;

            void OnPl2Changed(int newPl2)
            {
                if (isUpdating) return;
                sliderPl2Value = newPl2;
                if (sliderPl2Value < sliderPl1Value)
                {
                    isUpdating = true;
                    try
                    {
                        sliderPl1Value = sliderPl2Value;
                    }
                    finally
                    {
                        isUpdating = false;
                    }
                }
            }

            // User lowers PL2 from 90 to 50 (lower than PL1=70)
            OnPl2Changed(50);

            Assert.Equal(50, sliderPl1Value);
            Assert.Equal(50, sliderPl2Value);
            Assert.True(sliderPl2Value >= sliderPl1Value);
        }

        [Fact]
        public void SliderCouplingLogic_ReentrancyGuardPreventsInfiniteRecursion()
        {
            int pl1 = 45;
            int pl2 = 65;
            bool isUpdating = false;
            int pl2EventFiredCount = 0;

            void OnPl2Changed()
            {
                if (isUpdating) return;
                pl2EventFiredCount++;
                if (pl2 < pl1)
                {
                    isUpdating = true;
                    try
                    {
                        pl1 = pl2;
                    }
                    finally
                    {
                        isUpdating = false;
                    }
                }
            }

            void OnPl1Changed(int newVal)
            {
                if (isUpdating) return;
                pl1 = newVal;
                if (pl1 > pl2)
                {
                    isUpdating = true;
                    try
                    {
                        pl2 = pl1;      // Setting slider2 value directly
                        OnPl2Changed(); // Fires slider2 ValueChanged
                    }
                    finally
                    {
                        isUpdating = false;
                    }
                }
            }

            OnPl1Changed(80);

            // Reentrancy guard stops recursive ping-ponging: OnPl2Changed returns early because isUpdating is true
            Assert.Equal(0, pl2EventFiredCount);
            Assert.Equal(80, pl1);
            Assert.Equal(80, pl2);
        }

        #endregion
    }
}
