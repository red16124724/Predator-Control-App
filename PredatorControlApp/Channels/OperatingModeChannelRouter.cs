using System;
using System.Collections.Generic;

namespace PredatorControlApp.Channels
{
    /// <summary>
    /// Thread-safe composite router for operating mode channels (Primary e.g. WMI, Fallback e.g. EC-HID).
    /// Dispatches lock-free across channels to eliminate ABBA deadlock inversion hazards with WmiController._lock.
    /// </summary>
    public sealed class OperatingModeChannelRouter : IOperatingModeChannel
    {
        private readonly IOperatingModeChannel _primary;
        private readonly IOperatingModeChannel _fallback;

        public ModeTransportKind Transport => ModeTransportKind.Composite;

        public IReadOnlyList<OperatingMode> SupportedModes
        {
            get
            {
                try
                {
                    var modes = _primary.SupportedModes;
                    if (modes != null && modes.Count > 0) return modes;
                }
                catch { }

                try
                {
                    return _fallback.SupportedModes ?? Array.Empty<OperatingMode>();
                }
                catch
                {
                    return Array.Empty<OperatingMode>();
                }
            }
        }

        public OperatingModeChannelRouter(IOperatingModeChannel primary, IOperatingModeChannel fallback)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
            if (ReferenceEquals(primary, fallback))
                throw new ArgumentException("Primary and fallback channels must not be the same instance.", nameof(fallback));
        }

        public OperatingMode? Read()
        {
            try
            {
                var res = _primary.Read();
                if (res.HasValue) return res;
            }
            catch { }

            try
            {
                return _fallback.Read();
            }
            catch
            {
                return null;
            }
        }

        public bool Write(OperatingMode mode)
        {
            try
            {
                if (_primary.Write(mode)) return true;
            }
            catch { }

            try
            {
                return _fallback.Write(mode);
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetOperatingMode(out OperatingMode mode)
        {
            try
            {
                if (_primary.TryGetOperatingMode(out mode)) return true;
            }
            catch { }

            try
            {
                if (_fallback.TryGetOperatingMode(out mode)) return true;
            }
            catch { }

            mode = OperatingMode.Balanced;
            return false;
        }

        public bool TrySetOperatingMode(OperatingMode mode)
        {
            try
            {
                if (_primary.TrySetOperatingMode(mode)) return true;
            }
            catch { }

            try
            {
                return _fallback.TrySetOperatingMode(mode);
            }
            catch
            {
                return false;
            }
        }
    }
}
