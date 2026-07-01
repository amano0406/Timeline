using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var options = ReleaseOptions.Parse(args);

if (options.ValidateArtifacts)
{
    await ValidateArtifactsAsync(options);
    return;
}

if (options.WritePublishPreflight)
{
    await WritePublishPreflightAsync(options);
    return;
}

if (options.PublishReleaseArtifacts)
{
    await PublishReleaseArtifactsAsync(options);
    return;
}

if (options.WritePublishPlan)
{
    await WritePublishPlanAsync(options);
    return;
}

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

static async Task WritePublishPlanAsync(ReleaseOptions options)
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Timeline-SubProductReleaseBuilder");
    ApplyGitHubAuthenticationIfAvailable(client, options);
    var (plan, planPath) = await BuildPublishPlanAsync(options, client);
    await File.WriteAllTextAsync(
        planPath,
        JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);

    Console.WriteLine("Sub-product release publish plan created.");
    Console.WriteLine($"  Runtime: {plan.RuntimeIdentifier}");
    Console.WriteLine($"  Manifest: {plan.ManifestPath}");
    Console.WriteLine($"  Plan: {planPath}");
    Console.WriteLine($"  Ready: {plan.ReadyCount}");
    Console.WriteLine($"  Missing assets: {plan.AssetMissingCount}");
    Console.WriteLine($"  Missing releases: {plan.ReleaseMissingCount}");
    Console.WriteLine($"  Release check failed: {plan.ReleaseCheckFailedCount}");
    Console.WriteLine($"  Artifacts not created: {plan.ArtifactNotCreatedCount}");
}

static async Task ValidateArtifactsAsync(ReleaseOptions options)
{
    var outputRoot = Path.GetFullPath(options.OutputDirectory);
    var runtimeName = ToArtifactRuntimeName(options.RuntimeIdentifier);
    var (manifest, manifestPath) = await ReadArtifactManifestAsync(options, outputRoot, runtimeName);
    var items = manifest.Artifacts.Select(ValidateArtifact).ToList();
    var result = new SubProductArtifactValidationReport(
        "timeline_sub_product_artifact_validation",
        manifest.RuntimeIdentifier,
        manifest.RuntimeName,
        manifestPath,
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        items.Count,
        items.Count(item => item.Valid),
        items.Count(item => !item.Valid),
        items.Sum(item => item.Blockers.Count),
        items.Sum(item => item.Warnings.Count),
        items);

    Directory.CreateDirectory(outputRoot);
    var resultPath = Path.Combine(outputRoot, $"sub-product-artifacts-validation-{runtimeName}.json");
    await File.WriteAllTextAsync(
        resultPath,
        JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);

    Console.WriteLine("Sub-product artifacts validation finished.");
    Console.WriteLine($"  Runtime: {result.RuntimeIdentifier}");
    Console.WriteLine($"  Manifest: {result.ManifestPath}");
    Console.WriteLine($"  Result: {resultPath}");
    Console.WriteLine($"  Valid: {result.ValidCount}");
    Console.WriteLine($"  Invalid: {result.InvalidCount}");
    Console.WriteLine($"  Blockers: {result.BlockerCount}");
    Console.WriteLine($"  Warnings: {result.WarningCount}");

    if (result.InvalidCount > 0)
    {
        Environment.ExitCode = 2;
    }
}

