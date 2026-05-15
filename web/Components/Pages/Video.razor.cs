using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Video
{
    private sealed class FileTreeDirectory
    {
        private readonly Dictionary<string, FileTreeDirectory> _directories = new(StringComparer.OrdinalIgnoreCase);

        public FileTreeDirectory(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public IReadOnlyCollection<FileTreeDirectory> Directories => _directories.Values;
        public List<VideoFileRow> Files { get; } = [];

        public FileTreeDirectory GetOrAddDirectory(string name)
        {
            if (!_directories.TryGetValue(name, out var directory))
            {
                directory = new FileTreeDirectory(name);
                _directories.Add(name, directory);
            }

            return directory;
        }
    }

    private sealed record FileTreeRow(string Name, int Depth, VideoFileRow? File);

    private const int PageSize = 25;
    private VideoOverview? _overview;
    private VideoFileListResult? _files;
    private bool _loading = true;
    private bool _loadingPage;
    private bool _overviewLoading;
    private DateTime? _lastLoadedAt;
    private int _currentPage = 1;
    private string? _error;

    private IReadOnlyList<VideoFileRow> Files => _files?.Files ?? [];
    private bool Busy => _loading || _loadingPage;
    private bool ListBusy => _loading || _loadingPage;
    private int FileCount => _files?.Total > 0 ? _files.Total : _overview?.SourceFileCount ?? Files.Count;
    private int ListTotalItems => _files?.Pagination.TotalItems > 0 ? _files.Pagination.TotalItems : FileCount;
    private int ImportedFileCount => Math.Max(_overview?.ItemCount ?? 0, Files.Count(file => file.HasTimeline));
    private string ImportedFileCountLabel => _overviewLoading && _overview is null
        ? "確認中"
        : $"{ImportedFileCount:N0} / {FileCount:N0}";
    private int VerbalizationTargetFileCount => Math.Max(_overview?.AudioVerbalizationTargetFileCount ?? 0, Files.Count(file => file.HasTimeline && file.TurnCount > 0));
    private int FullyVerbalizedFileCount => Math.Max(_overview?.AudioVerbalizedFileCount ?? 0, Files.Count(IsFullyVerbalized));
    private string VerbalizedFileCountLabel => _overviewLoading && _overview is null
        ? "確認中"
        : $"{FullyVerbalizedFileCount:N0} / {VerbalizationTargetFileCount:N0}";
    private int UnprocessedCount => Files.Count(file => !file.Status.Equals("completed", StringComparison.OrdinalIgnoreCase));
    private int KnownDurationCount => Files.Count(file => file.DurationSec is > 0);
    private double TotalDurationSec => Files.Where(file => file.DurationSec is > 0).Sum(file => file.DurationSec!.Value);
    private string TotalDurationLabel => TotalDurationSec > 0 ? UiFormat.Duration(TotalDurationSec) : "未取得";
    private bool HasPartialDuration => Files.Count > 0 && KnownDurationCount > 0 && KnownDurationCount < Files.Count;
    private string ProcessingLabel => _overview?.ItemCount > 0 ? "取得済み" : "未作成";
    private string ProcessingIcon => _overview?.ItemCount > 0 ? "circle-check" : "circle-minus";
    private string ProcessingIconClass => _overview?.ItemCount > 0 ? "text-teal-700" : "text-slate-500";
    private string FileListStatusLabel => ListBusy
        ? "読み込み中"
        : _lastLoadedAt is null
            ? ""
            : $"最終更新 {_lastLoadedAt.Value:HH:mm:ss}";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await LoadAsync();
        }
    }

    private async Task LoadAsync(bool forceRefresh = false)
    {
        _loading = true;
        _overviewLoading = false;
        _error = null;
        try
        {
            _currentPage = 1;
            await InvokeAsync(StateHasChanged);
            await LoadPageAsync(_currentPage, forceRefresh);
            _ = LoadOverviewLaterAsync(forceRefresh);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _overviewLoading = false;
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadOverviewLaterAsync(bool forceRefresh = false)
    {
        _overviewLoading = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            _overview = await Timeline.GetVideoOverviewAsync(forceRefresh);
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
        }
        finally
        {
            _overviewLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ChangePageAsync(int page)
    {
        if (page == _currentPage)
        {
            return;
        }

        await LoadPageAsync(page);
    }

    private async Task LoadPageAsync(int page, bool forceRefresh = false)
    {
        _loadingPage = true;
        _error = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            _files = await Timeline.GetVideoFilesAsync(page, PageSize, forceRefresh);
            _currentPage = Math.Max(1, _files.Pagination.Page);
            _lastLoadedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loadingPage = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static List<FileTreeRow> BuildTreeRows(IEnumerable<VideoFileRow> files)
    {
        var virtualRoot = new FileTreeDirectory("");
        foreach (var file in files.OrderBy(FileSortPath, StringComparer.OrdinalIgnoreCase))
        {
            var directory = virtualRoot;
            foreach (var part in SplitPathParts(FileDirectoryPath(file)))
            {
                directory = directory.GetOrAddDirectory(part);
            }
            directory.Files.Add(file);
        }

        var rows = new List<FileTreeRow>();
        AddDirectoryRows(virtualRoot, 0, rows);
        return rows;
    }

    private static void AddDirectoryRows(FileTreeDirectory directory, int depth, List<FileTreeRow> rows)
    {
        foreach (var child in directory.Directories.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new FileTreeRow(child.Name, depth, null));
            AddDirectoryRows(child, depth + 1, rows);
        }

        foreach (var file in directory.Files.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new FileTreeRow(file.FileName, depth, file));
        }
    }

    private static string FileDirectoryPath(VideoFileRow file)
    {
        if (!string.IsNullOrWhiteSpace(file.DisplayPath))
        {
            var normalized = file.DisplayPath.Replace('/', '\\');
            var index = normalized.LastIndexOf('\\');
            return index > 0 ? normalized[..index] : "";
        }

        var root = (file.RootPath ?? "").Trim().TrimEnd('\\', '/');
        var directory = (file.Directory ?? "").Trim().Trim('\\', '/');
        return string.IsNullOrWhiteSpace(directory)
            ? root
            : string.IsNullOrWhiteSpace(root) ? directory : $"{root}\\{directory}";
    }

    private static string FileSortPath(VideoFileRow file) =>
        !string.IsNullOrWhiteSpace(file.DisplayPath)
            ? file.DisplayPath
            : $"{file.RootPath}\\{file.RelativePath}";

    private static IEnumerable<string> SplitPathParts(string path)
    {
        var normalized = (path ?? "").Trim().Replace('/', '\\').Trim('\\');
        return string.IsNullOrWhiteSpace(normalized)
            ? []
            : normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string TreeIndentStyle(int depth) =>
        $"--depth:{Math.Max(0, depth)}";

    private static string TreeRowClass(FileTreeRow row) =>
        row.File is null ? "tfa-file-tree-row tfa-file-tree-row-directory" : "tfa-file-tree-row tfa-file-tree-row-file";

    private static string StatusLabel(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "処理済み",
            "processing" => "処理中",
            "failed" => "失敗",
            _ => "未処理",
        };

    private static string StatusIcon(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "circle-check",
            "processing" => "spinner",
            "failed" => "triangle-exclamation",
            _ => "circle-minus",
        };

    private static string StatusPill(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "processing" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };

    private static string ArtifactLabel(VideoFileRow file)
    {
        if (!file.HasTimeline)
        {
            return "未作成";
        }

        var labels = new List<string>();
        if (file.FrameCount > 0)
        {
            labels.Add($"{file.FrameCount} フレーム");
        }
        if (file.TextBlockCount > 0)
        {
            labels.Add($"{file.TextBlockCount} テキスト");
        }
        if (file.SpeechCandidateCount > 0)
        {
            labels.Add($"{file.SpeechCandidateCount} 音声区間");
        }

        return labels.Count == 0 ? "作成済み" : string.Join(" / ", labels);
    }

    private static string DurationLabel(double? seconds) =>
        seconds is > 0 ? UiFormat.Duration(seconds.Value) : "-";

    private static bool IsFullyVerbalized(VideoFileRow file) =>
        file.HasTimeline
        && file.TurnCount > 0
        && file.AudioVerbalization.State.Equals("completed", StringComparison.OrdinalIgnoreCase);

    private static string VideoDetailUrl(VideoFileRow? file) =>
        file is null
            ? "video"
            : $"video/file-detail?path={Uri.EscapeDataString(file.SourcePath)}";

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;
}
