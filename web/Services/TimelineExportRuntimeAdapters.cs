using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Timeline.Web.Services;

public sealed record TimelineLocalApiOptions(
    int WebPort,
    string TimelineProductPath,
    string WindowsCodexProductPath);

public sealed class TimelineSettingsService
{
    private readonly IConfiguration _configuration;

    public TimelineSettingsService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GetStoreDirectory()
    {
        return FullPath(
            _configuration["Timeline:StoreDirectory"]
            ?? Environment.GetEnvironmentVariable("TIMELINE_STORE_DIRECTORY")
            ?? "/data/store");
    }

    public string GetWorkDirectory()
    {
        return FullPath(
            _configuration["Timeline:WorkDirectory"]
            ?? Environment.GetEnvironmentVariable("TIMELINE_WORK_DIRECTORY")
            ?? "/data/work");
    }

    private static string FullPath(string path)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
    }
}

public sealed class TimelineStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly TimelineSettingsService _settings;

    public TimelineStoreService(TimelineSettingsService settings)
    {
        _settings = settings;
    }

    public TimelineStoreOverviewState GetOverview()
    {
        var storeRoot = _settings.GetStoreDirectory();
        var manifestPath = Path.Combine(storeRoot, "manifest.json");
        var itemsPath = Path.Combine(storeRoot, "items.jsonl");
        var eventsPath = Path.Combine(storeRoot, "events.jsonl");
        return new TimelineStoreOverviewState
        {
            Available = File.Exists(manifestPath) && File.Exists(itemsPath) && File.Exists(eventsPath),
        };
    }

    public TimelineStoreOverview GetWebOverview()
    {
        var storeRoot = _settings.GetStoreDirectory();
        var manifestPath = Path.Combine(storeRoot, "manifest.json");
        var itemsPath = Path.Combine(storeRoot, "items.jsonl");
        var eventsPath = Path.Combine(storeRoot, "events.jsonl");

        if (!File.Exists(manifestPath))
        {
            return new TimelineStoreOverview
            {
                Available = false,
                StoreDirectory = storeRoot,
                ManifestPath = manifestPath,
                ItemsPath = itemsPath,
                EventsPath = eventsPath,
                Message = "Timeline store has not been rebuilt yet.",
            };
        }

        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
            var products = GetArray(manifest, "products");
            var available = File.Exists(itemsPath) && File.Exists(eventsPath);
            return new TimelineStoreOverview
            {
                Available = available,
                StoreDirectory = storeRoot,
                RebuildId = GetString(manifest, "rebuildId", string.Empty),
                CreatedAt = GetString(manifest, "createdAt", string.Empty),
                ItemCount = GetInt(manifest, "itemCount", 0),
                EventCount = GetInt(manifest, "eventCount", 0),
                ProductCount = products.Count,
                Products = products.Select(ConvertProduct).ToList(),
                ManifestPath = manifestPath,
                ItemsPath = itemsPath,
                EventsPath = eventsPath,
                Message = available ? string.Empty : "Timeline store files were not found. Rebuild the Timeline store.",
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TimelineStoreOverview
            {
                Available = false,
                StoreDirectory = storeRoot,
                ManifestPath = manifestPath,
                ItemsPath = itemsPath,
                EventsPath = eventsPath,
                Message = "Timeline store could not be read. Rebuild the Timeline store.",
            };
        }
    }

    public ProductRuntimeOverview GetProductRuntimeOverviewFallback()
    {
        var overview = GetWebOverview();
        if (overview.Products.Count == 0)
        {
            return new ProductRuntimeOverview
            {
                Message = overview.Available
                    ? "Storeには製品情報がありません。"
                    : "Storeがまだ作成されていないため、製品情報を確認できません。",
            };
        }

        return new ProductRuntimeOverview
        {
            Message = "Storeに保存済みの製品情報を表示しています。起動状態は未確認です。",
            Products = overview.Products
                .Select(product => new ProductRuntimeRow
                {
                    Id = product.ProductId,
                    DisplayName = string.IsNullOrWhiteSpace(product.DisplayName)
                        ? product.ProductId
                        : product.DisplayName,
                    ProductFound = product.Included || product.ItemCount > 0 || product.EventCount > 0,
                    ComposeFound = product.Included || product.ItemCount > 0 || product.EventCount > 0,
                    Enabled = product.Included || product.ItemCount > 0 || product.EventCount > 0,
                    Running = false,
                    State = "unknown",
                    Status = "未確認",
                    PagePath = ProductPagePath(product.ProductId),
                    SettingsPath = "timeline/settings",
                    Message = "最後にスキャンで取り込めた製品です。現在の起動状態は未確認です。",
                })
                .ToList(),
        };
    }

    public TimelineProductOverview GetAudioOverviewFallback()
    {
        var product = GetStoreProduct("audio");
        var found = IsStoreProductAvailable(product);
        return new TimelineProductOverview
        {
            ProductFound = found,
            ProductPath = product.ArchivePath,
            HasToken = found,
            InputRoots = found
                ? [new RootRow { Id = "store", DisplayName = "Timeline Store", Path = _settings.GetStoreDirectory(), Enabled = true }]
                : [],
            OutputRoot = found
                ? new RootRow { Id = "store", DisplayName = "Timeline Store", Path = _settings.GetStoreDirectory(), Enabled = true }
                : null,
            AudioFileCount = product.ItemCount,
            AudioItemCount = product.ItemCount,
            WorkerState = "未確認",
            Message = found
                ? "保存済みのTimeline Storeから音声ファイルの概要を表示しています。現在のサブ製品起動状態は未確認です。"
                : "保存済みのTimeline Storeに音声ファイルの情報がありません。",
        };
    }

    public ImageOverview GetImageOverviewFallback()
    {
        var product = GetStoreProduct("image");
        var found = IsStoreProductAvailable(product);
        return new ImageOverview
        {
            ProductFound = found,
            ProductPath = product.ArchivePath,
            SettingsValid = found,
            SourceFileCount = product.ItemCount,
            ItemCount = product.ItemCount,
            LatestRefresh = new ImageRefreshResult
            {
                State = found ? "store" : "missing",
                SourceCount = product.ItemCount,
                ProcessedCount = product.ItemCount,
            },
            Message = found
                ? "保存済みのTimeline Storeから画像ファイルの概要を表示しています。現在のサブ製品起動状態は未確認です。"
                : "保存済みのTimeline Storeに画像ファイルの情報がありません。",
        };
    }

    public VideoOverview GetVideoOverviewFallback()
    {
        var product = GetStoreProduct("video");
        var found = IsStoreProductAvailable(product);
        return new VideoOverview
        {
            ProductFound = found,
            ProductPath = product.ArchivePath,
            SettingsValid = found,
            Settings = new VideoSettings
            {
                HasToken = found,
                InputRoots = found
                    ? [new VideoInputRoot { Id = "store", DisplayName = "Timeline Store", Path = _settings.GetStoreDirectory(), DisplayPath = _settings.GetStoreDirectory(), Enabled = true, Exists = true }]
                    : [],
                OutputRoot = new VideoDirectoryRoot { Id = "store", DisplayName = "Timeline Store", Path = _settings.GetStoreDirectory(), DisplayPath = _settings.GetStoreDirectory(), Exists = found },
            },
            SourceFileCount = product.ItemCount,
            ItemCount = product.ItemCount,
            Message = found
                ? "保存済みのTimeline Storeから動画ファイルの概要を表示しています。現在のサブ製品起動状態は未確認です。"
                : "保存済みのTimeline Storeに動画ファイルの情報がありません。",
        };
    }

    public WindowsCodexOverview GetWindowsCodexOverviewFallback()
    {
        var product = GetStoreProduct("windows-codex");
        var found = IsStoreProductAvailable(product);
        return new WindowsCodexOverview
        {
            ProductFound = found,
            ProductPath = product.ArchivePath,
            SettingsValid = found,
            Current = new WindowsCodexCurrent
            {
                Available = found,
                State = found ? "store" : "missing",
                ThreadCount = product.ItemCount,
                EventCount = product.EventCount,
                ReusedThreadCount = product.ItemCount,
                Message = found
                    ? "保存済みのTimeline StoreからWindows Codexの概要を表示しています。"
                    : "保存済みのTimeline StoreにWindows Codexの情報がありません。",
            },
            Message = found
                ? "保存済みのTimeline StoreからWindows Codexの概要を表示しています。現在のサブ製品起動状態は未確認です。"
                : "保存済みのTimeline StoreにWindows Codexの情報がありません。",
        };
    }

    public ChatGptOverview GetChatGptOverviewFallback()
    {
        var product = GetStoreProduct("chatgpt");
        var found = IsStoreProductAvailable(product);
        return new ChatGptOverview
        {
            ProductFound = found,
            ProductPath = product.ArchivePath,
            SettingsFound = found,
            SettingsValid = found,
            ProcessableInputCount = product.ItemCount,
            ItemCount = product.ItemCount,
            LatestRefresh = new ChatGptRefreshSummary
            {
                Available = found,
                Discovered = product.ItemCount,
                Processed = product.ItemCount,
            },
            Message = found
                ? "保存済みのTimeline StoreからChatGPTの概要を表示しています。現在のサブ製品起動状態は未確認です。"
                : "保存済みのTimeline StoreにChatGPTの情報がありません。",
        };
    }

    public PcOverview GetPcOverviewFallback()
    {
        var product = GetStoreProduct("pc");
        var found = IsStoreProductAvailable(product);
        return new PcOverview
        {
            ProductFound = found,
            ProductPath = product.ArchivePath,
            SettingsValid = found,
            ItemCount = product.ItemCount,
            Message = found
                ? "保存済みのTimeline StoreからPC状態の概要を表示しています。現在のサブ製品起動状態は未確認です。"
                : "保存済みのTimeline StoreにPC状態の情報がありません。",
        };
    }

    private static string ProductPagePath(string productId)
        => productId switch
        {
            "audio" => "audio",
            "video" => "video",
            "image" => "image",
            "chatgpt" => "chatgpt",
            "windows-codex" => "windows-codex",
            "pc" => "pc",
            _ => string.Empty,
        };

    private TimelineExportProductResult GetStoreProduct(string productId)
        => GetWebOverview().Products.FirstOrDefault(product =>
            product.ProductId.Equals(productId, StringComparison.OrdinalIgnoreCase))
            ?? new TimelineExportProductResult { ProductId = productId };

    private static bool IsStoreProductAvailable(TimelineExportProductResult product)
        => product.Included || product.ItemCount > 0 || product.EventCount > 0;

    public TimelineItemSummaryJobStatus GetItemSummaryStatus(string? jobId)
    {
        var path = GetItemSummaryStatusPath(jobId);
        if (!File.Exists(path))
        {
            return new TimelineItemSummaryJobStatus
            {
                Available = false,
                State = "none",
                Stage = "none",
                Message = "素材概要の生成ジョブはまだありません。",
            };
        }

        try
        {
            return JsonSerializer.Deserialize<TimelineItemSummaryJobStatus>(File.ReadAllText(path), JsonOptions)
                ?? new TimelineItemSummaryJobStatus
                {
                    Available = false,
                    State = "unreadable",
                    Message = "素材概要の状態を読み取れませんでした。",
                };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TimelineItemSummaryJobStatus
            {
                Available = false,
                State = "unreadable",
                Message = "素材概要の状態を読み取れませんでした。",
                Error = ex.Message,
            };
        }
    }

    public TimelineItemSummary GetItemSummary(string? product, string? itemId)
    {
        var productId = ConvertTimelineText(product);
        var item = ConvertTimelineText(itemId);
        if (string.IsNullOrEmpty(productId) || string.IsNullOrEmpty(item))
        {
            return new TimelineItemSummary
            {
                Available = false,
                Message = "product and itemId are required.",
            };
        }

        var path = GetItemSummaryPath(productId, item);
        if (!File.Exists(path))
        {
            return new TimelineItemSummary
            {
                Available = false,
                Product = productId,
                ItemId = item,
                Path = path,
                Message = "素材概要はまだ作成されていません。",
            };
        }

        try
        {
            var summary = JsonSerializer.Deserialize<TimelineItemSummary>(File.ReadAllText(path), JsonOptions)
                ?? new TimelineItemSummary
                {
                    Product = productId,
                    ItemId = item,
                    Message = "素材概要を読み取れませんでした。",
                };
            summary.Available = true;
            summary.Path = path;
            return summary;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TimelineItemSummary
            {
                Available = false,
                Product = productId,
                ItemId = item,
                Path = path,
                Message = "素材概要を読み取れませんでした。",
                Error = ex.Message,
            };
        }
    }

    private static TimelineExportProductResult ConvertProduct(JsonNode? node)
    {
        var source = node as JsonObject;
        return new TimelineExportProductResult
        {
            ProductId = GetString(source, "productId", string.Empty),
            DisplayName = GetString(source, "displayName", string.Empty),
            ArchivePath = GetString(source, "archivePath", string.Empty),
            Included = GetBool(source, "included", false),
            ItemCount = GetInt(source, "itemCount", 0),
            EventCount = GetInt(source, "eventCount", 0),
            Message = GetString(source, "message", string.Empty),
        };
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

    private static bool GetBool(JsonObject? source, string name, bool fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValueKind() == JsonValueKind.True
                || bool.TryParse(ConvertTimelineText(node.GetValue<object>()), out var parsed) && parsed;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static JsonArray GetArray(JsonObject? source, string name)
        => GetNode(source, name) as JsonArray ?? [];

    private string GetItemSummaryRoot()
        => Path.Combine(_settings.GetStoreDirectory(), "derived", "item_summaries");

    private string GetItemSummaryStatusPath(string? jobId)
    {
        var text = ConvertTimelineText(jobId);
        return string.IsNullOrEmpty(text)
            ? Path.Combine(GetItemSummaryRoot(), "_jobs", "latest.json")
            : Path.Combine(GetItemSummaryRoot(), "_jobs", GetSafeSegment(text) + ".json");
    }

    private string GetItemSummaryPath(string product, string itemId)
        => Path.Combine(GetItemSummaryRoot(), GetSafeSegment(product), GetSafeSegment(itemId) + ".json");

    private static string GetSafeSegment(string value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return "item";
        }

        var safe = Regex.Replace(text, "[^A-Za-z0-9._-]+", "_").Trim('.', '_', '-');
        if (string.IsNullOrEmpty(safe))
        {
            safe = "item";
        }

        return safe.Length > 120 ? safe[..120] : safe;
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

public sealed class TimelineStoreOverviewState
{
    public bool Available { get; init; }
}

public sealed class TimelineOperationLogService
{
    private readonly ILogger<TimelineOperationLogService> _logger;

    public TimelineOperationLogService(ILogger<TimelineOperationLogService> logger)
    {
        _logger = logger;
    }

    public string NewOperationId(string source)
    {
        return $"{source}-{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32];
    }

    public void WriteOperationEvent(
        string operationId,
        string source,
        string product,
        string action,
        string status,
        string message,
        int? durationMs = null,
        string? stdout = null,
        string? stderr = null,
        JsonObject? details = null)
    {
        _logger.LogInformation(
            "Timeline operation {OperationId} {Source} {Product} {Action} {Status} {DurationMs}ms {Message}",
            operationId,
            source,
            product,
            action,
            status,
            durationMs,
            message);
    }
}

public static class TimelinePathConverter
{
    public static string ConvertTimelineWindowsPath(string path, TimelineLocalApiOptions options)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var storeRoot = Environment.GetEnvironmentVariable("TIMELINE_STORE_DIRECTORY") ?? "/data/store";
        var workRoot = Environment.GetEnvironmentVariable("TIMELINE_WORK_DIRECTORY") ?? "/data/work";
        var timelineRoot = @"C:\apps\Timeline";
        var storeWindowsRoot = timelineRoot + @"\data\to_timeline";
        var workWindowsRoot = timelineRoot + @"\data\work";

        if (text.Equals(storeWindowsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return storeRoot;
        }

        if (text.StartsWith(storeWindowsRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(storeRoot, text[(storeWindowsRoot.Length + 1)..].Replace('\\', Path.DirectorySeparatorChar));
        }

        if (text.Equals(workWindowsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return workRoot;
        }

        if (text.StartsWith(workWindowsRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(workRoot, text[(workWindowsRoot.Length + 1)..].Replace('\\', Path.DirectorySeparatorChar));
        }

        if (text.StartsWith("/mnt/c/", StringComparison.OrdinalIgnoreCase))
        {
            return "C:\\" + text[7..].Replace("/", "\\");
        }

        return text;
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
