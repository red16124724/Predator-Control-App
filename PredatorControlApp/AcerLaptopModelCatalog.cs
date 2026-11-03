using System;

using System.Collections.Generic;

using System.Text.RegularExpressions;



namespace PredatorControlApp

{

    /// <summary>

    /// Fan cooling configuration architecture.

    /// </summary>

    public enum FanConfiguration

    {

        DualFan = 2,

        TripleFan = 3

    }



    /// <summary>

    /// Strongly typed hardware capability profile for an Acer Predator or Nitro laptop model.

    /// </summary>

    public sealed record AcerModelProfile

    {

        public string ModelCodePattern { get; init; } = string.Empty;

        public string MarketingName { get; init; } = string.Empty;

        public int Year { get; init; } = 2024;

        public int LaunchYear => Year;

        public KeyboardLightingTier DefaultLightingTier { get; init; } = KeyboardLightingTier.FourZone;

        public int ZoneCount => DefaultLightingTier switch

        {

            KeyboardLightingTier.Unavailable => 0,

            KeyboardLightingTier.SingleZone => 1,

            KeyboardLightingTier.ThreeZone => 3,

            _ => 4

        };


        public bool HasRearLightbar { get; init; }

        public FanConfiguration FanConfiguration { get; init; } = FanConfiguration.DualFan;

        public FanConfiguration FanTopology => FanConfiguration;

        public bool SupportsCpuPlControl { get; init; }

        public bool HasFullKeyboardWithNumpad { get; init; } = true;

        public bool IsMatchedFromCatalog { get; init; } = true;



        public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
        private Regex? _compiledRegex;
        public Regex Regex => _compiledRegex ??= new Regex(
            string.IsNullOrWhiteSpace(ModelCodePattern) ? "^$" : ModelCodePattern,
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            RegexTimeout);

        public override string ToString() => $"{MarketingName} ({Year}) [{DefaultLightingTier}, {FanConfiguration}, PLControl={SupportsCpuPlControl}]";

    }



    /// <summary>

    /// Exhaustive catalog of Acer Predator and Nitro laptops from 2020 through 2026+.

    /// Serves as the single source of truth for BIOS/DMI product codes, keyboard RGB capabilities,

    /// fan cooling architecture, and CPU power limit control support.

    /// </summary>

    public static class AcerLaptopModelCatalog

    {

        private static readonly List<AcerModelProfile> CatalogEntries = new()

