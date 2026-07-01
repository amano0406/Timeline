using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

var options = ReleaseOptions.Parse(args);
var repoRoot = ResolveRepoRoot(Directory.GetCurrentDirectory());
var version = string.IsNullOrWhiteSpace(options.Version)
    ? SanitizeVersion((await RunCaptureAsync(repoRoot, "git", ["describe", "--tags", "--dirty", "--always"], TimeSpan.FromSeconds(10))).Output.Trim())
    : SanitizeVersion(options.Version);
if (string.IsNullOrWhiteSpace(version))
{
    version = "0.0.0-dev";
}

var commit = (await RunCaptureAsync(repoRoot, "git", ["rev-parse", "--short", "HEAD"], TimeSpan.FromSeconds(10))).Output.Trim();
var imageTag = SanitizeDockerTag(version);
var outputRoot = Path.GetFullPath(Path.Combine(repoRoot, options.OutputDirectory));
var stagingParent = Path.Combine(outputRoot, "staging");
var productRoot = Path.Combine(stagingParent, "Timeline");
var zipPath = Path.Combine(outputRoot, $"Timeline-{options.HostRuntime}-{version}.zip");

if (Directory.Exists(stagingParent))
{
    Directory.Delete(stagingParent, recursive: true);
}
Directory.CreateDirectory(productRoot);
Directory.CreateDirectory(outputRoot);
if (File.Exists(zipPath))
{
    File.Delete(zipPath);
}

var publishSpecs = new[]
{
    new PublishSpec("launcher", "launcher/Timeline.Launcher.csproj", "launcher", options.HostRuntime, SelfContained: true, UseAppHost: true),
    new PublishSpec("launcher-tray", "launcher-tray/Timeline.Launcher.Tray.csproj", "launcher-tray", options.HostRuntime, SelfContained: true, UseAppHost: true),
    new PublishSpec("local-api", "local-api/Timeline.LocalApi.csproj", "local-api", options.HostRuntime, SelfContained: true, UseAppHost: true),
    new PublishSpec("web", "web/Timeline.Web.csproj", "web", options.ContainerRuntime, SelfContained: false, UseAppHost: false),
    new PublishSpec("worker", "worker/Timeline.Worker.csproj", "worker", options.ContainerRuntime, SelfContained: false, UseAppHost: false),
};

foreach (var spec in publishSpecs)
{
    await PublishAsync(repoRoot, productRoot, spec);
}

CopyProductCompose(repoRoot, productRoot, imageTag);
CopyIfExists(Path.Combine(repoRoot, "docker-compose.gpu.yml"), Path.Combine(productRoot, "docker-compose.gpu.yml"));
CopyIfExists(Path.Combine(repoRoot, "README.md"), Path.Combine(productRoot, "README.md"));
CopyIfExists(Path.Combine(repoRoot, "docs", "distribution-artifacts.md"), Path.Combine(productRoot, "docs", "distribution-artifacts.md"));
WriteVersionFile(productRoot, options, version, commit);
WriteNotices(productRoot);
WriteRuntimeReadme(productRoot);
RemoveForbiddenArtifactContent(productRoot);

ZipFile.CreateFromDirectory(stagingParent, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

Console.WriteLine("Timeline product artifact created.");
Console.WriteLine($"  Runtime: {options.HostRuntime}");
Console.WriteLine($"  Container runtime: {options.ContainerRuntime}");
Console.WriteLine($"  Version: {version}");
Console.WriteLine($"  Zip: {zipPath}");

static async Task PublishAsync(string repoRoot, string productRoot, PublishSpec spec)
{
    var projectPath = Path.Combine(repoRoot, spec.ProjectPath.Replace('/', Path.DirectorySeparatorChar));
    var outputPath = Path.Combine(productRoot, spec.OutputDirectory);
    Directory.CreateDirectory(outputPath);

    var arguments = new List<string>
    {
        "publish",
        projectPath,
        "-c",
        "Release",
        "-r",
        spec.RuntimeIdentifier,
        "--self-contained",
        spec.SelfContained ? "true" : "false",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-o",
        outputPath
    };
    if (!spec.UseAppHost)
    {
        arguments.Add("-p:UseAppHost=false");
    }
    if (spec.SelfContained)
    {
        arguments.Add("-p:PublishSingleFile=false");
    }

    Console.WriteLine($"Publishing {spec.Name} ({spec.RuntimeIdentifier})...");
    var result = await RunCaptureAsync(repoRoot, ResolveDotnetCommand(), arguments, TimeSpan.FromMinutes(10));
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException($"dotnet publish failed for {spec.Name}.{Environment.NewLine}{result.CombinedText}");
    }
}

static void CopyProductCompose(string repoRoot, string productRoot, string imageTag)
{
    var dockerDirectory = Path.Combine(productRoot, "docker");
    Directory.CreateDirectory(dockerDirectory);

    var templatePath = Path.Combine(repoRoot, "packaging", "docker-compose.product.yml");
    var composeText = File.ReadAllText(templatePath).Replace("__TIMELINE_IMAGE_TAG__", imageTag, StringComparison.Ordinal);
    File.WriteAllText(Path.Combine(productRoot, "docker-compose.yml"), composeText);
    File.WriteAllText(Path.Combine(dockerDirectory, "docker-compose.product.yml"), composeText);

    CopyIfExists(
        Path.Combine(repoRoot, "packaging", "docker", "web.product.Dockerfile"),
        Path.Combine(dockerDirectory, "web.product.Dockerfile"));
    CopyIfExists(
        Path.Combine(repoRoot, "packaging", "docker", "worker.product.Dockerfile"),
        Path.Combine(dockerDirectory, "worker.product.Dockerfile"));
}

