using System;

using System.Collections.Generic;

using System.Drawing;

using System.Linq;

using System.Text.Json;

using System.Threading;

using System.Threading.Tasks;

using PredatorControlApp;

using Xunit;



namespace PredatorControlApp.Tests

{

    /// <summary>

    /// Comprehensive tests for keyboard zone lighting:

    /// 1. BIOS classification (1-Zone, 3-Zone, 4-Zone, Per-Key candidates and fallbacks)

    /// 2. AcerServiceClient zone & logo JSON payloads and async dispatch

    /// 3. WmiController 1/3/4 sector hardware dispatch and mode-0 static preparation

    /// 4. KeyboardLightingController state management, clamping, and debounce

    /// 5. Edge cases: all-black (off), rapid mutations, and concurrent multithreaded access

    /// </summary>

    [Collection("AcerServiceClientTests")]
    public class ZoneLightingHardwareAndUiTests

    {

        #region 1. BIOS Classification Tests



        [Theory]

        [InlineData("Predator PH315-50", "Predator Helios 300 (Single-Zone)")]

        [InlineData("Aspire A715-71", "Aspire 7 (Single-Zone)")]

        [InlineData("Nitro AN515-31", "Nitro 5 (Single-Zone)")]

        public void Classify_OneZoneModels_DetectSingleZoneTierAndSingleZone(string modelName, string expectedFamily)

        {

            var caps = KeyboardLightingDetector.Classify(

                modelName, "V1.33", "Coventry_CFS", "fp-single",

                serviceReachable: false, wmiZonePath: true, tierOverride: 0);



            Assert.True(caps.Tier == KeyboardLightingTier.SingleZone || caps.Tier == KeyboardLightingTier.OneZone);

            Assert.Equal(1, caps.ZoneCount);

            Assert.False(caps.PerKeySupported);

            Assert.Equal("1-ZONE RGB", caps.TierLabel);

            Assert.Contains(expectedFamily, caps.Notes);

        }



        [Theory]

        [InlineData("Nitro AN515-54", "Nitro 5 (3-Zone)")]

        [InlineData("Predator PH315-52", "Predator Helios 300 (3-Zone)")]

        public void Classify_ThreeZoneModels_AN515_54_And_PH315_52_DetectThreeZoneTier(string modelName, string expectedFamily)

        {

            var caps = KeyboardLightingDetector.Classify(

                modelName, "V1.12", "Octavia_CFS", "fp-ph315",

                serviceReachable: false, wmiZonePath: true, tierOverride: 0);



            Assert.Equal(KeyboardLightingTier.ThreeZone, caps.Tier);

            Assert.Equal(3, caps.ZoneCount);

            Assert.False(caps.PerKeySupported);

            Assert.Equal("3-ZONE RGB", caps.TierLabel);

            Assert.Contains(expectedFamily, caps.Notes);

        }



        [Theory]

        [InlineData("Predator Helios Neo PHN16-71", "Predator Helios Neo 16")]

        [InlineData("Predator PH315-54", "Predator Helios 300")]

        public void Classify_FourZoneModels_DetectFourZoneTierAndFourZones(string modelName, string expectedFamily)

        {

            var caps = KeyboardLightingDetector.Classify(

                modelName, "V1.05", "Board_CFS", "fp-4zone",

                serviceReachable: false, wmiZonePath: true, tierOverride: 0);



            Assert.Equal(KeyboardLightingTier.FourZone, caps.Tier);

            Assert.Equal(4, caps.ZoneCount);

            Assert.False(caps.PerKeySupported);

            Assert.Equal("4-ZONE RGB", caps.TierLabel);

            Assert.Contains(expectedFamily, caps.Notes);

        }



        [Theory]

        [InlineData("Predator Helios 16 PH16-72")]

        [InlineData("Predator Triton 16 PT16-51")]

        public void Classify_PerKeyModels_WithService_EnablesPerKey(string modelName)

