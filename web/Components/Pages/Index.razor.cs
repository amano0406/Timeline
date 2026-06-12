using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Index
{
    private TimelineStoreOverview? _store;
    private TimelineDashboardStats? _dashboardStats;
    private TimelineDockerWorkerStatus? _worker;
    private ProductRuntimeOverview? _runtime;
    private TimelineProductOverview? _audio;
    private WindowsCodexOverview? _windowsCodex;
    private ChatGptOverview? _chatGpt;
    private ImageOverview? _image;
    private VideoOverview? _video;
    private PcOverview? _pc;
    private AudioVerbalizationBulkStatus? _verbalizationBulk;
    private AudioVerbalizationBulkTargetSummary? _verbalizationTargets;
    private bool _loading = true;
    private bool _loadingDetails;
    private bool _loadingVerbalizationStatus;
    private bool _repairingWorker;
    private bool _loadingDashboardStats;
    private bool _dashboardChartsPending;
    private bool _disposed;
    private string? _error;
    private string? _dashboardChartWarning;
    private string? _workerRepairMessage;
    private string _dashboardRange = "last90";
    private readonly List<DashboardAlert> _alerts = [];
    private readonly List<DataSourceSummary> _dataSources = [];

    private static readonly DashboardRangeOption[] DashboardRangeOptions =
    [
        new("last30", "直近30日"),
        new("last90", "直近90日"),
        new("last365", "直近1年"),
        new("thisMonth", "今月"),
        new("lastMonth", "先月"),
        new("all", "全期間"),
    ];

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        _loadingDetails = false;
        _error = null;
        _dashboardChartWarning = null;
        try
        {
            var storeTask = Timeline.GetTimelineStoreOverviewWithLocalFallbackAsync();
            var dashboardStatsTask = Timeline.GetTimelineDashboardStatsWithLocalFallbackAsync(_dashboardRange);
            var workerTask = Timeline.GetTimelineWorkerStatusAsync();
            var runtimeTask = Timeline.GetProductRuntimeOverviewAsync();

            await Task.WhenAll(
                storeTask,
                dashboardStatsTask,
                workerTask,
                runtimeTask);

            _store = await storeTask;
            _dashboardStats = await dashboardStatsTask;
            _worker = await workerTask;
            _runtime = await runtimeTask;
            _dashboardChartsPending = true;
            _loadingDetails = true;

            BuildDashboard();
            _loading = false;
            await InvokeAsync(StateHasChanged);

            await LoadDetailSummariesAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
            _loadingDetails = false;
        }
    }

    private async Task LoadDetailSummariesAsync()
    {
        _loadingDetails = true;
        try
        {
            if (!HasInstalledProducts)
            {
                _audio = null;
                _windowsCodex = null;
                _chatGpt = null;
                _image = null;
                _video = null;
                _pc = null;
                _verbalizationTargets = null;
                _verbalizationBulk = null;
                BuildDashboard();
                return;
            }

            var audioTask = Timeline.GetAudioOverviewAsync();
            var windowsTask = Timeline.GetWindowsCodexOverviewAsync();
            var chatGptTask = Timeline.GetChatGptOverviewAsync();
            var imageTask = Timeline.GetImageOverviewAsync();
            var videoTask = Timeline.GetVideoOverviewAsync();
            var pcTask = Timeline.GetPcOverviewAsync();
            var bulkTargetsTask = Timeline.GetAudioVerbalizationBulkTargetSummaryAsync();

            await Task.WhenAll(
                audioTask,
                windowsTask,
                chatGptTask,
                imageTask,
                videoTask,
                pcTask,
                bulkTargetsTask);

            _audio = await audioTask;
            _windowsCodex = await windowsTask;
            _chatGpt = await chatGptTask;
            _image = await imageTask;
            _video = await videoTask;
            _pc = await pcTask;
            _verbalizationTargets = await bulkTargetsTask;

            BuildDashboard();
            QueueVerbalizationStatusLoad();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loadingDetails = false;
            if (!_disposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_dashboardChartsPending || _dashboardStats is null || !_dashboardStats.Available)
        {
            return;
        }

        _dashboardChartsPending = false;
        try
        {
            var result = await Js.InvokeAsync<DashboardChartRenderResult>("timelineDashboardCharts.render", _dashboardStats);
            if (result is null || !result.Ok)
            {
                _dashboardChartWarning = string.IsNullOrWhiteSpace(result?.Message)
                    ? "ダッシュボードのグラフを描画できませんでした。数値は表示されています。"
                    : $"ダッシュボードのグラフを描画できませんでした。{result.Message}";

                if (!_disposed)
                {
                    await InvokeAsync(StateHasChanged);
                }
            }
        }
        catch (JSException ex)
        {
            _dashboardChartWarning = $"ダッシュボードのグラフを描画できませんでした。{ex.Message}";
            if (!_disposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task ChangeDashboardRangeAsync(string range)
    {
        if (_loadingDashboardStats || string.Equals(_dashboardRange, range, StringComparison.Ordinal))
        {
            return;
        }

        _dashboardRange = range;
        _loadingDashboardStats = true;
        _dashboardChartWarning = null;
        _error = null;
        await InvokeAsync(StateHasChanged);

        try
        {
            _dashboardStats = await Timeline.GetTimelineDashboardStatsWithLocalFallbackAsync(_dashboardRange);
            _dashboardChartsPending = true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loadingDashboardStats = false;
            if (!_disposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task RepairWorkerAsync()
    {
        if (_repairingWorker)
        {
            return;
        }

        _repairingWorker = true;
        _error = null;
        _workerRepairMessage = "Timeline worker を復旧しています。Docker の起動に少し時間がかかる場合があります。";
        await InvokeAsync(StateHasChanged);

        try
        {
            var result = await Timeline.RepairTimelineWorkerAsync();
            _worker = result.Worker;
            _workerRepairMessage = string.IsNullOrWhiteSpace(result.Message)
                ? "Timeline worker の復旧を実行しました。"
                : result.Message;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _workerRepairMessage = null;
            BuildDashboard();
        }
        finally
        {
            _repairingWorker = false;
            if (!_disposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private sealed class DashboardChartRenderResult
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("renderedCount")]
        public int RenderedCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }

    private sealed record DashboardRangeOption(string Value, string Label);
}
