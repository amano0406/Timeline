using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed class TimelineDashboardStatsService
{
    private const int DefaultDayCount = 30;
    private const string DefaultRange = "last90";
    private const string DefaultBucket = "auto";

    private readonly TimelineSettingsService _settings;
    private readonly object _cacheLock = new();
    private string _cacheKey = "";
    private TimelineDashboardStatsResponse? _cache;

    public TimelineDashboardStatsService(TimelineSettingsService settings)
    {
        _settings = settings;
    }

    public TimelineDashboardStatsResponse GetStats(int dayCount = DefaultDayCount)
        => GetStats(string.Empty, DefaultBucket, dayCount);

    public TimelineDashboardStatsResponse GetStats(string range, string bucket = DefaultBucket, int dayCount = 0)
    {
        var rangeSpec = ResolveRange(range, bucket, dayCount);
        var storeDirectory = _settings.GetStoreDirectory();
        var itemsPath = Path.Combine(storeDirectory, "items.jsonl");
        var summariesIndexPath = Path.Combine(storeDirectory, "derived", "item_summaries", "index.jsonl");

        if (!File.Exists(itemsPath))
        {
            return new TimelineDashboardStatsResponse
            {
                Available = false,
                StoreDirectory = storeDirectory,
                Message = "Timeline store items were not found.",
            };
        }

        var cacheKey = BuildCacheKey(rangeSpec.CacheKey, itemsPath, summariesIndexPath);
        lock (_cacheLock)
        {
            if (_cache is not null && _cacheKey.Equals(cacheKey, StringComparison.Ordinal))
            {
                return _cache;
            }
        }

        var stats = BuildStats(storeDirectory, itemsPath, summariesIndexPath, rangeSpec);
        lock (_cacheLock)
        {
            _cacheKey = cacheKey;
            _cache = stats;
        }

        return stats;
    }

    private static TimelineDashboardStatsResponse BuildStats(
        string storeDirectory,
        string itemsPath,
        string summariesIndexPath,
        DashboardRangeSpec rangeSpec)
    {
        var itemRows = LoadItems(itemsPath);
        var summaryIndex = LoadSummaryIndex(summariesIndexPath);
        var filteredRows = itemRows
            .Where(item => IsInRange(GetLocalDate(item.CreatedAt), rangeSpec))
            .ToList();

        var productTotals = new Dictionary<string, DashboardProductStat>(StringComparer.OrdinalIgnoreCase);
        var dayStats = new SortedDictionary<DateOnly, DashboardDailyAccumulator>();
        var completedSummaries = 0;
        var failedSummaries = 0;
        long summarizedContextChars = 0;
        long summaryTextChars = 0;

        foreach (var item in filteredRows)
        {
            var product = item.Product.Length == 0 ? "unknown" : item.Product;
            var productName = item.ProductName.Length == 0 ? GetProductDisplayName(product) : item.ProductName;
            if (!productTotals.TryGetValue(product, out var productStat))
            {
                productStat = new DashboardProductStat
                {
                    ProductId = product,
                    DisplayName = productName,
                };
                productTotals[product] = productStat;
            }

            productStat.ItemCount++;
            productStat.EventCount += item.EventCount;

            var day = GetLocalDate(item.CreatedAt);
            if (day is not null)
            {
                var bucketStart = GetBucketStart(day.Value, rangeSpec.Bucket);
                if (!dayStats.TryGetValue(bucketStart, out var dayStat))
                {
                    dayStat = new DashboardDailyAccumulator(bucketStart, rangeSpec.Bucket);
                    dayStats[bucketStart] = dayStat;
                }

                dayStat.ItemCount++;
                dayStat.EventCount += item.EventCount;
                dayStat.AddProduct(product, 1);
            }

            if (summaryIndex.TryGetValue(NewSummaryKey(product, item.ItemId), out var summary))
            {
                if (summary.Completed)
                {
                    completedSummaries++;
                    productStat.SummaryCount++;
                    productStat.ContextChars += summary.ContextChars;
                    productStat.SummaryTextChars += summary.SummaryChars;
                    summarizedContextChars += summary.ContextChars;
                    summaryTextChars += summary.SummaryChars;
                    if (day is not null)
                    {
                        var bucketStart = GetBucketStart(day.Value, rangeSpec.Bucket);
                        if (dayStats.TryGetValue(bucketStart, out var dayStat))
                        {
                            dayStat.ContextChars += summary.ContextChars;
                            dayStat.SummaryTextChars += summary.SummaryChars;
                        }
                    }
                }
                else if (summary.Failed)
                {
                    failedSummaries++;
                }
            }
        }

        long cumulativeItems = 0;
        long cumulativeEvents = 0;
        long cumulativeContextChars = 0;
        var allDays = dayStats.Values
            .Select(day =>
            {
                cumulativeItems += day.ItemCount;
                cumulativeEvents += day.EventCount;
                cumulativeContextChars += day.ContextChars;
                return day.ToPoint(cumulativeItems, cumulativeEvents, cumulativeContextChars);
            })
            .ToList();

        return new TimelineDashboardStatsResponse
        {
            Available = true,
            StoreDirectory = storeDirectory,
            GeneratedAt = DateTimeOffset.Now.ToString("O"),
            DayCount = dayStats.Count,
            Range = rangeSpec.Range,
            RangeLabel = rangeSpec.RangeLabel,
            Bucket = rangeSpec.Bucket,
            From = rangeSpec.From?.ToString("yyyy-MM-dd") ?? string.Empty,
            To = rangeSpec.To?.ToString("yyyy-MM-dd") ?? string.Empty,
            TotalItems = filteredRows.Count,
            TotalEvents = filteredRows.Sum(item => (long)item.EventCount),
            SummaryTargetItems = filteredRows.Count,
            SummaryCompletedItems = completedSummaries,
            SummaryFailedItems = failedSummaries,
            SummarizedContextChars = summarizedContextChars,
            SummaryTextChars = summaryTextChars,
            DailyItems = allDays,
            ProductTotals = productTotals.Values
                .OrderByDescending(product => product.EventCount)
                .ThenBy(product => product.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Message = string.Empty,
        };
    }

    private static DashboardRangeSpec ResolveRange(string range, string bucket, int legacyDayCount)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
        var normalizedRange = NormalizeRange(range, legacyDayCount);
        DateOnly? from = null;
        DateOnly? to = today;
        var rangeLabel = normalizedRange;

        switch (normalizedRange)
        {
            case "last30":
                from = today.AddDays(-29);
                rangeLabel = "直近30日";
                break;
            case "last90":
                from = today.AddDays(-89);
                rangeLabel = "直近90日";
                break;
            case "last365":
                from = today.AddDays(-364);
                rangeLabel = "直近1年";
                break;
            case "thisMonth":
                from = new DateOnly(today.Year, today.Month, 1);
                rangeLabel = "今月";
                break;
            case "lastMonth":
                var firstThisMonth = new DateOnly(today.Year, today.Month, 1);
                var firstLastMonth = firstThisMonth.AddMonths(-1);
                from = firstLastMonth;
                to = firstThisMonth.AddDays(-1);
                rangeLabel = "先月";
                break;
            case "all":
                to = null;
                rangeLabel = "全期間";
                break;
        }

        var resolvedBucket = ResolveBucket(bucket, normalizedRange);
        return new DashboardRangeSpec(
            normalizedRange,
            rangeLabel,
            resolvedBucket,
            from,
            to);
    }

    private static string NormalizeRange(string range, int legacyDayCount)
    {
        if (!string.IsNullOrWhiteSpace(range))
        {
            return range.Trim() switch
            {
                "last30" or "last90" or "last365" or "thisMonth" or "lastMonth" or "all" => range.Trim(),
                _ => DefaultRange,
            };
        }

        return legacyDayCount switch
        {
            > 0 and <= 45 => "last30",
            > 45 and <= 180 => "last90",
            > 180 => "last365",
            _ => DefaultRange,
        };
    }

    private static string ResolveBucket(string bucket, string range)
    {
        if (!string.IsNullOrWhiteSpace(bucket) && !bucket.Equals(DefaultBucket, StringComparison.OrdinalIgnoreCase))
        {
            return bucket.Trim() switch
            {
                "day" or "week" or "month" => bucket.Trim(),
                _ => "day",
            };
        }

        return range switch
        {
            "last365" => "week",
            "all" => "month",
            _ => "day",
        };
    }

    private static bool IsInRange(DateOnly? date, DashboardRangeSpec range)
    {
        if (date is null)
        {
            return range.Range.Equals("all", StringComparison.OrdinalIgnoreCase);
        }

        if (range.From is not null && date.Value < range.From.Value)
        {
            return false;
        }

        return range.To is null || date.Value <= range.To.Value;
    }

    private static DateOnly? GetLocalDate(DateTimeOffset? value)
        => value is null ? null : DateOnly.FromDateTime(value.Value.ToLocalTime().Date);

    private static DateOnly GetBucketStart(DateOnly date, string bucket)
    {
        return bucket switch
        {
            "month" => new DateOnly(date.Year, date.Month, 1),
            "week" => date.AddDays(-GetMondayOffset(date)),
            _ => date,
        };
    }

    private static int GetMondayOffset(DateOnly date)
    {
        var day = (int)date.DayOfWeek;
        return day == 0 ? 6 : day - 1;
    }

    private static List<DashboardItemRow> LoadItems(string itemsPath)
    {
        var rows = new List<DashboardItemRow>();
        foreach (var line in File.ReadLines(itemsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is not JsonObject item)
                {
                    continue;
                }

                rows.Add(new DashboardItemRow(
                    GetString(item, "product", string.Empty),
                    GetString(item, "productName", string.Empty),
                    GetString(item, "itemId", string.Empty),
                    ParseDateTime(GetString(item, "createdAt", string.Empty)),
                    GetInt(item, "eventCount", 0)));
            }
            catch (JsonException)
            {
            }
        }

        return rows;
    }

    private static Dictionary<string, DashboardSummaryStat> LoadSummaryIndex(string summariesIndexPath)
    {
        var summaries = new Dictionary<string, DashboardSummaryStat>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(summariesIndexPath))
        {
            return summaries;
        }

        foreach (var line in File.ReadLines(summariesIndexPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is not JsonObject summary)
                {
                    continue;
                }

                var product = GetString(summary, "product", string.Empty);
                var itemId = GetString(summary, "itemId", string.Empty);
                if (product.Length == 0 || itemId.Length == 0)
                {
                    continue;
                }

                var state = GetString(summary, "state", string.Empty);
                var brief = GetString(summary, "briefSummary", string.Empty);
                var compressed = GetString(summary, "compressedSummary", string.Empty);
                var completed = state.Equals("completed", StringComparison.OrdinalIgnoreCase)
                    && (!string.IsNullOrWhiteSpace(brief) || !string.IsNullOrWhiteSpace(compressed));
                var failed = state.Equals("failed", StringComparison.OrdinalIgnoreCase);
                var sourceChars = completed ? ReadSummarySourceChars(GetString(summary, "path", string.Empty)) : 0;
                var summaryChars = completed ? brief.Length + compressed.Length : 0;
                summaries[NewSummaryKey(product, itemId)] = new DashboardSummaryStat(
                    completed,
                    failed,
                    sourceChars,
                    summaryChars);
            }
            catch (JsonException)
            {
            }
        }

        return summaries;
    }

    private static long ReadSummarySourceChars(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return 0;
        }

        try
        {
            var payload = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var compression = GetObject(payload, "compression");
            var source = GetObject(payload, "source");
            return Math.Max(
                GetLong(compression, "sourceChars", 0),
                GetLong(source, "readableCharCount", 0));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string BuildCacheKey(string rangeKey, params string[] paths)
    {
        var parts = new List<string> { rangeKey };
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                parts.Add($"{path}:missing");
                continue;
            }

            var info = new FileInfo(path);
            parts.Add($"{path}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }

        return string.Join("|", parts);
    }

    private static string NewSummaryKey(string product, string itemId)
        => $"{product.Trim().ToLowerInvariant()}/{itemId.Trim()}";

    private static DateTimeOffset? ParseDateTime(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

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

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<object>()?.ToString()?.Trim() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static int GetInt(JsonObject? source, string name, int fallback)
        => (int)Math.Clamp(GetLong(source, name, fallback), int.MinValue, int.MaxValue);

    private static long GetLong(JsonObject? source, string name, long fallback)
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
                return node.GetValue<long>();
            }

            return long.TryParse(node.GetValue<object>()?.ToString(), out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static string GetProductDisplayName(string productId)
    {
        return productId switch
        {
            "audio" => "TimelineForAudio",
            "windows-codex" => "TimelineForWindowsCodex",
            "chatgpt" => "TimelineForChatGPT",
            "image" => "TimelineForImage",
            "video" => "TimelineForVideo",
            "pc" => "TimelineForPcInfo",
            _ => productId,
        };
    }

    private sealed record DashboardItemRow(
        string Product,
        string ProductName,
        string ItemId,
        DateTimeOffset? CreatedAt,
        int EventCount);

    private sealed record DashboardRangeSpec(
        string Range,
        string RangeLabel,
        string Bucket,
        DateOnly? From,
        DateOnly? To)
    {
        public string CacheKey => $"{Range}:{Bucket}:{From?.ToString("yyyy-MM-dd") ?? ""}:{To?.ToString("yyyy-MM-dd") ?? ""}";
    }

    private sealed record DashboardSummaryStat(
        bool Completed,
        bool Failed,
        long ContextChars,
        long SummaryChars);

    private sealed class DashboardDailyAccumulator
    {
        public DashboardDailyAccumulator(DateOnly date, string bucket)
        {
            Date = date;
            Bucket = bucket;
        }

        public DateOnly Date { get; }
        public string Bucket { get; }
        public int ItemCount { get; set; }
        public long EventCount { get; set; }
        public long ContextChars { get; set; }
        public long SummaryTextChars { get; set; }
        public Dictionary<string, int> ProductCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddProduct(string productId, int count)
        {
            ProductCounts.TryGetValue(productId, out var current);
            ProductCounts[productId] = current + count;
        }

        public DashboardDailyPoint ToPoint(long cumulativeItems, long cumulativeEvents, long cumulativeContextChars)
        {
            return new DashboardDailyPoint
            {
                Date = Date.ToString("yyyy-MM-dd"),
                Label = FormatBucketLabel(Date, Bucket),
                ItemCount = ItemCount,
                EventCount = EventCount,
                ContextChars = ContextChars,
                SummaryTextChars = SummaryTextChars,
                CumulativeItems = cumulativeItems,
                CumulativeEvents = cumulativeEvents,
                CumulativeContextChars = cumulativeContextChars,
                ProductCounts = ProductCounts
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            };
        }

        private static string FormatBucketLabel(DateOnly date, string bucket)
        {
            return bucket switch
            {
                "month" => date.ToString("yyyy/MM"),
                "week" => $"{date:MM/dd}週",
                _ => date.ToString("M/d"),
            };
        }
    }
}

public sealed class TimelineDashboardStatsResponse
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("storeDirectory")]
    public string StoreDirectory { get; set; } = "";

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";

    [JsonPropertyName("dayCount")]
    public int DayCount { get; set; }

    [JsonPropertyName("range")]
    public string Range { get; set; } = "";

    [JsonPropertyName("rangeLabel")]
    public string RangeLabel { get; set; } = "";

    [JsonPropertyName("bucket")]
    public string Bucket { get; set; } = "";

    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonPropertyName("totalItems")]
    public int TotalItems { get; set; }

    [JsonPropertyName("totalEvents")]
    public long TotalEvents { get; set; }

    [JsonPropertyName("summaryTargetItems")]
    public int SummaryTargetItems { get; set; }

    [JsonPropertyName("summaryCompletedItems")]
    public int SummaryCompletedItems { get; set; }

    [JsonPropertyName("summaryFailedItems")]
    public int SummaryFailedItems { get; set; }

    [JsonPropertyName("summarizedContextChars")]
    public long SummarizedContextChars { get; set; }

    [JsonPropertyName("summaryTextChars")]
    public long SummaryTextChars { get; set; }

    [JsonPropertyName("dailyItems")]
    public List<DashboardDailyPoint> DailyItems { get; set; } = [];

    [JsonPropertyName("productTotals")]
    public List<DashboardProductStat> ProductTotals { get; set; } = [];

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class DashboardDailyPoint
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("itemCount")]
    public int ItemCount { get; set; }

    [JsonPropertyName("eventCount")]
    public long EventCount { get; set; }

    [JsonPropertyName("contextChars")]
    public long ContextChars { get; set; }

    [JsonPropertyName("summaryTextChars")]
    public long SummaryTextChars { get; set; }

    [JsonPropertyName("cumulativeItems")]
    public long CumulativeItems { get; set; }

    [JsonPropertyName("cumulativeEvents")]
    public long CumulativeEvents { get; set; }

    [JsonPropertyName("cumulativeContextChars")]
    public long CumulativeContextChars { get; set; }

    [JsonPropertyName("productCounts")]
    public Dictionary<string, int> ProductCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DashboardProductStat
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("itemCount")]
    public int ItemCount { get; set; }

    [JsonPropertyName("eventCount")]
    public long EventCount { get; set; }

    [JsonPropertyName("summaryCount")]
    public int SummaryCount { get; set; }

    [JsonPropertyName("contextChars")]
    public long ContextChars { get; set; }

    [JsonPropertyName("summaryTextChars")]
    public long SummaryTextChars { get; set; }
}
