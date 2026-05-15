using Microsoft.JSInterop;

namespace Timeline.Web.Services;

public static class BrowserDownload
{
    public static async Task<BrowserSaveHandle> BeginSaveAsync(IJSRuntime js, string suggestedName) =>
        await js.InvokeAsync<BrowserSaveHandle>("timelineDownload.beginSave", suggestedName);

    public static async Task SaveArchiveAsync(IJSRuntime js, BrowserSaveHandle save, string archivePath, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new InvalidOperationException("ダウンロードファイルを作成できませんでした。");
        }

        await js.InvokeVoidAsync(
            "timelineDownload.saveUrl",
            save.Id,
            DownloadFileUrl(archivePath),
            ArchiveFileName(archivePath, fallbackName));
    }

    public static string DownloadFileUrl(string archivePath) =>
        $"api/download/file?path={Uri.EscapeDataString(archivePath)}";

    public static string ArchiveFileName(string archivePath, string fallback)
    {
        var text = (archivePath ?? "").Trim().Replace('\\', '/');
        var fileName = text.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
    }
}
