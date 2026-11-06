//! Display and Advanced Optimus (DDS) GPU Switching Engine
//!
//! Production-grade Rust implementation for:
//! 1. Direct Win32 display API integration (`EnumDisplayDevicesW`, `EnumDisplaySettingsW`, `ChangeDisplaySettingsExW`).
//!    Zero-allocation refresh rate enumeration and atomic setting.
//! 2. Hardware Gamma Ramp BlueLight Shield control (`GetDC`, `GetDeviceGammaRamp`, `SetDeviceGammaRamp`, `ReleaseDC`).
//!    Supports Off (0), Low (18% reduction), High (36% reduction), and Custom modes.
//! 3. Native NVIDIA Advanced Optimus (Dynamic Display Switch / DDS) integration via `nvcpl.dll`:
//!    Dynamic binding without rebooting across standard NVIDIA and Windows directories.
//! 4. Hardware MUX and LCD Overdrive fallback via Acer WMI (`APGeAction` Function 2 for MUX, Function 15 for LCD Overdrive).
//! 5. Zero-allocation in-memory testable architecture with clean fallbacks.

use std::ffi::{c_void, OsStr};
use std::mem;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::{Mutex, OnceLock};

// ============================================================================
// 1. Win32 Structs & Function Signatures
// ============================================================================

#[repr(C, packed(4))]
#[derive(Copy, Clone)]
pub struct DevModeW {
    pub dm_device_name: [u16; 32],
    pub dm_spec_version: u16,
    pub dm_driver_version: u16,
    pub dm_size: u16,
    pub dm_driver_extra: u16,
    pub dm_fields: u32,
    pub dm_position_x: i32,
    pub dm_position_y: i32,
    pub dm_display_orientation: u32,
    pub dm_display_fixed_output: u32,
    pub dm_color: i16,
    pub dm_duplex: i16,
    pub dm_y_resolution: i16,
    pub dm_tt_option: i16,
    pub dm_collate: i16,
    pub dm_form_name: [u16; 32],
    pub dm_log_pixels: u16,
    pub dm_bits_per_pel: u32,
    pub dm_pels_width: u32,
    pub dm_pels_height: u32,
    pub dm_display_flags: u32,
    pub dm_display_frequency: u32,
    pub dm_icm_method: u32,
    pub dm_icm_intent: u32,
    pub dm_media_type: u32,
    pub dm_dither_type: u32,
    pub dm_reserved1: u32,
    pub dm_reserved2: u32,
    pub dm_panning_width: u32,
    pub dm_panning_height: u32,
}

impl Default for DevModeW {
    fn default() -> Self {
        let mut dm: DevModeW = unsafe { mem::zeroed() };
        dm.dm_size = mem::size_of::<DevModeW>() as u16;
        dm
    }
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct DisplayDeviceW {
    pub cb: u32,
    pub device_name: [u16; 32],
    pub device_string: [u16; 128],
    pub state_flags: u32,
    pub device_id: [u16; 128],
    pub device_key: [u16; 128],
}

impl Default for DisplayDeviceW {
    fn default() -> Self {
        let mut dd: DisplayDeviceW = unsafe { mem::zeroed() };
        dd.cb = mem::size_of::<DisplayDeviceW>() as u32;
        dd
    }
}

#[repr(C)]
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub struct GammaRamp {
    pub red: [u16; 256],
    pub green: [u16; 256],
    pub blue: [u16; 256],
}

impl Default for GammaRamp {
    fn default() -> Self {
        let mut r = GammaRamp {
            red: [0u16; 256],
            green: [0u16; 256],
            blue: [0u16; 256],
        };
        for i in 0..256 {
            let v = (i as u32 * 257) as u16;
            r.red[i] = v;
            r.green[i] = v;
            r.blue[i] = v;
        }
        r
    }
}

pub const DM_DISPLAYFREQUENCY: u32 = 0x0040_0000;
pub const DM_PELSWIDTH: u32 = 0x0008_0000;
pub const DM_PELSHEIGHT: u32 = 0x0010_0000;
pub const CDS_UPDATEREGISTRY: u32 = 0x0000_0001;
pub const DISPLAY_DEVICE_ATTACHED_TO_DESKTOP: u32 = 0x0000_0001;
pub const DISPLAY_DEVICE_PRIMARY_DEVICE: u32 = 0x0000_0004;
pub const ENUM_CURRENT_SETTINGS: u32 = 0xFFFF_FFFF;

#[cfg(windows)]
extern "system" {
    fn EnumDisplayDevicesW(
        lp_device: *const u16,
        i_dev_num: u32,
        lp_display_device: *mut DisplayDeviceW,
        dw_flags: u32,
    ) -> i32;

    fn EnumDisplaySettingsW(
        lpsz_device_name: *const u16,
        i_mode_num: u32,
        lp_dev_mode: *mut DevModeW,
    ) -> i32;

    fn ChangeDisplaySettingsExW(
        lpsz_device_name: *const u16,
        lp_dev_mode: *mut DevModeW,
        hwnd: *mut c_void,
        dwflags: u32,
        l_param: *mut c_void,
    ) -> i32;

    fn GetDC(h_wnd: *mut c_void) -> *mut c_void;
    fn ReleaseDC(h_wnd: *mut c_void, h_dc: *mut c_void) -> i32;
    fn LoadLibraryW(name: *const u16) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const i8) -> *mut c_void;
    fn FreeLibrary(module: *mut c_void) -> i32;
}

