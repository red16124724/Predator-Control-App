//! Low-level keyboard hook decision engine.
//!
//! Ports the state machine inside `KeyboardHook.HookCallback`. `SetWindowsHookEx`
//! and the message pump must stay managed because the hook procedure has
//! thread affinity and the delegate must stay alive for the hook's lifetime.
//! Everything that decides *what a keystroke means* lives here: modifier
//! tracking, injected-key rejection, function-key pass-through, the dedicated
//! Predator / Mode hardware scancodes, the Predator+number combos and custom
//! mode shortcuts.
//!
//! Timestamps are supplied by the caller (`Environment.TickCount64`) so the
//! engine stays deterministic and unit-testable.

use std::sync::Mutex;

/// The keystroke should be passed through untouched.
pub const ACTION_PASS: i32 = 0;
/// The Predator / NitroSense key was tapped; fire its action.
pub const ACTION_PREDATOR_SENSE: i32 = 1;
/// The dedicated Mode key was pressed; cycle the operating mode.
pub const ACTION_MODE_KEY: i32 = 2;
/// Predator + number 1-5; `out_value` carries the number.
pub const ACTION_NUMBER_KEY: i32 = 3;
/// A custom mode shortcut matched; `out_value` carries the firmware mode code.
pub const ACTION_CUSTOM_SHORTCUT: i32 = 4;

/// Dedicated Predator hardware scancode (0x75).
pub const SC_PREDATOR: u32 = 0x75;
/// Dedicated Mode hardware scancode (0x76).
pub const SC_MODE: u32 = 0x76;
/// Alternate Mode hardware scancode (0x77).
pub const SC_MODE_ALT: u32 = 0x77;

const LLKHF_LOWER_IL_INJECTED: u32 = 0x02;
const LLKHF_INJECTED: u32 = 0x10;

const MIN_FUNCTION_VK: u32 = 0x70;
const MAX_FUNCTION_VK: u32 = 0x87;
const MIN_MEDIA_VK: u32 = 0xAD;
const MAX_MEDIA_VK: u32 = 0xB7;

const PREDATOR_HOLD_TIMEOUT_MS: i64 = 3000;
const MODE_KEY_THROTTLE_MS: i64 = 250;
const PREDATOR_SENSE_THROTTLE_MS: i64 = 250;
const SHORTCUT_REPEAT_MS: i64 = 350;

/// Maximum number of custom shortcuts the engine tracks.
pub const MAX_SHORTCUTS: usize = 16;

/// One configured custom mode shortcut.
#[derive(Clone, Copy, Debug, Default)]
#[repr(C)]
pub struct ShortcutEntry {
    pub vk: u32,
    pub mode_code: u32,
    pub ctrl: u32,
    pub alt: u32,
    pub shift: u32,
    pub win: u32,
    pub enabled: u32,
}

impl ShortcutEntry {
    const EMPTY: Self = Self {
        vk: 0,
        mode_code: 0,
        ctrl: 0,
        alt: 0,
        shift: 0,
        win: 0,
        enabled: 0,
    };
}

/// Persistent hook state.
#[derive(Debug)]
pub struct HookState {
    ctrl: bool,
    alt: bool,
    shift: bool,
    win: bool,
    predator_pressed: bool,
    predator_used_in_combo: bool,
    suppress_predator_release: bool,
    active_number: i32,
    last_shortcut_vk: u32,
    last_shortcut_tick: i64,
    last_mode_tick: i64,
    last_sense_tick: i64,
    predator_press_tick: i64,
    allow_injected: bool,
    shortcuts: [ShortcutEntry; MAX_SHORTCUTS],
    shortcut_count: usize,
}

impl Default for HookState {
    fn default() -> Self {
        Self::new()
    }
}

impl HookState {
    pub const fn new() -> Self {
        Self {
            ctrl: false,
            alt: false,
            shift: false,
            win: false,
            predator_pressed: false,
            predator_used_in_combo: false,
            suppress_predator_release: false,
            active_number: 0,
            last_shortcut_vk: 0,
            last_shortcut_tick: 0,
            last_mode_tick: 0,
            last_sense_tick: 0,
            predator_press_tick: 0,
            allow_injected: false,
            shortcuts: [ShortcutEntry::EMPTY; MAX_SHORTCUTS],
            shortcut_count: 0,
        }
    }