        {

            var caps = KeyboardLightingDetector.Classify(

                modelName, "V1.00", null, null,

                serviceReachable: true, wmiZonePath: true, tierOverride: 0);



            Assert.Equal(KeyboardLightingTier.PerKey, caps.Tier);

            Assert.Equal(4, caps.ZoneCount);

            Assert.True(caps.PerKeySupported);

            Assert.Equal("PER-KEY RGB", caps.TierLabel);

        }



        [Theory]

        [InlineData("Predator Helios 16 PH16-72")]

        [InlineData("Predator Triton 16 PT16-51")]

        public void Classify_PerKeyModels_WithoutService_DegradesToFourZone(string modelName)

        {

            var caps = KeyboardLightingDetector.Classify(

                modelName, "V1.00", null, null,

                serviceReachable: false, wmiZonePath: true, tierOverride: 0);



            Assert.Equal(KeyboardLightingTier.FourZone, caps.Tier);

            Assert.Equal(4, caps.ZoneCount);

            Assert.False(caps.PerKeySupported);

            Assert.Contains("Acer Lighting Service is not reachable", caps.Notes);

        }



        [Fact]

        public void Classify_NoTransportAvailable_ReturnsUnavailableWithZeroZones()

        {

            var caps = KeyboardLightingDetector.Classify(

                "AN515-54", "V1.00", null, null,

                serviceReachable: false, wmiZonePath: false, tierOverride: 0);



            Assert.Equal(KeyboardLightingTier.Unavailable, caps.Tier);

            Assert.Equal(0, caps.ZoneCount);

            Assert.False(caps.PerKeySupported);

            Assert.Equal("UNAVAILABLE", caps.TierLabel);

        }



        #endregion



        #region 2. AcerServiceClient Payload & Protocol Tests



        [Fact]

        public void AcerService_BuildZoneLightingPayload_ValidJsonWithColorType2AndStaticEffect()

        {

            var colors = new List<Color>

            {

                Color.FromArgb(255, 0, 0),     // Red

                Color.FromArgb(0, 255, 0),     // Green

                Color.FromArgb(0, 0, 255),     // Blue

                Color.FromArgb(255, 255, 0)    // Yellow

            };



            string json = AcerServiceClient.BuildZoneLightingPayload(colors, brightnessPercent: 80, speedPercent: 60);



            Assert.NotNull(json);

            using var doc = JsonDocument.Parse(json);

            var root = doc.RootElement;



            Assert.Equal("LIGHTING", root.GetProperty("Function").GetString());

            var param = root.GetProperty("Parameter");



            Assert.Equal(1, param.GetProperty("device").GetInt32());

            Assert.Equal("Keyboard Device", param.GetProperty("deviceName").GetString());

            Assert.Equal("STATIC", param.GetProperty("effect").GetString());

            Assert.Equal(2, param.GetProperty("colortype").GetInt32());

            Assert.Equal(4, param.GetProperty("brightness").GetInt32()); // 80 / 20 = 4

            Assert.Equal(3, param.GetProperty("speed").GetInt32());



            var leds = param.GetProperty("LEDs");

            Assert.Equal(4, leds.GetArrayLength());



            Assert.Equal(0, leds[0].GetProperty("index").GetInt32());

            Assert.Equal("#FF0000", leds[0].GetProperty("color").GetString());



            Assert.Equal(1, leds[1].GetProperty("index").GetInt32());

            Assert.Equal("#00FF00", leds[1].GetProperty("color").GetString());



            Assert.Equal(2, leds[2].GetProperty("index").GetInt32());

            Assert.Equal("#0000FF", leds[2].GetProperty("color").GetString());



            Assert.Equal(3, leds[3].GetProperty("index").GetInt32());

            Assert.Equal("#FFFF00", leds[3].GetProperty("color").GetString());



            var subindex = param.GetProperty("subindex");

            Assert.Equal("STATIC", subindex.GetProperty("1").GetString());

            Assert.Equal("STATIC", subindex.GetProperty("2").GetString());

            Assert.Equal("STATIC", subindex.GetProperty("3").GetString());

            Assert.Equal("STATIC", subindex.GetProperty("4").GetString());

        }