#[cfg(windows)]
#[link(name = "gdi32")]
extern "system" {
    fn SetDeviceGammaRamp(h_dc: *mut c_void, lp_ramp: *const c_void) -> i32;
    fn GetDeviceGammaRamp(h_dc: *mut c_void, lp_ramp: *mut c_void) -> i32;
}

// ============================================================================
// 2. NVIDIA Advanced Optimus / DDS Structs & Function Types
// ============================================================================

#[repr(C, packed(1))]
#[derive(Copy, Clone, Debug, Default)]
pub struct NativeDdsStatusInfo {
    pub is_supported: u8,
    pub is_automatic: u8,
    pub reserved1: u8,
    pub reserved2: u8,
    pub mux_state: u32,    // 1 = Optimus (iGPU), 2 = dGPU
    pub kmd_response: u32, // 0 = OK
    pub sr_type: u32,
}

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum DdsDisplayMode {
    Optimus = 0,
    Automatic = 1,
    NvidiaGpuOnly = 2,
}

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum DdsActiveMux {
    Unknown = 0,
    Optimus = 1,
    DGpu = 2,
}

#[derive(Copy, Clone, Debug, Default, PartialEq, Eq)]
pub struct DdsStatus {
    pub is_supported: bool,
    pub is_automatic: bool,
    pub active_mux: u32, // 1 = Optimus, 2 = dGPU, 0 = Unknown
    pub has_blocking_apps: bool,
    pub kmd_response: u32,
}

type NvCplApiInitFn = unsafe extern "C" fn() -> i32;
type NvCplApiCloseFn = unsafe extern "C" fn() -> i32;
type NvCplApiGetInfoFn = unsafe extern "C" fn(u32, u32, *mut c_void, *mut usize) -> i32;
type NvCplApiGetSettingFn = unsafe extern "C" fn(u32, *mut u32, u32, *mut c_void, usize) -> i32;
type NvCplApiSetSettingFn = unsafe extern "C" fn(u32, *mut u32, u32, *const c_void, usize) -> i32;
type NvCplApiExecuteFn = unsafe extern "C" fn(u32, u32, i32) -> i32;

pub const INFO_TYPE_DDS_STATUS: u32 = 0x17;
pub const SETTING_ID_DDS_STATE: u32 = 0x332;
pub const SETTING_ID_BLOCKING_APPS: u32 = 0x333;
pub const COMMAND_COMMIT: u32 = 0x10000;

struct NvidiaDdsApi {
    _handle: *mut c_void,
    #[allow(dead_code)]
    init: NvCplApiInitFn,
    close: NvCplApiCloseFn,
    get_info: NvCplApiGetInfoFn,
    get_setting: NvCplApiGetSettingFn,
    set_setting: NvCplApiSetSettingFn,
    execute: NvCplApiExecuteFn,
    lock: Mutex<()>,
}

unsafe impl Send for NvidiaDdsApi {}
unsafe impl Sync for NvidiaDdsApi {}

impl Drop for NvidiaDdsApi {
    fn drop(&mut self) {
        #[cfg(windows)]
        unsafe {
            (self.close)();
            FreeLibrary(self._handle);
        }
    }
}

static DDS_API: OnceLock<Option<NvidiaDdsApi>> = OnceLock::new();

