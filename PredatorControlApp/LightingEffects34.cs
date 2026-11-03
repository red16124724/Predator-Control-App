using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace PredatorControlApp
{
    public enum LightingEffect34 : byte
    {
        Static = 0,
        Breathing = 1,
        Neon = 2,
        Wave = 3,
        Shifting = 4,
        Zoom = 5,
        Meteor = 6,
        Snake = 7,
        Lightning = 8,
        Stack = 9,
        MotionPoint = 10,
        ZoomIn = 11,
        Ripple = 12,
        Raindrop = 13,
        Fireball = 14,
        Snow = 15,
        Heartbeat = 16,
        Dazzling = 17,
        Matrix = 18,
        Swiping = 19,
        RowWave = 20,
        Racing = 21,
        Sprouting = 22,
        Disco = 23,
        PingPong = 24,
        LightShow = 25,
        FollowOperatingMode = 26,
        PerKey = 27,
        Combo = 28,
        Rainbow = 29,
        RainbowSpectrum = 29,
        Slash = 30,
        Star = 31,
        Blasting = 32
    }

    public static class LightingEffectsManager
    {
        public static readonly string[] EffectNames =
        {
            "Static", "Breathing", "Neon", "Wave", "Shifting", "Zoom", "Meteor",
            "Snake", "Lightning", "Stack", "Motion Point", "Zoom In", "Ripple", "Raindrop",
            "Fireball", "Snow", "Heartbeat", "Dazzling", "Matrix", "Swiping", "Row Wave",
            "Racing", "Sprouting", "Disco", "Ping Pong", "Light Show", "Follow Power Mode",
            "Per-Key Rainbow", "Combo Pulse", "Rainbow Spectrum", "Slash", "Starry Night", "Blasting"
        };

        public static int GetEffectIndex(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            string trimmed = name.Trim();
            if (string.Equals(trimmed, "Off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Off (Static)", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Static", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            for (int i = 0; i < EffectNames.Length; i++)
            {
                if (string.Equals(EffectNames[i], trimmed, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        private static CancellationTokenSource? _softwareAnimCts;
        private static readonly object _animLock = new();
        private static int _currentAnimationGeneration;

        /// <summary>Number of keyboard lighting zones driven by software animation.</summary>
        private const int ZoneCount = 4;

        /// <summary>Reusable native frame buffer (zones * RGB), one per worker thread.</summary>
        [ThreadStatic]
        private static byte[]? _nativeFrameBuffer;

        public static bool IsSoftwareAnimationRunning
        {
            get
            {
                lock (_animLock)
                {
                    return _softwareAnimCts != null && !_softwareAnimCts.IsCancellationRequested;
                }
            }
        }

        public static void StopSoftwareAnimation()
        {
            lock (_animLock)
            {
                Interlocked.Increment(ref _currentAnimationGeneration);
                if (_softwareAnimCts != null)
                {
                    try { _softwareAnimCts.Cancel(); _softwareAnimCts.Dispose(); } catch { }
                    _softwareAnimCts = null;
                }
            }
        }

        public static void ApplyEffect(
            int effectIndex, WmiController wmi,
            Color color, byte brightness, byte speed, byte direction = 0,
            OperatingMode currentPowerMode = OperatingMode.Balanced)
        {
            float alphaScale = Math.Clamp(color.A, (byte)0, (byte)255) / 255f;
            byte r = (byte)Math.Clamp(Math.Round(color.R * alphaScale), 0, 255);
            byte g = (byte)Math.Clamp(Math.Round(color.G * alphaScale), 0, 255);
            byte b = (byte)Math.Clamp(Math.Round(color.B * alphaScale), 0, 255);
            byte effectiveBrightness = color.A == 0 ? (byte)0 : brightness;
            ApplyEffect(effectIndex, wmi, r, g, b, effectiveBrightness, speed, direction, currentPowerMode);
        }


        public static void ApplyEffect(
            int effectIndex, WmiController wmi,
            byte r, byte g, byte b, byte brightness, byte speed, byte direction = 0,
            OperatingMode currentPowerMode = OperatingMode.Balanced)
        {
            // Clamp inputs to safe boundaries
            effectIndex = Math.Clamp(effectIndex, 0, EffectNames.Length - 1);
            brightness = Math.Clamp(brightness, (byte)0, (byte)100);
            speed = Math.Clamp(speed, (byte)1, (byte)100);
            direction = (byte)(direction != 0 ? 1 : 0);

            // Default color fallback so animations are never pitch black/invisible
            if (r == 0 && g == 0 && b == 0)
            {
                r = 0; g = 150; b = 255;
            }

            StopSoftwareAnimation();

            if (brightness == 0)
            {
                wmi.TurnOffBacklight();
                return;
            }

            if (effectIndex <= 6)
            {
                // Native hardware effect supported directly by EC / WMI
                byte hwSpeed = (byte)Math.Clamp(speed <= 9 ? speed : Math.Round(speed * 9.0 / 100.0), 1, 9);
                wmi.SetRgbMode(effectIndex, r, g, b, brightness, hwSpeed, (byte)direction);
                return;
            }

            // Advanced effects (7..32)
            var effect = (LightingEffect34)effectIndex;
            if (effect == LightingEffect34.FollowOperatingMode)
            {
                var modeColor = currentPowerMode switch
                {
                    OperatingMode.Quiet => Color.FromArgb(0, 220, 255),       // Cyan
                    OperatingMode.Performance => Color.FromArgb(255, 140, 0), // Orange
                    OperatingMode.Turbo => Color.FromArgb(255, 20, 50),       // Red
                    OperatingMode.Eco => Color.FromArgb(0, 255, 120),         // Green
                    _ => Color.FromArgb(0, 150, 255)                          // Blue (Balanced)
                };
                wmi.SetStaticColor(modeColor.R, modeColor.G, modeColor.B, brightness);
                return;
            }

            // For software animations (7..32), explicitly halt hardware EC animations (e.g. Meteor mode 6)
            // so the EC yields control of the LEDs to SetZoneColor / SetGamingRgbKb.
            wmi.SetRgbMode(0, r, g, b, brightness, 1, 0);
            wmi.LastMode = effectIndex;

            // Software-driven animated effects across 4 zones
            lock (_animLock)
            {
                int generation = ++_currentAnimationGeneration;
                try { _softwareAnimCts?.Cancel(); _softwareAnimCts?.Dispose(); } catch { }
                var cts = new CancellationTokenSource();
                _softwareAnimCts = cts;
                var token = cts.Token;

                Task.Run(async () =>
                {
                    int step = 0;
                    while (!token.IsCancellationRequested && generation == Volatile.Read(ref _currentAnimationGeneration))
                    {
                        try
                        {
                            ApplyAnimationFrame(effect, wmi, step++, r, g, b, token, generation);
                            // Frame pacing lives in the native core when available.
                            uint nativeDelay = PredatorNativeCore.RgbFrameDelayMs(speed);
                            int delayMs = nativeDelay > 0
                                ? (int)nativeDelay
                                : Math.Max(125, 200 - ((speed <= 9 ? speed * 10 : speed) * 8 / 10));
                            await Task.Delay(delayMs, token);
                        }
                        catch (OperationCanceledException) { break; }
                        catch { break; }
                    }
                }, token);
            }
        }

        internal static void ApplyAnimationFrame(
            LightingEffect34 effect, WmiController wmi, int step,
            byte baseR, byte baseG, byte baseB,
            CancellationToken token, int generation, bool forceManaged = false)
        {
            if (token.IsCancellationRequested || generation != Volatile.Read(ref _currentAnimationGeneration))
                return;

            // Native frame generation: the whole 4-zone frame is computed and written in
            // one C-ABI call with no managed allocation.
            byte[]? buffer = _nativeFrameBuffer;
            if (buffer == null || buffer.Length < PredatorNativeCore.MaxNativeZones * 3)
            {
                buffer = new byte[PredatorNativeCore.MaxNativeZones * 3];
                _nativeFrameBuffer = buffer;
            }
            // SetZoneColor applies the slider brightness once at the transport boundary.
            if (!forceManaged && PredatorNativeCore.RgbRenderFrame((int)effect, (ulong)step, baseR, baseG, baseB, 100, buffer, ZoneCount) == ZoneCount)
            {
                if (token.IsCancellationRequested || generation != Volatile.Read(ref _currentAnimationGeneration))
                    return;
                for (int zone = 1; zone <= ZoneCount; zone++)
                {
                    int offset = (zone - 1) * 3;
                    wmi.SetZoneColor(zone, buffer[offset], buffer[offset + 1], buffer[offset + 2]);
                }
                return;
            }

            // Managed fallback (native core not loaded).
            const float scale = 1f;
            byte scaleR(byte c) => (byte)Math.Clamp(Math.Round(c * scale), 0, 255);
            byte scaleG(byte c) => (byte)Math.Clamp(Math.Round(c * scale), 0, 255);
            byte scaleB(byte c) => (byte)Math.Clamp(Math.Round(c * scale), 0, 255);

            switch (effect)
            {
                case LightingEffect34.Snake:
                case LightingEffect34.PingPong:
                case LightingEffect34.Racing:
                    int activeZone = (step % 8) < 4 ? (step % 4) : (3 - (step % 4));
                    for (int z = 1; z <= 4; z++)
                    {
                        if (token.IsCancellationRequested || generation != Volatile.Read(ref _currentAnimationGeneration)) return;
                        if (z == activeZone + 1)
                            wmi.SetZoneColor(z, scaleR(baseR), scaleG(baseG), scaleB(baseB));
                        else
                            wmi.SetZoneColor(z, 0, 0, 0);
                    }
                    break;

                case LightingEffect34.Rainbow:
                case LightingEffect34.Disco:
                case LightingEffect34.LightShow:
                    for (int z = 1; z <= 4; z++)
                    {
                        if (token.IsCancellationRequested || generation != Volatile.Read(ref _currentAnimationGeneration)) return;
                        double hue = (((step * 15) + (z * 60)) % 360 + 360) % 360;
                        var col = ColorFromHsv(hue, 1.0, scale);
                        wmi.SetZoneColor(z, col.R, col.G, col.B);
                    }
                    break;

                case LightingEffect34.Heartbeat:
                case LightingEffect34.Ripple:
                case LightingEffect34.Blasting:
                    double pulse = Math.Abs(Math.Sin(step * 0.15));
                    byte pR = scaleR((byte)(baseR * pulse));
                    byte pG = scaleG((byte)(baseG * pulse));
                    byte pB = scaleB((byte)(baseB * pulse));
                    for (int z = 1; z <= 4; z++)
                    {
                        if (token.IsCancellationRequested || generation != Volatile.Read(ref _currentAnimationGeneration)) return;
                        wmi.SetZoneColor(z, pR, pG, pB);
                    }
                    break;

                default:
                    // Color shifting between primary and complementary
                    double phase = (Math.Sin(step * 0.1) + 1.0) / 2.0;
                    byte sR = scaleR((byte)(baseR * phase));
                    byte sG = scaleG((byte)(baseG * (1.0 - phase)));
                    byte sB = scaleB((byte)(baseB * phase));
                    for (int z = 1; z <= 4; z++)
                    {
                        if (token.IsCancellationRequested || generation != Volatile.Read(ref _currentAnimationGeneration)) return;
                        wmi.SetZoneColor(z, sR, sG, sB);
                    }
                    break;
            }
        }

        /// <summary>Reusable audio-reactive frame buffer, one per thread.</summary>
        [ThreadStatic]
        private static byte[]? _audioFrameBuffer;

        /// <summary>
        /// Drives the lighting zones from the audio spectrum: bass lights the first zone,
        /// treble the last, with transient (beat) boost applied by the native analyzer.
        /// Returns false when the native spectrum analyzer is unavailable.
        /// </summary>
        public static bool ApplyAudioReactiveFrame(AudioMeter meter, WmiController wmi, byte r, byte g, byte b, byte brightness)
        {
            if (meter == null || wmi == null) return false;

            byte[]? buffer = _audioFrameBuffer;
            if (buffer == null || buffer.Length < ZoneCount * 3)
            {
                buffer = new byte[ZoneCount * 3];
                _audioFrameBuffer = buffer;
            }

            if (meter.RenderZones(r, g, b, 100, buffer, ZoneCount) != ZoneCount)
                return false;

            wmi.SetBrightness(brightness, applyMode: false);
            for (int zone = 1; zone <= ZoneCount; zone++)
            {
                int offset = (zone - 1) * 3;
                wmi.SetZoneColor(zone, buffer[offset], buffer[offset + 1], buffer[offset + 2]);
            }
            return true;
        }

        private static Color ColorFromHsv(double hue, double saturation, double value)
        {
            hue = ((hue % 360) + 360) % 360;
            saturation = Math.Clamp(saturation, 0.0, 1.0);
            value = Math.Clamp(value, 0.0, 1.0);

            int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
            if (hi < 0) hi = (hi + 6) % 6;
            double f = hue / 60 - Math.Floor(hue / 60);

            value = value * 255;
            byte v = (byte)Math.Clamp(value, 0, 255);
            byte p = (byte)Math.Clamp(value * (1 - saturation), 0, 255);
            byte q = (byte)Math.Clamp(value * (1 - f * saturation), 0, 255);
            byte t = (byte)Math.Clamp(value * (1 - (1 - f) * saturation), 0, 255);

            return hi switch
            {
                0 => Color.FromArgb(v, t, p),
                1 => Color.FromArgb(q, v, p),
                2 => Color.FromArgb(p, v, t),
                3 => Color.FromArgb(p, q, v),
                4 => Color.FromArgb(t, p, v),
                _ => Color.FromArgb(v, p, q)
            };
        }
    }
}