    /// Accept synthetic keystrokes. Defaults to false so unprivileged software
    /// cannot spoof the Predator or Mode keys.
    pub fn set_allow_injected(&mut self, allow: bool) {
        self.allow_injected = allow;
    }

    pub fn allow_injected(&self) -> bool {
        self.allow_injected
    }

    /// Replaces the tracked shortcut table, clamping to [`MAX_SHORTCUTS`].
    ///
    /// # Safety
    /// `entries` must be valid for `count` reads.
    pub unsafe fn set_shortcuts(&mut self, entries: *const ShortcutEntry, count: i32) {
        self.shortcuts = [ShortcutEntry::EMPTY; MAX_SHORTCUTS];
        self.shortcut_count = 0;
        if (entries as usize) < 0x10000 || count <= 0 {
            return;
        }
        let n = (count as usize).min(MAX_SHORTCUTS);
        if (entries as usize) % std::mem::align_of::<ShortcutEntry>() == 0 {
            let slice = std::slice::from_raw_parts(entries, n);
            for (i, e) in slice.iter().enumerate() {
                if e.enabled != 0 && e.vk != 0 {
                    self.shortcuts[i] = *e;
                    self.shortcut_count = i + 1;
                }
            }
        } else {
            for i in 0..n {
                let e = std::ptr::read_unaligned(entries.add(i));
                if e.enabled != 0 && e.vk != 0 {
                    self.shortcuts[i] = e;
                    self.shortcut_count = i + 1;
                }
            }
        }
    }

    pub fn shortcut_count(&self) -> usize {
        self.shortcut_count
    }

    /// Clears every piece of transient state. Called on resume, unlock and
    /// desktop transitions so a held modifier can never wedge.
    pub fn reset(&mut self) {
        self.ctrl = false;
        self.alt = false;
        self.shift = false;
        self.win = false;
        self.predator_pressed = false;
        self.predator_used_in_combo = false;
        self.suppress_predator_release = false;
        self.active_number = 0;
        self.last_shortcut_vk = 0;
    }

    /// True while the Predator / NitroSense key is being held.
    pub fn predator_pressed(&self) -> bool {
        self.predator_pressed
    }

    /// Current modifier mask: bit0 Ctrl, bit1 Alt, bit2 Shift, bit3 Win.
    pub fn modifier_mask(&self) -> u32 {
        (self.ctrl as u32)
            | ((self.alt as u32) << 1)
            | ((self.shift as u32) << 2)
            | ((self.win as u32) << 3)
    }

    fn clear_predator_state(&mut self) {
        self.predator_pressed = false;
        self.predator_used_in_combo = false;
        self.suppress_predator_release = false;
        self.active_number = 0;
    }

    fn track_modifier(&mut self, vk: u32, down: bool) {
        match vk {
            0xA2 | 0xA3 | 0x11 => self.ctrl = down,
            0xA4 | 0xA5 | 0x12 => self.alt = down,
            0xA0 | 0xA1 | 0x10 => self.shift = down,
            0x5B | 0x5C => self.win = down,
            _ => {}
        }
    }

    fn match_shortcut(&self, vk: u32) -> Option<&ShortcutEntry> {
        let ctrl = self.ctrl as u32;
        let alt = self.alt as u32;
        let shift = self.shift as u32;
        let win = self.win as u32;
        self.shortcuts[..self.shortcut_count].iter().find(|s| {
            s.enabled != 0
                && s.vk == vk
                && s.ctrl == ctrl
                && s.alt == alt
                && s.shift == shift
                && s.win == win
        })
    }

