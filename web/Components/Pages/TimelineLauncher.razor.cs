using System.Globalization;
using Timeline.Web.Components.Shared;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineLauncher
{
    private TimelineRuntimeStatus? _status;
    private ProductManagementModal? _productManagement;
    private bool _loading = true;
    private bool _repairingWorker;
    private bool _confirmStopTimeline;
    private bool _stoppingTimeline;
    private string? _error;
    private string? _message;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        _message = null;
        try
        {
            _status = await Timeline.GetTimelineRuntimeStatusAsync();
            if (_productManagement is not null)
            {
                await _productManagement.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RepairWorkerAsync()
    {
        if (_repairingWorker)
        {
            return;
        }

        _repairingWorker = true;
        _error = null;
        _message = "自動処理を復旧しています。Docker の起動に少し時間がかかる場合があります。";
        try
        {
            var result = await Timeline.RepairTimelineWorkerAsync();
            _message = string.IsNullOrWhiteSpace(result.Message)
                ? "自動処理の復旧を実行しました。"
                : result.Message;
            _status = await Timeline.GetTimelineRuntimeStatusAsync();
            if (_productManagement is not null)
            {
                await _productManagement.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _message = null;
        }
        finally
        {
            _repairingWorker = false;
        }
    }

    private async Task StopTimelineAsync()
    {
        if (_stoppingTimeline)
        {
            return;
        }

        if (!_confirmStopTimeline)
        {
            _confirmStopTimeline = true;
            _message = "もう一度押すと Timeline を停止します。停止後はこの画面も開けなくなります。";
            _error = null;
            return;
        }

        _stoppingTimeline = true;
        _error = null;
        _message = "Timeline の停止を開始しています。再起動するときは Timeline Launcher を使ってください。";
        try
        {
            var result = await Timeline.StopTimelineAsync();
            _message = string.IsNullOrWhiteSpace(result.Message)
                ? "Timeline の停止を開始しました。"
                : result.Message;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _message = null;
            _confirmStopTimeline = false;
        }
        finally
        {
            _stoppingTimeline = false;
        }
    }

    private string StopTimelineButtonText
    {
        get
        {
            if (_stoppingTimeline)
            {
                return "停止中";
            }

            return _confirmStopTimeline ? "もう一度押して停止" : "Timelineを停止";
        }
    }

    private static bool HasComponentAction(TimelineRuntimeComponentStatus component) =>
        !string.IsNullOrWhiteSpace(component.ActionKind)
        && !string.IsNullOrWhiteSpace(component.ActionLabel);

    private static string RuntimeOverallTitle(string severity) => severity switch
    {
        "danger" => "対応が必要です",
        "warning" => "確認が必要です",
        _ => "利用できます",
    };

    private static string RuntimeSeverityLabel(string severity) => severity switch
    {
        "danger" => "要復旧",
        "warning" => "確認",
        _ => "正常",
    };

    private static string RuntimeSeverityIcon(string severity) => severity switch
    {
        "danger" => "triangle-exclamation",
        "warning" => "circle-exclamation",
        _ => "circle-check",
    };

    private static string RuntimeComponentIcon(TimelineRuntimeComponentStatus component) => component.Kind switch
    {
        "web" => "globe",
        "local-api" => "plug",
        "docker" => "server",
        "worker" => "gears",
        "ollama" => "brain",
        "products" => "boxes-stacked",
        _ => RuntimeSeverityIcon(component.Severity),
    };

    private static string RuntimeStateLabel(TimelineRuntimeComponentStatus component)
    {
        var state = component.State.Trim().ToLowerInvariant();
        return state switch
        {
            "running" => "稼働中",
            "stopped" => "停止中",
            "missing" => "未検出",
            "stale" => "停止中",
            "unreadable" => "未確認",
            "unknown" => "未確認",
            "model_missing" => "モデル未取得",
            "partial" => "一部停止",
            "broken" => "不完全",
            "not_installed" => "未準備",
            _ => RuntimeSeverityLabel(component.Severity),
        };
    }

    private static string RuntimePillClass(string severity) => severity switch
    {
        "danger" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
        "warning" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
        _ => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
    };

    private static string RuntimeIconBoxClass(string severity) => severity switch
    {
        "danger" => "border-red-200 bg-red-50 text-red-800",
        "warning" => "border-amber-200 bg-amber-50 text-amber-900",
        _ => "border-teal-200 bg-teal-50 text-teal-800",
    };

    private static string RuntimeComponentClass(string severity) => severity switch
    {
        "danger" => "border-red-200 bg-red-50 text-red-900",
        "warning" => "border-amber-200 bg-amber-50 text-amber-900",
        _ => "border-line bg-white text-slate-800",
    };

    private static string FormatDateTime(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "未取得";
    }
}
