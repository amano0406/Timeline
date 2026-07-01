using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class TimelinePathConverter
{
    public static string ConvertTimelineWindowsPath(string path, TimelineLocalApiOptions options)
        => ConvertTimelineWindowsPath(path, options, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    public static string ConvertTimelineWindowsPath(string path, TimelineLocalApiOptions options, bool isWindows)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.StartsWith("/mnt/c/", StringComparison.OrdinalIgnoreCase))
        {
            return isWindows
                ? "C:\\" + text[7..].Replace("/", "\\")
                : text;
        }

        if (TryMapContainerPathToHostPath(text, isWindows, out var hostPath))
        {
            return hostPath;
        }

        var windowsCodexOutputs = string.IsNullOrWhiteSpace(options.WindowsCodexProductPath)
            ? string.Empty
            : Path.Combine(options.WindowsCodexProductPath, "outputs");

        var mountMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/shared/outputs"] = windowsCodexOutputs,
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            mountMap["/input/codex-home"] = @"C:\Users\amano\.codex";
            mountMap["/input/codex-backup"] = @"C:\Codex\archive\migration-backup-2026-03-27\codex-home";
            mountMap["/input/codex-root"] = @"C:\Codex";
        }

        foreach (var (key, value) in mountMap)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (string.Equals(text, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (text.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase))
            {
                return CombinePortablePath(value, text[(key.Length + 1)..]);
            }
        }

        return text;
    }

    public static string ConvertTimelineContainerPath(
        string path,
        TimelineLocalApiOptions options,
        string productPath)
        => ConvertTimelineContainerPath(path, options, productPath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    public static string ConvertTimelineContainerPath(
        string path,
        TimelineLocalApiOptions options,
        string productPath,
        bool isWindows)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Equals("/workspace", StringComparison.OrdinalIgnoreCase))
        {
            return productPath;
        }

        if (text.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
        {
            return CombinePortablePath(productPath, text["/workspace/".Length..]);
        }

        var converted = ConvertTimelineWindowsPath(text, options, isWindows);
        if (!string.IsNullOrEmpty(converted))
        {
            text = converted;
        }

        if (Path.IsPathRooted(text) || LooksLikeWindowsDrivePath(text))
        {
            return text;
        }

        return CombinePortablePath(productPath, text);
    }

    private static bool TryMapContainerPathToHostPath(string path, bool isWindows, out string hostPath)
    {
        hostPath = string.Empty;
        foreach (var mapping in GetPathMappings(isWindows).OrderByDescending(mapping => mapping.Container.Length))
        {
            if (TryMapPathPrefix(path, mapping.Container, mapping.Host, out hostPath))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<PathMapping> GetPathMappings(bool isWindows)
    {
        foreach (var mapping in ReadJsonPathMappings())
        {
            yield return mapping;
        }

        var hostRoot = ExpandEnvironmentPath(Environment.GetEnvironmentVariable("TIMELINE_HOST_ROOT") ?? string.Empty);
        var containerRoot = ConvertTimelineText(Environment.GetEnvironmentVariable("TIMELINE_CONTAINER_ROOT"));
        if (!string.IsNullOrEmpty(hostRoot) && !string.IsNullOrEmpty(containerRoot))
        {
            yield return new PathMapping(hostRoot, containerRoot);
        }

        if (!isWindows)
        {
            var home = ExpandEnvironmentPath(Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (!string.IsNullOrEmpty(home))
            {
                yield return new PathMapping(home, "/host");
            }
        }
    }

    private static IEnumerable<PathMapping> ReadJsonPathMappings()
    {
        var raw = ConvertTimelineText(Environment.GetEnvironmentVariable("TIMELINE_PATH_MAPPINGS"));
        if (string.IsNullOrEmpty(raw))
        {
            yield break;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(raw);
        }
        catch
        {
            yield break;
        }

        if (root is not JsonArray mappings)
        {
            yield break;
        }

        foreach (var node in mappings.OfType<JsonObject>())
        {
            var host = ExpandEnvironmentPath(GetJsonString(node, "host"));
            var container = GetJsonString(node, "container");
            if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(container))
            {
                yield return new PathMapping(host, container);
            }
        }
    }

    private static bool TryMapPathPrefix(string path, string sourceRoot, string targetRoot, out string mappedPath)
    {
        mappedPath = string.Empty;

        var normalizedPath = NormalizeContainerPath(path);
        var normalizedRoot = NormalizeContainerPath(sourceRoot);
        if (string.IsNullOrEmpty(normalizedPath) || string.IsNullOrEmpty(normalizedRoot))
        {
            return false;
        }

        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            mappedPath = targetRoot;
            return true;
        }

        var prefix = normalizedRoot.EndsWith("/", StringComparison.Ordinal) ? normalizedRoot : normalizedRoot + "/";
        if (!normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        mappedPath = CombineMappedPath(targetRoot, normalizedPath[prefix.Length..]);
        return true;
    }

    private static string NormalizeContainerPath(string path)
    {
        var text = ConvertTimelineText(path).Replace('\\', '/');
        while (text.Contains("//", StringComparison.Ordinal))
        {
            text = text.Replace("//", "/", StringComparison.Ordinal);
        }

        return text.Length > 1 ? text.TrimEnd('/') : text;
    }

    private static string CombineMappedPath(string rootPath, string relativePath)
    {
        var root = ConvertTimelineText(rootPath);
        var relative = ConvertTimelineText(relativePath);
        if (string.IsNullOrEmpty(root))
        {
            return relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }

        if (string.IsNullOrEmpty(relative))
        {
            return root;
        }

        var separator = LooksLikeWindowsDrivePath(root) || root.Contains('\\', StringComparison.Ordinal) ? "\\" : "/";
        var normalizedRoot = root.TrimEnd('/', '\\');
        if (LooksLikeWindowsDrivePath(root) && normalizedRoot.EndsWith(":", StringComparison.Ordinal))
        {
            normalizedRoot += separator;
        }

        var joined = string.Join(
            separator,
            relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalizedRoot.EndsWith(separator, StringComparison.Ordinal)
            ? normalizedRoot + joined
            : normalizedRoot + separator + joined;
    }

    private static string ExpandEnvironmentPath(string path)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(home))
        {
            if (text.Equals("$HOME", StringComparison.OrdinalIgnoreCase) || text.Equals("~", StringComparison.Ordinal))
            {
                return home;
            }

            if (text.StartsWith("$HOME/", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("$HOME\\", StringComparison.OrdinalIgnoreCase))
            {
                return CombineMappedPath(home, text[6..]);
            }

            if (text.StartsWith("~/", StringComparison.Ordinal)
                || text.StartsWith("~\\", StringComparison.Ordinal))
            {
                return CombineMappedPath(home, text[2..]);
            }
        }

        return Environment.ExpandEnvironmentVariables(text);
    }

    private static string GetJsonString(JsonObject obj, string name)
        => obj.TryGetPropertyValue(name, out var node) ? ConvertTimelineText(node?.GetValue<object>()) : string.Empty;

    public static string CombinePortablePath(string rootPath, string relativePath)
    {
        var root = ConvertTimelineText(rootPath);
        var relative = ConvertTimelineText(relativePath);
        if (string.IsNullOrEmpty(root))
        {
            return relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        }

        if (string.IsNullOrEmpty(relative))
        {
            return root;
        }

        var parts = relative
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? root : Path.Combine([root, .. parts]);
    }

    private static bool LooksLikeWindowsDrivePath(string path)
        => Regex.IsMatch(ConvertTimelineText(path), "^[A-Za-z]:[\\\\/]");

    private static string ConvertTimelineText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }

    private sealed record PathMapping(string Host, string Container);
}
