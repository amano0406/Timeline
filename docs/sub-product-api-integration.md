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
| TimelineForPC | `http://localhost:19600` | start / stop / service setup | API |

## Checks

- `scripts/check-product-api-contracts.ps1`
  - probes each product's `/health` endpoint
  - runs read-only API checks for settings and item listing when the product is
    already running
  - does not start or stop products
  - skips products whose API is not reachable unless `-RequireRunning` is used
- `scripts/check-sub-product-cli-removal.ps1`
  - verifies sub-product manifests expose only runtime launchers
  - rejects legacy CLI entrypoint filenames and old operation module names
  - scans source files for retired Timeline-to-product command dispatch names
  - does not start or stop products
- `scripts/smoke-audio-api-download.ps1`
  - uses the TimelineForAudio API for direct product download checks

## Completed follow-up

TimelineForAudio, TimelineForChatGPT, TimelineForImage, TimelineForVideo, and
TimelineForWindowsCodex now serve their normal local API directly from the
resident worker container, so those paths no longer spawn a Python operation
process per API request. TimelineForPC serves its normal local API from a
resident Python process on the Windows host and calls the capture / item
functions in-process. The normal Timeline integration path no longer uses
product CLI command runners; start / stop remains the manifest launcher
surface.

Legacy host-side C# API projects, Dockerfiles for those APIs, and package-level
`__main__.py` CLI entrypoints have been removed from the sub-products. Legacy
worker `operations.py` CLI aggregators were also removed where they existed;
API-only helper modules remain as normal in-process service code. Remaining
process execution inside products is domain work such as ffmpeg probing or
Windows PC collection, not Timeline-to-product command dispatch.
