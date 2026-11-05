//! Process-name normalisation and profile matching for game sync.
//!
//! Ports `GameSyncController.SafeGetExecutableName`, `SafeGetProcessName` and
//! its profile-lookup predicate. Enumerating processes stays managed (it needs
//! toolhelp snapshot handles and `Process` objects); this module owns the
//! string and comparison rules that decide whether a running process belongs to
//! a saved profile.

use std::slice;

/// Bytes are interpreted as UTF-8. Returns the length written, or a negative
/// value when the output buffer is too small.
///
/// # Safety
/// `input` must be valid for `input_len` bytes; `out` for `out_cap` bytes.
pub unsafe fn copy_result(text: &str, out: *mut u8, out_cap: i32) -> i32 {
    if !crate::is_valid_mut_buffer(out) || out_cap <= 0 {
        return -1;
    }
    let bytes = text.as_bytes();
    if bytes.len() > out_cap as usize {
        return -1;
    }
    slice::from_raw_parts_mut(out, bytes.len()).copy_from_slice(bytes);
    bytes.len() as i32
}

/// Reads a UTF-8 string, returning an empty string for invalid pointers.
///
/// # Safety
/// `ptr` must be valid for `len` bytes when `len` is positive.
pub unsafe fn read_str<'a>(ptr: *const u8, len: i32) -> &'a str {
    if !crate::is_valid_buffer(ptr) || len <= 0 || len > 100_000 {
        return "";
    }
    std::str::from_utf8(slice::from_raw_parts(ptr, len as usize)).unwrap_or("")
}

/// Extracts the file name from a possibly quoted or fully-qualified path.
pub fn executable_name(raw: &str) -> String {
    let trimmed = raw.trim().trim_matches(|c| c == '"' || c == '\'');
    if trimmed.is_empty() {
        return String::new();
    }
    // Split on both separators so Windows and POSIX-style paths behave alike.
    match trimmed.rsplit(['/', '\\']).next() {
        Some(name) if !name.is_empty() => name.to_string(),
        _ => trimmed.to_string(),
    }
}

/// The process name without a trailing `.exe`, as `Process.ProcessName` reports it.
pub fn process_name(raw: &str) -> String {
    let name = executable_name(raw);
    if name.len() >= 4
        && name.is_char_boundary(name.len() - 4)
        && name[name.len() - 4..].eq_ignore_ascii_case(".exe")
    {
        name[..name.len() - 4].to_string()
    } else {
        name
    }
}

/// Case-insensitive comparison of two executable names, ignoring any path and
/// a trailing `.exe` on either side.
pub fn same_process(a: &str, b: &str) -> bool {
    let left = process_name(a);
    let right = process_name(b);
    !left.is_empty() && left.eq_ignore_ascii_case(&right)
}

/// True when `running` (as reported by the OS) matches `profile_executable`.
pub fn matches_running(profile_executable: &str, running: &str) -> bool {
    if profile_executable.trim().is_empty() || running.trim().is_empty() {
        return false;
    }
    // The OS may report either `game.exe` or `game`.
    let profile_exe = executable_name(profile_executable);
    let running_exe = executable_name(running);
    if profile_exe.eq_ignore_ascii_case(&running_exe) {
        return true;
    }
    same_process(&profile_exe, &running_exe)
}

/// Clamps a profile field, preserving the `-1` "unset" sentinel.
pub fn clamp_or_unset(value: i32, min: i32, max: i32) -> i32 {
    if value == -1 {
        -1
    } else {
        value.clamp(min, max)
    }
}

/// Clamps a profile field where `-1` means unset and the floor applies otherwise.
pub fn clamp_floor(value: i32, min: i32, max: i32) -> i32 {
    if value < 0 {
        -1
    } else {
        value.clamp(min, max)
    }
}

/// A sanitised game profile, mirroring the managed field set.
#[derive(Clone, Copy, Debug, Default)]
#[repr(C)]
pub struct ProfileValues {
    pub cpu_fan_speed: i32,
    pub gpu_fan_speed: i32,
    pub sys_fan_speed: i32,
    pub refresh_rate: i32,
    pub battery_limit: i32,
    pub rgb_brightness: i32,
    pub rgb_speed: i32,
    pub rgb_r: i32,
    pub rgb_g: i32,
    pub rgb_b: i32,
}

