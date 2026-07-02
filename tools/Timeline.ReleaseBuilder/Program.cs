using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

var options = ReleaseOptions.Parse(args);
if (!string.IsNullOrWhiteSpace(options.VerifyWindowsInstallerPath))
{
    var verification = VerifyWindowsInstallerBundle(
        Path.GetFullPath(options.VerifyWindowsInstallerPath),
        options.RequireWindowsExecutionTrust);
    if (options.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(verification, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
    else
    {
        PrintWindowsInstallerVerification(verification);
    }

    Environment.ExitCode = verification.Blockers.Count == 0 ? 0 : 1;
    return;
}

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
var artifactRuntimeName = ToArtifactRuntimeName(options.HostRuntime);
var zipPath = Path.Combine(outputRoot, $"Timeline-{artifactRuntimeName}-{version}.zip");

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

CreateMacAppBundle(productRoot, options.HostRuntime, version);
CopyProductCompose(repoRoot, productRoot, imageTag);
CopyIfExists(Path.Combine(repoRoot, "docker-compose.gpu.yml"), Path.Combine(productRoot, "docker-compose.gpu.yml"));
CopyIfExists(Path.Combine(repoRoot, "README.md"), Path.Combine(productRoot, "README.md"));
CopyIfExists(Path.Combine(repoRoot, "docs", "distribution-artifacts.md"), Path.Combine(productRoot, "docs", "distribution-artifacts.md"));
WriteVersionFile(productRoot, options, version, commit);
WriteNotices(productRoot);
WriteRuntimeReadme(productRoot);
RemoveForbiddenArtifactContent(productRoot);

CreateProductZip(stagingParent, zipPath, options.HostRuntime);
WriteArtifactManifest(outputRoot, options, version, commit, zipPath);
if (options.WindowsInstaller)
{
    CreateWindowsInstallerBundle(repoRoot, outputRoot, options, version, commit, zipPath);
}

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

static void CreateMacAppBundle(string productRoot, string hostRuntime, string version)
{
    if (!hostRuntime.StartsWith("osx-", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var launcherTrayDirectory = Path.Combine(productRoot, "launcher-tray");
    var launcherTrayExecutable = Path.Combine(launcherTrayDirectory, "Timeline.Launcher.Tray");
    if (!File.Exists(launcherTrayExecutable))
    {
        throw new FileNotFoundException("Mac resident Launcher executable was not found.", launcherTrayExecutable);
    }

    var appRoot = Path.Combine(productRoot, "Timeline.app");
    var contentsDirectory = Path.Combine(appRoot, "Contents");
    var macOsDirectory = Path.Combine(contentsDirectory, "MacOS");
    Directory.CreateDirectory(macOsDirectory);

    CopyDirectory(launcherTrayDirectory, macOsDirectory);
    File.WriteAllText(Path.Combine(contentsDirectory, "Info.plist"), BuildMacAppInfoPlist(version), System.Text.Encoding.UTF8);
    File.WriteAllText(Path.Combine(contentsDirectory, "PkgInfo"), "APPL????", System.Text.Encoding.ASCII);
}

static string BuildMacAppInfoPlist(string version)
{
    var bundleVersion = ResolveMacBundleVersion(version);
    return $"""
       <?xml version="1.0" encoding="UTF-8"?>
       <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
       <plist version="1.0">
       <dict>
         <key>CFBundleDevelopmentRegion</key>
         <string>ja_JP</string>
         <key>CFBundleExecutable</key>
         <string>Timeline.Launcher.Tray</string>
         <key>CFBundleIdentifier</key>
         <string>com.amanosystemlab.timeline</string>
         <key>CFBundleInfoDictionaryVersion</key>
         <string>6.0</string>
         <key>CFBundleName</key>
         <string>Timeline</string>
         <key>CFBundleDisplayName</key>
         <string>Timeline</string>
         <key>CFBundlePackageType</key>
         <string>APPL</string>
         <key>CFBundleShortVersionString</key>
         <string>{bundleVersion}</string>
         <key>CFBundleVersion</key>
         <string>{bundleVersion}</string>
         <key>LSApplicationCategoryType</key>
         <string>public.app-category.productivity</string>
         <key>LSMinimumSystemVersion</key>
         <string>13.0</string>
         <key>LSUIElement</key>
         <true/>
         <key>NSHighResolutionCapable</key>
         <true/>
       </dict>
       </plist>
       """;
}

static string ResolveMacBundleVersion(string version)
{
    var match = Regex.Match(version, @"\d+(?:\.\d+){0,2}");
    return match.Success ? match.Value : "0.0.0";
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

static void WriteArtifactManifest(
    string outputRoot,
    ReleaseOptions options,
    string version,
    string commit,
    string zipPath)
{
    Directory.CreateDirectory(outputRoot);
    var artifactRuntimeName = ToArtifactRuntimeName(options.HostRuntime);
    var fileInfo = new FileInfo(zipPath);
    var manifest = new TimelineArtifactManifest(
        ManifestType: "timeline_product_artifact_manifest",
        ProductId: "timeline",
        ProductName: "Timeline",
        Channel: options.Channel,
        Version: version,
        Commit: commit,
        RuntimeIdentifier: options.HostRuntime,
        RuntimeName: artifactRuntimeName,
        ContainerRuntimeIdentifier: options.ContainerRuntime,
        CreatedAt: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        Artifact: new TimelineArtifactManifestItem(
            ArtifactKind: "built_product_artifact",
            FileName: fileInfo.Name,
            Path: fileInfo.FullName,
            SizeBytes: fileInfo.Length,
            Sha256: ComputeSha256(zipPath),
            AppBundleIncluded: options.HostRuntime.StartsWith("osx-", StringComparison.OrdinalIgnoreCase)));

    File.WriteAllText(
        Path.Combine(outputRoot, $"timeline-artifact-{artifactRuntimeName}.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);
}

static void CreateWindowsInstallerBundle(
    string repoRoot,
    string outputRoot,
    ReleaseOptions options,
    string version,
    string commit,
    string productZipPath)
{
    if (!options.HostRuntime.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("--windows-installer can be used only with a Windows host runtime.");
    }

    var artifactRuntimeName = ToArtifactRuntimeName(options.HostRuntime);
    var installerStagingParent = Path.Combine(outputRoot, "installer-staging");
    var installerRoot = Path.Combine(installerStagingParent, "Timeline-Setup");
    var installerAppRoot = Path.Combine(installerRoot, "installer");
    var artifactsRoot = Path.Combine(installerRoot, "artifacts");
    var setupZipPath = Path.Combine(outputRoot, $"Timeline-{artifactRuntimeName}-{version}-setup.zip");

    if (Directory.Exists(installerStagingParent))
    {
        Directory.Delete(installerStagingParent, recursive: true);
    }

    if (File.Exists(setupZipPath))
    {
        File.Delete(setupZipPath);
    }

    Directory.CreateDirectory(installerAppRoot);
    Directory.CreateDirectory(artifactsRoot);

    Console.WriteLine($"Publishing Windows installer ({options.HostRuntime})...");
    var publishResult = RunCaptureAsync(
        repoRoot,
        ResolveDotnetCommand(),
        [
            "publish",
            Path.Combine(repoRoot, "installer-windows", "Timeline.WindowsInstaller.csproj"),
            "-c",
            "Release",
            "-r",
            options.HostRuntime,
            "--self-contained",
            "true",
            "-p:DebugType=none",
            "-p:DebugSymbols=false",
            "-p:PublishSingleFile=false",
            "-o",
            installerAppRoot
        ],
        TimeSpan.FromMinutes(10)).GetAwaiter().GetResult();
    if (publishResult.ExitCode != 0)
    {
        throw new InvalidOperationException($"dotnet publish failed for Windows installer.{Environment.NewLine}{publishResult.CombinedText}");
    }

    var productZipCopyPath = Path.Combine(artifactsRoot, Path.GetFileName(productZipPath));
    File.Copy(productZipPath, productZipCopyPath, overwrite: true);
    WriteWindowsInstallerReadme(installerRoot, productZipCopyPath);
    WriteWindowsInstallerManifest(installerRoot, options, version, commit, productZipCopyPath);

    CreateProductZip(installerStagingParent, setupZipPath, options.HostRuntime);
    WriteWindowsInstallerExternalManifest(outputRoot, options, version, commit, setupZipPath, productZipCopyPath);
    var verification = VerifyWindowsInstallerBundle(setupZipPath, options.RequireWindowsExecutionTrust);
    if (verification.Blockers.Count > 0)
    {
        throw new InvalidOperationException(
            "Windows installer bundle verification failed." +
            Environment.NewLine +
            string.Join(Environment.NewLine, verification.Blockers));
    }

    Console.WriteLine("Timeline Windows installer bundle created.");
    Console.WriteLine("Timeline Windows installer bundle verified.");
    Console.WriteLine($"  Setup: {setupZipPath}");
}

static void WriteWindowsInstallerReadme(string installerRoot, string productZipPath)
{
    var productZipName = Path.GetFileName(productZipPath);
    File.WriteAllText(
        Path.Combine(installerRoot, "README.txt"),
        $"""
        Timeline Windows setup bundle

        This bundle installs a built Timeline product artifact without using bat, sh, or command wrappers.

        Contents:
        - installer/Timeline.WindowsInstaller.exe
        - artifacts/{productZipName}

        Plan only:
          installer\Timeline.WindowsInstaller.exe --artifact artifacts\{productZipName} --plan

        Install:
          installer\Timeline.WindowsInstaller.exe --artifact artifacts\{productZipName}

        Default install directory:
          %LOCALAPPDATA%\Programs\Timeline

        Existing application files are not replaced unless --force is supplied. User data, settings, logs,
        runtime state, and managed products are preserved when --force is used.
        """ + Environment.NewLine);
}

static void WriteWindowsInstallerManifest(
    string installerRoot,
    ReleaseOptions options,
    string version,
    string commit,
    string productZipPath)
{
    var productZip = new FileInfo(productZipPath);
    var manifest = new TimelineWindowsInstallerManifest(
        ManifestType: "timeline_windows_installer_bundle_manifest",
        ProductId: "timeline",
        ProductName: "Timeline",
        Channel: options.Channel,
        Version: version,
        Commit: commit,
        RuntimeIdentifier: options.HostRuntime,
        RuntimeName: ToArtifactRuntimeName(options.HostRuntime),
        CreatedAt: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        ProductArtifact: new TimelineArtifactManifestItem(
            ArtifactKind: "built_product_artifact",
            FileName: productZip.Name,
            Path: Path.Combine("artifacts", productZip.Name).Replace('\\', '/'),
            SizeBytes: productZip.Length,
            Sha256: ComputeSha256(productZip.FullName),
            AppBundleIncluded: false),
        Installer: new TimelineInstallerManifestItem(
            InstallerKind: "windows_csharp_installer",
            EntryPoint: "installer/Timeline.WindowsInstaller.exe",
            UsesBatchOrShellWrapper: false));

    File.WriteAllText(
        Path.Combine(installerRoot, "installer-manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);
}

static void WriteWindowsInstallerExternalManifest(
    string outputRoot,
    ReleaseOptions options,
    string version,
    string commit,
    string setupZipPath,
    string productZipPath)
{
    var setupZip = new FileInfo(setupZipPath);
    var productZip = new FileInfo(productZipPath);
    var artifactRuntimeName = ToArtifactRuntimeName(options.HostRuntime);
    var manifest = new TimelineWindowsInstallerExternalManifest(
        ManifestType: "timeline_windows_installer_artifact_manifest",
        ProductId: "timeline",
        ProductName: "Timeline",
        Channel: options.Channel,
        Version: version,
        Commit: commit,
        RuntimeIdentifier: options.HostRuntime,
        RuntimeName: artifactRuntimeName,
        CreatedAt: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        SetupArtifact: new TimelineArtifactManifestItem(
            ArtifactKind: "windows_installer_bundle",
            FileName: setupZip.Name,
            Path: setupZip.FullName,
            SizeBytes: setupZip.Length,
            Sha256: ComputeSha256(setupZip.FullName),
            AppBundleIncluded: false),
        ProductArtifact: new TimelineArtifactManifestItem(
            ArtifactKind: "built_product_artifact",
            FileName: productZip.Name,
            Path: productZip.FullName,
            SizeBytes: productZip.Length,
            Sha256: ComputeSha256(productZip.FullName),
            AppBundleIncluded: false),
        Installer: new TimelineInstallerManifestItem(
            InstallerKind: "windows_csharp_installer",
            EntryPoint: "installer/Timeline.WindowsInstaller.exe",
            UsesBatchOrShellWrapper: false));

    File.WriteAllText(
        Path.Combine(outputRoot, $"timeline-installer-{artifactRuntimeName}.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);
}

static TimelineWindowsInstallerBundleVerificationResult VerifyWindowsInstallerBundle(
    string setupZipPath,
    bool requireWindowsExecutionTrust = false)
{
    var result = new TimelineWindowsInstallerBundleVerificationResult
    {
        State = "verifying",
        SetupArtifactPath = setupZipPath,
        ExecutionTrustRequired = requireWindowsExecutionTrust
    };

    if (requireWindowsExecutionTrust && !OperatingSystem.IsWindows())
    {
        result.Blockers.Add("Windows execution trust verification requires a Windows host because Authenticode signature state must be checked against Windows binaries.");
    }

    if (!File.Exists(setupZipPath))
    {
        result.State = "failed";
        result.Blockers.Add($"Setup artifact was not found: {setupZipPath}");
        return result;
    }

    var setupFile = new FileInfo(setupZipPath);
    result.SetupArtifactSizeBytes = setupFile.Length;
    result.SetupArtifactSha256 = ComputeSha256(setupZipPath);

    string? productArtifactTempPath = null;
    try
    {
        using var setupArchive = ZipFile.OpenRead(setupZipPath);
        var setupEntries = setupArchive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var requiredEntry in new[]
        {
            "Timeline-Setup/installer/Timeline.WindowsInstaller.exe",
            "Timeline-Setup/installer-manifest.json",
            "Timeline-Setup/README.txt"
        })
        {
            if (!setupEntries.Contains(requiredEntry))
            {
                result.Blockers.Add($"Setup artifact is missing required entry: {requiredEntry}");
            }
        }

        AddWindowsBinaryExecutionTrustResult(
            setupArchive,
            "Timeline-Setup/installer/Timeline.WindowsInstaller.exe",
            result.Warnings,
            result.Blockers,
            requireWindowsExecutionTrust,
            "Windows installer");

        AddForbiddenContentBlockers(setupEntries, "Setup artifact", result.Blockers);

        var productEntries = setupArchive.Entries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Name) &&
                entry.FullName.Replace('\\', '/').StartsWith("Timeline-Setup/artifacts/", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (productEntries.Length != 1)
        {
            result.Blockers.Add($"Setup artifact must contain exactly one built product ZIP under Timeline-Setup/artifacts. Found: {productEntries.Length}");
        }

        var manifestEntry = setupArchive.GetEntry("Timeline-Setup/installer-manifest.json");
        JsonElement? manifestRoot = null;
        if (manifestEntry is null)
        {
            result.Blockers.Add("Setup artifact is missing installer manifest.");
        }
        else
        {
            using var manifestStream = manifestEntry.Open();
            using var document = JsonDocument.Parse(manifestStream);
            manifestRoot = document.RootElement.Clone();
            ValidateWindowsInstallerManifest(manifestRoot.Value, productEntries.FirstOrDefault(), result);
        }

        var productEntry = productEntries.FirstOrDefault();
        if (productEntry is not null)
        {
            result.ProductArtifactFileName = Path.GetFileName(productEntry.FullName);
            result.ProductArtifactSizeBytes = productEntry.Length;
            productArtifactTempPath = Path.Combine(Path.GetTempPath(), $"timeline-product-artifact-{Guid.NewGuid():N}.zip");
            using (var productInput = productEntry.Open())
            using (var productOutput = File.Create(productArtifactTempPath))
            {
                productInput.CopyTo(productOutput);
            }

            result.ProductArtifactSha256 = ComputeSha256(productArtifactTempPath);
            ValidateProductArtifact(productArtifactTempPath, result, requireWindowsExecutionTrust);

            if (manifestRoot is not null)
            {
                ValidateProductArtifactAgainstManifest(manifestRoot.Value, productEntry, result);
            }
        }
    }
    catch (InvalidDataException ex)
    {
        result.Blockers.Add($"Setup artifact is not a valid ZIP archive. {ex.Message}");
    }
    catch (JsonException ex)
    {
        result.Blockers.Add($"Setup artifact manifest could not be parsed. {ex.Message}");
    }
    finally
    {
        if (!string.IsNullOrWhiteSpace(productArtifactTempPath) && File.Exists(productArtifactTempPath))
        {
            File.Delete(productArtifactTempPath);
        }
    }

    result.State = result.Blockers.Count == 0 ? "verified" : "failed";
    return result;
}

static void ValidateWindowsInstallerManifest(
    JsonElement manifest,
    ZipArchiveEntry? productEntry,
    TimelineWindowsInstallerBundleVerificationResult result)
{
    if (GetString(manifest, "manifestType") != "timeline_windows_installer_bundle_manifest")
    {
        result.Blockers.Add("Installer manifest has an unexpected manifestType.");
    }

    result.RuntimeIdentifier = GetString(manifest, "runtimeIdentifier");
    if (string.IsNullOrWhiteSpace(result.RuntimeIdentifier) ||
        !result.RuntimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
    {
        result.Blockers.Add($"Installer manifest runtimeIdentifier is not Windows: {result.RuntimeIdentifier}");
    }

    var installer = manifest.TryGetProperty("installer", out var installerElement)
        ? installerElement
        : default;
    if (installer.ValueKind == JsonValueKind.Undefined)
    {
        result.Blockers.Add("Installer manifest is missing installer metadata.");
    }
    else
    {
        result.InstallerEntryPoint = GetString(installer, "entryPoint");
        if (!string.Equals(result.InstallerEntryPoint, "installer/Timeline.WindowsInstaller.exe", StringComparison.OrdinalIgnoreCase))
        {
            result.Blockers.Add($"Installer manifest entryPoint is unexpected: {result.InstallerEntryPoint}");
        }

        if (installer.TryGetProperty("usesBatchOrShellWrapper", out var wrapper) &&
            wrapper.ValueKind == JsonValueKind.True)
        {
            result.Blockers.Add("Installer manifest must not use a batch, shell, or command wrapper.");
        }
    }

    if (!manifest.TryGetProperty("productArtifact", out var productArtifact))
    {
        result.Blockers.Add("Installer manifest is missing productArtifact metadata.");
        return;
    }

    var productArtifactPath = GetString(productArtifact, "path");
    if (productEntry is not null)
    {
        var expectedPath = productEntry.FullName.Replace('\\', '/');
        var manifestPath = $"Timeline-Setup/{productArtifactPath}";
        if (!string.Equals(manifestPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            result.Blockers.Add($"Installer manifest productArtifact path does not match bundle entry. Manifest: {productArtifactPath}, entry: {expectedPath}");
        }
    }
}

static void ValidateProductArtifactAgainstManifest(
    JsonElement manifest,
    ZipArchiveEntry productEntry,
    TimelineWindowsInstallerBundleVerificationResult result)
{
    if (!manifest.TryGetProperty("productArtifact", out var productArtifact))
    {
        return;
    }

    var expectedFileName = GetString(productArtifact, "fileName");
    if (!string.Equals(expectedFileName, Path.GetFileName(productEntry.FullName), StringComparison.OrdinalIgnoreCase))
    {
        result.Blockers.Add($"Installer manifest productArtifact fileName does not match bundle entry: {expectedFileName}");
    }

    var expectedSize = GetInt64(productArtifact, "sizeBytes");
    if (expectedSize is not null && result.ProductArtifactSizeBytes is not null && expectedSize.Value != result.ProductArtifactSizeBytes.Value)
    {
        result.Blockers.Add($"Installer manifest productArtifact sizeBytes does not match bundle entry. Manifest: {expectedSize}, entry: {result.ProductArtifactSizeBytes}");
    }

    var expectedSha256 = GetString(productArtifact, "sha256");
    if (!string.IsNullOrWhiteSpace(expectedSha256) &&
        !string.Equals(expectedSha256, result.ProductArtifactSha256, StringComparison.OrdinalIgnoreCase))
    {
        result.Blockers.Add("Installer manifest productArtifact sha256 does not match the embedded product ZIP.");
    }
}

static void ValidateProductArtifact(
    string productZipPath,
    TimelineWindowsInstallerBundleVerificationResult result,
    bool requireWindowsExecutionTrust)
{
    using var productArchive = ZipFile.OpenRead(productZipPath);
    var entries = productArchive.Entries
        .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
        .Select(entry => entry.FullName.Replace('\\', '/'))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var requiredEntry in new[]
    {
        "Timeline/VERSION",
        "Timeline/launcher/Timeline.Launcher.exe",
        "Timeline/launcher-tray/Timeline.Launcher.Tray.exe",
        "Timeline/local-api/Timeline.LocalApi.exe",
        "Timeline/docker-compose.yml"
    })
    {
        if (!entries.Contains(requiredEntry))
        {
            result.Blockers.Add($"Product artifact is missing required entry: {requiredEntry}");
        }
    }

    foreach (var executableEntry in new[]
    {
        "Timeline/launcher/Timeline.Launcher.exe",
        "Timeline/launcher/Timeline.Launcher.dll",
        "Timeline/launcher-tray/Timeline.Launcher.Tray.exe",
        "Timeline/launcher-tray/Timeline.Launcher.Tray.dll",
        "Timeline/local-api/Timeline.LocalApi.exe",
        "Timeline/local-api/Timeline.LocalApi.dll"
    })
    {
        AddWindowsBinaryExecutionTrustResult(
            productArchive,
            executableEntry,
            result.Warnings,
            result.Blockers,
            requireWindowsExecutionTrust,
            executableEntry);
    }

    if (entries.Any(entry => entry.Contains("/settings.json", StringComparison.OrdinalIgnoreCase)))
    {
        result.Blockers.Add("Product artifact must not contain settings.json.");
    }

    if (entries.Any(entry => entry.StartsWith("Timeline/data/", StringComparison.OrdinalIgnoreCase)))
    {
        result.Blockers.Add("Product artifact must not contain Timeline user data.");
    }

    AddForbiddenContentBlockers(entries, "Product artifact", result.Blockers);

    var versionEntry = productArchive.GetEntry("Timeline/VERSION");
    if (versionEntry is null)
    {
        return;
    }

    using var versionStream = versionEntry.Open();
    using var versionDocument = JsonDocument.Parse(versionStream);
    result.Version = GetString(versionDocument.RootElement, "version");
    var runtimeIdentifier = GetString(versionDocument.RootElement, "runtimeIdentifier");
    if (!string.IsNullOrWhiteSpace(runtimeIdentifier))
    {
        result.RuntimeIdentifier = runtimeIdentifier;
    }

    if (string.IsNullOrWhiteSpace(runtimeIdentifier) ||
        !runtimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
    {
        result.Blockers.Add($"Product artifact runtime is not Windows: {runtimeIdentifier}");
    }
}

static void AddForbiddenContentBlockers(IEnumerable<string> entries, string label, ICollection<string> blockers)
{
    foreach (var entry in entries.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
    {
        if (IsForbiddenArtifactEntry(entry))
        {
            blockers.Add($"{label} contains development-only or script content: {entry}");
        }
    }
}

static void AddWindowsBinaryExecutionTrustResult(
    ZipArchive archive,
    string entryName,
    ICollection<string> warnings,
    ICollection<string> blockers,
    bool requireWindowsExecutionTrust,
    string label)
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var entry = archive.Entries.FirstOrDefault(value =>
        string.Equals(value.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase));
    if (entry is null || string.IsNullOrWhiteSpace(entry.Name))
    {
        return;
    }

    var tempPath = Path.Combine(
        Path.GetTempPath(),
        $"timeline-signature-check-{Guid.NewGuid():N}{Path.GetExtension(entry.Name)}");
    try
    {
        entry.ExtractToFile(tempPath, overwrite: true);
        if (!HasAuthenticodeSignature(tempPath))
        {
            var message = $"{label} is not Authenticode-signed. Smart App Control, WDAC, or Code Integrity can block this binary on constrained Windows environments.";
            if (requireWindowsExecutionTrust)
            {
                blockers.Add(message);
            }
            else
            {
                warnings.Add(message);
            }
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
    {
        var message = $"Execution trust could not be checked for {label}: {ex.Message}";
        if (requireWindowsExecutionTrust)
        {
            blockers.Add(message);
        }
        else
        {
            warnings.Add(message);
        }
    }
    finally
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
        }
    }
}

static bool HasAuthenticodeSignature(string path)
{
    try
    {
#pragma warning disable SYSLIB0057
        using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        return certificate is not null;
    }
    catch (CryptographicException)
    {
        return false;
    }
}

static bool IsForbiddenArtifactEntry(string relativePath)
{
    var normalized = relativePath.Replace('\\', '/');
    var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var forbiddenSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "bin",
        "obj",
        "docs-temp",
        "scripts-temp",
        "node_modules"
    };
    if (segments.Any(forbiddenSegments.Contains))
    {
        return true;
    }

    var extension = Path.GetExtension(normalized);
    var forbiddenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".bat",
        ".cmd",
        ".command",
        ".cs",
        ".csproj",
        ".fs",
        ".fsproj",
        ".ps1",
        ".sh",
        ".sln",
        ".vb",
        ".vbproj"
    };
    return forbiddenExtensions.Contains(extension);
}

static string? GetString(JsonElement element, string propertyName)
{
    return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
        ? property.GetString()
        : null;
}

static long? GetInt64(JsonElement element, string propertyName)
{
    return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
        ? value
        : null;
}

static void PrintWindowsInstallerVerification(TimelineWindowsInstallerBundleVerificationResult result)
{
    Console.WriteLine($"Timeline Windows installer bundle verification: {result.State}");
    Console.WriteLine($"  Setup: {result.SetupArtifactPath}");
    Console.WriteLine($"  Version: {result.Version ?? "-"}");
    Console.WriteLine($"  Runtime: {result.RuntimeIdentifier ?? "-"}");
    Console.WriteLine($"  Product artifact: {result.ProductArtifactFileName ?? "-"}");
    Console.WriteLine($"  Installer entry point: {result.InstallerEntryPoint ?? "-"}");
    Console.WriteLine($"  Windows execution trust required: {result.ExecutionTrustRequired}");
    foreach (var warning in result.Warnings)
    {
        Console.WriteLine($"  Warning: {warning}");
    }

    foreach (var blocker in result.Blockers)
    {
        Console.WriteLine($"  Blocker: {blocker}");
    }
}

static string ComputeSha256(string path)
{
    using var stream = File.OpenRead(path);
    var hash = SHA256.HashData(stream);
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static void CreateProductZip(string sourceDirectory, string zipPath, string hostRuntime)
{
    using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
    foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
    {
        var relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
        var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
        if (ShouldMarkExecutable(relativePath, hostRuntime))
        {
            entry.ExternalAttributes = Convert.ToInt32("100755", 8) << 16;
        }

        using var input = File.OpenRead(file);
        using var output = entry.Open();
        input.CopyTo(output);
    }
}

static bool ShouldMarkExecutable(string relativePath, string hostRuntime)
{
    if (!hostRuntime.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) &&
        !hostRuntime.StartsWith("linux-", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var fileName = Path.GetFileName(relativePath);
    return fileName is "Timeline.Launcher" or "Timeline.Launcher.Tray" or "Timeline.LocalApi";
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

static string ToArtifactRuntimeName(string runtimeIdentifier)
{
    return runtimeIdentifier switch
    {
        "osx-arm64" => "macos-arm64",
        "osx-x64" => "macos-x64",
        _ => runtimeIdentifier
    };
}

internal sealed record ReleaseOptions(
    string HostRuntime,
    string ContainerRuntime,
    string OutputDirectory,
    string Channel,
    string? Version,
    bool WindowsInstaller,
    string? VerifyWindowsInstallerPath,
    bool RequireWindowsExecutionTrust,
    bool Json)
{
    public static ReleaseOptions Parse(string[] args)
    {
        var hostRuntime = "win-x64";
        var containerRuntime = "linux-x64";
        var outputDirectory = "release";
        var channel = "dev";
        string? version = null;
        var windowsInstaller = false;
        string? verifyWindowsInstallerPath = null;
        var requireWindowsExecutionTrust = false;
        var json = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (TryReadOption(args, ref index, arg, "--runtime", ref hostRuntime) ||
                TryReadOption(args, ref index, arg, "--host-runtime", ref hostRuntime) ||
                TryReadOption(args, ref index, arg, "--container-runtime", ref containerRuntime) ||
                TryReadOption(args, ref index, arg, "--output", ref outputDirectory) ||
                TryReadOption(args, ref index, arg, "--channel", ref channel) ||
                TryReadNullableOption(args, ref index, arg, "--verify-windows-installer", ref verifyWindowsInstallerPath) ||
                TryReadNullableOption(args, ref index, arg, "--version", ref version))
            {
                continue;
            }

            if (arg.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                continue;
            }

            if (arg.Equals("--require-windows-execution-trust", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--require-execution-trust", StringComparison.OrdinalIgnoreCase))
            {
                requireWindowsExecutionTrust = true;
                continue;
            }

            if (arg.Equals("--windows-installer", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--include-windows-installer", StringComparison.OrdinalIgnoreCase))
            {
                windowsInstaller = true;
                continue;
            }

            throw new ArgumentException($"Unknown argument: {arg}");
        }

        return new ReleaseOptions(
            hostRuntime,
            containerRuntime,
            outputDirectory,
            channel,
            version,
            windowsInstaller,
            verifyWindowsInstallerPath,
            requireWindowsExecutionTrust,
            json);
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

internal sealed record TimelineArtifactManifest(
    string ManifestType,
    string ProductId,
    string ProductName,
    string Channel,
    string Version,
    string Commit,
    string RuntimeIdentifier,
    string RuntimeName,
    string ContainerRuntimeIdentifier,
    string CreatedAt,
    TimelineArtifactManifestItem Artifact);

internal sealed record TimelineArtifactManifestItem(
    string ArtifactKind,
    string FileName,
    string Path,
    long SizeBytes,
    string Sha256,
    bool AppBundleIncluded);

internal sealed record TimelineInstallerManifestItem(
    string InstallerKind,
    string EntryPoint,
    bool UsesBatchOrShellWrapper);

internal sealed record TimelineWindowsInstallerManifest(
    string ManifestType,
    string ProductId,
    string ProductName,
    string Channel,
    string Version,
    string Commit,
    string RuntimeIdentifier,
    string RuntimeName,
    string CreatedAt,
    TimelineArtifactManifestItem ProductArtifact,
    TimelineInstallerManifestItem Installer);

internal sealed record TimelineWindowsInstallerExternalManifest(
    string ManifestType,
    string ProductId,
    string ProductName,
    string Channel,
    string Version,
    string Commit,
    string RuntimeIdentifier,
    string RuntimeName,
    string CreatedAt,
    TimelineArtifactManifestItem SetupArtifact,
    TimelineArtifactManifestItem ProductArtifact,
    TimelineInstallerManifestItem Installer);

internal sealed record TimelineWindowsInstallerBundleVerificationResult
{
    public string State { get; set; } = "";
    public string SetupArtifactPath { get; init; } = "";
    public long? SetupArtifactSizeBytes { get; set; }
    public string? SetupArtifactSha256 { get; set; }
    public string? ProductArtifactFileName { get; set; }
    public long? ProductArtifactSizeBytes { get; set; }
    public string? ProductArtifactSha256 { get; set; }
    public string? Version { get; set; }
    public string? RuntimeIdentifier { get; set; }
    public string? InstallerEntryPoint { get; set; }
    public bool ExecutionTrustRequired { get; init; }
    public List<string> Warnings { get; } = [];
    public List<string> Blockers { get; } = [];
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public string CombinedText => string.Join(
        Environment.NewLine,
        new[] { Output, Error }.Where(text => !string.IsNullOrWhiteSpace(text)));
}
