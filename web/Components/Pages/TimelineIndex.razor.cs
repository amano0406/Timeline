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
    private TimelineItemSummaryJobStatus? _itemSummaryStatus;
    private bool _loading = true;
    private bool _rebuilding;
    private bool _downloading;
    private bool _verbalizing;
    private bool _summarizing;
    private bool _cancelingScan;
    private bool _repairingWorker;
    private bool _pollingRebuildStatus;
    private bool _pollingAudioVerbalization;
    private bool _pollingItemSummaries;
    private bool _loadingAudioVerbalizationTargets;
    private bool _audioVerbalizationDetailsOpen;
    private bool _continueAudioAfterRebuild;
    private bool _scanStartModalOpen;
    private bool _includeChatGptExportInScan;
    private bool _disposed;
    private string? _error;
    private string? _operationMessage;
    private string _chatGptExportZipPath = "";
    private TimelineWorkerJobStatus? _workerStatus;
    private CancellationTokenSource? _pollingCts;
    private CancellationTokenSource? _operationMessageAutoClearCts;

    private bool Busy => _loading || _rebuilding || _downloading || _verbalizing || _summarizing || _cancelingScan;
    private string RefreshHref => $"/scan?refresh={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    private bool ShouldShowOperationMessage => !string.IsNullOrWhiteSpace(_operationMessage);
    private bool CanDownload => _overview?.Available == true;
    private bool ScanActive => _rebuilding || _verbalizing || _summarizing || RebuildActive || AudioVerbalizationActive || ItemSummaryActive;
    private bool CanCancelScan => ScanActive && !_cancelingScan;
    private string ScanButtonLabel => ScanActive ? "処理中" : "スキャン";
    private string ScanButtonIcon => ScanActive ? "spinner" : "arrows-rotate";
    private string ScanButtonIconSpin => ScanActive ? "fa-spin" : "";
    private string EventCountLabel => _overview?.Available == true ? $"{_overview.EventCount:N0} 件" : "-";
    private string ItemCountLabel => _overview?.Available == true ? $"{_overview.ItemCount:N0} 件" : "-";
    private string StoreMessage => _overview?.Available == true
        ? ""
        : "スキャンを始めると、各製品の取り込み結果を集めて Timeline の時間軸を作成します。作成後、ダッシュボードや各詳細画面で確認できます。";
    private bool ScanStartNeedsChatGptZip =>
        _includeChatGptExportInScan && string.IsNullOrWhiteSpace(_chatGptExportZipPath);
    private string ChatGptExportZipLabel =>
        string.IsNullOrWhiteSpace(_chatGptExportZipPath)
            ? "未選択"
            : _chatGptExportZipPath;
    private string DockerWorkerStatusLabel => _dockerWorkerStatus switch
    {
        { Available: true, State: "running" } => "稼働中",
        { Available: true, State: "stopping" } => "停止中",
        { Available: true, State: "stopped" } => "停止中",
        { Available: false, State: "local_api_unreachable" } => "操作機能に未接続",
        { Available: false, State: "docker_unavailable" } => "Docker未起動",
        { Available: false, State: "missing" } => "未起動",
        { Available: false, State: "stale" } => "応答なし",
        { Available: false, State: "unreadable" } => "状態取得失敗",
        { State: "unknown" } => "未確認",
        { State.Length: > 0 } => "確認が必要",
        _ => "未確認",
    };
    private bool DockerWorkerRunning =>
        _dockerWorkerStatus?.Available == true
        && _dockerWorkerStatus.State.Equals("running", StringComparison.OrdinalIgnoreCase);
    private bool LocalApiUnavailable =>
        _dockerWorkerStatus?.State.Equals("local_api_unreachable", StringComparison.OrdinalIgnoreCase) == true;
    private bool DockerUnavailable =>
        _dockerWorkerStatus?.State.Equals("docker_unavailable", StringComparison.OrdinalIgnoreCase) == true;
    private bool DockerWorkerStatusUnreadable =>
        _dockerWorkerStatus?.State.Equals("unreadable", StringComparison.OrdinalIgnoreCase) == true;
    private bool CanRepairDockerWorker =>
        !DockerWorkerRunning
        && !LocalApiUnavailable
        && !_repairingWorker
        && !ScanActive
        && !_loading;
    private string DockerWorkerStatusIcon => DockerWorkerRunning
        ? "circle-check"
        : _repairingWorker ? "spinner" : "triangle-exclamation";
    private string DockerWorkerStatusIconSpin => _repairingWorker ? "fa-spin" : "";
    private string DockerWorkerStatusPillClass => DockerWorkerRunning
        ? "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800"
        : "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900";
    private string DockerWorkerUpdatedLabel => string.IsNullOrWhiteSpace(_dockerWorkerStatus?.UpdatedAt)
        ? "-"
        : UiFormat.ShortDate(_dockerWorkerStatus.UpdatedAt);
    private string DockerWorkerStoreLabel => _dockerWorkerStatus?.StoreAvailable == true ? "利用可能" : "未確認";
    private string DockerWorkerHelpText => DockerWorkerRunning
        ? "自動処理は利用できます。"
        : LocalApiUnavailable
            ? "Timeline の操作機能に接続できないため、画面から自動処理を復旧できません。Timeline を起動し直してください。"
            : DockerUnavailable
                ? "Docker が起動していません。復旧を実行すると、Docker と Timeline の自動処理の起動を試します。"
                : DockerWorkerStatusUnreadable
                    ? "Docker または worker の状態を確認できません。復旧を実行すると、Docker と worker の起動を試します。"
                    : "スキャンに必要な自動処理が確認できません。復旧を実行すると、Timeline の worker だけを起動し直します。";
    private string DockerWorkerStatusDetail => RuntimeDisplayText.WorkerStatusDetail(_dockerWorkerStatus);
    private string DockerWorkerRepairLabel => DockerUnavailable ? "Dockerと自動処理を復旧" : "自動処理を復旧";
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
            _ = PollItemSummariesAsync(pollingToken);
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        SetOperationMessage(null);
        try
        {
            var overviewTask = Timeline.GetTimelineStoreOverviewWithLocalFallbackAsync();
            var workerTask = Timeline.GetTimelineWorkerStatusAsync();
            var rebuildTask = Timeline.GetTimelineRebuildStatusAsync();
            var verbalizationTask = Timeline.GetAudioVerbalizationBulkStatusAsync();
            var summaryTask = Timeline.GetTimelineItemSummaryStatusAsync();
            var runtimeTask = Timeline.GetProductRuntimeOverviewAsync();

            _dockerWorkerStatus = await workerTask;
            await InvokeAsync(StateHasChanged);

            await Task.WhenAll(overviewTask, workerTask, rebuildTask, verbalizationTask, summaryTask, runtimeTask);
            _overview = await overviewTask;
            _workerStatus = await rebuildTask;
            _audioVerbalizationStatus = await verbalizationTask;
            _itemSummaryStatus = await summaryTask;
            _runtime = await runtimeTask;
            QueueScanDataSourcesLoad();
            _continueAudioAfterRebuild = IsWorkerActive(_workerStatus);
            if (IsWorkerActive(_workerStatus))
            {
                SetOperationMessage(WorkerStatusLabel(_workerStatus));
            }
            else if (AudioVerbalizationActive)
            {
                SetOperationMessage(AudioVerbalizationStatusMessage(_audioVerbalizationStatus));
            }
            else if (ItemSummaryActive)
            {
                SetOperationMessage(ItemSummaryStatusMessage(_itemSummaryStatus));
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

    private void OpenScanStartModal()
    {
        if (ScanActive || _loading || _downloading)
        {
            return;
        }

        _error = null;
        _scanStartModalOpen = true;
    }

    private void CloseScanStartModal()
    {
        _scanStartModalOpen = false;
    }

    private async Task PickChatGptExportZipAsync()
    {
        try
        {
            var filePath = await Js.InvokeAsync<string?>(
                "timelineDirectoryPicker.pickFile",
                "ChatGPTエクスポートZIPを選択",
                "",
                "ZIP files (*.zip)|*.zip|All files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                _chatGptExportZipPath = filePath;
            }
        }
        catch (JSException ex)
        {
            _error = ex.Message;
            _scanStartModalOpen = false;
        }
    }

    private async Task ConfirmScanStartAsync()
    {
        if (ScanStartNeedsChatGptZip)
        {
            return;
        }

        var request = new TimelineRebuildRequest
        {
            ChatGptExportZipPath = _includeChatGptExportInScan ? _chatGptExportZipPath : "",
        };
        _scanStartModalOpen = false;
        await ScanAsync(request);
    }

    private async Task CancelScanAsync()
    {
        if (!ScanActive || _cancelingScan)
        {
            return;
        }

        _cancelingScan = true;
        _continueAudioAfterRebuild = false;
        _error = null;
        SetOperationMessage("停止要求を送信しています。現在の処理を中断します。");
        try
        {
            if (RebuildActive)
            {
                _workerStatus = await Timeline.CancelTimelineRebuildAsync(_workerStatus?.JobId ?? "");
            }

            if (AudioVerbalizationActive || _verbalizing)
            {
                _audioVerbalizationStatus = await Timeline.CancelAudioVerbalizationBulkAsync(_audioVerbalizationStatus?.JobId ?? "");
            }

            if (ItemSummaryActive || _summarizing)
            {
                _itemSummaryStatus = await Timeline.CancelTimelineItemSummariesAsync(_itemSummaryStatus?.JobId ?? "");
            }

            SetOperationMessage("停止要求を送信しました。完了済みの結果は残し、処理中の項目は中断します。");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _cancelingScan = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task RepairWorkerAsync()
    {
        if (_repairingWorker || ScanActive)
        {
            return;
        }

        _repairingWorker = true;
        _error = null;
        SetOperationMessage("自動処理を復旧しています。Docker の起動に少し時間がかかる場合があります。");
        await InvokeAsync(StateHasChanged);

        try
        {
            var result = await Timeline.RepairTimelineWorkerAsync();
            _dockerWorkerStatus = result.Worker;
            SetOperationMessage(string.IsNullOrWhiteSpace(result.Message)
                ? "自動処理の復旧を実行しました。"
                : result.Message,
                TimeSpan.FromSeconds(10));
            _overview = await Timeline.GetTimelineStoreOverviewWithLocalFallbackAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            SetOperationMessage(null);
        }
        finally
        {
            _repairingWorker = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ScanAsync(TimelineRebuildRequest? request = null)
    {
        _continueAudioAfterRebuild = true;
        var rebuilt = await RebuildAsync(request ?? new TimelineRebuildRequest());
        if (!rebuilt || AudioVerbalizationActive || _disposed)
        {
            return;
        }

        _continueAudioAfterRebuild = false;
        await StartAudioVerbalizationBulkAsync(showPrerequisiteError: false);
        if (!AudioVerbalizationActive && !_disposed && string.IsNullOrWhiteSpace(_error))
        {
            await ContinueScanAfterAudioVerbalizationAsync();
        }
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
                _overview = await Timeline.GetTimelineStoreOverviewWithLocalFallbackAsync();
                if (string.Equals(_workerStatus.State, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    _continueAudioAfterRebuild = false;
                    _error = string.IsNullOrWhiteSpace(_workerStatus.Error)
                        ? WorkerStatusLabel(_workerStatus)
                        : _workerStatus.Error;
                    return;
                }

                if (string.Equals(_workerStatus.State, "canceled", StringComparison.OrdinalIgnoreCase))
                {
                    _continueAudioAfterRebuild = false;
                    SetOperationMessage("スキャンを停止しました。完了済みの結果は残っています。", TimeSpan.FromSeconds(10));
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

    private async Task<bool> RebuildAsync(TimelineRebuildRequest? request = null)
    {
        _rebuilding = true;
        _error = null;
        SetOperationMessage("各プロダクトの API からデータを取得し、時間軸で使える状態に整えています。");
        try
        {
            _workerStatus = await Timeline.RebuildTimelineStoreAsync(request ?? new TimelineRebuildRequest());
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

            if (string.Equals(_workerStatus.State, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                _continueAudioAfterRebuild = false;
                SetOperationMessage("スキャンを停止しました。完了済みの結果は残っています。", TimeSpan.FromSeconds(10));
                return false;
            }

            var itemCount = _workerStatus.Result?.ItemCount ?? _workerStatus.ItemCount;
            var eventCount = _workerStatus.Result?.EventCount ?? _workerStatus.EventCount;
            SetOperationMessage($"自動処理で使える状態に整えました。{itemCount:N0} 件 / {eventCount:N0} イベント");
            _overview = await Timeline.GetTimelineStoreOverviewWithLocalFallbackAsync();
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
