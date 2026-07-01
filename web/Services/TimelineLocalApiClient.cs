using System.Net.Http.Json;
using System.Text.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineLocalApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<TimelineLocalApiClient> _logger;
    private readonly TimelineStoreService _localStore;
    private readonly TimelineDashboardStatsService _localDashboardStats;

    public TimelineLocalApiClient(
        HttpClient http,
        ILogger<TimelineLocalApiClient> logger,
        TimelineStoreService localStore,
        TimelineDashboardStatsService localDashboardStats)
    {
        _http = http;
        _logger = logger;
        _localStore = localStore;
        _localDashboardStats = localDashboardStats;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await _http.GetFromJsonAsync<LocalApiHealth>("health", JsonOptions, cancellationToken);
            return health?.Ok == true;
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "Timeline Local API health check failed.");
            return false;
        }
    }

    private async Task<T> GetRequiredJsonAsync<T>(
        string url,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetFromJsonAsync<T>(url, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException($"{failureMessage} 応答が空です。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogOptionalLocalApiReadFailure(ex, "{Message}", failureMessage);
            throw new InvalidOperationException(failureMessage, ex);
        }
    }

    private void LogOptionalLocalApiReadFailure(Exception ex, string message, params object?[] args)
    {
        if (IsExpectedLocalApiConnectionFailure(ex))
        {
            _logger.LogDebug(ex, message, args);
            return;
        }

        _logger.LogWarning(ex, message, args);
    }

    private static bool IsExpectedLocalApiConnectionFailure(Exception ex)
    {
        if (ex is HttpRequestException httpRequestException)
        {
            return httpRequestException.StatusCode is null;
        }

        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is System.Net.Sockets.SocketException)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ErrorMessageFromBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
        }

        return body.Trim();
    }

    private TimelineStoreOverview GetLocalStoreOverviewFallback()
        => _localStore.GetWebOverview();

    private TimelineDashboardStats GetLocalDashboardStatsFallback(
        string range,
        string bucket,
        int days)
        => ConvertDashboardStats(_localDashboardStats.GetStats(range, bucket, days));

    private static TimelineDashboardStats ConvertDashboardStats(TimelineDashboardStatsResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        return JsonSerializer.Deserialize<TimelineDashboardStats>(json, JsonOptions)
            ?? new TimelineDashboardStats
            {
                Available = response.Available,
                StoreDirectory = response.StoreDirectory,
                Message = response.Message,
            };
    }
}
