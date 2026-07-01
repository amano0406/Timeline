using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

var options = WindowsInstallerOptions.Parse(args);
if (options.ShowHelp)
{
    PrintHelp();
    return 0;
}

if (!OperatingSystem.IsWindows())
{
    WriteError("Timeline Windows Installer is available only on Windows.", options.JsonOutput);
    return 2;
}

try
{
    var artifactPath = ResolveArtifactPath(options.ArtifactPath);
    var installDirectory = ResolveInstallDirectory(options.InstallDirectory);
    var plan = BuildPlan(artifactPath, installDirectory, options);

    if (options.PlanOnly || options.DryRun)
    {
        WriteResult(plan, options.JsonOutput);
        return plan.Blockers.Count == 0 ? 0 : 1;
    }

    if (plan.Blockers.Count > 0)
    {
        WriteResult(plan, options.JsonOutput);
        return 1;
    }

    var result = Install(plan, options);
    WriteResult(result, options.JsonOutput);
    return result.Blockers.Count == 0 ? 0 : 1;
}
catch (Exception ex)
{
    WriteError(ex.Message, options.JsonOutput);
    return 1;
}

static TimelineWindowsInstallResult Install(TimelineWindowsInstallResult plan, WindowsInstallerOptions options)
{
    var stagingRoot = Path.Combine(
        Path.GetTempPath(),
        "TimelineInstaller",
        DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
    var extractedRoot = Path.Combine(stagingRoot, "Timeline");

    try
    {
        Directory.CreateDirectory(stagingRoot);
        ZipFile.ExtractToDirectory(plan.ArtifactPath, stagingRoot);
        if (!Directory.Exists(extractedRoot))
        {
            throw new DirectoryNotFoundException("The artifact did not contain the expected Timeline root directory.");
        }

        if (Directory.Exists(plan.InstallDirectory))
        {
            if (!options.Force)
            {
                throw new InvalidOperationException("The install directory already exists. Use --force only when replacing application files is intended.");
            }

            DeleteReplaceableApplicationContent(plan.InstallDirectory);
        }
        else
        {
            Directory.CreateDirectory(plan.InstallDirectory);
        }

        CopyDirectory(extractedRoot, plan.InstallDirectory);
        var shortcut = TimelineLauncherShortcutService.Install(plan.InstallDirectory);
        var uninstall = TimelineWindowsUninstallRegistrationService.Register(plan.InstallDirectory);

        plan.State = shortcut.Registered && uninstall.Registered ? "installed" : "installed_with_registration_warnings";
        plan.Shortcut = shortcut;
        plan.UninstallRegistration = uninstall;
        plan.Messages.Add("Timeline application files were installed.");
        if (shortcut.Registered)
        {
            plan.Messages.Add("Start Menu shortcut was registered.");
        }
        else
        {
            plan.Warnings.Add(shortcut.Message);
        }

        if (uninstall.Registered)
        {
            plan.Messages.Add("Windows Apps & Features uninstall entry was registered.");
        }
        else
        {
            plan.Warnings.Add(uninstall.Message);
        }

        WriteInstallReceipt(plan);
        return plan;
    }
    finally
    {
        TryDeleteDirectory(stagingRoot);
    }
}

static TimelineWindowsInstallResult BuildPlan(string artifactPath, string installDirectory, WindowsInstallerOptions options)
{
    var result = new TimelineWindowsInstallResult
    {
        State = "planned",
        ArtifactPath = artifactPath,
        InstallDirectory = installDirectory,
        Force = options.Force,
    };

    if (!File.Exists(artifactPath))
    {
        result.Blockers.Add($"Artifact was not found: {artifactPath}");
        return result;
    }

    ValidateArtifactShape(artifactPath, result);
    ValidateInstallDirectory(installDirectory, options, result);
    return result;
}

static void ValidateArtifactShape(string artifactPath, TimelineWindowsInstallResult result)
{
    try
    {
        using var archive = ZipFile.OpenRead(artifactPath);
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requiredEntries = new[]
        {
            "Timeline/VERSION",
            "Timeline/launcher/Timeline.Launcher.exe",
            "Timeline/launcher-tray/Timeline.Launcher.Tray.exe",
            "Timeline/local-api/Timeline.LocalApi.exe",
            "Timeline/docker-compose.yml"
        };

        foreach (var requiredEntry in requiredEntries)
        {
            if (!entries.Contains(requiredEntry))
            {
                result.Blockers.Add($"Artifact is missing required entry: {requiredEntry}");
            }
        }

        if (entries.Any(entry => entry.Contains("/settings.json", StringComparison.OrdinalIgnoreCase)))
        {
            result.Blockers.Add("Artifact must not contain settings.json.");
        }

        if (entries.Any(entry => entry.StartsWith("Timeline/data/", StringComparison.OrdinalIgnoreCase)))
        {
            result.Blockers.Add("Artifact must not contain Timeline user data.");
        }

        var versionEntry = archive.GetEntry("Timeline/VERSION");
        if (versionEntry is not null)
        {
            using var stream = versionEntry.Open();
            using var document = JsonDocument.Parse(stream);
            result.Version = document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString() ?? ""
                : "";
            result.RuntimeIdentifier = document.RootElement.TryGetProperty("runtimeIdentifier", out var runtime)
                ? runtime.GetString() ?? ""
                : "";

            if (!result.RuntimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
            {
                result.Blockers.Add($"Artifact runtime is not Windows: {result.RuntimeIdentifier}");
            }
        }
    }
    catch (InvalidDataException ex)
    {
        result.Blockers.Add($"Artifact is not a valid ZIP archive. {ex.Message}");
    }
    catch (JsonException ex)
    {
        result.Blockers.Add($"Artifact VERSION metadata could not be parsed. {ex.Message}");
    }
}

static void ValidateInstallDirectory(string installDirectory, WindowsInstallerOptions options, TimelineWindowsInstallResult result)
{
    if (Path.GetPathRoot(installDirectory) == Path.GetFullPath(installDirectory))
    {
        result.Blockers.Add("Install directory must not be a drive root.");
        return;
    }

    if (!Directory.Exists(installDirectory))
    {
        result.Messages.Add("Install directory will be created.");
        return;
    }

    var existingEntries = Directory.EnumerateFileSystemEntries(installDirectory).Take(2).ToArray();
    if (existingEntries.Length == 0)
    {
        result.Messages.Add("Install directory exists and is empty.");
        return;
    }

    if (options.Force)
    {
        result.Warnings.Add("Existing replaceable application files will be replaced. User data, settings, logs, runtime state, and managed products are preserved.");
    }
    else
    {
        result.Blockers.Add("Install directory already exists and is not empty. Re-run with --force only if this is the Timeline application directory to replace.");
    }
}

static void DeleteReplaceableApplicationContent(string installDirectory)
{
    var preserveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "data",
        "logs",
        "runtime",
        "products",
        "settings.json"
    };

    foreach (var directory in Directory.EnumerateDirectories(installDirectory))
    {
        if (!preserveNames.Contains(Path.GetFileName(directory)))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    foreach (var file in Directory.EnumerateFiles(installDirectory))
    {
        if (!preserveNames.Contains(Path.GetFileName(file)))
        {
            File.Delete(file);
        }
    }
}

static void CopyDirectory(string sourceDirectory, string destinationDirectory)
{
    Directory.CreateDirectory(destinationDirectory);
    foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
    }

    foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        var destinationPath = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationDirectory);
        File.Copy(file, destinationPath, overwrite: true);
    }
}

