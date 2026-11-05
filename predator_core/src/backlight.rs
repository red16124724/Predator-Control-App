//! Backlight state machine engine ported from .

use std::time::{SystemTime, UNIX_EPOCH};

/// System AC power line status matching Windows .
#[repr(i32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PowerLineStatus {
    Offline = 0,
    Online = 1,
    Unknown = 255,
}

impl From<i32> for PowerLineStatus {
    fn from(val: i32) -> Self {
        match val {
            0 => PowerLineStatus::Offline,
            1 => PowerLineStatus::Online,
            _ => PowerLineStatus::Unknown,
        }
    }
}

/// Windows battery charge status flags.
pub const BATTERY_STATUS_HIGH: i32 = 1;
pub const BATTERY_STATUS_LOW: i32 = 2;
pub const BATTERY_STATUS_CRITICAL: i32 = 4;
pub const BATTERY_STATUS_CHARGING: i32 = 8;
pub const BATTERY_STATUS_NO_BATTERY: i32 = 128;
pub const BATTERY_STATUS_UNKNOWN: i32 = 255;

/// Helper to get current UTC timestamp in seconds.
pub fn current_utc_sec() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// Backlight state machine tracking AC/battery transitions, lid events, suspend/resume, and idle dimming.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BacklightStateManager {
    pub is_lid_closed: bool,
    pub is_sleeping: bool,
    pub is_plugged_in: Option<bool>,
    pub saved_ac_brightness: i32,
    pub manual_backlight_on_battery: bool,
    pub manual_battery_brightness: i32,
    pub idle_timeout_seconds: i32,
    pub dimmed_brightness_percent: i32,
    pub is_dimmed: bool,
    pub last_activity_utc_sec: i64,
}

/// Compatibility alias.
pub type BacklightState = BacklightStateManager;

impl Default for BacklightStateManager {
    fn default() -> Self {
        Self::new(100)
    }
}

impl BacklightStateManager {
    /// Creates a new state manager with the specified initial AC brightness.
    pub fn new(initial_ac_brightness: i32) -> Self {
        Self {
            is_lid_closed: false,
            is_sleeping: false,
            is_plugged_in: None,
            saved_ac_brightness: initial_ac_brightness.clamp(0, 100),
            manual_backlight_on_battery: false,
            manual_battery_brightness: 0,
            idle_timeout_seconds: 0,
            dimmed_brightness_percent: 30,
            is_dimmed: false,
            last_activity_utc_sec: current_utc_sec(),
        }
    }

    // --- Getters and Setters ---

    pub fn is_lid_closed(&self) -> bool {
        self.is_lid_closed
    }

    pub fn set_is_lid_closed(&mut self, val: bool) {
        self.is_lid_closed = val;
    }

    pub fn is_sleeping(&self) -> bool {
        self.is_sleeping
    }

    pub fn set_is_sleeping(&mut self, val: bool) {
        self.is_sleeping = val;
    }

    pub fn is_plugged_in(&self) -> Option<bool> {
        self.is_plugged_in
    }

    pub fn set_is_plugged_in(&mut self, val: Option<bool>) {
        self.is_plugged_in = val;
    }

    pub fn set_plugged_in_state(&mut self, val: Option<bool>) {
        self.is_plugged_in = val;
    }

    pub fn saved_ac_brightness(&self) -> i32 {
        self.saved_ac_brightness
    }

    pub fn set_saved_ac_brightness(&mut self, val: i32) {
        self.saved_ac_brightness = val.clamp(0, 100);
    }

    pub fn manual_backlight_on_battery(&self) -> bool {
        self.manual_backlight_on_battery
    }

    pub fn set_manual_backlight_on_battery(&mut self, val: bool) {
        self.manual_backlight_on_battery = val;
    }

    pub fn manual_battery_brightness(&self) -> i32 {
        self.manual_battery_brightness
    }

    pub fn set_manual_battery_brightness(&mut self, val: i32) {
        self.manual_battery_brightness = val.clamp(0, 100);
    }

