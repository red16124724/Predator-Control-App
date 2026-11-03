use std::ffi::c_void;
use std::ptr::null_mut;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, RwLock};
use std::thread::{self, JoinHandle};
use std::time::Duration;

#[repr(C)]
#[derive(Copy, Clone, Debug, Default)]
pub struct NativeTelemetrySnapshot {
    pub cpu_load: f64,
    pub gpu_load: f64,
    pub cpu_temp: f64,
    pub gpu_temp: f64,
    pub cpu_fan_rpm: u32,
    pub gpu_fan_rpm: u32,
    pub current_pl1: u32,
    pub current_pl2: u32,
    pub timestamp_ms: u64,
}

#[repr(C)]
struct FileTime {
    dw_low_date_time: u32,
    dw_high_date_time: u32,
}

impl FileTime {
    fn to_u64(&self) -> u64 {
        ((self.dw_high_date_time as u64) << 32) | (self.dw_low_date_time as u64)
    }
}

#[repr(C)]
struct NvUsages {
    version: u32,
    usages: [u32; 33],
}

type NvApiQueryInterfaceFn = unsafe extern "C" fn(id: u32) -> *mut c_void;
type NvApiInitializeFn = unsafe extern "C" fn() -> i32;
type NvApiEnumPhysicalGpusFn = unsafe extern "C" fn(gpu_handles: *mut *mut c_void, gpu_count: *mut i32) -> i32;
type NvApiGpuGetUsagesFn = unsafe extern "C" fn(gpu_handle: *mut c_void, usages: *mut NvUsages) -> i32;

#[link(name = "kernel32")]
#[link(name = "winmm")]
extern "system" {
    fn LoadLibraryW(lpLibFileName: *const u16) -> *mut c_void;
    fn FreeLibrary(hLibModule: *mut c_void) -> i32;
    fn GetProcAddress(hModule: *mut c_void, lpProcName: *const i8) -> *mut c_void;
    fn GetSystemTimes(lpIdleTime: *mut FileTime, lpKernelTime: *mut FileTime, lpUserTime: *mut FileTime) -> i32;
    fn timeBeginPeriod(uPeriod: u32) -> u32;
    fn timeEndPeriod(uPeriod: u32) -> u32;
}

fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

pub struct NvApiState {
    _h_module: *mut c_void,
    get_usages: Option<NvApiGpuGetUsagesFn>,
    gpu_handles: [*mut c_void; 64],
    gpu_count: usize,
    available: bool,
}

unsafe impl Send for NvApiState {}
unsafe impl Sync for NvApiState {}

impl NvApiState {
    pub fn try_init() -> Self {
        let mut state = Self {
            _h_module: null_mut(),
            get_usages: None,
            gpu_handles: [null_mut(); 64],
            gpu_count: 0,
            available: false,
        };

        let wide = to_wide("nvapi64.dll");
        let h_mod = unsafe { LoadLibraryW(wide.as_ptr()) };
        if h_mod.is_null() {
            return state;
        }
        state._h_module = h_mod;

        let proc_name = c"nvapi_QueryInterface";
        let p_query = unsafe { GetProcAddress(h_mod, proc_name.as_ptr()) };
        if p_query.is_null() {
            return state;
        }

        let query_fn: NvApiQueryInterfaceFn = unsafe { std::mem::transmute(p_query) };

        // 0x0150E828 = NvAPI_Initialize
        let p_init = unsafe { query_fn(0x0150E828) };
        if p_init.is_null() {
            return state;
        }
        let init_fn: NvApiInitializeFn = unsafe { std::mem::transmute(p_init) };
        if unsafe { init_fn() } != 0 {
            return state;
        }

        // 0xE5AC92AA = NvAPI_EnumPhysicalGPUs
        let p_enum = unsafe { query_fn(0xE5AC92AA) };
        if p_enum.is_null() {
            return state;
        }
        let enum_fn: NvApiEnumPhysicalGpusFn = unsafe { std::mem::transmute(p_enum) };
        let mut count = 0i32;
        if unsafe { enum_fn(state.gpu_handles.as_mut_ptr(), &mut count) } != 0 || count <= 0 {
            return state;
        }
        state.gpu_count = (count as usize).min(64);

        // 0x189A1FDF = NvAPI_GPU_GetUsages
        let mut p_usages = unsafe { query_fn(0x189A1FDF) };
        if p_usages.is_null() {
            // Fallback ID 0x189A1F42
            p_usages = unsafe { query_fn(0x189A1F42) };
        }
        if !p_usages.is_null() {
            state.get_usages = Some(unsafe { std::mem::transmute(p_usages) });
            state.available = true;
        }

        state
    }

