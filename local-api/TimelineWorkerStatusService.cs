using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

public sealed class TimelineWorkerStatusService
{
    private static readonly ConcurrentDictionary<string, RebuildJobController> ActiveRebuildJobs = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimelineLocalApiOptions _options;

    public TimelineWorkerStatusService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        IServiceScopeFactory scopeFactory,
        TimelineLocalApiOptions options)
    {
        _settings = settings;
        _operations = operations;
        _scopeFactory = scopeFactory;
        _options = options;
    }

    public async Task<JsonObject> StartRebuildAsync(JsonObject? request, CancellationToken cancellationToken)
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
            var result = await StartRebuildCoreAsync(request, cancellationToken);
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
            var status = ReadWorkerJobStatusObject(text);
            if (IsWorkerJobActive(status) && !IsRebuildJobActiveInProcess(text))
            {
                SetStaleWorkerJobFailed(status);
                status = ReadWorkerJobStatusObject(text);
            }

            return status;
        }

        var latest = GetLatestWorkerJobStatusObject();
        var latestJobId = GetString(latest, "jobId", string.Empty);
        if (IsWorkerJobActive(latest) && !IsRebuildJobActiveInProcess(latestJobId))
        {
            SetStaleWorkerJobFailed(latest);
            latest = GetLatestWorkerJobStatusObject();
        }

        return latest;
    }

    public JsonObject CancelRebuild(string? jobId)
    {
        var requestedJobId = ConvertTimelineText(jobId);
        var status = string.IsNullOrWhiteSpace(requestedJobId)
            ? GetLatestWorkerJobStatusObject()
            : ReadWorkerJobStatusObject(requestedJobId);
        var effectiveJobId = GetString(status, "jobId", requestedJobId);
        if (string.IsNullOrWhiteSpace(effectiveJobId))
        {
            return NewWorkerJobStatus(
                string.Empty,
                "none",
                "none",
                "No active Timeline rebuild job exists.",
                string.Empty,
                string.Empty,
                DateTimeOffset.Now.ToString("o"),
                string.Empty,
                0,
                0,
                null);
        }

        if (!IsWorkerJobActive(status))
        {
            return status;
        }

        var now = DateTimeOffset.Now.ToString("o");
        status["state"] = "canceling";
        status["stage"] = "canceling";
        status["message"] = "Timeline rebuild cancellation was requested.";
        status["updatedAt"] = now;
        status["completedAt"] = string.Empty;
        WriteWorkerJobStatus(status);

        if (ActiveRebuildJobs.TryGetValue(effectiveJobId, out var controller))
        {
            controller.Cancel();
        }

        return status;
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
            var updatedAt = GetString(payload, "updatedAt", string.Empty);
            if (IsHeartbeatStale(updatedAt))
            {
                return new TimelineDockerWorkerStatusResponse
                {
                    Available = false,
                    Worker = GetString(payload, "worker", "timeline-worker"),
                    State = "stale",
                    UpdatedAt = updatedAt,
                    WorkDirectory = GetString(payload, "workDirectory", string.Empty),
                    StoreDirectory = GetString(payload, "storeDirectory", string.Empty),
                    StoreAvailable = GetBool(payload, "storeAvailable", false),
                    RebuildId = GetString(payload, "rebuildId", string.Empty),
                    CreatedAt = GetString(payload, "createdAt", string.Empty),
                    ItemCount = GetInt(payload, "itemCount", 0),
                    EventCount = GetInt(payload, "eventCount", 0),
                    Message = "Timeline Docker worker heartbeat is stale.",
                };
            }

            return new TimelineDockerWorkerStatusResponse
            {
                Available = true,
                Worker = GetString(payload, "worker", "timeline-worker"),
                State = GetString(payload, "state", string.Empty),
                UpdatedAt = updatedAt,
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

    public async Task<JsonObject> RepairDockerWorkerAsync(CancellationToken cancellationToken)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "worker",
            "Timeline",
            "timeline_worker_repair",
            "started",
            "Timeline worker repair started.");

        try
        {
            var root = Path.GetFullPath(_options.TimelineProductPath);
            var scriptPath = Path.Combine(root, "scripts", "repair-worker.ps1");
            if (!File.Exists(scriptPath))
            {
                throw new InvalidOperationException($"Timeline worker repair script was not found: {scriptPath}");
            }

            var result = await RunRepairScriptAsync(root, scriptPath, cancellationToken);
            var status = GetStatus();
            var ok = result.ExitCode == 0 && status.Available && status.State.Equals("running", StringComparison.OrdinalIgnoreCase);
            var message = ok
                ? "Timeline worker を復旧しました。"
                : "Timeline worker の復旧結果を確認できませんでした。";

            var payload = new JsonObject
            {
                ["ok"] = ok,
                ["exitCode"] = result.ExitCode,
                ["message"] = message,
                ["stdout"] = result.Stdout,
                ["stderr"] = result.Stderr,
                ["worker"] = JsonSerializer.SerializeToNode(status),
            };

            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "worker",
                "Timeline",
                "timeline_worker_repair",
                ok ? "completed" : "failed",
                message,
                durationMs: durationMs,
                stdout: result.Stdout,
                stderr: result.Stderr,
                details: new JsonObject
                {
                    ["exitCode"] = result.ExitCode,
                    ["workerState"] = status.State,
                    ["workerAvailable"] = status.Available,
                });

            if (!ok)
            {
                throw new InvalidOperationException(BuildRepairFailureMessage(message, result.Stderr));
            }

            return payload;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "worker",
                "Timeline",
                "timeline_worker_repair",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private async Task<JsonObject> StartRebuildCoreAsync(JsonObject? request, CancellationToken cancellationToken)
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

        var controller = new RebuildJobController();
        ActiveRebuildJobs[jobId] = controller;
        var rebuildRequest = request?.DeepClone() as JsonObject;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var rebuild = scope.ServiceProvider.GetRequiredService<TimelineStoreRebuildService>();
                await rebuild.RunRebuildJobAsync(jobId, rebuildRequest, controller.Token);
            }
            catch (OperationCanceledException) when (controller.Token.IsCancellationRequested)
            {
                var canceledAt = DateTimeOffset.Now.ToString("o");
                WriteWorkerJobStatus(NewWorkerJobStatus(
                    jobId,
                    "canceled",
                    "canceled",
                    "Timeline store rebuild was canceled.",
                    string.Empty,
                    now,
                    canceledAt,
                    canceledAt,
                    0,
                    0,
                    null));
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
                if (ActiveRebuildJobs.TryRemove(jobId, out var removed))
                {
                    removed.Dispose();
                }
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

    private static async Task<RepairScriptResult> RunRepairScriptAsync(
        string root,
        string scriptPath,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArgument(scriptPath)} -RepoRoot {QuoteArgument(root)}",
            WorkingDirectory = root,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Timeline worker repair process could not be started.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw new TimeoutException("Timeline worker repair timed out.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new RepairScriptResult(process.ExitCode, stdout, stderr);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string QuoteArgument(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string BuildRepairFailureMessage(string message, string detail)
    {
        var normalized = ConvertTimelineText(detail);
        if (string.IsNullOrEmpty(normalized))
        {
            return message;
        }

        const int maxDetailLength = 500;
        if (normalized.Length > maxDetailLength)
        {
            normalized = "..." + normalized[^maxDetailLength..];
        }

        return $"{message} {normalized}";
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
        var currentState = GetString(status, "state", string.Empty);
        if (currentState.Equals("canceling", StringComparison.OrdinalIgnoreCase))
        {
            var canceled = NewWorkerJobStatus(
                jobId,
                "canceled",
                "canceled",
                "Timeline rebuild was canceled after the stop request.",
                string.Empty,
                GetString(status, "startedAt", string.Empty),
                now,
                now,
                GetInt(status, "itemCount", 0),
                GetInt(status, "eventCount", 0),
                CloneNode(GetNode(status, "result")));
            WriteWorkerJobStatus(canceled);
            return;
        }

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
            || state.Equals("running", StringComparison.OrdinalIgnoreCase)
            || state.Equals("canceling", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHeartbeatStale(string updatedAt)
    {
        if (string.IsNullOrWhiteSpace(updatedAt))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(updatedAt, out var timestamp))
        {
            return true;
        }

        return (DateTimeOffset.Now - timestamp).TotalSeconds > 30;
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

    private sealed class RebuildJobController : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();

        public CancellationToken Token => _cancellation.Token;

        public void Cancel()
        {
            if (!_cancellation.IsCancellationRequested)
            {
                _cancellation.Cancel();
            }
        }

        public void Dispose()
        {
            _cancellation.Dispose();
        }
    }

    private sealed record RepairScriptResult(
        int ExitCode,
        string Stdout,
        string Stderr);
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