static SubProductArtifactValidationItem ValidateArtifact(SubProductArtifactBuildResult artifact)
{
    var blockers = new List<string>();
    var warnings = new List<string>();
    var forbiddenEntries = new List<string>();
    var nestedArchiveEntries = new List<string>();
    var requiredEntries = new List<string>();
    var metadataWarnings = new List<string>();
    var metadataBlockers = new List<string>();
    var artifactPath = Path.GetFullPath(artifact.ArtifactPath ?? string.Empty);
    var artifactName = Path.GetFileName(artifactPath);
    var expectedRoot = $"{artifact.ProductName}/";
    var expectedVersionEntry = $"{expectedRoot}VERSION";
    var expectedProductEntry = $"{expectedRoot}timeline-product.json";
    long sizeBytes = 0;
    var entryCount = 0;

    if (!artifact.State.Equals("created", StringComparison.OrdinalIgnoreCase))
    {
        blockers.Add($"Artifact state is not created: {artifact.State}");
    }

    if (string.IsNullOrWhiteSpace(artifact.ArtifactPath) || !File.Exists(artifactPath))
    {
        blockers.Add($"Artifact file was not found: {artifact.ArtifactPath}");
        return NewArtifactValidationItem();
    }

    sizeBytes = new FileInfo(artifactPath).Length;
    if (sizeBytes <= 0)
    {
        blockers.Add("Artifact ZIP is empty.");
    }

    if (artifact.SizeBytes > 0 && artifact.SizeBytes != sizeBytes)
    {
        warnings.Add($"Manifest size differs from actual artifact size: manifest={artifact.SizeBytes}, actual={sizeBytes}.");
    }

    var expectedArtifactName = $"{artifact.ProductName}-{artifact.RuntimeName}-{artifact.Version}.zip";
    if (!artifactName.Equals(expectedArtifactName, StringComparison.OrdinalIgnoreCase))
    {
        warnings.Add($"Artifact name does not match the expected naming rule: expected={expectedArtifactName}, actual={artifactName}.");
    }

    try
    {
        using var archive = ZipFile.OpenRead(artifactPath);
        foreach (var entry in archive.Entries)
        {
            var entryName = NormalizeZipEntryName(entry.FullName);
            if (string.IsNullOrWhiteSpace(entryName) || entryName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            entryCount++;
            if (IsUnsafeZipEntryPath(entryName))
            {
                blockers.Add($"Unsafe ZIP entry path: {entryName}");
            }

            if (!entryName.StartsWith(expectedRoot, StringComparison.Ordinal))
            {
                blockers.Add($"ZIP entry is outside the product root directory: {entryName}");
            }

            if (entryName.Equals(expectedVersionEntry, StringComparison.Ordinal))
            {
                requiredEntries.Add(entryName);
                ValidateVersionEntry(entry, artifact, metadataBlockers, metadataWarnings);
            }

            if (entryName.Equals(expectedProductEntry, StringComparison.Ordinal))
            {
                requiredEntries.Add(entryName);
                ValidateTimelineProductEntry(entry, artifact, metadataBlockers);
            }

            if (IsForbiddenZipEntry(entryName))
            {
                forbiddenEntries.Add(entryName);
            }

            if (entryName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                nestedArchiveEntries.Add(entryName);
            }
        }
    }
    catch (Exception ex)
    {
        blockers.Add($"Artifact ZIP could not be opened: {ex.Message}");
    }

    if (!requiredEntries.Any(entry => entry.Equals(expectedVersionEntry, StringComparison.Ordinal)))
    {
        blockers.Add($"Required VERSION file was not found: {expectedVersionEntry}");
    }

    if (!requiredEntries.Any(entry => entry.Equals(expectedProductEntry, StringComparison.Ordinal)))
    {
        blockers.Add($"Required timeline-product.json file was not found: {expectedProductEntry}");
    }

    if (forbiddenEntries.Count > 0)
    {
        blockers.Add($"Forbidden entries were found: {forbiddenEntries.Count}");
    }

    if (nestedArchiveEntries.Count > 0)
    {
        blockers.Add($"Nested ZIP entries were found: {nestedArchiveEntries.Count}");
    }

    blockers.AddRange(metadataBlockers);
    warnings.AddRange(metadataWarnings);
    return NewArtifactValidationItem();

    SubProductArtifactValidationItem NewArtifactValidationItem()
    {
        var valid = blockers.Count == 0;
        return new SubProductArtifactValidationItem(
            artifact.ProductId,
            artifact.ProductName,
            artifact.Version,
            artifact.RuntimeIdentifier,
            artifact.RuntimeName,
            artifactPath,
            artifactName,
            sizeBytes,
            entryCount,
            requiredEntries.Count,
            2,
            valid,
            valid ? "ready" : "invalid",
            blockers,
            warnings,
            forbiddenEntries,
            nestedArchiveEntries);
    }
}

static void ValidateVersionEntry(
    ZipArchiveEntry entry,
    SubProductArtifactBuildResult artifact,
    List<string> blockers,
    List<string> warnings)
{
    try
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        CompareVersionProperty(root, "artifactType", "timeline_sub_product_artifact", blockers);
        CompareVersionProperty(root, "productId", artifact.ProductId, blockers);
        CompareVersionProperty(root, "productName", artifact.ProductName, blockers);
        CompareVersionProperty(root, "version", artifact.Version, blockers);
        CompareVersionProperty(root, "runtimeIdentifier", artifact.RuntimeIdentifier, blockers);
        if (!root.TryGetProperty("commit", out var commitElement) || string.IsNullOrWhiteSpace(commitElement.GetString()))
        {
            warnings.Add("VERSION does not contain commit.");
        }
    }
    catch (Exception ex)
    {
        blockers.Add($"VERSION file could not be parsed as JSON: {ex.Message}");
    }
}

static void ValidateTimelineProductEntry(
    ZipArchiveEntry entry,
    SubProductArtifactBuildResult artifact,
    List<string> blockers)
{
    try
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        CompareVersionProperty(root, "productId", artifact.ProductId, blockers);
        CompareVersionProperty(root, "displayName", artifact.ProductName, blockers);
    }
    catch (Exception ex)
    {
        blockers.Add($"timeline-product.json could not be parsed as JSON: {ex.Message}");
    }
}

