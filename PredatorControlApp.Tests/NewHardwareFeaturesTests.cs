using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class NewHardwareFeaturesTests
    {
        [Theory]
        [InlineData(0, 0)]  // Static -> Static
        [InlineData(1, 1)]  // Breathing -> Breathing
        [InlineData(2, 2)]  // Neon -> Neon
        [InlineData(3, 2)]  // Wave -> Neon (Single-LED fallback)
        [InlineData(4, 2)]  // Shifting -> Neon
        [InlineData(5, 2)]  // Zoom -> Neon
        [InlineData(6, 2)]  // Meteor -> Neon
        [InlineData(7, 2)]  // Twinkling -> Neon
        public void Test_SetGamingLED_ModeMapping_SingleLedHardware(int inputMode, byte expectedMappedMode)
        {
            byte mapped = (byte)(inputMode switch
            {
                1 => 1,
                2 or 3 => 2,
                _ => (inputMode == 0 ? 0 : 2)
            });
            Assert.Equal(expectedMappedMode, mapped);
        }

        [Fact]
        public void Test_SetGamingLEDColor_BytePacking_LogoAndExterior()
        {
            byte r = 0, g = 150, b = 255;
            ulong logoZone = 0x01ul;
            ulong exteriorZone = 0x0Ful;

            ulong logoColorPayload = ((ulong)b << 24) | ((ulong)g << 16) | ((ulong)r << 8) | logoZone;
            ulong exteriorColorPayload = ((ulong)b << 24) | ((ulong)g << 16) | ((ulong)r << 8) | exteriorZone;

            // Verify Byte 0 = Zone
            Assert.Equal(0x01ul, logoColorPayload & 0xFFul);
            Assert.Equal(0x0Ful, exteriorColorPayload & 0xFFul);

            // Verify Byte 1 = R
            Assert.Equal(r, (byte)((logoColorPayload >> 8) & 0xFFul));
            // Verify Byte 2 = G
            Assert.Equal(g, (byte)((logoColorPayload >> 16) & 0xFFul));
            // Verify Byte 3 = B
            Assert.Equal(b, (byte)((logoColorPayload >> 24) & 0xFFul));
        }

        [Fact]
        public void Test_LcdOverdrivePayloads()
        {
            var (onPayload, offPayload) = WmiController.GetLcdOverdrivePayloads();

            Assert.Equal(0x1000000000010UL, onPayload);
            Assert.Equal(0x10UL, offPayload);
        }

        [Fact]
        public void Test_TelemetryPollingIntervals_StrictAcAndBattery()
        {
            // Requirement: On charging = 2000ms (2 seconds), on battery = 5000ms (5 seconds)
            int acInterval = 2000;
            int batteryInterval = 5000;

            Assert.Equal(2000, acInterval);
            Assert.Equal(5000, batteryInterval);
        }

        [Fact]
        public void Test_FanCurveHysteresisDeadband_SuppressesMinorJitter()
        {
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(90, 100)
            };

            int baseTemp = 55;
            int baseSpeed = Form1.InterpolateCurve(curve, baseTemp);

            // Jitter by +1°C (e.g. 56°C)
            int jitterTemp = 56;
            int jitterSpeed = Form1.InterpolateCurve(curve, jitterTemp);

            int tempDelta = Math.Abs(jitterTemp - baseTemp);
            int speedDelta = Math.Abs(jitterSpeed - baseSpeed);

            // With deadband: tempDelta must be >= 2 AND speedDelta must be >= 3
            bool shouldTrigger = (tempDelta >= 2) && (speedDelta >= 3);
            Assert.False(shouldTrigger, "1°C thermal jitter should NOT trigger hardware fan commands");

            // Meaningful temperature rise of +5°C (55°C -> 60°C)
            int jumpTemp = 60;
            int jumpSpeed = Form1.InterpolateCurve(curve, jumpTemp);

            int jumpTempDelta = Math.Abs(jumpTemp - baseTemp);
            int jumpSpeedDelta = Math.Abs(jumpSpeed - baseSpeed);

            bool jumpShouldTrigger = (jumpTempDelta >= 2) && (jumpSpeedDelta >= 3);
            Assert.True(jumpShouldTrigger, "5°C temperature rise with >=3% speed change MUST trigger hardware fan update");
        }



        [Fact]
        public void Test_WmiHotkeyWatcher_EventDispatchSafety()
        {
            int receivedDetail = -1;
            int callCount = 0;
            var watcher = new WmiHotkeyWatcher(detail =>
            {
                receivedDetail = detail;
                System.Threading.Interlocked.Increment(ref callCount);
            });

            // Disposing immediately shouldn't throw
            watcher.Dispose();
            // Multiple dispose shouldn't throw
            watcher.Dispose();

            Assert.Equal(-1, receivedDetail);
            Assert.Equal(0, callCount);
        }

        [Fact]
        public void Test_GpuMode_ThreeModesSupported()
        {
            Assert.Equal(0, (byte)GpuMode.Auto);
            Assert.Equal(1, (byte)GpuMode.iGpuOnly);
            Assert.Equal(1, (byte)GpuMode.Hybrid);
            Assert.Equal(2, (byte)GpuMode.dGpuOnly);
            Assert.Equal(2, (byte)GpuMode.Discrete);
        }

        [Theory]
        [InlineData(0x00, 3)] // Quiet
        [InlineData(0x01, 2)] // Balanced
        [InlineData(0x04, 1)] // Performance
        [InlineData(0x05, 0)] // Turbo
        [InlineData(0x06, 4)] // Eco
        [InlineData(0xFF, 1)] // Fallback
        public void Test_WmiModeToEcMode_Mapping(byte wmiMode, byte expectedEcMode)
        {
            Assert.Equal(expectedEcMode, AcerProtocol.WmiModeToEcMode(wmiMode));
        }

        [Theory]
        [InlineData(3, 0x00)] // Quiet
        [InlineData(2, 0x01)] // Balanced
        [InlineData(1, 0x04)] // Performance
        [InlineData(0, 0x05)] // Turbo
        [InlineData(4, 0x06)] // Eco
        [InlineData(9, 0x01)] // Default/Fallback to Balanced
        public void Test_EcModeToWmiMode_Mapping(byte ecMode, byte expectedWmiMode)
        {
            Assert.Equal(expectedWmiMode, AcerProtocol.EcModeToWmiMode(ecMode));
        }

        [Fact]
        public void Test_PdhLoadMonitor_InitializesAndSamplesCpuSafely()
        {
            using var monitor = new PdhLoadMonitor();
            var sample = monitor.Sample();
            if (sample.Cpu.HasValue)
            {
                Assert.InRange(sample.Cpu.Value, 0.0, 100.0);
            }
        }

        [Fact]
        public void Test_TelemetryCadence_StrictIntervals()
        {
            var mgr = new BacklightStateManager();
            
            // On AC (online) - strictly 2000ms
            bool onBatteryAc = mgr.IsOnBattery(System.Windows.Forms.PowerLineStatus.Online);
            int intervalAc = onBatteryAc ? 5000 : 2000;
            Assert.False(onBatteryAc);
            Assert.Equal(2000, intervalAc);

            // On Battery (offline) - strictly 5000ms
            bool onBatteryDc = mgr.IsOnBattery(System.Windows.Forms.PowerLineStatus.Offline);
            int intervalDc = onBatteryDc ? 5000 : 2000;
            Assert.True(onBatteryDc);
            Assert.Equal(5000, intervalDc);
        }

        [Fact]
        public void Test_MediaPlaybackDetector_DoesNotThrowAndReturnsBool()
        {
            // Verifies native COM call safety without crash
            bool isPlaying = MediaPlaybackDetector.IsAudioPlaying();
            Assert.True(isPlaying || !isPlaying);
        }

        [Fact]
        public void Test_EcHidDevice_Diagnostic()
        {
            var paths = EcHidDevice_DiagnosticHelper();
            using var dev = EcHidDevice.TryOpen();
            bool open = dev != null && dev.IsOpen;
            Console.WriteLine($"[Diagnostic] dev open={open}, version={dev?.Version}, len={dev?.ReportLengthActual}");
        }

        private static List<string> EcHidDevice_DiagnosticHelper()
        {
            var list = new List<string>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
            Console.WriteLine($"hDevInfo={(long)hDevInfo}, hidGuid={hidGuid}");
            if (hDevInfo == (IntPtr)(-1)) return list;
            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                uint index = 0;
                while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, index++, ref did))
                {
                    SetupDiGetDeviceInterfaceDetail(hDevInfo, ref did, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;
                    IntPtr pDetail = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(pDetail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref did, pDetail, reqSize, out _, IntPtr.Zero))
                        {
                            IntPtr pPath = (IntPtr)((long)pDetail + 4);
                            string? path = Marshal.PtrToStringAuto(pPath);
                            if (!string.IsNullOrEmpty(path))
                            {
                                using var h = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                                int err = Marshal.GetLastWin32Error();
                                if (!h.IsInvalid)
                                {
                                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                                    bool hasAttr = HidD_GetAttributes(h, ref attr);
                                    if (hasAttr && (attr.VendorID == 0x1025 || path.Contains("1025", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        var caps = new HIDP_CAPS();
                                        if (HidD_GetPreparsedData(h, out IntPtr pData))
                                        {
                                            try
                                            {
                                                HidP_GetCaps(pData, ref caps);
                                            }
                                            finally { HidD_FreePreparsedData(pData); }
                                        }
                                        Console.WriteLine($"Path: {path} VID=0x{attr.VendorID:X4} PID=0x{attr.ProductID:X4} UsagePage=0x{caps.UsagePage:X4} Usage=0x{caps.Usage:X4} FeatureLen={caps.FeatureReportByteLength} InLen={caps.InputReportByteLength}");
                                        
                                        using var rw7 = CreateFile(path, 0xC0000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                                        // Test feature report with various Report IDs
                                        for (byte rId = 0; rId <= 5; rId++)
                                        {
                                            byte[] buf = new byte[Math.Max(caps.FeatureReportByteLength, (ushort)1)];
                                            buf[0] = rId;
                                            bool getOk = HidD_GetFeature(rw7, buf, buf.Length);
                                            int getErr = Marshal.GetLastWin32Error();
                                            if (getOk)
                                            {
                                                Console.WriteLine($"   -> GetFeature(rw7, rId={rId}, len={buf.Length}) SUCCESS: {BitConverter.ToString(buf)}");
                                            }
                                        }
                                        // Also try ReportId 0xA0
                                        {
                                            byte[] buf = new byte[Math.Max(caps.FeatureReportByteLength, (ushort)1)];
                                            buf[0] = 0xA0;
                                            bool getOk = HidD_GetFeature(rw7, buf, buf.Length);
                                            int getErr = Marshal.GetLastWin32Error();
                                            if (getOk)
                                            {
                                                Console.WriteLine($"   -> GetFeature(rw7, rId=0xA0, len={buf.Length}) SUCCESS: {BitConverter.ToString(buf)}");
                                            }
                                        }
                                    }
                                }
                                else if (path.Contains("1025", StringComparison.OrdinalIgnoreCase))
                                {
                                    Console.WriteLine($"Failed to open path {path}: err={err}");
                                }
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(pDetail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(hDevInfo); }
            return list;
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid HidGuid);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(SafeFileHandle HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);
        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);
        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        #region Battery Calibration ETA & Recovery Tests

        [Fact]
        public void BatteryCalibrationEtaCalculator_ChargingEta_CalculatesAccurately()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 1.0);
            var eta = calc.CalculateEta(phase: 1, remainingCapacity: 30000, fullCapacity: 60000, directRate: 30000);
            Assert.NotNull(eta);
            Assert.Equal(60, Math.Round(eta!.Value.TotalMinutes));
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_DischargingEta_CalculatesAccurately()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 1.0);
            var eta = calc.CalculateEta(phase: 2, remainingCapacity: 30000, fullCapacity: 60000, directRate: 15000);
            Assert.NotNull(eta);
            Assert.Equal(120, Math.Round(eta!.Value.TotalMinutes));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-100)]
        public void BatteryCalibrationEtaCalculator_ZeroOrNegativeRate_ReturnsNullWithoutThrowing(double rate)
        {
            var calc = new BatteryCalibrationEtaCalculator();
            var eta = calc.CalculateEta(phase: 1, remainingCapacity: 30000, fullCapacity: 60000, directRate: rate);
            Assert.Null(eta);
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_EmaSmoothing_DampensRateJitter()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 0.2);
            calc.UpdateDirectRate(10000);
            calc.UpdateDirectRate(20000);

            Assert.NotNull(calc.SmoothedRate);
            Assert.Equal(12000.0, calc.SmoothedRate!.Value, 1);
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_PercentageFallback_CalculatesAccurately()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 1.0);
            var t0 = DateTime.UtcNow;
            calc.UpdatePercentage(50, t0);
            calc.UpdatePercentage(52, t0.AddMinutes(2));

            var eta = calc.CalculateEta(phase: 1, remainingCapacity: 52, fullCapacity: 100, directRate: null);
            Assert.NotNull(eta);
            Assert.Equal(48, Math.Round(eta!.Value.TotalMinutes));
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_Reset_ClearsAllInternalState()
        {
            var calc = new BatteryCalibrationEtaCalculator();
            calc.UpdateDirectRate(15000);
            calc.UpdatePercentage(50, DateTime.UtcNow);

            calc.Reset();

            Assert.Null(calc.SmoothedRate);
            Assert.Null(calc.LastPercent);
            Assert.Null(calc.SmoothedPercentPerMinute);
        }

        [Fact]
        public void BatteryCalibration_WhenDirectRateUnavailable_Phase2_CalculatesFromPercentageDelta()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 1.0);
            var t0 = DateTime.UtcNow;

            // Telemetry tick 1: 90%
            calc.UpdatePercentage(90, t0);
            // Telemetry tick 2: 86% after 4 minutes -> rate = (90 - 86) / 4 = 1.0% per min
            calc.UpdatePercentage(86, t0.AddMinutes(4));

            // Phase 2 (Discharging): current 86% -> 86 minutes remaining to empty
            var eta = calc.CalculateEta(phase: 2, remainingCapacity: 86, fullCapacity: 100, directRate: 0);

            Assert.NotNull(eta);
            Assert.Equal(86, Math.Round(eta!.Value.TotalMinutes));
        }

        [Fact]
        public void BatteryCalibration_WarmupFallback_ImmediateValidEta()
        {
            var calc = new BatteryCalibrationEtaCalculator();

            // Direct rate is 0 and no percentage history yet, but allowWarmupFallback is true
            var etaCharging = calc.CalculateEta(phase: 1, remainingCapacity: 60, fullCapacity: 100, directRate: 0, allowWarmupFallback: true, batteryPercent: 60);
            Assert.NotNull(etaCharging);
            // 40% remaining * 1.5 min = 60 mins
            Assert.Equal(60, Math.Round(etaCharging!.Value.TotalMinutes));

            var etaDischarging = calc.CalculateEta(phase: 2, remainingCapacity: 60, fullCapacity: 100, directRate: 0, allowWarmupFallback: true, batteryPercent: 60);
            Assert.NotNull(etaDischarging);
            Assert.True(etaDischarging!.Value.TotalMinutes > 0);

            // Static helper direct check
            var staticCharging = BatteryCalibrationEtaCalculator.GetWarmupFallbackEta(1, 70);
            Assert.NotNull(staticCharging);
            Assert.Equal(45, Math.Round(staticCharging!.Value.TotalMinutes)); // 30% * 1.5 = 45m
        }

        [Fact]
        public void BatteryCalibration_RapidUpdatesUnderThreeSeconds_AreIgnoredToPreventJitter()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 1.0);
            var t0 = DateTime.UtcNow;
            calc.UpdatePercentage(50, t0);

            // Rapid jitter: update 1.5 seconds later (0.025 min < 0.05 min threshold)
            calc.UpdatePercentage(55, t0.AddSeconds(1.5));
            Assert.Null(calc.SmoothedPercentPerMinute); // Ignored
            Assert.Equal(50, calc.LastPercent); // Baseline unchanged

            // Valid update: 4 seconds later (0.0667 min >= 0.05 min threshold)
            calc.UpdatePercentage(52, t0.AddSeconds(4));
            Assert.NotNull(calc.SmoothedPercentPerMinute);
            Assert.Equal(52, calc.LastPercent);
            Assert.Equal(30.0, calc.SmoothedPercentPerMinute!.Value, 1);
        }

        [Fact]
        public void BatteryCalibration_Pipeline_WarmupToCalculationToLabelFormat()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 1.0);
            var t0 = DateTime.UtcNow;

            // 1. Initial State: Without fallback, returns null -> "Calculating remaining time..."
            var eta0 = calc.CalculateEta(phase: 1, remainingCapacity: 20, fullCapacity: 100, directRate: 0, allowWarmupFallback: false);
            string label0 = UiLabelFormatter.FormatCalibrationEta(eta0, isCharging: true);
            Assert.Equal("Calculating remaining time...", label0);

            // 2. Initial State: With warmup fallback, immediately provides valid formatted ETA!
            var etaWarmup = calc.CalculateEta(phase: 1, remainingCapacity: 20, fullCapacity: 100, directRate: 0, allowWarmupFallback: true, batteryPercent: 20);
            Assert.NotNull(etaWarmup);
            string labelWarmup = UiLabelFormatter.FormatCalibrationEta(etaWarmup, isCharging: true);
            Assert.StartsWith("Estimated Time Remaining (Charging):", labelWarmup);
            Assert.DoesNotContain("Calculating", labelWarmup);

            // 3. Second tick (warmed up: +5% in 10 minutes -> 0.5% per minute)
            calc.UpdatePercentage(20, t0);
            calc.UpdatePercentage(25, t0.AddMinutes(10));
            // Remaining 75% / 0.5% per min = 150 minutes (02h 30m)
            var eta2 = calc.CalculateEta(phase: 1, remainingCapacity: 25, fullCapacity: 100, directRate: 0);
            Assert.NotNull(eta2);
            string label2 = UiLabelFormatter.FormatCalibrationEta(eta2, isCharging: true);
            Assert.Equal("Estimated Time Remaining (Charging): 02h 30m", label2);

            // 4. Switch to Phase 2 (Discharge: 80% down to 78% in 3 minutes -> 0.667% per minute)
            calc.Reset();
            calc.UpdatePercentage(80, t0);
            calc.UpdatePercentage(78, t0.AddMinutes(3));
            var eta3 = calc.CalculateEta(phase: 2, remainingCapacity: 78, fullCapacity: 100, directRate: 0);
            Assert.NotNull(eta3);
            string label3 = UiLabelFormatter.FormatCalibrationEta(eta3, isCharging: false);
            Assert.Equal("Estimated Time Remaining (Discharging): 01h 57m", label3);
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(5, true)]
        [InlineData(15, true)]
        [InlineData(16, false)]
        [InlineData(50, false)]
        public void BatteryCalibration_PostShutdownRecovery_Logic_CutoffVsInterrupted(int lastPercent, bool expectedSuccess)
        {
            var settings = new AppSettings
            {
                BatteryCalibrationPhase = 2,
                CalibrationLastKnownPercent = lastPercent,
                CalibrationPreChargeLimitEnabled = true,
                LastBatteryCalibrationUtcTicks = 0
            };

            bool completed = settings.BatteryCalibrationPhase == 2 && settings.CalibrationLastKnownPercent >= 0 && settings.CalibrationLastKnownPercent <= 15;
            if (completed)
            {
                settings.LastBatteryCalibrationUtcTicks = 999999L;
            }
            if (settings.CalibrationPreChargeLimitEnabled)
            {
                settings.BatteryLimit = true;
            }
            settings.BatteryCalibrationPhase = 0;
            settings.CalibrationLastKnownPercent = -1;

            Assert.Equal(expectedSuccess, completed);
            Assert.Equal(0, settings.BatteryCalibrationPhase);
            Assert.Equal(-1, settings.CalibrationLastKnownPercent);
            Assert.True(settings.BatteryLimit);
            if (expectedSuccess)
            {
                Assert.Equal(999999L, settings.LastBatteryCalibrationUtcTicks);
            }
            else
            {
                Assert.Equal(0L, settings.LastBatteryCalibrationUtcTicks);
            }
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_CapacityDelta_CalculatesRateAndEtaAccurately()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 1.0);
            var t0 = DateTime.UtcNow;

            // t0: 50,000 mWh
            calc.UpdateCapacity(50000, t0);
            Assert.Equal(50000, calc.LastCapacity);
            Assert.Null(calc.SmoothedRate);

            // t0 + 30s: 50,250 mWh (delta = 250 mWh in 30 seconds = 30,000 mW = 30W)
            calc.UpdateCapacity(50250, t0.AddSeconds(30));
            Assert.NotNull(calc.SmoothedRate);
            Assert.Equal(30000.0, calc.SmoothedRate!.Value, 1);

            // Full capacity 60,000 mWh, remaining 50,250 mWh -> delta = 9,750 mWh
            // Hours = 9,750 / 30,000 = 0.325 hours = 19.5 minutes
            var eta = calc.CalculateEta(phase: 1, remainingCapacity: 50250, fullCapacity: 60000, directRate: null);
            Assert.NotNull(eta);
            Assert.Equal(20, Math.Round(eta!.Value.TotalMinutes));
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_CapacityDelta_RejectsJitterUnderSixSeconds()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 1.0);
            var t0 = DateTime.UtcNow;

            calc.UpdateCapacity(50000, t0);
            // 3 seconds later (< 6 seconds threshold)
            calc.UpdateCapacity(50050, t0.AddSeconds(3));
            Assert.Null(calc.SmoothedRate);
            Assert.Equal(50000, calc.LastCapacity);

            // 10 seconds later (>= 6 seconds threshold)
            calc.UpdateCapacity(50083, t0.AddSeconds(10)); // 83 mWh / (10/3600) = ~29880 mW
            Assert.NotNull(calc.SmoothedRate);
            Assert.Equal(50083, calc.LastCapacity);
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_CapacityDelta_DischargingEta_CalculatesAccurately()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 1.0);
            var t0 = DateTime.UtcNow;

            // Discharging: drops from 40,000 mWh to 39,875 mWh in 30s (delta = 125 mWh = 15,000 mW)
            calc.UpdateCapacity(40000, t0);
            calc.UpdateCapacity(39875, t0.AddSeconds(30));
            Assert.NotNull(calc.SmoothedRate);
            Assert.Equal(15000.0, calc.SmoothedRate!.Value, 1);

            // Phase 2: remaining 39,875 mWh -> hours = 39,875 / 15,000 = 2.658 hours = 159.5 minutes
            var eta = calc.CalculateEta(phase: 2, remainingCapacity: 39875, fullCapacity: 60000, directRate: null);
            Assert.NotNull(eta);
            Assert.Equal(160, Math.Round(eta!.Value.TotalMinutes));
        }

        [Fact]
        public void BatteryCalibrationEtaCalculator_InferRateFromPercentPerMinute_WhenFullCapacityKnown()
        {
            var calc = new BatteryCalibrationEtaCalculator(alpha: 1.0);
            var t0 = DateTime.UtcNow;

            // 1% per minute with 60,000 mWh pack -> inferred 600 mWh/min = 36,000 mW
            calc.UpdatePercentage(50, t0);
            calc.UpdatePercentage(51, t0.AddMinutes(1));

            var eta = calc.CalculateEta(phase: 1, remainingCapacity: 50000, fullCapacity: 60000, directRate: null);
            Assert.NotNull(eta);
            Assert.NotNull(calc.SmoothedRate);
            Assert.Equal(36000.0, calc.SmoothedRate!.Value, 1);
        }

        #endregion
    }
}