static void WriteInstallReceipt(TimelineWindowsInstallResult result)
{
    var receiptDirectory = Path.Combine(result.InstallDirectory, "runtime");
    Directory.CreateDirectory(receiptDirectory);
    File.WriteAllText(
        Path.Combine(receiptDirectory, "windows-install-receipt.json"),
        JsonSerializer.Serialize(result, JsonOptions()) + Environment.NewLine);
}

static string ResolveArtifactPath(string? explicitArtifactPath)
{
    if (!string.IsNullOrWhiteSpace(explicitArtifactPath))
    {
        return Path.GetFullPath(explicitArtifactPath);
    }

    var currentDirectory = Directory.GetCurrentDirectory();
    var candidates = Directory
        .EnumerateFiles(currentDirectory, "Timeline-win-*.zip", SearchOption.TopDirectoryOnly)
        .Concat(Directory.Exists(Path.Combine(currentDirectory, "artifacts"))
            ? Directory.EnumerateFiles(Path.Combine(currentDirectory, "artifacts"), "Timeline-win-*.zip", SearchOption.TopDirectoryOnly)
            : [])
        .Where(path => !Path.GetFileName(path).Contains("-setup", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .ToArray();

    return candidates.Length == 1
        ? Path.GetFullPath(candidates[0])
        : throw new InvalidOperationException("Specify --artifact because a single Timeline Windows artifact could not be resolved.");
}

static string ResolveInstallDirectory(string? explicitInstallDirectory)
{
    if (!string.IsNullOrWhiteSpace(explicitInstallDirectory))
    {
        return Path.GetFullPath(explicitInstallDirectory);
    }

    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(localAppData))
    {
        throw new InvalidOperationException("LocalApplicationData folder could not be resolved.");
    }

    return Path.Combine(localAppData, "Programs", "Timeline");
}

static void TryDeleteDirectory(string directory)
{
    try
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    catch
    {
    }
}

static void WriteResult(TimelineWindowsInstallResult result, bool jsonOutput)
{
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions()));
        return;
    }

    Console.WriteLine($"Timeline Windows installer: {result.State}");
    Console.WriteLine($"  artifact: {result.ArtifactPath}");
    Console.WriteLine($"  install directory: {result.InstallDirectory}");
    if (!string.IsNullOrWhiteSpace(result.Version))
    {
        Console.WriteLine($"  version: {result.Version}");
    }

    foreach (var message in result.Messages)
    {
        Console.WriteLine($"  info: {message}");
    }

    foreach (var warning in result.Warnings)
    {
        Console.WriteLine($"  warning: {warning}");
    }

    foreach (var blocker in result.Blockers)
    {
        Console.WriteLine($"  blocker: {blocker}");
    }
}

