# Changelog

Local 1.5.5 improvements: one update check at startup, including hidden launches, plus manual checks; verified updates with rollback copy; verified embedded-only native loading with hash-specific cache; on-demand telemetry and PawnIO; compatibility report with preview, copy and save. The build remains unsigned pending a trusted signing certificate.

All notable changes to **Predator Control App** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.5.5 Comprehensive Audit, Bug Fix & Stabilization Overhaul] - 2026-11-02

Comprehensive stability and hardening overhaul across C# WinForms, Rust Core (`predator_core`), and the test suite:

### Fixed & Hardened
- **WinForms Lifecycle & GDI+ Robustness**:
  - Fixed double-disposal race condition in `Form1.Designer.cs` and `Form1.cs` where `_trayIcon.Dispose()` triggered `ObjectDisposedException` on application shutdown.
  - Eliminated `SafeBeginInvoke` delegate loss race condition when background threads dispatched tasks before UI handle creation.
  - Resolved native unmanaged heap leak in `HistoryBuffer` (`TelemetryHistory.cs`) via `IDisposable` + finalizer calling `HistoryDestroy`.
  - Guarded against native GDI+ status code 3 (`OutOfMemoryException`) in `HistoryGraphControl` by validating and sanitizing `NaN`/`Infinity` coordinates before path construction.
  - Eliminated 624 GDI objects per frame allocation in `KeyboardRgbMap.cs` by caching `s_centeredFormat`, path reuse, and shared pens/brushes.
  - Fixed mouse wheel message swallowing in `DarkScrollPanel.cs` over embedded sliders, dropdowns, and graphs.
  - Cleaned up `KeyboardHook.cs` finalizer to remove managed object and event dereferencing during CLR teardown.
  - Fixed Windows modifier key detection in `CustomShortcutsForm.cs` and `Form1.cs` using `KeyboardHook.GetAsyncKeyState`.
- **Lock-Free Concurrency & Ring-0 Safety**:
  - Eliminated lock inversion in `AcerServiceClient.cs` by converting `SupersedeKeyboardLighting` to a non-blocking lock-free atomic increment.
  - Implemented strict directional split-write failure rollback in `pawnio.rs` to prevent partial hardware power limit states.
  - Hardened C-ABI boundaries in `PredatorNativeCore.cs` and `PredatorNativeMigrations.cs` with buffer length validation.
  - Extracted worker and released `GLOBAL_TELEMETRY` mutex before thread join in `predator_core_cleanup`.
  - Added `#[repr(C)]` to `pub struct Rgb8` ensuring strict memory layout across FFI.
- **Per-Zone Static RGB Persistence**: Completely decoupled Static Zone 1–4 RGB colors from Dynamic RGB animations in `AppSettings.cs` and `WmiController.cs`. Fixed boot/startup color clobbering where dynamic effect colors overwrote individual static zones.
- **Dynamic RGB Overhaul**: Completely excised legacy `"Twinkling"` effect across all enums, UI dropdowns, and lighting engines. Added explicit `"Off (Static)"` mode as index 0 in the Dynamic RGB dropdown to cleanly halt animation timers and re-engage saved per-zone static colors.
- **Thermal Safety & Anti-Throttle Blackout**: Hardened `AntiThrottleSafety.cs` and `ThermalSafetyCoordinator.cs` against sensor dropout/blackout (`NaN`, `Infinity`, `<= 0`). Corrected power cap return on corrupt sensor input.
- **MMIO & Ring-0 Power Limit Hardening**: Enforced strict directional split writes (`NewPL1 > OldPL2` ordering) in `PawnIoController.cs` and Rust core. Fixed handle leaks in MCHBAR initialization failure paths, added `Global\Access_PCI` mutex timeout protection, and preserved hardware Tau=0 architectural windows.
- **UI & WinForms Concurrency Hardening**: Fixed lock inversions, guarded thread-unsafe UI updates with `InvokeRequired`, safely cloned fan curve collections before normalization, eliminated premature Icon disposal, and enabled clean memory trimming on minimize-to-tray.
- **Rust Core & FFI Hardening**: Added panic barriers (`catch_unwind`), null-check guards, alignment safety, and mutex poison recovery across all exported C-ABI functions in `predator_core`.

