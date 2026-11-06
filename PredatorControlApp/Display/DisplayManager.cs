using System;
using System.Collections.Generic;
using System.Linq;

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

        public int CurrentRefreshRate => _currentRefreshRate;
        public int BlueLightMode => _blueLightMode;
        public int CurrentDdsMode => _ddsMode;
        public bool IsLcdOverdriveEnabled => _lcdOverdrive;

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

                    if (PredatorNativeCore.DisplayGetDdsStatus(out var ddsStatus) && ddsStatus.IsSupported)
                    {
                        if (ddsStatus.IsAutomatic) _ddsMode = 1;
                        else if (ddsStatus.ActiveMux == 2) _ddsMode = 2;
                        else _ddsMode = 0;
                    }
                }
            }
        }

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
            if (hz < 30) return false;

            bool success = false;
            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                success = PredatorNativeCore.DisplaySetRefreshRate(hz);
            }

            if (success || !PredatorNativeCore.IsNativeCoreAvailable)
            {
                lock (_lock) { _currentRefreshRate = hz; }
                RefreshRateChanged?.Invoke(hz);
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
                BlueLightShieldChanged?.Invoke(mode);
                return true;
            }

            return false;
        }

        public NativeDdsStatus GetDdsStatus()
        {
            if (PredatorNativeCore.IsNativeCoreAvailable &&
                PredatorNativeCore.DisplayGetDdsStatus(out var status))
            {
                return status;
            }

            return new NativeDdsStatus
            {
                IsSupported = false,
                IsAutomatic = false,
                ActiveMux = 0,
                HasBlockingApps = false
            };
        }

        public bool SetDdsMode(int mode)
        {
            if (mode < 0 || mode > 2) return false;

            bool success = false;
            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                success = PredatorNativeCore.DisplaySetDdsMode(mode);
            }

            if (success || !PredatorNativeCore.IsNativeCoreAvailable)
            {
                lock (_lock) { _ddsMode = mode; }
                DdsModeChanged?.Invoke(mode);
                return true;
            }

            return false;
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
                LcdOverdriveChanged?.Invoke(enabled);
                return true;
            }

            return false;
        }
    }
}