        [Fact]

        public void AcerService_BuildZoneLightingPayload_NullOrEmptyColors_ProducesEmptyLedsArray()

        {

            string jsonNull = AcerServiceClient.BuildZoneLightingPayload(null, 100, 50);

            using (var docNull = JsonDocument.Parse(jsonNull))

            {

                var leds = docNull.RootElement.GetProperty("Parameter").GetProperty("LEDs");

                Assert.Equal(0, leds.GetArrayLength());

                Assert.Equal(2, docNull.RootElement.GetProperty("Parameter").GetProperty("colortype").GetInt32());

            }



            string jsonEmpty = AcerServiceClient.BuildZoneLightingPayload(Array.Empty<Color>(), 0, 50);

            using (var docEmpty = JsonDocument.Parse(jsonEmpty))

            {

                var param = docEmpty.RootElement.GetProperty("Parameter");

                Assert.Equal(0, param.GetProperty("LEDs").GetArrayLength());

                Assert.Equal(0, param.GetProperty("brightness").GetInt32());

            }

        }



        [Fact]

        public async Task AcerService_SetZoneLightingAsync_DispatchesPacket100WithCorrectJson()

        {

            uint capturedPacketId = 0;

            string? capturedPayload = null;



            AcerServiceClient.CommandInterceptor = (pkt, payload) =>

            {

                capturedPacketId = pkt;

                capturedPayload = payload;

            };



            try

            {

                var colors = new[] { Color.Red, Color.Green, Color.Blue, Color.White };

                await AcerServiceClient.SetZoneLightingAsync(colors, 100, 50);



                Assert.Equal(100u, capturedPacketId);

                Assert.NotNull(capturedPayload);

                Assert.Contains("\"colortype\":2", capturedPayload);

                Assert.Contains("\"effect\":\"STATIC\"", capturedPayload);

                Assert.Contains("\"#FF0000\"", capturedPayload);

            }

            finally

            {

                AcerServiceClient.CommandInterceptor = null;

            }

        }



        #endregion



        #region 3. WmiController Hardware Dispatch Tests



        [Fact]

        public void WmiController_HardwareDispatch_OneSector_SetsUnifiedColorAcrossAllZones()

        {

            using var wmi = new WmiController();

            var commands = new List<(string Method, ulong Input)>();



            wmi.CommandInterceptor = (method, input) =>

            {

                commands.Add((method, input));

            };



            var colors = new[] { Color.FromArgb(255, 100, 50) };

            bool ok = wmi.SetZoneColors(colors);



            Assert.True(ok);

            var rgbKbCalls = commands.Where(c => c.Method == "SetGamingRgbKb").ToList();

            Assert.Equal(5, rgbKbCalls.Count); // 1 unified broadcast (0x0F) + 4 fallback zone masks



            ulong payload = rgbKbCalls[0].Input;

            ulong unifiedMask = payload & 0xFF;

            Assert.Equal(0x0FUL, unifiedMask); // Unified 1-sector mask (all 4 zones)



            byte r = (byte)((payload >> 8) & 0xFF);

            byte g = (byte)((payload >> 16) & 0xFF);

            byte b = (byte)((payload >> 24) & 0xFF);

            Assert.Equal(255, r);

            Assert.Equal(100, g);

            Assert.Equal(50, b);

        }



        [Fact]

        public void WmiController_HardwareDispatch_ThreeSector_SetsThreeZoneMasks()

