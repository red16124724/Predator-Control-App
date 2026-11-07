# Predator Control App v1.5.8 Release Notes & Audit Summary

The sidebar version label displays â€œMade by RED4724â€. This local build is unsigned; trusted signing requires a suitable code-signing certificate and signing tool.

Startup-check update verification: the Release publish succeeded and 13 focused .NET tests passed, including one-shot scheduling and cancellation. A live GitHub check, hidden-startup UI, and hardware behavior were not exercised in this verification.

Current local 1.5.8 improvements: one update check on startup, including hidden launches, and manual checks without periodic polling; mandatory asset-specific SHA-256, upstream replacement warning and previous-executable backup; verified embedded native library; on-demand native initialization; and a compatibility report. The executable remains unsigned; physical hardware and live update installation were not tested.

## Executive Overview
Version 1.5.8 brings UI typography and scaling fixes, complete removal of BIOS modification controls and safety gates (while preserving hardware MUX switch controls), live read-only hardware architecture diagnostics, and comprehensive codebase stabilization across the C# .NET 10 WinForms UI, native Rust Core (predator_core.dll), Ring-0 PawnIO hardware drivers, and the complete test suite.


## Predator Control App v1.5.8 Highlights (2026-11-06)

- **UI Typography & Scaling Fix**:
  - Removed duplicate font scaling and excessive bold text across all cards, restoring clean, sharp 9pt regular fonts and eliminating text overlapping.
  - Removed manual font point size DPI multiplication from `UpdateDpiFonts()`; GDI per-monitor DPI handles font point-to-pixel scaling automatically under PerMonitorV2, preventing double-scaling.
  - Completely eliminated `RescaleControlFonts()` recursive loops across all forms (`Form1`, `DisplaySettingsForm`, `BiosSettingsForm`, `FanCurveForm`, `GameSyncForm`, `HardwareCapabilitiesForm`) so font sizes never compound on DPI transitions or form activation.
  - Re-balanced typography hierarchy: crisp 8.5pt Bold card headers, legible 9pt Regular body text for descriptions and labels, with 9pt Bold strictly reserved for real-time telemetry numbers and active statuses.
- **BIOS Setting Removal (100%)**:
  - Removed all BIOS modifying controls and safety gates 100%.
  - Completely removed the "BIOS Settings" sidebar tab, navigation button (`_btnNavBios`), and panel (`_pnlBios`).
  - Completely excised all BIOS modifying controls and methods: `BuildBiosPage()`, `PopulateEmbeddedBiosRows()`, `SwitchBiosCategory()`, `RestoreBiosDefaults()`, `_chkEnableBiosModsSys`, `_chkAllowDangerousBiosSys`, and `trayBiosSettings`.
  - Streamlined sidebar navigation to 7 clean tabs: Dashboard, Fans & Cooling, RGB Lighting, Display Settings, Game Sync, System & Power, Settings & Info.
- **MUX Switch Preservation**:
  - Fully retained hardware MUX switch controls in Display Settings (`_pnlDisplay` and `DisplaySettingsForm`) with BIOS/ACPI/WMI backend querying and safe reboot prompt.
  - Direct hardware MUX modes supported: Automatic (Optimus MUX), Integrated Only (iGPU MUX), and Discrete Only (dGPU MUX).
  - Mandatory system reboot prompt and confirmation (`shutdown.exe /r /t 5`) preserved for ACPI/WMI hardware display switching.
- **Live Read-Only BIOS Architecture Badge**:
  - Placed in Settings & Info diagnostic card (`_pnlSettings`) without modification risks.
  - Live hardware architecture identity: BIOS version, model name, SMBIOS version, and architecture badge (`HardwareIdentity.GetBiosHardwareBadge()`) dynamically queried without hardcoded values.

### Native Rust Expansion & Audit Fixes (Update 2026-11-07)