    /// Decides what a keystroke means.
    ///
    /// Returns `(action, value)` where `value` is the mode code for
    /// [`ACTION_CUSTOM_SHORTCUT`] or the digit for [`ACTION_NUMBER_KEY`].
    pub fn process(
        &mut self,
        vk: u32,
        scan: u32,
        flags: u32,
        is_down: bool,
        now_ms: i64,
    ) -> (i32, u32) {
        self.track_modifier(vk, is_down);

        // Reject synthetic input unless explicitly permitted.
        let injected = (flags & LLKHF_INJECTED) != 0 || (flags & LLKHF_LOWER_IL_INJECTED) != 0;
        if injected && !self.allow_injected {
            return (ACTION_PASS, 0);
        }

        // Function and media/launch keys always pass through untouched.
        if (MIN_FUNCTION_VK..=MAX_FUNCTION_VK).contains(&vk)
            || (MIN_MEDIA_VK..=MAX_MEDIA_VK).contains(&vk)
        {
            if is_down && self.predator_pressed {
                self.clear_predator_state();
            }
            return (ACTION_PASS, 0);
        }

        // Release the auto-repeat guard for the last triggered shortcut.
        if !is_down && vk == self.last_shortcut_vk {
            self.last_shortcut_vk = 0;
        }

        // Custom mode shortcuts, on key-down of a non-modifier key.
        if is_down && !is_modifier_key(vk) {
            if let Some(entry) = self.match_shortcut(vk).copied() {
                let repeated = self.last_shortcut_vk == vk
                    && (now_ms - self.last_shortcut_tick) < SHORTCUT_REPEAT_MS;
                if repeated {
                    return (ACTION_PASS, 0);
                }
                self.last_shortcut_vk = vk;
                self.last_shortcut_tick = now_ms;
                // Pass through so application state stays predictable.
                return (ACTION_CUSTOM_SHORTCUT, entry.mode_code);
            }
        }

        // Keys that must never be reinterpreted as hardware hotkeys, even when the
        // incoming scancode happens to match the Mode or Predator scancode.
        // Windows keys, modifiers, navigation, control, media, browser and OEM keys
        // all pass through untouched, and any held Predator state is cleared.
        if is_passthrough_key(vk) {
            if is_down && self.predator_pressed {
                self.clear_predator_state();
            }
            return (ACTION_PASS, 0);
        }

        // F6/F7/F8 are ordinary function keys on PC keyboards: never reinterpret.
        if vk == 0x75 || vk == 0x76 || vk == 0x77 {
            if is_down && self.predator_pressed {
                self.clear_predator_state();
            }
            return (ACTION_PASS, 0);
        }

        // Dedicated Mode key: a hardware scancode that is not a function key.
        let is_mode_key = (scan == SC_MODE || scan == SC_MODE_ALT)
            && !(MIN_FUNCTION_VK..=MAX_FUNCTION_VK).contains(&vk);

        if is_mode_key {
            if is_down && (now_ms - self.last_mode_tick) >= MODE_KEY_THROTTLE_MS {
                self.last_mode_tick = now_ms;
                return (ACTION_MODE_KEY, 0);
            }
            return (ACTION_PASS, 0);
        }

        // Dedicated Predator / NitroSense key.
        let is_predator_key =
            scan == SC_PREDATOR && !(MIN_FUNCTION_VK..=MAX_FUNCTION_VK).contains(&vk);

        if is_predator_key {
            if is_down {
                if !self.predator_pressed {
                    self.predator_pressed = true;
                    self.predator_press_tick = now_ms;
                    self.predator_used_in_combo = false;
                }
            } else {
                let fire = !self.predator_used_in_combo
                    && !self.suppress_predator_release
                    && (now_ms - self.last_sense_tick) >= PREDATOR_SENSE_THROTTLE_MS;
                if fire {
                    self.last_sense_tick = now_ms;
                }
                self.clear_predator_state();
                if fire {
                    return (ACTION_PREDATOR_SENSE, 0);
                }
            }
            return (ACTION_PASS, 0);
        }

        // A stuck Predator key must never hold the number row hostage.
        if self.predator_pressed && (now_ms - self.predator_press_tick) > PREDATOR_HOLD_TIMEOUT_MS {
            self.clear_predator_state();
        }

        // Predator + 1-5 (top row or numpad).
        let is_number_key = (0x31..=0x35).contains(&vk) || (0x61..=0x65).contains(&vk);
        if is_number_key {
            let within_window = (now_ms - self.predator_press_tick) <= PREDATOR_HOLD_TIMEOUT_MS;
            if self.predator_pressed && within_window {
                let number = if vk >= 0x61 { vk - 0x60 } else { vk - 0x30 };
                if is_down {
                    if self.active_number != number as i32 {
                        self.predator_used_in_combo = true;
                        self.suppress_predator_release = true;
                        self.active_number = number as i32;
                        // Number keys always pass through so typing is never swallowed.
                        return (ACTION_NUMBER_KEY, number);
                    }
                } else if self.active_number == number as i32 {
                    self.active_number = 0;
                }
            }
            return (ACTION_PASS, 0);
        }

        // Any other key press cancels a held Predator key.
        if is_down && self.predator_pressed {
            self.clear_predator_state();
        }

        (ACTION_PASS, 0)
    }
}

