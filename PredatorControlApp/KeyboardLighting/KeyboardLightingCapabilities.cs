using System;

using System.Collections.Generic;

using System.Linq;

using System.Text.RegularExpressions;

using System.Threading;

using System.Threading.Tasks;



namespace PredatorControlApp

{

    /// <summary>

    /// What the app believes this machine's keyboard can do.

    /// Tier is learned from SMBIOS/BIOS identity plus a live probe of the Acer

    /// Lighting Service. It is never assumed from the app's own defaults.

    /// </summary>

    public enum KeyboardLightingTier

    {

        Unavailable = 0,

        SingleZone = 1,

        OneZone = 1,

        ThreeZone = 2,

        FourZone = 3,

        PerKey = 4

    }



    public sealed record KeyboardLightingCapabilities

    {

        public KeyboardLightingTier Tier { get; init; } = KeyboardLightingTier.FourZone;

        public int ZoneCount { get; init; } = 4;

        public bool PerKeySupported { get; init; }


        public bool HasRearLightbar { get; init; }

        public string ModelMarketingName { get; init; } = string.Empty;

        public AcerModelProfile? ModelProfile { get; init; }

        public string LayoutId { get; init; } = KeyboardLayouts.AnsiTklId;

        public int KeyCount { get; init; } = KeyboardLayouts.AnsiTklKeyCount;

        public string Model { get; init; } = string.Empty;

        public string BiosVersion { get; init; } = string.Empty;

        public string BoardProduct { get; init; } = string.Empty;

        public string SmbiosFingerprint { get; init; } = string.Empty;

        public bool LightingServiceReachable { get; init; }

        public bool WmiZonePathAvailable { get; init; }

        public string DetectionSource { get; init; } = "none";

        public string Notes { get; init; } = string.Empty;

        /// <summary>

        /// True only when a per-key write was positively acknowledged on this

        /// exact machine. Without that, per-key remains an experimental path.

        /// </summary>

        public bool VerifiedOnHardware { get; init; }

        public DateTime DetectedAtUtc { get; init; } = DateTime.UtcNow;



        public string TierLabel => Tier switch

        {

            KeyboardLightingTier.PerKey => "PER-KEY RGB",

            KeyboardLightingTier.FourZone => "4-ZONE RGB",

            KeyboardLightingTier.ThreeZone => "3-ZONE RGB",

            KeyboardLightingTier.SingleZone => "1-ZONE RGB",

            _ => "UNAVAILABLE"

        };



        public string Summary

        {

            get

            {

                string model = string.IsNullOrWhiteSpace(Model) ? "Unknown model" : Model.Trim();

                string bios = string.IsNullOrWhiteSpace(BiosVersion) ? "BIOS unknown" : BiosVersion.Trim();

                return $"{model} | {bios} | {TierLabel}";

            }

        }

    }



    /// <summary>

    /// Reads platform identity from firmware/BIOS sources and decides the

    /// highest lighting tier the machine can safely accept.

    /// </summary>

    public static class KeyboardLightingDetector

    {

        /// <summary>

        /// Models whose retail specification lists per-key RGB. This table is a

        /// candidate filter, not proof: the Acer Lighting Service probe must also

        /// succeed before the per-key path is enabled.

        /// </summary>

        private static readonly (string Pattern, string Family)[] PerKeyCandidates =

        {

            (@"\bPH16-7[12]\b", "Predator Helios 16"),

            (@"\bPH18-7[12]\b", "Predator Helios 18"),

            (@"\bPTX17-7[12]\b", "Predator Triton 17 X"),

            (@"\bPT516-5[12]s?\b", "Predator Triton 500 SE"),

            (@"\bPT517-5[12]\b", "Predator Triton 500 SE"),

            (@"\bPT16-5[12]\b", "Predator Triton 16")

        };



        private static readonly (string Pattern, string Family)[] ThreeZoneCandidates =
        {
            (@"\bAN515[-_]54\b", "Nitro 5 (3-Zone)"),
            (@"\bAN517[-_]51\b", "Nitro 5 (3-Zone)"),
            (@"\bPH315[-_]52\b", "Predator Helios 300 (3-Zone)"),
            (@"\bPH317[-_]5[23]\b", "Predator Helios 300 (3-Zone)")
        };

        private static readonly (string Pattern, string Family)[] SingleZoneCandidates =
        {
            (@"\bAN515[-_]3[1-9]\b", "Nitro 5 (Single-Zone)"),
            (@"\bAN515[-_]4[1-3]\b", "Nitro 5 (Single-Zone)"),
            (@"\bAN515[-_]5[1-3]\b", "Nitro 5 (Single-Zone)"),
            (@"\bANV14[-_].*", "Nitro V 14 (Single-Zone)"),
            (@"\bANV15[-_].*", "Nitro V 15 (Single-Zone)"),
            (@"\bANV16[-_].*", "Nitro V 16 (Single-Zone)"),
            (@"\bA715-[0-9]{2}\b", "Aspire 7 (Single-Zone)"),
            (@"\bPH315-50\b", "Predator Helios 300 (Single-Zone)"),
            (@"\bPH317-50\b", "Predator Helios 300 (Single-Zone)"),
            (@"\bG3-57[12]\b", "Predator Helios 300 (Single-Zone)")
        };