    pub fn idle_timeout_seconds(&self) -> i32 {
        self.idle_timeout_seconds
    }

    pub fn set_idle_timeout_seconds(&mut self, val: i32) {
        self.idle_timeout_seconds = val.max(0);
    }

    pub fn dimmed_brightness_percent(&self) -> i32 {
        self.dimmed_brightness_percent
    }

    pub fn set_dimmed_brightness_percent(&mut self, val: i32) {
        self.dimmed_brightness_percent = val.clamp(0, 100);
    }

    pub fn is_dimmed(&self) -> bool {
        self.is_dimmed
    }

    pub fn set_is_dimmed(&mut self, val: bool) {
        self.is_dimmed = val;
    }

    pub fn last_activity_utc_sec(&self) -> i64 {
        self.last_activity_utc_sec
    }

    pub fn set_last_activity_utc_sec(&mut self, val: i64) {
        self.last_activity_utc_sec = val;
    }

    // --- State Machine Logic ---

    /// Evaluates if system is currently running on battery.
    pub fn is_on_battery(&self, line_status: PowerLineStatus, charge_status: i32) -> bool {
        if (charge_status & BATTERY_STATUS_CHARGING) != 0
            || (charge_status & BATTERY_STATUS_NO_BATTERY) != 0
        {
            return false;
        }
        if line_status == PowerLineStatus::Online {
            return false;
        }
        if line_status == PowerLineStatus::Offline {
            return true;
        }
        if self.is_plugged_in == Some(true) {
            return false;
        }
        if self.is_plugged_in == Some(false) {
            return true;
        }

        // PowerLineStatus.Unknown: during sleep/wake transition, default to battery unless confirmed plugged in
        self.is_plugged_in != Some(true)
    }

    /// Records user activity at current system time.
    pub fn record_activity(&mut self) {
        self.last_activity_utc_sec = current_utc_sec();
    }

    /// Records user activity at a specific UTC second.
    pub fn record_activity_at(&mut self, now_sec: i64) {
        self.last_activity_utc_sec = now_sec;
    }

    /// Evaluates whether backlight should dim due to inactivity.
    /// Returns .
    pub fn evaluate_idle(&mut self, now_sec: i64) -> (bool, bool, i32) {
        if self.is_lid_closed || self.is_sleeping || self.idle_timeout_seconds <= 0 {
            return (false, false, 0);
        }

        let idle_sec = now_sec - self.last_activity_utc_sec;
        if idle_sec >= self.idle_timeout_seconds as i64 && !self.is_dimmed {
            self.is_dimmed = true;
            return (true, true, self.dimmed_brightness_percent.clamp(0, 100));
        }
        (false, false, 0)
    }

    /// Restores brightness on user activity if currently dimmed.
    /// Returns .
    pub fn on_activity(&mut self) -> (bool, bool, i32) {
        self.on_activity_at(current_utc_sec())
    }

    /// Restores brightness on user activity at the specified timestamp.
    /// Returns .
    pub fn on_activity_at(&mut self, now_sec: i64) -> (bool, bool, i32) {
        self.last_activity_utc_sec = now_sec;
        if self.is_dimmed {
            self.is_dimmed = false;
            let target = if self.is_plugged_in == Some(true) {
                self.saved_ac_brightness.clamp(0, 100)
            } else if self.manual_backlight_on_battery {
                self.manual_battery_brightness.clamp(0, 100)
            } else {
                0
            };
            return (true, true, target);
        }
        (false, false, 0)
    }

    /// Handles lid open/closed events.
    /// Returns .
    pub fn on_lid_changed(
        &mut self,
        is_open: bool,
        line_status: PowerLineStatus,
        charge_status: i32,
    ) -> (bool, i32) {
        self.on_lid_changed_at(is_open, line_status, charge_status, current_utc_sec())
    }

