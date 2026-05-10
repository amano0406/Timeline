namespace Timeline.Web.Services;

public sealed class TimelineSettingsDialogService
{
    public event Action<string?>? OpenRequested;

    public void Open(string? productId = null)
    {
        OpenRequested?.Invoke(string.IsNullOrWhiteSpace(productId) ? null : productId);
    }
}