        private static readonly (string Pattern, string Family)[] OneZoneCandidates = SingleZoneCandidates;

        private static readonly (string Pattern, string Family)[] FourZoneCandidates =
        {
            (@"\bPHN14[-_]51\b", "Predator Helios Neo 14"),
            (@"\bPHN16[-_]7[123]\b", "Predator Helios Neo 16"),
            (@"\bPHN18[-_]7[12]\b", "Predator Helios Neo 18"),
            (@"\bPH315[-_]5[345]\b", "Predator Helios 300"),
            (@"\bPH317[-_]5[456]\b", "Predator Helios 300"),
            (@"\bPT315[-_]5[23]\b", "Predator Triton 300"),
            (@"\bAN515[-_]4[4-7]\b", "Nitro 5"),
            (@"\bAN515[-_]5[5-8]\b", "Nitro 5"),
            (@"\bAN517[-_]4[12]\b", "Nitro 5"),
            (@"\bAN517[-_]5[2-5]\b", "Nitro 5"),
            (@"\bAN14[-_].*", "Nitro 14"),
            (@"\bAN16[-_].*", "Nitro 16"),
            (@"\bAN17[-_].*", "Nitro 17"),
            (@"\bAN18[-_].*", "Nitro 18")
        };

        /// <summary>

        public static KeyboardLightingCapabilities Detect(WmiController? wmi = null, int tierOverride = 0)

        {

            string model = string.Empty;

            string bios = string.Empty;

            string board = string.Empty;

            string fingerprint = string.Empty;

            string source = "registry";



            try

            {

                var identity = BiosUefiPowerScanner.ReadPlatformIdentity();

                model = identity.SystemProductName;

                bios = identity.BiosVersion;

                board = identity.BaseBoardProduct;

                fingerprint = identity.HardwareFingerprint;

                source = "smbios+bios-registry";

            }

            catch

            {

                try

                {

                    model = HardwareIdentity.ReadModel();

                    bios = HardwareIdentity.ReadBiosVersion();

                    source = "win32-bios";

                }

                catch { }

            }



            if (string.IsNullOrWhiteSpace(model))

            {

                try { model = HardwareIdentity.ReadModel(); } catch { }

            }

            if (string.IsNullOrWhiteSpace(bios))

            {

                try { bios = HardwareIdentity.ReadBiosVersion(); } catch { }

            }



            bool wmiZonePath = wmi != null;

            bool serviceReachable = false;

            try

            {

                serviceReachable = AcerServiceClient.ProbeServiceAsync(350).GetAwaiter().GetResult();

            }

            catch { }



            return Classify(model, bios, board, fingerprint, serviceReachable, wmiZonePath, tierOverride, source);

        }



        /// <summary>

        /// Pure decision function behind <see cref="Detect"/>. Separated so the

        /// capability rules can be tested without touching real firmware or the

        /// Acer Lighting Service.

        /// </summary>

        public static KeyboardLightingCapabilities Classify(

            string? model,

            string? bios,

            string? board,

            string? fingerprint,

            bool serviceReachable,

            bool wmiZonePath,

            int tierOverride,

            string source = "test")