static void CompareVersionProperty(JsonElement root, string propertyName, string expectedValue, List<string> blockers)
{
    if (!root.TryGetProperty(propertyName, out var element))
    {
        blockers.Add($"VERSION does not contain {propertyName}.");
        return;
    }

    var actualValue = element.GetString() ?? string.Empty;
    if (!actualValue.Equals(expectedValue, StringComparison.Ordinal))
    {
        blockers.Add($"VERSION {propertyName} mismatch: expected={expectedValue}, actual={actualValue}.");
    }
}

static string NormalizeZipEntryName(string entryName)
    => (entryName ?? string.Empty).Replace('\\', '/').TrimStart('/');

static bool IsUnsafeZipEntryPath(string entryName)
{
    if (string.IsNullOrWhiteSpace(entryName) ||
        entryName.StartsWith("/", StringComparison.Ordinal) ||
        entryName.Contains(':', StringComparison.Ordinal))
    {
        return true;
    }

    var segments = entryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
    return segments.Any(segment => segment.Equals("..", StringComparison.Ordinal));
}

static bool IsForbiddenZipEntry(string entryName)
{
    var segments = entryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Any(segment => ReleaseExclusionRules.ForbiddenDirectoryNames.Contains(segment)))
    {
        return true;
    }

    var fileName = segments.LastOrDefault() ?? string.Empty;
    if (ReleaseExclusionRules.ForbiddenFileNames.Contains(fileName))
    {
        return true;
    }

    var extension = Path.GetExtension(fileName);
    if (ReleaseExclusionRules.ForbiddenFileExtensions.Contains(extension))
    {
        return true;
    }

    return fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase);
}

