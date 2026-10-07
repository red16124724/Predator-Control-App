using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Threading;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class WmiHotkeyWatcher : IDisposable
    {
        private ManagementEventWatcher? _apgeWatcher;
        private ManagementEventWatcher? _genericWatcher;
        private readonly Action<int> _onHotkeyEvent;
        private readonly object _lock = new();
        private bool _isDisposed;
        private int _isRestarting;

        private long _lastModeKeyTick;
        private const int DebounceThresholdMs = 250;

        public event Action? ModeKeyPressed;

        public WmiHotkeyWatcher(Action<int> onHotkeyEvent)
        {
            _onHotkeyEvent = onHotkeyEvent ?? throw new ArgumentNullException(nameof(onHotkeyEvent));
            StartWatching();
        }

        private void StopAndDisposeWatchersUnderLock()
        {
            if (_apgeWatcher != null)
            {
                try
                {
                    _apgeWatcher.Stopped -= OnWatcherStopped;
                    _apgeWatcher.EventArrived -= WmiEventArrived;
                    try { _apgeWatcher.Stop(); } catch { }
                    _apgeWatcher.Dispose();
                }
                catch { }
                finally
                {
                    _apgeWatcher = null;
                }
            }

            if (_genericWatcher != null)
            {
                try
                {
                    _genericWatcher.Stopped -= OnWatcherStopped;
                    _genericWatcher.EventArrived -= WmiEventArrived;
                    try { _genericWatcher.Stop(); } catch { }
                    _genericWatcher.Dispose();
                }
                catch { }
                finally
                {
                    _genericWatcher = null;
                }
            }
        }

        private void StartWatching()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                StopAndDisposeWatchersUnderLock();

                try
                {
                    var scope = new ManagementScope(@"\\.\root\wmi");
                    scope.Connect();

                    var query = new EventQuery("SELECT * FROM APGeEvent");
                    var watcher = new ManagementEventWatcher(scope, query);
                    watcher.EventArrived += WmiEventArrived;
                    watcher.Stopped += OnWatcherStopped;
                    watcher.Start();
                    _apgeWatcher = watcher;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"WmiHotkeyWatcher: APGeEvent unavailable: {ex.Message}");
                    if (_apgeWatcher != null)
                    {
                        try { _apgeWatcher.Dispose(); } catch { }
                        _apgeWatcher = null;
                    }
                }

                try
                {
                    var scope = new ManagementScope(@"\\.\root\wmi");
                    scope.Connect();

                    var query = new EventQuery("SELECT * FROM AcerGenericEvent");
                    var watcher = new ManagementEventWatcher(scope, query);
                    watcher.EventArrived += WmiEventArrived;
                    watcher.Stopped += OnWatcherStopped;
                    watcher.Start();
                    _genericWatcher = watcher;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"WmiHotkeyWatcher: AcerGenericEvent unavailable: {ex.Message}");
                    if (_genericWatcher != null)
                    {
                        try { _genericWatcher.Dispose(); } catch { }
                        _genericWatcher = null;
                    }
                }
            }
        }

        private void OnWatcherStopped(object sender, StoppedEventArgs e)
        {
            if (_isDisposed) return;
            if (Interlocked.CompareExchange(ref _isRestarting, 1, 0) == 0)
            {
                System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
                {
                    try
                    {
                        if (!_isDisposed)
                        {
                            StartWatching();
                        }
                    }
                    catch { }
                    finally
                    {
                        Interlocked.Exchange(ref _isRestarting, 0);
                    }
                });
            }
        }

        private void FireModeKeyPressed()
        {
            if (_isDisposed) return;
            long now = Environment.TickCount64;
            if (now - _lastModeKeyTick >= DebounceThresholdMs)
            {
                _lastModeKeyTick = now;
                try { ModeKeyPressed?.Invoke(); } catch { }
            }
        }

        private void WmiEventArrived(object sender, EventArrivedEventArgs e)
        {
            if (_isDisposed) return;

            try
            {
                using var eventObj = e.NewEvent;
                if (eventObj == null) return;

                PropertyData? detailProp = null;
                try { detailProp = eventObj.Properties["EventDetail"] ?? eventObj.Properties["Detail"] ?? eventObj.Properties["EventData"]; } catch { }
                if (detailProp == null)
                {
                    foreach (PropertyData p in eventObj.Properties)
                    {
                        if (p.Name.Equals("EventDetail", StringComparison.OrdinalIgnoreCase) ||
                            p.Name.Equals("Detail", StringComparison.OrdinalIgnoreCase))
                        {
                            detailProp = p;
                            break;
                        }
                    }
                }

                if (detailProp != null && detailProp.Value != null)
                {
                    if (detailProp.Value is byte[] bytes)
                    {
                        var fwEvent = FirmwareEvent.Decode(bytes);
                        if (fwEvent != null)
                        {
                            if (fwEvent.Kind == FirmwareEventKind.ModeKey || fwEvent.Value == 7 || fwEvent.Value == 0x82 || fwEvent.Value == 0x86 || fwEvent.Value == 0x87)
                            {
                                FireModeKeyPressed();
                            }
                            else if (fwEvent.Kind == FirmwareEventKind.Hotkey)
                            {
                                if (fwEvent.Value == 7 || fwEvent.Value == 0x82 || fwEvent.Value == 0x86 || fwEvent.Value == 0x87)
                                    FireModeKeyPressed();
                                else
                                {
                                    try { _onHotkeyEvent(fwEvent.Value); } catch { }
                                }
                            }
                            else
                            {
                                try { _onHotkeyEvent(bytes[0]); } catch { }
                            }
                        }
                        else if (bytes.Length > 0)
                        {
                            if (bytes[0] == 7 || bytes[0] == 0x82 || bytes[0] == 0x86 || bytes[0] == 0x87 ||
                                (bytes.Length > 1 && (bytes[1] == 7 || bytes[1] == 0x82 || bytes[1] == 0x86 || bytes[1] == 0x87)))
                            {
                                FireModeKeyPressed();
                            }
                            else
                            {
                                try { _onHotkeyEvent(bytes[0]); } catch { }
                            }
                        }
                    }
                    else
                    {
                        int eventDetail = Convert.ToInt32(detailProp.Value);
                        if (eventDetail == 7 || eventDetail == 0x82 || eventDetail == 0x86 || eventDetail == 0x87)
                        {
                            FireModeKeyPressed();
                        }
                        else
                        {
                            try { _onHotkeyEvent(eventDetail); } catch { }
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
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                StopAndDisposeWatchersUnderLock();
            }
        }

        ~WmiHotkeyWatcher()
        {
            Dispose(false);
        }
    }
}
