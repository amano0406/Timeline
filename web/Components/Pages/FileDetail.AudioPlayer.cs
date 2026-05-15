using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class FileDetail
{
    private async Task SeekAsync(double seconds)
    {
        if (_detail?.AudioAvailable != true)
        {
            return;
        }

        _activeTurnStartSec = Math.Max(0, seconds);
        StateHasChanged();
        await JS.InvokeVoidAsync("timelineAudioPlayer.seek", AudioElementId, Math.Max(0, seconds));
    }

    [JSInvokable]
    public async Task OnAudioTimeChanged(double currentTime)
    {
        var nextStart = FindActiveTurnStart(currentTime);
        if (nextStart == _activeTurnStartSec)
        {
            return;
        }

        _activeTurnStartSec = nextStart;
        await InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        await UnwatchAudioAsync();
        CancelVerbalizationPolling();
        _audioDotNetRef?.Dispose();
        _audioDotNetRef = null;
    }

    private async Task UnwatchAudioAsync()
    {
        if (!_audioWatchAttached)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("timelineAudioPlayer.unwatch", AudioElementId);
        }
        catch
        {
        }
        finally
        {
            _audioWatchAttached = false;
        }
    }

    private double? FindActiveTurnStart(double currentTime)
    {
        if (_verbalizationResult?.Turns.Count > 0)
        {
            foreach (var turn in _verbalizationResult.Turns)
            {
                if (currentTime >= turn.StartSec && currentTime < Math.Max(turn.StartSec, turn.EndSec))
                {
                    return turn.StartSec;
                }
            }
        }

        if (_detail?.Turns.Count > 0)
        {
            foreach (var turn in _detail.Turns)
            {
                if (currentTime >= turn.StartSec && currentTime < Math.Max(turn.StartSec, turn.EndSec))
                {
                    return turn.StartSec;
                }
            }
        }

        return null;
    }
}