- **Three new native modules** (`wmi_codec.rs`, `capability.rs`, `gameprobe.rs`; 646 lines, 17 new C-ABI exports) move the pure decision logic into `predator_core`.
- **Six further areas migrated** (`power_limits.rs`, `service_proto.rs`, `settings_policy.rs`, `theme_policy.rs`, `wmi_payload.rs`, `catalog_policy.rs`): power-limit clamp/estimate/default policy, MMIO decode and deviation, Acer service packet framing and effect canonicalisation, settings clamps and allowed-limit sanitisation, the power-mode palette, WMI fan payload layouts and the model-catalog heuristic chain. The crate now exports **218 entry points across 37 modules**.
- **WMI codec**: tachometer/fan decoding, mode LED palette, non-destructive mode payload, extended system-fan payload, fan-speed payload encoding and the confirmed-mode anti-blinking short-circuit.
- **Capability policy**: factory EC fan-table detection, fan-table clamping/decoding, tri-state capability-override merging and mode-default finalisation.
- **Game probe**: RawInput vendor-packet classification, key debounce, media-poll caching and process/profile name matching.
- Every call site keeps its original managed implementation as a fallback when the native core is missing, so behaviour degrades gracefully.

### Game Sync Profile Table Cleanup (Update 2026-11-07)

- **Invented profiles removed**: the Configured Game Profiles table was listing 45 placeholder entries (`Game_0.exe`…`Game_9.exe`, `FakeGame`, `CorruptGame`, `Updated 50`, `DoomEternal.exe`, Russian and CJK sample names) that no user had added. They came from stress tests that constructed `GameSyncController` with the default save path and therefore wrote their fixtures into the real `game_sync.json` and the `HKCU\SOFTWARE\PredatorControl\Profiles` registry hive.
- **Store cleaned**: `game_sync.json`, its `.bak` companion and all 45 registry profile sub-keys now hold zero profiles (Game Sync stays disabled until the user enables it). A copy of the polluted JSON is kept in the workspace as evidence.
- **Load-time validation**: `GameSyncController.Load()` validates every stored profile and drops case-insensitive duplicates, so corrupt or repeated records never reach the UI.
- **Test isolation**: the five remaining tests that used the production path now receive a unique temporary JSON file, so future runs cannot repopulate the user's store.
- **UI**: the profiles card shows an explicit empty state when nothing is configured and lists only the user's own profiles; the same controller backs the Advanced Editor list.

### Single-File Distribution Guarantee (Update 2026-11-07)

- **One executable, everything inside**: `PredatorControlApp.exe` is a self-contained .NET 10 single-file build that carries the .NET runtime, all managed dependencies, the native `predator_core.dll` (embedded resource, extracted to a hash-specific cache after hash verification) and the PawnIO payload images.
- **No loose content either**: `IncludeAllContentForSelfExtract` is now enabled together with `IncludeNativeLibrariesForSelfExtract`, so content and native files are bundled rather than copied beside the executable.
- **Verified layout**: publishing into an empty folder yields exactly one file (`PredatorControlApp.exe`); no `.dll`, `.deps.json`, `.runtimeconfig.json`, icon or `.bin` sidecars.
- **Stale symbol file removed**: the root `PredatorControlApp.pdb` belonged to the previous build and did not match the shipped executable.
- **Recorded as a standing rule**: packaging requirements and the verification command are documented in `AGENTS.md` and the README distribution section.

### Full Stress Sweep & Codebase Scan (Update 2026-11-07)

- **Managed suite**: 2,217 passed, 0 failed, 0 skipped in 6 m 57 s — the complete stress, chaos, concurrency, fuzz and UI-lifecycle coverage across dashboard, fans, RGB, display/MUX, Game Sync, thermal safety, IPC, updater and power limits.
- **Release build**: 0 errors; the 30 remaining warnings are `CS0618` notices for the deliberately retired BIOS-modification members.
- **Whole-source scan**: no inverted `Math.Clamp` bounds (0 of 453 call sites), no unguarded numeric parsing (all 8 `Convert`/`Parse` sites are inside `try` or use `TryParse`), the single `First()` after `Skip(1)` is guarded by a `Count >= 3` check, all WinForms timers are disposed, `Process.GetProcesses()` handles are released in `finally`, there is no `Application.DoEvents` reentrancy, the single `async void` handler is fully guarded, and blocking async waits are bounded with timeouts and fallbacks.
- **Outcome**: no reproducible defect found, so no behavioural change was made; the executable was republished from the audited source as a single file with all binaries embedded.
- **Not verified on hardware**: EC/WMI/PawnIO writes, MUX switching, real brightness changes, live update installation and interactive UI rendering were not exercised physically; UI evidence comes from the STA lifecycle/disposal stress tests, not a visual inspection.
- **Fixed**: capability-override tri-state misalignment (a `ModeKey` override was applied to the `HasEcHid` slot and ignored); `NearestFanSpeed` had lost Acer protocol-encoder precedence; the native BIOS fallback invented "Enabled (10%)" for any non-zero SMBIOS gaming record `0x15`; `PowerModeSwitchGuard` now cancels its pending delays on disposal.
- **Verification**: 2,217 managed tests passing (0 failed, 0 skipped) and 342 native Rust tests passing (2 hardware-only tests ignored). The executable was republished as a self-contained single file with the native core embedded; no `predator_core.dll` sidecar is shipped.

