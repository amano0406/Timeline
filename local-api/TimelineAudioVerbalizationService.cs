using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Text;
using System.Globalization;

public sealed class TimelineAudioVerbalizationService
{
    private readonly TimelineSettingsService _settings;
    private readonly TimelineLocalApiOptions _options;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineAudioFileService _audioFiles;
    private readonly TimelineVideoOverviewService _videoFiles;
    private readonly TimelineAudioVerbalizationPlanService _planner;
    private readonly TimelineAudioVerbalizationExecutionService _execution;
    private readonly TimelineAudioVerbalizationJobRegistry _jobs;

    public TimelineAudioVerbalizationService(
        TimelineSettingsService settings,
        TimelineLocalApiOptions options,
        TimelineOperationLogService operations,
        TimelineAudioFileService audioFiles,
        TimelineVideoOverviewService videoFiles,
        TimelineAudioVerbalizationPlanService planner,
        TimelineAudioVerbalizationExecutionService execution,
        TimelineAudioVerbalizationJobRegistry jobs)
    {
        _settings = settings;
        _options = options;
        _operations = operations;
        _audioFiles = audioFiles;
        _videoFiles = videoFiles;
        _planner = planner;
        _execution = execution;
        _jobs = jobs;
    }

    public JsonObject GetBulkStatus(string? jobId)
    {
        var path = GetBulkStatusPath(jobId);
        if (!File.Exists(path))
        {
            var jobIdText = ConvertTimelineText(jobId);
            if (!string.IsNullOrEmpty(jobIdText))
            {
                return NewBulkStatus(jobIdText, "unknown", "Bulk audio verbalization job was not found.");
            }

            return NewBulkStatus(string.Empty, "not_started", string.Empty);
        }

        try
        {
            var status = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return NormalizeBulkStatus(status);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return NewBulkStatus(ConvertTimelineText(jobId), "unreadable", ex.Message);
        }
    }

    public JsonObject GetBulkTargetSummary(bool forceRefresh, string localApiBaseUrl)
    {
        var activeStatus = GetBulkStatus(string.Empty);
        if (IsBulkActive(activeStatus))
        {
            var totalItems = GetInt(activeStatus, "totalItems", 0);
            var completedItems = GetInt(activeStatus, "completedItems", 0);
            var reviewItems = GetInt(activeStatus, "reviewItems", 0);
            var activeFailedItems = GetInt(activeStatus, "failedItems", 0);
            var skippedItems = GetInt(activeStatus, "skippedItems", 0);
            var remainingItems = Math.Max(0, totalItems - completedItems - reviewItems - activeFailedItems - skippedItems);
            return new JsonObject
            {
                ["available"] = true,
                ["targetCount"] = remainingItems,
                ["failedItems"] = activeFailedItems,
                ["changedItems"] = 0,
                ["notStartedItems"] = 0,
                ["unknownItems"] = 0,
                ["activeOrStaleItems"] = remainingItems,
                ["byState"] = new JsonObject
                {
                    ["running"] = remainingItems,
                },
                ["updatedAt"] = GetString(activeStatus, "updatedAt", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)),
                ["message"] = "Bulk audio verbalization is running.",
                ["cached"] = false,
            };
        }

        var targets = GetBulkTargets(localApiBaseUrl);
        var byState = new Dictionary<string, int>(StringComparer.Ordinal);
        var failedItems = 0;
        var changedItems = 0;
        var notStartedItems = 0;
        var unknownItems = 0;
        var activeOrStaleItems = 0;

        foreach (var target in targets)
        {
            var state = GetString(target.Status, "state", string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(state))
            {
                state = "unknown";
            }
            byState[state] = byState.TryGetValue(state, out var current) ? current + 1 : 1;

            var signatureState = GetString(target.Status, "signatureState", string.Empty).ToLowerInvariant();
            if (state == "failed")
            {
                failedItems++;
            }
            else if (state is "not_started" or "planned" or "source_transcript")
            {
                notStartedItems++;
            }
            else if (!string.IsNullOrEmpty(signatureState) && signatureState != "current")
            {
                changedItems++;
            }
            else if (state is "unknown" or "unreadable")
            {
                unknownItems++;
            }
            else if (state is "queued" or "running")
            {
                activeOrStaleItems++;
            }
        }

