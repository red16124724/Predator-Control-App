---
name: acer-predator-hardware-workflow
description: >-
  End-to-end workflow runbook for developing, debugging, testing, and packaging
  Acer Predator and Nitro hardware control applications. Covers ACPI/WMI BIOS
  interfaces, NVAPI GPU monitoring, native Rust predator_core integration,
  directional MMIO split-write ordering, FFI hardening, lid and power state
  lifecycle, EC firmware watchdog strategies, UI thread decoupling, safe xUnit test
  design, and single-file portable embedding.
---

# Acer Predator Hardware Control & Packaging Workflow

Current local 1.5.5: verify embedded-only native loading, on-demand initialization, one startup update check, manual checks and checksum-required installation and privacy-aware compatibility reporting. The executable is unsigned until signed by a trusted publisher.

This skill provides the comprehensive end-to-end procedure for developing and maintaining hardware utilities for Acer Predator / Nitro laptops, including reverse-engineered WMI methods, native Rust hardware acceleration (`predator_core`), NVAPI thermal monitoring, system power state synchronization, test safety, and deployment.

This copy documents the local 1.5.5 brightness update, not an official upstream release. The previous brightness update passed 243 selected .NET and 126 pure Rust tests; those are historical results. Earlier migration verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs. The complete inherited hardware/integration suite, UI launch, and physical keyboard brightness require separate verification.

---

## Rust migration scope for local 1.5.5

SMBIOS type 170/172 parsing, selected protocol packet rules, lighting tier arbitration, hex parsing, color blending, and service-level quantization are native-first. Preserve C# reference fallbacks. Existing WMI/HID transport and hardware-write paths remain unchanged; EC-HID transport, ACPI PPCC and RGB frame processing were already partly or fully native. Validate native/managed parity with synthetic malformed and boundary inputs and retain the brightness regressions. Do not claim hardware validation without actual device testing. Current verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs.

---

## 1. ACPI & WMI Hardware Architecture

Acer gaming laptops route thermal, fan, lighting, and power limits through ACPI methods exposed via WMI (`root\WMI`).

### Core WMI Classes & Methods
- **Namespace**: `root\WMI`
- **Primary Class**: `AcerGamingFunction` (or `Acer_Gaming_Bios_Interface`)
- **Battery Class**: `BatteryControl`

### Hardware Register & Command Cheat Sheet

| Operation | WMI Method | Input Format (`gmInput`) | Output / Decoding |
| :--- | :--- | :--- | :--- |
| **CPU Temperature** | `GetGamingSysInfo` | `0x0101` (Sensor ID `0x01`) | `(raw >> 8) & 0xFFFF` (°C) |
| **GPU Temperature** | `GetGamingSysInfo` | `0x0A01` (Sensor ID `0x0A`) | `(raw >> 8) & 0xFFFF` (°C) |
| **CPU Fan RPM** | `GetGamingSysInfo` | `0x0201` (Sensor ID `0x02`) | `MaskTachometerRpm(raw)`: `raw & 0x1FFF` (13-bit mask) |
| **GPU Fan RPM** | `GetGamingSysInfo` | `0x0601` (Sensor ID `0x06`) | `MaskTachometerRpm(raw)`: `raw & 0x1FFF` (13-bit mask) |
| **Fan RPM (Direct)** | `GetGamingFanSpeed` | `0x01` (CPU), `0x04` (GPU) | `raw & 0x1FFF` (Method 17) |
| **Fan Duty Speed** | `SetGamingFanSpeed` | `0x01 | (cpuSpeed << 8)` (CPU)<br>`0x04 | (gpuSpeed << 8)` (GPU) | Success if `(raw & 0xFF) == 0` |
| **Fan Mode** | `SetGamingFanBehavior` | `0x09 | (mode << 16) | (mode << 22)`<br>(`0x01` Auto, `0x02` Max, `0x03` Custom) | BIOS Fan Mode switch |
| **Power Mode** | `SetGamingMiscSetting` | `0x0B | (mode << 8)`<br>(`0x00` Quiet, `0x01` Balanced, `0x04` Perf, `0x05` Turbo, `0x06` Eco) | BIOS ACPI power limits |
| **RGB Lighting** | `SetGamingKBBacklight`<br>`SetGamingLEDBehavior` | 16-byte mode packet (`mode, speed, bright, dir...`) | Zero brightness = turn off LEDs |
| **Battery 80% Limit** | `SetBatteryHealthControl` | `uBatteryNo=1, uFunctionMask=1, uFunctionStatus=1` | Limits charging to 80% |

### Brightness ownership and regression checks

