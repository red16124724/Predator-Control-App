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
        private int _appliedKeyboardBrightness = -1;

        /// <summary>Last lid-logo color chosen by the user, or null while the logo simply mirrors the keyboard.</summary>
        private byte _speed = 5;       

        private byte _direction = 0;   

        private int _lastMode = 3;
        private int _appliedHardwareMode = -1;     



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



        public byte LastR { get { lock (_lock) return _lastR; } set { lock (_lock) _lastR = value; } }

        public byte LastG { get { lock (_lock) return _lastG; } set { lock (_lock) _lastG = value; } }

        public byte LastB { get { lock (_lock) return _lastB; } set { lock (_lock) _lastB = value; } }

        public byte Brightness { get { lock (_lock) return _brightness; } }

        public byte Speed { get { lock (_lock) return _speed; } }

        public byte Direction { get { lock (_lock) return _direction; } }

        public int LastMode { get { lock (_lock) return _lastMode; } set { lock (_lock) _lastMode = value; } }
        public int LastRgbMode { get { lock (_lock) return _lastMode; } set { lock (_lock) _lastMode = value; } }

        public byte CustomCpuFanSpeed { get { lock (_lock) return _customCpuFanSpeed; } }

        public byte CustomGpuFanSpeed { get { lock (_lock) return _customGpuFanSpeed; } }

        public byte CustomSystemFanSpeed { get { lock (_lock) return _customSystemFanSpeed; } }



        internal Action<string, ulong>? CommandInterceptor { get; set; }

        internal Action<string, byte[]>? LedArrayCommandInterceptor { get; set; }
        internal Func<string, byte[], bool>? LedArrayCommandResultOverride { get; set; }



        public EcHidDevice? EcHid { get; set; }



        public Channels.IOperatingModeChannel ModeChannel { get; private set; } = null!;

        public Channels.IFanControlChannel FanControl { get; private set; } = null!;

        public IThermalSafetyService ThermalSafety { get; private set; } = null!;



        private readonly bool _enableHardware;

        public WmiController() : this(enableHardware: true) { }

        internal WmiController(bool enableHardware)

        {

            try

            {

                _enableHardware = enableHardware;
                if (_enableHardware) EcHid = EcHidDevice.TryOpen();

            }

            catch { }



            ThermalSafety = new ThermalSafetyService();

            InitializeChannels();

        }



        public void InitializeChannels(DeviceCapabilities? caps = null)

        {

            var wmiMode = new Channels.WmiOperatingModeChannel(

                (cmd, payload) => SendCommand(cmd, payload),

                () => TryGetPowerMode(out OperatingMode m) ? m : null,

                (m) => { UpdateModeKeyLed(m); QueueWindowsOverlaySync(m); });

            var wmiFan = new Channels.WmiFanControlChannel(

                (m, apply) => { SetFanBehavior(m, apply); return true; },

                (f, p, force) => { SetFanSpeed(f, p, force); return true; },

                (ch, pct) => SetFanSpeed(ch, pct),

                (ch) => ch.Id switch

                {

                    FanId.Cpu => CpuFanRpm,

                    FanId.System => SystemFanRpm,

                    _ => GpuFanRpm

                });



            if (EcHid != null && EcHid.IsOpen)

            {

                ModeChannel = new Channels.OperatingModeChannelRouter(wmiMode, new Channels.EcHidOperatingModeChannel(EcHid));

                FanControl = new Channels.EcHidFanControlChannel(EcHid, wmiFan);

            }

            else

            {

                ModeChannel = wmiMode;

                FanControl = wmiFan;

            }

        }



        private static bool IsComOrWmiException(Exception ex)

        {

            if (ex is ManagementException mex && (mex.ErrorCode == ManagementStatus.NotFound ||

                                                  mex.ErrorCode == ManagementStatus.MethodNotImplemented ||

                                                  mex.ErrorCode == ManagementStatus.InvalidMethod ||

                                                  mex.ErrorCode == ManagementStatus.InvalidParameter))

            {

                return false;

            }

            return ex is COMException || ex is ManagementException ||

                   ex is InvalidOperationException || ex is ObjectDisposedException ||

                   ex.InnerException is COMException || ex.InnerException is ManagementException ||

                   ex.InnerException is InvalidOperationException || ex.InnerException is ObjectDisposedException;

        }



        private static object? TryGetPropertyValue(ManagementBaseObject? mbo, string propName)

        {

            if (mbo == null) return null;

            try

            {

                foreach (PropertyData prop in mbo.Properties)

                {

                    if (string.Equals(prop.Name, propName, StringComparison.OrdinalIgnoreCase))

                        return prop.Value;

                }

            }

            catch { }

            return null;

        }



        private static void SafeDisposeManagementObject(ref ManagementObject? obj)

        {

            if (obj != null)

            {

                try

                {

                    if (Marshal.IsComObject(obj))

                    {

                        Marshal.ReleaseComObject(obj);

                    }

                    obj.Dispose();

                }

                catch { }

                finally

                {

                    obj = null;

                }

            }

        }



        private ManagementObject? GetWmiObjectUnderLock()

        {

            if (_disposed || !_enableHardware) return null;

            if (_cachedObj != null) return _cachedObj;

            if ((DateTime.UtcNow - _lastSearchAttempt) < SearchRetryInterval) return null;



            _lastSearchAttempt = DateTime.UtcNow;

            ManagementObject? selected = null;

            try

            {

                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM AcerGamingFunction", WmiEnumOptions);

                using var results = searcher.Get();

                foreach (ManagementBaseObject mbo in results)

                {

                    if (mbo is ManagementObject mo)

                    {

                        if (selected == null)

                        {

                            selected = mo;

                        }

                        else

                        {

                            SafeDisposeManagementObject(ref mo!);

                        }

                    }

                    else

                    {

                        try

                        {

                            if (Marshal.IsComObject(mbo)) Marshal.ReleaseComObject(mbo);

                            mbo.Dispose();

                        }

                        catch { }

                    }

                }

                _cachedObj = selected;
                selected = null;

            }

            catch

            {

                _cachedObj = null;

            }

            finally

            {

                if (selected != null)

                {

                    SafeDisposeManagementObject(ref selected);

                }

            }

            return _cachedObj;

        }



        private void InvalidateCacheUnderLock()

        {

            SafeDisposeManagementObject(ref _cachedObj);

            _lastSearchAttempt = DateTime.MinValue;

        }



        private ManagementObject? _cachedActionObj;

        private DateTime _lastActionSearchAttempt = DateTime.MinValue;



        private ManagementObject? GetActionObjectUnderLock()

        {

            if (_disposed || !_enableHardware) return null;

            if (_cachedActionObj != null) return _cachedActionObj;

            if ((DateTime.UtcNow - _lastActionSearchAttempt) < SearchRetryInterval) return null;



            _lastActionSearchAttempt = DateTime.UtcNow;

            ManagementObject? selected = null;

            try

            {

                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM APGeAction", WmiEnumOptions);

                using var results = searcher.Get();

                foreach (ManagementBaseObject mbo in results)

                {

                    if (mbo is ManagementObject mo)

                    {

                        if (selected == null)

                        {

                            selected = mo;

                        }

                        else

                        {

                            SafeDisposeManagementObject(ref mo!);

                        }

                    }

                    else

                    {

                        try

                        {

                            if (Marshal.IsComObject(mbo)) Marshal.ReleaseComObject(mbo);

                            mbo.Dispose();

                        }

                        catch { }

                    }

                }

                _cachedActionObj = selected;

                selected = null;

            }

            catch

            {

                _cachedActionObj = null;

            }

            finally

            {

                if (selected != null)

                {

                    SafeDisposeManagementObject(ref selected);

                }

            }

            return _cachedActionObj;

        }



        private void InvalidateActionCacheUnderLock()

        {

            SafeDisposeManagementObject(ref _cachedActionObj);

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

                (bool success, ulong output) res = (false, 0);
                WithGamingMutex(() =>
                {
                    using var outParams = obj.InvokeMethod(method, inParams, WmiTimeoutOptions);
                    if (outParams != null)
                    {
                        foreach (PropertyData prop in outParams.Properties)
                        {
                            if (prop.Name != "ReturnValue" && !prop.IsArray && prop.Value != null)
                            {
                                ulong result = Convert.ToUInt64(prop.Value);
                                res = ((result & 0xFF) == 0, result);
                                return res;
                            }
                        }
                    }
                    return res;
                }, out _);
                return res;

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

            if (Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))

            {

                try

                {

                    return SendCommandUnderLock(method, input);

                }

                finally

                {

                    Monitor.Exit(_lock);

                }

            }

            return (false, 0);

        }



        public const string ACER_GAMING_MUTEX_NAME = @"Global\AcerGamingFunctionMutex";
        private static Mutex? _cachedGamingMutex;
        private static readonly object _gamingMutexLock = new();

        private static Mutex? GetOrCreateGamingMutex()
        {
            if (_cachedGamingMutex != null) return _cachedGamingMutex;
            lock (_gamingMutexLock)
            {
                if (_cachedGamingMutex != null) return _cachedGamingMutex;
                try
                {
                    if (!Mutex.TryOpenExisting(ACER_GAMING_MUTEX_NAME, out _cachedGamingMutex))
                    {
                        try { _cachedGamingMutex = new Mutex(false, ACER_GAMING_MUTEX_NAME); } catch { }
                    }
                }
                catch { }
                return _cachedGamingMutex;
            }
        }

        private static bool WithGamingMutex<T>(Func<T> action, out T result, int timeoutMs = 2000)
        {
            Mutex? mutex = GetOrCreateGamingMutex();
            bool acquired = false;
            try
            {
                if (mutex != null)
                {
                    try { acquired = mutex.WaitOne(timeoutMs); }
                    catch (AbandonedMutexException) { acquired = true; }
                }

                if (mutex != null && !acquired)
                {
                    result = default!;
                    return false;
                }

                result = action();
                return true;
            }
            finally
            {
                if (acquired && mutex != null)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }



        public (bool success, ulong output) GetGamingSysInfo(uint input)
        {
            lock (_lock)
            {
                return GetGamingSysInfoUnderLock(input);
            }
        }

        private (bool success, ulong output) GetGamingSysInfoUnderLock(uint input)
        {
            try
            {
                var obj = GetWmiObjectUnderLock();
                if (obj == null) return (false, 0);

                using var inParams = obj.GetMethodParameters("GetGamingSysInfo");
                if (inParams == null) return (false, 0);
                inParams["gmInput"] = input;

                ulong raw = 0;
                bool ok = false;
                WithGamingMutex(() =>
                {
                    using var outParams = obj.InvokeMethod("GetGamingSysInfo", inParams, WmiTimeoutOptions);
                    if (outParams != null)
                    {
                        var gmOut = TryGetPropertyValue(outParams, "gmOutput");
                        if (gmOut != null)
                        {
                            raw = Convert.ToUInt64(gmOut);
                            ok = true;
                        }
                    }
                    return ok;
                }, out _);

                return (ok, raw);
            }
            catch
            {
                return (false, 0);
            }
        }

        public (bool success, ulong output) SetGamingSysInfo(ulong payload)
        {
            lock (_lock)
            {
                return SetGamingSysInfoUnderLock(payload);
            }
        }

        private (bool success, ulong output) SetGamingSysInfoUnderLock(ulong payload)
        {
            return SendCommandUnderLock("SetGamingSysInfo", payload);
        }

        public bool SetWinKeyLock(bool enable)
        {
            lock (_lock)
            {
                var (getOk, currentVal) = GetGamingSysInfoUnderLock(AcerProtocol.GamingSysInfoConfigQuery);
                ulong payload = AcerProtocol.ApplySystemConfigBitPreservingUpperBytes(getOk ? currentVal : 0, AcerProtocol.GamingSysInfoWinKeyBit, enable);
                var res = SetGamingSysInfoUnderLock(payload);
                return res.success;
            }
        }

        private (bool success, ulong output) SendCommandUnderLock(string method, ulong input)

        {

            try

            {

                try { CommandInterceptor?.Invoke(method, input); } catch { }
                if (!_enableHardware || CommandInterceptor != null) return (true, 0);

                var obj = GetWmiObjectUnderLock();

                if (obj == null) return (false, 0);



                ManagementBaseObject? inParams = null;

                try

                {

                    inParams = obj.GetMethodParameters(method);

                }

                catch (ManagementException mex) when (mex.ErrorCode == ManagementStatus.NotFound ||

                                                     mex.ErrorCode == ManagementStatus.MethodNotImplemented ||

                                                     mex.ErrorCode == ManagementStatus.InvalidMethod)

                {

                    return (false, 0);

                }

                if (inParams == null) return (false, 0);

                using var inParamsDisposer = inParams;

                inParams["gmInput"] = input;



                (bool success, ulong output) res = (false, 0);

                WithGamingMutex(() =>

                {

                    using var outParams = obj.InvokeMethod(method, inParams, WmiTimeoutOptions);

                    var gmOut = TryGetPropertyValue(outParams, "gmOutput");

                    if (gmOut != null)

                    {

                        ulong result = Convert.ToUInt64(gmOut);

                        res = ((result & 0xFF) == 0, result);

                    }

                    return res;

                }, out _);

                return res;

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
            if (Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            {
                try
                {
                    return SendLedCommandUnderLock(payload);
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }
            return false;
        }



        private bool SendLedCommandUnderLock(byte[] payload)

        {

            try

            {

                if (payload == null) return false;

                byte[] safePayload = payload.Length == 16 ? payload : new byte[16];

                if (payload.Length != 16)

                {

                    Buffer.BlockCopy(payload, 0, safePayload, 0, Math.Min(payload.Length, 16));

                }

                try { LedArrayCommandInterceptor?.Invoke("SetGamingKBBacklight", safePayload); } catch { }
                if (!_enableHardware || LedArrayCommandInterceptor != null) return LedArrayCommandResultOverride?.Invoke("SetGamingKBBacklight", safePayload) ?? true;



                var obj = GetWmiObjectUnderLock();

                if (obj == null) return false;



                using var inParams = obj.GetMethodParameters("SetGamingKBBacklight");

                if (inParams == null) return false;

                inParams["gmInput"] = safePayload;



                bool success = false;

                WithGamingMutex(() =>

                {

                    using var outParams = obj.InvokeMethod("SetGamingKBBacklight", inParams, WmiTimeoutOptions);

                    var gmOut = TryGetPropertyValue(outParams, "gmOutput");

                    if (gmOut != null)

                    {

                        ulong result = Convert.ToUInt64(gmOut);

                        success = (result & 0xFF) == 0;

                    }

                    return success;

                }, out _);

                return success;

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

                if (payload == null) return false;

                byte[] safePayload = payload.Length == 16 ? payload : new byte[16];

                if (payload.Length != 16)

                {

                    Buffer.BlockCopy(payload, 0, safePayload, 0, Math.Min(payload.Length, 16));

                }

                try { LedArrayCommandInterceptor?.Invoke(method, safePayload); } catch { }
                if (!_enableHardware || LedArrayCommandInterceptor != null) return LedArrayCommandResultOverride?.Invoke(method, safePayload) ?? true;



                var obj = GetWmiObjectUnderLock();

                if (obj == null) return false;



                ManagementBaseObject? inParams = null;

                try

                {

                    inParams = obj.GetMethodParameters(method);

                }

                catch (ManagementException mex) when (mex.ErrorCode == ManagementStatus.NotFound ||

                                                     mex.ErrorCode == ManagementStatus.MethodNotImplemented ||

                                                     mex.ErrorCode == ManagementStatus.InvalidMethod)

                {

                    return false;

                }

                if (inParams == null) return false;

                using var inParamsDisposer = inParams;

                inParams["gmInput"] = safePayload;



                bool success = false;

                WithGamingMutex(() =>

                {

                    using var outParams = obj.InvokeMethod(method, inParams, WmiTimeoutOptions);

                    var gmOut = TryGetPropertyValue(outParams, "gmOutput");

                    if (gmOut != null)

                    {

                        ulong result = Convert.ToUInt64(gmOut);

                        success = (result & 0xFF) == 0;

                    }

                    return success;

                }, out _);

                return success;

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

            if (raw == 0 || raw == 0xFFFF || raw == 0xFFFFFFFF || raw == 0xFFFFFFFFFFFFFFFFUL) return 0;



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



                    ManagementBaseObject? inParams = null;

                    try

                    {

                        inParams = obj.GetMethodParameters("GetGamingSysInfo");

                    }

                    catch (ManagementException mex) when (mex.ErrorCode == ManagementStatus.NotFound ||

                                                         mex.ErrorCode == ManagementStatus.MethodNotImplemented ||

                                                         mex.ErrorCode == ManagementStatus.InvalidMethod)

                    {

                        return 0;

                    }

                    if (inParams == null) return 0;

                    using var inParamsDisposer = inParams;

                    inParams["gmInput"] = (uint)(0x0001 | (sensorId << 8));

                    ulong raw = 0;

                    bool queryOk = false;

                    WithGamingMutex(() =>

                    {

                        using var outParams = obj.InvokeMethod("GetGamingSysInfo", inParams, WmiTimeoutOptions);

                        var gmOut = TryGetPropertyValue(outParams, "gmOutput");

                        if (gmOut != null)

                        {

                            raw = Convert.ToUInt64(gmOut);

                            queryOk = true;

                        }

                        return queryOk;

                    }, out _);

                    if (!queryOk) return 0;



                    if (sensorId == (ulong)SensorId.CpuFanSpeed || sensorId == (ulong)SensorId.GpuFanSpeed ||

                        sensorId == (ulong)SensorId.SystemFanSpeed || sensorId == (ulong)SensorId.System2FanSpeed ||

                        sensorId == (ulong)SensorId.Gpu2FanSpeed)

                    {

                        int rpm = DecodeFanSpeed(raw);

                        if (rpm > 0) return rpm;

                    }



                    if (sensorId == (ulong)SensorId.CpuTemperature || sensorId == (ulong)SensorId.SystemTemperature ||

                        sensorId == (ulong)SensorId.System2Temperature || sensorId == (ulong)SensorId.GpuTemperature ||

                        sensorId == (ulong)SensorId.Gpu2Temperature)

                    {

                        if ((raw & 0xFF) != 0) return 0;

                        int temp = (int)((raw >> 8) & 0xFFFF);

                        if (temp <= 0 || temp > 125 || temp == 0xFFFF) return 0;

                        return temp;

                    }



                    if ((raw & 0xFF) != 0) return 0;

                    return (int)((raw >> 8) & 0xFFFF);

                }

                catch (Exception ex)

                {

                    if (IsComOrWmiException(ex) || ex is NullReferenceException)

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



                    ManagementBaseObject? inParams = null;

                    try

                    {

                        inParams = obj.GetMethodParameters("GetGamingFanSpeed");

                    }

                    catch (ManagementException mex) when (mex.ErrorCode == ManagementStatus.NotFound ||

                                                         mex.ErrorCode == ManagementStatus.MethodNotImplemented ||

                                                         mex.ErrorCode == ManagementStatus.InvalidMethod)

                    {

                        return 0;

                    }

                    if (inParams == null) return 0;

                    using var inParamsDisposer = inParams;

                    inParams["gmInput"] = fanType;

                    ulong raw = 0;

                    bool queryOk = false;

                    WithGamingMutex(() =>

                    {

                        using var outParams = obj.InvokeMethod("GetGamingFanSpeed", inParams, WmiTimeoutOptions);

                        var gmOut = TryGetPropertyValue(outParams, "gmOutput");

                        if (gmOut != null)

                        {

                            raw = Convert.ToUInt64(gmOut);

                            queryOk = true;

                        }

                        return queryOk;

                    }, out _);

                    if (!queryOk) return 0;

                    return DecodeFanSpeed(raw);

                }

                catch (Exception ex)

                {

                    if (IsComOrWmiException(ex) || ex is NullReferenceException)

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

                    else

                    {

                        _lastGpuTempReadTime = DateTime.MinValue;

                        _lastGpuRpmReadTime = DateTime.MinValue;

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

                var ecHid = EcHid;

                bool ecSuccess = false;

                if (ecHid != null && ecHid.IsOpen)

                {

                    try

                    {

                        if (AcerProtocol.TryWmiModeToEcMode(mode, out byte ecMode))

                        {

                            ecSuccess = ecHid.WriteMode(ecMode);

                        }

                    }

                    catch { }

                }



                if (forceRetry && _cachedObj == null)

                {

                    _lastSearchAttempt = DateTime.MinValue;

                }

                isModeSwitch = !_lastAppliedPowerMode.HasValue || _lastAppliedPowerMode.Value != mode;

                if (isModeSwitch && _lastAppliedPowerMode == 0x05 && mode != 0x05)

                {

                    try

                    {

                        // Exiting Turbo: reset fan boost before setting target power mode so target mode is final command

                        SendCommandUnderLock("SetGamingFanBehavior", BuildFanBehaviorPayload(0x01));

                    }

                    catch { }

                }

                ulong currentMisc = 0;
                var getRes = SendCommandUnderLock("GetGamingMiscSetting", (ulong)0x0B);
                if (getRes.success && getRes.output != 0)
                {
                    currentMisc = getRes.output;
                }
                ulong payload;
                if (currentMisc != 0)
                {
                    // Non-destructive read-modify-write: preserve bit 0 (Fn/Media key mode) and vendor flags
                    payload = (currentMisc & ~0xFF00UL) | ((ulong)mode << 8);
                    if ((payload & 0xFF) == 0) payload |= 0x0BUL;
                }
                else
                {
                    payload = (ulong)0x0B | ((ulong)mode << 8);
                }
                var res = SendCommandUnderLock("SetGamingMiscSetting", payload);

                if (!res.success)

                {

                    res = SendCommandUnderLock("SetGamingProfile", (ulong)mode);

                }

                _lastAppliedPowerMode = mode;

                success = res.success || ecSuccess;

                if (res.success && _cachedObj != null)

                {

                    UpdateModeKeyLed(mode);

                }

            }



            if (isModeSwitch && success)

            {

                // Deferred on purpose: PowerSetActiveOverlayScheme retunes USB,

                // PCIe and radio power policy. Running it inline with the EC

                // write is what produced audio stutter and Bluetooth dropouts

                // during rapid mode switching.

                QueueWindowsOverlaySync(mode);

            }

            return success;

        }



        public void SetPowerMode(byte mode)

        {

            TrySetPowerMode(mode, false);

        }



        public bool TryGetPowerMode(out byte mode)

        {

            lock (_lock)

            {

                if (EcHid != null && EcHid.IsOpen)

                {

                    try

                    {

                        byte? ecMode = EcHid.ReadMode();

                        if (ecMode.HasValue && AcerProtocol.TryEcModeToWmiMode(ecMode.Value, out byte mappedMode))

                        {

                            _lastAppliedPowerMode = mappedMode;

                            mode = mappedMode;

                            return true;

                        }

                    }

                    catch { }

                }



                if (_lastAppliedPowerMode.HasValue)

                {

                    mode = _lastAppliedPowerMode.Value;

                    return true;

                }



                mode = 0x01;

                return false;

            }

        }



        public bool TryGetPowerMode(out OperatingMode mode)

        {

            if (TryGetPowerMode(out byte rawMode) && Enum.IsDefined(typeof(OperatingMode), rawMode))

            {

                mode = (OperatingMode)rawMode;

                return true;

            }

            mode = OperatingMode.Balanced;

            return false;

        }



        public bool TrySetOperatingMode(OperatingMode mode, bool forceRetry = false) =>

            TrySetPowerMode((byte)mode, forceRetry);



        public byte GetPowerMode()

        {

            TryGetPowerMode(out byte mode);

            return mode;

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



        private byte? _lastAppliedCpuSpeed;

        private byte? _lastAppliedGpuSpeed;

        private byte? _lastAppliedSystemSpeed;



        public void InvalidateAppliedFanSpeeds()

        {

            lock (_lock)

            {

                _lastAppliedCpuSpeed = null;

                _lastAppliedGpuSpeed = null;

                _lastAppliedSystemSpeed = null;

            }

        }



        public void SetFanBehavior(byte mode, bool applyCustomSpeeds = true)

        {

            lock (_lock)

            {

                InvalidateAppliedFanSpeeds();

                SendCommandUnderLock("SetGamingFanBehavior", BuildFanBehaviorPayload(mode));



                if (mode == 0x03 && applyCustomSpeeds)

                {

                    SetFanSpeed(1UL, _customCpuFanSpeed, force: true);

                    SetFanSpeed(4UL, _customGpuFanSpeed, force: true);

                    SetFanSpeed(2UL, _customSystemFanSpeed, force: true);

                }

            }

        }



        public bool SetFanSpeed(FanChannel fan, int percent)

        {

            if (fan == null) return false;

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



        public void SetFanSpeed(ulong fanType, byte percentage, bool force = false)

        {

            byte clamped = Math.Clamp(percentage, (byte)10, (byte)100);



            if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))

                return;



            try

            {

                if (_disposed) return;



                if (fanType == 1)

                {

                    _customCpuFanSpeed = clamped;

                    if (!force && _lastAppliedCpuSpeed == clamped) return;

                    _lastAppliedCpuSpeed = clamped;

                    var (direct, extended) = BuildCpuFanSpeedPayloads(clamped);

                    SendCommandUnderLock("SetGamingFanSpeed", direct);

                    SendCommandUnderLock("SetGamingFanSpeed", extended);

                }

                else if (fanType == 2)

                {

                    _customSystemFanSpeed = clamped;

                    if (!force && _lastAppliedSystemSpeed == clamped) return;

                    _lastAppliedSystemSpeed = clamped;

                    ulong payload = AcerProtocol.FanSpeedInput(FanChannel.System, clamped);

                    SendCommandUnderLock("SetGamingFanSpeed", payload);

                    SendCommandUnderLock("SetGamingFanSpeed", 0x05UL | (3UL << 8) | ((ulong)clamped << 16));

                }

                else

                {

                    _customGpuFanSpeed = clamped;

                    if (!force && _lastAppliedGpuSpeed == clamped) return;

                    _lastAppliedGpuSpeed = clamped;

                    var (directPri, directAlt, extPri, extAlt) = BuildGpuFanSpeedPayloads(clamped);

                    SendCommandUnderLock("SetGamingFanSpeed", directPri);

                    SendCommandUnderLock("SetGamingFanSpeed", directAlt);

                    SendCommandUnderLock("SetGamingFanSpeed", extPri);

                    SendCommandUnderLock("SetGamingFanSpeed", extAlt);

                }

            }

            finally

            {

                Monitor.Exit(_lock);

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

            lock (_lock)

            {

                _lastR = r; _lastG = g; _lastB = b;

                _brightness = Math.Clamp(brightness, (byte)0, (byte)100);

                _speed = Math.Clamp(speed, (byte)1, (byte)9);

                _direction = (byte)(direction != 0 ? 1 : 0);

                _lastMode = Math.Clamp(mode, 0, 10);

                ApplyLightingMode(_lastMode);

            }

        }



        public void SetBrightness(byte brightness, bool applyMode = true)

        {

            lock (_lock)

            {

                _brightness = Math.Clamp(brightness, (byte)0, (byte)100);

                if (_brightness == 0)

                    TurnOffBacklight();

                else if (applyMode)
                {
                    int modeToApply = _lastMode > 0 ? _lastMode : 3;
                    ApplyLightingMode(modeToApply);
                }

            }

        }



        public void TurnOffBacklight()

        {

            try { LightingEffectsManager.StopSoftwareAnimation(); } catch { }

            lock (_lock)

            {

                _brightness = 0;
                if (_enableHardware) AcerServiceClient.SupersedeKeyboardLighting();



                try

                {

                    // Exterior LED off via WMI

                    ulong[] offZones = { 0x05ul, 0x06ul, 0x07ul, 0x0Ful, 0xF0ul, 0x01ul };

                    foreach (var z in offZones)

                    {

                        SendCommandUnderLock("SetGamingLEDBehavior", 0x06ul | (z << 8));

                        SendCommandUnderLock("SetGamingLEDColor", z);

                    }



                    // Dual-dispatch 4-zone + exterior off (zone bitmasks: 1, 2, 4, 8, 0x10, 0x20, 0x40, 0x80, 0xF0)

                    ulong[] zoneMasks = { 1UL, 2UL, 4UL, 8UL, 0x10UL, 0x20UL, 0x40UL, 0x80UL, 0xF0UL };

                    foreach (ulong mask in zoneMasks)

                    {

                        SendCommandUnderLock("SetGamingRgbKb", mask);

                    }



                    // Main 16-byte packet off

                    byte[] payload = new byte[16];

                    payload[0] = 0;

                    payload[1] = 0;

                    payload[2] = 0;

                    bool keyboardOff = SendLedArrayCommandUnderLock("SetGamingKBBacklight", payload);

                    keyboardOff |= SendLedArrayCommandUnderLock("SetGamingLED", payload);

                    foreach (byte z in new byte[] { 5, 6, 7 })

                    {

                        payload[9] = z;

                        SendLedArrayCommandUnderLock("SetGamingLED", payload);

                    }



                    if (_enableHardware) _ = AcerServiceClient.SetKeyboardLightingAsync(1, "STATIC", null, 0, 1, false);
                    if (_enableHardware) _ = AcerServiceClient.SetExteriorLightingAsync(0, 0, 0, 0, 0, 0);
                    if (keyboardOff) _appliedKeyboardBrightness = 0;

                }

                catch { }

            }

        }



        public void SetSpeed(byte speed)

        {

            lock (_lock)

            {

                _speed = Math.Clamp(speed, (byte)1, (byte)9);

                ApplyLightingMode(_lastMode);

            }

        }



        public void SetDirection(byte direction)

        {

            lock (_lock)

            {

                _direction = (byte)(direction != 0 ? 1 : 0);

                ApplyLightingMode(_lastMode);

            }

        }



        public void SetStaticColor(byte r, byte g, byte b, byte brightness)

        {

            lock (_lock)

            {

                _lastR = r; _lastG = g; _lastB = b;

                _brightness = Math.Clamp(brightness, (byte)0, (byte)100);

                _lastMode = 0;
                // Preserve _appliedHardwareMode so ApplyLightingMode(0) knows if Meteor (6) was active to halt EC loop
                if (brightness == 0)

                    TurnOffBacklight();

                else

                    ApplyLightingMode(0);

            }

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

            byte cBright = (byte)Math.Clamp(brightness, 0, 100);
            if (color.A == 0) cBright = 0;

            SetStaticColor(r, g, b, cBright);

        }



        private bool EnsureKeyboardBrightnessUnderLock(bool force = false)
        {
            if (!force && _appliedKeyboardBrightness == _brightness) return true;
            if (_enableHardware) AcerServiceClient.SupersedeKeyboardLighting();
            byte[] packet = new byte[16];
            packet[2] = _brightness;
            packet[5] = _lastR;
            packet[6] = _lastG;
            packet[7] = _lastB;
            packet[9] = 1;
            bool applied = SendLedCommandUnderLock(packet);
            applied |= SendLedArrayCommandUnderLock("SetGamingLED", packet);
            if (applied) _appliedKeyboardBrightness = _brightness;
            return applied;
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

                bool hardwareBrightness = EnsureKeyboardBrightnessUnderLock();
                if (!hardwareBrightness && _appliedKeyboardBrightness >= 0 && _appliedKeyboardBrightness != 100) return false;
                int colorBrightness = _brightness == 0 ? 0 : hardwareBrightness ? 100 : _brightness;
                byte scaledR = (byte)((r * colorBrightness) / 100);

                byte scaledG = (byte)((g * colorBrightness) / 100);

                byte scaledB = (byte)((b * colorBrightness) / 100);

                if (_brightness > 0)

                {

                    if (r > 0 && scaledR == 0) scaledR = 1;

                    if (g > 0 && scaledG == 0) scaledG = 1;

                    if (b > 0 && scaledB == 0) scaledB = 1;

                }

                ulong zoneMask = (zone >= 1 && zone <= 4) ? (1UL << (zone - 1)) : (zone == 0 ? 0x0FUL : (ulong)zone);

                ulong payload = zoneMask | ((ulong)scaledR << 8) | ((ulong)scaledG << 16) | ((ulong)scaledB << 24);

                var res = SendCommandUnderLock("SetGamingRgbKb", payload);

                return res.success;

            }

        }



        /// <summary>

        /// Applies zone colors in one lock pass.

        /// Fixes animated EC timer overwrite by resetting mode to static (mode 0)

        /// via SetGamingKBBacklight / SetGamingLED and SetGamingLEDBehavior(0x01ul).

        /// Supports 1-sector (mask 0x0F & broadcast to 1, 2, 4, 8),

        /// 3-sector (masks 1, 2, 4), and 4-sector (masks 1, 2, 4, 8).

        /// </summary>

        public bool SetZoneColors(IReadOnlyList<Color> colors, int brightness = -1, bool isNativeThreeZone = true)

        {

            if (colors == null || colors.Count == 0) return false;



            if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))

                return false;



            try

            {

                if (_disposed) return false;



                if (_enableHardware) AcerServiceClient.SupersedeKeyboardLighting();

                // Ensure firmware animations are stopped so animated EC timers do not overwrite zone registers

                _lastMode = 0;

                _appliedHardwareMode = 0;

                int effBrightness = brightness >= 0 ? Math.Clamp(brightness, 0, 100) : (int)_brightness;
                _brightness = (byte)effBrightness;



                try { LightingEffectsManager.StopSoftwareAnimation(); } catch { }



                
                // Mode 0 Static packet to halt any running EC hardware animation (Meteor, Wave)
                // so the EC state machine yields control of the LEDs to SetGamingRgbKb.
                byte[] ecHaltPkt = new byte[16];
                ecHaltPkt[0] = 0; // Mode 0 Static
                ecHaltPkt[2] = (byte)effBrightness;
                ecHaltPkt[3] = 0; // Disable dynamic effect flags
                ecHaltPkt[8] = 0; // Explicitly clear mode 6/7 sub-registers
                ecHaltPkt[9] = 1; // Keyboard
                bool hardwareBrightness = SendLedCommandUnderLock(ecHaltPkt);
                hardwareBrightness |= SendLedArrayCommandUnderLock("SetGamingLED", ecHaltPkt);
                if (hardwareBrightness) _appliedKeyboardBrightness = effBrightness;
                int colorBrightness = effBrightness == 0 ? 0 : hardwareBrightness ? 100 : effBrightness;
                if (_enableHardware) Thread.Sleep(25);

                // Dispatch to AcerServiceClient with live zone colors
                // The lighting controller uses the service only if this native write fails.

                bool any = false;



                if (colors.Count == 1)

                {

                    Color color = colors[0];

                    byte r = color.R;

                    byte g = color.G;

                    byte b = color.B;



                    byte scaledR = (byte)((r * colorBrightness) / 100);

                    byte scaledG = (byte)((g * colorBrightness) / 100);

                    byte scaledB = (byte)((b * colorBrightness) / 100);

                    if (effBrightness > 0)

                    {

                        if (r > 0 && scaledR == 0) scaledR = 1;

                        if (g > 0 && scaledG == 0) scaledG = 1;

                        if (b > 0 && scaledB == 0) scaledB = 1;

                    }



                    // 1-zone broadcast mask 0x0F

                    ulong allZonesPayload = 0x0FUL | ((ulong)scaledR << 8) | ((ulong)scaledG << 16) | ((ulong)scaledB << 24);

                    var resAll = SendCommandUnderLock("SetGamingRgbKb", allZonesPayload);

                    any |= resAll.success;



                    // Also broadcast to individual zone bitmasks 1, 2, 4, 8 for firmware variants

                    ulong[] allMasks = { 1UL, 2UL, 4UL, 8UL };

                    foreach (ulong mask in allMasks)

                    {

                        ulong payload = mask | ((ulong)scaledR << 8) | ((ulong)scaledG << 16) | ((ulong)scaledB << 24);

                        var res = SendCommandUnderLock("SetGamingRgbKb", payload);

                        any |= res.success;

                    }

                }

                else if (colors.Count == 3)
                {
                    // Native 3-zone (Helios Neo 14): Left=1, Center=2, Right=4
                    // 4-zone hardware mapped to 3 zones (PHN16/18): Left=1, Center=2|4, Right=8
                    ulong[] zoneMasks = isNativeThreeZone
                        ? new ulong[] { 1UL, 2UL, 4UL }
                        : new ulong[] { 1UL, 6UL, 8UL };

                    for (int i = 0; i < 3; i++)

                    {

                        Color color = colors[i];

                        byte r = color.R;

                        byte g = color.G;

                        byte b = color.B;



                        byte scaledR = (byte)((r * colorBrightness) / 100);

                        byte scaledG = (byte)((g * colorBrightness) / 100);

                        byte scaledB = (byte)((b * colorBrightness) / 100);

                        if (effBrightness > 0)

                        {

                            if (r > 0 && scaledR == 0) scaledR = 1;

                            if (g > 0 && scaledG == 0) scaledG = 1;

                            if (b > 0 && scaledB == 0) scaledB = 1;

                        }



                        ulong zoneMask = zoneMasks[i];

                        ulong payload = zoneMask | ((ulong)scaledR << 8) | ((ulong)scaledG << 16) | ((ulong)scaledB << 24);

                        var res = SendCommandUnderLock("SetGamingRgbKb", payload);

                        any |= res.success;

                    }

                }

                else if (colors.Count == 2)

                {

                    // 2-zone mapping: color 0 -> zones 1, 2 (masks 1, 2); color 1 -> zones 3, 4 (masks 4, 8)

                    (ulong Mask, int ColorIdx)[] twoZoneMap = { (1UL, 0), (2UL, 0), (4UL, 1), (8UL, 1) };

                    foreach (var (mask, cIdx) in twoZoneMap)

                    {

                        Color color = colors[cIdx];

                        byte r = color.R;

                        byte g = color.G;

                        byte b = color.B;



                        byte scaledR = (byte)((r * colorBrightness) / 100);

                        byte scaledG = (byte)((g * colorBrightness) / 100);

                        byte scaledB = (byte)((b * colorBrightness) / 100);

                        if (effBrightness > 0)

                        {

                            if (r > 0 && scaledR == 0) scaledR = 1;

                            if (g > 0 && scaledG == 0) scaledG = 1;

                            if (b > 0 && scaledB == 0) scaledB = 1;

                        }



                        ulong payload = mask | ((ulong)scaledR << 8) | ((ulong)scaledG << 16) | ((ulong)scaledB << 24);

                        var res = SendCommandUnderLock("SetGamingRgbKb", payload);

                        any |= res.success;

                    }

                }

                else

                {

                    // 4-zone (or >4 array length capped safely at 4 zones)

                    int count = Math.Min(4, colors.Count);

                    ulong[] zoneMasks = { 1UL, 2UL, 4UL, 8UL };



                    for (int i = 0; i < count; i++)

                    {

                        Color color = colors[i];

                        byte r = color.R;

                        byte g = color.G;

                        byte b = color.B;



                        byte scaledR = (byte)((r * colorBrightness) / 100);

                        byte scaledG = (byte)((g * colorBrightness) / 100);

                        byte scaledB = (byte)((b * colorBrightness) / 100);

                        if (effBrightness > 0)

                        {

                            if (r > 0 && scaledR == 0) scaledR = 1;

                            if (g > 0 && scaledG == 0) scaledG = 1;

                            if (b > 0 && scaledB == 0) scaledB = 1;

                        }



                        ulong zoneMask = zoneMasks[i];

                        ulong payload = zoneMask | ((ulong)scaledR << 8) | ((ulong)scaledG << 16) | ((ulong)scaledB << 24);

                        var res = SendCommandUnderLock("SetGamingRgbKb", payload);

                        any |= res.success;

                    }

                }

                // 2. Dual-dispatch SetGamingKBBacklight for modern Acer laptops (PHN14, PHN16, PHN18, etc.) on newer BIOS
                effBrightness = brightness >= 0 ? Math.Clamp(brightness, 0, 100) : _brightness;
                byte[] kbPayload = new byte[16];
                kbPayload[0] = 0; // Mode 0 Static
                kbPayload[1] = 0;
                kbPayload[2] = (byte)effBrightness;
                kbPayload[8] = 0;

                if (colors.Count == 1)
                {
                    Color color = colors[0];
                    byte scaledR = color.R;
                    byte scaledG = color.G;
                    byte scaledB = color.B;
                    if (effBrightness > 0)
                    {
                        if (color.R > 0 && scaledR == 0) scaledR = 1;
                        if (color.G > 0 && scaledG == 0) scaledG = 1;
                        if (color.B > 0 && scaledB == 0) scaledB = 1;
                    }
                    kbPayload[5] = scaledR;
                    kbPayload[6] = scaledG;
                    kbPayload[7] = scaledB;
                    // Zone index 0 addresses every LED on the chain, so it is avoided in favour of the
                    // keyboard-only zone addresses (1 and the 0x0F broadcast).
                    foreach (byte z in new byte[] { 1, 0x0F })
                    {
                        byte[] pkt = (byte[])kbPayload.Clone();
                        pkt[9] = z;
                        any |= SendLedArrayCommandUnderLock("SetGamingKBBacklight", pkt);
                        any |= SendLedArrayCommandUnderLock("SetGamingLED", pkt);
                    }
                }
                else if (colors.Count == 3)
                {
                    // Native 3-zone: Zones 1, 2, 3 mapped 1:1
                    // 4-zone hardware mapped to 3 zones: Zone 1=Left, Zones 2&3=Center, Zone 4=Right
                    (byte zoneIdx, int colorIdx)[] mappings = isNativeThreeZone
                        ? new (byte, int)[] { (1, 0), (2, 1), (3, 2) }
                        : new (byte, int)[] { (1, 0), (2, 1), (3, 1), (4, 2) };

                    foreach (var (zoneIdx, colorIdx) in mappings)
                    {
                        Color color = colors[colorIdx];
                        byte scaledR = color.R;
                        byte scaledG = color.G;
                        byte scaledB = color.B;
                        if (effBrightness > 0)
                        {
                            if (color.R > 0 && scaledR == 0) scaledR = 1;
                            if (color.G > 0 && scaledG == 0) scaledG = 1;
                            if (color.B > 0 && scaledB == 0) scaledB = 1;
                        }
                        byte[] pkt = (byte[])kbPayload.Clone();
                        pkt[5] = scaledR;
                        pkt[6] = scaledG;
                        pkt[7] = scaledB;
                        pkt[9] = zoneIdx;
                        any |= SendLedArrayCommandUnderLock("SetGamingKBBacklight", pkt);
                        any |= SendLedArrayCommandUnderLock("SetGamingLED", pkt);
                        ulong colorPayload = ((uint)scaledB << 24) | ((uint)scaledG << 16) | ((uint)scaledR << 8) | (uint)zoneIdx;
                        SendCommandUnderLock("SetGamingLEDColor", colorPayload);
                    }
                }
                else
                {
                    int count = colors.Count == 2 ? 4 : Math.Min(4, colors.Count);
                    for (int i = 0; i < count; i++)
                    {
                        Color color = colors[colors.Count == 2 ? i / 2 : i];
                        byte scaledR = color.R;
                        byte scaledG = color.G;
                        byte scaledB = color.B;
                        if (effBrightness > 0)
                        {
                            if (color.R > 0 && scaledR == 0) scaledR = 1;
                            if (color.G > 0 && scaledG == 0) scaledG = 1;
                            if (color.B > 0 && scaledB == 0) scaledB = 1;
                        }
                        byte[] pkt = (byte[])kbPayload.Clone();
                        pkt[5] = scaledR;
                        pkt[6] = scaledG;
                        pkt[7] = scaledB;
                        pkt[9] = (byte)(i + 1); // Zones 1, 2, 3, 4
                        any |= SendLedArrayCommandUnderLock("SetGamingKBBacklight", pkt);
                        any |= SendLedArrayCommandUnderLock("SetGamingLED", pkt);
                        ulong colorPayload = ((uint)scaledB << 24) | ((uint)scaledG << 16) | ((uint)scaledR << 8) | (uint)(i + 1);
                        SendCommandUnderLock("SetGamingLEDColor", colorPayload);
                    }
                }



                if (_enableHardware || AcerServiceClient.CommandInterceptor != null)
                {
                    var leds = new List<(int Index, byte R, byte G, byte B)>();
                    for (int i = 0; i < colors.Count; i++)
                    {
                        leds.Add((i, colors[i].R, colors[i].G, colors[i].B));
                    }
                    _ = AcerServiceClient.SetKeyboardLightingAsync(1, "STATIC", leds, effBrightness, 0, true);
                }

                return any;

            }

            finally

            {

                Monitor.Exit(_lock);

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



                bool hardwareBrightness = EnsureKeyboardBrightnessUnderLock(force: true);
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



                    // Exterior Lightbars: Zones 6 (Lightbar 1), 7 (Lightbar 2).
                    // Zone 5 and broadcast masks (0x0F, 0xF0) are excluded from the exterior lightbar write.
                    ulong[] extBehaviorZones = { 0x06ul, 0x07ul };
                    foreach (var z in extBehaviorZones)
                    {
                        ulong zonePayload = ledBehavior | (z << 8)
                            | ((ulong)scaledR << 16) | ((ulong)scaledG << 24) | ((ulong)scaledB << 32);
                        SendCommandUnderLock("SetGamingLEDBehavior", zonePayload);
                    }

                    // Exterior Lightbar Colors: Zones 6 & 7 only.
                    // Zone 5 is excluded from the exterior lightbar colour write.
                    ulong[] extColorZones = { 6ul, 7ul };
                    foreach (var cz in extColorZones)
                    {
                        ulong colorPayload = ((ulong)scaledB << 24) | ((ulong)scaledG << 16) | ((ulong)scaledR << 8) | cz;
                        SendCommandUnderLock("SetGamingLEDColor", colorPayload);
                    }

                    // Dual dispatch to AcerService for rear lightbars only (exclude logo device 4)
                    if (_enableHardware) _ = AcerServiceClient.SetExteriorLightingAsync(mode, r, g, b, _brightness, _speed, includeLightbars: true);

                    // Dual dispatch dynamic effect to AcerServiceClient for Keyboard device
                    string kbEffect = mode switch
                    {
                        1 => "BREATHING",
                        2 => "NEON",
                        3 => "WAVE",
                        4 => "SHIFTING",
                        5 => "ZOOM",
                        6 => "METEOR",
                        _ => "STATIC"
                    };
                    if (_enableHardware && !hardwareBrightness) _ = AcerServiceClient.SetKeyboardLightingAsync(1, kbEffect, null, _brightness, (int)_speed, false);

                }

                catch { }



                // 1. EC Hardware Animation Loop Reset / Halt:
                // When Meteor (mode 6) is active, the EC hardware animation loop latches.
                // To exit it and switch to static or other modes, a static Mode 0 packet (byte 0=0, byte 8=0, byte 9=1)
                // must be dispatched to the EC via both SetGamingKBBacklight and SetGamingLED, wait 25ms, and notify AcerServiceClient.
                if (_appliedHardwareMode == 6 || (_appliedHardwareMode != mode && (mode == 0 || _appliedHardwareMode != 0)))
                {
                    byte[] resetPkt = new byte[16];
                    resetPkt[0] = 0; // Mode 0 Static halts EC hardware animation loop
                    resetPkt[1] = 0;
                    resetPkt[2] = (byte)Math.Clamp((int)_brightness, 0, 100);
                    resetPkt[3] = 0; // Disable dynamic effect flags
                    resetPkt[5] = r;
                    resetPkt[6] = g;
                    resetPkt[7] = b;
                    resetPkt[8] = 0; // Explicitly clear mode 6/7 sub-registers
                    resetPkt[9] = 1; // Keyboard
                    SendLedCommandUnderLock(resetPkt);
                    SendLedArrayCommandUnderLock("SetGamingLED", resetPkt);
                    if (_enableHardware) Thread.Sleep(25);
                    if ((_appliedHardwareMode == 6 || mode != 6) && (_enableHardware || AcerServiceClient.CommandInterceptor != null))
                    {
                        _ = AcerServiceClient.SetKeyboardLightingAsync(1, "STATIC", null, (byte)Math.Clamp((int)_brightness, 0, 100), 0, false);
                    }
                }

                // 2. Dual-dispatch SetGamingRgbKb for 4-zone keyboards (Static and solid modes)
                if (mode == 0)
                {
                    try
                    {
                        for (int zone = 1; zone <= 4; zone++)
                        {
                            ulong zoneMask = 1UL << (zone - 1);
                            ulong rgbKbPayload = zoneMask | ((ulong)(hardwareBrightness ? r : scaledR) << 8) | ((ulong)(hardwareBrightness ? g : scaledG) << 16) | ((ulong)(hardwareBrightness ? b : scaledB) << 24);
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
                payload[5] = r;
                payload[6] = g;
                payload[7] = b;

                if (mode == 6) // Meteor
                {
                    payload[8] = 3;
                }
                else
                {
                    payload[8] = 0; // Explicitly 0 to prevent sticking in Meteor
                }

                payload[9] = 1;              
                SendLedCommandUnderLock(payload);
                SendLedArrayCommandUnderLock("SetGamingLED", payload);
                _appliedHardwareMode = mode;



                // Exterior LED payload: Single-LED hardware only accepts Static (0), Breathing (1), or Neon (2).

                // Map dynamic modes (Wave=3, etc.) to Neon (2) so exterior lighting cycles colours alongside the keyboard.

                // SetGamingLED exterior sync omitted so the exterior write cannot clobber the keyboard

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



                    return _cachedGpuTempReading;

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

                    if (rpm == 0 || rpm > 15000 || IsDutyCyclePayload(rpm))

                        rpm = GetSensorReading((ulong)SensorId.System2FanSpeed);



                    _systemFanChecked = true;

                    if (rpm > 0 && rpm <= 15000 && !IsDutyCyclePayload(rpm))

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

        private readonly object _overlaySyncLock = new();

        private CancellationTokenSource? _overlaySyncCts;



        /// <summary>

        /// When false the app leaves the Windows power overlay untouched.

        /// Some corporate images and dock setups manage this policy externally.

        /// </summary>

        public bool WindowsOverlaySyncEnabled { get; set; } = true;



        /// <summary>Delay before the Windows overlay follows an Acer mode change.</summary>

        public int WindowsOverlaySyncDelayMs { get; set; } = 1200;



        /// <summary>

        /// Queues a single, cancellable Windows overlay update. A newer mode

        /// cancels the pending one, so rapid mode cycling never applies two

        /// overlay changes in a row.

        /// </summary>

        public void QueueWindowsOverlaySync(byte acerMode, int? delayMs = null)

        {

            if (!WindowsOverlaySyncEnabled) return;



            int delay = Math.Clamp(delayMs ?? WindowsOverlaySyncDelayMs, 0, 15000);

            CancellationTokenSource cts;



            lock (_overlaySyncLock)

            {

                if (_disposed) return;

                try { _overlaySyncCts?.Cancel(); } catch { }

                cts = new CancellationTokenSource();

                _overlaySyncCts = cts;

            }



            _ = Task.Run(async () =>

            {

                try

                {

                    if (delay > 0)

                        await Task.Delay(delay, cts.Token).ConfigureAwait(false);

                    if (cts.IsCancellationRequested) return;



                    lock (_lock)

                    {

                        if (_disposed || _lastWindowsOverlayMode == acerMode) return;

                        try

                        {

                            Guid overlay = GetOverlayForMode(acerMode);

                            PowerSetActiveOverlayScheme(in overlay);

                            _lastWindowsOverlayMode = acerMode;

                        }

                        catch { }

                    }

                }

                catch (OperationCanceledException) { }

                catch { }

            }, cts.Token);

        }





        private ManagementObject? _cachedBatteryObj;

        private DateTime _lastBatterySearchAttempt = DateTime.MinValue;



        private ManagementObject? GetBatteryControlObjectUnderLock()

        {

            if (_disposed) return null;

            if (_cachedBatteryObj != null) return _cachedBatteryObj;

            if ((DateTime.UtcNow - _lastBatterySearchAttempt) < SearchRetryInterval) return null;



            _lastBatterySearchAttempt = DateTime.UtcNow;

            ManagementObject? selected = null;

            try

            {

                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryControl", WmiEnumOptions);

                using var results = searcher.Get();

                foreach (ManagementBaseObject mbo in results)

                {

                    if (mbo is ManagementObject mo)

                    {

                        if (selected == null)

                        {

                            selected = mo;

                        }

                        else

                        {

                            SafeDisposeManagementObject(ref mo!);

                        }

                    }

                    else

                    {

                        try

                        {

                            if (Marshal.IsComObject(mbo)) Marshal.ReleaseComObject(mbo);

                            mbo.Dispose();

                        }

                        catch { }

                    }

                }

                _cachedBatteryObj = selected;

                selected = null;

            }

            catch

            {

                _cachedBatteryObj = null;

            }

            finally

            {

                if (selected != null)

                {

                    SafeDisposeManagementObject(ref selected);

                }

            }

            return _cachedBatteryObj;

        }



        private void InvalidateBatteryCacheUnderLock()

        {

            SafeDisposeManagementObject(ref _cachedBatteryObj);

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

                    if (inParams == null) return false;

                    inParams["uBatteryNo"] = (byte)1;

                    inParams["uFunctionMask"] = (byte)1;

                    inParams["uFunctionStatus"] = (byte)(enable ? 1 : 0);

                    inParams["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };



                    using var outParams = obj.InvokeMethod("SetBatteryHealthControl", inParams, WmiTimeoutOptions);

                    var rawRet = TryGetPropertyValue(outParams, "uReturn");

                    if (rawRet == null) return false;

                    if (rawRet is byte[] retBytes)

                    {

                        if (retBytes.Length > 0 && retBytes[0] != 0) return false;

                        return true;

                    }

                    uint result = Convert.ToUInt32(rawRet);

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
                // Strict Read-Modify-Write: query GetGamingSysInfo before SetGamingSysInfo
                // so upper status bytes containing volatile Fn/Media Key mode bits are never zeroed.
                var (getOk, currentVal) = GetGamingSysInfoUnderLock(AcerProtocol.GamingSysInfoConfigQuery);
                ulong payload = AcerProtocol.LcdOverdriveInputWithPreservedState(enable, getOk ? currentVal : 0);
                var res = SetGamingSysInfoUnderLock(payload);
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

                    if (inParams == null) return false;

                    inParams["uBatteryNo"] = (byte)1;

                    inParams["uFunctionQuery"] = (byte)1;

                    inParams["uReserved"] = new byte[2];



                    using var outParams = obj.InvokeMethod("GetBatteryHealthControlStatus", inParams, WmiTimeoutOptions);

                    var fnList = TryGetPropertyValue(outParams, "uFunctionList");

                    if (fnList == null) return false;

                    ulong list = Convert.ToUInt64(fnList);

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

                    if (inParams == null) return null;

                    inParams["uBatteryNo"] = (byte)1;

                    inParams["uFunctionQuery"] = (byte)1;

                    inParams["uReserved"] = new byte[2];



                    using var outParams = obj.InvokeMethod("GetBatteryHealthControlStatus", inParams, WmiTimeoutOptions);

                    if (outParams == null) return null;



                    var rawRet = TryGetPropertyValue(outParams, "uReturn");

                    if (rawRet != null)

                    {

                        if (rawRet is byte[] retBytes && retBytes.Length > 0 && retBytes.Any(b => b != 0)) return null;

                        if (rawRet is not byte[] && Convert.ToUInt32(rawRet) != 0) return null;

                    }



                    var fnList = TryGetPropertyValue(outParams, "uFunctionList");

                    var fnStatus = TryGetPropertyValue(outParams, "uFunctionStatus");

                    if (fnList == null || fnStatus == null) return null;

                    ulong list = Convert.ToUInt64(fnList);

                    var status = fnStatus as byte[];

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



                    bool healthOk = false;

                    try

                    {

                        using var inParams = obj.GetMethodParameters("SetBatteryHealthControl");

                        if (inParams != null)

                        {

                            inParams["uBatteryNo"] = (byte)1;

                            inParams["uFunctionMask"] = (byte)2;

                            inParams["uFunctionStatus"] = (byte)(enable ? 1 : 0);

                            inParams["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };



                            using var outParams = obj.InvokeMethod("SetBatteryHealthControl", inParams, WmiTimeoutOptions);

                            var rawRet = TryGetPropertyValue(outParams, "uReturn");

                            if (rawRet != null)

                            {

                                if (rawRet is byte[] retBytes)

                                {

                                    healthOk = retBytes.Length == 0 || retBytes[0] == 0;

                                }

                                else

                                {

                                    healthOk = Convert.ToUInt32(rawRet) == 0;

                                }

                            }

                        }

                    }

                    catch { }



                    // Also invoke SetBatteryFunctionData to activate/deactivate hardware auto-discharge circuit (BAC switch)

                    bool bacOk = false;

                    try

                    {

                        using var inDataParams = obj.GetMethodParameters("SetBatteryFunctionData");

                        if (inDataParams != null)

                        {

                            inDataParams["uFunctionMask"] = (byte)2;

                            inDataParams["uBACSwitch"] = (byte)(enable ? 1 : 0);

                            inDataParams["uReservedIn"] = new byte[6];



                            using var outDataParams = obj.InvokeMethod("SetBatteryFunctionData", inDataParams, WmiTimeoutOptions);

                            var retCode = TryGetPropertyValue(outDataParams, "uReturnCode");

                            if (retCode != null)

                            {

                                bacOk = Convert.ToUInt32(retCode) == 0;

                            }

                            else

                            {

                                bacOk = true;

                            }

                        }

                    }

                    catch { }



                    return healthOk || bacOk;

                }

                catch

                {

                    InvalidateBatteryCacheUnderLock();

                    return false;

                }

            }

        }



        public bool StartBatteryCalibration() => SetBatteryCalibration(true);



        public bool StopBatteryCalibration() => SetBatteryCalibration(false);



        public BatteryHealthStatus? GetBatteryCalibrationStatus() => GetBatteryHealthStatus();



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
            string model = HardwareIdentity.ReadModel();
            if (!AcerProtocol.UsesFanTable(model))
                return null;

            lock (_lock)

            {

                try

                {

                    var obj = GetWmiObjectUnderLock();

                    if (obj == null) return null;

                    ulong raw = 0;

                    bool queryOk = false;

                    WithGamingMutex(() =>

                    {

                        using var outParams = obj.InvokeMethod("GetGamingFanTable", null, WmiTimeoutOptions);

                        var gmOut = TryGetPropertyValue(outParams, "gmOutput");

                        if (gmOut != null)

                        {

                            raw = Convert.ToUInt64(gmOut);

                            queryOk = true;

                        }

                        return queryOk;

                    }, out _);

                    if (queryOk && AcerProtocol.IsOk(raw))

                        return AcerProtocol.FanTableValue(raw);

                }

                catch (ManagementException mex) when (mex.ErrorCode == ManagementStatus.NotFound ||
                                                     mex.ErrorCode == ManagementStatus.MethodNotImplemented ||
                                                     mex.ErrorCode == ManagementStatus.InvalidMethod)
                {
                    return null;
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
                // Strict Read-Modify-Write: Query GetGamingSysInfo before modifying USB charging bit
                // to preserve upper status bytes containing volatile Fn/Media Key mode bits
                var (sysOk, currentSys) = GetGamingSysInfoUnderLock(AcerProtocol.GamingSysInfoConfigQuery);
                ulong payload = AcerProtocol.UsbChargingInputWithPreservedState(on, floor, sysOk ? currentSys : 0);
                var res = SendActionCommandUnderLock("SetFunction", payload);
                if (!res.success)
                {
                    // Fallback to SetGamingSysInfo with preserved upper status bytes
                    ulong sysPayload = AcerProtocol.ApplySystemConfigBitPreservingUpperBytes(sysOk ? currentSys : 0, AcerProtocol.GamingSysInfoUsbChargeBit, on);
                    var sysRes = SetGamingSysInfoUnderLock(sysPayload);
                    return sysRes.success;
                }
                return res.success;
            }
        }



        public (int Brightness, int TimeoutSeconds)? GetBacklightTimeout(byte hotkey = 2)

        {

            lock (_lock)

            {

                if (EcHid != null && EcHid.IsOpen)

                {

                    var ecVal = EcHid.ReadBacklightTimeout();

                    if (ecVal.HasValue) return ecVal;

                }



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

            lock (_lock)

            {

                if (EcHid != null && EcHid.IsOpen)

                {

                    if (EcHid.WriteBacklightTimeout(brightness, timeoutSeconds))

                        return true;

                }



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



        ~WmiController()

        {

            Dispose(false);

        }



        public void Dispose()

        {

            Dispose(true);

            GC.SuppressFinalize(this);

        }



        protected virtual void Dispose(bool disposing)

        {

            if (disposing)

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

                    SafeDisposeManagementObject(ref _cachedObj);

                    SafeDisposeManagementObject(ref _cachedBatteryObj);

                    SafeDisposeManagementObject(ref _cachedActionObj);

                    try { EcHid?.Dispose(); } catch { }

                    EcHid = null;

                }



                lock (_overlaySyncLock)

                {

                    try { _overlaySyncCts?.Cancel(); } catch { }

                    _overlaySyncCts = null;

                }

            }

        }

    }

}