# Operation Log Web Test Checklist

Purpose: verify that user-facing Web operations leave enough live console and persistent operation-log evidence to investigate incomplete, failed, or slow operations later.

Persistent log location:

```text
C:\TimelineData\Timeline\logs\operations\<operation-id>\
```

Expected files per operation:

```text
events.jsonl
summary.json
```

## Scope

- Use Web UI operations, not direct helper API calls, as the primary trigger.
- Verify the bottom-right CLI console for live feedback.
- Verify persistent operation directories after the UI action.
- Avoid destructive operations in this pass:
  - generated-data deletion is excluded
  - settings save is excluded
  - sub-product Docker direct access is excluded
- Product operations must continue to go through each product's `cli.ps1`.

## Preconditions

- [x] Timeline starts with `start.ps1`.
- [x] Web is reachable at `http://127.0.0.1:19000`.
- [x] Helper is reachable at `http://127.0.0.1:19001/health`.
- [x] Existing operation-log directory count is captured before the test.
- [x] Right-bottom CLI console can be opened from the Web UI.

## Web UI Checks

| ID | UI Area | User Action | Expected Live Console Evidence | Expected Persistent Log Evidence | Result |
|---|---|---|---|---|---|
| LOG-WEB-001 | Global console | Open CLI console | Console panel opens without blocking page use | No new persistent log required | PASS |
| LOG-WEB-002 | Dashboard | Open dashboard | Runtime status can be viewed | No new persistent log required unless CLI is invoked | PASS |
| LOG-WEB-003 | TimelineForAudio list | Open audio file list | Audio overview/list operation is visible | `TimelineForAudio` Web operation logs are written | PASS |
| LOG-WEB-004 | TimelineForAudio list | Click `一覧更新` | New audio list operation is visible | New `audio_files_list` operation log appears | PASS |
| LOG-WEB-005 | TimelineForAudio settings | Open audio settings | Settings/model operation is visible when invoked | `TimelineForAudio` operation log appears | PASS |
| LOG-WEB-006 | TimelineForWindowsCodex list | Open thread list | Windows Codex overview/items operation is visible | `TimelineForWindowsCodex` Web and CLI operation logs appear | PASS |
| LOG-WEB-007 | TimelineForWindowsCodex settings | Open settings | Settings operation is visible | `TimelineForWindowsCodex` operation log appears | PASS |
| LOG-WEB-008 | TimelineForChatGPT list | Open thread list | ChatGPT overview/items operation is visible | `TimelineForChatGPT` operation log appears | PASS |
| LOG-WEB-009 | TimelineForChatGPT settings | Open settings | Settings operation is visible | `TimelineForChatGPT` operation log appears | PASS |
| LOG-WEB-010 | TimelineForImage list | Open image file list | Image overview/list operation is visible | `TimelineForImage` Web and CLI operation logs appear | PASS |
| LOG-WEB-011 | TimelineForImage list | Click `一覧更新` | New image list operation is visible | New `image_files_list` operation log appears | PASS |
| LOG-WEB-012 | TimelineForImage settings | Open settings | Settings operation is visible | `TimelineForImage` operation log appears | PASS |
| LOG-WEB-013 | Timeline | Open timeline page | Timeline store status can be viewed | No new persistent log required unless worker/CLI is invoked | PASS |
| LOG-WEB-014 | Timeline | Click `時間軸を再構築` | Progress/status feedback appears | Web start log, worker operation log, and product CLI logs are written | PASS |
| LOG-WEB-015 | Global console | Reopen CLI console after actions | Recent commands show product, command, exit code, duration | Persistent logs contain matching operation IDs for CLI entries | PASS |

## Post-Test Checks

- [x] New operation directories were created for CLI-backed Web actions.
- [x] At least one persistent log contains `state: info` for CLI start and `state: success` or `state: error` for completion.
- [x] Web operation start/completion records are written.
- [x] Child CLI operation logs are linked to parent Web operation logs through `parentOperationId`.
- [x] No persistent log contains an unredacted Hugging Face token or obvious secret value.
- [x] Failed operations, if any, include the failed operation state and error detail.
- [x] The Web UI remains usable after the test.

## Test Run

Run time: 2026-05-04 19:18 JST.

Result artifact:

```text
C:\apps\Timeline\output\playwright\operation-log-web-test-results.json
```

Screenshots:

```text
C:\apps\Timeline\output\playwright\operation-log-console-open.png
C:\apps\Timeline\output\playwright\operation-log-audio-list.png
C:\apps\Timeline\output\playwright\operation-log-image-list.png
C:\apps\Timeline\output\playwright\operation-log-timeline-rebuild.png
C:\apps\Timeline\output\playwright\operation-log-console-after-actions.png
```

Summary:

- Initial operation directories: 12
- Final operation directories: 36
- New operation directories during the test: 24
- Web UI checks: 15 / 15 passed
- Post-test checks: 5 / 5 passed
- `timeline_rebuild` correctly produced logs for a failed worker run. The recorded error was: `TimelineForAudio CLI returned a container-prefixed Windows path...`. This is a sub-product contract issue, not a missing-log issue.

## Notes

- Web operations that represent a user-visible action should create a parent operation log even when they do not invoke a CLI.
- CLI-backed Web operations should create child CLI operation logs with `parentOperationId`.
- If a CLI-backed UI action creates no operation log, that is a defect.
- If an operation starts but never completes, the last `summary.json` state and `events.jsonl` tail should make the stalled point visible.