        {

            // =========================================================================

            // 1. Acer Predator Helios (2020 - 2026+)

            // =========================================================================

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH315[-_]53\b",

                MarketingName = "Predator Helios 300 (2020)",

                Year = 2020,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH315[-_]54\b",

                MarketingName = "Predator Helios 300 (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH315[-_]55\b",

                MarketingName = "Predator Helios 300 (2022)",

                Year = 2022,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH317[-_]54\b",

                MarketingName = "Predator Helios 300 17-inch (2020)",

                Year = 2020,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH317[-_]55\b",

                MarketingName = "Predator Helios 300 17-inch (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH317[-_]56\b",

                MarketingName = "Predator Helios 300 17-inch (2022)",

                Year = 2022,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH16[-_]71\b",

                MarketingName = "Predator Helios 16 (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH16[-_]72\b",

                MarketingName = "Predator Helios 16 (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH16[-_]73\b",

                MarketingName = "Predator Helios 16 (2025/2026)",

                Year = 2025,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH18[-_]71\b",

                MarketingName = "Predator Helios 18 (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH18[-_]72\b",

                MarketingName = "Predator Helios 18 (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH18[-_]73\b",

                MarketingName = "Predator Helios 18 (2025/2026)",

                Year = 2025,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH717[-_]72\b",

                MarketingName = "Predator Helios 700 (2020)",

                Year = 2020,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH517[-_]52\b",

                MarketingName = "Predator Helios 500 (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },



            // =========================================================================

            // 2. Acer Predator Helios Neo (2023 - 2026+)

            // =========================================================================

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPHN14[-_]51\b",

                MarketingName = "Predator Helios Neo 14 (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,

                HasFullKeyboardWithNumpad = false,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPHN16[-_]71\b",

                MarketingName = "Predator Helios Neo 16 (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPHN16[-_]72\b",

                MarketingName = "Predator Helios Neo 16 (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPHN16[-_]73\b",

                MarketingName = "Predator Helios Neo 16 (2025/2026)",

                Year = 2025,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPHN18[-_]7[12]\b",

                MarketingName = "Predator Helios Neo 18 (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },



            // =========================================================================

            // 3. Acer Predator Triton (2020 - 2026+)

            // =========================================================================

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPT314[-_]51s?\b",

                MarketingName = "Predator Triton 300 SE (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,

                HasFullKeyboardWithNumpad = false,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPT314[-_]52s?\b",

                MarketingName = "Predator Triton 300 SE (2022)",

                Year = 2022,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,

                HasFullKeyboardWithNumpad = false,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPT315[-_]52\b",

                MarketingName = "Predator Triton 300 (2020)",

                Year = 2020,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPT315[-_]53\b",

                MarketingName = "Predator Triton 300 (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPT316[-_]51s?\b",

                MarketingName = "Predator Triton 300 SE 16-inch (2022)",

                Year = 2022,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPT515[-_]52\b",

                MarketingName = "Predator Triton 500 (2020)",

                Year = 2020,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.TripleFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPT516[-_]51s?\b",

                MarketingName = "Predator Triton 500 SE (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.TripleFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPT516[-_]52s?\b",

                MarketingName = "Predator Triton 500 SE (2022)",

                Year = 2022,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.TripleFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPT14[-_]51\b",

                MarketingName = "Predator Triton 14 (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,

                HasFullKeyboardWithNumpad = false,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPT16[-_]51\b",

                MarketingName = "Predator Triton 16 (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPTX17[-_]71\b",

                MarketingName = "Predator Triton 17 X (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.TripleFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPTX17[-_]72\b",

                MarketingName = "Predator Triton 17 X (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.PerKey,


                HasRearLightbar = true,

                FanConfiguration = FanConfiguration.TripleFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPTN16[-_]51\b",

                MarketingName = "Predator Triton Neo 16 (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },



            // =========================================================================

            // 4. Acer Nitro 5 (2020 - 2023)

            // =========================================================================

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN515[-_]44\b",

                MarketingName = "Acer Nitro 5 AMD (2020)",

                Year = 2020,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN515[-_]45\b",

                MarketingName = "Acer Nitro 5 AMD (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN515[-_]46\b",

                MarketingName = "Acer Nitro 5 AMD (2022)",

                Year = 2022,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN515[-_]47\b",

                MarketingName = "Acer Nitro 5 AMD (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN515[-_]55\b",

                MarketingName = "Acer Nitro 5 Intel (2020)",

                Year = 2020,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN515[-_]56\b",

                MarketingName = "Acer Nitro 5 Intel (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN515[-_]57\b",

                MarketingName = "Acer Nitro 5 Intel (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN515[-_]58\b",

                MarketingName = "Acer Nitro 5 Intel (2022)",

                Year = 2022,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN517[-_]41\b",

                MarketingName = "Acer Nitro 5 17-inch AMD (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN517[-_]42\b",

                MarketingName = "Acer Nitro 5 17-inch AMD (2022)",

                Year = 2022,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN517[-_]52\b",

                MarketingName = "Acer Nitro 5 17-inch Intel (2020)",

                Year = 2020,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN517[-_]54\b",

                MarketingName = "Acer Nitro 5 17-inch Intel (2021)",

                Year = 2021,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN517[-_]55\b",

                MarketingName = "Acer Nitro 5 17-inch Intel (2022)",

                Year = 2022,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },



            // =========================================================================

            // 5. Acer Nitro 16 / 17 / 14 (2023 - 2026+)

            // =========================================================================

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN16[-_]41\b",

                MarketingName = "Acer Nitro 16 AMD (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN16[-_]42\b",

                MarketingName = "Acer Nitro 16 AMD (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN16[-_]43\b",

                MarketingName = "Acer Nitro 16 AMD (2025)",

                Year = 2025,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN16[-_]51\b",

                MarketingName = "Acer Nitro 16 Intel (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN16[-_]71\b",

                MarketingName = "Acer Nitro 16 Intel (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN16[-_]72\b",

                MarketingName = "Acer Nitro 16 Intel (2025)",

                Year = 2025,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN17[-_]41\b",

                MarketingName = "Acer Nitro 17 AMD (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN17[-_]42\b",

                MarketingName = "Acer Nitro 17 AMD (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN17[-_]51\b",

                MarketingName = "Acer Nitro 17 Intel (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN17[-_]71\b",

                MarketingName = "Acer Nitro 17 Intel (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN17[-_]72\b",

                MarketingName = "Acer Nitro 17 Intel (2025)",

                Year = 2025,

                DefaultLightingTier = KeyboardLightingTier.FourZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN14[-_]41\b",

                MarketingName = "Acer Nitro 14 AMD (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.FourZone,

                HasFullKeyboardWithNumpad = false,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN14[-_]51\b",

                MarketingName = "Acer Nitro 14 Intel (2024/2025)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.FourZone,

                HasFullKeyboardWithNumpad = false,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },



            // =========================================================================

            // 6. Acer Nitro V 14 / 15 / 16 (2023 - 2026+)

            // =========================================================================

            new AcerModelProfile

