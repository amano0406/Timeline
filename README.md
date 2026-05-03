# Timeline

Timeline is the local parent UI for Timeline products.

This repository does not contain the TimelineForAudio engine. It connects to existing local products under `C:\apps`, starting with `C:\apps\TimelineForAudio`.

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

After starting Timeline, verify the web routes and the TimelineForAudio PS1 download path:

```powershell
.\scripts\smoke-web.ps1
.\scripts\smoke-audio-ps1-download.ps1
```
