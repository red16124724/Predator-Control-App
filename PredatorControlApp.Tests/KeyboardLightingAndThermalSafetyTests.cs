using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Coverage for the per-zone / per-key lighting layer, the power-mode
    /// switch guard and the optimized thermal safety coordinator.
    /// No real hardware or Acer service is touched: transports are injected.
    /// </summary>
    public class KeyboardLightingAndThermalSafetyTests
    {
        private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        #region Capability detector

        [Fact]
        public void Classify_PerKeyCandidate_WithService_EnablesPerKey()
        {
            var caps = KeyboardLightingDetector.Classify(
                "Predator PH16-71", "V1.15", "PH16-71", "fp-1",
                serviceReachable: true, wmiZonePath: true, tierOverride: 0);

            Assert.Equal(KeyboardLightingTier.PerKey, caps.Tier);
            Assert.True(caps.PerKeySupported);
            Assert.Equal(4, caps.ZoneCount);
            Assert.True(caps.LightingServiceReachable);
            Assert.True(caps.WmiZonePathAvailable);
            Assert.False(caps.VerifiedOnHardware); // never claimed without an acknowledged write
            Assert.Contains("PER-KEY", caps.TierLabel);
        }

        [Fact]
        public void Classify_PerKeyCandidate_WithoutService_DegradesToFourZone()
        {
            var caps = KeyboardLightingDetector.Classify(
                "Predator PH18-72", "V1.00", null, null,
                serviceReachable: false, wmiZonePath: true, tierOverride: 0);

            Assert.Equal(KeyboardLightingTier.FourZone, caps.Tier);
            Assert.False(caps.PerKeySupported);
            Assert.Contains("not reachable", caps.Notes, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Classify_PerKeyCandidate_IsCaseInsensitive_AndMatchesBoardProduct()
        {
            var fromModel = KeyboardLightingDetector.Classify(
                "predator ph16-71", null, null, null,
                serviceReachable: true, wmiZonePath: true, tierOverride: 0);
            Assert.Equal(KeyboardLightingTier.PerKey, fromModel.Tier);

            var fromBoard = KeyboardLightingDetector.Classify(
                "Generic Laptop", null, "PTX17-71", null,
                serviceReachable: true, wmiZonePath: false, tierOverride: 0);
            Assert.Equal(KeyboardLightingTier.PerKey, fromBoard.Tier);
        }

        [Fact]
        public void Classify_KnownFourZoneFamily_ReportsFamilyInNotes()
        {
            var caps = KeyboardLightingDetector.Classify(
                "Predator PHN16-71", null, null, null,
                serviceReachable: false, wmiZonePath: true, tierOverride: 0);

            Assert.Equal(KeyboardLightingTier.FourZone, caps.Tier);
            Assert.Contains("Helios Neo", caps.Notes);
        }

        [Fact]
        public void Classify_NoTransport_IsUnavailable()
        {
            var caps = KeyboardLightingDetector.Classify(
                "Unknown", null, null, null,
                serviceReachable: false, wmiZonePath: false, tierOverride: 0);

            Assert.Equal(KeyboardLightingTier.Unavailable, caps.Tier);
            Assert.Equal(0, caps.ZoneCount);
            Assert.False(caps.PerKeySupported);
        }

        [Fact]
        public void Classify_ForceFourZone_OverridesPerKeyCandidate()
        {
            var caps = KeyboardLightingDetector.Classify(
                "Predator PH16-71", null, null, null,
                serviceReachable: true, wmiZonePath: true, tierOverride: 1);

            Assert.Equal(KeyboardLightingTier.FourZone, caps.Tier);
            Assert.Contains("forced", caps.Notes, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Classify_ForcePerKey_IsExplicitlyExperimental()
        {
            var caps = KeyboardLightingDetector.Classify(
                "Unknown", null, null, null,
                serviceReachable: false, wmiZonePath: true, tierOverride: 2);

            Assert.Equal(KeyboardLightingTier.PerKey, caps.Tier);
            Assert.True(caps.PerKeySupported);
            Assert.Contains("Unverified", caps.Notes, StringComparison.OrdinalIgnoreCase);
            Assert.False(caps.VerifiedOnHardware);
        }

        [Fact]
        public void Classify_Summary_IncludesModelBiosAndTier()
        {
            var caps = KeyboardLightingDetector.Classify(
                "Predator PH16-71", "V1.15", null, null,
                serviceReachable: true, wmiZonePath: true, tierOverride: 0);

            Assert.Contains("Predator PH16-71", caps.Summary);
            Assert.Contains("V1.15", caps.Summary);
            Assert.Contains("PER-KEY RGB", caps.Summary);
        }

        #endregion

        #region Palette

        [Fact]
        public void Palette_SerializeParse_RoundTrips()
        {
            Color[] colors =
            {
                Color.FromArgb(255, 0, 0),
                Color.FromArgb(0, 255, 0),
                Color.FromArgb(0, 0, 255),
                Color.FromArgb(255, 255, 255)
            };

            string text = KeyboardLightingPalette.Serialize(colors);
            Color[] parsed = KeyboardLightingPalette.Parse(text, 4, Color.Black);

            Assert.Equal(colors, parsed);
            Assert.Equal("#FF0000,#00FF00,#0000FF,#FFFFFF", text);
        }

        [Fact]
        public void Palette_Parse_FillsMissingEntriesWithFallback()
        {
            Color[] parsed = KeyboardLightingPalette.Parse("#102030", 3, Color.Black);

            Assert.Equal(Color.FromArgb(255, 16, 32, 48), parsed[0]);
            Assert.Equal(Color.Black, parsed[1]);
            Assert.Equal(Color.Black, parsed[2]);
        }

        [Fact]
        public void Palette_Parse_SupportsShortHexAndNamedColors()
        {
            Color[] parsed = KeyboardLightingPalette.Parse("#abc,Red;bogus", 3, Color.Black);

            Assert.Equal(Color.FromArgb(255, 0xAA, 0xBB, 0xCC), parsed[0]);
            Assert.Equal(Color.Red, parsed[1]);
            Assert.Equal(Color.Black, parsed[2]); // invalid token keeps the fallback
        }

        [Fact]
        public void Palette_Parse_EmptyOrZeroCount_IsSafe()
        {
            Assert.Empty(KeyboardLightingPalette.Parse(null, 0));
            Assert.Equal(2, KeyboardLightingPalette.Parse(null, 2).Length);
            Assert.Equal(string.Empty, KeyboardLightingPalette.Serialize(Array.Empty<Color>()));
        }

        [Fact]
        public void Palette_Blend_ClampsAndInterpolates()
        {
            Color mid = KeyboardLightingPalette.Blend(Color.Black, Color.White, 0.5);
            Assert.Equal(128, mid.R);
            Assert.Equal(128, mid.G);
            Assert.Equal(128, mid.B);

            Assert.Equal(Color.Black.ToArgb(), KeyboardLightingPalette.Blend(Color.Black, Color.White, -1).ToArgb());
            Assert.Equal(Color.White.ToArgb(), KeyboardLightingPalette.Blend(Color.Black, Color.White, 2).ToArgb());
        }

        #endregion

        #region Controller gating and fallback

        private sealed class RecordingWriter
        {
            private readonly bool _result;
            public int Calls { get; private set; }
            public int LastBrightness { get; private set; }
            public string LastEffect { get; private set; } = string.Empty;

            public RecordingWriter(bool result) { _result = result; }

            public Task<bool> Write(
                int device,
                string effect,
                IReadOnlyList<(int Index, byte R, byte G, byte B)> leds,
                int brightnessPercent,
                int speedPercent,
                bool perKey)
            {
                Calls++;
                LastEffect = effect;
                LastBrightness = brightnessPercent;
                return Task.FromResult(_result);
            }
        }

        private static KeyboardLightingCapabilities Caps(KeyboardLightingTier tier)
            => new()
            {
                Tier = tier,
                PerKeySupported = tier == KeyboardLightingTier.PerKey,
                ZoneCount = tier == KeyboardLightingTier.Unavailable ? 0 : 4,
                KeyCount = KeyboardLayouts.AnsiTklKeyCount
            };

        [Fact]
        public async Task Controller_PerKeyGatedOff_DoesNotCallPerKeyTransport()
        {
            var writer = new RecordingWriter(true);
            using var controller = new KeyboardLightingController(null, Caps(KeyboardLightingTier.FourZone), writer.Write);

            var status = await controller.ApplyAsync(KeyboardLightingScope.PerKey);

            Assert.Equal(0, writer.Calls);
            Assert.False(status.Success);
            Assert.True(status.Degraded);
            Assert.Contains("No keyboard lighting transport", status.Message);
        }

        [Fact]
        public async Task Controller_PerKeyWriteSuccess_ReportsPerKeyTier()
        {
            var writer = new RecordingWriter(true);
            using var controller = new KeyboardLightingController(null, Caps(KeyboardLightingTier.PerKey), writer.Write);
            controller.Brightness = 100;

            var status = await controller.ApplyAsync(KeyboardLightingScope.PerKey);

            Assert.Equal(1, writer.Calls);
            Assert.Equal("STATIC", writer.LastEffect);
            Assert.True(status.Success);
            Assert.False(status.Degraded);
            Assert.Equal(KeyboardLightingTier.PerKey, status.AppliedTier);
        }

        [Fact]
        public async Task Controller_PerKeyWriteFailure_DegradesAndRepeatsFallback()
        {
            var writer = new RecordingWriter(false);
            using var controller = new KeyboardLightingController(null, Caps(KeyboardLightingTier.PerKey), writer.Write);

            var statuses = new List<KeyboardLightingStatus>();
            controller.StatusChanged += (_, s) => statuses.Add(s);

            var final = await controller.ApplyAsync(KeyboardLightingScope.PerKey);

            Assert.Equal(1, writer.Calls);
            Assert.Contains(statuses, s => s.Degraded && s.Message.Contains("Falling back", StringComparison.OrdinalIgnoreCase));
            Assert.False(final.Success);
            Assert.Equal(KeyboardLightingTier.Unavailable, final.AppliedTier);
        }

        [Fact]
        public void Controller_ZoneAndKeyState_IsBoundedAndCloned()
        {
            using var controller = new KeyboardLightingController(null, Caps(KeyboardLightingTier.FourZone));

            controller.SetZoneColor(-1, Color.Red);
            controller.SetZoneColor(4, Color.Red);
            controller.SetZoneColor(0, Color.Red);
            controller.SetAllZones(Color.Green);
            controller.SetZoneColor(3, Color.Blue);

            Assert.Equal(Color.Green, controller.GetZoneColor(0));
            Assert.Equal(Color.Blue, controller.GetZoneColor(3));
            Assert.Equal(Color.Black.ToArgb(), controller.GetZoneColor(9).ToArgb());

            var snapshot = controller.SnapshotZoneColors();
            snapshot[0] = Color.Yellow;
            Assert.Equal(Color.Green, controller.GetZoneColor(0)); // snapshot is a clone

            controller.SetKeyColor(-1, Color.Red);
            controller.SetKeyColor(KeyboardLayouts.AnsiTklKeyCount, Color.Red);
            controller.SetKeyColor(0, Color.Red);
            Assert.Equal(Color.Red, controller.SnapshotKeyColors()[0]);
        }

        [Fact]
        public void Controller_UpdateCapabilities_PreservesPaintedKeys()
        {
            using var controller = new KeyboardLightingController(null, Caps(KeyboardLightingTier.FourZone));
            controller.SetKeyColor(0, Color.Red);

            controller.UpdateCapabilities(new KeyboardLightingCapabilities
            {
                Tier = KeyboardLightingTier.PerKey,
                PerKeySupported = true,
                KeyCount = 4
            });

            Assert.Equal(4, controller.KeyCount);
            Assert.Equal(Color.Red, controller.SnapshotKeyColors()[0]);
            Assert.Equal(KeyboardLightingPalette.DefaultZoneColor.ToArgb(),
                controller.SnapshotKeyColors()[3].ToArgb());
        }

        [Fact]
        public void Controller_ClampsBrightnessAndSpeed()
        {
            using var controller = new KeyboardLightingController(null, Caps(KeyboardLightingTier.FourZone));

            controller.Brightness = -20;
            controller.Speed = 0;
            Assert.Equal(0, controller.Brightness);
            Assert.Equal(1, controller.Speed);

            controller.Brightness = 500;
            controller.Speed = 500;
            Assert.Equal(100, controller.Brightness);
            Assert.Equal(100, controller.Speed);
        }

        [Fact]
        public void ServicePayload_PerKey_ContainsLedEntriesAndSanitizedEffect()
        {
            var leds = new List<(int Index, byte R, byte G, byte B)>
            {
                (0, 255, 0, 0),
                (1, 0, 0, 255)
            };

            string json = AcerServiceClient.BuildKeyboardLightingPayload("wave; drop", leds, 100, 50, perKey: true);

            Assert.Contains("\"device\":1", json);
            Assert.Contains("\"effect\":\"WAVEDROP\"", json);
            Assert.Contains("{\"index\":0,\"color\":\"#FF0000\"}", json);
            Assert.Contains("{\"index\":1,\"color\":\"#0000FF\"}", json);
            Assert.Contains("\"brightness\":5", json);
        }

        [Fact]
        public void ServicePayload_ZoneMode_SendsEmptyLedArray()
        {
            string json = AcerServiceClient.BuildKeyboardLightingPayload("static", null, 0, 1, perKey: false);

            Assert.Contains("\"LEDs\":[]", json);
            Assert.Contains("\"brightness\":0", json);
            Assert.Contains("\"effect\":\"STATIC\"", json);
        }

        #endregion

        #region Power mode switch guard

        [Fact]
        public void Guard_LatestWins_DropsIntermediateModes()
        {
            var applied = new List<byte>();
            int blocked = 0;
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            using var guard = new PowerModeSwitchGuard(mode =>
            {
                lock (applied) applied.Add(mode);
                if (mode == 1 && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
                {
                    entered.Set();
                    release.Wait(5000);
                }
                return true;
            }, minIntervalMs: 0, settleMs: 0);

            guard.Request(1);
            Assert.True(entered.Wait(5000), "first hardware write never started");

            guard.Request(2);
            guard.Request(3);
            release.Set();

            Assert.True(WaitUntil(() => guard.CompletedCount == 2), "guard did not drain the queue");
            byte[] snapshot;
            lock (applied) snapshot = applied.ToArray();

            Assert.Equal(new byte[] { 1, 3 }, snapshot);
            Assert.Equal((byte)3, guard.LastAppliedMode);
            Assert.Equal(0L, guard.FailedCount);
        }

        [Fact]
        public void Guard_DuplicatePendingRequest_IsCoalesced()
        {
            var applied = new List<byte>();
            int blocked = 0;
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            using var guard = new PowerModeSwitchGuard(mode =>
            {
                lock (applied) applied.Add(mode);
                if (mode == 1 && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
                {
                    entered.Set();
                    release.Wait(5000);
                }
                return true;
            }, minIntervalMs: 0, settleMs: 0);

            guard.Request(1);
            Assert.True(entered.Wait(5000));

            guard.Request(2);
            guard.Request(2); // duplicate while pending
            release.Set();

            Assert.True(WaitUntil(() => guard.CompletedCount == 2));
            byte[] snapshot;
            lock (applied) snapshot = applied.ToArray();

            Assert.Equal(new byte[] { 1, 2 }, snapshot);
            Assert.True(guard.CoalescedCount >= 2);
        }

        [Fact]
        public void Guard_Failure_IsCountedAndReported()
        {
            using var guard = new PowerModeSwitchGuard(_ => false, minIntervalMs: 0, settleMs: 0);
            PowerModeSwitchResult? result = null;
            guard.SwitchCompleted += (_, r) => result = r;

            guard.Request(5);

            Assert.True(WaitUntil(() => guard.FailedCount == 1));
            Assert.Equal(0L, guard.CompletedCount);
            Assert.NotNull(result);
            Assert.False(result!.Value.Success);
            Assert.Equal((byte)5, result.Value.Mode);
        }

        [Fact]
        public void Guard_MinimumInterval_SpacesHardwareWrites()
        {
            var stamps = new List<DateTime>();
            using var guard = new PowerModeSwitchGuard(_ =>
            {
                lock (stamps) stamps.Add(DateTime.UtcNow);
                return true;
            }, minIntervalMs: 400, settleMs: 0);

            guard.Request(1);
            Assert.True(WaitUntil(() => guard.CompletedCount == 1));
            guard.Request(2);
            Assert.True(WaitUntil(() => guard.CompletedCount == 2), "second write never completed");

            TimeSpan delta;
            lock (stamps) delta = stamps[1] - stamps[0];
            Assert.True(delta.TotalMilliseconds >= 300,
                $"writes were only {delta.TotalMilliseconds:0} ms apart");
        }

        [Fact]
        public void Guard_Dispose_BlocksFurtherRequests()
        {
            int calls = 0;
            var guard = new PowerModeSwitchGuard(_ => { Interlocked.Increment(ref calls); return true; },
                minIntervalMs: 0, settleMs: 0);
            guard.Dispose();

            guard.Request(1);
            Thread.Sleep(150);

            Assert.Equal(0, Volatile.Read(ref calls));
            Assert.Null(guard.PendingMode);
        }

        #endregion

        #region Thermal safety

        [Fact]
        public void ThermalProfile_Normalize_RepairsInvertedAndOutOfRangeValues()
        {
            var profile = new ThermalSafetyProfile
            {
                CpuWarnCelsius = 95,
                CpuCeilingCelsius = 80,
                CpuCriticalCelsius = 70,
                HysteresisCelsius = 99,
                MaxFanFloorPercent = 5,
                CpuPowerCapWatts = 999,
                MinPowerCapWatts = 1
            }.Normalize();

            Assert.True(profile.CpuWarnCelsius < profile.CpuCeilingCelsius);
            Assert.True(profile.CpuCeilingCelsius <= profile.CpuCriticalCelsius);
            Assert.InRange(profile.HysteresisCelsius, 0, 15);
            Assert.Equal(40, profile.MaxFanFloorPercent);
            Assert.Equal(250, profile.CpuPowerCapWatts);
            Assert.Equal(5, profile.MinPowerCapWatts);
        }

        [Fact]
        public void ThermalProfile_NaN_UsesSafeDefaults()
        {
            var profile = new ThermalSafetyProfile
            {
                CpuWarnCelsius = double.NaN,
                HysteresisCelsius = double.NaN
            }.Normalize();

            Assert.Equal(75, profile.CpuWarnCelsius);
            Assert.Equal(95, profile.CpuCeilingCelsius);
            Assert.Equal(100, profile.CpuCriticalCelsius);
            Assert.Equal(3, profile.HysteresisCelsius);
        }

        [Fact]
        public void ThermalProfile_ToLimits_MatchesDefaultTriplets()
        {
            var profile = ThermalSafetyProfile.Default;

            Assert.Equal(75, profile.ToCpuLimits().ThrottleThresholdCelsius);
            Assert.Equal(95, profile.ToCpuLimits().CeilingThresholdCelsius);
            Assert.Equal(100, profile.ToCpuLimits().CriticalThresholdCelsius);
            Assert.Equal(70, profile.ToGpuLimits().ThrottleThresholdCelsius);
            Assert.Equal(87, profile.ToGpuLimits().CeilingThresholdCelsius);
            Assert.Equal(90, profile.ToGpuLimits().CriticalThresholdCelsius);
        }

        [Fact]
        public void Coordinator_Disabled_ReturnsIdleDecision()
        {
            var coordinator = new ThermalSafetyCoordinator(new ThermalSafetyProfile { Enabled = false });
            int criticalEvents = 0;
            coordinator.CriticalEntered += (_, _) => criticalEvents++;

            var decision = coordinator.Evaluate(120, 120);

            Assert.False(decision.Active);
            Assert.False(decision.Critical);
            Assert.Equal(0, decision.Stage);
            Assert.Null(decision.CpuPowerCapWatts);
            Assert.Equal(0, criticalEvents);
        }

        [Fact]
        public void Coordinator_WarnStage_RaisesFloorAndPowerCap()
        {
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);

            ThermalDecision decision = ThermalDecision.Idle;
            for (int i = 0; i < 12; i++) decision = coordinator.Evaluate(80, 0);

            Assert.Equal(1, decision.Stage);
            Assert.True(decision.Active);
            Assert.InRange(decision.CpuFanFloor, 1, 36);
            Assert.NotNull(decision.CpuPowerCapWatts);
            Assert.InRange(decision.CpuPowerCapWatts!.Value, 15, 45);
        }

        [Fact]
        public void Coordinator_CeilingStage_PinsMaxFloorAndCeilingCap()
        {
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);

            ThermalDecision decision = ThermalDecision.Idle;
            for (int i = 0; i < 12; i++) decision = coordinator.Evaluate(95, 0);

            Assert.Equal(2, decision.Stage);
            Assert.True(decision.Active);
            Assert.False(decision.Critical);
            Assert.Equal(100, decision.CpuFanFloor);
            Assert.Equal(45, decision.CpuPowerCapWatts);
        }

        [Fact]
        public void Coordinator_CriticalStage_PinsFansAndFloorsPowerOnce()
        {
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);
            int criticalEvents = 0;
            coordinator.CriticalEntered += (_, _) => criticalEvents++;

            var decision = coordinator.Evaluate(100, 0);

            Assert.Equal(3, decision.Stage);
            Assert.True(decision.Critical);
            Assert.Equal("CRITICAL", decision.StageLabel);
            Assert.Equal(100, decision.CpuFanFloor);
            Assert.Equal(15, decision.CpuPowerCapWatts);
            Assert.Equal(1, criticalEvents);
            Assert.Contains("critical", decision.Reason, StringComparison.OrdinalIgnoreCase);

            coordinator.Evaluate(100, 0);
            Assert.Equal(1, criticalEvents); // no alert storm
        }

        [Fact]
        public void Coordinator_Hysteresis_HoldsFloorThenReleases()
        {
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);

            ThermalDecision decision = ThermalDecision.Idle;
            for (int i = 0; i < 12; i++) decision = coordinator.Evaluate(80, 0);
            Assert.Equal(1, decision.Stage);
            Assert.True(decision.CpuFanFloor > 0);

            for (int i = 0; i < 40; i++) decision = coordinator.Evaluate(73, 0);
            Thread.Sleep(320);
            decision = coordinator.Evaluate(73, 0);
            Assert.Equal(1, decision.Stage);
            Assert.Equal(15, decision.CpuFanFloor); // held inside the hysteresis band

            for (int i = 0; i < 40; i++) decision = coordinator.Evaluate(60, 0);
            Assert.Equal(0, decision.Stage);
            Assert.False(decision.Active);
            Assert.Equal(0, decision.CpuFanFloor);
        }

        [Fact]
        public void Coordinator_Ema_IgnoresSingleHotSample()
        {
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);

            for (int i = 0; i < 8; i++) coordinator.Evaluate(50, 0);
            var decision = coordinator.Evaluate(100, 0);

            Assert.Equal(0, decision.Stage);
            Assert.False(decision.Active);
            Assert.Equal(0, decision.CpuFanFloor);
            Assert.Null(decision.CpuPowerCapWatts);
        }

        [Fact]
        public void Coordinator_InvalidSamples_AreIgnored()
        {
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);

            Assert.Equal(0, coordinator.Evaluate(double.NaN, double.PositiveInfinity).Stage);
            Assert.Equal(0, coordinator.Evaluate(-5, 0).Stage);
            Assert.Equal(0, coordinator.Evaluate(0, 0).Stage);
            Assert.Equal(0, coordinator.Evaluate(500, 500).Stage);
        }

        [Fact]
        public void Coordinator_RateLimitsFloorRise()
        {
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);

            var first = coordinator.Evaluate(80, 0);
            var second = coordinator.Evaluate(80, 0);
            Assert.Equal(12, first.CpuFanFloor);
            Assert.Equal(12, second.CpuFanFloor); // within the 250 ms step interval

            Thread.Sleep(320);
            var third = coordinator.Evaluate(80, 0);
            Assert.Equal(24, third.CpuFanFloor);

            Thread.Sleep(320);
            var fourth = coordinator.Evaluate(80, 0);
            Assert.Equal(36, fourth.CpuFanFloor); // warn..ceiling target at 80 C
        }

        [Fact]
        public void Coordinator_ProfileChange_DisablingResetsState()
        {
            var coordinator = new ThermalSafetyCoordinator(ThermalSafetyProfile.Default);
            coordinator.Evaluate(100, 0);
            Assert.True(coordinator.LastDecision.Critical);

            coordinator.Profile = new ThermalSafetyProfile { Enabled = false };

            var decision = coordinator.Evaluate(100, 0);
            Assert.Equal(0, decision.Stage);
            Assert.Equal(0, decision.CpuFanFloor);
            Assert.False(decision.Active);
        }

        [Fact]
        public void AppSettings_ThermalMapping_RoundTripsWithoutSaving()
        {
            var settings = new AppSettings();
            var profile = settings.ToThermalProfile();

            Assert.Equal(75, profile.CpuWarnCelsius);
            Assert.Equal(95, profile.CpuCeilingCelsius);
            Assert.Equal(100, profile.CpuCriticalCelsius);

            settings.ApplyThermalProfile(new ThermalSafetyProfile
            {
                Enabled = false,
                CpuWarnCelsius = 70,
                CpuCeilingCelsius = 90,
                CpuCriticalCelsius = 98,
                HysteresisCelsius = 5,
                MaxFanFloorPercent = 80,
                CpuPowerCapWatts = 60,
                MinPowerCapWatts = 20,
                CriticalAlertEnabled = false
            });

            Assert.False(settings.AntiThrottleEnabled);
            Assert.Equal(70, settings.ThermalCpuWarnCelsius);
            Assert.Equal(90, settings.ThermalCpuCeilingCelsius);
            Assert.Equal(98, settings.ThermalCpuCriticalCelsius);
            Assert.Equal(5, settings.ThermalHysteresisCelsius);
            Assert.Equal(80, settings.ThermalMaxFanFloorPercent);
            Assert.Equal(60, settings.ThermalCpuPowerCapWatts);
            Assert.Equal(20, settings.ThermalMinPowerCapWatts);
            Assert.False(settings.ThermalCriticalAlertEnabled);
        }

        #endregion

        #region Layout sanity

        [Fact]
        public void Layout_AnsiTkl_HasStableGeometryAndLabels()
        {
            Assert.Equal(KeyboardLayouts.AnsiTklKeyCount, KeyboardLayouts.AnsiTkl.Count);
            Assert.True(KeyboardLayouts.MaxRow >= 5);
            Assert.Equal(KeyboardLayouts.AnsiTkl, KeyboardLayouts.Get("unknown-layout"));
            Assert.All(KeyboardLayouts.AnsiTkl, key =>
            {
                Assert.False(string.IsNullOrWhiteSpace(key.Label));
                Assert.True(key.WidthUnits > 0);
                Assert.InRange(key.Row, 0, KeyboardLayouts.MaxRow);
            });
        }

        #endregion
        #region Per-Zone RGB & Multi-Tier Lighting Tests

        [Fact]
        public void Classify_ThreeZoneCandidate_Nitro5_DetectsThreeZone()
        {
            var caps = KeyboardLightingDetector.Classify(
                "Nitro AN515-54", "V1.33", null, null,
                serviceReachable: false, wmiZonePath: true, tierOverride: 0);

            Assert.Equal(KeyboardLightingTier.ThreeZone, caps.Tier);
            Assert.Equal(3, caps.ZoneCount);
            Assert.False(caps.PerKeySupported);
            Assert.Equal("3-ZONE RGB", caps.TierLabel);
            Assert.Contains("Nitro 5", caps.Notes);
        }

        [Fact]
        public void Classify_ThreeZoneCandidate_PredatorHelios300_DetectsThreeZone()
        {
            var caps = KeyboardLightingDetector.Classify(
                "Predator PH315-52", "V1.12", null, null,
                serviceReachable: false, wmiZonePath: true, tierOverride: 0);

            Assert.Equal(KeyboardLightingTier.ThreeZone, caps.Tier);
            Assert.Equal(3, caps.ZoneCount);
            Assert.Equal("3-ZONE RGB", caps.TierLabel);
        }

        [Fact]
        public void Classify_SingleZoneCandidate_DetectsSingleZone()
        {
            var caps = KeyboardLightingDetector.Classify(
                "Nitro AN515-31", "V1.05", null, null,
                serviceReachable: false, wmiZonePath: true, tierOverride: 0);

            Assert.Equal(KeyboardLightingTier.SingleZone, caps.Tier);
            Assert.Equal(1, caps.ZoneCount);
            Assert.False(caps.PerKeySupported);
            Assert.Equal("1-ZONE RGB", caps.TierLabel);
            Assert.Contains("Single-Zone", caps.Notes);
        }

        [Fact]
        public void Classify_TierOverrides_SingleAndThreeZone()
        {
            var single = KeyboardLightingDetector.Classify(
                "Generic Laptop", null, null, null,
                serviceReachable: true, wmiZonePath: false, tierOverride: 3);
            Assert.Equal(KeyboardLightingTier.SingleZone, single.Tier);
            Assert.Equal(1, single.ZoneCount);

            var three = KeyboardLightingDetector.Classify(
                "Generic Laptop", null, null, null,
                serviceReachable: true, wmiZonePath: false, tierOverride: 4);
            Assert.Equal(KeyboardLightingTier.ThreeZone, three.Tier);
            Assert.Equal(3, three.ZoneCount);
        }

        [Fact]
        public void ServicePayload_ZoneLighting_BuildsCorrectPayloadWithColortype2()
        {
            var colors = new[]
            {
                Color.Red,
                Color.FromArgb(255, 0, 255, 0),
                Color.Blue,
                Color.Yellow
            };

            string json = AcerServiceClient.BuildZoneLightingPayload(colors, 100, 50);

            Assert.Contains("\"colortype\":2", json);
            Assert.Contains("\"effect\":\"STATIC\"", json);
            Assert.Contains("\"device\":1", json);
            Assert.Contains("FF0000", json);
            Assert.Contains("00FF00", json);
            Assert.Contains("0000FF", json);
            Assert.Contains("FFFF00", json);
        }

        [Fact]
        public async Task Controller_ApplyZones_DualDispatchesToZoneWriter()
        {
            IReadOnlyList<Color>? dispatchedZones = null;
            int dispatchedBrightness = 0;
            int dispatchedSpeed = 0;
            int writeCount = 0;

            KeyboardZoneWriter zoneWriter = (zones, brightness, speed, ct) =>
            {
                writeCount++;
                dispatchedZones = zones;
                dispatchedBrightness = brightness;
                dispatchedSpeed = speed;
                return Task.FromResult(true);
            };

            var caps = new KeyboardLightingCapabilities
            {
                Tier = KeyboardLightingTier.FourZone,
                ZoneCount = 4,
                PerKeySupported = false
            };

            using var controller = new KeyboardLightingController(null, caps, null, zoneWriter);
            controller.Brightness = 80;
            controller.Speed = 60;
            controller.SetZoneColor(0, Color.Red);
            controller.SetZoneColor(1, Color.Green);
            controller.SetZoneColor(2, Color.Blue);
            controller.SetZoneColor(3, Color.White);

            var status = await controller.ApplyAsync(KeyboardLightingScope.Zones);

            Assert.True(status.Success);
            Assert.Equal(1, writeCount);
            Assert.NotNull(dispatchedZones);
            Assert.Equal(4, dispatchedZones.Count);
            Assert.Equal(Color.Red.ToArgb(), dispatchedZones[0].ToArgb());
            Assert.Equal(80, dispatchedBrightness);
            Assert.Equal(60, dispatchedSpeed);
            Assert.Equal(KeyboardLightingTier.FourZone, status.AppliedTier);
        }

        [Fact]
        public async Task Controller_ThreeZone_DispatchesThreeZones()
        {
            IReadOnlyList<Color>? dispatchedZones = null;
            KeyboardZoneWriter zoneWriter = (zones, brightness, speed, ct) =>
            {
                dispatchedZones = zones;
                return Task.FromResult(true);
            };

            var caps = new KeyboardLightingCapabilities
            {
                Tier = KeyboardLightingTier.ThreeZone,
                ZoneCount = 3,
                PerKeySupported = false
            };

            using var controller = new KeyboardLightingController(null, caps, null, zoneWriter);
            controller.SetZoneColor(0, Color.Red);
            controller.SetZoneColor(1, Color.Green);
            controller.SetZoneColor(2, Color.Blue);

            var status = await controller.ApplyAsync(KeyboardLightingScope.Zones);

            Assert.True(status.Success);
            Assert.NotNull(dispatchedZones);
            Assert.Equal(3, dispatchedZones.Count);
            Assert.Equal(Color.Red.ToArgb(), dispatchedZones[0].ToArgb());
            Assert.Equal(Color.Green.ToArgb(), dispatchedZones[1].ToArgb());
            Assert.Equal(Color.Blue.ToArgb(), dispatchedZones[2].ToArgb());
            Assert.Equal(KeyboardLightingTier.ThreeZone, status.AppliedTier);
        }

        [Fact]
        public async Task Controller_SingleZone_DispatchesSingleZone()
        {
            IReadOnlyList<Color>? dispatchedZones = null;
            KeyboardZoneWriter zoneWriter = (zones, brightness, speed, ct) =>
            {
                dispatchedZones = zones;
                return Task.FromResult(true);
            };

            var caps = new KeyboardLightingCapabilities
            {
                Tier = KeyboardLightingTier.SingleZone,
                ZoneCount = 1,
                PerKeySupported = false
            };

            using var controller = new KeyboardLightingController(null, caps, null, zoneWriter);
            controller.SetZoneColor(0, Color.Cyan);

            var status = await controller.ApplyAsync(KeyboardLightingScope.Zones);

            Assert.True(status.Success);
            Assert.NotNull(dispatchedZones);
            Assert.Single(dispatchedZones);
            Assert.Equal(Color.Cyan.ToArgb(), dispatchedZones[0].ToArgb());
            Assert.Equal(KeyboardLightingTier.SingleZone, status.AppliedTier);
        }

        [Fact]
        public void WmiController_SetZoneColors_SingleZoneBroadcast()
        {
            using var wmi = new WmiController();
            wmi.CommandInterceptor = (method, input) => { };
            bool result = wmi.SetZoneColors(new[] { Color.Red });
            Assert.True(result);
        }

        [Fact]
        public void WmiController_SetZoneColors_ThreeZone()
        {
            using var wmi = new WmiController();
            wmi.CommandInterceptor = (method, input) => { };
            bool result = wmi.SetZoneColors(new[] { Color.Red, Color.Green, Color.Blue });
            Assert.True(result);
        }

        [Fact]
        public void WmiController_SetZoneColors_FourZone()
        {
            using var wmi = new WmiController();
            wmi.CommandInterceptor = (method, input) => { };
            bool result = wmi.SetZoneColors(new[] { Color.Red, Color.Green, Color.Blue, Color.Yellow });
            Assert.True(result);
        }





        #endregion
        #region Full Keyboard & Numpad Layout Tests

        [Fact]
        public void Layout_FullAnsi_HasValidGeometryAndNumpadKeys()
        {
            Assert.Equal(KeyboardLayouts.FullAnsiKeyCount, KeyboardLayouts.FullAnsi.Count);
            Assert.True(KeyboardLayouts.FullAnsiWidthUnits >= 23.0 && KeyboardLayouts.FullAnsiWidthUnits <= 24.0);

            // Dedicated keys check
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "PredatorSense");
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "Mute");
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "NumLk");

            // Numpad numerals
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "7" && k.Row == 2);
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "8" && k.Row == 2);
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "9" && k.Row == 2);
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "4" && k.Row == 3);
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "5" && k.Row == 3);
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "6" && k.Row == 3);
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "1" && k.Row == 4);
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "2" && k.Row == 4);
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "3" && k.Row == 4);
            Assert.Contains(KeyboardLayouts.FullAnsi, k => k.Label == "0" && k.Row == 5 && k.WidthUnits == 2.0);

            Assert.All(KeyboardLayouts.FullAnsi, key =>
            {
                Assert.False(string.IsNullOrWhiteSpace(key.Label));
                Assert.True(key.WidthUnits > 0);
                Assert.InRange(key.Row, 0, KeyboardLayouts.MaxRow);
            });
        }

        [Fact]
        public void Layout_Get_ResolvesDefaultAndModels()
        {
            Assert.Equal(KeyboardLayouts.FullAnsi, KeyboardLayouts.Get(null));
            Assert.Equal(KeyboardLayouts.FullAnsi, KeyboardLayouts.Get(""));
            Assert.Equal(KeyboardLayouts.FullAnsi, KeyboardLayouts.Get(KeyboardLayouts.FullAnsiId));
            Assert.Equal(KeyboardLayouts.AnsiTkl, KeyboardLayouts.Get(KeyboardLayouts.AnsiTklId));

            // Model resolution
            Assert.Equal(KeyboardLayouts.AnsiTkl, KeyboardLayouts.Get("PHN14-51"));
            Assert.Equal(KeyboardLayouts.AnsiTkl, KeyboardLayouts.Get("PT14-51"));
            Assert.Equal(KeyboardLayouts.FullAnsi, KeyboardLayouts.Get("PH16-71"));
            Assert.Equal(KeyboardLayouts.FullAnsi, KeyboardLayouts.Get("PH18-72"));
        }

        [Theory]
        [InlineData("Predator Helios Neo 14 PHN14-51", false)]
        [InlineData("Predator Triton 14 PT14-51", false)]
        [InlineData("PT314-51s", false)]
        [InlineData("PT314-52s", false)]
        [InlineData("Acer Nitro 14 AMD (2024)", false)]
        [InlineData("AN14-41", false)]
        [InlineData("AN14-51", false)]
        [InlineData("ANV14-61", false)]
        [InlineData("Predator Helios 16 PH16-71", true)]
        [InlineData("Predator Helios 18 PH18-72", true)]
        [InlineData("Predator Triton 17 X PTX17-71", true)]
        [InlineData("Predator Helios 300 PH315-53", true)]
        [InlineData("PH317-55", true)]
        [InlineData("Acer Nitro 16 AN16-41", true)]
        [InlineData("Acer Nitro 17 AN17-51", true)]
        [InlineData("ANV15-51", true)]
        [InlineData("ANV16-41", true)]
        public void AcerModelProfile_HasFullKeyboardWithNumpad_MatchesSize(string model, bool expectedFullKeyboard)
        {
            var profile = AcerLaptopModelCatalog.Lookup(model);
            Assert.Equal(expectedFullKeyboard, profile.HasFullKeyboardWithNumpad);
        }

        [Fact]
        public void KeyboardRgbMap_DynamicLayoutSwitching_AndHitTesting()
        {
            using var map = new KeyboardRgbMap(KeyboardLayouts.AnsiTklId);
            map.Size = new Size(800, 200);
            Assert.Equal(KeyboardLayouts.AnsiTklKeyCount, map.KeyCount);

            // Set color on key 0 (Esc)
            map.SetColor(0, Color.Red);
            Assert.Equal(Color.Red.ToArgb(), map.Colors[0].ToArgb());

            // Switch to FullAnsi
            map.SetLayout(KeyboardLayouts.FullAnsi, KeyboardLayouts.FullAnsiWidthUnits);
            Assert.Equal(KeyboardLayouts.FullAnsiKeyCount, map.KeyCount);
            Assert.Equal(KeyboardLayouts.FullAnsiWidthUnits, map.TotalWidthUnits);
            Assert.Equal(Color.Red.ToArgb(), map.Colors[0].ToArgb()); // Preserved

            // Hit test key 0
            int hit0 = map.KeyIndexAt(new Point(10, 10));
            Assert.Equal(0, hit0);

            // Switch back via layout ID
            map.SetLayout(KeyboardLayouts.AnsiTklId);
            Assert.Equal(KeyboardLayouts.AnsiTklKeyCount, map.KeyCount);
            Assert.Equal(KeyboardLayouts.AnsiTklWidthUnits, map.TotalWidthUnits);
        }

        #endregion
    }
}
