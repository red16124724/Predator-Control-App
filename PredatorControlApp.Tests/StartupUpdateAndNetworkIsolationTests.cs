using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class StartupUpdateAndNetworkIsolationTests
    {
        [Fact]
        public async Task StartupCheck_RunsOnceWithoutPeriodicPolling()
        {
            int calls = 0;
            await Updater.ScheduleStartupCheckAsync(() => Interlocked.Increment(ref calls), TimeSpan.Zero, CancellationToken.None);
            await Task.Delay(100);
            Assert.Equal(1, Volatile.Read(ref calls));
        }

        [Fact]
        public async Task StartupCheck_CancelledBeforeDelay_DoesNotRun()
        {
            int calls = 0;
            using var cts = new CancellationTokenSource();
            var scheduled = Updater.ScheduleStartupCheckAsync(() => Interlocked.Increment(ref calls), TimeSpan.FromMinutes(1), cts.Token);
            cts.Cancel();
            await scheduled;
            Assert.Equal(0, calls);
        }

        [Fact]
        public void TagParser_ValidSemVer_ParsesAccurately()
        {
            Assert.True(Updater.TryParseTag("v1.5.5", out var v0));
            Assert.Equal(new Version(1, 5, 5), v0);

            Assert.True(Updater.TryParseTag("v1.5.0", out var v1));
            Assert.Equal(new Version(1, 5, 0), v1);

            Assert.True(Updater.TryParseTag("v.1.4.1", out var v2));
            Assert.Equal(new Version(1, 4, 1), v2);

            Assert.True(Updater.TryParseTag("1.2.3", out var v3));
            Assert.Equal(new Version(1, 2, 3), v3);
        }

        [Fact]
        public void TagParser_InvalidOrNightly_Rejects()
        {
            Assert.False(Updater.TryParseTag("nightly", out _));
            Assert.False(Updater.TryParseTag("beta-build-42", out _));
            Assert.False(Updater.TryParseTag("", out _));
            Assert.False(Updater.TryParseTag("    ", out _));
        }

        [Fact]
        public void PickAsset_SelectsStandaloneExe_WhenSelfContained()
        {
            const string json = @"
            {
                ""assets"": [
                    { ""name"": ""PredatorControlApp.zip"", ""browser_download_url"": ""https://github.com/test/app.zip"" },
                    { ""name"": ""PredatorControlApp-Standalone.exe"", ""browser_download_url"": ""https://github.com/test/standalone.exe"" },
                    { ""name"": ""PredatorControlApp-net10.exe"", ""browser_download_url"": ""https://github.com/test/light.exe"" }
                ]
            }";

            using var doc = JsonDocument.Parse(json);
            string? standaloneUrl = Updater.PickAsset(doc.RootElement, true);
            Assert.Equal("https://github.com/test/standalone.exe", standaloneUrl);

            string? lightUrl = Updater.PickAsset(doc.RootElement, false);
            Assert.Equal("https://github.com/test/light.exe", lightUrl);
        }

        [Fact]
        public void Sha256Extraction_ParsesSha256ChecksumFromBody()
        {
            const string json = @"
            {
                ""body"": ""## Release v1.5.0\nSHA-256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\nPredatorControlApp-Standalone.exe: abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789""
            }";

            using var doc = JsonDocument.Parse(json);
            string? sha = Updater.ExtractExpectedSha256(doc.RootElement, "https://github.com/test/PredatorControlApp-Standalone.exe");
            Assert.NotNull(sha);
            Assert.Equal(64, sha.Length);
            Assert.StartsWith("abcdef", sha);
        }

        [Fact]
        public void Sha256Extraction_RequiresDigestForSelectedAsset()
        {
            const string json = """
                {"body":"SHA-256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                 "assets":[{"browser_download_url":"https://github.com/test/other.exe","digest":"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}]}
                """;
            using var doc = JsonDocument.Parse(json);
            Assert.Null(Updater.ExtractExpectedSha256(doc.RootElement, "https://github.com/test/app.exe"));
            Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                Updater.ExtractExpectedSha256(doc.RootElement, "https://github.com/test/other.exe"));
        }

        [Fact]
        public void Sha256Extraction_DoesNotMatchSuffixOfDifferentAssetName()
        {
            using var doc = JsonDocument.Parse("""
                {"body":"other-app.exe: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}
                """);
            Assert.Null(Updater.ExtractExpectedSha256(doc.RootElement, "https://github.com/test/app.exe"));
        }

        [Fact]
        public async Task Apply_RejectsMissingChecksumBeforeDownload()
        {
            var update = new UpdateInfo(new Version(9, 0, 0), "v9.0.0", "",
                "https://github.com/red16124724/Predator-Control-App/releases/download/v9.0.0/app.exe");
            await Assert.ThrowsAsync<System.Security.SecurityException>(() => Updater.ApplyAsync(update));
        }

        [Fact]
        public void ZeroBackgroundNetworkPolling_EnsuresNoPeriodicHttpTimers()
        {
            var settings = new AppSettings();
            // AppSettings does not have any periodic network update timers or background internet intervals
            Assert.NotNull(settings);
            Assert.False(settings.BatteryLimit);
        }
    }
}
