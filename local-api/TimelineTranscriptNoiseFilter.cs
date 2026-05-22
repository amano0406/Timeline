using System.Globalization;
using System.Text.Json.Nodes;

internal static class TimelineTranscriptNoiseFilter
{
    private const double LongKnownHallucinationDurationSec = 8.0;
    private const double HighNoSpeechProbability = 0.6;

    private static readonly HashSet<string> KnownSilenceHallucinations = new(StringComparer.Ordinal)
    {
        Normalize("ご視聴ありがとうございました"),
        Normalize("ご視聴ありがとうございます"),
        Normalize("ありがとうございました"),
        Normalize("Thank you for watching"),
        Normalize("Thanks for watching"),
    };

    private static readonly HashSet<string> LongSegmentHallucinations = new(StringComparer.Ordinal)
    {
        Normalize("ご視聴ありがとうございました"),
        Normalize("ご視聴ありがとうございます"),
        Normalize("Thank you for watching"),
        Normalize("Thanks for watching"),
    };

    public static bool IsLikelySilentHallucination(JsonObject? turn)
    {
        if (turn is null)
        {
            return false;
        }

        var text = GetStringAny(turn, ["text", "sourceText", "transcriptText", "readableText", "transcript_text"]);
        var normalizedText = Normalize(text);
        if (!KnownSilenceHallucinations.Contains(normalizedText))
        {
            return false;
        }

        var startSec = GetDoubleAny(turn, ["start_sec", "startSec", "start"], 0) ?? 0;
        var endSec = GetDoubleAny(turn, ["end_sec", "endSec", "end"], startSec) ?? startSec;
        var durationSec = Math.Max(0, endSec - startSec);
        var noSpeechProbability = GetDoubleAny(turn, ["no_speech_probability", "noSpeechProbability", "no_speech_prob"], null);

        return LongSegmentHallucinations.Contains(normalizedText)
                && durationSec >= LongKnownHallucinationDurationSec
            || noSpeechProbability >= HighNoSpeechProbability;
    }

    public static bool IsKnownSilenceHallucinationText(string? text)
        => KnownSilenceHallucinations.Contains(Normalize(text));

    private static string Normalize(string? value)
        => string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)))
            .ToLowerInvariant();

    private static string GetStringAny(JsonObject? source, string[] names)
    {
        if (source is null)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            if (!source.TryGetPropertyValue(name, out var node))
            {
                continue;
            }

            var value = GetScalarString(node);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string GetScalarString(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return string.Empty;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text.Trim();
        }

        return value.ToString().Trim();
    }

    private static double? GetDoubleAny(JsonObject? source, string[] names, double? fallback)
    {
        if (source is null)
        {
            return fallback;
        }

        foreach (var name in names)
        {
            if (!source.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
            {
                continue;
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (value.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }
}
