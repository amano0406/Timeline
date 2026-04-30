using Timeline.Web.Components;
using Timeline.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient<TimelineHelperClient>(client =>
{
    var helperBaseUrl = builder.Configuration["Timeline:HelperBaseUrl"]
        ?? Environment.GetEnvironmentVariable("TIMELINE_HELPER_BASE_URL")
        ?? "http://host.docker.internal:19001";
    client.BaseAddress = new Uri(helperBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, product = "Timeline" }));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
