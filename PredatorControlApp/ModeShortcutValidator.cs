using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows.Forms;

using System.Text.Json;

namespace PredatorControlApp
{
    public sealed class CustomModeShortcutJsonConverter : JsonConverter<CustomModeShortcut>
    {
        public override CustomModeShortcut? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string modeName = string.Empty;
            if (root.TryGetProperty("ModeName", out var pName) && pName.ValueKind == JsonValueKind.String)
            {
                modeName = pName.GetString() ?? string.Empty;
            }

            byte modeCode = 0;
            if (root.TryGetProperty("ModeCode", out var pCode) || root.TryGetProperty("ModeByteCode", out pCode))
            {
                if (pCode.ValueKind == JsonValueKind.Number && pCode.TryGetByte(out byte b)) modeCode = b;
                else if (pCode.ValueKind == JsonValueKind.String && byte.TryParse(pCode.GetString(), out byte sb)) modeCode = sb;
            }

            Keys key = Keys.None;
            if (root.TryGetProperty("Key", out var pKey))
            {
                if (pKey.ValueKind == JsonValueKind.Number && pKey.TryGetInt32(out int kInt))
                {
                    key = (Keys)kInt;
                }
                else if (pKey.ValueKind == JsonValueKind.String)
                {
                    string? kStr = pKey.GetString();
                    if (!string.IsNullOrEmpty(kStr) && Enum.TryParse<Keys>(kStr, true, out var parsedKey))
                    {
                        key = parsedKey;
                    }
                }
            }

            bool ctrl = false;
            if (root.TryGetProperty("Ctrl", out var pCtrl))
            {
                if (pCtrl.ValueKind is JsonValueKind.True or JsonValueKind.False) ctrl = pCtrl.GetBoolean();
                else if (pCtrl.ValueKind == JsonValueKind.Number && pCtrl.TryGetInt32(out int ci)) ctrl = ci != 0;
                else if (pCtrl.ValueKind == JsonValueKind.String && bool.TryParse(pCtrl.GetString(), out bool cb)) ctrl = cb;
            }

            bool alt = false;
            if (root.TryGetProperty("Alt", out var pAlt))
            {
                if (pAlt.ValueKind is JsonValueKind.True or JsonValueKind.False) alt = pAlt.GetBoolean();
                else if (pAlt.ValueKind == JsonValueKind.Number && pAlt.TryGetInt32(out int ai)) alt = ai != 0;
                else if (pAlt.ValueKind == JsonValueKind.String && bool.TryParse(pAlt.GetString(), out bool ab)) alt = ab;
            }

            bool shift = false;
            if (root.TryGetProperty("Shift", out var pShift))
            {
                if (pShift.ValueKind is JsonValueKind.True or JsonValueKind.False) shift = pShift.GetBoolean();
                else if (pShift.ValueKind == JsonValueKind.Number && pShift.TryGetInt32(out int si)) shift = si != 0;
                else if (pShift.ValueKind == JsonValueKind.String && bool.TryParse(pShift.GetString(), out bool sb)) shift = sb;
            }

            bool win = false;
            if (root.TryGetProperty("Win", out var pWin))
            {
                if (pWin.ValueKind is JsonValueKind.True or JsonValueKind.False) win = pWin.GetBoolean();
                else if (pWin.ValueKind == JsonValueKind.Number && pWin.TryGetInt32(out int wi)) win = wi != 0;
                else if (pWin.ValueKind == JsonValueKind.String && bool.TryParse(pWin.GetString(), out bool wb)) win = wb;
            }

            bool enabled = true;
            if (root.TryGetProperty("Enabled", out var pEnabled))
            {
                if (pEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False) enabled = pEnabled.GetBoolean();
                else if (pEnabled.ValueKind == JsonValueKind.Number && pEnabled.TryGetInt32(out int ei)) enabled = ei != 0;
                else if (pEnabled.ValueKind == JsonValueKind.String && bool.TryParse(pEnabled.GetString(), out bool eb)) enabled = eb;
            }

