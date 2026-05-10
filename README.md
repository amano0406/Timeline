# Timeline

Timeline is the local parent UI, product manager, and cross-product timeline
store for Timeline products.

Timeline does not contain the sub-product engines. Each sub-product remains a
separate local product with its own `cli.ps1`, settings, workers, and generated
data. Timeline coordinates those products through their public Windows-side
entry points and then builds Timeline-owned data that can be scanned, reviewed,
downloaded, and prepared for later LLM analysis.

Timeline may read generated files that a sub-product has already produced, but
it must not enter or operate a sub-product Docker container directly.

## Supported Products

The default product registry points to the local products under `C:\apps`:

- `C:\apps\TimelineForAudio`
- `C:\apps\TimelineForVideo`
- `C:\apps\TimelineForImage`
- `C:\apps\TimelineForWindowsCodex`
- `C:\apps\TimelineForChatGPT`
- `C:\apps\TimelineForPC`

Each product is expected to expose a `cli.ps1`. Newer products should also ship
a `timeline-product.json` manifest so Timeline can understand product identity,
repository location, release asset naming, runtime behavior, and uninstall
policy without hard-coded assumptions.

Manifest design:

```text
docs\timeline-product-manifest.md
```

Sub-product management design:

```text
docs\sub-product-management-design.html
docs\product-uninstall-design.md
```

Strategy notes:

```text
docs\future-product-roadmap.html
docs\monetization-and-product-strategy-notes.html
docs\timeline-llm-data-rules.html
```

## Current Scope

Timeline currently has four practical roles:

1. Show whether required settings, products, and background services are ready.
2. Let the user scan available source products and confirm what has been
   imported.
3. Rebuild Timeline's own cross-product store from sub-product exports.
4. Manage local sub-products: install, uninstall, start, stop, restart, and
   inspect runtime status.

Sub-product list pages are intentionally confirmation-oriented. They are not the
main analysis surface. Their job is to make it easy to confirm that source files,
threads, images, videos, and PC-state records are being detected and converted.

List pages use normal page-based paging rather than infinite scroll. Timeline
can revisit infinite or virtual scrolling later after search, date filtering,
and timeline analysis workflows become mature enough to justify it.

## Start

Use PowerShell from the repository directory:

```powershell
cd C:\apps\Timeline
.\start.ps1
```

For development checks where you do not want to open another browser tab:

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

`start.bat` and `stop.bat` are also available for users who prefer double-click
entry points.

## Screens

- Dashboard
  - Shows important warnings, suggested next actions, Timeline growth, and
    source-status summaries.
  - It should help the user decide what to check next, not expose low-level
    product controls.
- Scan
  - The main operational page for keeping Timeline data current.
  - The single "scan" action refreshes product data through product CLIs,
    rebuilds the Timeline store, and runs Timeline-owned preparation steps such
    as speech verbalization when eligible.
  - Source cards open the confirmation lists for audio, video, image,
    Windows Codex, ChatGPT, and PC state.
- Settings modal
  - Basic mode: display language, time zone, Timeline data directory, shared AI
    settings, Hugging Face token, and input directories.
  - Advanced mode: Timeline and product-specific settings that map more closely
    to each product's `settings.json`.
- Product management modal
  - Shows installed/missing products, configured locations, runtime state, and
    available actions.
  - Supports install, uninstall, start, stop, restart, and status refresh.
- Audio files
  - File tree, paging, generated-data delete/download, audio detail view,
    playback, and speech verbalization state.
- Video files
  - File list/detail, generated-data download/delete where supported, and
    speech verbalization state.
- Image files
  - File list/detail, generated-data download/delete where supported.
- Windows Codex
  - Thread list, thread detail view, Markdown-rendered chat display, generated
    data download/delete where supported.
- ChatGPT
  - ZIP upload when needed, thread list, thread detail view, Markdown-rendered
    chat display, generated-data download/delete where supported.
