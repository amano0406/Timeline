using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed class TimelineStoreExportService
{
    private readonly TimelineLocalApiOptions _options;
    private readonly TimelineSettingsService _settings;
    private readonly TimelineStoreService _store;
    private readonly TimelineOperationLogService _operations;

    public TimelineStoreExportService(
        TimelineLocalApiOptions options,
        TimelineSettingsService settings,
        TimelineStoreService store,
        TimelineOperationLogService operations)
    {
        _options = options;
        _settings = settings;
        _store = store;
        _operations = operations;
    }

    public TimelineStoreDownloadResponse CreateDownload()
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "Timeline",
            "timeline_export_download",
            "started",
            "Web operation started.");

        try
        {
            var result = CreateDownloadCore(operationId);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "Timeline",
                "timeline_export_download",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: ConvertDownloadResultDetails(result));
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "Timeline",
                "timeline_export_download",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private TimelineStoreDownloadResponse CreateDownloadCore(string operationId)
    {
        var overview = _store.GetOverview();
        if (!overview.Available)
        {
            throw new InvalidOperationException("Timeline store has not been rebuilt yet. Rebuild the Timeline store first.");
        }

        var manifestPath = Path.Combine(_settings.GetStoreDirectory(), "manifest.json");
        var manifest = ReadManifest(manifestPath);
        var packagePath = ResolvePackagePath(GetString(manifest, "packagePath", string.Empty));
        if (string.IsNullOrEmpty(packagePath) || !Directory.Exists(packagePath))
        {
            throw new InvalidOperationException("Timeline store package was not found. Rebuild the Timeline store.");
        }

        var downloadRoot = GetExportDownloadRoot();
        var archivePath = Path.Combine(downloadRoot, $"Timeline-store-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        ZipFile.CreateFromDirectory(packagePath, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
        var archive = new FileInfo(archivePath);
        if (archive.Length <= 0)
        {
            throw new InvalidOperationException("Timeline store ZIP was empty. Rebuild the Timeline store.");
        }

        var result = new TimelineStoreDownloadResponse
        {
            ArchivePath = archivePath,
            ArchiveSizeBytes = archive.Length,
            ItemCount = GetInt(manifest, "itemCount", 0),
            EventCount = GetInt(manifest, "eventCount", 0),
            Products = GetArray(manifest, "products"),
        };

        _operations.WriteOperationEvent(
            operationId,
            "web",
            "Timeline",
            "timeline_export_archive_created",
            "completed",
            "Timeline archive created.",
            details: ConvertDownloadResultDetails(result));

        return result;
    }

    private string ResolvePackagePath(string packagePath)
    {
        var text = ConvertTimelineText(packagePath);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var localPath = TimelinePathConverter.ConvertTimelineWindowsPath(text, _options);
        if (string.IsNullOrEmpty(localPath))
        {
            localPath = text;
        }

        return Path.GetFullPath(Path.IsPathRooted(localPath)
            ? localPath
            : Path.Combine(_options.TimelineProductPath, localPath));
    }

    private string GetExportDownloadRoot()
    {
        var root = Path.Combine(_settings.GetWorkDirectory(), "downloads", "timeline");
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static JsonObject ReadManifest(string manifestPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
                ?? throw new InvalidOperationException("Timeline store manifest was empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Timeline store could not be read. Rebuild the Timeline store.", ex);
        }
    }

    private static JsonObject ConvertDownloadResultDetails(TimelineStoreDownloadResponse result)
    {
        return new JsonObject
        {
            ["archivePath"] = result.ArchivePath,
            ["archiveSizeBytes"] = result.ArchiveSizeBytes,
            ["itemCount"] = result.ItemCount,
            ["eventCount"] = result.EventCount,
            ["products"] = result.Products.DeepClone(),
        };
    }

    private static JsonArray GetArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array
            ? array.DeepClone().AsArray()
            : [];
    }

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

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return ConvertTimelineText(node.GetValue<object>());
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static int GetInt(JsonObject? source, string name, int fallback)
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
                return node.GetValue<int>();
            }

            return int.TryParse(ConvertTimelineText(node.GetValue<object>()), out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
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
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }
}

public sealed class TimelineStoreDownloadResponse
{
    [JsonPropertyName("archivePath")]
    public string ArchivePath { get; set; } = "";

    [JsonPropertyName("archiveSizeBytes")]
    public long ArchiveSizeBytes { get; set; }

    [JsonPropertyName("itemCount")]
    public int ItemCount { get; set; }

    [JsonPropertyName("eventCount")]
    public int EventCount { get; set; }

    [JsonPropertyName("products")]
    public JsonArray Products { get; set; } = [];
}