/// True for standalone modifier virtual keys.
pub fn is_modifier_key(vk: u32) -> bool {
    matches!(
        vk,
        0x10 | 0x11 | 0x12 | 0xA0 | 0xA1 | 0xA2 | 0xA3 | 0xA4 | 0xA5 | 0x5B | 0x5C
    )
}

/// True for keys that must always reach Windows untranslated.
///
/// This mirrors `KeyboardHook.IsPassthroughKey` exactly: function keys, Windows
/// keys, modifiers, navigation/editing keys, media, browser, common control and
/// OEM keys. It exists so that a hardware scancode which collides with the Mode
/// or Predator scancode can never hijack an ordinary key.
pub fn is_passthrough_key(vk: u32) -> bool {
    // F1..F24 (includes VK_F6/F7/F8 at 0x75/0x76/0x77).
    if (0x70..=0x87).contains(&vk) {
        return true;
    }
    // VK_LWIN..VK_SLEEP.
    if (0x5B..=0x5F).contains(&vk) {
        return true;
    }
    // Tab, Shift..CapsLock, Escape, NumLock/ScrollLock, left/right modifiers.
    if vk == 0x09
        || (0x10..=0x14).contains(&vk)
        || vk == 0x1B
        || (0x90..=0x91).contains(&vk)
        || (0xA0..=0xA5).contains(&vk)
    {
        return true;
    }
    // Navigation / cursor / editing keys.
    if (0x21..=0x2F).contains(&vk) {
        return true;
    }
    // Media and launch keys.
    if (0xAD..=0xB7).contains(&vk) {
        return true;
    }
    // Browser keys.
    if (0xA6..=0xAC).contains(&vk) {
        return true;
    }
    // Cancel, Backspace, Clear, Return, Space.
    if matches!(vk, 0x03 | 0x08 | 0x0C | 0x0D | 0x20) {
        return true;
    }
    // OEM keys: 0xBA..0xC0, 0xDB..0xF5, 0x92..0x96.
    if (0xBA..=0xC0).contains(&vk) || (0xDB..=0xF5).contains(&vk) || (0x92..=0x96).contains(&vk) {
        return true;
    }
    false
}

static GLOBAL_HOOK: Mutex<HookState> = Mutex::new(HookState::new());

