//! Event-Driven Game Profiles Native Engine.
//!
//! Provides zero-polling, Win32 event-driven foreground window tracking via
//! `SetWinEventHook` (`EVENT_SYSTEM_FOREGROUND`). Foreground changes trigger
//! instant process image resolution via `QueryFullProcessImageNameW`.
//!
//! Features:
//! - Strictly event-driven: zero background polling or continuous timer overhead.
//! - Debounced foreground transitions for rapid Alt-Tab switching.
//! - Self-process filtering and system process protection.
//! - In-memory game profile match table with executable name and full path matching.
//! - Process lifecycle monitoring via `RegisterWaitForSingleObject` to detect
//!   process termination instantly without thread polling.
//! - Discrete event queue: `GameStarted`, `GameExited`, `GameSwitched`.

use std::collections::{HashMap, VecDeque};
use std::ffi::c_void;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Mutex;
use std::thread::JoinHandle;

use crate::procsync;

/// Event types emitted to consumers.
pub const EVENT_TYPE_NONE: i32 = 0;
pub const EVENT_TYPE_STARTED: i32 = 1;
pub const EVENT_TYPE_EXITED: i32 = 2;
pub const EVENT_TYPE_SWITCHED: i32 = 3;

/// Debounce window for rapid Alt-Tab switching in milliseconds.
pub const DEBOUNCE_THRESHOLD_MS: u64 = 80;

/// Maximum buffered events in queue to prevent memory leak under load.
pub const MAX_EVENT_QUEUE_CAPACITY: usize = 256;

/// Blocked system and protected executables that must never trigger game profiles.
pub const BLOCKED_EXECUTABLES: &[&str] = &[
    "svchost.exe",
    "csrss.exe",
    "smss.exe",
    "wininit.exe",
    "winlogon.exe",
    "services.exe",
    "lsass.exe",
    "fontdrvhost.exe",
    "dwm.exe",
    "conhost.exe",
    "dllhost.exe",
    "taskhostw.exe",
    "sihost.exe",
    "ctfmon.exe",
    "spoolsv.exe",
    "searchindexer.exe",
    "wmiprvse.exe",
    "runtimebroker.exe",
    "shellexperiencehost.exe",
    "startmenuexperiencehost.exe",
    "textinputhost.exe",
    "securityhealthsystray.exe",
    "securityhealthservice.exe",
    "msmpeng.exe",
    "nissrv.exe",
    "sgrmbroker.exe",
    "audiodg.exe",
    "predatorcontrolapp.exe",
    "system.exe",
    "registry.exe",
    "idle.exe",
    "memory compression.exe",
    "explorer.exe",
    "cmd.exe",
    "powershell.exe",
    "pwsh.exe",
    "taskmgr.exe",
    "rundll32.exe",
    "regsvr32.exe",
    "cscript.exe",
    "wscript.exe",
    "mshta.exe",
    "regedit.exe",
];

/// Checks if an executable name belongs to blocked system processes.
pub fn is_blocked_executable(name: &str) -> bool {
    let exe = procsync::executable_name(name);
    let trimmed = exe.trim();
    if trimmed.is_empty() {
        return true;
    }
    let lower = trimmed.to_ascii_lowercase();
    let with_ext = if lower.ends_with(".exe") {
        lower
    } else {
        format!("{}.exe", lower)
    };

    BLOCKED_EXECUTABLES
        .iter()
        .any(|&blocked| blocked.eq_ignore_ascii_case(&with_ext))
}

/// In-memory registration entry for a game profile.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct GameProfileConfig {
    pub profile_id: u32,
    pub exe_name: String,
    pub full_path: String,
    pub mode: i32,
    pub pl1: u32,
    pub pl2: u32,
    pub fan_mode: i32,
    pub refresh_rate: i32,
    pub dds_mode: i32,
}

/// State machine events.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProfileEvent {
    GameStarted {
        profile_id: u32,
        pid: u32,
    },
    GameExited {
        profile_id: u32,
        pid: u32,
    },
    GameSwitched {
        old_profile_id: u32,
        new_profile_id: u32,
    },
}

