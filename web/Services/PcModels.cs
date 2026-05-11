namespace Timeline.Web.Services;

public sealed class PcOverview
{
    public bool ProductFound { get; set; }
    public string ProductPath { get; set; } = "";
    public bool SettingsValid { get; set; }
    public PcSettings Settings { get; set; } = new();
    public int ItemCount { get; set; }
    public string Message { get; set; } = "";
}

public sealed class PcSettings
{
    public string SettingsPath { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    public string OutputRootDisplayPath { get; set; } = "";
    public bool OutputRootReady { get; set; }
    public string RedactionProfile { get; set; } = "";
    public string MockProfile { get; set; } = "";
}

public sealed class PcItemListResult
{
    public int Total { get; set; }
    public TimelinePagination Pagination { get; set; } = new();
    public List<PcItemRow> Items { get; set; } = [];
}

public sealed class PcItemRow
{
    public string ItemId { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string Title { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int EventCount { get; set; }
    public string LatestUpdateStatus { get; set; } = "";
    public string TimelinePath { get; set; } = "";
    public string ConvertInfoPath { get; set; } = "";
}

public sealed class PcRefreshResult
{
    public string RunId { get; set; } = "";
    public string State { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string EventId { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public string CompletedAt { get; set; } = "";
}

public sealed class PcItemsRequest
{
    public List<string> ItemIds { get; set; } = [];
    public string OutputPath { get; set; } = "";
}

public sealed class PcItemsDownloadResult
{
    public string ArchivePath { get; set; } = "";
    public List<string> ItemIds { get; set; } = [];
}

public sealed class PcSettingsSaveRequest
{
    public string OutputRoot { get; set; } = "";
    public string OutputRootPath { get; set; } = "";
    public string RedactionProfile { get; set; } = "";
    public string MockProfile { get; set; } = "";
}
