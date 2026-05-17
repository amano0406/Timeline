using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class TimelineStoreRebuildService
{
    private static readonly TimelineStoreProduct[] Products =
    [
        new("audio", "TimelineForAudio"),
        new("windows-codex", "TimelineForWindowsCodex"),
        new("chatgpt", "TimelineForChatGPT"),
        new("image", "TimelineForImage"),
        new("video", "TimelineForVideo"),
        new("pc", "TimelineForPC"),
    ];

    private readonly TimelineSettingsService _settings;
    private readonly TimelineWorkerStatusService _workerStatus;
    private readonly TimelineProductActionService _productActions;
    private readonly TimelineThreadProductOverviewService _threadProducts;
    private readonly TimelineImageFileService _imageFiles;
    private readonly TimelineVideoOverviewService _videoOverview;
    private readonly TimelinePcSnapshotService _pcSnapshots;

    public TimelineStoreRebuildService(
        TimelineSettingsService settings,
        TimelineWorkerStatusService workerStatus,
        TimelineProductActionService productActions,
        TimelineThreadProductOverviewService threadProducts,
        TimelineImageFileService imageFiles,
        TimelineVideoOverviewService videoOverview,
        TimelinePcSnapshotService pcSnapshots)
    {
        _settings = settings;
        _workerStatus = workerStatus;
        _productActions = productActions;
        _threadProducts = threadProducts;
        _imageFiles = imageFiles;
        _videoOverview = videoOverview;
        _pcSnapshots = pcSnapshots;
    }

    public async Task RunRebuildJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now.ToString("o");
        var lastStage = "collecting";
        var lastMessage = "Collecting product data through product APIs one product at a time.";
        void WriteProgress(string stage, string message)
        {
            lastStage = stage;
            lastMessage = message;
            WriteJobStatus(jobId, "running", stage, message, startedAt);
        }

        try
        {
            WriteJobStatus(
                jobId,
                "running",
                lastStage,
                lastMessage,
                startedAt);

            var result = await RebuildStoreAsync(
                jobId,
                startedAt,
                WriteProgress,
                cancellationToken);

            WriteJobStatus(
                jobId,
                "completed",
                "completed",
                "Timeline store rebuild completed.",
                startedAt,
                result: result,
                itemCount: GetInt(result, "itemCount", 0),
                eventCount: GetInt(result, "eventCount", 0));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            WriteJobStatus(
                jobId,
                "failed",
                string.IsNullOrWhiteSpace(lastStage) ? "failed" : lastStage,
                string.IsNullOrWhiteSpace(lastMessage)
                    ? "Timeline store rebuild failed."
                    : "Timeline store rebuild failed while: " + lastMessage,
                startedAt,
                error: ex.Message);
        }
    }

    private async Task<JsonObject> RebuildStoreAsync(
        string jobId,
        string startedAt,
        Action<string, string> progress,
        CancellationToken cancellationToken)
    {
        progress("preparing", "Preparing timeline store workspace.");

        var storeRoot = _settings.GetStoreDirectory();
        var rebuildsRoot = Path.Combine(storeRoot, "rebuilds");
        Directory.CreateDirectory(rebuildsRoot);

        var rebuildId = "rebuild-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var stagingRoot = Path.Combine(_settings.GetWorkDirectory(), "timeline-store-staging", rebuildId);
        var packageRoot = Path.Combine(stagingRoot, "package");
        var rebuildRoot = Path.Combine(rebuildsRoot, rebuildId);
        if (Directory.Exists(rebuildRoot))
        {
            Directory.Delete(rebuildRoot, recursive: true);
        }
        Directory.CreateDirectory(Path.Combine(packageRoot, "timeline"));

        var itemsPath = Path.Combine(packageRoot, "timeline", "items.jsonl");
        var eventsPath = Path.Combine(packageRoot, "timeline", "events.jsonl");
        var refreshResults = new JsonArray();
        var productResults = new JsonArray();

        try
        {
            await using (var itemsWriter = new StreamWriter(itemsPath, append: false, new UTF8Encoding(false)))
            await using (var eventsWriter = new StreamWriter(eventsPath, append: false, new UTF8Encoding(false)))
            {
                foreach (var product in Products)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    progress("refreshing", "Refreshing " + product.DisplayName + " data through its API.");
                    refreshResults.Add(await RefreshProductForScanAsync(product, cancellationToken));

                    progress("downloading", "Downloading " + product.DisplayName + " data through its API.");
                    var download = await DownloadProductForExportAsync(product, cancellationToken);

                    progress("importing", "Importing " + product.DisplayName + " data into the Timeline store.");
                    productResults.Add(AddProductArchive(
                        product,
                        download.ArchivePath,
                        packageRoot,
                        itemsWriter,
                        eventsWriter,
                        progress));
                }
            }
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }

        var itemCount = 0;
        var eventCount = 0;
        foreach (var node in productResults)
        {
            if (node is JsonObject result)
            {
                itemCount += GetInt(result, "itemCount", 0);
                eventCount += GetInt(result, "eventCount", 0);
            }
        }

        if (itemCount <= 0)
        {
            TryDeleteDirectory(stagingRoot);
            throw new InvalidOperationException("No Timeline items were found. Check each product list first.");
        }

        progress("sorting", "Sorting timeline events.");
        SortEventsFile(eventsPath);

        progress("publishing", "Publishing rebuilt timeline store.");
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactType"] = "timeline_store",
            ["createdAt"] = DateTimeOffset.Now.ToString("o"),
            ["rebuildId"] = rebuildId,
            ["packagePath"] = rebuildRoot,
            ["itemCount"] = itemCount,
            ["eventCount"] = eventCount,
            ["refreshes"] = CloneArray(refreshResults),
            ["products"] = CloneArray(productResults),
            ["files"] = new JsonObject
            {
                ["items"] = "items.jsonl",
                ["events"] = "events.jsonl",
                ["packageItems"] = "timeline/items.jsonl",
                ["packageEvents"] = "timeline/events.jsonl",
            },
        };
        WriteJsonFile(Path.Combine(packageRoot, "manifest.json"), manifest);
        File.WriteAllLines(
            Path.Combine(packageRoot, "README.md"),
            [
                "# Timeline Store",
                "",
                "This directory is the current Timeline store package.",
                "",
                "- timeline/items.jsonl: one row per managed item.",
                "- timeline/events.jsonl: one row per timeline event, sorted for Timeline browsing.",
                "- products/: product download contents expanded for inspection.",
                "- source-downloads/: raw product API download ZIPs.",
            ],
            new UTF8Encoding(false));

        try
        {
            Directory.Move(packageRoot, rebuildRoot);
            File.Copy(Path.Combine(rebuildRoot, "manifest.json"), GetManifestPath(), overwrite: true);
            File.Copy(Path.Combine(rebuildRoot, "timeline", "items.jsonl"), GetItemsPath(), overwrite: true);
            File.Copy(Path.Combine(rebuildRoot, "timeline", "events.jsonl"), GetEventsPath(), overwrite: true);
        }
        catch
        {
            TryDeleteDirectory(rebuildRoot);
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }

        return new JsonObject
        {
            ["rebuildId"] = rebuildId,
            ["storeDirectory"] = storeRoot,
            ["packagePath"] = rebuildRoot,
            ["manifestPath"] = GetManifestPath(),
            ["itemsPath"] = GetItemsPath(),
            ["eventsPath"] = GetEventsPath(),
            ["itemCount"] = itemCount,
            ["eventCount"] = eventCount,
            ["products"] = CloneArray(productResults),
        };
    }

    private async Task<JsonObject> RefreshProductForScanAsync(
        TimelineStoreProduct product,
        CancellationToken cancellationToken)
    {
        if (product.ProductId.Equals("audio", StringComparison.OrdinalIgnoreCase))
        {
            return NewRefreshResult(
                product,
                refreshed: true,
                skipped: false,
                reason: string.Empty,
                await _productActions.RefreshAudioAsync(new JsonObject { ["queueOnly"] = false }, cancellationToken));
        }
        if (product.ProductId.Equals("windows-codex", StringComparison.OrdinalIgnoreCase))
        {
            return NewRefreshResult(
                product,
                refreshed: true,
                skipped: false,
                reason: string.Empty,
                await _threadProducts.RefreshWindowsCodexAsync(cancellationToken));
        }
        if (product.ProductId.Equals("chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            return NewRefreshResult(
                product,
                refreshed: false,
                skipped: true,
                reason: "ChatGPT refresh requires a user-selected export ZIP.",
                result: null);
        }
        if (product.ProductId.Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            return NewRefreshResult(
                product,
                refreshed: true,
                skipped: false,
                reason: string.Empty,
                await _productActions.RefreshImageAsync(new JsonObject(), cancellationToken));
        }
        if (product.ProductId.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            return NewRefreshResult(
                product,
                refreshed: true,
                skipped: false,
                reason: string.Empty,
                await _productActions.RefreshVideoAsync(new JsonObject(), cancellationToken));
        }
        if (product.ProductId.Equals("pc", StringComparison.OrdinalIgnoreCase))
        {
            return NewRefreshResult(
                product,
                refreshed: true,
                skipped: false,
                reason: string.Empty,
                await _productActions.RefreshPcAsync(cancellationToken));
        }

        throw new InvalidOperationException("Unsupported product: " + product.ProductId);
    }

    private async Task<ProductDownloadResult> DownloadProductForExportAsync(
        TimelineStoreProduct product,
        CancellationToken cancellationToken)
    {
        if (product.ProductId.Equals("audio", StringComparison.OrdinalIgnoreCase))
        {
            return NewDownloadResult(product, await _productActions.DownloadAudioItemsAsync(new JsonObject { ["all"] = true }, cancellationToken));
        }
        if (product.ProductId.Equals("windows-codex", StringComparison.OrdinalIgnoreCase))
        {
            return NewDownloadResult(product, await _threadProducts.DownloadWindowsCodexItemsAsync(new JsonObject(), cancellationToken));
        }
        if (product.ProductId.Equals("chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            return NewDownloadResult(product, await _threadProducts.DownloadChatGptItemsAsync(new JsonObject(), cancellationToken));
        }
        if (product.ProductId.Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            if (GetInt(_imageFiles.GetOverview(), "itemCount", 0) <= 0)
            {
                return new ProductDownloadResult(product.ProductId, product.DisplayName, string.Empty);
            }

            return NewDownloadResult(product, await _productActions.DownloadImageItemsAsync(new JsonObject(), cancellationToken));
        }
        if (product.ProductId.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            if (GetInt(_videoOverview.GetOverview(), "itemCount", 0) <= 0)
            {
                return new ProductDownloadResult(product.ProductId, product.DisplayName, string.Empty);
            }

            return NewDownloadResult(product, await _productActions.DownloadVideoItemsAsync(new JsonObject(), cancellationToken));
        }
        if (product.ProductId.Equals("pc", StringComparison.OrdinalIgnoreCase))
        {
            if (GetInt(await _pcSnapshots.GetOverviewAsync(cancellationToken), "itemCount", 0) <= 0)
            {
                return new ProductDownloadResult(product.ProductId, product.DisplayName, string.Empty);
            }

            return NewDownloadResult(product, await _productActions.DownloadPcItemsAsync(new JsonObject(), cancellationToken));
        }

        throw new InvalidOperationException("Unsupported product: " + product.ProductId);
    }

    private JsonObject AddProductArchive(
        TimelineStoreProduct product,
        string archivePath,
        string packageRoot,
        StreamWriter itemsWriter,
        StreamWriter eventsWriter,
        Action<string, string> progress)
    {
        var result = new JsonObject
        {
            ["productId"] = product.ProductId,
            ["displayName"] = product.DisplayName,
            ["archivePath"] = archivePath,
            ["included"] = false,
            ["itemCount"] = 0,
            ["eventCount"] = 0,
            ["message"] = string.Empty,
        };
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
        {
            result["message"] = "Product download ZIP was not found.";
            return result;
        }

        var safeProductId = GetSafeSegment(product.ProductId);
        var sourceDownloadsRoot = Path.Combine(packageRoot, "source-downloads");
        Directory.CreateDirectory(sourceDownloadsRoot);
        File.Copy(archivePath, Path.Combine(sourceDownloadsRoot, safeProductId + ".zip"), overwrite: true);

        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            CopyZipEntryToFile(entry, packageRoot, "products/" + safeProductId);
            var entryName = entry.FullName.Replace('\\', '/');
            var match = Regex.Match(entryName, "^items/([^/]+)/timeline\\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                continue;
            }

            var itemId = Uri.UnescapeDataString(match.Groups[1].Value);
            var timeline = ReadZipEntryJson(entry);
            if (timeline is null)
            {
                continue;
            }

            var rawTimelinePath = "products/" + safeProductId + "/" + entryName;
            var rawConvertInfoPath = "products/" + safeProductId + "/items/" + itemId + "/convert_info.json";
            var eventCount = product.ProductId.ToLowerInvariant() switch
            {
                "audio" => WriteAudioTimeline(timeline, product, itemId, rawTimelinePath, rawConvertInfoPath, itemsWriter, eventsWriter),
                "image" => WriteImageTimeline(timeline, product, itemId, rawTimelinePath, rawConvertInfoPath, itemsWriter, eventsWriter),
                "video" => WriteVideoTimeline(timeline, product, itemId, rawTimelinePath, rawConvertInfoPath, itemsWriter, eventsWriter),
                "pc" => WritePcTimeline(timeline, product, itemId, rawTimelinePath, rawConvertInfoPath, itemsWriter, eventsWriter),
                _ => WriteThreadTimeline(timeline, product, itemId, rawTimelinePath, rawConvertInfoPath, itemsWriter, eventsWriter),
            };

            result["itemCount"] = GetInt(result, "itemCount", 0) + 1;
            result["eventCount"] = GetInt(result, "eventCount", 0) + eventCount;
            if (GetInt(result, "itemCount", 0) == 1 || GetInt(result, "itemCount", 0) % 25 == 0)
            {
                progress("importing", "Importing " + product.DisplayName + " data: " + GetInt(result, "itemCount", 0) + " items.");
            }
        }

        result["included"] = true;
        return result;
    }

    private static int WriteAudioTimeline(
        JsonObject timeline,
        TimelineStoreProduct product,
        string itemId,
        string rawTimelinePath,
        string rawConvertInfoPath,
        StreamWriter itemsWriter,
        StreamWriter eventsWriter)
    {
        var source = GetObject(timeline, "source");
        var resolvedItemId = GetStringAny(timeline, ["item_id", "itemId", "media_id", "mediaId"], itemId);
        if (string.IsNullOrEmpty(resolvedItemId))
        {
            resolvedItemId = itemId;
        }
        var title = GetStringAny(source, ["filename", "file_name", "display_name", "source_file_identity"], resolvedItemId);
        var turns = GetArray(timeline, "turns");

        WriteJsonLine(itemsWriter, NewItemRow(product, resolvedItemId, "audio", title, GetStringAny(source, ["recorded_at", "created_at", "modified_at"], string.Empty), string.Empty, turns.Count, rawTimelinePath, rawConvertInfoPath));

        var sequence = 0;
        foreach (var turn in turns.OfType<JsonObject>())
        {
            var transcriptText = GetReadableTranscriptText(turn);
            var phoneTokenText = GetStringAny(turn, ["phone_tokens", "phoneTokens", "acoustic_units", "acousticUnits"], string.Empty);
            WriteJsonLine(eventsWriter, NewEventRow(
                product.ProductId + ":" + resolvedItemId + ":turn:" + sequence,
                product.ProductId,
                resolvedItemId,
                "audio_turn",
                sequence,
                GetStringAny(turn, ["absolute_start_at", "absoluteStartAt"], string.Empty),
                GetStringAny(turn, ["absolute_end_at", "absoluteEndAt"], string.Empty),
                GetNumberAny(turn, ["start_sec", "startSec"]),
                GetNumberAny(turn, ["end_sec", "endSec"]),
                GetStringAny(turn, ["absolute_start_at", "absoluteStartAt"], string.Empty).Length > 0 ? "absolute" : "source_relative",
                "speaker",
                GetString(turn, "speaker", string.Empty),
                transcriptText.Length > 0 ? "transcript_text" : "phone_tokens",
                transcriptText.Length > 0 ? transcriptText : phoneTokenText,
                NewSourceRef(rawTimelinePath, rawConvertInfoPath)));
            sequence++;
        }

        return sequence;
    }

    private static int WriteThreadTimeline(
        JsonObject timeline,
        TimelineStoreProduct product,
        string itemId,
        string rawTimelinePath,
        string rawConvertInfoPath,
        StreamWriter itemsWriter,
        StreamWriter eventsWriter)
    {
        var resolvedItemId = GetStringAny(timeline, ["thread_id", "conversation_id", "item_id", "id"], itemId);
        if (string.IsNullOrEmpty(resolvedItemId))
        {
            resolvedItemId = itemId;
        }
        var messages = GetArray(timeline, "messages");
        var createdAt = GetString(timeline, "created_at", string.Empty);
        var updatedAt = GetString(timeline, "updated_at", string.Empty);
        var title = GetString(timeline, "title", resolvedItemId);

        WriteJsonLine(itemsWriter, NewItemRow(product, resolvedItemId, "thread", title, createdAt, updatedAt, messages.Count, rawTimelinePath, rawConvertInfoPath));

        var sequence = 0;
        foreach (var message in messages.OfType<JsonObject>())
        {
            var created = GetStringAny(message, ["created_at", "createdAt", "timestamp"], string.Empty);
            WriteJsonLine(eventsWriter, NewEventRow(
                product.ProductId + ":" + resolvedItemId + ":message:" + sequence,
                product.ProductId,
                resolvedItemId,
                "message",
                sequence,
                created,
                created,
                null,
                null,
                created.Length > 0 ? "absolute" : "sequence",
                "role",
                GetString(message, "role", string.Empty),
                "text",
                GetStringAny(message, ["text", "content", "body"], string.Empty),
                NewSourceRef(rawTimelinePath, rawConvertInfoPath)));
            sequence++;
        }

        return sequence;
    }

    private static int WriteImageTimeline(
        JsonObject timeline,
        TimelineStoreProduct product,
        string itemId,
        string rawTimelinePath,
        string rawConvertInfoPath,
        StreamWriter itemsWriter,
        StreamWriter eventsWriter)
    {
        var source = GetObject(timeline, "source");
        var resolvedItemId = GetStringAny(timeline, ["item_id", "itemId", "record_id", "recordId"], itemId);
        if (string.IsNullOrEmpty(resolvedItemId))
        {
            resolvedItemId = itemId;
        }
        var title = GetStringAny(source, ["relative_path", "path", "display_name"], resolvedItemId);
        var events = GetArray(timeline, "events");

        WriteJsonLine(itemsWriter, NewItemRow(product, resolvedItemId, "image", title, string.Empty, string.Empty, events.Count, rawTimelinePath, rawConvertInfoPath));

        var sequence = 0;
        foreach (var imageEvent in events.OfType<JsonObject>())
        {
            var time = GetStringAny(imageEvent, ["time", "created_at", "createdAt", "timestamp"], string.Empty);
            var summary = GetNode(imageEvent, "summary");
            WriteJsonLine(eventsWriter, NewEventRow(
                product.ProductId + ":" + resolvedItemId + ":image:" + sequence,
                product.ProductId,
                resolvedItemId,
                GetString(imageEvent, "type", "image_event"),
                sequence,
                time,
                time,
                null,
                null,
                time.Length > 0 ? "absolute" : "sequence",
                "source",
                "image",
                "image_summary",
                summary?.ToJsonString() ?? "{}",
                NewSourceRef(rawTimelinePath, rawConvertInfoPath)));
            sequence++;
        }

        return sequence;
    }

    private static int WriteVideoTimeline(
        JsonObject timeline,
        TimelineStoreProduct product,
        string itemId,
        string rawTimelinePath,
        string rawConvertInfoPath,
        StreamWriter itemsWriter,
        StreamWriter eventsWriter)
    {
        var resolvedItemId = GetStringAny(timeline, ["itemId", "item_id", "id"], itemId);
        if (string.IsNullOrEmpty(resolvedItemId))
        {
            resolvedItemId = itemId;
        }
        var lanes = GetObject(timeline, "lanes");
        var eventCount = 0;
        if (lanes is not null)
        {
            foreach (var property in lanes)
            {
                eventCount += GetArrayValues(property.Value).Count;
            }
        }

        WriteJsonLine(itemsWriter, NewItemRow(
            product,
            resolvedItemId,
            "video",
            GetVideoSourceTitle(timeline, resolvedItemId),
            string.Empty,
            GetStringAny(timeline, ["generatedAt", "generated_at"], string.Empty),
            eventCount,
            rawTimelinePath,
            rawConvertInfoPath,
            new JsonObject
            {
                ["sourceFingerprint"] = GetStringAny(timeline, ["sourceFingerprint", "source_fingerprint"], string.Empty),
                ["durationSec"] = GetNumberAny(timeline, ["durationSec", "duration_sec"]),
            }));

        var sequence = 0;
        if (lanes is null)
        {
            return 0;
        }

        foreach (var property in lanes)
        {
            var lane = ConvertTimelineText(property.Key);
            foreach (var node in GetArrayValues(property.Value).OfType<JsonObject>())
            {
                var eventType = GetStringAny(node, ["eventType", "event_type"], "video_event");
                var sourceRef = NewSourceRef(rawTimelinePath, rawConvertInfoPath);
                sourceRef["lane"] = lane;
                sourceRef["frameId"] = GetString(node, "frameId", string.Empty);
                sourceRef["artifactPath"] = GetString(node, "artifactPath", string.Empty);
                sourceRef["source"] = GetString(node, "source", string.Empty);

                var content = GetVideoEventContent(node, lane, eventType);
                WriteJsonLine(eventsWriter, NewEventRow(
                    product.ProductId + ":" + resolvedItemId + ":video:" + sequence,
                    product.ProductId,
                    resolvedItemId,
                    eventType,
                    sequence,
                    string.Empty,
                    string.Empty,
                    GetNumberAny(node, ["startSec", "start_sec", "timeSec", "time_sec"]),
                    GetNumberAny(node, ["endSec", "end_sec", "timeSec", "time_sec"]),
                    "source_relative",
                    "source",
                    lane.Length > 0 ? "video:" + lane : "video",
                    GetString(content, "kind", "video_event_summary"),
                    GetString(content, "value", string.Empty),
                    sourceRef));
                sequence++;
            }
        }

        return sequence;
    }

    private static int WritePcTimeline(
        JsonObject timeline,
        TimelineStoreProduct product,
        string itemId,
        string rawTimelinePath,
        string rawConvertInfoPath,
        StreamWriter itemsWriter,
        StreamWriter eventsWriter)
    {
        var resolvedItemId = GetStringAny(timeline, ["item_id", "itemId", "id"], itemId);
        if (string.IsNullOrEmpty(resolvedItemId))
        {
            resolvedItemId = itemId;
        }
        var events = GetArray(timeline, "events");

        WriteJsonLine(itemsWriter, NewItemRow(
            product,
            resolvedItemId,
            "windows_pc",
            GetString(timeline, "title", resolvedItemId),
            GetStringAny(timeline, ["created_at_utc", "createdAtUtc", "created_at", "createdAt"], string.Empty),
            GetStringAny(timeline, ["updated_at_utc", "updatedAtUtc", "updated_at", "updatedAt"], string.Empty),
            events.Count,
            rawTimelinePath,
            rawConvertInfoPath));

        var sequence = 0;
        foreach (var pcEvent in events.OfType<JsonObject>())
        {
            var occurredAt = GetStringAny(pcEvent, ["occurred_at_utc", "occurredAtUtc", "occurred_at", "occurredAt", "timestamp"], string.Empty);
            var sourceRef = NewSourceRef(rawTimelinePath, rawConvertInfoPath);
            sourceRef["recordedAt"] = GetStringAny(pcEvent, ["recorded_at_utc", "recordedAtUtc"], string.Empty);
            sourceRef["runId"] = GetStringAny(pcEvent, ["run_id", "runId"], string.Empty);
            sourceRef["updateStatus"] = GetStringAny(pcEvent, ["update_status", "updateStatus"], string.Empty);
            sourceRef["artifactRefs"] = GetNodeAny(pcEvent, ["artifact_refs", "artifactRefs"])?.DeepClone();

            WriteJsonLine(eventsWriter, NewEventRow(
                GetStringAny(pcEvent, ["event_id", "eventId"], product.ProductId + ":" + resolvedItemId + ":pc:" + sequence),
                product.ProductId,
                resolvedItemId,
                GetStringAny(pcEvent, ["event_type", "eventType"], "pc_snapshot"),
                sequence,
                occurredAt,
                occurredAt,
                null,
                null,
                occurredAt.Length > 0 ? "absolute" : "sequence",
                "source",
                "pc",
                "pc_snapshot_summary",
                GetString(pcEvent, "summary", string.Empty),
                sourceRef));
            sequence++;
        }

        return sequence;
    }

    private static JsonObject NewItemRow(
        TimelineStoreProduct product,
        string itemId,
        string itemType,
        string title,
        string createdAt,
        string updatedAt,
        int eventCount,
        string rawTimelinePath,
        string rawConvertInfoPath,
        JsonObject? extraSourceRef = null)
    {
        var sourceRef = NewSourceRef(rawTimelinePath, rawConvertInfoPath);
        if (extraSourceRef is not null)
        {
            foreach (var property in extraSourceRef)
            {
                sourceRef[property.Key] = property.Value?.DeepClone();
            }
        }

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["product"] = product.ProductId,
            ["productName"] = product.DisplayName,
            ["itemId"] = itemId,
            ["itemType"] = itemType,
            ["title"] = title,
            ["createdAt"] = createdAt,
            ["updatedAt"] = updatedAt,
            ["eventCount"] = eventCount,
            ["sourceRef"] = sourceRef,
        };
    }

    private static JsonObject NewEventRow(
        string eventId,
        string productId,
        string itemId,
        string eventType,
        int sequence,
        string absoluteStartAt,
        string absoluteEndAt,
        double? relativeStartSec,
        double? relativeEndSec,
        string timeBasis,
        string actorType,
        string actorLabel,
        string contentKind,
        string contentValue,
        JsonObject sourceRef)
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["eventId"] = eventId,
            ["product"] = productId,
            ["itemId"] = itemId,
            ["eventType"] = eventType,
            ["sequence"] = sequence,
            ["time"] = new JsonObject
            {
                ["absoluteStartAt"] = absoluteStartAt,
                ["absoluteEndAt"] = absoluteEndAt,
                ["relativeStartSec"] = relativeStartSec,
                ["relativeEndSec"] = relativeEndSec,
                ["timeBasis"] = timeBasis,
            },
            ["actor"] = new JsonObject
            {
                ["type"] = actorType,
                ["label"] = actorLabel,
            },
            ["content"] = new JsonObject
            {
                ["kind"] = contentKind,
                ["value"] = contentValue,
            },
            ["sourceRef"] = sourceRef,
        };
    }

    private void WriteJobStatus(
        string jobId,
        string state,
        string stage,
        string message,
        string startedAt,
        string error = "",
        JsonObject? result = null,
        int itemCount = 0,
        int eventCount = 0)
    {
        var now = DateTimeOffset.Now.ToString("o");
        _workerStatus.WriteWorkerJobStatus(new JsonObject
        {
            ["jobId"] = jobId,
            ["kind"] = "timeline_rebuild",
            ["state"] = state,
            ["stage"] = stage,
            ["message"] = message,
            ["error"] = error,
            ["startedAt"] = startedAt,
            ["updatedAt"] = now,
            ["completedAt"] = state is "completed" or "failed" ? now : string.Empty,
            ["itemCount"] = itemCount,
            ["eventCount"] = eventCount,
            ["result"] = result?.DeepClone(),
        });
    }

    private static JsonObject NewRefreshResult(
        TimelineStoreProduct product,
        bool refreshed,
        bool skipped,
        string reason,
        JsonObject? result)
    {
        var payload = new JsonObject
        {
            ["productId"] = product.ProductId,
            ["displayName"] = product.DisplayName,
            ["refreshed"] = refreshed,
            ["skipped"] = skipped,
            ["result"] = result?.DeepClone(),
        };
        if (!string.IsNullOrEmpty(reason))
        {
            payload["reason"] = reason;
        }

        return payload;
    }

    private static ProductDownloadResult NewDownloadResult(TimelineStoreProduct product, JsonObject payload)
    {
        return new ProductDownloadResult(
            product.ProductId,
            product.DisplayName,
            GetString(payload, "archivePath", string.Empty));
    }

    private static JsonObject NewSourceRef(string rawTimelinePath, string rawConvertInfoPath)
        => new()
        {
            ["timelinePath"] = rawTimelinePath,
            ["convertInfoPath"] = rawConvertInfoPath,
        };

    private static string GetVideoSourceTitle(JsonObject timeline, string fallback)
    {
        var lanes = GetObject(timeline, "lanes");
        var visualEvents = GetArray(lanes, "visual");
        foreach (var node in visualEvents.OfType<JsonObject>())
        {
            var sourcePath = GetString(node, "sourcePath", string.Empty);
            if (!string.IsNullOrEmpty(sourcePath))
            {
                return Path.GetFileName(sourcePath);
            }
        }

        return fallback;
    }

    private static JsonObject GetVideoEventContent(JsonObject videoEvent, string lane, string eventType)
    {
        var text = GetString(videoEvent, "text", string.Empty);
        if (eventType.Equals("audio_transcript_segment", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["kind"] = "transcript_text", ["value"] = text };
        }
        if (eventType.Equals("audio_acoustic_units", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["kind"] = "phone_tokens", ["value"] = text };
        }
        if (eventType.Equals("frame_ocr_text", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["kind"] = "text", ["value"] = text };
        }

        return new JsonObject
        {
            ["kind"] = "video_event_summary",
            ["value"] = GetVideoEventReadableValue(videoEvent, lane, eventType),
        };
    }

    private static string GetVideoEventReadableValue(JsonObject videoEvent, string lane, string eventType)
    {
        var duration = FormatDuration(GetNumberAny(videoEvent, ["durationSec", "duration_sec"]));
        var sourceName = Path.GetFileName(GetString(videoEvent, "sourcePath", string.Empty));
        var artifactName = Path.GetFileName(GetString(videoEvent, "artifactPath", string.Empty));

        return eventType switch
        {
            "video_observed" => string.IsNullOrEmpty(sourceName) ? "Video source observed." : "Video source observed: " + sourceName,
            "video_interval" => string.IsNullOrEmpty(duration) ? "Video interval." : "Video duration: " + duration + ".",
            "audio_reference" => "Audio stream detected.",
            "audio_derivative" => string.IsNullOrEmpty(artifactName) ? "Audio derivative prepared." : "Audio derivative prepared: " + artifactName + ".",
            "audio_speech_candidate" => string.IsNullOrEmpty(duration) ? "Speech candidate interval." : "Speech candidate interval: " + duration + ".",
            "activity_candidate_interval" => string.IsNullOrEmpty(duration) ? "Activity candidate interval." : "Activity candidate interval: " + duration + ".",
            "activity_skipped_interval" => string.IsNullOrEmpty(duration) ? "Skipped activity interval." : "Skipped activity interval: " + duration + ".",
            _ => string.IsNullOrEmpty(duration)
                ? (string.IsNullOrEmpty(lane) ? "video" : lane) + " event '" + eventType + "'."
                : (string.IsNullOrEmpty(lane) ? "video" : lane) + " event '" + eventType + "': " + duration + ".",
        };
    }

    private static string FormatDuration(double? seconds)
    {
        if (seconds is null)
        {
            return string.Empty;
        }
        if (seconds >= 3600)
        {
            return (seconds.Value / 3600.0).ToString("0.#", CultureInfo.InvariantCulture) + " hours";
        }
        if (seconds >= 60)
        {
            return (seconds.Value / 60.0).ToString("0.#", CultureInfo.InvariantCulture) + " minutes";
        }

        return seconds.Value.ToString("0.#", CultureInfo.InvariantCulture) + " seconds";
    }

    private static string GetReadableTranscriptText(JsonObject turn)
    {
        var candidates = new[]
        {
            GetStringAny(turn, ["display_text", "displayText", "text", "transcript", "transcript_text", "transcriptText"], string.Empty),
            GetStringAny(GetObject(turn, "text"), ["display", "value", "raw"], string.Empty),
        };
        return candidates.FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate)) ?? string.Empty;
    }

    private static JsonObject? ReadZipEntryJson(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CopyZipEntryToFile(ZipArchiveEntry entry, string destinationRoot, string prefix)
    {
        var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(relative) || relative.EndsWith("/", StringComparison.Ordinal))
        {
            return;
        }

        var targetRelative = string.IsNullOrEmpty(prefix) ? relative : prefix + "/" + relative;
        var destination = Path.Combine(destinationRoot, targetRelative.Replace('/', Path.DirectorySeparatorChar));
        var destinationRootFull = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destinationFull = Path.GetFullPath(destination);
        if (!destinationFull.StartsWith(destinationRootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var parent = Path.GetDirectoryName(destinationFull);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        entry.ExtractToFile(destinationFull, overwrite: true);
    }

    private static void SortEventsFile(string eventsPath)
    {
        if (string.IsNullOrEmpty(eventsPath) || !File.Exists(eventsPath))
        {
            return;
        }

        var rows = new List<SortRow>();
        var ordinal = 0;
        foreach (var line in File.ReadLines(eventsPath))
        {
            var text = line.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            rows.Add(new SortRow(GetEventSortKey(text, ordinal), ordinal, text));
            ordinal++;
        }

        if (rows.Count <= 1)
        {
            return;
        }

        var tempPath = eventsPath + ".tmp";
        using (var writer = new StreamWriter(tempPath, append: false, new UTF8Encoding(false)))
        {
            foreach (var row in rows.OrderBy(row => row.SortKey, StringComparer.Ordinal).ThenBy(row => row.Ordinal))
            {
                writer.WriteLine(row.Line);
            }
        }
        File.Move(tempPath, eventsPath, overwrite: true);
    }

    private static string GetEventSortKey(string jsonLine, int ordinal)
    {
        try
        {
            var row = JsonNode.Parse(jsonLine) as JsonObject;
            var time = GetObject(row, "time");
            var absoluteStartAt = GetString(time, "absoluteStartAt", string.Empty);
            var product = GetString(row, "product", string.Empty);
            var itemId = GetString(row, "itemId", string.Empty);
            var sequence = GetInt(row, "sequence", 0);
            if (!string.IsNullOrEmpty(absoluteStartAt))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"0|{absoluteStartAt}|{product}|{itemId}|{sequence:D10}|{ordinal:D10}");
            }

            var relativeStart = GetNumber(GetNode(time, "relativeStartSec"));
            if (relativeStart is not null)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"1|{product}|{itemId}|{relativeStart.Value:0000000000.000000}|{sequence:D10}|{ordinal:D10}");
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"2|{product}|{itemId}|{sequence:D10}|{ordinal:D10}");
        }
        catch (JsonException)
        {
            return string.Create(CultureInfo.InvariantCulture, $"9|{ordinal:D10}");
        }
    }

    private string GetManifestPath()
        => Path.Combine(_settings.GetStoreDirectory(), "manifest.json");

    private string GetItemsPath()
        => Path.Combine(_settings.GetStoreDirectory(), "items.jsonl");

    private string GetEventsPath()
        => Path.Combine(_settings.GetStoreDirectory(), "events.jsonl");

    private static void WriteJsonLine(StreamWriter writer, JsonObject payload)
        => writer.WriteLine(payload.ToJsonString());

    private static void WriteJsonFile(string path, JsonObject payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static JsonArray CloneArray(JsonArray source)
    {
        var clone = new JsonArray();
        foreach (var item in source)
        {
            clone.Add(item?.DeepClone());
        }

        return clone;
    }

    private static JsonObject? GetObject(JsonObject? source, string name)
        => GetNode(source, name) as JsonObject;

    private static JsonNode? GetNode(JsonObject? source, string name)
    {
        if (source is null)
        {
            return null;
        }

        foreach (var property in source)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static JsonNode? GetNodeAny(JsonObject? source, string[] names)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is not null)
            {
                return node;
            }
        }

        return null;
    }

    private static List<JsonNode?> GetArray(JsonObject? source, string name)
        => GetArrayValues(GetNode(source, name));

    private static List<JsonNode?> GetArrayValues(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array.ToList();
        }
        return node is null ? [] : [node];
    }

    private static string GetString(JsonObject? source, string name, string fallback)
        => ConvertTimelineText(GetNode(source, name), fallback);

    private static string GetStringAny(JsonObject? source, string[] names, string fallback)
        => ConvertTimelineText(GetNodeAny(source, names), fallback);

    private static int GetInt(JsonObject? source, string name, int fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<int>();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }

        return int.TryParse(ConvertTimelineText(node, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static double? GetNumberAny(JsonObject? source, string[] names)
        => GetNumber(GetNodeAny(source, names));

    private static double? GetNumber(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<double>();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }

        return double.TryParse(ConvertTimelineText(node, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string ConvertTimelineText(object? value)
        => ConvertTimelineText(value, string.Empty);

    private static string ConvertTimelineText(object? value, string fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value is JsonNode node)
        {
            try
            {
                return node.GetValueKind() switch
                {
                    JsonValueKind.String => node.GetValue<string>()?.Trim() ?? string.Empty,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => fallback,
                    _ => node.GetValue<object>()?.ToString()?.Trim() ?? fallback,
                };
            }
            catch (InvalidOperationException)
            {
                return node.ToJsonString();
            }
        }

        return value.ToString()?.Trim() ?? fallback;
    }

    private static string GetSafeSegment(string value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return "item";
        }

        var safe = Regex.Replace(text, "[^A-Za-z0-9._-]+", "_", RegexOptions.CultureInvariant).Trim('.', '_', '-');
        if (string.IsNullOrEmpty(safe))
        {
            return "item";
        }

        return safe.Length > 120 ? safe[..120] : safe;
    }

    private sealed record TimelineStoreProduct(string ProductId, string DisplayName);

    private sealed record ProductDownloadResult(string ProductId, string DisplayName, string ArchivePath);

    private sealed record SortRow(string SortKey, int Ordinal, string Line);
}
