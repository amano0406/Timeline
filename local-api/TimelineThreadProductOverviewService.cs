using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class TimelineThreadProductOverviewService
{
    private const int MaxThreadProductJobStatusPollFailures = 20;

    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineLocalApiOptions _options;
    private readonly TimelineProductApiClient _api;

    public TimelineThreadProductOverviewService(
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

    public JsonObject GetWindowsCodexOverview()
    {
        return InvokeWebOperation(
            "TimelineForWindowsCodex",
            "windows_codex_overview",
            () =>
            {
                var productPath = GetProductPath("windows-codex");
                var productFound = !string.IsNullOrEmpty(productPath) && Directory.Exists(productPath);
                var settings = ReadWindowsCodexSettings(productPath);
                var convertedSettings = ConvertWindowsCodexSettings(settings);
                var outputRoot = GetString(convertedSettings, "outputsRoot", string.Empty);
                var outputLocalPath = ConvertWindowsPath(outputRoot);
                var threadCount = CountThreadDirectories(outputLocalPath);
                var messages = new List<string>();
                if (!productFound)
                {
                    messages.Add("TimelineForWindowsCodex was not found.");
                }

                return new JsonObject
                {
                    ["productFound"] = productFound,
                    ["productPath"] = productPath,
                    ["settingsValid"] = productFound
                        && !string.IsNullOrEmpty(outputRoot)
                        && GetArray(convertedSettings, "sourceRoots").OfType<JsonObject>().Any(root => GetBool(root, "exists", false) && GetBool(root, "readable", false)),
                    ["settings"] = convertedSettings,
                    ["current"] = NewWindowsCodexCurrent(threadCount),
                    ["threads"] = new JsonArray(),
                    ["jobs"] = new JsonArray(),
                    ["message"] = string.Join(" ", messages.Where(message => !string.IsNullOrEmpty(message))),
                };
            });
    }

    public JsonObject GetChatGptOverview()
    {
        return InvokeWebOperation(
            "TimelineForChatGPT",
            "chatgpt_overview",
            () =>
            {
                var productPath = GetProductPath("chatgpt");
                var productFound = !string.IsNullOrEmpty(productPath) && Directory.Exists(productPath);
                var settings = ReadChatGptSettings(productPath);
                var outputRoot = ConvertChatGptDirectoryRoot(
                    GetObject(settings, "outputRoot") ?? new JsonObject(),
                    "output",
                    "Output",
                    productPath);
                var stateRoot = ConvertChatGptDirectoryRoot(
                    GetObject(settings, "stateRoot") ?? new JsonObject(),
                    "state",
                    "State",
                    productPath);
                var outputLocalPath = GetString(outputRoot, "displayPath", string.Empty);
                var itemCount = CountThreadDirectories(outputLocalPath);
                var messages = new List<string>();
                if (!productFound)
                {
                    messages.Add("TimelineForChatGPT was not found.");
                }
                if (!GetBool(settings, "settingsFound", false))
                {
                    messages.Add("settings.json was not found.");
                }
                if (string.IsNullOrEmpty(GetString(outputRoot, "path", string.Empty)))
                {
                    messages.Add("Output root is not configured.");
                }

                return new JsonObject
                {
                    ["productFound"] = productFound,
                    ["productPath"] = productPath,
                    ["settingsFound"] = GetBool(settings, "settingsFound", false),
                    ["settingsPath"] = GetString(settings, "path", string.Empty),
                    ["settingsValid"] = productFound && GetBool(settings, "settingsFound", false) && !string.IsNullOrEmpty(GetString(outputRoot, "path", string.Empty)),
                    ["inputRoots"] = new JsonArray(),
                    ["masterRoot"] = outputRoot.DeepClone(),
                    ["outputRoot"] = outputRoot,
                    ["stateRoot"] = stateRoot,
                    ["recursive"] = GetBool(settings, "recursive", false),
                    ["profile"] = GetString(settings, "profile", string.Empty),
                    ["processableInputCount"] = itemCount,
                    ["itemCount"] = itemCount,
                    ["latestRefresh"] = NewChatGptRefreshSummary(),
                    ["threads"] = new JsonArray(),
                    ["jobs"] = new JsonArray(),
                    ["message"] = string.Join(" ", messages.Where(message => !string.IsNullOrEmpty(message))),
                };
            });
    }

    public Task<JsonObject> GetWindowsCodexThreadsAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForWindowsCodex",
            "windows_codex_items_list",
            operationId => ListThreadItemsViaApiAsync(
                "windows-codex",
                "TimelineForWindowsCodex",
                GetWindowsCodexOutputLocalPath(),
                page,
                pageSize,
                operationId,
                cancellationToken));
    }

    public Task<JsonObject> GetWindowsCodexThreadDetailAsync(string itemId, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForWindowsCodex",
            "windows_codex_items_detail",
            operationId => ReadThreadDetailViaApiAsync(
                "windows-codex",
                "TimelineForWindowsCodex",
                itemId,
                operationId,
                cancellationToken));
    }

    public Task<JsonObject> RefreshWindowsCodexAsync(CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForWindowsCodex",
            "windows_codex_refresh",
            operationId => RefreshWindowsCodexCoreAsync(operationId, cancellationToken));
    }

    public Task<JsonObject> RefreshWindowsCodexWithJobAsync(
        Action<JsonObject>? productJobProgress,
        CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForWindowsCodex",
            "windows_codex_refresh_job",
            operationId => RefreshThreadProductJobCoreAsync(
                "windows-codex",
                "TimelineForWindowsCodex",
                new JsonObject(),
                productJobProgress,
                operationId,
                () => RefreshWindowsCodexCoreAsync(operationId, cancellationToken),
                ConvertWindowsCodexCurrent,
                cancellationToken));
    }

    public Task<JsonObject> DownloadWindowsCodexItemsAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForWindowsCodex",
            "windows_codex_items_download",
            async operationId =>
            {
                var itemIds = GetRequestItemIds(request);
                var destination = ResolveManagedDownloadDirectory("windows-codex", GetString(request, "outputPath", string.Empty));
                var payload = await _api.PostJsonAsync(
                    "windows-codex",
                    "TimelineForWindowsCodex",
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
                return ConvertThreadDownloadResult(payload as JsonObject, itemIds, "TimelineForWindowsCodex");
            });
    }

    public Task<JsonObject> DeleteWindowsCodexItemsAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForWindowsCodex",
            "windows_codex_items_delete_generated",
            async operationId =>
            {
                var itemIds = GetRequestItemIds(request);
                var payload = await _api.PostJsonAsync(
                    "windows-codex",
                    "TimelineForWindowsCodex",
                    "/items/remove",
                    new JsonObject
                    {
                        ["itemIds"] = NewStringArray(itemIds),
                    },
                    900,
                    operationId,
                    cancellationToken);
                return ConvertThreadRemoveResult(payload as JsonObject, itemIds);
            });
    }

    public Task<JsonObject> GetChatGptThreadsAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForChatGPT",
            "chatgpt_items_list",
            operationId => ListThreadItemsViaApiAsync(
                "chatgpt",
                "TimelineForChatGPT",
                GetChatGptOutputLocalPath(),
                page,
                pageSize,
                operationId,
                cancellationToken));
    }

    public Task<JsonObject> GetChatGptThreadDetailAsync(string itemId, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForChatGPT",
            "chatgpt_items_detail",
            operationId => ReadThreadDetailViaApiAsync(
                "chatgpt",
                "TimelineForChatGPT",
                itemId,
                operationId,
                cancellationToken));
    }

    public Task<JsonObject> RefreshChatGptAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForChatGPT",
            "chatgpt_refresh",
            async operationId =>
            {
                var filePath = GetString(request, "filePath", string.Empty);
                if (string.IsNullOrEmpty(filePath))
                {
                    throw new InvalidOperationException("ChatGPT export ZIP is required.");
                }

                var requestBody = new JsonObject
                {
                    ["filePath"] = filePath,
                };
                var downloadTo = GetString(request, "downloadTo", string.Empty);
                if (!string.IsNullOrEmpty(downloadTo))
                {
                    requestBody["downloadTo"] = downloadTo;
                }
                if (GetBool(request, "overwrite", false))
                {
                    requestBody["overwrite"] = true;
                }

                var payload = await _api.PostJsonAsync(
                    "chatgpt",
                    "TimelineForChatGPT",
                    "/items/refresh",
                    requestBody,
                    1800,
                    operationId,
                    cancellationToken);
                return ConvertChatGptRefreshSummary(payload as JsonObject);
            });
    }

    public Task<JsonObject> DownloadChatGptItemsAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForChatGPT",
            "chatgpt_items_download",
            async operationId =>
            {
                var itemIds = GetRequestItemIds(request);
                if (itemIds.Count > 0)
                {
                    throw new InvalidOperationException("TimelineForChatGPT API does not support selected item download yet.");
                }

                var destination = ResolveManagedDownloadDirectory("chatgpt", GetString(request, "outputPath", string.Empty));
                var payload = await _api.PostJsonAsync(
                    "chatgpt",
                    "TimelineForChatGPT",
                    "/items/download",
                    new JsonObject
                    {
                        ["to"] = destination,
                        ["overwrite"] = true,
                    },
                    900,
                    operationId,
                    cancellationToken);
                return ConvertThreadDownloadResult(payload as JsonObject, itemIds, "TimelineForChatGPT");
            });
    }

    public JsonObject DeleteChatGptItems(JsonObject? request)
    {
        return InvokeWebOperation(
            "TimelineForChatGPT",
            "chatgpt_items_delete_generated",
            () => throw new InvalidOperationException("TimelineForChatGPT does not support generated item removal in the current product API contract."));
    }

    private JsonObject InvokeWebOperation(string productName, string action, Func<JsonObject> operation)
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
            var result = operation();
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
                    ["settingsValid"] = GetBool(result, "settingsValid", false),
                });
            return result;
        }
        catch (Exception ex)
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
                    ["settingsValid"] = GetBool(result, "settingsValid", false),
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

    private async Task<JsonObject> RefreshWindowsCodexCoreAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        var payload = await _api.PostJsonAsync(
            "windows-codex",
            "TimelineForWindowsCodex",
            "/items/refresh",
            new JsonObject(),
            900,
            operationId,
            cancellationToken);
        return ConvertWindowsCodexCurrent(payload as JsonObject);
    }

    private async Task<JsonObject> RefreshThreadProductJobCoreAsync(
        string productId,
        string productName,
        JsonObject requestBody,
        Action<JsonObject>? productJobProgress,
        string operationId,
        Func<Task<JsonObject>> fallback,
        Func<JsonObject?, JsonObject> convertResult,
        CancellationToken cancellationToken)
    {
        JsonObject status;
        try
        {
            var payload = await _api.PostJsonAsync(
                productId,
                productName,
                "/jobs",
                new JsonObject
                {
                    ["type"] = "refresh",
                    ["options"] = requestBody.DeepClone(),
                },
                30,
                operationId,
                cancellationToken);
            status = ConvertThreadProductJobStatus(payload as JsonObject, productId, productName);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Endpoint not found", StringComparison.OrdinalIgnoreCase))
        {
            return await fallback();
        }

        productJobProgress?.Invoke(status);
        var jobId = GetString(status, "jobId", string.Empty);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            if (IsThreadProductJobActive(status))
            {
                throw new InvalidOperationException(productName + " API did not return a job id.");
            }

            return convertResult(GetObject(status, "result"));
        }

        var pollFailures = 0;
        while (IsThreadProductJobActive(status))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            try
            {
                var payload = await _api.GetJsonAsync(
                    productId,
                    productName,
                    "/jobs/" + Uri.EscapeDataString(jobId),
                    30,
                    operationId,
                    cancellationToken);
                status = ConvertThreadProductJobStatus(payload as JsonObject, productId, productName);
                pollFailures = 0;
                productJobProgress?.Invoke(status);
            }
            catch (Exception ex) when (IsTransientThreadProductJobPollException(ex, cancellationToken))
            {
                pollFailures++;
                if (pollFailures >= MaxThreadProductJobStatusPollFailures)
                {
                    throw new TimeoutException($"Timed out repeatedly while polling {productName} refresh job status.", ex);
                }

                productJobProgress?.Invoke(WithPollingWarning(
                    status,
                    productName + " status polling timed out; retrying.",
                    pollFailures));
            }
        }

        var state = GetString(status, "state", string.Empty);
        if (state.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || state.Equals("interrupted", StringComparison.OrdinalIgnoreCase))
        {
            var error = GetString(status, "error", string.Empty);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? productName + " refresh job failed."
                : error);
        }

        var result = convertResult(GetObject(status, "result"));
        result["job"] = status.DeepClone();
        return result;
    }

    private async Task<JsonObject> ListThreadItemsViaApiAsync(
        string productId,
        string productName,
        string rootPath,
        int page,
        int pageSize,
        string operationId,
        CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Max(1, pageSize);
        var payload = await _api.PostJsonAsync(
            productId,
            productName,
            "/items/list",
            new JsonObject
            {
                ["page"] = effectivePage,
                ["pageSize"] = effectivePageSize,
            },
            120,
            operationId,
            cancellationToken);

        return ConvertThreadListResult(payload as JsonObject, rootPath, effectivePage, effectivePageSize);
    }

    private JsonObject ConvertThreadListResult(JsonObject? payload, string rootPath, int page, int pageSize)
    {
        var items = GetArray(payload, "items")
            .OfType<JsonObject>()
            .ToList();
        var threads = new JsonArray();
        foreach (var item in items)
        {
            threads.Add(ConvertThreadItemRow(item, rootPath));
        }

        var total = GetIntAny(
            payload,
            ["total", "total_items", "totalItems", "item_count", "itemCount"],
            threads.Count);
        if (total < threads.Count)
        {
            total = threads.Count;
        }

        var pagination = ConvertThreadPagination(
            GetObject(payload, "pagination"),
            page,
            pageSize,
            total,
            threads.Count);

        return NewThreadListResult(threads, pagination, total);
    }

    private async Task<JsonObject> ReadThreadDetailViaApiAsync(
        string productId,
        string productName,
        string itemId,
        string operationId,
        CancellationToken cancellationToken)
    {
        var payload = await _api.PostJsonAsync(
            productId,
            productName,
            "/items/detail",
            new JsonObject
            {
                ["itemId"] = itemId,
            },
            120,
            operationId,
            cancellationToken);

        return ConvertThreadDetailResult(payload as JsonObject, itemId);
    }

    private JsonObject ConvertThreadDetailResult(JsonObject? payload, string fallbackItemId)
    {
        var itemId = GetStringAny(
            payload,
            ["itemId", "item_id", "threadId", "thread_id", "conversationId", "conversation_id", "id"],
            fallbackItemId);
        if (payload is null)
        {
            return NewUnavailableThreadDetail(
                itemId,
                string.Empty,
                string.Empty,
                string.Empty,
                "Thread could not be read.");
        }

        var messages = new JsonArray();
        foreach (var messageNode in GetArray(payload, "messages"))
        {
            messages.Add(CloneValueOrNull(messageNode));
        }

        var messageCount = GetIntAny(payload, ["messageCount", "message_count"], messages.Count);
        if (messageCount < messages.Count)
        {
            messageCount = messages.Count;
        }

        var title = GetStringAny(payload, ["title", "preferredTitle", "preferred_title", "name"], itemId);
        return new JsonObject
        {
            ["available"] = GetBool(payload, "available", false),
            ["itemId"] = itemId,
            ["title"] = title,
            ["createdAt"] = GetStringAny(payload, ["createdAt", "created_at"], string.Empty),
            ["updatedAt"] = GetStringAny(payload, ["updatedAt", "updated_at"], string.Empty),
            ["messageCount"] = messageCount,
            ["messages"] = messages,
            ["directoryPath"] = ConvertWindowsPath(GetStringAny(payload, ["directoryPath", "directory_path", "itemDir", "item_dir"], string.Empty)),
            ["timelinePath"] = ConvertWindowsPath(GetStringAny(payload, ["timelinePath", "timeline_path"], string.Empty)),
            ["convertInfoPath"] = ConvertWindowsPath(GetStringAny(payload, ["convertInfoPath", "convert_info_path"], string.Empty)),
            ["message"] = GetString(payload, "message", string.Empty),
        };
    }

    private static JsonObject ConvertThreadPagination(
        JsonObject? source,
        int fallbackPage,
        int fallbackPageSize,
        int fallbackTotal,
        int returnedItems)
    {
        var page = GetIntAny(source, ["page"], fallbackPage);
        var pageSize = GetIntAny(source, ["pageSize", "page_size"], fallbackPageSize);
        var total = GetIntAny(source, ["totalItems", "total_items", "total"], fallbackTotal);
        var returned = GetIntAny(source, ["returnedItems", "returned_items"], returnedItems);
        return NewPagination(page, pageSize, total, returned);
    }

    private JsonObject ConvertThreadDownloadResult(
        JsonObject? payload,
        IReadOnlyCollection<string> requestedItemIds,
        string productName)
    {
        var archivePath = ConvertWindowsPath(GetStringAny(
            payload,
            [
                "archivePath",
                "archive_path",
                "downloadPath",
                "download_path",
                "destinationPath",
                "destination_path",
                "downloadZipPath",
                "download_zip_path",
                "zipPath",
                "zip_path",
            ],
            string.Empty));
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
        {
            throw new InvalidOperationException(productName + " API did not create a download ZIP.");
        }

        var itemIds = requestedItemIds.Count > 0
            ? requestedItemIds
            : GetPayloadItemIds(payload);
        return new JsonObject
        {
            ["archivePath"] = archivePath,
            ["itemIds"] = NewStringArray(itemIds),
        };
    }

    private static JsonObject ConvertThreadProductJobStatus(
        JsonObject? payload,
        string fallbackProductId,
        string fallbackProductName)
    {
        var progress = GetObject(payload, "progress");
        var result = GetObject(payload, "result");
        var warnings = new JsonArray();
        foreach (var warning in GetArray(payload, "warnings"))
        {
            warnings.Add(warning?.DeepClone());
        }
        return new JsonObject
        {
            ["productId"] = GetString(payload, "productId", fallbackProductId),
            ["productName"] = GetString(payload, "productName", fallbackProductName),
            ["type"] = GetString(payload, "type", "refresh"),
            ["jobId"] = GetString(payload, "jobId", string.Empty),
            ["state"] = GetString(payload, "state", string.Empty),
            ["phase"] = GetString(payload, "phase", string.Empty),
            ["stage"] = GetString(payload, "stage", string.Empty),
            ["message"] = GetString(payload, "message", string.Empty),
            ["progress"] = new JsonObject
            {
                ["percent"] = GetDouble(progress, "percent", 0.0),
                ["current"] = GetIntAny(progress, ["current"], 0),
                ["total"] = GetIntAny(progress, ["total"], 0),
                ["unit"] = GetString(progress, "unit", "items"),
                ["currentItem"] = GetString(progress, "currentItem", string.Empty),
            },
            ["startedAt"] = GetString(payload, "startedAt", string.Empty),
            ["updatedAt"] = GetString(payload, "updatedAt", string.Empty),
            ["completedAt"] = GetString(payload, "completedAt", string.Empty),
            ["error"] = GetString(payload, "error", string.Empty),
            ["warnings"] = warnings,
            ["result"] = result?.DeepClone(),
        };
    }

    private static bool IsThreadProductJobActive(JsonObject? status)
    {
        var state = GetString(status, "state", string.Empty);
        return state.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || state.Equals("running", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientThreadProductJobPollException(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (ex is OperationCanceledException or TimeoutException)
        {
            return true;
        }

        return ex is InvalidOperationException invalid
            && invalid.Message.Contains("health check timed out", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject WithPollingWarning(JsonObject status, string message, int pollFailures)
    {
        var clone = status.DeepClone().AsObject();
        clone["message"] = message;
        var warnings = new JsonArray();
        foreach (var warning in GetArray(clone, "warnings"))
        {
            warnings.Add(warning?.DeepClone());
        }
        warnings.Add($"{message} Consecutive failures: {pollFailures}.");
        clone["warnings"] = warnings;
        return clone;
    }

    private static JsonObject ConvertThreadRemoveResult(JsonObject? payload, IReadOnlyCollection<string> requestedItemIds)
    {
        if (requestedItemIds.Count == 0)
        {
            throw new InvalidOperationException("No items were selected.");
        }

        return new JsonObject
        {
            ["itemIds"] = NewStringArray(requestedItemIds),
            ["deletedCount"] = GetIntAny(payload, ["deletedCount", "deleted_count", "removedCount", "removed_count"], 0),
            ["missingItemIds"] = NewStringArray(GetStringArrayAny(payload, ["missingItemIds", "missing_item_ids"])),
        };
    }

    private static IReadOnlyList<string> GetPayloadItemIds(JsonObject? payload)
    {
        var values = GetStringArrayAny(payload, ["itemIds", "item_ids"]);
        if (values.Count > 0)
        {
            return values;
        }

        var itemIds = new List<string>();
        foreach (var item in GetArray(payload, "items").OfType<JsonObject>())
        {
            var itemId = GetStringAny(
                item,
                ["item_id", "itemId", "thread_id", "threadId", "conversation_id", "conversationId", "id"],
                string.Empty);
            if (!string.IsNullOrEmpty(itemId) && !itemIds.Contains(itemId, StringComparer.OrdinalIgnoreCase))
            {
                itemIds.Add(itemId);
            }
        }

        return itemIds;
    }

    private JsonObject ReadWindowsCodexSettings(string productPath)
    {
        var settingsPath = GetSettingsFilePath(productPath);
        var payload = ReadJsonFile(settingsPath);
        var fixedSources = new[] { "/input/codex-home", "/input/codex-backup" };
        var defaultOutputRoot = GetManagedProductDataDirectory("windows-codex");
        if (payload is null)
        {
            return new JsonObject
            {
                ["settings_path"] = settingsPath,
                ["source_roots"] = NewStringArray(fixedSources),
                ["effective_source_roots"] = NewStringArray(fixedSources),
                ["outputRoot"] = defaultOutputRoot,
                ["outputs_root"] = defaultOutputRoot,
                ["redaction_profile"] = string.Empty,
                ["include_archived_sources"] = null,
                ["include_tool_outputs"] = null,
                ["include_compaction_recovery"] = null,
                ["using_default_source_roots"] = true,
            };
        }

        var outputRoot = GetStringAny(payload, ["outputRoot", "outputs_root"], defaultOutputRoot);
        return new JsonObject
        {
            ["settings_path"] = settingsPath,
            ["source_roots"] = NewStringArray(fixedSources),
            ["effective_source_roots"] = NewStringArray(fixedSources),
            ["outputRoot"] = outputRoot,
            ["outputs_root"] = outputRoot,
            ["redaction_profile"] = GetString(payload, "redaction_profile", string.Empty),
            ["include_archived_sources"] = CloneValueOrNull(GetNode(payload, "include_archived_sources")),
            ["include_tool_outputs"] = CloneValueOrNull(GetNode(payload, "include_tool_outputs")),
            ["include_compaction_recovery"] = CloneValueOrNull(GetNode(payload, "include_compaction_recovery")),
            ["using_default_source_roots"] = true,
        };
    }

    private JsonObject ConvertWindowsCodexSettings(JsonObject settings)
    {
        var sources = new JsonArray();
        foreach (var sourcePath in GetStringArray(settings, "source_roots"))
        {
            sources.Add(ConvertWindowsCodexSourceRoot(sourcePath));
        }

        var outputRoot = GetStringAny(settings, ["outputRoot", "outputs_root"], string.Empty);
        var outputLocalPath = ConvertWindowsPath(outputRoot);
        return new JsonObject
        {
            ["settingsPath"] = GetString(settings, "settings_path", string.Empty),
            ["sourceRoots"] = sources,
            ["outputsRoot"] = outputRoot,
            ["outputsRootDisplayPath"] = !string.IsNullOrEmpty(outputLocalPath) ? outputLocalPath : outputRoot,
            ["outputsRootReady"] = PathExists(outputLocalPath),
            ["redactionProfile"] = GetString(settings, "redaction_profile", string.Empty),
            ["includeArchivedSources"] = CloneValueOrNull(GetNode(settings, "include_archived_sources")),
            ["includeToolOutputs"] = CloneValueOrNull(GetNode(settings, "include_tool_outputs")),
            ["usingDefaultSourceRoots"] = GetBool(settings, "using_default_source_roots", true),
            ["issues"] = new JsonArray(),
        };
    }

    private JsonObject ConvertWindowsCodexSourceRoot(string sourcePath)
    {
        var displayPath = ConvertWindowsPath(sourcePath);
        return new JsonObject
        {
            ["path"] = sourcePath,
            ["displayPath"] = displayPath,
            ["kind"] = string.Empty,
            ["exists"] = PathExists(displayPath),
            ["readable"] = PathExists(displayPath),
        };
    }

    private JsonObject ReadChatGptSettings(string productPath)
    {
        var settingsPath = GetSettingsFilePath(productPath);
        var payload = ReadJsonFile(settingsPath);
        var defaultOutput = GetManagedProductDataDirectory("chatgpt");
        if (payload is null)
        {
            var defaultRoot = NewDirectoryRootPayload("output", "Output", defaultOutput);
            return new JsonObject
            {
                ["path"] = settingsPath,
                ["settingsFound"] = false,
                ["inputRoots"] = new JsonArray(),
                ["masterRoot"] = defaultRoot.DeepClone(),
                ["outputRoot"] = defaultRoot,
                ["stateRoot"] = NewDirectoryRootPayload("runtime", "Runtime", string.Empty),
                ["allowedExtensions"] = NewStringArray([".zip"]),
                ["recursive"] = false,
                ["profile"] = string.Empty,
            };
        }

        var outputPath = GetStringAny(payload, ["outputRoot", "masterRoot"], defaultOutput);
        if (TryGetNode(payload, "outputRoot", out var outputNode) && outputNode is JsonObject outputRoot)
        {
            outputPath = GetString(outputRoot, "path", outputPath);
        }

        var root = NewDirectoryRootPayload("output", "Output", outputPath);
        return new JsonObject
        {
            ["path"] = settingsPath,
            ["settingsFound"] = true,
            ["inputRoots"] = new JsonArray(),
            ["masterRoot"] = root.DeepClone(),
            ["outputRoot"] = root,
            ["stateRoot"] = NewDirectoryRootPayload("runtime", "Runtime", string.Empty),
            ["allowedExtensions"] = NewStringArray([".zip"]),
            ["recursive"] = false,
            ["profile"] = string.Empty,
        };
    }

    private JsonObject ConvertChatGptDirectoryRoot(
        JsonObject root,
        string fallbackId,
        string fallbackDisplayName,
        string productPath)
    {
        var path = GetString(root, "path", string.Empty);
        var localPath = ConvertChatGptLocalPath(path, productPath);
        return new JsonObject
        {
            ["id"] = GetString(root, "id", fallbackId),
            ["displayName"] = GetString(root, "displayName", fallbackDisplayName),
            ["path"] = path,
            ["displayPath"] = localPath,
            ["exists"] = PathExists(localPath),
        };
    }

    private static JsonObject NewDirectoryRootPayload(string id, string displayName, string path)
        => new()
        {
            ["id"] = id,
            ["displayName"] = displayName,
            ["path"] = path,
        };

    private static JsonObject NewWindowsCodexCurrent(int threadCount)
        => new()
        {
            ["available"] = true,
            ["state"] = "available",
            ["runId"] = string.Empty,
            ["updatedAt"] = string.Empty,
            ["runDirectory"] = string.Empty,
            ["archivePath"] = string.Empty,
            ["archiveExists"] = false,
            ["archiveSizeBytes"] = 0,
            ["catalogPath"] = string.Empty,
            ["processingMode"] = string.Empty,
            ["threadCount"] = threadCount,
            ["eventCount"] = 0,
            ["reusedThreadCount"] = 0,
            ["renderedThreadCount"] = 0,
            ["fidelityWarningCount"] = 0,
            ["updateCounts"] = new JsonObject
            {
                ["new"] = 0,
                ["changed"] = 0,
                ["unchanged"] = 0,
                ["missing"] = 0,
                ["degraded"] = 0,
            },
            ["message"] = string.Empty,
        };

    private static JsonObject NewChatGptRefreshSummary()
        => new()
        {
            ["available"] = false,
            ["startedAt"] = string.Empty,
            ["completedAt"] = string.Empty,
            ["reportPath"] = string.Empty,
            ["discovered"] = 0,
            ["processed"] = 0,
            ["skipped"] = 0,
            ["failed"] = 0,
            ["missing"] = 0,
            ["duplicates"] = 0,
            ["durationSeconds"] = 0,
        };

    private static JsonObject ConvertWindowsCodexCurrent(JsonObject? payload)
    {
        var updateCounts = GetObject(payload, "update_counts") ?? new JsonObject();
        return new JsonObject
        {
            ["available"] = true,
            ["state"] = GetString(payload, "state", string.Empty),
            ["runId"] = GetStringAny(payload, ["refresh_id", "run_id"], string.Empty),
            ["updatedAt"] = GetString(payload, "completed_at", string.Empty),
            ["runDirectory"] = GetString(payload, "run_directory", string.Empty),
            ["archivePath"] = string.Empty,
            ["archiveExists"] = false,
            ["archiveSizeBytes"] = 0,
            ["catalogPath"] = string.Empty,
            ["processingMode"] = GetString(payload, "processing_mode", string.Empty),
            ["threadCount"] = GetIntAny(payload, ["thread_count", "threadCount"], 0),
            ["eventCount"] = GetIntAny(payload, ["message_count", "messageCount"], 0),
            ["reusedThreadCount"] = GetIntAny(payload, ["reused_thread_count", "reusedThreadCount"], 0),
            ["renderedThreadCount"] = GetIntAny(payload, ["rendered_thread_count", "renderedThreadCount"], 0),
            ["fidelityWarningCount"] = GetIntAny(payload, ["fidelity_warning_count", "fidelityWarningCount"], 0),
            ["updateCounts"] = new JsonObject
            {
                ["new"] = GetIntAny(updateCounts, ["new"], 0),
                ["changed"] = GetIntAny(updateCounts, ["changed"], 0),
                ["unchanged"] = GetIntAny(updateCounts, ["unchanged"], 0),
                ["missing"] = GetIntAny(updateCounts, ["missing"], 0),
                ["degraded"] = GetIntAny(updateCounts, ["degraded"], 0),
            },
            ["message"] = string.Empty,
        };
    }

    private static JsonObject ConvertChatGptRefreshSummary(JsonObject? payload)
    {
        var current = GetObject(payload, "current") ?? new JsonObject();
        var manifest = GetObject(payload, "manifest") ?? new JsonObject();
        return new JsonObject
        {
            ["available"] = true,
            ["startedAt"] = GetString(current, "started_at", string.Empty),
            ["completedAt"] = GetString(current, "completed_at", string.Empty),
            ["reportPath"] = GetString(current, "download_zip_path", string.Empty),
            ["discovered"] = GetIntAny(manifest, ["item_count", "itemCount"], 0),
            ["processed"] = GetIntAny(current, ["item_count", "itemCount"], 0),
            ["skipped"] = 0,
            ["failed"] = 0,
            ["missing"] = 0,
            ["duplicates"] = 0,
            ["durationSeconds"] = 0,
        };
    }

    private string GetProductPath(string productId)
    {
        foreach (var product in _settings.ReadSettings().ProductRegistry.Products)
        {
            if (product.Id.Equals(productId, StringComparison.OrdinalIgnoreCase))
            {
                return product.Path;
            }
        }

        return string.Empty;
    }

    private string GetManagedProductDataDirectory(string productId)
    {
        return Path.Combine(_settings.GetDataRootDirectory(), "to_text", productId);
    }

    private static string GetSettingsFilePath(string productPath)
    {
        var settingsPath = Path.Combine(productPath, "settings.json");
        return File.Exists(settingsPath)
            ? settingsPath
            : Path.Combine(productPath, "settings.example.json");
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

    private string ConvertWindowsPath(string path)
    {
        var converted = TimelinePathConverter.ConvertTimelineWindowsPath(path, _options);
        return string.IsNullOrEmpty(converted) ? path : converted;
    }

    private static string ConvertChatGptLocalPath(string path, string productPath)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Equals("/workspace", StringComparison.OrdinalIgnoreCase))
        {
            return productPath;
        }
        if (text.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(productPath, text["/workspace/".Length..].Replace("/", "\\"));
        }

        return Path.IsPathRooted(text) ? text : Path.Combine(productPath, text);
    }

    private static int CountThreadDirectories(string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            return 0;
        }

        var itemsRoot = Path.Combine(rootPath, "items");
        if (Directory.Exists(itemsRoot))
        {
            return SafeEnumerateDirectories(itemsRoot).Count();
        }

        return SafeEnumerateDirectories(rootPath).Count(path =>
            File.Exists(Path.Combine(path, "timeline.json"))
            || File.Exists(Path.Combine(path, "convert_info.json")));
    }

    private string GetWindowsCodexOutputLocalPath()
    {
        var productPath = GetProductPath("windows-codex");
        var settings = ConvertWindowsCodexSettings(ReadWindowsCodexSettings(productPath));
        return ConvertWindowsPath(GetString(settings, "outputsRoot", string.Empty));
    }

    private string GetChatGptOutputLocalPath()
    {
        var productPath = GetProductPath("chatgpt");
        var settings = ReadChatGptSettings(productPath);
        var outputRoot = ConvertChatGptDirectoryRoot(
            GetObject(settings, "outputRoot") ?? new JsonObject(),
            "output",
            "Output",
            productPath);
        return GetString(outputRoot, "displayPath", string.Empty);
    }

    private string ResolveManagedDownloadDirectory(string productId, string requestedPath)
    {
        var downloadRoot = Path.Combine(_settings.GetWorkDirectory(), "downloads");
        Directory.CreateDirectory(downloadRoot);
        var candidate = ConvertTimelineText(requestedPath);
        string localPath;
        if (!string.IsNullOrEmpty(candidate))
        {
            localPath = ConvertWindowsPath(candidate);
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

    private JsonObject ConvertThreadItemRow(JsonObject item, string rootPath)
    {
        var itemId = GetStringAny(
            item,
            ["item_id", "itemId", "thread_id", "threadId", "conversation_id", "conversationId", "id"],
            string.Empty);
        var title = GetStringAny(item, ["title", "preferred_title", "preferredTitle", "name"], string.Empty);
        if (string.IsNullOrEmpty(title))
        {
            title = GetString(item, "first_user_message_excerpt", string.Empty);
        }
        if (string.IsNullOrEmpty(title))
        {
            title = itemId;
        }

        var directoryPath = GetStringAny(item, ["directoryPath", "directory_path", "item_dir", "itemDir"], string.Empty);
        var timelinePath = ResolveThreadArtifactPath(GetStringAny(item, ["timeline_path", "timelinePath"], string.Empty), rootPath);
        var convertInfoPath = ResolveThreadArtifactPath(GetStringAny(item, ["convert_info_path", "convertInfoPath"], string.Empty), rootPath);
        if (string.IsNullOrEmpty(directoryPath) && !string.IsNullOrEmpty(timelinePath))
        {
            directoryPath = GetDirectoryName(timelinePath);
        }
        if (string.IsNullOrEmpty(directoryPath) && !string.IsNullOrEmpty(rootPath) && !string.IsNullOrEmpty(itemId))
        {
            directoryPath = Path.Combine(rootPath, itemId);
        }
        if (string.IsNullOrEmpty(timelinePath) && !string.IsNullOrEmpty(directoryPath))
        {
            timelinePath = Path.Combine(directoryPath, "timeline.json");
        }
        if (string.IsNullOrEmpty(convertInfoPath) && !string.IsNullOrEmpty(directoryPath))
        {
            convertInfoPath = Path.Combine(directoryPath, "convert_info.json");
        }

        var createdAt = GetStringAny(item, ["created_at", "createdAt", "started_at_utc", "startedAtUtc"], string.Empty);
        var updatedAt = GetStringAny(item, ["updated_at", "updatedAt", "ended_at_utc", "endedAtUtc"], string.Empty);
        var messageCount = GetIntAny(item, ["message_count", "messageCount", "event_count", "eventCount"], 0);

        return new JsonObject
        {
            ["itemId"] = itemId,
            ["title"] = title,
            ["createdAt"] = createdAt,
            ["updatedAt"] = updatedAt,
            ["messageCount"] = messageCount,
            ["directoryPath"] = directoryPath,
            ["timelinePath"] = timelinePath,
            ["convertInfoPath"] = convertInfoPath,
        };
    }

    private string ResolveThreadArtifactPath(string value, string rootPath)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var localPath = ConvertWindowsPath(text);
        if (Path.IsPathRooted(localPath))
        {
            return localPath;
        }

        return !string.IsNullOrEmpty(rootPath)
            ? Path.Combine(rootPath, localPath.Replace("/", "\\"))
            : localPath;
    }

    private static JsonObject ConvertThreadMessage(JsonObject message, int index)
        => new()
        {
            ["index"] = index,
            ["role"] = GetString(message, "role", string.Empty),
            ["createdAt"] = GetString(message, "created_at", string.Empty),
            ["text"] = GetString(message, "text", string.Empty),
        };

    private static JsonObject NewThreadListResult(JsonArray threads, JsonObject pagination, int total)
        => new()
        {
            ["total"] = total,
            ["pagination"] = pagination,
            ["threads"] = threads,
        };

    private static JsonObject NewUnavailableThreadDetail(
        string itemId,
        string directoryPath,
        string timelinePath,
        string convertInfoPath,
        string message,
        string title = "")
        => new()
        {
            ["available"] = false,
            ["itemId"] = itemId,
            ["title"] = title,
            ["createdAt"] = string.Empty,
            ["updatedAt"] = string.Empty,
            ["messageCount"] = 0,
            ["messages"] = new JsonArray(),
            ["directoryPath"] = directoryPath,
            ["timelinePath"] = timelinePath,
            ["convertInfoPath"] = convertInfoPath,
            ["message"] = message,
        };

    private static JsonObject NewPagination(
        int page,
        int pageSize,
        int totalItems,
        int returnedItems)
    {
        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Max(1, pageSize);
        var totalPages = totalItems > 0
            ? (int)Math.Ceiling(totalItems / (double)effectivePageSize)
            : 0;
        var offset = (effectivePage - 1) * effectivePageSize;
        return new JsonObject
        {
            ["mode"] = "page",
            ["page"] = effectivePage,
            ["pageSize"] = effectivePageSize,
            ["totalItems"] = totalItems,
            ["totalPages"] = totalPages,
            ["returnedItems"] = returnedItems,
            ["offset"] = offset,
            ["rangeStart"] = returnedItems > 0 ? offset + 1 : 0,
            ["rangeEnd"] = returnedItems > 0 ? offset + returnedItems : 0,
            ["hasPrevious"] = effectivePage > 1 && totalItems > 0,
            ["hasNext"] = effectivePage < totalPages,
        };
    }

    private static string GetSafeChildDirectory(string rootPath, string childName)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var safeRootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(Path.Combine(fullRoot, childName.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullCandidate.StartsWith(safeRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid thread id.");
        }

        return fullCandidate;
    }

    private static string GetDirectoryName(string path)
        => Path.GetDirectoryName(path) ?? string.Empty;

    private static IEnumerable<string> SafeEnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        try
        {
            return Directory.EnumerateFiles(path, searchPattern, searchOption).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static bool PathExists(string path)
        => !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

    private static bool IsPathUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? CloneValueOrNull(JsonNode? node)
        => node?.DeepClone();

    private static JsonObject? GetObject(JsonObject? source, string name)
        => GetNode(source, name) as JsonObject;

    private static JsonNode? GetNode(JsonObject? source, string name)
        => TryGetNode(source, name, out var node) ? node : null;

    private static bool TryGetNode(JsonObject? source, string name, out JsonNode? node)
    {
        node = null;
        if (source is null)
        {
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

        return false;
    }

    private static JsonArray NewStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static IEnumerable<string> GetStringArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array
            ? array.Select(ConvertTimelineText).Where(value => !string.IsNullOrEmpty(value))
            : [];
    }

    private static IReadOnlyList<string> GetStringArrayAny(JsonObject? source, string[] names)
    {
        foreach (var name in names)
        {
            var values = GetStringArray(source, name).ToList();
            if (values.Count > 0)
            {
                return values;
            }
        }

        return [];
    }

    private static List<string> GetRequestItemIds(JsonObject? request)
    {
        var itemIds = new List<string>();
        foreach (var itemId in GetArray(request, "itemIds"))
        {
            var text = ConvertTimelineText(itemId);
            if (!string.IsNullOrEmpty(text) && !itemIds.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                itemIds.Add(text);
            }
        }

        return itemIds;
    }

    private static List<JsonNode?> GetArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array ? array.ToList() : [];
    }

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertTimelineText(node);
    }

    private static string GetStringAny(JsonObject? source, string[] names, string fallback)
    {
        foreach (var name in names)
        {
            if (TryGetNode(source, name, out var node))
            {
                return ConvertTimelineText(node);
            }
        }

        return fallback;
    }

    private static int GetIntAny(JsonObject? source, string[] names, int fallback)
    {
        foreach (var name in names)
        {
            if (TryGetNode(source, name, out var node))
            {
                return ConvertTimelineInt(node, fallback);
            }
        }

        return fallback;
    }

    private static int ConvertTimelineInt(JsonNode? node, int fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<int>();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }

        return int.TryParse(ConvertTimelineText(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static double GetDouble(JsonObject? source, string name, double fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<double>();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }

        return double.TryParse(ConvertTimelineText(node), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
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

        return ConvertTimelineText(node).ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static string ConvertTimelineText(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonNode node)
        {
            try
            {
                return node.GetValue<object>()?.ToString()?.Trim() ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }

        return value.ToString()?.Trim() ?? string.Empty;
    }

}
