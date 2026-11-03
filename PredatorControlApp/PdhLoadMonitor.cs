using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public sealed class PdhLoadMonitor : IDisposable
    {
        private const uint PDH_FMT_DOUBLE = 0x00000200;

        [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(IntPtr szDataSource, IntPtr dwUserData, out IntPtr phQuery);

        [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern uint PdhCollectQueryData(IntPtr hQuery);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern uint PdhCloseQuery(IntPtr hQuery);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
            public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double doubleValue;
        }

        private IntPtr _query = IntPtr.Zero;
        private IntPtr _cpuCounter = IntPtr.Zero;
        private bool _isDisposed;
        private readonly object _lock = new();
        private long _lastInitAttemptTick;
        private long _lastCollectTick;
        private int _consecutiveErrors;
        private ulong _prevIdleTime;
        private ulong _prevKernelTime;
        private ulong _prevUserTime;
        private bool _hasSystemTimesBaseline;

        private double? SampleCpuViaSystemTimes()
        {
            try
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;

                ulong idleU = idle.ToUInt64();
                ulong kernelU = kernel.ToUInt64();
                ulong userU = user.ToUInt64();

                if (!_hasSystemTimesBaseline)
                {
                    _prevIdleTime = idleU;
                    _prevKernelTime = kernelU;
                    _prevUserTime = userU;
                    _hasSystemTimesBaseline = true;
                    return null;
                }

                ulong idleDelta = idleU >= _prevIdleTime ? idleU - _prevIdleTime : 0;
                ulong kernelDelta = kernelU >= _prevKernelTime ? kernelU - _prevKernelTime : 0;
                ulong userDelta = userU >= _prevUserTime ? userU - _prevUserTime : 0;

                _prevIdleTime = idleU;
                _prevKernelTime = kernelU;
                _prevUserTime = userU;

                ulong totalSystem = kernelDelta + userDelta;
                if (totalSystem == 0) return null;

                if (idleDelta >= totalSystem) return 0.0;
                double load = (1.0 - ((double)idleDelta / totalSystem)) * 100.0;
                return Math.Clamp(load, 0.0, 100.0);
            }
            catch
            {
                return null;
            }
        }

        #region NVAPI Direct Hardware Interop (Zero DPC Latency / Zero Dxgkrnl Stalls)

        private static volatile bool _nvapiInitialized;
        private static volatile bool _nvapiAvailable;
        private static volatile IntPtr[]? _nvGpuHandles;
        private static volatile int _nvGpuCount;

        [StructLayout(LayoutKind.Sequential)]
        private struct NV_USAGES
        {
            public uint Version;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
            public uint[] Usages;
        }

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")]
        private static extern IntPtr NvAPI_QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_Initialize_Delegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_EnumPhysicalGPUs_Delegate([Out] IntPtr[] gpuHandles, out int gpuCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_GPU_GetUsages_Delegate(IntPtr gpuHandle, ref NV_USAGES usages);

        private static volatile NvAPI_GPU_GetUsages_Delegate? _nvGetUsages;
        private static readonly uint s_nvUsagesVersion = (uint)(Marshal.SizeOf<NV_USAGES>() | 0x10000);
        [ThreadStatic]
        private static NV_USAGES s_reusableUsages;

        private static readonly object s_nvapiLock = new();
        private static int s_nvapiConsecutiveFailures = 0;

        private static void EnsureNvApi()
        {
            if (_nvapiInitialized) return;
            lock (s_nvapiLock)
            {
                if (_nvapiInitialized) return;
                try
                {
                    IntPtr pInit = NvAPI_QueryInterface(0x0150E828); // NvAPI_Initialize
                    if (pInit == IntPtr.Zero) return;
                    var init = Marshal.GetDelegateForFunctionPointer<NvAPI_Initialize_Delegate>(pInit);
                    if (init() != 0) return;

                    IntPtr pEnum = NvAPI_QueryInterface(0xE5AC92AA); // NvAPI_EnumPhysicalGPUs
                    if (pEnum == IntPtr.Zero) return;
                    var enumGpus = Marshal.GetDelegateForFunctionPointer<NvAPI_EnumPhysicalGPUs_Delegate>(pEnum);
                    var handles = new IntPtr[64];
                    if (enumGpus(handles, out int count) != 0 || count == 0) return;

                    IntPtr pUsages = NvAPI_QueryInterface(0x189A1FDF); // NvAPI_GPU_GetUsages standard ID
                    if (pUsages == IntPtr.Zero) pUsages = NvAPI_QueryInterface(0x189A1F42); // Fallback ID
                    if (pUsages == IntPtr.Zero) return;
                    _nvGetUsages = Marshal.GetDelegateForFunctionPointer<NvAPI_GPU_GetUsages_Delegate>(pUsages);

                    _nvGpuHandles = handles;
                    _nvGpuCount = Math.Clamp(count, 0, handles.Length);
                    _nvapiAvailable = true;
                }
                catch { }
                finally
                {
                    _nvapiInitialized = true;
                }
            }
        }

        private static double? TrySampleNvApi()
        {
            // Native NVAPI bridge (predator_core::nvapi) is preferred: it resolves the
            // interface IDs once into plain function pointers and reads usages through a
            // single indirect call on a stack-allocated struct, with no marshalling or
            // delegate dispatch. The managed path below remains as a fallback.
            if (PredatorNativeCore.IsNativeCoreAvailable && PredatorNativeCore.NvApiGetGpuLoad(out double nativeLoad))
            {
                return Math.Clamp(nativeLoad, 0.0, 100.0);
            }

            EnsureNvApi();
            if (!_nvapiAvailable || _nvGetUsages == null || _nvGpuHandles == null || _nvGpuCount == 0)
                return null;

            lock (s_nvapiLock)
            {
                try
                {
                    if (s_reusableUsages.Usages == null)
                    {
                        s_reusableUsages = new NV_USAGES
                        {
                            Version = s_nvUsagesVersion,
                            Usages = new uint[33]
                        };
                    }

                    double maxUsage = 0;
                    bool anyRead = false;
                    for (int i = 0; i < _nvGpuCount && i < _nvGpuHandles.Length; i++)
                    {
                        s_reusableUsages.Version = s_nvUsagesVersion;
                        if (s_reusableUsages.Usages != null) Array.Clear(s_reusableUsages.Usages, 0, s_reusableUsages.Usages.Length);

                        if (_nvGetUsages(_nvGpuHandles[i], ref s_reusableUsages) == 0)
                        {
                            if (s_reusableUsages.Usages != null && s_reusableUsages.Usages.Length > 3)
                            {
                                uint gpuCoreUsage = s_reusableUsages.Usages[3]; // Index 3 is GPU Core load
                                if (gpuCoreUsage <= 100)
                                {
                                    if (gpuCoreUsage > maxUsage) maxUsage = gpuCoreUsage;
                                    anyRead = true;
                                }
                            }
                        }
                    }
                    if (anyRead)
                    {
                        s_nvapiConsecutiveFailures = 0;
                        return Math.Clamp(maxUsage, 0.0, 100.0);
                    }
                    else
                    {
                        if (++s_nvapiConsecutiveFailures >= 3)
                        {
                            // Reset state so handles re-enumerate after waking from sleep or driver reload
                            _nvapiInitialized = false;
                            _nvapiAvailable = false;
                            s_nvapiConsecutiveFailures = 0;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        #endregion

        public PdhLoadMonitor()
        {
            lock (_lock)
            {
                Init();
            }
        }

        private int _backoffMs = 1000;

        private void ResetQuery()
        {
            _consecutiveErrors = 0;
            _backoffMs = Math.Min(_backoffMs * 2, 5000);
            ReleaseQuery();
            _lastInitAttemptTick = Environment.TickCount64;
        }

        private void Init()
        {
            ReleaseQuery();
            _lastInitAttemptTick = Environment.TickCount64;
            try
            {
                uint status = PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out var queryHandle);
                if (status == 0 && queryHandle != IntPtr.Zero)
                {
                    _query = queryHandle;
                    _cpuCounter = IntPtr.Zero;

                    if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpuCounter) != 0 || _cpuCounter == IntPtr.Zero)
                    {
                        _cpuCounter = IntPtr.Zero;
                        if (PdhAddEnglishCounter(_query, @"\Processor Information(0,_Total)\% Processor Utility", IntPtr.Zero, out _cpuCounter) != 0 || _cpuCounter == IntPtr.Zero)
                        {
                            _cpuCounter = IntPtr.Zero;
                            if (PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpuCounter) != 0)
                            {
                                _cpuCounter = IntPtr.Zero;
                            }
                        }
                    }

                    if (_cpuCounter == IntPtr.Zero)
                    {
                        _backoffMs = Math.Min(_backoffMs * 2, 5000);
                        ReleaseQuery();
                        return;
                    }

                    // Initial collection to prime the counter
                    PdhCollectQueryData(_query);
                    _lastCollectTick = Environment.TickCount64;
                    _consecutiveErrors = 0;
                    _backoffMs = 1000;
                }
                else
                {
                    _query = IntPtr.Zero;
                    _backoffMs = Math.Min(_backoffMs * 2, 5000);
                }
            }
            catch
            {
                _query = IntPtr.Zero;
                _backoffMs = Math.Min(_backoffMs * 2, 5000);
                ReleaseQuery();
            }
        }

        public (double? Cpu, double? Gpu) Sample(bool sampleGpu = true)
        {
            if (_isDisposed) return (null, null);

            if (PredatorNativeCore.IsLoaded && PredatorNativeCore.TelemetryGetSnapshot(out var snapshot))
            {
                if (snapshot.CpuLoad >= 0 && snapshot.TimestampMs > 0)
                {
                    double cpuVal = Math.Clamp(snapshot.CpuLoad, 0.0, 100.0);
                    double? gpuVal = null;
                    if (sampleGpu && snapshot.GpuLoad >= 0)
                    {
                        gpuVal = Math.Clamp(snapshot.GpuLoad, 0.0, 100.0);
                    }
                    return (cpuVal, gpuVal);
                }
            }

            double? cpu = null;
            lock (_lock)
            {
                if (_isDisposed) return (null, null);

                if (_query == IntPtr.Zero && (Environment.TickCount64 - _lastInitAttemptTick) > _backoffMs)
                {
                    _lastInitAttemptTick = Environment.TickCount64;
                    Init();
                    if (_query != IntPtr.Zero)
                    {
                        _backoffMs = 1000;
                        // Avoid immediate collection right after Init priming to prevent 0ms delta PDH_INVALID_DATA.
                        cpu = SampleCpuViaSystemTimes();
                    }
                }

                if (_query != IntPtr.Zero && cpu == null)
                {
                    long now = Environment.TickCount64;
                    // Require at least 50ms interval between collections for valid rate calculation
                    if (now - _lastCollectTick >= 50)
                    {
                        try
                        {
                            uint colStatus = PdhCollectQueryData(_query);
                            if (colStatus == 0 && _cpuCounter != IntPtr.Zero)
                            {
                                _lastCollectTick = now;
                                if (PdhGetFormattedCounterValue(_cpuCounter, PDH_FMT_DOUBLE, out _, out var val) == 0 && val.CStatus <= 1 && !double.IsNaN(val.doubleValue) && !double.IsInfinity(val.doubleValue))
                                {
                                    cpu = Math.Clamp(val.doubleValue, 0.0, 100.0);
                                    _consecutiveErrors = 0;
                                    _backoffMs = 1000;
                                }
                                else
                                {
                                    if (++_consecutiveErrors >= 3)
                                    {
                                        ResetQuery();
                                    }
                                }
                            }
                            else
                            {
                                if (++_consecutiveErrors >= 3)
                                {
                                    ResetQuery();
                                }
                            }
                        }
                        catch
                        {
                            ResetQuery();
                        }
                    }
                }
            }

            if (cpu == null)
            {
                lock (_lock)
                {
                    cpu = SampleCpuViaSystemTimes();
                }
            }

            if (_isDisposed) return (null, null);
            double? gpu = sampleGpu ? SampleGpu() : null;
            return (cpu, gpu);
        }

        private double? SampleGpu()
        {
            // Direct hardware query via NVAPI (zero DPC latency, zero dxgkrnl calls/stalls)
            // PDH \GPU Engine counters are deliberately excluded to prevent dxgkrnl driver stalls and A/V desync
            if (GpuPowerMonitor.IsGpuAsleep()) return null;
            return TrySampleNvApi();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                ReleaseQuery();
            }
        }

        private void ReleaseQuery()
        {
            try
            {
                _cpuCounter = IntPtr.Zero;
                IntPtr q = Interlocked.Exchange(ref _query, IntPtr.Zero);
                if (q != IntPtr.Zero)
                {
                    PdhCloseQuery(q);
                }
            }
            catch { }
        }

        ~PdhLoadMonitor()
        {
            Dispose(false);
        }
    }
}
