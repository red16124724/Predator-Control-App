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
    [Collection("NoParallel")]
    public class Step4AndStep5PowerLimitTests
    {
        private static void RunInStaThread(Action action)
        {
            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(15000);

            if (caught != null)
            {
                throw new AggregateException("STA thread test failure", caught);
            }
        }

        #region Step 4: Arbitrate ThermalSafetyService / AntiThrottleSafety vs. CpuPowerLimitController

        [Fact]
        public void Step4_AntiThrottleSafety_CalculatePowerCapWatts_BehavesCorrectlyAcrossThermalZones()
        {
            var limits = new ThermalLimits(85, 95, 100);
            var safety = new AntiThrottleSafety(limits, enabled: true);

            // Safe temperature (below ThrottleThreshold) -> null cap (no reduction)
            Assert.Null(safety.CalculatePowerCapWatts(70, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45));
            Assert.Null(safety.CalculatePowerCapWatts(85, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45));

            // Between Throttle and Ceiling -> Linear interpolation cap
            int? midCap = safety.CalculatePowerCapWatts(90, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45);
            Assert.NotNull(midCap);
            Assert.InRange(midCap.Value, 45, 65);

            // Ceiling threshold -> Cap at base TDP
            int? ceilingCap = safety.CalculatePowerCapWatts(95, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45);
            Assert.NotNull(ceilingCap);
            Assert.Equal(45, ceilingCap.Value);

            // Critical threshold -> Cap at minPowerWatts
            int? criticalCap = safety.CalculatePowerCapWatts(102, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45);
            Assert.NotNull(criticalCap);
            Assert.Equal(15, criticalCap.Value);

            // Disabled -> always null
            safety.Enabled = false;
            Assert.Null(safety.CalculatePowerCapWatts(105, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45));
        }

        [Fact]
        public void Step4_CpuPowerLimitController_EnforcesThermalCapInHardware_WithoutOverwritingAppSettings()
        {
            using var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(
                tdpWatts: 45,
                minWatts: 15,
                maxWatts: 140,
                pl1Watts: 45,
                pl2Watts: 75,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true
            );

            var settings = new AppSettings
            {
                EnableCustomPowerLimits = true,
                PowerMode = (byte)OperatingMode.Balanced,
                ScannedMinPowerWatts = 15,
                ScannedMaxPl1Watts = 115,
                ScannedMaxPl2Watts = 140,
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140,
                CpuThermalSpecTdpWatts = 45,
                BiosHardwareFingerprint = "TestFingerprint",
                HasCompletedBiosPowerScan = true
            };

            // Configure custom limits for Balanced mode: 65W PL1, 95W PL2
            settings.SetPowerLimitsForMode(OperatingMode.Balanced, 65, 95);
            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Balanced, 45, 75, isVerifiedBiosScan: true);

            using var controller = new CpuPowerLimitController(settings, backend);
            Console.WriteLine("TEST: mode=" + controller.GetCurrentOperatingMode() + " userLimits=" + settings.GetUserPowerLimitsForMode(OperatingMode.Balanced) + " eff=" + settings.GetEffectiveLimitsForMode(OperatingMode.Balanced));
controller.InitializeHardwareFingerprintAndBaseline("TestFingerprint");
Console.WriteLine("TEST AFTER INIT: mode=" + controller.GetCurrentOperatingMode() + " hwPl1 before read");
            controller.ApplyPowerLimitsForMode(OperatingMode.Balanced);

            // Verify initial hardware limits match user configured settings
            controller.PawnIo.ReadPowerUnits(out uint units, out _);
            controller.PawnIo.ReadPowerLimits(units, out int hwPl1, out int hwPl2, out _, out _);
            Assert.Equal(65, hwPl1);
            Assert.Equal(95, hwPl2);

            // Apply transient runtime thermal safety cap of 35W
            controller.SetThermalSafetyCap(35);
            Assert.Equal(35, controller.ThermalSafetyCapWatts);

            // Verify hardware limits are capped to 35W
            controller.PawnIo.ReadPowerLimits(units, out hwPl1, out hwPl2, out _, out _);
            Assert.Equal(35, hwPl1);
            Assert.Equal(35, hwPl2);

            // CRITICAL INVARIANT: AppSettings custom settings and BIOS defaults must NOT be modified
            var (userPl1, userPl2) = settings.GetUserPowerLimitsForMode(OperatingMode.Balanced);
            Assert.Equal(65, userPl1);
            Assert.Equal(95, userPl2);

            var (biosPl1, biosPl2, _) = settings.GetBiosDefaultLimitsForMode(OperatingMode.Balanced);
            Assert.Equal(45, biosPl1);
            Assert.Equal(75, biosPl2);

            // Temperatures return to safe levels: clear thermal safety cap
            controller.ClearThermalSafetyCap();
            Assert.Null(controller.ThermalSafetyCapWatts);

            // Hardware power limits must automatically restore to full user configured values (65W / 95W)
            controller.PawnIo.ReadPowerLimits(units, out hwPl1, out hwPl2, out _, out _);
            Assert.Equal(65, hwPl1);
            Assert.Equal(95, hwPl2);
        }

        #endregion

        #region Step 5: Fix UI Slider Binding, Coupling at Max PL2, & Division-by-Zero Guard

        [Fact]
        public void Step5_PredatorSlider_DivisionByZeroGuard_HandlesInvertedOrIdenticalBounds()
        {
            var slider = new PredatorSlider();

            // Set Maximum <= Minimum
            slider.Minimum = 50;
            slider.Maximum = 50;
            slider.Value = 50;

            // Paint should not throw DivideByZeroException
            using (var bmp = new Bitmap(300, 30))
            using (var g = Graphics.FromImage(bmp))
            {
                var pe = new PaintEventArgs(g, new Rectangle(0, 0, 300, 30));
                slider.Size = new Size(300, 30);
                slider.Refresh();
            }

            slider.Minimum = 100;
            slider.Maximum = 10; // Minimum > Maximum automatically repaired by properties
            Assert.True(slider.Maximum >= slider.Minimum);
        }

        [Fact]
        public void Step5_Form1_SlidersBoundToCpuMaxAllowedPl2Watts_AndSmoothlyCoupleAtMaxPl2()
        {
            RunInStaThread(() =>
            {
                using var form = new Form1();
                var settings = form.Settings;

                settings.CpuMinAllowedPowerWatts = 15;
                settings.CpuMaxAllowedPl1Watts = 115;
                settings.CpuMaxAllowedPl2Watts = 140;
                settings.CpuThermalSpecTdpWatts = 45;
                settings.HasCompletedBiosPowerScan = true;
                settings.BiosDefaultBalancedPl1Watts = 45;
                settings.BiosDefaultBalancedPl2Watts = 75;

                form.Capabilities = new DeviceCapabilities
                {
                    CpuTdpWatts = 45,
                    ScannedMinPowerWatts = 15,
                    ScannedMaxPl1Watts = 115,
                    ScannedMaxPl2Watts = 140
                };

                form.SetCurrentPowerModeForTesting(0x01); // Balanced
                form.UpdatePowerLimitsForMode(0x01);

                Assert.NotNull(form.SliderPl1);
                Assert.NotNull(form.SliderPl2);

                // Both sliders' Maximum bound to CpuMaxAllowedPl2Watts (140)
                Assert.Equal(140, form.SliderPl1.Maximum);
                Assert.Equal(140, form.SliderPl2.Maximum);

                // Both sliders' Minimum bound to CpuMinAllowedPowerWatts (15)
                Assert.Equal(15, form.SliderPl1.Minimum);
                Assert.Equal(15, form.SliderPl2.Minimum);

                // Drag PL1 all the way to CpuMaxAllowedPl2Watts (140)
                form.SliderPl1.Value = 140;

                // PL2 slider must smoothly couple and move to CpuMaxAllowedPl2Watts (140)
                Assert.Equal(140, form.SliderPl2.Value);
                Assert.Equal(form.SliderPl1.Value, form.SliderPl2.Value);

                // BIOS Default labels and Reset button must preserve immutable BIOS defaults
                var (bPl1, bPl2, _) = settings.GetBiosDefaultLimitsForMode(OperatingMode.Balanced);
                Assert.Equal(45, bPl1);
                Assert.Equal(75, bPl2);

                // Click Reset to BIOS Default button
                form.BtnResetPowerLimits_Click(null, EventArgs.Empty);

                // Values must restore to immutable BIOS defaults (45W / 75W)
                Assert.Equal(45, form.SliderPl1.Value);
                Assert.Equal(75, form.SliderPl2.Value);
            });
        }

        #endregion
    }
}
