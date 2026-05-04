# Timeline

Timeline is the local parent UI for Timeline products.

This repository does not contain the sub-product engines. It connects to existing local products under `C:\apps`:

- `C:\apps\TimelineForAudio`
- `C:\apps\TimelineForWindowsCodex`
- `C:\apps\TimelineForChatGPT`
- `C:\apps\TimelineForImage`

Timeline operates sub-products through each product's `cli.ps1`. It may read generated output files, but it must not enter or operate sub-product Docker containers directly.

## Start

```powershell
cd C:\apps\Timeline
.\start.ps1
```

Open:

```text
http://127.0.0.1:19000
```

Stop:

```powershell
.\stop.ps1
```

## Smoke checks

After starting Timeline, verify the web routes and sub-product `cli.ps1` contracts:

```powershell
.\scripts\check-powershell-ascii.ps1
.\scripts\smoke-web.ps1
.\scripts\check-product-cli-contracts.ps1
```

To include ZIP download creation checks, run:

```powershell
.\scripts\check-product-cli-contracts.ps1 -IncludeDownloads
```

If this check fails, fix the target product's `cli.ps1` contract or output path handling. Timeline should not silently fall back to reading product output directories.

For the focused TimelineForAudio download path check, run:

```powershell
.\scripts\smoke-audio-ps1-download.ps1
```

## Timeline Store

Timeline has its own cross-product store for normalized timeline data.

Default locations:

```text
C:\TimelineData\Timeline\store
C:\TimelineData\Timeline\work
```

Main files:

```text
store\manifest.json
store\items.jsonl
store\events.jsonl
store\rebuilds\<rebuild-id>\
```

The Timeline page rebuilds this store by downloading from each sub-product through
that product's `cli.ps1` into the Timeline work directory, then normalizing the
results into `items.jsonl` and `events.jsonl`.

The store ZIP download packages the already rebuilt Timeline store. It does not
silently recollect sub-product data at download time.

## Structure

- `web/`: Blazor Web App
- `scripts/timeline-helper-server.ps1`: Windows-side local helper server
- `scripts/timeline-store-worker.ps1`: Windows-side Timeline store rebuild worker
- `worker/`: Timeline-owned Docker worker. It currently monitors the store and writes heartbeat status
- `docker-compose.yml`: Docker startup for the web UI and Timeline worker

The Docker worker belongs to Timeline. It is not a layer for directly operating sub-product Docker containers.

## PowerShell encoding guard

Timeline still supports Windows PowerShell 5.1 entry points. Keep all `.ps1`
files ASCII-only, except for an optional UTF-8 BOM at the start of the file.
Windows PowerShell 5.1 can misread UTF-8 without BOM and break parsing when
Japanese text is embedded in scripts.

Put user-facing Japanese text in Blazor/C# UI files or JSON resources, not in
PowerShell scripts. `start.ps1` and `stop.ps1` run
`scripts\check-powershell-ascii.ps1` before loading helper scripts.
