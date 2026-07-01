using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class TimelineVersionService
{
    private const string ProductId = "timeline";
    private const string RepositoryOwner = "amano0406";
    private const string RepositoryName = "Timeline";
    private const string LatestReleaseUrl = "https://api.github.com/repos/amano0406/Timeline/releases/latest";

    public static async Task<TimelineVersionStatus> GetStatusAsync(string timelineRoot, CancellationToken cancellationToken)
    {
        var current = GetCurrentVersion(timelineRoot);
        var latest = await GetLatestReleaseAsync(current.RuntimeIdentifier, cancellationToken);

        var updateAvailable = false;
        if (current.CurrentVersionStatus == "ok" && latest.LatestVersionStatus == "ok")
        {
            updateAvailable = !NormalizeVersion(current.CurrentVersion)
                .Equals(NormalizeVersion(latest.LatestVersion), StringComparison.OrdinalIgnoreCase);
        }

        return new TimelineVersionStatus
        {
            ProductId = ProductId,
            ProductName = "Timeline",
            CurrentVersion = current.CurrentVersion,
            CurrentVersionStatus = current.CurrentVersionStatus,
            CurrentCommit = current.CurrentCommit,
            Channel = current.Channel,
            RuntimeIdentifier = current.RuntimeIdentifier,
            ContainerRuntimeIdentifier = current.ContainerRuntimeIdentifier,
            ArtifactKind = current.ArtifactKind,
            VersionSource = current.VersionSource,
            LatestVersion = latest.LatestVersion,
            LatestVersionStatus = latest.LatestVersionStatus,
            LatestVersionMessage = latest.LatestVersionMessage,
            LatestReleaseUrl = latest.LatestReleaseUrl,
            ReleaseArtifactName = latest.ReleaseArtifactName,
            ReleaseArtifactUrl = latest.ReleaseArtifactUrl,
            ReleaseSource = latest.ReleaseSource,
            UpdateAvailable = updateAvailable,
            CheckedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    public static TimelineCurrentVersion GetCurrentVersion(string timelineRoot)
    {
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        var versionPath = Path.Combine(timelineRoot, "VERSION");
        if (File.Exists(versionPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(versionPath))?.AsObject();
                if (root is not null)
                {
                    return new TimelineCurrentVersion
                    {
                        CurrentVersion = GetString(root, "version", "unknown"),
                        CurrentVersionStatus = "ok",
                        CurrentCommit = GetString(root, "commit", ""),
                        Channel = GetString(root, "channel", "stable"),
                        RuntimeIdentifier = GetString(root, "runtimeIdentifier", runtimeIdentifier),
                        ContainerRuntimeIdentifier = GetString(root, "containerRuntimeIdentifier", ""),
                        ArtifactKind = "built_product_artifact",
                        VersionSource = versionPath,
                    };
                }
            }
            catch
            {
                return UnknownCurrentVersion(runtimeIdentifier, "VERSION file could not be read.");
            }
        }

        var gitVersion = RunGit(timelineRoot, "describe --tags --dirty --always");
        var gitCommit = RunGit(timelineRoot, "rev-parse --short HEAD");
        if (gitVersion.ExitCode == 0 && !string.IsNullOrWhiteSpace(gitVersion.Output))
        {
            return new TimelineCurrentVersion
            {
                CurrentVersion = gitVersion.Output.Trim(),
                CurrentVersionStatus = "ok",
                CurrentCommit = gitCommit.ExitCode == 0 ? gitCommit.Output.Trim() : "",
                Channel = "dev",
                RuntimeIdentifier = runtimeIdentifier,
                ContainerRuntimeIdentifier = "",
                ArtifactKind = "developer_checkout",
                VersionSource = "git",
            };
        }

        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        return new TimelineCurrentVersion
        {
            CurrentVersion = assemblyVersion,
            CurrentVersionStatus = assemblyVersion == "unknown" ? "unknown" : "ok",
            CurrentCommit = "",
            Channel = "unknown",
            RuntimeIdentifier = runtimeIdentifier,
            ContainerRuntimeIdentifier = "",
            ArtifactKind = "unknown",
            VersionSource = "assembly",
        };
    }

    private static async Task<TimelineLatestVersion> GetLatestReleaseAsync(string runtimeIdentifier, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Timeline", "version-check"));
            using var response = await client.GetAsync(LatestReleaseUrl, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new TimelineLatestVersion
                {
                    LatestVersionStatus = "no_release",
                    LatestVersionMessage = "GitHub Release がまだ見つかりません。source archive ZIP は製品配布物として扱いません。",
                    ReleaseSource = "github_release_asset",
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new TimelineLatestVersion
                {
                    LatestVersionStatus = "request_failed",
                    LatestVersionMessage = $"GitHub Release の確認に失敗しました。HTTP {(int)response.StatusCode}",
                    ReleaseSource = "github_release_asset",
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject;
            if (root is null)
            {
                return new TimelineLatestVersion
                {
                    LatestVersionStatus = "request_failed",
                    LatestVersionMessage = "GitHub Release の応答を読み取れませんでした。",
                    ReleaseSource = "github_release_asset",
                };
            }

            var latestVersion = GetString(root, "tag_name", "");
            var releaseUrl = GetString(root, "html_url", "");
            var expectedRuntimeName = ToArtifactRuntimeName(runtimeIdentifier);
            var expectedPrefix = $"Timeline-{expectedRuntimeName}-";
            var asset = root["assets"]?.AsArray()
                .OfType<JsonObject>()
                .Select(node => new
                {
                    Name = GetString(node, "name", ""),
                    Url = GetString(node, "browser_download_url", ""),
                })
                .FirstOrDefault(item =>
                    item.Name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) &&
                    item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            if (asset is null)
            {
                return new TimelineLatestVersion
                {
                    LatestVersion = latestVersion,
                    LatestVersionStatus = "asset_missing",
                    LatestVersionMessage = $"最新 Release は見つかりましたが、この環境向けのビルド済み成果物 {expectedPrefix}*.zip が見つかりません。",
                    LatestReleaseUrl = releaseUrl,
                    ReleaseSource = "github_release_asset",
                };
            }

            return new TimelineLatestVersion
            {
                LatestVersion = latestVersion,
                LatestVersionStatus = "ok",
                LatestVersionMessage = "GitHub Release のビルド済み成果物を確認できました。",
                LatestReleaseUrl = releaseUrl,
                ReleaseArtifactName = asset.Name,
                ReleaseArtifactUrl = asset.Url,
                ReleaseSource = "github_release_asset",
            };
        }
        catch (Exception ex)
        {
            return new TimelineLatestVersion
            {
                LatestVersionStatus = "request_failed",
                LatestVersionMessage = $"GitHub Release の確認に失敗しました。{ex.Message}",
                ReleaseSource = "github_release_asset",
            };
        }
    }

    private static string ToArtifactRuntimeName(string runtimeIdentifier)
    {
        if (runtimeIdentifier.Equals("osx-arm64", StringComparison.OrdinalIgnoreCase))
        {
            return "macos-arm64";
        }

        if (runtimeIdentifier.Equals("osx-x64", StringComparison.OrdinalIgnoreCase))
        {
            return "macos-x64";
        }

        return string.IsNullOrWhiteSpace(runtimeIdentifier) ? "unknown" : runtimeIdentifier;
    }

    private static string NormalizeVersion(string value)
        => value.Trim().TrimStart('v', 'V');

    private static TimelineCurrentVersion UnknownCurrentVersion(string runtimeIdentifier, string reason)
        => new()
        {
            CurrentVersion = "unknown",
            CurrentVersionStatus = "unknown",
            CurrentCommit = "",
            Channel = "unknown",
            RuntimeIdentifier = runtimeIdentifier,
            ContainerRuntimeIdentifier = "",
            ArtifactKind = "unknown",
            VersionSource = reason,
        };

    private static string GetString(JsonObject root, string propertyName, string fallback)
    {
        var value = root[propertyName];
        return value is null ? fallback : value.ToString();
    }

    private static GitProcessResult RunGit(string workingDirectory, string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            process.Start();
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                process.Kill(entireProcessTree: true);
                return new GitProcessResult(124, "", "git timed out.");
            }

            return new GitProcessResult(
                process.ExitCode,
                process.StandardOutput.ReadToEnd(),
                process.StandardError.ReadToEnd());
        }
        catch (Exception ex)
        {
            return new GitProcessResult(127, "", ex.Message);
        }
    }

    private sealed record GitProcessResult(int ExitCode, string Output, string Error);
}

public sealed class TimelineVersionStatus
{
    public string ProductId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string CurrentVersionStatus { get; set; } = "";
    public string CurrentCommit { get; set; } = "";
    public string Channel { get; set; } = "";
    public string RuntimeIdentifier { get; set; } = "";
    public string ContainerRuntimeIdentifier { get; set; } = "";
    public string ArtifactKind { get; set; } = "";
    public string VersionSource { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string LatestVersionStatus { get; set; } = "";
    public string LatestVersionMessage { get; set; } = "";
    public string LatestReleaseUrl { get; set; } = "";
    public string ReleaseArtifactName { get; set; } = "";
    public string ReleaseArtifactUrl { get; set; } = "";
    public string ReleaseSource { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string CheckedAt { get; set; } = "";
}

public sealed class TimelineCurrentVersion
{
    public string CurrentVersion { get; set; } = "";
    public string CurrentVersionStatus { get; set; } = "";
    public string CurrentCommit { get; set; } = "";
    public string Channel { get; set; } = "";
    public string RuntimeIdentifier { get; set; } = "";
    public string ContainerRuntimeIdentifier { get; set; } = "";
    public string ArtifactKind { get; set; } = "";
    public string VersionSource { get; set; } = "";
}

public sealed class TimelineLatestVersion
{
    public string LatestVersion { get; set; } = "";
    public string LatestVersionStatus { get; set; } = "";
    public string LatestVersionMessage { get; set; } = "";
    public string LatestReleaseUrl { get; set; } = "";
    public string ReleaseArtifactName { get; set; } = "";
    public string ReleaseArtifactUrl { get; set; } = "";
    public string ReleaseSource { get; set; } = "";
}