        {

            string normalized = NormalizeModel(model);

            string combined = $"{normalized} {NormalizeModel(board)}";

            var profile = AcerLaptopModelCatalog.Lookup(combined);



            bool candidatePerKey = MatchesAny(combined, PerKeyCandidates.Select(p => p.Pattern)) || profile.DefaultLightingTier == KeyboardLightingTier.PerKey;

            bool candidateThreeZone = MatchesAny(combined, ThreeZoneCandidates.Select(p => p.Pattern)) || profile.DefaultLightingTier == KeyboardLightingTier.ThreeZone;

            bool candidateSingleZone = MatchesAny(combined, SingleZoneCandidates.Select(p => p.Pattern)) || profile.DefaultLightingTier == KeyboardLightingTier.SingleZone;

            string candidateFamily = FindFamily(combined, PerKeyCandidates)

                                  ?? FindFamily(combined, ThreeZoneCandidates)

                                  ?? FindFamily(combined, SingleZoneCandidates)

                                  ?? FindFamily(combined, FourZoneCandidates)

                                  ?? (profile.MarketingName.Length > 0 ? profile.MarketingName : string.Empty);



            KeyboardLightingTier tier = NativeLightingRules.TrySelectTier(
                tierOverride, candidatePerKey, candidateThreeZone, candidateSingleZone,
                serviceReachable, wmiZonePath, out var nativeTier)
                ? nativeTier
                : SelectTierManaged(tierOverride, candidatePerKey, candidateThreeZone,
                    candidateSingleZone, serviceReachable, wmiZonePath);

            string notes;



            if (tierOverride == 2)

            {


                notes = "Per-key forced by user override. Unverified write path; falls back to 4-zone on failure.";

            }

            else if (tierOverride == 1)

            {


                notes = "4-zone forced by user override.";

            }

            else if (tierOverride == 3)

            {


                notes = "Single-zone forced by user override.";

            }

            else if (tierOverride == 4)

            {


                notes = "3-zone forced by user override.";

            }

            else if (candidatePerKey && serviceReachable)

            {


                notes = "Per-key candidate matched in BIOS identity and Acer Lighting Service is reachable.";

            }

            else if (candidatePerKey && !serviceReachable)

            {


                notes = "Per-key model detected, but Acer Lighting Service is not reachable. Running 4-zone only.";

            }

            else if (candidateThreeZone && (serviceReachable || wmiZonePath))

            {


                notes = $"BIOS identifies a 3-zone {candidateFamily}.";

            }

            else if (candidateSingleZone && (serviceReachable || wmiZonePath))

            {


                notes = $"BIOS identifies a single-zone {candidateFamily}.";

            }

            else if (serviceReachable || wmiZonePath)

            {


                notes = candidateFamily.Length > 0

                    ? $"BIOS identifies a 4-zone {candidateFamily}; per-key is not advertised."

                    : "No per-key capability signature found; exposing 4-zone control only.";

            }

            else

            {


                notes = "Neither Acer Lighting Service nor the Acer WMI lighting path responded.";

            }



            int zoneCount = tier switch

            {

                KeyboardLightingTier.Unavailable => 0,

                KeyboardLightingTier.SingleZone => 1,

                KeyboardLightingTier.ThreeZone => 3,

                _ => 4

            };



            string layoutId = profile.HasFullKeyboardWithNumpad ? KeyboardLayouts.FullAnsiId : KeyboardLayouts.AnsiTklId;
            int keyCount = profile.HasFullKeyboardWithNumpad ? KeyboardLayouts.FullAnsiKeyCount : KeyboardLayouts.AnsiTklKeyCount;

            return new KeyboardLightingCapabilities
            {
                Tier = tier,
                ZoneCount = zoneCount,
                PerKeySupported = tier == KeyboardLightingTier.PerKey,
                LayoutId = layoutId,
                KeyCount = keyCount,


                HasRearLightbar = profile.HasRearLightbar,

                ModelMarketingName = profile.MarketingName,

                ModelProfile = profile,

                Model = string.IsNullOrWhiteSpace(model) ? "Unknown" : model.Trim(),

                BiosVersion = string.IsNullOrWhiteSpace(bios) ? "Unknown" : bios.Trim(),

                BoardProduct = board ?? string.Empty,

                SmbiosFingerprint = fingerprint ?? string.Empty,

                LightingServiceReachable = serviceReachable,

                WmiZonePathAvailable = wmiZonePath,

                DetectionSource = source,

                Notes = notes,

                VerifiedOnHardware = false,

                DetectedAtUtc = DateTime.UtcNow

            };

        }



        internal static KeyboardLightingTier SelectTierManaged(int tierOverride, bool perKey, bool three, bool single, bool service, bool wmi)
        {
            bool available = service || wmi;
            return tierOverride switch
            {
                2 => KeyboardLightingTier.PerKey,
                1 => available ? KeyboardLightingTier.FourZone : KeyboardLightingTier.Unavailable,
                3 => available ? KeyboardLightingTier.SingleZone : KeyboardLightingTier.Unavailable,
                4 => available ? KeyboardLightingTier.ThreeZone : KeyboardLightingTier.Unavailable,
                _ when perKey && service => KeyboardLightingTier.PerKey,
                _ when perKey => KeyboardLightingTier.FourZone,
                _ when three && available => KeyboardLightingTier.ThreeZone,
                _ when single && available => KeyboardLightingTier.SingleZone,
                _ when available => KeyboardLightingTier.FourZone,
                _ => KeyboardLightingTier.Unavailable
            };
        }

        public static Task<KeyboardLightingCapabilities> DetectAsync(WmiController? wmi, int tierOverride = 0, CancellationToken ct = default)

            => Task.Run(() => Detect(wmi, tierOverride), ct);



        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

        private static string NormalizeModel(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                return Regex.Replace(value.ToUpperInvariant(), @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
            }
            catch (RegexMatchTimeoutException)
            {
                return value.Trim();
            }
        }

        private static bool MatchesAny(string value, IEnumerable<string> patterns)
        {
            foreach (var p in patterns)
            {
                try
                {
                    if (Regex.IsMatch(value, p, RegexOptions.IgnoreCase, RegexTimeout))
                        return true;
                }
                catch (RegexMatchTimeoutException) { }
            }
            return false;
        }

        private static string? FindFamily(string value, IEnumerable<(string Pattern, string Family)> table)
        {
            foreach (var entry in table)
            {
                try
                {
                    if (Regex.IsMatch(value, entry.Pattern, RegexOptions.IgnoreCase, RegexTimeout))
                        return entry.Family;
                }
                catch (RegexMatchTimeoutException) { }
            }
            return null;
        }
    }
}
