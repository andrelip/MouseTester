# MouseTester

[![Downloads](https://img.shields.io/github/downloads/valleyofdoom/MouseTester/total.svg)](https://github.com/valleyofdoom/MouseTester/releases)

Windows mouse polling-rate analyzer. Registers for Raw Input via `WM_INPUT`,
timestamps each event with `QueryPerformanceCounter`, and plots intervals /
frequency / velocity with OxyPlot.

This fork extends the upstream tool with a jitter-diagnostics layer that
answers *why* a measurement looks noisy: ETW kernel-event correlation, CPU
state sampling, per-spike cause attribution (DPC storms, GC pauses, focus
loss, etc.), USB host-controller topology with vendor-process detection,
and a pre-flight trust score. See `CLAUDE.md` for architecture details.

## Dependencies

### Runtime

- **.NET Framework 4.8** (x64) — already present on Windows 10 1903+ /
  Windows 11.
- **Windows Debugging Tools (cdb.exe)** — only required if Smart App
  Control is enabled on your machine. The launcher (`LaunchMouseTester.bat`)
  invokes `cdb.exe` so the unsigned binary is allowed to run. Install via
  Windows SDK ("Debugging Tools for Windows" component).
- **Administrator rights** — required to enable the ETW kernel session
  (DPC / ISR / context-switch attribution). Without admin the cold sampler
  still works, but driver-level causes will not be identified.

### NuGet packages added by this fork

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.Diagnostics.Tracing.TraceEvent` | 3.1.16 | ETW kernel session — captures DPC, ISR, and ContextSwitch events for per-spike attribution. Bundles a native dep (see below). |
| `Microsoft.Diagnostics.NETCore.Client` | 0.2.661903 | TraceEvent transitive dependency. |
| `System.Security.Principal.Windows` | 5.0.0 | TraceEvent transitive — Windows principal/identity APIs. |
| `System.Security.AccessControl` | 6.0.1 | TraceEvent transitive — Windows ACL APIs. |
| `Microsoft.Win32.Registry` | 5.0.0 | TraceEvent transitive — registry access. |
| `System.Threading.Tasks.Extensions` | 4.6.3 | TraceEvent transitive — ValueTask. |
| `Microsoft.Bcl.AsyncInterfaces` | 10.0.7 | TraceEvent transitive — async stream interfaces. |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.7 | TraceEvent transitive — DI abstractions. |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.7 | TraceEvent transitive — logging abstractions. |

All managed dependencies are embedded into a single EXE by **Costura.Fody**
(already in upstream). New references must carry `<Private>True</Private>`
in the csproj so Costura picks them up.

### Native dependency (cannot be embedded)

- `KernelTraceControl.dll` (x64, ~260 KB) — ships with the TraceEvent
  package under `packages\Microsoft.Diagnostics.Tracing.TraceEvent.3.1.16\build\native\amd64\`.
  Required at runtime alongside `MouseTester.exe` to start the kernel ETW
  session. Copied to the output directory by a `<None CopyToOutputDirectory="PreserveNewest">`
  entry in the csproj.

### .NET Framework assembly references added

- `System.Management` — WMI queries (`Win32_USBController`, `Win32_USBControllerDevice`,
  `Win32_PnPEntity`, `Win32_Service`) for USB topology enumeration and
  service-process mapping.
- `System.ServiceProcess` — `ServiceController` for stopping driver
  services (e.g. `LGHUBUpdaterService` before killing `lghub_updater.exe`).

## Build

### Required tools

- **Visual Studio 2022 Build Tools** (or VS Community) with:
  - .NET desktop workload
  - .NET Framework 4.8 SDK
  - .NET Framework 4.8 Targeting Pack
- **`nuget.exe`** — standalone CLI.

### Commands

```powershell
nuget restore .\MouseTester\MouseTester.sln
MSBuild.exe .\MouseTester\MouseTester.sln -p:Configuration=Release -p:Platform=x64
```

Output: `MouseTester\MouseTester\bin\x64\Release\MouseTester.exe` plus
`KernelTraceControl.dll`.

## Launch

**Always launch via `LaunchMouseTester.bat`**, not by double-clicking the
EXE directly. The script wraps:

```
cdb.exe -gG -c "qd" MouseTester.exe
```

`cdb.exe` is Microsoft-signed and trusted by Smart App Control; the `qd`
(quit-detach) command makes cdb exit immediately while leaving MouseTester
running. Without this wrapper, SAC will block the unsigned EXE.

For ETW kernel diagnostics, right-click the .bat → **Run as administrator**.

## Hot keys

| Key | Action |
|---|---|
| F1 / F2 | Start / Stop log |
| F3 | Open plot window |
| F4 | Pre-flight Trust Check (timer / power / EcoQoS / overlay-software detection, with USB devices + close-hooks dialog) |
| F5 | Synthetic 1 kHz Self-Test (5 s) — validates the analysis pipeline end-to-end |

## Credits

Originally by [microe](https://github.com/microe/mousetester), forked and
modernized by [valleyofdoom](https://github.com/valleyofdoom/MouseTester).
Jitter-diagnostics layer added in this fork.
