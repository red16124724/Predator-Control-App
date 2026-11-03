using System;

namespace PredatorControlApp.Channels
{
    public sealed class WmiFanControlChannel : IFanControlChannel
    {
        private readonly Func<byte, bool, bool> _setBehavior;
        private readonly Func<ulong, byte, bool, bool> _setSpeed;
        private readonly Func<FanChannel, int, bool>? _setChannelSpeed;
        private readonly Func<FanChannel, int>? _getRpm;

        public WmiFanControlChannel(
            Func<byte, bool, bool> setBehavior,
            Func<ulong, byte, bool, bool> setSpeed,
            Func<FanChannel, int, bool>? setChannelSpeed = null,
            Func<FanChannel, int>? getRpm = null)
        {
            _setBehavior = setBehavior ?? throw new ArgumentNullException(nameof(setBehavior));
            _setSpeed = setSpeed ?? throw new ArgumentNullException(nameof(setSpeed));
            _setChannelSpeed = setChannelSpeed;
            _getRpm = getRpm;
        }

        public bool SetFanBehavior(byte mode, bool applyCustomSpeeds = true)
        {
            try { return _setBehavior(mode, applyCustomSpeeds); } catch { return false; }
        }

        public bool SetFanSpeed(ulong fanType, byte percentage, bool force = false)
        {
            try { return _setSpeed(fanType, percentage, force); } catch { return false; }
        }

        public bool TrySetFanSpeed(FanChannel channel, int percent)
        {
            if (channel == null) return false;

            try
            {
                if (_setChannelSpeed != null)
                    return _setChannelSpeed(channel, percent);

                byte clamped = (byte)Math.Clamp(percent, 0, 100);
                ulong fanType = channel.Id switch
                {
                    FanId.Cpu => 1UL,
                    FanId.System => 2UL,
                    _ => 4UL
                };
                return SetFanSpeed(fanType, clamped, force: true);
            }
            catch
            {
                return false;
            }
        }

        public bool TrySetFanBehavior(byte mode) => SetFanBehavior(mode, applyCustomSpeeds: true);

        public int GetFanRpm(FanChannel channel)
        {
            try
            {
                return (_getRpm != null && channel != null) ? _getRpm(channel) : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
