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
        _audioPlaying = true;
        StateHasChanged();
        await JS.InvokeVoidAsync("timelineAudioPlayer.seek", AudioElementId, Math.Max(0, seconds));
        await ScrollTranscriptToTurnAsync(_activeTurnStartSec.Value);
    }

    private async Task ToggleTurnPlaybackAsync(double seconds)
    {
        if (IsTurnPlaying(seconds))
        {
            await PauseAudioAsync();
            return;
        }

        await SeekAsync(seconds);
    }

    private async Task PauseAudioAsync()
    {
        if (_detail?.AudioAvailable != true)
        {
            return;
        }

        _audioPlaying = false;
        StateHasChanged();
        await JS.InvokeVoidAsync("timelineAudioPlayer.pause", AudioElementId);
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
        if (nextStart is not null)
        {
            await ScrollTranscriptToTurnAsync(nextStart.Value);
        }
    }

    [JSInvokable]
    public async Task OnAudioPlaybackStateChanged(bool playing)
    {
        if (_audioPlaying == playing)
        {
            return;
        }

        _audioPlaying = playing;
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
        var rows = TranscriptRows;
        if (rows.Count > 0)
        {
            foreach (var row in rows)
            {
                if (currentTime >= row.StartSec && currentTime < Math.Max(row.StartSec, row.EndSec))
                {
                    return row.StartSec;
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

    private async Task ScrollTranscriptToTurnAsync(double startSec)
    {
        try
        {
            await JS.InvokeVoidAsync(
                "timelineAudioPlayer.scrollTurnIntoView",
                TranscriptScrollElementId,
                TurnStartDataValue(startSec));
        }
        catch
        {
        }
    }
}
