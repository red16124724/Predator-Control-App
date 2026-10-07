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

        private static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

        internal static bool TryParseTag(string tag, out Version v)
        {
            v = new Version(0, 0, 0);
            if (string.IsNullOrEmpty(tag)) return false;
            string clean = tag.Trim();
            if (clean.StartsWith("v.", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(2);
            else if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(1);

            if (!Version.TryParse(clean.Trim(), out var parsed)) return false;
            v = Norm(parsed);
            return true;
        }

        #endregion

        #region Check

        internal static async Task<UpdateInfo?> CheckAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PredatorControl");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var doc = JsonDocument.Parse(await http.GetStringAsync(ReleasesApi));

            var current = Current;
            UpdateInfo? newest = null;
            var notes = new StringBuilder();

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (Flag(rel, "draft") || Flag(rel, "prerelease")) continue;
                if (!TryParseTag(Str(rel, "tag_name"), out var v) || v <= current) continue;

                notes.AppendLine($"--- {Str(rel, "name", $"v{v.ToString(3)}")} ---")
                     .AppendLine(Str(rel, "body").Trim().Replace("\r\n", "\n").Replace("\n", Environment.NewLine))
                     .AppendLine();

                if (newest == null || v > newest.Version)
                {
                    string url = PickAsset(rel, IsSelfContained) ?? Str(rel, "html_url", ReleasesWeb);
                    string? sha256 = ExtractExpectedSha256(rel, url);
                    newest = new UpdateInfo(v, Str(rel, "tag_name"), "", url, sha256);
                }
            }

            return newest == null ? null : newest with { Notes = notes.ToString().TrimEnd() };
        }

        internal static string? ExtractExpectedSha256(JsonElement rel, string? targetUrl = null)
        {
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
                        var assetMatch = Regex.Match(body, Regex.Escape(assetName) + @"[\s:=]+([a-fA-F0-9]{64})", RegexOptions.IgnoreCase);
                        if (assetMatch.Success) return assetMatch.Groups[1].Value.ToLowerInvariant();
                    }
                }
            }

            var match = Regex.Match(body, @"(?:SHA-?256|checksum)[\s:=]+([a-fA-F0-9]{64})", RegexOptions.IgnoreCase);
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

        internal static async Task ApplyAsync(UpdateInfo info)
        {
            if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            {
                throw new System.Security.SecurityException("Download URL is not a recognized GitHub HTTPS endpoint.");
            }

            if (!info.DownloadUrl.EndsWith(".exe", OIC))
            {
                Process.Start(new ProcessStartInfo(info.DownloadUrl) { UseShellExecute = true });
                return;
            }

            string target = Environment.ProcessPath ?? Application.ExecutablePath;
            target = Path.GetFullPath(target);

            // Validation: target path must not contain quotes, newlines, or invalid path characters to prevent command injection
            if (target.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || target.Contains('"') || target.Contains('\r') || target.Contains('\n'))
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
                    using var src = await http.GetStreamAsync(info.DownloadUrl);
                    using var dst = File.Create(staged);
                    using var incHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    byte[] buffer = new byte[81920];
                    int bytesRead;
                    long totalBytes = 0;
                    const long maxAllowedBytes = 250 * 1024 * 1024; // 250 MB ceiling to prevent unbounded stream DoS

                    while ((bytesRead = await src.ReadAsync(buffer)) > 0)
                    {
                        totalBytes += bytesRead;
                        if (totalBytes > maxAllowedBytes)
                        {
                            try { File.Delete(staged); } catch { }
                            throw new InvalidOperationException("Download exceeded maximum allowed size threshold.");
                        }
                        await dst.WriteAsync(buffer.AsMemory(0, bytesRead));
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
                    if (!string.Equals(computedHashHex, info.ExpectedSha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(staged); } catch { }
                        throw new System.Security.SecurityException($"SHA256 verification failed! Expected {info.ExpectedSha256}, got {computedHashHex}");
                    }
                }

                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(RegPath);
                    key.SetValue("UpdateNotes", info.Notes);
                    key.SetValue("UpdateNotesVersion", info.Version.ToString(3));
                }
                catch { }

                int pid = Environment.ProcessId;

                // Completely static parameterized batch script. NO string interpolation of paths or PID!
                // All values (%~1, %~2, %~3) are passed safely as separate arguments via ProcessStartInfo.ArgumentList.
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
                    :trymove
                    move /y "%STAGED%" "%TARGET%" >nul 2>&1
                    if not errorlevel 1 goto launch
                    set /a retries+=1
                    if %retries% geq 30 goto cleanup
                    "%SystemRoot%\System32\timeout.exe" /t 1 /nobreak >nul
                    goto trymove
                    :launch
                    start "" "%TARGET%"
                    :cleanup
                    if exist "%STAGED%" del /f /q "%STAGED%" >nul 2>&1
                    (goto) 2>nul & del "%~f0"
                    """;

                File.WriteAllText(script, scriptContent, new UTF8Encoding(false));

                string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                var psi = new ProcessStartInfo(cmdPath)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Environment.SystemDirectory
                };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add(staged);
                psi.ArgumentList.Add(target);
                psi.ArgumentList.Add(pid.ToString());

                Process.Start(psi);
            }
            catch
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                try { if (File.Exists(script)) File.Delete(script); } catch { }
                throw;
            }
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
                using var key = Registry.CurrentUser.CreateSubKey(RegPath);
                notes = key.GetValue("UpdateNotes") as string;
                version = key.GetValue("UpdateNotesVersion") as string;
                if (string.IsNullOrWhiteSpace(notes)) return;
                key.DeleteValue("UpdateNotes", false);
                key.DeleteValue("UpdateNotesVersion", false);
            }
            catch { return; }

            if (Version.TryParse(version, out var v) && Norm(v) > Current)
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
            const int W = 520, Bar = 36, Pad = 20, BtnW = 120, BtnH = 34, BodyH = 300;
            int bodyY = Bar + 72;
            int btnY = bodyY + BodyH + Pad;

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

            var bar = new Panel { Location = Point.Empty, Size = new Size(W, Bar), BackColor = TitleBarBg };
            bar.MouseDown += (s, e) => Drag(dlg, e);
            dlg.Controls.Add(bar);

            var lblTitle = new Label
            {
                Text = title,
                Location = new Point(14, 9),
                AutoSize = true,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
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
                Font = new Font("Segoe UI", 10f),
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
                Location = new Point(Pad, Bar + 16),
                Size = new Size(W - Pad * 2, 48),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
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
                Font = new Font("Segoe UI", 9.5f)
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
                    Location = new Point(W - Pad - BtnW * 2 - 12, btnY),
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
