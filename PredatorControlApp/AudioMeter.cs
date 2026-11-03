using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace PredatorControlApp
{
    /// <summary>
    /// Audio-reactive spectrum analyzer.
    ///
    /// The DSP (Hann window, real FFT, log-spaced bands, spectral-flux transient
    /// detection, per-zone RGB mapping) lives entirely in <c>predator_core</c>
    /// (<c>audio.rs</c> with <c>realfft</c>/<c>rustfft</c> + runtime-dispatched AVX).
    /// Loopback capture is managed by <see cref="WasapiCapture"/>, feeding raw
    /// interleaved float samples directly to the native analyzer.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class AudioMeter : IDisposable
    {
        private const int TargetSampleRate = 48_000;
        private const int FftSize = 2048;
        private const int BandCount = 32;

        private readonly object _gate = new();
        private readonly float[] _bandBuffer = new float[BandCount];
        private readonly WasapiCapture _capture;

        private IntPtr _nativeHandle;
        private volatile bool _disposed;

        public AudioMeter()
        {
            if (PredatorNativeCore.IsNativeCoreAvailable)
            {
                _nativeHandle = PredatorNativeCore.AudioCreate(TargetSampleRate, FftSize, BandCount);
            }

            _capture = new WasapiCapture();
            _capture.SamplesAvailable += OnSamplesAvailable;
        }

        private void OnSamplesAvailable(float[] samples, int count, int channels)
        {
            if (_disposed || !IsAvailable) return;
            lock (_gate)
            {
                if (_nativeHandle != IntPtr.Zero)
                {
                    PredatorNativeCore.AudioPush(_nativeHandle, samples, count, channels);
                }
            }
        }

        /// <summary>True when the native FFT core is loaded and a capture handle exists.</summary>
        public bool IsAvailable => PredatorNativeCore.IsNativeCoreAvailable && _nativeHandle != IntPtr.Zero;

        public bool IsCapturing => _capture.IsRunning;

        /// <summary>True if the last capture attempt failed (no endpoint, device busy, ...).</summary>
        public bool HadCaptureFailure => _capture.HadCaptureFailure;

        public int BandCountValue => BandCount;

        public int SampleRate => _capture.SampleRate;

        public int Channels => _capture.Channels;

        public int ReconnectCount => _capture.ReconnectCount;

        public WasapiCapture UnderlyingCapture => _capture;

        /// <summary>Smoothed RMS level, 0..~1 for normal program material.</summary>
        public float Level
        {
            get
            {
                if (!IsAvailable) return 0f;
                lock (_gate)
                {
                    return _nativeHandle != IntPtr.Zero ? PredatorNativeCore.AudioGetLevel(_nativeHandle) : 0f;
                }
            }
        }

        public bool IsBeat
        {
            get
            {
                if (!IsAvailable) return false;
                lock (_gate)
                {
                    return _nativeHandle != IntPtr.Zero && PredatorNativeCore.AudioIsBeat(_nativeHandle);
                }
            }
        }

        public float BeatStrength
        {
            get
            {
                if (!IsAvailable) return 0f;
                lock (_gate)
                {
                    return _nativeHandle != IntPtr.Zero ? PredatorNativeCore.AudioBeatStrength(_nativeHandle) : 0f;
                }
            }
        }

        /// <summary>
        /// Copies the current band energies (0..1, log spaced 32 Hz..16 kHz) into
        /// <paramref name="destination"/>. Returns the number of bands written.
        /// </summary>
        public int GetSpectrum(float[]? destination)
        {
            if (destination == null || destination.Length == 0 || !IsAvailable) return 0;
            lock (_gate)
            {
                if (_nativeHandle == IntPtr.Zero) return 0;
                return PredatorNativeCore.AudioGetBands(_nativeHandle, destination);
            }
        }

        /// <summary>Convenience overload returning the shared band buffer (do not hold it).</summary>
        public float[] GetSpectrum()
        {
            lock (_gate)
            {
                if (IsAvailable && _nativeHandle != IntPtr.Zero)
                {
                    PredatorNativeCore.AudioGetBands(_nativeHandle, _bandBuffer);
                }
                return _bandBuffer;
            }
        }

        /// <summary>
        /// Renders the spectrum onto <paramref name="zoneCount"/> keyboard zones as RGB
        /// triples (bass first). Returns the number of zones written.
        /// </summary>
        public int RenderZones(byte r, byte g, byte b, byte brightness, byte[] destination, int zoneCount)
        {
            if (destination == null || zoneCount <= 0 || !IsAvailable) return 0;
            if (destination.Length < zoneCount * 3) return 0;
            lock (_gate)
            {
                if (_nativeHandle == IntPtr.Zero) return 0;
                return PredatorNativeCore.AudioRenderZones(_nativeHandle, r, g, b, brightness, destination, zoneCount);
            }
        }

        public void Reset()
        {
            if (!IsAvailable) return;
            lock (_gate)
            {
                if (_nativeHandle != IntPtr.Zero)
                {
                    PredatorNativeCore.AudioReset(_nativeHandle);
                }
            }
        }

        /// <summary>Starts the WASAPI loopback capture thread. Safe to call repeatedly.</summary>
        public void Start()
        {
            if (_disposed || !IsAvailable) return;
            _capture.Start();
        }

        public void Stop()
        {
            _capture.Stop();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _capture.Dispose();

            IntPtr handle;
            lock (_gate)
            {
                handle = _nativeHandle;
                _nativeHandle = IntPtr.Zero;
            }
            if (handle != IntPtr.Zero) PredatorNativeCore.AudioDestroy(handle);
        }

        public static AudioMeter? TryCreate()
        {
            var meter = new AudioMeter();
            if (!meter.IsAvailable)
            {
                meter.Dispose();
                return null;
            }
            return meter;
        }
    }
}
