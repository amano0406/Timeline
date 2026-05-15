using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ChatGpt
{
    private async Task PollActiveJobAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (ShowProcessingProgress)
                {
                    await InvokeAsync(RefreshOverviewForPollingAsync);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshOverviewForPollingAsync()
    {
        if (_pollingOverview || _loading || _deleting || _downloading)
        {
            return;
        }

        _pollingOverview = true;
        var hadActiveJob = ActiveJob is not null;
        try
        {
            _overview = await Timeline.GetChatGptOverviewAsync();
            if (hadActiveJob && ActiveJob is null)
            {
                await LoadThreadPageAsync(_currentThreadPage, reset: true);
            }
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
        }
        finally
        {
            _pollingOverview = false;
            StateHasChanged();
        }
    }

    private async Task StartRefreshAsync()
    {
        _error = null;
        _operationMessage = null;

        string? filePath;
        try
        {
            filePath = await Js.InvokeAsync<string?>(
                "timelineDirectoryPicker.pickFile",
                "ChatGPT export ZIPを選択",
                "",
                "ZIP files (*.zip)|*.zip|All files (*.*)|*.*");
        }
        catch (JSException ex)
        {
            _error = ex.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        _refreshing = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            var result = await Timeline.RefreshChatGptAsync(new ChatGptRefreshRequest
            {
                FilePath = filePath,
            });
            _operationMessage = result.Available
                ? $"更新が完了しました。処理 {result.Processed} 件、スキップ {result.Skipped} 件。"
                : "更新を実行しました。";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }
}
