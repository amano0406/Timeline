using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        var runtime = _settings.ReadSettings().Runtime;
        var dockerState = TryGetTimelineWorkerContainerState(runtime);
        if (!File.Exists(path))
        {
            if (IsDockerEngineUnavailable(dockerState.Message))
            {
                return new TimelineDockerWorkerStatusResponse
                {
                    Available = false,
                    Worker = "timeline-worker",
                    State = "docker_unavailable",
                    UpdatedAt = string.Empty,
                    WorkDirectory = string.Empty,
                    StoreDirectory = string.Empty,
                    StoreAvailable = false,
                    RebuildId = string.Empty,
                    CreatedAt = string.Empty,
                    ItemCount = 0,
                    EventCount = 0,
                    Message = "Docker engine is not running.",
                };
            }

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

            if (!dockerState.Known)
            {
                if (IsDockerEngineUnavailable(dockerState.Message))
                {
                    return NewDockerWorkerStatusFromHeartbeat(
                        payload,
                        available: false,
                        state: "docker_unavailable",
                        message: "Docker engine is not running.");
                }

                return NewDockerWorkerStatusFromHeartbeat(
                    payload,
                    available: false,
                    state: "unreadable",
                    message: string.IsNullOrWhiteSpace(dockerState.Message)
                        ? "Timeline Docker worker state could not be checked."
                        : dockerState.Message);
            }

            if (dockerState.Known && !dockerState.Running)
            {
                var state = dockerState.State.Equals("not_found", StringComparison.OrdinalIgnoreCase)
                    ? "missing"
                    : "stale";
                return NewDockerWorkerStatusFromHeartbeat(
                    payload,
                    available: false,
                    state: state,
                    message: dockerState.Message);
            }

            if (IsHeartbeatStale(updatedAt))
            {
                return NewDockerWorkerStatusFromHeartbeat(
                    payload,
                    available: false,
                    state: "stale",
                    message: "Timeline Docker worker heartbeat is stale.");
            }

            return NewDockerWorkerStatusFromHeartbeat(
                payload,
                available: true,
                state: GetString(payload, "state", string.Empty),
                message: string.Empty);
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

    private static TimelineDockerWorkerStatusResponse NewDockerWorkerStatusFromHeartbeat(
        JsonObject? payload,
        bool available,
        string state,
        string message)
    {
        return new TimelineDockerWorkerStatusResponse
        {
            Available = available,
            Worker = GetString(payload, "worker", "timeline-worker"),
            State = state,
            UpdatedAt = GetString(payload, "updatedAt", string.Empty),
            WorkDirectory = GetString(payload, "workDirectory", string.Empty),
            StoreDirectory = GetString(payload, "storeDirectory", string.Empty),
            StoreAvailable = GetBool(payload, "storeAvailable", false),
            RebuildId = GetString(payload, "rebuildId", string.Empty),
            CreatedAt = GetString(payload, "createdAt", string.Empty),
            ItemCount = GetInt(payload, "itemCount", 0),
            EventCount = GetInt(payload, "eventCount", 0),
            Message = message,
        };
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
            var result = await RepairDockerWorkerCoreAsync(root, cancellationToken);
            var status = GetStatus();
            var ok = result.ExitCode == 0 && status.Available && status.State.Equals("running", StringComparison.OrdinalIgnoreCase);
            var message = ok
                ? "Timeline worker repair completed."
                : "Timeline worker repair result could not be confirmed.";

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

    private async Task<RepairScriptResult> RepairDockerWorkerCoreAsync(
        string root,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        var runtime = _settings.ReadSettings().Runtime;
        var dataRoot = _settings.GetDataRootDirectory();
        var workSource = _settings.GetWorkDirectory();
        var storeSource = _settings.GetStoreDirectory();
        var workerDirectory = _settings.GetWorkerDirectory();
        var heartbeatPath = Path.Combine(workerDirectory, "docker-worker-heartbeat.json");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(workSource);
        Directory.CreateDirectory(storeSource);
        Directory.CreateDirectory(workerDirectory);

        var docker = await InitializeDockerEngineAsync(timeout.Token);
        var repairStartedAt = DateTimeOffset.Now.AddSeconds(-2);
        var composeProjectName = GetTimelineComposeProjectName(runtime);
        var imageTag = GetTimelineImageTag(runtime, composeProjectName);
        var composePath = Path.Combine(root, "docker-compose.yml");
        if (!File.Exists(composePath))
        {
            throw new InvalidOperationException($"Timeline docker-compose.yml was not found: {composePath}");
        }

        var environment = new Dictionary<string, string>(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
        {
            ["DOCKER_CONFIG"] = GetScopedDockerConfigDir(root),
            ["TIMELINE_LOCAL_API_PORT"] = runtime.LocalApiPortStart.ToString(),
            ["TIMELINE_WEB_PORT"] = runtime.WebPort.ToString(),
            ["TIMELINE_OLLAMA_PORT"] = runtime.OllamaPort.ToString(),
            ["TIMELINE_IMAGE_TAG"] = imageTag,
            ["TIMELINE_OLLAMA_VOLUME_NAME"] = runtime.OllamaVolumeName,
            ["TIMELINE_WORK_SOURCE"] = workSource,
            ["TIMELINE_STORE_SOURCE"] = storeSource,
        };

        using var lockStream = await OpenTimelineRepairLockAsync(root, timeout.Token);
        var result = await RunTimelineProcessAsync(
            docker,
            [
                "compose",
                "-f",
                composePath,
                "-p",
                composeProjectName,
                "up",
                "-d",
                "--build",
                "worker",
            ],
            root,
            environment,
            timeout.Token);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildRepairFailureMessage("Timeline worker docker compose repair failed.", result.Stderr));
        }

        await WaitTimelineRepairWorkerHeartbeatAsync(heartbeatPath, repairStartedAt, timeout.Token);
        return result;
    }

    private static async Task<FileStream> OpenTimelineRepairLockAsync(string root, CancellationToken cancellationToken)
    {
        var generatedDir = Path.Combine(root, ".docker");
        Directory.CreateDirectory(generatedDir);
        var lockPath = Path.Combine(generatedDir, "docker-compose.lock");
        for (var attempt = 1; attempt <= 300; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new TimeoutException($"Timed out waiting for lock: {lockPath}");
    }

    private static async Task WaitTimelineRepairWorkerHeartbeatAsync(
        string heartbeatPath,
        DateTimeOffset minimumUpdatedAt,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(heartbeatPath))
            {
                try
                {
                    var payload = JsonNode.Parse(await File.ReadAllTextAsync(heartbeatPath, cancellationToken)) as JsonObject;
                    var state = GetString(payload, "state", string.Empty);
                    var updatedAtText = GetString(payload, "updatedAt", string.Empty);
                    if (state.Equals("running", StringComparison.OrdinalIgnoreCase)
                        && DateTimeOffset.TryParse(updatedAtText, out var updatedAt)
                        && updatedAt >= minimumUpdatedAt)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException("Timeline worker did not write a running heartbeat.");
    }

    private static async Task<string> InitializeDockerEngineAsync(CancellationToken cancellationToken)
    {
        var docker = ResolveDockerCommand();
        var dockerDesktop = GetDockerDesktopPath();
        if (string.IsNullOrEmpty(docker))
        {
            if (!string.IsNullOrEmpty(dockerDesktop))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dockerDesktop,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                docker = ResolveDockerCommand();
            }
        }

        if (string.IsNullOrEmpty(docker))
        {
            throw new InvalidOperationException("Docker command was not found.");
        }

        if (await TestDockerInfoAsync(docker, cancellationToken))
        {
            return docker;
        }

        if (!string.IsNullOrEmpty(dockerDesktop))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dockerDesktop,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            for (var attempt = 1; attempt <= 60; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TestDockerInfoAsync(docker, cancellationToken))
                {
                    return docker;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        var message = string.IsNullOrEmpty(dockerDesktop)
            ? "Docker engine is not ready. Start Docker and retry."
            : "Docker Desktop is installed but the Docker engine is not ready.";
        throw new InvalidOperationException(message);
    }

    private static async Task<bool> TestDockerInfoAsync(string docker, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunTimelineProcessAsync(docker, ["info"], Directory.GetCurrentDirectory(), null, cancellationToken);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<RepairScriptResult> RunTimelineProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                process.StartInfo.Environment[pair.Key] = pair.Value;
            }
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Process could not be started: {fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            TryKillProcess(process);
            throw;
        }

        return new RepairScriptResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static DockerContainerState TryGetTimelineWorkerContainerState(TimelineRuntimeSettingsResponse runtime)
    {
        var docker = ResolveDockerCommand();
        if (string.IsNullOrEmpty(docker))
        {
            return DockerContainerState.Unknown("Docker command was not found.");
        }

        var containerName = $"{GetTimelineComposeProjectName(runtime)}-worker-1";
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = docker,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.StartInfo.ArgumentList.Add("container");
        process.StartInfo.ArgumentList.Add("inspect");
        process.StartInfo.ArgumentList.Add("--format");
        process.StartInfo.ArgumentList.Add("{{.State.Status}}");
        process.StartInfo.ArgumentList.Add(containerName);

        try
        {
            if (!process.Start())
            {
                return DockerContainerState.Unknown("Docker inspect could not be started.");
            }

            if (!process.WaitForExit(1500))
            {
                TryKillProcess(process);
                return DockerContainerState.Unknown("Docker inspect timed out.");
            }

            var stdout = process.StandardOutput.ReadToEnd().Trim();
            var stderr = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0)
            {
                if (stderr.Contains("No such object", StringComparison.OrdinalIgnoreCase))
                {
                    return DockerContainerState.NotRunning(
                        "not_found",
                        "Timeline Docker worker container was not found.");
                }

                return DockerContainerState.Unknown(ConvertTimelineText(stderr));
            }

            var state = string.IsNullOrWhiteSpace(stdout) ? "unknown" : stdout;
            return state.Equals("running", StringComparison.OrdinalIgnoreCase)
                ? DockerContainerState.RunningState(state)
                : DockerContainerState.NotRunning(
                    state,
                    $"Timeline Docker worker container is not running. Docker state: {state}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return DockerContainerState.Unknown(ex.Message);
        }
    }

    private static bool IsDockerEngineUnavailable(string message)
    {
        var text = ConvertTimelineText(message);
        return text.Contains("docker API", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Docker daemon", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Docker engine is not ready", StringComparison.OrdinalIgnoreCase)
            || text.Contains("The system cannot find the file specified", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pipe/docker", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDockerCommand()
    {
        var dockerCommandName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "docker.exe" : "docker";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var dockerExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Docker",
                "Docker",
                "resources",
                "bin",
                "docker.exe");
            if (File.Exists(dockerExe))
            {
                return dockerExe;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(entry, dockerCommandName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string GetDockerDesktopPath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return string.Empty;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Docker", "Docker", "Docker Desktop.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static string GetScopedDockerConfigDir(string root)
    {
        var configDir = Path.Combine(root, ".docker", "docker-config");
        var configPath = Path.Combine(configDir, "config.json");
        Directory.CreateDirectory(configDir);
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, "{}", Encoding.ASCII);
        }

        return configDir;
    }

    private static string GetTimelineComposeProjectName(TimelineRuntimeSettingsResponse runtime)
    {
        var instancePart = NormalizeRuntimeNamePart(runtime.InstanceName);
        return string.IsNullOrEmpty(instancePart) ? "timeline" : $"timeline-{instancePart}";
    }

    private static string GetTimelineImageTag(TimelineRuntimeSettingsResponse runtime, string composeProjectName)
    {
        var imageTag = NormalizeRuntimeResourceName(runtime.ImageTag);
        if (!string.IsNullOrEmpty(imageTag))
        {
            return imageTag;
        }

        return composeProjectName.Equals("timeline", StringComparison.OrdinalIgnoreCase)
            ? "latest"
            : composeProjectName;
    }

    private static string NormalizeRuntimeNamePart(string value)
    {
        var text = ConvertTimelineText(value).ToLowerInvariant();
        return string.IsNullOrEmpty(text)
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(text, "[^a-z0-9]+", "-").Trim('-');
    }

    private static string NormalizeRuntimeResourceName(string value)
    {
        var text = ConvertTimelineText(value).ToLowerInvariant();
        return string.IsNullOrEmpty(text)
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(text, "[^a-z0-9_.-]+", "-").Trim('-');
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

    private sealed record DockerContainerState(
        bool Known,
        bool Running,
        string State,
        string Message)
    {
        public static DockerContainerState RunningState(string state)
            => new(true, true, state, string.Empty);

        public static DockerContainerState NotRunning(string state, string message)
            => new(true, false, state, message);

        public static DockerContainerState Unknown(string message)
            => new(false, false, "unknown", message);
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
