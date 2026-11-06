//! NVAPI bridge.
//!
//! Replaces the managed `Marshal.GetDelegateForFunctionPointer` + P/Invoke path with
//! a native runtime binding to `nvapi64.dll`:
//!
//! * `LoadLibraryW` + `GetProcAddress("nvapi_QueryInterface")` once, then the
//!   interface IDs are resolved once into plain function pointers.
//! * No marshalling, no delegate allocation, no GC handles, no `ref` struct copies
//!   per sample: a GPU usage poll is a single indirect call on a caller-owned struct.
//! * Function pointers are cached in an immutable `NvApi` snapshot behind a mutex,
//!   so the hot read path never touches the loader again.
//! * After 3 consecutive failures the handle table is dropped so the next poll
//!   re-enumerates (survives driver reload / resume from sleep), matching the
//!   managed circuit-breaker behaviour.

use std::ffi::c_void;
use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;

const NVAPI_INITIALIZE: u32 = 0x0150_E828;
const NVAPI_ENUM_PHYSICAL_GPUS: u32 = 0xE5AC_92AA;
const NVAPI_GPU_GET_USAGES: u32 = 0x189A_1FDF;
const NVAPI_GPU_GET_USAGES_FALLBACK: u32 = 0x189A_1F42;
const NVAPI_GPU_GET_THERMAL_SETTINGS: u32 = 0xE364_0A56;

const MAX_PHYSICAL_GPUS: usize = 64;
/// `NvAPI_GPU_GetUsages` indexes: 3 is the GPU core utilization percentage.
const GPU_CORE_USAGE_INDEX: usize = 3;

type NvQueryInterface = unsafe extern "C" fn(u32) -> *mut c_void;
type NvInitialize = unsafe extern "C" fn() -> i32;
type NvEnumPhysicalGpus = unsafe extern "C" fn(*mut usize, *mut u32) -> i32;
type NvGpuGetUsages = unsafe extern "C" fn(*mut c_void, *mut NvUsages) -> i32;
type NvGpuGetThermalSettings =
    unsafe extern "C" fn(*mut c_void, u32, *mut NvThermalSettings) -> i32;

#[link(name = "kernel32")]
extern "system" {
    fn LoadLibraryW(name: *const u16) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const i8) -> *mut c_void;
    fn FreeLibrary(module: *mut c_void) -> i32;
}

/// `NV_GPU_THERMAL_SETTINGS_V2` sensor entry.
#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct NvThermalSensor {
    pub controller: u32,
    pub default_min_temp: u32,
    pub default_max_temp: u32,
    pub current_temp: u32,
    pub target: i8,
    pub _pad: [u8; 3],
}

/// `NV_GPU_THERMAL_SETTINGS_V2`.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NvThermalSettings {
    pub version: u32,
    pub count: u32,
    pub sensor: [NvThermalSensor; 3],
}

impl Default for NvThermalSettings {
    fn default() -> Self {
        Self {
            version: (std::mem::size_of::<NvThermalSettings>() as u32) | 0x0001_0000,
            count: 0,
            sensor: [NvThermalSensor::default(); 3],
        }
    }
}

/// `NV_GPU_GETUSAGES` (version + 33 usage counters).
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NvUsages {
    pub version: u32,
    pub usages: [u32; 33],
}

impl Default for NvUsages {
    fn default() -> Self {
        Self {
            version: (std::mem::size_of::<NvUsages>() as u32) | 0x0001_0000,
            usages: [0; 33],
        }
    }
}

struct NvApi {
    module: *mut c_void,
    enum_gpus: NvEnumPhysicalGpus,
    get_usages: NvGpuGetUsages,
    get_thermal: Option<NvGpuGetThermalSettings>,
    handles: [usize; MAX_PHYSICAL_GPUS],
    count: usize,
}

// The function pointers are immutable after construction and NVAPI is thread-safe
// for the queries we make; the whole struct is only reachable behind a mutex.
unsafe impl Send for NvApi {}

impl Drop for NvApi {
    fn drop(&mut self) {
        if !self.module.is_null() {
            unsafe {
                FreeLibrary(self.module);
            }
            self.module = std::ptr::null_mut();
        }
    }
}

