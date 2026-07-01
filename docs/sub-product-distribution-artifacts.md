# Sub-product distribution artifacts

This document tracks `KAN-60`.

Timeline should update sub-products from user-facing product artifacts, not
GitHub source archives. Source archives are still useful for development, but a
user update flow needs a predictable runtime ZIP that preserves user data and
can be validated before replacement.

## Artifact name

Sub-product artifacts use this name:

```text
<product-name>-<runtime>-<version>.zip
```

Examples:

```text
TimelineForAudio-win-x64-v0.4.11.zip
TimelineForImage-win-x64-v0.2.8.zip
TimelineForVideo-win-x64-v0.5.7.zip
TimelineForChatGPT-win-x64-v0.2.8.zip
TimelineForWindowsCodex-win-x64-v0.2.7.zip
TimelineForPcInfo-win-x64-v0.2.1.zip
```

Timeline's update plan endpoint looks for the same naming pattern in each
sub-product's latest GitHub Release.

## Artifact layout

Each ZIP contains exactly one product root directory:

```text
TimelineForAudio/
  VERSION
  README.md
  docker-compose.yml
  timeline-product.json
  worker/
  runtime/
```

The exact files differ by sub-product, but the top-level `VERSION` file is
required. It identifies the artifact as a Timeline sub-product artifact.

## Required metadata

`VERSION` is JSON:

```json
{
  "artifactType": "timeline_sub_product_artifact",
  "productId": "audio",
  "productName": "TimelineForAudio",
  "version": "v0.4.11",
  "commit": "abcdef0",
  "channel": "dev",
  "runtimeIdentifier": "win-x64",
  "createdAt": "2026-07-01T00:00:00.0000000Z"
}
```

## Excluded content

Artifacts must not include:

- `.git`, `.github`, `.docker`, `.runtime`, `.playwright-cli`;
- tests and local caches;
- `settings.json`;
- input/source material;
- generated output;
- logs;
- previous release ZIPs;
- Docker volumes or runtime state.

These are user data or development/runtime state, not immutable application
content.

## Builder

Timeline now includes a common sub-product artifact builder:

```powershell
dotnet run --project .\tools\Timeline.SubProductReleaseBuilder\Timeline.SubProductReleaseBuilder.csproj -- --help
```

```powershell
dotnet run --project .\tools\Timeline.SubProductReleaseBuilder\Timeline.SubProductReleaseBuilder.csproj -- `
  --repo C:\apps\TimelineForAudio `
  --product-name TimelineForAudio `
  --product-id audio `
  --runtime win-x64 `
  --output release\sub-products `
  --channel dev
```

The builder is C# and cross-platform. It does not use `bat`, `sh`, or
`command` files as the product entry. It only packages an existing sub-product
repository into a runtime artifact ZIP.

All current sub-products can be built in one run:

```powershell
dotnet run --project .\tools\Timeline.SubProductReleaseBuilder\Timeline.SubProductReleaseBuilder.csproj -- `
  --all `
  --products-root C:\apps `
  --runtime win-x64 `
  --output release\sub-products `
  --channel dev
```

The matrix mode currently targets:

- `TimelineForAudio`
- `TimelineForImage`
- `TimelineForVideo`
- `TimelineForChatGPT`
- `TimelineForWindowsCodex`
- `TimelineForPcInfo`

It writes a manifest next to the ZIPs:

```text
sub-product-artifacts-<runtime>.json
```

The manifest lists each product, source repository path, version, commit,
artifact path, runtime, size, and creation state. This file is intended as the
machine-readable handoff to validation, release attachment, and update planning.

## Artifact validation

Before creating a publish plan, validate the generated artifact ZIPs:

```powershell
dotnet run --project .\tools\Timeline.SubProductReleaseBuilder\Timeline.SubProductReleaseBuilder.csproj -- `
  --validate-artifacts `
  --runtime win-x64 `
  --output release\sub-products
```

The validation command reads `sub-product-artifacts-<runtime>.json`, opens each
ZIP, and verifies that:

- the artifact file exists and is not empty;
- the ZIP has a single product root directory;
- the product root contains the required `VERSION` and `timeline-product.json`
  files;
- `VERSION` matches the manifest product, version, and runtime;
- `timeline-product.json` matches the manifest product identity;
- settings, data directories, logs, temp files, nested ZIPs, runtime caches, and
  other excluded paths are not present.

It writes:

```text
sub-product-artifacts-validation-<runtime>.json
```

This command is read-only. Treat `invalidCount > 0` as a release blocker.

## Publish plan

Before publishing or attaching artifacts to GitHub Releases, generate a
read-only publish plan:

```powershell
dotnet run --project .\tools\Timeline.SubProductReleaseBuilder\Timeline.SubProductReleaseBuilder.csproj -- `
  --publish-plan `
  --runtime win-x64 `
  --output release\sub-products `
  --github-owner amano0406
```

For reliable GitHub API reads, set `GITHUB_TOKEN` or `GH_TOKEN` before running
the command. Without a token, GitHub may return rate-limit `403` responses; in
that case the plan records `release_check_failed` instead of guessing.

The publish plan reads `sub-product-artifacts-<runtime>.json`, checks the
matching GitHub Release for each artifact tag, and writes:

```text
sub-product-release-publish-plan-<runtime>.json
```

This command is read-only. It does not create releases, upload assets, delete
assets, or change repository settings.

Plan states:

