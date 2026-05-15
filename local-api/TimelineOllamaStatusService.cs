using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed class TimelineOllamaStatusService
{
    private readonly HttpClient _http;
    private readonly TimelineSettingsService _settings;

    public TimelineOllamaStatusService(HttpClient http, TimelineSettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task<TimelineOllamaStatusResponse> GetStatusAsync(
        string? baseUrl,
        string? model,
        CancellationToken cancellationToken)
    {
        var settings = _settings.ReadSettings().AudioVerbalization;
        var resolvedBaseUrl = ConvertTimelineText(baseUrl);
        if (string.IsNullOrEmpty(resolvedBaseUrl))
        {
            resolvedBaseUrl = ConvertTimelineText(settings.OllamaBaseUrl);
        }
        if (string.IsNullOrEmpty(resolvedBaseUrl))
        {
            resolvedBaseUrl = "http://127.0.0.1:11434";
        }

        var resolvedModel = ConvertTimelineText(model);
        if (string.IsNullOrEmpty(resolvedModel))
        {
            resolvedModel = ConvertTimelineText(settings.Model);
        }

        var tagsUrl = resolvedBaseUrl.TrimEnd('/') + "/api/tags";

        try
        {
            using var response = await _http.GetAsync(tagsUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            var modelNames = ReadModelNames(payload);
            var modelAvailable = modelNames.Any(name => name.Equals(resolvedModel, StringComparison.OrdinalIgnoreCase));

            return new TimelineOllamaStatusResponse
            {
                Available = true,
                BaseUrl = resolvedBaseUrl,
                Model = resolvedModel,
                ModelAvailable = modelAvailable,
                ModelNames = modelNames,
                Message = modelAvailable
                    ? "Ollama is available."
                    : "Ollama is running, but the configured model was not found.",
            };
        }
        catch
        {
            return new TimelineOllamaStatusResponse
            {
                Available = false,
                BaseUrl = resolvedBaseUrl,
                Model = resolvedModel,
                ModelAvailable = false,
                ModelNames = [],
                Message = "Ollama is not reachable.",
            };
        }
    }

    private static List<string> ReadModelNames(JsonObject? payload)
    {
        var result = new List<string>();
        if (payload is null || GetNode(payload, "models") is not JsonArray models)
        {
            return result;
        }

        foreach (var item in models.OfType<JsonObject>())
        {
            var name = GetStringAny(item, ["name", "model"], string.Empty);
            if (!string.IsNullOrEmpty(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static JsonNode? GetNode(JsonObject source, string name)
    {
        foreach (var property in source)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string GetStringAny(JsonObject source, string[] names, string fallback)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is null)
            {
                continue;
            }

            try
            {
                return ConvertTimelineText(node.GetValue<object>());
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }

        return fallback;
    }

    private static string ConvertTimelineText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }
}

public sealed class TimelineOllamaStatusResponse
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("modelAvailable")]
    public bool ModelAvailable { get; set; }

    [JsonPropertyName("modelNames")]
    public List<string> ModelNames { get; set; } = [];

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
