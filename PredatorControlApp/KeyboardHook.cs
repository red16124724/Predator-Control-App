using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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

        private readonly Action _onPredatorSensePressed;
        private readonly Action<int>? _onPredatorNumberPressed;
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private bool _isDisposed;

        private bool _isPredatorKeyPressed;
        private bool _predatorUsedInCombo;
        private bool _suppressPredatorReleaseAction;
        private int? _activePredatorNumber;

        public KeyboardHook(Action onPredatorSensePressed, Action<int>? onPredatorNumberPressed = null)
        {
            _onPredatorSensePressed = onPredatorSensePressed ?? throw new ArgumentNullException(nameof(onPredatorSensePressed));
            _onPredatorNumberPressed = onPredatorNumberPressed;
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
            if (nCode >= 0)
            {
                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                if (isKeyDown || isKeyUp)
                {
                    var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

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
                                _onPredatorSensePressed();
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
                                _onPredatorNumberPressed(number);
                            }
                        }
                        else if (isKeyUp && _activePredatorNumber == number)
                        {
                            _activePredatorNumber = null;
                        }
                        return (IntPtr)1;
                    }

                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
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
        }
    }
}
