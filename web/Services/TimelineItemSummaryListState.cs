using System.Globalization;

namespace Timeline.Web.Services;

public sealed class TimelineItemSummaryListState
{
    private const int MaxParallelRequests = 6;
    private readonly Dictionary<string, TimelineItemSummary> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Clear()
    {
        lock (_gate)
        {
            _summaries.Clear();
            _loading.Clear();
        }
    }

    public async Task LoadAsync(
        TimelineHelperClient timeline,
        string product,
        IEnumerable<string?> itemIds,
        CancellationToken cancellationToken = default)
    {
        var targets = itemIds
            .Select(item => (item ?? string.Empty).Trim())
            .Where(item => !string.IsNullOrEmpty(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var itemId in targets)
            {
                _loading.Add(itemId);
            }
        }

        using var throttler = new SemaphoreSlim(MaxParallelRequests);
        var tasks = targets.Select(async itemId =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var summary = await timeline.GetTimelineItemSummaryAsync(product, itemId, cancellationToken);
                lock (_gate)
                {
                    _summaries[itemId] = summary;
                }
            }
            finally
            {
                throttler.Release();
                lock (_gate)
                {
                    _loading.Remove(itemId);
                }
            }
        });

        await Task.WhenAll(tasks);
    }

    public string SummaryStatusLabel(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return "-";
        }

        lock (_gate)
        {
            if (_loading.Contains(itemId))
            {
                return "確認中";
            }

            if (!_summaries.TryGetValue(itemId, out var summary))
            {
                return "未確認";
            }

            if (IsFailed(summary))
            {
                return "失敗";
            }

            return summary.Available && HasSummaryText(summary) ? "作成済み" : "未作成";
        }
    }

    public string SummaryStatusPillClass(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700";
        }

        lock (_gate)
        {
            if (_loading.Contains(itemId))
            {
                return "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800";
            }

            if (!_summaries.TryGetValue(itemId, out var summary))
            {
                return "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700";
            }

            if (IsFailed(summary))
            {
                return "tfa-status-pill border-red-200 bg-red-50 text-red-800";
            }

            return summary.Available && HasSummaryText(summary)
                ? "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800"
                : "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700";
        }
    }

    public string TextCharCountLabel(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return "-";
        }

        lock (_gate)
        {
            if (_loading.Contains(itemId))
            {
                return "確認中";
            }

            if (!_summaries.TryGetValue(itemId, out var summary) || !summary.Available)
            {
                return "-";
            }

            var count = summary.Compression.SourceChars > 0
                ? summary.Compression.SourceChars
                : summary.Source.ReadableCharCount;
            return count > 0 ? $"{count.ToString("N0", CultureInfo.CurrentCulture)} 文字" : "-";
        }
    }

    private static bool HasSummaryText(TimelineItemSummary summary) =>
        !string.IsNullOrWhiteSpace(summary.BriefSummary)
        || !string.IsNullOrWhiteSpace(summary.CompressedSummary);

    private static bool IsFailed(TimelineItemSummary summary) =>
        summary.State.Equals("failed", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(summary.Error);
}
