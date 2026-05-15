using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class AudioFiles
{
    private const int FilePageSize = 25;

    private TimelineProductOverview? _overview;
    private AudioFileListResult? _files;
    private readonly HashSet<string> _selectedFileKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading = true;
    private bool _refreshing;
    private bool _downloading;
    private bool _deleting;
    private bool _pollingOverview;
    private bool _loadingMoreFiles;
    private bool _deleteModalOpen;
    private bool _runDetailsOpen;
    private bool _disposed;
    private int _currentFilePage = 1;
    private DateTime? _lastLoadedAt;
    private CancellationTokenSource? _pollingCts;
    private string? _error;
    private string? _operationMessage;

    private IReadOnlyList<AudioFileRow> Files => _files?.Files ?? [];
    private int FileCount => _files?.Total ?? Files.Count;
    private int LoadedFileCount => Files.Count;
    private bool ListBusy => _loading || _loadingMoreFiles;
    private int FileTotalItems => _files?.Pagination.TotalItems > 0
        ? _files.Pagination.TotalItems
        : FileCount;
    private string FileListStatusLabel => ListBusy
        ? "読み込み中"
        : _lastLoadedAt is null
            ? ""
            : $"最終更新 {_lastLoadedAt.Value:HH:mm:ss}";
    private int ImportedFileCount => Math.Max(_overview?.AudioItemCount ?? 0, Files.Count(file => file.HasTimeline));
    private string ImportedFileCountLabel => $"{ImportedFileCount:N0} / {Math.Max(FileCount, _overview?.AudioFileCount ?? 0):N0}";
    private int VerbalizationTargetFileCount => Math.Max(_overview?.AudioVerbalizationTargetFileCount ?? 0, Files.Count(file => file.HasTimeline && file.TurnCount > 0));
    private int FullyVerbalizedFileCount => Math.Max(_overview?.AudioVerbalizedFileCount ?? 0, Files.Count(IsFullyVerbalized));
    private string VerbalizedFileCountLabel => $"{FullyVerbalizedFileCount:N0} / {VerbalizationTargetFileCount:N0}";
    private int UnprocessedCount => Files.Count(file => !file.Status.Equals("completed", StringComparison.OrdinalIgnoreCase));
    private int KnownDurationCount => Files.Count(file => file.DurationSec is > 0);
    private double TotalDurationSec => Files.Where(file => file.DurationSec is > 0).Sum(file => file.DurationSec!.Value);
    private string TotalDurationLabel => TotalDurationSec > 0 ? UiFormat.Duration(TotalDurationSec) : "未取得";
    private bool HasPartialDuration => Files.Count > 0 && KnownDurationCount > 0 && KnownDurationCount < Files.Count;
    private int SelectedFileCount => _selectedFileKeys.Count;
    private bool HasSelection => SelectedFileCount > 0;
    private int SelectedGeneratedItemCount => SelectedGeneratedItemIds().Count;
    private bool HasGeneratedItemSelection => SelectedGeneratedItemCount > 0;
    private bool AllVisibleSelected => Files.Count > 0 && Files.All(file => _selectedFileKeys.Contains(FileKey(file)));
    private string SelectionSummary =>
        HasSelection
            ? $"{SelectedFileCount} 件選択中 / 操作可能な生成物 {SelectedGeneratedItemCount} 件"
            : "未選択";
    private string WorkerState => _overview?.WorkerState ?? "checking";
    private AudioRunProgress? ActiveRun => IsActiveRun(_overview?.ActiveRun) ? _overview?.ActiveRun : null;
    private bool WorkerWaiting =>
        WorkerState.Equals("checking", StringComparison.OrdinalIgnoreCase)
        || WorkerState.Equals("starting", StringComparison.OrdinalIgnoreCase)
        || WorkerState.Equals("processing", StringComparison.OrdinalIgnoreCase);

    private string WorkerStateLabel => WorkerState.ToLowerInvariant() switch
    {
        "processing" => "分析中",
        "running" => "待機中",
        "checking" => "確認中",
        "starting" => "起動待ち",
        "stopped" => "停止中",
        _ => "未確認",
    };

    private string WorkerStateIcon => WorkerState.ToLowerInvariant() switch
    {
        "processing" => "spinner",
        "running" => "microchip",
        "checking" => "spinner",
        "starting" => "spinner",
        _ => "circle-minus",
    };

    private string WorkerStateIconClass => WorkerState.ToLowerInvariant() switch
    {
        "processing" => "text-sky-800",
        "running" => "text-teal-700",
        "checking" => "text-sky-800",
        "starting" => "text-sky-800",
        _ => "text-slate-500",
    };
    protected override void OnInitialized()
    {
        _pollingCts = new CancellationTokenSource();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _pollingCts is null || _disposed)
        {
            return;
        }

        await LoadAsync();
        if (_disposed || _pollingCts is null)
        {
            return;
        }
        _ = PollActiveRunAsync(_pollingCts.Token);
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            _overview = await Timeline.GetAudioOverviewAsync();
            _files = new AudioFileListResult();
            _currentFilePage = 1;
            await InvokeAsync(StateHasChanged);
            await LoadFilePageAsync(1, reset: true);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadFilePageAsync(int page, bool reset)
    {
        var result = await Timeline.GetAudioFilesAsync(page, FilePageSize);
        _files = result;

        _lastLoadedAt = DateTime.Now;
        _currentFilePage = Math.Max(1, result.Pagination.Page);
        RemoveMissingSelections();
        await InvokeAsync(StateHasChanged);
    }

    private async Task ChangeFilePageAsync(int page)
    {
        if (page == _currentFilePage || _loadingMoreFiles)
        {
            return;
        }

        _loadingMoreFiles = true;
        _error = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            await LoadFilePageAsync(page, reset: true);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loadingMoreFiles = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task PollActiveRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (ShouldPollRunProgress)
                {
                    await InvokeAsync(RefreshOverviewForPollingAsync);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool ShouldPollRunProgress =>
        IsActiveRun(_overview?.ActiveRun)
        || WorkerState.Equals("processing", StringComparison.OrdinalIgnoreCase)
        || WorkerState.Equals("starting", StringComparison.OrdinalIgnoreCase);

    private async Task RefreshOverviewForPollingAsync()
    {
        if (_pollingOverview || _loading || _refreshing || _deleting || _downloading)
        {
            return;
        }

        _pollingOverview = true;
        var hadActiveRun = IsActiveRun(_overview?.ActiveRun);
        try
        {
            _overview = await Timeline.GetAudioOverviewAsync();
            if (hadActiveRun && !IsActiveRun(_overview.ActiveRun))
            {
                await LoadFilePageAsync(_currentFilePage, reset: true);
            }
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
        }
        finally
        {
            _pollingOverview = false;
            StateHasChanged();
        }
    }

    private async Task StartAudioRefreshAsync()
    {
        _refreshing = true;
        _error = null;
        _operationMessage = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            var result = await Timeline.RefreshAudioAsync(new AudioRefreshRequest
            {
                QueueOnly = true,
            });
            _operationMessage = RefreshMessage(result);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ToggleRunDetails() => _runDetailsOpen = !_runDetailsOpen;

    private static string DetailHref(AudioFileRow file) =>
        "/audio/file-detail?sourceId=" + Uri.EscapeDataString(file.SourceId) + "&path=" + Uri.EscapeDataString(file.RelativePath);

    private static string DurationLabel(double? seconds) =>
        seconds is > 0 ? UiFormat.Duration(seconds.Value) : "-";

    private static string ArtifactLabel(AudioFileRow file)
    {
        if (file.TurnCount > 0)
        {
            return $"JSONあり / {file.TurnCount} 区間";
        }

        return "JSONあり";
    }

    private static bool ShouldShowVerbalizationStatus(AudioFileRow file) =>
        file.HasTimeline && file.AudioVerbalization is not null;

    private static bool IsFullyVerbalized(AudioFileRow file) =>
        file.HasTimeline
        && file.TurnCount > 0
        && file.AudioVerbalization.State.Equals("completed", StringComparison.OrdinalIgnoreCase);

    private static string VerbalizationLabel(AudioVerbalizationStatus status)
    {
        var label = status.State.ToLowerInvariant() switch
        {
            "completed" => "言語化済み",
            "running" => "言語化中",
            "planned" => "言語化待ち",
            "needs_review" => "一部未解決",
            "stale" => "再言語化必要",
            "failed" => "言語化失敗",
            "unreadable" => "読取不可",
            _ => "未言語化",
        };

        if ((status.State.Equals("running", StringComparison.OrdinalIgnoreCase)
                || status.State.Equals("planned", StringComparison.OrdinalIgnoreCase))
            && status.TotalChunks > 0)
        {
            return $"{label} {status.CompletedChunks}/{status.TotalChunks}";
        }

        return label;
    }

    private static string VerbalizationIcon(string state) =>
        state.ToLowerInvariant() switch
        {
            "completed" => "language",
            "running" => "spinner",
            "planned" => "clock",
            "needs_review" => "triangle-exclamation",
            "stale" => "arrows-rotate",
            "failed" => "triangle-exclamation",
            "unreadable" => "circle-xmark",
            _ => "circle-minus",
        };

    private static string VerbalizationIconSpin(string state) =>
        state.Equals("running", StringComparison.OrdinalIgnoreCase) ? "fa-spin" : "";

    private static string VerbalizationPill(string state) =>
        state.ToLowerInvariant() switch
        {
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "running" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "planned" or "needs_review" or "stale" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
            "failed" or "unreadable" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };

    private void OpenDeleteModal()
    {
        if (HasGeneratedItemSelection)
        {
            _deleteModalOpen = true;
        }
    }

    private void CloseDeleteModal()
    {
        if (!_deleting)
        {
            _deleteModalOpen = false;
        }
    }

    private async Task ConfirmDeleteSelectedAsync()
    {
        var selected = SelectedFiles().ToList();
        var itemIds = SelectedGeneratedItemIds(selected).ToList();
        var identities = selected
            .Select(SourceFileIdentity)
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (itemIds.Count == 0)
        {
            _error = "削除できる生成物があるファイルを選択してください。";
            return;
        }

        _deleting = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DeleteAudioGeneratedAsync(new AudioDeleteGeneratedRequest
            {
                ItemIds = itemIds,
                SourceFileIdentities = identities,
            });
            _selectedFileKeys.Clear();
            _deleteModalOpen = false;
            _operationMessage = DeleteGeneratedMessage(result, selected.Count);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _deleting = false;
        }
    }

    private static string RefreshMessage(AudioRefreshResult result)
    {
        if (result.QueuedCount > 0)
        {
            return $"{result.QueuedCount} 件の分析を開始しました。処理状況は一覧更新で確認できます。";
        }

        if (result.State.Equals("skipped", StringComparison.OrdinalIgnoreCase))
        {
            return "新しく分析する音声はありませんでした。";
        }

        return "再スキャンを開始しました。処理状況は一覧更新で確認できます。";
    }

    public void Dispose()
    {
        _disposed = true;
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
    }

}
