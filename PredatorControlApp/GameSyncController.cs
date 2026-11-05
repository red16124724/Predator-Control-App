using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class GameSyncController : IDisposable
    {
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly List<GameProfile> _profiles = new();
        private readonly string _savePath;

        private readonly object _lock = new();
        private bool _enabled;
        private bool _disposed;
        private string? _activeExe;
        private DashboardSnapshot? _savedSnapshot;    

        public event Action<GameProfile>? GameDetected;

        public event Action<DashboardSnapshot>? GameExited;

        public event Action<bool>? EnabledChanged;

        public bool IsEnabled
        {
            get { lock (_lock) { return _enabled; } }
            set
            {
                bool changed = false;
                DashboardSnapshot? snapToExit = null;
                lock (_lock)
                {
                    if (_disposed) return;
                    if (_enabled != value)
                    {
                        _enabled = value;
                        changed = true;
                        if (_enabled)
                        {
                            try { _pollTimer.Start(); } catch { }
                        }
                        else
                        {
                            try { _pollTimer.Stop(); } catch { }
                            if (_activeExe != null)
                            {
                                snapToExit = _savedSnapshot;
                                _activeExe = null;
                                _savedSnapshot = null;
                            }
                        }
                        SaveUnderLock();
                    }
                }
                if (changed)
                {
                    if (snapToExit != null) GameExited?.Invoke(snapToExit);
                    EnabledChanged?.Invoke(value);
                }
            }
        }

        public IReadOnlyList<GameProfile> Profiles
        {
            get
            {
                lock (_lock) { return _profiles.Select(p => p.Clone()).ToList().AsReadOnly(); }
            }
        }

        public string? ActiveGameExe
        {
            get { lock (_lock) { return _activeExe; } }
        }

        public static string SafeGetExecutableName(string? exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return "";
            string? native = PredatorNativeMigrations.ExecutableName(exeName);
            if (native != null) return native;
            string trimmed = exeName.Trim().Trim('"', (char)39);
            if (string.IsNullOrWhiteSpace(trimmed)) return "";
            try
            {
                string fileName = Path.GetFileName(trimmed);
                return string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
            }
            catch
            {
                int lastSlash = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
                if (lastSlash >= 0 && lastSlash < trimmed.Length - 1)
                    return trimmed.Substring(lastSlash + 1);
                return trimmed;
            }
        }

        public static string SafeGetProcessName(string? exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return "";
            string? native = PredatorNativeMigrations.ProcessName(exeName);
            if (native != null) return native;
            string name = SafeGetExecutableName(exeName);
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 4);
            return name;
        }

        public static void SanitizeProfileValues(GameProfile p)
        {
            if (p == null) return;

            // Field clamps are owned by the native core; the managed clamps below
            // remain the fallback when the DLL is absent.
            var native = new NativeProfileValues
            {
                CpuFanSpeed = p.CpuFanSpeed,
                GpuFanSpeed = p.GpuFanSpeed,
                SysFanSpeed = p.SysFanSpeed,
                RefreshRate = p.RefreshRate,
                BatteryLimit = p.BatteryLimit,
                RgbBrightness = p.RgbBrightness,
                RgbSpeed = p.RgbSpeed,
                RgbR = p.RgbR,
                RgbG = p.RgbG,
                RgbB = p.RgbB
            };
            if (PredatorNativeMigrations.SanitizeProfile(ref native))
            {
                p.CpuFanSpeed = native.CpuFanSpeed;
                p.GpuFanSpeed = native.GpuFanSpeed;
                p.SysFanSpeed = native.SysFanSpeed;
                p.RefreshRate = native.RefreshRate;
                p.BatteryLimit = native.BatteryLimit;
                p.RgbBrightness = native.RgbBrightness;
                p.RgbSpeed = native.RgbSpeed;
                p.RgbR = native.RgbR;
                p.RgbG = native.RgbG;
                p.RgbB = native.RgbB;
                return;
            }

            if (p.CpuFanSpeed != -1) p.CpuFanSpeed = Math.Clamp(p.CpuFanSpeed, 0, 100);
            if (p.GpuFanSpeed != -1) p.GpuFanSpeed = Math.Clamp(p.GpuFanSpeed, 0, 100);
            if (p.SysFanSpeed != -1) p.SysFanSpeed = Math.Clamp(p.SysFanSpeed, 0, 100);
            if (p.RefreshRate != -1 && p.RefreshRate < 30) p.RefreshRate = -1;
            if (p.BatteryLimit != -1) p.BatteryLimit = p.BatteryLimit < 0 ? -1 : Math.Clamp(p.BatteryLimit, 50, 100);
            if (p.RgbBrightness != -1) p.RgbBrightness = Math.Clamp(p.RgbBrightness, 0, 100);
            if (p.RgbSpeed != -1) p.RgbSpeed = Math.Clamp(p.RgbSpeed, 0, 100);
            if (p.RgbR != -1) p.RgbR = Math.Clamp(p.RgbR, 0, 255);
            if (p.RgbG != -1) p.RgbG = Math.Clamp(p.RgbG, 0, 255);
            if (p.RgbB != -1) p.RgbB = Math.Clamp(p.RgbB, 0, 255);
        }

        public GameSyncController(string? customSavePath = null)
        {
            if (customSavePath != null)
            {
                _savePath = customSavePath;
            }
            else
            {
                _savePath = "";
                try
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrEmpty(appData))
                        appData = Path.GetTempPath();
                    var dir = Path.Combine(appData, "PredatorControl");
                    Directory.CreateDirectory(dir);
                    _savePath = Path.Combine(dir, "game_sync.json");
                }
                catch { }
            }

            _pollTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _pollTimer.Tick += PollProcesses;

            Load();

            if (_enabled)
                _pollTimer.Start();
        }

        public void AddProfile(GameProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.ExecutableName)) return;
            SanitizeProfileValues(profile);
            lock (_lock)
            {
                _profiles.RemoveAll(p => p.ExecutableName.Equals(profile.ExecutableName, StringComparison.OrdinalIgnoreCase));
                _profiles.Add(profile.Clone());
                SaveUnderLock();
            }
        }

        public void RemoveProfile(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return;
            DashboardSnapshot? snapToExit = null;
            lock (_lock)
            {
                _profiles.RemoveAll(p => p.ExecutableName.Equals(exeName, StringComparison.OrdinalIgnoreCase));

                if (_activeExe != null && _activeExe.Equals(exeName, StringComparison.OrdinalIgnoreCase))
                {
                    snapToExit = _savedSnapshot;
                    _activeExe = null;
                    _savedSnapshot = null;
                }
                SaveUnderLock();
            }
            try
            {
                RegistrySafety.DeleteGameProfile(exeName);
            }
            catch { }
            if (snapToExit != null)
            {
                GameExited?.Invoke(snapToExit);
            }
        }

        public void UpdateProfile(GameProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.ExecutableName)) return;
            SanitizeProfileValues(profile);
            lock (_lock)
            {
                var idx = _profiles.FindIndex(p => p.ExecutableName.Equals(profile.ExecutableName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    _profiles[idx] = profile.Clone();
                else
                    _profiles.Add(profile.Clone());
                SaveUnderLock();
            }
        }

        public void SetPreGameSnapshot(DashboardSnapshot snapshot)
        {
            lock (_lock)
            {
                _savedSnapshot = snapshot?.Clone();
            }
        }

        private int _isPolling;
        
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        public static HashSet<string> GetRunningProcessNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr hSnap = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
            if (hSnap != IntPtr.Zero && hSnap != (IntPtr)(-1))
            {
                using var safeSnap = new Microsoft.Win32.SafeHandles.SafeFileHandle(hSnap, ownsHandle: true);
                try
                {
                    var pe = new PROCESSENTRY32();
                    pe.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                    if (Process32First(safeSnap.DangerousGetHandle(), ref pe))
                    {
                        do
                        {
                            if (!string.IsNullOrEmpty(pe.szExeFile))
                            {
                                set.Add(pe.szExeFile);
                            }
                        } while (Process32Next(safeSnap.DangerousGetHandle(), ref pe));
                    }
                }
                catch { }
            }
            else
            {
                Process[]? procs = null;
                try
                {
                    procs = Process.GetProcesses();
                    foreach (var p in procs)
                    {
                        try { set.Add(p.ProcessName + ".exe"); } catch { }
                    }
                }
                catch { }
                finally
                {
                    if (procs != null)
                    {
                        foreach (var p in procs)
                        {
                            try { p.Dispose(); } catch { }
                        }
                    }
                }
            }
            return set;
        }

        public static string? GetForegroundProcessName()
        {
            string? nativeFg = PredatorNativeMigrations.GetForegroundProcessName();
            if (!string.IsNullOrEmpty(nativeFg)) return nativeFg;

            try
            {
                IntPtr fgHwnd = GetForegroundWindow();
                if (fgHwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(fgHwnd, out uint fgPid);
                    if (fgPid > 0)
                    {
                        try
                        {
                            using var fgProc = Process.GetProcessById((int)fgPid);
                            return fgProc.ProcessName;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        private bool IsProcessActive(string nameWithoutExe)
        {
            if (string.IsNullOrWhiteSpace(nameWithoutExe)) return false;

            if (PredatorNativeMigrations.IsProcessRunning(nameWithoutExe))
                return true;

            string? fg = GetForegroundProcessName();
            if (fg != null && fg.Equals(nameWithoutExe, StringComparison.OrdinalIgnoreCase))
                return true;

            var running = GetRunningProcessNames();
            return running.Contains(nameWithoutExe + ".exe") || running.Contains(nameWithoutExe);
        }

        private void PollProcesses(object? sender, EventArgs e)
        {
            if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try
                {
                    PollOnce();
                }
                finally
                {
                    Interlocked.Exchange(ref _isPolling, 0);
                }
            });
        }

        internal void PollOnce()
        {
            GameProfile? detectedProfile = null;
            DashboardSnapshot? exitedSnapshot = null;
            string? currentActiveExe;
            List<GameProfile> profilesSnapshot;

            lock (_lock)
            {
                if (_disposed || !_enabled || _profiles.Count == 0) return;
                currentActiveExe = _activeExe;
                profilesSnapshot = _profiles.Select(p => p.Clone()).ToList();
            }

            // 1. Fast foreground window check
            string? fgProcName = GetForegroundProcessName();
            if (!string.IsNullOrEmpty(fgProcName))
            {
                var fgProfile = profilesSnapshot.FirstOrDefault(p =>
                    SafeGetProcessName(p.ExecutableName).Equals(fgProcName, StringComparison.OrdinalIgnoreCase));

                if (fgProfile != null)
                {
                    lock (_lock)
                    {
                        if (!_disposed && _enabled)
                        {
                            if (!string.Equals(_activeExe, fgProfile.ExecutableName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Switched to new game in foreground!
                                _activeExe = fgProfile.ExecutableName;
                                detectedProfile = fgProfile.Clone();
                            }
                        }
                    }

                    if (detectedProfile != null)
                    {
                        GameDetected?.Invoke(detectedProfile);
                    }
                    return;
                }
            }

            // 2. Background process check using atomic Toolhelp snapshot
            var runningProcs = GetRunningProcessNames();

            if (currentActiveExe != null)
            {
                string activeName = SafeGetExecutableName(currentActiveExe);
                bool stillRunning = runningProcs.Contains(activeName);

                if (!stillRunning)
                {
                    lock (_lock)
                    {
                        if (!_disposed && string.Equals(_activeExe, currentActiveExe, StringComparison.OrdinalIgnoreCase))
                        {
                            exitedSnapshot = _savedSnapshot?.Clone() ?? new DashboardSnapshot();
                            _activeExe = null;
                            _savedSnapshot = null;
                            
                            // Rapid game launching transition: check if another game launched immediately
                            foreach (var profile in profilesSnapshot)
                            {
                                string exeName = SafeGetExecutableName(profile.ExecutableName);
                                if (runningProcs.Contains(exeName))
                                {
                                    _activeExe = profile.ExecutableName;
                                    _savedSnapshot = exitedSnapshot; // Preserve original pre-game snapshot!
                                    detectedProfile = profile.Clone();
                                    exitedSnapshot = null; // Seamless direct transition!
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (var profile in profilesSnapshot)
                {
                    string exeName = SafeGetExecutableName(profile.ExecutableName);
                    if (runningProcs.Contains(exeName))
                    {
                        lock (_lock)
                        {
                            if (!_disposed && _enabled && _activeExe == null)
                            {
                                _activeExe = profile.ExecutableName;
                                detectedProfile = profile.Clone();
                            }
                        }
                        break;
                    }
                }
            }

            if (exitedSnapshot != null)
                GameExited?.Invoke(exitedSnapshot);
            else if (detectedProfile != null)
                GameDetected?.Invoke(detectedProfile);
        }

        #region Persistence

        private void SaveUnderLock()
        {
            try
            {
                if (string.IsNullOrEmpty(_savePath)) return;
                var data = new GameSyncData
                {
                    Enabled = _enabled,
                    Profiles = _profiles.ToList()
                };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                var dir = Path.GetDirectoryName(_savePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tempPath = Path.Combine(!string.IsNullOrEmpty(dir) ? dir : Path.GetTempPath(), $"{Path.GetFileName(_savePath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(tempPath, json);
                    try { if (File.Exists(_savePath)) File.SetAttributes(_savePath, FileAttributes.Normal); } catch { }
                    File.Move(tempPath, _savePath, overwrite: true);
                    try { File.Copy(_savePath, _savePath + ".bak", overwrite: true); } catch { }
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
            catch { }
        }

        public void Save()
        {
            lock (_lock)
            {
                SaveUnderLock();
            }
        }

        public void Load()
        {
            lock (_lock)
            {
                bool loaded = false;
                try
                {
                    if (!string.IsNullOrEmpty(_savePath) && File.Exists(_savePath))
                    {
                        var json = File.ReadAllText(_savePath);
                        var data = JsonSerializer.Deserialize<GameSyncData>(json);
                        if (data != null)
                        {
                            _enabled = data.Enabled;
                            _profiles.Clear();
                            if (data.Profiles != null)
                            {
                                foreach (var p in data.Profiles)
                                {
                                    if (p != null && !string.IsNullOrWhiteSpace(p.ExecutableName))
                                    {
                                        SanitizeProfileValues(p);
                                        _profiles.Add(p);
                                    }
                                }
                            }
                            loaded = true;
                        }
                    }
                }
                catch { }

                if (!loaded && !string.IsNullOrEmpty(_savePath))
                {
                    string bakPath = _savePath + ".bak";
                    if (File.Exists(bakPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(bakPath);
                            var data = JsonSerializer.Deserialize<GameSyncData>(json);
                            if (data != null)
                            {
                                _enabled = data.Enabled;
                                _profiles.Clear();
                                if (data.Profiles != null)
                                {
                                    foreach (var p in data.Profiles)
                                    {
                                        if (p != null && !string.IsNullOrWhiteSpace(p.ExecutableName))
                                        {
                                            SanitizeProfileValues(p);
                                            _profiles.Add(p);
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private class GameSyncData
        {
            public bool Enabled { get; set; }
            public List<GameProfile>? Profiles { get; set; }
        }

        // Redundant set consolidated with GameProfile.BlockedExecutableNames
        private static HashSet<string> BlockedExecutables => GameProfile.BlockedExecutableNames;

        private const long MaxImportBytes = 5 * 1024 * 1024;

        private static void ValidateJsonPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path required.", nameof(path));
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only .json files are allowed.", nameof(path));
            if (path.Split('/', '\\').Any(s => s == ".."))
                throw new System.Security.SecurityException("Path traversal rejected.");
        }

        public void ExportProfiles(string path)
        {
            ValidateJsonPath(path);
            string full = Path.GetFullPath(path);
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(new GameSyncData { Enabled = _enabled, Profiles = _profiles.ToList() },
                    new JsonSerializerOptions { WriteIndented = true });
            }
            File.WriteAllText(full, json);
        }

        public int ImportProfiles(string path, bool merge = true)
        {
            ValidateJsonPath(path);
            string full = Path.GetFullPath(path);
            var fileInfo = new FileInfo(full);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("Import file not found.", full);
            if (fileInfo.Length > MaxImportBytes)
                throw new InvalidDataException($"File exceeds maximum allowed size of {MaxImportBytes} bytes.");
            GameSyncData? data;
            try
            {
                data = JsonSerializer.Deserialize<GameSyncData>(File.ReadAllText(full));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Corrupted JSON format.", ex);
            }
            var accepted = new List<GameProfile>();
            if (data?.Profiles != null)
            {
                foreach (var p in data.Profiles)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.ExecutableName)) continue;
                    string name = p.ExecutableName.Trim();
                    if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..")
                        || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                        || BlockedExecutables.Contains(name)) continue;
                    p.ExecutableName = name;
                    SanitizeProfileValues(p);
                    accepted.Add(p);
                }
            }
            lock (_lock)
            {
                if (!merge) _profiles.Clear();
                foreach (var p in accepted)
                {
                    _profiles.RemoveAll(x => x.ExecutableName.Equals(p.ExecutableName, StringComparison.OrdinalIgnoreCase));
                    _profiles.Add(p);
                }
                SaveUnderLock();
            }
            return accepted.Count;
        }

        #endregion

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _enabled = false;
                                GameDetected = null;
                GameExited = null;
                EnabledChanged = null;
            }
            try
            {
                _pollTimer.Stop();
                _pollTimer.Tick -= PollProcesses;
                _pollTimer.Dispose();
            }
            catch { }
        }
    }
}