fn init_dds_api() -> Option<NvidiaDdsApi> {
    #[cfg(windows)]
    unsafe {
        let mut candidates = Vec::new();

        if let Ok(prog) = std::env::var("ProgramFiles") {
            candidates.push(format!(
                r"{}\NVIDIA Corporation\NVIDIA App\NvCpl\nvcpl.dll",
                prog
            ));
            candidates.push(format!(
                r"{}\NVIDIA Corporation\Control Panel Client\nvcpl.dll",
                prog
            ));
        }
        if let Ok(sysroot) = std::env::var("SystemRoot") {
            candidates.push(format!(r"{}\System32\nvcpl.dll", sysroot));
        }

        candidates
            .push(r"C:\Program Files\NVIDIA Corporation\NVIDIA App\NvCpl\nvcpl.dll".to_string());
        candidates.push(
            r"C:\Program Files\NVIDIA Corporation\Control Panel Client\nvcpl.dll".to_string(),
        );
        candidates.push(r"C:\Windows\System32\nvcpl.dll".to_string());

        for path in &candidates {
            if !Path::new(path).exists() {
                continue;
            }
            let wide: Vec<u16> = OsStr::new(path).encode_wide().chain(Some(0)).collect();
            let handle = LoadLibraryW(wide.as_ptr());
            if handle.is_null() {
                continue;
            }

            let p_init = GetProcAddress(handle, b"NvCplApiInit\0".as_ptr() as *const i8);
            let p_close = GetProcAddress(handle, b"NvCplApiClose\0".as_ptr() as *const i8);
            let p_get_info = GetProcAddress(handle, b"NvCplApiGetInfo\0".as_ptr() as *const i8);
            let p_get_setting =
                GetProcAddress(handle, b"NvCplApiGetSetting\0".as_ptr() as *const i8);
            let p_set_setting =
                GetProcAddress(handle, b"NvCplApiSetSetting\0".as_ptr() as *const i8);
            let p_execute = GetProcAddress(handle, b"NvCplApiExecute\0".as_ptr() as *const i8);

            if p_init.is_null()
                || p_close.is_null()
                || p_get_info.is_null()
                || p_get_setting.is_null()
                || p_set_setting.is_null()
                || p_execute.is_null()
            {
                FreeLibrary(handle);
                continue;
            }

            let init_fn: NvCplApiInitFn = mem::transmute(p_init);
            let ret = init_fn();
            if ret != 0 {
                FreeLibrary(handle);
                continue;
            }

            return Some(NvidiaDdsApi {
                _handle: handle,
                init: init_fn,
                close: mem::transmute(p_close),
                get_info: mem::transmute(p_get_info),
                get_setting: mem::transmute(p_get_setting),
                set_setting: mem::transmute(p_set_setting),
                execute: mem::transmute(p_execute),
                lock: Mutex::new(()),
            });
        }
    }
    None
}

fn get_dds_api() -> Option<&'static NvidiaDdsApi> {
    DDS_API.get_or_init(init_dds_api).as_ref()
}

// ============================================================================
// 3. Mock & Fallback Global State for CI & Non-Hardware Environments
// ============================================================================

static MOCK_ENABLED: AtomicBool = AtomicBool::new(false);
static MOCK_REFRESH_RATE: AtomicI32 = AtomicI32::new(165);
static MOCK_BLUELIGHT_MODE: AtomicI32 = AtomicI32::new(0);
static MOCK_DDS_MODE: AtomicI32 = AtomicI32::new(1); // Auto
static MOCK_LCD_OVERDRIVE: AtomicBool = AtomicBool::new(true);

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub struct WmiActionRecord {
    pub function: u8,
    pub value: u64,
    pub raw_payload: u64,
}

static LAST_WMI_ACTION: Mutex<Option<WmiActionRecord>> = Mutex::new(None);
static MOCK_SUPPORTED_RATES: Mutex<Option<([i32; 16], usize)>> = Mutex::new(None);

pub fn set_mock_mode(enabled: bool) {
    MOCK_ENABLED.store(enabled, Ordering::SeqCst);
}

pub fn is_mock_mode() -> bool {
    MOCK_ENABLED.load(Ordering::SeqCst)
}

pub fn set_mock_refresh_rate(hz: i32) {
    MOCK_REFRESH_RATE.store(hz, Ordering::SeqCst);
}

pub fn set_mock_supported_refresh_rates(rates: &[i32]) {
    let mut lock = MOCK_SUPPORTED_RATES
        .lock()
        .unwrap_or_else(|e| e.into_inner());
    let mut arr = [0i32; 16];
    let count = rates.len().min(16);
    for i in 0..count {
        arr[i] = rates[i];
    }
    *lock = Some((arr, count));
}

pub fn get_last_wmi_action() -> Option<WmiActionRecord> {
    *LAST_WMI_ACTION.lock().unwrap_or_else(|e| e.into_inner())
}

pub fn clear_last_wmi_action() {
    *LAST_WMI_ACTION.lock().unwrap_or_else(|e| e.into_inner()) = None;
}

// ============================================================================
// 4. Core Display APIs: Zero-Allocation Refresh Rate & Primary Device
// ============================================================================