impl NvApi {
    fn load() -> Option<NvApi> {
        let wide: Vec<u16> = OsStr::new("nvapi64.dll")
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let module = unsafe { LoadLibraryW(wide.as_ptr()) };
        if module.is_null() {
            return None;
        }

        let query =
            unsafe { GetProcAddress(module, b"nvapi_QueryInterface\0".as_ptr() as *const i8) };
        if query.is_null() {
            unsafe {
                FreeLibrary(module);
            }
            return None;
        }
        let query: NvQueryInterface = unsafe { std::mem::transmute(query) };

        let init_ptr = unsafe { query(NVAPI_INITIALIZE) };
        if init_ptr.is_null() {
            unsafe {
                FreeLibrary(module);
            }
            return None;
        }
        let init: NvInitialize = unsafe { std::mem::transmute(init_ptr) };
        if unsafe { init() } != 0 {
            unsafe {
                FreeLibrary(module);
            }
            return None;
        }

        let enum_ptr = unsafe { query(NVAPI_ENUM_PHYSICAL_GPUS) };
        if enum_ptr.is_null() {
            unsafe {
                FreeLibrary(module);
            }
            return None;
        }
        let enum_gpus: NvEnumPhysicalGpus = unsafe { std::mem::transmute(enum_ptr) };

        let mut handles = [0usize; MAX_PHYSICAL_GPUS];
        let mut count: u32 = 0;
        if unsafe { enum_gpus(handles.as_mut_ptr(), &mut count) } != 0 || count == 0 {
            unsafe {
                FreeLibrary(module);
            }
            return None;
        }

        let mut usages_ptr = unsafe { query(NVAPI_GPU_GET_USAGES) };
        if usages_ptr.is_null() {
            usages_ptr = unsafe { query(NVAPI_GPU_GET_USAGES_FALLBACK) };
        }
        if usages_ptr.is_null() {
            unsafe {
                FreeLibrary(module);
            }
            return None;
        }
        let get_usages: NvGpuGetUsages = unsafe { std::mem::transmute(usages_ptr) };

        let thermal_ptr = unsafe { query(NVAPI_GPU_GET_THERMAL_SETTINGS) };
        let get_thermal = if thermal_ptr.is_null() {
            None
        } else {
            Some(unsafe {
                std::mem::transmute::<*mut c_void, NvGpuGetThermalSettings>(thermal_ptr)
            })
        };

        Some(NvApi {
            module,
            enum_gpus,
            get_usages,
            get_thermal,
            handles,
            count: (count as usize).min(MAX_PHYSICAL_GPUS),
        })
    }

    /// Highest GPU core utilization across all physical GPUs, 0..=100.
    fn sample_usage(&mut self) -> Option<f64> {
        if self.count == 0 {
            return None;
        }
        // Reusable, stack-allocated query struct: no per-sample heap traffic.
        let mut usages = NvUsages::default();
        let mut max_usage = 0.0_f64;
        let mut any_read = false;

        for i in 0..self.count {
            let handle = self.handles[i] as *mut c_void;
            if handle.is_null() {
                continue;
            }
            usages.version = (std::mem::size_of::<NvUsages>() as u32) | 0x0001_0000;
            usages.usages = [0; 33];
            if unsafe { (self.get_usages)(handle, &mut usages) } == 0 {
                let core = usages.usages[GPU_CORE_USAGE_INDEX];
                if core <= 100 {
                    max_usage = max_usage.max(core as f64);
                    any_read = true;
                }
            }
        }

        if any_read {
            Some(max_usage.clamp(0.0, 100.0))
        } else {
            None
        }
    }

    /// GPU core temperature in Celsius from the first valid thermal sensor.
    fn sample_temperature(&mut self) -> Option<f64> {
        let get_thermal = self.get_thermal?;
        for i in 0..self.count {
            let handle = self.handles[i] as *mut c_void;
            if handle.is_null() {
                continue;
            }
            let mut settings = NvThermalSettings::default();
            let status = unsafe { get_thermal(handle, 0, &mut settings) };
            if status != 0 || settings.count == 0 || settings.count > 3 {
                continue;
            }
            for s in 0..settings.count as usize {
                let sensor = settings.sensor[s];
                // Reject obviously bogus readings instead of trusting the ABI blindly.
                if sensor.controller <= 2 && sensor.current_temp > 0 && sensor.current_temp <= 150 {
                    return Some(sensor.current_temp as f64);
                }
            }
        }
        None
    }

    /// Drops the current `NvApi` when the driver stops answering, so the next poll
    /// re-loads the library and re-enumerates handles.
    fn reenumerate(&mut self) -> bool {
        // Re-run enumeration against the already-loaded module.
        let mut handles = [0usize; MAX_PHYSICAL_GPUS];
        let mut count: u32 = 0;
        if unsafe { (self.enum_gpus)(handles.as_mut_ptr(), &mut count) } != 0 || count == 0 {
            return false;
        }
        self.handles = handles;
        self.count = (count as usize).min(MAX_PHYSICAL_GPUS);
        true
    }
}

