using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class TimelineProductActionService
{
    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineLocalApiOptions _options;
    private readonly TimelineProductApiClient _api;

    public TimelineProductActionService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        TimelineLocalApiOptions options,
        TimelineProductApiClient api)
    {
        _settings = settings;
        _operations = operations;
        _options = options;
        _api = api;
    }

    public Task<JsonObject> RefreshAudioAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForAudio",
            "audio_refresh",
            operationId => RefreshAudioCoreAsync(request, operationId, cancellationToken));
    }

    public Task<JsonObject> DownloadAudioItemsAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForAudio",
            "audio_items_download",
            operationId => DownloadAudioItemsCoreAsync(request, operationId, cancellationToken));
    }

    public Task<JsonObject> DeleteAudioGeneratedAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForAudio",
            "audio_files_delete_generated",
            operationId => DeleteAudioGeneratedCoreAsync(request, operationId, cancellationToken));
    }

    public Task<JsonObject> RefreshImageAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForImage",
            "image_refresh",
            operationId => RefreshImageCoreAsync(request, operationId, cancellationToken));
    }

    public Task<JsonObject> DownloadImageItemsAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForImage",
            "image_items_download",
            operationId => DownloadImageItemsCoreAsync(request, operationId, cancellationToken));
    }

    public Task<JsonObject> DeleteImageItemsAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForImage",
            "image_items_delete_generated",
            operationId => DeleteImageItemsCoreAsync(request, operationId, cancellationToken));
    }

    public Task<JsonObject> RefreshVideoAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForVideo",
            "video_refresh",
            operationId => RefreshVideoCoreAsync(request, operationId, cancellationToken));
    }

    public Task<JsonObject> RefreshVideoWithJobAsync(
        JsonObject? request,
        Action<JsonObject>? productJobProgress,
        CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForVideo",
            "video_refresh_job",
            operationId => RefreshVideoJobCoreAsync(request, productJobProgress, operationId, cancellationToken));
    }

    public Task<JsonObject> DownloadVideoItemsAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForVideo",
            "video_items_download",
            operationId => DownloadVideoItemsCoreAsync(request, operationId, cancellationToken));
    }

    public Task<JsonObject> RefreshPcAsync(CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForPC",
            "pc_refresh",
            operationId => RefreshPcCoreAsync(operationId, cancellationToken));
    }

    public Task<JsonObject> DownloadPcItemsAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForPC",
            "pc_items_download",
            operationId => DownloadPcItemsCoreAsync(request, operationId, cancellationToken));
    }

    private async Task<JsonObject> RefreshAudioCoreAsync(
        JsonObject? request,
        string operationId,
        CancellationToken cancellationToken)
    {
        var queueOnly = GetBool(request, "queueOnly", true);
        var timeoutSeconds = queueOnly ? 120 : 900;
        var maxItems = GetNullableInt(request, "maxItems");
        var requestBody = new JsonObject
        {
            ["queueOnly"] = queueOnly,
            ["reprocessDuplicates"] = GetBool(request, "reprocessDuplicates", false),
        };
        if (maxItems is > 0)
        {
            requestBody["maxItems"] = maxItems.Value;
        }

        var payload = await _api.PostJsonAsync(
            "audio",
            "TimelineForAudio",
            "/items/refresh",
            requestBody,
            timeoutSeconds,
            operationId,
            cancellationToken);
        var result = ConvertAudioRefreshResult(payload as JsonObject);
        result["queueOnly"] = queueOnly;
        return result;
    }

    private async Task<JsonObject> DownloadAudioItemsCoreAsync(
        JsonObject? request,
        string operationId,
        CancellationToken cancellationToken)
    {
        var itemIds = GetRequestItemIds(request)
            .Where(itemId => !itemId.Contains(':', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var outputPath = ResolveManagedDownloadFile(
            "audio",
            "TimelineForAudio-items",
            GetString(request, "outputPath", string.Empty));

        var payload = await _api.PostJsonAsync(
            "audio",
            "TimelineForAudio",
            "/items/download",
            new JsonObject
            {
                ["outputPath"] = outputPath,
                ["itemIds"] = NewStringArray(itemIds),
            },
            900,
            operationId,
            cancellationToken);
        var result = ConvertAudioDownloadItemsResult(payload as JsonObject);
        var returnedArchivePath = GetString(result, "archivePath", string.Empty);
        if (TestContainerPrefixedWindowsPath(returnedArchivePath))
        {
            throw new InvalidOperationException("TimelineForAudio API returned a container-prefixed Windows path. The product must write to the requested host path and return that host path. Returned path: " + returnedArchivePath);
        }

        var archivePath = ResolveDownloadLocalPath(returnedArchivePath);
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
        {
            throw new InvalidOperationException("TimelineForAudio API did not create a downloadable ZIP. Returned path: " + returnedArchivePath);
        }
        if (!Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("TimelineForAudio API created an unexpected download file type.");
        }
        if (!IsDownloadFileAllowed(archivePath))
        {
            throw new InvalidOperationException("TimelineForAudio API did not create the ZIP in the Timeline work directory. Returned path: " + returnedArchivePath);
        }

        return new JsonObject
        {
            ["archivePath"] = archivePath,
            ["itemIds"] = GetArray(result, "itemIds"),
        };
    }

    private async Task<JsonObject> DeleteAudioGeneratedCoreAsync(
        JsonObject? request,
        string operationId,
        CancellationToken cancellationToken)
    {
        var requestedItemIds = GetRequestItemIds(request);
        var requestedSourceFileIdentities = GetStringArray(request, "sourceFileIdentities")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requestedSourceFileIdentities.Count == 0)
        {
            requestedSourceFileIdentities = requestedItemIds.ToList();
        }

        if (requestedItemIds.Count == 0 && requestedSourceFileIdentities.Count == 0)
        {
            throw new InvalidOperationException("No audio files were selected for generated artifact deletion.");
        }

        var uniqueItemIds = requestedItemIds
            .Where(itemId => !itemId.Contains(':', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (uniqueItemIds.Count == 0)
        {
            return ConvertAudioDeleteGeneratedResult(
                new JsonObject
                {
                    ["dry_run"] = GetBool(request, "dryRun", false),
                    ["output_root_id"] = string.Empty,
                    ["output_root_path"] = string.Empty,
                    ["requested_item_ids"] = new JsonArray(),
                    ["requested_source_file_identities"] = NewStringArray(requestedSourceFileIdentities),
                    ["matched_count"] = 0,
                    ["missing_item_ids"] = new JsonArray(),
                    ["missing_source_file_identities"] = new JsonArray(),
                    ["catalog_rows_removed"] = 0,
                    ["media_dirs_removed"] = 0,
                    ["media_dirs"] = new JsonArray(),
                    ["unsafe_media_dirs"] = new JsonArray(),
                },
                [],
                requestedSourceFileIdentities);
        }

        var payload = await _api.PostJsonAsync(
            "audio",
            "TimelineForAudio",
            "/items/remove",
            new JsonObject
            {
                ["itemIds"] = NewStringArray(uniqueItemIds),
                ["dryRun"] = GetBool(request, "dryRun", false),
            },
            900,
            operationId,
            cancellationToken);
        return ConvertAudioDeleteGeneratedResult(payload as JsonObject, uniqueItemIds, requestedSourceFileIdentities);
    }

    private async Task<JsonObject> RefreshImageCoreAsync(
        JsonObject? request,
        string operationId,
        CancellationToken cancellationToken)
    {
        var requestBody = new JsonObject
        {
            ["reprocessDuplicates"] = GetBool(request, "reprocessDuplicates", false),
        };
        var maxItems = GetNullableInt(request, "maxItems");
        if (maxItems is > 0)
        {
            requestBody["maxItems"] = maxItems.Value;
        }

        var payload = await _api.PostJsonAsync(
            "image",
            "TimelineForImage",
            "/items/refresh",
            requestBody,
            900,
            operationId,
            cancellationToken);
        return new JsonObject
        {
            ["runId"] = GetString(payload as JsonObject, "run_id", string.Empty),
            ["state"] = GetString(payload as JsonObject, "state", string.Empty),
            ["sourceCount"] = GetInt(payload as JsonObject, "source_count", 0),
            ["processedCount"] = GetInt(payload as JsonObject, "processed_count", 0),
            ["skippedCount"] = GetInt(payload as JsonObject, "skipped_count", 0),
            ["failedCount"] = GetInt(payload as JsonObject, "failed_count", 0),
            ["archivePath"] = ConvertProductLocalPath("image", GetString(payload as JsonObject, "archive_path", string.Empty)),
        };
    }

    private async Task<JsonObject> DownloadImageItemsCoreAsync(
        JsonObject? request,
        string operationId,
        CancellationToken cancellationToken)
    {
        var itemIds = GetRequestItemIds(request);
        var destination = ResolveManagedDownloadDirectory(
            "image",
            GetStringAny(request, ["destinationPath", "downloadPath", "to"], string.Empty));
        var payload = await _api.PostJsonAsync(
            "image",
            "TimelineForImage",
            "/items/download",
            new JsonObject
            {
                ["itemIds"] = NewStringArray(itemIds),
                ["to"] = destination,
                ["overwrite"] = true,
            },
            900,
            operationId,
            cancellationToken);
        var archivePath = ConvertProductLocalPath(
            "image",
            GetStringAny(payload as JsonObject, ["archive_path", "archivePath", "download_path", "downloadPath"], string.Empty));
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
        {
            throw new InvalidOperationException("TimelineForImage API did not create a download ZIP.");
        }
        if (!IsDownloadFileAllowed(archivePath))
        {
            throw new InvalidOperationException("TimelineForImage API does not support Timeline-managed download destination yet.");
        }

        return new JsonObject
        {
            ["archivePath"] = archivePath,
            ["itemIds"] = NewStringArray(itemIds),
        };
    }

    private async Task<JsonObject> DeleteImageItemsCoreAsync(
        JsonObject? request,
        string operationId,
        CancellationToken cancellationToken)
    {
        var itemIds = GetRequestItemIds(request);
        var payload = await _api.PostJsonAsync(
            "image",
            "TimelineForImage",
            "/items/remove",
            new JsonObject
            {
                ["itemIds"] = NewStringArray(itemIds),
                ["dryRun"] = GetBool(request, "dryRun", false),
            },
            900,
            operationId,
            cancellationToken);
        return new JsonObject
        {
            ["itemIds"] = NewStringArray(itemIds),
            ["deletedCount"] = GetInt(payload as JsonObject, "removed_count", 0),
            ["missingItemIds"] = NewStringArray(GetStringArray(payload as JsonObject, "missing")),
        };
    }

    private async Task<JsonObject> RefreshVideoCoreAsync(
        JsonObject? request,
        string operationId,
        CancellationToken cancellationToken)
    {
        var requestBody = BuildVideoRefreshRequestBody(request);

        try
        {
            var payload = await _api.PostJsonAsync(
                "video",
                "TimelineForVideo",
                "/items/refresh",
                requestBody,
                3600,
                operationId,
                cancellationToken);
            return new JsonObject
            {
                ["runId"] = GetStringAny(payload as JsonObject, ["run_id", "runId", "refresh_id", "refreshId"], string.Empty),
                ["state"] = GetString(payload as JsonObject, "state", string.Empty),
                ["sourceCount"] = GetIntAny(payload as JsonObject, ["source_count", "sourceCount", "total"], 0),
                ["processedCount"] = GetIntAny(payload as JsonObject, ["processed_count", "processedCount", "processed"], 0),
                ["skippedCount"] = GetIntAny(payload as JsonObject, ["skipped_count", "skippedCount", "skipped"], 0),
                ["failedCount"] = GetIntAny(payload as JsonObject, ["failed_count", "failedCount", "failed"], 0),
                ["message"] = string.Empty,
            };
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Timed out waiting for lock", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("catalog.lock", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["runId"] = string.Empty,
                ["state"] = "busy",
                ["sourceCount"] = 0,
                ["processedCount"] = 0,
                ["skippedCount"] = 0,
                ["failedCount"] = 0,
                ["message"] = ex.Message,
            };
        }
    }

    private async Task<JsonObject> RefreshVideoJobCoreAsync(
        JsonObject? request,
        Action<JsonObject>? productJobProgress,
        string operationId,
        CancellationToken cancellationToken)
    {
        var requestBody = BuildVideoRefreshRequestBody(request);
        JsonObject status;
        try
        {
            var payload = await _api.PostJsonAsync(
                "video",
                "TimelineForVideo",
                "/jobs",
                new JsonObject
                {
                    ["type"] = "refresh",
                    ["options"] = requestBody.DeepClone(),
                },
                30,
                operationId,
                cancellationToken);
            status = ConvertVideoJobStatus(payload as JsonObject);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Endpoint not found", StringComparison.OrdinalIgnoreCase))
        {
            return await RefreshVideoCoreAsync(request, operationId, cancellationToken);
        }

        productJobProgress?.Invoke(status);
        var jobId = GetString(status, "jobId", string.Empty);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new InvalidOperationException("TimelineForVideo API did not return a job id.");
        }

        while (IsProductJobActive(status))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var payload = await _api.GetJsonAsync(
                "video",
                "TimelineForVideo",
                "/jobs/" + Uri.EscapeDataString(jobId),
                30,
                operationId,
                cancellationToken);
            status = ConvertVideoJobStatus(payload as JsonObject);
            productJobProgress?.Invoke(status);
        }

        var state = GetString(status, "state", string.Empty);
        if (state.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            var error = GetString(status, "error", string.Empty);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "TimelineForVideo refresh job failed."
                : error);
        }

        return ConvertVideoRefreshResult(status);
    }

    private static JsonObject BuildVideoRefreshRequestBody(JsonObject? request)
    {
        var requestBody = new JsonObject
        {
            ["reprocessDuplicates"] = GetBool(request, "reprocessDuplicates", false),
        };
        var maxItems = GetNullableInt(request, "maxItems");
        if (maxItems is > 0)
        {
            requestBody["maxItems"] = maxItems.Value;
        }
        var samplesPerVideo = GetNullableInt(request, "samplesPerVideo");
        if (samplesPerVideo is > 0)
        {
            requestBody["samplesPerVideo"] = samplesPerVideo.Value;
        }
        return requestBody;
    }

    private async Task<JsonObject> DownloadVideoItemsCoreAsync(
        JsonObject? request,
        string operationId,
        CancellationToken cancellationToken)
    {
        var itemIds = GetRequestItemIds(request);
        var payload = await _api.PostJsonAsync(
            "video",
            "TimelineForVideo",
            "/items/download",
            new JsonObject
            {
                ["itemIds"] = NewStringArray(itemIds),
            },
            900,
            operationId,
            cancellationToken);
        var archivePath = ConvertProductLocalPath(
            "video",
            GetStringAny(
                payload as JsonObject,
                ["archivePath", "archive_path", "downloadPath", "download_path", "zipPath", "zip_path"],
                string.Empty));
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
        {
            throw new InvalidOperationException("TimelineForVideo API did not create a download ZIP.");
        }
        if (!Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("TimelineForVideo API created an unexpected download file type.");
        }

        var counts = GetObject(payload as JsonObject, "counts");
        return new JsonObject
        {
            ["archivePath"] = archivePath,
            ["itemIds"] = NewStringArray(itemIds),
            ["itemCount"] = GetIntAny(counts, ["items", "itemCount"], 0),
        };
    }

    private async Task<JsonObject> RefreshPcCoreAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        var payload = await _api.PostJsonAsync(
            "pc",
            "TimelineForPC",
            "/items/refresh",
            new JsonObject(),
            900,
            operationId,
            cancellationToken);
        var timelineArtifacts = GetObject(payload as JsonObject, "timeline_artifacts");
        return new JsonObject
        {
            ["runId"] = GetStringAny(payload as JsonObject, ["run_id", "runId"], string.Empty),
            ["state"] = GetString(payload as JsonObject, "state", string.Empty),
            ["itemId"] = GetString(timelineArtifacts, "item_id", string.Empty),
            ["eventId"] = GetString(timelineArtifacts, "event_id", string.Empty),
            ["reportPath"] = ConvertProductLocalPath("pc", GetStringAny(payload as JsonObject, ["report_path", "reportPath"], string.Empty)),
            ["completedAt"] = GetStringAny(payload as JsonObject, ["completed_at_utc", "completedAtUtc"], string.Empty),
        };
    }

    private async Task<JsonObject> DownloadPcItemsCoreAsync(
        JsonObject? request,
        string operationId,
        CancellationToken cancellationToken)
    {
        var itemIds = GetRequestItemIds(request);
        var destination = ResolveManagedDownloadDirectory(
            "pc",
            GetStringAny(request, ["destinationPath", "downloadPath", "to", "outputPath"], string.Empty));

        var payload = await _api.PostJsonAsync(
            "pc",
            "TimelineForPC",
            "/items/download",
            new JsonObject
            {
                ["to"] = destination,
                ["overwrite"] = true,
                ["itemIds"] = NewStringArray(itemIds),
            },
            900,
            operationId,
            cancellationToken);
        var archivePath = ResolveDownloadLocalPath(
            GetStringAny(payload as JsonObject, ["archive_path", "archivePath", "download_path", "downloadPath", "destination_path", "destinationPath"], string.Empty));
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
        {
            throw new InvalidOperationException("TimelineForPC API did not create a download ZIP.");
        }
        if (!IsDownloadFileAllowed(archivePath))
        {
            throw new InvalidOperationException("TimelineForPC API does not support Timeline-managed download destination yet.");
        }

        return new JsonObject
        {
            ["archivePath"] = archivePath,
            ["itemIds"] = NewStringArray(itemIds),
        };
    }

    private async Task<JsonObject> InvokeWebOperationAsync(
        string productName,
        string action,
        Func<string, Task<JsonObject>> operation)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            productName,
            action,
            "started",
            "Web operation started.");

        try
        {
            var result = await operation(operationId);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                productName,
                action,
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["message"] = GetString(result, "message", string.Empty),
                    ["state"] = GetString(result, "state", string.Empty),
                });
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                productName,
                action,
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private JsonObject ConvertAudioDownloadItemsResult(JsonObject? payload)
    {
        var archivePath = GetStringAny(
            payload,
            ["archive_path", "archivePath", "destination_path", "destinationPath", "download_path", "downloadPath", "zip_path", "zipPath"],
            string.Empty);
        return new JsonObject
        {
            ["archivePath"] = ConvertProductLocalPath("audio", archivePath),
            ["itemIds"] = NewStringArray(GetStringArrayAny(payload, ["item_ids", "itemIds"])),
        };
    }

    private static JsonObject ConvertAudioDeleteGeneratedResult(
        JsonObject? payload,
        IReadOnlyCollection<string> requestedItemIds,
        IReadOnlyCollection<string> requestedSourceFileIdentities)
    {
        var resultRequestedItemIds = GetStringArray(payload, "requested_item_ids");
        if (resultRequestedItemIds.Count == 0)
        {
            resultRequestedItemIds = requestedItemIds.ToList();
        }

        var resultRequestedSourceFileIdentities = GetStringArray(payload, "requested_source_file_identities");
        if (resultRequestedSourceFileIdentities.Count == 0)
        {
            resultRequestedSourceFileIdentities = requestedSourceFileIdentities.ToList();
        }

        var missingItemIds = GetStringArray(payload, "missing_item_ids");
        var missingSourceFileIdentities = GetStringArray(payload, "missing_source_file_identities");
        if (missingSourceFileIdentities.Count == 0)
        {
            missingSourceFileIdentities = missingItemIds.ToList();
        }

        var removedCount = GetIntAny(
            payload,
            ["catalog_rows_removed", "removed_count", "items_removed", "item_count"],
            0);
        var matchedCount = GetIntAny(
            payload,
            ["matched_count", "removed_count", "items_removed", "item_count"],
            removedCount);

        return new JsonObject
        {
            ["dryRun"] = GetBool(payload, "dry_run", false),
            ["outputRootId"] = GetString(payload, "output_root_id", string.Empty),
            ["outputRootPath"] = GetString(payload, "output_root_path", string.Empty),
            ["requestedItemIds"] = NewStringArray(resultRequestedItemIds),
            ["requestedSourceFileIdentities"] = NewStringArray(resultRequestedSourceFileIdentities),
            ["matchedCount"] = matchedCount,
            ["missingItemIds"] = NewStringArray(missingItemIds),
            ["missingSourceFileIdentities"] = NewStringArray(missingSourceFileIdentities),
            ["catalogRowsRemoved"] = removedCount,
            ["mediaDirsRemoved"] = GetInt(payload, "media_dirs_removed", 0),
            ["mediaDirs"] = NewStringArray(GetStringArray(payload, "media_dirs")),
            ["unsafeMediaDirs"] = NewStringArray(GetStringArray(payload, "unsafe_media_dirs")),
        };
    }

    private static JsonObject ConvertAudioRefreshResult(JsonObject? payload)
    {
        var queuedLimit = GetNullableInt(payload, "queued_limit");
        return new JsonObject
        {
            ["state"] = GetString(payload, "state", string.Empty),
            ["runId"] = GetString(payload, "run_id", string.Empty),
            ["runDir"] = GetString(payload, "run_dir", string.Empty),
            ["queueOnly"] = GetBool(payload, "queue_only", true),
            ["totalDiscovered"] = GetInt(payload, "total_discovered", 0),
            ["selectedCount"] = GetInt(payload, "selected_count", 0),
            ["queuedCount"] = GetInt(payload, "queued_count", 0),
            ["skippedCount"] = GetInt(payload, "skipped_count", 0),
            ["deferredCount"] = GetInt(payload, "deferred_count", 0),
            ["queuedLimit"] = queuedLimit.HasValue ? queuedLimit.Value : null,
        };
    }

    private static JsonObject ConvertVideoJobStatus(JsonObject? payload)
    {
        var progress = GetObject(payload, "progress");
        var result = GetObject(payload, "result");
        return new JsonObject
        {
            ["productId"] = GetString(payload, "productId", "video"),
            ["productName"] = GetString(payload, "productName", "TimelineForVideo"),
            ["type"] = GetString(payload, "type", "refresh"),
            ["jobId"] = GetString(payload, "jobId", string.Empty),
            ["state"] = GetString(payload, "state", string.Empty),
            ["phase"] = GetString(payload, "phase", string.Empty),
            ["stage"] = GetString(payload, "stage", string.Empty),
            ["message"] = GetString(payload, "message", string.Empty),
            ["progress"] = new JsonObject
            {
                ["percent"] = GetDouble(progress, "percent", 0.0),
                ["current"] = GetInt(progress, "current", 0),
                ["total"] = GetInt(progress, "total", 0),
                ["unit"] = GetString(progress, "unit", "files"),
                ["currentItem"] = GetString(progress, "currentItem", string.Empty),
            },
            ["startedAt"] = GetString(payload, "startedAt", string.Empty),
            ["updatedAt"] = GetString(payload, "updatedAt", string.Empty),
            ["completedAt"] = GetString(payload, "completedAt", string.Empty),
            ["error"] = GetString(payload, "error", string.Empty),
            ["warnings"] = GetArray(payload, "warnings"),
            ["result"] = result?.DeepClone(),
        };
    }

    private static JsonObject ConvertVideoRefreshResult(JsonObject jobStatus)
    {
        var result = GetObject(jobStatus, "result");
        var counts = GetObject(result, "counts");
        return new JsonObject
        {
            ["runId"] = GetString(jobStatus, "jobId", string.Empty),
            ["state"] = GetString(jobStatus, "state", string.Empty),
            ["sourceCount"] = GetInt(counts, "sourceFiles", 0),
            ["processedCount"] = GetInt(counts, "processedItems", 0),
            ["skippedCount"] = GetInt(counts, "skippedItems", 0),
            ["failedCount"] = GetInt(counts, "failedItems", 0),
            ["message"] = GetString(jobStatus, "message", string.Empty),
            ["job"] = jobStatus.DeepClone(),
        };
    }

    private static bool IsProductJobActive(JsonObject? status)
    {
        var state = GetString(status, "state", string.Empty);
        return state.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || state.Equals("running", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveManagedDownloadDirectory(string productId, string requestedPath)
    {
        var downloadRoot = GetDownloadRoot();
        var candidate = ConvertTimelineText(requestedPath);
        string localPath;
        if (!string.IsNullOrEmpty(candidate))
        {
            localPath = ResolveDownloadLocalPath(candidate);
            if (string.IsNullOrEmpty(localPath))
            {
                localPath = candidate;
            }
            if (!Path.IsPathRooted(localPath))
            {
                localPath = Path.Combine(downloadRoot, localPath);
            }
        }
        else
        {
            localPath = Path.Combine(downloadRoot, productId);
        }

        if (!IsPathUnderRoot(localPath, downloadRoot))
        {
            throw new InvalidOperationException("Download staging path must be under the Timeline work directory.");
        }

        Directory.CreateDirectory(localPath);
        return Path.GetFullPath(localPath);
    }

    private string ResolveManagedDownloadFile(string productId, string filePrefix, string requestedPath)
    {
        var downloadRoot = GetDownloadRoot();
        var candidate = ConvertTimelineText(requestedPath);
        string localPath;
        if (!string.IsNullOrEmpty(candidate))
        {
            localPath = ResolveDownloadLocalPath(candidate);
            if (string.IsNullOrEmpty(localPath))
            {
                localPath = candidate;
            }
            if (!Path.IsPathRooted(localPath))
            {
                localPath = Path.Combine(downloadRoot, localPath);
            }
            if (string.IsNullOrEmpty(Path.GetExtension(localPath)))
            {
                localPath += ".zip";
            }
        }
        else
        {
            var directory = Path.Combine(downloadRoot, productId);
            Directory.CreateDirectory(directory);
            localPath = Path.Combine(directory, $"{filePrefix}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        }

        if (!IsPathUnderRoot(localPath, downloadRoot))
        {
            throw new InvalidOperationException("Download staging path must be under the Timeline work directory.");
        }

        var parent = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        return Path.GetFullPath(localPath);
    }

    private bool IsDownloadFileAllowed(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath)
                && Path.GetExtension(fullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                && IsPathUnderRoot(fullPath, GetDownloadRoot());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private string ResolveDownloadLocalPath(string path)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        if (text.StartsWith("/mnt/c/", StringComparison.OrdinalIgnoreCase))
        {
            return "C:\\" + text[7..].Replace("/", "\\");
        }

        var localPath = TimelinePathConverter.ConvertTimelineWindowsPath(text, _options);
        return string.IsNullOrEmpty(localPath) ? text : localPath;
    }

    private string ConvertProductLocalPath(string productId, string path)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        if (text.Equals("/workspace", StringComparison.OrdinalIgnoreCase))
        {
            return GetProductPath(productId);
        }
        if (text.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(GetProductPath(productId), text["/workspace/".Length..].Replace("/", "\\"));
        }
        if (text.StartsWith("/mnt/c/", StringComparison.OrdinalIgnoreCase))
        {
            return "C:\\" + text[7..].Replace("/", "\\");
        }
        if (Path.IsPathRooted(text))
        {
            return text;
        }

        return Path.Combine(GetProductPath(productId), text);
    }

    private string GetDownloadRoot()
    {
        var root = Path.Combine(_settings.GetWorkDirectory(), "downloads");
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private string GetProductPath(string productId)
    {
        foreach (var product in _settings.ReadSettings().ProductRegistry.Products)
        {
            if (product.Id.Equals(productId, StringComparison.OrdinalIgnoreCase))
            {
                return product.Path ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TestAudioMissingCommand(string message, string commandName)
    {
        var text = ConvertTimelineText(message).ToLowerInvariant();
        return text.Contains($"invalid choice: '{commandName}'", StringComparison.Ordinal)
            || text.Contains($"invalid choice: \"{commandName}\"", StringComparison.Ordinal)
            || text.Contains("argument command: invalid choice", StringComparison.Ordinal);
    }

    private static bool TestContainerPrefixedWindowsPath(string path)
    {
        return Regex.IsMatch(
            ConvertTimelineText(path),
            "^/[A-Za-z0-9_.-]+/[A-Za-z]:[\\\\/]",
            RegexOptions.CultureInvariant);
    }

    private static List<string> GetRequestItemIds(JsonObject? request)
    {
        return GetStringArray(request, "itemIds")
            .Where(itemId => !string.IsNullOrEmpty(itemId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static JsonArray GetArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array
            ? array.DeepClone().AsArray()
            : new JsonArray();
    }

    private static JsonObject? GetObject(JsonObject? source, string name)
        => GetNode(source, name) as JsonObject;

    private static JsonNode? GetNode(JsonObject? source, string name)
    {
        if (source is null)
        {
            return null;
        }

        foreach (var property in source)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static JsonNode? GetNodeAny(JsonObject? source, string[] names)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is not null)
            {
                return node;
            }
        }

        return null;
    }

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertNodeToString(node, fallback);
    }

    private static string GetStringAny(JsonObject? source, string[] names, string fallback)
    {
        var node = GetNodeAny(source, names);
        return node is null ? fallback : ConvertNodeToString(node, fallback);
    }

    private static int GetInt(JsonObject? source, string name, int fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertNodeToInt(node, fallback);
    }

    private static int GetIntAny(JsonObject? source, string[] names, int fallback)
    {
        var node = GetNodeAny(source, names);
        return node is null ? fallback : ConvertNodeToInt(node, fallback);
    }

    private static double GetDouble(JsonObject? source, string name, double fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertNodeToDouble(node, fallback);
    }

    private static int? GetNullableInt(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        var value = ConvertNodeToInt(node, int.MinValue);
        return value == int.MinValue ? null : value;
    }

    private static bool GetBool(JsonObject? source, string name, bool fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }
        if (node.GetValueKind() == JsonValueKind.True)
        {
            return true;
        }
        if (node.GetValueKind() == JsonValueKind.False)
        {
            return false;
        }

        var text = ConvertNodeToString(node, string.Empty).ToLowerInvariant();
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        return text is "1" or "true" or "yes" or "on";
    }

    private static List<string> GetStringArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return [];
        }
        if (node is JsonArray array)
        {
            return array
                .Select(item => ConvertNodeToString(item, string.Empty))
                .Where(text => !string.IsNullOrEmpty(text))
                .ToList();
        }

        var text = ConvertNodeToString(node, string.Empty);
        return string.IsNullOrEmpty(text) ? [] : [text];
    }

    private static List<string> GetStringArrayAny(JsonObject? source, string[] names)
    {
        foreach (var name in names)
        {
            var values = GetStringArray(source, name);
            if (values.Count > 0)
            {
                return values;
            }
        }

        return [];
    }

    private static JsonArray NewStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            var text = ConvertTimelineText(value);
            if (!string.IsNullOrEmpty(text))
            {
                array.Add(text);
            }
        }

        return array;
    }

    private static int ConvertNodeToInt(JsonNode node, int fallback)
    {
        try
        {
            if (node.GetValueKind() == JsonValueKind.Number && node.GetValue<int>() is var value)
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
        }

        return int.TryParse(ConvertNodeToString(node, string.Empty), out var parsed)
            ? parsed
            : fallback;
    }

    private static double ConvertNodeToDouble(JsonNode node, double fallback)
    {
        try
        {
            if (node.GetValueKind() == JsonValueKind.Number && node.GetValue<double>() is var value)
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
        }

        return double.TryParse(ConvertNodeToString(node, string.Empty), out var parsed)
            ? parsed
            : fallback;
    }

    private static string ConvertNodeToString(JsonNode? node, string fallback)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return fallback;
        }
        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.String => node.GetValue<string>()?.Trim() ?? string.Empty,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => node.ToJsonString().Trim(),
                _ => node.ToJsonString().Trim(),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    private static string ConvertTimelineText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            JsonNode node => ConvertNodeToString(node, string.Empty),
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }
}
