using System.Net.Http.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineLocalApiClient
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
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline settings.");
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

    public async Task<TimelineLauncherShortcutStatus> GetLauncherShortcutStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineLauncherShortcutStatus>(
                    "timeline/launcher-shortcut/status",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineLauncherShortcutStatus
                {
                    State = "unknown",
                    Message = "Timeline のアプリ入口の状態を取得できませんでした。",
                };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline launcher shortcut status.");
            return new TimelineLauncherShortcutStatus
            {
                Supported = false,
                State = "local_api_unreachable",
                Message = "Timeline の操作機能に接続できません。Timeline Launcher から起動し直してください。",
            };
        }
    }

    public async Task<TimelineLauncherShortcutStatus> InstallLauncherShortcutAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync("timeline/launcher-shortcut/install", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"Timeline のアプリ入口を作成できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineLauncherShortcutStatus>(JsonOptions, cancellationToken)
            ?? new TimelineLauncherShortcutStatus { State = "unknown", Message = "Timeline のアプリ入口の作成結果が空でした。" };
    }

    public async Task<TimelineLauncherShortcutStatus> RemoveLauncherShortcutAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync("timeline/launcher-shortcut/remove", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"Timeline のアプリ入口を削除できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineLauncherShortcutStatus>(JsonOptions, cancellationToken)
            ?? new TimelineLauncherShortcutStatus { State = "unknown", Message = "Timeline のアプリ入口の削除結果が空でした。" };
    }

    public async Task<TimelineUninstallPlan> GetTimelineUninstallPlanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineUninstallPlan>(
                    "timeline/uninstall/plan",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineUninstallPlan
                {
                    ProductId = "timeline",
                    ProductName = "Timeline",
                    State = "empty",
                };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline uninstall plan.");
            return new TimelineUninstallPlan
            {
                ProductId = "timeline",
                ProductName = "Timeline",
                State = "local_api_unreachable",
                Mode = "read_only",
                CanExecute = false,
                RequiresExplicitConfirmation = true,
                Warnings =
                [
                    new TimelineUninstallPlanMessage
                    {
                        Code = "local_api_unreachable",
                        Message = "Timeline の操作機能に接続できないため、削除対象を確認できませんでした。",
                    },
                ],
            };
        }
    }

    public async Task<PathStatusResult> GetPathStatusAsync(
        string path,
        string kind = "directory",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new PathStatusResult
            {
                Ok = false,
                Path = path,
                Kind = kind,
                Message = "パスが未設定です。",
            };
        }

        try
        {
            return await _http.GetFromJsonAsync<PathStatusResult>(
                    $"path-status?path={Uri.EscapeDataString(path)}&kind={Uri.EscapeDataString(kind)}",
                    JsonOptions,
                    cancellationToken)
                ?? new PathStatusResult
                {
                    Ok = false,
                    Path = path,
                    Kind = kind,
                    Message = "ディレクトリの確認結果が空です。",
                };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to validate path: {Path}", path);
            return new PathStatusResult
            {
                Ok = false,
                Path = path,
                Kind = kind,
                Message = "ディレクトリを確認できませんでした。",
            };
        }
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
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline store overview.");
            return new TimelineStoreOverview { Message = "補助サーバーから時間軸の状態を取得できませんでした。" };
        }
    }

    public async Task<TimelineDashboardStats> GetTimelineDashboardStatsAsync(
        int days = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineDashboardStats>(
                    $"timeline/dashboard/stats?days={Math.Clamp(days, 7, 90)}",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineDashboardStats { Message = "ダッシュボード統計を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline dashboard stats.");
            return new TimelineDashboardStats { Message = "補助サーバーからダッシュボード統計を取得できませんでした。" };
        }
    }

    public async Task<TimelineDashboardStats> GetTimelineDashboardStatsAsync(
        string range,
        string bucket = "auto",
        int days = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(range))
            {
                query.Add($"range={Uri.EscapeDataString(range)}");
            }
            if (!string.IsNullOrWhiteSpace(bucket))
            {
                query.Add($"bucket={Uri.EscapeDataString(bucket)}");
            }
            if (days > 0)
            {
                query.Add($"days={Math.Clamp(days, 7, 365)}");
            }

            var queryText = query.Count == 0 ? string.Empty : $"?{string.Join("&", query)}";
            return await _http.GetFromJsonAsync<TimelineDashboardStats>(
                    $"timeline/dashboard/stats{queryText}",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineDashboardStats { Message = "Dashboard stats were empty." };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline dashboard stats.");
            return new TimelineDashboardStats { Message = "Dashboard stats could not be loaded." };
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
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline events.");
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
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline LLM input preview.");
            return new TimelineLlmInputPreviewResult { Message = "補助サーバーからLLM入力データを取得できませんでした。" };
        }
    }

    public async Task<TimelineWorkerJobStatus> RebuildTimelineStoreAsync(
        TimelineRebuildRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            "timeline/rebuild",
            request ?? new TimelineRebuildRequest(),
            JsonOptions,
            cancellationToken);
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
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline rebuild status.");
            return new TimelineWorkerJobStatus { State = "unknown", Message = "時間軸再構築の状態を取得できませんでした。" };
        }
    }

    public async Task<TimelineWorkerJobStatus> CancelTimelineRebuildAsync(
        string jobId = "",
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            "timeline/rebuild/cancel",
            new { jobId },
            JsonOptions,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"Timeline rebuild cancel failed. HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineWorkerJobStatus>(JsonOptions, cancellationToken)
            ?? new TimelineWorkerJobStatus();
    }

    public async Task<TimelineItemSummaryJobStatus> StartTimelineItemSummariesAsync(
        bool force = false,
        bool runAll = false,
        int maxItems = 20,
        bool pendingOnly = true,
        bool fastMode = false,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            "timeline/item-summaries/start",
            new { force, runAll, maxItems, pendingOnly, fastMode },
            JsonOptions,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"素材概要の生成を開始できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineItemSummaryJobStatus>(JsonOptions, cancellationToken)
            ?? new TimelineItemSummaryJobStatus();
    }

    public async Task<TimelineItemSummaryJobStatus> GetTimelineItemSummaryStatusAsync(
        string jobId = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "timeline/item-summaries/status";
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                url += $"?jobId={Uri.EscapeDataString(jobId)}";
            }

            return await _http.GetFromJsonAsync<TimelineItemSummaryJobStatus>(
                    url,
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineItemSummaryJobStatus { State = "unknown", Message = "素材概要の状態を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline item summary status.");
            return _localStore.GetItemSummaryStatus(jobId);
        }
    }

    public async Task<TimelineItemSummary> GetTimelineItemSummaryAsync(
        string product,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineItemSummary>(
                    "timeline/item-summaries/item"
                    + $"?product={Uri.EscapeDataString(product)}"
                    + $"&itemId={Uri.EscapeDataString(itemId)}",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineItemSummary { Product = product, ItemId = itemId, Message = "素材概要を取得できませんでした。" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline item summary {Product}/{ItemId}.", product, itemId);
            return _localStore.GetItemSummary(product, itemId);
        }
    }

    public async Task<TimelineItemSummaryJobStatus> CancelTimelineItemSummariesAsync(
        string jobId = "",
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            "timeline/item-summaries/cancel",
            new { jobId },
            JsonOptions,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"素材概要の生成を停止できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineItemSummaryJobStatus>(JsonOptions, cancellationToken)
            ?? new TimelineItemSummaryJobStatus();
    }

    public async Task<TimelineDockerWorkerStatus> GetTimelineWorkerStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineDockerWorkerStatus>(
                    "timeline/worker/status",
                    JsonOptions,
                    cancellationToken)
                ?? new TimelineDockerWorkerStatus { State = "unknown" };
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline worker status.");
            return new TimelineDockerWorkerStatus
            {
                Available = false,
                Worker = "timeline-worker",
                State = "local_api_unreachable",
                StoreAvailable = false,
                Message = "Timeline の操作機能に接続できません。Timeline を起動し直してください。",
            };
        }
    }

    public async Task<TimelineWorkerRepairResult> RepairTimelineWorkerAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync("timeline/worker/repair", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"Timeline worker を復旧できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineWorkerRepairResult>(JsonOptions, cancellationToken)
            ?? new TimelineWorkerRepairResult { Message = "Timeline worker の復旧結果が空でした。" };
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