impl ProfileEvent {
    pub fn event_type(&self) -> i32 {
        match self {
            ProfileEvent::GameStarted { .. } => EVENT_TYPE_STARTED,
            ProfileEvent::GameExited { .. } => EVENT_TYPE_EXITED,
            ProfileEvent::GameSwitched { .. } => EVENT_TYPE_SWITCHED,
        }
    }
}

/// Active running game tracker.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ActiveGame {
    pub profile_id: u32,
    pub pid: u32,
}

/// Pure state machine & match table for unit testability and determinism.
#[derive(Debug)]
pub struct GameProfileEngine {
    profiles: HashMap<u32, GameProfileConfig>,
    active_game: Option<ActiveGame>,
    event_queue: VecDeque<ProfileEvent>,
    last_event_tick: u64,
    last_handled_pid: u32,
    mock_own_pid: Option<u32>,
}

impl Default for GameProfileEngine {
    fn default() -> Self {
        Self::new()
    }
}

impl GameProfileEngine {
    pub fn new() -> Self {
        Self {
            profiles: HashMap::new(),
            active_game: None,
            event_queue: VecDeque::with_capacity(32),
            last_event_tick: 0,
            last_handled_pid: 0,
            mock_own_pid: None,
        }
    }

    /// Set an explicit mock PID to treat as own process (for testing self-ignore).
    pub fn set_mock_own_pid(&mut self, pid: Option<u32>) {
        self.mock_own_pid = pid;
    }

    fn own_pid(&self) -> u32 {
        if let Some(pid) = self.mock_own_pid {
            pid
        } else {
            unsafe { GetCurrentProcessId() }
        }
    }

    /// Registers a profile. Replaces any existing profile with the same ID.
    pub fn register(&mut self, config: GameProfileConfig) -> Result<(), &'static str> {
        if config.profile_id == 0 {
            return Err("Profile ID must be greater than 0");
        }
        let exe = config.exe_name.trim();
        if exe.is_empty() {
            return Err("Executable name cannot be empty");
        }
        if exe.len() > 260 {
            return Err("Executable name exceeds 260 characters");
        }
        if is_blocked_executable(exe) {
            return Err("Targeting system or protected executable is prohibited");
        }

