<!-- Current local 1.5.5 improvements include startup and manual checks with checksum-required installation, embedded native verification, lazy initialization, and compatibility diagnostics. The executable is unsigned; record actual tests. -->
## Description
<!-- Please describe your changes and the problem they solve. -->
<!-- This source package is the local 1.5.5 brightness update, not an official upstream release. -->

## Changes Made
- 

## Hardware Tested On
- **Laptop Model:** (e.g. Acer Predator Helios Neo 16 PHN16-71)
- **Windows Version:** (e.g. Windows 11 23H2)
- **EC / BIOS Version:** 
- **Validation method:** hardware-disabled/mock tests, physical laptop check, or both
- **Brightness comparison:** static color/effect, AC or battery, and 0% / intermediate / 100% results

## Validation
<!-- Record exact commands and results. Current local package: The previous brightness update passed 243 selected .NET and 126 pure Rust tests; those are historical results. Earlier migration verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs. The complete inherited hardware/integration suite, UI launch, and physical keyboard brightness require separate verification. -->
<!-- Do not run inherited hardware-accessing tests without auditing and deliberate authorization. -->

## Checklist
- [ ] My code builds cleanly (`dotnet build PredatorControlApp.slnx`)
- [ ] Relevant audited tests pass; commands, counts, and unrun tests are recorded
- [ ] No telemetry added; networking documentation matches the startup and manual GitHub update checks
- [ ] RGB brightness is applied once, with firmware acknowledgement or a supported software fallback
- [ ] Static, managed/native animation, audio-reactive, alpha, zone mapping, and rapid slider updates are covered where changed
- [ ] Physical brightness checks are reported separately from packet/mock tests, including restart and battery behavior when tested
- [ ] The published executable embeds the fresh native DLL and the package excludes stale sidecars

## Rust migration verification

- [ ] SMBIOS parser checked against managed reference on valid, malformed and truncated data.
- [ ] Protocol packet encodings and lighting rules checked against managed behavior and boundary values.
- [ ] Existing brightness regression regression tests passed.
- [ ] Device discovery and hardware writes were only exercised when explicitly authorized on suitable hardware.

Current local migration verification: 262 selected .NET tests passed; Release build had zero warnings and errors; 137 Rust tests passed across 15 filtered module runs.

Migration test scope: targeted tests do not intentionally issue hardware writes. In the earlier migration, loading PredatorNativeCore could open PawnIO and start telemetry/NVAPI polling. The current build initializes those facilities on demand; tests that request hardware operations still require care. Physical hardware effects were not measured.

Local audit update: see AUDIT.md at the source root for fixes, exact verification scope, and unresolved hardware-policy findings.
