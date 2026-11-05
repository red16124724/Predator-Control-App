using System;
using System.Windows.Forms;

namespace PredatorControlApp
{
    public class BacklightStateManager : IDisposable
    {
        private readonly object _lock = new();
        private readonly IntPtr _nativeHandle;
        private bool _disposed;

        private bool _isLidClosed;
        private bool _isSleeping;
        private bool? _isPluggedIn;
        private int _savedAcBrightness = 100;
        private bool _manualBacklightOnBattery;
        private int _manualBatteryBrightness;

        private int _idleTimeoutSeconds = 0; // 0 = disabled
        private int _dimmedBrightnessPercent = 30;
        private bool _isDimmed = false;
        private DateTime _lastActivityUtc = DateTime.UtcNow;

        public BacklightStateManager(int initialAcBrightness = 100)
        {
            _savedAcBrightness = Math.Clamp(initialAcBrightness, 0, 100);
            _nativeHandle = PredatorNativeMigrations.BacklightCreate(_savedAcBrightness);
        }

        ~BacklightStateManager()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    if (_nativeHandle != IntPtr.Zero)
                    {
                        PredatorNativeMigrations.BacklightDestroy(_nativeHandle);
                    }
                    _disposed = true;
                }
            }
        }

        public bool IsLidClosed { get { lock (_lock) { return _isLidClosed; } } private set { lock (_lock) { _isLidClosed = value; } } }
        public bool IsSleeping { get { lock (_lock) { return _isSleeping; } } private set { lock (_lock) { _isSleeping = value; } } }
        public bool? IsPluggedIn { get { lock (_lock) { return _isPluggedIn; } } private set { lock (_lock) { _isPluggedIn = value; } } }

        public void SetPluggedInState(bool? pluggedIn)
        {
            lock (_lock)
            {
                _isPluggedIn = pluggedIn;
                PredatorNativeMigrations.BacklightSetPluggedIn(_nativeHandle, pluggedIn);
            }
        }

        public int SavedAcBrightness
        {
            get { lock (_lock) { return _savedAcBrightness; } }
            set
            {
                lock (_lock)
                {
                    _savedAcBrightness = Math.Clamp(value, 0, 100);
                    PredatorNativeMigrations.BacklightSetSavedAc(_nativeHandle, _savedAcBrightness);
                }
            }
        }

        public bool ManualBacklightOnBattery
        {
            get { lock (_lock) { return _manualBacklightOnBattery; } }
            set
            {
                lock (_lock)
                {
                    _manualBacklightOnBattery = value;
                    PredatorNativeMigrations.BacklightSetManualBattery(_nativeHandle, _manualBacklightOnBattery, _manualBatteryBrightness);
                }
            }
        }

        public int ManualBatteryBrightness
        {
            get { lock (_lock) { return _manualBatteryBrightness; } }
            set
            {
                lock (_lock)
                {
                    _manualBatteryBrightness = Math.Clamp(value, 0, 100);
                    PredatorNativeMigrations.BacklightSetManualBattery(_nativeHandle, _manualBacklightOnBattery, _manualBatteryBrightness);
                }
            }
        }

        public int IdleTimeoutSeconds
        {
            get { lock (_lock) return _idleTimeoutSeconds; }
            set
            {
                lock (_lock)
                {
                    _idleTimeoutSeconds = Math.Max(0, value);
                    PredatorNativeMigrations.BacklightSetIdleConfig(_nativeHandle, _idleTimeoutSeconds, _dimmedBrightnessPercent);
                }
            }
        }

        public int DimmedBrightnessPercent
        {
            get { lock (_lock) return _dimmedBrightnessPercent; }
            set
            {
                lock (_lock)
                {
                    _dimmedBrightnessPercent = Math.Clamp(value, 0, 100);
                    PredatorNativeMigrations.BacklightSetIdleConfig(_nativeHandle, _idleTimeoutSeconds, _dimmedBrightnessPercent);
                }
            }
        }

        public bool IsDimmed
        {
            get { lock (_lock) return _isDimmed; }
        }

        public void RecordActivity()
        {
            lock (_lock)
            {
                _lastActivityUtc = DateTime.UtcNow;
                PredatorNativeMigrations.BacklightRecordActivity(_nativeHandle);
            }
        }

        public bool EvaluateIdle(DateTime nowUtc, out bool shouldDim, out int targetBrightness)
        {
            lock (_lock)
            {
                shouldDim = false;
                targetBrightness = 0;

                long nowSec = (long)(nowUtc - DateTime.UnixEpoch).TotalSeconds;
                if (PredatorNativeMigrations.BacklightEvaluateIdle(_nativeHandle, nowSec, out shouldDim, out targetBrightness))
                {
                    _isDimmed = shouldDim;
                    return true;
                }

                if (_isLidClosed || _isSleeping) return false;
                if (_idleTimeoutSeconds <= 0) return false;

                double idleSec = (nowUtc - _lastActivityUtc).TotalSeconds;
                if (idleSec >= _idleTimeoutSeconds)
                {
                    if (!_isDimmed)
                    {
                        _isDimmed = true;
                        shouldDim = true;
                        targetBrightness = _dimmedBrightnessPercent;
                        return true;
                    }
                }
                return false;
            }
        }

        public bool OnActivity(out bool shouldRestore, out int targetBrightness)
        {
            lock (_lock)
            {
                _lastActivityUtc = DateTime.UtcNow;
                shouldRestore = false;
                targetBrightness = 0;

                if (PredatorNativeMigrations.BacklightOnActivity(_nativeHandle, out shouldRestore, out targetBrightness))
                {
                    _isDimmed = false;
                    return true;
                }

                if (_isDimmed)
                {
                    _isDimmed = false;
                    shouldRestore = true;

                    if (_isPluggedIn == true)
                    {
                        targetBrightness = Math.Clamp(_savedAcBrightness, 0, 100);
                    }
                    else
                    {
                        targetBrightness = _manualBacklightOnBattery ? Math.Clamp(_manualBatteryBrightness, 0, 100) : 0;
                    }
                    return true;
                }
                return false;
            }
        }

        public bool IsOnBattery(PowerLineStatus lineStatus, BatteryChargeStatus chargeStatus = 0)
        {
            lock (_lock)
            {
                int nativeOnBatt = PredatorNativeMigrations.BacklightIsOnBattery(_nativeHandle, (int)lineStatus, (int)chargeStatus);
                if (nativeOnBatt != PredatorNativeMigrations.Unavailable)
                {
                    return nativeOnBatt == 1;
                }

                if ((chargeStatus & BatteryChargeStatus.Charging) != 0 || (chargeStatus & BatteryChargeStatus.NoSystemBattery) != 0)
                    return false;
                if (lineStatus == PowerLineStatus.Online)
                    return false;
                if (lineStatus == PowerLineStatus.Offline)
                    return true;
                if (_isPluggedIn == true)
                    return false;
                if (_isPluggedIn == false)
                    return true;

                // PowerLineStatus.Unknown: during sleep/wake transition, default to battery unless confirmed plugged in
                return _isPluggedIn != true;
            }
        }

        public void OnLidChanged(bool isOpen, PowerLineStatus lineStatus, out bool shouldTurnOff, out int targetBrightness, BatteryChargeStatus chargeStatus = 0)
        {
            lock (_lock)
            {
                _isLidClosed = !isOpen;
                _isDimmed = false;
                _lastActivityUtc = DateTime.UtcNow;

                bool isCharging = (chargeStatus & BatteryChargeStatus.Charging) != 0;
                if (lineStatus == PowerLineStatus.Online || isCharging)
                    _isPluggedIn = true;
                else if (lineStatus == PowerLineStatus.Offline)
                    _isPluggedIn = false;

                if (PredatorNativeMigrations.BacklightOnLidChanged(_nativeHandle, isOpen, (int)lineStatus, (int)chargeStatus, out shouldTurnOff, out targetBrightness))
                {
                    if (!isOpen || _isSleeping)
                    {
                        _manualBacklightOnBattery = false;
                        _manualBatteryBrightness = 0;
                    }
                    return;
                }

                if (!isOpen || _isSleeping)
                {
                    // Lid closed or sleeping: keyboard and logo lighting MUST ALWAYS BE OFF
                    _manualBacklightOnBattery = false;
                    _manualBatteryBrightness = 0;
                    shouldTurnOff = true;
                    targetBrightness = 0;
                }
                else
                {
                    // When lid is opened on battery, backlight MUST be off by default unless manually turned on
                    bool onBatt = IsOnBattery(lineStatus, chargeStatus);
                    if (onBatt)
                    {
                        _manualBacklightOnBattery = false;
                        _manualBatteryBrightness = 0;
                        shouldTurnOff = true;
                        targetBrightness = 0;
                    }
                    else
                    {
                        targetBrightness = Math.Clamp(_savedAcBrightness, 0, 100);
                        shouldTurnOff = targetBrightness <= 0;
                    }
                }
            }
        }

        public void OnSuspend(out bool shouldTurnOff, out int targetBrightness)
        {
            lock (_lock)
            {
                _isSleeping = true;
                _isDimmed = false;
                _manualBacklightOnBattery = false;
                _manualBatteryBrightness = 0;
                shouldTurnOff = true;
                targetBrightness = 0;

                PredatorNativeMigrations.BacklightOnSuspend(_nativeHandle, out _, out _);
            }
        }

        public void OnResume(PowerLineStatus lineStatus, out bool shouldTurnOff, out int targetBrightness, BatteryChargeStatus chargeStatus = 0)
        {
            lock (_lock)
            {
                _isSleeping = false;
                _isDimmed = false;
                _lastActivityUtc = DateTime.UtcNow;

                bool isCharging = (chargeStatus & BatteryChargeStatus.Charging) != 0;
                if (lineStatus == PowerLineStatus.Online || isCharging)
                    _isPluggedIn = true;
                else if (lineStatus == PowerLineStatus.Offline)
                    _isPluggedIn = false;

                if (PredatorNativeMigrations.BacklightOnResume(_nativeHandle, (int)lineStatus, (int)chargeStatus, out shouldTurnOff, out targetBrightness))
                {
                    if (_isLidClosed)
                    {
                        _manualBacklightOnBattery = false;
                        _manualBatteryBrightness = 0;
                        shouldTurnOff = true;
                        targetBrightness = 0;
                    }
                    return;
                }

                if (_isLidClosed)
                {
                    _manualBacklightOnBattery = false;
                    _manualBatteryBrightness = 0;
                    shouldTurnOff = true;
                    targetBrightness = 0;
                    return;
                }

                bool onBatt = IsOnBattery(lineStatus, chargeStatus);
                if (onBatt)
                {
                    _manualBacklightOnBattery = false;
                    _manualBatteryBrightness = 0;
                    shouldTurnOff = true;
                    targetBrightness = 0;
                }
                else
                {
                    targetBrightness = Math.Clamp(_savedAcBrightness, 0, 100);
                    shouldTurnOff = targetBrightness <= 0;
                }
            }
        }

        public void OnPowerSourceChanged(bool pluggedIn, out bool shouldTurnOff, out int targetBrightness)
        {
            lock (_lock)
            {
                _isPluggedIn = pluggedIn;
                _isDimmed = false;
                _lastActivityUtc = DateTime.UtcNow;
                PredatorNativeMigrations.BacklightSetPluggedIn(_nativeHandle, pluggedIn);

                if (_isLidClosed || _isSleeping)
                {
                    shouldTurnOff = true;
                    targetBrightness = 0;
                    return;
                }

                if (pluggedIn)
                {
                    _manualBacklightOnBattery = false;
                    targetBrightness = Math.Clamp(_savedAcBrightness, 0, 100);
                    shouldTurnOff = targetBrightness <= 0;
                }
                else
                {
                    // Switched to battery: default to off unless user has explicitly overridden during this session
                    if (!_manualBacklightOnBattery)
                    {
                        shouldTurnOff = true;
                        targetBrightness = 0;
                    }
                    else
                    {
                        targetBrightness = Math.Clamp(_manualBatteryBrightness, 0, 100);
                        shouldTurnOff = targetBrightness <= 0;
                    }
                }
            }
        }

        public void OnUserAdjustedBrightness(int brightness, bool onBattery)
        {
            lock (_lock)
            {
                _isDimmed = false;
                _lastActivityUtc = DateTime.UtcNow;

                if (onBattery)
                {
                    _manualBacklightOnBattery = brightness > 0;
                    _manualBatteryBrightness = Math.Clamp(brightness, 0, 100);
                    PredatorNativeMigrations.BacklightSetManualBattery(_nativeHandle, _manualBacklightOnBattery, _manualBatteryBrightness);
                }
                else
                {
                    _savedAcBrightness = Math.Clamp(brightness, 0, 100);
                    PredatorNativeMigrations.BacklightSetSavedAc(_nativeHandle, _savedAcBrightness);
                }
            }
        }
    }
}
