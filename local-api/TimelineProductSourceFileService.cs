using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class TimelineProductSourceFileService
{
    private static readonly string[] DefaultAudioExtensions =
    [
        ".mp3",
        ".wav",
        ".m4a",
        ".aac",
        ".flac",
    ];

    private static readonly string[] DefaultImageExtensions =
    [
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif",
        ".bmp",
        ".tif",
        ".tiff",
        ".heic",
    ];

    private static readonly string[] DefaultVideoExtensions =
    [
        ".avi",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp4",
        ".webm",
        ".wmv",
    ];

    private static readonly string[] DefaultVideoArtifactExtensions =
    [
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".mp3",
        ".wav",
        ".m4a",
    ];

    private readonly TimelineLocalApiOptions _options;
    private readonly TimelineSettingsService _settings;

    public TimelineProductSourceFileService(
        TimelineLocalApiOptions options,
        TimelineSettingsService settings)
    {
        _options = options;
        _settings = settings;
    }

    public IResult GetAudioSourceFile(string? sourceId, string? relativePath)
    {
        var source = ResolveAudioSourceFile(sourceId, relativePath);
        if (string.IsNullOrEmpty(source))
        {
            return SourceNotFound("Audio source was not found.");
        }

        return Results.File(
            source,
            GetAudioMimeType(source),
            fileDownloadName: null,
            enableRangeProcessing: true);
    }

    public IResult GetImageSourceFile(string? sourcePath)
    {
        var source = ResolveDirectSourceFile(
            "image",
            sourcePath,
            ["inputRoots", "input_roots"],
            ["imageExtensions", "image_extensions"],
            DefaultImageExtensions);
        if (string.IsNullOrEmpty(source))
        {
            return SourceNotFound("Image source was not found.");
        }

        return Results.File(
            source,
            GetImageMimeType(source),
            fileDownloadName: null,
            enableRangeProcessing: true);
    }

    public IResult GetImageArtifactFile(string? artifactPath)
    {
        var productPath = GetProductPath("image");
        var settings = ReadProductSettings(productPath);
        var outputRoot = ConvertProductLocalPath(
            GetStringAny(settings, ["outputRoot", "output_root"], string.Empty),
            productPath);
        var artifact = ResolveGeneratedArtifactFile(artifactPath, productPath, outputRoot, DefaultImageExtensions);
        if (string.IsNullOrEmpty(artifact))
        {
            return SourceNotFound("Image artifact was not found.");
        }

        return Results.File(
            artifact,
            GetImageMimeType(artifact),
            fileDownloadName: null,
            enableRangeProcessing: true);
    }

    public IResult GetVideoSourceFile(string? sourcePath)
    {
        var source = ResolveDirectSourceFile(
            "video",
            sourcePath,
            ["inputRoots", "input_roots"],
            ["videoExtensions", "video_extensions"],
            DefaultVideoExtensions);
        if (string.IsNullOrEmpty(source))
        {
            return SourceNotFound("Video source was not found.");
        }

        return Results.File(
            source,
            GetVideoMimeType(source),
            fileDownloadName: null,
            enableRangeProcessing: true);
    }

    public IResult GetVideoArtifactFile(string? artifactPath)
    {
        var productPath = GetProductPath("video");
        var settings = ReadProductSettings(productPath);
        var outputRoot = ConvertProductLocalPath(
            GetStringAny(settings, ["outputRoot", "output_root"], string.Empty),
            productPath);
        var artifact = ResolveGeneratedArtifactFile(artifactPath, productPath, outputRoot, DefaultVideoArtifactExtensions);
        if (string.IsNullOrEmpty(artifact))
        {
            return SourceNotFound("Video artifact was not found.");
        }

        return Results.File(
            artifact,
            GetArtifactMimeType(artifact),
            fileDownloadName: null,
            enableRangeProcessing: true);
    }

    private string ResolveAudioSourceFile(string? sourceId, string? relativePath)
    {
        var sourceIdText = ConvertTimelineText(sourceId);
        var relativeText = ConvertTimelineText(relativePath)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(sourceIdText) || string.IsNullOrEmpty(relativeText))
        {
            return string.Empty;
        }

        var productPath = GetProductPath("audio");
        var settings = ReadProductSettings(productPath);
        var roots = GetAudioInputRoots(settings);
        var extensions = GetExtensionSet(settings, ["audioExtensions", "audio_extensions"], DefaultAudioExtensions);

        foreach (var root in roots)
        {
            if (!root.Enabled || string.IsNullOrEmpty(root.Path))
            {
                continue;
            }

            var rootPathText = root.Path;
            var rootMatches = root.Id.Equals(sourceIdText, StringComparison.Ordinal)
                || rootPathText.Equals(sourceIdText, StringComparison.Ordinal)
                || GetNormalizedPathKey(rootPathText).Equals(GetNormalizedPathKey(sourceIdText), StringComparison.OrdinalIgnoreCase);
            if (!rootMatches)
            {
                continue;
            }

            if (!Directory.Exists(rootPathText))
            {
                continue;
            }

            var rootPath = Path.GetFullPath(rootPathText);
            var candidate = Path.Combine(rootPath, relativeText);
            var resolved = ResolveAllowedFile(candidate, [rootPath], extensions);
            if (!string.IsNullOrEmpty(resolved))
            {
                return resolved;
            }
        }

        return string.Empty;
    }

    private string ResolveGeneratedArtifactFile(
        string? artifactPath,
        string productPath,
        string outputRoot,
        string[] defaultExtensions)
    {
        var candidatePath = ConvertProductLocalPath(artifactPath, productPath);
        if (string.IsNullOrEmpty(candidatePath) || !File.Exists(candidatePath))
        {
            return string.Empty;
        }

        var root = ConvertTimelineText(outputRoot);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return string.Empty;
        }

        return ResolveAllowedFile(candidatePath, [Path.GetFullPath(root)], GetExtensionSet(null, [], defaultExtensions));
    }

    private string ResolveDirectSourceFile(
        string productId,
        string? sourcePath,
        string[] inputRootNames,
        string[] extensionNames,
        string[] defaultExtensions)
    {
        var productPath = GetProductPath(productId);
        var settings = ReadProductSettings(productPath);
        var candidatePath = ConvertProductLocalPath(sourcePath, productPath);
        if (string.IsNullOrEmpty(candidatePath) || !File.Exists(candidatePath))
        {
            return string.Empty;
        }

        var roots = GetStringArrayAny(settings, inputRootNames)
            .Select(root => ConvertProductLocalPath(root, productPath))
            .Where(root => !string.IsNullOrEmpty(root) && Directory.Exists(root))
            .Select(Path.GetFullPath)
            .ToList();
        if (roots.Count == 0)
        {
            return string.Empty;
        }

        var extensions = GetExtensionSet(settings, extensionNames, defaultExtensions);
        return ResolveAllowedFile(candidatePath, roots, extensions);
    }

    private string ResolveAllowedFile(string candidatePath, IReadOnlyList<string> rootPaths, HashSet<string> extensions)
    {
        try
        {
            var resolvedCandidate = Path.GetFullPath(candidatePath);
            if (!File.Exists(resolvedCandidate))
            {
                return string.Empty;
            }

            var extension = Path.GetExtension(resolvedCandidate);
            if (!extensions.Contains(extension))
            {
                return string.Empty;
            }

            var candidateKey = GetNormalizedPathKey(resolvedCandidate);
            foreach (var rootPath in rootPaths)
            {
                var rootKey = GetNormalizedPathKey(rootPath);
                if (candidateKey.Equals(rootKey, StringComparison.OrdinalIgnoreCase)
                    || candidateKey.StartsWith(rootKey + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return resolvedCandidate;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        return string.Empty;
    }

    private string GetProductPath(string productId)
    {
        var product = _settings
            .ReadSettings()
            .ProductRegistry
            .Products
            .FirstOrDefault(item => item.Id.Equals(productId, StringComparison.OrdinalIgnoreCase));
        return ConvertTimelineText(product?.Path);
    }

    private JsonObject? ReadProductSettings(string productPath)
    {
        if (string.IsNullOrEmpty(productPath))
        {
            return null;
        }

        var settingsPath = Path.Combine(productPath, "settings.json");
        if (!File.Exists(settingsPath))
        {
            settingsPath = Path.Combine(productPath, "settings.example.json");
        }
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<AudioInputRoot> GetAudioInputRoots(JsonObject? settings)
    {
        var roots = new List<AudioInputRoot>();
        var index = 1;
        foreach (var node in GetArray(settings, "inputRoots"))
        {
            var fallbackId = $"audio-{index}";
            if (node is JsonObject obj)
            {
                var path = GetString(obj, "path", string.Empty);
                roots.Add(new AudioInputRoot(
                    GetString(obj, "id", fallbackId),
                    path,
                    GetBool(obj, "enabled", true)));
            }
            else
            {
                roots.Add(new AudioInputRoot(fallbackId, ConvertNodeToString(node), true));
            }

            index++;
        }

        return roots;
    }

    private string ConvertProductLocalPath(string? path, string productPath)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return TimelinePathConverter.ConvertTimelineContainerPath(text, _options, productPath);
    }

    private static HashSet<string> GetExtensionSet(
        JsonObject? settings,
        string[] names,
        string[] defaultExtensions)
    {
        var values = GetStringArrayAny(settings, names).ToList();
        if (values.Count == 0)
        {
            values.AddRange(defaultExtensions);
        }

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var text = ConvertTimelineText(value);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            extensions.Add(text.StartsWith('.') ? text : "." + text);
        }

        return extensions;
    }

    private static IEnumerable<string> GetStringArrayAny(JsonObject? source, string[] names)
    {
        foreach (var name in names)
        {
            var array = GetArray(source, name);
            if (array.Count > 0)
            {
                return array.Select(ConvertNodeToString).Where(value => !string.IsNullOrEmpty(value));
            }
        }

        return [];
    }

    private static List<JsonNode?> GetArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array ? array.ToList() : [];
    }

    private static JsonNode? GetNode(JsonObject? source, string name)
    {
        if (source is null)
        {
            return null;
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

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertNodeToString(node);
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

    private static bool GetBool(JsonObject? source, string name, bool fallback)
    {
        var node = GetNode(source, name);
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

        return ConvertNodeToString(node).ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
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
            return string.Empty;
        }
    }

    private static string GetNormalizedPathKey(string path)
        => ConvertTimelineText(path).TrimEnd('\\', '/').Replace('/', '\\').ToLowerInvariant();

    private static IResult SourceNotFound(string message)
    {
        return Results.Json(
            new { ok = false, message },
            statusCode: StatusCodes.Status404NotFound);
    }

    private static string GetAudioMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            _ => "application/octet-stream",
        };
    }

    private static string GetImageMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" => "image/tiff",
            ".tiff" => "image/tiff",
            ".heic" => "image/heic",
            _ => "application/octet-stream",
        };
    }

    private static string GetVideoMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".wmv" => "video/x-ms-wmv",
            _ => "application/octet-stream",
        };
    }

    private static string GetArtifactMimeType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".heic" => GetImageMimeType(path),
            ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" => GetAudioMimeType(path),
            ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv" or ".avi" or ".wmv" => GetVideoMimeType(path),
            _ => "application/octet-stream",
        };
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

    private sealed record AudioInputRoot(string Id, string Path, bool Enabled);
}
