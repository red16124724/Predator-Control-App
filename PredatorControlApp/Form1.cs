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
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_ACTIVATE = 1;

        public TitleBarButton()
        {
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            UseMnemonic = false;
        }

        protected override bool ShowFocusCues => false;

        public new void PerformClick()
        {
            OnClick(EventArgs.Empty);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_ACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }
    }



    [SupportedOSPlatform("windows")]

    public partial class Form1 : Form

    {
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00020000; // WS_MINIMIZEBOX for native taskbar click-to-minimize and Alt-Tab integration
                return cp;
            }
        }

        #region Win32 Interop — Memory Management

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        private static DateTime _lastTrimWorkingSetUtc = DateTime.MinValue;
        private static readonly TimeSpan TrimWorkingSetCooldown = TimeSpan.FromSeconds(30);

        public bool IsUiRenderActive => Visible && WindowState != FormWindowState.Minimized && !IsDisposed && !Disposing && !_isClosing && IsHandleCreated;

        private static void TrimWorkingSet(bool force = false)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                if (!force && (now - _lastTrimWorkingSetUtc) < TrimWorkingSetCooldown)
                    return;
                _lastTrimWorkingSetUtc = now;

                GC.Collect(1, GCCollectionMode.Optimized, false);
                using var proc = Process.GetCurrentProcess();
                SetProcessWorkingSetSize(proc.Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }



        private void SafeBeginInvoke(Action action)

        {

            try

            {

                if (IsDisposed || Disposing || _isClosing || _shutdownCts.IsCancellationRequested) return;

                if (IsHandleCreated)

                {

                    BeginInvoke(new Action(() =>

                    {

                        try

                        {

                            if (!IsDisposed && !Disposing && !_isClosing)

                            {

                                action();

                            }

                        }

                        catch (ObjectDisposedException) { }

                        catch (InvalidOperationException) { }

                        catch { }

                    }));

                }

                else

                {

                    if (Environment.CurrentManagedThreadId == _mainThreadId)

                    {

                        if (!IsDisposed && !Disposing && !_isClosing && !_shutdownCts.IsCancellationRequested)

                            action();

                    }

                    else
                    {
                        int executed = 0;
                        EventHandler? onHandle = null;
                        EventHandler? onDisposed = null;

                        onHandle = (s, e) =>
                        {
                            HandleCreated -= onHandle;
                            Disposed -= onDisposed;
                            if (Interlocked.Exchange(ref executed, 1) != 0) return;
                            try
                            {
                                if (!IsDisposed && !Disposing && !_isClosing)
                                {
                                    BeginInvoke(new Action(() =>
                                    {
                                        try
                                        {
                                            if (!IsDisposed && !Disposing && !_isClosing)
                                                action();
                                        }
                                        catch { }
                                    }));
                                }
                            }
                            catch { }
                        };

                        onDisposed = (s, e) =>
                        {
                            Disposed -= onDisposed;
                            HandleCreated -= onHandle;
                            Interlocked.Exchange(ref executed, 1);
                        };

                        HandleCreated += onHandle;
                        Disposed += onDisposed;

                        if (IsHandleCreated && Interlocked.Exchange(ref executed, 1) == 0)
                        {
                            HandleCreated -= onHandle;
                            Disposed -= onDisposed;
                            try
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    try
                                    {
                                        if (!IsDisposed && !Disposing && !_isClosing)
                                            action();
                                    }
                                    catch { }
                                }));
                            }
                            catch { }
                        }
                    }

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

        private static readonly uint WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

        #endregion

        #region Win32 Interop — Window Elevation & Foreground Lock Bypass

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fUnknown);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private static readonly IntPtr HWND_TOP = new IntPtr(0);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int WM_CANCELMODE = 0x001F;

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        private const byte VK_MENU = 0x12; // ALT key
        private const uint KEYEVENTF_KEYUP = 0x0002;



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
        private bool _suppressUiEvents;

        private readonly int _mainThreadId;

        private readonly CancellationTokenSource _shutdownCts = new();

        private int _isHardwareScanRunning;

        private long _powerLimitApplySequence;

        private Action<OperatingMode, int, int, bool>? _powerLimitsChangedHandler;

        private int _maxHz = 144;

        private float _dpiScale = 1f; 

        private int _formW;           



        private static Color FormBg => ThemeManager.FormBg;

        private static Color SeparatorColor => ThemeManager.Separator;

        private static Color HeaderColor => ThemeManager.HeaderText;

        private static Color SubHeaderColor => ThemeManager.TextSecondary;

        private static Color AccentColor => ThemeManager.Accent;



        private Font _fontTitle = new("Segoe UI", 9.5f, FontStyle.Bold);
        private Font _fontSectionHeader = new("Segoe UI", 8.5f, FontStyle.Bold);
        private Font _fontHeaderLight = new("Segoe UI", 8.5f, FontStyle.Regular);
        private Font _fontBody = new("Segoe UI", 9f, FontStyle.Regular);
        private Font _fontBodyBold = new("Segoe UI", 9f, FontStyle.Bold);
        private Font _fontCalibrationPercent = new("Segoe UI", 18f, FontStyle.Bold);
        private Font _fontRiskBadge = new("Segoe UI", 7.5f, FontStyle.Bold);

        private Font FontTitle => _fontTitle;
        private Font FontSectionHeader => _fontSectionHeader;
        private Font FontHeaderLight => _fontHeaderLight;
        private Font FontBody => _fontBody;
        private Font FontBodyBold => _fontBodyBold;
        private Font FontCalibrationPercent => _fontCalibrationPercent;
        private Font FontRiskBadge => _fontRiskBadge;

        private void UpdateDpiFonts()
        {
            var oldTitle = _fontTitle;
            var oldSec = _fontSectionHeader;
            var oldLight = _fontHeaderLight;
            var oldBody = _fontBody;
            var oldBold = _fontBodyBold;
            var oldCalib = _fontCalibrationPercent;
            var oldRisk = _fontRiskBadge;

            _fontTitle = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _fontSectionHeader = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _fontHeaderLight = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            _fontBody = new Font("Segoe UI", 9f, FontStyle.Regular);
            _fontBodyBold = new Font("Segoe UI", 9f, FontStyle.Bold);
            _fontCalibrationPercent = new Font("Segoe UI", 18f, FontStyle.Bold);
            _fontRiskBadge = new Font("Segoe UI", 7.5f, FontStyle.Bold);

            try { oldTitle?.Dispose(); } catch { }
            try { oldSec?.Dispose(); } catch { }
            try { oldLight?.Dispose(); } catch { }
            try { oldBody?.Dispose(); } catch { }
            try { oldBold?.Dispose(); } catch { }
            try { oldCalib?.Dispose(); } catch { }
            try { oldRisk?.Dispose(); } catch { }
        }

        private bool _isUpdatingOverdrive;
        private bool _isUpdatingModeKeyAction;



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



        // Keyboard lighting: 4-zone is always available; per-key is gated on a

        // BIOS/SMBIOS identity match plus a live Acer Lighting Service probe.

        private KeyboardLightingController? _lightingController;

        private KeyboardLightingCapabilities? _lightingCapabilities;

        private KeyboardRgbMap? _perKeyMap;

        private Label? _lblLightingCapability;

        private Label? _lblLightingStatus;

        private PredatorDropDown? _cboLightingTier;

        private PredatorButton? _btnZonesApply;

        private PredatorButton? _btnPerKeySend;

        private readonly PredatorButton?[] _zoneButtons = new PredatorButton?[4];

        private readonly List<PredatorButton> _perKeySwatches = new();

        private Color _activePaintColor = Color.FromArgb(0, 150, 255);

        private bool _lightingProbeRunning;

        private bool _suppressLightingTierChange;

        private int _lightingPersistPending;

        private int _selectedZoneIndex = 0;

        private int _zoneLayoutMode = 4; // 1 = 1-Zone, 3 = 3-Zone, 4 = 4-Zone, 0 = Per-Key

        private int _detectedBiosLayoutMode = 4;

        private bool _syncAllZones;

        private PredatorButton? _btnPresetCyberpunk;
        private PredatorButton? _btnPresetFirestorm;
        private PredatorButton? _btnPresetIce;
        private PredatorButton? _btnPresetOff;
        private PredatorButton? _btnPresetCustom;

        private readonly PredatorButton?[] _layoutPillButtons = new PredatorButton?[4];

        private readonly List<PredatorButton> _zonePaletteButtons = new();

        private readonly List<PredatorButton> _dynamicTintButtons = new();
        private readonly object _brightnessApplyLock = new();
        private int _brightnessApplyGeneration;

        private Panel? _pnlSectorVisualizer;

        private Label? _lblPerKeyPrompt;

        private Label? _lblBiosDetectedBadge;

        private PredatorButton? _btnSyncAll;



        private static readonly (string Name, Color Color)[] CuratedZonePalette = new[]

        {

            ("Predator Cyan", Color.FromArgb(0x00, 0xE5, 0xFF)),

            ("Electric Blue", Color.FromArgb(0x29, 0x79, 0xFF)),

            ("Neon Purple", Color.FromArgb(0x7C, 0x4D, 0xFF)),

            ("Hot Pink", Color.FromArgb(0xFF, 0x40, 0x81)),

            ("Crimson Red", Color.FromArgb(0xFF, 0x17, 0x44)),

            ("Toxic Green", Color.FromArgb(0x00, 0xE6, 0x76)),

            ("Electric Orange", Color.FromArgb(0xFF, 0x91, 0x00)),

            ("Amber Gold", Color.FromArgb(0xFF, 0xEA, 0x00)),

            ("Pure White", Color.FromArgb(0xFF, 0xFF, 0xFF)),

            ("Dark / Off", Color.FromArgb(0x00, 0x00, 0x00))

        };





        // Power mode switch smoothing and the optimized thermal safety layer.

        private readonly FanCommandQueue _fanCommandQueue = new();

        private PowerModeSwitchGuard? _powerModeSwitchGuard;

        private ThermalSafetyCoordinator? _thermalSafety;

        private ThermalDecision _lastThermalDecision = ThermalDecision.Idle;

        private Label? _lblThermalStage;

        private Label? _lblThermalDetail;

        private Label? _lblThermalCeilingHdr;

        private Label? _lblThermalHysteresisHdr;

        private PredatorSwitch? _switchThermalGuard;

        private PredatorSlider? _sliderThermalCeiling;

        private PredatorSlider? _sliderThermalHysteresis;

        private DateTime _telemetryPauseUntilUtc = DateTime.MinValue;

        private bool _suppressThermalUi;



        private PredatorSlider _brightnessSlider = null!, _speedSlider = null!;

        private PredatorSlider _cpuFanSlider = null!, _gpuFanSlider = null!;

        private PredatorSlider? _sysFanSlider;

        private bool _isUpdatingFanUi;



        private bool _isInitializing = true;
        private bool _isInitializingLightingUi = false;
        private UpdateInfo? _pendingUpdateInfo;
        private int _startupUpdateCheckTriggered;
        
        private PredatorButton? _btnDisp60, _btnDispMax;

        private PredatorSwitch? _swOdSys;

        private Label? _lblOdStatusSys;

        private FanCurveGraph? _embeddedCurveCpu;

        private FanCurveGraph? _embeddedCurveGpu;

        private PredatorButton? _btnEmbeddedApplyCurve;

        private PredatorButton? _btnEmbeddedResetCurve;

        private Label? _lblEmbeddedHysteresis;

        private PredatorSlider? _sliderEmbeddedHysteresis;

        internal PredatorSlider? EmbeddedHysteresisSlider => _sliderEmbeddedHysteresis;

        internal Label? EmbeddedHysteresisLabel => _lblEmbeddedHysteresis;

        internal PredatorButton DashboardBtn60Hz => _btn60Hz;

        internal PredatorButton DashboardBtnMaxHz => _btnMaxHz;

        internal PredatorButton? SystemBtnDisp60 => _btnDisp60;

        internal PredatorButton? SystemBtnDispMax => _btnDispMax;

        internal ToolStripMenuItem? TrayDisplay60 => _trayDisplay60;

        internal ToolStripMenuItem? TrayDisplayMax => _trayDisplayMax;

        internal PredatorSwitch SwitchLcdOverdrive => _switchLcdOverdrive;

        internal PredatorSwitch? SwOdSys => _swOdSys;

        internal ToolStripMenuItem? TrayLcdOverdrive => _trayLcdOverdrive;

        internal Label LblLcdOverdriveStatus => _lblLcdOverdriveStatus;

        internal Label? LblOdStatusSys => _lblOdStatusSys;



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

        internal byte CurrentPowerMode => _currentPowerMode;

        internal void SetCurrentPowerModeForTesting(byte mode) => _currentPowerMode = mode;

        private byte _pendingTargetPowerMode = 0x01;

        internal byte PendingTargetPowerMode => _pendingTargetPowerMode;

        private DateTime _powerModeTransitionLockUntilUtc = DateTime.MinValue;

        internal DateTime PowerModeTransitionLockUntilUtc => _powerModeTransitionLockUntilUtc;

        internal const int PowerModeTransitionLockMs = 2500;

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

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]

        internal DeviceCapabilities Capabilities

        {

            get => _capabilities;

            set

            {

                _capabilities = value;

                if (value.ScannedMinPowerWatts > 0) _appSettings.CpuMinAllowedPowerWatts = value.ScannedMinPowerWatts;

                if (value.ScannedMaxPl1Watts > 0) _appSettings.CpuMaxAllowedPl1Watts = value.ScannedMaxPl1Watts;

                if (value.ScannedMaxPl2Watts > 0) _appSettings.CpuMaxAllowedPl2Watts = value.ScannedMaxPl2Watts;

                if (value.CpuTdpWatts > 0) _appSettings.CpuThermalSpecTdpWatts = value.CpuTdpWatts;

            }

        }

        private readonly PdhLoadMonitor _pdhMonitor = new();

        private RawInputKeyWatcher? _rawInputWatcher;

        private SecureNamedPipeIpc.PipeServer? _pipeServer;

        private readonly CurveFollower _cpuCurveFollower = new();

        private readonly CurveFollower _gpuCurveFollower = new();

        private readonly CurveFollower _sysCurveFollower = new();

        private int _cpuSensorMisses;

        private readonly object _dampingLock = new();

        private readonly Queue<int> _cpuDampingQueue = new();

        private Action? _formThemeHandler;

        private readonly Queue<int> _gpuDampingQueue = new();

        private DateTime _lastModeKeyUtc = DateTime.MinValue;

        private DateTime _lastToggleWindowUtc = DateTime.MinValue;



        private Label? _lblSysFanRpm;

        private Label? _lblFanLockStatus;

        private Label? _lblGpuSleepBadge;

        private Label? _lblHardwareLinkTitle;
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

        private Panel? _pnlCalibrationOverlay;

        private Label? _lblCalibrationTitle;

        private Label? _lblCalibrationAwakeLock;

        private Panel? _pnlCalibrationWarning;

        private Label? _lblCalibrationWarning;

        private Label? _lblCalibrationPhase;

        private Label? _lblCalibrationBatteryPercent;

        private Label? _lblCalibrationPowerRate;

        private ProgressBar? _pbCalibration;

        private Label? _lblCalibrationEta;

        private Label? _lblCalibrationLastCalibrated;

        private PredatorButton? _btnCancelCalibration;

        private ToolTip? _tipCalibration;

        private Label? _lblLastCalibratedMain;

        private readonly BatteryCalibrationEtaCalculator _calibrationEtaCalc = new();

        private DateTime _calibrationPhaseStartTime;

        private int _consecutiveInvalidBatteryReadings = 0;

        private PredatorDropDown? _cboTheme;

        private PredatorButton? _btnDiagnostics;

        private Label? _lblGpuStatusSys;



        private Panel _pnlTitle = null!;

        private Panel _sidebarPanel = null!;

        private Panel _pageContainer = null!;

        private DarkScrollPanel _pnlDashboard = null!;
        private DarkScrollPanel _pnlFans = null!;
        private DarkScrollPanel _pnlLighting = null!;
        private DarkScrollPanel _pnlDisplay = null!;
        private DarkScrollPanel _pnlGameSync = null!;
        private DarkScrollPanel _pnlSystem = null!;
        private DarkScrollPanel _pnlSettings = null!;

        // Navigation and panel aliases
        private DarkScrollPanel _pnlDash => _pnlDashboard;
        private DarkScrollPanel _pnlRgb => _pnlLighting;

        private PredatorButton _btnNavDashboard = null!;
        private PredatorButton _btnNavFans = null!;
        private PredatorButton _btnNavLighting = null!;
        private PredatorButton _btnNavDisplay = null!;
        private PredatorButton _btnNavGameSync = null!;
        private PredatorButton _btnNavSystem = null!;
        private PredatorButton _btnNavSettings = null!;

        // Button aliases
        private PredatorButton _btnNavDash => _btnNavDashboard;
        private PredatorButton _btnNavRgb => _btnNavLighting;

        private ListView? _lvGameProfiles;
        private PredatorButton? _btnGameSyncAdd, _btnGameSyncEdit, _btnGameSyncDelete;

        private PredatorButton? _activeNavBtn;



        private PredatorButton _btnThemeToggle = null!;

        private TitleBarButton? _btnCaptionClose, _btnCaptionMax, _btnCaptionMin;

        private bool _isMaximized16x9;

        private Rectangle _normalBounds;

        private readonly AppSettings _appSettings = AppSettings.Load();

        internal AppSettings Settings => _appSettings;

        private readonly object _saveLock = new();

        private int _modeKeyAction = 0; // 0 = Cycle, 1 = TurboToggle

        private byte _turboReturnMode = 0x01;

        private PredatorDropDown _cboModeKeyAction = null!;

        private PredatorDropDown _cboModeKeyActionSys = null!;

        private PredatorButton _btnTriggerModeKey = null!;

        private Label _lblModePill = null!;

        private Label _lblPowerPill = null!;

        #region Custom Mode Shortcuts (Embedded Settings UI)

        private sealed class ShortcutRowControls
        {
            public CustomModeShortcut Shortcut { get; }
            public Label LblBadge { get; }
            public Label LblMode { get; }
            public TextBox TxtShortcut { get; }
            public PredatorButton BtnRecord { get; }
            public PredatorButton BtnClear { get; }
            public bool IsRecording { get; set; }

            public ShortcutRowControls(CustomModeShortcut shortcut, Label lblBadge, Label lblMode, TextBox txtShortcut, PredatorButton btnRecord, PredatorButton btnClear)
            {
                Shortcut = shortcut;
                LblBadge = lblBadge;
                LblMode = lblMode;
                TxtShortcut = txtShortcut;
                BtnRecord = btnRecord;
                BtnClear = btnClear;
            }
        }

        private readonly List<ShortcutRowControls> _shortcutRows = new();
        private Label? _lblShortcutsStatus;
        private static readonly Font FontShortcutDot = new("Segoe UI", 11f, FontStyle.Bold);

        #endregion



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

                                  _trayRgbMeteor = null!;



        #region CPU Power Limits (PL1 / PL2) Controls

        private Panel? _cardPowerLimits;

        private Label? _lblPowerLimitModeContext;

        private Label? _lblPowerBoundsSummary;

        private Label? _lblPl1Value;

        private Label? _lblPl2Value;

        private PredatorSlider? _sliderPl1;

        private PredatorSlider? _sliderPl2;

        private PredatorButton? _btnResetPowerLimits;

        private PredatorButton? _btnScanPawnIo;

        private Label? _lblPowerLimitStatus;

        private bool _isUpdatingPowerLimitsUi;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]

        internal PredatorSlider? SliderPl1 => _sliderPl1;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]

        internal PredatorSlider? SliderPl2 => _sliderPl2;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]

        internal PredatorButton? BtnResetPowerLimits => _btnResetPowerLimits;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]

        internal PredatorButton? BtnScanPawnIo => _btnScanPawnIo;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]

        internal Label? LblPowerLimitStatus => _lblPowerLimitStatus;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]

        internal Label? LblPl1Value => _lblPl1Value;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]

        internal Label? LblPl2Value => _lblPl2Value;

        #endregion



        #endregion



        #region DPI Scaling Helper



        private int S(int px) => px == 0 ? 0 : (px > 0 ? Math.Max(1, (int)Math.Round(px * _dpiScale)) : Math.Min(-1, (int)Math.Round(px * _dpiScale)));



        #endregion



        #region Constructor & Visibility



        private bool _startupLightingRestored;

        protected override void SetVisibleCore(bool value)

        {

            if (!_allowVisible)

            {

                value = false;

                if (!IsHandleCreated) CreateHandle();

                if (!_startupLightingRestored)

                {

                    _startupLightingRestored = true;

                    _ = RestoreSavedKeyboardLightingWithBootRetryAsync(_shutdownCts.Token);

                }

            }

            base.SetVisibleCore(value);

        }



        public Form1()
        {
            _mainThreadId = Environment.CurrentManagedThreadId;
            try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }

            _isInitializing = true;
            _backlightMgr.SavedAcBrightness = _appSettings.KeyboardBrightnessLevel;

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

                try

                {

                    CpuPowerLimitController.Instance = new CpuPowerLimitController(_wmi, _appSettings);

                    CpuPowerLimitController.Instance.InitializeHardwareFingerprintAndBaseline();

                    _powerLimitsChangedHandler = (mode, pl1, pl2, isCustom) =>

                    {

                        SafeBeginInvoke(() =>

                        {

                            if (_isClosing || IsDisposed) return;

                            if (ByteToOperatingMode(_currentPowerMode) == mode)

                            {

                                _isUpdatingPowerLimitsUi = true;

                                try

                                {

                                    if (_sliderPl1 != null && _sliderPl2 != null)

                                    {

                                        _sliderPl1.Minimum = 0;

                                        _sliderPl1.Maximum = 500;

                                        _sliderPl2.Minimum = 0;

                                        _sliderPl2.Maximum = 500;



                                        int minP = _appSettings.CpuMinAllowedPowerWatts > 0 ? _appSettings.CpuMinAllowedPowerWatts : 15;

                                        int maxP = _appSettings.CpuMaxAllowedPl2Watts > 0 ? _appSettings.CpuMaxAllowedPl2Watts : 140;

                                        _sliderPl1.Minimum = minP;

                                        _sliderPl1.Maximum = maxP;

                                        _sliderPl2.Minimum = minP;

                                        _sliderPl2.Maximum = maxP;



                                        if (pl2 >= _sliderPl1.Value)

                                        {

                                            _sliderPl2.Value = pl2;

                                            _sliderPl1.Value = pl1;

                                        }

                                        else

                                        {

                                            _sliderPl1.Value = pl1;

                                            _sliderPl2.Value = pl2;

                                        }

                                    }

                                }

                                finally

                                {

                                    _isUpdatingPowerLimitsUi = false;

                                }

                                UpdatePowerLimitLabels();

                                UpdatePowerLimitStatusUi();

                            }

                        });

                    };

                    CpuPowerLimitController.Instance.PowerLimitsChanged += _powerLimitsChangedHandler;

                }

                catch { }

                _capabilities = CapabilityProbe.Probe(_wmi, _wmi.EcHid, CpuPowerLimitController.Instance?.PawnIo);

                SyncCapabilitiesWithPowerLimits();

                ThemeManager.LoadThemeFromRegistry();

                _cpuCurveFollower.HysteresisDegrees = _appSettings.FanHysteresisDegrees;

                _gpuCurveFollower.HysteresisDegrees = _appSettings.FanHysteresisDegrees;

                _sysCurveFollower.HysteresisDegrees = _appSettings.FanHysteresisDegrees;



            _dpiScale = (this.DeviceDpi > 0) ? this.DeviceDpi / 96f : 1.0f;

            try { int detectedHz = GetMaxRefreshRateCore(); if (detectedHz > 60) _maxHz = detectedHz; } catch { }



            InitializeKeyboardLighting();

            InitializeThermalSafety();

            InitializePowerModeSwitchGuard();



            BuildUI();

            ThemeManager.ApplyTheme(this);

                            foreach (var row in _shortcutRows)
                            {
                                if (row.TxtShortcut != null && !row.TxtShortcut.IsDisposed)
                                {
                                    row.TxtShortcut.BackColor = ThemeManager.WindowBg;
                                    row.TxtShortcut.ForeColor = row.Shortcut.Enabled ? ThemeManager.TextPrimary : ThemeManager.TextSecondary;
                                }
                                if (row.LblMode != null && !row.LblMode.IsDisposed)
                                {
                                    row.LblMode.ForeColor = ThemeManager.TextPrimary;
                                }
                            }

            UpdatePowerModePill(_currentPowerMode);

            RefreshLightingUi();

            RefreshThermalUi();

            BeginKeyboardCapabilityDetection();

            _formThemeHandler = () =>

            {

                SafeBeginInvoke(() =>

                {
                    foreach (var row in _shortcutRows)
                    {
                        if (row.TxtShortcut != null && !row.TxtShortcut.IsDisposed)
                        {
                            row.TxtShortcut.BackColor = ThemeManager.WindowBg;
                            row.TxtShortcut.ForeColor = row.Shortcut.Enabled ? ThemeManager.TextPrimary : ThemeManager.TextSecondary;
                        }
                        if (row.LblMode != null && !row.LblMode.IsDisposed)
                            row.LblMode.ForeColor = ThemeManager.TextPrimary;
                    }

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

                        var trimmed = msg?.Trim();

                        if (string.Equals(trimmed, "SHOW", StringComparison.OrdinalIgnoreCase) ||

                            string.Equals(trimmed, "SHOW_WINDOW", StringComparison.OrdinalIgnoreCase))

                        {

                            ShowApp();

                        }

                        else if (string.Equals(trimmed, "TOGGLE", StringComparison.OrdinalIgnoreCase))

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



            UpdateDisplayRefreshRateButtons(GetCurrentRefreshRate());



            LoadMemory();

            RecoverInterruptedCalibrationOnStartup();



            try

            {

                OperatingMode startupMode = ByteToOperatingMode(_currentPowerMode);

                CpuPowerLimitController.Instance?.ApplyPowerLimitsForMode(startupMode, forceHardwareApply: true);

            }

            catch { }



            if (_gameSync == null)
            {
                _gameSync = new GameSyncController();
                _gameSync.GameDetected += OnGameDetected;
                _gameSync.GameExited += OnGameExited;
            }



            if (_gameSync.IsEnabled)

            {

                if (_switchGameSync != null) _switchGameSync.Checked = true;

                if (_lblGameSyncStatus != null) _lblGameSyncStatus.Text = "Active \u2014 Monitoring";
            UpdateGameSyncStatusLabel();
            PopulateGameSyncProfilesList();

            }



            var powerInit = SystemInformation.PowerStatus;

            _timer.Interval = _backlightMgr.IsOnBattery(powerInit.PowerLineStatus, powerInit.BatteryChargeStatus) ? 5000 : 2000;

            _timer.Tick += UpdateTelemetry;

            _timer.Start();



            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            SystemEvents.SessionSwitch += OnSessionSwitch;

            SystemEvents.SessionEnding += OnSessionEnding;

            this.FormClosed += (s, e) =>

            {

                SystemEvents.PowerModeChanged -= OnPowerModeChanged;

                try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }

                try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { }

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
                            try
                            {
                                _switchStartWithWindows.Checked = enabled;
                            }
                            finally
                            {
                                _suppressStartupToggle = false;
                            }

                        });

                    }

                    catch (Exception ex)

                    {

                        Program.Report(ex, false);

                    }



                    try

                    {

                        var probed = CapabilityProbe.Probe(_wmi, _wmi.EcHid, CpuPowerLimitController.Instance?.PawnIo);

                        SafeBeginInvoke(() =>

                        {

                            _capabilities = probed;

                            SyncCapabilitiesWithPowerLimits();

                            if (_lblProtocolBadge != null)

                            {

                                _lblProtocolBadge.Text = _wmi.EcHid != null && _wmi.EcHid.IsOpen ? "Direct Hardware Controller" : "Acer System Driver";

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

                    },

                    onCustomModeShortcutTriggered: (modeCode) =>

                    {

                        SafeBeginInvoke(() => ApplyPowerModeByCode(modeCode));

                    });

                _keyboardHook.SetCustomShortcuts(_appSettings.CustomModeShortcuts);

            }

            catch { }



            try

            {

                _wmiHotkeyWatcher = new WmiHotkeyWatcher((detail) =>

                {

                    // Standard Fn combinations (such as Fn+F5 for display switch or Fn+F7 for touchpad)

                    // are never hijacked for window toggling. Window visibility is toggled exclusively

                    // via dedicated PredatorSense / NitroSense key events.

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

    private void TriggerStartupUpdateCheckAsync()
    {
        if (Interlocked.Exchange(ref _startupUpdateCheckTriggered, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Cold-boot Wi-Fi/DHCP association delay
                await Task.Delay(TimeSpan.FromSeconds(8), _shutdownCts.Token).ConfigureAwait(false);
                if (_shutdownCts.IsCancellationRequested || _isClosing || IsDisposed) return;

                SafeBeginInvoke(async () =>
                {
                    try
                    {
                        if (_isClosing || IsDisposed) return;
                        await CheckForUpdatesAsync(userInitiated: false);
                    }
                    catch { }
                });
            }
            catch (OperationCanceledException) { }
            catch { }
        }, _shutdownCts.Token);
    }

    internal void ApplySavedKeyboardLightingFromSettings()
    {
        if (_isClosing || IsDisposed) return;
        var settings = _appSettings;
        if (settings == null) return;

        var power = SystemInformation.PowerStatus;
        bool onBattery = _backlightMgr.IsOnBattery(power.PowerLineStatus, power.BatteryChargeStatus);
        if (onBattery && !_manualBacklightOnBattery)
        {
            EnforceBacklightOffOnBattery();
            return;
        }

        string dynamicEffect = settings.DynamicLightingEffectName;
        if (string.Equals(dynamicEffect, "Off", StringComparison.OrdinalIgnoreCase) || settings.IsZoneLightingActive)
        {
            ApplyStaticZoneColorsToHardware();
        }
        else
        {
            int mode = 0;
            for (int i = 1; i < RgbModeNames.Length; i++)
            {
                if (string.Equals(RgbModeNames[i], dynamicEffect, StringComparison.OrdinalIgnoreCase))
                {
                    mode = i;
                    break;
                }
            }
            if (mode > 0)
            {
                ApplyRgbModeFromDropdown(mode);
            }
            else
            {
                ApplyStaticZoneColorsToHardware();
            }
        }
    }



        #region Updates



        private async Task CheckForUpdatesAsync(bool userInitiated = true)

        {

            if (_updateCheckRunning) return;

            _updateCheckRunning = true;

            if (_btnCheckUpdates != null && !_btnCheckUpdates.IsDisposed)

            {

                _btnCheckUpdates.Enabled = false;

                if (userInitiated)

                {

                    _btnCheckUpdates.Text = "Checking updates…";

                }

            }



            try

            {

                var info = await Updater.CheckAsync();

                if (_isClosing || IsDisposed) return;



                if (info == null)

                {

                    if (userInitiated)

                    {

                        MessageBox.Show(this, $"You're on the latest version (v{Updater.CurrentText}).",

                            "Predator Control", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    }

                    return;

                }



                _pendingUpdateInfo = info;

                if (_btnCheckUpdates != null && !_btnCheckUpdates.IsDisposed)
                {
                    _btnCheckUpdates.Text = $"✨ Update Available: v{info.Version.ToString(3)}";
                }

                if (userInitiated)
                {
                    await PromptUpdateAsync(info);
                }
                else
                {
                    try
                    {
                        if (_trayIcon.Visible)
                        {
                            _trayIcon.ShowBalloonTip(
                                6000,
                                "Predator Control Update Available",
                                $"Version v{info.Version.ToString(3)} is available. Click here or open the app to update.",
                                ToolTipIcon.Info);
                        }
                    }
                    catch { }
                }

            }

            catch (Exception ex)

            {

                if (_isClosing || IsDisposed) return;

                if (userInitiated)

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

            }

            finally

            {

                _updateCheckRunning = false;

                if (_btnCheckUpdates != null && !_btnCheckUpdates.IsDisposed)

                {

                    _btnCheckUpdates.Enabled = true;

                    if (!_btnCheckUpdates.Text.StartsWith("✨"))

                    {

                        _btnCheckUpdates.Text = $"⬇  Check for Updates  (v{Updater.CurrentText})";

                    }

                }

            }

        }



        private async Task PromptUpdateAsync(UpdateInfo info)

        {

            bool accepted = Updater.ShowNotes(this, "Update available",

                $"Version {info.Version.ToString(3)} is available — you have v{Updater.CurrentText}",

                info.Notes, confirm: true);



            if (!accepted) return;

            if (MessageBox.Show(this,
                "This installs the upstream release, which may replace changes in this customized build. " +
                "The current executable will be saved beside the app as a .previous.exe backup. Continue?",
                "Confirm upstream update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

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

            var lockObj = _powerNotifyLock;

            if (lockObj == null) return;



            try

            {

                lock (lockObj)

                {

                    if (_powerSettingRegistrationLid != IntPtr.Zero)

                    {

                        try { UnregisterPowerSettingNotification(_powerSettingRegistrationLid); } catch { }

                        _powerSettingRegistrationLid = IntPtr.Zero;

                    }

                    if (_powerSettingRegistrationPower != IntPtr.Zero)

                    {

                        try { UnregisterPowerSettingNotification(_powerSettingRegistrationPower); } catch { }

                        _powerSettingRegistrationPower = IntPtr.Zero;

                    }

                    if (_powerSettingRegistrationBattery != IntPtr.Zero)

                    {

                        try { UnregisterPowerSettingNotification(_powerSettingRegistrationBattery); } catch { }

                        _powerSettingRegistrationBattery = IntPtr.Zero;

                    }

                    if (_powerSettingRegistrationDisplay != IntPtr.Zero)

                    {

                        try { UnregisterPowerSettingNotification(_powerSettingRegistrationDisplay); } catch { }

                        _powerSettingRegistrationDisplay = IntPtr.Zero;

                    }

                }

            }

            catch { }

        }



        protected override void OnHandleCreated(EventArgs e)

        {

            base.OnHandleCreated(e);

            RegisterPowerNotifications();

            try

            {

                ChangeWindowMessageFilterEx(this.Handle, WM_SHOWME, MSGFLT_ALLOW, IntPtr.Zero);

                if (WM_TASKBARCREATED != 0)

                {

                    ChangeWindowMessageFilterEx(this.Handle, WM_TASKBARCREATED, MSGFLT_ALLOW, IntPtr.Zero);

                }

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

            else if (WM_TASKBARCREATED != 0 && m.Msg == (int)WM_TASKBARCREATED)

            {

                try

                {

                    if (_trayIcon != null)

                    {

                        _trayIcon.Visible = false;

                        _trayIcon.Visible = true;

                    }

                }

                catch { }

            }



            base.WndProc(ref m);

        }



        private bool IsOnBattery()

        {

            var power = SystemInformation.PowerStatus;

            bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;

            if (isCharging || power.PowerLineStatus == PowerLineStatus.Online) return false;

            if (power.PowerLineStatus == PowerLineStatus.Offline) return true;

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

                        bool isAc = lineStatus == PowerLineStatus.Online || isCharging || (lineStatus != PowerLineStatus.Offline && _isPluggedIn == true);

                        bool onBattery = !isAc;

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

                        _wmi.SuppressGpuQueries = onBattery;
                        GpuPowerMonitor.SetPowerState(onBattery);

                        if (isAc)

                        {

                            GpuPowerMonitor.ResetDebounce();

                        }

                        UpdatePowerStatusUI();

                        UpdateTelemetryCore();



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
                            Task.Run(() => ApplyCurrentLightingStateAsync(selectedRgb, targetBright));

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

                    bool isResumeAc = resumeLine == PowerLineStatus.Online || isResumeCharging || (resumeLine != PowerLineStatus.Offline && _isPluggedIn == true);

                    bool resumeOnBattery = !isResumeAc;

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

                        _pendingPluggedIn = isAc;

                        _powerLineStableTicks = 2;

                        _backlightMgr.SetPluggedInState(isAc);

                        _wmi.SuppressGpuQueries = !isAc;
                        GpuPowerMonitor.SetPowerState(!isAc);

                        if (isAc)

                        {

                            GpuPowerMonitor.ResetDebounce();

                        }

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
                            Task.Run(() => ApplyCurrentLightingStateAsync(selectedRgbResume, targetBright));

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

                    Task.Run(() => { try { _wmi.TurnOffBacklight(); } catch { } });

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

                    int selectedRgb = _rgbDropDown?.SelectedIndex ?? -1;
                    Task.Run(() => ApplyCurrentLightingStateAsync(selectedRgb, targetBright));

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

                    int selectedRgb = _rgbDropDown?.SelectedIndex ?? -1;
                    Task.Run(() => ApplyCurrentLightingStateAsync(selectedRgb, targetBright));

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

                if (_backlightEnforceCts != null) { try { _backlightEnforceCts.Cancel(); } catch { } try { _backlightEnforceCts.Dispose(); } catch { } }

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

                if (_rgbRestoreCts != null) { try { _rgbRestoreCts.Cancel(); } catch { } try { _rgbRestoreCts.Dispose(); } catch { } }

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

                Task.Run(() => { try { _wmi.TurnOffBacklight(); } catch { } });

                EnforceBacklightOffOnBattery();

            }

            else

            {

                int targetBright = onBattery

                    ? (_backlightMgr.ManualBatteryBrightness >= 0 ? _backlightMgr.ManualBatteryBrightness : (_brightnessSlider?.Value ?? 50))

                    : (_backlightMgr.SavedAcBrightness >= 0 ? _backlightMgr.SavedAcBrightness : (_brightnessSlider?.Value ?? 100));



                if (targetBright < 0 && !onBattery) targetBright = 100;



                if (_brightnessSlider != null && _brightnessSlider.Value != targetBright)

                {

                    _brightnessSlider.Value = targetBright;

                    if (_lblBrightHdr != null)

                        _lblBrightHdr.Text = $"BRIGHTNESS: {targetBright}%";

                }



                if (_appSettings.IsZoneLightingActive || string.Equals(_appSettings.DynamicLightingEffectName, "Off", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyStaticZoneColorsToHardware();
                }
                else
                {
                    int selectedMode = _rgbDropDown != null && _rgbDropDown.SelectedIndex > 0
                        ? _rgbDropDown.SelectedIndex
                        : (_wmi.LastRgbMode > 0 ? _wmi.LastRgbMode : 3);
                    selectedMode = Math.Clamp(selectedMode, 1, LightingEffectsManager.EffectNames.Length - 1);
                    byte speed = GetMappedSpeed();
                    Color color = Color.FromArgb(_appSettings.DynamicEffectPrimaryColorArgb);
                    if (color.IsEmpty || (color.R == 0 && color.G == 0 && color.B == 0))
                    {
                        color = (_colorPicker != null && _colorPicker.Color != Color.Empty && _colorPicker.Color != Color.Black && _colorPicker.Color.A != 0)
                            ? _colorPicker.Color
                            : Color.FromArgb(0, 150, 255);
                    }
                    byte direction = (byte)Math.Clamp(_appSettings.DynamicEffectDirection, 0, 1);

                    Task.Run(() =>
                    {
                        try
                        {
                            LightingEffectsManager.ApplyEffect(selectedMode, _wmi, color.R, color.G, color.B, (byte)targetBright, speed, direction, (OperatingMode)_currentPowerMode);
                        }
                        catch { }
                    });
                }

            }

        }



        #endregion



        #endregion



        #region Display Control

 

        public static string? GetDisplayDeviceName(int displayIndex)
        {
            try
            {
                var screens = Screen.AllScreens;
                if (displayIndex >= 0 && displayIndex < screens.Length)
                    return screens[displayIndex].DeviceName;
            }
            catch { }
            return null;
        }

        public static int GetCurrentRefreshRateCore(int displayIndex = 0)
        {
            if (displayIndex < 0) return 60;
            string? devName = GetDisplayDeviceName(displayIndex);
            if (displayIndex > 0 && devName == null) return 60;
            DEVMODE dm = new(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return EnumDisplaySettings(devName, ENUM_CURRENT_SETTINGS, ref dm) ? (dm.dmDisplayFrequency > 0 ? dm.dmDisplayFrequency : 60) : 60;
        }

        public static int GetMaxRefreshRateCore(int displayIndex = 0)
        {
            if (displayIndex < 0) return 60;
            string? devName = GetDisplayDeviceName(displayIndex);
            if (displayIndex > 0 && devName == null) return 60;
            DEVMODE cur = new(); cur.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(devName, ENUM_CURRENT_SETTINGS, ref cur)) return 60;

            int maxHz = cur.dmDisplayFrequency > 0 ? cur.dmDisplayFrequency : 60;
            DEVMODE dm = new(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            for (int modeNum = 0; EnumDisplaySettings(devName, modeNum, ref dm); modeNum++)
            {
                if (IsSameGeometry(dm, cur) && dm.dmDisplayFrequency > maxHz)
                    maxHz = dm.dmDisplayFrequency;
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            }
            return maxHz;
        }

        public static int[] GetSupportedRefreshRatesCore(int displayIndex = 0)
        {
            if (displayIndex < 0) return new[] { 60 };
            string? devName = GetDisplayDeviceName(displayIndex);
            if (displayIndex > 0 && devName == null) return new[] { 60 };
            DEVMODE cur = new(); cur.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(devName, ENUM_CURRENT_SETTINGS, ref cur)) return new[] { 60 };

            var rates = new HashSet<int>();
            if (cur.dmDisplayFrequency > 0) rates.Add(cur.dmDisplayFrequency);

            DEVMODE dm = new(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            for (int modeNum = 0; EnumDisplaySettings(devName, modeNum, ref dm); modeNum++)
            {
                if (IsSameGeometry(dm, cur) && dm.dmDisplayFrequency > 0)
                    rates.Add(dm.dmDisplayFrequency);
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            }
            return rates.Count > 0 ? rates.OrderBy(r => r).ToArray() : new[] { 60 };
        }

        internal static bool IsSameGeometry(DEVMODE a, DEVMODE b) =>
            a.dmPelsWidth == b.dmPelsWidth &&
            a.dmPelsHeight == b.dmPelsHeight &&
            a.dmBitsPerPel == b.dmBitsPerPel &&
            (a.dmDisplayFlags & DM_INTERLACED) == 0;

        public static bool SetRefreshRateCore(int hz, int displayIndex = 0)
        {
            if (SetRefreshRateOverride != null) return SetRefreshRateOverride(hz);
            if (hz <= 0 || displayIndex < 0) return false;

            string? devName = GetDisplayDeviceName(displayIndex);
            if (displayIndex > 0 && devName == null) return false;
            DEVMODE cur = new(); cur.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(devName, ENUM_CURRENT_SETTINGS, ref cur)) return false;
            if (cur.dmDisplayFrequency == hz) return true;

            DEVMODE dm = new(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            DEVMODE? match = null;
            for (int modeNum = 0; EnumDisplaySettings(devName, modeNum, ref dm); modeNum++)
            {
                if (IsSameGeometry(dm, cur) && dm.dmDisplayFrequency == hz) { match = dm; break; }
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            }
            if (match == null) return false;

            DEVMODE target = match.Value;
            target.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            target.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

            if (ChangeDisplaySettingsEx(devName, ref target, IntPtr.Zero, CDS_TEST, IntPtr.Zero) != DISP_CHANGE_SUCCESSFUL) return false;
            return ChangeDisplaySettingsEx(devName, ref target, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
        }



        internal static Func<int, bool>? SetRefreshRateOverride { get; set; }

        private int GetCurrentRefreshRate() => GetCurrentRefreshRateCore();

        private bool SetRefreshRate(int hz)

        {

            if (SetRefreshRateOverride != null) return SetRefreshRateOverride(hz);

            return SetRefreshRateCore(hz);

        }



        #endregion



        #region System Tray



        private void SetupSystemTray()

        {

            try { _trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }

            catch { _trayIcon.Icon = SystemIcons.Application; }



            _trayIcon.ContextMenuStrip = _trayMenu;

            try { _trayIcon.Text = "Predator Control"; } catch { }

            try { _trayIcon.Visible = true; } catch { }

            _trayIcon.DoubleClick += (s, e) => ShowApp();
            _trayIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowApp(); };
            _trayIcon.BalloonTipClicked += (s, e) =>
            {
                SafeBeginInvoke(() =>
                {
                    ShowApp();
                    if (_pendingUpdateInfo != null)
                    {
                        _ = PromptUpdateAsync(_pendingUpdateInfo);
                    }
                });
            };

        }



        private void BuildTrayMenu()

        {

            try { _trayMenu?.Dispose(); } catch { }

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

            var trayDisplaySettings = new ToolStripMenuItem("Display Settings (Hardware MUX Switch)...", null, (s, e) =>
            {
                if (!this.Visible) this.Show();
                if (this.WindowState == FormWindowState.Minimized) this.WindowState = FormWindowState.Normal;
                this.Activate();
                SwitchPage(_pnlDisplay, _btnNavDisplay);
            });
            displayMenu.DropDownItems.AddRange([_trayDisplay60, _trayDisplayMax, new ToolStripSeparator(), _trayLcdOverdrive, new ToolStripSeparator(), trayDisplaySettings]);



            var rgbMenu = new ToolStripMenuItem("  Keyboard RGB");

            _trayRgbStatic = new ToolStripMenuItem("Static", null, (s, e) => ApplyRgbModeFromDropdown(0));

            _trayRgbBreathe = new ToolStripMenuItem("Breathing", null, (s, e) => ApplyRgbModeFromDropdown(1));

            _trayRgbNeon = new ToolStripMenuItem("Neon", null, (s, e) => ApplyRgbModeFromDropdown(2));

            _trayRgbWave = new ToolStripMenuItem("Wave", null, (s, e) => ApplyRgbModeFromDropdown(3));

            _trayRgbShift = new ToolStripMenuItem("Shifting", null, (s, e) => ApplyRgbModeFromDropdown(4));

            _trayRgbZoom = new ToolStripMenuItem("Zoom", null, (s, e) => ApplyRgbModeFromDropdown(5));

            _trayRgbMeteor = new ToolStripMenuItem("Meteor", null, (s, e) => ApplyRgbModeFromDropdown(6));

            rgbMenu.DropDownItems.AddRange([_trayRgbStatic, _trayRgbBreathe, _trayRgbNeon, _trayRgbWave,

                                            _trayRgbShift, _trayRgbZoom, _trayRgbMeteor]);



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



        public void NavigateToPanel(int panelIndex)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => NavigateToPanel(panelIndex))); } catch { }
                return;
            }
            switch (panelIndex % 7)
            {
                case 0: SwitchPage(_pnlDashboard, _btnNavDashboard); break;
                case 1: SwitchPage(_pnlFans, _btnNavFans); break;
                case 2: SwitchPage(_pnlLighting, _btnNavLighting); break;
                case 3: SwitchPage(_pnlDisplay, _btnNavDisplay); break;
                case 4: SwitchPage(_pnlGameSync, _btnNavGameSync); break;
                case 5: SwitchPage(_pnlSystem, _btnNavSystem); break;
                case 6: SwitchPage(_pnlSettings, _btnNavSettings); break;
            }
        }

        private void SwitchPage(DarkScrollPanel targetPage, PredatorButton targetBtn)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => SwitchPage(targetPage, targetBtn))); } catch { }
                return;
            }
            if (targetPage == null || targetBtn == null) return;

            if (_pnlDashboard != null) _pnlDashboard.Visible = false;
            if (_pnlFans != null) _pnlFans.Visible = false;
            if (_pnlLighting != null) _pnlLighting.Visible = false;
            if (_pnlDisplay != null) _pnlDisplay.Visible = false;
            if (_pnlGameSync != null) _pnlGameSync.Visible = false;
            if (_pnlSystem != null) _pnlSystem.Visible = false;
            if (_pnlSettings != null) _pnlSettings.Visible = false;

            targetPage.Visible = true;
            targetPage.BringToFront();

            if (_btnNavDashboard != null) _btnNavDashboard.IsActive = false;
            if (_btnNavFans != null) _btnNavFans.IsActive = false;
            if (_btnNavLighting != null) _btnNavLighting.IsActive = false;
            if (_btnNavDisplay != null) _btnNavDisplay.IsActive = false;
            if (_btnNavGameSync != null) _btnNavGameSync.IsActive = false;
            if (_btnNavSystem != null) _btnNavSystem.IsActive = false;
            if (_btnNavSettings != null) _btnNavSettings.IsActive = false;

            targetBtn.IsActive = true;
            _activeNavBtn = targetBtn;
            if (_shortcutRows != null)
            {
                foreach (var r in _shortcutRows) { if (r != null && r.IsRecording) StopShortcutRecording(r); }
            }

            if (_pnlCalibrationOverlay != null && _pnlCalibrationOverlay.Visible)
            {
                _pnlCalibrationOverlay.BringToFront();
            }
        }

        private void SelectNavButton(PredatorButton targetBtn, DarkScrollPanel targetPage)
        {
            SwitchPage(targetPage, targetBtn);
        }

        private void SelectNavButton(PredatorButton targetBtn)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => SelectNavButton(targetBtn))); } catch { }
                return;
            }
            if (targetBtn == _btnNavDashboard) SwitchPage(_pnlDashboard, _btnNavDashboard);
            else if (targetBtn == _btnNavFans) SwitchPage(_pnlFans, _btnNavFans);
            else if (targetBtn == _btnNavLighting) SwitchPage(_pnlLighting, _btnNavLighting);
            else if (targetBtn == _btnNavDisplay) SwitchPage(_pnlDisplay, _btnNavDisplay);
            else if (targetBtn == _btnNavGameSync) SwitchPage(_pnlGameSync, _btnNavGameSync);
            else if (targetBtn == _btnNavSystem) SwitchPage(_pnlSystem, _btnNavSystem);
            else if (targetBtn == _btnNavSettings) SwitchPage(_pnlSettings, _btnNavSettings);
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

                    var work = Screen.FromHandle(this.Handle).WorkingArea;

                    int targetH = Math.Max(S(540), Math.Min(S(720), work.Height - S(40)));

                    int targetW = (int)Math.Round(targetH * 16.0 / 9.0);

                    if (targetW > work.Width - S(40))

                    {

                        targetW = work.Width - S(40);

                        targetH = (int)Math.Round(targetW * 9.0 / 16.0);

                    }

                    int targetX = work.X + (work.Width - targetW) / 2;

                    int targetY = work.Y + (work.Height - targetH) / 2;

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

                    _appIconBitmap?.Dispose();

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
            _lblModePill.SizeChanged += (s, e) =>
            {
                if (_lblPowerPill != null && !_lblPowerPill.IsDisposed)
                {
                    _lblPowerPill.Left = _lblModePill.Right + S(12);
                }
            };

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

            _btnCaptionClose.Click += (s, e) => HideApp();



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

            _btnCaptionMin.Click += (s, e) => HideApp();



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
            int navBtnH = S(38);
            int navGap = S(4);
            int y = S(12);

            _btnNavDashboard = CreateNavButton("📊  Dashboard", navPad, y, navBtnW, navBtnH);
            _btnNavDashboard.Click += (s, e) => SwitchPage(_pnlDashboard, _btnNavDashboard);
            y += navBtnH + navGap;

            _btnNavFans = CreateNavButton("💨  Fans & Cooling", navPad, y, navBtnW, navBtnH);
            _btnNavFans.Click += (s, e) => SwitchPage(_pnlFans, _btnNavFans);
            y += navBtnH + navGap;

            _btnNavLighting = CreateNavButton("✨  RGB Lighting", navPad, y, navBtnW, navBtnH);
            _btnNavLighting.Click += (s, e) => SwitchPage(_pnlLighting, _btnNavLighting);
            y += navBtnH + navGap;

            _btnNavDisplay = CreateNavButton("🖥️  Display Settings", navPad, y, navBtnW, navBtnH);
            _btnNavDisplay.Click += (s, e) => SwitchPage(_pnlDisplay, _btnNavDisplay);
            y += navBtnH + navGap;

            _btnNavGameSync = CreateNavButton("🎮  Game Sync", navPad, y, navBtnW, navBtnH);
            _btnNavGameSync.Click += (s, e) => SwitchPage(_pnlGameSync, _btnNavGameSync);
            y += navBtnH + navGap;

            _btnNavSystem = CreateNavButton("⚡  System & Power", navPad, y, navBtnW, navBtnH);
            _btnNavSystem.Click += (s, e) => SwitchPage(_pnlSystem, _btnNavSystem);
            y += navBtnH + navGap;

            _btnNavSettings = CreateNavButton("⚙️  Settings & Info", navPad, y, navBtnW, navBtnH);
            _btnNavSettings.Click += (s, e) => SwitchPage(_pnlSettings, _btnNavSettings);

            _sidebarPanel.Controls.Add(_btnNavDashboard);
            _sidebarPanel.Controls.Add(_btnNavFans);
            _sidebarPanel.Controls.Add(_btnNavLighting);
            _sidebarPanel.Controls.Add(_btnNavDisplay);
            _sidebarPanel.Controls.Add(_btnNavGameSync);
            _sidebarPanel.Controls.Add(_btnNavSystem);
            _sidebarPanel.Controls.Add(_btnNavSettings);



            int footerY = sidebarH - S(44);

            var lblSideVer = new Label

            {

                Text = $"v.{Updater.CurrentText}  Made by RED4724",

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
            _pnlDisplay = CreatePagePanel(containerW, containerH);
            _pnlGameSync = CreatePagePanel(containerW, containerH);
            _pnlSystem = CreatePagePanel(containerW, containerH);
            _pnlSettings = CreatePagePanel(containerW, containerH);

            _contentPanel = _pnlDashboard;

            BuildDashboardPage();
            BuildFansPage();
            BuildLightingPage();
            BuildDisplayPage();
            BuildGameSyncPage();
            BuildSystemPage();
            BuildSettingsPage();

            _pageContainer.Controls.Add(_pnlDashboard);
            _pageContainer.Controls.Add(_pnlFans);
            _pageContainer.Controls.Add(_pnlLighting);
            _pageContainer.Controls.Add(_pnlDisplay);
            _pageContainer.Controls.Add(_pnlGameSync);
            _pageContainer.Controls.Add(_pnlSystem);
            _pageContainer.Controls.Add(_pnlSettings);



            BuildCalibrationOverlay();

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

            var lblCpuTitle = MakeLabel(cardSensors, "CPU:", cPad, S(44), FontBody, SubHeaderColor);

            _lblCpuTemp = MakeLabel(cardSensors, "43°C", lblCpuTitle.Right + S(8), S(44), FontBodyBold, ThemeManager.TextPrimary);



            var lblGpuTitle = MakeLabel(cardSensors, "GPU:", cardW / 2, S(44), FontBody, SubHeaderColor);

            _lblGpuTemp = MakeLabel(cardSensors, "39°C", lblGpuTitle.Right + S(8), S(44), FontBodyBold, ThemeManager.TextPrimary);

            _lblGpuSleepBadge = MakeLabel(cardSensors, "[ 💤 Low Power Standby ]", _lblGpuTemp.Right + S(8), S(44), FontHeaderLight, ThemeManager.StatusD3Cold, "d3cold");

            _lblGpuSleepBadge.Visible = false;



            var lblCpuFanTitle = MakeLabel(cardSensors, "CPU FAN:", cPad, S(72), FontBody, SubHeaderColor);

            _lblCpuRpm = MakeLabel(cardSensors, "-- RPM", lblCpuFanTitle.Right + S(8), S(72), FontBodyBold, ThemeManager.TextPrimary);



            var lblGpuFanTitle = MakeLabel(cardSensors, "GPU FAN:", cardW / 2, S(72), FontBody, SubHeaderColor);

            _lblGpuRpm = MakeLabel(cardSensors, "-- RPM", lblGpuFanTitle.Right + S(8), S(72), FontBodyBold, ThemeManager.TextPrimary);



            if (_capabilities.HasThirdFan)
            {
                var lblSysFanTitle = MakeLabel(cardSensors, "SYSTEM FAN:", cPad, S(100), FontBody, SubHeaderColor);
                int sysW = TextRenderer.MeasureText("SYSTEM FAN:", FontBody).Width;
                _lblSysFanRpm = MakeLabel(cardSensors, "-- RPM", cPad + sysW + S(8), S(100), FontBodyBold, ThemeManager.TextPrimary);

                _lblHardwareLinkTitle = MakeLabel(cardSensors, "HARDWARE LINK:", cardW / 2, S(100), FontBody, SubHeaderColor);
                int linkW = TextRenderer.MeasureText("HARDWARE LINK:", FontBody).Width;
                _lblProtocolBadge = MakeLabel(cardSensors, _wmi.EcHid != null && _wmi.EcHid.IsOpen ? "Direct Hardware Controller" : "Acer System Driver", (cardW / 2) + linkW + S(8), S(100), FontBodyBold, AccentColor, "accent");
                _lblProtocolBadge.AutoEllipsis = true;
                _lblProtocolBadge.MaximumSize = new Size(Math.Max(S(50), cardW - _lblProtocolBadge.Left - cPad), S(20));
            }
            else
            {
                _lblSysFanRpm = null;
                _lblHardwareLinkTitle = MakeLabel(cardSensors, "HARDWARE LINK:", cPad, S(100), FontBody, SubHeaderColor);
                int linkW = TextRenderer.MeasureText("HARDWARE LINK:", FontBody).Width;
                _lblProtocolBadge = MakeLabel(cardSensors, _wmi.EcHid != null && _wmi.EcHid.IsOpen ? "Direct Hardware Controller" : "Acer System Driver", cPad + linkW + S(8), S(100), FontBodyBold, AccentColor, "accent");
                _lblProtocolBadge.AutoEllipsis = true;
                _lblProtocolBadge.MaximumSize = new Size(Math.Max(S(50), cardW - _lblProtocolBadge.Left - cPad), S(20));
            }



            var lblPowerTitle = MakeLabel(cardSensors, "POWER SOURCE:", cPad, S(128), FontBody, SubHeaderColor);

            var pInitDash = SystemInformation.PowerStatus;

            bool isChargingDash = (pInitDash.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;

            bool isAcDash = pInitDash.PowerLineStatus == PowerLineStatus.Online || isChargingDash;

            string initPowerSource = isChargingDash ? "Charging" : (isAcDash ? "Plugged In" : "On Battery");

            _lblPowerStatus = MakeLabel(cardSensors, initPowerSource, lblPowerTitle.Right + S(8), S(128), FontBodyBold, ThemeManager.TextPrimary);



            var lblFanProfileTitle = MakeLabel(cardSensors, "FAN PROFILE:", cardW / 2, S(128), FontBody, SubHeaderColor);

            _lblFanStatus = MakeLabel(cardSensors, "Auto", lblFanProfileTitle.Right + S(8), S(128), FontBodyBold, ThemeManager.TextPrimary);



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



            MakeLabel(cardPower, "MODE KEY ACTION:", cPad, S(86), FontBody, SubHeaderColor);

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
                if (_isInitializing || _isUpdatingModeKeyAction) return;
                try
                {
                    _isUpdatingModeKeyAction = true;
                    _modeKeyAction = _cboModeKeyAction.SelectedIndex;
                    if (_cboModeKeyActionSys != null && _cboModeKeyActionSys.SelectedIndex != _modeKeyAction)
                        _cboModeKeyActionSys.SelectedIndex = _modeKeyAction;
                    SaveState("ModeKeyAction", _modeKeyAction);
                }
                finally
                {
                    _isUpdatingModeKeyAction = false;
                }
            };

            cardPower.Controls.Add(_cboModeKeyAction);



            _btnTriggerModeKey = MakeButton(cardPower, "⚡ Switch Mode", cPad + mkaDropW + S(8), S(108), S(100), S(30));

            _btnTriggerModeKey.Click += (s, e) => HandleModeKeyPress();



            MakeLabel(cardPower, "Press Mode key or button to cycle profiles or toggle Turbo mode.", cPad, S(148), FontHeaderLight, SubHeaderColor);



            // Card 3B: CPU Power Limits (PL1 / PL2)

            _cardPowerLimits = BuildCpuPowerLimitsSection(_pnlDashboard, col2X, pad + S(196), cardW);



            // Card 4: Display Refresh Rate & LCD Overdrive

            var cardQuickCooling = MakeCard(_pnlDashboard, "DISPLAY & REFRESH RATE", col2X, pad + S(464), cardW, S(164));

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

            var cardQuickJump = MakeCard(_pnlDashboard, "QUICK NAVIGATION & SHORTCUTS", col2X, pad + S(640), cardW, S(184));

            var btnJumpFans = MakeButton(cardQuickJump, "💨  Open Fan Controls & Speed Locking", cPad, S(44), cInnerW, S(34));

            btnJumpFans.Click += (s, e) => SwitchPage(_pnlFans, _btnNavFans);



            var btnJumpSys = MakeButton(cardQuickJump, "⚡  Open Graphics & Power Automation", cPad, S(88), cInnerW, S(34));

            btnJumpSys.Click += (s, e) => SwitchPage(_pnlSystem, _btnNavSystem);



            var btnJumpRgb = MakeButton(cardQuickJump, "✨  Open RGB Lighting Studio", cPad, S(132), cInnerW, S(34));

            btnJumpRgb.Click += (s, e) => SwitchPage(_pnlLighting, _btnNavLighting);



            _pnlDashboard.AutoScrollMinSize = new Size(0, S(860));

        }





        #region CPU Power Limits (PL1 / PL2) Implementation



        private Panel BuildCpuPowerLimitsSection(Control parent, int x, int y, int cardW)

        {

            int cPad = S(14);

            int cInnerW = cardW - cPad * 2;

            int cardH = S(256);



            var card = MakeCard(parent, "CPU POWER LIMITS (PL1 / PL2)", x, y, cardW, cardH);



            // Mode context header

            string modeName = GetPowerModeName(_currentPowerMode);

            Color modeColor = ThemeManager.GetPowerModeColor(_currentPowerMode);

            _lblPowerLimitModeContext = MakeLabel(card, $"ACTIVE MODE: {modeName.ToUpperInvariant()}", cPad, S(38), FontBody, modeColor, "custom_color");



            // Hardware bounds summary

            string boundsSummary = UiLabelFormatter.FormatHardwarePowerBoundsSummary(

                _capabilities.ScannedMinPowerWatts,

                _capabilities.ScannedMaxPl1Watts,

                _capabilities.ScannedMaxPl2Watts,

                _capabilities.CpuTdpWatts,

                _capabilities.CpuModel);

            _lblPowerBoundsSummary = MakeLabel(card, boundsSummary, cPad, S(58), FontHeaderLight, SubHeaderColor);

            _lblPowerBoundsSummary.AutoSize = false;

            _lblPowerBoundsSummary.Size = new Size(cInnerW, S(20));

            _lblPowerBoundsSummary.AutoEllipsis = true;



            // Divider

            card.Controls.Add(new Panel { Location = new Point(cPad, S(76)), Size = new Size(cInnerW, 1), BackColor = ThemeManager.Separator });



            // PL1 Section

            int defPl1 = _capabilities.GetScannedPl1(_currentPowerMode);

            int defPl2 = _capabilities.GetScannedPl2(_currentPowerMode);

            var opMode = ByteToOperatingMode(_currentPowerMode);

            var (biosDefPl1, biosDefPl2, _) = _appSettings.GetBiosDefaultLimitsForMode(opMode);

            if (biosDefPl1 > 0) defPl1 = biosDefPl1;

            if (biosDefPl2 > 0) defPl2 = biosDefPl2;



            var (initPl1, initPl2) = GetSavedModePowerLimits(_currentPowerMode, defPl1, defPl2);



            int minPower = _appSettings.CpuMinAllowedPowerWatts > 0 ? _appSettings.CpuMinAllowedPowerWatts

                : (_capabilities.ScannedMinPowerWatts > 0 ? _capabilities.ScannedMinPowerWatts : 15);

            int maxPl2 = _appSettings.CpuMaxAllowedPl2Watts > 0 ? _appSettings.CpuMaxAllowedPl2Watts

                : (_capabilities.ScannedMaxPl2Watts > 0 ? _capabilities.ScannedMaxPl2Watts : 140);

            if (maxPl2 < minPower) maxPl2 = minPower;

            initPl1 = Math.Clamp(initPl1, minPower, maxPl2);

            initPl2 = Math.Clamp(initPl2, initPl1, maxPl2);



            _lblPl1Value = MakeLabel(card, UiLabelFormatter.FormatPl1Label(initPl1, defPl1), cPad, S(82), FontBodyBold, ThemeManager.TextPrimary);

            _sliderPl1 = new PredatorSlider

            {

                Location = new Point(cPad, S(104)),

                Size = new Size(cInnerW, S(26)),

                Minimum = minPower,

                Maximum = maxPl2,

                Value = initPl1

            };

            _sliderPl1.ValueChanged += SliderPl1_ValueChanged;

            _sliderPl1.ValueCommitted += SliderPl1_ValueCommitted;

            card.Controls.Add(_sliderPl1);



            // PL2 Section

            _lblPl2Value = MakeLabel(card, UiLabelFormatter.FormatPl2Label(initPl2, defPl2), cPad, S(136), FontBodyBold, ThemeManager.TextPrimary);

            _sliderPl2 = new PredatorSlider

            {

                Location = new Point(cPad, S(158)),

                Size = new Size(cInnerW, S(26)),

                Minimum = minPower,

                Maximum = maxPl2,

                Value = initPl2

            };

            _sliderPl2.ValueChanged += SliderPl2_ValueChanged;

            _sliderPl2.ValueCommitted += SliderPl2_ValueCommitted;

            card.Controls.Add(_sliderPl2);



            // Reset to Laptop Default Button & Scan Device for PawnIO Button

            int btnSpacing = S(8);

            int scanBtnW = S(90);

            int resetBtnW = cInnerW - scanBtnW - btnSpacing;



            _btnResetPowerLimits = MakeButton(card, UiLabelFormatter.FormatResetToBiosDefaultButton(opMode, defPl1, defPl2), cPad, S(192), resetBtnW, S(28));

            _btnResetPowerLimits.Tag = $"{defPl1},{defPl2}";

            _btnResetPowerLimits.Click += BtnResetPowerLimits_Click;



            _btnScanPawnIo = MakeButton(card, "🔍 Re-Scan", cPad + resetBtnW + btnSpacing, S(192), scanBtnW, S(28));

            _btnScanPawnIo.Click += (s, e) => ReScanPawnIoHardware();



            // Power Limit Apply Status Feedback Label

            _lblPowerLimitStatus = MakeLabel(card, "", cPad, S(226), FontHeaderLight, ThemeManager.TextSecondary);

            _lblPowerLimitStatus.AutoSize = false;

            _lblPowerLimitStatus.AutoEllipsis = true;

            _lblPowerLimitStatus.Size = new Size(cInnerW, S(24));

            _lblPowerLimitStatus.Cursor = Cursors.Hand;

            _lblPowerLimitStatus.Click += (s, e) => ReScanPawnIoHardware();



            var statusContextMenu = new ContextMenuStrip();

            var menuScanAction = new ToolStripMenuItem("🔍 Scan Device for PawnIO", null, (s, e) => ReScanPawnIoHardware());

            var menuRescanAction = new ToolStripMenuItem("🔄 Re-Scan Hardware", null, (s, e) => ReScanPawnIoHardware());

            statusContextMenu.Items.Add(menuScanAction);

            statusContextMenu.Items.Add(menuRescanAction);

            _lblPowerLimitStatus.ContextMenuStrip = statusContextMenu;



            UpdatePowerLimitStatusUi();



            return card;

        }



        private void SliderPl1_ValueChanged(object? sender, EventArgs e)

        {

            if (_isUpdatingPowerLimitsUi || _sliderPl1 == null || _sliderPl2 == null) return;



            // Real-time PL2 >= PL1 coupling: dragging PL1 above PL2 raises PL2

            if (_sliderPl1.Value > _sliderPl2.Value)

            {

                _isUpdatingPowerLimitsUi = true;

                try

                {

                    _sliderPl2.Value = _sliderPl1.Value;

                    if (_sliderPl1.Value > _sliderPl2.Value)

                    {

                        _sliderPl1.Value = _sliderPl2.Value;

                    }

                }

                finally

                {

                    _isUpdatingPowerLimitsUi = false;

                }

            }



            UpdatePowerLimitLabels();

        }



        private void SliderPl1_ValueCommitted(object? sender, EventArgs e)

        {

            ApplyCurrentPowerLimitsToHardware();

        }



        private void SliderPl2_ValueChanged(object? sender, EventArgs e)

        {

            if (_isUpdatingPowerLimitsUi || _sliderPl1 == null || _sliderPl2 == null) return;



            // Real-time PL2 >= PL1 coupling: dragging PL2 below PL1 lowers PL1

            if (_sliderPl2.Value < _sliderPl1.Value)

            {

                _isUpdatingPowerLimitsUi = true;

                try

                {

                    _sliderPl1.Value = _sliderPl2.Value;

                    if (_sliderPl2.Value < _sliderPl1.Value)

                    {

                        _sliderPl2.Value = _sliderPl1.Value;

                    }

                }

                finally

                {

                    _isUpdatingPowerLimitsUi = false;

                }

            }



            UpdatePowerLimitLabels();

        }



        private void SliderPl2_ValueCommitted(object? sender, EventArgs e)

        {

            ApplyCurrentPowerLimitsToHardware();

        }



        internal void BtnResetPowerLimits_Click(object? sender, EventArgs e)

        {

            byte mode = _currentPowerMode;

            var opMode = ByteToOperatingMode(mode);

            var (biosDefPl1, biosDefPl2, _) = _appSettings.GetBiosDefaultLimitsForMode(opMode);

            int defPl1 = biosDefPl1 > 0 ? biosDefPl1 : _capabilities.GetScannedPl1(mode);

            int defPl2 = biosDefPl2 > 0 ? biosDefPl2 : _capabilities.GetScannedPl2(mode);



            int minPower = _appSettings.CpuMinAllowedPowerWatts > 0 ? _appSettings.CpuMinAllowedPowerWatts

                : (_capabilities.ScannedMinPowerWatts > 0 ? _capabilities.ScannedMinPowerWatts : 15);

            int maxPl2 = _appSettings.CpuMaxAllowedPl2Watts > 0 ? _appSettings.CpuMaxAllowedPl2Watts

                : (_capabilities.ScannedMaxPl2Watts > 0 ? _capabilities.ScannedMaxPl2Watts : 140);

            if (maxPl2 < minPower) maxPl2 = minPower;

            int maxPl1 = maxPl2; // Expand custom PL1 sliders all the way up to CPU Max PL2 in all 5 modes

            _isUpdatingPowerLimitsUi = true;

            try

            {

                if (_sliderPl1 != null && _sliderPl2 != null)

                {

                    _sliderPl1.Minimum = 0;

                    _sliderPl1.Maximum = 500;

                    _sliderPl2.Minimum = 0;

                    _sliderPl2.Maximum = 500;



                    _sliderPl1.Minimum = minPower;

                    _sliderPl1.Maximum = maxPl1;

                    _sliderPl2.Minimum = minPower;

                    _sliderPl2.Maximum = maxPl2;



                    if (defPl2 >= _sliderPl1.Value)

                    {

                        _sliderPl2.Value = defPl2;

                        _sliderPl1.Value = defPl1;

                    }

                    else

                    {

                        _sliderPl1.Value = defPl1;

                        _sliderPl2.Value = defPl2;

                    }

                }

            }

            finally

            {

                _isUpdatingPowerLimitsUi = false;

            }



            SaveModePowerLimits(mode, defPl1, defPl2, isReset: true);

            UpdatePowerLimitLabels();

            ApplyCurrentPowerLimitsToHardware(isReset: true);

        }



        internal void UpdatePowerLimitsForMode(byte mode)

        {

            _currentPowerMode = mode;

            if (_sliderPl1 == null || _sliderPl2 == null) return;

            SyncCapabilitiesWithPowerLimits();



            string modeName = GetPowerModeName(mode);

            Color modeColor = ThemeManager.GetPowerModeColor(mode);



            if (_lblPowerLimitModeContext != null)

            {

                _lblPowerLimitModeContext.Text = $"ACTIVE MODE: {modeName.ToUpperInvariant()}";

                _lblPowerLimitModeContext.ForeColor = modeColor;

            }



            if (_lblPowerBoundsSummary != null)

            {

                _lblPowerBoundsSummary.Text = UiLabelFormatter.FormatHardwarePowerBoundsSummary(

                    _capabilities.ScannedMinPowerWatts,

                    _capabilities.ScannedMaxPl1Watts,

                    _capabilities.ScannedMaxPl2Watts,

                    _capabilities.CpuTdpWatts,

                    _capabilities.CpuModel);

            }



                        int minPower = _appSettings.CpuMinAllowedPowerWatts > 0 ? _appSettings.CpuMinAllowedPowerWatts

                : (_capabilities.ScannedMinPowerWatts > 0 ? _capabilities.ScannedMinPowerWatts : 15);

            int maxPl2 = _appSettings.CpuMaxAllowedPl2Watts > 0 ? _appSettings.CpuMaxAllowedPl2Watts

                : (_capabilities.ScannedMaxPl2Watts > 0 ? _capabilities.ScannedMaxPl2Watts : 140);

            if (maxPl2 < minPower) maxPl2 = minPower;

            int maxPl1 = maxPl2; // Expand custom PL1 sliders all the way up to CPU Max PL2 in all 5 modes

            var opMode = ByteToOperatingMode(mode);

            var (biosDefPl1, biosDefPl2, _) = _appSettings.GetBiosDefaultLimitsForMode(opMode);

            int defPl1 = biosDefPl1 > 0 ? biosDefPl1 : _capabilities.GetScannedPl1(mode);

            int defPl2 = biosDefPl2 > 0 ? biosDefPl2 : _capabilities.GetScannedPl2(mode);



            var (curPl1, curPl2) = GetSavedModePowerLimits(mode, defPl1, defPl2);



            curPl1 = Math.Clamp(curPl1, minPower, maxPl1);

            curPl2 = Math.Clamp(curPl2, curPl1, Math.Max(curPl1, maxPl2));



            _isUpdatingPowerLimitsUi = true;

            try

            {

                // First widen bounds to prevent WinForms slider clamping

                _sliderPl1.Minimum = 0;

                _sliderPl1.Maximum = 500;

                _sliderPl2.Minimum = 0;

                _sliderPl2.Maximum = 500;



                _sliderPl1.Minimum = minPower;

                _sliderPl1.Maximum = maxPl2;

                _sliderPl2.Minimum = minPower;

                _sliderPl2.Maximum = maxPl2;



                // Ensure PL2 is adjusted first when increasing values to maintain PL2 >= PL1

                if (curPl2 >= _sliderPl1.Value)

                {

                    _sliderPl2.Value = curPl2;

                    _sliderPl1.Value = curPl1;

                }

                else

                {

                    _sliderPl1.Value = curPl1;

                    _sliderPl2.Value = curPl2;

                }

            }

            finally

            {

                _isUpdatingPowerLimitsUi = false;

            }



            UpdatePowerLimitLabels();



            if (_btnResetPowerLimits != null)

            {

                _btnResetPowerLimits.Text = UiLabelFormatter.FormatResetToBiosDefaultButton(opMode, defPl1, defPl2);

                _btnResetPowerLimits.Tag = $"{defPl1},{defPl2}";

                bool isCustomized = (curPl1 != defPl1) || (curPl2 != defPl2);

                _btnResetPowerLimits.Enabled = isCustomized;

            }

        }



        private void UpdatePowerLimitLabels()

        {

            if (_sliderPl1 == null || _sliderPl2 == null) return;

            byte mode = _currentPowerMode;

            var opMode = ByteToOperatingMode(mode);

            var (biosPl1, biosPl2, _) = _appSettings.GetBiosDefaultLimitsForMode(opMode);

            if (biosPl1 <= 0 || biosPl2 <= 0)

            {

                biosPl1 = _capabilities.GetScannedPl1(mode);

                biosPl2 = _capabilities.GetScannedPl2(mode);

            }



            if (_lblPl1Value != null)

                _lblPl1Value.Text = UiLabelFormatter.FormatPl1Label(_sliderPl1.Value, biosPl1);



            if (_lblPl2Value != null)

                _lblPl2Value.Text = UiLabelFormatter.FormatPl2Label(_sliderPl2.Value, biosPl2);



            if (_btnResetPowerLimits != null)

            {

                _btnResetPowerLimits.Text = UiLabelFormatter.FormatResetToBiosDefaultButton(opMode, biosPl1, biosPl2);

                bool isCustomized = (_sliderPl1.Value != biosPl1) || (_sliderPl2.Value != biosPl2);

                _btnResetPowerLimits.Enabled = isCustomized;

            }

        }



        private (int pl1, int pl2) GetSavedModePowerLimits(byte mode, int defPl1, int defPl2)

        {

            try

            {

                var opMode = ByteToOperatingMode(mode);

                var (storedPl1, storedPl2) = _appSettings.GetUserPowerLimitsForMode(opMode);

                int pl1 = storedPl1 > 0 ? storedPl1 : defPl1;

                int pl2 = storedPl2 > 0 ? storedPl2 : defPl2;

                return (pl1, pl2);

            }

            catch

            {

                return (defPl1, defPl2);

            }

        }



        private void SaveModePowerLimits(byte mode, int pl1, int pl2, bool isReset = false)

        {

            try

            {

                var opMode = ByteToOperatingMode(mode);

                if (isReset)

                {

                    _appSettings.ResetPowerLimitsForMode(opMode);

                }

                else

                {

                    _appSettings.SetPowerLimitsForMode(opMode, pl1, pl2);

                }

                _appSettings.Save();

            }

            catch { }

        }



        private void ApplyCurrentPowerLimitsToHardware(bool isReset = false, bool persistOverride = true)

        {

            if (_sliderPl1 == null || _sliderPl2 == null) return;

            long applySeq = Interlocked.Increment(ref _powerLimitApplySequence);

            int pl1 = _sliderPl1.Value;

            int pl2 = _sliderPl2.Value;

            byte mode = _currentPowerMode;

            var opMode = ByteToOperatingMode(mode);



            if (!isReset && persistOverride)

            {

                SaveModePowerLimits(mode, pl1, pl2, isReset: false);

            }



            Task.Run(() =>

            {

                if (_isClosing || _shutdownCts.IsCancellationRequested) return;

                if (applySeq != Volatile.Read(ref _powerLimitApplySequence)) return;



                try

                {

                    PowerLimitApplyResult result;

                    if (isReset)

                    {

                        result = CpuPowerLimitController.Instance.ResetModeToStockDefaults(opMode);

                    }

                    else

                    {

                        result = CpuPowerLimitController.Instance.SetPowerLimitsForMode(opMode, pl1, pl2);

                    }



                    SafeBeginInvoke(() =>

                    {

                        UpdatePowerLimitStatusUi(result, pl1, pl2, isReset);

                    });

                }

                catch (Exception ex)

                {

                    SafeBeginInvoke(() =>

                    {

                        if (_lblPowerLimitStatus != null)

                        {

                            _lblPowerLimitStatus.Text = $"⚠️ Error applying limits: {ex.Message}";

                            _lblPowerLimitStatus.ForeColor = ThemeManager.RedStatus;

                        }

                    });

                }

            });

        }



        private void UpdatePowerLimitStatusUi(PowerLimitApplyResult result, int pl1, int pl2, bool isReset = false)

        {

            if (_lblPowerLimitStatus == null) return;



            if (result.IsSuccess)

            {

                _lblPowerLimitStatus.Text = isReset

                    ? $"✓ Restored laptop default: {pl1}W / {pl2}W"

                    : $"✓ Applied to CPU: PL1={pl1}W | PL2={pl2}W";

                _lblPowerLimitStatus.ForeColor = ThemeManager.TealAccent;

            }

            else

            {

                switch (result.Status)

                {

                    case PowerLimitApplyStatus.AccessDenied:

                        _lblPowerLimitStatus.Text = "🔒 PawnIO detected! Run app as Administrator to unlock power limits";

                        _lblPowerLimitStatus.ForeColor = ThemeManager.YellowStatus;

                        break;

                    case PowerLimitApplyStatus.DriverNotInstalled:

                        var curScan = CpuPowerLimitController.Instance.ScanDeviceForPawnIo();

                        if (curScan.RequiresElevation)

                        {

                            _lblPowerLimitStatus.Text = "🔒 PawnIO detected! Run app as Administrator to unlock power limits";

                            _lblPowerLimitStatus.ForeColor = ThemeManager.YellowStatus;

                        }

                        else if (curScan.IsInstalled || _capabilities.PawnIoInstalled)

                        {

                            _lblPowerLimitStatus.Text = "⚠️ PawnIO service installed but stopped";

                            _lblPowerLimitStatus.ForeColor = ThemeManager.YellowStatus;

                        }

                        else

                        {

                            _lblPowerLimitStatus.Text = "⚠️ PawnIO driver not detected (Install PawnIO for CPU power limits)";

                            _lblPowerLimitStatus.ForeColor = ThemeManager.RedStatus;

                        }

                        break;

                    case PowerLimitApplyStatus.BiosLocked:

                        _lblPowerLimitStatus.Text = "🔒 CPU power limits locked by BIOS (MSR 0x610 Bit 63)";

                        _lblPowerLimitStatus.ForeColor = ThemeManager.YellowStatus;

                        break;

                    default:

                        _lblPowerLimitStatus.Text = string.IsNullOrWhiteSpace(result.Message)

                            ? $"⚠️ Hardware error ({result.Status})"

                            : $"⚠️ {result.Message}";

                        _lblPowerLimitStatus.ForeColor = ThemeManager.RedStatus;

                        break;

                }

            }

        }



        private void UpdatePowerLimitStatusUi()

        {

            if (_lblPowerLimitStatus == null) return;



            if (_capabilities.PawnIoAvailable)

            {

                _lblPowerLimitStatus.Text = "⚡ PawnIO Active (MSR & MMIO power limits unlocked)";

                _lblPowerLimitStatus.ForeColor = ThemeManager.TealAccent;

            }

            else if (_capabilities.PawnIoInstalled && _capabilities.PawnIoRequiresAdmin)

            {

                _lblPowerLimitStatus.Text = "🔒 PawnIO detected! Run app as Administrator to unlock power limits";

                _lblPowerLimitStatus.ForeColor = ThemeManager.YellowStatus;

            }

            else if (_capabilities.PawnIoInstalled)

            {

                _lblPowerLimitStatus.Text = "⚠️ PawnIO service installed but stopped";

                _lblPowerLimitStatus.ForeColor = ThemeManager.YellowStatus;

            }

            else

            {

                _lblPowerLimitStatus.Text = "⚠️ PawnIO driver not detected (Install PawnIO for CPU power limits)";

                _lblPowerLimitStatus.ForeColor = ThemeManager.RedStatus;

            }

        }



        private void SyncCapabilitiesWithPowerLimits()

        {

            if (CpuPowerLimitController.Instance == null) return;



            var powerCaps = CpuPowerLimitController.Instance.Capabilities;

            var pawnScan = CpuPowerLimitController.Instance.ScanDeviceForPawnIo();



            bool pawnIoAvailable = CpuPowerLimitController.Instance.IsAvailable && powerCaps.IsSupported;

            bool pawnIoInstalled = pawnScan.IsInstalled || powerCaps.BaselineStatus != PowerLimitApplyStatus.DriverNotInstalled;

            bool pawnIoRequiresAdmin = pawnScan.RequiresElevation || powerCaps.BaselineStatus == PowerLimitApplyStatus.AccessDenied;

            bool isMsrLocked = powerCaps.IsLocked;



            string diag = !string.IsNullOrWhiteSpace(powerCaps.Diagnostics) ? powerCaps.Diagnostics : pawnScan.DiagnosticMessage;



            var scannedDefaults = new Dictionary<byte, (int Pl1, int Pl2)>

            {

                { 0x06, _appSettings.GetDefaultPowerLimitsForMode(OperatingMode.Eco) },

                { 0x00, _appSettings.GetDefaultPowerLimitsForMode(OperatingMode.Quiet) },

                { 0x01, _appSettings.GetDefaultPowerLimitsForMode(OperatingMode.Balanced) },

                { 0x04, _appSettings.GetDefaultPowerLimitsForMode(OperatingMode.Performance) },

                { 0x05, _appSettings.GetDefaultPowerLimitsForMode(OperatingMode.Turbo) }

            };



            _capabilities = _capabilities with

            {

                BiosPowerScan = _capabilities.BiosPowerScan ?? CpuPowerLimitController.Instance?.Capabilities?.BiosPowerScan,

                PawnIoAvailable = pawnIoAvailable,

                PawnIoInstalled = pawnIoInstalled,

                PawnIoRequiresAdmin = pawnIoRequiresAdmin,

                PawnIoDiagnosticMessage = diag,

                IsMsr0x610Locked = isMsrLocked,

                ScannedMinPowerWatts = _capabilities.ScannedMinPowerWatts > 0 ? _capabilities.ScannedMinPowerWatts : (_appSettings.CpuMinAllowedPowerWatts > 0 ? _appSettings.CpuMinAllowedPowerWatts : (powerCaps.ScannedMinPowerWatts > 0 ? powerCaps.ScannedMinPowerWatts : 15)),

                ScannedMaxPl1Watts = _capabilities.ScannedMaxPl1Watts > 0 ? _capabilities.ScannedMaxPl1Watts : (_appSettings.CpuMaxAllowedPl1Watts > 0 ? _appSettings.CpuMaxAllowedPl1Watts : (powerCaps.ScannedMaxPl1Watts > 0 ? powerCaps.ScannedMaxPl1Watts : 115)),

                ScannedMaxPl2Watts = _capabilities.ScannedMaxPl2Watts > 0 ? _capabilities.ScannedMaxPl2Watts : (_appSettings.CpuMaxAllowedPl2Watts > 0 ? _appSettings.CpuMaxAllowedPl2Watts : (powerCaps.ScannedMaxPl2Watts > 0 ? powerCaps.ScannedMaxPl2Watts : 140)),

                CpuTdpWatts = _capabilities.CpuTdpWatts > 0 ? _capabilities.CpuTdpWatts : (_appSettings.CpuThermalSpecTdpWatts > 0 ? _appSettings.CpuThermalSpecTdpWatts : (powerCaps.ThermalSpecPowerWatts > 0 ? powerCaps.ThermalSpecPowerWatts : 45)),

                ScannedModeDefaults = scannedDefaults

            };



            UpdatePowerLimitStatusUi();

        }



        internal void ReScanPawnIoHardware()

        {

            _ = ReScanPawnIoHardwareAsync();

        }



        internal async Task ReScanPawnIoHardwareAsync()

        {

            if (Interlocked.CompareExchange(ref _isHardwareScanRunning, 1, 0) != 0) return;

            if (_btnScanPawnIo != null) _btnScanPawnIo.Enabled = false;



            try
            {
                if (_lblPowerLimitStatus != null)

            {

                _lblPowerLimitStatus.Text = "⏳ Scanning native BIOS/UEFI, ACPI tables & PawnIO hardware...";

                _lblPowerLimitStatus.ForeColor = ThemeManager.TealAccent;

            }



            await Task.Run(() =>

            {

                try

                {

                    var biosScan = CpuPowerLimitController.Instance?.ReScanHardwareAndBiosDefaults();

                    var scan = CpuPowerLimitController.Instance?.ScanDeviceForPawnIo();

                    var powerCaps = CpuPowerLimitController.Instance?.Capabilities;



                    SafeBeginInvoke(() =>

                    {

                        PerformReScanCore(biosScan, scan, powerCaps);

                    });

                }

                catch (Exception ex)

                {

                    SafeBeginInvoke(() =>

                    {

                        if (_lblPowerLimitStatus != null)

                        {

                            _lblPowerLimitStatus.Text = $"⚠️ Scan error: {ex.Message}";

                            _lblPowerLimitStatus.ForeColor = ThemeManager.RedStatus;

                        }

                    });

                }

            });

            }

            finally

            {

                Interlocked.Exchange(ref _isHardwareScanRunning, 0);

                SafeBeginInvoke(() =>

                {

                    if (_btnScanPawnIo != null && !IsDisposed && !_isClosing)

                    {

                        _btnScanPawnIo.Enabled = true;

                    }

                });

            }

        }



        internal void PerformReScanCore(BiosPowerScanResult? biosScan, PawnIoScanResult? scan, DevicePowerCapabilities? powerCaps)

        {

            bool pawnIoAvailable = CpuPowerLimitController.Instance?.IsAvailable == true && (powerCaps?.IsSupported == true);

            bool pawnIoInstalled = (scan?.IsInstalled == true) || (powerCaps != null && powerCaps.BaselineStatus != PowerLimitApplyStatus.DriverNotInstalled);

            bool pawnIoRequiresAdmin = (scan?.RequiresElevation == true) || (powerCaps != null && powerCaps.BaselineStatus == PowerLimitApplyStatus.AccessDenied);



            // Ensure Re-Scan prioritizes native BIOS/UEFI & ACPI discovery result, then fused silicon MSR 0x614, then verified settings

            int minW = 0;

            int max1W = 0;

            int max2W = 0;

            int tdpW = 0;



            if (biosScan != null && biosScan.HasCompletedBiosPowerScan && biosScan.CpuMaxAllowedPl2Watts > 0)

            {

                // Priority 1: Full native BIOS/UEFI & ACPI PPCC + silicon MSR scan result

                minW = biosScan.CpuMinAllowedPowerWatts;

                max1W = biosScan.CpuMaxAllowedPl1Watts;

                max2W = biosScan.CpuMaxAllowedPl2Watts;

                tdpW = biosScan.CpuThermalSpecTdpWatts;

            }

            else if (biosScan?.SiliconData != null && biosScan.SiliconData.IsFound && biosScan.SiliconData.MaximumPowerWatts > 0)

            {

                // Priority 1b: Fused silicon data from MSR scan

                minW = biosScan.SiliconData.MinimumPowerWatts;

                max2W = biosScan.SiliconData.MaximumPowerWatts;

                tdpW = biosScan.SiliconData.ThermalSpecPowerWatts;

                max1W = Math.Min(max2W, Math.Max(tdpW, (int)Math.Round(max2W * 0.85)));

            }

            else if (_appSettings.HasCompletedBiosPowerScan && _appSettings.CpuMaxAllowedPl2Watts > 0)

            {

                // Priority 2: Verified settings from previous authoritative scan

                minW = _appSettings.CpuMinAllowedPowerWatts;

                max1W = _appSettings.CpuMaxAllowedPl1Watts;

                max2W = _appSettings.CpuMaxAllowedPl2Watts;

                tdpW = _appSettings.CpuThermalSpecTdpWatts;

            }

            else if (powerCaps != null && powerCaps.IsSupported && powerCaps.ScannedMaxPl2Watts > 0)

            {

                // Priority 3: PawnIO device capabilities

                minW = powerCaps.ScannedMinPowerWatts;

                max1W = powerCaps.ScannedMaxPl1Watts;

                max2W = powerCaps.ScannedMaxPl2Watts;

                tdpW = powerCaps.ThermalSpecPowerWatts;

            }

            else

            {

                // Priority 4: Cached hardware capabilities

                minW = _capabilities.ScannedMinPowerWatts > 0 ? _capabilities.ScannedMinPowerWatts : _capabilities.CpuMinAllowedPowerWatts;

                max1W = _capabilities.ScannedMaxPl1Watts > 0 ? _capabilities.ScannedMaxPl1Watts : _capabilities.CpuMaxAllowedPl1Watts;

                max2W = _capabilities.ScannedMaxPl2Watts > 0 ? _capabilities.ScannedMaxPl2Watts : _capabilities.CpuMaxAllowedPl2Watts;

                tdpW = _capabilities.CpuTdpWatts > 0 ? _capabilities.CpuTdpWatts : _capabilities.CpuThermalSpecTdpWatts;

            }



            // Fallback to cached capabilities or settings if any bound is invalid or zero

            if (minW <= 0) minW = _capabilities.ScannedMinPowerWatts > 0 ? _capabilities.ScannedMinPowerWatts : (_appSettings.CpuMinAllowedPowerWatts > 0 ? _appSettings.CpuMinAllowedPowerWatts : 15);

            if (tdpW <= 0) tdpW = _capabilities.CpuTdpWatts > 0 ? _capabilities.CpuTdpWatts : (_appSettings.CpuThermalSpecTdpWatts > 0 ? _appSettings.CpuThermalSpecTdpWatts : 45);

            if (max1W <= 0) max1W = _capabilities.ScannedMaxPl1Watts > 0 ? _capabilities.ScannedMaxPl1Watts : (_appSettings.CpuMaxAllowedPl1Watts > 0 ? _appSettings.CpuMaxAllowedPl1Watts : 115);

            if (max2W <= 0) max2W = _capabilities.ScannedMaxPl2Watts > 0 ? _capabilities.ScannedMaxPl2Watts : (_appSettings.CpuMaxAllowedPl2Watts > 0 ? _appSettings.CpuMaxAllowedPl2Watts : 140);



            // Maintain valid min/max bounds without corruption

            if (minW < 5) minW = 5;

            if (tdpW < minW) tdpW = minW;

            if (max1W < minW) max1W = minW;

            if (max2W < max1W) max2W = max1W;



            _appSettings.CpuMinAllowedPowerWatts = minW;

            _appSettings.CpuMaxAllowedPl1Watts = max1W;

            _appSettings.CpuMaxAllowedPl2Watts = max2W;

            _appSettings.CpuThermalSpecTdpWatts = tdpW;

            _appSettings.HasCompletedBiosPowerScan = true;



            // Record authoritative mode defaults if provided by biosScan

            if (biosScan != null && biosScan.HasCompletedBiosPowerScan)

            {

                if (biosScan.BiosDefaultEcoPl1Watts > 0 && biosScan.BiosDefaultEcoPl2Watts > 0)

                    _appSettings.RecordBiosDefaultLimitsForMode(OperatingMode.Eco, biosScan.BiosDefaultEcoPl1Watts, biosScan.BiosDefaultEcoPl2Watts, isVerifiedBiosScan: true);

                if (biosScan.BiosDefaultQuietPl1Watts > 0 && biosScan.BiosDefaultQuietPl2Watts > 0)

                    _appSettings.RecordBiosDefaultLimitsForMode(OperatingMode.Quiet, biosScan.BiosDefaultQuietPl1Watts, biosScan.BiosDefaultQuietPl2Watts, isVerifiedBiosScan: true);

                if (biosScan.BiosDefaultBalancedPl1Watts > 0 && biosScan.BiosDefaultBalancedPl2Watts > 0)

                    _appSettings.RecordBiosDefaultLimitsForMode(OperatingMode.Balanced, biosScan.BiosDefaultBalancedPl1Watts, biosScan.BiosDefaultBalancedPl2Watts, isVerifiedBiosScan: true);

                if (biosScan.BiosDefaultPerformancePl1Watts > 0 && biosScan.BiosDefaultPerformancePl2Watts > 0)

                    _appSettings.RecordBiosDefaultLimitsForMode(OperatingMode.Performance, biosScan.BiosDefaultPerformancePl1Watts, biosScan.BiosDefaultPerformancePl2Watts, isVerifiedBiosScan: true);

                if (biosScan.BiosDefaultTurboPl1Watts > 0 && biosScan.BiosDefaultTurboPl2Watts > 0)

                    _appSettings.RecordBiosDefaultLimitsForMode(OperatingMode.Turbo, biosScan.BiosDefaultTurboPl1Watts, biosScan.BiosDefaultTurboPl2Watts, isVerifiedBiosScan: true);

            }

            _appSettings.Save();



            var scannedDefaults = new Dictionary<byte, (int Pl1, int Pl2)>

            {

                { 0x06, _appSettings.GetDefaultPowerLimitsForMode(OperatingMode.Eco) },

                { 0x00, _appSettings.GetDefaultPowerLimitsForMode(OperatingMode.Quiet) },

                { 0x01, _appSettings.GetDefaultPowerLimitsForMode(OperatingMode.Balanced) },

                { 0x04, _appSettings.GetDefaultPowerLimitsForMode(OperatingMode.Performance) },

                { 0x05, _appSettings.GetDefaultPowerLimitsForMode(OperatingMode.Turbo) }

            };



            var validatedScan = (biosScan != null && biosScan.HasCompletedBiosPowerScan)

                ? biosScan

                : (_capabilities.BiosPowerScan ?? biosScan ?? new BiosPowerScanResult());

            if (string.IsNullOrWhiteSpace(validatedScan.BiosHardwareFingerprint) && !string.IsNullOrWhiteSpace(_capabilities.BiosHardwareFingerprint))

            {

                validatedScan.BiosHardwareFingerprint = _capabilities.BiosHardwareFingerprint;

            }

            validatedScan.CpuMinAllowedPowerWatts = minW;

            validatedScan.CpuMaxAllowedPl1Watts = max1W;

            validatedScan.CpuMaxAllowedPl2Watts = max2W;

            validatedScan.CpuThermalSpecTdpWatts = tdpW;



            var (eco1, eco2) = scannedDefaults[0x06];

            var (quiet1, quiet2) = scannedDefaults[0x00];

            var (bal1, bal2) = scannedDefaults[0x01];

            var (perf1, perf2) = scannedDefaults[0x04];

            var (turbo1, turbo2) = scannedDefaults[0x05];



            if (validatedScan.BiosDefaultEcoPl1Watts <= 0) validatedScan.BiosDefaultEcoPl1Watts = eco1;

            if (validatedScan.BiosDefaultEcoPl2Watts <= 0) validatedScan.BiosDefaultEcoPl2Watts = eco2;

            if (validatedScan.BiosDefaultQuietPl1Watts <= 0) validatedScan.BiosDefaultQuietPl1Watts = quiet1;

            if (validatedScan.BiosDefaultQuietPl2Watts <= 0) validatedScan.BiosDefaultQuietPl2Watts = quiet2;

            if (validatedScan.BiosDefaultBalancedPl1Watts <= 0) validatedScan.BiosDefaultBalancedPl1Watts = bal1;

            if (validatedScan.BiosDefaultBalancedPl2Watts <= 0) validatedScan.BiosDefaultBalancedPl2Watts = bal2;

            if (validatedScan.BiosDefaultPerformancePl1Watts <= 0) validatedScan.BiosDefaultPerformancePl1Watts = perf1;

            if (validatedScan.BiosDefaultPerformancePl2Watts <= 0) validatedScan.BiosDefaultPerformancePl2Watts = perf2;

            if (validatedScan.BiosDefaultTurboPl1Watts <= 0) validatedScan.BiosDefaultTurboPl1Watts = turbo1;

            if (validatedScan.BiosDefaultTurboPl2Watts <= 0) validatedScan.BiosDefaultTurboPl2Watts = turbo2;



            _capabilities = _capabilities with

            {

                BiosPowerScan = validatedScan,

                BiosHardwareFingerprint = (!string.IsNullOrWhiteSpace(validatedScan.BiosHardwareFingerprint))

                    ? validatedScan.BiosHardwareFingerprint

                    : _capabilities.BiosHardwareFingerprint,

                PawnIoInstalled = pawnIoInstalled,

                PawnIoRequiresAdmin = pawnIoRequiresAdmin,

                PawnIoAvailable = pawnIoAvailable,

                PawnIoDiagnosticMessage = !string.IsNullOrWhiteSpace(powerCaps?.Diagnostics) ? powerCaps.Diagnostics : (scan?.DiagnosticMessage ?? ""),

                IsMsr0x610Locked = powerCaps?.IsLocked ?? false,

                CpuTdpWatts = tdpW,

                ScannedMinPowerWatts = minW,

                ScannedMaxPl1Watts = max1W,

                ScannedMaxPl2Watts = max2W,

                ScannedModeDefaults = scannedDefaults

            };



            byte curMode = _currentPowerMode;

            var curOpMode = ByteToOperatingMode(curMode);

            var (biosD1, biosD2, _) = _appSettings.GetBiosDefaultLimitsForMode(curOpMode);

            int curDefPl1 = biosD1 > 0 ? biosD1 : _capabilities.GetScannedPl1(curMode);

            int curDefPl2 = biosD2 > 0 ? biosD2 : _capabilities.GetScannedPl2(curMode);



            int minP = _appSettings.CpuMinAllowedPowerWatts > 0 ? _appSettings.CpuMinAllowedPowerWatts : minW;

            int maxP = _appSettings.CpuMaxAllowedPl2Watts > 0 ? _appSettings.CpuMaxAllowedPl2Watts : max2W;

            if (maxP < minP) maxP = minP;



            var (curPl1, curPl2) = GetSavedModePowerLimits(curMode, curDefPl1, curDefPl2);

            curPl1 = Math.Clamp(curPl1, minP, maxP);

            curPl2 = Math.Clamp(curPl2, curPl1, Math.Max(curPl1, maxP));



            _isUpdatingPowerLimitsUi = true;

            try

            {

                if (_sliderPl1 != null && _sliderPl2 != null)

                {

                    // First widen bounds: _sliderPl1.Minimum = 0; _sliderPl1.Maximum = 500; _sliderPl2.Minimum = 0; _sliderPl2.Maximum = 500;

                    _sliderPl1.Minimum = 0;

                    _sliderPl1.Maximum = 500;

                    _sliderPl2.Minimum = 0;

                    _sliderPl2.Maximum = 500;



                    _sliderPl1.Minimum = minP;

                    _sliderPl1.Maximum = maxP;

                    _sliderPl2.Minimum = minP;

                    _sliderPl2.Maximum = maxP;



                    // Then update .Value in safe order (PL2 first if increasing, PL1 first if decreasing) to the effective mode limits:

                    if (curPl2 >= _sliderPl1.Value)

                    {

                        _sliderPl2.Value = curPl2;

                        _sliderPl1.Value = curPl1;

                    }

                    else

                    {

                        _sliderPl1.Value = curPl1;

                        _sliderPl2.Value = curPl2;

                    }

                }



                if (_lblPowerBoundsSummary != null)

                {

                    _lblPowerBoundsSummary.Text = UiLabelFormatter.FormatHardwarePowerBoundsSummary(

                        _capabilities.ScannedMinPowerWatts,

                        _capabilities.ScannedMaxPl1Watts,

                        _capabilities.ScannedMaxPl2Watts,

                        _capabilities.CpuTdpWatts,

                        _capabilities.CpuModel);

                }



                if (_btnResetPowerLimits != null)

                {

                    _btnResetPowerLimits.Text = UiLabelFormatter.FormatResetButtonLabel(curDefPl1, curDefPl2);

                    _btnResetPowerLimits.Tag = $"{curDefPl1},{curDefPl2}";

                    bool isCustomized = (_sliderPl1 != null && _sliderPl2 != null) &&

                        ((_sliderPl1.Value != curDefPl1) || (_sliderPl2.Value != curDefPl2));

                    _btnResetPowerLimits.Enabled = isCustomized;

                }



                UpdatePowerLimitLabels();

                UpdatePowerLimitStatusUi();

            }

            finally

            {

                _isUpdatingPowerLimitsUi = false;

            }

        }



        private static OperatingMode ByteToOperatingMode(byte mode) => mode switch

        {

            0x00 => OperatingMode.Quiet,

            0x04 => OperatingMode.Performance,

            0x05 => OperatingMode.Turbo,

            0x06 => OperatingMode.Eco,

            _ => OperatingMode.Balanced

        };



        private static string GetPowerModeName(byte mode) => mode switch

        {

            0x00 => "Quiet",

            0x04 => "Performance",

            0x05 => "Turbo",

            0x06 => "Eco",

            _ => "Balanced"

        };



        #endregion



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

            _lblAcFanHdr = MakeLabel(cardFanModes, "ON AC POWER:", cPad, S(116), FontBody, SubHeaderColor);

            _lblBatteryFanHdr = MakeLabel(cardFanModes, "ON BATTERY:", cPad + dropW + S(10), S(116), FontBody, SubHeaderColor);



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

                _ = _fanCommandQueue.Enqueue(FanCommandKind.Mode, () =>

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

            _lblCpuFanSpeedHdr = MakeLabel(cardCustomFans, "CPU FAN: 50%", cPad, S(88), FontBody, SubHeaderColor);

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

                if (_isUpdatingFanUi) return;

                int val = _cpuFanSlider.Value;

                _ = _fanCommandQueue.Enqueue(FanCommandKind.Cpu, () => _wmi.SetCpuFanSpeed((byte)val));

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



            _lblGpuFanSpeedHdr = MakeLabel(cardCustomFans, "GPU FAN: 50%", cPad, S(148), FontBody, SubHeaderColor);

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

                if (_isUpdatingFanUi) return;

                int val = _gpuFanSlider.Value;

                _ = _fanCommandQueue.Enqueue(FanCommandKind.Gpu, () => _wmi.SetGpuFanSpeed((byte)val));

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

                _lblSysFanSpeedHdr = MakeLabel(cardCustomFans, "SYSTEM FAN: 50%", cPad, S(208), FontBody, SubHeaderColor);

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

                    if (_isUpdatingFanUi) return;

                    int val = _sysFanSlider.Value;

                    _ = _fanCommandQueue.Enqueue(FanCommandKind.System, () => _wmi.SetSystemFanSpeed((byte)val));

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

            MakeLabel(cardCoolingTech, "FACTORY FAN PRESET (SPEED TABLE):", cPad, S(40), FontBody, SubHeaderColor);

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



            // Card 3b: thermal guard (staged ceiling optimized for this app).

            int thermalCardY = techCardY + S(116) + S(12);

            int thermalCardH = S(304);

            var cardThermal = MakeCard(_pnlFans, "THERMAL GUARD", col1X, thermalCardY, cardW, thermalCardH);

            MakeLabel(cardThermal, "STAGED FAN FLOOR + CPU WATT CAP", cPad, S(40), FontHeaderLight, SubHeaderColor);



            _switchThermalGuard = new PredatorSwitch { Location = new Point(cPad, S(62)), Size = new Size(S(48), S(24)) };

            _switchThermalGuard.Checked = _appSettings.ToThermalProfile().Enabled;

            _switchThermalGuard.CheckedChanged += (s, e) =>

            {

                if (_isInitializing) return;

                _appSettings.AntiThrottleEnabled = _switchThermalGuard.Checked;

                ApplyThermalSettingsFromUi();

            };

            cardThermal.Controls.Add(_switchThermalGuard);

            MakeLabel(cardThermal, "Enable thermal guard", cPad + S(58), S(64), FontBody, ThemeManager.TextPrimary);



            _lblThermalStage = MakeLabel(cardThermal, "STAGE: NORMAL", cPad, S(96), FontBodyBold, SubHeaderColor);

            _lblThermalDetail = MakeLabel(cardThermal, string.Empty, cPad, S(118), FontHeaderLight, SubHeaderColor);

            _lblThermalDetail.AutoSize = false;

            _lblThermalDetail.Size = new Size(cInnerW, S(42));



            _lblThermalCeilingHdr = MakeLabel(cardThermal, "CPU CEILING: 95 C", cPad, S(162), FontHeaderLight, SubHeaderColor);

            _sliderThermalCeiling = new PredatorSlider { Location = new Point(cPad, S(184)), Size = new Size(cInnerW, S(26)), Minimum = 80, Maximum = 100, Value = 95 };

            _sliderThermalCeiling.ValueChanged += (s, e) =>

            {

                if (_lblThermalCeilingHdr != null)

                    _lblThermalCeilingHdr.Text = $"CPU CEILING: {_sliderThermalCeiling.Value} C";

            };

            _sliderThermalCeiling.ValueCommitted += (s, e) => ApplyThermalSettingsFromUi();

            cardThermal.Controls.Add(_sliderThermalCeiling);



            _lblThermalHysteresisHdr = MakeLabel(cardThermal, "RELEASE HYSTERESIS: 3 C", cPad, S(214), FontHeaderLight, SubHeaderColor);

            _sliderThermalHysteresis = new PredatorSlider { Location = new Point(cPad, S(236)), Size = new Size(cInnerW, S(26)), Minimum = 0, Maximum = 10, Value = 3 };

            _sliderThermalHysteresis.ValueChanged += (s, e) =>

            {

                if (_lblThermalHysteresisHdr != null)

                    _lblThermalHysteresisHdr.Text = $"RELEASE HYSTERESIS: {_sliderThermalHysteresis.Value} C";

            };

            _sliderThermalHysteresis.ValueCommitted += (s, e) => ApplyThermalSettingsFromUi();

            cardThermal.Controls.Add(_sliderThermalHysteresis);



            var thermalNote = MakeLabel(cardThermal, "The guard never shuts the machine down. It raises the fan floor and lowers the CPU watt cap in stages.", cPad, S(266), FontHeaderLight, SubHeaderColor);

            thermalNote.AutoSize = false;

            thermalNote.Size = new Size(cInnerW, S(40));



            // Card 4 (Right Column): Interactive Fan Curves

            var cardCurves = MakeCard(_pnlFans, "INTERACTIVE FAN CURVES", col2X, pad, cardW, S(640));

            MakeLabel(cardCurves, "Drag points (Temp °C vs Speed %) to customize hardware response curves.", cPad, S(38), FontHeaderLight, SubHeaderColor);



            int curveH = S(195);

            _embeddedCurveCpu = new FanCurveGraph

            {

                CurveColor = Color.FromArgb(0, 180, 255),

                FanLabel = "CPU FAN CURVE",

                Location = new Point(cPad, S(60)),

                Size = new Size(cInnerW, curveH),

                Points = _cpuCurvePoints

            };

            _embeddedCurveCpu.DoubleClick += (s, e) => OpenFanCurveEditor();

            cardCurves.Controls.Add(_embeddedCurveCpu);



            _embeddedCurveGpu = new FanCurveGraph

            {

                CurveColor = Color.FromArgb(255, 77, 109),

                FanLabel = "GPU FAN CURVE",

                Location = new Point(cPad, S(260)),

                Size = new Size(cInnerW, curveH),

                Points = _gpuCurvePoints

            };

            _embeddedCurveGpu.DoubleClick += (s, e) => OpenFanCurveEditor();

            cardCurves.Controls.Add(_embeddedCurveGpu);



            _lblEmbeddedHysteresis = MakeLabel(cardCurves, UiLabelFormatter.FormatHysteresisLabel(_appSettings.FanHysteresisDegrees), cPad, S(460), FontBodyBold, AccentColor, "accent");

            _sliderEmbeddedHysteresis = new PredatorSlider

            {

                Minimum = CurveFollower.MinHysteresisDegrees,

                Maximum = CurveFollower.MaxHysteresisDegrees,

                Value = Math.Clamp(_appSettings.FanHysteresisDegrees, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees),

                Location = new Point(cPad, S(482)),

                Size = new Size(cInnerW, S(26))

            };

            _sliderEmbeddedHysteresis.ValueChanged += (s, e) =>

            {

                int val = _sliderEmbeddedHysteresis.Value;

                if (_lblEmbeddedHysteresis != null)

                    _lblEmbeddedHysteresis.Text = UiLabelFormatter.FormatHysteresisLabel(val);

                _appSettings.FanHysteresisDegrees = val;

                _cpuCurveFollower.HysteresisDegrees = val;

                _gpuCurveFollower.HysteresisDegrees = val;

                _sysCurveFollower.HysteresisDegrees = val;

            };

            _sliderEmbeddedHysteresis.ValueCommitted += (s, e) =>

            {

                int val = _sliderEmbeddedHysteresis.Value;

                _appSettings.FanHysteresisDegrees = val;

                _appSettings.Save();

                SaveState("FanHysteresisDegrees", val);

                SaveStateToHklm("FanHysteresisDegrees", val);

            };

            cardCurves.Controls.Add(_sliderEmbeddedHysteresis);



            int curveBtnW = (cInnerW - S(10)) / 2;

            _btnEmbeddedResetCurve = MakeButton(cardCurves, "↺  Reset Defaults", cPad, S(516), curveBtnW, S(34));

            _btnEmbeddedApplyCurve = MakeButton(cardCurves, "✓  Apply Curves", cPad + curveBtnW + S(10), S(516), curveBtnW, S(34));



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

                if (_sliderEmbeddedHysteresis != null)

                {

                    _sliderEmbeddedHysteresis.Value = CurveFollower.DefaultHysteresisDegrees;

                }

                if (_lblEmbeddedHysteresis != null)

                {

                    _lblEmbeddedHysteresis.Text = UiLabelFormatter.FormatHysteresisLabel(CurveFollower.DefaultHysteresisDegrees);

                }

                _appSettings.FanHysteresisDegrees = CurveFollower.DefaultHysteresisDegrees;

                _cpuCurveFollower.HysteresisDegrees = CurveFollower.DefaultHysteresisDegrees;

                _gpuCurveFollower.HysteresisDegrees = CurveFollower.DefaultHysteresisDegrees;

                _sysCurveFollower.HysteresisDegrees = CurveFollower.DefaultHysteresisDegrees;

                _appSettings.Save();

                SaveState("FanHysteresisDegrees", CurveFollower.DefaultHysteresisDegrees);

                SaveStateToHklm("FanHysteresisDegrees", CurveFollower.DefaultHysteresisDegrees);



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

                if (_sliderEmbeddedHysteresis != null)

                {

                    int val = _sliderEmbeddedHysteresis.Value;

                    _appSettings.FanHysteresisDegrees = val;

                    _cpuCurveFollower.HysteresisDegrees = val;

                    _gpuCurveFollower.HysteresisDegrees = val;

                    _sysCurveFollower.HysteresisDegrees = val;

                    _appSettings.Save();

                    SaveState("FanHysteresisDegrees", val);

                    SaveStateToHklm("FanHysteresisDegrees", val);

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



            MakeLabel(cardCurves, "💡 Curves apply in real time when fan profile is set to Custom > Dynamic Curve.", cPad, S(558), FontHeaderLight, SubHeaderColor);



            int totalH = Math.Max(Math.Max(techCardY + S(126), thermalCardY + thermalCardH + S(12)), S(660));

            _pnlFans.AutoScrollMinSize = new Size(0, totalH + S(20));

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



            // Capability banner: filled by the BIOS/service probe, never assumed.

            _lblLightingCapability = MakeLabel(_pnlLighting, "KEYBOARD: detecting capabilities...", pad, pad, FontBody, SubHeaderColor);

            _lblLightingCapability.AutoSize = false;

            _lblLightingCapability.AutoEllipsis = true;

            _lblLightingCapability.Size = new Size(usableW, S(20));



            int topY = pad + S(26);

            int topH = S(300);



            // Card 1: dynamic animated effects and dynamic color tint (static lighting is in Zone Studio below).
            var cardRgb = MakeCard(_pnlLighting, "LIGHTING EFFECTS", col1X, topY, cardW, topH);
            MakeLabel(cardRgb, "DYNAMIC EFFECT:", cPad, S(44), FontBody, SubHeaderColor);

            _rgbDropDown = new PredatorDropDown { Location = new Point(cPad, S(66)), Size = new Size(cInnerW, S(34)) };
            _rgbDropDown.Items.Add("Off");
            for (int i = 1; i < RgbModeNames.Length; i++)
            {
                _rgbDropDown.Items.Add(RgbModeNames[i]);
            }
            _rgbDropDown.SelectedIndex = 0; // Default to Off (Static)

            _rgbDropDown.SelectedIndexChanged += (s, e) =>
            {
                if (_isInitializingLightingUi || _isInitializing || _suppressUiEvents || _isGameSyncOverriding || _isApplyingRgbMode) return;
                if (_rgbDropDown.SelectedIndex < 0) return;
                int mode = _rgbDropDown.SelectedIndex;
                if (mode == 0)
                {
                    LightingEffectsManager.StopSoftwareAnimation();
                    SetZonePickersEnabled(true);
                    _appSettings.DynamicLightingEffectName = "Off";
                    _appSettings.IsZoneLightingActive = true;
                    _appSettings.ActiveLightingPreset = "Custom";
                    _appSettings.Save();
                    ApplyStaticZoneColorsToHardware();
                    UpdateRgbControls(0);
                    CheckRgbTrayFromMode(0);
                }
                else
                {
                    string effectName = _rgbDropDown.Items[mode]?.ToString() ?? RgbModeNames[mode];
                    _appSettings.DynamicLightingEffectName = effectName;
                    _appSettings.IsZoneLightingActive = false;
                    _appSettings.ActiveLightingPreset = "Dynamic";
                    _appSettings.Save();
                    ApplyRgbModeFromDropdown(mode);
                }
            };
            cardRgb.Controls.Add(_rgbDropDown);

            MakeLabel(cardRgb, "DYNAMIC COLOR / TINT:", cPad, S(108), FontBody, SubHeaderColor);

            int tintSwatchSize = S(24);
            int tintSwatchGap = S(6);
            _dynamicTintButtons.Clear();
            int tintCount = CuratedZonePalette.Length;
            for (int i = 0; i < tintCount; i++)
            {
                Color tintColor = CuratedZonePalette[i].Color;
                var tBtn = new PredatorButton
                {
                    Text = "",
                    Location = new Point(cPad + i * (tintSwatchSize + tintSwatchGap), S(132)),
                    Size = new Size(tintSwatchSize, tintSwatchSize),
                    Cursor = Cursors.Hand
                };
                tBtn.Paint += (s, e) =>
                {
                    if (tBtn.Width <= 7 || tBtn.Height <= 7) return;
                    var g = e.Graphics;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    bool isCur = (tintColor.R == _colorPicker.Color.R && tintColor.G == _colorPicker.Color.G && tintColor.B == _colorPicker.Color.B);
                    if (isCur)
                    {
                        using var pen = new Pen(ThemeManager.Accent, 2.5f);
                        g.DrawEllipse(pen, 1, 1, tBtn.Width - 3, tBtn.Height - 3);
                    }
                    else
                    {
                        using var pen = new Pen(ThemeManager.ControlBorder, 1f);
                        g.DrawEllipse(pen, 2, 2, tBtn.Width - 5, tBtn.Height - 5);
                    }
                    using var brush = new SolidBrush(tintColor);
                    g.FillEllipse(brush, 3, 3, tBtn.Width - 7, tBtn.Height - 7);
                };
                tBtn.Click += (s, e) =>
                {
                    if (_isInitializingLightingUi) return;
                    _colorPicker.Color = tintColor;
                    _appSettings.DynamicEffectPrimaryColorArgb = tintColor.ToArgb();
                    _appSettings.Save();
                    foreach (var b in _dynamicTintButtons) b.Invalidate();
                    _btnColorPick?.Invalidate();
                    SaveState("RGB_R", tintColor.R);
                    SaveState("RGB_G", tintColor.G);
                    SaveState("RGB_B", tintColor.B);
                    int currentDynamicMode = _rgbDropDown.SelectedIndex;
                    if (currentDynamicMode > 0)
                    {
                        ApplyRgbModeFromDropdown(currentDynamicMode);
                    }
                };
                cardRgb.Controls.Add(tBtn);
                _dynamicTintButtons.Add(tBtn);
            }

            int customBtnX = cPad + tintCount * (tintSwatchSize + tintSwatchGap) + S(8);
            int customBtnW = Math.Max(S(90), cInnerW - (customBtnX - cPad));
            _btnColorPick = MakeButton(cardRgb, "Custom...", customBtnX, S(128), customBtnW, S(32));
            _btnColorPick.Paint += (s, e) =>
            {
                if (_btnColorPick.Width <= S(16) || _btnColorPick.Height <= S(16)) return;
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int cy = _btnColorPick.Height / 2;
                Color dotColor = _colorPicker.Color;
                using var glowBrush = new SolidBrush(Color.FromArgb(100, dotColor.R, dotColor.G, dotColor.B));
                g.FillEllipse(glowBrush, S(12) - S(5), cy - S(5), S(10), S(10));
                using var brush = new SolidBrush(dotColor);
                g.FillEllipse(brush, S(12) - S(3), cy - S(3), S(6), S(6));
            };
            _btnColorPick.Click += (s, e) =>
            {
                if (_isInitializingLightingUi) return;
                ShowColorDialog(_colorPicker.Color, c =>
                {
                    _colorPicker.Color = c;
                    _appSettings.DynamicEffectPrimaryColorArgb = c.ToArgb();
                    _appSettings.Save();
                    foreach (var b in _dynamicTintButtons) b.Invalidate();
                    _btnColorPick.Invalidate();
                    SaveState("RGB_R", c.R);
                    SaveState("RGB_G", c.G);
                    SaveState("RGB_B", c.B);
                    int currentDynamicMode = _rgbDropDown.SelectedIndex;
                    if (currentDynamicMode > 0)
                    {
                        ApplyRgbModeFromDropdown(currentDynamicMode);
                    }
                });
            };

            var effectHint = MakeLabel(cardRgb, "Animated dynamic effects with custom color tint. To configure static lighting or custom colors per zone, use Keyboard RGB & Zone Studio below.", cPad, S(184), FontHeaderLight, SubHeaderColor);
            effectHint.AutoSize = false;
            effectHint.Size = new Size(cInnerW, S(70));



            // Card 2: brightness, speed, status, capability override.

            var cardParams = MakeCard(_pnlLighting, "LIGHTING SETTINGS", col2X, topY, cardW, topH);

            _lblBrightHdr = MakeLabel(cardParams, $"BRIGHTNESS: {_appSettings.KeyboardBrightnessLevel}%", cPad, S(44), FontBody, SubHeaderColor);

            _brightnessSlider = new PredatorSlider { Location = new Point(cPad, S(66)), Size = new Size(cInnerW, S(28)), Minimum = 0, Maximum = 100, Value = _appSettings.KeyboardBrightnessLevel };

            _brightnessSlider.ValueChanged += (s, e) =>

            {
                if (_isInitializingLightingUi) return;

                if (IsOnBattery() && _brightnessSlider.Value == 0 && !_manualBacklightOnBattery)

                    _lblBrightHdr.Text = "BRIGHTNESS: Off (Battery Saver)";

                else

                    _lblBrightHdr.Text = $"BRIGHTNESS: {_brightnessSlider.Value}%";

            };

            _brightnessSlider.ValueCommitted += (s, e) =>
            {
                if (_isInitializingLightingUi) return;
                int bVal = _brightnessSlider.Value;
                _appSettings.KeyboardBrightnessLevel = bVal;
                _appSettings.RgbBrightness = bVal;
                SaveState("Brightness", bVal);
                SaveState("KeyboardBrightnessLevel", bVal);
                _appSettings.Save();
                _backlightMgr.SavedAcBrightness = bVal;
                if (_lightingController != null)
                {
                    _lightingController.Brightness = bVal;
                }

                bool onBat = IsOnBattery();
                if (onBat)
                {
                    _manualBacklightOnBattery = bVal > 0;
                    _backlightMgr.OnUserAdjustedBrightness(bVal, true);
                }
                else
                {
                    _backlightMgr.OnUserAdjustedBrightness(bVal, false);
                }

                int gen = Interlocked.Increment(ref _brightnessApplyGeneration);

                if (bVal == 0)
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            if (gen != Volatile.Read(ref _brightnessApplyGeneration)) return;
                            lock (_brightnessApplyLock)
                            {
                                if (gen != Volatile.Read(ref _brightnessApplyGeneration)) return;
                                _wmi.TurnOffBacklight();
                            }
                        }
                        catch { }
                    });
                    if (onBat) _lblBrightHdr.Text = "BRIGHTNESS: Off (Battery Saver)";
                }
                else
                {
                    int currentDynamicMode = (_rgbDropDown != null && _rgbDropDown.SelectedIndex >= 0)
                        ? _rgbDropDown.SelectedIndex
                        : 0;

                    if (currentDynamicMode == 0 || string.Equals(_appSettings.DynamicLightingEffectName, "Off", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_lightingController != null)
                        {
                            Task.Run(() =>
                            {
                                try
                                {
                                    if (gen != Volatile.Read(ref _brightnessApplyGeneration)) return;
                                    lock (_brightnessApplyLock)
                                    {
                                        if (gen != Volatile.Read(ref _brightnessApplyGeneration)) return;
                                        _wmi.SetBrightness((byte)bVal, applyMode: false);
                                        _lightingController.Brightness = bVal;
                                        _lightingController.QueueApply(KeyboardLightingScope.Zones, 0);
                                    }
                                }
                                catch { }
                            });
                        }
                    }
                    else
                    {
                        int dynMode = currentDynamicMode > 0 ? currentDynamicMode : (_wmi.LastMode > 0 ? _wmi.LastMode : 3);
                        byte spd = GetMappedSpeed();
                        Color c = Color.FromArgb(_appSettings.DynamicEffectPrimaryColorArgb);
                        Task.Run(() =>
                        {
                            try
                            {
                                if (gen != Volatile.Read(ref _brightnessApplyGeneration)) return;
                                lock (_brightnessApplyLock)
                                {
                                    if (gen != Volatile.Read(ref _brightnessApplyGeneration)) return;
                                    LightingEffectsManager.ApplyEffect(dynMode, _wmi, c.R, c.G, c.B, (byte)bVal, spd, 0, (OperatingMode)_currentPowerMode);
                                }
                            }
                            catch { }
                        });
                    }
                }
            };

            cardParams.Controls.Add(_brightnessSlider);



            _lblSpeedHdr = MakeLabel(cardParams, "EFFECT SPEED: 50%", cPad, S(102), FontBody, SubHeaderColor);

            _speedSlider = new PredatorSlider { Location = new Point(cPad, S(124)), Size = new Size(cInnerW, S(28)), Minimum = 1, Maximum = 100, Value = 50 };

            _speedSlider.ValueChanged += (s, e) =>
            {
                if (_isInitializingLightingUi) return;
                _lblSpeedHdr.Text = $"EFFECT SPEED: {_speedSlider.Value}%";
            };

            _speedSlider.ValueCommitted += (s, e) =>

            {
                if (_isInitializingLightingUi) return;

                byte spd = GetMappedSpeed();

                Task.Run(() => _wmi.SetSpeed(spd));

                if (_lightingController != null) _lightingController.Speed = _speedSlider.Value;

                SaveState("RGB_Speed", _speedSlider.Value);

            };

            cardParams.Controls.Add(_speedSlider);



            _lblLightingStatus = MakeLabel(cardParams, "Ready.", cPad, S(158), FontHeaderLight, SubHeaderColor);

            _lblLightingStatus.AutoSize = false;

            _lblLightingStatus.Size = new Size(cInnerW, S(46));



            _cboLightingTier = new PredatorDropDown { Location = new Point(cPad, S(210)), Size = new Size(cInnerW, S(32)) };

            _cboLightingTier.Items.AddRange(new[]

            {

                "Auto (BIOS + Acer service)",

                "Force 4-zone",

                "Force per-key (experimental)",

                "Force 1-zone (monozone)",

                "Force 3-zone"

            });

            _cboLightingTier.SelectedIndex = Math.Clamp(_appSettings.KeyboardLightingTierOverride, 0, 4);

            _cboLightingTier.SelectedIndexChanged += (s, e) =>

            {

                if (_suppressLightingTierChange) return;

                _appSettings.KeyboardLightingTierOverride = Math.Clamp(_cboLightingTier.SelectedIndex, 0, 4);

                _appSettings.Save();

                BeginKeyboardCapabilityDetection(force: true);

            };

            cardParams.Controls.Add(_cboLightingTier);



            var btnDetect = MakeButton(cardParams, "Re-detect Keyboard Hardware", cPad, S(250), cInnerW, S(32));

            btnDetect.Click += (s, e) => BeginKeyboardCapabilityDetection(force: true);



            int zoneGap = S(10);

            // Card 3: Keyboard RGB Visualizer & Zone Studio (Supports 1-Zone, 3-Zone, 4-Zone, and Per-Key)

            int zoneY = topY + topH + gap;

            int zoneH = S(336);

            var cardZones = MakeCard(_pnlLighting, "KEYBOARD RGB & ZONE STUDIO", col1X, zoneY, usableW, zoneH);

            MakeLabel(cardZones, "DYNAMIC MULTI-ZONE RGB CONTROL. WORKS ON ACER 4-ZONE, 3-ZONE, AND SINGLE-ZONE TRANSPORTS.", cPad, S(40), FontHeaderLight, SubHeaderColor);



            // Layout Segmented Pill Selector & BIOS Auto-detect badge

            int pillW = S(84);

            int pillGap = S(6);

            string[] layoutLabels = { "1-Zone", "3-Zone", "4-Zone", "Per-Key" };

            int[] layoutModes = { 1, 3, 4, 0 };



            for (int i = 0; i < layoutModes.Length; i++)

            {

                int targetMode = layoutModes[i];

                var pillBtn = new PredatorButton

                {

                    Text = layoutLabels[i],

                    Location = new Point(cPad + i * (pillW + pillGap), S(64)),

                    Size = new Size(pillW, S(30)),

                    Cursor = Cursors.Hand

                };

                pillBtn.Click += (s, e) => { if (_isInitializingLightingUi) return; SetZoneLayout(targetMode, userInitiated: true); };

                cardZones.Controls.Add(pillBtn);

                _layoutPillButtons[i] = pillBtn;

            }



            _lblBiosDetectedBadge = MakeLabel(cardZones, _detectedBiosLayoutMode == 0 ? "✨ (Auto-detected from BIOS: Per-Key RGB)" : "✨ (Auto-detected from BIOS: 4-Zone RGB)",

                cPad + 4 * (pillW + pillGap) + S(10), S(70), FontHeaderLight, ThemeManager.TealAccent);



            // Sector visualizer container (dynamically renders 1, 3, or 4 sectors)

            _pnlSectorVisualizer = new Panel

            {

                Location = new Point(cPad, S(102)),

                Size = new Size(cInnerW, S(82)),

                BackColor = Color.Transparent,

                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right

            };

            _pnlSectorVisualizer.Resize += (s, e) =>

            {

                if (!_isClosing && !IsDisposed && _pnlSectorVisualizer.Width > 0)

                {

                    SetZoneLayout(_zoneLayoutMode, userInitiated: false);

                }

            };

            cardZones.Controls.Add(_pnlSectorVisualizer);



            _lblPerKeyPrompt = MakeLabel(cardZones, "⌨️  PER-KEY MATRIX MODE: Click or drag across individual keys on the map below. Quick Palette swatches set the active paint color.",

                cPad, S(118), FontHeaderLight, AccentColor, "accent");

            _lblPerKeyPrompt.Visible = false;



            for (int i = 0; i < 4; i++)

            {

                int zi = i;

                var btn = new PredatorButton

                {

                    Text = "",

                    Cursor = Cursors.Hand

                };

                btn.Paint += (s, e) => PaintZoneButton(btn, zi, e.Graphics);

                btn.Click += (s, e) =>

                {
                    if (_isInitializingLightingUi) return;

                    _selectedZoneIndex = zi;

                    RefreshZoneButtons();

                };

                btn.DoubleClick += (s, e) => { if (_isInitializingLightingUi) return; PickZoneColor(zi); };

                btn.MouseDown += (s, e) =>

                {
                    if (_isInitializingLightingUi) return;

                    if (e.Button == MouseButtons.Right)

                    {

                        _selectedZoneIndex = zi;

                        RefreshZoneButtons();

                        PickZoneColor(zi);

                    }

                };

                _zoneButtons[i] = btn;

                _pnlSectorVisualizer.Controls.Add(btn);

            }



            // Quick Color Palette

            MakeLabel(cardZones, "QUICK COLOR PALETTE (CLICK SWATCH TO APPLY):", cPad, S(194), FontBody, SubHeaderColor);



            int zoneSwatchSize = S(26);

            int zoneSwatchGap = S(8);

            _zonePaletteButtons.Clear();

            for (int i = 0; i < CuratedZonePalette.Length; i++)

            {

                Color swatchColor = CuratedZonePalette[i].Color;

                var sBtn = new PredatorButton

                {

                    Text = "",

                    Location = new Point(cPad + i * (zoneSwatchSize + zoneSwatchGap), S(216)),

                    Size = new Size(zoneSwatchSize, zoneSwatchSize),

                    Cursor = Cursors.Hand

                };

                sBtn.Paint += (s, e) =>

                {
                    if (sBtn.Width <= 7 || sBtn.Height <= 7) return;

                    var g = e.Graphics;

                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    bool isCur = (swatchColor.ToArgb() == _activePaintColor.ToArgb());

                    if (isCur)

                    {

                        using var pen = new Pen(ThemeManager.Accent, 2.5f);

                        g.DrawEllipse(pen, 1, 1, sBtn.Width - 3, sBtn.Height - 3);

                    }

                    else

                    {

                        using var pen = new Pen(ThemeManager.ControlBorder, 1f);

                        g.DrawEllipse(pen, 2, 2, sBtn.Width - 5, sBtn.Height - 5);

                    }

                    using var brush = new SolidBrush(swatchColor);

                    g.FillEllipse(brush, 3, 3, sBtn.Width - 7, sBtn.Height - 7);

                };

                sBtn.Click += (s, e) =>
                {
                    if (_isInitializingLightingUi) return;
                    _appSettings.IsZoneLightingActive = true;
                    _appSettings.ActiveLightingPreset = "Custom";
                    _appSettings.Save();
                    ApplyZoneQuickColor(swatchColor);
                };

                cardZones.Controls.Add(sBtn);

                _zonePaletteButtons.Add(sBtn);

            }



            _btnSyncAll = MakeButton(cardZones, "🔗 Sync All", cPad + CuratedZonePalette.Length * (zoneSwatchSize + zoneSwatchGap) + S(12), S(214), S(110), S(30));

            _btnSyncAll.Click += (s, e) =>

            {
                if (_isInitializingLightingUi) return;

                _syncAllZones = !_syncAllZones;

                _btnSyncAll.Text = _syncAllZones ? "✓ Sync All (ON)" : "🔗 Sync All";

                _btnSyncAll.IsActive = _syncAllZones;

                _btnSyncAll.CustomActiveColor = _syncAllZones ? ThemeManager.Accent : null;

                _btnSyncAll.Invalidate();

            };



            // Theme Presets & Action Bar

            MakeLabel(cardZones, "THEME PRESETS & QUICK TOOLS:", cPad, S(256), FontBody, SubHeaderColor);



            int actGap = S(8);

            int actBtnW = (cInnerW - 5 * actGap) / 6;

            int actY = S(278);

            int actH = S(34);



            _btnPresetCyberpunk = MakeButton(cardZones, "Cyberpunk", cPad, actY, actBtnW, actH);

            _btnPresetCyberpunk.Click += (s, e) => { if (_isInitializingLightingUi) return; ApplyZonePreset(new[]

            {

                Color.FromArgb(0x00, 0xE5, 0xFF), // Cyan

                Color.FromArgb(0xFF, 0x40, 0x81), // Magenta

                Color.FromArgb(0x7C, 0x4D, 0xFF), // Purple

                Color.FromArgb(0x29, 0x79, 0xFF)  // Blue

            }, "Cyberpunk"); };



            _btnPresetFirestorm = MakeButton(cardZones, "Firestorm", cPad + (actBtnW + actGap), actY, actBtnW, actH);

            _btnPresetFirestorm.Click += (s, e) => { if (_isInitializingLightingUi) return; ApplyZonePreset(new[]

            {

                Color.FromArgb(0xFF, 0x17, 0x44), // Red

                Color.FromArgb(0xFF, 0x91, 0x00), // Orange

                Color.FromArgb(0xFF, 0xEA, 0x00), // Gold

                Color.FromArgb(0xD5, 0x00, 0x00)  // Crimson

            }, "Firestorm"); };



            _btnPresetIce = MakeButton(cardZones, "Predator Ice", cPad + (actBtnW + actGap) * 2, actY, actBtnW, actH);

            _btnPresetIce.Click += (s, e) => { if (_isInitializingLightingUi) return; ApplyZonePreset(new[]

            {

                Color.FromArgb(0x29, 0x79, 0xFF), // Deep Blue

                Color.FromArgb(0x00, 0xE5, 0xFF), // Cyan

                Color.FromArgb(0xFF, 0xFF, 0xFF), // White

                Color.FromArgb(0x00, 0xE5, 0xFF)  // Cyan

            }, "Predator Ice"); };



            _btnPresetOff = MakeButton(cardZones, "All Off", cPad + (actBtnW + actGap) * 3, actY, actBtnW, actH);


            _btnPresetOff.Click += (s, e) =>

            {
                if (_isInitializingLightingUi) return;

                _appSettings.IsZoneLightingActive = true;
                _appSettings.ActiveLightingPreset = "Off";
                _appSettings.KeyboardZoneLayoutMode = _zoneLayoutMode;

                if (_lightingController == null) return;

                _lightingController.SetAllZones(Color.Black);

                if (_zoneLayoutMode == 0 && _perKeyMap != null)

                {

                    _lightingController.SetAllKeys(Color.Black);

                    _perKeyMap.FillAll(Color.Black);

                }

                PersistLightingState(immediate: true);

                RefreshZoneButtons();

                InvalidateZoneSwatches();

                InvalidatePerKeySwatches();

                UpdatePresetButtonsHighlight();

                _lightingController.Effect = "STATIC";

                _lightingController.QueueApply(_zoneLayoutMode == 0 ? KeyboardLightingScope.PerKey : KeyboardLightingScope.Zones, 0);

            };



            _btnPresetCustom = MakeButton(cardZones, "Custom...", cPad + (actBtnW + actGap) * 4, actY, actBtnW, actH);


            _btnPresetCustom.Click += (s, e) =>
            {
                if (_isInitializingLightingUi) return;
                _appSettings.IsZoneLightingActive = true;
                _appSettings.ActiveLightingPreset = "Custom";
                _appSettings.KeyboardZoneLayoutMode = _zoneLayoutMode;
                UpdatePresetButtonsHighlight();
                PickZoneColor(_syncAllZones ? -1 : _selectedZoneIndex);
            };



            _btnZonesApply = MakeButton(cardZones, "Apply Now", cPad + (actBtnW + actGap) * 5, actY, actBtnW, actH);

            _btnZonesApply.Click += (s, e) =>

            {

                _appSettings.IsZoneLightingActive = true;
                _appSettings.Save();
                if (_lightingController == null) return;

                PersistLightingState();

                _lightingController.QueueApply(_zoneLayoutMode == 0 ? KeyboardLightingScope.PerKey : KeyboardLightingScope.Zones, 0);

                _btnZonesApply.CustomActiveColor = Color.FromArgb(0, 200, 100);

                _btnZonesApply.IsActive = true;

                Task.Delay(1000).ContinueWith(_ => SafeBeginInvoke(() =>

                {

                    if (_btnZonesApply != null && !_btnZonesApply.IsDisposed)

                    {

                        _btnZonesApply.IsActive = false;

                        _btnZonesApply.CustomActiveColor = null;

                        _btnZonesApply.Invalidate();

                    }

                }));

            };



            SetZoneLayout(_appSettings.KeyboardZoneLayoutMode, userInitiated: false);



            // Card 4: per-key map (experimental, capability gated).

            int keyY = zoneY + zoneH + gap;

            int keyH = S(396);

            var cardKeys = MakeCard(_pnlLighting, "PER-KEY MAP (EXPERIMENTAL)", col1X, keyY, usableW, keyH);

            var keyHint = MakeLabel(cardKeys, "Enabled only when the BIOS identity and the Acer Lighting Service both confirm per-key support. A write the service does not acknowledge falls back to the four zones above.", cPad, S(40), FontHeaderLight, SubHeaderColor);

            keyHint.AutoSize = false;

            keyHint.Size = new Size(usableW - cPad * 2, S(34));



            var profile = AcerLaptopModelCatalog.Lookup(_appSettings.KeyboardCapabilityModel);

            string layoutId = profile?.HasFullKeyboardWithNumpad == false ? KeyboardLayouts.AnsiTklId : KeyboardLayouts.FullAnsiId;

            _perKeyMap = new KeyboardRgbMap(layoutId)

            {

                Location = new Point(cPad, S(78)),

                Size = new Size(usableW - cPad * 2, S(196))

            };

            _perKeyMap.KeyPainted += OnPerKeyPainted;

            cardKeys.Controls.Add(_perKeyMap);



            MakeLabel(cardKeys, "PAINT COLOR:", cPad, S(284), FontBody, SubHeaderColor);

            Color[] palette =

            {

                Color.FromArgb(255, 60, 60),

                Color.FromArgb(255, 150, 40),

                Color.FromArgb(255, 230, 70),

                Color.FromArgb(70, 220, 120),

                Color.FromArgb(0, 170, 255),

                Color.FromArgb(130, 110, 255),

                Color.FromArgb(240, 90, 200),

                Color.FromArgb(240, 240, 240)

            };

            int pkSwatchSize = S(24);

            int swatchX = cPad + S(96);

            for (int i = 0; i < palette.Length; i++)

            {

                Color swatch = palette[i];

                var btn = new PredatorButton

                {

                    Text = "",

                    Location = new Point(swatchX + i * (pkSwatchSize + S(6)), S(280)),

                    Size = new Size(pkSwatchSize, pkSwatchSize)

                };

                btn.Paint += (s, e) =>

                {
                    if (btn.Width <= 7 || btn.Height <= 7) return;

                    var g = e.Graphics;

                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    using (var brush = new SolidBrush(swatch))

                    {

                        g.FillEllipse(brush, 3, 3, btn.Width - 7, btn.Height - 7);

                    }

                    if (swatch.ToArgb() == _activePaintColor.ToArgb())

                    {

                        using var pen = new Pen(AccentColor, 2f);

                        g.DrawEllipse(pen, 1, 1, btn.Width - 3, btn.Height - 3);

                    }

                };

                btn.Click += (s, e) =>

                {

                    _activePaintColor = swatch;

                    InvalidatePerKeySwatches();

                };

                cardKeys.Controls.Add(btn);

                _perKeySwatches.Add(btn);

            }



            int keyBtnGap = S(10);

            int keyBtnW = (usableW - cPad * 2 - keyBtnGap * 2) / 3;

            var btnFill = MakeButton(cardKeys, "Fill All Keys", cPad, S(316), keyBtnW, S(34));

            btnFill.Click += (s, e) =>

            {

                if (_lightingController == null || _perKeyMap == null) return;

                _lightingController.SetAllKeys(_activePaintColor);

                _perKeyMap.FillAll(_activePaintColor);

                PersistLightingState();

                _lightingController.QueueApply(KeyboardLightingScope.PerKey, 0);

            };



            var btnClearKeys = MakeButton(cardKeys, "Clear Keys", cPad + keyBtnW + keyBtnGap, S(316), keyBtnW, S(34));

            btnClearKeys.Click += (s, e) =>

            {

                if (_lightingController == null || _perKeyMap == null) return;

                _lightingController.SetAllKeys(Color.Black);

                _perKeyMap.FillAll(Color.Black);

                PersistLightingState();

                _lightingController.QueueApply(KeyboardLightingScope.PerKey, 0);

            };



            _btnPerKeySend = MakeButton(cardKeys, "Send to Keyboard", cPad + (keyBtnW + keyBtnGap) * 2, S(316), keyBtnW, S(34));

            _btnPerKeySend.Click += (s, e) =>

            {

                if (_lightingController == null) return;

                PersistLightingState();

                _ = ApplyLightingNowAsync(KeyboardLightingScope.PerKey);

            };



            MakeLabel(cardKeys, "Right-click a key to inspect it. Drag to paint several keys at once. The map stays disabled unless per-key support was confirmed.", cPad, S(358), FontHeaderLight, SubHeaderColor);



            _pnlLighting.AutoScrollMinSize = new Size(0, keyY + keyH + pad * 2);

            RefreshLightingUi();

        }



        private void PaintZoneButton(PredatorButton btn, int zone, Graphics g)

        {

            if (btn.Width <= S(12) || btn.Height <= S(12)) return;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;



            bool isSelected = (_zoneLayoutMode != 0) && (zone == _selectedZoneIndex);

            Color zoneColor = _lightingController?.GetZoneColor(zone) ?? Color.Black;



            // Background surface

            var bounds = new Rectangle(0, 0, btn.Width, btn.Height);

            Color bg = isSelected

                ? (ThemeManager.IsDarkThemeActive ? Color.FromArgb(24, 34, 56) : Color.FromArgb(235, 242, 252))

                : (ThemeManager.IsDarkThemeActive ? Color.FromArgb(20, 26, 42) : Color.FromArgb(244, 240, 232));

            using (var brush = new SolidBrush(bg))

            {

                g.FillRectangle(brush, bounds);

            }



            // Glowing cyan highlight border when selected

            if (isSelected)

            {

                using var glowPen = new Pen(Color.FromArgb(90, ThemeManager.Accent), 3f);

                g.DrawRectangle(glowPen, 1, 1, btn.Width - 3, btn.Height - 3);



                using var selPen = new Pen(ThemeManager.Accent, 1.5f);

                g.DrawRectangle(selPen, 0, 0, btn.Width - 1, btn.Height - 1);

            }

            else

            {

                using var borderPen = new Pen(ThemeManager.ControlBorder, 1f);

                g.DrawRectangle(borderPen, 0, 0, btn.Width - 1, btn.Height - 1);

            }



            // Titles based on active layout

            string title;

            string subtitle;

            if (_zoneLayoutMode == 1)

            {

                title = "WHOLE KEYBOARD";

                subtitle = "UNIFIED SINGLE-ZONE RGB";

            }

            else if (_zoneLayoutMode == 3)

            {

                title = zone switch { 0 => "ZONE 1", 1 => "ZONE 2", _ => "ZONE 3" };

                subtitle = zone switch { 0 => "LEFT SECTOR", 1 => "CENTER SECTOR", _ => "RIGHT SECTOR" };

            }

            else

            {

                title = $"ZONE {zone + 1}";

                subtitle = zone switch

                {

                    0 => "WASD / LEFT",

                    1 => "QWERTY / CENTER",

                    2 => "NAV / RIGHT",

                    _ => "NUMPAD / EDGE"

                };

            }



            int leftPad = S(12);

            int topPad = S(8);



            // Draw Title

            Font titleFont = FontBodyBold;

            Color titleColor = isSelected ? ThemeManager.Accent : ThemeManager.TextPrimary;

            TextRenderer.DrawText(g, title, titleFont, new Point(leftPad, topPad), titleColor,

                TextFormatFlags.Left | TextFormatFlags.NoPrefix);



            // Draw Subtitle

            Font subFont = FontHeaderLight;

            Color subColor = ThemeManager.TextSecondary;

            TextRenderer.DrawText(g, subtitle, subFont, new Point(leftPad, topPad + S(18)), subColor,

                TextFormatFlags.Left | TextFormatFlags.NoPrefix);



            // Color pill & Hex Badge row

            int pillY = btn.Height - S(26);

            int pillSize = S(16);



            // Color swatch with ambient glow

            using (var auralBrush = new SolidBrush(Color.FromArgb(90, zoneColor.R, zoneColor.G, zoneColor.B)))

            {

                g.FillEllipse(auralBrush, leftPad - 2, pillY - 2, pillSize + 4, pillSize + 4);

            }

            using (var colorBrush = new SolidBrush(zoneColor))

            {

                g.FillEllipse(colorBrush, leftPad, pillY, pillSize, pillSize);

            }

            using (var circlePen = new Pen(Color.FromArgb(180, 255, 255, 255), 1f))

            {

                g.DrawEllipse(circlePen, leftPad, pillY, pillSize, pillSize);

            }



            // Hex badge

            string hexText = zoneColor.ToArgb() == Color.Black.ToArgb()

                ? "#000000 (OFF)"

                : $"#{zoneColor.R:X2}{zoneColor.G:X2}{zoneColor.B:X2}";



            int hexX = leftPad + pillSize + S(8);

            var hexRect = new Rectangle(hexX, pillY - 1, S(96), S(18));

            using (var hexBgBrush = new SolidBrush(ThemeManager.IsDarkThemeActive ? Color.FromArgb(14, 18, 30) : Color.FromArgb(228, 222, 212)))

            {

                g.FillRectangle(hexBgBrush, hexRect);

            }

            using (var hexBorderPen = new Pen(ThemeManager.ControlBorder, 1f))

            {

                g.DrawRectangle(hexBorderPen, hexRect);

            }

            TextRenderer.DrawText(g, hexText, FontHeaderLight, hexRect, ThemeManager.TextPrimary,

                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);



            // If selected, draw small cyan indicator tag on top right

            if (isSelected)

            {

                string tagText = "ACTIVE";

                Size tagSize = TextRenderer.MeasureText(g, tagText, FontHeaderLight);

                int tagH = Math.Max(tagSize.Height + 2, S(16));

                var tagRect = new Rectangle(btn.Width - tagSize.Width - S(14), topPad, tagSize.Width + S(6), tagH);

                using var tagBg = new SolidBrush(Color.FromArgb(40, ThemeManager.Accent));

                g.FillRectangle(tagBg, tagRect);

                using var tagBorder = new Pen(ThemeManager.Accent, 1f);

                g.DrawRectangle(tagBorder, tagRect);

                TextRenderer.DrawText(g, tagText, FontHeaderLight, tagRect, ThemeManager.Accent,

                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            }

        }



        private void ShowColorDialog(Color initial, Action<Color> onColorChosen)

        {

            using var dlg = new ColorDialog { FullOpen = true, Color = initial };

            if (_appSettings.CustomColors != null && _appSettings.CustomColors.Length > 0)

            {

                dlg.CustomColors = (int[])_appSettings.CustomColors.Clone();

            }

            var res = dlg.ShowDialog(this);

            if (dlg.CustomColors != null)

            {

                _appSettings.CustomColors = (int[])dlg.CustomColors.Clone();

                _appSettings.Save();

            }

            if (res == DialogResult.OK)

            {

                onColorChosen(dlg.Color);

            }

        }



        private void SetZoneLayout(int layoutMode, bool userInitiated = true)

        {

            _zoneLayoutMode = layoutMode;
            if (_lightingController != null) _lightingController.LayoutMode = layoutMode;
            if (userInitiated)
            {
                _appSettings.KeyboardZoneLayoutMode = layoutMode;
                _appSettings.Save();
            }

            if (_zoneButtons[0] == null) return;



            int activePillIdx = layoutMode switch

            {

                1 => 0,

                3 => 1,

                4 => 2,

                _ => 3

            };



            for (int i = 0; i < _layoutPillButtons.Length; i++)

            {

                var pill = _layoutPillButtons[i];

                if (pill != null)

                {

                    bool isCurrent = (i == activePillIdx);

                    pill.IsActive = isCurrent;

                    pill.CustomActiveColor = isCurrent ? ThemeManager.Accent : null;

                    pill.Invalidate();

                }

            }



            if (layoutMode == 0) // Per-Key

            {

                if (_pnlSectorVisualizer != null) _pnlSectorVisualizer.Visible = false;

                if (_lblPerKeyPrompt != null) _lblPerKeyPrompt.Visible = true;

            }

            else

            {

                if (_pnlSectorVisualizer != null) _pnlSectorVisualizer.Visible = true;

                if (_lblPerKeyPrompt != null) _lblPerKeyPrompt.Visible = false;



                int totalW = _pnlSectorVisualizer?.ClientSize.Width ?? S(800);

                if (totalW <= 0) totalW = S(800);

                int zoneGap = S(10);

                int count = Math.Clamp(layoutMode, 1, 4); // 1, 3, or 4

                int zoneW = Math.Max(10, (totalW - (count - 1) * zoneGap) / count);

                int btnH = Math.Max(S(40), (_pnlSectorVisualizer?.ClientSize.Height ?? S(82)) - 2);



                for (int i = 0; i < 4; i++)

                {

                    var btn = _zoneButtons[i];

                    if (btn == null) continue;



                    if (i < count)

                    {

                        btn.Visible = true;

                        btn.Location = new Point(i * (zoneW + zoneGap), 0);

                        btn.Size = new Size(zoneW, btnH);

                        btn.Invalidate();

                    }

                    else

                    {

                        btn.Visible = false;

                    }

                }



                if (_selectedZoneIndex >= count)

                {

                    _selectedZoneIndex = 0;

                }

            }



            RefreshZoneButtons();

        }



        private void ApplyZoneQuickColor(Color color)

        {
            if (_isInitializingLightingUi) return;

            if (_lightingController == null) return;
            LightingEffectsManager.StopSoftwareAnimation();
            SetZonePickersEnabled(true);
            _appSettings.DynamicLightingEffectName = "Off";
            _appSettings.IsZoneLightingActive = true;
            _appSettings.ActiveLightingPreset = "Custom";
            bool prevQuickSuppress = _suppressUiEvents;
            _suppressUiEvents = true;
            try
            {
                if (_rgbDropDown != null && _rgbDropDown.SelectedIndex != 0) _rgbDropDown.SelectedIndex = 0;
            }
            finally
            {
                _suppressUiEvents = prevQuickSuppress;
            }
            CheckRgbTrayFromMode(0);

            _activePaintColor = color;



            if (_syncAllZones || _zoneLayoutMode == 1)

            {

                _lightingController.SetAllZones(color);

                if (_zoneLayoutMode == 0 && _perKeyMap != null)

                {

                    _lightingController.SetAllKeys(color);

                    _perKeyMap.FillAll(color);

                }

            }

            else if (_zoneLayoutMode == 3)

            {

                _lightingController.SetZoneColor(_selectedZoneIndex, color);

                if (_selectedZoneIndex == 2)

                {

                    _lightingController.SetZoneColor(3, color);

                }

            }

            else if (_zoneLayoutMode == 4)

            {

                _lightingController.SetZoneColor(_selectedZoneIndex, color);

            }

            else if (_zoneLayoutMode == 0 && _perKeyMap != null)

            {

                InvalidatePerKeySwatches();

            }



            PersistLightingState();

            RefreshZoneButtons();

            InvalidateZoneSwatches();



            _lightingController.QueueApply(_zoneLayoutMode == 0 ? KeyboardLightingScope.PerKey : KeyboardLightingScope.Zones, 0);

        }



        private void ApplyZonePreset(Color[] colors, string presetName = "Custom")

        {
            if (_isInitializingLightingUi) return;

            if (_lightingController == null) return;
            LightingEffectsManager.StopSoftwareAnimation();
            SetZonePickersEnabled(true);
            _appSettings.DynamicLightingEffectName = "Off";
            _appSettings.IsZoneLightingActive = true;
            _appSettings.ActiveLightingPreset = presetName;
            _appSettings.KeyboardZoneLayoutMode = _zoneLayoutMode;
            bool prevPresetSuppress = _suppressUiEvents;
            _suppressUiEvents = true;
            try
            {
                if (_rgbDropDown != null && _rgbDropDown.SelectedIndex != 0) _rgbDropDown.SelectedIndex = 0;
            }
            finally
            {
                _suppressUiEvents = prevPresetSuppress;
            }
            CheckRgbTrayFromMode(0);

            for (int i = 0; i < Math.Min(4, colors.Length); i++)

            {

                _lightingController.SetZoneColor(i, colors[i]);

            }

            if (_zoneLayoutMode == 0 && _perKeyMap != null)

            {

                var keys = _perKeyMap.Keys;

                double widthUnits = _perKeyMap.TotalWidthUnits > 0 ? _perKeyMap.TotalWidthUnits : KeyboardLayouts.FullAnsiWidthUnits;

                for (int i = 0; i < keys.Count; i++)

                {

                    double x = keys[i].XUnits;

                    int quad = (int)Math.Clamp(Math.Floor(x / (widthUnits / 4.0)), 0, 3);

                    Color qc = colors[Math.Min(quad, colors.Length - 1)];

                    _lightingController.SetKeyColor(i, qc);

                    _perKeyMap.SetColor(i, qc);

                }

            }

            PersistLightingState();

            RefreshZoneButtons();

            InvalidateZoneSwatches();

            InvalidatePerKeySwatches();

            UpdatePresetButtonsHighlight();

            _lightingController.Effect = "STATIC";

            _lightingController.QueueApply(_zoneLayoutMode == 0 ? KeyboardLightingScope.PerKey : KeyboardLightingScope.Zones, 60);

        }

        private void UpdatePresetButtonsHighlight()
        {
            if (!_appSettings.IsZoneLightingActive)
            {
                if (_btnPresetCyberpunk != null) { _btnPresetCyberpunk.IsActive = false; _btnPresetCyberpunk.CustomActiveColor = null; _btnPresetCyberpunk.Invalidate(); }
                if (_btnPresetFirestorm != null) { _btnPresetFirestorm.IsActive = false; _btnPresetFirestorm.CustomActiveColor = null; _btnPresetFirestorm.Invalidate(); }
                if (_btnPresetIce != null) { _btnPresetIce.IsActive = false; _btnPresetIce.CustomActiveColor = null; _btnPresetIce.Invalidate(); }
                if (_btnPresetOff != null) { _btnPresetOff.IsActive = false; _btnPresetOff.CustomActiveColor = null; _btnPresetOff.Invalidate(); }
                if (_btnPresetCustom != null) { _btnPresetCustom.IsActive = false; _btnPresetCustom.CustomActiveColor = null; _btnPresetCustom.Invalidate(); }
                return;
            }

            string preset = _appSettings.ActiveLightingPreset ?? "Custom";
            void SetBtn(PredatorButton? b, bool active)
            {
                if (b == null) return;
                b.IsActive = active;
                b.CustomActiveColor = active ? ThemeManager.Accent : null;
                b.Invalidate();
            }

            SetBtn(_btnPresetCyberpunk, string.Equals(preset, "Cyberpunk", StringComparison.OrdinalIgnoreCase));
            SetBtn(_btnPresetFirestorm, string.Equals(preset, "Firestorm", StringComparison.OrdinalIgnoreCase));
            SetBtn(_btnPresetIce, string.Equals(preset, "Predator Ice", StringComparison.OrdinalIgnoreCase));
            SetBtn(_btnPresetOff, string.Equals(preset, "Off", StringComparison.OrdinalIgnoreCase));
            SetBtn(_btnPresetCustom, string.Equals(preset, "Custom", StringComparison.OrdinalIgnoreCase));
        }



        private void InvalidateZoneSwatches()

        {

            foreach (var btn in _zonePaletteButtons)

            {

                if (btn != null && !btn.IsDisposed) btn.Invalidate();

            }

        }
        private void UpdateZoneSwatchUiOnly(int zone, Color color)
        {
            if (_lightingController != null)
            {
                _lightingController.SetZoneColor(zone, color);
            }
            if (zone >= 0 && zone < _zoneButtons.Length)
            {
                var btn = _zoneButtons[zone];
                if (btn != null && !btn.IsDisposed)
                {
                    btn.Invalidate();
                }
            }
        }

        private void UpdateDynamicEffectDropdownUiOnly(string effectName)
        {
            int targetIdx = 0;
            if (!string.IsNullOrWhiteSpace(effectName) && !string.Equals(effectName, "Off", StringComparison.OrdinalIgnoreCase) && _rgbDropDown != null)
            {
                for (int i = 1; i < _rgbDropDown.Items.Count; i++)
                {
                    if (string.Equals(_rgbDropDown.Items[i]?.ToString(), effectName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIdx = i;
                        break;
                    }
                }
            }

            if (_rgbDropDown != null && targetIdx >= 0 && targetIdx < _rgbDropDown.Items.Count)
            {
                if (_rgbDropDown.SelectedIndex != targetIdx)
                {
                    _rgbDropDown.SelectedIndex = targetIdx;
                }
            }

            UpdateRgbControls(targetIdx);
            CheckRgbTrayFromMode(targetIdx);
        }

        private void UpdateDynamicColorSwatchUiOnly(Color color)
        {
            if (_colorPicker != null)
            {
                _colorPicker.Color = color;
            }
            foreach (var b in _dynamicTintButtons)
            {
                if (b != null && !b.IsDisposed) b.Invalidate();
            }
            if (_btnColorPick != null && !_btnColorPick.IsDisposed)
            {
                _btnColorPick.Invalidate();
            }
        }

        private void UpdateBrightnessUiOnly(int brightness)
        {
            int clamped = Math.Clamp(brightness, 0, 100);
            if (_brightnessSlider != null)
            {
                _brightnessSlider.Value = clamped;
            }
            if (_lblBrightHdr != null)
            {
                _lblBrightHdr.Text = (IsOnBattery() && clamped == 0 && !_manualBacklightOnBattery)
                    ? "BRIGHTNESS: Off (Battery Saver)"
                    : $"BRIGHTNESS: {clamped}%";
            }
        }







        #region Keyboard Lighting Integration



        private void InitializeKeyboardLighting()

        {

            KeyboardLightingTier provisional = _appSettings.KeyboardLightingTierOverride switch

            {

                2 => KeyboardLightingTier.PerKey,

                3 => KeyboardLightingTier.SingleZone,

                4 => KeyboardLightingTier.ThreeZone,

                1 => KeyboardLightingTier.FourZone,

                _ => (_appSettings.PerKeyCapabilityDetected ? KeyboardLightingTier.PerKey : KeyboardLightingTier.FourZone)

            };



            var initProfile = AcerLaptopModelCatalog.Lookup(_appSettings.KeyboardCapabilityModel);

            string initLayoutId = initProfile?.HasFullKeyboardWithNumpad == false ? KeyboardLayouts.AnsiTklId : KeyboardLayouts.FullAnsiId;

            int initKeyCount = initProfile?.HasFullKeyboardWithNumpad == false ? KeyboardLayouts.AnsiTklKeyCount : KeyboardLayouts.FullAnsiKeyCount;



            _lightingCapabilities = new KeyboardLightingCapabilities

            {

                Tier = provisional,

                PerKeySupported = provisional == KeyboardLightingTier.PerKey,

                LayoutId = initLayoutId,

                KeyCount = initKeyCount,

                Model = string.IsNullOrWhiteSpace(_appSettings.KeyboardCapabilityModel)

                    ? "Unknown"

                    : _appSettings.KeyboardCapabilityModel,

                Notes = "Cached capability from the previous run. Re-detect to confirm against the current BIOS and Acer service.",

                DetectionSource = "cached",

                VerifiedOnHardware = false

            };



            _lightingController = new KeyboardLightingController(_wmi, _lightingCapabilities);
            _lightingController.LayoutMode = _zoneLayoutMode;

            _lightingController.LoadState(
                new[] {
                    Color.FromArgb(_appSettings.StaticZone1ColorArgb),
                    Color.FromArgb(_appSettings.StaticZone2ColorArgb),
                    Color.FromArgb(_appSettings.StaticZone3ColorArgb),
                    Color.FromArgb(_appSettings.StaticZone4ColorArgb)
                },
                ParsePerKeyColors(_appSettings.KeyboardPerKeyColors));

            _lightingController.Brightness = _appSettings.KeyboardBrightnessLevel;

            _lightingController.Speed = _appSettings.RgbSpeed > 0 ? _appSettings.RgbSpeed : 50;

            _lightingController.Effect = "STATIC";

            _lightingController.StatusChanged += OnLightingStatusChanged;




        }



        private static Color[]? ParsePerKeyColors(string? value)

        {

            if (string.IsNullOrWhiteSpace(value)) return null;

            return KeyboardLightingPalette.Parse(value, KeyboardLayouts.FullAnsiKeyCount, Color.Black);

        }



        private void BeginKeyboardCapabilityDetection(bool force = false)

        {

            if (_lightingProbeRunning) return;

            _lightingProbeRunning = true;



            if (_lblLightingCapability != null)

            {

                _lblLightingCapability.Text = force

                    ? "KEYBOARD: re-reading BIOS identity and probing the Acer Lighting Service..."

                    : "KEYBOARD: reading BIOS identity and probing the Acer Lighting Service...";

                _lblLightingCapability.ForeColor = SubHeaderColor;

            }



            int tierOverride = _appSettings.KeyboardLightingTierOverride;

            _ = Task.Run(async () =>

            {

                KeyboardLightingCapabilities caps;

                try

                {

                    caps = await KeyboardLightingDetector.DetectAsync(_wmi, tierOverride).ConfigureAwait(false);

                }

                catch (Exception ex)

                {

                    caps = new KeyboardLightingCapabilities

                    {

                        Tier = KeyboardLightingTier.FourZone,

                        Model = string.IsNullOrWhiteSpace(_appSettings.KeyboardCapabilityModel)

                            ? "Unknown"

                            : _appSettings.KeyboardCapabilityModel,

                        Notes = "Capability probe failed (" + ex.Message + "). 4-zone control remains available.",

                        DetectionSource = "probe-failed"

                    };

                }



                if (IsDisposed || _isClosing)

                {

                    _lightingProbeRunning = false;

                    return;

                }



                try

                {

                    SafeBeginInvoke(() => ApplyDetectedLightingCapabilities(caps));

                }

                catch

                {

                    _lightingProbeRunning = false;

                }

            });

        }



        private void ApplyDetectedLightingCapabilities(KeyboardLightingCapabilities caps)

        {

            _lightingProbeRunning = false;

            if (IsDisposed || _isClosing) return;



            _lightingCapabilities = caps;

            _lightingController?.UpdateCapabilities(caps);

            _detectedBiosLayoutMode = caps.Tier switch

            {

                KeyboardLightingTier.SingleZone => 1,

                KeyboardLightingTier.ThreeZone => 3,

                KeyboardLightingTier.FourZone => 4,

                KeyboardLightingTier.PerKey => 0,

                _ => 4

            };

            if (_lblBiosDetectedBadge != null && !_lblBiosDetectedBadge.IsDisposed)

            {

                string modelTag = !string.IsNullOrWhiteSpace(caps.ModelMarketingName)

                    ? caps.ModelMarketingName

                    : (!string.IsNullOrWhiteSpace(caps.Model) ? caps.Model : "Detected Platform");

                _lblBiosDetectedBadge.Text = $"(Auto-detected from BIOS: {caps.TierLabel} - {modelTag})";

            }

            if (_appSettings.KeyboardLightingTierOverride == 0)

            {

                SetZoneLayout(ResolveSavedZoneLayout(_appSettings.KeyboardZoneLayoutMode, caps.Tier), userInitiated: false);

            }





            _appSettings.PerKeyCapabilityDetected = caps.PerKeySupported;

            _appSettings.KeyboardCapabilityModel = caps.Model;

            _appSettings.KeyboardCapabilityNotes = caps.Notes;

            _appSettings.Save();



            _suppressLightingTierChange = true;

            try

            {

                if (_cboLightingTier != null &&

                    _cboLightingTier.SelectedIndex != _appSettings.KeyboardLightingTierOverride)

                {

                    _cboLightingTier.SelectedIndex = _appSettings.KeyboardLightingTierOverride;

                }

            }

            finally

            {

                _suppressLightingTierChange = false;

            }



            RefreshLightingUi();

            UpdateLightingStatusLabel(new KeyboardLightingStatus(

                caps.Tier != KeyboardLightingTier.Unavailable,

                false,

                caps.Tier,

                caps.Notes,

                DateTime.UtcNow));

            // Cold-start capability discovery can replace a provisional single-zone layout.
            // Replay the current saved intent through the final detected transport/layout.
            ApplySavedKeyboardLightingFromSettings();

        }



        internal static int ResolveSavedZoneLayout(int saved, KeyboardLightingTier tier)
        {
            if (tier == KeyboardLightingTier.SingleZone) return 1;
            if (tier == KeyboardLightingTier.ThreeZone) return 3;
            if (saved == 0 && tier != KeyboardLightingTier.PerKey) return 4;
            return saved is 0 or 1 or 3 or 4 ? saved : 4;
        }

        private void RefreshLightingUi()

        {

            if (IsDisposed || _lblLightingCapability == null) return;



            var caps = _lightingCapabilities;

            if (caps != null)

            {

                _lblLightingCapability.Text = $"KEYBOARD: {caps.TierLabel}   |   {caps.Summary}";

                _lblLightingCapability.ForeColor = caps.Tier switch

                {

                    KeyboardLightingTier.Unavailable => Color.FromArgb(232, 116, 104),

                    KeyboardLightingTier.PerKey => AccentColor,

                    _ => SubHeaderColor

                };

            }



            if (_speedSlider != null && _lightingController != null)

            {

                int wanted = Math.Clamp(_lightingController.Speed, _speedSlider.Minimum, _speedSlider.Maximum);

                if (_speedSlider.Value != wanted) _speedSlider.Value = wanted;

                if (_lblSpeedHdr != null) _lblSpeedHdr.Text = $"EFFECT SPEED: {_speedSlider.Value}%";

            }



            RefreshZoneButtons();



            if (_perKeyMap != null && _lightingController != null)

            {

                if (caps != null && !string.IsNullOrEmpty(caps.LayoutId))

                {

                    if ((caps.LayoutId == KeyboardLayouts.AnsiTklId && _perKeyMap.KeyCount != KeyboardLayouts.AnsiTklKeyCount) ||

                        (caps.LayoutId == KeyboardLayouts.FullAnsiId && _perKeyMap.KeyCount != KeyboardLayouts.FullAnsiKeyCount))

                    {

                        _perKeyMap.SetLayout(caps.LayoutId);

                    }

                }

                _perKeyMap.Colors = _lightingController.SnapshotKeyColors();

                _perKeyMap.AccentColor = AccentColor;

            }



            bool perKey = caps?.PerKeySupported == true;

            if (_perKeyMap != null) _perKeyMap.Enabled = perKey;

            if (_btnPerKeySend != null) _btnPerKeySend.Enabled = perKey;

            if (_btnZonesApply != null) _btnZonesApply.Enabled = caps == null || caps.Tier != KeyboardLightingTier.Unavailable;

        }



        private void RefreshZoneButtons()

        {

            foreach (var btn in _zoneButtons)

            {

                if (btn != null && !btn.IsDisposed) btn.Invalidate();

            }

        }



        private void InvalidatePerKeySwatches()

        {

            foreach (var btn in _perKeySwatches)

            {

                if (btn != null && !btn.IsDisposed) btn.Invalidate();

            }

        }



        private void PickZoneColor(int zone)

        {
            if (_isInitializingLightingUi) return;

            var controller = _lightingController;

            if (controller == null) return;
            _appSettings.IsZoneLightingActive = true;
            _appSettings.ActiveLightingPreset = "Custom";
            _appSettings.Save();



            Color initial = zone >= 0 ? controller.GetZoneColor(zone) : _activePaintColor;

            ShowColorDialog(initial, chosenColor =>

            {

                if (_syncAllZones || zone < 0 || _zoneLayoutMode == 1)

                {

                    controller.SetAllZones(chosenColor);

                    _activePaintColor = chosenColor;

                    if (_zoneLayoutMode == 0 && _perKeyMap != null)

                    {

                        controller.SetAllKeys(chosenColor);

                        _perKeyMap.FillAll(chosenColor);

                    }

                }

                else if (_zoneLayoutMode == 3)

                {

                    controller.SetZoneColor(zone, chosenColor);

                    if (zone == 2) controller.SetZoneColor(3, chosenColor);

                    _activePaintColor = chosenColor;

                }

                else

                {

                    controller.SetZoneColor(zone, chosenColor);

                    _activePaintColor = chosenColor;

                }



                PersistLightingState(immediate: true);

                RefreshZoneButtons();

                InvalidateZoneSwatches();

                InvalidatePerKeySwatches();

                controller.QueueApply(_zoneLayoutMode == 0 ? KeyboardLightingScope.PerKey : KeyboardLightingScope.Zones);

            });

        }



        private void OnPerKeyPainted(object? sender, int index)

        {

            var controller = _lightingController;

            if (controller == null || _perKeyMap == null) return;

            if (_lightingCapabilities?.PerKeySupported != true) return;
            _appSettings.IsZoneLightingActive = true;
            _appSettings.ActiveLightingPreset = "Custom";
            _appSettings.Save();



            controller.SetKeyColor(index, _activePaintColor);

            _perKeyMap.SetColor(index, _activePaintColor);

            PersistLightingState();

            controller.QueueApply(KeyboardLightingScope.PerKey, 120);

        }



        private void PersistLightingState(bool immediate = false)
        {
            var controller = _lightingController;
            if (controller == null) return;

            try
            {
                var zoneSnap = controller.SnapshotZoneColors();
                if (zoneSnap.Length >= 4)
                {
                    _appSettings.StaticZone1ColorArgb = zoneSnap[0].ToArgb();
                    _appSettings.StaticZone2ColorArgb = zoneSnap[1].ToArgb();
                    _appSettings.StaticZone3ColorArgb = zoneSnap[2].ToArgb();
                    _appSettings.StaticZone4ColorArgb = zoneSnap[3].ToArgb();
                }
                _appSettings.IsZoneLightingActive = true;
                _appSettings.KeyboardZoneColors = KeyboardLightingPalette.Serialize(zoneSnap);
                _appSettings.KeyboardPerKeyColors = KeyboardLightingPalette.Serialize(controller.SnapshotKeyColors());
            }
            catch
            {
                return;
            }

            if (immediate)
            {
                try { _appSettings.Save(); } catch { }
                return;
            }

            if (Interlocked.CompareExchange(ref _lightingPersistPending, 1, 0) != 0) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _lightingPersistPending, 0);
                }

                try { _appSettings.Save(); } catch { }
            });
        }



        internal static bool ShouldRestoreStaticLighting(AppSettings settings, int selectedRgb) =>
            settings.IsZoneLightingActive ||
            string.Equals(settings.DynamicLightingEffectName, "Off", StringComparison.OrdinalIgnoreCase) || selectedRgb <= 0;

        private async Task ApplyCurrentLightingStateAsync(int selectedRgb, int targetBright)
        {
            _wmi.SetBrightness((byte)targetBright, applyMode: false);
            if (ShouldRestoreStaticLighting(_appSettings, selectedRgb))
            {
                var scope = (_zoneLayoutMode == 0 && _lightingCapabilities?.PerKeySupported == true)
                    ? KeyboardLightingScope.PerKey
                    : KeyboardLightingScope.Zones;
                if (_lightingController != null)
                {
                    _lightingController.Brightness = targetBright;
                    await _lightingController.ApplyAsync(scope).ConfigureAwait(false);
                }
            }
            else if (selectedRgb > 0)
            {
                int dynamicMode = selectedRgb;
                byte spd = GetMappedSpeed();
                Color c = Color.FromArgb(_appSettings.DynamicEffectPrimaryColorArgb);
                LightingEffectsManager.ApplyEffect(dynamicMode, _wmi, c.R, c.G, c.B, (byte)targetBright, spd, 0, (OperatingMode)_currentPowerMode);
            }
        }

        private async Task ApplyLightingNowAsync(KeyboardLightingScope scope)

        {

            var controller = _lightingController;

            if (controller == null) return;



            try

            {

                var status = await controller.ApplyAsync(scope).ConfigureAwait(true);

                UpdateLightingStatusLabel(status);

            }

            catch (Exception ex)

            {

                Program.Report(ex, false);

                UpdateLightingStatusLabel(new KeyboardLightingStatus(

                    false,

                    true,

                    _lightingCapabilities?.Tier ?? KeyboardLightingTier.Unavailable,

                    ex.Message,

                    DateTime.UtcNow));

            }

        }



        private void OnLightingStatusChanged(object? sender, KeyboardLightingStatus status)

        {

            if (IsDisposed || _isClosing) return;

            try { SafeBeginInvoke(() => UpdateLightingStatusLabel(status)); } catch { }

        }



        private void UpdateLightingStatusLabel(KeyboardLightingStatus status)

        {

            if (IsDisposed || Disposing || _isClosing || _lblLightingStatus == null || _lblLightingStatus.IsDisposed) return;

            if (InvokeRequired)
            {
                SafeBeginInvoke(() => UpdateLightingStatusLabel(status));
                return;
            }



            string prefix = status.Success ? (status.Degraded ? "Partial: " : "OK: ") : "Failed: ";

            _lblLightingStatus.Text = prefix + status.Message;

            _lblLightingStatus.ForeColor = status.Success

                ? (status.Degraded ? Color.FromArgb(240, 190, 90) : Color.FromArgb(120, 210, 150))

                : Color.FromArgb(232, 116, 104);

        }



        #endregion



        #region Thermal Safety Integration



        private void InitializeThermalSafety()

        {

            _thermalSafety = new ThermalSafetyCoordinator(_appSettings.ToThermalProfile());

            _thermalSafety.CriticalEntered += OnThermalCriticalEntered;

        }



        private void OnThermalCriticalEntered(object? sender, ThermalDecision decision)

        {

            if (!_appSettings.ThermalCriticalAlertEnabled) return;

            if (IsDisposed || _isClosing) return;



            try

            {

                SafeBeginInvoke(() =>

                {

                    if (_lblThermalStage != null && !_lblThermalStage.IsDisposed)

                    {

                        _lblThermalStage.Text = "STAGE: CRITICAL";

                        _lblThermalStage.ForeColor = Color.FromArgb(232, 96, 88);

                    }



                    try

                    {

                        _trayIcon.ShowBalloonTip(

                            6000,

                            "Thermal guard: critical",

                            decision.Reason + ". Fans pinned to 100% and CPU power capped.",

                            ToolTipIcon.Warning);

                    }

                    catch { }

                });

            }

            catch { }

        }



        private ThermalDecision EvaluateThermalSafety()

        {

            if (_thermalSafety == null) return ThermalDecision.Idle;



            int minW = _appSettings.CpuMinAllowedPowerWatts > 0 ? _appSettings.CpuMinAllowedPowerWatts : 15;

            int tdpW = _appSettings.CpuThermalSpecTdpWatts > 0 ? _appSettings.CpuThermalSpecTdpWatts : 45;

            var (targetPl1, _) = _appSettings.GetEffectivePowerLimitsForMode(ByteToOperatingMode(_currentPowerMode));



            return _thermalSafety.Evaluate(_cpuTemp, _gpuTemp, targetPl1, minW, tdpW);

        }



        private int CurrentThermalFloor(bool cpu)

        {

            if (!_appSettings.AntiThrottleEnabled || _thermalSafety == null) return 0;



            var decision = _thermalSafety.IsDecisionFresh(TimeSpan.FromSeconds(5))

                ? _lastThermalDecision

                : EvaluateThermalSafety();



            return cpu ? decision.CpuFanFloor : decision.GpuFanFloor;

        }



        private void ApplyThermalPowerCap()

        {

            var controller = CpuPowerLimitController.Instance;

            if (controller == null) return;



            try

            {

                if (_appSettings.AntiThrottleEnabled && _lastThermalDecision.CpuPowerCapWatts.HasValue)

                {

                    controller.SetThermalSafetyCap(_lastThermalDecision.CpuPowerCapWatts.Value);

                }

                else if (controller.ThermalSafetyCapWatts.HasValue)

                {

                    controller.ClearThermalSafetyCap();

                }

            }

            catch { }

        }



        private void RefreshThermalUi()

        {

            if (IsDisposed) return;



            var profile = _thermalSafety?.Profile ?? _appSettings.ToThermalProfile();



            _suppressThermalUi = true;

            try

            {

                if (_switchThermalGuard != null && !_switchThermalGuard.IsDisposed)

                    _switchThermalGuard.Checked = profile.Enabled;



                if (_sliderThermalCeiling != null && !_sliderThermalCeiling.IsDisposed)

                    _sliderThermalCeiling.Value = Math.Clamp((int)Math.Round(profile.CpuCeilingCelsius),

                        _sliderThermalCeiling.Minimum, _sliderThermalCeiling.Maximum);



                if (_sliderThermalHysteresis != null && !_sliderThermalHysteresis.IsDisposed)

                    _sliderThermalHysteresis.Value = Math.Clamp((int)Math.Round(profile.HysteresisCelsius),

                        _sliderThermalHysteresis.Minimum, _sliderThermalHysteresis.Maximum);

            }

            finally

            {

                _suppressThermalUi = false;

            }



            if (_lblThermalCeilingHdr != null)

                _lblThermalCeilingHdr.Text = $"CPU CEILING: {Math.Round(profile.CpuCeilingCelsius)} C";

            if (_lblThermalHysteresisHdr != null)

                _lblThermalHysteresisHdr.Text = $"RELEASE HYSTERESIS: {Math.Round(profile.HysteresisCelsius)} C";



            UpdateThermalTelemetryUi();

        }



        private void UpdateThermalTelemetryUi()

        {

            var decision = _lastThermalDecision;



            if (_lblThermalStage != null && !_lblThermalStage.IsDisposed)

            {

                _lblThermalStage.Text = $"STAGE: {decision.StageLabel}   fan floor {Math.Max(decision.CpuFanFloor, decision.GpuFanFloor)}%";

                _lblThermalStage.ForeColor = decision.Stage >= 3

                    ? Color.FromArgb(232, 96, 88)

                    : decision.Stage == 2

                        ? Color.FromArgb(240, 176, 80)

                        : decision.Stage == 1

                            ? Color.FromArgb(240, 214, 120)

                            : SubHeaderColor;

            }



            if (_lblThermalDetail != null && !_lblThermalDetail.IsDisposed)

            {

                string cap = decision.CpuPowerCapWatts.HasValue

                    ? $"CPU cap {decision.CpuPowerCapWatts.Value} W"

                    : "CPU cap unchanged";

                _lblThermalDetail.Text = $"{decision.Reason}. {cap}. Fans {decision.CpuFanFloor}% CPU / {decision.GpuFanFloor}% GPU.";

            }

        }



        private void ApplyThermalSettingsFromUi()

        {

            if (_isInitializing || _suppressThermalUi || _thermalSafety == null) return;



            var profile = new ThermalSafetyProfile

            {

                Enabled = _switchThermalGuard?.Checked ?? _appSettings.AntiThrottleEnabled,

                CpuWarnCelsius = _appSettings.ThermalCpuWarnCelsius,

                CpuCeilingCelsius = _sliderThermalCeiling?.Value ?? _appSettings.ThermalCpuCeilingCelsius,

                CpuCriticalCelsius = Math.Max(_appSettings.ThermalCpuCriticalCelsius, _sliderThermalCeiling?.Value ?? _appSettings.ThermalCpuCeilingCelsius),

                GpuWarnCelsius = _appSettings.ThermalGpuWarnCelsius,

                GpuCeilingCelsius = _appSettings.ThermalGpuCeilingCelsius,

                GpuCriticalCelsius = _appSettings.ThermalGpuCriticalCelsius,

                HysteresisCelsius = _sliderThermalHysteresis?.Value ?? _appSettings.ThermalHysteresisCelsius,

                MaxFanFloorPercent = _appSettings.ThermalMaxFanFloorPercent,

                CpuPowerCapWatts = _appSettings.ThermalCpuPowerCapWatts,

                MinPowerCapWatts = _appSettings.ThermalMinPowerCapWatts,

                CriticalAlertEnabled = _appSettings.ThermalCriticalAlertEnabled

            }.Normalize();



            _thermalSafety.Profile = profile;

            _appSettings.ApplyThermalProfile(profile);

            _appSettings.Save();

            RefreshThermalUi();

        }



        #endregion



        #region Power Mode Switch Guard



        private void InitializePowerModeSwitchGuard()

        {

            int settle = Math.Clamp(_appSettings.PowerModeSettleMs, 100, 3000);

            _wmi.WindowsOverlaySyncEnabled = _appSettings.WindowsOverlaySyncEnabled;



            _powerModeSwitchGuard = new PowerModeSwitchGuard(

                applyHardware: mode => _wmi.TrySetPowerMode(mode, forceRetry: true),

                onApplied: mode => _ = CpuPowerLimitController.Instance?.OnPowerModeChanged(ByteToOperatingMode(mode)),

                minIntervalMs: 900,

                settleMs: settle,

                onBeforeSwitch: () => _telemetryPauseUntilUtc = DateTime.UtcNow.AddMilliseconds(settle + 700));

        }



        #endregion



        #region Dedicated Display, BIOS, and Game Sync Pages

        private void BuildDisplayPage()
        {
            int pad = S(16);
            int gap = S(16);
            int usableW = _pageContainer.Width - pad * 2 - gap - DarkScrollPanel.NativeBarWidth;
            int cardW = usableW / 2;
            int col1X = pad;
            int col2X = pad + cardW + gap;
            int cPad = S(14);
            int cInnerW = cardW - cPad * 2;

            // ==========================================
            // CARD 1: HARDWARE MUX SWITCH (BIOS REBOOT REQUIRED)
            // ==========================================
            var cardGpu = MakeCard(_pnlDisplay, "HARDWARE MUX SWITCH (BIOS REBOOT REQUIRED)", col1X, pad, cardW, S(300));
            MakeLabel(cardGpu, "Physical hardware display multiplexer (MUX) routing in BIOS", cPad, S(40), FontHeaderLight, SubHeaderColor);

            _lblGpuStatusSys = MakeLabel(cardGpu, "Status: Querying BIOS hardware MUX switch...", cPad, S(68), FontBodyBold, ThemeManager.Accent);
            UpdateGpuModeStatusLabel();

            int muxBtnH = S(34);
            int muxGap = S(8);
            int muxY = S(100);
            _btnGpuAuto = MakeButton(cardGpu, "🔄  Automatic (Optimus MUX)", cPad, muxY, cInnerW, muxBtnH);
            muxY += muxBtnH + muxGap;
            _btnGpuHybrid = MakeButton(cardGpu, "🔋  Integrated Only (iGPU MUX)", cPad, muxY, cInnerW, muxBtnH);
            muxY += muxBtnH + muxGap;
            _btnGpuDiscrete = MakeButton(cardGpu, "⚡  Discrete Only (dGPU MUX)", cPad, muxY, cInnerW, muxBtnH);
            muxY += muxBtnH + muxGap + S(6);

            var curGpuMode = _wmi.GetGpuMode() ?? GpuMode.Auto;
            var muxItem = Bios.BiosManager.Instance.Settings.FirstOrDefault(s => s.Name.Contains("Display MUX", StringComparison.OrdinalIgnoreCase));
            if (muxItem != null && !string.IsNullOrEmpty(muxItem.CurrentValue))
            {
                if (muxItem.CurrentValue.Contains("Discrete", StringComparison.OrdinalIgnoreCase)) curGpuMode = GpuMode.dGpuOnly;
                else if (muxItem.CurrentValue.Contains("Optimus", StringComparison.OrdinalIgnoreCase)) curGpuMode = GpuMode.iGpuOnly;
            }
            if (curGpuMode == GpuMode.dGpuOnly) HighlightBtn(_btnGpuDiscrete, ref _activeGpuModeBtn);
            else if (curGpuMode == GpuMode.iGpuOnly) HighlightBtn(_btnGpuHybrid, ref _activeGpuModeBtn);
            else HighlightBtn(_btnGpuAuto, ref _activeGpuModeBtn);

            _btnGpuAuto.Click += (s, e) => ApplyGpuMode(GpuMode.Auto, _btnGpuAuto);
            _btnGpuHybrid.Click += (s, e) => ApplyGpuMode(GpuMode.iGpuOnly, _btnGpuHybrid);
            _btnGpuDiscrete.Click += (s, e) => ApplyGpuMode(GpuMode.dGpuOnly, _btnGpuDiscrete);

            MakeLabel(cardGpu, "Hardware MUX switch changes configure UEFI/BIOS routing and require a system restart to take effect.", cPad, muxY, FontHeaderLight, SubHeaderColor);

            // ==========================================
            // CARD 2: DISPLAY REFRESH RATE & OVERDRIVE
            // ==========================================
            var cardRate = MakeCard(_pnlDisplay, "DISPLAY REFRESH RATE & OVERDRIVE", col2X, pad, cardW, S(300));
            MakeLabel(cardRate, "Panel refresh frequency and liquid crystal response acceleration", cPad, S(40), FontHeaderLight, SubHeaderColor);

            int dispBtnW = (cInnerW - S(8)) / 2;
            _btnDisp60 = MakeButton(cardRate, "60 Hz", cPad, S(74), dispBtnW, S(36));
            _btnDispMax = MakeButton(cardRate, $"{_maxHz} Hz (Maximum)", cPad + dispBtnW + S(8), S(74), dispBtnW, S(36));
            _btnDisp60.Click += (s, e) => ApplyDisplayMode(60, _btnDisp60);
            _btnDispMax.Click += (s, e) => ApplyDisplayMode(_maxHz, _btnDispMax);

            // LCD Overdrive Switch
            _lblOdStatusSys = MakeLabel(cardRate, "LCD Overdrive (Fast 3ms Response Time)", cPad, S(138), FontBody, ThemeManager.TextPrimary);
            _swOdSys = new PredatorSwitch { Location = new Point(cardW - cPad - S(48), S(134)), Size = new Size(S(48), S(26)) };
            _swOdSys.Checked = _switchLcdOverdrive.Checked;
            _swOdSys.CheckedChanged += (s, e) => ApplyLcdOverdrive(_swOdSys.Checked);
            cardRate.Controls.Add(_swOdSys);

            MakeLabel(cardRate, "High refresh rates provide smoother gameplay animations and lower display input latency.", cPad, S(184), FontHeaderLight, SubHeaderColor);

            _pnlDisplay.AutoScrollMinSize = new Size(0, S(440));
        }

        private void BuildGameSyncPage()
        {
            if (_gameSync == null)
            {
                _gameSync = new GameSyncController();
                _gameSync.GameDetected += OnGameDetected;
                _gameSync.GameExited += OnGameExited;
            }
            int pad = S(16);
            int gap = S(16);
            int usableW = _pageContainer.Width - pad * 2 - DarkScrollPanel.NativeBarWidth;
            int cPad = S(14);

            // ==========================================
            // CARD 1: GAME SYNC ENGINE STATUS & ACTIVE DETECTION
            // ==========================================
            var cardStatus = MakeCard(_pnlGameSync, "GAME SYNC ENGINE & AUTOMATION", pad, pad, usableW, S(120));
            MakeLabel(cardStatus, "Automatically triggers custom Acer power modes, fan curves, refresh rates & RGB keyboard profiles when gaming.", cPad, S(40), FontHeaderLight, SubHeaderColor);

            _lblGameSyncStatus = MakeLabel(cardStatus, GetGameSyncStatusText(), cPad, S(74), FontBodyBold, _gameSync.IsEnabled ? ThemeManager.SuccessGreen : SubHeaderColor);

            _switchGameSync = new PredatorToggle
            {
                Location = new Point(usableW - cPad - S(52), S(70)),
                Size = new Size(S(48), S(24)),
                Checked = _gameSync.IsEnabled
            };
            _switchGameSync.CheckedChanged += (s, e) =>
            {
                _gameSync.IsEnabled = _switchGameSync.Checked;
                UpdateGameSyncStatusLabel();
            };
            cardStatus.Controls.Add(_switchGameSync);

            // ==========================================
            // CARD 2: CONFIGURED GAME PROFILES
            // ==========================================
            int cardProfilesTop = pad + S(120) + gap;
            int cardProfilesH = S(580);
            var cardProfiles = MakeCard(_pnlGameSync, "CONFIGURED GAME PROFILES", pad, cardProfilesTop, usableW, cardProfilesH);

            // Action buttons toolbar
            int btnH = S(34);
            int btnY = S(40);
            int btnW = S(140);
            int btnGap = S(8);

            _btnGameSyncAdd = MakeButton(cardProfiles, "➕  Add Profile", cPad, btnY, btnW, btnH);
            _btnGameSyncAdd.Click += (s, e) => OnAddGameSyncProfile();

            _btnGameSyncEdit = MakeButton(cardProfiles, "✏️  Edit Profile", cPad + btnW + btnGap, btnY, btnW, btnH);
            _btnGameSyncEdit.Click += (s, e) => OnEditGameSyncProfile();

            _btnGameSyncDelete = MakeButton(cardProfiles, "🗑️  Delete Profile", cPad + (btnW + btnGap) * 2, btnY, btnW, btnH);
            _btnGameSyncDelete.Click += (s, e) => OnDeleteGameSyncProfile();

            _btnConfigureGames = MakeButton(cardProfiles, "🎮  Advanced Editor", cPad + (btnW + btnGap) * 3, btnY, S(160), btnH);
            _btnConfigureGames.Click += (s, e) =>
            {
                using var form = new GameSyncForm(_gameSync, _maxHz);
                if (!this.Visible || !this.IsHandleCreated || this.WindowState == FormWindowState.Minimized)
                    form.ShowDialog();
                else
                    try { form.ShowDialog(this); } catch { form.ShowDialog(); }
                PopulateGameSyncProfilesList();
            };

            // Profiles ListView
            int listY = S(86);
            int listW = usableW - cPad * 2;
            int listH = cardProfilesH - listY - cPad;

            _lvGameProfiles = new ListView
            {
                Location = new Point(cPad, listY),
                Size = new Size(listW, listH),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = ThemeManager.CardBg,
                ForeColor = ThemeManager.TextPrimary,
                Font = FontBody,
                BorderStyle = BorderStyle.FixedSingle
            };

            _lvGameProfiles.Columns.Add("Game / App Name", S(220));
            _lvGameProfiles.Columns.Add("Executable", S(160));
            _lvGameProfiles.Columns.Add("Power Mode", S(120));
            _lvGameProfiles.Columns.Add("Fan Mode", S(110));
            _lvGameProfiles.Columns.Add("Refresh Rate", S(110));
            _lvGameProfiles.Columns.Add("RGB Profile", S(130));
            _lvGameProfiles.DoubleClick += (s, e) => OnEditGameSyncProfile();

            cardProfiles.Controls.Add(_lvGameProfiles);

            PopulateGameSyncProfilesList();

            _pnlGameSync.AutoScrollMinSize = new Size(0, cardProfilesTop + cardProfilesH + pad * 2);
        }

        private string GetGameSyncStatusText()
        {
            if (_gameSync == null || !_gameSync.IsEnabled) return "⚪ Game Sync Disabled";
            if (!string.IsNullOrEmpty(_gameSync.ActiveGameExe))
                return $"🟢 Active Game Detected: {_gameSync.ActiveGameExe}";
            return "🟢 Game Sync Active (Monitoring Foreground Games)";
        }

        private void UpdateGameSyncStatusLabel()
        {
            if (_lblGameSyncStatus == null) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(UpdateGameSyncStatusLabel);
                return;
            }
            _lblGameSyncStatus.Text = GetGameSyncStatusText();
            _lblGameSyncStatus.ForeColor = (_gameSync != null && _gameSync.IsEnabled) ? ThemeManager.SuccessGreen : SubHeaderColor;
        }

        private void PopulateGameSyncProfilesList()
        {
            if (_lvGameProfiles == null || _gameSync == null) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(PopulateGameSyncProfilesList);
                return;
            }

            _lvGameProfiles.BeginUpdate();
            _lvGameProfiles.Items.Clear();

            foreach (var p in _gameSync.Profiles)
            {
                string pwrName = p.PowerMode switch
                {
                    0x00 => "Quiet",
                    0x01 => "Balanced",
                    0x04 => "Performance",
                    0x05 => "Turbo",
                    0x06 => "Eco",
                    _ => "Balanced"
                };
                string fanName = p.FanMode switch
                {
                    0x01 => "Auto",
                    0x02 => "Max",
                    0x03 => "Custom",
                    _ => "Auto"
                };
                string hzName = p.RefreshRate > 0 ? $"{p.RefreshRate} Hz" : "Don't Change";
                string rgbName = p.RgbMode switch
                {
                    1 => "Static",
                    2 => "Breathing",
                    3 => "Neon",
                    4 => "Wave",
                    5 => "Shifting",
                    6 => "Zoom",
                    7 => "Meteor",
                    _ => "Don't Change"
                };

                var item = new ListViewItem(string.IsNullOrEmpty(p.DisplayName) ? p.ExecutableName : p.DisplayName);
                item.SubItems.Add(p.ExecutableName);
                item.SubItems.Add(pwrName);
                item.SubItems.Add(fanName);
                item.SubItems.Add(hzName);
                item.SubItems.Add(rgbName);
                item.Tag = p;

                if (string.Equals(_gameSync.ActiveGameExe, p.ExecutableName, StringComparison.OrdinalIgnoreCase))
                {
                    item.BackColor = Color.FromArgb(0, 60, 40);
                    item.ForeColor = ThemeManager.Accent;
                }

                _lvGameProfiles.Items.Add(item);
            }

            _lvGameProfiles.EndUpdate();
        }

        private void OnAddGameSyncProfile()
        {
            try
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Select Game Executable",
                    Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*",
                    CheckFileExists = true
                };
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    string exeName = System.IO.Path.GetFileName(ofd.FileName);
                    string displayName = System.IO.Path.GetFileNameWithoutExtension(ofd.FileName);

                    if (_gameSync.Profiles.Any(p => p.ExecutableName.Equals(exeName, StringComparison.OrdinalIgnoreCase)))
                    {
                        MessageBox.Show(this, $"Profile for '{exeName}' already exists.", "Game Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    var newProfile = new GameProfile
                    {
                        ExecutableName = exeName,
                        ExecutablePath = ofd.FileName,
                        DisplayName = displayName,
                        PowerMode = 0x01,
                        FanMode = 0x01,
                        RefreshRate = _maxHz,
                        RgbMode = -1
                    };
                    _gameSync.AddProfile(newProfile);
                    PopulateGameSyncProfilesList();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to add game profile: {ex.Message}", "Game Sync Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnEditGameSyncProfile()
        {
            if (_lvGameProfiles == null || _lvGameProfiles.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "Please select a game profile to edit.", "Edit Game Profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var form = new GameSyncForm(_gameSync, _maxHz);
            if (!this.Visible || !this.IsHandleCreated || this.WindowState == FormWindowState.Minimized)
                form.ShowDialog();
            else
                try { form.ShowDialog(this); } catch { form.ShowDialog(); }
            PopulateGameSyncProfilesList();
        }

        private void OnDeleteGameSyncProfile()
        {
            if (_lvGameProfiles == null || _lvGameProfiles.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "Please select a game profile to delete.", "Delete Game Profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedItem = _lvGameProfiles.SelectedItems[0];
            if (selectedItem.Tag is GameProfile profile)
            {
                var res = MessageBox.Show(this, $"Are you sure you want to delete profile for '{profile.DisplayName}' ({profile.ExecutableName})?",
                    "Confirm Delete Profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (res == DialogResult.Yes)
                {
                    _gameSync.RemoveProfile(profile.ExecutableName);
                    PopulateGameSyncProfilesList();
                }
            }
        }

        #endregion

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

            // ==========================================
            // COLUMN 1: AUTOMATIC POWER PROFILES & MODE KEY
            // ==========================================
            var cardPowerAuto = MakeCard(_pnlSystem, "AUTOMATIC POWER PROFILES & MODE KEY", col1X, pad, cardW, S(250));
            int pDropW = (cInnerW - S(10)) / 2;
            _lblAcProfileHdr = MakeLabel(cardPowerAuto, "ON AC POWER:", cPad, S(44), FontBody, SubHeaderColor);
            _lblBatteryProfileHdr = MakeLabel(cardPowerAuto, "ON BATTERY:", cPad + pDropW + S(10), S(44), FontBody, SubHeaderColor);

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

            MakeLabel(cardPowerAuto, "PHYSICAL MODE KEY ACTION:", cPad, S(112), FontBody, SubHeaderColor);
            _cboModeKeyActionSys = new PredatorDropDown
            {
                Location = new Point(cPad, S(136)),
                Size = new Size(cInnerW, S(30))
            };
            _cboModeKeyActionSys.Items.AddRange(new[] { "Cycle Power Modes", "Toggle Turbo Mode (Return to Previous)" });
            _cboModeKeyActionSys.SelectedIndex = _modeKeyAction;
            _cboModeKeyActionSys.SelectedIndexChanged += (s, e) =>
            {
                if (_isInitializing || _isUpdatingModeKeyAction) return;
                try
                {
                    _isUpdatingModeKeyAction = true;
                    _modeKeyAction = _cboModeKeyActionSys.SelectedIndex;
                    if (_cboModeKeyAction != null && _cboModeKeyAction.SelectedIndex != _modeKeyAction)
                        _cboModeKeyAction.SelectedIndex = _modeKeyAction;
                    SaveState("ModeKeyAction", _modeKeyAction);
                }
                finally
                {
                    _isUpdatingModeKeyAction = false;
                }
            };
            cardPowerAuto.Controls.Add(_cboModeKeyActionSys);

            MakeLabel(cardPowerAuto, "Profiles switch automatically on AC/Battery. Mode key cycles or toggles Turbo.", cPad, S(180), FontHeaderLight, SubHeaderColor);

            // ==========================================
            // COLUMN 2: USB CHARGING & BATTERY MAINTENANCE
            // ==========================================
            var cardUsb = MakeCard(_pnlSystem, "USB CHARGING & BATTERY MAINTENANCE", col2X, pad, cardW, S(282));
            MakeLabel(cardUsb, "Charge USB Devices While Laptop is Off", cPad, S(44), FontBody, ThemeManager.TextPrimary);
            _switchUsbCharging = new PredatorSwitch { Location = new Point(cardW - cPad - S(48), S(40)), Size = new Size(S(48), S(26)) };
            var curUsb = _wmi.GetUsbCharging();
            _switchUsbCharging.Checked = curUsb?.Enabled ?? true;
            cardUsb.Controls.Add(_switchUsbCharging);

            MakeLabel(cardUsb, "STOP CHARGING WHEN BATTERY REACHES:", cPad, S(80), FontBody, SubHeaderColor);
            _cboUsbFloor = new PredatorDropDown { Location = new Point(cPad, S(104)), Size = new Size(cInnerW, S(30)) };
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

            _btnBatteryCalibration = MakeButton(cardUsb, "⚡  Start Battery Calibration", cPad, S(152), cInnerW, S(34));
            _btnBatteryCalibration.Click += (s, e) => StartBatteryCalibration();
            _lblLastCalibratedMain = MakeLabel(cardUsb, UiLabelFormatter.FormatLastCalibrated(_appSettings.LastBatteryCalibrationUtcTicks), cPad, S(192), FontBody, SubHeaderColor);

            MakeLabel(cardUsb, "Maintains healthy lithium battery chemistry via hardware threshold limits.", cPad, S(224), FontHeaderLight, SubHeaderColor);

            _pnlSystem.AutoScrollMinSize = new Size(0, S(370));
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

            MakeLabel(cardApp, "APPLICATION THEME:", cPad, S(44), FontBody, SubHeaderColor);

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

                if (_isClosing || IsDisposed) return;

                _suppressStartupToggle = true;
                try
                {
                    _switchStartWithWindows.Checked = !wanted;
                }
                finally
                {
                    _suppressStartupToggle = false;
                }

                MessageBox.Show(this, wanted ? "Could not register Predator Control to start with Windows. Scheduled task could not be created." : "Could not remove startup task.", "Start with Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            };

            cardStartup.Controls.Add(_switchStartWithWindows);

            MakeLabel(cardStartup, "Launches minimized to system tray via scheduled task with no UAC prompt.", cPad, S(92), FontHeaderLight, SubHeaderColor);



            // Card 3: Hardware Diagnostics & Capabilities

            var cardDiag = MakeCard(_pnlSettings, "HARDWARE DIAGNOSTICS & CAPABILITIES", col2X, pad, cardW, S(156));

            _btnDiagnostics = MakeButton(cardDiag, "Compatibility report", cPad, S(44), (cInnerW - S(8)) / 2, S(36));
            _btnDiagnostics.Click += (s, e) =>
            {
                string nativeStatus = PredatorNativeCore.IsNativeCoreAvailable
                    ? PredatorNativeCore.LoadSource : "Unavailable; managed fallback where supported";
                string report = CompatibilityReport.Create(HardwareIdentity.ReadModel(), HardwareIdentity.ReadBiosVersion(),
                    _capabilities, _lightingCapabilities, nativeStatus);
                using var dialog = new CompatibilityDiagnosticsForm(report);
                dialog.ShowDialog(this);
            };
            var btnDetailedDiagnostics = MakeButton(cardDiag, "Copy detailed info", cPad + (cInnerW + S(8)) / 2, S(44), (cInnerW - S(8)) / 2, S(36));
            btnDetailedDiagnostics.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "The detailed report includes your device serial number and other hardware identifiers. Copy it?",
                    "Detailed diagnostics", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                var pStatus = SystemInformation.PowerStatus;
                bool onBatt = _backlightMgr.IsOnBattery(pStatus.PowerLineStatus, pStatus.BatteryChargeStatus);
                bool asleep = onBatt || GpuPowerMonitor.IsGpuAsleep();
                var pdh = _pdhMonitor.Sample(!asleep);
                int? sysRpm = _capabilities.HasThirdFan ? _wmi.SystemFanRpm : null;
                string detailedReport = DiagnosticsDumper.GenerateReport(_wmi, _capabilities, pdh, asleep, sysRpm);
                bool copied = DiagnosticsDumper.CopyToClipboard(detailedReport);
                MessageBox.Show(this, copied ? "Detailed report copied. Review it before sharing." : "Could not copy the report to the clipboard.",
                    "Detailed diagnostics", MessageBoxButtons.OK, copied ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };

            string biosHardwareBadge = HardwareIdentity.GetBiosHardwareBadge();
            MakeLabel(cardDiag, biosHardwareBadge, cPad, S(86), FontHeaderLight, ThemeManager.Accent);

            var btnDisplaySettings = MakeButton(cardDiag, "Display Settings (Hardware MUX)", cPad, S(110), cInnerW, S(32));
            btnDisplaySettings.Click += (s, e) =>
            {
                SwitchPage(_pnlDisplay, _btnNavDisplay);
            };



            // Card 4: Application Updates & About

            var cardUpdates = MakeCard(_pnlSettings, "APPLICATION UPDATES & ABOUT", col2X, pad + S(172), cardW, S(160));

            MakeLabel(cardUpdates, $"Version v.{Updater.CurrentText}", cPad, S(48), FontBodyBold, ThemeManager.TextPrimary);

            _btnCheckUpdates = MakeButton(cardUpdates, "⬇  Check for Updates", cardW - cPad - S(160), S(44), S(160), S(32));

            _btnCheckUpdates.Click += async (s, e) => await CheckForUpdatesAsync();



            MakeLabel(cardUpdates, "SHA-256 verified single-file updater. Zero background network polling.", cPad, S(92), FontHeaderLight, SubHeaderColor);



            // Card 5: Custom Mode Shortcuts (Embedded)
            int shortcutsCardH = S(325);
            var cardShortcuts = MakeCard(_pnlSettings, "CUSTOM MODE SHORTCUTS", col2X, pad + S(344), cardW, shortcutsCardH);

            MakeLabel(cardShortcuts, "Configure custom shortcuts to switch Eco, Quiet, Balanced, Performance, Turbo.", cPad, S(40), FontHeaderLight, SubHeaderColor);

            _shortcutRows.Clear();
            var shortcutsList = _appSettings.CustomModeShortcuts ?? ModeShortcutValidator.CreateDefaultShortcuts();
            var editingList = new List<CustomModeShortcut>();
            foreach (var sc in shortcutsList)
            {
                if (sc != null) editingList.Add(sc.Clone());
            }

            var requiredModes = new (string Name, byte Code, Keys DefaultKey)[]
            {
                ("Eco", 0x06, Keys.D1),
                ("Quiet", 0x00, Keys.D2),
                ("Balanced", 0x01, Keys.D3),
                ("Performance", 0x04, Keys.D4),
                ("Turbo", 0x05, Keys.D5)
            };
            foreach (var req in requiredModes)
            {
                if (!editingList.Any(s => s.ModeCode == req.Code))
                {
                    editingList.Add(new CustomModeShortcut(req.Name, req.Code, req.DefaultKey, ctrl: true, alt: true));
                }
            }

            int rowY = S(68);
            int rowStep = S(36);
            int dotW = S(16), dotH = S(22);
            int modeW = S(94), modeH = S(22);
            int clrBtnW = S(54), recBtnW = S(76), btnH = S(26);

            int clearX = cardW - cPad - clrBtnW;
            int recordX = clearX - S(6) - recBtnW;
            int txtX = cPad + dotW + S(4) + modeW + S(6);
            int txtW = Math.Max(S(90), recordX - S(8) - txtX);

            foreach (var req in requiredModes)
            {
                var shortcut = editingList.FirstOrDefault(s => s.ModeCode == req.Code) ?? new CustomModeShortcut(req.Name, req.Code, req.DefaultKey, ctrl: true, alt: true);

                Color modeColor = shortcut.ModeCode switch
                {
                    0x06 => Color.FromArgb(38, 166, 154),  // Eco (Teal)
                    0x00 => Color.FromArgb(41, 128, 185),  // Quiet (Blue)
                    0x01 => Color.FromArgb(39, 174, 96),   // Balanced (Green)
                    0x04 => Color.FromArgb(230, 126, 34),  // Performance (Orange)
                    0x05 => Color.FromArgb(231, 76, 60),   // Turbo (Red)
                    _ => Color.FromArgb(140, 140, 140)
                };

                var lblBadge = new Label
                {
                    Text = "●",
                    Font = FontShortcutDot,
                    ForeColor = modeColor,
                    BackColor = Color.Transparent,
                    Location = new Point(cPad, rowY + S(2)),
                    Size = new Size(dotW, dotH),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Tag = "custom_color"
                };
                cardShortcuts.Controls.Add(lblBadge);

                var lblMode = new Label
                {
                    Text = $"{shortcut.ModeName} Mode",
                    Font = FontBodyBold,
                    ForeColor = ThemeManager.TextPrimary,
                    BackColor = Color.Transparent,
                    Location = new Point(cPad + dotW + S(4), rowY + S(3)),
                    Size = new Size(modeW, modeH),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                cardShortcuts.Controls.Add(lblMode);

                var txtShortcut = new TextBox
                {
                    Text = shortcut.Enabled ? shortcut.ToString() : "Disabled",
                    ReadOnly = true,
                    BackColor = ThemeManager.WindowBg,
                    ForeColor = shortcut.Enabled ? ThemeManager.TextPrimary : ThemeManager.TextSecondary,
                    TextAlign = HorizontalAlignment.Center,
                    Font = FontBodyBold,
                    BorderStyle = BorderStyle.FixedSingle,
                    Location = new Point(txtX, rowY + S(2)),
                    Size = new Size(txtW, S(24))
                };
                cardShortcuts.Controls.Add(txtShortcut);

                var btnRecord = new PredatorButton { Text = "Record", Location = new Point(recordX, rowY + S(1)), Size = new Size(recBtnW, btnH) };
                var btnClear = new PredatorButton { Text = "Clear", Location = new Point(clearX, rowY + S(1)), Size = new Size(clrBtnW, btnH) };
                cardShortcuts.Controls.Add(btnRecord);
                cardShortcuts.Controls.Add(btnClear);

                var rowControls = new ShortcutRowControls(shortcut, lblBadge, lblMode, txtShortcut, btnRecord, btnClear);
                _shortcutRows.Add(rowControls);

                btnRecord.Click += (s, e) => ToggleShortcutRecording(rowControls);
                btnClear.Click += (s, e) => ClearShortcutRow(rowControls);

                txtShortcut.KeyDown += (s, e) =>
                {
                    if (rowControls.IsRecording)
                    {
                        HandleShortcutKeyRecord(rowControls, e);
                    }
                };

                txtShortcut.LostFocus += (s, e) =>
                {
                    if (rowControls.IsRecording)
                    {
                        StopShortcutRecording(rowControls);
                        SetShortcutStatus("Recording cancelled.", ThemeManager.TextSecondary);
                    }
                };

                txtShortcut.PreviewKeyDown += (s, e) =>
                {
                    if (rowControls.IsRecording)
                    {
                        e.IsInputKey = true;
                    }
                };

                rowY += rowStep;
            }

            _lblShortcutsStatus = new Label
            {
                Text = string.Empty,
                Tag = "custom_color",
                Font = FontHeaderLight,
                ForeColor = ThemeManager.TextSecondary,
                BackColor = Color.Transparent,
                Location = new Point(cPad, S(250)),
                Size = new Size(cInnerW, S(20)),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            cardShortcuts.Controls.Add(_lblShortcutsStatus);

            int actionBtnH = S(32);
            int btnSpacing = S(10);
            int resetBtnW = Math.Max(S(50), (cInnerW - btnSpacing) / 2);
            int saveBtnW = Math.Max(S(50), cInnerW - resetBtnW - btnSpacing);
            int actionY = S(276);

            var btnResetShortcuts = new PredatorButton { Text = "↺  Reset Defaults", Location = new Point(cPad, actionY), Size = new Size(resetBtnW, actionBtnH) };
            btnResetShortcuts.Click += (s, e) => ResetShortcutDefaults();
            cardShortcuts.Controls.Add(btnResetShortcuts);

            var btnSaveShortcuts = new PredatorButton { Text = "💾  Save & Apply", Location = new Point(cPad + resetBtnW + btnSpacing, actionY), Size = new Size(saveBtnW, actionBtnH) };
            btnSaveShortcuts.Click += (s, e) => SaveAndApplyShortcuts();
            cardShortcuts.Controls.Add(btnSaveShortcuts);

            _pnlSettings.AutoScrollMinSize = new Size(0, S(700));

        }



        
        #region Custom Mode Shortcuts Helper Methods

        private void SetShortcutStatus(string text, Color color)
        {
            if (_lblShortcutsStatus == null || _lblShortcutsStatus.IsDisposed) return;
            _lblShortcutsStatus.Text = text;
            _lblShortcutsStatus.ForeColor = color;
        }

        private void ToggleShortcutRecording(ShortcutRowControls row)
        {
            if (row.IsRecording)
            {
                StopShortcutRecording(row);
                SetShortcutStatus("Recording cancelled.", ThemeManager.TextSecondary);
                return;
            }

            foreach (var r in _shortcutRows)
            {
                if (r.IsRecording) StopShortcutRecording(r);
            }

            row.IsRecording = true;
            row.BtnRecord.Text = "Press keys...";
            row.TxtShortcut.Text = "Listening...";
            row.TxtShortcut.ForeColor = ThemeManager.Accent;
            row.TxtShortcut.Focus();
            SetShortcutStatus("Press shortcut keys (e.g. Ctrl+Alt+1). Esc to cancel. F1–F24 blocked.", Color.FromArgb(0, 180, 255));
        }

        private void StopShortcutRecording(ShortcutRowControls row)
        {
            row.IsRecording = false;
            row.BtnRecord.Text = "Record";
            row.TxtShortcut.Text = row.Shortcut.Enabled ? row.Shortcut.ToString() : "Disabled";
            row.TxtShortcut.ForeColor = row.Shortcut.Enabled ? ThemeManager.TextPrimary : ThemeManager.TextSecondary;
        }

        private void ClearShortcutRow(ShortcutRowControls row)
        {
            StopShortcutRecording(row);
            row.Shortcut.Enabled = false;
            row.Shortcut.Key = Keys.None;
            row.Shortcut.Ctrl = false;
            row.Shortcut.Alt = false;
            row.Shortcut.Shift = false;
            row.Shortcut.Win = false;
            row.TxtShortcut.Text = "Disabled";
            row.TxtShortcut.ForeColor = ThemeManager.TextSecondary;
            SetShortcutStatus($"{row.Shortcut.ModeName} Mode shortcut disabled. Click 'Save & Apply' to commit.", ThemeManager.TextSecondary);
        }

        private void HandleShortcutKeyRecord(ShortcutRowControls row, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            Keys key = e.KeyCode;

            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
                return;

            if (key == Keys.Escape)
            {
                StopShortcutRecording(row);
                SetShortcutStatus("Recording cancelled.", ThemeManager.TextSecondary);
                return;
            }

            bool ctrl = e.Control;
            bool alt = e.Alt;
            bool shift = e.Shift;
            bool win = (KeyboardHook.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (KeyboardHook.GetAsyncKeyState(0x5C) & 0x8000) != 0;

            if (!ModeShortcutValidator.IsValid(key, ctrl, alt, shift, win, out string failureReason))
            {
                SetShortcutStatus($"⚠ {failureReason}", Color.FromArgb(255, 80, 80));
                return;
            }

            foreach (var other in _shortcutRows)
            {
                if (other != row && other.Shortcut.Enabled && other.Shortcut.Key == key &&
                    other.Shortcut.Ctrl == ctrl && other.Shortcut.Alt == alt && other.Shortcut.Shift == shift &&
                    other.Shortcut.Win == win)
                {
                    SetShortcutStatus($"⚠ Shortcut already assigned to {other.Shortcut.ModeName} Mode.", Color.FromArgb(255, 80, 80));
                    return;
                }
            }

            row.Shortcut.Key = key;
            row.Shortcut.Ctrl = ctrl;
            row.Shortcut.Alt = alt;
            row.Shortcut.Shift = shift;
            row.Shortcut.Win = win;
            row.Shortcut.Enabled = true;

            StopShortcutRecording(row);
            row.TxtShortcut.Text = row.Shortcut.ToString();
            row.TxtShortcut.ForeColor = ThemeManager.TextPrimary;

            SetShortcutStatus($"✓ Shortcut set to {row.Shortcut}. Click 'Save & Apply' to commit.", Color.FromArgb(46, 204, 113));
        }

        private void ResetShortcutDefaults()
        {
            var defaults = ModeShortcutValidator.CreateDefaultShortcuts();
            foreach (var row in _shortcutRows)
            {
                var def = defaults.FirstOrDefault(d => d.ModeCode == row.Shortcut.ModeCode);
                if (def != null)
                {
                    row.Shortcut.Key = def.Key;
                    row.Shortcut.Ctrl = def.Ctrl;
                    row.Shortcut.Alt = def.Alt;
                    row.Shortcut.Shift = def.Shift;
                    row.Shortcut.Win = def.Win;
                    row.Shortcut.Enabled = def.Enabled;
                }
                StopShortcutRecording(row);
                row.TxtShortcut.Text = row.Shortcut.ToString();
                row.TxtShortcut.ForeColor = row.Shortcut.Enabled ? ThemeManager.TextPrimary : ThemeManager.TextSecondary;
            }
            SetShortcutStatus("Defaults restored (Ctrl+Alt+1..5). Click 'Save & Apply' to commit.", Color.FromArgb(0, 180, 255));
        }

        private void SaveAndApplyShortcuts()
        {
            foreach (var row in _shortcutRows)
            {
                if (row.Shortcut.Enabled && !ModeShortcutValidator.IsValid(row.Shortcut, out string reason))
                {
                    SetShortcutStatus($"⚠ Invalid shortcut on {row.Shortcut.ModeName}: {reason}", Color.FromArgb(255, 80, 80));
                    return;
                }
            }

            foreach (var r in _shortcutRows)
            {
                if (r.IsRecording) StopShortcutRecording(r);
            }

            _appSettings.CustomModeShortcuts = _shortcutRows.Select(r => r.Shortcut.Clone()).ToList();
            _appSettings.Save();
            _keyboardHook?.SetCustomShortcuts(_appSettings.CustomModeShortcuts);

            SetShortcutStatus("✓ Shortcuts saved & applied.", Color.FromArgb(46, 204, 113));
        }

        #endregion

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

                if (card.Width > 1 && card.Height > 1)

                {

                    using var pen = new Pen(ThemeManager.CardBorder, 1f);

                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);

                }

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
            _speedSlider.Enabled = true;
            if (_btnColorPick != null) _btnColorPick.Enabled = true;
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
            _lblModePill.Size = _lblModePill.GetPreferredSize(Size.Empty);

            _lblModePill.ForeColor = GetPowerModeColor(mode);
            if (_lblPowerPill != null && !_lblPowerPill.IsDisposed)
            {
                _lblPowerPill.Left = _lblModePill.Right + S(12);
            }



            UpdatePowerLimitsForMode(mode);

        }



        private void ApplyPowerMode(byte mode, PredatorButton? btn)

        {

            btn ??= PowerByteToBtn(mode);

            HighlightBtn(btn, ref _activePowerBtn);

            _currentPowerMode = mode;

            _pendingTargetPowerMode = mode;

            _powerModeTransitionLockUntilUtc = DateTime.UtcNow.AddMilliseconds(PowerModeTransitionLockMs);

            _wmiHotkeyWatcher?.SuppressModeKeyUntil(_powerModeTransitionLockUntilUtc);

            UpdatePowerLimitsForMode(mode);



            if (!_isGameSyncOverriding)

            {

                SaveState("Power", mode);

                SaveStateToHklm("Power", mode);



                var power = SystemInformation.PowerStatus;

                bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;

                bool isAc = power.PowerLineStatus == PowerLineStatus.Online || isCharging || (power.PowerLineStatus != PowerLineStatus.Offline && _isPluggedIn == true);

                bool onBattery = !isAc;

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

            if (_appSettings.IsZoneLightingActive)
            {
                var scope = (_zoneLayoutMode == 0 && _lightingCapabilities?.PerKeySupported == true)
                    ? KeyboardLightingScope.PerKey
                    : KeyboardLightingScope.Zones;
                _ = _lightingController?.ApplyAsync(scope);
            }
            else if (_rgbDropDown != null && _rgbDropDown.SelectedIndex == (int)LightingEffect34.FollowOperatingMode)
            {
                ApplyRgbModeFromDropdown(_rgbDropDown.SelectedIndex);
            }

            ScheduleRgbRestoreWatchdog();

            UpdateFanLockStatus();

            UpdateDisplayRefreshRateButtons(GetCurrentRefreshRate());



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

            var guard = _powerModeSwitchGuard;

            if (guard != null && _appSettings.SmoothPowerModeSwitching)

            {

                // Latest-wins: the guard performs exactly one EC/WMI mode write,

                // spaces writes out, and pauses telemetry while the platform

                // renegotiates USB and radio power policy.

                guard.Request(mode);

                return;

            }



            lock (_powerModeQueueLock)

            {

                _pendingHardwarePowerMode = mode;

                if (_isApplyingHardwarePowerMode) return;

                _isApplyingHardwarePowerMode = true;

            }



            Task.Run(() =>

            {

                try

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

                            _ = CpuPowerLimitController.Instance?.OnPowerModeChanged(ByteToOperatingMode((byte)targetMode));

                        }

                        catch { }

                    }

                }

                finally

                {

                    lock (_powerModeQueueLock)

                    {

                        _isApplyingHardwarePowerMode = false;

                    }

                }

            });

        }



        private void ApplyPowerModeByCode(byte modeCode)

        {

            PredatorButton? btn = modeCode switch

            {

                0x06 => _btnEco,

                0x00 => _btnQuiet,

                0x01 => _btnBalanced,

                0x04 => _btnPerform,

                0x05 => _btnTurbo,

                _ => null

            };

            if (btn != null)

            {

                ApplyPowerMode(modeCode, btn);

            }

        }



        private void HandleModeKeyPress()

        {

            if (_isClosing || IsDisposed) return;

            if (DateTime.UtcNow < _powerModeTransitionLockUntilUtc)

                return;

            if ((DateTime.UtcNow - _lastModeKeyUtc).TotalMilliseconds < 350)

                return;

            _lastModeKeyUtc = DateTime.UtcNow;

            _powerModeTransitionLockUntilUtc = DateTime.UtcNow.AddMilliseconds(PowerModeTransitionLockMs);

            _wmiHotkeyWatcher?.SuppressModeKeyUntil(_powerModeTransitionLockUntilUtc);



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

            else if (lockState == FanLock.TurboMode)

            {

                _lblFanLockStatus.Text = "🔒 Fan Lock: Locked by Turbo Mode (Max Hardware Speed)";

                _lblFanLockStatus.ForeColor = Color.FromArgb(255, 60, 60);

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



        internal FanLock? GetCurrentFanLock()

        {

            return GetCurrentFanLock(GetCurrentPowerByte());

        }



        internal static FanLock? GetCurrentFanLock(byte currentPower)

        {

            if (currentPower == 0x06) return FanLock.EcoMode;

            return null;

        }



        private async void ApplyGpuMode(GpuMode mode, PredatorButton btn)
        {
            try
            {
                HighlightBtn(btn, ref _activeGpuModeBtn);

                string modeName = mode switch
                {
                    GpuMode.Auto => "Automatic (Optimus MUX)",
                    GpuMode.iGpuOnly => "Integrated Only (iGPU MUX)",
                    GpuMode.dGpuOnly => "Discrete Only (dGPU MUX)",
                    _ => mode.ToString()
                };

                // Check if already in requested active BIOS MUX mode
                GpuMode? curGpu = _wmi.GetGpuMode();
                var biosItem = Bios.BiosManager.Instance.Settings.FirstOrDefault(s => s.Name.Contains("Display MUX", StringComparison.OrdinalIgnoreCase));
                if (!curGpu.HasValue && biosItem != null && !string.IsNullOrEmpty(biosItem.CurrentValue))
                {
                    if (biosItem.CurrentValue.Contains("Discrete", StringComparison.OrdinalIgnoreCase)) curGpu = GpuMode.dGpuOnly;
                    else if (biosItem.CurrentValue.Contains("Optimus", StringComparison.OrdinalIgnoreCase)) curGpu = GpuMode.iGpuOnly;
                    else curGpu = GpuMode.Auto;
                }

                if (curGpu.HasValue && curGpu.Value == mode)
                {
                    UpdateGpuModeStatusLabel($"Status: Already set to {modeName} (Active in BIOS)");
                    return;
                }

                // Apply to BIOS via BiosManager & _wmi
                string biosOption = mode switch
                {
                    GpuMode.Auto => "Auto",
                    GpuMode.iGpuOnly => "Optimus",
                    GpuMode.dGpuOnly => "Discrete GPU only",
                    _ => "Auto"
                };

                if (biosItem != null)
                {
                    biosItem.PendingValue = biosOption;
                    await Task.Run(() => Bios.BiosManager.Instance.ApplySetting(biosItem, out _, bypassSafetyGates: true));
                }

                await Task.Run(() => _wmi.SetGpuMode(mode));

                _appSettings.GpuMode = (int)mode;
                _appSettings.SetBiosSettingValue("Display MUX (Advanced Optimus / dGPU)", biosOption);
                _appSettings.Save();

                UpdateGpuModeStatusLabel($"Status: Hardware MUX set to {modeName} (Reboot Required)");

                var res = MessageBox.Show(this,
                    $"Hardware MUX switch set to {modeName} in BIOS.\r\n\r\nA system restart is required for the display multiplexer to re-route the internal display in firmware. Would you like to restart now?",
                    "Hardware MUX Restart Required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

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

        private void UpdateGpuModeStatusLabel(string? pendingNotice = null)
        {
            if (_lblGpuStatusSys == null) return;
            try
            {
                if (!string.IsNullOrEmpty(pendingNotice))
                {
                    _lblGpuStatusSys.Text = pendingNotice;
                    _lblGpuStatusSys.ForeColor = ThemeManager.YellowStatus;
                    return;
                }

                GpuMode? curGpu = _wmi.GetGpuMode();
                var biosItem = Bios.BiosManager.Instance.Settings.FirstOrDefault(s => s.Name.Contains("Display MUX", StringComparison.OrdinalIgnoreCase));
                if (!curGpu.HasValue && biosItem != null && !string.IsNullOrEmpty(biosItem.CurrentValue))
                {
                    if (biosItem.CurrentValue.Contains("Discrete", StringComparison.OrdinalIgnoreCase)) curGpu = GpuMode.dGpuOnly;
                    else if (biosItem.CurrentValue.Contains("Optimus", StringComparison.OrdinalIgnoreCase)) curGpu = GpuMode.iGpuOnly;
                    else curGpu = GpuMode.Auto;
                }
                curGpu ??= (GpuMode)Math.Clamp(_appSettings.GpuMode, 0, 2);

                _lblGpuStatusSys.Text = curGpu switch
                {
                    GpuMode.dGpuOnly => "Status: Discrete Only (dGPU MUX Active - BIOS)",
                    GpuMode.iGpuOnly => "Status: Integrated Only (iGPU MUX Active - BIOS)",
                    _ => "Status: Automatic (Optimus MUX Active - BIOS)"
                };
                _lblGpuStatusSys.ForeColor = ThemeManager.SuccessGreen;
            }
            catch
            {
                _lblGpuStatusSys.Text = "Status: Hardware MUX Switch Active (BIOS)";
                _lblGpuStatusSys.ForeColor = ThemeManager.Accent;
            }
        }

        internal void ApplyLcdOverdrive(bool enable)
        {
            if (_isUpdatingOverdrive) return;
            try
            {
                _isUpdatingOverdrive = true;
                if (_switchLcdOverdrive != null && _switchLcdOverdrive.Checked != enable)
                    _switchLcdOverdrive.Checked = enable;
                if (_swOdSys != null && _swOdSys.Checked != enable)
                    _swOdSys.Checked = enable;
                if (_trayLcdOverdrive != null)
                    _trayLcdOverdrive.Checked = enable;
                if (_lblLcdOverdriveStatus != null)
                {
                    _lblLcdOverdriveStatus.Text = enable ? "LCD Overdrive (Fast Response Time)" : "LCD Overdrive (Standard)";
                    _lblLcdOverdriveStatus.ForeColor = enable ? AccentColor : Color.White;
                }
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
            }
            finally
            {
                _isUpdatingOverdrive = false;
            }

            // Milestone 8: LCD Overdrive is intentionally decoupled from hardware.

            // The toggle is UI and persisted state only. No WMI method call, EC

            // transaction or display-driver write is issued from here, because

            // overdrive polling froze the function-key row on some Predator board

            // revisions. Re-enabling the hardware path requires a board-specific fix.

        }



        private void ApplyFanMode(byte mode, PredatorButton? btn)

        {

            btn ??= FanByteToBtn(mode);

            HighlightBtn(btn, ref _activeFanBtn);

            if (!_isGameSyncOverriding)

            {

                SaveState("Fan", mode);

                SaveStateToHklm("Fan", mode);



                var power = SystemInformation.PowerStatus;

                bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;

                bool isAc = power.PowerLineStatus == PowerLineStatus.Online || isCharging || (power.PowerLineStatus != PowerLineStatus.Offline && _isPluggedIn == true);

                bool onBattery = !isAc;

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

                    _ = _fanCommandQueue.Enqueue(FanCommandKind.Mode, () =>

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

                _ = _fanCommandQueue.Enqueue(FanCommandKind.Mode, () => _wmi.SetFanBehavior(mode, false));

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

                if (!_fanCurveForm.Visible)
                {
                    if (this.Visible && this.IsHandleCreated && this.WindowState != FormWindowState.Minimized)
                        _fanCurveForm.Show(this);
                    else
                        _fanCurveForm.Show();
                }
                if (_fanCurveForm.WindowState == FormWindowState.Minimized)
                    _fanCurveForm.WindowState = FormWindowState.Normal;
                _fanCurveForm.Activate();
                return;
            }



            _fanCurveForm = new FanCurveForm();

            lock (_curveLock)

            {

                _fanCurveForm.SetCpuCurve(_cpuCurvePoints.ToList());

                _fanCurveForm.SetGpuCurve(_gpuCurvePoints.ToList());

            }

            _fanCurveForm.SetHysteresis(_appSettings.FanHysteresisDegrees);

            _fanCurveForm.UpdateTemps(_cpuTemp, _gpuTemp);



            _fanCurveForm.ApplyClicked += (s, args) =>

            {

                lock (_curveLock)

                {

                    _cpuCurvePoints = args.CpuPoints.Select(p => new Point(p.X, p.Y)).ToList();

                    _gpuCurvePoints = args.GpuPoints.Select(p => new Point(p.X, p.Y)).ToList();

                }

                int newHysteresis = Math.Clamp(args.HysteresisDegrees, CurveFollower.MinHysteresisDegrees, CurveFollower.MaxHysteresisDegrees);

                _appSettings.FanHysteresisDegrees = newHysteresis;

                _cpuCurveFollower.HysteresisDegrees = newHysteresis;

                _gpuCurveFollower.HysteresisDegrees = newHysteresis;

                _sysCurveFollower.HysteresisDegrees = newHysteresis;



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

                SaveState("FanHysteresisDegrees", newHysteresis);

                SaveState("FanCurveEnabled", 1);

                SaveStateToHklm("FanCurveEnabled", 1);

                _appSettings.Save();



                SafeBeginInvoke(() =>

                {

                    if (_sliderEmbeddedHysteresis != null)

                        _sliderEmbeddedHysteresis.Value = newHysteresis;

                    if (_lblEmbeddedHysteresis != null)

                        _lblEmbeddedHysteresis.Text = UiLabelFormatter.FormatHysteresisLabel(newHysteresis);

                    if (_embeddedCurveCpu != null)

                        _embeddedCurveCpu.Points = _cpuCurvePoints;

                    if (_embeddedCurveGpu != null)

                        _embeddedCurveGpu.Points = _gpuCurvePoints;

                });



                ApplyFanCurve(forceHardwareApply: true);

                args.Success = true;

            };



            _fanCurveForm.FormClosed += (s, e) => _fanCurveForm = null;

            if (!this.Visible || !this.IsHandleCreated || this.WindowState == FormWindowState.Minimized)

            {

                _fanCurveForm.Show();

            }

            else

            {

                try { _fanCurveForm.Show(this); } catch { _fanCurveForm.Show(); }

            }

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
                lock (RegistrySafety.RegistryLock)
                {
                    string data = string.Join(";", points.Select(p => $"{p.X},{p.Y}"));
                    using var key = RegistrySafety.CreateSafeSubKey(Registry.CurrentUser, RegistrySafety.BaseKeyPath);
                    key?.SetValue(name, data);
                    key?.Flush();
                }
            }
            catch { }

            try
            {
                lock (RegistrySafety.RegistryLock)
                {
                    string data = string.Join(";", points.Select(p => $"{p.X},{p.Y}"));
                    using var hklmKey = RegistrySafety.CreateSafeSubKey(Registry.LocalMachine, RegistrySafety.BaseKeyPath);
                    hklmKey?.SetValue(name, data);
                    hklmKey?.Flush();
                }
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
                    lock (RegistrySafety.RegistryLock)
                    {
                        using var key = RegistrySafety.OpenSafeSubKey(Registry.CurrentUser, RegistrySafety.BaseKeyPath);
                        data = key?.GetValue(name) as string;
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(data))
                {
                    try
                    {
                        lock (RegistrySafety.RegistryLock)
                        {
                            using var hklmKey = RegistrySafety.OpenSafeSubKey(Registry.LocalMachine, RegistrySafety.BaseKeyPath);
                            data = hklmKey?.GetValue(name) as string;
                        }
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



        internal void UpdateDisplayRefreshRateButtons(int currentHz)

        {

            bool is60 = currentHz <= 60;

            if (_btn60Hz != null) _btn60Hz.IsActive = is60;

            if (_btnMaxHz != null) _btnMaxHz.IsActive = !is60;

            if (_btnDisp60 != null) _btnDisp60.IsActive = is60;

            if (_btnDispMax != null) _btnDispMax.IsActive = !is60;

            _activeDisplayBtn = is60 ? _btn60Hz : _btnMaxHz;

            if (_trayDisplay60 != null && _trayDisplayMax != null)

                CheckTrayItem(is60 ? _trayDisplay60 : _trayDisplayMax, _trayDisplay60, _trayDisplayMax);

        }



        internal void ApplyDisplayMode(int hz, PredatorButton? btn = null)

        {

            if (!SetRefreshRate(hz)) return;

            UpdateDisplayRefreshRateButtons(hz);

            if (!_isInitializing)

            {

                SaveState("RefreshRate", hz);

            }

        }



        private void ApplyBatteryLimit(bool limit)

        {

            if (_appSettings.BatteryCalibrationPhase != 0 && limit) return;

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



        #region Battery Calibration & Wake Lock



        private void BuildCalibrationOverlay()

        {
            try { _tipCalibration?.Dispose(); } catch { }
            _tipCalibration = new ToolTip();

            _pnlCalibrationOverlay = new Panel

            {

                Name = "_pnlCalibrationOverlay",

                Tag = "overlay",

                Visible = false,

                Dock = DockStyle.Fill,

                BackColor = ThemeManager.FormBg

            };

            _pnlCalibrationOverlay.Paint += (s, e) =>

            {

                if (_pnlCalibrationOverlay.Width > 2 && _pnlCalibrationOverlay.Height > 2)

                {

                    using var borderPen = new Pen(ThemeManager.CardBorder, 2f);

                    e.Graphics.DrawRectangle(borderPen, 1, 1, _pnlCalibrationOverlay.Width - 2, _pnlCalibrationOverlay.Height - 2);

                }

            };



            int cardW = Math.Min(S(580), _formW - S(80));

            int cardH = S(410);



            var card = new Panel

            {

                Name = "cardCalibration",

                Tag = "card",

                Size = new Size(cardW, cardH),

                BackColor = ThemeManager.CardBg,

                Anchor = AnchorStyles.None

            };

            card.Location = new Point(Math.Max(0, (_pnlCalibrationOverlay.ClientSize.Width - cardW) / 2),

                                      Math.Max(0, (_pnlCalibrationOverlay.ClientSize.Height - cardH) / 2));

            card.Paint += (s, e) =>

            {

                if (card.Width > 1 && card.Height > 1)

                {

                    using var pen = new Pen(ThemeManager.CardBorder, 1f);

                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);

                }

            };



            _pnlCalibrationOverlay.Resize += (s, e) =>

            {

                card.Location = new Point(Math.Max(0, (_pnlCalibrationOverlay.ClientSize.Width - card.Width) / 2),

                                          Math.Max(0, (_pnlCalibrationOverlay.ClientSize.Height - card.Height) / 2));

            };



            int cPad = S(20);

            int innerW = cardW - cPad * 2;



            _lblCalibrationTitle = new Label

            {

                Text = "⚡ BATTERY CALIBRATION IN PROGRESS",

                Font = FontTitle,

                ForeColor = ThemeManager.HeaderText,

                Location = new Point(cPad, S(16)),

                AutoSize = true,

                UseMnemonic = false

            };

            card.Controls.Add(_lblCalibrationTitle);



            _lblCalibrationAwakeLock = new Label

            {

                Text = "🔒 System Awake Lock: Active",

                Font = FontSectionHeader,

                ForeColor = Color.FromArgb(0x00, 0xCC, 0x88),

                Location = new Point(cPad, S(42)),

                AutoSize = true,

                UseMnemonic = false

            };

            card.Controls.Add(_lblCalibrationAwakeLock);



            // Warning Banner + ToolTip

            _pnlCalibrationWarning = new Panel

            {

                Location = new Point(cPad, S(68)),

                Size = new Size(innerW, S(48)),

                BackColor = Color.FromArgb(45, 35, 15)

            };

            _pnlCalibrationWarning.Paint += (s, e) =>

            {

                if (_pnlCalibrationWarning.Width > 1 && _pnlCalibrationWarning.Height > 1)

                {

                    using var pen = new Pen(Color.FromArgb(230, 160, 30), 1f);

                    e.Graphics.DrawRectangle(pen, 0, 0, _pnlCalibrationWarning.Width - 1, _pnlCalibrationWarning.Height - 1);

                }

            };

            _lblCalibrationWarning = new Label

            {

                Text = "⚠️ Warning: Keep AC adapter connected. Do not close lid or interrupt calibration.",

                Font = FontBody,

                ForeColor = Color.FromArgb(255, 200, 80),

                Dock = DockStyle.Fill,

                TextAlign = ContentAlignment.MiddleCenter,

                UseMnemonic = false

            };

            string warnTip = "Battery calibration cycles through 100% full charge and empty discharge to reset the hardware fuel gauge. Interrupting this process will restore prior settings.";

            _tipCalibration.SetToolTip(_lblCalibrationWarning, warnTip);

            _tipCalibration.SetToolTip(_pnlCalibrationWarning, warnTip);

            _pnlCalibrationWarning.Controls.Add(_lblCalibrationWarning);

            card.Controls.Add(_pnlCalibrationWarning);



            // Phase label

            _lblCalibrationPhase = new Label

            {

                Text = "Phase 1 / 2: Charging to Full (100%)",

                Font = FontTitle,

                ForeColor = ThemeManager.Accent,

                Location = new Point(cPad, S(126)),

                AutoSize = true,

                UseMnemonic = false

            };

            card.Controls.Add(_lblCalibrationPhase);



            // Battery % and Power Rate

            _lblCalibrationBatteryPercent = new Label

            {

                Text = "--%",

                Font = FontCalibrationPercent,

                ForeColor = ThemeManager.TextPrimary,

                Location = new Point(cPad, S(152)),

                AutoSize = true,

                UseMnemonic = false

            };

            card.Controls.Add(_lblCalibrationBatteryPercent);



            _lblCalibrationPowerRate = new Label

            {

                Text = "Power Rate: Measuring...",

                Font = FontBody,

                ForeColor = ThemeManager.TextSecondary,

                Location = new Point(cPad, S(194)),

                AutoSize = true,

                UseMnemonic = false

            };

            card.Controls.Add(_lblCalibrationPowerRate);



            // Progress bar

            _pbCalibration = new ProgressBar

            {

                Location = new Point(cPad, S(220)),

                Size = new Size(innerW, S(18)),

                Minimum = 0,

                Maximum = 100,

                Value = 0,

                Style = ProgressBarStyle.Continuous

            };

            card.Controls.Add(_pbCalibration);



            // ETA label

            _lblCalibrationEta = new Label

            {

                Text = "Calculating remaining time...",

                Font = FontSectionHeader,

                ForeColor = ThemeManager.TextPrimary,

                Location = new Point(cPad, S(248)),

                AutoSize = true,

                UseMnemonic = false

            };

            card.Controls.Add(_lblCalibrationEta);



            // Last Calibrated

            _lblCalibrationLastCalibrated = new Label

            {

                Text = UiLabelFormatter.FormatLastCalibrated(_appSettings.LastBatteryCalibrationUtcTicks),

                Font = FontBody,

                ForeColor = ThemeManager.TextSecondary,

                Location = new Point(cPad, S(276)),

                AutoSize = true,

                UseMnemonic = false

            };

            card.Controls.Add(_lblCalibrationLastCalibrated);



            // Cancel Button

            _btnCancelCalibration = new PredatorButton

            {

                Text = "❌  Cancel Calibration",

                Location = new Point(cPad, S(314)),

                Size = new Size(innerW, S(36)),

                UseMnemonic = false

            };

            _btnCancelCalibration.Click += (s, e) => CancelBatteryCalibration();

            card.Controls.Add(_btnCancelCalibration);



            _pnlCalibrationOverlay.Controls.Add(card);

            _pageContainer.Controls.Add(_pnlCalibrationOverlay);

        }



        private void StartBatteryCalibration()

        {

            var result = MessageBox.Show(this,

                "Battery Calibration will charge the battery to 100%, discharge it completely to calibrate the fuel gauge, and recharge it.\n\n" +

                "• Keep AC adapter connected at all times.\n" +

                "• System sleep and display timeout will be temporarily suspended.\n" +

                "• This process may take several hours.\n\n" +

                "Do you want to start Battery Calibration now?",

                "Start Battery Calibration",

                MessageBoxButtons.YesNo,

                MessageBoxIcon.Question);



            if (result != DialogResult.Yes) return;



            bool preLimit = _switchBatteryLimit != null && _switchBatteryLimit.Checked;

            _appSettings.CalibrationPreChargeLimitEnabled = preLimit;



            if (preLimit)

            {

                if (_switchBatteryLimit != null && _switchBatteryLimit.Checked)

                    _switchBatteryLimit.Checked = false;



                if (_lblBatteryStatus != null)

                    _lblBatteryStatus.Text = "Full Charge (100%)";



                CheckTrayItem(_trayBatteryLimit100, _trayBatteryLimit80, _trayBatteryLimit100);

                SaveState("BatteryLimit", 0);

            }



            PowerWakeLock.Acquire(displayRequired: true, awayMode: false);

            _calibrationPhaseStartTime = DateTime.UtcNow;



            var telemetry = BatteryCalibrationEtaCalculator.QueryTelemetry();

            int currentPct = telemetry.BatteryPercent;

            if (currentPct < 0 || currentPct > 100)

            {

                MessageBox.Show(this,

                    "Unable to read battery status or no battery detected. Please ensure your battery is connected and try again.",

                    "Battery Calibration Unavailable",

                    MessageBoxButtons.OK,

                    MessageBoxIcon.Warning);

                return;

            }

            int initialPhase = currentPct >= 99 ? 2 : 1;



            _appSettings.BatteryCalibrationPhase = initialPhase;

            _appSettings.CalibrationLastKnownPercent = currentPct;

            _appSettings.Save();

            RegistrySafety.SaveCalibrationState(_appSettings.LastBatteryCalibrationUtcTicks, _appSettings.BatteryCalibrationPhase, _appSettings.CalibrationLastKnownPercent, _appSettings.CalibrationPreChargeLimitEnabled);



            var now = DateTime.UtcNow;

            _calibrationEtaCalc.Reset();

            _calibrationEtaCalc.UpdatePercentage(currentPct, now);

            if (telemetry.RemainingCapacity > 0)

            {

                _calibrationEtaCalc.UpdateCapacity(telemetry.RemainingCapacity, now);

            }



            Task.Run(() =>

            {

                try

                {

                    if (preLimit)

                    {

                        _wmi.SetBatteryChargeLimit(false);

                        Thread.Sleep(100);

                    }



                    if (initialPhase == 2)

                    {

                        _wmi.StartBatteryCalibration();

                    }

                }

                catch (Exception ex)

                {

                    Program.Report(ex, false);

                }

            });



            ShowCalibrationOverlay(true);

            UpdateCalibrationOverlayUI(telemetry);

        }



        private void CancelBatteryCalibration(bool force = false)

        {

            if (!force)

            {

                var result = MessageBox.Show(this,

                    "Are you sure you want to cancel Battery Calibration?\n\n" +

                    "The calibration cycle will be stopped and normal charging behavior will be restored.",

                    "Cancel Battery Calibration",

                    MessageBoxButtons.YesNo,

                    MessageBoxIcon.Warning);



                if (result != DialogResult.Yes) return;

            }



            _calibrationPhaseStartTime = default;

            _wmi.StopBatteryCalibration();

            PowerWakeLock.ReleaseAll();



            _appSettings.BatteryCalibrationPhase = 0;

            _appSettings.CalibrationLastKnownPercent = -1;

            _appSettings.Save();

            RegistrySafety.SaveCalibrationState(_appSettings.LastBatteryCalibrationUtcTicks, 0, -1, false);



            if (_appSettings.CalibrationPreChargeLimitEnabled)

            {

                ApplyBatteryLimit(true);

            }



            ShowCalibrationOverlay(false);

            if (_lblLastCalibratedMain != null)

            {

                _lblLastCalibratedMain.Text = UiLabelFormatter.FormatLastCalibrated(_appSettings.LastBatteryCalibrationUtcTicks);

            }

        }



        private void CompleteBatteryCalibration()

        {

            _calibrationPhaseStartTime = default;

            _wmi.StopBatteryCalibration();

            PowerWakeLock.ReleaseAll();



            _appSettings.LastBatteryCalibrationUtcTicks = DateTime.UtcNow.Ticks;

            _appSettings.BatteryCalibrationPhase = 0;

            _appSettings.CalibrationLastKnownPercent = -1;

            _appSettings.Save();

            RegistrySafety.SaveCalibrationState(_appSettings.LastBatteryCalibrationUtcTicks, 0, -1, false);



            if (_appSettings.CalibrationPreChargeLimitEnabled)

            {

                ApplyBatteryLimit(true);

            }



            ShowCalibrationOverlay(false);

            if (_lblLastCalibratedMain != null)

            {

                _lblLastCalibratedMain.Text = UiLabelFormatter.FormatLastCalibrated(_appSettings.LastBatteryCalibrationUtcTicks);

            }



            MessageBox.Show(this,

                "Battery Calibration has completed successfully!\n\n" +

                "The battery fuel gauge has been recalibrated and your previous power settings have been restored.",

                "Battery Calibration Complete",

                MessageBoxButtons.OK,

                MessageBoxIcon.Information);

        }



        private void RecoverInterruptedCalibrationOnStartup()

        {

            try

            {

                int phase = _appSettings.BatteryCalibrationPhase;

                int lastPct = _appSettings.CalibrationLastKnownPercent;



                if (phase == 0) return;



                // Case 1: Post-shutdown cutoff at empty (Phase 2 discharging and shutdown at <= 15% or invalid telemetry on sudden cutoff)

                if (phase == 2 && ((lastPct >= 0 && lastPct <= 15) || lastPct == -1 || lastPct == 255))

                {

                    _wmi.StopBatteryCalibration();

                    PowerWakeLock.ReleaseAll();



                    _appSettings.LastBatteryCalibrationUtcTicks = DateTime.UtcNow.Ticks;

                    _appSettings.BatteryCalibrationPhase = 0;

                    _appSettings.CalibrationLastKnownPercent = -1;

                    _appSettings.Save();

                    RegistrySafety.SaveCalibrationState(_appSettings.LastBatteryCalibrationUtcTicks, 0, -1, false);



                    if (_appSettings.CalibrationPreChargeLimitEnabled)

                    {

                        ApplyBatteryLimit(true);

                    }



                    if (_lblLastCalibratedMain != null)

                    {

                        _lblLastCalibratedMain.Text = UiLabelFormatter.FormatLastCalibrated(_appSettings.LastBatteryCalibrationUtcTicks);

                    }

                }

                // Case 2: Interrupted at high percent or system crash/reboot

                else

                {

                    bool isHardwareCalibrating = false;

                    try

                    {

                        var status = _wmi.GetBatteryCalibrationStatus();

                        isHardwareCalibrating = status != null && status.CalibrationOn;

                    }

                    catch { }



                    if (isHardwareCalibrating)

                    {

                        _calibrationPhaseStartTime = DateTime.UtcNow;

                        PowerWakeLock.Acquire(displayRequired: true, awayMode: false);

                        ShowCalibrationOverlay(true);

                    }

                    else

                    {

                        _wmi.StopBatteryCalibration();

                        PowerWakeLock.ReleaseAll();



                        _appSettings.BatteryCalibrationPhase = 0;

                        _appSettings.CalibrationLastKnownPercent = -1;

                        _appSettings.Save();

                        RegistrySafety.SaveCalibrationState(_appSettings.LastBatteryCalibrationUtcTicks, 0, -1, false);



                        if (_appSettings.CalibrationPreChargeLimitEnabled)

                        {

                            ApplyBatteryLimit(true);

                        }

                    }

                }

            }

            catch (Exception ex)

            {

                Program.Report(ex, false);

            }

        }



        private void ShowCalibrationOverlay(bool show)

        {

            if (_pnlCalibrationOverlay != null)

            {

                _pnlCalibrationOverlay.Visible = show;

                if (show)

                {

                    _pnlCalibrationOverlay.BringToFront();

                }

            }

        }



        private void UpdateCalibrationOverlayTick()

        {

            if (_isClosing || IsDisposed) return;

            if (InvokeRequired)

            {

                SafeBeginInvoke(UpdateCalibrationOverlayTick);

                return;

            }

            try

            {

                if (!PowerWakeLock.IsLocked)

                {

                    PowerWakeLock.Acquire(displayRequired: true, awayMode: false);

                }



                var snap = BatteryCalibrationEtaCalculator.QueryTelemetry();

                int currentPct = snap.BatteryPercent;



                // Guard against invalid battery percent readings (-1 or 255)

                if (currentPct < 0 || currentPct > 100)

                {

                    _consecutiveInvalidBatteryReadings++;

                    if (_consecutiveInvalidBatteryReadings > 30) // ~60 seconds of invalid telemetry

                    {

                        CancelBatteryCalibration(force: true);

                        return;

                    }

                    UpdateCalibrationOverlayUI(snap);

                    return;

                }



                _consecutiveInvalidBatteryReadings = 0;

                _appSettings.CalibrationLastKnownPercent = currentPct;



                var now = DateTime.UtcNow;

                if (_calibrationPhaseStartTime == default)

                {

                    _calibrationPhaseStartTime = now;

                }

                else if ((now - _calibrationPhaseStartTime) > TimeSpan.FromHours(12))

                {

                    CancelBatteryCalibration(force: true);

                    return;

                }



                _calibrationEtaCalc.UpdatePercentage(currentPct, now);

                if (snap.RemainingCapacity > 0)

                {

                    _calibrationEtaCalc.UpdateCapacity(snap.RemainingCapacity, now);

                }



                if (_appSettings.BatteryCalibrationPhase == 1)

                {

                    if (currentPct <= 100 && (currentPct >= 100 || (currentPct >= 99 && snap.FullChargedCapacity > 0 && snap.RemainingCapacity >= snap.FullChargedCapacity)))

                    {

                        _appSettings.BatteryCalibrationPhase = 2;

                        _calibrationPhaseStartTime = now;

                        _calibrationEtaCalc.Reset();

                        _calibrationEtaCalc.UpdatePercentage(currentPct, now);

                        if (snap.RemainingCapacity > 0)

                        {

                            _calibrationEtaCalc.UpdateCapacity(snap.RemainingCapacity, now);

                        }

                        Task.Run(() => _wmi.StartBatteryCalibration());

                        RegistrySafety.SaveCalibrationState(_appSettings.LastBatteryCalibrationUtcTicks, _appSettings.BatteryCalibrationPhase, _appSettings.CalibrationLastKnownPercent, _appSettings.CalibrationPreChargeLimitEnabled);

                    }

                }

                else if (_appSettings.BatteryCalibrationPhase == 2)

                {

                    if (currentPct >= 0 && (currentPct <= 3 || (currentPct <= 5 && snap.IsCharging)))

                    {

                        CompleteBatteryCalibration();

                        return;

                    }

                }



                RegistrySafety.SaveCalibrationState(_appSettings.LastBatteryCalibrationUtcTicks, _appSettings.BatteryCalibrationPhase, _appSettings.CalibrationLastKnownPercent, _appSettings.CalibrationPreChargeLimitEnabled);



                UpdateCalibrationOverlayUI(snap);

            }

            catch (Exception ex)

            {

                Program.Report(ex, false);

            }

        }



        private void UpdateCalibrationOverlayUI(BatteryTelemetrySnapshot snap)

        {

            if (_pnlCalibrationOverlay == null || !_pnlCalibrationOverlay.Visible) return;



            bool isChargingPhase = _appSettings.BatteryCalibrationPhase == 1;

            double rate = isChargingPhase ? snap.ChargeRate : snap.DischargeRate;



            var eta = _calibrationEtaCalc.CalculateEta(

                _appSettings.BatteryCalibrationPhase,

                snap.RemainingCapacity,

                snap.FullChargedCapacity,

                rate > 0 ? rate : null,

                allowWarmupFallback: true,

                batteryPercent: snap.BatteryPercent);



            if (_lblCalibrationPhase != null)

                _lblCalibrationPhase.Text = isChargingPhase ? "Phase 1 / 2: Charging to Full (100%)" : "Phase 2 / 2: Discharging to Empty (0%)";



            if (_lblCalibrationWarning != null)

            {

                if (isChargingPhase)

                {

                    if (!snap.IsPowerOnline)

                    {

                        _lblCalibrationWarning.Text = "⚠️ CRITICAL: AC power disconnected! Reconnect AC charger to continue charging.";

                    }

                    else

                    {

                        _lblCalibrationWarning.Text = "⚠️ Warning: Keep AC adapter connected. Charging to full 100%.";

                    }

                }

                else

                {

                    _lblCalibrationWarning.Text = "⚡ Discharging: Auto-discharge active. If % does not decrease on your model, disconnect AC until empty.";

                }

            }



            if (_lblCalibrationBatteryPercent != null)

                _lblCalibrationBatteryPercent.Text = $"{snap.BatteryPercent}%";



            if (_pbCalibration != null)

                _pbCalibration.Value = Math.Clamp(snap.BatteryPercent, 0, 100);



            if (_lblCalibrationPowerRate != null)

            {

                double? activeRate = rate > 0 ? rate : _calibrationEtaCalc.SmoothedRate;

                if (activeRate.HasValue && activeRate.Value > 0)

                {

                    double r = activeRate.Value;

                    _lblCalibrationPowerRate.Text = r >= 1000

                        ? $"Power Rate: {r / 1000.0:F1} W"

                        : $"Power Rate: {r:F0} mW";

                }

                else if (_calibrationEtaCalc.SmoothedPercentPerMinute.HasValue && _calibrationEtaCalc.SmoothedPercentPerMinute.Value > 0)

                {

                    double pctPerHour = _calibrationEtaCalc.SmoothedPercentPerMinute.Value * 60.0;

                    if (snap.FullChargedCapacity > 100)

                    {

                        double estW = (pctPerHour / 100.0) * snap.FullChargedCapacity / 1000.0;

                        _lblCalibrationPowerRate.Text = $"Power Rate: ~{estW:F1} W";

                    }

                    else

                    {

                        _lblCalibrationPowerRate.Text = $"Rate: ~{_calibrationEtaCalc.SmoothedPercentPerMinute.Value:F2} %/min";

                    }

                }

                else

                {

                    _lblCalibrationPowerRate.Text = isChargingPhase ? "Power Rate: Measuring (~35 W)" : "Power Rate: Measuring (~25 W)";

                }

            }



            if (_lblCalibrationEta != null)

                _lblCalibrationEta.Text = UiLabelFormatter.FormatCalibrationEta(eta, isChargingPhase);



            if (_lblCalibrationLastCalibrated != null)

                _lblCalibrationLastCalibrated.Text = UiLabelFormatter.FormatLastCalibrated(_appSettings.LastBatteryCalibrationUtcTicks);



            if (_lblLastCalibratedMain != null)

                _lblLastCalibratedMain.Text = UiLabelFormatter.FormatLastCalibrated(_appSettings.LastBatteryCalibrationUtcTicks);

        }



                private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            if (_isClosing || IsDisposed) return;
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon)
            {
                _ = RestoreSavedKeyboardLightingWithBootRetryAsync(_shutdownCts.Token);
            }
        }

        private void OnSessionEnding(object? sender, SessionEndingEventArgs e)

        {

            try
            {
                PersistLightingState(immediate: true);
                _appSettings?.Save();
            }
            catch { }

            try

            {

                if (_appSettings != null && _appSettings.BatteryCalibrationPhase != 0)

                {

                    var p = SystemInformation.PowerStatus;

                    int pct = (int)Math.Round(Math.Clamp(p.BatteryLifePercent, 0f, 1f) * 100.0);

                    _appSettings.CalibrationLastKnownPercent = pct;

                    RegistrySafety.SaveCalibrationState(_appSettings.LastBatteryCalibrationUtcTicks, _appSettings.BatteryCalibrationPhase, _appSettings.CalibrationLastKnownPercent, _appSettings.CalibrationPreChargeLimitEnabled);

                }

            }

            catch { }

        }



        #endregion



        private void SetZonePickersEnabled(bool enabled)
        {
            foreach (var btn in _zoneButtons)
            {
                if (btn != null && !btn.IsDisposed) btn.Enabled = enabled;
            }
            foreach (var pBtn in _zonePaletteButtons)
            {
                if (pBtn != null && !pBtn.IsDisposed) pBtn.Enabled = enabled;
            }
            if (_btnZonesApply != null && !_btnZonesApply.IsDisposed) _btnZonesApply.Enabled = enabled;
        }

        private void ApplyStaticZoneColorsToHardware()
        {
            Color z1 = Color.FromArgb(_appSettings.StaticZone1ColorArgb);
            Color z2 = Color.FromArgb(_appSettings.StaticZone2ColorArgb);
            Color z3 = Color.FromArgb(_appSettings.StaticZone3ColorArgb);
            Color z4 = Color.FromArgb(_appSettings.StaticZone4ColorArgb);

            if (_lightingController != null)
            {
                _lightingController.SetZoneColor(0, z1);
                _lightingController.SetZoneColor(1, z2);
                _lightingController.SetZoneColor(2, z3);
                _lightingController.SetZoneColor(3, z4);
                var scope = (_zoneLayoutMode == 0 && _lightingCapabilities?.PerKeySupported == true)
                    ? KeyboardLightingScope.PerKey
                    : KeyboardLightingScope.Zones;
                _lightingController.Brightness = _appSettings.KeyboardBrightnessLevel;
                _ = _lightingController.ApplyAsync(scope);
            }
            else if (_wmi != null)
            {
                int bright = _appSettings.KeyboardBrightnessLevel;
                Task.Run(() => _wmi.SetZoneColors(new[] { z1, z2, z3, z4 }, bright));
            }
            RefreshZoneButtons();
        }

        private void ApplyRgbModeFromDropdown(int mode)
        {
            if (_isApplyingRgbMode) return;
            _isApplyingRgbMode = true;

            try
            {
                if (mode == 0)
                {
                    LightingEffectsManager.StopSoftwareAnimation();
                    SetZonePickersEnabled(true);
                    _appSettings.DynamicLightingEffectName = "Off";
                    _appSettings.IsZoneLightingActive = true;
                    _appSettings.ActiveLightingPreset = "Custom";
                    _appSettings.Save();

                    ApplyStaticZoneColorsToHardware();

                    if (_rgbDropDown != null && _rgbDropDown.SelectedIndex != 0)
                    {
                        _rgbDropDown.SelectedIndex = 0;
                    }

                    SaveState("RGB_Mode", 0);
                    UpdateRgbControls(0);
                    CheckRgbTrayFromMode(0);
                    return;
                }

                _appSettings.IsZoneLightingActive = false;
                _appSettings.ActiveLightingPreset = "Dynamic";
                if (mode > 0 && mode < RgbModeNames.Length)
                {
                    _appSettings.DynamicLightingEffectName = RgbModeNames[mode];
                }
                _appSettings.Save();

                int brightVal = _brightnessSlider.Value;
                if (brightVal == 0)
                {
                    brightVal = _appSettings.KeyboardBrightnessLevel > 0 ? _appSettings.KeyboardBrightnessLevel : (_backlightMgr.SavedAcBrightness > 0 ? _backlightMgr.SavedAcBrightness : 100);
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

                Color dynColor = Color.FromArgb(_appSettings.DynamicEffectPrimaryColorArgb);
                if (dynColor.IsEmpty || (dynColor.R == 0 && dynColor.G == 0 && dynColor.B == 0))
                {
                    dynColor = _colorPicker.Color;
                    if (dynColor.IsEmpty || (dynColor.R == 0 && dynColor.G == 0 && dynColor.B == 0))
                        dynColor = Color.FromArgb(0, 150, 255);
                }

                byte r = dynColor.R;
                byte g = dynColor.G;
                byte b = dynColor.B;
                byte direction = (byte)Math.Clamp(_appSettings.DynamicEffectDirection, 0, 1);

                Task.Run(() => LightingEffectsManager.ApplyEffect(mode, _wmi, r, g, b, (byte)bright, (byte)speed, direction, (OperatingMode)_currentPowerMode));

                if (_rgbDropDown != null && mode >= 0 && mode < _rgbDropDown.Items.Count)
                {
                    if (_rgbDropDown.SelectedIndex != mode)
                        _rgbDropDown.SelectedIndex = mode;
                }

                SaveState("RGB_Mode", mode);
                SaveState("RGB_R", r); SaveState("RGB_G", g); SaveState("RGB_B", b);

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

                _ => null

            };

            CheckTrayItem(active, _trayRgbStatic, _trayRgbBreathe, _trayRgbNeon, _trayRgbWave,

                          _trayRgbShift, _trayRgbZoom, _trayRgbMeteor);

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

                RgbMode = _rgbDropDown.SelectedIndex >= 0 ? _rgbDropDown.SelectedIndex : 0,

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

            if (_lblGameSyncStatus != null) _lblGameSyncStatus.Text = $"Active \u2014 {profile.DisplayName}";
            UpdateGameSyncStatusLabel();
            PopulateGameSyncProfilesList();



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



                _isUpdatingFanUi = true;

                try

                {

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

                }

                finally

                {

                    _isUpdatingFanUi = false;

                }



                if (cpuSpeed >= 10 || gpuSpeed >= 10 || sysSpeed >= 10)

                {

                    await _fanCommandQueue.Enqueue(FanCommandKind.Speeds, () =>

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

                _isApplyingRgbMode = true;
                try
                {
                    int targetModeIdx = mode >= 0 ? Math.Clamp(mode, 0, _rgbDropDown.Items.Count - 1) : 0;
                    if (_rgbDropDown.SelectedIndex != targetModeIdx)
                        _rgbDropDown.SelectedIndex = targetModeIdx;
                }
                finally
                {
                    _isApplyingRgbMode = false;
                }



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

                    _isUpdatingFanUi = true;

                    try

                    {

                        _isUpdatingFanUi = true;

                        try

                        {

                            _cpuFanSlider.Value = cpuSpeed;

                            _gpuFanSlider.Value = gpuSpeed;

                            if (_sysFanSlider != null) _sysFanSlider.Value = sysSpeed;

                            _lblCpuFanSpeedHdr.Text = $"CPU FAN: {cpuSpeed}%";

                            _lblGpuFanSpeedHdr.Text = $"GPU FAN: {gpuSpeed}%";

                            if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {sysSpeed}%";

                        }

                        finally

                        {

                            _isUpdatingFanUi = false;

                        }

                    }

                    finally

                    {

                        _isUpdatingFanUi = false;

                    }

                    await _fanCommandQueue.Enqueue(FanCommandKind.Speeds, () =>

                    {

                        _wmi.SetCpuFanSpeed((byte)cpuSpeed);

                        _wmi.SetGpuFanSpeed((byte)gpuSpeed);

                        if (_capabilities.HasThirdFan) _wmi.SetSystemFanSpeed((byte)sysSpeed);

                    });

                }

            }



            if (snap.RefreshRate > 0)

                ApplyDisplayMode(snap.RefreshRate, snap.RefreshRate <= 60 ? _btn60Hz : _btnMaxHz);



            if (_appSettings.BatteryCalibrationPhase == 0)

            {

                ApplyBatteryLimit(snap.BatteryLimit == 1);

            }



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

            _isApplyingRgbMode = true;
            try
            {
                int targetSnapIdx = rgbMode >= 0 ? Math.Clamp(rgbMode, 0, _rgbDropDown.Items.Count - 1) : 0;
                if (_rgbDropDown.SelectedIndex != targetSnapIdx)
                    _rgbDropDown.SelectedIndex = targetSnapIdx;
            }
            finally
            {
                _isApplyingRgbMode = false;
            }



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

                            case "FanHysteresisDegrees": _appSettings.FanHysteresisDegrees = value; break;

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

                            case "Brightness":
                                _appSettings.RgbBrightness = value;
                                _appSettings.KeyboardBrightnessLevel = value;
                                break;

                            case "KeyboardBrightnessLevel":
                                _appSettings.KeyboardBrightnessLevel = value;
                                _appSettings.RgbBrightness = value;
                                break;

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
                        lock (RegistrySafety.RegistryLock)
                        {
                            using var key = RegistrySafety.CreateSafeSubKey(Registry.CurrentUser, RegistrySafety.BaseKeyPath);
                            key?.SetValue(name, value);
                            key?.Flush();
                        }
                    }
                    catch { }

                    try
                    {
                        lock (RegistrySafety.RegistryLock)
                        {
                            using var hklmKey = RegistrySafety.CreateSafeSubKey(Registry.LocalMachine, RegistrySafety.BaseKeyPath);
                            hklmKey?.SetValue(name, value);
                            hklmKey?.Flush();
                        }
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

                using var key = RegistrySafety.CreateSafeSubKey(Registry.CurrentUser, RegistrySafety.BaseKeyPath);

                using var hklmKey = RegistrySafety.OpenSafeSubKey(Registry.LocalMachine, RegistrySafety.BaseKeyPath);



                int maxRgb = RgbModeNames.Length - 1;

                int savedRgbMode = settings.RgbMode;

                if (savedRgbMode < 0 || savedRgbMode > maxRgb) savedRgbMode = GetInt(key, "RGB_Mode", -1, 0, maxRgb);

                if (savedRgbMode == -1) savedRgbMode = GetInt(hklmKey, "RGB_Mode", 3, 0, maxRgb);



                int savedBrightness = settings.KeyboardBrightnessLevel;

                int savedSpeed = settings.RgbSpeed;



                _backlightMgr.SavedAcBrightness = Math.Clamp(savedBrightness, 0, 100);

                var initLine = SystemInformation.PowerStatus.PowerLineStatus;

                var initCharge = SystemInformation.PowerStatus.BatteryChargeStatus;

                bool onBatteryStartup = _backlightMgr.IsOnBattery(initLine, initCharge);

                bool isChargingStartup = (initCharge & BatteryChargeStatus.Charging) != 0;

                _isPluggedIn = !onBatteryStartup || isChargingStartup || initLine == PowerLineStatus.Online;
                GpuPowerMonitor.SetPowerState(!_isPluggedIn.GetValueOrDefault());

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



                _isUpdatingFanUi = true;

                try

                {

                    _cpuFanSlider.Value = Math.Clamp(savedFanSpeedCpu, 10, 100);

                    _gpuFanSlider.Value = Math.Clamp(savedFanSpeedGpu, 10, 100);

                    if (_sysFanSlider != null) _sysFanSlider.Value = Math.Clamp(savedFanSpeedSys, 10, 100);

                    _lblCpuFanSpeedHdr.Text = $"CPU FAN: {_cpuFanSlider.Value}%";

                    _lblGpuFanSpeedHdr.Text = $"GPU FAN: {_gpuFanSlider.Value}%";

                    if (_lblSysFanSpeedHdr != null && _sysFanSlider != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {_sysFanSlider.Value}%";

                }

                finally

                {

                    _isUpdatingFanUi = false;

                }



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

                        _ = _fanCommandQueue.Enqueue(FanCommandKind.Mode, () =>

                        {

                            _wmi.SetFanBehavior(0x03, applyCustomSpeeds: true);

                            _wmi.SetCpuFanSpeed((byte)cpuVal);

                            _wmi.SetGpuFanSpeed((byte)gpuVal);

                            if (_capabilities.HasThirdFan && sysVal > 0) _wmi.SetSystemFanSpeed((byte)sysVal);

                        });

                    }

                }



                _appSettings.StaticZone1ColorArgb = settings.StaticZone1ColorArgb;
                _appSettings.StaticZone2ColorArgb = settings.StaticZone2ColorArgb;
                _appSettings.StaticZone3ColorArgb = settings.StaticZone3ColorArgb;
                _appSettings.StaticZone4ColorArgb = settings.StaticZone4ColorArgb;
                _appSettings.DynamicLightingEffectName = settings.DynamicLightingEffectName;
                _appSettings.DynamicEffectPrimaryColorArgb = settings.DynamicEffectPrimaryColorArgb;
                _appSettings.KeyboardBrightnessLevel = settings.KeyboardBrightnessLevel;
                _appSettings.RgbBrightness = settings.KeyboardBrightnessLevel;
                _appSettings.IsZoneLightingActive = settings.IsZoneLightingActive;
                _appSettings.KeyboardZoneColors = settings.KeyboardZoneColors;

                string dynamicEffect = settings.DynamicLightingEffectName;
                if (string.IsNullOrWhiteSpace(dynamicEffect) || string.Equals(dynamicEffect, "Twinkling", StringComparison.OrdinalIgnoreCase))
                {
                    dynamicEffect = "Off";
                    settings.DynamicLightingEffectName = "Off";
                    _appSettings.DynamicLightingEffectName = "Off";
                }

                _suppressUiEvents = true;
                _isInitializingLightingUi = true;
                try
                {
                    UpdateZoneSwatchUiOnly(0, Color.FromArgb(_appSettings.StaticZone1ColorArgb));
                    UpdateZoneSwatchUiOnly(1, Color.FromArgb(_appSettings.StaticZone2ColorArgb));
                    UpdateZoneSwatchUiOnly(2, Color.FromArgb(_appSettings.StaticZone3ColorArgb));
                    UpdateZoneSwatchUiOnly(3, Color.FromArgb(_appSettings.StaticZone4ColorArgb));

                    UpdateDynamicEffectDropdownUiOnly(_appSettings.DynamicLightingEffectName);
                    UpdateDynamicColorSwatchUiOnly(Color.FromArgb(_appSettings.DynamicEffectPrimaryColorArgb));
                    UpdateBrightnessUiOnly(_appSettings.KeyboardBrightnessLevel);
                }
                finally
                {
                    _isInitializingLightingUi = false;
                    _suppressUiEvents = false;
                }

                int targetInitIdx = (_rgbDropDown != null && _rgbDropDown.SelectedIndex >= 0) ? _rgbDropDown.SelectedIndex : 0;

                if (onBatteryStartup)
                {
                    Task.Run(() => _wmi.TurnOffBacklight());
                    EnforceBacklightOffOnBattery();
                }
                else
                {
                    if (string.Equals(settings.DynamicLightingEffectName, "Off", StringComparison.OrdinalIgnoreCase) || targetInitIdx == 0)
                    {
                        LightingEffectsManager.StopSoftwareAnimation();
                        SetZonePickersEnabled(true);
                        Color[] staticColors = new[]
                        {
                            Color.FromArgb(settings.StaticZone1ColorArgb),
                            Color.FromArgb(settings.StaticZone2ColorArgb),
                            Color.FromArgb(settings.StaticZone3ColorArgb),
                            Color.FromArgb(settings.StaticZone4ColorArgb)
                        };

                        if (_lightingController != null)
                        {
                            _lightingController.Brightness = _appSettings.KeyboardBrightnessLevel;
                            var scope = (_zoneLayoutMode == 0 && _lightingCapabilities?.PerKeySupported == true)
                                ? KeyboardLightingScope.PerKey
                                : KeyboardLightingScope.Zones;
                            _ = _lightingController.ApplyAsync(scope);
                        }
                        else if (_wmi != null)
                        {
                            Task.Run(() => _wmi.SetZoneColors(staticColors, _appSettings.KeyboardBrightnessLevel));
                        }
                    }
                    else
                    {
                        ApplyRgbModeFromDropdown(targetInitIdx);
                    }
                }



                bool limitEnabled = settings.BatteryLimit || GetInt(key, "BatteryLimit", 0, 0, 1) == 1;

                ApplyBatteryLimit(limitEnabled);



                Task.Run(() =>

                {

                    try

                    {

                        bool supported = _wmi?.IsBatteryControlSupported() == true;

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



                            SafeBeginInvoke(disableAction);

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

                else

                {

                    UpdateDisplayRefreshRateButtons(GetCurrentRefreshRate());

                }



                bool coolBoost = settings.CoolBoost || GetInt(key, "CoolBoost", 0, 0, 1) == 1;

                if (_switchCoolBoost != null)

                {

                    _switchCoolBoost.Checked = coolBoost;

                }

                Task.Run(() => _wmi?.SetCoolBoost(coolBoost));



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



                // Ensure active profile CPU power limits (PL1/PL2 via PawnIO and Acer ACPI WMI) are immediately applied on startup/boot

                if (settings.EnableCustomPowerLimits || settings.HasCustomPowerLimitsForMode(ByteToOperatingMode(powerMode)))

                {

                    var activeOpMode = ByteToOperatingMode(powerMode);

                    var (targetPl1, targetPl2) = settings.GetEffectiveLimitsForMode(activeOpMode);

                    if (targetPl1 > 0 && targetPl2 > 0)

                    {

                        Task.Run(() =>

                        {

                            try

                            {

                                _wmi?.TrySetPowerMode(powerMode, forceRetry: true);

                                if (CpuPowerLimitController.Instance != null)

                                {

                                    var res = CpuPowerLimitController.Instance.ApplyActiveModePowerLimits(activeOpMode);

                                    SafeBeginInvoke(() =>

                                    {

                                        UpdatePowerLimitStatusUi(res, targetPl1, targetPl2, false);

                                    });

                                }

                            }

                            catch { }

                        });

                    }

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
                else
                {
                    _ = RestoreSavedKeyboardLightingWithBootRetryAsync(_shutdownCts.Token);
                }



                SafeBeginInvoke(() => { _ = ResyncAfterResume(); });

            }

            else if (e.Mode == PowerModes.StatusChange)

            {

                SafeBeginInvoke(() =>

                {

                    if (_isClosing || IsDisposed) return;

                    var power = SystemInformation.PowerStatus;

                    bool isCharging = (power.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;

                    bool isAc = power.PowerLineStatus == PowerLineStatus.Online || isCharging || (power.PowerLineStatus != PowerLineStatus.Offline && _isPluggedIn == true);

                    bool onBattery = !isAc;

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

                _wmi.InvalidateSensorCaches();

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



                // Re-assert PowerWakeLock if active before sleep/standby

                if (PowerWakeLock.IsLocked)

                {

                    PowerWakeLock.Reassert();

                }



                // Re-apply active CPU power limits after sleep/resume as firmware/BIOS resets MSR 0x610 and MMIO RAPL

                try

                {

                    if (CpuPowerLimitController.Instance != null &&

                        (_appSettings.EnableCustomPowerLimits || _appSettings.HardwareHasActiveCustomPowerOverride))

                    {

                        CpuPowerLimitController.Instance.ApplyActiveModePowerLimits();

                    }

                }

                catch (Exception ex)

                {

                    Program.Report(ex, false);

                }



                // Re-apply display refresh rate

                int targetHz = _activeDisplayBtn == _btn60Hz ? 60 : _maxHz;

                if (SetRefreshRate(targetHz))

                {

                    UpdateDisplayRefreshRateButtons(targetHz);

                }



                // Re-apply battery charge limit

                if (_switchBatteryLimit?.Checked == true && _wmi != null)

                {

                    await Task.Run(() => _wmi.SetBatteryChargeLimit(true));

                    if (_isClosing || IsDisposed) return;

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

                            ApplySavedKeyboardLightingFromSettings();

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



        // TelemetrySnapshot defined in TelemetrySnapshot.cs

        private TelemetrySnapshot _latestTelemetrySnapshot;

        private void PushLatestTelemetrySnapshotToUi()
        {
            if (!IsUiRenderActive) return;
            var snap = _latestTelemetrySnapshot;
            _lblCpuTemp.Text = snap.CpuTemp > 0 ? $"{snap.CpuTemp}°C" : "--°C";
            _lblGpuTemp.Text = snap.GpuTemp > 0 ? $"{snap.GpuTemp}°C" : (snap.GpuAsleep || snap.OnBattery ? "Asleep" : "--°C");
            _lblCpuTemp.ForeColor = TempColor(snap.CpuTemp);
            _lblGpuTemp.ForeColor = TempColor(snap.GpuTemp);

            if (_lblGpuSleepBadge != null)
            {
                _lblGpuSleepBadge.Visible = true;
                _lblGpuSleepBadge.Text = (snap.GpuAsleep || snap.OnBattery) ? "[ 💤 Low Power Standby ]" : "[ 🔥 Active ]";
                _lblGpuSleepBadge.ForeColor = (snap.GpuAsleep || snap.OnBattery) ? Color.FromArgb(120, 200, 255) : AccentColor;
            }

            _lblCpuRpm.Text = snap.CpuRpm > 0 ? $"{snap.CpuRpm} RPM" : "-- RPM";
            _lblGpuRpm.Text = snap.GpuRpm > 0 ? $"{snap.GpuRpm} RPM" : "-- RPM";
            if (_lblSysFanRpm != null)
            {
                _lblSysFanRpm.Text = snap.SysRpm > 0 ? $"{snap.SysRpm} RPM" : "-- RPM";
            }

            UpdateThermalTelemetryUi();
            UpdateFanLockStatus();
            UpdatePowerStatusUI();
        }

        private void UpdateTelemetry(object? sender, EventArgs e)

        {

            try { UpdateTelemetryCore(); }

            catch (Exception ex) { Program.Report(ex, false); }

        }



        private void UpdateTelemetryCore()
        {
            if (_isClosing || IsDisposed || Disposing) return;

            if (InvokeRequired)

            {

                SafeBeginInvoke(() => UpdateTelemetryCore());

                return;

            }



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

            bool isAc = lineStatus == PowerLineStatus.Online || isCharging || (lineStatus != PowerLineStatus.Offline && _isPluggedIn == true);

            bool onBattery = !isAc;

            _backlightMgr.SetPluggedInState(isAc);

            _wmi.SuppressGpuQueries = onBattery;
            GpuPowerMonitor.SetPowerState(onBattery);

            bool isConnectedToCharger = isAc;



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
                        Task.Run(() => ApplyCurrentLightingStateAsync(selectedRgbMode, targetBright));

                    }

                }

            }



            if (_appSettings.BatteryCalibrationPhase != 0)

            {

                UpdateCalibrationOverlayTick();

            }



            if (_isSleeping || _isLidClosed)

                return;



            bool isWindowVisible = IsUiRenderActive;



            // A power-mode switch resets USB/PCIe/radio power policy for a short

            // window. Skipping a telemetry pass during that settle avoids the

            // ACPI contention that used to stutter audio and drop Bluetooth.

            if (DateTime.UtcNow < _telemetryPauseUntilUtc)

                return;



            if (Interlocked.CompareExchange(ref _telemetryRunning, 1, 0) != 0)

                return;



            Task.Run(() =>

            {

                var prevPriority = Thread.CurrentThread.Priority;

                try

                {

                    if (_isClosing || IsDisposed) return;

                    // Run telemetry with BelowNormal thread priority so Windows audio engine (AudioDG.exe) and video renderer are never preempted

                    try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }

                    try

                    {

                        OperatingMode currentOpMode = ByteToOperatingMode(_currentPowerMode);

                        CpuPowerLimitController.Instance?.PeriodicVerifyPowerLimits(currentOpMode);

                    }

                    catch { }

                    int cpuTemp = _wmi.CpuTemp;

                    int cpuRpm = isWindowVisible ? _wmi.CpuFanRpm : 0;

                    int sysRpm = isWindowVisible ? _wmi.SystemFanRpm : 0;

                    bool gpuAsleep = onBattery;

                    int gpuTemp = 0;

                    int gpuRpm = 0;

                    double? cpuLoad = null;

                    double? gpuLoad = null;

                    bool queryGpu = !onBattery;

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



                    bool hasHwMode = _wmi.TryGetPowerMode(out byte probedHwMode);



                    SafeBeginInvoke(() =>

                    {

                        try

                        {

                            if (_isClosing || IsDisposed) return;



                            _cpuTemp = cpuTemp;

                            _gpuTemp = gpuTemp;

                            _latestTelemetrySnapshot = new TelemetrySnapshot(cpuTemp, gpuTemp, cpuRpm, gpuRpm, sysRpm, cpuLoad, gpuLoad, gpuAsleep, onBattery, hasHwMode, probedHwMode);



                            // One thermal evaluation per tick, shared by the

                            // badge, the fan floors and the CPU watt cap.

                            _lastThermalDecision = EvaluateThermalSafety();

                            ApplyThermalPowerCap();



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



                            if (hasHwMode)

                            {

                                if (DateTime.UtcNow < _powerModeTransitionLockUntilUtc)

                                {

                                    if (probedHwMode == _pendingTargetPowerMode)

                                    {

                                        _powerModeTransitionLockUntilUtc = DateTime.MinValue;

                                    }

                                }

                                else if (probedHwMode != _currentPowerMode && PowerByteToBtn(probedHwMode) != null)

                                {

                                    ApplyPowerMode(probedHwMode, PowerByteToBtn(probedHwMode));

                                }

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
                                    if (_lastThermalDecision.Active)
                                    {
                                        if (_lblHardwareLinkTitle != null) _lblHardwareLinkTitle.Visible = false;
                                        if (_lblHardwareLinkTitle != null) _lblProtocolBadge.Location = _lblHardwareLinkTitle.Location;
                                        _lblProtocolBadge.Text = _lastThermalDecision.Stage >= 3
                                            ? "THERMAL GUARD: CRITICAL - fans 100%, CPU capped"
                                            : $"THERMAL GUARD: {_lastThermalDecision.StageLabel} - floor {Math.Max(_lastThermalDecision.CpuFanFloor, _lastThermalDecision.GpuFanFloor)}%";
                                        _lblProtocolBadge.ForeColor = _lastThermalDecision.Stage >= 3
                                            ? Color.FromArgb(232, 96, 88)
                                            : Color.FromArgb(240, 190, 90);
                                    }
                                    else
                                    {
                                        if (_lblHardwareLinkTitle != null)
                                        {
                                            _lblHardwareLinkTitle.Visible = true;
                                            int titleW = TextRenderer.MeasureText(_lblHardwareLinkTitle.Text, _lblHardwareLinkTitle.Font).Width;
                                            _lblProtocolBadge.Location = new Point(_lblHardwareLinkTitle.Left + titleW + S(8), _lblHardwareLinkTitle.Top);
                                        }
                                        _lblProtocolBadge.Text = _wmi.EcHid != null && _wmi.EcHid.IsOpen ? "Direct Hardware Controller" : "Acer System Driver";
                                        _lblProtocolBadge.ForeColor = SubHeaderColor;
                                    }
                                }



                                UpdateThermalTelemetryUi();



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

                            try { _trayIcon.Text = trayText.Length > 63 ? trayText.Substring(0, 63) : trayText; } catch { }



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

            bool cpuValid = _cpuTemp > 0;

            bool gpuValid = !onBattery && _gpuTemp > 0 && !GpuPowerMonitor.IsGpuAsleep();



            if (!cpuValid && !gpuValid)

            {

                return;

            }



            bool cpuChanged = false;

            int targetCpuSpeed = 0;

            int currentCpuTemp = 0;



            if (cpuValid)

            {

                int effectiveCpuTemp = Math.Clamp(_cpuTemp, 1, 130);

                // 3-sample rolling average temperature damping

                lock (_dampingLock)

                {

                    _cpuDampingQueue.Enqueue(effectiveCpuTemp);

                    while (_cpuDampingQueue.Count > 3) _cpuDampingQueue.Dequeue();

                    currentCpuTemp = (int)Math.Round(_cpuDampingQueue.Average());

                }



                int cpuSafetyFloor = CurrentThermalFloor(cpu: true);

                targetCpuSpeed = _cpuCurveFollower.Update(currentCpuTemp, cpuPoints, cpuSafetyFloor);

                targetCpuSpeed = Math.Clamp(targetCpuSpeed, 10, 100);

                cpuChanged = forceHardwareApply || (_lastCurveCpuSpeed != targetCpuSpeed);



                if (!_isCpuFanLocked && cpuChanged)

                {

                    _lastCurveCpuSpeed = targetCpuSpeed;

                    _lastCurveCpuTemp = currentCpuTemp;

                    _isUpdatingFanUi = true;

                    try

                    {

                        _cpuFanSlider.Value = targetCpuSpeed;

                    }

                    finally

                    {

                        _isUpdatingFanUi = false;

                    }

                    _lblCpuFanSpeedHdr.Text = $"CPU FAN: {targetCpuSpeed}%";

                }

            }



            bool gpuChanged = false;

            int targetGpuSpeed = 0;

            if (gpuValid)

            {

                int effectiveGpuTemp = Math.Clamp(_gpuTemp, 1, 130);

                int currentGpuTemp;

                lock (_dampingLock)

                {

                    _gpuDampingQueue.Enqueue(effectiveGpuTemp);

                    while (_gpuDampingQueue.Count > 3) _gpuDampingQueue.Dequeue();

                    currentGpuTemp = (int)Math.Round(_gpuDampingQueue.Average());

                }



                int gpuSafetyFloor = CurrentThermalFloor(cpu: false);

                targetGpuSpeed = _gpuCurveFollower.Update(currentGpuTemp, gpuPoints, gpuSafetyFloor);

                targetGpuSpeed = Math.Clamp(targetGpuSpeed, 10, 100);

                gpuChanged = forceHardwareApply || (_lastCurveGpuSpeed != targetGpuSpeed);



                if (!_isGpuFanLocked && gpuChanged)

                {

                    _lastCurveGpuSpeed = targetGpuSpeed;

                    _lastCurveGpuTemp = currentGpuTemp;

                    _isUpdatingFanUi = true;

                    try

                    {

                        _gpuFanSlider.Value = targetGpuSpeed;

                    }

                    finally

                    {

                        _isUpdatingFanUi = false;

                    }

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

                int sysSafetyFloor = CurrentThermalFloor(cpu: true);

                targetSysSpeed = _sysCurveFollower.Update(currentCpuTemp, cpuPoints, sysSafetyFloor);

                targetSysSpeed = Math.Clamp(targetSysSpeed, 10, 100);

                sysChanged = forceHardwareApply || (_lastCurveSysSpeed != targetSysSpeed);



                if (!_isSysFanLocked && sysChanged)

                {

                    _lastCurveSysSpeed = targetSysSpeed;

                    _isUpdatingFanUi = true;

                    try

                    {

                        _sysFanSlider.Value = targetSysSpeed;

                    }

                    finally

                    {

                        _isUpdatingFanUi = false;

                    }

                    if (_lblSysFanSpeedHdr != null) _lblSysFanSpeedHdr.Text = $"SYS FAN: {targetSysSpeed}%";

                }

            }



            if (cpuChanged || gpuChanged || sysChanged || forceHardwareApply)

            {

                int cSpeed = _isCpuFanLocked ? _cpuFanSlider.Value : (cpuChanged ? targetCpuSpeed : _lastCurveCpuSpeed);

                if (cSpeed <= 0) cSpeed = targetCpuSpeed;



                int gSpeed = _isGpuFanLocked ? _gpuFanSlider.Value : (gpuChanged ? targetGpuSpeed : _lastCurveGpuSpeed);

                int sSpeed = _isSysFanLocked ? (_sysFanSlider?.Value ?? 50) : (sysChanged ? targetSysSpeed : _lastCurveSysSpeed);



                _ = _fanCommandQueue.Enqueue(FanCommandKind.Mode, () =>

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

            Point[] pts = Array.Empty<Point>();

            bool copied = false;

            for (int attempt = 0; attempt < 3; attempt++)

            {

                try

                {

                    lock (curve)

                    {

                        pts = curve.ToArray();

                        copied = true;

                        break;

                    }

                }

                catch

                {

                    Thread.SpinWait(10);

                }

            }

            if (!copied || pts.Length == 0) return 50;

            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                var nativePts = new NativeControlPoint[pts.Length];
                for (int i = 0; i < pts.Length; i++)
                    nativePts[i] = new NativeControlPoint(pts[i].X, pts[i].Y);
                return PredatorNativeCore.CurveInterpolate(nativePts, temp);
            }



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

                        _ = _fanCommandQueue.Enqueue(FanCommandKind.Mode, () =>

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



        private void HighlightBtn(PredatorButton? btn, ref PredatorButton? tracker)

        {

            if (tracker != null && !tracker.IsDisposed) tracker.IsActive = false;

            if (btn != null && !btn.IsDisposed) btn.IsActive = true;

            tracker = btn;

        }



        private static void CheckTrayItem(ToolStripMenuItem? active, params ToolStripMenuItem[] group)

        {

            foreach (var item in group) item.Checked = false;

            if (active != null) active.Checked = true;

        }



                private void ForceForeground()
        {
            try
            {
                IntPtr hWnd = this.Handle;
                if (hWnd == IntPtr.Zero) return;

                // 1. Unminimize / restore if iconic
                if (IsIconic(hWnd) || this.WindowState == FormWindowState.Minimized)
                {
                    ShowWindow(hWnd, SW_RESTORE);
                    this.WindowState = FormWindowState.Normal;
                }
                else
                {
                    ShowWindow(hWnd, SW_SHOW);
                }

                // 2. Pop above borderless/fullscreen windows momentarily, then clear topmost so minimize/close work cleanly
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

                // 3. AttachThreadInput trick to bridge foreground rights from active game thread
                IntPtr foreWnd = GetForegroundWindow();
                uint curThread = GetCurrentThreadId();
                uint foreThread = foreWnd != IntPtr.Zero ? GetWindowThreadProcessId(foreWnd, out _) : 0;

                bool attached = false;
                if (foreThread != 0 && foreThread != curThread)
                {
                    attached = AttachThreadInput(curThread, foreThread, true);
                }

                try
                {
                    // 4. Force foreground activation
                    BringWindowToTop(hWnd);
                    SetForegroundWindow(hWnd);
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(curThread, foreThread, false);
                    }
                }

                // Demote from HWND_TOPMOST so window does not permanently trap or block taskbar / minimize
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                this.TopMost = false;

                // 5. Cancel any lingering modal menu state so caption buttons immediately receive clicks
                SendMessage(hWnd, WM_CANCELMODE, 0, 0);
                ReleaseCapture();

                this.BringToFront();
                this.Activate();
                this.Focus();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ForceForeground] Error: {ex.Message}");
            }
        }

        private void ShowApp()
        {
            if (IsDisposed || Disposing || _isClosing) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(ShowApp);
                return;
            }
            _allowVisible = true;
            this.Enabled = true;
            this.Show();

            ForceForeground();

            Updater.ShowPendingNotes(this);
            PushLatestTelemetrySnapshotToUi();
            UpdateTelemetryCore();
        }

        private void HideApp()
        {
            if (IsDisposed || Disposing || _isClosing) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(HideApp);
                return;
            }
            try
            {
                this.TopMost = false;
                if (IsHandleCreated)
                {
                    SetWindowPos(this.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    SendMessage(this.Handle, WM_CANCELMODE, 0, 0);
                    ReleaseCapture();
                }
            }
            catch { }

            // Dismiss any open modal dialogs or child tool windows to prevent modal hangs or Alt-Tab traps
            try
            {
                foreach (Form f in Application.OpenForms.Cast<Form>().ToList())
                {
                    if (f != this && !f.IsDisposed)
                    {
                        if (f.Modal)
                        {
                            try { f.DialogResult = DialogResult.Cancel; f.Close(); } catch { }
                        }
                        else if (f is FanCurveForm || f is HardwareCapabilitiesForm || f is CustomShortcutsForm)
                        {
                            try { f.Hide(); } catch { }
                        }
                        else
                        {
                            try { f.Close(); } catch { }
                        }
                    }
                }
            }
            catch { }

            foreach (var r in _shortcutRows)
            {
                if (r.IsRecording) StopShortcutRecording(r);
            }

            this.Enabled = true;
            this.Hide();
            _allowVisible = false;
            this.WindowState = FormWindowState.Normal;
            TrimWorkingSet(force: true);
        }



        protected override void OnResize(EventArgs e)

        {

            base.OnResize(e);

            if (IsDisposed || Disposing || _isClosing) return;

            if (WindowState == FormWindowState.Minimized)

            {

                TrimWorkingSet();

            }

                        else if (WindowState == FormWindowState.Normal && Visible)
            {
                PushLatestTelemetrySnapshotToUi();
                UpdateTelemetryCore();
            }

        }



        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            float oldDpi = e.DeviceDpiOld > 0 ? e.DeviceDpiOld : 96f;
            float factor = (float)e.DeviceDpiNew / oldDpi;
            _dpiScale = e.DeviceDpiNew / 96f;
            base.OnDpiChanged(e);

            if (Math.Abs(factor - 1.0f) > 0.001f)
            {
                try
                {
                    UpdateDpiFonts();
                    RescaleAllPagesAndChrome(factor);
                }
                catch { }
            }
        }

        private void RescaleAllPagesAndChrome(float factor)
        {
            if (_pnlDashboard != null)
            {
                _pnlDashboard.SetDpiScale(_dpiScale);
                _pnlDashboard.AutoScrollMinSize = new Size(
                    (int)(_pnlDashboard.AutoScrollMinSize.Width * factor),
                    (int)(_pnlDashboard.AutoScrollMinSize.Height * factor));
            }
            if (_pnlFans != null)
            {
                _pnlFans.SetDpiScale(_dpiScale);
                _pnlFans.AutoScrollMinSize = new Size(
                    (int)(_pnlFans.AutoScrollMinSize.Width * factor),
                    (int)(_pnlFans.AutoScrollMinSize.Height * factor));
            }
            if (_pnlLighting != null)
            {
                _pnlLighting.SetDpiScale(_dpiScale);
                _pnlLighting.AutoScrollMinSize = new Size(
                    (int)(_pnlLighting.AutoScrollMinSize.Width * factor),
                    (int)(_pnlLighting.AutoScrollMinSize.Height * factor));
            }
            if (_pnlDisplay != null)
            {
                _pnlDisplay.SetDpiScale(_dpiScale);
                _pnlDisplay.AutoScrollMinSize = new Size(
                    (int)(_pnlDisplay.AutoScrollMinSize.Width * factor),
                    (int)(_pnlDisplay.AutoScrollMinSize.Height * factor));
            }
            if (_pnlGameSync != null)
            {
                _pnlGameSync.SetDpiScale(_dpiScale);
                _pnlGameSync.AutoScrollMinSize = new Size(
                    (int)(_pnlGameSync.AutoScrollMinSize.Width * factor),
                    (int)(_pnlGameSync.AutoScrollMinSize.Height * factor));
            }
            if (_pnlSystem != null)
            {
                _pnlSystem.SetDpiScale(_dpiScale);
                _pnlSystem.AutoScrollMinSize = new Size(
                    (int)(_pnlSystem.AutoScrollMinSize.Width * factor),
                    (int)(_pnlSystem.AutoScrollMinSize.Height * factor));
            }
            if (_pnlSettings != null)
            {
                _pnlSettings.SetDpiScale(_dpiScale);
                _pnlSettings.AutoScrollMinSize = new Size(
                    (int)(_pnlSettings.AutoScrollMinSize.Width * factor),
                    (int)(_pnlSettings.AutoScrollMinSize.Height * factor));
            }
            Invalidate(true);
        }



        private void ToggleWindowVisibility()
        {
            if (IsDisposed || Disposing || _isClosing) return;
            if (InvokeRequired)
            {
                SafeBeginInvoke(ToggleWindowVisibility);
                return;
            }
            if ((DateTime.UtcNow - _lastToggleWindowUtc).TotalMilliseconds < 350)
                return;
            _lastToggleWindowUtc = DateTime.UtcNow;

            IntPtr foreWnd = GetForegroundWindow();
            GetWindowThreadProcessId(foreWnd, out uint forePid);
            uint currentPid = (uint)Process.GetCurrentProcess().Id;

            // Only considered active if this window or an app modal has input focus
            bool isAppActive = (foreWnd == this.Handle) || (forePid == currentPid);

            // Elevate process priority on Predator key activation so app responds instantly over 100% CPU games
            try
            {
                using var currentProc = Process.GetCurrentProcess();
                if (currentProc.PriorityClass != ProcessPriorityClass.High)
                {
                    currentProc.PriorityClass = ProcessPriorityClass.High;
                }
            }
            catch { }

            // If app is already visible but game has focus behind it, bring it to front instead of hiding!
            if (this.Visible && this.WindowState != FormWindowState.Minimized && isAppActive)
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

                try { _shutdownCts.Cancel(); } catch { }



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

                if (_trayIcon != null)

                {

                    try { _trayIcon.Visible = false; } catch { }

                    if (_trayIcon.Icon != null && !ReferenceEquals(_trayIcon.Icon, SystemIcons.Application))

                    {

                        try { _trayIcon.Icon.Dispose(); } catch { }

                    }

                    try { _trayIcon.Dispose(); } catch { }
                    _trayIcon = null!;

                }
                if (this.Icon != null && !ReferenceEquals(this.Icon, SystemIcons.Application))
                {
                    try { this.Icon.Dispose(); } catch { }
                }
                try { _tipCalibration?.Dispose(); _tipCalibration = null; } catch { }

                try { _trayMenu?.Dispose(); } catch { }
                _trayMenu = null!;

                if (_timer != null)

                {

                    try { _timer.Tick -= UpdateTelemetry; } catch { }

                    try { _timer.Stop(); } catch { }

                    try { _timer.Dispose(); } catch { }
                    _timer = null!;

                }



                try { _gameSync.GameDetected -= OnGameDetected; } catch { }

                try { _gameSync.GameExited -= OnGameExited; } catch { }

                try { _gameSync.Dispose(); } catch { }

                try { PersistLightingState(); } catch { }

                try { _lightingController?.Dispose(); } catch { }

                try { _powerModeSwitchGuard?.Dispose(); } catch { }

                try { _wmi.Dispose(); } catch { }

                try { _pdhMonitor.Dispose(); } catch { }

                try { _keyboardHook?.Dispose(); } catch { }

                try { _wmiHotkeyWatcher?.Dispose(); } catch { }

                try { _rawInputWatcher?.Dispose(); } catch { }

                try { _pipeServer?.Dispose(); } catch { }

                try { _colorPicker.Dispose(); } catch { }

                try { _fanCurveForm?.Dispose(); } catch { }

                if (_powerLimitsChangedHandler != null && CpuPowerLimitController.Instance != null)

                {

                    try { CpuPowerLimitController.Instance.PowerLimitsChanged -= _powerLimitsChangedHandler; } catch { }

                    _powerLimitsChangedHandler = null;

                }

                try { CpuPowerLimitController.Instance?.Dispose(); } catch { }

                try { PowerWakeLock.ReleaseAll(); } catch { }
                try { _appSettings.Save(); } catch { }

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

        protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try { _wmi?.TurnOffLogo(); } catch { }
        if (!_startupLightingRestored)
        {
            _startupLightingRestored = true;
            _ = RestoreSavedKeyboardLightingWithBootRetryAsync(_shutdownCts.Token);
        }
        TriggerStartupUpdateCheckAsync();
    }

    

    public async Task RestoreSavedKeyboardLightingWithBootRetryAsync(CancellationToken ct)
    {
        // Pass 1: Immediate application on startup
        SafeBeginInvoke(ApplySavedLightingStateToHardware);

        // Pass 2 & Pass 3: Re-apply at +1500ms and +4000ms after boot to override late Acer EC/WMI resets
        int[] retryDelaysMs = { 1500, 4000 };
        foreach (int delayMs in retryDelaysMs)
        {
            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || _isClosing || IsDisposed) return;
                SafeBeginInvoke(ApplySavedLightingStateToHardware);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch { }
        }
    }

    public void ApplySavedLightingStateToHardware()
    {
        if (_isClosing || IsDisposed) return;
        var settings = _appSettings;
        if (settings == null) return;

        try { _wmi?.TurnOffLogo(); } catch { }

        bool onBattery = false;
        try
        {
            var power = SystemInformation.PowerStatus;
            onBattery = _backlightMgr.IsOnBattery(power.PowerLineStatus, power.BatteryChargeStatus) && !_manualBacklightOnBattery;
        }
        catch { }

        if (onBattery)
        {
            EnforceBacklightOffOnBattery();
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.DynamicLightingEffectName) ||
            settings.DynamicLightingEffectName.Equals("Off", StringComparison.OrdinalIgnoreCase) ||
            settings.DynamicLightingEffectName.Equals("Static", StringComparison.OrdinalIgnoreCase) ||
            settings.IsZoneLightingActive)
        {
            if (_lightingController != null)
            {
                _lightingController.StopDynamicEffect();
                _lightingController.Brightness = settings.KeyboardBrightnessLevel;
                _lightingController.ApplyPerZoneStaticColors(
                    Color.FromArgb(settings.StaticZone1ColorArgb),
                    Color.FromArgb(settings.StaticZone2ColorArgb),
                    Color.FromArgb(settings.StaticZone3ColorArgb),
                    Color.FromArgb(settings.StaticZone4ColorArgb),
                    settings.KeyboardBrightnessLevel);
            }
            else if (_wmi != null)
            {
                Color[] staticColors = new[]
                {
                    Color.FromArgb(settings.StaticZone1ColorArgb),
                    Color.FromArgb(settings.StaticZone2ColorArgb),
                    Color.FromArgb(settings.StaticZone3ColorArgb),
                    Color.FromArgb(settings.StaticZone4ColorArgb)
                };
                int bright = settings.KeyboardBrightnessLevel;
                Task.Run(() => _wmi.SetZoneColors(staticColors, bright));
            }
            RefreshZoneButtons();
        }
        else
        {
            if (_lightingController != null)
            {
                _lightingController.Brightness = settings.KeyboardBrightnessLevel;
                _lightingController.StartDynamicEffect(
                    settings.DynamicLightingEffectName,
                    Color.FromArgb(settings.DynamicEffectPrimaryColorArgb),
                    settings.DynamicEffectSpeed,
                    settings.KeyboardBrightnessLevel);
            }
            else
            {
                int mode = 0;
                for (int i = 1; i < RgbModeNames.Length; i++)
                {
                    if (string.Equals(RgbModeNames[i], settings.DynamicLightingEffectName, StringComparison.OrdinalIgnoreCase))
                    {
                        mode = i;
                        break;
                    }
                }
                if (mode > 0)
                {
                    ApplyRgbModeFromDropdown(mode);
                }
            }
        }
    }

    }
}