            return new CustomModeShortcut(modeName, modeCode, key, ctrl, alt, shift, win, enabled);
        }

        public override void Write(Utf8JsonWriter writer, CustomModeShortcut value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteString("ModeName", value.ModeName);
            writer.WriteNumber("ModeCode", value.ModeCode);
            writer.WriteNumber("Key", (int)value.Key);
            writer.WriteBoolean("Ctrl", value.Ctrl);
            writer.WriteBoolean("Alt", value.Alt);
            writer.WriteBoolean("Shift", value.Shift);
            writer.WriteBoolean("Win", value.Win);
            writer.WriteBoolean("Enabled", value.Enabled);
            writer.WriteEndObject();
        }
    }

    // End Converter

    /// <summary>
    /// Represents a customizable keyboard shortcut bound to an Acer operating power mode.
    /// Supports serialization, cloning, formatting, and validation.
    /// </summary>
    [JsonConverter(typeof(CustomModeShortcutJsonConverter))]
    public sealed class CustomModeShortcut : IEquatable<CustomModeShortcut>
    {
        /// <summary>
        /// Human-readable mode name ("Eco", "Quiet", "Balanced", "Performance", "Turbo").
        /// </summary>
        public string ModeName { get; set; } = string.Empty;

        /// <summary>
        /// Hardware operating mode code byte:
        /// Eco = 0x06, Quiet = 0x00, Balanced = 0x01, Performance = 0x04, Turbo = 0x05.
        /// </summary>
        public byte ModeCode { get; set; }

        /// <summary>
        /// Alias for ModeCode.
        /// </summary>
        public byte ModeByteCode
        {
            get => ModeCode;
            set => ModeCode = value;
        }

        /// <summary>
        /// Gets whether a valid primary key is assigned.
        /// </summary>
        [JsonIgnore]
        public bool IsConfigured => Key != Keys.None;

        /// <summary>
        /// User-friendly display string format (e.g. "Ctrl + Alt + D5" or "None").
        /// </summary>
        [JsonIgnore]
        public string DisplayString
        {
            get
            {
                if (Key == Keys.None) return "None";
                var parts = new List<string>(4);
                if (Ctrl) parts.Add("Ctrl");
                if (Alt) parts.Add("Alt");
                if (Shift) parts.Add("Shift");
                if (Win) parts.Add("Win");
                parts.Add(Key.ToString());
                return string.Join(" + ", parts);
            }
        }

        /// <summary>
        /// The primary key (System.Windows.Forms.Keys).
        /// </summary>
        public Keys Key { get; set; }

        /// <summary>
        /// Whether the Control (Ctrl) modifier is required.
        /// </summary>
        public bool Ctrl { get; set; }

        /// <summary>
        /// Whether the Alternate (Alt) modifier is required.
        /// </summary>
        public bool Alt { get; set; }

        /// <summary>
        /// Whether the Shift modifier is required.
        /// </summary>
        public bool Shift { get; set; }

        /// <summary>
        /// Whether the Windows (Win) modifier is required (must always be false for valid shortcuts).
        /// </summary>
        public bool Win { get; set; }

        /// <summary>
        /// Whether this shortcut is actively enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets the virtual key code (vkCode) of the primary key.
        /// </summary>
        [JsonIgnore]
        public int VkCode => (int)(Key & Keys.KeyCode);

        public CustomModeShortcut()
        {
        }

        public CustomModeShortcut(string modeName, byte modeCode, Keys key, bool ctrl = false, bool alt = false, bool shift = false, bool win = false, bool enabled = true)
        {
            ModeName = modeName ?? string.Empty;
            ModeCode = modeCode;
            Key = key & Keys.KeyCode;
            Ctrl = ctrl || (key & Keys.Control) != 0;
            Alt = alt || (key & Keys.Alt) != 0;
            Shift = shift || (key & Keys.Shift) != 0;
            Win = win;
            Enabled = enabled;
        }

        public CustomModeShortcut(OperatingMode mode, Keys key, bool ctrl = false, bool alt = false, bool shift = false, bool win = false, bool enabled = true)
            : this(mode.ToString(), (byte)mode, key, ctrl, alt, shift, win, enabled)
        {
        }

        /// <summary>
        /// Creates a deep copy of this shortcut.
        /// </summary>
        public CustomModeShortcut Clone()
        {
            return new CustomModeShortcut(ModeName, ModeCode, Key, Ctrl, Alt, Shift, Win, Enabled);
        }

        /// <summary>
        /// Returns a readable representation of the shortcut combo (e.g. "Ctrl+Alt+E").
        /// </summary>
        public override string ToString()
        {
            return ModeShortcutValidator.FormatShortcut(Key, Ctrl, Alt, Shift, Win);
        }

        /// <summary>
        /// Validates whether this shortcut satisfies all system, OS, and safety rules.
        /// </summary>
        public bool IsValid(out string failureReason)
        {
            return ModeShortcutValidator.IsValid(this, out failureReason);
        }

        /// <summary>
        /// Checks whether another shortcut has the identical key and modifier combination, regardless of target mode.
        /// </summary>
        public bool HasSameCombination(CustomModeShortcut? other)
        {
            if (other is null) return false;
            return (Key & Keys.KeyCode) == (other.Key & Keys.KeyCode) &&
                   Ctrl == other.Ctrl &&
                   Alt == other.Alt &&
                   Shift == other.Shift &&
                   Win == other.Win;
        }

        public bool Equals(CustomModeShortcut? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return HasSameCombination(other) &&
                   Enabled == other.Enabled &&
                   ModeCode == other.ModeCode &&
                   string.Equals(ModeName, other.ModeName, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj) => Equals(obj as CustomModeShortcut);

        public override int GetHashCode()
        {
            return HashCode.Combine((int)(Key & Keys.KeyCode), Ctrl, Alt, Shift, Win, ModeCode, Enabled);
        }

        public static bool operator ==(CustomModeShortcut? left, CustomModeShortcut? right)
        {
            if (left is null) return right is null;
            return left.Equals(right);
        }

        public static bool operator !=(CustomModeShortcut? left, CustomModeShortcut? right) => !(left == right);
    }

    /// <summary>
    /// Validates, formats, and manages custom keyboard shortcuts for Acer operating power modes.
    /// Strictly protects against conflicting with Windows default shortcuts, function keys (F1-F24),
    /// system keys, editing shortcuts, and Win key chords.
    /// </summary>
    public static class ModeShortcutValidator
    {
        // Operating mode code constants matching Acer hardware firmware:
        public const byte CodeQuiet = 0x00;
        public const byte CodeBalanced = 0x01;
        public const byte CodePerformance = 0x04;
        public const byte CodeTurbo = 0x05;
        public const byte CodeEco = 0x06;

        // Function key virtual-key boundaries (VK_F1 to VK_F24)
        public const int MinFunctionKeyVk = 0x70; // 112 (F1)
        public const int MaxFunctionKeyVk = 0x87; // 135 (F24)

        // Reserved system keys that must NEVER be bound as primary keys
        private static readonly HashSet<Keys> ReservedSystemKeys = new()
        {
            Keys.Escape,
            Keys.Tab,
            Keys.PrintScreen,
            Keys.Pause,
            Keys.NumLock,
            Keys.CapsLock,
            Keys.Scroll,
            Keys.Apps,
            Keys.Sleep
        };

        // Reserved standard editing/application keys when Ctrl is pressed without Alt
        private static readonly Dictionary<Keys, string> ReservedCtrlEditingKeys = new()
        {
            { Keys.C, "Ctrl+C (Copy)" },
            { Keys.V, "Ctrl+V (Paste)" },
            { Keys.X, "Ctrl+X (Cut)" },
            { Keys.Z, "Ctrl+Z (Undo)" },
            { Keys.Y, "Ctrl+Y (Redo)" },
            { Keys.A, "Ctrl+A (Select All)" },
            { Keys.S, "Ctrl+S (Save)" },
            { Keys.P, "Ctrl+P (Print)" },
            { Keys.O, "Ctrl+O (Open)" },
            { Keys.N, "Ctrl+N (New Document/Window)" },
            { Keys.W, "Ctrl+W (Close Tab/Window)" },
            { Keys.F, "Ctrl+F (Find)" },
            { Keys.H, "Ctrl+H (History/Replace)" },
            { Keys.T, "Ctrl+T / Ctrl+Shift+T (New/Reopen Tab)" },
            { Keys.D, "Ctrl+D (Bookmark/Delete line)" },
            { Keys.R, "Ctrl+R (Refresh/Reload)" },
            { Keys.J, "Ctrl+J (Downloads/Console)" },
            { Keys.Tab, "Ctrl+Tab / Ctrl+Shift+Tab (Switch Tabs)" }
        };

        /// <summary>
        /// Validates a <see cref="CustomModeShortcut"/> instance.
        /// </summary>
        /// <summary>
        /// Validates a <see cref="CustomModeShortcut"/> instance. Alias for <see cref="IsValid(CustomModeShortcut?, out string)"/>.
        /// </summary>
        public static bool Validate(CustomModeShortcut? shortcut, out string failureReason)
            => IsValid(shortcut, out failureReason);

        /// <summary>
        /// Validates a <see cref="CustomModeShortcut"/> instance.
        /// </summary>
        public static bool Validate(CustomModeShortcut? shortcut)
            => IsValid(shortcut, out _);

        public static bool IsValid(CustomModeShortcut? shortcut, out string failureReason)
        {
            if (shortcut is null)
            {
                failureReason = "Shortcut definition cannot be null.";
                return false;
            }

            return IsValid(shortcut.Key, shortcut.Ctrl, shortcut.Alt, shortcut.Shift, shortcut.Win, out failureReason);
        }

        /// <summary>
        /// Validates key and modifiers using an integer virtual key code (vkCode).
        /// </summary>
        public static bool IsValid(int vkCode, bool ctrl, bool alt, bool shift, bool win, out string failureReason)
        {
            return IsValid((Keys)vkCode, ctrl, alt, shift, win, out failureReason);
        }

        /// <summary>
        /// Validates a shortcut key combination against Windows OS standards, Acer hardware boundaries,
        /// and application safety rules.
        /// </summary>
        public static bool IsValid(Keys key, bool ctrl, bool alt, bool shift, bool win, out string failureReason)
        {
            // The native core makes the decision; the wording of the message
            // stays here so callers and tests keep their exact strings.
            int nativeResult = PredatorNativeMigrations.ValidateShortcut(key, ctrl, alt, shift, win, out uint nativeRule);
            if (nativeResult == 0)
            {
                return ResolveRule(nativeRule, key, out failureReason);
            }

            // Normalize base key from combined Keys flags
            Keys baseKey = key & Keys.KeyCode;
            if ((key & Keys.Control) != 0) ctrl = true;
            if ((key & Keys.Alt) != 0) alt = true;
            if ((key & Keys.Shift) != 0) shift = true;

            int vk = (int)baseKey;

            // 1. Function keys rule: F1 to F24 (vk 0x70 to 0x87) strictly forbidden with any or no modifiers (Ctrl/Alt/Shift/Win)
            if ((vk >= MinFunctionKeyVk && vk <= MaxFunctionKeyVk) || (baseKey >= Keys.F1 && baseKey <= Keys.F24))
            {
                failureReason = $"Function keys (F1-F24) are not allowed in custom mode shortcuts ({baseKey}).";
                return false;
            }

            // 2. Windows key combinations rule:
            // "Any Win key combinations (Win+L, Win+D, Win+E, Win+R, Win+X, Win+S, Win+A, Win+V, Win+P, Win+I, Win+Tab, Win+Shift+S, etc.)
            // or Win key alone MUST BE FORBIDDEN."
            if (win)
            {
                failureReason = "Windows key combinations are reserved by Windows OS and cannot be bound.";
                return false;
            }

            // 3. Base key existence check
            if (baseKey == Keys.None || vk == 0)
            {
                failureReason = "No primary key specified.";
                return false;
            }

            // Win key as base key check
            if (baseKey == Keys.LWin || baseKey == Keys.RWin)
            {
                failureReason = "Windows key cannot be used as a shortcut key.";
                return false;
            }

            // 4. Modifier-only and bare key checks:
            // Must have at least one valid key and at least one modifier (Ctrl, Alt, or Shift)
            // so normal single-key typing is never swallowed or hijacked.
            if (IsModifierKey(baseKey))
            {
                failureReason = "Modifier keys (Ctrl, Alt, Shift, Win) cannot be used alone as primary keys.";
                return false;
            }

            if (!ctrl && !alt && !shift)
            {
                failureReason = "Shortcut must include at least one modifier key (Ctrl, Alt, or Shift) to prevent hijacking normal keyboard typing.";
                return false;
            }

            // 5. System keys rule:
            // "System keys: Escape, Tab, PrintScreen, Pause, NumLock, CapsLock, ScrollLock."
            if (ReservedSystemKeys.Contains(baseKey))
            {
                failureReason = $"Key '{baseKey}' is a reserved system/hardware control key and cannot be bound.";
                return false;
            }

            // 6. Default Windows shortcuts rule:
            // "Alt+Tab, Alt+Shift+Tab, Alt+F4, Alt+Esc, Alt+Space, Ctrl+Alt+Del, Ctrl+Shift+Esc."
            if (alt && baseKey == Keys.Space && !ctrl)
            {
                failureReason = "Alt+Space is reserved by Windows for the window system menu.";
                return false;
            }

            if (ctrl && alt && baseKey == Keys.Delete)
            {
                failureReason = "Ctrl+Alt+Del is reserved by Windows for security/logon attention.";
                return false;
            }

            if (ctrl && shift && baseKey == Keys.Escape)
            {
                failureReason = "Ctrl+Shift+Esc is reserved by Windows for Task Manager.";
                return false;
            }

            if (alt && !ctrl && (baseKey == Keys.Tab || baseKey == Keys.Escape))
            {
                failureReason = $"Alt+{baseKey} is reserved by Windows for window switching/cycling.";
                return false;
            }

            if (alt && !ctrl && baseKey == Keys.Enter)
            {
                failureReason = "Alt+Enter is reserved for window properties / fullscreen toggle.";
                return false;
            }

            // 7. Standard Windows editing/app shortcuts rule:
            // "Standard Windows editing/app shortcuts: Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+Z, Ctrl+Y, Ctrl+A, Ctrl+S, Ctrl+P, Ctrl+O, Ctrl+N, Ctrl+W, Ctrl+F, Ctrl+H, Ctrl+T, Ctrl+Shift+T, Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+D, Ctrl+R, Ctrl+J."
            // When Ctrl is held without Alt, standard application editing shortcuts must not be captured.
            if (ctrl && !alt && ReservedCtrlEditingKeys.TryGetValue(baseKey, out string? reservedName))
            {
                failureReason = $"'{reservedName}' is a reserved standard application shortcut.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }

        /// <summary>
        /// Validates a collection of shortcuts, ensuring each individual shortcut is valid and no duplicates exist.
        /// </summary>
        public static bool ValidateShortcutSet(IEnumerable<CustomModeShortcut>? shortcuts, out string failureReason)
        {
            if (shortcuts is null)
            {
                failureReason = "Shortcut set cannot be null.";
                return false;
            }

            var list = shortcuts.ToList();
            if (list.Count == 0)
            {
                failureReason = "Shortcut set cannot be empty.";
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null)
                {
                    failureReason = "Shortcut set cannot contain null items.";
                    return false;
                }
                if (!s.Enabled) continue;
                if (!IsValid(s, out var err))
                {
                    failureReason = $"Invalid shortcut for mode '{s.ModeName}': {err}";
                    return false;
                }
            }

            for (int i = 0; i < list.Count; i++)
            {
                var s1 = list[i];
                if (s1 == null || !s1.Enabled || s1.Key == Keys.None) continue;
                for (int j = i + 1; j < list.Count; j++)
                {
                    var s2 = list[j];
                    if (s2 == null || !s2.Enabled || s2.Key == Keys.None) continue;
                    if (s1.HasSameCombination(s2))
                    {
                        failureReason = $"Shortcut '{s1}' is assigned to multiple modes: '{s1.ModeName}' and '{s2.ModeName}'. Each mode must have a unique shortcut.";
                        return false;
                    }
                }
            }

            failureReason = string.Empty;
            return true;
        }

        /// <summary>
        /// Finds the matching mode shortcut from the configured collection, if any matches the pressed key and modifiers.
        /// </summary>
        public static bool TryFindMatchingMode(
            IEnumerable<CustomModeShortcut> shortcuts,
            Keys key,
            bool ctrl,
            bool alt,
            bool shift,
            bool win,
            out CustomModeShortcut? matchedShortcut)
        {
            matchedShortcut = null;
            if (shortcuts is null) return false;

            Keys baseKey = key & Keys.KeyCode;
            if (baseKey == Keys.None) return false;

            foreach (var s in shortcuts)
            {
                if (s != null &&
                    s.Enabled &&
                    s.Key != Keys.None &&
                    (s.Key & Keys.KeyCode) == baseKey &&
                    s.Ctrl == ctrl &&
                    s.Alt == alt &&
                    s.Shift == shift &&
                    s.Win == win)
                {
                    matchedShortcut = s;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Factory method to create default shortcuts for Acer operating modes.
        /// </summary>
        public static List<CustomModeShortcut> CreateDefaultShortcuts() => GetDefaultShortcuts();

        /// <summary>
        /// Returns the default recommended letter-based shortcuts for all Acer operating modes:
        /// - Eco: Ctrl+Alt+E (0x06)
        /// - Quiet: Ctrl+Alt+Q (0x00)
        /// - Balanced: Ctrl+Alt+B (0x01)
        /// - Performance: Ctrl+Alt+P (0x04)
        /// - Turbo: Ctrl+Alt+T (0x05)
        /// </summary>
        public static List<CustomModeShortcut> GetDefaultShortcuts()
        {
            return new List<CustomModeShortcut>
            {
                new CustomModeShortcut("Eco", CodeEco, Keys.E, ctrl: true, alt: true, shift: false, win: false),
                new CustomModeShortcut("Quiet", CodeQuiet, Keys.Q, ctrl: true, alt: true, shift: false, win: false),
                new CustomModeShortcut("Balanced", CodeBalanced, Keys.B, ctrl: true, alt: true, shift: false, win: false),
                new CustomModeShortcut("Performance", CodePerformance, Keys.P, ctrl: true, alt: true, shift: false, win: false),
                new CustomModeShortcut("Turbo", CodeTurbo, Keys.T, ctrl: true, alt: true, shift: false, win: false)
            };
        }

        /// <summary>
        /// Returns the alternative numeric default shortcuts:
        /// - Quiet: Ctrl+Alt+1 (0x00)
        /// - Balanced: Ctrl+Alt+2 (0x01)
        /// - Performance: Ctrl+Alt+3 (0x04)
        /// - Turbo: Ctrl+Alt+4 (0x05)
        /// - Eco: Ctrl+Alt+5 (0x06)
        /// </summary>
        public static List<CustomModeShortcut> GetNumericDefaultShortcuts()
        {
            return new List<CustomModeShortcut>
            {
                new CustomModeShortcut("Quiet", CodeQuiet, Keys.D1, ctrl: true, alt: true, shift: false, win: false),
                new CustomModeShortcut("Balanced", CodeBalanced, Keys.D2, ctrl: true, alt: true, shift: false, win: false),
                new CustomModeShortcut("Performance", CodePerformance, Keys.D3, ctrl: true, alt: true, shift: false, win: false),
                new CustomModeShortcut("Turbo", CodeTurbo, Keys.D4, ctrl: true, alt: true, shift: false, win: false),
                new CustomModeShortcut("Eco", CodeEco, Keys.D5, ctrl: true, alt: true, shift: false, win: false)
            };
        }

        /// <summary>
        /// Returns the default shortcut for a specific mode code.
        /// </summary>
        public static CustomModeShortcut GetDefaultShortcutForMode(byte modeCode)
        {
            return modeCode switch
            {
                CodeQuiet => new CustomModeShortcut("Quiet", CodeQuiet, Keys.Q, ctrl: true, alt: true, shift: false, win: false),
                CodeBalanced => new CustomModeShortcut("Balanced", CodeBalanced, Keys.B, ctrl: true, alt: true, shift: false, win: false),
                CodePerformance => new CustomModeShortcut("Performance", CodePerformance, Keys.P, ctrl: true, alt: true, shift: false, win: false),
                CodeTurbo => new CustomModeShortcut("Turbo", CodeTurbo, Keys.T, ctrl: true, alt: true, shift: false, win: false),
                CodeEco => new CustomModeShortcut("Eco", CodeEco, Keys.E, ctrl: true, alt: true, shift: false, win: false),
                _ => new CustomModeShortcut("Balanced", CodeBalanced, Keys.B, ctrl: true, alt: true, shift: false, win: false)
            };
        }

        /// <summary>
        /// Deep clones a list of shortcuts.
        /// </summary>
        public static List<CustomModeShortcut> CloneShortcuts(IEnumerable<CustomModeShortcut>? shortcuts)
        {
            if (shortcuts is null) return new List<CustomModeShortcut>();
            return shortcuts.Where(s => s != null).Select(s => s.Clone()).ToList();
        }

        /// <summary>
        /// Formats a key and modifier combination into a readable string (e.g. "Ctrl+Alt+E").
        /// </summary>
        public static string FormatShortcut(Keys key, bool ctrl, bool alt, bool shift, bool win)
        {
            var parts = new List<string>(5);
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (win) parts.Add("Win");

            Keys baseKey = key & Keys.KeyCode;
            if (baseKey != Keys.None)
            {
                parts.Add(FormatKeyName(baseKey));
            }

            return parts.Count > 0 ? string.Join("+", parts) : "(None)";
        }

        /// <summary>
        /// Converts a virtual key into a human-friendly display name.
        /// </summary>
        public static string FormatKeyName(Keys key)
        {
            return key switch
            {
                Keys.D0 => "0",
                Keys.D1 => "1",
                Keys.D2 => "2",
                Keys.D3 => "3",
                Keys.D4 => "4",
                Keys.D5 => "5",
                Keys.D6 => "6",
                Keys.D7 => "7",
                Keys.D8 => "8",
                Keys.D9 => "9",
                Keys.NumPad0 => "NumPad 0",
                Keys.NumPad1 => "NumPad 1",
                Keys.NumPad2 => "NumPad 2",
                Keys.NumPad3 => "NumPad 3",
                Keys.NumPad4 => "NumPad 4",
                Keys.NumPad5 => "NumPad 5",
                Keys.NumPad6 => "NumPad 6",
                Keys.NumPad7 => "NumPad 7",
                Keys.NumPad8 => "NumPad 8",
                Keys.NumPad9 => "NumPad 9",
                Keys.Oemtilde => "~",
                Keys.Oemplus => "+",
                Keys.OemMinus => "-",
                Keys.OemQuestion => "?",
                Keys.Oemcomma => ",",
                Keys.OemPeriod => ".",
                Keys.OemPipe => "|",
                Keys.OemQuotes => "\"",
                Keys.OemSemicolon => ";",
                Keys.OemOpenBrackets => "[",
                Keys.OemCloseBrackets => "]",
                Keys.Space => "Space",
                _ => key.ToString()
            };
        }

        /// <summary>
        /// Resolves standard mode names to their firmware mode code byte.
        /// </summary>
        public static byte GetModeCode(string modeName)
        {
            if (string.Equals(modeName, "Eco", StringComparison.OrdinalIgnoreCase)) return CodeEco;
            if (string.Equals(modeName, "Quiet", StringComparison.OrdinalIgnoreCase)) return CodeQuiet;
            if (string.Equals(modeName, "Balanced", StringComparison.OrdinalIgnoreCase)) return CodeBalanced;
            if (string.Equals(modeName, "Performance", StringComparison.OrdinalIgnoreCase)) return CodePerformance;
            if (string.Equals(modeName, "Turbo", StringComparison.OrdinalIgnoreCase)) return CodeTurbo;
            return CodeBalanced;
        }

        /// <summary>
        /// Resolves mode code bytes to their canonical mode name string.
        /// </summary>
        public static string GetModeName(byte modeCode)
        {
            return modeCode switch
            {
                CodeEco => "Eco",
                CodeQuiet => "Quiet",
                CodeBalanced => "Balanced",
                CodePerformance => "Performance",
                CodeTurbo => "Turbo",
                _ => "Balanced"
            };
        }

        /// <summary>
        /// Determines whether a key is a standalone modifier key.
        /// </summary>
        /// <summary>
        /// Maps a rule id returned by the native core onto the user-facing
        /// message for that rule.
        /// </summary>
        private static bool ResolveRule(uint rule, Keys key, out string failureReason)
        {
            Keys baseKey = key & Keys.KeyCode;
            switch (rule)
            {
                case PredatorNativeMigrations.RuleOk:
                    failureReason = string.Empty;
                    return true;
                case PredatorNativeMigrations.RuleFunctionKey:
                    failureReason = $"Function keys (F1-F24) are not allowed in custom mode shortcuts ({baseKey}).";
                    return false;
                case PredatorNativeMigrations.RuleWinModifier:
                    failureReason = "Windows key combinations are reserved by Windows OS and cannot be bound.";
                    return false;
                case PredatorNativeMigrations.RuleNoPrimaryKey:
                    failureReason = "No primary key specified.";
                    return false;
                case PredatorNativeMigrations.RuleWinBaseKey:
                    failureReason = "Windows key cannot be used as a shortcut key.";
                    return false;
                case PredatorNativeMigrations.RuleModifierAlone:
                    failureReason = "Modifier keys (Ctrl, Alt, Shift, Win) cannot be used alone as primary keys.";
                    return false;
                case PredatorNativeMigrations.RuleNoModifier:
                    failureReason = "Shortcut must include at least one modifier key (Ctrl, Alt, or Shift) to prevent hijacking normal keyboard typing.";
                    return false;
                case PredatorNativeMigrations.RuleReservedSystemKey:
                    failureReason = $"Key '{baseKey}' is a reserved system/hardware control key and cannot be bound.";
                    return false;
                case PredatorNativeMigrations.RuleAltSpace:
                    failureReason = "Alt+Space is reserved by Windows for the window system menu.";
                    return false;
                case PredatorNativeMigrations.RuleCtrlAltDel:
                    failureReason = "Ctrl+Alt+Del is reserved by Windows for security/logon attention.";
                    return false;
                case PredatorNativeMigrations.RuleCtrlShiftEsc:
                    failureReason = "Ctrl+Shift+Esc is reserved by Windows for Task Manager.";
                    return false;
                case PredatorNativeMigrations.RuleAltTabOrEsc:
                    failureReason = $"Alt+{baseKey} is reserved by Windows for window switching/cycling.";
                    return false;
                case PredatorNativeMigrations.RuleAltEnter:
                    failureReason = "Alt+Enter is reserved for window properties / fullscreen toggle.";
                    return false;
                case PredatorNativeMigrations.RuleCtrlEditingKey:
                    failureReason = ReservedCtrlEditingKeys.TryGetValue(baseKey, out string? reservedName)
                        ? $"'{reservedName}' is a reserved standard application shortcut."
                        : "Reserved standard application shortcut.";
                    return false;
                default:
                    failureReason = string.Empty;
                    return true;
            }
        }

        public static bool IsModifierKey(Keys key)
        {
            Keys baseKey = key & Keys.KeyCode;
            return baseKey is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
                or Keys.Menu or Keys.LMenu or Keys.RMenu
                or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
                or Keys.LWin or Keys.RWin or Keys.Modifiers;
        }
    }
}