        self.profiles.insert(config.profile_id, config);
        Ok(())
    }

    /// Unregisters a profile by ID.
    pub fn unregister(&mut self, profile_id: u32) -> bool {
        let removed = self.profiles.remove(&profile_id).is_some();
        if let Some(active) = self.active_game {
            if active.profile_id == profile_id {
                self.active_game = None;
            }
        }
        removed
    }

    /// Clears all registered profiles and active state.
    pub fn clear(&mut self) {
        self.profiles.clear();
        self.active_game = None;
        self.event_queue.clear();
        self.last_event_tick = 0;
        self.last_handled_pid = 0;
    }

    pub fn profile_count(&self) -> usize {
        self.profiles.len()
    }

    pub fn active_game(&self) -> Option<ActiveGame> {
        self.active_game
    }

    /// Finds matching profile using full path precedence, followed by executable name.
    pub fn find_match(&self, exe_name: &str, full_path: &str) -> Option<GameProfileConfig> {
        if exe_name.trim().is_empty() && full_path.trim().is_empty() {
            return None;
        }

        // 1. Exact or normalized full path match if specified in profile
        if !full_path.trim().is_empty() {
            for profile in self.profiles.values() {
                if !profile.full_path.trim().is_empty()
                    && profile
                        .full_path
                        .trim()
                        .eq_ignore_ascii_case(full_path.trim())
                {
                    return Some(profile.clone());
                }
            }
        }

        // 2. Executable name matching (case-insensitive, with/without .exe)
        for profile in self.profiles.values() {
            if procsync::matches_running(&profile.exe_name, exe_name) {
                return Some(profile.clone());
            }
            if !full_path.is_empty() && procsync::matches_running(&profile.exe_name, full_path) {
                return Some(profile.clone());
            }
        }

        None
    }

    /// Processes a foreground window transition event with Alt-Tab debouncing.
    ///
    /// Returns the emitted `ProfileEvent` if a state transition occurred.
    pub fn handle_foreground_transition(
        &mut self,
        pid: u32,
        exe_name: &str,
        full_path: &str,
        now_ms: u64,
    ) -> Option<ProfileEvent> {
        // Self-process ignore
        if pid == 0 || pid == self.own_pid() {
            return None;
        }

        // Blocked system processes ignore
        if is_blocked_executable(exe_name) {
            return None;
        }

        // Rapid Alt-Tab debounce: ignore duplicate events for the same process within threshold
        let elapsed = now_ms.saturating_sub(self.last_event_tick);
        if elapsed < DEBOUNCE_THRESHOLD_MS && self.last_handled_pid == pid {
            return None;
        }

        self.last_event_tick = now_ms;
        self.last_handled_pid = pid;

        // Match against registered profiles
        let matched = self.find_match(exe_name, full_path)?;
        let matched_id = matched.profile_id;

        match self.active_game {
            Some(active) => {
                if active.profile_id == matched_id {
                    // Same game already active; ignore redundant focus events
                    None
                } else {
                    // Switched from old game to new game
                    let old_id = active.profile_id;
                    self.active_game = Some(ActiveGame {
                        profile_id: matched_id,
                        pid,
                    });
                    let event = ProfileEvent::GameSwitched {
                        old_profile_id: old_id,
                        new_profile_id: matched_id,
                    };
                    self.push_event(event);
                    Some(event)
                }
            }
            None => {
                // Game started
                self.active_game = Some(ActiveGame {
                    profile_id: matched_id,
                    pid,
                });
                let event = ProfileEvent::GameStarted {
                    profile_id: matched_id,
                    pid,
                };
                self.push_event(event);
                Some(event)
            }
        }
    }

    /// Handles process termination callback.
    ///
    /// If the terminating PID matches the active game, emits `GameExited`.
    pub fn handle_process_exit(&mut self, pid: u32) -> Option<ProfileEvent> {
        if let Some(active) = self.active_game {
            if active.pid == pid {
                let prof_id = active.profile_id;
                self.active_game = None;
                let event = ProfileEvent::GameExited {
                    profile_id: prof_id,
                    pid,
                };
                self.push_event(event);
                return Some(event);
            }
        }
        None
    }

    fn push_event(&mut self, event: ProfileEvent) {
        if self.event_queue.len() >= MAX_EVENT_QUEUE_CAPACITY {
            self.event_queue.pop_front();
        }
        self.event_queue.push_back(event);
    }

    /// Polls the next event from the internal queue (FIFO).
    pub fn poll_event(&mut self) -> Option<ProfileEvent> {
        self.event_queue.pop_front()
    }
}

// ============================================================================
// Win32 FFI Bindings & Process Lifecycle Monitoring
// ============================================================================

type WinEventProc = unsafe extern "system" fn(
    h_win_event_hook: *mut c_void,
    event: u32,
    hwnd: *mut c_void,
    id_object: i32,
    id_child: i32,
    id_event_thread: u32,
    dwms_event_time: u32,
);

type WaitOrTimerCallback =
    unsafe extern "system" fn(lp_parameter: *mut c_void, timer_or_wait_fired: u8);

const EVENT_SYSTEM_FOREGROUND: u32 = 0x0003;
const WINEVENT_OUTOFCONTEXT: u32 = 0x0000;
const WINEVENT_SKIPOWNPROCESS: u32 = 0x0002;

const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
const SYNCHRONIZE: u32 = 0x00100000;
const INFINITE: u32 = 0xFFFFFFFF;
const WT_EXECUTEONLYONCE: u32 = 0x00000008;
const WM_QUIT: u32 = 0x0012;

#[repr(C)]
struct Point {
    x: i32,
    y: i32,
}

#[repr(C)]
struct Msg {
    hwnd: *mut c_void,
    message: u32,
    wparam: usize,
    lparam: isize,
    time: u32,
    pt: Point,
    lprivate: u32,
}

