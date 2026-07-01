# Timeline uninstall plan

This document tracks the first implementation step for `KAN-51`.

## Purpose

Timeline uninstall must not be a single delete operation. The product contains
application files, local settings, user input material, generated Timeline data,
sub-product data, logs, and Docker resources. Some runtime resources, such as
Ollama data, may be shared with other tools.

The first implementation exposes a read-only uninstall plan. It does not delete
files. The plan exists so the UI, Launcher, future installer, and Jira
verification can agree on what each uninstall level means before destructive
execution is added.

## Command

```powershell
dotnet run --project .\launcher\Timeline.Launcher.csproj -- uninstall-plan
dotnet run --project .\launcher\Timeline.Launcher.csproj -- uninstall-plan --json
```

## Local API

```text
GET http://127.0.0.1:19001/timeline/uninstall/plan
```

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

The UI still does not execute uninstall operations. Destructive execution should
be added only after the installer/uninstaller flow has a final confirmation
model.

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
- It does not decide Docker Desktop or Ollama ownership.

Those actions should be implemented only after the plan is visible to users and
can require strong confirmation for destructive levels.
