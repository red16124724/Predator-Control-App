using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class EnekRgbAndLogoIsolationTests
    {
        [Fact]
        public void EnekRgbClient_Constants_MatchHardwareSpecification()
        {
            Assert.Equal(0xA4, EnekRgbClient.ReportId);
            Assert.Equal(0x21, EnekRgbClient.TargetKeyboard);
            Assert.Equal(0x83, EnekRgbClient.TargetCoverLogo);
            Assert.Equal(11, EnekRgbClient.ExpectedReportLength);

            Assert.Equal(0x02, EnekRgbClient.EffectStatic);
            Assert.Equal(0x04, EnekRgbClient.EffectBreathing);
            Assert.Equal(0x05, EnekRgbClient.EffectNeon);
            Assert.Equal(0x07, EnekRgbClient.EffectWave);
            Assert.Equal(0x09, EnekRgbClient.EffectZoom);
            Assert.Equal(0x0A, EnekRgbClient.EffectMeteor);
            Assert.Equal(0x0B, EnekRgbClient.EffectTwinkling);

            Assert.Equal(new byte[] { 0x01, 0x02, 0x04, 0x08 }, EnekRgbClient.ZoneMasks);
        }

        [Fact]
        public void EnekRgbClient_TurnOffLogo_SendsTarget83WithZeroBrightness()
        {
            using var client = new EnekRgbClient();
            byte[]? capturedPacket = null;
            client.FeatureReportInterceptor = (packet) =>
            {
                capturedPacket = (byte[])packet.Clone();
                return true;
            };

            bool result = client.TurnOffLogo();

            Assert.True(result);
            Assert.NotNull(capturedPacket);
            Assert.Equal(11, capturedPacket.Length);
            Assert.Equal(0xA4, capturedPacket[0]); // Report ID
            Assert.Equal(0x83, capturedPacket[1]); // TargetCoverLogo
            Assert.Equal(0x01, capturedPacket[2]); // Effect
            Assert.Equal(0x00, capturedPacket[3]); // Brightness = 0 (OFF)
            Assert.Equal(0x00, capturedPacket[4]); // Speed = 0
            Assert.Equal(0x00, capturedPacket[5]); // Direction = 0
            Assert.Equal(0x00, capturedPacket[6]); // R = 0
            Assert.Equal(0x00, capturedPacket[7]); // G = 0
            Assert.Equal(0x00, capturedPacket[8]); // B = 0
            Assert.Equal(0x01, capturedPacket[9]); // ZoneMask = 1
            Assert.Equal(0x00, capturedPacket[10]);
        }

        [Fact]
        public void EnekRgbClient_SetZoneColors_Sends4IndividualFeatureReports()
        {
            using var client = new EnekRgbClient();
            var capturedPackets = new List<byte[]>();
            client.FeatureReportInterceptor = (packet) =>
            {
                capturedPackets.Add((byte[])packet.Clone());
                return true;
            };

            var colors = new[]
            {
                Color.FromArgb(255, 0, 0),     // Zone 1: Red
                Color.FromArgb(0, 255, 0),     // Zone 2: Green
                Color.FromArgb(0, 0, 255),     // Zone 3: Blue
                Color.FromArgb(255, 255, 0)    // Zone 4: Yellow
            };

            bool result = client.SetZoneColors(colors, 80);

            Assert.True(result);
            Assert.Equal(4, capturedPackets.Count);

            byte[] expectedMasks = { 0x01, 0x02, 0x04, 0x08 };
            for (int i = 0; i < 4; i++)
            {
                var pkt = capturedPackets[i];
                Assert.Equal(11, pkt.Length);
                Assert.Equal(0xA4, pkt[0]);              // Report ID
                Assert.Equal(0x21, pkt[1]);              // TargetKeyboard
                Assert.Equal(0x02, pkt[2]);              // EffectStatic
                Assert.Equal(80, pkt[3]);                // Brightness
                Assert.Equal(0, pkt[4]);                 // Speed
                Assert.Equal(0, pkt[5]);                 // Direction
                Assert.Equal(colors[i].R, pkt[6]);       // R
                Assert.Equal(colors[i].G, pkt[7]);       // G
                Assert.Equal(colors[i].B, pkt[8]);       // B
                Assert.Equal(expectedMasks[i], pkt[9]);  // Zone mask
                Assert.Equal(0x00, pkt[10]);
            }
        }

        [Fact]
        public void EnekRgbClient_SetDynamicEffect_MapsOpcodesAccurately()
        {
            using var client = new EnekRgbClient();
            var capturedPackets = new List<byte[]>();
            client.FeatureReportInterceptor = (packet) =>
            {
                capturedPackets.Add((byte[])packet.Clone());
                return true;
            };

            Color color = Color.FromArgb(10, 20, 30);

            // Test Breathing (mode 1 -> 0x04)
            client.SetDynamicEffect(1, color, 90, 5, 0);
            Assert.Equal(0x04, capturedPackets.Last()[2]);
            Assert.Equal(0x21, capturedPackets.Last()[1]);
            Assert.Equal(90, capturedPackets.Last()[3]);
            Assert.Equal(0x0F, capturedPackets.Last()[9]); // All zones broadcast

            // Test Neon (mode 2 -> 0x05)
            client.SetDynamicEffect(2, color, 90, 5, 0);
            Assert.Equal(0x05, capturedPackets.Last()[2]);

            // Test Wave (mode 3 -> 0x07)
            client.SetDynamicEffect(3, color, 90, 5, 1);
            Assert.Equal(0x07, capturedPackets.Last()[2]);
            Assert.Equal(1, capturedPackets.Last()[5]); // Direction = 1

            // Test Zoom (mode 5 -> 0x09)
            client.SetDynamicEffect(5, color, 90, 5, 0);
            Assert.Equal(0x09, capturedPackets.Last()[2]);

            // Test Meteor (mode 6 -> 0x0A)
            client.SetDynamicEffect(6, color, 90, 5, 0);
            Assert.Equal(0x0A, capturedPackets.Last()[2]);

            // Test Twinkling (mode 7 -> 0x0B)
            client.SetDynamicEffect(7, color, 90, 5, 0);
            Assert.Equal(0x0B, capturedPackets.Last()[2]);
        }

        [Fact]
        public void EnekRgbClient_TurnOffBacklight_SendsZeroBrightnessReport()
        {
            using var client = new EnekRgbClient();
            byte[]? capturedPacket = null;
            client.FeatureReportInterceptor = (packet) =>
            {
                capturedPacket = (byte[])packet.Clone();
                return true;
            };

            bool result = client.TurnOffBacklight();

            Assert.True(result);
            Assert.NotNull(capturedPacket);
            Assert.Equal(0xA4, capturedPacket[0]);
            Assert.Equal(0x21, capturedPacket[1]); // TargetKeyboard
            Assert.Equal(0x02, capturedPacket[2]); // Static
            Assert.Equal(0, capturedPacket[3]);    // Brightness = 0
            Assert.Equal(0x0F, capturedPacket[9]); // All zones
        }

        [Fact]
        public void WmiController_WhenEnekAvailable_BypassesWmiAndDispatchesViaEnekDirect()
        {
            using var wmi = new WmiController(enableHardware: false);
            var enekReports = new List<byte[]>();
            var wmiCommands = new List<(string Method, ulong Input)>();
            var wmiArrays = new List<(string Method, byte[] Payload)>();

            wmi.EnekRgb.DeviceAvailabilityChecker = () => true;
            wmi.EnekRgb.FeatureReportInterceptor = (pkt) =>
            {
                enekReports.Add((byte[])pkt.Clone());
                return true;
            };

            wmi.CommandInterceptor = (method, input) => wmiCommands.Add((method, input));
            wmi.LedArrayCommandInterceptor = (method, payload) => wmiArrays.Add((method, (byte[])payload.Clone()));

            Color[] colors = new[]
            {
                Color.FromArgb(200, 100, 50),
                Color.FromArgb(50, 100, 200),
                Color.FromArgb(100, 200, 50),
                Color.FromArgb(250, 250, 50)
            };

            // Call SetZoneColors
            bool ok = wmi.SetZoneColors(colors, 75);

            Assert.True(ok);
            // 4 keyboard reports + 1 logo off report
            Assert.Equal(5, enekReports.Count);

            // Verify first 4 reports are keyboard zone reports
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(0x21, enekReports[i][1]); // TargetKeyboard
                Assert.Equal(75, enekReports[i][3]);   // Brightness
            }

            // Verify 5th report is logo turn-off (Target 0x83, Brightness 0)
            Assert.Equal(0x83, enekReports[4][1]);     // TargetCoverLogo
            Assert.Equal(0x00, enekReports[4][3]);     // Brightness 0

            // CRITICAL: WMI commands MUST be completely bypassed on ENEK hardware to prevent periodic blinking
            Assert.Empty(wmiCommands);
            Assert.Empty(wmiArrays);
        }

        [Fact]
        public void WmiController_TurnOffLogo_TurnsOffLogoViaEnekOrWmi()
        {
            // Case 1: Enek available
            using (var wmi = new WmiController(enableHardware: false))
            {
                byte[]? captured = null;
                wmi.EnekRgb.DeviceAvailabilityChecker = () => true;
                wmi.EnekRgb.FeatureReportInterceptor = (pkt) =>
                {
                    captured = (byte[])pkt.Clone();
                    return true;
                };

                wmi.TurnOffLogo();

                Assert.NotNull(captured);
                Assert.Equal(0x83, captured[1]); // TargetCoverLogo
                Assert.Equal(0, captured[3]);    // Brightness 0
            }

            // Case 2: WMI fallback
            using (var wmi = new WmiController(enableHardware: false))
            {
                var wmiCommands = new List<(string Method, ulong Input)>();
                wmi.EnekRgb.DeviceAvailabilityChecker = () => false;
                wmi.CommandInterceptor = (method, input) => wmiCommands.Add((method, input));

                wmi.TurnOffLogo();

                Assert.Contains(wmiCommands, c => c.Method == "SetGamingLEDBehavior" && c.Input == (0x06ul | (0x01ul << 8)));
                Assert.Contains(wmiCommands, c => c.Method == "SetGamingLEDColor" && c.Input == 0x01ul);
            }
        }

        [Fact]
        public void ZeroLogoQueries_AuditSourceFiles_ConfirmsNoLogoQueriesExist()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string predatorAppDir = Path.Combine(root, "PredatorControlApp");

            string[] csFiles = Directory.GetFiles(predatorAppDir, "*.cs", SearchOption.AllDirectories);

            foreach (string file in csFiles)
            {
                string text = File.ReadAllText(file);

                // Zero calls to GetGamingLED or GetGamingLEDBehavior
                Assert.DoesNotContain("GetGamingLED(", text);
                Assert.DoesNotContain("GetGamingLEDBehavior(", text);

                // Ensure no query commands target zone 1 for reading
                Assert.DoesNotContain("GetGamingLEDColor", text);
            }
        }
    }
}
