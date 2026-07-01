# Timeline user distribution artifacts

This document defines the product-facing distribution artifacts for Timeline.
It is the reference for KAN-42 and KAN-44.

## Goal

Timeline users should receive built product artifacts, not GitHub source code
archives.

The normal user path is:

1. Download the artifact for the user's operating system.
2. Install or extract it using the documented product flow.
3. Start Timeline through the C# Launcher.
4. Let the Launcher check runtime prerequisites and start the local services.

Users should not need to clone the repository, download GitHub source archives,
build .NET projects, run PowerShell scripts, or understand the internal Docker
Compose structure before they can try Timeline.

## Artifact classes

Timeline now distinguishes three artifact classes.

| Class | Audience | Purpose | User-facing |
| --- | --- | --- | --- |
| Source archive | Developers | Reproduce source at a tag | No |
| Developer checkout | Developers and maintainers | Local development and debugging | No |
| Built product artifact | Users | Install, launch, and update Timeline | Yes |

GitHub automatically generated source ZIP/TAR files are not product
distribution artifacts. They can remain available on GitHub, but Timeline must
not present them as the recommended user download.

## Initial product artifacts

The first user-facing artifact set should be small and explicit.

| Artifact | Target | Purpose |
| --- | --- | --- |
| `Timeline-win-x64-<version>.zip` | Windows x64 | Built Timeline product for Windows validation before a full installer exists |
| `Timeline-macos-arm64-<version>.zip` | macOS Apple Silicon | Built Timeline product for Mac validation before a full installer exists |
| `Timeline-macos-x64-<version>.zip` | macOS Intel | Built Timeline product for Mac validation if Intel support is kept |

Installer formats such as `.msi`, `.exe`, `.pkg`, or `.dmg` belong to KAN-41.
They should consume the same built product layout instead of inventing another
runtime layout.

## Product layout

Each built product artifact should contain one Timeline application root.

```text
Timeline/
  launcher/
  launcher-tray/
  local-api/
  web/
  worker/
  runtime/
  docker/
  docs/
  THIRD-PARTY-NOTICES.txt
  VERSION
```

The names above describe responsibilities, not necessarily final binary names.
The exact publish output can differ by runtime identifier, but the user-facing
root should stay predictable.

## Windows artifact build

KAN-45 uses the repository release builder to create the first Windows product
artifact.

```text
dotnet run --project tools/Timeline.ReleaseBuilder -- --runtime win-x64 --version <version>
```

The builder publishes host-side executables for Windows and container-side
runtime files for Docker:

| Area | Runtime | Why |
| --- | --- | --- |
| Launcher | `win-x64` | Runs directly on the user's Windows machine |
| Launcher tray | `win-x64` | Runs directly on the user's Windows machine |
| Local API | `win-x64` | Runs directly on the user's Windows machine |
| Web | `linux-x64` | Runs inside Docker's Linux container runtime |
| Worker | `linux-x64` | Runs inside Docker's Linux container runtime |

The product artifact's root `docker-compose.yml` is generated from
`packaging/docker-compose.product.yml`. It does not build from Timeline source
projects. It builds Docker images from the already published `web/` and
`worker/` runtime directories.

This means the user still needs Docker for the current Timeline runtime, but
does not need a .NET SDK or source checkout to start from the product artifact.

## Mac artifact build

KAN-46 uses the same release builder for Mac artifacts.

```text
dotnet run --project tools/Timeline.ReleaseBuilder -- --runtime osx-arm64 --container-runtime linux-arm64 --version <version>
```

The generated user-facing file name uses `macos-arm64`, not the .NET runtime
identifier `osx-arm64`:

```text
Timeline-macos-arm64-<version>.zip
```

For Apple Silicon Macs, the host-side Launcher, tray app, and Local API are
published as `osx-arm64`, while Web and Worker are published as `linux-arm64`
for Docker's Linux container runtime.

The ZIP writer marks the Mac host executables as executable entries. Mac
hardware still has to verify Gatekeeper behavior, quarantine attributes, Docker
Desktop startup, and whether the tray experience should become a `.app` bundle
or remain a raw executable until the installer work in KAN-41.

### Required contents

The artifact must include:

- C# resident Launcher, which is the normal user entry point.
- C# CLI Launcher, for diagnostics and controlled launcher operations.
- Timeline Local API runtime.
- Timeline Web runtime.
- Timeline Worker runtime.
- Docker Compose files needed for Timeline-owned containers.
- Version metadata for the artifact.
- Minimal user-facing documentation for launch and troubleshooting.

