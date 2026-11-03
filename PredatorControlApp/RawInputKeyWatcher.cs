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
        private const uint RIDEV_NOLEGACY = 0x00000030; // Strictly forbidden: never pass RIDEV_NOLEGACY to preserve BIOS Fn keys & Media keys

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
            try
            {
                AppDomain.CurrentDomain.ProcessExit += OnProcessExitOrCrash;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                Application.ApplicationExit += OnApplicationExit;
            }
            catch { }
        }

        private void OnProcessExitOrCrash(object? sender, EventArgs e) => Dispose(true);
        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) => Dispose(true);
        private void OnApplicationExit(object? sender, EventArgs e) => Dispose(true);

        private void RegisterDevices()
        {
            // Only listen to dedicated Acer hardware vendor pages.
            // Do NOT register generic keyboard (1, 6) or Consumer Control (0x0C, 1)
            // so standard keyboard inputs, Fn keys, and media controls are never intercepted.
            RegisterSingleDevice(136, 1, RIDEV_INPUTSINK);     // Acer Gaming Page
            RegisterSingleDevice(0xFF00, 1, RIDEV_INPUTSINK);  // Acer Vendor Defined Page
            RegisterSingleDevice(0xFF05, 1, RIDEV_INPUTSINK);  // Acer EC / Gaming HID Page
            RegisterSingleDevice(0xFFA0, 1, RIDEV_INPUTSINK);  // Acer Launch Manager / NitroSense Page
        }

        private void RegisterSingleDevice(ushort usagePage, ushort usage, uint flags)
        {
            // Standard keyboard (1, 6) and consumer controls (0x0C, 1) must never be registered, and RIDEV_NOLEGACY is banned
            if ((flags & RIDEV_NOLEGACY) != 0 || (usagePage == 1 && usage == 6) || usagePage == 0x0C)
                return;
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
            RegisterSingleDevice(136, 1, RIDEV_REMOVE);
            RegisterSingleDevice(0xFF00, 1, RIDEV_REMOVE);
            RegisterSingleDevice(0xFF05, 1, RIDEV_REMOVE);
            RegisterSingleDevice(0xFFA0, 1, RIDEV_REMOVE);
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
                var handler = NitroSenseKeyPressed;
                if (handler != null)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(static h =>
                    {
                        try { h(); } catch { }
                    }, handler, preferLocal: true);
                }
            }
        }

        private void FireModeKey()
        {
            if (_isDisposed) return;
            long now = Environment.TickCount64;
            if (now - _lastModeKeyTick >= DebounceThresholdMs)
            {
                _lastModeKeyTick = now;
                var handler = ModeKeyPressed;
                if (handler != null)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(static h =>
                    {
                        try { h(); } catch { }
                    }, handler, preferLocal: true);
                }
            }
        }

        private void ProcessRawInput(IntPtr hRawInput)
        {
            if (_isDisposed) return;
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size < headerSize || size > 65536)
                return;

            IntPtr pData = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRawInput, RID_INPUT, pData, ref size, headerSize) == size)
                {
                    var header = Marshal.PtrToStructure<RAWINPUTHEADER>(pData);
                    IntPtr pRaw = (IntPtr)((long)pData + headerSize);

                    if (header.dwType == 2) // RIM_TYPEHID
                    {
                        int hidDataOffset = Marshal.OffsetOf<RAWHID>("bRawData").ToInt32();
                        if ((long)size < headerSize + hidDataOffset) return;
                        var hid = Marshal.PtrToStructure<RAWHID>(pRaw);
                        int maxPayload = (int)((long)size - headerSize - hidDataOffset);
                        if (maxPayload < 2) return;
                        int totalBytes = (int)Math.Min((long)hid.dwSizeHid * Math.Max(1L, (long)hid.dwCount), (long)maxPayload);
                        if (totalBytes >= 2)
                        {
                            byte[] rawBytes = new byte[totalBytes];
                            Marshal.Copy((IntPtr)((long)pRaw + hidDataOffset), rawBytes, 0, totalBytes);

                            // Strict Acer vendor packet signature: byte == 129 (0x81)
                            // Handles both 0-indexed (no report ID) and 1-indexed (report ID prefix) payloads
                            bool isNitroKey = (rawBytes[0] == 0x81 && (rawBytes[1] == 0xFF || rawBytes[1] == 0x05 || rawBytes[1] == 0x01)) ||
                                              (totalBytes >= 3 && rawBytes[1] == 0x81 && (rawBytes[2] == 0xFF || rawBytes[2] == 0x05 || rawBytes[2] == 0x01));

                            bool isModeKey = (rawBytes[0] == 0x81 && (rawBytes[1] == 0x82 || rawBytes[1] == 0x86 || rawBytes[1] == 0x87)) ||
                                             (totalBytes >= 3 && rawBytes[1] == 0x81 && (rawBytes[2] == 0x82 || rawBytes[2] == 0x86 || rawBytes[2] == 0x87));

                            if (isNitroKey)
                            {
                                FireNitroSenseKey();
                            }
                            else if (isModeKey)
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
            NitroSenseKeyPressed = null;
            ModeKeyPressed = null;
            try
            {
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExitOrCrash;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                Application.ApplicationExit -= OnApplicationExit;
            }
            catch { }
            try { UnregisterDevices(); } catch { }
            if (disposing)
            {
                try { DestroyHandle(); } catch { }
            }
        }

        ~RawInputKeyWatcher()
        {
            Dispose(false);
        }
    }
}
