# Timeline-family Docker and port contract

This document defines Docker resource and local port rules for the Timeline
parent product and Timeline-family sub-products.

It is based on the current README and Docker Compose files in:

- `C:\apps\Timeline`
- `C:\apps\TimelineForAudio`
- `C:\apps\TimelineForWindowsCodex`
- `C:\apps\TimelineForChatGPT`
- `C:\apps\TimelineForImage`
- `C:\apps\TimelineForVideo`
- `C:\apps\TimelineForPC`

## Current facts

As of this check:

| Product | Runtime shape | Docker Compose | Local API port | Ollama dependency |
| --- | --- | --- | --- | --- |
| Timeline | parent web, helper, worker, optional Ollama | yes | yes | yes |
| TimelineForAudio | resident Docker worker with worker-hosted API | yes | yes | no |
| TimelineForWindowsCodex | Docker Compose worker with worker-hosted API | yes | yes | no |
| TimelineForChatGPT | Docker Compose worker with worker-hosted API | yes | yes | no |
| TimelineForImage | resident Docker worker with API | yes | yes | no |
| TimelineForVideo | resident Docker worker with worker-hosted API | yes | yes | no |
| TimelineForPC | Windows host Python API | no | yes | no |

Current Timeline defaults:

```text
Timeline Web:              19000
Timeline Helper:           19001-19010
Timeline-owned Ollama:     11434 when using the Ollama ecosystem default
```

Sub-product Compose project names are derived from `runtime.instanceName`, with
base names such as:

```text
timeline-for-audio
timeline-for-image
timeline-for-video
timeline-for-chatgpt
timeline-for-windows-codex
```

This keeps Docker resources separate when two copies of the same sub-product are
run on one PC. Local API ports must still be assigned explicitly per copy.

## Goal

Allow multiple copies of the same Timeline-family product to exist on one PC
without sharing the wrong Docker resources.

This matters for:

- development copy plus verification copy
- copied product directories
- different product versions running on the same machine
- Windows-hosted local API servers with configurable ports

## Portfolio port design

Use the `19000-19999` range as the Timeline-family local product range.

The range is intentionally split by owner and product responsibility rather
than by the ports that happen to exist today. Existing defaults may remain while
they fit the design, but new APIs should follow the reservation map below.

Top-level reservations:

| Range | Owner | Purpose |
| --- | --- | --- |
| `19000-19099` | Timeline parent product | Parent web UI, helper/control APIs, parent-owned adapters, and parent development ports |
| `19100-19699` | Default sub-products | One 100-port block per default sub-product |
| `19700-19899` | Future official products | Future Timeline-family products with stable product ids |
| `19900-19999` | Local development only | Manual experiments, one-off tests, and non-shipped defaults |

Parent Timeline reservation:

| Range | Purpose | Default or rule |
| --- | --- | --- |
| `19000` | Timeline Web UI | Current default web port |
| `19001-19010` | Timeline Helper API | Current helper fallback range |
| `19011-19019` | Future parent control APIs | Reserved; do not assign to sub-products |
| `19020-19029` | Parent worker or callback endpoints | Reserved; publish only if needed |
| `19030-19039` | Parent-owned adapter or dependency ports | Use for Timeline-owned adapters when the native external default is not appropriate |
| `19080-19099` | Parent development and smoke-test services | Not for shipped product defaults |

Default sub-product blocks:

| Product | Reserved range | Default API port |
| --- | --- | --- |
| TimelineForAudio | `19100-19199` | `19100` |
| TimelineForWindowsCodex | `19200-19299` | `19200` |
| TimelineForChatGPT | `19300-19399` | `19300` |
| TimelineForImage | `19400-19499` | `19400` |
| TimelineForVideo | `19500-19599` | `19500` |
| TimelineForPC | `19600-19699` | `19600` |

Within each sub-product block:

| Offset | Purpose |
| --- | --- |
| `+00` | Primary local API or web endpoint |
| `+01-+09` | Product helper/control APIs |
| `+10-+19` | Product-owned worker endpoints, if any |
| `+20-+39` | Product-owned adapters or embedded dependencies |
| `+40-+59` | Local callbacks, webhooks, or bridge services |
| `+60-+79` | Product development diagnostics |
| `+80-+99` | Reserved for future use |

Examples:

```text
TimelineForImage primary API:     19400
TimelineForImage helper API:      19401
TimelineForImage local adapter:   19420
TimelineForVideo primary API:     19500
TimelineForVideo diagnostics:     19560-19579
```