    /// Handles lid open/closed events at specified timestamp.
    /// Returns .
    pub fn on_lid_changed_at(
        &mut self,
        is_open: bool,
        line_status: PowerLineStatus,
        charge_status: i32,
        now_sec: i64,
    ) -> (bool, i32) {
        self.is_lid_closed = !is_open;
        self.is_dimmed = false;
        self.last_activity_utc_sec = now_sec;

        let is_charging = (charge_status & BATTERY_STATUS_CHARGING) != 0;
        if line_status == PowerLineStatus::Online || is_charging {
            self.is_plugged_in = Some(true);
        } else if line_status == PowerLineStatus::Offline {
            self.is_plugged_in = Some(false);
        }

        if !is_open || self.is_sleeping {
            self.manual_backlight_on_battery = false;
            self.manual_battery_brightness = 0;
            (true, 0)
        } else {
            let on_batt = self.is_on_battery(line_status, charge_status);
            if on_batt {
                self.manual_backlight_on_battery = false;
                self.manual_battery_brightness = 0;
                (true, 0)
            } else {
                let target = self.saved_ac_brightness.clamp(0, 100);
                (target <= 0, target)
            }
        }
    }

    /// Handles system suspend/sleep event.
    /// Returns .
    pub fn on_suspend(&mut self) -> (bool, i32) {
        self.is_sleeping = true;
        self.is_dimmed = false;
        self.manual_backlight_on_battery = false;
        self.manual_battery_brightness = 0;
        (true, 0)
    }

    /// Handles system resume from sleep event.
    /// Returns .
    pub fn on_resume(&mut self, line_status: PowerLineStatus, charge_status: i32) -> (bool, i32) {
        self.on_resume_at(line_status, charge_status, current_utc_sec())
    }

    /// Handles system resume at specified timestamp.
    /// Returns .
    pub fn on_resume_at(
        &mut self,
        line_status: PowerLineStatus,
        charge_status: i32,
        now_sec: i64,
    ) -> (bool, i32) {
        self.is_sleeping = false;
        self.is_dimmed = false;
        self.last_activity_utc_sec = now_sec;

        let is_charging = (charge_status & BATTERY_STATUS_CHARGING) != 0;
        if line_status == PowerLineStatus::Online || is_charging {
            self.is_plugged_in = Some(true);
        } else if line_status == PowerLineStatus::Offline {
            self.is_plugged_in = Some(false);
        }

        if self.is_lid_closed {
            self.manual_backlight_on_battery = false;
            self.manual_battery_brightness = 0;
            return (true, 0);
        }

        let on_batt = self.is_on_battery(line_status, charge_status);
        if on_batt {
            self.manual_backlight_on_battery = false;
            self.manual_battery_brightness = 0;
            (true, 0)
        } else {
            let target = self.saved_ac_brightness.clamp(0, 100);
            (target <= 0, target)
        }
    }

    /// Handles AC plug/unplug events.
    /// Returns .
    pub fn on_power_source_changed(&mut self, plugged_in: bool) -> (bool, i32) {
        self.on_power_source_changed_at(plugged_in, current_utc_sec())
    }

    /// Handles AC plug/unplug events at specified timestamp.
    /// Returns .
    pub fn on_power_source_changed_at(&mut self, plugged_in: bool, now_sec: i64) -> (bool, i32) {
        self.is_plugged_in = Some(plugged_in);
        self.is_dimmed = false;
        self.last_activity_utc_sec = now_sec;

        if self.is_lid_closed || self.is_sleeping {
            return (true, 0);
        }

        if plugged_in {
            self.manual_backlight_on_battery = false;
            let target = self.saved_ac_brightness.clamp(0, 100);
            (target <= 0, target)
        } else if !self.manual_backlight_on_battery {
            (true, 0)
        } else {
            let target = self.manual_battery_brightness.clamp(0, 100);
            (target <= 0, target)
        }
    }

    /// Handles user manual brightness adjustment.
    pub fn on_user_adjusted_brightness(&mut self, brightness: i32, on_battery: bool) {
        self.on_user_adjusted_brightness_at(brightness, on_battery, current_utc_sec())
    }

