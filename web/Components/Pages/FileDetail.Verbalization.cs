using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class FileDetail
{
    private async Task LoadVerbalizationResultAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SourceId) || string.IsNullOrWhiteSpace(RelativePath))
        {
            _verbalizationResult = null;
            return;
        }

        _verbalizationResult = await Timeline.GetAudioVerbalizationResultAsync(SourceId, RelativePath, cancellationToken);
        if (_detail is not null && _verbalizationResult.Status is not null)
        {
            _detail.AudioVerbalization = _verbalizationResult.Status;
        }
    }

    private async Task StartVerbalizationAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceId) || string.IsNullOrWhiteSpace(RelativePath) || _detail is null)
        {
            return;
        }

        _startingVerbalization = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var status = await Timeline.StartAudioVerbalizationAsync(new AudioVerbalizationStartRequest
            {
                SourceId = SourceId,
                RelativePath = RelativePath,
            });
            _detail.AudioVerbalization = status;
            await LoadVerbalizationResultAsync();
            StartVerbalizationPollingIfNeeded();
            _operationMessage = VerbalizationStartMessage(status);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _startingVerbalization = false;
        }
    }

    private void StartVerbalizationPollingIfNeeded()
    {
        CancelVerbalizationPolling();
        if (!ShouldPollVerbalization())
        {
            return;
        }

        _verbalizationPollingCts = new CancellationTokenSource();
        _verbalizationPollingTask = PollVerbalizationAsync(_verbalizationPollingCts.Token);
    }

    private async Task PollVerbalizationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await InvokeAsync(async () =>
                {
                    await LoadVerbalizationResultAsync(cancellationToken);
                    StateHasChanged();
                });

                if (!ShouldPollVerbalization())
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await InvokeAsync(() =>
            {
                _operationMessage = ex.Message;
                StateHasChanged();
            });
        }
    }

    private bool ShouldPollVerbalization()
    {
        var state = _verbalizationResult?.Status.State ?? _detail?.AudioVerbalization.State ?? "";
        return state.Equals("running", StringComparison.OrdinalIgnoreCase)
            || state.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || state.Equals("planned", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanStartVerbalization(AudioVerbalizationStatus status)
    {
        if (!status.Available)
        {
            return false;
        }

        var state = status.State ?? "";
        return !state.Equals("completed", StringComparison.OrdinalIgnoreCase)
            && !state.Equals("running", StringComparison.OrdinalIgnoreCase)
            && !state.Equals("queued", StringComparison.OrdinalIgnoreCase);
    }

    private void CancelVerbalizationPolling()
    {
        _verbalizationPollingCts?.Cancel();
        _verbalizationPollingCts?.Dispose();
        _verbalizationPollingCts = null;
        _verbalizationPollingTask = null;
    }
}