static async Task WritePublishPreflightAsync(ReleaseOptions options)
{
    var outputRoot = Path.GetFullPath(options.OutputDirectory);
    var runtimeName = ToArtifactRuntimeName(options.RuntimeIdentifier);
    var (manifest, manifestPath) = await ReadArtifactManifestAsync(options, outputRoot, runtimeName);

    using var client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Timeline-SubProductReleaseBuilder");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    ApplyGitHubAuthenticationIfAvailable(client, options);

    var tokenSource = ResolveGitHubTokenSource(options);
    var tokenPresent = !string.IsNullOrWhiteSpace(tokenSource);
    var rateLimit = await ReadGitHubApiAsync(client, "https://api.github.com/rate_limit");
    var rateLimitState = rateLimit.Success ? "ok" : "failed";
    int? rateLimitLimit = null;
    int? rateLimitRemaining = null;
    string rateLimitResetAt = string.Empty;
    if (rateLimit.Success)
    {
        using var document = JsonDocument.Parse(rateLimit.Body);
        if (document.RootElement.TryGetProperty("resources", out var resources) &&
            resources.TryGetProperty("core", out var core))
        {
            rateLimitLimit = TryReadIntProperty(core, "limit");
            rateLimitRemaining = TryReadIntProperty(core, "remaining");
            var reset = TryReadIntProperty(core, "reset");
            if (reset is not null)
            {
                rateLimitResetAt = DateTimeOffset.FromUnixTimeSeconds(reset.Value).ToString("O", CultureInfo.InvariantCulture);
            }
        }
    }

    var userState = tokenPresent ? "unchecked" : "skipped_no_token";
    var userLogin = string.Empty;
    var userMessage = tokenPresent
        ? "GitHub token is present, but the authenticated user was not checked yet."
        : "GitHub token was not found. Publishing cannot run until a token is supplied.";
    if (tokenPresent)
    {
        var userResult = await ReadGitHubApiAsync(client, "https://api.github.com/user");
        if (userResult.Success)
        {
            using var document = JsonDocument.Parse(userResult.Body);
            userLogin = document.RootElement.TryGetProperty("login", out var loginElement)
                ? loginElement.GetString() ?? string.Empty
                : string.Empty;
            userState = "ok";
            userMessage = string.IsNullOrWhiteSpace(userLogin)
                ? "GitHub token is valid, but the login name was not returned."
                : $"GitHub token is valid for {userLogin}.";
        }
        else
        {
            userState = "failed";
            userMessage = userResult.Message;
        }
    }

    var items = new List<SubProductPublishPreflightItem>();
    var skipRepositoryChecksBecauseRateLimited = !tokenPresent && rateLimitRemaining is not null and <= 0;
    foreach (var artifact in manifest.Artifacts)
    {
        if (!artifact.State.Equals("created", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new SubProductPublishPreflightItem(
                artifact.ProductId,
                artifact.ProductName,
                artifact.Version,
                artifact.RuntimeName,
                Path.GetFileName(artifact.ArtifactPath),
                $"https://github.com/{options.GitHubOwner}/{artifact.ProductName}",
                "skipped_artifact_not_created",
                0,
                artifact.Message));
            continue;
        }

        if (skipRepositoryChecksBecauseRateLimited)
        {
            items.Add(new SubProductPublishPreflightItem(
                artifact.ProductId,
                artifact.ProductName,
                artifact.Version,
                artifact.RuntimeName,
                Path.GetFileName(artifact.ArtifactPath),
                $"https://github.com/{options.GitHubOwner}/{artifact.ProductName}",
                "skipped_rate_limited",
                rateLimit.StatusCode,
                "Repository checks were skipped because the unauthenticated GitHub API rate limit is exhausted. Set GITHUB_TOKEN or GH_TOKEN."));
            continue;
        }

        var repoUrl = $"https://api.github.com/repos/{options.GitHubOwner}/{artifact.ProductName}";
        var repoResult = await ReadGitHubApiAsync(client, repoUrl);
        var state = repoResult.Success
            ? "ok"
            : repoResult.StatusCode == (int)HttpStatusCode.NotFound
                ? "repo_not_found"
                : "failed";
        var message = state switch
        {
            "ok" => "Repository can be read.",
            "repo_not_found" => "Repository was not found or the token cannot access it.",
            _ => repoResult.Message,
        };

        items.Add(new SubProductPublishPreflightItem(
            artifact.ProductId,
            artifact.ProductName,
            artifact.Version,
            artifact.RuntimeName,
            Path.GetFileName(artifact.ArtifactPath),
            $"https://github.com/{options.GitHubOwner}/{artifact.ProductName}",
            state,
            repoResult.StatusCode,
            message));
    }

    var repoFailureCount = items.Count(item => item.State is not "ok" and not "skipped_artifact_not_created" and not "skipped_rate_limited");
    var artifactNotCreatedCount = items.Count(item => item.State.Equals("skipped_artifact_not_created", StringComparison.OrdinalIgnoreCase));
    var canPublish = tokenPresent &&
        rateLimitState.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
        userState.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
        repoFailureCount == 0 &&
        artifactNotCreatedCount == 0;
    var messageSummary = canPublish
        ? "Publish prerequisites look ready. External publishing still requires explicit approval and --confirm-publish."
        : "Publish prerequisites are not complete. Resolve token, API, repository, or artifact issues before publishing.";

    var result = new SubProductPublishPreflightResult(
        "timeline_sub_product_release_publish_preflight",
        options.GitHubOwner,
        manifest.RuntimeIdentifier,
        manifest.RuntimeName,
        manifestPath,
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        options.GitHubTokenEnvironmentVariable,
        tokenSource,
        tokenPresent,
        rateLimitState,
        rateLimit.StatusCode,
        rateLimit.Message,
        rateLimitLimit,
        rateLimitRemaining,
        rateLimitResetAt,
        userState,
        userLogin,
        userMessage,
        canPublish,
        messageSummary,
        items.Count,
        items.Count(item => item.State.Equals("ok", StringComparison.OrdinalIgnoreCase)),
        repoFailureCount,
        artifactNotCreatedCount,
        items);

    Directory.CreateDirectory(outputRoot);
    var resultPath = Path.Combine(outputRoot, $"sub-product-release-publish-preflight-{runtimeName}.json");
    await File.WriteAllTextAsync(
        resultPath,
        JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);

    Console.WriteLine("Sub-product release publish preflight created.");
    Console.WriteLine($"  Runtime: {result.RuntimeIdentifier}");
    Console.WriteLine($"  Manifest: {result.ManifestPath}");
    Console.WriteLine($"  Result: {resultPath}");
    Console.WriteLine($"  Token present: {result.TokenPresent}");
    Console.WriteLine($"  API: {result.RateLimitState}");
    Console.WriteLine($"  User: {result.AuthenticatedUserState}");
    Console.WriteLine($"  Repositories OK: {result.RepositoryOkCount}");
    Console.WriteLine($"  Repository failures: {result.RepositoryFailureCount}");
    Console.WriteLine($"  Can publish: {result.CanPublish}");
}

