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
                    if (_enabled != value)
                    {
                        _enabled = value;
                        changed = true;
                        if (_enabled)
                            _pollTimer.Start();
                        else
                        {
                            _pollTimer.Stop();
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
                lock (_lock) { return _profiles.ToList().AsReadOnly(); }
            }
        }

        public string? ActiveGameExe
        {
            get { lock (_lock) { return _activeExe; } }
        }

        public static string SafeGetExecutableName(string? exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return "";
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
            string name = SafeGetExecutableName(exeName);
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 4);
            return name;
        }

        public static void SanitizeProfileValues(GameProfile p)
        {
            if (p == null) return;
            if (p.CpuFanSpeed != -1) p.CpuFanSpeed = Math.Clamp(p.CpuFanSpeed, 0, 100);
            if (p.GpuFanSpeed != -1) p.GpuFanSpeed = Math.Clamp(p.GpuFanSpeed, 0, 100);
            if (p.SysFanSpeed != -1) p.SysFanSpeed = Math.Clamp(p.SysFanSpeed, 0, 100);
            if (p.RefreshRate != -1 && p.RefreshRate < 30) p.RefreshRate = -1;
            if (p.BatteryLimit != -1) p.BatteryLimit = Math.Clamp(p.BatteryLimit, 50, 100);
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
                _profiles.Add(profile);
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
                    _profiles[idx] = profile;
                else
                    _profiles.Add(profile);
                SaveUnderLock();
            }
        }

        public void SetPreGameSnapshot(DashboardSnapshot snapshot)
        {
            lock (_lock)
            {
                _savedSnapshot = snapshot;
            }
        }

        private int _isPolling;
        private int _cachedActivePid = -1;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private bool IsProcessActive(string nameWithoutExe)
        {
            if (string.IsNullOrWhiteSpace(nameWithoutExe)) return false;

            // 1. Fast Path: Check if currently focused/foreground window belongs to this process (O(1) < 0.05ms)
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
                            if (fgProc.ProcessName.Equals(nameWithoutExe, StringComparison.OrdinalIgnoreCase))
                            {
                                _cachedActivePid = (int)fgPid;
                                return true;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 2. Fast Path: Check previously tracked PID without scanning all processes
            if (_cachedActivePid > 0)
            {
                try
                {
                    using var proc = Process.GetProcessById(_cachedActivePid);
                    if (!proc.HasExited && proc.ProcessName.Equals(nameWithoutExe, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    _cachedActivePid = -1;
                }
            }

            // 3. Fallback: Search by process name
            Process[]? matches = null;
            try
            {
                matches = Process.GetProcessesByName(nameWithoutExe);
                if (matches.Length == 0) return false;

                bool isBrowser = nameWithoutExe.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                                 nameWithoutExe.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
                                 nameWithoutExe.Equals("msedge", StringComparison.OrdinalIgnoreCase);

                foreach (var p in matches)
                {
                    try
                    {
                        if (isBrowser)
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                _cachedActivePid = p.Id;
                                return true;
                            }
                        }
                        else
                        {
                            try
                            {
                                if (!p.HasExited)
                                {
                                    _cachedActivePid = p.Id;
                                    return true;
                                }
                            }
                            catch
                            {
                                // Access Denied from anti-cheat or elevated game process means it is active!
                                _cachedActivePid = p.Id;
                                return true;
                            }
                        }
                    }
                    catch { }
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (matches != null)
                {
                    foreach (var p in matches)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
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
                if (!_enabled || _profiles.Count == 0) return;
                currentActiveExe = _activeExe;
                profilesSnapshot = _profiles.ToList();
            }

            if (currentActiveExe != null)
            {
                string nameWithoutExe = Path.GetFileNameWithoutExtension(currentActiveExe).Trim();
                bool stillRunning = IsProcessActive(nameWithoutExe);

                if (!stillRunning)
                {
                    lock (_lock)
                    {
                        if (_activeExe == currentActiveExe)
                        {
                            exitedSnapshot = _savedSnapshot;
                            _activeExe = null;
                            _savedSnapshot = null;
                        }
                    }
                }
            }
            else
            {
                foreach (var profile in profilesSnapshot)
                {
                    string nameWithoutExe = Path.GetFileNameWithoutExtension(profile.ExecutableName).Trim();
                    if (IsProcessActive(nameWithoutExe))
                    {
                        lock (_lock)
                        {
                            if (_enabled && _activeExe == null)
                            {
                                _activeExe = profile.ExecutableName;
                                detectedProfile = profile;
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
                File.WriteAllText(_savePath, json);
                try { File.Copy(_savePath, _savePath + ".bak", overwrite: true); } catch { }
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

        private static readonly HashSet<string> BlockedExecutables = new(StringComparer.OrdinalIgnoreCase)
        {
            "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
            "rundll32.exe", "regsvr32.exe", "explorer.exe", "svchost.exe", "csrss.exe", "winlogon.exe", "lsass.exe"
        };

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
            if (new FileInfo(full).Length > MaxImportBytes)
                throw new InvalidDataException("Import file too large.");
            var data = JsonSerializer.Deserialize<GameSyncData>(File.ReadAllText(full));
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
            }
            _pollTimer.Stop();
            _pollTimer.Dispose();
        }
    }
}
