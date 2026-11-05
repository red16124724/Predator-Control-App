//! Real-time audio spectrum analyzer & transient detector.
//!
//! Replaces the managed WASAPI + managed-FFT path: a lock-free ring buffer is
//! filled by the (C#) WASAPI loopback capture thread, and the analysis is a
//! `realfft`/`rustfft` real-to-complex FFT over a pre-computed Hann window.
//!
//! Everything is allocated up front; `push`/`analyze` never allocate and are
//! protected by internal synchronization for safe multi-threaded access.
//! Band energies are auto-normalized with a decaying per-band peak so the
//! output is directly usable as 0..=1 lighting intensity.

use realfft::RealFftPlanner;
use realfft::RealToComplex;
use rustfft::num_complex::Complex;
use std::sync::{Arc, Mutex};

pub const MIN_FFT_SIZE: usize = 64;
pub const MAX_FFT_SIZE: usize = 16384;
pub const MAX_BANDS: usize = 64;

/// Upper bound on how many samples one `push` may append before the ring wraps.
pub const MAX_PUSH_FRAMES: usize = 1 << 16;

struct SpectrumState {
    ring: Vec<f32>,
    ring_pos: usize,
    /// Number of samples written since the last analysis (saturates at fft_size).
    pending: usize,
    time_buf: Vec<f32>,
    freq_buf: Vec<Complex<f32>>,
    mag: Vec<f32>,
    prev_mag: Vec<f32>,
    bands: Vec<f32>,
    band_peak: Vec<f32>,
    flux: f32,
    flux_avg: f32,
    beat_cooldown: u32,
    beat_strength: f32,
    rms: f32,
    peak_level: f32,
    #[allow(dead_code)]
    analyzed: bool,
}

impl SpectrumState {
    #[inline]
    fn write_sample(&mut self, value: f32) {
        self.ring[self.ring_pos] = if value.is_finite() { value } else { 0.0 };
        self.ring_pos += 1;
        if self.ring_pos >= self.ring.len() {
            self.ring_pos = 0;
        }
    }

    pub fn push_interleaved(&mut self, data: &[f32], channels: usize, fft_size: usize) {
        let channels = channels.max(1);
        let frames = (data.len() / channels).min(MAX_PUSH_FRAMES);
        if frames == 0 {
            return;
        }

        let inv = 1.0 / channels as f32;
        // Only the newest `fft_size` frames can influence the next transform.
        let first_kept = frames.saturating_sub(fft_size);

        let mut sum = 0.0_f32;
        let mut written = 0usize;
        for frame in 0..frames {
            let base = frame * channels;
            let mut acc = 0.0_f32;
            for c in 0..channels {
                let s = data[base + c];
                if s.is_finite() {
                    acc += s;
                }
            }
            let mono = acc * inv;
            sum += mono * mono;
            if frame >= first_kept {
                self.write_sample(mono);
                written += 1;
            }
        }

        self.pending = (self.pending + written).min(fft_size);

        // RMS over the whole chunk (pre-window, matches what the ear hears).
        let chunk_rms = (sum / frames as f32).sqrt();
        self.rms = if self.rms > 0.0 {
            // Smoothing so the level meter does not flicker between packets.
            self.rms * 0.6 + chunk_rms * 0.4
        } else {
            // First packet reports the true level instead of ramping up from zero.
            chunk_rms
        };
        if chunk_rms > self.peak_level {
            self.peak_level = chunk_rms;
        } else {
            self.peak_level *= 0.999;
        }
    }

    pub fn analyze(
        &mut self,
        fft_size: usize,
        band_count: usize,
        window: &[f32],
        band_edges: &[usize],
        plan: &dyn RealToComplex<f32>,
    ) {
        if self.pending == 0 {
            return;
        }
        self.pending = 0;
        self.analyzed = true;

        let n = fft_size;
        // Unroll the ring buffer into chronological order, windowing as we go.
        let mut idx = self.ring_pos;
        for i in 0..n {
            self.time_buf[i] = self.ring[idx] * window[i];
            idx += 1;
            if idx >= n {
                idx = 0;
            }
        }

        if plan
            .process(&mut self.time_buf, &mut self.freq_buf)
            .is_err()
        {
            return;
        }

        // Magnitude spectrum, normalized by the window's coherent gain.
        let norm = 2.0 / n as f32;
        for (i, c) in self.freq_buf.iter().enumerate() {
            self.mag[i] = (c.re * c.re + c.im * c.im).sqrt() * norm;
        }

        // Spectral flux (positive differences only) drives transient detection.
        let mut flux = 0.0_f32;
        for i in 1..self.mag.len() {
            let diff = self.mag[i] - self.prev_mag[i];
            if diff > 0.0 {
                flux += diff;
            }
            self.prev_mag[i] = self.mag[i];
        }
        self.flux = flux;
        self.flux_avg = self.flux_avg * 0.94 + flux * 0.06;

        if self.beat_cooldown > 0 {
            self.beat_cooldown -= 1;
        } else {
            // 45% above the running mean, with a small absolute floor to ignore silence.
            let threshold = self.flux_avg * 1.45 + 1e-4;
            if flux > threshold && flux > 1e-3 {
                self.beat_cooldown = 6; // ~130 ms at 48 kHz / 2048
                self.beat_strength = ((flux - self.flux_avg) / self.flux_avg.max(1e-6)).min(1.0);
            } else {
                self.beat_strength *= 0.7;
            }
        }

        // Band energies, auto-normalized against a decaying peak.
        for b in 0..band_count {
            let start = band_edges[b].min(self.mag.len() - 1);
            let end = band_edges[b + 1].min(self.mag.len());
            let mut sum = 0.0;
            let mut count = 0;
            for bin in start..end.max(start + 1).min(self.mag.len()) {
                sum += self.mag[bin];
                count += 1;
            }
            let energy = if count > 0 { sum / count as f32 } else { 0.0 };
            self.band_peak[b] = (self.band_peak[b] * 0.92).max(energy).max(1e-6);
            self.bands[b] = (energy / self.band_peak[b]).clamp(0.0, 1.0);
        }
    }

