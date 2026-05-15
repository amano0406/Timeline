# Docker runtime rulebook

This rulebook defines how Timeline-family products should name Docker resources,
publish ports, and prepare for future API-based product connections.

## Goals

- Multiple Timeline-family products can exist on the same PC.
- A developer can run more than one copy when ports are configured explicitly.
- Product data is not mixed by accident.
- Shared base images and model caches can be reused when that is operationally safe.
- Sub-products can later move from `cli.ps1` integration to API integration without
  changing the parent product boundary.

## Resource classes

Treat Docker resources in two different classes.

Shared resources:

- External base images such as `ollama/ollama:latest`.
- Optional model cache volumes for products that actually use a model cache.

Instance resources:

- Compose project names.
- Containers.
- Networks.
- Product data volumes.
- Product-specific local build images.
- Published host ports.

Shared resources may be reused across products. Instance resources must be
separated by an instance name whenever more than one copy may run on the same PC.

## Timeline runtime settings

Timeline reads the `runtime` block in `settings.json`. On first `start.ps1`,
Timeline creates this block when it is missing and generates a stable local
`instanceName` when it is empty.

```json
{
  "runtime": {
    "instanceName": "local-0123abcd89",
    "imageTag": "",
    "webPort": 19000,
    "helperPortStart": 19001,
    "helperPortEnd": 19010,
    "ollamaPort": 11434,
    "ollamaModel": "qwen3.5:9b",
    "shareOllamaVolume": true,
    "ollamaVolumeName": "timeline-ollama"
  }
}
```

The generated `instanceName` is persisted. It must not change on every launch.

If `instanceName` is still empty before initialization:

- Compose project: `timeline`
- Web: `http://127.0.0.1:19000`
- Helper: first free port from `19001` through `19010`
- Ollama: `http://127.0.0.1:11434`
- Local build image tag: `latest`
- Ollama volume: `timeline-ollama`

When `instanceName` is set:

- Compose project: `timeline-<instanceName>`
- Local build image tag: `timeline-<instanceName>` unless `imageTag` is set.
- The user must configure non-conflicting ports if another instance is already
  running.

`instanceName` is normalized to lowercase ASCII letters, digits, and hyphens.

## Compose rules

Compose files in Timeline-family products should follow these rules:

- Do not set fixed `container_name`.
- Do not set fixed network names for ordinary app networks.
- Use Compose project names for container, network, and anonymous volume scoping.
- Use environment variables for host ports.
- Use environment variables for local build image tags.
- Keep external base image names stable when sharing is acceptable.

Recommended local image pattern:

```yaml
image: product-name:${PRODUCT_IMAGE_TAG:-latest}
```

Recommended port pattern:

```yaml
ports:
  - "127.0.0.1:${PRODUCT_WEB_PORT:-19000}:8080"
```

## Ollama in Timeline

Timeline itself uses Ollama for audio verbalization. The inspected sub-products
do not currently use Ollama as a runtime dependency.

For Timeline, Ollama is intentionally treated as a shared-capable dependency.

- The `ollama/ollama:latest` image may be shared.
- The Ollama model cache volume may be shared.
- Product data must not be stored in the Ollama volume.
- If multiple Ollama containers are started, each instance must use a unique host
  port.
- If a product uses an already-running Ollama service, it should store only the
  base URL, not assume ownership of that service.

Timeline's default shared Ollama volume is `timeline-ollama`.

Do not add Ollama settings to a sub-product unless that sub-product actually
uses Ollama.

## Sub-product connection model

The current parent-to-sub-product integration is `cli.ps1`.

Timeline must not start a sub-product as a side effect of reading data or
serving an API request. Only explicit runtime actions such as
`/products/runtime/<product>/start` may start a sub-product.

If a sub-product is stopped:

- Runtime-dependent API calls must return an unavailable/stopped result.
- Timeline may still read Timeline-owned cached data or local settings when that
  does not call the sub-product runtime.
- Timeline must not call a sub-product CLI just to discover whether data is
  available.

The future target is API integration:

- Each sub-product may expose its own local API server.
- Each sub-product should define a default API port.
- Timeline should allow users to override the sub-product API base URL or port.
- Timeline should keep using a connector boundary instead of directly touching a
  sub-product's Docker containers or internal files.

Until a sub-product API contract is defined, do not implement API calls in
Timeline. Keep the existing `cli.ps1` integration as the operational path.

Future setting shape:

```json
{
  "productRegistry": {
    "products": [
      {
        "id": "audio",
        "displayName": "TimelineForAudio",
        "path": "C:\\apps\\TimelineForAudio",
        "connectionMode": "cli",
        "apiBaseUrl": "http://127.0.0.1:19101"
      }
    ]
  }
}
```

`connectionMode` and `apiBaseUrl` are design placeholders until the API contract
is finalized.

See also
[sub-product-docker-port-contract.md](sub-product-docker-port-contract.md) for
the Docker project, image, volume, and future API port rules that sub-products
should follow.

## Operational guidance

For normal users, keep defaults.

For developers running multiple copies:

1. Keep each copy's generated `runtime.instanceName`, or set a unique one
   manually.
2. Set a unique `runtime.webPort`.
3. Set a unique helper port range.
4. Set a unique `runtime.ollamaPort` or point the product at an external Ollama
   service.
5. Keep `runtime.shareOllamaVolume` enabled when the goal is to avoid repeated
   model downloads.

Do not randomize resource names on every launch. Generate once, persist the
instance name, and keep port choices explicit so the operator can reason about
ports, data, and running instances.