### Power-Mode PL1/PL2 Enforcement & Transport Retry Fixes (Update 2026-11-06)

- **Custom CPU power limits now survive every mode transition.** `CpuPowerLimitController.ApplyForModeSwitchAsync` writes the new mode's saved PL1/PL2 at 0, 250, 600 and 1200 ms, re-reads the per-mode settings on each pass, and invalidates the applied-wattage cache first so even a same-wattage mode change rewrites MSR `0x610` and MMIO `0xFED159A0`/`0xFED159A4`.
- **Watchdog verifies both registers.** The periodic verifier reads back MSR `0x610` and the MMIO mirror (lock-bit aware) and re-applies via the PL2-before-PL1 directional path when either deviates by ≥ 1 W.
- **Every switch path is wired:** power-mode buttons, tray menu, physical Mode/Turbo key, GameSync profile launch/exit, custom keyboard shortcuts, the switch guard's `onApplied` callback, and AC/DC transitions that keep the current mode (the EC still reloads its thermal table).
- **No more false success caching.** `WmiController` keeps a separate confirmed-mode cache for the anti-blinking redundant-write short-circuit, so a failed EC/WMI mode write is retried instead of being reported as applied; failed `SetGamingFanSpeed` writes invalidate their cache so the same fan percentage retries.
- **Verification:** 0 build errors; 2,211 managed tests passing, 0 failed, 0 skipped (9 new regression tests). Actual EC hardware behaviour was not exercised.

### Comprehensive Stress Testing, Chaos Fuzzing & Bug Remediation (Update 2026-11-06)
- **App-Wide Chaos, Fuzzing & Concurrency Stress Verification**:
  - Implemented FullAppExhaustiveChaosAndStressTests.cs (13 tests, all passed in 22s) covering AppSettings numerical fuzzing, massive strings (100k) & SQL/XSS injection, corrupted JSON/registry recovery, 20-thread concurrent state mutations (2,000 parallel ops), UI navigation chaos across 8 panels (800 rapid switches), concurrent MUX mode toggling, BiosManager safety gate fuzzing, extreme telemetry loads (-500°C to 1250°C, 50k RPM, D3Cold sleep transitions), fan curve follower oscillations (1,000 rapid cycles), and GameSync rapid switching (400 events/sec).
  - 100% test pass rate across 96 test classes (~2,200+ unit and integration tests) in the solution.
- **Subsystem Concurrency, Telemetry & UI Fixes**:
  - Eliminated lock inversion deadlock between WmiController._lock and BiosManager._lock during MUX updates.
  - Eliminated telemetry UI starvation via non-blocking hot cache (<800ms) and Monitor.TryEnter(TimeSpan.FromMilliseconds(150)) contention fallbacks.
  - Hardened dynamic DPI scaling across 100%–200% displays with UpdateDpiFonts() and recursive control font rescaling (RescaleControlFonts).
  - Fixed multi-monitor refresh rate query fallback on invalid display indices.
  - Hardened GameSync process exit event delivery, added retry loop on file renames against file locks, and isolated test paths.
  - Hardened hardware protocol bitmasks, sign extension handling on USB charging ACPI payloads, and backlight timeouts.

