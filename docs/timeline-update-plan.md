# Timeline update plan

This document defines the first safe update surface for `KAN-57`.

## Purpose

Timeline body update is riskier than sub-product update because the update may
replace the Launcher, Local API, Web, Worker, Docker runtime files, and the
process that is coordinating the update.

For this reason, the first implementation exposes a read-only update plan
instead of replacing files immediately.

The plan answers:

- whether the current installation can be updated by the product updater;
- which release artifact would be used;
- which local files and directories must be preserved;
- which application files may be replaced;
- which runtime resources may be stopped or recreated;
- which blockers prevent an update.

## Command

```powershell
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-plan
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-plan --json
```

## Local API

```text
GET http://127.0.0.1:19001/timeline/update/plan
```

The Local API endpoint returns the same plan model as the Launcher command.
The Launcher remains the owner of update orchestration because it can stop and
restart Timeline more safely than the Web UI.

## States

| State | Meaning |
| --- | --- |
| `ready` | A matching built product artifact exists and the current installation is a built artifact. |
| `up_to_date` | The current built artifact is already at the latest detected version. |
| `blocked` | Update must not run until blockers are resolved. |

Developer checkouts are intentionally blocked. They should be updated through
Git and normal development workflows, not by the product updater.

## Preserved data

The update plan preserves:

- `settings.json`
- the configured data root
- `to_timeline`
- `work`
- logs and backups under the data root
- Docker volumes, including shared Ollama data
- sub-product application directories

These are local installation data, not product artifact content.

## Replaced application content

The update plan treats the following as replaceable product artifact content:

- `launcher`
- `launcher-tray`
- `local-api`
- `web`
- `worker`
- `docker`
- `docker-compose.yml`
- `VERSION`
- product documentation shipped with the artifact

## Planned execution order

1. Download the matching built product artifact into a staging directory.
2. Validate archive name, root layout, `VERSION`, runtime identifier, and
   required files.
3. Stop Timeline through the Launcher.
4. Move the current product application files to a rollback directory.
5. Move the validated application files into the Timeline root while preserving
   settings and data.
6. Start Timeline through the Launcher.
7. Run setup verification and health checks.
8. Remove the rollback directory only after verification succeeds.

## Non-goals for the first implementation

- It does not apply updates yet.
- It does not delete user data.
- It does not update sub-products.
- It does not treat GitHub source archives as product update targets.
- It does not bypass installer work tracked separately by `KAN-41`.
