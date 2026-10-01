# Contributing to Predator Control App

Thank you for your interest in making Predator Control App better! We welcome bug reports, hardware testing results, and pull requests from everyone.

---

## 🛠️ How to Build the Project

### Prerequisites
1. **Windows 10 or Windows 11 (64-bit)**
2. **.NET 10 SDK** (Download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0))
3. **Visual Studio 2022 / 2026** or **Visual Studio Code** with C# Dev Kit.

### Building
Open a PowerShell or Terminal window in the repository folder:

```powershell
# Restore dependencies
dotnet restore PredatorControlApp.slnx

# Build the solution
dotnet build PredatorControlApp.slnx

# Run the full test suite
dotnet test PredatorControlApp.slnx
```

### Publishing the Standalone Single-File Binary
To produce the zero-dependency, self-contained executable:

```powershell
dotnet publish PredatorControlApp/PredatorControlApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
The output file will be generated in `PredatorControlApp/bin/Release/net10.0-windows/win-x64/publish/PredatorControlApp.exe`.

---

## 🧭 Core Principles & Contribution Rules

1. **100% Offline & Zero Telemetry**:
   - The app must **never** connect to the internet on startup, in the background, or via background timers.
   - The only permitted network request is when the user explicitly clicks the "Check for Updates" button.
   - No tracking, analytics, or telemetry pingbacks are permitted.

2. **Ultra-Lightweight & Battery-Friendly**:
   - Polling loops must be lazy and light on CPU cycles.
   - Hardware interfaces (EC HID, WMI, ACPI) must handle errors gracefully without crashing the UI.

3. **Beginner-Friendly Experience**:
   - Error messages, logs, and interfaces should be simple, clear, and informative.
   - Diagnostic dumps must be safe (no private user tokens or passwords).

4. **Testing**:
   - Always run `dotnet test` before submitting a PR.
   - Add new tests in `PredatorControlApp.Tests` whenever introducing new logic or hardware parsing.

---

## 💬 Submitting Pull Requests

1. Fork the repository and create your feature branch: `git checkout -b feature/cool-hardware-feature`.
2. Commit your changes with clear, concise messages.
3. Push to your branch and open a Pull Request using the PR template.
4. Mention which Acer laptop model (if any) you tested your changes on.