        {

            using var wmi = new WmiController();

            var commands = new List<(string Method, ulong Input)>();



            wmi.CommandInterceptor = (method, input) =>

            {

                commands.Add((method, input));

            };



            var colors = new[]

            {

                Color.FromArgb(255, 0, 0),

                Color.FromArgb(0, 255, 0),

                Color.FromArgb(0, 0, 255)

            };



            bool ok = wmi.SetZoneColors(colors);



            Assert.True(ok);

            var rgbKbCalls = commands.Where(c => c.Method == "SetGamingRgbKb").ToList();

            Assert.Equal(3, rgbKbCalls.Count);



            Assert.Equal(1UL, rgbKbCalls[0].Input & 0xFF); // Zone 1 mask: 0x01

            Assert.Equal(2UL, rgbKbCalls[1].Input & 0xFF); // Zone 2 mask: 0x02

            Assert.Equal(4UL, rgbKbCalls[2].Input & 0xFF); // Zone 3 mask: 0x04



            Assert.Equal(255u, (rgbKbCalls[0].Input >> 8) & 0xFF); // Zone 1 R = 255

            Assert.Equal(255u, (rgbKbCalls[1].Input >> 16) & 0xFF); // Zone 2 G = 255

            Assert.Equal(255u, (rgbKbCalls[2].Input >> 24) & 0xFF); // Zone 3 B = 255

        }



        [Fact]

        public void WmiController_HardwareDispatch_FourSector_SetsFourZoneMasks()

        {

            using var wmi = new WmiController();

            var commands = new List<(string Method, ulong Input)>();



            wmi.CommandInterceptor = (method, input) =>

            {

                commands.Add((method, input));

            };



            var colors = new[]

            {

                Color.FromArgb(200, 10, 20),

                Color.FromArgb(30, 210, 40),

                Color.FromArgb(50, 60, 220),

                Color.FromArgb(150, 160, 170)

            };



            bool ok = wmi.SetZoneColors(colors);



            Assert.True(ok);

            var rgbKbCalls = commands.Where(c => c.Method == "SetGamingRgbKb").ToList();

            Assert.Equal(4, rgbKbCalls.Count);



            Assert.Equal(1UL, rgbKbCalls[0].Input & 0xFF); // Zone 1 mask: 0x01

            Assert.Equal(2UL, rgbKbCalls[1].Input & 0xFF); // Zone 2 mask: 0x02

            Assert.Equal(4UL, rgbKbCalls[2].Input & 0xFF); // Zone 3 mask: 0x04

            Assert.Equal(8UL, rgbKbCalls[3].Input & 0xFF); // Zone 4 mask: 0x08

        }









        [Fact]

        public void WmiController_SetZoneColors_NullAndEmpty_ReturnsFalseSafely()

        {

            using var wmi = new WmiController();

            Assert.False(wmi.SetZoneColors(null!));

            Assert.False(wmi.SetZoneColors(Array.Empty<Color>()));

        }



        [Fact]

        public void WmiController_VerifiesMode0StaticPreparation()

        {

            using var wmi = new WmiController();

            var ledArrayCommands = new List<(string Method, byte[] Payload)>();

            wmi.LedArrayCommandInterceptor = (method, payload) => ledArrayCommands.Add((method, (byte[])payload.Clone()));



            wmi.SetStaticColor(255, 128, 64, 100);



            Assert.Equal(0, wmi.LastRgbMode);

            Assert.Equal(255, wmi.LastR);

            Assert.Equal(128, wmi.LastG);

            Assert.Equal(64, wmi.LastB);



            // SetGamingKBBacklight byte 0 must be 0 for Mode-0 Static

            var kbPacket = ledArrayCommands.FirstOrDefault(c => c.Method == "SetGamingKBBacklight");

            Assert.NotNull(kbPacket.Payload);

            Assert.Equal(0, kbPacket.Payload[0]); // mode 0

            Assert.Equal(255, kbPacket.Payload[5]); // R

            Assert.Equal(128, kbPacket.Payload[6]); // G

            Assert.Equal(64, kbPacket.Payload[7]); // B

        }



        #endregion



        




