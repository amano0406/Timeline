using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed class TimelineSettingsService
{
    private static readonly string[] ProductIds =
    [
        "audio",
        "windows-codex",
        "chatgpt",
        "image",
        "video",
        "pc",
    ];

    private readonly TimelineLocalApiOptions _options;

    public TimelineSettingsService(TimelineLocalApiOptions options)
    {
        _options = options;
    }

    public TimelineSettingsResponse ReadSettings()
    {
        var payload = ReadSettingsPayload();
        var dataRoot = GetTimelineDataRootValueFromPayload(payload);
        var resolvedDataRoot = ResolveTimelineDataRootPath(dataRoot);
        var displayLanguageId = GetString(payload, "displayLanguageId", "ja-JP");
        if (!DisplayLanguageOptions.Any(option => option.Id.Equals(displayLanguageId, StringComparison.Ordinal)))
        {
            displayLanguageId = "ja-JP";
        }

        var timeZoneId = GetString(payload, "timeZoneId", "Asia/Tokyo");
        var runtime = ResolveRuntimeSettings(payload);

        return new TimelineSettingsResponse
        {
            SchemaVersion = 1,
            DataRoot = dataRoot,
            ResolvedDataRoot = resolvedDataRoot,
            DisplayLanguageId = displayLanguageId,
            DisplayLanguages = DisplayLanguageOptions,
            TimeZoneId = timeZoneId,
            TimeZones = TimeZoneOptions,
            WorkDirectory = Path.Combine(resolvedDataRoot, "work"),
            StoreDirectory = Path.Combine(resolvedDataRoot, "to_timeline"),
            Runtime = runtime,
            CommonAi = ResolveCommonAiSettingsForResponse(payload),
            ProductRegistry = ResolveProductRegistry(payload, resolvedDataRoot),
            AudioVerbalization = ResolveAudioVerbalizationSettings(displayLanguageId, GetRuntimeOllamaBaseUrl(runtime)),
        };
    }

    public TimelineSettingsResponse SaveSettings(JsonObject? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("Settings payload is required.");
        }

        var displayLanguageId = GetString(request, "displayLanguageId", string.Empty);
        if (string.IsNullOrWhiteSpace(displayLanguageId))
        {
            throw new InvalidOperationException("Display language is required.");
        }
        if (!DisplayLanguageOptions.Any(option => option.Id.Equals(displayLanguageId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Unsupported display language.");
        }

        var timeZoneId = GetString(request, "timeZoneId", string.Empty);
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new InvalidOperationException("Time zone is required.");
        }
        if (!TimeZoneOptions.Any(option => option.Id.Equals(timeZoneId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Unsupported time zone.");
        }

        var current = ReadSettingsPayload();
        var dataRoot = GetString(request, "dataRoot", string.Empty);
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = GetTimelineDataRootValueFromPayload(current);
        }

        var resolvedDataRoot = ResolveTimelineDataRootPath(dataRoot);
        var requestRuntime = GetObject(request, "runtime");
        var runtime = requestRuntime is null
            ? ResolveRuntimeSettings(current)
            : ResolveRuntimeSettings(new JsonObject { ["runtime"] = requestRuntime.DeepClone() });

        var requestRegistry = GetObject(request, "productRegistry");
        var productRegistry = requestRegistry is null
            ? ResolveProductRegistry(current, resolvedDataRoot)
            : ResolveProductRegistry(new JsonObject { ["productRegistry"] = requestRegistry.DeepClone() }, resolvedDataRoot);

        var requestCommonAi = GetObject(request, "commonAi");
        var commonAi = ResolveCommonAiSettingsForSave(requestCommonAi, GetObject(current, "commonAi"));
        var audioVerbalization = ResolveAudioVerbalizationSettings(displayLanguageId, GetRuntimeOllamaBaseUrl(runtime));

        var payload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["dataRoot"] = dataRoot,
            ["displayLanguageId"] = displayLanguageId,
            ["timeZoneId"] = timeZoneId,
            ["runtime"] = ToJsonObject(runtime),
            ["commonAi"] = commonAi,
            ["productRegistry"] = ToJsonObject(productRegistry),
            ["audioVerbalization"] = ToJsonObject(audioVerbalization),
        };

        var timelinePath = _options.TimelineProductPath;
        if (!Directory.Exists(timelinePath))
        {
            Directory.CreateDirectory(timelinePath);
        }

        WriteSettingsPayload(payload);
        Directory.CreateDirectory(resolvedDataRoot);
        Directory.CreateDirectory(Path.Combine(resolvedDataRoot, "work"));
        Directory.CreateDirectory(Path.Combine(resolvedDataRoot, "to_timeline"));
        Directory.CreateDirectory(Path.Combine(resolvedDataRoot, "logs", "operations"));

        return ReadSettings();
    }

    public string GetWorkerDirectory()
    {
        var workerDirectory = Path.Combine(GetWorkDirectory(), "worker");
        Directory.CreateDirectory(workerDirectory);
        return Path.GetFullPath(workerDirectory);
    }

    public string GetWorkDirectory()
    {
        var workDirectory = Path.Combine(GetDataRootDirectory(), "work");
        Directory.CreateDirectory(workDirectory);
        return Path.GetFullPath(workDirectory);
    }

    public string GetDataRootDirectory()
    {
        var payload = ReadSettingsPayload();
        var dataRoot = GetTimelineDataRootValueFromPayload(payload);
        var resolvedDataRoot = ResolveTimelineDataRootPath(dataRoot);
        Directory.CreateDirectory(resolvedDataRoot);
        return Path.GetFullPath(resolvedDataRoot);
    }

    public string GetStoreDirectory()
    {
        var storeDirectory = Path.Combine(GetDataRootDirectory(), "to_timeline");
        Directory.CreateDirectory(storeDirectory);
        return Path.GetFullPath(storeDirectory);
    }

    private JsonObject? ReadSettingsPayload()
    {
        var path = GetAppSettingsPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string GetAppSettingsPath() => Path.Combine(_options.TimelineProductPath, "settings.json");

    private void WriteSettingsPayload(JsonObject payload)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };
        File.WriteAllText(GetAppSettingsPath(), payload.ToJsonString(options) + Environment.NewLine);
    }

    private string GetTimelineDataRootValueFromPayload(JsonObject? payload)
    {
        var dataRoot = GetString(payload, "dataRoot", string.Empty);
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            return dataRoot;
        }

        var legacy = GetTimelineLegacyDataRootValue(payload);
        return string.IsNullOrWhiteSpace(legacy) ? "data" : legacy;
    }

    private string GetTimelineLegacyDataRootValue(JsonObject? payload)
    {
        var workParent = GetTimelineParentForNamedChild(GetString(payload, "workDirectory", string.Empty), "work");
        var storeParent = GetTimelineParentForNamedChild(GetString(payload, "storeDirectory", string.Empty), "store");
        var toTimelineParent = GetTimelineParentForNamedChild(GetString(payload, "toTimelineDirectory", string.Empty), "to_timeline");
        if (string.IsNullOrWhiteSpace(toTimelineParent))
        {
            toTimelineParent = GetTimelineParentForNamedChild(GetString(payload, "storeDirectory", string.Empty), "to_timeline");
        }

        foreach (var candidate in new[] { toTimelineParent, storeParent, workParent })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private string GetTimelineParentForNamedChild(string path, string childName)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var localPath = ConvertTimelineWindowsPath(text);
        if (string.IsNullOrEmpty(localPath))
        {
            localPath = text;
        }

        var trimmed = localPath.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        var leaf = Path.GetFileName(trimmed);
        if (!leaf.Equals(childName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return Path.GetDirectoryName(trimmed) ?? string.Empty;
    }

    private string ResolveTimelineDataRootPath(string dataRoot)
    {
        var text = ConvertTimelineText(dataRoot);
        if (string.IsNullOrEmpty(text))
        {
            text = "data";
        }

        var localPath = ConvertTimelineWindowsPath(text);
        if (string.IsNullOrEmpty(localPath))
        {
            localPath = text;
        }

        return Path.GetFullPath(Path.IsPathRooted(localPath)
            ? localPath
            : Path.Combine(_options.TimelineProductPath, localPath));
    }

    private TimelineRuntimeSettingsResponse ResolveRuntimeSettings(JsonObject? payload)
    {
        var source = GetObject(payload, "runtime");
        var instanceName = GetString(source, "instanceName", string.Empty);
        var imageTag = GetString(source, "imageTag", string.Empty);
        var webPort = GetPort(GetNode(source, "webPort"), 19000);
        var helperPortStart = GetPort(GetNode(source, "helperPortStart"), 19001);
        var helperPortEnd = GetPort(GetNode(source, "helperPortEnd"), 19010);
        if (helperPortEnd < helperPortStart)
        {
            helperPortEnd = helperPortStart;
        }

        var ollamaPort = GetPort(GetNode(source, "ollamaPort"), 11434);
        var ollamaModel = GetString(source, "ollamaModel", "qwen3.5:9b");
        if (string.IsNullOrWhiteSpace(ollamaModel))
        {
            ollamaModel = "qwen3.5:9b";
        }

        var shareOllamaVolume = GetBool(GetNode(source, "shareOllamaVolume"), true);
        var instancePart = NormalizeRuntimeNamePart(instanceName);
        var projectName = string.IsNullOrEmpty(instancePart) ? "timeline" : $"timeline-{instancePart}";
        var defaultVolume = shareOllamaVolume ? "timeline-ollama" : $"{projectName}-ollama";
        var ollamaVolumeName = GetString(source, "ollamaVolumeName", defaultVolume);
        if (string.IsNullOrWhiteSpace(ollamaVolumeName))
        {
            ollamaVolumeName = defaultVolume;
        }

        return new TimelineRuntimeSettingsResponse
        {
            InstanceName = instanceName,
            ImageTag = imageTag,
            WebPort = webPort,
            HelperPortStart = helperPortStart,
            HelperPortEnd = helperPortEnd,
            OllamaPort = ollamaPort,
            OllamaModel = ollamaModel,
            ShareOllamaVolume = shareOllamaVolume,
            OllamaVolumeName = ollamaVolumeName,
        };
    }

    private TimelineCommonAiSettingsResponse ResolveCommonAiSettingsForResponse(JsonObject? payload)
    {
        var source = GetObject(payload, "commonAi");
        var computeMode = GetString(source, "computeMode", "auto").ToLowerInvariant();
        if (computeMode is not ("auto" or "gpu" or "cpu"))
        {
            computeMode = "auto";
        }

        var token = GetStringAny(source, ["huggingFaceToken", "huggingfaceToken", "token"], string.Empty).Trim();
        return new TimelineCommonAiSettingsResponse
        {
            ComputeMode = computeMode,
            HasHuggingFaceToken = !string.IsNullOrWhiteSpace(token),
            HuggingFaceTokenPreview = GetTimelineTokenPreview(token),
        };
    }

    private static JsonObject ResolveCommonAiSettingsForSave(JsonObject? requestCommonAi, JsonObject? currentCommonAi)
    {
        var existingComputeMode = GetString(currentCommonAi, "computeMode", "auto");
        var existingToken = GetStringAny(currentCommonAi, ["huggingFaceToken", "huggingfaceToken", "token"], string.Empty);
        var computeMode = GetString(requestCommonAi, "computeMode", existingComputeMode).ToLowerInvariant();
        if (computeMode is not ("auto" or "gpu" or "cpu"))
        {
            computeMode = "auto";
        }

        var token = existingToken;
        foreach (var name in new[] { "huggingFaceToken", "huggingfaceToken", "token" })
        {
            if (TryGetNode(requestCommonAi, name, out var tokenNode))
            {
                token = ConvertTimelineText(tokenNode);
                break;
            }
        }

        return new JsonObject
        {
            ["computeMode"] = computeMode,
            ["huggingFaceToken"] = token.Trim(),
        };
    }

    private TimelineProductRegistryResponse ResolveProductRegistry(JsonObject? payload, string dataRootPath)
    {
        var defaults = NewProductRegistryDefaults(dataRootPath);
        var registrySource = GetObject(payload, "productRegistry");
        var configuredProducts = GetArray(registrySource, "products");
        if (configuredProducts.Count == 0)
        {
            configuredProducts = GetArray(payload, "products");
        }

        var configuredById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var configured in configuredProducts)
        {
            var id = GetString(configured, "id", string.Empty);
            if (!string.IsNullOrWhiteSpace(id))
            {
                configuredById[id] = configured;
            }
        }

        var products = new List<TimelineProductRegistryProductResponse>();
        foreach (var productId in ProductIds)
        {
            var defaultProduct = defaults.Single(item => item.Id.Equals(productId, StringComparison.OrdinalIgnoreCase));
            var source = configuredById.TryGetValue(productId, out var configured)
                ? configured
                : null;
            products.Add(ConvertProductDefinition(productId, source, defaultProduct));
        }

        return new TimelineProductRegistryResponse { Products = products };
    }

    private List<TimelineProductRegistryProductResponse> NewProductRegistryDefaults(string dataRootPath)
    {
        var root = ConvertTimelineText(dataRootPath);
        if (string.IsNullOrEmpty(root))
        {
            root = ResolveTimelineDataRootPath("data");
        }

        var productsRoot = Path.Combine(root, "products");
        return
        [
            NewProductDefault("audio", "TimelineForAudio", Path.Combine(productsRoot, "TimelineForAudio"), "https://github.com/amano0406/TimelineForAudio"),
            NewProductDefault("windows-codex", "TimelineForWindowsCodex", Path.Combine(productsRoot, "TimelineForWindowsCodex"), "https://github.com/amano0406/TimelineForWindowsCodex"),
            NewProductDefault("chatgpt", "TimelineForChatGPT", Path.Combine(productsRoot, "TimelineForChatGPT"), "https://github.com/amano0406/TimelineForChatGPT"),
            NewProductDefault("image", "TimelineForImage", Path.Combine(productsRoot, "TimelineForImage"), "https://github.com/amano0406/TimelineForImage"),
            NewProductDefault("video", "TimelineForVideo", Path.Combine(productsRoot, "TimelineForVideo"), "https://github.com/amano0406/TimelineForVideo"),
            NewProductDefault("pc", "TimelineForPC", Path.Combine(productsRoot, "TimelineForPC"), "https://github.com/amano0406/TimelineForPC"),
        ];
    }

    private static TimelineProductRegistryProductResponse NewProductDefault(
        string id,
        string displayName,
        string path,
        string sourceUrl)
    {
        return new TimelineProductRegistryProductResponse
        {
            Id = id,
            DisplayName = displayName,
            Path = path,
            SourceType = "github-source-archive",
            SourceUrl = sourceUrl,
            Version = string.Empty,
            Enabled = true,
            Required = false,
        };
    }

    private static TimelineProductRegistryProductResponse ConvertProductDefinition(
        string productId,
        JsonObject? source,
        TimelineProductRegistryProductResponse defaultProduct)
    {
        var path = GetString(source, "path", string.Empty);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = GetString(source, "developmentPath", string.Empty);
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            path = GetString(source, "installPath", string.Empty);
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            path = defaultProduct.Path;
        }

        var sourceUrl = GetString(source, "sourceUrl", defaultProduct.SourceUrl);
        var sourceType = GetString(source, "sourceType", defaultProduct.SourceType);
        if (string.IsNullOrWhiteSpace(sourceUrl) && !string.IsNullOrWhiteSpace(defaultProduct.SourceUrl))
        {
            sourceUrl = defaultProduct.SourceUrl;
            sourceType = defaultProduct.SourceType;
        }

        if (string.IsNullOrWhiteSpace(sourceType))
        {
            sourceType = "github-source-archive";
        }

        return new TimelineProductRegistryProductResponse
        {
            Id = productId,
            DisplayName = GetString(source, "displayName", defaultProduct.DisplayName),
            Path = path,
            SourceType = sourceType,
            SourceUrl = sourceUrl,
            Version = GetString(source, "version", defaultProduct.Version),
            Enabled = GetBool(GetNode(source, "enabled"), defaultProduct.Enabled),
            Required = GetBool(GetNode(source, "required"), defaultProduct.Required),
        };
    }

    private static TimelineAudioVerbalizationSettingsResponse ResolveAudioVerbalizationSettings(
        string displayLanguageId,
        string ollamaBaseUrl)
    {
        var language = ConvertTimelineText(displayLanguageId);
        if (string.IsNullOrEmpty(language))
        {
            language = "ja-JP";
        }

        var baseUrl = ConvertTimelineText(ollamaBaseUrl);
        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = "http://127.0.0.1:11434";
        }

        return new TimelineAudioVerbalizationSettingsResponse
        {
            Enabled = true,
            Provider = "ollama",
            OllamaBaseUrl = baseUrl,
            Model = "qwen3.5:9b",
            FastModel = "qwen3.5:4b",
            Language = language,
            ChunkMinMinutes = 5,
            ChunkMaxMinutes = 10,
            ChunkMaxTurns = 12,
            NumPredict = 2048,
            NearbyContextMinutes = 1440,
            NearbyTimelineHintMaxEvents = 24,
            NearbyTimelineHintMaxChars = 500,
            MaxConcurrentJobs = 1,
            AutoRun = false,
            UsePreviousChunkSummary = true,
            UseUnconfirmedVerbalizationAsWeakHint = true,
        };
    }

    private static string GetRuntimeOllamaBaseUrl(TimelineRuntimeSettingsResponse runtime)
        => $"http://127.0.0.1:{runtime.OllamaPort}";

    private string ConvertTimelineWindowsPath(string path)
        => TimelinePathConverter.ConvertTimelineWindowsPath(path, _options);

    private static string ConvertTimelineText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }

    private static string GetTimelineTokenPreview(string token)
    {
        var value = ConvertTimelineText(token);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        const char bullet = '\u2022';
        if (value.Length <= 8)
        {
            return new string(bullet, value.Length);
        }

        return value[..4] + new string(bullet, Math.Max(4, value.Length - 8)) + value[^4..];
    }

    private static string NormalizeRuntimeNamePart(string value)
    {
        var text = ConvertTimelineText(value).ToLowerInvariant();
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return System.Text.RegularExpressions.Regex.Replace(text, "[^a-z0-9]+", "-").Trim('-');
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

    private static JsonObject? GetObject(JsonObject? source, string name)
        => GetNode(source, name) as JsonObject;

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
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

    private static string GetStringAny(JsonObject? source, string[] names, string fallback)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is not null)
            {
                return GetString(source, name, fallback);
            }
        }

        return fallback;
    }

    private static int GetPort(JsonNode? node, int fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        var text = ConvertNodeToString(node);
        return int.TryParse(text, out var port) && port is >= 1 and <= 65535 ? port : fallback;
    }

    private static bool GetBool(JsonNode? node, bool fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        if (node.GetValueKind() == JsonValueKind.True)
        {
            return true;
        }
        if (node.GetValueKind() == JsonValueKind.False)
        {
            return false;
        }

        var text = ConvertNodeToString(node).ToLowerInvariant();
        return text switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static string ConvertNodeToString(JsonNode node)
    {
        try
        {
            return ConvertTimelineText(node.GetValue<object>());
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static List<JsonObject> GetArray(JsonObject? source, string name)
    {
        var array = GetNode(source, name) as JsonArray;
        if (array is null)
        {
            return [];
        }

        return array.OfType<JsonObject>().ToList();
    }

    private static JsonObject ToJsonObject<T>(T value)
    {
        return JsonSerializer.SerializeToNode(value) as JsonObject ?? new JsonObject();
    }

    private static readonly List<TimelineDisplayLanguageOptionResponse> DisplayLanguageOptions =
    [
        new() { Id = "ja-JP", Label = "\u65e5\u672c\u8a9e" },
        new() { Id = "en-US", Label = "English" },
    ];

    private static readonly List<TimelineTimeZoneOptionResponse> TimeZoneOptions =
    [
        new() { Id = "Asia/Tokyo", Label = "Japan (Asia/Tokyo)" },
        new() { Id = "UTC", Label = "UTC" },
        new() { Id = "America/Los_Angeles", Label = "US Pacific (America/Los_Angeles)" },
        new() { Id = "America/New_York", Label = "US Eastern (America/New_York)" },
        new() { Id = "Europe/London", Label = "UK (Europe/London)" },
    ];
}

public sealed class TimelineSettingsResponse
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("dataRoot")]
    public string DataRoot { get; set; } = "";

    [JsonPropertyName("resolvedDataRoot")]
    public string ResolvedDataRoot { get; set; } = "";

    [JsonPropertyName("displayLanguageId")]
    public string DisplayLanguageId { get; set; } = "";

    [JsonPropertyName("displayLanguages")]
    public List<TimelineDisplayLanguageOptionResponse> DisplayLanguages { get; set; } = [];

    [JsonPropertyName("timeZoneId")]
    public string TimeZoneId { get; set; } = "";

    [JsonPropertyName("timeZones")]
    public List<TimelineTimeZoneOptionResponse> TimeZones { get; set; } = [];

    [JsonPropertyName("workDirectory")]
    public string WorkDirectory { get; set; } = "";

    [JsonPropertyName("storeDirectory")]
    public string StoreDirectory { get; set; } = "";

    [JsonPropertyName("runtime")]
    public TimelineRuntimeSettingsResponse Runtime { get; set; } = new();

    [JsonPropertyName("commonAi")]
    public TimelineCommonAiSettingsResponse CommonAi { get; set; } = new();

    [JsonPropertyName("productRegistry")]
    public TimelineProductRegistryResponse ProductRegistry { get; set; } = new();

    [JsonPropertyName("audioVerbalization")]
    public TimelineAudioVerbalizationSettingsResponse AudioVerbalization { get; set; } = new();
}

public sealed class TimelineDisplayLanguageOptionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

public sealed class TimelineTimeZoneOptionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

public sealed class TimelineRuntimeSettingsResponse
{
    [JsonPropertyName("instanceName")]
    public string InstanceName { get; set; } = "";

    [JsonPropertyName("imageTag")]
    public string ImageTag { get; set; } = "";

    [JsonPropertyName("webPort")]
    public int WebPort { get; set; }

    [JsonPropertyName("helperPortStart")]
    public int HelperPortStart { get; set; }

    [JsonPropertyName("helperPortEnd")]
    public int HelperPortEnd { get; set; }

    [JsonPropertyName("ollamaPort")]
    public int OllamaPort { get; set; }

    [JsonPropertyName("ollamaModel")]
    public string OllamaModel { get; set; } = "";

    [JsonPropertyName("shareOllamaVolume")]
    public bool ShareOllamaVolume { get; set; }

    [JsonPropertyName("ollamaVolumeName")]
    public string OllamaVolumeName { get; set; } = "";
}

public sealed class TimelineCommonAiSettingsResponse
{
    [JsonPropertyName("computeMode")]
    public string ComputeMode { get; set; } = "";

    [JsonPropertyName("hasHuggingFaceToken")]
    public bool HasHuggingFaceToken { get; set; }

