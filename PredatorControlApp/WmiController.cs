using System.Management;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class WmiController : IDisposable
    {
        private static readonly InvokeMethodOptions WmiTimeoutOptions = new() { Timeout = TimeSpan.FromMilliseconds(1500) };
        private static readonly System.Management.EnumerationOptions WmiEnumOptions = new() { Timeout = TimeSpan.FromMilliseconds(1500), ReturnImmediately = true };

        private ManagementObject? _cachedObj;
        private DateTime _lastSearchAttempt = DateTime.MinValue;
        private static readonly TimeSpan SearchRetryInterval = TimeSpan.FromSeconds(5);
        private readonly object _lock = new();
        private bool _disposed;

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveOverlayScheme(in Guid scheme);

        public static readonly Guid OVERLAY_EFFICIENCY = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
        public static readonly Guid OVERLAY_BALANCED = new("00000000-0000-0000-0000-000000000000");
        public static readonly Guid OVERLAY_PERFORMANCE = new("ded574b5-45a0-4f42-8737-46345c09c238");

        private byte _lastR = 0, _lastG = 150, _lastB = 255;
        private byte _brightness = 100;
        private byte _speed = 5;       
        private byte _direction = 0;   
        private int _lastMode = 3;     

        private byte _customCpuFanSpeed = 50;
        private byte _customGpuFanSpeed = 50;
        private byte _customSystemFanSpeed = 50;

        private int _cachedCpuTempReading;
        private int _cachedGpuTempReading;
        private int _cachedCpuRpmReading;
        private int _cachedGpuRpmReading;
        private DateTime _lastCpuTempReadTime = DateTime.MinValue;
        private DateTime _lastGpuTempReadTime = DateTime.MinValue;
        private DateTime _lastCpuRpmReadTime = DateTime.MinValue;
        private DateTime _lastGpuRpmReadTime = DateTime.MinValue;
        private static readonly TimeSpan SensorCacheDuration = TimeSpan.FromMilliseconds(800);

        public byte LastR => _lastR;
        public byte LastG => _lastG;
        public byte LastB => _lastB;
        public byte Brightness => _brightness;
        public byte Speed => _speed;
        public byte Direction => _direction;
        public int LastRgbMode => _lastMode;
        public byte CustomCpuFanSpeed { get { lock (_lock) return _customCpuFanSpeed; } }
        public byte CustomGpuFanSpeed { get { lock (_lock) return _customGpuFanSpeed; } }
        public byte CustomSystemFanSpeed { get { lock (_lock) return _customSystemFanSpeed; } }

        public EcHidDevice? EcHid { get; set; }

        public WmiController()
        {
            try
            {
                EcHid = EcHidDevice.TryOpen();
            }
            catch { }
        }

        private static bool IsComOrWmiException(Exception ex) =>
            ex is COMException || ex is ManagementException ||
            ex.InnerException is COMException || ex.InnerException is ManagementException;

        private ManagementObject? GetWmiObjectUnderLock()
        {
            if (_disposed) return null;
            if (_cachedObj != null) return _cachedObj;
            if ((DateTime.UtcNow - _lastSearchAttempt) < SearchRetryInterval) return null;

            _lastSearchAttempt = DateTime.UtcNow;
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM AcerGamingFunction", WmiEnumOptions);
                using var results = searcher.Get();
                _cachedObj = results.Cast<ManagementObject>().FirstOrDefault();
            }
            catch { _cachedObj = null; }
            return _cachedObj;
        }

        private void InvalidateCacheUnderLock()
        {
            try { _cachedObj?.Dispose(); } catch { }
            _cachedObj = null;
            _lastAppliedPowerMode = null;
            _lastSearchAttempt = DateTime.MinValue;
        }

        private ManagementObject? _cachedActionObj;
        private DateTime _lastActionSearchAttempt = DateTime.MinValue;

        private ManagementObject? GetActionObjectUnderLock()
        {
            if (_disposed) return null;
            if (_cachedActionObj != null) return _cachedActionObj;
            if ((DateTime.UtcNow - _lastActionSearchAttempt) < SearchRetryInterval) return null;

            _lastActionSearchAttempt = DateTime.UtcNow;
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM APGeAction", WmiEnumOptions);
                using var results = searcher.Get();
                _cachedActionObj = results.Cast<ManagementObject>().FirstOrDefault();
            }
            catch { _cachedActionObj = null; }
            return _cachedActionObj;
        }

        private void InvalidateActionCacheUnderLock()
        {
            try { _cachedActionObj?.Dispose(); } catch { }
            _cachedActionObj = null;
            _lastActionSearchAttempt = DateTime.MinValue;
        }

        private (bool success, ulong output) SendActionCommandUnderLock(string method, ulong input)
        {
            try
            {
                var obj = GetActionObjectUnderLock();
                if (obj == null) return (false, 0);

                using var inParams = obj.GetMethodParameters(method);
                if (inParams != null)
                {
                    var prop = inParams.Properties.Cast<PropertyData>().FirstOrDefault();
                    if (prop != null)
                    {
                        prop.Value = prop.Type switch
                        {
                            CimType.UInt8 => (byte)input,
                            CimType.UInt16 => (ushort)input,
                            CimType.UInt32 => (uint)input,
                            _ => input
                        };
                    }
                }
                using var outParams = obj.InvokeMethod(method, inParams, WmiTimeoutOptions);
                if (outParams != null)
                {
                    foreach (PropertyData prop in outParams.Properties)
                    {
                        if (prop.Name != "ReturnValue" && !prop.IsArray && prop.Value != null)
                        {
                            ulong result = Convert.ToUInt64(prop.Value);
                            return ((result & 0xFF) == 0, result);
                        }
                    }
                }
                return (false, 0);
            }
            catch (Exception ex)
            {
                if (IsComOrWmiException(ex))
                {
                    InvalidateActionCacheUnderLock();
                }
                return (false, 0);
            }
        }

        private (bool success, ulong output) SendCommand(string method, ulong input)
        {
            lock (_lock)
            {
                return SendCommandUnderLock(method, input);
            }
        }

        private (bool success, ulong output) SendCommandUnderLock(string method, ulong input)
        {
            try
            {
                var obj = GetWmiObjectUnderLock();
                if (obj == null) return (false, 0);

                using var inParams = obj.GetMethodParameters(method);
                inParams["gmInput"] = input;
                using var outParams = obj.InvokeMethod(method, inParams, WmiTimeoutOptions);
                if (outParams != null && outParams["gmOutput"] != null)
                {
                    ulong result = Convert.ToUInt64(outParams["gmOutput"]);
                    return ((result & 0xFF) == 0, result);
                }
                return (false, 0);
            }
            catch (Exception ex)
            {
                if (IsComOrWmiException(ex))
                {
                    InvalidateCacheUnderLock();
                }
                return (false, 0);
            }
        }

        private bool SendLedCommand(byte[] payload)
        {
            lock (_lock)
            {
                return SendLedCommandUnderLock(payload);
            }
        }

        private bool SendLedCommandUnderLock(byte[] payload)
        {
            try
            {
                var obj = GetWmiObjectUnderLock();
                if (obj == null) return false;

                using var inParams = obj.GetMethodParameters("SetGamingKBBacklight");
                inParams["gmInput"] = payload;
                using var outParams = obj.InvokeMethod("SetGamingKBBacklight", inParams, WmiTimeoutOptions);
                if (outParams != null && outParams["gmOutput"] != null)
                {
                    ulong result = Convert.ToUInt64(outParams["gmOutput"]);
                    return (result & 0xFF) == 0;
                }
                return false;
            }
            catch (Exception ex)
            {
                if (IsComOrWmiException(ex))
                {
                    InvalidateCacheUnderLock();
                }
                return false;
            }
        }

        private bool SendLedArrayCommandUnderLock(string method, byte[] payload)
        {
            try
            {
                var obj = GetWmiObjectUnderLock();
                if (obj == null) return false;

                using var inParams = obj.GetMethodParameters(method);
                inParams["gmInput"] = payload;
                using var outParams = obj.InvokeMethod(method, inParams, WmiTimeoutOptions);
                if (outParams != null && outParams["gmOutput"] != null)
                {
                    ulong result = Convert.ToUInt64(outParams["gmOutput"]);
                    return (result & 0xFF) == 0;
                }
                return false;
            }
            catch (Exception ex)
            {
                if (IsComOrWmiException(ex))
                {
                    InvalidateCacheUnderLock();
                }
                return false;
            }
        }

        public static int MaskTachometerRpm(ulong raw)
        {
            if ((raw & 0xFF) == 0) return (int)((raw >> 8) & 0x1FFF);
            return 0;
        }

        public static int DecodeFanSpeed(ulong raw)
        {
            // If raw > 0x1FFF (8191), it cannot be a direct 13-bit tachometer value and is encoded
            // as an ACPI packet: (rpm << 8) | status_byte
            if (raw > 0x1FFF)
            {
                return MaskTachometerRpm(raw);
            }
            // Otherwise, it is a direct 13-bit fan speed reading from Method 17
            return (int)(raw & 0x1FFF);
        }

        public int GetSensorReading(ulong sensorId)
        {
            lock (_lock)
            {
                try
                {
                    var obj = GetWmiObjectUnderLock();
                    if (obj == null) return 0;

                    using var inParams = obj.GetMethodParameters("GetGamingSysInfo");
                    inParams["gmInput"] = (uint)(0x0001 | (sensorId << 8));
                    using var outParams = obj.InvokeMethod("GetGamingSysInfo", inParams, WmiTimeoutOptions);
                    ulong raw = Convert.ToUInt64(outParams["gmOutput"]);

                    if (sensorId == (ulong)SensorId.CpuFanSpeed || sensorId == (ulong)SensorId.GpuFanSpeed ||
                        sensorId == (ulong)SensorId.SystemFanSpeed || sensorId == (ulong)SensorId.System2FanSpeed ||
                        sensorId == (ulong)SensorId.Gpu2FanSpeed)
                    {
                        int rpm = MaskTachometerRpm(raw);
                        if (rpm > 0) return rpm;
                    }

                    int val = (int)((raw >> 8) & 0xFFFF);
                    if (val == 0) val = (int)(raw & 0xFF);
                    return val;
                }
                catch (Exception ex)
                {
                    if (IsComOrWmiException(ex))
                    {
                        InvalidateCacheUnderLock();
                    }
                    return 0;
                }
            }
        }

        public int GetGamingFanSpeed(ulong fanType)
        {
            lock (_lock)
            {
                try
                {
                    var obj = GetWmiObjectUnderLock();
                    if (obj == null) return 0;

                    using var inParams = obj.GetMethodParameters("GetGamingFanSpeed");
                    inParams["gmInput"] = fanType;
                    using var outParams = obj.InvokeMethod("GetGamingFanSpeed", inParams, WmiTimeoutOptions);
                    ulong raw = Convert.ToUInt64(outParams["gmOutput"]);
                    return DecodeFanSpeed(raw);
                }
                catch (Exception ex)
                {
                    if (IsComOrWmiException(ex))
                    {
                        InvalidateCacheUnderLock();
                    }
                    return 0;
                }
            }
        }

        public static (byte R, byte G, byte B) GetModeLedColor(byte mode) => mode switch
        {
            0 => (0, 220, 255),   // Quiet: Cyan
            1 => (0, 150, 255),   // Balanced: Blue
            4 => (255, 140, 0),   // Performance: Orange
            5 => (255, 20, 50),   // Turbo: Red
            6 => (0, 255, 120),   // Eco: Green
            _ => (0, 150, 255)
        };

        public void UpdateModeKeyLed(byte mode)
        {
            lock (_lock)
            {
                try
                {
                    var (r, g, b) = GetModeLedColor(mode);
                    ulong logoColorPayload = ((ulong)b << 24) | ((ulong)g << 16) | ((ulong)r << 8) | 0x01ul;
                    SendCommandUnderLock("SetGamingLEDColor", logoColorPayload);
                }
                catch { }
            }
        }

        private byte? _lastAppliedPowerMode;
        public byte? LastAppliedPowerMode { get { lock (_lock) { return _lastAppliedPowerMode; } } }

        private bool _suppressGpuQueries;
        public bool SuppressGpuQueries
        {
            get { lock (_lock) { return _suppressGpuQueries; } }
            set
            {
                lock (_lock)
                {
                    _suppressGpuQueries = value;
                    if (value)
                    {
                        _cachedGpuTempReading = 0;
                        _cachedGpuRpmReading = 0;
                    }
                }
            }
        }

        public bool TrySetPowerMode(byte mode, bool forceRetry = false)
        {
            bool isModeSwitch;
            bool success;
            lock (_lock)
            {
                if (forceRetry && _cachedObj == null)
                {
                    _lastSearchAttempt = DateTime.MinValue;
                }
                if (EcHid != null && EcHid.IsOpen)
                {
                    try { EcHid.WriteMode(AcerProtocol.WmiModeToEcMode(mode)); } catch { }
                }
                isModeSwitch = !_lastAppliedPowerMode.HasValue || _lastAppliedPowerMode.Value != mode;
                if (isModeSwitch && mode != 1)
                {
                    // Acer EC firmware requires stepping through Balanced (1) before switching to target mode
                    SendCommandUnderLock("SetGamingMiscSetting", (ulong)0x0B | (1UL << 8));
                }
                var res = SendCommandUnderLock("SetGamingMiscSetting", (ulong)0x0B | ((ulong)mode << 8));
                if (!res.success)
                {
                    res = SendCommandUnderLock("SetGamingProfile", (ulong)mode);
                }
                _lastAppliedPowerMode = mode;
                if (res.success && _cachedObj != null)
                {
                    UpdateModeKeyLed(mode);
                }
                success = res.success || (EcHid != null && EcHid.IsOpen);
            }

            if (isModeSwitch && success)
            {
                SyncWindowsPowerMode(mode);
            }
            return success;
        }

        public void SetPowerMode(byte mode)
        {
            TrySetPowerMode(mode, false);
        }

        public static ulong BuildFanBehaviorPayload(byte mode)
        {
            // Bit 0: CPU fan (GroupBit 0 -> mask bit 0 = 0x01, shift 16)
            // Bit 1: System fan (GroupBit 1 -> mask bit 1 = 0x02, shift 18)
            // Bit 3: GPU fan (GroupBit 3 -> mask bit 3 = 0x08, shift 22)
            // Combined mask = 0x01 | 0x02 | 0x08 = 0x0B
            return (ulong)(0x0B | ((ulong)mode << 16) | ((ulong)mode << 18) | ((ulong)mode << 22));
        }

        public static (ulong direct, ulong extended) BuildCpuFanSpeedPayloads(byte percentage)
        {
            byte clamped = Math.Clamp(percentage, (byte)10, (byte)100);
            ulong direct = 0x01UL | ((ulong)clamped << 8);
            ulong extended = 0x05UL | (1UL << 8) | ((ulong)clamped << 16);
            return (direct, extended);
        }

        public static (ulong directPrimary, ulong directAlt, ulong extendedPrimary, ulong extendedAlt) BuildGpuFanSpeedPayloads(byte percentage)
        {
            byte clamped = Math.Clamp(percentage, (byte)10, (byte)100);
            ulong directPrimary = 0x04UL | ((ulong)clamped << 8);
            ulong directAlt = 0x04UL | ((ulong)clamped << 8);
            ulong extendedPrimary = 0x05UL | (2UL << 8) | ((ulong)clamped << 16);
            ulong extendedAlt = 0x05UL | (4UL << 8) | ((ulong)clamped << 16);
            return (directPrimary, directAlt, extendedPrimary, extendedAlt);
        }

        public void SetFanBehavior(byte mode, bool applyCustomSpeeds = true)
        {
            lock (_lock)
            {
                SendCommandUnderLock("SetGamingFanBehavior", BuildFanBehaviorPayload(mode));

                if (mode == 0x03 && applyCustomSpeeds)
                {
                    SetFanSpeed(1UL, _customCpuFanSpeed);
                    SetFanSpeed(4UL, _customGpuFanSpeed);
                    SetFanSpeed(2UL, _customSystemFanSpeed);
                }
            }
        }

        public bool SetFanSpeed(FanChannel fan, int percent)
        {
            byte clamped = (byte)Math.Clamp(percent, 10, 100);
            if (fan.Chip == FanChip.Cpu)
            {
                SetCpuFanSpeed(clamped);
                return true;
            }
            if (fan.Chip == FanChip.System)
            {
                SetSystemFanSpeed(clamped);
                return true;
            }
            SetGpuFanSpeed(clamped);
            return true;
        }

        public void SetFanSpeed(ulong fanType, byte percentage)
        {
            byte clamped = Math.Clamp(percentage, (byte)10, (byte)100);

            lock (_lock)
            {
                if (fanType == 1) _customCpuFanSpeed = clamped;
                else if (fanType == 2) _customSystemFanSpeed = clamped;
                else if (fanType == 4) _customGpuFanSpeed = clamped;

                if (fanType == 1)
                {
                    var (direct, extended) = BuildCpuFanSpeedPayloads(clamped);
                    SendCommandUnderLock("SetGamingFanSpeed", direct);
                    SendCommandUnderLock("SetGamingFanSpeed", extended);
                }
                else if (fanType == 2)
                {
                    ulong payload = AcerProtocol.FanSpeedInput(FanChannel.System, clamped);
                    SendCommandUnderLock("SetGamingFanSpeed", payload);
                    SendCommandUnderLock("SetGamingFanSpeed", 0x05UL | (3UL << 8) | ((ulong)clamped << 16));
                }
                else
                {
                    var (directPri, directAlt, extPri, extAlt) = BuildGpuFanSpeedPayloads(clamped);
                    SendCommandUnderLock("SetGamingFanSpeed", directPri);
                    SendCommandUnderLock("SetGamingFanSpeed", directAlt);
                    SendCommandUnderLock("SetGamingFanSpeed", extPri);
                    SendCommandUnderLock("SetGamingFanSpeed", extAlt);
                }
            }
        }

        public void SetCpuFanSpeed(byte percentage) => SetFanSpeed(1UL, percentage);
        public void SetGpuFanSpeed(byte percentage) => SetFanSpeed(4UL, percentage);
        public void SetSystemFanSpeed(byte percentage) => SetFanSpeed(2UL, percentage);
        public void SetFanSpeed(byte cpuPercentage, byte gpuPercentage)
        {
            SetCpuFanSpeed(cpuPercentage);
            SetGpuFanSpeed(gpuPercentage);
        }
        public void SetFanSpeed(byte cpuPercentage, byte gpuPercentage, byte sysPercentage)
        {
            SetCpuFanSpeed(cpuPercentage);
            SetGpuFanSpeed(gpuPercentage);
            SetSystemFanSpeed(sysPercentage);
        }

        public void SetRgbMode(int mode, byte r, byte g, byte b, byte brightness, byte speed, byte direction)
        {
            _lastR = r; _lastG = g; _lastB = b;
            _brightness = Math.Clamp(brightness, (byte)0, (byte)100);
            _speed = Math.Clamp(speed, (byte)1, (byte)9);
            _direction = (byte)(direction != 0 ? 1 : 0);
            _lastMode = Math.Clamp(mode, 0, 10);
            ApplyLightingMode(_lastMode);
        }

        public void SetBrightness(byte brightness)
        {
            _brightness = Math.Clamp(brightness, (byte)0, (byte)100);
            if (_brightness == 0)
                TurnOffBacklight();
            else
                ApplyLightingMode(_lastMode);
        }

        public void TurnOffBacklight()
        {
            try { LightingEffectsManager.StopSoftwareAnimation(); } catch { }
            lock (_lock)
            {
                _brightness = 0;

                try
                {
                    // Exterior LED / Logo off via WMI
                    SendCommandUnderLock("SetGamingLEDBehavior", 0x06ul | (0x01ul << 8));
                    SendCommandUnderLock("SetGamingLEDBehavior", 0x06ul | (0x0Ful << 8));
                    SendCommandUnderLock("SetGamingLEDColor", 0x01ul);
                    SendCommandUnderLock("SetGamingLEDColor", 0x0Ful);

                    // Dual-dispatch 4-zone off (zone bitmasks: 1, 2, 4, 8)
                    ulong[] zoneMasks = { 1UL, 2UL, 4UL, 8UL };
                    foreach (ulong mask in zoneMasks)
                    {
                        SendCommandUnderLock("SetGamingRgbKb", mask);
                    }

                    // Main 16-byte packet off
                    byte[] payload = new byte[16];
                    payload[0] = 0;
                    payload[1] = 0;
                    payload[2] = 0;
                    SendLedArrayCommandUnderLock("SetGamingKBBacklight", payload);
                    SendLedArrayCommandUnderLock("SetGamingLED", payload);

                    AcerServiceClient.SetLogoLightingAsync(0, 0, 0, 0, 0, 0);
                }
                catch { }
            }
        }

        public void SetSpeed(byte speed)
        {
            _speed = Math.Clamp(speed, (byte)1, (byte)9);
            ApplyLightingMode(_lastMode);
        }

        public void SetDirection(byte direction)
        {
            _direction = (byte)(direction != 0 ? 1 : 0);
            ApplyLightingMode(_lastMode);
        }

        public void SetStaticColor(byte r, byte g, byte b, byte brightness)
        {
            _lastR = r; _lastG = g; _lastB = b;
            _brightness = brightness;
            _lastMode = 0;
            if (brightness == 0)
                TurnOffBacklight();
            else
                ApplyLightingMode(0);
        }

        public void SetStaticColor(int r, int g, int b, int brightness)
        {
            byte cr = (byte)Math.Clamp(r, 0, 255);
            byte cg = (byte)Math.Clamp(g, 0, 255);
            byte cb = (byte)Math.Clamp(b, 0, 255);
            byte cBright = (byte)Math.Clamp(brightness, 0, 100);
            SetStaticColor(cr, cg, cb, cBright);
        }

        public void SetStaticColor(Color color, int brightness)
        {
            float alphaScale = Math.Clamp(color.A, (byte)0, (byte)255) / 255f;
            byte r = (byte)Math.Clamp(Math.Round(color.R * alphaScale), 0, 255);
            byte g = (byte)Math.Clamp(Math.Round(color.G * alphaScale), 0, 255);
            byte b = (byte)Math.Clamp(Math.Round(color.B * alphaScale), 0, 255);
            byte cBright = (byte)Math.Clamp(Math.Round(brightness * alphaScale), 0, 100);
            SetStaticColor(r, g, b, cBright);
        }

        public bool SetZoneColor(int zone, int r, int g, int b)
        {
            if (zone < 0 || zone > 4) return false;
            byte cr = (byte)Math.Clamp(r, 0, 255);
            byte cg = (byte)Math.Clamp(g, 0, 255);
            byte cb = (byte)Math.Clamp(b, 0, 255);
            return SetZoneColor(zone, cr, cg, cb);
        }

        public bool SetZoneColor(int zone, Color color)
        {
            if (zone < 0 || zone > 4) return false;
            float alphaScale = Math.Clamp(color.A, (byte)0, (byte)255) / 255f;
            byte r = (byte)Math.Clamp(Math.Round(color.R * alphaScale), 0, 255);
            byte g = (byte)Math.Clamp(Math.Round(color.G * alphaScale), 0, 255);
            byte b = (byte)Math.Clamp(Math.Round(color.B * alphaScale), 0, 255);
            return SetZoneColor(zone, r, g, b);
        }

        public bool SetZoneColor(int zone, byte r, byte g, byte b)
        {
            if (zone < 0 || zone > 4) return false;
            lock (_lock)
            {
                if (_disposed) return false;
                byte scaledR = (byte)((r * _brightness) / 100);
                byte scaledG = (byte)((g * _brightness) / 100);
                byte scaledB = (byte)((b * _brightness) / 100);
                if (_brightness > 0)
                {
                    if (r > 0 && scaledR == 0) scaledR = 1;
                    if (g > 0 && scaledG == 0) scaledG = 1;
                    if (b > 0 && scaledB == 0) scaledB = 1;
                }
                ulong zoneMask = (zone >= 1 && zone <= 4) ? (1UL << (zone - 1)) : (zone == 0 ? 0x0FUL : (ulong)zone);
                ulong payload = zoneMask | ((ulong)scaledR << 8) | ((ulong)scaledG << 16) | ((ulong)scaledB << 24);
                var res = SendCommandUnderLock("SetGamingRgbKb", payload);
                return res.success || _cachedObj == null;
            }
        }

        public void ApplyLightingMode(int mode)
        {
            lock (_lock)
            {
                if (_brightness == 0)
                {
                    TurnOffBacklight();
                    return;
                }

                byte r = _lastR;
                byte g = _lastG;
                byte b = _lastB;
                if (r == 0 && g == 0 && b == 0)
                {
                    r = 0; g = 150; b = 255;
                }
                byte scaledR = (byte)((r * _brightness) / 100);
                byte scaledG = (byte)((g * _brightness) / 100);
                byte scaledB = (byte)((b * _brightness) / 100);

                if (_brightness > 0)
                {
                    if (r > 0 && scaledR == 0) scaledR = 1;
                    if (g > 0 && scaledG == 0) scaledG = 1;
                    if (b > 0 && scaledB == 0) scaledB = 1;
                }

                // 1. Exterior LED Behavior & Color (Follows keyboard RGB across all modes)
                try
                {
                    ulong ledBehavior = mode switch
                    {
                        1 => 0x01ul,      // Breathing
                        2 or 3 => 0x02ul, // Neon
                        _ => (mode == 0 ? 0x00ul : 0x02ul)
                    };

                    // Zone 0x0F = all exterior zones
                    ulong zonePayload = ledBehavior | (0x0Ful << 8)
                        | ((ulong)scaledR << 16) | ((ulong)scaledG << 24) | ((ulong)scaledB << 32);
                    SendCommandUnderLock("SetGamingLEDBehavior", zonePayload);

                    // Zone 0x01 = lid logo zone specifically
                    ulong logoPayload = ledBehavior | (0x01ul << 8)
                        | ((ulong)scaledR << 16) | ((ulong)scaledG << 24) | ((ulong)scaledB << 32);
                    SendCommandUnderLock("SetGamingLEDBehavior", logoPayload);

                    // SetGamingLEDColor: Byte 0=Zone, Byte 1=R, Byte 2=G, Byte 3=B
                    ulong logoColorPayload = ((ulong)scaledB << 24) | ((ulong)scaledG << 16) | ((ulong)scaledR << 8) | 0x01ul;
                    SendCommandUnderLock("SetGamingLEDColor", logoColorPayload);

                    ulong exteriorColorPayload = ((ulong)scaledB << 24) | ((ulong)scaledG << 16) | ((ulong)scaledR << 8) | 0x0Ful;
                    SendCommandUnderLock("SetGamingLEDColor", exteriorColorPayload);

                    // Dual dispatch to AcerService device 4 (Logo Device) for Helios/Predator models
                    AcerServiceClient.SetLogoLightingAsync(mode, r, g, b, _brightness, _speed);
                }
                catch { }

                // 2. Dual-dispatch SetGamingRgbKb for 4-zone keyboards (Static and solid modes)
                if (mode == 0 || mode == 1)
                {
                    try
                    {
                        for (int zone = 1; zone <= 4; zone++)
                        {
                            ulong zoneMask = 1UL << (zone - 1);
                            ulong rgbKbPayload = zoneMask | ((ulong)scaledR << 8) | ((ulong)scaledG << 16) | ((ulong)scaledB << 24);
                            SendCommandUnderLock("SetGamingRgbKb", rgbKbPayload);
                        }
                    }
                    catch { }
                }

                // 3. Primary: SetGamingKBBacklight 16-byte packet and SetGamingLED matching OpenSense
                byte[] payload = new byte[16];
                payload[0] = (byte)mode;     
                payload[1] = (byte)(mode != 0 ? Math.Clamp((int)_speed, 1, 9) : 0);         
                payload[2] = (byte)Math.Clamp((int)_brightness, 0, 100);    
                payload[3] = (byte)(mode == 3 ? 8 : 0); // Wave effect flag
                payload[4] = (byte)(mode is 3 or 4 or 5 ? _direction : 0); // Direction
                payload[5] = scaledR;
                payload[6] = scaledG;
                payload[7] = scaledB;
                if (mode == 6 || mode == 7) // Meteor or Twinkling
                {
                    payload[8] = 3;
                }
                else
                {
                    Array.Copy(payload, 0, payload, 8, 8);
                }
                payload[9] = 1;              
                SendLedCommandUnderLock(payload);

                // Exterior LED / Logo payload: Single-LED hardware only accepts Static (0), Breathing (1), or Neon (2).
                // Map dynamic modes (Wave=3, etc.) to Neon (2) so the lid logo cycles colors alongside the keyboard.
                byte[] logoPayloadArr = (byte[])payload.Clone();
                logoPayloadArr[0] = (byte)(mode switch
                {
                    1 => 1,          // Breathing
                    2 or 3 => 2,     // Neon
                    _ => (mode == 0 ? 0 : 2)
                });
                SendLedArrayCommandUnderLock("SetGamingLED", logoPayloadArr);
            }
        }

        public virtual int CpuTemp
        {
            get
            {
                lock (_lock)
                {
                    if (DateTime.UtcNow - _lastCpuTempReadTime < SensorCacheDuration && _cachedCpuTempReading > 0)
                        return _cachedCpuTempReading;

                    _lastCpuTempReadTime = DateTime.UtcNow;
                    int temp = GetSensorReading(0x01);
                    if (temp > 0 && temp <= 125)
                    {
                        _cachedCpuTempReading = temp;
                        return temp;
                    }
                    return _cachedCpuTempReading;
                }
            }
        }

        public virtual int GpuTemp
        {
            get
            {
                lock (_lock)
                {
                    if (_suppressGpuQueries) return 0;
                    if (DateTime.UtcNow - _lastGpuTempReadTime < SensorCacheDuration && _cachedGpuTempReading > 0)
                        return _cachedGpuTempReading;

                    _lastGpuTempReadTime = DateTime.UtcNow;
                    int temp = GetSensorReading(0x0A);
                    if (temp > 0 && temp <= 125)
                    {
                        _cachedGpuTempReading = temp;
                        return temp;
                    }

                    _cachedGpuTempReading = 0;
                    return 0;
                }
            }
        }

        public static bool IsDutyCyclePayload(int val)
        {
            // Detect raw Acer duty cycle payload: 2560 is 10% duty (0x0A00 = 10 << 8).
            // Any multiple of 256 where (val >> 8) <= 100 and (val & 0xFF) == 0 is a duty cycle percentage, not tachometer RPM.
            return val == 2560 || (val > 0 && (val & 0xFF) == 0 && (val >> 8) <= 100);
        }

        public virtual int CpuFanRpm
        {
            get
            {
                lock (_lock)
                {
                    if (DateTime.UtcNow - _lastCpuRpmReadTime < SensorCacheDuration && _cachedCpuRpmReading > 0)
                        return _cachedCpuRpmReading;

                    _lastCpuRpmReadTime = DateTime.UtcNow;
                    int rpm = GetSensorReading(0x02);
                    if (rpm > 0 && rpm <= 15000 && !IsDutyCyclePayload(rpm))
                    {
                        _cachedCpuRpmReading = rpm;
                        return rpm;
                    }

                    int legacyRpm = GetGamingFanSpeed(0x01);
                    if (legacyRpm > 0 && legacyRpm <= 15000 && !IsDutyCyclePayload(legacyRpm))
                    {
                        _cachedCpuRpmReading = legacyRpm;
                        return legacyRpm;
                    }

                    return _cachedCpuRpmReading;
                }
            }
        }

        public virtual int GpuFanRpm
        {
            get
            {
                lock (_lock)
                {
                    if (_suppressGpuQueries) return 0;
                    if (DateTime.UtcNow - _lastGpuRpmReadTime < SensorCacheDuration && _cachedGpuRpmReading > 0)
                        return _cachedGpuRpmReading;

                    _lastGpuRpmReadTime = DateTime.UtcNow;
                    int rpm = GetSensorReading(0x06);
                    if (rpm > 0 && rpm <= 15000 && !IsDutyCyclePayload(rpm))
                    {
                        _cachedGpuRpmReading = rpm;
                        return rpm;
                    }

                    int legacyRpm = GetGamingFanSpeed(0x04);
                    if (legacyRpm > 0 && legacyRpm <= 15000 && !IsDutyCyclePayload(legacyRpm))
                    {
                        _cachedGpuRpmReading = legacyRpm;
                        return legacyRpm;
                    }

                    return _cachedGpuRpmReading;
                }
            }
        }

        private int _cachedSystemRpmReading;
        private DateTime _lastSystemRpmReadTime = DateTime.MinValue;
        private bool _systemFanChecked;
        private bool _hasSystemFan = true;

        public virtual int SystemFanRpm
        {
            get
            {
                lock (_lock)
                {
                    if (DateTime.UtcNow - _lastSystemRpmReadTime < SensorCacheDuration)
                        return _cachedSystemRpmReading;

                    if (_systemFanChecked && !_hasSystemFan && (DateTime.UtcNow - _lastSystemRpmReadTime).TotalSeconds < 10)
                        return 0;

                    _lastSystemRpmReadTime = DateTime.UtcNow;
                    int rpm = GetSensorReading((ulong)SensorId.SystemFanSpeed);
                    if (rpm == 0 || rpm > 15000)
                        rpm = GetSensorReading((ulong)SensorId.System2FanSpeed);

                    _systemFanChecked = true;
                    if (rpm > 0 && rpm <= 15000)
                    {
                        _hasSystemFan = true;
                        _cachedSystemRpmReading = rpm;
                        return rpm;
                    }

                    _hasSystemFan = false;
                    _cachedSystemRpmReading = 0;
                    return 0;
                }
            }
        }

        public static Guid GetOverlayForMode(byte acerMode) => acerMode switch
        {
            0x00 or 0x06 => OVERLAY_EFFICIENCY,
            0x04 or 0x05 => OVERLAY_PERFORMANCE,
            _ => OVERLAY_BALANCED
        };

        private byte? _lastWindowsOverlayMode;

        private void SyncWindowsPowerMode(byte acerMode)
        {
            if (_lastWindowsOverlayMode == acerMode) return;
            try
            {
                Guid overlay = GetOverlayForMode(acerMode);
                PowerSetActiveOverlayScheme(in overlay);
                _lastWindowsOverlayMode = acerMode;
            }
            catch { }
        }

        private ManagementObject? _cachedBatteryObj;
        private DateTime _lastBatterySearchAttempt = DateTime.MinValue;

        private ManagementObject? GetBatteryControlObjectUnderLock()
        {
            if (_disposed) return null;
            if (_cachedBatteryObj != null) return _cachedBatteryObj;
            if ((DateTime.UtcNow - _lastBatterySearchAttempt) < SearchRetryInterval) return null;

            _lastBatterySearchAttempt = DateTime.UtcNow;
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryControl", WmiEnumOptions);
                using var results = searcher.Get();
                _cachedBatteryObj = results.Cast<ManagementObject>().FirstOrDefault();
            }
            catch
            {
                _cachedBatteryObj = null;
            }
            return _cachedBatteryObj;
        }

        private void InvalidateBatteryCacheUnderLock()
        {
            try { _cachedBatteryObj?.Dispose(); } catch { }
            _cachedBatteryObj = null;
            _lastBatterySearchAttempt = DateTime.MinValue;
        }

        public bool SetBatteryChargeLimit(bool enable)
        {
            lock (_lock)
            {
                try
                {
                    var obj = GetBatteryControlObjectUnderLock();
                    if (obj == null) return false;

                    using var inParams = obj.GetMethodParameters("SetBatteryHealthControl");
                    inParams["uBatteryNo"] = (byte)1;
                    inParams["uFunctionMask"] = (byte)1;
                    inParams["uFunctionStatus"] = (byte)(enable ? 1 : 0);
                    inParams["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };

                    using var outParams = obj.InvokeMethod("SetBatteryHealthControl", inParams, WmiTimeoutOptions);
                    var rawRet = outParams["uReturn"];
                    if (rawRet is byte[] retBytes)
                    {
                        if (retBytes.Length > 0 && retBytes[0] != 0) return false;
                        return true;
                    }
                    ushort result = Convert.ToUInt16(rawRet);
                    return result == 0;
                }
                catch
                {
                    InvalidateBatteryCacheUnderLock();
                    return false;
                }
            }
        }

        public bool IsBatteryControlSupported()
        {
            lock (_lock)
            {
                try
                {
                    var obj = GetBatteryControlObjectUnderLock();
                    return obj != null;
                }
                catch
                {
                    InvalidateBatteryCacheUnderLock();
                    return false;
                }
            }
        }

        public static (ulong onPayload, ulong offPayload) GetLcdOverdrivePayloads()
        {
            return (0x1000000000010UL, 0x10UL);
        }

        public bool SetLcdOverdrive(bool enable)
        {
            lock (_lock)
            {
                if (_disposed) return false;
                var (onPayload, offPayload) = GetLcdOverdrivePayloads();
                var res = SendCommandUnderLock("SetGamingProfile", enable ? onPayload : offPayload);
                try { AcerServiceClient.SetLcdOverdriveAsync(enable); } catch { }
                return res.success;
            }
        }

        public bool IsBatteryCalibrationSupported()
        {
            lock (_lock)
            {
                try
                {
                    var obj = GetBatteryControlObjectUnderLock();
                    if (obj == null) return false;

                    using var inParams = obj.GetMethodParameters("GetBatteryHealthControlStatus");
                    inParams["uBatteryNo"] = (byte)1;
                    inParams["uFunctionQuery"] = (byte)1;
                    inParams["uReserved"] = new byte[2];

                    using var outParams = obj.InvokeMethod("GetBatteryHealthControlStatus", inParams, WmiTimeoutOptions);
                    if (outParams == null || outParams["uFunctionList"] == null) return false;
                    ulong list = Convert.ToUInt64(outParams["uFunctionList"]);
                    return (list & 2UL) != 0;
                }
                catch
                {
                    InvalidateBatteryCacheUnderLock();
                    return false;
                }
            }
        }

        public BatteryHealthStatus? GetBatteryHealthStatus()
        {
            lock (_lock)
            {
                try
                {
                    var obj = GetBatteryControlObjectUnderLock();
                    if (obj == null) return null;

                    using var inParams = obj.GetMethodParameters("GetBatteryHealthControlStatus");
                    inParams["uBatteryNo"] = (byte)1;
                    inParams["uFunctionQuery"] = (byte)1;
                    inParams["uReserved"] = new byte[2];

                    using var outParams = obj.InvokeMethod("GetBatteryHealthControlStatus", inParams, WmiTimeoutOptions);
                    if (outParams == null) return null;

                    var retBytes = outParams["uReturn"] as byte[];
                    if (retBytes != null && retBytes.Any(b => b != 0)) return null;

                    if (outParams["uFunctionList"] == null || outParams["uFunctionStatus"] == null) return null;
                    ulong list = Convert.ToUInt64(outParams["uFunctionList"]);
                    var status = outParams["uFunctionStatus"] as byte[];
                    if (status == null) return null;

                    bool healthSupported = (list & (ulong)BatteryFunction.HealthMode) != 0;
                    bool calSupported = (list & (ulong)BatteryFunction.Calibration) != 0;

                    int healthIdx = BitOperations.TrailingZeroCount((uint)BatteryFunction.HealthMode);
                    int calIdx = BitOperations.TrailingZeroCount((uint)BatteryFunction.Calibration);

                    bool healthOn = healthIdx < status.Length && status[healthIdx] == 1;
                    bool calOn = calIdx < status.Length && status[calIdx] == 1;

                    return new BatteryHealthStatus(healthSupported, calSupported, healthOn, calOn);
                }
                catch
                {
                    InvalidateBatteryCacheUnderLock();
                    return null;
                }
            }
        }

        public bool SetBatteryCalibration(bool enable)
        {
            lock (_lock)
            {
                try
                {
                    var obj = GetBatteryControlObjectUnderLock();
                    if (obj == null) return false;

                    using var inParams = obj.GetMethodParameters("SetBatteryHealthControl");
                    inParams["uBatteryNo"] = (byte)1;
                    inParams["uFunctionMask"] = (byte)2;
                    inParams["uFunctionStatus"] = (byte)(enable ? 1 : 0);
                    inParams["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };

                    using var outParams = obj.InvokeMethod("SetBatteryHealthControl", inParams, WmiTimeoutOptions);
                    var rawRet = outParams["uReturn"];
                    if (rawRet is byte[] retBytes)
                    {
                        if (retBytes.Length > 0 && retBytes[0] != 0) return false;
                        return true;
                    }
                    ushort result = Convert.ToUInt16(rawRet);
                    return result == 0;
                }
                catch
                {
                    InvalidateBatteryCacheUnderLock();
                    return false;
                }
            }
        }

        public bool? GetCoolBoost()
        {
            lock (_lock)
            {
                var res = SendActionCommandUnderLock("GetFunction", AcerProtocol.CoolBoostGetInput);
                if (res.success)
                {
                    return AcerProtocol.CoolBoostValue(res.output);
                }
                return null;
            }
        }

        public bool SetCoolBoost(bool on)
        {
            lock (_lock)
            {
                var res = SendActionCommandUnderLock("SetFunction", AcerProtocol.CoolBoostSetInput(on));
                return res.success;
            }
        }

        public FanTable? GetFanTable()
        {
            lock (_lock)
            {
                try
                {
                    var obj = GetWmiObjectUnderLock();
                    if (obj == null) return null;
                    using var outParams = obj.InvokeMethod("GetGamingFanTable", null, WmiTimeoutOptions);
                    if (outParams != null && outParams["gmOutput"] != null)
                    {
                        ulong raw = Convert.ToUInt64(outParams["gmOutput"]);
                        if (AcerProtocol.IsOk(raw))
                            return AcerProtocol.FanTableValue(raw);
                    }
                }
                catch (Exception ex)
                {
                    if (IsComOrWmiException(ex))
                    {
                        InvalidateCacheUnderLock();
                    }
                }
                return null;
            }
        }

        public bool SetFanTable(FanTable table)
        {
            lock (_lock)
            {
                var res = SendCommandUnderLock("SetGamingFanTable", AcerProtocol.FanTableInput(table));
                return res.success;
            }
        }

        public bool IsGpuModeSwitchSupported()
        {
            lock (_lock)
            {
                var res = SendCommandUnderLock("GetGamingMiscSetting", AcerProtocol.MiscGetInput(MiscSetting.GpuModeSupport));
                if (res.success)
                {
                    return AcerProtocol.MiscValue(res.output) == 3;
                }
                return false;
            }
        }

        public GpuMode? GetGpuMode()
        {
            lock (_lock)
            {
                var res = SendCommandUnderLock("GetGamingMiscSetting", AcerProtocol.MiscGetInput(MiscSetting.GpuMode));
                if (res.success)
                {
                    byte val = AcerProtocol.MiscValue(res.output);
                    if (Enum.IsDefined(typeof(GpuMode), val))
                        return (GpuMode)val;
                }
                return null;
            }
        }

        public bool SetGpuMode(GpuMode mode)
        {
            lock (_lock)
            {
                var res = SendCommandUnderLock("SetGamingMiscSetting", AcerProtocol.MiscSetInput(MiscSetting.GpuMode, (byte)mode));
                return res.success;
            }
        }

        public UsbChargingState? GetUsbCharging()
        {
            lock (_lock)
            {
                var res = SendActionCommandUnderLock("GetFunction", AcerProtocol.UsbChargingQuery);
                if (res.success)
                {
                    return AcerProtocol.UsbChargingValue(res.output);
                }
                return null;
            }
        }

        public bool SetUsbCharging(bool on, int floor)
        {
            lock (_lock)
            {
                if (!AcerProtocol.UsbChargingFloors.Contains(floor)) return false;
                var res = SendActionCommandUnderLock("SetFunction", AcerProtocol.UsbChargingInput(on, floor));
                return res.success;
            }
        }

        public (int Brightness, int TimeoutSeconds)? GetBacklightTimeout(byte hotkey = 2)
        {
            if (EcHid != null && EcHid.IsOpen)
            {
                var ecVal = EcHid.ReadBacklightTimeout();
                if (ecVal.HasValue) return ecVal;
            }

            lock (_lock)
            {
                var res = SendActionCommandUnderLock("GetFunction", AcerProtocol.BacklightTimeoutQuery(hotkey));
                if (res.success)
                {
                    return (AcerProtocol.BacklightBrightnessValue(res.output), AcerProtocol.BacklightTimeoutValue(res.output));
                }
                return null;
            }
        }

        public bool SetBacklightTimeout(byte hotkey, int brightness, int timeoutSeconds)
        {
            if (EcHid != null && EcHid.IsOpen)
            {
                if (EcHid.WriteBacklightTimeout(brightness, timeoutSeconds))
                    return true;
            }

            lock (_lock)
            {
                var res = SendActionCommandUnderLock("SetFunction", AcerProtocol.BacklightTimeoutInput(hotkey, brightness, timeoutSeconds));
                return res.success;
            }
        }

        public void InvalidateSensorCaches()
        {
            lock (_lock)
            {
                _cachedCpuTempReading = 0;
                _cachedGpuTempReading = 0;
                _cachedCpuRpmReading = 0;
                _cachedGpuRpmReading = 0;
                _cachedSystemRpmReading = 0;
                _lastCpuTempReadTime = DateTime.MinValue;
                _lastGpuTempReadTime = DateTime.MinValue;
                _lastCpuRpmReadTime = DateTime.MinValue;
                _lastGpuRpmReadTime = DateTime.MinValue;
                _lastSystemRpmReadTime = DateTime.MinValue;
                InvalidateCacheUnderLock();
                InvalidateActionCacheUnderLock();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try { LightingEffectsManager.StopSoftwareAnimation(); } catch { }
                _cachedCpuTempReading = 0;
                _cachedGpuTempReading = 0;
                _cachedCpuRpmReading = 0;
                _cachedGpuRpmReading = 0;
                _cachedSystemRpmReading = 0;
                _lastCpuTempReadTime = DateTime.MinValue;
                _lastGpuTempReadTime = DateTime.MinValue;
                _lastCpuRpmReadTime = DateTime.MinValue;
                _lastGpuRpmReadTime = DateTime.MinValue;
                _lastSystemRpmReadTime = DateTime.MinValue;
                try { _cachedObj?.Dispose(); } catch { }
                _cachedObj = null;
                try { _cachedBatteryObj?.Dispose(); } catch { }
                _cachedBatteryObj = null;
                try { _cachedActionObj?.Dispose(); } catch { }
                _cachedActionObj = null;
                try { EcHid?.Dispose(); } catch { }
                EcHid = null;
            }
        }
    }
}