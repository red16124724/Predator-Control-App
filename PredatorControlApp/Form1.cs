using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public partial class Form1 : Form
    {
        #region Win32 Interop — Single Instance

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        internal static readonly uint WM_SHOWME = RegisterWindowMessage("PREDATOR_CONTROL_SHOW_INSTANCE");

        #endregion

        #region Win32 Interop — Window Dragging

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        #endregion

        #region Win32 Interop — Dark Scrollbar

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int SB_VERT = 1;

        #endregion

        #region Win32 Interop — Display Control

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int CDS_UPDATEREGISTRY = 0x01;
        private const int CDS_TEST = 0x02;
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const int DM_BITSPERPEL = 0x040000;
        private const int DM_PELSWIDTH = 0x080000;
        private const int DM_PELSHEIGHT = 0x100000;
        private const int DM_DISPLAYFREQUENCY = 0x400000;
        private const int DM_INTERLACED = 0x02;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
            public int dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        #endregion

        #region Win32 Interop — Power & Lid Notifications

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

        internal const int WM_POWERBROADCAST = 0x0218;
        internal const int PBT_APMQUERYSUSPEND = 0x0000;
        internal const int PBT_APMSUSPEND = 0x0004;
        internal const int PBT_APMRESUMESUSPEND = 0x0007;
        internal const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
        internal const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        internal const int PBT_POWERSETTINGCHANGE = 0x8013;
        internal const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

        internal static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
        internal static readonly Guid GUID_ACDC_POWER_SOURCE = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
        internal static readonly Guid GUID_BATTERY_PERCENTAGE_REMAINING = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");
        internal static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C1019F6160C4");

        [StructLayout(LayoutKind.Sequential)]
        private struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data;
        }

        #endregion

        #region Fields
        
        private WmiController _wmi = new();
        private System.Windows.Forms.Timer _timer = new();
        private NotifyIcon _trayIcon = new();
        private ContextMenuStrip _trayMenu = new();
        private ColorDialog _colorPicker = new() { FullOpen = true, Color = Color.FromArgb(0, 150, 255) };

        private readonly BacklightStateManager _backlightMgr = new();
        private bool _manualBacklightOnBattery;
        private bool _isLidClosed;
        private bool _isSleeping;
        private IntPtr _powerSettingRegistrationLid = IntPtr.Zero;
        private IntPtr _powerSettingRegistrationPower = IntPtr.Zero;
        private IntPtr _powerSettingRegistrationBattery = IntPtr.Zero;
        private readonly object _powerNotifyLock = new();
        private CancellationTokenSource? _backlightEnforceCts;
        private readonly object _backlightEnforceLock = new();
        private CancellationTokenSource? _rgbRestoreCts;
        private readonly object _rgbRestoreLock = new();

        private int _cpuTemp, _gpuTemp;
        private int _telemetryRunning;
        private DarkScrollPanel _contentPanel = null!;

        private bool? _isPluggedIn;
        private bool? _pendingPluggedIn;
        private int _powerLineStableTicks;
        private bool _allowVisible = true;
        private bool _isResyncing;
        private bool _isClosing;
        private int _maxHz;
        private float _dpiScale = 1f; 
        private int _formW;           

        private static readonly Color FormBg = Color.FromArgb(22, 22, 26);
        private static readonly Color SeparatorColor = Color.FromArgb(40, 40, 44);
        private static readonly Color HeaderColor = Color.FromArgb(120, 120, 135);
        private static readonly Color SubHeaderColor = Color.FromArgb(100, 100, 110);
        private static readonly Color AccentColor = Color.FromArgb(0, 200, 160);

        private static readonly Font FontTitle = new("Segoe UI", 9.5f, FontStyle.Bold);
        private static readonly Font FontSectionHeader = new("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Font FontHeaderLight = new("Segoe UI", 8.5f, FontStyle.Regular);
        private static readonly Font FontBody = new("Segoe UI", 9.5f, FontStyle.Regular);
        private static readonly Font FontBodyBold = new("Segoe UI", 9.5f, FontStyle.Bold);

        private Label _lblTitle = null!, _lblCpuTemp = null!, _lblGpuTemp = null!;
        private Label _lblCpuRpm = null!, _lblGpuRpm = null!;
        private Label _lblPowerStatus = null!, _lblFanStatus = null!;
        private Label _lblBrightHdr = null!, _lblSpeedHdr = null!;
        private Label _lblCpuFanSpeedHdr = null!, _lblGpuFanSpeedHdr = null!;
        private Label? _lblSysFanSpeedHdr;

        private PredatorButton _btnQuiet = null!, _btnBalanced = null!, _btnPerform = null!,
                               _btnTurbo = null!, _btnEco = null!;

        private PredatorButton _btnAutoFan = null!, _btnMaxFan = null!, _btnCustomFan = null!;
        private PredatorButton _btnFixedSpeed = null!, _btnFanCurve = null!;
        private PredatorButton? _activeCustomSubBtn;
        private PredatorButton _btn60Hz = null!, _btnMaxHz = null!;

        private PredatorDropDown _rgbDropDown = null!;
        private PredatorButton _btnColorPick = null!;

        private PredatorSlider _brightnessSlider = null!, _speedSlider = null!;
        private PredatorSlider _cpuFanSlider = null!, _gpuFanSlider = null!;
        private PredatorSlider? _sysFanSlider;

        private FanCurveForm? _fanCurveForm;
        private bool _fanCurveEnabled;
        private List<Point> _cpuCurvePoints = new() { new(30,10), new(45,15), new(55,30), new(65,50), new(72,65), new(80,80), new(88,92), new(95,100) };
        private List<Point> _gpuCurvePoints = new() { new(30,10), new(45,15), new(55,30), new(65,50), new(72,65), new(80,80), new(88,92), new(95,100) };
        private int _lastCurveCpuSpeed = -1;
        private int _lastCurveGpuSpeed = -1;
        private int _lastCurveSysSpeed = -1;
        private int _lastCurveCpuTemp = -1;
        private int _lastCurveGpuTemp = -1;

        private KeyboardHook? _keyboardHook;
        private WmiHotkeyWatcher? _wmiHotkeyWatcher;

        private PredatorSwitch _switchLcdOverdrive = null!;
        private Label _lblLcdOverdriveStatus = null!;

        private byte _currentPowerMode = 0x01;
        
        private PredatorButton? _activePowerBtn, _activeFanBtn, _activeDisplayBtn;
        private bool _isUpdatingBattery;
        private bool _isApplyingRgbMode;

        private PredatorDropDown _cboAcProfile = null!, _cboBatteryProfile = null!;
        private Label _lblAcProfileHdr = null!, _lblBatteryProfileHdr = null!;

        private PredatorDropDown _cboAcFan = null!, _cboBatteryFan = null!;
        private Label _lblAcFanHdr = null!, _lblBatteryFanHdr = null!;
        internal static readonly byte[] AcProfileValues = { 0xFF, 0x00, 0x01, 0x04, 0x05 };
        internal static readonly byte[] BatteryProfileValues = { 0xFF, 0x00, 0x01, 0x06 };
        internal static readonly byte[] FanProfileValues = { 0xFF, 0x01, 0x02, 0x03 };
        internal static readonly byte[] AcFanValues = { 0xFF, 0x01, 0x02, 0x03 };
        internal static readonly byte[] BatteryFanValues = { 0xFF, 0x01, 0x02, 0x03 };

        private PredatorSwitch _switchBatteryLimit = null!;
        private Label _lblBatteryStatus = null!;

        
        private GameSyncController _gameSync = null!;
        private PredatorToggle _switchGameSync = null!;
        private Label _lblGameSyncStatus = null!;
        private PredatorButton _btnConfigureGames = null!;
        private bool _isGameSyncOverriding;

        private PredatorToggle _switchStartWithWindows = null!;
        private bool _suppressStartupToggle;
        private Label _lblStartupStatus = null!;

        private PredatorButton _btnCheckUpdates = null!;
        private bool _updateCheckRunning;

        private DeviceCapabilities _capabilities = DeviceCapabilities.None;
        private readonly PdhLoadMonitor _pdhMonitor = new();
        private RawInputKeyWatcher? _rawInputWatcher;
        private SecureNamedPipeIpc.PipeServer? _pipeServer;
        private readonly CurveFollower _cpuCurveFollower = new();
        private readonly CurveFollower _gpuCurveFollower = new();
        private readonly CurveFollower _sysCurveFollower = new();
        private int _cpuSensorMisses;
        private DateTime _lastFirmwareReassert = DateTime.UtcNow;
        private readonly Queue<int> _cpuDampingQueue = new();
        private readonly Queue<int> _gpuDampingQueue = new();
        private bool _isDustDefenderRunning;

        private Label? _lblSysFanRpm;
        private HistoryGraphControl? _cpuHistoryGraph;
        private HistoryGraphControl? _gpuHistoryGraph;

        private PredatorSwitch? _switchCoolBoost;
        private PredatorButton? _btnDustDefender;
        private PredatorDropDown? _cboFanTable;
        private PredatorDropDown? _cboGpuMode;
        private PredatorSwitch? _switchUsbCharging;
        private PredatorDropDown? _cboUsbFloor;
        private PredatorButton? _btnBatteryCalibration;
        private PredatorDropDown? _cboBacklightTimeout;
        private PredatorDropDown? _cboTheme;
        private PredatorButton? _btnDiagnostics;

        private static readonly string[] RgbModeNames = LightingEffectsManager.EffectNames;

        private ToolStripMenuItem _trayPowerQuiet = null!, _trayPowerBal = null!, _trayPowerPerf = null!,
                                  _trayPowerTurbo = null!, _trayPowerEco = null!;
        private ToolStripMenuItem _trayFanAuto = null!, _trayFanMax = null!, _trayFanCustom = null!;
        private ToolStripMenuItem _trayDisplay60 = null!, _trayDisplayMax = null!;
        private ToolStripMenuItem _trayLcdOverdrive = null!;
        private ToolStripMenuItem _trayBatteryLimit80 = null!, _trayBatteryLimit100 = null!;
        private ToolStripMenuItem _trayBatteryMenu = null!;
        private ToolStripMenuItem _trayRgbStatic = null!, _trayRgbBreathe = null!, _trayRgbNeon = null!,
                                  _trayRgbWave = null!, _trayRgbShift = null!, _trayRgbZoom = null!,
                                  _trayRgbMeteor = null!, _trayRgbTwinkle = null!;

        #endregion

        #region DPI Scaling Helper

        private int S(int px) => (int)(px * _dpiScale);

        #endregion

        #region Constructor & Visibility

        protected override void SetVisibleCore(bool value)
        {
            if (!_allowVisible)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        public Form1()
        {
            if (Environment.CommandLine.IndexOf("-hidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Environment.CommandLine.IndexOf("--hidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Environment.CommandLine.IndexOf("/hidden", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _allowVisible = false;
            }

            InitializeComponent();
            this.DoubleBuffered = true;
            _maxHz = GetMaxRefreshRate();

            _capabilities = CapabilityProbe.Probe(_wmi, _wmi.EcHid);
            ThemeManager.LoadThemeFromRegistry();

            _dpiScale = this.DeviceDpi / 96f;

            BuildUI();
            ThemeManager.ApplyTheme(this);
            BuildTrayMenu();
            SetupSystemTray();

            try
            {
                _pipeServer = new SecureNamedPipeIpc.PipeServer(msg =>
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(ToggleWindowVisibility));
                });
                _pipeServer.Start();
            }
            catch { }

            try
            {
                _rawInputWatcher = new RawInputKeyWatcher();
                _rawInputWatcher.NitroSenseKeyPressed += () =>
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(ToggleWindowVisibility));
                };
                _rawInputWatcher.ModeKeyPressed += () =>
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(CyclePowerMode));
                };
            }
            catch { }

            if (GetCurrentRefreshRate() <= 60)
            {
                HighlightBtn(_btn60Hz, ref _activeDisplayBtn);
                CheckTrayItem(_trayDisplay60, _trayDisplay60, _trayDisplayMax);
            }
            else
            {
                HighlightBtn(_btnMaxHz, ref _activeDisplayBtn);
                CheckTrayItem(_trayDisplayMax, _trayDisplay60, _trayDisplayMax);
            }

            LoadMemory();

            _gameSync = new GameSyncController();
            _gameSync.GameDetected += OnGameDetected;
            _gameSync.GameExited += OnGameExited;

            if (_gameSync.IsEnabled)
            {
                _switchGameSync.Checked = true;
                _lblGameSyncStatus.Text = "Active \u2014 Monitoring";
            }

            var initLine = SystemInformation.PowerStatus.PowerLineStatus;
            _timer.Interval = _backlightMgr.IsOnBattery(initLine) ? 5000 : 2000;
            _timer.Tick += UpdateTelemetry;
            _timer.Start();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            this.FormClosed += (s, e) => SystemEvents.PowerModeChanged -= OnPowerModeChanged;

            this.Shown += (s, e) =>
            {
                if (_allowVisible)
                {
                    Updater.ShowPendingNotes(this);
                }

                Task.Run(() =>
                {
                    EnsureStartupTaskUpdated();
                    MigrateLegacyStartup();
                    bool enabled = IsStartupEnabled();
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _suppressStartupToggle = true;
                            _switchStartWithWindows.Checked = enabled;
                            _suppressStartupToggle = false;
                        }));
                    }
                    catch { }
                });
            };

            try
            {
                _keyboardHook = new KeyboardHook(
                    onPredatorSensePressed: () =>
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke(new Action(ToggleWindowVisibility));
                    },
                    onPredatorNumberPressed: (num) =>
                    {
                        if (!IsDisposed && IsHandleCreated)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                switch (num)
                                {
                                    case 1: ApplyPowerMode(0x06, _btnEco); break;
                                    case 2: ApplyPowerMode(0x00, _btnQuiet); break;
                                    case 3: ApplyPowerMode(0x01, _btnBalanced); break;
                                    case 4: ApplyPowerMode(0x04, _btnPerform); break;
                                    case 5: ApplyPowerMode(0x05, _btnTurbo); break;
                                }
                            }));
                        }
                    });
            }
            catch { }

            try
            {
                _wmiHotkeyWatcher = new WmiHotkeyWatcher((detail) =>
                {
                    if (detail == 5 && !IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(ToggleWindowVisibility));
                    }
                });
                _wmiHotkeyWatcher.ModeKeyPressed += () =>
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(CyclePowerMode));
                };
                _wmiHotkeyWatcher.DustDefenderEvent += (running) =>
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _isDustDefenderRunning = running;
                            if (_btnDustDefender != null)
                            {
                                _btnDustDefender.Text = running ? "🔄  DustDefender: Running..." : "🔄  DustDefender (Reverse Spin Cycle)";
                                _btnDustDefender.Enabled = !running;
                            }
                        }));
                    }
                };
            }
            catch { }
        }

        #region Updates

        private async Task CheckForUpdatesAsync()
        {
            if (_updateCheckRunning) return;
            _updateCheckRunning = true;
            _btnCheckUpdates.Enabled = false;

            try
            {
                var info = await Updater.CheckAsync();

                if (info == null)
                {
                    MessageBox.Show(this, $"You're on the latest version (v{Updater.CurrentText}).",
                        "Predator Control", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                await PromptUpdateAsync(info);
            }
            catch (Exception ex)
            {
                var dr = MessageBox.Show(this,
                    $"Could not check for updates:\n{ex.Message}\n\nWould you like to open the releases page in your browser?",
                    "Predator Control", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (dr == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(Updater.ReleasesWeb) { UseShellExecute = true });
                    }
                    catch { }
                }
            }
            finally
            {
                _updateCheckRunning = false;
                if (!_btnCheckUpdates.IsDisposed) _btnCheckUpdates.Enabled = true;
            }
        }

        private async Task PromptUpdateAsync(UpdateInfo info)
        {
            bool accepted = Updater.ShowNotes(this, "Update available",
                $"Version {info.Version.ToString(3)} is available — you have v{Updater.CurrentText}",
                info.Notes, confirm: true);

            if (!accepted) return;

            try
            {
                _btnCheckUpdates.Enabled = false;
                _btnCheckUpdates.Text = "Downloading update…";
                await Updater.ApplyAsync(info);

                if (info.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    _isClosing = true;
                    Application.Exit();
                }
                else
                {
                    _btnCheckUpdates.Enabled = true;
                    _btnCheckUpdates.Text = $"⬇  Check for Updates  (v{Updater.CurrentText})";
                }
            }
            catch (Exception ex)
            {
                _btnCheckUpdates.Enabled = true;
                _btnCheckUpdates.Text = $"⬇  Check for Updates  (v{Updater.CurrentText})";
                MessageBox.Show(this, $"Update failed:\n{ex.Message}",
                    "Predator Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region Power Notifications & Hardware Lifecycle

        private void RegisterPowerNotifications()
        {
            lock (_powerNotifyLock)
            {
                if (IsHandleCreated)
                {
                    Guid lidGuid = GUID_LIDSWITCH_STATE_CHANGE;
                    Guid powerGuid = GUID_ACDC_POWER_SOURCE;
                    Guid batteryGuid = GUID_BATTERY_PERCENTAGE_REMAINING;

                    if (_powerSettingRegistrationLid == IntPtr.Zero)
                        _powerSettingRegistrationLid = RegisterPowerSettingNotification(this.Handle, ref lidGuid, DEVICE_NOTIFY_WINDOW_HANDLE);

                    if (_powerSettingRegistrationPower == IntPtr.Zero)
                        _powerSettingRegistrationPower = RegisterPowerSettingNotification(this.Handle, ref powerGuid, DEVICE_NOTIFY_WINDOW_HANDLE);

                    if (_powerSettingRegistrationBattery == IntPtr.Zero)
                        _powerSettingRegistrationBattery = RegisterPowerSettingNotification(this.Handle, ref batteryGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
                }
            }
        }

        private void UnregisterPowerNotifications()
        {
            lock (_powerNotifyLock)
            {
                if (_powerSettingRegistrationLid != IntPtr.Zero)
                {
                    UnregisterPowerSettingNotification(_powerSettingRegistrationLid);
                    _powerSettingRegistrationLid = IntPtr.Zero;
                }
                if (_powerSettingRegistrationPower != IntPtr.Zero)
                {
                    UnregisterPowerSettingNotification(_powerSettingRegistrationPower);
                    _powerSettingRegistrationPower = IntPtr.Zero;
                }
                if (_powerSettingRegistrationBattery != IntPtr.Zero)
                {
                    UnregisterPowerSettingNotification(_powerSettingRegistrationBattery);
                    _powerSettingRegistrationBattery = IntPtr.Zero;
                }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterPowerNotifications();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterPowerNotifications();
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST)
            {
                HandlePowerBroadcast(ref m);
            }
            else if (m.Msg == (int)WM_SHOWME)
            {
                ShowApp();
            }

            base.WndProc(ref m);
        }

        private bool IsOnBattery()
        {
            return _backlightMgr.IsOnBattery(SystemInformation.PowerStatus.PowerLineStatus);
        }

        private void HandlePowerBroadcast(ref Message m)
        {
            int wParam = m.WParam.ToInt32();
            switch (wParam)
            {
                case PBT_APMPOWERSTATUSCHANGE:
                    BeginInvoke(new Action(() =>
                    {
                        var lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
                        bool onBattery = _backlightMgr.IsOnBattery(lineStatus);
                        bool isAc = !onBattery;
                        if (_isPluggedIn != isAc)
                        {
                            _isPluggedIn = isAc;
                            try { ApplyPowerRules(isAc); } catch { }
                        }
                        else
                        {
                            try { ApplyFanRules(isAc); } catch { }
                        }

                        if (onBattery && !_manualBacklightOnBattery)
                        {
                            EnforceBacklightOffOnBattery();
                        }
                        else if (!onBattery && _backlightMgr.SavedAcBrightness > 0)
                        {
                            AcerServiceClient.ResetConnectionState();
                            lock (_backlightEnforceLock)
                            {
                                try { _backlightEnforceCts?.Cancel(); } catch { }
                                try { _backlightEnforceCts?.Dispose(); } catch { }
                                _backlightEnforceCts = null;
                            }
                            int targetBright = _backlightMgr.SavedAcBrightness;
                            if (_brightnessSlider != null && _brightnessSlider.Value == 0)
                            {
                                _brightnessSlider.Value = targetBright;
                                if (_lblBrightHdr != null)
                                    _lblBrightHdr.Text = $"BRIGHTNESS: {targetBright}%";
                            }
                            Task.Run(() =>
                            {
                                _wmi.SetBrightness((byte)targetBright);
                                if (_rgbDropDown != null)
                                    _wmi.ApplyLightingMode(_rgbDropDown.SelectedIndex);
                            });
                        }
                        ScheduleRgbRestoreWatchdog();
                    }));
                    break;

                case PBT_APMSUSPEND:
                    _isSleeping = true;
                    _backlightMgr.OnSuspend(out bool shouldOffSuspend, out _);
                    if (shouldOffSuspend)
                    {
                        _manualBacklightOnBattery = false;
                        Task.Run(() => _wmi.TurnOffBacklight());
                    }
                    break;

                case PBT_APMRESUMEAUTOMATIC:
                case PBT_APMRESUMESUSPEND:
                    _isSleeping = false;
                    BeginInvoke(new Action(() =>
                    {
                        var lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
                        _backlightMgr.OnResume(lineStatus, out bool shouldTurnOffResume, out int targetBright);
                        bool isAc = !shouldTurnOffResume && lineStatus == PowerLineStatus.Online;
                        _isPluggedIn = isAc;
                        try { ApplyPowerRules(isAc); } catch { }

                        if (shouldTurnOffResume)
                        {
                            _manualBacklightOnBattery = false;
                            if (_brightnessSlider != null)
                            {
                                _brightnessSlider.Value = 0;
                                if (_lblBrightHdr != null)
                                    _lblBrightHdr.Text = "BRIGHTNESS: 0% (Off on Battery)";
                            }
                            EnforceBacklightOffOnBattery();
                        }
                        else
                        {
                            lock (_backlightEnforceLock)
                            {
                                try { _backlightEnforceCts?.Cancel(); } catch { }
                                try { _backlightEnforceCts?.Dispose(); } catch { }
                                _backlightEnforceCts = null;
                            }
                            if (_brightnessSlider != null)
                            {
                                _brightnessSlider.Value = targetBright;
                                if (_lblBrightHdr != null)
                                    _lblBrightHdr.Text = $"BRIGHTNESS: {targetBright}%";
                            }
                            Task.Run(() =>
                            {
                                _wmi.SetBrightness((byte)targetBright);
                                if (_rgbDropDown != null)
                                    _wmi.ApplyLightingMode(_rgbDropDown.SelectedIndex);
                            });
                        }
                        ScheduleRgbRestoreWatchdog();
                    }));
                    break;

                case PBT_POWERSETTINGCHANGE:
                    if (m.LParam != IntPtr.Zero)
                    {
                        try
                        {
                            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                            if (setting.PowerSetting == GUID_LIDSWITCH_STATE_CHANGE && setting.DataLength >= 1)
                            {
                                byte lidData = Marshal.ReadByte(m.LParam, Marshal.OffsetOf<POWERBROADCAST_SETTING>(nameof(POWERBROADCAST_SETTING.Data)).ToInt32());
                                bool isOpen = lidData != 0;
                                OnLidStateChanged(isOpen);
                            }
                            else if (setting.PowerSetting == GUID_ACDC_POWER_SOURCE && setting.DataLength >= 1)
                            {
                                int offset = Marshal.OffsetOf<POWERBROADCAST_SETTING>(nameof(POWERBROADCAST_SETTING.Data)).ToInt32();
                                int acline = setting.DataLength >= 4
                                    ? Marshal.ReadInt32(m.LParam, offset)
                                    : Marshal.ReadByte(m.LParam, offset);
                                bool isAc = acline == 0;
                                OnPowerSourceChangedImmediate(isAc);
                            }
                        }
                        catch { }
                    }
                    break;
            }
        }

        private void OnLidStateChanged(bool isOpen)
        {
            _isLidClosed = !isOpen;
            var lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
            _backlightMgr.OnLidChanged(isOpen, lineStatus, out bool shouldTurnOff, out int targetBright);

            BeginInvoke(new Action(() =>
            {
                if (_isClosing || IsDisposed) return;

                if (shouldTurnOff)
                {
                    _manualBacklightOnBattery = false;
                    if (_brightnessSlider != null)
                    {
                        _brightnessSlider.Value = 0;
                        if (_lblBrightHdr != null)
                            _lblBrightHdr.Text = "BRIGHTNESS: 0% (Off on Battery)";
                    }
                    EnforceBacklightOffOnBattery();
                }
                else
                {
                    lock (_backlightEnforceLock)
                    {
                        try { _backlightEnforceCts?.Cancel(); } catch { }
                        try { _backlightEnforceCts?.Dispose(); } catch { }
                        _backlightEnforceCts = null;
                    }
                    if (_brightnessSlider != null)
                    {
                        _brightnessSlider.Value = targetBright;
                        if (_lblBrightHdr != null)
                            _lblBrightHdr.Text = $"BRIGHTNESS: {targetBright}%";
                    }
                    Task.Run(() =>
                    {
                        _wmi.SetBrightness((byte)targetBright);
                        if (_rgbDropDown != null)
                            _wmi.ApplyLightingMode(_rgbDropDown.SelectedIndex);
                    });
                }
            }));
        }

        private void OnPowerSourceChangedImmediate(bool isAc)
        {
            _backlightMgr.OnPowerSourceChanged(isAc, out bool shouldTurnOff, out int targetBright);

            BeginInvoke(new Action(() =>
            {
                if (_isClosing || IsDisposed) return;

                if (_isPluggedIn != isAc)
                {
                    _isPluggedIn = isAc;
                    try { ApplyPowerRules(isAc); } catch { }
                }
                else
                {
                    try { ApplyFanRules(isAc); } catch { }
                }

                if (isAc)
                {
                    AcerServiceClient.ResetConnectionState();
                    _manualBacklightOnBattery = false;
                    lock (_backlightEnforceLock)
                    {
                        try { _backlightEnforceCts?.Cancel(); } catch { }
                        try { _backlightEnforceCts?.Dispose(); } catch { }
                        _backlightEnforceCts = null;
                    }
                }

                if (shouldTurnOff)
                {
                    _manualBacklightOnBattery = false;
                    if (_brightnessSlider != null)
                    {
                        _brightnessSlider.Value = 0;
                        if (_lblBrightHdr != null)
                            _lblBrightHdr.Text = "BRIGHTNESS: 0% (Off on Battery)";
                    }
                    EnforceBacklightOffOnBattery();
                }
                else
                {
                    lock (_backlightEnforceLock)
                    {
                        try { _backlightEnforceCts?.Cancel(); } catch { }
                        try { _backlightEnforceCts?.Dispose(); } catch { }
                        _backlightEnforceCts = null;
                    }
                    if (_brightnessSlider != null)
                    {
                        _brightnessSlider.Value = targetBright;
                        if (_lblBrightHdr != null)
                            _lblBrightHdr.Text = $"BRIGHTNESS: {targetBright}%";
                    }
                    Task.Run(() =>
                    {
                        _wmi.SetBrightness((byte)targetBright);
                        if (_rgbDropDown != null)
                            _wmi.ApplyLightingMode(_rgbDropDown.SelectedIndex);
                    });
                }

                ScheduleRgbRestoreWatchdog();
            }));
        }

        private void EnforceBacklightOffOnBattery()
        {
            if (_isClosing || IsDisposed) return;

            var currentStatus = SystemInformation.PowerStatus.PowerLineStatus;
            if (currentStatus == PowerLineStatus.Online)
                return;

            CancellationToken token;
            lock (_backlightEnforceLock)
            {
                try { _backlightEnforceCts?.Cancel(); } catch { }
                try { _backlightEnforceCts?.Dispose(); } catch { }
                _backlightEnforceCts = new CancellationTokenSource();
                token = _backlightEnforceCts.Token;
            }

            Task.Run(async () =>
            {
                try
                {
                    if (token.IsCancellationRequested || _isClosing || IsDisposed) return;
                    var st = SystemInformation.PowerStatus.PowerLineStatus;
                    if (st == PowerLineStatus.Online) return;

                    _wmi.TurnOffBacklight();

                    int[] delays = { 150, 400, 800, 1500, 2500, 4000, 6000 };
                    foreach (int ms in delays)
                    {
                        await Task.Delay(ms, token);
                        if (_isClosing || IsDisposed || token.IsCancellationRequested) break;

                        var status = SystemInformation.PowerStatus.PowerLineStatus;
                        if (status == PowerLineStatus.Online) break;

                        if (_isLidClosed || _isSleeping || (_backlightMgr.IsOnBattery(status) && !_manualBacklightOnBattery))
                        {
                            _wmi.TurnOffBacklight();
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }, token);
        }

        private void ScheduleRgbRestoreWatchdog(int delayMs = 650)
        {
            if (_isClosing || IsDisposed) return;

            lock (_rgbRestoreLock)
            {
                try { _rgbRestoreCts?.Cancel(); } catch { }
                try { _rgbRestoreCts?.Dispose(); } catch { }
                _rgbRestoreCts = new CancellationTokenSource();
                var token = _rgbRestoreCts.Token;

                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delayMs, token);
                        if (token.IsCancellationRequested || _isClosing || IsDisposed) return;

                        if (IsHandleCreated && !IsDisposed)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (_isClosing || IsDisposed) return;
                                RestoreUserLightingState();
                            }));
                        }

                        // Secondary pulse at delayMs + 600ms to catch slow EC firmware transitions
                        await Task.Delay(600, token);
                        if (token.IsCancellationRequested || _isClosing || IsDisposed) return;

                        if (IsHandleCreated && !IsDisposed)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (_isClosing || IsDisposed) return;
                                RestoreUserLightingState();
                            }));
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Program.Report(ex, false); }
                }, token);
            }
        }

        private void RestoreUserLightingState()
        {
            if (_isClosing || IsDisposed) return;

            var lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
            bool onBattery = _backlightMgr.IsOnBattery(lineStatus);
            bool shouldBeOff = _isLidClosed || _isSleeping || (onBattery && !_manualBacklightOnBattery);

            if (shouldBeOff)
            {
                if (_brightnessSlider != null && _brightnessSlider.Value != 0)
                {
                    _brightnessSlider.Value = 0;
                    if (_lblBrightHdr != null)
                        _lblBrightHdr.Text = "BRIGHTNESS: 0% (Off on Battery)";
                }
                EnforceBacklightOffOnBattery();
            }
            else
            {
                int targetBright = onBattery
                    ? (_brightnessSlider?.Value ?? _backlightMgr.SavedAcBrightness)
                    : (_backlightMgr.SavedAcBrightness > 0 ? _backlightMgr.SavedAcBrightness : (_brightnessSlider?.Value ?? 100));

                if (targetBright <= 0 && !onBattery) targetBright = 100;

                if (_brightnessSlider != null && _brightnessSlider.Value != targetBright)
                {
                    _brightnessSlider.Value = targetBright;
                    if (_lblBrightHdr != null)
                        _lblBrightHdr.Text = $"BRIGHTNESS: {targetBright}%";
                }

                int selectedMode = _rgbDropDown != null && _rgbDropDown.SelectedIndex >= 0
                    ? _rgbDropDown.SelectedIndex
                    : (_wmi.LastRgbMode >= 0 ? _wmi.LastRgbMode : 0);
                selectedMode = Math.Clamp(selectedMode, 0, 33);
                byte speed = GetMappedSpeed();
                Color color = (_colorPicker != null && _colorPicker.Color != Color.Empty && _colorPicker.Color != Color.Black && _colorPicker.Color.A != 0)
                    ? _colorPicker.Color
                    : (_wmi.LastR != 0 || _wmi.LastG != 0 || _wmi.LastB != 0
                        ? Color.FromArgb(_wmi.LastR, _wmi.LastG, _wmi.LastB)
                        : Color.FromArgb(0, 150, 255));
                byte direction = _wmi.Direction;

                Task.Run(() =>
                {
                    LightingEffectsManager.ApplyEffect(selectedMode, _wmi, color.R, color.G, color.B, (byte)targetBright, speed, direction, (OperatingMode)_currentPowerMode);
                });
            }
        }

        #endregion

        #endregion

        #region Display Control
 
        internal static int GetCurrentRefreshRateCore()
        {
            DEVMODE dm = new(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 60;
        }

        internal static int GetMaxRefreshRateCore()
        {
            DEVMODE cur = new(); cur.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref cur)) return 60;

            int maxHz = cur.dmDisplayFrequency > 0 ? cur.dmDisplayFrequency : 60;
            DEVMODE dm = new(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            for (int modeNum = 0; EnumDisplaySettings(null, modeNum, ref dm); modeNum++)
            {
                if (IsSameGeometry(dm, cur) && dm.dmDisplayFrequency > maxHz)
                    maxHz = dm.dmDisplayFrequency;
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            }
            return maxHz;
        }

        internal static bool IsSameGeometry(DEVMODE a, DEVMODE b) =>
            a.dmPelsWidth == b.dmPelsWidth &&
            a.dmPelsHeight == b.dmPelsHeight &&
            a.dmBitsPerPel == b.dmBitsPerPel &&
            (a.dmDisplayFlags & DM_INTERLACED) == 0;

        internal static bool SetRefreshRateCore(int hz)
        {
            if (hz <= 0) return false;

            DEVMODE cur = new(); cur.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref cur)) return false;
            if (cur.dmDisplayFrequency == hz) return true;

            DEVMODE dm = new(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            DEVMODE? match = null;
            for (int modeNum = 0; EnumDisplaySettings(null, modeNum, ref dm); modeNum++)
            {
                if (IsSameGeometry(dm, cur) && dm.dmDisplayFrequency == hz) { match = dm; break; }
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            }
            if (match == null) return false;

            DEVMODE target = match.Value;
            target.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            target.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

            if (ChangeDisplaySettingsEx(null, ref target, IntPtr.Zero, CDS_TEST, IntPtr.Zero) != DISP_CHANGE_SUCCESSFUL) return false;
            return ChangeDisplaySettingsEx(null, ref target, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
        }

        private int GetCurrentRefreshRate() => GetCurrentRefreshRateCore();
        private int GetMaxRefreshRate() => GetMaxRefreshRateCore();
        private bool SetRefreshRate(int hz) => SetRefreshRateCore(hz);

        #endregion

        #region System Tray

        private void SetupSystemTray()
        {
            try { _trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { _trayIcon.Icon = SystemIcons.Application; }

            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.Text = "Predator Control";
            try { _trayIcon.Visible = true; } catch { }
            _trayIcon.DoubleClick += (s, e) => ShowApp();
        }

        private void BuildTrayMenu()
        {
            _trayMenu = new ContextMenuStrip();

            var powerMenu = new ToolStripMenuItem("  Power Mode"); 
            _trayPowerQuiet = new ToolStripMenuItem("Quiet", null, (s, e) => ApplyPowerMode(0x00, _btnQuiet));
            _trayPowerBal = new ToolStripMenuItem("Balanced", null, (s, e) => ApplyPowerMode(0x01, _btnBalanced));
            _trayPowerPerf = new ToolStripMenuItem("Performance", null, (s, e) => ApplyPowerMode(0x04, _btnPerform));
            _trayPowerTurbo = new ToolStripMenuItem("Turbo", null, (s, e) => ApplyPowerMode(0x05, _btnTurbo));
            _trayPowerEco = new ToolStripMenuItem("Eco", null, (s, e) => ApplyPowerMode(0x06, _btnEco));
            powerMenu.DropDownItems.AddRange([_trayPowerQuiet, _trayPowerBal, _trayPowerPerf, _trayPowerTurbo, _trayPowerEco]);

            var fanMenu = new ToolStripMenuItem("  Fan Mode");
            _trayFanAuto = new ToolStripMenuItem("Auto", null, (s, e) => ApplyFanMode(0x01, _btnAutoFan));
            _trayFanMax = new ToolStripMenuItem("Max", null, (s, e) => ApplyFanMode(0x02, _btnMaxFan));
            _trayFanCustom = new ToolStripMenuItem("Custom", null, (s, e) => ApplyFanMode(0x03, _btnCustomFan));
            fanMenu.DropDownItems.AddRange([_trayFanAuto, _trayFanMax, _trayFanCustom]);

            var displayMenu = new ToolStripMenuItem("  Display");
            _trayDisplay60 = new ToolStripMenuItem("60 Hz", null, (s, e) => ApplyDisplayMode(60, _btn60Hz));
            _trayDisplayMax = new ToolStripMenuItem($"{_maxHz} Hz", null, (s, e) => ApplyDisplayMode(_maxHz, _btnMaxHz));
            _trayLcdOverdrive = new ToolStripMenuItem("LCD Overdrive (3ms)", null, (s, e) => ApplyLcdOverdrive(!_switchLcdOverdrive.Checked)) { CheckOnClick = true };
            displayMenu.DropDownItems.AddRange([_trayDisplay60, _trayDisplayMax, new ToolStripSeparator(), _trayLcdOverdrive]);

            var rgbMenu = new ToolStripMenuItem("  Keyboard RGB");
            _trayRgbStatic = new ToolStripMenuItem("Static", null, (s, e) => ApplyRgbModeFromDropdown(0));
            _trayRgbBreathe = new ToolStripMenuItem("Breathing", null, (s, e) => ApplyRgbModeFromDropdown(1));
            _trayRgbNeon = new ToolStripMenuItem("Neon", null, (s, e) => ApplyRgbModeFromDropdown(2));
            _trayRgbWave = new ToolStripMenuItem("Wave", null, (s, e) => ApplyRgbModeFromDropdown(3));
            _trayRgbShift = new ToolStripMenuItem("Shifting", null, (s, e) => ApplyRgbModeFromDropdown(4));
            _trayRgbZoom = new ToolStripMenuItem("Zoom", null, (s, e) => ApplyRgbModeFromDropdown(5));
            _trayRgbMeteor = new ToolStripMenuItem("Meteor", null, (s, e) => ApplyRgbModeFromDropdown(6));
            _trayRgbTwinkle = new ToolStripMenuItem("Twinkling", null, (s, e) => ApplyRgbModeFromDropdown(7));
            rgbMenu.DropDownItems.AddRange([_trayRgbStatic, _trayRgbBreathe, _trayRgbNeon, _trayRgbWave,
                                            _trayRgbShift, _trayRgbZoom, _trayRgbMeteor, _trayRgbTwinkle]);

            _trayBatteryMenu = new ToolStripMenuItem("  Battery Limit");
            _trayBatteryLimit80 = new ToolStripMenuItem("Limit to 80%", null, (s, e) => ApplyBatteryLimit(true));
            _trayBatteryLimit100 = new ToolStripMenuItem("Full Charge (100%)", null, (s, e) => ApplyBatteryLimit(false));
            _trayBatteryMenu.DropDownItems.AddRange([_trayBatteryLimit80, _trayBatteryLimit100]);

            _trayMenu.Items.Add(powerMenu);
            _trayMenu.Items.Add(fanMenu);
            _trayMenu.Items.Add(displayMenu);
            _trayMenu.Items.Add(_trayBatteryMenu);
            _trayMenu.Items.Add(rgbMenu);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Open Dashboard", null, (s, e) => ShowApp());
            _trayMenu.Items.Add("Exit", null, (s, e) => { _isClosing = true; Application.Exit(); });
        }

        #endregion

        #region UI Building

        private void BuildUI()
        {
            this.Controls.Clear();
            this.BackColor = FormBg;
            this.ForeColor = Color.White;

            _formW = S(450);
            int workH = Screen.PrimaryScreen?.WorkingArea.Height ?? S(1000);
            this.ClientSize = new Size(_formW, Math.Max(S(400), Math.Min(S(960), workH - 40)));
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int pad = S(24);
            int contentW = _formW - pad * 2;
            int gap = S(6);
            int btnH = S(34);
            int y = 0;

            var pnlTitle = new Panel { Height = S(40), Width = _formW, BackColor = Color.FromArgb(18, 18, 21) };
            pnlTitle.MouseDown += TitleBar_MouseDown;
            this.Controls.Add(pnlTitle);
            var picIcon = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(S(16), S(16)), Location = new Point(pad - S(4), S(12)), BackColor = Color.Transparent };
            try { var extIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); if (extIcon != null) picIcon.Image = extIcon.ToBitmap(); } catch { }
            picIcon.MouseDown += TitleBar_MouseDown;
            pnlTitle.Controls.Add(picIcon);

            _lblTitle = new Label { Text = "Predator Control", ForeColor = Color.White, Font = FontTitle, AutoSize = true, Location = new Point(pad + S(20), S(11)), BackColor = Color.Transparent };
            _lblTitle.MouseDown += TitleBar_MouseDown;
            pnlTitle.Controls.Add(_lblTitle);

            var lblClose = new Label { Text = "●", ForeColor = Color.FromArgb(255, 95, 86), Font = new Font("Arial", 12f), AutoSize = true, Location = new Point(_formW - pad - S(4), S(9)), Cursor = Cursors.Hand, BackColor = Color.Transparent };
            var lblMin = new Label { Text = "●", ForeColor = Color.FromArgb(255, 189, 46), Font = new Font("Arial", 12f), AutoSize = true, Location = new Point(lblClose.Left - S(20), S(9)), Cursor = Cursors.Hand, BackColor = Color.Transparent };
            
            lblClose.Click += (s, e) => { this.Close(); };
            lblMin.Click += (s, e) => { this.WindowState = FormWindowState.Minimized; };
            
            pnlTitle.Controls.Add(lblClose);
            pnlTitle.Controls.Add(lblMin);

            y = pnlTitle.Bottom;

            _contentPanel = new DarkScrollPanel
            {
                Location = new Point(0, y),
                Size = new Size(_formW + DarkScrollPanel.NativeBarWidth, this.ClientSize.Height - y),
                BackColor = FormBg
            };
            _contentPanel.SetDpiScale(_dpiScale);
            this.Controls.Add(_contentPanel);
            _contentPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;

            y = S(24); 

            MakeLabel("CPU:", pad, y, FontBody, SubHeaderColor);
            _lblCpuTemp = MakeLabel("43°C", pad + S(34), y, FontBodyBold, Color.White);

            MakeLabel("GPU:", _formW / 2 + S(10), y, FontBody, SubHeaderColor);
            _lblGpuTemp = MakeLabel("39°C", _formW / 2 + S(46), y, FontBodyBold, Color.White);

            y += S(24);
            MakeLabel("CPU FAN:", pad, y, FontBody, SubHeaderColor);
            _lblCpuRpm = MakeLabel("-- RPM", pad + S(64), y, FontBodyBold, Color.White);

            MakeLabel("GPU FAN:", _formW / 2 + S(10), y, FontBody, SubHeaderColor);
            _lblGpuRpm = MakeLabel("-- RPM", _formW / 2 + S(74), y, FontBodyBold, Color.White);

            y += S(24);
            MakeLabel("Fan speed:", pad, y, FontBody, SubHeaderColor);
            _lblFanStatus = MakeLabel("Auto", pad + S(74), y, FontBodyBold, Color.White);

            MakeLabel("Power:", _formW / 2 + S(10), y, FontBody, SubHeaderColor);
            _lblPowerStatus = MakeLabel("Plugged In", _formW / 2 + S(56), y, FontBodyBold, Color.White);

            if (_capabilities.HasThirdFan)
            {
                y += S(24);
                MakeLabel("SYS FAN:", pad, y, FontBody, SubHeaderColor);
                _lblSysFanRpm = MakeLabel("-- RPM", pad + S(64), y, FontBodyBold, Color.White);
            }

            y += S(24);
            int graphH = S(55);
            int graphW = (contentW - gap) / 2;
            _cpuHistoryGraph = new HistoryGraphControl
            {
                Location = new Point(pad, y),
                Size = new Size(graphW, graphH),
                Title = "CPU (Temp & Load)",
                Unit = "°C",
                Minimum = 20,
                Maximum = 100
            };
            _gpuHistoryGraph = new HistoryGraphControl
            {
                Location = new Point(pad + graphW + gap, y),
                Size = new Size(graphW, graphH),
                Title = "GPU (Temp & Load)",
                Unit = "°C",
                Minimum = 20,
                Maximum = 100
            };
            _contentPanel.Controls.Add(_cpuHistoryGraph);
            _contentPanel.Controls.Add(_gpuHistoryGraph);
            y += graphH;

            y += S(30);
            AddSeparator(y);

            y += S(20);
            MakeSectionHeader("POWER MODE", pad, y);
            
            y += S(24);
            int btnW = (contentW - 4 * gap) / 5;
            _btnQuiet = MakeButton("Quiet", pad, y, btnW, btnH);
            _btnBalanced = MakeButton("Balanced", pad + (btnW + gap), y, btnW, btnH);
            _btnPerform = MakeButton("Perf", pad + (btnW + gap) * 2, y, btnW, btnH);
            _btnTurbo = MakeButton("Turbo", pad + (btnW + gap) * 3, y, btnW, btnH);
            _btnEco = MakeButton("Eco", pad + (btnW + gap) * 4, y, btnW, btnH);
            
            _btnQuiet.Click += (s, e) => ApplyPowerMode(0x00, _btnQuiet);
            _btnBalanced.Click += (s, e) => ApplyPowerMode(0x01, _btnBalanced);
            _btnPerform.Click += (s, e) => ApplyPowerMode(0x04, _btnPerform);
            _btnTurbo.Click += (s, e) => ApplyPowerMode(0x05, _btnTurbo);
            _btnEco.Click += (s, e) => ApplyPowerMode(0x06, _btnEco);

            y += btnH + S(14);
            int profileDropW = (contentW - gap) / 2;
            _lblAcProfileHdr = MakeLabel("ON AC POWER:", pad, y, FontSectionHeader, SubHeaderColor);
            _lblBatteryProfileHdr = MakeLabel("ON BATTERY:", pad + profileDropW + gap, y, FontSectionHeader, SubHeaderColor);

            y += S(20);
            _cboAcProfile = new PredatorDropDown { Location = new Point(pad, y), Size = new Size(profileDropW, S(30)) };
            _cboAcProfile.Items.AddRange(new[] { "Don't Change", "Quiet", "Balanced", "Perf", "Turbo" });
            _cboAcProfile.SelectedIndex = 0;
            _contentPanel.Controls.Add(_cboAcProfile);

            _cboBatteryProfile = new PredatorDropDown { Location = new Point(pad + profileDropW + gap, y), Size = new Size(profileDropW, S(30)) };
            _cboBatteryProfile.Items.AddRange(new[] { "Don't Change", "Quiet", "Balanced", "Eco" });
            _cboBatteryProfile.SelectedIndex = 0;
            _contentPanel.Controls.Add(_cboBatteryProfile);

            _cboAcProfile.SelectedIndexChanged += (s, e) =>
            {
                int idx = _cboAcProfile.SelectedIndex;
                SaveState("AutoPowerAC", idx);
                SaveStateToHklm("AutoPowerAC", idx);
                if (idx > 0 && idx < AcProfileValues.Length)
                {
                    byte mode = AcProfileValues[idx];
                    SaveState("Power_AC", mode);
                    SaveStateToHklm("Power_AC", mode);
                }
                if (_isPluggedIn == true) ApplyPowerRules(true);
                Task.Run(() => EnsureBootPowerTaskRegistered());
            };
            _cboBatteryProfile.SelectedIndexChanged += (s, e) =>
            {
                int idx = _cboBatteryProfile.SelectedIndex;
                SaveState("AutoPowerBattery", idx);
                SaveStateToHklm("AutoPowerBattery", idx);
                if (idx > 0 && idx < BatteryProfileValues.Length)
                {
                    byte mode = BatteryProfileValues[idx];
                    SaveState("Power_Battery", mode);
                    SaveStateToHklm("Power_Battery", mode);
                }
                if (_isPluggedIn == false) ApplyPowerRules(false);
                Task.Run(() => EnsureBootPowerTaskRegistered());
            };

            y += S(30) + S(20);
            MakeSectionHeader("FAN CONTROL", pad, y);
            
            y += S(24);
            int fanBtnW = (contentW - 2 * gap) / 3;
            _btnAutoFan = MakeButton("Auto", pad, y, fanBtnW, btnH);
            _btnMaxFan = MakeButton("Max", pad + (fanBtnW + gap), y, fanBtnW, btnH);
            _btnCustomFan = MakeButton("Custom", pad + (fanBtnW + gap) * 2, y, fanBtnW, btnH);

            _btnAutoFan.Click += (s, e) => ApplyFanMode(0x01, _btnAutoFan);
            _btnMaxFan.Click += (s, e) => ApplyFanMode(0x02, _btnMaxFan);
            _btnCustomFan.Click += (s, e) => ApplyFanMode(0x03, _btnCustomFan);

            y += btnH + S(14);
            _lblAcFanHdr = MakeLabel("ON AC POWER:", pad, y, FontSectionHeader, SubHeaderColor);
            _lblBatteryFanHdr = MakeLabel("ON BATTERY:", pad + profileDropW + gap, y, FontSectionHeader, SubHeaderColor);

            y += S(20);
            _cboAcFan = new PredatorDropDown { Location = new Point(pad, y), Size = new Size(profileDropW, S(30)) };
            _cboAcFan.Items.AddRange(new[] { "Don't Change", "Auto", "Max", "Custom" });
            _cboAcFan.SelectedIndex = 0;
            _contentPanel.Controls.Add(_cboAcFan);

            _cboBatteryFan = new PredatorDropDown { Location = new Point(pad + profileDropW + gap, y), Size = new Size(profileDropW, S(30)) };
            _cboBatteryFan.Items.AddRange(new[] { "Don't Change", "Auto", "Max", "Custom" });
            _cboBatteryFan.SelectedIndex = 0;
            _contentPanel.Controls.Add(_cboBatteryFan);

            _cboAcFan.SelectedIndexChanged += (s, e) =>
            {
                int idx = _cboAcFan.SelectedIndex;
                SaveState("AutoFanAC", idx);
                SaveStateToHklm("AutoFanAC", idx);
                if (idx > 0 && idx < AcFanValues.Length)
                {
                    byte mode = AcFanValues[idx];
                    SaveState("Fan_AC", mode);
                    SaveStateToHklm("Fan_AC", mode);
                }
                if (_isPluggedIn == true) ApplyFanRules(true);
                Task.Run(() => EnsureBootPowerTaskRegistered());
            };
            _cboBatteryFan.SelectedIndexChanged += (s, e) =>
            {
                int idx = _cboBatteryFan.SelectedIndex;
                SaveState("AutoFanBattery", idx);
                SaveStateToHklm("AutoFanBattery", idx);
                if (idx > 0 && idx < BatteryFanValues.Length)
                {
                    byte mode = BatteryFanValues[idx];
                    SaveState("Fan_Battery", mode);
                    SaveStateToHklm("Fan_Battery", mode);
                }
                if (_isPluggedIn == false) ApplyFanRules(false);
                Task.Run(() => EnsureBootPowerTaskRegistered());
            };

            y += S(30) + S(12);
            int fanCount = _capabilities.HasThirdFan ? 3 : 2;
            int fanSliderW = (contentW - gap * (fanCount - 1)) / fanCount;
            _lblCpuFanSpeedHdr = MakeLabel("CPU FAN: 50%", pad, y, FontSectionHeader, SubHeaderColor);
            _lblGpuFanSpeedHdr = MakeLabel("GPU FAN: 50%", pad + fanSliderW + gap, y, FontSectionHeader, SubHeaderColor);
            _lblCpuFanSpeedHdr.Visible = false;
            _lblGpuFanSpeedHdr.Visible = false;
            if (_capabilities.HasThirdFan)
            {
                _lblSysFanSpeedHdr = MakeLabel("SYS FAN: 50%", pad + (fanSliderW + gap) * 2, y, FontSectionHeader, SubHeaderColor);
                _lblSysFanSpeedHdr.Visible = false;
            }

            y += S(24);
            _cpuFanSlider = new PredatorSlider
            {
                Location = new Point(pad, y),
                Size     = new Size(fanSliderW, S(28)),
                Minimum  = 10, Maximum = 100, Value = 50,
                Visible  = false
            };
            _gpuFanSlider = new PredatorSlider
            {
                Location = new Point(pad + fanSliderW + gap, y),
                Size     = new Size(fanSliderW, S(28)),
                Minimum  = 10, Maximum = 100, Value = 50,
                Visible  = false
            };
            _contentPanel.Controls.Add(_cpuFanSlider);
            _contentPanel.Controls.Add(_gpuFanSlider);

            if (_capabilities.HasThirdFan)
            {
                _sysFanSlider = new PredatorSlider
                {
                    Location = new Point(pad + (fanSliderW + gap) * 2, y),
                    Size     = new Size(fanSliderW, S(28)),
                    Minimum  = 10, Maximum = 100, Value = 50,
                    Visible  = false
                };
                _contentPanel.Controls.Add(_sysFanSlider);

                _sysFanSlider.ValueChanged += (s, e) =>
                {
                    if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {_sysFanSlider.Value}%";
                };

                _sysFanSlider.ValueCommitted += (s, e) =>
                {
                    int val = _sysFanSlider.Value;
                    Task.Run(() => _wmi.SetSystemFanSpeed((byte)val));
                    SaveState("FanSpeedSys", val);
                    SaveFanSpeedForProfile("Sys", val);
                };
            }

            _cpuFanSlider.ValueChanged   += (s, e) => _lblCpuFanSpeedHdr.Text = $"CPU FAN: {_cpuFanSlider.Value}%";
            _gpuFanSlider.ValueChanged   += (s, e) => _lblGpuFanSpeedHdr.Text = $"GPU FAN: {_gpuFanSlider.Value}%";

            _cpuFanSlider.ValueCommitted += (s, e) =>
            {
                int val = _cpuFanSlider.Value;
                Task.Run(() => _wmi.SetCpuFanSpeed((byte)val));
                SaveState("FanSpeedCpu", val);
                SaveFanSpeedForProfile("Cpu", val);
            };
            _gpuFanSlider.ValueCommitted += (s, e) =>
            {
                int val = _gpuFanSlider.Value;
                Task.Run(() => _wmi.SetGpuFanSpeed((byte)val));
                SaveState("FanSpeedGpu", val);
                SaveFanSpeedForProfile("Gpu", val);
            };

            y += S(28) + S(8);
            int subBtnW = (contentW - gap) / 2;
            _btnFixedSpeed = MakeButton("Fixed Speed", pad, y, subBtnW, btnH);
            _btnFanCurve = MakeButton("Curve", pad + subBtnW + gap, y, subBtnW, btnH);
            _btnFixedSpeed.Visible = false;
            _btnFanCurve.Visible = false;

            _btnFixedSpeed.Click += (s, e) =>
            {
                _fanCurveEnabled = false;
                _lastCurveCpuSpeed = -1;
                _lastCurveGpuSpeed = -1;
                _lastCurveSysSpeed = -1;
                _cpuCurveFollower.Reset();
                _gpuCurveFollower.Reset();
                _sysCurveFollower.Reset();
                _cpuDampingQueue.Clear();
                _gpuDampingQueue.Clear();

                ApplyFanMode(0x03, _btnCustomFan);
                HighlightBtn(_btnFixedSpeed, ref _activeCustomSubBtn);
                SaveState("FanCurveEnabled", 0);
                SaveStateToHklm("FanCurveEnabled", 0);

                if (_fanCurveForm != null && !_fanCurveForm.IsDisposed)
                    _fanCurveForm.Close();

                bool isLocked = GetCurrentFanLock() != null;
                _cpuFanSlider.Enabled = !isLocked;
                _gpuFanSlider.Enabled = !isLocked;
                if (_sysFanSlider != null) _sysFanSlider.Enabled = !isLocked;

                byte cpuVal = (byte)_cpuFanSlider.Value;
                byte gpuVal = (byte)_gpuFanSlider.Value;
                byte sysVal = (byte)(_sysFanSlider?.Value ?? 50);
                Task.Run(() =>
                {
                    _wmi.SetFanBehavior(0x03, applyCustomSpeeds: true);
                    _wmi.SetCpuFanSpeed(cpuVal);
                    _wmi.SetGpuFanSpeed(gpuVal);
                    if (_capabilities.HasThirdFan) _wmi.SetSystemFanSpeed(sysVal);
                });
            };

            _btnFanCurve.Click += (s, e) =>
            {
                _fanCurveEnabled = true;
                _lastCurveCpuSpeed = -1;
                _lastCurveGpuSpeed = -1;
                _lastCurveSysSpeed = -1;
                _cpuCurveFollower.Reset();
                _gpuCurveFollower.Reset();
                _sysCurveFollower.Reset();
                _cpuDampingQueue.Clear();
                _gpuDampingQueue.Clear();

                ApplyFanMode(0x03, _btnCustomFan);
                HighlightBtn(_btnFanCurve, ref _activeCustomSubBtn);
                SaveState("FanCurveEnabled", 1);
                SaveStateToHklm("FanCurveEnabled", 1);

                _cpuFanSlider.Enabled = false;
                _gpuFanSlider.Enabled = false;
                if (_sysFanSlider != null) _sysFanSlider.Enabled = false;

                ApplyFanCurve(forceHardwareApply: true);
                OpenFanCurveEditor();
            };

            if (_capabilities.CoolBoost)
            {
                y += btnH + S(12);
                int cbSwitchH = S(30);
                var lblCoolBoost = MakeLabel("Acer CoolBoost (Max Hardware Fan Boost)", pad, y, FontBody, Color.White);
                CenterV(lblCoolBoost, y, cbSwitchH);
                _switchCoolBoost = new PredatorSwitch
                {
                    Location = new Point(_formW - pad - S(48), y),
                    Size = new Size(S(48), cbSwitchH)
                };
                _switchCoolBoost.Checked = _wmi.GetCoolBoost() ?? false;
                _switchCoolBoost.CheckedChanged += (s, e) =>
                {
                    bool on = _switchCoolBoost.Checked;
                    Task.Run(() => _wmi.SetCoolBoost(on));
                };
                _contentPanel.Controls.Add(_switchCoolBoost);
                y += cbSwitchH;
            }

            if (_capabilities.DustDefender)
            {
                y += S(10);
                _btnDustDefender = MakeButton("🔄  DustDefender (Reverse Spin Cycle)", pad, y, contentW, btnH);
                _btnDustDefender.Click += (s, e) =>
                {
                    _btnDustDefender.Enabled = false;
                    _isDustDefenderRunning = true;
                    Task.Run(() =>
                    {
                        var res = _wmi.StartDustDefender();
                        BeginInvoke(new Action(() =>
                        {
                            if (!IsDisposed && _btnDustDefender != null)
                            {
                                _isDustDefenderRunning = res == DustDefenderStart.Started || res == DustDefenderStart.Running;
                                _btnDustDefender.Enabled = !_isDustDefenderRunning;
                                _btnDustDefender.Text = _isDustDefenderRunning ? "🔄  DustDefender: Running..." : "🔄  DustDefender (Reverse Spin Cycle)";
                            }
                        }));

                        if (res == DustDefenderStart.Started)
                        {
                            Task.Delay(25000).ContinueWith(_ =>
                            {
                                if (!IsDisposed && IsHandleCreated)
                                {
                                    BeginInvoke(new Action(() =>
                                    {
                                        _isDustDefenderRunning = false;
                                        if (_btnDustDefender != null)
                                        {
                                            _btnDustDefender.Text = "🔄  DustDefender (Reverse Spin Cycle)";
                                            _btnDustDefender.Enabled = true;
                                        }
                                    }));
                                }
                            });
                        }
                    });
                };
                y += btnH;
            }

            if (_capabilities.FanTable)
            {
                y += S(12);
                MakeLabel("FACTORY EC FAN TABLE:", pad, y, FontSectionHeader, SubHeaderColor);
                y += S(20);
                _cboFanTable = new PredatorDropDown { Location = new Point(pad, y), Size = new Size(contentW, S(30)) };
                _cboFanTable.Items.AddRange(new[] { "Standard", "Faster", "Fastest" });
                var curTable = _wmi.GetFanTable() ?? FanTable.Standard;
                _cboFanTable.SelectedIndex = Math.Clamp((int)curTable - 1, 0, 2);
                _cboFanTable.SelectedIndexChanged += (s, e) =>
                {
                    var table = (FanTable)(_cboFanTable.SelectedIndex + 1);
                    Task.Run(() => _wmi.SetFanTable(table));
                };
                _contentPanel.Controls.Add(_cboFanTable);
                y += S(30);
            }

            y += btnH + S(20);
            MakeSectionHeader("DISPLAY & LCD OVERDRIVE", pad, y);
            
            y += S(24);
            int dispBtnW = (contentW - gap) / 2;
            _btn60Hz = MakeButton("60 Hz", pad, y, dispBtnW, btnH);
            _btnMaxHz = MakeButton($"{_maxHz} Hz (Max)", pad + dispBtnW + gap, y, dispBtnW, btnH);

            _btn60Hz.Click += (s, e) => ApplyDisplayMode(60, _btn60Hz);
            _btnMaxHz.Click += (s, e) => ApplyDisplayMode(_maxHz, _btnMaxHz);

            y += btnH + S(16);
            int odSwitchH = S(30);
            _lblLcdOverdriveStatus = MakeLabel("LCD Overdrive (3ms Response)", pad, y, FontBody, Color.White);
            CenterV(_lblLcdOverdriveStatus, y, odSwitchH);

            _switchLcdOverdrive = new PredatorSwitch
            {
                Location = new Point(_formW - pad - S(48), y),
                Size = new Size(S(48), odSwitchH)
            };
            _contentPanel.Controls.Add(_switchLcdOverdrive);

            _switchLcdOverdrive.CheckedChanged += (s, e) =>
            {
                ApplyLcdOverdrive(_switchLcdOverdrive.Checked);
            };
            y += odSwitchH;

            if (_capabilities.GpuModeSwitch)
            {
                y += S(14);
                MakeLabel("GPU WORKING MODE (MUX SWITCH):", pad, y, FontSectionHeader, SubHeaderColor);
                y += S(20);
                _cboGpuMode = new PredatorDropDown { Location = new Point(pad, y), Size = new Size(contentW, S(30)) };
                _cboGpuMode.Items.AddRange(new[] { "Hybrid (NVIDIA Optimus)", "Discrete (dGPU Direct Only)" });
                var curGpuMode = _wmi.GetGpuMode() ?? GpuMode.Hybrid;
                _cboGpuMode.SelectedIndex = curGpuMode == GpuMode.Discrete ? 1 : 0;
                _cboGpuMode.SelectedIndexChanged += (s, e) =>
                {
                    var targetMode = _cboGpuMode.SelectedIndex == 1 ? GpuMode.Discrete : GpuMode.Hybrid;
                    Task.Run(() => _wmi.SetGpuMode(targetMode));
                    var res = MessageBox.Show(this, "GPU Working Mode changed. A system reboot is required for the BIOS MUX switch to take effect. Would you like to restart now?", "MUX Switch Reboot Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (res == DialogResult.Yes)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });
                        }
                        catch (Exception ex)
                        {
                            Program.Report(ex, false);
                        }
                    }
                };
                _contentPanel.Controls.Add(_cboGpuMode);
                y += S(30);
            }

            y += S(28);
            MakeSectionHeader("BATTERY CHARGE LIMIT", pad, y);

            y += S(24);
            int switchH = S(30);
            _lblBatteryStatus = MakeLabel("Full Charge (100%)", pad, y, FontBody, SubHeaderColor);
            CenterV(_lblBatteryStatus, y, switchH);

            _switchBatteryLimit = new PredatorSwitch
            {
                Location = new Point(_formW - pad - S(48), y),
                Size = new Size(S(48), switchH)
            };
            _contentPanel.Controls.Add(_switchBatteryLimit);

            _switchBatteryLimit.CheckedChanged += (s, e) =>
            {
                ApplyBatteryLimit(_switchBatteryLimit.Checked);
            };

            if (_capabilities.UsbCharging)
            {
                y += switchH + S(12);
                int usbSwitchH = S(30);
                var lblUsbCharge = MakeLabel("Power-Off USB Charging", pad, y, FontBody, Color.White);
                CenterV(lblUsbCharge, y, usbSwitchH);
                _switchUsbCharging = new PredatorSwitch
                {
                    Location = new Point(_formW - pad - S(48), y),
                    Size = new Size(S(48), usbSwitchH)
                };
                var curUsb = _wmi.GetUsbCharging();
                _switchUsbCharging.Checked = curUsb?.Enabled ?? true;
                _contentPanel.Controls.Add(_switchUsbCharging);

                y += usbSwitchH + S(6);
                int usbFloorDropW = contentW;
                MakeLabel("LOW BATTERY CHARGING FLOOR:", pad, y, FontSectionHeader, SubHeaderColor);
                y += S(20);
                _cboUsbFloor = new PredatorDropDown { Location = new Point(pad, y), Size = new Size(usbFloorDropW, S(30)) };
                _cboUsbFloor.Items.AddRange(new[] { "Stop at 10% Battery", "Stop at 20% Battery", "Stop at 30% Battery" });
                int floorIdx = curUsb?.FloorPercent switch { 10 => 0, 20 => 1, _ => 2 };
                _cboUsbFloor.SelectedIndex = floorIdx;
                _contentPanel.Controls.Add(_cboUsbFloor);

                _switchUsbCharging.CheckedChanged += (s, e) =>
                {
                    int floor = _cboUsbFloor.SelectedIndex switch { 0 => 10, 1 => 20, _ => 30 };
                    Task.Run(() => _wmi.SetUsbCharging(_switchUsbCharging.Checked, floor));
                };
                _cboUsbFloor.SelectedIndexChanged += (s, e) =>
                {
                    int floor = _cboUsbFloor.SelectedIndex switch { 0 => 10, 1 => 20, _ => 30 };
                    Task.Run(() => _wmi.SetUsbCharging(_switchUsbCharging.Checked, floor));
                };
                y += S(30);
            }

            var batt = WindowsBattery.Read();
            if (batt != null)
            {
                y += S(12);
                MakeLabel($"Battery Wear: {batt.WearLevelPercent:F1}% | Cycles: {(batt.CycleCount.HasValue ? batt.CycleCount.Value.ToString() : "N/A")}", pad, y, FontBody, SubHeaderColor);
                y += S(20);
            }

            if (_capabilities.BatteryCalibration)
            {
                y += S(8);
                _btnBatteryCalibration = MakeButton("⚡  Start Battery Calibration Cycle", pad, y, contentW, btnH);
                _btnBatteryCalibration.Click += (s, e) =>
                {
                    _btnBatteryCalibration.Enabled = false;
                    Task.Run(() =>
                    {
                        bool ok = _wmi.SetBatteryCalibration(true);
                        BeginInvoke(new Action(() =>
                        {
                            if (!IsDisposed && _btnBatteryCalibration != null)
                            {
                                _btnBatteryCalibration.Enabled = true;
                                MessageBox.Show(this, ok ? "Hardware battery calibration initiated. Keep AC adapter connected until calibration completes." : "Could not start calibration cycle.", "Battery Calibration", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                            }
                        }));
                    });
                };
                y += btnH;
            }

            y += switchH + S(12);
            _lblStartupStatus = MakeLabel("Start with Windows", pad, y, FontBody, SubHeaderColor);
            CenterV(_lblStartupStatus, y, switchH);

            _switchStartWithWindows = new PredatorToggle
            {
                Location = new Point(_formW - pad - S(48), y),
                Size = new Size(S(48), switchH),
                Checked = false
            };
            _contentPanel.Controls.Add(_switchStartWithWindows);

            _switchStartWithWindows.CheckedChanged += async (s, e) =>
            {
                if (_suppressStartupToggle) return;

                bool wanted = _switchStartWithWindows.Checked;
                _switchStartWithWindows.Enabled = false;
                bool ok = false;
                try
                {
                    ok = await Task.Run(() => SetStartupEnabled(wanted) && IsStartupEnabled() == wanted);
                }
                catch { }
                finally
                {
                    if (!IsDisposed && _switchStartWithWindows.IsHandleCreated)
                        _switchStartWithWindows.Enabled = true;
                }

                if (ok) return;

                _suppressStartupToggle = true;
                _switchStartWithWindows.Checked = !wanted;
                _suppressStartupToggle = false;

                MessageBox.Show(this,
                    wanted
                        ? "Could not register Predator Control to start with Windows.\r\n\r\nThe scheduled task could not be created. Try running the app as administrator."
                        : "Could not remove the Predator Control startup task.",
                    "Start with Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            y += switchH + S(28);
            MakeSectionHeader("KEYBOARD RGB MODE", pad, y);
            
            y += S(24);
            int dropH = S(34);
            _rgbDropDown = new PredatorDropDown { Location = new Point(pad, y), Size = new Size(contentW, dropH) };
            foreach (var name in RgbModeNames) _rgbDropDown.Items.Add(name);
            _rgbDropDown.SelectedIndex = 3; 
            _contentPanel.Controls.Add(_rgbDropDown);

            y += dropH + S(28);
            _lblBrightHdr = MakeLabel("BRIGHTNESS: 100%", pad, y, FontSectionHeader, SubHeaderColor);
            _lblSpeedHdr = MakeLabel("EFFECT SPEED: 50%", _formW / 2 + S(10), y, FontSectionHeader, SubHeaderColor);
            
            y += S(24);
            int sliderW = (contentW - gap * 4) / 2;
            _brightnessSlider = new PredatorSlider { Location = new Point(pad, y), Size = new Size(sliderW, S(28)), Minimum = 0, Maximum = 100, Value = 100 };
            _contentPanel.Controls.Add(_brightnessSlider);
            
            _speedSlider = new PredatorSlider { Location = new Point(_formW / 2 + S(10), y), Size = new Size(sliderW, S(28)), Minimum = 1, Maximum = 100, Value = 50 };
            _contentPanel.Controls.Add(_speedSlider);

            _brightnessSlider.ValueChanged += (s, e) =>
            {
                if (IsOnBattery() && _brightnessSlider.Value == 0 && !_manualBacklightOnBattery)
                    _lblBrightHdr.Text = "BRIGHTNESS: 0% (Off on Battery)";
                else
                    _lblBrightHdr.Text = $"BRIGHTNESS: {_brightnessSlider.Value}%";
            };
            _brightnessSlider.ValueCommitted += (s, e) =>
            {
                bool onBat = IsOnBattery();
                if (onBat)
                {
                    _manualBacklightOnBattery = _brightnessSlider.Value > 0;
                    _backlightMgr.OnUserAdjustedBrightness(_brightnessSlider.Value, true);
                }
                else
                {
                    _backlightMgr.OnUserAdjustedBrightness(_brightnessSlider.Value, false);
                    SaveState("Brightness", _brightnessSlider.Value);
                }

                int bVal = _brightnessSlider.Value;
                if (bVal == 0)
                {
                    Task.Run(() => _wmi.TurnOffBacklight());
                    if (onBat)
                        _lblBrightHdr.Text = "BRIGHTNESS: 0% (Off on Battery)";
                }
                else
                {
                    Task.Run(() => _wmi.SetBrightness((byte)bVal));
                }
            };

            _speedSlider.ValueChanged += (s, e) => { _lblSpeedHdr.Text = $"EFFECT SPEED: {_speedSlider.Value}%"; };
            _speedSlider.ValueCommitted += (s, e) =>
            {
                byte spd = GetMappedSpeed();
                Task.Run(() => _wmi.SetSpeed(spd));
                SaveState("RGB_Speed", _speedSlider.Value);
            };

            _rgbDropDown.SelectedIndexChanged += (s, e) =>
            {
                if (_isGameSyncOverriding || _isApplyingRgbMode) return; 
                int mode = _rgbDropDown.SelectedIndex;
                ApplyRgbModeFromDropdown(mode);
            };

            y += S(44);
            MakeSectionHeader("COLOR CUSTOMIZATION", pad, y);
            
            y += S(24);
            _btnColorPick = MakeButton("    Choose Custom Color", pad, y, contentW, btnH);
            _btnColorPick.Paint += (s, e) => {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int cy = _btnColorPick.Height / 2;
                int cx = _btnColorPick.Width / 2 - S(70);
                Color dotColor = _colorPicker.Color;
                using var glowBrush = new SolidBrush(Color.FromArgb(100, dotColor.R, dotColor.G, dotColor.B));
                g.FillEllipse(glowBrush, cx - S(8), cy - S(8), S(16), S(16));
                using var brush = new SolidBrush(dotColor);
                g.FillEllipse(brush, cx - S(6), cy - S(6), S(12), S(12));
            };

            _btnColorPick.Click += (s, e) =>
            {
                if (_colorPicker.ShowDialog() == DialogResult.OK)
                {
                    _btnColorPick.Invalidate();
                    Color c = _colorPicker.Color;
                    int bVal = _brightnessSlider.Value;
                    if (bVal == 0)
                    {
                        bVal = _backlightMgr.SavedAcBrightness > 0 ? _backlightMgr.SavedAcBrightness : 100;
                        _brightnessSlider.Value = bVal;
                        _lblBrightHdr.Text = $"BRIGHTNESS: {bVal}%";
                        if (IsOnBattery())
                        {
                            _manualBacklightOnBattery = true;
                            _backlightMgr.OnUserAdjustedBrightness(bVal, true);
                        }
                    }
                    byte spd = GetMappedSpeed();
                    Task.Run(() => _wmi.SetRgbMode(0, c.R, c.G, c.B, (byte)bVal, spd, 0));
                    _rgbDropDown.SelectedIndex = 0;
                    SaveState("RGB_Mode", 0);
                    SaveState("RGB_R", c.R); SaveState("RGB_G", c.G); SaveState("RGB_B", c.B);
                    UpdateRgbControls(0);
                    CheckRgbTrayFromMode(0);
                }
            };

            y += btnH + S(14);
            MakeLabel("KEYBOARD BACKLIGHT TIMEOUT:", pad, y, FontSectionHeader, SubHeaderColor);
            y += S(20);
            _cboBacklightTimeout = new PredatorDropDown { Location = new Point(pad, y), Size = new Size(contentW, S(30)) };
            _cboBacklightTimeout.Items.AddRange(new[] { "Never (Always On)", "30 Seconds", "1 Minute", "2 Minutes", "5 Minutes" });
            _cboBacklightTimeout.SelectedIndex = 0;
            _cboBacklightTimeout.SelectedIndexChanged += (s, e) =>
            {
                int seconds = _cboBacklightTimeout.SelectedIndex switch
                {
                    1 => 30,
                    2 => 60,
                    3 => 120,
                    4 => 300,
                    _ => 0
                };
                int bVal = _brightnessSlider.Value;
                Task.Run(() => _wmi.SetBacklightTimeout(2, bVal, seconds));
            };
            _contentPanel.Controls.Add(_cboBacklightTimeout);

            y += S(30) + S(28);
            AddSeparator(y);
            y += S(20);
            MakeSectionHeader("GAME SYNC", pad, y);

            y += S(24);
            int syncSwitchH = S(30);
            _lblGameSyncStatus = MakeLabel("Disabled", pad, y, FontBody, SubHeaderColor);
            CenterV(_lblGameSyncStatus, y, syncSwitchH);

            _switchGameSync = new PredatorToggle
            {
                Location = new Point(_formW - pad - S(48), y),
                Size = new Size(S(48), syncSwitchH)
            };
            _contentPanel.Controls.Add(_switchGameSync);

            _switchGameSync.CheckedChanged += (s, e) =>
            {
                _gameSync.IsEnabled = _switchGameSync.Checked;
                _lblGameSyncStatus.Text = _switchGameSync.Checked ? "Active — Monitoring" : "Disabled";
            };

            y += syncSwitchH + S(10);
            _btnConfigureGames = MakeButton("🎮  Configure Executables", pad, y, contentW, btnH);
            _btnConfigureGames.Click += (s, e) =>
            {
                using var form = new GameSyncForm(_gameSync, _maxHz);
                form.ShowDialog(this);
            };

            y += btnH + S(24);
            AddSeparator(y);

            y += S(20);
            MakeSectionHeader("THEME & DIAGNOSTICS", pad, y);

            y += S(24);
            int themeDropW = (contentW - gap) / 2;
            MakeLabel("APP THEME:", pad, y, FontSectionHeader, SubHeaderColor);
            MakeLabel("DIAGNOSTICS:", pad + themeDropW + gap, y, FontSectionHeader, SubHeaderColor);

            y += S(20);
            _cboTheme = new PredatorDropDown { Location = new Point(pad, y), Size = new Size(themeDropW, S(30)) };
            _cboTheme.Items.AddRange(new[] { "System Default", "Dark Theme", "Light Theme" });
            _cboTheme.SelectedIndex = (int)ThemeManager.CurrentTheme;
            _cboTheme.SelectedIndexChanged += (s, e) =>
            {
                var theme = (AppTheme)_cboTheme.SelectedIndex;
                ThemeManager.SetTheme(theme);
                ThemeManager.ApplyTheme(this);
            };
            _contentPanel.Controls.Add(_cboTheme);

            _btnDiagnostics = MakeButton("📋  Copy Diagnostics", pad + themeDropW + gap, y, themeDropW, S(30));
            _btnDiagnostics.Click += (s, e) =>
            {
                var pdh = _pdhMonitor.Sample();
                bool asleep = GpuPowerMonitor.IsGpuAsleep();
                int? sysRpm = _capabilities.HasThirdFan ? _wmi.SystemFanRpm : null;
                string report = DiagnosticsDumper.GenerateReport(_wmi, _capabilities, pdh, asleep, sysRpm);
                DiagnosticsDumper.CopyToClipboard(report);
                MessageBox.Show(this, "Diagnostic report copied to clipboard!", "Predator Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            y += S(30) + S(16);
            AddSeparator(y);

            y += S(20);
            MakeSectionHeader("UPDATES", pad, y);

            y += S(24);
            int updBtnH = S(30), updBtnW = S(160);
            var lblVersion = MakeLabel($"Version v.{Updater.CurrentText}", pad, y, FontBody, SubHeaderColor);
            CenterV(lblVersion, y, updBtnH);

            _btnCheckUpdates = MakeButton("⬇  Check for Updates", _formW - pad - updBtnW, y, updBtnW, updBtnH);
            _btnCheckUpdates.Click += async (s, e) => await CheckForUpdatesAsync();

            _contentPanel.AutoScrollMinSize = new Size(0, y + updBtnH + S(50));
        }

        private void UpdateRgbControls(int mode)
        {
            bool hasSpeed = mode != 0;
            _speedSlider.Enabled = hasSpeed;
            _btnColorPick.Enabled = mode == 0;
        }

        private void MakeSectionHeader(string label, int x, int y)
        {
            MakeLabel(label, x, y, FontSectionHeader, HeaderColor);
        }

        private Label MakeLabel(string text, int x, int y, Font font, Color color)
        {
            var lbl = new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = true, Font = font, ForeColor = color, BackColor = Color.Transparent
            };
            _contentPanel.Controls.Add(lbl);
            return lbl;
        }

        private PredatorButton MakeButton(string text, int x, int y, int width, int height)
        {
            var btn = new PredatorButton { Text = text, Location = new Point(x, y), Size = new Size(width, height) };
            _contentPanel.Controls.Add(btn);
            return btn;
        }

        private void AddSeparator(int y)
        {
            int pad = S(24);
            _contentPanel.Controls.Add(new Panel { Location = new Point(pad, y), Size = new Size(_formW - pad * 2, 1), BackColor = SeparatorColor });
        }

        private void CenterV(Label lbl, int controlY, int controlH)
        {
            lbl.Location = new Point(lbl.Left, controlY + (controlH - lbl.Height) / 2);
        }

        #endregion

        #region Action Handlers

        private static void SaveStateToHklm(string name, int val)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\PredatorControl");
                key?.SetValue(name, val);
            }
            catch { }
        }

        private void ApplyPowerMode(byte mode, PredatorButton btn)
        {
            HighlightBtn(btn, ref _activePowerBtn);
            _currentPowerMode = mode;
            if (!_isGameSyncOverriding)
            {
                SaveState("Power", mode);
                SaveStateToHklm("Power", mode);

                var lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
                bool onBattery = _backlightMgr.IsOnBattery(lineStatus);
                if (onBattery)
                {
                    SaveState("Power_Battery", mode);
                    SaveStateToHklm("Power_Battery", mode);
                }
                else
                {
                    SaveState("Power_AC", mode);
                    SaveStateToHklm("Power_AC", mode);
                }
            }

            _lblPowerStatus.Text = mode switch
            {
                0x00 => "Quiet",
                0x04 => "Performance",
                0x05 => "Turbo",
                0x06 => "Eco",
                _ => "Balanced"
            };

            var trayItem = mode switch
            {
                0x00 => _trayPowerQuiet,
                0x04 => _trayPowerPerf,
                0x05 => _trayPowerTurbo,
                0x06 => _trayPowerEco,
                _ => _trayPowerBal
            };
            CheckTrayItem(trayItem, _trayPowerQuiet, _trayPowerBal, _trayPowerPerf, _trayPowerTurbo, _trayPowerEco);

            Task.Run(() => _wmi.SetPowerMode(mode));
            if (_rgbDropDown != null && _rgbDropDown.SelectedIndex == (int)LightingEffect34.FollowOperatingMode)
            {
                ApplyRgbModeFromDropdown(_rgbDropDown.SelectedIndex);
            }
            ScheduleRgbRestoreWatchdog();

            if (GetCurrentFanByte() == 0x03)
            {
                bool locked = GetCurrentFanLock() != null;
                _cpuFanSlider.Enabled = !locked && !_fanCurveEnabled;
                _gpuFanSlider.Enabled = !locked && !_fanCurveEnabled;
                if (_sysFanSlider != null) _sysFanSlider.Enabled = !locked && !_fanCurveEnabled;
                if (!locked && _fanCurveEnabled)
                {
                    ApplyFanCurve(forceHardwareApply: true);
                }
            }
        }

        private void CyclePowerMode()
        {
            var modes = _capabilities.OperatingModes;
            if (modes == null || modes.Count == 0) return;

            var allowedModes = modes.Where(m =>
            {
                var btn = PowerByteToBtn((byte)m);
                return btn != null && btn.Enabled;
            }).ToList();
            if (allowedModes.Count == 0) allowedModes = modes.ToList();

            int currentIndex = allowedModes.FindIndex(m => (byte)m == _currentPowerMode);
            int nextIndex = (currentIndex + 1) % allowedModes.Count;
            byte nextMode = (byte)allowedModes[nextIndex];
            ApplyPowerMode(nextMode, PowerByteToBtn(nextMode));
        }

        private FanLock? GetCurrentFanLock()
        {
            if (_isDustDefenderRunning) return FanLock.DustDefender;
            byte currentPower = GetCurrentPowerByte();
            if (currentPower == 0x00) return FanLock.QuietMode;
            if (currentPower == 0x06) return FanLock.EcoMode;
            return null;
        }

        private void ApplyLcdOverdrive(bool enable)
        {
            if (_switchLcdOverdrive.Checked != enable)
                _switchLcdOverdrive.Checked = enable;
            if (_trayLcdOverdrive != null)
                _trayLcdOverdrive.Checked = enable;
            _lblLcdOverdriveStatus.Text = enable ? "LCD Overdrive (3ms Enabled)" : "LCD Overdrive (Disabled)";
            _lblLcdOverdriveStatus.ForeColor = enable ? AccentColor : Color.White;
            SaveState("LcdOverdrive", enable ? 1 : 0);
            SaveStateToHklm("LcdOverdrive", enable ? 1 : 0);
            Task.Run(() => _wmi.SetLcdOverdrive(enable));
        }

        private void ApplyFanMode(byte mode, PredatorButton btn)
        {
            HighlightBtn(btn, ref _activeFanBtn);
            if (!_isGameSyncOverriding)
            {
                SaveState("Fan", mode);
                SaveStateToHklm("Fan", mode);

                var lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
                bool onBattery = _backlightMgr.IsOnBattery(lineStatus);
                if (onBattery)
                {
                    SaveState("Fan_Battery", mode);
                    SaveStateToHklm("Fan_Battery", mode);
                }
                else
                {
                    SaveState("Fan_AC", mode);
                    SaveStateToHklm("Fan_AC", mode);
                }
            }

            _lblFanStatus.Text = mode switch
            {
                0x02 => "Max",
                0x03 => "Custom",
                _ => "Auto"
            };

            bool isCustom = mode == 0x03;
            _lblCpuFanSpeedHdr.Visible = isCustom;
            _lblGpuFanSpeedHdr.Visible = isCustom;
            if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Visible = isCustom;
            _cpuFanSlider.Visible = isCustom;
            _gpuFanSlider.Visible = isCustom;
            if (_sysFanSlider != null) _sysFanSlider.Visible = isCustom;
            _btnFixedSpeed.Visible = isCustom;
            _btnFanCurve.Visible = isCustom;

            if (isCustom)
            {
                bool isLocked = GetCurrentFanLock() != null;
                if (_fanCurveEnabled)
                {
                    HighlightBtn(_btnFanCurve, ref _activeCustomSubBtn);
                    _cpuFanSlider.Enabled = false;
                    _gpuFanSlider.Enabled = false;
                    if (_sysFanSlider != null) _sysFanSlider.Enabled = false;
                    if (!isLocked)
                    {
                        ApplyFanCurve(forceHardwareApply: true);
                    }
                }
                else
                {
                    HighlightBtn(_btnFixedSpeed, ref _activeCustomSubBtn);
                    _cpuFanSlider.Enabled = !isLocked;
                    _gpuFanSlider.Enabled = !isLocked;
                    if (_sysFanSlider != null) _sysFanSlider.Enabled = !isLocked;
                    byte cpuVal = (byte)_cpuFanSlider.Value;
                    byte gpuVal = (byte)_gpuFanSlider.Value;
                    byte sysVal = (byte)(_sysFanSlider?.Value ?? 50);
                    Task.Run(() =>
                    {
                        _wmi.SetFanBehavior(0x03, applyCustomSpeeds: true);
                        _wmi.SetCpuFanSpeed(cpuVal);
                        _wmi.SetGpuFanSpeed(gpuVal);
                        if (_capabilities.HasThirdFan) _wmi.SetSystemFanSpeed(sysVal);
                    });
                }
            }
            else
            {
                _lastCurveCpuSpeed = -1;
                _lastCurveGpuSpeed = -1;
                _lastCurveSysSpeed = -1;
                _cpuCurveFollower.Reset();
                _gpuCurveFollower.Reset();
                _sysCurveFollower.Reset();
                _cpuDampingQueue.Clear();
                _gpuDampingQueue.Clear();
                if (_activeCustomSubBtn != null)
                {
                    _activeCustomSubBtn.IsActive = false;
                    _activeCustomSubBtn = null;
                }
                Task.Run(() => _wmi.SetFanBehavior(mode, false));
            }

            var trayItem = mode switch
            {
                0x01 => _trayFanAuto,
                0x02 => _trayFanMax,
                _ => _trayFanCustom
            };
            CheckTrayItem(trayItem, _trayFanAuto, _trayFanMax, _trayFanCustom);
        }

        private void OpenFanCurveEditor()
        {
            if (_fanCurveForm != null && !_fanCurveForm.IsDisposed)
            {
                _fanCurveForm.Activate();
                return;
            }

            _fanCurveForm = new FanCurveForm();
            _fanCurveForm.SetCpuCurve(_cpuCurvePoints);
            _fanCurveForm.SetGpuCurve(_gpuCurvePoints);
            _fanCurveForm.UpdateTemps(_cpuTemp, _gpuTemp);

            _fanCurveForm.ApplyClicked += (s, args) =>
            {
                _cpuCurvePoints = args.CpuPoints;
                _gpuCurvePoints = args.GpuPoints;
                _fanCurveEnabled = true;
                _lastCurveCpuSpeed = -1;
                _lastCurveGpuSpeed = -1;
                _lastCurveSysSpeed = -1;
                _cpuCurveFollower.Reset();
                _gpuCurveFollower.Reset();
                _sysCurveFollower.Reset();
                _cpuDampingQueue.Clear();
                _gpuDampingQueue.Clear();

                ApplyFanMode(0x03, _btnCustomFan);
                HighlightBtn(_btnFanCurve, ref _activeCustomSubBtn);
                _cpuFanSlider.Enabled = false;
                _gpuFanSlider.Enabled = false;
                if (_sysFanSlider != null) _sysFanSlider.Enabled = false;

                SaveCurveToRegistry("CpuCurve", _cpuCurvePoints);
                SaveCurveToRegistry("GpuCurve", _gpuCurvePoints);
                SaveState("FanCurveEnabled", 1);
                SaveStateToHklm("FanCurveEnabled", 1);

                ApplyFanCurve(forceHardwareApply: true);
                args.Success = true;
            };

            _fanCurveForm.FormClosed += (s, e) => _fanCurveForm = null;
            _fanCurveForm.Show(this);
        }

        private void SaveCurveToRegistry(string name, List<Point> points)
        {
            try
            {
                string data = string.Join(";", points.Select(p => $"{p.X},{p.Y}"));
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");
                key.SetValue(name, data);
            }
            catch { }
            try
            {
                string data = string.Join(";", points.Select(p => $"{p.X},{p.Y}"));
                using var hklmKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\PredatorControl");
                hklmKey?.SetValue(name, data);
            }
            catch { }
        }

        private List<Point>? LoadCurveFromRegistry(string name)
        {
            try
            {
                string? data = null;
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\PredatorControl");
                    data = key?.GetValue(name) as string;
                }
                catch { }

                if (string.IsNullOrEmpty(data))
                {
                    try
                    {
                        using var hklmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PredatorControl");
                        data = hklmKey?.GetValue(name) as string;
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(data)) return null;

                var pts = new List<Point>();
                foreach (var pair in data.Split(';'))
                {
                    var parts = pair.Split(',');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                        pts.Add(new Point(x, y));
                }
                return pts.Count >= 2 ? FanCurveGraph.Normalize(pts) : null;
            }
            catch { return null; }
        }

        private void ApplyDisplayMode(int hz, PredatorButton btn)
        {
            if (!SetRefreshRate(hz)) return;   
            HighlightBtn(btn, ref _activeDisplayBtn);
            CheckTrayItem(hz <= 60 ? _trayDisplay60 : _trayDisplayMax, _trayDisplay60, _trayDisplayMax);
        }

        private void ApplyBatteryLimit(bool limit)
        {
            if (_isUpdatingBattery) return;
            _isUpdatingBattery = true;

            try
            {
                if (_switchBatteryLimit.Checked != limit)
                    _switchBatteryLimit.Checked = limit;

                _lblBatteryStatus.Text = limit ? "Limit to 80% (Health)" : "Full Charge (100%)";
                CheckTrayItem(limit ? _trayBatteryLimit80 : _trayBatteryLimit100, _trayBatteryLimit80, _trayBatteryLimit100);
                SaveState("BatteryLimit", limit ? 1 : 0);

                Task.Run(() => _wmi.SetBatteryChargeLimit(limit));
            }
            finally
            {
                _isUpdatingBattery = false;
            }
        }

        private void ApplyRgbModeFromDropdown(int mode)
        {
            if (_isApplyingRgbMode) return;
            _isApplyingRgbMode = true;
            try
            {
                int brightVal = _brightnessSlider.Value;
                if (brightVal == 0)
                {
                    brightVal = _backlightMgr.SavedAcBrightness > 0 ? _backlightMgr.SavedAcBrightness : 100;
                    _brightnessSlider.Value = brightVal;
                    _lblBrightHdr.Text = $"BRIGHTNESS: {brightVal}%";
                    if (IsOnBattery())
                    {
                        _manualBacklightOnBattery = true;
                        _backlightMgr.OnUserAdjustedBrightness(brightVal, true);
                    }
                }

                byte bright = (byte)brightVal;
                byte speed = GetMappedSpeed();
                byte r = mode == 0 ? _colorPicker.Color.R : _wmi.LastR;
                byte g = mode == 0 ? _colorPicker.Color.G : _wmi.LastG;
                byte b = mode == 0 ? _colorPicker.Color.B : _wmi.LastB;

                Task.Run(() => LightingEffectsManager.ApplyEffect(mode, _wmi, r, g, b, bright, speed, 0, (OperatingMode)_currentPowerMode));

                if (_rgbDropDown.SelectedIndex != mode)
                    _rgbDropDown.SelectedIndex = mode;

                SaveState("RGB_Mode", mode);
                if (mode == 0)
                {
                    SaveState("RGB_R", r); SaveState("RGB_G", g); SaveState("RGB_B", b);
                }
                UpdateRgbControls(mode);
                CheckRgbTrayFromMode(mode);
            }
            finally
            {
                _isApplyingRgbMode = false;
            }
        }

        private void CheckRgbTrayFromMode(int mode)
        {
            var active = mode switch
            {
                0 => _trayRgbStatic,
                1 => _trayRgbBreathe,
                2 => _trayRgbNeon,
                3 => _trayRgbWave,
                4 => _trayRgbShift,
                5 => _trayRgbZoom,
                6 => _trayRgbMeteor,
                _ => _trayRgbTwinkle
            };
            CheckTrayItem(active, _trayRgbStatic, _trayRgbBreathe, _trayRgbNeon, _trayRgbWave,
                          _trayRgbShift, _trayRgbZoom, _trayRgbMeteor, _trayRgbTwinkle);
        }

        #endregion

        #region Game Sync Handlers

        private DashboardSnapshot CaptureCurrentState()
        {
            return new DashboardSnapshot
            {
                PowerMode = GetCurrentPowerByte(),
                FanMode = GetCurrentFanByte(),
                CpuFanSpeed = _cpuFanSlider.Value,
                GpuFanSpeed = _gpuFanSlider.Value,
                SysFanSpeed = _sysFanSlider?.Value ?? 50,
                FanCurveWasEnabled = _fanCurveEnabled,
                RefreshRate = GetCurrentRefreshRate(),
                BatteryLimit = _switchBatteryLimit.Checked ? 1 : 0,
                RgbMode = _rgbDropDown.SelectedIndex,
                RgbBrightness = _brightnessSlider.Value,
                RgbSpeed = _speedSlider.Value,
                RgbR = _wmi.LastR,
                RgbG = _wmi.LastG,
                RgbB = _wmi.LastB,
            };
        }

        private byte GetCurrentPowerByte()
        {
            if (_activePowerBtn == _btnQuiet) return 0x00;
            if (_activePowerBtn == _btnPerform) return 0x04;
            if (_activePowerBtn == _btnTurbo) return 0x05;
            if (_activePowerBtn == _btnEco) return 0x06;
            return 0x01; 
        }

        private byte GetCurrentFanByte()
        {
            if (_activeFanBtn == _btnMaxFan) return 0x02;
            if (_activeFanBtn == _btnCustomFan) return 0x03;
            return 0x01; 
        }

        private PredatorButton PowerByteToBtn(byte mode) => mode switch
        {
            0x00 => _btnQuiet,
            0x04 => _btnPerform,
            0x05 => _btnTurbo,
            0x06 => _btnEco,
            _ => _btnBalanced
        };

        private PredatorButton FanByteToBtn(byte mode) => mode switch
        {
            0x02 => _btnMaxFan,
            0x03 => _btnCustomFan,
            _ => _btnAutoFan
        };

        private void OnGameDetected(GameProfile profile)
        {
            if (_isClosing || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnGameDetected(profile))); } catch { }
                return;
            }

            _ = HandleGameDetectedAsync(profile);
        }

        private async Task HandleGameDetectedAsync(GameProfile profile)
        {
            if (_isClosing || IsDisposed) return;
            _isGameSyncOverriding = true;
            try { await ApplyGameProfile(profile); }
            catch (Exception ex) { Program.Report(ex, false); }
            finally { _isGameSyncOverriding = false; }
        }

        private async Task ApplyGameProfile(GameProfile profile)
        {
            _lblGameSyncStatus.Text = $"Active \u2014 {profile.DisplayName}";

            _gameSync.SetPreGameSnapshot(CaptureCurrentState());

            ApplyPowerMode(profile.PowerMode, PowerByteToBtn(profile.PowerMode));
            ApplyFanMode(profile.FanMode, FanByteToBtn(profile.FanMode));

            if (profile.FanMode == 0x03)
            {
                _fanCurveEnabled = false;
                HighlightBtn(_btnFixedSpeed, ref _activeCustomSubBtn);
                _cpuFanSlider.Enabled = true;
                _gpuFanSlider.Enabled = true;
                if (_sysFanSlider != null) _sysFanSlider.Enabled = true;

                int cpuSpeed = profile.CpuFanSpeed >= 10 ? Math.Clamp(profile.CpuFanSpeed, 10, 100) : -1;
                int gpuSpeed = profile.GpuFanSpeed >= 10 ? Math.Clamp(profile.GpuFanSpeed, 10, 100) : -1;
                int sysSpeed = profile.SysFanSpeed >= 10 ? Math.Clamp(profile.SysFanSpeed, 10, 100) : -1;

                if (cpuSpeed >= 10)
                {
                    _cpuFanSlider.Value = cpuSpeed;
                    _lblCpuFanSpeedHdr.Text = $"CPU FAN: {cpuSpeed}%";
                }
                if (gpuSpeed >= 10)
                {
                    _gpuFanSlider.Value = gpuSpeed;
                    _lblGpuFanSpeedHdr.Text = $"GPU FAN: {gpuSpeed}%";
                }
                if (sysSpeed >= 10 && _sysFanSlider != null)
                {
                    _sysFanSlider.Value = sysSpeed;
                    if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {sysSpeed}%";
                }

                if (cpuSpeed >= 10 || gpuSpeed >= 10 || sysSpeed >= 10)
                {
                    await Task.Run(() =>
                    {
                        if (cpuSpeed >= 10) _wmi.SetCpuFanSpeed((byte)cpuSpeed);
                        if (gpuSpeed >= 10) _wmi.SetGpuFanSpeed((byte)gpuSpeed);
                        if (sysSpeed >= 10 && _capabilities.HasThirdFan) _wmi.SetSystemFanSpeed((byte)sysSpeed);
                    });
                }
            }

            if (profile.RefreshRate > 0)
                ApplyDisplayMode(profile.RefreshRate, profile.RefreshRate <= 60 ? _btn60Hz : _btnMaxHz);

            if (profile.BatteryLimit >= 0)
                ApplyBatteryLimit(profile.BatteryLimit == 1);

            await Task.Delay(500);
            if (IsDisposed) return;

            if (profile.RgbMode >= 0)
            {
                int mode = Math.Clamp(profile.RgbMode, 0, 7);
                int brightVal = profile.RgbBrightness >= 0 ? Math.Clamp(profile.RgbBrightness, 0, 100) : _brightnessSlider.Value;
                int speedVal = profile.RgbSpeed >= 0 ? Math.Clamp(profile.RgbSpeed, 1, 100) : _speedSlider.Value;
                byte bright = (byte)brightVal;
                byte speed = profile.RgbSpeed >= 0 ? (byte)Math.Clamp(Math.Round(speedVal * 9.0 / 100.0), 1, 9) : GetMappedSpeed();
                byte r = profile.RgbR >= 0 ? (byte)Math.Clamp(profile.RgbR, 0, 255) : _wmi.LastR;
                byte g = profile.RgbG >= 0 ? (byte)Math.Clamp(profile.RgbG, 0, 255) : _wmi.LastG;
                byte b = profile.RgbB >= 0 ? (byte)Math.Clamp(profile.RgbB, 0, 255) : _wmi.LastB;

                await Task.Run(() => _wmi.SetRgbMode(mode, r, g, b, bright, speed, 0));
                if (_rgbDropDown.SelectedIndex != mode)
                    _rgbDropDown.SelectedIndex = mode;
                _brightnessSlider.Value = brightVal;
                _speedSlider.Value = speedVal;
                UpdateRgbControls(mode);
                CheckRgbTrayFromMode(mode);
            }
        }

        private void OnGameExited(DashboardSnapshot snap)
        {
            if (_isClosing || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnGameExited(snap))); } catch { }
                return;
            }

            _ = HandleGameExitedAsync(snap);
        }

        private async Task HandleGameExitedAsync(DashboardSnapshot snap)
        {
            if (_isClosing || IsDisposed) return;
            _lblGameSyncStatus.Text = "Active \u2014 Monitoring";

            if (snap != null)
            {
                _isGameSyncOverriding = true;
                try { await RestoreSnapshot(snap); }
                catch (Exception ex) { Program.Report(ex, false); }
                finally { _isGameSyncOverriding = false; }
            }
        }

        private async Task RestoreSnapshot(DashboardSnapshot snap)
        {
            ApplyPowerMode(snap.PowerMode, PowerByteToBtn(snap.PowerMode));
            ApplyFanMode(snap.FanMode, FanByteToBtn(snap.FanMode));

            if (snap.FanMode == 0x03)
            {
                if (snap.FanCurveWasEnabled)
                {
                    _fanCurveEnabled = true;
                    HighlightBtn(_btnFanCurve, ref _activeCustomSubBtn);
                    _cpuFanSlider.Enabled = false;
                    _gpuFanSlider.Enabled = false;
                    if (_sysFanSlider != null) _sysFanSlider.Enabled = false;
                    ApplyFanCurve(forceHardwareApply: true);
                }
                else
                {
                    _fanCurveEnabled = false;
                    HighlightBtn(_btnFixedSpeed, ref _activeCustomSubBtn);
                    _cpuFanSlider.Enabled = true;
                    _gpuFanSlider.Enabled = true;
                    if (_sysFanSlider != null) _sysFanSlider.Enabled = true;
                    int cpuSpeed = Math.Clamp(snap.CpuFanSpeed, 10, 100);
                    int gpuSpeed = Math.Clamp(snap.GpuFanSpeed, 10, 100);
                    int sysSpeed = Math.Clamp(snap.SysFanSpeed, 10, 100);
                    _cpuFanSlider.Value = cpuSpeed;
                    _gpuFanSlider.Value = gpuSpeed;
                    if (_sysFanSlider != null) _sysFanSlider.Value = sysSpeed;
                    _lblCpuFanSpeedHdr.Text = $"CPU FAN: {cpuSpeed}%";
                    _lblGpuFanSpeedHdr.Text = $"GPU FAN: {gpuSpeed}%";
                    if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {sysSpeed}%";
                    await Task.Run(() =>
                    {
                        _wmi.SetCpuFanSpeed((byte)cpuSpeed);
                        _wmi.SetGpuFanSpeed((byte)gpuSpeed);
                        if (_capabilities.HasThirdFan) _wmi.SetSystemFanSpeed((byte)sysSpeed);
                    });
                }
            }

            if (snap.RefreshRate > 0)
                ApplyDisplayMode(snap.RefreshRate, snap.RefreshRate <= 60 ? _btn60Hz : _btnMaxHz);

            ApplyBatteryLimit(snap.BatteryLimit == 1);

            await Task.Delay(500);
            if (IsDisposed) return;

            int rgbMode = Math.Clamp(snap.RgbMode, 0, 7);
            int bright = Math.Clamp(snap.RgbBrightness, 0, 100);
            int speed = Math.Clamp(snap.RgbSpeed, 1, 100);

            byte r = (byte)Math.Clamp(snap.RgbR, 0, 255);
            byte g = (byte)Math.Clamp(snap.RgbG, 0, 255);
            byte b = (byte)Math.Clamp(snap.RgbB, 0, 255);
            byte spd = (byte)Math.Clamp(Math.Round(speed * 9.0 / 100.0), 1, 9);
            byte brt = (byte)bright;

            await Task.Run(() => _wmi.SetRgbMode(rgbMode, r, g, b, brt, spd, 0));
            if (_rgbDropDown.SelectedIndex != rgbMode)
                _rgbDropDown.SelectedIndex = rgbMode;
            _brightnessSlider.Value = bright;
            _speedSlider.Value = speed;
            UpdateRgbControls(rgbMode);
            CheckRgbTrayFromMode(rgbMode);
        }

        #endregion

        #region State Persistence

        private void SaveState(string name, int value)
        {
            if (_isGameSyncOverriding) return; 
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");
                key?.SetValue(name, value);
            }
            catch { }
            try
            {
                using var hklmKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\PredatorControl");
                hklmKey?.SetValue(name, value);
            }
            catch { }
        }

        internal static int GetInt(RegistryKey? key, string name, int fallback, int min, int max)
        {
            if (key == null) return fallback;
            try
            {
                object? raw = key.GetValue(name);
                if (raw is int i) return Math.Clamp(i, min, max);
                if (raw != null && int.TryParse(raw.ToString(), out int p)) return Math.Clamp(p, min, max);
            }
            catch { }
            return fallback;
        }

        private void LoadMemory()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");
                using var hklmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PredatorControl");

                int savedRgbMode = GetInt(key, "RGB_Mode", -1, 0, 7);
                if (savedRgbMode == -1) savedRgbMode = GetInt(hklmKey, "RGB_Mode", 3, 0, 7);

                int savedBrightness = GetInt(key, "Brightness", -1, 0, 100);
                if (savedBrightness == -1) savedBrightness = GetInt(hklmKey, "Brightness", 100, 0, 100);

                int savedSpeed = GetInt(key, "RGB_Speed", -1, 1, 100);
                if (savedSpeed == -1) savedSpeed = GetInt(hklmKey, "RGB_Speed", 50, 1, 100);

                _backlightMgr.SavedAcBrightness = Math.Clamp(savedBrightness, 0, 100);
                var initLine = SystemInformation.PowerStatus.PowerLineStatus;
                bool onBatteryStartup = _backlightMgr.IsOnBattery(initLine);
                _isPluggedIn = !onBatteryStartup;
                _pendingPluggedIn = _isPluggedIn;
                _powerLineStableTicks = 2;

                int savedR = GetInt(key, "RGB_R", 0, 0, 255);
                int savedG = GetInt(key, "RGB_G", 150, 0, 255);
                int savedB = GetInt(key, "RGB_B", 255, 0, 255);
                if (savedR == 0 && savedG == 0 && savedB == 0)
                {
                    savedR = 0; savedG = 150; savedB = 255;
                }
                _colorPicker.Color = Color.FromArgb(savedR, savedG, savedB);
                _btnColorPick?.Invalidate();

                if (onBatteryStartup)
                {
                    _manualBacklightOnBattery = false;
                    _brightnessSlider.Value = 0;
                    if (_lblBrightHdr != null) _lblBrightHdr.Text = "BRIGHTNESS: 0% (Off on Battery)";
                }
                else
                {
                    _brightnessSlider.Value = Math.Clamp(savedBrightness, 0, 100);
                    if (_lblBrightHdr != null) _lblBrightHdr.Text = $"BRIGHTNESS: {_brightnessSlider.Value}%";
                }
                _speedSlider.Value = Math.Clamp(savedSpeed, 1, 100);
                if (_lblSpeedHdr != null) _lblSpeedHdr.Text = $"EFFECT SPEED: {_speedSlider.Value}%";

                int savedAutoAc = GetInt(key, "AutoPowerAC", -1, 0, _cboAcProfile.Items.Count - 1);
                if (savedAutoAc == -1) savedAutoAc = GetInt(hklmKey, "AutoPowerAC", 0, 0, _cboAcProfile.Items.Count - 1);

                int savedAutoBat = GetInt(key, "AutoPowerBattery", -1, 0, _cboBatteryProfile.Items.Count - 1);
                if (savedAutoBat == -1) savedAutoBat = GetInt(hklmKey, "AutoPowerBattery", 0, 0, _cboBatteryProfile.Items.Count - 1);

                _cboAcProfile.SelectedIndex = savedAutoAc;
                _cboBatteryProfile.SelectedIndex = savedAutoBat;

                int savedAutoFanAc = GetInt(key, "AutoFanAC", -1, 0, _cboAcFan.Items.Count - 1);
                if (savedAutoFanAc == -1) savedAutoFanAc = GetInt(hklmKey, "AutoFanAC", 0, 0, _cboAcFan.Items.Count - 1);

                int savedAutoFanBat = GetInt(key, "AutoFanBattery", -1, 0, _cboBatteryFan.Items.Count - 1);
                if (savedAutoFanBat == -1) savedAutoFanBat = GetInt(hklmKey, "AutoFanBattery", 0, 0, _cboBatteryFan.Items.Count - 1);

                _cboAcFan.SelectedIndex = savedAutoFanAc;
                _cboBatteryFan.SelectedIndex = savedAutoFanBat;

                int savedPower = -1;
                if (onBatteryStartup)
                {
                    if (savedAutoBat > 0 && savedAutoBat < BatteryProfileValues.Length)
                    {
                        savedPower = BatteryProfileValues[savedAutoBat];
                    }
                    else
                    {
                        savedPower = GetInt(key, "Power_Battery", -1, 0x00, 0x06);
                        if (savedPower == -1) savedPower = GetInt(hklmKey, "Power_Battery", -1, 0x00, 0x06);
                        if (savedPower == -1) savedPower = GetInt(key, "Power", -1, 0x00, 0x06);
                        if (savedPower == -1) savedPower = GetInt(hklmKey, "Power", 0x01, 0x00, 0x06);
                    }
                    if (savedPower == 0x04 || savedPower == 0x05) savedPower = 0x01;
                }
                else
                {
                    if (savedAutoAc > 0 && savedAutoAc < AcProfileValues.Length)
                    {
                        savedPower = AcProfileValues[savedAutoAc];
                    }
                    else
                    {
                        savedPower = GetInt(key, "Power_AC", -1, 0x00, 0x06);
                        if (savedPower == -1) savedPower = GetInt(hklmKey, "Power_AC", -1, 0x00, 0x06);
                        if (savedPower == -1) savedPower = GetInt(key, "Power", -1, 0x00, 0x06);
                        if (savedPower == -1) savedPower = GetInt(hklmKey, "Power", 0x01, 0x00, 0x06);
                    }
                    if (!onBatteryStartup && savedPower == 0x06) savedPower = 0x01;
                }

                if (savedPower < 0 || savedPower > 6) savedPower = 0x01;
                if (!onBatteryStartup && savedPower == 0x06) savedPower = 0x01;

                if (onBatteryStartup)
                {
                    _btnPerform.Enabled = false;
                    _btnTurbo.Enabled = false;
                    _btnEco.Enabled = true;
                    _trayPowerPerf.Enabled = false;
                    _trayPowerTurbo.Enabled = false;
                    _trayPowerEco.Enabled = true;
                }
                else
                {
                    _btnPerform.Enabled = true;
                    _btnTurbo.Enabled = true;
                    _btnEco.Enabled = false;
                    _trayPowerPerf.Enabled = true;
                    _trayPowerTurbo.Enabled = true;
                    _trayPowerEco.Enabled = false;
                }

                var (powerMode, powerBtn) = savedPower switch
                {
                    0x00 => ((byte)0x00, _btnQuiet),
                    0x04 => ((byte)0x04, _btnPerform),
                    0x05 => ((byte)0x05, _btnTurbo),
                    0x06 => ((byte)0x06, _btnEco),
                    _ => ((byte)0x01, _btnBalanced)
                };
                ApplyPowerMode(powerMode, powerBtn);

                int savedFan = -1;
                if (onBatteryStartup)
                {
                    if (savedAutoFanBat > 0 && savedAutoFanBat < BatteryFanValues.Length)
                    {
                        savedFan = BatteryFanValues[savedAutoFanBat];
                    }
                    else
                    {
                        savedFan = GetInt(key, "Fan_Battery", -1, 0x01, 0x03);
                        if (savedFan == -1) savedFan = GetInt(hklmKey, "Fan_Battery", -1, 0x01, 0x03);
                        if (savedFan == -1) savedFan = GetInt(key, "Fan", -1, 0x01, 0x03);
                        if (savedFan == -1) savedFan = GetInt(hklmKey, "Fan", 0x01, 0x01, 0x03);
                    }
                }
                else
                {
                    if (savedAutoFanAc > 0 && savedAutoFanAc < AcFanValues.Length)
                    {
                        savedFan = AcFanValues[savedAutoFanAc];
                    }
                    else
                    {
                        savedFan = GetInt(key, "Fan_AC", -1, 0x01, 0x03);
                        if (savedFan == -1) savedFan = GetInt(hklmKey, "Fan_AC", -1, 0x01, 0x03);
                        if (savedFan == -1) savedFan = GetInt(key, "Fan", -1, 0x01, 0x03);
                        if (savedFan == -1) savedFan = GetInt(hklmKey, "Fan", 0x01, 0x01, 0x03);
                    }
                }

                if (savedFan < 1 || savedFan > 3) savedFan = 0x01;

                string fanSuffix = onBatteryStartup ? "Battery" : "AC";
                int savedFanSpeedCpu = GetInt(key, $"FanSpeedCpu{fanSuffix}", -1, 10, 100);
                if (savedFanSpeedCpu == -1) savedFanSpeedCpu = GetInt(key, "FanSpeedCpu", 50, 10, 100);

                int savedFanSpeedGpu = GetInt(key, $"FanSpeedGpu{fanSuffix}", -1, 10, 100);
                if (savedFanSpeedGpu == -1) savedFanSpeedGpu = GetInt(key, "FanSpeedGpu", 50, 10, 100);

                int savedFanSpeedSys = GetInt(key, $"FanSpeedSys{fanSuffix}", -1, 10, 100);
                if (savedFanSpeedSys == -1) savedFanSpeedSys = GetInt(key, "FanSpeedSys", 50, 10, 100);

                _cpuFanSlider.Value = savedFanSpeedCpu;
                _gpuFanSlider.Value = savedFanSpeedGpu;
                if (_sysFanSlider != null) _sysFanSlider.Value = savedFanSpeedSys;
                _lblCpuFanSpeedHdr.Text = $"CPU FAN: {savedFanSpeedCpu}%";
                _lblGpuFanSpeedHdr.Text = $"GPU FAN: {savedFanSpeedGpu}%";
                if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {savedFanSpeedSys}%";

                var (fanMode, fanBtn) = savedFan switch
                {
                    0x02 => ((byte)0x02, _btnMaxFan),
                    0x03 => ((byte)0x03, _btnCustomFan),
                    _ => ((byte)0x01, _btnAutoFan)
                };

                var loadedCpu = LoadCurveFromRegistry("CpuCurve");
                var loadedGpu = LoadCurveFromRegistry("GpuCurve");
                if (loadedCpu != null) _cpuCurvePoints = loadedCpu;
                if (loadedGpu != null) _gpuCurvePoints = loadedGpu;

                int savedCurveEnabled = GetInt(key, "FanCurveEnabled", 0, 0, 1);
                if (savedCurveEnabled == 1 && fanMode == 0x03)
                    _fanCurveEnabled = true;

                ApplyFanMode(fanMode, fanBtn);

                if (fanMode == 0x03)
                {
                    if (_fanCurveEnabled)
                    {
                        ApplyFanCurve(forceHardwareApply: true);
                    }
                    else
                    {
                        _cpuFanSlider.Value = savedFanSpeedCpu;
                        _gpuFanSlider.Value = savedFanSpeedGpu;
                        if (_sysFanSlider != null) _sysFanSlider.Value = savedFanSpeedSys;
                        _lblCpuFanSpeedHdr.Text = $"CPU FAN: {savedFanSpeedCpu}%";
                        _lblGpuFanSpeedHdr.Text = $"GPU FAN: {savedFanSpeedGpu}%";
                        if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {savedFanSpeedSys}%";
                        Task.Run(() =>
                        {
                            _wmi.SetFanBehavior(0x03, applyCustomSpeeds: true);
                            _wmi.SetCpuFanSpeed((byte)savedFanSpeedCpu);
                            _wmi.SetGpuFanSpeed((byte)savedFanSpeedGpu);
                            if (_capabilities.HasThirdFan) _wmi.SetSystemFanSpeed((byte)savedFanSpeedSys);
                        });
                    }
                }

                int clampedMode = Math.Clamp(savedRgbMode, 0, 7);
                _isApplyingRgbMode = true;
                try
                {
                    _rgbDropDown.SelectedIndex = clampedMode;
                    UpdateRgbControls(clampedMode);
                    CheckRgbTrayFromMode(clampedMode);
                }
                finally
                {
                    _isApplyingRgbMode = false;
                }

                if (onBatteryStartup)
                {
                    Task.Run(() => _wmi.TurnOffBacklight());
                    EnforceBacklightOffOnBattery();
                }
                else
                {
                    if (clampedMode == 0)
                    {
                        Task.Run(() => _wmi.SetStaticColor((byte)savedR, (byte)savedG, (byte)savedB, (byte)savedBrightness));
                    }
                    else
                    {
                        ApplyRgbModeFromDropdown(clampedMode);
                    }
                }

                bool limitEnabled = GetInt(key, "BatteryLimit", 0, 0, 1) == 1;

                _isUpdatingBattery = true;
                _switchBatteryLimit.Checked = limitEnabled;
                _lblBatteryStatus.Text = limitEnabled ? "Limit to 80% (Health)" : "Full Charge (100%)";
                CheckTrayItem(limitEnabled ? _trayBatteryLimit80 : _trayBatteryLimit100, _trayBatteryLimit80, _trayBatteryLimit100);
                _isUpdatingBattery = false;

                Task.Run(() =>
                {
                    try
                    {
                        bool supported = _wmi.IsBatteryControlSupported();
                        if (supported)
                        {
                            _wmi.SetBatteryChargeLimit(limitEnabled);
                        }
                        else
                        {
                            Action disableAction = () =>
                            {
                                if (IsDisposed) return;
                                _isUpdatingBattery = true;
                                _switchBatteryLimit.Checked = false;
                                _switchBatteryLimit.Enabled = false;
                                _lblBatteryStatus.Text = "Not Supported";
                                _lblBatteryStatus.ForeColor = SubHeaderColor;
                                _trayBatteryLimit80.Enabled = false;
                                _trayBatteryLimit100.Enabled = false;
                                _trayBatteryMenu.Enabled = false;
                                _isUpdatingBattery = false;
                            };

                            if (IsHandleCreated)
                            {
                                try { BeginInvoke(disableAction); } catch { }
                            }
                            else
                            {
                                HandleCreated += (s, e) => { try { BeginInvoke(disableAction); } catch { } };
                            }
                        }
                    }
                    catch { }
                });

                int savedLcdOd = GetInt(key, "LcdOverdrive", -1, 0, 1);
                if (savedLcdOd == -1) savedLcdOd = GetInt(hklmKey, "LcdOverdrive", 1, 0, 1);
                bool lcdOdEnabled = (savedLcdOd == 1);
                _switchLcdOverdrive.Checked = lcdOdEnabled;
                if (_trayLcdOverdrive != null) _trayLcdOverdrive.Checked = lcdOdEnabled;
                _lblLcdOverdriveStatus.Text = lcdOdEnabled ? "LCD Overdrive (3ms Enabled)" : "LCD Overdrive (Disabled)";
                _lblLcdOverdriveStatus.ForeColor = lcdOdEnabled ? AccentColor : Color.White;
                Task.Run(() => _wmi.SetLcdOverdrive(lcdOdEnabled));
            }
            catch { }
        }

        #endregion

        #region Telemetry & Power Rules

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (_isClosing || IsDisposed || !IsHandleCreated) return;

            if (e.Mode == PowerModes.Resume)
            {
                var immediateLine = SystemInformation.PowerStatus.PowerLineStatus;
                if (_backlightMgr.IsOnBattery(immediateLine) && !_manualBacklightOnBattery)
                {
                    EnforceBacklightOffOnBattery();
                }

                try { BeginInvoke(new Action(async () => await ResyncAfterResume())); }
                catch { }
            }
            else if (e.Mode == PowerModes.StatusChange)
            {
                BeginInvoke(new Action(() =>
                {
                    if (_isClosing || IsDisposed) return;
                    var line = SystemInformation.PowerStatus.PowerLineStatus;
                    bool isAc = !_backlightMgr.IsOnBattery(line);
                    _isPluggedIn = isAc;
                    if (_gameSync?.ActiveGameExe == null)
                    {
                        ApplyPowerRules(isAc);
                    }
                    ScheduleRgbRestoreWatchdog();
                }));
            }
        }

        private async Task ResyncAfterResume()
        {
            if (_isResyncing) return;
            _isResyncing = true;

            try
            {
                _lastCurveCpuSpeed = -1;
                _lastCurveGpuSpeed = -1;

                var initialLine = SystemInformation.PowerStatus.PowerLineStatus;
                if (_backlightMgr.IsOnBattery(initialLine) && !_manualBacklightOnBattery)
                {
                    EnforceBacklightOffOnBattery();
                }

                // Wait 2.5s for ACPI, display, and thermal drivers to stabilize after resume
                await Task.Delay(2500);
                if (_isClosing || IsDisposed) return;

                var lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
                bool onBattery = _backlightMgr.IsOnBattery(lineStatus);
                bool isPluggedIn = lineStatus == PowerLineStatus.Online || (!onBattery && _isPluggedIn == true);

                _isPluggedIn = isPluggedIn;
                _pendingPluggedIn = isPluggedIn;
                _powerLineStableTicks = 2;

                // Re-apply power & fan rules based on current live AC/Battery state (unless Game Sync is active)
                if (_gameSync?.ActiveGameExe == null)
                {
                    ApplyPowerRules(isPluggedIn);
                    ApplyFanRules(isPluggedIn);
                }

                // Re-apply display refresh rate
                int targetHz = _activeDisplayBtn == _btn60Hz ? 60 : _maxHz;
                SetRefreshRate(targetHz);

                // Re-apply battery charge limit
                if (_switchBatteryLimit.Checked)
                {
                    await Task.Run(() => _wmi.SetBatteryChargeLimit(true));
                }

                // Re-sync RGB backlight safely based on power state (unless Game Sync is active)
                if (_gameSync?.ActiveGameExe == null)
                {
                    if (onBattery)
                    {
                        if (!_manualBacklightOnBattery)
                        {
                            _brightnessSlider.Value = 0;
                            _lblBrightHdr.Text = "BRIGHTNESS: 0% (Off on Battery)";
                            EnforceBacklightOffOnBattery();
                        }
                    }
                    else
                    {
                        // On AC: ensure RGB backlight is ON if it was originally on
                        int targetBright = _backlightMgr.SavedAcBrightness;
                        if (targetBright > 0)
                        {
                            _brightnessSlider.Value = targetBright;
                            _lblBrightHdr.Text = $"BRIGHTNESS: {targetBright}%";
                            await Task.Run(() =>
                            {
                                _wmi.SetBrightness((byte)targetBright);
                                if (_rgbDropDown != null)
                                    _wmi.ApplyLightingMode(_rgbDropDown.SelectedIndex);
                            });
                        }
                    }
                }
                ScheduleRgbRestoreWatchdog();
            }
            catch (Exception ex) { Program.Report(ex, false); }
            finally
            {
                _isResyncing = false;
            }
        }

        internal static bool? DebouncePowerLine(PowerLineStatus line, bool? current, ref bool? pending, ref int ticks)
        {
            if (line == PowerLineStatus.Unknown)
            {
                pending = null;
                ticks = 0;
                return current;
            }

            bool pluggedIn = line == PowerLineStatus.Online;

            if (pending != pluggedIn)
            {
                pending = pluggedIn;
                ticks = 1;
            }
            else if (ticks < 2)
            {
                ticks++;
            }

            return ticks >= 2 ? pluggedIn : current;
        }

        private void UpdateTelemetry(object? sender, EventArgs e)
        {
            try { UpdateTelemetryCore(); }
            catch (Exception ex) { Program.Report(ex, false); }
        }

        private void UpdateTelemetryCore()
        {
            if (_isClosing || IsDisposed) return;

            bool? confirmed = DebouncePowerLine(SystemInformation.PowerStatus.PowerLineStatus,
                                                _isPluggedIn, ref _pendingPluggedIn, ref _powerLineStableTicks);

            if (confirmed != _isPluggedIn && !_isResyncing)
            {
                try
                {
                    if (_gameSync?.ActiveGameExe == null)
                    {
                        ApplyPowerRules(confirmed == true);
                    }
                    ScheduleRgbRestoreWatchdog();
                }
                catch (Exception ex) { Program.Report(ex, false); }
                finally { _isPluggedIn = confirmed; }
            }

            var lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
            bool onBattery = _backlightMgr.IsOnBattery(lineStatus);
            bool isConnectedToCharger = !onBattery && (lineStatus == PowerLineStatus.Online || _isPluggedIn == true);

            int targetInterval = onBattery ? 5000 : 2000;

            if (_timer.Interval != targetInterval)
            {
                _timer.Interval = targetInterval;
            }
            if (_gameSync?.ActiveGameExe == null)
            {
                if (onBattery && !_manualBacklightOnBattery)
                {
                    if (_brightnessSlider.Value != 0)
                    {
                        _brightnessSlider.Value = 0;
                        _lblBrightHdr.Text = "BRIGHTNESS: 0% (Off on Battery)";
                        Task.Run(() => _wmi.TurnOffBacklight());
                    }
                }
                else if (isConnectedToCharger && _backlightMgr.SavedAcBrightness > 0)
                {
                    AcerServiceClient.ResetConnectionState();
                    if (_brightnessSlider.Value == 0)
                    {
                        int targetBright = _backlightMgr.SavedAcBrightness;
                        _brightnessSlider.Value = targetBright;
                        _lblBrightHdr.Text = $"BRIGHTNESS: {targetBright}%";
                        Task.Run(() =>
                        {
                            _wmi.SetBrightness((byte)targetBright);
                            if (_rgbDropDown != null)
                                _wmi.ApplyLightingMode(_rgbDropDown.SelectedIndex);
                        });
                    }
                }
            }

            if (Interlocked.CompareExchange(ref _telemetryRunning, 1, 0) != 0)
                return;

            Task.Run(() =>
            {
                try
                {
                    int cpuTemp = _wmi.CpuTemp;
                    int cpuRpm = _wmi.CpuFanRpm;
                    int sysRpm = _capabilities.HasThirdFan ? _wmi.SystemFanRpm : 0;
                    bool gpuAsleep = GpuPowerMonitor.IsGpuAsleep();

                    int gpuTemp = 0;
                    int gpuRpm = 0;
                    if (!gpuAsleep)
                    {
                        gpuTemp = _wmi.GpuTemp;
                        gpuRpm = _wmi.GpuFanRpm;
                    }

                    var (cpuLoad, gpuLoad) = _pdhMonitor.Sample();

                    if (_isClosing || IsDisposed || !IsHandleCreated) return;

                    BeginInvoke(new Action(() =>
                    {
                        if (_isClosing || IsDisposed) return;

                        _cpuTemp = cpuTemp;
                        _gpuTemp = gpuTemp;

                        if (cpuTemp <= 0)
                        {
                            _cpuSensorMisses++;
                            if (_cpuSensorMisses >= 3)
                            {
                                if (_fanCurveEnabled || GetCurrentFanByte() == 0x03)
                                {
                                    ApplyFanMode(0x01, _btnAutoFan);
                                }
                            }
                        }
                        else
                        {
                            _cpuSensorMisses = 0;
                        }

                        if ((DateTime.UtcNow - _lastFirmwareReassert).TotalSeconds >= 30)
                        {
                            _lastFirmwareReassert = DateTime.UtcNow;
                            byte pMode = GetCurrentPowerByte();
                            byte fMode = GetCurrentFanByte();
                            Task.Run(() =>
                            {
                                _wmi.SetPowerMode(pMode);
                                if (fMode != 0x03)
                                {
                                    _wmi.SetFanBehavior(fMode, false);
                                }
                            });
                        }

                        _lblCpuTemp.Text = _cpuTemp > 0 ? $"{_cpuTemp}°C" : "--°C";
                        _lblGpuTemp.Text = _gpuTemp > 0 ? $"{_gpuTemp}°C" : (gpuAsleep ? "Asleep" : "--°C");
                        _lblCpuTemp.ForeColor = TempColor(_cpuTemp);
                        _lblGpuTemp.ForeColor = TempColor(_gpuTemp);

                        _lblCpuRpm.Text = cpuRpm > 0 ? $"{cpuRpm} RPM" : "-- RPM";
                        _lblGpuRpm.Text = gpuRpm > 0 ? $"{gpuRpm} RPM" : "-- RPM";
                        if (_lblSysFanRpm != null)
                        {
                            _lblSysFanRpm.Text = sysRpm > 0 ? $"{sysRpm} RPM" : "-- RPM";
                        }

                        _cpuHistoryGraph?.PushSample(_cpuTemp > 0 ? _cpuTemp : null, cpuLoad);
                        _gpuHistoryGraph?.PushSample(_gpuTemp > 0 ? _gpuTemp : null, gpuAsleep ? null : gpuLoad);

                        string trayText = $"Predator Control\nCPU: {(_cpuTemp > 0 ? $"{_cpuTemp}°C" : "N/A")}  GPU: {(_gpuTemp > 0 ? $"{_gpuTemp}°C" : "N/A")}";
                        _trayIcon.Text = trayText.Length > 63 ? trayText.Substring(0, 63) : trayText;

                        if (_fanCurveForm != null && !_fanCurveForm.IsDisposed)
                            _fanCurveForm.UpdateTemps(_cpuTemp, _gpuTemp);

                        ApplyFanCurve();
                    }));
                }
                catch (Exception ex)
                {
                    Program.Report(ex, false);
                }
                finally
                {
                    Interlocked.Exchange(ref _telemetryRunning, 0);
                }
            });
        }

        private void ApplyFanCurve(bool forceHardwareApply = false)
        {
            if (!_fanCurveEnabled) return;
            if (GetCurrentFanByte() != 0x03) return;  
            if (GetCurrentFanLock() != null) return;

            if (_cpuTemp <= 0)
            {
                return;
            }

            // 3-sample rolling average temperature damping
            _cpuDampingQueue.Enqueue(_cpuTemp);
            while (_cpuDampingQueue.Count > 3) _cpuDampingQueue.Dequeue();
            int currentCpuTemp = (int)Math.Round(_cpuDampingQueue.Average());

            int targetCpuSpeed = _cpuCurveFollower.Update(currentCpuTemp, _cpuCurvePoints);
            targetCpuSpeed = Math.Clamp(targetCpuSpeed, 10, 100);
            bool cpuChanged = forceHardwareApply || (_lastCurveCpuSpeed != targetCpuSpeed);

            if (cpuChanged)
            {
                _lastCurveCpuSpeed = targetCpuSpeed;
                _lastCurveCpuTemp = currentCpuTemp;
                _cpuFanSlider.Value = targetCpuSpeed;
                _lblCpuFanSpeedHdr.Text = $"CPU FAN: {targetCpuSpeed}%";
            }

            bool gpuChanged = false;
            int targetGpuSpeed = 0;
            if (_gpuTemp > 0 && !GpuPowerMonitor.IsGpuAsleep())
            {
                _gpuDampingQueue.Enqueue(_gpuTemp);
                while (_gpuDampingQueue.Count > 3) _gpuDampingQueue.Dequeue();
                int currentGpuTemp = (int)Math.Round(_gpuDampingQueue.Average());

                targetGpuSpeed = _gpuCurveFollower.Update(currentGpuTemp, _gpuCurvePoints);
                targetGpuSpeed = Math.Clamp(targetGpuSpeed, 10, 100);
                gpuChanged = forceHardwareApply || (_lastCurveGpuSpeed != targetGpuSpeed);

                if (gpuChanged)
                {
                    _lastCurveGpuSpeed = targetGpuSpeed;
                    _lastCurveGpuTemp = currentGpuTemp;
                    _gpuFanSlider.Value = targetGpuSpeed;
                    _lblGpuFanSpeedHdr.Text = $"GPU FAN: {targetGpuSpeed}%";
                }
            }
            else
            {
                _gpuDampingQueue.Clear();
                _gpuCurveFollower.Reset();
                _lastCurveGpuSpeed = -1;
                _lastCurveGpuTemp = -1;
                _lblGpuFanSpeedHdr.Text = "GPU FAN: Asleep (D3Cold)";
            }

            bool sysChanged = false;
            int targetSysSpeed = 0;
            if (_capabilities.HasThirdFan && _sysFanSlider != null)
            {
                targetSysSpeed = _sysCurveFollower.Update(currentCpuTemp, _cpuCurvePoints);
                targetSysSpeed = Math.Clamp(targetSysSpeed, 10, 100);
                sysChanged = forceHardwareApply || (_lastCurveSysSpeed != targetSysSpeed);

                if (sysChanged)
                {
                    _lastCurveSysSpeed = targetSysSpeed;
                    _sysFanSlider.Value = targetSysSpeed;
                    if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {targetSysSpeed}%";
                }
            }

            if (cpuChanged || gpuChanged || sysChanged || forceHardwareApply)
            {
                int cSpeed = cpuChanged ? targetCpuSpeed : _lastCurveCpuSpeed;
                if (cSpeed <= 0) cSpeed = targetCpuSpeed;

                int gSpeed = gpuChanged ? targetGpuSpeed : _lastCurveGpuSpeed;
                int sSpeed = sysChanged ? targetSysSpeed : _lastCurveSysSpeed;

                Task.Run(() =>
                {
                    _wmi.SetFanBehavior(0x03, applyCustomSpeeds: false);
                    if (cSpeed > 0) _wmi.SetCpuFanSpeed((byte)cSpeed);
                    if (gSpeed > 0 && _gpuTemp > 0 && !GpuPowerMonitor.IsGpuAsleep()) _wmi.SetGpuFanSpeed((byte)gSpeed);
                    if (_capabilities.HasThirdFan && sSpeed > 0) _wmi.SetSystemFanSpeed((byte)sSpeed);
                });
            }
        }

        internal static int InterpolateCurve(List<Point>? curve, int temp)
        {
            if (curve == null || curve.Count == 0) return 50;
            if (temp <= curve[0].X) return Math.Clamp(curve[0].Y, 10, 100);
            if (temp >= curve[^1].X) return Math.Clamp(curve[^1].Y, 10, 100);

            for (int i = 0; i < curve.Count - 1; i++)
            {
                if (temp >= curve[i].X && temp <= curve[i + 1].X)
                {
                    float span = curve[i + 1].X - curve[i].X;
                    if (span == 0) return Math.Clamp(curve[i].Y, 10, 100);
                    float t = (temp - curve[i].X) / span;
                    int result = (int)Math.Round(curve[i].Y + t * (curve[i + 1].Y - curve[i].Y));
                    return Math.Clamp(result, 10, 100);
                }
            }
            return Math.Clamp(curve[^1].Y, 10, 100);
        }

        private void ApplyPowerRules(bool pluggedIn)
        {
            if (pluggedIn)
            {
                _btnPerform.Enabled = true;
                _btnTurbo.Enabled = true;
                _btnEco.Enabled = false;
                _trayPowerPerf.Enabled = true;
                _trayPowerTurbo.Enabled = true;
                _trayPowerEco.Enabled = false;

                int acIdx = _cboAcProfile.SelectedIndex;
                if (acIdx > 0 && acIdx < AcProfileValues.Length)
                {
                    byte mode = AcProfileValues[acIdx];
                    ApplyPowerMode(mode, PowerByteToBtn(mode));
                }
                else
                {
                    byte mode = 0x01;
                    try
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\PredatorControl");
                        using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PredatorControl");
                        int savedAc = GetInt(key, "Power_AC", -1, 0x00, 0x06);
                        if (savedAc == -1) savedAc = GetInt(hklm, "Power_AC", -1, 0x00, 0x06);
                        if (savedAc == -1) savedAc = GetInt(key, "Power", -1, 0x00, 0x06);
                        if (savedAc == -1) savedAc = GetInt(hklm, "Power", 0x01, 0x00, 0x06);
                        if (savedAc >= 0 && savedAc != 0x06) mode = (byte)savedAc;
                    }
                    catch { }
                    ApplyPowerMode(mode, PowerByteToBtn(mode));
                }
            }
            else
            {
                _btnPerform.Enabled = false;
                _btnTurbo.Enabled = false;
                _btnEco.Enabled = true;
                _trayPowerPerf.Enabled = false;
                _trayPowerTurbo.Enabled = false;
                _trayPowerEco.Enabled = true;

                int batIdx = _cboBatteryProfile.SelectedIndex;
                if (batIdx > 0 && batIdx < BatteryProfileValues.Length)
                {
                    byte mode = BatteryProfileValues[batIdx];
                    ApplyPowerMode(mode, PowerByteToBtn(mode));
                }
                else
                {
                    byte mode = 0x01;
                    try
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\PredatorControl");
                        using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PredatorControl");
                        int savedBat = GetInt(key, "Power_Battery", -1, 0x00, 0x06);
                        if (savedBat == -1) savedBat = GetInt(hklm, "Power_Battery", -1, 0x00, 0x06);
                        if (savedBat == -1) savedBat = GetInt(key, "Power", -1, 0x00, 0x06);
                        if (savedBat == -1) savedBat = GetInt(hklm, "Power", 0x01, 0x00, 0x06);
                        if (savedBat >= 0 && savedBat != 0x04 && savedBat != 0x05) mode = (byte)savedBat;
                    }
                    catch { }
                    ApplyPowerMode(mode, PowerByteToBtn(mode));
                }
            }

            ApplyFanRules(pluggedIn);
        }

        private void ApplyFanRules(bool pluggedIn)
        {
            var cbo = pluggedIn ? _cboAcFan : _cboBatteryFan;
            int idx = cbo.SelectedIndex;
            byte fanMode = 0x01;

            if (idx > 0 && idx < FanProfileValues.Length)
            {
                fanMode = FanProfileValues[idx];
            }
            else
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\PredatorControl");
                    using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PredatorControl");
                    string fanKeyName = pluggedIn ? "Fan_AC" : "Fan_Battery";
                    int savedFan = GetInt(key, fanKeyName, -1, 0x01, 0x03);
                    if (savedFan == -1) savedFan = GetInt(hklm, fanKeyName, -1, 0x01, 0x03);
                    if (savedFan == -1) savedFan = GetInt(key, "Fan", -1, 0x01, 0x03);
                    if (savedFan == -1) savedFan = GetInt(hklm, "Fan", 0x01, 0x01, 0x03);
                    if (savedFan >= 1 && savedFan <= 3) fanMode = (byte)savedFan;
                }
                catch { }
            }

            // Check if user has fan curve enabled currently or in registry
            bool curveActive = _fanCurveEnabled;
            if (!curveActive)
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");
                    curveActive = GetInt(key, "FanCurveEnabled", 0, 0, 1) == 1;
                }
                catch { }
            }

            if (fanMode == 0x03)
            {
                _fanCurveEnabled = curveActive;
                _lastCurveCpuSpeed = -1;
                _lastCurveGpuSpeed = -1;
                _lastCurveSysSpeed = -1;
            }

            ApplyFanMode(fanMode, FanByteToBtn(fanMode));

            if (fanMode == 0x03)
            {
                if (_fanCurveEnabled)
                {
                    HighlightBtn(_btnFanCurve, ref _activeCustomSubBtn);
                    _cpuFanSlider.Enabled = false;
                    _gpuFanSlider.Enabled = false;
                    if (_sysFanSlider != null) _sysFanSlider.Enabled = false;
                    ApplyFanCurve(forceHardwareApply: true);
                }
                else
                {
                    HighlightBtn(_btnFixedSpeed, ref _activeCustomSubBtn);
                    bool isLocked = GetCurrentFanLock() != null;
                    _cpuFanSlider.Enabled = !isLocked;
                    _gpuFanSlider.Enabled = !isLocked;
                    if (_sysFanSlider != null) _sysFanSlider.Enabled = !isLocked;

                    string suffix = pluggedIn ? "AC" : "Battery";
                    try
                    {
                        using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");
                        int cpuSpeed = GetInt(key, $"FanSpeedCpu{suffix}", -1, 10, 100);
                        if (cpuSpeed == -1) cpuSpeed = GetInt(key, "FanSpeedCpu", _cpuFanSlider.Value, 10, 100);

                        int gpuSpeed = GetInt(key, $"FanSpeedGpu{suffix}", -1, 10, 100);
                        if (gpuSpeed == -1) gpuSpeed = GetInt(key, "FanSpeedGpu", _gpuFanSlider.Value, 10, 100);

                        int sysSpeed = GetInt(key, $"FanSpeedSys{suffix}", -1, 10, 100);
                        if (sysSpeed == -1) sysSpeed = GetInt(key, "FanSpeedSys", _sysFanSlider?.Value ?? 50, 10, 100);

                        _cpuFanSlider.Value = cpuSpeed;
                        _gpuFanSlider.Value = gpuSpeed;
                        if (_sysFanSlider != null) _sysFanSlider.Value = sysSpeed;
                        _lblCpuFanSpeedHdr.Text = $"CPU FAN: {cpuSpeed}%";
                        _lblGpuFanSpeedHdr.Text = $"GPU FAN: {gpuSpeed}%";
                        if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {sysSpeed}%";
                        int tCpu = cpuSpeed;
                        int tGpu = gpuSpeed;
                        int tSys = sysSpeed;
                        Task.Run(() =>
                        {
                            _wmi.SetFanBehavior(0x03, applyCustomSpeeds: true);
                            _wmi.SetCpuFanSpeed((byte)tCpu);
                            _wmi.SetGpuFanSpeed((byte)tGpu);
                            if (_capabilities.HasThirdFan) _wmi.SetSystemFanSpeed((byte)tSys);
                        });
                    }
                    catch { }
                }
            }
        }

        private void SaveFanSpeedForProfile(string fan, int value)
        {
            if (_isPluggedIn == true)
            {
                SaveState($"FanSpeed{fan}AC", value);
                SaveStateToHklm($"FanSpeed{fan}AC", value);
            }
            else if (_isPluggedIn == false)
            {
                SaveState($"FanSpeed{fan}Battery", value);
                SaveStateToHklm($"FanSpeed{fan}Battery", value);
            }
        }

        private static Color TempColor(int temp) => temp switch
        {
            <= 0 => Color.FromArgb(107, 114, 128),
            < 55 => Color.FromArgb(0, 200, 160),
            < 72 => Color.FromArgb(255, 220, 50),
            < 87 => Color.FromArgb(255, 140, 0),
            _ => Color.FromArgb(255, 60, 60)
        };

        #endregion

        #region UI Helpers

        private byte GetMappedSpeed() => (byte)Math.Clamp(Math.Round(_speedSlider.Value * 9.0 / 100.0), 1, 9);

        private void HighlightBtn(PredatorButton btn, ref PredatorButton? tracker)
        {
            if (tracker != null) tracker.IsActive = false;
            btn.IsActive = true;
            tracker = btn;
        }

        private static void CheckTrayItem(ToolStripMenuItem active, params ToolStripMenuItem[] group)
        {
            foreach (var item in group) item.Checked = false;
            active.Checked = true;
        }

        private void ShowApp()
        {
            _allowVisible = true;
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
            this.Activate();
            Updater.ShowPendingNotes(this);
        }

        private void HideApp()
        {
            this.Hide();
            _allowVisible = false;
        }

        private void ToggleWindowVisibility()
        {
            if (this.Visible && this.WindowState != FormWindowState.Minimized)
            {
                HideApp();
            }
            else
            {
                ShowApp();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_isClosing && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideApp();
            }
            else
            {
                _isClosing = true;
                lock (_backlightEnforceLock)
                {
                    try { _backlightEnforceCts?.Cancel(); } catch { }
                    try { _backlightEnforceCts?.Dispose(); } catch { }
                    _backlightEnforceCts = null;
                }
                lock (_rgbRestoreLock)
                {
                    try { _rgbRestoreCts?.Cancel(); } catch { }
                    try { _rgbRestoreCts?.Dispose(); } catch { }
                    _rgbRestoreCts = null;
                }
                UnregisterPowerNotifications();
                _trayIcon.Visible = false;
                try { _trayIcon.Dispose(); } catch { }
                try { _trayMenu.Dispose(); } catch { }
                _timer.Stop();
                try { _timer.Dispose(); } catch { }

                int waited = 0;
                while (Interlocked.CompareExchange(ref _telemetryRunning, 0, 0) == 1 && waited < 40)
                {
                    Thread.Sleep(25);
                    Application.DoEvents();
                    waited++;
                }

                _gameSync.GameDetected -= OnGameDetected;
                _gameSync.GameExited -= OnGameExited;
                _gameSync.Dispose();
                _wmi.Dispose();
                try { _keyboardHook?.Dispose(); } catch { }
                try { _wmiHotkeyWatcher?.Dispose(); } catch { }
                try { _colorPicker.Dispose(); } catch { }
                try { _fanCurveForm?.Dispose(); } catch { }
                base.OnFormClosing(e);
            }
        }

        private const string StartupTaskName = "PredatorControl";
        private const string StartupPowerTaskName = "PredatorControlPowerBoot";

        private static bool IsStartupEnabled()
        {
            return RunSchtasks($"/Query /TN \"{StartupTaskName}\"") == 0;
        }

        internal static bool EnsureBootPowerTaskRegistered(string? customExe = null)
        {
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = string.IsNullOrEmpty(localApp)
                    ? Path.GetTempPath()
                    : Path.Combine(localApp, "PredatorControl");
                Directory.CreateDirectory(dir);

                string bootXmlPath = Path.Combine(dir, $"startup_boot_{Guid.NewGuid():N}.xml");
                try
                {
                    File.WriteAllText(bootXmlPath, BuildBootPowerTaskXml(customExe), Encoding.Unicode);
                    return RunSchtasks($"/Create /TN \"{StartupPowerTaskName}\" /XML \"{bootXmlPath}\" /F") == 0;
                }
                finally
                {
                    try { File.Delete(bootXmlPath); } catch { }
                }
            }
            catch { return false; }
        }

        internal static void EnsureStartupTaskUpdated()
        {
            try
            {
                EnsureBootPowerTaskRegistered();
                if (IsStartupEnabled())
                {
                    SetStartupEnabled(true);
                }
            }
            catch { }
        }

        private static bool SetStartupEnabled(bool enable)
        {
            RemoveLegacyRunKey();

            if (!enable)
            {
                return RunSchtasks($"/Delete /TN \"{StartupTaskName}\" /F") == 0 || !IsStartupEnabled();
            }

            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = string.IsNullOrEmpty(localApp)
                ? Path.GetTempPath()
                : Path.Combine(localApp, "PredatorControl");
            try { Directory.CreateDirectory(dir); } catch { }
            string xmlPath = Path.Combine(dir, $"startup_{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(xmlPath, BuildStartupTaskXml(), Encoding.Unicode);
                bool resMain = RunSchtasks($"/Create /TN \"{StartupTaskName}\" /XML \"{xmlPath}\" /F") == 0;

                EnsureBootPowerTaskRegistered();

                return resMain;
            }
            catch { return false; }
            finally
            {
                try { File.Delete(xmlPath); } catch { }
            }
        }

        internal static string BuildBootPowerTaskXml(string? customExe = null)
        {
            string exe = System.Security.SecurityElement.Escape(customExe ?? (Environment.ProcessPath ?? Application.ExecutablePath));

            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Instantly applies Predator Control power modes at machine boot without waiting for GUI.</Description>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger>
      <Enabled>true</Enabled>
    </BootTrigger>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>-apply-power</Arguments>
    </Exec>
  </Actions>
</Task>";
        }

        internal static string BuildStartupTaskXml(string? customUser = null, string? customExe = null)
        {
            string user = System.Security.SecurityElement.Escape(customUser ?? WindowsIdentity.GetCurrent().Name);
            string exe = System.Security.SecurityElement.Escape(customExe ?? (Environment.ProcessPath ?? Application.ExecutablePath));

            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Starts Predator Control at logon with administrator rights.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>-hidden</Arguments>
    </Exec>
  </Actions>
</Task>";
        }

        private static int RunSchtasks(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p == null) return -1;
                p.WaitForExit(15000);
                if (!p.HasExited)
                {
                    try { p.Kill(); } catch { }
                    return -1;
                }
                return p.ExitCode;
            }
            catch { return -1; }
        }

        private static void RemoveLegacyRunKey()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue(StartupTaskName, false);
            }
            catch { }
        }

        private static void MigrateLegacyStartup()
        {
            bool hadLegacy;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                hadLegacy = key?.GetValue(StartupTaskName) != null;
            }
            catch { return; }

            if (hadLegacy && !IsStartupEnabled())
                SetStartupEnabled(true);
            else if (hadLegacy)
                RemoveLegacyRunKey();
        }

        #endregion
    }
}