### Verification
- **Test Suite**: **1,965 tests passed out of 1,965 (100% pass rate, 0 failures, 0 skipped)** across unit, integration, stress, and fuzz suites in `PredatorControlApp.Tests`.
- **Rust Core Tests**: **185 unit and FFI stress tests passed out of 185 (100% pass rate)** in `cargo test --release`.
- **Portability**: Published single-file self-contained x64 executable (`PredatorControlApp.exe`) with embedded `predator_core.dll` and PawnIO drivers.

---

## [1.5.5 Rust parsing and rules update] - 2026-11-02

This local update keeps version 1.5.5 and the prior brightness fix. Native-first Rust paths now cover Acer SMBIOS gaming/hotkey records, selected hardware-protocol packet rules and selected lighting capability, color and service-level rules. Managed fallback implementations and existing hardware transports remain available. Existing native ACPI PPCC parsing remains in place.

Verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs. No physical laptop or live hardware-write validation is claimed.

---

## [1.5.5 brightness update] - 2026-11-02

This locally rebuilt brightness update is not an official upstream release.

### Fixed

- Give acknowledged firmware brightness control sole ownership of dimming, keeping RGB color packets unscaled. Use software color scaling only on supported paths when firmware brightness is unavailable.
- Render managed, native, and audio-reactive frames at full slider intensity before transport applies brightness once. Preserve color alpha without applying it twice.
- Serialize keyboard service writes, drain an active service write before WMI dispatch, and skip stale queued requests so a delayed low-brightness update cannot overwrite a newer 100% request. Prefer WMI and use keyboard service fallback only when needed, avoiding a second service write after a successful WMI write.
- Map two supplied zone colors across all four physical keyboard zones.
- Update all included Markdown documents and local version metadata, including native extraction cache version 1.5.5.

### Verification and limits

The previous brightness update passed 243 selected .NET and 126 pure Rust tests; those are historical results. Earlier migration verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs. The complete inherited hardware/integration suite, UI launch, and physical keyboard brightness require separate verification.

Brightness tests check packet values and request ordering without changing laptop hardware. Physical keyboard brightness and model-specific firmware behavior still require validation on the target laptop. Historical test totals and release claims below describe earlier documentation, not tests rerun for this local update.

---

## [1.6.0] - 2026-11-02

### 🚀 Summary
Native-core expansion release: seven additional subsystems moved from managed C# into the
`predator_core` Rust crate, closing the gaps left by the 1.5.5 audit. All new paths are
native-first with the existing managed implementations retained as fallbacks.

### 🌟 Added & Improved
- **Native Keyboard Decision Engine (`hook.rs`)**: The low-level keyboard hook now delegates
  keystroke interpretation to Rust — modifier tracking, injected-key rejection, function and
  media pass-through, a passthrough rule that protects Windows/modifier/navigation/OEM keys
  from scancode collisions, Predator/Mode scancode disambiguation, the 3 s hold timeout,
  Predator+1-5 combos and custom mode shortcut matching. `SetWindowsHookEx` and the delegate
  lifetime correctly remain managed. The engine exposes live state so the managed mirror
  flags stay consistent.
- **Native Shortcut Rule Engine (`shortcuts.rs`)**: All nine `ModeShortcutValidator` rules
  and duplicate-combination detection now execute natively. Rule *wording* stays in C# so
  every user-facing message and test assertion is unchanged.
- **Native Battery ETA Engine (`battery.rs`)**: EMA smoothing, phase-change handling,
  sleep/clock-skew re-baselining, plausibility clamps and charge/discharge ETA computation.
- **Native Telemetry History (`history.rs`)**: Ring-buffer storage with gap-aware
  min/max/average and bounded snapshot export behind an opaque handle.
- **Native Graph Projection (`graphproj.rs`)**: The per-frame series-to-pixel loop used by
  the streaming telemetry graph.
- **Native Process Matching (`procsync.rs`)**: Executable/process name normalisation,
  profile matching against running processes and game-profile field clamping.
- **Native Pipe Framing (`pipeguard.rs`)**: Control-character stripping, UTF-8-safe
  truncation and message validation for the secure named-pipe channel.
- **Interop split**: New surface lives in `PredatorNativeMigrations.cs`; every wrapper
  returns an explicit "unavailable" sentinel instead of a plausible zero, keeping fallback
  paths reachable.

- **Single-File Full Binary Integration**: Integrated all native binaries (predator_core.dll Rust dynamic library and PawnIO kernel bytecode payloads) directly into PredatorControlApp.exe as embedded manifest resources with automatic runtime self-extraction and zero-configuration portable execution. Zero external DLL or driver side-cars needed.

