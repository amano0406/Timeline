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
