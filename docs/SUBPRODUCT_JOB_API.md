# Subproduct Job API Rulebook

This rulebook defines the local API contract Timeline expects from subproducts
when a product action can take more than a few seconds.

The contract is shared as a behavior rule. Each subproduct keeps its own
implementation and must not import a shared framework unless that product
explicitly decides to do so.

## Required Endpoints

Long-running refresh actions should expose these endpoints:

- `POST /jobs`
- `GET /jobs/{jobId}`
- `GET /jobs/active`
- `GET /jobs`

`POST /items/refresh` may remain for direct diagnostic use, but Timeline should
prefer `/jobs` when it is available.

## Job State

Use these states:

- `queued`: accepted but not processing yet
- `running`: processing is active
- `completed`: completed without item errors
- `completed_with_errors`: completed with one or more item errors
- `failed`: the whole job failed
- `interrupted`: the worker stopped before it could finish or mark failure
- `none`: no active job for `/jobs/active`

Timeline treats only `queued` and `running` as active.

## Job Payload

Every job status response should include:

```json
{
  "schemaVersion": "timeline.product_job.v1",
  "productId": "video",
  "productName": "TimelineForVideo",
  "type": "refresh",
  "jobId": "run-...",
  "state": "running",
  "phase": "audio",
  "stage": "audio",
  "message": "Analyzing video audio.",
  "progress": {
    "percent": 45.0,
    "current": 3,
    "total": 20,
    "unit": "files",
    "currentItem": "C:\\path\\file.mp4",
    "estimatedRemainingSeconds": null
  },
  "startedAt": "2026-05-20T00:00:00Z",
  "updatedAt": "2026-05-20T00:00:10Z",
  "completedAt": "",
  "error": null,
  "warnings": [],
  "result": null
}
```

`updatedAt` must change while a long job is alive. If item count does not change
for a long time, the product should still update the current stage or heartbeat
so Timeline can distinguish "still working" from "stalled".

## Interrupted Run Handling

Subproducts must not leave old `queued` or `running` jobs visible forever after a
worker restart, process crash, Docker OOM kill, or host reboot.

On API startup, `/jobs`, `/jobs/active`, and `/jobs/{jobId}` should compare
stored job state with the in-process active worker registry. Stored `queued` or
`running` jobs that are no longer active must be marked `interrupted`.

The `interrupted` status should preserve:

- original `jobId`
- original `startedAt`
- last known `stage`
- last known `currentItem`
- last known progress

It should add a clear message such as:

```text
Worker stopped before the job completed. Queue a new refresh to retry.
```

## Resource Rules

Products that process large local files should:

- process one item at a time unless concurrency is explicitly bounded
- write per-item progress before starting each item
- update progress between heavy substeps such as extraction, OCR, transcription,
  diarization, normalization, and packaging
- write item outputs as each item completes
- clean temporary files in `finally` blocks
- avoid keeping every large intermediate object in memory
- release model or tensor intermediates after each item where the runtime allows
  it

Audio/video model products should especially avoid passing unbounded long or
duration-unknown media into heavy models without chunking, skipping, or a clear
fallback policy.

## Timeline-Side Display

Timeline should surface product job details rather than collapsing them into a
generic timeout. At minimum, users should see:

- product name
- failed stage
- current item
- last update time
- whether the product API stopped, timed out, or returned a product error

