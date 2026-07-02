# Timeline uninstall plan

This document tracks the uninstall scope model for `KAN-51` and the Windows
GUI uninstall path implemented for `KAN-65`.

## Purpose

Timeline uninstall must not be a single delete operation. The product contains
application files, local settings, user input material, generated Timeline data,
sub-product data, logs, and Docker resources. Some runtime resources, such as
Ollama data, may be shared with other tools.

The Timeline settings UI still exposes the uninstall plan as an impact preview.
Windows installer builds additionally register a GUI uninstall entry. The
default Windows uninstall action removes replaceable application files only and
preserves settings, materials, generated data, logs, runtime state, managed
products, and Docker resources.

## Command

```powershell
dotnet run --project .\launcher\Timeline.Launcher.csproj -- uninstall-plan
dotnet run --project .\launcher\Timeline.Launcher.csproj -- uninstall-plan --json
dotnet run --project .\launcher\Timeline.Launcher.csproj -- uninstall
```

For installed Windows builds, the OS app list points to the bundled installer:

```text
Timeline/installer/Timeline.WindowsInstaller.exe --uninstall --install-dir <TimelineRoot>
```

That command opens a GUI confirmation and then runs a temporary worker copy so
the uninstaller can remove its own installed files safely.

## Local API

```text
GET http://127.0.0.1:19001/timeline/uninstall/plan
```

Windows uninstall-list registration is tracked separately from deletion
execution:

```text
GET  http://127.0.0.1:19001/timeline/uninstall-registration/status
POST http://127.0.0.1:19001/timeline/uninstall-registration/install
POST http://127.0.0.1:19001/timeline/uninstall-registration/remove
```

When `Timeline/installer/Timeline.WindowsInstaller.exe` exists, the registration
command points Windows to the GUI uninstaller. Development checkouts or older
installs without the bundled installer can still fall back to Launcher
diagnostic commands.

## UI

The Timeline settings page shows the read-only plan under the uninstall impact
area. The UI currently displays:

- the recommended uninstall level
- the application root, data root, and settings path
- warning messages translated into user-facing Japanese
- a selectable uninstall level so users can compare intended scope before any
  future execution step
- each uninstall level, whether strong confirmation is required, and a short
  preview of affected targets

The settings page still does not execute full destructive uninstall operations.
The Windows GUI uninstaller currently executes only the default app-only level.
Broader deletion levels should require a stronger confirmation model before
they become executable.

## Levels

| Level | Meaning | Default |
| --- | --- | --- |
| `app_only` | Remove replaceable Timeline application files only. Preserve settings, materials, generated data, logs, sub-products, and Docker resources. | Yes |
| `app_and_settings` | Remove application files and `settings.json`. User data remains, but reinstall starts from fresh settings. | No |
| `app_and_local_data` | Remove application files, settings, input material, generated text, Timeline store, work files, logs, and managed sub-product data. | No |
| `app_and_runtime_resources` | Include runtime resources such as Docker project resources. Shared resources remain opt-out by default. | No |

## Important constraint

The current development layout keeps `settings.json` and the default `data`
directory under the Timeline application root. A future app-only uninstaller
must either preserve these paths explicitly or move user data to a dedicated
application data directory before deleting the application root.

For that reason, the current plan returns warnings such as:

- `settings_inside_app_root`
- `data_inside_app_root`

## Non-goals

- It does not delete files.
- It does not remove Docker resources.
- It does not uninstall sub-products.
- It does not create a Windows or Mac installer.
- It does not make the Windows uninstall entry destructive.
- It does not decide Docker Desktop or Ollama ownership.

Those actions should be implemented only after the plan is visible to users and
can require strong confirmation for destructive levels.
