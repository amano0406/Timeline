using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Shared;

public partial class InitialProductSetup
{
    private const string SetupDismissedKey = "timeline.initialProductSetup.dismissedForSession";

    private ProductRuntimeOverview? _overview;
    private TimelineDockerWorkerStatus? _worker;
    private AudioVerbalizationOllamaStatus? _ollama;
    private bool _initialized;
    private bool _visible;
    private bool _installing;
    private string? _error;
    private string? _message;
    private readonly HashSet<string> _selectedProductIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _installStates = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DefaultSelectedProductIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio",
        "image",
        "video",
    };

    private IReadOnlyList<ProductRuntimeRow> Products => _overview?.Products ?? [];

    private IReadOnlyList<ProductRuntimeRow> InstallableProducts =>
        Products
            .Where(product =>
                product.SupportedOnCurrentOperatingSystem
                && !IsInstalled(product)
                && !string.IsNullOrWhiteSpace(product.SourceUrl))
            .ToList();

    private bool HasInstalledProducts => Products.Any(IsInstalled);

    private bool CanInstallSelected =>
        !_installing
        && InstallableProducts.Any(product => IsSelected(product.Id) && ProductInstallState(product.Id) != "done");

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _initialized)
        {
            return;
        }

        _initialized = true;
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var dismissed = await Js.InvokeAsync<string?>("sessionStorage.getItem", SetupDismissedKey);
            if (string.Equals(dismissed, "true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await LoadSetupStatusAsync();
            if (HasInstalledProducts || InstallableProducts.Count == 0)
            {
                return;
            }

            foreach (var product in InstallableProducts)
            {
                if (DefaultSelectedProductIds.Contains(product.Id))
                {
                    _selectedProductIds.Add(product.Id);
                }
            }

            _visible = true;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            _error = RuntimeDisplayText.ProductStatusLoadFailure(ex.Message);
            _visible = true;
            StateHasChanged();
        }
    }

    private async Task DismissAsync()
    {
        await Js.InvokeVoidAsync("sessionStorage.setItem", SetupDismissedKey, "true");
        _visible = false;
    }

    private async Task RefreshAsync()
    {
        _error = null;
        _message = null;

        try
        {
            await LoadSetupStatusAsync();
            _message = "状態を再確認しました。";
        }
        catch (Exception ex)
        {
            _error = RuntimeDisplayText.ProductStatusLoadFailure(ex.Message);
        }

        StateHasChanged();
    }

    private async Task LoadSetupStatusAsync()
    {
        var overviewTask = Timeline.GetProductRuntimeOverviewAsync();
        var workerTask = Timeline.GetTimelineWorkerStatusAsync();
        var ollamaTask = Timeline.GetAudioVerbalizationOllamaStatusAsync();

        _overview = await overviewTask;
        _worker = await workerTask;
        _ollama = await ollamaTask;
    }

    private async Task InstallSelectedAsync()
    {
        _installing = true;
        _error = null;
        _message = null;
        try
        {
            var targets = InstallableProducts
                .Where(product => IsSelected(product.Id) && ProductInstallState(product.Id) != "done")
                .ToList();

            if (targets.Count == 0)
            {
                _message = "インストールする製品を選択してください。";
                return;
            }

            foreach (var product in targets)
            {
                _installStates[product.Id] = "running";
                _message = $"{DisplayName(product)} をインストールしています。";
                StateHasChanged();

                try
                {
                    await Timeline.InstallProductAsync(
                        product.Id,
                        new ProductInstallRequest { RestoreSettingsBackup = true });
                    _installStates[product.Id] = "done";
                }
                catch (Exception ex)
                {
                    _installStates[product.Id] = "failed";
                    _error = RuntimeDisplayText.ProductActionFailure(DisplayName(product), "インストール", ex.Message);
                    break;
                }
            }

            _overview = await Timeline.GetProductRuntimeOverviewAsync();
            if (_installStates.Values.Any(state => state == "failed"))
            {
                _message = "失敗した製品があります。成功した製品はそのまま利用できます。";
                return;
            }

            await Js.InvokeVoidAsync("sessionStorage.setItem", SetupDismissedKey, "true");
            _message = "選択した製品をインストールしました。";
            _visible = false;
        }
        finally
        {
            _installing = false;
            StateHasChanged();
        }
    }

    private bool IsSelected(string productId) => _selectedProductIds.Contains(productId);

    private void SetSelected(string productId, ChangeEventArgs args)
    {
        var selected = args.Value is bool value
            ? value
            : string.Equals(Convert.ToString(args.Value), "true", StringComparison.OrdinalIgnoreCase);

        if (selected)
        {
            _selectedProductIds.Add(productId);
        }
        else
        {
            _selectedProductIds.Remove(productId);
        }
    }

    private string ProductInstallState(string productId) =>
        _installStates.TryGetValue(productId, out var state) ? state : "waiting";

    private static bool IsInstalled(ProductRuntimeRow product) =>
        product.ProductFound
        && (product.ComposeFound
            || product.Id.Equals("pc", StringComparison.OrdinalIgnoreCase)
            || !product.SupportedOnCurrentOperatingSystem);

    private static string DisplayName(ProductRuntimeRow product) =>
        string.IsNullOrWhiteSpace(product.DisplayName) ? product.Id : product.DisplayName;

    private static string ProductIcon(string productId) => productId switch
    {
        "audio" => "file-audio",
        "image" => "image",
        "video" => "video",
        "windows-codex" => "terminal",
        "chatgpt" => "comments",
        "pc" => "desktop",
        _ => "box",
    };

    private static string ProductDescription(string productId) => productId switch
    {
        "audio" => "音声ファイルを取り込み、時間軸で扱える形にします。",
        "image" => "画像ファイルを取り込み、後から確認・分析できる形にします。",
        "video" => "動画ファイルを取り込み、映像と音声を時間軸で扱える形にします。",
        "windows-codex" => "Windows Codex のスレッドを取り込みます。",
        "chatgpt" => "ChatGPT のエクスポートデータを取り込みます。",
        "pc" => "PC状態の記録を取り込みます。",
        _ => "Timeline で扱うデータを増やします。",
    };

    private static string InstallStateLabel(string state) => state switch
    {
        "running" => "処理中",
        "done" => "完了",
        "failed" => "失敗",
        _ => "未インストール",
    };

    private static string InstallStatePillClass(string state) => state switch
    {
        "running" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
        "done" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
        "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
        _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
    };

    private IReadOnlyList<SetupPrerequisiteRow> SetupPrerequisites
    {
        get
        {
            var rows = new List<SetupPrerequisiteRow>
            {
                new(
                    "Timeline 操作機能",
                    _overview is null ? "確認不可" : "接続済み",
                    _overview is null ? "failed" : "done",
                    _overview is null
                        ? "製品状態を取得できません。Timeline を起動し直してから、もう一度確認してください。"
                        : "画面から製品状態を確認できます。",
                    _overview is null
                        ? "Timeline を起動し直してから、この画面の「状態を再確認」を押してください。"
                        : "そのまま次の項目を確認してください。"),
            };

            rows.Add(new(
                "Docker / 処理基盤",
                DockerReady ? "利用可能" : "確認が必要",
                DockerReady ? "done" : "warning",
                DockerPrerequisiteMessage,
                DockerReady
                    ? "そのまま進められます。"
                    : "Docker Desktop を起動してから、この画面の「状態を再確認」を押してください。"));

            rows.Add(new(
                "Ollama / AIモデル",
                OllamaReady ? "利用可能" : "確認が必要",
                OllamaReady ? "done" : "warning",
                OllamaPrerequisiteMessage,
                OllamaReady
                    ? "そのまま進められます。"
                    : "Ollama を起動し、設定中のモデルを取得してから「状態を再確認」を押してください。"));

            rows.Add(new(
                "自動処理",
                WorkerReady ? "起動中" : "確認が必要",
                WorkerReady ? "done" : "warning",
                WorkerReady
                    ? "スキャンや時間軸作成に使う自動処理が動いています。"
                    : WorkerPrerequisiteMessage,
                WorkerReady
                    ? "そのまま進められます。"
                    : "状態更新または復旧を実行してから、この画面の「状態を再確認」を押してください。"));

            rows.Add(new(
                "サブ製品",
                HasInstalledProducts ? "準備済み" : "未インストール",
                HasInstalledProducts ? "done" : "warning",
                HasInstalledProducts
                    ? "既に使える製品があります。"
                    : "音声・画像・動画などを扱うには、下の一覧から使う製品をインストールしてください。",
                HasInstalledProducts
                    ? "必要に応じて製品管理画面で追加できます。"
                    : "下の一覧で使う製品を選び、「選択した製品をインストール」を押してください。"));

            return rows;
        }
    }

    private bool WorkerReady =>
        _worker?.Available == true
        && string.Equals(_worker.State, "running", StringComparison.OrdinalIgnoreCase);

    private bool DockerReady
    {
        get
        {
            if (_worker is null)
            {
                return false;
            }

            var state = (_worker.State ?? "").Trim();
            return !state.Equals("docker_unavailable", StringComparison.OrdinalIgnoreCase)
                && !state.Equals("local_api_unreachable", StringComparison.OrdinalIgnoreCase)
                && !state.Equals("unreadable", StringComparison.OrdinalIgnoreCase)
                && !state.Equals("unknown", StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool OllamaReady =>
        _ollama?.Available == true
        && _ollama.ModelAvailable;

    private string DockerPrerequisiteMessage
    {
        get
        {
            if (_worker is null)
            {
                return "Docker を使う処理基盤の状態をまだ確認できていません。";
            }

            var state = (_worker.State ?? "").Trim();
            if (state.Equals("docker_unavailable", StringComparison.OrdinalIgnoreCase))
            {
                return "Docker が起動していません。Docker を起動してから、もう一度状態を確認してください。";
            }

            if (state.Equals("local_api_unreachable", StringComparison.OrdinalIgnoreCase))
            {
                return "Timeline の操作機能に接続できないため、Docker の状態も確認できません。Timeline を起動し直してください。";
            }

            if (state.Equals("unreadable", StringComparison.OrdinalIgnoreCase)
                || state.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                return "Docker または自動処理の状態を読み取れません。状態更新または復旧を試してください。";
            }

            return "Docker を使う処理基盤を確認できます。";
        }
    }

    private string OllamaPrerequisiteMessage
    {
        get
        {
            if (_ollama is null)
            {
                return "Ollama の状態をまだ確認できていません。AIを使う処理の前に状態を確認してください。";
            }

            if (_ollama.Available && _ollama.ModelAvailable)
            {
                return string.IsNullOrWhiteSpace(_ollama.Model)
                    ? "Ollama が動いており、設定中のモデルを利用できます。"
                    : $"Ollama が動いており、{_ollama.Model} を利用できます。";
            }

            if (_ollama.Available)
            {
                return "Ollama は動いていますが、設定中のモデルが見つかりません。設定またはモデル取得を確認してください。";
            }

            return "Ollama に接続できません。AIを使う文字起こしや概要生成の前に、Ollama の起動を確認してください。";
        }
    }

    private string WorkerPrerequisiteMessage
    {
        get
        {
            var detail = RuntimeDisplayText.WorkerStatusDetail(_worker);
            return string.IsNullOrWhiteSpace(detail)
                ? "自動処理の状態を確認できません。状態更新または復旧を試してください。"
                : detail;
        }
    }

    private static string PrerequisiteStatePillClass(string state) => state switch
    {
        "done" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
        "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
        "warning" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-800",
        _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
    };

    private sealed record SetupPrerequisiteRow(
        string Name,
        string Label,
        string State,
        string Message,
        string NextAction);
}
