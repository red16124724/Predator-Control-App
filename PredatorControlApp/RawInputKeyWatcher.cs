using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public sealed class RawInputKeyWatcher : NativeWindow, IDisposable
    {
        private const int WM_INPUT = 0x00FF;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_REMOVE = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWHID
        {
            public uint dwSizeHid;
            public uint dwCount;
            public byte bRawData;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        public event Action? NitroSenseKeyPressed;
        public event Action? ModeKeyPressed;
        private bool _isDisposed;

        public RawInputKeyWatcher()
        {
            CreateHandle(new CreateParams());
            RegisterDevices();
        }

        private void RegisterDevices()
        {
            try
            {
                var devices = new RAWINPUTDEVICE[2]
                {
                    new()
                    {
                        usUsagePage = 1, // Generic Desktop
                        usUsage = 6,     // Keyboard
                        dwFlags = RIDEV_INPUTSINK,
                        hwndTarget = Handle
                    },
                    new()
                    {
                        usUsagePage = 136, // Acer Gaming Page
                        usUsage = 1,
                        dwFlags = RIDEV_INPUTSINK,
                        hwndTarget = Handle
                    }
                };

                RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch { }
        }

        private void UnregisterDevices()
        {
            try
            {
                var devices = new RAWINPUTDEVICE[2]
                {
                    new()
                    {
                        usUsagePage = 1,
                        usUsage = 6,
                        dwFlags = RIDEV_REMOVE,
                        hwndTarget = IntPtr.Zero
                    },
                    new()
                    {
                        usUsagePage = 136,
                        usUsage = 1,
                        dwFlags = RIDEV_REMOVE,
                        hwndTarget = IntPtr.Zero
                    }
                };

                RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                ProcessRawInput(m.LParam);
            }
            base.WndProc(ref m);
        }

        private void ProcessRawInput(IntPtr hRawInput)
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
                return;

            IntPtr pData = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRawInput, RID_INPUT, pData, ref size, headerSize) == size)
                {
                    var header = Marshal.PtrToStructure<RAWINPUTHEADER>(pData);
                    IntPtr pRaw = (IntPtr)((long)pData + headerSize);

                    if (header.dwType == 1) // RIM_TYPEKEYBOARD
                    {
                        var kb = Marshal.PtrToStructure<RAWKEYBOARD>(pRaw);
                        // MakeCode 117 (0x75) = Predator / NitroSense dedicated key
                        if (kb.MakeCode == 117 && (kb.Flags & 2) != 0 && (kb.Flags & 1) == 0)
                        {
                            NitroSenseKeyPressed?.Invoke();
                        }
                        else if ((kb.MakeCode == 118 || kb.MakeCode == 119 || kb.VKey == 0x86 || kb.VKey == 0x87) && (kb.Flags & 1) == 0)
                        {
                            ModeKeyPressed?.Invoke();
                        }
                    }
                    else if (header.dwType == 2) // RIM_TYPEHID
                    {
                        var hid = Marshal.PtrToStructure<RAWHID>(pRaw);
                        int hidDataOffset = Marshal.OffsetOf<RAWHID>("bRawData").ToInt32();
                        int dataLen = (int)Math.Min(hid.dwSizeHid, size - headerSize - (uint)hidDataOffset);
                        if (dataLen >= 3)
                        {
                            byte[] rawBytes = new byte[dataLen];
                            Marshal.Copy((IntPtr)((long)pRaw + hidDataOffset), rawBytes, 0, dataLen);
                            if (rawBytes[1] == 129 && rawBytes[2] == 0xFF)
                            {
                                NitroSenseKeyPressed?.Invoke();
                            }
                            else if (rawBytes[1] == 7 || rawBytes[1] == 130 || rawBytes[1] == 0x82 || (rawBytes[1] == 129 && rawBytes[2] == 0x07))
                            {
                                ModeKeyPressed?.Invoke();
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(pData);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            UnregisterDevices();
            DestroyHandle();
        }
    }
}
