using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineIndex
{
    private static string ProductIcon(string productId) => productId switch
    {
        "audio" => "file-audio",
        "windows-codex" => "terminal",
        "chatgpt" => "comments",
        "image" => "image",
        "video" => "video",
        "pc" => "desktop",
        _ => "box",
    };

    private static string ProductPillClass(string productId) => productId switch
    {
        "audio" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
        "windows-codex" => "tfa-status-pill border-slate-300 bg-slate-50 text-slate-700",
        "chatgpt" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
        "image" => "tfa-status-pill border-emerald-200 bg-emerald-50 text-emerald-800",
        "video" => "tfa-status-pill border-indigo-200 bg-indigo-50 text-indigo-800",
        "pc" => "tfa-status-pill border-cyan-200 bg-cyan-50 text-cyan-900",
        _ => "tfa-status-pill border-line bg-slate-50 text-slate-700",
    };

    private TimelineExportProductResult? StoreProduct(string productId) =>
        _overview?.Products.FirstOrDefault(product =>
            product.ProductId.Equals(productId, StringComparison.OrdinalIgnoreCase));

    private bool IsInstalledProduct(string productId) =>
        _runtime?.Products.Any(product =>
            product.Id.Equals(productId, StringComparison.OrdinalIgnoreCase)
            && product.ProductFound
            && (product.ComposeFound || product.Id.Equals("pc", StringComparison.OrdinalIgnoreCase))) == true;

    private string MaterialProductStatusLabel(string productId)
    {
        if (_overview?.Available != true)
        {
            return "未確認";
        }

        var product = StoreProduct(productId);
        if (product is null)
        {
            return "未反映";
        }

        if (product.Included)
        {
            return $"{product.ItemCount:N0} 件";
        }

        if (product.ItemCount <= 0 && product.EventCount <= 0)
        {
            return "対象なし";
        }

        return "未反映";
    }

    private string MaterialProductPillClass(string productId)
    {
        if (_overview?.Available != true)
        {
            return "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700";
        }

        var product = StoreProduct(productId);
        return "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700";
    }

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private sealed record MaterialProductLink(
        string ProductId,
        string Label,
        string Icon,
        string Href,
        string Description);

    private static readonly MaterialProductLink[] AllMaterialProductLinks =
    [
        new("audio", "音声ファイル", "file-audio", "audio/files", "音声の取り込みと補正状態を確認"),
        new("video", "動画ファイル", "video", "video", "動画の取り込みと補正状態を確認"),
        new("image", "画像ファイル", "image", "image", "画像の取り込み状態を確認"),
        new("chatgpt", "ChatGPT", "comments", "chatgpt", "ChatGPT スレッドの取り込み状態を確認"),
        new("windows-codex", "Windows Codex", "terminal", "windows-codex", "Codex スレッドの取り込み状態を確認"),
        new("pc", "PC状態", "desktop", "pc", "PC状態の取り込み結果を確認"),
    ];
}