        [Fact]
        public void WmiController_SetZoneColors_DualDispatch_SetGamingKBBacklight_And_Service()
        {
            using var wmi = new WmiController();
            var wmiCommands = new List<(string Method, ulong Input)>();
            var ledArrayCommands = new List<(string Method, byte[] Payload)>();
            var acerPayloads = new List<string>();

            wmi.CommandInterceptor = (method, input) => wmiCommands.Add((method, input));
            wmi.LedArrayCommandInterceptor = (method, payload) => ledArrayCommands.Add((method, (byte[])payload.Clone()));
            AcerServiceClient.CommandInterceptor = (pkt, json) =>
            {
                if (json != null) { lock (acerPayloads) { acerPayloads.Add(json); } }
            };

            try
            {
                var colors = new[]
                {
                    Color.FromArgb(255, 0, 0),
                    Color.FromArgb(0, 255, 0),
                    Color.FromArgb(0, 0, 255),
                    Color.FromArgb(255, 255, 0)
                };

                bool ok = wmi.SetZoneColors(colors, 100);
                Assert.True(ok);

                // Channel 1: Legacy SetGamingRgbKb
                var rgbKbCalls = wmiCommands.Where(c => c.Method == "SetGamingRgbKb").ToList();
                Assert.Equal(4, rgbKbCalls.Count);

                // Channel 2: Modern BIOS 16-byte SetGamingKBBacklight array packet
                // 1 EC halt packet (pkt[0]=0, pkt[3]=0, pkt[8]=0, pkt[9]=1) + 4 zone packets = 5 packets
                var kbPackets = ledArrayCommands.Where(c => c.Method == "SetGamingKBBacklight").ToList();
                Assert.Equal(5, kbPackets.Count);
                Assert.Equal(0, kbPackets[0].Payload[0]); // EC halt static mode 0
                Assert.Equal(0, kbPackets[0].Payload[3]); // EC halt dynamic flag 0
                Assert.Equal(0, kbPackets[0].Payload[8]); // EC halt twinkling sub-reg 0
                Assert.Equal(1, kbPackets[0].Payload[9]); // EC halt target keyboard
                for (int i = 0; i < 4; i++)
                {
                    var pkt = kbPackets[i + 1].Payload;
                    Assert.Equal(0, pkt[0]); // mode 0 static
                    Assert.Equal(100, pkt[2]); // 100% brightness
                    Assert.Equal((byte)(i + 1), pkt[9]); // zone index 1..4
                }

                // Channel 3: AcerServiceClient port 21919/46933 IPC
                // Fire-and-forget lighting work queued by an earlier test can land here, so wait for
                // this test's own payload instead of accepting the first payload to arrive.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1500)
                {
                    lock (acerPayloads)
                    {
                        if (acerPayloads.Any(p => p != null && p.Contains("LIGHTING") && p.Contains("#FF0000"))) break;
                    }
                    Thread.Sleep(10);
                }
                lock (acerPayloads)
                {
                    Assert.Contains(acerPayloads, p => p != null && p.Contains("LIGHTING") && p.Contains("#FF0000"));
                }
            }
            finally
            {
                AcerServiceClient.CommandInterceptor = null;
            }
        }

        #region 4. KeyboardLightingController Tests



        [Fact]

        public void Controller_ZoneSetting_And_SetAllZones()

        {

            var caps = new KeyboardLightingCapabilities

            {

                Tier = KeyboardLightingTier.FourZone,

                ZoneCount = 4

            };

            using var controller = new KeyboardLightingController(null, caps);



            controller.SetZoneColor(0, Color.Red);

            controller.SetZoneColor(1, Color.Green);

            controller.SetZoneColor(2, Color.Blue);

            controller.SetZoneColor(3, Color.Yellow);



            Assert.Equal(Color.Red, controller.GetZoneColor(0));

            Assert.Equal(Color.Green, controller.GetZoneColor(1));

            Assert.Equal(Color.Blue, controller.GetZoneColor(2));

            Assert.Equal(Color.Yellow, controller.GetZoneColor(3));



            controller.SetAllZones(Color.Cyan);

            for (int i = 0; i < 4; i++)

            {

                Assert.Equal(Color.Cyan, controller.GetZoneColor(i));

            }



            var snapshot = controller.SnapshotZoneColors();

            Assert.Equal(4, snapshot.Length);

            snapshot[0] = Color.Magenta; // verify snapshot is cloned and doesn't mutate internal state

            Assert.Equal(Color.Cyan, controller.GetZoneColor(0));

        }



        [Fact]