            {

                ModelCodePattern = @"\bANV15[-_]41\b",

                MarketingName = "Acer Nitro V 15 AMD (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.SingleZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bANV15[-_]51\b",

                MarketingName = "Acer Nitro V 15 Intel (2023)",

                Year = 2023,

                DefaultLightingTier = KeyboardLightingTier.SingleZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bANV16[-_]41\b",

                MarketingName = "Acer Nitro V 16 AMD (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.SingleZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bANV16[-_]51\b",

                MarketingName = "Acer Nitro V 16 Intel (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.SingleZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bANV14[-_]61\b",

                MarketingName = "Acer Nitro V 14 AMD (2024)",

                Year = 2024,

                DefaultLightingTier = KeyboardLightingTier.SingleZone,

                HasFullKeyboardWithNumpad = false,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = false

            },



            // =========================================================================

            // 7. Legacy & Pre-2020 Models (Helios 300 / Nitro 5 / Aspire 7)

            // =========================================================================

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH315[-_]50\b",

                MarketingName = "Predator Helios 300 (Single-Zone)",

                Year = 2017,

                DefaultLightingTier = KeyboardLightingTier.SingleZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH317[-_]50\b",

                MarketingName = "Predator Helios 300 (Single-Zone)",

                Year = 2017,

                DefaultLightingTier = KeyboardLightingTier.SingleZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bG3[-_]57[12]\b",

                MarketingName = "Predator Helios 300 (Single-Zone)",

                Year = 2017,

                DefaultLightingTier = KeyboardLightingTier.SingleZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bA715[-_][0-9]{2}\b",

                MarketingName = "Aspire 7 (Single-Zone)",

                Year = 2018,

                DefaultLightingTier = KeyboardLightingTier.SingleZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN515[-_]3[1-9]\b",

                MarketingName = "Nitro 5 (Single-Zone)",

                Year = 2018,

                DefaultLightingTier = KeyboardLightingTier.SingleZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bAN515[-_]54\b",

                MarketingName = "Nitro 5 (3-Zone)",

                Year = 2019,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH315[-_]52\b",

                MarketingName = "Predator Helios 300 (3-Zone)",

                Year = 2019,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH317[-_]52\b",

                MarketingName = "Predator Helios 300 17-inch (3-Zone)",

