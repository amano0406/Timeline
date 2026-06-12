using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Pc
{
    private const int PageSize = 25;
    private PcOverview? _overview;
    private PcItemListResult? _items;
    private bool _loading = true;
    private bool _loadingPage;
    private bool _refreshing;
    private bool _downloading;
    private DateTime? _lastLoadedAt;
    private int _currentPage = 1;
    private string? _error;
    private string? _operationMessage;

    private IReadOnlyList<PcItemRow> Items => _items?.Items ?? [];
    private bool Busy => _loading || _loadingPage || _refreshing || _downloading;
    private bool ListBusy => Busy;
    private bool CanDownload => (_overview?.ItemCount ?? 0) > 0 || Items.Count > 0;
    private int ItemCount => _items?.Total > 0 ? _items.Total : _overview?.ItemCount ?? Items.Count;
    private string ItemCountLabel => $"{ItemCount:N0} 件";
    private int ListTotalItems => _items?.Pagination.TotalItems > 0 ? _items.Pagination.TotalItems : ItemCount;
    private int VisibleEventCount => Items.Sum(item => item.EventCount);
    private string ProcessingLabel => _overview?.ItemCount > 0 ? "取得済み" : "未作成";
    private string ProcessingIcon => _overview?.ItemCount > 0 ? "circle-check" : "circle-minus";
    private string ProcessingIconClass => _overview?.ItemCount > 0 ? "text-teal-700" : "text-slate-500";
    private string ListStatusLabel => ListBusy
        ? "読み込み中"
        : _lastLoadedAt is null
            ? ""
            : $"最終更新 {_lastLoadedAt.Value:HH:mm:ss}";

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
            _overview = await Timeline.GetPcOverviewAsync();
            _currentPage = 1;
            _items = new PcItemListResult
            {
                Total = _overview.ItemCount,
                Pagination = new TimelinePagination
                {
                    Page = 1,
                    PageSize = PageSize,
                    TotalItems = _overview.ItemCount,
                    ReturnedItems = 0,
                },
            };
            await InvokeAsync(StateHasChanged);
            await LoadPageAsync(_currentPage);
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

    private async Task RefreshPcAsync()
    {
        _refreshing = true;
        _error = null;
        _operationMessage = "PC状態を取得しています。";
        try
        {
            var result = await Timeline.RefreshPcAsync();
            var state = string.IsNullOrWhiteSpace(result.State) ? "completed" : result.State;
            _operationMessage = $"PC状態を記録しました。状態: {state}";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _operationMessage = null;
            _error = ex.Message;
        }
        finally
        {
            _refreshing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task DownloadAsync()
    {
        var suggestedName = $"TimelineForPcInfo-items-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var save = await BrowserDownload.BeginSaveAsync(Js, suggestedName);
        if (!save.Accepted)
        {
            if (!string.IsNullOrWhiteSpace(save.Message))
            {
                _error = save.Message;
            }
            return;
        }

        _downloading = true;
        _error = null;
        _operationMessage = "PC状態のZIPを作成しています。";
        try
        {
            var result = await Timeline.DownloadPcItemsAsync(new PcItemsRequest());
            await BrowserDownload.SaveArchiveAsync(Js, save, result.ArchivePath, suggestedName);
            _operationMessage = "PC状態のZIPを作成しました。";
        }
        catch (Exception ex)
        {
            _operationMessage = null;
            _error = ex.Message;
        }
        finally
        {
            _downloading = false;
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
        _loadingPage = true;
        _error = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            _items = await Timeline.GetPcItemsAsync(page, PageSize);
            _currentPage = Math.Max(1, _items.Pagination.Page);
            _lastLoadedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loadingPage = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string PcUpdateStatusLabel(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "first_seen" => "初回取得",
            "changed" => "更新あり",
            "unchanged" => "変化なし",
            _ => EmptyText(value),
        };

    private static string PcUpdateStatusIcon(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "changed" => "circle-check",
            "unchanged" => "circle-minus",
            _ => "circle-info",
        };

    private static string PcUpdateStatusPillClass(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "first_seen" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "changed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "unchanged" => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };
}