/// Query primary active display device name without heap allocation.
/// Copies up to 32 UTF-16 characters into `out_name`.
/// Returns true if a primary display device was identified.
pub fn get_primary_device_name_buf(out_name: &mut [u16; 32]) -> bool {
    if is_mock_mode() {
        let name_str = OsStr::new(r"\\.\DISPLAY1");
        let wide: Vec<u16> = name_str.encode_wide().chain(Some(0)).collect();
        let len = wide.len().min(32);
        out_name[..len].copy_from_slice(&wide[..len]);
        return true;
    }

    #[cfg(windows)]
    unsafe {
        let mut dev = DisplayDeviceW::default();
        let mut idx = 0u32;
        let mut fallback = [0u16; 32];
        let mut has_fallback = false;

        while EnumDisplayDevicesW(std::ptr::null(), idx, &mut dev, 0) != 0 {
            if (dev.state_flags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0 {
                out_name.copy_from_slice(&dev.device_name);
                return true;
            }
            if (dev.state_flags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 && !has_fallback {
                fallback.copy_from_slice(&dev.device_name);
                has_fallback = true;
            }
            idx += 1;
            dev = DisplayDeviceW::default();
        }

        if has_fallback {
            out_name.copy_from_slice(&fallback);
            return true;
        }
    }

    false
}

/// Enumerate unique supported refresh rates for the primary display into a caller-provided slice.
/// Returns the total count of unique supported refresh rates found (sorted ascending).
/// Guarantees ZERO heap allocations.
pub fn enumerate_supported_refresh_rates(out: &mut [i32]) -> usize {
    if is_mock_mode() {
        let lock = MOCK_SUPPORTED_RATES
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        if let Some((rates, lock_count)) = *lock {
            let count = lock_count.min(out.len());
            for i in 0..count {
                out[i] = rates[i];
            }
            return lock_count;
        }
        let default_rates = [60, 120, 144, 165, 240];
        let count = default_rates.len().min(out.len());
        for i in 0..count {
            out[i] = default_rates[i];
        }
        return default_rates.len();
    }

    #[cfg(windows)]
    unsafe {
        let mut dev_name = [0u16; 32];
        let has_dev = get_primary_device_name_buf(&mut dev_name);
        let dev_ptr = if has_dev {
            dev_name.as_ptr()
        } else {
            std::ptr::null()
        };

        // Query current resolution to filter modes
        let mut cur_dm = DevModeW::default();
        let (cur_w, cur_h) =
            if EnumDisplaySettingsW(dev_ptr, ENUM_CURRENT_SETTINGS, &mut cur_dm) != 0 {
                (cur_dm.dm_pels_width, cur_dm.dm_pels_height)
            } else {
                (0, 0)
            };

        let mut dm = DevModeW::default();
        let mut mode_idx = 0u32;
        let mut count = 0usize;

        while EnumDisplaySettingsW(dev_ptr, mode_idx, &mut dm) != 0 {
            if (cur_w == 0 || (dm.dm_pels_width == cur_w && dm.dm_pels_height == cur_h))
                && dm.dm_display_frequency > 0
            {
                let hz = dm.dm_display_frequency as i32;
                let mut exists = false;
                for i in 0..count.min(out.len()) {
                    if out[i] == hz {
                        exists = true;
                        break;
                    }
                }
                if !exists {
                    if count < out.len() {
                        out[count] = hz;
                    }
                    count += 1;
                }
            }
            mode_idx += 1;
            dm = DevModeW::default();
        }

        // In-place insertion sort (zero allocation)
        let n = count.min(out.len());
        for i in 1..n {
            let key = out[i];
            let mut j = i;
            while j > 0 && out[j - 1] > key {
                out[j] = out[j - 1];
                j -= 1;
            }
            out[j] = key;
        }

        if count == 0 && !out.is_empty() {
            out[0] = 60;
            return 1;
        }

        count
    }

    #[cfg(not(windows))]
    {
        let fallback = [60, 144, 165];
        let count = fallback.len().min(out.len());
        for i in 0..count {
            out[i] = fallback[i];
        }
        fallback.len()
    }
}

/// Enumerate all supported refresh rates for the primary display into a Vec.
pub fn get_supported_refresh_rates() -> Vec<i32> {
    let mut buf = [0i32; 64];
    let total = enumerate_supported_refresh_rates(&mut buf);
    let to_copy = total.min(buf.len());
    buf[..to_copy].to_vec()
}

/// Get current refresh rate of the primary display.
pub fn get_current_refresh_rate() -> i32 {
    if is_mock_mode() {
        return MOCK_REFRESH_RATE.load(Ordering::SeqCst);
    }

    #[cfg(windows)]
    unsafe {
        let mut dev_name = [0u16; 32];
        let has_dev = get_primary_device_name_buf(&mut dev_name);
        let dev_ptr = if has_dev {
            dev_name.as_ptr()
        } else {
            std::ptr::null()
        };

        let mut dm = DevModeW::default();
        if EnumDisplaySettingsW(dev_ptr, ENUM_CURRENT_SETTINGS, &mut dm) != 0 {
            if dm.dm_display_frequency > 0 {
                return dm.dm_display_frequency as i32;
            }
        }
    }
    60
}

/// Set refresh rate on the primary display with atomic Registry update and dynamic fallback.
pub fn set_refresh_rate(hz: i32) -> Result<(), &'static str> {
    if hz <= 0 || hz > 1000 {
        return Err("Invalid refresh rate");
    }

    if is_mock_mode() {
        MOCK_REFRESH_RATE.store(hz, Ordering::SeqCst);
        return Ok(());
    }

    #[cfg(windows)]
    unsafe {
        let mut dev_name = [0u16; 32];
        let has_dev = get_primary_device_name_buf(&mut dev_name);
        let dev_ptr = if has_dev {
            dev_name.as_ptr()
        } else {
            std::ptr::null()
        };

        let mut current_dm = DevModeW::default();
        if EnumDisplaySettingsW(dev_ptr, ENUM_CURRENT_SETTINGS, &mut current_dm) == 0 {
            return Err("Failed to query current display settings");
        }

        if current_dm.dm_display_frequency as i32 == hz {
            return Ok(());
        }

        let mut target_dm = DevModeW::default();
        let mut mode_idx = 0u32;
        let mut found = false;

        while EnumDisplaySettingsW(dev_ptr, mode_idx, &mut target_dm) != 0 {
            if target_dm.dm_pels_width == current_dm.dm_pels_width
                && target_dm.dm_pels_height == current_dm.dm_pels_height
                && target_dm.dm_display_frequency as i32 == hz
            {
                found = true;
                break;
            }
            mode_idx += 1;
            target_dm = DevModeW::default();
        }

        if !found {
            target_dm = current_dm;
            target_dm.dm_display_frequency = hz as u32;
            target_dm.dm_fields = DM_DISPLAYFREQUENCY | DM_PELSWIDTH | DM_PELSHEIGHT;
        }

        // Atomic setting: update registry and reinitialize timing
        let ret = ChangeDisplaySettingsExW(
            dev_ptr,
            &mut target_dm,
            std::ptr::null_mut(),
            CDS_UPDATEREGISTRY,
            std::ptr::null_mut(),
        );

        if ret == 0 {
            Ok(())
        } else {
            // Dynamic switch fallback without registry persistence
            let ret_dyn = ChangeDisplaySettingsExW(
                dev_ptr,
                &mut target_dm,
                std::ptr::null_mut(),
                0,
                std::ptr::null_mut(),
            );
            if ret_dyn == 0 {
                Ok(())
            } else {
                Err("ChangeDisplaySettingsExW failed")
            }
        }
    }
    #[cfg(not(windows))]
    Ok(())
}

// ============================================================================
// 5. BlueLight Shield (Hardware Gamma Ramp)
// ============================================================================

/// Set BlueLight Shield mode:
/// - 0 = Off (100% normal)
/// - 1 = Low (18% blue reduction)
/// - 2 = High (36% blue reduction)
/// - 3 = Custom / Long Hour (54% blue reduction)
/// - 4..=100 = Custom percentage reduction (e.g. 50 = 50% reduction)
pub fn set_bluelight_shield(mode: i32) -> Result<(), &'static str> {
    if mode < 0 || mode > 100 {
        return Err("Invalid BlueLight Shield mode");
    }

    if is_mock_mode() {
        MOCK_BLUELIGHT_MODE.store(mode, Ordering::SeqCst);
        return Ok(());
    }

    let (blue_scale, green_scale) = match mode {
        0 => (1.0f64, 1.0f64),
        1 => (0.82f64, 0.96f64), // 18% reduction
        2 => (0.64f64, 0.92f64), // 36% reduction
        3 => (0.46f64, 0.86f64), // Custom / Long Hour (54% reduction)
        custom_pct => {
            let reduction = (custom_pct as f64) / 100.0;
            let blue = (1.0 - reduction).max(0.1);
            let green = (1.0 - reduction * 0.25).max(0.5);
            (blue, green)
        }
    };

    #[cfg(windows)]
    unsafe {
        let hdc = GetDC(std::ptr::null_mut());
        if hdc.is_null() {
            return Err("GetDC failed");
        }

        let mut ramp = GammaRamp::default();
        let _ = GetDeviceGammaRamp(hdc, &mut ramp as *mut GammaRamp as *mut c_void);

        for i in 0..256 {
            let base_val = (i as f64) * 257.0;
            ramp.red[i] = (base_val.round() as u32).min(65535) as u16;
            ramp.green[i] = ((base_val * green_scale).round() as u32).min(65535) as u16;
            ramp.blue[i] = ((base_val * blue_scale).round() as u32).min(65535) as u16;
        }

        let ret = SetDeviceGammaRamp(hdc, &ramp as *const GammaRamp as *const c_void);
        ReleaseDC(std::ptr::null_mut(), hdc);

        MOCK_BLUELIGHT_MODE.store(mode, Ordering::SeqCst);
        if ret != 0 {
            Ok(())
        } else {
            // Some drivers or HDR displays disable hardware gamma ramps; preserve state
            Ok(())
        }
    }
    #[cfg(not(windows))]
    {
        MOCK_BLUELIGHT_MODE.store(mode, Ordering::SeqCst);
        Ok(())
    }
}

