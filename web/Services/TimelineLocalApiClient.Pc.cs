using System.Net.Http.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineLocalApiClient
{
    public async Task<PcOverview> GetPcOverviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<PcOverview>(
                    "products/pc/overview",
                    JsonOptions,
                    cancellationToken)
                ?? OfflinePcOverview("補助サーバーから状態を取得できませんでした。");
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load TimelineForPcInfo overview.");
            return _localStore.GetPcOverviewFallback();
        }
    }

    public async Task<PcItemListResult> GetPcItemsAsync(
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
        => await GetRequiredJsonAsync<PcItemListResult>(
            $"products/pc/items?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
            "PC状態一覧を取得できませんでした。",
            cancellationToken);

    public async Task<PcRefreshResult> RefreshPcAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync("products/pc/refresh", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"PC状態を取得できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<PcRefreshResult>(JsonOptions, cancellationToken)
            ?? new PcRefreshResult();
    }

    public async Task<PcItemsDownloadResult> DownloadPcItemsAsync(
        PcItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/pc/items/download", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"ダウンロードを作成できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<PcItemsDownloadResult>(JsonOptions, cancellationToken)
            ?? new PcItemsDownloadResult();
    }

    public async Task<PcOverview> SavePcSettingsAsync(
        PcSettingsSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/pc/settings", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"設定を保存できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<PcOverview>(JsonOptions, cancellationToken)
            ?? OfflinePcOverview("設定を保存しましたが、状態を読み取れませんでした。");
    }
}
