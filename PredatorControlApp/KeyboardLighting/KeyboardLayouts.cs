using System;
using System.Collections.Generic;
using System.Linq;

namespace PredatorControlApp
{
    /// <summary>A single logical key in the app's per-key map.</summary>
    public sealed record KeyboardKey(int Index, string Label, int Row, double XUnits, double WidthUnits);

    /// <summary>
    /// Logical keyboard geometry used by the per-key editor and the lighting
    /// transport. Indexes are row-major from 0. Hardware LED order can differ
    /// between panels; the transport therefore sends the logical index only on
    /// the experimental Acer Lighting Service path.
    /// </summary>
    public static class KeyboardLayouts
    {
        public const string AnsiTklId = "ansi-tkl-87";
        public const int AnsiTklKeyCount = 87;
        public const double AnsiTklWidthUnits = 22.0;

        public const string FullAnsiId = "full-ansi-104";
        public const int FullAnsiKeyCount = 100;
        public const double FullAnsiWidthUnits = 24.0;

        private static readonly Lazy<IReadOnlyList<KeyboardKey>> _ansiTkl = new(BuildAnsiTkl);
        private static readonly Lazy<IReadOnlyList<KeyboardKey>> _fullAnsi = new(BuildFullAnsi);

        public static IReadOnlyList<KeyboardKey> AnsiTkl => _ansiTkl.Value;
        public static IReadOnlyList<KeyboardKey> FullAnsi => _fullAnsi.Value;

        public static int MaxRow => Math.Max(
            AnsiTkl.Count == 0 ? 0 : AnsiTkl.Max(k => k.Row),
            FullAnsi.Count == 0 ? 0 : FullAnsi.Max(k => k.Row));

        public static IReadOnlyList<KeyboardKey> Get(string? layoutId)
        {
            if (string.Equals(layoutId, AnsiTklId, StringComparison.OrdinalIgnoreCase))
                return AnsiTkl;
            if (string.Equals(layoutId, FullAnsiId, StringComparison.OrdinalIgnoreCase))
                return FullAnsi;
            if (string.IsNullOrWhiteSpace(layoutId))
                return FullAnsi;
            if (AcerLaptopModelCatalog.TryLookup(layoutId, out var profile))
                return profile.HasFullKeyboardWithNumpad ? FullAnsi : AnsiTkl;
            return AnsiTkl;
        }

        public static IReadOnlyList<KeyboardKey> GetForProfile(AcerModelProfile? profile)
            => profile?.HasFullKeyboardWithNumpad == false ? AnsiTkl : FullAnsi;

        private static IReadOnlyList<KeyboardKey> BuildAnsiTkl()
        {
            var rows = new List<List<(string Label, double Width)>>
            {
                new()
                {
                    ("Esc", 1.0), ("F1", 1.0), ("F2", 1.0), ("F3", 1.0), ("F4", 1.0),
                    ("F5", 1.0), ("F6", 1.0), ("F7", 1.0), ("F8", 1.0),
                    ("F9", 1.0), ("F10", 1.0), ("F11", 1.0), ("F12", 1.0),
                    ("PrtSc", 1.0), ("ScrLk", 1.0), ("Pause", 1.0)
                },
                new()
                {
                    ("`", 1.0), ("1", 1.0), ("2", 1.0), ("3", 1.0), ("4", 1.0),
                    ("5", 1.0), ("6", 1.0), ("7", 1.0), ("8", 1.0), ("9", 1.0), ("0", 1.0),
                    ("-", 1.0), ("=", 1.0), ("Backspace", 2.0),
                    ("Ins", 1.0), ("Home", 1.0), ("PgUp", 1.0)
                },
                new()
                {
                    ("Tab", 1.5), ("Q", 1.0), ("W", 1.0), ("E", 1.0), ("R", 1.0), ("T", 1.0),
                    ("Y", 1.0), ("U", 1.0), ("I", 1.0), ("O", 1.0), ("P", 1.0),
                    ("[", 1.0), ("]", 1.0), ("\\", 1.5),
                    ("Del", 1.0), ("End", 1.0), ("PgDn", 1.0)
                },
                new()
                {
                    ("Caps", 1.75), ("A", 1.0), ("S", 1.0), ("D", 1.0), ("F", 1.0), ("G", 1.0),
                    ("H", 1.0), ("J", 1.0), ("K", 1.0), ("L", 1.0),
                    (";", 1.0), ("'", 1.0), ("Enter", 2.25)
                },
                new()
                {
                    ("LShift", 2.25), ("Z", 1.0), ("X", 1.0), ("C", 1.0), ("V", 1.0),
                    ("B", 1.0), ("N", 1.0), ("M", 1.0),
                    (",", 1.0), (".", 1.0), ("/", 1.0), ("RShift", 2.75),
                    ("Up", 1.0)
                },
                new()
                {
                    ("LCtrl", 1.25), ("Win", 1.25), ("LAlt", 1.25), ("Space", 6.25),
                    ("RAlt", 1.25), ("Fn", 1.25), ("Menu", 1.25), ("RCtrl", 1.25),
                    ("Left", 1.0), ("Down", 1.0), ("Right", 1.0)
                }
            };

            var keys = new List<KeyboardKey>(AnsiTklKeyCount);
            int index = 0;
            for (int row = 0; row < rows.Count; row++)
            {
                double x = 0;
                foreach (var (label, width) in rows[row])
                {
                    keys.Add(new KeyboardKey(index++, label, row, x, width));
                    x += width;
                }
            }
            return keys;
        }

