using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Index
{
    private TimelineAppSettings? _settings;
    private TimelineStoreOverview? _store;
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
    private bool _disposed;
    private string? _error;
    private readonly List<DashboardAlert> _alerts = [];
    private readonly List<DataSourceSummary> _dataSources = [];

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        _loadingDetails = false;
        _error = null;
        try
        {
            var settingsTask = Timeline.GetTimelineSettingsAsync();
            var storeTask = Timeline.GetTimelineStoreOverviewAsync();
            var workerTask = Timeline.GetTimelineWorkerStatusAsync();
            var runtimeTask = Timeline.GetProductRuntimeOverviewAsync();

            await Task.WhenAll(
                settingsTask,
                storeTask,
                workerTask,
                runtimeTask);

            _settings = await settingsTask;
            _store = await storeTask;
            _worker = await workerTask;
            _runtime = await runtimeTask;
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
}
