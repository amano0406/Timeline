using System.Text.Json.Serialization;

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
    public string DisplayLanguageId { get; set; } = "ja-JP";
    public List<TimelineDisplayLanguageOption> DisplayLanguages { get; set; } = [];
    public string TimeZoneId { get; set; } = "Asia/Tokyo";
    public List<TimelineTimeZoneOption> TimeZones { get; set; } = [];
    public string WorkDirectory { get; set; } = @"C:\TimelineData\Timeline\work";
    public string StoreDirectory { get; set; } = @"C:\TimelineData\Timeline\store";
    public TimelineCommonAiSettings CommonAi { get; set; } = new();
    public TimelineProductRegistry ProductRegistry { get; set; } = new();
    public TimelineAudioVerbalizationSettings AudioVerbalization { get; set; } = new();
}

public sealed class TimelineAppSettingsSaveRequest
{
    public string DisplayLanguageId { get; set; } = "ja-JP";
    public string TimeZoneId { get; set; } = "Asia/Tokyo";
    public string WorkDirectory { get; set; } = @"C:\TimelineData\Timeline\work";
    public string StoreDirectory { get; set; } = @"C:\TimelineData\Timeline\store";
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
    public string SourceType { get; set; } = "release";
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

public sealed class TimelineProductOverview
{
    public bool ProductFound { get; set; }
    public string ProductPath { get; set; } = "";
    public bool HasToken { get; set; }
    public string TokenPreview { get; set; } = "";
    public string ComputeMode { get; set; } = "cpu";
    public List<string> CpuDevices { get; set; } = [];
    public List<string> GpuDevices { get; set; } = [];
    public List<RootRow> InputRoots { get; set; } = [];
    public RootRow? OutputRoot { get; set; }
    public int AudioFileCount { get; set; }
    public int AudioItemCount { get; set; }
    public int AudioVerbalizationTargetFileCount { get; set; }
    public int AudioVerbalizedFileCount { get; set; }
    public string WorkerState { get; set; } = "未確認";
    public AudioRunProgress? ActiveRun { get; set; }
    public bool RestartRequired { get; set; }
    public string Message { get; set; } = "";
}

public sealed class AudioRunProgress
{
    public string RunId { get; set; } = "";
    public string State { get; set; } = "";
    public string CurrentStage { get; set; } = "";
    public string Message { get; set; } = "";
    public int ItemsTotal { get; set; }
    public int ItemsDone { get; set; }
    public int ItemsSkipped { get; set; }
    public int ItemsFailed { get; set; }
    public double ProgressPercent { get; set; }
    public double ProcessedDurationSec { get; set; }
    public double TotalDurationSec { get; set; }
    public double EstimatedRemainingSec { get; set; }
    public string CurrentItem { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class AudioModelInventoryResult
{
    public bool Available { get; set; }
    public string Message { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    public string PipelineName { get; set; } = "";
    public string PipelineVersion { get; set; } = "";
    public List<AudioModelRow> Models { get; set; } = [];
}

public sealed class AudioModelRow
{
    public string Role { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Source { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string Backend { get; set; } = "";
    public bool Required { get; set; }
    public bool Configured { get; set; }
    public bool RequiresHuggingFaceToken { get; set; }
    public bool RequiresAccessApproval { get; set; }
    public string UnitType { get; set; } = "";
    public string Url { get; set; } = "";
    public string License { get; set; } = "";
    public string Gated { get; set; } = "";
    public string RemoteStatus { get; set; } = "";
    public string RemoteMessage { get; set; } = "";
    public List<string> Notes { get; set; } = [];
}

public sealed class AudioFileListResult
{
    public int Total { get; set; }
    public bool Truncated { get; set; }
    public TimelinePagination Pagination { get; set; } = new();
    public List<AudioFileRow> Files { get; set; } = [];
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

public sealed class AudioFileRow
{
    public string ItemId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SourceFileIdentity { get; set; } = "";
    public string SourceDisplayName { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Directory { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ModifiedAt { get; set; } = "";
    public double? DurationSec { get; set; }
    public string Status { get; set; } = "";
    public bool HasTimeline { get; set; }
    public bool HasAudio { get; set; }
    public string RunId { get; set; } = "";
    public string MediaId { get; set; } = "";
    public int TurnCount { get; set; }
    public int SpeakerCount { get; set; }
    public AudioVerbalizationStatus AudioVerbalization { get; set; } = new();
}

public sealed class AudioFileDetailResult
{
    public bool Available { get; set; }
    public string Message { get; set; } = "";
    public AudioFileRow? File { get; set; }
    public bool TimelineAvailable { get; set; }
    public bool AudioAvailable { get; set; }
    public string AudioUrl { get; set; } = "";
    public string TimelinePath { get; set; } = "";
    public string ConvertInfoPath { get; set; } = "";
    public string PipelineVersion { get; set; } = "";
    public string UnitType { get; set; } = "";
    public List<AudioTimelineTurn> Turns { get; set; } = [];
    public AudioVerbalizationStatus AudioVerbalization { get; set; } = new();
}

public sealed class AudioTimelineTurn
{
    public int Index { get; set; }
    public double StartSec { get; set; }
    public double EndSec { get; set; }
    public string AbsoluteStartAt { get; set; } = "";
    public string AbsoluteEndAt { get; set; } = "";
    public string Speaker { get; set; } = "";
    public string PhoneTokens { get; set; } = "";
    public string UnitType { get; set; } = "";
    public double? Confidence { get; set; }
}

public sealed class AudioVerbalizationStartRequest
{
    public string SourceId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool Force { get; set; }
}

public sealed class AudioVerbalizationStatus
{
    public bool Available { get; set; }
    public string State { get; set; } = "not_started";
    public string AudioItemId { get; set; } = "";
    public string SourceFileIdentity { get; set; } = "";
    public string Language { get; set; } = "ja-JP";
    public string Model { get; set; } = "qwen3.5:9b";
    public string Signature { get; set; } = "";
    public string ExpectedSignature { get; set; } = "";
    public string SummarySignature { get; set; } = "";
    public string ExpectedSummarySignature { get; set; } = "";
    public string SignatureState { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public int TotalTurns { get; set; }
    public int VerbalizedTurns { get; set; }
    public int UnresolvedTurns { get; set; }
    public int TotalChunks { get; set; }
    public int CompletedChunks { get; set; }
    public string JobId { get; set; } = "";
    public string CurrentChunkId { get; set; } = "";
    public string PlanPath { get; set; } = "";
    public string ResultPath { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public double ElapsedSec { get; set; }
    public double EstimatedRemainingSec { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class AudioVerbalizationBulkStatus
{
    public bool Available { get; set; }
    public string State { get; set; } = "not_started";
    public string JobId { get; set; } = "";
    public int TotalItems { get; set; }
    public int CompletedItems { get; set; }
    public int ReviewItems { get; set; }
    public int FailedItems { get; set; }
    public int SkippedItems { get; set; }
    public int TotalTurns { get; set; }
    public int VerbalizedTurns { get; set; }
    public int UnresolvedTurns { get; set; }
    public int TotalChunks { get; set; }
    public int CompletedChunks { get; set; }
    public string CurrentAudioItemId { get; set; } = "";
    public string CurrentFileName { get; set; } = "";
    public string CurrentRelativePath { get; set; } = "";
    public string CurrentChunkId { get; set; } = "";
    public int CurrentItemCompletedChunks { get; set; }
    public int CurrentItemTotalChunks { get; set; }
    public string StartedAt { get; set; } = "";
    public string CompletedAt { get; set; } = "";
    public double ElapsedSec { get; set; }
    public double EstimatedRemainingSec { get; set; }
    public double ProgressPercent { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class AudioVerbalizationBulkTargetSummary
{
    public bool Available { get; set; }
    public int TargetCount { get; set; }
    public int FailedItems { get; set; }
    public int ChangedItems { get; set; }
    public int NotStartedItems { get; set; }
    public int UnknownItems { get; set; }
    public int ActiveOrStaleItems { get; set; }
    public Dictionary<string, int> ByState { get; set; } = [];
    public string UpdatedAt { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class AudioVerbalizationOllamaStatus
{
    public bool Available { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen3.5:9b";
    public bool ModelAvailable { get; set; }
    public List<string> ModelNames { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class AudioVerbalizationResult
{
    public bool Available { get; set; }
    public AudioVerbalizationStatus Status { get; set; } = new();
    public List<AudioVerbalizedTurn> Turns { get; set; } = [];
    public List<AudioVerbalizedChunk> Chunks { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class AudioVerbalizedTurn
{
    public string TurnId { get; set; } = "";
    public int Index { get; set; }
    public double StartSec { get; set; }
    public double EndSec { get; set; }
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public double? Confidence { get; set; }
    public string Status { get; set; } = "";
    public List<string> Basis { get; set; } = [];
    public List<string> UncertainTerms { get; set; } = [];
}

public sealed class AudioVerbalizedChunk
{
    public string ChunkId { get; set; } = "";
    public int Sequence { get; set; }
    public string State { get; set; } = "";
    public double StartSec { get; set; }
    public double EndSec { get; set; }
    public int TurnCount { get; set; }
    public string ContextPath { get; set; } = "";
    public string SummaryPath { get; set; } = "";
    public string ResultPath { get; set; } = "";
    public int RetryCount { get; set; }
    public string Error { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed class AudioDeleteGeneratedRequest
{
    public List<string> ItemIds { get; set; } = [];
    public List<string> SourceFileIdentities { get; set; } = [];
    public bool DryRun { get; set; }
}

public sealed class AudioDeleteGeneratedResult
{
    public bool DryRun { get; set; }
    public string OutputRootId { get; set; } = "";
    public string OutputRootPath { get; set; } = "";
    public List<string> RequestedItemIds { get; set; } = [];
    public List<string> RequestedSourceFileIdentities { get; set; } = [];
    public int MatchedCount { get; set; }
    public List<string> MissingItemIds { get; set; } = [];
    public List<string> MissingSourceFileIdentities { get; set; } = [];
    public int CatalogRowsRemoved { get; set; }
    public int MediaDirsRemoved { get; set; }
    public List<string> MediaDirs { get; set; } = [];
    public List<string> UnsafeMediaDirs { get; set; } = [];
}

public sealed class AudioRefreshRequest
{
    public bool QueueOnly { get; set; } = true;
    public bool ReprocessDuplicates { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxItems { get; set; }
}

public sealed class AudioRefreshResult
{
    public string State { get; set; } = "";
    public string RunId { get; set; } = "";
    public string RunDir { get; set; } = "";
    public bool QueueOnly { get; set; }
    public int TotalDiscovered { get; set; }
    public int SelectedCount { get; set; }
    public int QueuedCount { get; set; }
    public int SkippedCount { get; set; }
    public int DeferredCount { get; set; }
    public int? QueuedLimit { get; set; }
}

public sealed class AudioDownloadItemsRequest
{
    public List<string> ItemIds { get; set; } = [];
    public string OutputPath { get; set; } = "";
}

public sealed class AudioDownloadItemsResult
{
    public string ArchivePath { get; set; } = "";
    public List<string> ItemIds { get; set; } = [];
}

public sealed class AudioSettingsSaveRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; set; }
    public string ComputeMode { get; set; } = "cpu";
    public List<RootRow> InputRoots { get; set; } = [];
    public RootRow? OutputRoot { get; set; }
    public string OutputPath { get; set; } = "";
}

public sealed class ProductRuntimeOverview
{
    public List<ProductRuntimeRow> Products { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class ProductRuntimeRow
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string PagePath { get; set; } = "";
    public string SettingsPath { get; set; } = "";
    public string ProductPath { get; set; } = "";
    public string Path { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool ProductFound { get; set; }
    public bool ComposeFound { get; set; }
    public bool StartFound { get; set; }
    public bool StopFound { get; set; }
    public string ContainerName { get; set; } = "";
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Running { get; set; }
    public string StartedAt { get; set; } = "";
    public int ExitCode { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ProductUninstallRequest
{
    public bool KeepSettings { get; set; } = true;
    public bool RemoveGeneratedData { get; set; }
}

public sealed class ProductUninstallPlan
{
    public string ProductId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ProductPath { get; set; } = "";
    public bool KeepSettings { get; set; } = true;
    public bool RemoveGeneratedData { get; set; }
    public long TotalDeleteBytes { get; set; }
    public ProductUninstallPathPlan AppDirectory { get; set; } = new();
    public ProductUninstallSettingsPlan Settings { get; set; } = new();
    public List<ProductUninstallPathPlan> GeneratedData { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class ProductUninstallPathPlan
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public long SizeBytes { get; set; }
    public bool WillDelete { get; set; }
}

public sealed class ProductUninstallSettingsPlan
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public long SizeBytes { get; set; }
    public bool WillBackup { get; set; }
    public string BackupPath { get; set; } = "";
    public bool WillDeleteBackup { get; set; }
}

public sealed class TimelineExportDownloadResult
{
    public string ArchivePath { get; set; } = "";
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
    public TimelineRebuildResult? Result { get; set; }
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

public sealed class WindowsCodexOverview
{
    public bool ProductFound { get; set; }
    public string ProductPath { get; set; } = "";
    public bool SettingsValid { get; set; }
    public WindowsCodexSettings Settings { get; set; } = new();
    public WindowsCodexCurrent Current { get; set; } = new();
    public List<TimelineThreadRow> Threads { get; set; } = [];
    public List<WindowsCodexJobRow> Jobs { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class WindowsCodexSettings
{
    public string SettingsPath { get; set; } = "";
    public List<WindowsCodexSourceRoot> SourceRoots { get; set; } = [];
    public string OutputsRoot { get; set; } = "";
    public string OutputsRootDisplayPath { get; set; } = "";
    public bool OutputsRootReady { get; set; }
    public string RedactionProfile { get; set; } = "";
    public bool? IncludeArchivedSources { get; set; }
    public bool? IncludeToolOutputs { get; set; }
    public bool UsingDefaultSourceRoots { get; set; }
    public List<string> Issues { get; set; } = [];
}

public sealed class WindowsCodexSettingsSaveRequest
{
    public List<string> SourceRoots { get; set; } = [];
    public string OutputsRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";
}

public sealed class WindowsCodexDownloadItemsRequest
{
    public List<string> ItemIds { get; set; } = [];
    public string OutputPath { get; set; } = "";
}

public sealed class WindowsCodexDownloadItemsResult
{
    public string ArchivePath { get; set; } = "";
    public List<string> ItemIds { get; set; } = [];
}

public sealed class TimelineThreadItemsRequest
{
    public List<string> ItemIds { get; set; } = [];
    public string OutputPath { get; set; } = "";
}

public sealed class TimelineThreadItemsDownloadResult
{
    public string ArchivePath { get; set; } = "";
    public List<string> ItemIds { get; set; } = [];
}

public sealed class TimelineThreadItemsDeleteResult
{
    public List<string> ItemIds { get; set; } = [];
    public int DeletedCount { get; set; }
    public List<string> MissingItemIds { get; set; } = [];
}

public sealed class TimelineThreadListResult
{
    public int Total { get; set; }
    public TimelinePagination Pagination { get; set; } = new();
    public List<TimelineThreadRow> Threads { get; set; } = [];
}

public sealed class WindowsCodexSourceRoot
{
    public string Path { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Exists { get; set; }
    public bool Readable { get; set; }
}

public sealed class WindowsCodexCurrent
{
    public bool Available { get; set; }
    public string State { get; set; } = "";
    public string RunId { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string RunDirectory { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public bool ArchiveExists { get; set; }
    public long ArchiveSizeBytes { get; set; }
    public string CatalogPath { get; set; } = "";
    public string ProcessingMode { get; set; } = "";
    public int ThreadCount { get; set; }
    public int EventCount { get; set; }
    public int ReusedThreadCount { get; set; }
    public int RenderedThreadCount { get; set; }
    public int FidelityWarningCount { get; set; }
    public WindowsCodexUpdateCounts UpdateCounts { get; set; } = new();
    public string Message { get; set; } = "";
}

public sealed class WindowsCodexUpdateCounts
{
    public int New { get; set; }
    public int Changed { get; set; }
    public int Unchanged { get; set; }
    public int Missing { get; set; }
    public int Degraded { get; set; }
}

public sealed class WindowsCodexJobRow
{
    public string RunId { get; set; } = "";
    public string State { get; set; } = "";
    public string CurrentStage { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int ThreadCount { get; set; }
    public int ThreadsDone { get; set; }
    public string ArchivePath { get; set; } = "";
}

public sealed class ChatGptOverview
{
    public bool ProductFound { get; set; }
    public string ProductPath { get; set; } = "";
    public bool SettingsFound { get; set; }
    public string SettingsPath { get; set; } = "";
    public bool SettingsValid { get; set; }
    public List<ChatGptInputRoot> InputRoots { get; set; } = [];
    public ChatGptDirectoryRoot MasterRoot { get; set; } = new();
    public ChatGptDirectoryRoot OutputRoot { get; set; } = new();
    public ChatGptDirectoryRoot StateRoot { get; set; } = new();
    public bool Recursive { get; set; }
    public string Profile { get; set; } = "";
    public int ProcessableInputCount { get; set; }
    public int ItemCount { get; set; }
    public ChatGptRefreshSummary LatestRefresh { get; set; } = new();
    public List<TimelineThreadRow> Threads { get; set; } = [];
    public List<ChatGptJobRow> Jobs { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class TimelineThreadRow
{
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int MessageCount { get; set; }
    public string DirectoryPath { get; set; } = "";
    public string TimelinePath { get; set; } = "";
    public string ConvertInfoPath { get; set; } = "";
}

public sealed class WindowsCodexThreadDetail
{
    public bool Available { get; set; }
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int MessageCount { get; set; }
    public List<TimelineThreadMessage> Messages { get; set; } = [];
    public string DirectoryPath { get; set; } = "";
    public string TimelinePath { get; set; } = "";
    public string ConvertInfoPath { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ChatGptThreadDetail
{
    public bool Available { get; set; }
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int MessageCount { get; set; }
    public List<TimelineThreadMessage> Messages { get; set; } = [];
    public string DirectoryPath { get; set; } = "";
    public string TimelinePath { get; set; } = "";
    public string ConvertInfoPath { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class TimelineThreadMessage
{
    public int Index { get; set; }
    public string Role { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class ChatGptInputRoot
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Exists { get; set; }
    public int FileCount { get; set; }
    public long SizeBytes { get; set; }
}

public sealed class ChatGptDirectoryRoot
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public bool Exists { get; set; }
}

public sealed class ChatGptSettingsSaveRequest
{
    public List<ChatGptInputRoot> InputRoots { get; set; } = [];
    public ChatGptDirectoryRoot MasterRoot { get; set; } = new();
    public string MasterRootPath { get; set; } = "";
    public ChatGptDirectoryRoot OutputRoot { get; set; } = new();
    public string OutputRootPath { get; set; } = "";
    public ChatGptDirectoryRoot StateRoot { get; set; } = new();
    public bool Recursive { get; set; }
    public string Profile { get; set; } = "timeline-default";
}

public sealed class ChatGptRefreshRequest
{
    public string FilePath { get; set; } = "";
    public string DownloadTo { get; set; } = "";
    public bool Overwrite { get; set; }
}

public sealed class ChatGptRefreshSummary
{
    public bool Available { get; set; }
    public string StartedAt { get; set; } = "";
    public string CompletedAt { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public int Discovered { get; set; }
    public int Processed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Missing { get; set; }
    public int Duplicates { get; set; }
    public double DurationSeconds { get; set; }
}

public sealed class ChatGptJobRow
{
    public string JobId { get; set; } = "";
    public string State { get; set; } = "";
    public string CurrentStage { get; set; } = "";
    public string Message { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string CompletedAt { get; set; } = "";
    public int ConversationsTotal { get; set; }
    public int ConversationsDone { get; set; }
    public double ProgressPercent { get; set; }
    public int ProcessedCount { get; set; }
    public int ErrorCount { get; set; }
    public int BatchCount { get; set; }
    public string InputPath { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public long ArchiveSizeBytes { get; set; }
    public string RunDirectory { get; set; } = "";
    public string CurrentConversation { get; set; } = "";
}

public sealed class ImageOverview
{
    public bool ProductFound { get; set; }
    public string ProductPath { get; set; } = "";
    public bool SettingsValid { get; set; }
    public ImageSettings Settings { get; set; } = new();
    public int SourceFileCount { get; set; }
    public int ItemCount { get; set; }
    public ImageRefreshResult LatestRefresh { get; set; } = new();
    public string Message { get; set; } = "";
}

public sealed class ImageSettings
{
    public string SettingsPath { get; set; } = "";
    public List<ImageInputRoot> InputRoots { get; set; } = [];
    public ImageDirectoryRoot OutputRoot { get; set; } = new();
    public List<string> Issues { get; set; } = [];
}

public sealed class ImageInputRoot
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Exists { get; set; }
}

public sealed class ImageDirectoryRoot
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public bool Exists { get; set; }
}

public sealed class ImageItemListResult
{
    public int Total { get; set; }
    public TimelinePagination Pagination { get; set; } = new();
    public List<ImageItemRow> Items { get; set; } = [];
}

public sealed class ImageFileListResult
{
    public int Total { get; set; }
    public int ProcessedTotal { get; set; }
    public TimelinePagination Pagination { get; set; } = new();
    public List<ImageItemRow> Files { get; set; } = [];
}

public sealed class ImageItemRow
{
    public string ItemId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceDisplayName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ModifiedAt { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string TimelinePath { get; set; } = "";
    public string ConvertInfoPath { get; set; } = "";
    public string ImageRecordPath { get; set; } = "";
    public bool HasTimeline { get; set; }
    public bool HasImageRecord { get; set; }
}

public sealed class ImageFileDetailResult
{
    public bool Available { get; set; }
    public string Message { get; set; } = "";
    public ImageItemRow? File { get; set; }
    public bool ImageAvailable { get; set; }
    public bool ImageRecordAvailable { get; set; }
    public bool TimelineAvailable { get; set; }
    public string ImageRecordPath { get; set; } = "";
    public string TimelinePath { get; set; } = "";
    public string ConvertInfoPath { get; set; } = "";
    public ImageRecordSummary Record { get; set; } = new();
    public List<ImageTextBlock> TextBlocks { get; set; } = [];
}

public sealed class ImageRecordSummary
{
    public string TimelineAt { get; set; } = "";
    public string CapturedAt { get; set; } = "";
    public string ModifiedAt { get; set; } = "";
    public string FormatName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Orientation { get; set; } = "";
    public string CameraMake { get; set; } = "";
    public string CameraModel { get; set; } = "";
    public string ImageKind { get; set; } = "";
    public List<string> ContentTypes { get; set; } = [];
    public bool HasText { get; set; }
    public string FullText { get; set; } = "";
    public int OcrBlockCount { get; set; }
    public string BrightnessLevel { get; set; } = "";
    public string ContrastLevel { get; set; } = "";
    public double? Brightness { get; set; }
    public double? Contrast { get; set; }
    public bool NeedsReview { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class ImageTextBlock
{
    public int Index { get; set; }
    public string BlockId { get; set; } = "";
    public string Text { get; set; } = "";
    public string NormalizedText { get; set; } = "";
    public string Role { get; set; } = "";
    public double? ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "";
}

public sealed class ImageRefreshRequest
{
    public bool ReprocessDuplicates { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxItems { get; set; }
}

public sealed class ImageRefreshResult
{
    public string RunId { get; set; } = "";
    public string State { get; set; } = "";
    public int SourceCount { get; set; }
    public int ProcessedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public string ArchivePath { get; set; } = "";
}

public sealed class ImageItemsRequest
{
    public List<string> ItemIds { get; set; } = [];
    public bool DryRun { get; set; }
}

public sealed class ImageItemsDownloadResult
{
    public string ArchivePath { get; set; } = "";
    public List<string> ItemIds { get; set; } = [];
}

public sealed class ImageSettingsSaveRequest
{
    public List<ImageInputRoot> InputRoots { get; set; } = [];
    public ImageDirectoryRoot OutputRoot { get; set; } = new();
    public string OutputRootPath { get; set; } = "";
}

public sealed class VideoOverview
{
    public bool ProductFound { get; set; }
    public string ProductPath { get; set; } = "";
    public bool SettingsValid { get; set; }
    public VideoSettings Settings { get; set; } = new();
    public int SourceFileCount { get; set; }
    public int ItemCount { get; set; }
    public int AudioVerbalizationTargetFileCount { get; set; }
    public int AudioVerbalizedFileCount { get; set; }
    public List<string> CpuDevices { get; set; } = [];
    public List<string> GpuDevices { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class VideoSettings
{
    public string SettingsPath { get; set; } = "";
    public List<VideoInputRoot> InputRoots { get; set; } = [];
    public VideoDirectoryRoot OutputRoot { get; set; } = new();
    public string ComputeMode { get; set; } = "gpu";
    public bool HasToken { get; set; }
    public string TokenPreview { get; set; } = "";
    public List<string> Issues { get; set; } = [];
}

public sealed class VideoInputRoot
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Exists { get; set; }
}

public sealed class VideoDirectoryRoot
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public bool Exists { get; set; }
}

public sealed class VideoSettingsSaveRequest
{
    public List<VideoInputRoot> InputRoots { get; set; } = [];
    public VideoDirectoryRoot OutputRoot { get; set; } = new();
    public string OutputRootPath { get; set; } = "";
    public string Token { get; set; } = "";
    public string ComputeMode { get; set; } = "gpu";
}

public sealed class VideoFileListResult
{
    public int Total { get; set; }
    public TimelinePagination Pagination { get; set; } = new();
    public List<VideoFileRow> Files { get; set; } = [];
}

public sealed class VideoFileRow
{
    public string ItemId { get; set; } = "";
    public string SourceFileIdentity { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Directory { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ModifiedAt { get; set; } = "";
    public double? DurationSec { get; set; }
    public string Status { get; set; } = "";
    public bool HasTimeline { get; set; }
    public int FrameCount { get; set; }
    public int TextBlockCount { get; set; }
    public int SpeechCandidateCount { get; set; }
    public int TurnCount { get; set; }
    public AudioVerbalizationStatus AudioVerbalization { get; set; } = new();
}

public sealed class VideoFileDetailResult
{
    public bool Available { get; set; }
    public string Message { get; set; } = "";
    public VideoFileRow? File { get; set; }
    public bool VideoAvailable { get; set; }
    public bool TimelineAvailable { get; set; }
    public List<AudioTimelineTurn> Turns { get; set; } = [];
    public AudioVerbalizationStatus AudioVerbalization { get; set; } = new();
    public AudioVerbalizationResult AudioVerbalizationResult { get; set; } = new();
}

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

public sealed class HelperHealth
{
    public bool Ok { get; set; }
}
