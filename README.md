# 🦅 Predator Control App

> **A high-performance, lightweight, zero-bloat hardware control center for Acer Predator and Acer Nitro gaming laptops.**  
> Direct control over thermal fans, power profiles, CPU PL1/PL2 power limits, keyboard lighting, battery care, and GPU display modes — without OEM background service overhead.

<div align="center">

[![Release v1.5.5](https://img.shields.io/badge/Release-v1.5.5%20Verified-blue?style=for-the-badge&logo=github)](https://github.com/red16124724/Predator-Control-App/releases/tag/v1.5.5)
[![Build Status](https://img.shields.io/badge/Build-Passing-brightgreen?style=for-the-badge&logo=githubactions&logoColor=white)](https://github.com/red16124724/Predator-Control-App/actions)
[![Tech Stack](https://img.shields.io/badge/Stack-.NET%2010%20%7C%20Rust%20Core-orange?style=for-the-badge&logo=dotnet)](https://github.com/red16124724/Predator-Control-App)
[![Tests Passing](https://img.shields.io/badge/Tests-2%2C460%20Passing%20(100%25)-success?style=for-the-badge&logo=checkmarx)](https://github.com/red16124724/Predator-Control-App)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow?style=for-the-badge)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-informational?style=for-the-badge&logo=windows)](https://github.com/red16124724/Predator-Control-App)

[Download v1.5.5 Portable](https://github.com/red16124724/Predator-Control-App/releases/latest) • [Supported Hardware](#-supported-hardware-matrix) • [Architecture](#-visual-architecture) • [CLI & Shortcuts](#-keyboard-shortcuts--command-line-arguments) • [Quick Start](#-quick-start)

</div>

---

## ⚡ Why Predator Control App?

Stock OEM utilities (**PredatorSense** & **NitroSense**) consume up to **500 MB+ RAM**, run 6+ persistent background telemetry services, lock fan curves to rigid presets, and introduce input latency during gaming sessions.

**Predator Control App** replaces OEM bloat with a standalone single binary:
- **Instant Response (< 1ms)**: Zero Electron, zero webviews — pure native WinForms on .NET 10 with a high-throughput Rust kernel interop core (`predator_core.dll`).
- **Zero Background Drain**: Minimal memory footprint (~18-28 MB private working set) and near-zero idle CPU usage (< 0.1%).
- **Dedicated Embedded Display, BIOS & Game Sync Menus**: Dedicated full-sized pages for Display Settings (NVIDIA Advanced Optimus DDS, 60Hz/MaxHz modes, LCD Overdrive), BIOS & Firmware Settings (dynamic hardware architecture badge querying Win32_BIOS/WMI with zero hardcoding, two-tier safety gates), and Game Sync (event-driven foreground game profiles).
- **Helios Neo & AI Lighting Stability**: Eliminates periodic RGB keyboard blinking on Helios Neo 16/16s/18 and 16 AI series by suppressing redundant EC/WMI reassertions.
- **Deep Ring-0 Hardware Access**: Direct Embedded Controller (EC) fan registers, ACPI PPCC table parsing, and Intel MCHBAR MMIO / MSR `0x610` power limit management via signed PawnIO driver.

---

---

## 📸 Application Preview & Interface

<div align="center">

<img width="95%" alt="Predator Control App - Main Telemetry, Fan Tachometers and Power Profiles" src="https://github.com/user-attachments/assets/9b314410-fec7-4ceb-af88-52d6e8d0823e" />

<br/><br/>

<img width="95%" alt="Predator Control App - Advanced Fan Curves, Hysteresis and CoolBoost" src="https://github.com/user-attachments/assets/fc7a9cea-63e5-4ee6-b2d1-7419d344c0fe" />

<br/><br/>

<img width="95%" alt="Predator Control App - 4-Zone and Per-Key RGB Lighting Studio with Reboot Persistence" src="https://github.com/user-attachments/assets/69df58b8-ca2e-42c3-9640-29fe1a7a6067" />

<br/><br/>

<img width="95%" alt="Predator Control App - CPU Power Limits PL1/PL2 and PawnIO Ring-0 Tuning" src="https://github.com/user-attachments/assets/f64809f2-a455-44d7-9517-b854bb4bc6ed" />

<br/><br/>

<img width="95%" alt="Predator Control App - Battery Care Diagnostics and Calibration Wizard" src="https://github.com/user-attachments/assets/534930c6-3f12-4006-82a0-7ee3d3ae1602" />

</div>

---

## 🎯 Core Features Grid

| Feature | Description | Architecture / Implementation |
|---|---|---|
| **⚡ Ultra-Low Latency Telemetry** | Real-time monitoring of CPU/GPU temperatures, fan RPM tachometers, and package wattage. | Native Windows PDH counters + Direct EC polling at sub-100ms intervals with rolling history graphs. |
| **🌈 4-Zone & Per-Key RGB Lighting** | Full keyboard lighting control with guaranteed reboot persistence, 33 dynamic lighting animations, and isolated lid logo behavior. | Granular per-zone palette management, synchronized `KeyboardBrightnessLevel` & `RgbBrightness` with immediate registry flush on slider commit (preventing 100% reversion across reboots/restarts), and complete detachment of lid logo RGB (`SetGamingLEDColor` zone `0x01` and OpenRGB device 0 eliminated; OEM default preserved). |
| **❄️ Intelligent Fan Curves & Hysteresis** | Smooth temperature-to-RPM curves, dual/triple fan support, and Acer CoolBoost hardware ceiling toggle. | Multi-point curve follower with EMA spike-damping to eliminate fan revving during transient CPU spikes. |
| **🔋 CPU PL1/PL2 Power Limits** | Dynamic sustained (PL1) and burst (PL2) wattage tuning with BIOS default rollback protection. | Signed PawnIO Ring-0 MMIO/MSR driver interop with directional split-write hazard protection and MCHBAR sync. |
| **🩺 Battery Calibration Wizard** | Integrated battery health diagnostics, wear percentage tracking, and automated recalibration. | Hardware fuel-gauge recalibration workflow with automated wake-lock protection and real-time EMA discharge ETA calculation. |
| **🖥️ Display & GPU Switching (DDS)** | NVIDIA Advanced Optimus mode switching (Auto, Optimus, dGPU), high-precision refresh rates (30-360Hz+), BlueLight Shield, and LCD Overdrive. | Native Rust gamma-ramp calculation, NvAPI DDS interop, and Win32 display mode switching. |
| **⚙️ Acer BIOS & Firmware GUI** | Graphical configuration for firmware settings across Predator & Nitro series with two-tier hardware safety gates. | Three-tier access: Mode 1 ACPI-WMI, Mode 2 Direct ACPI, and Mode 3 UEFI NVRAM variable writes with password zeroization. |
| **🎮 Event-Driven Game Profiles** | Automatic process-aware performance, thermal, and display mode switching on game launch and window focus. | Native Rust ring-buffer event queue, 300ms foreground debouncing, and automated default profile restoration. |
| **⌨️ Custom Shortcuts & Key Interception** | Dedicated Predator/NitroSense key capture and customizable system-wide mode hotkeys. | Low-level Win32 `RawInput` hooks with fullscreen game preemption (`AttachThreadInput` + `SetWindowPos TOPMOST`). |

> [!NOTE]
> **Keyboard Brightness & Exterior Lighting Architecture:**
> - **Guaranteed Brightness Persistence**: Keyboard brightness level (`KeyboardBrightnessLevel` strictly synchronized with `RgbBrightness`) is immediately committed and flushed to the Windows Registry upon slider release. This guarantees saved brightness levels persist across system reboots, Windows Fast Startup, power transitions (sleep/resume), and application restarts without reverting to 100%.
> - **Complete Lid Logo Detachment**: The laptop lid logo RGB is fully decoupled from the application by eliminating `SetGamingLEDColor` zone `0x01` logo payloads and OpenRGB device 0 triggers. The lid logo remains entirely under OEM hardware/BIOS defaults and is never overridden or clobbered.

---

## 💻 Supported Hardware Matrix

Predator Control App provides verified native hardware interop across Acer Predator and Acer Nitro laptop lineups:

| Laptop Family | Series / Generation | Model Identifiers | Keyboard Lighting | Fan Configuration | Supported Hardware Controls |
|---|---|---|---|---|---|
| **Predator Helios** | Helios 300 / 500 / 700 | `PH315`, `PH317`, `PH517`, `PH717` | 4-Zone RGB / Per-Key RGB | Dual / Triple Fan (AeroBlade 3D) | Power Profiles, Auto/Max/Custom Curves, CoolBoost, PL1/PL2 Tuning, Turbo Key |
| **Predator Helios** | Helios 16 / 18 / Neo | `PH16-71/72`, `PH18-71/72`, `PHN14`, `PHN16`, `PHN18` | Per-Key RGB / 4-Zone RGB | Triple Fan (Chassis System Fan) | Dynamic Power Modes, Advanced Optimus MUX Switch, PL1/PL2 MCHBAR, Refresh Switching |
| **Predator Triton** | Triton 300 / 500 / 700 | `PT314`, `PT315`, `PT515`, `PT516`, `PT715` | 3-Zone / 4-Zone / Per-Key | Dual / Triple Fan (Vortex Flow) | Turbo Key, MUX Switch, Custom Fan Curves, CPU RAPL Limits, Battery Care |
| **Predator Triton** | Triton 14 / 16 / 17 X | `PT14-51`, `PT16-51`, `PTX17-71` | Per-Key RGB | Triple AeroBlade 3D Fan System | Full Hardware Interop, Display Overdrive, PL1/PL2 Sliders, Low-D3Cold Tracking |
| **Acer Nitro 5** | 2020 – 2023 (AMD) | `AN515-44/45/46/47`, `AN517-41/42` | 4-Zone RGB / Single-Zone Red | Dual Fan (CPU + GPU) | Auto/Max/Custom Curves, CoolBoost, 4-Zone RGB, NitroSense Key, Battery Threshold |
| **Acer Nitro 5** | 2020 – 2023 (Intel) | `AN515-55/56/57/58`, `AN517-52/54/55` | 4-Zone RGB | Dual Fan (CPU + GPU) | Full Power Modes, Fan Curves, CoolBoost, 4-Zone RGB, Intel PL1/PL2 MSR Tuning |
| **Acer Nitro 14 / 16 / 17** | 2023 – 2026+ (AMD/Intel) | `AN14-41/51`, `AN16-41/42/51/71`, `AN17-41/51/71` | 4-Zone RGB (No Numpad on 14) | Dual High-Flow Fans | Power Modes, NitroSense Key, CoolBoost, 4-Zone RGB, Display Refresh, ACPI PPCC Tuning |
| **Acer Nitro V Series** | Nitro V 14 / 15 / 16 | `ANV14-61`, `ANV15-41/51`, `ANV16-41/51` | Single-Zone Monochrome Backlight | Dual Fan System | Power Profiles, Backlight Brightness, Fan Curves, NitroSense Key, Intel PL1/PL2 (on Intel) |

---

## ⚡ Quick Start

### 1. Download & Run
Predator Control App is distributed as a **portable, standalone single-file binary** with zero external dependencies.

1. Download **`PredatorControlApp.exe`** from the [Latest Release](https://github.com/red16124724/Predator-Control-App/releases/latest).
2. Place the executable in a directory of your choice (e.g., `C:\Tools\PredatorControl\`).
3. **Right-click** `PredatorControlApp.exe` and select **"Run as administrator"**.

> [!IMPORTANT]
> **Why Administrator Rights Are Required:**  
> Direct communication with Embedded Controller fan registers, ACPI thermal zones, and Intel CPU power limits requires elevated Windows security permissions.

### 2. Optional: PawnIO Driver (For CPU Power Limits)
Basic fan curves, dynamic lighting, battery care, and telemetry work out of the box. To unlock custom **PL1/PL2 CPU power sliders**:

```powershell
# Install signed PawnIO driver via WinGet
winget install -e --id namazso.PawnIO
```
*Or download `PawnIO.Setup.exe` manually from [pawnio.eu](https://pawnio.eu).*

---

### Global System Shortcuts

Change laptop performance states instantly from within any fullscreen game:

| Shortcut | Action | Target Profile Code |
|---|---|---|
| `Ctrl + Alt + E` | Switch to **Eco Mode** | `0x06` (Teal) |
| `Ctrl + Alt + Q` | Switch to **Quiet Mode** | `0x00` (Blue) |
| `Ctrl + Alt + B` | Switch to **Balanced Mode** | `0x01` (Green) |
| `Ctrl + Alt + P` | Switch to **Performance Mode** | `0x04` (Orange) |
| `Ctrl + Alt + T` | Switch to **Turbo Mode** (Instant Max Fans) | `0x05` (Red) |
| **Physical Predator Key** | Preempt fullscreen surface and toggle control window | Hardware Scancode Hook |
| **NitroSense Key (`N`)** | Preempt fullscreen surface and toggle control window | RawInput Keyboard Hook |

*All global hotkeys can be customized or rebound in **Settings → Custom Mode Shortcuts**.*

---

## 🧹 How to Safely Disable PredatorSense & NitroSense (Optional)

Predator Control App communicates directly with your laptop's hardware via ACPI, WMI, and EC transports. Acer background services are not required and can be disabled to reclaim system resources:

### Step 1: Disable Background Services
1. Press `Win + R`, type **`services.msc`**, and press **Enter**.
2. Locate and disable the following services (if present on your system):
   - `Acer Gaming Service`
   - `AcerService`
   - `NitroSense Service` (on Nitro laptops)
   - `PredatorSense Service` (on Predator laptops)
3. For each service:
   - Double-click the entry.
   - Set **Startup type** to **Disabled**.
   - Click **Stop** if running.
   - Click **OK**.

### Step 2: Disable OEM Startup Applications
1. Press `Ctrl + Shift + Esc` to open **Task Manager**.
2. Navigate to the **Startup apps** tab.
3. Locate **PredatorSense** or **NitroSense**, right-click, and select **Disable**.

---

## 🛠️ Building From Source

### Prerequisites
- Windows 10 or 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Rust Toolchain (msvc-x86_64)](https://www.rust-lang.org/) *(for rebuilding `predator_core`)*

### Build Commands
```powershell
# 1. Clone repository
git clone https://github.com/red16124724/Predator-Control-App.git
cd Predator-Control-App

# 2. Compile native Rust core (optional)
cd predator_core
cargo test --release
cargo build --release
Copy-Item target/release/predator_core.dll ../PredatorControlApp/predator_core.dll
cd ..

# 3. Publish standalone single-file binary
dotnet publish PredatorControlApp/PredatorControlApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

---

## 📄 License & Disclaimer

- **License**: Released under the terms of the [MIT License](LICENSE).
- **Disclaimer**: This is an independent open-source project. Not affiliated with, endorsed by, or sponsored by Acer Inc. Acer, Predator, PredatorSense, Nitro, and NitroSense are registered trademarks of Acer Inc.
