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

## Remaining work

- Publish or attach these artifacts to the corresponding sub-product GitHub
  Releases.
- Replace the existing source-archive update execution path with a built
  artifact execution path.
