# predator_core

Current local 1.5.5: native loading uses the verified embedded DLL and a hash-specific cache. Telemetry and PawnIO are initialized on demand; actual performance and hardware behavior require measurement.

High-performance native Rust core library for `PredatorControlApp`.

This package accompanies the local 1.5.5 brightness update, not an official upstream release. The previous brightness update passed 243 selected .NET and 126 pure Rust tests; those are historical results. Earlier migration verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 285 Rust tests passed (100% across 6 suites) across 15 filtered module runs. The complete inherited hardware/integration suite, UI launch, and physical keyboard brightness require separate verification.

Designed as a high-efficiency native hot-path engine and hardware abstraction layer for Acer Predator and Nitro gaming laptops, replacing latency-sensitive managed C# code with zero-allocation, thread-safe, and panic-isolated Rust implementations.

## Added parsing and rule modules

`smbios.rs` parses Acer type 170 hotkey and type 172 gaming/version structures. `protocol_migration.rs` encodes and decodes selected Acer hardware protocol fields without performing device I/O. `lighting_rules.rs` selects lighting tiers, parses RGB/ARGB hex, blends colors, and quantizes service brightness and speed. Managed discovery, model matching, and the relevant reference fallbacks remain in C#. Existing device I/O paths are unchanged, including the Rust EC-HID transport. Current verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 285 Rust tests passed (100% across 6 suites) across 15 filtered module runs.

## Architecture & Native Modules

The source has 19 subsystem modules, including `smbios.rs`, `protocol_migration.rs`, and `lighting_rules.rs`. The main hardware/rendering modules are listed below; `RUST-MIGRATION.md` describes all migrated modules and managed fallbacks.

1. **`curve.rs` (Fan Curve Damping & Interpolation)**
   - Piecewise linear interpolation over temperature-to-speed control points.
   - Zero-allocation 3-sample rolling damping buffer (`RollingDampingBuffer`).
   - Deadband hysteresis follower with asymmetric tracking (instant reaction to rising temperatures, delayed hold on cooling).
   - Dynamic safety floor enforcement from thermal coordinators.

2. **`hook.rs` (Low-Level Keyboard Hook Pre-Filter)**
   - Native decision engine for the managed `WH_KEYBOARD_LL` callback. Hook registration and delegate lifetime stay managed.
   - Modifier tracking, injected-key rejection, passthrough, Predator/Mode scancode decisions, held-key timeouts, and shortcut matching.
   - Ordinary keys are passed through before hardware-scancode matches so a scancode collision cannot consume them.

3. **`telemetry.rs` (High-Resolution Background Telemetry Worker)**
   - Multimedia timer loop using `timeBeginPeriod(1)` and 100ms default update intervals.
   - Low-overhead CPU load delta tracking via kernel `GetSystemTimes`.
   - Real epoch millisecond timestamp generation for hardware synchronization.
   - Dynamic NVAPI runtime loader for GPU thermals and utilization without hard dependencies.

4. **`pawnio.rs` (PawnIO Ring-0 IOCTL Engine)**
   - Direct kernel communication with `\\.\PawnIO`.
   - Bytecode module loading (`IOCTL 0xA1B22084`) and function execution (`IOCTL 0xA1B22104`).
   - **Directional MMIO split writes**: Writes PL2 first when `new_PL1 > old_PL2`, eliminating transient illegal hardware states where `PL1 > PL2`.
   - Inter-process synchronization via named Win32 mutexes (`Global\Access_MSR`, `Global\Access_PCI`).
   - Preserves CPU Tau timing windows and falls back gracefully to 64-bit atomic writes.

5. **`thermal.rs` (Multi-Stage Thermal Safety Coordinator)**
   - Exponential Moving Average (EMA) thermal filter with $\alpha = 0.35$.
   - 4-stage proactive throttling coordinator (Normal, Warm, Ceiling, Critical).
   - Proportional CPU power limit capping and automatic fan floor escalation under thermal duress.

