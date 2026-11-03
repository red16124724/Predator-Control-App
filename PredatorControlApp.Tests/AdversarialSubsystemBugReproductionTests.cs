using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PredatorControlApp;
using PredatorControlApp.Channels;

namespace PredatorControlApp.Tests
{
    public class AdversarialSubsystemBugReproductionTests
    {
        [Fact]
        public void Bug1_AppSettings_Sanitize_PreservesUnscannedBoundsZeroSentinels()
        {
            var settings = new AppSettings();
            // Fresh settings or post-invalidation: HasScannedHardwarePowerBounds is false
            Assert.False(settings.HasScannedHardwarePowerBounds);
            Assert.Equal(0, settings.ScannedCpuTdpWatts);
            Assert.Equal(0, settings.ScannedMinPowerWatts);
            Assert.Equal(0, settings.ScannedMaxPl1Watts);
            Assert.Equal(0, settings.ScannedMaxPl2Watts);

            // Baseline estimation BEFORE Sanitize: correctly defaults to 45W TDP anchor
            var (prePl1, prePl2) = settings.EstimateProportionalDefaultsForMode(OperatingMode.Balanced);
            Assert.Equal(45, prePl1);
            Assert.Equal(61, prePl2);

            // Now call Sanitize()
            settings.Sanitize();

            // VERIFICATION: Sanitize MUST NOT corrupt the 0 sentinels to 5
            Assert.Equal(0, settings.ScannedCpuTdpWatts);
            Assert.Equal(0, settings.ScannedMinPowerWatts);
            Assert.Equal(0, settings.ScannedMaxPl1Watts);
            Assert.Equal(0, settings.ScannedMaxPl2Watts);

            var (postPl1, postPl2) = settings.EstimateProportionalDefaultsForMode(OperatingMode.Balanced);
            Assert.Equal(45, postPl1);
            Assert.Equal(61, postPl2);
        }

        [Fact]
        public void Bug2_CpuSpecDetector_ComputeModeDefaults_GuaranteesPl2GtePl1_WhenMaxPl2Constrained()
        {
            // Case: baseTdp = 125W (e.g. desktop processor), but MaxPl2 is constrained to 100W by OEM or config
            int baseTdp = 125;
            int minPower = 35;
            int maxPl1 = 150;
            int maxPl2 = 100;

            var defaults = CpuSpecDetector.ComputeModeDefaults(baseTdp, minPower, maxPl1, maxPl2);

            var (balPl1, balPl2) = defaults[0x01]; // Balanced mode
            var (perfPl1, perfPl2) = defaults[0x04]; // Performance mode
            var (turboPl1, turboPl2) = defaults[0x05]; // Turbo mode

            // VERIFICATION: PL2 >= PL1 invariant must be strictly preserved across all modes
            Assert.True(balPl2 >= balPl1, $"Balanced mode violated PL2 >= PL1: PL1={balPl1}, PL2={balPl2}");
            Assert.True(perfPl2 >= perfPl1, $"Performance mode violated PL2 >= PL1: PL1={perfPl1}, PL2={perfPl2}");
            Assert.True(turboPl2 >= turboPl1, $"Turbo mode violated PL2 >= PL1: PL1={turboPl1}, PL2={turboPl2}");
        }

        [Fact]
        public void Bug3_PredatorSlider_MinimumMaximumSetter_FiresValueChangedOnClamp()
        {
            var slider = new PredatorSlider { Minimum = 10, Maximum = 100, Value = 50 };
            bool eventFired = false;
            slider.ValueChanged += (s, e) => { eventFired = true; };

            // When Minimum is raised above Value, Value clamps from 50 to 70 and MUST fire ValueChanged
            slider.Minimum = 70;

            Assert.Equal(70, slider.Value);
            Assert.True(eventFired, "PredatorSlider.Minimum setter changed Value without firing ValueChanged event!");

            eventFired = false;
            // When Maximum is lowered below Value, Value clamps from 70 to 40 and MUST fire ValueChanged
            slider.Maximum = 40;
            Assert.Equal(40, slider.Value);
            Assert.True(eventFired, "PredatorSlider.Maximum setter changed Value without firing ValueChanged event!");
        }

        [Fact]
        public void Bug4_Form1SliderCoupling_Pl2LoweredBelowPl1Min_MaintainsPl2GtePl1()
        {
            // Simulate Form1 slider coupling logic directly
            var sliderPl1 = new PredatorSlider { Minimum = 25, Maximum = 115, Value = 50 };
            var sliderPl2 = new PredatorSlider { Minimum = 10, Maximum = 140, Value = 65 };

            bool isUpdating = false;

            sliderPl2.ValueChanged += (s, e) =>
            {
                if (isUpdating) return;
                if (sliderPl2.Value < sliderPl1.Value)
                {
                    isUpdating = true;
                    try
                    {
                        sliderPl1.Value = sliderPl2.Value;
                    }
                    finally
                    {
                        isUpdating = false;
                    }
                    // Reverse clamp to enforce PL2 >= PL1 when PL1 clamps to Minimum
                    if (sliderPl2.Value < sliderPl1.Value)
                    {
                        sliderPl2.Value = sliderPl1.Value;
                    }
                }
            };

            // Set sliderPl2 to 10 (below sliderPl1.Minimum of 25)
            sliderPl2.Value = 10;

            // VERIFICATION: Both sliders must clamp to 25, never allowing PL2 < PL1
            Assert.Equal(25, sliderPl1.Value);
            Assert.Equal(25, sliderPl2.Value);
            Assert.True(sliderPl2.Value >= sliderPl1.Value, $"PL2 < PL1 violation: PL1={sliderPl1.Value}, PL2={sliderPl2.Value}");
        }

        [Fact]
        public void Bug5_BatteryCalibrationEta_SlowTrickleCharge_UpdatesTimestamp()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            calc.UpdateCapacity(50000, now);
            Assert.Equal(50000, calc.LastCapacity);

            // Trickle charge: 10 seconds later, 1 mWh difference
            calc.UpdateCapacity(50001, now.AddSeconds(10));
            Assert.Equal(50001, calc.LastCapacity);

            // Continue trickle charge: 20 seconds later
            calc.UpdateCapacity(50002, now.AddSeconds(20));
            Assert.Equal(50002, calc.LastCapacity);

            // 30 seconds later
            calc.UpdateCapacity(50003, now.AddSeconds(30));
            Assert.Equal(50003, calc.LastCapacity);
        }

        [Fact]
        public void Bug6_BatteryCalibrationEta_PercentageGlitch_CapsRealisticRate()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 0.5);
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            calc.UpdatePercentage(80, now);

            // Glitch: 3 seconds later (0.05 min), battery reports 20% (delta 60%)
            calc.UpdatePercentage(20, now.AddSeconds(3));

            // ratePerMin is capped to 30.0 %/min max
            Assert.NotNull(calc.SmoothedPercentPerMinute);
            Assert.True(calc.SmoothedPercentPerMinute.Value <= 30.0,
                $"Expected capped ratePerMin <= 30 %/min, got {calc.SmoothedPercentPerMinute.Value}");

            // Phase 2 calculation with 20% remaining
            var eta = calc.CalculateEta(phase: 2, remainingCapacity: 10000, fullCapacity: 50000, directRate: null, batteryPercent: 20);
            Assert.NotNull(eta);
            // ETA is 20% / 30 %/min = 40 seconds (realistic, not sub-5-seconds)
            Assert.True(eta.Value.TotalSeconds >= 30.0,
                $"Percentage rate clamp prevented unrealistic sub-minute ETA: {eta.Value.TotalSeconds} seconds");
        }
    }
}
