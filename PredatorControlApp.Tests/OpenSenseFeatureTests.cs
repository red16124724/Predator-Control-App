using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class OpenSenseFeatureTests
    {
        #region 1. AcerProtocol Payload Bit Packing & Decoding

        [Fact]
        public void Test_AcerProtocol_NearestFanSpeed_ClampingAndRounding()
        {
            Assert.Equal(0, AcerProtocol.NearestFanSpeed(-20));
            Assert.Equal(0, AcerProtocol.NearestFanSpeed(4));
            Assert.Equal(10, AcerProtocol.NearestFanSpeed(6));
            Assert.Equal(50, AcerProtocol.NearestFanSpeed(48));
            Assert.Equal(50, AcerProtocol.NearestFanSpeed(52));
            Assert.Equal(90, AcerProtocol.NearestFanSpeed(89));
            Assert.Equal(100, AcerProtocol.NearestFanSpeed(99));
            Assert.Equal(100, AcerProtocol.NearestFanSpeed(150));
        }

        [Fact]
        public void Test_AcerProtocol_FanSpeedInput_Clamping()
        {
            ulong zero = AcerProtocol.FanSpeedInput(FanChannel.Cpu, -10);
            Assert.Equal(0u, (zero >> 8) & 0xFF);

            ulong fifty = AcerProtocol.FanSpeedInput(FanChannel.Gpu, 50);
            Assert.Equal(50u, (fifty >> 8) & 0xFF);

            ulong hundred = AcerProtocol.FanSpeedInput(FanChannel.System, 150);
            Assert.Equal(100u, (hundred >> 8) & 0xFF);
        }

        [Fact]
        public void Test_AcerProtocol_FanBehaviorInput_ChannelBitmasking()
        {
            var settings = new List<(FanChannel Fan, FanBehavior Behavior)>
            {
                (FanChannel.Cpu, FanBehavior.Auto),
                (FanChannel.Gpu, FanBehavior.Max),
                (FanChannel.System, FanBehavior.Custom)
            };

            ulong payload = AcerProtocol.FanBehaviorInput(settings);

            // Channel mask bits: Cpu (bit 0), System (bit 1), Gpu (bit 3) -> (1 | 2 | 8) = 11
            Assert.Equal(11UL, payload & 0x0FUL);

            // Behavior values: Auto (1) << 16, Custom (2) << 18, Max (3) << 22
            ulong cpuBehavior = (payload >> 16) & 0x03UL;
            ulong sysBehavior = (payload >> 18) & 0x03UL;
            ulong gpuBehavior = (payload >> 22) & 0x03UL;

            Assert.Equal((ulong)FanBehavior.Auto, cpuBehavior);
            Assert.Equal((ulong)FanBehavior.Custom, sysBehavior);
            Assert.Equal((ulong)FanBehavior.Max, gpuBehavior);
        }

        [Fact]
        public void Test_AcerProtocol_FanTable_EncodingAndModelMatching()
        {
            ulong payload = AcerProtocol.FanTableInput(FanTable.Faster);
            Assert.Equal((ulong)FanTable.Faster, payload);

            ulong output = ((ulong)FanTable.Fastest << 8);
            FanTable? table = AcerProtocol.FanTableValue(output);
            Assert.Equal(FanTable.Fastest, table);

            Assert.True(AcerProtocol.UsesFanTable("Nitro AN515-58"));
            Assert.True(AcerProtocol.UsesFanTable("Nitro AN517-42"));
            Assert.False(AcerProtocol.UsesFanTable("Aspire 5 A515"));
            Assert.False(AcerProtocol.UsesFanTable(null));
        }

        [Fact]
        public void Test_AcerProtocol_CoolBoost_Payloads()
        {
            ulong onInput = AcerProtocol.CoolBoostSetInput(true);
            Assert.Equal(7UL | (1UL << 16), onInput);

            ulong offInput = AcerProtocol.CoolBoostSetInput(false);
            Assert.Equal(7UL, offInput);

            ulong onOutput = (1UL << 8);
            Assert.True(AcerProtocol.CoolBoostValue(onOutput));

            ulong offOutput = (0UL << 8);
            Assert.False(AcerProtocol.CoolBoostValue(offOutput));
        }

        [Fact]
        public void Test_AcerProtocol_DustDefender_Decoding()
        {
            ulong runningOutput = (1UL << 24);
            Assert.True(AcerProtocol.DustDefenderValue(runningOutput));

            ulong stoppedOutput = (0UL << 24);
            Assert.False(AcerProtocol.DustDefenderValue(stoppedOutput));
        }

        [Fact]
        public void Test_AcerProtocol_UsbCharging_PayloadsAndState()
        {
            ulong onPayload = AcerProtocol.UsbChargingInput(true, 10);
            Assert.Equal(4UL | (15UL << 8) | (10UL << 16), onPayload);

            ulong offPayload = AcerProtocol.UsbChargingInput(false, 30);
            Assert.Equal(4UL | (31UL << 8) | (30UL << 16), offPayload);

            var state = AcerProtocol.UsbChargingValue(onPayload);
            Assert.True(state.Enabled);
            Assert.Equal(10, state.FloorPercent);

            var offState = AcerProtocol.UsbChargingValue(offPayload);
            Assert.False(offState.Enabled);
            Assert.Equal(30, offState.FloorPercent);
        }

        [Fact]
        public void Test_AcerProtocol_MiscSetting_EncodingAndDecoding()
        {
            ulong setPayload = AcerProtocol.MiscSetInput(MiscSetting.GpuMode, 2);
            Assert.Equal((ulong)MiscSetting.GpuMode | (2UL << 8), setPayload);

            byte val = AcerProtocol.MiscValue(setPayload);
            Assert.Equal(2, val);
        }

        [Fact]
        public void Test_AcerProtocol_BacklightTimeout_EncodingAndDecoding()
        {
            byte hotkey = 0x81;
            int brightness = 85;
            int timeout = 30;

            ulong payload = AcerProtocol.BacklightTimeoutInput(hotkey, brightness, timeout);

            Assert.Equal(85, AcerProtocol.BacklightBrightnessValue(payload));
            Assert.Equal(30, AcerProtocol.BacklightTimeoutValue(payload));
        }

        [Fact]
        public void Test_AcerProtocol_DecodeOperatingModeMask()
        {
            // Bitmask: Quiet (1 << 0), Balanced (1 << 1), Performance (1 << 4), Turbo (1 << 5)
            ulong mask = (1UL << 0) | (1UL << 1) | (1UL << 4) | (1UL << 5);
            ulong output = mask << 8;

            var modes = AcerProtocol.DecodeOperatingModeMask(output);

            Assert.Contains(OperatingMode.Quiet, modes);
            Assert.Contains(OperatingMode.Balanced, modes);
            Assert.Contains(OperatingMode.Performance, modes);
            Assert.Contains(OperatingMode.Turbo, modes);
            Assert.DoesNotContain(OperatingMode.Eco, modes);
        }

        #endregion

        #region 2. BatteryProtocol Decoding

        [Fact]
        public void Test_BatteryProtocol_DecodeStatus()
        {
            ulong functionList = (ulong)BatteryFunction.HealthMode | (ulong)BatteryFunction.Calibration;
            byte[] statusBytes = new byte[8];
            int healthIdx = System.Numerics.BitOperations.TrailingZeroCount((uint)BatteryFunction.HealthMode);
            int calIdx = System.Numerics.BitOperations.TrailingZeroCount((uint)BatteryFunction.Calibration);

            statusBytes[healthIdx] = 1;
            statusBytes[calIdx] = 0;

            var status = BatteryProtocol.DecodeStatus(functionList, statusBytes);

            Assert.NotNull(status);
            Assert.True(status.HealthModeSupported);
            Assert.True(status.CalibrationSupported);
            Assert.True(status.HealthModeOn);
            Assert.False(status.CalibrationOn);
        }

        [Fact]
        public void Test_BatteryProtocol_DecodeStatus_NullBytes_ReturnsNull()
        {
            var status = BatteryProtocol.DecodeStatus(0xFF, null);
            Assert.Null(status);
        }

        #endregion

        #region 3. Hardware Identity & SNID Computation

        [Theory]
        [InlineData("NXKGBEK0011230002A4000", "12300004240")]
        [InlineData("NHQBEDG001456000109900", "45600001699")]
        public void Test_HardwareIdentity_ComputeSnid_ValidSerial(string serial, string expectedSnid)
        {
            string? snid = HardwareIdentity.ComputeSnid(serial);
            Assert.Equal(expectedSnid, snid);
        }

        [Theory]
        [InlineData("")]
        [InlineData("SHORT_SERIAL")]
        [InlineData("INVALID_CHARS_IN_HEX!_22")]
        public void Test_HardwareIdentity_ComputeSnid_InvalidInput_ReturnsNull(string invalidSerial)
        {
            string? snid = HardwareIdentity.ComputeSnid(invalidSerial);
            Assert.Null(snid);
        }

        #endregion

        #region 4. AcerSmbios Table Parser

        [Fact]
        public void Test_AcerSmbios_Parse_Type172_And_Type170()
        {
            // Build synthetic SMBIOS buffer:
            // Type 172 (0xAC): len=9, major=3, minor=1, record 0x01 = 0x0001
            // Followed by double null 0x00, 0x00
            // Type 170 (0xAA): len=18, data[14]=0x81, data[15]=0x02, value=0x0004
            // Followed by double null 0x00, 0x00
            var bytes = new List<byte>
            {
                // Type 172
                172, 9, 0, 0, 3, 1, 0x01, 0x01, 0x00, 0, 0,
                // Type 170
                170, 18, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x81, 0x02, 0x04, 0x00, 0, 0
            };

            var smbios = AcerSmbios.Parse(bytes.ToArray());

            Assert.Equal((byte)3, smbios.GamingMajor);
            Assert.Equal((byte)1, smbios.GamingMinor);
            Assert.Equal(3.01, smbios.GamingVersion);
            Assert.True(smbios.HasEcLightBars);
            Assert.Equal((ushort)1, smbios.Gaming(1));
            Assert.True(smbios.HasHotkey(0x81));
        }

        [Fact]
        public void Test_AcerSmbios_Parse_Empty_ReturnsEmptyObject()
        {
            var smbios = AcerSmbios.Parse(ReadOnlySpan<byte>.Empty);
            Assert.Null(smbios.GamingMajor);
            Assert.Null(smbios.GamingMinor);
            Assert.Empty(smbios.GamingRecords);
            Assert.Empty(smbios.HotkeyFunctions);
        }

        #endregion

        #region 5. CurveFollower Hysteresis, Deadband & Boundaries

        [Fact]
        public void Test_CurveFollower_TrackingAndDeadband()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(90, 100)
            };

            // Initial point at 50°C -> speed 40%
            int initial = follower.Update(50, curve);
            Assert.Equal(40, initial);
            Assert.Equal(40, follower.Current);

            // Minor rise that produces only 1% speed change (within 2% deadband)
            // e.g. 50.5°C
            int damped = follower.Update(50.5, curve);
            Assert.Equal(40, damped); // Holds at 40% due to deadband

            // Significant jump to 70°C -> speed 70% (exceeds deadband)
            int jump = follower.Update(70, curve);
            Assert.Equal(70, jump);

            // Reset clears state
            follower.Reset();
            Assert.Null(follower.Current);
        }

        [Fact]
        public void Test_CurveFollower_BoundaryPassThrough()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 0),
                new(90, 100)
            };

            // Start near 100% boundary (e.g. 89°C -> 98%)
            follower.Update(89, curve);
            Assert.Equal(98, follower.Current);

            // Jump to 100% boundary (only 2% change, but boundary MUST pass through)
            int boundaryHit = follower.Update(90, curve);
            Assert.Equal(100, boundaryHit);
        }

        #endregion

        #region 6. HistoryBuffer Circular Buffer

        [Fact]
        public void Test_HistoryBuffer_CapacityAndIndexing()
        {
            var buffer = new HistoryBuffer(5);
            Assert.Equal(5, buffer.Capacity);
            Assert.Equal(0, buffer.Count);

            buffer.Add(10.0);
            buffer.Add(20.0);
            buffer.Add(30.0);
            Assert.Equal(3, buffer.Count);
            Assert.Equal(10.0, buffer[0]);
            Assert.Equal(20.0, buffer[1]);
            Assert.Equal(30.0, buffer[2]);

            // Overflow buffer beyond capacity of 5
            buffer.Add(40.0);
            buffer.Add(50.0);
            buffer.Add(60.0); // drops 10.0
            Assert.Equal(5, buffer.Count);
            Assert.Equal(20.0, buffer[0]);
            Assert.Equal(60.0, buffer[4]);

            var range = buffer.Range();
            Assert.NotNull(range);
            Assert.Equal(20.0, range.Value.Min);
            Assert.Equal(60.0, range.Value.Max);

            buffer.Clear();
            Assert.Equal(0, buffer.Count);
            Assert.Null(buffer.Range());
        }

        #endregion

        #region 7. Updater SHA256 Verification

        [Fact]
        public void Test_Updater_ExtractExpectedSha256_RegexPatterns()
        {
            string fakeJsonSha = """
            {
                "tag_name": "v2.0.0",
                "body": "Bugfixes and performance updates.\n\nSHA-256: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n"
            }
            """;

            using var doc = JsonDocument.Parse(fakeJsonSha);
            string? sha = Updater.ExtractExpectedSha256(doc.RootElement);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", sha);

            string fakeJsonChecksum = """
            {
                "tag_name": "v2.0.0",
                "body": "Checksum: AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899"
            }
            """;
            using var doc2 = JsonDocument.Parse(fakeJsonChecksum);
            string? sha2 = Updater.ExtractExpectedSha256(doc2.RootElement);
            Assert.Equal("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899", sha2);
        }

        [Fact]
        public void Test_Updater_ComputeFileSha256_And_VerifySha256()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"test_sha256_{Guid.NewGuid():N}.bin");
            try
            {
                byte[] testData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                File.WriteAllBytes(tempFile, testData);

                string expectedHash;
                using (var sha = SHA256.Create())
                {
                    expectedHash = Convert.ToHexString(sha.ComputeHash(testData)).ToLowerInvariant();
                }

                string actualHash = Updater.ComputeFileSha256(tempFile);
                Assert.Equal(expectedHash, actualHash);

                Assert.True(Updater.VerifySha256(tempFile, expectedHash));
                Assert.False(Updater.VerifySha256(tempFile, "0000000000000000000000000000000000000000000000000000000000000000"));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        #endregion

        #region 8. LightingEffects34 Structure & Manager

        [Fact]
        public void Test_LightingEffects34_CountAndEnumMapping()
        {
            Assert.Equal(34, LightingEffectsManager.EffectNames.Length);

            // Verify index 0 is Static and index 33 is Blasting
            Assert.Equal("Static", LightingEffectsManager.EffectNames[0]);
            Assert.Equal((int)LightingEffect34.Static, 0);

            Assert.Equal("Blasting", LightingEffectsManager.EffectNames[33]);
            Assert.Equal((int)LightingEffect34.Blasting, 33);

            Assert.Equal("Follow Power Mode", LightingEffectsManager.EffectNames[(int)LightingEffect34.FollowOperatingMode]);
        }

        #endregion

        #region 9. DeviceCapabilities & Overrides

        [Fact]
        public void Test_DeviceCapabilities_ImmutableDefaults()
        {
            var caps = DeviceCapabilities.None;
            Assert.False(caps.CoolBoost);
            Assert.False(caps.DustDefender);
            Assert.False(caps.HasThirdFan);
            Assert.False(caps.HasEcHid);
            Assert.True(caps.HasOperatingModes);
            Assert.Equal(4, caps.OperatingModes.Count);
        }

        [Fact]
        public void Test_CapabilityOverrides_ApplyToCapabilities()
        {
            var baseCaps = DeviceCapabilities.None;
            var overrides = new CapabilityOverrides
            {
                CoolBoost = true,
                ThirdFan = true,
                GpuModeSwitch = true
            };

            var resolved = overrides.Apply(baseCaps);

            Assert.True(resolved.CoolBoost);
            Assert.True(resolved.HasThirdFan);
            Assert.True(resolved.GpuModeSwitch);
        }

        #endregion

        #region 10. DiagnosticsDumper

        [Fact]
        public void Test_DiagnosticsDumper_GenerateReport_ContainsHeaders()
        {
            // Create dummy WmiController instance or use null-safe calls
            var caps = new DeviceCapabilities
            {
                CoolBoost = true,
                DustDefender = true,
                HasThirdFan = true,
                HasEcHid = true
            };

            using var wmi = new WmiController();
            string report = DiagnosticsDumper.GenerateReport(
                wmi, caps, (Cpu: 25.0, Gpu: 10.0), gpuAsleep: false, thirdFanRpm: 3200);

            Assert.Contains("PREDATOR CONTROL APP - DIAGNOSTIC SYSTEM DUMP", report);
            Assert.Contains("[HARDWARE IDENTITY & FIRMWARE]", report);
            Assert.Contains("[SMBIOS LOW-LEVEL ACER INTERFACES]", report);
            Assert.Contains("[HARDWARE CAPABILITIES & FEATURES]", report);
            Assert.Contains("[LIVE TELEMETRY]", report);
            Assert.Contains("Direct EC HID:          True", report);
            Assert.Contains("3rd / System Fan:       True", report);
            Assert.Contains("3200 RPM", report);
        }

        [Fact]
        public void Test_DiagnosticsDumper_GpuAsleep_GuardsD3Cold()
        {
            using var wmi = new WmiController();
            var caps = new DeviceCapabilities { HasThirdFan = true };
            string report = DiagnosticsDumper.GenerateReport(
                wmi, caps, (50, 0), gpuAsleep: true, thirdFanRpm: 2500);

            Assert.Contains("GPU Temperature:   Asleep (D3Cold)", report);
            Assert.Contains("GPU Power State:   D3Cold / Sleep", report);
        }

        #endregion

        #region 11. SecureNamedPipeIpc, FirmwareEvent & System Fan Tests

        [Fact]
        public void Test_SecureNamedPipeIpc_CreatePipeSecurity_SucceedsWithoutException()
        {
            var sec = SecureNamedPipeIpc.CreatePipeSecurity();
            Assert.NotNull(sec);
        }

        [Fact]
        public async Task Test_SecureNamedPipeIpc_ServerClientLoopback()
        {
            string? receivedMessage = null;
            using var server = new SecureNamedPipeIpc.PipeServer(msg => receivedMessage = msg);
            server.Start();

            await Task.Delay(200);

            bool sent = await SecureNamedPipeIpc.SendMessageWithVerificationAsync("SHOW", 3000);
            Assert.True(sent);

            var sw = Stopwatch.StartNew();
            while (receivedMessage == null && sw.ElapsedMilliseconds < 2000)
            {
                await Task.Delay(50);
            }

            Assert.Equal("SHOW", receivedMessage);
        }

        [Fact]
        public void Test_FirmwareEvent_Decode_ByteArray_ThermalDustDefender()
        {
            // Thermal event (6), Value = 1, Detail[2] = 1 (DustDefender running)
            byte[] payload = new byte[] { 6, 1, 1, 0 };
            var ev = FirmwareEvent.Decode(payload);
            Assert.NotNull(ev);
            Assert.Equal(FirmwareEventKind.Thermal, ev.Kind);
            Assert.Equal(1, ev.Value);
            Assert.True(ev.DustDefenderRunning);

            // Thermal event (6), Value = 1, Detail[2] = 0 (DustDefender stopped)
            byte[] payloadStopped = new byte[] { 6, 1, 0, 0 };
            var evStopped = FirmwareEvent.Decode(payloadStopped);
            Assert.NotNull(evStopped);
            Assert.False(evStopped.DustDefenderRunning);
        }

        [Fact]
        public void Test_FirmwareEvent_Decode_ByteArray_ModeKeyAndGeneric()
        {
            byte[] modeKeyPayload = new byte[] { 7, 1 };
            var evMode = FirmwareEvent.Decode(modeKeyPayload);
            Assert.NotNull(evMode);
            Assert.Equal(FirmwareEventKind.ModeKey, evMode.Kind);

            byte[] nsKeyPayload = new byte[] { 0, 5 };
            var evNs = FirmwareEvent.Decode(nsKeyPayload);
            Assert.NotNull(evNs);
            Assert.Equal((FirmwareEventKind)0, evNs.Kind);
            Assert.Equal(5, evNs.Value);
        }

        [Fact]
        public void Test_WmiController_SystemFanSpeed_PayloadEncoding()
        {
            using var wmi = new WmiController();
            wmi.SetSystemFanSpeed(65);
            Assert.Equal(65, wmi.CustomSystemFanSpeed);

            wmi.SetFanSpeed(40, 60, 80);
            Assert.Equal(40, wmi.CustomCpuFanSpeed);
            Assert.Equal(60, wmi.CustomGpuFanSpeed);
            Assert.Equal(80, wmi.CustomSystemFanSpeed);
        }

        [Fact]
        public void Test_FanLock_EnumValues()
        {
            Assert.True(Enum.IsDefined(typeof(FanLock), FanLock.QuietMode));
            Assert.True(Enum.IsDefined(typeof(FanLock), FanLock.EcoMode));
            Assert.True(Enum.IsDefined(typeof(FanLock), FanLock.DustDefender));
        }

        [Fact]
        public void Test_CapabilityOverrides_AllNewCapabilities()
        {
            var overrides = new CapabilityOverrides
            {
                CoolBoost = true,
                ThirdFan = true,
                DustDefender = true,
                FanTable = true,
                GpuModeSwitch = true,
                UsbCharging = true,
                BatteryCalibration = true
            };
            var caps = overrides.Apply(DeviceCapabilities.None);

            Assert.True(caps.CoolBoost);
            Assert.True(caps.HasThirdFan);
            Assert.True(caps.DustDefender);
            Assert.True(caps.FanTable);
            Assert.True(caps.GpuModeSwitch);
            Assert.True(caps.UsbCharging);
            Assert.True(caps.BatteryCalibration);
        }

        #endregion
    }
}
