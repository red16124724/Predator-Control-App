# Security Policy

Signing status: this local release is unsigned. A publisher must obtain a trusted code-signing certificate with its private key and Windows SignTool, sign the final executable with SHA-256 and a trusted timestamp, and verify the signature before distribution. Checksums do not authenticate the publisher.

## Local build and upstream support

This package is a local 1.5.5 brightness update. It is not an official upstream release or a statement of upstream support. The historical policy listed upstream 1.4.x and 1.5.x as supported; contact the maintainer for the current upstream policy.

The local executable is unsigned. Supplied SHA-256 checksums identify the packaged files but do not authenticate a publisher or prove that the application is free of vulnerabilities.

---

## Security architecture in the local 1.5.5 build

Predator Control App enforces defense-in-depth principles across all subsystems:
1. **Network and update behavior**: Hardware control works locally. The app checks GitHub once at startup, including hidden launches, and on manual request. It does not perform periodic update polling or analytics. Downloads use HTTPS and allowed GitHub hosts. Executable installation requires a SHA-256 associated with the chosen release asset. This is checksum verification, not signature verification.
2. **Ring-0 Kernel Driver Safety via PawnIO**: All MSR and MMIO operations validate register boundaries, maintain named inter-process mutex locks (`Global\Access_MSR`, `Global\Access_PCI`), and enforce directional split-write ordering (`NewPL1 > OldPL2 ? PL2 then PL1 : PL1 then PL2`) to prevent transient illegal hardware states.
3. **Native FFI panic handling**: The Rust C-ABI entry points use `safe_ffi` panic handling. `catch_unwind` contains unwinding Rust panics at these boundaries; it does not prevent invalid-pointer faults, all process aborts, or memory leaks. Managed/native buffers and handle lifetimes still require validation.
4. **Secure Named Pipe IPC**: Local IPC is strictly confined to `PipeDirection.InOut`, `PipeOptions.Asynchronous`, and `CurrentUserOnly` ACL token checks to prevent unauthorized local elevation or injection.
5. **Deterministic Registry Isolation**: App settings are stored under user-isolated `HKCU\Software\PredatorControl` with registry serialization locks to prevent concurrent race conditions.
6. **Brightness transport**: The local fix uses acknowledged firmware brightness commands before leaving RGB values unscaled. Supported software fallbacks apply brightness once. Keyboard service writes are serialized; a barrier drains active writes and supersedes stale queued writes before WMI control. The controller prefers WMI and avoids a second keyboard service dispatch after success. This change does not expand fan, CPU power, or driver privileges.

The previous brightness update passed 243 selected .NET and 126 pure Rust tests; those are historical results. Earlier migration verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs. The complete inherited hardware/integration suite, UI launch, and physical keyboard brightness require separate verification.

Brightness regression tests with mocked hardware interfaces do not establish a complete security audit or physical brightness calibration. The complete inherited test suite was not run indiscriminately because some tests can access live hardware. Native extraction verifies its cached file against the embedded resource before loading; adjacent and search-path native DLLs are not used. Integrity of the executable itself still matters.

---

## Reporting a Vulnerability

Security and privacy are taken very seriously.

- If you discover a vulnerability or potential exploit, please **do not open a public issue**.
- Instead, submit a report via GitHub Security Advisories or contact the maintainer directly through GitHub.
- Please include:
  1. A clear description of the vulnerability.
  2. Steps to reproduce or proof-of-concept code.
  3. Affected component (e.g., IPC named pipes, updater checksum, EC driver, PawnIO backend).

We will investigate promptly and release a patched version as soon as possible.

## Firmware and rule migration boundary

SMBIOS type 170/172 parsing and selected protocol and lighting rules now cross the Rust FFI boundary. Pointer and buffer lengths are checked in native entry points, which use panic containment; invalid pointers and process aborts remain outside that guarantee. Managed fallbacks stay available when the native DLL or exports are unavailable. Operating-system discovery remains managed. Existing WMI/HID transport and hardware-write paths are unchanged, including the pre-existing Rust EC-HID transport. Current verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs; real hardware was not exercised.

Migration test scope: targeted tests do not intentionally issue hardware writes. In the earlier migration, loading PredatorNativeCore could open PawnIO and start telemetry/NVAPI polling. The current build initializes those facilities on demand; tests that request hardware operations still require care. Physical hardware effects were not measured.

Local audit update: see AUDIT.md at the source root for fixes, exact verification scope, and unresolved hardware-policy findings.
