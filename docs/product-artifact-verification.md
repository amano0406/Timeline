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

As of KAN-46:

- `Timeline-macos-arm64-0.0.0-kan46.zip` was created from Windows.
- The ZIP did not include forbidden local data or development directories.
- Mac host executable entries were marked with executable metadata.
- Final Mac startup remains a Mac hardware verification item.

## Relationship to Jira epics

| Area | Jira | Responsibility |
| --- | --- | --- |
| Built artifact shape | KAN-42 | Defines and creates product artifacts |
| Artifact verification | KAN-47 | Defines startup and failure classification |
| OS installer | KAN-41 | Provides OS-native install/uninstall and app entry |
| Runtime prerequisites | KAN-43 | Handles Docker and other local requirements |
| Update | KAN-40 | Replaces installed artifacts safely |