- PC state
  - Item list for TimelineForPC records. Detail view is intentionally deferred
    until the product data needs a richer UI.

Shared list paging looks like this:

```text
1 - 100 / 41,596 items
1 / 416 pages
First / Previous / 1 2 3 4 5 / Next / Last
```

Selection operations apply to the currently displayed page unless a button says
otherwise, such as "download all".

## Timeline Store

Timeline has its own cross-product store for normalized timeline data.

Default root:

```text
C:\TimelineData\Timeline
```

Timeline manages these subdirectories under the root:

```text
C:\TimelineData\Timeline\work
C:\TimelineData\Timeline\store
C:\TimelineData\Timeline\logs
```

Main store files:

```text
store\manifest.json
store\items.jsonl
store\events.jsonl
store\rebuilds\<rebuild-id>\
```

The scan page rebuilds this store by downloading from each sub-product through
that product's `cli.ps1` into Timeline's work directory, then normalizing the
results into `items.jsonl` and `events.jsonl`.

The Timeline ZIP download packages the already rebuilt Timeline store. It does
not silently recollect sub-product data at download time.

Timeline separates three data layers:

1. Timeline master data: original timeline facts, raw references, and
   conversion provenance.
2. LLM input data: text-first material prepared from the master data for
   analysis, report generation, search answers, and article generation.
3. LLM generated results: derived reports, analysis text, hypotheses, and next
   actions.

Directly hard-to-read intermediate data such as audio/video phone tokens,
pre-OCR images, video frames, and binary files belongs in Timeline master data
or raw references. Normal report/analysis LLM inputs should use readable text
representations such as verbalized speech, OCR text, image descriptions, thread
messages, and operation summaries.

Rules for this split are kept in:

```text
docs\timeline-llm-data-rules.html
```

## Speech Verbalization

Timeline can verbalize phone-token timelines from TimelineForAudio and
TimelineForVideo into readable candidate text. This belongs to Timeline rather
than the source products because nearby Timeline context and previous chunk
results can be used as weak hints.

The feature is still quality-gated. The implementation supports queued chunk
processing, but product-level behavior may intentionally limit the amount of
work until output quality is acceptable.

Current implementation:

- Creates 5-10 minute chunk plans from speech timeline turns.
- Writes per-chunk `context/*.context.json` and `summary.json` files under the
  Timeline store.
- Queues long-running LLM work in PowerShell workers so the Web request can
  return quickly.
- Calls the Timeline-owned Ollama Docker service with JSON output.
- Uses nearby Timeline text candidates and previous verbalization results as
  weak hints.
- Stores completed or failed results under Timeline's store.
- Manages the Ollama URL, model, chunk size, and concurrency internally.

Default model:

```text
qwen3.5:9b
```

`start.ps1` starts Ollama through `docker-compose.yml` and pulls the default
model on first run. The model data is stored in the `ollama` Docker volume.
Timeline exposes Ollama only on localhost:

```text
http://127.0.0.1:11434
```

## Operation Logs

Timeline writes persistent operation logs for incident review under:

```text
C:\TimelineData\Timeline\logs\operations\<operation-id>\
```

Each operation directory contains:

```text
events.jsonl
summary.json
```

Web actions create parent operation records. CLI calls launched while handling
that Web action create child records with `parentOperationId`, so an incident
can be traced from the button/API operation to the product `cli.ps1` command,
exit code, stdout/stderr tail, and worker state changes.

These logs are internal diagnostic data. The user-facing UI should not rely on a
visible console panel.

The Web operation checklist is kept in:

```text
docs\operation-log-web-test-checklist.md
```

## Sub-product Operation Rules

Timeline must operate sub-products through their public Windows-side entry
points.

Allowed:

- Run a sub-product `cli.ps1` from Windows-side helper/worker code.
- Run PowerShell sub-product CLI scripts through
  `scripts\invoke-product-cli-utf8.ps1` so redirected JSON keeps UTF-8 text and
  remains parseable.
