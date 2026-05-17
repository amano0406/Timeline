using Timeline.Web.Components;
using Timeline.Web.Endpoints;
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
app.MapStaticAssets();
app.UseAntiforgery();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, product = "Timeline" }));
app.MapTimelineProxyEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
