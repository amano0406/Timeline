using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

var options = LauncherOptions.Parse(args);
var root = TimelinePaths.ResolveRoot(options.Root);
var settings = TimelineSettings.Load(root);
var command = options.Command;

Console.OutputEncoding = System.Text.Encoding.UTF8;

try
{
    return command switch
    {
        "status" => await ShowStatus(root, settings),
        "preflight" => await ShowPreflight(root, settings),
        "start" => await RunStart(root, settings, openBrowser: !options.NoOpen),
        "stop" => await RunStop(root, settings),
        "open" => await OpenOrStart(root, settings),
        "shortcut-status" => ShowShortcutStatus(root),
        "shortcut-install" or "install-shortcut" => InstallShortcut(root),
        "shortcut-remove" or "remove-shortcut" => RemoveShortcut(root),
        "help" => ShowHelp(),
        _ => ShowUnknownCommand(command)
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("Timeline Launcher failed.");
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> ShowPreflight(string root, TimelineSettings settings)
{
    var checks = new List<PreflightCheck>();
    var settingsPath = Path.Combine(root, "settings.json");

    checks.Add(NewInfo("OS", GetPlatformDescription()));
    checks.Add(Directory.Exists(root)
        ? NewOk("Timeline root", root)
        : NewError("Timeline root", $"Directory was not found: {root}"));

    AddRequiredPathCheck(checks, root, "docker-compose.yml", requiredKind: "file");
    AddRequiredPathCheck(checks, root, "launcher", requiredKind: "directory");
    AddRequiredPathCheck(checks, root, "launcher-tray", requiredKind: "directory");
    AddRequiredPathCheck(checks, root, "local-api", requiredKind: "directory");
    AddRequiredPathCheck(checks, root, "web", requiredKind: "directory");

    checks.Add(File.Exists(settingsPath)
        ? NewOk("settings.json", $"Loaded from {settingsPath}")
        : NewWarning("settings.json", "settings.json was not found. Default ports will be used."));

    checks.Add(NewInfo("Configured Web", settings.WebUrl));
    checks.Add(NewInfo("Configured Local API", settings.LocalApiHealthUrl));

    var dotnet = ResolveCommand(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
    checks.Add(string.IsNullOrWhiteSpace(dotnet)
        ? NewError(".NET SDK", "dotnet command was not found on PATH.")
        : NewOk(".NET SDK", dotnet));

    var docker = ResolveDockerCommand();
    checks.Add(string.IsNullOrWhiteSpace(docker)
        ? NewError("Docker command", "Docker command was not found.")
        : NewOk("Docker command", docker));

    var dockerStatus = string.IsNullOrWhiteSpace(docker)
        ? NewDockerProblemStatus(127, "Docker command could not be found.")
        : GetDockerStatus(root);
    checks.Add(dockerStatus.Available
        ? NewOk("Docker Engine", dockerStatus.Message)
        : NewError("Docker Engine", $"{dockerStatus.Message} {dockerStatus.Action}".Trim()));

    checks.Add(await IsWebReady(settings.WebHealthUrl)
        ? NewOk("Web health", $"{settings.WebHealthUrl} is responding.")
        : NewWarning("Web health", $"{settings.WebHealthUrl} is not responding. This is acceptable before startup."));

    checks.Add(await IsLocalApiReady(settings.LocalApiHealthUrl)
        ? NewOk("Local API health", $"{settings.LocalApiHealthUrl} is responding.")
        : NewWarning("Local API health", $"{settings.LocalApiHealthUrl} is not responding. This is acceptable before startup."));

    PrintPreflightChecks(checks);
    return PreflightExitCode(checks);
}

static async Task<int> OpenOrStart(string root, TimelineSettings settings)
{
    if (!await IsWebReady(settings.WebHealthUrl))
    {
        Console.WriteLine("Timeline is not running. Starting Timeline...");
        var exitCode = await RunStart(root, settings, openBrowser: false);
        if (exitCode != 0)
        {
            return exitCode;
        }
    }

    if (!await WaitForWeb(settings.WebHealthUrl, TimeSpan.FromSeconds(30)))
    {
        Console.Error.WriteLine("Timeline web did not become ready.");
        Console.Error.WriteLine($"Open manually after startup: {settings.WebUrl}");
        return 1;
    }

    Console.WriteLine($"Opening Timeline: {settings.WebUrl}");
    OpenUrl(settings.WebUrl);
    return 0;
}

static async Task<int> ShowStatus(string root, TimelineSettings settings)
{
    var runtimeStatus = await FetchRuntimeStatus(settings.RuntimeStatusUrl);
    if (runtimeStatus is not null)
    {
        Console.WriteLine("Timeline status");
        Console.WriteLine($"  {runtimeStatus.Message}");
        Console.WriteLine($"  state: {runtimeStatus.State}");
        Console.WriteLine();

        foreach (var component in runtimeStatus.Components)
        {
            Console.WriteLine($"- {component.Label}: {component.State}");
            if (!string.IsNullOrWhiteSpace(component.Message))
            {
                Console.WriteLine($"  {component.Message}");
            }
        }

        return runtimeStatus.Severity is "error" ? 2 : 0;
    }

    var localApiReady = await IsLocalApiReady(settings.LocalApiHealthUrl);

    Console.WriteLine("Timeline status");
    Console.WriteLine(localApiReady
        ? "  Timeline runtime status could not be read."
        : "  Timeline local API is not responding.");
    Console.WriteLine();
    var dockerStatus = GetDockerStatus(root);
    Console.WriteLine($"- Web: {(await IsWebReady(settings.WebHealthUrl) ? "running" : "not responding")}");
    Console.WriteLine($"- Local API: {(localApiReady ? "running" : "not responding")}");
    Console.WriteLine($"- Docker: {dockerStatus.State}");
    if (!string.IsNullOrWhiteSpace(dockerStatus.Message))
    {
        Console.WriteLine($"  {dockerStatus.Message}");
    }
    if (!string.IsNullOrWhiteSpace(dockerStatus.Action))
    {
        Console.WriteLine($"  Next action: {dockerStatus.Action}");
    }

    var dockerSummary = dockerStatus.Available ? TryGetDockerSummary(root) : string.Empty;
    if (!string.IsNullOrWhiteSpace(dockerSummary))
    {
        Console.WriteLine();
        Console.WriteLine("Docker containers containing timeline:");
        Console.WriteLine(dockerSummary);
    }

    Console.WriteLine();
    Console.WriteLine("To start Timeline, run: TimelineLauncher start");
    return 2;
}

static async Task<int> RunStart(string root, TimelineSettings settings, bool openBrowser)
{
    Console.WriteLine("Starting Timeline through the C# launcher runtime...");
    return await TimelineDirectRuntime.StartAsync(root, settings, openBrowser);
}

static async Task<int> RunStop(string root, TimelineSettings settings)
{
    Console.WriteLine("Stopping Timeline through the C# launcher runtime...");
    return await TimelineDirectRuntime.StopAsync(root, settings);
}

static int ShowShortcutStatus(string root)
{
    var status = TimelineLauncherShortcutService.GetStatus(root);
    PrintShortcutStatus(status);
    return status.State.Equals("failed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}

static int InstallShortcut(string root)
{
    var status = TimelineLauncherShortcutService.Install(root);
    PrintShortcutStatus(status);
    return status.Registered ? 0 : 1;
}

static int RemoveShortcut(string root)
{
    var status = TimelineLauncherShortcutService.Remove(root);
    PrintShortcutStatus(status);
    return status.State.Equals("failed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}

static void PrintShortcutStatus(TimelineLauncherShortcutStatus status)
{
    Console.WriteLine("Timeline app entry");
    Console.WriteLine($"  {status.Message}");
    Console.WriteLine($"  platform: {status.Platform}");
    Console.WriteLine($"  state: {status.State}");
    Console.WriteLine($"  registered: {status.Registered}");
    Console.WriteLine($"  kind: {status.Kind}");
    if (!string.IsNullOrWhiteSpace(status.ShortcutPath))
    {
        Console.WriteLine($"  shortcut: {status.ShortcutPath}");
    }
    var commandLine = TimelineLauncherShortcutService.FormatCommandLine(status);
    if (!string.IsNullOrWhiteSpace(commandLine))
    {
        Console.WriteLine($"  target: {commandLine}");
    }
}

static int ShowHelp()
{
    Console.WriteLine("Timeline Launcher");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  TimelineLauncher [open|status|preflight|start|stop|shortcut-status|shortcut-install|shortcut-remove|help] [--no-open]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  open    Open Timeline. Starts it first when needed.");
    Console.WriteLine("  status  Show Timeline runtime status.");
    Console.WriteLine("  preflight  Check local prerequisites before runtime verification.");
    Console.WriteLine("  start   Start Timeline.");
    Console.WriteLine("  stop    Stop Timeline.");
    Console.WriteLine("  shortcut-status   Show the OS app entry status.");
    Console.WriteLine("  shortcut-install  Create or update the OS app entry.");
    Console.WriteLine("  shortcut-remove   Remove the OS app entry.");
    return 0;
}

static int ShowUnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    return ShowHelp() == 0 ? 2 : 1;
}

static async Task<bool> IsWebReady(string url) => await HttpOk(url);

static async Task<bool> IsLocalApiReady(string url) => await HttpOk(url);

static async Task<bool> WaitForWeb(string url, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow.Add(timeout);
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (await IsWebReady(url))
        {
            return true;
        }

        await Task.Delay(1000);
    }

    return false;
}

static async Task<bool> HttpOk(string url)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await client.GetAsync(url);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

static async Task<RuntimeStatus?> FetchRuntimeStatus(string url)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<RuntimeStatus>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch
    {
        return null;
    }
}

static string TryGetDockerSummary(string root)
{
    var docker = ResolveDockerCommand();
    if (string.IsNullOrWhiteSpace(docker))
    {
        return string.Empty;
    }

    var result = RunProcess(root, docker, "ps --format \"{{.Names}}\\t{{.Status}}\"", TimeSpan.FromSeconds(4));
    if (result.ExitCode != 0)
    {
        return string.Empty;
    }

    var lines = result.Output
        .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => line.Contains("timeline", StringComparison.OrdinalIgnoreCase))
        .Take(20)
        .ToArray();

    return lines.Length == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
}

static ProcessResult RunProcess(string root, string fileName, string arguments, TimeSpan timeout)
{
    try
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        process.Start();
        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup only.
            }

            return new ProcessResult(124, string.Empty, "Docker command timed out.");
        }

        return new ProcessResult(
            process.ExitCode,
            process.StandardOutput.ReadToEnd(),
            process.StandardError.ReadToEnd());
    }
    catch (Exception ex)
    {
        return new ProcessResult(127, string.Empty, ex.Message);
    }
}

