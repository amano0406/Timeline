# Timeline

Timeline is the local parent UI, product manager, and cross-product timeline
store for Timeline sub-products.

Timeline does not contain the conversion engines. Each sub-product remains a
separate local product with its own public Windows-side entry points such as
`start.ps1`, `stop.ps1`, and a local product API. Timeline coordinates
the public product API and launchers, then builds Timeline-owned data for review, scan, download, and later LLM
workflows.

Timeline must not enter or operate a sub-product Docker container directly.

## Start

```powershell
cd <Timeline>
.\start.ps1
```

For development checks without opening a browser tab:

```powershell
.\start.ps1 -NoOpen
```

Open:

```text
http://127.0.0.1:19000
```

The first `start.ps1` run creates a stable local `runtime.instanceName` in
`settings.json` when it is missing or empty. Docker project names, containers,
networks, and local build image tags are derived from that instance name. Ports
remain explicit settings so a developer can run multiple copies by assigning
different ports.

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

External base images such as `ollama/ollama:latest` may be shared. Timeline's
own containers, networks, and local build image tags are scoped from the
configured instance. Published ports must be changed manually when running more
than one copy at the same time. See
[docs/docker-runtime-rulebook.md](docs/docker-runtime-rulebook.md).

Stop:

```powershell
.\stop.ps1
```

## Product Scope

Default sub-products:

- TimelineForAudio
- TimelineForVideo
- TimelineForImage
- TimelineForWindowsCodex
- TimelineForChatGPT
- TimelineForPcInfo

Sub-products are expected to expose a stable local API plus start/stop launchers. Newer
products should also ship `timeline-product.json` so Timeline can avoid
hard-coded assumptions about product identity, launchers, settings, generated
data, and uninstall policy.

## Data Layout

Timeline's local data root defaults to:

```text
<Timeline>\data
```

Main directories under the data root:

```text
data\products\<sub-product>
data\work
data\to_text\<sub-product>
data\to_timeline
data\to_timeline\derived
data\logs
data\backups
data\test
```

Main Timeline store files:

```text
to_timeline\manifest.json
to_timeline\items.jsonl
to_timeline\events.jsonl
to_timeline\rebuilds\<rebuild-id>\
to_timeline\derived\item_summaries\
```

`settings.json` lives in the Timeline product directory. The `dataRoot` setting
controls the data root.

## Timeline-Derived Data

Timeline does more than collect product outputs. During scan and post-scan
processing, Timeline creates derived data that is easier for humans and LLMs to
use while keeping the original product data as the source of truth.

- Speech-derived events can be verbalized with nearby Timeline context. Audio
  and video speech segments are not treated as complete truth by themselves;
  Timeline can use surrounding events, files, threads, PC activity, and other
  speech-derived records as hints before storing a refined text candidate.
- Item summaries are stored as Timeline-owned derived data. Audio files, video
  files, ChatGPT threads, Windows Codex threads, and other supported items can
  have a short summary and a detailed summary so later search or LLM workflows
  can inspect the summary before reading the full item.
- Derived data must keep provenance. A summary or verbalized text is useful
  input, but it is not the original source file, raw transcript, OCR result, or
  thread body.
- Timeline store downloads include the rebuilt Timeline store and derived data.
  They do not include the original large media/source files unless a
  sub-product explicitly exported them.

## Main Operations

- Dashboard: current state, warnings, next actions, and Timeline growth.
- Scan: refresh product data through product APIs and rebuild Timeline data.
- Post-scan derived processing: create speech verbalization and item summaries
  for later search, review, and LLM workflows.
- Settings: Timeline settings and product-specific settings.
- Product management: install, uninstall, start, stop, restart, and runtime
  status through product launchers.
- Product list/detail pages: confirm imported audio, video, image, thread, and
  PC-state records.

## Operation Rules

- Use each sub-product's public Windows-side API and start/stop launchers.
- Do not call Docker directly for a sub-product from Timeline.
- Do not start a sub-product as a side effect of a read API call. Stopped
  products must stay stopped until the user runs an explicit start action.
- Do not delete original user source files as part of product uninstall.
- Product generated data is kept by default during uninstall unless the user
  explicitly selects generated-data removal.
- Timeline store download packages the already rebuilt Timeline store. It does
  not recollect sub-product data at download time.

## Checks

```powershell
.\scripts\check-powershell-ascii.ps1
.\scripts\check-sub-product-cli-removal.ps1
.\scripts\smoke-thread-detail-api-bridge.ps1
.\scripts\smoke-web.ps1
.\scripts\test-product-uninstall.ps1
```

Web build check:

```powershell
docker compose build web
```

## Detailed Docs

- [docs/MAINTENANCE.md](docs/MAINTENANCE.md): current internal maintenance
  notes and active non-contract risks.
- [docs/timeline-product-manifest.md](docs/timeline-product-manifest.md):
  sub-product manifest contract.
- [docs/product-uninstall-design.md](docs/product-uninstall-design.md):
  product uninstall policy and safety model.
- [docs/docker-runtime-rulebook.md](docs/docker-runtime-rulebook.md):
  Docker project names, ports, images, volumes, and API connection rules.
- [docs/sub-product-docker-port-contract.md](docs/sub-product-docker-port-contract.md):
  Docker project, image, volume, and API port rules for sub-products.
- [docs/timeline-llm-data-rules.html](docs/timeline-llm-data-rules.html):
  Timeline master data, LLM input data, and generated result separation.
