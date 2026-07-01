# Sub-product update plan

This document tracks the safe sub-product update direction for `KAN-40`,
`KAN-60`, and `KAN-61`.

## Purpose

Timeline manages several sub-products. A user should be able to update them
without losing settings, source files, generated data, or runtime data.

The current implementation can update a sub-product through GitHub source
archive ZIPs. That is useful as a transitional mechanism, but it is not the
final user-facing update contract. The safer direction is to use built product
artifacts, the same broad policy used for Timeline body updates.

## Read-only plan endpoint

The first implementation adds a read-only plan endpoint:

```text
GET http://127.0.0.1:19001/products/runtime/<productId>/update-plan
```

The endpoint does not start, stop, download, delete, or replace anything.

It returns:

- current product path and source settings;
- installed version and latest source version when available;
- latest built product artifact status when a GitHub Release exists;
- whether the product is installed and complete;
- whether the product path is managed by Timeline;
- current distribution mode;
- paths that should be preserved;
- application path that would be replaced;
- planned update steps;
- blockers and warnings.

## Current distribution modes

| Mode | Meaning | User-facing safety |
| --- | --- | --- |
| `built_product_artifact` | Update uses a built runtime artifact. | Target direction. |
| `built_product_artifact_missing` | Product metadata still points at a GitHub repository, but a matching runtime artifact is not available from the Release. | Block normal update until a built artifact is attached. |
| `legacy_source_archive_demoted` | Only legacy source archive metadata is available. | Informational only; not a normal user-facing updater. |
| `unknown` | Product source type is missing or not recognized. | Block or warn. |
| `unsupported` | Product source type is configured but does not have a supported update flow. | Block normal update until a supported distribution path is defined. |

## Update plan states

| State | Meaning | User-facing action |
| --- | --- | --- |
| `up_to_date` | No newer supported update is available. | No action needed. |
| `built_artifact_ready` | A newer built product artifact is available and the product can use the built updater. | The UI may offer an update. |
| `built_artifact_required` | A newer GitHub source archive exists, but no matching built artifact is available. | Do not offer a normal update; publish a built artifact first. |
| `blocked` | The product path, metadata, or runtime state is unsafe for automatic update planning. | Show blockers and ask the user to resolve them. |

## Built artifact discovery

The plan endpoint now checks the latest GitHub Release for a runtime-specific
ZIP asset. The expected naming pattern is:

```text
<repository-name>-<runtime>-<version>.zip
```

Examples:

```text
TimelineForAudio-win-x64-v0.4.11.zip
TimelineForVideo-macos-arm64-v0.5.7.zip
TimelineForPcInfo-win-x64-v0.2.1.zip
```

The runtime name follows the Timeline artifact convention:

- `win-x64`
- `macos-arm64`
- `macos-x64`
- other .NET runtime identifiers as-is

If no Release or matching asset exists, the endpoint reports that as a warning.
It does not fall back silently to a source archive for the normal user-facing
update path. `sourceArchiveUpdateAvailable=true` may still be returned as
diagnostic information, but `canUseCurrentUpdater=false` keeps the old source
archive updater out of the normal update flow.

The product runtime overview follows the same rule. Its `updateAvailable`
field is reserved for a normal safe update path, not for a newer source archive.
When only a newer source archive exists, the overview can expose
`sourceArchiveUpdateAvailable=true` so the UI can show that a built artifact is
still required.

The product management UI can request the per-product update plan explicitly.
When `canUseBuiltArtifactUpdater=true`, the UI may call:

```text
POST http://127.0.0.1:19001/products/runtime/<productId>/update-artifact/apply-latest
```

The endpoint requires `confirm=true`, downloads the latest built artifact from
the Release asset URL, validates it with the same artifact validator, and then
uses the existing built artifact apply flow. It does not use the source archive
updater.

## Built artifact validation

Downloaded or locally generated artifacts can be validated before update:

```text
GET http://127.0.0.1:19001/products/runtime/<productId>/update-artifact/validate?path=<zip-path>
```

The validator checks:

- ZIP file exists and is readable;
- archive name matches `<product-name>-<runtime>-<version>.zip`;
- ZIP contains exactly one product root directory;
- product root matches the configured product display name;
- `VERSION` exists and is a Timeline sub-product artifact;
- `productId`, `productName`, and runtime match the target product and current
  machine;