6. **`rgb.rs` (RGB Animation Math & Frame Generator)**
   - Migrated from `LightingEffects34.cs` software animation loops.
   - Byte-identical port of animation math (travelling, hue-cycle, pulse, and shift styles) including the CLR's banker's rounding on color casts (`round_ties_even`).
   - Single C-ABI call renders an entire 4-zone frame into a caller-owned buffer: zero per-tick allocations, zero LINQ, zero boxing.
   - Includes frame pacing (`frame_delay_ms`) and linear cross-fade (`blend_zones`).

7. **`audio.rs` (Real-Time Spectrum Analyzer)**
   - Replaces managed FFT meters with a high-performance `realfft`/`rustfft` transform utilizing runtime-dispatched AVX/SSE kernels.
   - Pre-computed Hann window, log-spaced 32 Hz..16 kHz bands, and decaying per-band AGC for immediate lighting intensity mapping.
   - Spectral-flux transient (beat) detection with adaptive thresholding and cooldown.
   - `push_interleaved` mixes stereo directly into the internal ring buffer: zero heap allocations on the audio capture thread.

8. **`nvapi.rs` (NVAPI Bridge)**
   - Replaces dynamic P/Invoke and delegate marshaling with direct `LoadLibraryW` + `nvapi_QueryInterface` function pointer dispatch.
   - `GPU_GetUsages` (v2, 136-byte struct) and `GPU_GetThermalSettings` (v2, 68-byte struct) called directly on stack structs.
   - Built-in circuit breaker: re-enumerates handle tables and reloads the driver module after 3 consecutive failures to recover from sleep/wake cycles.
   - Validates thermal readings (controller $\le 2$, $0 < \text{temp} \le 150\,^\circ\text{C}$) to reject corrupted ABI buffers.

9. **`acpi.rs` (Zero-Copy ACPI / AML Parser)**
   - Migrated from `BiosUefiPowerScanner.cs`.
   - Borrowed `&[u8]` parsing of `RSMB` firmware blobs with per-table checksum and length validation.
   - Full `PPCC` AML package walker with structured parsing and flat-stream heuristic fallback.
   - Strict slice bounds checking on every offset: invalid or adversarial firmware blobs safely fail without panics.

10. **`ec_hid.rs` (Native EC-HID Transport)**
    - Migrated from `EcHidDevice`.
    - SetupAPI interface enumeration, `CreateFileW`, capability-driven report lengths, and `HidD_SetFeature`/`HidD_GetFeature` exchange in a single call.
    - `hid.dll` dynamically resolved at runtime via `GetProcAddress`.
    - Device handles and the `Global\ECHIDFeatureVID_...` kernel mutex are governed by RAII, guaranteeing resource release on all paths.

The remaining modules are `shortcuts.rs`, `battery.rs`, `history.rs`, `graphproj.rs`, `procsync.rs`, and `pipeguard.rs` for shortcut rules, battery ETA, telemetry storage, graph projection, process matching, and pipe framing.

## Brightness contract

`predator_rgb_render_frame` and `predator_audio_render_zones` still accept a brightness value as part of their API. The app's animation callers request full slider intensity from these renderers, then let `WmiController` apply slider brightness once. When firmware brightness is acknowledged, transport RGB values remain unscaled; supported software fallbacks scale colors only when firmware brightness is unavailable. Do not pre-dim a frame and also send the same dimming level to firmware.

Effect animation, color alpha, and audio response can legitimately change individual frame intensity. A 100% slider means no additional slider attenuation, not a guarantee that every frame or selected color has identical physical light output. Regression tests inspect frame/packet values and service ordering; target-laptop brightness still requires a physical comparison.

## FFI Hardening & Safety Guarantees

- **Panic Barrier**: Every exported C-ABI function is wrapped in `safe_ffi`, executing within `std::panic::catch_unwind`. No Rust panics ever cross the FFI boundary into the CLR host.
- **Mutex Poison Recovery**: Internal global locks implement automatic poison recovery (`.unwrap_or_else(|e| e.into_inner())`), preventing cascading application lockups.
- **Unaligned Memory Safety**: Raw pointer writes utilize `std::ptr::write_unaligned` with strict null pointer validation.
- **Directional MMIO Split-Write Ordering**: Mitigates hardware race conditions when writing dual 32-bit RAPL registers:
  - If `new_PL1 > old_PL2`: Write PL2 upper DWORD (0xFED159A4) **FIRST**, then write PL1 lower DWORD (0xFED159A0) **SECOND**.
  - Else: Write PL1 lower DWORD (0xFED159A0) **FIRST**, then write PL2 upper DWORD (0xFED159A4) **SECOND**.

