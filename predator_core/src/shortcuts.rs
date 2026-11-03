//! Custom mode-shortcut validation rules.
//!
//! Ports `ModeShortcutValidator.IsValid` from the managed application. The
//! wording of human-facing messages deliberately stays in C#: this module
//! returns a rule identifier and the managed layer maps it to the exact string
//! its callers (and tests) expect. That keeps the decision authoritative here
//! while leaving localization/formatting where it belongs.

/// WinForms `Keys.KeyCode` mask.
pub const KEY_CODE_MASK: u32 = 0xFFFF;
/// WinForms `Keys.Shift` flag.
pub const MOD_SHIFT: u32 = 0x10000;
/// WinForms `Keys.Control` flag.
pub const MOD_CONTROL: u32 = 0x20000;
/// WinForms `Keys.Alt` flag.
pub const MOD_ALT: u32 = 0x40000;

/// A shortcut satisfied every rule.
pub const OK: u32 = 0;
/// Function keys F1-F24 are never bindable.
pub const E_FUNCTION_KEY: u32 = 1;
/// Any Win-key chord is reserved by the OS.
pub const E_WIN_MODIFIER: u32 = 2;
/// No primary key supplied.
pub const E_NO_PRIMARY_KEY: u32 = 3;
/// The Windows key itself used as the primary key.
pub const E_WIN_BASE_KEY: u32 = 4;
/// A bare modifier used as the primary key.
pub const E_MODIFIER_ALONE: u32 = 5;
/// No Ctrl/Alt/Shift held, so typing would be hijacked.
pub const E_NO_MODIFIER: u32 = 6;
/// Reserved system/hardware control key.
pub const E_RESERVED_SYSTEM_KEY: u32 = 7;
/// Alt+Space opens the window system menu.
pub const E_ALT_SPACE: u32 = 8;
/// Ctrl+Alt+Del is the secure attention sequence.
pub const E_CTRL_ALT_DEL: u32 = 9;
/// Ctrl+Shift+Esc opens Task Manager.
pub const E_CTRL_SHIFT_ESC: u32 = 10;
/// Alt+Tab / Alt+Esc cycle windows.
pub const E_ALT_TAB_OR_ESC: u32 = 11;
/// Alt+Enter toggles window properties / fullscreen.
pub const E_ALT_ENTER: u32 = 12;
/// A standard Ctrl editing/application chord.
pub const E_CTRL_EDITING_KEY: u32 = 13;

/// VK_F1 (0x70).
pub const MIN_FUNCTION_KEY_VK: u32 = 0x70;
/// VK_F24 (0x87).
pub const MAX_FUNCTION_KEY_VK: u32 = 0x87;

const VK_SPACE: u32 = 0x20;
const VK_TAB: u32 = 0x09;
const VK_ESCAPE: u32 = 0x1B;
const VK_DELETE: u32 = 0x2E;
const VK_RETURN: u32 = 0x0D;
const VK_LWIN: u32 = 0x5B;
const VK_RWIN: u32 = 0x5C;

/// Escape, Tab, PrintScreen, Pause, NumLock, CapsLock, Scroll, Apps, Sleep.
const RESERVED_SYSTEM_KEYS: &[u32] = &[0x1B, 0x09, 0x2C, 0x13, 0x90, 0x14, 0x91, 0x5D, 0x5F];

/// Ctrl+<key> chords that belong to standard application editing.
const RESERVED_CTRL_KEYS: &[u32] = &[
    0x43, // C copy
    0x56, // V paste
    0x58, // X cut
    0x5A, // Z undo
    0x59, // Y redo
    0x41, // A select all
    0x53, // S save
    0x50, // P print
    0x4F, // O open
    0x4E, // N new
    0x57, // W close
    0x46, // F find
    0x48, // H history/replace
    0x54, // T new tab
    0x44, // D bookmark/delete line
    0x52, // R refresh
    0x4A, // J downloads/console
    0x09, // Tab switch tabs
];

/// True when `vk` is a standalone modifier (Shift/Ctrl/Alt/Win, left or right).
pub fn is_modifier_key(vk: u32) -> bool {
    matches!(
        vk,
        0x10 | 0x11
            | 0x12
            | 0xA0
            | 0xA1
            | 0xA2
            | 0xA3
            | 0xA4
            | 0xA5
            | 0x5B
            | 0x5C
    )
}

/// True when `base` is one of the reserved Ctrl-only editing chords.
pub fn is_reserved_ctrl_key(base: u32) -> bool {
    RESERVED_CTRL_KEYS.contains(&base)
}

