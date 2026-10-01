using System;
using System.Windows.Forms;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class BacklightStateManagerTests
    {
        [Fact]
        public void DefaultState_BacklightIsOffOnBatteryByDefault()
        {
            var mgr = new BacklightStateManager();
            bool onBattery = mgr.IsOnBattery(PowerLineStatus.Offline);
            Assert.True(onBattery);
            Assert.False(mgr.ManualBacklightOnBattery);
            Assert.Equal(0, mgr.ManualBatteryBrightness);
        }

        [Fact]
        public void UnknownPowerStatus_DefaultsToBatteryWhenNotConfirmedOnline()
        {
            var mgr = new BacklightStateManager();
            // Fresh start with unknown line status: must default to battery
            Assert.True(mgr.IsOnBattery(PowerLineStatus.Unknown));

            // Set to online
            mgr.OnPowerSourceChanged(true, out bool shouldTurnOff, out int targetBright);
            Assert.False(shouldTurnOff);
            Assert.Equal(100, targetBright);

            // Now if status becomes Unknown momentarily on wake, but IsPluggedIn was confirmed true
            Assert.False(mgr.IsOnBattery(PowerLineStatus.Unknown));

            // Once unplugged to battery
            mgr.OnPowerSourceChanged(false, out shouldTurnOff, out targetBright);
            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);
            // Now unknown status stays battery
            Assert.True(mgr.IsOnBattery(PowerLineStatus.Unknown));
        }

        [Fact]
        public void LidClosed_TurnsOffBacklight_AndClearsManualBatteryOverride()
        {
            var mgr = new BacklightStateManager();
            // User manually turns on backlight on battery
            mgr.OnUserAdjustedBrightness(80, onBattery: true);
            Assert.True(mgr.ManualBacklightOnBattery);
            Assert.Equal(80, mgr.ManualBatteryBrightness);

            // Close the lid
            mgr.OnLidChanged(isOpen: false, PowerLineStatus.Offline, out bool shouldTurnOff, out int targetBright);
            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);
            Assert.True(mgr.IsLidClosed);
            Assert.False(mgr.ManualBacklightOnBattery);
            Assert.Equal(0, mgr.ManualBatteryBrightness);
        }

        [Fact]
        public void LidOpenedOnBattery_KeepsBacklightOffByDefault()
        {
            var mgr = new BacklightStateManager();
            // Close lid first
            mgr.OnLidChanged(isOpen: false, PowerLineStatus.Offline, out _, out _);

            // Now open lid on battery
            mgr.OnLidChanged(isOpen: true, PowerLineStatus.Offline, out bool shouldTurnOff, out int targetBright);
            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);
            Assert.False(mgr.IsLidClosed);
            Assert.False(mgr.ManualBacklightOnBattery);
        }

        [Fact]
        public void LidOpenedOnAC_RestoresSavedAcBrightness()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 75;

            mgr.OnLidChanged(isOpen: false, PowerLineStatus.Online, out _, out _);
            mgr.OnLidChanged(isOpen: true, PowerLineStatus.Online, out bool shouldTurnOff, out int targetBright);

            Assert.False(shouldTurnOff);
            Assert.Equal(75, targetBright);
        }

        [Fact]
        public void Suspend_TurnsOffBacklight_AndClearsManualOverride()
        {
            var mgr = new BacklightStateManager();
            mgr.OnUserAdjustedBrightness(60, onBattery: true);
            Assert.True(mgr.ManualBacklightOnBattery);

            // System suspends / goes to sleep
            mgr.OnSuspend(out bool shouldTurnOff, out int targetBright);
            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);
            Assert.True(mgr.IsSleeping);
            Assert.False(mgr.ManualBacklightOnBattery);
            Assert.Equal(0, mgr.ManualBatteryBrightness);
        }

        [Fact]
        public void ResumeOnBattery_KeepsBacklightOffByDefault()
        {
            var mgr = new BacklightStateManager();
            mgr.OnSuspend(out _, out _);

            // System wakes on battery
            mgr.OnResume(PowerLineStatus.Offline, out bool shouldTurnOff, out int targetBright);
            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);
            Assert.False(mgr.IsSleeping);
            Assert.False(mgr.ManualBacklightOnBattery);
        }

        [Fact]
        public void ResumeOnAC_RestoresAcBrightness()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 90;
            mgr.OnSuspend(out _, out _);

            mgr.OnResume(PowerLineStatus.Online, out bool shouldTurnOff, out int targetBright);
            Assert.False(shouldTurnOff);
            Assert.Equal(90, targetBright);
            Assert.False(mgr.IsSleeping);
        }

        [Fact]
        public void ResumeWithUnknownPowerStatus_WhenConfirmedPluggedIn_PreservesACStateAndRestoresBrightness()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 85;
            mgr.OnPowerSourceChanged(true, out _, out _);

            mgr.OnSuspend(out _, out _);

            // Resuming with Unknown status must NOT erase confirmed plugged in state
            mgr.OnResume(PowerLineStatus.Unknown, out bool shouldTurnOff, out int targetBright);
            Assert.False(shouldTurnOff);
            Assert.Equal(85, targetBright);
            Assert.False(mgr.IsOnBattery(PowerLineStatus.Unknown));
        }

        [Fact]
        public void LidOpenedWithUnknownPowerStatus_WhenConfirmedPluggedIn_PreservesACStateAndRestoresBrightness()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 95;
            mgr.OnPowerSourceChanged(true, out _, out _);

            mgr.OnLidChanged(false, PowerLineStatus.Online, out _, out _);

            // Lid opened with momentary Unknown line status
            mgr.OnLidChanged(true, PowerLineStatus.Unknown, out bool shouldTurnOff, out int targetBright);
            Assert.False(shouldTurnOff);
            Assert.Equal(95, targetBright);
            Assert.False(mgr.IsOnBattery(PowerLineStatus.Unknown));
        }

        [Fact]
        public void PowerSourceSwitch_ACtoBattery_TurnsOffBacklightByDefault()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 100;
            mgr.OnPowerSourceChanged(true, out _, out _);

            // Unplug charger
            mgr.OnPowerSourceChanged(false, out bool shouldTurnOff, out int targetBright);
            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);
            Assert.False(mgr.ManualBacklightOnBattery);
        }

        [Fact]
        public void PowerSourceSwitch_BatteryToAC_RestoresSavedAcBrightness()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 85;
            mgr.OnPowerSourceChanged(false, out _, out _);

            // Plug in charger
            mgr.OnPowerSourceChanged(true, out bool shouldTurnOff, out int targetBright);
            Assert.False(shouldTurnOff);
            Assert.Equal(85, targetBright);
        }

        [Fact]
        public void PowerSourceSwitch_PluggingInAc_ClearsManualBatteryOverride()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 90;
            // On battery, user sets brightness to 60
            mgr.OnUserAdjustedBrightness(60, onBattery: true);
            Assert.True(mgr.ManualBacklightOnBattery);
            Assert.Equal(60, mgr.ManualBatteryBrightness);

            // Plug into AC: manual override must be cleared
            mgr.OnPowerSourceChanged(true, out bool shouldTurnOff, out int targetBright);
            Assert.False(shouldTurnOff);
            Assert.Equal(90, targetBright);
            Assert.False(mgr.ManualBacklightOnBattery);

            // Unplug back to battery: should default to off
            mgr.OnPowerSourceChanged(false, out shouldTurnOff, out targetBright);
            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);
        }

        [Fact]
        public void LidClosedOnAC_TurnsOffBacklight_WhenSavedBrightnessPositive()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 80;
            mgr.OnPowerSourceChanged(true, out _, out _);

            mgr.OnLidChanged(isOpen: false, PowerLineStatus.Online, out bool shouldTurnOff, out int targetBright);
            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);
        }

        [Fact]
        public void LidOpenedOnAC_AfterBeingClosedOnAC_RestoresSavedAcBrightness()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 80;
            mgr.OnPowerSourceChanged(true, out _, out _);

            mgr.OnLidChanged(isOpen: false, PowerLineStatus.Online, out _, out _);
            mgr.OnLidChanged(isOpen: true, PowerLineStatus.Online, out bool shouldTurnOff, out int targetBright);
            Assert.False(shouldTurnOff);
            Assert.Equal(80, targetBright);
        }

        [Fact]
        public void SuspendOnAC_TurnsOffBacklight_WhenSavedBrightnessPositive()
        {
            var mgr = new BacklightStateManager();
            mgr.SavedAcBrightness = 70;
            mgr.OnPowerSourceChanged(true, out _, out _);

            mgr.OnSuspend(out bool shouldTurnOff, out int targetBright);
            Assert.True(shouldTurnOff);
            Assert.Equal(0, targetBright);
        }

        [Fact]
        public void ConcurrencyStressTest_RapidStateTransitions_NoExceptions()
        {
            var mgr = new BacklightStateManager();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<System.Exception>();

            System.Threading.Tasks.Parallel.For(0, 500, i =>
            {
                try
                {
                    switch (i % 6)
                    {
                        case 0:
                            mgr.OnLidChanged(i % 2 == 0, (PowerLineStatus)(i % 3), out _, out _);
                            break;
                        case 1:
                            mgr.OnPowerSourceChanged(i % 2 == 1, out _, out _);
                            break;
                        case 2:
                            mgr.OnSuspend(out _, out _);
                            break;
                        case 3:
                            mgr.OnResume((PowerLineStatus)(i % 3), out _, out _);
                            break;
                        case 4:
                            mgr.OnUserAdjustedBrightness(i % 101, onBattery: (i % 2 == 0));
                            break;
                        case 5:
                            _ = mgr.IsOnBattery((PowerLineStatus)(i % 3));
                            break;
                    }
                }
                catch (System.Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
        }
    }
}