External dependency defaults are separate from the Timeline-family product
range. For example, an externally managed Ollama service may still use its
ecosystem default `11434`. If Timeline or a sub-product owns a separate adapter
or dependency endpoint for an instance, that published host port should either
be configurable or come from that product's reserved block.

## Resource classes

Treat Docker resources as either shared or instance-scoped.

Shared resources:

- External base images such as Python, Node, .NET, CUDA, or other public base
  images.
- External model/cache data only when the product explicitly supports sharing.

Instance-scoped resources:

- Compose project name
- Containers
- Networks
- Product-owned volumes
- Product-specific local build images
- Published host ports, if the product exposes an API later

If the resource contains product state, generated data, queues, app data, or
runtime cache, assume it is instance-scoped unless the product has a documented
reason to share it.

## Instance name

Each product copy should have a stable local instance name.

Rules:

- Generate it once on first start if missing.
- Persist it in local settings.
- Do not regenerate it on every launch.
- Normalize to lowercase ASCII letters, digits, and hyphens.

Example settings shape:

```json
{
  "runtime": {
    "instanceName": "local-0123abcd89"
  }
}
```

Recommended generated shape:

```text
local-<10 lowercase hex chars>
```

## Compose project names

Do not keep one fixed Compose project name when multiple copies may run.

Recommended pattern:

```text
<product-id>-<instance-name>
```

Examples:

```text
timeline-for-image-local-0123abcd89
timeline-for-video-local-4567efab90
```

PowerShell start shape:

```powershell
docker compose `
  --project-directory $ProductRoot `
  -p $ComposeProject `
  up -d --build
```

`$ComposeProject` should come from settings or be derived from the persisted
instance name.

## Containers and networks

Do not set fixed `container_name`.

Let Compose derive container and network names from the project name:

```text
<compose-project>-worker-1
<compose-project>_default
```

Rules:

- No fixed `container_name`
- No fixed ordinary network name
- No shared default network across copies of the same product
- No hard-coded container group name outside the derived Compose project

## Local build images

Product-specific build images should be instance-scoped when multiple copies of
the same product may be developed or run at the same time.

Current examples that need this treatment:

```text
timeline-for-audio-worker-cpu:latest
timeline-for-audio-worker-gpu:latest
timeline-for-image-worker:latest
timeline-for-video-worker:latest
timeline-for-video-worker-gpu:latest
timeline-for-windows-codex-worker
```

Recommended Compose shape:

```yaml
services:
  worker:
    image: timeline-for-image-worker:${TIMELINE_FOR_IMAGE_IMAGE_TAG:-latest}
```

Recommended runtime value:

```text
TIMELINE_FOR_IMAGE_IMAGE_TAG=local-0123abcd89
```

or:

```text
TIMELINE_FOR_IMAGE_IMAGE_TAG=timeline-for-image-local-0123abcd89
```

## Volumes

Product-owned volumes should be instance-scoped.

Current examples that should not be shared between copied product instances:

```text
app-data
cache-data
```

Recommended Compose shape:

```yaml
volumes:
  app-data:
    name: timeline-for-image-${TIMELINE_FOR_IMAGE_INSTANCE_NAME:-default}-app-data
  cache-data:
    name: timeline-for-image-${TIMELINE_FOR_IMAGE_INSTANCE_NAME:-default}-cache-data
```

Use shared cache volumes only when the sub-product documents that the data is
safe to share. Do not assume sharing is safe just because the volume is called
`cache-data`.

## Ports

The currently inspected Docker-based sub-products do not publish host ports from
Compose. Their Timeline-facing APIs run on the Windows host and connect to the
resident worker or generated output as needed.

Port rules apply to every sub-product local API and to any other published
service.

Rules:

- Every host port must be configurable.
- Bind local APIs to `127.0.0.1` by default.
- Provide one documented default port for normal users.
- Allow override through settings or environment variables.
- If the configured port is occupied, fail with a clear message or use a
  documented fallback range.
- Do not silently choose a random port unless the chosen port is persisted and
  visible to the user.

Recommended future Compose shape:

```yaml
services:
  api:
    ports:
      - "127.0.0.1:${TIMELINE_FOR_IMAGE_API_PORT:-19400}:8080"
