# Sub-product host launcher removal

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
- `scripts/check-product-cli-contracts.ps1`
  - kept only as a compatibility wrapper
  - delegates to `check-product-api-contracts.ps1`
- `scripts/smoke-audio-ps1-download.ps1`
  - kept only as a compatibility filename
  - uses the TimelineForAudio API for direct product download checks

## Remaining follow-up

The worker-internal Python modules named `cli.py` are not host launchers. The
local C# APIs still execute product commands through Docker worker entrypoints,
and some of those entrypoints are implemented by Python command modules
internally. Removing or renaming those internal modules is a separate worker
refactor.