static void WriteError(string message, bool jsonOutput)
{
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { state = "failed", error = message }, JsonOptions()));
        return;
    }

    Console.Error.WriteLine(message);
}

static JsonSerializerOptions JsonOptions()
    => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

static void PrintHelp()
{
    Console.WriteLine("""
        Timeline Windows Installer

        Usage:
          Timeline.WindowsInstaller.exe --artifact <Timeline-win-x64-version.zip> [options]

        Options:
          --artifact <path>      Timeline Windows product artifact ZIP.
          --install-dir <path>   Install directory. Defaults to %LOCALAPPDATA%\Programs\Timeline.
          --force                Replace existing application files while preserving user data.
          --plan                 Show install plan only.
          --dry-run              Alias of --plan.
          --json                 Print machine-readable JSON.
          --help                 Show help.
        """);
}

internal sealed class TimelineWindowsInstallResult
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("artifactPath")]
    public string ArtifactPath { get; set; } = "";

    [JsonPropertyName("installDirectory")]
    public string InstallDirectory { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("runtimeIdentifier")]
    public string RuntimeIdentifier { get; set; } = "";

    [JsonPropertyName("force")]
    public bool Force { get; set; }

    [JsonPropertyName("messages")]
    public List<string> Messages { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("blockers")]
    public List<string> Blockers { get; set; } = [];

    [JsonPropertyName("shortcut")]
    public TimelineLauncherShortcutStatus? Shortcut { get; set; }

    [JsonPropertyName("uninstallRegistration")]
    public TimelineWindowsUninstallRegistrationStatus? UninstallRegistration { get; set; }
}

internal sealed record WindowsInstallerOptions(
    string? ArtifactPath,
    string? InstallDirectory,
    bool Force,
    bool PlanOnly,
    bool DryRun,
    bool JsonOutput,
    bool ShowHelp)
{
    public static WindowsInstallerOptions Parse(string[] args)
    {
        string? artifactPath = null;
        string? installDirectory = null;
        var force = false;
        var planOnly = false;
        var dryRun = false;
        var jsonOutput = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (ReadValue(args, ref index, arg, "--artifact", ref artifactPath) ||
                ReadValue(args, ref index, arg, "--install-dir", ref installDirectory))
            {
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--force":
                    force = true;
                    break;
                case "--plan":
                    planOnly = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new WindowsInstallerOptions(artifactPath, installDirectory, force, planOnly, dryRun, jsonOutput, showHelp);
    }

    private static bool ReadValue(string[] args, ref int index, string arg, string optionName, ref string? value)
    {
        if (arg.StartsWith($"{optionName}=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg[(optionName.Length + 1)..];
            return true;
        }

        if (arg.Equals(optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{optionName} requires a value.");
            }

            value = args[++index];
            return true;
        }

        return false;
    }
}
