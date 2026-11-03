//! Real-time audio spectrum analyzer & transient detector.
//!
//! Replaces the managed WASAPI + managed-FFT path: a lock-free ring buffer is
//! filled by the (C#) WASAPI loopback capture thread, and the analysis is a
//! `realfft`/`rustfft` real-to-complex FFT over a pre-computed Hann window.
//!
//! Everything is allocated up front; `push`/`analyze` never allocate and never
//! lock. Band energies are auto-normalized with a decaying per-band peak so the
//! output is directly usable as 0..=1 lighting intensity.

use realfft::RealFftPlanner;
use realfft::RealToComplex;
use rustfft::num_complex::Complex;
use std::sync::Arc;

pub const MIN_FFT_SIZE: usize = 64;
pub const MAX_FFT_SIZE: usize = 16384;
pub const MAX_BANDS: usize = 64;

/// Upper bound on how many samples one `push` may append before the ring wraps.
pub const MAX_PUSH_FRAMES: usize = 1 << 16;

pub struct SpectrumAnalyzer {
    fft_size: usize,
    sample_rate: u32,
    band_count: usize,
    window: Vec<f32>,
    ring: Vec<f32>,
    ring_pos: usize,
    /// Number of samples written since the last analysis (saturates at fft_size).
    pending: usize,
    plan: Arc<dyn RealToComplex<f32>>,
    time_buf: Vec<f32>,
    freq_buf: Vec<Complex<f32>>,
    mag: Vec<f32>,
    prev_mag: Vec<f32>,
    bands: Vec<f32>,
    band_peak: Vec<f32>,
    band_edges: Vec<usize>,
    flux: f32,
    flux_avg: f32,
    beat_cooldown: u32,
    beat_strength: f32,
    rms: f32,
    peak_level: f32,
    analyzed: bool,
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
            fft_size.clamp(MIN_FFT_SIZE, MAX_FFT_SIZE).next_power_of_two()
        };
        let sample_rate = if sample_rate == 0 { 48_000 } else { sample_rate };
        let band_count = band_count.clamp(1, MAX_BANDS);

        let mut planner = RealFftPlanner::<f32>::new();
        let plan = planner.plan_fft_forward(fft_size);

        let bins = fft_size / 2 + 1;
        let band_edges = Self::band_edges(sample_rate, fft_size, band_count);

        Some(Self {
            fft_size,
            sample_rate,
            band_count,
            window: hann(fft_size),
            ring: vec![0.0; fft_size],
            ring_pos: 0,
            pending: 0,
            plan,
            time_buf: vec![0.0; fft_size],
            freq_buf: vec![Complex::new(0.0, 0.0); bins],
            mag: vec![0.0; bins],
            prev_mag: vec![0.0; bins],
            bands: vec![0.0; band_count],
            band_peak: vec![1e-6; band_count],
            band_edges,
            flux: 0.0,
            flux_avg: 0.0,
            beat_cooldown: 0,
            beat_strength: 0.0,
            rms: 0.0,
            peak_level: 0.0,
            analyzed: false,
        })
    }

    /// Log-spaced band boundaries in Hz, expressed as bin indices.
    fn band_edges(sample_rate: u32, fft_size: usize, band_count: usize) -> Vec<usize> {
        let bins = fft_size / 2 + 1;
        let nyquist = sample_rate as f32 / 2.0;
        // Musical range: 32 Hz .. 16 kHz, clamped to the available Nyquist.
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
        // Guarantee monotonic, non-degenerate bands where possible within max_bin.
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

    #[inline]
    fn write_sample(&mut self, value: f32) {
        self.ring[self.ring_pos] = if value.is_finite() { value } else { 0.0 };
        self.ring_pos += 1;
        if self.ring_pos == self.ring.len() {
            self.ring_pos = 0;
        }
    }

    /// Append mono samples. Overwrites the oldest data once the ring is full.
    pub fn push(&mut self, mono: &[f32]) {
        self.push_interleaved(mono, 1);
    }

    /// Append interleaved samples, averaging channels down to mono.
    ///
    /// Mixing writes straight into the ring buffer: this path performs no
    /// allocation at all, which matters because the capture thread calls it every
    /// few milliseconds.
    pub fn push_interleaved(&mut self, data: &[f32], channels: usize) {
        let channels = channels.max(1);
        let frames = (data.len() / channels).min(MAX_PUSH_FRAMES);
        if frames == 0 {
            return;
        }

        let inv = 1.0 / channels as f32;
        // Only the newest `fft_size` frames can influence the next transform.
        let first_kept = frames.saturating_sub(self.fft_size);

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

        self.pending = (self.pending + written).min(self.fft_size);

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

    /// Run the FFT and update bands / transient state. No-op when no new samples arrived.
    pub fn analyze(&mut self) {
        if self.pending == 0 {
            return;
        }
        self.pending = 0;
        self.analyzed = true;

        let n = self.fft_size;
        // Unroll the ring buffer into chronological order, windowing as we go.
        let mut idx = self.ring_pos;
        for i in 0..n {
            self.time_buf[i] = self.ring[idx] * self.window[i];
            idx += 1;
            if idx == n {
                idx = 0;
            }
        }

        if self.plan.process(&mut self.time_buf, &mut self.freq_buf).is_err() {
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
        for b in 0..self.band_count {
            let start = self.band_edges[b].min(self.mag.len() - 1);
            let end = self.band_edges[b + 1].min(self.mag.len());
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

    pub fn bands(&mut self) -> &[f32] {
        self.analyze();
        &self.bands
    }

    pub fn level(&self) -> f32 {
        self.rms.clamp(0.0, 4.0)
    }

    pub fn peak(&self) -> f32 {
        self.peak_level.clamp(0.0, 4.0)
    }

    pub fn is_beat(&self) -> bool {
        self.beat_strength > 0.0 && self.beat_cooldown >= 5
    }

    pub fn beat_strength(&self) -> f32 {
        if self.is_beat() {
            self.beat_strength.clamp(0.0, 1.0)
        } else {
            0.0
        }
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

    /// Maps spectrum energy onto keyboard zones: bass drives zone 0, treble the last zone.
    /// Writes `zones` RGB triples into `out` (`zones * 3` bytes).
    pub fn render_zones(&mut self, base: [u8; 3], brightness: u8, out: &mut [u8]) -> usize {
        self.analyze();
        let zones = out.len() / 3;
        if zones == 0 {
            return 0;
        }
        let scale = (brightness.min(100) as f32) / 100.0;
        let level = self.rms.clamp(0.0, 1.0);
        // Blend the reactive spectrum with the steady-state level so quiet
        // passages still glow instead of going fully dark.
        let beat_boost = self.beat_strength();

        for z in 0..zones {
            let t0 = z as f32 / zones as f32;
            let mut energy = 0.0;
            let groups = 6.min(self.band_count.max(1));
            for g in 0..groups {
                let band = ((t0 * self.band_count as f32) as usize + g).min(self.band_count - 1);
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
        // 1000 Hz tone, filled well past the FFT window.
        let samples = tone(1000.0, 48_000.0, 8192, 0.5);
        a.push(&samples);
        let bands = a.bands().to_vec();

        let bin_hz: f32 = 48_000.0 / 4096.0;
        let expected_bin = (1000.0 / bin_hz).round() as usize;

        // Find the band whose bin range contains the 1 kHz bin.
        let mut best = usize::MAX;
        for (i, b) in a.band_edges.windows(2).enumerate() {
            if expected_bin >= b[0] && expected_bin < b[1] {
                best = i;
            }
        }
        assert_ne!(best, usize::MAX, "1 kHz bin must fall inside a band");
        assert_eq!(bands[best], 1.0, "band {} must reach full energy", best);

        // The raw magnitude spectrum must actually peak inside that band.
        let peak_bin = (1..a.mag.len())
            .max_by(|x, y| a.mag[*x].partial_cmp(&a.mag[*y]).unwrap())
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
        let mut quiet = SpectrumAnalyzer::new(48_000, 1024, 8).unwrap();
        let mut loud = SpectrumAnalyzer::new(48_000, 1024, 8).unwrap();
        quiet.push(&tone(440.0, 48_000.0, 4096, 0.05));
        loud.push(&tone(440.0, 48_000.0, 4096, 0.9));
        assert!(loud.level() > quiet.level() * 4.0);
    }

    #[test]
    fn transient_detection_fires_on_onsets() {
        let mut a = SpectrumAnalyzer::new(48_000, 1024, 16).unwrap();
        // Feed silence until the flux average settles.
        for _ in 0..8 {
            a.push(&vec![0.0_f32; 1024]);
            a.analyze();
        }
        let mut detected = false;
        // A sharp burst should trip the detector within a few frames.
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
        let mut a = SpectrumAnalyzer::new(48_000, 1024, 8).unwrap();
        let mono = tone(600.0, 48_000.0, 2048, 0.5);
        let mut interleaved = Vec::with_capacity(mono.len() * 2);
        for s in &mono {
            interleaved.push(*s);
            interleaved.push(*s);
        }
        a.push_interleaved(&interleaved, 2);
        let stereo_level = a.level();
        assert!(stereo_level > 0.1 && stereo_level < 0.6, "level {}", stereo_level);
    }

    #[test]
    fn render_zones_produces_non_black_output_for_audible_input() {
        let mut a = SpectrumAnalyzer::new(48_000, 1024, 16).unwrap();
        a.push(&tone(200.0, 48_000.0, 4096, 0.9));
        let mut out = [0u8; 4 * 3];
        assert_eq!(a.render_zones([255, 40, 0], 100, &mut out), 4);
        assert!(out.iter().any(|&v| v > 0));
        // Blue channel must stay dark for a pure-red base color.
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