#[derive(Default)]
struct NvState {
    api: Option<NvApi>,
    attempted: bool,
    consecutive_failures: u32,
}

impl NvState {
    /// 0 = not available, 1 = available.
    fn ensure(&mut self) -> bool {
        if self.api.is_some() {
            return true;
        }
        if self.attempted {
            // Only retry after a failure streak (see `record_failure`).
            return false;
        }
        self.attempted = true;
        self.api = NvApi::load();
        self.api.is_some()
    }

    fn record_failure(&mut self) {
        self.consecutive_failures = self.consecutive_failures.saturating_add(1);
        if self.consecutive_failures >= 3 {
            self.consecutive_failures = 0;
            let reenumerated = self
                .api
                .as_mut()
                .map(|api| api.reenumerate())
                .unwrap_or(false);
            if !reenumerated {
                // Full reload on the next call.
                self.api = None;
                self.attempted = false;
            }
        }
    }

    fn record_success(&mut self) {
        self.consecutive_failures = 0;
    }
}

static NV_STATE: std::sync::Mutex<NvState> = std::sync::Mutex::new(NvState {
    api: None,
    attempted: false,
    consecutive_failures: 0,
});

fn with_nv<R>(_default: R, f: impl FnOnce(&mut NvState) -> R) -> R {
    let mut guard = NV_STATE.lock().unwrap_or_else(|e| e.into_inner());
    f(&mut guard)
}

pub fn is_available() -> bool {
    with_nv(false, |state| state.ensure())
}

/// GPU core utilization percentage, or `None` when NVAPI is unavailable.
pub fn gpu_load() -> Option<f64> {
    with_nv(None, |state| {
        if !state.ensure() {
            return None;
        }
        let api = state.api.as_mut()?;
        match api.sample_usage() {
            Some(load) => {
                state.record_success();
                Some(load)
            }
            None => {
                state.record_failure();
                None
            }
        }
    })
}

/// GPU core temperature in Celsius, or `None` when unavailable/implausible.
pub fn gpu_temperature() -> Option<f64> {
    with_nv(None, |state| {
        if !state.ensure() {
            return None;
        }
        let api = state.api.as_mut()?;
        let temp = api.sample_temperature();
        if temp.is_some() {
            state.record_success();
        }
        temp
    })
}

/// Number of physical GPUs NVAPI reported (0 when unavailable).
pub fn gpu_count() -> u32 {
    with_nv(0, |state| {
        if !state.ensure() {
            return 0;
        }
        state.api.as_ref().map(|api| api.count as u32).unwrap_or(0)
    })
}

/// Releases the NVAPI module handle. The next call re-initialises.
pub fn shutdown() {
    let mut guard = NV_STATE.lock().unwrap_or_else(|e| e.into_inner());
    guard.api = None;
    guard.attempted = false;
    guard.consecutive_failures = 0;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn struct_layouts_match_nvapi_versions() {
        // NvAPI_GPU_GetUsages v2 = 4 + 33 * 4 = 136 bytes.
        assert_eq!(std::mem::size_of::<NvUsages>(), 136);
        assert_eq!(NvUsages::default().version, 136 | 0x10000);
        // NvAPI_GPU_GetThermalSettings v2 = 4 + 4 + 3 * 20 = 68 bytes.
        assert_eq!(std::mem::size_of::<NvThermalSettings>(), 68);
        assert_eq!(NvThermalSettings::default().version, 68 | 0x10000);
    }

    #[test]
    fn queries_never_panic_without_nvapi() {
        // On a machine without an NVIDIA driver these must degrade to None/0,
        // never abort across the FFI boundary.
        let _ = is_available();
        let load = gpu_load();
        assert!(load.is_none() || (0.0..=100.0).contains(&load.unwrap()));
        let temp = gpu_temperature();
        assert!(temp.is_none() || (0.0..=150.0).contains(&temp.unwrap()));
        let _ = gpu_count();
        shutdown();
    }

    #[test]
    fn shutdown_resets_attempt_state() {
        let _ = is_available();
        shutdown();
        let state = NV_STATE.lock().unwrap_or_else(|e| e.into_inner());
        assert!(state.api.is_none());
        assert!(!state.attempted);
    }
}
