using System.Net.Http.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineLocalApiClient
{
    public async Task<TimelineProductOverview> GetAudioOverviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineProductOverview>(
                    "products/audio/overview",
                    JsonOptions,
                    cancellationToken)
                ?? OfflineOverview("補助サーバーから状態を取得できませんでした。");
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load TimelineForAudio overview.");
            return _localStore.GetAudioOverviewFallback();
        }
    }

    public async Task<AudioFileListResult> GetAudioFilesAsync(
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
        => await GetRequiredJsonAsync<AudioFileListResult>(
            $"products/audio/files?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
            "音声ファイル一覧を取得できませんでした。",
            cancellationToken);

    public async Task<AudioFileDetailResult> GetAudioFileDetailAsync(
        string sourceId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "products/audio/files/detail"
                + $"?sourceId={Uri.EscapeDataString(sourceId)}"
                + $"&path={Uri.EscapeDataString(relativePath)}";
            return await _http.GetFromJsonAsync<AudioFileDetailResult>(
                    url,
                    JsonOptions,
                    cancellationToken)
                ?? new AudioFileDetailResult { Message = "音声詳細を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load TimelineForAudio file detail.");
            return new AudioFileDetailResult { Message = "音声詳細を取得できませんでした。" };
        }
    }

    public async Task<AudioVerbalizationStatus> GetAudioVerbalizationStatusAsync(
        string sourceId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AudioVerbalizationStatus>(
                    "timeline/audio-verbalization/status"
                    + $"?sourceId={Uri.EscapeDataString(sourceId)}"
                    + $"&path={Uri.EscapeDataString(relativePath)}",
                    JsonOptions,
                    cancellationToken)
                ?? new AudioVerbalizationStatus { Message = "言語化状態を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load audio verbalization status.");
            return new AudioVerbalizationStatus { State = "unknown", Message = "言語化状態を取得できませんでした。" };
        }
    }

    public async Task<AudioVerbalizationResult> GetAudioVerbalizationResultAsync(
        string sourceId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AudioVerbalizationResult>(
                    "timeline/audio-verbalization/result"
                    + $"?sourceId={Uri.EscapeDataString(sourceId)}"
                    + $"&path={Uri.EscapeDataString(relativePath)}",
                    JsonOptions,
                    cancellationToken)
                ?? new AudioVerbalizationResult { Message = "言語化結果を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load audio verbalization result.");
            return new AudioVerbalizationResult { Message = "言語化結果を取得できませんでした。" };
        }
    }

    public async Task<AudioVerbalizationStatus> StartAudioVerbalizationAsync(
        AudioVerbalizationStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("timeline/audio-verbalization/start", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"音声の言語化を開始できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<AudioVerbalizationStatus>(JsonOptions, cancellationToken)
            ?? new AudioVerbalizationStatus { Message = "音声の言語化状態を取得できませんでした。" };
    }

    public async Task<AudioVerbalizationBulkStatus> GetAudioVerbalizationBulkStatusAsync(
        string jobId = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "timeline/audio-verbalization/bulk/status";
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                url += $"?jobId={Uri.EscapeDataString(jobId)}";
            }
            return await _http.GetFromJsonAsync<AudioVerbalizationBulkStatus>(
                    url,
                    JsonOptions,
                    cancellationToken)
                ?? new AudioVerbalizationBulkStatus { Message = "一括言語化の状態を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load audio verbalization bulk status.");
            return new AudioVerbalizationBulkStatus { State = "unknown", Message = "一括言語化の状態を取得できませんでした。" };
        }
    }

    public async Task<AudioVerbalizationBulkTargetSummary> GetAudioVerbalizationBulkTargetSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AudioVerbalizationBulkTargetSummary>(
                    "timeline/audio-verbalization/bulk/targets",
                    JsonOptions,
                    cancellationToken)
                ?? new AudioVerbalizationBulkTargetSummary { Message = "一括言語化の対象を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load audio verbalization bulk targets.");
            return new AudioVerbalizationBulkTargetSummary { Message = "一括言語化の対象を取得できませんでした。" };
        }
    }

    public async Task<AudioVerbalizationBulkStatus> StartAudioVerbalizationBulkAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync("timeline/audio-verbalization/bulk/start", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"一括言語化を開始できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<AudioVerbalizationBulkStatus>(JsonOptions, cancellationToken)
            ?? new AudioVerbalizationBulkStatus { Message = "一括言語化の状態を取得できませんでした。" };
    }

    public async Task<AudioVerbalizationBulkStatus> CancelAudioVerbalizationBulkAsync(
        string jobId = "",
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            "timeline/audio-verbalization/bulk/cancel",
            new { jobId },
            JsonOptions,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"Audio verbalization bulk cancel failed. HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<AudioVerbalizationBulkStatus>(JsonOptions, cancellationToken)
            ?? new AudioVerbalizationBulkStatus();
    }

    public async Task<AudioVerbalizationOllamaStatus> GetAudioVerbalizationOllamaStatusAsync(
        string baseUrl = "",
        string model = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "timeline/audio-verbalization/ollama/status";
            if (!string.IsNullOrWhiteSpace(baseUrl) || !string.IsNullOrWhiteSpace(model))
            {
                url += $"?baseUrl={Uri.EscapeDataString(baseUrl)}&model={Uri.EscapeDataString(model)}";
            }
            return await _http.GetFromJsonAsync<AudioVerbalizationOllamaStatus>(
                    url,
                    JsonOptions,
                    cancellationToken)
                ?? new AudioVerbalizationOllamaStatus { Message = "Ollama の状態を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load Ollama status.");
            return new AudioVerbalizationOllamaStatus { Message = "Ollama の状態を取得できませんでした。" };
        }
    }

    public async Task<AudioModelInventoryResult> GetAudioModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AudioModelInventoryResult>(
                    "products/audio/models",
                    JsonOptions,
                    cancellationToken)
                ?? OfflineModels("モデル一覧を取得できませんでした。");
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load TimelineForAudio model inventory.");
            return OfflineModels("TimelineForAudio API からモデル一覧を取得できませんでした。");
        }
    }

    public async Task<AudioDeleteGeneratedResult> DeleteAudioGeneratedAsync(
        AudioDeleteGeneratedRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/audio/files/delete-generated", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"生成物を削除できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<AudioDeleteGeneratedResult>(JsonOptions, cancellationToken)
            ?? new AudioDeleteGeneratedResult();
    }

    public async Task<AudioRefreshResult> RefreshAudioAsync(
        AudioRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/audio/refresh", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"再スキャンを開始できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<AudioRefreshResult>(JsonOptions, cancellationToken)
            ?? new AudioRefreshResult();
    }

    public async Task<AudioDownloadItemsResult> DownloadAudioItemsAsync(
        AudioDownloadItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/audio/items/download", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"ダウンロードを作成できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<AudioDownloadItemsResult>(JsonOptions, cancellationToken)
            ?? new AudioDownloadItemsResult();
    }

    public async Task<TimelineProductOverview> SaveAudioSettingsAsync(
        AudioSettingsSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/audio/settings", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"設定を保存できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineProductOverview>(JsonOptions, cancellationToken)
            ?? OfflineOverview("設定を保存しましたが、状態を読み取れませんでした。");
    }
}
