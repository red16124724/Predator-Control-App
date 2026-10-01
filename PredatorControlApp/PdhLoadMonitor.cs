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
        private const uint PDH_MORE_DATA = 0x800007D2;

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

        [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(
            IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr pItemBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double doubleValue;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PDH_FMT_COUNTERVALUE_ITEM
        {
            public IntPtr szName;
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        private IntPtr _query = IntPtr.Zero;
        private IntPtr _cpuCounter = IntPtr.Zero;
        private IntPtr _gpuCounter = IntPtr.Zero;
        private bool _isDisposed;

        public PdhLoadMonitor()
        {
            Init();
        }

        private void Init()
        {
            try
            {
                if (PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out _query) == 0)
                {
                    // CPU Counter
                    if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpuCounter) != 0)
                    {
                        PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpuCounter);
                    }

                    // GPU 3D Engine Counter
                    PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter);

                    // Initial collection to prime the counter
                    PdhCollectQueryData(_query);
                }
            }
            catch { }
        }

        public (double? Cpu, double? Gpu) Sample()
        {
            if (_isDisposed || _query == IntPtr.Zero) return (null, null);

            try
            {
                if (PdhCollectQueryData(_query) != 0) return (null, null);

                double? cpu = null;
                if (_cpuCounter != IntPtr.Zero)
                {
                    if (PdhGetFormattedCounterValue(_cpuCounter, PDH_FMT_DOUBLE, out _, out var val) == 0 && val.CStatus <= 1)
                    {
                        cpu = Math.Clamp(val.doubleValue, 0.0, 100.0);
                    }
                }

                double? gpu = SampleGpu();
                return (cpu, gpu);
            }
            catch
            {
                return (null, null);
            }
        }

        private double? SampleGpu()
        {
            if (_gpuCounter == IntPtr.Zero) return null;

            try
            {
                uint bufSize = 0;
                uint itemCount = 0;
                uint status = PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref bufSize, ref itemCount, IntPtr.Zero);
                if (status != PDH_MORE_DATA && status != 0) return null;
                if (bufSize == 0 || itemCount == 0) return 0.0;

                IntPtr pBuf = Marshal.AllocHGlobal((int)bufSize);
                try
                {
                    if (PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref bufSize, ref itemCount, pBuf) == 0)
                    {
                        double maxVal = 0.0;
                        int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                        for (int i = 0; i < itemCount; i++)
                        {
                            IntPtr pItem = (IntPtr)((long)pBuf + (i * itemSize));
                            var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(pItem);
                            if (item.FmtValue.CStatus <= 1)
                            {
                                if (item.FmtValue.doubleValue > maxVal)
                                    maxVal = item.FmtValue.doubleValue;
                            }
                        }
                        return Math.Clamp(maxVal, 0.0, 100.0);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pBuf);
                }
            }
            catch { }
            return null;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try
            {
                if (_query != IntPtr.Zero)
                {
                    PdhCloseQuery(_query);
                    _query = IntPtr.Zero;
                }
            }
            catch { }
        }
    }
}
