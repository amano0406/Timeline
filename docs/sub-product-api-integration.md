# Sub-product API Integration

## Goal

Timeline operates sub-products through each product's local API. Host-side
product command launchers are not part of the normal integration contract.

## Current product integration status

| Product | API base URL from manifest | Manifest command surface | Timeline normal operation |
| --- | --- | --- | --- |
| TimelineForAudio | `http://localhost:19100` | start / stop | API |
| TimelineForWindowsCodex | `http://localhost:19200` | start / stop | API |
| TimelineForChatGPT | `http://localhost:19300` | start / stop | API |
| TimelineForImage | `http://localhost:19400` | start / stop | API |
| TimelineForVideo | `http://localhost:19500` | start / stop | API |
| TimelineForPcInfo | `http://localhost:19600` | start / stop / service setup | API |

## Checks

- `scripts/check-product-api-contracts.ps1`
  - probes each product's `/health` endpoint
  - runs read-only API checks for settings and item listing when the product is
    already running
  - does not start or stop products
  - skips products whose API is not reachable unless `-RequireRunning` is used
- `scripts/check-sub-product-cli-removal.ps1`
  - verifies sub-product manifests expose only runtime launchers
  - rejects retired entrypoint filenames and old operation module names
  - scans source files for retired Timeline-to-product command dispatch names
  - does not start or stop products
- `scripts/smoke-audio-api-download.ps1`
  - uses the TimelineForAudio API for direct product download checks

## Completed follow-up

TimelineForAudio, TimelineForChatGPT, TimelineForImage, TimelineForVideo, and
TimelineForWindowsCodex now serve their normal local API directly from the
resident worker container, so those paths no longer spawn a Python operation
process per API request. TimelineForPcInfo serves its normal local API from a
resident Python process on the Windows host and calls the capture / item
functions in-process. The normal Timeline integration path no longer uses
product command runners; start / stop remains the manifest launcher
surface.

Legacy host-side C# API projects, Dockerfiles for those APIs, package-level
entrypoint files, and worker operation aggregators have been removed from the
sub-products. API-only helper modules remain as normal in-process service code.
Remaining process execution inside products is domain work such as ffmpeg
probing or Windows PC collection, not Timeline-to-product command dispatch.

## Host-only product boundary

Host-only products are allowed only when the source data cannot be collected
from a Docker worker. TimelineForPcInfo is the current example because it captures
the current Windows machine state and current-user autostart state.

For those products:

- The host process must expose a local HTTP API and a health endpoint.
- Timeline must not call product operation command runners.
- Host-side code should stay limited to Windows-only collection and process
  lifecycle.
- Platform-neutral work such as normalization, report rendering, and archive
  packaging should be treated as a future Docker offload candidate if it becomes
  heavy.