static async Task PublishReleaseArtifactsAsync(ReleaseOptions options)
{
    if (!options.ConfirmPublish)
    {
        throw new InvalidOperationException("Publishing changes GitHub Releases. Re-run with --publish --confirm-publish only after explicit release approval.");
    }

    var token = ResolveGitHubToken(options);
    if (string.IsNullOrWhiteSpace(token))
    {
        throw new InvalidOperationException($"GitHub token was not found. Set {options.GitHubTokenEnvironmentVariable} or GH_TOKEN before publishing.");
    }

    using var client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Timeline-SubProductReleaseBuilder");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    var (plan, _) = await BuildPublishPlanAsync(options, client);
    var results = new List<SubProductPublishRunItem>();
    foreach (var item in plan.Items)
    {
        if (item.State.Equals("ready", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(NewPublishRunItem(item, "skipped_ready", "Release and artifact already exist."));
            continue;
        }

        if (item.State.Equals("artifact_not_created", StringComparison.OrdinalIgnoreCase) ||
            item.State.Equals("release_check_failed", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(NewPublishRunItem(item, $"skipped_{item.State}", item.Message));
            continue;
        }

        if (!File.Exists(item.ArtifactPath))
        {
            results.Add(NewPublishRunItem(item, "failed", $"Artifact file was not found: {item.ArtifactPath}"));
            continue;
        }

        var releaseUrl = item.ReleaseUrl;
        var uploadUrl = item.ReleaseUploadUrl;
        if (item.State.Equals("release_missing", StringComparison.OrdinalIgnoreCase))
        {
            var createResult = await CreateGitHubReleaseAsync(client, options.GitHubOwner, item.ProductName, item.Version);
            if (!createResult.Success)
            {
                results.Add(NewPublishRunItem(item, "failed", createResult.Message));
                continue;
            }

            releaseUrl = string.IsNullOrWhiteSpace(createResult.ReleaseUrl) ? releaseUrl : createResult.ReleaseUrl;
            uploadUrl = createResult.UploadUrl;
        }

        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            results.Add(NewPublishRunItem(item, "failed", "GitHub Release upload URL was not available."));
            continue;
        }

        var uploadResult = await UploadGitHubReleaseAssetAsync(client, uploadUrl, item.ArtifactName, item.ArtifactPath);
        results.Add(new SubProductPublishRunItem(
            item.ProductId,
            item.ProductName,
            item.Version,
            item.ArtifactName,
            releaseUrl,
            uploadResult.Success ? "published" : "failed",
            uploadResult.Message));
    }

    var runtimeName = ToArtifactRuntimeName(options.RuntimeIdentifier);
    var result = new SubProductPublishRunResult(
        "timeline_sub_product_release_publish_result",
        options.GitHubOwner,
        plan.RuntimeIdentifier,
        plan.RuntimeName,
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        results.Count,
        results.Count(item => item.State.Equals("published", StringComparison.OrdinalIgnoreCase)),
        results.Count(item => item.State.StartsWith("skipped_", StringComparison.OrdinalIgnoreCase)),
        results.Count(item => item.State.Equals("failed", StringComparison.OrdinalIgnoreCase)),
        results);

    var resultPath = Path.Combine(Path.GetFullPath(options.OutputDirectory), $"sub-product-release-publish-result-{runtimeName}.json");
    await File.WriteAllTextAsync(
        resultPath,
        JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);

    Console.WriteLine("Sub-product release artifacts publish finished.");
    Console.WriteLine($"  Runtime: {result.RuntimeIdentifier}");
    Console.WriteLine($"  Result: {resultPath}");
    Console.WriteLine($"  Published: {result.PublishedCount}");
    Console.WriteLine($"  Skipped: {result.SkippedCount}");
    Console.WriteLine($"  Failed: {result.FailedCount}");

    if (result.FailedCount > 0)
    {
        Environment.ExitCode = 2;
    }
}

static async Task<(SubProductPublishPlan Plan, string PlanPath)> BuildPublishPlanAsync(ReleaseOptions options, HttpClient client)
{
    var outputRoot = Path.GetFullPath(options.OutputDirectory);
    var runtimeName = ToArtifactRuntimeName(options.RuntimeIdentifier);
    var (manifest, manifestPath) = await ReadArtifactManifestAsync(options, outputRoot, runtimeName);
    var items = new List<SubProductPublishPlanItem>();
    foreach (var artifact in manifest.Artifacts)
    {
        if (!artifact.State.Equals("created", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new SubProductPublishPlanItem(
                artifact.ProductId,
                artifact.ProductName,
                artifact.Version,
                artifact.RuntimeName,
                Path.GetFileName(artifact.ArtifactPath),
                artifact.ArtifactPath,
                artifact.SizeBytes,
                $"https://github.com/{options.GitHubOwner}/{artifact.ProductName}/releases/tag/{artifact.Version}",
                string.Empty,
                "artifact_not_created",
                string.Empty,
                false,
                [],
                [],
                "Artifact was not created, so it cannot be attached.",
                string.Empty,
                string.Empty));
            continue;
        }

        var artifactName = Path.GetFileName(artifact.ArtifactPath);
        var releaseState = await ReadReleaseStateAsync(client, options.GitHubOwner, artifact.ProductName, artifact.Version);
        var uploadCommand = releaseState.ReleaseExists
            ? $"gh release upload {artifact.Version} \"{artifact.ArtifactPath}\" --repo {options.GitHubOwner}/{artifact.ProductName} --clobber"
            : $"gh release create {artifact.Version} \"{artifact.ArtifactPath}\" --repo {options.GitHubOwner}/{artifact.ProductName} --title {artifact.Version} --notes \"Timeline sub-product runtime artifact.\"";
        var assetExists = releaseState.AssetNames.Any(name => name.Equals(artifactName, StringComparison.OrdinalIgnoreCase));
        var state = !string.IsNullOrWhiteSpace(releaseState.ReleaseCheckError)
            ? "release_check_failed"
            : releaseState.ReleaseExists
            ? assetExists ? "ready" : "asset_missing"
            : "release_missing";
        var message = state switch
        {
            "ready" => "Release and matching artifact asset already exist.",
            "asset_missing" => "Release exists, but the matching runtime artifact asset is missing.",
            "release_missing" => "Tag exists locally/remotely, but GitHub Release for the tag was not found.",
            "release_check_failed" => $"GitHub Release state could not be checked: {releaseState.ReleaseCheckError}",
            _ => "Release state could not be determined.",
        };

        items.Add(new SubProductPublishPlanItem(
            artifact.ProductId,
            artifact.ProductName,
            artifact.Version,
            artifact.RuntimeName,
            artifactName,
            artifact.ArtifactPath,
            artifact.SizeBytes,
            $"https://github.com/{options.GitHubOwner}/{artifact.ProductName}/releases/tag/{artifact.Version}",
            releaseState.ReleaseUploadUrl,
            state,
            releaseState.LatestReleaseTag,
            releaseState.ReleaseExists,
            releaseState.AssetNames,
            releaseState.LatestAssetNames,
            message,
            releaseState.ReleaseCheckError,
            uploadCommand));
    }

    var plan = new SubProductPublishPlan(
        "timeline_sub_product_release_publish_plan",
        options.GitHubOwner,
        manifest.RuntimeIdentifier,
        manifest.RuntimeName,
        manifestPath,
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        items.Count,
        items.Count(item => item.State.Equals("ready", StringComparison.OrdinalIgnoreCase)),
        items.Count(item => item.State.Equals("asset_missing", StringComparison.OrdinalIgnoreCase)),
        items.Count(item => item.State.Equals("release_missing", StringComparison.OrdinalIgnoreCase)),
        items.Count(item => item.State.Equals("release_check_failed", StringComparison.OrdinalIgnoreCase)),
        items.Count(item => item.State.Equals("artifact_not_created", StringComparison.OrdinalIgnoreCase)),
        items);

    return (plan, Path.Combine(outputRoot, $"sub-product-release-publish-plan-{runtimeName}.json"));
}

static async Task<(SubProductArtifactManifest Manifest, string ManifestPath)> ReadArtifactManifestAsync(
    ReleaseOptions options,
    string outputRoot,
    string runtimeName)
{
    var manifestName = string.IsNullOrWhiteSpace(options.ManifestName)
        ? $"sub-product-artifacts-{runtimeName}.json"
        : SanitizeSegment(options.ManifestName);
    if (!manifestName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
        manifestName += ".json";
    }

    var manifestPath = Path.Combine(outputRoot, manifestName);
    if (!File.Exists(manifestPath))
    {
        throw new FileNotFoundException($"Sub-product artifact manifest was not found: {manifestPath}");
    }

    var manifest = JsonSerializer.Deserialize<SubProductArtifactManifest>(
        await File.ReadAllTextAsync(manifestPath),
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException($"Sub-product artifact manifest could not be read: {manifestPath}");

    return (manifest, manifestPath);
}

static string ResolveGitHubToken(ReleaseOptions options)
{
    var token = Environment.GetEnvironmentVariable(options.GitHubTokenEnvironmentVariable);
    if (!string.IsNullOrWhiteSpace(token))
    {
        return token;
    }

    return Environment.GetEnvironmentVariable("GH_TOKEN") ?? string.Empty;
}

static string ResolveGitHubTokenSource(ReleaseOptions options)
{
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.GitHubTokenEnvironmentVariable)))
    {
        return options.GitHubTokenEnvironmentVariable;
    }

    return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GH_TOKEN")) ? string.Empty : "GH_TOKEN";
}

