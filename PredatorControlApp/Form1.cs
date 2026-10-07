using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    internal sealed class TitleBarButton : Button
    {
        public TitleBarButton()
        {
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            UseMnemonic = false;
        }

        protected override bool ShowFocusCues => false;

        protected override void OnGotFocus(EventArgs e)
        {
            Parent?.Focus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0007) // WM_SETFOCUS
            {
                return;
            }
            base.WndProc(ref m);
        }
    }

    [SupportedOSPlatform("windows")]
    public partial class Form1 : Form
    {
        #region Win32 Interop — Memory Management

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        private static void TrimWorkingSet()
        {
            try
            {
                GC.Collect(1, GCCollectionMode.Optimized, false);
                using var proc = Process.GetCurrentProcess();
                EmptyWorkingSet(proc.Handle);
            }
            catch { }
        }

        private void SafeBeginInvoke(Action action)
        {
            try
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (!IsDisposed)
                            {
                                action();
                            }
                        }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                        catch { }
                    }));
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        #endregion

        #region Win32 Interop — Single Instance

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr changeInfo);

        private const uint MSGFLT_ALLOW = 1;

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
                if (e.Clicks == 2)
                {
                    ToggleMaximize16x9();
                    return;
                }
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
        private Bitmap? _appIconBitmap;
        private ContextMenuStrip _trayMenu = new();
        private ColorDialog _colorPicker = new() { FullOpen = true, Color = Color.FromArgb(0, 150, 255) };

        private readonly BacklightStateManager _backlightMgr = new();
        private bool _manualBacklightOnBattery;
        private bool _isLidClosed;
        private bool _isSleeping;
        private IntPtr _powerSettingRegistrationLid = IntPtr.Zero;
        private IntPtr _powerSettingRegistrationPower = IntPtr.Zero;
        private IntPtr _powerSettingRegistrationBattery = IntPtr.Zero;
        private IntPtr _powerSettingRegistrationDisplay = IntPtr.Zero;
        private readonly object _curveLock = new();
        private readonly object _powerNotifyLock = new();
        private CancellationTokenSource? _backlightEnforceCts;
        private readonly object _backlightEnforceLock = new();
        private CancellationTokenSource? _rgbRestoreCts;
        private readonly object _rgbRestoreLock = new();

        private int _cpuTemp, _gpuTemp;
        private int _telemetryRunning;
        private bool _suppressThemeChange;
        private DarkScrollPanel _contentPanel = null!;

        private bool? _isPluggedIn;
        private bool? _pendingPluggedIn;
        private int _powerLineStableTicks;
        private bool _allowVisible = true;
        private bool _isResyncing;
        private bool _isClosing;
        private int _maxHz = 144;
        private float _dpiScale = 1f; 
        private int _formW;           

        private static Color FormBg => ThemeManager.FormBg;
        private static Color SeparatorColor => ThemeManager.Separator;
        private static Color HeaderColor => ThemeManager.HeaderText;
        private static Color SubHeaderColor => ThemeManager.TextSecondary;
        private static Color AccentColor => ThemeManager.Accent;

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

        private bool _isInitializing = true;
        private PredatorButton? _btnDisp60, _btnDispMax;
        private PredatorSwitch? _swOdSys;
        private Label? _lblOdStatusSys;
        private FanCurveGraph? _embeddedCurveCpu;
        private FanCurveGraph? _embeddedCurveGpu;
        private PredatorButton? _btnEmbeddedApplyCurve;
        private PredatorButton? _btnEmbeddedResetCurve;

        private FanCurveForm? _fanCurveForm;
        private bool _fanCurveEnabled;
        private List<Point> _cpuCurvePoints = new(FanCurveGraph.DefaultCpuPoints);
        private List<Point> _gpuCurvePoints = new(FanCurveGraph.DefaultGpuPoints);
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
        private readonly object _dampingLock = new();
        private readonly Queue<int> _cpuDampingQueue = new();
        private Action? _formThemeHandler;
        private readonly Queue<int> _gpuDampingQueue = new();
        private DateTime _lastModeKeyUtc = DateTime.MinValue;
        private DateTime _lastToggleWindowUtc = DateTime.MinValue;

        private Label? _lblSysFanRpm;
        private Label? _lblFanLockStatus;
        private Label? _lblGpuSleepBadge;
        private Label? _lblProtocolBadge;
        private HistoryGraphControl? _cpuHistoryGraph;
        private HistoryGraphControl? _gpuHistoryGraph;

        private PredatorButton? _btnLockCpu;
        private PredatorButton? _btnLockGpu;
        private PredatorButton? _btnLockSys;
        private bool _isCpuFanLocked;
        private bool _isGpuFanLocked;
        private bool _isSysFanLocked;

        private PredatorSwitch? _switchCoolBoost;
        private PredatorDropDown? _cboFanTable;
        private PredatorButton? _btnGpuAuto;
        private PredatorButton? _btnGpuHybrid;
        private PredatorButton? _btnGpuDiscrete;
        private PredatorButton? _activeGpuModeBtn;
        private PredatorSwitch? _switchUsbCharging;
        private PredatorDropDown? _cboUsbFloor;
        private PredatorButton? _btnBatteryCalibration;
        private PredatorDropDown? _cboTheme;
        private PredatorButton? _btnDiagnostics;

        private Panel _pnlTitle = null!;
        private Panel _sidebarPanel = null!;
        private Panel _pageContainer = null!;
        private DarkScrollPanel _pnlDashboard = null!;
        private DarkScrollPanel _pnlFans = null!;
        private DarkScrollPanel _pnlLighting = null!;
        private DarkScrollPanel _pnlSystem = null!;
        private DarkScrollPanel _pnlSettings = null!;

        private PredatorButton _btnNavDashboard = null!;
        private PredatorButton _btnNavFans = null!;
        private PredatorButton _btnNavLighting = null!;
        private PredatorButton _btnNavSystem = null!;
        private PredatorButton _btnNavSettings = null!;
        private PredatorButton? _activeNavBtn;

        private PredatorButton _btnThemeToggle = null!;
        private TitleBarButton? _btnCaptionClose, _btnCaptionMax, _btnCaptionMin;
        private bool _isMaximized16x9;
        private Rectangle _normalBounds;
        private readonly AppSettings _appSettings = AppSettings.Load();
        private readonly object _saveLock = new();
        private int _modeKeyAction = 0; // 0 = Cycle, 1 = TurboToggle
        private byte _turboReturnMode = 0x01;
        private PredatorDropDown _cboModeKeyAction = null!;
        private PredatorDropDown _cboModeKeyActionSys = null!;
        private PredatorButton _btnTriggerModeKey = null!;
        private Label _lblModePill = null!;
        private Label _lblPowerPill = null!;

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
            _isInitializing = true;
            try
            {
                if (Environment.CommandLine.IndexOf("-hidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    Environment.CommandLine.IndexOf("--hidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    Environment.CommandLine.IndexOf("/hidden", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _allowVisible = false;
                }

                InitializeComponent();
                this.DoubleBuffered = true;
                try { Updater.CleanStaleUpdateArtifacts(); } catch { }
                _capabilities = CapabilityProbe.Probe(_wmi, _wmi.EcHid);
                ThemeManager.LoadThemeFromRegistry();

            _dpiScale = this.DeviceDpi / 96f;
            try { int detectedHz = GetMaxRefreshRateCore(); if (detectedHz > 60) _maxHz = detectedHz; } catch { }

            BuildUI();
            ThemeManager.ApplyTheme(this);
            UpdatePowerModePill(_currentPowerMode);
            _formThemeHandler = () =>
            {
                SafeBeginInvoke(() =>
                {
                    this.BackColor = ThemeManager.FormBg;
                            if (_pnlTitle != null) _pnlTitle.BackColor = ThemeManager.TitleBarBg;
                            if (_sidebarPanel != null) _sidebarPanel.BackColor = ThemeManager.SidebarBg;
                            if (_pageContainer != null) _pageContainer.BackColor = ThemeManager.FormBg;
                            if (_btnThemeToggle != null) _btnThemeToggle.Text = ThemeManager.IsDarkThemeActive ? "🌙" : "☀️";
                            if (_lblTitle != null) _lblTitle.ForeColor = ThemeManager.TextPrimary;
                            if (_lblModePill != null) _lblModePill.ForeColor = GetPowerModeColor(_currentPowerMode);
                            UpdatePowerStatusUI();
                            if (_btnCaptionClose != null) _btnCaptionClose.ForeColor = ThemeManager.TextPrimary;
                            if (_btnCaptionMax != null)
                            {
                                _btnCaptionMax.ForeColor = ThemeManager.TextPrimary;
                                _btnCaptionMax.FlatAppearance.MouseOverBackColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(0x2A, 0x32, 0x45) : Color.FromArgb(0xDC, 0xE1, 0xEC);
                                _btnCaptionMax.FlatAppearance.MouseDownBackColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(0x3D, 0x47, 0x60) : Color.FromArgb(0xC8, 0xD1, 0xE0);
                            }
                            if (_btnCaptionMin != null)
                            {
                                _btnCaptionMin.ForeColor = ThemeManager.TextPrimary;
                                _btnCaptionMin.FlatAppearance.MouseOverBackColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(0x2A, 0x32, 0x45) : Color.FromArgb(0xDC, 0xE1, 0xEC);
                                _btnCaptionMin.FlatAppearance.MouseDownBackColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(0x3D, 0x47, 0x60) : Color.FromArgb(0xC8, 0xD1, 0xE0);
                            }
                            ThemeManager.ApplyTheme(this);
                            if (_cboTheme != null)
                            {
                                _suppressThemeChange = true;
                                try { _cboTheme.SelectedIndex = (int)ThemeManager.CurrentTheme; }
                                finally { _suppressThemeChange = false; }
                            }
                            _appSettings.Theme = ThemeManager.CurrentTheme.ToString();
                });
            };
            ThemeManager.ThemeChanged += _formThemeHandler;
            BuildTrayMenu();
            SetupSystemTray();

            try
            {
                _pipeServer = new SecureNamedPipeIpc.PipeServer(msg =>
                {
                    SafeBeginInvoke(() =>
                    {
                        if (string.Equals(msg?.Trim(), "SHOW", StringComparison.OrdinalIgnoreCase))
                        {
                            ShowApp();
                        }
                        else
                        {
                            ToggleWindowVisibility();
                        }
                    });
                });
                _pipeServer.Start();
            }
            catch { }

            try
            {
                _rawInputWatcher = new RawInputKeyWatcher();
                _rawInputWatcher.NitroSenseKeyPressed += () =>
                {
                    SafeBeginInvoke(ToggleWindowVisibility);
                };
                _rawInputWatcher.ModeKeyPressed += () =>
                {
                    SafeBeginInvoke(HandleModeKeyPress);
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

            var powerInit = SystemInformation.PowerStatus;
            _timer.Interval = _backlightMgr.IsOnBattery(powerInit.PowerLineStatus, powerInit.BatteryChargeStatus) ? 5000 : 2000;
            _timer.Tick += UpdateTelemetry;
            _timer.Start();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            this.FormClosed += (s, e) =>
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                if (_formThemeHandler != null)
                {
                    ThemeManager.ThemeChanged -= _formThemeHandler;
                }
            };

            this.Shown += (s, e) =>
            {
                _activePowerBtn?.Invalidate();
                _activeFanBtn?.Invalidate();
                _activeCustomSubBtn?.Invalidate();
                _activeDisplayBtn?.Invalidate();
                _activeGpuModeBtn?.Invalidate();

                if (_allowVisible)
                {
                    Updater.ShowPendingNotes(this);
                }

                Task.Run(() =>
                {
                    try
                    {
                        EnsureStartupTaskUpdated();
                        MigrateLegacyStartup();
                        bool enabled = IsStartupEnabled();
                        SafeBeginInvoke(() =>
                        {
                            _suppressStartupToggle = true;
                            _switchStartWithWindows.Checked = enabled;
                            _suppressStartupToggle = false;
                        });
                    }
                    catch (Exception ex)
                    {
                        Program.Report(ex, false);
                    }

                    try
                    {
                        var probed = CapabilityProbe.Probe(_wmi, _wmi.EcHid);
                        SafeBeginInvoke(() =>
                        {
                            _capabilities = probed;
                            if (_lblProtocolBadge != null)
                            {
                                _lblProtocolBadge.Text = _wmi.EcHid != null && _wmi.EcHid.IsOpen ? "⚡ Direct Hardware Controller" : "🔌 Acer System Driver";
                            }
                        });
                    }
                    catch { }
                });
            };

            try
            {
                _keyboardHook = new KeyboardHook(
                    onPredatorSensePressed: () =>
                    {
                        SafeBeginInvoke(ToggleWindowVisibility);
                    },
                    onPredatorNumberPressed: (num) =>
                    {
                        SafeBeginInvoke(() =>
                        {
                            switch (num)
                            {
                                case 1: ApplyPowerMode(0x06, _btnEco); break;
                                case 2: ApplyPowerMode(0x00, _btnQuiet); break;
                                case 3: ApplyPowerMode(0x01, _btnBalanced); break;
                                case 4: ApplyPowerMode(0x04, _btnPerform); break;
                                case 5: ApplyPowerMode(0x05, _btnTurbo); break;
                            }
                        });
                    },
                    onModeKeyPressed: () =>
                    {
                        SafeBeginInvoke(HandleModeKeyPress);
                    });
            }
            catch { }

            try
            {
                _wmiHotkeyWatcher = new WmiHotkeyWatcher((detail) =>
                {
                    if (detail == 5)
                    {
                        SafeBeginInvoke(ToggleWindowVisibility);
                    }
                });
                _wmiHotkeyWatcher.ModeKeyPressed += () =>
                {
                    SafeBeginInvoke(HandleModeKeyPress);
                };
            }
            catch { }
        }
        finally
        {
            _isInitializing = false;
        }
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
                    Guid displayGuid = GUID_CONSOLE_DISPLAY_STATE;

                    if (_powerSettingRegistrationLid == IntPtr.Zero)
                        _powerSettingRegistrationLid = RegisterPowerSettingNotification(this.Handle, ref lidGuid, DEVICE_NOTIFY_WINDOW_HANDLE);

                    if (_powerSettingRegistrationPower == IntPtr.Zero)
                        _powerSettingRegistrationPower = RegisterPowerSettingNotification(this.Handle, ref powerGuid, DEVICE_NOTIFY_WINDOW_HANDLE);

                    if (_powerSettingRegistrationBattery == IntPtr.Zero)
                        _powerSettingRegistrationBattery = RegisterPowerSettingNotification(this.Handle, ref batteryGuid, DEVICE_NOTIFY_WINDOW_HANDLE);

                    if (_powerSettingRegistrationDisplay == IntPtr.Zero)
                        _powerSettingRegistrationDisplay = RegisterPowerSettingNotification(this.Handle, ref displayGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
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
                if (_powerSettingRegistrationDisplay != IntPtr.Zero)
                {
                    UnregisterPowerSettingNotification(_powerSettingRegistrationDisplay);
                    _powerSettingRegistrationDisplay = IntPtr.Zero;
                }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterPowerNotifications();
            try
            {
                ChangeWindowMessageFilterEx(this.Handle, WM_SHOWME, MSGFLT_ALLOW, IntPtr.Zero);
            }
            catch { }
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
            var power = SystemInformation.PowerStatus;
            bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
            if (isCharging) return false;
            if (_isPluggedIn.HasValue) return !_isPluggedIn.Value;
            return _backlightMgr.IsOnBattery(power.PowerLineStatus, power.BatteryChargeStatus);
        }

        private void HandlePowerBroadcast(ref Message m)
        {
            int wParam = m.WParam.ToInt32();
            switch (wParam)
            {
                case PBT_APMPOWERSTATUSCHANGE:
                    SafeBeginInvoke(() =>
                    {
                        var power = SystemInformation.PowerStatus;
                        var lineStatus = power.PowerLineStatus;
                        bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                        bool onBattery = !isCharging && (lineStatus == PowerLineStatus.Offline || (_isPluggedIn == false) || _backlightMgr.IsOnBattery(lineStatus, power.BatteryChargeStatus));
                        bool isAc = !onBattery;
                        _backlightMgr.OnPowerSourceChanged(isAc, out _, out _);
                        _timer.Interval = onBattery ? 5000 : 2000;
                        if (_isPluggedIn != isAc)
                        {
                            _isPluggedIn = isAc;
                            _pendingPluggedIn = isAc;
                            _powerLineStableTicks = 2;
                            _backlightMgr.SetPluggedInState(isAc);
                            try { ApplyPowerRules(isAc); } catch { }
                        }
                        else
                        {
                            try { ApplyFanRules(isAc); } catch { }
                        }
                        UpdatePowerStatusUI();

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
                            int selectedRgb = _rgbDropDown?.SelectedIndex ?? -1;
                            Task.Run(() =>
                            {
                                _wmi.SetBrightness((byte)targetBright);
                                if (selectedRgb >= 0)
                                    _wmi.ApplyLightingMode(selectedRgb);
                            });
                        }
                        ScheduleRgbRestoreWatchdog();
                    });
                    break;

                case PBT_APMSUSPEND:
                    _isSleeping = true;
                    _timer.Stop();
                    lock (_backlightEnforceLock)
                    {
                        try { _backlightEnforceCts?.Cancel(); } catch { }
                    }
                    _backlightMgr.OnSuspend(out bool shouldOffSuspend, out _);
                    if (shouldOffSuspend)
                    {
                        _manualBacklightOnBattery = false;
                        Task.Run(() => _wmi.TurnOffBacklight());
                    }
                    break;

                case PBT_APMRESUMEAUTOMATIC:
                case PBT_APMRESUMESUSPEND:
                    if (!_isSleeping) break;
                    _isSleeping = false;
                    Interlocked.Exchange(ref _telemetryRunning, 0);
                    _cpuDampingQueue.Clear();
                    _gpuDampingQueue.Clear();
                    _cpuCurveFollower.Reset();
                    _gpuCurveFollower.Reset();
                    _sysCurveFollower.Reset();
                    _lastCurveCpuSpeed = -1;
                    _lastCurveGpuSpeed = -1;
                    _lastCurveSysSpeed = -1;
                    _wmi.InvalidateSensorCaches();

                    var resumePower = SystemInformation.PowerStatus;
                    var resumeLine = resumePower.PowerLineStatus;
                    var resumeCharge = resumePower.BatteryChargeStatus;
                    bool isResumeCharging = (resumeCharge & BatteryChargeStatus.Charging) != 0;
                    bool resumeOnBattery = !isResumeCharging && (resumeLine == PowerLineStatus.Offline || (_isPluggedIn == false) || _backlightMgr.IsOnBattery(resumeLine, resumeCharge));
                    _timer.Interval = resumeOnBattery ? 5000 : 2000;
                    _timer.Start();

                    SafeBeginInvoke(() =>
                    {
                        var lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
                        var chargeStatus = SystemInformation.PowerStatus.BatteryChargeStatus;
                        _backlightMgr.OnResume(lineStatus, out bool shouldTurnOffResume, out int targetBright, chargeStatus);
                        bool isChargingNow = (chargeStatus & BatteryChargeStatus.Charging) != 0;
                        bool isAc = !shouldTurnOffResume && (lineStatus == PowerLineStatus.Online || isChargingNow);
                        _isPluggedIn = isAc;
                        _backlightMgr.SetPluggedInState(isAc);
                        try { ApplyPowerRules(isAc); } catch { }
                        UpdatePowerStatusUI();

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
                            int selectedRgbResume = _rgbDropDown?.SelectedIndex ?? -1;
                            Task.Run(() =>
                            {
                                _wmi.SetBrightness((byte)targetBright);
                                if (selectedRgbResume >= 0)
                                    _wmi.ApplyLightingMode(selectedRgbResume);
                            });
                        }
                        ScheduleRgbRestoreWatchdog();
                    });
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
                            else if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE && setting.DataLength >= 1)
                            {
                                int offset = Marshal.OffsetOf<POWERBROADCAST_SETTING>(nameof(POWERBROADCAST_SETTING.Data)).ToInt32();
                                int displayState = setting.DataLength >= 4
                                    ? Marshal.ReadInt32(m.LParam, offset)
                                    : Marshal.ReadByte(m.LParam, offset);
                                bool displayOff = displayState == 0;
                                OnDisplayStateChanged(displayOff);
                            }
                        }
                        catch { }
                    }
                    break;
            }
        }

        private void OnDisplayStateChanged(bool displayOff)
        {
            if (displayOff)
            {
                _timer.Interval = 10000;
            }
            else
            {
                _timer.Interval = 2000;
                SafeBeginInvoke(() => UpdateTelemetryCore());
            }
        }

        private void OnLidStateChanged(bool isOpen)
        {
            _isLidClosed = !isOpen;
            var power = SystemInformation.PowerStatus;
            _backlightMgr.OnLidChanged(isOpen, power.PowerLineStatus, out bool shouldTurnOff, out int targetBright, power.BatteryChargeStatus);

            SafeBeginInvoke(() =>
            {
                if (_isClosing || IsDisposed) return;

                UpdatePowerStatusUI();

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
            });
        }

        private void OnPowerSourceChangedImmediate(bool isAc)
        {
            _backlightMgr.OnPowerSourceChanged(isAc, out bool shouldTurnOff, out int targetBright);
            _pendingPluggedIn = isAc;
            _powerLineStableTicks = 2;
            _timer.Interval = isAc ? 2000 : 5000;

            SafeBeginInvoke(() =>
            {
                if (_isClosing || IsDisposed) return;

                if (_isPluggedIn != isAc)
                {
                    _isPluggedIn = isAc;
                    _backlightMgr.SetPluggedInState(isAc);
                    try { ApplyPowerRules(isAc); } catch { }
                }
                else
                {
                    try { ApplyFanRules(isAc); } catch { }
                }
                UpdatePowerStatusUI();

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
            });
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

                        var power = SystemInformation.PowerStatus;
                        if (power.PowerLineStatus == PowerLineStatus.Online || (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0) break;

                        if (_isLidClosed || _isSleeping || (_backlightMgr.IsOnBattery(power.PowerLineStatus, power.BatteryChargeStatus) && !_manualBacklightOnBattery))
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

                        SafeBeginInvoke(() =>
                        {
                            if (_isClosing || IsDisposed) return;
                            RestoreUserLightingState();
                        });

                        // Secondary pulse at delayMs + 600ms to catch slow EC firmware transitions
                        await Task.Delay(600, token);
                        if (token.IsCancellationRequested || _isClosing || IsDisposed) return;

                        SafeBeginInvoke(() =>
                        {
                            if (_isClosing || IsDisposed) return;
                            RestoreUserLightingState();
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Program.Report(ex, false); }
                }, token);
            }
        }

        private void RestoreUserLightingState()
        {
            if (_isClosing || IsDisposed) return;

            var power = SystemInformation.PowerStatus;
            bool onBattery = _backlightMgr.IsOnBattery(power.PowerLineStatus, power.BatteryChargeStatus);
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
 
        internal static int GetCurrentRefreshRateCore(int displayIndex = 0)
        {
            if (displayIndex != 0) return 60;
            DEVMODE dm = new(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 60;
        }

        internal static int GetMaxRefreshRateCore(int displayIndex = 0)
        {
            if (displayIndex != 0) return 60;
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

        internal static bool SetRefreshRateCore(int hz, int displayIndex = 0)
        {
            if (displayIndex != 0) return false;
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
            this.ForeColor = ThemeManager.TextPrimary;

            // Strict 16:9 aspect ratio window geometry clamped to screen working area
            int workH = Screen.PrimaryScreen?.WorkingArea.Height ?? S(1080);
            int workW = Screen.PrimaryScreen?.WorkingArea.Width ?? S(1920);
            int targetH = Math.Max(S(540), Math.Min(S(720), workH - S(40)));
            int targetW = (int)Math.Round(targetH * 16.0 / 9.0);
            if (targetW > workW - S(40))
            {
                targetW = workW - S(40);
                targetH = (int)Math.Round(targetW * 9.0 / 16.0);
            }
            _formW = targetW;
            int _formH = targetH;
            this.ClientSize = new Size(_formW, _formH);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int titleH = S(44);
            int sidebarW = S(210);

            BuildTitleBar(titleH);
            BuildSidebar(sidebarW, titleH, _formH - titleH);
            BuildPageContainer(sidebarW, titleH, _formW - sidebarW, _formH - titleH);

            SwitchPage(_pnlDashboard, _btnNavDashboard);
        }

        private void SwitchPage(DarkScrollPanel targetPage, PredatorButton targetBtn)
        {
            if (_pnlDashboard != null) _pnlDashboard.Visible = false;
            if (_pnlFans != null) _pnlFans.Visible = false;
            if (_pnlLighting != null) _pnlLighting.Visible = false;
            if (_pnlSystem != null) _pnlSystem.Visible = false;
            if (_pnlSettings != null) _pnlSettings.Visible = false;

            targetPage.Visible = true;
            targetPage.BringToFront();

            if (_btnNavDashboard != null) _btnNavDashboard.IsActive = false;
            if (_btnNavFans != null) _btnNavFans.IsActive = false;
            if (_btnNavLighting != null) _btnNavLighting.IsActive = false;
            if (_btnNavSystem != null) _btnNavSystem.IsActive = false;
            if (_btnNavSettings != null) _btnNavSettings.IsActive = false;

            targetBtn.IsActive = true;
            _activeNavBtn = targetBtn;
        }

        private void ToggleMaximize16x9()
        {
            if (!_isMaximized16x9)
            {
                _normalBounds = this.Bounds;
                var screen = Screen.FromHandle(this.Handle);
                Rectangle work = screen.WorkingArea;

                Rectangle targetBounds = Calculate16x9MaximizedBounds(work);

                _isMaximized16x9 = true;
                if (_btnCaptionMax != null) _btnCaptionMax.Text = "🗗";
                this.SetBounds(targetBounds.X, targetBounds.Y, targetBounds.Width, targetBounds.Height);
            }
            else
            {
                _isMaximized16x9 = false;
                if (_btnCaptionMax != null) _btnCaptionMax.Text = "🗖";
                if (_normalBounds.Width > 0 && _normalBounds.Height > 0)
                {
                    this.SetBounds(_normalBounds.X, _normalBounds.Y, _normalBounds.Width, _normalBounds.Height);
                }
                else
                {
                    int workH = Screen.FromHandle(this.Handle).WorkingArea.Height;
                    int workW = Screen.FromHandle(this.Handle).WorkingArea.Width;
                    int targetH = Math.Max(S(540), Math.Min(S(720), workH - S(40)));
                    int targetW = (int)Math.Round(targetH * 16.0 / 9.0);
                    int targetX = (workW - targetW) / 2;
                    int targetY = (workH - targetH) / 2;
                    this.SetBounds(targetX, targetY, targetW, targetH);
                }
            }
        }

        public static Rectangle Calculate16x9MaximizedBounds(Rectangle work)
        {
            if (work.Width <= 0 || work.Height <= 0) return work;
            int targetW = work.Width;
            int targetH = (int)Math.Round(targetW * 9.0 / 16.0);
            if (targetH > work.Height)
            {
                targetH = work.Height;
                targetW = (int)Math.Round(targetH * 16.0 / 9.0);
            }
            if (targetW > work.Width) targetW = work.Width;

            int targetX = work.X + (work.Width - targetW) / 2;
            int targetY = work.Y + (work.Height - targetH) / 2;
            return new Rectangle(targetX, targetY, targetW, targetH);
        }

        private void BuildTitleBar(int titleH)
        {
            _pnlTitle = new Panel
            {
                Name = "pnlTitle",
                Tag = "title_bar",
                Location = new Point(0, 0),
                Size = new Size(_formW, titleH),
                BackColor = ThemeManager.TitleBarBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _pnlTitle.MouseDown += TitleBar_MouseDown;
            this.Controls.Add(_pnlTitle);

            int pad = S(16);

            var picIcon = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(S(20), S(20)),
                Location = new Point(pad, (titleH - S(20)) / 2),
                BackColor = Color.Transparent
            };
            try
            {
                using var extIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extIcon != null)
                {
                    _appIconBitmap = extIcon.ToBitmap();
                    picIcon.Image = _appIconBitmap;
                }
            }
            catch { }
            picIcon.MouseDown += TitleBar_MouseDown;
            _pnlTitle.Controls.Add(picIcon);

            _lblTitle = new Label
            {
                Name = "_lblTitle",
                Text = "PREDATOR CONTROL",
                ForeColor = ThemeManager.TextPrimary,
                Font = FontTitle,
                AutoSize = true,
                Location = new Point(picIcon.Right + S(10), (titleH - S(18)) / 2),
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
            _lblTitle.MouseDown += TitleBar_MouseDown;
            _pnlTitle.Controls.Add(_lblTitle);

            _lblModePill = new Label
            {
                Text = "⚡ BALANCED",
                Font = FontSectionHeader,
                ForeColor = GetPowerModeColor(_currentPowerMode),
                AutoSize = true,
                Location = new Point(_lblTitle.Right + S(16), (titleH - S(16)) / 2),
                BackColor = Color.Transparent,
                Tag = "custom_color",
                UseMnemonic = false
            };
            _lblModePill.MouseDown += TitleBar_MouseDown;
            _pnlTitle.Controls.Add(_lblModePill);

            var powerInit = SystemInformation.PowerStatus;
            bool initCharging = (powerInit.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
            bool initAc = powerInit.PowerLineStatus == PowerLineStatus.Online || initCharging;
            _lblPowerPill = new Label
            {
                Text = initCharging ? "⚡ CHARGING" : (initAc ? "🔌 AC" : "🔋 BATTERY"),
                Font = FontHeaderLight,
                ForeColor = initCharging ? AccentColor : ThemeManager.TextSecondary,
                AutoSize = true,
                Location = new Point(_lblModePill.Right + S(12), (titleH - S(16)) / 2),
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
            _lblPowerPill.MouseDown += TitleBar_MouseDown;
            _pnlTitle.Controls.Add(_lblPowerPill);

            int btnW = S(46);
            _btnCaptionClose = new TitleBarButton
            {
                Text = "✕",
                Font = FontBody,
                Size = new Size(btnW, titleH),
                Location = new Point(_formW - btnW, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = ThemeManager.TextPrimary,
                Cursor = Cursors.Default,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnCaptionClose.FlatAppearance.BorderSize = 0;
            _btnCaptionClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 17, 35); // #E81123
            _btnCaptionClose.FlatAppearance.MouseDownBackColor = Color.FromArgb(241, 112, 122);
            _btnCaptionClose.MouseEnter += (s, e) => _btnCaptionClose.ForeColor = Color.White;
            _btnCaptionClose.MouseLeave += (s, e) => _btnCaptionClose.ForeColor = ThemeManager.TextPrimary;
            _btnCaptionClose.Click += (s, e) => this.Close();

            _btnCaptionMax = new TitleBarButton
            {
                Text = "🗖",
                Font = FontBody,
                Size = new Size(btnW, titleH),
                Location = new Point(_formW - btnW * 2, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = ThemeManager.TextPrimary,
                Cursor = Cursors.Default,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnCaptionMax.FlatAppearance.BorderSize = 0;
            _btnCaptionMax.FlatAppearance.MouseOverBackColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(0x2A, 0x32, 0x45) : Color.FromArgb(0xDC, 0xE1, 0xEC);
            _btnCaptionMax.FlatAppearance.MouseDownBackColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(0x3D, 0x47, 0x60) : Color.FromArgb(0xC8, 0xD1, 0xE0);
            _btnCaptionMax.Click += (s, e) => ToggleMaximize16x9();

            _btnCaptionMin = new TitleBarButton
            {
                Text = "—",
                Font = FontBody,
                Size = new Size(btnW, titleH),
                Location = new Point(_formW - btnW * 3, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = ThemeManager.TextPrimary,
                Cursor = Cursors.Default,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnCaptionMin.FlatAppearance.BorderSize = 0;
            _btnCaptionMin.FlatAppearance.MouseOverBackColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(0x2A, 0x32, 0x45) : Color.FromArgb(0xDC, 0xE1, 0xEC);
            _btnCaptionMin.FlatAppearance.MouseDownBackColor = ThemeManager.IsDarkThemeActive ? Color.FromArgb(0x3D, 0x47, 0x60) : Color.FromArgb(0xC8, 0xD1, 0xE0);
            _btnCaptionMin.Click += (s, e) => this.WindowState = FormWindowState.Minimized;

            _btnThemeToggle = new PredatorButton
            {
                Text = ThemeManager.IsDarkThemeActive ? "🌙" : "☀️",
                Size = new Size(S(36), S(26)),
                Location = new Point(_btnCaptionMin.Left - S(44), (titleH - S(26)) / 2),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnThemeToggle.Click += (s, e) =>
            {
                var newTheme = ThemeManager.IsDarkThemeActive ? AppTheme.Light : AppTheme.Dark;
                ThemeManager.SetTheme(newTheme);
                ThemeManager.ApplyTheme(this);
                _appSettings.Theme = newTheme.ToString();
                _appSettings.Save();
                _btnThemeToggle.Text = ThemeManager.IsDarkThemeActive ? "🌙" : "☀️";
                if (_cboTheme != null)
                {
                    _suppressThemeChange = true;
                    try { _cboTheme.SelectedIndex = (int)newTheme; }
                    finally { _suppressThemeChange = false; }
                }
            };

            _pnlTitle.Controls.Add(_btnCaptionClose);
            _pnlTitle.Controls.Add(_btnCaptionMax);
            _pnlTitle.Controls.Add(_btnCaptionMin);
            _pnlTitle.Controls.Add(_btnThemeToggle);
        }

        private void BuildSidebar(int sidebarW, int topY, int sidebarH)
        {
            _sidebarPanel = new Panel
            {
                Name = "_sidebarPanel",
                Tag = "sidebar",
                Location = new Point(0, topY),
                Size = new Size(sidebarW, sidebarH),
                BackColor = ThemeManager.SidebarBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left
            };
            this.Controls.Add(_sidebarPanel);

            int navPad = S(10);
            int navBtnW = sidebarW - navPad * 2;
            int navBtnH = S(44);
            int navGap = S(6);
            int y = S(14);

            _btnNavDashboard = CreateNavButton("📊  Dashboard", navPad, y, navBtnW, navBtnH);
            _btnNavDashboard.Click += (s, e) => SwitchPage(_pnlDashboard, _btnNavDashboard);
            y += navBtnH + navGap;

            _btnNavFans = CreateNavButton("💨  Fans & Cooling", navPad, y, navBtnW, navBtnH);
            _btnNavFans.Click += (s, e) => SwitchPage(_pnlFans, _btnNavFans);
            y += navBtnH + navGap;

            _btnNavLighting = CreateNavButton("✨  RGB Lighting", navPad, y, navBtnW, navBtnH);
            _btnNavLighting.Click += (s, e) => SwitchPage(_pnlLighting, _btnNavLighting);
            y += navBtnH + navGap;

            _btnNavSystem = CreateNavButton("⚡  System & Power", navPad, y, navBtnW, navBtnH);
            _btnNavSystem.Click += (s, e) => SwitchPage(_pnlSystem, _btnNavSystem);
            y += navBtnH + navGap;

            _btnNavSettings = CreateNavButton("⚙️  Settings & Info", navPad, y, navBtnW, navBtnH);
            _btnNavSettings.Click += (s, e) => SwitchPage(_pnlSettings, _btnNavSettings);

            _sidebarPanel.Controls.Add(_btnNavDashboard);
            _sidebarPanel.Controls.Add(_btnNavFans);
            _sidebarPanel.Controls.Add(_btnNavLighting);
            _sidebarPanel.Controls.Add(_btnNavSystem);
            _sidebarPanel.Controls.Add(_btnNavSettings);

            int footerY = sidebarH - S(44);
            var lblSideVer = new Label
            {
                Text = $"v.{Updater.CurrentText}",
                Font = FontHeaderLight,
                ForeColor = ThemeManager.TextMuted,
                Location = new Point(navPad + S(6), footerY),
                AutoSize = true,
                BackColor = Color.Transparent,
                Tag = "muted",
                UseMnemonic = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            _sidebarPanel.Controls.Add(lblSideVer);
        }

        private PredatorButton CreateNavButton(string text, int x, int y, int w, int h)
        {
            return new PredatorButton
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                IsNavButton = true,
                Padding = new Padding(S(12), 0, 0, 0),
                Cursor = Cursors.Hand
            };
        }

        private void BuildPageContainer(int leftX, int topY, int containerW, int containerH)
        {
            _pageContainer = new Panel
            {
                Name = "_pageContainer",
                Tag = "page_container",
                Location = new Point(leftX, topY),
                Size = new Size(containerW, containerH),
                BackColor = ThemeManager.FormBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            this.Controls.Add(_pageContainer);

            _pnlDashboard = CreatePagePanel(containerW, containerH);
            _pnlFans = CreatePagePanel(containerW, containerH);
            _pnlLighting = CreatePagePanel(containerW, containerH);
            _pnlSystem = CreatePagePanel(containerW, containerH);
            _pnlSettings = CreatePagePanel(containerW, containerH);

            _contentPanel = _pnlDashboard;

            BuildDashboardPage();
            BuildFansPage();
            BuildLightingPage();
            BuildSystemPage();
            BuildSettingsPage();

            _pageContainer.Controls.Add(_pnlDashboard);
            _pageContainer.Controls.Add(_pnlFans);
            _pageContainer.Controls.Add(_pnlLighting);
            _pageContainer.Controls.Add(_pnlSystem);
            _pageContainer.Controls.Add(_pnlSettings);
        }

        private DarkScrollPanel CreatePagePanel(int w, int h)
        {
            var pnl = new DarkScrollPanel
            {
                Location = new Point(0, 0),
                Size = new Size(w + DarkScrollPanel.NativeBarWidth, h),
                BackColor = ThemeManager.FormBg,
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            pnl.SetDpiScale(_dpiScale);
            return pnl;
        }

        private void BuildDashboardPage()
        {
            int pad = S(16);
            int gap = S(16);
            int usableW = _pageContainer.Width - pad * 2 - gap - DarkScrollPanel.NativeBarWidth;
            int cardW = usableW / 2;
            int col1X = pad;
            int col2X = pad + cardW + gap;
            int cPad = S(14);
            int cInnerW = cardW - cPad * 2;

            // Card 1: Real-Time Sensors
            var cardSensors = MakeCard(_pnlDashboard, "REAL-TIME SENSORS", col1X, pad, cardW, S(230));
            MakeLabel(cardSensors, "CPU:", cPad, S(44), FontBody, SubHeaderColor);
            _lblCpuTemp = MakeLabel(cardSensors, "43°C", cPad + S(42), S(44), FontBodyBold, ThemeManager.TextPrimary);

            MakeLabel(cardSensors, "GPU:", cardW / 2, S(44), FontBody, SubHeaderColor);
            _lblGpuTemp = MakeLabel(cardSensors, "39°C", cardW / 2 + S(42), S(44), FontBodyBold, ThemeManager.TextPrimary);
            _lblGpuSleepBadge = MakeLabel(cardSensors, "[ 💤 Low Power Standby ]", cardW / 2 + S(96), S(44), FontSectionHeader, ThemeManager.StatusD3Cold, "d3cold");
            _lblGpuSleepBadge.Visible = false;

            MakeLabel(cardSensors, "CPU FAN:", cPad, S(72), FontBody, SubHeaderColor);
            _lblCpuRpm = MakeLabel(cardSensors, "-- RPM", cPad + S(74), S(72), FontBodyBold, ThemeManager.TextPrimary);

            MakeLabel(cardSensors, "GPU FAN:", cardW / 2, S(72), FontBody, SubHeaderColor);
            _lblGpuRpm = MakeLabel(cardSensors, "-- RPM", cardW / 2 + S(74), S(72), FontBodyBold, ThemeManager.TextPrimary);

            if (_capabilities.HasThirdFan)
            {
                MakeLabel(cardSensors, "SYSTEM FAN:", cPad, S(100), FontBody, SubHeaderColor);
                _lblSysFanRpm = MakeLabel(cardSensors, "-- RPM", cPad + S(74), S(100), FontBodyBold, ThemeManager.TextPrimary);

                MakeLabel(cardSensors, "HARDWARE LINK:", cardW / 2, S(100), FontBody, SubHeaderColor);
                _lblProtocolBadge = MakeLabel(cardSensors, _wmi.EcHid != null && _wmi.EcHid.IsOpen ? "⚡ Direct Hardware Controller" : "🔌 Acer System Driver", cardW / 2 + S(84), S(100), FontBodyBold, AccentColor, "accent");
            }
            else
            {
                _lblSysFanRpm = null;
                MakeLabel(cardSensors, "HARDWARE LINK:", cPad, S(100), FontBody, SubHeaderColor);
                _lblProtocolBadge = MakeLabel(cardSensors, _wmi.EcHid != null && _wmi.EcHid.IsOpen ? "⚡ Direct Hardware Controller" : "🔌 Acer System Driver", cPad + S(84), S(100), FontBodyBold, AccentColor, "accent");
            }

            MakeLabel(cardSensors, "POWER SOURCE:", cPad, S(128), FontBody, SubHeaderColor);
            var pInitDash = SystemInformation.PowerStatus;
            bool isChargingDash = (pInitDash.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
            bool isAcDash = pInitDash.PowerLineStatus == PowerLineStatus.Online || isChargingDash;
            string initPowerSource = isChargingDash ? "Charging" : (isAcDash ? "Plugged In" : "On Battery");
            _lblPowerStatus = MakeLabel(cardSensors, initPowerSource, cPad + S(110), S(128), FontBodyBold, ThemeManager.TextPrimary);

            MakeLabel(cardSensors, "FAN PROFILE:", cardW / 2, S(128), FontBody, SubHeaderColor);
            _lblFanStatus = MakeLabel(cardSensors, "Auto", cardW / 2 + S(90), S(128), FontBodyBold, ThemeManager.TextPrimary);

            cardSensors.Controls.Add(new Panel { Location = new Point(cPad, S(156)), Size = new Size(cInnerW, 1), BackColor = ThemeManager.Separator });

            var batt = WindowsBattery.Read();
            string battInfo = batt != null ? $"Battery Wear: {batt.WearLevelPercent:F1}% | Cycles: {(batt.CycleCount.HasValue ? batt.CycleCount.Value.ToString() : "N/A")}" : "Battery: Monitored";
            MakeLabel(cardSensors, battInfo, cPad, S(166), FontHeaderLight, SubHeaderColor);

            _lblBatteryStatus = MakeLabel(cardSensors, "Full Charge (100%)", cPad, S(194), FontBody, SubHeaderColor);
            _switchBatteryLimit = new PredatorSwitch
            {
                Location = new Point(cardW - cPad - S(48), S(190)),
                Size = new Size(S(48), S(26))
            };
            _switchBatteryLimit.CheckedChanged += (s, e) => ApplyBatteryLimit(_switchBatteryLimit.Checked);
            cardSensors.Controls.Add(_switchBatteryLimit);

            // Card 2: Live Telemetry Stream Graphs
            var cardGraphs = MakeCard(_pnlDashboard, "LIVE TELEMETRY STREAM", col1X, pad + S(242), cardW, S(270));
            _cpuHistoryGraph = new HistoryGraphControl
            {
                Location = new Point(cPad, S(42)),
                Size = new Size(cInnerW, S(96)),
                Title = "CPU (Temp & Load)",
                Unit = "°C",
                Minimum = 20,
                Maximum = 100
            };
            _gpuHistoryGraph = new HistoryGraphControl
            {
                Location = new Point(cPad, S(152)),
                Size = new Size(cInnerW, S(96)),
                Title = "GPU (Temp & Load)",
                Unit = "°C",
                Minimum = 20,
                Maximum = 100
            };
            cardGraphs.Controls.Add(_cpuHistoryGraph);
            cardGraphs.Controls.Add(_gpuHistoryGraph);

            // Card 3: Acer Power Profiles
            var cardPower = MakeCard(_pnlDashboard, "ACER POWER PROFILES", col2X, pad, cardW, S(184));
            int btnW = (cInnerW - 4 * S(6)) / 5;
            int btnH = S(34);
            _btnQuiet = MakeButton(cardPower, "Quiet", cPad, S(44), btnW, btnH);
            _btnBalanced = MakeButton(cardPower, "Balanced", cPad + (btnW + S(6)), S(44), btnW, btnH);
            _btnPerform = MakeButton(cardPower, "Performance", cPad + (btnW + S(6)) * 2, S(44), btnW, btnH);
            _btnTurbo = MakeButton(cardPower, "Turbo", cPad + (btnW + S(6)) * 3, S(44), btnW, btnH);
            _btnEco = MakeButton(cardPower, "Eco", cPad + (btnW + S(6)) * 4, S(44), btnW, btnH);

            _btnQuiet.Click += (s, e) => ApplyPowerMode(0x00, _btnQuiet);
            _btnBalanced.Click += (s, e) => ApplyPowerMode(0x01, _btnBalanced);
            _btnPerform.Click += (s, e) => ApplyPowerMode(0x04, _btnPerform);
            _btnTurbo.Click += (s, e) => ApplyPowerMode(0x05, _btnTurbo);
            _btnEco.Click += (s, e) => ApplyPowerMode(0x06, _btnEco);

            MakeLabel(cardPower, "MODE KEY ACTION:", cPad, S(86), FontSectionHeader, SubHeaderColor);
            int mkaDropW = cInnerW - S(108);
            _cboModeKeyAction = new PredatorDropDown
            {
                Location = new Point(cPad, S(108)),
                Size = new Size(mkaDropW, S(30))
            };
            _cboModeKeyAction.Items.AddRange(new[] { "Cycle Power Modes", "Toggle Turbo Mode" });
            _cboModeKeyAction.SelectedIndex = _modeKeyAction;
            _cboModeKeyAction.SelectedIndexChanged += (s, e) =>
            {
                if (_isInitializing) return;
                _modeKeyAction = _cboModeKeyAction.SelectedIndex;
                if (_cboModeKeyActionSys != null && _cboModeKeyActionSys.SelectedIndex != _modeKeyAction)
                    _cboModeKeyActionSys.SelectedIndex = _modeKeyAction;
                SaveState("ModeKeyAction", _modeKeyAction);
            };
            cardPower.Controls.Add(_cboModeKeyAction);

            _btnTriggerModeKey = MakeButton(cardPower, "⚡ Switch Mode", cPad + mkaDropW + S(8), S(108), S(100), S(30));
            _btnTriggerModeKey.Click += (s, e) => HandleModeKeyPress();

            MakeLabel(cardPower, "Press Mode key or button to cycle profiles or toggle Turbo mode.", cPad, S(148), FontHeaderLight, SubHeaderColor);

            // Card 4: Display Refresh Rate & LCD Overdrive
            var cardQuickCooling = MakeCard(_pnlDashboard, "DISPLAY & REFRESH RATE", col2X, pad + S(196), cardW, S(164));
            int dispW = (cInnerW - S(8)) / 2;
            _btn60Hz = MakeButton(cardQuickCooling, "60 Hz", cPad, S(44), dispW, S(32));
            _btnMaxHz = MakeButton(cardQuickCooling, $"{_maxHz} Hz (Maximum)", cPad + dispW + S(8), S(44), dispW, S(32));
            _btn60Hz.Click += (s, e) => ApplyDisplayMode(60, _btn60Hz);
            _btnMaxHz.Click += (s, e) => ApplyDisplayMode(_maxHz, _btnMaxHz);

            _lblLcdOverdriveStatus = MakeLabel(cardQuickCooling, "LCD Overdrive (Fast Response Time)", cPad, S(90), FontBody, ThemeManager.TextPrimary);
            _switchLcdOverdrive = new PredatorSwitch
            {
                Location = new Point(cardW - cPad - S(48), S(86)),
                Size = new Size(S(48), S(26))
            };
            _switchLcdOverdrive.CheckedChanged += (s, e) => ApplyLcdOverdrive(_switchLcdOverdrive.Checked);
            cardQuickCooling.Controls.Add(_switchLcdOverdrive);

            MakeLabel(cardQuickCooling, "Acer CoolBoost (Maximum Fan Speed Boost)", cPad, S(126), FontBody, ThemeManager.TextPrimary);
            _switchCoolBoost = new PredatorSwitch
            {
                Location = new Point(cardW - cPad - S(48), S(122)),
                Size = new Size(S(48), S(26))
            };
            _switchCoolBoost.Checked = _wmi.GetCoolBoost() ?? false;
            _switchCoolBoost.CheckedChanged += (s, e) =>
            {
                bool on = _switchCoolBoost.Checked;
                if (!_isInitializing)
                {
                    SaveState("CoolBoost", on ? 1 : 0);
                    SaveStateToHklm("CoolBoost", on ? 1 : 0);
                }
                Task.Run(() => _wmi.SetCoolBoost(on));
            };
            cardQuickCooling.Controls.Add(_switchCoolBoost);

            // Card 5: Quick Navigation Shortcuts
            var cardQuickJump = MakeCard(_pnlDashboard, "QUICK NAVIGATION & SHORTCUTS", col2X, pad + S(372), cardW, S(184));
            var btnJumpFans = MakeButton(cardQuickJump, "💨  Open Fan Controls & Speed Locking", cPad, S(44), cInnerW, S(34));
            btnJumpFans.Click += (s, e) => SwitchPage(_pnlFans, _btnNavFans);

            var btnJumpSys = MakeButton(cardQuickJump, "⚡  Open Graphics & Power Automation", cPad, S(88), cInnerW, S(34));
            btnJumpSys.Click += (s, e) => SwitchPage(_pnlSystem, _btnNavSystem);

            var btnJumpRgb = MakeButton(cardQuickJump, "✨  Open RGB Lighting Studio", cPad, S(132), cInnerW, S(34));
            btnJumpRgb.Click += (s, e) => SwitchPage(_pnlLighting, _btnNavLighting);

            _pnlDashboard.AutoScrollMinSize = new Size(0, S(580));
        }

        private void BuildFansPage()
        {
            int pad = S(16);
            int gap = S(16);
            int usableW = _pageContainer.Width - pad * 2 - gap - DarkScrollPanel.NativeBarWidth;
            int cardW = usableW / 2;
            int col1X = pad;
            int col2X = pad + cardW + gap;
            int cPad = S(14);
            int cInnerW = cardW - cPad * 2;

            // Card 1: Fan Modes & Auto-Switching
            var cardFanModes = MakeCard(_pnlFans, "FAN MODES & AUTOMATION", col1X, pad, cardW, S(230));
            int fanBtnW = (cInnerW - 2 * S(8)) / 3;
            int btnH = S(34);
            _btnAutoFan = MakeButton(cardFanModes, "Auto", cPad, S(44), fanBtnW, btnH);
            _btnMaxFan = MakeButton(cardFanModes, "Maximum", cPad + fanBtnW + S(8), S(44), fanBtnW, btnH);
            _btnCustomFan = MakeButton(cardFanModes, "Custom", cPad + 2 * (fanBtnW + S(8)), S(44), fanBtnW, btnH);

            _btnAutoFan.Click += (s, e) => ApplyFanMode(0x01, _btnAutoFan);
            _btnMaxFan.Click += (s, e) => ApplyFanMode(0x02, _btnMaxFan);
            _btnCustomFan.Click += (s, e) => ApplyFanMode(0x03, _btnCustomFan);

            _lblFanLockStatus = MakeLabel(cardFanModes, "🔓 Fan Lock: All Fans Unlocked (Manual & Curve Ready)", cPad, S(90), FontHeaderLight, AccentColor, "accent");

            int dropW = (cInnerW - S(10)) / 2;
            _lblAcFanHdr = MakeLabel(cardFanModes, "ON AC POWER:", cPad, S(116), FontSectionHeader, SubHeaderColor);
            _lblBatteryFanHdr = MakeLabel(cardFanModes, "ON BATTERY:", cPad + dropW + S(10), S(116), FontSectionHeader, SubHeaderColor);

            _cboAcFan = new PredatorDropDown { Location = new Point(cPad, S(140)), Size = new Size(dropW, S(30)) };
            _cboAcFan.Items.AddRange(new[] { "Don't Change", "Auto", "Maximum", "Custom" });
            _cboAcFan.SelectedIndex = 0;
            cardFanModes.Controls.Add(_cboAcFan);

            _cboBatteryFan = new PredatorDropDown { Location = new Point(cPad + dropW + S(10), S(140)), Size = new Size(dropW, S(30)) };
            _cboBatteryFan.Items.AddRange(new[] { "Don't Change", "Auto", "Maximum", "Custom" });
            _cboBatteryFan.SelectedIndex = 0;
            cardFanModes.Controls.Add(_cboBatteryFan);

            _cboAcFan.SelectedIndexChanged += (s, e) =>
            {
                if (_isInitializing) return;
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
                if (_isInitializing) return;
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

            MakeLabel(cardFanModes, "Automatic fan profile rules trigger upon connecting or disconnecting charger.", cPad, S(186), FontHeaderLight, SubHeaderColor);

            // Card 2: Custom Manual Speeds & Independent FanLock
            int customCardH = _capabilities.HasThirdFan ? S(290) : S(230);
            var cardCustomFans = MakeCard(_pnlFans, "CUSTOM MANUAL FAN SPEEDS & SPEED LOCKING", col1X, pad + S(242), cardW, customCardH);
            int subW = (cInnerW - S(8)) / 2;
            _btnFixedSpeed = MakeButton(cardCustomFans, "Fixed Speed", cPad, S(44), subW, btnH);
            _btnFanCurve = MakeButton(cardCustomFans, "Dynamic Curve", cPad + subW + S(8), S(44), subW, btnH);

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

                _cpuFanSlider.Enabled = !_isCpuFanLocked;
                _gpuFanSlider.Enabled = !_isGpuFanLocked;
                if (_sysFanSlider != null) _sysFanSlider.Enabled = !_isSysFanLocked;

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
            };

            int sldW = cInnerW - S(110);
            _lblCpuFanSpeedHdr = MakeLabel(cardCustomFans, "CPU FAN: 50%", cPad, S(88), FontSectionHeader, SubHeaderColor);
            _cpuFanSlider = new PredatorSlider
            {
                Location = new Point(cPad, S(110)),
                Size = new Size(sldW, S(28)),
                Minimum = 10, Maximum = 100, Value = 50
            };
            _btnLockCpu = MakeButton(cardCustomFans, "🔓 Lock CPU", cPad + sldW + S(8), S(108), S(102), S(30));
            cardCustomFans.Controls.Add(_cpuFanSlider);

            _cpuFanSlider.ValueChanged += (s, e) => _lblCpuFanSpeedHdr.Text = $"CPU FAN: {_cpuFanSlider.Value}%";
            _cpuFanSlider.ValueCommitted += (s, e) =>
            {
                int val = _cpuFanSlider.Value;
                Task.Run(() => _wmi.SetCpuFanSpeed((byte)val));
                SaveState("FanSpeedCpu", val);
                SaveFanSpeedForProfile("Cpu", val);
            };
            _btnLockCpu.Click += (s, e) =>
            {
                _isCpuFanLocked = !_isCpuFanLocked;
                _btnLockCpu.Text = _isCpuFanLocked ? "🔒 CPU Locked" : "🔓 Lock CPU";
                _btnLockCpu.IsActive = _isCpuFanLocked;
                _cpuFanSlider.Enabled = !_isCpuFanLocked && !_fanCurveEnabled;
                UpdateFanLockStatus();
            };

            _lblGpuFanSpeedHdr = MakeLabel(cardCustomFans, "GPU FAN: 50%", cPad, S(148), FontSectionHeader, SubHeaderColor);
            _gpuFanSlider = new PredatorSlider
            {
                Location = new Point(cPad, S(170)),
                Size = new Size(sldW, S(28)),
                Minimum = 10, Maximum = 100, Value = 50
            };
            _btnLockGpu = MakeButton(cardCustomFans, "🔓 Lock GPU", cPad + sldW + S(8), S(168), S(102), S(30));
            cardCustomFans.Controls.Add(_gpuFanSlider);

            _gpuFanSlider.ValueChanged += (s, e) => _lblGpuFanSpeedHdr.Text = $"GPU FAN: {_gpuFanSlider.Value}%";
            _gpuFanSlider.ValueCommitted += (s, e) =>
            {
                int val = _gpuFanSlider.Value;
                Task.Run(() => _wmi.SetGpuFanSpeed((byte)val));
                SaveState("FanSpeedGpu", val);
                SaveFanSpeedForProfile("Gpu", val);
            };
            _btnLockGpu.Click += (s, e) =>
            {
                _isGpuFanLocked = !_isGpuFanLocked;
                _btnLockGpu.Text = _isGpuFanLocked ? "🔒 GPU Locked" : "🔓 Lock GPU";
                _btnLockGpu.IsActive = _isGpuFanLocked;
                _gpuFanSlider.Enabled = !_isGpuFanLocked && !_fanCurveEnabled;
                UpdateFanLockStatus();
            };

            if (_capabilities.HasThirdFan)
            {
                _lblSysFanSpeedHdr = MakeLabel(cardCustomFans, "SYSTEM FAN: 50%", cPad, S(208), FontSectionHeader, SubHeaderColor);
                _sysFanSlider = new PredatorSlider
                {
                    Location = new Point(cPad, S(230)),
                    Size = new Size(sldW, S(28)),
                    Minimum = 10, Maximum = 100, Value = 50
                };
                _btnLockSys = MakeButton(cardCustomFans, "🔓 Lock System", cPad + sldW + S(8), S(228), S(102), S(30));
                cardCustomFans.Controls.Add(_sysFanSlider);

                _sysFanSlider.ValueChanged += (s, e) => _lblSysFanSpeedHdr.Text = $"SYSTEM FAN: {_sysFanSlider.Value}%";
                _sysFanSlider.ValueCommitted += (s, e) =>
                {
                    int val = _sysFanSlider.Value;
                    Task.Run(() => _wmi.SetSystemFanSpeed((byte)val));
                    SaveState("FanSpeedSys", val);
                    SaveFanSpeedForProfile("Sys", val);
                };
                _btnLockSys.Click += (s, e) =>
                {
                    _isSysFanLocked = !_isSysFanLocked;
                    _btnLockSys.Text = _isSysFanLocked ? "🔒 System Locked" : "🔓 Lock System";
                    _btnLockSys.IsActive = _isSysFanLocked;
                    if (_sysFanSlider != null) _sysFanSlider.Enabled = !_isSysFanLocked && !_fanCurveEnabled;
                    UpdateFanLockStatus();
                };
            }
            else
            {
                _lblSysFanSpeedHdr = null;
                _sysFanSlider = null;
                _btnLockSys = null;
            }

            // Card 3: Factory Cooling Presets
            int techCardY = pad + S(242) + customCardH + S(12);
            var cardCoolingTech = MakeCard(_pnlFans, "FACTORY COOLING PRESETS", col1X, techCardY, cardW, S(116));
            MakeLabel(cardCoolingTech, "FACTORY FAN PRESET (SPEED TABLE):", cPad, S(40), FontSectionHeader, SubHeaderColor);
            _cboFanTable = new PredatorDropDown { Location = new Point(cPad, S(64)), Size = new Size(cInnerW, S(30)) };
            _cboFanTable.Items.AddRange(new[] { "Standard", "Faster", "Fastest" });
            var curTable = _wmi.GetFanTable() ?? FanTable.Standard;
            _cboFanTable.SelectedIndex = Math.Clamp((int)curTable - 1, 0, 2);
            _cboFanTable.SelectedIndexChanged += (s, e) =>
            {
                if (_isInitializing) return;
                var table = (FanTable)(_cboFanTable.SelectedIndex + 1);
                Task.Run(() => _wmi.SetFanTable(table));
            };
            cardCoolingTech.Controls.Add(_cboFanTable);

            // Card 4 (Right Column): Interactive Fan Curves
            var cardCurves = MakeCard(_pnlFans, "INTERACTIVE FAN CURVES", col2X, pad, cardW, S(640));
            MakeLabel(cardCurves, "Drag points (Temp °C vs Speed %) to customize hardware response curves.", cPad, S(38), FontHeaderLight, SubHeaderColor);

            int curveH = S(210);
            _embeddedCurveCpu = new FanCurveGraph
            {
                CurveColor = Color.FromArgb(0, 180, 255),
                FanLabel = "CPU FAN CURVE",
                Location = new Point(cPad, S(60)),
                Size = new Size(cInnerW, curveH),
                Points = _cpuCurvePoints
            };
            cardCurves.Controls.Add(_embeddedCurveCpu);

            _embeddedCurveGpu = new FanCurveGraph
            {
                CurveColor = Color.FromArgb(255, 77, 109),
                FanLabel = "GPU FAN CURVE",
                Location = new Point(cPad, S(280)),
                Size = new Size(cInnerW, curveH),
                Points = _gpuCurvePoints
            };
            cardCurves.Controls.Add(_embeddedCurveGpu);

            int curveBtnW = (cInnerW - S(10)) / 2;
            _btnEmbeddedResetCurve = MakeButton(cardCurves, "↺  Reset Defaults", cPad, S(504), curveBtnW, S(36));
            _btnEmbeddedApplyCurve = MakeButton(cardCurves, "✓  Apply Curves", cPad + curveBtnW + S(10), S(504), curveBtnW, S(36));

            _btnEmbeddedResetCurve.Click += (s, e) =>
            {
                lock (_curveLock)
                {
                    if (_embeddedCurveCpu != null)
                    {
                        _embeddedCurveCpu.Points = FanCurveGraph.DefaultCpuPoints;
                        _cpuCurvePoints = new List<Point>(_embeddedCurveCpu.Points);
                    }
                    if (_embeddedCurveGpu != null)
                    {
                        _embeddedCurveGpu.Points = FanCurveGraph.DefaultGpuPoints;
                        _gpuCurvePoints = new List<Point>(_embeddedCurveGpu.Points);
                    }
                }
                SaveCurveToRegistry("CpuCurve", _cpuCurvePoints);
                SaveCurveToRegistry("GpuCurve", _gpuCurvePoints);
                if (_fanCurveEnabled)
                {
                    ApplyFanCurve(forceHardwareApply: true);
                }
            };

            _btnEmbeddedApplyCurve.Click += (s, e) =>
            {
                lock (_curveLock)
                {
                    if (_embeddedCurveCpu != null) _cpuCurvePoints = new List<Point>(_embeddedCurveCpu.Points);
                    if (_embeddedCurveGpu != null) _gpuCurvePoints = new List<Point>(_embeddedCurveGpu.Points);
                }
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

                _btnEmbeddedApplyCurve.CustomActiveColor = Color.FromArgb(0, 180, 80);
                _btnEmbeddedApplyCurve.IsActive = true;
                Task.Delay(1500).ContinueWith(_ =>
                {
                    SafeBeginInvoke(() =>
                    {
                        if (_btnEmbeddedApplyCurve != null)
                        {
                            _btnEmbeddedApplyCurve.IsActive = false;
                            _btnEmbeddedApplyCurve.CustomActiveColor = null;
                        }
                    });
                });
            };

            MakeLabel(cardCurves, "💡 Curves apply in real time when fan profile is set to Custom > Dynamic Curve.", cPad, S(552), FontHeaderLight, SubHeaderColor);

            int totalH = Math.Max(techCardY + S(126), S(660));
            _pnlFans.AutoScrollMinSize = new Size(0, totalH);
        }

        private void BuildLightingPage()
        {
            int pad = S(16);
            int gap = S(16);
            int usableW = _pageContainer.Width - pad * 2 - gap - DarkScrollPanel.NativeBarWidth;
            int cardW = usableW / 2;
            int col1X = pad;
            int col2X = pad + cardW + gap;
            int cPad = S(14);
            int cInnerW = cardW - cPad * 2;

            // Card 1: 4-Zone Keyboard Lighting
            var cardRgb = MakeCard(_pnlLighting, "KEYBOARD RGB LIGHTING", col1X, pad, cardW, S(280));
            MakeLabel(cardRgb, "DYNAMIC LIGHTING EFFECTS:", cPad, S(44), FontSectionHeader, SubHeaderColor);
            _rgbDropDown = new PredatorDropDown { Location = new Point(cPad, S(68)), Size = new Size(cInnerW, S(34)) };
            foreach (var name in RgbModeNames) _rgbDropDown.Items.Add(name);
            _rgbDropDown.SelectedIndex = 3;
            _rgbDropDown.SelectedIndexChanged += (s, e) =>
            {
                if (_isGameSyncOverriding || _isApplyingRgbMode) return;
                int mode = _rgbDropDown.SelectedIndex;
                ApplyRgbModeFromDropdown(mode);
            };
            cardRgb.Controls.Add(_rgbDropDown);

            _btnColorPick = MakeButton(cardRgb, "    🎨  Choose Custom Color", cPad, S(120), cInnerW, S(38));
            _btnColorPick.Paint += (s, e) =>
            {
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
                    _isApplyingRgbMode = true;
                    try
                    {
                        Task.Run(() => _wmi.SetRgbMode(0, c.R, c.G, c.B, (byte)bVal, spd, 0));
                        _rgbDropDown.SelectedIndex = 0;
                        SaveState("RGB_Mode", 0);
                        SaveState("RGB_R", c.R); SaveState("RGB_G", c.G); SaveState("RGB_B", c.B);
                        UpdateRgbControls(0);
                        CheckRgbTrayFromMode(0);
                    }
                    finally
                    {
                        _isApplyingRgbMode = false;
                    }
                }
            };

            MakeLabel(cardRgb, "Control 4-Zone dynamic animation effects or configure static solid color per zone.", cPad, S(180), FontHeaderLight, SubHeaderColor);

            // Card 2: Lighting Settings
            var cardParams = MakeCard(_pnlLighting, "LIGHTING SETTINGS", col2X, pad, cardW, S(210));
            _lblBrightHdr = MakeLabel(cardParams, "BRIGHTNESS: 100%", cPad, S(44), FontSectionHeader, SubHeaderColor);
            _brightnessSlider = new PredatorSlider { Location = new Point(cPad, S(68)), Size = new Size(cInnerW, S(28)), Minimum = 0, Maximum = 100, Value = 100 };
            _brightnessSlider.ValueChanged += (s, e) =>
            {
                if (IsOnBattery() && _brightnessSlider.Value == 0 && !_manualBacklightOnBattery)
                    _lblBrightHdr.Text = "BRIGHTNESS: Off (Battery Saver)";
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
                    if (onBat) _lblBrightHdr.Text = "BRIGHTNESS: Off (Battery Saver)";
                }
                else
                {
                    Task.Run(() => _wmi.SetBrightness((byte)bVal));
                }
            };
            cardParams.Controls.Add(_brightnessSlider);

            _lblSpeedHdr = MakeLabel(cardParams, "EFFECT SPEED: 50%", cPad, S(110), FontSectionHeader, SubHeaderColor);
            _speedSlider = new PredatorSlider { Location = new Point(cPad, S(134)), Size = new Size(cInnerW, S(28)), Minimum = 1, Maximum = 100, Value = 50 };
            _speedSlider.ValueChanged += (s, e) => { _lblSpeedHdr.Text = $"EFFECT SPEED: {_speedSlider.Value}%"; };
            _speedSlider.ValueCommitted += (s, e) =>
            {
                byte spd = GetMappedSpeed();
                Task.Run(() => _wmi.SetSpeed(spd));
                SaveState("RGB_Speed", _speedSlider.Value);
            };
            cardParams.Controls.Add(_speedSlider);

            _pnlLighting.AutoScrollMinSize = new Size(0, S(250));
        }

        private void BuildSystemPage()
        {
            int pad = S(16);
            int gap = S(16);
            int usableW = _pageContainer.Width - pad * 2 - gap - DarkScrollPanel.NativeBarWidth;
            int cardW = usableW / 2;
            int col1X = pad;
            int col2X = pad + cardW + gap;
            int cPad = S(14);
            int cInnerW = cardW - cPad * 2;

            // Card 1: Graphics Working Mode (Display Switch)
            var cardMux = MakeCard(_pnlSystem, "GRAPHICS WORKING MODE (DISPLAY SWITCH)", col1X, pad, cardW, S(200));
            MakeLabel(cardMux, "Display routing: Dedicated connects GPU directly for best gaming performance; Integrated saves battery.", cPad, S(44), FontHeaderLight, SubHeaderColor);

            int muxBtnW = (cInnerW - 2 * S(8)) / 3;
            int btnH = S(36);
            _btnGpuAuto = MakeButton(cardMux, "🔄  Automatic (Dynamic)", cPad, S(76), muxBtnW, btnH);
            _btnGpuHybrid = MakeButton(cardMux, "🔋  Integrated Only", cPad + muxBtnW + S(8), S(76), muxBtnW, btnH);
            _btnGpuDiscrete = MakeButton(cardMux, "⚡  Dedicated Only", cPad + 2 * (muxBtnW + S(8)), S(76), muxBtnW, btnH);

            var curGpuMode = _wmi.GetGpuMode() ?? GpuMode.Auto;
            if (curGpuMode == GpuMode.dGpuOnly) HighlightBtn(_btnGpuDiscrete, ref _activeGpuModeBtn);
            else if (curGpuMode == GpuMode.iGpuOnly) HighlightBtn(_btnGpuHybrid, ref _activeGpuModeBtn);
            else HighlightBtn(_btnGpuAuto, ref _activeGpuModeBtn);

            _btnGpuAuto.Click += (s, e) => ApplyGpuMode(GpuMode.Auto, _btnGpuAuto);
            _btnGpuHybrid.Click += (s, e) => ApplyGpuMode(GpuMode.iGpuOnly, _btnGpuHybrid);
            _btnGpuDiscrete.Click += (s, e) => ApplyGpuMode(GpuMode.dGpuOnly, _btnGpuDiscrete);

            MakeLabel(cardMux, "Switching graphics working mode requires a restart to reinitialize display hardware.", cPad, S(132), FontHeaderLight, SubHeaderColor);

            // Card 2: Power Profile Auto-Switching & Mode Key
            var cardPowerAuto = MakeCard(_pnlSystem, "AUTOMATIC POWER PROFILES & MODE KEY", col1X, pad + S(212), cardW, S(250));
            int pDropW = (cInnerW - S(10)) / 2;
            _lblAcProfileHdr = MakeLabel(cardPowerAuto, "ON AC POWER:", cPad, S(44), FontSectionHeader, SubHeaderColor);
            _lblBatteryProfileHdr = MakeLabel(cardPowerAuto, "ON BATTERY:", cPad + pDropW + S(10), S(44), FontSectionHeader, SubHeaderColor);

            _cboAcProfile = new PredatorDropDown { Location = new Point(cPad, S(68)), Size = new Size(pDropW, S(30)) };
            _cboAcProfile.Items.AddRange(new[] { "Don't Change", "Quiet", "Balanced", "Performance", "Turbo" });
            _cboAcProfile.SelectedIndex = 0;
            cardPowerAuto.Controls.Add(_cboAcProfile);

            _cboBatteryProfile = new PredatorDropDown { Location = new Point(cPad + pDropW + S(10), S(68)), Size = new Size(pDropW, S(30)) };
            _cboBatteryProfile.Items.AddRange(new[] { "Don't Change", "Quiet", "Balanced", "Eco" });
            _cboBatteryProfile.SelectedIndex = 0;
            cardPowerAuto.Controls.Add(_cboBatteryProfile);

            _cboAcProfile.SelectedIndexChanged += (s, e) =>
            {
                if (_isInitializing) return;
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
                if (_isInitializing) return;
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

            MakeLabel(cardPowerAuto, "PHYSICAL MODE KEY ACTION:", cPad, S(112), FontSectionHeader, SubHeaderColor);
            _cboModeKeyActionSys = new PredatorDropDown
            {
                Location = new Point(cPad, S(136)),
                Size = new Size(cInnerW, S(30))
            };
            _cboModeKeyActionSys.Items.AddRange(new[] { "Cycle Power Modes", "Toggle Turbo Mode (Return to Previous)" });
            _cboModeKeyActionSys.SelectedIndex = _modeKeyAction;
            _cboModeKeyActionSys.SelectedIndexChanged += (s, e) =>
            {
                if (_isInitializing) return;
                _modeKeyAction = _cboModeKeyActionSys.SelectedIndex;
                if (_cboModeKeyAction != null && _cboModeKeyAction.SelectedIndex != _modeKeyAction)
                    _cboModeKeyAction.SelectedIndex = _modeKeyAction;
                SaveState("ModeKeyAction", _modeKeyAction);
            };
            cardPowerAuto.Controls.Add(_cboModeKeyActionSys);

            MakeLabel(cardPowerAuto, "Profiles switch automatically on AC/Battery. Mode key cycles or toggles Turbo.", cPad, S(180), FontHeaderLight, SubHeaderColor);

            // Card 3: Display & LCD Overdrive
            var cardDisp = MakeCard(_pnlSystem, "DISPLAY REFRESH RATE & OVERDRIVE", col2X, pad, cardW, S(160));
            int dispBtnW = (cInnerW - S(8)) / 2;
            _btnDisp60 = MakeButton(cardDisp, "60 Hz", cPad, S(44), dispBtnW, S(34));
            _btnDispMax = MakeButton(cardDisp, $"{_maxHz} Hz (Maximum)", cPad + dispBtnW + S(8), S(44), dispBtnW, S(34));
            _btnDisp60.Click += (s, e) => ApplyDisplayMode(60, _btnDisp60);
            _btnDispMax.Click += (s, e) => ApplyDisplayMode(_maxHz, _btnDispMax);

            _lblOdStatusSys = MakeLabel(cardDisp, "LCD Overdrive (Fast Response Time)", cPad, S(98), FontBody, ThemeManager.TextPrimary);
            _swOdSys = new PredatorSwitch { Location = new Point(cardW - cPad - S(48), S(94)), Size = new Size(S(48), S(26)) };
            _swOdSys.Checked = _switchLcdOverdrive.Checked;
            _swOdSys.CheckedChanged += (s, e) => ApplyLcdOverdrive(_swOdSys.Checked);
            cardDisp.Controls.Add(_swOdSys);

            // Card 4: USB Charging & Battery Maintenance
            var cardUsb = MakeCard(_pnlSystem, "USB CHARGING & BATTERY MAINTENANCE", col2X, pad + S(172), cardW, S(240));
            MakeLabel(cardUsb, "Charge USB Devices While Laptop is Off", cPad, S(48), FontBody, ThemeManager.TextPrimary);
            _switchUsbCharging = new PredatorSwitch { Location = new Point(cardW - cPad - S(48), S(44)), Size = new Size(S(48), S(26)) };
            var curUsb = _wmi.GetUsbCharging();
            _switchUsbCharging.Checked = curUsb?.Enabled ?? true;
            cardUsb.Controls.Add(_switchUsbCharging);

            MakeLabel(cardUsb, "STOP CHARGING WHEN BATTERY REACHES:", cPad, S(84), FontSectionHeader, SubHeaderColor);
            _cboUsbFloor = new PredatorDropDown { Location = new Point(cPad, S(108)), Size = new Size(cInnerW, S(30)) };
            _cboUsbFloor.Items.AddRange(new[] { "Stop at 10% Battery", "Stop at 20% Battery", "Stop at 30% Battery" });
            int floorIdx = curUsb?.FloorPercent switch { 10 => 0, 20 => 1, _ => 2 };
            _cboUsbFloor.SelectedIndex = floorIdx;
            cardUsb.Controls.Add(_cboUsbFloor);

            _switchUsbCharging.CheckedChanged += (s, e) =>
            {
                bool isChecked = _switchUsbCharging.Checked;
                int floor = _cboUsbFloor.SelectedIndex switch { 0 => 10, 1 => 20, _ => 30 };
                Task.Run(() => _wmi.SetUsbCharging(isChecked, floor));
            };
            _cboUsbFloor.SelectedIndexChanged += (s, e) =>
            {
                bool isChecked = _switchUsbCharging.Checked;
                int floor = _cboUsbFloor.SelectedIndex switch { 0 => 10, 1 => 20, _ => 30 };
                Task.Run(() => _wmi.SetUsbCharging(isChecked, floor));
            };

            _btnBatteryCalibration = MakeButton(cardUsb, "⚡  Start Battery Calibration", cPad, S(156), cInnerW, S(34));
            _btnBatteryCalibration.Click += (s, e) =>
            {
                _btnBatteryCalibration.Enabled = false;
                Task.Run(() =>
                {
                    bool ok = _wmi.SetBatteryCalibration(true);
                    SafeBeginInvoke(() =>
                    {
                        if (!IsDisposed && _btnBatteryCalibration != null)
                        {
                            _btnBatteryCalibration.Enabled = true;
                            MessageBox.Show(this, ok ? "Hardware battery calibration initiated. Keep AC adapter connected until calibration completes." : "Could not start calibration cycle.", "Battery Calibration", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                        }
                    });
                });
            };

            // Card 5: Game Sync
            var cardGame = MakeCard(_pnlSystem, "GAME SYNC PROFILES", col2X, pad + S(424), cardW, S(150));
            _lblGameSyncStatus = MakeLabel(cardGame, "Disabled", cPad, S(48), FontBody, SubHeaderColor);
            _switchGameSync = new PredatorToggle { Location = new Point(cardW - cPad - S(48), S(44)), Size = new Size(S(48), S(24)) };
            _switchGameSync.CheckedChanged += (s, e) =>
            {
                _gameSync.IsEnabled = _switchGameSync.Checked;
                _lblGameSyncStatus.Text = _switchGameSync.Checked ? "Active (Monitoring)" : "Disabled";
            };
            cardGame.Controls.Add(_switchGameSync);

            _btnConfigureGames = MakeButton(cardGame, "🎮  Configure Game Profiles", cPad, S(84), cInnerW, S(34));
            _btnConfigureGames.Click += (s, e) =>
            {
                using var form = new GameSyncForm(_gameSync, _maxHz);
                form.ShowDialog(this);
            };

            _pnlSystem.AutoScrollMinSize = new Size(0, S(600));
        }

        private void BuildSettingsPage()
        {
            int pad = S(16);
            int gap = S(16);
            int usableW = _pageContainer.Width - pad * 2 - gap - DarkScrollPanel.NativeBarWidth;
            int cardW = usableW / 2;
            int col1X = pad;
            int col2X = pad + cardW + gap;
            int cPad = S(14);
            int cInnerW = cardW - cPad * 2;

            // Card 1: Appearance & Theme
            var cardApp = MakeCard(_pnlSettings, "APPEARANCE & THEME", col1X, pad, cardW, S(170));
            MakeLabel(cardApp, "APPLICATION THEME:", cPad, S(44), FontSectionHeader, SubHeaderColor);
            _cboTheme = new PredatorDropDown { Location = new Point(cPad, S(68)), Size = new Size(cInnerW, S(30)) };
            _cboTheme.Items.AddRange(new[] { "System Default", "Dark Mode", "Light Mode" });
            _cboTheme.SelectedIndex = (int)ThemeManager.CurrentTheme;
            _cboTheme.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressThemeChange) return;
                var theme = (AppTheme)_cboTheme.SelectedIndex;
                ThemeManager.SetTheme(theme);
                ThemeManager.ApplyTheme(this);
                _appSettings.Theme = theme.ToString();
                _appSettings.Save();
                if (_btnThemeToggle != null) _btnThemeToggle.Text = ThemeManager.IsDarkThemeActive ? "🌙" : "☀️";
            };
            cardApp.Controls.Add(_cboTheme);
            MakeLabel(cardApp, "Instant seamless theme switching with WCAG 2.2 AA compliant contrast.", cPad, S(116), FontHeaderLight, SubHeaderColor);

            // Card 2: Startup & Automation
            var cardStartup = MakeCard(_pnlSettings, "STARTUP & AUTOMATION", col1X, pad + S(182), cardW, S(160));
            _lblStartupStatus = MakeLabel(cardStartup, "Start Predator Control with Windows", cPad, S(48), FontBody, SubHeaderColor);
            _switchStartWithWindows = new PredatorToggle { Location = new Point(cardW - cPad - S(48), S(44)), Size = new Size(S(48), S(24)) };
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
                    if (!IsDisposed && _switchStartWithWindows.IsHandleCreated) _switchStartWithWindows.Enabled = true;
                }
                if (ok) return;
                _suppressStartupToggle = true;
                _switchStartWithWindows.Checked = !wanted;
                _suppressStartupToggle = false;
                MessageBox.Show(this, wanted ? "Could not register Predator Control to start with Windows. Scheduled task could not be created." : "Could not remove startup task.", "Start with Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            cardStartup.Controls.Add(_switchStartWithWindows);
            MakeLabel(cardStartup, "Launches minimized to system tray via scheduled task with no UAC prompt.", cPad, S(92), FontHeaderLight, SubHeaderColor);

            // Card 3: Hardware Diagnostics
            var cardDiag = MakeCard(_pnlSettings, "HARDWARE DIAGNOSTICS", col2X, pad, cardW, S(120));
            _btnDiagnostics = MakeButton(cardDiag, "📋  Copy System Info to Clipboard", cPad, S(44), cInnerW, S(34));
            _btnDiagnostics.Click += (s, e) =>
            {
                var pStatus = SystemInformation.PowerStatus;
                bool onBatt = _backlightMgr.IsOnBattery(pStatus.PowerLineStatus, pStatus.BatteryChargeStatus);
                bool asleep = onBatt || GpuPowerMonitor.IsGpuAsleep();
                var pdh = _pdhMonitor.Sample(!asleep);
                int? sysRpm = _capabilities.HasThirdFan ? _wmi.SystemFanRpm : null;
                string report = DiagnosticsDumper.GenerateReport(_wmi, _capabilities, pdh, asleep, sysRpm);
                DiagnosticsDumper.CopyToClipboard(report);
                MessageBox.Show(this, "Diagnostic report copied to clipboard!", "Predator Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            // Card 4: Application Updates & About
            var cardUpdates = MakeCard(_pnlSettings, "APPLICATION UPDATES & ABOUT", col2X, pad + S(132), cardW, S(160));
            MakeLabel(cardUpdates, $"Version v.{Updater.CurrentText}", cPad, S(48), FontBodyBold, ThemeManager.TextPrimary);
            _btnCheckUpdates = MakeButton(cardUpdates, "⬇  Check for Updates", cardW - cPad - S(160), S(44), S(160), S(32));
            _btnCheckUpdates.Click += async (s, e) => await CheckForUpdatesAsync();

            MakeLabel(cardUpdates, "SHA-256 verified single-file updater. Zero background network polling.", cPad, S(92), FontHeaderLight, SubHeaderColor);

            _pnlSettings.AutoScrollMinSize = new Size(0, S(360));
        }

        private Panel MakeCard(Control parent, string title, int x, int y, int w, int h)
        {
            var card = new Panel
            {
                Name = "card_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Tag = "card",
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = ThemeManager.CardBg
            };

            card.Paint += (s, e) =>
            {
                using var pen = new Pen(ThemeManager.CardBorder, 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            var lblHeader = new Label
            {
                Text = UiLabelFormatter.CleanUnderscores(title),
                Font = FontSectionHeader,
                ForeColor = ThemeManager.HeaderText,
                Location = new Point(S(14), S(12)),
                AutoSize = true,
                BackColor = Color.Transparent,
                Tag = "header",
                UseMnemonic = false
            };
            card.Controls.Add(lblHeader);

            var sep = new Panel
            {
                Location = new Point(S(14), S(32)),
                Size = new Size(w - S(28), 1),
                BackColor = ThemeManager.Separator
            };
            card.Controls.Add(sep);

            parent.Controls.Add(card);
            return card;
        }

        private Label MakeLabel(Control parent, string text, int x, int y, Font font, Color color, string? tag = null)
        {
            var lbl = new Label
            {
                Text = UiLabelFormatter.CleanUnderscores(text),
                Location = new Point(x, y),
                AutoSize = true,
                Font = font,
                ForeColor = color,
                BackColor = Color.Transparent,
                Tag = tag,
                UseMnemonic = false
            };
            parent.Controls.Add(lbl);
            return lbl;
        }

        private PredatorButton MakeButton(Control parent, string text, int x, int y, int width, int height)
        {
            var btn = new PredatorButton { Text = UiLabelFormatter.CleanUnderscores(text), Location = new Point(x, y), Size = new Size(width, height) };
            parent.Controls.Add(btn);
            return btn;
        }

        private void UpdateRgbControls(int mode)
        {
            bool hasSpeed = mode != 0;
            _speedSlider.Enabled = hasSpeed;
            _btnColorPick.Enabled = mode == 0;
        }

        private void MakeSectionHeader(string label, int x, int y)
        {
            MakeLabel((Control?)_contentPanel ?? this, label, x, y, FontSectionHeader, HeaderColor);
        }

        private Label MakeLabel(string text, int x, int y, Font font, Color color)
        {
            return MakeLabel((Control?)_contentPanel ?? this, text, x, y, font, color);
        }

        private PredatorButton MakeButton(string text, int x, int y, int width, int height)
        {
            return MakeButton((Control?)_contentPanel ?? this, text, x, y, width, height);
        }

        private void AddSeparator(int y)
        {
            int pad = S(16);
            int w = (_contentPanel != null ? _contentPanel.Width : _formW) - pad * 2;
            (_contentPanel ?? (Control)this).Controls.Add(new Panel { Location = new Point(pad, y), Size = new Size(w, 1), BackColor = SeparatorColor });
        }

        private void CenterV(Label lbl, int controlY, int controlH)
        {
            lbl.Location = new Point(lbl.Left, controlY + (controlH - lbl.Height) / 2);
        }

        #endregion

        #region Action Handlers

        private void SaveStateToHklm(string name, int val)
        {
            if (_isInitializing) return;
            Task.Run(() =>
            {
                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\PredatorControl");
                    key?.SetValue(name, val);
                }
                catch { }
            });
        }

        public static Color GetPowerModeColor(byte mode) => ThemeManager.GetPowerModeColor(mode);

        public static Color GetPowerModePillColor(byte mode) => ThemeManager.GetPowerModePillColor(mode);
        public static string GetPowerModePillColorHex(byte mode) => ThemeManager.GetPowerModePillColorHex(mode);
        public static IReadOnlyList<Point> DefaultCpuCurve => FanCurveGraph.DefaultCpuPoints;

        private void UpdatePowerModePill(byte mode)
        {
            if (_lblModePill == null) return;
            string modeName = mode switch
            {
                0x00 => "Quiet",
                0x04 => "Performance",
                0x05 => "Turbo",
                0x06 => "Eco",
                _ => "Balanced"
            };
            _lblModePill.Text = $"⚡ {modeName.ToUpperInvariant()}";
            _lblModePill.ForeColor = GetPowerModeColor(mode);
        }

        private void ApplyPowerMode(byte mode, PredatorButton btn)
        {
            HighlightBtn(btn, ref _activePowerBtn);
            _currentPowerMode = mode;
            if (!_isGameSyncOverriding)
            {
                SaveState("Power", mode);
                SaveStateToHklm("Power", mode);

                var power = SystemInformation.PowerStatus;
                bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                bool onBattery = !isCharging && (power.PowerLineStatus == PowerLineStatus.Offline || (_isPluggedIn == false) || _backlightMgr.IsOnBattery(power.PowerLineStatus, power.BatteryChargeStatus));
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
                UpdatePowerStatusUI();
            }

            UpdatePowerModePill(mode);

            var trayItem = mode switch
            {
                0x00 => _trayPowerQuiet,
                0x04 => _trayPowerPerf,
                0x05 => _trayPowerTurbo,
                0x06 => _trayPowerEco,
                _ => _trayPowerBal
            };
            CheckTrayItem(trayItem, _trayPowerQuiet, _trayPowerBal, _trayPowerPerf, _trayPowerTurbo, _trayPowerEco);

            QueueHardwarePowerMode(mode);
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

        private int _pendingHardwarePowerMode = -1;
        private readonly object _powerModeQueueLock = new();
        private bool _isApplyingHardwarePowerMode;

        private void QueueHardwarePowerMode(byte mode)
        {
            lock (_powerModeQueueLock)
            {
                _pendingHardwarePowerMode = mode;
                if (_isApplyingHardwarePowerMode) return;
                _isApplyingHardwarePowerMode = true;
            }

            Task.Run(() =>
            {
                while (true)
                {
                    int targetMode;
                    lock (_powerModeQueueLock)
                    {
                        targetMode = _pendingHardwarePowerMode;
                        _pendingHardwarePowerMode = -1;
                        if (targetMode < 0)
                        {
                            _isApplyingHardwarePowerMode = false;
                            break;
                        }
                    }

                    try
                    {
                        _wmi.SetPowerMode((byte)targetMode);
                    }
                    catch { }
                }
            });
        }

        private void HandleModeKeyPress()
        {
            if (_isClosing || IsDisposed) return;
            if ((DateTime.UtcNow - _lastModeKeyUtc).TotalMilliseconds < 350)
                return;
            _lastModeKeyUtc = DateTime.UtcNow;

            if (_modeKeyAction == 1) // Turbo Toggle
            {
                if (IsOnBattery())
                {
                    CyclePowerMode();
                    return;
                }

                if (_currentPowerMode != 0x05)
                {
                    _turboReturnMode = _currentPowerMode;
                    SaveState("TurboReturnMode", _turboReturnMode);
                    ApplyPowerMode(0x05, _btnTurbo);
                }
                else
                {
                    byte returnMode = (_turboReturnMode == 0x05 || _turboReturnMode == 0x06) ? (byte)0x01 : _turboReturnMode;
                    var returnBtn = PowerByteToBtn(returnMode);
                    if (returnBtn == null || !returnBtn.Enabled)
                    {
                        returnMode = 0x01;
                        returnBtn = _btnBalanced;
                    }
                    ApplyPowerMode(returnMode, returnBtn);
                }
            }
            else
            {
                CyclePowerMode();
            }
        }

        private void CyclePowerMode()
        {
            var modes = _capabilities.OperatingModes;
            if (modes == null || modes.Count == 0)
            {
                bool onBattery = IsOnBattery();
                modes = onBattery
                    ? new[] { OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Eco }
                    : new[] { OperatingMode.Quiet, OperatingMode.Balanced, OperatingMode.Performance, OperatingMode.Turbo };
            }

            var allowedModes = modes.Where(m =>
            {
                var btn = PowerByteToBtn((byte)m);
                return btn != null && btn.Enabled;
            }).ToList();
            if (allowedModes.Count == 0) allowedModes = modes.ToList();

            int currentIndex = allowedModes.FindIndex(m => (byte)m == _currentPowerMode);
            int nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % allowedModes.Count;
            byte nextMode = (byte)allowedModes[nextIndex];
            ApplyPowerMode(nextMode, PowerByteToBtn(nextMode));
        }

        private void UpdateFanLockStatus()
        {
            if (_lblFanLockStatus == null) return;
            var lockState = GetCurrentFanLock();
            if (lockState == FanLock.QuietMode)
            {
                _lblFanLockStatus.Text = "🔒 Fan Lock: Locked by Quiet Mode (Preset Hardware Curve)";
                _lblFanLockStatus.ForeColor = Color.FromArgb(255, 180, 50);
            }
            else if (lockState == FanLock.EcoMode)
            {
                _lblFanLockStatus.Text = "🔒 Fan Lock: Locked by Eco Mode (Preset Hardware Curve)";
                _lblFanLockStatus.ForeColor = Color.FromArgb(255, 180, 50);
            }
            else
            {
                bool anyLocked = _isCpuFanLocked || _isGpuFanLocked || _isSysFanLocked;
                if (anyLocked)
                {
                    var lockedList = new List<string>();
                    if (_isCpuFanLocked) lockedList.Add("CPU");
                    if (_isGpuFanLocked) lockedList.Add("GPU");
                    if (_isSysFanLocked) lockedList.Add("System");
                    _lblFanLockStatus.Text = $"🔒 Fan Lock: {string.Join(" + ", lockedList)} Locked (Manual Override)";
                    _lblFanLockStatus.ForeColor = AccentColor;
                }
                else
                {
                    _lblFanLockStatus.Text = "🔓 Fan Lock: All Fans Unlocked (Manual & Curve Ready)";
                    _lblFanLockStatus.ForeColor = Color.FromArgb(100, 180, 120);
                }
            }
        }

        private FanLock? GetCurrentFanLock()
        {
            byte currentPower = GetCurrentPowerByte();
            if (currentPower == 0x00) return FanLock.QuietMode;
            if (currentPower == 0x06) return FanLock.EcoMode;
            return null;
        }

        private async void ApplyGpuMode(GpuMode mode, PredatorButton btn)
        {
            try
            {
                HighlightBtn(btn, ref _activeGpuModeBtn);
                bool ok = await Task.Run(() => _wmi.SetGpuMode(mode));
                if (!ok)
                {
                    MessageBox.Show(this, "Failed to switch GPU working mode via ACPI/WMI.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string modeName = mode switch
                {
                    GpuMode.Auto => "Automatic (Dynamic)",
                    GpuMode.iGpuOnly => "Integrated Only",
                    GpuMode.dGpuOnly => "Dedicated Only",
                    _ => mode.ToString()
                };
                var res = MessageBox.Show(this,
                    $"Graphics Working Mode switched to {modeName}.\r\n\r\nA system restart is required for the display switch to take effect. Would you like to restart now?",
                    "Graphics Mode Restart Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (res == DialogResult.Yes)
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });
                        }
                        catch (Exception ex)
                        {
                            Program.Report(ex, false);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Program.Report(ex, false);
            }
        }

        private void ApplyLcdOverdrive(bool enable)
        {
            if (_switchLcdOverdrive.Checked != enable)
                _switchLcdOverdrive.Checked = enable;
            if (_swOdSys != null && _swOdSys.Checked != enable)
                _swOdSys.Checked = enable;
            if (_trayLcdOverdrive != null)
                _trayLcdOverdrive.Checked = enable;
            _lblLcdOverdriveStatus.Text = enable ? "LCD Overdrive (Fast Response Time)" : "LCD Overdrive (Standard)";
            _lblLcdOverdriveStatus.ForeColor = enable ? AccentColor : Color.White;
            if (_lblOdStatusSys != null)
            {
                _lblOdStatusSys.Text = enable ? "LCD Overdrive (Fast Response Time)" : "LCD Overdrive (Standard)";
                _lblOdStatusSys.ForeColor = enable ? AccentColor : Color.White;
            }
            if (!_isInitializing)
            {
                SaveState("LcdOverdrive", enable ? 1 : 0);
                SaveStateToHklm("LcdOverdrive", enable ? 1 : 0);
            }
            Task.Run(() => _wmi.SetLcdOverdrive(enable));
        }

        private void ApplyFanMode(byte mode, PredatorButton btn)
        {
            HighlightBtn(btn, ref _activeFanBtn);
            if (!_isGameSyncOverriding)
            {
                SaveState("Fan", mode);
                SaveStateToHklm("Fan", mode);

                var power = SystemInformation.PowerStatus;
                bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                bool onBattery = !isCharging && (power.PowerLineStatus == PowerLineStatus.Offline || (_isPluggedIn == false) || _backlightMgr.IsOnBattery(power.PowerLineStatus, power.BatteryChargeStatus));
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
            if (_btnLockCpu != null) _btnLockCpu.Visible = isCustom;
            if (_btnLockGpu != null) _btnLockGpu.Visible = isCustom;
            if (_btnLockSys != null) _btnLockSys.Visible = isCustom;

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
                    _cpuFanSlider.Enabled = !isLocked && !_isCpuFanLocked;
                    _gpuFanSlider.Enabled = !isLocked && !_isGpuFanLocked;
                    if (_sysFanSlider != null) _sysFanSlider.Enabled = !isLocked && !_isSysFanLocked;
                    byte cpuVal = (byte)_cpuFanSlider.Value;
                    byte gpuVal = (byte)_gpuFanSlider.Value;
                    byte sysVal = (byte)(_sysFanSlider?.Value ?? 50);
                    Task.Run(() =>
                    {
                        _wmi.SetFanBehavior(0x03, applyCustomSpeeds: true);
                        if (!_isCpuFanLocked) _wmi.SetCpuFanSpeed(cpuVal);
                        if (!_isGpuFanLocked) _wmi.SetGpuFanSpeed(gpuVal);
                        if (!_isSysFanLocked) _wmi.SetSystemFanSpeed(sysVal);
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
            lock (_curveLock)
            {
                _fanCurveForm.SetCpuCurve(_cpuCurvePoints.ToList());
                _fanCurveForm.SetGpuCurve(_gpuCurvePoints.ToList());
            }
            _fanCurveForm.UpdateTemps(_cpuTemp, _gpuTemp);

            _fanCurveForm.ApplyClicked += (s, args) =>
            {
                lock (_curveLock)
                {
                    _cpuCurvePoints = args.CpuPoints;
                    _gpuCurvePoints = args.GpuPoints;
                }
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
            if (_isInitializing) return;
            try
            {
                var curveData = points.Select(CurvePointData.FromPoint).ToList();
                if (name == "CpuCurve") _appSettings.CpuCurve = curveData;
                else if (name == "GpuCurve") _appSettings.GpuCurve = curveData;
                _appSettings.Save();
            }
            catch { }

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
            // Primary: load from Registry as the canonical user preference
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

                if (!string.IsNullOrEmpty(data))
                {
                    var pts = new List<Point>();
                    foreach (var pair in data.Split(';'))
                    {
                        var parts = pair.Split(',');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                            pts.Add(new Point(x, y));
                    }
                    if (pts.Count >= 2)
                    {
                        var norm = FanCurveGraph.Normalize(pts);
                        if (name == "CpuCurve") _appSettings.CpuCurve = norm.Select(CurvePointData.FromPoint).ToList();
                        else if (name == "GpuCurve") _appSettings.GpuCurve = norm.Select(CurvePointData.FromPoint).ToList();
                        return norm;
                    }
                }
            }
            catch { }

            // Secondary: check settings.json, but guard against flat-line dummy test corruptions
            try
            {
                var settings = AppSettings.Load();
                var rawCurve = name == "CpuCurve" ? settings.CpuCurve : settings.GpuCurve;
                if (rawCurve != null && rawCurve.Count >= 2)
                {
                    bool isFlatDummy = rawCurve.All(p => p.Y == rawCurve[0].Y);
                    if (!isFlatDummy)
                    {
                        var norm = FanCurveGraph.Normalize(rawCurve.Select(p => p.ToPoint()).ToList());
                        if (name == "CpuCurve") _appSettings.CpuCurve = norm.Select(CurvePointData.FromPoint).ToList();
                        else if (name == "GpuCurve") _appSettings.GpuCurve = norm.Select(CurvePointData.FromPoint).ToList();
                        return norm;
                    }
                }
            }
            catch { }

            return name == "GpuCurve" ? new List<Point>(FanCurveGraph.DefaultGpuPoints) : new List<Point>(FanCurveGraph.DefaultCpuPoints);
        }

        private void ApplyDisplayMode(int hz, PredatorButton btn)
        {
            if (!SetRefreshRate(hz)) return;
            bool is60 = hz <= 60;
            _btn60Hz.IsActive = is60;
            _btnMaxHz.IsActive = !is60;
            if (_btnDisp60 != null) _btnDisp60.IsActive = is60;
            if (_btnDispMax != null) _btnDispMax.IsActive = !is60;
            _activeDisplayBtn = is60 ? _btn60Hz : _btnMaxHz;
            CheckTrayItem(is60 ? _trayDisplay60 : _trayDisplayMax, _trayDisplay60, _trayDisplayMax);
            if (!_isInitializing)
            {
                SaveState("RefreshRate", hz);
            }
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

                Task.Run(() => LightingEffectsManager.ApplyEffect(mode, _wmi, r, g, b, (byte)bright, (byte)speed, (byte)0, (OperatingMode)_currentPowerMode));

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
            return _currentPowerMode;
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
                SafeBeginInvoke(() => OnGameDetected(profile));
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
                int mode = Math.Clamp(profile.RgbMode, 0, RgbModeNames.Length - 1);
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
                SafeBeginInvoke(() => OnGameExited(snap));
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

            int rgbMode = Math.Clamp(snap.RgbMode, 0, RgbModeNames.Length - 1);
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
            if (_isInitializing || _isGameSyncOverriding) return; 
            Task.Run(() =>
            {
                lock (_saveLock)
                {
                    try
                    {
                        switch (name)
                        {
                            case "Power": _appSettings.PowerMode = (byte)value; break;
                            case "Power_AC": _appSettings.PowerModeAC = (byte)value; break;
                            case "Power_Battery": _appSettings.PowerModeBattery = (byte)value; break;
                            case "AutoPowerAC": _appSettings.AutoPowerAC = value; break;
                            case "AutoPowerBattery": _appSettings.AutoPowerBattery = value; break;
                            case "ModeKeyAction": _appSettings.ModeKeyAction = value; break;
                            case "TurboReturnMode": _appSettings.TurboReturnMode = (byte)value; break;
                            case "Fan": _appSettings.FanMode = (byte)value; break;
                            case "Fan_AC": _appSettings.FanModeAC = (byte)value; break;
                            case "Fan_Battery": _appSettings.FanModeBattery = (byte)value; break;
                            case "AutoFanAC": _appSettings.AutoFanAC = value; break;
                            case "AutoFanBattery": _appSettings.AutoFanBattery = value; break;
                            case "FanCurveEnabled": _appSettings.FanCurveEnabled = value == 1; break;
                            case "FanSpeedCpu": _appSettings.FanSpeedCpu = value; break;
                            case "FanSpeedGpu": _appSettings.FanSpeedGpu = value; break;
                            case "FanSpeedSys": _appSettings.FanSpeedSys = value; break;
                            case "FanSpeedCpuAC": _appSettings.FanSpeedCpuAC = value; break;
                            case "FanSpeedCpuBattery": _appSettings.FanSpeedCpuBattery = value; break;
                            case "FanSpeedGpuAC": _appSettings.FanSpeedGpuAC = value; break;
                            case "FanSpeedGpuBattery": _appSettings.FanSpeedGpuBattery = value; break;
                            case "FanSpeedSysAC": _appSettings.FanSpeedSysAC = value; break;
                            case "FanSpeedSysBattery": _appSettings.FanSpeedSysBattery = value; break;
                            case "RefreshRate": _appSettings.RefreshRate = value; break;
                            case "LcdOverdrive": _appSettings.LcdOverdrive = value == 1; break;
                            case "BatteryLimit": _appSettings.BatteryLimit = value == 1; break;
                            case "CoolBoost": _appSettings.CoolBoost = value == 1; break;
                            case "RGB_Mode": _appSettings.RgbMode = value; break;
                            case "Brightness": _appSettings.RgbBrightness = value; break;
                            case "RGB_Speed": _appSettings.RgbSpeed = value; break;
                            case "RGB_R": _appSettings.RgbR = value; break;
                            case "RGB_G": _appSettings.RgbG = value; break;
                            case "RGB_B": _appSettings.RgbB = value; break;
                        }
                        _appSettings.Theme = ThemeManager.CurrentTheme.ToString();
                        _appSettings.Save();
                    }
                    catch { }

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
            });
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
                var settings = AppSettings.Load();
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");
                using var hklmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PredatorControl");

                int maxRgb = RgbModeNames.Length - 1;
                int savedRgbMode = settings.RgbMode;
                if (savedRgbMode < 0 || savedRgbMode > maxRgb) savedRgbMode = GetInt(key, "RGB_Mode", -1, 0, maxRgb);
                if (savedRgbMode == -1) savedRgbMode = GetInt(hklmKey, "RGB_Mode", 3, 0, maxRgb);

                int savedBrightness = settings.RgbBrightness;
                int savedSpeed = settings.RgbSpeed;

                _backlightMgr.SavedAcBrightness = Math.Clamp(savedBrightness, 0, 100);
                var initLine = SystemInformation.PowerStatus.PowerLineStatus;
                var initCharge = SystemInformation.PowerStatus.BatteryChargeStatus;
                bool onBatteryStartup = _backlightMgr.IsOnBattery(initLine, initCharge);
                bool isChargingStartup = (initCharge & BatteryChargeStatus.Charging) != 0;
                _isPluggedIn = !onBatteryStartup || isChargingStartup || initLine == PowerLineStatus.Online;
                _pendingPluggedIn = _isPluggedIn;
                _powerLineStableTicks = 2;
                UpdatePowerStatusUI();

                int savedR = settings.RgbR;
                int savedG = settings.RgbG;
                int savedB = settings.RgbB;
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

                int savedAutoAc = Math.Clamp(settings.AutoPowerAC, 0, _cboAcProfile.Items.Count - 1);
                int savedAutoBat = Math.Clamp(settings.AutoPowerBattery, 0, _cboBatteryProfile.Items.Count - 1);

                _cboAcProfile.SelectedIndex = savedAutoAc;
                _cboBatteryProfile.SelectedIndex = savedAutoBat;

                int savedAutoFanAc = Math.Clamp(settings.AutoFanAC, 0, _cboAcFan.Items.Count - 1);
                int savedAutoFanBat = Math.Clamp(settings.AutoFanBattery, 0, _cboBatteryFan.Items.Count - 1);

                _cboAcFan.SelectedIndex = savedAutoFanAc;
                _cboBatteryFan.SelectedIndex = savedAutoFanBat;

                _modeKeyAction = Math.Clamp(settings.ModeKeyAction, 0, 1);
                _turboReturnMode = settings.TurboReturnMode;
                if (_cboModeKeyAction != null) _cboModeKeyAction.SelectedIndex = _modeKeyAction;
                if (_cboModeKeyActionSys != null) _cboModeKeyActionSys.SelectedIndex = _modeKeyAction;

                int savedPower = -1;
                if (onBatteryStartup)
                {
                    if (savedAutoBat > 0 && savedAutoBat < BatteryProfileValues.Length)
                    {
                        savedPower = BatteryProfileValues[savedAutoBat];
                    }
                    else
                    {
                        savedPower = settings.PowerModeBattery ?? settings.PowerMode;
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
                        savedPower = settings.PowerModeAC ?? settings.PowerMode;
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
                        savedFan = settings.FanModeBattery ?? settings.FanMode;
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
                        savedFan = settings.FanModeAC ?? settings.FanMode;
                    }
                }

                if (savedFan < 1 || savedFan > 3) savedFan = 0x01;

                int savedFanSpeedCpu = onBatteryStartup ? settings.FanSpeedCpuBattery : settings.FanSpeedCpuAC;
                if (savedFanSpeedCpu <= 0) savedFanSpeedCpu = settings.FanSpeedCpu;
                int savedFanSpeedGpu = onBatteryStartup ? settings.FanSpeedGpuBattery : settings.FanSpeedGpuAC;
                if (savedFanSpeedGpu <= 0) savedFanSpeedGpu = settings.FanSpeedGpu;
                int savedFanSpeedSys = onBatteryStartup ? settings.FanSpeedSysBattery : settings.FanSpeedSysAC;
                if (savedFanSpeedSys <= 0) savedFanSpeedSys = settings.FanSpeedSys;

                _cpuFanSlider.Value = Math.Clamp(savedFanSpeedCpu, 10, 100);
                _gpuFanSlider.Value = Math.Clamp(savedFanSpeedGpu, 10, 100);
                if (_sysFanSlider != null) _sysFanSlider.Value = Math.Clamp(savedFanSpeedSys, 10, 100);
                _lblCpuFanSpeedHdr.Text = $"CPU FAN: {_cpuFanSlider.Value}%";
                _lblGpuFanSpeedHdr.Text = $"GPU FAN: {_gpuFanSlider.Value}%";
                if (_lblSysFanSpeedHdr != null && _sysFanSlider != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {_sysFanSlider.Value}%";

                var (fanMode, fanBtn) = savedFan switch
                {
                    0x02 => ((byte)0x02, _btnMaxFan),
                    0x03 => ((byte)0x03, _btnCustomFan),
                    _ => ((byte)0x01, _btnAutoFan)
                };

                var loadedCpu = LoadCurveFromRegistry("CpuCurve");
                _cpuCurvePoints = loadedCpu ?? new List<Point>(FanCurveGraph.DefaultCpuPoints);

                var loadedGpu = LoadCurveFromRegistry("GpuCurve");
                _gpuCurvePoints = loadedGpu ?? new List<Point>(FanCurveGraph.DefaultGpuPoints);

                if (_embeddedCurveCpu != null) _embeddedCurveCpu.Points = _cpuCurvePoints;
                if (_embeddedCurveGpu != null) _embeddedCurveGpu.Points = _gpuCurvePoints;

                if ((settings.FanCurveEnabled || GetInt(key, "FanCurveEnabled", 0, 0, 1) == 1) && fanMode == 0x03)
                    _fanCurveEnabled = true;

                ApplyFanMode(fanMode, fanBtn);

                if (fanMode == 0x03)
                {
                    if (_fanCurveEnabled)
                    {
                        HighlightBtn(_btnFanCurve, ref _activeCustomSubBtn);
                        ApplyFanCurve(forceHardwareApply: true);
                    }
                    else
                    {
                        HighlightBtn(_btnFixedSpeed, ref _activeCustomSubBtn);
                        int cpuVal = _cpuFanSlider.Value;
                        int gpuVal = _gpuFanSlider.Value;
                        int sysVal = (_capabilities.HasThirdFan && _sysFanSlider != null) ? _sysFanSlider.Value : 0;
                        Task.Run(() =>
                        {
                            _wmi.SetFanBehavior(0x03, applyCustomSpeeds: true);
                            _wmi.SetCpuFanSpeed((byte)cpuVal);
                            _wmi.SetGpuFanSpeed((byte)gpuVal);
                            if (_capabilities.HasThirdFan && sysVal > 0) _wmi.SetSystemFanSpeed((byte)sysVal);
                        });
                    }
                }

                int clampedMode = Math.Clamp(savedRgbMode, 0, maxRgb);
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

                bool limitEnabled = settings.BatteryLimit || GetInt(key, "BatteryLimit", 0, 0, 1) == 1;
                ApplyBatteryLimit(limitEnabled);

                Task.Run(() =>
                {
                    try
                    {
                        bool supported = _wmi.IsBatteryControlSupported();
                        if (!supported)
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
                                SafeBeginInvoke(disableAction);
                            }
                            else
                            {
                                HandleCreated += (s, e) => SafeBeginInvoke(disableAction);
                            }
                        }
                    }
                    catch { }
                });

                bool lcdOdEnabled = settings.LcdOverdrive;
                ApplyLcdOverdrive(lcdOdEnabled);

                if (settings.RefreshRate > 0)
                {
                    ApplyDisplayMode(settings.RefreshRate, settings.RefreshRate <= 60 ? _btn60Hz : _btnMaxHz);
                }

                bool coolBoost = settings.CoolBoost || GetInt(key, "CoolBoost", 0, 0, 1) == 1;
                if (_switchCoolBoost != null)
                {
                    _switchCoolBoost.Checked = coolBoost;
                }
                Task.Run(() => _wmi.SetCoolBoost(coolBoost));

                int savedGpuMode = settings.GpuMode;
                if (savedGpuMode < 0 || savedGpuMode > 2) savedGpuMode = GetInt(key, "GpuMode", 0, 0, 2);
                var targetGpuBtn = savedGpuMode switch
                {
                    1 => _btnGpuHybrid,
                    2 => _btnGpuDiscrete,
                    _ => _btnGpuAuto
                };
                if (targetGpuBtn != null)
                {
                    HighlightBtn(targetGpuBtn, ref _activeGpuModeBtn);
                }
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
                var immediatePower = SystemInformation.PowerStatus;
                if (_backlightMgr.IsOnBattery(immediatePower.PowerLineStatus, immediatePower.BatteryChargeStatus) && !_manualBacklightOnBattery)
                {
                    EnforceBacklightOffOnBattery();
                }

                SafeBeginInvoke(async () => await ResyncAfterResume());
            }
            else if (e.Mode == PowerModes.StatusChange)
            {
                SafeBeginInvoke(() =>
                {
                    if (_isClosing || IsDisposed) return;
                    var power = SystemInformation.PowerStatus;
                    bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                    bool onBattery = !isCharging && (power.PowerLineStatus == PowerLineStatus.Offline || (_isPluggedIn == false) || _backlightMgr.IsOnBattery(power.PowerLineStatus, power.BatteryChargeStatus));
                    bool isAc = !onBattery;
                    _backlightMgr.SetPluggedInState(isAc);
                    if (_isPluggedIn != isAc)
                    {
                        _isPluggedIn = isAc;
                        if (_gameSync?.ActiveGameExe == null)
                        {
                            ApplyPowerRules(isAc);
                        }
                        ScheduleRgbRestoreWatchdog();
                    }
                    UpdatePowerStatusUI();
                });
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
                var initialCharge = SystemInformation.PowerStatus.BatteryChargeStatus;
                if (_backlightMgr.IsOnBattery(initialLine, initialCharge) && !_manualBacklightOnBattery)
                {
                    EnforceBacklightOffOnBattery();
                }

                // Wait 2.5s for ACPI, display, and thermal drivers to stabilize after resume
                await Task.Delay(2500);
                if (_isClosing || IsDisposed) return;

                var power = SystemInformation.PowerStatus;
                bool onBattery = _backlightMgr.IsOnBattery(power.PowerLineStatus, power.BatteryChargeStatus);
                bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                bool isPluggedIn = power.PowerLineStatus == PowerLineStatus.Online || isCharging || (!onBattery && _isPluggedIn == true);

                _isPluggedIn = isPluggedIn;
                _pendingPluggedIn = isPluggedIn;
                _powerLineStableTicks = 2;
                _backlightMgr.SetPluggedInState(isPluggedIn);

                // Re-apply power & fan rules based on current live AC/Battery state (unless Game Sync is active)
                if (_gameSync?.ActiveGameExe == null)
                {
                    ApplyPowerRules(isPluggedIn);
                    ApplyFanRules(isPluggedIn);
                }
                UpdatePowerStatusUI();

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
                            int selectedRgbMode = _rgbDropDown?.SelectedIndex ?? -1;
                            await Task.Run(() =>
                            {
                                _wmi.SetBrightness((byte)targetBright);
                                if (selectedRgbMode >= 0)
                                    _wmi.ApplyLightingMode(selectedRgbMode);
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

        internal void UpdatePowerStatusUI()
        {
            if (_isClosing || IsDisposed) return;
            try
            {
                var power = SystemInformation.PowerStatus;
                bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                bool isAc = power.PowerLineStatus == PowerLineStatus.Online || isCharging || _isPluggedIn == true;

                if (_lblPowerPill != null)
                {
                    if (isCharging)
                    {
                        _lblPowerPill.Text = "⚡ CHARGING";
                        _lblPowerPill.ForeColor = AccentColor;
                    }
                    else if (isAc)
                    {
                        _lblPowerPill.Text = "🔌 AC";
                        _lblPowerPill.ForeColor = ThemeManager.TextSecondary;
                    }
                    else
                    {
                        _lblPowerPill.Text = "🔋 BATTERY";
                        _lblPowerPill.ForeColor = ThemeManager.TextSecondary;
                    }
                }

                if (_lblPowerStatus != null)
                {
                    if (isCharging)
                        _lblPowerStatus.Text = "Charging";
                    else if (isAc)
                        _lblPowerStatus.Text = "Plugged In";
                    else
                        _lblPowerStatus.Text = "On Battery";
                }
            }
            catch { }
        }

        internal static bool? DebouncePowerLine(PowerLineStatus line, bool? current, ref bool? pending, ref int ticks, BatteryChargeStatus chargeStatus = 0)
        {
            bool isCharging = (chargeStatus & BatteryChargeStatus.Charging) != 0;
            if (line == PowerLineStatus.Unknown && !isCharging)
            {
                pending = null;
                ticks = 0;
                return current;
            }

            bool pluggedIn = line == PowerLineStatus.Online || isCharging;

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

            var powerStatus = SystemInformation.PowerStatus;
            bool? confirmed = DebouncePowerLine(powerStatus.PowerLineStatus,
                                                _isPluggedIn, ref _pendingPluggedIn, ref _powerLineStableTicks,
                                                powerStatus.BatteryChargeStatus);

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
                finally
                {
                    _isPluggedIn = confirmed;
                    _backlightMgr.SetPluggedInState(confirmed);
                }
            }
            else
            {
                _backlightMgr.SetPluggedInState(_isPluggedIn);
            }

            var lineStatus = powerStatus.PowerLineStatus;
            bool isCharging = (powerStatus.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
            bool onBattery = !isCharging && (lineStatus == PowerLineStatus.Offline || (_isPluggedIn == false) || _backlightMgr.IsOnBattery(lineStatus, powerStatus.BatteryChargeStatus));
            bool isAc = !onBattery;
            _backlightMgr.SetPluggedInState(isAc);
            _wmi.SuppressGpuQueries = onBattery;
            bool isConnectedToCharger = !onBattery && (lineStatus == PowerLineStatus.Online || isCharging || _isPluggedIn == true);

            UpdatePowerStatusUI();

            // Strict cadence required: strictly 2000ms on AC/charging, strictly 5000ms on battery (never throttled to 8s)
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
                    if (_brightnessSlider.Value == 0)
                    {
                        AcerServiceClient.ResetConnectionState();
                        int targetBright = _backlightMgr.SavedAcBrightness;
                        _brightnessSlider.Value = targetBright;
                        _lblBrightHdr.Text = $"BRIGHTNESS: {targetBright}%";
                        int selectedRgbMode = _rgbDropDown?.SelectedIndex ?? -1;
                        Task.Run(() =>
                        {
                            _wmi.SetBrightness((byte)targetBright);
                            if (selectedRgbMode >= 0)
                                _wmi.ApplyLightingMode(selectedRgbMode);
                        });
                    }
                }
            }

            if (_isSleeping || _isLidClosed)
                return;

            bool isWindowVisible = this.Visible && this.WindowState != FormWindowState.Minimized;

            if (Interlocked.CompareExchange(ref _telemetryRunning, 1, 0) != 0)
                return;

            Task.Run(() =>
            {
                var prevPriority = Thread.CurrentThread.Priority;
                try
                {
                    // Run telemetry with BelowNormal thread priority so Windows audio engine (AudioDG.exe) and video renderer are never preempted
                    try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                    int cpuTemp = _wmi.CpuTemp;
                    int cpuRpm = isWindowVisible ? _wmi.CpuFanRpm : 0;
                    int sysRpm = isWindowVisible ? _wmi.SystemFanRpm : 0;
                    bool gpuAsleep = GpuPowerMonitor.IsGpuAsleep();

                    int gpuTemp = 0;
                    int gpuRpm = 0;
                    double? cpuLoad = null;
                    double? gpuLoad = null;

                    bool queryGpu = !onBattery && (_isPluggedIn == true) && !gpuAsleep;
                    if (queryGpu)
                    {
                        gpuTemp = _wmi.GpuTemp;
                        if (isWindowVisible)
                        {
                            gpuRpm = _wmi.GpuFanRpm;
                        }
                    }

                    if (isWindowVisible)
                    {
                        var (sampledCpu, sampledGpu) = _pdhMonitor.Sample(queryGpu);
                        cpuLoad = sampledCpu;
                        gpuLoad = sampledGpu;
                    }

                    SafeBeginInvoke(() =>
                    {
                        try
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

                            if (!onBattery && _isPluggedIn == true && _currentPowerMode != 0x06 && (DateTime.UtcNow - _lastFirmwareReassert).TotalSeconds >= 30)
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

                            if (isWindowVisible)
                            {
                                _lblCpuTemp.Text = _cpuTemp > 0 ? $"{_cpuTemp}°C" : "--°C";
                                _lblGpuTemp.Text = _gpuTemp > 0 ? $"{_gpuTemp}°C" : (gpuAsleep || onBattery ? "Asleep" : "--°C");
                                _lblCpuTemp.ForeColor = TempColor(_cpuTemp);
                                _lblGpuTemp.ForeColor = TempColor(_gpuTemp);

                                if (_lblGpuSleepBadge != null)
                                {
                                    _lblGpuSleepBadge.Visible = true;
                                    _lblGpuSleepBadge.Text = (gpuAsleep || onBattery) ? "[ 💤 Low Power Standby ]" : "[ 🔥 Active ]";
                                    _lblGpuSleepBadge.ForeColor = (gpuAsleep || onBattery) ? Color.FromArgb(120, 200, 255) : AccentColor;
                                }

                                if (_lblProtocolBadge != null)
                                {
                                    _lblProtocolBadge.Text = _wmi.EcHid != null && _wmi.EcHid.IsOpen ? "⚡ Direct Hardware Controller" : "🔌 Acer System Driver";
                                }

                                _lblCpuRpm.Text = cpuRpm > 0 ? $"{cpuRpm} RPM" : "-- RPM";
                                _lblGpuRpm.Text = gpuRpm > 0 ? $"{gpuRpm} RPM" : "-- RPM";
                                if (_lblSysFanRpm != null)
                                {
                                    _lblSysFanRpm.Text = sysRpm > 0 ? $"{sysRpm} RPM" : "-- RPM";
                                }
                                UpdateFanLockStatus();

                                _cpuHistoryGraph?.PushSample(_cpuTemp > 0 ? _cpuTemp : null, cpuLoad);
                                _gpuHistoryGraph?.PushSample(_gpuTemp > 0 ? _gpuTemp : null, (gpuAsleep || onBattery) ? null : gpuLoad);

                                if (_embeddedCurveCpu != null) _embeddedCurveCpu.CurrentTemp = _cpuTemp;
                                if (_embeddedCurveGpu != null) _embeddedCurveGpu.CurrentTemp = _gpuTemp;
                            }

                            string trayText = $"Predator Control\nCPU: {(_cpuTemp > 0 ? $"{_cpuTemp}°C" : "N/A")}  GPU: {(_gpuTemp > 0 ? $"{_gpuTemp}°C" : ((gpuAsleep || onBattery) ? "Asleep" : "N/A"))}";
                            _trayIcon.Text = trayText.Length > 63 ? trayText.Substring(0, 63) : trayText;

                            if (_fanCurveForm != null && !_fanCurveForm.IsDisposed && _fanCurveForm.Visible)
                                _fanCurveForm.UpdateTemps(_cpuTemp, _gpuTemp);

                            ApplyFanCurve();
                        }
                        catch (Exception ex)
                        {
                            Program.Report(ex, false);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Program.Report(ex, false);
                }
                finally
                {
                    try { Thread.CurrentThread.Priority = prevPriority; } catch { }
                    Interlocked.Exchange(ref _telemetryRunning, 0);
                }
            });
        }

        private void ApplyFanCurve(bool forceHardwareApply = false)
        {
            if (this.InvokeRequired)
            {
                SafeBeginInvoke(() => ApplyFanCurve(forceHardwareApply));
                return;
            }

            if (!_fanCurveEnabled) return;
            if (GetCurrentFanByte() != 0x03) return;

            List<Point> cpuPoints;
            List<Point> gpuPoints;
            lock (_curveLock)
            {
                cpuPoints = _cpuCurvePoints.ToList();
                gpuPoints = _gpuCurvePoints.ToList();
            }

            var pState = SystemInformation.PowerStatus;
            bool onBattery = _backlightMgr.IsOnBattery(pState.PowerLineStatus, pState.BatteryChargeStatus);
            bool cpuValid = _cpuTemp > 0 && _cpuTemp <= 125;
            bool gpuValid = !onBattery && _gpuTemp > 0 && _gpuTemp <= 125 && !GpuPowerMonitor.IsGpuAsleep();

            if (!cpuValid && !gpuValid)
            {
                return;
            }

            bool cpuChanged = false;
            int targetCpuSpeed = 0;
            int currentCpuTemp = 0;

            if (cpuValid)
            {
                // 3-sample rolling average temperature damping
                lock (_dampingLock)
                {
                    _cpuDampingQueue.Enqueue(_cpuTemp);
                    while (_cpuDampingQueue.Count > 3) _cpuDampingQueue.Dequeue();
                    currentCpuTemp = (int)Math.Round(_cpuDampingQueue.Average());
                }

                targetCpuSpeed = _cpuCurveFollower.Update(currentCpuTemp, cpuPoints);
                targetCpuSpeed = Math.Clamp(targetCpuSpeed, 10, 100);
                cpuChanged = forceHardwareApply || (_lastCurveCpuSpeed != targetCpuSpeed);

                if (!_isCpuFanLocked && cpuChanged)
                {
                    _lastCurveCpuSpeed = targetCpuSpeed;
                    _lastCurveCpuTemp = currentCpuTemp;
                    _cpuFanSlider.Value = targetCpuSpeed;
                    _lblCpuFanSpeedHdr.Text = $"CPU FAN: {targetCpuSpeed}%";
                }
            }

            bool gpuChanged = false;
            int targetGpuSpeed = 0;
            if (gpuValid)
            {
                int currentGpuTemp;
                lock (_dampingLock)
                {
                    _gpuDampingQueue.Enqueue(_gpuTemp);
                    while (_gpuDampingQueue.Count > 3) _gpuDampingQueue.Dequeue();
                    currentGpuTemp = (int)Math.Round(_gpuDampingQueue.Average());
                }

                targetGpuSpeed = _gpuCurveFollower.Update(currentGpuTemp, gpuPoints);
                targetGpuSpeed = Math.Clamp(targetGpuSpeed, 10, 100);
                gpuChanged = forceHardwareApply || (_lastCurveGpuSpeed != targetGpuSpeed);

                if (!_isGpuFanLocked && gpuChanged)
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
            if (_sysFanSlider != null && cpuValid)
            {
                targetSysSpeed = _sysCurveFollower.Update(currentCpuTemp, cpuPoints);
                targetSysSpeed = Math.Clamp(targetSysSpeed, 10, 100);
                sysChanged = forceHardwareApply || (_lastCurveSysSpeed != targetSysSpeed);

                if (!_isSysFanLocked && sysChanged)
                {
                    _lastCurveSysSpeed = targetSysSpeed;
                    _sysFanSlider.Value = targetSysSpeed;
                    if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {targetSysSpeed}%";
                }
            }

            if (cpuChanged || gpuChanged || sysChanged || forceHardwareApply)
            {
                int cSpeed = _isCpuFanLocked ? _cpuFanSlider.Value : (cpuChanged ? targetCpuSpeed : _lastCurveCpuSpeed);
                if (cSpeed <= 0) cSpeed = targetCpuSpeed;

                int gSpeed = _isGpuFanLocked ? _gpuFanSlider.Value : (gpuChanged ? targetGpuSpeed : _lastCurveGpuSpeed);
                int sSpeed = _isSysFanLocked ? (_sysFanSlider?.Value ?? 50) : (sysChanged ? targetSysSpeed : _lastCurveSysSpeed);

                Task.Run(() =>
                {
                    _wmi.SetFanBehavior(0x03, applyCustomSpeeds: false);
                    if (!_isCpuFanLocked && cSpeed > 0 && cpuValid) _wmi.SetCpuFanSpeed((byte)cSpeed);
                    if (!_isGpuFanLocked && gSpeed > 0 && gpuValid) _wmi.SetGpuFanSpeed((byte)gSpeed);
                    if (!_isSysFanLocked && sSpeed > 0 && cpuValid) _wmi.SetSystemFanSpeed((byte)sSpeed);
                });
            }
        }

        internal static int InterpolateCurve(List<Point>? curve, int temp)
        {
            if (curve == null) return 50;
            Point[] pts;
            try
            {
                pts = curve.ToArray();
            }
            catch
            {
                return 50;
            }
            if (pts.Length == 0) return 50;

            bool isSorted = true;
            for (int i = 0; i < pts.Length - 1; i++)
            {
                if (pts[i].X > pts[i + 1].X)
                {
                    isSorted = false;
                    break;
                }
            }
            if (!isSorted)
            {
                Array.Sort(pts, (a, b) => a.X.CompareTo(b.X));
            }

            if (pts.Length == 1) return Math.Clamp(pts[0].Y, 10, 100);

            if (temp <= pts[0].X) return Math.Clamp(pts[0].Y, 10, 100);
            if (temp >= pts[^1].X) return Math.Clamp(pts[^1].Y, 10, 100);

            for (int i = 0; i < pts.Length - 1; i++)
            {
                if (temp >= pts[i].X && temp <= pts[i + 1].X)
                {
                    double span = (double)pts[i + 1].X - (double)pts[i].X;
                    if (span <= 0.0) return Math.Clamp(pts[i].Y, 10, 100);
                    double t = ((double)temp - (double)pts[i].X) / span;
                    double interp = pts[i].Y + t * ((double)pts[i + 1].Y - (double)pts[i].Y);
                    if (double.IsNaN(interp) || double.IsInfinity(interp)) return Math.Clamp(pts[i].Y, 10, 100);
                    int result = (int)Math.Clamp(Math.Round(interp), 10.0, 100.0);
                    return result;
                }
            }
            return Math.Clamp(pts[^1].Y, 10, 100);
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

                if (_gameSync?.ActiveGameExe != null)
                {
                    ApplyFanRules(pluggedIn);
                    return;
                }

                int acIdx = _cboAcProfile.SelectedIndex;
                if (acIdx > 0 && acIdx < AcProfileValues.Length)
                {
                    byte mode = AcProfileValues[acIdx];
                    ApplyPowerMode(mode, PowerByteToBtn(mode));
                }
                else
                {
                    // "Don't Change": Keep active profile if supported on AC; clamp Eco (0x06) to Balanced
                    if (_currentPowerMode == 0x06)
                    {
                        ApplyPowerMode(0x01, _btnBalanced);
                    }
                    else
                    {
                        HighlightBtn(PowerByteToBtn(_currentPowerMode), ref _activePowerBtn);
                        UpdatePowerModePill(_currentPowerMode);
                    }
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

                if (_gameSync?.ActiveGameExe != null)
                {
                    if (_currentPowerMode == 0x04 || _currentPowerMode == 0x05)
                    {
                        ApplyPowerMode(0x01, _btnBalanced);
                    }
                    ApplyFanRules(pluggedIn);
                    return;
                }

                int batIdx = _cboBatteryProfile.SelectedIndex;
                if (batIdx > 0 && batIdx < BatteryProfileValues.Length)
                {
                    byte mode = BatteryProfileValues[batIdx];
                    ApplyPowerMode(mode, PowerByteToBtn(mode));
                }
                else
                {
                    // "Don't Change": Keep active profile if supported on Battery; clamp Perf/Turbo (0x04, 0x05) to Balanced
                    if (_currentPowerMode == 0x04 || _currentPowerMode == 0x05)
                    {
                        ApplyPowerMode(0x01, _btnBalanced);
                    }
                    else
                    {
                        HighlightBtn(PowerByteToBtn(_currentPowerMode), ref _activePowerBtn);
                        UpdatePowerModePill(_currentPowerMode);
                    }
                }
            }

            ApplyFanRules(pluggedIn);
        }

        private void ApplyFanRules(bool pluggedIn)
        {
            if (_gameSync?.ActiveGameExe != null)
            {
                return;
            }
            var cbo = pluggedIn ? _cboAcFan : _cboBatteryFan;
            int idx = cbo.SelectedIndex;
            byte fanMode = 0x01;

            if (idx > 0 && idx < FanProfileValues.Length)
            {
                fanMode = FanProfileValues[idx];
            }
            else
            {
                fanMode = GetCurrentFanByte();
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
            UpdateTelemetryCore();
        }

        private void HideApp()
        {
            this.Hide();
            _allowVisible = false;
            TrimWorkingSet();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
            {
                TrimWorkingSet();
            }
            else if (WindowState == FormWindowState.Normal && Visible)
            {
                UpdateTelemetryCore();
            }
        }

        private void ToggleWindowVisibility()
        {
            if ((DateTime.UtcNow - _lastToggleWindowUtc).TotalMilliseconds < 350)
                return;
            _lastToggleWindowUtc = DateTime.UtcNow;

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
                    Application.DoEvents();
                    Thread.Sleep(25);
                    waited++;
                }

                _gameSync.GameDetected -= OnGameDetected;
                _gameSync.GameExited -= OnGameExited;
                _gameSync.Dispose();
                _wmi.Dispose();
                try { _pdhMonitor.Dispose(); } catch { }
                try { _keyboardHook?.Dispose(); } catch { }
                try { _wmiHotkeyWatcher?.Dispose(); } catch { }
                try { _rawInputWatcher?.Dispose(); } catch { }
                try { _pipeServer?.Dispose(); } catch { }
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
