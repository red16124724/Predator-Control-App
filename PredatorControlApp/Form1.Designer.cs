namespace PredatorControlApp
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _isClosing = true;
                try { _shutdownCts?.Cancel(); } catch { }
                try { _shutdownCts?.Dispose(); } catch { }
                try { _fanCommandQueue?.ClearAndStop(); } catch { }
                try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
                try { Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding; } catch { }
                if (_formThemeHandler != null)
                {
                    try { ThemeManager.ThemeChanged -= _formThemeHandler; } catch { }
                }
                lock (_backlightEnforceLock)
                {
                    try { _backlightEnforceCts?.Cancel(); } catch { }
                    try { _backlightEnforceCts?.Dispose(); } catch { }
                    _backlightEnforceCts = null;
                }
                lock (_rgbRestoreLock)
                {
                    try { _rgbRestoreCts?.Cancel(); } catch { }
                    try { _rgbRestoreCts?.Dispose(); } catch { }
                    _rgbRestoreCts = null;
                }
                UnregisterPowerNotifications();
                try { PowerWakeLock.Release(); } catch { }
                if (_gameSync != null)
                {
                    try { _gameSync.GameDetected -= OnGameDetected; } catch { }
                    try { _gameSync.GameExited -= OnGameExited; } catch { }
                    try { _gameSync.Dispose(); } catch { }
                }
                if (_lightingController != null)
                {
                    try { _lightingController.StatusChanged -= OnLightingStatusChanged; } catch { }
                    try { _lightingController.Dispose(); } catch { }
                }
                if (_thermalSafety != null)
                {
                    try { _thermalSafety.CriticalEntered -= OnThermalCriticalEntered; } catch { }
                }
                if (_powerLimitsChangedHandler != null && CpuPowerLimitController.Instance != null)
                {
                    try { CpuPowerLimitController.Instance.PowerLimitsChanged -= _powerLimitsChangedHandler; } catch { }
                    _powerLimitsChangedHandler = null;
                }
                try { _powerModeSwitchGuard?.Dispose(); } catch { }
                try { CpuPowerLimitController.Instance?.Dispose(); } catch { }
// Non-blocking background telemetry shutdown
                _wmi?.Dispose();
                if (_trayIcon != null)
                {
                    try { _trayIcon.Visible = false; } catch { }
                    if (_trayIcon.Icon != null && !ReferenceEquals(_trayIcon.Icon, SystemIcons.Application))
                    {
                        try { _trayIcon.Icon.Dispose(); } catch { }
                    }
                    try { _trayIcon.Dispose(); } catch { }
                    _trayIcon = null;
                }
                if (this.Icon != null && !ReferenceEquals(this.Icon, SystemIcons.Application))
                {
                    try { this.Icon.Dispose(); } catch { }
                }
                try { _trayMenu?.Dispose(); } catch { }
                _trayMenu = null;
                if (_timer != null)
                {
                    try { _timer.Tick -= UpdateTelemetry; } catch { }
                    try { _timer.Stop(); } catch { }
                    try { _timer.Dispose(); } catch { }
                    _timer = null;
                }
                try { LightingEffectsManager.StopSoftwareAnimation(); } catch { }
                try { _keyboardHook?.Dispose(); } catch { }
                try { _wmiHotkeyWatcher?.Dispose(); } catch { }
                try { _rawInputWatcher?.Dispose(); } catch { }
                try { _pipeServer?.Dispose(); } catch { }
                try { _pdhMonitor?.Dispose(); } catch { }
                try { _colorPicker?.Dispose(); } catch { }
                try { _fanCurveForm?.Dispose(); } catch { }
                try { _appIconBitmap?.Dispose(); } catch { }
                try { _tipCalibration?.Dispose(); } catch { }
                    components?.Dispose();
            }
            else
            {
                try { UnregisterPowerNotifications(); } catch { }
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            AutoScaleMode = AutoScaleMode.None;
        }
    }
}