static DockerStatus GetDockerStatus(string root)
{
    var docker = ResolveDockerCommand();
    if (string.IsNullOrWhiteSpace(docker))
    {
        return NewDockerProblemStatus(127, "Docker command could not be found.");
    }

    var result = RunProcess(root, docker, "info", TimeSpan.FromSeconds(4));
    if (result.ExitCode == 0)
    {
        return new DockerStatus(
            Available: true,
            State: "running",
            Message: "Docker は起動しています。",
            Action: "");
    }

    var details = CombineProcessText(result);
    return NewDockerProblemStatus(result.ExitCode, details);
}

static DockerStatus NewDockerProblemStatus(int exitCode, string details)
{
    var state = ResolveDockerProblemState(exitCode, details);
    return new DockerStatus(
        Available: false,
        State: state,
        Message: DescribeDockerProblem(exitCode, details),
        Action: ResolveDockerProblemAction(state));
}

static string ResolveDockerProblemState(int exitCode, string details)
{
    if (exitCode == 124)
    {
        return "timeout";
    }

    if (exitCode == 127)
    {
        return "command_missing";
    }

    if (IsDockerEngineUnavailable(details))
    {
        return "engine_stopped";
    }

    if (IsDockerCommandMissing(details))
    {
        return "command_missing";
    }

    return "unknown";
}

