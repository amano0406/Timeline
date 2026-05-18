# Maintenance Notes

This file keeps stable internal maintenance notes that are still useful after
completed TODO and progress-log documents were removed.

These notes are not an external product contract. They must not be used to
change API inputs, outputs, generated file layouts, Docker names, ports, or
user-visible behavior during refactoring.

## Refactor Rules

- Preserve external behavior: UI behavior, API contracts, output files,
  default paths, Docker Compose names, ports, and volume semantics must not
  change during internal refactors.
- Treat visible screen changes as out of scope for refactor-only work. Do not
  change labels, layout, navigation, loading states, or displayed data unless a
  user-facing behavior change is explicitly requested.
- Keep sub-product operations behind public product APIs and start/stop launchers.
- Do not enter sub-product Docker containers from Timeline.
- Run `scripts/check-sub-product-cli-removal.ps1` after changing bundled sub-products or product manifests.
- Keep PowerShell files ASCII-only.
- Run `scripts/check-powershell-ascii.ps1` after editing PowerShell.
- Run `scripts/smoke-thread-detail-api-bridge.ps1` after changing ChatGPT or
  WindowsCodex thread-detail API bridging.
- Run a web build or smoke check after non-document code changes.

## Current Internal Risks

- Timeline HTTP/API execution lives in the C# Local API. Do not reintroduce the
  retired PowerShell helper server or worker API wrappers.
- Web service DTOs are split by product/area. Keep serialized property names,
  default values, and response shapes stable when moving model classes.
- `TimelineHelperClient` is split into product/area partial files. Keep endpoint
  URLs, fallback messages, and error-body handling stable when moving methods.
- Local API proxy endpoints are registered from `web/Endpoints`. Preserve route
  paths, query validation, range streaming, content-type fallback, and
  `Content-Disposition` forwarding when refactoring them.
- `MarkdownText` keeps markup in `.razor` and parser/render helpers in
  code-behind. Its manual `RenderTreeBuilder` sequence usage is intentionally
  preserved to avoid changing rendered Markdown behavior during refactors.
- `ProductManagementModal` keeps markup in `.razor` and splits code-behind by
  concern: load state in `ProductManagementModal.razor.cs`, start / stop /
  install / update actions in `ProductManagementModal.Actions.cs`, uninstall
  request / plan loading in `ProductManagementModal.Uninstall.cs`, runtime
  labels and button availability in `ProductManagementModal.Display.cs`, and
  completion / size labels in `ProductManagementModal.Completion.cs`. Keep
  button availability, modal text, completion messages, and runtime status
  labels stable during refactors.
- `InitialProductSetup` keeps the initial-install prompt markup in `.razor` and
  session / selection / install loop state in code-behind. Do not change its
  first-run visibility or selected-product defaults during refactors.
- Most page components keep route markup in `.razor` and page state / loading /
  paging / download / delete handlers in `.razor.cs`. Keep page routes,
  breadcrumbs, labels, paging semantics, and action side effects stable when
  moving code.
- `BrowserDownload` is the shared wrapper for browser save-picker JS interop,
  download proxy URL creation, and archive filename fallback. Keep existing
  save-cancel handling on each page stable when reusing it.
- `AudioFiles` is split further by concern: core load/poll state in
  `AudioFiles.razor.cs`, tree/selection helpers in `AudioFiles.Tree.cs`,
  download helpers in `AudioFiles.Downloads.cs`, active-run display helpers in
  `AudioFiles.RunStatus.cs`, and file status display helpers in
  `AudioFiles.Status.cs`.
- `TimelineIndex` is split further by concern: core load / scan / rebuild
  flow in `TimelineIndex.razor.cs`, audio verbalization state in
  `TimelineIndex.Verbalization.cs`, timeline export in
  `TimelineIndex.Downloads.cs`, scan progress labels in
  `TimelineIndex.Status.cs`, and material product link display in
  `TimelineIndex.MaterialProducts.cs`.
- `Index` keeps dashboard markup in `.razor` and splits code-behind by
  concern: initial / detail loading in `Index.razor.cs`, alert and data-source
  construction in `Index.Dashboard.cs`, verbalization status refresh in
  `Index.Verbalization.cs`, and display helpers / summary records in
  `Index.Display.cs`.
- `ChatGpt` keeps the thread-list markup in `.razor` and splits code-behind by
  concern: loading / paging in `ChatGpt.razor.cs`, processing progress in
  `ChatGpt.Processing.cs`, downloads in `ChatGpt.Downloads.cs`, delete actions
  in `ChatGpt.Delete.cs`, selection state in `ChatGpt.Selection.cs`, and
  display helpers in `ChatGpt.Display.cs`.
- `WindowsCodex` follows the same thread-list split: loading / paging in
  `WindowsCodex.razor.cs`, downloads in `WindowsCodex.Downloads.cs`, delete
  actions in `WindowsCodex.Delete.cs`, and selection / display helpers in
  `WindowsCodex.Selection.cs` and `WindowsCodex.Display.cs`.
- `Image` keeps the image-list markup in `.razor` and splits code-behind by
  concern: loading / paging in `Image.razor.cs`, tree row model helpers in
  `Image.TreeModels.cs`, tree / selection helpers in `Image.Tree.cs`,
  downloads in `Image.Downloads.cs`, delete actions in `Image.Delete.cs`, and
  summary display properties in `Image.Display.cs`.
- `FileDetail` keeps the audio detail markup in `.razor` and splits
  code-behind by concern: query loading in `FileDetail.razor.cs`, audio player
  JS watch / seek state in `FileDetail.AudioPlayer.cs`, audio verbalization
  loading / polling in `FileDetail.Verbalization.cs`, and display labels in
  `FileDetail.Display.cs`.
- Video `POST /files/list` can be slow on cold cache. Timeline currently mitigates
  this with progressive loading and cache behavior; do not change visible list
  behavior while refactoring.
- Product management can wait when helper-side product checks are busy. Keep
  loading feedback stable unless a dedicated behavior change is planned.
- Speech verbalization bulk processing now covers every audio/video target that
  still needs work. Keep target selection, retry priority, and batch limits
  explicit when changing this flow.

## Active Follow-Ups

- Continue moving product-specific assumptions toward `timeline-product.json`.
- Keep generated-data deletion separate from app uninstall and source-file
  deletion.
- Revisit data migration only as an explicit user-facing behavior change.
- If Timeline store moves from files to a database later, treat it as a
  separate migration project with compatibility checks.

## Removed Progress Notes

The following progress/TODO documents were removed because they mostly contained
completed checklists, dated run logs, or screenshot inventories:

- `README.ja.md`
- `docs/audio-verbalization-todo.md`
- `docs/dashboard-redesign-todo.md`
- `docs/directory-root-restructure-todo.md`
- `docs/operation-log-web-test-checklist.md`
- `docs/scan-timeline-integration-todo.md`
- `docs/settings-unification-todo.md`
- `docs/timeline-for-pc-integration-todo.md`
- `docs/timeline-for-video-integration-todo.md`

The following old HTML design/prototype notes were also removed because their
stable content is now covered by README, manifest docs, uninstall docs, or the
LLM data-rule document:

- `docs/audio-verbalization-implementation.html`
- `docs/future-product-roadmap.html`
- `docs/monetization-and-product-strategy-notes.html`
- `docs/navigation-settings-scan-redesign-report.html`
- `docs/scan-timeline-integration-prototypes.html`
- `docs/settings-screen-wireframe.html`
- `docs/settings-unification-design.html`
- `docs/sub-product-management-design.html`
