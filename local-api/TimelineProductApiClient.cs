using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class TimelineProductApiClient
{
    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly HttpClient _http;

    public TimelineProductApiClient(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        HttpClient http)
    {
        _settings = settings;
        _operations = operations;
        _http = http;
    }

    public async Task<JsonNode?> PostJsonAsync(
        string productId,
        string productName,
        string path,
        JsonObject? requestBody,
        int timeoutSeconds,
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        var productPath = GetProductPath(productId);
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            throw new InvalidOperationException($"{productName} product directory was not found: {productPath}");
        }

        var baseUrl = GetProductHealthBaseUrl(productId, productPath);
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException($"{productName} API base URL was not resolved.");
        }

        await AssertProductApiAccessAllowedAsync(productName, baseUrl, cancellationToken);

        var operationId = _operations.NewOperationId("api");
        var url = baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "api",
            productName,
            path,
            "started",
            "Product API request started.",
            commandLine: url,
            parentOperationId: parentOperationId);

        var failureLogged = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var content = new StringContent(
                (requestBody ?? new JsonObject()).ToJsonString(),
                Encoding.UTF8,
                "application/json");
            using var response = await _http.PostAsync(url, content, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            var payload = TryParseJson(text);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                var message = GetJsonErrorMessage(payload);
                if (string.IsNullOrEmpty(message))
                {
                    message = !string.IsNullOrWhiteSpace(text)
                        ? text.Trim()
                        : $"{(int)response.StatusCode} {response.ReasonPhrase}";
                }

                _operations.WriteOperationEvent(
                    operationId,
                    "api",
                    productName,
                    path,
                    "failed",
                    message,
                    commandLine: url,
                    exitCode: (int)response.StatusCode,
                    durationMs: durationMs,
                    stdout: text,
                    parentOperationId: parentOperationId);
                failureLogged = true;
                throw new InvalidOperationException($"{productName} API failed: {message}");
            }

            _operations.WriteOperationEvent(
                operationId,
                "api",
                productName,
                path,
                "completed",
                "Product API request completed.",
                commandLine: url,
                exitCode: (int)response.StatusCode,
                durationMs: durationMs,
                stdout: text,
                parentOperationId: parentOperationId);
            return payload;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!failureLogged)
            {
                var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
                _operations.WriteOperationEvent(
                    operationId,
                    "api",
                    productName,
                    path,
                    "error",
                    ex.Message,
                    commandLine: url,
                    durationMs: durationMs,
                    stderr: ex.Message,
                    parentOperationId: parentOperationId);
            }
            throw;
        }
    }

    public async Task<JsonNode?> GetJsonAsync(
        string productId,
        string productName,
        string path,
        int timeoutSeconds,
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        var productPath = GetProductPath(productId);
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            throw new InvalidOperationException($"{productName} product directory was not found: {productPath}");
        }

        var baseUrl = GetProductHealthBaseUrl(productId, productPath);
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException($"{productName} API base URL was not resolved.");
        }

        await AssertProductApiAccessAllowedAsync(productName, baseUrl, cancellationToken);

        var operationId = _operations.NewOperationId("api");
        var url = baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "api",
            productName,
            path,
            "started",
            "Product API request started.",
            commandLine: url,
            parentOperationId: parentOperationId);

        var failureLogged = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await _http.GetAsync(url, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            var payload = TryParseJson(text);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                var message = GetJsonErrorMessage(payload);
                if (string.IsNullOrEmpty(message))
                {
                    message = !string.IsNullOrWhiteSpace(text)
                        ? text.Trim()
                        : $"{(int)response.StatusCode} {response.ReasonPhrase}";
                }

                _operations.WriteOperationEvent(
                    operationId,
                    "api",
                    productName,
                    path,
                    "failed",
                    message,
                    commandLine: url,
                    exitCode: (int)response.StatusCode,
                    durationMs: durationMs,
                    stdout: text,
                    parentOperationId: parentOperationId);
                failureLogged = true;
                throw new InvalidOperationException($"{productName} API failed: {message}");
            }

            _operations.WriteOperationEvent(
                operationId,
                "api",
                productName,
                path,
                "completed",
                "Product API request completed.",
                commandLine: url,
                exitCode: (int)response.StatusCode,
                durationMs: durationMs,
                stdout: text,
                parentOperationId: parentOperationId);
            return payload;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!failureLogged)
            {
                var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
                _operations.WriteOperationEvent(
                    operationId,
                    "api",
                    productName,
                    path,
                    "error",
                    ex.Message,
                    commandLine: url,
                    durationMs: durationMs,
                    stderr: ex.Message,
                    parentOperationId: parentOperationId);
            }
            throw;
        }
    }

    private async Task AssertProductApiAccessAllowedAsync(
        string productName,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var text = await _http.GetStringAsync(baseUrl.TrimEnd('/') + "/health", timeout.Token);
            if (TestHealthValue(text))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        throw new InvalidOperationException($"{productName} is not running. Start the product explicitly before accessing this endpoint.");
    }

    private string GetProductPath(string productId)
    {
        foreach (var product in _settings.ReadSettings().ProductRegistry.Products)
        {
            if (product.Id.Equals(productId, StringComparison.OrdinalIgnoreCase))
            {
                return product.Path ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private string GetProductHealthBaseUrl(string productId, string productPath)
    {
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            return string.Empty;
        }

        var environmentOverride = GetProductApiBaseUrlOverride(productId);
        if (!string.IsNullOrEmpty(environmentOverride))
        {
            return environmentOverride;
        }

        var settings = ReadJsonObject(Path.Combine(productPath, "settings.json"));
        var runtime = GetObject(settings, "runtime");

        var apiBaseUrl = GetStringAny(runtime, ["apiBaseUrl", "api_base_url"], string.Empty);
        if (string.IsNullOrEmpty(apiBaseUrl))
        {
            apiBaseUrl = GetStringAny(settings, ["apiBaseUrl", "api_base_url"], string.Empty);
        }
        if (!string.IsNullOrEmpty(apiBaseUrl))
        {
            return apiBaseUrl.TrimEnd('/');
        }

        var hostName = GetStringAny(runtime, ["apiHost", "api_host"], string.Empty);
        if (string.IsNullOrEmpty(hostName))
        {
            hostName = GetStringAny(settings, ["apiHost", "api_host"], string.Empty);
        }
        if (string.IsNullOrEmpty(hostName) || hostName == "0.0.0.0")
        {
            hostName = "127.0.0.1";
        }

        var port = GetApiPort(GetNodeAny(runtime, ["apiPort", "api_port"]));
        if (port <= 0)
        {
            port = GetApiPort(GetNodeAny(settings, ["apiPort", "api_port"]));
        }
        if (port <= 0)
        {
            port = GetDefaultApiPort(productId);
        }
        if (port <= 0)
        {
            return string.Empty;
        }

        return $"http://{hostName}:{port}";
    }

    private static string GetProductApiBaseUrlOverride(string productId)
    {
        var normalized = new string(productId
            .Select(value => char.IsLetterOrDigit(value) ? char.ToUpperInvariant(value) : '_')
            .ToArray());
        foreach (var name in new[]
        {
            $"TIMELINE_PRODUCT_{normalized}_API_BASE_URL",
            $"TIMELINE_{normalized}_API_BASE_URL",
        })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim().TrimEnd('/');
            }
        }

        return string.Empty;
    }

    private static JsonNode? TryParseJson(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(trimmed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetJsonErrorMessage(JsonNode? payload)
    {
        if (payload is not JsonObject obj)
        {
            return string.Empty;
        }

        if (GetNode(obj, "error") is JsonObject error)
        {
            var nested = GetStringAny(error, ["message"], string.Empty);
            if (!string.IsNullOrEmpty(nested))
            {
                return nested;
            }
        }

        return GetStringAny(obj, ["message"], string.Empty);
    }

    private static JsonObject? ReadJsonObject(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TestHealthValue(string value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try
            {
                return TestHealthValue(JsonNode.Parse(text));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TestHealthValue(JsonNode? value)
    {
        if (value is null)
        {
            return false;
        }
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }
            if (scalar.TryGetValue<string>(out var textValue))
            {
                return TestHealthValue(textValue);
            }
        }
        if (value is JsonObject obj)
        {
            foreach (var name in new[] { "ok", "healthy", "running", "ready", "status" })
            {
                var node = GetNode(obj, name);
                if (node is null)
                {
                    continue;
                }
                if (name.Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    var status = ConvertNodeToString(node);
                    return status.Equals("ok", StringComparison.OrdinalIgnoreCase)
                        || status.Equals("healthy", StringComparison.OrdinalIgnoreCase)
                        || status.Equals("running", StringComparison.OrdinalIgnoreCase)
                        || status.Equals("ready", StringComparison.OrdinalIgnoreCase);
                }

                return TestHealthValue(node);
            }
        }

        return TestHealthValue(ConvertNodeToString(value));
    }

    private static JsonObject? GetObject(JsonObject? source, string name)
    {
        return GetNode(source, name) as JsonObject;
    }

    private static JsonNode? GetNodeAny(JsonObject? source, string[] names)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is not null)
            {
                return node;
            }
        }

        return null;
    }

    private static JsonNode? GetNode(JsonObject? source, string name)
    {
        if (source is null)
        {
            return null;
        }
        if (source.TryGetPropertyValue(name, out var node))
        {
            return node;
        }

        foreach (var property in source)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string GetStringAny(JsonObject? source, string[] names, string fallback)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is not null)
            {
                return ConvertNodeToString(node);
            }
        }

        return fallback;
    }

    private static int GetApiPort(JsonNode? node)
    {
        var text = ConvertNodeToString(node);
        return int.TryParse(text, out var port) && port is >= 1 and <= 65535 ? port : 0;
    }

    private static int GetDefaultApiPort(string productId)
    {
        return productId switch
        {
            "audio" => 19100,
            "windows-codex" => 19200,
            "chatgpt" => 19300,
            "image" => 19400,
            "video" => 19500,
            "pc" => 19600,
            _ => 0,
        };
    }

    private static string ConvertNodeToString(JsonNode? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var textValue))
            {
                return ConvertTimelineText(textValue);
            }
            if (scalar.TryGetValue<int>(out var intValue))
            {
                return intValue.ToString();
            }
            if (scalar.TryGetValue<long>(out var longValue))
            {
                return longValue.ToString();
            }
            if (scalar.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (scalar.TryGetValue<bool>(out var boolValue))
            {
                return boolValue ? "true" : "false";
            }
        }

        return ConvertTimelineText(value.ToJsonString());
    }

    private static string ConvertTimelineText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
