public sealed class TimelineDownloadService
{
    private readonly TimelineLocalApiOptions _options;
    private readonly TimelineSettingsService _settings;

    public TimelineDownloadService(
        TimelineLocalApiOptions options,
        TimelineSettingsService settings)
    {
        _options = options;
        _settings = settings;
    }

    public IResult GetDownloadFile(string? path)
    {
        var localPath = ResolveDownloadLocalPath(path);
        if (!IsDownloadFileAllowed(localPath))
        {
            return Results.Json(
                new { ok = false, message = "Download file was not found." },
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.File(
            localPath,
            "application/zip",
            Path.GetFileName(localPath),
            enableRangeProcessing: true);
    }

    private string ResolveDownloadLocalPath(string? path)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var localPath = TimelinePathConverter.ConvertTimelineWindowsPath(text, _options);
        return string.IsNullOrEmpty(localPath) ? text : localPath;
    }

    private bool IsDownloadFileAllowed(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)
                || new FileInfo(fullPath).Length <= 0
                || !Path.GetExtension(fullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return IsPathUnderRoot(fullPath, GetDownloadRoot());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private string GetDownloadRoot()
    {
        var root = Path.Combine(_settings.GetWorkDirectory(), "downloads");
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
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
