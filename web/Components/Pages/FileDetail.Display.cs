using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class FileDetail
{
    private static string DurationLabel(double? seconds) =>
        seconds is > 0 ? UiFormat.Duration(seconds.Value) : "-";

    private static string SpeakerLabel(string value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string TokenLabel(string value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string VerbalizedText(AudioVerbalizedTurn turn) =>
        string.IsNullOrWhiteSpace(turn.Text) ? "文字起こしはありません。" : turn.Text;

    private static bool HasUncertainTerms(AudioVerbalizedTurn turn) =>
        turn.UncertainTerms is { Count: > 0 };

    private static string UncertainTermsText(AudioVerbalizedTurn turn) =>
        turn.UncertainTerms is { Count: > 0 } ? string.Join("、", turn.UncertainTerms) : "";

    private string ActiveTurnRowClass(double startSec) =>
        _activeTurnStartSec is not null && Math.Abs(_activeTurnStartSec.Value - startSec) < 0.01
            ? "bg-teal-50"
            : "";

    private static string ConfidenceLabel(double? value) =>
        value is null ? "-" : value.Value.ToString("0.000");

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string VerbalizationLabel(string state) =>
        state.ToLowerInvariant() switch
        {
            "completed" => "取得済み",
            "source_transcript" => "補正前",
            "running" => "取得中",
            "queued" => "待機中",
            "planned" => "準備済み",
            "needs_review" => "候補あり",
            "stale" => "再取得必要",
            "failed" => "エラー",
            "unavailable" => "対象外",
            "unreadable" => "読込不可",
            "not_implemented" => "準備中",
            _ => "未取得",
        };

    private static string VerbalizationPill(string state) =>
        state.ToLowerInvariant() switch
        {
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "source_transcript" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "running" or "queued" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "planned" or "needs_review" or "stale" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
            "failed" or "unreadable" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            "not_implemented" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
            "unavailable" => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
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
            "failed" or "unreadable" => "circle-xmark",
            "not_implemented" => "triangle-exclamation",
            _ => "circle-minus",
        };

    private static string VerbalizationProgress(AudioVerbalizationStatus status)
    {
        if (status.UnresolvedTurns > 0 && status.TotalTurns > 0)
        {
            return $"{Math.Min(status.VerbalizedTurns, status.TotalTurns)} / {status.TotalTurns} 区間、未解決 {status.UnresolvedTurns}";
        }

        return status.TotalChunks > 0
            ? $"{Math.Min(status.CompletedChunks, status.TotalChunks)} / {status.TotalChunks} チャンク"
            : status.TotalTurns > 0
                ? $"{Math.Min(status.VerbalizedTurns, status.TotalTurns)} / {status.TotalTurns} 区間"
                : "-";
    }

    private static bool ShouldShowVerbalizationRemaining(AudioVerbalizationStatus status) =>
        status.EstimatedRemainingSec > 0
        && (
            status.State.Equals("running", StringComparison.OrdinalIgnoreCase)
            || status.State.Equals("planned", StringComparison.OrdinalIgnoreCase)
        );

    private static string VerbalizationDisplayMessage(AudioVerbalizationStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.Message))
        {
            return "";
        }
        if (status.Message.StartsWith("Ollama request failed.", StringComparison.OrdinalIgnoreCase))
        {
            return "Ollamaに接続できません。Ollamaを起動し、設定画面でモデルを確認してから再実行してください。";
        }
        if (status.Message.Equals("Audio verbalization completed.", StringComparison.OrdinalIgnoreCase))
        {
            return "文字起こしを取得しました。";
        }
        if (status.Message.Equals("Audio verbalization completed with unresolved turns.", StringComparison.OrdinalIgnoreCase)
            || status.Message.Equals("Audio verbalization has unresolved turns.", StringComparison.OrdinalIgnoreCase))
        {
            return "一部の区間を確認できませんでした。再実行で改善する場合があります。";
        }
        if (status.Message.Equals("Audio verbalization input signature changed.", StringComparison.OrdinalIgnoreCase))
        {
            return "元データまたは内部設定が変わったため、再取得が必要です。";
        }
        if (status.Message.Equals("Audio verbalization worker has been queued.", StringComparison.OrdinalIgnoreCase))
        {
            return "文字起こし処理を開始しました。準備ができると処理が進みます。";
        }
        if (status.Message.Equals("Audio verbalization is running.", StringComparison.OrdinalIgnoreCase))
        {
            return "文字起こし処理中です。";
        }
        if (status.Message.Equals("Source transcript text is available.", StringComparison.OrdinalIgnoreCase)
            || status.Message.Equals("Source transcript text is available for refinement.", StringComparison.OrdinalIgnoreCase))
        {
            return "サブ製品の文字起こし結果を表示しています。必要に応じて補正できます。";
        }
        return status.Message;
    }

    private static string VerbalizationMessageClass(string state) =>
        state.Equals("failed", StringComparison.OrdinalIgnoreCase)
            ? "mt-3 rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-800"
            : "mt-3 rounded-md border border-line bg-slate-50 p-3 text-sm text-slate-700";

    private static string VerbalizationStartMessage(AudioVerbalizationStatus status) =>
        status.State.Equals("planned", StringComparison.OrdinalIgnoreCase)
            ? $"文字起こし補正のチャンク計画を作成しました。{status.TotalChunks} チャンクを順番に処理する予定です。"
            : status.State.Equals("not_implemented", StringComparison.OrdinalIgnoreCase)
                ? "この音声はまだ文字起こしを開始できません。時間軸データを作成してから再度確認してください。"
                : string.IsNullOrWhiteSpace(VerbalizationDisplayMessage(status)) ? VerbalizationLabel(status.State) : VerbalizationDisplayMessage(status);

    private static string VerbalizedTurnLabel(string status) =>
        (status ?? "").ToLowerInvariant() switch
        {
            "confirmed" => "確認済み",
            "source_transcript" => "補正前",
            "candidate" => "候補",
            "needs_review" => "候補",
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
            _ => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
        };

    private static string AudioSourceUrl(AudioFileRow file) =>
        "api/audio/source"
        + $"?sourceId={Uri.EscapeDataString(file.SourceId)}"
        + $"&path={Uri.EscapeDataString(file.RelativePath)}";

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