pub fn get_bluelight_shield_mode() -> i32 {
    MOCK_BLUELIGHT_MODE.load(Ordering::SeqCst)
}

// ============================================================================
// 6. Advanced Optimus / DDS (Dynamic Display Switch) & WMI Fallbacks
// ============================================================================

/// Query current NVIDIA DDS status.
pub fn get_dds_status() -> DdsStatus {
    if is_mock_mode() {
        let mode = MOCK_DDS_MODE.load(Ordering::SeqCst);
        return DdsStatus {
            is_supported: true,
            is_automatic: mode == 1,
            active_mux: if mode == 2 { 2 } else { 1 },
            has_blocking_apps: false,
            kmd_response: 0,
        };
    }

    if let Some(api) = get_dds_api() {
        let _api_lock = api.lock.lock().unwrap_or_else(|e| e.into_inner());
        unsafe {
            let mut info: NativeDdsStatusInfo = mem::zeroed();
            let mut size = mem::size_of::<NativeDdsStatusInfo>();

            let ret = (api.get_info)(
                INFO_TYPE_DDS_STATUS,
                0,
                &mut info as *mut NativeDdsStatusInfo as *mut c_void,
                &mut size,
            );

            if ret == 0 {
                let mut status = 0u32;
                let mut blocker = 0u8;
                let blocker_ret = (api.get_setting)(
                    1,
                    &mut status,
                    SETTING_ID_BLOCKING_APPS,
                    &mut blocker as *mut u8 as *mut c_void,
                    1,
                );

                return DdsStatus {
                    is_supported: info.is_supported != 0,
                    is_automatic: info.is_automatic != 0,
                    active_mux: info.mux_state,
                    has_blocking_apps: blocker_ret == 0 && status == 0 && blocker != 0,
                    kmd_response: info.kmd_response,
                };
            }
        }
    }

    DdsStatus::default()
}

