using System.Net.Http.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineHelperClient
{
    public async Task<AudioModelInventoryResult> GetImageModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AudioModelInventoryResult>(
                    "products/image/models",
                    JsonOptions,
                    cancellationToken)
                ?? OfflineModels("画像モデル一覧を取得できませんでした。");
        }
        catch (Exception ex)
        {
            LogOptionalHelperReadFailure(ex, "Failed to load TimelineForImage model inventory.");
            return OfflineModels("TimelineForImage API からモデル一覧を取得できませんでした。");
        }
    }

    public async Task<ImageOverview> GetImageOverviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ImageOverview>(
                    "products/image/overview",
                    JsonOptions,
                    cancellationToken)
                ?? OfflineImageOverview("補助サーバーから状態を取得できませんでした。");
        }
        catch (Exception ex)
        {
            LogOptionalHelperReadFailure(ex, "Failed to load TimelineForImage overview.");
            return _localStore.GetImageOverviewFallback();
        }
    }

    public async Task<ImageItemListResult> GetImageItemsAsync(
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
        => await GetRequiredJsonAsync<ImageItemListResult>(
            $"products/image/items?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
            "画像の生成物一覧を取得できませんでした。",
            cancellationToken);

    public async Task<ImageFileListResult> GetImageFilesAsync(
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
        => await GetRequiredJsonAsync<ImageFileListResult>(
            $"products/image/files?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
            "画像ファイル一覧を取得できませんでした。",
            cancellationToken);

    public async Task<ImageFileDetailResult> GetImageFileDetailAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"products/image/files/detail?path={Uri.EscapeDataString(sourcePath)}";
            return await _http.GetFromJsonAsync<ImageFileDetailResult>(
                    url,
                    JsonOptions,
                    cancellationToken)
                ?? new ImageFileDetailResult { Message = "画像詳細を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalHelperReadFailure(ex, "Failed to load TimelineForImage file detail.");
            return new ImageFileDetailResult { Message = "画像詳細を取得できませんでした。" };
        }
    }

    public async Task<ImageRefreshResult> RefreshImageAsync(
        ImageRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/image/refresh", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"更新を実行できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ImageRefreshResult>(JsonOptions, cancellationToken)
            ?? new ImageRefreshResult();
    }

    public async Task<ImageItemsDownloadResult> DownloadImageItemsAsync(
        ImageItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/image/items/download", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"ダウンロードを作成できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ImageItemsDownloadResult>(JsonOptions, cancellationToken)
            ?? new ImageItemsDownloadResult();
    }

    public async Task<TimelineThreadItemsDeleteResult> DeleteImageItemsAsync(
        ImageItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/image/items/delete-generated", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"生成物を削除できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineThreadItemsDeleteResult>(JsonOptions, cancellationToken)
            ?? new TimelineThreadItemsDeleteResult();
    }

    public async Task<ImageOverview> SaveImageSettingsAsync(
        ImageSettingsSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/image/settings", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"設定を保存できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ImageOverview>(JsonOptions, cancellationToken)
            ?? OfflineImageOverview("設定を保存しましたが、状態を読み取れませんでした。");
    }
}
