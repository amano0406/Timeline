using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Timeline.Web.Endpoints;

internal static class TimelineProxyEndpoints
{
    private const string ProxyClientName = "TimelineHelperProxy";

    public static IEndpointRouteBuilder MapTimelineProxyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/audio/source", ProxyAudioSourceAsync);
        endpoints.MapGet("/api/image/source", ProxyImageSourceAsync);
        endpoints.MapGet("/api/image/artifact", ProxyImageArtifactAsync);
        endpoints.MapGet("/api/video/source", ProxyVideoSourceAsync);
        endpoints.MapGet("/api/video/artifact", ProxyVideoArtifactAsync);
        endpoints.MapGet("/api/download/file", ProxyDownloadFileAsync);

        return endpoints;
    }

    private static async Task ProxyAudioSourceAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var sourceId = context.Request.Query["sourceId"].ToString();
        var relativePath = context.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(relativePath))
        {
            await WriteBadRequestAsync(context, "sourceId and path are required.", cancellationToken);
            return;
        }

        var helperUrl = "products/audio/files/source"
            + $"?sourceId={Uri.EscapeDataString(sourceId)}"
            + $"&path={Uri.EscapeDataString(relativePath)}";
        await ProxyHelperFileAsync(
            context,
            httpClientFactory,
            helperUrl,
            "Audio source was not found.",
            "application/octet-stream",
            forwardContentDisposition: false,
            cancellationToken);
    }

    private static async Task ProxyImageSourceAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var path = context.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(path))
        {
            await WriteBadRequestAsync(context, "path is required.", cancellationToken);
            return;
        }

        await ProxyHelperFileAsync(
            context,
            httpClientFactory,
            $"products/image/files/source?path={Uri.EscapeDataString(path)}",
            "Image source was not found.",
            "application/octet-stream",
            forwardContentDisposition: false,
            cancellationToken);
    }

    private static async Task ProxyImageArtifactAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var path = context.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(path))
        {
            await WriteBadRequestAsync(context, "path is required.", cancellationToken);
            return;
        }

        await ProxyHelperFileAsync(
            context,
            httpClientFactory,
            $"products/image/files/artifact?path={Uri.EscapeDataString(path)}",
            "Image artifact was not found.",
            "application/octet-stream",
            forwardContentDisposition: false,
            cancellationToken);
    }

    private static async Task ProxyVideoSourceAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var path = context.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(path))
        {
            await WriteBadRequestAsync(context, "path is required.", cancellationToken);
            return;
        }

        await ProxyHelperFileAsync(
            context,
            httpClientFactory,
            $"products/video/files/source?path={Uri.EscapeDataString(path)}",
            "Video source was not found.",
            "application/octet-stream",
            forwardContentDisposition: false,
            cancellationToken);
    }

    private static async Task ProxyVideoArtifactAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var path = context.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(path))
        {
            await WriteBadRequestAsync(context, "path is required.", cancellationToken);
            return;
        }

        await ProxyHelperFileAsync(
            context,
            httpClientFactory,
            $"products/video/files/artifact?path={Uri.EscapeDataString(path)}",
            "Video artifact was not found.",
            "application/octet-stream",
            forwardContentDisposition: false,
            cancellationToken);
    }

    private static async Task ProxyDownloadFileAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var path = context.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(path))
        {
            await WriteBadRequestAsync(context, "path is required.", cancellationToken);
            return;
        }

        if (TryGetAllowedLocalFile(path, configuration, out var localPath))
        {
            await WriteLocalFileAsync(context, localPath, cancellationToken);
            return;
        }

        await ProxyHelperFileAsync(
            context,
            httpClientFactory,
            $"downloads/file?path={Uri.EscapeDataString(path)}",
            "Download file was not found.",
            "application/zip",
            forwardContentDisposition: true,
            cancellationToken);
    }

    private static bool TryGetAllowedLocalFile(
        string path,
        IConfiguration configuration,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var candidate = Path.GetFullPath(path);
        if (!File.Exists(candidate))
        {
            return false;
        }

        var allowedRoots = new[]
        {
            ConfiguredFullPath(configuration, "Timeline:WorkDirectory", "TIMELINE_WORK_DIRECTORY", "/data/work"),
            ConfiguredFullPath(configuration, "Timeline:StoreDirectory", "TIMELINE_STORE_DIRECTORY", "/data/store"),
        };

        foreach (var root in allowedRoots)
        {
            if (IsUnderRoot(candidate, root))
            {
                fullPath = candidate;
                return true;
            }
        }

        return false;
    }

    private static string ConfiguredFullPath(
        IConfiguration configuration,
        string configurationKey,
        string environmentVariable,
        string fallback)
    {
        return Path.GetFullPath(
            configuration[configurationKey]
            ?? Environment.GetEnvironmentVariable(environmentVariable)
            ?? fallback);
    }

    private static bool IsUnderRoot(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedCandidate.Equals(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteLocalFileAsync(
        HttpContext context,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(fullPath);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/zip";
        context.Response.ContentLength = fileInfo.Length;
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"{Uri.EscapeDataString(fileInfo.Name)}\"";

        await using var stream = File.OpenRead(fullPath);
        await stream.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private static async Task ProxyHelperFileAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        string helperUrl,
        string fallbackNotFoundMessage,
        string defaultContentType,
        bool forwardContentDisposition,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, helperUrl);
        if (context.Request.Headers.TryGetValue("Range", out var rangeHeader))
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader.ToArray());
        }

        var client = httpClientFactory.CreateClient(ProxyClientName);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            await context.Response.WriteAsJsonAsync(
                new { message = string.IsNullOrWhiteSpace(body) ? fallbackNotFoundMessage : body },
                cancellationToken);
            return;
        }

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? defaultContentType;
        if (response.Content.Headers.ContentLength is long contentLength)
        {
            context.Response.ContentLength = contentLength;
        }
        if (response.Content.Headers.ContentRange is { } contentRange)
        {
            context.Response.Headers["Content-Range"] = contentRange.ToString();
        }
        if (response.Headers.AcceptRanges.Count > 0)
        {
            context.Response.Headers["Accept-Ranges"] = string.Join(", ", response.Headers.AcceptRanges);
        }
        if (forwardContentDisposition
            && response.Content.Headers.TryGetValues("Content-Disposition", out var contentDisposition))
        {
            context.Response.Headers["Content-Disposition"] = string.Join(", ", contentDisposition);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private static async Task WriteBadRequestAsync(
        HttpContext context,
        string message,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message }, cancellationToken);
    }
}
