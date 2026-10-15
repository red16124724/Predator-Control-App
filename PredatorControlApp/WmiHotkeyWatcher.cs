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

        private void StopAndDisposeWatchers()
        {
            ManagementEventWatcher? apge;
            ManagementEventWatcher? generic;
            lock (_lock)
            {
                apge = _apgeWatcher;
                _apgeWatcher = null;
                generic = _genericWatcher;
                _genericWatcher = null;
            }

            if (apge != null)
            {
                try
                {
                    apge.Stopped -= OnWatcherStopped;
                    apge.EventArrived -= WmiEventArrived;
                    try { apge.Stop(); } catch { }
                    apge.Dispose();
                }
                catch { }
            }

            if (generic != null)
            {
                try
                {
                    generic.Stopped -= OnWatcherStopped;
                    generic.EventArrived -= WmiEventArrived;
                    try { generic.Stop(); } catch { }
                    generic.Dispose();
                }
                catch { }
            }
        }

        private void StartWatching()
        {
            StopAndDisposeWatchers();
            lock (_lock)
            {
                if (_isDisposed) return;

                ManagementEventWatcher? apge = null;
                try
                {
                    var scope = new ManagementScope(@"\\.\root\wmi");
                    scope.Connect();

                    var query = new EventQuery("SELECT * FROM APGeEvent");
                    apge = new ManagementEventWatcher(scope, query);
                    apge.EventArrived += WmiEventArrived;
                    apge.Stopped += OnWatcherStopped;
                    apge.Start();
                    _apgeWatcher = apge;
                    apge = null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"WmiHotkeyWatcher: APGeEvent unavailable: {ex.Message}");
                    try { apge?.Dispose(); } catch { }
                    if (_apgeWatcher != null)
                    {
                        try { _apgeWatcher.Dispose(); } catch { }
                        _apgeWatcher = null;
                    }
                }

                ManagementEventWatcher? generic = null;
                try
                {
                    var scope = new ManagementScope(@"\\.\root\wmi");
                    scope.Connect();

                    var query = new EventQuery("SELECT * FROM AcerGenericEvent");
                    generic = new ManagementEventWatcher(scope, query);
                    generic.EventArrived += WmiEventArrived;
                    generic.Stopped += OnWatcherStopped;
                    generic.Start();
                    _genericWatcher = generic;
                    generic = null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"WmiHotkeyWatcher: AcerGenericEvent unavailable: {ex.Message}");
                    try { generic?.Dispose(); } catch { }
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

        private DateTime _suppressUntilUtc = DateTime.MinValue;

        public void SuppressModeKeyUntil(DateTime untilUtc)
        {
            lock (_lock)
            {
                if (untilUtc > _suppressUntilUtc)
                    _suppressUntilUtc = untilUtc;
            }
        }

        public void SuppressModeKey(int durationMs = 2500)
        {
            SuppressModeKeyUntil(DateTime.UtcNow.AddMilliseconds(durationMs));
        }

        private void FireModeKeyPressed()
        {
            if (_isDisposed) return;
            bool shouldFire = false;
            lock (_lock)
            {
                if (DateTime.UtcNow < _suppressUntilUtc)
                    return;

                long now = Environment.TickCount64;
                if (now - _lastModeKeyTick >= DebounceThresholdMs)
                {
                    _lastModeKeyTick = now;
                    shouldFire = true;
                }
            }
            if (shouldFire)
            {
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
                            if (fwEvent.Kind == FirmwareEventKind.ModeKey || fwEvent.Value == 7 || fwEvent.Value == 0x82 || fwEvent.Value == 0x86 || fwEvent.Value == 0x87 ||
                                bytes[0] == 7 || bytes[0] == 0x82 || bytes[0] == 0x86 || bytes[0] == 0x87)
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
            if (disposing)
            {
                lock (_lock)
                {
                    if (_isDisposed) return;
                    _isDisposed = true;
                }
                StopAndDisposeWatchers();
            }
        }

        ~WmiHotkeyWatcher()
        {
            Dispose(false);
        }
    }
}
