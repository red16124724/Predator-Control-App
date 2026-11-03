using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace PredatorControlApp
{
    public delegate void AudioSamplesAvailableHandler(float[] samples, int count, int channels);

    /// <summary>
    /// High-performance WASAPI loopback audio capture provider.
    /// Features:
    /// - Zero allocations on the steady-state packet capture loop.
    /// - Automatic device invalidation / disconnection detection (AUDCLNT_E_DEVICE_INVALIDATED).
    /// - Seamless background reconnection when audio endpoints switch or reconnect.
    /// - Clean COM interface disposal with zero handle leaks.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WasapiCapture : IDisposable
    {
        public const int TargetSampleRate = 48_000;
        public const int FftSize = 2048;
        private const int CaptureSleepMs = 10;
        private const long BufferDurationHns = 1_000_000; // 100 ms

        private const int AudclntShareModeShared = 0;
        private const uint AudclntStreamFlagsLoopback = 0x00020000;
        private const int ClsctxAll = 23;
        private const int ERender = 0;
        private const int EConsole = 0;

        public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
        public const int AudclntEServiceNotRunning = unchecked((int)0x88890003);
        public const int AudclntEResourcesInvalidated = unchecked((int)0x88890026);

        private readonly object _lock = new();
        private Thread? _captureThread;
        private volatile bool _running;
        private volatile bool _disposed;
        private volatile bool _captureFailed;
        private int _sampleRate = TargetSampleRate;
        private int _channels = 2;
        private int _reconnectCount = 0;

        public event AudioSamplesAvailableHandler? SamplesAvailable;
        public event Action<bool>? StateChanged;

        public bool IsRunning => _running;
        public bool HadCaptureFailure => _captureFailed;
        public int SampleRate => _sampleRate;
        public int Channels => _channels;
        public int ReconnectCount => _reconnectCount;

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed || _running) return;
                _running = true;
                _captureThread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = "WasapiCapture-Thread",
                    Priority = ThreadPriority.BelowNormal
                };
                _captureThread.Start();
            }
        }

        public void Stop()
        {
            _running = false;
            Thread? thread;
            lock (_lock)
            {
                thread = _captureThread;
                _captureThread = null;
            }
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                try { thread.Join(500); } catch { }
            }
        }

        private void CaptureLoop()
        {
            while (_running && !_disposed)
            {
                IMMDeviceEnumerator? enumerator = null;
                IMMDevice? device = null;
                IAudioClient? client = null;
                IAudioCaptureClient? capture = null;
                IntPtr pFormat = IntPtr.Zero;

                try
                {
                    enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                    int hr = enumerator.GetDefaultAudioEndpoint(ERender, EConsole, out device);
                    if (hr != 0 || device == null)
                    {
                        _captureFailed = true;
                    }
                    else
                    {
                        Guid iidClient = typeof(IAudioClient).GUID;
                        hr = device.Activate(ref iidClient, ClsctxAll, IntPtr.Zero, out object? clientObj);
                        if (hr != 0 || clientObj is not IAudioClient audioClient)
                        {
                            _captureFailed = true;
                        }
                        else
                        {
                            client = audioClient;
                            hr = client.GetMixFormat(out pFormat);
                            if (hr != 0 || pFormat == IntPtr.Zero)
                            {
                                _captureFailed = true;
                            }
                            else
                            {
                                var mix = Marshal.PtrToStructure<WaveFormatEx>(pFormat);
                                const ushort WaveFormatIeeeFloat = 3;
                                const ushort WaveFormatExtensible = 0xFFFE;
                                bool isFloat = mix.FormatTag == WaveFormatIeeeFloat || mix.FormatTag == WaveFormatExtensible;

                                if (!isFloat || mix.Channels == 0 || mix.Channels > 8)
                                {
                                    _captureFailed = true;
                                }
                                else
                                {
                                    _channels = mix.Channels;
                                    _sampleRate = mix.SamplesPerSec > 0 ? (int)mix.SamplesPerSec : TargetSampleRate;

                                    hr = client.Initialize(
                                        AudclntShareModeShared,
                                        AudclntStreamFlagsLoopback,
                                        BufferDurationHns,
                                        0,
                                        pFormat,
                                        IntPtr.Zero);

                                    if (hr != 0)
                                    {
                                        _captureFailed = true;
                                    }
                                    else
                                    {
                                        Guid iidCapture = typeof(IAudioCaptureClient).GUID;
                                        hr = client.GetService(ref iidCapture, out IntPtr pCapture);
                                        if (hr != 0 || pCapture == IntPtr.Zero)
                                        {
                                            _captureFailed = true;
                                        }
                                        else
                                        {
                                            capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(pCapture);
                                            Marshal.Release(pCapture);

                                            hr = client.Start();
                                            if (hr != 0)
                                            {
                                                _captureFailed = true;
                                            }
                                            else
                                            {
                                                _captureFailed = false;
                                                StateChanged?.Invoke(true);
                                                CapturePackets(capture);
                                                try { client.Stop(); } catch { }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    _captureFailed = true;
                }
                finally
                {
                    if (pFormat != IntPtr.Zero)
                    {
                        try { Marshal.FreeCoTaskMem(pFormat); } catch { }
                    }
                    if (capture != null) { try { Marshal.ReleaseComObject(capture); } catch { } }
                    if (client != null) { try { Marshal.ReleaseComObject(client); } catch { } }
                    if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
                    if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } }
                }

                if (_running && !_disposed)
                {
                    Interlocked.Increment(ref _reconnectCount);
                    StateChanged?.Invoke(false);
                    // 500 ms backoff before reconnect attempt
                    Thread.Sleep(500);
                }
            }
            _running = false;
            StateChanged?.Invoke(false);
        }

        private void CapturePackets(IAudioCaptureClient capture)
        {
            const uint AudclntBufferflagsSilent = 0x2;
            float[]? sampleBuffer = null;
            int consecutiveErrors = 0;

            while (_running && !_disposed)
            {
                int hr = capture.GetNextPacketSize(out uint packetFrames);
                if (hr != 0)
                {
                    if (hr == AudclntEDeviceInvalidated || hr == AudclntEServiceNotRunning || hr == AudclntEResourcesInvalidated)
                    {
                        _captureFailed = true;
                        break;
                    }
                    consecutiveErrors++;
                    if (consecutiveErrors >= 10)
                    {
                        _captureFailed = true;
                        break;
                    }
                    Thread.Sleep(CaptureSleepMs);
                    continue;
                }
                consecutiveErrors = 0;

                if (packetFrames == 0)
                {
                    Thread.Sleep(CaptureSleepMs);
                    continue;
                }

                while (packetFrames > 0 && _running && !_disposed)
                {
                    hr = capture.GetBuffer(out IntPtr pData, out uint frames, out uint flags, out _, out _);
                    if (hr != 0 || frames == 0)
                    {
                        if (hr == AudclntEDeviceInvalidated || hr == AudclntEServiceNotRunning || hr == AudclntEResourcesInvalidated)
                        {
                            _captureFailed = true;
                            return;
                        }
                        break;
                    }

                    int valueCount = checked((int)frames * _channels);
                    if (sampleBuffer == null || sampleBuffer.Length < valueCount)
                    {
                        sampleBuffer = new float[Math.Max(valueCount, FftSize * _channels)];
                    }

                    if ((flags & AudclntBufferflagsSilent) != 0 || pData == IntPtr.Zero)
                    {
                        Array.Clear(sampleBuffer, 0, valueCount);
                    }
                    else
                    {
                        Marshal.Copy(pData, sampleBuffer, 0, valueCount);
                    }

                    capture.ReleaseBuffer(frames);

                    SamplesAvailable?.Invoke(sampleBuffer, valueCount, _channels);

                    if (capture.GetNextPacketSize(out packetFrames) != 0) break;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        #region WASAPI COM Declarations
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WaveFormatEx
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSec;
            public uint AvgBytesPerSec;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort ExtraSize;
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291492E")]
        private class MMDeviceEnumerator { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
            int GetDevice(string pwstrId, out IMMDevice ppDevice);
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
            int GetId(out string ppstrId);
            int GetState(out int pdwState);
        }

        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig]
            int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
            [PreserveSig]
            int GetBufferSize(out uint pNumBufferFrames);
            [PreserveSig]
            int GetStreamLatency(out long phnsLatency);
            [PreserveSig]
            int GetCurrentPadding(out uint pNumPaddingFrames);
            [PreserveSig]
            int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
            [PreserveSig]
            int GetMixFormat(out IntPtr ppDeviceFormat);
            [PreserveSig]
            int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
            [PreserveSig]
            int Start();
            [PreserveSig]
            int Stop();
            [PreserveSig]
            int Reset();
            [PreserveSig]
            int SetEventHandle(IntPtr eventHandle);
            [PreserveSig]
            int GetService(ref Guid riid, out IntPtr ppv);
        }

        [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
            [PreserveSig]
            int ReleaseBuffer(uint numFramesRead);
            [PreserveSig]
            int GetNextPacketSize(out uint pNumFramesInNextPacket);
        }
        #endregion
    }
}
