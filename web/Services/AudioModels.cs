using System.Text.Json.Serialization;

namespace Timeline.Web.Services;

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
    public string Text { get; set; } = "";
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
