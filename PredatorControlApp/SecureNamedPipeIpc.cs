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

            using var currentIdentity = WindowsIdentity.GetCurrent();
            var currentUser = currentIdentity.User;
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
            sec.AddAccessRule(new PipeAccessRule(authUsers, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize | PipeAccessRights.ReadPermissions, AccessControlType.Allow));
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

                using var currentIdentity = WindowsIdentity.GetCurrent();
                var currentUser = currentIdentity.User;
                return currentUser != null && owner.Equals(currentUser);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsTrustedClient(NamedPipeServerStream pipe)
        {
            try
            {
                SecurityIdentifier? clientSid = null;
                pipe.RunAsClient(() =>
                {
                    using var id = WindowsIdentity.GetCurrent();
                    clientSid = id.User;
                });

                if (clientSid == null) return false;

                if (clientSid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                    clientSid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                {
                    return true;
                }

                using var serverIdentity = WindowsIdentity.GetCurrent();
                return serverIdentity.User != null && clientSid.Equals(serverIdentity.User);
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
                    Task.Run(RunServerLoopAsync).ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }

            private async Task RunServerLoopAsync()
            {
                try
                {
                    while (!_isDisposed)
                    {
                        CancellationToken token;
                        try
                        {
                            lock (_startLock)
                            {
                                if (_isDisposed || _cts.IsCancellationRequested) break;
                                token = _cts.Token;
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }

                    NamedPipeServerStream? pipe = null;
                    try
                    {
                        pipe = NamedPipeServerStreamAcl.Create(
                            _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 4096, outBufferSize: 4096, CreatePipeSecurity());

                        await pipe.WaitForConnectionAsync(token);

                        var connectedPipe = pipe;
                        pipe = null; // Ownership transferred to client task

                        _ = Task.Run(async () =>
                        {
                            using (connectedPipe)
                            {
                                if (!IsTrustedClient(connectedPipe))
                                {
                                    try { connectedPipe.Disconnect(); } catch { }
                                    return;
                                }

                                try
                                {
                                    using var reader = new StreamReader(connectedPipe, Encoding.UTF8);
                                    CancellationToken clientToken;
                                    try { clientToken = _cts.Token; }
                                    catch (ObjectDisposedException) { return; }
                                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(clientToken);
                                    readCts.CancelAfter(2000);
                                    try
                                    {
                                        // Bounded chunked message reading to prevent unbounded memory allocation DoS attacks
                                        var sb = new StringBuilder();
                                        char[] charBuf = new char[64];
                                        bool newlineReceived = false;
                                        while (sb.Length < MaxMessageLength)
                                        {
                                            int toRead = Math.Min(charBuf.Length, MaxMessageLength - sb.Length);
                                            int readCount = await reader.ReadAsync(charBuf.AsMemory(0, toRead), readCts.Token);
                                            if (readCount <= 0) break;
                                            for (int i = 0; i < readCount; i++)
                                            {
                                                char c = charBuf[i];
                                                if (c == '\n') { newlineReceived = true; break; }
                                                if (c != '\r') sb.Append(c);
                                            }
                                            if (newlineReceived) break;
                                        }

                                        if (newlineReceived)
                                        {
                                            string line = sb.ToString().Replace(" ", "").Trim();
                                            if (!string.IsNullOrEmpty(line))
                                            {
                                                try { _onMessage(line); } catch { }
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                catch { }
                                finally
                                {
                                    try { connectedPipe.Disconnect(); } catch { }
                                }
                            }
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        pipe?.Dispose();
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        pipe?.Dispose();
                        break;
                    }
                    catch
                    {
                        pipe?.Dispose();
                        try
                        {
                            if (!_isDisposed)
                                await Task.Delay(200, token);
                            else
                                break;
                        }
                        catch { break; }
                    }
                }
                }
                finally
                {
                    try { _cts.Dispose(); } catch { }
                }
            }

            public void Dispose()
            {
                lock (_startLock)
                {
                    if (_isDisposed) return;
                    _isDisposed = true;
                }
                try { _cts.Cancel(); } catch { }
            }
        }

        public static async Task<bool> SendMessageWithVerificationAsync(string message, int timeoutMs = 2000, string? pipeName = null)
        {
            if (string.IsNullOrEmpty(message)) return false;
            timeoutMs = Math.Max(100, timeoutMs);
            // Message shaping (control-character stripping and bounded length)
            // is owned by the native core; fall back locally only if it is absent.
            string sanitizedMessage = PredatorNativeMigrations.SanitizePipeMessage(message)
                ?? message.Replace("\r", "").Replace("\n", " ");
            if (sanitizedMessage.Length > MaxMessageLength - 2)
            {
                sanitizedMessage = sanitizedMessage.Substring(0, MaxMessageLength - 2);
            }
            if (sanitizedMessage.Length == 0) return false;
            string targetPipe = pipeName ?? PipeName;

            try
            {
                // Explicitly specify TokenImpersonationLevel.Identification to prevent server from impersonating elevated client token
                using var pipe = new NamedPipeClientStream(".", targetPipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
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
                    if (pipe.IsConnected)
                    {
                        var drainTask = Task.Run(() =>
                        {
                            try { if (pipe.IsConnected) pipe.WaitForPipeDrain(); } catch { }
                        });
                        var completed = await Task.WhenAny(drainTask, Task.Delay(1500, cts.Token));
                        if (completed == drainTask)
                        {
                            await drainTask;
                        }
                    }
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
