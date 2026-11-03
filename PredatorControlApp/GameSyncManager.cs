using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    /// <summary>
    /// Thread-safe manager for Game Sync profile detection, active window tracking,
    /// and seamless game profile transitions.
    /// Acts as the high-level orchestration interface for GameSyncController.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class GameSyncManager : IDisposable
    {
        private readonly GameSyncController _controller;

        public event Action<GameProfile>? GameDetected
        {
            add { _controller.GameDetected += value; }
            remove { _controller.GameDetected -= value; }
        }

        public event Action<DashboardSnapshot>? GameExited
        {
            add { _controller.GameExited += value; }
            remove { _controller.GameExited -= value; }
        }

        public event Action<bool>? EnabledChanged
        {
            add { _controller.EnabledChanged += value; }
            remove { _controller.EnabledChanged -= value; }
        }

        public bool IsEnabled
        {
            get => _controller.IsEnabled;
            set => _controller.IsEnabled = value;
        }

        public IReadOnlyList<GameProfile> Profiles => _controller.Profiles;
        public string? ActiveGameExe => _controller.ActiveGameExe;

        public GameSyncManager(string? customSavePath = null)
        {
            _controller = new GameSyncController(customSavePath);
        }

        public GameSyncManager(GameSyncController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        public GameSyncController UnderlyingController => _controller;

        public void AddProfile(GameProfile profile) => _controller.AddProfile(profile);
        public void RemoveProfile(string exeName) => _controller.RemoveProfile(exeName);
        public void UpdateProfile(GameProfile profile) => _controller.UpdateProfile(profile);
        public void SetPreGameSnapshot(DashboardSnapshot snapshot) => _controller.SetPreGameSnapshot(snapshot);
        public void PollOnce() => _controller.PollOnce();

        public static string SafeGetExecutableName(string? exeName) => GameSyncController.SafeGetExecutableName(exeName);
        public static string SafeGetProcessName(string? exeName) => GameSyncController.SafeGetProcessName(exeName);
        public static string? GetForegroundProcessName() => GameSyncController.GetForegroundProcessName();
        public static HashSet<string> GetRunningProcessNames() => GameSyncController.GetRunningProcessNames();

        public void Dispose()
        {
            _controller.Dispose();
        }
    }
}
