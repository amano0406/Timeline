using System.Net.Http.Json;

namespace Timeline.Web.Services;

public sealed partial class TimelineLocalApiClient
{
    public Task<TimelineStoreOverview> GetTimelineStoreOverviewWithLocalFallbackAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(GetLocalStoreOverviewFallback());

    public Task<TimelineDashboardStats> GetTimelineDashboardStatsWithLocalFallbackAsync(
        int days = 30,
        CancellationToken cancellationToken = default)
    {
        var safeDays = Math.Clamp(days, 7, 90);
        return Task.FromResult(GetLocalDashboardStatsFallback(string.Empty, "auto", safeDays));
    }

    public Task<TimelineDashboardStats> GetTimelineDashboardStatsWithLocalFallbackAsync(
        string range,
        string bucket = "auto",
        int days = 0,
        CancellationToken cancellationToken = default)
        => Task.FromResult(GetLocalDashboardStatsFallback(range, bucket, days));
}
