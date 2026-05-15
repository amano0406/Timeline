using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class WindowsCodex
{
    private async Task DownloadSelectedAsync()
    {
        var itemIds = SelectedItemIds();
        if (itemIds.Count == 0)
        {
            _error = "ダウンロードするスレッドを選択してください。";
            return;
        }

        var save = await BeginSaveAsync($"TimelineForWindowsCodex-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (save is null)
        {
            return;
        }

        _downloading = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DownloadWindowsCodexItemsAsync(new WindowsCodexDownloadItemsRequest
            {
                ItemIds = itemIds,
            });
            await BrowserDownload.SaveArchiveAsync(Js, save, result.ArchivePath, $"TimelineForWindowsCodex-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            _operationMessage = $"{itemIds.Count} 件を保存しました。";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _downloading = false;
        }
    }

    private async Task DownloadAllAsync()
    {
        var save = await BeginSaveAsync($"TimelineForWindowsCodex-all-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (save is null)
        {
            return;
        }

        _downloading = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DownloadWindowsCodexItemsAsync(new WindowsCodexDownloadItemsRequest());
            await BrowserDownload.SaveArchiveAsync(Js, save, result.ArchivePath, $"TimelineForWindowsCodex-all-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            _operationMessage = "すべてのスレッドを保存しました。";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _downloading = false;
        }
    }

    private async Task<BrowserSaveHandle?> BeginSaveAsync(string suggestedName)
    {
        var save = await BrowserDownload.BeginSaveAsync(Js, suggestedName);
        if (!save.Accepted)
        {
            if (!string.IsNullOrWhiteSpace(save.Message))
            {
                _error = save.Message;
            }
            return null;
        }
        return save;
    }
}