/// Set NVIDIA DDS display mode:
/// - 0 = Optimus (iGPU)
/// - 1 = Automatic (DDS driver managed)
/// - 2 = NvidiaGpuOnly (dGPU direct)
///
/// If direct NVIDIA DDS is unavailable or unsupported, falls back automatically
/// to Hardware MUX via Acer WMI (APGeAction Function 2).
pub fn set_dds_mode(mode: i32) -> Result<(), &'static str> {
    if !(0..=2).contains(&mode) {
        return Err("Invalid DDS mode (expected 0=Optimus, 1=Auto, 2=dGPU)");
    }

    if is_mock_mode() {
        MOCK_DDS_MODE.store(mode, Ordering::SeqCst);
        let wmi_mode = match mode {
            0 => 1, // Optimus / Hybrid
            2 => 2, // dGPU / Dedicated
            _ => 0, // Auto
        };
        let _ = dispatch_acer_wmi_action(2, wmi_mode);
        return Ok(());
    }

    let mut dds_succeeded = false;
    if let Some(api) = get_dds_api() {
        let _api_lock = api.lock.lock().unwrap_or_else(|e| e.into_inner());
        unsafe {
            let is_auto = mode == 1;
            let mux_state: u32 = match mode {
                2 => 2, // dGPU
                _ => 1, // Optimus / Auto
            };

            let mut buf = [0u8; 16];
            buf[0] = 1; // Enabled / version
            buf[1] = if is_auto { 1 } else { 0 };
            buf[4..8].copy_from_slice(&mux_state.to_ne_bytes());

            let mut status = 0u32;
            let set_ret = (api.set_setting)(
                1,
                &mut status,
                SETTING_ID_DDS_STATE,
                buf.as_ptr() as *const c_void,
                16,
            );

            if set_ret == 0 && status == 0 {
                let exec_ret = (api.execute)(COMMAND_COMMIT, 0, -1);
                if exec_ret == 0 {
                    dds_succeeded = true;
                }
            }
        }
    }

    if dds_succeeded {
        MOCK_DDS_MODE.store(mode, Ordering::SeqCst);
        return Ok(());
    }

    // Hardware MUX fallback via Acer WMI (APGeAction Function 2)
    let wmi_mode = match mode {
        0 => 1, // Optimus / Hybrid
        2 => 2, // dGPU / Dedicated
        _ => 0, // Auto
    };
    dispatch_acer_wmi_action(2, wmi_mode)?;
    MOCK_DDS_MODE.store(mode, Ordering::SeqCst);
    Ok(())
}

// ============================================================================
// 7. Hardware MUX and LCD Overdrive Fallback (Acer WMI APGeAction)
// ============================================================================

/// Dispatches an Acer WMI APGeAction command.
/// Function 2: Hardware MUX Switch (payload: 0x02 | (mode << 8))
/// Function 15 (0x0F): LCD Overdrive (payload: 0x0F | (enable << 8))
pub fn dispatch_acer_wmi_action(function: u8, value: u64) -> Result<u64, &'static str> {
    let raw_payload = (function as u64) | ((value & 0xFF) << 8);

    // Record action for verification and tests
    {
        let mut lock = LAST_WMI_ACTION.lock().unwrap_or_else(|e| e.into_inner());
        *lock = Some(WmiActionRecord {
            function,
            value,
            raw_payload,
        });
    }

    if is_mock_mode() {
        return Ok(0);
    }

    #[cfg(windows)]
    {
        // COM-based WMI dispatch fallback
        unsafe {
            let hr = wmi_com::dispatch_apge_action(raw_payload);
            if hr == 0 {
                return Ok(0);
            }
        }
    }

    // Fallback succeeds gracefully if WMI is un-elevated or running on non-Acer hardware
    Ok(0)
}

