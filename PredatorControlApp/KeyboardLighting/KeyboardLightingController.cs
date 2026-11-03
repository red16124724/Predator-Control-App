using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PredatorControlApp
{
    public enum KeyboardLightingScope
    {
        Zones = 0,
        PerKey = 1,
        All = 2
    }

    public sealed record KeyboardLightingStatus(
        bool Success,
        bool Degraded,
        KeyboardLightingTier AppliedTier,
        string Message,
        DateTime TimestampUtc)
    {
        public static KeyboardLightingStatus Idle { get; } = new(
            true, false, KeyboardLightingTier.FourZone, "Idle", DateTime.UtcNow);
    }

    /// <summary>
    /// Per-key transport seam. Defaults to the Acer Lighting Service; tests
    /// substitute it so the fallback logic can be exercised without hardware.
    /// </summary>
    public delegate Task<bool> KeyboardPerKeyWriter(
        int device,
        string effect,
        IReadOnlyList<(int Index, byte R, byte G, byte B)> leds,
        int brightnessPercent,
        int speedPercent,
        bool perKey);

    /// <summary>
    /// Zone transport seam. Defaults to AcerServiceClient.SetZoneLightingAsync;
    /// tests substitute it so dual-dispatch and zone fallback can be exercised without hardware.
    /// </summary>
    public delegate Task<bool> KeyboardZoneWriter(
        IReadOnlyList<Color> zoneColors,
        int brightness,
        int speed,
        CancellationToken cancellationToken);

    /// <summary>
    /// Owns keyboard lighting state and serializes hardware writes.
    ///
    /// Design goals:
    ///  - one writer at a time, so Acer WMI / service calls never interleave;
    ///  - debounce UI drags into a single write pass;
    ///  - 4-zone is the guaranteed transport;
    ///  - per-key is attempted only when the capability detector confirmed it,
    ///    and every failure degrades to 4-zone instead of silently doing nothing.
    /// </summary>
    public sealed class KeyboardLightingController : IDisposable
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _applyGate = new(1, 1);
        private readonly WmiController? _wmi;
        private readonly KeyboardPerKeyWriter? _perKeyWriter;
        private readonly KeyboardZoneWriter? _zoneWriter;

        private KeyboardLightingCapabilities _capabilities;
        private Color[] _zoneColors = new Color[4];
        private Color[] _keyColors;
        private int _brightness = 100;
        private int _speed = 50;
        private string _effect = "STATIC";
        private CancellationTokenSource? _debounceCts;
        private bool _disposed;

        public event EventHandler<KeyboardLightingStatus>? StatusChanged;

        public KeyboardLightingController(
            WmiController? wmi,
            KeyboardLightingCapabilities capabilities,
            KeyboardPerKeyWriter? perKeyWriter = null,
            KeyboardZoneWriter? zoneWriter = null)
        {
            _wmi = wmi;
            _perKeyWriter = perKeyWriter;
            _zoneWriter = zoneWriter;
            _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
            _layoutMode = capabilities.Tier switch
            {
                KeyboardLightingTier.SingleZone => 1,
                KeyboardLightingTier.ThreeZone => 3,
                _ => 4
            };

            for (int i = 0; i < _zoneColors.Length; i++) _zoneColors[i] = KeyboardLightingPalette.DefaultZoneColor;
            _keyColors = new Color[capabilities.KeyCount > 0 ? capabilities.KeyCount : KeyboardLayouts.AnsiTklKeyCount];
            for (int i = 0; i < _keyColors.Length; i++) _keyColors[i] = KeyboardLightingPalette.DefaultZoneColor;
        }

        public KeyboardLightingCapabilities Capabilities
        {
            get { lock (_sync) return _capabilities; }
        }

        public int ZoneCount => _capabilities.ZoneCount > 0 ? _capabilities.ZoneCount : 4;

        public int KeyCount
        {
            get { lock (_sync) return _keyColors.Length; }
        }

        public int Brightness
        {
            get { lock (_sync) return _brightness; }
            set { lock (_sync) _brightness = Math.Clamp(value, 0, 100); }
        }

        public int Speed
        {
            get { lock (_sync) return _speed; }
            set { lock (_sync) _speed = Math.Clamp(value, 1, 100); }
        }

        public string Effect
        {
            get { lock (_sync) return _effect; }
            set { lock (_sync) _effect = string.IsNullOrWhiteSpace(value) ? "STATIC" : value.Trim().ToUpperInvariant(); }
        }

        public int LayoutMode
        {
            get { lock (_sync) return _layoutMode; }
            set { lock (_sync) _layoutMode = value; }
        }
        private int _layoutMode = 4;

        public void UpdateCapabilities(KeyboardLightingCapabilities capabilities)
        {
            if (capabilities == null) return;
            lock (_sync)
            {
                _capabilities = capabilities;
                int wanted = capabilities.KeyCount > 0 ? capabilities.KeyCount : KeyboardLayouts.AnsiTklKeyCount;
                if (_keyColors.Length != wanted)
                {
                    var resized = new Color[wanted];
                    for (int i = 0; i < wanted; i++)
                        resized[i] = i < _keyColors.Length ? _keyColors[i] : KeyboardLightingPalette.DefaultZoneColor;
                    _keyColors = resized;
                }
            }
        }

        public Color GetZoneColor(int zone)
        {
            lock (_sync)
            {
                return zone >= 0 && zone < _zoneColors.Length ? _zoneColors[zone] : KeyboardLightingPalette.Off;
            }
        }

        public Color[] SnapshotZoneColors()
        {
            lock (_sync) return (Color[])_zoneColors.Clone();
        }

        public Color[] SnapshotKeyColors()
        {
            lock (_sync) return (Color[])_keyColors.Clone();
        }

        public void SetZoneColor(int zone, Color color)
        {
            if (zone < 0 || zone > 3) return;
            lock (_sync) _zoneColors[zone] = color;
        }

        public void SetAllZones(Color color)
        {
            lock (_sync)
            {
                for (int i = 0; i < _zoneColors.Length; i++) _zoneColors[i] = color;
            }
        }

        public void SetKeyColor(int keyIndex, Color color)
        {
            lock (_sync)
            {
                if (keyIndex < 0 || keyIndex >= _keyColors.Length) return;
                _keyColors[keyIndex] = color;
            }
        }

        public void SetKeyColors(IReadOnlyList<Color> colors)
        {
            if (colors == null) return;
            lock (_sync)
            {
                int count = Math.Min(colors.Count, _keyColors.Length);
                for (int i = 0; i < count; i++) _keyColors[i] = colors[i];
            }
        }

        public void SetAllKeys(Color color)
        {
            lock (_sync)
            {
                for (int i = 0; i < _keyColors.Length; i++) _keyColors[i] = color;
            }
        }

                public void ApplyPerZoneStaticColors(Color zone1, Color zone2, Color zone3, Color zone4, int brightness = 100)
        {
            lock (_sync)
            {
                _zoneColors[0] = zone1;
                _zoneColors[1] = zone2;
                _zoneColors[2] = zone3;
                _zoneColors[3] = zone4;
                _brightness = Math.Clamp(brightness, 0, 100);
                _effect = "STATIC";
            }
            StopDynamicEffect();
            _ = ApplyAsync(KeyboardLightingScope.Zones);
        }

        public void StopDynamicEffect()
        {
            LightingEffectsManager.StopSoftwareAnimation();
        }

        public void StartDynamicEffect(string effectName, Color primaryColor, int speed, int brightness)
        {
            if (_wmi != null)
            {
                int effectIdx = LightingEffectsManager.GetEffectIndex(effectName);
                LightingEffectsManager.ApplyEffect(
                    effectIdx,
                    _wmi,
                    primaryColor,
                    (byte)Math.Clamp(brightness, 0, 100),
                    (byte)Math.Clamp(speed, 1, 100));
            }
        }

        public void LoadState(IReadOnlyList<Color>? zones, IReadOnlyList<Color>? keys)
        {
            lock (_sync)
            {
                if (zones != null)
                {
                    for (int i = 0; i < _zoneColors.Length && i < zones.Count; i++) _zoneColors[i] = zones[i];
                }
                if (keys != null)
                {
                    for (int i = 0; i < _keyColors.Length && i < keys.Count; i++) _keyColors[i] = keys[i];
                }
            }
        }

        /// <summary>
        /// Coalesces rapid UI edits. A later call replaces the pending one.
        /// </summary>
        public void QueueApply(KeyboardLightingScope scope, int debounceMs = 180)
        {
            CancellationTokenSource cts;
            lock (_sync)
            {
                if (_disposed) return;
                if (_debounceCts != null)
                {
                    try { _debounceCts.Cancel(); } catch { }
                    try { _debounceCts.Dispose(); } catch { }
                }
                cts = new CancellationTokenSource();
                _debounceCts = cts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Math.Clamp(debounceMs, 0, 5000), cts.Token).ConfigureAwait(false);
                    await ApplyAsync(scope).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Raise(new KeyboardLightingStatus(false, false, _capabilities.Tier, ex.Message, DateTime.UtcNow));
                }
            }, cts.Token);
        }

        public async Task<KeyboardLightingStatus> ApplyAsync(KeyboardLightingScope scope)
        {
            Color[] zones;
            Color[] keys;
            int brightness;
            int speed;
            string effect;
            KeyboardLightingCapabilities caps;

            bool gateEntered = false;
            try
            {
                try
                {
                    gateEntered = await _applyGate.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return KeyboardLightingStatus.Idle;
                }

                if (!gateEntered)
                {
                    lock (_sync) caps = _capabilities;
                    return new KeyboardLightingStatus(
                        false, true, caps.Tier,
                        "Keyboard lighting apply timed out waiting for hardware gate.", DateTime.UtcNow);
                }

                lock (_sync)
                {
                    if (_disposed) return KeyboardLightingStatus.Idle;
                    zones = (Color[])_zoneColors.Clone();
                    keys = (Color[])_keyColors.Clone();
                    brightness = _brightness;
                    speed = _speed;
                    effect = _effect;
                    caps = _capabilities;
                }

                bool wantPerKey = (scope == KeyboardLightingScope.PerKey || scope == KeyboardLightingScope.All)
                                  && caps.PerKeySupported
                                  && string.Equals(effect, "STATIC", StringComparison.OrdinalIgnoreCase);

                if (wantPerKey)
                {
                    var leds = new List<(int Index, byte R, byte G, byte B)>(keys.Length);
                    for (int i = 0; i < keys.Length; i++)
                        leds.Add((i, keys[i].R, keys[i].G, keys[i].B));

                    bool ok = _perKeyWriter != null
                        ? await _perKeyWriter(1, effect, leds, brightness, speed, true).ConfigureAwait(false)
                        : await AcerServiceClient
                            .SetKeyboardLightingAsync(1, effect, leds, brightness, speed, perKey: true)
                            .ConfigureAwait(false);

                    if (ok)
                    {
                        var status = new KeyboardLightingStatus(
                            true, false, KeyboardLightingTier.PerKey,
                            $"Per-key map applied to {keys.Length} keys.", DateTime.UtcNow);
                        Raise(status);
                        return status;
                    }

                    // Service refused: fall through to the 4-zone path and report it.
                    Raise(new KeyboardLightingStatus(
                        false, true, KeyboardLightingTier.FourZone,
                        "Per-key write was not acknowledged. Falling back to 4-zone.", DateTime.UtcNow));
                }

                if ((_wmi == null && !AcerServiceClient.IsServiceReachable && _zoneWriter == null) || caps.Tier == KeyboardLightingTier.Unavailable)
                {
                    var status = new KeyboardLightingStatus(
                        false, true, KeyboardLightingTier.Unavailable,
                        "No keyboard lighting transport is available on this device.", DateTime.UtcNow);
                    Raise(status);
                    return status;
                }

                int currentLayout;
                lock (_sync) currentLayout = _layoutMode;

                int effectiveCount = caps.Tier switch
                {
                    KeyboardLightingTier.SingleZone => 1,
                    KeyboardLightingTier.ThreeZone => 3,
                    _ => (currentLayout == 1 || currentLayout == 3) ? currentLayout : 4
                };

                Color[] zonesToApply = zones.Take(effectiveCount).ToArray();

                bool zoneOk = false;
                if (_wmi != null)
                {
                    zoneOk = _wmi.SetZoneColors(zonesToApply, brightness, isNativeThreeZone: caps.Tier == KeyboardLightingTier.ThreeZone);
                }

                bool serviceZoneOk = false;
                if (!zoneOk && (AcerServiceClient.IsServiceReachable || _zoneWriter != null))
                {
                    try
                    {
                        serviceZoneOk = _zoneWriter != null
                            ? await _zoneWriter(zonesToApply, brightness, speed, CancellationToken.None).ConfigureAwait(false)
                            : await AcerServiceClient.SetZoneLightingAsync(zonesToApply, brightness, speed).ConfigureAwait(false);
                    }
                    catch { }
                }

                bool anyOk = zoneOk || serviceZoneOk;
                KeyboardLightingTier appliedTier = caps.Tier switch
                {
                    KeyboardLightingTier.SingleZone => KeyboardLightingTier.SingleZone,
                    KeyboardLightingTier.ThreeZone => KeyboardLightingTier.ThreeZone,
                    _ => KeyboardLightingTier.FourZone
                };

                string msg = caps.Tier switch
                {
                    KeyboardLightingTier.SingleZone => "Single-zone colors applied.",
                    KeyboardLightingTier.ThreeZone => "3-zone colors applied.",
                    _ => (caps.PerKeySupported ? "4-zone fallback applied." : "4-zone colors applied.")
                };

                var finalStatus = new KeyboardLightingStatus(
                    anyOk,
                    scope != KeyboardLightingScope.Zones && caps.PerKeySupported,
                    anyOk ? appliedTier : KeyboardLightingTier.Unavailable,
                    anyOk ? msg : "The Acer zone command did not complete.",
                    DateTime.UtcNow);
                Raise(finalStatus);
                return finalStatus;
            }
            finally
            {
                if (gateEntered)
                {
                    try { _applyGate.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }

        private void Raise(KeyboardLightingStatus status)
        {
            try { StatusChanged?.Invoke(this, status); } catch { }
        }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                try { _debounceCts?.Cancel(); } catch { }
                _debounceCts?.Dispose();
                _debounceCts = null;
            }

            try
            {
                if (_applyGate.Wait(TimeSpan.FromMilliseconds(500)))
                {
                    _applyGate.Release();
                }
            }
            catch { }

            try { _applyGate.Dispose(); } catch { }
        }
    }
}
