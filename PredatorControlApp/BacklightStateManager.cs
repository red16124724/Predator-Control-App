using System;
using System.Windows.Forms;

namespace PredatorControlApp
{
    public class BacklightStateManager
    {
        private readonly object _lock = new();

        private bool _isLidClosed;
        private bool _isSleeping;
        private bool? _isPluggedIn;
        private int _savedAcBrightness = 100;
        private bool _manualBacklightOnBattery;
        private int _manualBatteryBrightness;

        public bool IsLidClosed { get { lock (_lock) { return _isLidClosed; } } private set { lock (_lock) { _isLidClosed = value; } } }
        public bool IsSleeping { get { lock (_lock) { return _isSleeping; } } private set { lock (_lock) { _isSleeping = value; } } }
        public bool? IsPluggedIn { get { lock (_lock) { return _isPluggedIn; } } private set { lock (_lock) { _isPluggedIn = value; } } }
        public void SetPluggedInState(bool? pluggedIn) { lock (_lock) { _isPluggedIn = pluggedIn; } }
        public int SavedAcBrightness { get { lock (_lock) { return _savedAcBrightness; } } set { lock (_lock) { _savedAcBrightness = Math.Clamp(value, 0, 100); } } }
        public bool ManualBacklightOnBattery { get { lock (_lock) { return _manualBacklightOnBattery; } } set { lock (_lock) { _manualBacklightOnBattery = value; } } }
        public int ManualBatteryBrightness { get { lock (_lock) { return _manualBatteryBrightness; } } set { lock (_lock) { _manualBatteryBrightness = Math.Clamp(value, 0, 100); } } }

        public bool IsOnBattery(PowerLineStatus lineStatus, BatteryChargeStatus chargeStatus = 0)
        {
            lock (_lock)
            {
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

                bool isCharging = (chargeStatus & BatteryChargeStatus.Charging) != 0;
                if (lineStatus == PowerLineStatus.Online || isCharging)
                    _isPluggedIn = true;
                else if (lineStatus == PowerLineStatus.Offline)
                    _isPluggedIn = false;

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
                _manualBacklightOnBattery = false;
                _manualBatteryBrightness = 0;
                shouldTurnOff = true;
                targetBrightness = 0;
            }
        }

        public void OnResume(PowerLineStatus lineStatus, out bool shouldTurnOff, out int targetBrightness, BatteryChargeStatus chargeStatus = 0)
        {
            lock (_lock)
            {
                _isSleeping = false;

                bool isCharging = (chargeStatus & BatteryChargeStatus.Charging) != 0;
                if (lineStatus == PowerLineStatus.Online || isCharging)
                    _isPluggedIn = true;
                else if (lineStatus == PowerLineStatus.Offline)
                    _isPluggedIn = false;

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
                if (onBattery)
                {
                    _manualBacklightOnBattery = brightness > 0;
                    _manualBatteryBrightness = Math.Clamp(brightness, 0, 100);
                }
                else
                {
                    _savedAcBrightness = Math.Clamp(brightness, 0, 100);
                }
            }
        }
    }
}
