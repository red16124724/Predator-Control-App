//! Native EC-HID transport for the Acer embedded controller feature reports.
//!
//! Ported from `EcHidDevice`: SetupAPI interface enumeration, `CreateFile` on the
//! HID device path, capability-driven report length, and the
//! `HidD_SetFeature` + `HidD_GetFeature` exchange.
//!
//! Why native:
//! * The device handle and the SetupAPI enumeration list are owned by an RAII
//!   `EcHid` value: `Drop` always closes the handle and destroys the info set, so
//!   a failed or abandoned open can never leak a kernel handle.
//! * The cross-process feature-report mutex (`Global\ECHIDFeatureVID_...`) is a
//!   real kernel mutex with a 500 ms timeout, not a managed `Mutex` wrapper.
//! * No `SafeFileHandle`, no GC finalizer thread, no P/Invoke transition per
//!   report: the entire exchange happens inside one native call.
//!
//! `hid.dll` is bound at runtime via `LoadLibraryW`/`GetProcAddress` (the MinGW
//! toolchain ships no `libhid.a` import library), mirroring the NVAPI module.

use std::cell::OnceCell;
use std::ffi::c_void;

const DIGCF_PRESENT: u32 = 0x02;
const DIGCF_DEVICEINTERFACE: u32 = 0x10;
const GENERIC_READ_WRITE: u32 = 0xC000_0000;
const FILE_SHARE_RW: u32 = 0x07;
const OPEN_EXISTING: u32 = 3;
const FILE_ATTRIBUTE_NORMAL: u32 = 0x80;
const INVALID_HANDLE_VALUE: isize = -1;

const DEFAULT_REPORT_LENGTH: usize = 65;
const MAX_REPORT_LENGTH: usize = 4096;
const MAX_DEVICE_PATHS: usize = 16;
const EXCHANGE_ATTEMPTS: u32 = 3;
const BUSY_TIMEOUT_MS: u32 = 500;

pub const ENEK_VID: u16 = 0x048D;
pub const ENEK_USAGE_PAGE: u16 = 0xFF00;
pub const ENEK_USAGE: u16 = 0x0001;
pub const ENEK_REPORT_LEN: usize = 17;
pub const ENEK_REPORT_ID: u8 = 0xA4;
pub const ENEK_TARGET_KEYBOARD: u8 = 0x21;
pub const ENEK_TARGET_COVER_LOGO: u8 = 0x83;
pub const ENEK_EFFECT_STATIC: u8 = 0x02;
pub const ENEK_ZONE_MASKS: [u8; 4] = [0x01, 0x02, 0x04, 0x08];

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct Guid {
    data1: u32,
    data2: u16,
    data3: u16,
    data4: [u8; 8],
}

#[repr(C)]
#[derive(Clone, Copy)]
struct SpDeviceInterfaceData {
    cb_size: u32,
    interface_class_guid: Guid,
    flags: u32,
    reserved: usize,
}

