using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class TimelineProductSettingsService
{
    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineProductApiClient _api;

    public TimelineProductSettingsService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        TimelineProductApiClient api)
    {
        _settings = settings;
        _operations = operations;
        _api = api;
    }

    public void SaveAudioSettings(JsonObject? request)
    {
        InvokeSaveOperation(
            "TimelineForAudio",
            "audio_settings_save",
            () =>
            {
                var productPath = GetRequiredProductPath("audio", "TimelineForAudio");
                var current = ReadSettingsPayload(productPath);
                var payload = CloneObject(current);
                payload["schemaVersion"] = 1;
                payload["inputRoots"] = NewStringArray(GetRootPaths(request, "inputRoots"));
                payload["outputRoot"] = GetOutputPath(
                    request,
                    current,
                    ["outputRoot"],
                    ["outputPath"],
                    string.Empty);

                var defaultComputeMode = _settings.GetResolvedCommonAiComputeMode();
                var computeMode = GetString(request, "computeMode", GetString(current, "computeMode", defaultComputeMode)).ToLowerInvariant();
                payload["computeMode"] = computeMode is "cpu" or "gpu" ? computeMode : defaultComputeMode;

                if (TryGetNode(request, "token", out var tokenNode)
                    || TryGetNode(request, "huggingFaceToken", out tokenNode)
                    || TryGetNode(request, "huggingfaceToken", out tokenNode))
                {
                    payload["huggingFaceToken"] = ConvertTimelineText(tokenNode).Trim();
                }
                else if (!TryGetNode(payload, "huggingFaceToken", out _)
                    && TryGetNode(current, "huggingfaceToken", out var existingLowerToken))
                {
                    payload["huggingFaceToken"] = ConvertTimelineText(existingLowerToken).Trim();
                }

                WriteSettingsPayload(productPath, payload);
                CreateDirectoryIfPath(payload["outputRoot"]);
            });
    }

    public void SaveImageSettings(JsonObject? request)
    {
        InvokeSaveOperation(
            "TimelineForImage",
            "image_settings_save",
            () =>
            {
                var productPath = GetRequiredProductPath("image", "TimelineForImage");
                var current = ReadSettingsPayload(productPath);
                var payload = CloneObject(current);
                payload["schemaVersion"] = 1;
                payload["inputRoots"] = NewStringArray(GetRootPaths(request, "inputRoots"));
                payload["outputRoot"] = GetOutputPath(
                    request,
                    current,
                    ["outputRoot"],
                    ["outputRootPath"],
                    GetManagedProductDataDirectory("image"));

                var defaultComputeMode = _settings.GetResolvedCommonAiComputeMode();
                var computeMode = GetString(request, "computeMode", GetString(current, "computeMode", defaultComputeMode)).ToLowerInvariant();
                payload["computeMode"] = computeMode is "auto" or "cpu" or "gpu" ? computeMode : defaultComputeMode;

                WriteSettingsPayload(productPath, payload);
                CreateDirectoryIfPath(payload["outputRoot"]);
            });
    }

    public void SaveVideoSettings(JsonObject? request)
    {
        InvokeSaveOperation(
            "TimelineForVideo",
            "video_settings_save",
            () =>
            {
                var productPath = GetRequiredProductPath("video", "TimelineForVideo");
                var current = ReadSettingsPayload(productPath);
                var payload = CloneObject(current);
                payload["schemaVersion"] = 1;
                payload["inputRoots"] = NewStringArray(GetRootPaths(request, "inputRoots"));
                payload["outputRoot"] = GetOutputPath(
                    request,
                    current,
                    ["outputRoot"],
                    ["outputRootPath"],
                    GetManagedProductDataDirectory("video"));

                var defaultComputeMode = _settings.GetResolvedCommonAiComputeMode();
                var computeMode = GetString(request, "computeMode", GetString(current, "computeMode", defaultComputeMode)).ToLowerInvariant();
                payload["computeMode"] = computeMode is "cpu" or "gpu" ? computeMode : defaultComputeMode;

                if (TryGetNode(request, "token", out var tokenNode)
                    || TryGetNode(request, "huggingFaceToken", out tokenNode)
                    || TryGetNode(request, "huggingfaceToken", out tokenNode))
                {
                    payload["huggingFaceToken"] = ConvertTimelineText(tokenNode).Trim();
                }
                else if (!TryGetNode(payload, "huggingFaceToken", out _)
                    && TryGetNode(current, "huggingfaceToken", out var existingLowerToken))
                {
                    payload["huggingFaceToken"] = ConvertTimelineText(existingLowerToken).Trim();
                }

                WriteSettingsPayload(productPath, payload);
                CreateDirectoryIfPath(payload["outputRoot"]);
            });
    }

    public void SavePcSettings(JsonObject? request)
    {
        InvokeSaveOperation(
            "TimelineForPcInfo",
            "pc_settings_save",
            () =>
            {
                var productPath = GetRequiredProductPath("pc", "TimelineForPcInfo");
                var current = ReadSettingsPayload(productPath);
                var payload = CloneObject(current);
                payload["schemaVersion"] = 1;
                payload["outputRoot"] = GetOutputPath(
                    request,
                    current,
                    ["outputRoot"],
                    ["outputRootPath"],
                    GetManagedProductDataDirectory("pc"));

                if (TryGetNode(request, "redactionProfile", out var redactionNode)
                    || TryGetNode(request, "redaction_profile", out redactionNode))
                {
                    payload["redactionProfile"] = ConvertTimelineText(redactionNode);
                }

                if (TryGetNode(request, "mockProfile", out var mockNode)
                    || TryGetNode(request, "mock_profile", out mockNode))
                {
                    payload["mockProfile"] = ConvertTimelineText(mockNode);
                }

                WriteSettingsPayload(productPath, payload);
                CreateDirectoryIfPath(payload["outputRoot"]);
            });
    }

    public Task<JsonObject> SavePcSettingsAsync(JsonObject? request, CancellationToken cancellationToken)
    {
        return InvokeSaveOperationAsync(
            "TimelineForPcInfo",
            "pc_settings_save",
            operationId => SavePcSettingsCoreAsync(request, operationId, cancellationToken));
    }

    public void SaveWindowsCodexSettings(JsonObject? request)
    {
        InvokeSaveOperation(
            "TimelineForWindowsCodex",
            "windows_codex_settings_save",
            () =>
            {
                var productPath = GetRequiredProductPath("windows-codex", "TimelineForWindowsCodex");
                var current = ReadSettingsPayload(productPath);
                var payload = CloneObject(current);
                payload["schemaVersion"] = 1;
                var outputRoot = GetOutputPath(
                    request,
                    current,
                    ["outputsRoot", "outputRoot"],
                    ["outputsRootPath", "outputRootPath"],
                    GetManagedProductDataDirectory("windows-codex"));
                payload["outputRoot"] = outputRoot;
                payload["outputs_root"] = outputRoot;

                WriteSettingsPayload(productPath, payload);
                CreateDirectoryIfPath(payload["outputRoot"]);
            });
    }

    public void SaveChatGptSettings(JsonObject? request)
    {
        InvokeSaveOperation(
            "TimelineForChatGPT",
            "chatgpt_settings_save",
            () =>
            {
                var productPath = GetRequiredProductPath("chatgpt", "TimelineForChatGPT");
                var current = ReadSettingsPayload(productPath);
                var payload = CloneObject(current);
                payload["schemaVersion"] = 1;
                var outputRoot = GetOutputPath(
                    request,
                    current,
                    ["outputRoot", "masterRoot"],
                    ["outputRootPath", "masterRootPath"],
                    GetManagedProductDataDirectory("chatgpt"));
                if (string.IsNullOrWhiteSpace(outputRoot))
                {
                    throw new InvalidOperationException("Output directory is required.");
                }

                payload["outputRoot"] = outputRoot;

                WriteSettingsPayload(productPath, payload);
                CreateDirectoryIfPath(payload["outputRoot"]);
            });
    }

    private void InvokeSaveOperation(string productName, string action, Action operation)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            productName,
            action,
            "started",
            "Web operation started.");

        try
        {
            operation();
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                productName,
                action,
                "completed",
                "Web operation completed.",
                durationMs: durationMs);
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                productName,
                action,
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private async Task<JsonObject> InvokeSaveOperationAsync(
        string productName,
        string action,
        Func<string, Task<JsonObject>> operation)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            productName,
            action,
            "started",
            "Web operation started.");

        try
        {
            var result = await operation(operationId);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                productName,
                action,
                "completed",
                "Web operation completed.",
                durationMs: durationMs);
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                productName,
                action,
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private async Task<JsonObject> SavePcSettingsCoreAsync(
        JsonObject? request,
        string operationId,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject();
        var outputRoot = GetOutputPath(
            request,
            new JsonObject(),
            ["outputRoot"],
            ["outputRootPath"],
            string.Empty);
        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            body["outputRoot"] = outputRoot;
        }

        var instanceName = GetStringAny(request, ["instanceName", "instance_name"], string.Empty);
        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            body["instanceName"] = instanceName;
        }

        var apiPort = GetStringAny(request, ["apiPort", "api_port"], string.Empty);
        if (!string.IsNullOrWhiteSpace(apiPort))
        {
            body["apiPort"] = apiPort;
        }

        var payload = await _api.PostJsonAsync(
            "pc",
            "TimelineForPcInfo",
            "/settings/save",
            body,
            60,
            operationId,
            cancellationToken) as JsonObject ?? new JsonObject();
        CreateDirectoryIfPath(payload["outputRoot"]);
        return payload;
    }

    private string GetRequiredProductPath(string productId, string displayName)
    {
        foreach (var product in _settings.ReadSettings().ProductRegistry.Products)
        {
            if (product.Id.Equals(productId, StringComparison.OrdinalIgnoreCase))
            {
                var path = ConvertTimelineText(product.Path);
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    return path;
                }

                throw new InvalidOperationException($"{displayName} was not found: {path}");
            }
        }

        throw new InvalidOperationException($"{displayName} was not registered.");
    }

    private string GetManagedProductDataDirectory(string productId)
    {
        return Path.Combine(_settings.GetDataRootDirectory(), "to_text", productId);
    }

    private static JsonObject ReadSettingsPayload(string productPath)
    {
        var settingsPath = Path.Combine(productPath, "settings.json");
        if (!File.Exists(settingsPath))
        {
            settingsPath = Path.Combine(productPath, "settings.example.json");
        }

        if (!File.Exists(settingsPath))
        {
            return new JsonObject
            {
                ["schemaVersion"] = 1,
            };
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject
                ?? new JsonObject { ["schemaVersion"] = 1 };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new JsonObject
            {
                ["schemaVersion"] = 1,
            };
        }
    }

    private static void WriteSettingsPayload(string productPath, JsonObject payload)
    {
        Directory.CreateDirectory(productPath);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };
        File.WriteAllText(
            Path.Combine(productPath, "settings.json"),
            payload.ToJsonString(options) + Environment.NewLine);
    }

    private static JsonObject CloneObject(JsonObject source)
        => (JsonObject)source.DeepClone();

    private static List<string> GetRootPaths(JsonObject? request, string name)
    {
        var roots = new List<string>();
        var node = GetNode(request, name);
        if (node is not JsonArray array)
        {
            return roots;
        }

        foreach (var item in array)
        {
            var path = item is JsonObject root
                ? GetString(root, "path", string.Empty)
                : ConvertTimelineText(item);
            if (!string.IsNullOrWhiteSpace(path))
            {
                roots.Add(path.Trim());
            }
        }

        return roots;
    }

    private static string GetOutputPath(
        JsonObject? request,
        JsonObject current,
        string[] objectNames,
        string[] directNames,
        string fallback)
    {
        foreach (var name in objectNames)
        {
            if (!TryGetNode(request, name, out var node))
            {
                continue;
            }

            if (node is JsonObject root)
            {
                var path = GetString(root, "path", string.Empty);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path.Trim();
                }
            }
            else
            {
                var path = ConvertTimelineText(node);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path.Trim();
                }
            }
        }

        foreach (var name in directNames)
        {
            var path = GetString(request, name, string.Empty);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path.Trim();
            }
        }

        foreach (var name in objectNames)
        {
            if (!TryGetNode(current, name, out var node))
            {
                continue;
            }

            var path = node is JsonObject root
                ? GetString(root, "path", string.Empty)
                : ConvertTimelineText(node);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path.Trim();
            }
        }

        return fallback;
    }

    private static JsonArray NewStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static void CreateDirectoryIfPath(JsonNode? pathNode)
    {
        var path = ConvertTimelineText(pathNode);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private static JsonNode? GetNode(JsonObject? source, string name)
        => TryGetNode(source, name, out var node) ? node : null;

    private static bool TryGetNode(JsonObject? source, string name, out JsonNode? node)
    {
        node = null;
        if (source is null)
        {
            return false;
        }

        foreach (var property in source)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                node = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertTimelineText(node);
    }

    private static string GetStringAny(JsonObject? source, string[] names, string fallback)
    {
        foreach (var name in names)
        {
            var value = GetString(source, name, string.Empty);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string ConvertTimelineText(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonNode node)
        {
            try
            {
                return node.GetValue<object>()?.ToString()?.Trim() ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }

        return value.ToString()?.Trim() ?? string.Empty;
    }
}
