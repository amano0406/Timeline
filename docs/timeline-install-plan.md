# Timeline install plan

This document defines the read-only install plan used by the C# Launcher,
Timeline Local API, and future OS installers.

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

## Current implementation

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

## Installer boundary

The install plan is not the installer.

The future Windows or macOS installer should consume the same concepts:

- application files are replaceable;
- settings are local configuration;
- user input files and generated Timeline data are user data;
- OS app entry, startup registration, and uninstall registration are OS-level
  registrations;
- destructive operations require explicit user confirmation.

## Relationship to uninstall

`docs/timeline-uninstall-plan.md` defines deletion levels.

The install plan and uninstall plan should stay aligned. Anything the installer
registers should be visible in the uninstall plan before it becomes
destructive.
