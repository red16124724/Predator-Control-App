using System;
using System.Diagnostics;
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
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ACER");
        private static bool _serviceAvailable = true;
        private static DateTime _lastFailedAttempt = DateTime.MinValue;
        private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(10);

        public static bool IsServiceReachable
        {
            get => _serviceAvailable || (DateTime.UtcNow - _lastFailedAttempt) >= RetryCooldown;
        }

        public static void ResetConnectionState()
        {
            _serviceAvailable = true;
            _lastFailedAttempt = DateTime.MinValue;
        }

        private static void TryStartService(string serviceName)
        {
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
                p?.WaitForExit(1000);
            }
            catch { }
        }

        public static void EnsureAcerService()
        {
            if (_serviceAvailable) return;
            TryStartService("AcerServiceSvc");
            TryStartService("AcerLightingService");
        }

        public static void SetLogoLightingAsync(int mode, byte r, byte g, byte b, int brightness, int speed)
        {
            Task.Run(() =>
            {
                try
                {
                    EnsureAcerService();
                    if (!IsServiceReachable) return;

                    // Logo Device is a single RGB LED on Predator lids and only accepts STATIC, BREATHING, or NEON
                    string effect = mode switch
                    {
                        1 => "BREATHING",
                        2 or 3 => "NEON",
                        _ => "STATIC"
                    };

                    int scaledBrightness = (brightness <= 0) ? 0 : Math.Clamp((int)Math.Ceiling(brightness / 20.0), 1, 5);
                    string color = $"#{r:X2}{g:X2}{b:X2}";

                    string json = $"{{\"Function\":\"LIGHTING\",\"Parameter\":{{\"device\":4,\"effect\":\"{effect}\",\"speed\":{Math.Clamp(speed, 1, 5)},\"duration\":3,\"direction\":0,\"brightness\":{scaledBrightness},\"color\":\"{color.ToLower()}\",\"random\":true,\"LEDs\":[],\"dyEffect\":\"WAVE\",\"music_effect\":1,\"UserDynamicEffect\":8,\"Notification\":[{{\"name\":\"Number\",\"enable\":true}},{{\"name\":\"NewMail\",\"enable\":true}},{{\"name\":\"LowBattery\",\"enable\":true}}],\"subindex\":{{\"1\":\"{effect}\",\"2\":\"{effect}\",\"3\":\"{effect}\",\"4\":\"{effect}\"}},\"deviceName\":\"Logo Device\",\"colortype\":1}}}}";

                    SendCommand(100, json);
                }
                catch { }
            });
        }

        public static void SetLcdOverdriveAsync(bool enabled)
        {
            Task.Run(() =>
            {
                try
                {
                    EnsureAcerService();
                    if (!IsServiceReachable) return;

                    int status = enabled ? 1 : 0;
                    string json = $"{{\"Function\":\"LCD_OVERDRIVE\",\"Parameter\":{{\"status\":{status}}}}}";
                    SendCommand(100, json);
                }
                catch { }
            });
        }

        private static string? SendCommand(uint packetId, string jsonPayload)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.SendTimeout = 200;
                socket.ReceiveTimeout = 200;

                using var cts = new CancellationTokenSource(200);
                var connectTask = socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, CommandPort), cts.Token).AsTask();
                if (!connectTask.Wait(200) || !socket.Connected)
                {
                    _serviceAvailable = false;
                    _lastFailedAttempt = DateTime.UtcNow;
                    return null;
                }

                using var stream = new NetworkStream(socket, true);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonPayload);
                byte[] packet = new byte[8 + jsonBytes.Length];

                Buffer.BlockCopy(Magic, 0, packet, 0, 4);
                BitConverter.GetBytes(packetId).CopyTo(packet, 4);
                Buffer.BlockCopy(jsonBytes, 0, packet, 8, jsonBytes.Length);

                stream.Write(packet, 0, packet.Length);
                stream.Flush();

                byte[] buffer = new byte[4096];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead > 8)
                {
                    _serviceAvailable = true;
                    return Encoding.UTF8.GetString(buffer, 8, bytesRead - 8);
                }
                _serviceAvailable = true;
                return string.Empty;
            }
            catch
            {
                _serviceAvailable = false;
                _lastFailedAttempt = DateTime.UtcNow;
                return null;
            }
        }
    }
}
