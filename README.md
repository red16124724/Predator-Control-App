# 🦅 Predator Control App

<div align="center">

![Predator Control App](https://img.shields.io/badge/Predator_Control-v1.4.0-red?style=for-the-badge&logo=acer&logoColor=white)
[![Latest Release](https://img.shields.io/badge/Download-v1.4.0_Standalone-brightgreen?style=for-the-badge&logo=github)](https://github.com/red16124724/Predator-Control-App/releases)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011%20(64--bit)-blue?style=for-the-badge&logo=windows)](https://github.com/red16124724/Predator-Control-App/releases)
[![License](https://img.shields.io/badge/License-MIT-orange?style=for-the-badge)](LICENSE)
[![No Install Needed](https://img.shields.io/badge/Portable-Zero_Install_Required-purple?style=for-the-badge)](#-quick-start--easy-installation-for-beginners)
[![100% Offline](https://img.shields.io/badge/Privacy-100%25_Offline-success?style=for-the-badge)](#-100-offline-privacy-guarantee)

**A lightning-fast, ultra-lightweight, and feature-packed control center for Acer Predator gaming laptops.**  
*Get 100% direct control over your fans, power modes, screen refresh rate, keyboard lighting, battery health, and GPU MUX switch — without the bloat, battery drain, or lag of PredatorSense.*

</div>

---

## 📖 Table of Contents

- [⚡ Quick Start — Easy Installation for Beginners](#-quick-start--easy-installation-for-beginners)
- [❓ Common Questions & Troubleshooting](#-common-questions--troubleshooting)
- [✨ Key Features](#-key-features)
  - [🚀 Power & Performance Modes](#-power--performance-modes)
  - [❄️ Advanced Fan & Cooling Control](#️-advanced-fan--cooling-control)
  - [🎮 GPU & Display Control (MUX Switch)](#-gpu--display-control-mux-switch)
  - [🌈 34 Keyboard RGB Lighting Effects](#-34-keyboard-rgb-lighting-effects)
  - [🔋 Battery Care & Health](#-battery-care--health)
  - [📊 Live Hardware Telemetry](#-live-hardware-telemetry)
  - [🎨 Themes & Diagnostics](#-themes--diagnostics)
- [🛡️ 100% Offline Privacy Guarantee](#️-100-offline-privacy-guarantee)
- [🧹 How to Safely Disable PredatorSense (Optional)](#-how-to-safely-disable-predatorsense-optional)
- [💻 Building from Source](#-building-from-source)
- [⚠️ Disclaimer](#️-disclaimer)
- [📄 License](#-license)

---

## ⚡ Quick Start — Easy Installation for Beginners

You **do NOT need to install anything** or set up complicated developer tools! Just follow these 3 simple steps:

### 1️⃣ Step 1: Download the App
Head over to the **[Releases Page](https://github.com/red16124724/Predator-Control-App/releases)** and download:
- 📥 **`PredatorControlApp-Standalone.exe`** *(Recommended)* — Everything is bundled into this single file. You do not even need to install .NET!

### 2️⃣ Step 2: Run as Administrator
1. Move the downloaded file to a folder you like (for example, inside `C:\Program Files\PredatorControl` or a folder in your `Documents`).
2. **Right-click** on `PredatorControlApp-Standalone.exe` and select **"Run as administrator"**.

> 💡 **Why does it need Administrator rights?**  
> Windows strictly protects laptop hardware controls (like fan speeds, power limits, and BIOS MUX switches) so accidental programs cannot change them. Running as administrator gives the app permission to adjust your fans and power modes safely.

### 3️⃣ Step 3: You're Done!
The app will open up with your laptop's live temperatures, fan controls, and power modes.
- When you close or minimize the window, it quietly stays in your **System Tray** (the notification area down by your clock in the bottom-right corner of your screen).
- It will automatically launch quietly whenever you turn on your laptop so your cooling settings always apply.

---

## ❓ Common Questions & Troubleshooting

<details>
<summary><b>🔵 Windows shows a blue screen saying "Windows protected your PC"?</b></summary>

> This is called **Windows SmartScreen**. Microsoft shows this warning for any new or community open-source app that hasn't paid thousands of dollars for commercial publisher certificates.  
> **How to continue:**
> 1. Click **"More info"** on the blue popup.
> 2. Click the **"Run anyway"** button that appears.
</details>

<details>
<summary><b>🧩 Do I need to install .NET runtime?</b></summary>

> **No!** If you download `PredatorControlApp-Standalone.exe`, the entire runtime is built right inside the file. It runs out of the box on any 64-bit Windows 10 or Windows 11 laptop.
</details>

<details>
<summary><b>📍 I closed the window and it disappeared. Where did it go?</b></summary>

> The app does not quit when you close it — it minimizes to your **System Tray** next to the clock in the bottom-right corner of Windows. Click the little up-arrow (`^`) on your taskbar, and you will see the red Predator icon. Click it to bring the window back or right-click it for quick controls!
</details>

<details>
<summary><b>⚡ Do I have to uninstall Acer PredatorSense first?</b></summary>

> **No!** You can keep PredatorSense installed. Predator Control App works alongside it or completely replaces it. Once you are comfortable with Predator Control App, you can disable the background PredatorSense services (see [How to Disable PredatorSense](#-how-to-safely-disable-predatorsense-optional) below) to free up RAM and CPU usage.
</details>

<details>
<summary><b>🔄 How do I check for updates?</b></summary>

> Inside the app, simply click the **"Check for Updates"** button. If a new version is available on GitHub, the app will show you what's new and update smoothly. It **never** checks in the background without your permission.
</details>

---

## ✨ Key Features

### 🚀 Power & Performance Modes
- **5 Hardware Power Profiles**: Instantly switch between **Quiet / Silent**, **Balanced**, **Performance**, **Turbo**, and **Eco**.
- **Auto AC/Battery Switching**: Automatically switches to battery-saving profiles when you unplug your charger, and restores high performance when plugged back in.
- **Physical Predator Key**: Press the dedicated physical "Mode" key on your laptop's keyboard to cycle power modes, with the Mode Key LED in sync!

### ❄️ Advanced Fan & Cooling Control
- **Direct EC Hardware Access**: Communicates directly with your laptop's Embedded Controller (EC) chip via high-speed HID commands, with automatic fallback to Acer WMI.
- **3-Fan Support**: Full controls for laptops with CPU, GPU, and 3rd Chassis/System fans.
- **Acer CoolBoost Toggle**: Unlocks elevated maximum fan RPM limits for intense gaming sessions.
- **Interactive Fan Curve Editor**: Design custom multi-point fan curves with your mouse.
- **Spike Damping & Hysteresis**: Prevents irritating fan spin-up and spin-down noises caused by momentary 1-second CPU usage spikes.
- **FanLock Channel Locking**: Lock any individual fan to a specific speed independently.

### 🎮 GPU & Display Control (MUX Switch)
- **BIOS MUX Switch Toggle**: Switch between **Hybrid Mode (Nvidia Optimus)** for long battery life and **Discrete GPU Mode** for maximum gaming FPS without display bottlenecking. *(Note: Requires a normal system reboot to switch hardware displays).*
- **GPU Sleep (D3Cold) Status**: Live indicator showing when your dedicated Nvidia graphics card is in deep low-power sleep, saving valuable watts on battery.
- **Display Refresh Rate Switcher**: Instantly toggle your screen between 60 Hz (for battery preservation) and your panel's maximum (144 Hz, 165 Hz, or 240 Hz).

### 🌈 34 Keyboard RGB Lighting Effects
Upgrade from the 8 basic factory presets to **34 dynamic lighting effects**:
- *Static, Breathing, Neon, Wave, Shifting, Zoom, Meteor, Twinkling, Rainbow, Spiral, Fire, Waterfall, and 22 more!*
- Adjustable lighting speed, brightness slider, and custom 4-zone colors.
  
### 🔋 Battery Care & Health
- **Live Battery Health & Wear**: See your laptop battery's real wear percentage, designed capacity, and full charge capacity.
- **Battery Cycle Count**: Track how many charge cycles your battery has completed.
- **Hardware Battery Calibration Cycle**: Automated cycle to reset and recalibrate internal battery fuel-gauge sensors.
- **Power-Off USB Charging Toggle**: Control whether external devices can charge from your laptop's USB ports while the laptop is turned off, with a low-battery cutoff floor.

### 📊 Live Hardware Telemetry
- **Accurate CPU & GPU Temperatures**: Real-time readouts in the app window and right in the system tray.
- **Windows PDH Hardware Load**: Monitors CPU and GPU utilization using lightweight Windows performance counters.
- **Real-Time Streaming Graph**: Watch fan speeds and temperatures stream live on an interactive chart.

### 🎨 Themes & Diagnostics
- **Theme Switcher**: Switch between modern **Dark**, **Light**, or your Windows **System** theme.
- **One-Click Diagnostic Dump**: Click one button to copy all your laptop's hardware specifications, sensor states, and EC identifiers to your clipboard for easy sharing on forums or bug reports.

---

## 🛡️ 100% Offline Privacy Guarantee

Predator Control App is built with a **strict offline-first privacy architecture**:
- ❌ **Zero Telemetry**: No tracking, no analytics, no identifiers sent anywhere.
- ❌ **No Background Web Traffic**: The application never touches the internet on startup or while running in the background.
- 🔒 **Secure On-Demand Updates Only**: The app only connects to GitHub when you explicitly click the "Check for Updates" button.
- 🛡️ **SHA-256 Checksum Verification**: Every download is verified against published cryptographic hashes to protect against tampering.



<img width="2510" height="1384" alt="image" src="https://github.com/user-attachments/assets/e47765c5-f167-4fc7-b6d8-3cb5af4e5b28" />
<img width="2532" height="1412" alt="image" src="https://github.com/user-attachments/assets/bd0082ca-e92b-4caa-8a95-045da3257fcb" />
<img width="2560" height="1600" alt="image" src="https://github.com/user-attachments/assets/d165f93d-9334-4daa-83ac-c1b5e864ce9c" />
<img width="2550" height="1380" alt="image" src="https://github.com/user-attachments/assets/6cc1cf7f-d9ab-40d6-92eb-56f358c68499" />
<img width="2554" height="1414" alt="image" src="https://github.com/user-attachments/assets/d6cb8f35-f419-4e0e-a3d9-76ed99a288c3" />

---

## 🧹 How to Safely Disable PredatorSense (Optional)

Predator Control App talks directly to your laptop's built-in Windows ACPI/WMI/EC drivers. You do **not** need Acer's background services running. If you want to disable them to save RAM and CPU:

### Step 1: Disable Background Services
1. Press `Win + R` on your keyboard, type **`services.msc`**, and press **Enter**.
2. Locate the following services:
   - `Acer Gaming Service`
   - `Acer Quick Access Service`
   - `AcerService`
   - `Acer Power Button Service`
3. For each one:
   - Double-click the service.
   - Change **Startup type** to **Disabled**.
   - Click **Stop** if it is currently running.
   - Click **OK**.

### Step 2: Disable PredatorSense Startup
1. Press `Ctrl + Shift + Esc` to open **Task Manager**.
2. Click the **Startup apps** tab on the left.
3. Find **PredatorSense** in the list, right-click it, and choose **Disable**.

> 💡 *Note: You can easily reverse these steps at any time if you ever want to re-enable PredatorSense.*

---

## 💻 Building from Source

If you are a developer and want to build the application yourself:

### Requirements
- Windows 10 or 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Quick Commands
```powershell
# 1. Clone the repository
git clone https://github.com/red16124724/Predator-Control-App.git
cd Predator-Control-App

# 2. Restore dependencies
dotnet restore PredatorControlApp.slnx

# 3. Build the solution
dotnet build PredatorControlApp.slnx -c Release

# 4. Run all 271 unit tests
dotnet test PredatorControlApp.slnx

# 5. Publish the standalone single-file binary
dotnet publish PredatorControlApp/PredatorControlApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

---

## ⚠️ Disclaimer

This is a free, open-source community utility. It is not affiliated with, endorsed by, or sponsored by Acer Inc.

- Acer, Predator, PredatorSense, CoolBoost, and DustDefender are registered trademarks of Acer Inc.
- While this application has been carefully designed with failsafes, hardware controls directly adjust cooling and power states. Use responsibly.

---

## 📄 License

This project is licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.
