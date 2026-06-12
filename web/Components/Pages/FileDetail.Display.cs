using System.Globalization;
using Timeline.Web.Components.Shared;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class FileDetail
{
    private const double MinSilenceDisplayGapSec = 1.0;

    private static string DurationLabel(double? seconds) =>
        seconds is > 0 ? UiFormat.Duration(seconds.Value) : "-";

    private IReadOnlyList<AudioTranscriptDisplayRow> TranscriptRows => BuildTranscriptRows();

    private static IReadOnlyList<TextVolumeChartPoint> BuildTextVolumePoints(
        IReadOnlyList<AudioTranscriptDisplayRow> rows,
        double? durationSec) =>
        TextVolumeChartBuilder.Build(
            rows
                .Where(row => !row.IsSilence && row.Turn is not null)
                .Select(row => new TextVolumeSegment(
                    row.StartSec,
                    row.EndSec,
                    row.Turn!.Text)),
            durationSec);

    private IReadOnlyList<AudioTranscriptDisplayRow> BuildTranscriptRows()
    {
        var turns = _verbalizationResult?.Turns
            .OrderBy(turn => turn.StartSec)
            .ThenBy(turn => turn.EndSec)
            .ToList() ?? [];
        if (turns.Count == 0)
        {
            return [];
        }

        var sourceTurns = _detail?.Turns
            .Where(turn => turn.EndSec > turn.StartSec)
            .ToList() ?? [];
        var coverageStart = sourceTurns.Count > 0
            ? Math.Min(sourceTurns.Min(turn => turn.StartSec), turns[0].StartSec)
            : turns[0].StartSec;
        var coverageEnd = sourceTurns.Count > 0
            ? Math.Max(sourceTurns.Max(turn => turn.EndSec), turns[^1].EndSec)
            : turns[^1].EndSec;

        var rows = new List<AudioTranscriptDisplayRow>();
        var cursor = coverageStart;
        foreach (var turn in turns)
        {
            if (turn.StartSec - cursor >= MinSilenceDisplayGapSec)
            {
                rows.Add(NewSilenceRow(cursor, turn.StartSec));
            }

            rows.Add(new AudioTranscriptDisplayRow
            {
                Turn = turn,
                SourceTurn = SourceTurnFor(turn),
                StartSec = turn.StartSec,
                EndSec = Math.Max(turn.EndSec, turn.StartSec),
            });
            cursor = Math.Max(cursor, turn.EndSec);
        }

        if (coverageEnd - cursor >= MinSilenceDisplayGapSec)
        {
            rows.Add(NewSilenceRow(cursor, coverageEnd));
        }

        return rows;
    }

    private static AudioTranscriptDisplayRow NewSilenceRow(double startSec, double endSec) =>
        new()
        {
            IsSilence = true,
            StartSec = Math.Max(0, startSec),
            EndSec = Math.Max(startSec, endSec),
        };

    private static string SpeakerLabel(string value) =>
        FormatSpeakerLabel(value);

    private static string TranscriptSpeakerLabel(AudioTranscriptDisplayRow row) =>
        row.IsSilence ? "話者なし" : SpeakerLabel(row.Turn?.Speaker ?? "");

    private static string TranscriptTextLabel(AudioTranscriptDisplayRow row) =>
        row.IsSilence ? "無音" : row.Turn is null ? "" : VerbalizedText(row.Turn);

    private static string TranscriptBasisLabel(AudioTranscriptDisplayRow row) =>
        row.IsSilence ? "音声なし" : row.Turn is null ? "-" : BasisText(row.Turn);

    private static string TranscriptStatusLabel(AudioTranscriptDisplayRow row) =>
        row.IsSilence ? "無音" : row.Turn is null ? "-" : VerbalizedTurnLabel(row.Turn.Status);

    private static string TranscriptStatusPill(AudioTranscriptDisplayRow row) =>
        row.IsSilence
            ? "tfa-status-pill border-slate-200 bg-slate-50 text-slate-600"
            : row.Turn is null
                ? "tfa-status-pill border-slate-200 bg-slate-50 text-slate-600"
                : VerbalizedTurnPill(row.Turn.Status);

    private static string FormatSpeakerLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            return "不明";
        }

        const string prefix = "SPEAKER_";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value[prefix.Length..], CultureInfo.InvariantCulture, out var speakerIndex))
        {
            return $"話者 {speakerIndex + 1}";
        }

        return value;
    }

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

    private bool IsTurnPlaying(double startSec) =>
        _audioPlaying
        && _activeTurnStartSec is not null
        && Math.Abs(_activeTurnStartSec.Value - startSec) < 0.01;

    private string PlaybackIcon(double startSec) =>
        IsTurnPlaying(startSec) ? "pause" : "play";

    private string PlaybackTitle(double startSec) =>
        IsTurnPlaying(startSec) ? "一時停止" : "この位置から再生";

    private string PlaybackButtonClass(double startSec) =>
        IsTurnPlaying(startSec)
            ? "tfa-icon-box tfa-turn-playback-active"
            : "tfa-icon-box text-accent";

    private static string TurnStartDataValue(double startSec) =>
        startSec.ToString("R", CultureInfo.InvariantCulture);

    private string TranscriptRowClass(AudioTranscriptDisplayRow row)
    {
        var activeClass = ActiveTurnRowClass(row.StartSec);
        if (!row.IsSilence)
        {
            return activeClass;
        }

        return string.IsNullOrEmpty(activeClass)
            ? "tfa-transcript-silence-row"
            : $"tfa-transcript-silence-row {activeClass}";
    }

    private static string TranscriptRangeLabel(AudioTranscriptDisplayRow row) =>
        $"{TranscriptStartLabel(row.StartSec)} - {TranscriptEndLabel(row.StartSec, row.EndSec)}";

    private static string TranscriptStartLabel(double seconds) =>
        WholeSecondDurationLabel(DisplayStartSecond(seconds));

    private static string TranscriptEndLabel(double startSec, double endSec)
    {
        var startSecond = DisplayStartSecond(startSec);
        var endSecond = Math.Max(startSecond, (int)Math.Ceiling(Math.Max(startSec, endSec)) - 1);
        return WholeSecondDurationLabel(endSecond);
    }

    private static int DisplayStartSecond(double seconds)
    {
        if (seconds <= 0)
        {
            return 0;
        }

        var floor = Math.Floor(seconds);
        return Math.Abs(seconds - floor) < 0.001
            ? (int)floor
            : (int)Math.Ceiling(seconds);
    }

    private static string WholeSecondDurationLabel(int seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static string ConfidenceLabel(double? value) =>
        value is null ? "-" : value.Value.ToString("0.000");

    private static string MetricLabel(double? value) =>
        value is null ? "-" : value.Value.ToString("0.000");

    private static string NumberLabel(double? value) =>
        value is null ? "-" : value.Value.ToString("0.###");

    private static string IntLikeLabel(double? value) =>
        value is null ? "-" : value.Value.ToString("0");

    private static string RangeLabel(AudioObservationRange range) =>
        range.Count <= 0 || range.Min is null || range.Max is null
            ? "-"
            : $"{range.Min.Value:0.000} - {range.Max.Value:0.000}";

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string StatusValueLabel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.ToLowerInvariant() switch
            {
                "ok" => "正常",
                "completed" => "完了",
                "failed" => "失敗",
                "running" => "処理中",
                _ => value,
            };

    private static string LanguageValueLabel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.ToLowerInvariant() switch
            {
                "ja" or "ja-jp" => "日本語",
                "en" or "en-us" => "英語",
                _ => value,
            };

    private static string DeviceValueLabel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.ToLowerInvariant() switch
            {
                "cuda" => "GPU（CUDA）",
                "cpu" => "CPU",
                _ => value,
            };

    private static string RecordedAtSourceLabel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.ToLowerInvariant() switch
            {
                "metadata" => "メタデータ",
                "filename" => "ファイル名",
                _ => value,
            };

    private AudioTimelineTurn? SourceTurnFor(AudioVerbalizedTurn turn) =>
        _detail?.Turns.FirstOrDefault(sourceTurn => sourceTurn.Index == turn.Index);

    private static string SourceTextLabel(AudioTimelineTurn? turn) =>
        string.IsNullOrWhiteSpace(turn?.Text) ? "-" : turn.Text;

    private static bool ShouldShowSourceText(AudioTimelineTurn? sourceTurn, AudioVerbalizedTurn turn) =>
        !string.IsNullOrWhiteSpace(sourceTurn?.Text)
        && !SameTranscriptText(sourceTurn.Text, turn.Text);

    private static string SourceToRefinedClass(AudioTimelineTurn? sourceTurn, AudioVerbalizedTurn turn) =>
        SameTranscriptText(sourceTurn?.Text, turn.Text)
            ? "text-slate-900"
            : "text-slate-900 font-medium";

    private static bool SameTranscriptText(string? sourceText, string? refinedText) =>
        string.Equals(sourceText?.Trim(), refinedText?.Trim(), StringComparison.Ordinal);

    private sealed class AudioTranscriptDisplayRow
    {
        public AudioVerbalizedTurn? Turn { get; init; }
        public AudioTimelineTurn? SourceTurn { get; init; }
        public double StartSec { get; init; }
        public double EndSec { get; init; }
        public bool IsSilence { get; init; }
    }

    private static string BasisText(AudioVerbalizedTurn turn) =>
        turn.Basis is { Count: > 0 }
            ? string.Join(" / ", turn.Basis.Select(BasisLabel).Where(label => !string.IsNullOrWhiteSpace(label)).Distinct())
            : "-";

    private static string BasisLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = value.Trim();
        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("source text is readable") || lower.Contains("sourcetext is clear"))
        {
            return "元の文字起こしが明瞭";
        }
        if (lower.Contains("punctuation"))
        {
            return "句読点を補正";
        }
        if (lower.Contains("spacing"))
        {
            return "読みやすさを調整";
        }
        if (lower.Contains("asr error") || lower.Contains("transcription error"))
        {
            return "音声認識の誤りを補正";
        }
        if (lower.Contains("phonetic"))
        {
            return "音の近さを根拠に補正";
        }
        if (lower.Contains("uncertainterms"))
        {
            return "要確認語あり";
        }
        if (lower.Contains("no_strong_text_hint"))
        {
            return "強い補正候補なし";
        }
        if (lower.Contains("same_as_source"))
        {
            return "元の文字起こしと同じ";
        }
        if (lower.Contains("empty_source"))
        {
            return "元の文字起こしなし";
        }

        return normalized.Any(ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
            ? "補正根拠あり"
            : normalized;
    }

    private int MaxSpeakerTurnCount =>
        _detail?.Observation.Speakers.Count > 0
            ? Math.Max(1, _detail.Observation.Speakers.Max(speaker => speaker.TurnCount))
            : 1;

    private string SpeakerBarStyle(AudioObservationSpeaker speaker, int index)
    {
        var percent = Math.Clamp(speaker.TurnCount / (double)MaxSpeakerTurnCount * 100, 2, 100);
        return $"width: {percent:0.#}%; background: {SpeakerColor(index)}";
    }

    private static string SpeakerColor(int index) => index switch
    {
        0 => "#0f766e",
        1 => "#0ea5e9",
        2 => "#f59e0b",
        _ => "#94a3b8",
    };

    private static string VerbalizationLabel(string state) =>
        state.ToLowerInvariant() switch
        {
            "completed" => "取得済み",
            "source_transcript" => "補正前",
            "running" => "取得中",
            "queued" => "待機中",
            "planned" => "準備済み",
            "needs_review" => "候補あり",
            "stalled" => "停止の可能性",
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
            "planned" or "needs_review" or "stale" or "stalled" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
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
            "needs_review" or "stalled" => "triangle-exclamation",
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

    private static double VerbalizationProgressPercent(AudioVerbalizationStatus status) =>
        Math.Clamp(
            status.ProgressPercent > 0
                ? status.ProgressPercent
                : status.TotalChunks > 0
                    ? status.CompletedChunks / (double)status.TotalChunks * 100
                    : status.TotalTurns > 0
                        ? status.VerbalizedTurns / (double)status.TotalTurns * 100
                        : 0,
            0,
            100);

    private static int VerbalizationProgressAriaValue(AudioVerbalizationStatus status) =>
        (int)Math.Round(VerbalizationProgressPercent(status));

    private static string VerbalizationProgressBarClass(AudioVerbalizationStatus status) =>
        status.State.Equals("stalled", StringComparison.OrdinalIgnoreCase)
            ? "h-full rounded-full bg-amber-500 transition-all"
            : status.State.Equals("failed", StringComparison.OrdinalIgnoreCase)
                ? "h-full rounded-full bg-red-500 transition-all"
                : "h-full rounded-full bg-teal-600 transition-all";

    private static string VerbalizationProgressBarStyle(AudioVerbalizationStatus status) =>
        $"width: {VerbalizationProgressPercent(status):0.##}%;";

    private static string VerbalizationCurrentChunkLabel(AudioVerbalizationStatus status) =>
        status.TotalChunks > 0
            ? $"{status.CurrentChunkId} / {status.TotalChunks} チャンク"
            : status.CurrentChunkId;

    private static string VerbalizationLastActivityLabel(AudioVerbalizationStatus status) =>
        status.LastActivitySec > 0 ? $"{UiFormat.Duration(status.LastActivitySec)} 前" : "-";

    private static string VerbalizationRuntimeLabel(AudioVerbalizationStatus status)
    {
        if (status.State.Equals("stalled", StringComparison.OrdinalIgnoreCase))
        {
            return "実行プロセスなし";
        }

        if (!IsActiveVerbalizationState(status.State))
        {
            return "-";
        }

        return status.ActiveJob ? "実行中" : "実行プロセスなし";
    }

    private static bool IsActiveVerbalizationState(string? state) =>
        (state ?? "").ToLowerInvariant() is "running" or "queued" or "planned" or "starting";

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
        if (status.Message.StartsWith("Audio verbalization appears stopped.", StringComparison.OrdinalIgnoreCase)
            || status.State.Equals("stalled", StringComparison.OrdinalIgnoreCase))
        {
            return "処理は停止している可能性が高いです。実行プロセスが見つからず、結果ファイルも更新されていません。必要であれば再実行してください。";
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
            : state.Equals("stalled", StringComparison.OrdinalIgnoreCase)
                ? "mt-3 rounded-md border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900"
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
