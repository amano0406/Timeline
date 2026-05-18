using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public sealed class TimelineProductRuntimeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] ProductIds =
    [
        "audio",
        "windows-codex",
        "chatgpt",
        "image",
        "video",
        "pc",
    ];

    private static readonly Dictionary<string, (string DisplayName, string Description, string PagePath)> ProductMetadata =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["audio"] = ("TimelineForAudio", "audio", "audio/files"),
            ["windows-codex"] = ("TimelineForWindowsCodex", "codex", "windows-codex"),
            ["chatgpt"] = ("TimelineForChatGPT", "chatgpt", "chatgpt"),
            ["image"] = ("TimelineForImage", "image", "image"),
            ["video"] = ("TimelineForVideo", "video", "video"),
            ["pc"] = ("TimelineForPC", "pc", "pc"),
        };

    private readonly TimelineSettingsService _settings;
    private readonly TimelineProductSettingsService _productSettings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineLocalApiOptions _options;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, CachedLatestVersion> _latestVersionCache = new(StringComparer.OrdinalIgnoreCase);

    public TimelineProductRuntimeService(
        TimelineSettingsService settings,
        TimelineProductSettingsService productSettings,
        TimelineOperationLogService operations,
        TimelineLocalApiOptions options,
        HttpClient http)
    {
        _settings = settings;
        _productSettings = productSettings;
        _operations = operations;
        _options = options;
        _http = http;
    }

    public async Task<ProductRuntimeOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var definitions = GetProductDefinitions();
        var rows = await Task.WhenAll(definitions.Select(definition => ConvertRuntimeStatusAsync(definition, cancellationToken)));
        return new ProductRuntimeOverviewResponse
        {
            Products = rows.ToList(),
            Message = string.Empty,
        };
    }

    public async Task<ProductRuntimeRowResponse> StartProductAsync(
        string productId,
        CancellationToken cancellationToken)
    {
        return await InvokeProductWebOperationAsync(
            productId,
            "product_start",
            async (definition, operationId) => await StartProductCoreAsync(definition, restart: false, operationId, cancellationToken),
            cancellationToken);
    }

    public async Task<ProductRuntimeRowResponse> RestartProductAsync(
        string productId,
        CancellationToken cancellationToken)
    {
        return await InvokeProductWebOperationAsync(
            productId,
            "product_restart",
            async (definition, operationId) => await StartProductCoreAsync(definition, restart: true, operationId, cancellationToken),
            cancellationToken);
    }

    public async Task<ProductRuntimeRowResponse> StopProductAsync(
        string productId,
        CancellationToken cancellationToken)
    {
        return await InvokeProductWebOperationAsync(
            productId,
            "product_stop",
            async (definition, operationId) => await StopProductCoreAsync(definition, operationId, cancellationToken),
            cancellationToken);
    }

    public async Task<ProductRuntimeRowResponse> InstallProductAsync(
        string productId,
        JsonObject? request,
        CancellationToken cancellationToken)
    {
        var options = GetProductInstallOptions(request);
        var definition = GetProductDefinition(productId);
        var productPath = Path.GetFullPath(definition.ProductPath);
        AssertProductAppManagedByTimeline(productPath, "Install");

        if (Directory.Exists(productPath) && Directory.EnumerateFileSystemEntries(productPath).Any())
        {
            return await ConvertRuntimeStatusAsync(definition, cancellationToken);
        }

        var parent = Path.GetDirectoryName(productPath);
        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidOperationException("Product parent directory could not be resolved.");
        }
        Directory.CreateDirectory(parent);
        if (Directory.Exists(productPath) && Directory.EnumerateFileSystemEntries(productPath).Any())
        {
            throw new InvalidOperationException($"Product directory is not empty: {productPath}");
        }

        WriteRuntimeState(definition.Id, "installing", message: "Installing product.");
        ProductSourceArchiveStage? stage = null;
        try
        {
            stage = await NewProductSourceArchiveStageAsync(definition, cancellationToken);
            if (Directory.Exists(productPath))
            {
                Directory.Delete(productPath, recursive: true);
            }
            Directory.Move(stage.SourceRoot, productPath);
            AssertProductPathDeleteSafe(definition.Id, productPath);
            WriteProductInstallState(definition.Id, stage.LatestVersion, stage.SourceUrl, stage.ArchiveUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            WriteRuntimeState(definition.Id, "failed", message: ex.Message);
            throw new InvalidOperationException($"{definition.DisplayName} install failed: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                if (stage is not null && Directory.Exists(stage.StageRoot))
                {
                    Directory.Delete(stage.StageRoot, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        definition = GetProductDefinition(productId);
        var restoredSettingsPath = string.Empty;
        if (options.RestoreSettingsBackup)
        {
            restoredSettingsPath = RestoreProductSettingsBackup(definition);
        }
        if (string.IsNullOrEmpty(restoredSettingsPath))
        {
            try
            {
                InitializeProductSettingsFromApp(definition);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                WriteRuntimeState(definition.Id, "failed", message: ex.Message);
                throw new InvalidOperationException($"{definition.DisplayName} initial settings failed: {ex.Message}", ex);
            }
        }

        var installedMessage = string.IsNullOrEmpty(restoredSettingsPath)
            ? "Product installed. Settings initialized."
            : "Product installed. Settings restored.";
        WriteRuntimeState(definition.Id, "stopped", message: installedMessage);
        return await ConvertRuntimeStatusAsync(definition, cancellationToken);
    }

    public async Task<ProductRuntimeRowResponse> UpdateProductAsync(
        string productId,
        CancellationToken cancellationToken)
    {
        var definition = GetProductDefinition(productId);
        var status = await ConvertRuntimeStatusAsync(definition, cancellationToken);
        if (!status.ProductFound)
        {
            throw new InvalidOperationException("Product is not installed.");
        }
        if (!status.ComposeFound)
        {
            throw new InvalidOperationException("Product is incomplete and cannot be updated safely.");
        }

        var productPath = Path.GetFullPath(definition.ProductPath);
        AssertProductAppManagedByTimeline(productPath, "Update");
        AssertProductPathDeleteSafe(definition.Id, productPath);
        if (!await TestProductGitWorktreeCleanAsync(productPath, cancellationToken))
        {
            throw new InvalidOperationException("Product has local Git changes. Commit or discard them before updating.");
        }

        var source = await GetProductSourceInfoAsync(definition, cancellationToken);
        var installedVersion = await GetProductInstalledVersionAsync(definition, cancellationToken);
        if (!string.IsNullOrEmpty(installedVersion)
            && CompareVersionText(installedVersion, source.LatestVersion) >= 0)
        {
            return status;
        }

        var wasRunning = status.Running;
        if (wasRunning)
        {
            _ = await StopProductCoreAsync(
                definition,
                _operations.NewOperationId("product-update-stop"),
                cancellationToken);
            definition = GetProductDefinition(productId);
            productPath = Path.GetFullPath(definition.ProductPath);
        }

        var plan = BuildProductUninstallPlan(
            definition,
            new JsonObject
            {
                ["keepSettings"] = true,
                ["removeGeneratedData"] = false,
            });
        _ = BackupProductSettingsForUninstall(plan);

        WriteRuntimeState(definition.Id, "updating", message: "Updating product.");
        ProductSourceArchiveStage? stage = null;
        var oldPath = string.Empty;
        var newInstalled = false;
        try
        {
            stage = await NewProductSourceArchiveStageAsync(definition, cancellationToken);
            var parent = Path.GetDirectoryName(productPath);
            if (string.IsNullOrEmpty(parent))
            {
                throw new InvalidOperationException("Product parent directory could not be resolved.");
            }

            oldPath = Path.Combine(
                parent,
                "." + Path.GetFileName(productPath) + ".timeline-old-"
                    + DateTime.Now.ToString("yyyyMMddHHmmss")
                    + "-"
                    + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.Move(productPath, oldPath);
            Directory.Move(stage.SourceRoot, productPath);
            newInstalled = true;
            AssertProductPathDeleteSafe(definition.Id, productPath);
            definition = GetProductDefinition(productId);
            _ = RestoreProductSettingsBackup(definition);
            WriteProductInstallState(definition.Id, stage.LatestVersion, stage.SourceUrl, stage.ArchiveUrl);
            WriteRuntimeState(definition.Id, "stopped", message: "Product updated.");

            if (wasRunning)
            {
                _ = await StartProductCoreAsync(
                    definition,
                    restart: false,
                    _operations.NewOperationId("product-update-start"),
                    cancellationToken);
            }

            if (!string.IsNullOrEmpty(oldPath) && Directory.Exists(oldPath))
            {
                Directory.Delete(oldPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (newInstalled && Directory.Exists(productPath))
            {
                TryDeleteDirectory(productPath);
            }
            if (!string.IsNullOrEmpty(oldPath)
                && Directory.Exists(oldPath)
                && !Directory.Exists(productPath))
            {
                try
                {
                    Directory.Move(oldPath, productPath);
                }
                catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                {
                }
            }
            WriteRuntimeState(definition.Id, "failed", message: ex.Message);
            throw new InvalidOperationException($"{definition.DisplayName} update failed: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                if (stage is not null && Directory.Exists(stage.StageRoot))
                {
                    Directory.Delete(stage.StageRoot, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        definition = GetProductDefinition(productId);
        return await ConvertRuntimeStatusAsync(definition, cancellationToken);
    }

    public ProductUninstallPlanResponse GetProductUninstallPlan(
        string productId,
        JsonObject? request)
    {
        var definition = GetProductDefinition(productId);
        return BuildProductUninstallPlan(definition, request);
    }

    public async Task<ProductRuntimeRowResponse> UninstallProductAsync(
        string productId,
        JsonObject? request,
        CancellationToken cancellationToken)
    {
        var definition = GetProductDefinition(productId);
        var productPath = Path.GetFullPath(definition.ProductPath);
        AssertProductAppManagedByTimeline(productPath, "Uninstall");

        var status = await ConvertRuntimeStatusAsync(definition, cancellationToken);
        if (status.Running)
        {
            _ = await StopProductCoreAsync(
                definition,
                _operations.NewOperationId("product-uninstall-stop"),
                cancellationToken);
            definition = GetProductDefinition(productId);
            productPath = Path.GetFullPath(definition.ProductPath);
        }

        AssertProductPathDeleteSafe(definition.Id, productPath);
        if (!await TestProductGitWorktreeCleanAsync(productPath, cancellationToken))
        {
            throw new InvalidOperationException("Product has local Git changes. Commit or discard them before uninstalling.");
        }

        var plan = BuildProductUninstallPlan(definition, request);
        AssertProductManagedDeletePathSafe(productPath, productPath);
        var sourcePaths = GetProductSourceDataPaths(definition);
        foreach (var row in plan.GeneratedData)
        {
            if (!row.WillDelete)
            {
                continue;
            }

            AssertProductGeneratedPathNotSource(row.Path, sourcePaths);
            AssertProductManagedDeletePathSafe(row.Path, productPath);
        }

        foreach (var resource in plan.RuntimeData.Resources)
        {
            if (!resource.WillDelete)
            {
                continue;
            }

            var runtimeLocalPath = ConvertTimelineWindowsPath(resource.Path);
            if (string.IsNullOrEmpty(runtimeLocalPath))
            {
                runtimeLocalPath = resource.Path;
            }
            if (string.IsNullOrWhiteSpace(runtimeLocalPath))
            {
                throw new InvalidOperationException("Runtime data delete path is empty.");
            }

            AssertProductGeneratedPathNotSource(runtimeLocalPath, sourcePaths);
            AssertProductManagedDeletePathSafe(runtimeLocalPath, productPath);
        }

        var operationId = _operations.NewOperationId("product-uninstall");
        var commandLine = "Remove-Item " + QuoteArgument(productPath) + " -Recurse -Force";
        _operations.WriteOperationEvent(
            operationId,
            "command",
            definition.DisplayName,
            "product_uninstall",
            "info",
            "Product uninstall start.",
            commandLine: commandLine);
        _operations.WriteOperationEvent(
            operationId,
            "plan",
            definition.DisplayName,
            "product_uninstall_plan",
            "ready",
            "Product uninstall plan created.",
            details: JsonSerializer.SerializeToNode(plan, JsonOptions));

        var startedAt = DateTimeOffset.Now;
        try
        {
            WriteRuntimeState(definition.Id, "uninstalling", message: "Uninstalling product.");
            WriteUninstallStepEvent(
                operationId,
                definition.DisplayName,
                "product_uninstall_settings_backup",
                "running",
                "Product settings backup step started.",
                JsonSerializer.SerializeToNode(plan.Settings, JsonOptions));
            var backupPath = BackupProductSettingsForUninstall(plan);
            WriteUninstallStepEvent(
                operationId,
                definition.DisplayName,
                "product_uninstall_settings_backup",
                "completed",
                "Product settings backup step completed.",
                new JsonObject { ["backupPath"] = backupPath });

            foreach (var row in plan.GeneratedData)
            {
                if (!row.WillDelete || !Directory.Exists(row.Path))
                {
                    continue;
                }

                var details = JsonSerializer.SerializeToNode(row, JsonOptions);
                WriteUninstallStepEvent(
                    operationId,
                    definition.DisplayName,
                    "product_uninstall_generated_delete",
                    "running",
                    "Generated data delete step started.",
                    details);
                Directory.Delete(row.Path, recursive: true);
                WriteUninstallStepEvent(
                    operationId,
                    definition.DisplayName,
                    "product_uninstall_generated_delete",
                    "completed",
                    "Generated data delete step completed.",
                    details);
            }

            foreach (var resource in plan.RuntimeData.Resources)
            {
                if (!resource.WillDelete)
                {
                    continue;
                }

                var runtimeLocalPath = ConvertTimelineWindowsPath(resource.Path);
                if (string.IsNullOrEmpty(runtimeLocalPath))
                {
                    runtimeLocalPath = resource.Path;
                }

                var details = JsonSerializer.SerializeToNode(resource, JsonOptions);
                if (Directory.Exists(runtimeLocalPath))
                {
                    WriteUninstallStepEvent(
                        operationId,
                        definition.DisplayName,
                        "product_uninstall_runtime_delete",
                        "running",
                        "Runtime data delete step started.",
                        details);
                    Directory.Delete(runtimeLocalPath, recursive: true);
                    WriteUninstallStepEvent(
                        operationId,
                        definition.DisplayName,
                        "product_uninstall_runtime_delete",
                        "completed",
                        "Runtime data delete step completed.",
                        details);
                }
                else if (File.Exists(runtimeLocalPath))
                {
                    WriteUninstallStepEvent(
                        operationId,
                        definition.DisplayName,
                        "product_uninstall_runtime_delete",
                        "running",
                        "Runtime data delete step started.",
                        details);
                    File.Delete(runtimeLocalPath);
                    WriteUninstallStepEvent(
                        operationId,
                        definition.DisplayName,
                        "product_uninstall_runtime_delete",
                        "completed",
                        "Runtime data delete step completed.",
                        details);
                }
            }

            WriteUninstallStepEvent(
                operationId,
                definition.DisplayName,
                "product_uninstall_app_delete",
                "running",
                "Product application delete step started.",
                JsonSerializer.SerializeToNode(plan.AppDirectory, JsonOptions));
            Directory.Delete(productPath, recursive: true);
            WriteUninstallStepEvent(
                operationId,
                definition.DisplayName,
                "product_uninstall_app_delete",
                "completed",
                "Product application delete step completed.",
                JsonSerializer.SerializeToNode(plan.AppDirectory, JsonOptions));

            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            WriteRuntimeState(definition.Id, "not-created", message: "Product uninstalled.");
            _operations.WriteOperationEvent(
                operationId,
                "result",
                definition.DisplayName,
                "product_uninstall",
                "success",
                "Product uninstalled.",
                commandLine: commandLine,
                exitCode: 0,
                durationMs: durationMs,
                stdout: JsonSerializer.Serialize(plan, JsonOptions));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            WriteRuntimeState(definition.Id, "failed", message: ex.Message);
            _operations.WriteOperationEvent(
                operationId,
                "result",
                definition.DisplayName,
                "product_uninstall",
                "error",
                "Product uninstall failed.",
                commandLine: commandLine,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }

        definition = GetProductDefinition(productId);
        return await ConvertRuntimeStatusAsync(definition, cancellationToken);
    }

    private async Task<ProductRuntimeRowResponse> InvokeProductWebOperationAsync(
        string productId,
        string action,
        Func<ProductRuntimeDefinition, string, Task<ProductRuntimeRowResponse>> operation,
        CancellationToken cancellationToken)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "Timeline",
            action,
            "started",
            "Web operation started.");

        try
        {
            var definition = GetProductDefinition(productId);
            var result = await operation(definition, operationId);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "Timeline",
                action,
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: ConvertRuntimeRowDetails(result));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "Timeline",
                action,
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private async Task<ProductRuntimeRowResponse> StartProductCoreAsync(
        ProductRuntimeDefinition definition,
        bool restart,
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        var productPath = definition.ProductPath;
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            throw new InvalidOperationException($"Product directory was not found: {productPath}");
        }

        if (restart && File.Exists(definition.StopPath))
        {
            WriteRuntimeState(definition.Id, "restarting", message: "Restarting product.");
            await RunLoggedProcessAsync(
                definition,
                definition.StopPath,
                timeoutSeconds: 180,
                parentOperationId,
                cancellationToken);
        }

        if (File.Exists(definition.StartPath))
        {
            if (!restart)
            {
                WriteRuntimeState(definition.Id, "starting", message: "Starting product.");
            }

            var result = await RunLoggedProcessAsync(
                definition,
                definition.StartPath,
                timeoutSeconds: 240,
                parentOperationId,
                cancellationToken);
            if (result.ExitCode != 0 && !IsProductStartOutputSuccess(result.Stdout + "\n" + result.Stderr))
            {
                var message = !string.IsNullOrWhiteSpace(result.Stderr)
                    ? result.Stderr.Trim()
                    : !string.IsNullOrWhiteSpace(result.Stdout)
                        ? result.Stdout.Trim()
                        : $"exit code {result.ExitCode}";
                throw new InvalidOperationException($"{definition.DisplayName} start failed: {message}");
            }

            WriteRuntimeState(
                definition.Id,
                "running",
                DateTimeOffset.Now.ToString("o"),
                "Product started.");
            return await WaitForProductRuntimeStateAsync(
                definition,
                expectedRunning: true,
                restart ? "restart" : "start",
                TimeSpan.FromSeconds(45),
                cancellationToken);
        }

        throw new InvalidOperationException($"Product start script was not found: {definition.StartPath}");
    }

    private async Task<ProductRuntimeRowResponse> StopProductCoreAsync(
        ProductRuntimeDefinition definition,
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        var productPath = definition.ProductPath;
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            throw new InvalidOperationException($"Product directory was not found: {productPath}");
        }
        if (!File.Exists(definition.StopPath))
        {
            throw new InvalidOperationException($"Product stop script was not found: {definition.StopPath}");
        }

        WriteRuntimeState(definition.Id, "stopping", message: "Stopping product.");
        var result = await RunLoggedProcessAsync(
            definition,
            definition.StopPath,
            timeoutSeconds: 180,
            parentOperationId,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            var message = !string.IsNullOrWhiteSpace(result.Stderr)
                ? result.Stderr.Trim()
                : !string.IsNullOrWhiteSpace(result.Stdout)
                    ? result.Stdout.Trim()
                    : $"exit code {result.ExitCode}";
            WriteRuntimeState(definition.Id, "failed", message: message);
            throw new InvalidOperationException($"{definition.DisplayName} stop failed: {message}");
        }

        WriteRuntimeState(definition.Id, "stopped", message: "Product stopped.");
        return await WaitForProductRuntimeStateAsync(
            definition,
            expectedRunning: false,
            "stop",
            TimeSpan.FromSeconds(30),
            cancellationToken);
    }

    private async Task<ProductRuntimeRowResponse> WaitForProductRuntimeStateAsync(
        ProductRuntimeDefinition definition,
        bool expectedRunning,
        string action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now.Add(timeout);
        ProductRuntimeRowResponse status;
        do
        {
            status = await ConvertRuntimeStatusAsync(definition, cancellationToken);
            if (status.Running == expectedRunning)
            {
                return status;
            }

            if (DateTimeOffset.Now >= deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        while (true);

        var expectedState = expectedRunning ? "running" : "stopped";
        var message = !string.IsNullOrWhiteSpace(status.Message)
            ? status.Message
            : $"Product did not become {expectedState}.";
        WriteRuntimeState(definition.Id, "failed", message: message);
        throw new InvalidOperationException($"{definition.DisplayName} {action} failed: {message}");
    }

    private async Task<ProcessRunResult> RunLoggedProcessAsync(
        ProductRuntimeDefinition definition,
        string scriptPath,
        int timeoutSeconds,
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        var powershell = GetPowerShellPath();
        var arguments = new[]
        {
            "-NoLogo",
            "-NoProfile",
            "-WindowStyle",
            "Hidden",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
        };
        var operationId = _operations.NewOperationId("launcher");
        var commandLine = BuildCommandLine(powershell, arguments);
        var startedAt = DateTimeOffset.Now;

        _operations.WriteOperationEvent(
            operationId,
            "command",
            definition.DisplayName,
            "launcher",
            "info",
            "Product launcher started.",
            commandLine: commandLine,
            parentOperationId: parentOperationId);

        try
        {
            var result = await RunProcessAsync(
                powershell,
                arguments,
                definition.ProductPath,
                timeoutSeconds,
                GetChildProcessEnvironment(),
                cancellationToken);

            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "result",
                definition.DisplayName,
                "launcher",
                result.ExitCode == 0 ? "success" : "error",
                result.ExitCode == 0 ? "Product launcher completed." : "Product launcher failed.",
                commandLine: commandLine,
                exitCode: result.ExitCode,
                durationMs: durationMs,
                stdout: result.Stdout,
                stderr: result.Stderr,
                parentOperationId: parentOperationId);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "result",
                definition.DisplayName,
                "launcher",
                "error",
                "Product launcher execution error.",
                commandLine: commandLine,
                durationMs: durationMs,
                stderr: ex.Message,
                parentOperationId: parentOperationId);
            throw;
        }
    }

    private static async Task<ProcessRunResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = new UTF8Encoding(false);
        process.StartInfo.StandardErrorEncoding = new UTF8Encoding(false);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        foreach (var pair in environment)
        {
            process.StartInfo.Environment[pair.Key] = pair.Value;
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
            }

            throw new TimeoutException($"{fileName} timed out after {timeoutSeconds} seconds.");
        }

        return new ProcessRunResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private void WriteRuntimeState(
        string productId,
        string state,
        string startedAt = "",
        string message = "")
    {
        var path = GetProductRuntimeStatePath(productId);
        var payload = new JsonObject
        {
            ["productId"] = productId,
            ["state"] = state,
            ["startedAt"] = startedAt,
            ["updatedAt"] = DateTimeOffset.Now.ToString("o"),
            ["message"] = message,
        };

        File.WriteAllText(path, payload.ToJsonString(), new UTF8Encoding(false));
    }

    private JsonObject ConvertRuntimeRowDetails(ProductRuntimeRowResponse row)
    {
        return new JsonObject
        {
            ["state"] = row.State,
            ["message"] = row.Message,
        };
    }

    private ProductInstallOptions GetProductInstallOptions(JsonObject? request)
    {
        return new ProductInstallOptions(
            GetBool(GetNode(request, "restoreSettingsBackup"), true));
    }

    private async Task<ProductSourceArchiveStage> NewProductSourceArchiveStageAsync(
        ProductRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        var source = await GetProductSourceInfoAsync(definition, cancellationToken);
        var operationId = _operations.NewOperationId("product-install");
        var stageRoot = Path.Combine(_settings.GetWorkDirectory(), "product-installs", operationId);
        var extractRoot = Path.Combine(stageRoot, "extract");
        Directory.CreateDirectory(extractRoot);
        var archivePath = Path.Combine(stageRoot, "source.zip");
        var commandLine = "Download " + QuoteArgument(source.ArchiveUrl);
        var startedAt = DateTimeOffset.Now;

        _operations.WriteOperationEvent(
            operationId,
            "command",
            definition.DisplayName,
            "product_source_download",
            "info",
            "Product source download start.",
            commandLine: commandLine);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var response = await _http.GetAsync(source.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var sourceStream = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var targetStream = File.Create(archivePath))
            {
                await sourceStream.CopyToAsync(targetStream, timeout.Token);
            }

            ZipFile.ExtractToDirectory(archivePath, extractRoot);
            var sourceRoots = Directory
                .EnumerateDirectories(extractRoot)
                .Where(path => !Path.GetFileName(path).Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sourceRoots.Count != 1)
            {
                throw new InvalidOperationException("Source archive did not contain one product root directory.");
            }

            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "result",
                definition.DisplayName,
                "product_source_download",
                "success",
                "Product source downloaded.",
                commandLine: commandLine,
                exitCode: 0,
                durationMs: durationMs);

            return new ProductSourceArchiveStage(
                operationId,
                stageRoot,
                sourceRoots[0],
                archivePath,
                source.SourceUrl,
                source.ArchiveUrl,
                source.LatestVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "result",
                definition.DisplayName,
                "product_source_download",
                "error",
                "Product source download failed.",
                commandLine: commandLine,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private void WriteProductInstallState(
        string productId,
        string version,
        string sourceUrl,
        string archiveUrl)
    {
        var path = GetProductInstallStatePath(productId);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var payload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["productId"] = productId,
            ["version"] = version,
            ["sourceUrl"] = sourceUrl,
            ["archiveUrl"] = archiveUrl,
            ["installedAt"] = DateTimeOffset.Now.ToString("o"),
        };
        File.WriteAllText(path, payload.ToJsonString(), new UTF8Encoding(false));
    }

    private string RestoreProductSettingsBackup(ProductRuntimeDefinition definition)
    {
        var productPath = definition.ProductPath;
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            return string.Empty;
        }

        var backupPath = Path.Combine(GetProductBackupRoot(definition.Id), "settings", "settings.json");
        if (!File.Exists(backupPath))
        {
            return string.Empty;
        }

        var targetPath = Path.Combine(productPath, "settings.json");
        if (File.Exists(targetPath))
        {
            return string.Empty;
        }

        File.Copy(backupPath, targetPath, overwrite: true);
        return targetPath;
    }

    private void InitializeProductSettingsFromApp(ProductRuntimeDefinition definition)
    {
        var productId = definition.Id.ToLowerInvariant();
        var outputDirectory = GetManagedProductDataDirectory(productId);
        var outputRoot = NewInitialOutputRoot(outputDirectory);
        var computeMode = GetInitialProductComputeMode();

        switch (productId)
        {
            case "audio":
                _productSettings.SaveAudioSettings(new JsonObject
                {
                    ["inputRoots"] = new JsonArray(GetInitialProductInputDirectory(productId)),
                    ["outputRoot"] = outputRoot.DeepClone(),
                    ["outputPath"] = outputDirectory,
                    ["computeMode"] = computeMode,
                });
                break;
            case "image":
                _productSettings.SaveImageSettings(new JsonObject
                {
                    ["inputRoots"] = new JsonArray(GetInitialProductInputDirectory(productId)),
                    ["outputRoot"] = outputRoot.DeepClone(),
                    ["outputRootPath"] = outputDirectory,
                });
                break;
            case "video":
                _productSettings.SaveVideoSettings(new JsonObject
                {
                    ["inputRoots"] = new JsonArray(GetInitialProductInputDirectory(productId)),
                    ["outputRoot"] = outputRoot.DeepClone(),
                    ["outputRootPath"] = outputDirectory,
                    ["computeMode"] = computeMode,
                });
                break;
            case "windows-codex":
                _productSettings.SaveWindowsCodexSettings(new JsonObject
                {
                    ["outputRoot"] = outputDirectory,
                    ["outputsRoot"] = outputDirectory,
                });
                break;
            case "chatgpt":
                _productSettings.SaveChatGptSettings(new JsonObject
                {
                    ["outputRoot"] = outputRoot.DeepClone(),
                    ["outputRootPath"] = outputDirectory,
                    ["masterRoot"] = outputRoot.DeepClone(),
                    ["masterRootPath"] = outputDirectory,
                });
                break;
            case "pc":
                _productSettings.SavePcSettings(new JsonObject
                {
                    ["outputRoot"] = outputDirectory,
                    ["outputRootPath"] = outputDirectory,
                });
                break;
        }
    }

    private string GetInitialProductInputDirectory(string productId)
    {
        var path = Path.Combine(_settings.GetDataRootDirectory(), "input", GetSafeSegment(productId, "_"));
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private string GetManagedProductDataDirectory(string productId)
    {
        return Path.Combine(_settings.GetDataRootDirectory(), "to_text", productId);
    }

    private string GetInitialProductComputeMode()
    {
        var commonAi = _settings.ReadSettings().CommonAi;
        var computeMode = ConvertTimelineText(commonAi.ComputeMode).ToLowerInvariant();
        return computeMode is "cpu" or "gpu" ? computeMode : "gpu";
    }

    private static JsonObject NewInitialOutputRoot(string path)
    {
        return new JsonObject
        {
            ["id"] = "output",
            ["displayName"] = "Output",
            ["path"] = path,
            ["enabled"] = true,
        };
    }

    private ProductUninstallPlanResponse BuildProductUninstallPlan(
        ProductRuntimeDefinition definition,
        JsonObject? request)
    {
        var options = GetProductUninstallOptions(request);
        var productPath = string.IsNullOrWhiteSpace(definition.ProductPath)
            ? string.Empty
            : Path.GetFullPath(definition.ProductPath);
        var settingsPath = GetProductSettingsFilePath(definition);
        var settingsBackupPath = string.IsNullOrEmpty(settingsPath)
            ? string.Empty
            : Path.Combine(GetProductBackupRoot(definition.Id), "settings", "settings.json");
        var generatedPaths = GetProductGeneratedDataPaths(definition);
        var appManagedByTimeline = IsProductAppManagedByTimeline(productPath);

        var generatedRows = new List<ProductUninstallPathPlanResponse>();
        long generatedTotalBytes = 0;
        foreach (var path in generatedPaths)
        {
            var exists = Directory.Exists(path);
            var sizeBytes = exists ? GetDirectorySizeBytes(path) : 0;
            if (options.RemoveGeneratedData)
            {
                generatedTotalBytes += sizeBytes;
            }

            generatedRows.Add(new ProductUninstallPathPlanResponse
            {
                Path = path,
                Exists = exists,
                SizeBytes = sizeBytes,
                WillDelete = options.RemoveGeneratedData,
            });
        }

        var appExists = Directory.Exists(productPath);
        var appSizeBytes = appExists ? GetDirectorySizeBytes(productPath) : 0;
        var appWillDelete = appManagedByTimeline && appExists;
        var settingsExists = !string.IsNullOrEmpty(settingsPath) && File.Exists(settingsPath);
        var settingsSizeBytes = settingsExists ? GetFileSizeBytes(settingsPath) : 0;
        var runtimeData = GetProductRuntimeDataPlan(definition);
        var totalDeleteBytes = generatedTotalBytes;
        if (appWillDelete)
        {
            totalDeleteBytes += appSizeBytes;
        }
        if (runtimeData.WillDelete)
        {
            totalDeleteBytes += runtimeData.SizeBytes;
        }

        var warnings = new List<string>();
        if (appExists && !appManagedByTimeline)
        {
            warnings.Add("Product app path is outside Timeline-managed products. App uninstall is disabled for this placement.");
        }
        if (!string.IsNullOrEmpty(runtimeData.Message) && runtimeData.UsesDocker && !runtimeData.ManagedByTimeline)
        {
            warnings.Add(runtimeData.Message);
        }

        return new ProductUninstallPlanResponse
        {
            ProductId = definition.Id,
            DisplayName = definition.DisplayName,
            ProductPath = productPath,
            KeepSettings = options.KeepSettings,
            RemoveGeneratedData = options.RemoveGeneratedData,
            TotalDeleteBytes = totalDeleteBytes,
            AppDirectory = new ProductUninstallPathPlanResponse
            {
                Path = productPath,
                Exists = appExists,
                SizeBytes = appSizeBytes,
                WillDelete = appWillDelete,
            },
            Settings = new ProductUninstallSettingsPlanResponse
            {
                Path = settingsPath,
                Exists = settingsExists,
                SizeBytes = settingsSizeBytes,
                WillBackup = options.KeepSettings && settingsExists,
                BackupPath = settingsBackupPath,
                WillDeleteBackup = !options.KeepSettings,
            },
            GeneratedData = generatedRows,
            RuntimeData = runtimeData,
            Warnings = warnings,
        };
    }

    private ProductUninstallOptions GetProductUninstallOptions(JsonObject? request)
    {
        return new ProductUninstallOptions(
            GetBool(GetNode(request, "keepSettings"), true),
            GetBool(GetNode(request, "removeGeneratedData"), false));
    }

    private string BackupProductSettingsForUninstall(ProductUninstallPlanResponse plan)
    {
        if (!plan.Settings.WillBackup
            || string.IsNullOrEmpty(plan.Settings.Path)
            || string.IsNullOrEmpty(plan.Settings.BackupPath)
            || !File.Exists(plan.Settings.Path))
        {
            return string.Empty;
        }

        var backupDirectory = Path.GetDirectoryName(plan.Settings.BackupPath);
        if (string.IsNullOrEmpty(backupDirectory))
        {
            return string.Empty;
        }

        Directory.CreateDirectory(backupDirectory);
        File.Copy(plan.Settings.Path, plan.Settings.BackupPath, overwrite: true);

        var historyPath = Path.Combine(backupDirectory, $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Copy(plan.Settings.Path, historyPath, overwrite: true);

        var metadataRoot = Path.GetDirectoryName(backupDirectory);
        if (!string.IsNullOrEmpty(metadataRoot))
        {
            var metadataPath = Path.Combine(metadataRoot, "metadata.json");
            var metadata = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["productId"] = plan.ProductId,
                ["displayName"] = plan.DisplayName,
                ["productPath"] = plan.ProductPath,
                ["settingsPath"] = plan.Settings.Path,
                ["backupPath"] = plan.Settings.BackupPath,
                ["latestHistoryPath"] = historyPath,
                ["backedUpAt"] = DateTimeOffset.Now.ToString("o"),
            };
            File.WriteAllText(metadataPath, metadata.ToJsonString(), new UTF8Encoding(false));
        }

        return plan.Settings.BackupPath;
    }

    private void WriteUninstallStepEvent(
        string operationId,
        string productName,
        string action,
        string state,
        string message,
        JsonNode? details)
    {
        _operations.WriteOperationEvent(
            operationId,
            "step",
            productName,
            action,
            state,
            message,
            details: details);
    }

    private async Task<bool> TestProductGitWorktreeCleanAsync(
        string productPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(Path.Combine(productPath, ".git")))
        {
            return true;
        }

        try
        {
            var result = await RunProcessAsync(
                "git",
                ["status", "--porcelain"],
                productPath,
                timeoutSeconds: 30,
                GetChildProcessEnvironment(),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("git status failed.");
            }

            return string.IsNullOrWhiteSpace(result.Stdout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Could not verify product Git state: {ex.Message}", ex);
        }
    }

    private void AssertProductAppManagedByTimeline(string productPath, string action)
    {
        if (!IsProductAppManagedByTimeline(productPath))
        {
            throw new InvalidOperationException($"Product path is outside Timeline-managed products. {action} is disabled: {productPath}");
        }
    }

    private void AssertProductPathDeleteSafe(string productId, string productPath)
    {
        var path = ConvertTimelineText(productPath);
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("Product path is empty.");
        }

        var fullPath = Path.GetFullPath(path);
        var timelinePath = Path.GetFullPath(_options.TimelineProductPath);
        if (fullPath.Equals(timelinePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Timeline itself cannot be uninstalled from product management.");
        }

        var root = Path.GetPathRoot(fullPath)?.TrimEnd('\\', '/') ?? string.Empty;
        if (fullPath.TrimEnd('\\', '/').Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Drive root cannot be removed.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Product directory was not found: {fullPath}");
        }

        var hasKnownLauncher = File.Exists(Path.Combine(fullPath, "start.ps1"))
            || File.Exists(Path.Combine(fullPath, "stop.ps1"))
            || File.Exists(Path.Combine(fullPath, "timeline-product.json"));
        var hasGit = Directory.Exists(Path.Combine(fullPath, ".git"));
        if (!hasKnownLauncher && !hasGit)
        {
            throw new InvalidOperationException($"The target directory does not look like a Timeline sub-product: {fullPath}");
        }
    }

    private void AssertProductManagedDeletePathSafe(string path, string productPath)
    {
        var pathText = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(pathText))
        {
            throw new InvalidOperationException("Delete path is empty.");
        }

        var fullPath = Path.GetFullPath(pathText);
        var root = Path.GetPathRoot(fullPath)?.TrimEnd('\\', '/') ?? string.Empty;
        if (fullPath.TrimEnd('\\', '/').Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Drive root cannot be removed.");
        }

        var timelinePath = Path.GetFullPath(_options.TimelineProductPath);
        if (fullPath.Equals(timelinePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Timeline itself cannot be removed.");
        }

        if (!string.IsNullOrEmpty(productPath))
        {
            var productFullPath = Path.GetFullPath(productPath);
            if (fullPath.Equals(productFullPath, StringComparison.OrdinalIgnoreCase)
                || IsPathUnderRoot(fullPath, productFullPath))
            {
                return;
            }
        }

        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Delete path was not found: {fullPath}");
        }
    }

    private static void AssertProductGeneratedPathNotSource(
        string generatedPath,
        IReadOnlyList<string> sourcePaths)
    {
        if (string.IsNullOrEmpty(generatedPath))
        {
            return;
        }

        var generatedFullPath = Path.GetFullPath(generatedPath);
        foreach (var sourcePath in sourcePaths)
        {
            if (string.IsNullOrEmpty(sourcePath))
            {
                continue;
            }

            var sourceFullPath = Path.GetFullPath(sourcePath);
            if (generatedFullPath.Equals(sourceFullPath, StringComparison.OrdinalIgnoreCase)
                || IsPathUnderRoot(generatedFullPath, sourceFullPath))
            {
                throw new InvalidOperationException($"Generated data path overlaps a source path and cannot be removed: {generatedFullPath}");
            }
        }
    }

    private string GetProductSettingsFilePath(ProductRuntimeDefinition definition)
    {
        if (string.IsNullOrEmpty(definition.ProductPath))
        {
            return string.Empty;
        }

        var path = Path.Combine(definition.ProductPath, "settings.json");
        return File.Exists(path) ? Path.GetFullPath(path) : string.Empty;
    }

    private List<string> GetProductGeneratedDataPaths(ProductRuntimeDefinition definition)
    {
        var settings = ReadJsonObject(Path.Combine(definition.ProductPath, "settings.json"));
        var paths = definition.Id.ToLowerInvariant() switch
        {
            "audio" => GetConfiguredOutputPaths(settings, ["outputRoot", "output_root"], ["outputRoots", "output_roots"]),
            "windows-codex" => GetConfiguredOutputPaths(settings, ["outputRoot", "outputsRoot", "outputs_root"], []),
            "chatgpt" => GetConfiguredOutputPaths(settings, ["masterRoot", "outputRoot"], []),
            "image" => GetConfiguredOutputPaths(settings, ["outputRoot", "output_root"], []),
            "video" => GetConfiguredOutputPaths(settings, ["outputRoot", "output_root"], []),
            "pc" => GetConfiguredOutputPaths(settings, ["outputRoot", "output_root", "masterRoot"], []),
            _ => [],
        };
        return NormalizeLocalPaths(paths);
    }

    private List<string> GetProductSourceDataPaths(ProductRuntimeDefinition definition)
    {
        var settings = ReadJsonObject(Path.Combine(definition.ProductPath, "settings.json"));
        var paths = definition.Id.ToLowerInvariant() switch
        {
            "audio" or "chatgpt" or "image" or "video" => GetConfiguredRootArrayPaths(settings, ["inputRoots", "input_roots"]),
            "pc" => GetConfiguredRootArrayPaths(settings, ["inputRoots", "sourceRoots"]),
            _ => [],
        };
        return NormalizeLocalPaths(paths);
    }

    private List<string> GetConfiguredOutputPaths(
        JsonObject? settings,
        string[] singleNames,
        string[] arrayNames)
    {
        var paths = new List<string>();
        foreach (var name in singleNames)
        {
            var path = GetObjectPathValue(GetNode(settings, name));
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }
        foreach (var name in arrayNames)
        {
            foreach (var node in GetNodeArrayValues(GetNode(settings, name)))
            {
                var path = GetObjectPathValue(node);
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }
        }
        return paths;
    }

    private List<string> GetConfiguredRootArrayPaths(JsonObject? settings, string[] names)
    {
        var paths = new List<string>();
        foreach (var name in names)
        {
            foreach (var node in GetNodeArrayValues(GetNode(settings, name)))
            {
                var path = GetObjectPathValue(node);
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }
        }
        return paths;
    }

    private List<string> NormalizeLocalPaths(IEnumerable<string> paths)
    {
        var normalized = new List<string>();
        foreach (var path in paths)
        {
            var text = ConvertTimelineText(path);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var localPath = ConvertTimelineWindowsPath(text);
            if (string.IsNullOrEmpty(localPath))
            {
                localPath = text;
            }

            try
            {
                var fullPath = Path.GetFullPath(localPath);
                if (!normalized.Any(item => item.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    normalized.Add(fullPath);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        return normalized;
    }

    private ProductUninstallRuntimeDataPlanResponse GetProductRuntimeDataPlan(ProductRuntimeDefinition definition)
    {
        var manifest = ReadJsonObject(Path.Combine(definition.ProductPath, "timeline-product.json"));
        var runtime = GetObject(manifest, "runtime");
        var usesDocker = GetBool(GetNode(runtime, "usesDocker"), false);
        var managedByTimeline = GetBool(GetNode(runtime, "dockerManagedByTimeline"), false);
        var resources = GetProductRuntimeResources(runtime, definition.ProductPath, managedByTimeline);
        var resourceSizeBytes = resources.Where(resource => resource.WillDelete).Sum(resource => resource.SizeBytes);
        var willDelete = resources.Any(resource => resource.WillDelete);
        var message = usesDocker switch
        {
            true when !managedByTimeline => "Runtime data is used by this product, but Timeline has no explicit management contract yet.",
            true when managedByTimeline && resources.Count == 0 => "Runtime data management is declared, but resource deletion is not implemented in this version.",
            true when managedByTimeline => "Runtime data resources are declared. Local paths can be removed; Docker resource deletion is not implemented yet.",
            _ => "This product does not declare Timeline-managed runtime data.",
        };

        return new ProductUninstallRuntimeDataPlanResponse
        {
            UsesDocker = usesDocker,
            ManagedByTimeline = managedByTimeline,
            Exists = usesDocker,
            SizeBytes = resourceSizeBytes,
            WillDelete = willDelete,
            Resources = resources,
            Message = message,
        };
    }

    private List<ProductRuntimeResourcePlanResponse> GetProductRuntimeResources(
        JsonObject? runtime,
        string productPath,
        bool managedByTimeline)
    {
        var resources = new List<ProductRuntimeResourcePlanResponse>();
        if (runtime is null)
        {
            return resources;
        }

        var docker = GetObject(runtime, "docker");
        if (docker is not null)
        {
            AddDockerRuntimeResources(resources, docker, ["composeProjects", "composeProjectNames", "projects"], "docker-project", productPath, managedByTimeline);
            AddDockerRuntimeResources(resources, docker, ["containers"], "docker-container", productPath, managedByTimeline);
            AddDockerRuntimeResources(resources, docker, ["images"], "docker-image", productPath, managedByTimeline);
            AddDockerRuntimeResources(resources, docker, ["volumes"], "docker-volume", productPath, managedByTimeline);
            AddDockerRuntimeResources(resources, docker, ["networks"], "docker-network", productPath, managedByTimeline);
        }

        foreach (var name in new[] { "localPaths", "managedPaths" })
        {
            foreach (var node in GetNodeArrayValues(GetNode(runtime, name)))
            {
                AddProductRuntimeResource(
                    resources,
                    "local-path",
                    GetRuntimeResourceName(node),
                    GetRuntimeResourcePath(node),
                    productPath,
                    managedByTimeline);
            }
        }

        return resources;
    }

    private void AddDockerRuntimeResources(
        List<ProductRuntimeResourcePlanResponse> resources,
        JsonObject docker,
        string[] names,
        string kind,
        string productPath,
        bool managedByTimeline)
    {
        foreach (var name in names)
        {
            foreach (var node in GetNodeArrayValues(GetNode(docker, name)))
            {
                AddProductRuntimeResource(
                    resources,
                    kind,
                    GetRuntimeResourceName(node),
                    path: string.Empty,
                    productPath,
                    managedByTimeline);
            }
        }
    }

    private void AddProductRuntimeResource(
        List<ProductRuntimeResourcePlanResponse> resources,
        string kind,
        string name,
        string path,
        string productPath,
        bool managedByTimeline)
    {
        var nameText = ConvertTimelineText(name);
        var pathText = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(nameText) && string.IsNullOrEmpty(pathText))
        {
            return;
        }

        var exists = false;
        long sizeBytes = 0;
        var message = string.Empty;
        var coveredByProductPath = false;
        if (!string.IsNullOrEmpty(pathText))
        {
            var localPath = ConvertTimelineWindowsPath(pathText);
            if (string.IsNullOrEmpty(localPath))
            {
                localPath = pathText;
            }
            if (Directory.Exists(localPath))
            {
                exists = true;
                sizeBytes = GetDirectorySizeBytes(localPath);
            }
            else if (File.Exists(localPath))
            {
                exists = true;
                sizeBytes = GetFileSizeBytes(localPath);
            }
            else
            {
                message = "Declared path was not found.";
            }

            if (exists && !string.IsNullOrEmpty(productPath))
            {
                try
                {
                    var fullLocalPath = Path.GetFullPath(localPath);
                    var fullProductPath = Path.GetFullPath(productPath);
                    if (fullLocalPath.Equals(fullProductPath, StringComparison.OrdinalIgnoreCase)
                        || IsPathUnderRoot(fullLocalPath, fullProductPath))
                    {
                        coveredByProductPath = true;
                        message = "This path is covered by product application deletion.";
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                }
            }
        }
        else if (kind.StartsWith("docker-", StringComparison.OrdinalIgnoreCase))
        {
            exists = true;
            message = "Docker resource deletion is not implemented by Timeline yet.";
        }

        resources.Add(new ProductRuntimeResourcePlanResponse
        {
            Kind = kind,
            Name = nameText,
            Path = pathText,
            Exists = exists,
            SizeBytes = sizeBytes,
            WillDelete = managedByTimeline && kind.Equals("local-path", StringComparison.OrdinalIgnoreCase) && exists && !coveredByProductPath,
            ManagedByTimeline = managedByTimeline,
            Message = message,
        });
    }

    private static string GetRuntimeResourceName(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }
        if (node is JsonObject obj)
        {
            return GetStringAny(obj, ["name", "id", "value"], string.Empty);
        }
        return ConvertNodeToString(node);
    }

    private static string GetRuntimeResourcePath(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }
        if (node is JsonObject obj)
        {
            return GetStringAny(obj, ["path", "value"], string.Empty);
        }
        return ConvertNodeToString(node);
    }

    private static string GetObjectPathValue(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }
        if (node is JsonObject obj)
        {
            return GetString(obj, "path", string.Empty);
        }
        return ConvertNodeToString(node);
    }

    private static IEnumerable<JsonNode?> GetNodeArrayValues(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                yield return item;
            }
            yield break;
        }

        if (node is not null)
        {
            yield return node;
        }
    }

    private static bool GetBool(JsonNode? node, bool fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => node.GetValue<int>() != 0,
                JsonValueKind.String => ConvertBoolText(node.GetValue<string>(), fallback),
                _ => fallback,
            };
        }
        catch (InvalidOperationException)
        {
            return ConvertBoolText(ConvertNodeToString(node), fallback);
        }
    }

    private static bool ConvertBoolText(string? value, bool fallback)
    {
        var text = ConvertTimelineText(value).ToLowerInvariant();
        return text switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static long GetFileSizeBytes(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long GetDirectorySizeBytes(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return 0;
        }

        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                size += GetFileSizeBytes(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return size;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private Dictionary<string, string> GetChildProcessEnvironment()
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("PATH")
            ?? string.Empty;
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot))
        {
            systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', '/') ?? @"C:\Windows";
        }

        var dockerBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker",
            "Docker",
            "resources",
            "bin");
        var system32 = Path.Combine(systemRoot, "System32");
        var powerShellBin = Path.Combine(system32, "WindowsPowerShell", "v1.0");

        if (File.Exists(Path.Combine(dockerBin, "docker.exe")))
        {
            currentPath = PrependExistingPath(currentPath, dockerBin);
        }
        currentPath = PrependExistingPath(currentPath, system32);
        currentPath = PrependExistingPath(currentPath, powerShellBin);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = currentPath,
            ["Path"] = currentPath,
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL",
            ["DOCKER_CONFIG"] = GetScopedDockerConfigDir(),
        };
    }

    private string GetScopedDockerConfigDir()
    {
        var root = ConvertTimelineText(_options.TimelineProductPath);
        if (string.IsNullOrEmpty(root))
        {
            root = Directory.GetCurrentDirectory();
        }

        var configDir = Path.Combine(root, ".docker", "docker-config");
        var configPath = Path.Combine(configDir, "config.json");
        Directory.CreateDirectory(configDir);
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, "{}", Encoding.ASCII);
        }

        return configDir;
    }

    private static string GetPowerShellPath()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot))
        {
            systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', '/') ?? @"C:\Windows";
        }

        var candidate = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : "powershell.exe";
    }

    private static bool IsProductStartOutputSuccess(string text)
    {
        var value = ConvertTimelineText(text);
        return value.Contains("is running", StringComparison.OrdinalIgnoreCase)
            || value.Contains("was started", StringComparison.OrdinalIgnoreCase)
            || value.Contains("worker is running", StringComparison.OrdinalIgnoreCase)
            || value.Contains("worker-1 was started", StringComparison.OrdinalIgnoreCase);
    }

    private static string PrependExistingPath(string currentPath, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
        {
            return currentPath;
        }

        var parts = currentPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Any(part => part.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return currentPath;
        }

        return string.IsNullOrEmpty(currentPath) ? candidate : candidate + ";" + currentPath;
    }

    private static string BuildCommandLine(string fileName, IEnumerable<string> arguments)
    {
        return string.Join(" ", new[] { fileName }.Concat(arguments).Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        var text = ConvertTimelineText(value);
        if (text.Length == 0)
        {
            return "\"\"";
        }

        if (!text.Any(char.IsWhiteSpace) && !text.Contains('"'))
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private List<ProductRuntimeDefinition> GetProductDefinitions()
    {
        var registry = _settings.ReadSettings().ProductRegistry.Products;
        var products = new Dictionary<string, TimelineProductRegistryProductResponse>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in registry)
        {
            if (!string.IsNullOrWhiteSpace(product.Id))
            {
                products[product.Id] = product;
            }
        }

        var definitions = new List<ProductRuntimeDefinition>();
        foreach (var productId in ProductIds)
        {
            products.TryGetValue(productId, out var product);
            var metadata = ProductMetadata[productId];
            var productPath = product?.Path ?? string.Empty;
            definitions.Add(new ProductRuntimeDefinition(
                productId,
                product?.DisplayName ?? metadata.DisplayName,
                metadata.Description,
                metadata.PagePath,
                $"timeline/settings?product={productId}#product-specific-settings",
                productPath,
                product?.Path ?? productPath,
                product?.SourceType ?? string.Empty,
                product?.SourceUrl ?? string.Empty,
                product?.Version ?? string.Empty,
                product?.Enabled ?? true,
                string.IsNullOrEmpty(productPath) ? string.Empty : Path.Combine(productPath, "start.ps1"),
                string.IsNullOrEmpty(productPath) ? string.Empty : Path.Combine(productPath, "stop.ps1")));
        }

        return definitions;
    }

    private ProductRuntimeDefinition GetProductDefinition(string productId)
    {
        foreach (var definition in GetProductDefinitions())
        {
            if (definition.Id.Equals(productId, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        throw new InvalidOperationException($"Unknown product: {productId}");
    }

    private async Task<ProductRuntimeRowResponse> ConvertRuntimeStatusAsync(
        ProductRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        var productPath = definition.ProductPath;
        var appManagedByTimeline = IsProductAppManagedByTimeline(productPath);
        var productFound = Directory.Exists(productPath);
        var startFound = !string.IsNullOrEmpty(definition.StartPath) && File.Exists(definition.StartPath);
        var stopFound = !string.IsNullOrEmpty(definition.StopPath) && File.Exists(definition.StopPath);
        var launcherFound = startFound || stopFound;

        var state = "not-created";
        var status = string.Empty;
        var running = false;
        var startedAt = string.Empty;
        var message = string.Empty;
        JsonObject? stored = null;

        if (productFound && launcherFound)
        {
            state = "ready";
            status = "ready";
            stored = ReadRuntimeState(definition.Id);
            if (stored is not null)
            {
                var storedState = GetString(stored, "state", string.Empty);
                if (!string.IsNullOrEmpty(storedState))
                {
                    state = storedState;
                    status = storedState;
                    running = state is "starting" or "running" or "restarting";
                }

                var storedStartedAt = GetString(stored, "startedAt", string.Empty);
                if (!string.IsNullOrEmpty(storedStartedAt))
                {
                    startedAt = storedStartedAt;
                }

                var storedMessage = GetString(stored, "message", string.Empty);
                if (!string.IsNullOrEmpty(storedMessage))
                {
                    message = storedMessage;
                }
            }

            var actualRuntime = await GetProductHealthRuntimeStatusAsync(
                definition.Id,
                productPath,
                stored,
                cancellationToken);
            if (actualRuntime.Checked)
            {
                state = actualRuntime.State;
                status = actualRuntime.Status;
                running = actualRuntime.Running;
                startedAt = actualRuntime.StartedAt;
                if (!string.IsNullOrEmpty(actualRuntime.Message))
                {
                    message = actualRuntime.Message;
                }
            }
        }
        else if (!productFound)
        {
            message = "Product directory was not found.";
        }
        else
        {
            message = "Product launcher was not found.";
        }

        var versionInfo = await GetProductVersionInfoAsync(definition, productFound, cancellationToken);
        var settingsBackup = GetProductSettingsBackupInfo(definition.Id);

        return new ProductRuntimeRowResponse
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            Description = definition.Description,
            PagePath = definition.PagePath,
            SettingsPath = definition.SettingsPath,
            ProductPath = productPath,
            Path = string.IsNullOrWhiteSpace(definition.Path) ? productPath : definition.Path,
            SourceType = definition.SourceType,
            SourceUrl = definition.SourceUrl,
            Version = definition.Version,
            AppManagedByTimeline = appManagedByTimeline,
            DestructiveActionsDisabled = productFound && !appManagedByTimeline,
            InstalledVersion = versionInfo.InstalledVersion,
            LatestVersion = versionInfo.LatestVersion,
            LatestVersionStatus = versionInfo.LatestVersionStatus,
            UpdateAvailable = versionInfo.UpdateAvailable,
            ReleaseArchiveUrl = versionInfo.ReleaseArchiveUrl,
            SettingsBackupAvailable = settingsBackup.Exists,
            SettingsBackupPath = settingsBackup.Path,
            SettingsBackupAt = settingsBackup.BackedUpAt,
            Enabled = definition.Enabled,
            ProductFound = productFound,
            ComposeFound = launcherFound,
            StartFound = startFound,
            StopFound = stopFound,
            ContainerName = startFound
                ? Path.GetFileName(definition.StartPath)
                : stopFound
                    ? Path.GetFileName(definition.StopPath)
                    : string.Empty,
            State = state,
            Status = status,
            Running = running,
            StartedAt = startedAt,
            ExitCode = 0,
            Message = message,
        };
    }

    private async Task<ProductActualRuntimeStatus> GetProductHealthRuntimeStatusAsync(
        string productId,
        string productPath,
        JsonObject? storedState,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            return new ProductActualRuntimeStatus(false, "stopped", "stopped", string.Empty, "Product directory was not found.", true);
        }

        var baseUrl = GetProductHealthBaseUrl(productId, productPath);
        if (string.IsNullOrEmpty(baseUrl))
        {
            return new ProductActualRuntimeStatus(false, "stopped", "stopped", string.Empty, "Product health API base URL was not resolved.", true);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/health");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            var running = TestHealthValue(text);
            return new ProductActualRuntimeStatus(
                running,
                running ? "running" : "stopped",
                running ? "running" : "stopped",
                running ? GetString(storedState, "startedAt", string.Empty) : string.Empty,
                running ? "Product health API is running." : "Product health API returned false.",
                true);
        }
        catch
        {
            return new ProductActualRuntimeStatus(false, "stopped", "stopped", string.Empty, "Product health API is stopped.", true);
        }
    }

    private string GetProductHealthBaseUrl(string productId, string productPath)
    {
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            return string.Empty;
        }

        var settings = ReadJsonObject(Path.Combine(productPath, "settings.json"));
        var runtime = GetObject(settings, "runtime");

        var apiBaseUrl = GetStringAny(runtime, ["apiBaseUrl", "api_base_url"], string.Empty);
        if (string.IsNullOrEmpty(apiBaseUrl))
        {
            apiBaseUrl = GetStringAny(settings, ["apiBaseUrl", "api_base_url"], string.Empty);
        }
        if (!string.IsNullOrEmpty(apiBaseUrl))
        {
            return apiBaseUrl.TrimEnd('/');
        }

        var hostName = GetStringAny(runtime, ["apiHost", "api_host"], string.Empty);
        if (string.IsNullOrEmpty(hostName))
        {
            hostName = GetStringAny(settings, ["apiHost", "api_host"], string.Empty);
        }
        if (string.IsNullOrEmpty(hostName) || hostName == "0.0.0.0")
        {
            hostName = "127.0.0.1";
        }

        var port = GetApiPort(GetNodeAny(runtime, ["apiPort", "api_port"]));
        if (port <= 0)
        {
            port = GetApiPort(GetNodeAny(settings, ["apiPort", "api_port"]));
        }
        if (port <= 0)
        {
            port = GetDefaultApiPort(productId);
        }
        if (port <= 0)
        {
            return string.Empty;
        }

        return $"http://{hostName}:{port}";
    }

    private async Task<ProductVersionInfo> GetProductVersionInfoAsync(
        ProductRuntimeDefinition definition,
        bool productFound,
        CancellationToken cancellationToken)
    {
        var installedVersion = productFound ? await GetProductInstalledVersionAsync(definition, cancellationToken) : string.Empty;
        var latestVersion = string.Empty;
        var archiveUrl = string.Empty;
        var latestStatus = string.Empty;

        try
        {
            var source = await GetProductSourceInfoAsync(definition, cancellationToken);
            latestVersion = source.LatestVersion;
            archiveUrl = source.ArchiveUrl;
            latestStatus = "ok";
        }
        catch (Exception ex)
        {
            latestStatus = ex.Message;
        }

        var updateAvailable = false;
        if (productFound && !string.IsNullOrEmpty(latestVersion))
        {
            updateAvailable = string.IsNullOrEmpty(installedVersion)
                || CompareVersionText(installedVersion, latestVersion) < 0;
        }

        return new ProductVersionInfo(installedVersion, latestVersion, latestStatus, updateAvailable, archiveUrl);
    }

    private async Task<string> GetProductInstalledVersionAsync(
        ProductRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        var productPath = definition.ProductPath;
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            return string.Empty;
        }

        var gitVersion = await GetProductGitVersionAsync(productPath, cancellationToken);
        if (!string.IsNullOrEmpty(gitVersion))
        {
            return gitVersion;
        }

        var state = ReadJsonObject(GetProductInstallStatePath(definition.Id));
        var stateVersion = GetString(state, "version", string.Empty);
        if (!string.IsNullOrEmpty(stateVersion))
        {
            return stateVersion;
        }

        var manifest = ReadJsonObject(Path.Combine(productPath, "timeline-product.json"));
        var manifestVersion = GetString(manifest, "version", string.Empty);
        if (!string.IsNullOrEmpty(manifestVersion))
        {
            return manifestVersion;
        }

        return definition.Version;
    }

    private async Task<string> GetProductGitVersionAsync(string productPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(productPath, ".git")))
        {
            return string.Empty;
        }

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.WorkingDirectory = productPath;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.ArgumentList.Add("describe");
            process.StartInfo.ArgumentList.Add("--tags");
            process.StartInfo.ArgumentList.Add("--abbrev=0");
            process.Start();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            return process.ExitCode == 0 ? stdout.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<ProductSourceInfo> GetProductSourceInfoAsync(
        ProductRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        var sourceUrl = ConvertTimelineText(definition.SourceUrl);
        if (string.IsNullOrEmpty(sourceUrl))
        {
            throw new InvalidOperationException("Product install source was not configured.");
        }

        var repository = ResolveGitHubRepository(sourceUrl)
            ?? throw new InvalidOperationException($"Only GitHub source archive installation is supported: {sourceUrl}");

        var latestVersion = await GetLatestGitHubTagAsync(repository.Owner, repository.Repo, cancellationToken);
        var archiveVersion = Uri.EscapeDataString(latestVersion);
        var archiveUrl = $"https://github.com/{repository.Owner}/{repository.Repo}/archive/refs/tags/{archiveVersion}.zip";
        return new ProductSourceInfo(sourceUrl, latestVersion, archiveUrl);
    }

    private async Task<string> GetLatestGitHubTagAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var key = $"{owner}/{repo}".ToLowerInvariant();
        if (_latestVersionCache.TryGetValue(key, out var cached)
            && DateTimeOffset.Now - cached.CachedAt < TimeSpan.FromSeconds(300))
        {
            return cached.Version;
        }

        var best = string.Empty;
        var errors = new List<string>();
        try
        {
            best = await GetLatestGitHubTagFromAtomAsync(owner, repo, cancellationToken);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        if (string.IsNullOrEmpty(best))
        {
            try
            {
                best = await GetLatestGitHubTagFromApiAsync(owner, repo, cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (string.IsNullOrEmpty(best))
        {
            throw new InvalidOperationException($"GitHub tags could not be resolved for {owner}/{repo}: {string.Join(" / ", errors)}");
        }

        _latestVersionCache[key] = new CachedLatestVersion(best, DateTimeOffset.Now);
        return best;
    }

    private async Task<string> GetLatestGitHubTagFromAtomAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        var url = $"https://github.com/{owner}/{repo}/tags.atom";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var xml = await SendStringRequestAsync(request, timeout.Token);
        var best = string.Empty;

        foreach (Match match in Regex.Matches(xml, "/releases/tag/([^\"<]+)"))
        {
            var name = Uri.UnescapeDataString(match.Groups[1].Value);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (string.IsNullOrEmpty(best) || CompareVersionText(best, name) < 0)
            {
                best = name;
            }
        }

        if (string.IsNullOrEmpty(best))
        {
            throw new InvalidOperationException($"No GitHub tags were found in Atom feed for {owner}/{repo}.");
        }

        return best;
    }

    private async Task<string> GetLatestGitHubTagFromApiAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/tags?per_page=100";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var json = await SendStringRequestAsync(request, timeout.Token);
        var tags = JsonNode.Parse(json) as JsonArray;
        var best = string.Empty;
        foreach (var tag in tags?.OfType<JsonObject>() ?? [])
        {
            var name = GetString(tag, "name", string.Empty);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (string.IsNullOrEmpty(best) || CompareVersionText(best, name) < 0)
            {
                best = name;
            }
        }

        if (string.IsNullOrEmpty(best))
        {
            throw new InvalidOperationException($"No GitHub tags were found for {owner}/{repo}.");
        }

        return best;
    }

    private async Task<string> SendStringRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private ProductSettingsBackupInfo GetProductSettingsBackupInfo(string productId)
    {
        var backupRoot = GetProductBackupRoot(productId);
        var backupPath = Path.Combine(backupRoot, "settings", "settings.json");
        var exists = File.Exists(backupPath);
        var backedUpAt = string.Empty;
        var metadataPath = Path.Combine(backupRoot, "metadata.json");
        if (File.Exists(metadataPath))
        {
            var metadata = ReadJsonObject(metadataPath);
            backedUpAt = GetString(metadata, "backedUpAt", string.Empty);
        }

        return new ProductSettingsBackupInfo(exists, exists ? backupPath : string.Empty, backedUpAt);
    }

    private bool IsProductAppManagedByTimeline(string productPath)
    {
        var pathText = ConvertTimelineText(productPath);
        if (string.IsNullOrEmpty(pathText))
        {
            return false;
        }

        try
        {
            var localPath = ConvertTimelineWindowsPath(pathText);
            if (string.IsNullOrEmpty(localPath))
            {
                localPath = pathText;
            }

            var fullPath = Path.GetFullPath(localPath);
            var managedRoot = Path.Combine(_settings.GetDataRootDirectory(), "products");
            Directory.CreateDirectory(managedRoot);
            return IsPathUnderRoot(fullPath, managedRoot);
        }
        catch
        {
            return false;
        }
    }

    private JsonObject? ReadRuntimeState(string productId)
    {
        var path = GetProductRuntimeStatePath(productId);
        return ReadJsonObject(path);
    }

    private string GetProductRuntimeStatePath(string productId)
    {
        var root = Path.Combine(_settings.GetWorkDirectory(), "product-runtime");
        Directory.CreateDirectory(root);
        return Path.Combine(root, GetSafeSegment(productId, "-") + ".json");
    }

    private string GetProductInstallStatePath(string productId)
    {
        return Path.Combine(GetProductBackupRoot(productId), "install-state.json");
    }

    private string GetProductBackupRoot(string productId)
    {
        var safeProductId = GetSafeSegment(productId, "_");
        if (string.IsNullOrEmpty(safeProductId))
        {
            safeProductId = "product";
        }

        return Path.Combine(_settings.GetDataRootDirectory(), "backups", "products", safeProductId);
    }

    private string ConvertTimelineWindowsPath(string path)
        => TimelinePathConverter.ConvertTimelineWindowsPath(path, _options);

    private static JsonObject? ReadJsonObject(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static GitHubRepository? ResolveGitHubRepository(string sourceUrl)
    {
        var text = ConvertTimelineText(sourceUrl);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = Regex.Match(text, @"^https?://github\.com/([^/]+)/([^/.]+)(?:\.git)?(?:/.*)?$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return new GitHubRepository(match.Groups[1].Value, match.Groups[2].Value);
        }

        match = Regex.Match(text, @"^git@github\.com:([^/]+)/([^/.]+)(?:\.git)?$", RegexOptions.IgnoreCase);
        return match.Success ? new GitHubRepository(match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    private static bool TestHealthValue(string value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try
            {
                return TestHealthValue(JsonNode.Parse(text));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TestHealthValue(JsonNode? value)
    {
        if (value is null)
        {
            return false;
        }

        var kind = value.GetValueKind();
        if (kind == JsonValueKind.True)
        {
            return true;
        }
        if (kind == JsonValueKind.False)
        {
            return false;
        }

        if (value is JsonObject obj)
        {
            var node = GetNodeAny(obj, ["ok", "healthy", "running"]);
            if (node is not null)
            {
                return TestHealthValue(node);
            }
        }

        var text = ConvertNodeToString(value);
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try
            {
                return TestHealthValue(JsonNode.Parse(text));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }

    private static int CompareVersionText(string left, string right)
    {
        var leftText = ConvertTimelineText(left);
        var rightText = ConvertTimelineText(right);
        if (string.IsNullOrEmpty(leftText) && string.IsNullOrEmpty(rightText))
        {
            return 0;
        }
        if (string.IsNullOrEmpty(leftText))
        {
            return -1;
        }
        if (string.IsNullOrEmpty(rightText))
        {
            return 1;
        }

        var leftParts = ConvertVersionParts(leftText);
        var rightParts = ConvertVersionParts(rightText);
        for (var index = 0; index < 4; index++)
        {
            if (leftParts[index] < rightParts[index])
            {
                return -1;
            }
            if (leftParts[index] > rightParts[index])
            {
                return 1;
            }
        }

        return string.Compare(leftText, rightText, StringComparison.OrdinalIgnoreCase);
    }

    private static int[] ConvertVersionParts(string version)
    {
        var text = ConvertTimelineText(version);
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        text = Regex.Split(text, "[-+]")[0];
        var parts = text
            .Split('.', StringSplitOptions.None)
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .ToList();
        while (parts.Count < 4)
        {
            parts.Add(0);
        }

        return parts.Take(4).ToArray();
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            var pathFull = Path.GetFullPath(path).TrimEnd('\\', '/');
            var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (pathFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = rootFull + Path.DirectorySeparatorChar;
            return pathFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int GetDefaultApiPort(string productId)
    {
        return productId switch
        {
            "audio" => 19100,
            "windows-codex" => 19200,
            "chatgpt" => 19300,
            "image" => 19400,
            "video" => 19500,
            "pc" => 19600,
            _ => 0,
        };
    }

    private static int GetApiPort(JsonNode? node)
    {
        var text = ConvertNodeToString(node);
        return int.TryParse(text, out var port) && port is >= 1 and <= 65535 ? port : 0;
    }

    private static JsonObject? GetObject(JsonObject? source, string name)
        => GetNode(source, name) as JsonObject;

    private static JsonNode? GetNodeAny(JsonObject? source, string[] names)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is not null)
            {
                return node;
            }
        }

        return null;
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

    private static string GetStringAny(JsonObject? source, string[] names, string fallback)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is not null)
            {
                return ConvertNodeToString(node);
            }
        }

        return fallback;
    }

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertNodeToString(node);
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
            return node.ToJsonString();
        }
    }

    private static string GetSafeSegment(string value, string replacement)
    {
        var text = ConvertTimelineText(value);
        var safe = Regex.Replace(text, "[^A-Za-z0-9._-]", replacement);
        return safe;
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

    private sealed record ProductRuntimeDefinition(
        string Id,
        string DisplayName,
        string Description,
        string PagePath,
        string SettingsPath,
        string ProductPath,
        string Path,
        string SourceType,
        string SourceUrl,
        string Version,
        bool Enabled,
        string StartPath,
        string StopPath);

    private sealed record ProductActualRuntimeStatus(
        bool Running,
        string State,
        string Status,
        string StartedAt,
        string Message,
        bool Checked);

    private sealed record ProductVersionInfo(
        string InstalledVersion,
        string LatestVersion,
        string LatestVersionStatus,
        bool UpdateAvailable,
        string ReleaseArchiveUrl);

    private sealed record ProductSourceInfo(
        string SourceUrl,
        string LatestVersion,
        string ArchiveUrl);

    private sealed record ProductSourceArchiveStage(
        string OperationId,
        string StageRoot,
        string SourceRoot,
        string ArchivePath,
        string SourceUrl,
        string ArchiveUrl,
        string LatestVersion);

    private sealed record GitHubRepository(string Owner, string Repo);

    private sealed record CachedLatestVersion(string Version, DateTimeOffset CachedAt);

    private sealed record ProcessRunResult(int ExitCode, string Stdout, string Stderr);

    private sealed record ProductSettingsBackupInfo(bool Exists, string Path, string BackedUpAt);

    private sealed record ProductInstallOptions(bool RestoreSettingsBackup);

    private sealed record ProductUninstallOptions(bool KeepSettings, bool RemoveGeneratedData);
}

public sealed class ProductRuntimeOverviewResponse
{
    [JsonPropertyName("products")]
    public List<ProductRuntimeRowResponse> Products { get; set; } = [];

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class ProductRuntimeRowResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("pagePath")]
    public string PagePath { get; set; } = "";

    [JsonPropertyName("settingsPath")]
    public string SettingsPath { get; set; } = "";

    [JsonPropertyName("productPath")]
    public string ProductPath { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = "";

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("appManagedByTimeline")]
    public bool AppManagedByTimeline { get; set; }

    [JsonPropertyName("destructiveActionsDisabled")]
    public bool DestructiveActionsDisabled { get; set; }

    [JsonPropertyName("installedVersion")]
    public string InstalledVersion { get; set; } = "";

    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = "";

    [JsonPropertyName("latestVersionStatus")]
    public string LatestVersionStatus { get; set; } = "";

    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; set; }

    [JsonPropertyName("releaseArchiveUrl")]
    public string ReleaseArchiveUrl { get; set; } = "";

    [JsonPropertyName("settingsBackupAvailable")]
    public bool SettingsBackupAvailable { get; set; }

    [JsonPropertyName("settingsBackupPath")]
    public string SettingsBackupPath { get; set; } = "";

    [JsonPropertyName("settingsBackupAt")]
    public string SettingsBackupAt { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("productFound")]
    public bool ProductFound { get; set; }

    [JsonPropertyName("composeFound")]
    public bool ComposeFound { get; set; }

    [JsonPropertyName("startFound")]
    public bool StartFound { get; set; }

    [JsonPropertyName("stopFound")]
    public bool StopFound { get; set; }

    [JsonPropertyName("containerName")]
    public string ContainerName { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("startedAt")]
    public string StartedAt { get; set; } = "";

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class ProductUninstallPlanResponse
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("productPath")]
    public string ProductPath { get; set; } = "";

    [JsonPropertyName("keepSettings")]
    public bool KeepSettings { get; set; } = true;

    [JsonPropertyName("removeGeneratedData")]
    public bool RemoveGeneratedData { get; set; }

    [JsonPropertyName("totalDeleteBytes")]
    public long TotalDeleteBytes { get; set; }

    [JsonPropertyName("appDirectory")]
    public ProductUninstallPathPlanResponse AppDirectory { get; set; } = new();

    [JsonPropertyName("settings")]
    public ProductUninstallSettingsPlanResponse Settings { get; set; } = new();

    [JsonPropertyName("generatedData")]
    public List<ProductUninstallPathPlanResponse> GeneratedData { get; set; } = [];

    [JsonPropertyName("runtimeData")]
    public ProductUninstallRuntimeDataPlanResponse RuntimeData { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];
}

public sealed class ProductUninstallPathPlanResponse
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("willDelete")]
    public bool WillDelete { get; set; }
}

public sealed class ProductUninstallSettingsPlanResponse
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("willBackup")]
    public bool WillBackup { get; set; }

    [JsonPropertyName("backupPath")]
    public string BackupPath { get; set; } = "";

    [JsonPropertyName("willDeleteBackup")]
    public bool WillDeleteBackup { get; set; }
}

public sealed class ProductUninstallRuntimeDataPlanResponse
{
    [JsonPropertyName("usesDocker")]
    public bool UsesDocker { get; set; }

    [JsonPropertyName("managedByTimeline")]
    public bool ManagedByTimeline { get; set; }

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("willDelete")]
    public bool WillDelete { get; set; }

    [JsonPropertyName("resources")]
    public List<ProductRuntimeResourcePlanResponse> Resources { get; set; } = [];

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class ProductRuntimeResourcePlanResponse
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("willDelete")]
    public bool WillDelete { get; set; }

    [JsonPropertyName("managedByTimeline")]
    public bool ManagedByTimeline { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
