using System.Net.Http.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineHelperClient
{
    public async Task<VideoOverview> GetVideoOverviewAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = forceRefresh ? "products/video/overview?refresh=1" : "products/video/overview";
            return await _http.GetFromJsonAsync<VideoOverview>(
                    url,
                    JsonOptions,
                    cancellationToken)
                ?? OfflineVideoOverview("補助サーバーから状態を取得できませんでした。");
        }
        catch (Exception ex)
        {
            LogOptionalHelperReadFailure(ex, "Failed to load TimelineForVideo overview.");
            return _localStore.GetVideoOverviewFallback();
        }
    }

    public async Task<VideoFileListResult> GetVideoFilesAsync(
        int page = 1,
        int pageSize = 100,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
        => await GetRequiredJsonAsync<VideoFileListResult>(
            $"products/video/files?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}{(forceRefresh ? "&refresh=1" : "")}",
            "動画ファイル一覧を取得できませんでした。",
            cancellationToken);

    public async Task<VideoFileDetailResult> GetVideoFileDetailAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"products/video/files/detail?path={Uri.EscapeDataString(sourcePath)}";
            return await _http.GetFromJsonAsync<VideoFileDetailResult>(
                    url,
                    JsonOptions,
                    cancellationToken)
                ?? new VideoFileDetailResult { Message = "動画詳細を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalHelperReadFailure(ex, "Failed to load TimelineForVideo file detail.");
            return new VideoFileDetailResult { Message = "動画詳細を取得できませんでした。" };
        }
    }

    public async Task<VideoOverview> SaveVideoSettingsAsync(
        VideoSettingsSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/video/settings", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"設定を保存できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<VideoOverview>(JsonOptions, cancellationToken)
            ?? OfflineVideoOverview("設定を保存しましたが、状態を読み取れませんでした。");
    }
}
