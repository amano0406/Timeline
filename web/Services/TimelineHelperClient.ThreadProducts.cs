using System.Net.Http.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineHelperClient
{
    public async Task<TimelineThreadListResult> GetWindowsCodexThreadsAsync(
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
        => await GetRequiredJsonAsync<TimelineThreadListResult>(
            $"products/windows-codex/items?page={Math.Max(1, page)}&pageSize={Math.Max(1, pageSize)}",
            "Windows Codex のスレッド一覧を取得できませんでした。",
            cancellationToken);

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
}
