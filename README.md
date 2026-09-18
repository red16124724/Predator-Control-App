# Predator Control

A lightweight, open-source replacement for PredatorSense on Acer Predator laptops. Built with C# and WinForms, it sits in your system tray and gives you direct control over your laptop's performance, fans, display and keyboard RGB — without the bloat.

---

## Features

-  **Power Modes** — Quiet, Balanced, Performance, Turbo, Eco (auto-switches with power state)
-  **Fan Control** — Auto, Max, Custom(Separate on battery and charging)
-  **Display Refresh Rate** — Toggle between 60 Hz and your panel's max Hz
-  **Keyboard RGB** — 8 lighting modes (Static, Breathing, Neon, Wave, Shifting, Zoom, Meteor, Twinkling) with brightness and speed control
-  **Live CPU/GPU temperatures** in the title bar
-  **System tray** — full control without opening the window
-  **Remembers your settings** across reboots via the registry
-  **Runs on startup** automatically
-  **Launches with Predator Key** =>
-   Predator key + 1 = eco
-   Predator key + 2 = quiet
-   Predator key + 3 = balanced
-   Predator key + 4 = performance
-   Predator key + 5 = turbo

---

## Requirements

- Acer Predator laptop
- Windows 10 or 11
- **.NET 10 Runtime** — download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0)
- Must be run as **Administrator**

> **Note:** It may or may not work on yours. Check the disclaimer at the bottom.

---

## Download

Go to the [Releases](../../releases) page and download the latest `PredatorControlApp.exe`. Right-click → **Run as administrator**.

---

## Building from Source

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Clone this repo
3. Open `PredatorControlApp.slnx` in Visual Studio 2022+, or run:
   ```
   dotnet build
   ```
4. The output is in `PredatorControlApp/bin/Debug/net10.0-windows/`

---

## Replacing PredatorSense — Full Setup Guide

### Step 1 — Disable PredatorSense Services

Open **Services** (`Win + R` → type `services.msc` → Enter) and set the following services to **Disabled**:

| Service Name | What it does |
| `Acer Gaming Service` | PredatorSense background daemon |
| `Acer Quick Access Service` | Hotkey management (Fn keys) |
| `Acer Power Button Service` | Hardware button handling |

For each service:
1. Double-click it
2. Set **Startup type** to `Disabled`
3. Click **Stop** if it's running
4. Click **OK**
   
### Step 2 — Disable PredatorSense from Startup

1. Press `Ctrl + Shift + Esc` to open Task Manager
2. Click the **Startup apps** tab
3. Find **PredatorSense** and any other Acer apps
4. Right-click → **Disable**

### Step 3 — Remove PredatorSense from Startup Registry (optional, thorough)

1. Press `Win + R`, type `regedit`, press Enter
2. Navigate to:
   ```
   HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
   ```
3. Delete any entries related to `PredatorSense`, `AcerGaming`, or `Acer`
4. Also check:
   ```
   HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
   ```

### Step 4 — Make Predator Control Run on Startup Instead

This app registers itself on startup automatically the first time it runs. It launches with the `-hidden` flag so it starts directly in the system tray without showing the window.

To verify it's registered:
1. Open `regedit`
2. Navigate to:
   ```
   HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
   ```
3. You should see a `PredatorControl` entry pointing to the app's `.exe`

If it's not there, just run the app once as Administrator and it will self-register.

### Step 5 — Uninstall PredatorSense (optional)

If you want to fully remove it:
1. Open **Settings** → **Apps** → **Installed apps**
2. Search for **PredatorSense**
3. Click the three dots → **Uninstall**

>  Do this **after** confirming Predator Control works correctly for you. Keep PredatorSense installed as a fallback until you're happy.

---
## Disclaimer

>  **This app was built with the assistance of AI tools.**
> Compatibility with other Predator models, Windows versions, or hardware configurations is not guaranteed.
> **Use at your own risk. Any issues, damage, or unexpected behavior that occurs as a result of using this app are solely your responsibility.** The author provides no warranty, support, or guarantee of any kind.
> If something breaks — reflash your BIOS, reinstall PredatorSense, or restore from a backup. That's on you.

---
<img width="886" height="1491" alt="Screenshot_20260913233005" src="https://github.com/user-attachments/assets/a74e22c1-a136-4f2a-9ae9-44043a9182fa" />
<img width="900" height="1017" alt="Screenshot_20260913233020" src="https://github.com/user-attachments/assets/03dc8a34-24a4-4669-9edc-94eec57509bb" />
<img width="443" height="539" alt="Screenshot_20260913231346" src="https://github.com/user-attachments/assets/434220df-25be-4937-bcdd-7ffbf34c398d" />
<img width="433" height="257" alt="Screenshot_20260913233037" src="https://github.com/user-attachments/assets/6fe39280-a438-46dc-90bc-5c91e71b786c" />
<img width="407" height="232" alt="Screenshot_20260913233048" src="https://github.com/user-attachments/assets/8edd1ae9-00b4-4573-9712-6b6a79f1ded5" />
<img width="1582" height="174" alt="Screenshot_20260913233137" src="https://github.com/user-attachments/assets/ed90d484-7a6a-49c4-96da-e0a5e10b529d" />

---
##  Strictly Not for Sale

This is a **free, open-source utility** created for the community. 
* **It is strictly prohibited to sell this software**, bundle it with commercial products, or use it for any commercial sales/marketing.
* If you paid for this software, you have been scammed. Please report the seller and demand a refund.
* Unauthorized commercial distribution or sale of this software will lead to immediate take-down requests and legal consequences.

---
## License

[MIT](LICENSE) — do whatever you want with it.
