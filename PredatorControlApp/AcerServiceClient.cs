using System;

using System.Collections.Generic;

using System.Diagnostics;

using System.Drawing;

using System.IO;

using System.Net;

using System.Net.Sockets;

using System.Text;

using System.Threading;

using System.Threading.Tasks;



namespace PredatorControlApp

{

    public class AcerServiceClient

    {

        public const int CommandPort = 46933;

        public const int LightingServicePort = 21919;

        internal static Action<uint, string>? CommandInterceptor { get; set; }

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ACER");

        private static readonly SemaphoreSlim _socketLock = new(1, 1);

        private static readonly SemaphoreSlim _keyboardLightingLock = new(1, 1);
        private static long _keyboardLightingGeneration;

        private static readonly object _stateLock = new();

        public const int OpenRgbPort = 6742;



        private static bool _openRgbAvailable = false;

        private static DateTime _lastOpenRgbAttempt = DateTime.MinValue;

        private static readonly TimeSpan OpenRgbCooldown = TimeSpan.FromSeconds(5);

        public static bool TrySendOpenRgbLeds(int deviceIndex, IReadOnlyList<Color> colors, int brightness = 100)

        {

            if (CommandInterceptor != null) return true;

            if (colors == null || colors.Count == 0) return false;

            lock (_stateLock)
            {
                if (!_openRgbAvailable && (DateTime.UtcNow - _lastOpenRgbAttempt < OpenRgbCooldown))
                {
                    return false;
                }
            }

            try

            {

                using var client = new System.Net.Sockets.TcpClient();

                var connectTask = client.ConnectAsync("127.0.0.1", OpenRgbPort);

                connectTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

                if (!connectTask.Wait(100) || !client.Connected)
                {
                    lock (_stateLock)
                    {
                        _openRgbAvailable = false;
                        _lastOpenRgbAttempt = DateTime.UtcNow;
                    }
                    return false;
                }

                lock (_stateLock)
                {
                    _openRgbAvailable = true;
                    _lastOpenRgbAttempt = DateTime.UtcNow;
                }



                using var stream = client.GetStream();

                stream.WriteTimeout = 300;



                byte[] clientName = Encoding.ASCII.GetBytes("PredatorControlApp\0");

                byte[] h1 = new byte[16];

                Buffer.BlockCopy(Encoding.ASCII.GetBytes("ORGB"), 0, h1, 0, 4);

                BitConverter.GetBytes((uint)0).CopyTo(h1, 4);

                BitConverter.GetBytes((uint)50).CopyTo(h1, 8);

                BitConverter.GetBytes((uint)clientName.Length).CopyTo(h1, 12);

                stream.Write(h1, 0, h1.Length);

                stream.Write(clientName, 0, clientName.Length);



                int numColors = colors.Count;

                int payloadSize = 4 + 2 + (numColors * 4);

                byte[] payload = new byte[payloadSize];

                BitConverter.GetBytes((uint)payloadSize).CopyTo(payload, 0);

                BitConverter.GetBytes((ushort)numColors).CopyTo(payload, 4);



                int effBright = Math.Clamp(brightness, 0, 100);

                for (int i = 0; i < numColors; i++)

                {

                    Color c = colors[i];

                    byte r = (byte)((c.R * effBright) / 100);

                    byte g = (byte)((c.G * effBright) / 100);

                    byte b = (byte)((c.B * effBright) / 100);

                    if (effBright > 0)

                    {

                        if (c.R > 0 && r == 0) r = 1;

                        if (c.G > 0 && g == 0) g = 1;

                        if (c.B > 0 && b == 0) b = 1;

                    }

                    int offset = 6 + (i * 4);

                    payload[offset] = r;

                    payload[offset + 1] = g;

                    payload[offset + 2] = b;

                    payload[offset + 3] = 0;

                }



                byte[] hUpdate = new byte[16];

                Buffer.BlockCopy(Encoding.ASCII.GetBytes("ORGB"), 0, hUpdate, 0, 4);

                BitConverter.GetBytes((uint)deviceIndex).CopyTo(hUpdate, 4);

                BitConverter.GetBytes((uint)1050).CopyTo(hUpdate, 8);

                BitConverter.GetBytes((uint)payloadSize).CopyTo(hUpdate, 12);



                stream.Write(hUpdate, 0, hUpdate.Length);

                stream.Write(payload, 0, payload.Length);

                stream.Flush();

                return true;

            }

            catch

            {

                return false;

            }

        }



        private static bool _serviceAvailable = true;

        private static DateTime _lastFailedAttempt = DateTime.MinValue;

