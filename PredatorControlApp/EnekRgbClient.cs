using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PredatorControlApp
{
    /// <summary>
    /// Direct Win32 HID client for the ENEK5130 RGB Keyboard and Lid Logo controller (HID\VID_...&amp;ENEK5130).
    /// Used in modern Acer Predator and Nitro laptops (e.g. Helios Neo 16 PHN16-71/72, Helios Neo 18 PHN18-71,
    /// Helios Neo 16S PHN16S-71, Macan_ARX) to provide 100% standalone, kernel-level 4-zone RGB,
    /// hardware animation control, and lid logo turn-off without relying on AcerService or ACPI-WMI.
    /// 
    /// On ENEK5130 hardware, ACPI-WMI calls cause bus collisions and trigger periodic EC resets
    /// that make keyboard RGB blink and fail to set properly. This driver speaks pure HID 0xA4 reports.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class EnekRgbClient : IDisposable
    {
        public const byte ReportId = 0xA4;
        public const byte TargetKeyboard = 0x21;
        public const byte TargetCoverLogo = 0x83;
        public const int ExpectedReportLength = 11;

        // ENEK5130 hardware effect IDs (confirmed reverse-engineered)
        public const byte EffectStatic = 0x02;
        public const byte EffectBreathing = 0x04;
        public const byte EffectNeon = 0x05;
        public const byte EffectWave = 0x07;
        public const byte EffectZoom = 0x09;
        public const byte EffectMeteor = 0x0A;
        public const byte EffectTwinkling = 0x0B;

        public static readonly byte[] ZoneMasks = { 0x01, 0x02, 0x04, 0x08 };

        private readonly object _lock = new();
        private SafeFileHandle? _deviceHandle;
        private bool _isDisposed;

        /// <summary>Test hook: intercept feature reports sent to hardware.</summary>
        public Func<byte[], bool>? FeatureReportInterceptor { get; set; }

        /// <summary>Test hook: override device availability detection.</summary>
        public Func<bool>? DeviceAvailabilityChecker { get; set; }

        public bool IsAvailable
        {
            get
            {
                lock (_lock)
                {
                    if (_isDisposed) return false;
                    if (DeviceAvailabilityChecker != null)
                        return DeviceAvailabilityChecker();
                    return EnsureDeviceOpen();
                }
            }
        }

        public EnekRgbClient()
        {
        }

        #region Device Connection Management

        private bool EnsureDeviceOpen()
        {
            if (_isDisposed) return false;
            if (PredatorNativeCore.IsNativeCoreAvailable && PredatorNativeCore.EnekIsOpen())
                return true;
            if (_deviceHandle != null && !_deviceHandle.IsInvalid && !_deviceHandle.IsClosed)
                return true;

            if (PredatorNativeCore.IsNativeCoreAvailable && PredatorNativeCore.EnekOpen())
                return true;

            try
            {
                string? path = FindEnekDevicePath();
                if (string.IsNullOrEmpty(path))
                    return false;

                var handle = CreateFile(
                    path,
                    0xC0000000, // GENERIC_READ | GENERIC_WRITE
                    3,          // FILE_SHARE_READ | FILE_SHARE_WRITE
                    IntPtr.Zero,
                    3,          // OPEN_EXISTING
                    0,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    // Fall back to write-only access if read access is contested
                    handle.Dispose();
                    handle = CreateFile(path, 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // GENERIC_WRITE
                }

                if (!handle.IsInvalid)
                {
                    _deviceHandle = handle;
                    return true;
                }

                handle.Dispose();
            }
            catch (Exception ex)
            {
                Program.Report(ex, false);
            }

            return false;
        }

        private void CloseHandle()
        {
            try
            {
                if (PredatorNativeCore.IsNativeCoreAvailable)
                {
                    PredatorNativeCore.EnekClose();
                }
                _deviceHandle?.Dispose();
            }
            catch { }
            _deviceHandle = null;
        }

        #endregion

        #region Public RGB Operations

        /// <summary>
        /// Sets the 4 individual keyboard zone colors (100% standalone via direct HID feature report).
        /// </summary>
        public bool SetZoneColors(Color[] zoneColors, byte brightness = 100)
        {
            if (zoneColors == null || zoneColors.Length < 4) return false;

            lock (_lock)
            {
                if (FeatureReportInterceptor == null && (!EnsureDeviceOpen() || _deviceHandle == null))
                    return false;

                byte b = Math.Clamp(brightness, (byte)0, (byte)100);

                // If brightness is 0, turn off all zones immediately
                if (b == 0)
                {
                    return TurnOffBacklight();
                }

                if (FeatureReportInterceptor == null && PredatorNativeCore.IsNativeCoreAvailable && (PredatorNativeCore.EnekIsOpen() || PredatorNativeCore.EnekOpen()))
                {
                    if (PredatorNativeCore.EnekSetZones(
                        zoneColors[0].R, zoneColors[0].G, zoneColors[0].B,
                        zoneColors[1].R, zoneColors[1].G, zoneColors[1].B,
                        zoneColors[2].R, zoneColors[2].G, zoneColors[2].B,
                        zoneColors[3].R, zoneColors[3].G, zoneColors[3].B,
                        b))
                    {
                        return true;
                    }
                }

                // Dispatch 4 per-zone feature reports using confirmed masks 0x01, 0x02, 0x04, 0x08
                bool allSucceeded = true;
                for (int i = 0; i < 4; i++)
                {
                    bool ok = SendFeatureReport(EffectStatic, b, 0, 0, zoneColors[i].R, zoneColors[i].G, zoneColors[i].B, ZoneMasks[i]);
                    if (!ok) allSucceeded = false;
                }

                return allSucceeded;
            }
        }

        /// <summary>
        /// Sets a native hardware-animated lighting effect on the keyboard (Breathing, Neon, Wave, Shifting, Zoom, Meteor, Twinkling).
        /// </summary>
        public bool SetDynamicEffect(int mode, Color color, byte brightness, byte speed, byte direction)
        {
            lock (_lock)
            {
                if (FeatureReportInterceptor == null && (!EnsureDeviceOpen() || _deviceHandle == null))
                    return false;

                byte b = Math.Clamp(brightness, (byte)0, (byte)100);
                if (b == 0 || mode == 0)
                {
                    if (mode == 0 && b > 0)
                    {
                        var solid = new Color[] { color, color, color, color };
                        return SetZoneColors(solid, b);
                    }
                    return TurnOffBacklight();
                }

                // Map mode index (0..7) to ENEK5130 hardware effect opcode
                byte effectOpcode = mode switch
                {
                    1 => EffectBreathing, // 0x04
                    2 => EffectNeon,      // 0x05
                    3 => EffectWave,      // 0x07
                    4 => EffectWave,      // 0x07 (Shifting uses wave opcode with direction)
                    5 => EffectZoom,      // 0x09
                    6 => EffectMeteor,    // 0x0A (Snake/Meteor)
                    7 => EffectTwinkling, // 0x0B
                    _ => EffectWave
                };

                byte hidSpeed = (byte)Math.Clamp((int)speed, 1, 10);
                byte hidDir = (byte)(direction != 0 ? 1 : 0);

                return SendFeatureReport(effectOpcode, b, hidSpeed, hidDir, color.R, color.G, color.B, 0x0F);
            }
        }

        /// <summary>
        /// Instantly disables keyboard backlight by sending zero brightness to all zones.
        /// </summary>
        public bool TurnOffBacklight()
        {
            lock (_lock)
            {
                if (FeatureReportInterceptor == null && (!EnsureDeviceOpen() || _deviceHandle == null))
                    return false;
                return SendFeatureReport(TargetKeyboard, EffectStatic, 0, 0, 0, 0, 0, 0, 0x0F);
            }
        }

        /// <summary>
        /// Completely and permanently turns off the laptop lid logo RGB at all times.
        /// Sends TargetCoverLogo (0x83) with brightness 0 and effect 0x01.
        /// </summary>
        public bool TurnOffLogo()
        {
            lock (_lock)
            {
                if (FeatureReportInterceptor == null && (!EnsureDeviceOpen() || _deviceHandle == null))
                    return false;
                return SendFeatureReport(TargetCoverLogo, 0x01, 0, 0, 0, 0, 0, 0, 0x01);
            }
        }

        /// <summary>
        /// Lid cover logo on ENEK5130 target 0x83.
        /// When mode is 0 or brightness is 0, completely shuts off the logo.
        /// </summary>
        public bool SetLogoLighting(int mode, Color color, byte brightness, byte speed)
        {
            lock (_lock)
            {
                if (FeatureReportInterceptor == null && (!EnsureDeviceOpen() || _deviceHandle == null))
                    return false;

                byte b = Math.Clamp(brightness, (byte)0, (byte)100);
                if (b == 0 || mode == 0)
                {
                    return SendFeatureReport(TargetCoverLogo, 0x01, 0, 0, 0, 0, 0, 0, 0x01);
                }

                byte effect = mode switch
                {
                    2 => EffectBreathing,
                    3 => EffectNeon,
                    _ => EffectStatic
                };
                byte hidSpeed = (byte)Math.Clamp((int)speed, 1, 5);
                return SendFeatureReport(TargetCoverLogo, effect, b, hidSpeed, 0, color.R, color.G, color.B, 0x01);
            }
        }

        private bool SendFeatureReport(byte effect, byte brightness, byte speed, byte direction, byte r, byte g, byte b, byte zoneMask)
            => SendFeatureReport(TargetKeyboard, effect, brightness, speed, direction, r, g, b, zoneMask);

        public bool SendFeatureReport(byte target, byte effect, byte brightness, byte speed, byte direction, byte r, byte g, byte b, byte zoneMask)
        {
            byte[] packet = new byte[ExpectedReportLength];
            packet[0] = ReportId;
            packet[1] = target;
            packet[2] = effect;
            packet[3] = brightness;
            packet[4] = speed;
            packet[5] = direction;
            packet[6] = r;
            packet[7] = g;
            packet[8] = b;
            packet[9] = zoneMask;
            packet[10] = 0x00;

            if (FeatureReportInterceptor != null)
            {
                return FeatureReportInterceptor(packet);
            }

            if (PredatorNativeCore.IsNativeCoreAvailable && (PredatorNativeCore.EnekIsOpen() || PredatorNativeCore.EnekOpen()))
            {
                if (PredatorNativeCore.EnekSendReport(packet))
                {
                    return true;
                }
            }

            if (_deviceHandle == null || _deviceHandle.IsInvalid || _deviceHandle.IsClosed)
            {
                if (!EnsureDeviceOpen() || _deviceHandle == null || _deviceHandle.IsInvalid)
                    return false;
            }

            try
            {
                bool success = HidD_SetFeature(_deviceHandle, packet, packet.Length);
                if (!success)
                {
                    CloseHandle();
                }
                return success;
            }
            catch (Exception ex)
            {
                Program.Report(ex, false);
                CloseHandle();
                return false;
            }
        }

        #endregion

        #region Win32 Device Enumeration & P/Invoke

        public static List<string> EnumerateAllHidPaths()
        {
            var result = new List<string>();
            try
            {
                HidD_GetHidGuid(out Guid hidGuid);
                IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
                if (hDevInfo == (IntPtr)(-1)) return result;

                try
                {
                    var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    uint index = 0;
                    while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, index++, ref did))
                    {
                        SetupDiGetDeviceInterfaceDetail(hDevInfo, ref did, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                        if (reqSize <= 4) continue;

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
                                    result.Add(path);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pDetail);
                        }
                    }
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(hDevInfo);
                }
            }
            catch (Exception ex)
            {
                Program.Report(ex, false);
            }
            return result;
        }

        public static string? FindEnekDevicePath()
        {
            return EnumerateAllHidPaths().FirstOrDefault(p => p.Contains("ENEK5130", StringComparison.OrdinalIgnoreCase));
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

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

        #endregion

        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                CloseHandle();
            }
        }
    }
}
