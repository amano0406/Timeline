using System.Net.Http.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineHelperClient
{
    public async Task<TimelineAppSettings> GetTimelineSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineAppSettings>(
                    "timeline/settings",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineAppSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Timeline settings.");
            return new TimelineAppSettings();
        }
    }

    public async Task<TimelineAppSettings> SaveTimelineSettingsAsync(
        TimelineAppSettingsSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("timeline/settings", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"設定を保存できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineAppSettings>(JsonOptions, cancellationToken)
            ?? new TimelineAppSettings();
    }

    public async Task<TimelineStoreOverview> GetTimelineStoreOverviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineStoreOverview>(
                    "timeline/store/overview",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineStoreOverview { Message = "時間軸の状態を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Timeline store overview.");
            return new TimelineStoreOverview { Message = "補助サーバーから時間軸の状態を取得できませんでした。" };
        }
    }

    public async Task<TimelineEventListResult> GetTimelineEventsAsync(
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineEventListResult>(
                    $"timeline/events?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineEventListResult { Message = "時間軸一覧を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Timeline events.");
            return new TimelineEventListResult { Message = "補助サーバーから時間軸一覧を取得できませんでした。" };
        }
    }

    public async Task<TimelineLlmInputPreviewResult> GetTimelineLlmInputPreviewAsync(
        string purpose = "preview",
        string product = "",
        string from = "",
        string to = "",
        int page = 1,
        int pageSize = 50,
        int maxChars = 4000,
        int scanLimit = 5000,
        bool countTotal = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>
            {
                $"purpose={Uri.EscapeDataString(purpose)}",
                $"page={Math.Max(1, page)}",
                $"pageSize={Math.Clamp(pageSize, 1, 200)}",
                $"maxChars={Math.Clamp(maxChars, 200, 20000)}",
                $"scanLimit={Math.Clamp(scanLimit, 100, 50000)}",
                $"countTotal={countTotal.ToString().ToLowerInvariant()}",
            };
            if (!string.IsNullOrWhiteSpace(product))
            {
                query.Add($"product={Uri.EscapeDataString(product)}");
            }
            if (!string.IsNullOrWhiteSpace(from))
            {
                query.Add($"from={Uri.EscapeDataString(from)}");
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                query.Add($"to={Uri.EscapeDataString(to)}");
            }

            return await _http.GetFromJsonAsync<TimelineLlmInputPreviewResult>(
                    $"timeline/llm-input/preview?{string.Join("&", query)}",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineLlmInputPreviewResult { Message = "LLM入力データを取得できませんでした。" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Timeline LLM input preview.");
            return new TimelineLlmInputPreviewResult { Message = "補助サーバーからLLM入力データを取得できませんでした。" };
        }
    }

    public async Task<TimelineWorkerJobStatus> RebuildTimelineStoreAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync("timeline/rebuild", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"時間軸を再構築できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineWorkerJobStatus>(JsonOptions, cancellationToken)
            ?? new TimelineWorkerJobStatus();
    }

    public async Task<TimelineWorkerJobStatus> GetTimelineRebuildStatusAsync(
        string jobId = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "timeline/rebuild/status";
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                url += $"?jobId={Uri.EscapeDataString(jobId)}";
            }
            return await _http.GetFromJsonAsync<TimelineWorkerJobStatus>(
                    url,
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineWorkerJobStatus { Message = "時間軸再構築の状態を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Timeline rebuild status.");
            return new TimelineWorkerJobStatus { State = "unknown", Message = "時間軸再構築の状態を取得できませんでした。" };
        }
    }

    public async Task<TimelineDockerWorkerStatus> GetTimelineWorkerStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineDockerWorkerStatus>(
                    "timeline/worker/status",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineDockerWorkerStatus { Message = "Timeline worker の状態を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Timeline worker status.");
            return new TimelineDockerWorkerStatus { State = "unknown", Message = "Timeline worker の状態を取得できませんでした。" };
        }
    }

    public async Task<TimelineExportDownloadResult> DownloadTimelineExportAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync("timeline/export/download", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"ダウンロードを作成できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineExportDownloadResult>(JsonOptions, cancellationToken)
            ?? new TimelineExportDownloadResult();
    }
}