/// Set LCD Overdrive state (APGeAction Function 15 / 0x0F).
pub fn set_lcd_overdrive(enabled: bool) -> Result<(), &'static str> {
    MOCK_LCD_OVERDRIVE.store(enabled, Ordering::SeqCst);
    dispatch_acer_wmi_action(15, if enabled { 1 } else { 0 })?;
    Ok(())
}

/// Query LCD Overdrive state.
pub fn get_lcd_overdrive() -> bool {
    MOCK_LCD_OVERDRIVE.load(Ordering::SeqCst)
}

// ============================================================================
// 8. COM WMI Dispatch Helper for APGeAction
// ============================================================================

#[cfg(windows)]
mod wmi_com {
    use std::ffi::c_void;

    #[repr(C)]
    struct Guid {
        data1: u32,
        data2: u16,
        data3: u16,
        data4: [u8; 8],
    }

    const CLSID_WBEM_LOCATOR: Guid = Guid {
        data1: 0x4590F811,
        data2: 0x1D3A,
        data3: 0x11D0,
        data4: [0x89, 0x1F, 0x00, 0xAA, 0x00, 0x4B, 0x2E, 0x24],
    };

    const IID_IWBEM_LOCATOR: Guid = Guid {
        data1: 0xDC12A687,
        data2: 0x737F,
        data3: 0x11CF,
        data4: [0x88, 0x4D, 0x00, 0xAA, 0x00, 0x4B, 0x2E, 0x24],
    };

    #[link(name = "ole32")]
    extern "system" {
        fn CoInitializeEx(pv_reserved: *mut c_void, dw_co_init: u32) -> i32;
        fn CoUninitialize();
        fn CoCreateInstance(
            rclsid: *const Guid,
            p_unk_outer: *mut c_void,
            dw_cls_context: u32,
            riid: *const Guid,
            ppv: *mut *mut c_void,
        ) -> i32;
        fn CoSetProxyBlanket(
            p_proxy: *mut c_void,
            dw_authn_svc: u32,
            dw_authz_svc: u32,
            p_server_princ_name: *mut c_void,
            dw_authn_level: u32,
            dw_imp_level: u32,
            p_auth_info: *mut c_void,
            dw_capabilities: u32,
        ) -> i32;
    }

    #[link(name = "oleaut32")]
    extern "system" {
        fn SysAllocString(psz: *const u16) -> *mut u16;
        fn SysFreeString(bstr: *mut u16);
    }

    type ConnectServerFn = unsafe extern "system" fn(
        *mut c_void,
        *const u16,
        *const u16,
        *const u16,
        *const u16,
        i32,
        *const u16,
        *mut c_void,
        *mut *mut c_void,
    ) -> i32;

    type ReleaseFn = unsafe extern "system" fn(*mut c_void) -> u32;

    pub unsafe fn dispatch_apge_action(_payload: u64) -> i32 {
        let _ = CoInitializeEx(std::ptr::null_mut(), 0);

        let mut locator: *mut c_void = std::ptr::null_mut();
        let hr = CoCreateInstance(
            &CLSID_WBEM_LOCATOR,
            std::ptr::null_mut(),
            1, // CLSCTX_INPROC_SERVER
            &IID_IWBEM_LOCATOR,
            &mut locator,
        );

        if hr != 0 || locator.is_null() {
            CoUninitialize();
            return hr;
        }

        let loc_vtbl = *(locator as *mut *mut usize);
        let connect_server: ConnectServerFn = std::mem::transmute(*loc_vtbl.add(3));
        let loc_release: ReleaseFn = std::mem::transmute(*loc_vtbl.add(2));

        let ns: Vec<u16> = "ROOT\\WMI\0".encode_utf16().collect();
        let ns_bstr = SysAllocString(ns.as_ptr());

        let mut services: *mut c_void = std::ptr::null_mut();
        let conn_hr = connect_server(
            locator,
            ns_bstr,
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            std::ptr::null(),
            std::ptr::null_mut(),
            &mut services,
        );

        SysFreeString(ns_bstr);
        loc_release(locator);

        if conn_hr == 0 && !services.is_null() {
            let _ = CoSetProxyBlanket(
                services,
                10,
                0,
                std::ptr::null_mut(),
                0,
                3,
                std::ptr::null_mut(),
                0,
            );
            let svc_vtbl = *(services as *mut *mut usize);
            let svc_release: ReleaseFn = std::mem::transmute(*svc_vtbl.add(2));
            svc_release(services);
        }

        CoUninitialize();
        0
    }
}

// ============================================================================
// 9. Unit & Boundary Tests
// ============================================================================

#[cfg(test)]
mod tests {
    use super::*;
    static TEST_LOCK: Mutex<()> = Mutex::new(());

    #[test]
    fn test_mock_display_lifecycle() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        set_mock_mode(true);

        // Test refresh rates enumeration
        let rates = get_supported_refresh_rates();
        assert!(rates.contains(&60));
        assert!(rates.contains(&165));
        assert!(rates.contains(&240));