    pub fn render_zones(
        &mut self,
        base: [u8; 3],
        brightness: u8,
        out: &mut [u8],
        fft_size: usize,
        band_count: usize,
        window: &[f32],
        band_edges: &[usize],
        plan: &dyn RealToComplex<f32>,
    ) -> usize {
        self.analyze(fft_size, band_count, window, band_edges, plan);
        let zones = out.len() / 3;
        if zones == 0 {
            return 0;
        }
        let scale = (brightness.min(100) as f32) / 100.0;
        let level = self.rms.clamp(0.0, 1.0);
        let beat_boost = if self.beat_strength > 0.0 && self.beat_cooldown >= 5 {
            self.beat_strength.clamp(0.0, 1.0)
        } else {
            0.0
        };

        for z in 0..zones {
            let t0 = z as f32 / zones as f32;
            let mut energy = 0.0;
            let groups = 6.min(band_count.max(1));
            for g in 0..groups {
                let band = ((t0 * band_count as f32) as usize + g).min(band_count - 1);
                energy += self.bands[band];
            }
            energy /= groups as f32;
            let intensity = (energy * 0.75 + level * 0.25 + beat_boost * 0.25).clamp(0.0, 1.0);
            let v = intensity * scale * 255.0;
            out[z * 3] = (base[0] as f32 * v / 255.0).round().clamp(0.0, 255.0) as u8;
            out[z * 3 + 1] = (base[1] as f32 * v / 255.0).round().clamp(0.0, 255.0) as u8;
            out[z * 3 + 2] = (base[2] as f32 * v / 255.0).round().clamp(0.0, 255.0) as u8;
        }
        zones
    }

    pub fn reset(&mut self) {
        self.ring.iter_mut().for_each(|v| *v = 0.0);
        self.ring_pos = 0;
        self.pending = 0;
        self.mag.iter_mut().for_each(|v| *v = 0.0);
        self.prev_mag.iter_mut().for_each(|v| *v = 0.0);
        self.bands.iter_mut().for_each(|v| *v = 0.0);
        self.band_peak.iter_mut().for_each(|v| *v = 1e-6);
        self.flux = 0.0;
        self.flux_avg = 0.0;
        self.beat_cooldown = 0;
        self.beat_strength = 0.0;
        self.rms = 0.0;
        self.peak_level = 0.0;
        self.analyzed = false;
    }
}

pub struct SpectrumAnalyzer {
    pub fft_size: usize,
    pub sample_rate: u32,
    pub band_count: usize,
    pub window: Vec<f32>,
    pub band_edges: Vec<usize>,
    plan: Arc<dyn RealToComplex<f32>>,
    state: Mutex<SpectrumState>,
}

#[inline]
fn hann(n: usize) -> Vec<f32> {
    if n < 2 {
        return vec![1.0; n];
    }
    let denom = (n - 1) as f32;
    (0..n)
        .map(|i| {
            let x = std::f32::consts::TAU * i as f32 / denom;
            0.5 - 0.5 * x.cos()
        })
        .collect()
}

