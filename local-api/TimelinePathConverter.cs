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
            return "C:\\" + text[7..].Replace("/", "\\");
        }

        var windowsCodexOutputs = string.IsNullOrWhiteSpace(options.WindowsCodexProductPath)
            ? string.Empty
            : Path.Combine(options.WindowsCodexProductPath, "outputs");

        var mountMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/input/codex-home"] = @"C:\Users\amano\.codex",
            ["/input/codex-backup"] = @"C:\Codex\archive\migration-backup-2026-03-27\codex-home",
            ["/input/codex-root"] = @"C:\Codex",
            ["/shared/outputs"] = windowsCodexOutputs,
        };

        foreach (var (key, value) in mountMap)
        {
            if (string.Equals(text, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (text.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase))
            {
                return value + "\\" + text[(key.Length + 1)..].Replace("/", "\\");
            }
        }

        return text;
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
