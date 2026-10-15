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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        // Test hook to allow mocking and test assertions without depending on Windows kernel state
        internal static Func<uint, uint>? ExecutionStateOverride { get; set; }

        public static bool IsLocked { get; private set; }

        public static uint CurrentFlags { get; private set; }

        /// <summary>
        /// Prevents system sleep, idle standby, and display power-off during mission-critical tasks (e.g. Battery Calibration).
        /// </summary>
        public static bool Acquire(bool displayRequired = true, bool awayMode = false)
        {
            lock (_syncLock)
            {
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
                    IsLocked = true;
                    CurrentFlags = flags;
                    return true;
                }

                // If kernel call failed, do not claim locked
                IsLocked = false;
                CurrentFlags = 0;
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

                IsLocked = false;
                CurrentFlags = flags;
                return prev != 0;
            }
        }

        /// <summary>
        /// Acquires wake lock and returns an IDisposable scope that releases the lock upon disposal.
        /// </summary>
        public static IDisposable AcquireScoped(bool displayRequired = true, bool awayMode = false)
        {
            Acquire(displayRequired, awayMode);
            return new WakeLockScope();
        }

        private sealed class WakeLockScope : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (!_disposed)
                {
                    Release();
                    _disposed = true;
                }
            }
        }

        internal static void ResetForTesting()
        {
            lock (_syncLock)
            {
                ExecutionStateOverride = null;
                IsLocked = false;
                CurrentFlags = 0;
            }
        }
    }
}