        return new JsonObject
        {
            ["available"] = true,
            ["targetCount"] = targets.Count,
            ["failedItems"] = failedItems,
            ["changedItems"] = changedItems,
            ["notStartedItems"] = notStartedItems,
            ["unknownItems"] = unknownItems,
            ["activeOrStaleItems"] = activeOrStaleItems,
            ["byState"] = NewIntObject(byState),
            ["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["message"] = targets.Count > 0
                ? "Bulk audio verbalization has target files."
                : "No audio files need verbalization.",
            ["cached"] = false,
        };
    }

    public JsonObject StartBulk(string localApiBaseUrl)
    {
        var operationId = _operations.NewOperationId("worker");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "worker",
            "Timeline",
            "audio_verbalization_bulk_start",
            "started",
            "Web operation started.");

        try
        {
            var result = StartBulkCore(localApiBaseUrl);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "worker",
                "Timeline",
                "audio_verbalization_bulk_start",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["jobId"] = GetString(result, "jobId", string.Empty),
                    ["state"] = GetString(result, "state", string.Empty),
                    ["message"] = GetString(result, "message", string.Empty),
                });
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "worker",
                "Timeline",
                "audio_verbalization_bulk_start",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    public Task<JsonObject> StartSingleAsync(
        JsonObject? request,
        string localApiBaseUrl,
        CancellationToken cancellationToken)
    {
        return InvokeWorkerOperationAsync(
            "audio_verbalization_start",
            operationId => StartSingleCoreAsync(request, localApiBaseUrl, operationId, cancellationToken));
    }

    private Task<JsonObject> StartSingleCoreAsync(
        JsonObject? request,
        string localApiBaseUrl,
        string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = request ?? new JsonObject();
        var sourceId = GetString(payload, "sourceId", string.Empty);
        var relativePath = GetString(payload, "relativePath", string.Empty);
        if (string.IsNullOrEmpty(relativePath))
        {
            relativePath = GetString(payload, "path", string.Empty);
        }
        var force = GetBool(payload, "force", false);
        var jobId = NewSingleJobId();
        var detail = _audioFiles.GetFileDetail(sourceId, relativePath, localApiBaseUrl);
        var execution = _planner.CreateExecutionContext(
            detail,
            sourceId,
            relativePath,
            jobId,
            "queued",
            "Audio verbalization worker has been queued.",
            force);
        if (!execution.CanRun)
        {
            return Task.FromResult(execution.Status);
        }

        _operations.WriteOperationEvent(
            jobId,
            "worker",
            "Timeline",
            "audio_verbalization",
            "queued",
            "Audio verbalization worker has been queued.",
            details: new JsonObject
            {
                ["audioItemId"] = execution.AudioItemId,
                ["sourceId"] = execution.SourceId,
                ["relativePath"] = execution.RelativePath,
                ["planPath"] = GetString(execution.Status, "planPath", string.Empty),
                ["resultPath"] = execution.ResultPath,
            });

        try
        {
            StartSingleWorker(execution.AudioItemId, jobId);
            return Task.FromResult(execution.Status);
        }
        catch (Exception ex)
        {
            var failedStatus = MarkSingleStartFailed(execution, jobId, ex.Message);
            _operations.WriteOperationEvent(
                jobId,
                "worker",
                "Timeline",
                "audio_verbalization",
                "failed",
                ex.Message,
                details: new JsonObject
                {
                    ["audioItemId"] = execution.AudioItemId,
                    ["resultPath"] = execution.ResultPath,
                });
            return Task.FromResult(failedStatus);
        }
    }

    private JsonObject StartBulkCore(string localApiBaseUrl)
    {
        var latestStatus = GetBulkStatus(string.Empty);
        if (IsBulkActive(latestStatus))
        {
            var latestJobId = GetString(latestStatus, "jobId", string.Empty);
            if (_jobs.IsActive(latestJobId))
            {
                return latestStatus;
            }

            latestStatus["state"] = "failed";
            latestStatus["completedAt"] = DateTimeOffset.Now.ToString("o");
            latestStatus["message"] = "Audio verbalization bulk job was marked failed because its worker was not found.";
            WriteBulkStatus(latestStatus);
            _operations.WriteOperationEvent(
                latestJobId,
                "worker",
                "Timeline",
                "audio_verbalization_bulk",
                "failed",
                GetString(latestStatus, "message", string.Empty));
        }

        var jobId = NewBulkJobId();
        var status = NewBulkStatus(jobId, "queued", "Audio verbalization bulk worker has been queued.");
        WriteBulkStatus(status);
        try
        {
            StartBulkWorker(jobId, localApiBaseUrl);
        }
        catch (Exception ex)
        {
            status["state"] = "failed";
            status["message"] = ex.Message;
            status["completedAt"] = DateTimeOffset.Now.ToString("o");
            WriteBulkStatus(status);
            _operations.WriteOperationEvent(
                jobId,
                "worker",
                "Timeline",
                "audio_verbalization_bulk",
                "failed",
                ex.Message);
        }

        return status;
    }

    private async Task<JsonObject> InvokeWorkerOperationAsync(
        string action,
        Func<string, Task<JsonObject>> operation)
    {
        var operationId = _operations.NewOperationId("worker");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "worker",
            "Timeline",
            action,
            "started",
            "Web operation started.");

        try
        {
            var result = await operation(operationId);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "worker",
                "Timeline",
                action,
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["jobId"] = GetString(result, "jobId", string.Empty),
                    ["state"] = GetString(result, "state", string.Empty),
                    ["message"] = GetString(result, "message", string.Empty),
                });
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "worker",
                "Timeline",
                action,
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private void StartBulkWorker(string jobId, string localApiBaseUrl)
    {
        _operations.WriteOperationEvent(
            jobId,
            "worker",
            "Timeline",
            "audio_verbalization_bulk",
            "starting",
            "Audio verbalization bulk worker task is starting.",
            details: new JsonObject
            {
                ["worker"] = "local-api-background-task",
            });

        _ = Task.Run(() => RunBulkAsync(jobId, localApiBaseUrl));

        _operations.WriteOperationEvent(
            jobId,
            "worker",
            "Timeline",
            "audio_verbalization_bulk",
            "queued",
            "Audio verbalization bulk worker task was started.",
            details: new JsonObject
            {
                ["worker"] = "local-api-background-task",
            });
    }

    private async Task RunBulkAsync(string jobId, string localApiBaseUrl)
    {
        using var lease = _jobs.MarkActive(jobId, "_bulk");
        var status = GetBulkStatus(jobId);
        if (string.IsNullOrEmpty(GetString(status, "jobId", string.Empty)))
        {
            status = NewBulkStatus(jobId, "running", "Audio verbalization bulk job is running.");
        }

        try
        {
            status["state"] = "running";
            status["message"] = "Audio verbalization bulk job is collecting targets.";
            if (string.IsNullOrEmpty(GetString(status, "startedAt", string.Empty)))
            {
                status["startedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            }
            WriteBulkStatus(status);

            _operations.WriteOperationEvent(
                jobId,
                "worker",
                "Timeline",
                "audio_verbalization_bulk",
                "running",
                "Audio verbalization bulk execution started.");

            var targets = GetBulkTargets(localApiBaseUrl);
            var totalTurns = targets.Sum(target => GetInt(target.Status, "totalTurns", 0));
            var totalChunks = targets.Sum(target => GetInt(target.Status, "totalChunks", 0));
            status["totalItems"] = targets.Count;
            status["totalTurns"] = totalTurns;
            status["totalChunks"] = totalChunks;
            status["message"] = targets.Count > 0
                ? "Audio verbalization bulk job is running."
                : "No audio files need verbalization.";

            if (targets.Count == 0)
            {
                status["state"] = "completed";
                status["completedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                WriteBulkStatus(status);
                _operations.WriteOperationEvent(
                    jobId,
                    "worker",
                    "Timeline",
                    "audio_verbalization_bulk",
                    "completed",
                    "No audio files need verbalization.");
                return;
            }
            WriteBulkStatus(status);

            var completedItems = 0;
            var reviewItems = 0;
            var failedItems = 0;
            var skippedItems = 0;
            var completedChunksBase = 0;
            var verbalizedTurnsBase = 0;
            var unresolvedTurnsBase = 0;

            foreach (var target in targets)
            {
                status["currentAudioItemId"] = target.AudioItemId;
                status["currentFileName"] = target.FileName;
                status["currentRelativePath"] = target.RelativePath;
                status["currentChunkId"] = string.Empty;
                status["currentItemCompletedChunks"] = 0;
                status["currentItemTotalChunks"] = 0;
                status["completedItems"] = completedItems;
                status["reviewItems"] = reviewItems;
                status["failedItems"] = failedItems;
                status["skippedItems"] = skippedItems;
                status["message"] = "Audio verbalization bulk job is preparing the current file.";
                WriteBulkStatus(status);

                try
                {
                    var detail = GetBulkTargetDetail(target, localApiBaseUrl);
                    var context = _planner.CreateExecutionContext(
                        detail,
                        target.SourceId,
                        target.RelativePath,
                        jobId,
                        "queued",
                        "Audio verbalization is queued in a bulk job.",
                        force: false);

                    if (!context.CanRun)
                    {
                        skippedItems++;
                        status["skippedItems"] = skippedItems;
                        status["message"] = "Audio verbalization bulk job skipped a file.";
                        WriteBulkStatus(status);
                        continue;
                    }

                    var itemTotalChunks = GetInt(context.Status, "totalChunks", 0);
                    status["currentAudioItemId"] = context.AudioItemId;
                    status["currentFileName"] = string.IsNullOrEmpty(context.FileName) ? target.FileName : context.FileName;
                    status["currentRelativePath"] = target.RelativePath;
                    status["currentItemTotalChunks"] = itemTotalChunks;
                    var remainingUnknownItems = Math.Max(0, targets.Count - completedItems - reviewItems - failedItems - skippedItems - 1);
                    totalChunks = Math.Max(totalChunks, completedChunksBase + itemTotalChunks + remainingUnknownItems);
                    status["totalChunks"] = totalChunks;
                    status["message"] = "Audio verbalization bulk job is processing the current file.";
                    WriteBulkStatus(status);

                    void Progress(JsonObject fileStatus, int completedChunks, int currentTotalChunks)
                    {
                        status["currentChunkId"] = GetString(fileStatus, "currentChunkId", string.Empty);
                        status["currentItemCompletedChunks"] = completedChunks;
                        status["currentItemTotalChunks"] = currentTotalChunks;
                        status["completedChunks"] = completedChunksBase + completedChunks;
                        status["verbalizedTurns"] = verbalizedTurnsBase + GetInt(fileStatus, "verbalizedTurns", 0);
                        status["unresolvedTurns"] = unresolvedTurnsBase + GetInt(fileStatus, "unresolvedTurns", 0);
                        status["message"] = "Audio verbalization bulk job is processing the current chunk.";
                        WriteBulkStatus(status);
                    }

                    var finalItemStatus = await _execution.RunSingleAsync(
                        context.AudioItemId,
                        jobId,
                        Progress,
                        CancellationToken.None);

                    var finalState = GetString(finalItemStatus, "state", string.Empty).ToLowerInvariant();
                    var finalCompletedChunks = GetInt(finalItemStatus, "completedChunks", 0);
                    var finalVerbalizedTurns = GetInt(finalItemStatus, "verbalizedTurns", 0);
                    var finalUnresolvedTurns = GetInt(finalItemStatus, "unresolvedTurns", 0);
                    completedChunksBase += finalCompletedChunks;
                    verbalizedTurnsBase += finalVerbalizedTurns;
                    unresolvedTurnsBase += finalUnresolvedTurns;
                    if (finalState == "completed")
                    {
                        completedItems++;
                    }
                    else if (finalState == "needs_review")
                    {
                        reviewItems++;
                    }
                    else
                    {
                        failedItems++;
                    }
                }
                catch (Exception ex)
                {
                    failedItems++;
                    status["message"] = ex.Message;
                    _operations.WriteOperationEvent(
                        jobId,
                        "worker",
                        "Timeline",
                        "audio_verbalization_bulk_item",
                        "failed",
                        ex.Message,
                        details: new JsonObject
                        {
                            ["product"] = target.Product,
                            ["sourceId"] = target.SourceId,
                            ["relativePath"] = target.RelativePath,
                        });
                }

                status["completedItems"] = completedItems;
                status["reviewItems"] = reviewItems;
                status["failedItems"] = failedItems;
                status["skippedItems"] = skippedItems;
                status["completedChunks"] = completedChunksBase;
                status["verbalizedTurns"] = verbalizedTurnsBase;
                status["unresolvedTurns"] = unresolvedTurnsBase;
                status["currentChunkId"] = string.Empty;
                status["message"] = "Audio verbalization bulk job moved to the next file.";
                WriteBulkStatus(status);
            }

            status["state"] = "completed";
            status["currentAudioItemId"] = string.Empty;
            status["currentFileName"] = string.Empty;
            status["currentRelativePath"] = string.Empty;
            status["currentChunkId"] = string.Empty;
            status["currentItemCompletedChunks"] = 0;
            status["currentItemTotalChunks"] = 0;
            status["completedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            status["estimatedRemainingSec"] = 0;
            status["message"] = failedItems > 0
                ? "Audio verbalization bulk job completed with failed files."
                : reviewItems > 0
                    ? "Audio verbalization bulk job completed with review files."
                    : "Audio verbalization bulk job completed.";
            WriteBulkStatus(status);
            _operations.WriteOperationEvent(
                jobId,
                "worker",
                "Timeline",
                "audio_verbalization_bulk",
                "completed",
                GetString(status, "message", string.Empty),
                details: new JsonObject
                {
                    ["totalItems"] = targets.Count,
                    ["completedItems"] = completedItems,
                    ["reviewItems"] = reviewItems,
                    ["failedItems"] = failedItems,
                    ["skippedItems"] = skippedItems,
                    ["verbalizedTurns"] = verbalizedTurnsBase,
                    ["unresolvedTurns"] = unresolvedTurnsBase,
                });
        }
        catch (Exception ex)
        {
            status["state"] = "failed";
            status["message"] = ex.Message;
            status["completedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            status["estimatedRemainingSec"] = 0;
            WriteBulkStatus(status);
            _operations.WriteOperationEvent(
                jobId,
                "worker",
                "Timeline",
                "audio_verbalization_bulk",
                "failed",
                ex.Message);
        }
    }

    private List<AudioVerbalizationBulkTarget> GetBulkTargets(string localApiBaseUrl)
    {
        var audioTargets = new List<AudioVerbalizationBulkTarget>();
        var page = 1;
        const int pageSize = 200;
        while (page <= 10000)
        {
            var result = _audioFiles.GetFiles(page, pageSize);
            foreach (var file in GetArray(result, "files").OfType<JsonObject>())
            {
                var status = GetObject(file, "audioVerbalization") ?? new JsonObject();
                if (!AudioVerbalizationNeedsWork(status))
                {
                    continue;
                }

                audioTargets.Add(new AudioVerbalizationBulkTarget(
                    "audio",
                    GetString(file, "sourceId", string.Empty),
                    GetString(file, "relativePath", string.Empty),
                    GetString(file, "displayPath", string.Empty),
                    GetString(file, "fileName", string.Empty),
                    GetString(file, "itemId", string.Empty),
                    status,
                    file));
            }

            var pagination = GetObject(result, "pagination");
            if (!GetBool(pagination ?? new JsonObject(), "hasNext", false))
            {
                break;
            }
            page++;
        }

        var videoTargets = new List<AudioVerbalizationBulkTarget>();
        page = 1;
        while (page <= 10000)
        {
            var result = _videoFiles.GetFiles(page, pageSize);
            foreach (var file in GetArray(result, "files").OfType<JsonObject>())
            {
                var status = GetObject(file, "audioVerbalization") ?? new JsonObject();
                if (!AudioVerbalizationNeedsWork(status))
                {
                    continue;
                }

                videoTargets.Add(new AudioVerbalizationBulkTarget(
                    "video",
                    "video",
                    GetString(file, "relativePath", string.Empty),
                    GetString(file, "sourcePath", string.Empty),
                    GetString(file, "fileName", string.Empty),
                    GetString(file, "itemId", string.Empty),
                    status,
                    file));
            }

            var pagination = GetObject(result, "pagination");
            if (!GetBool(pagination ?? new JsonObject(), "hasNext", false))
            {
                break;
            }
            page++;
        }

        return audioTargets
            .Concat(videoTargets)
            .OrderBy(target => BulkTargetWorkPriority(target.Status))
            .ThenBy(target => BulkTargetProductPriority(target.Product))
            .ThenBy(target => GetString(target.Row, "fileName", string.Empty), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private JsonObject GetBulkTargetDetail(
        AudioVerbalizationBulkTarget target,
        string localApiBaseUrl)
    {
        return target.Product.Equals("video", StringComparison.OrdinalIgnoreCase)
            ? _videoFiles.GetFileDetail(target.SourcePath)
            : _audioFiles.GetFileDetail(target.SourceId, target.RelativePath, localApiBaseUrl);
    }

    private bool AudioVerbalizationNeedsWork(JsonObject status)
    {
        if (!GetBool(status, "available", false))
        {
            return false;
        }

        var state = GetString(status, "state", string.Empty).ToLowerInvariant();
        if (state is "queued" or "running")
        {
            var jobId = GetString(status, "jobId", string.Empty);
            return !_jobs.IsActive(jobId);
        }

        if (state is "completed" or "needs_review")
        {
            return false;
        }

        return GetInt(status, "totalTurns", 0) > 0;
    }

    private static int BulkTargetWorkPriority(JsonObject status)
    {
        var state = GetString(status, "state", string.Empty).ToLowerInvariant();
        return state switch
        {
            "failed" or "unreadable" or "unknown" => 0,
            "queued" or "running" => 1,
            "source_transcript" or "not_started" or "planned" => 2,
            _ => 3,
        };
    }

    private static int BulkTargetProductPriority(string product) =>
        product.Equals("audio", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private void StartSingleWorker(string audioItemId, string jobId)
    {
        if (string.IsNullOrEmpty(ConvertTimelineText(audioItemId)))
        {
            throw new InvalidOperationException("Audio item id is required.");
        }
        if (string.IsNullOrEmpty(ConvertTimelineText(jobId)))
        {
            throw new InvalidOperationException("Audio verbalization job id is required.");
        }

        _operations.WriteOperationEvent(
            jobId,
            "worker",
            "Timeline",
            "audio_verbalization",
            "starting",
            "Audio verbalization worker task is starting.",
            details: new JsonObject
            {
                ["audioItemId"] = audioItemId,
                ["worker"] = "local-api-background-task",
            });

        _ = Task.Run(() => _execution.RunSingleAsync(
            audioItemId,
            jobId,
            cancellationToken: CancellationToken.None));

        _operations.WriteOperationEvent(
            jobId,
            "worker",
            "Timeline",
            "audio_verbalization",
            "queued",
            "Audio verbalization worker task was started.",
            details: new JsonObject
            {
                ["audioItemId"] = audioItemId,
                ["worker"] = "local-api-background-task",
            });
    }

    private JsonObject MarkSingleStartFailed(
        TimelineAudioVerbalizationExecutionContext execution,
        string jobId,
        string message)
    {
        var payload = ReadJsonFile(execution.ResultPath) ?? new JsonObject();
        var status = GetObject(payload, "status") ?? execution.Status.DeepClone() as JsonObject ?? new JsonObject();
        status["state"] = "failed";
        status["jobId"] = jobId;
        status["updatedAt"] = DateTimeOffset.Now.ToString("o");
        status["message"] = message;

        WriteJsonFile(execution.ResultPath, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = status,
            ["turns"] = CloneArray(GetArray(payload, "turns")),
            ["chunks"] = CloneArray(GetArray(payload, "chunks")),
        });

        return status;
    }

    private string GetBulkStatusPath(string? jobId)
    {
        var directory = GetBulkDirectory();
        var jobIdText = ConvertTimelineText(jobId);
        if (string.IsNullOrEmpty(jobIdText))
        {
            return Path.GetFullPath(Path.Combine(directory, "latest.json"));
        }

        return Path.GetFullPath(Path.Combine(directory, GetTimelineZipSafeSegment(jobIdText) + ".json"));
    }

    private string GetBulkDirectory()
    {
        var path = Path.Combine(_settings.GetStoreDirectory(), "audio-verbalizations", "_bulk");
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private void WriteBulkStatus(JsonObject status)
    {
        var normalized = NormalizeBulkStatus(status);
        UpdateBulkTiming(normalized);
        var jobId = GetString(normalized, "jobId", string.Empty);
        if (!string.IsNullOrEmpty(jobId))
        {
            WriteJsonFile(GetBulkStatusPath(jobId), normalized);
        }
        WriteJsonFile(GetBulkStatusPath(string.Empty), normalized);
    }

    private static JsonObject NewBulkStatus(string jobId, string state, string message)
    {
        var now = DateTimeOffset.Now.ToString("o");
        return new JsonObject
        {
            ["available"] = true,
            ["state"] = state,
            ["jobId"] = jobId,
            ["totalItems"] = 0,
            ["completedItems"] = 0,
            ["reviewItems"] = 0,
            ["failedItems"] = 0,
            ["skippedItems"] = 0,
            ["totalTurns"] = 0,
            ["verbalizedTurns"] = 0,
            ["unresolvedTurns"] = 0,
            ["totalChunks"] = 0,
            ["completedChunks"] = 0,
            ["currentAudioItemId"] = string.Empty,
            ["currentFileName"] = string.Empty,
            ["currentRelativePath"] = string.Empty,
            ["currentChunkId"] = string.Empty,
            ["currentItemCompletedChunks"] = 0,
            ["currentItemTotalChunks"] = 0,
            ["startedAt"] = string.IsNullOrEmpty(jobId) ? string.Empty : now,
            ["completedAt"] = string.Empty,
            ["elapsedSec"] = 0,
            ["estimatedRemainingSec"] = 0,
            ["progressPercent"] = 0,
            ["updatedAt"] = now,
            ["message"] = message,
        };
    }

    private static bool IsBulkActive(JsonObject status)
    {
        var state = GetString(status, "state", string.Empty).ToLowerInvariant();
        return state is "starting" or "queued" or "running";
    }

    private static string NewBulkJobId()
        => $"audio-verbalization-bulk-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..("audio-verbalization-bulk-".Length + 15 + 1 + 8)];

    private static JsonObject NormalizeBulkStatus(JsonObject? status)
    {
        var copy = new JsonObject();
        if (status is not null)
        {
            foreach (var property in status)
            {
                copy[property.Key] = property.Value?.DeepClone();
            }
        }

        var totalItems = ClampNonNegativeInt(GetLong(copy, "totalItems", 0));
        var completedItems = ClampNonNegativeInt(GetLong(copy, "completedItems", 0));
        var reviewItems = ClampNonNegativeInt(GetLong(copy, "reviewItems", 0));
        var failedItems = ClampNonNegativeInt(GetLong(copy, "failedItems", 0));
        var skippedItems = ClampNonNegativeInt(GetLong(copy, "skippedItems", 0));
        var totalTurns = ClampNonNegativeInt(GetLong(copy, "totalTurns", 0));
        var verbalizedTurns = ClampNonNegativeInt(GetLong(copy, "verbalizedTurns", 0));
        var unresolvedTurns = ClampNonNegativeInt(GetLong(copy, "unresolvedTurns", 0));
        var totalChunks = ClampNonNegativeInt(GetLong(copy, "totalChunks", 0));
        var completedChunks = ClampNonNegativeInt(GetLong(copy, "completedChunks", 0));

        if (totalItems > 0)
        {
            completedItems = Math.Min(completedItems, totalItems);
            reviewItems = Math.Min(reviewItems, totalItems);
            failedItems = Math.Min(failedItems, totalItems);
            skippedItems = Math.Min(skippedItems, totalItems);
        }

        if (totalTurns > 0)
        {
            verbalizedTurns = Math.Min(verbalizedTurns, totalTurns);
            unresolvedTurns = Math.Min(unresolvedTurns, totalTurns);
            if (totalChunks > totalTurns)
            {
                totalChunks = totalTurns;
            }
        }

        if (totalChunks <= 0 && completedChunks > 0)
        {
            totalChunks = completedChunks;
        }
        if (totalChunks > 0)
        {
            completedChunks = Math.Min(completedChunks, totalChunks);
        }

        copy["totalItems"] = totalItems;
        copy["completedItems"] = completedItems;
        copy["reviewItems"] = reviewItems;
        copy["failedItems"] = failedItems;
        copy["skippedItems"] = skippedItems;
        copy["totalTurns"] = totalTurns;
        copy["verbalizedTurns"] = verbalizedTurns;
        copy["unresolvedTurns"] = unresolvedTurns;
        copy["totalChunks"] = totalChunks;
        copy["completedChunks"] = completedChunks;
        if (!IsBulkActive(copy))
        {
            copy["currentAudioItemId"] = string.Empty;
            copy["currentFileName"] = string.Empty;
            copy["currentRelativePath"] = string.Empty;
            copy["currentChunkId"] = string.Empty;
            copy["currentItemCompletedChunks"] = 0;
            copy["currentItemTotalChunks"] = 0;
        }
        return copy;
    }

    private static void UpdateBulkTiming(JsonObject status)
    {
        var startedAtText = GetString(status, "startedAt", string.Empty);
        var startedAt = DateTimeOffset.Now;
        if (!string.IsNullOrEmpty(startedAtText))
        {
            DateTimeOffset.TryParse(startedAtText, out startedAt);
        }

        var now = DateTimeOffset.Now;
        var elapsedSec = Math.Max(0, (now - startedAt).TotalSeconds);
        var totalItems = GetLong(status, "totalItems", 0);
        var finishedItems = GetLong(status, "completedItems", 0)
            + GetLong(status, "reviewItems", 0)
            + GetLong(status, "failedItems", 0)
            + GetLong(status, "skippedItems", 0);
        var totalTurns = GetLong(status, "totalTurns", 0);
        var processedTurns = GetLong(status, "verbalizedTurns", 0)
            + GetLong(status, "unresolvedTurns", 0);
        var totalChunks = GetLong(status, "totalChunks", 0);
        var completedChunks = GetLong(status, "completedChunks", 0);

        var progressRatio = 0.0;
        if (totalItems > 0)
        {
            progressRatio = Math.Max(progressRatio, finishedItems / (double)totalItems);
        }
        if (totalTurns > 0)
        {
            progressRatio = Math.Max(progressRatio, processedTurns / (double)totalTurns);
        }
        if (totalChunks > 0)
        {
            progressRatio = Math.Max(progressRatio, completedChunks / (double)totalChunks);
        }
        progressRatio = Math.Min(1, Math.Max(0, progressRatio));

        var remainingSec = 0.0;
        if (progressRatio > 0 && progressRatio < 1)
        {
            remainingSec = elapsedSec / progressRatio - elapsedSec;
        }

        status["elapsedSec"] = Math.Round(elapsedSec, 1);
        status["estimatedRemainingSec"] = Math.Round(remainingSec, 1);
        status["progressPercent"] = Math.Round(progressRatio * 100, 1);
        status["updatedAt"] = now.ToString("o");
    }

    private static int ClampNonNegativeInt(long value)
    {
        return (int)Math.Min(Math.Max(0, value), int.MaxValue);
    }

    private static long GetLong(JsonObject source, string name, long fallback)
    {
        if (!TryGetNode(source, name, out var node) || node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                var value = node.AsValue();
                if (value.TryGetValue<long>(out var longValue))
                {
                    return longValue;
                }
                if (value.TryGetValue<int>(out var intValue))
                {
                    return intValue;
                }
                if (value.TryGetValue<double>(out var doubleValue))
                {
                    return (long)Math.Round(doubleValue);
                }
            }

            return long.TryParse(ConvertTimelineText(node.GetValue<object>()), out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static int GetInt(JsonObject source, string name, int fallback)
    {
        if (!TryGetNode(source, name, out var node) || node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                if (node.AsValue().TryGetValue<int>(out var intValue))
                {
                    return intValue;
                }
                if (node.AsValue().TryGetValue<double>(out var doubleValue))
                {
                    return (int)Math.Round(doubleValue);
                }
            }

            return int.TryParse(
                ConvertTimelineText(node.GetValue<object>()),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static double GetDouble(JsonObject source, string name, double fallback)
    {
        if (!TryGetNode(source, name, out var node) || node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.AsValue().TryGetValue<double>(out var doubleValue) ? doubleValue : fallback;
            }

            return double.TryParse(
                ConvertTimelineText(node.GetValue<object>()),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static string GetString(JsonObject source, string name, string fallback)
    {
        if (!TryGetNode(source, name, out var node) || node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.String)
            {
                return ConvertTimelineText(node.GetValue<string>());
            }

            return ConvertTimelineText(node.GetValue<object>());
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static bool GetBool(JsonObject source, string name, bool fallback)
    {
        if (!TryGetNode(source, name, out var node) || node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.True)
            {
                return true;
            }
            if (node.GetValueKind() == JsonValueKind.False)
            {
                return false;
            }

            return bool.TryParse(ConvertTimelineText(node.GetValue<object>()), out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static JsonObject? GetObject(JsonObject? source, string name)
        => TryGetNode(source, name, out var node) ? node as JsonObject : null;

    private static JsonArray GetArray(JsonObject? source, string name)
        => TryGetNode(source, name, out var node) && node is JsonArray array ? array : new JsonArray();

    private static JsonObject NewIntObject(IReadOnlyDictionary<string, int> values)
    {
        var result = new JsonObject();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static bool TryGetNode(JsonObject? source, string name, out JsonNode? node)
    {
        if (source is null)
        {
            node = null;
            return false;
        }

        foreach (var property in source)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                node = property.Value;
                return true;
            }
        }

        node = null;
        return false;
    }

    private static string GetTimelineZipSafeSegment(string value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return "item";
        }

        var safe = System.Text.RegularExpressions.Regex.Replace(text, "[^A-Za-z0-9._-]+", "_").Trim('.', '_', '-');
        if (string.IsNullOrEmpty(safe))
        {
            return "item";
        }

        return safe.Length > 120 ? safe[..120] : safe;
    }

    private static string NewSingleJobId()
        => $"audio-verbalization-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..("audio-verbalization-".Length + 15 + 1 + 8)];

    private static void WriteJsonFile(string path, JsonObject payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static JsonObject? ReadJsonFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonArray CloneArray(JsonArray source)
    {
        var result = new JsonArray();
        foreach (var item in source)
        {
            result.Add(item?.DeepClone());
        }

        return result;
    }

    private static string ConvertTimelineText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }

    private sealed record AudioVerbalizationBulkTarget(
        string Product,
        string SourceId,
        string RelativePath,
        string SourcePath,
        string FileName,
        string AudioItemId,
        JsonObject Status,
        JsonObject Row);
}
