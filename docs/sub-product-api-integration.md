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
- `scripts/smoke-audio-api-download.ps1`
  - uses the TimelineForAudio API for direct product download checks

## Completed follow-up

Worker-internal Python operation modules are named `operations.py` in the
sub-products. TimelineForAudio, TimelineForChatGPT, TimelineForImage, and
TimelineForVideo now serve their normal local API directly from the resident
worker container, so those paths no longer spawn a Python operation process per
API request. Other products still keep operation runners behind their local API
boundary while start / stop remains the manifest launcher surface.