### 🧹 Cleanup
- Removed `predator_core/target/` — roughly 450 MB of untracked build artifacts that had been
  shipped inside the source drop and are regenerated by `cargo build`.

### 🧪 Verification
- `cargo test`: **169 passed, 0 failed** (158 unit + 11 FFI fuzz/stress), 0 warnings.
- `dotnet build PredatorControlApp`: **0 errors, 0 warnings**.
- Native export surface grown from 56 to **94** entry points.

### 📝 Documentation
- `RUST-MIGRATION.md` corrected and rewritten: the previously non-existent `hook.rs` module
  is now real, the module count (16) and export count (94) are accurate, and the stale
  "21 FFI entry points" claim is fixed.

---

## [1.5.5] - 2026-11-01

### 🚀 Summary
Targeted stability, safety, and UX release introducing embedded custom mode shortcut management, single-file self-contained portable runtime packaging, caption button lifecycle fixes, per-zone static RGB persistence decoupling, WASAPI audio capture auto-reconnect, and deep hardware concurrency hardening.

### 🌟 Added & Improved
- **Embedded Custom Mode Shortcuts**: Embedded in-app keyboard shortcut manager directly into Settings Card 5 for all five power profiles (Eco, Quiet, Balanced, Performance, Turbo) with Record, Clear, Reset Defaults, and Save & Apply; eliminated external popup dialog dependency.
- **Single-File Self-Contained Portable Executable**: Fully bundled standalone package (`PredatorControlApp.exe`, 54.4 MB) containing embedded `predator_core.dll` native runtime, zero external installer or runtime setup needed.
- **Predator Key & Caption Lifecycle Fixes**: Fixed issue where opening app via physical Predator key prevented title bar close/minimize buttons from working; `_btnCaptionMin` and `_btnCaptionClose` bind immediate foreground demotion (`HWND_NOTOPMOST`) and `HideApp()` on both `MouseDown` and `Click`.
- **Per-Zone Static RGB Persistence & Dynamic RGB Overhaul**: Separated persisted static 4-zone colors from transient dynamic RGB effect buffers; dynamic RGB animations no longer clobber saved zone colors on startup/boot; completely removed unstable "Twinkling" effect across all enums, UI palettes, and engines; added explicit "Off" mode as default item in Dynamic RGB dropdown that activates saved per-zone static profile.
- **WASAPI Audio Capture Auto-Reconnect**: Fixed infinite loop and thread stall on `AUDCLNT_E_DEVICE_INVALIDATED` (`0x88890004`) when unplugging or switching default audio endpoints; added automatic exponential backoff reconnect loop in `CaptureLoop`.
- **Hardware Concurrency & Mutex Safety**: Wrapped WMI/ACPI action commands in inter-process `WithGamingMutex` to prevent collisions with background OEM services; enforced two-phase directional split-writes in `PawnIoController` (`NewPL1 > OldPL2 ? PL2 then PL1 : PL1 then PL2`) under `lock (_ioLock)`.
- **Rust Core Hardening (`predator_core`)**: Floated piecewise linear curve interpolation to eliminate debug integer overflow panics on boundary temps; wrapped all 21 FFI entry points in `catch_unwind` panic firewalls.
- **Exhaustive Testing & Verification**: 1,835 / 1,835 test suite pass rate in `PredatorControlApp.Tests` (100% pass) and 85 / 85 tests in `predator_core`.
- **Fullscreen Window Preemption**: Pressing the dedicated Predator key activates Win32 `AttachThreadInput`, `SetWindowPos(HWND_TOPMOST)`, `BringWindowToTop`, and `SwitchToThisWindow` routines, reliably bypassing Windows `SetForegroundWindow` restrictions and elevating the window above exclusive fullscreen DirectX/Vulkan game surfaces.
- **Process Scheduling Priority**: Automatically elevates application process priority class to `High` and UI thread priority to `AboveNormal`, guaranteeing real-time responsiveness and zero stutter even while demanding 3D games utilize 100% CPU/GPU.
- **Background Smart Toggle**: If the app is already visible in the background while a fullscreen game is focused, pressing the Predator key pulls it directly into the foreground over the game rather than mistakenly minimizing or hiding it.

---

## [1.5.0] - 2026-10-17

### ðŸš€ Summary
Major feature release introducing native Acer BIOS/UEFI & ACPI WMI scanning for per-mode default CPU power limits (PL1/PL2), CPU silicon fused capability discovery via PawnIO and ACPI PPCC tables, dynamic per-mode slider controls with stock BIOS defaults reset, and automated pre-override firmware baseline capture.

