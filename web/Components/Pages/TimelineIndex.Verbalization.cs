using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineIndex
{
    private string AudioVerbalizationStepLabel => AudioVerbalizationStepState switch
    {
        "running" => "処理中",
        "failed" => "失敗",
        "review" => "未解決あり",
        "completed" => "完了",
        _ => "未実行",
    };
    private string AudioVerbalizationStepDetail => _audioVerbalizationStatus switch
    {
        _ when RebuildActive => "時間軸作成後に、音声・動画の文字起こしを補正します。",
        null => "未補正の音声・動画があれば、まとめて補正できます。",
        { State: "running" or "queued" or "starting" } => $"補正中 {_audioVerbalizationStatus.CompletedChunks:N0} / {_audioVerbalizationStatus.TotalChunks:N0} チャンク",
        { State: "completed", FailedItems: > 0 } => FailedAudioVerbalizationDetail,
        { State: "completed", ReviewItems: > 0, VerbalizedTurns: > 0 } => $"未解決 {_audioVerbalizationStatus.ReviewItems:N0} 件。作成済みの補正候補だけを表示します。",
        { State: "completed", ReviewItems: > 0 } => $"未解決 {_audioVerbalizationStatus.ReviewItems:N0} 件。読める候補は作成できませんでした。",
        { State: "completed" } => "未補正の音声・動画は残っていません。",
        { State: "failed" } => "補正が途中で止まりました。",
        _ => "未補正の音声・動画があれば、まとめて補正できます。",
    };
    private string FailedAudioVerbalizationDetail =>
        _audioVerbalizationTargetSummary?.TargetCount > 0
            ? $"失敗 {_audioVerbalizationStatus?.FailedItems:N0} 件。次回スキャンで {_audioVerbalizationTargetSummary.TargetCount:N0} 件を再試行します。"
            : $"失敗 {_audioVerbalizationStatus?.FailedItems:N0} 件。必要に応じてログを確認します。";
    private string AudioVerbalizationStepState
    {
        get
        {
            if (AudioVerbalizationActive || _verbalizing)
            {
                return "running";
            }
            if (RebuildActive)
            {
                return "waiting";
            }
            if (_audioVerbalizationStatus?.State.Equals("failed", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "failed";
            }
            if (_audioVerbalizationStatus?.State.Equals("completed", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (_audioVerbalizationStatus.FailedItems > 0)
                {
                    return "failed";
                }
                return _audioVerbalizationStatus.ReviewItems > 0 ? "review" : "completed";
            }
            return "waiting";
        }
    }
    private string AudioVerbalizationStepPillClass => StepPillClass(AudioVerbalizationStepState);
    private string TimelineReflectStepLabel => RebuildActive
        ? "処理中"
        : _overview?.Available == true
            ? "利用可能"
            : "未作成";
    private string TimelineReflectStepDetail => RebuildActive
        ? TimelineReflectActiveDetail
        : _overview?.Available == true
            ? $"{EventCountLabel}を時間軸に反映済み"
            : "素材取り込み後に時間軸へ反映します。";
    private string TimelineReflectActiveDetail =>
        (_workerStatus?.Stage ?? "") switch
        {
            "importing" => "取得したデータを時間軸へ取り込んでいます。",
            "sorting" => "時間順に並べています。",
            "publishing" => "画面で使う時間軸へ反映しています。",
            _ => "素材取り込み後に時間軸へ反映します。",
        };
    private string TimelineReflectStepPillClass => StepPillClass(RebuildActive
        ? "running"
        : _overview?.Available == true
            ? "completed"
            : "waiting");
    private bool AudioVerbalizationActive => IsActiveAudioVerbalization(_audioVerbalizationStatus);
    private bool ShouldShowAudioVerbalizationProgress =>
        _audioVerbalizationStatus is not null
        && (_verbalizing || AudioVerbalizationActive);
    private int AudioVerbalizationFinishedItems =>
        _audioVerbalizationStatus is null
            ? 0
            : _audioVerbalizationStatus.CompletedItems + _audioVerbalizationStatus.ReviewItems + _audioVerbalizationStatus.FailedItems + _audioVerbalizationStatus.SkippedItems;
    private double AudioVerbalizationProgressPercent =>
        _audioVerbalizationStatus is null
            ? 0
            : _audioVerbalizationStatus.ProgressPercent > 0
                ? _audioVerbalizationStatus.ProgressPercent
                : _audioVerbalizationStatus.TotalItems > 0
                    ? (double)AudioVerbalizationFinishedItems / _audioVerbalizationStatus.TotalItems * 100
                    : 0;
    private string AudioVerbalizationProgressCountLabel =>
        _audioVerbalizationStatus is null
            ? "0 / 0 件"
            : $"{AudioVerbalizationFinishedItems} / {_audioVerbalizationStatus.TotalItems} 件";
    private string AudioVerbalizationCurrentFile =>
        _verbalizing || AudioVerbalizationActive
            ? _audioVerbalizationStatus?.CurrentFileName ?? ""
            : "";
    private string AudioVerbalizationStateLabel => AudioVerbalizationStateText(_audioVerbalizationStatus?.State ?? "");
    private string AudioVerbalizationStatePill => AudioVerbalizationStatePillClass(_audioVerbalizationStatus?.State ?? "");
    private string AudioVerbalizationIcon =>
        (_audioVerbalizationStatus?.State ?? "").ToLowerInvariant() switch
        {
            "running" or "queued" or "starting" => "spinner",
            "completed" => "circle-check",
            "failed" => "triangle-exclamation",
            _ => "language",
        };
    private string AudioVerbalizationIconSpin => AudioVerbalizationActive || _verbalizing ? "fa-spin" : "";

    private async Task PollAudioVerbalizationAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (AudioVerbalizationActive)
                {
                    await InvokeAsync(RefreshAudioVerbalizationStatusAsync);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshAudioVerbalizationStatusAsync()
    {
        if (_pollingAudioVerbalization || _loading || _verbalizing)
        {
            return;
        }

        _pollingAudioVerbalization = true;
        var hadActive = AudioVerbalizationActive;
        try
        {
            _audioVerbalizationStatus = await Timeline.GetAudioVerbalizationBulkStatusAsync(_audioVerbalizationStatus?.JobId ?? "");
            if (hadActive && !AudioVerbalizationActive)
            {
                await ContinueScanAfterAudioVerbalizationAsync();
                QueueAudioVerbalizationTargetSummaryLoad();
            }
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
        }
        finally
        {
            _pollingAudioVerbalization = false;
            StateHasChanged();
        }
    }

    private async Task ContinueScanAfterAudioVerbalizationAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (ShouldRebuildTimelineAfterAudioVerbalization(_audioVerbalizationStatus))
        {
            var rebuilt = await RebuildTimelineAfterAudioVerbalizationAsync();
            if (!rebuilt)
            {
                return;
            }
        }
        else
        {
            var message = AudioVerbalizationStatusMessage(_audioVerbalizationStatus);
            SetOperationMessage(message, AudioVerbalizationMessageAutoClearDelay(_audioVerbalizationStatus));
        }

        if (CanStartItemSummaryAfterAudioVerbalization(_audioVerbalizationStatus))
        {
            await StartItemSummaryGenerationAsync();
        }
    }

    private async Task<bool> RebuildTimelineAfterAudioVerbalizationAsync()
    {
        if (_disposed || _rebuilding)
        {
            return false;
        }

        SetOperationMessage("文字起こし補正の結果を時間軸へ反映しています。");
        var rebuilt = await RebuildAsync();
        if (rebuilt)
        {
            SetOperationMessage("文字起こし補正の結果を時間軸へ反映しました。", TimeSpan.FromSeconds(10));
        }
        return rebuilt;
    }

    private void ToggleAudioVerbalizationDetails() => _audioVerbalizationDetailsOpen = !_audioVerbalizationDetailsOpen;

    private async Task StartAudioVerbalizationBulkAsync(bool showPrerequisiteError = true)
    {
        if (_overview?.Available != true)
        {
            if (showPrerequisiteError)
            {
                _error = "先にスキャンしてください。文字起こし補正では周辺情報をヒントとして使います。";
            }
            return;
        }

        _verbalizing = true;
        _error = null;
        SetOperationMessage("未補正の音声・動画があれば、周辺情報を使って順番に補正します。");
        await InvokeAsync(StateHasChanged);
        try
        {
            _audioVerbalizationStatus = await Timeline.StartAudioVerbalizationBulkAsync();
            _audioVerbalizationTargetSummary = null;
            SetOperationMessage(
                AudioVerbalizationStatusMessage(_audioVerbalizationStatus),
                AudioVerbalizationMessageAutoClearDelay(_audioVerbalizationStatus));
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _verbalizing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static bool IsWorkerActive(TimelineWorkerJobStatus? status)
    {
        var state = status?.State ?? "";
        return string.Equals(state, "queued", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "canceling", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveAudioVerbalization(AudioVerbalizationBulkStatus? status) =>
        status is not null
        && (
            status.State.Equals("running", StringComparison.OrdinalIgnoreCase)
            || status.State.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || status.State.Equals("starting", StringComparison.OrdinalIgnoreCase)
            || status.State.Equals("canceling", StringComparison.OrdinalIgnoreCase)
        );

    private static bool IsFailedAudioVerbalization(AudioVerbalizationBulkStatus? status) =>
        status is not null
        && status.State.Equals("failed", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldRebuildTimelineAfterAudioVerbalization(AudioVerbalizationBulkStatus? status) =>
        status is not null
        && status.State.Equals("completed", StringComparison.OrdinalIgnoreCase)
        && (
            status.VerbalizedTurns > 0
            || status.CompletedItems > 0
            || status.ReviewItems > 0
        );

    private static string AudioVerbalizationStatusMessage(AudioVerbalizationBulkStatus? status)
    {
        if (status is null)
        {
            return "文字起こし補正の状態を確認しています。";
        }

        if (status.TotalItems == 0 && status.State.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            return "未補正の音声・動画はありませんでした。";
        }

        if (status.State.Equals("canceling", StringComparison.OrdinalIgnoreCase))
        {
            return "文字起こし補正の停止要求を受け付けました。処理中のAI呼び出しを止め、未確定の結果は反映しません。";
        }

        if (IsActiveAudioVerbalization(status))
        {
            return "未補正の音声・動画を周辺情報を使って順番に補正しています。";
        }

        if (status.State.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(status.Message)
                ? "文字起こし補正に失敗しました。"
                : status.Message;
        }

        if (status.State.Equals("canceled", StringComparison.OrdinalIgnoreCase))
        {
            return "文字起こし補正を停止しました。完了済みの結果は残っています。";
        }

        if (status.FailedItems > 0)
        {
            return $"文字起こし補正が完了しました。失敗 {status.FailedItems:N0} 件。";
        }

        return status.ReviewItems > 0 && status.VerbalizedTurns > 0
            ? $"文字起こし補正が完了しました。未解決 {status.ReviewItems:N0} 件。"
            : status.ReviewItems > 0
                ? $"文字起こし補正が完了しましたが、読める候補は作成できませんでした。未解決 {status.ReviewItems:N0} 件。"
            : "文字起こし補正が完了しました。未補正の音声・動画は残っていません。";
    }

    private static TimeSpan? AudioVerbalizationMessageAutoClearDelay(AudioVerbalizationBulkStatus? status)
    {
        if (status is null)
        {
            return null;
        }

        if (status.State.Equals("completed", StringComparison.OrdinalIgnoreCase)
            && status.FailedItems <= 0)
        {
            return TimeSpan.FromSeconds(10);
        }

        return null;
    }

    private void SetOperationMessage(string? message, TimeSpan? autoClearAfter = null)
    {
        _operationMessageAutoClearCts?.Cancel();
        _operationMessageAutoClearCts?.Dispose();
        _operationMessageAutoClearCts = null;
        _operationMessage = message;

        if (string.IsNullOrWhiteSpace(message) || autoClearAfter is null)
        {
            return;
        }

        _operationMessageAutoClearCts = new CancellationTokenSource();
        _ = ClearOperationMessageAfterAsync(autoClearAfter.Value, _operationMessageAutoClearCts.Token);
    }

    private async Task ClearOperationMessageAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            await InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _operationMessage = null;
                    StateHasChanged();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void QueueAudioVerbalizationTargetSummaryLoad()
    {
        if (_disposed || _loadingAudioVerbalizationTargets)
        {
            return;
        }

        _ = LoadAudioVerbalizationTargetSummaryAsync();
    }

    private async Task LoadAudioVerbalizationTargetSummaryAsync()
    {
        if (_disposed || _loadingAudioVerbalizationTargets)
        {
            return;
        }

        _loadingAudioVerbalizationTargets = true;
        try
        {
            _audioVerbalizationTargetSummary = await Timeline.GetAudioVerbalizationBulkTargetSummaryAsync();
        }
        catch
        {
            _audioVerbalizationTargetSummary = null;
        }
        finally
        {
            _loadingAudioVerbalizationTargets = false;
            if (!_disposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private static string AudioVerbalizationStateText(string state) =>
        state.ToLowerInvariant() switch
        {
            "running" => "補正中",
            "queued" or "starting" => "待機中",
            "completed" => "完了",
            "failed" => "失敗",
            "unknown" or "unreadable" => "確認不可",
            _ => "未実行",
        };

    private static string AudioVerbalizationStatePillClass(string state) =>
        state.ToLowerInvariant() switch
        {
            "running" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "queued" or "starting" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "failed" or "unknown" or "unreadable" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };
}
