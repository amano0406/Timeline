using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class VideoFileDetail
{
    private const string VideoElementId = "video-detail-player";

    [SupplyParameterFromQuery(Name = "path")]
    public string SourcePath { get; set; } = "";

    private VideoFileDetailResult? _detail;
    private bool _loading = true;
    private string? _error;
    private double? _activeTurnStartSec;

    private bool HasVerbalizedTurns => _detail?.AudioVerbalizationResult.Turns.Count > 0;

    protected override async Task OnParametersSetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                _detail = new VideoFileDetailResult { Message = "動画ファイルが指定されていません。" };
                return;
            }

            _detail = await Timeline.GetVideoFileDetailAsync(SourcePath);
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

    private static string DisplayName(VideoFileRow file) =>
        !string.IsNullOrWhiteSpace(file.FileName) ? file.FileName : EmptyText(file.RelativePath);

    private static string VideoSourceUrl(VideoFileRow file) =>
        $"api/video/source?path={Uri.EscapeDataString(file.SourcePath)}";

    private async Task SeekAsync(double seconds)
    {
        _activeTurnStartSec = Math.Max(0, seconds);
        await JS.InvokeVoidAsync("timelineAudioPlayer.seek", VideoElementId, Math.Max(0, seconds));
    }

    private string ActiveTurnRowClass(double startSec) =>
        _activeTurnStartSec is not null && Math.Abs(_activeTurnStartSec.Value - startSec) < 0.01
            ? "bg-teal-50"
            : "";

    private static string DurationLabel(double? seconds) =>
        seconds is > 0 ? UiFormat.Duration(seconds.Value) : "-";

    private static string SpeakerLabel(string value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string ReadableText(AudioVerbalizedTurn turn) =>
        string.IsNullOrWhiteSpace(turn.Text) ? "文字起こしはありません。" : turn.Text;

    private static string ConfidenceLabel(double? value) =>
        value is null ? "-" : value.Value.ToString("0.000");

    private static string CountLabel(int count) =>
        count > 0 ? $"{count:N0} 件" : "-";

    private static string VerbalizationLabel(AudioVerbalizationStatus? status)
    {
        if (status?.Available != true)
        {
            return "-";
        }

        return status.State.ToLowerInvariant() switch
        {
            "completed" => $"{status.VerbalizedTurns:N0} / {status.TotalTurns:N0} 件",
            "source_transcript" => "補正前",
            "needs_review" => $"{status.VerbalizedTurns:N0} / {status.TotalTurns:N0} 件",
            "running" => "処理中",
            "queued" => "待機中",
            "failed" => "失敗",
            "stale" => "再処理待ち",
            "not_started" => "未取得",
            "planned" => "準備済み",
            _ => "未確認",
        };
    }

    private static string VerbalizationStateLabel(string state) =>
        state.ToLowerInvariant() switch
        {
            "completed" => "取得済み",
            "source_transcript" => "補正前",
            "running" => "取得中",
            "queued" => "待機中",
            "planned" => "準備済み",
            "needs_review" => "候補あり",
            "stale" => "再取得待ち",
            "failed" => "失敗",
            _ => "未取得",
        };

    private static string VerbalizationPill(string state) =>
        state.ToLowerInvariant() switch
        {
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "source_transcript" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "running" or "queued" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "planned" or "needs_review" or "stale" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
            "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };

    private static string VerbalizationIcon(string state) =>
        state.ToLowerInvariant() switch
        {
            "completed" => "circle-check",
            "source_transcript" => "file-lines",
            "running" => "spinner",
            "queued" => "clock",
            "planned" => "list-check",
            "needs_review" => "triangle-exclamation",
            "stale" => "arrows-rotate",
            "failed" => "circle-xmark",
            _ => "circle-minus",
        };

    private static string VerbalizedTurnLabel(string status) =>
        (status ?? "").ToLowerInvariant() switch
        {
            "confirmed" => "確認済み",
            "source_transcript" => "補正前",
            "candidate" or "needs_review" => "候補",
            "unresolved" => "未解決",
            "failed" => "失敗",
            _ => string.IsNullOrWhiteSpace(status) ? "候補" : status,
        };

    private static string VerbalizedTurnPill(string status) =>
        (status ?? "").ToLowerInvariant() switch
        {
            "confirmed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "candidate" or "needs_review" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };

    private static string StatusLabel(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "処理済み",
            "processing" => "処理中",
            "failed" => "失敗",
            _ => "未処理",
        };

    private static string StatusIcon(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "circle-check",
            "processing" => "spinner",
            "failed" => "triangle-exclamation",
            _ => "circle-minus",
        };

    private static string StatusPill(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "processing" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;
}
