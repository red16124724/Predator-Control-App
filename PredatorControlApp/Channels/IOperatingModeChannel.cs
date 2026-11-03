using System;
using System.Collections.Generic;

namespace PredatorControlApp.Channels
{
    public enum ModeTransportKind
    {
        WmiMisc0x0B,
        EcHidFeature0x02,
        ActionFunction7,
        Composite
    }

    public interface IOperatingModeChannel
    {
        ModeTransportKind Transport { get; }
        OperatingMode? Read();
        bool Write(OperatingMode mode);
        bool TryGetOperatingMode(out OperatingMode mode);
        bool TrySetOperatingMode(OperatingMode mode);
        IReadOnlyList<OperatingMode> SupportedModes { get; }
    }
}
