# Windows trusted distribution

This document defines the product direction for `KAN-67`.

## Goal

Windows users should be able to install and start Timeline as a normal trusted
application. A structurally valid ZIP is not enough if Windows refuses to run
the Launcher.

## Current State

Timeline can currently produce:

- a Windows product ZIP;
- a Windows setup ZIP;
- a C# Windows installer entry;
- Start Menu and Apps & Features registration logic;
- artifact verification that separates blockers from warnings.

The remaining gap is execution trust. Current local artifacts are unsigned, and
Smart App Control / WDAC / Code Integrity can block the installer or Launcher.

## Product Position

Do not make policy weakening, manual unblock operations, PowerShell scripts, or
batch files the normal user answer.

Development fallbacks may exist, but the product path should be:

1. user downloads a Windows installer artifact;
2. user runs it through a normal OS flow;
3. Timeline appears as an installed application;
4. Launcher starts without requiring the user to understand .NET, Docker, or
   scripts.

## Distribution Options

| Option | User Value | Risk | Product Position |
| --- | --- | --- | --- |
| Sign installer and app binaries | Best fit for a normal desktop product | Requires certificate handling and release discipline | Preferred first target |
| MSIX or MSI with signing | Stronger OS-native story | More packaging work; signing still required | Good later target |
| Unsigned setup ZIP | Easy to generate | Blocked by stricter Windows policy; weak trust signal | Development or private testing only |
| Ask users to unblock or lower policy | Fast workaround | Bad product experience and may be impossible on managed PCs | Not acceptable as normal path |
| Move more work into Docker/Web | Reduces local executable surface | Still needs one trusted local entry for startup and OS integration | Useful but not sufficient |

## Recommended Direction

Use a signed Windows distribution path as the product target.

Near term:

- keep producing setup ZIPs for internal validation;
- keep unsigned binaries as warnings, not blockers;
- keep `artifact integrity` and `execution trust` as separate verification
  results;
- clearly mark unsigned Windows artifacts as not product-ready.

Product-ready target:

- sign `Timeline.WindowsInstaller.exe`;
- sign `Timeline.Launcher.exe` and related Launcher binaries;
- sign `Timeline.Launcher.Tray.exe`;
- sign `Timeline.LocalApi.exe`;
- verify a signed artifact on a Smart App Control / Code Integrity constrained
  Windows machine;
- publish user-facing guidance for unsupported locked-down environments.

## Verification Gates

### Artifact Integrity

This gate answers whether the bundle is structurally correct.

It checks:

- required files exist;
- manifest sizes and hashes match;
- source files, temporary directories, settings, user data, and script wrappers
  are not included;
- product runtime matches the target platform.

Failure here is a blocker.

### Execution Trust

This gate answers whether the OS is likely to allow the product to run.

It checks:

- installer signature state;
- Launcher signature state;
- resident Launcher signature state;
- Local API signature state;
- observed OS policy failures.

Unsigned binaries are warnings while the signing pipeline does not exist. They
become release blockers when the Windows distribution is declared
product-ready.

The release verifier supports both modes:

```text
# Internal artifact-shape check. Unsigned Windows binaries are warnings.
dotnet tools/Timeline.ReleaseBuilder/bin/Debug/net10.0/Timeline.ReleaseBuilder.dll --verify-windows-installer release/Timeline-win-x64-<version>-setup.zip --json

# Product-ready Windows distribution check. Unsigned Windows binaries are blockers.
dotnet tools/Timeline.ReleaseBuilder/bin/Debug/net10.0/Timeline.ReleaseBuilder.dll --verify-windows-installer release/Timeline-win-x64-<version>-setup.zip --require-windows-execution-trust --json
```

Until signing is introduced, the strict check is expected to fail. That failure
is intentional: it prevents a structurally valid but untrusted Windows bundle
from being treated as ready for normal users.

## Open Decisions

- Which certificate source will be used for signing?
- Will the first production Windows artifact remain a setup ZIP, or move to MSI
  / MSIX?
- Where will signing happen: local release machine, CI, or a dedicated release
  workflow?
- Which Windows policy profile is the minimum supported validation target?
- What exact user-facing message is shown when execution trust fails?

## Related Jira

- `KAN-41`: OS-native install and uninstall
- `KAN-63`: Windows installer artifact generation
- `KAN-66`: Smart App Control blocks unsigned Launcher
- `KAN-67`: trusted Windows distribution path
