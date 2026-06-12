using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Index
{
    private void BuildDashboard()
    {
        _alerts.Clear();
        _dataSources.Clear();

        AddSystemAlerts();
        AddSettingsAlerts();
        AddProcessingAlerts();
        AddScanUpdateAlerts();
        AddDataSources();
    }

    private void AddSystemAlerts()
    {
        if (WorkerStatusKnown && !string.Equals(_worker!.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            _alerts.Add(new(
                "danger",
                "Timeline worker が止まっています",
                "時間軸の状態確認に使う内部処理が動いていません。復旧を実行すると worker だけを起動し直します。",
                "worker を復旧",
                "",
                "worker-repair"));
        }

        if (RuntimeStatusKnown)
        {
            if (AvailableProductCount == 0)
            {
                _alerts.Add(new("warning", "製品が未インストールです", "製品管理から必要な製品をインストールしてください。", "製品管理を開く", "", "products"));
            }

            var brokenProducts = _runtime!.Products
                .Where(product => product.ProductFound && !product.ComposeFound)
                .Select(product => product.DisplayName)
                .Take(3)
                .ToList();

            if (brokenProducts.Count > 0)
            {
                _alerts.Add(new("warning", "確認が必要な製品があります", $"{string.Join("、", brokenProducts)} を確認してください。", "製品管理を開く", "", "products"));
            }
        }

        if (_store is null || !_store.Available)
        {
            _alerts.Add(new(
                "warning",
                "まだスキャンが完了していません",
                "スキャン画面で「スキャンを始める」を押すと、各製品の取り込み結果を集めて Timeline の時間軸を作成します。",
                "スキャンを始める",
                "scan",
                "link"));
            return;
        }

        if (ParseDateTime(_store.CreatedAt) is null)
        {
            _alerts.Add(new("info", "時間軸の最終構築日時を確認できません", "時間軸を再構築すると、現在の素材を反映できます。", "スキャンを開く", "scan", "link"));
        }
    }

    private void AddSettingsAlerts()
    {
        if (_audio is not null && _audio.ProductFound)
        {
            if (_audio.InputRoots.Count == 0 || _audio.OutputRoot is null)
            {
                _alerts.Add(new("warning", "音声の入出力設定を確認してください", "音声ファイルを取り込むためのディレクトリ設定が不足しています。", "設定を開く", "", "settings"));
            }
            if (!_audio.HasToken)
            {
                _alerts.Add(new("warning", "Hugging Face トークンが未設定です", "音声や動画の解析に必要なモデル利用条件を満たせない可能性があります。", "設定を開く", "", "settings"));
            }
        }

        if (_video is not null && _video.ProductFound)
        {
            if (_video.Settings.InputRoots.Count == 0 || !_video.Settings.OutputRoot.Exists)
            {
                _alerts.Add(new("warning", "動画の入出力設定を確認してください", "動画ファイルを取り込むためのディレクトリ設定が不足しています。", "設定を開く", "", "settings"));
            }
            if (!_video.Settings.HasToken)
            {
                _alerts.Add(new("warning", "動画用の Hugging Face トークンが未設定です", "動画内音声の解析に必要なモデル利用条件を満たせない可能性があります。", "設定を開く", "", "settings"));
            }
        }
    }

    private void AddProcessingAlerts()
    {
        if (!HasInstalledProducts)
        {
            return;
        }

        var bulkState = (_verbalizationBulk?.State ?? "").Trim().ToLowerInvariant();
        if (bulkState is "running" or "queued" or "starting")
        {
            _alerts.Add(new("info", "言語化を実行中です", _verbalizationBulk?.CurrentFileName is { Length: > 0 } fileName ? $"{fileName} を処理しています。" : "音声または動画を言語化しています。", "スキャンを開く", "scan", "link"));
        }
        else if ((_verbalizationTargets?.TargetCount ?? 0) > 0)
        {
            _alerts.Add(new("info", "言語化の検証対象があります", "現在は品質確認のため、音声1件・動画1件だけを処理する暫定モードです。", "スキャンを開く", "scan", "link"));
        }
    }

    private void AddScanUpdateAlerts()
    {
        if (_store?.Available != true || !HasInstalledProducts || _loadingDetails)
        {
            return;
        }

        var candidates = GetScanUpdateCandidates().ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var names = string.Join("、", candidates.Take(3).Select(candidate => candidate.Name));
        var remaining = candidates.Count - 3;
        var suffix = remaining > 0 ? $" ほか{remaining:N0}件" : "";
        var details = string.Join(" / ", candidates.Take(2).Select(candidate => candidate.Reason));
        _alerts.Add(new(
            "info",
            "スキャンで最新化できます",
            $"{names}{suffix} に未反映または未処理の素材があります。{details}",
            "スキャンを開く",
            "scan",
            "link"));
    }

    private IEnumerable<ScanUpdateCandidate> GetScanUpdateCandidates()
    {
        if (_audio is { ProductFound: true } audio)
        {
            var storeCount = StoreItemCount("audio", audio.AudioItemCount);
            if (audio.AudioFileCount > audio.AudioItemCount)
            {
                yield return new("音声ファイル", $"入力 {audio.AudioFileCount:N0} 件に対して処理済み {audio.AudioItemCount:N0} 件です。");
            }
            else if (audio.AudioItemCount != storeCount)
            {
                yield return new("音声ファイル", $"処理済み {audio.AudioItemCount:N0} 件に対して Timeline 反映済み {storeCount:N0} 件です。");
            }
        }

        if (_video is { ProductFound: true } video)
        {
            var storeCount = StoreItemCount("video", video.ItemCount);
            if (video.SourceFileCount > video.ItemCount)
            {
                yield return new("動画ファイル", $"入力 {video.SourceFileCount:N0} 件に対して処理済み {video.ItemCount:N0} 件です。");
            }
            else if (video.ItemCount != storeCount)
            {
                yield return new("動画ファイル", $"処理済み {video.ItemCount:N0} 件に対して Timeline 反映済み {storeCount:N0} 件です。");
            }
        }

        if (_image is { ProductFound: true } image)
        {
            var storeCount = StoreItemCount("image", image.ItemCount);
            if (image.SourceFileCount > image.ItemCount)
            {
                yield return new("画像ファイル", $"入力 {image.SourceFileCount:N0} 件に対して処理済み {image.ItemCount:N0} 件です。");
            }
            else if (image.ItemCount != storeCount)
            {
                yield return new("画像ファイル", $"処理済み {image.ItemCount:N0} 件に対して Timeline 反映済み {storeCount:N0} 件です。");
            }
        }

        if (_chatGpt is { ProductFound: true } chatGpt)
        {
            var storeCount = StoreItemCount("chatgpt", chatGpt.ItemCount);
            if (chatGpt.ItemCount != storeCount)
            {
                yield return new("ChatGPT", $"処理済み {chatGpt.ItemCount:N0} 件に対して Timeline 反映済み {storeCount:N0} 件です。");
            }
        }

        if (_windowsCodex is { ProductFound: true } windowsCodex)
        {
            var currentCount = windowsCodex.Current.ThreadCount;
            var storeCount = StoreItemCount("windows-codex", currentCount);
            if (currentCount != storeCount)
            {
                yield return new("Windows Codex", $"検出 {currentCount:N0} 件に対して Timeline 反映済み {storeCount:N0} 件です。");
            }
            else if (windowsCodex.Current.UpdateCounts.New > 0 || windowsCodex.Current.UpdateCounts.Changed > 0)
            {
                yield return new("Windows Codex", $"新規 {windowsCodex.Current.UpdateCounts.New:N0} 件、変更 {windowsCodex.Current.UpdateCounts.Changed:N0} 件があります。");
            }
        }

        if (_pc is { ProductFound: true } pc)
        {
            var storeCount = StoreItemCount("pc", pc.ItemCount);
            if (pc.ItemCount != storeCount)
            {
                yield return new("PC状態", $"処理済み {pc.ItemCount:N0} 件に対して Timeline 反映済み {storeCount:N0} 件です。");
            }
        }
    }

    private void AddDataSources()
    {
        if (!HasInstalledProducts)
        {
            return;
        }

        var audioStoreCount = StoreItemCount("audio", _audio?.AudioItemCount ?? 0);
        var videoStoreCount = StoreItemCount("video", _video?.ItemCount ?? 0);
        var imageStoreCount = StoreItemCount("image", _image?.ItemCount ?? 0);
        var chatGptStoreCount = StoreItemCount("chatgpt", _chatGpt?.ItemCount ?? 0);
        var windowsStoreCount = StoreItemCount("windows-codex", _windowsCodex?.Current.ThreadCount ?? 0);
        var windowsProcessedCount = WindowsCodexProcessedCount(_windowsCodex);
        var pcStoreCount = StoreItemCount("pc", _pc?.ItemCount ?? 0);

        _dataSources.Add(new("音声ファイル", "file-audio", "音声の取り込みと言語化候補", SourceState(_audio?.ProductFound == true || RuntimeProductFound("audio"), audioStoreCount), [
            new("対象", DetailSummaryText(_audio is null, FormatNumber(_audio?.AudioFileCount ?? 0))),
            new("処理済み", DetailSummaryText(_audio is null, FormatNumber(_audio?.AudioItemCount ?? 0))),
            new("Timeline", FormatNumber(audioStoreCount)),
            new("言語化", AudioVerbalizationSummaryText),
        ]));
        _dataSources.Add(new("動画ファイル", "video", "動画の取り込みと言語化候補", SourceState(_video?.ProductFound == true || RuntimeProductFound("video"), videoStoreCount), [
            new("対象", DetailSummaryText(_video is null, FormatNumber(_video?.SourceFileCount ?? 0))),
            new("処理済み", DetailSummaryText(_video is null, FormatNumber(_video?.ItemCount ?? 0))),
            new("Timeline", FormatNumber(videoStoreCount)),
            new("言語化", VideoVerbalizationSummaryText),
        ]));
        _dataSources.Add(new("画像ファイル", "image", "画像の取り込みとOCR候補", SourceState(_image?.ProductFound == true || RuntimeProductFound("image"), imageStoreCount), [
            new("対象", DetailSummaryText(_image is null, FormatNumber(_image?.SourceFileCount ?? 0))),
            new("処理済み", DetailSummaryText(_image is null, FormatNumber(_image?.ItemCount ?? 0))),
            new("Timeline", FormatNumber(imageStoreCount)),
            new("言語化", "対象外"),
        ]));
        _dataSources.Add(new("ChatGPT", "comments", "会話スレッドの取り込み", SourceState(_chatGpt?.ProductFound == true || RuntimeProductFound("chatgpt"), chatGptStoreCount), [
            new("入力候補", DetailSummaryText(_chatGpt is null, FormatNumber(_chatGpt?.ProcessableInputCount ?? 0))),
            new("処理済み", DetailSummaryText(_chatGpt is null, FormatNumber(_chatGpt?.ItemCount ?? 0))),
            new("Timeline", FormatNumber(chatGptStoreCount)),
            new("イベント", FormatNumber(StoreEventCount("chatgpt"))),
        ]));
        _dataSources.Add(new("Windows Codex", "terminal", "Codex スレッドの取り込み", SourceState(_windowsCodex?.ProductFound == true || RuntimeProductFound("windows-codex"), windowsStoreCount), [
            new("スレッド", DetailSummaryText(_windowsCodex is null, FormatNumber(_windowsCodex?.Current.ThreadCount ?? 0))),
            new("処理済み", DetailSummaryText(_windowsCodex is null, FormatNumber(windowsProcessedCount))),
            new("Timeline", FormatNumber(windowsStoreCount)),
            new("イベント", FormatNumber(StoreEventCount("windows-codex"))),
        ]));
        _dataSources.Add(new("PC状態", "desktop", "PC状態ログの取り込み", SourceState(_pc?.ProductFound == true || RuntimeProductFound("pc"), pcStoreCount), [
            new("対象", DetailSummaryText(_pc is null, FormatNumber(_pc?.ItemCount ?? 0))),
            new("処理済み", DetailSummaryText(_pc is null, FormatNumber(_pc?.ItemCount ?? 0))),
            new("Timeline", FormatNumber(pcStoreCount)),
            new("保存先", DetailSummaryText(_pc is null, _pc?.Settings.OutputRootReady == true ? "利用可" : "未設定")),
        ]));
    }

    private static int WindowsCodexProcessedCount(WindowsCodexOverview? overview)
    {
        if (overview is null)
        {
            return 0;
        }

        return overview.Current.RenderedThreadCount > 0
            ? overview.Current.RenderedThreadCount
            : overview.Current.ThreadCount;
    }
}