```

Recommended future settings shape:

```json
{
  "runtime": {
    "apiPort": 19400
  }
}
```

The default API ports are the `+00` ports from the portfolio port map.

TimelineForPC runs on the Windows host and uses `19600` as its default primary
API port.

## Ollama

The inspected sub-products do not currently use Ollama as a runtime dependency.

Do not add Ollama settings to a sub-product unless that product actually uses
Ollama.

If a future sub-product does use Ollama:

- Treat it as an optional external dependency.
- Store only the base URL needed to connect to it.
- Do not store product data in an Ollama volume.
- Do not assume Timeline owns the Ollama process.
- If the sub-product starts its own Ollama container, its host port must be
  configurable and non-conflicting.

## Startup inputs

Each Docker-based sub-product startup script should derive Docker values from
settings and environment variables.

Recommended environment variables:

```text
<PRODUCT>_INSTANCE_NAME
<PRODUCT>_COMPOSE_PROJECT
<PRODUCT>_IMAGE_TAG
<PRODUCT>_API_PORT
```

Example:

```text
TIMELINE_FOR_IMAGE_INSTANCE_NAME=local-0123abcd89
TIMELINE_FOR_IMAGE_COMPOSE_PROJECT=timeline-for-image-local-0123abcd89
TIMELINE_FOR_IMAGE_IMAGE_TAG=timeline-for-image-local-0123abcd89
TIMELINE_FOR_IMAGE_API_PORT=19400
```

Require API port settings for Timeline-managed sub-products that expose the local API.

## Parent connection

Current parent-to-sub-product boundary:

```text
http://127.0.0.1:<apiPort>
```

The parent Timeline product must use the local API for product operations.
Host launchers should not be invoked for normal refresh, list, download,
remove, detail, model, or settings operations.

Minimum API shape expected by Timeline:

```text
GET  /health
POST /items/list
POST /items/refresh
POST /items/download
POST /settings/status
POST /settings/init
```

Optional API routes are product-specific, but the currently used optional
routes are:

```text
POST /items/remove
POST /items/detail
POST /models/list
```

`GET /health` is the running-state boundary. If it does not return a healthy
value, Timeline should report the product as stopped or unavailable instead of
starting Docker implicitly.

Product registry shape:

```json
{
  "id": "image",
  "displayName": "TimelineForImage",
  "path": "C:\\apps\\TimelineForImage",
  "connectionMode": "api",
  "apiBaseUrl": "http://127.0.0.1:19400"
}
```

## Runtime ownership

Runtime startup should remain explicit:

- `start.ps1` or `start.bat` starts Docker resources.
- `stop.ps1` or `stop.bat` stops Docker resources.
- Timeline's product manager may call those explicit start/stop entrypoints.

Read-only status or settings calls should not become hidden Docker ownership
points. This keeps Docker resource naming and port usage predictable.

## Migration checklist

For Docker-based sub-products:

1. Read the product README and runtime docs first.
2. Add a persisted `runtime.instanceName`.
3. Derive Compose project name from product id and instance name.
4. Use `docker compose -p <derived-project>`.
5. Remove fixed `name:` from Compose or override it consistently with `-p`.
6. Avoid fixed `container_name`.
7. Avoid fixed ordinary network names.
8. Scope product-owned volumes by instance name.
9. Scope product-specific local build image tags by instance name.
10. Make local API and any published host ports configurable.
11. Do not add Ollama settings unless the product actually uses Ollama.

For host-only sub-products such as TimelineForPC:

1. Do not add Docker settings just for consistency.
2. Keep host execution explicit.
3. Keep API port settings configurable.
4. Keep the host API thin. It may collect Windows-only facts and manage the
   local host process lifecycle, but normal Timeline integration still goes
   through HTTP API routes.
5. Treat normalization, report rendering, packaging, and other CPU-heavy or
   platform-neutral work as Docker offload candidates if they grow beyond the
   simple adapter role.

## Acceptance tests

Two copies of the same Docker-based sub-product should be able to coexist when
their instance names differ.

Test shape:

```powershell
# Copy A
$env:TIMELINE_FOR_IMAGE_INSTANCE_NAME = "local-a"
.\start.ps1

# Copy B
$env:TIMELINE_FOR_IMAGE_INSTANCE_NAME = "local-b"
.\start.ps1
```

Expected result:

- Different Compose projects
- Different containers
- Different networks
- Different product-owned volumes
- Different local build image tags
- No published port conflict

For products with a local API, also verify:

- Copy A and Copy B use different configured host ports.
- Both API base URLs are visible in settings or product registry.
