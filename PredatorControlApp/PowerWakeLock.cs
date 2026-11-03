using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public static class PowerWakeLock
    {
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002;
        public const uint ES_USER_PRESENT = 0x00000004;
        public const uint ES_AWAYMODE_REQUIRED = 0x00000040;
        public const uint ES_CONTINUOUS = 0x80000000;

        private static readonly object _syncLock = new();
        private static int _lockRefCount = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        static PowerWakeLock()
        {
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => ReleaseAll();
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ReleaseAll();
            }
            catch { }
        }

        // Test hook to allow mocking and test assertions without depending on Windows kernel state
        internal static Func<uint, uint>? ExecutionStateOverride { get; set; }

        private static bool _isLocked;
        public static bool IsLocked
        {
            get
            {
                lock (_syncLock)
                {
                    if (_isLocked && _timeoutUtc.HasValue && DateTime.UtcNow >= _timeoutUtc.Value)
                    {
                        ReleaseAll();
                        return false;
                    }
                    return _isLocked;
                }
            }
            private set
            {
                lock (_syncLock) _isLocked = value;
            }
        }

        private static uint _currentFlags;
        public static uint CurrentFlags
        {
            get { lock (_syncLock) return _currentFlags; }
            private set
            {
                lock (_syncLock) _currentFlags = value;
            }
        }

        private static DateTime? _timeoutUtc;
        public static DateTime? TimeoutUtc
        {
            get { lock (_syncLock) return _timeoutUtc; }
            set { lock (_syncLock) _timeoutUtc = value; }
        }

        /// <summary>
        /// Prevents system sleep, idle standby, and display power-off during mission-critical tasks (e.g. Battery Calibration).
        /// </summary>
        public static bool Acquire(bool displayRequired = true, bool awayMode = false, TimeSpan? timeout = null)
        {
            lock (_syncLock)
            {
                if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
                {
                    _timeoutUtc = DateTime.UtcNow.Add(timeout.Value);
                }

                uint flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED;
                if (displayRequired) flags |= ES_DISPLAY_REQUIRED;
                if (awayMode) flags |= ES_AWAYMODE_REQUIRED;

                uint prev;
                try
                {
                    prev = ExecutionStateOverride != null
                        ? ExecutionStateOverride(flags)
                        : SetThreadExecutionState(flags);
                }
                catch
                {
                    prev = 0;
                }

                if (prev != 0)
                {
                    _lockRefCount++;
                    _isLocked = true;
                    _currentFlags = flags;
                    return true;
                }

                // If kernel call failed, do not claim locked
                _isLocked = false;
                _currentFlags = 0;
                _timeoutUtc = null;
                return false;
            }
        }

        /// <summary>
        /// Reasserts the active execution state flags with the Windows kernel (e.g. after system resume from sleep/hibernate).
        /// </summary>
        public static bool Reassert()
        {
            lock (_syncLock)
            {
                if (!_isLocked || _lockRefCount <= 0) return false;
                if (_timeoutUtc.HasValue && DateTime.UtcNow >= _timeoutUtc.Value)
                {
                    ReleaseAll();
                    return false;
                }

                uint flags = _currentFlags;
                if (flags == 0) flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;

                uint prev;
                try
                {
                    prev = ExecutionStateOverride != null
                        ? ExecutionStateOverride(flags)
                        : SetThreadExecutionState(flags);
                }
                catch
                {
                    prev = 0;
                }

                if (prev != 0)
                {
                    _isLocked = true;
                    _currentFlags = flags;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Restores normal Windows power-saving and sleep behavior.
        /// </summary>
        public static bool Release()
        {
            lock (_syncLock)
            {
                if (!_isLocked && _lockRefCount <= 0) return true;

                if (_lockRefCount > 1)
                {
                    _lockRefCount--;
                    return true;
                }

                _lockRefCount = 0;
                _timeoutUtc = null;
                uint flags = ES_CONTINUOUS;
                uint prev;
                try
                {
                    prev = ExecutionStateOverride != null
                        ? ExecutionStateOverride(flags)
                        : SetThreadExecutionState(flags);
                }
                catch
                {
                    prev = 0;
                }

                _isLocked = false;
                _currentFlags = ES_CONTINUOUS;
                return prev != 0;
            }
        }

        /// <summary>
        /// Acquires wake lock and returns an IDisposable scope that releases the lock upon disposal.
        /// </summary>

        /// <summary>
        /// Atomically clears all active wake lock references and restores standard sleep behavior immediately.
        /// </summary>
        public static bool ReleaseAll()
        {
            lock (_syncLock)
            {
                _lockRefCount = 0;
                _timeoutUtc = null;
                uint flags = ES_CONTINUOUS;
                uint prev;
                try
                {
                    prev = ExecutionStateOverride != null
                        ? ExecutionStateOverride(flags)
                        : SetThreadExecutionState(flags);
                }
                catch
                {
                    prev = 0;
                }

                _isLocked = false;
                _currentFlags = ES_CONTINUOUS;
                return prev != 0;
            }
        }
        public static IDisposable AcquireScoped(bool displayRequired = true, bool awayMode = false, TimeSpan? timeout = null)
        {
            bool acquired = Acquire(displayRequired, awayMode, timeout);
            return new WakeLockScope(acquired);
        }

        private sealed class WakeLockScope : IDisposable
        {
            private readonly bool _acquired;
            private int _disposed;
            public WakeLockScope(bool acquired) => _acquired = acquired;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0 && _acquired)
                {
                    Release();
                }
            }
        }

        internal static void ResetForTesting()
        {
            lock (_syncLock)
            {
                ExecutionStateOverride = null;
                _lockRefCount = 0;
                _isLocked = false;
                _currentFlags = 0;
                _timeoutUtc = null;
            }
        }
    }
}
