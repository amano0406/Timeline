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

Before applying a downloaded artifact, the Launcher can show a read-only apply
plan:

```powershell
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-apply-plan --artifact <zip-path>
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-apply-plan --artifact <zip-path> --json
```

Downloaded artifacts can be validated without applying an update:

```powershell
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-validate --artifact <zip-path>
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-validate --artifact <zip-path> --json
```

When a release manifest is available, validate the manifest before the raw ZIP.
This confirms that the manifest points to the intended Timeline artifact and
that the file size and SHA-256 hash still match:

```powershell
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-manifest-validate --manifest <json-path>
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-manifest-validate --manifest <json-path> --json
```

Validated artifacts can also be staged without replacing the current
installation:

```powershell
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-stage --artifact <zip-path>
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-stage --artifact <zip-path> --json
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-manifest-stage --manifest <json-path> --json
```

Recovery policy can also be inspected without applying an update:

```powershell
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-recovery-plan
dotnet run --project .\launcher\Timeline.Launcher.csproj -- update-recovery-plan --artifact <zip-path> --json
```

## Local API

```text
GET http://127.0.0.1:19001/timeline/update/plan
GET http://127.0.0.1:19001/timeline/update/recovery/plan
GET http://127.0.0.1:19001/timeline/update/recovery/plan?path=<zip-path>
GET http://127.0.0.1:19001/timeline/update/artifact/apply-plan?path=<zip-path>
GET http://127.0.0.1:19001/timeline/update/artifact/validate?path=<zip-path>
GET http://127.0.0.1:19001/timeline/update/artifact/manifest/validate?path=<json-path>
POST http://127.0.0.1:19001/timeline/update/artifact/stage
POST http://127.0.0.1:19001/timeline/update/artifact/manifest/stage
```

The Local API endpoint returns the same plan model as the Launcher command.
The Launcher remains the owner of update orchestration because it can stop and
restart Timeline more safely than the Web UI.

`update-apply-plan` is the user-facing decision point. It combines the general
update plan, artifact validation, rollback locations, and failure policy into a
single read-only response with `canApply`. `update-recovery-plan` remains the
lower-level failure recovery policy.

`update-stage` is still non-destructive. It validates the ZIP, extracts it under
`<dataRoot>/work/timeline-updates/<operationId>/artifact/`, and writes a
`stage.json` operation record. It does not stop Timeline, replace application
files, touch Docker resources, or delete user data. Developer checkouts may use
this command to verify built artifacts, but `canApplyAfterStage` remains false
when the current installation is not itself a built product artifact.
`update-manifest-stage` performs the same staging through the release manifest
after confirming the manifest and artifact hash.

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

## Recovery policy

`KAN-59` adds a read-only recovery plan. It does not roll back files yet. Its
purpose is to make the future updater refuse unsafe updates and explain what
will happen if a phase fails.

The recovery plan defines:

- the staging root for a downloaded artifact;
- the rollback root for the current application files;
- the operation log path;
- every replaceable application item that must be backed up;
- the paths that must never be deleted by recovery;
- the failed phase and next action mapping.

Rollback data is planned under:

```text
<dataRoot>/backups/timeline-updates/<operationId>/
```

Downloaded and extracted update files are planned under:

```text
<dataRoot>/work/timeline-updates/<operationId>/
```

The data root is used because it is explicitly preserved across product
updates. The rollback directory must not be inside application folders that are
being replaced.

## Failure phases

| Phase | Local state | Recovery policy |
| --- | --- | --- |
| `download` | No local application files changed. | Discard partial download and retry. |
| `validate` | No local application files changed. | Reject the artifact and keep the current installation. |
| `stop` | Runtime may be stopped, but files are unchanged. | Start Timeline again through the Launcher. |
| `backup` | Replacement has not started. | Keep partial backup for diagnostics and retry only after backup can complete. |
| `replace` | Application files may be partially replaced. | Restore backed-up application files before startup. |
| `start` | New files are installed but startup failed. | Keep backup, show diagnostics, allow rollback. |
| `verify` | Startup passed but checks failed. | Keep backup, allow retry or rollback. |
| `cleanup` | Verification passed. | Keep backup and retry cleanup later. |

Recovery must not delete:

- `settings.json`;
- user input files;
- generated Timeline data;
- sub-product directories;
- Docker volumes, including shared Ollama data.

## Non-goals for the first implementation

- It does not apply updates yet.
- It does not delete user data.
- It does not update sub-products.
- It does not treat GitHub source archives as product update targets.
- It does not bypass installer work tracked separately by `KAN-41`.

## Artifact validation

The artifact validation step checks the ZIP before any replacement is allowed.

Required entries:

- `launcher/`
- `launcher-tray/`
- `local-api/`
- `web/`
- `worker/`
- `docker-compose.yml`
- `VERSION`

Forbidden entries:

- `.git/`
- `data/`
- `docs-temp/`
- `scripts-temp/`
- `source-downloads/`
- `release/`
- `settings.json`

The validator also reads `VERSION` and blocks a runtime mismatch, for example
using a macOS artifact on Windows.

`valid` means the artifact can be applied to the current installation. A
cross-runtime artifact such as `osx-arm64` on Windows remains `valid=false`.
For release checks, `structureValid` shows whether the ZIP itself has the
required product layout and avoids forbidden local data. This lets Windows CI
or a Windows development machine confirm a macOS artifact shape without making
it installable on Windows.
