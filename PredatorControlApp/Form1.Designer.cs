namespace PredatorControlApp
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnregisterPowerNotifications();
                _wmi?.Dispose();
                _gameSync?.Dispose();    
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                _trayMenu?.Dispose();
                _timer?.Dispose();
                try { LightingEffectsManager.StopSoftwareAnimation(); } catch { }
                try { _keyboardHook?.Dispose(); } catch { }
                try { _wmiHotkeyWatcher?.Dispose(); } catch { }
                try { _rawInputWatcher?.Dispose(); } catch { }
                try { _pipeServer?.Dispose(); } catch { }
                try { _pdhMonitor?.Dispose(); } catch { }
                try { _colorPicker?.Dispose(); } catch { }
                try { _fanCurveForm?.Dispose(); } catch { }
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
