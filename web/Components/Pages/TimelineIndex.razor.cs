using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineIndex
{
    private TimelineStoreOverview? _overview;
    private ProductRuntimeOverview? _runtime;
    private TimelineDockerWorkerStatus? _dockerWorkerStatus;
    private AudioVerbalizationBulkStatus? _audioVerbalizationStatus;
    private AudioVerbalizationBulkTargetSummary? _audioVerbalizationTargetSummary;
    private bool _loading = true;
    private bool _rebuilding;
    private bool _downloading;
    private bool _verbalizing;
    private bool _pollingRebuildStatus;
    private bool _pollingAudioVerbalization;
    private bool _loadingAudioVerbalizationTargets;
    private bool _audioVerbalizationDetailsOpen;
    private bool _continueAudioAfterRebuild;
    private bool _disposed;
    private string? _error;
    private string? _operationMessage;
    private TimelineWorkerJobStatus? _workerStatus;
    private CancellationTokenSource? _pollingCts;
    private CancellationTokenSource? _operationMessageAutoClearCts;

    private bool Busy => _loading || _rebuilding || _downloading || _verbalizing;
    private bool ShouldShowOperationMessage => !string.IsNullOrWhiteSpace(_operationMessage);
    private bool CanDownload => _overview?.Available == true;
    private bool ScanActive => _rebuilding || _verbalizing || RebuildActive || AudioVerbalizationActive;
    private string ScanButtonLabel => ScanActive ? "処理中" : "スキャン";
    private string ScanButtonIcon => ScanActive ? "spinner" : "arrows-rotate";
    private string ScanButtonIconSpin => ScanActive ? "fa-spin" : "";
    private string EventCountLabel => _overview?.Available == true ? $"{_overview.EventCount:N0} 件" : "-";
    private string ItemCountLabel => _overview?.Available == true ? $"{_overview.ItemCount:N0} 件" : "-";
    private string StoreMessage => _overview?.Available == true
        ? ""
        : "スキャンを始めると、各製品の取り込み結果を集めて Timeline の時間軸を作成します。作成後、ダッシュボードや各詳細画面で確認できます。";
    private IReadOnlyList<MaterialProductLink> InstalledMaterialLinks =>
        AllMaterialProductLinks.Where(link => IsInstalledProduct(link.ProductId)).ToList();
    private string DockerWorkerStatusLabel => _dockerWorkerStatus switch
    {
        { Available: true, State: "running" } => "稼働中",
        { Available: true, State: "stopping" } => "停止中",
        { Available: true, State: "stopped" } => "停止",
        { Available: false, State: "missing" } => "未確認",
        { State.Length: > 0 } => _dockerWorkerStatus.State,
        _ => "未確認",
    };
    private string DockerWorkerStatusIcon => _dockerWorkerStatus?.Available == true && _dockerWorkerStatus.State == "running"
        ? "circle-check"
        : "circle-minus";
    private string DockerWorkerStatusPillClass => _dockerWorkerStatus?.Available == true && _dockerWorkerStatus.State == "running"
        ? "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800"
        : "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700";
    private string DockerWorkerUpdatedLabel => string.IsNullOrWhiteSpace(_dockerWorkerStatus?.UpdatedAt)
        ? "-"
        : UiFormat.ShortDate(_dockerWorkerStatus.UpdatedAt);
    private string DockerWorkerStoreLabel => _dockerWorkerStatus?.StoreAvailable == true ? "利用可能" : "未確認";
    private bool RebuildActive => _rebuilding || IsWorkerActive(_workerStatus);
    private string MaterialImportStepLabel => RebuildActive
        ? "処理中"
        : _overview?.Available == true
            ? "完了"
            : "未作成";
    private string MaterialImportStepDetail => RebuildActive
        ? WorkerStatusLabel(_workerStatus)
        : _overview?.Available == true
            ? $"{ItemCountLabel}を取り込み済み"
            : "スキャンすると素材を取り込みます。";
    private string MaterialImportStepPillClass => StepPillClass(RebuildActive
        ? "running"
        : _overview?.Available == true
            ? "completed"
            : "waiting");
    protected override async Task OnInitializedAsync()
    {
        var pollingCts = new CancellationTokenSource();
        _pollingCts = pollingCts;
        var pollingToken = pollingCts.Token;
        await LoadAsync();
        if (!_disposed && !pollingToken.IsCancellationRequested)
        {
            _ = PollTimelineRebuildAsync(pollingToken);
            _ = PollAudioVerbalizationAsync(pollingToken);
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        SetOperationMessage(null);
        try
        {
            var overviewTask = Timeline.GetTimelineStoreOverviewAsync();
            var workerTask = Timeline.GetTimelineWorkerStatusAsync();
            var rebuildTask = Timeline.GetTimelineRebuildStatusAsync();
            var verbalizationTask = Timeline.GetAudioVerbalizationBulkStatusAsync();
            var runtimeTask = Timeline.GetProductRuntimeOverviewAsync();
            await Task.WhenAll(overviewTask, workerTask, rebuildTask, verbalizationTask, runtimeTask);
            _overview = await overviewTask;
            _dockerWorkerStatus = await workerTask;
            _workerStatus = await rebuildTask;
            _audioVerbalizationStatus = await verbalizationTask;
            _runtime = await runtimeTask;
            _continueAudioAfterRebuild = IsWorkerActive(_workerStatus);
            if (IsWorkerActive(_workerStatus))
            {
                SetOperationMessage(WorkerStatusLabel(_workerStatus));
            }
            else if (AudioVerbalizationActive)
            {
                SetOperationMessage(AudioVerbalizationStatusMessage(_audioVerbalizationStatus));
            }
            if (AudioVerbalizationActive)
            {
                _audioVerbalizationTargetSummary = null;
            }
            else
            {
                QueueAudioVerbalizationTargetSummaryLoad();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ScanAsync()
    {
        _continueAudioAfterRebuild = true;
        var rebuilt = await RebuildAsync();
        if (!rebuilt || AudioVerbalizationActive || _disposed)
        {
            return;
        }

        _continueAudioAfterRebuild = false;
        await StartAudioVerbalizationBulkAsync(showPrerequisiteError: false);
    }

    private async Task PollTimelineRebuildAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if (IsWorkerActive(_workerStatus) || _continueAudioAfterRebuild)
                {
                    await InvokeAsync(RefreshTimelineRebuildStatusAsync);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshTimelineRebuildStatusAsync()
    {
        if (_pollingRebuildStatus || _loading || _rebuilding)
        {
            return;
        }

        _pollingRebuildStatus = true;
        var hadActive = IsWorkerActive(_workerStatus);
        try
        {
            _workerStatus = await Timeline.GetTimelineRebuildStatusAsync(_workerStatus?.JobId ?? "");
            if (IsWorkerActive(_workerStatus))
            {
                SetOperationMessage(WorkerStatusLabel(_workerStatus));
                return;
            }

            if (hadActive)
            {
                _dockerWorkerStatus = await Timeline.GetTimelineWorkerStatusAsync();
                _overview = await Timeline.GetTimelineStoreOverviewAsync();
                if (string.Equals(_workerStatus.State, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    _continueAudioAfterRebuild = false;
                    _error = string.IsNullOrWhiteSpace(_workerStatus.Error)
                        ? WorkerStatusLabel(_workerStatus)
                        : _workerStatus.Error;
                    return;
                }

                if (_continueAudioAfterRebuild && !_disposed && !AudioVerbalizationActive)
                {
                    _continueAudioAfterRebuild = false;
                    await StartAudioVerbalizationBulkAsync(showPrerequisiteError: false);
                    return;
                }

                SetOperationMessage(WorkerStatusLabel(_workerStatus), TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
        }
        finally
        {
            _pollingRebuildStatus = false;
            StateHasChanged();
        }
    }

    private async Task<bool> RebuildAsync()
    {
        _rebuilding = true;
        _error = null;
        SetOperationMessage("各プロダクトの API からデータを取得し、時間軸で使える状態に整えています。");
        try
        {
            _workerStatus = await Timeline.RebuildTimelineStoreAsync();
            SetOperationMessage(WorkerStatusLabel(_workerStatus));
            while (IsWorkerActive(_workerStatus))
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                _workerStatus = await Timeline.GetTimelineRebuildStatusAsync(_workerStatus.JobId);
                SetOperationMessage(WorkerStatusLabel(_workerStatus));
                await InvokeAsync(StateHasChanged);
            }

            if (string.Equals(_workerStatus.State, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(_workerStatus.Error)
                    ? "自動処理に失敗しました。"
                    : _workerStatus.Error);
            }

            var itemCount = _workerStatus.Result?.ItemCount ?? _workerStatus.ItemCount;
            var eventCount = _workerStatus.Result?.EventCount ?? _workerStatus.EventCount;
            SetOperationMessage($"自動処理で使える状態に整えました。{itemCount:N0} 件 / {eventCount:N0} イベント");
            _overview = await Timeline.GetTimelineStoreOverviewAsync();
            return true;
        }
        catch (Exception ex)
        {
            _continueAudioAfterRebuild = false;
            _error = ex.Message;
            return false;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _operationMessageAutoClearCts?.Cancel();
        _operationMessageAutoClearCts?.Dispose();
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
    }

}
