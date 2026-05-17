using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ChatGpt
{
    private async Task DownloadSelectedAsync()
    {
        if (!SupportsSelectedDownload)
        {
            _error = "TimelineForChatGPT does not support selected item download in the current product API contract.";
            return;
        }

        var itemIds = SelectedItemIds();
        if (itemIds.Count == 0)
        {
            _error = "ダウンロードするスレッドを選択してください。";
            return;
        }

        var save = await BeginSaveAsync($"TimelineForChatGPT-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (save is null)
        {
            return;
        }

        _downloading = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DownloadChatGptItemsAsync(new TimelineThreadItemsRequest
            {
                ItemIds = itemIds,
            });
            await BrowserDownload.SaveArchiveAsync(Js, save, result.ArchivePath, $"TimelineForChatGPT-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
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
        var save = await BeginSaveAsync($"TimelineForChatGPT-all-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (save is null)
        {
            return;
        }

        _downloading = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DownloadChatGptItemsAsync(new TimelineThreadItemsRequest());
            await BrowserDownload.SaveArchiveAsync(Js, save, result.ArchivePath, $"TimelineForChatGPT-all-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
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