static void WriteVersionFile(string productRoot, ReleaseOptions options, string version, string commit)
{
    var metadata = new
    {
        productId = "timeline",
        version,
        commit,
        channel = options.Channel,
        runtimeIdentifier = options.HostRuntime,
        containerRuntimeIdentifier = options.ContainerRuntime,
        createdAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
    };

    File.WriteAllText(
        Path.Combine(productRoot, "VERSION"),
        JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
}

static void WriteNotices(string productRoot)
{
    File.WriteAllText(
        Path.Combine(productRoot, "THIRD-PARTY-NOTICES.txt"),
        "Third-party notices are derived from the Timeline source repository dependencies and container base images." + Environment.NewLine);
}

static void WriteRuntimeReadme(string productRoot)
{
    var runtimeDirectory = Path.Combine(productRoot, "runtime");
    Directory.CreateDirectory(runtimeDirectory);
    File.WriteAllText(
        Path.Combine(runtimeDirectory, "README.txt"),
        "Runtime state, user data, logs, and generated stores are created outside the immutable product artifact." + Environment.NewLine);
}

static void RemoveForbiddenArtifactContent(string productRoot)
{
    var forbiddenDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "bin",
        "obj",
        "data",
        "logs",
        "docs-temp",
        "scripts-temp",
        "node_modules"
    };
    foreach (var directory in Directory
        .EnumerateDirectories(productRoot, "*", SearchOption.AllDirectories)
        .OrderByDescending(path => path.Length))
    {
        var name = Path.GetFileName(directory);
        if (forbiddenDirectoryNames.Contains(name))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    foreach (var file in Directory.EnumerateFiles(productRoot, "settings.json", SearchOption.AllDirectories))
    {
        File.Delete(file);
    }
}

static void CopyIfExists(string sourcePath, string destinationPath)
{
    if (!File.Exists(sourcePath))
    {
        return;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
    File.Copy(sourcePath, destinationPath, overwrite: true);
}

static string ResolveRepoRoot(string startDirectory)
{
    var current = new DirectoryInfo(startDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "README.md")) &&
            Directory.Exists(Path.Combine(current.FullName, "launcher")) &&
            Directory.Exists(Path.Combine(current.FullName, "local-api")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("Timeline repository root was not found.");
}

static string ResolveDotnetCommand()
{
    return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
}

static async Task<ProcessResult> RunCaptureAsync(string workingDirectory, string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
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
            CreateNoWindow = OperatingSystem.IsWindows()
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
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
            return new ProcessResult(124, string.Empty, $"Command timed out: {fileName}");
        }

        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }
    catch (Exception ex)
    {
        return new ProcessResult(127, string.Empty, ex.Message);
    }
}

static void TryKill(Process process)
{
    try
    {
        process.Kill(entireProcessTree: true);
    }
    catch
    {
    }
}

static string SanitizeVersion(string value)
{
    return Regex.Replace((value ?? string.Empty).Trim(), "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
}

static string SanitizeDockerTag(string value)
{
    var sanitized = SanitizeVersion(value).ToLowerInvariant();
    return string.IsNullOrWhiteSpace(sanitized) ? "dev" : sanitized;
}

internal sealed record ReleaseOptions(
    string HostRuntime,
    string ContainerRuntime,
    string OutputDirectory,
    string Channel,
    string? Version)
{
    public static ReleaseOptions Parse(string[] args)
    {
        var hostRuntime = "win-x64";
        var containerRuntime = "linux-x64";
        var outputDirectory = "release";
        var channel = "dev";
        string? version = null;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (TryReadOption(args, ref index, arg, "--runtime", ref hostRuntime) ||
                TryReadOption(args, ref index, arg, "--host-runtime", ref hostRuntime) ||
                TryReadOption(args, ref index, arg, "--container-runtime", ref containerRuntime) ||
                TryReadOption(args, ref index, arg, "--output", ref outputDirectory) ||
                TryReadOption(args, ref index, arg, "--channel", ref channel) ||
                TryReadNullableOption(args, ref index, arg, "--version", ref version))
            {
                continue;
            }

            throw new ArgumentException($"Unknown argument: {arg}");
        }

        return new ReleaseOptions(hostRuntime, containerRuntime, outputDirectory, channel, version);
    }

    private static bool TryReadOption(string[] args, ref int index, string arg, string optionName, ref string value)
    {
        if (arg.StartsWith($"{optionName}=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg[(optionName.Length + 1)..];
            return true;
        }

        if (arg.Equals(optionName, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
        {
            value = args[++index];
            return true;
        }

        return false;
    }

    private static bool TryReadNullableOption(string[] args, ref int index, string arg, string optionName, ref string? value)
    {
        var text = value ?? string.Empty;
        if (TryReadOption(args, ref index, arg, optionName, ref text))
        {
            value = text;
            return true;
        }

        return false;
    }
}

internal sealed record PublishSpec(
    string Name,
    string ProjectPath,
    string OutputDirectory,
    string RuntimeIdentifier,
    bool SelfContained,
    bool UseAppHost);

internal sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public string CombinedText => string.Join(
        Environment.NewLine,
        new[] { Output, Error }.Where(text => !string.IsNullOrWhiteSpace(text)));
}
