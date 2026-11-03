# 🦅 Predator & Nitro Control App

A lightweight, zero-bloat, native hardware control center for **Acer Predator** and **Acer Nitro** series gaming laptops. Direct control over cooling fans, power modes, CPU power limits (PL1/PL2), keyboard lighting, battery care, and display settings — without PredatorSense or NitroSense background overhead.

---

## 📖 Table of Contents

- [⚡ Quick Start — Easy Installation for Beginners](#-quick-start--easy-installation-for-beginners)
- [💻 Supported Hardware: Acer Predator & Nitro Series](#-supported-hardware-acer-predator--nitro-series)
- [❓ Common Questions & Troubleshooting](#-common-questions--troubleshooting)
- [✨ Key Features](#-key-features)
  - [🚀 Power & Performance Modes](#-power--performance-modes)
  - [❄️ Advanced Fan & Cooling Control (Auto, Max, Custom)](#️-advanced-fan--cooling-control-auto-max-custom)
  - [🌈 Keyboard RGB & Backlight Control](#-keyboard-rgb--backlight-control)
  - [⌨️ Dedicated NitroSense & Predator Key Capture](#️-dedicated-nitrosense--predator-key-capture)
  - [⚡ CPU Power Limits (PL1/PL2) via PawnIO & Native Ring-0 MMIO/MSR](#-cpu-power-limits-pl1pl2-via-pawnio--native-ring-0-mmiomsr)
  - [🎮 GPU & Display Control (MUX Switch)](#-gpu--display-control-mux-switch)
  - [🎛️ Custom Mode Shortcuts](#️-custom-mode-shortcuts)
  - [🔋 Battery Care & Health](#-battery-care--health)
  - [📊 Live Hardware Telemetry](#-live-hardware-telemetry)
  - [🎨 Themes & Diagnostics](#-themes--diagnostics)
- [Network and update behavior](#network-and-update-behavior)
- [🧹 How to Safely Disable PredatorSense & NitroSense (Optional)](#-how-to-safely-disable-predatorsense--nitrosense-optional)
- [💻 Building from Source](#-building-from-source)
- [⚠️ Disclaimer](#️-disclaimer)
- [📄 License](#-license)

---

## ⚡ Quick Start — Easy Installation for Beginners

You **do NOT need to install anything** or set up complicated developer tools! Just follow these 3 simple steps:

### 1️⃣ Step 1: Download the App
Use `PredatorControlApp.exe` from the supplied local 1.5.5 package. The **[upstream releases page](https://github.com/red16124724/Predator-Control-App/releases)** contains maintainer releases and may not include this local fix.
- **`PredatorControlApp.exe`** is a portable, self-contained Windows x64 executable. It bundles the .NET 10 runtime and native `predator_core.dll`; native components are extracted automatically when needed. No manual runtime installation or DLL extraction is required.

### 2️⃣ Step 2: Run as Administrator
1. Move `PredatorControlApp.exe` to a folder you like (for example, `C:\Program Files\PredatorControl` or a folder in your `Documents`).
2. **Right-click** on `PredatorControlApp.exe` and select **"Run as administrator"**.

> 💡 **Why does it need Administrator rights?**  
> Windows strictly protects laptop hardware controls (such as embedded controller fan registers, Intel RAPL power limits, and BIOS MUX switches) so unprivileged software cannot alter them. Running as administrator grants the permissions required to adjust cooling and power profiles safely.

### 3️⃣ Step 3: You're Done!
The app opens immediately with your laptop's live temperatures, fan controls, and power profiles.
- **Physical Predator / NitroSense Key & Caption Controls**: Instant window preemption over fullscreen 3D games with priority boost. Pressing the physical Predator key, dedicated NitroSense key (`N`), `_btnCaptionMin`, or `_btnCaptionClose` delivers immediate foreground demotion and clean system tray minimization.
- Automatically launches quietly on Windows startup if configured so your customized cooling settings always apply.

### Optional: PawnIO Driver (for CPU Power Limits)

Fan, lighting, battery, display control, and telemetry all work out of the box. Custom **PL1/PL2 CPU power limits** additionally utilize the signed **PawnIO** kernel driver to interface with Intel MSR and MMIO registers:

1. **WinGet (recommended):**

   ```powershell
   winget install -e --id namazso.PawnIO
   ```

2. **Manual:** download `PawnIO.Setup.exe` from [pawnio.eu](https://pawnio.eu) or the [PawnIO releases page](https://github.com/namazso/PawnIO/releases), run it as administrator, then restart Predator Control App.

Without PawnIO the app still runs normally: the CPU power card clearly reports that the driver is missing while every other feature keeps functioning smoothly.

---

## 💻 Supported Hardware: Acer Predator & Nitro Series

Predator Control App provides native, direct hardware support across both Acer Predator and Acer Nitro laptop lineups via ACPI, WMI, and Embedded Controller (EC) communication:

| Laptop Family | Series / Generation | Model Codes | Keyboard Lighting | Fan Configuration | Supported Hardware Controls |
|---|---|---|---|---|---|
| **Acer Nitro 5** | 2020 – 2023 (AMD) | `AN515-44`, `AN515-45`, `AN515-46`, `AN515-47`, `AN517-41`, `AN517-42` | 4-Zone RGB / Single-Zone | Dual Fan (CPU + GPU) | Power modes, Auto/Max/Custom fan curves, CoolBoost, RGB lighting, NitroSense key, CPU PL1/PL2 (where supported) |
| **Acer Nitro 5** | 2020 – 2023 (Intel) | `AN515-55`, `AN515-56`, `AN515-57`, `AN515-58`, `AN517-52`, `AN517-54`, `AN517-55` | 4-Zone RGB | Dual Fan (CPU + GPU) | Power modes, Auto/Max/Custom fan curves, CoolBoost, 4-Zone RGB, NitroSense key, CPU PL1/PL2 MSR/MMIO tuning |
| **Acer Nitro 5** | Pre-2020 / Legacy | `AN515-31`, `AN515-54` | Single-Zone / 3-Zone RGB | Dual Fan (CPU + GPU) | Power modes, Fan Auto/Max, Single/3-Zone backlight, NitroSense key |
| **Acer Nitro 14** | 2024 – 2026+ | `AN14-41` (AMD), `AN14-51` (Intel) | 4-Zone RGB (No Numpad) | Dual Fan (High-Flow) | Power modes, Auto/Max/Custom fan curves, CoolBoost, 4-Zone RGB, NitroSense key, ACPI PPCC power limits |
| **Acer Nitro 16** | 2023 – 2026+ | `AN16-41`, `AN16-42`, `AN16-43` (AMD), `AN16-51`, `AN16-71`, `AN16-72` (Intel) | 4-Zone RGB | Dual Fan (High-Flow) | Power modes, Auto/Max/Custom fan curves, CoolBoost, 4-Zone RGB, NitroSense key, CPU PL1/PL2 tuning, MUX / display refresh |
| **Acer Nitro 17** | 2023 – 2026+ | `AN17-41`, `AN17-42` (AMD), `AN17-51`, `AN17-71`, `AN17-72` (Intel) | 4-Zone RGB | Dual Fan (High-Flow) | Power modes, Auto/Max/Custom fan curves, CoolBoost, 4-Zone RGB, NitroSense key, CPU PL1/PL2 tuning, MUX / display refresh |
| **Acer Nitro V 14** | 2024 – 2026+ | `ANV14-61` (AMD) | Single-Zone Backlight | Dual Fan (CPU + GPU) | Power modes, Auto/Max/Custom fan curves, CoolBoost, Backlight brightness, NitroSense key |
| **Acer Nitro V 15** | 2023 – 2026+ | `ANV15-41` (AMD), `ANV15-51` (Intel) | Single-Zone Backlight | Dual Fan (CPU + GPU) | Power modes, Auto/Max/Custom fan curves, CoolBoost, Backlight brightness, NitroSense key, CPU PL1/PL2 (Intel) |
| **Acer Nitro V 16** | 2024 – 2026+ | `ANV16-41` (AMD), `ANV16-51` (Intel) | Single-Zone Backlight | Dual Fan (CPU + GPU) | Power modes, Auto/Max/Custom fan curves, CoolBoost, Backlight brightness, NitroSense key, CPU PL1/PL2 (Intel) |
| **Acer Predator Helios** | 2020 – 2026+ | Helios 300, Helios 16, Helios 18, Helios Neo 14/16/18 | Per-Key RGB / 4-Zone RGB | Dual / Triple Fan (3rd Chassis) | All hardware features, MUX switch, Advanced Optimus, Predator/Mode keys, PL1/PL2 tuning |
| **Acer Predator Triton** | 2020 – 2026+ | Triton 300, 300 SE, 500, 14, 16, 17 X | Per-Key RGB / 3-Zone / 4-Zone | Dual / Triple Fan (AeroBlade 3D) | All hardware features, MUX switch, Advanced Optimus, Turbo key, PL1/PL2 tuning |

---

## ❓ Common Questions & Troubleshooting

<details>
<summary><b>🔵 Windows shows a blue screen saying "Windows protected your PC"?</b></summary>

> The local executable is unsigned, so Windows may show an unknown-publisher or SmartScreen warning. This warning does not establish whether an executable is safe. Verify that you are opening the intended local package and compare its supplied SHA-256 checksum before deciding whether to run it.
</details>

<details>
<summary><b>🧩 Do I need to install .NET runtime or extract native DLLs?</b></summary>

> No manual installation is required. The executable bundles .NET 10 and its native core. The app automatically extracts its embedded native DLL to its versioned local cache when needed. Only the embedded native core is loaded, after SHA-256 verification in a hash-specific cache.
</details>

<details>
<summary><b>📍 I closed the window and it disappeared. Where did it go?</b></summary>

> The app minimizes to your **System Tray** next to the clock in the bottom-right corner of Windows. Click the up-arrow (`^`) on your taskbar to reveal the red Predator icon. Click it to restore the window, or right-click it for instant quick controls!
</details>

<details>
<summary><b>⚡ Do I have to uninstall Acer PredatorSense or NitroSense first?</b></summary>

> **No!** Predator Control App works cleanly alongside PredatorSense and NitroSense or completely replaces them. Once comfortable with Predator Control App, you can disable background PredatorSense or NitroSense services (see [How to Safely Disable PredatorSense & NitroSense](#-how-to-safely-disable-predatorsense--nitrosense-optional) below) to eliminate background CPU and RAM drain.
</details>

<details>
<summary><b>🔄 How do I check for updates?</b></summary>

> The app checks GitHub once at startup, including when launched hidden, and again when you click **"Check for Updates"** in Settings. There is no periodic polling. An upstream release can replace local changes; installation requires an asset-specific SHA-256 and keeps a `.previous.exe` backup.
</details>

---

## ✨ Key Features

### 🚀 Power & Performance Modes
- **Hardware Power Profiles**: Instantly switch between **Quiet / Eco**, **Balanced**, **Performance**, and **Turbo** modes across Predator and Nitro models.
- **Acer Protocol Parity**: Direct dispatch of Acer SMBIOS type 170 hotkey and type 172 gaming-record commands, ensuring identical firmware performance mode registration as OEM PredatorSense and NitroSense.
- **Auto AC/Battery Switching**: Automatically transitions to energy-efficient profiles when unplugged from AC power, restoring full high performance when reconnected.
- **Smooth Mode Switching**: One serialized, latest-wins hardware dispatch per switch. Windows power overlay synchronization is deferred and telemetry pauses during the settle window, completely eliminating USB/Bluetooth renegotiation stutter and peripheral dropouts.
- **Instant Caption & Minimize Controls**: Dedicated title bar caption buttons (`_btnCaptionMin`, `_btnCaptionClose`) immediately release capture, strip topmost status, and demote foreground priority to guarantee instantaneous background hiding without UI thread hanging.

### ❄️ Advanced Fan & Cooling Control (Auto, Max, Custom)
- **Direct EC Hardware Access**: Native HID transport talks directly to Embedded Controller (EC) fan registers with transparent fallback to Acer WMI.
- **Comprehensive Fan Profiles**:
  - **Auto**: Firmware-governed fan curves with intelligent hysteresis and spike damping to avoid RPM revving on short CPU bursts.
  - **Max**: Instant 100% duty cycle on both CPU and GPU fans for immediate thermal evacuation under heavy loads.
  - **Custom Multi-Point Curves**: Intuitive visual curve editor to customize temperature-to-RPM response curves for independent CPU and GPU fan channels.
- **Dual & Triple Fan Architecture Support**: Full support for standard 2-fan Nitro configurations (CPU + GPU) and modern 3-fan Predator chassis configurations (CPU + GPU + 3rd System/Chassis fan).
- **Acer CoolBoost Hardware Toggle**: Direct EC register toggle elevating maximum fan RPM ceiling beyond stock curves for peak thermal dissipation during gaming or rendering.
- **FanLock Channel Locking**: Lock any individual fan channel to a constant speed independently.
- **Multi-Stage Thermal Guard**: Native coordinator featuring Warn, Ceiling, and Critical thermal tiers per CPU/GPU with configurable thresholds, fan floors, EMA smoothing, and dynamic CPU watt capping.

### 🌈 Keyboard RGB & Backlight Control
- **Full Tier Auto-Detection**:
  - **Single-Zone Backlight**: Brightness control for monochrome backlit keyboards on Nitro V 14/15/16 and legacy Nitro 5 models.
  - **3-Zone RGB**: Distinct 3-zone color control on supported classic Nitro 5 models (e.g., AN515-54).
  - **4-Zone RGB**: Granular 4-zone palette editor on Nitro 5 (2020+), Nitro 14/16/17, and Predator Helios/Helios Neo series.
  - **Per-Key RGB**: Experimental per-key matrix mapping with verified hardware capability probing on high-end Predator Helios and Triton laptops.
- **33 Dynamic RGB Effects + Off / Static**: Upgraded lighting engine offering 33 fluid dynamic lighting effects (*Breathing, Neon, Wave, Shifting, Zoom, Meteor, Rainbow, Spiral, Fire, Waterfall, Audio Reactive, and 22 more*).
- **Explicit "Off" Mode**: Added clean, dedicated "Off" presets for both Static Per-Zone and dynamic modes to disable keyboard lighting entirely.
- **Strict Color State Separation**: Persisted per-zone static colors are strictly decoupled from transient dynamic animation colors. Booting into dynamic lighting or previewing effects never clobbers your custom saved per-zone static palette.

### ⌨️ Dedicated NitroSense & Predator Key Capture
- **Hardware Scancode Interception**: Intercepts the physical dedicated **NitroSense** key (`N`), **Predator** key, and **Mode** key via Win32 `RawInput` and low-level keyboard hooks (`predator_core` native hook engine).
- **Fullscreen Game Preemption**: Uses Win32 thread-input attachment (`AttachThreadInput`), `SetWindowPos(HWND_TOPMOST)`, `BringWindowToTop`, and `SwitchToThisWindow` routines with process priority escalation (`High` process priority class, `AboveNormal` UI thread priority) to preempt exclusive and borderless fullscreen DirectX/Vulkan game surfaces instantly when the hardware key is pressed.
- **Debounced Key Action Handling**: Eliminates key repeat chatter with hardware debouncing. Configurable to cycle power profiles or toggle the control window.

### ⚡ CPU Power Limits (PL1/PL2) via PawnIO & Native Ring-0 MMIO/MSR
- **Expanded Custom PL1 Sliders up to CPU Max PL2**: Custom PL1 sliders expand all the way up to CPU Max PL2 (`PL1 == Max PL2`), unlocking maximum continuous thermal headroom for sustained multi-threaded rendering and gaming.
- **Directional Split-Write Hazard Protection**: Ring-0 MMIO/MSR engine eliminates transient out-of-order power limits when writing 64-bit Package RAPL registers (`0xFED159A0` / `0xFED159A4`):
  ```
  NewPL1 > OldPL2 ? Write PL2 then PL1 : Write PL1 then PL2
  ```
- **Firmware-Derived & Immutable BIOS Defaults**: Automatically captures authentic OEM baseline limits per mode (`Eco`, `Quiet`, `Balanced`, `Performance`, `Turbo`) from SMBIOS, zero-copy ACPI/AML `PPCC` tables, Acer BIOS MOF WMI classes, and Intel RAPL MSRs (`0x606`, `0x614`). Factory defaults remain immutable per profile.
- **Hardware Architecture Support**: Available on Intel-based Nitro 5, Nitro 14/16/17, Nitro V, and Predator laptops. AMD-based models leverage firmware ACPI baseline detection and real-time hardware TDP/temperature telemetry.
- **Atomic RAPL Execution**: Single locked MSR `0x610` transaction preserving Tau time windows and mirroring settings to MCHBAR while verifying BIOS lock bit assertions.
- **Active Drift Re-Verification**: Periodically inspects package power limits every 2 seconds and re-asserts target wattage if firmware drifts limits by more than 1 W.
- **One-Click Reset & Dedicated Re-Scan**: Instant restoration of OEM per-mode defaults with a dedicated, fully responsive **🔍 Re-Scan** button that re-evaluates silicon capabilities and firmware tables on demand.

### 🎮 GPU & Display Control (MUX Switch)
- **BIOS MUX Switch Toggle**: Easily toggle between **Hybrid (Nvidia Optimus)** for maximum battery life and **Discrete GPU** for uncompromised display performance with zero Optimus overhead *(supported on MUX-equipped Predator and Nitro models, requires system reboot)*.
- **GPU Sleep (D3Cold) Status**: Live UI indicator displaying when your dedicated Nvidia graphics card enters deep power-saving sleep.
- **Display Refresh Rate Switcher**: Instantly toggle display panels between 60 Hz (for battery preservation) and maximum factory refresh rates (144 Hz, 165 Hz, or 240 Hz).

### 🎛️ Custom Mode Shortcuts
- **Integrated Settings Card 5**: Embedded directly into the Settings tab (Card 5) with zero secondary dialog windows required.
- **Per-Profile Hotkey Binding**: Assign custom system-wide key combinations to switch between **Eco**, **Quiet**, **Balanced**, **Performance**, and **Turbo** modes.
- **Workflow Controls**:
  - **Record**: Click to capture any key sequence with real-time modifier tracking (Ctrl, Alt, Shift, Win).
  - **Clear**: Remove active shortcut binding for a specific profile.
  - **Reset Defaults**: Restore standard factory shortcut configurations in one click.
  - **Save & Apply**: Immediately persist hotkey registrations to active low-level hook listeners.

### 🔋 Battery Care & Health
- **Live Battery Health & Wear**: Real-time readouts of designed battery capacity, current full charge capacity, and degradation wear percentage.
- **Battery Cycle Counter**: Monitor total accumulated hardware charge cycles.
- **Hardware Battery Calibration Cycle**: Automated conditioning workflow with wake-lock protection and real-time EMA discharge ETA computation to recalibrate battery fuel-gauge sensors.
- **Power-Off USB Charging Toggle**: Control external USB charging while your laptop is powered off, complete with a low-battery safety cutoff threshold.

### 📊 Live Hardware Telemetry
- **Accurate Real-Time Telemetry**: Real-time CPU & GPU temperatures and fan tachometer speeds displayed in-app and in the system notification tray.
- **Windows PDH Hardware Load**: Low-overhead hardware utilization tracking leveraging native Windows Performance Data Helper counters.
- **Real-Time Streaming Graph**: Live charting of thermal histories and fan RPM curves.

### 🎨 Themes & Diagnostics
- **Theme Switcher**: Instant switching between modern **Dark**, **Light**, or Windows **System** themes with high-DPI font scaling.
- **One-Click Diagnostic Dump**: Generate comprehensive hardware reports containing SMBIOS platform IDs, ACPI tables, and sensor registers to your clipboard for quick troubleshooting.

---

## Network and update behavior

Hardware controls do not require internet access. The app checks GitHub once at startup, including when launched hidden, and when requested through Settings. It does not perform periodic update polling or analytics.

The updater restricts download URLs to HTTPS and permitted GitHub hosts. It requires and verifies an asset-specific SHA-256 before installing an executable. A checksum is an integrity comparison, not a publisher signature. The local build remains unsigned.

---

## 🧹 How to Safely Disable PredatorSense & NitroSense (Optional)

Predator Control App communicates directly with your laptop's hardware via ACPI, WMI, and EC transports. Acer background services are not required and can be disabled to reclaim system resources:

### Step 1: Disable Background Services
1. Press `Win + R`, type **`services.msc`**, and press **Enter**.
2. Locate and disable the following services (if present on your system):
   - `Acer Gaming Service`
   - `Acer Quick Access Service`
   - `AcerService`
   - `Acer Power Button Service`
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

## 💻 Building from Source

### Requirements
- Windows 10 or 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Rust Toolchain (stable-x86_64-pc-windows-msvc)](https://www.rust-lang.org/) *(optional, required for rebuilding `predator_core`)*

### Quick Commands
```powershell
# 1. Clone repository
git clone https://github.com/red16124724/Predator-Control-App.git
cd Predator-Control-App

# 2. Build and test native Rust core (optional)
cd predator_core
cargo test --release
cargo build --release
Copy-Item target/release/predator_core.dll ../PredatorControlApp/predator_core.dll
cd ..

# 3. Restore .NET dependencies
dotnet restore PredatorControlApp.slnx

# 4. Run only tests audited for no intentional hardware writes
# See CONTRIBUTING.md before selecting tests from the inherited suite.

# 5. Publish standalone single-file binary (54.4 MB)
dotnet publish PredatorControlApp/PredatorControlApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

---

## ⚠️ Disclaimer

This is an independent open-source utility and is not affiliated with, endorsed by, or sponsored by Acer Inc.
- Acer, Predator, PredatorSense, Nitro, NitroSense, Nitro V, CoolBoost, and DustDefender are trademarks or registered trademarks of Acer Inc.
- Hardware controls directly adjust fan speeds and power states. Use responsibly.

---

## 📄 License

This project is licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.
