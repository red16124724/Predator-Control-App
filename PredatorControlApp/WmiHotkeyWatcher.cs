using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class WmiHotkeyWatcher : IDisposable
    {
        private ManagementEventWatcher? _apgeWatcher;
        private ManagementEventWatcher? _genericWatcher;
        private readonly Action<int> _onHotkeyEvent;
        private bool _isDisposed;

        public event Action? ModeKeyPressed;
        public event Action<bool>? DustDefenderEvent;

        public WmiHotkeyWatcher(Action<int> onHotkeyEvent)
        {
            _onHotkeyEvent = onHotkeyEvent ?? throw new ArgumentNullException(nameof(onHotkeyEvent));
            StartWatching();
        }

        private void StartWatching()
        {
            try
            {
                var scope = new ManagementScope(@"\\localhost\root\wmi");
                scope.Connect();

                var query = new EventQuery("SELECT * FROM APGeEvent");
                _apgeWatcher = new ManagementEventWatcher(scope, query);
                _apgeWatcher.EventArrived += WmiEventArrived;
                _apgeWatcher.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WmiHotkeyWatcher: APGeEvent unavailable: {ex.Message}");
            }

            try
            {
                var scope = new ManagementScope(@"\\localhost\root\wmi");
                scope.Connect();

                var query = new EventQuery("SELECT * FROM AcerGenericEvent");
                _genericWatcher = new ManagementEventWatcher(scope, query);
                _genericWatcher.EventArrived += WmiEventArrived;
                _genericWatcher.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WmiHotkeyWatcher: AcerGenericEvent unavailable: {ex.Message}");
            }
        }

        private void WmiEventArrived(object sender, EventArrivedEventArgs e)
        {
            if (_isDisposed) return;

            try
            {
                using var eventObj = e.NewEvent;
                if (eventObj == null) return;

                var detailProp = eventObj.Properties["EventDetail"];
                if (detailProp != null && detailProp.Value != null)
                {
                    if (detailProp.Value is byte[] bytes)
                    {
                        var fwEvent = FirmwareEvent.Decode(bytes);
                        if (fwEvent != null)
                        {
                            if (fwEvent.Kind == FirmwareEventKind.ModeKey)
                            {
                                ModeKeyPressed?.Invoke();
                            }
                            else if (fwEvent.Kind == FirmwareEventKind.Hotkey)
                            {
                                _onHotkeyEvent(fwEvent.Value);
                            }
                            else if (fwEvent.DustDefenderRunning.HasValue)
                            {
                                DustDefenderEvent?.Invoke(fwEvent.DustDefenderRunning.Value);
                            }
                            else
                            {
                                _onHotkeyEvent(bytes[0]);
                            }
                        }
                        else if (bytes.Length > 0)
                        {
                            _onHotkeyEvent(bytes[0]);
                        }
                    }
                    else
                    {
                        int eventDetail = Convert.ToInt32(detailProp.Value);
                        if (eventDetail == 7)
                        {
                            ModeKeyPressed?.Invoke();
                        }
                        else
                        {
                            _onHotkeyEvent(eventDetail);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling WMI hotkey event: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _apgeWatcher?.Stop();
                _apgeWatcher?.Dispose();
            }
            catch { }

            try
            {
                _genericWatcher?.Stop();
                _genericWatcher?.Dispose();
            }
            catch { }
        }
    }
}
