using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint SC_PREDATOR = 0x75;
        private const uint SC_MODE = 0x76;
        private const uint SC_MODE_ALT = 0x77;

        // LLKHF Flags
        private const uint LLKHF_EXTENDED = 0x01;
        private const uint LLKHF_LOWER_IL_INJECTED = 0x02;
        private const uint LLKHF_INJECTED = 0x10;
        private const uint LLKHF_ALTDOWN = 0x20;
        private const uint LLKHF_UP = 0x80;

        private readonly Action _onPredatorSensePressed;
        private readonly Action<int>? _onPredatorNumberPressed;
        private readonly Action? _onModeKeyPressed;
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private bool _isDisposed;

        public event Action? ModeKeyPressed;

        private bool _isPredatorKeyPressed;
        private bool _predatorUsedInCombo;
        private bool _suppressPredatorReleaseAction;
        private int? _activePredatorNumber;
        private long _lastModeKeyTick;

        /// <summary>
        /// When false (default), synthetic or injected keystrokes (LLKHF_INJECTED or LLKHF_LOWER_IL_INJECTED)
        /// are rejected to protect against unprivileged software spoofing.
        /// </summary>
        public bool AllowInjectedKeys { get; set; } = false;

        public KeyboardHook(Action onPredatorSensePressed, Action<int>? onPredatorNumberPressed = null, Action? onModeKeyPressed = null)
        {
            _onPredatorSensePressed = onPredatorSensePressed ?? throw new ArgumentNullException(nameof(onPredatorSensePressed));
            _onPredatorNumberPressed = onPredatorNumberPressed;
            _onModeKeyPressed = onModeKeyPressed;
            _proc = HookCallback;
            _hookId = SetHook(_proc);
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                if (curModule != null && curModule.ModuleName != null)
                {
                    IntPtr moduleHandle = GetModuleHandle(curModule.ModuleName);
                    IntPtr hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, moduleHandle, 0);
                    if (hook != IntPtr.Zero)
                        return hook;
                }
            }
            catch { }
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, IntPtr.Zero, 0);
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || lParam == IntPtr.Zero)
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            try
            {
                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                if (isKeyDown || isKeyUp)
                {
                    var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                    // Check LLKHF_INJECTED and LLKHF_LOWER_IL_INJECTED to prevent synthetic spoofing
                    bool isInjected = (hookStruct.flags & LLKHF_INJECTED) != 0 ||
                                      (hookStruct.flags & LLKHF_LOWER_IL_INJECTED) != 0;

                    if (isInjected && !AllowInjectedKeys)
                    {
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    bool isModeKey = hookStruct.scanCode == SC_MODE || hookStruct.scanCode == SC_MODE_ALT ||
                                     (hookStruct.vkCode >= 0x82 && hookStruct.vkCode <= 0x87) || // VK_F19..VK_F24
                                     hookStruct.vkCode == 0x7C; // VK_F13
                    if (isModeKey)
                    {
                        if (isKeyDown)
                        {
                            long now = Environment.TickCount64;
                            if (now - _lastModeKeyTick < 250)
                            {
                                return (IntPtr)1; // Debounced rapid spamming
                            }
                            _lastModeKeyTick = now;

                            // Asynchronous dispatch: never execute arbitrary delegate code synchronously in LL hook thread!
                            DispatchAsync(() =>
                            {
                                if (_onModeKeyPressed != null)
                                    _onModeKeyPressed.Invoke();
                                else
                                    ModeKeyPressed?.Invoke();
                            });
                        }
                        return (IntPtr)1; // Consume key
                    }

                    bool isPredatorKey = hookStruct.scanCode == SC_PREDATOR;

                    if (isPredatorKey)
                    {
                        if (isKeyDown)
                        {
                            if (!_isPredatorKeyPressed)
                            {
                                _isPredatorKeyPressed = true;
                                _predatorUsedInCombo = false;
                            }
                        }
                        else if (isKeyUp)
                        {
                            if (!_predatorUsedInCombo && !_suppressPredatorReleaseAction)
                            {
                                DispatchAsync(() => _onPredatorSensePressed());
                            }
                            _isPredatorKeyPressed = false;
                            _predatorUsedInCombo = false;
                            _suppressPredatorReleaseAction = false;
                            _activePredatorNumber = null;
                        }
                        return (IntPtr)1; // Consume key
                    }

                    if (isKeyUp && _isPredatorKeyPressed && hookStruct.scanCode == SC_PREDATOR)
                    {
                        _isPredatorKeyPressed = false;
                        _predatorUsedInCombo = false;
                        _suppressPredatorReleaseAction = false;
                        _activePredatorNumber = null;
                        return (IntPtr)1;
                    }

                    // Predator key + number row (1-5) or numpad (1-5) selects mode directly
                    if (_isPredatorKeyPressed && _onPredatorNumberPressed != null &&
                        ((hookStruct.vkCode >= 0x31 && hookStruct.vkCode <= 0x35) ||
                         (hookStruct.vkCode >= 0x61 && hookStruct.vkCode <= 0x65)))
                    {
                        int number = hookStruct.vkCode >= 0x61
                            ? (int)(hookStruct.vkCode - 0x60)
                            : (int)(hookStruct.vkCode - 0x30);

                        if (isKeyDown)
                        {
                            if (_activePredatorNumber != number)
                            {
                                _predatorUsedInCombo = true;
                                _suppressPredatorReleaseAction = true;
                                _activePredatorNumber = number;
                                int capturedNumber = number;
                                DispatchAsync(() => _onPredatorNumberPressed(capturedNumber));
                            }
                        }
                        else if (isKeyUp && _activePredatorNumber == number)
                        {
                            _activePredatorNumber = null;
                        }
                        return (IntPtr)1; // Consume key
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KeyboardHook callback exception: {ex.Message}");
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static void DispatchAsync(Action action)
        {
            if (action == null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"KeyboardHook async callback failure: {ex.Message}");
                }
            });
        }

        #region Win32 API Imports

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        #endregion

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            GC.KeepAlive(_proc);
        }
    }
}
