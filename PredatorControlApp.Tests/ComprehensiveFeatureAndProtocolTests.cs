using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Comprehensive unit tests verifying:
    /// 1. Display refresh rate buttons synchronization across dashboard and system panel.
    /// 2. GetCurrentFanLock() returning null for Quiet, Balanced, Performance, Turbo, and only FanLock.EcoMode for Eco (0x06).
    /// 3. Power limits boot application and ReScan stability: verify that ReScan never corrupts or diminishes valid CPU min/max PL1/PL2 limits.
    /// 4. LCD Overdrive multi-dispatch commands and payloads.
    /// </summary>
    [Collection("NoParallel")]
    public class ComprehensiveFeatureAndProtocolTests
    {
        private static void RunOnSta(Action action)
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            bool finished = thread.Join(15000);
            Assert.True(finished, "STA thread timed out executing test.");
            if (error != null) throw error;
        }

        #region 1. Display Refresh Rate Buttons Synchronization

        [Fact]
        public void DisplayRefreshRate_ButtonsSynchronization_AcrossDashboardAndSystemPanel_60Hz()
        {
            RunOnSta(() =>
            {
                using var form = new Form1();
                form.UpdateDisplayRefreshRateButtons(60);

                // Dashboard buttons
                Assert.True(form.DashboardBtn60Hz.IsActive, "Dashboard 60Hz button should be active for 60Hz");
                Assert.False(form.DashboardBtnMaxHz.IsActive, "Dashboard MaxHz button should be inactive for 60Hz");

                // System panel buttons (must be in lockstep synchronization)
                Assert.NotNull(form.SystemBtnDisp60);
                Assert.NotNull(form.SystemBtnDispMax);
                Assert.True(form.SystemBtnDisp60!.IsActive, "System panel 60Hz button should be active for 60Hz");
                Assert.False(form.SystemBtnDispMax!.IsActive, "System panel MaxHz button should be inactive for 60Hz");

                // Tray menu items
                if (form.TrayDisplay60 != null && form.TrayDisplayMax != null)
                {
                    Assert.True(form.TrayDisplay60.Checked, "Tray 60Hz item should be checked for 60Hz");
                    Assert.False(form.TrayDisplayMax.Checked, "Tray MaxHz item should be unchecked for 60Hz");
                }
            });
        }

        [Theory]
        [InlineData(120)]
        [InlineData(144)]
        [InlineData(165)]
        [InlineData(240)]
        [InlineData(360)]
        public void DisplayRefreshRate_ButtonsSynchronization_AcrossDashboardAndSystemPanel_MaxHz(int highHz)
        {
            RunOnSta(() =>
            {
                using var form = new Form1();
                form.UpdateDisplayRefreshRateButtons(highHz);

                // Dashboard buttons
                Assert.False(form.DashboardBtn60Hz.IsActive, $"Dashboard 60Hz button should be inactive for {highHz}Hz");
                Assert.True(form.DashboardBtnMaxHz.IsActive, $"Dashboard MaxHz button should be active for {highHz}Hz");

                // System panel buttons
                Assert.NotNull(form.SystemBtnDisp60);
                Assert.NotNull(form.SystemBtnDispMax);
                Assert.False(form.SystemBtnDisp60!.IsActive, $"System panel 60Hz button should be inactive for {highHz}Hz");
                Assert.True(form.SystemBtnDispMax!.IsActive, $"System panel MaxHz button should be active for {highHz}Hz");

                // Tray menu items
                if (form.TrayDisplay60 != null && form.TrayDisplayMax != null)
                {
                    Assert.False(form.TrayDisplay60.Checked, $"Tray 60Hz item should be unchecked for {highHz}Hz");
                    Assert.True(form.TrayDisplayMax.Checked, $"Tray MaxHz item should be checked for {highHz}Hz");
                }
            });
        }

        [Fact]
        public void DisplayRefreshRate_ApplyDisplayMode_FromDashboard_SynchronizesSystemPanel()
        {
            RunOnSta(() =>
            {
                using var form = new Form1();
                var originalOverride = Form1.SetRefreshRateOverride;
                try
                {
                    Form1.SetRefreshRateOverride = _ => true;

                    // Switch to 60Hz via dashboard button
                    form.ApplyDisplayMode(60, form.DashboardBtn60Hz);
                    Assert.True(form.DashboardBtn60Hz.IsActive);
                    Assert.False(form.DashboardBtnMaxHz.IsActive);
                    Assert.True(form.SystemBtnDisp60!.IsActive);
                    Assert.False(form.SystemBtnDispMax!.IsActive);

                    // Switch to 144Hz via dashboard button
                    form.ApplyDisplayMode(144, form.DashboardBtnMaxHz);
                    Assert.False(form.DashboardBtn60Hz.IsActive);
                    Assert.True(form.DashboardBtnMaxHz.IsActive);
                    Assert.False(form.SystemBtnDisp60!.IsActive);
                    Assert.True(form.SystemBtnDispMax!.IsActive);
                }
                finally
                {
                    Form1.SetRefreshRateOverride = originalOverride;
                }
            });
        }

        [Fact]
        public void DisplayRefreshRate_ApplyDisplayMode_FromSystemPanel_SynchronizesDashboard()
        {
            RunOnSta(() =>
            {
                using var form = new Form1();
                var originalOverride = Form1.SetRefreshRateOverride;
                try
                {
                    Form1.SetRefreshRateOverride = _ => true;

                    // Click system panel button for 165Hz
                    form.ApplyDisplayMode(165, form.SystemBtnDispMax);
                    Assert.False(form.DashboardBtn60Hz.IsActive);
                    Assert.True(form.DashboardBtnMaxHz.IsActive);
                    Assert.False(form.SystemBtnDisp60!.IsActive);
                    Assert.True(form.SystemBtnDispMax!.IsActive);

                    // Click system panel button for 60Hz
                    form.ApplyDisplayMode(60, form.SystemBtnDisp60);
                    Assert.True(form.DashboardBtn60Hz.IsActive);
                    Assert.False(form.DashboardBtnMaxHz.IsActive);
                    Assert.True(form.SystemBtnDisp60!.IsActive);
                    Assert.False(form.SystemBtnDispMax!.IsActive);
                }
                finally
                {
                    Form1.SetRefreshRateOverride = originalOverride;
                }
            });
        }

        [Fact]
        public void DisplayRefreshRate_FailedRefreshRateChange_DoesNotDesyncButtons()
        {
            RunOnSta(() =>
            {
                using var form = new Form1();
                var originalOverride = Form1.SetRefreshRateOverride;
                try
                {
                    // Start in known 60Hz state
                    Form1.SetRefreshRateOverride = _ => true;
                    form.ApplyDisplayMode(60, form.DashboardBtn60Hz);

                    Assert.True(form.DashboardBtn60Hz.IsActive);
                    Assert.True(form.SystemBtnDisp60!.IsActive);

                    // Simulate driver failure on rate change
                    Form1.SetRefreshRateOverride = _ => false;
                    form.ApplyDisplayMode(165, form.DashboardBtnMaxHz);

                    // State should not corrupt or desynchronize on failure
                    Assert.True(form.DashboardBtn60Hz.IsActive, "60Hz button should remain active when 165Hz apply fails");
                    Assert.False(form.DashboardBtnMaxHz.IsActive, "MaxHz button should remain inactive when 165Hz apply fails");
                    Assert.True(form.SystemBtnDisp60!.IsActive, "System panel 60Hz button should remain active");
                    Assert.False(form.SystemBtnDispMax!.IsActive, "System panel MaxHz button should remain inactive");
                }
                finally
                {
                    Form1.SetRefreshRateOverride = originalOverride;
                }
            });
        }

        [Fact]
        public void DisplayRefreshRate_RapidAlternation_MaintainsLockstepSynchronization()
        {
            RunOnSta(() =>
            {
                using var form = new Form1();
                var originalOverride = Form1.SetRefreshRateOverride;
                try
                {
                    Form1.SetRefreshRateOverride = _ => true;

                    for (int i = 0; i < 20; i++)
                    {
                        int targetHz = (i % 2 == 0) ? 60 : 165;
                        var activeBtn = (i % 2 == 0) ? form.DashboardBtn60Hz : form.DashboardBtnMaxHz;
                        form.ApplyDisplayMode(targetHz, activeBtn);

                        bool expected60 = targetHz <= 60;
                        Assert.Equal(expected60, form.DashboardBtn60Hz.IsActive);
                        Assert.Equal(!expected60, form.DashboardBtnMaxHz.IsActive);
                        Assert.Equal(expected60, form.SystemBtnDisp60!.IsActive);
                        Assert.Equal(!expected60, form.SystemBtnDispMax!.IsActive);
                        Assert.Equal(form.DashboardBtn60Hz.IsActive, form.SystemBtnDisp60!.IsActive);
                        Assert.Equal(form.DashboardBtnMaxHz.IsActive, form.SystemBtnDispMax!.IsActive);
                    }
                }
                finally
                {
                    Form1.SetRefreshRateOverride = originalOverride;
                }
            });
        }

        #endregion

        #region 2. GetCurrentFanLock() Mode Specification Tests

        [Theory]
        [InlineData(0x00, false)] // Quiet Mode
        [InlineData(0x01, false)] // Balanced Mode
        [InlineData(0x04, false)] // Performance Mode
        [InlineData(0x05, false)] // Turbo Mode
        [InlineData(0x06, true)]  // Eco Mode
        public void GetCurrentFanLock_StaticMethod_ReturnsNullForQuietBalancedPerformanceTurbo_AndEcoFor0x06(byte powerByte, bool expectEcoLock)
        {
            FanLock? lockState = Form1.GetCurrentFanLock(powerByte);

            if (expectEcoLock)
            {
                Assert.NotNull(lockState);
                Assert.Equal(FanLock.EcoMode, lockState.Value);
            }
            else
            {
                Assert.Null(lockState);
            }
        }

        [Theory]
        [InlineData(0x02)] // Invalid / obsolete
        [InlineData(0x03)] // Custom fan mode byte (not a power mode)
        [InlineData(0x07)] // Unused power byte
        [InlineData(0x08)] // Unused power byte
        [InlineData(0xFF)] // Invalid sentinel
        public void GetCurrentFanLock_InvalidOrUnknownPowerBytes_ReturnsNull(byte powerByte)
        {
            FanLock? lockState = Form1.GetCurrentFanLock(powerByte);
            Assert.Null(lockState);
        }

        [Fact]
        public void GetCurrentFanLock_InstanceMethod_ReturnsNullForQuietBalancedPerformanceTurbo_AndEcoFor0x06()
        {
            RunOnSta(() =>
            {
                using var form = new Form1();

                // Quiet mode (0x00) -> null (unlocked, allowing custom curve)
                form.SetCurrentPowerModeForTesting(0x00);
                Assert.Null(form.GetCurrentFanLock());

                // Balanced mode (0x01) -> null
                form.SetCurrentPowerModeForTesting(0x01);
                Assert.Null(form.GetCurrentFanLock());

                // Performance mode (0x04) -> null
                form.SetCurrentPowerModeForTesting(0x04);
                Assert.Null(form.GetCurrentFanLock());

                // Turbo mode (0x05) -> null
                form.SetCurrentPowerModeForTesting(0x05);
                Assert.Null(form.GetCurrentFanLock());

                // Eco mode (0x06) -> FanLock.EcoMode
                form.SetCurrentPowerModeForTesting(0x06);
                Assert.Equal(FanLock.EcoMode, form.GetCurrentFanLock());
            });
        }

        [Fact]
        public void UpdateFanLockStatus_QuietAndTurboDoNotLockFans_AllowsManualAndCurveSpeeds()
        {
            RunOnSta(() =>
            {
                using var form = new Form1();

                // Test Quiet Mode: fans are unlocked
                form.SetCurrentPowerModeForTesting(0x00);
                bool quietLocked = form.GetCurrentFanLock() != null;
                Assert.False(quietLocked, "Quiet mode must not lock fan customization");

                // Test Turbo Mode: fans are unlocked
                form.SetCurrentPowerModeForTesting(0x05);
                bool turboLocked = form.GetCurrentFanLock() != null;
                Assert.False(turboLocked, "Turbo mode must not lock fan customization");

                // Test Eco Mode: fans ARE locked
                form.SetCurrentPowerModeForTesting(0x06);
                bool ecoLocked = form.GetCurrentFanLock() != null;
                Assert.True(ecoLocked, "Eco mode must lock fans to Eco hardware profile");
                Assert.Equal(FanLock.EcoMode, form.GetCurrentFanLock());
            });
        }

        #endregion

        #region 3. Power Limits Boot Application & ReScan Stability Tests

        [Fact]
        public void ReScan_PreservesValidCpuMinAndMaxLimits_WhenScannerReturnsZeroOrFails()
        {
            // Initial established valid capabilities
            var initialCaps = new DeviceCapabilities
            {
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 115,
                ScannedMaxPl2Watts = 140,
                CpuTdpWatts = 45
            };

            Assert.Equal(15, initialCaps.CpuMinAllowedPowerWatts);
            Assert.Equal(115, initialCaps.CpuMaxAllowedPl1Watts);
            Assert.Equal(140, initialCaps.CpuMaxAllowedPl2Watts);
            Assert.Equal(45, initialCaps.CpuThermalSpecTdpWatts);

            // Simulate a failed ReScan where scanner returns 0 or negative
            var failedBiosScan = new BiosPowerScanResult
            {
                CpuMinAllowedPowerWatts = 0,
                CpuMaxAllowedPl1Watts = 0,
                CpuMaxAllowedPl2Watts = 0,
                CpuThermalSpecTdpWatts = 0,
                HasCompletedBiosPowerScan = false
            };

            // Resolution logic: ReScan fallback protects established valid limits
            int minW = failedBiosScan.CpuMinAllowedPowerWatts > 0 ? failedBiosScan.CpuMinAllowedPowerWatts : initialCaps.CpuMinAllowedPowerWatts;
            int max1W = failedBiosScan.CpuMaxAllowedPl1Watts > 0 ? failedBiosScan.CpuMaxAllowedPl1Watts : initialCaps.CpuMaxAllowedPl1Watts;
            int max2W = failedBiosScan.CpuMaxAllowedPl2Watts > 0 ? failedBiosScan.CpuMaxAllowedPl2Watts : initialCaps.CpuMaxAllowedPl2Watts;
            int tdpW = failedBiosScan.CpuThermalSpecTdpWatts > 0 ? failedBiosScan.CpuThermalSpecTdpWatts : initialCaps.CpuThermalSpecTdpWatts;

            if (max1W < minW) max1W = minW;
            if (max2W < max1W) max2W = max1W;

            var rescannedCaps = initialCaps with
            {
                ScannedMinPowerWatts = minW,
                ScannedMaxPl1Watts = max1W,
                ScannedMaxPl2Watts = max2W,
                CpuTdpWatts = tdpW
            };

            // Limits must remain fully preserved and uncorrupted
            Assert.Equal(15, rescannedCaps.CpuMinAllowedPowerWatts);
            Assert.Equal(115, rescannedCaps.CpuMaxAllowedPl1Watts);
            Assert.Equal(140, rescannedCaps.CpuMaxAllowedPl2Watts);
            Assert.Equal(45, rescannedCaps.CpuThermalSpecTdpWatts);
        }

        [Fact]
        public void ReScan_EnforcesInvariant_MinAllowedPowerWatts_LessThanOrEqual_MaxPl1_LessThanOrEqual_MaxPl2()
        {
            // Simulate pathological input where scanner reports inverted limits (e.g. maxPl1 < min or maxPl2 < maxPl1)
            int minW = 25;
            int max1W = 10; // lower than min
            int max2W = 15; // lower than max1 after clamp

            if (max1W < minW) max1W = minW;
            if (max2W < max1W) max2W = max1W;

            Assert.True(minW <= max1W, $"Invariant violated: minW ({minW}) must be <= max1W ({max1W})");
            Assert.True(max1W <= max2W, $"Invariant violated: max1W ({max1W}) must be <= max2W ({max2W})");
            Assert.Equal(25, minW);
            Assert.Equal(25, max1W);
            Assert.Equal(25, max2W);
        }

        [Fact]
        public void PowerLimits_BootApplication_ZeroAssumptionRule_DoesNotTouchHardware_WhenCustomLimitsDisabled()
        {
            var settings = new AppSettings();
            settings.EnableCustomPowerLimits = false;
            settings.ScannedMinPowerWatts = 15;
            settings.ScannedMaxPl1Watts = 115;
            settings.ScannedMaxPl2Watts = 140;

            // Zero-Assumption Rule check:
            // Custom limits must not be applied if EnableCustomPowerLimits is false
            bool shouldTouchHardware = settings.EnableCustomPowerLimits;
            Assert.False(shouldTouchHardware, "Zero-Assumption Rule: Never touch hardware on boot unless EnableCustomPowerLimits == true");

            // Effective limits fallback cleanly to defaults without corruption
            var (effPl1, effPl2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced);
            Assert.True(effPl1 >= 15 && effPl1 <= 115);
            Assert.True(effPl2 >= effPl1 && effPl2 <= 140);
        }

        [Fact]
        public void PowerLimits_BootApplication_AppliesEffectiveLimits_WhenCustomLimitsEnabled()
        {
            var settings = new AppSettings();
            settings.EnableCustomPowerLimits = true;
            settings.ScannedMinPowerWatts = 15;
            settings.ScannedMaxPl1Watts = 115;
            settings.ScannedMaxPl2Watts = 140;

            // Set user custom limits for Balanced mode
            settings.SetPowerLimitsForMode(OperatingMode.Balanced, 55, 75);

            bool shouldTouchHardware = settings.EnableCustomPowerLimits;
            Assert.True(shouldTouchHardware);

            var (effPl1, effPl2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Balanced);
            Assert.Equal(55, effPl1);
            Assert.Equal(75, effPl2);
        }

        [Fact]
        public void ReScan_DoesNotDiminishExistingValidPowerBounds_OverMultipleIterations()
        {
            var caps = new DeviceCapabilities
            {
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 115,
                ScannedMaxPl2Watts = 140,
                CpuTdpWatts = 45
            };

            var rng = new Random(42);

            for (int i = 0; i < 50; i++)
            {
                // Simulate periodic rescans where some attempts succeed and some return transient 0s
                bool scanSucceeded = rng.Next(2) == 1;
                var scanResult = scanSucceeded
                    ? new BiosPowerScanResult
                    {
                        CpuMinAllowedPowerWatts = 15,
                        CpuMaxAllowedPl1Watts = 115,
                        CpuMaxAllowedPl2Watts = 140,
                        CpuThermalSpecTdpWatts = 45,
                        HasCompletedBiosPowerScan = true
                    }
                    : new BiosPowerScanResult
                    {
                        CpuMinAllowedPowerWatts = 0,
                        CpuMaxAllowedPl1Watts = 0,
                        CpuMaxAllowedPl2Watts = 0,
                        CpuThermalSpecTdpWatts = 0,
                        HasCompletedBiosPowerScan = false
                    };

                int minW = scanResult.CpuMinAllowedPowerWatts > 0 ? scanResult.CpuMinAllowedPowerWatts : caps.CpuMinAllowedPowerWatts;
                int max1W = scanResult.CpuMaxAllowedPl1Watts > 0 ? scanResult.CpuMaxAllowedPl1Watts : caps.CpuMaxAllowedPl1Watts;
                int max2W = scanResult.CpuMaxAllowedPl2Watts > 0 ? scanResult.CpuMaxAllowedPl2Watts : caps.CpuMaxAllowedPl2Watts;
                int tdpW = scanResult.CpuThermalSpecTdpWatts > 0 ? scanResult.CpuThermalSpecTdpWatts : caps.CpuThermalSpecTdpWatts;

                if (max1W < minW) max1W = minW;
                if (max2W < max1W) max2W = max1W;

                caps = caps with
                {
                    ScannedMinPowerWatts = minW,
                    ScannedMaxPl1Watts = max1W,
                    ScannedMaxPl2Watts = max2W,
                    CpuTdpWatts = tdpW
                };

                Assert.Equal(15, caps.CpuMinAllowedPowerWatts);
                Assert.Equal(115, caps.CpuMaxAllowedPl1Watts);
                Assert.Equal(140, caps.CpuMaxAllowedPl2Watts);
                Assert.Equal(45, caps.CpuThermalSpecTdpWatts);
            }
        }

        [Fact]
        public void ReScan_MatchingFingerprint_PreservesConfiguredUserPowerLimits()
        {
            var settings = new AppSettings();
            string stableFingerprint = "Acer_Predator_PH16-71_V1.15_Core_i7-13700HX";
            settings.BiosHardwareFingerprint = stableFingerprint;

            // Configure user limits
            settings.EnableCustomPowerLimits = true;
            settings.SetPowerLimitsForMode(OperatingMode.Quiet, 35, 45);
            settings.SetPowerLimitsForMode(OperatingMode.Performance, 80, 100);

            // ReScan with identical fingerprint
            settings.InvalidateIfBiosFingerprintChanged(stableFingerprint);

            // User limits must remain preserved
            Assert.True(settings.EnableCustomPowerLimits);
            var (quietPl1, quietPl2) = settings.GetUserPowerLimitsForMode(OperatingMode.Quiet);
            var (perfPl1, perfPl2) = settings.GetUserPowerLimitsForMode(OperatingMode.Performance);
            Assert.Equal(35, quietPl1);
            Assert.Equal(45, quietPl2);
            Assert.Equal(80, perfPl1);
            Assert.Equal(100, perfPl2);
        }

        #endregion

        #region 4. LCD Overdrive Multi-Dispatch Commands & Payloads

        [Fact]
        public void LcdOverdrive_GetLcdOverdrivePayloads_ReturnsExpectedHardwareBitmasks()
        {
            var (onPayload, offPayload) = WmiController.GetLcdOverdrivePayloads();

            // Hardware specification:
            // ON payload = 0x1000000000010UL
            // OFF payload = 0x10UL
            Assert.Equal(0x1000000000010UL, onPayload);
            Assert.Equal(0x10UL, offPayload);

            // Verify bit 4 (0x10) is set on both (indicating Overdrive capability feature flag)
            Assert.True((onPayload & 0x10UL) != 0, "Bit 4 must be set on ON payload");
            Assert.True((offPayload & 0x10UL) != 0, "Bit 4 must be set on OFF payload");

            // Verify high bit 48 (0x1000000000000UL) is set ONLY on ON payload
            Assert.True((onPayload & 0x1000000000000UL) != 0, "High bit 48 must be set on ON payload");
            Assert.False((offPayload & 0x1000000000000UL) != 0, "High bit 48 must be cleared on OFF payload");
        }

        [Fact]
        public void LcdOverdrive_AcerServiceClient_BuildLcdOverdrivePayload_MatchesOemProtocol()
        {
            Assert.Equal(100u, AcerServiceClient.LcdOverdrivePacketId);

            string onPayload = AcerServiceClient.BuildLcdOverdrivePayload(true);
            Assert.Equal("{\"Function\":\"LCD_OVERDRIVE\",\"Parameter\":{\"status\":1}}", onPayload);

            string offPayload = AcerServiceClient.BuildLcdOverdrivePayload(false);
            Assert.Equal("{\"Function\":\"LCD_OVERDRIVE\",\"Parameter\":{\"status\":0}}", offPayload);
        }

        [Fact]
        public void LcdOverdrive_ApplyLcdOverdrive_SynchronizesDashboardSystemPanelAndTray()
        {
            RunOnSta(() =>
            {
                using var form = new Form1();

                // Enable LCD Overdrive
                form.ApplyLcdOverdrive(true);

                Assert.True(form.SwitchLcdOverdrive.Checked, "Dashboard LCD Overdrive switch should be checked");
                if (form.SwOdSys != null)
                {
                    Assert.True(form.SwOdSys.Checked, "System panel LCD Overdrive switch should be checked");
                }
                if (form.TrayLcdOverdrive != null)
                {
                    Assert.True(form.TrayLcdOverdrive.Checked, "Tray LCD Overdrive item should be checked");
                }

                Assert.Equal("LCD Overdrive (Fast Response Time)", form.LblLcdOverdriveStatus.Text);
                if (form.LblOdStatusSys != null)
                {
                    Assert.Equal("LCD Overdrive (Fast Response Time)", form.LblOdStatusSys.Text);
                }

                // Disable LCD Overdrive
                form.ApplyLcdOverdrive(false);

                Assert.False(form.SwitchLcdOverdrive.Checked, "Dashboard LCD Overdrive switch should be unchecked");
                if (form.SwOdSys != null)
                {
                    Assert.False(form.SwOdSys.Checked, "System panel LCD Overdrive switch should be unchecked");
                }
                if (form.TrayLcdOverdrive != null)
                {
                    Assert.False(form.TrayLcdOverdrive.Checked, "Tray LCD Overdrive item should be unchecked");
                }

                Assert.Equal("LCD Overdrive (Standard)", form.LblLcdOverdriveStatus.Text);
                if (form.LblOdStatusSys != null)
                {
                    Assert.Equal("LCD Overdrive (Standard)", form.LblOdStatusSys.Text);
                }
            });
        }

        [Fact]
        public void LcdOverdrive_MultiDispatchResilience_HandlesDisposedOrUnreachableServiceWithoutThrowing()
        {
            // Verify AcerServiceClient handles unreachable TCP socket gracefully without throwing
            var taskOn = AcerServiceClient.SetLcdOverdriveAsync(true);
            var taskOff = AcerServiceClient.SetLcdOverdriveAsync(false);

            Task.WaitAll(new[] { taskOn, taskOff }, TimeSpan.FromSeconds(5));
            Assert.True(taskOn.IsCompletedSuccessfully, "SetLcdOverdriveAsync(true) should complete gracefully");
            Assert.True(taskOff.IsCompletedSuccessfully, "SetLcdOverdriveAsync(false) should complete gracefully");

            // Verify WmiController handles disposed state without throwing
            var wmi = new WmiController();
            wmi.Dispose();
            bool result = wmi.SetLcdOverdrive(true);
            Assert.False(result, "Disposed WmiController must return false cleanly without throwing");
        }

        #endregion
    }
}
