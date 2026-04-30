using System.Text.Json.Serialization;

namespace Timeline.Web.Services;

public sealed class RootRow
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class TimelineProductOverview
{
    public bool ProductFound { get; set; }
    public string ProductPath { get; set; } = "";
    public bool HasToken { get; set; }
    public string TokenPreview { get; set; } = "";
    public string ComputeMode { get; set; } = "cpu";
    public List<RootRow> InputRoots { get; set; } = [];
    public RootRow? OutputRoot { get; set; }
    public int AudioFileCount { get; set; }
    public string WorkerState { get; set; } = "未確認";
    public bool RestartRequired { get; set; }
    public string Message { get; set; } = "";
}

public sealed class AudioFileListResult
{
    public int Total { get; set; }
    public bool Truncated { get; set; }
    public List<AudioFileRow> Files { get; set; } = [];
}

public sealed class AudioFileRow
{
    public string SourceId { get; set; } = "";
    public string SourceDisplayName { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ModifiedAt { get; set; } = "";
    public string Status { get; set; } = "";
}

public sealed class AudioSettingsSaveRequest
{
    public string? Token { get; set; }
    public string ComputeMode { get; set; } = "cpu";
    public List<RootRow> InputRoots { get; set; } = [];
    public RootRow? OutputRoot { get; set; }
    public string OutputPath { get; set; } = "";
}

public sealed class HelperHealth
{
    public bool Ok { get; set; }
}
