---
name: acer-predator-hardware-workflow
description: >-
  End-to-end workflow runbook for developing, debugging, testing, and packaging
  Acer Predator and Nitro hardware control applications. Covers ACPI/WMI BIOS
  interfaces, NVAPI GPU monitoring, Acer SysMonitor service IPC, lid and power state
  lifecycle, EC firmware watchdog strategies, UI thread decoupling, safe xUnit test
  design, and single-file standalone packaging.
---

# Acer Predator Hardware Control & Packaging Workflow

This skill provides the comprehensive end-to-end procedure for developing and maintaining hardware utilities for Acer Predator / Nitro laptops, including reverse-engineered WMI methods, NVAPI thermal monitoring, system power state synchronization, test safety, and deployment.

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
| **Fan Duty Speed** | `SetGamingFanSpeed` | `0x01 \| (cpuSpeed << 8)` (CPU)<br>`0x04 \| (gpuSpeed << 8)` (GPU) | Success if `(raw & 0xFF) == 0` |
| **Fan Mode** | `SetGamingFanBehavior` | `0x09 \| (mode << 16) \| (mode << 22)`<br>(`0x01` Auto, `0x02` Max, `0x03` Custom) | BIOS Fan Mode switch |
| **Power Mode** | `SetGamingMiscSetting` | `0x0B \| (mode << 8)`<br>(`0x00` Quiet, `0x01` Balanced, `0x04` Perf, `0x05` Turbo, `0x06` Eco) | BIOS ACPI power limits |
| **RGB Lighting** | `SetGamingKBBacklight`<br>`SetGamingLEDBehavior` | 16-byte mode packet (`mode, speed, bright, dir...`) | Zero brightness = turn off LEDs |
| **Battery 80% Limit** | `SetBatteryHealthControl` | `uBatteryNo=1, uFunctionMask=1, uFunctionStatus=1` | Limits charging to 80% |

---

## 2. NVIDIA NVAPI Integration & Battery Sleep Safety

Direct P/Invoke to `nvapi64.dll` provides GPU die temperatures matching MSI Afterburner and HWInfo.

### Function IDs
- `NvAPI_Initialize`: `0x0150E828`
- `NvAPI_EnumPhysicalGPUs`: `0xE5AC921F`
- `NvAPI_GPU_GetThermalSettings`: `0xE3640A56` (struct version: `0x00020044`, 68 bytes)

### D3Cold Low-Power State Preservation
- When running on battery, querying NVAPI must never wake a sleeping discrete GPU.
- Check return status from `NvAPI_GPU_GetThermalSettings`; if non-zero, invalidate handle and return fallback temperature.
- Never force GPU temp or fan speed to 0 artificially when on battery unless the sensor reading is genuinely 0 or offline.

---

## 3. UI Thread Decoupling & Responsiveness (Crucial Anti-Lag Rule)

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

## 4. Power State & Lid Event Lifecycle Management

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

## 5. Critical DOs and DONTs

### DOs
- **DO** mask fan tachometer values with `& 0x1FFF` (13 bits) to strip upper status bits (`0x2000`, `0x4000`, `0x8000`).
- **DO** apply a retry cooldown (e.g. 5 seconds) on failed WMI queries to avoid stalling when running under non-admin permissions.
- **DO** protect all WMI calls with `lock (_lock)`.
- **DO** clean up power setting notification handles in `OnFormClosing` and `Dispose(bool)`.
- **DO** restrict form-closing cancellation strictly to `e.CloseReason == CloseReason.UserClosing`.

### DONTs
- **DONT** execute WMI queries, socket connects, or named-pipe reads synchronously on the WinForms UI thread.
- **DONT** instantiate WinForms `Form` instances inside xUnit unit tests (WinForms requires STA and crashes MTA test runners).
- **DONT** write scheduled task XML to `%TEMP%` without deterministic deletion.
- **DONT** rely solely on `SystemInformation.PowerStatus`; handle `PowerLineStatus.Unknown` as Battery.

---

## 6. Build & Packaging Configuration

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
```

Embed `requireAdministrator` in `app.manifest` for required ACPI WMI access.
