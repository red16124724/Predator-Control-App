# Rust migration — native hot paths

The current 1.5.5 improvements load only the verified embedded native library. Native telemetry starts when requested and PawnIO opens on demand. The 101 focused managed tests passed; physical hardware validation and performance measurement are pending.

This document records the C# → Rust migration of the application's hot-path and
rule-driven subsystems into the `predator_core` crate.

## Local 1.5.5 firmware, protocol, and lighting update

- `smbios.rs` parses Acer SMBIOS type 172 gaming/version records and type 170 hotkey records. The existing ACPI PPCC Rust parser predates this update. C# still obtains firmware tables, and the managed SMBIOS parser remains the fallback.
- `protocol_migration.rs` handles selected Acer WMI/EC power-mode mappings, status and sensor fields, fan behavior/speed/table values, CoolBoost, USB charging, miscellaneous settings, system-config masks, backlight timeout fields, and battery status decoding. Actual WMI calls and battery argument-object construction stay in C#. Existing WMI/HID transport and hardware-write paths are unchanged; EC-HID already includes a Rust transport.
- `lighting_rules.rs` handles capability-tier arbitration from C#-supplied candidate flags, hexadecimal RGB/ARGB parsing, color blending, and Acer service brightness/speed quantization. C# retains model matching, per-key verification, named colors, persistence, hardware dispatch, and the previous brightness-ownership logic. Existing Rust animation/audio code remains in place.

Native calls are attempted first and use the managed implementations when unavailable. These changes do not establish physical laptop compatibility or improve performance without measurements. Current verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs.

## Local 1.5.5 brightness update

This is a local rebuild, not an official upstream release. The brightness fix keeps transport dimming separate from frame rendering. Managed/native RGB and audio-reactive paths supply frames at full slider intensity, and the transport applies brightness once through acknowledged firmware control or a supported software fallback. Color alpha remains a separate factor and is applied once. The Rust rendering API still accepts brightness for its other callers; app callers must avoid dimming a frame before requesting the same dimming from firmware.

Keyboard service writes are serialized with stale queued requests skipped. Before WMI dispatch, a barrier drains an active service write and supersedes older queued writes. The controller prefers WMI and uses keyboard service fallback only when needed; a successful WMI write does not dispatch another keyboard service request. Two-color input maps across all four physical zones. These changes address packet scaling and ordering; they do not measure physical keyboard brightness on every model.

The previous brightness update passed 243 selected .NET and 126 pure Rust tests; those are historical results. Earlier migration verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs. The complete inherited hardware/integration suite, UI launch, and physical keyboard brightness require separate verification.

The module, export, and historical test totals below describe the native migration. They do not mean that the complete inherited hardware-accessing suite was rerun for this brightness update.

The newly migrated SMBIOS parser, selected protocol operations, and lighting rules attempt the native implementation first and retain managed fallbacks. Other subsystems have their own availability requirements; audio-reactive lighting, for example, depends on the native analyzer.

---

## 1. Status at a glance

| Metric | Value |
|---|---|
| Native Rust modules | **19** (16 earlier modules plus 3 in this migration) |
| Rust source | `predator_core/src/*.rs` — 19 modules plus `lib.rs` and `migration_exports.rs` |
| Exported C-ABI entry points | **100** (56 in `lib.rs`, 38 in `migration_exports.rs`, and 6 in the 3 new modules) |
| Historical Rust migration tests | **169** (158 unit + 11 FFI fuzz/stress), as recorded for that migration |
| Managed interop surface | `PredatorNativeCore.cs`, `PredatorNativeMigrations.cs`, `FirmwareParsingNative.cs`, `NativeAcerProtocol.cs`, and `KeyboardLighting/NativeLightingRules.cs` |
| Runtime dependencies | `realfft` 3.5, `rustfft` 6.4 — no others |

The C-ABI exports use `safe_ffi` to catch unwinding Rust panics. This does not prevent invalid-pointer faults, aborts, or all possible memory errors.

---

## 2. Modules migrated in this pass

The seven modules below were added to close the gaps identified in the migration audit.
Unlike the original batch, these are **rule and arithmetic engines** rather than hardware
transports, so their FFI surfaces are deliberately narrow and fully unit-tested.