### Hardware BIOS MUX Switch & BIOS Settings Full Persistence (Update 2026-11-06)
- **Hardware BIOS MUX Switch Overhaul (Device Reboot Required)**:
  - Replaced dynamic display switching (Advanced Optimus / DDS) with dedicated Hardware BIOS MUX Switch in the Display section.
  - Queries live BIOS MUX state directly from hardware via WmiController.GetGpuMode() (AcerGamingFunction, AcerSmbios Type 172, APGeAction Function 2, ListBIOSSettings).
  - Added dedicated MUX modes: Automatic (Optimus MUX), Integrated Only (iGPU MUX), and Discrete Only (dGPU MUX).
  - Mode switches write to BIOS firmware and prompt with mandatory system restart confirmation dialog (shutdown.exe /r /t 5).
- **BIOS Settings Full Persistence & Real ACPI/WMI Hardware Dispatch**:
  - Full persistence of all BIOS settings via AppSettings.BiosSettingsValues with automatic Registry synchronization at HKCU\Software\PredatorControl\BiosSettings.
  - Dispatches setting changes to Acer ACPI/WMI hardware endpoints (Function Key, Keyboard Backlight Timeout, POST Animation & Sound, Power-off USB Charge, Battery Threshold, LCD Overdrive, and InsydeH2O UEFI variables).
  - Added RefreshLiveHardwareValues() to query live hardware state on app launch.
  - Setting rows display live values, update immediately on Apply, and persist across reboots.
### Dedicated Embedded Menus, Dynamic Hardware Architecture Badge & Complete Pruning (Update 2026-11-06)
- **Dedicated Embedded Menus ("BIOS Settings", "Display Settings", "Game Sync")**:
  - Re-architected sidebar navigation with 8 dedicated page tabs: Dashboard, Fans & Cooling, RGB Lighting, Display Settings, BIOS Settings, Game Sync, System & Power, and Settings & Info.
  - Sized buttons to 38px with 4px gap starting at y = 12px, providing an uncluttered, legible navigation experience that fits completely inside standard 600px+ laptop viewports.
  - "Display Settings" (`_pnlDisplay`): Hosts Graphics Working Mode (Automatic, Integrated Only, Dedicated Only) and Display Refresh Rate (60 Hz and Max Hz buttons, LCD Overdrive switch).
  - "BIOS Settings" (`_pnlBios`): Features dynamic hardware architecture badge, two-tier safety interlocks, category tabs, setting rows, and defaults restoration.
  - "Game Sync" (`_pnlGameSync`): Features master switch, active game detection badge, profile table, and full profile CRUD workflows.
  - "System & Power" (`_pnlSystem`): Pruned and streamlined to host only Automatic Power Profiles & Mode Key and USB Charging & Battery Maintenance.
- **Dynamic Hardware Architecture & BIOS Badge**:
  - Zero hardcoded values: dynamically queries `Win32_BIOS`, `Win32_ComputerSystem`, `Win32_Processor`, system registry (`HKLM\HARDWARE\DESCRIPTION\System\BIOS`, `CentralProcessor\0`), and runtime architecture.
  - Live queried values reflect the exact host device (e.g., `Acer Predator PHN14-51 | BIOS: V1.08 (06/13/2025) | SMBIOS v3.6 | x64`).
- **Complete 100% Feature Pruning**:
  - Pruned Acer BlueLight Shieldâ„¢ across UI, backend, settings, registry, and tests.
  - Pruned GPU overclocking controls.
  - Pruned Supervisor Password prompts and state.
  - Pruned dynamic refresh rate dropdown (retained 60 Hz and Max Hz).

### UI Embedded Redesign & Helios Neo RGB Blinking Remediation (Update 2026-11-06)
- **Helios Neo Periodic RGB Blinking Elimination**:
  - Eliminated unconditional 30-second firmware reassertion timer loop in Form1 telemetry tick, which repeatedly triggered Embedded Controller (EC) mode-change lighting animations on Acer Predator Helios Neo 16 / 16s / 16 AI / 18 models.
  - Added duplicate mode deduplication guard in WmiController.TrySetPowerMode: redundant calls return true immediately without dispatching EC HID feature reports or ACPI WMI commands.
- **GPU Switching Multi-Tier Fallback Pipeline**:
  - Replaced legacy WMI-only call with multi-tier fallback: Tier 1 NVIDIA Advanced Optimus (DDS) -> Tier 2 BIOS Display MUX -> Tier 3 ACPI WMI.
  - Added active mode verification: selecting the currently active GPU mode succeeds instantly without triggering the 'Failed to switch GPU working mode via ACPI/WMI' error dialog.
  - NVIDIA Advanced Optimus switches apply dynamically in real time without requiring a system restart.