## Single-File Portable Deployment

- `predator_core.dll` is embedded directly into `PredatorControlApp` as an embedded resource (`<EmbeddedResource Include="predator_core.dll" />`).
- At runtime, `PredatorNativeCore` verifies the extracted DLL in `%LocalAppData%\PredatorControlApp\native\v1.5.5\<SHA-256>\predator_core.dll` against the embedded stream using SHA-256.
- Extraction is synchronized across concurrent instances using a named Win32 mutex (`Local\PredatorControlApp_Dll_Extract_Lock`).
- Dynamic linking is wired automatically via `NativeLibrary.SetDllImportResolver`.

## Verification scope

The previous brightness update passed 243 selected .NET and 126 pure Rust tests; those are historical results. Earlier migration verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 285 Rust tests passed (100% across 6 suites) across 15 filtered module runs. The complete inherited hardware/integration suite, UI launch, and physical keyboard brightness require separate verification.

Earlier documents reported 85 native tests and 1,835 managed tests; the later migration reported 169 native tests and 94 exports. Those are historical results. Use the current validation record and exact test selection rather than assuming the complete suite was rerun. Some inherited tests can initialize hardware interfaces. See `../CONTRIBUTING.md` for brightness regression checks and native DLL copy requirements.

## C-ABI FFI Exports

- `predator_core_init() -> i32`
- `predator_core_cleanup()`
- `predator_pawnio_is_available() -> i32`
- `predator_pawnio_read_msr(msr: u32, out_val: *mut u64) -> i32`
- `predator_pawnio_write_msr(msr: u32, val: u64) -> i32`
- `predator_pawnio_read_mmio32(addr: u64, out_val: *mut u32) -> i32`
- `predator_pawnio_write_mmio32(addr: u64, val: u32) -> i32`
- `predator_pawnio_apply_power_limits(target_pl1: u32, target_pl2: u32, out_mmio_mirrored: *mut i32) -> i32`
- `predator_telemetry_get_snapshot(out_snapshot: *mut NativeTelemetrySnapshot) -> i32`
- `predator_telemetry_set_interval(ms: u32) -> i32`
- `predator_hook_install() -> i32`
- `predator_hook_uninstall()`
- `predator_hook_is_predator_pressed() -> i32`
- `predator_hook_get_and_reset_predator_press_count() -> u32`
- `predator_hook_get_and_reset_mode_press_count() -> u32`
- `predator_curve_create(hysteresis: i32) -> *mut FanCurveFollower`
- `predator_curve_update(handle: *mut FanCurveFollower, temp: f64, points_ptr: *const ControlPoint, point_count: usize, safety_floor: i32) -> i32`
- `predator_curve_interpolate(points_ptr: *const ControlPoint, point_count: usize, temp: i32) -> i32`
- `predator_curve_reset(handle: *mut FanCurveFollower)`
- `predator_curve_destroy(handle: *mut FanCurveFollower)`
- `predator_thermal_evaluate(cpu_temp: f64, gpu_temp: f64, target_pl1: i32, min_power: i32, base_tdp: i32, out_decision: *mut NativeThermalDecision) -> i32`

### RGB Animation
- `predator_rgb_create(effect, r, g, b, brightness, speed, direction, zones) -> *mut RgbAnimator`
- `predator_rgb_next_frame(handle, out: *mut u8, zones) -> i32`
- `predator_rgb_render_frame(effect, step, r, g, b, brightness, out: *mut u8, zones) -> i32`
- `predator_rgb_frame_delay_ms(speed) -> u32`
- `predator_rgb_delay_ms(handle) -> u32`
- `predator_rgb_set_brightness(handle, brightness)`
- `predator_rgb_reset(handle)` / `predator_rgb_destroy(handle)`
- `predator_rgb_hsv_to_rgb(h, s, v, out: *mut u8) -> i32`
- `predator_rgb_blend(current: *mut u8, target: *const u8, zones, t: f32) -> i32`
- `predator_rgb_apply_brightness(zones: *mut u8, count, brightness) -> i32`

