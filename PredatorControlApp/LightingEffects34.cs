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

        public static void StopSoftwareAnimation()
        {
            lock (_animLock)
            {
                _softwareAnimCts?.Cancel();
                _softwareAnimCts?.Dispose();
                _softwareAnimCts = null;
            }
        }

        public static void ApplyEffect(
            int effectIndex, WmiController wmi,
            byte r, byte g, byte b, byte brightness, byte speed, byte direction,
            OperatingMode currentPowerMode = OperatingMode.Balanced)
        {
            StopSoftwareAnimation();

            if (effectIndex <= 7)
            {
                // Native hardware effect supported directly by EC / WMI
                wmi.SetRgbMode(effectIndex, r, g, b, brightness, speed, direction);
                return;
            }

            // Advanced effects (8..33)
            var effect = (LightingEffect34)Math.Clamp(effectIndex, 0, 33);
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
                _softwareAnimCts = new CancellationTokenSource();
                var token = _softwareAnimCts.Token;

                Task.Run(async () =>
                {
                    int step = 0;
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            ApplyAnimationFrame(effect, wmi, step++, r, g, b, brightness, speed);
                            int delayMs = Math.Max(50, 160 - (speed * 10));
                            await Task.Delay(delayMs, token);
                        }
                        catch (OperationCanceledException) { break; }
                        catch { }
                    }
                }, token);
            }
        }

        private static void ApplyAnimationFrame(
            LightingEffect34 effect, WmiController wmi, int step,
            byte baseR, byte baseG, byte baseB, byte brightness, byte speed)
        {
            double scale = brightness / 100.0;
            switch (effect)
            {
                case LightingEffect34.Snake:
                case LightingEffect34.PingPong:
                case LightingEffect34.Racing:
                    int activeZone = (step % 8) < 4 ? (step % 4) : (3 - (step % 4));
                    for (int z = 1; z <= 4; z++)
                    {
                        if (z == activeZone + 1)
                            wmi.SetZoneColor(z, (byte)(baseR * scale), (byte)(baseG * scale), (byte)(baseB * scale));
                        else
                            wmi.SetZoneColor(z, 0, 0, 0);
                    }
                    break;

                case LightingEffect34.Rainbow:
                case LightingEffect34.Disco:
                case LightingEffect34.LightShow:
                    for (int z = 1; z <= 4; z++)
                    {
                        double hue = ((step * 15) + (z * 60)) % 360;
                        var col = ColorFromHsv(hue, 1.0, scale);
                        wmi.SetZoneColor(z, col.R, col.G, col.B);
                    }
                    break;

                case LightingEffect34.Heartbeat:
                case LightingEffect34.Ripple:
                case LightingEffect34.Blasting:
                    double pulse = Math.Abs(Math.Sin(step * 0.15));
                    byte pR = (byte)(baseR * pulse * scale);
                    byte pG = (byte)(baseG * pulse * scale);
                    byte pB = (byte)(baseB * pulse * scale);
                    for (int z = 1; z <= 4; z++)
                        wmi.SetZoneColor(z, pR, pG, pB);
                    break;

                default:
                    // Color shifting between primary and complementary
                    double phase = (Math.Sin(step * 0.1) + 1.0) / 2.0;
                    byte sR = (byte)(baseR * phase * scale);
                    byte sG = (byte)(baseG * (1.0 - phase) * scale);
                    byte sB = (byte)(baseB * phase * scale);
                    for (int z = 1; z <= 4; z++)
                        wmi.SetZoneColor(z, sR, sG, sB);
                    break;
            }
        }

        private static Color ColorFromHsv(double hue, double saturation, double value)
        {
            int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
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