- Keep one slider-brightness owner. After an acknowledged firmware brightness command, transmit raw RGB color values; use supported software scaling only when firmware brightness is unavailable. A failed command is not proof that firmware accepted the requested level.
- Render managed/native RGB and audio-reactive frames at full slider intensity before transport dimming. Apply color alpha once and retain intentional effect/audio modulation.
- Serialize keyboard service writes and skip older queued requests. Before WMI dispatch, drain an active service write and supersede older queued writes. Prefer WMI, use service fallback only when needed, and do not dispatch a keyboard service write after successful WMI control. An earlier dimming task must not overwrite a later 100% request.
- Check 0%, intermediate levels, and 100%; static, native/managed, and audio frames; transparent colors; two-color mapping across four physical zones; and rapid low-to-high requests.
- Use hardware-disabled controllers or fake transports for packet tests. Record physical keyboard comparisons separately, including laptop model, BIOS, power state, effect, restart/wake behavior, and firmware limits. Packet correctness alone is not a brightness measurement.

---

## 2. Native Rust Acceleration (`predator_core`) & FFI Hardening

High-throughput hardware polling, signal processing, and low-level kernel operations execute natively within `predator_core.dll`.

### All Native Rust Subsystems
1. **`curve.rs`**: Piecewise linear interpolation, 3-sample rolling damping buffer (`RollingDampingBuffer`), asymmetric deadband hysteresis, and dynamic thermal safety floors.
2. **`hook.rs`**: Non-destructive `WH_KEYBOARD_LL` filter with atomic tracking for Predator (`0x00FF`) and Mode (`0x00FE`) keys.
3. **`telemetry.rs`**: Low-overhead multimedia timer worker (`timeBeginPeriod(1)`, 100ms interval) with kernel CPU load delta and epoch ms timestamping.
4. **`pawnio.rs`**: Ring-0 IOCTL driver bridge (`\\.\PawnIO`) with directional MMIO split-write ordering and named mutex synchronization (`Global\Access_MSR`, `Global\Access_PCI`).
5. **`thermal.rs`**: 4-stage proactive throttling coordinator (Normal, Warm, Ceiling, Critical) with EMA filtering ($\alpha = 0.35$).
6. **`rgb.rs`**: Zero-allocation 4-zone animation generator with CLR banker's rounding (`round_ties_even`).
7. **`audio.rs`**: Real-time AVX/SSE FFT spectrum analyzer with pre-computed Hann window, log-spaced AGC bands, and spectral-flux beat detection.
8. **`nvapi.rs`**: Direct function pointer dispatch for GPU thermal and usage polling with 3-strike circuit breaker and automatic driver reload.
9. **`acpi.rs`**: Zero-copy borrowed slice AML/PPCC parser with strict slice bounds checks.
10. **`ec_hid.rs`**: Native SetupAPI + HID feature transport with RAII mutex ownership.

### Directional MMIO Split-Write Hazard Mitigation
Writing dual 32-bit registers for CPU Package RAPL (PL1 at `MCHBAR + 0x59A0`, PL2 at `MCHBAR + 0x59A4`) presents a hardware race hazard:
- If `new_PL1 > old_PL2`, write PL2 upper DWORD (0xFED159A4) **FIRST**, then PL1 lower DWORD (0xFED159A0) **SECOND**.
- Otherwise (`new_PL1 <= old_PL2`), write PL1 lower DWORD (0xFED159A0) **FIRST**, then PL2 upper DWORD (0xFED159A4) **SECOND**.
This prevents transient illegal hardware states where `PL1 > PL2`. Tau time windows are preserved, lock bit 31 is cleared, and an atomic 64-bit MMIO write is attempted as fallback.

### FFI Boundary Isolation
- Every C-ABI export is enclosed in `safe_ffi` using `std::panic::catch_unwind`. No Rust panic ever unwinds across the FFI boundary into the CLR host.
- Mutexes implement poison recovery via `.unwrap_or_else(|e| e.into_inner())`.
- Unaligned memory writes use `std::ptr::write_unaligned` with strict null checks.

---

## 3. NVIDIA NVAPI Integration & Battery Sleep Safety

Direct P/Invoke to `nvapi64.dll` (or native dispatch via `nvapi.rs`) provides GPU die temperatures matching MSI Afterburner and HWInfo.

### Function IDs
- `NvAPI_Initialize`: `0x0150E828`
- `NvAPI_EnumPhysicalGPUs`: `0xE5AC921F`
- `NvAPI_GPU_GetThermalSettings`: `0xE3640A56` (struct version: `0x00020044`, 68 bytes)

### D3Cold Low-Power State Preservation
- When running on battery, querying NVAPI must never wake a sleeping discrete GPU.
- Check return status from `NvAPI_GPU_GetThermalSettings`; if non-zero, invalidate handle and return fallback temperature.
- Never force GPU temp or fan speed to 0 artificially when on battery unless the sensor reading is genuinely 0 or offline.

---

