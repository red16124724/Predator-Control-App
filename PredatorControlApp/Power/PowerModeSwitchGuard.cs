using System;
using System.Collections.Generic;
using System.Threading;

namespace PredatorControlApp
{
    /// <summary>
    /// Serialized, latest-wins power mode switch queue.
    ///
    /// Why this exists: the previous path applied the EC/WMI mode, re-applied
    /// CPU power limits (which themselves wrote the mode again), and toggled the
    /// Windows power overlay inline. On some machines that burst resets USB and
    /// Bluetooth power state, which is heard as audio/video stutter and seen as
    /// a Bluetooth dropout. This guard does the mode write once, keeps a minimum
    /// spacing between writes, and lets the caller pause telemetry for the
    /// settle window.
    /// </summary>
    public sealed class PowerModeSwitchGuard : IDisposable
    {
        private readonly object _sync = new();
        private readonly Func<byte, bool> _applyHardware;
        private readonly Action<byte>? _onApplied;
        private readonly Action? _onBeforeSwitch;
        private readonly Action? _onAfterSwitch;
        private readonly int _minIntervalMs;
        private readonly int _settleMs;

        private int _pendingMode = -1;
        private bool _workerRunning;
        private bool _disposed;
        private readonly CancellationTokenSource _cts = new();
        private DateTime _lastSwitchUtc = DateTime.MinValue;
        private long _completed;
        private long _coalesced;
        private long _failed;
        private byte _lastAppliedMode = 0xFF;

        public event EventHandler<PowerModeSwitchResult>? SwitchCompleted;

        public PowerModeSwitchGuard(
            Func<byte, bool> applyHardware,
            Action<byte>? onApplied = null,
            int minIntervalMs = 900,
            int settleMs = 350,
            Action? onBeforeSwitch = null,
            Action? onAfterSwitch = null)
        {
            _applyHardware = applyHardware ?? throw new ArgumentNullException(nameof(applyHardware));
            _onApplied = onApplied;
            _minIntervalMs = Math.Clamp(minIntervalMs, 0, 10000);
            _settleMs = Math.Clamp(settleMs, 0, 5000);
            _onBeforeSwitch = onBeforeSwitch;
            _onAfterSwitch = onAfterSwitch;
        }

        public bool IsSwitching
        {
            get { lock (_sync) return !_disposed && _workerRunning; }
        }

        public int? PendingMode
        {
            get { lock (_sync) return _pendingMode >= 0 ? _pendingMode : null; }
        }

        public byte LastAppliedMode
        {
            get { lock (_sync) return _lastAppliedMode; }
        }

        public long CompletedCount { get { lock (_sync) return _completed; } }
        public long CoalescedCount { get { lock (_sync) return _coalesced; } }
        public long FailedCount { get { lock (_sync) return _failed; } }

        /// <summary>
        /// Request a mode. If the worker is busy the newest request replaces the
        /// pending one; intermediate modes are never written to hardware.
        /// </summary>
        public void Request(byte mode)
        {
            lock (_sync)
            {
                if (_disposed) { _workerRunning = false; return; }

                if (_pendingMode == mode)
                {
                    _coalesced++;
                    return;
                }

                _pendingMode = mode;
                if (_workerRunning)
                {
                    _coalesced++;
                    return;
                }
                _workerRunning = true;
            }

            Task.Run(WorkerLoop);
        }

        private async Task WorkerLoop()
        {
            try
            {
                while (true)
                {
                    int mode;
                    lock (_sync)
                    {
                        if (_disposed || _pendingMode < 0)
                        {
                            return;
                        }
                        mode = _pendingMode;
                        _pendingMode = -1;
                    }

                    int waitMs = 0;
                    lock (_sync)
                    {
                        if (_lastSwitchUtc != DateTime.MinValue)
                        {
                            double elapsed = (DateTime.UtcNow - _lastSwitchUtc).TotalMilliseconds;
                            waitMs = (int)Math.Max(0, _minIntervalMs - elapsed);
                        }
                    }

                    if (waitMs > 0)
                    {
                        try { await Task.Delay(waitMs, _cts.Token).ConfigureAwait(false); } catch { }
                    }

                    lock (_sync)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        // If a newer mode was requested while waiting, fast-forward to latest mode
                        if (_pendingMode >= 0)
                        {
                            mode = _pendingMode;
                            _pendingMode = -1;
                        }
                    }

                    bool ok = false;
                    string error = string.Empty;
                    try
                    {
                        try { _onBeforeSwitch?.Invoke(); } catch { }
                        ok = _applyHardware((byte)mode);
                        if (ok)
                        {
                            lock (_sync) _lastAppliedMode = (byte)mode;
                            try { _onApplied?.Invoke((byte)mode); } catch { }
                            if (_settleMs > 0)
                            {
                                try { await Task.Delay(_settleMs, _cts.Token).ConfigureAwait(false); } catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        error = ex.Message;
                    }
                    finally
                    {
                        try { _onAfterSwitch?.Invoke(); } catch { }
                    }

                    var eventArgs = new PowerModeSwitchResult((byte)mode, ok, error, DateTime.UtcNow);
                    try
                    {
                        SwitchCompleted?.Invoke(this, eventArgs);
                    }
                    catch { }

                    lock (_sync)
                    {
                        _lastSwitchUtc = DateTime.UtcNow;
                        if (ok) _completed++; else _failed++;
                    }
                }
            }
            finally
            {
                lock (_sync)
                {
                    _workerRunning = false;
                    if (!_disposed && _pendingMode >= 0)
                    {
                        _workerRunning = true;
                        _ = Task.Run(WorkerLoop);
                    }
                }
            }
        }

        public void ResetCounters()
        {
            lock (_sync)
            {
                _completed = 0;
                _coalesced = 0;
                _failed = 0;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _pendingMode = -1;
                _workerRunning = false;
            }
        }
    }

    public readonly record struct PowerModeSwitchResult(
        byte Mode,
        bool Success,
        string Error,
        DateTime CompletedAtUtc);
}
