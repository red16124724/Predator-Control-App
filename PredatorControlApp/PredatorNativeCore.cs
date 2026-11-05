using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PredatorControlApp
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeTelemetrySnapshot
    {
        public double CpuLoad;
        public double GpuLoad;
        public double CpuTemp;
        public double GpuTemp;
        public uint CpuFanRpm;
        public uint GpuFanRpm;
        public uint CurrentPl1;
        public uint CurrentPl2;
        public ulong TimestampMs;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeControlPoint
    {
        public int Temp;
        public int Speed;

        public NativeControlPoint(int temp, int speed)
        {
            Temp = temp;
            Speed = speed;
        }

        public static NativeControlPoint FromPoint(Point p) => new(p.X, p.Y);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeThermalDecision
    {        public int Stage;
        [MarshalAs(UnmanagedType.I1)]
        public bool Active;
        [MarshalAs(UnmanagedType.I1)]
        public bool Critical;
        private readonly byte _pad0_0;
        private readonly byte _pad0_1;
        public int CpuFanFloor;
        public int GpuFanFloor;
        public int CpuPowerCapWatts;
        private readonly uint _pad1;
        public double CpuTemp;
        public double GpuTemp;

        public bool IsActive => Active;
        public bool IsCritical => Critical;
    }

    /// <summary>
    /// Ordered two-store plan for the package RAPL MMIO limit register, produced by
    /// <c>pawnio.rs</c>. Written in C# order: <see cref="FirstAddress"/> then
    /// <see cref="SecondAddress"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeMchbarWritePlan
    {
        public ulong MirroredValue;
        public ulong FirstAddress;
        public ulong SecondAddress;
        public uint FirstValue;
        public uint SecondValue;
        public uint FirstPreviousValue;
        public int Status;

        public const int StatusOk = 0;
        public const int StatusReadFailed = 1;
        public const int StatusBiosLocked = 2;
    }

    public static class PredatorNativeCore
    {
        private const string DllName = "predator_core.dll";
        private const string VersionString = "v1.5.5";
        private static IntPtr _nativeLibraryHandle = IntPtr.Zero;
        private static readonly bool _isLoaded;
        private static readonly object _syncLock = new();

        private static volatile bool _circuitBreakerTripped;
        private static readonly List<GCHandle> _pinnedDelegateHandles = new();
        private static readonly List<Delegate> _pinnedDelegates = new();

        public static bool IsLoaded => _isLoaded;
        public static string LoadSource => _isLoaded ? "Embedded, verified" : "Unavailable";
        public static bool IsNativeCoreAvailable => _isLoaded && !_circuitBreakerTripped;
        internal static void ResetCircuitBreakerForTesting() => _circuitBreakerTripped = false;

        /// <summary>
        /// Pins a callback delegate for foreign native interop, preventing GC collection and CallbackOnCollectedDelegate MDA crashes.
        /// </summary>
        public static GCHandle PinCallback(Delegate callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (_syncLock)
            {
                _pinnedDelegates.Add(callback);
                var handle = GCHandle.Alloc(callback);
                _pinnedDelegateHandles.Add(handle);
                return handle;
            }
        }

        /// <summary>
        /// Unpins a callback delegate handle, releasing native root and allowing GC collection.
        /// </summary>
        public static void UnpinCallback(ref GCHandle handle)
        {
            if (!handle.IsAllocated) return;
            var h = handle;
            handle = default;
            UnpinCallback(h);
        }

        public static void UnpinCallback(GCHandle handle)
        {
            if (!handle.IsAllocated) return;
            lock (_syncLock)
            {
                if (_pinnedDelegateHandles.Remove(handle))
                {
                    try
                    {
                        object? target = handle.Target;
                        if (target is Delegate del)
                        {
                            _pinnedDelegates.Remove(del);
                        }
                    }
                    catch { }
                    finally
                    {
                        if (handle.IsAllocated)
                        {
                            handle.Free();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Unpins a callback delegate, releasing native root and allowing GC collection.
        /// </summary>
        public static void UnpinCallback(Delegate callback)
        {
            if (callback == null) return;
            lock (_syncLock)
            {
                _pinnedDelegates.Remove(callback);
                for (int i = _pinnedDelegateHandles.Count - 1; i >= 0; i--)
                {
                    var h = _pinnedDelegateHandles[i];
                    if (h.IsAllocated)
                    {
                        try
                        {
                            if (ReferenceEquals(h.Target, callback))
                            {
                                _pinnedDelegateHandles.RemoveAt(i);
                                h.Free();
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private static void HandleNativeException(Exception ex)
        {
            if (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                _circuitBreakerTripped = true;
            }
        }

        static PredatorNativeCore()
        {
            try
            {
                Assembly currentAssembly = typeof(PredatorNativeCore).Assembly;

                // Only the DLL embedded in this application is trusted.
                string? resourceName = null;
                foreach (string name in currentAssembly.GetManifestResourceNames())
                {
                    if (name.EndsWith(DllName, StringComparison.OrdinalIgnoreCase))
                    {
                        resourceName = name;
                        break;
                    }
                }

                if (resourceName != null)
                {
                    using Stream? resource = currentAssembly.GetManifestResourceStream(resourceName);
                    if (resource != null)
                    {
                        using var resourceBytes = new MemoryStream();
                        resource.CopyTo(resourceBytes);
                        byte[] expected = SHA256.HashData(resourceBytes.ToArray());
                        string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "PredatorControlApp", "native", VersionString, Convert.ToHexString(expected));
                        string targetPath = Path.Combine(targetDir, DllName);
                        using var extractMutex = new Mutex(false, @"Local\PredatorControlApp_Dll_Extract_Lock");
                        bool acquired;
                        try { acquired = extractMutex.WaitOne(TimeSpan.FromSeconds(5)); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) return;
                        try
                        {
                            Directory.CreateDirectory(targetDir);
                            bool matches = false;
                            if (File.Exists(targetPath))
                            {
                                using var existing = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                                matches = SHA256.HashData(existing).AsSpan().SequenceEqual(expected);
                            }
                            if (!matches)
                            {
                                string tempPath = Path.Combine(targetDir, $"{DllName}.{Guid.NewGuid():N}.tmp");
                                try
                                {
                                    File.WriteAllBytes(tempPath, resourceBytes.ToArray());
                                    File.Move(tempPath, targetPath, overwrite: true);
                                }
                                finally
                                {
                                    if (File.Exists(tempPath)) File.Delete(tempPath);
                                }
                            }
                            // Deny modification while checking and loading the verified file.
                            using var verified = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            if (SHA256.HashData(verified).AsSpan().SequenceEqual(expected) &&
                                NativeLibrary.TryLoad(targetPath, out _nativeLibraryHandle))
                                _isLoaded = _nativeLibraryHandle != IntPtr.Zero;
                        }
                        finally { extractMutex.ReleaseMutex(); }
                    }
                }

                // 4. Register DllImportResolver so all P/Invoke calls route to loaded library
                if (_isLoaded && _nativeLibraryHandle != IntPtr.Zero)
                {
                    try
                    {
                        NativeLibrary.SetDllImportResolver(currentAssembly, (libraryName, asm, searchPath) =>
                        {
                            if (libraryName.Equals(DllName, StringComparison.OrdinalIgnoreCase) ||
                                libraryName.Equals("predator_core", StringComparison.OrdinalIgnoreCase))
                            {
                                return _nativeLibraryHandle;
                            }
                            return IntPtr.Zero;
                        });
                    }
                    catch
                    {
                        // Resolver already registered
                    }

                }
            }
            catch
            {
                _isLoaded = false;
            }
        }

        #region Core P/Invoke Signatures

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_core_init();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_core_cleanup();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_pawnio_is_available();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_pawnio_read_msr(uint msr, out ulong outVal);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_pawnio_write_msr(uint msr, ulong val);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_pawnio_read_mmio32(ulong addr, out uint outVal);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_pawnio_write_mmio32(ulong addr, uint val);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_pawnio_apply_power_limits(uint targetPl1, uint targetPl2, out int mmioMirrored);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_pawnio_plan_msr_write(ulong currentMsrLimit, ulong rawPl1, ulong rawPl2, out ulong outValue);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_pawnio_plan_mchbar_write(
            ulong currentMmio,
            int readOk,
            ulong rawPl1,
            ulong rawPl2,
            ulong fallbackTau1,
            ulong fallbackTau2,
            ulong pl1Address,
            ulong pl2Address,
            out NativeMchbarWritePlan outPlan);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_telemetry_get_snapshot(out NativeTelemetrySnapshot outSnapshot);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_telemetry_set_interval(uint ms);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr predator_curve_create(int hysteresis);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_curve_update(IntPtr handle, double temp, [In] NativeControlPoint[] points, nuint pointCount, int safetyFloor);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_curve_interpolate([In] NativeControlPoint[] points, nuint pointCount, int temp);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_curve_reset(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_curve_destroy(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr predator_thermal_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_thermal_destroy(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_thermal_evaluate(IntPtr handle, double cpuTemp, double gpuTemp, int targetPl1, int minPower, int baseTdp, out NativeThermalDecision outDecision);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_thermal_set_profile(
            IntPtr handle,
            int enabled,
            double cpuWarn,
            double cpuCeiling,
            double cpuCritical,
            double gpuWarn,
            double gpuCeiling,
            double gpuCritical,
            double hysteresis,
            int maxFanFloor,
            int cpuPowerCapWatts,
            int minPowerCapWatts,
            int criticalFansFullSpeed,
            double emaAlpha);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_thermal_reset(IntPtr handle);

        #endregion

        #region Safe Managed Wrappers

        public static int Init()
        {
            if (!IsNativeCoreAvailable) return -1;
            try { return predator_core_init(); }
            catch (Exception ex) { HandleNativeException(ex); return -1; }
        }

        public static void Cleanup()
        {
            if (!IsNativeCoreAvailable) return;
            try { predator_core_cleanup(); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        public static bool PawnIoIsAvailable()
        {
            if (!IsNativeCoreAvailable) return false;
            try { return predator_pawnio_is_available() != 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static bool PawnIoReadMsr(uint msr, out ulong value)
        {
            value = 0;
            if (!IsNativeCoreAvailable) return false;
            try { return predator_pawnio_read_msr(msr, out value) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static bool PawnIoWriteMsr(uint msr, ulong value)
        {
            if (!IsNativeCoreAvailable) return false;
            try { return predator_pawnio_write_msr(msr, value) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static bool PawnIoReadMmio32(ulong addr, out uint value)
        {
            value = 0;
            if (!IsNativeCoreAvailable) return false;
            try { return predator_pawnio_read_mmio32(addr, out value) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static bool PawnIoWriteMmio32(ulong addr, uint value)
        {
            if (!IsNativeCoreAvailable) return false;
            try { return predator_pawnio_write_mmio32(addr, value) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static bool PawnIoApplyPowerLimits(uint pl1, uint pl2, out bool mmioMirrored)
        {
            mmioMirrored = false;
            if (!IsNativeCoreAvailable) return false;
            try
            {
                int res = predator_pawnio_apply_power_limits(pl1, pl2, out int mirrored);
                mmioMirrored = mirrored != 0;
                return res == 0;
            }
            catch (Exception ex)
            {
                HandleNativeException(ex);
                return false;
            }
        }

        /// <summary>
        /// Builds the MSR 0x610 payload. Returns false when the register is BIOS-locked
        /// or native code is unavailable, in which case the caller must use its fallback.
        /// </summary>
        public static bool PawnIoPlanMsrWrite(ulong currentMsrLimit, ulong rawPl1, ulong rawPl2, out ulong payload)
        {
            payload = 0;
            if (!IsNativeCoreAvailable) return false;
            try { return predator_pawnio_plan_msr_write(currentMsrLimit, rawPl1, rawPl2, out payload) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        /// <summary>
        /// Plans the hazard-free MMIO split write. Returns false when the plan cannot be
        /// produced (locked register or unreadable state); inspect <paramref name="plan"/>
        /// for the specific status.
        /// </summary>
        public static bool PawnIoPlanMchbarWrite(
            ulong currentMmio,
            bool readOk,
            ulong rawPl1,
            ulong rawPl2,
            ulong fallbackTau1,
            ulong fallbackTau2,
            ulong pl1Address,
            ulong pl2Address,
            out NativeMchbarWritePlan plan)
        {
            plan = default;
            if (!IsNativeCoreAvailable) return false;
            try
            {
                int status = predator_pawnio_plan_mchbar_write(
                    currentMmio, readOk ? 1 : 0, rawPl1, rawPl2,
                    fallbackTau1, fallbackTau2, pl1Address, pl2Address, out plan);
                return status == NativeMchbarWritePlan.StatusOk;
            }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static bool TelemetryGetSnapshot(out NativeTelemetrySnapshot snapshot)
        {
            snapshot = default;
            if (!IsNativeCoreAvailable) return false;
            try
            {
                int rc = predator_telemetry_get_snapshot(out snapshot);
                if (rc == -2 && Init() == 0)
                {
                    rc = predator_telemetry_get_snapshot(out snapshot);
                }
                return rc == 0;
            }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static void TelemetrySetInterval(uint ms)
        {
            if (!IsNativeCoreAvailable) return;
            try { if (Init() == 0) predator_telemetry_set_interval(ms); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        public static IntPtr CurveCreate(int hysteresis)
        {
            if (!IsNativeCoreAvailable) return IntPtr.Zero;
            try { return predator_curve_create(hysteresis); }
            catch (Exception ex) { HandleNativeException(ex); return IntPtr.Zero; }
        }

        public static int CurveUpdate(IntPtr handle, double temp, NativeControlPoint[] points, int safetyFloor)
        {
            int fallback = Math.Max(50, Math.Clamp(safetyFloor, 0, 100));
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero || points == null || points.Length == 0) return fallback;
            try { return predator_curve_update(handle, temp, points, (nuint)points.Length, safetyFloor); }
            catch (Exception ex) { HandleNativeException(ex); return fallback; }
        }

        public static int CurveInterpolate(NativeControlPoint[] points, int temp)
        {
            if (!IsNativeCoreAvailable || points == null || points.Length == 0) return 50;
            try { return predator_curve_interpolate(points, (nuint)points.Length, temp); }
            catch (Exception ex) { HandleNativeException(ex); return 50; }
        }

        public static void CurveReset(IntPtr handle)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero) return;
            try { predator_curve_reset(handle); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        public static void CurveDestroy(IntPtr handle)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero) return;
            try { predator_curve_destroy(handle); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        /// <summary>
        /// Allocates an independent native thermal coordinator. Each instance owns its
        /// own latches, filtered temperatures, and fan floors; release it with
        /// <see cref="ThermalDestroy"/>.
        /// </summary>
        public static IntPtr ThermalCreate()
        {
            if (!IsNativeCoreAvailable) return IntPtr.Zero;
            try { return predator_thermal_create(); }
            catch (Exception ex) { HandleNativeException(ex); return IntPtr.Zero; }
        }

        /// <summary>Releases a coordinator returned by <see cref="ThermalCreate"/>.</summary>
        public static void ThermalDestroy(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !IsNativeCoreAvailable) return;
            try { predator_thermal_destroy(handle); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        public static bool ThermalEvaluate(IntPtr handle, double cpuTemp, double gpuTemp, int targetPl1, int minPower, int baseTdp, out NativeThermalDecision decision)
        {
            decision = default;
            if (handle == IntPtr.Zero || !IsNativeCoreAvailable) return false;
            try { return predator_thermal_evaluate(handle, cpuTemp, gpuTemp, targetPl1, minPower, baseTdp, out decision) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        /// <summary>
        /// Pushes the user-visible thermal profile into the native coordinator so the
        /// Rust policy evaluates the same thresholds as <c>ThermalSafetyCoordinator</c>.
        /// </summary>
        public static bool ThermalSetProfile(
            IntPtr handle,
            bool enabled,
            double cpuWarn,
            double cpuCeiling,
            double cpuCritical,
            double gpuWarn,
            double gpuCeiling,
            double gpuCritical,
            double hysteresis,
            int maxFanFloor,
            int cpuPowerCapWatts,
            int minPowerCapWatts,
            bool criticalFansFullSpeed,
            double emaAlpha)
        {
            if (handle == IntPtr.Zero || !IsNativeCoreAvailable) return false;
            try
            {
                return predator_thermal_set_profile(
                    handle,
                    enabled ? 1 : 0,
                    cpuWarn,
                    cpuCeiling,
                    cpuCritical,
                    gpuWarn,
                    gpuCeiling,
                    gpuCritical,
                    hysteresis,
                    maxFanFloor,
                    cpuPowerCapWatts,
                    minPowerCapWatts,
                    criticalFansFullSpeed ? 1 : 0,
                    emaAlpha) == 0;
            }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        /// <summary>Clears native latches, filtered temperatures, and fan floors.</summary>
        public static bool ThermalReset(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !IsNativeCoreAvailable) return false;
            try { return predator_thermal_reset(handle) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        #endregion

        #region Rust RGB animation / audio FFT / NVAPI / ACPI / EC-HID

        /// <summary>Largest zone count the native animator can render in one frame.</summary>
        public const int MaxNativeZones = 8;

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePpccEntry
        {
            public int Index;
            public int MinMilliwatts;
            public int MaxMilliwatts;
            public int MinTimeWindowMs;
            public int MaxTimeWindowMs;
            public int StepMilliwatts;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePpccSummary
        {
            public int MinPowerWatts;
            public int MaxPl1Watts;
            public int MaxPl2Watts;
            public int BaseTdpWatts;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeAcpiTable
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] Signature;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] OemTableId;
            public uint Length;
            public byte Revision;
            private readonly byte _pad0;
            private readonly byte _pad1;
            private readonly byte _pad2;
            public uint Offset;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr predator_rgb_create(int effect, byte r, byte g, byte b, byte brightness, byte speed, byte direction, nuint zones);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_rgb_next_frame(IntPtr handle, [Out] byte[] outRgb, nuint zones);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_rgb_render_frame(int effect, ulong step, byte r, byte g, byte b, byte brightness, [Out] byte[] outRgb, nuint zones);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern uint predator_rgb_frame_delay_ms(byte speed);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_rgb_set_brightness(IntPtr handle, byte brightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_rgb_reset(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_rgb_destroy(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_rgb_hsv_to_rgb(double hue, double saturation, double value, [Out] byte[] outRgb);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_rgb_blend([In, Out] byte[] current, [In] byte[] target, nuint zones, float t);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_rgb_apply_brightness([In, Out] byte[] zones, nuint count, byte brightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr predator_audio_create(uint sampleRate, nuint fftSize, nuint bandCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_audio_destroy(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_audio_push(IntPtr handle, [In] float[] samples, nuint count, nuint channels);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_audio_get_bands(IntPtr handle, [Out] float[] outBands, nuint capacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern float predator_audio_get_level(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_audio_is_beat(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern float predator_audio_beat_strength(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_audio_render_zones(IntPtr handle, byte r, byte g, byte b, byte brightness, [Out] byte[] outRgb, nuint zones);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_audio_reset(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_nvapi_is_available();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_nvapi_get_gpu_load(out double load);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_nvapi_get_gpu_temperature(out double temperature);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern uint predator_nvapi_get_gpu_count();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_nvapi_shutdown();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_acpi_ppcc_entries([In] byte[] aml, nuint length, [Out] NativePpccEntry[] outEntries, nuint capacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_acpi_ppcc_summary([In] byte[] aml, nuint length, out NativePpccSummary summary);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_acpi_scan_tables([In] byte[] blob, nuint length, [Out] NativeAcpiTable[] outTables, nuint capacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_echid_open(ushort vendorId, ushort productId, ushort usagePage, ushort usage);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void predator_echid_close();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_echid_is_open();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_echid_report_length();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_echid_exchange([In] byte[] request, nuint requestLength, [Out] byte[] reply, nuint replyCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int predator_echid_enumerate(nuint limit);

        // ---- RGB animation ----

        public static IntPtr RgbCreate(int effect, byte r, byte g, byte b, byte brightness, byte speed, byte direction, int zones)
        {
            if (!IsNativeCoreAvailable) return IntPtr.Zero;
            try { return predator_rgb_create(effect, r, g, b, brightness, speed, direction, (nuint)Math.Clamp(zones, 1, MaxNativeZones)); }
            catch (Exception ex) { HandleNativeException(ex); return IntPtr.Zero; }
        }

        /// <summary>Renders the next animation frame into <paramref name="frameRgb"/> (zones * 3 bytes).</summary>
        public static int RgbNextFrame(IntPtr handle, byte[] frameRgb, int zones)
        {
            int clampedZones = Math.Clamp(zones, 1, MaxNativeZones);
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero || frameRgb == null || frameRgb.Length < clampedZones * 3) return -1;
            try { return predator_rgb_next_frame(handle, frameRgb, (nuint)clampedZones); }
            catch (Exception ex) { HandleNativeException(ex); return -1; }
        }

        public static int RgbRenderFrame(int effect, ulong step, byte r, byte g, byte b, byte brightness, byte[] frameRgb, int zones)
        {
            int clampedZones = Math.Clamp(zones, 1, MaxNativeZones);
            if (!IsNativeCoreAvailable || frameRgb == null || frameRgb.Length < clampedZones * 3) return -1;
            try { return predator_rgb_render_frame(effect, step, r, g, b, brightness, frameRgb, (nuint)clampedZones); }
            catch (Exception ex) { HandleNativeException(ex); return -1; }
        }

        public static uint RgbFrameDelayMs(byte speed)
        {
            if (!IsNativeCoreAvailable) return 0;
            try { return predator_rgb_frame_delay_ms(speed); }
            catch (Exception ex) { HandleNativeException(ex); return 0; }
        }

        public static void RgbSetBrightness(IntPtr handle, byte brightness)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero) return;
            try { predator_rgb_set_brightness(handle, brightness); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        public static void RgbReset(IntPtr handle)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero) return;
            try { predator_rgb_reset(handle); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        public static void RgbDestroy(IntPtr handle)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero) return;
            try { predator_rgb_destroy(handle); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        public static bool RgbHsvToRgb(double hue, double saturation, double value, byte[] outRgb)
        {
            if (!IsNativeCoreAvailable || outRgb == null || outRgb.Length < 3) return false;
            try { return predator_rgb_hsv_to_rgb(hue, saturation, value, outRgb) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static bool RgbBlend(byte[] current, byte[] target, int zones, float t)
        {
            if (!IsNativeCoreAvailable || current == null || target == null) return false;
            if (current.Length < zones * 3 || target.Length < zones * 3 || zones <= 0) return false;
            try { return predator_rgb_blend(current, target, (nuint)Math.Min(zones, MaxNativeZones), t) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static bool RgbApplyBrightness(byte[] zones, int count, byte brightness)
        {
            if (!IsNativeCoreAvailable || zones == null || count <= 0) return false;
            if (zones.Length < count * 3) return false;
            try { return predator_rgb_apply_brightness(zones, (nuint)Math.Min(count, MaxNativeZones), brightness) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        // ---- Audio spectrum ----

        public static IntPtr AudioCreate(uint sampleRate, int fftSize, int bandCount)
        {
            if (!IsNativeCoreAvailable) return IntPtr.Zero;
            try { return predator_audio_create(sampleRate, (nuint)Math.Max(64, fftSize), (nuint)Math.Clamp(bandCount, 1, 64)); }
            catch (Exception ex) { HandleNativeException(ex); return IntPtr.Zero; }
        }

        public static void AudioDestroy(IntPtr handle)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero) return;
            try { predator_audio_destroy(handle); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        public static bool AudioPush(IntPtr handle, float[] samples, int count, int channels)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero || samples == null || count <= 0) return false;
            if (count > samples.Length) count = samples.Length;
            try { return predator_audio_push(handle, samples, (nuint)count, (nuint)Math.Max(1, channels)) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static int AudioGetBands(IntPtr handle, float[] outBands)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero || outBands == null || outBands.Length == 0) return -1;
            try { return predator_audio_get_bands(handle, outBands, (nuint)outBands.Length); }
            catch (Exception ex) { HandleNativeException(ex); return -1; }
        }

        public static float AudioGetLevel(IntPtr handle)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero) return 0f;
            try { return predator_audio_get_level(handle); }
            catch (Exception ex) { HandleNativeException(ex); return 0f; }
        }

        public static bool AudioIsBeat(IntPtr handle)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero) return false;
            try { return predator_audio_is_beat(handle) != 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static float AudioBeatStrength(IntPtr handle)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero) return 0f;
            try { return predator_audio_beat_strength(handle); }
            catch (Exception ex) { HandleNativeException(ex); return 0f; }
        }

        public static int AudioRenderZones(IntPtr handle, byte r, byte g, byte b, byte brightness, byte[] frameRgb, int zones)
        {
            int clampedZones = Math.Clamp(zones, 1, MaxNativeZones);
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero || frameRgb == null || frameRgb.Length < clampedZones * 3) return -1;
            try { return predator_audio_render_zones(handle, r, g, b, brightness, frameRgb, (nuint)clampedZones); }
            catch (Exception ex) { HandleNativeException(ex); return -1; }
        }

        public static void AudioReset(IntPtr handle)
        {
            if (!IsNativeCoreAvailable || handle == IntPtr.Zero) return;
            try { predator_audio_reset(handle); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        // ---- NVAPI ----

        public static bool NvApiIsAvailable()
        {
            if (!IsNativeCoreAvailable) return false;
            try { return predator_nvapi_is_available() != 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static bool NvApiGetGpuLoad(out double load)
        {
            load = 0;
            if (!IsNativeCoreAvailable) return false;
            try { return predator_nvapi_get_gpu_load(out load) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static bool NvApiGetGpuTemperature(out double temperature)
        {
            temperature = 0;
            if (!IsNativeCoreAvailable) return false;
            try { return predator_nvapi_get_gpu_temperature(out temperature) == 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static uint NvApiGetGpuCount()
        {
            if (!IsNativeCoreAvailable) return 0;
            try { return predator_nvapi_get_gpu_count(); }
            catch (Exception ex) { HandleNativeException(ex); return 0; }
        }

        public static void NvApiShutdown()
        {
            if (!IsNativeCoreAvailable) return;
            try { predator_nvapi_shutdown(); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        // ---- ACPI / AML ----

        /// <summary>Parses PPCC power-limit entries from a raw AML table.</summary>
        public static int AcpiParsePpccEntries(byte[] aml, NativePpccEntry[] outEntries)
        {
            if (!IsNativeCoreAvailable || aml == null || aml.Length == 0) return -1;
            try
            {
                if (outEntries == null || outEntries.Length == 0)
                    return predator_acpi_ppcc_entries(aml, (nuint)aml.Length, Array.Empty<NativePpccEntry>(), 0);
                return predator_acpi_ppcc_entries(aml, (nuint)aml.Length, outEntries, (nuint)outEntries.Length);
            }
            catch (Exception ex) { HandleNativeException(ex); return -1; }
        }

        public static bool AcpiParsePpccSummary(byte[] aml, out NativePpccSummary summary)
        {
            summary = default;
            if (!IsNativeCoreAvailable || aml == null || aml.Length == 0) return false;
            try { return predator_acpi_ppcc_summary(aml, (nuint)aml.Length, out summary) == 1; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static int AcpiScanTables(byte[] blob, NativeAcpiTable[] outTables)
        {
            if (!IsNativeCoreAvailable || blob == null || blob.Length == 0) return -1;
            try
            {
                if (outTables == null || outTables.Length == 0)
                    return predator_acpi_scan_tables(blob, (nuint)blob.Length, Array.Empty<NativeAcpiTable>(), 0);
                return predator_acpi_scan_tables(blob, (nuint)blob.Length, outTables, (nuint)outTables.Length);
            }
            catch (Exception ex) { HandleNativeException(ex); return -1; }
        }

        // ---- EC-HID transport ----

        public static bool EcHidOpen(ushort vendorId, ushort productId, ushort usagePage, ushort usage)
        {
            if (!IsNativeCoreAvailable) return false;
            try { return predator_echid_open(vendorId, productId, usagePage, usage) != 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static void EcHidClose()
        {
            if (!IsNativeCoreAvailable) return;
            try { predator_echid_close(); }
            catch (Exception ex) { HandleNativeException(ex); }
        }

        public static bool EcHidIsOpen()
        {
            if (!IsNativeCoreAvailable) return false;
            try { return predator_echid_is_open() != 0; }
            catch (Exception ex) { HandleNativeException(ex); return false; }
        }

        public static int EcHidReportLength()
        {
            if (!IsNativeCoreAvailable) return 0;
            try { return predator_echid_report_length(); }
            catch (Exception ex) { HandleNativeException(ex); return 0; }
        }

        /// <summary>Runs one EC feature-report exchange; returns reply byte count or a negative code.</summary>
        public static int EcHidExchange(byte[] request, byte[] reply)
        {
            if (!IsNativeCoreAvailable || request == null || reply == null) return -1;
            try { return predator_echid_exchange(request, (nuint)request.Length, reply, (nuint)reply.Length); }
            catch (Exception ex) { HandleNativeException(ex); return -1; }
        }

        public static int EcHidEnumerate(int limit)
        {
            if (!IsNativeCoreAvailable) return 0;
            try { return predator_echid_enumerate((nuint)Math.Max(1, limit)); }
            catch (Exception ex) { HandleNativeException(ex); return 0; }
        }

        #endregion
    }
}