    pub fn sample_gpu_load(&self) -> Option<f64> {
        if !self.available || self.gpu_count == 0 {
            return None;
        }
        let get_usages = self.get_usages?;

        let mut max_usage = 0.0f64;
        let mut any_read = false;

        for i in 0..self.gpu_count {
            let handle = self.gpu_handles[i];
            if handle.is_null() {
                continue;
            }

            let mut usages_struct = NvUsages {
                version: (std::mem::size_of::<NvUsages>() as u32) | 0x10000,
                usages: [0; 33],
            };

            let res = unsafe { get_usages(handle, &mut usages_struct) };
            if res == 0 {
                let core_load = usages_struct.usages[3]; // Index 3 is GPU core usage
                if core_load <= 100 {
                    if (core_load as f64) > max_usage {
                        max_usage = core_load as f64;
                    }
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
}

impl Drop for NvApiState {
    fn drop(&mut self) {
        if !self._h_module.is_null() {
            unsafe { FreeLibrary(self._h_module) };
            self._h_module = null_mut();
        }
    }
}

pub struct CpuUsageSampler {
    prev_idle: u64,
    prev_kernel: u64,
    prev_user: u64,
    has_baseline: bool,
}

impl CpuUsageSampler {
    pub fn new() -> Self {
        Self {
            prev_idle: 0,
            prev_kernel: 0,
            prev_user: 0,
            has_baseline: false,
        }
    }

    pub fn sample(&mut self) -> Option<f64> {
        let mut idle = FileTime { dw_low_date_time: 0, dw_high_date_time: 0 };
        let mut kernel = FileTime { dw_low_date_time: 0, dw_high_date_time: 0 };
        let mut user = FileTime { dw_low_date_time: 0, dw_high_date_time: 0 };

        let res = unsafe { GetSystemTimes(&mut idle, &mut kernel, &mut user) };
        if res == 0 {
            return None;
        }

        let idle_u = idle.to_u64();
        let kernel_u = kernel.to_u64();
        let user_u = user.to_u64();

        if !self.has_baseline {
            self.prev_idle = idle_u;
            self.prev_kernel = kernel_u;
            self.prev_user = user_u;
            self.has_baseline = true;
            return None;
        }

        let idle_delta = if idle_u >= self.prev_idle { idle_u - self.prev_idle } else { 0 };
        let kernel_delta = if kernel_u >= self.prev_kernel { kernel_u - self.prev_kernel } else { 0 };
        let user_delta = if user_u >= self.prev_user { user_u - self.prev_user } else { 0 };

        self.prev_idle = idle_u;
        self.prev_kernel = kernel_u;
        self.prev_user = user_u;

        let total_system = kernel_delta + user_delta;
        if total_system == 0 {
            return None;
        }

        let load = (1.0 - ((idle_delta as f64) / (total_system as f64))) * 100.0;
        Some(load.clamp(0.0, 100.0))
    }
}

pub struct TelemetryWorker {
    is_running: Arc<AtomicBool>,
    interval_ms: Arc<AtomicU32>,
    snapshot: Arc<RwLock<NativeTelemetrySnapshot>>,
    worker_thread: Option<JoinHandle<()>>,
}

impl TelemetryWorker {
    pub fn new() -> Self {
        Self {
            is_running: Arc::new(AtomicBool::new(false)),
            interval_ms: Arc::new(AtomicU32::new(2000)),
            snapshot: Arc::new(RwLock::new(NativeTelemetrySnapshot::default())),
            worker_thread: None,
        }
    }

    pub fn start(&mut self) {
        if self.is_running.swap(true, Ordering::SeqCst) {
            return;
        }

        let is_running = self.is_running.clone();
        let interval_ms = self.interval_ms.clone();
        let snapshot = self.snapshot.clone();

        self.worker_thread = Some(thread::spawn(move || {
            unsafe { timeBeginPeriod(1) };
            struct TimePeriodGuard(u32);
            impl Drop for TimePeriodGuard {
                fn drop(&mut self) {
                    unsafe { timeEndPeriod(self.0) };
                }
            }
            let _period_guard = TimePeriodGuard(1);

            let mut cpu_sampler = CpuUsageSampler::new();
            let nvapi = NvApiState::try_init();

            while is_running.load(Ordering::Acquire) {
                let interval = interval_ms.load(Ordering::Acquire).max(250) as u64;

                let cpu_load = cpu_sampler.sample().unwrap_or(0.0);
                let gpu_load = nvapi.sample_gpu_load().unwrap_or(0.0);

                let now_ms = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map(|d| d.as_millis() as u64)
                    .unwrap_or(0);

                {
                    let mut snap = snapshot.write().unwrap_or_else(|e| e.into_inner());
                    snap.cpu_load = cpu_load;
                    snap.gpu_load = gpu_load;
                    snap.timestamp_ms = now_ms;
                }

                // Sleep in responsive slices to allow prompt exit on stop()
                let step = 50;
                let steps = interval / step;
                for _ in 0..steps {
                    if !is_running.load(Ordering::Acquire) {
                        break;
                    }
                    thread::sleep(Duration::from_millis(step));
                }
                let rem = interval % step;
                if rem > 0 && is_running.load(Ordering::Acquire) {
                    thread::sleep(Duration::from_millis(rem));
                }
            }
        }));
    }

    pub fn set_interval(&self, ms: u32) {
        self.interval_ms.store(ms.clamp(250, 10000), Ordering::Release);
    }

    pub fn get_snapshot(&self) -> NativeTelemetrySnapshot {
        *self.snapshot.read().unwrap_or_else(|e| e.into_inner())
    }

    pub fn stop(&mut self) {
        self.is_running.store(false, Ordering::SeqCst);
        if let Some(handle) = self.worker_thread.take() {
            let _ = handle.join();
        }
    }
}

impl Drop for TelemetryWorker {
    fn drop(&mut self) {
        self.stop();
    }
}
