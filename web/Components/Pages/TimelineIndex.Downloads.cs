using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineIndex
{
    private async Task DownloadAsync()
    {
        var suggestedName = $"Timeline-store-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        _error = null;
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
        SetOperationMessage("保存済みの時間軸から ZIP を作成しています。");
        try
        {
            var result = await Task.Run(TimelineExport.CreateDownload);
            if (result.ArchiveSizeBytes <= 0)
            {
                throw new InvalidOperationException("Timeline store ZIP was empty. Rebuild the Timeline store and try again.");
            }

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
