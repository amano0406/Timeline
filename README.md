# Timeline

Timeline is the local parent UI, product manager, and cross-product timeline
store for Timeline sub-products.

Timeline does not contain the conversion engines. Each sub-product remains a
separate local product with its own public Windows-side entry points such as
`cli.ps1`, `start.ps1`, and `stop.ps1`. Timeline coordinates those entry points
and builds Timeline-owned data for review, scan, download, and later LLM
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
- TimelineForPC

Sub-products are expected to expose a stable launcher/CLI contract. Newer
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
```

`settings.json` lives in the Timeline product directory. The `dataRoot` setting
controls the data root.

## Main Operations

- Dashboard: current state, warnings, next actions, and Timeline growth.
- Scan: refresh product data through product CLIs and rebuild Timeline data.
- Settings: Timeline settings and product-specific settings.
- Product management: install, uninstall, start, stop, restart, and runtime
  status through product launchers.
- Product list/detail pages: confirm imported audio, video, image, thread, and
  PC-state records.

## Operation Rules

- Use each sub-product's public Windows-side launcher or CLI.
- Do not call Docker directly for a sub-product from Timeline.
- Do not delete original user source files as part of product uninstall.
- Product generated data is kept by default during uninstall unless the user
  explicitly selects generated-data removal.
- Timeline store download packages the already rebuilt Timeline store. It does
  not recollect sub-product data at download time.

## Checks

```powershell
.\scripts\check-powershell-ascii.ps1
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
- [docs/timeline-llm-data-rules.html](docs/timeline-llm-data-rules.html):
  Timeline master data, LLM input data, and generated result separation.
