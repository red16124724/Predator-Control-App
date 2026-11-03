using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;
using PredatorControlApp;

namespace PredatorControlApp.Tests
{
    public class UpdaterStartupAndVersionParsingTests
    {
        [Theory]
        [InlineData("v1.5.6", 1, 5, 6, 0)]
        [InlineData("v.1.5.6", 1, 5, 6, 0)]
        [InlineData("1.5.6", 1, 5, 6, 0)]
        [InlineData("v1.5.5-clean", 1, 5, 5, 0)]
        [InlineData("v1.6.0-beta.1", 1, 6, 0, 0)]
        [InlineData("1.5.5+build1024", 1, 5, 5, 0)]
        [InlineData("v1.5.5.2", 1, 5, 5, 2)]
        [InlineData("1.5.5.0", 1, 5, 5, 0)]
        [InlineData("v2.0", 2, 0, 0, 0)]
        public void TryParseNormalizedVersion_ParsesVariousFormats(string raw, int expectedMajor, int expectedMinor, int expectedBuild, int expectedRevision)
        {
            bool ok = Updater.TryParseNormalizedVersion(raw, out var parsed);
            Assert.True(ok, $"Failed to parse version: {raw}");
            Assert.Equal(expectedMajor, parsed.Major);
            Assert.Equal(expectedMinor, parsed.Minor);
            Assert.Equal(expectedBuild, Math.Max(parsed.Build, 0));
            Assert.Equal(expectedRevision, Math.Max(parsed.Revision, 0));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("invalid-tag")]
        [InlineData("v_release")]
        public void TryParseNormalizedVersion_RejectsInvalidInput(string? raw)
        {
            bool ok = Updater.TryParseNormalizedVersion(raw, out _);
            Assert.False(ok);
        }

        [Fact]
        public void IsRemoteVersionNewer_Handles4PartHotfixesCorrectly()
        {
            var local = new Version(1, 5, 5, 0);
            var remoteHotfix = new Version(1, 5, 5, 1);
            Assert.True(Updater.IsRemoteVersionNewer(remoteHotfix, local));
            Assert.False(Updater.IsRemoteVersionNewer(local, remoteHotfix));
        }

        [Fact]
        public void IsRemoteVersionNewer_Handles3PartVs4PartCorrectly()
        {
            // 3-part version 1.5.6 should be newer than 4-part 1.5.5.0
            var local = new Version(1, 5, 5, 0);
            var remote = new Version(1, 5, 6);
            Assert.True(Updater.IsRemoteVersionNewer(remote, local));

            // Identical versions (3-part vs 4-part with revision 0)
            var localSame = new Version(1, 5, 5, 0);
            var remoteSame = new Version(1, 5, 5);
            Assert.False(Updater.IsRemoteVersionNewer(remoteSame, localSame));
            Assert.False(Updater.IsRemoteVersionNewer(localSame, remoteSame));
        }

        [Fact]
        public void IsRemoteVersionNewer_WithTagStrings_WorksCorrectly()
        {
            var current = new Version(1, 5, 5, 0);
            Assert.True(Updater.IsRemoteVersionNewer("v1.5.6", current));
            Assert.True(Updater.IsRemoteVersionNewer("v1.6.0-beta", current));
            Assert.True(Updater.IsRemoteVersionNewer("v1.5.5.1", current));
            Assert.False(Updater.IsRemoteVersionNewer("v1.5.5", current));
            Assert.False(Updater.IsRemoteVersionNewer("v1.5.4", current));
            Assert.False(Updater.IsRemoteVersionNewer("v1.5.5.0", current));
        }

        [Fact]
        public void IsTransientNetworkException_IdentifiesColdBootErrors()
        {
            Assert.True(Updater.IsTransientNetworkException(new HttpRequestException("DNS failed")));
            Assert.True(Updater.IsTransientNetworkException(new TimeoutException("DHCP timeout")));
            Assert.True(Updater.IsTransientNetworkException(new SocketException(10060))); // WSAETIMEDOUT
            Assert.True(Updater.IsTransientNetworkException(new TaskCanceledException("timed out")));
            Assert.True(Updater.IsTransientNetworkException(new Exception("Wrapper", new SocketException(10061))));

            Assert.False(Updater.IsTransientNetworkException(new ArgumentNullException()));
            Assert.False(Updater.IsTransientNetworkException(new InvalidOperationException("App state invalid")));
        }
    }
}
