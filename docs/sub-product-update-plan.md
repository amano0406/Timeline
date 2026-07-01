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
