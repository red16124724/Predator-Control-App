using System;
using System.Collections.Generic;

namespace PredatorControlApp.Channels
{
    public sealed class WmiOperatingModeChannel : IOperatingModeChannel
    {
        private readonly Func<string, ulong, (bool success, ulong value)> _sendCommand;
        private readonly Func<OperatingMode?> _readMode;
        private readonly Action<byte>? _onModeSet;

        public ModeTransportKind Transport => ModeTransportKind.WmiMisc0x0B;

        public IReadOnlyList<OperatingMode> SupportedModes { get; } = new[]
        {
            OperatingMode.Quiet,
            OperatingMode.Balanced,
            OperatingMode.Performance,
            OperatingMode.Turbo,
            OperatingMode.Eco
        };

        public WmiOperatingModeChannel(
            Func<string, ulong, (bool success, ulong value)> sendCommand,
            Func<OperatingMode?>? readMode = null,
            Action<byte>? onModeSet = null)
        {
            _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
            _readMode = readMode ?? (() => null);
            _onModeSet = onModeSet;
        }

        public OperatingMode? Read()
        {
            try
            {
                return _readMode();
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
                byte rawMode = (byte)mode;
                ulong currentMisc = 0;
                try
                {
                    var getRes = _sendCommand("GetGamingMiscSetting", 0x0BUL);
                    if (getRes.success && getRes.value != 0)
                    {
                        currentMisc = getRes.value;
                    }
                }
                catch { }

                ulong payload;
                if (currentMisc != 0)
                {
                    // Non-destructive read-modify-write: preserve bit 0 (BIOS Fn/Media key mode) and vendor upper flags; byte 0 is 0x0B (OperatingMode setting ID)
                    payload = (currentMisc & ~0xFF00UL) | ((ulong)rawMode << 8) | 0x0BUL;
                }
                else
                {
                    payload = 0x0BUL | ((ulong)rawMode << 8);
                }

                var res = _sendCommand("SetGamingMiscSetting", payload);
                if (!res.success)
                {
                    res = _sendCommand("SetGamingProfile", (ulong)rawMode);
                }
                if (res.success)
                {
                    try { _onModeSet?.Invoke(rawMode); } catch { }
                }
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