- Read files generated by sub-products when Timeline needs to display or package
  already-created results.
- Use Timeline's own Docker services defined in this repository.
- Use a product's `start.ps1` and `stop.ps1` for product runtime control when
  the product supports them.

Not allowed:

- Enter a sub-product Docker container.
- Run commands, Python, or shell processes inside a sub-product Docker
  container.
- Silently fall back to product output directories when a required `cli.ps1`
  download operation fails.
- Write downloaded data into another product's application directory.

Timeline-managed uninstall uses Timeline's product registry and uninstall plan.
It does not call a sub-product `uninstall.ps1`; that script remains a standalone
product concern. The default uninstall target is the configured product
application directory. Master data and generated data are handled by explicit
user choices in the uninstall flow.

## Product Source Archives

Timeline and the sub-products are distributed from public GitHub tags by using
GitHub's automatically generated source archives. This keeps installation simple
for normal users: Git and a GitHub account are not required for public
repositories.

Example:

```text
https://github.com/amano0406/TimelineForAudio/archive/refs/tags/v0.4.7.zip
```

Timeline stores the GitHub repository URL in product settings and resolves the
latest tag to the matching source archive when installing or updating a
sub-product. The local helper reads the public tags feed first and only falls
back to the GitHub API when needed, so normal use does not require a GitHub
account or Git.

The source archive should contain the product files needed for local execution.
Local settings, generated data, Docker volumes, caches, and build output should
not be committed to the repository, so they are not included in the GitHub source
archive.

Distribution rules are documented in:

```text
docs\timeline-product-manifest.md
```

## Smoke Checks

After starting Timeline, verify the web routes and sub-product `cli.ps1`
contracts:

```powershell
.\scripts\check-powershell-ascii.ps1
.\scripts\smoke-web.ps1
.\scripts\check-product-cli-contracts.ps1
```

To include ZIP download creation checks, run:

```powershell
.\scripts\check-product-cli-contracts.ps1 -IncludeDownloads
```

If this check fails, fix the target product's `cli.ps1` contract or output path
handling. Timeline should not silently fall back to reading product output
directories.

For the focused TimelineForAudio download path check, run:

```powershell
.\scripts\smoke-audio-ps1-download.ps1
```

For uninstall behavior, use a test fixture or backed-up product directory before
running:

```powershell
.\scripts\test-product-uninstall.ps1
```

## Structure

- `web/`: Blazor Web App
- `scripts\timeline-helper-server.ps1`: Windows-side local helper server
- `scripts\timeline-store-worker.ps1`: Windows-side Timeline store rebuild worker
- `scripts\audio-verbalization-worker.ps1`: speech verbalization worker
- `scripts\audio-verbalization-bulk-worker.ps1`: queued verbalization worker
- `worker/`: Timeline-owned Docker worker. It monitors the store and writes
  heartbeat status.
- `docker-compose.yml`: Docker startup for the web UI, Timeline worker, and
  Ollama

The Docker worker belongs to Timeline. It is not a layer for directly operating
sub-product Docker containers.

## Responsive UI

The UI supports desktop and smartphone widths.

- Desktop uses a fixed left sidebar.
- Smartphone widths use a top bar and off-canvas sidebar.
- List cards keep the page height controlled by scrolling list contents inside
  the list card.
- Table headers stay visible inside scrollable list containers.

## PowerShell Encoding Guard

Timeline still supports Windows PowerShell 5.1 entry points. Keep all `.ps1`
files ASCII-only, except for an optional UTF-8 BOM at the start of the file.
Windows PowerShell 5.1 can misread UTF-8 without BOM and break parsing when
Japanese text is embedded in scripts.

Put user-facing Japanese text in Blazor/C# UI files or JSON resources, not in
PowerShell scripts. `start.ps1` and `stop.ps1` run
`scripts\check-powershell-ascii.ps1` before loading helper scripts.
