using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

public static class TimelinePathConverter
{
    public static string ConvertTimelineWindowsPath(string path, TimelineLocalApiOptions options)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.StartsWith("/mnt/c/", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "C:\\" + text[7..].Replace("/", "\\")
                : text;
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

        var converted = ConvertTimelineWindowsPath(text, options);
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
}
