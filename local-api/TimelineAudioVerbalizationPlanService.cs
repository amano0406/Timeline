using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class TimelineAudioVerbalizationPlanService
{
    private const string PromptVersion = "audio-transcript-refinement-v1";

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimelineSettingsService _settings;
    private readonly TimelineAudioVerbalizationJobRegistry _jobs;

    public TimelineAudioVerbalizationPlanService(
        TimelineSettingsService settings,
        TimelineAudioVerbalizationJobRegistry jobs)
    {
        _settings = settings;
        _jobs = jobs;
    }

    public TimelineAudioVerbalizationExecutionContext CreateExecutionContext(
        JsonObject detail,
        string sourceId,
        string relativePath,
        string jobId,
        string initialState,
        string initialMessage,
        bool force)
    {
        var status = GetObject(detail, "audioVerbalization") ?? NewUnavailableStatus(detail);
        if (!GetBool(status, "available", false))
        {
            return new TimelineAudioVerbalizationExecutionContext(
                CanRun: false,
                Status: CloneObject(status),
                Plan: null,
                Directory: string.Empty,
                ResultPath: string.Empty,
                SourceId: sourceId,
                RelativePath: relativePath,
                AudioItemId: GetString(status, "audioItemId", string.Empty),
                FileName: GetString(GetObject(detail, "file"), "fileName", string.Empty),
                Reason: "unavailable");
        }

        var currentState = GetString(status, "state", string.Empty).ToLowerInvariant();
        if (!force && currentState is "queued" or "running")
        {
            var currentJobId = GetString(status, "jobId", string.Empty);
            if (_jobs.IsActive(currentJobId))
            {
                return new TimelineAudioVerbalizationExecutionContext(
                    CanRun: false,
                    Status: CloneObject(status),
                    Plan: null,
                    Directory: string.Empty,
                    ResultPath: string.Empty,
                    SourceId: sourceId,
                    RelativePath: relativePath,
                    AudioItemId: GetString(status, "audioItemId", string.Empty),
                    FileName: GetString(GetObject(detail, "file"), "fileName", string.Empty),
                    Reason: "already_active");
            }
        }

        var appSettings = _settings.ReadSettings();
        var verbalizationSettings = ConvertAudioVerbalizationSettings(appSettings.AudioVerbalization);
        var audioItemId = GetString(status, "audioItemId", string.Empty);
        var directory = GetAudioVerbalizationDirectory(audioItemId);
        var contextDirectory = Path.Combine(directory, "context");
        var resultsDirectory = Path.Combine(directory, "results");
        Directory.CreateDirectory(contextDirectory);
        Directory.CreateDirectory(resultsDirectory);

        var planPath = Path.Combine(directory, "verbalization-plan.json");
        var resultPath = Path.Combine(directory, "audio-verbalization.json");
        if (!force)
        {
            var resumeContext = TryCreateResumeExecutionContext(
                detail,
                sourceId,
                relativePath,
                jobId,
                initialMessage,
                status,
                directory,
                planPath,
                resultPath,
                audioItemId);
            if (resumeContext is not null)
            {
                return resumeContext;
            }
        }

        var plan = NewAudioVerbalizationPlan(detail, verbalizationSettings);
        WriteJsonFile(planPath, plan);

        var chunks = GetArray(plan, "chunks");
        var plannedTurnCount = chunks
            .OfType<JsonObject>()
            .Sum(chunk => GetInt(chunk, "turnCount", 0));
        var now = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        var plannedStatus = new JsonObject
        {
            ["available"] = true,
            ["state"] = initialState,
            ["audioItemId"] = audioItemId,
            ["sourceFileIdentity"] = GetString(status, "sourceFileIdentity", string.Empty),
            ["language"] = GetString(verbalizationSettings, "language", "ja-JP"),
            ["model"] = GetString(verbalizationSettings, "model", "qwen3.5:9b"),
            ["signature"] = GetString(plan, "signature", string.Empty),
            ["expectedSignature"] = GetString(plan, "signature", string.Empty),
            ["summarySignature"] = GetString(plan, "summarySignature", string.Empty),
            ["expectedSummarySignature"] = GetString(plan, "summarySignature", string.Empty),
            ["signatureState"] = "current",
            ["promptVersion"] = GetString(plan, "promptVersion", string.Empty),
            ["totalTurns"] = plannedTurnCount,
            ["verbalizedTurns"] = 0,
            ["totalChunks"] = chunks.Count,
            ["completedChunks"] = 0,
            ["jobId"] = jobId,
            ["currentChunkId"] = GetString(chunks.FirstOrDefault() as JsonObject, "chunkId", string.Empty),
            ["planPath"] = planPath,
            ["resultPath"] = resultPath,
            ["startedAt"] = string.Empty,
            ["elapsedSec"] = 0,
            ["estimatedRemainingSec"] = 0,
            ["updatedAt"] = now,
            ["message"] = initialMessage,
        };

        WriteJsonFile(resultPath, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = plannedStatus.DeepClone(),
            ["turns"] = new JsonArray(),
            ["chunks"] = NewInitialResultChunks(chunks, contextDirectory),
        });

        WriteContextFiles(plan, directory);

        return new TimelineAudioVerbalizationExecutionContext(
            CanRun: true,
            Status: plannedStatus,
            Plan: plan,
            Directory: directory,
            ResultPath: resultPath,
            SourceId: sourceId,
            RelativePath: relativePath,
            AudioItemId: audioItemId,
            FileName: GetString(GetObject(detail, "file"), "fileName", string.Empty),
            Reason: string.Empty);
    }

    private TimelineAudioVerbalizationExecutionContext? TryCreateResumeExecutionContext(
        JsonObject detail,
        string sourceId,
        string relativePath,
        string jobId,
        string initialMessage,
        JsonObject currentStatus,
        string directory,
        string planPath,
        string resultPath,
        string audioItemId)
    {
        var currentState = GetString(currentStatus, "state", string.Empty).ToLowerInvariant();
        if (currentState is "completed" or "needs_review" or "not_started" or "source_transcript" or "planned")
        {
            return null;
        }
        if (!File.Exists(planPath) || !File.Exists(resultPath))
        {
            return null;
        }

        var plan = ReadJsonFile(planPath);
        var result = ReadJsonFile(resultPath);
        if (plan is null || result is null)
        {
            return null;
        }

        var resultStatus = GetObject(result, "status") ?? new JsonObject();
        var resultState = GetString(resultStatus, "state", string.Empty).ToLowerInvariant();
        if (resultState is "completed" or "needs_review" or "not_started" or "source_transcript" or "planned")
        {
            return null;
        }

        var planChunks = GetArray(plan, "chunks").OfType<JsonObject>().ToList();
        if (planChunks.Count == 0)
        {
            return null;
        }

        var resultChunks = GetArray(result, "chunks")
            .OfType<JsonObject>()
            .Where(chunk =>
            {
                var state = GetString(chunk, "state", string.Empty).ToLowerInvariant();
                return state is "completed" or "unresolved";
            })
            .ToDictionary(
                chunk => GetString(chunk, "chunkId", string.Empty),
                chunk => chunk,
                StringComparer.Ordinal);

        var nextChunkId = planChunks
            .Select(chunk => GetString(chunk, "chunkId", string.Empty))
            .FirstOrDefault(chunkId => !string.IsNullOrEmpty(chunkId) && !resultChunks.ContainsKey(chunkId))
            ?? string.Empty;

        var now = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        var resumeStatus = CloneObject(resultStatus);
        resumeStatus["available"] = true;
        resumeStatus["state"] = "queued";
        resumeStatus["jobId"] = jobId;
        resumeStatus["currentChunkId"] = nextChunkId;
        resumeStatus["updatedAt"] = now;
        resumeStatus["message"] = string.IsNullOrEmpty(initialMessage)
            ? "音声由来イベントの補正ジョブを再開します。保存済みの進捗を再利用します。"
            : initialMessage;

        WriteJsonFile(resultPath, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = resumeStatus.DeepClone(),
            ["turns"] = CloneArray(GetArray(result, "turns")),
            ["chunks"] = CloneArray(GetArray(result, "chunks")),
        });

        return new TimelineAudioVerbalizationExecutionContext(
            CanRun: true,
            Status: resumeStatus,
            Plan: plan,
            Directory: directory,
            ResultPath: resultPath,
            SourceId: sourceId,
            RelativePath: relativePath,
            AudioItemId: audioItemId,
            FileName: GetString(GetObject(detail, "file"), "fileName", string.Empty),
            Reason: "resume");
    }

    private JsonObject NewAudioVerbalizationPlan(JsonObject detail, JsonObject verbalizationSettings)
    {
        var file = GetObject(detail, "file") ?? new JsonObject();
        var turns = GetArray(detail, "turns");
        var chunks = GetAudioVerbalizationChunkPlan(turns, verbalizationSettings);
        var source = new JsonObject
        {
            ["audioItemId"] = GetString(file, "itemId", string.Empty),
            ["sourceFileIdentity"] = GetString(file, "sourceFileIdentity", string.Empty),
            ["fileName"] = GetString(file, "fileName", string.Empty),
            ["displayPath"] = GetString(file, "displayPath", string.Empty),
            ["durationSec"] = CloneNode(GetNode(file, "durationSec")),
            ["turnCount"] = turns.Count,
        };
        var signatureSet = NewSignatureSet(source, verbalizationSettings, chunks);

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["createdAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["signature"] = GetString(signatureSet, "signature", string.Empty),
            ["summarySignature"] = GetString(signatureSet, "summarySignature", string.Empty),
            ["promptVersion"] = GetString(signatureSet, "promptVersion", string.Empty),
            ["signatureAlgorithm"] = GetString(signatureSet, "algorithm", "sha256"),
            ["source"] = source,
            ["settings"] = CloneObject(verbalizationSettings),
            ["chunks"] = chunks,
        };
    }

    private JsonArray GetAudioVerbalizationChunkPlan(JsonArray turns, JsonObject settings)
    {
        var chunkMaxMinutes = Math.Max(1, GetInt(settings, "chunkMaxMinutes", 10));
        var chunkMaxTurns = Math.Max(1, GetInt(settings, "chunkMaxTurns", 12));
        var chunkMaxSeconds = Math.Max(60, chunkMaxMinutes * 60);
        var sortedTurns = turns
            .OfType<JsonObject>()
            .Where(turn => !TimelineTranscriptNoiseFilter.IsLikelySilentHallucination(turn))
            .OrderBy(turn => GetDouble(turn, "startSec", 0))
            .ToList();

        var chunks = new JsonArray();
        var current = new List<JsonObject>();
        double? currentStart = null;

        foreach (var turn in sortedTurns)
        {
            var startSec = GetDouble(turn, "startSec", 0);
            var endSec = GetDouble(turn, "endSec", startSec);
            if (current.Count == 0)
            {
                currentStart = startSec;
            }

            var prospectiveDuration = endSec - (currentStart ?? startSec);
            var exceedsDuration = current.Count > 0 && prospectiveDuration > chunkMaxSeconds;
            var exceedsTurns = current.Count >= chunkMaxTurns;
            if (exceedsDuration || exceedsTurns)
            {
                chunks.Add(NewAudioVerbalizationChunk(chunks.Count + 1, current));
                current = [];
                currentStart = startSec;
            }

            current.Add(turn);
        }

        if (current.Count > 0)
        {
            chunks.Add(NewAudioVerbalizationChunk(chunks.Count + 1, current));
        }

        return chunks;
    }

    private static JsonObject NewAudioVerbalizationChunk(int index, IReadOnlyList<JsonObject> turns)
    {
        var chunkId = $"chunk-{index:D4}";
        var startSec = 0.0;
        var endSec = 0.0;
        if (turns.Count > 0)
        {
            startSec = GetDouble(turns[0], "startSec", 0);
            endSec = GetDouble(turns[^1], "endSec", startSec);
        }

        var plannedTurns = new JsonArray();
        var tokenEstimate = 0;
        foreach (var turn in turns)
        {
            var turnIndex = GetInt(turn, "index", 0);
            var sourceText = GetReadableTranscriptText(turn);
            var phoneTokens = GetString(turn, "phoneTokens", string.Empty);
            var phoneTextHint = ConvertPhoneTokenHint(phoneTokens);
            var primaryText = !string.IsNullOrEmpty(sourceText) ? sourceText : phoneTokens;
            var inputKind = !string.IsNullOrEmpty(sourceText)
                ? "source_transcript"
                : !string.IsNullOrEmpty(phoneTokens)
                    ? "phone_tokens"
                    : "empty";
            tokenEstimate += Math.Max(1, (int)Math.Ceiling(primaryText.Length / 4.0));
            plannedTurns.Add(new JsonObject
            {
                ["turnId"] = $"turn-{turnIndex:D6}",
                ["index"] = turnIndex,
                ["startSec"] = CloneNode(GetNode(turn, "startSec")) ?? JsonValue.Create(0),
                ["endSec"] = CloneNode(GetNode(turn, "endSec")) ?? JsonValue.Create(0),
                ["absoluteStartAt"] = GetString(turn, "absoluteStartAt", string.Empty),
                ["absoluteEndAt"] = GetString(turn, "absoluteEndAt", string.Empty),
                ["speaker"] = GetString(turn, "speaker", string.Empty),
                ["inputKind"] = inputKind,
                ["sourceText"] = sourceText,
                ["phoneTokens"] = phoneTokens,
                ["phoneTextHint"] = phoneTextHint,
                ["confidence"] = CloneNode(GetNode(turn, "confidence")),
            });
        }

        return new JsonObject
        {
            ["chunkId"] = chunkId,
            ["sequence"] = index,
            ["state"] = "planned",
            ["startSec"] = startSec,
            ["endSec"] = endSec,
            ["durationSec"] = Math.Max(0, endSec - startSec),
            ["turnCount"] = plannedTurns.Count,
            ["inputTokenEstimate"] = tokenEstimate,
            ["turns"] = plannedTurns,
        };
    }

    private void WriteContextFiles(JsonObject plan, string directory)
    {
        var contextDirectory = Path.Combine(directory, "context");
        Directory.CreateDirectory(contextDirectory);
        var chunks = GetArray(plan, "chunks");
        var settings = GetObject(plan, "settings") ?? new JsonObject();
        var hintCandidates = GetHintCandidates(plan, settings);
        JsonObject? previousChunk = null;
        var previousSummaryPath = string.Empty;

        foreach (var chunk in chunks.OfType<JsonObject>())
        {
            var chunkId = GetString(chunk, "chunkId", string.Empty);
            if (string.IsNullOrEmpty(chunkId))
            {
                continue;
            }

            var context = NewContext(plan, chunk, previousChunk, previousSummaryPath, hintCandidates);
            var contextPath = Path.Combine(contextDirectory, $"{chunkId}.context.json");
            var summaryPath = Path.Combine(contextDirectory, $"{chunkId}.summary.json");
            WriteJsonFile(contextPath, context);
            if (!File.Exists(summaryPath))
            {
                WriteJsonFile(summaryPath, new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["chunkId"] = chunkId,
                    ["state"] = "empty",
                    ["summary"] = string.Empty,
                    ["updatedAt"] = string.Empty,
                });
            }

            previousChunk = chunk;
            previousSummaryPath = summaryPath;
        }
    }

    private JsonObject NewContext(
        JsonObject plan,
        JsonObject chunk,
        JsonObject? previousChunk,
        string previousSummaryPath,
        IReadOnlyList<AudioVerbalizationHintCandidate> hintCandidates)
    {
        var source = GetObject(plan, "source") ?? new JsonObject();
        var settings = GetObject(plan, "settings") ?? new JsonObject();
        var chunkId = GetString(chunk, "chunkId", string.Empty);
        var turns = GetArray(chunk, "turns");
        var expectedTurnIds = new JsonArray();
        foreach (var turn in turns.OfType<JsonObject>())
        {
            var turnId = GetString(turn, "turnId", string.Empty);
            if (!string.IsNullOrEmpty(turnId))
            {
                expectedTurnIds.Add(turnId);
            }
        }

        var nearbyHints = GetNearbyHints(plan, chunk, settings, hintCandidates);
        var nearbyUserTextCandidates = GetTextCandidateHints(nearbyHints);
        var nearbyEvidenceCandidates = GetEvidenceCandidateHints(nearbyHints);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["createdAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["chunkId"] = chunkId,
            ["expectedTurnIds"] = expectedTurnIds,
            ["expectedTurnCount"] = expectedTurnIds.Count,
            ["language"] = GetString(settings, "language", "ja-JP"),
            ["model"] = GetString(settings, "model", "qwen3.5:9b"),
            ["source"] = CloneObject(source),
            ["timeRange"] = new JsonObject
            {
                ["startSec"] = CloneNode(GetNode(chunk, "startSec")) ?? JsonValue.Create(0),
                ["endSec"] = CloneNode(GetNode(chunk, "endSec")) ?? JsonValue.Create(0),
                ["durationSec"] = CloneNode(GetNode(chunk, "durationSec")) ?? JsonValue.Create(0),
                ["absoluteStartAt"] = GetChunkAbsoluteTime(chunk, end: false),
                ["absoluteEndAt"] = GetChunkAbsoluteTime(chunk, end: true),
            },
            ["rollingContext"] = new JsonObject
            {
                ["previousChunkId"] = previousChunk is null ? string.Empty : GetString(previousChunk, "chunkId", string.Empty),
                ["previousSummaryPath"] = previousSummaryPath,
                ["previousSummary"] = string.Empty,
                ["nearbyContextMinutes"] = GetInt(settings, "nearbyContextMinutes", 1440),
                ["usePreviousChunkSummary"] = GetBool(settings, "usePreviousChunkSummary", true),
                ["useUnconfirmedVerbalizationAsWeakHint"] = GetBool(settings, "useUnconfirmedVerbalizationAsWeakHint", true),
            },
            ["nearbyTimelineHints"] = nearbyHints,
            ["nearbyEvidenceCandidates"] = nearbyEvidenceCandidates,
            ["nearbyUserTextCandidates"] = nearbyUserTextCandidates,
            ["turns"] = CloneArray(turns),
        };
    }

    private List<AudioVerbalizationHintCandidate> GetHintCandidates(JsonObject plan, JsonObject settings)
    {
        var eventsPath = Path.Combine(_settings.GetStoreDirectory(), "events.jsonl");
        if (!File.Exists(eventsPath))
        {
            return [];
        }

        var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var dateKeys = new HashSet<string>(StringComparer.Ordinal);
        var contextMinutes = Math.Max(1, GetInt(settings, "nearbyContextMinutes", 1440));
        foreach (var chunk in GetArray(plan, "chunks").OfType<JsonObject>())
        {
            if (!TryParseDateTimeOffset(GetChunkAbsoluteTime(chunk, end: false), out var targetStart))
            {
                continue;
            }
            if (!TryParseDateTimeOffset(GetChunkAbsoluteTime(chunk, end: true), out var targetEnd))
            {
                targetEnd = targetStart;
            }

            var windowStart = targetStart.AddMinutes(-1 * contextMinutes);
            var windowEnd = targetEnd.AddMinutes(contextMinutes);
            windows.Add((windowStart, windowEnd));
            for (var day = windowStart.Date; day <= windowEnd.Date; day = day.AddDays(1))
            {
                dateKeys.Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }

        if (windows.Count == 0)
        {
            return [];
        }

        var candidates = new List<AudioVerbalizationHintCandidate>();
        var ordinal = 0;
        foreach (var line in File.ReadLines(eventsPath))
        {
            var text = line.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            if (dateKeys.Count > 0 && !dateKeys.Any(text.Contains))
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

            var time = GetObject(entry, "time");
            var eventStartText = GetString(time, "absoluteStartAt", string.Empty);
            if (!TryParseDateTimeOffset(eventStartText, out var eventStart))
            {
                continue;
            }
            if (!windows.Any(window => eventStart >= window.Start && eventStart <= window.End))
            {
                continue;
            }

            var content = GetObject(entry, "content");
            var contentKind = GetString(content, "kind", string.Empty);
            var contentValue = GetString(content, "value", string.Empty);
            if (string.IsNullOrEmpty(contentValue) || contentKind.Equals("phone_tokens", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.Add(new AudioVerbalizationHintCandidate(eventStart, ordinal, entry));
            ordinal++;
        }

        return candidates;
    }

    private static JsonArray GetNearbyHints(
        JsonObject plan,
        JsonObject chunk,
        JsonObject settings,
        IReadOnlyList<AudioVerbalizationHintCandidate> hintCandidates)
    {
        if (!TryParseDateTimeOffset(GetChunkAbsoluteTime(chunk, end: false), out var targetStart))
        {
            return new JsonArray();
        }
        if (!TryParseDateTimeOffset(GetChunkAbsoluteTime(chunk, end: true), out var targetEnd))
        {
            targetEnd = targetStart;
        }

        var contextMinutes = Math.Max(1, GetInt(settings, "nearbyContextMinutes", 1440));
        var maxEvents = Math.Max(1, GetInt(settings, "nearbyTimelineHintMaxEvents", 24));
        var maxChars = Math.Max(120, GetInt(settings, "nearbyTimelineHintMaxChars", 500));
        var windowStart = targetStart.AddMinutes(-1 * contextMinutes);
        var windowEnd = targetEnd.AddMinutes(contextMinutes);
        var source = GetObject(plan, "source");
        var sourceAudioItemId = GetString(source, "audioItemId", string.Empty);

        var selected = hintCandidates
            .Where(candidate => candidate.EventStart >= windowStart && candidate.EventStart <= windowEnd)
            .Where(candidate =>
            {
                var productId = GetString(candidate.Event, "product", string.Empty);
                var itemId = GetString(candidate.Event, "itemId", string.Empty);
                return !(productId.Equals("audio", StringComparison.OrdinalIgnoreCase)
                    && itemId.Equals(sourceAudioItemId, StringComparison.Ordinal));
            })
            .Select(candidate => new
            {
                Candidate = candidate,
                DistanceSec = Math.Abs((candidate.EventStart - targetStart).TotalSeconds),
            })
            .OrderBy(item => item.DistanceSec)
            .ThenBy(item => item.Candidate.Ordinal)
            .Take(maxEvents);

        var result = new JsonArray();
        foreach (var item in selected)
        {
            result.Add(ConvertHintEvent(item.Candidate.Event, targetStart, maxChars));
        }

        return result;
    }

    private static JsonObject ConvertHintEvent(JsonObject entry, DateTimeOffset targetStart, int maxChars)
    {
        var time = GetObject(entry, "time");
        var actor = GetObject(entry, "actor");
        var content = GetObject(entry, "content");
        var eventStartText = GetString(time, "absoluteStartAt", string.Empty);
        double? deltaSec = null;
        if (TryParseDateTimeOffset(eventStartText, out var eventStart))
        {
            deltaSec = Math.Round((eventStart - targetStart).TotalSeconds, 1);
        }

        var productId = GetString(entry, "product", string.Empty);
        return new JsonObject
        {
            ["product"] = productId,
            ["productName"] = GetStoreProductDisplayName(productId),
            ["eventType"] = GetString(entry, "eventType", string.Empty),
            ["occurredAt"] = eventStartText,
            ["deltaSec"] = deltaSec,
            ["actorType"] = GetString(actor, "type", string.Empty),
            ["actorLabel"] = GetString(actor, "label", string.Empty),
            ["contentKind"] = GetString(content, "kind", string.Empty),
            ["contentPreview"] = ConvertHintText(GetString(content, "value", string.Empty), maxChars),
            ["itemId"] = GetString(entry, "itemId", string.Empty),
        };
    }

    private static JsonArray GetEvidenceCandidateHints(JsonArray hints)
    {
        var candidates = new JsonArray();
        var sequence = 1;
        foreach (var hint in hints.OfType<JsonObject>())
        {
            var contentPreview = GetString(hint, "contentPreview", string.Empty);
            if (string.IsNullOrEmpty(contentPreview))
            {
                continue;
            }

            var classification = ClassifyEvidenceCandidate(hint);
            candidates.Add(new JsonObject
            {
                ["evidenceId"] = $"evidence-{sequence:D4}",
                ["sourceProduct"] = GetString(hint, "product", string.Empty),
                ["sourceProductName"] = GetString(hint, "productName", string.Empty),
                ["evidenceType"] = GetString(classification, "evidenceType", "other"),
                ["trustLevel"] = GetString(classification, "trustLevel", "weak"),
                ["trustScore"] = CloneNode(GetNode(classification, "trustScore")),
                ["allowedUse"] = GetString(classification, "allowedUse", "context_only"),
                ["distanceBucket"] = GetString(classification, "distanceBucket", "context_window"),
                ["basis"] = CloneNode(GetNode(classification, "basis")),
                ["eventType"] = GetString(hint, "eventType", string.Empty),
                ["occurredAt"] = GetString(hint, "occurredAt", string.Empty),
                ["deltaSec"] = CloneNode(GetNode(hint, "deltaSec")),
                ["actorType"] = GetString(hint, "actorType", string.Empty),
                ["actorLabel"] = GetString(hint, "actorLabel", string.Empty),
                ["contentKind"] = GetString(hint, "contentKind", string.Empty),
                ["contentPreview"] = contentPreview,
                ["itemId"] = GetString(hint, "itemId", string.Empty),
            });
            sequence++;
        }

        return candidates;
    }

    private static JsonObject ClassifyEvidenceCandidate(JsonObject hint)
    {
        var product = GetString(hint, "product", string.Empty).ToLowerInvariant();
        var contentKind = GetString(hint, "contentKind", string.Empty).ToLowerInvariant();
        var eventType = GetString(hint, "eventType", string.Empty).ToLowerInvariant();
        var actorLabel = GetString(hint, "actorLabel", string.Empty).ToLowerInvariant();
        var evidenceType = GetEvidenceType(product, contentKind, eventType, actorLabel);
        var score = GetEvidenceBaseScore(product, contentKind, eventType, actorLabel) + GetDistanceScoreModifier(hint);
        score = Math.Clamp(score, 0.05, 0.98);
        var trustLevel = score >= 0.75
            ? "strong"
            : score >= 0.5
                ? "medium"
                : "weak";

        return new JsonObject
        {
            ["evidenceType"] = evidenceType,
            ["trustLevel"] = trustLevel,
            ["trustScore"] = Math.Round(score, 2),
            ["allowedUse"] = GetAllowedEvidenceUse(evidenceType, trustLevel),
            ["distanceBucket"] = GetDistanceBucket(hint),
            ["basis"] = NewStringArray(GetEvidenceBasis(product, contentKind, eventType, actorLabel, evidenceType, trustLevel)),
        };
    }

    private static string GetEvidenceType(string product, string contentKind, string eventType, string actorLabel)
    {
        if (product is "audio")
        {
            return "speech_derived_transcript";
        }
        if (product is "video" && (contentKind is "transcript_text" || eventType.Contains("audio", StringComparison.Ordinal)))
        {
            return "speech_derived_transcript";
        }
        if (product is "video")
        {
            return "video_observation";
        }
        if (product is "image" && (contentKind.Contains("ocr", StringComparison.Ordinal) || contentKind.Contains("text", StringComparison.Ordinal)))
        {
            return "image_ocr";
        }
        if (product is "image")
        {
            return "image_observation";
        }
        if (product is "pc")
        {
            return "pc_activity";
        }
        if ((product is "chatgpt" or "windows-codex") && actorLabel == "user")
        {
            return "user_authored_text";
        }
        if (product is "chatgpt" or "windows-codex")
        {
            return "conversation_text";
        }
        if (contentKind is "text" or "markdown")
        {
            return "timeline_text";
        }

        return "other";
    }

    private static double GetEvidenceBaseScore(string product, string contentKind, string eventType, string actorLabel)
    {
        var evidenceType = GetEvidenceType(product, contentKind, eventType, actorLabel);
        return evidenceType switch
        {
            "user_authored_text" => 0.88,
            "timeline_text" => 0.78,
            "pc_activity" => 0.74,
            "image_ocr" => 0.68,
            "image_observation" => 0.58,
            "conversation_text" => actorLabel == "assistant" ? 0.52 : 0.46,
            "video_observation" => 0.46,
            "speech_derived_transcript" => 0.36,
            _ => 0.42,
        };
    }

    private static string GetAllowedEvidenceUse(string evidenceType, string trustLevel)
        => evidenceType switch
        {
            "user_authored_text" => "proper_noun_and_reference_resolution",
            "timeline_text" => "proper_noun_and_reference_resolution",
            "pc_activity" => "reference_resolution",
            "image_ocr" => "vocabulary_and_reference_hint",
            "image_observation" => "context_hint_only",
            "conversation_text" => trustLevel == "medium" ? "vocabulary_and_reference_hint" : "weak_context_only",
            "speech_derived_transcript" => "weak_context_only",
            _ => "context_hint_only",
        };

    private static IEnumerable<string> GetEvidenceBasis(
        string product,
        string contentKind,
        string eventType,
        string actorLabel,
        string evidenceType,
        string trustLevel)
    {
        yield return $"source_product:{product}";
        yield return $"evidence_type:{evidenceType}";
        yield return $"trust:{trustLevel}";
        if (!string.IsNullOrEmpty(contentKind))
        {
            yield return $"content_kind:{contentKind}";
        }
        if (!string.IsNullOrEmpty(eventType))
        {
            yield return $"event_type:{eventType}";
        }
        if (!string.IsNullOrEmpty(actorLabel))
        {
            yield return $"actor:{actorLabel}";
        }
    }

    private static double GetDistanceScoreModifier(JsonObject hint)
    {
        var distanceSec = GetDistanceSec(hint);
        if (distanceSec is null)
        {
            return -0.08;
        }
        if (distanceSec <= 15 * 60)
        {
            return 0.06;
        }
        if (distanceSec <= 2 * 60 * 60)
        {
            return 0;
        }

        return -0.06;
    }

    private static string GetDistanceBucket(JsonObject hint)
    {
        var distanceSec = GetDistanceSec(hint);
        if (distanceSec is null)
        {
            return "unknown";
        }
        if (distanceSec <= 5 * 60)
        {
            return "same_moment";
        }
        if (distanceSec <= 60 * 60)
        {
            return "nearby_hour";
        }
        if (distanceSec <= 6 * 60 * 60)
        {
            return "same_day_near";
        }

        return "context_window";
    }

    private static double? GetDistanceSec(JsonObject hint)
    {
        var node = GetNode(hint, "deltaSec");
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        return Math.Abs(GetDouble(hint, "deltaSec", 0));
    }

    private static JsonArray GetTextCandidateHints(JsonArray hints)
    {
        var candidates = new JsonArray();
        foreach (var hint in hints.OfType<JsonObject>())
        {
            var contentKind = GetString(hint, "contentKind", string.Empty).ToLowerInvariant();
            if (!string.IsNullOrEmpty(contentKind) && contentKind is not ("text" or "markdown"))
            {
                continue;
            }

            var actorLabel = GetString(hint, "actorLabel", string.Empty).ToLowerInvariant();
            if (actorLabel != "user")
            {
                continue;
            }

            var text = GetString(hint, "contentPreview", string.Empty);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var lower = text.ToLowerInvariant();
            if (lower.StartsWith("<environment_context", StringComparison.Ordinal)
                || lower.StartsWith("<turn_aborted", StringComparison.Ordinal)
                || lower.StartsWith("<tool", StringComparison.Ordinal)
                || lower.StartsWith("the user interrupted", StringComparison.Ordinal)
                || lower.StartsWith("selected text:", StringComparison.Ordinal))
            {
                continue;
            }

            candidates.Add(new JsonObject
            {
                ["product"] = GetString(hint, "product", string.Empty),
                ["productName"] = GetString(hint, "productName", string.Empty),
                ["occurredAt"] = GetString(hint, "occurredAt", string.Empty),
                ["deltaSec"] = CloneNode(GetNode(hint, "deltaSec")),
                ["actorLabel"] = GetString(hint, "actorLabel", string.Empty),
                ["contentPreview"] = text,
                ["itemId"] = GetString(hint, "itemId", string.Empty),
            });
        }

        return candidates;
    }

    private static JsonArray NewInitialResultChunks(JsonArray chunks, string contextDirectory)
    {
        var result = new JsonArray();
        foreach (var chunk in chunks.OfType<JsonObject>())
        {
            var chunkId = GetString(chunk, "chunkId", string.Empty);
            result.Add(new JsonObject
            {
                ["chunkId"] = chunkId,
                ["sequence"] = GetInt(chunk, "sequence", 0),
                ["state"] = GetString(chunk, "state", "planned"),
                ["startSec"] = CloneNode(GetNode(chunk, "startSec")) ?? JsonValue.Create(0),
                ["endSec"] = CloneNode(GetNode(chunk, "endSec")) ?? JsonValue.Create(0),
                ["turnCount"] = GetInt(chunk, "turnCount", 0),
                ["contextPath"] = string.IsNullOrEmpty(chunkId) ? string.Empty : Path.Combine(contextDirectory, $"{chunkId}.context.json"),
                ["summaryPath"] = string.IsNullOrEmpty(chunkId) ? string.Empty : Path.Combine(contextDirectory, $"{chunkId}.summary.json"),
            });
        }

        return result;
    }

    private static JsonObject NewSignatureSet(JsonObject source, JsonObject settings, JsonArray chunks)
    {
        var summaryPayload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["kind"] = "audio-verbalization-summary",
            ["source"] = NewSignatureSource(source),
            ["settings"] = NewSignatureSettings(settings),
        };
        var fullPayload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["kind"] = "audio-verbalization",
            ["source"] = NewSignatureSource(source),
            ["settings"] = NewSignatureSettings(settings),
            ["chunks"] = NewSignatureChunks(chunks),
        };

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["algorithm"] = "sha256",
            ["promptVersion"] = PromptVersion,
            ["signature"] = Sha256Hex(fullPayload.ToJsonString(CompactJsonOptions)),
            ["summarySignature"] = Sha256Hex(summaryPayload.ToJsonString(CompactJsonOptions)),
        };
    }

    private static JsonObject NewSignatureSettings(JsonObject settings)
        => new()
        {
            ["provider"] = GetString(settings, "provider", "ollama").ToLowerInvariant(),
            ["language"] = GetString(settings, "language", "ja-JP"),
            ["model"] = GetString(settings, "model", "qwen3.5:9b"),
            ["chunkMaxMinutes"] = GetInt(settings, "chunkMaxMinutes", 10),
            ["chunkMaxTurns"] = GetInt(settings, "chunkMaxTurns", 12),
            ["numPredict"] = GetInt(settings, "numPredict", 4096),
            ["promptVersion"] = PromptVersion,
        };

    private static JsonObject NewSignatureSource(JsonObject? source)
        => new()
        {
            ["audioItemId"] = GetString(source, "audioItemId", string.Empty),
            ["sourceFileIdentity"] = GetString(source, "sourceFileIdentity", string.Empty),
            ["durationSec"] = CloneNode(GetNode(source, "durationSec")),
            ["turnCount"] = GetInt(source, "turnCount", 0),
        };

    private static JsonArray NewSignatureChunks(JsonArray chunks)
    {
        var result = new JsonArray();
        foreach (var chunk in chunks.OfType<JsonObject>())
        {
            var turns = new JsonArray();
            foreach (var turn in GetArray(chunk, "turns").OfType<JsonObject>())
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
                });
            }

            result.Add(new JsonObject
            {
                ["chunkId"] = GetString(chunk, "chunkId", string.Empty),
                ["sequence"] = GetInt(chunk, "sequence", 0),
                ["startSec"] = CloneNode(GetNode(chunk, "startSec")) ?? JsonValue.Create(0),
                ["endSec"] = CloneNode(GetNode(chunk, "endSec")) ?? JsonValue.Create(0),
                ["turns"] = turns,
            });
        }

        return result;
    }

    private static JsonObject ConvertAudioVerbalizationSettings(TimelineAudioVerbalizationSettingsResponse settings)
        => new()
        {
            ["enabled"] = settings.Enabled,
            ["provider"] = string.IsNullOrWhiteSpace(settings.Provider) ? "ollama" : settings.Provider.Trim(),
            ["ollamaBaseUrl"] = string.IsNullOrWhiteSpace(settings.OllamaBaseUrl) ? "http://127.0.0.1:11434" : settings.OllamaBaseUrl.Trim(),
            ["model"] = string.IsNullOrWhiteSpace(settings.Model) ? "qwen3.5:9b" : settings.Model.Trim(),
            ["fastModel"] = string.IsNullOrWhiteSpace(settings.FastModel) ? "qwen3.5:4b" : settings.FastModel.Trim(),
            ["language"] = string.IsNullOrWhiteSpace(settings.Language) ? "ja-JP" : settings.Language.Trim(),
            ["chunkMinMinutes"] = settings.ChunkMinMinutes <= 0 ? 5 : settings.ChunkMinMinutes,
            ["chunkMaxMinutes"] = settings.ChunkMaxMinutes <= 0 ? 10 : settings.ChunkMaxMinutes,
            ["chunkMaxTurns"] = settings.ChunkMaxTurns <= 0 ? 12 : settings.ChunkMaxTurns,
            ["numPredict"] = settings.NumPredict <= 0 ? 2048 : settings.NumPredict,
            ["nearbyContextMinutes"] = settings.NearbyContextMinutes <= 0 ? 1440 : settings.NearbyContextMinutes,
            ["nearbyTimelineHintMaxEvents"] = settings.NearbyTimelineHintMaxEvents <= 0 ? 24 : settings.NearbyTimelineHintMaxEvents,
            ["nearbyTimelineHintMaxChars"] = settings.NearbyTimelineHintMaxChars <= 0 ? 500 : settings.NearbyTimelineHintMaxChars,
            ["maxConcurrentJobs"] = settings.MaxConcurrentJobs <= 0 ? 1 : settings.MaxConcurrentJobs,
            ["autoRun"] = settings.AutoRun,
            ["usePreviousChunkSummary"] = settings.UsePreviousChunkSummary,
            ["useUnconfirmedVerbalizationAsWeakHint"] = settings.UseUnconfirmedVerbalizationAsWeakHint,
        };

    private string GetAudioVerbalizationDirectory(string audioItemId)
    {
        var safeItemId = GetZipSafeSegment(audioItemId);
        if (string.IsNullOrEmpty(safeItemId))
        {
            safeItemId = "unknown";
        }

        var path = Path.Combine(_settings.GetStoreDirectory(), "audio-verbalizations", safeItemId);
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private static JsonObject NewUnavailableStatus(JsonObject detail)
    {
        var file = GetObject(detail, "file");
        return new JsonObject
        {
            ["available"] = false,
            ["state"] = "unavailable",
            ["audioItemId"] = GetString(file, "itemId", string.Empty),
            ["sourceFileIdentity"] = GetString(file, "sourceFileIdentity", string.Empty),
            ["language"] = "ja-JP",
            ["model"] = "qwen3.5:9b",
            ["totalTurns"] = GetArray(detail, "turns").Count,
            ["verbalizedTurns"] = 0,
            ["unresolvedTurns"] = 0,
            ["totalChunks"] = 0,
            ["completedChunks"] = 0,
            ["jobId"] = string.Empty,
            ["currentChunkId"] = string.Empty,
            ["planPath"] = string.Empty,
            ["resultPath"] = string.Empty,
            ["message"] = GetString(detail, "message", "Audio verbalization status was not available."),
        };
    }

    private static string GetChunkAbsoluteTime(JsonObject chunk, bool end)
    {
        var turns = GetArray(chunk, "turns").OfType<JsonObject>().ToList();
        if (turns.Count == 0)
        {
            return string.Empty;
        }

        if (end)
        {
            for (var index = turns.Count - 1; index >= 0; index--)
            {
                var value = GetString(turns[index], "absoluteEndAt", string.Empty);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            for (var index = turns.Count - 1; index >= 0; index--)
            {
                var value = GetString(turns[index], "absoluteStartAt", string.Empty);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return string.Empty;
        }

        foreach (var turn in turns)
        {
            var value = GetString(turn, "absoluteStartAt", string.Empty);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        foreach (var turn in turns)
        {
            var value = GetString(turn, "absoluteEndAt", string.Empty);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        return string.Empty;
    }

    private static string GetReadableTranscriptText(JsonObject turn)
    {
        if (TimelineTranscriptNoiseFilter.IsLikelySilentHallucination(turn))
        {
            return string.Empty;
        }

        foreach (var name in new[] { "text", "transcriptText", "readableText" })
        {
            var value = GetString(turn, name, string.Empty);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ConvertPhoneTokenHint(string phoneTokens)
    {
        var text = ConvertTimelineText(phoneTokens);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        text = text.Replace('\u2581', ' ');
        foreach (var replacement in PhoneTokenReplacements)
        {
            text = text.Replace(replacement.Code, replacement.Text, StringComparison.Ordinal);
        }

        text = System.Text.RegularExpressions.Regex.Replace(text, "[^A-Za-z0-9]+", " ").Trim();
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var compact = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", string.Empty);
        if (compact.Length < 4)
        {
            return string.Empty;
        }
        return compact.Length > 1600 ? compact[..1600] : compact;
    }

    private static string ConvertHintText(string text, int maxChars)
    {
        var value = System.Text.RegularExpressions.Regex.Replace(ConvertTimelineText(text), "\\s+", " ").Trim();
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value.Length <= maxChars ? value : value[..Math.Max(0, maxChars)] + "...";
    }

    private static bool TryParseDateTimeOffset(string value, out DateTimeOffset result)
        => DateTimeOffset.TryParse(ConvertTimelineText(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out result)
            || DateTimeOffset.TryParse(ConvertTimelineText(value), out result);

    private static string GetStoreProductDisplayName(string productId)
        => ConvertTimelineText(productId).ToLowerInvariant() switch
        {
            "audio" => "TimelineForAudio",
            "image" => "TimelineForImage",
            "video" => "TimelineForVideo",
            "chatgpt" => "TimelineForChatGPT",
            "windows-codex" => "TimelineForWindowsCodex",
            "pc" => "TimelineForPcInfo",
            _ => ConvertTimelineText(productId),
        };

    private static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void WriteJsonFile(string path, JsonObject payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = payload.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + Environment.NewLine;
        JsonNode.Parse(json);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        JsonNode.Parse(File.ReadAllText(path));
    }

    private static JsonObject? ReadJsonFile(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetZipSafeSegment(string value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return "item";
        }

        var safe = System.Text.RegularExpressions.Regex.Replace(text, "[^A-Za-z0-9._-]+", "_").Trim('.', '_', '-');
        if (string.IsNullOrEmpty(safe))
        {
            return "item";
        }

        return safe.Length > 120 ? safe[..120] : safe;
    }

    private static JsonObject CloneObject(JsonObject source)
        => source.DeepClone() as JsonObject ?? new JsonObject();

    private static JsonArray CloneArray(JsonArray source)
    {
        var result = new JsonArray();
        foreach (var item in source)
        {
            result.Add(CloneNode(item));
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
            if (node.GetValueKind() == JsonValueKind.String)
            {
                return ConvertTimelineText(node.GetValue<string>());
            }

            return ConvertTimelineText(node.GetValue<object>());
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

            return int.TryParse(ConvertTimelineText(node.GetValue<object>()), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static double GetDouble(JsonObject? source, string name, double fallback)
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
                return node.AsValue().TryGetValue<double>(out var doubleValue) ? doubleValue : fallback;
            }

            return double.TryParse(ConvertTimelineText(node.GetValue<object>()), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
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

            return bool.TryParse(ConvertTimelineText(node.GetValue<object>()), out var parsed) ? parsed : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
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

    private sealed record AudioVerbalizationHintCandidate(DateTimeOffset EventStart, int Ordinal, JsonObject Event);

    private sealed record PhoneTokenReplacement(string Code, string Text);

    private static readonly PhoneTokenReplacement[] PhoneTokenReplacements =
    [
        new("\u0283", "sh"),
        new("\u026F", "u"),
        new("\u0255", "sh"),
        new("\u0291", "j"),
        new("\u0292", "j"),
        new("\u02A6", "ts"),
        new("\u02A7", "ch"),
        new("\u027E", "r"),
        new("\u014B", "ng"),
        new("\u0254", "o"),
        new("\u025B", "e"),
        new("\u0259", "a"),
        new("\u0261", "g"),
    ];
}

public sealed record TimelineAudioVerbalizationExecutionContext(
    bool CanRun,
    JsonObject Status,
    JsonObject? Plan,
    string Directory,
    string ResultPath,
    string SourceId,
    string RelativePath,
    string AudioItemId,
    string FileName,
    string Reason);
