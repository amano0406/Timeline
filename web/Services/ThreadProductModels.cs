namespace Timeline.Web.Services;

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
    public string Message { get; set; } = "";
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
