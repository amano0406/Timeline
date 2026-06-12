using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class TimelineLlmInputPreviewService
{
    private readonly TimelineSettingsService _settings;
    private readonly TimelineStoreService _store;

    public TimelineLlmInputPreviewService(
        TimelineSettingsService settings,
        TimelineStoreService store)
    {
        _settings = settings;
        _store = store;
    }

    public JsonObject GetPreview(
        string? purpose,
        string? product,
        string? from,
        string? to,
        int page,
        int pageSize,
        int maxChars,
        int scanLimit,
        bool countTotal)
    {
        var purposeText = ConvertTimelineText(purpose);
        var fromText = ConvertTimelineText(from);
        var toText = ConvertTimelineText(to);
        var overview = _store.GetOverview();
        if (!overview.Available)
        {
            return new JsonObject
            {
                ["available"] = false,
                ["packId"] = string.Empty,
                ["purpose"] = purposeText,
                ["targetPeriod"] = NewTargetPeriod(fromText, toText),
                ["inputPolicy"] = NewInputPolicy(),
                ["items"] = new JsonArray(),
                ["total"] = 0,
                ["pagination"] = NewPagination(page, pageSize, 0, 0),
                ["stats"] = new JsonObject
                {
                    ["partial"] = true,
                    ["scanLimit"] = 0,
                },
                ["assumptions"] = new JsonArray(),
                ["message"] = overview.Message,
            };
        }

        if (string.IsNullOrEmpty(purposeText))
        {
            purposeText = "preview";
        }

        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Min(200, Math.Max(1, pageSize));
        var effectiveScanLimit = Math.Min(50000, Math.Max(100, scanLimit));
        var offset = (effectivePage - 1) * effectivePageSize;
        var fromDate = ConvertTimelineLlmDateTime(fromText);
        var toDate = ConvertTimelineLlmDateTime(toText);
        var productFilter = ConvertTimelineText(product);
        var productNeedle = string.IsNullOrEmpty(productFilter)
            ? string.Empty
            : "\"product\":\"" + productFilter + "\"";
        var canFastSkipPhoneTokens = fromDate is null && toDate is null;
        var audioVerbalizationCache = new Dictionary<string, Dictionary<int, JsonObject>>(StringComparer.Ordinal);

        var items = new JsonArray();
        var total = 0;
        var scanned = 0;
        var skippedHardToRead = 0;
        var skippedAudioNotVerbalized = 0;
        var skippedEmpty = 0;
        var scanLimitReached = false;
        var eventsPath = Path.Combine(_settings.GetStoreDirectory(), "events.jsonl");

        foreach (var line in File.ReadLines(eventsPath))
        {
            var textLine = line.Trim();
            if (string.IsNullOrEmpty(textLine))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(productNeedle)
                && textLine.IndexOf(productNeedle, StringComparison.Ordinal) < 0)
            {
                continue;
            }

            if (canFastSkipPhoneTokens
                && textLine.IndexOf("\"kind\":\"phone_tokens\"", StringComparison.Ordinal) >= 0)
            {
                var lineProduct = GetJsonLineTextValue(textLine, "product");
                if (lineProduct.Equals("audio", StringComparison.Ordinal))
                {
                    var lineItemId = GetJsonLineTextValue(textLine, "itemId");
                    var lineSequence = GetJsonLineIntValue(textLine, "sequence", -1);
                    var lineVerbalization = GetAudioVerbalizationTurn(lineItemId, lineSequence, audioVerbalizationCache);
                    if (lineVerbalization is null)
                    {
                        scanned += 1;
                        skippedAudioNotVerbalized += 1;
                        if (scanned >= effectiveScanLimit)
                        {
                            scanLimitReached = true;
                            break;
                        }
                        continue;
                    }
                }
                else
                {
                    scanned += 1;
                    skippedHardToRead += 1;
                    if (scanned >= effectiveScanLimit)
                    {
                        scanLimitReached = true;
                        break;
                    }
                    continue;
                }
            }

            JsonObject? entry;
            try
            {
                entry = JsonNode.Parse(textLine) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            var eventProduct = GetString(entry, "product", string.Empty);
            if (!string.IsNullOrEmpty(productFilter)
                && !eventProduct.Equals(productFilter, StringComparison.Ordinal))
            {
                continue;
            }
            if (!IsEventInRange(entry, fromDate, toDate))
            {
                continue;
            }

            scanned += 1;
            var converted = ConvertInputEvent(entry, maxChars, audioVerbalizationCache);
            if (!GetBool(converted, "included", false))
            {
                var reason = GetString(converted, "skipReason", string.Empty);
                if (reason.Equals("hard_to_read", StringComparison.Ordinal))
                {
                    skippedHardToRead += 1;
                }
                else if (reason.Equals("audio_not_verbalized", StringComparison.Ordinal))
                {
                    skippedAudioNotVerbalized += 1;
                }
                else if (reason.Equals("empty_or_placeholder", StringComparison.Ordinal))
                {
                    skippedEmpty += 1;
                }

                if (scanned >= effectiveScanLimit)
                {
                    scanLimitReached = true;
                    break;
                }
                continue;
            }

            if (total >= offset && items.Count < effectivePageSize)
            {
                items.Add(CloneNode(GetNode(converted, "item")));
            }
            total += 1;

            if (!countTotal && items.Count >= effectivePageSize)
            {
                break;
            }
            if (scanned >= effectiveScanLimit)
            {
                scanLimitReached = true;
                break;
            }
        }

        return new JsonObject
        {
            ["available"] = true,
            ["packId"] = "llm-input-pack-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
            ["purpose"] = purposeText,
            ["targetPeriod"] = NewTargetPeriod(fromText, toText),
            ["inputPolicy"] = NewInputPolicy(),
            ["items"] = items,
            ["total"] = total,
            ["pagination"] = NewPagination(effectivePage, effectivePageSize, total, items.Count),
            ["stats"] = new JsonObject
            {
                ["partial"] = scanLimitReached || !countTotal,
                ["scanLimit"] = effectiveScanLimit,
                ["scannedEvents"] = scanned,
                ["includedItems"] = items.Count,
                ["totalReadableItems"] = total,
                ["skippedHardToRead"] = skippedHardToRead,
                ["skippedAudioNotVerbalized"] = skippedAudioNotVerbalized,
                ["skippedEmptyOrPlaceholder"] = skippedEmpty,
            },
            ["assumptions"] = new JsonArray
            {
                "Timeline master keeps raw references and intermediate data.",
                "Normal LLM inputs are text-only and exclude hard-to-read intermediate data.",
                "Audio text, when present, is a Timeline verbalization candidate from phone tokens with nearby context.",
                "LLM generated results are derived data, not primary facts.",
            },
            ["message"] = scanLimitReached
                ? "Timeline LLM input preview reached the scan limit before scanning all matching events."
                : string.Empty,
        };
    }

    private JsonObject ConvertInputEvent(
        JsonObject entry,
        int maxChars,
        Dictionary<string, Dictionary<int, JsonObject>> audioVerbalizationCache)
    {
        var productId = GetString(entry, "product", string.Empty);
        var eventId = GetString(entry, "eventId", string.Empty);
        var itemId = GetString(entry, "itemId", string.Empty);
        var eventType = GetString(entry, "eventType", string.Empty);
        var sequence = GetInt(entry, "sequence", 0);
        var time = GetObject(entry, "time");
        var actor = GetObject(entry, "actor");
        var content = GetObject(entry, "content");
        var sourceRef = GetObject(entry, "sourceRef");
        var contentKind = GetString(content, "kind", string.Empty);
        var contentValue = GetString(content, "value", string.Empty);
        JsonObject? verbalizedAudio = null;

        var text = contentValue;
        var kind = eventType;
        var notes = new JsonArray();
        var createdBy = new JsonObject
        {
            ["type"] = "source_product",
            ["name"] = productId,
            ["version"] = string.Empty,
        };

        if (contentKind.Equals("phone_tokens", StringComparison.Ordinal))
        {
            verbalizedAudio = GetAudioVerbalizationTurn(itemId, sequence, audioVerbalizationCache);
            if (verbalizedAudio is null)
            {
                return NewSkip("audio_not_verbalized");
            }

            text = GetString(verbalizedAudio, "text", string.Empty);
            contentKind = "audio_verbalized_text";
            createdBy = new JsonObject
            {
                ["type"] = "timeline",
                ["name"] = "audio_verbalization",
                ["version"] = GetString(verbalizedAudio, "model", string.Empty),
            };

            var status = GetString(verbalizedAudio, "status", string.Empty);
            if (!string.IsNullOrEmpty(status))
            {
                notes.Add("Audio phone tokens were verbalized by Timeline. Status: " + status + ".");
            }

            var confidence = GetDouble(verbalizedAudio, "confidence", 0);
            if (confidence > 0)
            {
                notes.Add("Audio verbalization confidence: " + confidence.ToString("0.###", CultureInfo.InvariantCulture) + ".");
            }
        }

        if (string.IsNullOrEmpty(text) || text.Equals("[text]", StringComparison.Ordinal))
        {
            return NewSkip("empty_or_placeholder");
        }

        var max = Math.Max(200, maxChars);
        if (text.Length > max)
        {
            text = text[..max];
            notes.Add("Text was truncated for preview.");
        }

        var occurredAt = GetString(time, "absoluteStartAt", string.Empty);
        var endedAt = GetString(time, "absoluteEndAt", string.Empty);
        var rawRefs = new JsonArray();
        AddIfNotEmpty(rawRefs, GetString(sourceRef, "timelinePath", string.Empty));
        AddIfNotEmpty(rawRefs, GetString(sourceRef, "convertInfoPath", string.Empty));
        if (verbalizedAudio is not null)
        {
            AddIfNotEmpty(rawRefs, GetString(verbalizedAudio, "resultPath", string.Empty));
        }

        return NewIncluded(new JsonObject
        {
            ["id"] = eventId,
            ["sourceProduct"] = productId,
            ["sourceProductName"] = GetProductDisplayName(productId),
            ["kind"] = kind,
            ["occurredAt"] = occurredAt,
            ["timeRange"] = NewTargetPeriod(occurredAt, endedAt),
            ["actor"] = new JsonObject
            {
                ["type"] = GetString(actor, "type", string.Empty),
                ["label"] = GetString(actor, "label", string.Empty),
            },
            ["title"] = itemId,
            ["text"] = text,
            ["contentKind"] = contentKind,
            ["notes"] = notes,
            ["sourceEventIds"] = new JsonArray { eventId },
            ["rawRefs"] = rawRefs,
            ["createdBy"] = createdBy,
        });
    }

    private Dictionary<int, JsonObject> GetAudioVerbalizationMap(string itemId)
    {
        var map = new Dictionary<int, JsonObject>();
        var safeItemId = ConvertTimelineText(itemId);
        if (string.IsNullOrEmpty(safeItemId))
        {
            return map;
        }

        var resultPath = Path.Combine(
            _settings.GetStoreDirectory(),
            "audio-verbalizations",
            GetTimelineZipSafeSegment(safeItemId),
            "audio-verbalization.json");
        if (!File.Exists(resultPath))
        {
            return map;
        }

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(File.ReadAllText(resultPath)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return map;
        }

        var status = GetObject(payload, "status");
        var model = GetString(status, "model", string.Empty);
        var language = GetString(status, "language", string.Empty);
        foreach (var turn in GetArray(payload, "turns").OfType<JsonObject>())
        {
            var sequence = GetAudioVerbalizationSequenceFromTurn(turn);
            if (sequence < 0)
            {
                continue;
            }

            var text = GetString(turn, "text", string.Empty);
            var state = GetString(turn, "status", string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(text) || state.Equals("unresolved", StringComparison.Ordinal))
            {
                continue;
            }

            map[sequence] = new JsonObject
            {
                ["text"] = text,
                ["status"] = state,
                ["confidence"] = GetDouble(turn, "confidence", 0),
                ["basis"] = ToStringArray(GetArray(turn, "basis")),
                ["uncertainTerms"] = ToStringArray(GetArray(turn, "uncertainTerms")),
                ["resultPath"] = resultPath,
                ["model"] = model,
                ["language"] = language,
            };
        }

        return map;
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

        return map.TryGetValue(sequence, out var value) ? value : null;
    }

    private static int GetAudioVerbalizationSequenceFromTurn(JsonObject turn)
    {
        var index = GetInt(turn, "index", 0);
        if (index > 0)
        {
            return index - 1;
        }

        var turnId = GetString(turn, "turnId", string.Empty);
        var match = Regex.Match(turnId, "^turn-(\\d+)$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1 : -1;
    }

    private static DateTimeOffset? ConvertTimelineLlmDateTime(string value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsEventInRange(JsonObject entry, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is null && to is null)
        {
            return true;
        }

        var occurredAt = GetString(GetObject(entry, "time"), "absoluteStartAt", string.Empty);
        var eventTime = ConvertTimelineLlmDateTime(occurredAt);
        if (eventTime is null)
        {
            return true;
        }

        if (from is not null && eventTime < from)
        {
            return false;
        }
        if (to is not null && eventTime > to)
        {
            return false;
        }

        return true;
    }

    private static JsonObject NewSkip(string reason)
        => new()
        {
            ["included"] = false,
            ["skipReason"] = reason,
            ["item"] = null,
        };

    private static JsonObject NewIncluded(JsonObject item)
        => new()
        {
            ["included"] = true,
            ["skipReason"] = string.Empty,
            ["item"] = item,
        };

    private static JsonObject NewTargetPeriod(string from, string to)
        => new()
        {
            ["from"] = from,
            ["to"] = to,
        };

    private static JsonObject NewInputPolicy()
        => new()
        {
            ["textOnly"] = true,
            ["excludeHardToReadIntermediateData"] = true,
            ["securityRedaction"] = "minimal",
        };

    private static JsonObject NewPagination(int page, int pageSize, int totalItems, int returnedItems)
    {
        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Max(1, pageSize);
        var totalPages = totalItems > 0
            ? (int)Math.Ceiling(totalItems / (double)effectivePageSize)
            : 0;
        var offset = (effectivePage - 1) * effectivePageSize;
        return new JsonObject
        {
            ["mode"] = "page",
            ["page"] = effectivePage,
            ["pageSize"] = effectivePageSize,
            ["totalItems"] = totalItems,
            ["totalPages"] = totalPages,
            ["returnedItems"] = returnedItems,
            ["offset"] = offset,
            ["rangeStart"] = returnedItems > 0 ? offset + 1 : 0,
            ["rangeEnd"] = returnedItems > 0 ? offset + returnedItems : 0,
            ["hasPrevious"] = effectivePage > 1 && totalItems > 0,
            ["hasNext"] = effectivePage < totalPages,
        };
    }

    private static string GetJsonLineTextValue(string line, string name)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var match = Regex.Match(line, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? Regex.Unescape(match.Groups[1].Value) : string.Empty;
    }

    private static int GetJsonLineIntValue(string line, string name, int fallback)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(name))
        {
            return fallback;
        }

        var match = Regex.Match(line, "\"" + Regex.Escape(name) + "\"\\s*:\\s*(-?\\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : fallback;
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
        => GetNode(source, name) as JsonArray ?? [];

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
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
                return node.GetValue<int>();
            }

            return int.TryParse(ConvertTimelineText(node.GetValue<object>()), out var parsed)
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
                return node.GetValue<double>();
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

    private static bool GetBool(JsonObject source, string name, bool fallback)
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

            return bool.TryParse(ConvertTimelineText(node.GetValue<object>()), out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static JsonArray ToStringArray(JsonArray source)
    {
        var result = new JsonArray();
        foreach (var item in source)
        {
            AddIfNotEmpty(result, ConvertNodeToString(item));
        }

        return result;
    }

    private static string ConvertNodeToString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        try
        {
            return ConvertTimelineText(node.GetValue<object>());
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static void AddIfNotEmpty(JsonArray target, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            target.Add(value);
        }
    }

    private static JsonNode? CloneNode(JsonNode? node)
        => node?.DeepClone();

    private static string GetTimelineZipSafeSegment(string value)
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

    private static string GetProductDisplayName(string productId)
    {
        return productId switch
        {
            "audio" => "TimelineForAudio",
            "windows-codex" => "TimelineForWindowsCodex",
            "chatgpt" => "TimelineForChatGPT",
            "image" => "TimelineForImage",
            "video" => "TimelineForVideo",
            "pc" => "TimelineForPcInfo",
            _ => productId,
        };
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
}