## 4. UI Thread Decoupling & Responsiveness (Crucial Anti-Lag Rule)

Hardware bus calls (ACPI/WMI, NVAPI, and TCP/pipe IPC) can take 5ms to 150ms depending on firmware state. **Never call them synchronously on the UI thread.**

### Asynchronous Telemetry
- Run polling loops in a background worker via `Task.Run` protected by `Interlocked.CompareExchange(ref _isRunning, 1, 0)`.
- Marshal UI label/slider updates back to the UI thread using `BeginInvoke`.
- Never block the UI timer tick: if a background poll is already active, return immediately.

### Asynchronous Commands
- Dispatch all fan speed, brightness, power mode, and RGB updates via `Task.Run`.
- Coalesce simultaneous updates (e.g. CPU + GPU fan curve speed changes) into a single task to prevent lock contention on `WmiController._lock`.

### Independent Sensor Caching
- Do **NOT** share a single `_lastReadTime` timestamp across multiple sensors.
- Maintain separate timestamps (`_lastCpuTempTime`, `_lastGpuTempTime`, `_lastCpuRpmTime`, `_lastGpuRpmTime`) to prevent the first sensor read from starving the remaining three.

---

## 5. Power State & Lid Event Lifecycle Management

### Win32 Notification Registration
Register for Win32 power notifications in `WndProc`:
- `GUID_LIDSWITCH_STATE_CHANGE` (`BA3E0F4D-B817-4094-A2D1-D56379E6A0F3`)
- `GUID_ACDC_POWER_SOURCE` (`5D3E9A59-E9D5-4B00-A6BD-FF34FF516548`)
- `GUID_BATTERY_PERCENTAGE_REMAINING` (`A7AD8041-B45A-4CAE-87A3-EECBB468A9E1`)

### Suppressing Embedded Controller (EC) Firmware Overrides
Acer EC firmware independently re-enables keyboard backlights upon physical lid open or wake events.
- **Multi-Delay Pulse Watchdog**: Trigger an asynchronous delayed pulse queue (150ms, 400ms, 800ms, 1500ms, 2500ms, 4000ms) on lid open/wake to repeatedly assert brightness 0 / off.
- **Cancellation**: Cancel and dispose previous watchdog tokens whenever a new event triggers.

---

## 6. Testing scope and quality gates

The previous brightness update passed 243 selected .NET and 126 pure Rust tests; those are historical results. Earlier migration verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs. The complete inherited hardware/integration suite, UI launch, and physical keyboard brightness require separate verification.

Audit tests before execution. Some inherited tests can initialize live hardware or drivers; run the relevant hardware-disabled regressions and pure tests rather than assuming a full-suite command is harmless. Report exact commands, results, and unrun checks. Physical hardware validation remains distinct from mocked packet assertions.

The native source has 19 subsystem modules. The three additional modules export their C-ABI functions alongside the existing interop surface. Earlier 85-native/1,835-managed test and 51-interop counts were historical; do not reuse them as current verification. See the root `CONTRIBUTING.md` and `RUST-MIGRATION.md` for build and test context.

---

## 7. Build, Single-File Embedding & Packaging Configuration

`predator_core.dll` is embedded directly into the executable as an embedded resource and verified via SHA-256 upon extraction:

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows</TargetFramework>
  <Nullable>enable</Nullable>
  <UseWindowsForms>true</UseWindowsForms>
  <ApplicationManifest>app.manifest</ApplicationManifest>
  <PublishSingleFile>true</PublishSingleFile>
  <SelfContained>true</SelfContained>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
</PropertyGroup>

<ItemGroup>
  <EmbeddedResource Include="predator_core.dll" />
  <EmbeddedResource Include="Pawn\*.bin" />
</ItemGroup>
```

Embed `requireAdministrator` in `app.manifest` for required ACPI WMI access and PawnIO driver communication.

The local 1.5.5 build uses `%LocalAppData%\PredatorControlApp\native\v1.5.5\<SHA-256>\predator_core.dll`. Rebuild and copy the native core into the app's embedded-resource location and test outputs before publishing. Only the verified embedded DLL loads; exclude stale sidecars and build caches from the distributable. The local executable is unsigned; package SHA-256 checksums provide file-integrity comparisons, not publisher signatures.

Hardware operation works without internet access. The updater checks GitHub once at startup, including hidden launches, and on request through Settings. It does not poll periodically and requires an asset-specific SHA-256 before executable installation.

Migration test scope: targeted tests do not intentionally issue hardware writes. In the earlier migration, loading PredatorNativeCore could open PawnIO and start telemetry/NVAPI polling. The current build initializes those facilities on demand; tests that request hardware operations still require care. Physical hardware effects were not measured.

Local audit update: see AUDIT.md at the source root for fixes, exact verification scope, and unresolved hardware-policy findings.
