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
        Twinkling = 7,
        Snake = 8,
        Lightning = 9,
        Stack = 10,
        MotionPoint = 11,
        ZoomIn = 12,
        Ripple = 13,
        Raindrop = 14,
        Fireball = 15,
        Snow = 16,
        Heartbeat = 17,
        Dazzling = 18,
        Matrix = 19,
        Swiping = 20,
        RowWave = 21,
        Racing = 22,
        Sprouting = 23,
        Disco = 24,
        PingPong = 25,
        LightShow = 26,
        FollowOperatingMode = 27,
        PerKey = 28,
        Combo = 29,
        Rainbow = 30,
        RainbowSpectrum = 30,
        Slash = 31,
        Star = 32,
        Blasting = 33
    }

    public static class LightingEffectsManager
    {
        public static readonly string[] EffectNames =
        {
            "Static", "Breathing", "Neon", "Wave", "Shifting", "Zoom", "Meteor", "Twinkling",
            "Snake", "Lightning", "Stack", "Motion Point", "Zoom In", "Ripple", "Raindrop",
            "Fireball", "Snow", "Heartbeat", "Dazzling", "Matrix", "Swiping", "Row Wave",
            "Racing", "Sprouting", "Disco", "Ping Pong", "Light Show", "Follow Power Mode",
            "Per-Key Rainbow", "Combo Pulse", "Rainbow Spectrum", "Slash", "Starry Night", "Blasting"
        };

        private static CancellationTokenSource? _softwareAnimCts;
        private static readonly object _animLock = new();
        private static int _currentAnimationGeneration;

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
                    try { _softwareAnimCts.Cancel(); } catch { }
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
            byte effectiveBrightness = (byte)Math.Clamp(Math.Round(brightness * alphaScale), 0, 100);
            ApplyEffect(effectIndex, wmi, r, g, b, effectiveBrightness, speed, direction, currentPowerMode);
        }


        public static void ApplyEffect(
            int effectIndex, WmiController wmi,
            byte r, byte g, byte b, byte brightness, byte speed, byte direction = 0,
            OperatingMode currentPowerMode = OperatingMode.Balanced)
        {
            // Clamp inputs to safe boundaries
            effectIndex = Math.Clamp(effectIndex, 0, 33);
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

            if (effectIndex <= 7)
            {
                // Native hardware effect supported directly by EC / WMI
                byte hwSpeed = (byte)Math.Clamp(speed <= 9 ? speed : Math.Round(speed * 9.0 / 100.0), 1, 9);
                wmi.SetRgbMode(effectIndex, r, g, b, brightness, hwSpeed, (byte)direction);
                return;
            }

            // Advanced effects (8..33)
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

            // Software-driven animated effects across 4 zones
            lock (_animLock)
            {
                int generation = ++_currentAnimationGeneration;
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
                            ApplyAnimationFrame(effect, wmi, step++, r, g, b, brightness, speed, token, generation);
                            int delayMs = Math.Max(125, 200 - ((speed <= 9 ? speed * 10 : speed) * 8 / 10));
                            await Task.Delay(delayMs, token);
                        }
                        catch (OperationCanceledException) { break; }
                        catch { break; }
                    }
                }, token);
            }
        }

        private static void ApplyAnimationFrame(
            LightingEffect34 effect, WmiController wmi, int step,
            byte baseR, byte baseG, byte baseB, byte brightness, byte speed,
            CancellationToken token, int generation)
        {
            if (token.IsCancellationRequested || generation != Volatile.Read(ref _currentAnimationGeneration))
                return;

            float scale = Math.Clamp(brightness, (byte)0, (byte)100) / 100f;
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
