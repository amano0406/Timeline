# Sub-product update plan

This document tracks the safe sub-product update direction for `KAN-58`.

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
| `legacy_source_archive` | Update uses GitHub tag source archive ZIP. | Transitional only. |
| `built_product_artifact` | Update uses a built runtime artifact. | Target direction. |
| `unknown` | Product source type is missing or not recognized. | Block or warn. |

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
It does not fall back silently to a source archive for the future user-facing
update path.

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

## Important gap

The current actual updater still uses `github-source-archive` when the product
registry is configured that way.

For a complete `KAN-58`, the next implementation should introduce built
artifact discovery and validation for sub-products, then either disable or
demote source archive update for normal users.

Artifact discovery and local artifact validation are now present. The remaining
gap is a UI flow and release-hosted artifacts. The Local API now has validation,
staging, and a guarded built-artifact application endpoint.

See also:

- [sub-product-distribution-artifacts.md](sub-product-distribution-artifacts.md)
