# Maintenance Notes

This file keeps stable internal maintenance notes that are still useful after
completed TODO and progress-log documents were removed.

These notes are not an external product contract. They must not be used to
change CLI/API inputs, outputs, generated file layouts, Docker names, ports, or
user-visible behavior during refactoring.

## Refactor Rules

- Preserve external behavior: UI behavior, CLI/API contracts, output files,
  default paths, Docker Compose names, ports, and volume semantics must not
  change during internal refactors.
- Treat visible screen changes as out of scope for refactor-only work. Do not
  change labels, layout, navigation, loading states, or displayed data unless a
  user-facing behavior change is explicitly requested.
- Keep sub-product operations behind public launchers and `cli.ps1`.
- Do not enter sub-product Docker containers from Timeline.
- Keep PowerShell files ASCII-only.
- Run `scripts/check-powershell-ascii.ps1` after editing PowerShell.
- Run a web build or smoke check after non-document code changes.

## Current Internal Risks

- `scripts/timeline-helper-server.ps1` is large and should be split only in
  behavior-preserving steps.
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
- Video `files list` can be slow on cold cache. Timeline currently mitigates
  this with progressive loading and cache behavior; do not change visible list
  behavior while refactoring.
- Product management can wait when helper-side product checks are busy. Keep
  loading feedback stable unless a dedicated behavior change is planned.
- Speech verbalization is still in quality-validation mode. Do not expand to
  full processing as part of unrelated refactors.

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
