using System.Globalization;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Index
{
    private IReadOnlyList<DataSourceSummary> DataSources => _dataSources;
    private IReadOnlyList<TimelineExportProductResult> ProductContributions =>
        (_store?.Products ?? [])
            .Where(product => product.EventCount > 0)
            .OrderByDescending(product => product.EventCount)
            .ToList();

    private int TotalImportedItems => _store?.Available == true
        ? _store.ItemCount
        : (_audio?.AudioItemCount ?? 0)
            + (_video?.ItemCount ?? 0)
            + (_image?.ItemCount ?? 0)
            + (_windowsCodex?.Current.ThreadCount ?? 0)
            + (_chatGpt?.ItemCount ?? 0)
            + (_pc?.ItemCount ?? 0);

    private bool RuntimeStatusKnown => _runtime?.Products.Count > 0;
    private bool WorkerStatusKnown => _worker?.Available == true;
    private int TotalProductCount => RuntimeStatusKnown ? _runtime!.Products.Count : (_store?.ProductCount ?? _store?.Products.Count ?? 0);
    private int AvailableProductCount => RuntimeStatusKnown
        ? _runtime!.Products.Count(product => product.ProductFound && product.ComposeFound)
        : (_store?.Products.Count(product => product.Included || product.ItemCount > 0 || product.EventCount > 0) ?? 0);
    private bool HasInstalledProducts => RuntimeStatusKnown ? AvailableProductCount > 0 : _store?.Available == true && AvailableProductCount > 0;
    private bool HasDashboardStats => _dashboardStats?.Available == true && _dashboardStats.DailyItems.Count > 0;
    private bool HasProductDashboardStats => _dashboardStats?.Available == true && _dashboardStats.ProductTotals.Count > 0;
    private int SummaryPendingItems => Math.Max(0, (_dashboardStats?.SummaryTargetItems ?? 0) - (_dashboardStats?.SummaryCompletedItems ?? 0) - (_dashboardStats?.SummaryFailedItems ?? 0));
    private double SummaryCompletionPercent => (_dashboardStats?.SummaryTargetItems ?? 0) <= 0
        ? 0
        : Math.Clamp((_dashboardStats?.SummaryCompletedItems ?? 0) / (double)_dashboardStats!.SummaryTargetItems * 100, 0, 100);
    private string DashboardStatsGeneratedAt => FormatDateTime(_dashboardStats?.GeneratedAt);
    private string DashboardBucketLabel => _dashboardStats?.Bucket switch
    {
        "week" => "週ごと",
        "month" => "月ごと",
        _ => "日ごと",
    };
    private string DashboardRangeDateLabel
    {
        get
        {
            var from = FormatDate(_dashboardStats?.From);
            var to = FormatDate(_dashboardStats?.To);
            if (from.Length == 0 && to.Length == 0)
            {
                return "全期間";
            }
            if (from.Length == 0)
            {
                return $"{to} まで";
            }
            if (to.Length == 0)
            {
                return $"{from} から";
            }
            return $"{from} - {to}";
        }
    }

    private int AlertLevel => _alerts.Any(alert => alert.Severity == "danger")
        ? 2
        : _alerts.Count > 0 ? 1 : 0;

    private string DashboardHeadline => AlertLevel switch
    {
        2 => "対応が必要です",
        1 => "確認した方がよい項目があります",
        _ => "Timeline は利用できます",
    };

    private string DashboardMessage => AlertLevel switch
    {
        2 => "設定不足や製品未検出など、先に解消した方がよい項目があります。",
        1 => "データは利用できますが、スキャンや言語化など確認した方がよい項目があります。",
        _ => "大きな問題は見つかっていません。下のグラフで蓄積状況を確認できます。",
    };

    private string DashboardIcon => AlertLevel switch
    {
        2 => "triangle-exclamation",
        1 => "circle-info",
        _ => "circle-check",
    };

    private string DashboardToneClass => AlertLevel switch
    {
        2 => "border-red-400",
        1 => "border-amber-400",
        _ => "border-teal-500",
    };

    private string DashboardIconToneClass => AlertLevel switch
    {
        2 => "text-red-700",
        1 => "text-amber-700",
        _ => "text-teal-700",
    };

    private DashboardAlert? PrimaryAlert => _alerts.FirstOrDefault();

    private string NextActionTitle => PrimaryAlert?.Title
        ?? (_loadingDetails ? "素材の状態を確認中" : "現時点で優先タスクはありません");
    private string NextActionText => PrimaryAlert?.Message
        ?? (_loadingDetails
            ? "各サブ製品の件数と Timeline への反映状況を確認しています。"
            : "未反映の素材や停止中の処理は見つかっていません。必要なときだけスキャン画面から手動で更新できます。");
    private string NextActionButton => PrimaryAlert?.ActionLabel ?? (_loadingDetails ? "確認中" : "");
    private string NextActionHref => PrimaryAlert?.Href ?? "scan";
    private string NextActionKind => PrimaryAlert?.ActionKind ?? (_loadingDetails ? "loading" : "none");

    private string VerbalizationStateLabel
    {
        get
        {
            if (_loadingDetails && _verbalizationTargets is null)
            {
                return "確認中";
            }

            var state = (_verbalizationBulk?.State ?? "").Trim().ToLowerInvariant();
            if (state is "running" or "queued" or "starting")
            {
                return "処理中";
            }
            if ((_verbalizationTargets?.TargetCount ?? 0) > 0)
            {
                return "品質検証モード";
            }
            return "未処理なし";
        }
    }

    private string VerbalizationTargetDisplay =>
        _loadingDetails && _verbalizationTargets is null
            ? "確認中"
            : FormatNumber(_verbalizationTargets?.TargetCount ?? 0);

    private string AudioVerbalizationSummaryText =>
        DetailSummaryText(_audio is null, $"{_audio?.AudioVerbalizedFileCount ?? 0} / {_audio?.AudioVerbalizationTargetFileCount ?? 0}");

    private string VideoVerbalizationSummaryText =>
        DetailSummaryText(_video is null, $"{_video?.AudioVerbalizedFileCount ?? 0} / {_video?.AudioVerbalizationTargetFileCount ?? 0}");

    private string DetailSummaryText(bool detailMissing, string value) =>
        _loadingDetails && detailMissing ? "確認中" : value;

    private bool RuntimeProductFound(string productId) =>
        RuntimeStatusKnown
            ? _runtime!.Products.Any(product =>
                string.Equals(product.Id, productId, StringComparison.OrdinalIgnoreCase)
                && product.ProductFound
                && product.ComposeFound)
            : _store?.Products.Any(product =>
                string.Equals(product.ProductId, productId, StringComparison.OrdinalIgnoreCase)
                && (product.Included || product.ItemCount > 0 || product.EventCount > 0)) == true;

    private static string SourceState(bool productFound, int itemCount)
    {
        if (!productFound)
        {
            return "missing";
        }
        return itemCount > 0 ? "ready" : "empty";
    }

    private static string SourceStateClass(DataSourceSummary source) => source.State switch
    {
        "missing" => "border-red-200 bg-red-50 text-red-800",
        "empty" => "border-amber-200 bg-amber-50 text-amber-800",
        _ => "border-teal-200 bg-teal-50 text-teal-800",
    };

    private static string AlertClass(string severity) => severity switch
    {
        "danger" => "border-red-200 bg-red-50 text-red-900",
        "warning" => "border-amber-200 bg-amber-50 text-amber-900",
        _ => "border-sky-200 bg-sky-50 text-sky-900",
    };

    private static string AlertIcon(string severity) => severity switch
    {
        "danger" => "triangle-exclamation",
        "warning" => "circle-exclamation",
        _ => "circle-info",
    };

    private static string FormatNumber(int value) => value.ToString("N0", CultureInfo.GetCultureInfo("ja-JP"));

    private static string FormatNumber(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("ja-JP"));

    private static string FormatCompactNumber(long value)
    {
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        var absolute = Math.Abs(value);
        if (absolute >= 100_000_000)
        {
            return $"{value / 100_000_000d:0.#}億";
        }
        if (absolute >= 10_000)
        {
            return $"{value / 10_000d:0.#}万";
        }
        return value.ToString("N0", culture);
    }

    private int StoreEventCount(string productId)
    {
        return _store?.Products.FirstOrDefault(product => string.Equals(product.ProductId, productId, StringComparison.OrdinalIgnoreCase))?.EventCount ?? 0;
    }

    private int StoreItemCount(string productId, int fallback)
    {
        if (_store?.Available == true)
        {
            return _store.Products.FirstOrDefault(product => string.Equals(product.ProductId, productId, StringComparison.OrdinalIgnoreCase))?.ItemCount ?? 0;
        }
        return fallback;
    }

    private static string FormatDateTime(string? value)
    {
        var date = ParseDateTime(value);
        return date is null ? "未取得" : date.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatDate(string? value)
    {
        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static DateTimeOffset? ParseDateTime(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date
            : null;
    }

    private int ContributionWidth(int eventCount)
    {
        var max = ProductContributions.Count == 0 ? 0 : ProductContributions.Max(product => product.EventCount);
        if (max <= 0)
        {
            return 0;
        }
        return Math.Clamp((int)Math.Round(eventCount / (double)max * 100), 3, 100);
    }

    private string ContributionBarStyle(int eventCount) =>
        $"width: {ContributionWidth(eventCount)}%;";

    private static bool HasAlertAction(DashboardAlert alert) =>
        !string.IsNullOrWhiteSpace(alert.ActionLabel)
        && !string.Equals(alert.ActionKind, "none", StringComparison.OrdinalIgnoreCase);

    private static string AlertActionHref(DashboardAlert alert)
    {
        if (!string.IsNullOrWhiteSpace(alert.Href))
        {
            return alert.Href;
        }

        return alert.ActionKind switch
        {
            "settings" => "timeline/settings",
            "products" => "timeline/products",
            _ => "scan",
        };
    }

    private static string DisplayProductName(string productId, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }
        return productId switch
        {
            "audio" => "TimelineForAudio",
            "video" => "TimelineForVideo",
            "image" => "TimelineForImage",
            "chatgpt" => "TimelineForChatGPT",
            "windows-codex" => "TimelineForWindowsCodex",
            "pc" => "TimelineForPcInfo",
            _ => productId,
        };
    }

    private sealed record DashboardAlert(string Severity, string Title, string Message, string ActionLabel, string Href, string ActionKind);

    private sealed record ScanUpdateCandidate(string Name, string Reason);

    private sealed record DataSourceSummary(
        string Name,
        string Icon,
        string Description,
        string State,
        IReadOnlyList<DataSourceMetric> Metrics)
    {
        public string StateLabel => State switch
        {
            "missing" => "未検出",
            "empty" => "未取得",
            _ => "取得済み",
        };
    }

    private sealed record DataSourceMetric(string Label, string Value);
}