/// Applies every clamp from `GameSyncController.SanitizeProfileValues`.
pub fn sanitize_profile(input: &ProfileValues) -> ProfileValues {
    let refresh = if input.refresh_rate != -1 && input.refresh_rate < 30 {
        -1
    } else {
        input.refresh_rate
    };

    ProfileValues {
        cpu_fan_speed: clamp_or_unset(input.cpu_fan_speed, 0, 100),
        gpu_fan_speed: clamp_or_unset(input.gpu_fan_speed, 0, 100),
        sys_fan_speed: clamp_or_unset(input.sys_fan_speed, 0, 100),
        refresh_rate: refresh,
        battery_limit: clamp_floor(input.battery_limit, 50, 100),
        rgb_brightness: clamp_or_unset(input.rgb_brightness, 0, 100),
        rgb_speed: clamp_or_unset(input.rgb_speed, 0, 100),
        rgb_r: clamp_or_unset(input.rgb_r, 0, 255),
        rgb_g: clamp_or_unset(input.rgb_g, 0, 255),
        rgb_b: clamp_or_unset(input.rgb_b, 0, 255),
    }
}

#[repr(C)]
struct ProcessEntry32W {
    dw_size: u32,
    cnt_usage: u32,
    th32_process_id: u32,
    th32_default_heap_id: usize,
    th32_module_id: u32,
    cnt_threads: u32,
    th32_parent_process_id: u32,
    pc_pri_class_base: i32,
    dw_flags: u32,
    sz_exe_file: [u16; 260],
}

#[link(name = "kernel32")]
extern "system" {
    fn CreateToolhelp32Snapshot(dw_flags: u32, th32_process_id: u32) -> *mut std::ffi::c_void;
    fn Process32FirstW(h_snapshot: *mut std::ffi::c_void, lppe: *mut ProcessEntry32W) -> i32;
    fn Process32NextW(h_snapshot: *mut std::ffi::c_void, lppe: *mut ProcessEntry32W) -> i32;
    fn CloseHandle(h_object: *mut std::ffi::c_void) -> i32;
    fn OpenProcess(
        desired_access: u32,
        inherit_handle: i32,
        process_id: u32,
    ) -> *mut std::ffi::c_void;
    fn QueryFullProcessImageNameW(
        h_process: *mut std::ffi::c_void,
        flags: u32,
        exe_name: *mut u16,
        size: *mut u32,
    ) -> i32;
}

#[link(name = "user32")]
extern "system" {
    fn GetForegroundWindow() -> *mut std::ffi::c_void;
    fn GetWindowThreadProcessId(h_wnd: *mut std::ffi::c_void, lpdw_process_id: *mut u32) -> u32;
}

pub fn is_process_running(exe_name: &str) -> bool {
    let trimmed = exe_name.trim();
    if trimmed.is_empty() {
        return false;
    }

    let h_snap = unsafe { CreateToolhelp32Snapshot(0x00000002, 0) };
    if h_snap.is_null() || h_snap as isize == -1 {
        return false;
    }

    let mut pe = ProcessEntry32W {
        dw_size: std::mem::size_of::<ProcessEntry32W>() as u32,
        cnt_usage: 0,
        th32_process_id: 0,
        th32_default_heap_id: 0,
        th32_module_id: 0,
        cnt_threads: 0,
        th32_parent_process_id: 0,
        pc_pri_class_base: 0,
        dw_flags: 0,
        sz_exe_file: [0u16; 260],
    };

    let mut found = false;
    if unsafe { Process32FirstW(h_snap, &mut pe) } != 0 {
        loop {
            let len = pe.sz_exe_file.iter().position(|&c| c == 0).unwrap_or(260);
            let exe = String::from_utf16_lossy(&pe.sz_exe_file[..len]);
            if matches_running(trimmed, &exe) {
                found = true;
                break;
            }
            if unsafe { Process32NextW(h_snap, &mut pe) } == 0 {
                break;
            }
        }
    }

    unsafe { CloseHandle(h_snap) };
    found
}

pub fn get_foreground_process_id() -> u32 {
    let hwnd = unsafe { GetForegroundWindow() };
    if hwnd.is_null() {
        return 0;
    }
    let mut pid: u32 = 0;
    unsafe { GetWindowThreadProcessId(hwnd, &mut pid) };
    pid
}

