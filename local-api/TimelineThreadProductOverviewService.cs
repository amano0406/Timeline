using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class TimelineThreadProductOverviewService
{
    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineLocalApiOptions _options;
    private readonly TimelineProductCliService _cli;

    public TimelineThreadProductOverviewService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        TimelineLocalApiOptions options,
        TimelineProductCliService cli)
    {
        _settings = settings;
        _operations = operations;
        _options = options;
        _cli = cli;
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

    public JsonObject GetWindowsCodexThreads(int page, int pageSize)
    {
        return InvokeWebOperation(
            "TimelineForWindowsCodex",
            "windows_codex_items_list",
            () => GetThreadRowsPageFromRoot(GetWindowsCodexOutputLocalPath(), page, pageSize));
    }

    public JsonObject GetWindowsCodexThreadDetail(string itemId)
    {
        var rootPath = GetWindowsCodexOutputLocalPath();
        return GetThreadDetailFromRoot(rootPath, itemId, ["thread_id", "conversation_id", "item_id", "id"]);
    }

    public Task<JsonObject> RefreshWindowsCodexAsync(CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "TimelineForWindowsCodex",
            "windows_codex_refresh",
            async operationId =>
            {
                var payload = await _cli.InvokeJsonAsync(
                    "windows-codex",
                    "TimelineForWindowsCodex",
                    ["items", "refresh", "--format", "json"],
                    900,
                    operationId,
                    cancellationToken);
                return ConvertWindowsCodexCurrent(payload as JsonObject);
            });
    }

    public JsonObject DownloadWindowsCodexItems(JsonObject? request)
    {
        return InvokeWebOperation(
            "TimelineForWindowsCodex",
            "windows_codex_items_download",
            () =>
            {
                var itemIds = GetRequestItemIds(request);
                var archivePath = CreateThreadItemsArchive(
                    productId: "windows-codex",
                    rootPath: GetWindowsCodexOutputLocalPath(),
                    itemIds: itemIds,
                    requestedOutputPath: GetString(request, "outputPath", string.Empty));
                return new JsonObject
                {
                    ["archivePath"] = archivePath,
                    ["itemIds"] = NewStringArray(itemIds),
                };
            });
    }

    public JsonObject DeleteWindowsCodexItems(JsonObject? request)
    {
        return InvokeWebOperation(
            "TimelineForWindowsCodex",
            "windows_codex_items_delete_generated",
            () => RemoveThreadItems(GetWindowsCodexOutputLocalPath(), GetRequestItemIds(request)));
    }

    public JsonObject GetChatGptThreads(int page, int pageSize)
    {
        return InvokeWebOperation(
            "TimelineForChatGPT",
            "chatgpt_items_list",
            () => GetThreadRowsPageFromRoot(GetChatGptOutputLocalPath(), page, pageSize));
    }

    public JsonObject GetChatGptThreadDetail(string itemId)
    {
        var rootPath = GetChatGptOutputLocalPath();
        return GetThreadDetailFromRoot(rootPath, itemId, ["conversation_id", "thread_id", "item_id", "id"]);
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

                var args = new List<string>
                {
                    "items",
                    "refresh",
                    "--file",
                    filePath,
                    "--json",
                };
                var downloadTo = GetString(request, "downloadTo", string.Empty);
                if (!string.IsNullOrEmpty(downloadTo))
                {
                    args.Add("--download-to");
                    args.Add(downloadTo);
                }
                if (GetBool(request, "overwrite", false))
                {
                    args.Add("--overwrite");
                }

                var payload = await _cli.InvokeJsonAsync(
                    "chatgpt",
                    "TimelineForChatGPT",
                    args,
                    1800,
                    operationId,
                    cancellationToken);
                return ConvertChatGptRefreshSummary(payload as JsonObject);
            });
    }

    public JsonObject DownloadChatGptItems(JsonObject? request)
    {
        return InvokeWebOperation(
            "TimelineForChatGPT",
            "chatgpt_items_download",
            () =>
            {
                var itemIds = GetRequestItemIds(request);
                if (itemIds.Count > 0)
                {
                    throw new InvalidOperationException("TimelineForChatGPT CLI does not support selected item download yet.");
                }

                var archivePath = CreateThreadItemsArchive(
                    productId: "chatgpt",
                    rootPath: GetChatGptOutputLocalPath(),
                    itemIds: itemIds,
                    requestedOutputPath: GetString(request, "outputPath", string.Empty));
                return new JsonObject
                {
                    ["archivePath"] = archivePath,
                    ["itemIds"] = new JsonArray(),
                };
            });
    }

    public JsonObject DeleteChatGptItems(JsonObject? request)
    {
        return InvokeWebOperation(
            "TimelineForChatGPT",
            "chatgpt_items_delete_generated",
            () => throw new InvalidOperationException("TimelineForChatGPT does not support generated item removal in the current product CLI contract."));
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

    private JsonObject GetThreadRowsPageFromRoot(string rootPath, int page, int pageSize)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            return NewThreadListResult([], NewPagination(Math.Max(1, page), Math.Max(1, pageSize), 0, 0), 0);
        }

        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Max(1, pageSize);
        var offset = (effectivePage - 1) * effectivePageSize;
        var manifest = ReadJsonFile(Path.Combine(rootPath, "manifest.json"));
        var manifestItems = GetArray(manifest, "items").OfType<JsonObject>().ToList();
        if (manifestItems.Count > 0)
        {
            var sortedItems = manifestItems
                .OrderByDescending(GetThreadSortDateFromItem)
                .ToList();
            var total = GetIntAny(
                manifest,
                ["item_count", "itemCount", "total_items", "totalItems", "total"],
                sortedItems.Count);
            if (total <= 0)
            {
                total = sortedItems.Count;
            }

            var threads = new JsonArray();
            foreach (var item in sortedItems.Skip(offset).Take(effectivePageSize))
            {
                threads.Add(ConvertThreadItemRow(item, rootPath));
            }

            return NewThreadListResult(threads, NewPagination(effectivePage, effectivePageSize, total, threads.Count), total);
        }

        var candidates = SafeEnumerateFiles(rootPath, "timeline.json", SearchOption.AllDirectories)
            .Select(path => new ThreadFileCandidate(path, GetDirectoryName(path), SafeGetLastWriteTimeUtc(path)))
            .OrderByDescending(candidate => candidate.SortDate)
            .ToList();
        var totalItems = candidates.Count;
        var pageCandidates = candidates.Skip(offset).Take(effectivePageSize).ToList();
        var pageRows = new JsonArray();
        foreach (var candidate in pageCandidates)
        {
            var timeline = ReadJsonFile(candidate.TimelinePath);
            if (timeline is null)
            {
                continue;
            }

            var messages = GetArray(timeline, "messages");
            var itemId = GetStringAny(
                timeline,
                ["thread_id", "conversation_id", "item_id", "id"],
                Path.GetFileName(candidate.DirectoryPath));
            var title = GetString(timeline, "title", string.Empty);
            if (string.IsNullOrEmpty(title))
            {
                title = itemId;
            }

            pageRows.Add(new JsonObject
            {
                ["itemId"] = itemId,
                ["title"] = title,
                ["createdAt"] = GetString(timeline, "created_at", string.Empty),
                ["updatedAt"] = GetString(timeline, "updated_at", string.Empty),
                ["messageCount"] = messages.Count,
                ["directoryPath"] = candidate.DirectoryPath,
                ["timelinePath"] = candidate.TimelinePath,
                ["convertInfoPath"] = Path.Combine(candidate.DirectoryPath, "convert_info.json"),
            });
        }

        return NewThreadListResult(pageRows, NewPagination(effectivePage, effectivePageSize, totalItems, pageRows.Count), totalItems);
    }

    private JsonObject RemoveThreadItems(string rootPath, IReadOnlyCollection<string> itemIds)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            throw new InvalidOperationException("Output directory is not configured.");
        }
        if (itemIds.Count == 0)
        {
            throw new InvalidOperationException("No items were selected.");
        }

        var deleted = 0;
        var missing = new JsonArray();
        foreach (var itemId in itemIds)
        {
            var itemRoot = GetSafeChildDirectory(rootPath, itemId);
            if (Directory.Exists(itemRoot))
            {
                Directory.Delete(itemRoot, recursive: true);
                deleted++;
            }
            else
            {
                missing.Add(itemId);
            }
        }

        return new JsonObject
        {
            ["itemIds"] = NewStringArray(itemIds),
            ["deletedCount"] = deleted,
            ["missingItemIds"] = missing,
        };
    }

    private string CreateThreadItemsArchive(
        string productId,
        string rootPath,
        IReadOnlyCollection<string> itemIds,
        string requestedOutputPath)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            throw new InvalidOperationException("Output directory is not configured.");
        }

        var sourceDirectories = itemIds.Count > 0
            ? ResolveSelectedThreadDirectories(rootPath, itemIds)
            : GetThreadDirectories(rootPath);
        if (sourceDirectories.Count == 0)
        {
            throw new InvalidOperationException("No generated items were found.");
        }

        var destination = ResolveManagedDownloadDirectory(productId, requestedOutputPath);
        var archiveName = productId + "-items-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip";
        var archivePath = Path.Combine(destination, archiveName);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifestPath = Path.Combine(rootPath, "manifest.json");
            if (itemIds.Count == 0 && File.Exists(manifestPath))
            {
                AddFileToArchive(archive, manifestPath, "manifest.json");
            }

            foreach (var directory in sourceDirectories)
            {
                var itemSegment = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                foreach (var file in SafeEnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(directory, file);
                    AddFileToArchive(archive, file, Path.Combine(itemSegment, relativePath));
                }
            }
        }

        return archivePath;
    }

    private List<string> ResolveSelectedThreadDirectories(string rootPath, IReadOnlyCollection<string> itemIds)
    {
        var directories = new List<string>();
        foreach (var itemId in itemIds)
        {
            var directory = GetSafeChildDirectory(rootPath, itemId);
            if (!Directory.Exists(directory))
            {
                throw new InvalidOperationException("Thread was not found.");
            }

            directories.Add(directory);
        }

        return directories;
    }

    private static List<string> GetThreadDirectories(string rootPath)
    {
        var itemsRoot = Path.Combine(rootPath, "items");
        var candidateRoot = Directory.Exists(itemsRoot) ? itemsRoot : rootPath;
        return SafeEnumerateDirectories(candidateRoot)
            .Where(path => File.Exists(Path.Combine(path, "timeline.json")) || File.Exists(Path.Combine(path, "convert_info.json")))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private static void AddFileToArchive(ZipArchive archive, string filePath, string entryName)
    {
        var normalizedEntry = entryName.Replace('\\', '/');
        archive.CreateEntryFromFile(filePath, normalizedEntry, CompressionLevel.Fastest);
    }

    private JsonObject GetThreadDetailFromRoot(string rootPath, string itemId, string[] itemIdNames)
    {
        var safeItemId = ConvertTimelineText(itemId);
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            return NewUnavailableThreadDetail(
                safeItemId,
                string.Empty,
                string.Empty,
                string.Empty,
                "Output directory is not configured.");
        }

        var threadDirectory = GetSafeChildDirectory(rootPath, safeItemId);
        var timelinePath = Path.Combine(threadDirectory, "timeline.json");
        var convertInfoPath = Path.Combine(threadDirectory, "convert_info.json");
        if (!File.Exists(timelinePath))
        {
            return NewUnavailableThreadDetail(
                safeItemId,
                threadDirectory,
                timelinePath,
                convertInfoPath,
                "Thread was not found.");
        }

        var timeline = ReadJsonFile(timelinePath);
        if (timeline is null)
        {
            return NewUnavailableThreadDetail(
                safeItemId,
                threadDirectory,
                timelinePath,
                convertInfoPath,
                "Thread could not be read.",
                title: safeItemId);
        }

        var messages = new JsonArray();
        var index = 0;
        foreach (var messageNode in GetArray(timeline, "messages"))
        {
            if (messageNode is JsonObject message)
            {
                messages.Add(ConvertThreadMessage(message, index));
            }

            index++;
        }

        var resolvedItemId = GetStringAny(timeline, itemIdNames, safeItemId);
        var titleText = GetString(timeline, "title", string.Empty);
        if (string.IsNullOrEmpty(titleText))
        {
            titleText = resolvedItemId;
        }

        return new JsonObject
        {
            ["available"] = true,
            ["itemId"] = resolvedItemId,
            ["title"] = titleText,
            ["createdAt"] = GetString(timeline, "created_at", string.Empty),
            ["updatedAt"] = GetString(timeline, "updated_at", string.Empty),
            ["messageCount"] = messages.Count,
            ["messages"] = messages,
            ["directoryPath"] = threadDirectory,
            ["timelinePath"] = timelinePath,
            ["convertInfoPath"] = convertInfoPath,
            ["message"] = string.Empty,
        };
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
        var timeline = ReadJsonFile(timelinePath);
        if (timeline is not null)
        {
            var timelineTitle = GetString(timeline, "title", string.Empty);
            if (!string.IsNullOrEmpty(timelineTitle))
            {
                title = timelineTitle;
            }
            if (string.IsNullOrEmpty(createdAt))
            {
                createdAt = GetString(timeline, "created_at", string.Empty);
            }
            if (string.IsNullOrEmpty(updatedAt))
            {
                updatedAt = GetString(timeline, "updated_at", string.Empty);
            }
            if (messageCount <= 0)
            {
                messageCount = GetArray(timeline, "messages").Count;
            }
        }

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

    private static DateTimeOffset GetThreadSortDateFromItem(JsonObject item)
    {
        var dateText = GetStringAny(
            item,
            ["updated_at", "updatedAt", "ended_at_utc", "endedAtUtc", "created_at", "createdAt", "started_at_utc", "startedAtUtc"],
            string.Empty);
        return DateTimeOffset.TryParse(
            dateText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private static DateTimeOffset SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
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

    private sealed record ThreadFileCandidate(string TimelinePath, string DirectoryPath, DateTimeOffset SortDate);
}
