using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ChatGpt
{
    private bool CanRefresh => _overview?.ProductFound == true && _overview.SettingsValid;
    private IReadOnlyList<TimelineThreadRow> Threads => _threads;
    private bool ListBusy => _loading || _loadingMoreThreads;
    private int ThreadTotalItems => _pagination?.TotalItems > 0
        ? _pagination.TotalItems
        : Math.Max(_threadTotal, Threads.Count);
    private string ThreadListStatusLabel => ListBusy
        ? "読み込み中"
        : _lastLoadedAt is null
            ? ""
            : $"最終更新 {_lastLoadedAt.Value:HH:mm:ss}";
    private string ImportedThreadCountLabel => $"{(_overview?.ItemCount ?? 0):N0} 件";
    private string EmptyThreadMessage => !string.IsNullOrWhiteSpace(_threadListMessage)
        ? _threadListMessage
        : "スレッドはありません。";
    private int TotalMessageCount => Threads.Sum(thread => thread.MessageCount);
    private ChatGptJobRow? ActiveJob => _overview?.Jobs.FirstOrDefault(IsActiveJob);
    private bool ShowProcessingProgress => _refreshing || ActiveJob is not null;
    private bool HasMeasuredProgress => ActiveJob is not null && (ActiveJob.ConversationsTotal > 0 || ActiveJob.ProgressPercent > 0);
    private double ProcessingProgressPercent => Math.Clamp(ActiveJob?.ProgressPercent ?? 0, 0, 100);
    private string ProcessingSummaryLabel => ShowProcessingProgress ? "処理中" : "待機中";
    private string ProcessingSummaryIcon => ShowProcessingProgress ? "spinner" : "circle-check";
    private string ProcessingSummaryIconClass => ShowProcessingProgress ? "text-sky-800" : "text-slate-500";
    private string ProcessingStateLabel => ActiveJob is not null ? StateLabel(ActiveJob.State) : "準備中";
    private string ProcessingStatePill => ActiveJob is not null
        ? StatePillClass(ActiveJob.State)
        : "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800";
    private string ProcessingStageLabel => ActiveJob is not null ? StageLabel(ActiveJob.CurrentStage) : "ZIPを処理中";
    private string ProcessingRunId => ActiveJob?.JobId ?? "";
    private string ProcessingCurrentItem => ActiveJob?.CurrentConversation ?? "";
    private string ProcessingProgressLabel => HasMeasuredProgress ? $"{ProcessingProgressPercent:0.#}%" : "処理中";
    private string ProcessingProgressCountLabel => ActiveJob is null
        ? "完了後に一覧へ反映します"
        : ActiveJob.ConversationsTotal > 0
            ? $"{ActiveJob.ConversationsDone} / {ActiveJob.ConversationsTotal} 件"
            : "件数を確認中";
    private int ProcessingProgressAriaValue => HasMeasuredProgress ? (int)Math.Round(ProcessingProgressPercent) : 0;
    private string ProcessingProgressBarClass => HasMeasuredProgress
        ? "tfa-run-progress-bar"
        : "tfa-run-progress-bar tfa-run-progress-bar-indeterminate";
    private string ProcessingProgressBarStyle => HasMeasuredProgress ? $"width:{ProcessingProgressPercent:0.##}%" : "";
    private string ProcessingTotalLabel => ActiveJob?.ConversationsTotal > 0 ? $"{ActiveJob.ConversationsTotal} 件" : "-";
    private string ProcessingDoneLabel => ActiveJob is not null ? $"{ActiveJob.ConversationsDone} 件" : "-";
    private string ProcessingErrorLabel => ActiveJob is not null ? $"{ActiveJob.ErrorCount} 件" : "-";
    private string ProcessingBatchLabel => ActiveJob?.BatchCount > 0 ? $"{ActiveJob.BatchCount} 件" : "-";
    private string DisplayTimeZoneId => _timelineSettings?.TimeZoneId ?? "Asia/Tokyo";
    private bool HasSelection => _selectedThreadIds.Count > 0;
    private int SelectedItemCount => SelectedItemIds().Count;
    private bool AllThreadsSelected => Threads.Count > 0 && Threads.All(thread => _selectedThreadIds.Contains(thread.ItemId));
    private string SelectionSummary => HasSelection ? $"{_selectedThreadIds.Count} 件選択中" : "未選択";

    private static string StateLabel(string? state)
    {
        return (state ?? "").Trim().ToLowerInvariant() switch
        {
            "completed" => "完了",
            "running" or "processing" => "処理中",
            "pending" or "queued" => "待機中",
            "failed" => "失敗",
            "" => "未作成",
            var value => value,
        };
    }

    private static string StatePillClass(string? state)
    {
        return (state ?? "").Trim().ToLowerInvariant() switch
        {
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "running" or "processing" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "pending" or "queued" => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
            "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };
    }

    private static bool IsActiveJob(ChatGptJobRow job) =>
        job.State.Equals("running", StringComparison.OrdinalIgnoreCase)
        || job.State.Equals("processing", StringComparison.OrdinalIgnoreCase)
        || job.State.Equals("pending", StringComparison.OrdinalIgnoreCase)
        || job.State.Equals("queued", StringComparison.OrdinalIgnoreCase);

    private static string StageLabel(string? stage)
    {
        return (stage ?? "").Trim().ToLowerInvariant() switch
        {
            "queued" => "待機中",
            "preflight" => "準備中",
            "extract" or "extract_zip" => "ZIP展開中",
            "parse" or "parse_conversations" => "会話解析中",
            "write" or "write_items" or "generate_items" => "保存中",
            "completed" => "完了",
            "failed" => "失敗",
            "" => "確認中",
            var value => value,
        };
    }

    private static string ThreadUrl(TimelineThreadRow thread) =>
        $"chatgpt/thread/{Uri.EscapeDataString(thread.ItemId)}";

    private string ShortDate(string? value) =>
        UiFormat.ShortDate(value ?? "", DisplayTimeZoneId);
}