| State | Meaning |
| --- | --- |
| `ready` | The GitHub Release and matching runtime asset already exist. |
| `asset_missing` | The GitHub Release exists, but the matching runtime ZIP is missing. |
| `release_missing` | The tag exists in the artifact manifest, but the GitHub Release for that tag is missing. |
| `release_check_failed` | GitHub could not be checked. Do not assume release or asset absence. |
| `artifact_not_created` | The manifest entry exists, but the artifact build itself did not complete. |

Each item includes the expected artifact name, release URL, current latest
release tag, existing asset names, and a suggested `gh release create` or
`gh release upload` command. The suggested command is an execution aid only;
publishing still requires explicit release approval.

## Publish preflight

Before running the publishing command, run a read-only preflight check:

```powershell
dotnet run --project .\tools\Timeline.SubProductReleaseBuilder\Timeline.SubProductReleaseBuilder.csproj -- `
  --publish-preflight `
  --runtime win-x64 `
  --output release\sub-products `
  --github-owner amano0406
```

The preflight reads `sub-product-artifacts-<runtime>.json` and checks:

- whether `GITHUB_TOKEN` or `GH_TOKEN` is present;
- whether GitHub API rate-limit information can be read;
- whether the token can identify the authenticated GitHub user;
- whether each target sub-product repository can be read.

It writes:

```text
sub-product-release-publish-preflight-<runtime>.json
```

This command is read-only. It does not create releases, upload assets, delete
assets, or change repository settings. `canPublish=true` means the local
preconditions look ready; it does not replace explicit release approval.

## Publish execution

Publishing is available as an explicit operation, but it is guarded because it
creates GitHub Releases and uploads release assets:

```powershell
dotnet run --project .\tools\Timeline.SubProductReleaseBuilder\Timeline.SubProductReleaseBuilder.csproj -- `
  --publish `
  --confirm-publish `
  --runtime win-x64 `
  --output release\sub-products `
  --github-owner amano0406
```

Requirements:

- explicit release approval has been given;
- `GITHUB_TOKEN` or `GH_TOKEN` is set with permission to create releases and
  upload release assets;
- the read-only publish plan has already been reviewed.

If `--confirm-publish` is omitted, the command stops before making any GitHub
changes. A successful publish writes:

```text
sub-product-release-publish-result-<runtime>.json
```

This first builder creates a cleaned product ZIP. It does not pre-build and
embed Docker images. Docker-based sub-products may still build their worker
image from the files inside the artifact when they start. If startup without
Docker build is required later, that should become a separate packaging step.

## Local verification on 2026-07-01

The builder produced local Windows artifacts for all six current sub-products:

| Product | Artifact | Size |
| --- | --- | ---: |
| TimelineForAudio | `TimelineForAudio-win-x64-v0.4.11.zip` | 0.10 MB |
| TimelineForImage | `TimelineForImage-win-x64-v0.2.8.zip` | 0.05 MB |
| TimelineForVideo | `TimelineForVideo-win-x64-v0.5.7.zip` | 0.13 MB |
| TimelineForChatGPT | `TimelineForChatGPT-win-x64-v0.2.8.zip` | 0.06 MB |
| TimelineForWindowsCodex | `TimelineForWindowsCodex-win-x64-v0.2.7.zip` | 0.06 MB |
| TimelineForPcInfo | `TimelineForPcInfo-win-x64-v0.2.1.zip` | 0.05 MB |

The verification checked that each ZIP contains a `VERSION` file and does not
contain excluded settings, data, logs, tests, or runtime cache paths.

Each generated ZIP was also accepted by the Timeline Local API validation
endpoint:

```text
GET /products/runtime/<productId>/update-artifact/validate?path=<zip-path>
```

All six artifacts returned `state=ready`, `valid=true`, `requiredEntries=2/2`,
with no blockers or warnings.

The builder was also verified in matrix mode. It created six artifacts and a
`sub-product-artifacts-win-x64.json` manifest. The artifacts listed in that
manifest were accepted by the Local API validation endpoint with
`state=ready`, `valid=true`, and `requiredEntries=2/2`.

The publish plan was also verified. On 2026-07-02, all six generated artifacts
returned `release_missing` because the corresponding tags existed, but the
latest GitHub Releases still pointed at older tags:

| Product | Artifact tag | Latest GitHub Release |
| --- | --- | --- |
| TimelineForAudio | `v0.4.11` | `v0.4.6` |
| TimelineForImage | `v0.2.8` | `v0.2.4` |
| TimelineForVideo | `v0.5.7` | `v0.5.4` |
| TimelineForChatGPT | `v0.2.8` | `v0.2.6` |
| TimelineForWindowsCodex | `v0.2.7` | `v0.2.5` |
| TimelineForPcInfo | `v0.2.1` | `v0.2.0` |

Timeline also exposes a read-only apply plan endpoint:

```text
GET /products/runtime/<productId>/update-artifact/apply-plan?path=<zip-path>
```

This endpoint is the bridge from "artifact is structurally valid" to "this
machine can safely apply this artifact now". It blocks application when the
current product path is not under Timeline-managed product locations, when the
product has local Git changes, or when required runtime files are missing.

## Remaining work

- Publish or attach these artifacts to the corresponding sub-product GitHub
  Releases.
- Confirm that Timeline's update plan reports `builtArtifactStatus=ok` after
  release artifacts are attached.
- Run an end-to-end update against a Timeline-managed product installation
  location, not the development repositories under `C:\apps`.

The legacy source-archive update execution path has been removed from the
normal user-facing updater. Product management now treats source-archive
version differences as diagnostic information and uses the built artifact
update plan and guarded artifact apply flow for normal updates.