static string DescribeDockerProblem(int exitCode, string details)
{
    if (exitCode == 124)
    {
        return "Docker の状態確認がタイムアウトしました。Docker Desktop が起動途中、または応答していない可能性があります。";
    }

    if (exitCode == 127)
    {
        return "Docker コマンドが見つからない、または実行できません。Docker Desktop のインストールと PATH を確認してください。";
    }

    if (IsDockerEngineUnavailable(details))
    {
        return "Docker Engine が起動していません。Timeline の自動処理を使うには Docker Desktop の起動が必要です。";
    }

    if (IsDockerCommandMissing(details))
    {
        return "Docker コマンドが見つからない、または実行できません。Docker Desktop のインストールと PATH を確認してください。";
    }

    return "Docker の状態を確認できません。Docker Desktop の状態を確認してください。";
}

static string ResolveDockerProblemAction(string state) => state switch
{
    "command_missing" => "Docker Desktop をインストールするか、docker.exe に PATH が通っている状態にしてから再実行してください。",
    "engine_stopped" => "Docker Desktop を起動してから、TimelineLauncher status または TimelineLauncher open を再実行してください。",
    "timeout" => "Docker Desktop の起動が完了するまで待ってから、TimelineLauncher status を再実行してください。",
    _ => "Docker Desktop の状態を確認してから、TimelineLauncher status を再実行してください。",
};