        private static DateTime _lastServiceStartAttempt = DateTime.MinValue;

        private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan ServiceStartCooldown = TimeSpan.FromSeconds(60);



        public static bool IsServiceReachable

        {

            get

            {

                lock (_stateLock)

                {

                    return _serviceAvailable || (DateTime.UtcNow - _lastFailedAttempt) >= RetryCooldown;

                }

            }

        }



        public static void ResetConnectionState()

        {

            lock (_stateLock)

            {

                _serviceAvailable = true;

                _lastFailedAttempt = DateTime.MinValue;

            }

        }



        private static void TryStartService(string serviceName)

        {

            lock (_stateLock)

            {

                if ((DateTime.UtcNow - _lastServiceStartAttempt) < ServiceStartCooldown) return;

                _lastServiceStartAttempt = DateTime.UtcNow;

            }



            try

            {

                var psi = new ProcessStartInfo

                {

                    FileName = "sc.exe",

                    Arguments = $"start \"{serviceName}\"",

                    CreateNoWindow = true,

                    UseShellExecute = false

                };

                using var p = Process.Start(psi);

                if (p != null && !p.WaitForExit(1000))

                {

                    try { p.Kill(); } catch { }

                }

            }

            catch { }

        }



        public static void EnsureAcerService()

        {

            if (CommandInterceptor != null) return;

            lock (_stateLock)

            {

                if (_serviceAvailable) return;

            }

            TryStartService("AcerServiceSvc");

            TryStartService("AcerLightingService");

        }



        public static Task SetExteriorLightingAsync(
            int mode, byte r, byte g, byte b, int brightness, int speed,
            bool includeLightbars = true)
        {
            return Task.Run(() =>
            {
                try
                {
                    // OpenRGB Device 1 is keyboard on laptops: exterior lighting must never overwrite OpenRGB device 1
                    EnsureAcerService();
                    if (!IsServiceReachable && CommandInterceptor == null) return;

                    // Exterior devices only accept STATIC, BREATHING, or NEON
                    string effect = mode switch
                    {
                        1 => "BREATHING",
                        2 or 3 => "NEON",
                        _ => "STATIC"
                    };

                    int scaledBrightness = ServiceBrightness(brightness);
                    string color = $"#{r:X2}{g:X2}{b:X2}";

                    // Rear lightbars & accent only: lid logo (AcerService device 4 / OpenRGB device 0) is strictly excluded
                    var devices = new List<(int Id, string Name)>();
                    if (includeLightbars)
                    {
                        devices.Add((2, "Light Bar"));
                        devices.Add((3, "Light Strip"));
                        devices.Add((5, "Exterior Accent"));
                    }

                    foreach (var (devId, devName) in devices)

                    {

                        string json = $"{{\"Function\":\"LIGHTING\",\"Parameter\":{{\"device\":{devId},\"effect\":\"{effect}\",\"speed\":{Math.Clamp(speed, 1, 5)},\"duration\":3,\"direction\":0,\"brightness\":{scaledBrightness},\"color\":\"{color.ToLower()}\",\"random\":false,\"LEDs\":[{{\"index\":0,\"color\":\"{color.ToLower()}\"}},{{\"index\":1,\"color\":\"{color.ToLower()}\"}}],\"dyEffect\":\"WAVE\",\"music_effect\":1,\"UserDynamicEffect\":8,\"Notification\":[{{\"name\":\"Number\",\"enable\":true}},{{\"name\":\"NewMail\",\"enable\":true}},{{\"name\":\"LowBattery\",\"enable\":true}}],\"deviceName\":\"{devName}\",\"colortype\":1}}}}";



                        string? resp = SendCommand(100, json, LightingServicePort, updateFailureState: false);

                        if (string.IsNullOrEmpty(resp))

                        {

                            SendCommand(100, json, CommandPort, updateFailureState: false);

                        }

                    }

                }

                catch { }

            });

        }



        public static Task SetExteriorColorAsync(Color color, int brightness = 100, int speed = 1)

        {

            return SetExteriorLightingAsync(0, color.R, color.G, color.B, brightness, speed);

        }



        public static void SetExteriorColor(Color color, int brightness = 100, int speed = 1)

        {

            try { SetExteriorColorAsync(color, brightness, speed).Wait(1000); } catch { }

        }



        public const uint LcdOverdrivePacketId = 100;



        /// <summary>

        /// Non-intrusive TCP probe for the Acer Lighting Service. Used by the

        /// keyboard capability detector; it does not write any lighting state.

        /// </summary>

