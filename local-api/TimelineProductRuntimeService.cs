using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
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

    private static readonly Dictionary<string, ProductRuntimeMetadata> ProductMetadata =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["audio"] = new("TimelineForAudio", "audio", "audio/files", ["windows", "macos", "linux"]),
            ["windows-codex"] = new(
                "TimelineForWindowsCodex",
                "codex",
                "windows-codex",
                ["windows"],
                "この製品は Windows Codex のローカル履歴を扱うため、このOSでは未対応です。Windows環境で利用してください。"),
            ["chatgpt"] = new("TimelineForChatGPT", "chatgpt", "chatgpt", ["windows", "macos", "linux"]),
            ["image"] = new("TimelineForImage", "image", "image", ["windows", "macos", "linux"]),
            ["video"] = new("TimelineForVideo", "video", "video", ["windows", "macos", "linux"]),
            ["pc"] = new(
                "TimelineForPcInfo",
                "pc",
                "pc",
                ["windows"],
                "この製品は Windows のPC状態を扱うため、このOSでは未対応です。Windows環境で利用してください。"),
        };

    private readonly TimelineSettingsService _settings;
    private readonly TimelineProductSettingsService _productSettings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineLocalApiOptions _options;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, CachedLatestVersion> _latestVersionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedProductReleaseArtifactInfo> _latestReleaseArtifactCache = new(StringComparer.OrdinalIgnoreCase);

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

        throw new InvalidOperationException(
            "Source archive product updates are disabled. Use a built product artifact update flow.");
    }

    public async Task<ProductUpdatePlanResponse> GetProductUpdatePlanAsync(
        string productId,
        CancellationToken cancellationToken)
    {
        var definition = GetProductDefinition(productId);
        var status = await ConvertRuntimeStatusAsync(definition, cancellationToken);
        var productPath = Path.GetFullPath(definition.ProductPath);
        var blockers = new List<ProductUpdatePlanMessageResponse>();
        var warnings = new List<ProductUpdatePlanMessageResponse>();
        var installedVersion = string.Empty;
        ProductSourceInfo? source = null;
        ProductReleaseArtifactInfo? builtArtifact = null;

        if (!status.ProductFound)
        {
            blockers.Add(NewProductUpdateMessage(
                "product_not_installed",
                "Product is not installed. Install must run before update."));
        }

        if (!status.ComposeFound)
        {
            blockers.Add(NewProductUpdateMessage(
                "product_incomplete",
                "Product compose/runtime files were not found, so update cannot be planned safely."));
        }

        var appManagedByTimeline = IsProductAppManagedByTimeline(productPath);
        if (!appManagedByTimeline)
        {
            blockers.Add(NewProductUpdateMessage(
                "app_not_managed_by_timeline",
                "Product app path is outside Timeline-managed product locations."));
        }

        if (!string.IsNullOrWhiteSpace(definition.SourceType)
            && IsGitHubSourceArchive(definition.SourceType))
        {
            warnings.Add(NewProductUpdateMessage(
                "legacy_source_archive_mode",
                "GitHub source archive metadata is transitional. Normal user-facing updates require a built product artifact."));
        }
        else if (string.IsNullOrWhiteSpace(definition.SourceType))
        {
            warnings.Add(NewProductUpdateMessage(
                "source_type_missing",
                "Product source type is not configured."));
        }

        if (!string.IsNullOrWhiteSpace(definition.ProductPath) && Directory.Exists(productPath))
        {
            try
            {
                installedVersion = await GetProductInstalledVersionAsync(definition, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                warnings.Add(NewProductUpdateMessage(
                    "installed_version_unavailable",
                    $"Installed version could not be read. {ex.Message}"));
            }
        }

        if (!string.IsNullOrWhiteSpace(definition.SourceUrl))
        {
            try
            {
                source = await GetProductSourceInfoAsync(definition, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                warnings.Add(NewProductUpdateMessage(
                    "latest_source_unavailable",
                    $"Latest source version could not be resolved. {ex.Message}"));
            }

            try
            {
                builtArtifact = await GetLatestProductReleaseArtifactAsync(definition, cancellationToken);
                if (!builtArtifact.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(NewProductUpdateMessage(
                        "built_artifact_" + NormalizeProductUpdateId(builtArtifact.Status),
                        builtArtifact.Message));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                warnings.Add(NewProductUpdateMessage(
                    "built_artifact_unavailable",
                    $"Latest built artifact could not be resolved. {ex.Message}"));
            }
        }
        else
        {
            warnings.Add(NewProductUpdateMessage(
                "source_url_missing",
                "Product source URL is not configured."));
        }

        var sourceVersionUpdateAvailable = source is not null
            && (string.IsNullOrEmpty(installedVersion) || CompareVersionText(installedVersion, source.LatestVersion) < 0);
        var builtArtifactUpdateAvailable = builtArtifact is not null
            && builtArtifact.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(installedVersion) || CompareVersionText(installedVersion, builtArtifact.LatestVersion) < 0);
        var sourceArchiveUpdateAvailable = sourceVersionUpdateAvailable && IsGitHubSourceArchive(definition.SourceType);
        if (sourceArchiveUpdateAvailable && !builtArtifactUpdateAvailable)
        {
            warnings.Add(NewProductUpdateMessage(
                "source_archive_update_demoted",
                "A newer GitHub source archive exists, but it is not the normal user-facing update path. Attach a matching built product artifact to the GitHub Release first."));
        }

        var canUseCurrentUpdater = false;
        var canUseBuiltArtifactUpdater = blockers.Count == 0 && builtArtifactUpdateAvailable;
        var state = blockers.Count > 0
            ? "blocked"
            : canUseBuiltArtifactUpdater
                ? "built_artifact_ready"
                : sourceArchiveUpdateAvailable
                ? "built_artifact_required"
                : "up_to_date";

        return new ProductUpdatePlanResponse
        {
            ProductId = definition.Id,
            DisplayName = definition.DisplayName,
            State = state,
            ProductPath = productPath,
            SourceType = definition.SourceType,
            SourceUrl = definition.SourceUrl,
            DistributionMode = ResolveProductDistributionMode(definition, builtArtifact),
            InstalledVersion = installedVersion,
            LatestVersion = source?.LatestVersion ?? string.Empty,
            ArchiveUrl = source?.ArchiveUrl ?? string.Empty,
            UpdateAvailable = builtArtifactUpdateAvailable,
            SourceArchiveUpdateAvailable = sourceArchiveUpdateAvailable,
            BuiltArtifactStatus = builtArtifact?.Status ?? string.Empty,
            BuiltArtifactMessage = builtArtifact?.Message ?? string.Empty,
            BuiltArtifactVersion = builtArtifact?.LatestVersion ?? string.Empty,
            BuiltArtifactName = builtArtifact?.ArtifactName ?? string.Empty,
            BuiltArtifactUrl = builtArtifact?.ArtifactUrl ?? string.Empty,
            BuiltArtifactRuntime = builtArtifact?.RuntimeName ?? string.Empty,
            BuiltArtifactUpdateAvailable = builtArtifactUpdateAvailable,
            CanUseCurrentUpdater = canUseCurrentUpdater,
            CanUseBuiltArtifactUpdater = canUseBuiltArtifactUpdater,
            Running = status.Running,
            ProductFound = status.ProductFound,
            ComposeFound = status.ComposeFound,
            AppManagedByTimeline = appManagedByTimeline,
            Preserve = BuildProductUpdatePreservePlan(definition),
            Replace = BuildProductUpdateReplacePlan(productPath),
            Steps = BuildProductUpdateSteps(),
            Blockers = blockers,
            Warnings = warnings,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    public ProductUpdateArtifactValidationResponse ValidateProductUpdateArtifact(
        string productId,
        string artifactPath)
    {
        var definition = GetProductDefinition(productId);
        var fullPath = Path.GetFullPath(artifactPath);
        var blockers = new List<ProductUpdatePlanMessageResponse>();
        var warnings = new List<ProductUpdatePlanMessageResponse>();
        var requiredEntries = BuildProductUpdateArtifactEntryChecks();
        var artifactRootPrefix = string.Empty;
        var version = string.Empty;
        var artifactType = string.Empty;
        var artifactProductId = string.Empty;
        var artifactProductName = string.Empty;
        var artifactRuntime = string.Empty;
        var commit = string.Empty;
        var channel = string.Empty;

        if (!File.Exists(fullPath))
        {
            blockers.Add(NewProductUpdateMessage(
                "artifact_missing",
                $"Artifact file was not found: {fullPath}"));
            return NewProductUpdateArtifactValidationResponse(
                definition,
                fullPath,
                artifactRootPrefix,
                requiredEntries,
                blockers,
                warnings,
                artifactType,
                artifactProductId,
                artifactProductName,
                version,
                commit,
                channel,
                artifactRuntime);
        }

        var artifactRuntimeName = ToProductArtifactRuntimeName(RuntimeInformation.RuntimeIdentifier);
        var expectedPrefix = $"{definition.DisplayName}-{artifactRuntimeName}-";
        var fileName = Path.GetFileName(fullPath);
        if (!fileName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(NewProductUpdateMessage(
                "artifact_name_mismatch",
                $"Artifact name must match {expectedPrefix}*.zip."));
        }

        try
        {
            using var archive = ZipFile.OpenRead(fullPath);
            artifactRootPrefix = ResolveProductArtifactRootPrefix(archive);
            if (string.IsNullOrWhiteSpace(artifactRootPrefix))
            {
                blockers.Add(NewProductUpdateMessage(
                    "artifact_root_missing",
                    "Artifact must contain exactly one product root directory."));
            }
            else if (!artifactRootPrefix.TrimEnd('/').Equals(definition.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(NewProductUpdateMessage(
                    "artifact_root_mismatch",
                    $"Artifact root must be {definition.DisplayName}/."));
            }

            foreach (var check in requiredEntries)
            {
                var entryPath = artifactRootPrefix + check.Path;
                var exists = archive.GetEntry(entryPath) is not null;
                check.Exists = exists;
                if (!exists)
                {
                    blockers.Add(NewProductUpdateMessage(
                        "required_entry_missing",
                        $"Required artifact entry is missing: {entryPath}"));
                }
            }

            var versionEntry = string.IsNullOrWhiteSpace(artifactRootPrefix)
                ? null
                : archive.GetEntry(artifactRootPrefix + "VERSION");
            if (versionEntry is null)
            {
                blockers.Add(NewProductUpdateMessage(
                    "version_missing",
                    "Artifact VERSION metadata is missing."));
            }
            else
            {
                try
                {
                    var versionText = ReadZipEntryText(versionEntry);
                    var root = JsonNode.Parse(versionText) as JsonObject;
                    if (root is null)
                    {
                        blockers.Add(NewProductUpdateMessage(
                            "version_invalid",
                            "Artifact VERSION metadata is not a JSON object."));
                    }
                    else
                    {
                        artifactType = GetString(root, "artifactType", string.Empty);
                        artifactProductId = GetString(root, "productId", string.Empty);
                        artifactProductName = GetString(root, "productName", string.Empty);
                        version = GetString(root, "version", string.Empty);
                        commit = GetString(root, "commit", string.Empty);
                        channel = GetString(root, "channel", string.Empty);
                        artifactRuntime = GetString(root, "runtimeIdentifier", string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    blockers.Add(NewProductUpdateMessage(
                        "version_unreadable",
                        $"Artifact VERSION metadata could not be read. {ex.Message}"));
                }
            }
        }
        catch (InvalidDataException ex)
        {
            blockers.Add(NewProductUpdateMessage(
                "artifact_invalid_zip",
                $"Artifact ZIP could not be read. {ex.Message}"));
        }
        catch (Exception ex)
        {
            blockers.Add(NewProductUpdateMessage(
                "artifact_unreadable",
                $"Artifact could not be read. {ex.Message}"));
        }

        if (!string.IsNullOrWhiteSpace(artifactType) &&
            !artifactType.Equals("timeline_sub_product_artifact", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(NewProductUpdateMessage(
                "artifact_type_mismatch",
                "Artifact VERSION metadata is not a Timeline sub-product artifact."));
        }

        if (!string.IsNullOrWhiteSpace(artifactProductId) &&
            !artifactProductId.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(NewProductUpdateMessage(
                "product_id_mismatch",
                $"Artifact productId must be {definition.Id}."));
        }

        if (!string.IsNullOrWhiteSpace(artifactProductName) &&
            !artifactProductName.Equals(definition.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(NewProductUpdateMessage(
                "product_name_mismatch",
                $"Artifact productName must be {definition.DisplayName}."));
        }

        if (!string.IsNullOrWhiteSpace(artifactRuntime))
        {
            var normalizedArtifactRuntime = ToProductArtifactRuntimeName(artifactRuntime);
            if (!normalizedArtifactRuntime.Equals(artifactRuntimeName, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(NewProductUpdateMessage(
                    "runtime_mismatch",
                    $"Artifact runtime {artifactRuntime} does not match current runtime {RuntimeInformation.RuntimeIdentifier}."));
            }
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            warnings.Add(NewProductUpdateMessage(
                "version_empty",
                "Artifact VERSION metadata does not include a version."));
        }

        return NewProductUpdateArtifactValidationResponse(
            definition,
            fullPath,
            artifactRootPrefix,
            requiredEntries,
            blockers,
            warnings,
            artifactType,
            artifactProductId,
            artifactProductName,
            version,
            commit,
            channel,
            artifactRuntime);
    }

    public ProductUpdateArtifactStageResponse StageProductUpdateArtifact(
        string productId,
        string artifactPath,
        string? operationId)
    {
        var definition = GetProductDefinition(productId);
        var validation = ValidateProductUpdateArtifact(productId, artifactPath);
        var blockers = validation.Blockers
            .Select(message => NewProductUpdateMessage(message.Code, message.Message))
            .ToList();
        var warnings = validation.Warnings
            .Select(message => NewProductUpdateMessage(message.Code, message.Message))
            .ToList();
        var normalizedOperationId = GetProductUpdateOperationId(definition.Id, operationId);
        var stagingRoot = Path.GetFullPath(Path.Combine(
            _settings.GetWorkDirectory(),
            "product-updates",
            normalizedOperationId,
            "artifact"));
        var extractedRootPath = string.IsNullOrWhiteSpace(validation.ArtifactRootPrefix)
            ? string.Empty
            : Path.Combine(stagingRoot, validation.ArtifactRootPrefix.TrimEnd('/', '\\'));

        if (!validation.Valid)
        {
            return NewProductUpdateArtifactStageResponse(
                definition,
                validation,
                normalizedOperationId,
                stagingRoot,
                extractedRootPath,
                blockers,
                warnings,
                staged: false);
        }

        if (Directory.Exists(stagingRoot) || File.Exists(stagingRoot))
        {
            blockers.Add(NewProductUpdateMessage(
                "staging_path_exists",
                $"Staging path already exists: {stagingRoot}"));
            return NewProductUpdateArtifactStageResponse(
                definition,
                validation,
                normalizedOperationId,
                stagingRoot,
                extractedRootPath,
                blockers,
                warnings,
                staged: false);
        }

        try
        {
            Directory.CreateDirectory(stagingRoot);
            ExtractProductUpdateArtifact(validation.ArtifactPath, validation.ArtifactRootPrefix, stagingRoot);
        }
        catch (Exception ex)
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                warnings.Add(NewProductUpdateMessage(
                    "staging_cleanup_failed",
                    $"Failed staging directory could not be removed. {cleanupEx.Message}"));
            }

            blockers.Add(NewProductUpdateMessage(
                "staging_failed",
                $"Artifact could not be staged. {ex.Message}"));
        }

        var staged = blockers.Count == 0
            && Directory.Exists(extractedRootPath)
            && File.Exists(Path.Combine(extractedRootPath, "VERSION"));
        if (!staged && blockers.Count == 0)
        {
            blockers.Add(NewProductUpdateMessage(
                "staging_incomplete",
                "Artifact was extracted, but the staged product root or VERSION file was not found."));
        }

        return NewProductUpdateArtifactStageResponse(
            definition,
            validation,
            normalizedOperationId,
            stagingRoot,
            extractedRootPath,
            blockers,
            warnings,
            staged);
    }

    public async Task<ProductRuntimeRowResponse> ApplyProductUpdateArtifactAsync(
        string productId,
        string artifactPath,
        string? operationId,
        bool confirm,
        CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            throw new InvalidOperationException("Built artifact update requires confirm=true.");
        }

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
        AssertProductAppManagedByTimeline(productPath, "Built artifact update");
        AssertProductPathDeleteSafe(definition.Id, productPath);
        if (!await TestProductGitWorktreeCleanAsync(productPath, cancellationToken))
        {
            throw new InvalidOperationException("Product has local Git changes. Commit or discard them before updating.");
        }

        var validation = ValidateProductUpdateArtifact(productId, artifactPath);
        if (!validation.Valid)
        {
            var messages = validation.Blockers.Count == 0
                ? "Artifact validation failed."
                : string.Join(" ", validation.Blockers.Select(message => message.Message));
            throw new InvalidOperationException(messages);
        }

        var stage = StageProductUpdateArtifact(productId, validation.ArtifactPath, operationId);
        if (!stage.Staged)
        {
            var messages = stage.Blockers.Count == 0
                ? "Artifact staging failed."
                : string.Join(" ", stage.Blockers.Select(message => message.Message));
            throw new InvalidOperationException(messages);
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

        WriteRuntimeState(definition.Id, "updating", message: "Updating product from built artifact.");
        var oldPath = string.Empty;
        var newInstalled = false;
        try
        {
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
            Directory.Move(stage.ExtractedRootPath, productPath);
            newInstalled = true;
            AssertProductPathDeleteSafe(definition.Id, productPath);
            definition = GetProductDefinition(productId);
            _ = RestoreProductSettingsBackup(definition);
            WriteProductInstallState(definition.Id, validation.Version, definition.SourceUrl, validation.ArtifactPath);
            WriteRuntimeState(definition.Id, "stopped", message: "Product updated from built artifact.");

            if (wasRunning)
            {
                _ = await StartProductCoreAsync(
                    definition,
                    restart: false,
                    _operations.NewOperationId("product-update-start"),
                    cancellationToken);
            }

            TryDeleteDirectory(oldPath);
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
            throw new InvalidOperationException($"{definition.DisplayName} built artifact update failed: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stage.StagingRoot))
                {
                    Directory.Delete(stage.StagingRoot, recursive: true);
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
            AssertProductManagedDeletePathSafe(
                row.Path,
                productPath,
                [GetManagedProductDataDirectory(definition.Id)]);
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
        AssertProductSupportedOnCurrentOperatingSystem(definition);

        var productPath = definition.ProductPath;
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            throw new InvalidOperationException($"Product directory was not found: {productPath}");
        }

        var stopLauncher = ResolveProductLauncher(definition, "stop", required: false);
        if (restart && stopLauncher is not null)
        {
            WriteRuntimeState(definition.Id, "restarting", message: "Restarting product.");
            await RunLoggedProcessAsync(
                definition,
                stopLauncher,
                timeoutSeconds: 180,
                parentOperationId,
                cancellationToken);
        }

        var startLauncher = ResolveProductLauncher(definition, "start", required: true)
            ?? throw new InvalidOperationException($"Product start launcher was not found: {definition.StartPath}");
        if (File.Exists(startLauncher.Path))
        {
            if (!restart)
            {
                WriteRuntimeState(definition.Id, "starting", message: "Starting product.");
            }

            var result = await RunLoggedProcessAsync(
                definition,
                startLauncher,
                timeoutSeconds: 900,
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

        throw new InvalidOperationException($"Product start launcher was not found: {startLauncher.Path}");
    }

    private async Task<ProductRuntimeRowResponse> StopProductCoreAsync(
        ProductRuntimeDefinition definition,
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        AssertProductSupportedOnCurrentOperatingSystem(definition);

        var productPath = definition.ProductPath;
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            throw new InvalidOperationException($"Product directory was not found: {productPath}");
        }
        var stopLauncher = ResolveProductLauncher(definition, "stop", required: true)
            ?? throw new InvalidOperationException($"Product stop launcher was not found: {definition.StopPath}");

        WriteRuntimeState(definition.Id, "stopping", message: "Stopping product.");
        var result = await RunLoggedProcessAsync(
            definition,
            stopLauncher,
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
        ProductLauncherScript launcher,
        int timeoutSeconds,
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        var processCommand = GetLauncherProcessCommand(launcher);
        var operationId = _operations.NewOperationId("launcher");
        var commandLine = BuildCommandLine(processCommand.FileName, processCommand.Arguments);
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
                processCommand.FileName,
                processCommand.Arguments,
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw new TimeoutException($"{fileName} timed out after {timeoutSeconds} seconds.");
        }

        return new ProcessRunResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }
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

    private List<ProductUpdatePathPlanResponse> BuildProductUpdatePreservePlan(ProductRuntimeDefinition definition)
    {
        var rows = new List<ProductUpdatePathPlanResponse>();
        var settingsPath = GetProductSettingsFilePath(definition);
        rows.Add(NewProductUpdatePath(
            "settings",
            "file",
            settingsPath,
            willPreserve: true,
            willReplace: false,
            "Product settings must be copied back after app replacement."));

        var index = 1;
        foreach (var path in GetProductSourceDataPaths(definition))
        {
            rows.Add(NewProductUpdatePath(
                "source_data_" + index++,
                "directory",
                path,
                willPreserve: true,
                willReplace: false,
                "User source data must not be changed by sub-product update."));
        }

        index = 1;
        foreach (var path in GetProductGeneratedDataPaths(definition))
        {
            rows.Add(NewProductUpdatePath(
                "generated_data_" + index++,
                "directory",
                path,
                willPreserve: true,
                willReplace: false,
                "Generated product data should survive application replacement."));
        }

        foreach (var resource in GetProductRuntimeDataPlan(definition).Resources)
        {
            rows.Add(new ProductUpdatePathPlanResponse
            {
                Id = "runtime_" + NormalizeProductUpdateId(resource.Kind + "_" + resource.Name),
                Kind = resource.Kind,
                Path = string.IsNullOrWhiteSpace(resource.Path) ? resource.Name : resource.Path,
                Exists = resource.Exists,
                WillPreserve = true,
                WillReplace = false,
                Reason = "Runtime data and Docker resources are not application files.",
            });
        }

        return rows;
    }

    private static List<ProductUpdatePathPlanResponse> BuildProductUpdateReplacePlan(string productPath)
    {
        return
        [
            NewProductUpdatePath(
                "app_directory",
                "directory",
                productPath,
                willPreserve: false,
                willReplace: true,
                "Sub-product application files are the update target."),
        ];
    }

    private static List<ProductUpdateStepPlanResponse> BuildProductUpdateSteps()
    {
        return
        [
            NewProductUpdateStep(1, "resolve_latest", "Resolve the latest distributable version for the sub-product."),
            NewProductUpdateStep(2, "validate_artifact", "Validate the artifact before changing local files."),
            NewProductUpdateStep(3, "stage_artifact", "Extract the validated artifact into a Timeline work directory."),
            NewProductUpdateStep(4, "stop", "Stop the sub-product only if it is currently running."),
            NewProductUpdateStep(5, "backup", "Back up settings and current application files before replacement."),
            NewProductUpdateStep(6, "replace", "Replace the sub-product application directory."),
            NewProductUpdateStep(7, "restore_settings", "Restore product settings after replacement."),
            NewProductUpdateStep(8, "start", "Restart the sub-product if it was running before update."),
            NewProductUpdateStep(9, "health", "Check product runtime or API health after update."),
        ];
    }

    private static List<ProductUpdateArtifactEntryCheckResponse> BuildProductUpdateArtifactEntryChecks()
    {
        return
        [
            NewProductUpdateArtifactEntryCheck("VERSION", "file"),
            NewProductUpdateArtifactEntryCheck("timeline-product.json", "file"),
        ];
    }

    private static ProductUpdateArtifactEntryCheckResponse NewProductUpdateArtifactEntryCheck(string path, string kind)
    {
        return new ProductUpdateArtifactEntryCheckResponse
        {
            Path = path,
            Kind = kind,
        };
    }

    private static ProductUpdateArtifactValidationResponse NewProductUpdateArtifactValidationResponse(
        ProductRuntimeDefinition definition,
        string artifactPath,
        string artifactRootPrefix,
        List<ProductUpdateArtifactEntryCheckResponse> requiredEntries,
        List<ProductUpdatePlanMessageResponse> blockers,
        List<ProductUpdatePlanMessageResponse> warnings,
        string artifactType,
        string artifactProductId,
        string artifactProductName,
        string version,
        string commit,
        string channel,
        string runtimeIdentifier)
    {
        return new ProductUpdateArtifactValidationResponse
        {
            ProductId = definition.Id,
            DisplayName = definition.DisplayName,
            ArtifactPath = artifactPath,
            ArtifactRootPrefix = artifactRootPrefix,
            State = blockers.Count == 0 ? "ready" : "blocked",
            Valid = blockers.Count == 0,
            ArtifactType = artifactType,
            ArtifactProductId = artifactProductId,
            ArtifactProductName = artifactProductName,
            Version = version,
            Commit = commit,
            Channel = channel,
            RuntimeIdentifier = runtimeIdentifier,
            CurrentRuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            RequiredEntries = requiredEntries,
            Blockers = blockers,
            Warnings = warnings,
            CheckedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    private ProductUpdateArtifactStageResponse NewProductUpdateArtifactStageResponse(
        ProductRuntimeDefinition definition,
        ProductUpdateArtifactValidationResponse validation,
        string operationId,
        string stagingRoot,
        string extractedRootPath,
        List<ProductUpdatePlanMessageResponse> blockers,
        List<ProductUpdatePlanMessageResponse> warnings,
        bool staged)
    {
        return new ProductUpdateArtifactStageResponse
        {
            ProductId = definition.Id,
            DisplayName = definition.DisplayName,
            OperationId = operationId,
            ArtifactPath = validation.ArtifactPath,
            ArtifactRootPrefix = validation.ArtifactRootPrefix,
            StagingRoot = stagingRoot,
            ExtractedRootPath = extractedRootPath,
            State = staged && blockers.Count == 0 ? "staged" : "blocked",
            Staged = staged && blockers.Count == 0,
            Validation = validation,
            Preserve = BuildProductUpdatePreservePlan(definition),
            Replace = BuildProductUpdateReplacePlan(Path.GetFullPath(definition.ProductPath)),
            NextSteps = BuildProductUpdateSteps()
                .Where(step => step.Order >= 4)
                .Select(step => NewProductUpdateStep(step.Order, step.Code, step.Message))
                .ToList(),
            Blockers = blockers,
            Warnings = warnings,
            StagedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    public async Task<ProductUpdateArtifactApplyPlanResponse> GetProductUpdateArtifactApplyPlanAsync(
        string productId,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        var definition = GetProductDefinition(productId);
        var status = await ConvertRuntimeStatusAsync(definition, cancellationToken);
        var productPath = Path.GetFullPath(definition.ProductPath);
        var blockers = new List<ProductUpdatePlanMessageResponse>();
        var warnings = new List<ProductUpdatePlanMessageResponse>();

        if (!status.ProductFound)
        {
            blockers.Add(NewProductUpdateMessage(
                "product_not_installed",
                "Product is not installed."));
        }

        if (!status.ComposeFound)
        {
            blockers.Add(NewProductUpdateMessage(
                "product_incomplete",
                "Product compose/runtime files were not found."));
        }

        var appManagedByTimeline = IsProductAppManagedByTimeline(productPath);
        if (!appManagedByTimeline)
        {
            blockers.Add(NewProductUpdateMessage(
                "app_not_managed_by_timeline",
                "Product app path is outside Timeline-managed product locations."));
        }

        var productPathDeleteSafe = false;
        if (status.ProductFound)
        {
            try
            {
                AssertProductPathDeleteSafe(definition.Id, productPath);
                productPathDeleteSafe = true;
            }
            catch (Exception ex)
            {
                blockers.Add(NewProductUpdateMessage(
                    "product_path_not_safe",
                    ex.Message));
            }
        }

        var gitWorktreeClean = false;
        var gitState = "not_checked";
        if (status.ProductFound)
        {
            try
            {
                gitWorktreeClean = await TestProductGitWorktreeCleanAsync(productPath, cancellationToken);
                gitState = gitWorktreeClean ? "clean" : "dirty";
                if (!gitWorktreeClean)
                {
                    blockers.Add(NewProductUpdateMessage(
                        "git_worktree_dirty",
                        "Product has local Git changes. Commit or discard them before updating."));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                gitState = "unavailable";
                blockers.Add(NewProductUpdateMessage(
                    "git_state_unavailable",
                    ex.Message));
            }
        }

        var validation = ValidateProductUpdateArtifact(productId, artifactPath);
        blockers.AddRange(validation.Blockers.Select(message => NewProductUpdateMessage(message.Code, message.Message)));
        warnings.AddRange(validation.Warnings.Select(message => NewProductUpdateMessage(message.Code, message.Message)));

        var canApply = blockers.Count == 0 && validation.Valid;
        return new ProductUpdateArtifactApplyPlanResponse
        {
            ProductId = definition.Id,
            DisplayName = definition.DisplayName,
            ArtifactPath = validation.ArtifactPath,
            State = canApply ? "ready" : "blocked",
            CanApply = canApply,
            RequiresConfirmation = true,
            ConfirmationParameter = "confirm",
            ProductPath = productPath,
            Running = status.Running,
            ProductFound = status.ProductFound,
            ComposeFound = status.ComposeFound,
            AppManagedByTimeline = appManagedByTimeline,
            ProductPathDeleteSafe = productPathDeleteSafe,
            GitWorktreeClean = gitWorktreeClean,
            GitState = gitState,
            Validation = validation,
            Preserve = BuildProductUpdatePreservePlan(definition),
            Replace = BuildProductUpdateReplacePlan(productPath),
            Steps = BuildProductUpdateSteps(),
            Blockers = blockers,
            Warnings = warnings,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    private static void ExtractProductUpdateArtifact(
        string artifactPath,
        string artifactRootPrefix,
        string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(artifactRootPrefix))
        {
            throw new InvalidOperationException("Artifact root prefix is empty.");
        }

        var normalizedRootPrefix = artifactRootPrefix.Replace('\\', '/');
        if (!normalizedRootPrefix.EndsWith('/'))
        {
            normalizedRootPrefix += "/";
        }

        var fullStagingRoot = Path.GetFullPath(stagingRoot);
        using var archive = ZipFile.OpenRead(artifactPath);
        foreach (var entry in archive.Entries)
        {
            var entryName = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(entryName) ||
                !entryName.StartsWith(normalizedRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = entryName[normalizedRootPrefix.Length..];
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(
                fullStagingRoot,
                normalizedRootPrefix.TrimEnd('/'),
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathUnderRoot(destinationPath, fullStagingRoot))
            {
                throw new InvalidDataException($"Artifact entry escapes staging root: {entry.FullName}");
            }

            if (entryName.EndsWith('/'))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            entry.ExtractToFile(destinationPath);
        }
    }

    private static string GetProductUpdateOperationId(string productId, string? requested)
    {
        var value = string.IsNullOrWhiteSpace(requested)
            ? $"sub-product-update-{productId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"
            : requested;
        value = Regex.Replace(value, "[^A-Za-z0-9_.-]+", "-").Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(value)
            ? $"sub-product-update-{productId}-{Guid.NewGuid():N}"
            : value;
    }

    private static ProductUpdatePathPlanResponse NewProductUpdatePath(
        string id,
        string kind,
        string path,
        bool willPreserve,
        bool willReplace,
        string reason)
    {
        return new ProductUpdatePathPlanResponse
        {
            Id = id,
            Kind = kind,
            Path = path,
            Exists = ProductUpdatePathExists(path, kind),
            WillPreserve = willPreserve,
            WillReplace = willReplace,
            Reason = reason,
        };
    }

    private static ProductUpdateStepPlanResponse NewProductUpdateStep(int order, string code, string message)
    {
        return new ProductUpdateStepPlanResponse
        {
            Order = order,
            Code = code,
            Message = message,
        };
    }

    private static ProductUpdatePlanMessageResponse NewProductUpdateMessage(string code, string message)
    {
        return new ProductUpdatePlanMessageResponse
        {
            Code = code,
            Message = message,
        };
    }

    private static bool ProductUpdatePathExists(string path, string kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return kind.Equals("file", StringComparison.OrdinalIgnoreCase)
            ? File.Exists(path)
            : Directory.Exists(path);
    }

    private static string NormalizeProductUpdateId(string text)
    {
        var value = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(value) ? "resource" : value;
    }

    private static bool IsGitHubSourceArchive(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return false;
        }

        return sourceType.Equals("github-source-archive", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveProductDistributionMode(
        ProductRuntimeDefinition definition,
        ProductReleaseArtifactInfo? builtArtifact)
    {
        if (builtArtifact is not null && builtArtifact.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            return "built_product_artifact";
        }

        if (IsGitHubSourceArchive(definition.SourceType))
        {
            return builtArtifact is null
                ? "legacy_source_archive_demoted"
                : "built_product_artifact_missing";
        }

        return string.IsNullOrWhiteSpace(definition.SourceType) ? "unknown" : "unsupported";
    }

    private static string ToProductArtifactRuntimeName(string runtimeIdentifier)
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

    private static string ResolveProductArtifactRootPrefix(ZipArchive archive)
    {
        var roots = archive.Entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return roots.Count == 1 ? roots[0].TrimEnd('/') + "/" : string.Empty;
    }

    private static string ReadZipEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
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
                    ["computeMode"] = computeMode,
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
        => _settings.GetResolvedCommonAiComputeMode();

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
        var sourcePaths = GetProductSourceDataPaths(definition);
        var appManagedByTimeline = IsProductAppManagedByTimeline(productPath);

        var generatedRows = new List<ProductUninstallPathPlanResponse>();
        long generatedTotalBytes = 0;
        var warnings = new List<string>();
        foreach (var path in generatedPaths)
        {
            var exists = Directory.Exists(path);
            var sizeBytes = exists ? GetDirectorySizeBytes(path) : 0;
            var managedGeneratedPath = IsProductGeneratedDataPathManaged(definition.Id, path);
            var overlapsSourcePath = ProductPathOverlapsAnySource(path, sourcePaths);
            var willDelete = options.RemoveGeneratedData
                && exists
                && managedGeneratedPath
                && !overlapsSourcePath;
            if (willDelete)
            {
                generatedTotalBytes += sizeBytes;
            }
            else if (options.RemoveGeneratedData && exists && !managedGeneratedPath)
            {
                warnings.Add($"Generated data path is outside Timeline-managed output data and will not be removed: {path}");
            }
            else if (options.RemoveGeneratedData && exists && overlapsSourcePath)
            {
                warnings.Add($"Generated data path overlaps source input data and will not be removed: {path}");
            }

            generatedRows.Add(new ProductUninstallPathPlanResponse
            {
                Path = path,
                Exists = exists,
                SizeBytes = sizeBytes,
                WillDelete = willDelete,
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
            || File.Exists(Path.Combine(fullPath, "start.sh"))
            || File.Exists(Path.Combine(fullPath, "start.command"))
            || File.Exists(Path.Combine(fullPath, "start.cmd"))
            || File.Exists(Path.Combine(fullPath, "start.bat"))
            || File.Exists(Path.Combine(fullPath, "stop.ps1"))
            || File.Exists(Path.Combine(fullPath, "stop.sh"))
            || File.Exists(Path.Combine(fullPath, "stop.command"))
            || File.Exists(Path.Combine(fullPath, "stop.cmd"))
            || File.Exists(Path.Combine(fullPath, "stop.bat"))
            || File.Exists(Path.Combine(fullPath, "timeline-product.json"));
        var hasGit = Directory.Exists(Path.Combine(fullPath, ".git"));
        if (!hasKnownLauncher && !hasGit)
        {
            throw new InvalidOperationException($"The target directory does not look like a Timeline sub-product: {fullPath}");
        }
    }

    private void AssertProductManagedDeletePathSafe(
        string path,
        string productPath,
        IReadOnlyList<string>? additionalAllowedRoots = null)
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

        if (additionalAllowedRoots is not null)
        {
            foreach (var rootPath in additionalAllowedRoots)
            {
                if (string.IsNullOrWhiteSpace(rootPath))
                {
                    continue;
                }

                var fullAllowedRoot = Path.GetFullPath(rootPath);
                if (fullPath.Equals(fullAllowedRoot, StringComparison.OrdinalIgnoreCase)
                    || IsPathUnderRoot(fullPath, fullAllowedRoot))
                {
                    return;
                }
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

    private bool IsProductGeneratedDataPathManaged(string productId, string generatedPath)
    {
        if (string.IsNullOrWhiteSpace(generatedPath))
        {
            return false;
        }

        try
        {
            var generatedFullPath = Path.GetFullPath(generatedPath);
            var managedRoot = Path.GetFullPath(GetManagedProductDataDirectory(productId));
            return generatedFullPath.Equals(managedRoot, StringComparison.OrdinalIgnoreCase)
                || IsPathUnderRoot(generatedFullPath, managedRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool ProductPathOverlapsAnySource(
        string generatedPath,
        IReadOnlyList<string> sourcePaths)
    {
        if (string.IsNullOrWhiteSpace(generatedPath))
        {
            return false;
        }

        try
        {
            var generatedFullPath = Path.GetFullPath(generatedPath);
            foreach (var sourcePath in sourcePaths)
            {
                if (string.IsNullOrEmpty(sourcePath))
                {
                    continue;
                }

                var sourceFullPath = Path.GetFullPath(sourcePath);
                if (generatedFullPath.Equals(sourceFullPath, StringComparison.OrdinalIgnoreCase)
                    || IsPathUnderRoot(generatedFullPath, sourceFullPath)
                    || IsPathUnderRoot(sourceFullPath, generatedFullPath))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        return false;
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
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
        }

        var environment = new Dictionary<string, string>(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
        {
            ["PATH"] = currentPath,
            ["DOCKER_CONFIG"] = GetScopedDockerConfigDir(),
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            environment["Path"] = currentPath;
            environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL";
        }

        return environment;
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
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "pwsh";
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot))
        {
            systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', '/') ?? @"C:\Windows";
        }

        var candidate = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : "powershell.exe";
    }

    private static string[] GetPowerShellScriptArguments(string scriptPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return
            [
                "-NoLogo",
                "-NoProfile",
                "-File",
                scriptPath,
            ];
        }

        return
        [
            "-NoLogo",
            "-NoProfile",
            "-WindowStyle",
            "Hidden",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
        ];
    }

    private static ProductLauncherScript? ResolveProductLauncher(
        ProductRuntimeDefinition definition,
        string action,
        bool required)
    {
        foreach (var candidate in GetProductLauncherCandidates(definition.ProductPath, action))
        {
            if (File.Exists(candidate.Path))
            {
                return candidate;
            }
        }

        if (!required)
        {
            return null;
        }

        var expected = string.Join(", ", GetProductLauncherCandidates(definition.ProductPath, action).Select(candidate => candidate.Path));
        throw new InvalidOperationException(
            $"Compatible product {action} launcher was not found for {GetOperatingSystemLabel()}: {expected}");
    }

    private static IEnumerable<ProductLauncherScript> GetProductLauncherCandidates(string productPath, string action)
    {
        if (string.IsNullOrWhiteSpace(productPath))
        {
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return new ProductLauncherScript(Path.Combine(productPath, $"{action}.ps1"), ProductLauncherKind.PowerShell);
            yield return new ProductLauncherScript(Path.Combine(productPath, $"{action}.cmd"), ProductLauncherKind.Command);
            yield return new ProductLauncherScript(Path.Combine(productPath, $"{action}.bat"), ProductLauncherKind.Command);
            yield break;
        }

        yield return new ProductLauncherScript(Path.Combine(productPath, $"{action}.sh"), ProductLauncherKind.Shell);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return new ProductLauncherScript(Path.Combine(productPath, $"{action}.command"), ProductLauncherKind.Shell);
        }

        if (IsCommandAvailable("pwsh"))
        {
            yield return new ProductLauncherScript(Path.Combine(productPath, $"{action}.ps1"), ProductLauncherKind.PowerShell);
        }
    }

    private static string GetDefaultProductLauncherPath(string productPath, string action)
    {
        return GetProductLauncherCandidates(productPath, action).FirstOrDefault()?.Path ?? string.Empty;
    }

    private static LauncherProcessCommand GetLauncherProcessCommand(ProductLauncherScript launcher)
    {
        return launcher.Kind switch
        {
            ProductLauncherKind.PowerShell => new LauncherProcessCommand(
                GetPowerShellPath(),
                GetPowerShellScriptArguments(launcher.Path)),
            ProductLauncherKind.Command => new LauncherProcessCommand(
                GetCommandShellPath(),
                ["/c", launcher.Path]),
            ProductLauncherKind.Shell => new LauncherProcessCommand(
                GetShellPath(),
                [launcher.Path]),
            _ => throw new InvalidOperationException($"Unsupported launcher kind: {launcher.Kind}"),
        };
    }

    private static string GetShellPath()
    {
        return IsCommandAvailable("bash") ? "bash" : "sh";
    }

    private static string GetCommandShellPath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "sh";
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot))
        {
            systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', '/') ?? @"C:\Windows";
        }

        var candidate = Path.Combine(systemRoot, "System32", "cmd.exe");
        return File.Exists(candidate) ? candidate : "cmd.exe";
    }

    private static bool IsCommandAvailable(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return false;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(Path.Combine(entry, commandName)))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetOperatingSystemLabel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macOS";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }

        return RuntimeInformation.OSDescription;
    }

    private static string GetCurrentOperatingSystemId()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macos";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux";
        }

        return "unknown";
    }

    private static bool IsProductSupportedOnOperatingSystem(ProductRuntimeDefinition definition, string operatingSystem)
    {
        if (definition.SupportedOperatingSystems.Count == 0)
        {
            return true;
        }

        return definition.SupportedOperatingSystems.Any(
            supported => supported.Equals(operatingSystem, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertProductSupportedOnCurrentOperatingSystem(ProductRuntimeDefinition definition)
    {
        if (IsProductSupportedOnOperatingSystem(definition, GetCurrentOperatingSystemId()))
        {
            return;
        }

        throw new InvalidOperationException(GetUnsupportedOperatingSystemMessage(definition));
    }

    private static string GetUnsupportedOperatingSystemMessage(ProductRuntimeDefinition definition)
    {
        var knownMessage = definition.Id.ToLowerInvariant() switch
        {
            "windows-codex" => "この製品は Windows Codex のローカル履歴を扱うため、このOSでは未対応です。Windows環境で利用してください。",
            "pc" => "この製品は Windows のPC状態を扱うため、このOSでは未対応です。Windows環境で利用してください。",
            _ => string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(knownMessage))
        {
            return knownMessage;
        }

        if (!string.IsNullOrWhiteSpace(definition.UnsupportedOperatingSystemMessage))
        {
            return definition.UnsupportedOperatingSystemMessage;
        }

        return $"{definition.DisplayName} は {GetOperatingSystemLabel()} では未対応です。対応OS: "
            + string.Join(", ", definition.SupportedOperatingSystems);
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

        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var parts = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Any(part => part.Equals(candidate, comparison)))
        {
            return currentPath;
        }

        return string.IsNullOrEmpty(currentPath) ? candidate : candidate + Path.PathSeparator + currentPath;
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
                metadata.SupportedOperatingSystems,
                metadata.UnsupportedOperatingSystemMessage,
                GetDefaultProductLauncherPath(productPath, "start"),
                GetDefaultProductLauncherPath(productPath, "stop")));
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
        var currentOperatingSystem = GetCurrentOperatingSystemId();
        var supportedOnCurrentOperatingSystem = IsProductSupportedOnOperatingSystem(definition, currentOperatingSystem);
        var unsupportedReason = supportedOnCurrentOperatingSystem
            ? string.Empty
            : GetUnsupportedOperatingSystemMessage(definition);
        var startFound = !string.IsNullOrEmpty(definition.StartPath) && File.Exists(definition.StartPath);
        var stopFound = !string.IsNullOrEmpty(definition.StopPath) && File.Exists(definition.StopPath);
        var launcherFound = startFound || stopFound;

        var state = supportedOnCurrentOperatingSystem ? "not-created" : "unsupported";
        var status = string.Empty;
        var running = false;
        var startedAt = string.Empty;
        var message = unsupportedReason;
        JsonObject? stored = null;

        if (!supportedOnCurrentOperatingSystem)
        {
            status = "unsupported";
        }
        else if (productFound && launcherFound)
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
            UpdateAvailable = false,
            SourceArchiveUpdateAvailable = versionInfo.SourceArchiveUpdateAvailable,
            ReleaseArchiveUrl = versionInfo.ReleaseArchiveUrl,
            SettingsBackupAvailable = settingsBackup.Exists,
            SettingsBackupPath = settingsBackup.Path,
            SettingsBackupAt = settingsBackup.BackedUpAt,
            CurrentOperatingSystem = currentOperatingSystem,
            SupportedOperatingSystems = definition.SupportedOperatingSystems.ToList(),
            SupportedOnCurrentOperatingSystem = supportedOnCurrentOperatingSystem,
            UnsupportedOperatingSystemMessage = unsupportedReason,
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

        var sourceArchiveUpdateAvailable = false;
        if (productFound && !string.IsNullOrEmpty(latestVersion))
        {
            sourceArchiveUpdateAvailable = IsGitHubSourceArchive(definition.SourceType)
                && (string.IsNullOrEmpty(installedVersion)
                    || CompareVersionText(installedVersion, latestVersion) < 0);
        }

        return new ProductVersionInfo(installedVersion, latestVersion, latestStatus, sourceArchiveUpdateAvailable, archiveUrl);
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

    private async Task<ProductReleaseArtifactInfo> GetLatestProductReleaseArtifactAsync(
        ProductRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        var sourceUrl = ConvertTimelineText(definition.SourceUrl);
        if (string.IsNullOrEmpty(sourceUrl))
        {
            return new ProductReleaseArtifactInfo(
                "source_url_missing",
                "Product source URL is not configured.",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        var repository = ResolveGitHubRepository(sourceUrl);
        if (repository is null)
        {
            return new ProductReleaseArtifactInfo(
                "source_not_github",
                $"Built artifact discovery currently supports GitHub releases only: {sourceUrl}",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        var runtimeName = ToProductArtifactRuntimeName(RuntimeInformation.RuntimeIdentifier);
        var cacheKey = $"{repository.Owner}/{repository.Repo}/{runtimeName}".ToLowerInvariant();
        if (_latestReleaseArtifactCache.TryGetValue(cacheKey, out var cached)
            && DateTimeOffset.Now - cached.CachedAt < TimeSpan.FromSeconds(300))
        {
            return cached.Info;
        }

        var info = await GetLatestGitHubReleaseArtifactAsync(repository.Owner, repository.Repo, runtimeName, cancellationToken);
        _latestReleaseArtifactCache[cacheKey] = new CachedProductReleaseArtifactInfo(info, DateTimeOffset.Now);
        return info;
    }

    private async Task<ProductReleaseArtifactInfo> GetLatestGitHubReleaseArtifactAsync(
        string owner,
        string repo,
        string runtimeName,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ProductReleaseArtifactInfo(
                    "no_release",
                    $"No GitHub Release was found for {owner}/{repo}.",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    runtimeName);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProductReleaseArtifactInfo(
                    "request_failed",
                    $"GitHub Release request failed for {owner}/{repo}. HTTP {(int)response.StatusCode}",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    runtimeName);
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null)
            {
                return new ProductReleaseArtifactInfo(
                    "request_failed",
                    $"GitHub Release response could not be parsed for {owner}/{repo}.",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    runtimeName);
            }

            var latestVersion = GetString(root, "tag_name", string.Empty);
            var releaseUrl = GetString(root, "html_url", string.Empty);
            var expectedPrefix = $"{repo}-{runtimeName}-";
            var asset = root["assets"]?.AsArray()
                .OfType<JsonObject>()
                .Select(node => new
                {
                    Name = GetString(node, "name", string.Empty),
                    Url = GetString(node, "browser_download_url", string.Empty),
                })
                .FirstOrDefault(item =>
                    item.Name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) &&
                    item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            if (asset is null)
            {
                return new ProductReleaseArtifactInfo(
                    "asset_missing",
                    $"GitHub Release exists for {owner}/{repo}, but no built product artifact matching {expectedPrefix}*.zip was found.",
                    latestVersion,
                    releaseUrl,
                    string.Empty,
                    string.Empty,
                    runtimeName);
            }

            return new ProductReleaseArtifactInfo(
                "ok",
                "Built product artifact was found.",
                latestVersion,
                releaseUrl,
                asset.Name,
                asset.Url,
                runtimeName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ProductReleaseArtifactInfo(
                "request_failed",
                $"GitHub Release request failed for {owner}/{repo}. {ex.Message}",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                runtimeName);
        }
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
        IReadOnlyList<string> SupportedOperatingSystems,
        string UnsupportedOperatingSystemMessage,
        string StartPath,
        string StopPath);

    private sealed record ProductRuntimeMetadata(
        string DisplayName,
        string Description,
        string PagePath,
        IReadOnlyList<string> SupportedOperatingSystems,
        string UnsupportedOperatingSystemMessage = "");

    private sealed record ProductLauncherScript(
        string Path,
        ProductLauncherKind Kind);

    private sealed record LauncherProcessCommand(
        string FileName,
        IReadOnlyList<string> Arguments);

    private enum ProductLauncherKind
    {
        PowerShell,
        Command,
        Shell,
    }

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
        bool SourceArchiveUpdateAvailable,
        string ReleaseArchiveUrl);

    private sealed record ProductSourceInfo(
        string SourceUrl,
        string LatestVersion,
        string ArchiveUrl);

    private sealed record ProductReleaseArtifactInfo(
        string Status,
        string Message,
        string LatestVersion,
        string ReleaseUrl,
        string ArtifactName,
        string ArtifactUrl,
        string RuntimeName);

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

    private sealed record CachedProductReleaseArtifactInfo(ProductReleaseArtifactInfo Info, DateTimeOffset CachedAt);

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

    [JsonPropertyName("sourceArchiveUpdateAvailable")]
    public bool SourceArchiveUpdateAvailable { get; set; }

    [JsonPropertyName("releaseArchiveUrl")]
    public string ReleaseArchiveUrl { get; set; } = "";

    [JsonPropertyName("settingsBackupAvailable")]
    public bool SettingsBackupAvailable { get; set; }

    [JsonPropertyName("settingsBackupPath")]
    public string SettingsBackupPath { get; set; } = "";

    [JsonPropertyName("settingsBackupAt")]
    public string SettingsBackupAt { get; set; } = "";

    [JsonPropertyName("currentOperatingSystem")]
    public string CurrentOperatingSystem { get; set; } = "";

    [JsonPropertyName("supportedOperatingSystems")]
    public List<string> SupportedOperatingSystems { get; set; } = [];

    [JsonPropertyName("supportedOnCurrentOperatingSystem")]
    public bool SupportedOnCurrentOperatingSystem { get; set; } = true;

    [JsonPropertyName("unsupportedOperatingSystemMessage")]
    public string UnsupportedOperatingSystemMessage { get; set; } = "";

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

public sealed class ProductUpdatePlanResponse
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("productPath")]
    public string ProductPath { get; set; } = "";

    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = "";

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = "";

    [JsonPropertyName("distributionMode")]
    public string DistributionMode { get; set; } = "";

    [JsonPropertyName("installedVersion")]
    public string InstalledVersion { get; set; } = "";

    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = "";

    [JsonPropertyName("archiveUrl")]
    public string ArchiveUrl { get; set; } = "";

    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; set; }

    [JsonPropertyName("sourceArchiveUpdateAvailable")]
    public bool SourceArchiveUpdateAvailable { get; set; }

    [JsonPropertyName("builtArtifactStatus")]
    public string BuiltArtifactStatus { get; set; } = "";

    [JsonPropertyName("builtArtifactMessage")]
    public string BuiltArtifactMessage { get; set; } = "";

    [JsonPropertyName("builtArtifactVersion")]
    public string BuiltArtifactVersion { get; set; } = "";

    [JsonPropertyName("builtArtifactName")]
    public string BuiltArtifactName { get; set; } = "";

    [JsonPropertyName("builtArtifactUrl")]
    public string BuiltArtifactUrl { get; set; } = "";

    [JsonPropertyName("builtArtifactRuntime")]
    public string BuiltArtifactRuntime { get; set; } = "";

    [JsonPropertyName("builtArtifactUpdateAvailable")]
    public bool BuiltArtifactUpdateAvailable { get; set; }

    [JsonPropertyName("canUseCurrentUpdater")]
    public bool CanUseCurrentUpdater { get; set; }

    [JsonPropertyName("canUseBuiltArtifactUpdater")]
    public bool CanUseBuiltArtifactUpdater { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("productFound")]
    public bool ProductFound { get; set; }

    [JsonPropertyName("composeFound")]
    public bool ComposeFound { get; set; }

    [JsonPropertyName("appManagedByTimeline")]
    public bool AppManagedByTimeline { get; set; }

    [JsonPropertyName("preserve")]
    public List<ProductUpdatePathPlanResponse> Preserve { get; set; } = [];

    [JsonPropertyName("replace")]
    public List<ProductUpdatePathPlanResponse> Replace { get; set; } = [];

    [JsonPropertyName("steps")]
    public List<ProductUpdateStepPlanResponse> Steps { get; set; } = [];

    [JsonPropertyName("blockers")]
    public List<ProductUpdatePlanMessageResponse> Blockers { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<ProductUpdatePlanMessageResponse> Warnings { get; set; } = [];

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";
}

public sealed class ProductUpdatePathPlanResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("willPreserve")]
    public bool WillPreserve { get; set; }

    [JsonPropertyName("willReplace")]
    public bool WillReplace { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

public sealed class ProductUpdateStepPlanResponse
{
    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class ProductUpdatePlanMessageResponse
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class ProductUpdateArtifactValidationResponse
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("artifactPath")]
    public string ArtifactPath { get; set; } = "";

    [JsonPropertyName("artifactRootPrefix")]
    public string ArtifactRootPrefix { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("artifactType")]
    public string ArtifactType { get; set; } = "";

    [JsonPropertyName("artifactProductId")]
    public string ArtifactProductId { get; set; } = "";

    [JsonPropertyName("artifactProductName")]
    public string ArtifactProductName { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("commit")]
    public string Commit { get; set; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("runtimeIdentifier")]
    public string RuntimeIdentifier { get; set; } = "";

    [JsonPropertyName("currentRuntimeIdentifier")]
    public string CurrentRuntimeIdentifier { get; set; } = "";

    [JsonPropertyName("requiredEntries")]
    public List<ProductUpdateArtifactEntryCheckResponse> RequiredEntries { get; set; } = [];

    [JsonPropertyName("blockers")]
    public List<ProductUpdatePlanMessageResponse> Blockers { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<ProductUpdatePlanMessageResponse> Warnings { get; set; } = [];

    [JsonPropertyName("checkedAt")]
    public string CheckedAt { get; set; } = "";
}

public sealed class ProductUpdateArtifactStageResponse
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = "";

    [JsonPropertyName("artifactPath")]
    public string ArtifactPath { get; set; } = "";

    [JsonPropertyName("artifactRootPrefix")]
    public string ArtifactRootPrefix { get; set; } = "";

    [JsonPropertyName("stagingRoot")]
    public string StagingRoot { get; set; } = "";

    [JsonPropertyName("extractedRootPath")]
    public string ExtractedRootPath { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("staged")]
    public bool Staged { get; set; }

    [JsonPropertyName("validation")]
    public ProductUpdateArtifactValidationResponse Validation { get; set; } = new();

    [JsonPropertyName("preserve")]
    public List<ProductUpdatePathPlanResponse> Preserve { get; set; } = [];

    [JsonPropertyName("replace")]
    public List<ProductUpdatePathPlanResponse> Replace { get; set; } = [];

    [JsonPropertyName("nextSteps")]
    public List<ProductUpdateStepPlanResponse> NextSteps { get; set; } = [];

    [JsonPropertyName("blockers")]
    public List<ProductUpdatePlanMessageResponse> Blockers { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<ProductUpdatePlanMessageResponse> Warnings { get; set; } = [];

    [JsonPropertyName("stagedAt")]
    public string StagedAt { get; set; } = "";
}

public sealed class ProductUpdateArtifactApplyPlanResponse
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("artifactPath")]
    public string ArtifactPath { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("canApply")]
    public bool CanApply { get; set; }

    [JsonPropertyName("requiresConfirmation")]
    public bool RequiresConfirmation { get; set; }

    [JsonPropertyName("confirmationParameter")]
    public string ConfirmationParameter { get; set; } = "";

    [JsonPropertyName("productPath")]
    public string ProductPath { get; set; } = "";

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("productFound")]
    public bool ProductFound { get; set; }

    [JsonPropertyName("composeFound")]
    public bool ComposeFound { get; set; }

    [JsonPropertyName("appManagedByTimeline")]
    public bool AppManagedByTimeline { get; set; }

    [JsonPropertyName("productPathDeleteSafe")]
    public bool ProductPathDeleteSafe { get; set; }

    [JsonPropertyName("gitWorktreeClean")]
    public bool GitWorktreeClean { get; set; }

    [JsonPropertyName("gitState")]
    public string GitState { get; set; } = "";

    [JsonPropertyName("validation")]
    public ProductUpdateArtifactValidationResponse Validation { get; set; } = new();

    [JsonPropertyName("preserve")]
    public List<ProductUpdatePathPlanResponse> Preserve { get; set; } = [];

    [JsonPropertyName("replace")]
    public List<ProductUpdatePathPlanResponse> Replace { get; set; } = [];

    [JsonPropertyName("steps")]
    public List<ProductUpdateStepPlanResponse> Steps { get; set; } = [];

    [JsonPropertyName("blockers")]
    public List<ProductUpdatePlanMessageResponse> Blockers { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<ProductUpdatePlanMessageResponse> Warnings { get; set; } = [];

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";
}

public sealed class ProductUpdateArtifactEntryCheckResponse
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }
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
