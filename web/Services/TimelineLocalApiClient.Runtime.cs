using System.Net.Http.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineLocalApiClient
{
    public async Task<TimelineRuntimeStatus> GetTimelineRuntimeStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineRuntimeStatus>(
                    "timeline/runtime/status",
                    JsonOptions,
                    cancellationToken)
                ?? NewLocalApiUnavailableRuntimeStatus("Timeline の起動状態を取得できませんでした。");
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Failed to load Timeline runtime status.");
            return NewLocalApiUnavailableRuntimeStatus("Timeline の操作機能に接続できません。Timeline を起動し直してください。");
        }
    }

    public async Task<TimelineRuntimeControlResult> StopTimelineAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync("timeline/runtime/stop", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessageFromBody(body)
                ?? $"Timeline の停止を開始できませんでした。HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TimelineRuntimeControlResult>(JsonOptions, cancellationToken)
            ?? new TimelineRuntimeControlResult
            {
                Accepted = false,
                State = "empty",
                Message = "Timeline の停止結果が空でした。",
            };
    }

    private static TimelineRuntimeStatus NewLocalApiUnavailableRuntimeStatus(string message)
    {
        return new TimelineRuntimeStatus
        {
            Available = false,
            State = "local_api_unreachable",
            Severity = "danger",
            Message = message,
            Components =
            [
                new TimelineRuntimeComponentStatus
                {
                    Id = "web",
                    Label = "Web画面",
                    Kind = "web",
                    Available = true,
                    State = "running",
                    Severity = "ok",
                    Message = "Timeline の画面を表示できています。",
                },
                new TimelineRuntimeComponentStatus
                {
                    Id = "local-api",
                    Label = "操作機能",
                    Kind = "local-api",
                    Available = false,
                    State = "unreachable",
                    Severity = "danger",
                    Message = message,
                },
            ],
            Worker = new TimelineDockerWorkerStatus
            {
                Available = false,
                State = "local_api_unreachable",
                Message = message,
            },
        };
    }
}
