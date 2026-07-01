using System.Globalization;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Index
{
    private IReadOnlyList<TimelineExportProductResult> ProductContributions =>
        (_store?.Products ?? [])
            .Where(product => product.EventCount > 0)
            .OrderByDescending(product => product.EventCount)
            .ToList();

    private bool RuntimeStatusKnown => _runtime?.Products.Count > 0;
    private bool WorkerStatusKnown => _worker is not null && !string.IsNullOrWhiteSpace(_worker.State);
    private bool LocalApiUnavailable =>
        _worker?.State.Equals("local_api_unreachable", StringComparison.OrdinalIgnoreCase) == true;
    private bool WorkerDockerUnavailable =>
        _worker?.State.Equals("docker_unavailable", StringComparison.OrdinalIgnoreCase) == true;
    private bool WorkerStatusUnreadable =>
        _worker?.State.Equals("unreadable", StringComparison.OrdinalIgnoreCase) == true;
    private int AvailableProductCount => RuntimeStatusKnown
        ? _runtime!.Products.Count(IsRuntimeProductAvailable)
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

    private static bool IsRuntimeProductAvailable(ProductRuntimeRow product) =>
        product.ProductFound
        && (product.ComposeFound || product.Id.Equals("pc", StringComparison.OrdinalIgnoreCase));

    private static bool IsRuntimeProductBroken(ProductRuntimeRow product) =>
        product.ProductFound
        && !product.ComposeFound
        && !product.Id.Equals("pc", StringComparison.OrdinalIgnoreCase);

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
}
