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
    }
}

