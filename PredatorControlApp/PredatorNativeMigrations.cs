using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PredatorControlApp
{
    /// <summary>
    /// One configured custom mode shortcut, mirroring <c>hook::ShortcutEntry</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeShortcutEntry
    {
        public uint Vk;
        public uint ModeCode;
        public uint Ctrl;
        public uint Alt;
        public uint Shift;
        public uint Win;
        public uint Enabled;
    }

    /// <summary>
    /// Game profile fields, mirroring <c>procsync::ProfileValues</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeProfileValues
    {
        public int CpuFanSpeed;
        public int GpuFanSpeed;
        public int SysFanSpeed;
        public int RefreshRate;
        public int BatteryLimit;
        public int RgbBrightness;
        public int RgbSpeed;
        public int RgbR;
        public int RgbG;
        public int RgbB;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePowerPair
    {
        public int Pl1Watts;
        public int Pl2Watts;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeCpuHardwareSpec
    {
        public uint Family;
        public int BaseTdpWatts;
        public int MinPowerWatts;
        public int MaxPl1Watts;
        public int MaxPl2Watts;
        public NativePowerPair Eco;         // 0x06
        public NativePowerPair Quiet;       // 0x00
        public NativePowerPair Balanced;    // 0x01
        public NativePowerPair Performance; // 0x04
        public NativePowerPair Turbo;       // 0x05
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeCurvePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRawBatteryInfo
    {
        public uint Capabilities;
        public byte Technology;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint CycleCount;
        public double WearLevelPercent;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeUefiPowerConfig
    {
        public uint Pl1Milliwatts;
        public uint Pl2Milliwatts;
        public uint Pl1Watts;
        public uint Pl2Watts;
        [MarshalAs(UnmanagedType.I1)] public bool OverclockingLock;
        [MarshalAs(UnmanagedType.I1)] public bool CfgLock;
    }

    /// <summary>
    /// Managed surface for the subsystems whose logic now lives in
    /// <c>predator_core</c>: shortcut rules, battery ETA, history storage,
    /// graph projection, the keyboard-hook decision engine, process matching
    /// and pipe-message framing.
    ///
    /// Every entry point degrades to a sentinel when the native core is absent,
    /// so each caller keeps its pre-existing managed fallback. Nothing here
    /// throws across the boundary: failures are reported as return codes.
    /// </summary>
    public static class PredatorNativeMigrations
    {
        private const string DllName = "predator_core.dll";

        // Hook action ids, matching `hook::ACTION_*`.
        public const int HookActionPass = 0;
        public const int HookActionPredatorSense = 1;
        public const int HookActionModeKey = 2;
        public const int HookActionNumberKey = 3;
        public const int HookActionCustomShortcut = 4;

        // Shortcut rule ids, matching `shortcuts::E_*`.
        public const uint RuleOk = 0;
        public const uint RuleFunctionKey = 1;
        public const uint RuleWinModifier = 2;
        public const uint RuleNoPrimaryKey = 3;
        public const uint RuleWinBaseKey = 4;
        public const uint RuleModifierAlone = 5;
        public const uint RuleNoModifier = 6;
        public const uint RuleReservedSystemKey = 7;
        public const uint RuleAltSpace = 8;
        public const uint RuleCtrlAltDel = 9;
        public const uint RuleCtrlShiftEsc = 10;
        public const uint RuleAltTabOrEsc = 11;
        public const uint RuleAltEnter = 12;
        public const uint RuleCtrlEditingKey = 13;

        /// <summary>Sentinel for "native core unavailable, use the managed path".</summary>
        public const int Unavailable = -1;

        private static bool Available => PredatorNativeCore.IsNativeCoreAvailable;

        #region P/Invoke declarations

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern uint predator_shortcut_validate(uint key, int ctrl, int alt, int shift, int win);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_shortcut_same_combination(
            uint keyA, int ctrlA, int altA, int shiftA, int winA,
            uint keyB, int ctrlB, int altB, int shiftB, int winB);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_shortcut_is_modifier_key(uint vk);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_shortcut_is_reserved_ctrl_key(uint vk);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_battery_eta_reset();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern double predator_battery_eta_set_alpha(double alpha);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_battery_eta_smoothed_rate(out double outRate);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_battery_eta_smoothed_percent_rate(out double outRate);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_battery_eta_update_capacity(int capacity, long tsMs);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_battery_eta_update_percent(int percent, long tsMs);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_battery_eta_calculate(
            int phase, int remainingCapacity, int fullCapacity,
            double directRateMw, int hasDirectRate, int allowWarmup,
            int batteryPercent, int hasPercent, out double outSeconds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_battery_eta_warmup(int phase, int batteryPercent, out double outSeconds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr predator_history_create(int capacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_history_destroy(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_history_add(IntPtr handle, double value, int hasValue);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_history_count(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_history_get(IntPtr handle, int index, out double outValue);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_history_min(IntPtr handle, out double outValue);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_history_max(IntPtr handle, out double outValue);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_history_average(IntPtr handle, out double outValue);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_history_latest(IntPtr handle, out double outValue);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_history_snapshot(IntPtr handle, [Out] double[] outValues, int outCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_history_clear(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern float predator_graph_project_y(double value, double min, double max, float height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_graph_project_series(
            [In] double[] samples, int count, int capacity,
            float width, float height, double min, double max, [Out] float[] outXy);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_hook_reset();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_hook_set_allow_injected(int allow);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_hook_set_shortcuts([In] NativeShortcutEntry[] entries, int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_hook_process(
            uint vk, uint scan, uint flags, int isDown, long nowMs, out uint outValue);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern uint predator_hook_modifier_mask();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern uint predator_hook_state();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_procsync_executable_name(
            [In] byte[] input, int inputLen, [Out] byte[] output, int outputCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_procsync_process_name(
            [In] byte[] input, int inputLen, [Out] byte[] output, int outputCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_procsync_same_process(
            [In] byte[] a, int aLen, [In] byte[] b, int bLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_procsync_matches_running(
            [In] byte[] profileExecutable, int profileLen, [In] byte[] running, int runningLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_procsync_sanitize_profile(
            ref NativeProfileValues input, out NativeProfileValues output);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_pipeguard_sanitize(
            [In] byte[] input, int inputLen, [Out] byte[] output, int outputCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_pipeguard_is_valid([In] byte[] input, int inputLen);

        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_curve_normalize([In] NativeCurvePoint[] inPts, UIntPtr inLen, [Out] NativeCurvePoint[] outPts, UIntPtr outCap);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_cpuspec_detect([In] byte[] modelBytes, int modelLen, out NativeCpuHardwareSpec outSpec);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_cpuspec_estimate_proportional_limits(int tdpWatts, byte mode, out int outPl1, out int outPl2);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr predator_backlight_create(int initialAcBrightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_backlight_destroy(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_evaluate_idle(IntPtr handle, long nowSec, out int outShouldDim, out int outBrightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_on_activity(IntPtr handle, out int outShouldRestore, out int outBrightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_is_on_battery(IntPtr handle, int lineStatus, int chargeStatus);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_on_lid_changed(IntPtr handle, int isOpen, int lineStatus, int chargeStatus, out int outTurnOff, out int outBrightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_on_suspend(IntPtr handle, out int outTurnOff, out int outBrightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_on_resume(IntPtr handle, int lineStatus, int chargeStatus, out int outTurnOff, out int outBrightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_set_plugged_in(IntPtr handle, int pluggedIn);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_set_saved_ac(IntPtr handle, int brightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_set_manual_battery(IntPtr handle, int enabled, int brightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_set_idle_config(IntPtr handle, int timeoutSec, int dimmedPercent);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_backlight_record_activity(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_procsync_is_process_running([In] byte[] namePtr, int nameLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_procsync_get_foreground_process_name([Out] byte[] outBuffer, int outCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_battery_parse_info([In] byte[] bytes, UIntPtr len, out NativeRawBatteryInfo outInfo);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_uefi_scan_power_and_locks([In] byte[] aml, UIntPtr amlLen, out NativeUefiPowerConfig outCfg);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_uefi_parse_variable_power([In] byte[] data, UIntPtr dataLen, out NativeUefiPowerConfig outCfg);

        #endregion

        #region Shortcut rules

        /// <summary>
        /// Returns a rule id, or <see cref="Unavailable"/> when the native core
        /// is missing and the caller must run its managed rules.
        /// </summary>
        public static int ValidateShortcut(Keys key, bool ctrl, bool alt, bool shift, bool win, out uint rule)
        {
            rule = RuleOk;
            if (!Available) return Unavailable;
            try
            {
                rule = predator_shortcut_validate((uint)(int)key, ctrl ? 1 : 0, alt ? 1 : 0, shift ? 1 : 0, win ? 1 : 0);
                return 0;
            }
            catch { return Unavailable; }
        }

        /// <summary>Returns <see cref="Unavailable"/> when the native core is missing.</summary>
        public static int SameCombination(Keys a, bool ctrlA, bool altA, bool shiftA, bool winA,
                                          Keys b, bool ctrlB, bool altB, bool shiftB, bool winB)
        {
            if (!Available) return Unavailable;
            try
            {
                return predator_shortcut_same_combination(
                    (uint)(int)a, ctrlA ? 1 : 0, altA ? 1 : 0, shiftA ? 1 : 0, winA ? 1 : 0,
                    (uint)(int)b, ctrlB ? 1 : 0, altB ? 1 : 0, shiftB ? 1 : 0, winB ? 1 : 0);
            }
            catch { return Unavailable; }
        }

        /// <summary>Returns <see cref="Unavailable"/> when the native core is missing.</summary>
        public static int IsModifierKey(int vk)
        {
            if (!Available) return Unavailable;
            try { return predator_shortcut_is_modifier_key((uint)vk); }
            catch { return Unavailable; }
        }

        /// <summary>Returns <see cref="Unavailable"/> when the native core is missing.</summary>
        public static int IsReservedCtrlKey(int vk)
        {
            if (!Available) return Unavailable;
            try { return predator_shortcut_is_reserved_ctrl_key((uint)vk); }
            catch { return Unavailable; }
        }

        #endregion

        #region Battery ETA

        public static bool BatteryEtaReset()
        {
            if (!Available) return false;
            try { predator_battery_eta_reset(); return true; }
            catch { return false; }
        }

        public static double BatteryEtaSetAlpha(double alpha)
        {
            if (!Available) return double.NaN;
            try { return predator_battery_eta_set_alpha(alpha); }
            catch { return double.NaN; }
        }

        public static bool BatteryEtaSmoothedRate(out double rate)
        {
            rate = 0;
            if (!Available) return false;
            try { return predator_battery_eta_smoothed_rate(out rate) != 0; }
            catch { return false; }
        }

        public static bool BatteryEtaSmoothedPercentRate(out double rate)
        {
            rate = 0;
            if (!Available) return false;
            try { return predator_battery_eta_smoothed_percent_rate(out rate) != 0; }
            catch { return false; }
        }

        public static bool BatteryEtaUpdateCapacity(int capacity, long tsMs)
        {
            if (!Available) return false;
            try { predator_battery_eta_update_capacity(capacity, tsMs); return true; }
            catch { return false; }
        }

        public static bool BatteryEtaUpdatePercent(int percent, long tsMs)
        {
            if (!Available) return false;
            try { predator_battery_eta_update_percent(percent, tsMs); return true; }
            catch { return false; }
        }

        /// <summary>
        /// Returns 0 with an estimate, 1 when warm-up is needed, 2 when no
        /// estimate is available, or <see cref="Unavailable"/>.
        /// </summary>
        public static int BatteryEtaCalculate(
            int phase, int remainingCapacity, int fullCapacity,
            double? directRateMw, bool allowWarmup, int? batteryPercent, out double seconds)
        {
            seconds = 0;
            if (!Available) return Unavailable;
            try
            {
                int code = predator_battery_eta_calculate(
                    phase, remainingCapacity, fullCapacity,
                    directRateMw ?? 0.0, directRateMw.HasValue ? 1 : 0,
                    allowWarmup ? 1 : 0,
                    batteryPercent ?? 0, batteryPercent.HasValue ? 1 : 0,
                    out seconds);

                // Rust codes: 1 estimate, 2 warm-up, 0 none.
                return code switch { 1 => 0, 2 => 1, _ => 2 };
            }
            catch { return Unavailable; }
        }

        public static bool BatteryEtaWarmup(int phase, int batteryPercent, out double seconds)
        {
            seconds = 0;
            if (!Available) return false;
            try { return predator_battery_eta_warmup(phase, batteryPercent, out seconds) != 0; }
            catch { return false; }
        }

        #endregion

        #region History storage

        public static IntPtr HistoryCreate(int capacity)
        {
            if (!Available) return IntPtr.Zero;
            try { return predator_history_create(capacity); }
            catch { return IntPtr.Zero; }
        }

        public static void HistoryDestroy(IntPtr handle)
        {
            if (!Available || handle == IntPtr.Zero) return;
            try { predator_history_destroy(handle); }
            catch { }
        }

        public static void HistoryAdd(IntPtr handle, double? value)
        {
            if (!Available || handle == IntPtr.Zero) return;
            try { predator_history_add(handle, value ?? 0.0, value.HasValue ? 1 : 0); }
            catch { }
        }

        public static int HistoryCount(IntPtr handle)
        {
            if (!Available || handle == IntPtr.Zero) return 0;
            try { return predator_history_count(handle); }
            catch { return 0; }
        }

        public static bool HistoryGet(IntPtr handle, int index, out double value)
        {
            value = 0;
            if (!Available || handle == IntPtr.Zero) return false;
            try { return predator_history_get(handle, index, out value) != 0; }
            catch { return false; }
        }

        public static bool HistoryMin(IntPtr handle, out double value)
        {
            value = 0;
            if (!Available || handle == IntPtr.Zero) return false;
            try { return predator_history_min(handle, out value) != 0; }
            catch { return false; }
        }

        public static bool HistoryMax(IntPtr handle, out double value)
        {
            value = 0;
            if (!Available || handle == IntPtr.Zero) return false;
            try { return predator_history_max(handle, out value) != 0; }
            catch { return false; }
        }

        public static bool HistoryAverage(IntPtr handle, out double value)
        {
            value = 0;
            if (!Available || handle == IntPtr.Zero) return false;
            try { return predator_history_average(handle, out value) != 0; }
            catch { return false; }
        }

        public static bool HistoryLatest(IntPtr handle, out double value)
        {
            value = 0;
            if (!Available || handle == IntPtr.Zero) return false;
            try { return predator_history_latest(handle, out value) != 0; }
            catch { return false; }
        }

        public static int HistorySnapshot(IntPtr handle, double[] buffer)
        {
            if (!Available || handle == IntPtr.Zero || buffer == null) return 0;
            try { return predator_history_snapshot(handle, buffer, buffer.Length); }
            catch { return 0; }
        }

        public static void HistoryClear(IntPtr handle)
        {
            if (!Available || handle == IntPtr.Zero) return;
            try { predator_history_clear(handle); }
            catch { }
        }

        #endregion

        #region Graph projection

        /// <summary>Returns <see cref="float.NaN"/> when the native core is missing.</summary>
        public static float GraphProjectY(double value, double min, double max, float height)
        {
            if (!Available) return float.NaN;
            try { return predator_graph_project_y(value, min, max, height); }
            catch { return float.NaN; }
        }

        /// <summary>
        /// Fills <paramref name="outXy"/> with interleaved (x, y) pairs.
        /// Returns 0 when the native core is missing.
        /// </summary>
        public static int GraphProjectSeries(double[] samples, int count, int capacity,
                                             float width, float height, double min, double max, float[] outXy)
        {
            if (!Available || samples == null || outXy == null || count <= 0 || count > samples.Length || outXy.Length < count * 2) return 0;
            try
            {
                return predator_graph_project_series(samples, count, capacity, width, height, min, max, outXy);
            }
            catch { return 0; }
        }

        #endregion

        #region Keyboard hook engine

        public static bool HookReset()
        {
            if (!Available) return false;
            try { predator_hook_reset(); return true; }
            catch { return false; }
        }

        public static bool HookSetAllowInjected(bool allow)
        {
            if (!Available) return false;
            try { predator_hook_set_allow_injected(allow ? 1 : 0); return true; }
            catch { return false; }
        }

        /// <summary>Returns the number of shortcuts stored, or <see cref="Unavailable"/>.</summary>
        public static int HookSetShortcuts(NativeShortcutEntry[] entries)
        {
            if (!Available) return Unavailable;
            try
            {
                return predator_hook_set_shortcuts(entries ?? Array.Empty<NativeShortcutEntry>(), entries?.Length ?? 0);
            }
            catch { return Unavailable; }
        }

        /// <summary>
        /// Returns an action id, or <see cref="Unavailable"/> when the caller
        /// must run its managed decision path.
        /// </summary>
        public static int HookProcess(uint vk, uint scan, uint flags, bool isDown, long nowMs, out uint value)
        {
            value = 0;
            if (!Available) return Unavailable;
            try { return predator_hook_process(vk, scan, flags, isDown ? 1 : 0, nowMs, out value); }
            catch { return Unavailable; }
        }

        public static uint HookModifierMask()
        {
            if (!Available) return 0;
            try { return predator_hook_modifier_mask(); }
            catch { return 0; }
        }

        /// <summary>
        /// Live engine state: bit0-3 Ctrl/Alt/Shift/Win, bit4 Predator key held.
        /// Returns 0 when the native core is missing.
        /// </summary>
        public static uint HookState()
        {
            if (!Available) return 0;
            try { return predator_hook_state(); }
            catch { return 0; }
        }

        /// <summary>Bit in <see cref="HookState"/> for the held Predator key.</summary>
        public const uint HookStatePredatorHeld = 0x10;

        #endregion

        #region Process matching

        /// <summary>Returns null when the native core is missing.</summary>
        public static string? ExecutableName(string input)
        {
            return NativeStringCall(input, predator_procsync_executable_name);
        }

        /// <summary>Returns null when the native core is missing.</summary>
        public static string? ProcessName(string input)
        {
            return NativeStringCall(input, predator_procsync_process_name);
        }

        private delegate int Utf8Transform([In] byte[] input, int inputLen, [Out] byte[] output, int outputCapacity);

        private static string? NativeStringCall(string? input, Utf8Transform call)
        {
            if (!Available) return null;
            try
            {
                byte[] inBytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
                byte[] outBytes = new byte[512];
                int written = call(inBytes, inBytes.Length, outBytes, outBytes.Length);
                return written < 0 ? string.Empty : Encoding.UTF8.GetString(outBytes, 0, written);
            }
            catch { return null; }
        }

        /// <summary>Returns <see cref="Unavailable"/> when the native core is missing.</summary>
        public static int SameProcess(string a, string b)
        {
            if (!Available) return Unavailable;
            try
            {
                byte[] left = Encoding.UTF8.GetBytes(a ?? string.Empty);
                byte[] right = Encoding.UTF8.GetBytes(b ?? string.Empty);
                return predator_procsync_same_process(left, left.Length, right, right.Length);
            }
            catch { return Unavailable; }
        }

        /// <summary>Returns <see cref="Unavailable"/> when the native core is missing.</summary>
        public static int MatchesRunning(string profileExecutable, string running)
        {
            if (!Available) return Unavailable;
            try
            {
                byte[] profile = Encoding.UTF8.GetBytes(profileExecutable ?? string.Empty);
                byte[] live = Encoding.UTF8.GetBytes(running ?? string.Empty);
                return predator_procsync_matches_running(profile, profile.Length, live, live.Length);
            }
            catch { return Unavailable; }
        }

        public static bool SanitizeProfile(ref NativeProfileValues values)
        {
            if (!Available) return false;
            try { return predator_procsync_sanitize_profile(ref values, out values) == 0; }
            catch { return false; }
        }

        #endregion

        #region Pipe framing

        /// <summary>Returns null when the native core is missing.</summary>
        public static string? SanitizePipeMessage(string? message)
        {
            if (!Available) return null;
            try
            {
                byte[] inBytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
                byte[] outBytes = new byte[512];
                int written = predator_pipeguard_sanitize(inBytes, inBytes.Length, outBytes, outBytes.Length);
                return written < 0 ? string.Empty : Encoding.UTF8.GetString(outBytes, 0, written);
            }
            catch { return null; }
        }

        /// <summary>Returns <see cref="Unavailable"/> when the native core is missing.</summary>
        public static int IsValidPipeMessage(string? message)
        {
            if (!Available) return Unavailable;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
                return predator_pipeguard_is_valid(bytes, bytes.Length);
            }
            catch { return Unavailable; }
        }


        #endregion

        #region Tier 1: 8-Point Fan Curve Normalizer

        public static bool TryNormalizeCurve(System.Collections.Generic.IReadOnlyList<System.Drawing.Point>? points, out System.Collections.Generic.List<System.Drawing.Point> normalized)
        {
            normalized = new System.Collections.Generic.List<System.Drawing.Point>();
            if (!Available || points == null || points.Count < 2) return false;
            try
            {
                var inArr = new NativeCurvePoint[points.Count];
                for (int i = 0; i < points.Count; i++)
                {
                    inArr[i] = new NativeCurvePoint { X = points[i].X, Y = points[i].Y };
                }
                var outArr = new NativeCurvePoint[8];
                int res = predator_curve_normalize(inArr, (UIntPtr)inArr.Length, outArr, (UIntPtr)outArr.Length);
                if (res != 8) return false;
                for (int i = 0; i < 8; i++)
                {
                    normalized.Add(new System.Drawing.Point(outArr[i].X, outArr[i].Y));
                }
                return true;
            }
            catch { return false; }
        }

        #endregion

        #region Tier 1: CPU Spec & Power Limits

        public static bool TryDetectCpuSpec(string? model, out NativeCpuHardwareSpec spec)
        {
            spec = default;
            if (!Available) return false;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(model ?? string.Empty);
                return predator_cpuspec_detect(bytes, bytes.Length, out spec) == 0;
            }
            catch { return false; }
        }

        public static bool TryEstimateProportionalLimits(int tdpWatts, byte mode, out int pl1, out int pl2)
        {
            pl1 = 0;
            pl2 = 0;
            if (!Available) return false;
            try
            {
                return predator_cpuspec_estimate_proportional_limits(tdpWatts, mode, out pl1, out pl2) == 0;
            }
            catch { return false; }
        }

        #endregion

        #region Tier 1: Backlight State Machine Native Engine

        public static IntPtr BacklightCreate(int initialAcBrightness)
        {
            if (!Available) return IntPtr.Zero;
            try { return predator_backlight_create(initialAcBrightness); }
            catch { return IntPtr.Zero; }
        }

        public static void BacklightDestroy(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !Available) return;
            try { predator_backlight_destroy(handle); } catch { }
        }

        public static bool BacklightEvaluateIdle(IntPtr handle, long nowSec, out bool shouldDim, out int brightness)
        {
            shouldDim = false;
            brightness = 0;
            if (handle == IntPtr.Zero || !Available) return false;
            try
            {
                int res = predator_backlight_evaluate_idle(handle, nowSec, out int dim, out brightness);
                shouldDim = dim != 0;
                return res != 0;
            }
            catch { return false; }
        }

        public static bool BacklightOnActivity(IntPtr handle, out bool shouldRestore, out int brightness)
        {
            shouldRestore = false;
            brightness = 0;
            if (handle == IntPtr.Zero || !Available) return false;
            try
            {
                int res = predator_backlight_on_activity(handle, out int restore, out brightness);
                shouldRestore = restore != 0;
                return res != 0;
            }
            catch { return false; }
        }

        public static int BacklightIsOnBattery(IntPtr handle, int lineStatus, int chargeStatus)
        {
            if (handle == IntPtr.Zero || !Available) return Unavailable;
            try { return predator_backlight_is_on_battery(handle, lineStatus, chargeStatus); }
            catch { return Unavailable; }
        }

        public static bool BacklightOnLidChanged(IntPtr handle, bool isOpen, int lineStatus, int chargeStatus, out bool shouldTurnOff, out int targetBrightness)
        {
            shouldTurnOff = false;
            targetBrightness = 0;
            if (handle == IntPtr.Zero || !Available) return false;
            try
            {
                int res = predator_backlight_on_lid_changed(handle, isOpen ? 1 : 0, lineStatus, chargeStatus, out int off, out targetBrightness);
                shouldTurnOff = off != 0;
                return res != 0;
            }
            catch { return false; }
        }

        public static bool BacklightOnSuspend(IntPtr handle, out bool shouldTurnOff, out int targetBrightness)
        {
            shouldTurnOff = false;
            targetBrightness = 0;
            if (handle == IntPtr.Zero || !Available) return false;
            try
            {
                int res = predator_backlight_on_suspend(handle, out int off, out targetBrightness);
                shouldTurnOff = off != 0;
                return res != 0;
            }
            catch { return false; }
        }

        public static bool BacklightOnResume(IntPtr handle, int lineStatus, int chargeStatus, out bool shouldTurnOff, out int targetBrightness)
        {
            shouldTurnOff = false;
            targetBrightness = 0;
            if (handle == IntPtr.Zero || !Available) return false;
            try
            {
                int res = predator_backlight_on_resume(handle, lineStatus, chargeStatus, out int off, out targetBrightness);
                shouldTurnOff = off != 0;
                return res != 0;
            }
            catch { return false; }
        }

        public static void BacklightSetPluggedIn(IntPtr handle, bool? pluggedIn)
        {
            if (handle == IntPtr.Zero || !Available) return;
            try
            {
                int val = pluggedIn.HasValue ? (pluggedIn.Value ? 1 : 0) : -1;
                predator_backlight_set_plugged_in(handle, val);
            }
            catch { }
        }

        public static void BacklightSetSavedAc(IntPtr handle, int brightness)
        {
            if (handle == IntPtr.Zero || !Available) return;
            try { predator_backlight_set_saved_ac(handle, brightness); } catch { }
        }

        public static void BacklightSetManualBattery(IntPtr handle, bool enabled, int brightness)
        {
            if (handle == IntPtr.Zero || !Available) return;
            try { predator_backlight_set_manual_battery(handle, enabled ? 1 : 0, brightness); } catch { }
        }

        public static void BacklightSetIdleConfig(IntPtr handle, int timeoutSec, int dimmedPercent)
        {
            if (handle == IntPtr.Zero || !Available) return;
            try { predator_backlight_set_idle_config(handle, timeoutSec, dimmedPercent); } catch { }
        }

        public static void BacklightRecordActivity(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !Available) return;
            try { predator_backlight_record_activity(handle); } catch { }
        }

        #endregion

        #region Tier 2: Background Process Scanner

        public static bool IsProcessRunning(string exeName)
        {
            if (!Available || string.IsNullOrWhiteSpace(exeName)) return false;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(exeName.Trim());
                return predator_procsync_is_process_running(bytes, bytes.Length) == 1;
            }
            catch { return false; }
        }

        public static string? GetForegroundProcessName()
        {
            if (!Available) return null;
            try
            {
                byte[] buf = new byte[512];
                int written = predator_procsync_get_foreground_process_name(buf, buf.Length);
                return written > 0 ? Encoding.UTF8.GetString(buf, 0, written) : null;
            }
            catch { return null; }
        }

        #endregion

        #region Tier 2: Battery Descriptor Parsing

        public static bool TryParseBatteryInfo(byte[] rawBytes, out NativeRawBatteryInfo info)
        {
            info = default;
            if (!Available || rawBytes == null || rawBytes.Length < 36) return false;
            try
            {
                return predator_battery_parse_info(rawBytes, (UIntPtr)rawBytes.Length, out info) == 0;
            }
            catch { return false; }
        }

        #endregion

        #region Tier 2: UEFI Firmware Scanner

        public static bool TryScanUefiPowerAndLocks(byte[] aml, out NativeUefiPowerConfig cfg)
        {
            cfg = default;
            if (!Available || aml == null || aml.Length == 0) return false;
            try
            {
                return predator_uefi_scan_power_and_locks(aml, (UIntPtr)aml.Length, out cfg) == 0;
            }
            catch { return false; }
        }

        public static bool TryParseUefiVariablePower(byte[] data, out NativeUefiPowerConfig cfg)
        {
            cfg = default;
            if (!Available || data == null || data.Length == 0) return false;
            try
            {
                return predator_uefi_parse_variable_power(data, (UIntPtr)data.Length, out cfg) == 0;
            }
            catch { return false; }
        }

        #endregion

    }
}
