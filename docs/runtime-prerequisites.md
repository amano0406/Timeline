# Timeline runtime prerequisites

This document inventories the external environment required to run Timeline.
It is the reference for KAN-43 and KAN-52.

## Goal

Users should know what Timeline needs before they start it, and failures should
be classified as either product artifact problems or local runtime prerequisite
problems.

Timeline currently uses Docker for the main runtime. Removing Docker is a
separate product direction and is not assumed here.

## Requirement classes

| Class | Meaning |
| --- | --- |
| Required | Timeline cannot run the current product runtime without it |
| Required by mode | Required only for a specific OS, compute mode, or product path |
| Recommended | Timeline can run without it, but the experience is degraded |
| Developer-only | Not required for user product artifacts |
| Future installer responsibility | Should eventually be checked or guided by the installer |

## Product artifact runtime

Built product artifacts should not require:

- Git
- a source checkout
- .NET SDK
- Node.js
- PowerShell startup scripts
- shell startup scripts

Built product artifacts currently do require:

- host executable support for the target OS
- Docker command access
- Docker Engine running
- usable host ports
- enough disk space for Docker images, volumes, and generated stores

## Core prerequisites

| Dependency | Class | Windows | Mac | Notes |
| --- | --- | --- | --- | --- |
| Docker command | Required | Docker Desktop CLI | Docker Desktop CLI | Launcher calls `docker compose` |
| Docker Engine | Required | Docker Desktop / Linux engine | Docker Desktop / Linux engine | Must be running before containers can start |
| Docker Compose plugin | Required | Included with modern Docker Desktop | Included with modern Docker Desktop | `docker compose`, not legacy `docker-compose` |
| Virtualization | Required by mode | Required for Docker Desktop / WSL2 backend | Required by Docker Desktop | Windows detection belongs to KAN-53 |
| WSL2 | Required by mode | Required by common Docker Desktop Windows setup | Not applicable | Exact requirement depends on Docker Desktop backend |
| Host ports | Required | Web, Local API, Ollama ports | Web, Local API, Ollama ports | Port conflicts are runtime configuration issues |
| Ollama runtime | Required | Timeline-owned Docker container | Timeline-owned Docker container | The model volume can be shared |
| Ollama model | Required by AI features | Pulled by Launcher when missing | Pulled by Launcher when missing | Slow first run is expected |
| GPU driver | Optional | NVIDIA only when GPU mode is selected | Not assumed | CPU mode should remain valid |

## Host ports

Default ports:

| Component | Default |
| --- | --- |
| Web | `19000` |
| Local API | `19001` |
| Ollama | `11434` |

If a port is already allocated, the artifact is not necessarily broken.
The user or setup flow should change the runtime port configuration and retry.

The KAN-45 verification hit this case with `11434`. Changing the test Ollama
port to `11435` allowed the built artifact to start.

## Windows-specific prerequisites

Windows product runtime needs:

- Docker Desktop installed or another compatible Docker CLI and Engine.
- Docker Engine running.
- WSL2 / virtualization available when Docker Desktop uses the WSL2 backend.
- Host ports free or configured to unused alternatives.

Windows diagnostics should distinguish:

- Docker command missing
- Docker Engine stopped
- Docker Engine unreachable
- WSL2 or virtualization likely missing
- host port already allocated

## Mac-specific prerequisites

Mac product runtime needs:

- Docker Desktop installed or another compatible Docker CLI and Engine.
- Docker Engine running.
- host executables allowed by macOS security controls.
- host ports free or configured to unused alternatives.

Mac diagnostics should distinguish:

- Docker command missing
- Docker Engine stopped
- Docker Engine unreachable
- executable permission problem
- Gatekeeper / quarantine problem
- host port already allocated

## Developer-only prerequisites

These are not product artifact requirements:

- .NET SDK
- Git
- source checkout
- NuGet restore access during user startup
- Node.js
- local PowerShell scripts

They remain required for development, release building, and source-level
debugging.

## Installer and setup responsibilities

KAN-41 and KAN-43 should eventually provide a user-facing setup experience that
can:

- detect missing Docker Desktop
- explain why Docker is required
- detect Docker Engine stopped
- guide the user to start Docker Desktop
- detect port conflicts before startup
- explain how changing ports affects Timeline URLs
- preserve settings and user data across reinstall and update

The installer should not silently install or modify privileged system
components without clear user consent.

## Relationship to other work

| Area | Jira | Relationship |
| --- | --- | --- |
| Built product artifacts | KAN-42 | Artifacts must avoid developer-only prerequisites |
| Verification path | KAN-47 | Uses this classification for failure diagnosis |
| Windows runtime detection | KAN-53 | Implements Windows-specific checks |
| Mac runtime detection | KAN-54 | Implements Mac-specific checks |
| Setup completion check | KAN-55 | Confirms the runtime can actually start |
| Installer / uninstaller | KAN-41 | Packages setup guidance into OS-native flows |
| Docker removal idea | Docker removal notes | Separate future direction |
