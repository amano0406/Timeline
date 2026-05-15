using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Shared;

public partial class InitialProductSetup
{
    private const string SetupDismissedKey = "timeline.initialProductSetup.dismissedForSession";

    private ProductRuntimeOverview? _overview;
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
            .Where(product => !IsInstalled(product) && !string.IsNullOrWhiteSpace(product.SourceUrl))
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

            _overview = await Timeline.GetProductRuntimeOverviewAsync();
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
            _error = ex.Message;
            _visible = true;
            StateHasChanged();
        }
    }

    private async Task DismissAsync()
    {
        await Js.InvokeVoidAsync("sessionStorage.setItem", SetupDismissedKey, "true");
        _visible = false;
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
                    _error = $"{DisplayName(product)} のインストールに失敗しました。{ex.Message}";
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
        && (product.ComposeFound || product.Id.Equals("pc", StringComparison.OrdinalIgnoreCase));

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
}
