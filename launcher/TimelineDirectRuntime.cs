using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class TimelineDirectRuntime
{
    public static async Task<int> StartAsync(string root, TimelineSettings launcherSettings, bool openBrowser)
    {
        var runtime = TimelineRuntimeConfiguration.LoadAndEnsure(root, launcherSettings);
        var paths = TimelineRuntimePaths.Create(root, runtime);

        Directory.CreateDirectory(paths.GeneratedDirectory);
        Directory.CreateDirectory(paths.LocalBuildRoot);
        Directory.CreateDirectory(paths.DockerConfigDirectory);
        Directory.CreateDirectory(paths.WorkSource);
        Directory.CreateDirectory(paths.StoreSource);
        EnsureDockerConfig(paths);

        var docker = ResolveDockerCommand();
        if (string.IsNullOrWhiteSpace(docker))
        {
            Console.Error.WriteLine("Docker command was not found. Install Docker Desktop or add docker to PATH.");
            return 1;
        }

        if (!await EnsureDockerEngineAsync(docker, root))
        {
            Console.Error.WriteLine("Docker engine did not become ready.");
            return 1;
        }

        var localApiPort = await EnsureLocalApiAsync(root, runtime, paths);
        if (localApiPort <= 0)
        {
            Console.Error.WriteLine("Timeline local API did not become ready.");
            return 1;
        }

        runtime = runtime with { LocalApiPort = localApiPort };
        var dockerEnvironment = BuildDockerEnvironment(runtime, paths);
        var composeArgs = BuildComposeArgs(root, runtime);

        Console.WriteLine("Starting Timeline containers...");
        Console.WriteLine($"  Compose project: {runtime.ComposeProjectName}");
        Console.WriteLine($"  Image tag: {runtime.ImageTag}");
        Console.WriteLine($"  Docker GPU override: {(composeArgs.Any(arg => arg.EndsWith("docker-compose.gpu.yml", StringComparison.OrdinalIgnoreCase)) ? "enabled" : "disabled")}");

        await WithDockerLockAsync(paths, async () =>
        {
            await EnsureOllamaVolumeAsync(root, docker, runtime, dockerEnvironment);
            var result = await ProcessRunner.RunAsync(
                root,
                docker,
                ["compose", .. composeArgs, "up", "-d", "--build", "--remove-orphans", "ollama", "web", "worker"],
                TimeSpan.FromMinutes(20),
                dockerEnvironment);
            WriteProcessLogs(paths.ComposeUpStdoutLog, paths.ComposeUpStderrLog, result);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"docker compose up failed with exit code {result.ExitCode}. {CombineProcessText(result)}");
            }
        });

        if (!await EnsureOllamaModelAsync(runtime))
        {
            return 1;
        }

        if (!await WaitForHttpOkAsync($"http://127.0.0.1:{runtime.WebPort}/api/health", TimeSpan.FromMinutes(1)))
        {
            Console.Error.WriteLine($"Timeline web did not become ready at http://127.0.0.1:{runtime.WebPort}.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("Timeline is running.");
        Console.WriteLine($"  Web UI: http://127.0.0.1:{runtime.WebPort}");
        Console.WriteLine($"  Local API: http://127.0.0.1:{localApiPort}");

        if (openBrowser)
        {
            OpenUrl($"http://127.0.0.1:{runtime.WebPort}");
        }

        await PrintConnectedProductsAsync(localApiPort);
        return 0;
    }

    public static async Task<int> StopAsync(string root, TimelineSettings launcherSettings)
    {
        var runtime = TimelineRuntimeConfiguration.LoadAndEnsure(root, launcherSettings);
        var paths = TimelineRuntimePaths.Create(root, runtime);
        Directory.CreateDirectory(paths.GeneratedDirectory);
        Directory.CreateDirectory(paths.DockerConfigDirectory);
        EnsureDockerConfig(paths);

        var docker = ResolveDockerCommand();
        if (!string.IsNullOrWhiteSpace(docker) && await DockerInfoOkAsync(docker, root))
        {
            var dockerEnvironment = BuildDockerEnvironment(runtime, paths);
            var composeArgs = BuildComposeArgs(root, runtime);
            await WithDockerLockAsync(paths, async () =>
            {
                var result = await ProcessRunner.RunAsync(
                    root,
                    docker,
                    ["compose", .. composeArgs, "down", "--remove-orphans"],
                    TimeSpan.FromMinutes(5),
                    dockerEnvironment);
                if (result.ExitCode != 0)
                {
                    Console.Error.WriteLine($"docker compose down reported exit code {result.ExitCode}.");
                    Console.Error.WriteLine(CombineProcessText(result));
                }
            });
        }
        else
        {
            Console.WriteLine("Docker engine is not running. Skipping docker compose down.");
        }

        StopLocalApi(runtime, paths);
        Console.WriteLine("Timeline stop request finished.");
        return 0;
    }

    private static async Task<int> EnsureLocalApiAsync(string root, TimelineRuntimeConfiguration runtime, TimelineRuntimePaths paths)
    {
        for (var port = runtime.LocalApiPortStart; port <= runtime.LocalApiPortEnd; port++)
        {
            if (await IsLocalApiReadyAsync(port))
            {
                Console.WriteLine($"Timeline local API is already running on port {port}.");
                return port;
            }

            try
            {
                await PrepareLocalApiAsync(root, paths with { LocalApiPort = port });
                StartLocalApi(root, runtime, paths with { LocalApiPort = port }, port);
                if (await WaitForLocalApiAsync(port, TimeSpan.FromSeconds(60)))
                {
                    return port;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Timeline local API did not start on port {port}. {ex.Message}");
                StopLocalApi(runtime with { LocalApiPort = port }, paths with { LocalApiPort = port });
            }
        }

        return 0;
    }

    private static async Task PrepareLocalApiAsync(string root, TimelineRuntimePaths paths)
    {
        var buildDir = paths.LocalApiBuildDirectory;
        if (Directory.Exists(buildDir))
        {
            Directory.Delete(buildDir, recursive: true);
        }
        Directory.CreateDirectory(buildDir);

        var bundledRuntimeDirectory = Path.Combine(root, "local-api");
        var bundledExecutablePath = Path.Combine(bundledRuntimeDirectory, LocalApiExecutableFileName());
        var bundledDllPath = Path.Combine(bundledRuntimeDirectory, "Timeline.LocalApi.dll");
        if (File.Exists(bundledExecutablePath) || File.Exists(bundledDllPath))
        {
            CopyDirectory(bundledRuntimeDirectory, buildDir);
            if (!HasRunnableLocalApi(paths))
            {
                throw new FileNotFoundException("Timeline local API bundled runtime was not runnable.", buildDir);
            }

            return;
        }

        var projectPath = Path.Combine(root, "local-api", "Timeline.LocalApi.csproj");
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("Timeline local API project or bundled runtime was not found.", bundledRuntimeDirectory);
        }

        var result = await ProcessRunner.RunAsync(
            root,
            ResolveDotnetCommand(),
            ["publish", projectPath, "-c", "Release", "-p:UseAppHost=false", "-o", buildDir],
            TimeSpan.FromMinutes(5));
        File.WriteAllText(paths.LocalApiPublishLog, CombineProcessText(result));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Timeline local API publish failed. See {paths.LocalApiPublishLog}");
        }

        if (!HasRunnableLocalApi(paths))
        {
            throw new FileNotFoundException("Timeline local API publish output was not found.", paths.LocalApiDllPath);
        }
    }

    private static void StartLocalApi(string root, TimelineRuntimeConfiguration runtime, TimelineRuntimePaths paths, int port)
    {
        StopLocalApi(runtime with { LocalApiPort = port }, paths);

        var executablePath = Path.Combine(paths.LocalApiBuildDirectory, LocalApiExecutableFileName());
        var runBundledExecutable = File.Exists(executablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = runBundledExecutable ? executablePath : ResolveDotnetCommand(),
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        };
        if (!runBundledExecutable)
        {
            startInfo.ArgumentList.Add(paths.LocalApiDllPath);
        }
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{port}");
        startInfo.ArgumentList.Add($"--Timeline:WebPort={runtime.WebPort}");
        startInfo.ArgumentList.Add($"--Timeline:ProductPath={root}");

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start Timeline local API process.");
        }

        File.WriteAllText(paths.LocalApiPidPath, process.Id.ToString(CultureInfo.InvariantCulture));
    }

    private static void StopLocalApi(TimelineRuntimeConfiguration runtime, TimelineRuntimePaths paths)
    {
        KillPidFileProcess(paths.LocalApiPidPath);
        if (runtime.LocalApiPort > 0 && IsLocalApiReadyAsync(runtime.LocalApiPort).GetAwaiter().GetResult())
        {
            foreach (var processId in ResolveListeningProcessIds(runtime.LocalApiPort))
            {
                TryKillProcess(processId);
            }
        }
    }

    private static async Task<bool> EnsureDockerEngineAsync(string docker, string root)
    {
        if (await DockerInfoOkAsync(docker, root))
        {
            return true;
        }

        TryStartDockerDesktop();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await DockerInfoOkAsync(docker, root))
            {
                return true;
            }
            await Task.Delay(2000);
        }

        return false;
    }

    private static async Task<bool> DockerInfoOkAsync(string docker, string root)
    {
        var result = await ProcessRunner.RunAsync(root, docker, ["info"], TimeSpan.FromSeconds(6));
        return result.ExitCode == 0;
    }

    private static void TryStartDockerDesktop()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Docker", "Docker", "Docker Desktop.exe")
                };
                var dockerDesktop = candidates.FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(dockerDesktop))
                {
                    Process.Start(new ProcessStartInfo { FileName = dockerDesktop, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                }
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && Directory.Exists("/Applications/Docker.app"))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    UseShellExecute = false,
                    ArgumentList = { "-a", "Docker" }
                });
            }
        }
        catch
        {
            // Docker may still be started by the user; readiness polling will decide.
        }
    }

    private static async Task EnsureOllamaVolumeAsync(
        string root,
        string docker,
        TimelineRuntimeConfiguration runtime,
        IReadOnlyDictionary<string, string> environment)
    {
        var inspect = await ProcessRunner.RunAsync(
            root,
            docker,
            ["volume", "inspect", runtime.OllamaVolumeName],
            TimeSpan.FromSeconds(20),
            environment);
        if (inspect.ExitCode == 0)
        {
            return;
        }

        var create = await ProcessRunner.RunAsync(
            root,
            docker,
            ["volume", "create", runtime.OllamaVolumeName],
            TimeSpan.FromSeconds(30),
            environment);
        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to create Docker volume {runtime.OllamaVolumeName}. {CombineProcessText(create)}");
        }
    }

    private static async Task<bool> EnsureOllamaModelAsync(TimelineRuntimeConfiguration runtime)
    {
        var baseUrl = $"http://127.0.0.1:{runtime.OllamaPort}";
        var ready = false;
        var modelReady = false;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                var tags = await client.GetFromJsonAsync<OllamaTagsResponse>($"{baseUrl}/api/tags");
                ready = true;
                modelReady = tags?.Models?.Any(model => string.Equals(model.Name, runtime.OllamaModel, StringComparison.Ordinal)) == true;
                break;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }

        if (!ready)
        {
            Console.Error.WriteLine($"Ollama did not become ready at {baseUrl}.");
            return false;
        }

        if (modelReady)
        {
            Console.WriteLine($"Ollama model {runtime.OllamaModel}: OK");
            return true;
        }

        Console.WriteLine($"Pulling Ollama model {runtime.OllamaModel}. This can take a while on first run...");
        using var pullClient = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        var response = await pullClient.PostAsJsonAsync($"{baseUrl}/api/pull", new { name = runtime.OllamaModel, stream = false });
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Ollama model pull failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            return false;
        }

        return true;
    }

    private static async Task PrintConnectedProductsAsync(int localApiPort)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var document = await JsonDocument.ParseAsync(
                await client.GetStreamAsync($"http://127.0.0.1:{localApiPort}/products/runtime/status"));
            if (!document.RootElement.TryGetProperty("products", out var products))
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine("Connected products:");
            foreach (var product in products.EnumerateArray())
            {
                var name = product.TryGetProperty("displayName", out var displayName) ? displayName.GetString() : "";
                var path = product.TryGetProperty("productPath", out var productPath) ? productPath.GetString() : "";
                var state = product.TryGetProperty("state", out var productState) ? productState.GetString() : "";
                Console.WriteLine($"  {name}: {path} [{state}]");
            }
        }
        catch
        {
            Console.WriteLine("Connected product status is not available.");
        }
    }

    private static IReadOnlyList<string> BuildComposeArgs(string root, TimelineRuntimeConfiguration runtime)
    {
        var args = new List<string> { "-f", Path.Combine(root, "docker-compose.yml") };
        var gpuComposePath = Path.Combine(root, "docker-compose.gpu.yml");
        if (File.Exists(gpuComposePath) && ShouldUseGpuCompose(root))
        {
            args.Add("-f");
            args.Add(gpuComposePath);
        }

        args.Add("-p");
        args.Add(runtime.ComposeProjectName);
        return args;
    }

    private static bool ShouldUseGpuCompose(string root)
    {
        var mode = ReadCommonAiComputeMode(root);
        if (mode == "cpu")
        {
            return false;
        }

        if (mode == "gpu")
        {
            return true;
        }

        try
        {
            var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "nvidia-smi.exe" : "nvidia-smi";
            var result = ProcessRunner.RunAsync(root, command, ["-L"], TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadCommonAiComputeMode(string root)
    {
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            if (!File.Exists(settingsPath))
            {
                return "auto";
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("commonAi", out var commonAi) &&
                commonAi.TryGetProperty("computeMode", out var modeProperty))
            {
                var mode = (modeProperty.GetString() ?? "auto").Trim().ToLowerInvariant();
                return mode is "auto" or "cpu" or "gpu" ? mode : "auto";
            }
        }
        catch
        {
        }

        return "auto";
    }

    private static IReadOnlyDictionary<string, string> BuildDockerEnvironment(
        TimelineRuntimeConfiguration runtime,
        TimelineRuntimePaths paths)
    {
        return new Dictionary<string, string>
        {
            ["DOCKER_CONFIG"] = paths.DockerConfigDirectory,
            ["TIMELINE_LOCAL_API_PORT"] = runtime.LocalApiPort.ToString(CultureInfo.InvariantCulture),
            ["TIMELINE_WEB_PORT"] = runtime.WebPort.ToString(CultureInfo.InvariantCulture),
            ["TIMELINE_OLLAMA_PORT"] = runtime.OllamaPort.ToString(CultureInfo.InvariantCulture),
            ["TIMELINE_IMAGE_TAG"] = runtime.ImageTag,
            ["TIMELINE_OLLAMA_VOLUME_NAME"] = runtime.OllamaVolumeName,
            ["TIMELINE_WORK_SOURCE"] = paths.WorkSource,
            ["TIMELINE_STORE_SOURCE"] = paths.StoreSource
        };
    }

    private static async Task WithDockerLockAsync(TimelineRuntimePaths paths, Func<Task> action)
    {
        Directory.CreateDirectory(paths.GeneratedDirectory);
        FileStream? stream = null;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            try
            {
                stream = File.Open(paths.ComposeLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }

        if (stream is null)
        {
            throw new TimeoutException($"Timed out waiting for lock: {paths.ComposeLockPath}");
        }

        await using (stream)
        {
            await action();
        }
    }

    private static void EnsureDockerConfig(TimelineRuntimePaths paths)
    {
        Directory.CreateDirectory(paths.DockerConfigDirectory);
        if (!File.Exists(paths.DockerConfigPath))
        {
            File.WriteAllText(paths.DockerConfigPath, "{}");
        }
    }

    private static bool HasRunnableLocalApi(TimelineRuntimePaths paths)
    {
        return File.Exists(Path.Combine(paths.LocalApiBuildDirectory, LocalApiExecutableFileName())) ||
            File.Exists(paths.LocalApiDllPath);
    }

    private static string LocalApiExecutableFileName()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Timeline.LocalApi.exe"
            : "Timeline.LocalApi";
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativeDirectory = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativeDirectory));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativeFile = Path.GetRelativePath(sourceDirectory, file);
            var destinationFile = Path.Combine(destinationDirectory, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile) ?? destinationDirectory);
            File.Copy(file, destinationFile, overwrite: true);
        }
    }

    private static void WriteProcessLogs(string stdoutPath, string stderrPath, ProcessRunResult result)
    {
        File.WriteAllText(stdoutPath, result.Output);
        File.WriteAllText(stderrPath, result.Error);
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            Console.WriteLine(result.Output.Trim());
        }
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            Console.Error.WriteLine(result.Error.Trim());
        }
    }

    private static async Task<bool> IsLocalApiReadyAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForLocalApiAsync(int port, TimeSpan timeout)
    {
        return await WaitForHttpOkAsync($"http://127.0.0.1:{port}/health", timeout);
    }

    private static async Task<bool> WaitForHttpOkAsync(string url, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
                return;
            }

            Process.Start("xdg-open", url);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open browser: {ex.Message}");
        }
    }

    private static string ResolveDotnetCommand()
    {
        var commandName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        return ResolveCommand(commandName) ?? commandName;
    }

    private static string ResolveDockerCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var dockerDesktopCli = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Docker",
                "Docker",
                "resources",
                "bin",
                "docker.exe");
            if (File.Exists(dockerDesktopCli))
            {
                return dockerDesktopCli;
            }
        }

        var commandName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "docker.exe" : "docker";
        return ResolveCommand(commandName) ?? string.Empty;
    }

    private static string? ResolveCommand(string commandName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(entry, commandName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void KillPidFileProcess(string pidPath)
    {
        if (!File.Exists(pidPath))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(pidPath).Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                TryKillProcess(pid);
            }
        }
        finally
        {
            TryDelete(pidPath);
        }
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
        }
    }

    private static IReadOnlyList<int> ResolveListeningProcessIds(int port)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ResolveWindowsListeningProcessIds(port);
        }

        return ResolveUnixListeningProcessIds(port);
    }

    private static IReadOnlyList<int> ResolveWindowsListeningProcessIds(int port)
    {
        var result = ProcessRunner.RunAsync(Environment.CurrentDirectory, "netstat.exe", ["-ano", "-p", "tcp"], TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            return [];
        }

        var ids = new HashSet<int>();
        foreach (var line in result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = Regex.Split(line, "\\s+").Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
            if (parts.Length < 5)
            {
                continue;
            }

            var localAddress = parts[1];
            var state = parts[3];
            var pidText = parts[^1];
            if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase) ||
                !localAddress.EndsWith($":{port}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                ids.Add(pid);
            }
        }

        return ids.ToArray();
    }

    private static IReadOnlyList<int> ResolveUnixListeningProcessIds(int port)
    {
        var result = ProcessRunner.RunAsync(Environment.CurrentDirectory, "lsof", ["-ti", $"tcp:{port}", "-sTCP:LISTEN"], TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            return [];
        }

        return result.Output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : 0)
            .Where(pid => pid > 0)
            .Distinct()
            .ToArray();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static string CombineProcessText(ProcessRunResult result)
    {
        return string.Join(
            Environment.NewLine,
            new[] { result.Output, result.Error }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private sealed record OllamaTagsResponse(OllamaModel[]? Models);

    private sealed record OllamaModel(string? Name);
}

internal sealed record TimelineRuntimeConfiguration(
    string InstanceName,
    string ComposeProjectName,
    string ImageTag,
    int WebPort,
    int LocalApiPortStart,
    int LocalApiPortEnd,
    int LocalApiPort,
    int OllamaPort,
    string OllamaModel,
    bool ShareOllamaVolume,
    string OllamaVolumeName,
    string DataRoot)
{
    public static TimelineRuntimeConfiguration LoadAndEnsure(string root, TimelineSettings launcherSettings)
    {
        var settingsPath = Path.Combine(root, "settings.json");
        var changed = false;
        JsonObject settings;
        if (File.Exists(settingsPath))
        {
            settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? [];
        }
        else
        {
            settings = [];
            settings["schemaVersion"] = 1;
            changed = true;
        }

        if (settings["runtime"] is not JsonObject runtime)
        {
            runtime = [];
            settings["runtime"] = runtime;
            changed = true;
        }

        changed |= Ensure(runtime, "instanceName", NewInstanceName());
        changed |= Ensure(runtime, "imageTag", "");
        changed |= Ensure(runtime, "webPort", launcherSettings.WebPort);
        changed |= Ensure(runtime, "localApiPortStart", launcherSettings.LocalApiPort);
        changed |= Ensure(runtime, "localApiPortEnd", launcherSettings.LocalApiPort);
        changed |= Ensure(runtime, "ollamaPort", 11434);
        changed |= Ensure(runtime, "ollamaModel", "qwen3.5:9b");
        changed |= Ensure(runtime, "shareOllamaVolume", true);
        changed |= Ensure(runtime, "ollamaVolumeName", "timeline-ollama");

        if (changed)
        {
            File.WriteAllText(
                settingsPath,
                settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }

        var instanceName = ReadString(runtime, "instanceName", "");
        var instancePart = SanitizeNamePart(instanceName);
        var projectName = string.IsNullOrWhiteSpace(instancePart) ? "timeline" : $"timeline-{instancePart}";

        var imageTag = SanitizeResourceName(ReadString(runtime, "imageTag", ""));
        if (string.IsNullOrWhiteSpace(imageTag))
        {
            imageTag = string.IsNullOrWhiteSpace(instancePart) ? "latest" : projectName;
        }

        var webPort = ReadPort(runtime, "webPort", 19000);
        var localApiPortStart = ReadPort(runtime, "localApiPortStart", 19001);
        var localApiPortEnd = ReadPort(runtime, "localApiPortEnd", Math.Max(19010, localApiPortStart));
        if (localApiPortEnd < localApiPortStart)
        {
            localApiPortEnd = localApiPortStart;
        }

        var ollamaPort = ReadPort(runtime, "ollamaPort", 11434);
        var ollamaModel = ReadString(runtime, "ollamaModel", "qwen3.5:9b");
        var shareOllamaVolume = ReadBool(runtime, "shareOllamaVolume", true);
        var defaultOllamaVolumeName = shareOllamaVolume ? "timeline-ollama" : $"{projectName}-ollama";
        var ollamaVolumeName = SanitizeResourceName(ReadString(runtime, "ollamaVolumeName", defaultOllamaVolumeName));
        if (string.IsNullOrWhiteSpace(ollamaVolumeName))
        {
            ollamaVolumeName = defaultOllamaVolumeName;
        }

        var dataRoot = ReadString(settings, "dataRoot", "data");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = "data";
        }
        if (!Path.IsPathRooted(dataRoot))
        {
            dataRoot = Path.GetFullPath(Path.Combine(root, dataRoot));
        }

        return new TimelineRuntimeConfiguration(
            instanceName,
            projectName,
            imageTag,
            webPort,
            localApiPortStart,
            localApiPortEnd,
            localApiPortStart,
            ollamaPort,
            ollamaModel,
            shareOllamaVolume,
            ollamaVolumeName,
            dataRoot);
    }

    private static bool Ensure(JsonObject target, string propertyName, JsonNode? value)
    {
        if (target[propertyName] is not null)
        {
            return false;
        }

        target[propertyName] = value;
        return true;
    }

    private static string NewInstanceName()
    {
        return $"local-{Guid.NewGuid():N}"[..16];
    }

    private static string ReadString(JsonObject source, string propertyName, string fallback)
    {
        return source[propertyName]?.GetValue<string>() ?? fallback;
    }

    private static int ReadPort(JsonObject source, string propertyName, int fallback)
    {
        var value = source[propertyName];
        if (value is null)
        {
            return fallback;
        }

        try
        {
            if (value.GetValueKind() == JsonValueKind.Number && value.GetValue<int>() is var number && number is >= 1 and <= 65535)
            {
                return number;
            }

            if (int.TryParse(value.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed is >= 1 and <= 65535)
            {
                return parsed;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static bool ReadBool(JsonObject source, string propertyName, bool fallback)
    {
        var value = source[propertyName];
        if (value is null)
        {
            return fallback;
        }

        try
        {
            if (value.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetValue<bool>();
            }

            var text = value.GetValue<string>().Trim().ToLowerInvariant();
            return text switch
            {
                "true" or "1" or "yes" or "on" => true,
                "false" or "0" or "no" or "off" => false,
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }

    private static string SanitizeNamePart(string value)
    {
        return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    }

    private static string SanitizeResourceName(string value)
    {
        return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9_.-]+", "-").Trim('-');
    }
}

internal sealed record TimelineRuntimePaths(
    string Root,
    int LocalApiPort,
    string GeneratedDirectory,
    string LocalBuildRoot,
    string LocalApiBuildDirectory,
    string LocalApiDllPath,
    string LocalApiPublishLog,
    string LocalApiPidPath,
    string DockerConfigDirectory,
    string DockerConfigPath,
    string ComposeLockPath,
    string ComposeUpStdoutLog,
    string ComposeUpStderrLog,
    string WorkSource,
    string StoreSource)
{
    public static TimelineRuntimePaths Create(string root, TimelineRuntimeConfiguration runtime)
    {
        var generated = Path.Combine(root, ".docker");
        var localBuildRoot = Path.Combine(root, ".local");
        var localApiBuildDirectory = Path.Combine(localBuildRoot, $"local-api-build-{runtime.LocalApiPort}");
        return new TimelineRuntimePaths(
            root,
            runtime.LocalApiPort,
            generated,
            localBuildRoot,
            localApiBuildDirectory,
            Path.Combine(localApiBuildDirectory, "Timeline.LocalApi.dll"),
            Path.Combine(generated, $"local-api-{runtime.LocalApiPort}.publish.log"),
            Path.Combine(generated, $"local-api-{runtime.LocalApiPort}.pid"),
            Path.Combine(generated, "docker-config"),
            Path.Combine(generated, "docker-config", "config.json"),
            Path.Combine(generated, "docker-compose.lock"),
            Path.Combine(generated, "compose-up.stdout.log"),
            Path.Combine(generated, "compose-up.stderr.log"),
            Path.Combine(runtime.DataRoot, "work"),
            Path.Combine(runtime.DataRoot, "to_timeline"));
    }
}

internal sealed record ProcessRunResult(int ExitCode, string Output, string Error);

internal static class ProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
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

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeoutSource = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new ProcessRunResult(124, string.Empty, $"Command timed out: {fileName}");
            }

            return new ProcessRunResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception ex)
        {
            return new ProcessRunResult(127, string.Empty, ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
