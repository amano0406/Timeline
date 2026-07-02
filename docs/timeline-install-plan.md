# Timeline install plan

This document defines the install plan used by the C# Launcher, Timeline Local
API, and OS installers.

The plan belongs to KAN-48 and KAN-49.

## Goal

Timeline should be treated as an OS application, not as a folder of source
files that users have to understand.

The user-facing direction is:

1. Install or extract a built Timeline artifact.
2. Register Timeline as an OS application.
3. Start Timeline through the C# resident Launcher.
4. Preserve settings and user data separately from application files.

Batch files, shell scripts, and `.command` wrappers are not user-facing
application entries.

## Current install plan implementation

The plan is read-only. It does not install, remove, or change OS settings.

Launcher:

```powershell
dotnet run --project .\launcher\Timeline.Launcher.csproj -- install-plan
dotnet run --project .\launcher\Timeline.Launcher.csproj -- install-plan --json
```

Local API:

```text
GET /timeline/install/plan
```

The response describes:

- the current platform;
- the Timeline application root;
- settings and data paths that must be preserved;
- the normal OS application entry;
- future startup and uninstall registrations;
- Windows and macOS installer artifact targets;
- warnings that an installer must handle.

## Current Windows setup implementation

KAN-63 adds the first C# Windows setup bundle. It is not an MSI and it is not
signed, but it is a real setup entry that can install a built Timeline product
artifact without source checkout or scripts.

Create it from the release builder:

```powershell
dotnet run --project .\tools\Timeline.ReleaseBuilder\Timeline.ReleaseBuilder.csproj -- --runtime win-x64 --version <version> --windows-installer
```

The generated setup ZIP contains:

```text
Timeline-Setup/
  installer/Timeline.WindowsInstaller.exe
  artifacts/Timeline-win-x64-<version>.zip
  installer-manifest.json
  README.txt
```

The setup executable supports a plan-only mode:

```powershell
.\installer\Timeline.WindowsInstaller.exe --artifact .\artifacts\Timeline-win-x64-<version>.zip --plan
```

Before executing the installer, the release builder can verify the setup ZIP
without changing Windows settings:

```powershell
dotnet run --project .\tools\Timeline.ReleaseBuilder\Timeline.ReleaseBuilder.csproj -- --verify-windows-installer .\release\Timeline-win-x64-<version>-setup.zip
dotnet run --project .\tools\Timeline.ReleaseBuilder\Timeline.ReleaseBuilder.csproj -- --verify-windows-installer .\release\Timeline-win-x64-<version>-setup.zip --json
```

This verification checks the setup bundle shape, the embedded product artifact,
manifest consistency, required Launcher / Local API entries, and absence of
source files, temporary development directories, user data, settings, and
script wrappers. It is the safe pre-install check for KAN-63 because it does
not create shortcuts, registry entries, or application files.

The setup verifier and the installer `--plan` output also report unsigned
Windows binaries as warnings. A warning here means the artifact can be installed
mechanically, but Windows execution policy may still block the installer,
Launcher, resident Launcher, or Local API when a user tries to run them.

This check does not prove that every Windows machine will execute the Launcher.
Smart App Control, WDAC, or Code Integrity policies can still block unsigned
Timeline assemblies. Treat setup ZIP verification and OS execution trust as
separate gates: the first is mechanical artifact integrity, and the second is
resolved by signing, trusted installer packaging, or explicit user guidance.

The default install directory is:

```text
%LOCALAPPDATA%\Programs\Timeline
```

When executed, the installer:

- extracts the built Timeline product artifact;
- creates or updates the Windows Start Menu shortcut through the C# Launcher
  shortcut service;
- registers Timeline in Windows Apps & Features through the uninstall
  registration service;
- writes an install receipt under the Timeline runtime directory.

The installer refuses to replace a non-empty install directory unless `--force`
is supplied. With `--force`, it replaces application files but preserves
`settings.json`, `data`, `logs`, `runtime`, and managed `products`.

## Windows application entry

The Windows application entry is the Start Menu shortcut.

When a published resident Launcher executable exists, the shortcut target
points directly to that executable.

If no published executable exists, development checkouts can still fall back to
`dotnet` execution. That fallback is acceptable for development, but it is not
the desired user artifact behavior.

The OS startup registration follows the same rule. When the resident Launcher
executable exists, startup registration points directly to it. Development
checkout fallback can still use the DLL or project path, but user-facing
artifacts should not require the .NET SDK or source project execution.

Startup status checks must compare the existing OS registration with the
expected Launcher command. If a registration exists but points to an old script,
project path, or different executable, the UI should show it as an update target
instead of treating it as fully healthy. Saving the startup setting is the
repair path; status checks do not rewrite OS settings by themselves.

The settings save path must also treat `legacy_registered` and
`registered_with_different_target` as refresh targets when OS startup is enabled.
Otherwise a stale registration would remain in place because it is technically
already registered.

## Windows uninstall registration

Windows also needs an Apps & Features / uninstall-list entry so the product is
visible as a normal installed application.

The C# Launcher owns the registration surface:

```powershell
dotnet run --project .\launcher\Timeline.Launcher.csproj -- uninstall-registration-status --json
dotnet run --project .\launcher\Timeline.Launcher.csproj -- uninstall-registration-install --json
dotnet run --project .\launcher\Timeline.Launcher.csproj -- uninstall-registration-remove --json
```

The registration is per-user under:

```text
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\Timeline
```

The registration points to the C# Launcher `uninstall` command. The command
starts Timeline when needed and opens the settings section where the user can
choose the uninstall scope. It does not delete files directly. This is
intentional: Timeline contains application files, settings, user material,
generated data, managed sub-products, logs, and Docker resources. Destructive
removal should only be added after the selectable uninstall levels have a final
confirmation flow.

The install plan reports this target as `uninstall_entry` so Windows installer
work can verify:

- whether the entry is registered;
- which registry key is used;
- which install location is advertised;
- which Launcher command would be invoked from Windows.

## Installer boundary

The install plan is still not itself the installer.

Windows setup and future macOS/native installers should consume the same
concepts:

- application files are replaceable;
- settings are local configuration;
- user input files and generated Timeline data are user data;
- OS app entry, startup registration, and uninstall registration are OS-level
  registrations;
- destructive operations require explicit user confirmation.

Startup registration is already managed by Timeline settings and the Local API
on Windows and macOS. The install plan should therefore show this area as
`settings_managed`, not as a future-only installer task. A future installer must
respect the same registration target instead of inventing a separate startup
mechanism.

## Relationship to uninstall

`docs/timeline-uninstall-plan.md` defines deletion levels.

The install plan and uninstall plan should stay aligned. Anything the installer
registers should be visible in the uninstall plan before it becomes
destructive.
