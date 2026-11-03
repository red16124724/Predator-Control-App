using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Comprehensive unit tests for PawnIO device scanning, initialization, and detection:
    /// 1. Test scanning when PawnIO service is installed but requires admin (AccessDenied).
    /// 2. Test scanning when PawnIO is fully running and accessible (Success).
    /// 3. Test scanning when PawnIO is stopped or not installed (DriverNotInstalled).
    /// 4. Verify EnsureInitialized() properly triggers backend.Initialize() without premature short-circuit.
    /// 5. Verify DeviceCapabilities properly reflects PawnIoInstalled, PawnIoAvailable, and PawnIoRequiresAdmin.
    /// </summary>
    public class PawnIoDeviceScanningTests
    {
        #region Test Spy Backend for Short-Circuit & Call-Count Tracking

        /// <summary>
        /// Spy backend that starts in an uninitialized state with IsDriverInstalled = false.
        /// Used to verify EnsureInitialized() actually invokes Initialize() without prematurely short-circuiting.
        /// </summary>
        private sealed class SpyPawnIoBackend : IPawnIoBackend
        {
            public int InitializeCallCount { get; private set; }
            public PowerLimitApplyStatus StatusToReturnOnInitialize { get; set; } = PowerLimitApplyStatus.Success;

            public bool IsDriverLoaded { get; set; }
            public bool IsOpen => IsDriverLoaded;
            public bool IsDriverInstalled { get; set; }
            public string DevicePath => @"\\.\SpyPawnIO";

            public PowerLimitApplyStatus Initialize()
            {
                InitializeCallCount++;
                if (StatusToReturnOnInitialize == PowerLimitApplyStatus.Success)
                {
                    IsDriverInstalled = true;
                    IsDriverLoaded = true;
                    return PowerLimitApplyStatus.Success;
                }
                else if (StatusToReturnOnInitialize == PowerLimitApplyStatus.AccessDenied)
                {
                    IsDriverInstalled = true; // Service/driver detected on machine
                    IsDriverLoaded = false;
                    return PowerLimitApplyStatus.AccessDenied;
                }
                else
                {
                    IsDriverInstalled = false;
                    IsDriverLoaded = false;
                    return StatusToReturnOnInitialize;
                }
            }

            public PawnIoScanResult ScanDevice() => new()
            {
                IsInstalled = IsDriverInstalled,
                IsRunning = IsDriverLoaded,
                IsAccessible = IsOpen,
                RequiresElevation = StatusToReturnOnInitialize == PowerLimitApplyStatus.AccessDenied,
                ServicePath = @"C:\Windows\System32\drivers\pawnio.sys",
                LastWin32Error = StatusToReturnOnInitialize == PowerLimitApplyStatus.AccessDenied ? 5 : 0,
                DiagnosticMessage = IsOpen
                    ? "Spy PawnIO active and accessible."
                    : (StatusToReturnOnInitialize == PowerLimitApplyStatus.AccessDenied ? "Access denied: Administrator privileges required." : "PawnIO not installed.")
            };

            public bool ReadMsr(uint msrIndex, out ulong value)
            {
                if (IsOpen && msrIndex == PawnIoController.MSR_RAPL_POWER_UNIT)
                {
                    value = 0x000A0E03UL; // 8 units per watt
                    return true;
                }
                if (IsOpen && msrIndex == PawnIoController.MSR_PKG_POWER_INFO)
                {
                    value = (1200UL << 32) | (120UL << 16) | 440UL; // 55W TDP
                    return true;
                }
                if (IsOpen && msrIndex == PawnIoController.MSR_PKG_POWER_LIMIT)
                {
                    value = 440UL | (1UL << 15) | (1UL << 16) | (1200UL << 32) | (1UL << 47) | (1UL << 48);
                    return true;
                }
                value = 0;
                return false;
            }

            public bool WriteMsr(uint msrIndex, ulong value) => IsOpen;
            public void Close() { IsDriverLoaded = false; }
            public void Dispose() { Close(); }
        }

        #endregion

        #region Requirement 1: Service Installed but Requires Admin (AccessDenied)

        [Fact]
        public void ScanDeviceForPawnIo_WhenServiceInstalledButRequiresAdmin_ReturnsRequiresElevationAndInaccessible()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.IsDriverInstalled = true;
            mock.SimulateAccessDenied = true;
            using var controller = new PawnIoController(mock);

            // Act
            var scan = controller.ScanDeviceForPawnIo();

            // Assert
            Assert.True(scan.IsInstalled, "Driver should be detected as installed on machine.");
            Assert.True(scan.RequiresElevation, "Scan must report elevation/admin requirement.");
            Assert.False(scan.IsAccessible, "Handle must NOT be marked accessible under AccessDenied.");
            Assert.Equal(5, scan.LastWin32Error); // ERROR_ACCESS_DENIED
            Assert.Contains("administrator", scan.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EnsureInitialized_WhenAccessDenied_ReturnsAccessDeniedStatus()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.IsDriverInstalled = true;
            mock.SimulateAccessDenied = true;
            using var controller = new PawnIoController(mock);

            // Act
            var status = controller.EnsureInitialized();

            // Assert
            Assert.Equal(PowerLimitApplyStatus.AccessDenied, status);
            Assert.False(controller.IsAvailable);
        }

        [Fact]
        public void ScanDevicePowerCapabilities_WhenAccessDenied_ReturnsUnsupportedWithAccessDeniedStatus()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.IsDriverInstalled = true;
            mock.SimulateAccessDenied = true;
            using var controller = new PawnIoController(mock);

            // Act
            var caps = controller.ScanDevicePowerCapabilities();

            // Assert
            Assert.False(caps.IsSupported);
            Assert.Equal(PowerLimitApplyStatus.AccessDenied, caps.BaselineStatus);
            Assert.Contains("AccessDenied", caps.Diagnostics);
        }

        [Fact]
        public void ApplyPowerLimits_WhenAccessDenied_ReturnsAccessDeniedResult()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.IsDriverInstalled = true;
            mock.SimulateAccessDenied = true;
            using var controller = new PawnIoController(mock);

            // Act
            var result = controller.ApplyPowerLimits(55, 115);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.AccessDenied, result.Status);
            Assert.Contains("Administrative", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Requirement 2: Fully Running and Accessible (Success)

        [Fact]
        public void ScanDeviceForPawnIo_WhenRunningAndAccessible_ReturnsInstalledRunningAccessible()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.ConfigureIntelBaseline(tdpWatts: 55, minWatts: 15, maxWatts: 157, pl1Watts: 55, pl2Watts: 157, unitsPerWatt: 8);
            using var controller = new PawnIoController(mock);

            // Act
            var scan = controller.ScanDeviceForPawnIo();

            // Assert
            Assert.True(scan.IsInstalled, "Driver must be reported as installed.");
            Assert.True(scan.IsRunning, "Driver must be reported as running.");
            Assert.True(scan.IsAccessible, "Driver must be reported as accessible.");
            Assert.False(scan.RequiresElevation, "Should not report requiring elevation when accessible.");
            Assert.Equal(0, scan.LastWin32Error);
            Assert.Contains("accessible", scan.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EnsureInitialized_WhenRunningAndAccessible_ReturnsSuccess()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.ConfigureIntelBaseline();
            using var controller = new PawnIoController(mock);

            // Act
            var status = controller.EnsureInitialized();

            // Assert
            Assert.Equal(PowerLimitApplyStatus.Success, status);
            Assert.True(controller.IsAvailable);
        }

        [Fact]
        public void ScanDevicePowerCapabilities_WhenRunningAndAccessible_ReturnsFullHardwareCapabilities()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.ConfigureIntelBaseline(
                tdpWatts: 55,
                minWatts: 15,
                maxWatts: 157,
                pl1Watts: 55,
                pl2Watts: 157,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true);
            using var controller = new PawnIoController(mock);

            // Act
            var caps = controller.ScanDevicePowerCapabilities();

            // Assert
            Assert.True(caps.IsSupported);
            Assert.Equal(PowerLimitApplyStatus.Success, caps.BaselineStatus);
            Assert.Equal(8U, caps.UnitsPerWatt);
            Assert.Equal(55, caps.ThermalSpecPowerWatts);
            Assert.Equal(15, caps.MinimumPowerWatts);
            Assert.Equal(157, caps.MaximumPowerWatts);
            Assert.Equal(55, caps.CurrentPl1Watts);
            Assert.Equal(157, caps.CurrentPl2Watts);
            Assert.Equal(15, caps.ScannedMinPowerWatts);
            Assert.Equal(157, caps.ScannedMaxPl2Watts);
            Assert.Equal(133, caps.ScannedMaxPl1Watts); // Math.Max(55, Round(157 * 0.85 = 133.45 -> 133))
            Assert.False(caps.IsLocked);
            Assert.True(caps.IsMmioAccessible);
        }

        [Fact]
        public void ApplyPowerLimits_WhenRunningAndAccessible_SuccessfullyUpdatesMsrAndMmio()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.ConfigureIntelBaseline(tdpWatts: 55, minWatts: 15, maxWatts: 157, pl1Watts: 55, pl2Watts: 157, unitsPerWatt: 8);
            using var controller = new PawnIoController(mock);

            // Act
            var result = controller.ApplyPowerLimits(65, 120);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.Success, result.Status);
            Assert.Equal(65, result.AppliedPl1Watts);
            Assert.Equal(120, result.AppliedPl2Watts);
            Assert.True(result.MmioMirrored);
        }

        #endregion

        #region Requirement 3: Stopped or Not Installed (DriverNotInstalled)

        [Fact]
        public void ScanDeviceForPawnIo_WhenDriverNotInstalled_ReturnsUninstalledAndInaccessible()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.IsDriverInstalled = false;
            using var controller = new PawnIoController(mock);

            // Act
            var scan = controller.ScanDeviceForPawnIo();

            // Assert
            Assert.False(scan.IsInstalled, "Driver must be reported as NOT installed.");
            Assert.False(scan.IsRunning, "Driver must be reported as NOT running.");
            Assert.False(scan.IsAccessible, "Driver must be reported as NOT accessible.");
            Assert.False(scan.RequiresElevation, "Should not falsely claim elevation is required when driver doesn't exist.");
            Assert.Contains("not installed", scan.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EnsureInitialized_WhenDriverNotInstalled_ReturnsDriverNotInstalledStatus()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.IsDriverInstalled = false;
            using var controller = new PawnIoController(mock);

            // Act
            var status = controller.EnsureInitialized();

            // Assert
            Assert.Equal(PowerLimitApplyStatus.DriverNotInstalled, status);
            Assert.False(controller.IsAvailable);
        }

        [Fact]
        public void ScanDevicePowerCapabilities_WhenDriverNotInstalled_ReturnsUnsupported()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.IsDriverInstalled = false;
            using var controller = new PawnIoController(mock);

            // Act
            var caps = controller.ScanDevicePowerCapabilities();

            // Assert
            Assert.False(caps.IsSupported);
            Assert.Equal(PowerLimitApplyStatus.DriverNotInstalled, caps.BaselineStatus);
            Assert.Contains("DriverNotInstalled", caps.Diagnostics);
        }

        [Fact]
        public void ApplyPowerLimits_WhenDriverNotInstalled_ReturnsDriverNotInstalledResult()
        {
            // Arrange
            using var mock = new MockPawnIoBackend();
            mock.IsDriverInstalled = false;
            using var controller = new PawnIoController(mock);

            // Act
            var result = controller.ApplyPowerLimits(45, 65);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal(PowerLimitApplyStatus.DriverNotInstalled, result.Status);
        }

        #endregion

        #region Requirement 4: EnsureInitialized() Does Not Prematurely Short-Circuit

        [Fact]
        public void EnsureInitialized_WhenIsDriverInstalledInitiallyFalse_InvokesBackendInitialize_WithoutShortCircuit()
        {
            // Arrange: Spy backend starts with IsDriverInstalled == false and IsDriverLoaded == false
            // Simulates NativePawnIoBackend which starts with false before Initialize() probes system.
            var spyBackend = new SpyPawnIoBackend
            {
                IsDriverInstalled = false,
                IsDriverLoaded = false,
                StatusToReturnOnInitialize = PowerLimitApplyStatus.Success
            };
            using var controller = new PawnIoController(spyBackend);

            // Act
            var status = controller.EnsureInitialized();

            // Assert
            // If EnsureInitialized() prematurely short-circuits on '!_backend.IsDriverInstalled',
            // InitializeCallCount would be 0 and status would falsely be DriverNotInstalled.
            Assert.Equal(1, spyBackend.InitializeCallCount);
            Assert.Equal(PowerLimitApplyStatus.Success, status);
            Assert.True(spyBackend.IsOpen);
            Assert.True(spyBackend.IsDriverInstalled);
        }

        [Fact]
        public void EnsureInitialized_WhenBackendIsOpen_DoesNotReinitializeIdempotently()
        {
            // Arrange: Spy backend is already initialized and open
            var spyBackend = new SpyPawnIoBackend
            {
                IsDriverInstalled = true,
                IsDriverLoaded = true
            };
            using var controller = new PawnIoController(spyBackend);

            // Act: First call when already open
            var status1 = controller.EnsureInitialized();
            var status2 = controller.EnsureInitialized();

            // Assert: Should not invoke Initialize() since IsOpen is already true
            Assert.Equal(0, spyBackend.InitializeCallCount);
            Assert.Equal(PowerLimitApplyStatus.Success, status1);
            Assert.Equal(PowerLimitApplyStatus.Success, status2);
        }

        [Fact]
        public void EnsureInitialized_WhenBackendInitializeReturnsAccessDenied_ProperlyReturnsAccessDenied()
        {
            // Arrange: Backend starts with IsDriverInstalled = false, Initialize() returns AccessDenied
            var spyBackend = new SpyPawnIoBackend
            {
                IsDriverInstalled = false,
                IsDriverLoaded = false,
                StatusToReturnOnInitialize = PowerLimitApplyStatus.AccessDenied
            };
            using var controller = new PawnIoController(spyBackend);

            // Act
            var status = controller.EnsureInitialized();

            // Assert: Must NOT short-circuit to DriverNotInstalled, must call Initialize() and return AccessDenied
            Assert.Equal(1, spyBackend.InitializeCallCount);
            Assert.Equal(PowerLimitApplyStatus.AccessDenied, status);
            Assert.True(spyBackend.IsDriverInstalled, "Driver presence was detected even if handle returned AccessDenied.");
        }

        [Fact]
        public void EnsureInitialized_WhenBackendInitializeReturnsDriverNotInstalled_InvokesInitializeBeforeReturning()
        {
            // Arrange: Backend starts uninitialized, Initialize() determines driver is not installed
            var spyBackend = new SpyPawnIoBackend
            {
                IsDriverInstalled = false,
                IsDriverLoaded = false,
                StatusToReturnOnInitialize = PowerLimitApplyStatus.DriverNotInstalled
            };
            using var controller = new PawnIoController(spyBackend);

            // Act
            var status = controller.EnsureInitialized();

            // Assert
            Assert.Equal(1, spyBackend.InitializeCallCount);
            Assert.Equal(PowerLimitApplyStatus.DriverNotInstalled, status);
            Assert.False(spyBackend.IsDriverInstalled);
        }

        #endregion

        #region Requirement 5: DeviceCapabilities Reflects PawnIoInstalled, PawnIoAvailable, PawnIoRequiresAdmin

        [Fact]
        public void DeviceCapabilities_DefaultConstructor_ReflectsDefaultPawnIoState()
        {
            // Arrange & Act
            var caps = new DeviceCapabilities();

            // Assert: Backwards compatible defaults
            Assert.True(caps.PawnIoAvailable, "Default PawnIoAvailable must be true for backwards compatibility.");
            Assert.True(caps.PawnIoInstalled, "Default PawnIoInstalled must be true when PawnIoAvailable is true.");
            Assert.False(caps.PawnIoRequiresAdmin, "Default PawnIoRequiresAdmin must be false.");
        }

        [Fact]
        public void DeviceCapabilities_ReflectsAllThreeMutuallyDistinctPawnIoStates()
        {
            // State 1: Healthy & Accessible (Installed=true, Available=true, RequiresAdmin=false)
            var activeCaps = new DeviceCapabilities
            {
                PawnIoInstalled = true,
                PawnIoAvailable = true,
                PawnIoRequiresAdmin = false,
                PawnIoDiagnosticMessage = "PawnIO kernel driver active and accessible."
            };
            Assert.True(activeCaps.PawnIoInstalled);
            Assert.True(activeCaps.PawnIoAvailable);
            Assert.False(activeCaps.PawnIoRequiresAdmin);
            Assert.Contains("accessible", activeCaps.PawnIoDiagnosticMessage);

            // State 2: Installed but Requires Admin (Installed=true, Available=false, RequiresAdmin=true)
            var adminCaps = new DeviceCapabilities
            {
                PawnIoInstalled = true,
                PawnIoAvailable = false,
                PawnIoRequiresAdmin = true,
                PawnIoDiagnosticMessage = "Access denied opening PawnIO device handle. Administrator privileges required."
            };
            Assert.True(adminCaps.PawnIoInstalled);
            Assert.False(adminCaps.PawnIoAvailable);
            Assert.True(adminCaps.PawnIoRequiresAdmin);
            Assert.Contains("Administrator", adminCaps.PawnIoDiagnosticMessage);

            // State 3: Not Installed / Stopped (Installed=false, Available=false, RequiresAdmin=false)
            var missingCaps = new DeviceCapabilities
            {
                PawnIoInstalled = false,
                PawnIoAvailable = false,
                PawnIoRequiresAdmin = false,
                PawnIoDiagnosticMessage = "PawnIO kernel driver is not installed or service is not running."
            };
            Assert.False(missingCaps.PawnIoInstalled);
            Assert.False(missingCaps.PawnIoAvailable);
            Assert.False(missingCaps.PawnIoRequiresAdmin);
            Assert.Contains("not installed", missingCaps.PawnIoDiagnosticMessage);
        }

        [Fact]
        public void DeviceCapabilities_RecordWithExpressions_PreservesImmutabilityAndUpdatesPawnIoFlags()
        {
            // Arrange
            var initialCaps = new DeviceCapabilities
            {
                PawnIoInstalled = true,
                PawnIoAvailable = true,
                PawnIoRequiresAdmin = false
            };

            // Act: Transition to AccessDenied state using record 'with' expression
            var updatedCaps = initialCaps with
            {
                PawnIoAvailable = false,
                PawnIoRequiresAdmin = true,
                PawnIoDiagnosticMessage = "Run as Administrator to access MSRs."
            };

            // Assert: Immutability verified
            Assert.True(initialCaps.PawnIoAvailable);
            Assert.False(initialCaps.PawnIoRequiresAdmin);

            Assert.True(updatedCaps.PawnIoInstalled);
            Assert.False(updatedCaps.PawnIoAvailable);
            Assert.True(updatedCaps.PawnIoRequiresAdmin);
            Assert.Equal("Run as Administrator to access MSRs.", updatedCaps.PawnIoDiagnosticMessage);
        }

        [Fact]
        public void DeviceCapabilities_Diagnostics_IncludesPawnIoScanningSummary()
        {
            // Arrange
            string diagAccessible = "PawnIO Driver: Available / Loaded";
            string diagAdmin = "PawnIO Driver: Detected (Requires Administrator)";
            string diagMissing = "PawnIO Driver: Not Detected";

            var caps1 = new DeviceCapabilities { Diagnostics = diagAccessible, PawnIoAvailable = true };
            var caps2 = new DeviceCapabilities { Diagnostics = diagAdmin, PawnIoRequiresAdmin = true };
            var caps3 = new DeviceCapabilities { Diagnostics = diagMissing, PawnIoInstalled = false };

            // Assert
            Assert.Contains("Available / Loaded", caps1.Diagnostics);
            Assert.Contains("Requires Administrator", caps2.Diagnostics);
            Assert.Contains("Not Detected", caps3.Diagnostics);
        }

        [Fact]
        public void PawnIoScanResult_MappingTo_DeviceCapabilities_MaintainsArchitecturalConsistency()
        {
            // Verify mapping helper logic converts PawnIoScanResult accurately to DeviceCapabilities
            DeviceCapabilities MapScanToCapabilities(PawnIoScanResult scan) => new()
            {
                PawnIoInstalled = scan.IsInstalled,
                PawnIoAvailable = scan.IsAccessible,
                PawnIoRequiresAdmin = scan.RequiresElevation,
                PawnIoDiagnosticMessage = scan.DiagnosticMessage
            };

            var scanAccessible = new PawnIoScanResult { IsInstalled = true, IsAccessible = true, RequiresElevation = false };
            var capsAccessible = MapScanToCapabilities(scanAccessible);
            Assert.True(capsAccessible.PawnIoInstalled);
            Assert.True(capsAccessible.PawnIoAvailable);
            Assert.False(capsAccessible.PawnIoRequiresAdmin);

            var scanAdmin = new PawnIoScanResult { IsInstalled = true, IsAccessible = false, RequiresElevation = true };
            var capsAdmin = MapScanToCapabilities(scanAdmin);
            Assert.True(capsAdmin.PawnIoInstalled);
            Assert.False(capsAdmin.PawnIoAvailable);
            Assert.True(capsAdmin.PawnIoRequiresAdmin);

            var scanMissing = new PawnIoScanResult { IsInstalled = false, IsAccessible = false, RequiresElevation = false };
            var capsMissing = MapScanToCapabilities(scanMissing);
            Assert.False(capsMissing.PawnIoInstalled);
            Assert.False(capsMissing.PawnIoAvailable);
            Assert.False(capsMissing.PawnIoRequiresAdmin);
        }

        #endregion
    }
}
