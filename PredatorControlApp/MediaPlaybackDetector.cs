using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public static class MediaPlaybackDetector
    {
        private static DateTime _lastCheckTime = DateTime.MinValue;
        private static bool _lastResult = false;
        private static readonly object _stateLock = new();
        private static readonly TimeSpan CheckCacheDuration = TimeSpan.FromMilliseconds(1000);

        public static bool IsAudioPlaying()
        {
            lock (_stateLock)
            {
                if (DateTime.UtcNow - _lastCheckTime < CheckCacheDuration)
                    return _lastResult;

                _lastCheckTime = DateTime.UtcNow;
                try
                {
                    _lastResult = CheckActiveAudioSessions();
                }
                catch
                {
                    _lastResult = false;
                }
                return _lastResult;
            }
        }

        private static bool CheckActiveAudioSessions()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator == null) return false;

            try
            {
                // eRender = 0, eMultimedia = 1
                int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice? device);
                if (hr != 0 || device == null)
                {
                    return false;
                }

                try
                {
                    Guid iidManager = typeof(IAudioSessionManager2).GUID;
                    hr = device.Activate(ref iidManager, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object? objManager);
                    if (hr != 0 || objManager == null) return false;

                    var manager = (IAudioSessionManager2)objManager;
                    try
                    {
                        hr = manager.GetSessionEnumerator(out IAudioSessionEnumerator? sessionEnumerator);
                        if (hr != 0 || sessionEnumerator == null) return false;

                        try
                        {
                            hr = sessionEnumerator.GetCount(out int count);
                            if (hr != 0 || count <= 0) return false;

                            for (int i = 0; i < count; i++)
                            {
                                hr = sessionEnumerator.GetSession(i, out IAudioSessionControl? control);
                                if (hr != 0 || control == null) continue;

                                try
                                {
                                    hr = control.GetState(out AudioSessionState state);
                                    if (hr == 0 && state == AudioSessionState.Active)
                                    {
                                        return true;
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(control);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(sessionEnumerator);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(manager);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }

            return false;
        }

        public enum AudioSessionState
        {
            Inactive = 0,
            Active = 1,
            Expired = 2
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291492E")]
        private class MMDeviceEnumerator
        {
        }

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

        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags, out IntPtr sessionControl);
            int GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, out IntPtr audioVolume);
            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
            int RegisterSessionNotification(IntPtr newSessionNotification);
            int UnregisterSessionNotification(IntPtr newSessionNotification);
            int RegisterDuckNotification(string sessionID, IntPtr duckNotification);
            int UnregisterDuckNotification(IntPtr duckNotification);
        }

        [Guid("E2F5EE55-048C-4610-BB86-ABF622CB575C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int sessionCount);
            [PreserveSig]
            int GetSession(int sessionIndex, out IAudioSessionControl session);
        }

        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            [PreserveSig]
            int GetState(out AudioSessionState pRetVal);
            int GetDisplayName(out string pRetVal);
            int SetDisplayName(string value, ref Guid eventContext);
            int GetIconPath(out string pRetVal);
            int SetIconPath(string value, ref Guid eventContext);
            int GetGroupingParam(out Guid pRetVal);
            int SetGroupingParam(ref Guid overrideValue, ref Guid eventContext);
            int RegisterAudioSessionNotification(IntPtr newNotifications);
            int UnregisterAudioSessionNotification(IntPtr newNotifications);
        }
    }
}
