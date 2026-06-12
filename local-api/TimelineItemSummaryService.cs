using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class TimelineItemSummaryService
{
    private const string PromptVersion = "item-summary-v2";
    private const int MaxDirectSourceChars = 12000;
    private const int MaxChunkSourceChars = 6000;
    private const int FallbackChunkSourceChars = 6000;
    private const int MaxCompressedSummaryChars = 2000;
    private const int MaxBriefSummaryChars = 500;
    private const int MaxSummaryRewriteAttempts = 3;
    private const int DefaultSummaryBatchItemLimit = 20;
    private const int MaxSummaryBatchItemLimit = 100;
    private const int MaxChunkedSummarySourceChars = 30000;
    private const int MaxSampledSourceChars = 12000;
    private static readonly HashSet<string> SupportedProducts = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio",
        "video",
        "image",
        "chatgpt",
        "windows-codex",
    };

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimelineSettingsService _settings;
    private readonly TimelineStoreService _store;
    private readonly TimelineOperationLogService _operations;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly object _sync = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeJobs = new(StringComparer.Ordinal);

    public TimelineItemSummaryService(
        TimelineSettingsService settings,
        TimelineStoreService store,
        TimelineOperationLogService operations,
        IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _store = store;
        _operations = operations;
        _httpClientFactory = httpClientFactory;
    }

    public JsonObject Start(JsonObject? request)
    {
        lock (_sync)
        {
            var latest = ReadStatus(string.Empty);
            if (IsActive(latest))
            {
                return latest;
            }

            var jobId = NewJobId();
            var startedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            var status = NewStatus(jobId, "queued", "queued", "素材概要の生成ジョブを開始しました。", startedAt);
            WriteStatus(status);

            var cancellation = new CancellationTokenSource();
            _activeJobs[jobId] = cancellation;

            _operations.WriteOperationEvent(
                jobId,
                "worker",
                "Timeline",
                "item_summary",
                "queued",
                "Timeline item summary job was queued.");

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunJobAsync(jobId, request?.DeepClone() as JsonObject, startedAt, cancellation.Token);
                }
                finally
                {
                    lock (_sync)
                    {
                        _activeJobs.Remove(jobId);
                    }

                    cancellation.Dispose();
                }
            });

            return status;
        }
    }

    public JsonObject Cancel(string? jobId)
    {
        var id = ConvertTimelineText(jobId);
        if (string.IsNullOrEmpty(id))
        {
            id = GetString(ReadStatus(string.Empty), "jobId", string.Empty);
        }

        if (string.IsNullOrEmpty(id))
        {
            return NewStatus(string.Empty, "none", "none", "素材概要の実行中ジョブはありません。", string.Empty);
        }

        lock (_sync)
        {
            if (_activeJobs.TryGetValue(id, out var cancellation))
            {
                cancellation.Cancel();
                var status = ReadStatus(id);
                status["state"] = "canceling";
                status["stage"] = "canceling";
                status["message"] = "素材概要の生成を停止しています。";
                status["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                WriteStatus(status);
                return status;
            }
        }

        var current = ReadStatus(id);
        if (IsActive(current))
        {
            current["state"] = "canceled";
            current["stage"] = "canceled";
            current["message"] = "素材概要の生成ジョブは実行中ではありませんでした。";
            current["completedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            current["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            WriteStatus(current);
        }

        return current;
    }

    public JsonObject GetStatus(string? jobId)
    {
        var status = ReadStatus(jobId);
        if (IsActive(status))
        {
            var id = GetString(status, "jobId", string.Empty);
            lock (_sync)
            {
                if (!string.IsNullOrEmpty(id) && !_activeJobs.ContainsKey(id))
                {
                    status["state"] = "interrupted";
                    status["stage"] = "interrupted";
                    status["message"] = "素材概要の生成ジョブは停止しました。再実行できます。";
                    status["completedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                    status["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                    WriteStatus(status);
                }
            }
        }

        return status;
    }

    public JsonObject GetSummary(string? product, string? itemId)
    {
        var productId = ConvertTimelineText(product);
        var item = ConvertTimelineText(itemId);
        if (string.IsNullOrEmpty(productId) || string.IsNullOrEmpty(item))
        {
            return new JsonObject
            {
                ["available"] = false,
                ["message"] = "product and itemId are required.",
            };
        }

        var path = GetSummaryPath(productId, item);
        if (!File.Exists(path))
        {
            return new JsonObject
            {
                ["available"] = false,
                ["product"] = productId,
                ["itemId"] = item,
                ["path"] = path,
                ["message"] = "素材概要はまだ作成されていません。",
            };
        }

        try
        {
            var payload = ReadJsonFile(path);
            payload["available"] = true;
            payload["path"] = path;
            return payload;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new JsonObject
            {
                ["available"] = false,
                ["product"] = productId,
                ["itemId"] = item,
                ["path"] = path,
                ["message"] = "素材概要を読み取れませんでした。",
                ["error"] = ex.Message,
            };
        }
    }

    public JsonObject GetTargets(JsonObject? request)
    {
        var overview = _store.GetOverview();
        if (!overview.Available)
        {
            return new JsonObject
            {
                ["available"] = false,
                ["targets"] = new JsonArray(),
                ["targetCount"] = 0,
                ["message"] = overview.Message,
            };
        }

        var targets = LoadTargets(request, applyDefaultBatchLimit: false);
        var rows = new JsonArray();
        foreach (var target in targets)
        {
            rows.Add(new JsonObject
            {
                ["product"] = target.Product,
                ["productName"] = target.ProductName,
                ["itemId"] = target.ItemId,
                ["itemType"] = target.ItemType,
                ["title"] = target.Title,
                ["eventCount"] = target.EventCount,
                ["hasSummary"] = File.Exists(GetSummaryPath(target.Product, target.ItemId)),
            });
        }

        return new JsonObject
        {
            ["available"] = true,
            ["targetCount"] = rows.Count,
            ["targets"] = rows,
            ["message"] = string.Empty,
        };
    }

    private async Task RunJobAsync(
        string jobId,
        JsonObject? request,
        string startedAt,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        var skipped = 0;
        var failed = 0;
        var targets = new List<ItemSummaryTarget>();
        try
        {
            WriteStatus(NewStatus(jobId, "running", "preparing", "素材概要の対象を確認しています。", startedAt));

            var overview = _store.GetOverview();
            if (!overview.Available)
            {
                throw new InvalidOperationException(overview.Message);
            }

            targets = LoadTargets(request, applyDefaultBatchLimit: true);
            var force = GetBool(request, "force", false);
            var status = NewStatus(jobId, "running", "loading", "素材ごとの本文を読み込んでいます。", startedAt);
            status["totalItems"] = targets.Count;
            WriteStatus(status);

            var eventsByItem = LoadEventsForTargets(targets);
            var summarySettings = GetSummarySettings();

            for (var index = 0; index < targets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = targets[index];
                var key = NewTargetKey(target.Product, target.ItemId);
                eventsByItem.TryGetValue(key, out var events);
                events ??= [];

                var sourceText = BuildSourceText(target, events);
                var inputSignature = ComputeInputSignature(target, sourceText, summarySettings);
                var summaryPath = GetSummaryPath(target.Product, target.ItemId);

                if (!force && TryReadReusableSummary(summaryPath, inputSignature))
                {
                    skipped += 1;
                    WriteProgress(jobId, startedAt, "running", "skipping", "既存の素材概要を再利用しています。", targets.Count, completed, skipped, failed, target);
                    continue;
                }

                WriteProgress(jobId, startedAt, "running", "summarizing", "素材概要を生成しています。", targets.Count, completed, skipped, failed, target);

                try
                {
                    var summary = await GenerateSummaryWithFallbackAsync(target, sourceText, summarySettings, cancellationToken);
                    var payload = NewSummaryPayload(target, summary, sourceText, inputSignature, summarySettings);
                    WriteJsonFile(summaryPath, payload);
                    WriteIndex();
                    completed += 1;
                    WriteProgress(jobId, startedAt, "running", "summarizing", "素材概要を保存しました。", targets.Count, completed, skipped, failed, target);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed += 1;
                    WriteFailureSummary(summaryPath, target, sourceText, inputSignature, summarySettings, ex.Message);
                    WriteProgress(jobId, startedAt, "running", "summarizing", "素材概要の生成に失敗しました。", targets.Count, completed, skipped, failed, target, ex.Message);
                }
            }

            var finalState = failed > 0 ? "completed_with_errors" : "completed";
            var finalMessage = failed > 0
                ? "素材概要の生成が完了しました。一部の素材で失敗があります。"
                : "素材概要の生成が完了しました。";
            var final = NewStatus(jobId, finalState, "completed", finalMessage, startedAt);
            final["totalItems"] = targets.Count;
            final["completedItems"] = completed;
            final["skippedItems"] = skipped;
            final["failedItems"] = failed;
            final["completedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            final["result"] = new JsonObject
            {
                ["summaryRoot"] = GetSummaryRoot(),
                ["indexPath"] = GetIndexPath(),
            };
            WriteStatus(final);
            WriteIndex();

            _operations.WriteOperationEvent(
                jobId,
                "llm",
                "Timeline",
                "item_summary",
                finalState,
                finalMessage,
                details: new JsonObject
                {
                    ["totalItems"] = targets.Count,
                    ["completedItems"] = completed,
                    ["skippedItems"] = skipped,
                    ["failedItems"] = failed,
                });
        }
        catch (OperationCanceledException)
        {
            var status = NewStatus(jobId, "canceled", "canceled", "素材概要の生成を停止しました。", startedAt);
            status["totalItems"] = targets.Count;
            status["completedItems"] = completed;
            status["skippedItems"] = skipped;
            status["failedItems"] = failed;
            status["completedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            WriteStatus(status);
            _operations.WriteOperationEvent(jobId, "llm", "Timeline", "item_summary", "canceled", "Timeline item summary job was canceled.");
        }
        catch (Exception ex)
        {
            var status = NewStatus(jobId, "failed", "failed", "素材概要の生成に失敗しました。", startedAt);
            status["error"] = ex.Message;
            status["totalItems"] = targets.Count;
            status["completedItems"] = completed;
            status["skippedItems"] = skipped;
            status["failedItems"] = failed;
            status["completedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            WriteStatus(status);
            _operations.WriteOperationEvent(jobId, "llm", "Timeline", "item_summary", "failed", ex.Message, stderr: ex.Message);
        }
    }

    private async Task<JsonObject> GenerateSummaryWithFallbackAsync(
        ItemSummaryTarget target,
        string sourceText,
        JsonObject settings,
        CancellationToken cancellationToken)
    {
        var limits = NewSummaryLimits(sourceText.Length);
        if (sourceText.Length > MaxChunkedSummarySourceChars)
        {
            return await GenerateSampledSummaryAsync(
                target,
                sourceText,
                settings,
                "Source text exceeded the sampled-summary size limit.",
                cancellationToken);
        }

        if (sourceText.Length > MaxDirectSourceChars)
        {
            try
            {
                return await GenerateChunkedSummaryAsync(
                    target,
                    sourceText,
                    settings,
                    "Source text exceeded the direct-summary size limit.",
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await GenerateSampledSummaryAsync(
                    target,
                    sourceText,
                    settings,
                    ex.Message,
                    cancellationToken);
            }
        }

        try
        {
            var summary = await InvokeOllamaSummaryAsync(settings, target, sourceText, "full", limits, cancellationToken);
            return await EnforceSummaryPolicyAsync(settings, summary, sourceText, limits, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                return await GenerateChunkedSummaryAsync(
                    target,
                    sourceText,
                    settings,
                    ex.Message,
                    cancellationToken,
                    FallbackChunkSourceChars);
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                return await GenerateSampledSummaryAsync(
                    target,
                    sourceText,
                    settings,
                    inner.Message,
                    cancellationToken);
            }
        }
    }

    private async Task<JsonObject> GenerateSampledSummaryAsync(
        ItemSummaryTarget target,
        string sourceText,
        JsonObject settings,
        string reason,
        CancellationToken cancellationToken)
    {
        var limits = NewSummaryLimits(sourceText.Length);
        var sampledSource = BuildSampledSourceText(sourceText, reason);
        try
        {
            var summary = await InvokeOllamaSummaryAsync(settings, target, sampledSource, "sampled", limits, cancellationToken);
            summary = await EnforceSummaryPolicyAsync(
                settings,
                summary,
                sampledSource,
                limits,
                cancellationToken,
                expandShortSummary: false);
            summary["generationMode"] = "sampled";
            return summary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NewExtractiveFallbackSummary(target, sourceText, limits, ex.Message);
        }
    }

    private async Task<JsonObject> GenerateChunkedSummaryAsync(
        ItemSummaryTarget target,
        string sourceText,
        JsonObject settings,
        string? reason,
        CancellationToken cancellationToken,
        int chunkSourceChars = MaxChunkSourceChars)
    {
        var chunks = SplitText(sourceText, chunkSourceChars);
        var finalLimits = NewSummaryLimits(sourceText.Length);
        if (chunks.Count <= 1)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason)
                ? "Item summary generation failed."
                : reason);
        }

        var chunkSummaries = new JsonArray();
        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLimits = NewChunkSummaryLimits(chunks[index].Length);
            var chunkSummary = await InvokeOllamaSummaryAsync(settings, target, chunks[index], "chunk", chunkLimits, cancellationToken);
            chunkSummary = await EnforceSummaryPolicyAsync(
                settings,
                chunkSummary,
                chunks[index],
                chunkLimits,
                cancellationToken,
                expandShortSummary: false);
            chunkSummaries.Add(new JsonObject
            {
                ["chunkIndex"] = index + 1,
                ["briefSummary"] = GetString(chunkSummary, "briefSummary", string.Empty),
                ["compressedSummary"] = GetString(chunkSummary, "compressedSummary", string.Empty),
            });
        }

        var merged = await MergeSummaryObjectsAsync(
            target,
            chunkSummaries,
            sourceText.Length,
            settings,
            string.IsNullOrWhiteSpace(reason) ? "source text was too large." : reason,
            finalLimits,
            cancellationToken);
        merged["generationMode"] = "chunked";
        merged["chunkCount"] = chunks.Count;
        var mergeContext = NewMergeInput(
            string.IsNullOrWhiteSpace(reason) ? "source text was too large." : reason,
            sourceText.Length,
            chunkSummaries).ToJsonString(FileJsonOptions);
        if (mergeContext.Length > MaxDirectSourceChars)
        {
            mergeContext = "Original source chars: "
                + sourceText.Length.ToString(CultureInfo.InvariantCulture)
                + Environment.NewLine
                + "Chunk count: "
                + chunks.Count.ToString(CultureInfo.InvariantCulture)
                + Environment.NewLine
                + "The final summary was produced from recursively merged chunk summaries.";
        }

        return await EnforceSummaryPolicyAsync(settings, merged, mergeContext, finalLimits, cancellationToken);
    }

    private async Task<JsonObject> MergeSummaryObjectsAsync(
        ItemSummaryTarget target,
        JsonArray summaries,
        int originalSourceChars,
        JsonObject settings,
        string reason,
        SummaryLimits finalLimits,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        var mergeInput = NewMergeInput(reason, originalSourceChars, summaries).ToJsonString(FileJsonOptions);
        if (mergeInput.Length <= MaxDirectSourceChars || summaries.Count <= 1 || depth >= 4)
        {
            return await InvokeOllamaSummaryAsync(settings, target, mergeInput, "merge", finalLimits, cancellationToken);
        }

        var intermediate = new JsonArray();
        var batchIndex = 0;
        foreach (var batch in BatchSummaryObjects(summaries, MaxDirectSourceChars - 1000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            batchIndex += 1;
            var batchInput = NewMergeInput(reason, originalSourceChars, batch).ToJsonString(FileJsonOptions);
            var batchLimits = NewChunkSummaryLimits(batchInput.Length);
            var batchSummary = await InvokeOllamaSummaryAsync(
                settings,
                target,
                batchInput,
                "merge-chunk",
                batchLimits,
                cancellationToken);
            batchSummary = await EnforceSummaryPolicyAsync(
                settings,
                batchSummary,
                batchInput,
                batchLimits,
                cancellationToken,
                expandShortSummary: false);
            intermediate.Add(new JsonObject
            {
                ["chunkIndex"] = batchIndex,
                ["briefSummary"] = GetString(batchSummary, "briefSummary", string.Empty),
                ["compressedSummary"] = GetString(batchSummary, "compressedSummary", string.Empty),
            });
        }

        return await MergeSummaryObjectsAsync(
            target,
            intermediate,
            originalSourceChars,
            settings,
            reason,
            finalLimits,
            cancellationToken,
            depth + 1);
    }

    private static JsonObject NewMergeInput(string reason, int originalSourceChars, JsonArray chunkSummaries)
        => new()
        {
            ["reason"] = reason,
            ["originalSourceChars"] = originalSourceChars,
            ["chunkCount"] = chunkSummaries.Count,
            ["chunkSummaries"] = CloneArray(chunkSummaries),
        };

    private static List<JsonArray> BatchSummaryObjects(JsonArray summaries, int maxChars)
    {
        var batches = new List<JsonArray>();
        var current = new JsonArray();
        foreach (var summary in summaries)
        {
            var next = CloneArray(current);
            next.Add(summary?.DeepClone());
            if (current.Count > 0 && next.ToJsonString(FileJsonOptions).Length > maxChars)
            {
                batches.Add(current);
                current = new JsonArray();
            }

            current.Add(summary?.DeepClone());
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    private async Task<JsonObject> InvokeOllamaSummaryAsync(
        JsonObject settings,
        ItemSummaryTarget target,
        string sourceText,
        string mode,
        SummaryLimits limits,
        CancellationToken cancellationToken)
    {
        var model = GetString(settings, "model", "qwen3.5:9b");
        var baseUrl = GetString(settings, "ollamaBaseUrl", "http://127.0.0.1:11434");
        var numPredict = Math.Max(8192, GetInt(settings, "numPredict", 4096));
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = NewOllamaMessages(SummarySystemPrompt, NewSummaryPrompt(target, mode, sourceText, limits)),
            ["stream"] = false,
            ["format"] = NewOllamaSummaryJsonSchema(limits.CompressedUpper, NewBriefSummaryLimits(limits.CompressedUpper).Upper),
            ["think"] = false,
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.0,
                ["num_ctx"] = 16384,
                ["num_predict"] = numPredict,
            },
        };

        var response = await PostOllamaAsync(baseUrl, body, cancellationToken);
        var doneReason = GetString(response, "done_reason", string.Empty);
        if (doneReason.Equals("length", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ollama stopped because the output length limit was reached.");
        }

        var content = GetOllamaResponseContent(response);
        if (string.IsNullOrWhiteSpace(content))
        {
            var error = GetString(response, "error", string.Empty);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "Ollama response did not contain summary content."
                : "Ollama response contained an error: " + error);
        }

        JsonObject parsed;
        try
        {
            parsed = ParseLlmJsonText(content);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            parsed = await InvokeOllamaPlainSummaryAsync(settings, target, sourceText, mode, limits, cancellationToken);
        }

        parsed["generationMode"] = mode;
        return NormalizeSummaryPayload(parsed);
    }

    private async Task<JsonObject> EnforceSummaryPolicyAsync(
        JsonObject settings,
        JsonObject summary,
        string sourceContext,
        SummaryLimits limits,
        CancellationToken cancellationToken,
        bool expandShortSummary = true)
    {
        var current = NormalizeSummaryPayload(summary);
        var generationMode = GetString(current, "generationMode", string.Empty);
        var chunkCount = GetInt(current, "chunkCount", 0);
        var rewriteCount = 0;
        var status = "within_limit";

        for (var attempt = 1; attempt <= MaxSummaryRewriteAttempts; attempt++)
        {
            var compressed = GetString(current, "compressedSummary", string.Empty);
            var brief = GetString(current, "briefSummary", string.Empty);
            var briefLimits = NewBriefSummaryLimits(limits.CompressedUpper);

            var compressedTooLong = compressed.Length > limits.CompressedUpper;
            var compressedTooShort = expandShortSummary
                && limits.CompressedLower > 0
                && compressed.Length < limits.CompressedLower;
            var briefTooLong = brief.Length > briefLimits.Upper;
            var briefTooShort = expandShortSummary
                && briefLimits.Lower > 0
                && brief.Length < briefLimits.Lower;
            if (compressedTooLong || briefTooLong)
            {
                compressedTooShort = false;
                briefTooShort = false;
            }

            if (!compressedTooLong && !compressedTooShort && !briefTooLong && !briefTooShort)
            {
                status = "within_limit";
                break;
            }

            rewriteCount += 1;
            status = compressedTooLong || briefTooLong ? "rewritten_to_fit" : "expanded_to_target";
            var prompt = NewRewritePrompt(
                current,
                sourceContext,
                limits,
                briefLimits,
                compressedTooLong,
                compressedTooShort,
                briefTooLong,
                briefTooShort);
            current = await InvokeOllamaRewriteAsync(settings, prompt, limits, briefLimits, cancellationToken);
        }

        var finalBriefLimits = NewBriefSummaryLimits(limits.CompressedUpper);
        var finalCompressed = GetString(current, "compressedSummary", string.Empty);
        var finalBrief = GetString(current, "briefSummary", string.Empty);
        if (finalCompressed.Length > limits.CompressedUpper
            && !string.IsNullOrWhiteSpace(finalBrief)
            && finalBrief.Length <= limits.CompressedUpper)
        {
            current["compressedSummary"] = finalBrief;
            status = "fallback_to_brief_summary";
            finalCompressed = finalBrief;
        }

        if (finalCompressed.Length > limits.CompressedUpper
            || GetString(current, "briefSummary", string.Empty).Length > finalBriefLimits.Upper)
        {
            status = "over_limit_after_retries";
        }
        else if (expandShortSummary
            && !status.Equals("fallback_to_brief_summary", StringComparison.OrdinalIgnoreCase)
            && (GetString(current, "compressedSummary", string.Empty).Length < limits.CompressedLower
                || GetString(current, "briefSummary", string.Empty).Length < finalBriefLimits.Lower))
        {
            status = "under_target_after_retries";
        }

        current["summaryStatus"] = status;
        if (string.IsNullOrWhiteSpace(GetString(current, "generationMode", string.Empty)) && !string.IsNullOrWhiteSpace(generationMode))
        {
            current["generationMode"] = generationMode;
        }
        if (GetInt(current, "chunkCount", 0) <= 0 && chunkCount > 0)
        {
            current["chunkCount"] = chunkCount;
        }
        current["rewriteCount"] = rewriteCount;
        current["compressedCharCount"] = GetString(current, "compressedSummary", string.Empty).Length;
        current["briefCharCount"] = GetString(current, "briefSummary", string.Empty).Length;
        current["compressedLimit"] = limits.CompressedUpper;
        current["compressedLower"] = limits.CompressedLower;
        current["briefLimit"] = finalBriefLimits.Upper;
        current["briefLower"] = finalBriefLimits.Lower;
        return current;
    }

    private async Task<JsonObject> InvokeOllamaRewriteAsync(
        JsonObject settings,
        string prompt,
        SummaryLimits compressedLimits,
        BriefSummaryLimits briefLimits,
        CancellationToken cancellationToken)
    {
        var model = GetString(settings, "model", "qwen3.5:9b");
        var baseUrl = GetString(settings, "ollamaBaseUrl", "http://127.0.0.1:11434");
        var configuredNumPredict = Math.Max(2048, GetInt(settings, "numPredict", 4096));
        var targetNumPredict = Math.Max(2048, (compressedLimits.CompressedUpper + briefLimits.Upper) * 4 + 512);
        var numPredict = Math.Min(Math.Max(8192, configuredNumPredict), targetNumPredict);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = NewOllamaMessages(SummarySystemPrompt, prompt),
            ["stream"] = false,
            ["format"] = NewOllamaSummaryJsonSchema(compressedLimits.CompressedUpper, briefLimits.Upper),
            ["think"] = false,
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.0,
                ["num_ctx"] = 16384,
                ["num_predict"] = numPredict,
            },
        };

        var response = await PostOllamaAsync(baseUrl, body, cancellationToken);
        var doneReason = GetString(response, "done_reason", string.Empty);
        if (doneReason.Equals("length", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ollama stopped because the output length limit was reached.");
        }

        var content = GetOllamaResponseContent(response);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Ollama response did not contain summary content.");
        }

        try
        {
            return NormalizeSummaryPayload(ParseLlmJsonText(content));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return await InvokeOllamaPlainRewriteAsync(settings, prompt, compressedLimits, briefLimits, cancellationToken);
        }
    }

    private async Task<JsonObject> InvokeOllamaPlainSummaryAsync(
        JsonObject settings,
        ItemSummaryTarget target,
        string sourceText,
        string mode,
        SummaryLimits limits,
        CancellationToken cancellationToken)
    {
        var model = GetString(settings, "model", "qwen3.5:9b");
        var baseUrl = GetString(settings, "ollamaBaseUrl", "http://127.0.0.1:11434");
        var numPredict = Math.Max(4096, GetInt(settings, "numPredict", 4096));
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = NewOllamaMessages(PlainSummarySystemPrompt, NewPlainSummaryPrompt(target, mode, sourceText, limits)),
            ["stream"] = false,
            ["think"] = false,
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.0,
                ["num_ctx"] = 16384,
                ["num_predict"] = numPredict,
            },
        };

        var response = await PostOllamaAsync(baseUrl, body, cancellationToken);
        var content = GetOllamaResponseContent(response);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Ollama plain response did not contain summary content.");
        }

        return NormalizeSummaryPayload(ParsePlainSummaryText(content));
    }

    private async Task<JsonObject> InvokeOllamaPlainRewriteAsync(
        JsonObject settings,
        string prompt,
        SummaryLimits compressedLimits,
        BriefSummaryLimits briefLimits,
        CancellationToken cancellationToken)
    {
        var model = GetString(settings, "model", "qwen3.5:9b");
        var baseUrl = GetString(settings, "ollamaBaseUrl", "http://127.0.0.1:11434");
        var configuredNumPredict = Math.Max(2048, GetInt(settings, "numPredict", 4096));
        var targetNumPredict = Math.Max(2048, (compressedLimits.CompressedUpper + briefLimits.Upper) * 4 + 512);
        var numPredict = Math.Min(Math.Max(4096, configuredNumPredict), targetNumPredict);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = NewOllamaMessages(PlainSummarySystemPrompt, prompt + Environment.NewLine + Environment.NewLine + PlainSummaryOutputRule),
            ["stream"] = false,
            ["think"] = false,
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.0,
                ["num_ctx"] = 16384,
                ["num_predict"] = numPredict,
            },
        };

        var response = await PostOllamaAsync(baseUrl, body, cancellationToken);
        var content = GetOllamaResponseContent(response);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Ollama plain response did not contain summary content.");
        }

        return NormalizeSummaryPayload(ParsePlainSummaryText(content));
    }

    private static JsonArray NewOllamaMessages(string systemPrompt, string userPrompt)
        => new(
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = systemPrompt,
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = userPrompt,
            });

    private static JsonObject NewOllamaSummaryJsonSchema(int compressedMaxChars, int briefMaxChars)
        => new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("briefSummary", "compressedSummary"),
            ["properties"] = new JsonObject
            {
                ["briefSummary"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = Math.Max(1, briefMaxChars),
                },
                ["compressedSummary"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = Math.Max(1, compressedMaxChars),
                },
            },
        };

    private static string GetOllamaResponseContent(JsonObject response)
    {
        var generatedText = GetString(response, "response", string.Empty);
        if (!string.IsNullOrWhiteSpace(generatedText))
        {
            return generatedText;
        }

        return GetString(GetObject(response, "message"), "content", string.Empty);
    }

    private async Task<JsonObject> PostOllamaAsync(
        string baseUrl,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(900);
        var url = baseUrl.TrimEnd('/') + "/api/chat";
        Exception? lastException = null;
        var lastFailure = string.Empty;
        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var content = new StringContent(body.ToJsonString(CompactJsonOptions), Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(url, content, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastFailure = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                    if (attempt < maxAttempts && IsTransientOllamaStatusCode(response.StatusCode))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                        continue;
                    }

                    throw new InvalidOperationException(NewOllamaRequestFailedMessage(baseUrl, body, lastFailure));
                }

                return JsonNode.Parse(text) as JsonObject
                    ?? throw new InvalidOperationException("Ollama response was not a JSON object.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastException = ex;
                lastFailure = ex.Message;
                if (attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                    continue;
                }
            }
        }

        throw new InvalidOperationException(NewOllamaRequestFailedMessage(baseUrl, body, lastFailure), lastException);
    }

    private List<ItemSummaryTarget> LoadTargets(JsonObject? request, bool applyDefaultBatchLimit)
    {
        var products = GetRequestedProducts(request);
        var requestedItemId = GetString(request, "itemId", string.Empty);
        var maxItems = GetSummaryMaxItems(request, applyDefaultBatchLimit);
        var itemsPath = Path.Combine(_settings.GetStoreDirectory(), "items.jsonl");
        var targets = new List<ItemSummaryTarget>();

        if (!File.Exists(itemsPath))
        {
            return targets;
        }

        foreach (var line in File.ReadLines(itemsPath))
        {
            var text = line.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            JsonObject? item;
            try
            {
                item = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (item is null)
            {
                continue;
            }

            var product = GetString(item, "product", string.Empty);
            if (!SupportedProducts.Contains(product))
            {
                continue;
            }
            if (products.Count > 0 && !products.ContainsKey(product))
            {
                continue;
            }

            var itemId = GetString(item, "itemId", string.Empty);
            if (!string.IsNullOrEmpty(requestedItemId) && !itemId.Equals(requestedItemId, StringComparison.Ordinal))
            {
                continue;
            }

            targets.Add(new ItemSummaryTarget(
                product,
                GetString(item, "productName", product),
                itemId,
                GetString(item, "itemType", string.Empty),
                GetString(item, "title", itemId),
                GetString(item, "createdAt", string.Empty),
                GetString(item, "updatedAt", string.Empty),
                GetInt(item, "eventCount", 0),
                CloneObject(item)));

            if (maxItems > 0 && targets.Count >= maxItems)
            {
                break;
            }
        }

        return targets;
    }

    private Dictionary<string, List<JsonObject>> LoadEventsForTargets(List<ItemSummaryTarget> targets)
    {
        var keys = new HashSet<string>(targets.Select(item => NewTargetKey(item.Product, item.ItemId)), StringComparer.Ordinal);
        var result = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return result;
        }

        var eventsPath = Path.Combine(_settings.GetStoreDirectory(), "events.jsonl");
        if (!File.Exists(eventsPath))
        {
            return result;
        }

        var audioVerbalizationCache = new Dictionary<string, Dictionary<int, JsonObject>>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(eventsPath))
        {
            var text = line.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            JsonObject? entry;
            try
            {
                entry = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            var product = GetString(entry, "product", string.Empty);
            var itemId = GetString(entry, "itemId", string.Empty);
            var key = NewTargetKey(product, itemId);
            if (!keys.Contains(key))
            {
                continue;
            }

            var converted = ConvertReadableEvent(entry, audioVerbalizationCache);
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }

            list.Add(converted);
        }

        return result;
    }

    private JsonObject ConvertReadableEvent(
        JsonObject source,
        Dictionary<string, Dictionary<int, JsonObject>> audioVerbalizationCache)
    {
        var copy = CloneObject(source);
        var product = GetString(copy, "product", string.Empty);
        var itemId = GetString(copy, "itemId", string.Empty);
        var sequence = GetInt(copy, "sequence", -1);
        var content = GetObject(copy, "content") ?? new JsonObject();
        var kind = GetString(content, "kind", string.Empty);

        if (kind.Equals("phone_tokens", StringComparison.OrdinalIgnoreCase)
            && (product.Equals("audio", StringComparison.OrdinalIgnoreCase) || product.Equals("video", StringComparison.OrdinalIgnoreCase)))
        {
            var verbalized = GetAudioVerbalizationTurn(itemId, sequence, audioVerbalizationCache);
            var text = GetString(verbalized, "text", string.Empty);
            if (!string.IsNullOrWhiteSpace(text))
            {
                content["kind"] = "audio_verbalized_text";
                content["value"] = text;
                copy["content"] = content;
            }
        }

        return copy;
    }

    private string BuildSourceText(ItemSummaryTarget target, List<JsonObject> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Product: " + target.ProductName + " (" + target.Product + ")");
        builder.AppendLine("Item ID: " + target.ItemId);
        builder.AppendLine("Item type: " + target.ItemType);
        builder.AppendLine("Title: " + target.Title);
        builder.AppendLine("Created at: " + target.CreatedAt);
        builder.AppendLine("Updated at: " + target.UpdatedAt);
        builder.AppendLine("Event count: " + events.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine("Events:");

        foreach (var item in events.OrderBy(GetEventSequence))
        {
            var time = GetObject(item, "time");
            var actor = GetObject(item, "actor");
            var content = GetObject(item, "content");
            var sequence = GetInt(item, "sequence", 0);
            var start = GetString(time, "absoluteStartAt", string.Empty);
            if (string.IsNullOrEmpty(start))
            {
                var relativeStart = GetNode(time, "relativeStartSec");
                start = relativeStart is null ? string.Empty : ConvertTimelineText(relativeStart);
            }

            builder.Append('[');
            builder.Append(sequence.ToString("0000", CultureInfo.InvariantCulture));
            builder.Append("] ");
            if (!string.IsNullOrWhiteSpace(start))
            {
                builder.Append(start);
                builder.Append(' ');
            }
            var actorLabel = GetString(actor, "label", string.Empty);
            if (!string.IsNullOrWhiteSpace(actorLabel))
            {
                builder.Append(actorLabel);
                builder.Append(": ");
            }
            builder.Append(GetString(content, "value", string.Empty));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private JsonObject NewSummaryPayload(
        ItemSummaryTarget target,
        JsonObject summary,
        string sourceText,
        string inputSignature,
        JsonObject settings)
    {
        var now = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactType"] = "timeline_item_summary",
            ["state"] = "completed",
            ["product"] = target.Product,
            ["productName"] = target.ProductName,
            ["itemId"] = target.ItemId,
            ["itemType"] = target.ItemType,
            ["title"] = target.Title,
            ["briefSummary"] = GetString(summary, "briefSummary", string.Empty),
            ["compressedSummary"] = GetString(summary, "compressedSummary", string.Empty),
            ["summaryStatus"] = GetString(summary, "summaryStatus", "within_limit"),
            ["generationMode"] = GetString(summary, "generationMode", "full"),
            ["chunkCount"] = GetInt(summary, "chunkCount", 0),
            ["rewriteCount"] = GetInt(summary, "rewriteCount", 0),
            ["inputSignature"] = inputSignature,
            ["promptVersion"] = PromptVersion,
            ["model"] = GetString(settings, "model", string.Empty),
            ["provider"] = GetString(settings, "provider", "ollama"),
            ["generatedAt"] = now,
            ["compression"] = new JsonObject
            {
                ["sourceChars"] = sourceText.Length,
                ["compressedCharCount"] = GetInt(summary, "compressedCharCount", GetString(summary, "compressedSummary", string.Empty).Length),
                ["compressedLower"] = GetInt(summary, "compressedLower", 0),
                ["compressedLimit"] = GetInt(summary, "compressedLimit", MaxCompressedSummaryChars),
                ["briefCharCount"] = GetInt(summary, "briefCharCount", GetString(summary, "briefSummary", string.Empty).Length),
                ["briefLower"] = GetInt(summary, "briefLower", 0),
                ["briefLimit"] = GetInt(summary, "briefLimit", MaxBriefSummaryChars),
            },
            ["source"] = new JsonObject
            {
                ["eventCount"] = target.EventCount,
                ["readableCharCount"] = sourceText.Length,
                ["item"] = target.Item.DeepClone(),
            },
            ["notice"] = "この概要はAI生成の派生情報です。正確性が必要な場合は元データを確認してください。",
        };
    }

    private void WriteFailureSummary(
        string summaryPath,
        ItemSummaryTarget target,
        string sourceText,
        string inputSignature,
        JsonObject settings,
        string message)
    {
        WriteJsonFile(summaryPath, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactType"] = "timeline_item_summary",
            ["state"] = "failed",
            ["product"] = target.Product,
            ["productName"] = target.ProductName,
            ["itemId"] = target.ItemId,
            ["itemType"] = target.ItemType,
            ["title"] = target.Title,
            ["inputSignature"] = inputSignature,
            ["promptVersion"] = PromptVersion,
            ["model"] = GetString(settings, "model", string.Empty),
            ["provider"] = GetString(settings, "provider", "ollama"),
            ["generatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["error"] = message,
            ["source"] = new JsonObject
            {
                ["eventCount"] = target.EventCount,
                ["readableCharCount"] = sourceText.Length,
            },
        });
    }

    private void WriteProgress(
        string jobId,
        string startedAt,
        string state,
        string stage,
        string message,
        int total,
        int completed,
        int skipped,
        int failed,
        ItemSummaryTarget? current,
        string error = "")
    {
        var status = NewStatus(jobId, state, stage, message, startedAt);
        status["totalItems"] = total;
        status["completedItems"] = completed;
        status["skippedItems"] = skipped;
        status["failedItems"] = failed;
        if (current is not null)
        {
            status["current"] = new JsonObject
            {
                ["product"] = current.Product,
                ["productName"] = current.ProductName,
                ["itemId"] = current.ItemId,
                ["title"] = current.Title,
            };
        }
        if (!string.IsNullOrWhiteSpace(error))
        {
            status["error"] = error;
        }

        WriteStatus(status);
    }

    private JsonObject GetSummarySettings()
    {
        var settings = _settings.ReadSettings();
        var audio = settings.AudioVerbalization;
        return new JsonObject
        {
            ["enabled"] = true,
            ["provider"] = string.IsNullOrWhiteSpace(audio.Provider) ? "ollama" : audio.Provider,
            ["ollamaBaseUrl"] = string.IsNullOrWhiteSpace(audio.OllamaBaseUrl) ? "http://127.0.0.1:11434" : audio.OllamaBaseUrl,
            ["model"] = string.IsNullOrWhiteSpace(audio.Model) ? settings.Runtime.OllamaModel : audio.Model,
            ["numPredict"] = Math.Max(2048, audio.NumPredict),
        };
    }

    private bool TryReadReusableSummary(string path, string inputSignature)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var payload = ReadJsonFile(path);
            return GetString(payload, "state", string.Empty).Equals("completed", StringComparison.OrdinalIgnoreCase)
                && GetString(payload, "inputSignature", string.Empty).Equals(inputSignature, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string ComputeInputSignature(ItemSummaryTarget target, string sourceText, JsonObject settings)
    {
        var text = PromptVersion
            + "\n"
            + target.Product
            + "\n"
            + target.ItemId
            + "\n"
            + GetString(settings, "provider", "ollama")
            + "\n"
            + GetString(settings, "model", string.Empty)
            + "\n"
            + sourceText;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private void WriteIndex()
    {
        var root = GetSummaryRoot();
        Directory.CreateDirectory(root);
        var rows = new List<JsonObject>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            if (path.Contains(Path.DirectorySeparatorChar + "_jobs" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            if (Path.GetFileName(path).Equals("index.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var payload = ReadJsonFile(path);
                rows.Add(new JsonObject
                {
                    ["product"] = GetString(payload, "product", string.Empty),
                    ["productName"] = GetString(payload, "productName", string.Empty),
                    ["itemId"] = GetString(payload, "itemId", string.Empty),
                    ["itemType"] = GetString(payload, "itemType", string.Empty),
                    ["title"] = GetString(payload, "title", string.Empty),
                    ["state"] = GetString(payload, "state", string.Empty),
                    ["briefSummary"] = GetString(payload, "briefSummary", string.Empty),
                    ["compressedSummary"] = GetString(payload, "compressedSummary", string.Empty),
                    ["summaryStatus"] = GetString(payload, "summaryStatus", string.Empty),
                    ["generatedAt"] = GetString(payload, "generatedAt", string.Empty),
                    ["path"] = path,
                });
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        var indexJsonPath = Path.Combine(root, "index.json");
        WriteJsonFile(indexJsonPath, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactType"] = "timeline_item_summary_index",
            ["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["count"] = rows.Count,
            ["items"] = new JsonArray(rows.Select(row => row.DeepClone()).ToArray()),
        });

        var indexJsonlPath = GetIndexPath();
        using var writer = new StreamWriter(indexJsonlPath, append: false, new UTF8Encoding(false));
        foreach (var row in rows)
        {
            writer.WriteLine(row.ToJsonString(CompactJsonOptions));
        }
    }

    private string GetSummaryRoot()
    {
        var path = Path.Combine(_settings.GetStoreDirectory(), "derived", "item_summaries");
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private string GetJobsRoot()
    {
        var path = Path.Combine(GetSummaryRoot(), "_jobs");
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private string GetIndexPath() => Path.Combine(GetSummaryRoot(), "index.jsonl");

    private string GetSummaryPath(string product, string itemId)
    {
        var path = Path.Combine(GetSummaryRoot(), GetSafeSegment(product), GetSafeSegment(itemId) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private string GetStatusPath(string? jobId)
    {
        var text = ConvertTimelineText(jobId);
        return string.IsNullOrEmpty(text)
            ? Path.Combine(GetJobsRoot(), "latest.json")
            : Path.Combine(GetJobsRoot(), GetSafeSegment(text) + ".json");
    }

    private JsonObject ReadStatus(string? jobId)
    {
        var path = GetStatusPath(jobId);
        if (!File.Exists(path))
        {
            return NewStatus(string.Empty, "none", "none", "素材概要の生成ジョブはまだありません。", string.Empty);
        }

        try
        {
            return ReadJsonFile(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new JsonObject
            {
                ["state"] = "unreadable",
                ["message"] = "素材概要のジョブ状態を読み取れませんでした。",
                ["error"] = ex.Message,
            };
        }
    }

    private void WriteStatus(JsonObject status)
    {
        var jobId = GetString(status, "jobId", string.Empty);
        status["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(jobId))
        {
            WriteJsonFile(GetStatusPath(jobId), status);
        }
        WriteJsonFile(GetStatusPath(string.Empty), status);
    }

    private static JsonObject NewStatus(string jobId, string state, string stage, string message, string startedAt)
        => new()
        {
            ["available"] = true,
            ["jobId"] = jobId,
            ["kind"] = "timeline_item_summary",
            ["state"] = state,
            ["stage"] = stage,
            ["message"] = message,
            ["error"] = string.Empty,
            ["startedAt"] = startedAt,
            ["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["completedAt"] = string.Empty,
            ["totalItems"] = 0,
            ["completedItems"] = 0,
            ["skippedItems"] = 0,
            ["failedItems"] = 0,
        };

    private static bool IsActive(JsonObject? status)
    {
        var state = GetString(status, "state", string.Empty);
        return state.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || state.Equals("running", StringComparison.OrdinalIgnoreCase)
            || state.Equals("canceling", StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, bool> GetRequestedProducts(JsonObject? request)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var product = GetString(request, "product", string.Empty);
        if (!string.IsNullOrEmpty(product) && SupportedProducts.Contains(product))
        {
            result[product] = true;
        }

        foreach (var node in GetArray(request, "products"))
        {
            var value = ConvertTimelineText(node);
            if (!string.IsNullOrEmpty(value) && SupportedProducts.Contains(value))
            {
                result[value] = true;
            }
        }

        return result;
    }

    private static int GetSummaryMaxItems(JsonObject? request, bool applyDefaultBatchLimit)
    {
        if (GetBool(request, "runAll", false))
        {
            return 0;
        }

        var fallback = applyDefaultBatchLimit ? DefaultSummaryBatchItemLimit : 0;
        var requested = GetInt(request, "maxItems", fallback);
        if (requested <= 0)
        {
            return 0;
        }

        return Math.Clamp(requested, 1, MaxSummaryBatchItemLimit);
    }

    private JsonObject? GetAudioVerbalizationTurn(
        string itemId,
        int sequence,
        Dictionary<string, Dictionary<int, JsonObject>> cache)
    {
        var safeItemId = ConvertTimelineText(itemId);
        if (string.IsNullOrEmpty(safeItemId) || sequence < 0)
        {
            return null;
        }

        if (!cache.TryGetValue(safeItemId, out var map))
        {
            map = GetAudioVerbalizationMap(safeItemId);
            cache[safeItemId] = map;
        }

        return map.TryGetValue(sequence, out var turn) ? turn : null;
    }

    private Dictionary<int, JsonObject> GetAudioVerbalizationMap(string itemId)
    {
        var path = Path.Combine(
            _settings.GetStoreDirectory(),
            "audio-verbalizations",
            GetSafeSegment(itemId),
            "audio-verbalization.json");
        var result = new Dictionary<int, JsonObject>();
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var payload = ReadJsonFile(path);
            foreach (var turn in GetArray(payload, "turns").OfType<JsonObject>())
            {
                var sequence = GetInt(turn, "sequence", -1);
                var text = GetString(turn, "text", string.Empty);
                var state = GetString(turn, "state", string.Empty);
                if (sequence >= 0 && !string.IsNullOrWhiteSpace(text) && !state.Equals("unresolved", StringComparison.OrdinalIgnoreCase))
                {
                    result[sequence] = turn;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    private static JsonObject NormalizeSummaryPayload(JsonObject source)
    {
        var compressed = GetString(source, "compressedSummary", string.Empty);
        if (string.IsNullOrWhiteSpace(compressed))
        {
            compressed = GetString(source, "longSummary", GetString(source, "details", GetString(source, "summary", string.Empty)));
        }

        var brief = GetString(source, "briefSummary", string.Empty);
        if (string.IsNullOrWhiteSpace(brief))
        {
            brief = GetString(source, "shortSummary", string.Empty);
        }

        return new JsonObject
        {
            ["briefSummary"] = ConvertTimelineText(brief),
            ["compressedSummary"] = ConvertTimelineText(compressed),
            ["generationMode"] = GetString(source, "generationMode", string.Empty),
            ["chunkCount"] = GetInt(source, "chunkCount", 0),
            ["summaryStatus"] = GetString(source, "summaryStatus", string.Empty),
            ["rewriteCount"] = GetInt(source, "rewriteCount", 0),
            ["compressedCharCount"] = GetInt(source, "compressedCharCount", ConvertTimelineText(compressed).Length),
            ["briefCharCount"] = GetInt(source, "briefCharCount", ConvertTimelineText(brief).Length),
            ["compressedLimit"] = GetInt(source, "compressedLimit", 0),
            ["compressedLower"] = GetInt(source, "compressedLower", 0),
            ["briefLimit"] = GetInt(source, "briefLimit", 0),
            ["briefLower"] = GetInt(source, "briefLower", 0),
        };
    }

    private static JsonArray NormalizeStringArray(JsonArray array, int maxItems)
    {
        var result = new JsonArray();
        foreach (var value in array.Select(ConvertTimelineText).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).Take(maxItems))
        {
            result.Add(value);
        }

        return result;
    }

    private static JsonObject ParseLlmJsonText(string content)
    {
        var text = ConvertTimelineText(content);
        text = Regex.Replace(text, "^```(?:json)?\\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, "\\s*```$", string.Empty, RegexOptions.IgnoreCase).Trim();
        try
        {
            return JsonNode.Parse(text) as JsonObject
                ?? throw new InvalidOperationException("LLM response was not a JSON object.");
        }
        catch (JsonException)
        {
            var repaired = EscapeInvalidJsonBackslashes(text);
            try
            {
                return JsonNode.Parse(repaired) as JsonObject
                    ?? throw new InvalidOperationException("LLM response was not a JSON object.");
            }
            catch (JsonException)
            {
            }

            var loose = TryParseLooseSummaryJson(text);
            if (loose is not null)
            {
                return loose;
            }

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var json = EscapeInvalidJsonBackslashes(text[start..(end + 1)]);
                try
                {
                    return JsonNode.Parse(json) as JsonObject
                        ?? throw new InvalidOperationException("LLM response was not a JSON object.");
                }
                catch (JsonException)
                {
                    loose = TryParseLooseSummaryJson(json);
                    if (loose is not null)
                    {
                        return loose;
                    }
                }
            }

            throw;
        }
    }

    private static JsonObject ParsePlainSummaryText(string content)
    {
        var text = ConvertTimelineText(content);
        text = Regex.Replace(text, "^```(?:text)?\\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, "\\s*```$", string.Empty, RegexOptions.IgnoreCase).Trim();
        var brief = ExtractPlainSection(text, "briefSummary", "compressedSummary");
        var compressed = ExtractPlainSection(text, "compressedSummary", string.Empty);
        if (string.IsNullOrWhiteSpace(brief) && string.IsNullOrWhiteSpace(compressed))
        {
            compressed = text;
        }
        if (string.IsNullOrWhiteSpace(brief))
        {
            brief = compressed;
        }

        return new JsonObject
        {
            ["briefSummary"] = brief,
            ["compressedSummary"] = compressed,
        };
    }

    private static string ExtractPlainSection(string text, string startLabel, string endLabel)
    {
        var startMatch = Regex.Match(
            text,
            "^\\s*" + Regex.Escape(startLabel) + "\\s*:\\s*",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (!startMatch.Success)
        {
            return string.Empty;
        }

        var start = startMatch.Index + startMatch.Length;
        var end = text.Length;
        if (!string.IsNullOrWhiteSpace(endLabel))
        {
            var endMatch = Regex.Match(
                text[start..],
                "^\\s*" + Regex.Escape(endLabel) + "\\s*:\\s*",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (endMatch.Success)
            {
                end = start + endMatch.Index;
            }
        }

        return text[start..end].Trim();
    }

    private static string EscapeInvalidJsonBackslashes(string text)
        => Regex.Replace(text, @"\\(?![""\\/bfnrtu])", @"\\");

    private static JsonObject? TryParseLooseSummaryJson(string text)
    {
        var looseBrief = TryExtractLooseJsonProperty(text, "briefSummary");
        var looseCompressed = TryExtractLooseJsonProperty(text, "compressedSummary");
        if (!string.IsNullOrWhiteSpace(looseBrief) && !string.IsNullOrWhiteSpace(looseCompressed))
        {
            return new JsonObject
            {
                ["briefSummary"] = DecodeLooseJsonString(looseBrief),
                ["compressedSummary"] = DecodeLooseJsonString(looseCompressed),
            };
        }

        var briefMatch = Regex.Match(
            text,
            "\"briefSummary\"\\s*:\\s*\"(?<value>.*?)\"\\s*,\\s*\"compressedSummary\"",
            RegexOptions.Singleline);
        var compressedMatch = Regex.Match(
            text,
            "\"compressedSummary\"\\s*:\\s*\"(?<value>.*)\"\\s*\\}\\s*$",
            RegexOptions.Singleline);
        if (!briefMatch.Success || !compressedMatch.Success)
        {
            return null;
        }

        return new JsonObject
        {
            ["briefSummary"] = DecodeLooseJsonString(briefMatch.Groups["value"].Value),
            ["compressedSummary"] = DecodeLooseJsonString(compressedMatch.Groups["value"].Value),
        };
    }

    private static string? TryExtractLooseJsonProperty(string text, string propertyName)
    {
        var key = "\"" + propertyName + "\"";
        var keyIndex = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
        {
            return null;
        }

        var colonIndex = text.IndexOf(':', keyIndex + key.Length);
        if (colonIndex < 0)
        {
            return null;
        }

        var startQuote = text.IndexOf('"', colonIndex + 1);
        if (startQuote < 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var index = startQuote + 1; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '"' && !IsEscapedQuote(text, index) && IsLooseJsonStringTerminator(text, index + 1))
            {
                return builder.ToString();
            }

            builder.Append(current);
        }

        return null;
    }

    private static bool IsEscapedQuote(string text, int quoteIndex)
    {
        var slashCount = 0;
        for (var index = quoteIndex - 1; index >= 0 && text[index] == '\\'; index--)
        {
            slashCount += 1;
        }

        return slashCount % 2 == 1;
    }

    private static bool IsLooseJsonStringTerminator(string text, int startIndex)
    {
        var index = startIndex;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index += 1;
        }

        return index >= text.Length || text[index] == ',' || text[index] == '}';
    }

    private static string DecodeLooseJsonString(string value)
    {
        var text = EscapeInvalidJsonBackslashes(value);
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + text + "\"")?.Trim() ?? value.Trim();
        }
        catch (JsonException)
        {
            return value
                .Replace("\\r\\n", Environment.NewLine, StringComparison.Ordinal)
                .Replace("\\n", Environment.NewLine, StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Trim();
        }
    }

    private static List<string> SplitText(string text, int maxChars)
    {
        var value = ConvertTimelineText(text);
        if (value.Length <= maxChars)
        {
            return [value];
        }

        var chunks = new List<string>();
        var offset = 0;
        while (offset < value.Length)
        {
            var length = Math.Min(maxChars, value.Length - offset);
            var end = offset + length;
            if (end < value.Length)
            {
                var newline = value.LastIndexOf('\n', end - 1, length);
                if (newline > offset + maxChars / 2)
                {
                    end = newline + 1;
                }
            }

            chunks.Add(value[offset..end]);
            offset = end;
        }

        return chunks;
    }

    private static string BuildSampledSourceText(string sourceText, string reason)
    {
        var value = ConvertTimelineText(sourceText);
        if (value.Length <= MaxSampledSourceChars)
        {
            return value;
        }

        var header = "This source is too large for full item-level summarization. "
            + "Summarize only from the sampled beginning, middle, and end. "
            + "If the sample is insufficient, state that the summary is based on a sample. "
            + "Reason: "
            + ConvertTimelineText(reason);
        var budget = MaxSampledSourceChars - header.Length - 200;
        if (budget < 3000)
        {
            budget = 3000;
        }

        var headLength = budget / 3;
        var middleLength = budget / 3;
        var tailLength = budget - headLength - middleLength;
        var middleStart = Math.Max(0, value.Length / 2 - middleLength / 2);
        var tailStart = Math.Max(0, value.Length - tailLength);

        var builder = new StringBuilder();
        builder.AppendLine(header);
        builder.AppendLine();
        builder.AppendLine("Sample: beginning");
        builder.AppendLine(value[..Math.Min(headLength, value.Length)]);
        builder.AppendLine();
        builder.AppendLine("Sample: middle");
        builder.AppendLine(value.Substring(middleStart, Math.Min(middleLength, value.Length - middleStart)));
        builder.AppendLine();
        builder.AppendLine("Sample: end");
        builder.AppendLine(value[tailStart..]);
        return builder.ToString();
    }

    private static JsonObject NewExtractiveFallbackSummary(
        ItemSummaryTarget target,
        string sourceText,
        SummaryLimits limits,
        string reason)
    {
        var usefulLines = ExtractUsefulSourceLines(sourceText, 12);
        var joined = string.Join(" / ", usefulLines);
        if (string.IsNullOrWhiteSpace(joined))
        {
            joined = target.Title;
        }

        var compressed = "概要生成は縮退モードです。対象は "
            + target.ProductName
            + " の「"
            + target.Title
            + "」。LLM要約が出力上限に達したため、素材本文から主要な断片だけを抽出しています。内容断片: "
            + joined;
        compressed = LimitText(compressed, Math.Max(80, limits.CompressedUpper));

        var briefLimits = NewBriefSummaryLimits(limits.CompressedUpper);
        var brief = LimitText(
            target.Title + " の概要生成は縮退モードです。内容断片: " + joined,
            Math.Max(40, briefLimits.Upper));

        return new JsonObject
        {
            ["briefSummary"] = brief,
            ["compressedSummary"] = compressed,
            ["summaryStatus"] = "extractive_fallback",
            ["generationMode"] = "extractive_fallback",
            ["chunkCount"] = 0,
            ["rewriteCount"] = 0,
            ["compressedCharCount"] = compressed.Length,
            ["briefCharCount"] = brief.Length,
            ["compressedLimit"] = limits.CompressedUpper,
            ["compressedLower"] = limits.CompressedLower,
            ["briefLimit"] = briefLimits.Upper,
            ["briefLower"] = briefLimits.Lower,
            ["fallbackReason"] = ConvertTimelineText(reason),
        };
    }

    private static List<string> ExtractUsefulSourceLines(string sourceText, int maxLines)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in ConvertTimelineText(sourceText).Split('\n'))
        {
            var line = ConvertTimelineText(rawLine);
            if (line.Length < 8
                || line.StartsWith("Product:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Item ID:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Item type:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Created at:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Updated at:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Event count:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("Events:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            line = LimitText(line, 160);
            if (seen.Add(line))
            {
                result.Add(line);
            }
            if (result.Count >= maxLines)
            {
                break;
            }
        }

        return result;
    }

    private static string NewTargetPromptText(ItemSummaryTarget target, string mode)
        => new JsonObject
        {
            ["product"] = target.Product,
            ["productName"] = target.ProductName,
            ["itemId"] = target.ItemId,
            ["itemType"] = target.ItemType,
            ["title"] = target.Title,
            ["mode"] = mode,
        }.ToJsonString(CompactJsonOptions);

    private static SummaryLimits NewSummaryLimits(int sourceChars)
    {
        if (sourceChars <= 0)
        {
            return new SummaryLimits(0, MaxCompressedSummaryChars);
        }

        var upper = Math.Min(MaxCompressedSummaryChars, Math.Max(30, (int)Math.Ceiling(sourceChars / 3.0)));
        var lower = Math.Min(upper, Math.Min(1200, Math.Max(30, (int)Math.Ceiling(sourceChars / 6.0))));
        return new SummaryLimits(lower, upper);
    }

    private static SummaryLimits NewChunkSummaryLimits(int sourceChars)
    {
        var upper = Math.Min(450, Math.Max(120, (int)Math.Ceiling(Math.Max(sourceChars, 1) / 4.0)));
        var lower = Math.Min(upper, Math.Max(60, (int)Math.Ceiling(Math.Max(sourceChars, 1) / 10.0)));
        return new SummaryLimits(lower, upper);
    }

    private static BriefSummaryLimits NewBriefSummaryLimits(int compressedChars)
    {
        if (compressedChars <= 0)
        {
            return new BriefSummaryLimits(0, MaxBriefSummaryChars);
        }

        var upper = Math.Min(MaxBriefSummaryChars, Math.Max(20, (int)Math.Ceiling(compressedChars / 3.0)));
        var lower = Math.Min(upper, Math.Min(300, Math.Max(20, (int)Math.Ceiling(compressedChars / 5.0))));
        return new BriefSummaryLimits(lower, upper);
    }

    private static string NewSummaryPrompt(
        ItemSummaryTarget target,
        string mode,
        string sourceText,
        SummaryLimits limits)
    {
        var briefLimits = NewBriefSummaryLimits(limits.CompressedUpper);
        var modeInstruction = mode.Equals("merge", StringComparison.OrdinalIgnoreCase)
            ? "The source text contains intermediate chunk summaries. Produce one coherent item-level summary. Do not concatenate chunks."
            : "The source text is source material for one Timeline item.";
        return SummaryPrompt
            + Environment.NewLine
            + Environment.NewLine
            + modeInstruction
            + Environment.NewLine
            + Environment.NewLine
            + "Target:"
            + Environment.NewLine
            + NewTargetPromptText(target, mode)
            + Environment.NewLine
            + Environment.NewLine
            + "Length policy:"
            + Environment.NewLine
            + "- compressedSummary must be at most " + limits.CompressedUpper.ToString(CultureInfo.InvariantCulture) + " Japanese characters."
            + Environment.NewLine
            + "- Try to keep compressedSummary at least " + limits.CompressedLower.ToString(CultureInfo.InvariantCulture) + " Japanese characters when the source has enough useful information."
            + Environment.NewLine
            + "- briefSummary must be at most " + briefLimits.Upper.ToString(CultureInfo.InvariantCulture) + " Japanese characters."
            + Environment.NewLine
            + "- Try to keep briefSummary at least " + briefLimits.Lower.ToString(CultureInfo.InvariantCulture) + " Japanese characters when compressedSummary has enough information."
            + Environment.NewLine
            + "- Do not cut text mechanically. Rewrite naturally."
            + Environment.NewLine
            + Environment.NewLine
            + "Source text:"
            + Environment.NewLine
            + sourceText;
    }

    private static string NewPlainSummaryPrompt(
        ItemSummaryTarget target,
        string mode,
        string sourceText,
        SummaryLimits limits)
    {
        var briefLimits = NewBriefSummaryLimits(limits.CompressedUpper);
        return PlainSummaryOutputRule
            + Environment.NewLine
            + Environment.NewLine
            + "compressedSummary は必ず " + limits.CompressedUpper.ToString(CultureInfo.InvariantCulture) + " 文字以内。"
            + Environment.NewLine
            + "briefSummary は必ず " + briefLimits.Upper.ToString(CultureInfo.InvariantCulture) + " 文字以内。"
            + Environment.NewLine
            + "長い列挙はすべて写さず、何を列挙しているかだけを要約してください。"
            + Environment.NewLine
            + "対象: "
            + NewTargetPromptText(target, mode)
            + Environment.NewLine
            + Environment.NewLine
            + "Source text:"
            + Environment.NewLine
            + sourceText;
    }

    private static string NewRewritePrompt(
        JsonObject current,
        string sourceContext,
        SummaryLimits compressedLimits,
        BriefSummaryLimits briefLimits,
        bool compressedTooLong,
        bool compressedTooShort,
        bool briefTooLong,
        bool briefTooShort)
    {
        var isReducing = compressedTooLong || briefTooLong;
        if (isReducing)
        {
            var currentSummary = new JsonObject
            {
                ["briefSummary"] = GetString(current, "briefSummary", string.Empty),
                ["compressedSummary"] = GetString(current, "compressedSummary", string.Empty),
            };
            return "長すぎる概要を短く書き直してください。"
                + Environment.NewLine
                + Environment.NewLine
                + "必須条件:"
                + Environment.NewLine
                + "- compressedSummary は必ず " + compressedLimits.CompressedUpper.ToString(CultureInfo.InvariantCulture) + " 文字以内。"
                + Environment.NewLine
                + "- briefSummary は必ず " + briefLimits.Upper.ToString(CultureInfo.InvariantCulture) + " 文字以内。"
                + Environment.NewLine
                + "- 出力は {\"briefSummary\":\"...\",\"compressedSummary\":\"...\"} のJSONだけ。"
                + Environment.NewLine
                + "- Markdown見出し、番号付きリスト、箇条書きは禁止。"
                + Environment.NewLine
                + "- 具体例の列挙は削り、主題と意味だけを残す。"
                + Environment.NewLine
                + "- compressedSummary は1から4文、または1から4行の話題行にする。"
                + Environment.NewLine
                + "- 文字数を守るためなら、低優先度の細部は捨てる。"
                + Environment.NewLine
                + Environment.NewLine
                + "現在の概要:"
                + Environment.NewLine
                + currentSummary.ToJsonString(FileJsonOptions);
        }

        var mode = compressedTooLong || briefTooLong
            ? "Reduce the summary to fit the limits. Keep only high-value context."
            : "Expand the summary using the source context. Do not invent unsupported facts.";
        var prompt = SummaryRewritePrompt
            + Environment.NewLine
            + Environment.NewLine
            + mode
            + Environment.NewLine
            + "compressedSummary limit: "
            + compressedLimits.CompressedUpper.ToString(CultureInfo.InvariantCulture)
            + " chars, preferred lower target: "
            + compressedLimits.CompressedLower.ToString(CultureInfo.InvariantCulture)
            + " chars."
            + Environment.NewLine
            + "briefSummary limit: "
            + briefLimits.Upper.ToString(CultureInfo.InvariantCulture)
            + " chars, preferred lower target: "
            + briefLimits.Lower.ToString(CultureInfo.InvariantCulture)
            + " chars."
            + Environment.NewLine
            + "Flags:"
            + Environment.NewLine
            + "- compressedTooLong: " + compressedTooLong.ToString(CultureInfo.InvariantCulture)
            + Environment.NewLine
            + "- compressedTooShort: " + compressedTooShort.ToString(CultureInfo.InvariantCulture)
            + Environment.NewLine
            + "- briefTooLong: " + briefTooLong.ToString(CultureInfo.InvariantCulture)
            + Environment.NewLine
            + "- briefTooShort: " + briefTooShort.ToString(CultureInfo.InvariantCulture)
            + Environment.NewLine
            + Environment.NewLine
            + "Current summary:"
            + Environment.NewLine
            + NormalizeSummaryPayload(current).ToJsonString(FileJsonOptions);
        if (!isReducing)
        {
            prompt += Environment.NewLine
                + Environment.NewLine
                + "Source context:"
                + Environment.NewLine
                + sourceContext;
        }

        return prompt;
    }

    private static bool IsTransientOllamaStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            || (int)statusCode >= 500;

    private static string NewOllamaRequestFailedMessage(string baseUrl, JsonObject body, string detail)
    {
        var message = "Ollama request failed. Make sure Ollama is running at "
            + baseUrl
            + " and model '"
            + GetString(body, "model", string.Empty)
            + "' is available.";
        return string.IsNullOrWhiteSpace(detail)
            ? message
            : message + " Detail: " + ConvertTimelineText(detail);
    }

    private static string NewTargetKey(string product, string itemId)
        => ConvertTimelineText(product) + "\n" + ConvertTimelineText(itemId);

    private static int GetEventSequence(JsonObject item)
        => GetInt(item, "sequence", 0);

    private static string NormalizeConfidence(string value)
    {
        var text = ConvertTimelineText(value).ToLowerInvariant();
        return text is "high" or "medium" or "low" ? text : "medium";
    }

    private static string LimitText(string value, int max)
    {
        var text = ConvertTimelineText(value);
        return text.Length <= max ? text : text[..max].TrimEnd();
    }

    private static string NewJobId()
        => $"item-summary-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..("item-summary-".Length + 15 + 1 + 8)];

    private static string GetSafeSegment(string value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return "item";
        }

        var safe = Regex.Replace(text, "[^A-Za-z0-9._-]+", "_").Trim('.', '_', '-');
        if (string.IsNullOrEmpty(safe))
        {
            safe = "item";
        }

        return safe.Length > 120 ? safe[..120] : safe;
    }

    private static JsonObject ReadJsonFile(string path)
        => JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException("JSON file was not an object: " + path);

    private static void WriteJsonFile(string path, JsonObject payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, payload.ToJsonString(FileJsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static JsonObject CloneObject(JsonObject source)
    {
        var clone = source.DeepClone() as JsonObject;
        return clone ?? new JsonObject();
    }

    private static JsonArray CloneArray(JsonArray source)
        => new(source.Select(item => item?.DeepClone()).ToArray());

    private static JsonArray GetArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node as JsonArray ?? [];
    }

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

    private static JsonObject? GetObject(JsonObject? source, string name)
        => GetNode(source, name) as JsonObject;

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertTimelineText(node);
    }

    private static int GetInt(JsonObject? source, string name, int fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        if (node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<int>(out var number))
        {
            return number;
        }

        return int.TryParse(ConvertTimelineText(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool GetBool(JsonObject? source, string name, bool fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        if (node.GetValueKind() == JsonValueKind.True)
        {
            return true;
        }
        if (node.GetValueKind() == JsonValueKind.False)
        {
            return false;
        }

        return ConvertTimelineText(node).ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static string ConvertTimelineText(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonNode node)
        {
            try
            {
                return node.GetValue<object>() switch
                {
                    null => string.Empty,
                    bool flag => flag ? "true" : "false",
                    _ => node.GetValue<object>()?.ToString()?.Trim() ?? string.Empty,
                };
            }
            catch (InvalidOperationException)
            {
                return node.ToJsonString(CompactJsonOptions).Trim();
            }
        }

        return value switch
        {
            bool flag => flag ? "true" : "false",
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }

    private sealed record ItemSummaryTarget(
        string Product,
        string ProductName,
        string ItemId,
        string ItemType,
        string Title,
        string CreatedAt,
        string UpdatedAt,
        int EventCount,
        JsonObject Item);

    private sealed record SummaryLimits(int CompressedLower, int CompressedUpper);

    private sealed record BriefSummaryLimits(int Lower, int Upper);

    private const string SummarySystemPrompt = """
You are a Timeline item summary engine.
Return only valid JSON.
The JSON object must have exactly two keys: briefSummary and compressedSummary.
Do not create arrays.
Do not create additional keys.
Every JSON value must be natural Japanese.
Do not use Chinese prose.
Do not use English prose except unavoidable IDs, paths, product names, or model names.
Do not use markdown headings.
Do not copy exhaustive lists from the source.
Do not invent unsupported facts.
""";

    private const string PlainSummarySystemPrompt = """
You are a Timeline item summary engine.
Return plain text only.
Use the exact two labels requested by the user.
Every value must be natural Japanese.
Do not copy exhaustive lists from the source.
Do not invent unsupported facts.
""";

    private const string PlainSummaryOutputRule = """
次の2ラベルだけを、この順番で出力してください。JSONは禁止です。

briefSummary:
（短い概要）

compressedSummary:
（主概要）
""";

    private const string SummaryPrompt = """
Create a Timeline item summary.

Return only this JSON shape:
{"briefSummary":"...","compressedSummary":"..."}

Do not create any other JSON keys.
Do not create arrays.
Do not return markdown.
Do not use markdown headings or numbered headings.
Do not use numbered lists.

Purpose:
- This summary will be used as compressed memory for a later AI.
- It should help a later AI decide whether the original item needs to be read.
- It is derived information, not primary truth.

Summary shape:
- briefSummary is a short compression of compressedSummary.
- compressedSummary is the main summary.
- compressedSummary may contain multiple topic lines as plain text.
- If the item has multiple topics, write them inside compressedSummary as:
  Topic title: topic summary.
- Keep the data structure simple. Topic sections are text, not JSON arrays.

Rules:
- Do not invent facts that are not supported by the source text.
- If information is uncertain, say it is uncertain.
- Do not force the maximum length when the source is short.
- Do not make the summary too short when the source contains enough useful context.
- If the source contains a long list, summarize what the list represents. Do not copy every item.
- For audio/video, summarize the spoken or visible content, not the file format.
- For ChatGPT/Windows Codex, summarize the thread purpose, decisions, actions, and unresolved points.
""";

    private const string SummaryRewritePrompt = """
Rewrite a Timeline item summary.

Return only this JSON shape:
{"briefSummary":"...","compressedSummary":"..."}

Do not create any other JSON keys.
Do not create arrays.
Do not return markdown.
Do not use markdown headings or numbered headings.

Rules:
- Do not cut text mechanically.
- Rewrite naturally.
- Preserve the important meaning.
- If the summary is too long, merge topics or omit low-priority details.
- If the summary is too short, add grounded context from the source context.
- compressedSummary may use topic lines as plain text:
  Topic title: topic summary.
- Keep the structure as two JSON keys only.
""";
}
