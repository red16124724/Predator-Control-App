using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
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
        private int _disposedState;

        private long _lastNitroSenseTick;
        private long _lastModeKeyTick;
        private const int DebounceThresholdMs = 250;

        public RawInputKeyWatcher()
        {
            CreateHandle(new CreateParams());
            RegisterDevices();
        }

        private void RegisterDevices()
        {
            RegisterSingleDevice(1, 6, RIDEV_INPUTSINK);       // Generic Desktop Keyboard
            RegisterSingleDevice(0x0C, 1, RIDEV_INPUTSINK);    // Consumer Control (Multimedia/chassis hotkeys)
            RegisterSingleDevice(136, 1, RIDEV_INPUTSINK);     // Acer Gaming Page
            RegisterSingleDevice(0xFF00, 1, RIDEV_INPUTSINK);  // Acer Vendor Defined Page
        }

        private void RegisterSingleDevice(ushort usagePage, ushort usage, uint flags)
        {
            try
            {
                var dev = new RAWINPUTDEVICE[]
                {
                    new()
                    {
                        usUsagePage = usagePage,
                        usUsage = usage,
                        dwFlags = flags,
                        hwndTarget = flags == RIDEV_REMOVE ? IntPtr.Zero : Handle
                    }
                };
                RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch { }
        }

        private void UnregisterDevices()
        {
            RegisterSingleDevice(1, 6, RIDEV_REMOVE);
            RegisterSingleDevice(0x0C, 1, RIDEV_REMOVE);
            RegisterSingleDevice(136, 1, RIDEV_REMOVE);
            RegisterSingleDevice(0xFF00, 1, RIDEV_REMOVE);
        }

        protected override void WndProc(ref Message m)
        {
            if (_isDisposed)
            {
                base.WndProc(ref m);
                return;
            }

            if (m.Msg == WM_INPUT)
            {
                ProcessRawInput(m.LParam);
            }
            base.WndProc(ref m);
        }

        private void FireNitroSenseKey()
        {
            if (_isDisposed) return;
            long now = Environment.TickCount64;
            if (now - _lastNitroSenseTick >= DebounceThresholdMs)
            {
                _lastNitroSenseTick = now;
                try { NitroSenseKeyPressed?.Invoke(); } catch { }
            }
        }

        private void FireModeKey()
        {
            if (_isDisposed) return;
            long now = Environment.TickCount64;
            if (now - _lastModeKeyTick >= DebounceThresholdMs)
            {
                _lastModeKeyTick = now;
                try { ModeKeyPressed?.Invoke(); } catch { }
            }
        }

        private void ProcessRawInput(IntPtr hRawInput)
        {
            if (_isDisposed) return;
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0 || size > 65536)
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
                        if ((kb.Flags & 1) == 0) // Key down
                        {
                            // MakeCode 117 (0x75) = Predator / NitroSense dedicated key
                            if (kb.MakeCode == 117)
                            {
                                FireNitroSenseKey();
                            }
                            else if (kb.MakeCode == 118 || kb.MakeCode == 119 || kb.MakeCode == 0x76 || kb.MakeCode == 0x77 || kb.VKey == 0x7C || (kb.VKey >= 0x82 && kb.VKey <= 0x87))
                            {
                                FireModeKey();
                            }
                        }
                    }
                    else if (header.dwType == 2) // RIM_TYPEHID
                    {
                        var hid = Marshal.PtrToStructure<RAWHID>(pRaw);
                        int hidDataOffset = Marshal.OffsetOf<RAWHID>("bRawData").ToInt32();
                        int maxPayload = (int)((long)size - headerSize - hidDataOffset);
                        if (maxPayload < 2) return;
                        int dataLen = Math.Min((int)hid.dwSizeHid, maxPayload);
                        if (dataLen >= 2)
                        {
                            byte[] rawBytes = new byte[dataLen];
                            Marshal.Copy((IntPtr)((long)pRaw + hidDataOffset), rawBytes, 0, dataLen);
                            if (dataLen >= 3 && rawBytes[1] == 129 && rawBytes[2] == 0xFF)
                            {
                                FireNitroSenseKey();
                            }
                            else if (rawBytes[0] == 7 || rawBytes[0] == 0x82 || rawBytes[0] == 0x86 || rawBytes[0] == 0x87 ||
                                     rawBytes[1] == 7 || rawBytes[1] == 130 || rawBytes[1] == 0x82 || rawBytes[1] == 0x86 || rawBytes[1] == 0x87 ||
                                     (dataLen >= 3 && rawBytes[1] == 129 && (rawBytes[2] == 0x07 || rawBytes[2] == 0x82 || rawBytes[2] == 0x86 || rawBytes[2] == 0x87)))
                            {
                                FireModeKey();
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
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposedState, 1) != 0) return;
            _isDisposed = true;
            try { UnregisterDevices(); } catch { }
            try { DestroyHandle(); } catch { }
        }

        ~RawInputKeyWatcher()
        {
            Dispose(false);
        }
    }
}
