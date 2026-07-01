using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ChatGpt
{
    private const int ThreadPageSize = 25;
    private static bool SupportsGeneratedDelete => false;
    private static bool SupportsSelectedDownload => false;

    private ChatGptOverview? _overview;
    private TimelineAppSettings? _timelineSettings;
    private TimelinePagination? _pagination;
    private readonly List<TimelineThreadRow> _threads = [];
    private readonly HashSet<string> _selectedThreadIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimelineItemSummaryListState _itemSummaryList = new();
    private bool _loading = true;
    private bool _loadingMoreThreads;
    private bool _refreshing;
    private bool _downloading;
    private bool _deleting;
    private bool _deleteModalOpen;
    private bool _pollingOverview;
    private bool _disposed;
    private DateTime? _lastLoadedAt;
    private int _currentThreadPage = 1;
    private int _threadTotal;
    private string? _threadListMessage;
    private string? _error;
    private string? _operationMessage;
    private CancellationTokenSource? _pollingCts;

    protected override void OnInitialized()
    {
        _pollingCts = new CancellationTokenSource();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed)
        {
            return;
        }

        await LoadAsync();
        if (!_disposed && _pollingCts is not null)
        {
            _ = PollActiveJobAsync(_pollingCts.Token);
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            var overviewTask = Timeline.GetChatGptOverviewAsync();
            var settingsTask = Timeline.GetTimelineSettingsAsync();
            await Task.WhenAll(overviewTask, settingsTask);
            _overview = await overviewTask;
            _timelineSettings = await settingsTask;
            _threads.Clear();
            _itemSummaryList.Clear();
            _threadListMessage = null;
            _currentThreadPage = 1;
            await InvokeAsync(StateHasChanged);
            await LoadThreadPageAsync(1, reset: true);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadThreadPageAsync(int page, bool reset)
    {
        var result = await Timeline.GetChatGptThreadsAsync(page, ThreadPageSize);
        if (reset)
        {
            _threads.Clear();
            _itemSummaryList.Clear();
        }
        _threadListMessage = string.IsNullOrWhiteSpace(result.Message) ? null : result.Message;
        _threads.AddRange(result.Threads);

        _threadTotal = result.Total > 0 ? result.Total : result.Pagination.TotalItems;
        _pagination = result.Pagination;
        _lastLoadedAt = DateTime.Now;
        _currentThreadPage = Math.Max(1, result.Pagination.Page);
        RemoveMissingSelections();
        await InvokeAsync(StateHasChanged);
        await _itemSummaryList.LoadAsync(Timeline, "chatgpt", _threads.Select(thread => thread.ItemId));
        await InvokeAsync(StateHasChanged);
    }

    private async Task ChangeThreadPageAsync(int page)
    {
        if (page == _currentThreadPage || _loadingMoreThreads)
        {
            return;
        }

        _loadingMoreThreads = true;
        _error = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            await LoadThreadPageAsync(page, reset: true);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loadingMoreThreads = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
    }
}
