using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace PredatorControlApp
{
    internal static class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const int HWND_BROADCAST = 0xffff;
        private static Mutex? _appMutex;
        private static readonly object _mutexLock = new();

        private static readonly HashSet<string> _reported = new();
        private static bool _dialogOpen;
        private static readonly object _reportLock = new();

        private const string DefaultMutexName = @"Global\PredatorControlApp_Unique_System_Mutex_999";
        internal static string MutexName = DefaultMutexName;

        private static Mutex CreateSafeMutex(string name)
        {
            try
            {
                var sec = new MutexSecurity();
                var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

                sec.AddAccessRule(new MutexAccessRule(authUsers, MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
                sec.AddAccessRule(new MutexAccessRule(admins, MutexRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new MutexAccessRule(localSystem, MutexRights.FullControl, AccessControlType.Allow));

                return MutexAcl.Create(false, name, out _, sec);
            }
            catch
            {
                try
                {
                    return new Mutex(false, name);
                }
                catch
                {
                    string localName = name.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase)
                        ? @"Local\" + name.Substring(7)
                        : name;
                    return new Mutex(false, localName);
                }
            }
        }

        private static void OnProcessExitCleanup(object? sender, EventArgs e)
        {
            ReleaseSingleInstanceLock();
        }

        internal static bool TryTakeSingleInstanceLock(string? customMutexName = null)
        {
            lock (_mutexLock)
            {
                try
                {
                    if (_appMutex != null) return false;
                    string name = customMutexName ?? MutexName;
                    var mutex = CreateSafeMutex(name);
                    bool acquired = false;
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.Zero, false);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }
                    catch
                    {
                        try { mutex.Dispose(); } catch { }
                        throw;
                    }

                    if (!acquired)
                    {
                        try { mutex.Dispose(); } catch { }
                        return false;
                    }

                    _appMutex = mutex;
                    try
                    {
                        AppDomain.CurrentDomain.ProcessExit -= OnProcessExitCleanup;
                        AppDomain.CurrentDomain.ProcessExit += OnProcessExitCleanup;
                    }
                    catch { }
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static void ReleaseSingleInstanceLock()
        {
            lock (_mutexLock)
            {
                var mtx = _appMutex;
                _appMutex = null;
                if (mtx != null)
                {
                    try { mtx.ReleaseMutex(); } catch { }
                    try { mtx.Dispose(); } catch { }
                }
            }
        }

        private static string LogPath
        {
            get
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
                return Path.Combine(dir, "PredatorControl", "crash.log");
            }
        }

        internal static bool IsBenignCancellation(Exception? ex)
        {
            if (ex == null) return true;
            if (ex is OperationCanceledException or TaskCanceledException) return true;
            if (ex is AggregateException agg)
            {
                var flat = agg.Flatten();
                if (flat.InnerExceptions.Count > 0 && flat.InnerExceptions.All(IsBenignCancellation))
                    return true;
            }
            if (ex is System.Net.Sockets.SocketException se &&
                (se.SocketErrorCode == System.Net.Sockets.SocketError.OperationAborted ||
                 se.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut))
            {
                return true;
            }
            return false;
        }

        internal static void Report(Exception ex, bool fatal)
        {
            if (!fatal && IsBenignCancellation(ex)) return;

            string key = $"{ex.GetType().FullName}|{ex.StackTrace}";

            lock (_reportLock)
            {
                try
                {
                    var path = LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    {
                        try { File.Move(path, path + ".old", true); } catch { try { File.Delete(path); } catch { } }
                    }
                    File.AppendAllText(path, $"[{DateTime.UtcNow:u}] {(fatal ? "FATAL" : "ERROR")} {ex}\n\n");
                }
                catch
                {
                    try
                    {
                        string tempLog = Path.Combine(Path.GetTempPath(), "PredatorControl-crash.log");
                        File.AppendAllText(tempLog, $"[{DateTime.UtcNow:u}] {(fatal ? "FATAL" : "ERROR")} {ex}\n\n");
                    }
                    catch { }
                }

                if (_dialogOpen || !_reported.Add(key)) return;
                _dialogOpen = true;
            }

            try
            {
                PowerWakeLock.ReleaseAll();
            }
            catch { }

            try
            {
                MessageBox.Show(
                    $"{(fatal ? "Fatal" : "Error")}: {ex.Message}\n\nLogged to:\n{LogPath}",
                    "Predator Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
            finally
            {
                lock (_reportLock)
                {
                    _dialogOpen = false;
                }
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Any(a => {
                if (string.IsNullOrWhiteSpace(a)) return false;
                string clean = a.Trim().TrimStart('-', '/');
                return clean.Equals("apply-power", StringComparison.OrdinalIgnoreCase);
            }))
            {
                ApplyPowerAtBootFast();
                return;
            }

            if (!TryTakeSingleInstanceLock())
            {
                try
                {
                    bool sent = SecureNamedPipeIpc.SendMessageWithVerificationAsync("SHOW", 1500).GetAwaiter().GetResult();
                    if (!sent)
                    {
                        PostMessage((IntPtr)HWND_BROADCAST, Form1.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                catch
                {
                    PostMessage((IntPtr)HWND_BROADCAST, Form1.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                }
                return;
            }

            try
            {
                try
            {
                using var currentProc = Process.GetCurrentProcess();
                currentProc.PriorityClass = ProcessPriorityClass.High;
            }
            catch { }

            ApplicationConfiguration.Initialize();
                SelfCheck.Run();

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) =>
                {
                    if (e.Exception != null && !IsBenignCancellation(e.Exception))
                    {
                        Report(e.Exception, false);
                    }
                };

                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    if (e.ExceptionObject is Exception ex && !IsBenignCancellation(ex))
                    {
                        Report(ex, true);
                    }
                };

                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    e.SetObserved();
                    if (e.Exception != null && !IsBenignCancellation(e.Exception))
                    {
                        Report(e.Exception, false);
                    }
                };

                Form1 form;
                try
                {
                    form = new Form1();
                }
                catch (Exception ex)
                {
                    Report(ex, true);
                    return;
                }

                Application.Run(form);
            }
            finally
            {
                ReleaseSingleInstanceLock();
            }
        }

        internal static byte ResolvePowerMode(bool onBattery)
        {
            byte[] acProfileValues = { 0xFF, 0x00, 0x01, 0x04, 0x05 };
            byte[] batteryProfileValues = { 0xFF, 0x00, 0x01, 0x06 };

            int? ReadModeFromKey(RegistryKey? key)
            {
                if (key == null) return null;
                try
                {
                    if (onBattery)
                    {
                        // 1. Check AutoPowerBattery dropdown selection
                        object? autoBatVal = key.GetValue("AutoPowerBattery");
                        if (TryParseInt(autoBatVal, out int autoBatIdx) && autoBatIdx > 0 && autoBatIdx < batteryProfileValues.Length)
                        {
                            return batteryProfileValues[autoBatIdx];
                        }

                        // 2. Check Power_Battery
                        object? pwrBatVal = key.GetValue("Power_Battery");
                        if (TryParseInt(pwrBatVal, out int pwrBat))
                        {
                            return pwrBat;
                        }
                    }
                    else
                    {
                        // 1. Check AutoPowerAC dropdown selection
                        object? autoAcVal = key.GetValue("AutoPowerAC");
                        if (TryParseInt(autoAcVal, out int autoAcIdx) && autoAcIdx > 0 && autoAcIdx < acProfileValues.Length)
                        {
                            return acProfileValues[autoAcIdx];
                        }

                        // 2. Check Power_AC
                        object? pwrAcVal = key.GetValue("Power_AC");
                        if (TryParseInt(pwrAcVal, out int pwrAc))
                        {
                            return pwrAc;
                        }
                    }

                    // 3. Fallback to general Power setting
                    object? pwrVal = key.GetValue("Power");
                    if (TryParseInt(pwrVal, out int pwr))
                    {
                        return pwr;
                    }
                }
                catch { }
                return null;
            }

            // 1. Try HKCU (user-specific preference)
            try
            {
                using var hkcu = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\PredatorControl");
                var m = ReadModeFromKey(hkcu);
                if (m.HasValue) return FinalizeMode(m.Value, onBattery);
            }
            catch { }

            // 2. Try user accounts in HKEY_USERS (useful when running as SYSTEM at boot)
            try
            {
                foreach (var sid in Registry.Users.GetSubKeyNames())
                {
                    if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    {
                        using var userKey = Registry.Users.OpenSubKey($@"{sid}\SOFTWARE\PredatorControl");
                        var m = ReadModeFromKey(userKey);
                        if (m.HasValue) return FinalizeMode(m.Value, onBattery);
                    }
                }
            }
            catch { }

            // 3. Try HKLM (machine-wide fallback)
            try
            {
                using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PredatorControl");
                var m = ReadModeFromKey(hklm);
                if (m.HasValue) return FinalizeMode(m.Value, onBattery);
            }
            catch { }

            return 0x01; // Default Balanced
        }

        internal static byte ResolveFanMode(bool onBattery)
        {
            byte[] fanProfileValues = { 0xFF, 0x01, 0x02, 0x03 };

            int? ReadFanModeFromKey(RegistryKey? key)
            {
                if (key == null) return null;
                try
                {
                    if (onBattery)
                    {
                        // 1. Check AutoFanBattery dropdown selection
                        object? autoBatVal = key.GetValue("AutoFanBattery");
                        if (TryParseInt(autoBatVal, out int autoBatIdx) && autoBatIdx > 0 && autoBatIdx < fanProfileValues.Length)
                        {
                            return fanProfileValues[autoBatIdx];
                        }

                        // 2. Check Fan_Battery
                        object? fanBatVal = key.GetValue("Fan_Battery");
                        if (TryParseInt(fanBatVal, out int fanBat))
                        {
                            return fanBat;
                        }
                    }
                    else
                    {
                        // 1. Check AutoFanAC dropdown selection
                        object? autoAcVal = key.GetValue("AutoFanAC");
                        if (TryParseInt(autoAcVal, out int autoAcIdx) && autoAcIdx > 0 && autoAcIdx < fanProfileValues.Length)
                        {
                            return fanProfileValues[autoAcIdx];
                        }

                        // 2. Check Fan_AC
                        object? fanAcVal = key.GetValue("Fan_AC");
                        if (TryParseInt(fanAcVal, out int fanAc))
                        {
                            return fanAc;
                        }
                    }

                    // 3. Fallback to general Fan setting
                    object? fanVal = key.GetValue("Fan");
                    if (TryParseInt(fanVal, out int fan))
                    {
                        return fan;
                    }
                }
                catch { }
                return null;
            }

            // 1. Try HKCU (user-specific preference)
            try
            {
                using var hkcu = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\PredatorControl");
                var m = ReadFanModeFromKey(hkcu);
                if (m.HasValue) return FinalizeFanMode(m.Value);
            }
            catch { }

            // 2. Try user accounts in HKEY_USERS (useful when running as SYSTEM at boot)
            try
            {
                foreach (var sid in Registry.Users.GetSubKeyNames())
                {
                    if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    {
                        using var userKey = Registry.Users.OpenSubKey($@"{sid}\SOFTWARE\PredatorControl");
                        var m = ReadFanModeFromKey(userKey);
                        if (m.HasValue) return FinalizeFanMode(m.Value);
                    }
                }
            }
            catch { }

            // 3. Try HKLM (machine-wide fallback)
            try
            {
                using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PredatorControl");
                var m = ReadFanModeFromKey(hklm);
                if (m.HasValue) return FinalizeFanMode(m.Value);
            }
            catch { }

            return 0x01; // Default Auto
        }

        private static byte FinalizeFanMode(int mode)
        {
            if (mode is < 0 or > 255) return 0x01;
            byte b = (byte)mode;
            if (b != 0x01 && b != 0x02 && b != 0x03)
            {
                return 0x01; // Default Auto for any unexpected value
            }
            return b;
        }

        private static byte FinalizeMode(int mode, bool onBattery)
        {
            if (mode is < 0 or > 255) return 0x01;
            byte b = (byte)mode;
            if (b != 0x00 && b != 0x01 && b != 0x04 && b != 0x05 && b != 0x06)
            {
                return 0x01; // Default Balanced for any unexpected value
            }
            if (onBattery && (b == 0x04 || b == 0x05))
            {
                return 0x01; // Force balanced on battery
            }
            if (!onBattery && b == 0x06)
            {
                return 0x01; // Eco is not supported on AC power, fallback to Balanced
            }
            return b;
        }

        private static bool TryParseInt(object? val, out int result)
        {
            if (val is int i) { result = i; return true; }
            if (val != null && int.TryParse(val.ToString(), out int parsed)) { result = parsed; return true; }
            result = 0;
            return false;
        }

        internal static void ResolveFanSpeeds(bool onBattery, out byte cpu, out byte gpu, string subKeyPath = @"SOFTWARE\PredatorControl")
        {
            cpu = 50;
            gpu = 50;
            string suffix = onBattery ? "Battery" : "AC";

            bool TryReadSpeedsFromKey(RegistryKey? key, ref byte cSpeed, ref byte gSpeed)
            {
                if (key == null) return false;
                try
                {
                    object? cpuVal = key.GetValue($"FanSpeedCpu{suffix}") ?? key.GetValue("FanSpeedCpu");
                    object? gpuVal = key.GetValue($"FanSpeedGpu{suffix}") ?? key.GetValue("FanSpeedGpu");
                    bool found = false;
                    if (TryParseInt(cpuVal, out int c) && c >= 10 && c <= 100) { cSpeed = (byte)c; found = true; }
                    if (TryParseInt(gpuVal, out int g) && g >= 10 && g <= 100) { gSpeed = (byte)g; found = true; }
                    return found;
                }
                catch { return false; }
            }

            // 1. Try HKCU (user-specific preference)
            try
            {
                using var hkcu = Registry.CurrentUser.OpenSubKey(subKeyPath);
                if (TryReadSpeedsFromKey(hkcu, ref cpu, ref gpu)) return;
            }
            catch { }

            // 2. Try user accounts in HKEY_USERS (useful when running as SYSTEM at boot)
            try
            {
                foreach (var sid in Registry.Users.GetSubKeyNames())
                {
                    if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    {
                        using var userKey = Registry.Users.OpenSubKey($@"{sid}\SOFTWARE\PredatorControl");
                        if (TryReadSpeedsFromKey(userKey, ref cpu, ref gpu)) return;
                    }
                }
            }
            catch { }

            // 3. Try HKLM (machine-wide fallback)
            try
            {
                using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PredatorControl");
                if (TryReadSpeedsFromKey(hklm, ref cpu, ref gpu)) return;
            }
            catch { }
        }

        internal static void TryApplyStartupPowerLimits(WmiController? wmi = null, byte? powerMode = null)
        {
            try
            {
                var settings = AppSettings.Load();
                var power = SystemInformation.PowerStatus;
                bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                bool onBattery = power.PowerLineStatus != PowerLineStatus.Online && !isCharging;
                byte modeByte = powerMode ?? ResolvePowerMode(onBattery);
                var opMode = (OperatingMode)modeByte;

                var (userPl1, userPl2) = settings.GetUserPowerLimitsForMode(opMode);
                bool hasUserLimits = userPl1 > 0 && userPl2 > 0;
                if (settings.EnableCustomPowerLimits || hasUserLimits)
                {
                    var (pl1, pl2) = settings.GetEffectiveLimitsForMode(opMode);
                    if (pl1 > 0 && pl2 > 0)
                    {
                        // 1. Ensure active profile ACPI power mode is set in WMI
                        if (wmi != null)
                        {
                            try { wmi.TrySetPowerMode(modeByte, forceRetry: true); } catch { }
                        }

                        // 2. Apply active profile CPU power limits via PawnIO (initialize kernel handle if needed)
                        var pawn = PawnIoController.Instance;
                        if (pawn.IsAvailable || pawn.EnsureInitialized() == PowerLimitApplyStatus.Success)
                        {
                            pawn.ApplyPowerLimits(pl1, pl2);
                        }
                    }
                }
            }
            catch { }
        }

        internal static void TryApplyPowerLimitsAtBoot(byte powerMode) => TryApplyStartupPowerLimits(null, powerMode);

        internal static void ApplyPowerAtBootFast()
        {
            try
            {
                var power = SystemInformation.PowerStatus;
                bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                bool onBattery = power.PowerLineStatus != PowerLineStatus.Online && !isCharging;
                byte powerMode = ResolvePowerMode(onBattery);
                byte fanMode = ResolveFanMode(onBattery);

                using var wmi = new WmiController();
                // When running immediately at boot or logon, WMI or ACPI driver may take a few moments to initialize.
                // Retry with a quick poll up to 5 seconds to ensure the hardware register receives the command.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    if (wmi.TrySetPowerMode(powerMode, forceRetry: true))
                    {
                        wmi.SetFanBehavior(fanMode, applyCustomSpeeds: true);
                        if (fanMode == 0x03)
                        {
                            ResolveFanSpeeds(onBattery, out byte cpu, out byte gpu);
                            wmi.SetFanSpeed(cpu, gpu);
                        }
                        TryApplyStartupPowerLimits(wmi, powerMode);
                        break;
                    }
                    Thread.Sleep(200);
                }
            }
            catch { }
        }
    }
}