| # | Module | Managed origin | Managed caller | What moved |
|---|---|---|---|---|
| 1 | `hook.rs` | `KeyboardHook.HookCallback` | `KeyboardHook` | The entire keystroke decision state machine: modifier tracking, injected-key rejection, function/media pass-through, Predator (`SC 0x75`) and Mode (`SC 0x76/0x77`) scancode disambiguation, the 3 s hold timeout, Predator+1-5 combos and custom mode shortcut matching with 350 ms repeat suppression. |
| 2 | `shortcuts.rs` | `ModeShortcutValidator` | `ModeShortcutValidator.IsValid` | All nine validation rules (function keys, Win chords, modifier-only keys, reserved system keys, OS-owned chords, standard Ctrl editing chords) plus duplicate-combination comparison. |
| 3 | `battery.rs` | `BatteryCalibrationEtaCalculator` | `CalculateEta` | EMA smoothing (α default 0.2), phase-change reset, sleep/clock-skew re-baselining, plausibility clamps and the full ETA calculation for both charge and discharge phases. |
| 4 | `history.rs` | `HistoryBuffer` | `HistoryBuffer` | Fixed-capacity ring buffer with oldest-first indexing, gap-aware min/max/average, latest-sample lookup and bounded snapshot export. |
| 5 | `graphproj.rs` | `HistoryGraphControl.DrawSeries` | `HistoryGraphControl` | The per-frame series-to-pixel projection: range normalisation, clamping, vertical inset and horizontal spacing for partially filled buffers. |
| 6 | `procsync.rs` | `GameSyncController` | `SafeGetExecutableName`, `SafeGetProcessName`, `SanitizeProfileValues` | Executable/process name normalisation, case-insensitive profile matching against running processes, and every game-profile field clamp. |
| 7 | `pipeguard.rs` | `SecureNamedPipeIpc` | `SendMessageWithVerificationAsync` (and the server read path) | Untrusted-input framing: CR stripping, LF→space conversion, NUL removal, trimming, UTF-8-boundary-safe truncation and message validity checks. |

### Why the hook moved but not `SetWindowsHookEx`

`SetWindowsHookEx` must stay managed: the hook procedure has thread affinity to the
installing thread, and the `LowLevelKeyboardProc` delegate must remain alive and pinned for
the hook's lifetime or Windows will call into freed memory. What *can* move is the decision
logic, which is the expensive and error-prone part. Rust is therefore called with
`(vkCode, scanCode, flags, isDown, tickCount)` and returns one of five action ids; the
managed side only dispatches the resulting callback. Every action passes the keystroke
through to `CallNextHookEx`, so Windows and the Acer Hotkey Service still receive it.

The passthrough rule is the security-critical part: Windows keys, modifiers, navigation,
control, media, browser and OEM keys are released *before* any hardware-scancode match, so
a scancode collision can never hijack an ordinary key. This is covered by a dedicated
regression test that feeds the whole passthrough set through the Mode scancode.

For the same reason the managed hook mirrors the engine's live state through
`predator_hook_state()` (bit0-3 Ctrl/Alt/Shift/Win, bit4 Predator held), so the public
modifier properties and callers that observe held-key state stay consistent.

---

## 3. Modules migrated previously

| # | Module | Managed origin | Key functionality |
|---|---|---|---|
| 1 | `curve.rs` | `Form1.InterpolateCurve`, `CurveFollower` | Piecewise linear interpolation, zero-allocation stack sort for small unsorted inputs, 3-sample rolling damping, asymmetric deadband hysteresis, dynamic safety floor. |
| 2 | `telemetry.rs` | Hardware monitor loop | `timeBeginPeriod(1)` 100 ms worker, `GetSystemTimes` CPU delta, atomic snapshot with real epoch-ms timestamps. |
| 3 | `pawnio.rs` | `PawnIoController` / `PawnIoDriver` | `\\.\PawnIO` IOCTL (`0xA1B22084` load, `0xA1B22104` exec), directional MMIO split-write hazard mitigation, MSR/MCHBAR planning. |
| 4 | `thermal.rs` | `ThermalSafetyCoordinator` | Four-stage thermal coordinator (Normal/Warm/Ceiling/Critical), EMA α = 0.35, CPU power capping, fan-floor escalation. |
| 5 | `rgb.rs` | `LightingEffects34` | 4-zone frame renderer, HSV conversion, cross-fade blending, CLR-matched `round_ties_even`. |
| 6 | `audio.rs` | `AudioMeter` | `realfft` spectrum analysis, pre-computed Hann window, log-spaced 32 Hz–16 kHz AGC bands, spectral-flux beat detection. |
| 7 | `nvapi.rs` | `PdhLoadMonitor` | `nvapi_QueryInterface` dispatch, GPU load/temp sampling, 3-strike circuit breaker with module reload. |
| 8 | `acpi.rs` | `BiosUefiPowerScanner` | Zero-copy borrowed-slice AML parser, PPCC package walker, flat-stream heuristic fallback, strict bounds checks. |
| 9 | `ec_hid.rs` | `AcerHardwareProtocols.EcHidDevice` | SetupAPI enumeration, `CreateFileW`, HID feature exchange, RAII kernel mutex ownership. |

---

## 4. Architecture and safety guarantees

### 4.1 Panic boundary isolation

Every export is wrapped in `safe_ffi`:

```rust
fn safe_ffi<F, R>(default: R, f: F) -> R
where
    F: FnOnce() -> R,
{
    match std::panic::catch_unwind(AssertUnwindSafe(f)) {
        Ok(ret) => ret,
        Err(_) => default,
    }
}
```

Global state locks (`GLOBAL_PAWNIO`, `GLOBAL_TELEMETRY`, `GLOBAL_ECHID`, `GLOBAL_HOOK`,
`GLOBAL_ETA`, `LIFECYCLE`) recover from poisoning with `.unwrap_or_else(|e| e.into_inner())`
so a panic in one thread cannot deadlock the rest of the process.

### 4.2 Out-parameters and alignment

