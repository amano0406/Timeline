using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed class TimelineStoreService
{
    private readonly TimelineSettingsService _settings;

    public TimelineStoreService(TimelineSettingsService settings)
    {
        _settings = settings;
    }

    public TimelineEventListResponse GetEvents(int page, int pageSize)
    {
        var overview = GetOverview();
        if (!overview.Available)
        {
            return new TimelineEventListResponse
            {
                Available = false,
                Total = 0,
                Pagination = NewPagination(page, pageSize, 0, 0),
                Events = [],
                Message = overview.Message,
            };
        }

        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Max(1, pageSize);
        var offset = (effectivePage - 1) * effectivePageSize;
        var rows = new List<TimelineEventRowResponse>();
        var total = 0;

        foreach (var line in File.ReadLines(Path.Combine(_settings.GetStoreDirectory(), "events.jsonl")))
        {
            var text = line.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (total >= offset && rows.Count < effectivePageSize)
            {
                try
                {
                    if (JsonNode.Parse(text) is JsonObject row)
                    {
                        rows.Add(ConvertEventRow(row));
                    }
                }
                catch (JsonException)
                {
                }
            }

            total += 1;
        }

        return new TimelineEventListResponse
        {
            Available = true,
            Total = total,
            Pagination = NewPagination(effectivePage, effectivePageSize, total, rows.Count),
            Events = rows,
            Message = string.Empty,
        };
    }

    public TimelineStoreOverviewResponse GetOverview()
    {
        var storeRoot = _settings.GetStoreDirectory();
        var manifestPath = Path.Combine(storeRoot, "manifest.json");
        var itemsPath = Path.Combine(storeRoot, "items.jsonl");
        var eventsPath = Path.Combine(storeRoot, "events.jsonl");

        if (!File.Exists(manifestPath))
        {
            return new TimelineStoreOverviewResponse
            {
                Available = false,
                StoreDirectory = storeRoot,
                RebuildId = string.Empty,
                CreatedAt = string.Empty,
                ItemCount = 0,
                EventCount = 0,
                ProductCount = 0,
                Products = [],
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

            return new TimelineStoreOverviewResponse
            {
                Available = available,
                StoreDirectory = storeRoot,
                RebuildId = GetString(manifest, "rebuildId", string.Empty),
                CreatedAt = GetString(manifest, "createdAt", string.Empty),
                ItemCount = GetInt(manifest, "itemCount", 0),
                EventCount = GetInt(manifest, "eventCount", 0),
                ProductCount = products.Count,
                Products = products,
                ManifestPath = manifestPath,
                ItemsPath = itemsPath,
                EventsPath = eventsPath,
                Message = available ? string.Empty : "Timeline store files were not found. Rebuild the Timeline store.",
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TimelineStoreOverviewResponse
            {
                Available = false,
                StoreDirectory = storeRoot,
                RebuildId = string.Empty,
                CreatedAt = string.Empty,
                ItemCount = 0,
                EventCount = 0,
                ProductCount = 0,
                Products = [],
                ManifestPath = manifestPath,
                ItemsPath = itemsPath,
                EventsPath = eventsPath,
                Message = "Timeline store could not be read. Rebuild the Timeline store.",
            };
        }
    }

    private static TimelineEventRowResponse ConvertEventRow(JsonObject source)
    {
        var time = GetObject(source, "time");
        var actor = GetObject(source, "actor");
        var content = GetObject(source, "content");
        var productId = GetString(source, "product", string.Empty);

        return new TimelineEventRowResponse
        {
            EventId = GetString(source, "eventId", string.Empty),
            Product = productId,
            ProductName = GetProductDisplayName(productId),
            ItemId = GetString(source, "itemId", string.Empty),
            EventType = GetString(source, "eventType", string.Empty),
            Sequence = GetInt(source, "sequence", 0),
            OccurredAt = GetString(time, "absoluteStartAt", string.Empty),
            EndedAt = GetString(time, "absoluteEndAt", string.Empty),
            RelativeStartSec = CloneNode(GetNode(time, "relativeStartSec")),
            RelativeEndSec = CloneNode(GetNode(time, "relativeEndSec")),
            TimeBasis = GetString(time, "timeBasis", string.Empty),
            ActorType = GetString(actor, "type", string.Empty),
            ActorLabel = GetString(actor, "label", string.Empty),
            ContentKind = GetString(content, "kind", string.Empty),
            ContentValue = GetString(content, "value", string.Empty),
        };
    }

    private static TimelinePaginationResponse NewPagination(
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
        return new TimelinePaginationResponse
        {
            Mode = "page",
            Page = effectivePage,
            PageSize = effectivePageSize,
            TotalItems = totalItems,
            TotalPages = totalPages,
            ReturnedItems = returnedItems,
            Offset = offset,
            RangeStart = returnedItems > 0 ? offset + 1 : 0,
            RangeEnd = returnedItems > 0 ? offset + returnedItems : 0,
            HasPrevious = effectivePage > 1 && totalItems > 0,
            HasNext = effectivePage < totalPages,
        };
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

    private static JsonObject? GetObject(JsonObject? source, string name)
        => GetNode(source, name) as JsonObject;

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

    private static JsonArray GetArray(JsonObject? source, string name)
    {
        return GetNode(source, name) as JsonArray ?? [];
    }

    private static JsonNode? CloneNode(JsonNode? node)
        => node?.DeepClone();

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

public sealed class TimelineStoreOverviewResponse
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("storeDirectory")]
    public string StoreDirectory { get; set; } = "";

    [JsonPropertyName("rebuildId")]
    public string RebuildId { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("itemCount")]
    public int ItemCount { get; set; }

    [JsonPropertyName("eventCount")]
    public int EventCount { get; set; }

    [JsonPropertyName("productCount")]
    public int ProductCount { get; set; }

    [JsonPropertyName("products")]
    public JsonArray Products { get; set; } = [];

    [JsonPropertyName("manifestPath")]
    public string ManifestPath { get; set; } = "";

    [JsonPropertyName("itemsPath")]
    public string ItemsPath { get; set; } = "";

    [JsonPropertyName("eventsPath")]
    public string EventsPath { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class TimelineEventListResponse
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("pagination")]
    public TimelinePaginationResponse Pagination { get; set; } = new();

    [JsonPropertyName("events")]
    public List<TimelineEventRowResponse> Events { get; set; } = [];

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class TimelinePaginationResponse
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalItems")]
    public int TotalItems { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("returnedItems")]
    public int ReturnedItems { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("rangeStart")]
    public int RangeStart { get; set; }

    [JsonPropertyName("rangeEnd")]
    public int RangeEnd { get; set; }

    [JsonPropertyName("hasPrevious")]
    public bool HasPrevious { get; set; }

    [JsonPropertyName("hasNext")]
    public bool HasNext { get; set; }
}

public sealed class TimelineEventRowResponse
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = "";

    [JsonPropertyName("product")]
    public string Product { get; set; } = "";

    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = "";

    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = "";

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "";

    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("occurredAt")]
    public string OccurredAt { get; set; } = "";

    [JsonPropertyName("endedAt")]
    public string EndedAt { get; set; } = "";

    [JsonPropertyName("relativeStartSec")]
    public JsonNode? RelativeStartSec { get; set; }

    [JsonPropertyName("relativeEndSec")]
    public JsonNode? RelativeEndSec { get; set; }

    [JsonPropertyName("timeBasis")]
    public string TimeBasis { get; set; } = "";

    [JsonPropertyName("actorType")]
    public string ActorType { get; set; } = "";

    [JsonPropertyName("actorLabel")]
    public string ActorLabel { get; set; } = "";

    [JsonPropertyName("contentKind")]
    public string ContentKind { get; set; } = "";

    [JsonPropertyName("contentValue")]
    public string ContentValue { get; set; } = "";
}