                Year = 2019,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            },

            new AcerModelProfile

            {

                ModelCodePattern = @"\bPH317[-_]53\b",

                MarketingName = "Predator Helios 300 17-inch (3-Zone)",

                Year = 2019,

                DefaultLightingTier = KeyboardLightingTier.ThreeZone,


                HasRearLightbar = false,

                FanConfiguration = FanConfiguration.DualFan,

                SupportsCpuPlControl = true

            }

        };



        public static IReadOnlyList<AcerModelProfile> AllModels => CatalogEntries.AsReadOnly();



        /// <summary>

        /// Fast lookup for an Acer laptop profile using model and/or baseboard identifiers.

        /// When the model is not in the explicit catalog, applies heuristic fallback rules.

        /// </summary>

        public static AcerModelProfile Lookup(string? model, string? board = null)

        {

            string cleanModel = model ?? string.Empty;

            string cleanBoard = board ?? string.Empty;

            string combined = $"{cleanModel} {cleanBoard}".Trim();



            if (string.IsNullOrWhiteSpace(combined))

                return CreateGenericFallback(combined);



            // 1. Exact catalog match via regular expressions
            foreach (var entry in CatalogEntries)
            {
                try
                {
                    if (entry.Regex.IsMatch(combined))
                    {
                        return entry;
                    }
                }
                catch (RegexMatchTimeoutException) { }
            }



            // 2. Heuristic fallback rules for unrecognized or future models

            string upper = combined.ToUpperInvariant();



            // Heuristic A: PH16 / PH18 / PTX17 -> Per-Key candidate, Rear Lightbar = true

            if (upper.Contains("PH16") || upper.Contains("PH18") || upper.Contains("PTX17"))

            {

                bool isTriple = upper.Contains("PTX17");

                return new AcerModelProfile

                {

                    ModelCodePattern = @"PH16|PH18|PTX17",

                    MarketingName = upper.Contains("PTX17") ? "Predator Triton 17 X (Per-Key Candidate)" : "Predator Helios (Per-Key Candidate)",

                    Year = DateTime.UtcNow.Year,

                    DefaultLightingTier = KeyboardLightingTier.PerKey,


                    HasRearLightbar = true,

                    FanConfiguration = isTriple ? FanConfiguration.TripleFan : FanConfiguration.DualFan,

                    SupportsCpuPlControl = true,

                    HasFullKeyboardWithNumpad = !(upper.Contains("PT314") || upper.Contains("PT14")),

                    IsMatchedFromCatalog = false

                };

            }



            // Heuristic B: PHN / AN16 / AN17 / PH315 -> FourZone

            if (upper.Contains("PHN") || upper.Contains("AN14") || upper.Contains("AN16") || upper.Contains("AN17") || upper.Contains("PH315") || (upper.Contains("NITRO") && upper.Contains("14")))

            {

                string fam = upper.Contains("PHN")

                    ? "Predator Helios Neo"

                    : (upper.Contains("PH315") ? "Predator Helios 300" : "Acer Nitro");



                return new AcerModelProfile

                {

                    ModelCodePattern = @"PHN|AN16|AN17|PH315",

                    MarketingName = fam,

                    Year = DateTime.UtcNow.Year,

                    DefaultLightingTier = KeyboardLightingTier.FourZone,


                    HasRearLightbar = false,

                    FanConfiguration = FanConfiguration.DualFan,

                    SupportsCpuPlControl = !upper.Contains("AN16-4") && !upper.Contains("AN17-4"),

                    HasFullKeyboardWithNumpad = !(upper.Contains("PHN14") || upper.Contains("AN14") || (upper.Contains("NITRO") && upper.Contains("14"))),

                    IsMatchedFromCatalog = false

                };

            }



            // Heuristic C: PT314 / PT516 / PT14 -> ThreeZone

            if (upper.Contains("PT314") || upper.Contains("PT516") || upper.Contains("PT14"))

            {

                bool isTriple = upper.Contains("PT516");

                return new AcerModelProfile

                {

                    ModelCodePattern = @"PT314|PT516|PT14",

                    MarketingName = "Predator Triton (3-Zone)",

                    Year = DateTime.UtcNow.Year,

                    DefaultLightingTier = KeyboardLightingTier.ThreeZone,


                    HasRearLightbar = false,

                    FanConfiguration = isTriple ? FanConfiguration.TripleFan : FanConfiguration.DualFan,

                    SupportsCpuPlControl = true,

                    IsMatchedFromCatalog = false

                };

            }



            // Heuristic D: ANV15 / ANV16 / ANV14 -> SingleZone (White/Monochrome)

            if (upper.Contains("ANV15") || upper.Contains("ANV16") || upper.Contains("ANV14"))

            {

                return new AcerModelProfile

                {

                    ModelCodePattern = @"ANV15|ANV16|ANV14",

                    MarketingName = "Acer Nitro V (Single-Zone)",

                    Year = DateTime.UtcNow.Year,

                    DefaultLightingTier = KeyboardLightingTier.SingleZone,


                    HasRearLightbar = false,

                    FanConfiguration = FanConfiguration.DualFan,

                    SupportsCpuPlControl = upper.Contains("-5"),

                    HasFullKeyboardWithNumpad = !(upper.Contains("ANV14") || upper.Contains("AN14")),

                    IsMatchedFromCatalog = false

                };

            }



            // Generic fallback -> 4-Zone

            return CreateGenericFallback(combined);

        }



        /// <summary>

        /// Fast lookup for an Acer laptop profile using model or board identifier.

        /// </summary>

        public static AcerModelProfile Lookup(string? modelOrBoard) => Lookup(modelOrBoard, null);



        /// <summary>

        /// Attempts to match an explicit profile in the catalog without falling back to heuristics.

        /// </summary>

        public static bool TryLookup(string? modelOrBoard, out AcerModelProfile profile)

        {

            if (string.IsNullOrWhiteSpace(modelOrBoard))

            {

                profile = CreateGenericFallback(modelOrBoard);

                return false;

            }



            foreach (var entry in CatalogEntries)
            {
                try
                {
                    if (entry.Regex.IsMatch(modelOrBoard))
                    {
                        profile = entry;
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException) { }
            }



            profile = Lookup(modelOrBoard);

            return false;

        }



        private static AcerModelProfile CreateGenericFallback(string? modelOrBoard = null) => new()

        {

            ModelCodePattern = ".*",

            MarketingName = "Acer Gaming Laptop (Generic Fallback)",

            Year = DateTime.UtcNow.Year,

            DefaultLightingTier = KeyboardLightingTier.FourZone,


            HasRearLightbar = false,

            FanConfiguration = FanConfiguration.DualFan,

            SupportsCpuPlControl = false,

            HasFullKeyboardWithNumpad = !(modelOrBoard != null && (modelOrBoard.Contains("14") && (modelOrBoard.Contains("Neo") || modelOrBoard.Contains("Triton") || modelOrBoard.Contains("Nitro") || modelOrBoard.Contains("PHN") || modelOrBoard.Contains("PT") || modelOrBoard.Contains("AN")))),

            IsMatchedFromCatalog = false

        };

    }

}