        public static Task<bool> ProbeServiceAsync(int timeoutMs = 350)

        {

            return Task.Run(async () =>

            {

                lock (_stateLock)
                {
                    if (!_serviceAvailable && (DateTime.UtcNow - _lastFailedAttempt) < RetryCooldown)
                    {
                        return false;
                    }
                }

                try

                {

                    int[] ports = { LightingServicePort, CommandPort };

                    foreach (int port in ports)

                    {

                        try

                        {

                            using var client = new TcpClient();

                            var connect = client.ConnectAsync(IPAddress.Loopback, port);

                            var completed = await Task.WhenAny(connect, Task.Delay(Math.Clamp(timeoutMs / 2, 50, 2500))).ConfigureAwait(false);

                            if (completed == connect && client.Connected)

                            {

                                lock (_stateLock)

                                {

                                    _serviceAvailable = true;

                                    _lastFailedAttempt = DateTime.MinValue;

                                }

                                return true;

                            }

                        }

                        catch { }

                    }

                    lock (_stateLock)

                    {

                        _serviceAvailable = false;

                        _lastFailedAttempt = DateTime.UtcNow;

                    }

                    return false;

                }

                catch

                {

                    lock (_stateLock)

                    {

                        _serviceAvailable = false;

                        _lastFailedAttempt = DateTime.UtcNow;

                    }

                    return false;

                }

            });

        }



        internal static int ServiceBrightness(int value) =>
            NativeLightingRules.TryServiceLevel(value, false, out int native) ? native :
            value <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(value / 20.0), 1, 5);

        internal static int ServiceSpeed(int value) =>
            NativeLightingRules.TryServiceLevel(value, true, out int native) ? native :
            Math.Clamp((int)Math.Round(Math.Clamp(value, 1, 100) / 100.0 * 5.0), 1, 5);

        /// <summary>

        /// Builds the Lighting Service keyboard payload. Zone lighting uses

        /// device 1 with an empty LED array; the experimental per-key path adds

        /// one LED entry per logical key.

        /// </summary>

        public static string BuildKeyboardLightingPayload(

            string effect,

            IReadOnlyList<(int Index, byte R, byte G, byte B)>? leds,

            int brightnessPercent,

            int speedPercent,

            bool perKey)

        {

            string safeEffect = NormalizeLightingEffect(effect);

            int scaledBrightness = ServiceBrightness(brightnessPercent);

            int scaledSpeed = ServiceSpeed(speedPercent);



            var sb = new StringBuilder(2048);

            sb.Append("{\"Function\":\"LIGHTING\",\"Parameter\":{");

            sb.Append("\"device\":1,");

            sb.Append("\"effect\":\"").Append(safeEffect).Append("\",");

            sb.Append("\"speed\":").Append(scaledSpeed).Append(',');

            sb.Append("\"duration\":3,\"direction\":0,");

            sb.Append("\"brightness\":").Append(scaledBrightness).Append(',');

            sb.Append("\"color\":\"#ffffff\",\"random\":false,");

            sb.Append("\"LEDs\":[");

            if (leds != null && leds.Count > 0)

            {

                for (int i = 0; i < leds.Count; i++)

                {

                    if (i > 0) sb.Append(',');

                    var led = leds[i];

                    sb.Append("{\"index\":").Append(led.Index)

                      .Append(",\"color\":\"#")

                      .Append(led.R.ToString("X2"))

                      .Append(led.G.ToString("X2"))

                      .Append(led.B.ToString("X2"))

                      .Append("\"}");

                }

            }

            sb.Append("],");

            sb.Append("\"dyEffect\":\"WAVE\",\"music_effect\":1,\"UserDynamicEffect\":8,");

            sb.Append("\"subindex\":{\"1\":\"").Append(safeEffect)

              .Append("\",\"2\":\"").Append(safeEffect)

              .Append("\",\"3\":\"").Append(safeEffect)

              .Append("\",\"4\":\"").Append(safeEffect).Append("\"},");

            sb.Append("\"deviceName\":\"Keyboard Device\",\"colortype\":1}}");

            return sb.ToString();

        }



        /// <summary>

        /// Builds the Lighting Service 4-zone lighting payload. Zone lighting uses

        /// device 1 with colortype 2 and effect STATIC, mapping zone indices 0..3 to hex colors.

        /// </summary>

        public static string BuildZoneLightingPayload(

            IReadOnlyList<Color>? zoneColors,

            int brightnessPercent = 100,

            int speedPercent = 50)