impl Default for SpDeviceInterfaceData {
    fn default() -> Self {
        Self {
            cb_size: std::mem::size_of::<SpDeviceInterfaceData>() as u32,
            interface_class_guid: Guid::default(),
            flags: 0,
            reserved: 0,
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct HiddAttributes {
    size: u32,
    vendor_id: u16,
    product_id: u16,
    version_number: u16,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct HidpCaps {
    usage: u16,
    usage_page: u16,
    input_report_byte_length: u16,
    output_report_byte_length: u16,
    feature_report_byte_length: u16,
    reserved: [u16; 17],
    number_link_collection_nodes: u16,
    number_input_button_caps: u16,
    number_input_value_caps: u16,
    number_input_data_indices: u16,
    number_output_button_caps: u16,
    number_output_value_caps: u16,
    number_output_data_indices: u16,
    number_feature_button_caps: u16,
    number_feature_value_caps: u16,
    number_feature_data_indices: u16,
}

/// `SP_DEVICE_INTERFACE_DETAIL_DATA_W` header: cbSize + flexible WCHAR path.
#[repr(C)]
#[derive(Clone, Copy)]
struct SpDeviceInterfaceDetailHeader {
    cb_size: u32,
}

#[link(name = "kernel32")]
extern "system" {
    fn CreateFileW(
        file_name: *const u16,
        desired_access: u32,
        share_mode: u32,
        security_attributes: *mut c_void,
        creation_disposition: u32,
        flags_and_attributes: u32,
        template_file: *mut c_void,
    ) -> *mut c_void;
    fn CloseHandle(handle: *mut c_void) -> i32;
    fn CreateMutexW(attributes: *mut c_void, initial_owner: i32, name: *const u16) -> *mut c_void;
    fn WaitForSingleObject(handle: *mut c_void, milliseconds: u32) -> u32;
    fn ReleaseMutex(handle: *mut c_void) -> i32;
    fn Sleep(milliseconds: u32);
    fn LoadLibraryW(name: *const u16) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const i8) -> *mut c_void;
}

#[link(name = "setupapi")]
extern "system" {
    fn SetupDiGetClassDevsW(
        class_guid: *const Guid,
        enumerator: *const u16,
        parent: *mut c_void,
        flags: u32,
    ) -> *mut c_void;
    fn SetupDiEnumDeviceInterfaces(
        device_info_set: *mut c_void,
        device_info_data: *mut c_void,
        interface_class_guid: *const Guid,
        member_index: u32,
        device_interface_data: *mut SpDeviceInterfaceData,
    ) -> i32;
    fn SetupDiGetDeviceInterfaceDetailW(
        device_info_set: *mut c_void,
        device_interface_data: *mut SpDeviceInterfaceData,
        device_interface_detail_data: *mut c_void,
        detail_size: u32,
        required_size: *mut u32,
        device_info_data: *mut c_void,
    ) -> i32;
    fn SetupDiDestroyDeviceInfoList(device_info_set: *mut c_void) -> i32;
}

type FnGetHidGuid = unsafe extern "system" fn(*mut Guid);
type FnGetAttributes = unsafe extern "system" fn(*mut c_void, *mut HiddAttributes) -> i32;
type FnGetPreparsed = unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> i32;
type FnFreePreparsed = unsafe extern "system" fn(*mut c_void) -> i32;
type FnGetCaps = unsafe extern "system" fn(*mut c_void, *mut HidpCaps) -> i32;
type FnGetFeature = unsafe extern "system" fn(*mut c_void, *mut u8, u32) -> i32;
type FnSetFeature = unsafe extern "system" fn(*mut c_void, *mut u8, u32) -> i32;

/// `hid.dll` entry points resolved once per process.
struct HidApi {
    get_hid_guid: FnGetHidGuid,
    get_attributes: FnGetAttributes,
    get_preparsed_data: FnGetPreparsed,
    free_preparsed_data: FnFreePreparsed,
    get_caps: FnGetCaps,
    get_feature: FnGetFeature,
    set_feature: FnSetFeature,
}

impl HidApi {
    fn load() -> Option<HidApi> {
        let wide: Vec<u16> = "hid.dll\0".encode_utf16().collect();
        let module = unsafe { LoadLibraryW(wide.as_ptr()) };
        if module.is_null() {
            return None;
        }

        unsafe fn resolve(module: *mut c_void, name: &[u8]) -> *mut c_void {
            GetProcAddress(module, name.as_ptr() as *const i8)
        }

        unsafe {
            let get_hid_guid = resolve(module, b"HidD_GetHidGuid\0");
            let get_attributes = resolve(module, b"HidD_GetAttributes\0");
            let get_preparsed_data = resolve(module, b"HidD_GetPreparsedData\0");
            let free_preparsed_data = resolve(module, b"HidD_FreePreparsedData\0");
            let get_caps = resolve(module, b"HidP_GetCaps\0");
            let get_feature = resolve(module, b"HidD_GetFeature\0");
            let set_feature = resolve(module, b"HidD_SetFeature\0");

            if get_hid_guid.is_null()
                || get_attributes.is_null()
                || get_preparsed_data.is_null()
                || free_preparsed_data.is_null()
                || get_caps.is_null()
                || get_feature.is_null()
                || set_feature.is_null()
            {
                return None;
            }

            Some(HidApi {
                get_hid_guid: std::mem::transmute(get_hid_guid),
                get_attributes: std::mem::transmute(get_attributes),
                get_preparsed_data: std::mem::transmute(get_preparsed_data),
                free_preparsed_data: std::mem::transmute(free_preparsed_data),
                get_caps: std::mem::transmute(get_caps),
                get_feature: std::mem::transmute(get_feature),
                set_feature: std::mem::transmute(set_feature),
            })
        }
    }
}

thread_local! {
    static HID_API: OnceCell<Option<HidApi>> = const { OnceCell::new() };
}

fn with_hid_api<R>(f: impl FnOnce(&HidApi) -> R) -> Option<R> {
    HID_API.with(|cell| cell.get_or_init(HidApi::load).as_ref().map(f))
}

fn read_report_length(handle: *mut c_void, api: &HidApi) -> usize {
    let mut preparsed: *mut c_void = std::ptr::null_mut();
    let mut length = DEFAULT_REPORT_LENGTH;
    if unsafe { (api.get_preparsed_data)(handle, &mut preparsed) } != 0 {
        let mut caps = HidpCaps::default();
        if unsafe { (api.get_caps)(preparsed, &mut caps) } >= 0
            && caps.feature_report_byte_length > 0
        {
            length = caps.feature_report_byte_length as usize;
        }
        unsafe { (api.free_preparsed_data)(preparsed) };
    }
    length.clamp(DEFAULT_REPORT_LENGTH, MAX_REPORT_LENGTH)
}

fn usage_matches(handle: *mut c_void, api: &HidApi, usage_page: u16, usage: u16) -> bool {
    let mut preparsed: *mut c_void = std::ptr::null_mut();
    let mut matches = true;
    if unsafe { (api.get_preparsed_data)(handle, &mut preparsed) } != 0 {
        let mut caps = HidpCaps::default();
        if unsafe { (api.get_caps)(preparsed, &mut caps) } >= 0 {
            matches = caps.usage_page == usage_page && caps.usage == usage;
        }
        unsafe { (api.free_preparsed_data)(preparsed) };
    }
    matches
}

/// Owns the open device handle and the inter-process feature-report mutex.
///
/// `Drop` closes both, so no error path can leak a kernel object.
pub struct EcHid {
    device: *mut c_void,
    mutex: *mut c_void,
    report_length: usize,
}

// The handle is exclusively owned and only used behind the global mutex.
unsafe impl Send for EcHid {}

impl EcHid {
    /// Enumerates HID interfaces and opens the first one matching VID/PID/usage.
    pub fn open(vendor_id: u16, product_id: u16, usage_page: u16, usage: u16) -> Option<EcHid> {
        with_hid_api(|api| Self::open_with(api, vendor_id, product_id, usage_page, usage)).flatten()
    }

    /// Enumerates HID interfaces and opens the ENEK5130 RGB controller.
    pub fn open_enek(vendor_id: u16, product_id: u16) -> Option<EcHid> {
        let vid = if vendor_id == 0 { ENEK_VID } else { vendor_id };
        with_hid_api(|api| Self::open_enek_with(api, vid, product_id)).flatten()
    }

    fn open_enek_with(api: &HidApi, vendor_id: u16, product_id: u16) -> Option<EcHid> {
        let mut hid_guid = Guid::default();
        unsafe { (api.get_hid_guid)(&mut hid_guid) };

        let info_set = unsafe {
            SetupDiGetClassDevsW(
                &hid_guid,
                std::ptr::null(),
                std::ptr::null_mut(),
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
            )
        };
        if info_set.is_null() || info_set == INVALID_HANDLE_VALUE as *mut c_void {
            return None;
        }

        let mut device = std::ptr::null_mut();
        let mut report_length = ENEK_REPORT_LEN;

        let mut index = 0u32;
        'outer: while index < 64 {
            let mut did = SpDeviceInterfaceData::default();
            let ok = unsafe {
                SetupDiEnumDeviceInterfaces(
                    info_set,
                    std::ptr::null_mut(),
                    &hid_guid,
                    index,
                    &mut did,
                )
            };
            if ok == 0 {
                break;
            }
            index += 1;

            let mut required: u32 = 0;
            unsafe {
                SetupDiGetDeviceInterfaceDetailW(
                    info_set,
                    &mut did,
                    std::ptr::null_mut(),
                    0,
                    &mut required,
                    std::ptr::null_mut(),
                );
            }
            if required == 0 || required > 16 * 1024 {
                continue;
            }

            let words = (required as usize + 7) / 8;
            let mut buf: Vec<u64> = vec![0; words];
            let header = buf.as_mut_ptr() as *mut SpDeviceInterfaceDetailHeader;
            unsafe {
                (*header).cb_size = if cfg!(target_pointer_width = "64") {
                    8
                } else {
                    6
                };
            }
            let ok = unsafe {
                SetupDiGetDeviceInterfaceDetailW(
                    info_set,
                    &mut did,
                    buf.as_mut_ptr() as *mut c_void,
                    required,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                )
            };
            if ok == 0 {
                continue;
            }

            let path_ptr = unsafe { (buf.as_ptr() as *const u8).add(4) as *const u16 };
            let limit = (required as usize).saturating_sub(4) / 2;
            let path_len = unsafe {
                let mut n = 0usize;
                while n < limit && *path_ptr.add(n) != 0 {
                    n += 1;
                }
                n
            };
            if path_len == 0 {
                continue;
            }

            let path_slice = unsafe { std::slice::from_raw_parts(path_ptr, path_len) };
            let path_str = String::from_utf16_lossy(path_slice);
            let has_enek_tag = path_str.to_uppercase().contains("ENEK5130");

            let mut wide: Vec<u16> = path_slice.to_vec();
            wide.push(0);

            let mut handle = unsafe {
                CreateFileW(
                    wide.as_ptr(),
                    GENERIC_READ_WRITE,
                    FILE_SHARE_RW,
                    std::ptr::null_mut(),
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    std::ptr::null_mut(),
                )
            };
            if handle.is_null() || handle == INVALID_HANDLE_VALUE as *mut c_void {
                handle = unsafe {
                    CreateFileW(
                        wide.as_ptr(),
                        0,
                        FILE_SHARE_RW,
                        std::ptr::null_mut(),
                        OPEN_EXISTING,
                        FILE_ATTRIBUTE_NORMAL,
                        std::ptr::null_mut(),
                    )
                };
            }
            if handle.is_null() || handle == INVALID_HANDLE_VALUE as *mut c_void {
                continue;
            }

            let mut attributes = HiddAttributes {
                size: std::mem::size_of::<HiddAttributes>() as u32,
                ..Default::default()
            };
            let attrs_ok = unsafe { (api.get_attributes)(handle, &mut attributes) } != 0;

            let vid_match = attrs_ok && (attributes.vendor_id == vendor_id);
            let pid_match = product_id == 0 || (attrs_ok && attributes.product_id == product_id);

            let rep_len = read_report_length(handle, api);
            let usage_match = usage_matches(handle, api, ENEK_USAGE_PAGE, ENEK_USAGE);

            let is_enek = has_enek_tag
                || (vid_match && pid_match && (usage_match || rep_len == 17 || rep_len >= 11));

            if !is_enek {
                unsafe { CloseHandle(handle) };
                continue;
            }

            report_length = rep_len.max(ENEK_REPORT_LEN);
            device = handle;
            break 'outer;
        }

        unsafe { SetupDiDestroyDeviceInfoList(info_set) };

        if device.is_null() || device == INVALID_HANDLE_VALUE as *mut c_void {
            return None;
        }

        let mutex_name: Vec<u16> = format!(
            "Global\\ECHIDFeatureVID_{:04X}&PID_{:04X}\0",
            vendor_id, product_id
        )
        .encode_utf16()
        .collect();
        let mutex = unsafe { CreateMutexW(std::ptr::null_mut(), 0, mutex_name.as_ptr()) };

        Some(EcHid {
            device,
            mutex,
            report_length,
        })
    }

    fn open_with(
        api: &HidApi,
        vendor_id: u16,
        product_id: u16,
        usage_page: u16,
        usage: u16,
    ) -> Option<EcHid> {
        let mut hid_guid = Guid::default();
        unsafe { (api.get_hid_guid)(&mut hid_guid) };

        let info_set = unsafe {
            SetupDiGetClassDevsW(
                &hid_guid,
                std::ptr::null(),
                std::ptr::null_mut(),
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
            )
        };
        if info_set.is_null() || info_set == INVALID_HANDLE_VALUE as *mut c_void {
            return None;
        }

        let mut device = std::ptr::null_mut();
        let mut report_length = DEFAULT_REPORT_LENGTH;

        let mut index = 0u32;
        'outer: while index < 64 {
            let mut did = SpDeviceInterfaceData::default();
            let ok = unsafe {
                SetupDiEnumDeviceInterfaces(
                    info_set,
                    std::ptr::null_mut(),
                    &hid_guid,
                    index,
                    &mut did,
                )
            };
            if ok == 0 {
                break;
            }
            index += 1;

            let mut required: u32 = 0;
            unsafe {
                SetupDiGetDeviceInterfaceDetailW(
                    info_set,
                    &mut did,
                    std::ptr::null_mut(),
                    0,
                    &mut required,
                    std::ptr::null_mut(),
                );
            }
            if required == 0 || required > 16 * 1024 {
                continue;
            }

            // The wide detail buffer must be pointer aligned; allocate as u64 words.
            let words = (required as usize + 7) / 8;
            let mut buf: Vec<u64> = vec![0; words];
            let header = buf.as_mut_ptr() as *mut SpDeviceInterfaceDetailHeader;
            unsafe {
                (*header).cb_size = if cfg!(target_pointer_width = "64") {
                    8
                } else {
                    6
                };
            }
            let ok = unsafe {
                SetupDiGetDeviceInterfaceDetailW(
                    info_set,
                    &mut did,
                    buf.as_mut_ptr() as *mut c_void,
                    required,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                )
            };
            if ok == 0 {
                continue;
            }

            // The device path starts right after the 4-byte cbSize field.
            let path_ptr = unsafe { (buf.as_ptr() as *const u8).add(4) as *const u16 };
            let limit = (required as usize).saturating_sub(4) / 2;
            let path_len = unsafe {
                let mut n = 0usize;
                while n < limit && *path_ptr.add(n) != 0 {
                    n += 1;
                }
                n
            };
            if path_len == 0 {
                continue;
            }

            let mut wide: Vec<u16> =
                unsafe { std::slice::from_raw_parts(path_ptr, path_len) }.to_vec();
            wide.push(0);

            // Try read/write first, then read-only (some EC HID stacks reject write access).
            let mut handle = unsafe {
                CreateFileW(
                    wide.as_ptr(),
                    GENERIC_READ_WRITE,
                    FILE_SHARE_RW,
                    std::ptr::null_mut(),
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    std::ptr::null_mut(),
                )
            };
            if handle.is_null() || handle == INVALID_HANDLE_VALUE as *mut c_void {
                handle = unsafe {
                    CreateFileW(
                        wide.as_ptr(),
                        0,
                        FILE_SHARE_RW,
                        std::ptr::null_mut(),
                        OPEN_EXISTING,
                        FILE_ATTRIBUTE_NORMAL,
                        std::ptr::null_mut(),
                    )
                };
            }
            if handle.is_null() || handle == INVALID_HANDLE_VALUE as *mut c_void {
                continue;
            }

            // Match the VID/PID/usage; close and skip anything else.
            let mut attributes = HiddAttributes {
                size: std::mem::size_of::<HiddAttributes>() as u32,
                ..Default::default()
            };
            let attrs_ok = unsafe { (api.get_attributes)(handle, &mut attributes) } != 0;
            if attrs_ok
                && (attributes.vendor_id != vendor_id || attributes.product_id != product_id)
            {
                unsafe { CloseHandle(handle) };
                continue;
            }

            if usage_page != 0 && !usage_matches(handle, api, usage_page, usage) {
                unsafe { CloseHandle(handle) };
                continue;
            }

            report_length = read_report_length(handle, api);
            device = handle;
            break 'outer;
        }

        unsafe { SetupDiDestroyDeviceInfoList(info_set) };

        if device.is_null() || device == INVALID_HANDLE_VALUE as *mut c_void {
            return None;
        }

        // Global named mutex shared with any other process talking to the same EC.
        let mutex_name: Vec<u16> = format!(
            "Global\\ECHIDFeatureVID_{:04X}&PID_{:04X}\0",
            vendor_id, product_id
        )
        .encode_utf16()
        .collect();
        let mutex = unsafe { CreateMutexW(std::ptr::null_mut(), 0, mutex_name.as_ptr()) };
        // A null mutex just means no cross-process arbitration; keep going.

        Some(EcHid {
            device,
            mutex,
            report_length,
        })
    }

    pub fn report_length(&self) -> usize {
        self.report_length
    }

    pub fn is_open(&self) -> bool {
        !self.device.is_null()
    }

    fn lock(&self) -> bool {
        if self.mutex.is_null() {
            return false;
        }
        let result = unsafe { WaitForSingleObject(self.mutex, BUSY_TIMEOUT_MS) };
        result == 0 || result == 0x80 // WAIT_OBJECT_0 | WAIT_ABANDONED
    }

    fn unlock(&self, locked: bool) {
        if locked && !self.mutex.is_null() {
            unsafe { ReleaseMutex(self.mutex) };
        }
    }

    /// Sends `request` and reads the reply report into `out`.
    ///
    /// Returns the number of bytes written to `out`, or `None` on failure.
    /// The caller owns the report framing (report id, header, command bytes).
    /// Sends a raw feature report buffer to the device via HidD_SetFeature.
    pub fn send_feature_report(&self, report: &[u8]) -> bool {
        if !self.is_open() || report.is_empty() {
            return false;
        }
        let len = self
            .report_length
            .max(report.len())
            .clamp(11, MAX_REPORT_LENGTH);
        let mut buf = vec![0u8; len];
        let copy_len = report.len().min(len);
        buf[..copy_len].copy_from_slice(&report[..copy_len]);

        with_hid_api(|api| {
            let locked = self.lock();
            let ok = unsafe { (api.set_feature)(self.device, buf.as_mut_ptr(), len as u32) } != 0;
            self.unlock(locked);
            ok
        })
        .unwrap_or(false)
    }

    /// Sets 4 keyboard zone colors on ENEK5130 hardware via direct HID feature reports.
    pub fn set_enek_zones(
        &self,
        r1: u8,
        g1: u8,
        b1: u8,
        r2: u8,
        g2: u8,
        b2: u8,
        r3: u8,
        g3: u8,
        b3: u8,
        r4: u8,
        g4: u8,
        b4: u8,
        brightness: u8,
    ) -> bool {
        let colors = [(r1, g1, b1), (r2, g2, b2), (r3, g3, b3), (r4, g4, b4)];
        let b = brightness.min(100);
        let mut all_ok = true;
        for i in 0..4 {
            let (r, g, b_val) = colors[i];
            let pkt = build_enek_zone_report(i, r, g, b_val, b);
            if !self.send_feature_report(&pkt) {
                all_ok = false;
            }
        }
        all_ok
    }

    pub fn exchange(&self, request: &[u8], out: &mut [u8]) -> Option<usize> {
        if !self.is_open() || request.is_empty() {
            return None;
        }
        let rep_len = self
            .report_length
            .clamp(DEFAULT_REPORT_LENGTH, MAX_REPORT_LENGTH);
        if out.len() < rep_len {
            return None;
        }

        with_hid_api(|api| {
            for attempt in 0..EXCHANGE_ATTEMPTS {
                if attempt > 0 {
                    unsafe { Sleep(20) };
                }

                let locked = self.lock();
                if !self.mutex.is_null() && !locked {
                    continue;
                }

                let mut result = None;
                // Prime the feature buffer first: some EC stacks drop volatile BIOS mode
                // bits (e.g. Media/Fn state) if SetFeature arrives without a prior GetFeature.
                let mut pre = vec![0u8; rep_len];
                pre[0] = request[0];
                let _ = unsafe { (api.get_feature)(self.device, pre.as_mut_ptr(), rep_len as u32) };

                let mut req = vec![0u8; rep_len];
                let copy = request.len().min(rep_len);
                req[..copy].copy_from_slice(&request[..copy]);

                if unsafe { (api.set_feature)(self.device, req.as_mut_ptr(), rep_len as u32) } != 0
                {
                    unsafe { Sleep(20) };
                    let mut reply = vec![0u8; rep_len];
                    reply[0] = request[0];
                    if unsafe { (api.get_feature)(self.device, reply.as_mut_ptr(), rep_len as u32) }
                        != 0
                    {
                        out[..rep_len].copy_from_slice(&reply);
                        result = Some(rep_len);
                    }
                }

                self.unlock(locked);
                if result.is_some() {
                    return result;
                }
            }
            None
        })
        .flatten()
    }
}

impl Drop for EcHid {
    fn drop(&mut self) {
        if !self.device.is_null() && self.device != INVALID_HANDLE_VALUE as *mut c_void {
            unsafe { CloseHandle(self.device) };
            self.device = std::ptr::null_mut();
        }
        if !self.mutex.is_null() {
            unsafe { CloseHandle(self.mutex) };
            self.mutex = std::ptr::null_mut();
        }
    }
}

/// Formats an 11-byte static ENEK zone feature report.
pub fn build_enek_zone_report(zone_index: usize, r: u8, g: u8, b: u8, brightness: u8) -> [u8; 11] {
    let mut pkt = [0u8; 11];
    pkt[0] = ENEK_REPORT_ID;
    pkt[1] = ENEK_TARGET_KEYBOARD;
    pkt[2] = if brightness == 0 {
        0x02
    } else {
        ENEK_EFFECT_STATIC
    };
    pkt[3] = brightness.min(100);
    pkt[4] = 0;
    pkt[5] = 0;
    pkt[6] = r;
    pkt[7] = g;
    pkt[8] = b;
    pkt[9] = ENEK_ZONE_MASKS[zone_index.min(3)];
    pkt[10] = 0x00;
    pkt
}

pub static GLOBAL_ENEK: std::sync::Mutex<Option<EcHid>> = std::sync::Mutex::new(None);

#[no_mangle]
pub unsafe extern "C" fn predator_enek_open(vid: u16, pid: u16) -> i32 {
    crate::safe_ffi(-1, || {
        let mut guard = GLOBAL_ENEK.lock().unwrap_or_else(|e| e.into_inner());
        match EcHid::open_enek(vid, pid) {
            Some(dev) => {
                *guard = Some(dev);
                0
            }
            None => -1,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_enek_close() {
    crate::safe_ffi((), || {
        let mut guard = GLOBAL_ENEK.lock().unwrap_or_else(|e| e.into_inner());
        guard.take();
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_enek_is_open() -> i32 {
    crate::safe_ffi(0, || {
        let guard = GLOBAL_ENEK.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(dev) = guard.as_ref() {
            if dev.is_open() {
                1
            } else {
                0
            }
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_enek_send_report(report_bytes: *const u8, len: usize) -> i32 {
    crate::safe_ffi(-1, || {
        if report_bytes.is_null() || len == 0 || !crate::is_valid_buffer_range(report_bytes, len) {
            return -1;
        }
        let slice = unsafe { std::slice::from_raw_parts(report_bytes, len) };
        let guard = GLOBAL_ENEK.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(dev) = guard.as_ref() {
            if dev.send_feature_report(slice) {
                0
            } else {
                -2
            }
        } else {
            -3
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_enek_set_zones(
    r1: u8,
    g1: u8,
    b1: u8,
    r2: u8,
    g2: u8,
    b2: u8,
    r3: u8,
    g3: u8,
    b3: u8,
    r4: u8,
    g4: u8,
    b4: u8,
    brightness: u8,
) -> i32 {
    crate::safe_ffi(-1, || {
        let guard = GLOBAL_ENEK.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(dev) = guard.as_ref() {
            if dev.set_enek_zones(r1, g1, b1, r2, g2, b2, r3, g3, b3, r4, g4, b4, brightness) {
                0
            } else {
                -2
            }
        } else {
            -3
        }
    })
}

/// Number of HID device interface paths currently enumerable (diagnostics only).
pub fn enumerate_hid_paths(limit: usize) -> usize {
    with_hid_api(|api| {
        let mut hid_guid = Guid::default();
        unsafe { (api.get_hid_guid)(&mut hid_guid) };
        let info_set = unsafe {
            SetupDiGetClassDevsW(
                &hid_guid,
                std::ptr::null(),
                std::ptr::null_mut(),
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
            )
        };
        if info_set.is_null() || info_set == INVALID_HANDLE_VALUE as *mut c_void {
            return 0;
        }

        let mut count = 0;
        for index in 0..limit.min(MAX_DEVICE_PATHS) as u32 {
            let mut did = SpDeviceInterfaceData::default();
            if unsafe {
                SetupDiEnumDeviceInterfaces(
                    info_set,
                    std::ptr::null_mut(),
                    &hid_guid,
                    index,
                    &mut did,
                )
            } == 0
            {
                break;
            }
            count += 1;
        }
        unsafe { SetupDiDestroyDeviceInfoList(info_set) };
        count
    })
    .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn opening_a_nonexistent_device_returns_none() {
        // No HID device has this VID/PID pair.
        assert!(EcHid::open(0xFFFF, 0xFFFF, 0, 0).is_none());
    }

    #[test]
    fn enumeration_never_panics() {
        let _ = enumerate_hid_paths(64);
    }

    #[test]
    fn test_enek_report_formatting_and_zone_masks() {
        assert_eq!(ENEK_VID, 0x048D);
        assert_eq!(ENEK_USAGE_PAGE, 0xFF00);
        assert_eq!(ENEK_USAGE, 0x0001);

        for zone in 0..4 {
            let pkt = build_enek_zone_report(zone, 10, 20, 30, 80);
            assert_eq!(pkt[0], 0xA4);
            assert_eq!(pkt[1], 0x21);
            assert_eq!(pkt[2], 0x02);
            assert_eq!(pkt[3], 80);
            assert_eq!(pkt[6], 10);
            assert_eq!(pkt[7], 20);
            assert_eq!(pkt[8], 30);
            assert_eq!(pkt[9], ENEK_ZONE_MASKS[zone]);
        }

        // Test brightness clamping
        let pkt = build_enek_zone_report(0, 255, 255, 255, 150);
        assert_eq!(pkt[3], 100);
    }

    #[test]
    fn abi_struct_sizes_are_correct() {
        assert_eq!(std::mem::size_of::<SpDeviceInterfaceDetailHeader>(), 4);
        assert_eq!(std::mem::size_of::<HiddAttributes>(), 12);
        // SP_DEVICE_INTERFACE_DATA: cbSize + GUID + flags + reserved pointer.
        assert_eq!(
            std::mem::size_of::<SpDeviceInterfaceData>(),
            if cfg!(target_pointer_width = "64") {
                32
            } else {
                28
            }
        );
        // HIDP_CAPS: 5 + 17 + 10 USHORTs = 32 * 2 = 64 bytes.
        assert_eq!(std::mem::size_of::<HidpCaps>(), 64);
    }
}
