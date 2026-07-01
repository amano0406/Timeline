using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Index
{
    private void BuildDashboard()
    {
        _alerts.Clear();

        AddSystemAlerts();
        AddSettingsAlerts();
        AddProcessingAlerts();
        AddScanUpdateAlerts();
    }

    private void AddSystemAlerts()
    {
        if (LocalApiUnavailable)
        {
            _alerts.Add(new(
                "danger",
                "Timeline の操作機能に接続できません",
                "状態確認や復旧操作に必要な機能が応答していません。Timeline を起動し直してください。",
                "スキャンを開く",
                "scan",
                "link"));
        }
        else if (WorkerDockerUnavailable)
        {
            _alerts.Add(new(
                "danger",
                "Docker が起動していません",
                "Timeline の自動処理を動かすための Docker が停止しています。復旧を実行すると、Docker と Timeline の自動処理の起動を試します。",
                "Dockerと自動処理を復旧",
                "",
                "worker-repair"));
        }
        else if (WorkerStatusUnreadable)
        {
            _alerts.Add(new(
                "danger",
                "自動処理の状態を確認できません",
                "Docker または worker の状態を確認できません。復旧を実行すると、Docker と worker の起動を試します。",
                "復旧を試す",
                "",
                "worker-repair"));
        }
        else if (WorkerStatusKnown && !string.Equals(_worker!.State, "running", StringComparison.OrdinalIgnoreCase))
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
                .Where(IsRuntimeProductBroken)
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
            _alerts.Add(new("info", "言語化待ちの素材があります", "音声由来イベントをより扱いやすい文字情報に整える対象があります。スキャン画面から言語化を進められます。", "スキャンを開く", "scan", "link"));
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

}