        public void Controller_IndexClamping_DoesNotThrowOrCorruptState()

        {

            var caps = new KeyboardLightingCapabilities { Tier = KeyboardLightingTier.FourZone, ZoneCount = 4 };

            using var controller = new KeyboardLightingController(null, caps);



            controller.SetAllZones(Color.White);



            // Out-of-bounds writes should be safely ignored

            controller.SetZoneColor(-1, Color.Red);

            controller.SetZoneColor(4, Color.Red);

            controller.SetZoneColor(100, Color.Red);



            for (int i = 0; i < 4; i++)

            {

                Assert.Equal(Color.White, controller.GetZoneColor(i));

            }



            // Out-of-bounds reads return KeyboardLightingPalette.Off

            Assert.Equal(KeyboardLightingPalette.Off, controller.GetZoneColor(-1));

            Assert.Equal(KeyboardLightingPalette.Off, controller.GetZoneColor(4));

            Assert.Equal(KeyboardLightingPalette.Off, controller.GetZoneColor(999));

        }



        [Fact]

        public async Task Controller_DebounceDispatch_CoalescesRapidEdits()

        {

            using var wmi = new WmiController();

            var commands = new List<(string Method, ulong Input)>();

            wmi.CommandInterceptor = (method, input) => commands.Add((method, input));



            var caps = new KeyboardLightingCapabilities { Tier = KeyboardLightingTier.FourZone, ZoneCount = 4 };

            using var controller = new KeyboardLightingController(wmi, caps);



            // Rapidly queue 5 zone updates with 40ms debounce

            for (int i = 1; i <= 5; i++)

            {

                controller.SetAllZones(Color.FromArgb(i * 40, 0, 0));

                controller.QueueApply(KeyboardLightingScope.Zones, debounceMs: 40);

                await Task.Delay(10);

            }



            // Wait for debounce settle

            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < 2000 && commands.Count(c => c.Method == "SetGamingRgbKb") < 4)

            {

                await Task.Delay(20);

            }



            // Wmi should receive 4 zone masks from the final applied state (Color: 200, 0, 0)

            var rgbKbCalls = commands.Where(c => c.Method == "SetGamingRgbKb").ToList();

            Assert.Equal(4, rgbKbCalls.Count);

            byte lastR = (byte)((rgbKbCalls[0].Input >> 8) & 0xFF);

            Assert.Equal(200, lastR);

        }



        [Fact]

        public async Task Controller_ApplyAsync_Zones_DispatchesThroughWmi()

        {

            using var wmi = new WmiController();

            var commands = new List<(string Method, ulong Input)>();

            wmi.CommandInterceptor = (method, input) => commands.Add((method, input));



            var caps = new KeyboardLightingCapabilities { Tier = KeyboardLightingTier.FourZone, ZoneCount = 4 };

            using var controller = new KeyboardLightingController(wmi, caps);



            controller.SetZoneColor(0, Color.Red);

            controller.SetZoneColor(1, Color.Green);

            controller.SetZoneColor(2, Color.Blue);

            controller.SetZoneColor(3, Color.White);



            var status = await controller.ApplyAsync(KeyboardLightingScope.Zones);



            Assert.True(status.Success);

            Assert.Equal(KeyboardLightingTier.FourZone, status.AppliedTier);

            Assert.Equal(4, commands.Count(c => c.Method == "SetGamingRgbKb"));

        }



        #endregion



        #region 5. Edge Cases & Concurrency Tests



        [Fact]

        public void EdgeCase_AllBlackOff_CorrectlyDispatched()

        {

            using var wmi = new WmiController();

            var commands = new List<(string Method, ulong Input)>();

            wmi.CommandInterceptor = (method, input) => commands.Add((method, input));



            var colors = new[] { Color.Black, Color.Black, Color.Black, Color.Black };

            bool ok = wmi.SetZoneColors(colors);



            Assert.True(ok);

            var rgbKbCalls = commands.Where(c => c.Method == "SetGamingRgbKb").ToList();

            Assert.Equal(4, rgbKbCalls.Count);



            foreach (var call in rgbKbCalls)

            {

                ulong payload = call.Input;

                byte r = (byte)((payload >> 8) & 0xFF);

                byte g = (byte)((payload >> 16) & 0xFF);

                byte b = (byte)((payload >> 24) & 0xFF);

                Assert.Equal(0, r);

                Assert.Equal(0, g);

                Assert.Equal(0, b);

            }

        }



