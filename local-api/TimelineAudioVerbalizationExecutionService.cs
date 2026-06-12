using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class TimelineAudioVerbalizationExecutionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineAudioVerbalizationJobRegistry _jobs;
    private readonly IHttpClientFactory _httpClientFactory;

    public TimelineAudioVerbalizationExecutionService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        TimelineAudioVerbalizationJobRegistry jobs,
        IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _operations = operations;
        _jobs = jobs;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<JsonObject> RunSingleAsync(
        string audioItemId,
        string jobId,
        Action<JsonObject, int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = _jobs.MarkActive(jobId, audioItemId);
        var directory = GetAudioVerbalizationDirectory(audioItemId);
        var planPath = Path.Combine(directory, "verbalization-plan.json");
        var resultPath = Path.Combine(directory, "audio-verbalization.json");

        try
        {
            if (!File.Exists(planPath))
            {
                throw new InvalidOperationException("Audio verbalization plan was not found.");
            }
            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException("Audio verbalization result file was not found.");
            }

            var plan = ReadJsonFileRequired(planPath);
            var resultPayload = ReadJsonFileRequired(resultPath);
            var initialStatus = CloneObject(GetObject(resultPayload, "status") ?? new JsonObject());
            initialStatus["jobId"] = jobId;

            _operations.WriteOperationEvent(
                jobId,
                "worker",
                "Timeline",
                "audio_verbalization",
                "running",
                "Audio verbalization worker started.",
                details: new JsonObject
                {
                    ["audioItemId"] = audioItemId,
                    ["planPath"] = planPath,
                    ["resultPath"] = resultPath,
                });

            return await ExecuteAsync(plan, directory, initialStatus, resultPath, resultPayload, progressCallback, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WriteWorkerFailure(audioItemId, jobId, planPath, resultPath, ex.Message);
        }
    }

    private async Task<JsonObject> ExecuteAsync(
        JsonObject plan,
        string directory,
        JsonObject initialStatus,
        string resultPath,
        JsonObject existingResultPayload,
        Action<JsonObject, int, int>? progressCallback,
        CancellationToken cancellationToken)
    {
        var settings = GetObject(plan, "settings") ?? new JsonObject();
        if (!GetBool(settings, "enabled", true))
        {
            return initialStatus;
        }

        var provider = GetString(settings, "provider", "ollama").ToLowerInvariant();
        if (provider != "ollama")
        {
            return initialStatus;
        }

        var resultsDirectory = Path.Combine(directory, "results");
        Directory.CreateDirectory(resultsDirectory);
        var contextDirectory = Path.Combine(directory, "context");
        var chunks = GetArray(plan, "chunks").OfType<JsonObject>().ToList();
        var existingResultChunks = GetReusableResultChunks(existingResultPayload);
        var existingTurns = GetReusableResultTurns(existingResultPayload);
        var resultChunks = new JsonArray();
        var allTurns = new JsonArray();
        var startedAt = DateTimeOffset.Now;

        var status = CloneObject(initialStatus);
        var operationId = GetString(status, "jobId", string.Empty);
        status["state"] = "running";
        status["updatedAt"] = startedAt.ToString("o", CultureInfo.InvariantCulture);
        status["message"] = "Audio verbalization is running.";
        UpdateTiming(status, startedAt, 0, chunks.Count);
        WriteOperation(operationId, "llm", "audio_verbalization", "running", "Audio verbalization execution started.", details: new JsonObject
        {
            ["totalChunks"] = chunks.Count,
            ["resultPath"] = resultPath,
        });

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkId = GetString(chunk, "chunkId", string.Empty);
            if (string.IsNullOrEmpty(chunkId))
            {
                continue;
            }

            var contextPath = Path.Combine(contextDirectory, $"{chunkId}.context.json");
            var summaryPath = Path.Combine(contextDirectory, $"{chunkId}.summary.json");
            var resultChunkPath = Path.Combine(resultsDirectory, $"{chunkId}.result.json");

            if (TryReuseResultChunk(chunk, existingResultChunks, existingTurns, resultChunks, allTurns))
            {
                status["currentChunkId"] = chunkId;
                status["completedChunks"] = resultChunks.Count;
                status["verbalizedTurns"] = GetResolvedTurnCount(allTurns);
                status["unresolvedTurns"] = GetUnresolvedTurnCount(allTurns);
                status["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                status["message"] = "Audio verbalization resumed from a saved chunk.";
                UpdateTiming(status, startedAt, resultChunks.Count, chunks.Count);
                WriteResultPayload(resultPath, status, resultChunks, allTurns);
                progressCallback?.Invoke(CloneObject(status), resultChunks.Count, chunks.Count);
                continue;
            }

            status["currentChunkId"] = chunkId;
            status["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            UpdateTiming(status, startedAt, resultChunks.Count, chunks.Count);
            WriteResultPayload(resultPath, status, resultChunks, allTurns);
            progressCallback?.Invoke(CloneObject(status), resultChunks.Count, chunks.Count);
            WriteOperation(operationId, "llm", "audio_verbalization_chunk", "running", "Audio verbalization chunk started.", details: new JsonObject
            {
                ["chunkId"] = chunkId,
                ["completedChunks"] = resultChunks.Count,
                ["totalChunks"] = chunks.Count,
            });

            var contextPayload = ReadJsonFileRequired(contextPath);
            ApplyPreviousSummary(contextPayload, contextPath);
            var executionChunk = CloneChunkWithoutSilentHallucinations(chunk);
            var executionContext = CloneContextWithoutSilentHallucinations(contextPayload);

            try
            {
                if (GetArray(executionChunk, "turns").Count <= 0)
                {
                    var skippedChunk = NewResultChunk(
                        executionChunk,
                        "completed",
                        0,
                        contextPath,
                        summaryPath,
                        resultChunkPath,
                        "Skipped silent hallucination turns.");
                    WriteJsonFile(summaryPath, new JsonObject
                    {
                        ["schemaVersion"] = 1,
                        ["chunkId"] = chunkId,
                        ["state"] = "completed",
                        ["summary"] = "Skipped silent hallucination turns.",
                        ["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                    });
                    WriteJsonFile(resultChunkPath, new JsonObject
                    {
                        ["schemaVersion"] = 1,
                        ["chunk"] = skippedChunk.DeepClone(),
                        ["turns"] = new JsonArray(),
                    });

                    resultChunks.Add(skippedChunk);
                    status["completedChunks"] = resultChunks.Count;
                    UpdateTiming(status, startedAt, resultChunks.Count, chunks.Count);
                    progressCallback?.Invoke(CloneObject(status), resultChunks.Count, chunks.Count);
                    continue;
                }

                var llmPayload = await InvokeOllamaGenerateJsonAsync(settings, executionContext, cancellationToken);
                var verbalizedTurns = ConvertVerbalizedTurns(executionChunk, llmPayload, executionContext);
                var summary = GetString(llmPayload, "summary", string.Empty);

                var nearbyUserTextCandidateCount = GetArray(executionContext, "nearbyUserTextCandidates").Count;
                var nearbyEvidenceCandidateCount = GetArray(executionContext, "nearbyEvidenceCandidates").Count;
                if (GetResolvedTurnCount(verbalizedTurns) == 0
                    && (nearbyUserTextCandidateCount > 0 || nearbyEvidenceCandidateCount > 0))
                {
                    WriteOperation(operationId, "llm", "audio_verbalization_chunk_retry", "running", "Audio verbalization chunk is retrying with distilled Timeline evidence.", details: new JsonObject
                    {
                        ["chunkId"] = chunkId,
                        ["userTextCandidateCount"] = nearbyUserTextCandidateCount,
                        ["evidenceCandidateCount"] = nearbyEvidenceCandidateCount,
                    });

                    var retryContext = NewRetryContext(executionContext);
                    var retryPayload = await InvokeOllamaGenerateJsonAsync(settings, retryContext, cancellationToken);
                    var retryVerbalizedTurns = ConvertVerbalizedTurns(executionChunk, retryPayload, retryContext);
                    if (GetResolvedTurnCount(retryVerbalizedTurns) > 0)
                    {
                        verbalizedTurns = retryVerbalizedTurns;
                        summary = GetString(retryPayload, "summary", summary);
                        WriteOperation(operationId, "llm", "audio_verbalization_chunk_retry", "completed", "Audio verbalization chunk retry produced readable candidates.", details: new JsonObject
                        {
                            ["chunkId"] = chunkId,
                            ["resolvedTurns"] = GetResolvedTurnCount(verbalizedTurns),
                            ["turnCount"] = verbalizedTurns.Count,
                        });
                    }
                }

                WriteJsonFile(summaryPath, new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["chunkId"] = chunkId,
                    ["state"] = "completed",
                    ["summary"] = summary,
                    ["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                });

                var resultChunk = NewResultChunk(executionChunk, "completed", verbalizedTurns.Count, contextPath, summaryPath, resultChunkPath, summary);
                WriteJsonFile(resultChunkPath, new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["chunk"] = resultChunk.DeepClone(),
                    ["turns"] = CloneArray(verbalizedTurns),
                });

                resultChunks.Add(resultChunk);
                AddRange(allTurns, verbalizedTurns);
                status["completedChunks"] = resultChunks.Count;
                status["verbalizedTurns"] = GetResolvedTurnCount(allTurns);
                status["unresolvedTurns"] = GetUnresolvedTurnCount(allTurns);
                UpdateTiming(status, startedAt, resultChunks.Count, chunks.Count);
                progressCallback?.Invoke(CloneObject(status), resultChunks.Count, chunks.Count);
                WriteOperation(operationId, "llm", "audio_verbalization_chunk", "completed", "Audio verbalization chunk completed.", details: new JsonObject
                {
                    ["chunkId"] = chunkId,
                    ["completedChunks"] = resultChunks.Count,
                    ["totalChunks"] = chunks.Count,
                    ["turnCount"] = verbalizedTurns.Count,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (IsRecoverableLlmError(ex.Message))
                {
                    var chunkTurns = GetArray(executionChunk, "turns");
                    var verbalizedTurns = NewUnresolvedTurns(chunkTurns, "LLM response could not be parsed as strict JSON.");
                    var summary = "Unresolved chunk. LLM response was not valid strict JSON.";
                    WriteJsonFile(summaryPath, new JsonObject
                    {
                        ["schemaVersion"] = 1,
                        ["chunkId"] = chunkId,
                        ["state"] = "unresolved",
                        ["summary"] = summary,
                        ["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                        ["message"] = ex.Message,
                    });

                    var resultChunk = NewResultChunk(executionChunk, "unresolved", verbalizedTurns.Count, contextPath, summaryPath, resultChunkPath, summary);
                    resultChunk["error"] = ex.Message;
                    WriteJsonFile(resultChunkPath, new JsonObject
                    {
                        ["schemaVersion"] = 1,
                        ["chunk"] = resultChunk.DeepClone(),
                        ["turns"] = CloneArray(verbalizedTurns),
                    });

                    resultChunks.Add(resultChunk);
                    AddRange(allTurns, verbalizedTurns);
                    status["completedChunks"] = resultChunks.Count;
                    status["verbalizedTurns"] = GetResolvedTurnCount(allTurns);
                    status["state"] = "running";
                    status["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                    status["message"] = "Audio verbalization chunk was saved as unresolved.";
                    UpdateTiming(status, startedAt, resultChunks.Count, chunks.Count);
                    WriteResultPayload(resultPath, status, resultChunks, allTurns);
                    progressCallback?.Invoke(CloneObject(status), resultChunks.Count, chunks.Count);
                    WriteOperation(operationId, "llm", "audio_verbalization_chunk", "unresolved", "Audio verbalization chunk was saved as unresolved.", details: new JsonObject
                    {
                        ["chunkId"] = chunkId,
                        ["completedChunks"] = resultChunks.Count,
                        ["totalChunks"] = chunks.Count,
                        ["turnCount"] = verbalizedTurns.Count,
                        ["error"] = ex.Message,
                    });
                    continue;
                }

                var failedChunk = NewResultChunk(executionChunk, "failed", GetInt(executionChunk, "turnCount", 0), contextPath, summaryPath, resultChunkPath, string.Empty);
                failedChunk["retryCount"] = 0;
                failedChunk["error"] = ex.Message;
                WriteJsonFile(resultChunkPath, new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["chunk"] = failedChunk.DeepClone(),
                    ["turns"] = new JsonArray(),
                });

                resultChunks.Add(failedChunk);
                status["state"] = "failed";
                status["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                status["message"] = ex.Message;
                UpdateTiming(status, startedAt, resultChunks.Count, chunks.Count);
                WriteResultPayload(resultPath, status, resultChunks, allTurns);
                progressCallback?.Invoke(CloneObject(status), resultChunks.Count, chunks.Count);
                WriteOperation(operationId, "llm", "audio_verbalization_chunk", "failed", ex.Message, details: new JsonObject
                {
                    ["chunkId"] = chunkId,
                    ["completedChunks"] = resultChunks.Count,
                    ["totalChunks"] = chunks.Count,
                });
                return status;
            }
        }

        var unresolvedTurns = GetUnresolvedTurnCount(allTurns);
        status["state"] = unresolvedTurns > 0 ? "needs_review" : "completed";
        status["currentChunkId"] = string.Empty;
        status["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        status["verbalizedTurns"] = GetResolvedTurnCount(allTurns);
        status["unresolvedTurns"] = unresolvedTurns;
        status["message"] = unresolvedTurns > 0
            ? "Audio verbalization completed with unresolved turns."
            : "Audio verbalization completed.";
        status["estimatedRemainingSec"] = 0;
        UpdateTiming(status, startedAt, resultChunks.Count, chunks.Count);
        WriteResultPayload(resultPath, status, resultChunks, allTurns);
        progressCallback?.Invoke(CloneObject(status), resultChunks.Count, chunks.Count);
        WriteOperation(operationId, "llm", "audio_verbalization", GetString(status, "state", string.Empty), GetString(status, "message", string.Empty), durationMs: (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds, details: new JsonObject
        {
            ["completedChunks"] = resultChunks.Count,
            ["totalChunks"] = chunks.Count,
            ["verbalizedTurns"] = GetInt(status, "verbalizedTurns", 0),
            ["unresolvedTurns"] = unresolvedTurns,
            ["resultPath"] = resultPath,
        });
        return status;
    }

    private async Task<JsonObject> InvokeOllamaGenerateJsonAsync(
        JsonObject settings,
        JsonObject context,
        CancellationToken cancellationToken)
    {
        var model = GetString(settings, "model", "qwen3.5:9b");
        var baseUrl = GetString(settings, "ollamaBaseUrl", "http://127.0.0.1:11434");
        var numPredict = GetInt(settings, "numPredict", 4096);
        if (numPredict < 512)
        {
            numPredict = 4096;
        }

        var contextJson = context.ToJsonString(JsonOptions);
        var body = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = AudioVerbalizationPrompt + "\n\nContext JSON:\n" + contextJson,
            ["stream"] = false,
            ["format"] = "json",
            ["think"] = false,
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.0,
                ["num_ctx"] = 8192,
                ["num_predict"] = numPredict,
            },
        };

        var response = await PostOllamaAsync(baseUrl, body, cancellationToken);
        var content = GetString(response, "response", string.Empty);
        if (string.IsNullOrEmpty(content))
        {
            var responseError = GetString(response, "error", string.Empty);
            if (!string.IsNullOrEmpty(responseError))
            {
                throw new InvalidOperationException("Ollama response contained an error: " + responseError);
            }

            throw new InvalidOperationException(
                "Ollama response did not contain message content. done_reason="
                + GetString(response, "done_reason", string.Empty)
                + " thinking_length=0");
        }

        try
        {
            return ParseLlmJsonText(content);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            var partial = ParsePartialLlmJsonText(content, context);
            if (partial is not null)
            {
                return partial;
            }

            return await RepairLlmJsonAsync(settings, context, content, ex.Message, cancellationToken);
        }
    }

    private async Task<JsonObject> RepairLlmJsonAsync(
        JsonObject settings,
        JsonObject context,
        string invalidResponse,
        string parseError,
        CancellationToken cancellationToken)
    {
        var model = GetString(settings, "model", "qwen3.5:9b");
        var baseUrl = GetString(settings, "ollamaBaseUrl", "http://127.0.0.1:11434");
        var numPredict = Math.Max(512, GetInt(settings, "numPredict", 4096));
        var preview = ConvertTimelineText(invalidResponse);
        if (preview.Length > 1200)
        {
            preview = preview[..1200];
        }

        var repairPayload = new JsonObject
        {
            ["context"] = NewRetryContext(context),
            ["invalidResponsePreview"] = preview,
            ["parseError"] = parseError,
        };
        var body = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = RepairPrompt + "\n\nRepair payload JSON:\n" + repairPayload.ToJsonString(JsonOptions),
            ["stream"] = false,
            ["format"] = "json",
            ["think"] = false,
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.0,
                ["num_ctx"] = 8192,
                ["num_predict"] = numPredict,
            },
        };

        var response = await PostOllamaAsync(baseUrl, body, cancellationToken);
        var content = GetString(response, "response", string.Empty);
        if (string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException(parseError);
        }

        try
        {
            return ParseLlmJsonText(content);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            var partial = ParsePartialLlmJsonText(content, context);
            if (partial is not null)
            {
                return partial;
            }

            throw new InvalidOperationException(parseError);
        }
    }

    private async Task<JsonObject> PostOllamaAsync(
        string baseUrl,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(900);
        var url = baseUrl.TrimEnd('/') + "/api/generate";
        Exception? lastException = null;
        var lastFailure = string.Empty;
        const int maxAttempts = 3;
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

    private static JsonArray ConvertVerbalizedTurns(JsonObject chunk, JsonObject llmPayload, JsonObject context)
    {
        var result = new JsonArray();
        var llmTurns = GetArray(llmPayload, "turns").OfType<JsonObject>().ToList();
        foreach (var sourceTurn in GetArray(chunk, "turns").OfType<JsonObject>())
        {
            result.Add(ConvertVerbalizedTurn(sourceTurn, llmTurns, context));
        }

        return result;
    }

    private static JsonObject CloneChunkWithoutSilentHallucinations(JsonObject chunk)
    {
        var clone = CloneObject(chunk);
        var turns = CloneTurnsWithoutSilentHallucinations(GetArray(chunk, "turns"));
        clone["turns"] = turns;
        clone["turnCount"] = turns.Count;
        if (turns.Count > 0)
        {
            var first = turns[0] as JsonObject;
            var last = turns[^1] as JsonObject;
            clone["startSec"] = CloneNode(GetNode(first, "startSec")) ?? JsonValue.Create(0);
            clone["endSec"] = CloneNode(GetNode(last, "endSec")) ?? JsonValue.Create(0);
        }

        return clone;
    }

    private static Dictionary<string, JsonObject> GetReusableResultChunks(JsonObject payload)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var chunk in GetArray(payload, "chunks").OfType<JsonObject>())
        {
            var chunkId = GetString(chunk, "chunkId", string.Empty);
            if (string.IsNullOrEmpty(chunkId))
            {
                continue;
            }

            var state = GetString(chunk, "state", string.Empty).ToLowerInvariant();
            if (state is "completed" or "unresolved")
            {
                result[chunkId] = chunk;
            }
        }

        return result;
    }

    private static Dictionary<string, JsonObject> GetReusableResultTurns(JsonObject payload)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var turn in GetArray(payload, "turns").OfType<JsonObject>())
        {
            var turnId = GetString(turn, "turnId", string.Empty);
            if (!string.IsNullOrEmpty(turnId))
            {
                result[turnId] = turn;
            }
        }

        return result;
    }

    private static bool TryReuseResultChunk(
        JsonObject sourceChunk,
        IReadOnlyDictionary<string, JsonObject> existingResultChunks,
        IReadOnlyDictionary<string, JsonObject> existingTurns,
        JsonArray resultChunks,
        JsonArray allTurns)
    {
        var chunkId = GetString(sourceChunk, "chunkId", string.Empty);
        if (string.IsNullOrEmpty(chunkId) || !existingResultChunks.TryGetValue(chunkId, out var resultChunk))
        {
            return false;
        }

        resultChunks.Add(CloneObject(resultChunk));
        foreach (var sourceTurn in GetArray(sourceChunk, "turns").OfType<JsonObject>())
        {
            var turnId = GetString(sourceTurn, "turnId", string.Empty);
            if (!string.IsNullOrEmpty(turnId) && existingTurns.TryGetValue(turnId, out var existingTurn))
            {
                allTurns.Add(CloneObject(existingTurn));
            }
        }

        return true;
    }

    private static JsonObject CloneContextWithoutSilentHallucinations(JsonObject context)
    {
        var clone = CloneObject(context);
        var turns = CloneTurnsWithoutSilentHallucinations(GetArray(context, "turns"));
        var expectedTurnIds = new JsonArray();
        foreach (var turn in turns.OfType<JsonObject>())
        {
            var turnId = GetString(turn, "turnId", string.Empty);
            if (!string.IsNullOrEmpty(turnId))
            {
                expectedTurnIds.Add(turnId);
            }
        }

        clone["turns"] = turns;
        clone["expectedTurnIds"] = expectedTurnIds;
        clone["expectedTurnCount"] = expectedTurnIds.Count;
        return clone;
    }

    private static JsonArray CloneTurnsWithoutSilentHallucinations(JsonArray sourceTurns)
    {
        var turns = new JsonArray();
        foreach (var turn in sourceTurns.OfType<JsonObject>())
        {
            if (TimelineTranscriptNoiseFilter.IsLikelySilentHallucination(turn))
            {
                continue;
            }

            turns.Add(turn.DeepClone());
        }

        return turns;
    }

    private static JsonObject ConvertVerbalizedTurn(
        JsonObject sourceTurn,
        IReadOnlyList<JsonObject> llmTurns,
        JsonObject context)
    {
        var turnId = GetString(sourceTurn, "turnId", string.Empty);
        var match = llmTurns.FirstOrDefault(turn => GetString(turn, "turnId", string.Empty) == turnId);
        var basis = GetStringArray(GetNode(match, "basis"));
        var uncertainTerms = GetStringArray(GetNode(match, "uncertainTerms"));
        var text = GetString(match, "text", string.Empty);
        var status = GetString(match, "status", "needs_review");

        if (!IsCandidateAcceptable(sourceTurn, text, status, context))
        {
            text = string.Empty;
            status = "unresolved";
            basis = ["candidate_rejected_by_local_validation"];
            uncertainTerms = [];
        }
        else if (GetArray(context, "nearbyUserTextCandidates").Count <= 0
            && status.Equals("candidate", StringComparison.OrdinalIgnoreCase))
        {
            status = "needs_review";
            basis.Add("no_strong_text_hint");
        }

        return new JsonObject
        {
            ["turnId"] = turnId,
            ["index"] = GetInt(sourceTurn, "index", 0),
            ["startSec"] = CloneNode(GetNode(sourceTurn, "startSec")) ?? JsonValue.Create(0),
            ["endSec"] = CloneNode(GetNode(sourceTurn, "endSec")) ?? JsonValue.Create(0),
            ["speaker"] = GetString(sourceTurn, "speaker", string.Empty),
            ["text"] = text,
            ["confidence"] = CloneNode(GetNode(match, "confidence")),
            ["status"] = status,
            ["basis"] = NewStringArray(basis),
            ["uncertainTerms"] = NewStringArray(uncertainTerms),
        };
    }

    private static JsonObject NewUnresolvedTurn(JsonObject sourceTurn, string reason)
        => new()
        {
            ["turnId"] = GetString(sourceTurn, "turnId", string.Empty),
            ["index"] = GetInt(sourceTurn, "index", 0),
            ["startSec"] = CloneNode(GetNode(sourceTurn, "startSec")) ?? JsonValue.Create(0),
            ["endSec"] = CloneNode(GetNode(sourceTurn, "endSec")) ?? JsonValue.Create(0),
            ["speaker"] = GetString(sourceTurn, "speaker", string.Empty),
            ["text"] = string.Empty,
            ["confidence"] = 0,
            ["status"] = "unresolved",
            ["basis"] = NewStringArray([reason]),
            ["uncertainTerms"] = new JsonArray(),
        };

    private static JsonArray NewUnresolvedTurns(JsonArray sourceTurns, string reason)
    {
        var result = new JsonArray();
        foreach (var turn in sourceTurns.OfType<JsonObject>())
        {
            result.Add(NewUnresolvedTurn(turn, reason));
        }

        return result;
    }

    private static JsonObject NewRetryContext(JsonObject context)
    {
        var turns = new JsonArray();
        foreach (var turn in GetArray(context, "turns").OfType<JsonObject>())
        {
            turns.Add(new JsonObject
            {
                ["turnId"] = GetString(turn, "turnId", string.Empty),
                ["index"] = GetInt(turn, "index", 0),
                ["startSec"] = CloneNode(GetNode(turn, "startSec")) ?? JsonValue.Create(0),
                ["endSec"] = CloneNode(GetNode(turn, "endSec")) ?? JsonValue.Create(0),
                ["absoluteStartAt"] = GetString(turn, "absoluteStartAt", string.Empty),
                ["absoluteEndAt"] = GetString(turn, "absoluteEndAt", string.Empty),
                ["speaker"] = GetString(turn, "speaker", string.Empty),
                ["inputKind"] = GetString(turn, "inputKind", string.Empty),
                ["sourceText"] = GetString(turn, "sourceText", string.Empty),
                ["phoneTokens"] = GetString(turn, "phoneTokens", string.Empty),
                ["phoneTextHint"] = GetString(turn, "phoneTextHint", string.Empty),
            });
        }

        var expectedTurnIds = new JsonArray();
        foreach (var turn in turns.OfType<JsonObject>())
        {
            var turnId = GetString(turn, "turnId", string.Empty);
            if (!string.IsNullOrEmpty(turnId))
            {
                expectedTurnIds.Add(turnId);
            }
        }

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["createdAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["retryReason"] = "no_readable_text_from_full_context",
            ["chunkId"] = GetString(context, "chunkId", string.Empty),
            ["expectedTurnIds"] = expectedTurnIds,
            ["expectedTurnCount"] = expectedTurnIds.Count,
            ["language"] = GetString(context, "language", "ja-JP"),
            ["source"] = CloneNode(GetNode(context, "source")),
            ["timeRange"] = CloneNode(GetNode(context, "timeRange")),
            ["rollingContext"] = CloneNode(GetNode(context, "rollingContext")),
            ["nearbyEvidenceCandidates"] = CopyCompactEvidenceCandidates(GetArray(context, "nearbyEvidenceCandidates"), 260),
            ["nearbyUserTextCandidates"] = CopyCompactHints(GetArray(context, "nearbyUserTextCandidates"), 260),
            ["turns"] = turns,
        };
    }

    private static JsonArray CopyCompactEvidenceCandidates(JsonArray candidates, int maxChars)
    {
        var result = new JsonArray();
        foreach (var candidate in candidates.OfType<JsonObject>())
        {
            var contentPreview = ConvertHintText(GetString(candidate, "contentPreview", string.Empty), maxChars);
            if (string.IsNullOrEmpty(contentPreview))
            {
                continue;
            }

            result.Add(new JsonObject
            {
                ["evidenceId"] = GetString(candidate, "evidenceId", string.Empty),
                ["sourceProduct"] = GetString(candidate, "sourceProduct", string.Empty),
                ["sourceProductName"] = GetString(candidate, "sourceProductName", string.Empty),
                ["evidenceType"] = GetString(candidate, "evidenceType", string.Empty),
                ["trustLevel"] = GetString(candidate, "trustLevel", string.Empty),
                ["trustScore"] = CloneNode(GetNode(candidate, "trustScore")),
                ["allowedUse"] = GetString(candidate, "allowedUse", string.Empty),
                ["distanceBucket"] = GetString(candidate, "distanceBucket", string.Empty),
                ["occurredAt"] = GetString(candidate, "occurredAt", string.Empty),
                ["deltaSec"] = CloneNode(GetNode(candidate, "deltaSec")),
                ["actorLabel"] = GetString(candidate, "actorLabel", string.Empty),
                ["contentKind"] = GetString(candidate, "contentKind", string.Empty),
                ["contentPreview"] = contentPreview,
                ["itemId"] = GetString(candidate, "itemId", string.Empty),
            });
        }

        return result;
    }

    private static JsonArray CopyCompactHints(JsonArray hints, int maxChars)
    {
        var result = new JsonArray();
        foreach (var hint in hints.OfType<JsonObject>())
        {
            var contentPreview = ConvertHintText(GetString(hint, "contentPreview", string.Empty), maxChars);
            if (string.IsNullOrEmpty(contentPreview))
            {
                continue;
            }

            result.Add(new JsonObject
            {
                ["product"] = GetString(hint, "product", string.Empty),
                ["productName"] = GetString(hint, "productName", string.Empty),
                ["occurredAt"] = GetString(hint, "occurredAt", string.Empty),
                ["deltaSec"] = CloneNode(GetNode(hint, "deltaSec")),
                ["actorLabel"] = GetString(hint, "actorLabel", string.Empty),
                ["contentPreview"] = contentPreview,
                ["itemId"] = GetString(hint, "itemId", string.Empty),
            });
        }

        return result;
    }

    private static JsonObject NewResultChunk(
        JsonObject chunk,
        string state,
        int turnCount,
        string contextPath,
        string summaryPath,
        string resultChunkPath,
        string summary)
    {
        var result = new JsonObject
        {
            ["chunkId"] = GetString(chunk, "chunkId", string.Empty),
            ["sequence"] = GetInt(chunk, "sequence", 0),
            ["state"] = state,
            ["startSec"] = CloneNode(GetNode(chunk, "startSec")) ?? JsonValue.Create(0),
            ["endSec"] = CloneNode(GetNode(chunk, "endSec")) ?? JsonValue.Create(0),
            ["turnCount"] = turnCount,
            ["contextPath"] = contextPath,
            ["summaryPath"] = summaryPath,
            ["resultPath"] = resultChunkPath,
        };
        if (!string.IsNullOrEmpty(summary))
        {
            result["summary"] = summary;
        }

        return result;
    }

    private static void ApplyPreviousSummary(JsonObject contextPayload, string contextPath)
    {
        var rollingContext = GetObject(contextPayload, "rollingContext");
        if (rollingContext is null)
        {
            return;
        }

        var previousSummaryPath = GetString(rollingContext, "previousSummaryPath", string.Empty);
        if (string.IsNullOrEmpty(previousSummaryPath) || !File.Exists(previousSummaryPath))
        {
            return;
        }

        try
        {
            var previousSummary = GetString(ReadJsonFile(previousSummaryPath), "summary", string.Empty);
            rollingContext["previousSummary"] = previousSummary;
            WriteJsonFile(contextPath, contextPayload);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private JsonObject WriteWorkerFailure(
        string audioItemId,
        string jobId,
        string planPath,
        string resultPath,
        string message)
    {
        var status = new JsonObject
        {
            ["available"] = true,
            ["state"] = "failed",
            ["audioItemId"] = audioItemId,
            ["sourceFileIdentity"] = string.Empty,
            ["language"] = "ja-JP",
            ["model"] = "qwen3.5:9b",
            ["totalTurns"] = 0,
            ["verbalizedTurns"] = 0,
            ["totalChunks"] = 0,
            ["completedChunks"] = 0,
            ["jobId"] = jobId,
            ["currentChunkId"] = string.Empty,
            ["planPath"] = planPath,
            ["resultPath"] = resultPath,
            ["startedAt"] = string.Empty,
            ["elapsedSec"] = 0,
            ["estimatedRemainingSec"] = 0,
            ["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["message"] = message,
        };
        var turns = new JsonArray();
        var chunks = new JsonArray();

        if (File.Exists(resultPath))
        {
            try
            {
                var payload = ReadJsonFileRequired(resultPath);
                status = CloneObject(GetObject(payload, "status") ?? status);
                status["state"] = "failed";
                status["jobId"] = jobId;
                status["updatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                status["message"] = message;
                turns = CloneArray(GetArray(payload, "turns"));
                chunks = CloneArray(GetArray(payload, "chunks"));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        WriteResultPayload(resultPath, status, chunks, turns);
        WriteOperation(jobId, "worker", "audio_verbalization", "failed", message, details: new JsonObject
        {
            ["audioItemId"] = audioItemId,
            ["planPath"] = planPath,
            ["resultPath"] = resultPath,
        });
        return status;
    }

    private static JsonObject ParseLlmJsonText(string text)
    {
        var jsonText = ConvertTimelineText(text);
        if (jsonText.StartsWith("```", StringComparison.Ordinal))
        {
            jsonText = Regex.Replace(jsonText, "^```[a-zA-Z0-9_-]*\\s*", string.Empty);
            jsonText = Regex.Replace(jsonText, "\\s*```$", string.Empty).Trim();
        }

        var startIndex = jsonText.IndexOf('{', StringComparison.Ordinal);
        var endIndex = jsonText.LastIndexOf('}');
        if (startIndex >= 0 && endIndex > startIndex)
        {
            jsonText = jsonText[startIndex..(endIndex + 1)];
        }

        return ParseJsonObjectAllowDuplicateProperties(jsonText)
            ?? throw new InvalidOperationException("LLM response was not a JSON object.");
    }

    private static JsonObject? ParseJsonObjectAllowDuplicateProperties(string jsonText)
    {
        using var document = JsonDocument.Parse(jsonText);
        return ConvertJsonElement(document.RootElement) as JsonObject;
    }

    private static JsonNode? ConvertJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    // LLM responses can contain duplicate keys. Keep the last value, matching common JSON parser behavior.
                    result.Remove(property.Name);
                    result[property.Name] = ConvertJsonElement(property.Value);
                }

                return result;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(ConvertJsonElement(item));
                }

                return array;
            case JsonValueKind.String:
                return JsonValue.Create(element.GetString());
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var longValue))
                {
                    return JsonValue.Create(longValue);
                }
                if (element.TryGetDecimal(out var decimalValue))
                {
                    return JsonValue.Create(decimalValue);
                }

                return JsonValue.Create(element.GetDouble());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }

    private static JsonObject? ParsePartialLlmJsonText(string text, JsonObject context)
    {
        var source = ConvertTimelineText(text);
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var idNode in GetArray(context, "expectedTurnIds"))
        {
            var id = ConvertTimelineText(GetValue(idNode));
            if (!string.IsNullOrEmpty(id))
            {
                allowed.Add(id);
            }
        }
        if (allowed.Count <= 0)
        {
            foreach (var turn in GetArray(context, "turns").OfType<JsonObject>())
            {
                var id = GetString(turn, "turnId", string.Empty);
                if (!string.IsNullOrEmpty(id))
                {
                    allowed.Add(id);
                }
            }
        }

        var summary = GetJsonStringProperty(source, "summary");
        var turns = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(source, "\"turnId\"\\s*:\\s*\"([^\"]+)\""))
        {
            var turnId = match.Groups[1].Value;
            if (string.IsNullOrEmpty(turnId)
                || (allowed.Count > 0 && !allowed.Contains(turnId))
                || seen.Contains(turnId))
            {
                continue;
            }

            var start = source.LastIndexOf('{', match.Index);
            if (start < 0)
            {
                continue;
            }

            var end = FindJsonObjectEnd(source, start);
            if (end <= start)
            {
                continue;
            }

            try
            {
                var turn = ParseJsonObjectAllowDuplicateProperties(source[start..(end + 1)]);
                if (turn is not null)
                {
                    seen.Add(turnId);
                    turns.Add(turn);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
            }
        }

        if (turns.Count <= 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["summary"] = summary,
            ["turns"] = turns,
        };
    }

    private static int FindJsonObjectEnd(string source, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < source.Length; index++)
        {
            var ch = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }
            if (ch == '{')
            {
                depth++;
                continue;
            }
            if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static string GetJsonStringProperty(string source, string name)
    {
        var match = Regex.Match(source, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
        if (!match.Success)
        {
            return string.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<string>("\"" + match.Groups[1].Value + "\"") ?? string.Empty;
        }
        catch (JsonException)
        {
            return match.Groups[1].Value;
        }
    }

    private static bool IsRecoverableLlmError(string message)
    {
        var text = ConvertTimelineText(message);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        if (text.StartsWith("Ollama request failed.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (text.StartsWith("Ollama response contained an error:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsResolvedTurn(JsonObject turn)
    {
        var status = GetString(turn, "status", string.Empty).ToLowerInvariant();
        var text = GetString(turn, "text", string.Empty);
        return status != "unresolved" && IsUsefulText(text);
    }

    private static int GetResolvedTurnCount(JsonArray turns)
        => turns.OfType<JsonObject>().Count(IsResolvedTurn);

    private static int GetUnresolvedTurnCount(JsonArray turns)
        => turns.OfType<JsonObject>().Count(turn => !IsResolvedTurn(turn));

    private static bool IsCandidateAcceptable(JsonObject sourceTurn, string text, string status, JsonObject context)
    {
        text = ConvertTimelineText(text);
        if (!IsUsefulText(text))
        {
            return false;
        }
        if (ConvertTimelineText(status).Equals("unresolved", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var signalLength = Regex.Matches(text, "\\p{L}|\\p{N}").Count;
        if (signalLength <= 1)
        {
            return false;
        }

        var sourceText = GetString(sourceTurn, "sourceText", string.Empty);
        var language = GetString(context, "language", "ja-JP");
        if (language.Equals("ja-JP", StringComparison.OrdinalIgnoreCase) && LooksLikeJapaneseMojibake(text))
        {
            return false;
        }
        if (!string.IsNullOrEmpty(sourceText))
        {
            return !language.Equals("ja-JP", StringComparison.OrdinalIgnoreCase)
                || text.Length <= 10
                || HasJapaneseSignal(text);
        }

        var phoneTextHint = Regex.Replace(GetString(sourceTurn, "phoneTextHint", string.Empty), "[^A-Za-z0-9]+", string.Empty);
        var hasStrongTextHint = GetArray(context, "nearbyUserTextCandidates").Count > 0;
        if (!hasStrongTextHint && phoneTextHint.Length <= 0)
        {
            return false;
        }
        if (!hasStrongTextHint)
        {
            if (phoneTextHint.Length < 12 || signalLength < 4 || text.Length > 180 || SentenceMarkerCount(text) > 2)
            {
                return false;
            }
            if (language.Equals("ja-JP", StringComparison.OrdinalIgnoreCase) && text.Length > 10 && !HasJapaneseSignal(text))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUsefulText(string text)
    {
        text = ConvertTimelineText(text);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        var placeholders = new HashSet<string>(StringComparer.Ordinal)
        {
            "(pause)", "[pause]", "pause", "(silence)", "[silence]", "silence",
            "(unclear)", "[unclear]", "unclear", "(unknown)", "[unknown]", "unknown",
            "(gap)", "[gap]", "gap", "(interval)", "[interval]", "interval", "...", "-",
            "\uFF08\u9593\uFF09", "\uFF08\u7121\u97F3\uFF09", "\u7121\u97F3",
        };
        if (placeholders.Contains(lower) || placeholders.Contains(text))
        {
            return false;
        }
        if (text.Contains("\u4E0D\u660E", StringComparison.Ordinal) && text.Length <= 12)
        {
            return false;
        }
        if (text.Contains("\u4E0D\u660E\u77AD", StringComparison.Ordinal) && text.Length <= 16)
        {
            return false;
        }

        return true;
    }

    private static bool HasJapaneseSignal(string text)
        => Regex.IsMatch(text, "[\\p{IsHiragana}\\p{IsCJKUnifiedIdeographs}]");

    private static bool LooksLikeJapaneseMojibake(string text)
    {
        text = ConvertTimelineText(text);
        if (text.Length < 4)
        {
            return false;
        }

        var markers = Regex.Matches(text, "[縺繧譚荳莨髮霆鬆蠎蛯隧驛邱繝]").Count;
        var signal = Regex.Matches(text, "\\p{L}|\\p{N}").Count;
        return markers >= 3 && signal > 0 && markers / (double)signal >= 0.2;
    }

    private static int SentenceMarkerCount(string text)
        => Regex.Matches(ConvertTimelineText(text), "[\\.\\!\\?\\u3002\\uFF01\\uFF1F]").Count;

    private static void UpdateTiming(JsonObject status, DateTimeOffset startedAt, int completedChunks, int totalChunks)
    {
        var now = DateTimeOffset.Now;
        var elapsedSec = Math.Max(0, (now - startedAt).TotalSeconds);
        var remainingSec = 0.0;
        if (completedChunks > 0 && totalChunks > completedChunks)
        {
            remainingSec = elapsedSec / completedChunks * (totalChunks - completedChunks);
        }

        status["startedAt"] = startedAt.ToString("o", CultureInfo.InvariantCulture);
        status["elapsedSec"] = Math.Round(elapsedSec, 1);
        status["estimatedRemainingSec"] = Math.Round(remainingSec, 1);
    }

    private void WriteOperation(
        string operationId,
        string kind,
        string action,
        string state,
        string message,
        int? durationMs = null,
        JsonNode? details = null)
    {
        _operations.WriteOperationEvent(
            operationId,
            kind,
            "Timeline",
            action,
            state,
            message,
            durationMs: durationMs,
            details: details);
    }

    private static void WriteResultPayload(string resultPath, JsonObject status, JsonArray chunks, JsonArray turns)
    {
        WriteJsonFile(resultPath, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = CloneObject(status),
            ["turns"] = CloneArray(turns),
            ["chunks"] = CloneArray(chunks),
        });
    }

    private static void WriteJsonFile(string path, JsonObject payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = payload.ToJsonString(FileJsonOptions) + Environment.NewLine;
        JsonNode.Parse(json);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        JsonNode.Parse(File.ReadAllText(path));
    }

    private static JsonObject ReadJsonFileRequired(string path)
    {
        var payload = ReadJsonFile(path);
        return payload ?? throw new InvalidOperationException("JSON file could not be read: " + path);
    }

    private static JsonObject? ReadJsonFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    }

    private string GetAudioVerbalizationDirectory(string audioItemId)
    {
        var path = Path.Combine(_settings.GetStoreDirectory(), "audio-verbalizations", GetZipSafeSegment(audioItemId));
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private static string GetZipSafeSegment(string value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return "item";
        }

        var safe = Regex.Replace(text, "[^A-Za-z0-9._-]+", "_").Trim('.', '_', '-');
        if (string.IsNullOrEmpty(safe))
        {
            return "item";
        }

        return safe.Length > 120 ? safe[..120] : safe;
    }

    private static void AddRange(JsonArray target, JsonArray source)
    {
        foreach (var item in source)
        {
            target.Add(item?.DeepClone());
        }
    }

    private static JsonObject CloneObject(JsonObject source)
        => source.DeepClone() as JsonObject ?? new JsonObject();

    private static JsonArray CloneArray(JsonArray source)
    {
        var result = new JsonArray();
        foreach (var item in source)
        {
            result.Add(item?.DeepClone());
        }

        return result;
    }

    private static JsonNode? CloneNode(JsonNode? node)
        => node?.DeepClone();

    private static JsonArray NewStringArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }

    private static List<string> GetStringArray(JsonNode? node)
    {
        var result = new List<string>();
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var value = ConvertTimelineText(GetValue(item));
                if (!string.IsNullOrEmpty(value))
                {
                    result.Add(value);
                }
            }
        }
        else
        {
            var value = ConvertTimelineText(GetValue(node));
            if (!string.IsNullOrEmpty(value))
            {
                result.Add(value);
            }
        }

        return result;
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

    private static JsonArray GetArray(JsonObject? source, string name)
        => GetNode(source, name) as JsonArray ?? new JsonArray();

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return ConvertTimelineText(GetValue(node));
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

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
                if (node.AsValue().TryGetValue<int>(out var intValue))
                {
                    return intValue;
                }
                if (node.AsValue().TryGetValue<double>(out var doubleValue))
                {
                    return (int)Math.Round(doubleValue);
                }
            }

            return int.TryParse(ConvertTimelineText(GetValue(node)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static bool GetBool(JsonObject? source, string name, bool fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.True)
            {
                return true;
            }
            if (node.GetValueKind() == JsonValueKind.False)
            {
                return false;
            }

            return bool.TryParse(ConvertTimelineText(GetValue(node)), out var parsed) ? parsed : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static object? GetValue(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.Number => node.GetValue<double>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => node.ToJsonString(CompactJsonOptions),
        };
    }

    private static string ConvertHintText(string text, int maxChars)
    {
        var value = Regex.Replace(ConvertTimelineText(text), "\\s+", " ").Trim();
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxChars ? value : value[..Math.Max(0, maxChars)] + "...";
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

    private const string AudioVerbalizationPrompt = """
You refine speech transcripts for a personal timeline.
Most current turns contain sourceText from Whisper or faster-whisper. Treat sourceText as the primary speech transcript.
Legacy turns may contain phoneTokens and phoneTextHint instead of sourceText. Use those only as fallback acoustic clues when sourceText is missing.
Use context.language as the target language.
For ja-JP, write natural Japanese in normal kanji/kana text. Do not write romaji-only output. Do not write katakana-only output unless the specific word is normally written in katakana.
For other languages, write ordinary readable text in that language.
Use file name, timestamps, speaker labels, rolling context, nearbyEvidenceCandidates, nearbyTimelineHints, and nearbyUserTextCandidates as context hints.
nearbyEvidenceCandidates is the structured view of surrounding Timeline context. It contains evidenceType, trustLevel, trustScore, allowedUse, and distanceBucket.
Prefer strong evidence over medium evidence, and medium evidence over weak evidence.
Weak evidence, especially speech_derived_transcript from audio or video, must not override readable sourceText. Use weak evidence only as vocabulary or context hints.
Use evidence allowedUse as a hard boundary: do not use weak_context_only or context_hint_only evidence to create words that are not acoustically supported.
nearbyUserTextCandidates may contain a text message created from the same dictated audio shortly after the recording. Treat these candidates as high priority hints.
Do not summarize the conversation, infer intent, or write a topic label.
For each turn, write the most likely words spoken in that turn.
The text field must be an utterance-level transcript refinement, not a summary.
Preserve the original meaning, speaker granularity, and timeline order. Do not merge turns or add content from another turn.
Use nearbyEvidenceCandidates, nearbyTimelineHints, and nearbyUserTextCandidates only for vocabulary, proper nouns, service names, and ambiguity resolution.
Correct obvious ASR errors, spacing, punctuation, and proper nouns only when sourceText or nearby hints support the correction.
If sourceText is readable and there is no strong correction, copy it with only light punctuation or spacing cleanup.
Do not use world knowledge, background facts, explanations, histories, product descriptions, or topic expansion.
Do not invent names, dates, model numbers, places, or facts that are not directly supported by sourceText, phoneTextHint, nearbyEvidenceCandidates, or nearbyUserTextCandidates.
If the only clue is phoneTextHint, keep the output conservative. Prefer needs_review with low confidence, or unresolved when the clue is too weak.
If phoneTextHint is short, mostly noise, or cannot support a readable utterance, return unresolved.
If a nearby user text plausibly matches the audio time range, align its matching words to the listed turns in timeline order.
Include exactly one item for every turnId in context.expectedTurnIds. If context.expectedTurnIds is absent, use the turnIds in context.turns.
Do not output any turnId that is not listed in context.expectedTurnIds.
Do not continue the nearby text beyond the listed turnIds.
If ambiguous, prefer a best-effort candidate with status needs_review and low confidence.
Do not mark a turn unresolved only because nearbyEvidenceCandidates, nearbyTimelineHints, or nearbyUserTextCandidates are empty.
When sourceText is present, use unresolved only when sourceText is empty, unusable, or clearly not speech.
When status is unresolved, text must be an empty string.
Do not use placeholder text such as pause, silence, unclear, unknown, gap, or interval.
Keep each text concise, but do not remove spoken content just to shorten it. Keep basis short. Do not copy long phone-token strings.
Return JSON only. The first character must be { and the last must be }.
Do not include thoughts, reasoning, markdown, role, content, examples, or any key other than summary and turns.
Schema: {"summary":"short processing note","turns":[{"turnId":"turn-000001","text":"refined spoken words","confidence":0.0,"status":"candidate|needs_review|unresolved","basis":["short reason"],"uncertainTerms":["term"]}]}
""";

    private const string RepairPrompt = """
The previous assistant response was not valid JSON.
Return strict JSON only. The first non-whitespace character must be { and the last must be }.
Do not continue, explain, reason, or include markdown.
Do not include thoughts, role, content, examples, or any key other than summary and turns.
Use the original context turns. Include exactly one item for every context.expectedTurnIds entry.
Do not output any turnId that is not listed in context.expectedTurnIds.
If the invalid response cannot be mapped to a turn, use empty text, confidence 0, status unresolved.
Schema:
{
  "summary": "short processing note for the next chunk",
  "turns": [
    {
      "turnId": "turn-000001",
      "text": "candidate readable text",
      "confidence": 0.0,
      "status": "candidate|needs_review|unresolved",
      "basis": ["short reason"],
      "uncertainTerms": ["term"]
    }
  ]
}
""";
}
