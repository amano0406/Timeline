using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineIndex
{
    private string CurrentScanPhaseTitle
    {
        get
        {
            if (AudioVerbalizationActive || _verbalizing)
            {
                return "文字起こし補正";
            }

            return RebuildStageGroup(_workerStatus?.Stage ?? "") switch
            {
                "collect" => "製品データの取り込み",
                "normalize" => "Timeline 形式への正規化",
                "publish" => "時間軸の整理と保存",
                _ when _overview?.Available == true => "完了",
                _ => "開始待ち",
            };
        }
    }

    private string CurrentScanPhaseDetail
    {
        get
        {
            if (AudioVerbalizationActive || _verbalizing)
            {
                return AudioVerbalizationStepDetail;
            }

            return RebuildStageGroup(_workerStatus?.Stage ?? "") switch
            {
                "collect" => ScanPhaseDetail("collect"),
                "normalize" => ScanPhaseDetail("normalize"),
                "publish" => ScanPhaseDetail("publish"),
                _ when _overview?.Available == true => $"{EventCountLabel}を時間軸に反映済みです。",
                _ => "スキャンを始めると、各製品を順番に確認します。",
            };
        }
    }

    private TimelineProductJobStatus? ActiveProductJob => _workerStatus?.ProductJob;

    private bool ShouldShowProductJobProgress =>
        RebuildActive && ActiveProductJob is not null && !string.IsNullOrWhiteSpace(ActiveProductJob.JobId);

    private string ProductJobTitle
    {
        get
        {
            var job = ActiveProductJob;
            if (job is null)
            {
                return "";
            }

            return $"{ProductJobProductLabel(job)} の処理状況";
        }
    }

    private string ProductJobStatusLine
    {
        get
        {
            var job = ActiveProductJob;
            if (job is null)
            {
                return "";
            }

            return FormatProductJobStatusLine(job);
        }
    }

    private static string FormatProductJobStatusLine(TimelineProductJobStatus job)
    {
        var state = ProductJobStateLabel(job.State);
        var stage = ProductJobStageLabel(job.Stage);
        var count = ProductJobProgressCountLabel(job.Progress);
        var message = string.IsNullOrWhiteSpace(job.Message) ? "" : $" / {job.Message}";
        return $"{state} / {stage}{count}{message}";
    }

    private static string ProductJobProductLabel(TimelineProductJobStatus job)
    {
        if (job.ProductId.Equals("video", StringComparison.OrdinalIgnoreCase)
            || job.ProductName.Equals("TimelineForVideo", StringComparison.OrdinalIgnoreCase))
        {
            return "動画ファイル";
        }

        return string.IsNullOrWhiteSpace(job.ProductName) ? job.ProductId : job.ProductName;
    }

    private static string ProductJobStateLabel(string state) =>
        state.ToLowerInvariant() switch
        {
            "queued" => "待機中",
            "running" => "処理中",
            "completed" => "完了",
            "completed_with_errors" => "確認あり",
            "failed" => "失敗",
            _ => string.IsNullOrWhiteSpace(state) ? "未確認" : state,
        };

    private static string ProductJobStageLabel(string stage) =>
        stage.ToLowerInvariant() switch
        {
            "queued" => "開始待ち",
            "start" => "準備",
            "sample" => "フレーム抽出",
            "frame_ocr" => "画面内テキスト解析",
            "audio" => "音声解析",
            "activity" => "活動解析",
            "refresh" => "記録更新",
            "completed" => "完了",
            "failed" => "失敗",
            _ => string.IsNullOrWhiteSpace(stage) ? "処理中" : stage,
        };

    private static string ProductJobProgressCountLabel(TimelineProductJobProgress progress)
    {
        if (progress.Total <= 0)
        {
            return "";
        }

        var unit = progress.Unit.Equals("files", StringComparison.OrdinalIgnoreCase) ? "件" : progress.Unit;
        return $" / {progress.Current:N0} / {progress.Total:N0} {unit}";
    }

    private bool ScanFailed =>
        string.Equals(_workerStatus?.State, "failed", StringComparison.OrdinalIgnoreCase);

    private string ScanFailurePhaseTitle =>
        RebuildStageGroup(_workerStatus?.Stage ?? "") switch
        {
            "collect" => "製品データ取得",
            "normalize" => "正規化",
            "publish" => "時間軸保存",
            _ => "確認できません",
        };

    private string ScanFailureCause
    {
        get
        {
            var error = (_workerStatus?.Error ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(error))
            {
                return error;
            }

            return string.IsNullOrWhiteSpace(_error)
                ? "エラー原因を取得できませんでした。"
                : _error;
        }
    }

    private string ScanFailureHelp
    {
        get
        {
            var cause = ScanFailureCause;
            if (cause.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || cause.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                return "サブ製品の処理が長時間返らず、Timeline 側の待機上限に到達しました。サブ製品側で進捗や失敗理由を返せるようにすると、原因をより絞り込めます。";
            }

            if (cause.Contains("is not running", StringComparison.OrdinalIgnoreCase))
            {
                return "対象のサブ製品が起動していない可能性があります。製品管理画面で起動状態を確認してください。";
            }

            if (cause.Contains("Product API failed", StringComparison.OrdinalIgnoreCase)
                || cause.Contains("API failed", StringComparison.OrdinalIgnoreCase))
            {
                return "サブ製品がエラーを返しました。サブ製品側の API が error または message を返していれば、ここに表示されます。";
            }

            return "詳細が必要な場合は、対象サブ製品のログまたは Timeline の操作ログを確認してください。";
        }
    }

    private IReadOnlyList<ScanPhaseProgressItem> RemainingScanPhaseItems =>
        ScanPhaseProgressItems
            .Where(item => ScanPhaseState(item.Phase) is "running" or "waiting")
            .ToList();

    private string ScanPhaseState(string phase)
    {
        var normalizedPhase = phase.Trim().ToLowerInvariant();
        if (AudioVerbalizationActive || _verbalizing)
        {
            return normalizedPhase == "verbalize" ? "running" : "completed";
        }

        if (string.Equals(_workerStatus?.State, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var failedGroup = RebuildStageGroup(_workerStatus?.Stage ?? "");
            return normalizedPhase == failedGroup ? "failed" : "waiting";
        }

        var group = RebuildStageGroup(_workerStatus?.Stage ?? "");
        if (RebuildActive)
        {
            return normalizedPhase switch
            {
                "collect" => group == "collect" ? "running" : "completed",
                "normalize" => group == "normalize" ? "running" : group == "publish" ? "completed" : "waiting",
                "publish" => group == "publish" ? "running" : "waiting",
                "verbalize" => "waiting",
                _ => "waiting",
            };
        }

        if (_overview?.Available == true)
        {
            if (normalizedPhase is "collect" or "normalize" or "publish")
            {
                return "completed";
            }

            return AudioVerbalizationStepState;
        }

        return "waiting";
    }

    private string ScanPhasePillClass(string phase) => StepPillClass(ScanPhaseState(phase));

    private string ScanPhaseRemainingItemClass(string phase) =>
        ScanPhaseState(phase) == "running"
            ? "tfa-scan-remaining-item tfa-scan-remaining-item-current"
            : "tfa-scan-remaining-item";

    private string ScanPhaseStateLabel(string phase) =>
        ScanPhaseState(phase) switch
        {
            "running" => "処理中",
            "completed" => "完了",
            "failed" => "失敗",
            "review" => "確認あり",
            _ => "待機",
        };

    private string ScanPhaseDetail(string phase)
    {
        var normalizedPhase = phase.Trim().ToLowerInvariant();
        var productName = ProductNameFromWorkerMessage(_workerStatus?.Message ?? "");
        var productSuffix = string.IsNullOrWhiteSpace(productName) ? "" : $"：{productName}";
        var stage = (_workerStatus?.Stage ?? "").Trim().ToLowerInvariant();

        if (normalizedPhase == "collect")
        {
            if (ScanPhaseState(phase) == "running")
            {
                return stage switch
                {
                    "preparing" => "作業場所を準備しています。",
                    "refreshing" => $"サブ製品の最新データを取り込んでいます{productSuffix}。",
                    "downloading" => $"サブ製品の出力データを取得しています{productSuffix}。",
                    _ => "各サブ製品を順番に確認しています。",
                };
            }

            return ScanPhaseState(phase) == "completed"
                ? "サブ製品から必要なデータを取得しました。"
                : "各サブ製品を順番に確認します。";
        }

        if (normalizedPhase == "normalize")
        {
            if (ScanPhaseState(phase) == "running")
            {
                return $"取得したデータを Timeline で扱える形式に整えています{productSuffix}。";
            }

            return ScanPhaseState(phase) == "completed"
                ? "取得データを Timeline 形式へ変換しました。"
                : "取り込み後に Timeline 形式へ変換します。";
        }

        if (normalizedPhase == "publish")
        {
            if (ScanPhaseState(phase) == "running")
            {
                return stage == "sorting"
                    ? "時間順に並べています。"
                    : "画面で使う時間軸として保存しています。";
            }

            return ScanPhaseState(phase) == "completed"
                ? $"{EventCountLabel}を時間軸に反映済みです。"
                : "正規化後に時間軸へ反映します。";
        }

        if (normalizedPhase == "verbalize")
        {
            if (RebuildActive)
            {
                return "時間軸作成後に、音声・動画の文字起こしを補正します。";
            }

            return AudioVerbalizationStepDetail;
        }

        return "";
    }

    private static string StepPillClass(string state) =>
        state.ToLowerInvariant() switch
        {
            "running" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "review" or "completed_with_errors" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };

    private static string RebuildStageGroup(string stage) =>
        stage.Trim().ToLowerInvariant() switch
        {
            "queued" or "preparing" or "collecting" or "refreshing" or "downloading" => "collect",
            "importing" => "normalize",
            "sorting" or "publishing" => "publish",
            _ => "",
        };

    private static string WorkerStatusLabel(TimelineWorkerJobStatus? status)
    {
        if (status is null)
        {
            return "自動処理を開始しています。";
        }

        var state = status.State switch
        {
            "queued" => "待機中",
            "running" => "処理中",
            "completed" => "完了",
            "failed" => "失敗",
            _ => status.State,
        };
        var message = status.Stage switch
        {
            "queued" => "開始待ちです。",
            "preparing" => "作業場所を準備しています。",
            "refreshing" => $"サブ製品の最新データを取り込んでいます{ProductNameSuffix(status.Message)}。",
            "collecting" => "各プロダクトの API からデータを取得しています。",
            "downloading" => $"サブ製品の出力データを取得しています{ProductNameSuffix(status.Message)}。",
            "importing" => $"取得したデータを Timeline 形式へ整えています{ProductNameSuffix(status.Message)}。",
            "sorting" => "時間順に並べています。",
            "publishing" => "画面で使う時間軸へ反映しています。",
            "completed" => "自動処理が完了しました。",
            "failed" => "自動処理に失敗しました。",
            _ => "自動処理を進めています。",
        };
        return $"{state}: {message}";
    }

    private static string ProductNameSuffix(string message)
    {
        var productName = ProductNameFromWorkerMessage(message);
        return string.IsNullOrWhiteSpace(productName) ? "" : $"：{productName}";
    }

    private static string ProductNameFromWorkerMessage(string message)
    {
        var text = message ?? "";
        if (text.Contains("TimelineForAudio", StringComparison.OrdinalIgnoreCase))
        {
            return "音声ファイル";
        }
        if (text.Contains("TimelineForImage", StringComparison.OrdinalIgnoreCase))
        {
            return "画像ファイル";
        }
        if (text.Contains("TimelineForVideo", StringComparison.OrdinalIgnoreCase))
        {
            return "動画ファイル";
        }
        if (text.Contains("TimelineForWindowsCodex", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows Codex";
        }
        if (text.Contains("TimelineForChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            return "ChatGPT";
        }
        if (text.Contains("TimelineForPC", StringComparison.OrdinalIgnoreCase))
        {
            return "PC状態";
        }

        return "";
    }

    private sealed record ScanPhaseProgressItem(string Phase, string Label, string Icon);

    private static readonly ScanPhaseProgressItem[] ScanPhaseProgressItems =
    [
        new("collect", "製品データ取得", "folder-open"),
        new("normalize", "正規化", "shuffle"),
        new("publish", "時間軸保存", "timeline"),
        new("verbalize", "文字起こし補正", "language"),
    ];

    private static string RunDurationLabel(double seconds) =>
        seconds > 0 ? UiFormat.Duration(seconds) : "-";

    private static string ProgressBarStyle(double percent) =>
        $"width:{Math.Clamp(percent, 0, 100):0.##}%";

    private static int ProgressAriaValue(double percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100));

    private static string ProgressLabel(double percent) =>
        $"{Math.Clamp(percent, 0, 100):0.#}%";
}
