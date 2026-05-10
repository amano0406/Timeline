using System.Net.Http.Json;
using System.Text.Json;

namespace Timeline.Web.Services;

public sealed class TimelineHelperClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<TimelineHelperClient> _logger;

    public TimelineHelperClient(HttpClient http, ILogger<TimelineHelperClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await _http.GetFromJsonAsync<HelperHealth>("health", JsonOptions, cancellationToken);
            return health?.Ok == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timeline helper health check failed.");
            return false;
        }
    }

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
            _logger.LogWarning(ex, "Failed to load TimelineForAudio overview.");
            return OfflineOverview("補助サーバーに接続できません。start.bat から起動してください。");
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
            _logger.LogWarning(ex, "Failed to load TimelineForAudio file detail.");
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
            _logger.LogWarning(ex, "Failed to load audio verbalization status.");
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
            _logger.LogWarning(ex, "Failed to load audio verbalization result.");
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
            _logger.LogWarning(ex, "Failed to load audio verbalization bulk status.");
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
            _logger.LogWarning(ex, "Failed to load audio verbalization bulk targets.");
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
            _logger.LogWarning(ex, "Failed to load Ollama status.");
            return new AudioVerbalizationOllamaStatus { Message = "Ollama の状態を取得できませんでした。" };
        }
    }

    public async Task<TimelineThreadListResult> GetWindowsCodexThreadsAsync(
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
        => await GetRequiredJsonAsync<TimelineThreadListResult>(
            $"products/windows-codex/items?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
            "Windows Codex のスレッド一覧を取得できませんでした。",
            cancellationToken);

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
            _logger.LogWarning(ex, "Failed to load TimelineForAudio model inventory.");
            return OfflineModels("TimelineForAudio CLI からモデル一覧を取得できませんでした。");
        }
    }

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
            _logger.LogWarning(ex, "Failed to load TimelineForImage model inventory.");
            return OfflineModels("TimelineForImage CLI からモデル一覧を取得できませんでした。");
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
            _logger.LogWarning(ex, "Failed to load product runtime overview.");
            return new ProductRuntimeOverview { Message = "補助サーバーに接続できません。start.bat から起動してください。" };
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

    public async Task<ProductRuntimeRow> UninstallProductAsync(
        string productId,
        CancellationToken cancellationToken = default)
        => await PostProductRuntimeActionAsync(productId, "uninstall", cancellationToken);

    private async Task<ProductRuntimeRow> PostProductRuntimeActionAsync(
        string productId,
        string action,
        CancellationToken cancellationToken)
    {
        var response = await _http.PostAsync(
            $"products/runtime/{Uri.EscapeDataString(productId)}/{Uri.EscapeDataString(action)}",
            content: null,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"製品操作を実行できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ProductRuntimeRow>(JsonOptions, cancellationToken)
            ?? new ProductRuntimeRow { Id = productId, Message = "製品操作は完了しましたが、状態を読み取れませんでした。" };
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

    public async Task<WindowsCodexOverview> GetWindowsCodexOverviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<WindowsCodexOverview>(
                    "products/windows-codex/overview",
                    JsonOptions,
                    cancellationToken)
                ?? OfflineWindowsCodexOverview("補助サーバーから状態を取得できませんでした。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load TimelineForWindowsCodex overview.");
            return OfflineWindowsCodexOverview("補助サーバーに接続できません。start.bat から起動してください。");
        }
    }

    public async Task<WindowsCodexCurrent> RefreshWindowsCodexAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync("products/windows-codex/refresh", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"更新を実行できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<WindowsCodexCurrent>(JsonOptions, cancellationToken)
            ?? new WindowsCodexCurrent();
    }

    public async Task<WindowsCodexThreadDetail> GetWindowsCodexThreadAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<WindowsCodexThreadDetail>(
                    $"products/windows-codex/threads/{Uri.EscapeDataString(itemId)}",
                    JsonOptions,
                    cancellationToken)
                ?? new WindowsCodexThreadDetail { ItemId = itemId, Message = "スレッドを取得できませんでした。" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load TimelineForWindowsCodex thread {ItemId}.", itemId);
            return new WindowsCodexThreadDetail
            {
                ItemId = itemId,
                Message = "補助サーバーからスレッドを取得できませんでした。",
            };
        }
    }

    public async Task<WindowsCodexDownloadItemsResult> DownloadWindowsCodexItemsAsync(
        WindowsCodexDownloadItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/windows-codex/items/download", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"ダウンロードを作成できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<WindowsCodexDownloadItemsResult>(JsonOptions, cancellationToken)
            ?? new WindowsCodexDownloadItemsResult();
    }

    public async Task<TimelineThreadItemsDeleteResult> DeleteWindowsCodexItemsAsync(
        TimelineThreadItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/windows-codex/items/delete-generated", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"生成物を削除できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineThreadItemsDeleteResult>(JsonOptions, cancellationToken)
            ?? new TimelineThreadItemsDeleteResult();
    }

    public async Task<WindowsCodexOverview> SaveWindowsCodexSettingsAsync(
        WindowsCodexSettingsSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/windows-codex/settings", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"設定を保存できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<WindowsCodexOverview>(JsonOptions, cancellationToken)
            ?? OfflineWindowsCodexOverview("設定を保存しましたが、状態を読み取れませんでした。");
    }

    public async Task<ChatGptOverview> GetChatGptOverviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ChatGptOverview>(
                    "products/chatgpt/overview",
                    JsonOptions,
                    cancellationToken)
                ?? OfflineChatGptOverview("補助サーバーから状態を取得できませんでした。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load TimelineForChatGPT overview.");
            return OfflineChatGptOverview("補助サーバーに接続できません。start.bat から起動してください。");
        }
    }

    public async Task<TimelineThreadListResult> GetChatGptThreadsAsync(
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
        => await GetRequiredJsonAsync<TimelineThreadListResult>(
            $"products/chatgpt/items?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
            "ChatGPT のスレッド一覧を取得できませんでした。",
            cancellationToken);

    public async Task<ChatGptThreadDetail> GetChatGptThreadAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ChatGptThreadDetail>(
                    $"products/chatgpt/threads/{Uri.EscapeDataString(itemId)}",
                    JsonOptions,
                    cancellationToken)
                ?? new ChatGptThreadDetail { ItemId = itemId, Message = "スレッドを取得できませんでした。" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load TimelineForChatGPT thread {ItemId}.", itemId);
            return new ChatGptThreadDetail
            {
                ItemId = itemId,
                Message = "補助サーバーからスレッドを取得できませんでした。",
            };
        }
    }

    public async Task<TimelineThreadItemsDownloadResult> DownloadChatGptItemsAsync(
        TimelineThreadItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/chatgpt/items/download", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"ダウンロードを作成できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineThreadItemsDownloadResult>(JsonOptions, cancellationToken)
            ?? new TimelineThreadItemsDownloadResult();
    }

    public async Task<TimelineThreadItemsDeleteResult> DeleteChatGptItemsAsync(
        TimelineThreadItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/chatgpt/items/delete-generated", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"生成物を削除できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineThreadItemsDeleteResult>(JsonOptions, cancellationToken)
            ?? new TimelineThreadItemsDeleteResult();
    }

    public async Task<ChatGptRefreshSummary> RefreshChatGptAsync(
        ChatGptRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/chatgpt/refresh", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"更新を実行できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ChatGptRefreshSummary>(JsonOptions, cancellationToken)
            ?? new ChatGptRefreshSummary();
    }

    public async Task<ChatGptOverview> SaveChatGptSettingsAsync(
        ChatGptSettingsSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/chatgpt/settings", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"設定を保存できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ChatGptOverview>(JsonOptions, cancellationToken)
            ?? OfflineChatGptOverview("設定を保存しましたが、状態を読み取れませんでした。");
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
            _logger.LogWarning(ex, "Failed to load TimelineForImage overview.");
            return OfflineImageOverview("補助サーバーに接続できません。start.bat から起動してください。");
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
            _logger.LogWarning(ex, "Failed to load TimelineForImage file detail.");
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
            _logger.LogWarning(ex, "Failed to load TimelineForVideo overview.");
            return OfflineVideoOverview("補助サーバーに接続できません。start.bat から起動してください。");
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
            _logger.LogWarning(ex, "Failed to load TimelineForVideo file detail.");
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
            _logger.LogWarning(ex, "Failed to load TimelineForPC overview.");
            return OfflinePcOverview("補助サーバーに接続できません。start.bat から起動してください。");
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

    private static TimelineProductOverview OfflineOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = @"C:\apps\TimelineForAudio",
        WorkerState = "未確認",
        Message = message,
    };

    private static WindowsCodexOverview OfflineWindowsCodexOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = @"C:\apps\TimelineForWindowsCodex",
        Message = message,
    };

    private static ChatGptOverview OfflineChatGptOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = @"C:\apps\TimelineForChatGPT",
        Message = message,
    };

    private static ImageOverview OfflineImageOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = @"C:\apps\TimelineForImage",
        Message = message,
    };

    private static VideoOverview OfflineVideoOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = @"C:\apps\TimelineForVideo",
        Message = message,
    };

    private static PcOverview OfflinePcOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = @"C:\apps\TimelineForPC",
        Message = message,
    };

    private async Task<T> GetRequiredJsonAsync<T>(
        string url,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetFromJsonAsync<T>(url, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException($"{failureMessage} 応答が空です。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Message}", failureMessage);
            throw new InvalidOperationException(failureMessage, ex);
        }
    }

    private static AudioModelInventoryResult OfflineModels(string message) => new()
    {
        Available = false,
        Message = message,
    };

    private static string? ErrorMessageFromBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
        }

        return body.Trim();
    }
}