#[link(name = "user32")]
extern "system" {
    fn SetWinEventHook(
        event_min: u32,
        event_max: u32,
        hmod_win_event_proc: *mut c_void,
        pfn_win_event_proc: WinEventProc,
        id_process: u32,
        id_thread: u32,
        dw_flags: u32,
    ) -> *mut c_void;

    fn UnhookWinEvent(h_win_event_hook: *mut c_void) -> i32;

    fn GetMessageW(
        lp_msg: *mut Msg,
        h_wnd: *mut c_void,
        w_msg_filter_min: u32,
        w_msg_filter_max: u32,
    ) -> i32;

    fn PostThreadMessageW(id_thread: u32, msg: u32, w_param: usize, l_param: isize) -> i32;

    fn GetForegroundWindow() -> *mut c_void;
    fn GetWindowThreadProcessId(h_wnd: *mut c_void, lpdw_process_id: *mut u32) -> u32;
}

#[link(name = "kernel32")]
extern "system" {
    fn GetCurrentProcessId() -> u32;
    fn GetCurrentThreadId() -> u32;
    fn OpenProcess(desired_access: u32, inherit_handle: i32, process_id: u32) -> *mut c_void;
    fn QueryFullProcessImageNameW(
        h_process: *mut c_void,
        flags: u32,
        exe_name: *mut u16,
        size: *mut u32,
    ) -> i32;
    fn CloseHandle(h_object: *mut c_void) -> i32;
    fn GetTickCount64() -> u64;

    fn RegisterWaitForSingleObject(
        ph_new_wait_object: *mut *mut c_void,
        h_object: *mut c_void,
        callback: WaitOrTimerCallback,
        context: *mut c_void,
        dw_milliseconds: u32,
        dw_flags: u32,
    ) -> i32;

    fn UnregisterWait(wait_handle: *mut c_void) -> i32;
}

// ============================================================================
// Global Engine & Hook Runtime Manager
// ============================================================================

struct ActiveWait {
    pid: u32,
    process_handle: *mut c_void,
    wait_handle: *mut c_void,
}

// Safety: The raw pointers are Win32 handles managed with atomic cleanup.
unsafe impl Send for ActiveWait {}
unsafe impl Sync for ActiveWait {}

static GLOBAL_ENGINE: Mutex<Option<GameProfileEngine>> = Mutex::new(None);
static ACTIVE_WAIT: Mutex<Option<ActiveWait>> = Mutex::new(None);
static HOOK_THREAD_ID: AtomicU32 = AtomicU32::new(0);
static HOOK_JOIN_HANDLE: Mutex<Option<JoinHandle<()>>> = Mutex::new(None);

fn with_global_engine<F, R>(f: F) -> R
where
    F: FnOnce(&mut GameProfileEngine) -> R,
{
    let mut guard = GLOBAL_ENGINE.lock().unwrap_or_else(|e| e.into_inner());
    if guard.is_none() {
        *guard = Some(GameProfileEngine::new());
    }
    f(guard.as_mut().unwrap())
}

/// Clean up wait handle and process handle for active process wait.
fn cleanup_active_wait() {
    let mut guard = ACTIVE_WAIT.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(wait) = guard.take() {
        unsafe {
            if !wait.wait_handle.is_null() {
                UnregisterWait(wait.wait_handle);
            }
            if !wait.process_handle.is_null() && wait.process_handle as isize != -1 {
                CloseHandle(wait.process_handle);
            }
        }
    }
}

/// Start monitoring process exit for a PID via `RegisterWaitForSingleObject`.
fn start_process_monitoring(pid: u32) {
    cleanup_active_wait();

    let h_proc = unsafe { OpenProcess(SYNCHRONIZE, 0, pid) };
    if h_proc.is_null() || h_proc as isize == -1 {
        return;
    }

    let mut h_wait: *mut c_void = std::ptr::null_mut();
    let res = unsafe {
        RegisterWaitForSingleObject(
            &mut h_wait,
            h_proc,
            process_exit_callback,
            pid as usize as *mut c_void,
            INFINITE,
            WT_EXECUTEONLYONCE,
        )
    };

    if res == 0 || h_wait.is_null() {
        unsafe { CloseHandle(h_proc) };
        return;
    }

    let mut guard = ACTIVE_WAIT.lock().unwrap_or_else(|e| e.into_inner());
    *guard = Some(ActiveWait {
        pid,
        process_handle: h_proc,
        wait_handle: h_wait,
    });
}