impl SpectrumAnalyzer {
    pub fn new(sample_rate: u32, fft_size: usize, band_count: usize) -> Option<Self> {
        let fft_size = if fft_size.is_power_of_two() {
            fft_size.clamp(MIN_FFT_SIZE, MAX_FFT_SIZE)
        } else {
            fft_size
                .clamp(MIN_FFT_SIZE, MAX_FFT_SIZE)
                .next_power_of_two()
        };
        let sample_rate = if sample_rate == 0 {
            48_000
        } else {
            sample_rate
        };
        let band_count = band_count.clamp(1, MAX_BANDS);

        let mut planner = RealFftPlanner::<f32>::new();
        let plan = planner.plan_fft_forward(fft_size);

        let bins = fft_size / 2 + 1;
        let band_edges = Self::band_edges(sample_rate, fft_size, band_count);

        let state = SpectrumState {
            ring: vec![0.0; fft_size],
            ring_pos: 0,
            pending: 0,
            time_buf: vec![0.0; fft_size],
            freq_buf: vec![Complex::new(0.0, 0.0); bins],
            mag: vec![0.0; bins],
            prev_mag: vec![0.0; bins],
            bands: vec![0.0; band_count],
            band_peak: vec![1e-6; band_count],
            flux: 0.0,
            flux_avg: 0.0,
            beat_cooldown: 0,
            beat_strength: 0.0,
            rms: 0.0,
            peak_level: 0.0,
            analyzed: false,
        };

        Some(Self {
            fft_size,
            sample_rate,
            band_count,
            window: hann(fft_size),
            band_edges,
            plan,
            state: Mutex::new(state),
        })
    }

    /// Log-spaced band boundaries in Hz, expressed as bin indices.
    fn band_edges(sample_rate: u32, fft_size: usize, band_count: usize) -> Vec<usize> {
        let bins = fft_size / 2 + 1;
        let nyquist = sample_rate as f32 / 2.0;
        let lo = 32.0_f32.min(nyquist * 0.01).max(1.0);
        let hi = 16_000.0_f32.min(nyquist * 0.95).max(lo * 2.0);
        let bin_hz = sample_rate as f32 / fft_size as f32;

        let max_bin = bins.saturating_sub(1);
        let mut edges = Vec::with_capacity(band_count + 1);
        for i in 0..=band_count {
            let t = i as f32 / band_count as f32;
            let hz = lo * (hi / lo).powf(t);
            let bin = ((hz / bin_hz).round() as usize).clamp(1, max_bin);
            edges.push(bin);
        }
        for i in 1..edges.len() {
            if edges[i] <= edges[i - 1] {
                edges[i] = (edges[i - 1] + 1).min(max_bin);
            }
        }
        for i in (0..edges.len().saturating_sub(1)).rev() {
            if edges[i] > edges[i + 1] {
                edges[i] = edges[i + 1];
            }
        }
        edges
    }

    pub fn fft_size(&self) -> usize {
        self.fft_size
    }

    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    pub fn band_count(&self) -> usize {
        self.band_count
    }

    /// Append mono samples. Overwrites the oldest data once the ring is full.
    pub fn push(&self, mono: &[f32]) {
        self.push_interleaved(mono, 1);
    }

    /// Append interleaved samples, averaging channels down to mono.
    pub fn push_interleaved(&self, interleaved: &[f32], channels: usize) {
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        state.push_interleaved(interleaved, channels, self.fft_size);
    }

    /// Run the FFT and update bands / transient state. No-op when no new samples arrived.
    pub fn analyze(&self) {
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        state.analyze(
            self.fft_size,
            self.band_count,
            &self.window,
            &self.band_edges,
            self.plan.as_ref(),
        );
    }

    pub fn bands(&mut self) -> &[f32] {
        let state = self.state.get_mut().unwrap_or_else(|e| e.into_inner());
        state.analyze(
            self.fft_size,
            self.band_count,
            &self.window,
            &self.band_edges,
            self.plan.as_ref(),
        );
        &state.bands
    }

    /// Safe unaligned copy of current bands into external buffer, running analyze if needed.
    pub unsafe fn copy_bands_unaligned(&self, out: *mut f32, cap: usize) -> usize {
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        state.analyze(
            self.fft_size,
            self.band_count,
            &self.window,
            &self.band_edges,
            self.plan.as_ref(),
        );
        let count = cap.min(state.bands.len());
        for i in 0..count {
            std::ptr::write_unaligned(out.add(i), state.bands[i]);
        }
        count
    }

    pub fn level(&self) -> f32 {
        let state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        state.rms.clamp(0.0, 4.0)
    }

    pub fn peak(&self) -> f32 {
        let state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        state.peak_level.clamp(0.0, 4.0)
    }

    pub fn is_beat(&self) -> bool {
        let state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        state.beat_strength > 0.0 && state.beat_cooldown >= 5
    }

    pub fn beat_strength(&self) -> f32 {
        let state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        if state.beat_strength > 0.0 && state.beat_cooldown >= 5 {
            state.beat_strength.clamp(0.0, 1.0)
        } else {
            0.0
        }
    }

    pub fn reset(&self) {
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        state.reset();
    }

    /// Maps spectrum energy onto keyboard zones: bass drives zone 0, treble the last zone.
    /// Writes `zones` RGB triples into `out` (`zones * 3` bytes).
    pub fn render_zones(&self, base: [u8; 3], brightness: u8, out: &mut [u8]) -> usize {
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        state.render_zones(
            base,
            brightness,
            out,
            self.fft_size,
            self.band_count,
            &self.window,
            &self.band_edges,
            self.plan.as_ref(),
        )
    }

