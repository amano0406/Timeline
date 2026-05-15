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
        AddDataSources();
    }

    private void AddSystemAlerts()
    {
        if (_worker is null || !_worker.Available || !string.Equals(_worker.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            _alerts.Add(new("danger", "Timeline worker を確認してください", _worker?.Message ?? "Timeline worker の状態を取得できません。", "状態更新", "", "link"));
        }

        if (_runtime is not null)
        {
            if (AvailableProductCount == 0)
            {
                _alerts.Add(new("warning", "製品が未インストールです", "製品管理から必要な製品をインストールしてください。", "製品管理を開く", "", "products"));
            }

            var brokenProducts = _runtime.Products
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

        var createdAt = ParseDateTime(_store.CreatedAt);
        if (createdAt is null)
        {
            _alerts.Add(new("info", "時間軸の最終構築日時を確認できません", "時間軸を再構築すると、現在の素材を反映できます。", "スキャンを開く", "scan", "link"));
        }
        else if ((DateTimeOffset.Now - createdAt.Value).TotalDays >= 3)
        {
            _alerts.Add(new("warning", "時間軸が古くなっています", $"最後の構築から {AgeText(createdAt.Value)} 経過しています。", "スキャンを開く", "scan", "link"));
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
        var pcStoreCount = StoreItemCount("pc", _pc?.ItemCount ?? 0);

        _dataSources.Add(new("音声ファイル", "file-audio", "音声の取り込みと言語化候補", SourceState(_audio?.ProductFound ?? RuntimeProductFound("audio"), audioStoreCount), [
            new("対象", DetailSummaryText(_audio is null, FormatNumber(_audio?.AudioFileCount ?? 0))),
            new("処理済み", DetailSummaryText(_audio is null, FormatNumber(_audio?.AudioItemCount ?? 0))),
            new("Timeline", FormatNumber(audioStoreCount)),
            new("言語化", AudioVerbalizationSummaryText),
        ]));
        _dataSources.Add(new("動画ファイル", "video", "動画の取り込みと言語化候補", SourceState(_video?.ProductFound ?? RuntimeProductFound("video"), videoStoreCount), [
            new("対象", DetailSummaryText(_video is null, FormatNumber(_video?.SourceFileCount ?? 0))),
            new("処理済み", DetailSummaryText(_video is null, FormatNumber(_video?.ItemCount ?? 0))),
            new("Timeline", FormatNumber(videoStoreCount)),
            new("言語化", VideoVerbalizationSummaryText),
        ]));
        _dataSources.Add(new("画像ファイル", "image", "画像の取り込みとOCR候補", SourceState(_image?.ProductFound ?? RuntimeProductFound("image"), imageStoreCount), [
            new("対象", DetailSummaryText(_image is null, FormatNumber(_image?.SourceFileCount ?? 0))),
            new("処理済み", DetailSummaryText(_image is null, FormatNumber(_image?.ItemCount ?? 0))),
            new("Timeline", FormatNumber(imageStoreCount)),
            new("言語化", "対象外"),
        ]));
        _dataSources.Add(new("ChatGPT", "comments", "会話スレッドの取り込み", SourceState(_chatGpt?.ProductFound ?? RuntimeProductFound("chatgpt"), chatGptStoreCount), [
            new("入力候補", DetailSummaryText(_chatGpt is null, FormatNumber(_chatGpt?.ProcessableInputCount ?? 0))),
            new("処理済み", DetailSummaryText(_chatGpt is null, FormatNumber(_chatGpt?.ItemCount ?? 0))),
            new("Timeline", FormatNumber(chatGptStoreCount)),
            new("イベント", FormatNumber(StoreEventCount("chatgpt"))),
        ]));
        _dataSources.Add(new("Windows Codex", "terminal", "Codex スレッドの取り込み", SourceState(_windowsCodex?.ProductFound ?? RuntimeProductFound("windows-codex"), windowsStoreCount), [
            new("スレッド", DetailSummaryText(_windowsCodex is null, FormatNumber(_windowsCodex?.Current.ThreadCount ?? 0))),
            new("処理済み", DetailSummaryText(_windowsCodex is null, FormatNumber(_windowsCodex?.Current.RenderedThreadCount ?? 0))),
            new("Timeline", FormatNumber(windowsStoreCount)),
            new("イベント", FormatNumber(StoreEventCount("windows-codex"))),
        ]));
        _dataSources.Add(new("PC状態", "desktop", "PC状態ログの取り込み", SourceState(_pc?.ProductFound ?? RuntimeProductFound("pc"), pcStoreCount), [
            new("対象", DetailSummaryText(_pc is null, FormatNumber(_pc?.ItemCount ?? 0))),
            new("処理済み", DetailSummaryText(_pc is null, FormatNumber(_pc?.ItemCount ?? 0))),
            new("Timeline", FormatNumber(pcStoreCount)),
            new("保存先", DetailSummaryText(_pc is null, _pc?.Settings.OutputRootReady == true ? "利用可" : "未設定")),
        ]));
    }
}
