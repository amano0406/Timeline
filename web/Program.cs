using Timeline.Web.Components;
using Timeline.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

var helperBaseUrl = builder.Configuration["Timeline:HelperBaseUrl"]
    ?? Environment.GetEnvironmentVariable("TIMELINE_HELPER_BASE_URL")
    ?? "http://host.docker.internal:19001";

builder.Services.AddHttpClient<TimelineHelperClient>(client =>
{
    client.BaseAddress = new Uri(helperBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(15);
});
builder.Services.AddHttpClient("TimelineHelperProxy", client =>
{
    client.BaseAddress = new Uri(helperBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(15);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, product = "Timeline" }));
app.MapGet("/api/audio/source", async Task (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var sourceId = context.Request.Query["sourceId"].ToString();
    var relativePath = context.Request.Query["path"].ToString();
    if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(relativePath))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message = "sourceId and path are required." }, cancellationToken);
        return;
    }

    var helperUrl = "products/audio/files/source"
        + $"?sourceId={Uri.EscapeDataString(sourceId)}"
        + $"&path={Uri.EscapeDataString(relativePath)}";
    using var request = new HttpRequestMessage(HttpMethod.Get, helperUrl);
    if (context.Request.Headers.TryGetValue("Range", out var rangeHeader))
    {
        request.Headers.TryAddWithoutValidation("Range", rangeHeader.ToArray());
    }

    var client = httpClientFactory.CreateClient("TimelineHelperProxy");
    using var response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        context.Response.StatusCode = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        await context.Response.WriteAsJsonAsync(
            new { message = string.IsNullOrWhiteSpace(body) ? "Audio source was not found." : body },
            cancellationToken);
        return;
    }

    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
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

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    await stream.CopyToAsync(context.Response.Body, cancellationToken);
});
app.MapGet("/api/image/source", async Task (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var path = context.Request.Query["path"].ToString();
    if (string.IsNullOrWhiteSpace(path))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message = "path is required." }, cancellationToken);
        return;
    }

    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        $"products/image/files/source?path={Uri.EscapeDataString(path)}");
    if (context.Request.Headers.TryGetValue("Range", out var rangeHeader))
    {
        request.Headers.TryAddWithoutValidation("Range", rangeHeader.ToArray());
    }

    var client = httpClientFactory.CreateClient("TimelineHelperProxy");
    using var response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        context.Response.StatusCode = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        await context.Response.WriteAsJsonAsync(
            new { message = string.IsNullOrWhiteSpace(body) ? "Image source was not found." : body },
            cancellationToken);
        return;
    }

    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
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

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    await stream.CopyToAsync(context.Response.Body, cancellationToken);
});
app.MapGet("/api/video/source", async Task (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var path = context.Request.Query["path"].ToString();
    if (string.IsNullOrWhiteSpace(path))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message = "path is required." }, cancellationToken);
        return;
    }

    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        $"products/video/files/source?path={Uri.EscapeDataString(path)}");
    if (context.Request.Headers.TryGetValue("Range", out var rangeHeader))
    {
        request.Headers.TryAddWithoutValidation("Range", rangeHeader.ToArray());
    }

    var client = httpClientFactory.CreateClient("TimelineHelperProxy");
    using var response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        context.Response.StatusCode = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        await context.Response.WriteAsJsonAsync(
            new { message = string.IsNullOrWhiteSpace(body) ? "Video source was not found." : body },
            cancellationToken);
        return;
    }

    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
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

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    await stream.CopyToAsync(context.Response.Body, cancellationToken);
});
app.MapGet("/api/download/file", async Task (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var path = context.Request.Query["path"].ToString();
    if (string.IsNullOrWhiteSpace(path))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message = "path is required." }, cancellationToken);
        return;
    }

    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        $"downloads/file?path={Uri.EscapeDataString(path)}");
    if (context.Request.Headers.TryGetValue("Range", out var rangeHeader))
    {
        request.Headers.TryAddWithoutValidation("Range", rangeHeader.ToArray());
    }

    var client = httpClientFactory.CreateClient("TimelineHelperProxy");
    using var response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        context.Response.StatusCode = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        await context.Response.WriteAsJsonAsync(
            new { message = string.IsNullOrWhiteSpace(body) ? "Download file was not found." : body },
            cancellationToken);
        return;
    }

    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/zip";
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
    if (response.Content.Headers.TryGetValues("Content-Disposition", out var contentDisposition))
    {
        context.Response.Headers["Content-Disposition"] = string.Join(", ", contentDisposition);
    }

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    await stream.CopyToAsync(context.Response.Body, cancellationToken);
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