Managed callers can hand over buffers that are not naturally aligned, so every pointer
write uses `std::ptr::write_unaligned` after an explicit null check. String-returning
exports report a negative length on overflow instead of truncating silently.

### 4.3 Fail-safe defaults

Each wrapper in `PredatorNativeMigrations.cs` returns a sentinel that means *"native core
unavailable, run the managed path"* rather than a plausible-looking zero:

- `PredatorNativeMigrations.Unavailable` (`-1`) for integer returns
- `null` for string returns
- `float.NaN` for `GraphProjectY`
- `IntPtr.Zero` for history handles

This is what keeps the fallback paths reachable and correct.

### 4.4 Directional MMIO split-write hazard

Writing package RAPL limits as two DWORDs has a hardware race: if the new PL1 exceeds the
currently active PL2, writing PL1 first briefly puts the package into an invalid
`PL1 > PL2` state that firmware may answer with an immediate clamp.

`pawnio.rs` orders the writes by direction — when `new_pl1 > old_pl2` it writes PL2 first,
otherwise PL1 first — preserves the Tau windows and clears lock bit 31. `PL2 >= PL1` is
also enforced independently in `battery.rs`-adjacent planning code.

### 4.5 DLL embedding and verified extraction

- `predator_core.dll` is embedded as an `EmbeddedResource` and bundled into the single-file
  executable.
- `PredatorNativeCore` extracts and loads only the embedded resource from a version-and-SHA-256-specific local cache; adjacent and search-path copies are ignored.
- Extraction is serialised across processes with the named mutex
  `Local\PredatorControlApp_Dll_Extract_Lock`.
- An existing extracted DLL is SHA-256-compared against the embedded stream and only
  overwritten when it has drifted.
- `NativeLibrary.SetDllImportResolver` binds every P/Invoke in the assembly, which is why
  `PredatorNativeMigrations.cs` can declare its own `DllImport`s with the same name.

---

## 5. Historical migration verification

| Check | Result |
|---|---|
| `cargo test` | Previously recorded: **169 passed, 0 failed** (158 unit + 11 FFI fuzz/stress), 0 warnings |
| `cargo build --release` | Clean; `predator_core.dll` ≈ 2.1 MB |
| `dotnet build PredatorControlApp` | **0 errors, 0 warnings** |

New Rust modules each carry their own unit tests covering the degenerate cases that matter:
non-finite and inverted inputs, empty and single-point collections, duplicate combinations,
repeat suppression, throttle windows, injected-key rejection, UTF-8 truncation boundaries,
`-1` sentinel preservation and null-pointer safety.

### Corrected documentation drift

Earlier revisions of this file claimed ten modules including `hook.rs` while only nine
existed, and credited `catch_unwind` with wrapping "21 FFI entry points". Both are now
accurate: `hook.rs` exists and is wired into the live hook, and the export count is 94.

---

## 6. Remaining opportunities

Deliberately **not** migrated in this pass, with reasons:

- **`Form1.cs` and the custom WinForms control set** — UI must stay managed; the drawing,
  layout and message loop are GDI+ bound.
- **WMI `InvokeMethod` hardware writes** — the protocol *is* WMI. Only the retry, timeout
  and cache layer around it is a candidate.
- **WASAPI COM capture** — `audio.rs` owns the DSP, but `IAudioClient` /
  `IAudioCaptureClient` interop remains managed. Moving the COM plumbing would need a
  `windows-rs` dependency and hardware-in-the-loop testing that this environment cannot
  provide.
- **`AcerProtocol` encode/decode** — touches live EC firmware; changing it without a
  physical Predator laptop to validate against is not safe.
- **`AppSettings` / `GameProfile` JSON persistence** — I/O bound, no measurable benefit.

---

## 7. Building

```bash
cd predator_core
cargo build --release
# Audit tests for hardware access before choosing a test selection.

cp target/release/predator_core.dll ../PredatorControlApp/predator_core.dll
cp target/release/predator_core.dll ../PredatorControlApp.Tests/predator_core.dll
cp target/release/predator_core.dll ../PredatorControlApp/bin/Debug/net10.0-windows/win-x64/
cp target/release/predator_core.dll ../PredatorControlApp.Tests/bin/Debug/net10.0-windows/

cd ..
dotnet build PredatorControlApp.slnx
# See CONTRIBUTING.md for the brightness regression test selection.
```

> The test project loads the DLL from its own output directory. Copy the freshly built
> binary into both `bin` folders before running the managed suite, otherwise the tests
> exercise a stale native core.

For the local 1.5.5 package, publish only after copying the freshly rebuilt DLL into the app's embedded resource location and checking the resulting executable. Only the embedded native core is loaded; rebuild it before publishing.

Migration test scope: targeted tests do not intentionally issue hardware writes. In the earlier migration, loading PredatorNativeCore could open PawnIO and start telemetry/NVAPI polling. The current build initializes those facilities on demand; tests that request hardware operations still require care. Physical hardware effects were not measured.

Local audit update: see AUDIT.md at the source root for fixes, exact verification scope, and unresolved hardware-policy findings.
