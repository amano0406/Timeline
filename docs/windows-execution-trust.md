# Windows execution trust

This document tracks the execution-trust problem found while validating the
Windows installer and uninstall entry.

## Problem

Timeline can produce a structurally valid Windows setup bundle, but Windows may
still refuse to execute the Launcher.

Observed on the development machine:

- `Timeline.ReleaseBuilder.dll` runs through `dotnet`.
- `dotnet run` can be blocked when it launches the generated
  `Timeline.ReleaseBuilder.exe` apphost.
- `Timeline.LocalApi.dll` can start.
- `Timeline.Launcher.Tray.dll` can load.
- `Timeline.Launcher.dll` and `Timeline.Launcher.exe` are blocked by Windows
  Code Integrity / Smart App Control.

The relevant event log fields were:

- policy name: `VerifiedAndReputableDesktop`
- requested signing level: `2`
- validated signing level: `1`
- signature count: `0`
- publisher: `Unknown`
- issuer: `Unknown`
- status: `0xc0e90002`

The important distinction is that artifact integrity and OS execution trust are
different gates. A valid ZIP only proves that the expected files are present and
hashes match. It does not prove that Windows will allow those files to run.

## User Impact

If this is not solved, a user can download or install Timeline successfully but
still be unable to start the product from the normal Launcher entry.

This affects:

- first launch after install;
- Start Menu launch;
- resident Launcher startup;
- Windows Apps & Features uninstall entry;
- update and recovery flows that rely on the Launcher.

## Design Position

Timeline should not ask users to run PowerShell, batch files, shell files, or
manual unblock commands as the normal solution.

The product should aim for a trusted application entry. Workarounds can exist
for development, but they are not the user-facing answer.

## Options

| Option | Value | Weakness |
| --- | --- | --- |
| Code sign Launcher and installer | Best match for normal Windows distribution | Requires certificate and release process |
| Package as MSIX/MSI with trusted signing | Strong OS-native installation story | More packaging work and signing still matters |
| Keep ZIP-only distribution | Simple to build | Weak against Smart App Control and user trust |
| Tell users to lower policy or unblock files | Fast for development | Poor product experience and not acceptable for managed PCs |
| Move all user actions into Docker/Web | Reduces local executable surface | Still needs one trusted local entry for startup, install, update, and local OS integration |

## Recommended Direction

Treat signing or trusted installer packaging as part of the Windows user
distribution path.

The near-term implementation should keep these gates separate:

1. Artifact verification:
   - generated ZIP shape;
   - manifest consistency;
   - source/script/data exclusion;
   - hash and size checks.
2. Execution trust verification:
   - whether Windows allows Launcher and installer entry points to load;
   - whether failures produce clear guidance instead of silent startup failure.

Current implementation state:

- Windows setup bundle verification reports unsigned installer, Launcher,
  resident Launcher, and Local API binaries as warnings.
- Windows installer `--plan` reports unsigned binaries in the embedded product
  artifact as warnings.
- These warnings are not blockers because the product can still produce a valid
  artifact before a signing pipeline exists.
- A user-facing Windows distribution should still be treated as incomplete until
  signing or another trusted packaging path is selected.

## Acceptance Direction

The Windows distribution path should not be considered product-ready until:

- Launcher execution succeeds on a Smart App Control / Code Integrity constrained
  Windows machine, or the remaining restriction is explicitly documented as an
  unsupported environment;
- installer validation reports artifact integrity separately from execution
  trust;
- user-facing docs explain what to do if Windows blocks Timeline;
- Jira issues that depend on OS app entry validation reference this constraint.

## Related Jira

- `KAN-41`: Timeline as an OS-installed application
- `KAN-63`: Windows installer artifact generation
- `KAN-65`: OS uninstall entry and uninstall scope selection
- `KAN-66`: Smart App Control blocks unsigned Launcher
