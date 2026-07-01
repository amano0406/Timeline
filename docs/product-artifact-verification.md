# Timeline product artifact verification

This document defines how to verify a built Timeline product artifact after it
is created. It is the reference for KAN-47.

## Goal

Artifact verification answers one question:

Can a user start Timeline from the built product artifact without cloning the
repository or building Timeline source code?

The verification must also separate product artifact problems from local
runtime environment problems.

## Verification levels

| Level | Purpose | Owner |
| --- | --- | --- |
| Artifact structure | Check files included in the ZIP | Release builder |
| Launcher preflight | Check local prerequisites and packaged runtime | Launcher |
| Runtime startup | Start Local API, Web, Worker, and Ollama | Launcher and Docker |
| Product health | Confirm Web and Local API are reachable | Launcher / tester |
| Platform validation | Confirm OS-specific behavior | Human on target OS |

## Artifact structure checks

The ZIP must contain one top-level `Timeline/` directory.

Required entries:

- `Timeline/launcher/`
- `Timeline/launcher-tray/`
- `Timeline/local-api/`
- `Timeline/web/`
- `Timeline/worker/`
- `Timeline/docker-compose.yml`
- `Timeline/docker/`
- `Timeline/VERSION`
- `Timeline/README.md`

Forbidden entries:

- `.git/`
- `docs-temp/`
- `scripts-temp/`
- `data/`
- `settings.json`
- `bin/`
- `obj/`
- `node_modules/`
- `logs/`

If forbidden entries are present, treat it as an artifact problem.

## Windows verification

Create the artifact:

```text
dotnet run --project tools/Timeline.ReleaseBuilder -- --runtime win-x64 --version <version>
```

Extract the ZIP and run:

```text
Timeline\launcher\Timeline.Launcher.exe configure-runtime --root Timeline --instance-name verify-<id> --web-port 19100 --local-api-port 19101 --ollama-port 19102 --share-ollama-volume false --ollama-volume-name timeline-verify-ollama --data-root data-verification --json
Timeline\launcher\Timeline.Launcher.exe preflight --json --root Timeline
Timeline\launcher\Timeline.Launcher.exe start --no-open --root Timeline
```

Expected health checks:

```text
http://127.0.0.1:<webPort>/api/health
http://127.0.0.1:<localApiPort>/health
```

Both should return HTTP 200 after startup.

## Mac Apple Silicon verification

Create the artifact:

```text
dotnet run --project tools/Timeline.ReleaseBuilder -- --runtime osx-arm64 --container-runtime linux-arm64 --version <version>
```

The generated file name should be:

```text
Timeline-macos-arm64-<version>.zip
```

After extraction on Mac, run:

```text
./Timeline/launcher/Timeline.Launcher configure-runtime --root ./Timeline --instance-name verify-<id> --web-port 19100 --local-api-port 19101 --ollama-port 19102 --share-ollama-volume false --ollama-volume-name timeline-verify-ollama --data-root data-verification --json
./Timeline/launcher/Timeline.Launcher preflight --json --root ./Timeline
./Timeline/launcher/Timeline.Launcher start --no-open --root ./Timeline
```

Mac-specific checks:

- Host executables keep execute permission after extraction.
- Gatekeeper or quarantine behavior is understood.
- Docker Desktop can start and run Apple Silicon container payloads.
- `web` and `worker` Docker images build from the packaged `linux-arm64`
  publish output.
- The tray experience is acceptable until KAN-41 decides the installer and
  `.app` bundle shape.

## Success criteria

The artifact verification succeeds when:

- Launcher starts from the extracted artifact.
- Local API starts from the bundled runtime, not from source publish.
- Docker Compose builds container images from packaged runtime files, not from
  Timeline source projects.
- Web health returns HTTP 200.
- Local API health returns HTTP 200.
- `VERSION` contains the expected runtime identifiers and version.
- A fresh artifact that does not yet have `settings.json` reports that as an
  initial-state information item, not as a warning or failure.

