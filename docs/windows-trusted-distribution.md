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
- run the release builder with an explicit Windows signing option;
- verify a signed artifact on a Smart App Control / Code Integrity constrained
  Windows machine;
- publish user-facing guidance for unsupported locked-down environments.

## Current Product Decisions

The near-term Windows product artifact remains a setup ZIP. MSI or MSIX can
become a later packaging layer, but it does not remove the need for signing, so
the first product gate is signed setup ZIP plus strict execution-trust
verification.

Unsigned setup ZIPs are internal validation artifacts only. They may prove that
the bundle shape is correct, but they must not be described as ready for normal
Windows users.

The minimum Windows validation target is a Windows 11 environment where Smart
App Control, WDAC, or Code Integrity can block unsigned desktop binaries. A
release that only works on a permissive developer machine is not enough evidence
for product readiness.

When execution trust fails, the user-facing guidance should be:

```text
Windows の保護機能により Timeline を起動できません。

この配布物は現在の Windows 環境で信頼済みアプリとして実行できません。
署名済みの最新版を入手して再試行してください。
会社や学校の管理PCを利用している場合は、管理者に Timeline の利用可否を確認してください。
解決しない場合は、この画面の詳細と Timeline のログを添えて報告してください。
```

The normal product guidance must not ask the user to weaken Windows security
policy, run manual unblock commands, or use PowerShell/batch/shell wrappers.
Those can remain diagnostic paths for development, but not the customer answer.

## Signing Entry Point

`KAN-68` adds an optional signing entry point to the release builder. Signing is
not automatic because it depends on an external code-signing certificate and a
Windows signing environment.

Product releases can enable signing with either a certificate thumbprint from
the Windows certificate store:

```text
dotnet tools/Timeline.ReleaseBuilder/bin/Debug/net10.0/Timeline.ReleaseBuilder.dll --runtime win-x64 --version <version> --windows-installer --windows-sign --windows-sign-cert-thumbprint <thumbprint> --windows-sign-timestamp-url <timestamp-url>
```

or a PFX file:

```text
dotnet tools/Timeline.ReleaseBuilder/bin/Debug/net10.0/Timeline.ReleaseBuilder.dll --runtime win-x64 --version <version> --windows-installer --windows-sign --windows-sign-cert-pfx <path-to.pfx> --windows-sign-cert-password-env TIMELINE_SIGNING_PFX_PASSWORD --windows-sign-timestamp-url <timestamp-url>
```

The builder signs only Windows host-side entry points:

- `Timeline.WindowsInstaller.exe`;
- `Timeline.Launcher.exe` / `Timeline.Launcher.dll`;
- `Timeline.Launcher.Tray.exe` / `Timeline.Launcher.Tray.dll`;
- `Timeline.LocalApi.exe` / `Timeline.LocalApi.dll`.

It does not sign Docker container-side Web or Worker outputs. Those are not the
local Windows execution surface that Smart App Control or WDAC blocks before
Timeline starts.

If `--windows-sign` is used without exactly one certificate selector, or if the
PFX password environment variable is missing, the builder fails before producing
a release artifact. This keeps unsigned production releases from being confused
with signed release candidates.

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
- whether the signing certificate declares Code Signing usage;
- whether the certificate chain is trusted by the Windows host running the
  verifier;
- observed OS policy failures.

Unsigned binaries are warnings while the signing pipeline does not exist. They
become release blockers when the Windows distribution is declared
product-ready.

A binary that is merely signed is not automatically product-ready. In strict
mode, a self-signed or otherwise untrusted certificate is still treated as an
execution-trust problem.

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

## Remaining Open Decisions

- Which certificate source will be used for signing?
- Where will signing happen: local release machine, CI, or a dedicated release
  workflow?
- Whether MSI / MSIX is worth adding after signed setup ZIP is working.

## Related Jira

- `KAN-41`: OS-native install and uninstall
- `KAN-63`: Windows installer artifact generation
- `KAN-66`: Smart App Control blocks unsigned Launcher
- `KAN-67`: trusted Windows distribution path
