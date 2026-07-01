using Timeline.Web.Components;
using Timeline.Web.Endpoints;
using Timeline.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFilter("System.Net.Http.HttpClient.TimelineLocalApiClient", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.TimelineLocalApiProxy", LogLevel.Warning);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

var localApiBaseUrl = builder.Configuration["Timeline:LocalApiBaseUrl"]
    ?? Environment.GetEnvironmentVariable("TIMELINE_LOCAL_API_BASE_URL")
    ?? "http://host.docker.internal:19001";
var webPort = int.TryParse(
    builder.Configuration["Timeline:WebPort"]
    ?? Environment.GetEnvironmentVariable("TIMELINE_WEB_PORT"),
    out var parsedWebPort)
    ? parsedWebPort
    : 19000;
var timelineProductPath =
    builder.Configuration["Timeline:ProductPath"]
    ?? Environment.GetEnvironmentVariable("TIMELINE_PRODUCT_PATH")
    ?? Directory.GetCurrentDirectory();
var windowsCodexProductPath =
    builder.Configuration["Timeline:WindowsCodexProductPath"]
    ?? Environment.GetEnvironmentVariable("TIMELINE_WINDOWS_CODEX_PRODUCT_PATH")
    ?? string.Empty;

builder.Services.AddHttpClient<TimelineLocalApiClient>(client =>
{
    client.BaseAddress = new Uri(localApiBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(15);
});
builder.Services.AddHttpClient("TimelineLocalApiProxy", client =>
{
    client.BaseAddress = new Uri(localApiBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(15);
});
builder.Services.AddSingleton(new TimelineLocalApiOptions(
    webPort,
    timelineProductPath,
    windowsCodexProductPath));
builder.Services.AddSingleton<TimelineSettingsService>();
builder.Services.AddSingleton<TimelineStoreService>();
builder.Services.AddSingleton<TimelineDashboardStatsService>();
builder.Services.AddSingleton<TimelineOperationLogService>();
builder.Services.AddSingleton<TimelineStoreExportService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.MapStaticAssets();
app.UseAntiforgery();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, product = "Timeline" }));
app.MapPost("/api/timeline/export/download", (TimelineStoreExportService exports) =>
{
    try
    {
        return Results.Json(exports.CreateDownload());
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { ok = false, message = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});
app.MapTimelineProxyEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