### ðŸŒŸ Added & Improved
- **Native BIOS/UEFI & ACPI WMI Power Limit Scanner**: Discovers true hardware baseline PL1/PL2 power limits directly from firmware memory, ACPI `PPCC` tables, Acer BIOS MOF WMI classes (`root\WMI`), and Intel RAPL MSRs (`0x606`, `0x614`) without any third-party software dependencies.
- **Dynamic Per-Mode Power Limit Sliders**: Dynamically scales slider minimums and maximums according to probed motherboard and CPU silicon capabilities with real-time `PL2 >= PL1` coupling.
- **Reset to BIOS Default**: Instant restoration of OEM factory-calibrated PL1/PL2 limits for each power profile (`Eco`, `Quiet`, `Balanced`, `Performance`, `Turbo`).
- **Hardware Fingerprint Invalidation**: Deterministic SMBIOS platform fingerprinting automatically refreshes scanned baseline limits if BIOS is updated or app runs on a different laptop.
- **PawnIO Hardware Enforcement**: Hardware-level register enforcement via `\\.\PawnIO` ensuring custom CPU power limits apply smoothly across mode transitions.

### Added (2026-10-29)

- **Per-Zone and Per-Key Keyboard RGB**: The Lighting page now has a dedicated four-zone colour editor (guaranteed on every lighting-capable Predator) and an experimental per-key map. Zone and key palettes persist across restarts, and the map supports click, drag and right-click painting.
- **Learned Keyboard Capability**: The keyboard tier is discovered from the BIOS/SMBIOS identity and a live probe of the Acer Lighting Service, never assumed. Per-key candidate models only filter the probe; `VerifiedOnHardware` stays false until an acknowledged write on the exact machine. Users can force 4-zone or force the experimental per-key path and re-detect at any time.
- **Per-Key Fallback**: If the service rejects a per-key write, the app reports the degradation and applies the four-zone colours instead of silently doing nothing.
- **Staged Thermal Guard**: Configurable warn / ceiling / critical thresholds for CPU and GPU, hysteresis, EMA smoothing, rate-limited fan floors, a proportional CPU watt cap, persistence and a Fans-page card. The critical stage pins fans to 100% and floors the CPU cap; it never shuts the machine down.
- **Smooth Power-Mode Switching**: A serialized, latest-wins switch guard performs one EC/WMI mode write per transition, defers the Windows overlay sync and pauses telemetry for the settle window. This targets the audio/video stutter and Bluetooth dropout seen while changing profiles.
- **Tests**: 40 new tests cover the capability classifier, palette codec, per-key gating and fallback, switch-guard coalescing and spacing, and thermal stages, hysteresis, EMA and rate limiting. The suite now executes 1,414 cases.

### Changed (2026-10-29)

- **Duplicate Mode Writes Removed**: `ApplyPowerMode`, `CpuPowerLimitController.ApplyPowerLimitsForMode` and `ApplyActiveModePowerLimits` no longer re-enter the EC/WMI mode write that the switch guard already owns.
- **Deferred Overlay Sync**: `PowerSetActiveOverlayScheme` is queued after the mode write settles instead of running inline under the WMI lock.

### Changed (2026-10-28)

- **LCD Overdrive Decoupling (Milestone 8)**: The LCD Overdrive toggle is now UI and persisted state only. All backend dispatches are stripped - the four WMI methods (`SetGamingProfile`, `SetGamingProfileSetting`, two `SetGamingMiscSetting` variants), the Acer service IPC packet `100`, and the boot-time re-apply - because those sweeping writes froze the function-key row on some Predator board revisions. Payload builders remain as protocol documentation and test fixtures.
- **Documentation**: `README.md` now documents PawnIO installation (WinGet one-liner plus manual installer), CPU PL1/PL2 power limits and the thermal safety layer. The stale "912 unit tests" claim is corrected: the suite now executes 1,414 cases.

## [1.4.2] - 2026-10-16

### ðŸš€ Summary
Critical stability and hardware synchronization patch addressing benign task cancellation popups, battery auto-discharge activation, fan curve UI overlaps, thread safety in multi-threaded settings sanitization, and GDI handle lifecycle management.

