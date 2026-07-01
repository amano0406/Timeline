using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class WindowsCodex
{
    private WindowsCodexCurrent Current => _overview?.Current ?? new WindowsCodexCurrent();
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
    private int ThreadCountLabel => Math.Max(Math.Max(_threadTotal, Threads.Count), Current.ThreadCount);
    private int GeneratedThreadCount => Current.Available ? Current.ThreadCount : 0;
    private string ThreadImportCountLabel => $"{GeneratedThreadCount:N0} / {ThreadCountLabel:N0}";
    private string EmptyThreadMessage => !string.IsNullOrWhiteSpace(_threadListMessage)
        ? _threadListMessage
        : "スレッドはありません。";
    private int TotalMessageCount => Threads.Sum(thread => thread.MessageCount);
    private string CurrentStateLabel => Current.Available ? StateLabel(Current.State) : "未作成";
    private string CurrentStateIcon => Current.Available && Current.State.Equals("completed", StringComparison.OrdinalIgnoreCase) ? "circle-check" : "circle-info";
    private string CurrentStateIconClass => Current.Available && Current.State.Equals("completed", StringComparison.OrdinalIgnoreCase) ? "text-teal-700" : "text-slate-500";
    private string DisplayTimeZoneId => _timelineSettings?.TimeZoneId ?? "Asia/Tokyo";
    private bool HasSelection => _selectedThreadIds.Count > 0;
    private int SelectedItemCount => SelectedItemIds().Count;
    private bool AllThreadsSelected => Threads.Count > 0 && Threads.All(thread => _selectedThreadIds.Contains(thread.ItemId));
    private string SelectionSummary => HasSelection ? $"{_selectedThreadIds.Count} 件選択中" : "未選択";
}
