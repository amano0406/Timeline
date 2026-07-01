using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

var webPort = GetInt32(
    builder.Configuration["Timeline:WebPort"]
    ?? Environment.GetEnvironmentVariable("TIMELINE_WEB_PORT"),
    19000);

var windowsCodexProductPath =
    builder.Configuration["Timeline:WindowsCodexProductPath"]
    ?? Environment.GetEnvironmentVariable("TIMELINE_WINDOWS_CODEX_PRODUCT_PATH")
    ?? string.Empty;

var timelineProductPath =
    builder.Configuration["Timeline:ProductPath"]
    ?? Environment.GetEnvironmentVariable("TIMELINE_PRODUCT_PATH")
    ?? Directory.GetCurrentDirectory();

builder.Services.AddSingleton(new TimelineLocalApiOptions(
    webPort,
    timelineProductPath,
    windowsCodexProductPath));
builder.Services.AddSingleton<TimelineStartupRegistrationService>();
builder.Services.AddSingleton<TimelineSettingsService>();
builder.Services.AddSingleton<TimelineWorkerStatusService>();
builder.Services.AddTransient<TimelineStoreRebuildService>();
builder.Services.AddSingleton<TimelineStoreService>();
builder.Services.AddSingleton<TimelineDashboardStatsService>();
builder.Services.AddSingleton<TimelineAudioVerbalizationJobRegistry>();
builder.Services.AddSingleton<TimelineAudioVerbalizationPlanService>();
builder.Services.AddSingleton<TimelineAudioVerbalizationExecutionService>();
builder.Services.AddSingleton<TimelineAudioVerbalizationService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TimelineDownloadService>();
builder.Services.AddSingleton<TimelineOperationLogService>();
builder.Services.AddSingleton<TimelineLlmInputPreviewService>();
builder.Services.AddSingleton<TimelineItemSummaryService>();
builder.Services.AddSingleton<TimelineProductSourceFileService>();
builder.Services.AddSingleton<TimelineProductSettingsService>();
builder.Services.AddSingleton<TimelineStoreExportService>();
builder.Services.AddSingleton<TimelineModelInventoryService>();
builder.Services.AddSingleton<TimelineAudioFileService>();
builder.Services.AddSingleton<TimelineImageFileService>();
builder.Services.AddSingleton<TimelineVideoOverviewService>();
builder.Services.AddSingleton<TimelinePcSnapshotService>();
builder.Services.AddSingleton<TimelinePickerService>();
builder.Services.AddTransient<TimelineThreadProductOverviewService>();
builder.Services.AddTransient<TimelineProductActionService>();
builder.Services.AddHttpClient<TimelineProductApiClient>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Timeline-local-product-api");
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHttpClient<TimelineProductRuntimeService>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Timeline-local-product-manager");
});
builder.Services.AddHttpClient<TimelineOllamaStatusService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddTransient<TimelineRuntimeStatusService>();
builder.Services.AddTransient<TimelineRuntimeControlService>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var options = context.RequestServices.GetRequiredService<TimelineLocalApiOptions>();
    ApplyCorsHeaders(context, options);

    if (HttpMethods.IsOptions(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync("{}", context.RequestAborted);
        return;
    }

    await next();
});

app.MapGet("/health", () => TypedResults.Json(new HealthResponse(true)));

app.MapGet("/timeline/runtime/status", async (
    TimelineRuntimeStatusService runtime,
    CancellationToken cancellationToken) =>
{
    return TypedResults.Json(await runtime.GetStatusAsync(cancellationToken));
});

app.MapPost("/timeline/runtime/stop", (TimelineRuntimeControlService runtime) =>
{
    return TypedResults.Json(runtime.StopTimeline());
});

app.MapGet("/timeline/launcher-shortcut/status", (TimelineLocalApiOptions options) =>
{
    return TypedResults.Json(TimelineLauncherShortcutService.GetStatus(options.TimelineProductPath));
});

app.MapPost("/timeline/launcher-shortcut/install", (TimelineLocalApiOptions options) =>
{
    return TypedResults.Json(TimelineLauncherShortcutService.Install(options.TimelineProductPath));
});

