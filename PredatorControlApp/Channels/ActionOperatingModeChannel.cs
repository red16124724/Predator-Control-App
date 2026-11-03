using System;
using System.Collections.Generic;

namespace PredatorControlApp.Channels
{
    public sealed class ActionOperatingModeChannel : IOperatingModeChannel
    {
        private readonly Func<int, ulong, (bool success, ulong value)> _executeAction;

        public ModeTransportKind Transport => ModeTransportKind.ActionFunction7;

        public IReadOnlyList<OperatingMode> SupportedModes { get; } = new[]
        {
            OperatingMode.Quiet,
            OperatingMode.Balanced,
            OperatingMode.Performance,
            OperatingMode.Turbo
        };

        public ActionOperatingModeChannel(Func<int, ulong, (bool success, ulong value)> executeAction)
        {
            _executeAction = executeAction ?? throw new ArgumentNullException(nameof(executeAction));
        }

        public OperatingMode? Read()
        {
            try
            {
                var res = _executeAction(7, 0);
                if (!res.success) return null;
                byte raw = (byte)((res.value >> 8) & 0xFF);
                return Enum.IsDefined(typeof(OperatingMode), raw) ? (OperatingMode)raw : null;
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
                var res = _executeAction(7, (ulong)mode);
                return res.success;
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetOperatingMode(out OperatingMode mode)
        {
            var m = Read();
            if (m.HasValue)
            {
                mode = m.Value;
                return true;
            }
            mode = OperatingMode.Balanced;
            return false;
        }

        public bool TrySetOperatingMode(OperatingMode mode) => Write(mode);
    }
}
