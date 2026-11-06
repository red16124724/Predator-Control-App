using System;
using System.Runtime.InteropServices;
using Xunit;
using PredatorControlApp;
using PredatorControlApp.Bios;

namespace PredatorControlApp.Tests
{
    public class DeviceBiosInfoAndArchitectureTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _output;

        public DeviceBiosInfoAndArchitectureTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _output = output;
        }
        [Fact]
        public void DeviceBiosInfo_FullModelName_FormatsManufacturerAndModel()
        {
            var info1 = new DeviceBiosInfo { Manufacturer = "Acer", Model = "Predator PHN14-51" };
            Assert.Equal("Acer Predator PHN14-51", info1.FullModelName);

            var info2 = new DeviceBiosInfo { Manufacturer = "Acer", Model = "Acer Predator Helios Neo 16 PHN16-71" };
            Assert.Equal("Acer Predator Helios Neo 16 PHN16-71", info2.FullModelName);

            var info3 = new DeviceBiosInfo { Manufacturer = "", Model = "Predator PH16-71" };
            Assert.Equal("Predator PH16-71", info3.FullModelName);

            var info4 = new DeviceBiosInfo { Manufacturer = "Microsoft Corporation", Model = "Virtual Machine" };
            Assert.Equal("Microsoft Corporation Virtual Machine", info4.FullModelName);

            var info5 = new DeviceBiosInfo { Manufacturer = "", Model = "" };
            Assert.Equal("Unknown System", info5.FullModelName);
        }

        [Fact]
        public void DeviceBiosInfo_ToBadgeString_ContainsExpectedComponents()
        {
            var info = new DeviceBiosInfo
            {
                Manufacturer = "Acer",
                Model = "Predator PHN14-51",
                BiosVendor = "Insyde Corp.",
                BiosVersion = "V1.08",
                ReleaseDate = "06/13/2025",
                SmbiosVersion = "3.6",
                Architecture = "x64"
            };

            string badge = info.ToBadgeString();
            Assert.Equal("Acer Predator PHN14-51 | BIOS: V1.08 (06/13/2025) | SMBIOS v3.6 | x64", badge);

            string badgeWithPrefix = info.ToBadgeString(includeModelPrefix: true);
            Assert.Equal("Model: Acer Predator PHN14-51 | BIOS: V1.08 (06/13/2025) | SMBIOS v3.6 | x64", badgeWithPrefix);
        }

        [Fact]
        public void HardwareIdentity_GetDeviceBiosInfo_ReturnsDynamicLiveSystemData()
        {
            if (!OperatingSystem.IsWindows()) return;

            var info = HardwareIdentity.GetDeviceBiosInfo(refresh: true);
            Assert.NotNull(info);
            Assert.False(string.IsNullOrWhiteSpace(info.FullModelName), "FullModelName must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(info.Architecture), "Architecture must not be empty");

            string badge = HardwareIdentity.GetBiosHardwareBadge();
            _output.WriteLine($"LIVE_HARDWARE_BADGE: {badge}");
            _output.WriteLine($"LIVE_MODEL: {info.FullModelName}");
            _output.WriteLine($"LIVE_BIOS: {info.BiosVersion}");
            _output.WriteLine($"LIVE_RELEASE_DATE: {info.ReleaseDate}");
            _output.WriteLine($"LIVE_SMBIOS: {info.SmbiosVersion}");
            _output.WriteLine($"LIVE_ARCH: {info.Architecture}");
            Assert.NotNull(badge);
            Assert.Contains("BIOS:", badge);

            // Verify it is not hardcoded to a fictitious test machine
            Assert.NotEqual("Predator PH16-71 | InsydeH2O v1.08", badge);
        }

        [Fact]
        public void BiosManager_BiosInfoAndBadge_ReflectDynamicHardware()
        {
            var mgr = BiosManager.Instance;
            Assert.NotNull(mgr.BiosInfo);
            Assert.False(string.IsNullOrWhiteSpace(mgr.BiosBadgeText));
            Assert.Contains("BIOS:", mgr.BiosBadgeText);
            Assert.Equal(mgr.BiosInfo.ToBadgeString(), mgr.BiosBadgeText);
        }

        [Fact]
        public void WmiController_GetDeviceBiosInfo_ExposesHardwareInfo()
        {
            if (!OperatingSystem.IsWindows()) return;

            using var wmi = new WmiController(enableHardware: false);
            var info = wmi.GetDeviceBiosInfo();
            Assert.NotNull(info);
            Assert.False(string.IsNullOrWhiteSpace(info.Architecture));
            Assert.Equal(info.ToBadgeString(), wmi.GetBiosHardwareBadge());
        }
    }
}
