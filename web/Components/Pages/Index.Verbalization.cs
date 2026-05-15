using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Index
{
    private void QueueVerbalizationStatusLoad()
    {
        if (_disposed || _loadingVerbalizationStatus)
        {
            return;
        }

        _ = LoadVerbalizationStatusAsync();
    }

    private async Task LoadVerbalizationStatusAsync()
    {
        _loadingVerbalizationStatus = true;
        try
        {
            var status = await Timeline.GetAudioVerbalizationBulkStatusAsync();
            if (_disposed)
            {
                return;
            }

            _verbalizationBulk = status;
            BuildDashboard();
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            _loadingVerbalizationStatus = false;
            if (!_disposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }
}
