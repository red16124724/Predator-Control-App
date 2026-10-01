using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PredatorControlApp
{
    public static class SecureNamedPipeIpc
    {
        public const string PipeName = "PredatorControlPipe";

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
            private bool _isDisposed;

            public PipeServer(Action<string> onMessage)
            {
                _onMessage = onMessage;
            }

            public void Start()
            {
                Task.Run(RunServerLoopAsync);
            }

            private async Task RunServerLoopAsync()
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = NamedPipeServerStreamAcl.Create(
                            PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreatePipeSecurity());

                        await pipe.WaitForConnectionAsync(_cts.Token);

                        using var reader = new StreamReader(pipe, Encoding.UTF8);
                        string? line = await reader.ReadLineAsync(_cts.Token);
                        if (!string.IsNullOrEmpty(line))
                        {
                            _onMessage(line);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        try { await Task.Delay(1000, _cts.Token); } catch { break; }
                    }
                }
            }

            public void Dispose()
            {
                if (_isDisposed) return;
                _isDisposed = true;
                _cts.Cancel();
                _cts.Dispose();
            }
        }

        public static async Task<bool> SendMessageWithVerificationAsync(string message, int timeoutMs = 2000)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var cts = new CancellationTokenSource(timeoutMs);

                await pipe.ConnectAsync(cts.Token);

                // Anti-Spoofing check
                if (!IsTrustedServer(pipe))
                {
                    throw new UnauthorizedAccessException("Named pipe server is not running under LocalSystem or Administrators.");
                }

                using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync(message);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
