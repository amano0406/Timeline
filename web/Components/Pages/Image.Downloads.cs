using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Image
{
    private async Task DownloadSelectedAsync()
    {
        await DownloadAsync(SelectedGeneratedItemIds(), $"TimelineForImage-selected-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
    }

    private async Task DownloadAllAsync()
    {
        await DownloadAsync([], $"TimelineForImage-all-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
    }

    private async Task DownloadAsync(List<string> itemIds, string suggestedName)
    {
        var save = await BrowserDownload.BeginSaveAsync(Js, suggestedName);
        if (!save.Accepted)
        {
            return;
        }

        _downloading = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DownloadImageItemsAsync(new ImageItemsRequest { ItemIds = itemIds });
            await BrowserDownload.SaveArchiveAsync(Js, save, result.ArchivePath, suggestedName);
            _operationMessage = "保存しました。";
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
}
