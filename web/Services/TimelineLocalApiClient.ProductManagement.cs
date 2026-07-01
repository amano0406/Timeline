using System.Net.Http.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineLocalApiClient
{
    public async Task<ProductRuntimeOverview> GetProductRuntimeOverviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ProductRuntimeOverview>(
                    "products/runtime/status",
                    JsonOptions,
                    cancellationToken)
                ?? new ProductRuntimeOverview { Message = "補助サーバーから状態を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load product runtime overview.");
            return _localStore.GetProductRuntimeOverviewFallback();
        }
    }

    public async Task<ProductRuntimeRow> RestartProductAsync(
        string productId,
        CancellationToken cancellationToken = default)
        => await PostProductRuntimeActionAsync(productId, "restart", cancellationToken);

    public async Task<ProductRuntimeRow> StartProductAsync(
        string productId,
        CancellationToken cancellationToken = default)
        => await PostProductRuntimeActionAsync(productId, "start", cancellationToken);

    public async Task<ProductRuntimeRow> StopProductAsync(
        string productId,
        CancellationToken cancellationToken = default)
        => await PostProductRuntimeActionAsync(productId, "stop", cancellationToken);

    public async Task<ProductRuntimeRow> InstallProductAsync(
        string productId,
        CancellationToken cancellationToken = default)
        => await PostProductRuntimeActionAsync(productId, "install", cancellationToken);

    public async Task<ProductRuntimeRow> InstallProductAsync(
        string productId,
        ProductInstallRequest request,
        CancellationToken cancellationToken = default)
        => await PostProductRuntimeActionAsync(productId, "install", request, cancellationToken);

    public async Task<ProductRuntimeRow> UpdateProductAsync(
        string productId,
        CancellationToken cancellationToken = default)
        => await PostProductRuntimeActionAsync(productId, "update", cancellationToken);

    public async Task<ProductUpdatePlan> GetProductUpdatePlanAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        return await _http.GetFromJsonAsync<ProductUpdatePlan>(
                $"products/runtime/{Uri.EscapeDataString(productId)}/update-plan",
                JsonOptions,
                cancellationToken)
            ?? new ProductUpdatePlan { ProductId = productId };
    }

    public async Task<ProductRuntimeRow> ApplyLatestProductUpdateArtifactAsync(
        string productId,
        ProductLatestUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var path = $"products/runtime/{Uri.EscapeDataString(productId)}/update-artifact/apply-latest";
        var response = await _http.PostAsJsonAsync(path, request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"更新を実行できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ProductRuntimeRow>(JsonOptions, cancellationToken)
            ?? new ProductRuntimeRow { Id = productId, Message = "更新しましたが、状態を読み取れませんでした。" };
    }

    public async Task<ProductUninstallPlan> GetProductUninstallPlanAsync(
        string productId,
        ProductUninstallRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"products/runtime/{Uri.EscapeDataString(productId)}/uninstall-plan",
            request,
            JsonOptions,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"アンインストール内容を確認できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ProductUninstallPlan>(JsonOptions, cancellationToken)
            ?? new ProductUninstallPlan { ProductId = productId };
    }

    public async Task<ProductRuntimeRow> UninstallProductAsync(
        string productId,
        ProductUninstallRequest request,
        CancellationToken cancellationToken = default)
        => await PostProductRuntimeActionAsync(productId, "uninstall", request, cancellationToken);

    private async Task<ProductRuntimeRow> PostProductRuntimeActionAsync(
        string productId,
        string action,
        CancellationToken cancellationToken)
        => await PostProductRuntimeActionAsync(productId, action, content: null, cancellationToken);

    private async Task<ProductRuntimeRow> PostProductRuntimeActionAsync(
        string productId,
        string action,
        object? content,
        CancellationToken cancellationToken)
    {
        var path = $"products/runtime/{Uri.EscapeDataString(productId)}/{Uri.EscapeDataString(action)}";
        HttpResponseMessage response;
        if (content is null)
        {
            response = await _http.PostAsync(path, content: null, cancellationToken);
        }
        else
        {
            response = await _http.PostAsJsonAsync(path, content, JsonOptions, cancellationToken);
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"製品操作を実行できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ProductRuntimeRow>(JsonOptions, cancellationToken)
            ?? new ProductRuntimeRow { Id = productId, Message = "製品操作は完了しましたが、状態を読み取れませんでした。" };
    }
}