- **Embedded System & Power Section UI Redesign**:
  - Embedded comprehensive 'Display & GPU Switching (Advanced Optimus)' card directly in System & Power: real-time DDS status badge, dynamic refresh rate list, quick 60Hz/MaxHz buttons, LCD Overdrive switch, and Acer BlueLight Shield hardware slider.
  - Embedded full 'Acer BIOS & Firmware Settings' card directly in System & Power: hardware model badge, two-tier safety gates (Enable Modifications & Allow Dangerous Settings), categorized tabs (System, Power & Battery, Boot & Setup), dynamic setting rows with risk badges, Supervisor Password dialog, and Restore Defaults button.
  - Modernized cardDiag and tray shortcuts to smoothly navigate to the embedded cards in System & Power.
  - Resized standalone DisplaySettingsForm and BiosSettingsForm dialogs to 720-820px with spacious control margins, completely resolving font clipping and combobox overlap.

### Imported OpenSense/PreySense Features & Exhaustive Stress Hardening (Update 2026-11-06)
- **Acer BIOS & Firmware Settings GUI**: Native multi-tier firmware access (Mode 1 ACPI-WMI, Mode 2 Direct ACPI, Mode 3 UEFI NVRAM) with two-tier hardware safety gates ('Enable Modifications' and 'Allow Dangerous Settings' warnings). Zeroization of supervisor passwords.
- **Advanced Display & GPU Switching**: NVIDIA Advanced Optimus (Dynamic Display Switch / DDS) mode switching, BlueLight Shield gamma computation in native Rust, refresh-rate mode switches (30-360Hz+), and LCD Overdrive toggling.
- **Event-Driven Game Profiles**: Native ring-buffer event queue and debounced foreground window monitoring automatically applying custom performance, thermal, and display profiles.
- **2,460 Total Tests Passed (100% Pass Rate)**: Full test suite passes 2,175 .NET tests and 285 native Rust tests with zero failures.
- **10 Critical Bugs Remediated**:
  1. GameSyncController: Lock-order inversion deadlock with Rust FFI.
  2. PredatorNativeCore.BiosGetSettingInfo: Memory buffer allocation size mismatch.
  3. PredatorNativeCore.DisplayGetSupportedRefreshRates: Output array boundary clamps.
  4. DisplaySettingsForm & BiosSettingsForm: GDI font allocation leaks (cached to static fonts).
  5. BiosSettingsForm: TabPage child control handle leak.
  6. BiosManager & bios.rs: Two-tier safety gate bypass prevention.
  7. AppSettings: Restored DDS, BlueLight, and BIOS safety flags in CreateSnapshot().
  8. RegistrySafety: Reboot persistence for DDS mode and LCD Overdrive.
  9. ThemeManager: UI event drop elimination across background STA threads.
  10. PawnIoController: MMIO IOCTL synchronization and thread shutdown resilience.

### Acer Nitro & Nitro V Series Support (Update 2026-11-03)
- **Universal Hardware Support for Acer Nitro**: Added complete model definitions and hardware routing for all 45 Acer Nitro models across Nitro 5, Nitro 14, Nitro 16, Nitro 17, Nitro 18, and Nitro V (14, 15, 16) covering 2017 to 2026+.
- **Accurate Keyboard Lighting Tiers**: Fixed classification so monochrome models (pre-2020 Nitro 5, all Nitro V) use SingleZone, and modern models (2020+ Nitro 5, Nitro 14/16/17/18) use 4-Zone RGB.
- **Dedicated NitroSense Key Capture**: Physical scancode `0xF5`, RawInput HID pages `0xFF05` and `0xFFA0`, and WMI event triggers now toggle the app instantly over 3D games.
- **Cooling Tables**: Integrated Nitro series into `UsesFanTable` for direct hardware fan curve control.
- **2,082 Managed Tests Passed**: Full solution test suite verified 100% clean across all 2,082 managed tests and 185 native Rust core tests.

