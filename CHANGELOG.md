# Changelog

All notable changes to **Predator Control App** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.4.0] - 2026-10-01

### 🚀 Summary
A massive release transforming Predator Control App into the ultimate lightweight, high-performance control center for Acer Predator gaming laptops. Brings 26+ advanced hardware features, direct EC HID hardware communication, MUX switch control, an interactive custom fan curve graph, 34 keyboard RGB lighting modes, hardware battery calibration, offline update security, and zero-dependency standalone distribution.

### 🌟 Added
- **Direct Embedded Controller (EC) HID Protocol**: Communicates directly with laptop EC hardware for instantaneous response with seamless, transparent fallback to Acer WMI.
- **MUX Switch / GPU Working Mode**: Directly connected to Acer BIOS. Effortlessly toggle between **Hybrid (Nvidia Optimus)** for maximum battery life and **Discrete GPU** for uncompromised gaming FPS (requires standard reboot to apply).
- **GPU Sleep / D3Cold Awareness**: Instant visual indicator in the UI showing when the dedicated Nvidia GPU is in low-power deep sleep (D3Cold).
- **3rd Fan / System Fan Control**: Full support and speed monitoring for modern Predator laptops equipped with 3 fans (CPU, GPU, and System/Chassis).
- **Acer DustDefender**: Integrated automated reverse spin fan cycle to clear accumulated dust and debris from cooling fins.
- **Acer CoolBoost Hardware Toggle**: Direct hardware toggle to unlock elevated fan ceiling curves during intense workloads.
- **Factory OEM Fan Lookup Tables**: Accurately mapped Acer factory fan lookup tables for native hardware RPM-to-percentage scaling.
- **Interactive Graphical Fan Curve Editor**: Full custom visual fan curve editor with support for multi-point curves.
- **Curve Hysteresis & Spike Damping**: Eliminates annoying rapid fan throttling on momentary CPU spikes using smoothing and hysteresis.
- **Sensor Failsafe & Periodic Reassertion**: Guardrails to guarantee fans reassert failsafe cooling if background software hangs or crashes.
- **Independent Fan Channel Locking (FanLock)**: Lock CPU, GPU, or System fans to specific speeds independently.
- **Windows PDH Hardware Load Tracking**: Ultra-lightweight CPU & GPU utilization monitoring directly via Windows Performance Data Helper counters.
- **Real-Time Streaming Telemetry History Graphs**: Live streaming graph rendering temperature and RPM trends smoothly.
- **Comprehensive Battery Telemetry**: Real-time readouts of battery health %, wear percentage, cycle count, charge rate, and power status.
- **Hardware Battery Calibration Cycle**: Automated cycle to discharge, reset, and recalibrate internal battery fuel-gauge sensors.
- **Power-Off USB Charging & Low-Battery Floor**: Toggle external device charging via USB ports while the laptop is turned off, with a low-battery protection floor.
- **Expanded Keyboard RGB Engine (34 Effects)**: Upgraded from 8 to 34 rich dynamic lighting effects (Static, Breathing, Neon, Wave, Shifting, Zoom, Meteor, Twinkling, Rainbow, Spiral, Fire, Waterfall, and more) with fine-grained color, speed, and brightness controls.
- **Physical Predator Mode Key Interception**: Non-blocking Raw Input interception for the dedicated physical hardware "Mode" key on Predator keyboards, including synchronized Mode Key LED indicator.
- **Backlight Auto-Off Timeout**: Configurable inactivity timer to automatically switch off keyboard backlight and preserve battery.
- **Comprehensive Hardware Identity & SMBIOS Probing**: Full DMI/SMBIOS hardware detection for exact motherboard model, BIOS version, EC version, and CPU/GPU identities.
- **User Hardware Capability Overrides**: Ability to override probed hardware capabilities via local configuration if needed.
- **System / Light / Dark Themes**: Clean, modern UI theme switching with native high-DPI scaling and DarkScrollPanel.
- **One-Click Diagnostic Dump**: Instantly copies all hardware IDs, sensor values, power modes, and fan status to the clipboard for troubleshooting.
- **Secure Named Pipe IPC & Anti-Spoofing**: Secure single-instance communication preventing spoofing and unauthorized process injection.
- **Zero-Dependency Standalone Build**: Single-file executable (`PredatorControlApp-Standalone.exe`) that runs immediately on any Windows 10/11 x64 system without needing .NET installed.

### 🔒 Security & Networking
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
