using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineIndex
{
    private async Task DownloadAsync()
    {
        var suggestedName = $"Timeline-store-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
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
        SetOperationMessage("保存済みの時間軸から ZIP を作成しています。");
        try
        {
            var result = await Timeline.DownloadTimelineExportAsync();
            await BrowserDownload.SaveArchiveAsync(Js, save, result.ArchivePath, suggestedName);
            SetOperationMessage($"保存しました。{result.ItemCount:N0} 件 / {result.EventCount:N0} イベント");
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
