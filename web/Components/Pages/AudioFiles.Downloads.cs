using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class AudioFiles
{
    private async Task DownloadSelectedAsync()
    {
        var selected = SelectedFiles().ToList();
        var itemIds = SelectedGeneratedItemIds(selected).ToList();
        if (itemIds.Count == 0)
        {
            _error = "ダウンロードできる生成物があるファイルを選択してください。";
            return;
        }

        var save = await BeginSaveAsync($"TimelineForAudio-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (save is null)
        {
            return;
        }

        _downloading = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DownloadAudioItemsAsync(new AudioDownloadItemsRequest
            {
                ItemIds = itemIds,
            });
            await BrowserDownload.SaveArchiveAsync(Js, save, result.ArchivePath, $"TimelineForAudio-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
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
        var save = await BeginSaveAsync($"TimelineForAudio-all-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (save is null)
        {
            return;
        }

        _downloading = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DownloadAudioItemsAsync(new AudioDownloadItemsRequest());
            await BrowserDownload.SaveArchiveAsync(Js, save, result.ArchivePath, $"TimelineForAudio-all-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            _operationMessage = "すべての生成物を保存しました。";
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
