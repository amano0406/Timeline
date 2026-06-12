using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineIndex
{
    private bool ItemSummaryActive => IsActiveItemSummary(_itemSummaryStatus);

    private string ItemSummaryStepState
    {
        get
        {
            if (ItemSummaryActive || _summarizing)
            {
                return "running";
            }

            var state = (_itemSummaryStatus?.State ?? "").Trim().ToLowerInvariant();
            return state switch
            {
                "completed" => "completed",
                "completed_with_errors" => "review",
                "failed" => "failed",
                "canceled" => "waiting",
                _ => "waiting",
            };
        }
    }

    private string ItemSummaryStepDetail => _itemSummaryStatus switch
    {
        _ when RebuildActive || AudioVerbalizationActive || _verbalizing => "時間軸の作成と音声由来イベントの補正が終わった後に、素材ごとの概要を作ります。",
        null => "スキャン後に、音声・動画・スレッド単位の概要を作ります。",
        { State: "queued" or "running" or "starting" } => ItemSummaryStatusMessage(_itemSummaryStatus),
        { State: "completed_with_errors" } => $"素材概要の生成は終わりましたが、失敗した素材が {_itemSummaryStatus.FailedItems:N0} 件あります。",
        { State: "completed" } => $"素材概要を作成済みです。生成 {_itemSummaryStatus.CompletedItems:N0} 件 / 再利用 {_itemSummaryStatus.SkippedItems:N0} 件。",
        { State: "failed" } => string.IsNullOrWhiteSpace(_itemSummaryStatus.Error)
            ? "素材概要の生成に失敗しました。"
            : _itemSummaryStatus.Error,
        _ => "スキャン後に、素材ごとの概要を作ります。",
    };

    private int ItemSummaryFinishedItems =>
        _itemSummaryStatus is null
            ? 0
            : _itemSummaryStatus.CompletedItems + _itemSummaryStatus.SkippedItems + _itemSummaryStatus.FailedItems;

    private double ItemSummaryProgressPercent =>
        _itemSummaryStatus is null || _itemSummaryStatus.TotalItems <= 0
            ? 0
            : (double)ItemSummaryFinishedItems / _itemSummaryStatus.TotalItems * 100;

    private string ItemSummaryProgressCountLabel =>
        _itemSummaryStatus is null
            ? "0 / 0 件"
            : $"{ItemSummaryFinishedItems:N0} / {_itemSummaryStatus.TotalItems:N0} 件";

    private async Task StartItemSummaryGenerationAsync(bool force = false)
    {
        if (_disposed || ItemSummaryActive || _summarizing)
        {
            return;
        }

        _summarizing = true;
        _error = null;
        SetOperationMessage("素材ごとの概要を生成しています。既に同じ内容から作成済みの概要は再利用します。");
        await InvokeAsync(StateHasChanged);
        try
        {
            _itemSummaryStatus = await Timeline.StartTimelineItemSummariesAsync(force, runAll: true);
            SetOperationMessage(ItemSummaryStatusMessage(_itemSummaryStatus), ItemSummaryMessageAutoClearDelay(_itemSummaryStatus));
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _summarizing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task PollItemSummariesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (ItemSummaryActive)
                {
                    await InvokeAsync(RefreshItemSummaryStatusAsync);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshItemSummaryStatusAsync()
    {
        if (_pollingItemSummaries || _loading || _summarizing)
        {
            return;
        }

        _pollingItemSummaries = true;
        var hadActive = ItemSummaryActive;
        try
        {
            _itemSummaryStatus = await Timeline.GetTimelineItemSummaryStatusAsync(_itemSummaryStatus?.JobId ?? "");
            if (ItemSummaryActive)
            {
                SetOperationMessage(ItemSummaryStatusMessage(_itemSummaryStatus));
                return;
            }

            if (hadActive)
            {
                SetOperationMessage(ItemSummaryStatusMessage(_itemSummaryStatus), ItemSummaryMessageAutoClearDelay(_itemSummaryStatus));
            }
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
        }
        finally
        {
            _pollingItemSummaries = false;
            StateHasChanged();
        }
    }

    private static bool IsActiveItemSummary(TimelineItemSummaryJobStatus? status)
    {
        var state = status?.State ?? "";
        return state.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || state.Equals("running", StringComparison.OrdinalIgnoreCase)
            || state.Equals("starting", StringComparison.OrdinalIgnoreCase)
            || state.Equals("canceling", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanStartItemSummaryAfterAudioVerbalization(AudioVerbalizationBulkStatus? status)
    {
        if (status is null)
        {
            return true;
        }

        return status.State.Equals("completed", StringComparison.OrdinalIgnoreCase);
    }

    private static string ItemSummaryStatusMessage(TimelineItemSummaryJobStatus? status)
    {
        if (status is null)
        {
            return "素材概要の状態を確認しています。";
        }

        if (status.State.Equals("queued", StringComparison.OrdinalIgnoreCase))
        {
            return "素材概要の生成を開始待ちです。";
        }

        if (IsActiveItemSummary(status))
        {
            var current = string.IsNullOrWhiteSpace(status.Current?.Title)
                ? ""
                : $" / 処理中: {status.Current.Title}";
            return $"素材概要を生成しています。{status.CompletedItems:N0} 件生成 / {status.SkippedItems:N0} 件再利用 / {status.FailedItems:N0} 件失敗 / 全 {status.TotalItems:N0} 件{current}";
        }

        if (status.State.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            return $"素材概要の生成が完了しました。{status.CompletedItems:N0} 件生成 / {status.SkippedItems:N0} 件再利用。";
        }

        if (status.State.Equals("completed_with_errors", StringComparison.OrdinalIgnoreCase))
        {
            return $"素材概要の生成が完了しました。一部失敗があります。生成 {status.CompletedItems:N0} 件 / 再利用 {status.SkippedItems:N0} 件 / 失敗 {status.FailedItems:N0} 件。";
        }

        if (status.State.Equals("canceled", StringComparison.OrdinalIgnoreCase))
        {
            return "素材概要の生成を停止しました。完了済みの概要は残ります。";
        }

        if (status.State.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(status.Error)
                ? "素材概要の生成に失敗しました。"
                : status.Error;
        }

        return string.IsNullOrWhiteSpace(status.Message)
            ? "素材概要は未実行です。"
            : status.Message;
    }

    private static TimeSpan? ItemSummaryMessageAutoClearDelay(TimelineItemSummaryJobStatus? status)
    {
        if (status is null)
        {
            return null;
        }

        return status.State.Equals("completed", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromSeconds(10)
            : null;
    }
}
