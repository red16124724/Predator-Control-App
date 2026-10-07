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
        private DateTime _lastInitAttempt = DateTime.MinValue;

        #region NVAPI Direct Hardware Interop (Zero DPC Latency / Zero Dxgkrnl Stalls)

        private static bool _nvapiInitialized;
        private static bool _nvapiAvailable;
        private static IntPtr[]? _nvGpuHandles;
        private static int _nvGpuCount;

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

        private static NvAPI_GPU_GetUsages_Delegate? _nvGetUsages;
        private static readonly uint s_nvUsagesVersion = (uint)(Marshal.SizeOf<NV_USAGES>() | 0x10000);
        [ThreadStatic]
        private static NV_USAGES s_reusableUsages;

        private static readonly object s_nvapiLock = new();

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

                    IntPtr pUsages = NvAPI_QueryInterface(0x189A1F42); // NvAPI_GPU_GetUsages
                    if (pUsages == IntPtr.Zero) return;
                    _nvGetUsages = Marshal.GetDelegateForFunctionPointer<NvAPI_GPU_GetUsages_Delegate>(pUsages);

                    _nvGpuHandles = handles;
                    _nvGpuCount = Math.Min(count, handles.Length);
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
            EnsureNvApi();
            if (!_nvapiAvailable || _nvGetUsages == null || _nvGpuHandles == null || _nvGpuCount == 0)
                return null;

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
                for (int i = 0; i < _nvGpuCount; i++)
                {
                    s_reusableUsages.Version = s_nvUsagesVersion;
                    Array.Clear(s_reusableUsages.Usages, 0, s_reusableUsages.Usages.Length);

                    if (_nvGetUsages(_nvGpuHandles[i], ref s_reusableUsages) == 0)
                    {
                        uint gpuCoreUsage = s_reusableUsages.Usages[3]; // Index 3 is GPU Core load
                        if (gpuCoreUsage <= 100)
                        {
                            if (gpuCoreUsage > maxUsage) maxUsage = gpuCoreUsage;
                            anyRead = true;
                        }
                    }
                }
                if (anyRead) return Math.Clamp(maxUsage, 0.0, 100.0);
            }
            catch { }
            return null;
        }

        #endregion

        public PdhLoadMonitor()
        {
            Init();
        }

        private void Init()
        {
            if (_query != IntPtr.Zero)
            {
                try { PdhCloseQuery(_query); } catch { }
                _query = IntPtr.Zero;
                _cpuCounter = IntPtr.Zero;
            }

            _lastInitAttempt = DateTime.UtcNow;
            try
            {
                if (PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out _query) == 0)
                {
                    // CPU Counter
                    if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpuCounter) != 0)
                    {
                        if (PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpuCounter) != 0)
                        {
                            _cpuCounter = IntPtr.Zero;
                        }
                    }

                    if (_cpuCounter == IntPtr.Zero)
                    {
                        try { PdhCloseQuery(_query); } catch { }
                        _query = IntPtr.Zero;
                        return;
                    }

                    // Initial collection to prime the counter
                    PdhCollectQueryData(_query);
                }
                else
                {
                    _query = IntPtr.Zero;
                }
            }
            catch
            {
                if (_query != IntPtr.Zero)
                {
                    try { PdhCloseQuery(_query); } catch { }
                    _query = IntPtr.Zero;
                }
                _cpuCounter = IntPtr.Zero;
            }
        }

        public (double? Cpu, double? Gpu) Sample(bool sampleGpu = true)
        {
            if (_isDisposed) return (null, null);

            double? cpu = null;
            lock (_lock)
            {
                if (_isDisposed) return (null, null);

                if (_query == IntPtr.Zero && (DateTime.UtcNow - _lastInitAttempt).TotalSeconds > 5)
                {
                    _lastInitAttempt = DateTime.UtcNow;
                    Init();
                }

                if (_query != IntPtr.Zero)
                {
                    try
                    {
                        if (PdhCollectQueryData(_query) == 0 && _cpuCounter != IntPtr.Zero)
                        {
                            if (PdhGetFormattedCounterValue(_cpuCounter, PDH_FMT_DOUBLE, out _, out var val) == 0 && val.CStatus <= 1 && !double.IsNaN(val.doubleValue) && !double.IsInfinity(val.doubleValue))
                            {
                                cpu = Math.Clamp(val.doubleValue, 0.0, 100.0);
                            }
                        }
                    }
                    catch
                    {
                        try { PdhCloseQuery(_query); } catch { }
                        _query = IntPtr.Zero;
                        _cpuCounter = IntPtr.Zero;
                    }
                }
            }

            double? gpu = sampleGpu ? SampleGpu() : null;
            return (cpu, gpu);
        }

        private double? SampleGpu()
        {
            // Direct hardware query via NVAPI (zero DPC latency, zero dxgkrnl calls/stalls)
            // PDH \GPU Engine counters are deliberately excluded to prevent dxgkrnl driver stalls and A/V desync
            return TrySampleNvApi();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                try
                {
                    if (_query != IntPtr.Zero)
                    {
                        PdhCloseQuery(_query);
                        _query = IntPtr.Zero;
                        _cpuCounter = IntPtr.Zero;
                    }
                }
                catch { }
            }
            GC.SuppressFinalize(this);
        }

        ~PdhLoadMonitor()
        {
            Dispose();
        }
    }
}
