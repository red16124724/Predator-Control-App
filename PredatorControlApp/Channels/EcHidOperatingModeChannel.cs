using System;
using System.Collections.Generic;

namespace PredatorControlApp.Channels
{
    public sealed class EcHidOperatingModeChannel : IOperatingModeChannel
    {
        private readonly EcHidDevice _ecHid;
        private readonly IOperatingModeChannel? _wmiSyncChannel;

        public ModeTransportKind Transport => ModeTransportKind.EcHidFeature0x02;

        public IReadOnlyList<OperatingMode> SupportedModes { get; } = new[]
        {
            OperatingMode.Quiet,
            OperatingMode.Balanced,
            OperatingMode.Performance,
            OperatingMode.Turbo,
            OperatingMode.Eco
        };

        public EcHidOperatingModeChannel(EcHidDevice ecHid, IOperatingModeChannel? wmiSyncChannel = null)
        {
            _ecHid = ecHid ?? throw new ArgumentNullException(nameof(ecHid));
            _wmiSyncChannel = wmiSyncChannel;
        }

        public OperatingMode? Read()
        {
            try
            {
                                byte? raw = _ecHid.ReadMode();
                if (!raw.HasValue) return null;

                if (AcerProtocol.TryEcModeToWmiMode(raw.Value, out byte wmiMode) && Enum.IsDefined(typeof(OperatingMode), wmiMode))
                {
                    return (OperatingMode)wmiMode;
                }
                return null;
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
                                byte rawWmi = (byte)mode;
                if (!AcerProtocol.TryWmiModeToEcMode(rawWmi, out byte ecMode)) return false;

                bool ok = _ecHid.WriteMode(ecMode);
                if (ok && _wmiSyncChannel != null)
                {
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { _wmiSyncChannel.Write(mode); } catch { }
                    });
                }
                return ok;
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
