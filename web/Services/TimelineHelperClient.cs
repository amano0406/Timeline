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
    {
        try
        {
            return await _http.GetFromJsonAsync<AudioFileListResult>(
                    $"products/audio/files?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
                    JsonOptions,
                    cancellationToken)
                ?? new AudioFileListResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load TimelineForAudio files.");
            return new AudioFileListResult();
        }
    }

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

    public async Task<TimelineThreadListResult> GetWindowsCodexThreadsAsync(
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineThreadListResult>(
                    $"products/windows-codex/items?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineThreadListResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load TimelineForWindowsCodex threads.");
            return new TimelineThreadListResult();
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
            _logger.LogWarning(ex, "Failed to load TimelineForAudio model inventory.");
            return OfflineModels("TimelineForAudio CLI からモデル一覧を取得できませんでした。");
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
        response.EnsureSuccessStatusCode();
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

    public Task<ProductRuntimeRow> StartProductAsync(string productId, CancellationToken cancellationToken = default) =>
        InvokeProductRuntimeActionAsync(productId, "start", cancellationToken);

    public Task<ProductRuntimeRow> RestartProductAsync(string productId, CancellationToken cancellationToken = default) =>
        InvokeProductRuntimeActionAsync(productId, "restart", cancellationToken);

    private async Task<ProductRuntimeRow> InvokeProductRuntimeActionAsync(
        string productId,
        string action,
        CancellationToken cancellationToken)
    {
        var response = await _http.PostAsync($"products/runtime/{Uri.EscapeDataString(productId)}/{action}", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"操作を実行できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ProductRuntimeRow>(JsonOptions, cancellationToken)
            ?? new ProductRuntimeRow { Id = productId, State = "unknown", Message = "操作後の状態を読み取れませんでした。" };
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
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineThreadListResult>(
                    $"products/chatgpt/items?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineThreadListResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load TimelineForChatGPT threads.");
            return new TimelineThreadListResult();
        }
    }

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
