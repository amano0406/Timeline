using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

var options = ReleaseOptions.Parse(args);

if (options.BuildAll)
{
    var productsRoot = Path.GetFullPath(options.ProductsRoot);
    var results = new List<SubProductArtifactBuildResult>();
    foreach (var product in ProductBuildSpec.All)
    {
        var productRoot = Path.Combine(productsRoot, product.ProductName);
        if (!Directory.Exists(productRoot))
        {
            results.Add(SubProductArtifactBuildResult.Missing(
                product.ProductId,
                product.ProductName,
                productRoot,
                options.RuntimeIdentifier,
                "Product repository was not found."));
            continue;
        }

        results.Add(await BuildArtifactAsync(options with
        {
            ProductRoot = productRoot,
            ProductName = product.ProductName,
            ProductId = product.ProductId,
            Version = null,
        }));
    }

    WriteManifest(options, results);
    Console.WriteLine("Sub-product artifact matrix created.");
    Console.WriteLine($"  Runtime: {options.RuntimeIdentifier}");
    Console.WriteLine($"  Output: {Path.GetFullPath(options.OutputDirectory)}");
    Console.WriteLine($"  Created: {results.Count(row => row.State.Equals("created", StringComparison.OrdinalIgnoreCase))}");
    Console.WriteLine($"  Missing: {results.Count(row => row.State.Equals("missing", StringComparison.OrdinalIgnoreCase))}");
    return;
}

var result = await BuildArtifactAsync(options);
Console.WriteLine("Sub-product artifact created.");
Console.WriteLine($"  Product: {result.ProductName}");
Console.WriteLine($"  Runtime: {result.RuntimeIdentifier}");
Console.WriteLine($"  Version: {result.Version}");
Console.WriteLine($"  Zip: {result.ArtifactPath}");

static async Task<SubProductArtifactBuildResult> BuildArtifactAsync(ReleaseOptions options)
{
    var productRoot = Path.GetFullPath(options.ProductRoot);
    if (!Directory.Exists(productRoot))
    {
        throw new DirectoryNotFoundException($"Product root was not found: {productRoot}");
    }

    var productName = string.IsNullOrWhiteSpace(options.ProductName)
        ? new DirectoryInfo(productRoot).Name
        : SanitizeSegment(options.ProductName);
    var productId = string.IsNullOrWhiteSpace(options.ProductId)
        ? NormalizeProductId(productName)
        : NormalizeProductId(options.ProductId);
    var runtimeIdentifier = string.IsNullOrWhiteSpace(options.RuntimeIdentifier)
        ? RuntimeInformation.RuntimeIdentifier
        : options.RuntimeIdentifier;
    var artifactRuntimeName = ToArtifactRuntimeName(runtimeIdentifier);
    var version = string.IsNullOrWhiteSpace(options.Version)
        ? SanitizeVersion((await RunCaptureAsync(productRoot, "git", ["describe", "--tags", "--dirty", "--always"], TimeSpan.FromSeconds(10))).Output.Trim())
        : SanitizeVersion(options.Version);
    if (string.IsNullOrWhiteSpace(version))
    {
        version = "0.0.0-dev";
    }

    var commit = (await RunCaptureAsync(productRoot, "git", ["rev-parse", "--short", "HEAD"], TimeSpan.FromSeconds(10))).Output.Trim();
    var outputRoot = Path.GetFullPath(options.OutputDirectory);
    var stagingParent = Path.Combine(outputRoot, "staging", productName);
    var artifactRoot = Path.Combine(stagingParent, productName);
    var zipPath = Path.Combine(outputRoot, $"{productName}-{artifactRuntimeName}-{version}.zip");

    if (Directory.Exists(stagingParent))
    {
        Directory.Delete(stagingParent, recursive: true);
    }
    Directory.CreateDirectory(artifactRoot);
    Directory.CreateDirectory(outputRoot);
    if (File.Exists(zipPath))
    {
        File.Delete(zipPath);
    }

    CopyProductFiles(productRoot, artifactRoot, outputRoot);
    WriteVersionFile(artifactRoot, productId, productName, version, commit, options.Channel, runtimeIdentifier);
    WriteRuntimeReadme(artifactRoot);
    CreateProductZip(stagingParent, zipPath, runtimeIdentifier);

    return new SubProductArtifactBuildResult(
        "created",
        productId,
        productName,
        productRoot,
        runtimeIdentifier,
        ToArtifactRuntimeName(runtimeIdentifier),
        version,
        commit,
        Path.GetFullPath(zipPath),
        new FileInfo(zipPath).Length,
        string.Empty);
}

static void WriteManifest(ReleaseOptions options, IReadOnlyList<SubProductArtifactBuildResult> results)
{
    var outputRoot = Path.GetFullPath(options.OutputDirectory);
    Directory.CreateDirectory(outputRoot);
    var runtimeName = ToArtifactRuntimeName(options.RuntimeIdentifier);
    var manifestName = string.IsNullOrWhiteSpace(options.ManifestName)
        ? $"sub-product-artifacts-{runtimeName}.json"
        : SanitizeSegment(options.ManifestName);
    if (!manifestName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
        manifestName += ".json";
    }

    var manifest = new SubProductArtifactManifest(
        "timeline_sub_product_artifact_manifest",
        options.Channel,
        options.RuntimeIdentifier,
        runtimeName,
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        results.ToList());
    File.WriteAllText(
        Path.Combine(outputRoot, manifestName),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);
}