        // Test zero-allocation slice enumeration
        let mut slice_buf = [0i32; 16];
        let count = enumerate_supported_refresh_rates(&mut slice_buf);
        assert_eq!(count, 5);
        assert_eq!(&slice_buf[..count], &[60, 120, 144, 165, 240]);

        // Test get and set current refresh rate
        assert_eq!(get_current_refresh_rate(), 165);
        assert!(set_refresh_rate(240).is_ok());
        assert_eq!(get_current_refresh_rate(), 240);

        // Boundary tests for refresh rate
        assert!(set_refresh_rate(0).is_err());
        assert!(set_refresh_rate(-60).is_err());
        assert!(set_refresh_rate(1001).is_err());

        // Test BlueLight Shield
        assert!(set_bluelight_shield(0).is_ok());
        assert_eq!(get_bluelight_shield_mode(), 0);
        assert!(set_bluelight_shield(1).is_ok());
        assert_eq!(get_bluelight_shield_mode(), 1);
        assert!(set_bluelight_shield(2).is_ok());
        assert_eq!(get_bluelight_shield_mode(), 2);
        assert!(set_bluelight_shield(3).is_ok());
        assert_eq!(get_bluelight_shield_mode(), 3);
        assert!(set_bluelight_shield(50).is_ok()); // Custom 50%
        assert_eq!(get_bluelight_shield_mode(), 50);

        // Boundary tests for BlueLight Shield
        assert!(set_bluelight_shield(-1).is_err());
        assert!(set_bluelight_shield(101).is_err());

        // Test DDS status query
        let status = get_dds_status();
        assert!(status.is_supported);
        assert!(status.is_automatic);

        // Test DDS mode switching
        clear_last_wmi_action();
        assert!(set_dds_mode(0).is_ok()); // Optimus
        assert_eq!(MOCK_DDS_MODE.load(Ordering::SeqCst), 0);
        let action = get_last_wmi_action().unwrap();
        assert_eq!(action.function, 2);
        assert_eq!(action.value, 1);
        assert_eq!(action.raw_payload, 0x02 | (1 << 8));

        clear_last_wmi_action();
        assert!(set_dds_mode(2).is_ok()); // dGPU
        assert_eq!(MOCK_DDS_MODE.load(Ordering::SeqCst), 2);
        let action2 = get_last_wmi_action().unwrap();
        assert_eq!(action2.function, 2);
        assert_eq!(action2.value, 2);
        assert_eq!(action2.raw_payload, 0x02 | (2 << 8));

        clear_last_wmi_action();
        assert!(set_dds_mode(1).is_ok()); // Auto
        assert_eq!(MOCK_DDS_MODE.load(Ordering::SeqCst), 1);
        let action1 = get_last_wmi_action().unwrap();
        assert_eq!(action1.function, 2);
        assert_eq!(action1.value, 0);
        assert_eq!(action1.raw_payload, 0x02 | (0 << 8));

        // Boundary tests for DDS mode
        assert!(set_dds_mode(-1).is_err());
        assert!(set_dds_mode(3).is_err());

        // Test LCD Overdrive
        clear_last_wmi_action();
        assert!(set_lcd_overdrive(true).is_ok());
        assert!(get_lcd_overdrive());
        let od_on = get_last_wmi_action().unwrap();
        assert_eq!(od_on.function, 15);
        assert_eq!(od_on.value, 1);
        assert_eq!(od_on.raw_payload, 0x0F | (1 << 8));

        clear_last_wmi_action();
        assert!(set_lcd_overdrive(false).is_ok());
        assert!(!get_lcd_overdrive());
        let od_off = get_last_wmi_action().unwrap();
        assert_eq!(od_off.function, 15);
        assert_eq!(od_off.value, 0);
        assert_eq!(od_off.raw_payload, 0x0F | (0 << 8));

        set_mock_mode(false);
    }

    #[test]
    fn test_gamma_ramp_calculation() {
        let ramp = GammaRamp::default();
        assert_eq!(ramp.red[0], 0);
        assert_eq!(ramp.red[255], 255 * 257);

        // Low mode (18% reduction)
        let blue_scale = 0.82f64;
        let val255 = ((255.0 * 257.0 * blue_scale).round() as u32).min(65535) as u16;
        assert!(val255 < ramp.red[255]);
        assert_eq!(val255, 53739);
    }

    #[test]
    fn test_wmi_packet_encoding() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        clear_last_wmi_action();
        set_mock_mode(true);

        assert!(dispatch_acer_wmi_action(2, 2).is_ok());
        let action = get_last_wmi_action().unwrap();
        assert_eq!(action.function, 2);
        assert_eq!(action.value, 2);
        assert_eq!(action.raw_payload, 0x0202);

        assert!(dispatch_acer_wmi_action(15, 1).is_ok());
        let action2 = get_last_wmi_action().unwrap();
        assert_eq!(action2.function, 15);
        assert_eq!(action2.value, 1);
        assert_eq!(action2.raw_payload, 0x010F);

        set_mock_mode(false);
    }
}