        [Fact]

        public void EdgeCase_RapidZoneColorChanges_ThreadSafety()

        {

            var caps = new KeyboardLightingCapabilities { Tier = KeyboardLightingTier.FourZone, ZoneCount = 4 };

            using var controller = new KeyboardLightingController(null, caps);



            var exceptions = new List<Exception>();

            var threads = new List<Thread>();



            for (int t = 0; t < 10; t++)

            {

                int threadId = t;

                var th = new Thread(() =>

                {

                    try

                    {

                        for (int i = 0; i < 200; i++)

                        {

                            var col = Color.FromArgb((threadId * 20 + i) % 256, 128, 64);

                            controller.SetZoneColor(i % 4, col);

                            if (i % 20 == 0) controller.SetAllZones(col);

                            var snapshot = controller.SnapshotZoneColors();

                            Assert.Equal(4, snapshot.Length);

                            var z = controller.GetZoneColor(i % 4);

                            Assert.Equal(255, z.A);

                        }

                    }

                    catch (Exception ex)

                    {

                        lock (exceptions) exceptions.Add(ex);

                    }

                });

                threads.Add(th);

            }



            foreach (var th in threads) th.Start();

            foreach (var th in threads) th.Join(5000);



            Assert.Empty(exceptions);

        }



        [Fact]

        public async Task EdgeCase_ConcurrentZoneUpdatesAcrossThreads()