/// Callback triggered by Windows thread pool when monitored process exits.
unsafe extern "system" fn process_exit_callback(context: *mut c_void, _timer_or_wait_fired: u8) {
    let pid = context as usize as u32;

    // Clean up wait handle
    {
        let mut guard = ACTIVE_WAIT.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(wait) = guard.as_ref() {
            if wait.pid == pid {
                let wait = guard.take().unwrap();
                if !wait.wait_handle.is_null() {
                    UnregisterWait(wait.wait_handle);
                }
                if !wait.process_handle.is_null() && wait.process_handle as isize != -1 {
                    CloseHandle(wait.process_handle);
                }
            }
        }
    }

    with_global_engine(|engine| {
        engine.handle_process_exit(pid);
    });
}

/// Extracts foreground window PID, executable name, and full image path.
pub fn get_foreground_window_info() -> Option<(u32, String, String)> {
    let hwnd = unsafe { GetForegroundWindow() };
    if hwnd.is_null() {
        return None;
    }

    let mut pid: u32 = 0;
    unsafe { GetWindowThreadProcessId(hwnd, &mut pid) };
    if pid == 0 || pid == unsafe { GetCurrentProcessId() } {
        return None;
    }

    let h_proc = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if h_proc.is_null() || h_proc as isize == -1 {
        return None;
    }

    let mut buf = [0u16; 1024];
    let mut size = buf.len() as u32;
    let res = unsafe { QueryFullProcessImageNameW(h_proc, 0, buf.as_mut_ptr(), &mut size) };
    unsafe { CloseHandle(h_proc) };

    if res == 0 || size == 0 {
        return None;
    }

    let full_path = String::from_utf16_lossy(&buf[..size as usize]);
    let exe_name = procsync::executable_name(&full_path);

    Some((pid, exe_name, full_path))
}

/// Callback installed into `SetWinEventHook`.
unsafe extern "system" fn win_event_hook_proc(
    _h_win_event_hook: *mut c_void,
    event: u32,
    hwnd: *mut c_void,
    _id_object: i32,
    _id_child: i32,
    _id_event_thread: u32,
    _dwms_event_time: u32,
) {
    if event != EVENT_SYSTEM_FOREGROUND || hwnd.is_null() {
        return;
    }

    let mut pid: u32 = 0;
    GetWindowThreadProcessId(hwnd, &mut pid);
    if pid == 0 || pid == GetCurrentProcessId() {
        return;
    }

    let h_proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
    if h_proc.is_null() || h_proc as isize == -1 {
        return;
    }

    let mut buf = [0u16; 1024];
    let mut size = buf.len() as u32;
    let res = QueryFullProcessImageNameW(h_proc, 0, buf.as_mut_ptr(), &mut size);
    CloseHandle(h_proc);

    if res == 0 || size == 0 {
        return;
    }

    let full_path = String::from_utf16_lossy(&buf[..size as usize]);
    let exe_name = procsync::executable_name(&full_path);
    let now_ms = GetTickCount64();

    let transition = with_global_engine(|engine| {
        engine.handle_foreground_transition(pid, &exe_name, &full_path, now_ms)
    });

    if let Some(event) = transition {
        match event {
            ProfileEvent::GameStarted { .. } | ProfileEvent::GameSwitched { .. } => {
                start_process_monitoring(pid);
            }
            _ => {}
        }
    }
}

// ============================================================================
// Public Subsystem API
// ============================================================================

