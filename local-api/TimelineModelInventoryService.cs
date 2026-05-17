using System.Text.Json.Nodes;

public sealed class TimelineModelInventoryService
{
    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineProductApiClient _api;
    private JsonObject? _audioModelCache;
    private DateTimeOffset _audioModelCacheAt;
    private JsonObject? _imageModelCache;
    private DateTimeOffset _imageModelCacheAt;

    public TimelineModelInventoryService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        TimelineProductApiClient api)
    {
        _settings = settings;
        _operations = operations;
        _api = api;
    }

    public JsonObject GetAudioModels()
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForAudio",
            "audio_models",
            "started",
            "Web operation started.");

        try
        {
            var result = GetAudioModelsCore();
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForAudio",
                "audio_models",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["available"] = GetBool(result, "available", false),
                    ["message"] = GetString(result, "message", string.Empty),
                });
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForAudio",
                "audio_models",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    public async Task<JsonObject> GetImageModelsAsync(CancellationToken cancellationToken)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForImage",
            "image_models",
            "started",
            "Web operation started.");

        try
        {
            var result = await GetImageModelsCoreAsync(operationId, cancellationToken);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForImage",
                "image_models",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["available"] = GetBool(result, "available", false),
                    ["message"] = GetString(result, "message", string.Empty),
                });
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForImage",
                "image_models",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private JsonObject GetAudioModelsCore()
    {
        var now = DateTimeOffset.Now;
        if (_audioModelCache is not null && now - _audioModelCacheAt < TimeSpan.FromMinutes(15))
        {
            return (JsonObject)_audioModelCache.DeepClone();
        }

        var result = new JsonObject
        {
            ["available"] = true,
            ["message"] = string.Empty,
            ["generatedAt"] = now.ToString("s"),
            ["pipelineName"] = "TimelineForAudio",
            ["pipelineVersion"] = string.Empty,
            ["models"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "speaker_diarization",
                    ["displayName"] = "Speaker diarization",
                    ["source"] = "huggingface",
                    ["modelId"] = "pyannote/speaker-diarization-community-1",
                    ["backend"] = "pyannote",
                    ["required"] = true,
                    ["configured"] = true,
                    ["requiresHuggingFaceToken"] = true,
                    ["requiresAccessApproval"] = true,
                    ["unitType"] = "speaker turns",
                    ["url"] = "https://huggingface.co/pyannote/speaker-diarization-community-1",
                    ["license"] = string.Empty,
                    ["gated"] = string.Empty,
                    ["remoteStatus"] = "not checked",
                    ["remoteMessage"] = string.Empty,
                    ["notes"] = new JsonArray(),
                },
                new JsonObject
                {
                    ["role"] = "acoustic-units",
                    ["displayName"] = "ZIPA large ONNX",
                    ["source"] = "local",
                    ["modelId"] = "zipa-large-onnx",
                    ["backend"] = "onnx",
                    ["required"] = true,
                    ["configured"] = true,
                    ["requiresHuggingFaceToken"] = false,
                    ["requiresAccessApproval"] = false,
                    ["unitType"] = "acoustic units",
                    ["url"] = string.Empty,
                    ["license"] = string.Empty,
                    ["gated"] = string.Empty,
                    ["remoteStatus"] = "local",
                    ["remoteMessage"] = string.Empty,
                    ["notes"] = new JsonArray(),
                },
            },
        };

        _audioModelCache = (JsonObject)result.DeepClone();
        _audioModelCacheAt = now;
        return result;
    }

    private async Task<JsonObject> GetImageModelsCoreAsync(
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (_imageModelCache is not null && now - _imageModelCacheAt < TimeSpan.FromMinutes(15))
        {
            return (JsonObject)_imageModelCache.DeepClone();
        }

        var productPath = GetProductPath("image");
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            return NewImageModelUnavailable(now, "TimelineForImage was not found.");
        }

        try
        {
            var payload = await _api.PostJsonAsync(
                "image",
                "TimelineForImage",
                "/models/list",
                new JsonObject(),
                120,
                parentOperationId,
                cancellationToken);
            var result = ConvertImageModelInventory(payload);
            _imageModelCache = (JsonObject)result.DeepClone();
            _imageModelCacheAt = now;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return NewImageModelUnavailable(now, ex.Message);
        }
    }

    private static JsonObject ConvertImageModelInventory(JsonNode? payload)
    {
        var models = new JsonArray();
        if (payload is JsonObject obj && GetNode(obj, "models") is JsonArray rows)
        {
            foreach (var rowNode in rows)
            {
                if (rowNode is not JsonObject row)
                {
                    continue;
                }

                var modelId = GetStringAny(row, ["model_id", "modelId", "id"], string.Empty);
                var role = GetString(row, "role", string.Empty);
                var local = GetBool(row, "local", false);
                var externalApi = GetBoolAny(row, ["external_api", "externalApi"], false);
                var source = local ? "local" : externalApi ? "external" : string.Empty;
                var notes = new JsonArray();
                if (GetNode(row, "notes") is JsonArray noteRows)
                {
                    foreach (var note in noteRows)
                    {
                        var text = ConvertNodeToString(note);
                        if (!string.IsNullOrEmpty(text))
                        {
                            notes.Add(text);
                        }
                    }
                }

                models.Add(new JsonObject
                {
                    ["role"] = role,
                    ["displayName"] = modelId,
                    ["source"] = source,
                    ["modelId"] = modelId,
                    ["backend"] = role,
                    ["required"] = true,
                    ["configured"] = true,
                    ["requiresHuggingFaceToken"] = false,
                    ["requiresAccessApproval"] = false,
                    ["unitType"] = role,
                    ["url"] = string.Empty,
                    ["license"] = string.Empty,
                    ["gated"] = string.Empty,
                    ["remoteStatus"] = local ? "local" : externalApi ? "external" : string.Empty,
                    ["remoteMessage"] = string.Empty,
                    ["notes"] = notes,
                });
            }
        }

        return new JsonObject
        {
            ["available"] = true,
            ["message"] = string.Empty,
            ["generatedAt"] = DateTime.Now.ToString("s"),
            ["pipelineName"] = "TimelineForImage",
            ["pipelineVersion"] = string.Empty,
            ["models"] = models,
        };
    }

    private static JsonObject NewImageModelUnavailable(DateTimeOffset now, string message)
    {
        return new JsonObject
        {
            ["available"] = false,
            ["message"] = message,
            ["generatedAt"] = now.ToString("s"),
            ["pipelineName"] = "TimelineForImage",
            ["pipelineVersion"] = string.Empty,
            ["models"] = new JsonArray(),
        };
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

    private static string GetString(JsonObject source, string name, string fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertNodeToString(node);
    }

    private static bool GetBool(JsonObject source, string name, bool fallback)
    {
        var text = GetString(source, name, string.Empty);
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        return text.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static bool GetBoolAny(JsonObject source, string[] names, bool fallback)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is null)
            {
                continue;
            }

            return ConvertNodeToString(node).ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on" => true,
                "false" or "0" or "no" or "off" => false,
                _ => fallback,
            };
        }

        return fallback;
    }

    private static string GetStringAny(JsonObject source, string[] names, string fallback)
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

    private static string ConvertNodeToString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        try
        {
            return ConvertTimelineText(node.GetValue<object>());
        }
        catch (InvalidOperationException)
        {
            return node.ToJsonString();
        }
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
