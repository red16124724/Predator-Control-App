using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests;

public class CompatibilityReportTests
{
    [Fact]
    public void ReportUsesDetectedCapabilitiesWithoutPrivateFirmwareFields()
    {
        var caps = DeviceCapabilities.None with { FanTable = true, BiosHardwareFingerprint = "PRIVATE-FINGERPRINT" };
        var lighting = new KeyboardLightingCapabilities
        {
            Tier = KeyboardLightingTier.FourZone, ZoneCount = 4,
            LightingServiceReachable = true, SmbiosFingerprint = "PRIVATE-SMBIOS"
        };
        string report = CompatibilityReport.Create("Test model", "V1.10", caps, lighting, "Embedded, verified");
        Assert.Contains("Model: Test model", report);
        Assert.Contains("BIOS: V1.10", report);
        Assert.Contains("Fan tables: Yes", report);
        Assert.Contains("Acer lighting service reachable: Yes", report);
        Assert.DoesNotContain("PRIVATE-FINGERPRINT", report);
        Assert.DoesNotContain("PRIVATE-SMBIOS", report);
    }

    [Fact]
    public void ReportShowsPendingLightingAndFlattensLineBreaks()
    {
        string report = CompatibilityReport.Create("Model\r\nInjected", "", DeviceCapabilities.None, null, "Unavailable");
        Assert.Contains("Model: Model  Injected", report);
        Assert.Contains("BIOS: Unknown", report);
        Assert.Contains("Detection has not completed.", report);
    }
}
