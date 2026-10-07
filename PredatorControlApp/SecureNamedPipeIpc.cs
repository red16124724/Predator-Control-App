using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PredatorControlApp
{
    public static class SecureNamedPipeIpc
    {
        public const string PipeName = "PredatorControlPipe";
        private const int MaxMessageLength = 256;

        public static PipeSecurity CreatePipeSecurity()
        {
            var sec = new PipeSecurity();
            var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                try
                {
                    sec.SetOwner(currentUser);
                }
                catch { }
            }

            sec.AddAccessRule(new PipeAccessRule(network, PipeAccessRights.FullControl, AccessControlType.Deny));
            sec.AddAccessRule(new PipeAccessRule(localSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
            if (currentUser != null && !currentUser.Equals(localSystem) && !currentUser.Equals(admins))
            {
                sec.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            sec.AddAccessRule(new PipeAccessRule(authUsers, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            // Deny CreateNewInstance for Authenticated Users to prevent pipe squatting / instance hijacking
            sec.AddAccessRule(new PipeAccessRule(authUsers, PipeAccessRights.CreateNewInstance, AccessControlType.Deny));

            return sec;
        }

        public static bool IsTrustedServer(NamedPipeClientStream pipe)
        {
            try
            {
                var accessControl = pipe.GetAccessControl();
                var owner = accessControl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner == null) return false;

                if (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                    owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                {
                    return true;
                }

                var currentUser = WindowsIdentity.GetCurrent().User;
                return currentUser != null && owner.Equals(currentUser);
            }
            catch
            {
                return false;
            }
        }

        public sealed class PipeServer : IDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly Action<string> _onMessage;
            private readonly string _pipeName;
            private bool _isDisposed;
            private bool _started;
            private readonly object _startLock = new();

            public PipeServer(Action<string> onMessage, string? pipeName = null)
            {
                _onMessage = onMessage ?? throw new ArgumentNullException(nameof(onMessage));
                _pipeName = pipeName ?? PipeName;
            }

            public void Start()
            {
                lock (_startLock)
                {
                    if (_isDisposed || _started) return;
                    _started = true;
                    Task.Run(RunServerLoopAsync);
                }
            }

            private async Task RunServerLoopAsync()
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = NamedPipeServerStreamAcl.Create(
                            _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreatePipeSecurity());

                        await pipe.WaitForConnectionAsync(_cts.Token);

                        using var reader = new StreamReader(pipe, Encoding.UTF8);
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        readCts.CancelAfter(2000);
                        try
                        {
                            // Bounded message reading to prevent unbounded memory allocation DoS attacks
                            var sb = new StringBuilder();
                            char[] charBuf = new char[1];
                            while (sb.Length < MaxMessageLength)
                            {
                                int readCount = await reader.ReadAsync(charBuf.AsMemory(0, 1), readCts.Token);
                                if (readCount <= 0) break;
                                if (charBuf[0] == '\n') break;
                                if (charBuf[0] != '\r') sb.Append(charBuf[0]);
                            }

                            string line = sb.ToString().Trim();
                            if (!string.IsNullOrEmpty(line))
                            {
                                try { _onMessage(line); } catch { }
                            }
                        }
                        catch (OperationCanceledException) when (!_cts.Token.IsCancellationRequested)
                        {
                            // Per-client read timeout; continue server loop to accept next connection
                        }
                        catch (IOException)
                        {
                            // Client disconnected abruptly or pipe broken; continue server loop
                        }
                    }
                    catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        if (_cts.Token.IsCancellationRequested) break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch
                    {
                        try { await Task.Delay(200, _cts.Token); } catch { break; }
                    }
                }
            }

            public void Dispose()
            {
                if (_isDisposed) return;
                _isDisposed = true;
                try { _cts.Cancel(); } catch { }
            }
        }

        public static async Task<bool> SendMessageWithVerificationAsync(string message, int timeoutMs = 2000, string? pipeName = null)
        {
            if (string.IsNullOrEmpty(message)) return false;
            timeoutMs = Math.Max(100, timeoutMs);
            string sanitizedMessage = message.Replace("\r", "").Replace("\n", " ");
            string targetPipe = pipeName ?? PipeName;

            try
            {
                // Explicitly specify TokenImpersonationLevel.None to prevent server from impersonating elevated client token
                using var pipe = new NamedPipeClientStream(".", targetPipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.None);
                using var cts = new CancellationTokenSource(timeoutMs);

                await pipe.ConnectAsync(cts.Token);

                // Anti-Spoofing check
                if (!IsTrustedServer(pipe))
                {
                    throw new UnauthorizedAccessException("Named pipe server is not running under LocalSystem or Administrators.");
                }

                using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync(sanitizedMessage.AsMemory(), cts.Token);
                await writer.FlushAsync(cts.Token);
                try
                {
                    var drainTask = Task.Run(() => { try { pipe.WaitForPipeDrain(); } catch { } });
                    await Task.WhenAny(drainTask, Task.Delay(1500, cts.Token));
                }
                catch { }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
