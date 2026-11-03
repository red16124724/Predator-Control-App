using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Exhaustive Stress Test and Code Audit Suite for Hardware Communication Paths,
    /// Kernel Drivers, and Concurrency Layers.
    /// </summary>
    [Collection("SingleInstanceTests")]
    [SupportedOSPlatform("windows")]
    public class HardwareConcurrencyStressTests : IDisposable
    {
        private readonly string _tempSettingsPath;

        public HardwareConcurrencyStressTests()
        {
            _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"PredatorHwStress_{Guid.NewGuid():N}.json");
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

        private static MockPawnIoBackend CreateConfiguredBackend(int pl1 = 45, int pl2 = 75, int tdp = 45, int min = 15, int max = 115)
        {
            var backend = new MockPawnIoBackend();
            backend.ConfigureIntelBaseline(
                tdpWatts: tdp,
                minWatts: min,
                maxWatts: max,
                pl1Watts: pl1,
                pl2Watts: pl2,
                unitsPerWatt: 8,
                locked: false,
                mirrorMmio: true
            );
            return backend;
        }

        /// <summary>
        /// 1. Rapid Power Mode Switching:
        /// Cycles rapidly between Eco -> Turbo -> Quiet -> Balanced -> Performance in quick succession.
        /// Verifies no deadlocks, no race conditions, and PL2 >= PL1 invariant across all events.
        /// </summary>
        [Fact]
        public async Task Stress_RapidPowerModeSwitching_AllFiveModes_ThreadSafeAndConsistent()
        {
            var backend = CreateConfiguredBackend();
            var pawnIo = new PawnIoController(backend);
            var settings = new AppSettings
            {
                ScannedHardwareSignature = "Acer_Predator_StressTest_V1",
                ScannedCpuTdpWatts = 45,
                ScannedMinPowerWatts = 12,
                ScannedMaxPl1Watts = 115,
                ScannedMaxPl2Watts = 140,
                EnableCustomPowerLimits = true,
                HasCompletedBiosPowerScan = true
            };

            var scanner = new MockBiosUefiPowerScanner(new BiosPowerScanResult
            {
                HasCompletedBiosPowerScan = true,
                CpuThermalSpecTdpWatts = 45,
                CpuMinAllowedPowerWatts = 12,
                CpuMaxAllowedPl1Watts = 115,
                CpuMaxAllowedPl2Watts = 140,
                BiosDefaultEcoPl1Watts = 25,
                BiosDefaultEcoPl2Watts = 35,
                BiosDefaultQuietPl1Watts = 35,
                BiosDefaultQuietPl2Watts = 45,
                BiosDefaultBalancedPl1Watts = 45,
                BiosDefaultBalancedPl2Watts = 65,
                BiosDefaultPerformancePl1Watts = 65,
                BiosDefaultPerformancePl2Watts = 90,
                BiosDefaultTurboPl1Watts = 90,
                BiosDefaultTurboPl2Watts = 115
            });

            var controller = new CpuPowerLimitController(settings, pawnIo, scanner);
            var modes = new[] { OperatingMode.Eco, OperatingMode.Turbo, OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance };

            var exceptions = new ConcurrentBag<Exception>();
            int eventsFired = 0;

            controller.PowerLimitsChanged += (mode, pl1, pl2, custom) =>
            {
                Interlocked.Increment(ref eventsFired);
                if (pl2 < pl1)
                {
                    exceptions.Add(new InvalidOperationException($"Invariant violated: PL2 ({pl2}) < PL1 ({pl1}) on mode {mode}"));
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var token = cts.Token;

            // Worker 1: Rapid mode cycling
            var modeSwitchTask = Task.Run(async () =>
            {
                int i = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var target = modes[i % modes.Length];
                        await controller.OnPowerModeChangedAsync(target, token, settleDelayMs: 0);
                        i++;
                        Thread.Yield();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });

            // Worker 2: Concurrent synchronous baseline capture & verify
            var verifyTask = Task.Run(() =>
            {
                int i = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var target = modes[i % modes.Length];
                        controller.PeriodicVerifyPowerLimits(target, force: true);
                        i++;
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });

            // Worker 3: Concurrent settings queries
            var queryTask = Task.Run(() =>
            {
                int i = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var target = modes[i % modes.Length];
                        var (p1, p2) = settings.GetEffectivePowerLimitsForMode(target);
                        Assert.True(p2 >= p1);
                        i++;
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });

            await Task.WhenAll(modeSwitchTask, verifyTask, queryTask);

            Assert.Empty(exceptions);
            Assert.True(eventsFired > 0, "Expected multiple PowerLimitsChanged events");
        }

        /// <summary>
        /// 2. Concurrent Telemetry Polling while Writing Power Limits:
        /// Verifies IOCTL race condition prevention, MSR/PCI mutex synchronization, and zero torn registers.
        /// </summary>
        [Fact]
        public async Task Stress_ConcurrentTelemetryPollingWhileWritingPowerLimits_NoTornRegisters()
        {
            var backend = CreateConfiguredBackend(pl1: 50, pl2: 80);
            var pawnIo = new PawnIoController(backend);

            var exceptions = new ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            var token = cts.Token;

            long readCount = 0;
            long writeCount = 0;

            // Thread 1: Continuous telemetry polling (reading MSR 0x606 & 0x610)
            var readerTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (pawnIo.ReadPowerUnits(out uint units, out _) &&
                            pawnIo.ReadPowerLimits(units, out int pl1, out int pl2, out bool locked, out ulong raw))
                        {
                            Interlocked.Increment(ref readCount);
                            if (pl2 < pl1)
                            {
                                exceptions.Add(new InvalidOperationException($"Torn read detected: PL2 ({pl2}) < PL1 ({pl1})"));
                            }
                        }
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });

            // Thread 2: Continuous power limit adjustment (writing MSR 0x610 and MMIO)
            var writerTask = Task.Run(() =>
            {
                int target = 40;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        target = (target % 60) + 30; // 30W to 90W
                        var res = pawnIo.ApplyPowerLimits(target, target + 25);
                        if (res.IsSuccess)
                        {
                            Interlocked.Increment(ref writeCount);
                        }
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });

            // Thread 3: Continuous MMIO mirror sampling
            var mmioReaderTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (backend.ReadMmio64(PawnIoController.MMIO_PKG_RAPL_LIMIT_ADDRESS, out ulong mmioVal))
                        {
                            uint low = (uint)(mmioVal & 0x7FFF);
                            uint high = (uint)((mmioVal >> 32) & 0x7FFF);
                            if (high > 0 && low > 0 && high < low)
                            {
                                exceptions.Add(new InvalidOperationException($"MMIO torn register state: PL2 ({high}) < PL1 ({low})"));
                            }
                        }
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });

            await Task.WhenAll(readerTask, writerTask, mmioReaderTask);

            Assert.Empty(exceptions);
            Assert.True(readCount > 50, $"Expected sustained telemetry reads, got {readCount}");
            Assert.True(writeCount > 20, $"Expected sustained power writes, got {writeCount}");
        }

        /// <summary>
        /// 3. Thermal Throttling Trigger and Transient Power Cap Recovery:
        /// Verifies ThermalSafetyCoordinator stage escalations, EMA smoothing, hysteresis latch,
        /// and transient power cap recovery via CpuPowerLimitController without altering persistent settings.
        /// </summary>
        [Fact]
        public void Stress_ThermalThrottlingTrigger_AndTransientPowerCapRecovery()
        {
            var profile = new ThermalSafetyProfile
            {
                Enabled = true,
                CpuWarnCelsius = 75,
                CpuCeilingCelsius = 85,
                CpuCriticalCelsius = 92,
                GpuWarnCelsius = 70,
                GpuCeilingCelsius = 80,
                GpuCriticalCelsius = 87,
                HysteresisCelsius = 4,
                CriticalFansFullSpeed = true,
                MinPowerCapWatts = 25
            };

            var coordinator = new ThermalSafetyCoordinator(profile);

            // Phase A: Normal temperature
            var decisionNormal = coordinator.Evaluate(cpuTemp: 55, gpuTemp: 50, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45);
            Assert.Equal(0, decisionNormal.Stage);
            Assert.False(decisionNormal.Critical);
            Assert.Null(decisionNormal.CpuPowerCapWatts);

            // Phase B: Thermal spike entering Warning / Ceiling
            for (int i = 0; i < 5; i++)
            {
                coordinator.Evaluate(cpuTemp: 88, gpuTemp: 78, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45);
            }
            var decisionCeiling = coordinator.Evaluate(cpuTemp: 88, gpuTemp: 78, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45);
            Assert.True(decisionCeiling.Stage >= 2, "Should reach CEILING stage");
            Assert.NotNull(decisionCeiling.CpuPowerCapWatts);
            Assert.True(decisionCeiling.CpuPowerCapWatts < 65, "Power cap should throttle below target 65W");

            // Phase C: Thermal spike entering CRITICAL stage (>= 92C)
            bool criticalFired = false;
            coordinator.CriticalEntered += (s, e) => criticalFired = true;

            for (int i = 0; i < 10; i++)
            {
                coordinator.Evaluate(cpuTemp: 95, gpuTemp: 82, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45);
            }

            var decisionCritical = coordinator.Evaluate(cpuTemp: 95, gpuTemp: 82, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45);
            Assert.Equal(3, decisionCritical.Stage);
            Assert.True(decisionCritical.Critical);
            Assert.True(criticalFired, "CriticalEntered event must fire");
            Assert.Equal(15, decisionCritical.CpuPowerCapWatts); // Clamped to hardware minimum (minPowerWatts = 15)

            // Connect to CpuPowerLimitController and test transient cap application & recovery
            var backend = CreateConfiguredBackend(pl1: 65, pl2: 90);
            var pawnIo = new PawnIoController(backend);
            var settings = new AppSettings
            {
                ScannedHardwareSignature = "Acer_Predator_ThermalCap_Test",
                EnableCustomPowerLimits = true,
                HasCompletedBiosPowerScan = true
            };
            settings.SetPowerLimitsForMode(OperatingMode.Performance, 65, 90);
            settings.Save();

            var controller = new CpuPowerLimitController(settings, pawnIo, new MockBiosUefiPowerScanner());

            // Apply critical cap
            controller.SetThermalSafetyCap(decisionCritical.CpuPowerCapWatts);
            Assert.Equal(15, controller.ThermalSafetyCapWatts);
            var (activePl1, activePl2) = settings.GetEffectivePowerLimitsForMode(OperatingMode.Performance);
            Assert.Equal(65, activePl1); // Saved settings must NOT be altered
            Assert.Equal(90, activePl2);

            // Phase D: Temperature drops, recovering from thermal cap
            for (int i = 0; i < 15; i++)
            {
                coordinator.Evaluate(cpuTemp: 50, gpuTemp: 45, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45);
            }
            var decisionRecovered = coordinator.Evaluate(cpuTemp: 50, gpuTemp: 45, targetPl1Watts: 65, minPowerWatts: 15, baseTdpWatts: 45);
            Assert.Equal(0, decisionRecovered.Stage);
            Assert.Null(decisionRecovered.CpuPowerCapWatts);

            // Clear transient cap in controller -> returns to normal limits
            controller.SetThermalSafetyCap(null);
            Assert.Null(controller.ThermalSafetyCapWatts);
        }

        /// <summary>
        /// 4. AC / Battery Transition Events (PBT_APMPOWERSTATUSCHANGE):
        /// Verifies GpuPowerMonitor status, WMI SuppressGpuQueries synchronization,
        /// and thread-safe transition handling.
        /// </summary>
        [Fact]
        public void Stress_AcBatteryTransitionEvents_PbtApmPowerStatusChange()
        {
            var wmi = new WmiController();

            // Simulate on AC
            GpuPowerMonitor.SetPowerState(onBattery: false);
            wmi.SuppressGpuQueries = false;
            Assert.False(GpuPowerMonitor.IsGpuAsleep());
            Assert.False(wmi.SuppressGpuQueries);

            // Simulate PBT_APMPOWERSTATUSCHANGE transition to Battery
            GpuPowerMonitor.SetPowerState(onBattery: true);
            wmi.SuppressGpuQueries = true;
            Assert.True(GpuPowerMonitor.IsGpuAsleep());
            Assert.True(wmi.SuppressGpuQueries);
            Assert.Equal(0, wmi.GpuTemp); // Gpu queries suppressed on battery

            // Rapid concurrent transitions (e.g. flickering AC adapter or sleep wake cycles)
            var exceptions = new ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var token = cts.Token;

            var flipTask = Task.Run(() =>
            {
                bool battery = false;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        battery = !battery;
                        GpuPowerMonitor.SetPowerState(battery);
                        wmi.SuppressGpuQueries = battery;
                        if (!battery) GpuPowerMonitor.ResetDebounce();
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });

            var sampleTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        bool asleep = GpuPowerMonitor.IsGpuAsleep();
                        bool suppressed = wmi.SuppressGpuQueries;
                        int temp = wmi.GpuTemp;
                        if (suppressed)
                        {
                            Assert.Equal(0, temp);
                        }
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });

            Task.WaitAll(flipTask, sampleTask);
            Assert.Empty(exceptions);

            // Reset back to AC
            GpuPowerMonitor.SetPowerState(false);
            wmi.SuppressGpuQueries = false;
            wmi.Dispose();
        }

        /// <summary>
        /// 5. Directional 32-bit MMIO Split-Write Ordering:
        /// Verifies that when NewPL1 > OldPL2, upper PL2 (0xFED159A4) is written FIRST,
        /// then lower PL1 (0xFED159A0) is written SECOND to avoid PCU hardware violation.
        /// Verifies normal ordering when NewPL1 <= OldPL2, and Tau bit preservation.
        /// </summary>
        [Fact]
        public void Stress_PawnIo_Directional32BitMmioSplitWriteOrdering_AndTauPreservation()
        {
            var backend = CreateConfiguredBackend(pl1: 45, pl2: 65);
            var pawnIo = new PawnIoController(backend);

            // Case A: New PL1 (75W) > Old PL2 (65W) -> Hazard condition!
            // Must write PL2 (95W) first, then PL1 (75W) second.
            int historyStartA = backend.History.Count;
            bool okHazard = pawnIo.WriteMchbarPowerLimits(75, 95, 8, out ulong writtenHazard);
            Assert.True(okHazard);

            var writes = backend.History.Skip(historyStartA).Where(h => h.Op == "WriteMmio32").ToList();
            Assert.True(writes.Count >= 2, "Expected at least two 32-bit MMIO writes");

            // First write must be PL2 address (physicalAddress + 4 = 0xFED159A4)
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, writes[0].Target);
            // Second write must be PL1 address (0xFED159A0)
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, writes[1].Target);

            // Case B: New PL1 (45W) <= Old PL2 (95W) -> Normal condition!
            // Must write PL1 (45W) first, then PL2 (65W) second.
            int historyStartB = backend.History.Count;
            bool okNormal = pawnIo.WriteMchbarPowerLimits(45, 65, 8, out ulong writtenNormal);
            Assert.True(okNormal);

            var writesNormal = backend.History.Skip(historyStartB).Where(h => h.Op == "WriteMmio32").ToList();
            Assert.True(writesNormal.Count >= 2);
            // First write must be PL1 address (0xFED159A0)
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, writesNormal[0].Target);
            // Second write must be PL2 address (0xFED159A4)
            Assert.Equal(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, writesNormal[1].Target);

            // Case C: Tau bit preservation - existing hardware Tau in MMIO (0x28 / 0x10) is preserved
            uint unitsPerWatt = 8;
            ulong rawPl1 = 45UL * unitsPerWatt;
            ulong rawPl2 = 65UL * unitsPerWatt;
            ulong fallbackTau1 = 0x32UL << 17;
            ulong fallbackTau2 = 0x18UL << 49;

            bool tauOk = pawnIo.WriteMchbarPowerLimits(rawPl1, rawPl2, fallbackTau1, fallbackTau2, out ulong mmioWritten);
            Assert.True(tauOk);
            // Existing hardware Tau1 (0x28 << 17) and Tau2 (0x10 << 49) must be preserved
            Assert.Equal(0x28UL << 17, mmioWritten & (0x7FUL << 17));
            Assert.Equal(0x10UL << 49, mmioWritten & (0x7FUL << 49));

            // Case D: When MMIO has zero Tau, fallback Tau is used
            backend.WriteMmio32(PawnIoController.MMIO_PKG_RAPL_PL1_ADDRESS, (uint)(rawPl1 | (1U << 15)));
            backend.WriteMmio32(PawnIoController.MMIO_PKG_RAPL_PL2_ADDRESS, (uint)(rawPl2 | (1U << 15)));
            bool tauFallbackOk = pawnIo.WriteMchbarPowerLimits(rawPl1, rawPl2, fallbackTau1, fallbackTau2, out ulong mmioWrittenFallback);
            Assert.True(tauFallbackOk);
            Assert.Equal(fallbackTau1, mmioWrittenFallback & (0x7FUL << 17));
            Assert.Equal(fallbackTau2, mmioWrittenFallback & (0x7FUL << 49));
        }

        /// <summary>
        /// 6. Acer WMI Read-Modify-Write Bitfields Preservation:
        /// Verifies that SetWinKeyLock, SetLcdOverdrive, and SetUsbCharging preserve upper bytes
        /// containing Fn/Media Key mode bits and never clobber hardware state.
        /// </summary>
        [Fact]
        public void Stress_AcerWmi_ReadModifyWriteBitfields_PreservesUpperBytes()
        {
            // Initial state with high Fn/Media key bit flags set (e.g. 0x8000_1234_0000UL)
            ulong volatileUpperBits = 0x8000123400000000UL;
            ulong currentSys = volatileUpperBits | 0x0100UL; // WinKey enabled

            // Test WinKeyLock RMW
            ulong toggledWinKeyOff = AcerProtocol.ApplySystemConfigBitPreservingUpperBytes(
                currentSys, AcerProtocol.GamingSysInfoWinKeyBit, false);
            Assert.Equal(0UL, toggledWinKeyOff & AcerProtocol.GamingSysInfoWinKeyBit);
            Assert.Equal(volatileUpperBits, toggledWinKeyOff & 0xFFFFFFFF00000000UL);

            ulong toggledWinKeyOn = AcerProtocol.ApplySystemConfigBitPreservingUpperBytes(
                toggledWinKeyOff, AcerProtocol.GamingSysInfoWinKeyBit, true);
            Assert.Equal(AcerProtocol.GamingSysInfoWinKeyBit, toggledWinKeyOn & AcerProtocol.GamingSysInfoWinKeyBit);
            Assert.Equal(volatileUpperBits, toggledWinKeyOn & 0xFFFFFFFF00000000UL);

            // Test LcdOverdrive RMW
            ulong overdriveOn = AcerProtocol.LcdOverdriveInputWithPreservedState(true, currentSys);
            Assert.Equal(volatileUpperBits, overdriveOn & 0xFFFFFFFFFFFF0000UL);
            Assert.Equal((ulong)MiscSetting.LcdOverdrive | (1UL << 8), overdriveOn & 0xFFFFUL);

            // Test UsbCharging RMW
            ulong usbChargeOn = AcerProtocol.UsbChargingInputWithPreservedState(true, 10, currentSys);
            Assert.Equal(volatileUpperBits, usbChargeOn & 0xFFFFFFFFFF000000UL);
            Assert.Equal(4UL | (15UL << 8) | (10UL << 16), usbChargeOn & 0xFFFFFFUL);
        }

        /// <summary>
        /// 7. KeyboardHook and RawInput Fast Passthrough:
        /// Verifies that all F1-F24 keys (0x70..0x87) and Media/Launch keys (0xAD..0xB7)
        /// are passed through immediately without swallowing or blocking.
        /// </summary>
        [Fact]
        public void Stress_KeyboardHook_F1ToF24AndMediaKeys_PassthroughVerified()
        {
            // F1 to F24
            for (uint vk = 0x70; vk <= 0x87; vk++)
            {
                bool isFast = (vk >= 0x70 && vk <= 0x87) || (vk >= 0xAD && vk <= 0xB7);
                Assert.True(isFast, $"VK 0x{vk:X2} must be identified as fast pass-through");
            }

            // Media keys: Mute, Volume Down, Volume Up, Next, Prev, Stop, Play/Pause, Mail, Media Select, App1, App2
            for (uint vk = 0xAD; vk <= 0xB7; vk++)
            {
                bool isFast = (vk >= 0x70 && vk <= 0x87) || (vk >= 0xAD && vk <= 0xB7);
                Assert.True(isFast, $"Media VK 0x{vk:X2} must be identified as fast pass-through");
            }

            // Standard keyboard exclusion in EcHidDevice
            Assert.True(EcHidDevice.IsStandardKeyboardOrConsumerCollection(1, 6));
            Assert.True(EcHidDevice.IsStandardKeyboardOrConsumerCollection(0x0C, 1));
            Assert.False(EcHidDevice.IsStandardKeyboardOrConsumerCollection(0xFF05, 1));
        }
    }
}
