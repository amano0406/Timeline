using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

public sealed class TimelineWorkerStatusService
{
    private static readonly ConcurrentDictionary<string, byte> ActiveRebuildJobs = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly IServiceScopeFactory _scopeFactory;

    public TimelineWorkerStatusService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        IServiceScopeFactory scopeFactory)
    {
        _settings = settings;
        _operations = operations;
        _scopeFactory = scopeFactory;
    }

    public async Task<JsonObject> StartRebuildAsync(CancellationToken cancellationToken)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "worker",
            "Timeline",
            "timeline_rebuild_start",
            "started",
            "Web operation started.");

        try
        {
            var result = await StartRebuildCoreAsync(cancellationToken);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "worker",
                "Timeline",
                "timeline_rebuild_start",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: GetOperationResultDetails(result));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "worker",
                "Timeline",
                "timeline_rebuild_start",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    public object? GetRebuildStatus(string? jobId)
    {
        var text = ConvertTimelineText(jobId);
        if (!string.IsNullOrEmpty(text))
        {
            return ReadWorkerJobStatus(text);
        }

        return GetLatestWorkerJobStatus();
    }

    public TimelineDockerWorkerStatusResponse GetStatus()
    {
        var path = Path.Combine(_settings.GetWorkerDirectory(), "docker-worker-heartbeat.json");
        if (!File.Exists(path))
        {
            return new TimelineDockerWorkerStatusResponse
            {
                Available = false,
                Worker = "timeline-worker",
                State = "missing",
                UpdatedAt = string.Empty,
                WorkDirectory = string.Empty,
                StoreDirectory = string.Empty,
                StoreAvailable = false,
                RebuildId = string.Empty,
                CreatedAt = string.Empty,
                ItemCount = 0,
                EventCount = 0,
                Message = "Timeline Docker worker heartbeat was not found.",
            };
        }

        try
        {
            var payload = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return new TimelineDockerWorkerStatusResponse
            {
                Available = true,
                Worker = GetString(payload, "worker", "timeline-worker"),
                State = GetString(payload, "state", string.Empty),
                UpdatedAt = GetString(payload, "updatedAt", string.Empty),
                WorkDirectory = GetString(payload, "workDirectory", string.Empty),
                StoreDirectory = GetString(payload, "storeDirectory", string.Empty),
                StoreAvailable = GetBool(payload, "storeAvailable", false),
                RebuildId = GetString(payload, "rebuildId", string.Empty),
                CreatedAt = GetString(payload, "createdAt", string.Empty),
                ItemCount = GetInt(payload, "itemCount", 0),
                EventCount = GetInt(payload, "eventCount", 0),
                Message = string.Empty,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TimelineDockerWorkerStatusResponse
            {
                Available = false,
                Worker = "timeline-worker",
                State = "unreadable",
                UpdatedAt = string.Empty,
                WorkDirectory = string.Empty,
                StoreDirectory = string.Empty,
                StoreAvailable = false,
                RebuildId = string.Empty,
                CreatedAt = string.Empty,
                ItemCount = 0,
                EventCount = 0,
                Message = ex.Message,
            };
        }
    }

    private async Task<JsonObject> StartRebuildCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var latest = GetLatestWorkerJobStatusObject();
        if (IsWorkerJobActive(latest))
        {
            var latestJobId = GetString(latest, "jobId", string.Empty);
            if (IsRebuildJobActiveInProcess(latestJobId))
            {
                return latest;
            }

            SetStaleWorkerJobFailed(latest);
        }

        var jobId = NewWorkerJobId();
        var now = DateTimeOffset.Now.ToString("o");
        var status = NewWorkerJobStatus(
            jobId,
            "queued",
            "queued",
            "Timeline rebuild worker has been queued.",
            string.Empty,
            now,
            now,
            string.Empty,
            0,
            0,
            null);
        WriteWorkerJobStatus(status);

        ActiveRebuildJobs[jobId] = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var rebuild = scope.ServiceProvider.GetRequiredService<TimelineStoreRebuildService>();
                await rebuild.RunRebuildJobAsync(jobId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                var failedAt = DateTimeOffset.Now.ToString("o");
                WriteWorkerJobStatus(NewWorkerJobStatus(
                    jobId,
                    "failed",
                    "failed",
                    "Timeline store rebuild failed.",
                    ex.Message,
                    now,
                    failedAt,
                    failedAt,
                    0,
                    0,
                    null));
            }
            finally
            {
                ActiveRebuildJobs.TryRemove(jobId, out _);
            }
        });

        return ReadWorkerJobStatusObject(jobId);
    }

    private JsonObject ReadWorkerJobStatusObject(string jobId)
    {
        var path = GetWorkerJobStatusPath(jobId);
        if (!File.Exists(path))
        {
            return NewWorkerJobStatus(
                jobId,
                "missing",
                string.Empty,
                "Worker job was not found.",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                0,
                null);
        }

        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? NewWorkerJobStatus(
                jobId,
                "unreadable",
                string.Empty,
                "Worker job status could not be read.",
                path,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                0,
                null);
    }

    private JsonObject GetLatestWorkerJobStatusObject()
    {
        var workerDirectory = _settings.GetWorkerDirectory();
        var jobs = Directory
            .EnumerateFiles(workerDirectory, "timeline-*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        if (jobs.Count == 0)
        {
            return NewWorkerJobStatus(
                string.Empty,
                "none",
                string.Empty,
                "No Timeline worker job has been started.",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                0,
                null);
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(jobs[0].FullName)) as JsonObject
                ?? NewWorkerJobStatus(
                    string.Empty,
                    "unreadable",
                    string.Empty,
                    "Latest Timeline worker job could not be read.",
                    jobs[0].FullName,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return NewWorkerJobStatus(
                string.Empty,
                "unreadable",
                string.Empty,
                "Latest Timeline worker job could not be read.",
                ex.Message,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                0,
                null);
        }
    }

    private void SetStaleWorkerJobFailed(JsonObject status)
    {
        var jobId = GetString(status, "jobId", string.Empty);
        if (string.IsNullOrEmpty(jobId))
        {
            return;
        }

        var now = DateTimeOffset.Now.ToString("o");
        var failed = NewWorkerJobStatus(
            jobId,
            "failed",
            "stale",
            "Timeline rebuild worker was not running.",
            "Timeline rebuild worker process was not found.",
            GetString(status, "startedAt", string.Empty),
            now,
            now,
            GetInt(status, "itemCount", 0),
            GetInt(status, "eventCount", 0),
            CloneNode(GetNode(status, "result")));
        WriteWorkerJobStatus(failed);
    }

    public void WriteWorkerJobStatus(JsonObject status)
    {
        var jobId = GetString(status, "jobId", string.Empty);
        if (string.IsNullOrEmpty(jobId))
        {
            throw new InvalidOperationException("Worker job id is required.");
        }

        File.WriteAllText(GetWorkerJobStatusPath(jobId), status.ToJsonString(), new UTF8Encoding(false));
        _operations.WriteOperationEvent(
            jobId,
            "worker",
            "Timeline",
            GetString(status, "kind", "timeline_worker"),
            GetString(status, "state", string.Empty),
            GetString(status, "message", string.Empty),
            details: new JsonObject
            {
                ["stage"] = GetString(status, "stage", string.Empty),
                ["error"] = GetString(status, "error", string.Empty),
                ["itemCount"] = GetInt(status, "itemCount", 0),
                ["eventCount"] = GetInt(status, "eventCount", 0),
                ["completedAt"] = GetString(status, "completedAt", string.Empty),
            });
    }

    private static bool IsRebuildJobActiveInProcess(string jobId)
        => !string.IsNullOrEmpty(ConvertTimelineText(jobId)) && ActiveRebuildJobs.ContainsKey(jobId);

    private static bool IsWorkerJobActive(JsonObject status)
    {
        var state = GetString(status, "state", string.Empty);
        return state.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || state.Equals("running", StringComparison.OrdinalIgnoreCase);
    }

    private static string NewWorkerJobId()
    {
        return $"timeline-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..(9 + 15 + 1 + 8)];
    }

    private static JsonObject NewWorkerJobStatus(
        string jobId,
        string state,
        string stage,
        string message,
        string error,
        string startedAt,
        string updatedAt,
        string completedAt,
        int itemCount,
        int eventCount,
        JsonNode? result)
    {
        return new JsonObject
        {
            ["jobId"] = jobId,
            ["kind"] = "timeline_rebuild",
            ["state"] = state,
            ["stage"] = stage,
            ["message"] = message,
            ["error"] = error,
            ["startedAt"] = startedAt,
            ["updatedAt"] = updatedAt,
            ["completedAt"] = completedAt,
            ["itemCount"] = itemCount,
            ["eventCount"] = eventCount,
            ["result"] = result?.DeepClone(),
        };
    }

    private static JsonObject GetOperationResultDetails(JsonObject result)
    {
        return new JsonObject
        {
            ["state"] = GetString(result, "state", string.Empty),
            ["message"] = GetString(result, "message", string.Empty),
        };
    }

    private object? ReadWorkerJobStatus(string jobId)
    {
        var path = GetWorkerJobStatusPath(jobId);
        if (!File.Exists(path))
        {
            return new TimelineWorkerJobStatusResponse
            {
                JobId = jobId,
                Kind = "timeline_rebuild",
                State = "missing",
                Stage = string.Empty,
                Message = "Worker job was not found.",
                Error = string.Empty,
                StartedAt = string.Empty,
                UpdatedAt = string.Empty,
                CompletedAt = string.Empty,
                ItemCount = 0,
                EventCount = 0,
                Result = null,
            };
        }

        return JsonNode.Parse(File.ReadAllText(path));
    }

    private object? GetLatestWorkerJobStatus()
    {
        var workerDirectory = _settings.GetWorkerDirectory();
        var jobs = Directory
            .EnumerateFiles(workerDirectory, "timeline-*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        if (jobs.Count == 0)
        {
            return new TimelineWorkerJobStatusResponse
            {
                JobId = string.Empty,
                Kind = "timeline_rebuild",
                State = "none",
                Stage = string.Empty,
                Message = "No Timeline worker job has been started.",
                Error = string.Empty,
                StartedAt = string.Empty,
                UpdatedAt = string.Empty,
                CompletedAt = string.Empty,
                ItemCount = 0,
                EventCount = 0,
                Result = null,
            };
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(jobs[0].FullName));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TimelineWorkerJobStatusResponse
            {
                JobId = string.Empty,
                Kind = "timeline_rebuild",
                State = "unreadable",
                Stage = string.Empty,
                Message = "Latest Timeline worker job could not be read.",
                Error = ex.Message,
                StartedAt = string.Empty,
                UpdatedAt = string.Empty,
                CompletedAt = string.Empty,
                ItemCount = 0,
                EventCount = 0,
                Result = null,
            };
        }
    }

    private string GetWorkerJobStatusPath(string jobId)
    {
        return Path.Combine(_settings.GetWorkerDirectory(), GetTimelineZipSafeSegment(jobId) + ".json");
    }

    private static string GetTimelineZipSafeSegment(string value)
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

    private static JsonNode? CloneNode(JsonNode? node)
        => node?.DeepClone();

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

            return ConvertTimelineText(node.GetValue<object>()).ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on" => true,
                "false" or "0" or "no" or "off" => false,
                _ => fallback,
            };
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
}

public sealed class TimelineDockerWorkerStatusResponse
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("worker")]
    public string Worker { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";

    [JsonPropertyName("workDirectory")]
    public string WorkDirectory { get; set; } = "";

    [JsonPropertyName("storeDirectory")]
    public string StoreDirectory { get; set; } = "";

    [JsonPropertyName("storeAvailable")]
    public bool StoreAvailable { get; set; }

    [JsonPropertyName("rebuildId")]
    public string RebuildId { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("itemCount")]
    public int ItemCount { get; set; }

    [JsonPropertyName("eventCount")]
    public int EventCount { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class TimelineWorkerJobStatusResponse
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("stage")]
    public string Stage { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonPropertyName("startedAt")]
    public string StartedAt { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";

    [JsonPropertyName("completedAt")]
    public string CompletedAt { get; set; } = "";

    [JsonPropertyName("itemCount")]
    public int ItemCount { get; set; }

    [JsonPropertyName("eventCount")]
    public int EventCount { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }
}
