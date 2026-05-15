using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class AudioFiles
{
    private static bool IsActiveRun(AudioRunProgress? run) =>
        run is not null
        && (
            run.State.Equals("running", StringComparison.OrdinalIgnoreCase)
            || run.State.Equals("processing", StringComparison.OrdinalIgnoreCase)
            || run.State.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || run.State.Equals("queued", StringComparison.OrdinalIgnoreCase)
        );

    private static string ProgressBarStyle(double percent) =>
        $"width:{Math.Clamp(percent, 0, 100):0.##}%";

    private static int ProgressAriaValue(double percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100));

    private static string ProgressLabel(double percent) =>
        $"{Math.Clamp(percent, 0, 100):0.#}%";

    private static string RunStateLabel(string state) =>
        state.ToLowerInvariant() switch
        {
            "running" or "processing" => "分析中",
            "pending" or "queued" => "待機中",
            _ => "確認中",
        };

    private static string RunStatePill(string state) =>
        state.ToLowerInvariant() switch
        {
            "running" or "processing" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "pending" or "queued" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };

    private static bool ShouldShowStage(AudioRunProgress run)
    {
        if (string.IsNullOrWhiteSpace(run.CurrentStage))
        {
            return false;
        }

        return !(
            run.State.Equals("pending", StringComparison.OrdinalIgnoreCase)
            && run.CurrentStage.Equals("queued", StringComparison.OrdinalIgnoreCase)
        );
    }

    private static string StageLabel(string stage) =>
        stage.ToLowerInvariant() switch
        {
            "queued" => "キュー投入済み",
            "preflight" => "準備中",
            "manifest" => "対象確認中",
            "extract_audio" => "音声準備中",
            "speech_candidates" or "detect_speech_candidates" => "発話区間検出中",
            "diarize" or "diarize_audio" => "話者分離中",
            "extract_acoustic_units" => "音響単位抽出中",
            "finalize" or "generate_artifacts" => "保存中",
            "llm_export" => "書き出し中",
            "completed" => "完了",
            "failed" => "失敗",
            "canceled" => "中止",
            "" => "確認中",
            _ => stage,
        };

    private static string RunDurationLabel(double seconds) =>
        seconds > 0 ? UiFormat.Duration(seconds) : "-";
}