        private static IReadOnlyList<KeyboardKey> BuildFullAnsi()
        {
            var rows = new List<List<(string Label, double Width)>>
            {
                new()
                {
                    ("Esc", 1.0), ("F1", 1.0), ("F2", 1.0), ("F3", 1.0), ("F4", 1.0),
                    ("F5", 1.0), ("F6", 1.0), ("F7", 1.0), ("F8", 1.0),
                    ("F9", 1.0), ("F10", 1.0), ("F11", 1.0), ("F12", 1.0),
                    ("PrtSc", 1.0), ("Del", 1.0), ("PredatorSense", 1.0), ("Mute", 1.0)
                },
                new()
                {
                    ("~", 1.0), ("1", 1.0), ("2", 1.0), ("3", 1.0), ("4", 1.0),
                    ("5", 1.0), ("6", 1.0), ("7", 1.0), ("8", 1.0), ("9", 1.0), ("0", 1.0),
                    ("-", 1.0), ("=", 1.0), ("Backspace", 2.0),
                    ("NumLk", 1.0), ("/", 1.0), ("*", 1.0), ("-", 1.0)
                },
                new()
                {
                    ("Tab", 1.5), ("Q", 1.0), ("W", 1.0), ("E", 1.0), ("R", 1.0), ("T", 1.0),
                    ("Y", 1.0), ("U", 1.0), ("I", 1.0), ("O", 1.0), ("P", 1.0),
                    ("[", 1.0), ("]", 1.0), ("\\", 1.5),
                    ("7", 1.0), ("8", 1.0), ("9", 1.0), ("+", 1.0)
                },
                new()
                {
                    ("Caps", 1.75), ("A", 1.0), ("S", 1.0), ("D", 1.0), ("F", 1.0), ("G", 1.0),
                    ("H", 1.0), ("J", 1.0), ("K", 1.0), ("L", 1.0),
                    (";", 1.0), ("'", 1.0), ("Enter", 2.25),
                    ("4", 1.0), ("5", 1.0), ("6", 1.0), ("+", 1.0)
                },
                new()
                {
                    ("LShift", 2.25), ("Z", 1.0), ("X", 1.0), ("C", 1.0), ("V", 1.0),
                    ("B", 1.0), ("N", 1.0), ("M", 1.0),
                    (",", 1.0), (".", 1.0), ("/", 1.0), ("RShift", 1.75),
                    ("Up", 1.0),
                    ("1", 1.0), ("2", 1.0), ("3", 1.0), ("Enter", 1.0)
                },
                new()
                {
                    ("LCtrl", 1.25), ("Win", 1.25), ("LAlt", 1.25), ("Space", 5.25),
                    ("RAlt", 1.25), ("Fn", 1.25), ("RCtrl", 1.25),
                    ("Left", 1.0), ("Down", 1.0), ("Right", 1.0),
                    ("0", 2.0), (".", 1.0), ("Enter", 1.0)
                }
            };

            var keys = new List<KeyboardKey>(FullAnsiKeyCount);
            int index = 0;
            for (int row = 0; row < rows.Count; row++)
            {
                double x = 0;
                foreach (var (label, width) in rows[row])
                {
                    keys.Add(new KeyboardKey(index++, label, row, x, width));
                    x += width;
                }
            }
            return keys;
        }
    }
}