### Audio Spectrum
- `predator_audio_create(sample_rate, fft_size, band_count) -> *mut SpectrumAnalyzer`
- `predator_audio_push(handle, samples: *const f32, count, channels) -> i32`
- `predator_audio_get_bands(handle, out: *mut f32, cap) -> i32`
- `predator_audio_get_level(handle) -> f32`
- `predator_audio_is_beat(handle) -> i32` / `predator_audio_beat_strength(handle) -> f32`
- `predator_audio_render_zones(handle, r, g, b, brightness, out: *mut u8, zones) -> i32`
- `predator_audio_reset(handle)` / `predator_audio_destroy(handle)`

### NVAPI Bridge
- `predator_nvapi_is_available() -> i32`
- `predator_nvapi_get_gpu_load(out_load: *mut f64) -> i32`
- `predator_nvapi_get_gpu_temperature(out_temp: *mut f64) -> i32`
- `predator_nvapi_get_gpu_count() -> u32`
- `predator_nvapi_shutdown()`

### ACPI / AML Parser
- `predator_acpi_ppcc_entries(aml: *const u8, len, out: *mut PpccEntry, cap) -> i32`
- `predator_acpi_ppcc_summary(aml: *const u8, len, out_summary: *mut PpccSummary) -> i32`
- `predator_acpi_scan_tables(blob: *const u8, len, out: *mut AcpiTable, cap) -> i32`

### EC-HID Transport
- `predator_echid_open(vendor_id, product_id, usage_page, usage) -> i32`
- `predator_echid_close()` / `predator_echid_is_open() -> i32`
- `predator_echid_report_length() -> i32`
- `predator_echid_exchange(request: *const u8, request_len, out: *mut u8, out_cap) -> i32`
- `predator_echid_enumerate(limit) -> i32`


### Display & GPU Switching (display.rs)
- predator_display_get_supported_refresh_rates(out_rates, cap, out_count) -> i32
- predator_display_calculate_gamma_ramp(level, out_red, out_green, out_blue) -> i32
- predator_display_dds_switch_mode(mode) -> i32
- predator_display_get_dds_status(out_supported, out_current, out_can_switch) -> i32

### Acer BIOS & Firmware Settings GUI (ios.rs)
- predator_bios_probe_settings(out_mode, out_count) -> i32
- predator_bios_get_setting_info(index, out_name, name_max, out_val, val_max, out_risk, out_kind) -> i32
- predator_bios_apply_setting(name, value, password, allow_dangerous) -> i32
- predator_bios_restore_defaults(password, user_defaults) -> i32

### Event-Driven Game Profiles (game_profile.rs)
- predator_game_profile_register(id, exe_name, power_mode, fan_mode, rgb_mode, dds_mode, priority) -> i32
- predator_game_profile_poll_events(out_events, cap, out_count) -> i32
- predator_game_profile_clear() / predator_game_profile_shutdown()

## Building

```bash
cargo build --release
# Audit hardware access before choosing tests. See ../CONTRIBUTING.md.
```
The compiled DLL is output to `target/release/predator_core.dll`.


## Single-File Portable Integration

predator_core.dll is integrated as an embedded resource (<EmbeddedResource Include="predator_core.dll" />) inside PredatorControlApp.exe. When the standalone single-file executable runs:
The embedded DLL is extracted into a hash-specific local cache, verified against the embedded bytes, and loaded while protected from modification.

No manual DLL sidecar is required. Adjacent and library-search-path copies are ignored; publish with the fresh embedded core.

Migration test scope: targeted tests do not intentionally issue hardware writes. Loading PredatorNativeCore no longer starts telemetry or opens PawnIO; tests that request telemetry or hardware operations may still access hardware. Tests were not fully hardware-isolated. Physical hardware effects were not measured.

Local audit update: see AUDIT.md at the source root for fixes, exact verification scope, and unresolved hardware-policy findings.
