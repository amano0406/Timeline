
namespace Timeline.Web.Components.Pages;

public partial class AudioFiles
{
    private static string StatusLabel(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "処理済み",
            "queued" => "待機中",
            "processing" => "処理中",
            "changed" => "再処理必要",
            "settings_changed" => "設定変更あり",
            "failed" => "エラー",
            _ => "未処理",
        };

    private static string StatusPill(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "queued" or "processing" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            "changed" or "settings_changed" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };

    private static string StatusIcon(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "circle-check",
            "queued" => "clock",
            "processing" => "spinner",
            "changed" or "settings_changed" => "triangle-exclamation",
            "failed" => "circle-xmark",
            _ => "circle-minus",
        };
}
