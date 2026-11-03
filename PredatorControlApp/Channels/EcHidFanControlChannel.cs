using System;

namespace PredatorControlApp.Channels
{
    public sealed class EcHidFanControlChannel : IFanControlChannel
    {
        private readonly EcHidDevice _ecHid;
        private readonly IFanControlChannel _fallback;

        public EcHidFanControlChannel(EcHidDevice ecHid, IFanControlChannel fallback)
        {
            _ecHid = ecHid ?? throw new ArgumentNullException(nameof(ecHid));
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        }

        public bool SetFanBehavior(byte mode, bool applyCustomSpeeds = true)
        {
            try { return _fallback.SetFanBehavior(mode, applyCustomSpeeds); }
            catch { return false; }
        }

        public bool SetFanSpeed(ulong fanType, byte percentage, bool force = false)
        {
            try { return _fallback.SetFanSpeed(fanType, percentage, force); }
            catch { return false; }
        }

        public bool TrySetFanSpeed(FanChannel channel, int percent)
        {
            try { return _fallback.TrySetFanSpeed(channel, percent); }
            catch { return false; }
        }

        public bool TrySetFanBehavior(byte mode)
        {
            try { return _fallback.TrySetFanBehavior(mode); }
            catch { return false; }
        }

        public int GetFanRpm(FanChannel channel)
        {
            try { return _fallback.GetFanRpm(channel); }
            catch { return 0; }
        }
    }
}
