# Contributing to Predator Control App

For the current 1.5.8 improvements, rebuild the Rust DLL before the managed app. The full managed suite passes 2,217 tests (0 failed, 0 skipped) and the native suite passes 342 tests (2 hardware-only tests ignored). Audit hardware tests before running them. Test the update flow, native loading and diagnostics on suitable hardware.

Thank you for your interest in making Predator Control App better! We welcome bug reports, hardware testing results, and pull requests from everyone.

This source package is the local 1.5.8 update, not an official upstream release. The previous brightness update passed 243 selected .NET and 126 pure Rust tests; those are historical results. Current migration verification: 2,217 managed tests pass (0 failed, 0 skipped); the Release build has zero warnings and errors; 342 Rust tests pass (2 hardware-only tests ignored). The complete inherited hardware/integration suite, UI launch, and physical keyboard brightness require separate verification.

---

## 🛠️ How to Build the Project

### Prerequisites
1. **Windows 10 or Windows 11 (64-bit)**
2. **.NET 10 SDK** (Download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0))
3. **Rust Toolchain (2021 Edition)** (Install via [rustup.rs](https://rustup.rs))
4. **Visual Studio 2022 / 2026** or **Visual Studio Code** with C# Dev Kit.

### Building
Open a PowerShell or Terminal window in the repository folder:

```powershell
# Build the native Rust core first
cd predator_core
cargo build --release
Copy-Item target\release\predator_core.dll ..\PredatorControlApp\predator_core.dll -Force
Copy-Item target\release\predator_core.dll ..\PredatorControlApp.Tests\predator_core.dll -Force
cd ..

# Restore C# dependencies
dotnet restore PredatorControlApp.slnx

# Build the solution
dotnet build PredatorControlApp.slnx

# If `cargo` under %USERPROFILE%\.cargo\bin reports a missing toolchain
# (dangling rustup shim), use the toolchain binary directly:
#   %USERPROFILE%\.rustup\toolchains\stable-x86_64-pc-windows-gnu\bin\cargo.exe
# with that directory on PATH and RUSTC pointing at its rustc.exe.

# Verify output copies before running tests. A stale native DLL can silently
# exercise the previous implementation. Adjust paths for Release builds.
Copy-Item predator_core\target\release\predator_core.dll PredatorControlApp\bin\Debug\net10.0-windows\win-x64\ -Force
Copy-Item predator_core\target\release\predator_core.dll PredatorControlApp.Tests\bin\Debug\net10.0-windows\ -Force

# Run the brightness regression regressions.
dotnet test PredatorControlApp.Tests/PredatorControlApp.Tests.csproj --no-build --filter "FullyQualifiedName~BrightnessRegressionTests|FullyQualifiedName~AnimationBrightnessRegressionTests|FullyQualifiedName~LightingServiceOrderingTests"
```

For the repeatable Release build and full audited selection used for this local package, run `./Verify-Brightness.ps1` from the source root. The script rebuilds/copies the native core, verifies output hashes, builds .NET, runs the selected managed tests, and checks 12 pure Rust modules. It does not launch the application or measure keyboard light output.

Audit other tests before running them. Some inherited .NET and Rust tests initialize live hardware interfaces or drivers. A test name or a passing result alone does not prove hardware isolation. Use mocked controllers and transports for brightness regression tests, and run hardware tests only in a deliberate session on a supported laptop.

### Publishing the Standalone Single-File Binary
To produce the zero-dependency, self-contained executable with embedded native Rust core:

```powershell
dotnet publish PredatorControlApp/PredatorControlApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
The output file will be generated in `publish/PredatorControlApp.exe`. Embed the freshly rebuilt native core, and verify the published executable's version and SHA-256. Do not distribute stale native sidecars, build caches, or an older executable. Native extraction uses a version-and-hash-specific local cache; adjacent DLLs are not loaded.

---

## 🧭 Core Principles & Contribution Rules

1. **Network behavior and privacy**:
   - The app checks GitHub once at startup, including hidden launches, and when requested through Settings. It does not poll periodically.
   - No tracking, analytics, or telemetry pingbacks are permitted. Hardware control must work without internet access.
   - Updater installation requires an asset-specific SHA-256; it is not publisher-signature verification.

2. **Ultra-Lightweight & Battery-Friendly**:
   - Polling loops must be lazy and light on CPU cycles.
   - Hardware interfaces (EC HID, WMI, ACPI) must handle errors gracefully without crashing the UI.

3. **Beginner-Friendly Experience**:
   - Error messages, logs, and interfaces should be simple, clear, and informative.
   - Diagnostic dumps must be safe (no private user tokens or passwords).

4. **Testing**:
   - Run the relevant audited test selection before submitting a PR, and report the exact command and results.
   - Add new tests in `PredatorControlApp.Tests` whenever introducing new logic or hardware parsing. Mode-switch power-limit regressions live in `ModeSwitchPowerLimitEnforcementTests.cs` and EC/WMI cache-retry regressions in `WmiModeCacheRetryTests.cs`; extend them when touching those paths.
   - Keep one brightness owner: acknowledged firmware brightness plus raw RGB, or supported software scaling when firmware control is unavailable. Never multiply a frame by slider brightness and then send the same brightness to firmware.
   - Cover 0%, an intermediate value, and 100%; color alpha; static, managed/native, and audio-reactive frames; service ordering; and two/four-zone mappings. Distinguish packet assertions from physical keyboard measurements.

---

## 💬 Submitting Pull Requests

1. Fork the repository and create your feature branch: `git checkout -b feature/cool-hardware-feature`.
2. Commit your changes with clear, concise messages.
3. Push to your branch and open a Pull Request using the PR template.
4. Mention which Acer laptop model (if any) you tested your changes on.

## Rust migration regression checks

For changes to firmware parsing, packet encoding, or lighting rules, compare native output with independent managed reference behavior using synthetic inputs. Run the `FirmwareParsingMigrationTests`, protocol migration parity tests, and `LightingRulesRustParityTests`, along with the existing brightness regression suite. Run only audited Rust module tests on a live machine; inherited hardware integration tests can access real devices. Do not mistake packet parity for a physical hardware test. Current verification: 2,217 managed tests pass (0 failed, 0 skipped); the Release build has zero warnings and errors; 342 Rust tests pass (2 hardware-only tests ignored).

Migration test scope: targeted tests do not intentionally issue hardware writes. In the earlier migration, loading PredatorNativeCore could open PawnIO and start telemetry/NVAPI polling. The current build initializes those facilities on demand; tests that request hardware operations still require care. Physical hardware effects were not measured.

Local audit update: see AUDIT.md at the source root for fixes, exact verification scope, and unresolved hardware-policy findings.
