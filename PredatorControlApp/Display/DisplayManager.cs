using System;
using System.Collections.Generic;
using System.Linq;
using PredatorControlApp;

namespace PredatorControlApp.Display
{
    public enum DdsMode
    {
        Optimus = 0,
        Automatic = 1,
        NvidiaGpuOnly = 2
    }

    public enum BlueLightShieldLevel
    {
        Off = 0,
        Low = 1,      // 18% reduction
        High = 2,     // 36% reduction
        Ultra = 3     // 54% reduction
    }

    public class DisplayManager
    {
        private static void SafeInvoke<T>(Action<T>? evt, T arg)
        {
            if (evt == null) return;
            foreach (Action<T> handler in evt.GetInvocationList())
            {
                try { handler(arg); }
                catch { /* isolate subscribers */ }
            }
        }

        private static readonly Lazy<DisplayManager> _instance = new(() => new DisplayManager());
        public static DisplayManager Instance => _instance.Value;

        private readonly object _lock = new();
        private int _currentRefreshRate = 60;
        private int _blueLightMode = 0;
        private int _ddsMode = 1; // Default Automatic
        private bool _lcdOverdrive = true;

        public event Action<int>? RefreshRateChanged;
        public event Action<int>? BlueLightShieldChanged;
        public event Action<int>? DdsModeChanged;
        public event Action<bool>? LcdOverdriveChanged;

        public int CurrentRefreshRate { get { lock (_lock) return _currentRefreshRate; } }
        public int BlueLightMode { get { lock (_lock) return _blueLightMode; } }
        public int CurrentDdsMode { get { lock (_lock) return _ddsMode; } }
        public bool IsLcdOverdriveEnabled { get { lock (_lock) return _lcdOverdrive; } }

        public DisplayManager()
        {
            RefreshCurrentState();
        }

        public void RefreshCurrentState()
        {
            lock (_lock)
            {
                if (PredatorNativeCore.IsNativeCoreAvailable)
                {
                    int hz = PredatorNativeCore.DisplayGetCurrentRefreshRate();
                    if (hz > 0) _currentRefreshRate = hz;
                }

                // Query live BIOS MUX state directly from hardware
                var mux = WmiController.Instance.GetGpuMode();
                if (mux.HasValue)
                {
                    _ddsMode = mux.Value switch
                    {
                        GpuMode.Auto => 1,
                        GpuMode.iGpuOnly => 0,
                        GpuMode.dGpuOnly => 2,
                        _ => 1
                    };
                }
            }
        }

        public int GetMaxRefreshRate()
        {
            try
            {
                int coreMax = Form1.GetMaxRefreshRateCore();
                if (coreMax >= 60) return coreMax;
            }
            catch { }

            var rates = GetSupportedRefreshRates();
            return (rates != null && rates.Length > 0) ? rates.Max() : 144;
        }

        public bool SetRefreshRate60() => SetRefreshRate(60);
        public bool SetRefreshRateMax() => SetRefreshRate(GetMaxRefreshRate());

        public int[] GetSupportedRefreshRates()
        {
            if (PredatorNativeCore.DisplayGetSupportedRefreshRates(out int[] rates) && rates != null && rates.Length > 0)
            {
                return rates.OrderBy(r => r).ToArray();
            }

            // High-fidelity fallback for gaming laptop panels
            return new[] { 60, 120, 144, 165, 240, 360 };
        }

        public bool SetRefreshRate(int hz)
        {
            if (hz < 30 || hz > 1000) return false;

            if (Form1.SetRefreshRateOverride != null)
            {
                bool ovr = Form1.SetRefreshRateOverride(hz);
                if (ovr)
                {
                    lock (_lock) { _currentRefreshRate = hz; }
                    SafeInvoke(RefreshRateChanged, hz);
                }
                return ovr;
            }

            bool success = false;
            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                success = PredatorNativeCore.DisplaySetRefreshRate(hz);
            }

            if (!success)
            {
                try
                {
                    success = Form1.SetRefreshRateCore(hz);
                }
                catch { }
            }

            if (success || !PredatorNativeCore.IsNativeCoreAvailable)
            {
                lock (_lock) { _currentRefreshRate = hz; }
                SafeInvoke(RefreshRateChanged, hz);
                return true;
            }

            return false;
        }

        public bool SetBlueLightShield(int mode)
        {
            if (mode < 0 || mode > 100) return false;

            bool success = false;
            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                success = PredatorNativeCore.DisplaySetBlueLightShield(mode);
            }

            if (success || !PredatorNativeCore.IsNativeCoreAvailable)
            {
                lock (_lock) { _blueLightMode = mode; }
                SafeInvoke(BlueLightShieldChanged, mode);
                return true;
            }

            return false;
        }

        public NativeDdsStatus GetDdsStatus()
        {
            var mux = WmiController.Instance.GetGpuMode();
            int activeMux = mux switch
            {
                GpuMode.dGpuOnly => 2,
                GpuMode.iGpuOnly => 1,
                _ => 1
            };

            return new NativeDdsStatus
            {
                IsSupported = true,
                IsAutomatic = mux == GpuMode.Auto,
                ActiveMux = activeMux,
                HasBlockingApps = false
            };
        }

        public GpuMode GetHardwareMuxMode()
        {
            return WmiController.Instance.GetGpuMode() ?? GpuMode.Auto;
        }

        public bool SetHardwareMuxMode(GpuMode mode)
        {
            bool ok = WmiController.Instance.SetGpuMode(mode);
            if (ok)
            {
                int modeInt = mode switch
                {
                    GpuMode.iGpuOnly => 0,
                    GpuMode.dGpuOnly => 2,
                    _ => 1
                };
                lock (_lock) { _ddsMode = modeInt; }
                SafeInvoke(DdsModeChanged, modeInt);
            }
            return ok;
        }

        public bool SetDdsMode(int mode)
        {
            if (mode < 0 || mode > 2) return false;
            GpuMode targetMode = mode switch
            {
                0 => GpuMode.iGpuOnly,
                2 => GpuMode.dGpuOnly,
                _ => GpuMode.Auto
            };
            return SetHardwareMuxMode(targetMode);
        }

        public bool SwitchDdsMode(int mode)
        {
            return SetDdsMode(mode);
        }

        public bool SwitchDdsMode(GpuMode mode)
        {
            return SetHardwareMuxMode(mode);
        }

        public bool SetLcdOverdrive(bool enabled)
        {
            bool success = false;
            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                success = PredatorNativeCore.DisplaySetLcdOverdrive(enabled);
            }

            if (success || !PredatorNativeCore.IsNativeCoreAvailable)
            {
                lock (_lock) { _lcdOverdrive = enabled; }
                SafeInvoke(LcdOverdriveChanged, enabled);
                return true;
            }

            return false;
        }
    }
}