### Full-Stack Stress Hardening & Defect Elimination (Update 2026-11-03)
- **Ring-0 Safety & Driver Leaks**: Disposed existing handles prior to re-creation in MCHBAR initialization; fixed directional split-write rollback when PL1/PL2 bounds checking fails.
- **Hardware Concurrency**: Guarded fan table WMI query to prevent invalidating cache on unsupported hardware models.
- **Rust Core SEH Hardening**: Protected all FFI entry points with pointer bounds checks (< 0x10000) to prevent uncatchable 0xC0000005 crashes.
- **FFI Handle Finalizers**: Added finalizers and GC.SuppressFinalize in AudioMeter, CurveFollower, and ThermalSafetyCoordinator to prevent native handle leaks.
- **UI & WinForms Reliability**: Added input-key overrides for shortcut navigation keys in CustomShortcutsForm and eliminated WinForms disposal/nullability warnings in Form1.
- **Settings & Background Resilience**: Added typed registry conversion fallbacks, non-blocking TCP reconnect cooldown in background service client, and cancellation token propagation.
- **100% Full Test Pass Rate**: Full suite passes all 2,092 managed tests and 189 native Rust unit and FFI fuzz tests (0 failed, 0 skipped).

### Predator Helios Neo 16 & 18 (PHN16/PHN18) RGB & Lid Logo Isolation (Update 2026-11-05)
- **Direct Win32 HID Client (`EnekRgbClient`)**: Direct HID communication with ENEK5130 RGB controller (`HID\VID_...&ENEK5130`) via 11-byte report ID `0xA4` target `0x21`. Eliminates the periodic keyboard blinking and RGB setting failure on PHN16 and PHN18 models caused by EC bus contention and ACPI-WMI resets.
- **Zero Lid Logo Queries**: Completely purged `GetGamingLED` and `GetGamingLEDBehavior` from application code. No queries are performed against the lid logo across the application lifetime.
- **Permanent Laptop Lid Logo Shut-Off**: Implemented `TurnOffLogo()` across ENEK direct HID (Target `0x83`, Effect `0x01`, Brightness `0`) and WMI fallback (`SetGamingLEDBehavior` 0x06 | 0x0100, `SetGamingLEDColor` 0x01ul). Dispatched at app startup, hardware sync retries, zone color updates, and backlight shutdown, ensuring the exterior lid logo remains completely dark at all times.
- **2,144 Managed Tests Passed**: Full solution test suite verified clean across 2,144 managed tests and 223 native Rust unit/stress tests.

### Keyboard Brightness Persistence & Lid Logo Detachment (Update 2026-11-04)
- **Keyboard Brightness Persistence Across Reboot & Fast Startup**: Fully synchronized `KeyboardBrightnessLevel` and `RgbBrightness` settings across `AppSettings`, registry keys (`HKCU\SOFTWARE\PredatorControl`), and UI sliders. Configured immediate registry flush on slider commit (`ValueCommitted`) to eliminate race conditions and prevent brightness from reverting to 100% on cold boot, Windows Fast Startup, ACPI power transitions (sleep/resume), session unlock, or application restart.
- **Complete Detachment of Laptop Lid Logo RGB**: Completely detached laptop lid logo RGB from application lighting routines by eliminating `SetGamingLEDColor` zone `0x01` logo payloads and OpenRGB device 0 triggers. The laptop lid logo is now fully decoupled from application lighting profiles, leaving it permanently under OEM hardware default/BIOS control without interference.