app.MapPost("/timeline/launcher-shortcut/remove", (TimelineLocalApiOptions options) =>
{
    return TypedResults.Json(TimelineLauncherShortcutService.Remove(options.TimelineProductPath));
});

app.MapGet("/timeline/settings", (TimelineSettingsService settings) =>
{
    return TypedResults.Json(settings.ReadSettings());
});

app.MapPost("/timeline/settings", async (
    HttpContext context,
    TimelineSettingsService settings,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(settings.SaveSettings(request));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status400BadRequest);
    }
});

app.MapGet("/timeline/worker/status", (TimelineWorkerStatusService workerStatus) =>
{
    return TypedResults.Json(workerStatus.GetStatus());
});

app.MapPost("/timeline/worker/repair", async (
    TimelineWorkerStatusService workerStatus,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await workerStatus.RepairDockerWorkerAsync(cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/timeline/rebuild/status", (string? jobId, TimelineWorkerStatusService workerStatus) =>
{
    return Results.Json(workerStatus.GetRebuildStatus(jobId));
});

app.MapGet("/timeline/store/overview", (TimelineStoreService store) =>
{
    return TypedResults.Json(store.GetOverview());
});

app.MapGet("/timeline/dashboard/stats", (HttpContext context, TimelineDashboardStatsService stats) =>
{
    var days = GetQueryInt(context, "days", 30);
    var range = ConvertTimelineText(context.Request.Query["range"].ToString());
    var bucket = ConvertTimelineText(context.Request.Query["bucket"].ToString());
    return TypedResults.Json(stats.GetStats(range, bucket, days));
});

app.MapPost("/timeline/rebuild", async (
    HttpContext context,
    TimelineWorkerStatusService workerStatus,
    CancellationToken cancellationToken) =>
{
    try
    {
        JsonObject? request = null;
        if ((context.Request.ContentLength ?? 0) > 0)
        {
            request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        }

        return Results.Json(await workerStatus.StartRebuildAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/timeline/rebuild/cancel", async (
    HttpContext context,
    TimelineWorkerStatusService workerStatus,
    CancellationToken cancellationToken) =>
{
    try
    {
        JsonObject? request = null;
        if ((context.Request.ContentLength ?? 0) > 0)
        {
            request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        }

        var jobId = ConvertTimelineText(request?["jobId"]);
        return Results.Json(workerStatus.CancelRebuild(jobId));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/timeline/events", (HttpContext context, TimelineStoreService store) =>
{
    var page = GetQueryInt(context, "page", 1);
    var pageSize = GetQueryPageSize(context);
    page = Math.Max(1, page);
    return TypedResults.Json(store.GetEvents(page, pageSize));
});

app.MapGet("/timeline/console/logs", (HttpContext context, TimelineOperationLogService operations) =>
{
    _ = long.TryParse(context.Request.Query["afterId"].ToString(), out var afterId);
    var limit = GetQueryInt(context, "limit", 120);
    return Results.Json(operations.GetConsoleLogs(afterId, limit));
});

app.MapPost("/timeline/console/clear", (TimelineOperationLogService operations) =>
{
    var operationId = operations.NewOperationId("web");
    var startedAt = DateTimeOffset.Now;
    operations.WriteOperationEvent(
        operationId,
        "web",
        "Timeline",
        "console_clear",
        "started",
        "Web operation started.");

    try
    {
        var result = operations.ClearConsoleLogs();
        var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
        operations.WriteOperationEvent(
            operationId,
            "web",
            "Timeline",
            "console_clear",
            "completed",
            "Web operation completed.",
            durationMs: durationMs);
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
        operations.WriteOperationEvent(
            operationId,
            "web",
            "Timeline",
            "console_clear",
            "failed",
            ex.Message,
            durationMs: durationMs,
            stderr: ex.Message);
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/timeline/operations", (HttpContext context, TimelineOperationLogService operations) =>
{
    var limit = GetQueryInt(context, "limit", 100);
    return Results.Json(operations.GetOperations(limit));
});

app.MapGet("/timeline/operations/detail", (string? operationId, TimelineOperationLogService operations) =>
{
    return Results.Json(operations.GetOperationDetail(operationId));
});

app.MapGet("/timeline/llm-input/preview", (HttpContext context, TimelineLlmInputPreviewService preview) =>
{
    var maxChars = GetQueryInt(context, "maxChars", 4000);
    var scanLimit = GetQueryInt(context, "scanLimit", 5000);
    var countTotal = ConvertTimelineText(context.Request.Query["countTotal"].ToString())
        .Equals("true", StringComparison.Ordinal);
    return Results.Json(preview.GetPreview(
        context.Request.Query["purpose"].ToString(),
        context.Request.Query["product"].ToString(),
        context.Request.Query["from"].ToString(),
        context.Request.Query["to"].ToString(),
        GetQueryInt(context, "page", 1),
        GetQueryPageSize(context),
        maxChars,
        scanLimit,
        countTotal));
});

app.MapGet("/timeline/item-summaries/status", (string? jobId, TimelineItemSummaryService summaries) =>
{
    return Results.Json(summaries.GetStatus(jobId));
});

app.MapGet("/timeline/item-summaries/targets", (
    HttpContext context,
    TimelineItemSummaryService summaries) =>
{
    var request = new JsonObject();
    var product = ConvertTimelineText(context.Request.Query["product"].ToString());
    var products = ConvertTimelineText(context.Request.Query["products"].ToString());
    var itemId = ConvertTimelineText(context.Request.Query["itemId"].ToString());
    var maxItems = GetQueryInt(context, "maxItems", 0);
    var includeDiff = ConvertTimelineText(context.Request.Query["includeDiff"].ToString());
    var diff = ConvertTimelineText(context.Request.Query["diff"].ToString());
    var includeTargets = ConvertTimelineText(context.Request.Query["includeTargets"].ToString());
    var fastMode = ConvertTimelineText(context.Request.Query["fastMode"].ToString());
    var fast = ConvertTimelineText(context.Request.Query["fast"].ToString());

    if (!string.IsNullOrEmpty(product))
    {
        request["product"] = product;
    }

    if (!string.IsNullOrEmpty(products))
    {
        request["products"] = products;
    }

    if (!string.IsNullOrEmpty(itemId))
    {
        request["itemId"] = itemId;
    }

    if (maxItems > 0)
    {
        request["maxItems"] = maxItems;
    }

    if (!string.IsNullOrEmpty(includeDiff))
    {
        request["includeDiff"] = includeDiff;
    }

    if (!string.IsNullOrEmpty(diff))
    {
        request["diff"] = diff;
    }

    if (!string.IsNullOrEmpty(includeTargets))
    {
        request["includeTargets"] = includeTargets;
    }

    if (!string.IsNullOrEmpty(fastMode))
    {
        request["fastMode"] = fastMode;
    }

    if (!string.IsNullOrEmpty(fast))
    {
        request["fast"] = fast;
    }

    return Results.Json(summaries.GetTargets(request));
});

app.MapGet("/timeline/item-summaries/item", (string? product, string? itemId, TimelineItemSummaryService summaries) =>
{
    return Results.Json(summaries.GetSummary(product, itemId));
});

app.MapPost("/timeline/item-summaries/start", async (
    HttpContext context,
    TimelineItemSummaryService summaries,
    CancellationToken cancellationToken) =>
{
    try
    {
        JsonObject? request = null;
        if ((context.Request.ContentLength ?? 0) > 0)
        {
            request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        }

        return Results.Json(summaries.Start(request));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/timeline/item-summaries/cancel", async (
    HttpContext context,
    TimelineItemSummaryService summaries,
    CancellationToken cancellationToken) =>
{
    try
    {
        JsonObject? request = null;
        if ((context.Request.ContentLength ?? 0) > 0)
        {
            request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        }

        return Results.Json(summaries.Cancel(ConvertTimelineText(request?["jobId"])));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/timeline/audio-verbalization/ollama/status", async (
    string? baseUrl,
    string? model,
    TimelineOllamaStatusService ollama,
    CancellationToken cancellationToken) =>
{
    return TypedResults.Json(await ollama.GetStatusAsync(baseUrl, model, cancellationToken));
});

app.MapGet("/timeline/audio-verbalization/status", (
    HttpContext context,
    string? sourceId,
    string? path,
    TimelineAudioFileService audioFiles) =>
{
    try
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Json(audioFiles.GetAudioVerbalizationStatus(sourceId, path, baseUrl));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/timeline/audio-verbalization/result", (
    HttpContext context,
    string? sourceId,
    string? path,
    TimelineAudioFileService audioFiles) =>
{
    try
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Json(audioFiles.GetAudioVerbalizationResult(sourceId, path, baseUrl));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/timeline/audio-verbalization/start", async (
    HttpContext context,
    TimelineAudioVerbalizationService verbalization,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Json(await verbalization.StartSingleAsync(request, baseUrl, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/timeline/audio-verbalization/bulk/status", (string? jobId, TimelineAudioVerbalizationService verbalization) =>
{
    return Results.Json(verbalization.GetBulkStatus(jobId));
});

app.MapPost("/timeline/audio-verbalization/bulk/start", (
    HttpContext context,
    TimelineAudioVerbalizationService verbalization) =>
{
    try
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Json(verbalization.StartBulk(baseUrl));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/timeline/audio-verbalization/bulk/cancel", async (
    HttpContext context,
    TimelineAudioVerbalizationService verbalization,
    CancellationToken cancellationToken) =>
{
    try
    {
        JsonObject? request = null;
        if ((context.Request.ContentLength ?? 0) > 0)
        {
            request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        }

        var jobId = ConvertTimelineText(request?["jobId"]);
        return Results.Json(verbalization.CancelBulk(jobId));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/timeline/audio-verbalization/bulk/targets", (
    HttpContext context,
    TimelineAudioVerbalizationService verbalization) =>
{
    try
    {
        var forceRefresh = ConvertTimelineText(context.Request.Query["refresh"].ToString())
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Json(verbalization.GetBulkTargetSummary(forceRefresh, baseUrl));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/timeline/export/download", (TimelineStoreExportService exports) =>
{
    try
    {
        return Results.Json(exports.CreateDownload());
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { ok = false, message = ex.Message },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/runtime/status", async (
    TimelineProductRuntimeService runtime,
    CancellationToken cancellationToken) =>
{
    return Results.Json(await runtime.GetOverviewAsync(cancellationToken));
});

app.MapGet("/products/audio/models", (TimelineModelInventoryService models) =>
{
    try
    {
        return Results.Json(models.GetAudioModels());
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/audio/overview", (TimelineAudioFileService audioFiles) =>
{
    try
    {
        return Results.Json(audioFiles.GetOverview());
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/audio/settings", async (
    HttpContext context,
    TimelineProductSettingsService productSettings,
    TimelineAudioFileService audioFiles,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        productSettings.SaveAudioSettings(request);
        return Results.Json(audioFiles.GetOverview());
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/audio/files", (
    HttpContext context,
    TimelineAudioFileService audioFiles) =>
{
    try
    {
        return Results.Json(audioFiles.GetFiles(
            Math.Max(1, GetQueryInt(context, "page", 1)),
            GetQueryPageSize(context)));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/audio/files/detail", (
    HttpContext context,
    string? sourceId,
    string? path,
    TimelineAudioFileService audioFiles) =>
{
    try
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Json(audioFiles.GetFileDetail(sourceId, path, baseUrl));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/audio/files/delete-generated", async (
    HttpContext context,
    TimelineProductActionService productActions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await productActions.DeleteAudioGeneratedAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/audio/refresh", async (
    HttpContext context,
    TimelineProductActionService productActions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await productActions.RefreshAudioAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/audio/items/download", async (
    HttpContext context,
    TimelineProductActionService productActions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await productActions.DownloadAudioItemsAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/image/overview", (TimelineImageFileService imageFiles) =>
{
    try
    {
        return Results.Json(imageFiles.GetOverview());
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/image/settings", async (
    HttpContext context,
    TimelineProductSettingsService productSettings,
    TimelineImageFileService imageFiles,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        productSettings.SaveImageSettings(request);
        return Results.Json(imageFiles.GetOverview());
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/image/models", async (
    TimelineModelInventoryService models,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await models.GetImageModelsAsync(cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/image/items", (
    HttpContext context,
    TimelineImageFileService imageFiles) =>
{
    try
    {
        return Results.Json(imageFiles.GetItems(
            Math.Max(1, GetQueryInt(context, "page", 1)),
            GetQueryPageSize(context)));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/image/files", (
    HttpContext context,
    TimelineImageFileService imageFiles) =>
{
    try
    {
        return Results.Json(imageFiles.GetFiles(
            Math.Max(1, GetQueryInt(context, "page", 1)),
            GetQueryPageSize(context)));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/image/files/detail", (
    string? path,
    TimelineImageFileService imageFiles) =>
{
    try
    {
        return Results.Json(imageFiles.GetFileDetail(path));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/image/refresh", async (
    HttpContext context,
    TimelineProductActionService productActions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await productActions.RefreshImageAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/image/items/download", async (
    HttpContext context,
    TimelineProductActionService productActions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await productActions.DownloadImageItemsAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/image/items/delete-generated", async (
    HttpContext context,
    TimelineProductActionService productActions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await productActions.DeleteImageItemsAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/video/overview", (TimelineVideoOverviewService videoOverview) =>
{
    try
    {
        return Results.Json(videoOverview.GetOverview());
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/video/settings", async (
    HttpContext context,
    TimelineProductSettingsService productSettings,
    TimelineVideoOverviewService videoOverview,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        productSettings.SaveVideoSettings(request);
        return Results.Json(videoOverview.GetOverview());
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/video/files", (
    HttpContext context,
    TimelineVideoOverviewService videoOverview) =>
{
    try
    {
        return Results.Json(videoOverview.GetFiles(
            Math.Max(1, GetQueryInt(context, "page", 1)),
            GetQueryPageSize(context)));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/video/files/detail", (
    string? path,
    TimelineVideoOverviewService videoOverview) =>
{
    try
    {
        return Results.Json(videoOverview.GetFileDetail(path));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/video/refresh", async (
    HttpContext context,
    TimelineProductActionService productActions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await productActions.RefreshVideoAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/pc/overview", async (
    TimelinePcSnapshotService pcSnapshots,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await pcSnapshots.GetOverviewAsync(cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/pc/settings", async (
    HttpContext context,
    TimelineProductSettingsService productSettings,
    TimelinePcSnapshotService pcSnapshots,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        await productSettings.SavePcSettingsAsync(request, cancellationToken);
        return Results.Json(await pcSnapshots.GetOverviewAsync(cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/pc/items", async (
    HttpContext context,
    TimelinePcSnapshotService pcSnapshots,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await pcSnapshots.GetItemsAsync(
            Math.Max(1, GetQueryInt(context, "page", 1)),
            GetQueryPageSize(context),
            cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/pc/refresh", async (
    TimelineProductActionService productActions,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await productActions.RefreshPcAsync(cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/pc/items/download", async (
    HttpContext context,
    TimelineProductActionService productActions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await productActions.DownloadPcItemsAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/windows-codex/overview", (TimelineThreadProductOverviewService threadProducts) =>
{
    try
    {
        return Results.Json(threadProducts.GetWindowsCodexOverview());
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/windows-codex/items", async (
    HttpContext context,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        var page = GetQueryInt(context, "page", 1);
        var pageSize = GetQueryPageSize(context);
        return Results.Json(await threadProducts.GetWindowsCodexThreadsAsync(page, pageSize, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/windows-codex/threads/{itemId}", async (
    string itemId,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await threadProducts.GetWindowsCodexThreadDetailAsync(itemId, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/windows-codex/refresh", async (
    JsonObject? request,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await threadProducts.RefreshWindowsCodexAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/windows-codex/items/download", async (
    HttpContext context,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await threadProducts.DownloadWindowsCodexItemsAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/windows-codex/items/delete-generated", async (
    HttpContext context,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await threadProducts.DeleteWindowsCodexItemsAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/windows-codex/settings", async (
    HttpContext context,
    TimelineProductSettingsService productSettings,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        productSettings.SaveWindowsCodexSettings(request);
        return Results.Json(threadProducts.GetWindowsCodexOverview());
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/chatgpt/overview", async (
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await threadProducts.GetChatGptOverviewAsync(cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/chatgpt/items", async (
    HttpContext context,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        var page = GetQueryInt(context, "page", 1);
        var pageSize = GetQueryPageSize(context);
        return Results.Json(await threadProducts.GetChatGptThreadsAsync(page, pageSize, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/chatgpt/threads/{itemId}", async (
    string itemId,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await threadProducts.GetChatGptThreadDetailAsync(itemId, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/chatgpt/refresh", async (
    HttpContext context,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await threadProducts.RefreshChatGptAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/chatgpt/items/download", async (
    HttpContext context,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(await threadProducts.DownloadChatGptItemsAsync(request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/chatgpt/items/delete-generated", async (
    HttpContext context,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return Results.Json(threadProducts.DeleteChatGptItems(request));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/chatgpt/settings", async (
    HttpContext context,
    TimelineProductSettingsService productSettings,
    TimelineThreadProductOverviewService threadProducts,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken);
        productSettings.SaveChatGptSettings(request);
        return Results.Json(await threadProducts.GetChatGptOverviewAsync(cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/runtime/{productId}/start", async (
    string productId,
    TimelineProductRuntimeService runtime,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await runtime.StartProductAsync(productId, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/runtime/{productId}/stop", async (
    string productId,
    TimelineProductRuntimeService runtime,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await runtime.StopProductAsync(productId, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/runtime/{productId}/restart", async (
    string productId,
    TimelineProductRuntimeService runtime,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await runtime.RestartProductAsync(productId, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/runtime/{productId}/install", async (
    HttpContext context,
    string productId,
    TimelineProductRuntimeService runtime,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await ReadOptionalJsonObjectAsync(context.Request, cancellationToken);
        return Results.Json(await runtime.InstallProductAsync(productId, request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/runtime/{productId}/update", async (
    string productId,
    TimelineProductRuntimeService runtime,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await runtime.UpdateProductAsync(productId, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/runtime/{productId}/uninstall-plan", async (
    HttpContext context,
    string productId,
    TimelineProductRuntimeService runtime,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await ReadOptionalJsonObjectAsync(context.Request, cancellationToken);
        return Results.Json(runtime.GetProductUninstallPlan(productId, request));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/products/runtime/{productId}/uninstall", async (
    HttpContext context,
    string productId,
    TimelineProductRuntimeService runtime,
    CancellationToken cancellationToken) =>
{
    try
    {
        var request = await ReadOptionalJsonObjectAsync(context.Request, cancellationToken);
        return Results.Json(await runtime.UninstallProductAsync(productId, request, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/products/audio/files/source", (
    string? sourceId,
    string? path,
    TimelineProductSourceFileService sourceFiles) =>
{
    return sourceFiles.GetAudioSourceFile(sourceId, path);
});

app.MapGet("/products/image/files/source", (
    string? path,
    TimelineProductSourceFileService sourceFiles) =>
{
    return sourceFiles.GetImageSourceFile(path);
});

app.MapGet("/products/image/files/artifact", (
    string? path,
    TimelineProductSourceFileService sourceFiles) =>
{
    return sourceFiles.GetImageArtifactFile(path);
});

app.MapGet("/products/video/files/source", (
    string? path,
    TimelineProductSourceFileService sourceFiles) =>
{
    return sourceFiles.GetVideoSourceFile(path);
});

app.MapGet("/products/video/files/artifact", (
    string? path,
    TimelineProductSourceFileService sourceFiles) =>
{
    return sourceFiles.GetVideoArtifactFile(path);
});

app.MapGet("/downloads/file", (string? path, TimelineDownloadService downloads) =>
{
    return downloads.GetDownloadFile(path);
});

app.MapGet("/path-status", (
    string? path,
    string? kind,
    TimelineLocalApiOptions options) =>
{
    var pathText = ConvertTimelineText(path);
    var kindText = ConvertTimelineText(kind).ToLowerInvariant();
    if (string.IsNullOrEmpty(kindText))
    {
        kindText = "directory";
    }

    var localPath = ConvertTimelineWindowsPath(pathText, options);
    if (string.IsNullOrEmpty(localPath))
    {
        localPath = pathText;
    }

    var exists = false;
    var isDirectory = false;
    var isFile = false;
    var readable = false;
    var message = string.Empty;

    if (string.IsNullOrEmpty(localPath))
    {
        message = "Path is empty.";
    }
    else
    {
        try
        {
            isDirectory = Directory.Exists(localPath);
            isFile = File.Exists(localPath);
            exists = isDirectory || isFile;
            readable = exists;

            if (!exists)
            {
                message = "Path was not found.";
            }
            else if (kindText == "file" && !isFile)
            {
                message = "Path is not a file.";
            }
            else if (kindText == "directory" && !isDirectory)
            {
                message = "Path is not a directory.";
            }
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }
    }

    var matchesKind = kindText switch
    {
        "file" => isFile,
        "any" => exists,
        _ => isDirectory,
    };

    return TypedResults.Json(new PathStatusResponse(
        exists && matchesKind,
        pathText,
        localPath,
        kindText,
        exists,
        isDirectory,
        isFile,
        readable,
        matchesKind,
        message));
});

app.MapGet("/pick-directory", async (
    string? title,
    string? initialPath,
    TimelinePickerService picker,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await picker.PickDirectoryAsync(title, initialPath, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/pick-file", async (
    string? title,
    string? initialPath,
    string? filter,
    TimelinePickerService picker,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await picker.PickFileAsync(title, initialPath, filter, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { message = ex.Message, ok = false },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Map("/{**path}", () => Results.Json(
    new { ok = false, message = "Endpoint was not found." },
    statusCode: StatusCodes.Status404NotFound));

app.Run();

static void ApplyCorsHeaders(HttpContext context, TimelineLocalApiOptions options)
{
    context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Range";

    var origin = context.Request.Headers.Origin.ToString();
    if (string.Equals(origin, $"http://127.0.0.1:{options.WebPort}", StringComparison.OrdinalIgnoreCase)
        || string.Equals(origin, $"http://localhost:{options.WebPort}", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers["Vary"] = "Origin";
    }
    else if (string.IsNullOrEmpty(origin))
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
    }
}

static string ConvertTimelineText(object? value)
{
    return value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        _ => value.ToString()?.Trim() ?? string.Empty,
    };
}

static string ConvertTimelineWindowsPath(string path, TimelineLocalApiOptions options)
{
    return TimelinePathConverter.ConvertTimelineWindowsPath(path, options);
}

static int GetInt32(string? value, int fallback)
{
    return int.TryParse(value, out var parsed) ? parsed : fallback;
}

static int GetQueryInt(HttpContext context, string name, int fallback)
{
    var value = context.Request.Query[name].ToString();
    return int.TryParse(value, out var parsed) ? parsed : fallback;
}

static int GetQueryPageSize(HttpContext context)
{
    var value = context.Request.Query["pageSize"].ToString();
    if (!int.TryParse(value, out var pageSize))
    {
        value = context.Request.Query["page-size"].ToString();
        if (!int.TryParse(value, out pageSize))
        {
            return 100;
        }
    }

    if (pageSize < 1)
    {
        return 100;
    }

    return Math.Min(pageSize, 500);
}

static async Task<JsonObject> ReadOptionalJsonObjectAsync(
    HttpRequest request,
    CancellationToken cancellationToken)
{
    if (request.ContentLength == 0)
    {
        return new JsonObject();
    }

    return await request.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? new JsonObject();
}

public sealed record TimelineLocalApiOptions(
    int WebPort,
    string TimelineProductPath,
    string WindowsCodexProductPath);

public sealed record HealthResponse(
    [property: JsonPropertyName("ok")] bool Ok);

public sealed record PathStatusResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("localPath")] string LocalPath,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("isDirectory")] bool IsDirectory,
    [property: JsonPropertyName("isFile")] bool IsFile,
    [property: JsonPropertyName("readable")] bool Readable,
    [property: JsonPropertyName("matchesKind")] bool MatchesKind,
    [property: JsonPropertyName("message")] string Message);
