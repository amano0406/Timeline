using System.Net.Http.Json;
using System.Text.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineHelperClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<TimelineHelperClient> _logger;

    public TimelineHelperClient(HttpClient http, ILogger<TimelineHelperClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await _http.GetFromJsonAsync<HelperHealth>("health", JsonOptions, cancellationToken);
            return health?.Ok == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timeline helper health check failed.");
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
            _logger.LogWarning(ex, "{Message}", failureMessage);
            throw new InvalidOperationException(failureMessage, ex);
        }
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
}