pub fn get_foreground_process_name() -> Option<String> {
    let hwnd = unsafe { GetForegroundWindow() };
    if hwnd.is_null() {
        return None;
    }
    let mut pid: u32 = 0;
    unsafe { GetWindowThreadProcessId(hwnd, &mut pid) };
    if pid == 0 {
        return None;
    }

    const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
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
    let name = process_name(&full_path);
    if name.is_empty() {
        None
    } else {
        Some(name)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn extracts_names_from_paths() {
        assert_eq!(executable_name(r"C:\Games\Doom\doom.exe"), "doom.exe");
        assert_eq!(executable_name("/usr/bin/game"), "game");
        assert_eq!(executable_name("  game.exe  "), "game.exe");
        assert_eq!(executable_name(r#""C:\a\b.exe""#), "b.exe");
        assert_eq!(executable_name(""), "");
    }

    #[test]
    fn strips_exe_suffix_case_insensitively() {
        assert_eq!(process_name("doom.exe"), "doom");
        assert_eq!(process_name("Doom.EXE"), "Doom");
        assert_eq!(process_name("doom"), "doom");
        assert_eq!(process_name("doom.exe.exe"), "doom.exe");
    }

    #[test]
    fn matches_with_and_without_suffix() {
        assert!(same_process("doom.exe", "doom"));
        assert!(same_process(r"C:\x\doom.exe", "DOOM"));
        assert!(!same_process("doom", "doom2"));
        assert!(!same_process("", "doom"));
    }

    #[test]
    fn matches_running_forms() {
        assert!(matches_running("doom.exe", "doom.exe"));
        assert!(matches_running("doom.exe", "doom"));
        assert!(matches_running(r"C:\Games\doom.exe", "doom.exe"));
        assert!(!matches_running("doom.exe", ""));
        assert!(!matches_running("", "doom.exe"));
    }

    #[test]
    fn preserves_unset_sentinels() {
        assert_eq!(clamp_or_unset(-1, 0, 100), -1);
        assert_eq!(clamp_or_unset(150, 0, 100), 100);
        assert_eq!(clamp_or_unset(-50, 0, 100), 0);
        assert_eq!(clamp_floor(-1, 50, 100), -1);
        assert_eq!(clamp_floor(-5, 50, 100), -1);
        assert_eq!(clamp_floor(20, 50, 100), 50);
        assert_eq!(clamp_floor(120, 50, 100), 100);
    }

    #[test]
    fn sanitizes_a_full_profile() {
        let input = ProfileValues {
            cpu_fan_speed: 150,
            gpu_fan_speed: -1,
            sys_fan_speed: -20,
            refresh_rate: 10,
            battery_limit: 20,
            rgb_brightness: 200,
            rgb_speed: 50,
            rgb_r: 300,
            rgb_g: -1,
            rgb_b: 128,
        };
        let out = sanitize_profile(&input);
        assert_eq!(out.cpu_fan_speed, 100);
        assert_eq!(out.gpu_fan_speed, -1);
        assert_eq!(out.sys_fan_speed, 0);
        assert_eq!(out.refresh_rate, -1);
        assert_eq!(out.battery_limit, 50);
        assert_eq!(out.rgb_brightness, 100);
        assert_eq!(out.rgb_r, 255);
        assert_eq!(out.rgb_g, -1);
        assert_eq!(out.rgb_b, 128);
    }

    #[test]
    fn buffer_copy_reports_overflow() {
        let mut buf = [0u8; 8];
        unsafe {
            assert_eq!(copy_result("abc", buf.as_mut_ptr(), 8), 3);
            assert_eq!(&buf[..3], b"abc");
            assert_eq!(copy_result("toolong", buf.as_mut_ptr(), 3), -1);
            assert_eq!(copy_result("abc", std::ptr::null_mut(), 8), -1);
        }
    }

    #[test]
    fn reads_strings_safely() {
        let bytes = b"doom.exe";
        unsafe {
            assert_eq!(read_str(bytes.as_ptr(), 8), "doom.exe");
            assert_eq!(read_str(std::ptr::null(), 8), "");
            assert_eq!(read_str(bytes.as_ptr(), 0), "");
            // Invalid UTF-8 bytes must not panic or cause UB
            let invalid_utf8 = [0xFF, 0xFE, 0xFD, 0x80];
            assert_eq!(
                read_str(invalid_utf8.as_ptr(), invalid_utf8.len() as i32),
                ""
            );
        }
    }

    #[test]
    fn handles_multibyte_and_non_ascii_process_names() {
        // Must never panic on non-char-boundary slicing
        assert_eq!(process_name("a🎮b"), "a🎮b");
        assert_eq!(process_name("🎮.exe"), "🎮");
        assert_eq!(process_name("游戏.EXE"), "游戏");
        assert_eq!(process_name("a🎮b.exe"), "a🎮b");
    }

    #[test]
    fn tests_native_process_running_on_current_exe() {
        if let Ok(current) = std::env::current_exe() {
            if let Some(name) = current.file_name().and_then(|n| n.to_str()) {
                assert!(
                    is_process_running(name),
                    "Current test process {name} should be running"
                );
                let without_exe = process_name(name);
                assert!(
                    is_process_running(&without_exe),
                    "Current test process without .exe should match"
                );
            }
        }
        assert!(!is_process_running("non_existent_fake_process_12345.exe"));
        assert!(!is_process_running(""));
    }

    #[test]
    fn tests_get_foreground_process_name_safe() {
        // Calling get_foreground_process_name should never panic or crash
        let _ = get_foreground_process_name();
        let _ = get_foreground_process_id();
    }
}