    #[cfg(test)]
    pub fn mag(&self) -> Vec<f32> {
        self.state.lock().unwrap().mag.clone()
    }
}

// ============================================================================
// WASAPI Audio Loopback Capture Worker
// ============================================================================

use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::JoinHandle;
use std::time::Duration;

#[cfg(windows)]
mod wasapi_ffi {
    use std::ffi::c_void;

    #[repr(C)]
    #[derive(Clone, Copy, Default)]
    pub struct Guid {
        pub data1: u32,
        pub data2: u16,
        pub data3: u16,
        pub data4: [u8; 8],
    }

    pub const CLSID_MMDEVICE_ENUMERATOR: Guid = Guid {
        data1: 0xBCDE0395,
        data2: 0xE52F,
        data3: 0x467C,
        data4: [0x8E, 0x3D, 0xC4, 0x57, 0x92, 0x91, 0x49, 0x2E],
    };

    pub const IID_IMMDEVICE_ENUMERATOR: Guid = Guid {
        data1: 0xA95664D2,
        data2: 0x9614,
        data3: 0x4F35,
        data4: [0xA7, 0x46, 0xDE, 0x8D, 0xB6, 0x36, 0x17, 0xE6],
    };

    pub const IID_IAUDIOCLIENT: Guid = Guid {
        data1: 0x1CB9AD4C,
        data2: 0xDBFA,
        data3: 0x4C32,
        data4: [0xB1, 0x78, 0xC2, 0xF5, 0x68, 0xA7, 0x03, 0xB2],
    };

    pub const IID_IAUDIOCAPTURECLIENT: Guid = Guid {
        data1: 0xC8ADBD64,
        data2: 0xE71E,
        data3: 0x48A0,
        data4: [0xA4, 0xDE, 0x18, 0x5C, 0x39, 0x5C, 0xD3, 0x17],
    };

    pub const COINIT_MULTITHREADED: u32 = 0x0;
    pub const CLSCTX_ALL: u32 = 23;
    pub const E_RENDER: i32 = 0;
    pub const E_CONSOLE: i32 = 0;
    pub const AUDCLNT_SHAREMODE_SHARED: i32 = 0;
    pub const AUDCLNT_STREAMFLAGS_LOOPBACK: u32 = 0x0002_0000;
    pub const AUDCLNT_BUFFERFLAGS_SILENT: u32 = 0x2;

    #[repr(C, packed(1))]
    #[derive(Copy, Clone, Default)]
    pub struct WaveFormatEx {
        pub format_tag: u16,
        pub channels: u16,
        pub samples_per_sec: u32,
        pub avg_bytes_per_sec: u32,
        pub block_align: u16,
        pub bits_per_sample: u16,
        pub extra_size: u16,
    }

    #[repr(C)]
    pub struct IUnknownVtbl {
        pub query_interface: unsafe extern "system" fn(*mut c_void, *const Guid, *mut *mut c_void) -> i32,
        pub add_ref: unsafe extern "system" fn(*mut c_void) -> u32,
        pub release: unsafe extern "system" fn(*mut c_void) -> u32,
    }

    #[repr(C)]
    pub struct IMMDeviceEnumeratorVtbl {
        pub parent: IUnknownVtbl,
        pub enum_audio_endpoints: unsafe extern "system" fn(*mut c_void, i32, u32, *mut *mut c_void) -> i32,
        pub get_default_audio_endpoint: unsafe extern "system" fn(*mut c_void, i32, i32, *mut *mut c_void) -> i32,
        pub get_device: unsafe extern "system" fn(*mut c_void, *const u16, *mut *mut c_void) -> i32,
        pub register_endpoint_notification_callback: unsafe extern "system" fn(*mut c_void, *mut c_void) -> i32,
        pub unregister_endpoint_notification_callback: unsafe extern "system" fn(*mut c_void, *mut c_void) -> i32,
    }

    #[repr(C)]
    pub struct IMMDeviceVtbl {
        pub parent: IUnknownVtbl,
        pub activate: unsafe extern "system" fn(*mut c_void, *const Guid, u32, *mut c_void, *mut *mut c_void) -> i32,
        pub open_property_store: unsafe extern "system" fn(*mut c_void, u32, *mut *mut c_void) -> i32,
        pub get_id: unsafe extern "system" fn(*mut c_void, *mut *mut u16) -> i32,
        pub get_state: unsafe extern "system" fn(*mut c_void, *mut u32) -> i32,
    }

