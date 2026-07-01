namespace Timeline.Web.Services;

public sealed partial class TimelineLocalApiClient
{
    private static TimelineProductOverview OfflineOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = "",
        WorkerState = "未確認",
        Message = message,
    };

    private static WindowsCodexOverview OfflineWindowsCodexOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = "",
        Message = message,
    };

    private static ChatGptOverview OfflineChatGptOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = "",
        Message = message,
    };

    private static ImageOverview OfflineImageOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = "",
        Message = message,
    };

    private static VideoOverview OfflineVideoOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = "",
        Message = message,
    };

    private static PcOverview OfflinePcOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = "",
        Message = message,
    };

    private static AudioModelInventoryResult OfflineModels(string message) => new()
    {
        Available = false,
        Message = message,
    };
}
