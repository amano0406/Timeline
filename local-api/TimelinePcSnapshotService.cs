using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class TimelinePcSnapshotService
{
    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineProductApiClient _api;

    public TimelinePcSnapshotService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        TimelineProductApiClient api)
    {
        _settings = settings;
        _operations = operations;
        _api = api;
    }

    public Task<JsonObject> GetOverviewAsync(CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "pc_overview",
            async operationId =>
            {
                var productPath = GetProductPath();
                if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
                {
                    return new JsonObject
                    {
                        ["productFound"] = false,
                        ["productPath"] = productPath,
                        ["settingsValid"] = false,
                        ["settings"] = new JsonObject(),
                        ["itemCount"] = 0,
                        ["message"] = "TimelineForPcInfo was not found.",
                    };
                }

                try
                {
                    var settingsPayload = await GetSettingsPayloadAsync(operationId, cancellationToken);
                    var itemsPayload = await GetItemsCoreAsync(1, 1, operationId, cancellationToken);
                    return new JsonObject
                    {
                        ["productFound"] = true,
                        ["productPath"] = productPath,
                        ["settingsValid"] = GetBool(settingsPayload, "ok", true),
                        ["settings"] = ConvertSettings(settingsPayload),
                        ["itemCount"] = GetInt(itemsPayload, "total", 0),
                        ["message"] = string.Empty,
                    };
                }
                catch (Exception ex)
                {
                    return new JsonObject
                    {
                        ["productFound"] = true,
                        ["productPath"] = productPath,
                        ["settingsValid"] = false,
                        ["settings"] = new JsonObject(),
                        ["itemCount"] = 0,
                        ["message"] = ex.Message,
                    };
                }
            });
    }

    public Task<JsonObject> GetItemsAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        return InvokeWebOperationAsync(
            "pc_items_list",
            operationId => GetItemsCoreAsync(page, pageSize, operationId, cancellationToken));
    }

    private async Task<JsonObject> InvokeWebOperationAsync(string action, Func<string, Task<JsonObject>> operation)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForPcInfo",
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
                "TimelineForPcInfo",
                action,
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["message"] = GetString(result, "message", string.Empty),
                    ["total"] = GetInt(result, "total", 0),
                    ["itemCount"] = GetInt(result, "itemCount", 0),
                });
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForPcInfo",
                action,
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private async Task<JsonObject> GetItemsCoreAsync(
        int page,
        int pageSize,
        string operationId,
        CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Max(1, pageSize);
        var payload = await _api.PostJsonAsync(
            "pc",
            "TimelineForPcInfo",
            "/items/list",
            new JsonObject
            {
                ["page"] = effectivePage,
                ["pageSize"] = effectivePageSize,
            },
            120,
            operationId,
            cancellationToken) as JsonObject ?? new JsonObject();

        var items = new JsonArray();
        foreach (var row in GetArray(payload, "items").OfType<JsonObject>())
        {
            items.Add(ConvertItemRow(row));
        }
        var totalItems = GetIntAny(payload, ["total", "item_count", "itemCount"], items.Count);

        return new JsonObject
        {
            ["total"] = totalItems,
            ["pagination"] = NewPagination(effectivePage, effectivePageSize, totalItems, items.Count),
            ["items"] = items,
        };
    }

    private async Task<JsonObject> GetSettingsPayloadAsync(string operationId, CancellationToken cancellationToken)
    {
        return await _api.PostJsonAsync(
            "pc",
            "TimelineForPcInfo",
            "/settings/status",
            new JsonObject(),
            60,
            operationId,
            cancellationToken) as JsonObject ?? new JsonObject();
    }

    private JsonObject? ReadPcSettingsFile()
    {
        var path = GetPcSettingsPath();
        if (!File.Exists(path))
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

    private JsonObject ConvertSettings(JsonObject payload)
    {
        var outputRoot = GetStringAny(payload, ["output_root", "outputRoot"], GetManagedPcDataDirectory());
        var settingsPath = GetStringAny(payload, ["settings_path", "settingsPath"], Path.Combine(GetProductPath(), "settings.json"));
        var runtime = GetObject(payload, "runtime");
        var apiHost = GetStringAny(runtime, ["apiHost", "api_host"], "127.0.0.1");
        var apiPort = GetIntAny(runtime, ["apiPort", "api_port"], 19600);
        var apiBaseUrl = GetStringAny(runtime, ["apiBaseUrl", "api_base_url"], $"http://{apiHost}:{apiPort}");

        return new JsonObject
        {
            ["settingsPath"] = settingsPath,
            ["outputRoot"] = outputRoot,
            ["outputRootDisplayPath"] = ConvertPcLocalPath(outputRoot),
            ["outputRootReady"] = PathExists(ConvertPcLocalPath(outputRoot)),
            ["redactionProfile"] = GetStringAny(payload, ["redaction_profile", "redactionProfile"], string.Empty),
            ["mockProfile"] = GetStringAny(payload, ["mock_profile", "mockProfile"], string.Empty),
            ["apiHost"] = apiHost,
            ["apiPort"] = apiPort,
            ["apiBaseUrl"] = apiBaseUrl,
        };
    }

    private List<JsonObject> DiscoverItems(string outputRoot)
    {
        var itemsRoot = string.IsNullOrEmpty(outputRoot) ? string.Empty : Path.Combine(outputRoot, "items");
        if (string.IsNullOrEmpty(itemsRoot) || !Directory.Exists(itemsRoot))
        {
            return [];
        }

        var rows = new List<JsonObject>();
        IEnumerable<string> directories;
        try
        {
            directories = Directory
                .EnumerateDirectories(itemsRoot)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }

        foreach (var directory in directories)
        {
            var timelinePath = Path.Combine(directory, "timeline.json");
            var convertInfoPath = Path.Combine(directory, "convert_info.json");
            if (!File.Exists(timelinePath) || !File.Exists(convertInfoPath))
            {
                continue;
            }

            var timeline = ReadJsonObject(timelinePath) ?? new JsonObject();
            var convertInfo = ReadJsonObject(convertInfoPath) ?? new JsonObject();
            var events = GetArray(timeline, "events");
            var itemId = GetFirstText(
                GetString(timeline, "item_id", string.Empty),
                GetString(convertInfo, "item_id", string.Empty),
                Path.GetFileName(directory));
            rows.Add(new JsonObject
            {
                ["schema_version"] = 1,
                ["item_id"] = itemId,
                ["itemId"] = itemId,
                ["item_type"] = GetFirstText(
                    GetString(timeline, "item_type", string.Empty),
                    GetString(convertInfo, "item_type", string.Empty),
                    "windows_pc"),
                ["title"] = GetFirstText(GetString(timeline, "title", string.Empty), "Windows PC snapshot history"),
                ["created_at_utc"] = GetFirstText(
                    GetString(timeline, "created_at_utc", string.Empty),
                    GetString(convertInfo, "created_at_utc", string.Empty)),
                ["updated_at_utc"] = GetFirstText(
                    GetString(timeline, "updated_at_utc", string.Empty),
                    GetString(convertInfo, "updated_at_utc", string.Empty)),
                ["event_count"] = events.Count,
                ["latest_update_status"] = GetFirstText(
                    GetString(timeline, "latest_update_status", string.Empty),
                    GetString(convertInfo, "update_status", string.Empty)),
                ["timeline_path"] = timelinePath,
                ["convert_info_path"] = convertInfoPath,
            });
        }

        return rows
            .OrderByDescending(row => GetString(row, "updated_at_utc", string.Empty), StringComparer.Ordinal)
            .ThenByDescending(row => GetString(row, "item_id", string.Empty), StringComparer.Ordinal)
            .ToList();
    }

    private JsonObject ConvertItemRow(JsonObject row)
    {
        return new JsonObject
        {
            ["itemId"] = GetStringAny(row, ["itemId", "item_id", "recordId", "id"], string.Empty),
            ["itemType"] = GetStringAny(row, ["item_type", "itemType", "recordType"], string.Empty),
            ["title"] = GetString(row, "title", string.Empty),
            ["createdAt"] = GetStringAny(row, ["created_at_utc", "createdAtUtc", "created_at", "createdAt"], string.Empty),
            ["updatedAt"] = GetStringAny(row, ["updated_at_utc", "updatedAtUtc", "updated_at", "updatedAt"], string.Empty),
            ["eventCount"] = GetIntAny(row, ["event_count", "eventCount"], 0),
            ["latestUpdateStatus"] = GetStringAny(row, ["latest_update_status", "latestUpdateStatus"], string.Empty),
            ["timelinePath"] = ConvertPcLocalPath(GetStringAny(row, ["timeline_path", "timelinePath"], string.Empty)),
            ["convertInfoPath"] = ConvertPcLocalPath(GetStringAny(row, ["convert_info_path", "convertInfoPath"], string.Empty)),
        };
    }

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

    private string ConvertPcLocalPath(string? path)
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

        return Path.IsPathRooted(text) ? text : Path.Combine(GetProductPath(), text);
    }

    private string GetPcSettingsPath()
    {
        var productPath = GetProductPath();
        var settingsPath = Path.Combine(productPath, "settings.json");
        if (File.Exists(settingsPath))
        {
            return settingsPath;
        }

        return Path.Combine(productPath, "settings.example.json");
    }

    private string GetProductPath()
    {
        foreach (var product in _settings.ReadSettings().ProductRegistry.Products)
        {
            if (product.Id.Equals("pc", StringComparison.OrdinalIgnoreCase))
            {
                return product.Path ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private string GetManagedPcDataDirectory()
    {
        var path = Path.Combine(_settings.GetDataRootDirectory(), "to_text", "pc");
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private static JsonObject? ReadJsonObject(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertNodeToString(node);
    }

    private static string GetStringAny(JsonObject? source, string[] names, string fallback)
    {
        foreach (var name in names)
        {
            if (TryGetNode(source, name, out var node))
            {
                return ConvertNodeToString(node);
            }
        }

        return fallback;
    }

    private static int GetInt(JsonObject? source, string name, int fallback)
    {
        var node = GetNode(source, name);
        return ConvertNodeToInt(node, fallback);
    }

    private static int GetIntAny(JsonObject? source, string[] names, int fallback)
    {
        foreach (var name in names)
        {
            if (TryGetNode(source, name, out var node))
            {
                return ConvertNodeToInt(node, fallback);
            }
        }

        return fallback;
    }

    private static int GetPort(JsonNode? node, int fallback)
    {
        var port = ConvertNodeToInt(node, fallback);
        return port is >= 1 and <= 65535 ? port : fallback;
    }

    private static int ConvertNodeToInt(JsonNode? node, int fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        if (node.GetValueKind() == JsonValueKind.Number)
        {
            try
            {
                return node.GetValue<int>();
            }
            catch (FormatException)
            {
            }
        }

        return int.TryParse(ConvertNodeToString(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool GetBool(JsonObject? source, string name, bool fallback)
    {
        var text = GetString(source, name, string.Empty);
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        return text.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static List<JsonNode?> GetArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array ? array.ToList() : [];
    }

    private static string ConvertNodeToString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        try
        {
            return ConvertTimelineText(node.GetValue<object>());
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
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

    private static string GetFirstText(params string[] values)
    {
        foreach (var value in values)
        {
            var text = ConvertTimelineText(value);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static bool PathExists(string path)
        => !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));
}