static bool IsDockerCommandMissing(string details)
{
    return details.Contains("docker command could not be started", StringComparison.OrdinalIgnoreCase)
        || details.Contains("The system cannot find the file", StringComparison.OrdinalIgnoreCase)
        || details.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase);
}

static bool IsDockerEngineUnavailable(string details)
{
    return details.Contains("Docker daemon", StringComparison.OrdinalIgnoreCase)
        || details.Contains("dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase)
        || details.Contains("docker engine", StringComparison.OrdinalIgnoreCase)
        || details.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
        || details.Contains("pipe/docker", StringComparison.OrdinalIgnoreCase)
        || details.Contains("docker API", StringComparison.OrdinalIgnoreCase);
}

static string CombineProcessText(ProcessResult result)
{
    return string.Join(
        Environment.NewLine,
        new[] { result.Output, result.Error }.Where(text => !string.IsNullOrWhiteSpace(text)));
}

static string ResolveDockerCommand()
{
    var commandName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "docker.exe" : "docker";
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

    return ResolveCommand(commandName);
}

static string ResolveCommand(string commandName)
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

    return string.Empty;
}

static void AddRequiredPathCheck(List<PreflightCheck> checks, string root, string relativePath, string requiredKind)
{
    var fullPath = Path.Combine(root, relativePath);
    var exists = requiredKind.Equals("file", StringComparison.OrdinalIgnoreCase)
        ? File.Exists(fullPath)
        : Directory.Exists(fullPath);

    checks.Add(exists
        ? NewOk(relativePath, fullPath)
        : NewError(relativePath, $"Required {requiredKind} was not found: {fullPath}"));
}

