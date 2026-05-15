using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Image
{
    private const int PageSize = 25;
    private ImageOverview? _overview;
    private ImageFileListResult? _files;
    private TimelinePagination? _pagination;
    private readonly List<ImageItemRow> _items = [];
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private int _currentPage = 1;
    private bool _loading = true;
    private bool _loadingMore;
    private bool _downloading;
    private bool _deleting;
    private string? _error;
    private string? _operationMessage;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            _overview = await Timeline.GetImageOverviewAsync();
            _items.Clear();
            _files = null;
            _selected.Clear();
            _currentPage = 1;
            _pagination = new TimelinePagination
            {
                Page = 1,
                PageSize = PageSize,
                TotalItems = _overview.SourceFileCount,
                ReturnedItems = 0,
            };
            await InvokeAsync(StateHasChanged);
            await LoadPageAsync(_currentPage);
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ChangePageAsync(int page)
    {
        if (page == _currentPage)
        {
            return;
        }

        await LoadPageAsync(page);
    }

    private async Task LoadPageAsync(int page)
    {
        _loadingMore = true;
        _error = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            var result = await Timeline.GetImageFilesAsync(page, PageSize);
            _items.Clear();
            _items.AddRange(result.Files);
            _files = result;
            _pagination = result.Pagination;
            _currentPage = Math.Max(1, result.Pagination.Page);
            RemoveMissingSelections();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loadingMore = false;
            await InvokeAsync(StateHasChanged);
        }
    }


}