static void CopyProductFiles(string sourceRoot, string destinationRoot, string outputRoot)
{
    var sourceFull = EnsureTrailingSeparator(Path.GetFullPath(sourceRoot));
    var destinationFull = EnsureTrailingSeparator(Path.GetFullPath(destinationRoot));
    var outputFull = EnsureTrailingSeparator(Path.GetFullPath(outputRoot));

    foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
    {
        var directoryFull = EnsureTrailingSeparator(Path.GetFullPath(directory));
        if (ShouldExcludeDirectory(directoryFull, sourceFull, destinationFull, outputFull))
        {
            continue;
        }

        var relativeDirectory = Path.GetRelativePath(sourceRoot, directory);
        Directory.CreateDirectory(Path.Combine(destinationRoot, relativeDirectory));
    }

    foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
    {
        var directoryFull = EnsureTrailingSeparator(Path.GetFullPath(Path.GetDirectoryName(file) ?? sourceRoot));
        if (ShouldExcludeDirectory(directoryFull, sourceFull, destinationFull, outputFull) ||
            ShouldExcludeFile(file))
        {
            continue;
        }

        var relativePath = Path.GetRelativePath(sourceRoot, file);
        var destinationPath = Path.Combine(destinationRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationRoot);
        File.Copy(file, destinationPath, overwrite: true);
    }
}