        {

            using var wmi = new WmiController();
            wmi.CommandInterceptor = (method, input) => { };

            var caps = new KeyboardLightingCapabilities { Tier = KeyboardLightingTier.FourZone, ZoneCount = 4 };

            using var controller = new KeyboardLightingController(wmi, caps);



            var tasks = new List<Task>();

            for (int i = 0; i < 8; i++)

            {

                int index = i;

                tasks.Add(Task.Run(async () =>

                {

                    for (int j = 0; j < 50; j++)

                    {

                        controller.SetZoneColor(index % 4, Color.FromArgb(j * 4, 100, 200));

                        if (j % 10 == 0)

                        {

                            var status = await controller.ApplyAsync(KeyboardLightingScope.Zones);

                            Assert.True(status.Success);

                        }

                    }

                }));

            }



            await Task.WhenAll(tasks);

            var finalColors = controller.SnapshotZoneColors();

            Assert.Equal(4, finalColors.Length);

        }



        
        [Fact]
        public void WmiController_MeteorTransition_SendsStaticHaltPacket_ToBothKBBacklightAndSetGamingLED()
        {
            using var wmi = new WmiController();
            var ledArrayCommands = new List<(string Method, byte[] Payload)>();
            var acerPayloads = new List<string>();

            wmi.LedArrayCommandInterceptor = (method, payload) => ledArrayCommands.Add((method, (byte[])payload.Clone()));
            AcerServiceClient.CommandInterceptor = (pkt, json) =>
            {
                if (json != null) { lock (acerPayloads) { acerPayloads.Add(json); } }
            };

            try
            {
                // 1. Enter Meteor mode (mode 6)
                wmi.SetRgbMode(6, 255, 0, 0, 100, 5, 0);
                Assert.Equal(6, wmi.LastRgbMode);

                ledArrayCommands.Clear();
                acerPayloads.Clear();

                // 2. Switch away from Meteor to Breathing (mode 1)
                wmi.SetRgbMode(1, 0, 255, 0, 100, 5, 0);

                // Verify EC halt static mode-0 packet was sent to BOTH SetGamingKBBacklight and SetGamingLED
                var haltKb = ledArrayCommands.Where(c => c.Method == "SetGamingKBBacklight" && c.Payload[0] == 0 && c.Payload[8] == 0 && c.Payload[9] == 1).ToList();
                var haltLed = ledArrayCommands.Where(c => c.Method == "SetGamingLED" && c.Payload[0] == 0 && c.Payload[8] == 0 && c.Payload[9] == 1).ToList();

                Assert.NotEmpty(haltKb);
                Assert.NotEmpty(haltLed);

                // Verify AcerServiceClient was notified with STATIC effect
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2000)
                {
                    lock (acerPayloads)
                    {
                        if (acerPayloads.Any(p => p != null && p.Contains("\"effect\":\"STATIC\""))) break;
                    }
                    Thread.Sleep(10);
                }
                lock (acerPayloads)
                {
                    Assert.Contains(acerPayloads, p => p != null && p.Contains("\"effect\":\"STATIC\""));
                }
            }
            finally
            {
                AcerServiceClient.CommandInterceptor = null;
            }
        }

        [Fact]
        public void WmiController_SetStaticColor_FromMeteor_DispatchesHaltPacket()
        {
            using var wmi = new WmiController();
            var ledArrayCommands = new List<(string Method, byte[] Payload)>();

            wmi.LedArrayCommandInterceptor = (method, payload) => ledArrayCommands.Add((method, (byte[])payload.Clone()));

            // Enter Meteor mode (mode 6)
            wmi.SetRgbMode(6, 0, 0, 255, 100, 5, 0);
            ledArrayCommands.Clear();

            // Call SetStaticColor directly
            wmi.SetStaticColor(255, 255, 255, 100);

            // Verify EC halt static mode-0 packet was sent to BOTH SetGamingKBBacklight and SetGamingLED
            var haltKb = ledArrayCommands.Where(c => c.Method == "SetGamingKBBacklight" && c.Payload[0] == 0 && c.Payload[9] == 1).ToList();
            var haltLed = ledArrayCommands.Where(c => c.Method == "SetGamingLED" && c.Payload[0] == 0 && c.Payload[9] == 1).ToList();

            Assert.NotEmpty(haltKb);
            Assert.NotEmpty(haltLed);
        }

        [Fact]
        public void AcerServiceClient_BuildZoneLightingPayload_MapsOneAndThreeZonesToFourHardwareZones()
        {
            // 1-zone mapping: all 4 zones receive color[0]
            var oneColor = new[] { Color.FromArgb(10, 20, 30) };
            string jsonOne = AcerServiceClient.BuildZoneLightingPayload(oneColor, 100, 50);
            using var docOne = System.Text.Json.JsonDocument.Parse(jsonOne);
            var ledsOne = docOne.RootElement.GetProperty("Parameter").GetProperty("LEDs");
            Assert.Equal(4, ledsOne.GetArrayLength());
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(i, ledsOne[i].GetProperty("index").GetInt32());
                Assert.Equal("#0A141E", ledsOne[i].GetProperty("color").GetString());
            }

            // 3-zone mapping to 4 physical zones: Left=0, Center-Left=1, Center-Right=1, Right=2
            var threeColors = new[]
            {
                Color.FromArgb(255, 0, 0),
                Color.FromArgb(0, 255, 0),
                Color.FromArgb(0, 0, 255)
            };
            string jsonThree = AcerServiceClient.BuildZoneLightingPayload(threeColors, 100, 50);
            using var docThree = System.Text.Json.JsonDocument.Parse(jsonThree);
            var ledsThree = docThree.RootElement.GetProperty("Parameter").GetProperty("LEDs");
            Assert.Equal(4, ledsThree.GetArrayLength());
            Assert.Equal("#FF0000", ledsThree[0].GetProperty("color").GetString()); // Left
            Assert.Equal("#00FF00", ledsThree[1].GetProperty("color").GetString()); // Center-Left
            Assert.Equal("#00FF00", ledsThree[2].GetProperty("color").GetString()); // Center-Right
            Assert.Equal("#0000FF", ledsThree[3].GetProperty("color").GetString()); // Right
        }


        #endregion

    }

}