/// Initializes the native game profile engine and installs the Win32 foreground hook.
pub fn init() -> i32 {
    let mut thread_guard = HOOK_JOIN_HANDLE.lock().unwrap_or_else(|e| e.into_inner());
    if thread_guard.is_some() {
        return 0; // Already running
    }

    let (tx, rx) = std::sync::mpsc::channel();

    let handle = std::thread::spawn(move || {
        let tid = unsafe { GetCurrentThreadId() };
        HOOK_THREAD_ID.store(tid, Ordering::SeqCst);

        let hook = unsafe {
            SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                std::ptr::null_mut(),
                win_event_hook_proc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
            )
        };

        if hook.is_null() {
            let _ = tx.send(Err("Failed to install SetWinEventHook"));
            return;
        }

        let _ = tx.send(Ok(()));

        let mut msg: Msg = unsafe { std::mem::zeroed() };
        while unsafe { GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) } > 0 {
            // Message pump dispatches hook events
        }

        unsafe {
            UnhookWinEvent(hook);
        }
    });

    match rx.recv() {
        Ok(Ok(())) => {
            *thread_guard = Some(handle);
            0
        }
        _ => -1,
    }
}

/// Shuts down the Win32 hook, cleans up process monitors, and stops the worker thread.
pub fn shutdown() -> i32 {
    cleanup_active_wait();

    let tid = HOOK_THREAD_ID.swap(0, Ordering::SeqCst);
    if tid != 0 {
        unsafe {
            PostThreadMessageW(tid, WM_QUIT, 0, 0);
        }
    }

    let mut thread_guard = HOOK_JOIN_HANDLE.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(handle) = thread_guard.take() {
        let _ = handle.join();
    }

    with_global_engine(|engine| {
        engine.clear();
    });
    0
}

/// Registers or updates a profile in the global match table.
pub fn register_profile(config: GameProfileConfig) -> Result<(), &'static str> {
    with_global_engine(|engine| engine.register(config))
}

/// Unregisters a profile by ID from the global match table.
pub fn unregister_profile(profile_id: u32) -> bool {
    with_global_engine(|engine| engine.unregister(profile_id))
}

/// Clears all profiles from the global match table.
pub fn clear_profiles() {
    cleanup_active_wait();
    with_global_engine(|engine| engine.clear());
}

/// Polls the next event from the global event queue.
pub fn poll_event() -> Option<ProfileEvent> {
    with_global_engine(|engine| engine.poll_event())
}

/// Returns the currently active (profile_id, pid).
pub fn get_active() -> Option<(u32, u32)> {
    with_global_engine(|engine| engine.active_game().map(|g| (g.profile_id, g.pid)))
}

/// Invokes a closure against the global engine (useful for testing or direct queries).
pub fn with_engine<F, R>(f: F) -> R
where
    F: FnOnce(&mut GameProfileEngine) -> R,
{
    with_global_engine(f)
}

