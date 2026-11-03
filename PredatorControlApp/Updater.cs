using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PredatorControlApp
{
    internal sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, string? ExpectedSha256 = null);

    [SupportedOSPlatform("windows")]
    internal static class Updater
    {
        internal const string ReleasesApi = "https://api.github.com/repos/red16124724/Predator-Control-App/releases";
        internal const string ReleasesWeb = "https://github.com/red16124724/Predator-Control-App/releases";
        private const string RegPath = @"SOFTWARE\PredatorControl";
        private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

        #region Version

        internal static Version Current => Norm(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

        internal static string CurrentText => Current.ToString(3);

        private static Version Norm(Version v) =>
            v.Revision > 0
                ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), v.Revision)
                : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

        internal static bool TryParseNormalizedVersion(string? rawVersionOrTag, out Version normalizedVersion)
        {
            normalizedVersion = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(rawVersionOrTag)) return false;

            string clean = rawVersionOrTag.Trim();
            if (clean.StartsWith("v.", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(2);
            else if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(1);

            // Strip SemVer build metadata (+...) and pre-release tag (-...)
            int metaIdx = clean.IndexOf('+');
            if (metaIdx >= 0) clean = clean.Substring(0, metaIdx);
            int prereleaseIdx = clean.IndexOf('-');
            if (prereleaseIdx >= 0) clean = clean.Substring(0, prereleaseIdx);

            clean = clean.Trim();
            if (!Version.TryParse(clean, out var parsed)) return false;

            normalizedVersion = Norm(parsed);
            return true;
        }

        internal static bool IsRemoteVersionNewer(Version remote, Version local)
        {
            int rMajor = Math.Max(remote.Major, 0), lMajor = Math.Max(local.Major, 0);
            if (rMajor != lMajor) return rMajor > lMajor;
            int rMinor = Math.Max(remote.Minor, 0), lMinor = Math.Max(local.Minor, 0);
            if (rMinor != lMinor) return rMinor > lMinor;
            int rBuild = Math.Max(remote.Build, 0), lBuild = Math.Max(local.Build, 0);
            if (rBuild != lBuild) return rBuild > lBuild;
            int rRev = Math.Max(remote.Revision, 0), lRev = Math.Max(local.Revision, 0);
            return rRev > lRev;
        }

        internal static bool IsRemoteVersionNewer(string? remoteTag, Version? localAssemblyVersion)
        {
            if (!TryParseNormalizedVersion(remoteTag, out var remote)) return false;
            var local = localAssemblyVersion != null ? Norm(localAssemblyVersion) : Current;
            return IsRemoteVersionNewer(remote, local);
        }

        internal static bool TryParseTag(string tag, out Version v)
        {
            return TryParseNormalizedVersion(tag, out v);
        }

        #endregion

        #region Check

        internal static async Task ScheduleStartupCheckAsync(Action check, TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested) check();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        internal static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
        {
            const int maxRetries = 3;
            TimeSpan[] retryDelays = { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12) };

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return await CheckInternalAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < maxRetries && !cancellationToken.IsCancellationRequested && IsTransientNetworkException(ex))
                {
                    await Task.Delay(retryDelays[attempt], cancellationToken).ConfigureAwait(false);
                }
            }

            return null;
        }

        internal static bool IsTransientNetworkException(Exception ex) =>
            ex is HttpRequestException or TimeoutException or System.Net.Sockets.SocketException or TaskCanceledException ||
            (ex.InnerException != null && IsTransientNetworkException(ex.InnerException));

        private static async Task<UpdateInfo?> CheckInternalAsync(CancellationToken cancellationToken)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"PredatorControlApp-Updater/{CurrentText}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(ReleasesApi, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Network timeout while checking for updates. Please check your internet connection.");
            }

            using (response)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new HttpRequestException("GitHub API rate limit reached (60 requests/hr). Please try again later or visit the releases page.", null, System.Net.HttpStatusCode.Forbidden);
                }
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    throw new HttpRequestException("GitHub releases repository not found or unreachable.", null, System.Net.HttpStatusCode.NotFound);
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"GitHub API returned status {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
                }

                var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(jsonString)) return null;

                using var doc = JsonDocument.Parse(jsonString);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

                var current = Current;
                UpdateInfo? newest = null;
                var notes = new StringBuilder();

                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (Flag(rel, "draft") || Flag(rel, "prerelease")) continue;
                    if (!TryParseNormalizedVersion(Str(rel, "tag_name"), out var v) || !IsRemoteVersionNewer(v, current)) continue;

                    string relName = Str(rel, "name", "v" + v.ToString(3));
                    string relBody = Str(rel, "body").Trim().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                    notes.AppendLine("--- " + relName + " ---").AppendLine(relBody).AppendLine();

                    if (newest == null || IsRemoteVersionNewer(v, newest.Version))
                    {
                        string url = PickAsset(rel, IsSelfContained) ?? Str(rel, "html_url", ReleasesWeb);
                        string? sha256 = ExtractExpectedSha256(rel, url);
                        newest = new UpdateInfo(v, Str(rel, "tag_name"), "", url, sha256);
                    }
                }

                return newest == null ? null : newest with { Notes = notes.ToString().TrimEnd() };
            }
        }

        internal static string? ExtractExpectedSha256(JsonElement rel, string? targetUrl = null)
        {
            if (!string.IsNullOrEmpty(targetUrl) && rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!string.Equals(Str(asset, "browser_download_url"), targetUrl, StringComparison.Ordinal)) continue;
                    string digest = Str(asset, "digest");
                    if (Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z", RegexOptions.IgnoreCase))
                        return digest.Substring(7).ToLowerInvariant();
                }
            }
            string body = Str(rel, "body");
            if (string.IsNullOrEmpty(body)) return null;

            // If a specific asset name is identifiable from the download url, try matching that asset's hash first
            if (!string.IsNullOrEmpty(targetUrl))
            {
                if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsedUri) ||
                    Uri.TryCreate(targetUrl, UriKind.Relative, out parsedUri))
                {
                    string localPath = parsedUri.IsAbsoluteUri ? parsedUri.LocalPath : targetUrl;
                    string assetName = Path.GetFileName(localPath);
                    if (!string.IsNullOrEmpty(assetName))
                    {
                        string escaped = Regex.Escape(assetName);
                        // Pattern 1: assetName: <hash> or assetName = <hash> or | assetName | <hash> |
                        var m1 = Regex.Match(body, @"(?<![a-zA-Z0-9._-])" + escaped + @"(?:`|\*)*[ \t:=|]+(?:`|\*)*([a-fA-F0-9]{64})(?![a-fA-F0-9])", RegexOptions.IgnoreCase);
                        if (m1.Success) return m1.Groups[1].Value.ToLowerInvariant();

                        // Pattern 2: <hash>  assetName or <hash> *assetName (standard sha256sum format) or | <hash> | assetName |
                        var m2 = Regex.Match(body, @"(?<![a-fA-F0-9])([a-fA-F0-9]{64})(?:`|\*)*[ \t*|]+(?:`|\*)*" + escaped + @"(?![a-zA-Z0-9._-])", RegexOptions.IgnoreCase);
                        if (m2.Success) return m2.Groups[1].Value.ToLowerInvariant();
                    }
                }
            }

            // A generic digest cannot safely be associated with a specific release asset.
            if (!string.IsNullOrEmpty(targetUrl)) return null;

            // Pattern 3: Explicit SHA256: <hash>
            var match = Regex.Match(body, @"(?:SHA-?256|checksum)[\s:=|]+(?:`|\*)*([a-fA-F0-9]{64})", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.ToLowerInvariant();
            return null;
        }

        private static bool Flag(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

        private static string Str(JsonElement e, string name, string fallback = "") =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : fallback;

        private static bool IsSelfContained =>
            !RuntimeEnvironment.GetRuntimeDirectory().Replace('/', '\\').Contains(@"\dotnet\shared\", OIC);

        internal static string? PickAsset(JsonElement rel, bool wantSelfContained)
        {
            if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

            string? fallback = null;
            foreach (var a in assets.EnumerateArray())
            {
                string name = Str(a, "name");
                if (!name.EndsWith(".exe", OIC)) continue;

                string url = Str(a, "browser_download_url");
                if (url.Length == 0) continue;

                bool selfContained = name.Contains("standalone", OIC) || name.Contains("self", OIC);
                if (selfContained == wantSelfContained) return url;
                fallback ??= url;
            }
            return fallback;
        }

        #endregion

        #region Apply

        private static bool IsAllowedDownloadHost(Uri uri) =>
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

        internal static async Task ApplyAsync(UpdateInfo info, CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new System.Security.SecurityException("Download URL is not a recognized secure HTTPS endpoint.");
            }

            bool isAllowedHost = IsAllowedDownloadHost(uri);

            if (!isAllowedHost)
            {
                throw new System.Security.SecurityException("Download URL host is not an authorized GitHub domain.");
            }

            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                if (!uri.AbsolutePath.StartsWith("/red16124724/Predator-Control-App/releases/download/", StringComparison.OrdinalIgnoreCase) &&
                    !uri.AbsolutePath.StartsWith("/red16124724/Predator-Control-App/releases/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new System.Security.SecurityException("Download URL points outside the official repository releases.");
                }
            }

            if (!info.DownloadUrl.EndsWith(".exe", OIC))
            {
                Process.Start(new ProcessStartInfo(info.DownloadUrl) { UseShellExecute = true });
                return;
            }

            if (string.IsNullOrWhiteSpace(info.ExpectedSha256) ||
                !Regex.IsMatch(info.ExpectedSha256.Trim(), @"\A[a-fA-F0-9]{64}\z"))
                throw new System.Security.SecurityException("This release does not provide an asset-specific SHA-256 checksum. Download it manually from the releases page.");

            string target = Environment.ProcessPath ?? Application.ExecutablePath;
            target = Path.GetFullPath(target);

            // Validation: target path must not contain quotes, newlines, or command injection characters
            if (target.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                target.Contains('"') || target.Contains('\r') || target.Contains('\n') ||
                target.Contains('&') || target.Contains('^') || target.Contains('%') || target.Contains('!') || target.Contains('`'))
            {
                throw new System.Security.SecurityException("Invalid characters detected in target executable path.");
            }

            string tempDir = Path.GetTempPath();
            var tempDirInfo = new DirectoryInfo(tempDir);
            if (tempDirInfo.Exists && (tempDirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new System.Security.SecurityException("Insecure temporary directory junction detected.");
            }

            string staged = Path.GetFullPath(Path.Combine(tempDir, $"PredatorControl-update-{Guid.NewGuid():N}.exe"));
            string script = Path.GetFullPath(Path.Combine(tempDir, $"PredatorControl-update-{Guid.NewGuid():N}.cmd"));

            try
            {
                string computedHashHex;
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("PredatorControl");
                    using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    var finalUri = response.RequestMessage?.RequestUri;
                    if (finalUri != null && (finalUri.Scheme != Uri.UriSchemeHttps || !IsAllowedDownloadHost(finalUri)))
                    {
                        throw new System.Security.SecurityException("Download was redirected to an untrusted domain or non-HTTPS endpoint.");
                    }
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        throw new HttpRequestException("GitHub download rate limit exceeded. Please try again later.", null, System.Net.HttpStatusCode.Forbidden);
                    }
                    response.EnsureSuccessStatusCode();

                    using var src = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var dst = File.Create(staged);
                    using var incHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    byte[] buffer = new byte[81920];
                    int bytesRead;
                    long totalBytes = 0;
                    const long maxAllowedBytes = 250 * 1024 * 1024; // 250 MB ceiling to prevent unbounded stream DoS

                    while (true)
                    {
                        using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        chunkCts.CancelAfter(TimeSpan.FromSeconds(45));

                        bytesRead = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), chunkCts.Token);
                        if (bytesRead <= 0) break;

                        totalBytes += bytesRead;
                        if (totalBytes > maxAllowedBytes)
                        {
                            try { File.Delete(staged); } catch { }
                            throw new InvalidOperationException("Download exceeded maximum allowed size threshold.");
                        }
                        await dst.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        incHash.AppendData(buffer, 0, bytesRead);
                    }
                    computedHashHex = Convert.ToHexString(incHash.GetHashAndReset()).ToLowerInvariant();
                }

                if (new FileInfo(staged).Length < 100_000)
                {
                    try { File.Delete(staged); } catch { }
                    throw new IOException("Downloaded file looks truncated.");
                }

                if (!string.IsNullOrEmpty(info.ExpectedSha256))
                {
                    string expected = info.ExpectedSha256.Trim().ToLowerInvariant();
                    if (!Regex.IsMatch(expected, "^[a-f0-9]{64}$"))
                    {
                        try { File.Delete(staged); } catch { }
                        throw new System.Security.SecurityException($"Invalid expected SHA256 format: '{info.ExpectedSha256}'.");
                    }
                    if (!string.Equals(computedHashHex, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(staged); } catch { }
                        throw new System.Security.SecurityException($"SHA256 verification failed! Expected {expected}, got {computedHashHex}");
                    }
                }

                try
                {
                    using var key = RegistrySafety.CreateSafeSubKey(Registry.CurrentUser, RegPath);
                    if (key != null)
                    {
                        key.SetValue("UpdateNotes", info.Notes);
                        key.SetValue("UpdateNotesVersion", info.Version.ToString(3));
                        key.Flush();
                    }
                }
                catch { }

                int pid = Environment.ProcessId;

                // The script receives paths as arguments; the launcher rejects command metacharacters.
                string scriptContent = """
                    @echo off
                    chcp 65001 >nul
                    set "STAGED=%~1"
                    set "TARGET=%~2"
                    set "PID=%~3"
                    :wait
                    "%SystemRoot%\System32\tasklist.exe" /fi "PID eq %PID%" /nh | "%SystemRoot%\System32\findstr.exe" /r "\<%PID%\>" >nul
                    if not errorlevel 1 (
                        "%SystemRoot%\System32\timeout.exe" /t 1 /nobreak >nul
                        goto wait
                    )
                    set retries=0
                    copy /y "%TARGET%" "%TARGET%.previous.exe" >nul 2>&1
                    if errorlevel 1 goto restart_orig
                    :trymove
                    move /y "%STAGED%" "%TARGET%" >nul 2>&1
                    if not errorlevel 1 goto launch
                    set /a retries+=1
                    if %retries% geq 30 goto restart_orig
                    "%SystemRoot%\System32\timeout.exe" /t 1 /nobreak >nul
                    goto trymove
                    :launch
                    start "" "%TARGET%"
                    goto cleanup
                    :restart_orig
                    start "" "%TARGET%"
                    :cleanup
                    if exist "%STAGED%" del /f /q "%STAGED%" >nul 2>&1
                    (goto) 2>nul & del "%~f0"
                    """;

                File.WriteAllText(script, scriptContent, new UTF8Encoding(false));

                var psi = CreateUpdateProcessStartInfo(script, staged, target, pid);
                if (Process.Start(psi) == null)
                    throw new IOException("Could not start the update process.");
            }
            catch
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                try { if (File.Exists(script)) File.Delete(script); } catch { }
                throw;
            }
        }

        internal static ProcessStartInfo CreateUpdateProcessStartInfo(string script, string staged, string target, int pid)
        {
            char[] unsafeCharacters = { '"', '\r', '\n', '&', '|', '<', '>', '^', '%', '!', '`' };
            foreach (string path in new[] { script, staged, target })
            {
                if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(unsafeCharacters) >= 0 ||
                    path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    throw new System.Security.SecurityException("Invalid characters in update script or executable path.");
            }
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Environment.SystemDirectory,
                Arguments = $"/d /s /c \"\"{script}\" \"{staged}\" \"{target}\" {pid}\""
            };
            return psi;
        }

        internal static string ComputeFileSha256(string filePath)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(filePath);
            byte[] hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        internal static bool VerifySha256(string filePath, string expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash) || !File.Exists(filePath)) return false;
            string actual = ComputeFileSha256(filePath);
            return string.Equals(actual, expectedHash.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }

        internal static void CleanStaleUpdateArtifacts()
        {
            try
            {
                string tempDir = Path.GetTempPath();
                string staged = Path.Combine(tempDir, "update_staged.exe");
                if (File.Exists(staged)) File.Delete(staged);
                string script = Path.Combine(tempDir, "apply_update.cmd");
                if (File.Exists(script)) File.Delete(script);

                var dir = new DirectoryInfo(tempDir);
                if (dir.Exists)
                {
                    foreach (var file in dir.EnumerateFiles("PredatorControl-update-*.*"))
                    {
                        try
                        {
                            if (DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromMinutes(30))
                            {
                                file.Delete();
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        #endregion

        #region Post-update notes

        internal static void ShowPendingNotes(IWin32Window owner)
        {
            string? notes, version;
            try
            {
                using var key = RegistrySafety.CreateSafeSubKey(Registry.CurrentUser, RegPath);
                if (key == null) return;
                notes = key.GetValue("UpdateNotes") as string;
                version = key.GetValue("UpdateNotesVersion") as string;
                if (string.IsNullOrWhiteSpace(notes)) return;
                key.DeleteValue("UpdateNotes", false);
                key.DeleteValue("UpdateNotesVersion", false);
                key.Flush();
            }
            catch { return; }

            if (TryParseNormalizedVersion(version, out var v) && IsRemoteVersionNewer(v, Current))
            {
                MessageBox.Show(owner, $"The update to v{version} could not be installed - you're still on v{CurrentText}.\n\n" +
                    "This usually means the app file was locked or write-protected. Try again, or download it manually from GitHub.",
                    "Predator Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ShowNotes(owner, $"Updated to v{version}", "What's new", notes!, confirm: false);
        }

        #endregion

        #region Dialog

        private static readonly Color FormBg = Color.FromArgb(22, 22, 26);
        private static readonly Color TitleBarBg = Color.FromArgb(18, 18, 21);
        private static readonly Color PanelBg = Color.FromArgb(30, 30, 34);
        private static readonly Color SeparatorColor = Color.FromArgb(40, 40, 44);
        private static readonly Color TitleTextColor = Color.FromArgb(200, 200, 205);
        private static readonly Color CloseHoverColor = Color.FromArgb(220, 50, 50);
        private static readonly Color BodyColor = Color.FromArgb(165, 165, 175);
        private static readonly Color TextColor = Color.FromArgb(210, 210, 215);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private static void Drag(Form dlg, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(dlg.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        }

        internal static string Plain(string markdown)
        {
            var sb = new StringBuilder();
            bool blank = false;

            foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();

                if (line.Length == 0)
                {
                    blank = sb.Length > 0;
                    continue;
                }

                if (blank) sb.AppendLine();
                blank = false;

                line = Regex.Replace(line, @"^\s{0,3}#{1,6}\s*", "");
                line = Regex.Replace(line, @"^\s*[-*+]\s+", "\u2022 ");
                line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
                line = Regex.Replace(line, @"`([^`]*)`", "$1");
                line = line.Replace("**", "").Replace("__", "");

                sb.AppendLine(line);
            }

            return sb.ToString().Trim();
        }

        internal static bool ShowNotes(IWin32Window owner, string title, string subtitle, string notes, bool confirm)
        {
            float dpi = (owner as Control)?.DeviceDpi > 0 ? (owner as Control)!.DeviceDpi / 96f : 1f;
            int S(int px) => px == 0 ? 0 : Math.Max(1, (int)Math.Round(px * dpi));

            int W = S(540), Bar = S(38), Pad = S(20), BtnW = S(125), BtnH = S(36), BodyH = S(300);
            int bodyY = Bar + S(72);
            int btnY = bodyY + BodyH + Pad;

            using var fontTitle = new Font("Segoe UI", 10f, FontStyle.Bold);
            using var fontClose = new Font("Segoe UI", 10f);
            using var fontSubtitle = new Font("Segoe UI", 10f, FontStyle.Bold);
            using var fontBody = new Font("Segoe UI", 9.5f);

            using var dlg = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                KeyPreview = true,
                BackColor = FormBg,
                ClientSize = new Size(W, btnY + BtnH + Pad),
                DialogResult = DialogResult.Cancel
            };
            try { dlg.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            dlg.Paint += (s, e) =>
            {
                using var p = new Pen(SeparatorColor, 1f);
                if (dlg.Width > 1 && dlg.Height > 1)
                    e.Graphics.DrawRectangle(p, 0, 0, dlg.Width - 1, dlg.Height - 1);
            };

            var bar = new Panel { Location = Point.Empty, Size = new Size(W, Bar), BackColor = TitleBarBg };
            bar.MouseDown += (s, e) => Drag(dlg, e);
            dlg.Controls.Add(bar);

            var lblTitle = new Label
            {
                Text = title,
                Location = new Point(S(14), Math.Max(0, (Bar - S(20)) / 2)),
                AutoSize = true,
                Font = fontTitle,
                ForeColor = TitleTextColor,
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
            lblTitle.MouseDown += (s, e) => Drag(dlg, e);
            bar.Controls.Add(lblTitle);

            var lblClose = new Label
            {
                Text = "\u2715",
                Location = new Point(W - Bar, 0),
                Size = new Size(Bar, Bar),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = fontClose,
                ForeColor = TitleTextColor,
                Cursor = Cursors.Hand,
                UseMnemonic = false
            };
            lblClose.MouseEnter += (s, e) => lblClose.ForeColor = CloseHoverColor;
            lblClose.MouseLeave += (s, e) => lblClose.ForeColor = TitleTextColor;
            lblClose.Click += (s, e) => dlg.Close();
            bar.Controls.Add(lblClose);

            dlg.Controls.Add(new Panel
            {
                Location = new Point(0, Bar),
                Size = new Size(W, 1),
                BackColor = SeparatorColor
            });

            dlg.Controls.Add(new Label
            {
                Text = subtitle,
                Location = new Point(Pad, Bar + S(14)),
                Size = new Size(W - Pad * 2, S(48)),
                Font = fontSubtitle,
                ForeColor = TextColor,
                BackColor = Color.Transparent,
                UseMnemonic = false
            });

            var txt = new TextBox
            {
                Text = Plain(notes),
                Location = new Point(Pad, bodyY),
                Size = new Size(W - Pad * 2, BodyH),
                Multiline = true,
                ReadOnly = true,
                TabStop = false,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = PanelBg,
                ForeColor = BodyColor,
                Font = fontBody
            };
            dlg.Controls.Add(txt);

            var btnPrimary = new PredatorButton
            {
                Text = confirm ? "Update Now" : "Close",
                Location = new Point(W - Pad - BtnW, btnY),
                Size = new Size(BtnW, BtnH)
            };
            btnPrimary.Click += (s, e) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
            dlg.Controls.Add(btnPrimary);

            if (confirm)
            {
                var btnLater = new PredatorButton
                {
                    Text = "Later",
                    Location = new Point(W - Pad - BtnW * 2 - S(12), btnY),
                    Size = new Size(BtnW, BtnH)
                };
                btnLater.Click += (s, e) => dlg.Close();
                dlg.Controls.Add(btnLater);
            }

            dlg.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) dlg.Close(); };
            dlg.Shown += (s, e) =>
            {
                txt.Select(0, 0);
                dlg.ActiveControl = btnPrimary;
            };

            return dlg.ShowDialog(owner) == DialogResult.OK;
        }

        #endregion
    }
}