static bool ShouldExcludeDirectory(
    string directoryFull,
    string sourceFull,
    string destinationFull,
    string outputFull)
{
    if (directoryFull.StartsWith(destinationFull, StringComparison.OrdinalIgnoreCase) ||
        directoryFull.StartsWith(outputFull, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    var relative = Path.GetRelativePath(sourceFull, directoryFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    return segments.Any(segment => ReleaseExclusionRules.ForbiddenDirectoryNames.Contains(segment));
}

static bool ShouldExcludeFile(string path)
{
    var fileName = Path.GetFileName(path);
    if (ReleaseExclusionRules.ForbiddenFileNames.Contains(fileName))
    {
        return true;
    }

    var extension = Path.GetExtension(path);
    if (ReleaseExclusionRules.ForbiddenFileExtensions.Contains(extension))
    {
        return true;
    }

    return fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase);
}

static void WriteVersionFile(
    string artifactRoot,
    string productId,
    string productName,
    string version,
    string commit,
    string channel,
    string runtimeIdentifier)
{
    var metadata = new
    {
        artifactType = "timeline_sub_product_artifact",
        productId,
        productName,
        version,
        commit,
        channel,
        runtimeIdentifier,
        createdAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
    };

    File.WriteAllText(
        Path.Combine(artifactRoot, "VERSION"),
        JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
}

static void WriteRuntimeReadme(string artifactRoot)
{
    var runtimeDirectory = Path.Combine(artifactRoot, "runtime");
    Directory.CreateDirectory(runtimeDirectory);
    File.WriteAllText(
        Path.Combine(runtimeDirectory, "README.txt"),
        "Runtime state, settings, user input, generated output, logs, and Docker volumes are created outside this immutable sub-product artifact." + Environment.NewLine);
}

static void CreateProductZip(string sourceDirectory, string zipPath, string runtimeIdentifier)
{
    using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
    foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
    {
        var relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
        var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
        if (ShouldMarkExecutable(relativePath, runtimeIdentifier))
        {
            entry.ExternalAttributes = Convert.ToInt32("100755", 8) << 16;
        }

        using var input = File.OpenRead(file);
        using var output = entry.Open();
        input.CopyTo(output);
    }
}

static bool ShouldMarkExecutable(string relativePath, string runtimeIdentifier)
{
    if (!runtimeIdentifier.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) &&
        !runtimeIdentifier.StartsWith("linux-", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var fileName = Path.GetFileName(relativePath);
    return string.IsNullOrEmpty(Path.GetExtension(fileName)) ||
        fileName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
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

static string EnsureTrailingSeparator(string path)
{
    return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}

static string SanitizeSegment(string value)
{
    var sanitized = Regex.Replace((value ?? string.Empty).Trim(), "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
    return string.IsNullOrWhiteSpace(sanitized) ? "product" : sanitized;
}

static string SanitizeVersion(string value)
{
    var sanitized = Regex.Replace((value ?? string.Empty).Trim(), "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
    return string.IsNullOrWhiteSpace(sanitized) ? string.Empty : sanitized;
}

static string NormalizeProductId(string value)
{
    var normalized = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    if (normalized.StartsWith("timelinefor", StringComparison.OrdinalIgnoreCase))
    {
        normalized = normalized["timelinefor".Length..].Trim('-');
    }

    return string.IsNullOrWhiteSpace(normalized) ? "product" : normalized;
}

static string ToArtifactRuntimeName(string runtimeIdentifier)
{
    return runtimeIdentifier switch
    {
        "osx-arm64" => "macos-arm64",
        "osx-x64" => "macos-x64",
        _ => string.IsNullOrWhiteSpace(runtimeIdentifier) ? "unknown" : runtimeIdentifier
    };
}

internal sealed record ReleaseOptions(
    string ProductRoot,
    string ProductName,
    string ProductId,
    string RuntimeIdentifier,
    string OutputDirectory,
    string Channel,
    string? Version,
    bool BuildAll,
    string ProductsRoot,
    string ManifestName)
{
    public static ReleaseOptions Parse(string[] args)
    {
        var productRoot = string.Empty;
        var productName = string.Empty;
        var productId = string.Empty;
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        var outputDirectory = "release";
        var channel = "dev";
        string? version = null;
        var buildAll = false;
        var productsRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".."));
        var manifestName = string.Empty;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.Equals("--all", StringComparison.OrdinalIgnoreCase))
            {
                buildAll = true;
                continue;
            }

            if (TryReadOption(args, ref index, arg, "--repo", ref productRoot) ||
                TryReadOption(args, ref index, arg, "--product-root", ref productRoot) ||
                TryReadOption(args, ref index, arg, "--product-name", ref productName) ||
                TryReadOption(args, ref index, arg, "--product-id", ref productId) ||
                TryReadOption(args, ref index, arg, "--runtime", ref runtimeIdentifier) ||
                TryReadOption(args, ref index, arg, "--output", ref outputDirectory) ||
                TryReadOption(args, ref index, arg, "--channel", ref channel) ||
                TryReadOption(args, ref index, arg, "--products-root", ref productsRoot) ||
                TryReadOption(args, ref index, arg, "--manifest", ref manifestName) ||
                TryReadNullableOption(args, ref index, arg, "--version", ref version))
            {
                continue;
            }

            throw new ArgumentException($"Unknown argument: {arg}");
        }

        if (!buildAll && string.IsNullOrWhiteSpace(productRoot))
        {
            throw new ArgumentException("Product root is required. Use --repo <path>.");
        }

        return new ReleaseOptions(
            productRoot,
            productName,
            productId,
            runtimeIdentifier,
            outputDirectory,
            channel,
            version,
            buildAll,
            productsRoot,
            manifestName);
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

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal sealed record ProductBuildSpec(string ProductId, string ProductName)
{
    public static readonly ProductBuildSpec[] All =
    [
        new("audio", "TimelineForAudio"),
        new("image", "TimelineForImage"),
        new("video", "TimelineForVideo"),
        new("chatgpt", "TimelineForChatGPT"),
        new("windows-codex", "TimelineForWindowsCodex"),
        new("pc", "TimelineForPcInfo"),
    ];
}

internal sealed record SubProductArtifactManifest(
    string ArtifactType,
    string Channel,
    string RuntimeIdentifier,
    string RuntimeName,
    string CreatedAt,
    List<SubProductArtifactBuildResult> Artifacts);

internal sealed record SubProductArtifactBuildResult(
    string State,
    string ProductId,
    string ProductName,
    string ProductRoot,
    string RuntimeIdentifier,
    string RuntimeName,
    string Version,
    string Commit,
    string ArtifactPath,
    long SizeBytes,
    string Message)
{
    public static SubProductArtifactBuildResult Missing(
        string productId,
        string productName,
        string productRoot,
        string runtimeIdentifier,
        string message)
    {
        return new SubProductArtifactBuildResult(
            "missing",
            productId,
            productName,
            productRoot,
            runtimeIdentifier,
            ToRuntimeName(runtimeIdentifier),
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            message);
    }

    private static string ToRuntimeName(string runtimeIdentifier)
    {
        return runtimeIdentifier switch
        {
            "osx-arm64" => "macos-arm64",
            "osx-x64" => "macos-x64",
            _ => string.IsNullOrWhiteSpace(runtimeIdentifier) ? "unknown" : runtimeIdentifier,
        };
    }
}

internal static class ReleaseExclusionRules
{
    public static readonly HashSet<string> ForbiddenDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docker",
        ".git",
        ".githooks",
        ".github",
        ".mypy_cache",
        ".playwright-cli",
        ".pytest_cache",
        ".ruff_cache",
        ".runtime",
        ".venv",
        "__pycache__",
        "bin",
        "build",
        "data",
        "dist",
        "docs-temp",
        "env",
        "input",
        "logs",
        "node_modules",
        "obj",
        "output",
        "outputs",
        "release",
        "scripts-temp",
        "temp",
        "tests",
        "tmp",
        "venv",
    };

    public static readonly HashSet<string> ForbiddenFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store",
        "Thumbs.db",
        "settings.json",
    };

    public static readonly HashSet<string> ForbiddenFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log",
        ".pyc",
        ".pyo",
        ".tmp",
    };
}