Sub-products can be `not-created` in a fresh artifact verification. That is not
an artifact failure. Sub-product installation and setup belong to the setup and
runtime prerequisite work.

## Failure classification

| Symptom | Classification | Related Jira |
| --- | --- | --- |
| Missing Launcher, Local API, Web, Worker, `VERSION`, or compose files | Artifact problem | KAN-42 / KAN-45 / KAN-46 |
| Local API tries to publish from source in a product artifact | Artifact problem | KAN-45 / KAN-46 |
| Docker command is missing | Runtime prerequisite | KAN-43 |
| Docker Engine is stopped | Runtime prerequisite | KAN-43 |
| Host port is already allocated | Runtime configuration | KAN-43 |
| OS application entry is missing | Installer / OS integration | KAN-41 |
| Artifact can start, but update replacement is unsafe | Update | KAN-40 |
| Mac executable permission or Gatekeeper blocks launch | Mac packaging / installer | KAN-41 / KAN-46 |

## Current verification evidence

As of KAN-45:

- `Timeline-win-x64-0.0.0-kan45.zip` was created.
- Windows artifact preflight detected bundled Local API runtime.
- Windows artifact startup succeeded after moving the test Ollama port from
  `11434` to `11435` because `11434` was already allocated locally.
- Web health returned HTTP 200.
- Local API health returned HTTP 200.
- `Timeline-win-x64-0.0.0-kan-preflight-aaeb148.zip` was created after the
  fresh-artifact preflight rule was corrected.
- Running the bundled
  `Timeline\launcher\Timeline.Launcher.exe preflight --json --root Timeline`
  from the extracted artifact returned `state=ok`, `errorCount=0`, and
  `warningCount=0`.
- In that artifact, missing `settings.json` was classified as expected
  first-start state because the artifact contains `VERSION` and packaged
  runtimes.

As of KAN-46:

- `Timeline-macos-arm64-0.0.0-kan46.zip` was created from Windows.
- `Timeline-macos-arm64-0.0.0-kan-mac-launcher-049cb85.zip` was also created
  after the tray launcher started preferring the bundled CLI launcher.
- The ZIP did not include forbidden local data or development directories.
- Mac host executable entries were marked with executable metadata.
- Final Mac startup remains a Mac hardware verification item.

As of KAN-47:

- Artifact preflight can now distinguish product packaging problems from normal
  first-run state.
- The Windows artifact has a repeatable preflight command and a recorded
  success result.
- Full runtime startup from an extracted artifact still needs an isolated
  verification environment or explicit port configuration, because the
  developer machine can already have Timeline running on the default ports.
- `configure-runtime` is the C# Launcher entry point for that explicit
  configuration. It writes the artifact-local `settings.json` without requiring
  `bat`, `sh`, or `command` wrapper scripts.
- `Timeline-win-x64-0.0.0-kan47-runtime-config-3b10152.zip` was created and
  extracted into an isolated verification root.
- The bundled Launcher wrote an artifact-local verification `settings.json` for
  `instanceName=verify-3b10152`, Web `19200`, Local API `19201`, and Ollama
  `19202`.
- `Timeline\launcher\Timeline.Launcher.exe start --no-open --root Timeline`
  completed from the extracted artifact. Docker Compose started `web`,
  `worker`, and `ollama` under project `timeline-verify-3b10152`.
- Post-start preflight returned `state=ok`, `errorCount=0`, and
  `warningCount=0`; Web health and Local API health both responded.
- `verify-setup` returned `needs_attention` only because sub-products are
  `not_installed` in the fresh artifact data root. This is a setup coverage
  item, not a built artifact failure.

## Relationship to Jira epics

| Area | Jira | Responsibility |
| --- | --- | --- |
| Built artifact shape | KAN-42 | Defines and creates product artifacts |
| Artifact verification | KAN-47 | Defines startup and failure classification |
| OS installer | KAN-41 | Provides OS-native install/uninstall and app entry |
| Runtime prerequisites | KAN-43 | Handles Docker and other local requirements |
| Update | KAN-40 | Replaces installed artifacts safely |