static void ApplyGitHubAuthenticationIfAvailable(HttpClient client, ReleaseOptions options)
{
    var token = ResolveGitHubToken(options);
    if (!string.IsNullOrWhiteSpace(token))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}

static async Task<GitHubReleaseMutationResult> CreateGitHubReleaseAsync(
    HttpClient client,
    string owner,
    string repo,
    string tag)
{
    var body = JsonSerializer.Serialize(new
    {
        tag_name = tag,
        name = tag,
        body = "Timeline sub-product runtime artifact.",
    });
    using var content = new StringContent(body, Encoding.UTF8, "application/json");
    using var response = await client.PostAsync($"https://api.github.com/repos/{owner}/{repo}/releases", content);
    var responseText = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        return new GitHubReleaseMutationResult(false, string.Empty, string.Empty, $"GitHub Release create failed: HTTP {(int)response.StatusCode} {responseText}");
    }

    using var document = JsonDocument.Parse(responseText);
    var root = document.RootElement;
    var uploadUrl = root.TryGetProperty("upload_url", out var uploadElement) ? uploadElement.GetString() ?? string.Empty : string.Empty;
    var htmlUrl = root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() ?? string.Empty : string.Empty;
    return new GitHubReleaseMutationResult(true, uploadUrl, htmlUrl, "Release created.");
}

