namespace Timeline.Web.Components.Shared;

public static class TextVolumeChartBuilder
{
    private static readonly double[] BucketSizeCandidates =
    [
        30,
        60,
        120,
        300,
        600,
        900,
        1800,
        3600,
    ];

    public static IReadOnlyList<TextVolumeChartPoint> Build(
        IEnumerable<TextVolumeSegment> segments,
        double? durationSec = null,
        int maxBuckets = 48)
    {
        var normalizedSegments = segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .Select(segment => new
            {
                StartSec = Math.Max(0, segment.StartSec),
                EndSec = Math.Max(Math.Max(0, segment.StartSec), segment.EndSec),
                CharacterCount = CountCharacters(segment.Text),
            })
            .Where(segment => segment.CharacterCount > 0)
            .ToList();

        if (normalizedSegments.Count == 0)
        {
            return [];
        }

        var observedEndSec = normalizedSegments.Max(segment => segment.EndSec);
        var totalSec = Math.Max(durationSec.GetValueOrDefault(), observedEndSec);
        if (totalSec <= 0)
        {
            totalSec = observedEndSec;
        }

        if (totalSec <= 0)
        {
            return [];
        }

        var bucketSizeSec = ChooseBucketSize(totalSec, maxBuckets);
        var bucketCount = Math.Max(1, (int)Math.Ceiling(totalSec / bucketSizeSec));
        var buckets = new double[bucketCount];

        foreach (var segment in normalizedSegments)
        {
            var segmentStart = segment.StartSec;
            var segmentEnd = segment.EndSec;
            if (segmentEnd <= segmentStart)
            {
                var index = Math.Clamp((int)Math.Floor(segmentStart / bucketSizeSec), 0, bucketCount - 1);
                buckets[index] += segment.CharacterCount;
                continue;
            }

            var firstBucket = Math.Clamp((int)Math.Floor(segmentStart / bucketSizeSec), 0, bucketCount - 1);
            var lastBucket = Math.Clamp((int)Math.Floor((segmentEnd - 0.001) / bucketSizeSec), 0, bucketCount - 1);
            var segmentDuration = segmentEnd - segmentStart;

            for (var bucketIndex = firstBucket; bucketIndex <= lastBucket; bucketIndex++)
            {
                var bucketStart = bucketIndex * bucketSizeSec;
                var bucketEnd = Math.Min(totalSec, bucketStart + bucketSizeSec);
                var overlap = Math.Max(0, Math.Min(segmentEnd, bucketEnd) - Math.Max(segmentStart, bucketStart));
                if (overlap <= 0)
                {
                    continue;
                }

                buckets[bucketIndex] += segment.CharacterCount * (overlap / segmentDuration);
            }
        }

        return buckets
            .Select((value, index) =>
            {
                var startSec = index * bucketSizeSec;
                var endSec = Math.Min(totalSec, startSec + bucketSizeSec);
                return new TextVolumeChartPoint(
                    $"{FormatTime(startSec)} - {FormatTime(endSec)}",
                    (int)Math.Round(value, MidpointRounding.AwayFromZero),
                    startSec,
                    endSec);
            })
            .ToList();
    }

    private static double ChooseBucketSize(double totalSec, int maxBuckets)
    {
        var target = totalSec / Math.Max(1, maxBuckets);
        return BucketSizeCandidates.FirstOrDefault(candidate => candidate >= target) is > 0 and var candidate
            ? candidate
            : BucketSizeCandidates[^1];
    }

    private static int CountCharacters(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(character => !char.IsWhiteSpace(character));

    private static string FormatTime(double seconds)
    {
        var safeSeconds = Math.Max(0, seconds);
        var totalSeconds = (int)Math.Floor(safeSeconds);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var secs = totalSeconds % 60;
        return hours > 0
            ? $"{hours:0}:{minutes:00}:{secs:00}"
            : $"{minutes:00}:{secs:00}";
    }
}

