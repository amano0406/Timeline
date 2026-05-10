namespace Timeline.Web.Services;

public sealed class TimelineProductsDialogService
{
    public event Action? OpenRequested;

    public void Open()
    {
        OpenRequested?.Invoke();
    }
}
