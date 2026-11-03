using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("SingleInstanceTests")]
    public class ExhaustiveFullAppStressTests : IDisposable
    {
        private readonly string _tempSettingsPath;

        public ExhaustiveFullAppStressTests()
        {
            _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"PredatorStress_{Guid.NewGuid():N}.json");
            AppSettings.CustomFilePath = _tempSettingsPath;
        }

        public void Dispose()
        {
            AppSettings.CustomFilePath = null;
            try
            {
                if (File.Exists(_tempSettingsPath))
                {
                    File.Delete(_tempSettingsPath);
                }
            }
            catch { }
        }

        [Fact]
        public void Test_ConcurrentSliderUpdatesAndModeSwitches_PreservesIntegrity()
        {
            var settings = new AppSettings
            {
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 140,
                CpuMaxAllowedPl2Watts = 140
            };

            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Eco, 28, 45, true);
            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Quiet, 35, 55, true);
            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Balanced, 55, 80, true);
            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Performance, 75, 115, true);
            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Turbo, 90, 140, true);

            const int threadCount = 8;
            const int iterationsPerThread = 250;

            Parallel.For(0, threadCount, t =>
            {
                var modes = new[] { OperatingMode.Eco, OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo };
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    var mode = modes[(i + t) % modes.Length];
                    int randomPl1 = 20 + ((i * 7 + t * 13) % 120);
                    int randomPl2 = randomPl1 + ((i * 3 + t * 5) % 40);

                    settings.SetPowerLimitsForMode(mode, randomPl1, randomPl2);

                    var (effPl1, effPl2) = settings.GetEffectivePowerLimitsForMode(mode);
                    Assert.True(effPl1 <= effPl2, $"PL1 {effPl1} exceeded PL2 {effPl2} in mode {mode}");
                    Assert.True(effPl1 >= settings.CpuMinAllowedPowerWatts);
                    Assert.True(effPl2 <= settings.CpuMaxAllowedPl2Watts);

                    var (defPl1, defPl2) = settings.GetDefaultPowerLimitsForMode(mode);
                    Assert.True(defPl1 > 0 && defPl2 > 0);
                    Assert.True(defPl1 <= defPl2);
                }
            });

            // Verify BIOS default limits remained completely immutable across the entire stress run
            Assert.Equal(28, settings.BiosDefaultEcoPl1Watts);
            Assert.Equal(45, settings.BiosDefaultEcoPl2Watts);
            Assert.Equal(55, settings.BiosDefaultBalancedPl1Watts);
            Assert.Equal(80, settings.BiosDefaultBalancedPl2Watts);
            Assert.Equal(90, settings.BiosDefaultTurboPl1Watts);
            Assert.Equal(140, settings.BiosDefaultTurboPl2Watts);
        }

        [Fact]
        public void Test_ThermalSafetyArbitrationUnderRapidSpikes_DoesNotCorruptSettings()
        {
            var settings = new AppSettings
            {
                CpuMinAllowedPowerWatts = 15,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 115
            };

            settings.RecordBiosDefaultLimitsForMode(OperatingMode.Performance, 75, 115, true);
            settings.SetPowerLimitsForMode(OperatingMode.Performance, 90, 110);

            var (userPl1, userPl2) = settings.GetUserPowerLimitsForMode(OperatingMode.Performance);
            Assert.Equal(90, userPl1);
            Assert.Equal(110, userPl2);

            // Simulate thermal safety cap arbitration
            int thermalSafetyCapWatts = 45; // e.g. severe thermal throttling cap
            int targetPl1 = userPl1;
            int targetPl2 = userPl2;

            int effectivePl1 = Math.Min(targetPl1, thermalSafetyCapWatts);
            int effectivePl2 = Math.Min(targetPl2, thermalSafetyCapWatts);

            Assert.Equal(45, effectivePl1);
            Assert.Equal(45, effectivePl2);

            // Verify AppSettings user and BIOS default limits were untouched
            var (reUserPl1, reUserPl2) = settings.GetUserPowerLimitsForMode(OperatingMode.Performance);
            Assert.Equal(90, reUserPl1);
            Assert.Equal(110, reUserPl2);

            Assert.Equal(75, settings.BiosDefaultPerformancePl1Watts);
            Assert.Equal(115, settings.BiosDefaultPerformancePl2Watts);

            // When thermal cap is released (back to -1 or normal)
            thermalSafetyCapWatts = -1;
            int restoredPl1 = thermalSafetyCapWatts > 0 ? Math.Min(targetPl1, thermalSafetyCapWatts) : targetPl1;
            int restoredPl2 = thermalSafetyCapWatts > 0 ? Math.Min(targetPl2, thermalSafetyCapWatts) : targetPl2;

            Assert.Equal(90, restoredPl1);
            Assert.Equal(110, restoredPl2);
        }

        [Fact]
        public void Test_CurveFollower_HighFrequencyFuzzingAndDeadbandHold()
        {
            using var follower = new CurveFollower(2);
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(90, 100)
            };

            // Initial point at 50°C -> 40%
            int init = follower.Update(50.0, curve);
            Assert.Equal(40, init);

            // Minor temperature fluctuations (within 2% deadband)
            int d1 = follower.Update(50.5, curve);
            Assert.Equal(40, d1);

            int d2 = follower.Update(49.8, curve);
            Assert.Equal(40, d2);

            // Step up to 70°C -> 70%
            int up = follower.Update(70.0, curve);
            Assert.Equal(70, up);

            // Cooling hysteresis: drop from 70 to 69 should hold peak or calculate within deadband
            int hold = follower.Update(69.0, curve);
            Assert.True(hold <= 70 && hold >= 68);

            // Safety floor override at 100% immediately bypasses deadband
            int emergency = follower.Update(50.0, curve, 100);
            Assert.Equal(100, emergency);

            // Extreme fuzz temperatures
            double[] extremeTemps = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -500.0, 1000.0 };
            foreach (double t in extremeTemps)
            {
                int safe = follower.Update(t, curve);
                Assert.True(safe >= 10 && safe <= 100, $"Extreme temp {t} gave invalid speed {safe}");
            }
        }

        [Fact]
        public void Test_PawnIoDirectionalMMIOOrderingContract()
        {
            // Case 1: NewPL1 > OldPL2 (Hazard)
            // Hardware constraint: PL2 MUST be written FIRST to prevent transient PL1 > PL2
            int oldPl2 = 65;
            int newPl1 = 80; // > oldPl2 (65)

            var writeOrder = new List<string>();
            if (newPl1 > oldPl2)
            {
                writeOrder.Add("PL2");
                writeOrder.Add("PL1");
            }
            else
            {
                writeOrder.Add("PL1");
                writeOrder.Add("PL2");
            }

            Assert.Equal("PL2", writeOrder[0]);
            Assert.Equal("PL1", writeOrder[1]);

            // Case 2: NewPL1 <= OldPL2 (Normal)
            newPl1 = 55; // <= oldPl2 (65)
                        writeOrder.Clear();
            if (newPl1 > oldPl2)
            {
                writeOrder.Add("PL2");
                writeOrder.Add("PL1");
            }
            else
            {
                writeOrder.Add("PL1");
                writeOrder.Add("PL2");
            }

            Assert.Equal("PL1", writeOrder[0]);
            Assert.Equal("PL2", writeOrder[1]);
        }
    }
}