        {

            int scaledBrightness = ServiceBrightness(brightnessPercent);

            int scaledSpeed = ServiceSpeed(speedPercent);



            var sb = new StringBuilder(1024);

            sb.Append("{\"Function\":\"LIGHTING\",\"Parameter\":{");

            sb.Append("\"device\":1,");

            sb.Append("\"effect\":\"STATIC\",");

            sb.Append("\"speed\":").Append(scaledSpeed).Append(',');

            sb.Append("\"duration\":3,\"direction\":0,");

            sb.Append("\"brightness\":").Append(scaledBrightness).Append(',');

            sb.Append("\"color\":\"#ffffff\",\"random\":false,");

            sb.Append("\"LEDs\":[");

            if (zoneColors != null && zoneColors.Count > 0)
            {
                Color[] mapped = zoneColors.Count switch
                {
                    1 => new[] { zoneColors[0], zoneColors[0], zoneColors[0], zoneColors[0] },
                    2 => new[] { zoneColors[0], zoneColors[0], zoneColors[1], zoneColors[1] },
                    3 => new[] { zoneColors[0], zoneColors[1], zoneColors[1], zoneColors[2] },
                    _ => new[] { zoneColors[0], zoneColors[1], zoneColors[2], zoneColors[3] }
                };

                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) sb.Append(',');
                    Color c = mapped[i];
                    sb.Append("{\"index\":").Append(i)
                      .Append(",\"color\":\"#")
                      .Append(c.R.ToString("X2"))
                      .Append(c.G.ToString("X2"))
                      .Append(c.B.ToString("X2"))
                      .Append("\"}");
                }
            }

            sb.Append("],");

            sb.Append("\"dyEffect\":\"WAVE\",\"music_effect\":1,\"UserDynamicEffect\":8,");

            sb.Append("\"subindex\":{\"1\":\"STATIC\",\"2\":\"STATIC\",\"3\":\"STATIC\",\"4\":\"STATIC\"},");

            sb.Append("\"deviceName\":\"Keyboard Device\",\"colortype\":2}}");

            return sb.ToString();

        }



        public static Task<bool> SetZoneLightingAsync(

            IReadOnlyList<Color> zoneColors,

            int brightness = 100,

            int speed = 50,

            CancellationToken cancellationToken = default)

        {

            long generation = Interlocked.Increment(ref _keyboardLightingGeneration);
            Color[] colors = zoneColors?.ToArray() ?? Array.Empty<Color>();
            return QueueKeyboardLighting(generation, () =>

            {

                try

                {

                    if (cancellationToken.IsCancellationRequested) return false;

                    // OpenRGB Device 1 is keyboard only; Device 0 (lid logo / exterior accent) is disconnected
                    try { TrySendOpenRgbLeds(1, colors, brightness); } catch { }

                    EnsureAcerService();

                    if (!IsServiceReachable && CommandInterceptor == null) return false;



                    string json = BuildZoneLightingPayload(colors, brightness, speed);

                    // Try AcerLightingService port 21919 first without poisoning reachable state; fallback to 46933

                    string? response = SendCommand(100, json, LightingServicePort, updateFailureState: false);

                    if (response == null && !cancellationToken.IsCancellationRequested)

                    {

                        response = SendCommand(100, json, CommandPort, updateFailureState: true);

                    }

                    return response != null;

                }

                catch

                {

                    return false;

                }

            }, cancellationToken);

        }



        /// <summary>

        /// Applies a keyboard lighting payload through the Acer Lighting Service.

        /// Returns false when the service is absent or the write is rejected;

        /// callers must degrade to the WMI 4-zone path.

        /// </summary>

        public static Task<bool> SetKeyboardLightingAsync(

            int device,

            string effect,

            IReadOnlyList<(int Index, byte R, byte G, byte B)>? leds,

            int brightnessPercent,

            int speedPercent,

            bool perKey)

        {

            long generation = Interlocked.Increment(ref _keyboardLightingGeneration);
            var colors = leds?.ToArray();
            return QueueKeyboardLighting(generation, () =>

            {

                try

                {

                    EnsureAcerService();

                    if (!IsServiceReachable && CommandInterceptor == null) return false;



                    string json = BuildKeyboardLightingPayload(effect, colors, brightnessPercent, speedPercent, perKey);

                    string? response = SendCommand(100, json);

                    return response != null;

                }

                catch

                {

                    return false;

                }

            });

        }

        internal static void SupersedeKeyboardLighting()
        {
            Interlocked.Increment(ref _keyboardLightingGeneration);
        }

        private static Task<bool> QueueKeyboardLighting(long generation, Func<bool> send, CancellationToken cancellationToken = default)
        {
            return Task.Run(async () =>
            {
                bool entered = false;
                try
                {
                    await _keyboardLightingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    entered = true;
                    if (cancellationToken.IsCancellationRequested ||
                        generation != Volatile.Read(ref _keyboardLightingGeneration)) return false;

                    return send();
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                finally
                {
                    if (entered) _keyboardLightingLock.Release();
                }
            }, cancellationToken);
        }



        private static string NormalizeLightingEffect(string? effect)

        {

            if (string.IsNullOrWhiteSpace(effect)) return "STATIC";

            var sb = new StringBuilder(effect.Length);

            foreach (char c in effect.Trim().ToUpperInvariant())

            {

                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_') sb.Append(c);

            }

            return sb.Length == 0 ? "STATIC" : sb.ToString();

        }



        public static string BuildLcdOverdrivePayload(bool enabled)

        {

            int status = enabled ? 1 : 0;

            return $"{{\"Function\":\"LCD_OVERDRIVE\",\"Parameter\":{{\"status\":{status}}}}}";

        }



        public static Task SetLcdOverdriveAsync(bool enabled)

        {

            // Milestone 8: no Acer service IPC is sent. Packet 100 (LCD_OVERDRIVE)

            // is retained as a constant and payload builder for protocol documentation

            // and tests, but this entry point is deliberately inert.

            _ = enabled;

            return Task.CompletedTask;

        }



        private static string? SendCommand(uint packetId, string jsonPayload, int port = CommandPort, bool updateFailureState = true)

        {

            if (jsonPayload != null) { try { CommandInterceptor?.Invoke(packetId, jsonPayload); } catch { } }

            if (CommandInterceptor != null) return "{\"Status\":0}";

            lock (_stateLock)
            {
                if (!_serviceAvailable && (DateTime.UtcNow - _lastFailedAttempt) < RetryCooldown)
                {
                    return null;
                }
            }

            if (!_socketLock.Wait(500)) return null;

            try

            {

                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                socket.SendTimeout = 200;

                socket.ReceiveTimeout = 200;



                using var cts = new CancellationTokenSource(200);

                var connectTask = socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cts.Token).AsTask();

                connectTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);



                bool connected = false;

                try

                {

                    connected = connectTask.Wait(200) && socket.Connected;

                }

                catch

                {

                    connected = false;

                }



                if (!connected)

                {

                    try { cts.Cancel(); } catch { }

                    try { socket.Close(0); } catch { }

                    if (updateFailureState)

                    {

                        lock (_stateLock)

                        {

                            _serviceAvailable = false;

                            _lastFailedAttempt = DateTime.UtcNow;

                        }

                    }

                    return null;

                }



                using var stream = new NetworkStream(socket, false);

                string safePayload = jsonPayload ?? string.Empty;

                byte[] jsonBytes = Encoding.UTF8.GetBytes(safePayload);

                byte[] packet = new byte[8 + jsonBytes.Length];



                Buffer.BlockCopy(Magic, 0, packet, 0, 4);

                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), packetId);

                Buffer.BlockCopy(jsonBytes, 0, packet, 8, jsonBytes.Length);



                stream.Write(packet, 0, packet.Length);

                stream.Flush();



                byte[] buffer = new byte[4096];

                int bytesRead = stream.Read(buffer, 0, buffer.Length);

                if (bytesRead >= 8 && buffer.AsSpan(0, 4).SequenceEqual(Magic))

                {

                    lock (_stateLock) _serviceAvailable = true;

                    if (!stream.DataAvailable)

                    {

                        return Encoding.UTF8.GetString(buffer, 8, bytesRead - 8);

                    }

                    using var ms = new MemoryStream();

                    ms.Write(buffer, 8, bytesRead - 8);

                    while (stream.DataAvailable)

                    {

                        int additional = stream.Read(buffer, 0, buffer.Length);

                        if (additional <= 0) break;

                        ms.Write(buffer, 0, additional);

                    }

                    return Encoding.UTF8.GetString(ms.ToArray());

                }

                if (updateFailureState)

                {

                    lock (_stateLock)

                    {

                        _serviceAvailable = false;

                        _lastFailedAttempt = DateTime.UtcNow;

                    }

                }

                return null;

            }

            catch

            {

                if (updateFailureState)

                {

                    lock (_stateLock)

                    {

                        _serviceAvailable = false;

                        _lastFailedAttempt = DateTime.UtcNow;

                    }

                }

                return null;

            }

            finally

            {

                _socketLock.Release();

            }

        }

    }

}

