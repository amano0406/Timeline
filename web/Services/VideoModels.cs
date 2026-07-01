using System.Text.Json.Serialization;

namespace Timeline.Web.Services;

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
    public string ComputeMode { get; set; } = "";
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; set; }
    public string ComputeMode { get; set; } = "";
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
    public VideoArtifacts Artifacts { get; set; } = new();
    public VideoActivitySummary Activity { get; set; } = new();
    public List<VideoFrameObservation> Frames { get; set; } = [];
    public List<AudioTimelineTurn> Turns { get; set; } = [];
    public AudioVerbalizationStatus AudioVerbalization { get; set; } = new();
    public AudioVerbalizationResult AudioVerbalizationResult { get; set; } = new();
}

public sealed class VideoArtifacts
{
    public string ContactSheetPath { get; set; } = "";
    public bool HasContactSheet { get; set; }
    public string AudioArtifactPath { get; set; } = "";
    public bool HasAudioArtifact { get; set; }
    public string FramesDirectory { get; set; } = "";
}

public sealed class VideoActivitySummary
{
    public bool Available { get; set; }
    public string Strategy { get; set; } = "";
    public string ActivityMapPath { get; set; } = "";
    public int ActiveSegments { get; set; }
    public int InactiveSegments { get; set; }
    public double ActiveSec { get; set; }
    public double InactiveSec { get; set; }
    public double? ActiveRatio { get; set; }
    public double? EstimatedReductionRatio { get; set; }
    public int VisualSentinels { get; set; }
}

public sealed class VideoFrameObservation
{
    public string FrameId { get; set; } = "";
    public double TimeSec { get; set; }
    public string ArtifactPath { get; set; } = "";
    public bool HasArtifact { get; set; }
    public string OcrOverlayPath { get; set; } = "";
    public bool HasOcrOverlay { get; set; }
    public VideoFrameOcrInfo Ocr { get; set; } = new();
    public VideoFrameVisualInfo Visual { get; set; } = new();
}

public sealed class VideoFrameOcrInfo
{
    public bool HasText { get; set; }
    public int BlockCount { get; set; }
}

public sealed class VideoFrameVisualInfo
{
    public bool Available { get; set; }
    public double? Brightness { get; set; }
    public double? Contrast { get; set; }
    public string BrightnessLevel { get; set; } = "";
    public string ContrastLevel { get; set; } = "";
    public List<VideoColorPaletteEntry> ColorPalette { get; set; } = [];
    public List<VideoGridCell> Grid { get; set; } = [];
}

public sealed class VideoColorPaletteEntry
{
    public string Hex { get; set; } = "";
    public List<int> Rgb { get; set; } = [];
    public double? Ratio { get; set; }
}

public sealed class VideoGridCell
{
    public string CellId { get; set; } = "";
    public int Row { get; set; }
    public int Col { get; set; }
    public List<double> BboxNorm { get; set; } = [];
    public VideoColorInfo AverageColor { get; set; } = new();
}

public sealed class VideoColorInfo
{
    public string Hex { get; set; } = "";
    public List<int> Rgb { get; set; } = [];
}
