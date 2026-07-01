namespace Timeline.Web.Services;

public sealed class TimelineExportDownloadResult
{
    public string ArchivePath { get; set; } = "";
    public long ArchiveSizeBytes { get; set; }
    public int ItemCount { get; set; }
    public int EventCount { get; set; }
    public List<TimelineExportProductResult> Products { get; set; } = [];
}

public sealed class TimelineExportProductResult
{
    public string ProductId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public bool Included { get; set; }
    public int ItemCount { get; set; }
    public int EventCount { get; set; }
    public string Message { get; set; } = "";
}

public sealed class TimelineStoreOverview
{
    public bool Available { get; set; }
    public string StoreDirectory { get; set; } = "";
    public string RebuildId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public int ItemCount { get; set; }
    public int EventCount { get; set; }
    public int ProductCount { get; set; }
    public List<TimelineExportProductResult> Products { get; set; } = [];
    public string ManifestPath { get; set; } = "";
    public string ItemsPath { get; set; } = "";
    public string EventsPath { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class TimelineDashboardStats
{
    public bool Available { get; set; }
    public string StoreDirectory { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    public int DayCount { get; set; }
    public string Range { get; set; } = "";
    public string RangeLabel { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int TotalItems { get; set; }
    public long TotalEvents { get; set; }
    public int SummaryTargetItems { get; set; }
    public int SummaryCompletedItems { get; set; }
    public int SummaryFailedItems { get; set; }
    public long SummarizedContextChars { get; set; }
    public long SummaryTextChars { get; set; }
    public List<TimelineDashboardDailyPoint> DailyItems { get; set; } = [];
    public List<TimelineDashboardProductStat> ProductTotals { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class TimelineDashboardDailyPoint
{
    public string Date { get; set; } = "";
    public string Label { get; set; } = "";
    public int ItemCount { get; set; }
    public long EventCount { get; set; }
    public long ContextChars { get; set; }
    public long SummaryTextChars { get; set; }
    public long CumulativeItems { get; set; }
    public long CumulativeEvents { get; set; }
    public long CumulativeContextChars { get; set; }
    public Dictionary<string, int> ProductCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TimelineDashboardProductStat
{
    public string ProductId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int ItemCount { get; set; }
    public long EventCount { get; set; }
    public int SummaryCount { get; set; }
    public long ContextChars { get; set; }
    public long SummaryTextChars { get; set; }
}

public sealed class TimelineRebuildResult
{
    public string RebuildId { get; set; } = "";
    public string StoreDirectory { get; set; } = "";
    public string PackagePath { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string ItemsPath { get; set; } = "";
    public string EventsPath { get; set; } = "";
    public int ItemCount { get; set; }
    public int EventCount { get; set; }
    public List<TimelineExportProductResult> Products { get; set; } = [];
}

public sealed class TimelineRebuildRequest
{
    public string ChatGptExportZipPath { get; set; } = "";
    public int MaxItems { get; set; }
    public int SamplesPerVideo { get; set; }
    public bool ReprocessDuplicates { get; set; }
}

public sealed class TimelineWorkerJobStatus
{
    public string JobId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public string Error { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string CompletedAt { get; set; } = "";
    public int ItemCount { get; set; }
    public int EventCount { get; set; }
    public TimelineProductJobStatus? ProductJob { get; set; }
    public TimelineRebuildResult? Result { get; set; }
}

public sealed class TimelineItemSummaryJobStatus
{
    public bool Available { get; set; }
    public string JobId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public string Error { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string CompletedAt { get; set; } = "";
    public int TotalItems { get; set; }
    public int CompletedItems { get; set; }
    public int SkippedItems { get; set; }
    public int FailedItems { get; set; }
    public int PendingItems { get; set; }
    public int ReusableItems { get; set; }
    public string SummaryMode { get; set; } = "";
    public TimelineItemSummaryCurrent? Current { get; set; }
}

public sealed class TimelineItemSummaryCurrent
{
    public string Product { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class TimelineItemSummary
{
    public bool Available { get; set; }
    public int SchemaVersion { get; set; }
    public string ArtifactType { get; set; } = "";
    public string State { get; set; } = "";
    public string Product { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string Title { get; set; } = "";
    public string BriefSummary { get; set; } = "";
    public string CompressedSummary { get; set; } = "";
    public string SummaryStatus { get; set; } = "";
    public string GenerationMode { get; set; } = "";
    public string SummaryMode { get; set; } = "";
    public int ChunkCount { get; set; }
    public int RewriteCount { get; set; }
    public string InputSignature { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    public TimelineItemSummaryCompression Compression { get; set; } = new();
    public TimelineItemSummarySource Source { get; set; } = new();
    public string Notice { get; set; } = "";
    public string Path { get; set; } = "";
    public string Message { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class TimelineItemSummaryCompression
{
    public int SourceChars { get; set; }
    public int CompressedCharCount { get; set; }
    public int CompressedLower { get; set; }
    public int CompressedLimit { get; set; }
    public int BriefCharCount { get; set; }
    public int BriefLower { get; set; }
    public int BriefLimit { get; set; }
}

public sealed class TimelineItemSummarySource
{
    public int EventCount { get; set; }
    public int ReadableCharCount { get; set; }
}

public sealed class TimelineProductJobStatus
{
    public string ProductId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Type { get; set; } = "";
    public string JobId { get; set; } = "";
    public string State { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public TimelineProductJobProgress Progress { get; set; } = new();
    public string StartedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string CompletedAt { get; set; } = "";
    public string Error { get; set; } = "";
    public List<string> Warnings { get; set; } = [];
}

public sealed class TimelineProductJobProgress
{
    public double Percent { get; set; }
    public int Current { get; set; }
    public int Total { get; set; }
    public string Unit { get; set; } = "";
    public string CurrentItem { get; set; } = "";
    public double? EstimatedRemainingSeconds { get; set; }
}

public sealed class TimelineDockerWorkerStatus
{
    public bool Available { get; set; }
    public string Worker { get; set; } = "";
    public string State { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string WorkDirectory { get; set; } = "";
    public string StoreDirectory { get; set; } = "";
    public bool StoreAvailable { get; set; }
    public string RebuildId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public int ItemCount { get; set; }
    public int EventCount { get; set; }
    public string Message { get; set; } = "";
}

public sealed class TimelineWorkerRepairResult
{
    public bool Ok { get; set; }
    public int ExitCode { get; set; }
    public string Message { get; set; } = "";
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public TimelineDockerWorkerStatus Worker { get; set; } = new();
}

public sealed class TimelineEventListResult
{
    public bool Available { get; set; }
    public int Total { get; set; }
    public TimelinePagination Pagination { get; set; } = new();
    public List<TimelineEventRow> Events { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class TimelineEventRow
{
    public string EventId { get; set; } = "";
    public string Product { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string EventType { get; set; } = "";
    public int Sequence { get; set; }
    public string OccurredAt { get; set; } = "";
    public string EndedAt { get; set; } = "";
    public double? RelativeStartSec { get; set; }
    public double? RelativeEndSec { get; set; }
    public string TimeBasis { get; set; } = "";
    public string ActorType { get; set; } = "";
    public string ActorLabel { get; set; } = "";
    public string ContentKind { get; set; } = "";
    public string ContentValue { get; set; } = "";
}

public sealed class TimelineLlmInputPreviewResult
{
    public bool Available { get; set; }
    public string PackId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public TimelineLlmTargetPeriod TargetPeriod { get; set; } = new();
    public TimelineLlmInputPolicy InputPolicy { get; set; } = new();
    public List<TimelineLlmInputItem> Items { get; set; } = [];
    public int Total { get; set; }
    public TimelinePagination Pagination { get; set; } = new();
    public TimelineLlmInputStats Stats { get; set; } = new();
    public List<string> Assumptions { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class TimelineLlmTargetPeriod
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class TimelineLlmInputPolicy
{
    public bool TextOnly { get; set; } = true;
    public bool ExcludeHardToReadIntermediateData { get; set; } = true;
    public string SecurityRedaction { get; set; } = "minimal";
}

public sealed class TimelineLlmInputStats
{
    public bool Partial { get; set; }
    public int ScanLimit { get; set; }
    public int ScannedEvents { get; set; }
    public int IncludedItems { get; set; }
    public int TotalReadableItems { get; set; }
    public int SkippedHardToRead { get; set; }
    public int SkippedAudioNotVerbalized { get; set; }
    public int SkippedEmptyOrPlaceholder { get; set; }
}

public sealed class TimelineLlmInputItem
{
    public string Id { get; set; } = "";
    public string SourceProduct { get; set; } = "";
    public string SourceProductName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string OccurredAt { get; set; } = "";
    public TimelineLlmTargetPeriod TimeRange { get; set; } = new();
    public TimelineLlmInputActor Actor { get; set; } = new();
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string ContentKind { get; set; } = "";
    public List<string> Notes { get; set; } = [];
    public List<string> SourceEventIds { get; set; } = [];
    public List<string> RawRefs { get; set; } = [];
    public TimelineLlmInputCreatedBy CreatedBy { get; set; } = new();
}

public sealed class TimelineLlmInputActor
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class TimelineLlmInputCreatedBy
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class BrowserSaveHandle
{
    public string Id { get; set; } = "";
    public bool Accepted { get; set; }
    public string Message { get; set; } = "";
}