// ============================================================================
// Unit Tests
// ============================================================================

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_profile_registration_and_validation() {
        let mut engine = GameProfileEngine::new();

        // 1. Profile with ID 0 is invalid
        let invalid_id = GameProfileConfig {
            profile_id: 0,
            exe_name: "cyberpunk2077.exe".to_string(),
            full_path: "".to_string(),
            mode: 2,
            pl1: 65,
            pl2: 125,
            fan_mode: 1,
            refresh_rate: 165,
            dds_mode: 1,
        };
        assert!(engine.register(invalid_id).is_err());

        // 2. Empty exe name is invalid
        let empty_exe = GameProfileConfig {
            profile_id: 1,
            exe_name: "   ".to_string(),
            full_path: "".to_string(),
            mode: 2,
            pl1: 65,
            pl2: 125,
            fan_mode: 1,
            refresh_rate: 165,
            dds_mode: 1,
        };
        assert!(engine.register(empty_exe).is_err());

        // 3. Blocked executable is prohibited
        let blocked = GameProfileConfig {
            profile_id: 1,
            exe_name: "explorer.exe".to_string(),
            full_path: "".to_string(),
            mode: 2,
            pl1: 65,
            pl2: 125,
            fan_mode: 1,
            refresh_rate: 165,
            dds_mode: 1,
        };
        assert!(engine.register(blocked).is_err());

        // 4. Valid profile succeeds
        let valid = GameProfileConfig {
            profile_id: 101,
            exe_name: "cyberpunk2077.exe".to_string(),
            full_path: r"D:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe".to_string(),
            mode: 3,
            pl1: 85,
            pl2: 140,
            fan_mode: 2,
            refresh_rate: 240,
            dds_mode: 2,
        };
        assert!(engine.register(valid.clone()).is_ok());
        assert_eq!(engine.profile_count(), 1);

        // 5. Updating existing profile ID replaces it
        let mut updated = valid.clone();
        updated.mode = 4;
        assert!(engine.register(updated).is_ok());
        assert_eq!(engine.profile_count(), 1);
        let found = engine.find_match("cyberpunk2077.exe", "").unwrap();
        assert_eq!(found.mode, 4);

        // 6. Unregister
        assert!(engine.unregister(101));
        assert_eq!(engine.profile_count(), 0);
        assert!(!engine.unregister(101));
    }

    #[test]
    fn test_profile_matching_rules() {
        let mut engine = GameProfileEngine::new();

        engine
            .register(GameProfileConfig {
                profile_id: 1,
                exe_name: "witcher3.exe".to_string(),
                full_path: r"C:\Steam\steamapps\common\Witcher 3\bin\x64\witcher3.exe".to_string(),
                mode: 2,
                pl1: 55,
                pl2: 115,
                fan_mode: 1,
                refresh_rate: 144,
                dds_mode: 1,
            })
            .unwrap();

        engine
            .register(GameProfileConfig {
                profile_id: 2,
                exe_name: "cs2.exe".to_string(),
                full_path: "".to_string(),
                mode: 1,
                pl1: 45,
                pl2: 95,
                fan_mode: 0,
                refresh_rate: 240,
                dds_mode: 0,
            })
            .unwrap();

        // Match by exact executable name
        let m1 = engine.find_match("witcher3.exe", "").unwrap();
        assert_eq!(m1.profile_id, 1);

        // Match without extension (process name)
        let m2 = engine.find_match("witcher3", "").unwrap();
        assert_eq!(m2.profile_id, 1);

        // Match case-insensitively
        let m3 = engine.find_match("CS2.EXE", "").unwrap();
        assert_eq!(m3.profile_id, 2);

        // Match by full path
        let m4 = engine
            .find_match(
                "unknown.exe",
                r"C:\Steam\steamapps\common\Witcher 3\bin\x64\witcher3.exe",
            )
            .unwrap();
        assert_eq!(m4.profile_id, 1);

        // Non-matching exe
        assert!(engine.find_match("notepad.exe", "").is_none());
    }

    #[test]
    fn test_foreground_state_machine_and_debouncing() {
        let mut engine = GameProfileEngine::new();
        engine.set_mock_own_pid(Some(9999));

        engine
            .register(GameProfileConfig {
                profile_id: 10,
                exe_name: "game_a.exe".to_string(),
                full_path: "".to_string(),
                mode: 2,
                pl1: 55,
                pl2: 115,
                fan_mode: 1,
                refresh_rate: 144,
                dds_mode: 1,
            })
            .unwrap();

        engine
            .register(GameProfileConfig {
                profile_id: 20,
                exe_name: "game_b.exe".to_string(),
                full_path: "".to_string(),
                mode: 3,
                pl1: 75,
                pl2: 135,
                fan_mode: 2,
                refresh_rate: 165,
                dds_mode: 2,
            })
            .unwrap();

        // 1. Focus game_a -> GameStarted
        let ev1 = engine.handle_foreground_transition(1001, "game_a.exe", "", 10_000);
        assert_eq!(
            ev1,
            Some(ProfileEvent::GameStarted {
                profile_id: 10,
                pid: 1001
            })
        );
        assert_eq!(
            engine.active_game(),
            Some(ActiveGame {
                profile_id: 10,
                pid: 1001
            })
        );

        // 2. Alt-Tab debounce: duplicate focus event within 50ms is suppressed
        let ev_dup = engine.handle_foreground_transition(1001, "game_a.exe", "", 10_030);
        assert_eq!(ev_dup, None);

        // 3. Focus outside (browser/notepad) -> active game remains active, no event emitted
        let ev_ext = engine.handle_foreground_transition(2002, "chrome.exe", "", 10_200);
        assert_eq!(ev_ext, None);
        assert_eq!(
            engine.active_game(),
            Some(ActiveGame {
                profile_id: 10,
                pid: 1001
            })
        );

        // 4. Alt-Tab back to game_a -> same profile already active, no duplicate start
        let ev_back = engine.handle_foreground_transition(1001, "game_a.exe", "", 10_400);
        assert_eq!(ev_back, None);

        // 5. Switch directly to game_b -> GameSwitched
        let ev_switch = engine.handle_foreground_transition(3003, "game_b.exe", "", 10_600);
        assert_eq!(
            ev_switch,
            Some(ProfileEvent::GameSwitched {
                old_profile_id: 10,
                new_profile_id: 20
            })
        );
        assert_eq!(
            engine.active_game(),
            Some(ActiveGame {
                profile_id: 20,
                pid: 3003
            })
        );

        // 6. Own process is ignored
        let ev_own =
            engine.handle_foreground_transition(9999, "predatorcontrolapp.exe", "", 10_800);
        assert_eq!(ev_own, None);

        // 7. Process exit for active game -> GameExited
        let ev_exit = engine.handle_process_exit(3003);
        assert_eq!(
            ev_exit,
            Some(ProfileEvent::GameExited {
                profile_id: 20,
                pid: 3003
            })
        );
        assert_eq!(engine.active_game(), None);

        // 8. Event queue FIFO polling
        assert_eq!(
            engine.poll_event(),
            Some(ProfileEvent::GameStarted {
                profile_id: 10,
                pid: 1001
            })
        );
        assert_eq!(
            engine.poll_event(),
            Some(ProfileEvent::GameSwitched {
                old_profile_id: 10,
                new_profile_id: 20
            })
        );
        assert_eq!(
            engine.poll_event(),
            Some(ProfileEvent::GameExited {
                profile_id: 20,
                pid: 3003
            })
        );
        assert_eq!(engine.poll_event(), None);
    }

    #[test]
    fn test_concurrency_and_thread_safety() {
        use std::sync::Arc;
        let engine = Arc::new(Mutex::new(GameProfileEngine::new()));

        // Register profiles
        for i in 1..=10 {
            engine
                .lock()
                .unwrap()
                .register(GameProfileConfig {
                    profile_id: i,
                    exe_name: format!("game_{}.exe", i),
                    full_path: format!(r"C:\Games\game_{}.exe", i),
                    mode: (i % 4) as i32,
                    pl1: 50 + i,
                    pl2: 100 + i,
                    fan_mode: 1,
                    refresh_rate: 144,
                    dds_mode: 0,
                })
                .unwrap();
        }

        let mut handles = Vec::new();
        for thread_idx in 0..4 {
            let eng = Arc::clone(&engine);
            let h = std::thread::spawn(move || {
                for iter in 0..100 {
                    let pid = 1000 + thread_idx * 100 + iter;
                    let profile_idx = (iter % 10) + 1;
                    let exe = format!("game_{}.exe", profile_idx);
                    let now = (iter as u64) * 100;

                    let mut guard = eng.lock().unwrap_or_else(|e| e.into_inner());
                    guard.handle_foreground_transition(pid, &exe, "", now);
                    let _ = guard.poll_event();
                }
            });
            handles.push(h);
        }

        for h in handles {
            h.join().unwrap();
        }
    }

    #[test]
    fn test_live_subsystem_init_and_shutdown() {
        // Test live Win32 hook thread creation, message loop, and teardown
        let init_res = init();
        assert_eq!(
            init_res, 0,
            "SetWinEventHook thread must initialize successfully"
        );

        // Idempotent init
        assert_eq!(init(), 0);

        // Register profile via subsystem API
        let reg_res = register_profile(GameProfileConfig {
            profile_id: 50,
            exe_name: "testgame.exe".to_string(),
            full_path: "".to_string(),
            mode: 1,
            pl1: 50,
            pl2: 100,
            fan_mode: 1,
            refresh_rate: 144,
            dds_mode: 1,
        });
        assert!(reg_res.is_ok());

        assert_eq!(get_active(), None);

        // Shutdown cleanly
        assert_eq!(
            shutdown(),
            0,
            "Subsystem shutdown must stop hook thread cleanly"
        );
        assert_eq!(get_active(), None);
    }
}