static void PrintPreflightChecks(IReadOnlyList<PreflightCheck> checks)
{
    Console.WriteLine("Timeline preflight");
    Console.WriteLine("  Checks local prerequisites for runtime verification.");
    Console.WriteLine();

    foreach (var check in checks)
    {
        Console.WriteLine($"- [{PreflightSeverityLabel(check.Severity)}] {check.Name}");
        if (!string.IsNullOrWhiteSpace(check.Message))
        {
            Console.WriteLine($"  {check.Message}");
        }
    }

    Console.WriteLine();
    var errors = checks.Count(check => check.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
    var warnings = checks.Count(check => check.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(errors > 0
        ? $"Result: {errors} error(s), {warnings} warning(s). Fix errors before runtime verification."
        : warnings > 0
            ? $"Result: {warnings} warning(s). Runtime verification can continue if these are expected."
            : "Result: all preflight checks passed.");
}

static int PreflightExitCode(IEnumerable<PreflightCheck> checks)
{
    if (checks.Any(check => check.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)))
    {
        return 2;
    }

    return checks.Any(check => check.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase))
        ? 1
        : 0;
}

static string PreflightSeverityLabel(string severity) => severity switch
{
    "ok" => "OK",
    "warning" => "WARN",
    "error" => "ERROR",
    _ => "INFO",
};

static PreflightCheck NewOk(string name, string message) => new("ok", name, message);

static PreflightCheck NewWarning(string name, string message) => new("warning", name, message);

static PreflightCheck NewError(string name, string message) => new("error", name, message);

static PreflightCheck NewInfo(string name, string message) => new("info", name, message);

static string GetPlatformDescription()
{
    var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "Windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "macOS"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "Linux"
                : "Unknown";

    return $"{platform} / {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}";
}

static void OpenUrl(string url)
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

internal sealed record LauncherOptions(string? Root, string Command, bool NoOpen)
{
    public static LauncherOptions Parse(string[] args)
    {
        string? root = null;
        var command = "open";
        var noOpen = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg == "--no-open")
            {
                noOpen = true;
                continue;
            }

            if (arg == "--root" && index + 1 < args.Length)
            {
                root = args[++index];
                continue;
            }

            if (arg.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
            {
                root = arg["--root=".Length..];
                continue;
            }

            command = arg.Trim().ToLowerInvariant();
        }

        return new LauncherOptions(root, command, noOpen);
    }
}

internal sealed record TimelineSettings(int WebPort, int LocalApiPort)
{
    public string WebUrl => $"http://127.0.0.1:{WebPort}";
    public string WebHealthUrl => $"{WebUrl}/api/health";
    public string LocalApiHealthUrl => $"http://127.0.0.1:{LocalApiPort}/health";
    public string RuntimeStatusUrl => $"http://127.0.0.1:{LocalApiPort}/timeline/runtime/status";

    public static TimelineSettings Load(string root)
    {
        var webPort = 19000;
        var localApiPort = 19001;
        var settingsPath = Path.Combine(root, "settings.json");
        if (!File.Exists(settingsPath))
        {
            return new TimelineSettings(webPort, localApiPort);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("runtime", out var runtime))
            {
                webPort = ReadPort(runtime, "webPort", webPort);
                localApiPort = ReadPort(runtime, "localApiPortStart", localApiPort);
            }
        }
        catch
        {
            // Keep defaults when settings cannot be read.
        }

        return new TimelineSettings(webPort, localApiPort);
    }

    private static int ReadPort(JsonElement runtime, string propertyName, int fallback)
    {
        if (!runtime.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}

internal sealed record RuntimeStatus(
    string State,
    string Severity,
    string Message,
    RuntimeComponent[] Components);

internal sealed record RuntimeComponent(
    string Label,
    string State,
    string Severity,
    string Message);

internal sealed record DockerStatus(bool Available, string State, string Message, string Action);

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal sealed record PreflightCheck(string Severity, string Name, string Message);

internal static class TimelinePaths
{
    public static string ResolveRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "docker-compose.yml")) &&
                Directory.Exists(Path.Combine(current.FullName, "local-api")) &&
                Directory.Exists(Path.Combine(current.FullName, "web")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

}