- required entries exist:
  - `VERSION`
  - `timeline-product.json`

Validation does not stop products, modify files, or apply an update.

## Built artifact application plan

Before staging or applying an artifact, Timeline can ask whether the current
machine is allowed to apply it:

```text
GET http://127.0.0.1:19001/products/runtime/<productId>/update-artifact/apply-plan?path=<zip-path>
```

The apply-plan endpoint is read-only. It checks the artifact and the installed
product state, then returns `state=ready` or `state=blocked`.

The plan includes:

- artifact validation result;
- whether the product is installed;
- whether runtime files were found;
- whether the product path is under Timeline-managed product locations;
- whether the product path is safe to replace;
- whether the product Git worktree is clean;
- preserved paths and replacement target;
- the guarded update steps that would run after explicit confirmation.

This endpoint exists so the UI and Launcher can show the user why an update can
or cannot start before calling the destructive `apply` endpoint.
The UI may call this read-only endpoint even for development placements such as
`C:\apps\TimelineForAudio`. In that case the plan should show the placement
blocker instead of hiding the diagnosis itself.

## Built artifact staging

After validation, a local artifact can be staged before replacement:

```text
POST http://127.0.0.1:19001/products/runtime/<productId>/update-artifact/stage
Content-Type: application/json

{
  "path": "C:\\apps\\Timeline\\release\\sub-products\\TimelineForAudio-win-x64-v0.4.11.zip"
}
```

The staging endpoint:

- runs the same artifact validation first;
- refuses to extract invalid artifacts;
- extracts only into Timeline's work directory:
  `work/product-updates/<operationId>/artifact/<ProductName>`;
- returns the staged root path, validation result, preserved paths, replacement
  target, and remaining update steps;
- does not stop the product;
- does not replace the installed product directory;
- does not change settings, source files, generated data, or Docker resources.

This is the safety boundary before actual replacement. It lets Timeline prove
that the artifact is structurally usable before any destructive update step is
introduced.

## Built artifact application

The actual application endpoint is intentionally guarded:

```text
POST http://127.0.0.1:19001/products/runtime/<productId>/update-artifact/apply
Content-Type: application/json

{
  "path": "C:\\apps\\Timeline\\release\\sub-products\\TimelineForAudio-win-x64-v0.4.11.zip",
  "confirm": true
}
```

Without `confirm=true`, the endpoint refuses to run. Even with confirmation,
the updater still blocks if:

- the product is not installed;
- required runtime files are missing;
- the product application path is outside Timeline-managed locations;
- the current product Git worktree has local changes;
- artifact validation fails;
- artifact staging fails.

When all checks pass, the updater:

1. stages the artifact;
2. stops the sub-product only if it was running;
3. backs up `settings.json`;
4. moves the current app directory to a rollback path;
5. moves the staged product root into the app directory;
6. restores settings;
7. records the installed artifact version;
8. restarts the product if it was running before the update.

The current development layout under `C:\apps` is expected to block because
those product paths are not Timeline-managed install locations. That is
intentional. Real application requires the installer or product manager to place
sub-products in a managed app directory first.

## Preservation rules

Sub-product update must preserve:

- product `settings.json`;
- user source data;
- generated product data;
- runtime work data;
- Docker volumes, networks, and images unless an explicit uninstall flow says otherwise.

The application directory is the update target. It may be replaced only after
settings and rollback data are prepared.

## Planned update order

1. Resolve the latest distributable version.
2. Validate the artifact before changing local files.
3. Stop the sub-product only if it is currently running.
4. Back up settings and current application files.
5. Replace the sub-product application directory.
6. Restore product settings.
7. Restart the sub-product if it was running before update.
8. Check product runtime or API health.

## Remaining gap

Built artifact discovery, validation, staging, and guarded application are now
present in the Local API. The remaining `KAN-40` / `KAN-60` gaps are:

- release-hosted runtime artifacts for each sub-product;
- update-plan confirmation that each product reports `builtArtifactStatus=ok`;
- a user-facing UI flow that prefers built artifacts over source archives;
- release publication and end-to-end update-plan verification after artifacts
  are attached.

See also:

- [sub-product-distribution-artifacts.md](sub-product-distribution-artifacts.md)
