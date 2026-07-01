# Timeline Mac Runtime Verification

This document is the verification checklist for Jira `KAN-16` and `KAN-24`.
It exists to keep the macOS work tied to the C# Launcher direction. Do not add
or revive `bat`, `sh`, or `.command` files as the normal user entry point.

## Goal

Confirm that Timeline can be treated as one local application on macOS:

- the C# CLI Launcher can start, stop, open, and report status;
- the C# resident Launcher can start as the menu-bar equivalent;
- Docker, Local API, Web, Worker, and Ollama can be observed through the
  launcher/runtime status path;
- at least one supported material type can be imported and scanned;
- Windows-only products are shown as unsupported instead of broken.

## Scope Boundary

This checklist does not prove a production-grade Mac release.

Out of scope:

- signed installer;
- App Store distribution;
- notarization;
- final app icon work;
- full support for every Mac model and Docker Desktop configuration.

Those belong to packaging or distribution work, not `KAN-16` / `KAN-24`.

## Preconditions

On the Mac machine:

- .NET SDK compatible with the repo target framework is installed;
- Docker Desktop is installed and running;
- the Timeline repository is checked out;
- sub-products are present where Timeline settings point to them;
- no Windows-only shell wrapper is used as the normal entry point.

Useful checks:

```bash
dotnet --info
docker info
docker compose version
```

These commands are diagnostic commands only. They are not Timeline launchers.

## KAN-16: C# Launcher Minimal Startup

Run from the Timeline repository root.

Check local prerequisites first:

```bash
dotnet run --project launcher/Timeline.Launcher.csproj -- preflight
```

Interpretation:

- `OK`: the prerequisite is available;
- `WARN`: the condition may be acceptable before startup, such as Web or Local
  API not responding yet;
- `ERROR`: fix this before runtime verification, such as Docker Engine not
  running or required repository directories missing.

Check current status:

```bash
dotnet run --project launcher/Timeline.Launcher.csproj -- status
```

Start without opening the browser:

```bash
dotnet run --project launcher/Timeline.Launcher.csproj -- start --no-open
```

Confirm health:

```bash
curl -f http://127.0.0.1:19000/api/health
curl -f http://127.0.0.1:19001/health
```

Open through the launcher:

```bash
dotnet run --project launcher/Timeline.Launcher.csproj -- open
```

Start the resident launcher:

```bash
dotnet run --project launcher-tray/Timeline.Launcher.Tray.csproj
```

Stop:

```bash
dotnet run --project launcher/Timeline.Launcher.csproj -- stop
```

`KAN-16` can be completed only when the result is known for:

- CLI launcher start;
- CLI launcher stop;
- Web health;
- Local API health;
- Worker status;
- Ollama status;
- resident launcher startup;
- any failure category listed below.

## KAN-24: Material Import And Minimal Scan

Start only after `KAN-16` has a known startup result.

Recommended first material:

1. Image, because it is usually the lightest Docker path.
2. ChatGPT export, if a small export zip is available.
3. Audio, if Ollama/Whisper dependencies are already confirmed.

Use a macOS host path such as:

```text
/Users/<user>/TimelineSamples/image
/Users/<user>/TimelineSamples/chatgpt
/Users/<user>/TimelineSamples/audio
```

Expected checks:

- the path can be saved in Timeline settings;
- Docker worker can read the mounted path;
- scan starts from the C# launcher/runtime path;
- scan status is visible in the Web UI;
- at least one item appears in the material list or detail screen;
- Windows-only products are not treated as generic failures.

`KAN-24` can be completed only when at least one supported material type has
been imported and observed in Timeline.

## Failure Classification

When a Mac run fails, classify it before creating more work.

| Area | Typical signal | Next action |
| --- | --- | --- |
| Docker Desktop | `docker info` fails | Treat as local Docker setup issue first |
| Docker Compose | compose command fails | Check Compose plugin availability |
| Local API | `19001/health` fails | Inspect Local API process and logs |
| Web | `19000/api/health` fails | Inspect Web process and port binding |
| Worker | runtime status shows worker stopped | Check worker container and compose project |
| Ollama | model/API unavailable | Check Ollama container, port, and model setting |
| Host path | worker cannot see `/Users/...` | Check bind mount and Timeline path mapping |
| Permission | file exists but cannot be read | Check macOS file permission and Docker file sharing |
| Unsupported product | Windows Codex or PC state fails | Confirm it is shown as unsupported, not broken |
| Avalonia GUI | resident launcher fails | Separate GUI/runtime issue from CLI launcher issue |

## Jira Evidence Template

Paste a concise result back to Jira.

```text
Mac verification result

Machine:
- macOS:
- CPU:
- Docker Desktop:
- .NET SDK:

KAN-16:
- preflight:
- launcher status:
- launcher start:
- Web health:
- Local API health:
- Worker:
- Ollama:
- resident launcher:
- launcher stop:

KAN-24:
- material type:
- host path:
- scan result:
- detail/list confirmation:

Failure classification:
- area:
- evidence:
- next task needed:
```

## Completion Rule

Do not complete `KAN-16`, `KAN-24`, or the parent `KAN-3` from Windows-only
evidence. Windows-side `osx-arm64` publish success is useful preparation, but
the acceptance criteria require Mac runtime behavior.