### ðŸŒŸ Fixed & Improved
- **Benign Cancellation Filtering**: Suppressed false alarm `TaskCanceledException` and benign network abort popups in unobserved task and UI thread exception handlers.
- **Battery Calibration Discharge Circuit**: Synchronously disabled 80% charge limits with settling delay before calibration initiation; resolved Phase 2 completion deadlocks and restored warmup discharge ETA estimates.
- **Fan Curve UI Overlay**: Removed duplicate popout button bleeding through fan curve header; enabled double-click on embedded curve graphs to directly launch curve editor.
- **Hardware & Telemetry Concurrency**: Decoupled EC HID mutex waits from WMI lock to eliminate telemetry thread blocking; sanitized power mode tracking and RPM decoding across fan channels.
- **Settings Multi-Thread Synchronization**: Synchronized curve sanitization under collection instance locks to eliminate array boundary exceptions during concurrent reads/writes.
- **Resource Lifecycle & High-DPI**: Added native `HICON` handle disposal on tray icon teardown, wrapped custom control theme updates in UI thread dispatches, and added dynamic scale updates on display DPI changes.

## [1.4.1] - 2026-10-15

### ðŸš€ Summary
Maintenance and hardening release introducing power mode transition smoothing, battery calibration overhaul with wake lock and ETA calculator, full-app adversarial stress testing suite, and deep GDI/Win32 resource safety fixes.

### ðŸŒŸ Added & Improved
- **Direct Power Mode Transitions**: Eliminated 1-second revert bounce through Balanced mode when changing power profiles; added optimistic UI dispatch and 2500ms telemetry lock window.
- **Battery Calibration Experience**: Added dedicated calibration loading overlay, real-time EMA charging/discharging ETA calculator, Win32 display/system execution wake lock (`SetThreadExecutionState`), cancel button, and post-shutdown recovery state machine.
- **GDI+ Bounds Hardening**: Fixed GDI+ parameter exceptions when controls are resized to zero or sub-14px bounds during minimization/docking.
- **Hardware & Protocol Integrity**: Hardened ACPI error code handling in sensor telemetry, added thread synchronization across battery and power management methods, and eliminated non-blocking GC finalizer starvation in PDH monitoring.
- **Multi-Monitor Window Restore**: Fixed 16:9 maximized bounds restore across multi-monitor working area origins.

## [1.4.0] - 2026-10-07

### ðŸš€ Summary
A major release bringing 16:9 adaptive maximization, 26+ advanced Acer Predator hardware controls, accurate high-RPM telemetry, full custom fan curve persistence, CoolBoost support, 34 keyboard RGB lighting modes, zero-dependency standalone distribution, and deep defensive stability hardening.

### ðŸŒŸ Added
- **Modern UI Redesign**: Transitioned from the legacy vertical-strip layout to a modern widescreen dashboard with organized cards, clean telemetry gauges, and crisp high-DPI scaling.
- **16:9 Maximize Mode**: Added a dedicated top-right maximize caption button and double-click titlebar maximize/restore. Automatically calculates and scales the application window to the largest possible 16:9 aspect ratio centered on the active display.
- **Direct Embedded Controller (EC) HID Protocol**: Communicates directly with laptop EC hardware for instantaneous response with seamless, transparent fallback to Acer WMI.
- **MUX Switch / GPU Working Mode**: Directly connected to Acer BIOS. Effortlessly toggle between **Hybrid (Nvidia Optimus)** for maximum battery life and **Discrete GPU** for uncompromised gaming FPS (requires standard reboot to apply).
- **GPU Sleep / D3Cold Awareness**: Instant visual indicator in the UI showing when the dedicated Nvidia GPU is in low-power deep sleep (D3Cold).
- **3rd Fan / System Fan Control**: Full support and speed monitoring for modern Predator laptops equipped with 3 fans (CPU, GPU, and System/Chassis).
- **Acer CoolBoost Hardware Toggle**: Direct hardware toggle to unlock elevated fan ceiling curves during intense workloads with persistent state retention.
- **Factory OEM Fan Lookup Tables**: Accurately mapped Acer factory fan lookup tables for native hardware RPM-to-percentage scaling.
- **Interactive Graphical Fan Curve Editor**: Full custom visual fan curve editor with support for multi-point curves, persistent settings, and deep-cloned points.
- **Curve Hysteresis & Spike Damping**: Eliminates annoying rapid fan throttling on momentary CPU spikes using smoothing and hysteresis.
- **Sensor Failsafe & Periodic Reassertion**: Guardrails to guarantee fans reassert failsafe cooling if background software hangs or crashes.
- **Independent Fan Channel Locking (FanLock)**: Lock CPU, GPU, or System fans to specific speeds independently.
- **Windows PDH Hardware Load Tracking**: Ultra-lightweight CPU & GPU utilization monitoring directly via Windows Performance Data Helper counters.
- **Real-Time Streaming Telemetry History Graphs**: Live streaming graph rendering temperature and RPM trends smoothly.
- **Comprehensive Battery Telemetry**: Real-time readouts of battery health %, wear percentage, cycle count, charge rate, and power status.
- **Hardware Battery Calibration Cycle**: Automated cycle to discharge, reset, and recalibrate internal battery fuel-gauge sensors.
- **Power-Off USB Charging & Low-Battery Floor**: Toggle external device charging via USB ports while the laptop is turned off, with a low-battery protection floor.
- **Expanded Keyboard RGB Engine (34 Effects)**: Upgraded from 8 to 34 rich dynamic lighting effects with fine-grained color, speed, and brightness controls.
- **Physical Predator Mode Key Interception**: Non-blocking Raw Input interception for the dedicated physical hardware "Mode" key on Predator keyboards, including synchronized Mode Key LED indicator.
- **Comprehensive Hardware Identity & SMBIOS Probing**: Full DMI/SMBIOS hardware detection for exact motherboard model, BIOS version, EC version, and CPU/GPU identities.
- **User Hardware Capability Overrides**: Ability to override probed hardware capabilities via local configuration if needed.
- **System / Light / Dark Themes**: Clean, modern UI theme switching with native high-DPI scaling and DarkScrollPanel.
- **One-Click Diagnostic Dump**: Instantly copies all hardware IDs, sensor values, power modes, and fan status to the clipboard for troubleshooting.
- **Zero-Dependency Standalone Build**: Single-file executable (`PredatorControlApp-Standalone.exe`) that runs immediately on any Windows 10/11 x64 system without needing .NET installed.