static async Task<GitHubReleaseMutationResult> UploadGitHubReleaseAssetAsync(
    HttpClient client,
    string uploadUrl,
    string assetName,
    string artifactPath)
{
    var normalizedUploadUrl = NormalizeGitHubUploadUrl(uploadUrl);
    var url = $"{normalizedUploadUrl}?name={Uri.EscapeDataString(assetName)}";
    await using var file = File.OpenRead(artifactPath);
    using var content = new StreamContent(file);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
    using var response = await client.PostAsync(url, content);
    var responseText = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        return new GitHubReleaseMutationResult(false, string.Empty, string.Empty, $"GitHub asset upload failed: HTTP {(int)response.StatusCode} {responseText}");
    }

    return new GitHubReleaseMutationResult(true, string.Empty, string.Empty, "Artifact uploaded.");
}

static string NormalizeGitHubUploadUrl(string uploadUrl)
{
    var marker = uploadUrl.IndexOf('{', StringComparison.Ordinal);
    return marker >= 0 ? uploadUrl[..marker] : uploadUrl;
}

static SubProductPublishRunItem NewPublishRunItem(SubProductPublishPlanItem item, string state, string message)
    => new(
        item.ProductId,
        item.ProductName,
        item.Version,
        item.ArtifactName,
        item.ReleaseUrl,
        state,
        message);

static async Task<GitHubReleaseState> ReadReleaseStateAsync(
    HttpClient client,
    string owner,
    string repo,
    string tag)
{
    var tagRelease = await ReadReleaseAsync(client, $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}");
    var latestRelease = await ReadReleaseAsync(client, $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
    return new GitHubReleaseState(
        tagRelease.Exists,
        tagRelease.AssetNames,
        tagRelease.UploadUrl,
        latestRelease.Tag,
        latestRelease.AssetNames,
        tagRelease.Error,
        latestRelease.Error);
}

static async Task<GitHubReleaseReadResult> ReadReleaseAsync(HttpClient client, string url)
{
    try
    {
        using var response = await client.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new GitHubReleaseReadResult(false, string.Empty, [], string.Empty, string.Empty);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new GitHubReleaseReadResult(false, string.Empty, [], string.Empty, $"HTTP {(int)response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
        var uploadUrl = root.TryGetProperty("upload_url", out var uploadElement) ? uploadElement.GetString() ?? string.Empty : string.Empty;
        var assets = new List<string>();
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetElement in assetsElement.EnumerateArray())
            {
                if (assetElement.TryGetProperty("name", out var nameElement))
                {
                    var name = nameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        assets.Add(name);
                    }
                }
            }
        }

        return new GitHubReleaseReadResult(true, tag, assets, uploadUrl, string.Empty);
    }
    catch (Exception ex)
    {
        return new GitHubReleaseReadResult(false, string.Empty, [], string.Empty, ex.Message);
    }
}

static async Task<GitHubApiReadResult> ReadGitHubApiAsync(HttpClient client, string url)
{
    try
    {
        using var response = await client.GetAsync(url);
        var responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return new GitHubApiReadResult(
                false,
                (int)response.StatusCode,
                responseText,
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }

        return new GitHubApiReadResult(true, (int)response.StatusCode, responseText, "OK");
    }
    catch (Exception ex)
    {
        return new GitHubApiReadResult(false, 0, string.Empty, ex.Message);
    }
}

