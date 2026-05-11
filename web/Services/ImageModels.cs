using System.Text.Json.Serialization;

namespace Timeline.Web.Services;

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