### ðŸ› ï¸ Fixed & Improved
- **Accurate Fan Speed Readings**: Fixed the dashboard RPM calculation and eliminated the false 2560 RPM ceiling clamp when fans operate at full throttle.
- **Fan Curve Persistence**: Custom CPU and GPU fan curves now reliably save and persist across application restarts.
- **Acer CoolBoost Retention**: Fixed CoolBoost state persistence across restarts and power mode transitions.
- **Startup Button Highlighting**: Active power modes, fan modes, refresh rates, and GPU modes are now properly highlighted immediately on launch.
- **Single-Instance Window Activation**: Launching the application when already running now reliably brings the existing window to the front instead of minimizing/toggling visibility.
- **Removed Keyboard Backlight Timeout**: Completely removed keyboard backlight timeout setting and background worker.
- **Stability & Crash Prevention**: Protected against unhandled exceptions during GPU mode changes and startup tasks, hardened registry path validation against traversal, and eliminated race conditions under high-concurrency loads.

### ðŸ”’ Security & Networking
Historical note: the following describes an earlier version. The current local build checks for updates once on startup and on manual request.

- **100% Offline by Default**: The application contains zero background network requests, zero telemetry, zero analytics, and zero startup checks.
- **On-Demand Update Verification**: Network activity is restricted strictly to when the user manually clicks "Check for Updates".
- **SHA-256 Checksum Verification**: Downloads are streamed through SHA-256 incremental hashing to verify integrity against published release checksums before staging.
- **HTTPS & GitHub Domain Allowlisting**: Enforces strict URL scheme and domain checks (`github.com`, `objects.githubusercontent.com`).
- **Process Invocation Hardening**: Self-updating replacement scripts use fully qualified system paths (`%SystemRoot%\System32\`) and directory isolation to prevent binary planting or PATH redirection.

---

## [1.3.0] - 2026-09-20
- Improved fan curve precision and stability.
- Added backlight state manager and registry persistence.
- Initial multi-point fan curve graph integration.

---

## [1.0.0] - 2026-08-15
- Initial release of Predator Control App.
- Core power modes (Silent, Balanced, Performance, Turbo, Eco).
- Basic 2-fan control (Auto, Max, Custom).
- 8 keyboard RGB lighting presets.
- 60Hz / Max Hz display refresh rate switcher.
- System tray minimization and startup registration.

Migration test scope: targeted tests do not intentionally issue hardware writes. In the earlier migration, loading PredatorNativeCore could open PawnIO and start telemetry/NVAPI polling. The current build initializes those facilities on demand; tests that request hardware operations still require care. Physical hardware effects were not measured.

Local audit update: see AUDIT.md at the source root for fixes, exact verification scope, and unresolved hardware-policy findings.