/// Validates a key and modifier combination, returning a rule identifier.
///
/// `key` is a WinForms `Keys` value; the modifier bits embedded in it are
/// folded into the explicit flags exactly as the managed implementation does.
pub fn validate(key: u32, ctrl_in: bool, alt_in: bool, shift_in: bool, win_in: bool) -> u32 {
    let base = key & KEY_CODE_MASK;
    let ctrl = ctrl_in || (key & MOD_CONTROL) != 0;
    let alt = alt_in || (key & MOD_ALT) != 0;
    let shift = shift_in || (key & MOD_SHIFT) != 0;
    let win = win_in;
    let _ = shift;

    // 1. Function keys are never bindable, with or without modifiers.
    if (MIN_FUNCTION_KEY_VK..=MAX_FUNCTION_KEY_VK).contains(&base) {
        return E_FUNCTION_KEY;
    }

    // 2. Any Win chord is reserved by the OS.
    if win {
        return E_WIN_MODIFIER;
    }

    // 3. A primary key must exist.
    if base == 0 {
        return E_NO_PRIMARY_KEY;
    }

    // 4. The Windows key itself is not a bindable primary key.
    if base == VK_LWIN || base == VK_RWIN {
        return E_WIN_BASE_KEY;
    }

    // 5. A bare modifier cannot be the primary key.
    if is_modifier_key(base) {
        return E_MODIFIER_ALONE;
    }

    // 6. At least one modifier, so ordinary typing is never swallowed.
    if !ctrl && !alt && !shift {
        return E_NO_MODIFIER;
    }

    // 7. Reserved system / hardware control keys.
    if RESERVED_SYSTEM_KEYS.contains(&base) {
        return E_RESERVED_SYSTEM_KEY;
    }

    // 8. Chords Windows owns outright.
    if alt && base == VK_SPACE && !ctrl {
        return E_ALT_SPACE;
    }
    if ctrl && alt && base == VK_DELETE {
        return E_CTRL_ALT_DEL;
    }
    if ctrl && shift && base == VK_ESCAPE {
        return E_CTRL_SHIFT_ESC;
    }
    if alt && !ctrl && (base == VK_TAB || base == VK_ESCAPE) {
        return E_ALT_TAB_OR_ESC;
    }
    if alt && !ctrl && base == VK_RETURN {
        return E_ALT_ENTER;
    }

    // 9. Standard Ctrl application shortcuts must keep working.
    if ctrl && !alt && is_reserved_ctrl_key(base) {
        return E_CTRL_EDITING_KEY;
    }

    OK
}

/// True when two combinations use the same key and modifiers, ignoring target mode.
pub fn same_combination(
    key_a: u32,
    ctrl_a: bool,
    alt_a: bool,
    shift_a: bool,
    win_a: bool,
    key_b: u32,
    ctrl_b: bool,
    alt_b: bool,
    shift_b: bool,
    win_b: bool,
) -> bool {
    (key_a & KEY_CODE_MASK) == (key_b & KEY_CODE_MASK)
        && ctrl_a == ctrl_b
        && alt_a == alt_b
        && shift_a == shift_b
        && win_a == win_b
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_default_chords() {
        // Ctrl+Alt+E / Q / B / P / T
        for vk in [0x45, 0x51, 0x42, 0x50, 0x54] {
            assert_eq!(validate(vk, true, true, false, false), OK);
        }
        // Numeric defaults Ctrl+Alt+1..5
        for vk in 0x31..=0x35 {
            assert_eq!(validate(vk, true, true, false, false), OK);
        }
    }

    #[test]
    fn rejects_function_keys() {
        for vk in MIN_FUNCTION_KEY_VK..=MAX_FUNCTION_KEY_VK {
            assert_eq!(validate(vk, true, true, false, false), E_FUNCTION_KEY);
        }
    }

    #[test]
    fn rejects_win_chords() {
        assert_eq!(validate(0x45, false, false, false, true), E_WIN_MODIFIER);
        assert_eq!(validate(0x5B, true, false, false, false), E_WIN_BASE_KEY);
    }

    #[test]
    fn rejects_bare_and_modifier_only() {
        assert_eq!(validate(0, true, false, false, false), E_NO_PRIMARY_KEY);
        assert_eq!(validate(0xA2, false, false, false, false), E_MODIFIER_ALONE);
        assert_eq!(validate(0x45, false, false, false, false), E_NO_MODIFIER);
    }

    #[test]
    fn rejects_system_keys() {
        assert_eq!(validate(0x1B, true, false, false, false), E_RESERVED_SYSTEM_KEY);
        assert_eq!(validate(0x09, false, true, false, false), E_RESERVED_SYSTEM_KEY);
        assert_eq!(validate(0x90, true, false, false, false), E_RESERVED_SYSTEM_KEY);
    }

    #[test]
    fn rejects_os_owned_chords() {
        assert_eq!(validate(VK_SPACE, false, true, false, false), E_ALT_SPACE);
        assert_eq!(validate(VK_DELETE, true, true, false, false), E_CTRL_ALT_DEL);
        assert_eq!(validate(VK_RETURN, false, true, false, false), E_ALT_ENTER);
        // Escape and Tab are rejected earlier as reserved system keys, so the
        // Ctrl+Shift+Esc and Alt+Tab branches below can never be reached. The
        // ordering is preserved to stay byte-identical with the managed rules.
        assert_eq!(validate(VK_ESCAPE, true, false, true, false), E_RESERVED_SYSTEM_KEY);
        assert_eq!(validate(VK_TAB, false, true, false, false), E_RESERVED_SYSTEM_KEY);
        assert_eq!(E_CTRL_SHIFT_ESC, 10);
        assert_eq!(E_ALT_TAB_OR_ESC, 11);
    }

    #[test]
    fn rejects_ctrl_editing_chords() {
        for vk in [0x43, 0x56, 0x58, 0x5A, 0x41, 0x53] {
            assert_eq!(validate(vk, true, false, false, false), E_CTRL_EDITING_KEY);
        }
        // Allowed once Alt is also held.
        assert_eq!(validate(0x43, true, true, false, false), OK);
    }

    #[test]
    fn modifier_bits_inside_key_are_folded() {
        // Ctrl+Alt+E encoded in the Keys value itself.
        assert_eq!(validate(0x45 | MOD_CONTROL | MOD_ALT, false, false, false, false), OK);
    }

    #[test]
    fn detects_duplicate_combinations() {
        assert!(same_combination(0x45, true, true, false, false, 0x45, true, true, false, false));
        assert!(!same_combination(0x45, true, true, false, false, 0x45, true, false, false, false));
    }
}