### Excluded contents

The artifact must not include:

- `.git/`
- source repository history
- development-only temporary files
- `docs-temp/`
- `scripts-temp/`
- local `data/`
- local `settings.json`
- local logs
- local backups
- generated Timeline store exports
- original user source files
- Node, NuGet, or Docker build caches

Development fallback scripts can remain in the repository, but they should not
be the user-facing entry point. If they are included temporarily for migration,
the Launcher remains the documented product entry.

The Windows product artifact currently excludes development fallback scripts.
Launcher, Local API, Web, Worker, Docker Compose, and product metadata are the
minimum runtime surface.

## Runtime data separation

The product artifact is immutable application content. User data is separate.

| Data | Default ownership | Artifact member |
| --- | --- | --- |
| Product binaries | Timeline release | Yes |
| Runtime settings | Local installation | No |
| User input files | User | No |
| Timeline generated store | Local installation | No |
| Sub-product generated data | Local installation / sub-product | No |
| Logs | Local installation | No |
| Docker volumes | Docker runtime | No |

This separation is required for safe uninstall, reinstall, and update flows.

## Launcher as the entry point

The built artifact should expose the resident C# Launcher as the normal user
entry point.

The Launcher owns:

- opening Timeline
- starting and stopping Timeline runtime services
- preflight checks
- OS startup registration
- recovery guidance
- future update orchestration

The Web UI can present update or setup choices, but the Launcher is the safer
owner for operations that stop or replace Timeline itself.

## Relationship to other epics

| Area | Jira | Relationship |
| --- | --- | --- |
| Built artifact shape | KAN-42 / KAN-44 | This document is the starting contract |
| Windows built artifact | KAN-45 | Consumes this layout |
| Mac built artifact | KAN-46 | Consumes this layout |
| Launch validation | KAN-47 | Verifies the artifact can start Timeline |
| OS installer and uninstaller | KAN-41 | Packages this artifact into OS-native install flows |
| Runtime prerequisites | KAN-43 | Runs after artifact launch or installer bootstrap |
| Update | KAN-40 | Downloads and swaps artifacts based on this layout |

## Version metadata

Every built artifact should carry product version metadata that can be read
without starting the full Web app.

Minimum metadata:

```json
{
  "productId": "timeline",
  "version": "0.0.0",
  "commit": "",
  "channel": "dev",
  "runtimeIdentifier": "win-x64",
  "createdAt": "2026-07-01T00:00:00Z"
}
```

The exact file name is not fixed yet. `VERSION` is sufficient for the first
iteration; a structured JSON manifest can replace or accompany it when update
implementation starts.

## Version and latest checks

Timeline must distinguish current-version detection from latest-version
detection.

Current-version detection reads:

- the built artifact's root `VERSION` file when running from a user artifact
- the Git checkout state when running from a developer checkout
- assembly metadata only as a last-resort fallback

Latest-version detection uses GitHub Release assets for built Timeline product
artifacts. GitHub source ZIP/TAR archives are not treated as user-facing update
targets.

The runtime status must distinguish:

- `ok`: a matching built artifact asset exists for the current runtime
- `no_release`: no GitHub Release is available yet
- `asset_missing`: a Release exists, but no matching built artifact exists
- `request_failed`: the latest-version check could not reach or read GitHub

This split prevents Timeline from telling a user to update to a source archive
that cannot be launched as a product.

## Update plan

KAN-57 adds a read-only update plan before file replacement is implemented.

The plan is available from:

```text
TimelineLauncher update-plan
GET /timeline/update/plan
```

Downloaded artifacts can be validated from:

```text
TimelineLauncher update-validate --artifact <zip-path>
GET /timeline/update/artifact/validate?path=<zip-path>
```

The plan is intentionally conservative:

- developer checkouts are blocked from product updater replacement;
- GitHub source archives are not update targets;
- `settings.json` and the configured data root are preserved;
- Docker volumes and sub-product application directories are preserved;
- the Launcher remains the orchestration owner for stopping, replacing, and
  verifying Timeline.

Actual replacement work should use this plan as its contract instead of
deriving update behavior independently.

## Acceptance notes for KAN-44

KAN-44 can be considered complete when:

- The repository documents the distinction between source archives and built
  product artifacts.
- The initial Windows and Mac artifact names are defined.
- The product artifact includes Launcher, Local API, Web, Worker, and runtime
  metadata at a responsibility level.
- User data and generated data are explicitly excluded from product artifacts.
- Downstream epics can refer to this document instead of redefining the layout.