/// Runs `f` against the global hook state.
pub fn with_hook<F, R>(_default: R, f: F) -> R
where
    F: FnOnce(&mut HookState) -> R,
{
    let mut guard = GLOBAL_HOOK.lock().unwrap_or_else(|e| e.into_inner());
    f(&mut guard)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn down(state: &mut HookState, vk: u32, now: i64) -> (i32, u32) {
        state.process(vk, 0, 0, true, now)
    }

    fn up(state: &mut HookState, vk: u32, now: i64) -> (i32, u32) {
        state.process(vk, 0, 0, false, now)
    }

    #[test]
    fn modifier_state_tracks_press_and_release() {
        let mut s = HookState::new();
        down(&mut s, 0xA2, 0);
        assert_eq!(s.modifier_mask() & 1, 1);
        up(&mut s, 0xA2, 10);
        assert_eq!(s.modifier_mask() & 1, 0);
    }

    #[test]
    fn function_keys_always_pass_through() {
        let mut s = HookState::new();
        for vk in MIN_FUNCTION_VK..=MAX_FUNCTION_VK {
            assert_eq!(down(&mut s, vk, 0).0, ACTION_PASS);
        }
    }

    #[test]
    fn media_keys_pass_through() {
        let mut s = HookState::new();
        for vk in MIN_MEDIA_VK..=MAX_MEDIA_VK {
            assert_eq!(down(&mut s, vk, 0).0, ACTION_PASS);
        }
    }

    #[test]
    fn f6_f7_f8_are_never_reinterpreted() {
        let mut s = HookState::new();
        // Scancode of the Predator key but an ordinary F6 virtual key.
        assert_eq!(s.process(0x75, SC_PREDATOR, 0, true, 0).0, ACTION_PASS);
        assert_eq!(s.process(0x75, SC_PREDATOR, 0, false, 10).0, ACTION_PASS);
    }

    #[test]
    fn dedicated_mode_key_fires_and_passes_through() {
        let mut s = HookState::new();
        // A non-function virtual key carried on the Mode scancode.
        assert_eq!(s.process(0x41, SC_MODE, 0, true, 10_000).0, ACTION_MODE_KEY);
        // Throttled within 250 ms.
        assert_eq!(s.process(0x41, SC_MODE, 0, true, 10_100).0, ACTION_PASS);
        assert_eq!(s.process(0x41, SC_MODE, 0, true, 10_300).0, ACTION_MODE_KEY);
    }

    #[test]
    fn predator_tap_fires_on_release() {
        let mut s = HookState::new();
        assert_eq!(s.process(0x6C, SC_PREDATOR, 0, true, 10_000).0, ACTION_PASS);
        assert_eq!(
            s.process(0x6C, SC_PREDATOR, 0, false, 10_200).0,
            ACTION_PREDATOR_SENSE
        );
        // A second tap 200 ms later is throttled.
        assert_eq!(s.process(0x6C, SC_PREDATOR, 0, true, 10_400).0, ACTION_PASS);
        assert_eq!(
            s.process(0x6C, SC_PREDATOR, 0, false, 10_400).0,
            ACTION_PASS
        );
    }

    #[test]
    fn predator_plus_number_emits_digit_and_suppresses_tap() {
        let mut s = HookState::new();
        s.process(0x6C, SC_PREDATOR, 0, true, 0);
        assert_eq!(down(&mut s, 0x33, 50), (ACTION_NUMBER_KEY, 3));
        up(&mut s, 0x33, 80);
        // Release must not also fire the Predator action.
        assert_eq!(s.process(0x6C, SC_PREDATOR, 0, false, 120).0, ACTION_PASS);
    }

    #[test]
    fn numpad_numbers_map_to_digits() {
        let mut s = HookState::new();
        s.process(0x6C, SC_PREDATOR, 0, true, 0);
        assert_eq!(down(&mut s, 0x65, 50), (ACTION_NUMBER_KEY, 5));
    }

    #[test]
    fn number_without_predator_is_plain_typing() {
        let mut s = HookState::new();
        assert_eq!(down(&mut s, 0x31, 0).0, ACTION_PASS);
    }

    #[test]
    fn held_predator_key_times_out() {
        let mut s = HookState::new();
        s.process(0x6C, SC_PREDATOR, 0, true, 0);
        // 3001 ms later the combo window has lapsed.
        assert_eq!(down(&mut s, 0x31, 3001).0, ACTION_PASS);
    }

    #[test]
    fn other_key_cancels_predator_combo() {
        let mut s = HookState::new();
        s.process(0x6C, SC_PREDATOR, 0, true, 0);
        down(&mut s, 0x41, 20);
        // Now a number is no longer a combo.
        assert_eq!(down(&mut s, 0x31, 40).0, ACTION_PASS);
    }

    #[test]
    fn injected_keys_are_rejected_by_default() {
        let mut s = HookState::new();
        assert_eq!(
            s.process(0x6C, SC_PREDATOR, LLKHF_INJECTED, true, 0).0,
            ACTION_PASS
        );
        assert_eq!(
            s.process(0x6C, SC_PREDATOR, LLKHF_LOWER_IL_INJECTED, true, 0)
                .0,
            ACTION_PASS
        );
        s.set_allow_injected(true);
        s.process(0x6C, SC_PREDATOR, LLKHF_INJECTED, true, 20_000);
        assert_eq!(
            s.process(0x6C, SC_PREDATOR, LLKHF_INJECTED, false, 20_200)
                .0,
            ACTION_PREDATOR_SENSE
        );
    }

    #[test]
    fn custom_shortcut_matches_exact_modifier_set() {
        let mut s = HookState::new();
        let entry = ShortcutEntry {
            vk: 0x45, // E
            mode_code: 0x06,
            ctrl: 1,
            alt: 1,
            shift: 0,
            win: 0,
            enabled: 1,
        };
        unsafe { s.set_shortcuts(&entry, 1) };
        assert_eq!(s.shortcut_count(), 1);

        // Without modifiers it must not match.
        assert_eq!(down(&mut s, 0x45, 0).0, ACTION_PASS);

        down(&mut s, 0xA2, 10); // Ctrl
        down(&mut s, 0xA4, 20); // Alt
        assert_eq!(down(&mut s, 0x45, 30), (ACTION_CUSTOM_SHORTCUT, 0x06));
    }

    #[test]
    fn custom_shortcut_suppresses_repeat() {
        let mut s = HookState::new();
        let entry = ShortcutEntry {
            vk: 0x51,
            mode_code: 0x00,
            ctrl: 1,
            alt: 1,
            shift: 0,
            win: 0,
            enabled: 1,
        };
        unsafe { s.set_shortcuts(&entry, 1) };
        down(&mut s, 0xA2, 0);
        down(&mut s, 0xA4, 0);
        assert_eq!(down(&mut s, 0x51, 100).0, ACTION_CUSTOM_SHORTCUT);
        assert_eq!(down(&mut s, 0x51, 200).0, ACTION_PASS);
        assert_eq!(down(&mut s, 0x51, 500).0, ACTION_CUSTOM_SHORTCUT);
    }

    #[test]
    fn disabled_shortcuts_are_ignored() {
        let mut s = HookState::new();
        let entry = ShortcutEntry {
            vk: 0x45,
            mode_code: 0x06,
            ctrl: 1,
            alt: 1,
            shift: 0,
            win: 0,
            enabled: 0,
        };
        unsafe { s.set_shortcuts(&entry, 1) };
        down(&mut s, 0xA2, 0);
        down(&mut s, 0xA4, 0);
        assert_eq!(down(&mut s, 0x45, 10).0, ACTION_PASS);
    }

    #[test]
    fn shortcut_table_is_clamped() {
        let mut s = HookState::new();
        let many = [ShortcutEntry {
            vk: 0x45,
            mode_code: 1,
            ctrl: 1,
            alt: 0,
            shift: 0,
            win: 0,
            enabled: 1,
        }; 40];
        unsafe { s.set_shortcuts(many.as_ptr(), 40) };
        assert_eq!(s.shortcut_count(), MAX_SHORTCUTS);
    }

    #[test]
    fn null_or_empty_shortcut_table_is_safe() {
        let mut s = HookState::new();
        unsafe {
            s.set_shortcuts(std::ptr::null(), 5);
            assert_eq!(s.shortcut_count(), 0);
            s.set_shortcuts(std::ptr::null(), 0);
            assert_eq!(s.shortcut_count(), 0);
        }
    }

    #[test]
    fn windows_modifier_and_oem_keys_pass_through_even_on_mode_scancode() {
        let mut s = HookState::new();
        // The exact set the managed suite exercises, all carrying the Mode scancode.
        let passthrough: [u32; 18] = [
            0x5B, 0x5C, 0x5D, 0x09, 0x10, 0x11, 0x12, 0x1B, 0xA0, 0xA1, 0xA2, 0xA4, 0xBA, 0xBB,
            0xBF, 0xDB, 0xDC, 0xE2,
        ];
        for vk in passthrough {
            assert_eq!(
                s.process(vk, SC_MODE, 0, true, 10_000).0,
                ACTION_PASS,
                "vk {vk:#x} must not trigger the mode key"
            );
            assert_eq!(
                s.process(vk, SC_MODE, 0, false, 10_050).0,
                ACTION_PASS,
                "vk {vk:#x}"
            );
        }
    }

    #[test]
    fn passthrough_predicate_covers_expected_ranges() {
        for vk in [
            0x70u32, 0x75, 0x87, 0x5B, 0x5D, 0xA0, 0xA5, 0x21, 0x2F, 0xAD, 0xB7, 0xA6, 0xBA, 0xF5,
        ] {
            assert!(is_passthrough_key(vk), "{vk:#x} should pass through");
        }
        // Ordinary letters and digits are not passthrough.
        for vk in [0x41u32, 0x45, 0x31, 0x35] {
            assert!(!is_passthrough_key(vk), "{vk:#x} should not pass through");
        }
    }

    #[test]
    fn reset_clears_held_modifiers() {
        let mut s = HookState::new();
        down(&mut s, 0xA2, 0);
        down(&mut s, 0xA4, 0);
        assert_ne!(s.modifier_mask(), 0);
        s.reset();
        assert_eq!(s.modifier_mask(), 0);
    }
}
