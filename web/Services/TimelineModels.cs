namespace Timeline.Web.Services;

public sealed class RootRow
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class TimelineAppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string DataRoot { get; set; } = "data";
    public string ResolvedDataRoot { get; set; } = "";
    public string DisplayLanguageId { get; set; } = "ja-JP";
    public List<TimelineDisplayLanguageOption> DisplayLanguages { get; set; } = [];
    public string TimeZoneId { get; set; } = "Asia/Tokyo";
    public List<TimelineTimeZoneOption> TimeZones { get; set; } = [];
    public string WorkDirectory { get; set; } = "";
    public string StoreDirectory { get; set; } = "";
    public TimelineCommonAiSettings CommonAi { get; set; } = new();
    public TimelineProductRegistry ProductRegistry { get; set; } = new();
    public TimelineAudioVerbalizationSettings AudioVerbalization { get; set; } = new();
}

public sealed class TimelineAppSettingsSaveRequest
{
    public string DataRoot { get; set; } = "data";
    public string DisplayLanguageId { get; set; } = "ja-JP";
    public string TimeZoneId { get; set; } = "Asia/Tokyo";
    public string WorkDirectory { get; set; } = "";
    public string StoreDirectory { get; set; } = "";
    public TimelineCommonAiSettings? CommonAi { get; set; }
    public TimelineProductRegistry? ProductRegistry { get; set; }
}

public sealed class TimelineCommonAiSettings
{
    public string ComputeMode { get; set; } = "auto";
}

public sealed class TimelineProductRegistry
{
    public List<TimelineProductRegistryProduct> Products { get; set; } = [];
}

public sealed class TimelineProductRegistryProduct
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string SourceType { get; set; } = "github-source-archive";
    public string SourceUrl { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Required { get; set; }
}

public sealed class TimelineAudioVerbalizationSettings
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "ollama";
    public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen3.5:9b";
    public string FastModel { get; set; } = "qwen3.5:4b";
    public string Language { get; set; } = "ja-JP";
    public int ChunkMinMinutes { get; set; } = 5;
    public int ChunkMaxMinutes { get; set; } = 10;
    public int ChunkMaxTurns { get; set; } = 12;
    public int NearbyContextMinutes { get; set; } = 1440;
    public int NearbyTimelineHintMaxEvents { get; set; } = 24;
    public int NearbyTimelineHintMaxChars { get; set; } = 500;
    public int MaxConcurrentJobs { get; set; } = 1;
    public bool AutoRun { get; set; }
    public bool UsePreviousChunkSummary { get; set; } = true;
    public bool UseUnconfirmedVerbalizationAsWeakHint { get; set; } = true;
}

public sealed class TimelineDisplayLanguageOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class TimelineTimeZoneOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class TimelinePagination
{
    public string Mode { get; set; } = "";
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }
    public int ReturnedItems { get; set; }
    public int Offset { get; set; }
    public int RangeStart { get; set; }
    public int RangeEnd { get; set; }
    public bool HasPrevious { get; set; }
    public bool HasNext { get; set; }
}

public sealed class HelperHealth
{
    public bool Ok { get; set; }
}