    [JsonPropertyName("huggingFaceTokenPreview")]
    public string HuggingFaceTokenPreview { get; set; } = "";
}

public sealed class TimelineProductRegistryResponse
{
    [JsonPropertyName("products")]
    public List<TimelineProductRegistryProductResponse> Products { get; set; } = [];
}

public sealed class TimelineProductRegistryProductResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = "";

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

public sealed class TimelineAudioVerbalizationSettingsResponse
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("ollamaBaseUrl")]
    public string OllamaBaseUrl { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("fastModel")]
    public string FastModel { get; set; } = "";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "";

    [JsonPropertyName("chunkMinMinutes")]
    public int ChunkMinMinutes { get; set; }

    [JsonPropertyName("chunkMaxMinutes")]
    public int ChunkMaxMinutes { get; set; }

    [JsonPropertyName("chunkMaxTurns")]
    public int ChunkMaxTurns { get; set; }

    [JsonPropertyName("numPredict")]
    public int NumPredict { get; set; }

    [JsonPropertyName("nearbyContextMinutes")]
    public int NearbyContextMinutes { get; set; }

    [JsonPropertyName("nearbyTimelineHintMaxEvents")]
    public int NearbyTimelineHintMaxEvents { get; set; }

    [JsonPropertyName("nearbyTimelineHintMaxChars")]
    public int NearbyTimelineHintMaxChars { get; set; }

    [JsonPropertyName("maxConcurrentJobs")]
    public int MaxConcurrentJobs { get; set; }

    [JsonPropertyName("autoRun")]
    public bool AutoRun { get; set; }

    [JsonPropertyName("usePreviousChunkSummary")]
    public bool UsePreviousChunkSummary { get; set; }

    [JsonPropertyName("useUnconfirmedVerbalizationAsWeakHint")]
    public bool UseUnconfirmedVerbalizationAsWeakHint { get; set; }
}
