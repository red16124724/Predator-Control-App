using System;

namespace PredatorControlApp.Channels
{
    public interface IFanControlChannel
    {
        bool SetFanBehavior(byte mode, bool applyCustomSpeeds = true);
        bool SetFanSpeed(ulong fanType, byte percentage, bool force = false);
        bool TrySetFanSpeed(FanChannel channel, int percent);
        bool TrySetFanBehavior(byte mode);
        int GetFanRpm(FanChannel channel);
    }
}