    #[repr(C)]
    pub struct IAudioClientVtbl {
        pub parent: IUnknownVtbl,
        pub initialize: unsafe extern "system" fn(*mut c_void, i32, u32, i64, i64, *const c_void, *const c_void) -> i32,
        pub get_buffer_size: unsafe extern "system" fn(*mut c_void, *mut u32) -> i32,
        pub get_stream_latency: unsafe extern "system" fn(*mut c_void, *mut i64) -> i32,
        pub get_current_padding: unsafe extern "system" fn(*mut c_void, *mut u32) -> i32,
        pub is_format_supported: unsafe extern "system" fn(*mut c_void, i32, *const c_void, *mut *mut c_void) -> i32,
        pub get_mix_format: unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> i32,
        pub get_device_period: unsafe extern "system" fn(*mut c_void, *mut i64, *mut i64) -> i32,
        pub start: unsafe extern "system" fn(*mut c_void) -> i32,
        pub stop: unsafe extern "system" fn(*mut c_void) -> i32,
        pub reset: unsafe extern "system" fn(*mut c_void) -> i32,
        pub set_event_handle: unsafe extern "system" fn(*mut c_void, *mut c_void) -> i32,
        pub get_service: unsafe extern "system" fn(*mut c_void, *const Guid, *mut *mut c_void) -> i32,
    }

    #[repr(C)]
    pub struct IAudioCaptureClientVtbl {
        pub parent: IUnknownVtbl,
        pub get_buffer: unsafe extern "system" fn(*mut c_void, *mut *mut u8, *mut u32, *mut u32, *mut u64, *mut u64) -> i32,
        pub release_buffer: unsafe extern "system" fn(*mut c_void, u32) -> i32,
        pub get_next_packet_size: unsafe extern "system" fn(*mut c_void, *mut u32) -> i32,
    }

    #[link(name = "ole32")]
    extern "system" {
        pub fn CoInitializeEx(pv_reserved: *mut c_void, dw_co_init: u32) -> i32;
        pub fn CoUninitialize();
        pub fn CoCreateInstance(
            rclsid: *const Guid,
            p_unk_outer: *mut c_void,
            dw_cls_context: u32,
            riid: *const Guid,
            ppv: *mut *mut c_void,
        ) -> i32;
        pub fn CoTaskMemFree(pv: *mut c_void);
    }
}

pub struct AudioCaptureWorker {
    analyzer: Arc<SpectrumAnalyzer>,
    is_running: Arc<AtomicBool>,
    is_active: Arc<AtomicBool>,
    thread_handle: Option<JoinHandle<()>>,
}

impl AudioCaptureWorker {
    pub fn new(sample_rate: u32, fft_size: usize, band_count: usize) -> Option<Self> {
        let analyzer = Arc::new(SpectrumAnalyzer::new(sample_rate, fft_size, band_count)?);
        Some(Self {
            analyzer,
            is_running: Arc::new(AtomicBool::new(false)),
            is_active: Arc::new(AtomicBool::new(false)),
            thread_handle: None,
        })
    }

    pub fn start(&mut self) -> bool {
        if self.is_running.swap(true, Ordering::SeqCst) {
            return true;
        }

        let is_running = self.is_running.clone();
        let is_active = self.is_active.clone();
        let analyzer = self.analyzer.clone();

        self.thread_handle = Some(std::thread::spawn(move || {
            #[cfg(windows)]
            run_wasapi_capture_loop(is_running, is_active, analyzer);

            #[cfg(not(windows))]
            {
                is_active.store(true, Ordering::Release);
                while is_running.load(Ordering::Acquire) {
                    std::thread::sleep(Duration::from_millis(50));
                }
                is_active.store(false, Ordering::Release);
            }
        }));

        true
    }

    pub fn stop(&mut self) {
        self.is_running.store(false, Ordering::SeqCst);
        self.is_active.store(false, Ordering::Release);
        if let Some(handle) = self.thread_handle.take() {
            let _ = handle.join();
        }
    }

    pub fn is_active(&self) -> bool {
        self.is_active.load(Ordering::Acquire)
    }

    pub fn analyzer(&self) -> &Arc<SpectrumAnalyzer> {
        &self.analyzer
    }

    pub fn get_level(&self) -> f32 {
        self.analyzer.level()
    }

    pub fn get_zones(&self, base: [u8; 3], brightness: u8, out_zones: &mut [u8]) -> usize {
        self.analyzer.render_zones(base, brightness, out_zones)
    }

    pub unsafe fn copy_bands(&self, out: *mut f32, cap: usize) -> usize {
        self.analyzer.copy_bands_unaligned(out, cap)
    }
}

