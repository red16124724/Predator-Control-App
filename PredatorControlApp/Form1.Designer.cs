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
                try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
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
                if (_gameSync != null)
                {
                    _gameSync.GameDetected -= OnGameDetected;
                    _gameSync.GameExited -= OnGameExited;
                    _gameSync.Dispose();
                }
                _wmi?.Dispose();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                _trayMenu?.Dispose();
                if (_timer != null)
                {
                    _timer.Tick -= UpdateTelemetry;
                    _timer.Stop();
                    _timer.Dispose();
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
                try { Icon?.Dispose(); } catch { }
                components?.Dispose();
            }
            else
            {
                UnregisterPowerNotifications();
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