    /// Handles user manual brightness adjustment at specified timestamp.
    pub fn on_user_adjusted_brightness_at(
        &mut self,
        brightness: i32,
        on_battery: bool,
        now_sec: i64,
    ) {
        self.is_dimmed = false;
        self.last_activity_utc_sec = now_sec;

        if on_battery {
            self.manual_backlight_on_battery = brightness > 0;
            self.manual_battery_brightness = brightness.clamp(0, 100);
        } else {
            self.saved_ac_brightness = brightness.clamp(0, 100);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_backlight_initial_state_and_clamping() {
        let mgr = BacklightStateManager::new(120);
        assert_eq!(mgr.saved_ac_brightness(), 100);
        assert!(!mgr.is_lid_closed());
        assert!(!mgr.is_sleeping());
        assert_eq!(mgr.is_plugged_in(), None);

        let mgr2 = BacklightStateManager::new(-50);
        assert_eq!(mgr2.saved_ac_brightness(), 0);
    }

    #[test]
    fn test_is_on_battery_logic() {
        let mut mgr = BacklightStateManager::new(100);

        // Charging or no battery -> not on battery
        assert!(!mgr.is_on_battery(PowerLineStatus::Offline, BATTERY_STATUS_CHARGING));
        assert!(!mgr.is_on_battery(PowerLineStatus::Offline, BATTERY_STATUS_NO_BATTERY));

        // Online -> not on battery
        assert!(!mgr.is_on_battery(PowerLineStatus::Online, 0));

        // Offline -> on battery
        assert!(mgr.is_on_battery(PowerLineStatus::Offline, 0));

        // Unknown line status falls back to is_plugged_in
        mgr.set_is_plugged_in(Some(true));
        assert!(!mgr.is_on_battery(PowerLineStatus::Unknown, 0));

        mgr.set_is_plugged_in(Some(false));
        assert!(mgr.is_on_battery(PowerLineStatus::Unknown, 0));

        // Unknown line status with is_plugged_in == None defaults to on battery
        mgr.set_is_plugged_in(None);
        assert!(mgr.is_on_battery(PowerLineStatus::Unknown, 0));
    }

    #[test]
    fn test_lid_transitions() {
        let mut mgr = BacklightStateManager::new(80);

        // Lid closed shuts off
        let (off, target) = mgr.on_lid_changed_at(false, PowerLineStatus::Online, 0, 1000);
        assert!(off);
        assert_eq!(target, 0);
        assert!(mgr.is_lid_closed());
        assert_eq!(mgr.is_plugged_in(), Some(true));

        // Lid opened on AC restores brightness
        let (off, target) = mgr.on_lid_changed_at(true, PowerLineStatus::Online, 0, 1010);
        assert!(!off);
        assert_eq!(target, 80);
        assert!(!mgr.is_lid_closed());

        // Lid opened on Battery forces off by default
        let (off, target) = mgr.on_lid_changed_at(true, PowerLineStatus::Offline, 0, 1020);
        assert!(off);
        assert_eq!(target, 0);
        assert_eq!(mgr.is_plugged_in(), Some(false));
        assert!(!mgr.manual_backlight_on_battery());

        // Lid opened while sleeping remains off
        mgr.set_is_sleeping(true);
        let (off, target) = mgr.on_lid_changed_at(true, PowerLineStatus::Online, 0, 1030);
        assert!(off);
        assert_eq!(target, 0);
    }

    #[test]
    fn test_suspend_and_resume() {
        let mut mgr = BacklightStateManager::new(75);

        // User sets battery override
        mgr.on_user_adjusted_brightness_at(50, true, 500);
        assert!(mgr.manual_backlight_on_battery());
        assert_eq!(mgr.manual_battery_brightness(), 50);

        // Suspend resets battery override and turns off
        let (off, target) = mgr.on_suspend();
        assert!(off);
        assert_eq!(target, 0);
        assert!(mgr.is_sleeping());
        assert!(!mgr.manual_backlight_on_battery());
        assert_eq!(mgr.manual_battery_brightness(), 0);

        // Resume on AC restores saved brightness
        let (off, target) = mgr.on_resume_at(PowerLineStatus::Online, 0, 1000);
        assert!(!off);
        assert_eq!(target, 75);
        assert!(!mgr.is_sleeping());
        assert_eq!(mgr.is_plugged_in(), Some(true));

        // Suspend and resume on battery
        mgr.on_suspend();
        let (off, target) = mgr.on_resume_at(PowerLineStatus::Offline, 0, 1100);
        assert!(off);
        assert_eq!(target, 0);
        assert_eq!(mgr.is_plugged_in(), Some(false));

        // Resume with lid closed remains off
        mgr.set_is_lid_closed(true);
        let (off, target) = mgr.on_resume_at(PowerLineStatus::Online, 0, 1200);
        assert!(off);
        assert_eq!(target, 0);
    }

    #[test]
    fn test_power_source_changed() {
        let mut mgr = BacklightStateManager::new(90);

        // Plugged in
        let (off, target) = mgr.on_power_source_changed_at(true, 100);
        assert!(!off);
        assert_eq!(target, 90);
        assert_eq!(mgr.is_plugged_in(), Some(true));

        // Unplugged without manual override turns off
        let (off, target) = mgr.on_power_source_changed_at(false, 110);
        assert!(off);
        assert_eq!(target, 0);
        assert_eq!(mgr.is_plugged_in(), Some(false));

        // User adjusts on battery
        mgr.on_user_adjusted_brightness_at(40, true, 120);
        assert!(mgr.manual_backlight_on_battery());

        // Plugged in restores AC brightness and clears battery override
        let (off, target) = mgr.on_power_source_changed_at(true, 130);
        assert!(!off);
        assert_eq!(target, 90);
        assert!(!mgr.manual_backlight_on_battery());

        // Set manual override on battery again, unplug should honor it
        mgr.set_manual_backlight_on_battery(true);
        mgr.set_manual_battery_brightness(60);
        let (off, target) = mgr.on_power_source_changed_at(false, 140);
        assert!(!off);
        assert_eq!(target, 60);

        // Power changed while lid closed stays off
        mgr.set_is_lid_closed(true);
        let (off, target) = mgr.on_power_source_changed_at(true, 150);
        assert!(off);
        assert_eq!(target, 0);
    }

    #[test]
    fn test_idle_evaluation_and_activity_restore() {
        let mut mgr = BacklightStateManager::new(100);
        mgr.set_idle_timeout_seconds(30);
        mgr.set_dimmed_brightness_percent(20);
        mgr.set_is_plugged_in(Some(true));
        mgr.record_activity_at(1000);

        // Before timeout -> no change
        let (changed, dim, target) = mgr.evaluate_idle(1020);
        assert!(!changed);
        assert!(!dim);
        assert_eq!(target, 0);

        // Exactly at timeout -> dims to 20%
        let (changed, dim, target) = mgr.evaluate_idle(1030);
        assert!(changed);
        assert!(dim);
        assert_eq!(target, 20);
        assert!(mgr.is_dimmed());

        // Second evaluation while already dimmed -> no further change
        let (changed, dim, _) = mgr.evaluate_idle(1040);
        assert!(!changed);
        assert!(!dim);

        // Activity restores AC brightness
        let (changed, restore, target) = mgr.on_activity_at(1050);
        assert!(changed);
        assert!(restore);
        assert_eq!(target, 100);
        assert!(!mgr.is_dimmed());

        // Dim again on battery with manual brightness
        mgr.set_is_plugged_in(Some(false));
        mgr.set_manual_backlight_on_battery(true);
        mgr.set_manual_battery_brightness(50);
        mgr.evaluate_idle(1090);
        assert!(mgr.is_dimmed());

        let (changed, restore, target) = mgr.on_activity_at(1100);
        assert!(changed);
        assert!(restore);
        assert_eq!(target, 50);

        // Dim again on battery without manual override -> restores to 0
        mgr.set_manual_backlight_on_battery(false);
        mgr.evaluate_idle(1140);
        assert!(mgr.is_dimmed());

        let (changed, restore, target) = mgr.on_activity_at(1150);
        assert!(changed);
        assert!(restore);
        assert_eq!(target, 0);
    }
}