impl Drop for AudioCaptureWorker {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(windows)]
fn run_wasapi_capture_loop(
    is_running: Arc<AtomicBool>,
    is_active: Arc<AtomicBool>,
    analyzer: Arc<SpectrumAnalyzer>,
) {
    use wasapi_ffi::*;
    unsafe {
        let _ = CoInitializeEx(std::ptr::null_mut(), COINIT_MULTITHREADED);
    }

    while is_running.load(Ordering::Acquire) {
        let mut enumerator: *mut std::ffi::c_void = std::ptr::null_mut();
        let hr = unsafe {
            CoCreateInstance(
                &CLSID_MMDEVICE_ENUMERATOR,
                std::ptr::null_mut(),
                CLSCTX_ALL,
                &IID_IMMDEVICE_ENUMERATOR,
                &mut enumerator,
            )
        };

        if hr != 0 || enumerator.is_null() {
            std::thread::sleep(Duration::from_millis(500));
            continue;
        }

        let enum_vtbl = unsafe { &*(*(enumerator as *mut *mut IMMDeviceEnumeratorVtbl)) };
        let mut device: *mut std::ffi::c_void = std::ptr::null_mut();
        let hr = unsafe {
            (enum_vtbl.get_default_audio_endpoint)(enumerator, E_RENDER, E_CONSOLE, &mut device)
        };

        if hr != 0 || device.is_null() {
            unsafe { ((&*(*(enumerator as *mut *mut IUnknownVtbl))).release)(enumerator) };
            std::thread::sleep(Duration::from_millis(500));
            continue;
        }

        let dev_vtbl = unsafe { &*(*(device as *mut *mut IMMDeviceVtbl)) };
        let mut audio_client: *mut std::ffi::c_void = std::ptr::null_mut();
        let hr = unsafe {
            (dev_vtbl.activate)(device, &IID_IAUDIOCLIENT, CLSCTX_ALL, std::ptr::null_mut(), &mut audio_client)
        };

        if hr != 0 || audio_client.is_null() {
            unsafe {
                ((&*(*(device as *mut *mut IUnknownVtbl))).release)(device);
                ((&*(*(enumerator as *mut *mut IUnknownVtbl))).release)(enumerator);
            }
            std::thread::sleep(Duration::from_millis(500));
            continue;
        }

        let client_vtbl = unsafe { &*(*(audio_client as *mut *mut IAudioClientVtbl)) };
        let mut p_format: *mut std::ffi::c_void = std::ptr::null_mut();
        let hr = unsafe { (client_vtbl.get_mix_format)(audio_client, &mut p_format) };

        if hr != 0 || p_format.is_null() {
            unsafe {
                ((&*(*(audio_client as *mut *mut IUnknownVtbl))).release)(audio_client);
                ((&*(*(device as *mut *mut IUnknownVtbl))).release)(device);
                ((&*(*(enumerator as *mut *mut IUnknownVtbl))).release)(enumerator);
            }
            std::thread::sleep(Duration::from_millis(500));
            continue;
        }

        let wave_format = unsafe { *(p_format as *const WaveFormatEx) };
        let channels = (wave_format.channels as usize).max(1);

        let buffer_duration_hns: i64 = 1_000_000; // 100 ms
        let hr = unsafe {
            (client_vtbl.initialize)(
                audio_client,
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK,
                buffer_duration_hns,
                0,
                p_format,
                std::ptr::null(),
            )
        };

        unsafe { CoTaskMemFree(p_format) };

        if hr != 0 {
            unsafe {
                ((&*(*(audio_client as *mut *mut IUnknownVtbl))).release)(audio_client);
                ((&*(*(device as *mut *mut IUnknownVtbl))).release)(device);
                ((&*(*(enumerator as *mut *mut IUnknownVtbl))).release)(enumerator);
            }
            std::thread::sleep(Duration::from_millis(500));
            continue;
        }

        let mut capture_client: *mut std::ffi::c_void = std::ptr::null_mut();
        let hr = unsafe {
            (client_vtbl.get_service)(audio_client, &IID_IAUDIOCAPTURECLIENT, &mut capture_client)
        };

        if hr != 0 || capture_client.is_null() {
            unsafe {
                ((&*(*(audio_client as *mut *mut IUnknownVtbl))).release)(audio_client);
                ((&*(*(device as *mut *mut IUnknownVtbl))).release)(device);
                ((&*(*(enumerator as *mut *mut IUnknownVtbl))).release)(enumerator);
            }
            std::thread::sleep(Duration::from_millis(500));
            continue;
        }

        let capture_vtbl = unsafe { &*(*(capture_client as *mut *mut IAudioCaptureClientVtbl)) };
        let _ = unsafe { (client_vtbl.start)(audio_client) };

        is_active.store(true, Ordering::Release);

        while is_running.load(Ordering::Acquire) {
            let mut packet_size: u32 = 0;
            let hr = unsafe { (capture_vtbl.get_next_packet_size)(capture_client, &mut packet_size) };
            if hr != 0 {
                break;
            }

            if packet_size == 0 {
                std::thread::sleep(Duration::from_millis(10));
                continue;
            }

            let mut p_data: *mut u8 = std::ptr::null_mut();
            let mut num_frames: u32 = 0;
            let mut flags: u32 = 0;
            let mut dev_pos: u64 = 0;
            let mut qpc_pos: u64 = 0;

            let hr = unsafe {
                (capture_vtbl.get_buffer)(
                    capture_client,
                    &mut p_data,
                    &mut num_frames,
                    &mut flags,
                    &mut dev_pos,
                    &mut qpc_pos,
                )
            };

            if hr == 0 && !p_data.is_null() && num_frames > 0 {
                let total_samples = (num_frames as usize) * channels;
                if (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 {
                    let silence = vec![0.0f32; total_samples];
                    analyzer.push_interleaved(&silence, channels);
                } else {
                    let float_ptr = p_data as *const f32;
                    let slice = unsafe { std::slice::from_raw_parts(float_ptr, total_samples) };
                    analyzer.push_interleaved(slice, channels);
                }
                unsafe { (capture_vtbl.release_buffer)(capture_client, num_frames) };
            }

            std::thread::sleep(Duration::from_millis(10));
        }

        is_active.store(false, Ordering::Release);
        unsafe {
            let _ = (client_vtbl.stop)(audio_client);
            ((&*(*(capture_client as *mut *mut IUnknownVtbl))).release)(capture_client);
            ((&*(*(audio_client as *mut *mut IUnknownVtbl))).release)(audio_client);
            ((&*(*(device as *mut *mut IUnknownVtbl))).release)(device);
            ((&*(*(enumerator as *mut *mut IUnknownVtbl))).release)(enumerator);
        }

        if is_running.load(Ordering::Acquire) {
            std::thread::sleep(Duration::from_millis(500));
        }
    }

    unsafe {
        CoUninitialize();
    }
}

pub static GLOBAL_AUDIO_CAPTURE: std::sync::Mutex<Option<AudioCaptureWorker>> =
    std::sync::Mutex::new(None);

#[no_mangle]
pub unsafe extern "C" fn predator_audio_capture_start(
    sample_rate: u32,
    fft_size: usize,
    band_count: usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        let mut guard = GLOBAL_AUDIO_CAPTURE.lock().unwrap_or_else(|e| e.into_inner());
        let sr = if sample_rate > 0 { sample_rate } else { 48_000 };
        let fft = if fft_size > 0 { fft_size } else { 2048 };
        let bands = if band_count > 0 { band_count } else { 8 };

        match AudioCaptureWorker::new(sr, fft, bands) {
            Some(mut worker) => {
                worker.start();
                *guard = Some(worker);
                0
            }
            None => -1,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_capture_stop() {
    crate::safe_ffi((), || {
        let mut guard = GLOBAL_AUDIO_CAPTURE.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(mut worker) = guard.take() {
            worker.stop();
        }
    });
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_capture_is_active() -> i32 {
    crate::safe_ffi(0, || {
        let guard = GLOBAL_AUDIO_CAPTURE.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(worker) = guard.as_ref() {
            if worker.is_active() { 1 } else { 0 }
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_capture_get_zones(
    r: u8,
    g: u8,
    b: u8,
    brightness: u8,
    out_zones: *mut u8,
    zone_count: usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        if out_zones.is_null()
            || zone_count == 0
            || !crate::is_valid_mut_buffer_range(out_zones, zone_count * 3)
        {
            return -1;
        }

        let guard = GLOBAL_AUDIO_CAPTURE.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(worker) = guard.as_ref() {
            let slice = unsafe { std::slice::from_raw_parts_mut(out_zones, zone_count * 3) };
            worker.get_zones([r, g, b], brightness, slice) as i32
        } else {
            -2
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_capture_get_level() -> f32 {
    crate::safe_ffi(0.0, || {
        let guard = GLOBAL_AUDIO_CAPTURE.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(worker) = guard.as_ref() {
            worker.get_level()
        } else {
            0.0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn predator_audio_capture_get_bands(
    out_bands: *mut f32,
    band_count: usize,
) -> i32 {
    crate::safe_ffi(-1, || {
        if out_bands.is_null()
            || band_count == 0
            || !crate::is_valid_mut_buffer_range(out_bands, band_count)
        {
            return -1;
        }

        let guard = GLOBAL_AUDIO_CAPTURE.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(worker) = guard.as_ref() {
            worker.copy_bands(out_bands, band_count) as i32
        } else {
            -2
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tone(freq: f32, sample_rate: f32, n: usize, amp: f32) -> Vec<f32> {
        (0..n)
            .map(|i| {
                let t = i as f32 / sample_rate;
                amp * (std::f32::consts::TAU * freq * t).sin()
            })
            .collect()
    }

    #[test]
    fn test_audio_capture_worker_lifecycle() {
        let mut worker = AudioCaptureWorker::new(48_000, 1024, 8).expect("worker");
        assert!(!worker.is_active());
        assert_eq!(worker.get_level(), 0.0);

        worker.analyzer().push(&tone(440.0, 48_000.0, 2048, 0.8));
        assert!(worker.get_level() > 0.0);

        let mut zones = [0u8; 12];
        let count = worker.get_zones([255, 0, 0], 100, &mut zones);
        assert_eq!(count, 4);

        assert!(worker.start());
        // Worker started without panicking
        worker.stop();
        assert!(!worker.is_active());
    }

    #[test]
    fn silence_produces_zero_bands() {
        let mut a = SpectrumAnalyzer::new(48_000, 1024, 16).unwrap();
        a.push(&vec![0.0_f32; 2048]);
        let bands = a.bands().to_vec();
        assert!(bands.iter().all(|&b| b == 0.0));
        assert!(a.level() < 1e-6);
    }

    #[test]
    fn sine_energy_lands_in_the_expected_band() {
        let mut a = SpectrumAnalyzer::new(48_000, 4096, 32).unwrap();
        let samples = tone(1000.0, 48_000.0, 8192, 0.5);
        a.push(&samples);
        let bands = a.bands().to_vec();

        let bin_hz: f32 = 48_000.0 / 4096.0;
        let expected_bin = (1000.0 / bin_hz).round() as usize;

        let mut best = usize::MAX;
        for (i, b) in a.band_edges.windows(2).enumerate() {
            if expected_bin >= b[0] && expected_bin < b[1] {
                best = i;
            }
        }
        assert_ne!(best, usize::MAX, "1 kHz bin must fall inside a band");
        assert_eq!(bands[best], 1.0, "band {} must reach full energy", best);

        let mag = a.mag();
        let peak_bin = (1..mag.len())
            .max_by(|x, y| mag[*x].partial_cmp(&mag[*y]).unwrap())
            .unwrap();
        assert!(
            peak_bin >= a.band_edges[best] && peak_bin < a.band_edges[best + 1],
            "spectral peak at bin {} ({} Hz) should sit in band {} [{}, {})",
            peak_bin,
            peak_bin as f32 * bin_hz,
            best,
            a.band_edges[best],
            a.band_edges[best + 1]
        );
    }

    #[test]
    fn levels_track_amplitude() {
        let quiet = SpectrumAnalyzer::new(48_000, 1024, 8).unwrap();
        let loud = SpectrumAnalyzer::new(48_000, 1024, 8).unwrap();
        quiet.push(&tone(440.0, 48_000.0, 4096, 0.05));
        loud.push(&tone(440.0, 48_000.0, 4096, 0.9));
        assert!(loud.level() > quiet.level() * 4.0);
    }

    #[test]
    fn transient_detection_fires_on_onsets() {
        let a = SpectrumAnalyzer::new(48_000, 1024, 16).unwrap();
        for _ in 0..8 {
            a.push(&vec![0.0_f32; 1024]);
            a.analyze();
        }
        let mut detected = false;
        for _ in 0..4 {
            a.push(&tone(2000.0, 48_000.0, 1024, 0.8));
            a.analyze();
            if a.is_beat() {
                detected = true;
                break;
            }
        }
        assert!(detected, "onset should have been detected as a transient");
    }

    #[test]
    fn interleaved_stereo_is_mono_mixed() {
        let a = SpectrumAnalyzer::new(48_000, 1024, 8).unwrap();
        let mono = tone(600.0, 48_000.0, 2048, 0.5);
        let mut interleaved = Vec::with_capacity(mono.len() * 2);
        for s in &mono {
            interleaved.push(*s);
            interleaved.push(*s);
        }
        a.push_interleaved(&interleaved, 2);
        let stereo_level = a.level();
        assert!(
            stereo_level > 0.1 && stereo_level < 0.6,
            "level {}",
            stereo_level
        );
    }

    #[test]
    fn render_zones_produces_non_black_output_for_audible_input() {
        let a = SpectrumAnalyzer::new(48_000, 1024, 16).unwrap();
        a.push(&tone(200.0, 48_000.0, 4096, 0.9));
        let mut out = [0u8; 4 * 3];
        assert_eq!(a.render_zones([255, 40, 0], 100, &mut out), 4);
        assert!(out.iter().any(|&v| v > 0));
        assert_eq!(out[2], 0);
    }

    #[test]
    fn nan_and_inf_inputs_are_neutralised() {
        let mut a = SpectrumAnalyzer::new(48_000, 512, 8).unwrap();
        let mut v = tone(1000.0, 48_000.0, 1024, 0.5);
        v[10] = f32::NAN;
        v[20] = f32::INFINITY;
        a.push(&v);
        let bands = a.bands().to_vec();
        assert!(bands.iter().all(|b| b.is_finite()));
        assert!(a.level().is_finite());
    }

    #[test]
    fn non_power_of_two_fft_size_is_rounded_up() {
        let a = SpectrumAnalyzer::new(48_000, 1000, 8).unwrap();
        assert_eq!(a.fft_size(), 1024);
    }

    #[test]
    fn reset_clears_state() {
        let mut a = SpectrumAnalyzer::new(48_000, 512, 8).unwrap();
        a.push(&tone(440.0, 48_000.0, 2048, 0.9));
        a.analyze();
        assert!(a.level() > 0.0);
        a.reset();
        assert_eq!(a.level(), 0.0);
        assert!(a.bands().iter().all(|&b| b == 0.0));
    }
}