### Key Highlights
1. **Per-Zone RGB Persistence & Boot Decoupling**: Static 4-zone keyboard RGB colors are now strictly isolated from Dynamic RGB animation frames. Booting or launching the app will never clobber individual zone colors with dynamic effect presets.
2. **Dynamic RGB Streamlining**: The legacy "Twinkling" effect has been completely removed across the entire stack. An explicit "Off (Static)" mode has been added as the primary option to instantly halt animation timers and restore user-defined per-zone static colors.
3. **Rust Core FFI Hardening**: All 183 unit and FFI fuzz tests in predator_core pass with 100% success. C-ABI boundaries are guarded by catch_unwind panic barriers, pointer alignment checks, and poison recovery.
4. **Ring-0 Safety & Directional MMIO**: CPU power limit adjustments strictly enforce directional split-write ordering (NewPL1 > OldPL2) to prevent transient over-current trips. MCHBAR driver handle leaks and PCI mutex timeout deadlocks are fully resolved.
5. **Thermal Safety Blackout Resilience**: AntiThrottleSafety and ThermalSafetyCoordinator handle sensor dropout, NaN, and negative temperatures gracefully while maintaining fail-safe throttling without corrupting persistent settings.
6. **100% Test Pass Rate**: Full test suite passes **1,941 / 1,941 tests (0 failed, 0 skipped)**.
7. **Single-File Portable Packaging**: Standalone PredatorControlApp.exe (~54.5 MB) bundles the .NET runtime, native Rust core, and PawnIO drivers as embedded resources with automatic runtime extraction fallback.
8. **Keyboard Brightness Persistence**: Synchronized `KeyboardBrightnessLevel` and `RgbBrightness` with immediate registry flush on slider commit, preventing unwanted reversion to 100% across cold boots, Windows Fast Startup, and application restarts.
9. **Laptop Lid Logo Detachment**: Completely decoupled laptop lid logo RGB from application lighting by eliminating `SetGamingLEDColor` zone `0x01` logo payloads and OpenRGB device 0 triggers, preserving OEM hardware/BIOS defaults.

---

# Predator Control 1.5.5: Rust migration

This locally rebuilt version retains the existing brightness changes. It is not an official upstream release.

## What moved to Rust

- Acer SMBIOS type 170 hotkey and type 172 gaming-record parsing. Existing ACPI/PPCC parsing was already native-first.
- 30 selected pure Acer protocol encoding/decoding operations, including mode, fan, sensor, battery and brightness-related fields.
- Lighting tier selection, RGB/ARGB hex parsing, alpha-aware blending, and Acer service brightness/speed quantization.

The migrated operations retain C# fallbacks. Windows data acquisition, WMI calls, device writes, UI, model-name matching, named colors, and other unmigrated logic remain in their existing paths. The existing EC-HID transport already has a Rust implementation. This is a migration of the listed components, not a claim that every firmware parser, protocol routine, or lighting rule has moved.

## Build and verification

- 262 selected .NET tests and 137 Rust tests across 15 filtered module runs passed. The release build had zero warnings and errors.
- Synthetic native-versus-managed parity checks cover SMBIOS malformed/truncated inputs, protocol boundaries, color parsing/blending, tier rules and brightness levels. Existing brightness regressions were included.
- The targeted tests did not intentionally issue hardware-write commands. Loading the native core calls its initializer, which may open PawnIO and start telemetry/NVAPI polling, so the tests are not fully hardware-isolated.
- The complete inherited hardware/integration suite, UI launch, and physical laptop behavior were not tested. No performance improvement is claimed without measurements.
- The published executable's bundled managed assembly and Rust DLL were compared by SHA-256 against the freshly built files. Version remains 1.5.5.

Run `Source/Verify-Brightness.ps1` for the audited test selection. Raw test results and build logs are in `Verification`.

## Files and rollback

`Source` contains the updated source, tests and their generated build files. `Original-Build-Files` preserves the prior 2165 historical build files requested earlier; those compiled artifacts can contain original code. They are separate from the updated app. All eight source Markdown documents were updated, with relevant copies at the package root.

To return to the pre-migration app, exit the running app from its tray menu and run `Rollback/PredatorControlApp-before-rust-migration.exe`. The prior Downloads folder was preserved. The source history bundle and migration patch are in `Verification`; source checkpoint: `1201b1d9ececfaba754e5ea0e95655939a0069e2`.

This earlier release was unsigned. See the current 1.5.8 improvements notes above for updated behavior.

Local audit update: see AUDIT.md at the source root for fixes, exact verification scope, and unresolved hardware-policy findings.

Zone restoration update (2026-11-03): restores missing legacy zone colors, preserves selected layout after capability detection, and prevents queued lighting requests from sending stale snapshots. 92 selected managed tests and 174 Rust unit tests passed. Physical restart verification and full hardware-writing suite remain outstanding.

Brightness persistence & lid logo detachment update (2026-11-04): synchronizes KeyboardBrightnessLevel and RgbBrightness, forces immediate registry flush on slider commit to eliminate 100% brightness reversion across reboots, fast startup, and app restarts; completely detaches laptop lid logo RGB by removing SetGamingLEDColor zone 0x01 payloads and OpenRGB device 0 triggers, leaving exterior lid lighting under OEM hardware default.

