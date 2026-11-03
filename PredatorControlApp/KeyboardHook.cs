using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

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

        // Dedicated Acer Hardware Scancodes:
        // Notice: On PC keyboards, VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77.
        // Therefore, scancode matching alone must NEVER be used without validating that the
        // Virtual Key code is NOT a standard/extended function key!
        private const uint SC_PREDATOR = 0x75;
        private const uint SC_MODE = 0x76;
        private const uint SC_MODE_ALT = 0x77;

        // LLKHF Flags
        private const uint LLKHF_LOWER_IL_INJECTED = 0x02;
        private const uint LLKHF_INJECTED = 0x10;

        private readonly Action _onPredatorSensePressed;
        private readonly Action<int>? _onPredatorNumberPressed;
        private readonly Action? _onModeKeyPressed;
        private Action<byte>? _onCustomModeShortcutTriggered;
        private readonly LowLevelKeyboardProc _proc;
        private readonly object _hookLock = new();
        private IntPtr _hookId = IntPtr.Zero;
        private bool _isDisposed;

        public event Action? ModeKeyPressed;

        private volatile bool _isPredatorKeyPressed;
        private volatile bool _predatorUsedInCombo;
        private volatile bool _suppressPredatorReleaseAction;
        private int? _activePredatorNumber;
        private volatile uint _lastTriggeredShortcutVk;
        private long _lastShortcutTriggerTick;
        private long _lastModeKeyTick;
        private long _lastPredatorSenseTick;
        private long _predatorKeyPressedTick;
        private const long PredatorKeyHoldTimeoutMs = 3000;

        // Modifier state tracking (thread-safe volatile + physical state query fallback)
        private volatile bool _isCtrlPressed;
        private volatile bool _isAltPressed;
        private volatile bool _isShiftPressed;
        private volatile bool _isWinPressed;

        public bool IsCtrlPressed => _isCtrlPressed || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        public bool IsAltPressed => _isAltPressed || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        public bool IsShiftPressed => _isShiftPressed || (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        public bool IsWinPressed => _isWinPressed || (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

        // Lock-free snapshot array for low-level hook execution speed
        private CustomModeShortcut[] _customShortcuts = Array.Empty<CustomModeShortcut>();

        // Watchdog & desktop transition resilience
        private readonly System.Threading.Timer? _watchdogTimer;
        private volatile bool _lastDesktopAccessible = true;

        /// <summary>
        /// When false (default), synthetic or injected keystrokes (LLKHF_INJECTED or LLKHF_LOWER_IL_INJECTED)
        /// are rejected to protect against unprivileged software spoofing.
        /// </summary>
        private bool _allowInjectedKeys;

        public bool AllowInjectedKeys
        {
            get => _allowInjectedKeys;
            set
            {
                _allowInjectedKeys = value;
                PredatorNativeMigrations.HookSetAllowInjected(value);
            }
        }

        public KeyboardHook(
            Action onPredatorSensePressed,
            Action<int>? onPredatorNumberPressed = null,
            Action? onModeKeyPressed = null,
            Action<byte>? onCustomModeShortcutTriggered = null)
        {
            _onPredatorSensePressed = onPredatorSensePressed ?? throw new ArgumentNullException(nameof(onPredatorSensePressed));
            _onPredatorNumberPressed = onPredatorNumberPressed;
            _onModeKeyPressed = onModeKeyPressed;
            _onCustomModeShortcutTriggered = onCustomModeShortcutTriggered;
            _proc = HookCallback;
            _hookId = SetHook(_proc);

            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExitOrCrash;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                Application.ApplicationExit += OnApplicationExit;
            }
            catch { }

            try
            {
                _watchdogTimer = new System.Threading.Timer(WatchdogCallback, null, 2500, 2500);
            }
            catch { }
        }

        public void SetCustomShortcuts(IEnumerable<CustomModeShortcut>? shortcuts)
        {
            var list = shortcuts != null
                ? shortcuts.Where(s => s != null && s.Enabled && s.Key != Keys.None).Select(s => s.Clone()).ToArray()
                : Array.Empty<CustomModeShortcut>();
            Volatile.Write(ref _customShortcuts, list);

            // Mirror the table into the native decision engine.
            var nativeEntries = new NativeShortcutEntry[list.Length];
            for (int i = 0; i < list.Length; i++)
            {
                var s = list[i];
                nativeEntries[i] = new NativeShortcutEntry
                {
                    Vk = (uint)(s.Key & Keys.KeyCode),
                    ModeCode = s.ModeCode,
                    Ctrl = s.Ctrl ? 1u : 0u,
                    Alt = s.Alt ? 1u : 0u,
                    Shift = s.Shift ? 1u : 0u,
                    Win = s.Win ? 1u : 0u,
                    Enabled = s.Enabled ? 1u : 0u
                };
            }
            PredatorNativeMigrations.HookSetShortcuts(nativeEntries);
        }

        public void SetCustomShortcutTrigger(Action<byte>? callback)
        {
            _onCustomModeShortcutTriggered = callback;
        }

        public void ResetModifierStates()
        {
            _isCtrlPressed = false;
            _isAltPressed = false;
            _isShiftPressed = false;
            _isWinPressed = false;
            _isPredatorKeyPressed = false;
            _predatorUsedInCombo = false;
            _suppressPredatorReleaseAction = false;
            _activePredatorNumber = null;
            _lastTriggeredShortcutVk = 0;
            PredatorNativeMigrations.HookReset();
        }

        public bool EnsureHookInstalled()
        {
            if (_isDisposed) return false;
            lock (_hookLock)
            {
                if (_hookId != IntPtr.Zero) return true;
                _hookId = SetHook(_proc);
                return _hookId != IntPtr.Zero;
            }
        }

        public void Rehook()
        {
            if (_isDisposed) return;
            lock (_hookLock)
            {
                try
                {
                    IntPtr old = Interlocked.Exchange(ref _hookId, IntPtr.Zero);
                    if (old != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(old);
                    }
                }
                catch { }

                try
                {
                    _hookId = SetHook(_proc);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"KeyboardHook Rehook failure: {ex.Message}");
                }
            }
        }

        private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            if (e.Reason is SessionSwitchReason.SessionUnlock or
                           SessionSwitchReason.SessionLogon or
                           SessionSwitchReason.ConsoleConnect or
                           SessionSwitchReason.RemoteConnect)
            {
                ResetModifierStates();
                Rehook();
            }
            else if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
            {
                ResetModifierStates();
            }
        }

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                ResetModifierStates();
                Rehook();
            }
        }

        private void OnProcessExitOrCrash(object? sender, EventArgs e)
        {
            UninstallHookDirect();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            UninstallHookDirect();
        }

        private void OnApplicationExit(object? sender, EventArgs e)
        {
            UninstallHookDirect();
        }

        private void UninstallHookDirect()
        {
            try
            {
                IntPtr hook = Interlocked.Exchange(ref _hookId, IntPtr.Zero);
                if (hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(hook);
                }
            }
            catch { }
        }

        private void WatchdogCallback(object? state)
        {
            if (_isDisposed) return;

            try
            {
                IntPtr hDesk = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_SWITCHDESKTOP */);
                bool isAccessible = hDesk != IntPtr.Zero;
                if (hDesk != IntPtr.Zero)
                {
                    CloseDesktop(hDesk);
                }

                if (!_lastDesktopAccessible && isAccessible)
                {
                    // Transition: returned from secure desktop (UAC, lock screen, Ctrl+Alt+Del)
                    ResetModifierStates();
                    Rehook();
                }
                else if (_hookId == IntPtr.Zero && isAccessible)
                {
                    // Hook chain broken or uninitialized
                    Rehook();
                }

                _lastDesktopAccessible = isAccessible;
            }
            catch { }
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            try
            {
                IntPtr hMod = GetModuleHandle(null);
                IntPtr hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, hMod, 0);
                if (hook != IntPtr.Zero)
                    return hook;
            }
            catch { }

            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                if (curModule?.ModuleName != null)
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
            if (_isDisposed || nCode < 0 || lParam == IntPtr.Zero)
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

                    // Native decision engine (`hook.rs`). It owns modifier tracking,
                    // injected-key rejection, function/media pass-through, the
                    // Predator/Mode scancode disambiguation, the Predator+number
                    // combos and custom mode shortcuts. Every action passes the
                    // keystroke through, so Windows and the Acer Hotkey Service keep
                    // receiving it.
                    int nativeAction = PredatorNativeMigrations.HookProcess(
                        hookStruct.vkCode, hookStruct.scanCode, hookStruct.flags,
                        isKeyDown, Environment.TickCount64, out uint nativeValue);

                    if (nativeAction != PredatorNativeMigrations.Unavailable)
                    {
                        // Mirror the engine's live state into the managed flags so
                        // observers (the public modifier properties and callers that
                        // watch held-key state) see exactly what the engine decided.
                        uint nativeState = PredatorNativeMigrations.HookState();
                        _isCtrlPressed = (nativeState & 0x1) != 0;
                        _isAltPressed = (nativeState & 0x2) != 0;
                        _isShiftPressed = (nativeState & 0x4) != 0;
                        _isWinPressed = (nativeState & 0x8) != 0;
                        _isPredatorKeyPressed = (nativeState & PredatorNativeMigrations.HookStatePredatorHeld) != 0;

                        switch (nativeAction)
                        {
                            case PredatorNativeMigrations.HookActionPredatorSense:
                                DispatchAsync(() => _onPredatorSensePressed());
                                break;
                            case PredatorNativeMigrations.HookActionModeKey:
                                DispatchAsync(() =>
                                {
                                    if (_onModeKeyPressed != null)
                                        _onModeKeyPressed.Invoke();
                                    else
                                        ModeKeyPressed?.Invoke();
                                });
                                break;
                            case PredatorNativeMigrations.HookActionNumberKey:
                                if (_onPredatorNumberPressed != null)
                                {
                                    int capturedNumber = (int)nativeValue;
                                    DispatchAsync(() => _onPredatorNumberPressed(capturedNumber));
                                }
                                break;
                            case PredatorNativeMigrations.HookActionCustomShortcut:
                                if (_onCustomModeShortcutTriggered != null)
                                {
                                    byte targetMode = (byte)nativeValue;
                                    DispatchAsync(() => _onCustomModeShortcutTriggered?.Invoke(targetMode));
                                }
                                break;
                        }

                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // Immediate fast pass-through for all VK_F1..VK_F24 (0x70..0x87) and media/launch keys (0xAD..0xB7)
                    uint rawVk = hookStruct.vkCode;
                    if ((rawVk >= 0x70 && rawVk <= 0x87) || (rawVk >= 0xAD && rawVk <= 0xB7))
                    {
                        if (isKeyDown && _isPredatorKeyPressed)
                        {
                            _isPredatorKeyPressed = false;
                            _predatorUsedInCombo = false;
                            _suppressPredatorReleaseAction = false;
                            _activePredatorNumber = null;
                        }
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // Track physical modifier keys state
                    uint vk = hookStruct.vkCode;
                    if (vk == 0xA2 || vk == 0xA3 || vk == 0x11) // LCONTROL, RCONTROL, CONTROL
                    {
                        _isCtrlPressed = isKeyDown;
                    }
                    else if (vk == 0xA4 || vk == 0xA5 || vk == 0x12) // LMENU, RMENU, MENU/ALT
                    {
                        _isAltPressed = isKeyDown;
                    }
                    else if (vk == 0xA0 || vk == 0xA1 || vk == 0x10) // LSHIFT, RSHIFT, SHIFT
                    {
                        _isShiftPressed = isKeyDown;
                    }
                    else if (vk == 0x5B || vk == 0x5C) // LWIN, RWIN
                    {
                        _isWinPressed = isKeyDown;
                    }

                    // Check LLKHF_INJECTED and LLKHF_LOWER_IL_INJECTED to prevent synthetic spoofing
                    bool isInjected = (hookStruct.flags & LLKHF_INJECTED) != 0 ||
                                      (hookStruct.flags & LLKHF_LOWER_IL_INJECTED) != 0;

                    if (isInjected && !AllowInjectedKeys)
                    {
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // Reset auto-repeat tracking on KeyUp
                    if (isKeyUp && vk == _lastTriggeredShortcutVk)
                    {
                        _lastTriggeredShortcutVk = 0;
                    }

                    // Custom Mode Shortcuts Check (on key down of non-modifier key) - 100% Lock-Free
                    if (isKeyDown && _onCustomModeShortcutTriggered != null && !IsModifierKey(vk))
                    {
                        var shortcuts = Volatile.Read(ref _customShortcuts);
                        CustomModeShortcut? matchedShortcut = null;
                        for (int i = 0; i < shortcuts.Length; i++)
                        {
                            var s = shortcuts[i];
                            if (!s.Enabled) continue;
                            uint shortcutVk = (uint)(s.Key & Keys.KeyCode);
                            if (shortcutVk == vk &&
                                s.Ctrl == _isCtrlPressed &&
                                s.Alt == _isAltPressed &&
                                s.Shift == _isShiftPressed &&
                                s.Win == _isWinPressed)
                            {
                                matchedShortcut = s;
                                break;
                            }
                        }

                        if (matchedShortcut != null)
                        {
                            long now = Environment.TickCount64;
                            // Suppress auto-repeat hammering while key is held down
                            if (_lastTriggeredShortcutVk == vk && (now - _lastShortcutTriggerTick < 350))
                            {
                                return CallNextHookEx(_hookId, nCode, wParam, lParam);
                            }
                            _lastTriggeredShortcutVk = vk;
                            _lastShortcutTriggerTick = now;

                            byte targetMode = matchedShortcut.ModeCode;
                            DispatchAsync(() => _onCustomModeShortcutTriggered?.Invoke(targetMode));
                            // Pass through so application state remains predictable
                            return CallNextHookEx(_hookId, nCode, wParam, lParam);
                        }
                    }

                    // 1. Mandatory Function Key, OEM Key, Windows Key & Modifier Key Immediate Passthrough:
                    // Standard and extended function keys (VK_F1 to VK_F24, i.e. hookStruct.vkCode >= 0x70 && hookStruct.vkCode <= 0x87),
                    // OEM keys, Windows keys (VK_LWIN, VK_RWIN), and modifier keys MUST be passed through immediately
                    // to CallNextHookEx at the very beginning of processing, BEFORE ANY check for Predator key or Mode key.
                    // Under NO circumstances should F1..F24 ever be consumed (return (IntPtr)1) or trigger Mode key cycling.
                    if (IsPassthroughKey(hookStruct.vkCode))
                    {
                        if (isKeyDown && _isPredatorKeyPressed)
                        {
                            _isPredatorKeyPressed = false;
                            _predatorUsedInCombo = false;
                            _suppressPredatorReleaseAction = false;
                            _activePredatorNumber = null;
                        }
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // 2. Disambiguate Predator & Mode Scancodes vs Function Keys:
                    // Note: SC_PREDATOR = 0x75 (VK_F6), SC_MODE = 0x76 (VK_F7), SC_MODE_ALT = 0x77 (VK_F8).
                    // If hookStruct.vkCode == 0x75 || hookStruct.vkCode == 0x76 || hookStruct.vkCode == 0x77,
                    // these are ordinary F6, F7, F8 function keys on PC keyboards! They MUST pass through!
                    if (hookStruct.vkCode == 0x75 || hookStruct.vkCode == 0x76 || hookStruct.vkCode == 0x77)
                    {
                        if (isKeyDown && _isPredatorKeyPressed)
                        {
                            _isPredatorKeyPressed = false;
                            _predatorUsedInCombo = false;
                            _suppressPredatorReleaseAction = false;
                            _activePredatorNumber = null;
                        }
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // 3. Dedicated Predator Mode switch key:
                    // Only match dedicated physical hardware scancodes (SC_MODE = 0x76, SC_MODE_ALT = 0x77).
                    // Disambiguation: Must NOT be any function key (vkCode < 0x70 || vkCode > 0x87)
                    // and must not be VK_F7 (0x76) or VK_F8 (0x77).
                    // Furthermore, isModeKey must NEVER swallow or consume the key; it must ALWAYS pass through
                    // via CallNextHookEx to Windows / Acer Hotkey Service.
                    bool isModeKey = (hookStruct.scanCode == SC_MODE || hookStruct.scanCode == SC_MODE_ALT)
                        && (hookStruct.vkCode < 0x70 || hookStruct.vkCode > 0x87)
                        && hookStruct.vkCode != 0x76 && hookStruct.vkCode != 0x77;

                    if (isModeKey)
                    {
                        if (isKeyDown)
                        {
                            long now = Environment.TickCount64;
                            if (now - _lastModeKeyTick >= 250)
                            {
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
                        }

                        // Always pass through to Windows / Acer Hotkey Service so Fn combos and function keys are NEVER swallowed
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // 4. Dedicated Predator / NitroSense key:
                    // Only match dedicated hardware scancode SC_PREDATOR = 0x75 when vkCode is NOT F6 (0x75)
                    // and NOT any standard or extended function key.
                    bool isPredatorKey = (hookStruct.scanCode == SC_PREDATOR || hookStruct.scanCode == 0xF5)
                        && (hookStruct.vkCode < 0x70 || hookStruct.vkCode > 0x87)
                        && hookStruct.vkCode != 0x75;

                    if (isPredatorKey)
                    {
                        if (isKeyDown)
                        {
                            if (!_isPredatorKeyPressed)
                            {
                                _isPredatorKeyPressed = true;
                                _predatorKeyPressedTick = Environment.TickCount64;
                                _predatorUsedInCombo = false;
                            }
                        }
                        else if (isKeyUp)
                        {
                            if (!_predatorUsedInCombo && !_suppressPredatorReleaseAction)
                            {
                                long now = Environment.TickCount64;
                                if (now - _lastPredatorSenseTick >= 250)
                                {
                                    _lastPredatorSenseTick = now;
                                    DispatchAsync(() => _onPredatorSensePressed());
                                }
                            }
                            _isPredatorKeyPressed = false;
                            _predatorUsedInCombo = false;
                            _suppressPredatorReleaseAction = false;
                            _activePredatorNumber = null;
                        }

                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // Timeout safety: if Predator key has been held longer than 3 seconds without keyup,
                    // automatically release the held state so number keys are never held hostage.
                    if (_isPredatorKeyPressed && (Environment.TickCount64 - _predatorKeyPressedTick > PredatorKeyHoldTimeoutMs))
                    {
                        _isPredatorKeyPressed = false;
                        _predatorUsedInCombo = false;
                        _suppressPredatorReleaseAction = false;
                        _activePredatorNumber = null;
                    }

                    // 5. Predator key combo with number row (1-5) or numpad (1-5)
                    bool isNumberKey = (hookStruct.vkCode >= 0x31 && hookStruct.vkCode <= 0x35) ||
                                       (hookStruct.vkCode >= 0x61 && hookStruct.vkCode <= 0x65);

                    if (isNumberKey)
                    {
                        // Trigger mode switch asynchronously when Predator key is actively held within the timeout window
                        if (_isPredatorKeyPressed && _onPredatorNumberPressed != null &&
                            (Environment.TickCount64 - _predatorKeyPressedTick <= PredatorKeyHoldTimeoutMs))
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
                        }

                        // Always pass through number keys so typing is NEVER swallowed or blocked!
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // 6. Any other normal key press while _isPredatorKeyPressed was flag-set:
                    // If user is typing any other key, clear any stuck predator key state.
                    if (isKeyDown && _isPredatorKeyPressed)
                    {
                        _isPredatorKeyPressed = false;
                        _predatorUsedInCombo = false;
                        _suppressPredatorReleaseAction = false;
                        _activePredatorNumber = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KeyboardHook callback exception: {ex.Message}");
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static bool IsModifierKey(uint vkCode)
        {
            return vkCode == 0x10 || vkCode == 0x11 || vkCode == 0x12 || // Shift, Control, Alt
                   vkCode == 0xA0 || vkCode == 0xA1 || // L/R Shift
                   vkCode == 0xA2 || vkCode == 0xA3 || // L/R Control
                   vkCode == 0xA4 || vkCode == 0xA5 || // L/R Menu (Alt)
                   vkCode == 0x5B || vkCode == 0x5C;   // L/R Win
        }

        /// <summary>
        /// Identifies standard/extended function keys (F1-F24), OEM keys, Windows keys, and modifier keys
        /// that must ALWAYS immediately pass through without interference or interception.
        /// </summary>
        private static bool IsPassthroughKey(uint vkCode)
        {
            // Standard and extended function keys: VK_F1 (0x70) to VK_F24 (0x87).
            // Under NO circumstances should F1..F24 ever be consumed or trigger Mode key cycling.
            // Note: VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77 are in this range.
            if (vkCode >= 0x70 && vkCode <= 0x87)
            {
                return true;
            }

            // Windows keys: VK_LWIN (0x5B), VK_RWIN (0x5C), VK_APPS (0x5D), VK_SLEEP (0x5F)
            if (vkCode >= 0x5B && vkCode <= 0x5F)
            {
                return true;
            }

            // Modifier & System keys:
            // VK_TAB (0x09), VK_SHIFT (0x10), VK_CONTROL (0x11), VK_MENU/Alt (0x12),
            // VK_PAUSE (0x13), VK_CAPITAL/CapsLock (0x14), VK_ESCAPE (0x1B),
            // VK_NUMLOCK (0x90), VK_SCROLL (0x91),
            // Left/Right modifiers: VK_LSHIFT (0xA0), VK_RSHIFT (0xA1), VK_LCONTROL (0xA2),
            // VK_RCONTROL (0xA3), VK_LMENU (0xA4), VK_RMENU (0xA5)
            if (vkCode == 0x09 ||
                (vkCode >= 0x10 && vkCode <= 0x14) ||
                vkCode == 0x1B ||
                (vkCode >= 0x90 && vkCode <= 0x91) ||
                (vkCode >= 0xA0 && vkCode <= 0xA5))
            {
                return true;
            }

            // Navigation / Cursor / Editing keys (frequently mapped to Fn + Arrows / Numpad):
            // VK_PRIOR (0x21), VK_NEXT (0x22), VK_END (0x23), VK_HOME (0x24),
            // VK_LEFT (0x25), VK_UP (0x26), VK_RIGHT (0x27), VK_DOWN (0x28),
            // VK_SELECT (0x29), VK_PRINT (0x2A), VK_EXECUTE (0x2B), VK_SNAPSHOT/PrtScn (0x2C),
            // VK_INSERT (0x2D), VK_DELETE (0x2E), VK_HELP (0x2F)
            if (vkCode >= 0x21 && vkCode <= 0x2F)
            {
                return true;
            }

            // Multimedia / Audio / Media keys:
            // VK_VOLUME_MUTE (0xAD), VK_VOLUME_DOWN (0xAE), VK_VOLUME_UP (0xAF),
            // VK_MEDIA_NEXT_TRACK (0xB0), VK_MEDIA_PREV_TRACK (0xB1), VK_MEDIA_STOP (0xB2),
            // VK_MEDIA_PLAY_PAUSE (0xB3), VK_LAUNCH_MAIL (0xB4), VK_LAUNCH_MEDIA_SELECT (0xB5),
            // VK_LAUNCH_APP1 (0xB6), VK_LAUNCH_APP2 (0xB7)
            if (vkCode >= 0xAD && vkCode <= 0xB7)
            {
                return true;
            }

            // Browser keys:
            // VK_BROWSER_BACK (0xA6) .. VK_BROWSER_HOME (0xAC)
            if (vkCode >= 0xA6 && vkCode <= 0xAC)
            {
                return true;
            }

            // Common control & whitespace keys:
            // VK_CANCEL (0x03), VK_BACK (0x08), VK_CLEAR (0x0C), VK_RETURN (0x0D), VK_SPACE (0x20)
            if (vkCode == 0x03 || vkCode == 0x08 || vkCode == 0x0C || vkCode == 0x0D || vkCode == 0x20)
            {
                return true;
            }

            // OEM keys:
            // 0xBA..0xC0 (VK_OEM_1, VK_OEM_PLUS, VK_OEM_COMMA, VK_OEM_MINUS, VK_OEM_PERIOD, VK_OEM_2, VK_OEM_3)
            // 0xDB..0xF5 (VK_OEM_4..VK_OEM_8, VK_OEM_102, process, packet, reset, jump, etc.)
            // 0x92..0x96 (OEM specific keys)
            if ((vkCode >= 0xBA && vkCode <= 0xC0) ||
                (vkCode >= 0xDB && vkCode <= 0xF5) ||
                (vkCode >= 0x92 && vkCode <= 0x96))
            {
                return true;
            }

            return false;
        }

        private static void DispatchAsync(Action action)
        {
            if (action == null) return;
            ThreadPool.UnsafeQueueUserWorkItem(static act =>
            {
                try
                {
                    act();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"KeyboardHook async callback failure: {ex.Message}");
                }
            }, action, preferLocal: true);
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
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int vKey);

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        #endregion

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _watchdogTimer?.Dispose();
            }
            catch { }

            try
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExitOrCrash;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                Application.ApplicationExit -= OnApplicationExit;
            }
            catch { }

            PredatorNativeMigrations.HookSetShortcuts(Array.Empty<NativeShortcutEntry>());
            ResetModifierStates();
            UninstallHookDirect();
            GC.KeepAlive(_proc);
            GC.SuppressFinalize(this);
        }

        ~KeyboardHook()
        {
            UninstallHookDirect();
        }
    }
}