static int? TryReadIntProperty(JsonElement element, string name)
{
    if (!element.TryGetProperty(name, out var value))
    {
        return null;
    }

    return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
        ? number
        : null;
}

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
    string ManifestName,
    bool ValidateArtifacts,
    bool WritePublishPlan,
    bool WritePublishPreflight,
    bool PublishReleaseArtifacts,
    bool ConfirmPublish,
    string GitHubOwner,
    string GitHubTokenEnvironmentVariable)
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
        var validateArtifacts = false;
        var writePublishPlan = false;
        var writePublishPreflight = false;
        var publishReleaseArtifacts = false;
        var confirmPublish = false;
        var gitHubOwner = "amano0406";
        var gitHubTokenEnvironmentVariable = "GITHUB_TOKEN";

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.Equals("--all", StringComparison.OrdinalIgnoreCase))
            {
                buildAll = true;
                continue;
            }

            if (arg.Equals("--validate-artifacts", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--validate", StringComparison.OrdinalIgnoreCase))
            {
                validateArtifacts = true;
                continue;
            }

            if (arg.Equals("--publish-plan", StringComparison.OrdinalIgnoreCase))
            {
                writePublishPlan = true;
                continue;
            }

            if (arg.Equals("--publish-preflight", StringComparison.OrdinalIgnoreCase))
            {
                writePublishPreflight = true;
                continue;
            }

            if (arg.Equals("--publish", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--publish-release-artifacts", StringComparison.OrdinalIgnoreCase))
            {
                publishReleaseArtifacts = true;
                continue;
            }

            if (arg.Equals("--confirm-publish", StringComparison.OrdinalIgnoreCase))
            {
                confirmPublish = true;
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
                TryReadOption(args, ref index, arg, "--github-owner", ref gitHubOwner) ||
                TryReadOption(args, ref index, arg, "--github-token-env", ref gitHubTokenEnvironmentVariable) ||
                TryReadOption(args, ref index, arg, "--manifest", ref manifestName) ||
                TryReadNullableOption(args, ref index, arg, "--version", ref version))
            {
                continue;
            }

            throw new ArgumentException($"Unknown argument: {arg}");
        }

        if (!buildAll && !validateArtifacts && !writePublishPlan && !writePublishPreflight && !publishReleaseArtifacts && string.IsNullOrWhiteSpace(productRoot))
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
            manifestName,
            validateArtifacts,
            writePublishPlan,
            writePublishPreflight,
            publishReleaseArtifacts,
            confirmPublish,
            gitHubOwner,
            gitHubTokenEnvironmentVariable);
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

internal sealed record SubProductPublishPlan(
    string ArtifactType,
    string GitHubOwner,
    string RuntimeIdentifier,
    string RuntimeName,
    string ManifestPath,
    string CreatedAt,
    int TotalCount,
    int ReadyCount,
    int AssetMissingCount,
    int ReleaseMissingCount,
    int ReleaseCheckFailedCount,
    int ArtifactNotCreatedCount,
    List<SubProductPublishPlanItem> Items);

internal sealed record SubProductPublishPlanItem(
    string ProductId,
    string ProductName,
    string Version,
    string RuntimeName,
    string ArtifactName,
    string ArtifactPath,
    long SizeBytes,
    string ReleaseUrl,
    string ReleaseUploadUrl,
    string State,
    string LatestReleaseTag,
    bool ReleaseExists,
    List<string> ReleaseAssetNames,
    List<string> LatestReleaseAssetNames,
    string Message,
    string ReleaseCheckError,
    string SuggestedCommand);

internal sealed record GitHubReleaseState(
    bool ReleaseExists,
    List<string> AssetNames,
    string ReleaseUploadUrl,
    string LatestReleaseTag,
    List<string> LatestAssetNames,
    string ReleaseCheckError,
    string LatestReleaseCheckError);

internal sealed record GitHubReleaseReadResult(
    bool Exists,
    string Tag,
    List<string> AssetNames,
    string UploadUrl,
    string Error);

internal sealed record GitHubReleaseMutationResult(
    bool Success,
    string UploadUrl,
    string ReleaseUrl,
    string Message);

internal sealed record GitHubApiReadResult(
    bool Success,
    int StatusCode,
    string Body,
    string Message);

internal sealed record SubProductArtifactValidationReport(
    string ArtifactType,
    string RuntimeIdentifier,
    string RuntimeName,
    string ManifestPath,
    string CreatedAt,
    int TotalCount,
    int ValidCount,
    int InvalidCount,
    int BlockerCount,
    int WarningCount,
    List<SubProductArtifactValidationItem> Items);

internal sealed record SubProductArtifactValidationItem(
    string ProductId,
    string ProductName,
    string Version,
    string RuntimeIdentifier,
    string RuntimeName,
    string ArtifactPath,
    string ArtifactName,
    long SizeBytes,
    int EntryCount,
    int RequiredEntriesFoundCount,
    int RequiredEntriesExpectedCount,
    bool Valid,
    string State,
    List<string> Blockers,
    List<string> Warnings,
    List<string> ForbiddenEntries,
    List<string> NestedArchiveEntries);

internal sealed record SubProductPublishPreflightResult(
    string ArtifactType,
    string GitHubOwner,
    string RuntimeIdentifier,
    string RuntimeName,
    string ManifestPath,
    string CreatedAt,
    string PrimaryTokenEnvironmentVariable,
    string TokenSource,
    bool TokenPresent,
    string RateLimitState,
    int RateLimitStatusCode,
    string RateLimitMessage,
    int? RateLimitLimit,
    int? RateLimitRemaining,
    string RateLimitResetAt,
    string AuthenticatedUserState,
    string AuthenticatedUserLogin,
    string AuthenticatedUserMessage,
    bool CanPublish,
    string Message,
    int TotalCount,
    int RepositoryOkCount,
    int RepositoryFailureCount,
    int ArtifactNotCreatedCount,
    List<SubProductPublishPreflightItem> Items);

internal sealed record SubProductPublishPreflightItem(
    string ProductId,
    string ProductName,
    string Version,
    string RuntimeName,
    string ArtifactName,
    string RepositoryUrl,
    string State,
    int StatusCode,
    string Message);

internal sealed record SubProductPublishRunResult(
    string ArtifactType,
    string GitHubOwner,
    string RuntimeIdentifier,
    string RuntimeName,
    string CreatedAt,
    int TotalCount,
    int PublishedCount,
    int SkippedCount,
    int FailedCount,
    List<SubProductPublishRunItem> Items);

internal sealed record SubProductPublishRunItem(
    string ProductId,
    string ProductName,
    string Version,
    string ArtifactName,
    string ReleaseUrl,
    string State,
    string Message);

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
