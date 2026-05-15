using System.Collections.Concurrent;

public sealed class TimelineAudioVerbalizationJobRegistry
{
    private readonly ConcurrentDictionary<string, int> _activeJobs = new(StringComparer.Ordinal);

    public bool IsActive(string? jobId)
    {
        var value = ConvertTimelineText(jobId);
        return !string.IsNullOrEmpty(value) && _activeJobs.ContainsKey(value);
    }

    public IDisposable MarkActive(string jobId, string audioItemId)
    {
        var value = ConvertTimelineText(jobId);
        if (string.IsNullOrEmpty(value))
        {
            return new EmptyLease();
        }

        _activeJobs.AddOrUpdate(value, 1, (_, current) => current + 1);
        return new Lease(_activeJobs, value);
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

    private sealed class Lease : IDisposable
    {
        private readonly ConcurrentDictionary<string, int> _activeJobs;
        private readonly string _jobId;
        private bool _disposed;

        public Lease(ConcurrentDictionary<string, int> activeJobs, string jobId)
        {
            _activeJobs = activeJobs;
            _jobId = jobId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _activeJobs.AddOrUpdate(
                _jobId,
                0,
                (_, current) => Math.Max(0, current - 1));
            if (_activeJobs.TryGetValue(_jobId, out var count) && count <= 0)
            {
                _activeJobs.TryRemove(_jobId, out _);
            }
            _disposed = true;
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
