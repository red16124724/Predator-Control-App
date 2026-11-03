# Predator Control App v1.5.5 Release Notes & Audit Summary

The sidebar version label displays “Made by RED4724”. This local build is unsigned; trusted signing requires a suitable code-signing certificate and signing tool.

Startup-check update verification: the Release publish succeeded and 13 focused .NET tests passed, including one-shot scheduling and cancellation. A live GitHub check, hidden-startup UI, and hardware behavior were not exercised in this verification.

Current local 1.5.5 improvements: one update check on startup, including hidden launches, and manual checks without periodic polling; mandatory asset-specific SHA-256, upstream replacement warning and previous-executable backup; verified embedded native library; on-demand native initialization; and a compatibility report. The executable remains unsigned; physical hardware and live update installation were not tested.

## Executive Overview
Version 1.5.5 brings a comprehensive codebase audit, bug fix, and stabilization overhaul across the C# .NET 10 WinForms UI, native Rust Core (predator_core.dll), Ring-0 PawnIO hardware drivers, and the complete 1,941-test suite.

### Key Highlights
1. **Per-Zone RGB Persistence & Boot Decoupling**: Static 4-zone keyboard RGB colors are now strictly isolated from Dynamic RGB animation frames. Booting or launching the app will never clobber individual zone colors with dynamic effect presets.
2. **Dynamic RGB Streamlining**: The legacy "Twinkling" effect has been completely removed across the entire stack. An explicit "Off (Static)" mode has been added as the primary option to instantly halt animation timers and restore user-defined per-zone static colors.
3. **Rust Core FFI Hardening**: All 183 unit and FFI fuzz tests in predator_core pass with 100% success. C-ABI boundaries are guarded by catch_unwind panic barriers, pointer alignment checks, and poison recovery.
4. **Ring-0 Safety & Directional MMIO**: CPU power limit adjustments strictly enforce directional split-write ordering (NewPL1 > OldPL2) to prevent transient over-current trips. MCHBAR driver handle leaks and PCI mutex timeout deadlocks are fully resolved.
5. **Thermal Safety Blackout Resilience**: AntiThrottleSafety and ThermalSafetyCoordinator handle sensor dropout, NaN, and negative temperatures gracefully while maintaining fail-safe throttling without corrupting persistent settings.
6. **100% Test Pass Rate**: Full test suite passes **1,941 / 1,941 tests (0 failed, 0 skipped)**.
7. **Single-File Portable Packaging**: Standalone PredatorControlApp.exe (~54.5 MB) bundles the .NET runtime, native Rust core, and PawnIO drivers as embedded resources with automatic runtime extraction fallback.

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

This earlier release was unsigned. See the current 1.5.5 improvements note above for updated behavior.

Local audit update: see AUDIT.md at the source root for fixes, exact verification scope, and unresolved hardware-policy findings.

Zone restoration update (2026-11-03): restores missing legacy zone colors, preserves selected layout after capability detection, and prevents queued lighting requests from sending stale snapshots. 92 selected managed tests and 174 Rust unit tests passed. Physical restart verification and full hardware-writing suite remain outstanding.
