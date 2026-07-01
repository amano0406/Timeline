using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public static class TimelineUpdatePlanService
{
    public static async Task<TimelineUpdatePlanResponse> GetPlanAsync(
        string timelineRoot,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(timelineRoot);
        var version = await TimelineVersionService.GetStatusAsync(root, cancellationToken);
        var dataRoot = ResolveDataRoot(root);
        var blockers = new List<TimelineUpdatePlanMessage>();
        var warnings = new List<TimelineUpdatePlanMessage>();

        if (!version.CurrentVersionStatus.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(new TimelineUpdatePlanMessage
            {
                Code = "current_version_unavailable",
                Message = "Current Timeline version could not be determined.",
            });
        }

        if (!version.ArtifactKind.Equals("built_product_artifact", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(new TimelineUpdatePlanMessage
            {
                Code = "not_built_artifact",
                Message = "This installation is not a built product artifact. Product updater must not replace developer checkouts.",
            });
        }

        if (!version.LatestVersionStatus.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(new TimelineUpdatePlanMessage
            {
                Code = "latest_artifact_unavailable",
                Message = LatestArtifactStatusMessage(version.LatestVersionStatus, version.RuntimeIdentifier),
            });
        }

        if (version.LatestVersionStatus.Equals("ok", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(version.ReleaseArtifactUrl))
        {
            blockers.Add(new TimelineUpdatePlanMessage
            {
                Code = "release_artifact_url_missing",
                Message = "Latest release artifact URL is missing.",
            });
        }

        if (string.IsNullOrWhiteSpace(version.RuntimeIdentifier))
        {
            warnings.Add(new TimelineUpdatePlanMessage
            {
                Code = "runtime_identifier_missing",
                Message = "Runtime identifier is empty. The updater cannot validate OS-specific artifacts precisely.",
            });
        }

        if (!File.Exists(Path.Combine(root, "settings.json")))
        {
            warnings.Add(new TimelineUpdatePlanMessage
            {
                Code = "settings_missing",
                Message = "settings.json is missing. Update can still be planned, but local runtime settings may be defaults.",
            });
        }

        var state = blockers.Count > 0
            ? "blocked"
            : version.UpdateAvailable
                ? "ready"
                : "up_to_date";

        return new TimelineUpdatePlanResponse
        {
            ProductId = "timeline",
            ProductName = "Timeline",
            State = state,
            CanUpdate = state.Equals("ready", StringComparison.OrdinalIgnoreCase),
            OperationOwner = "launcher",
            Mode = "plan_only",
            TimelineRoot = root,
            DataRoot = dataRoot,
            Version = version,
            Preserve = BuildPreservePlan(root, dataRoot),
            Replace = BuildReplacePlan(root),
            RuntimeResources = BuildRuntimeResourcePlan(),
            Steps = BuildSteps(),
            Blockers = blockers,
            Warnings = warnings,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    public static TimelineUpdateArtifactValidationResponse ValidateArtifact(
        string timelineRoot,
        string artifactPath)
    {
        var root = Path.GetFullPath(timelineRoot);
        var fullPath = Path.GetFullPath(artifactPath);
        var current = TimelineVersionService.GetCurrentVersion(root);
        var blockers = new List<TimelineUpdatePlanMessage>();
        var warnings = new List<TimelineUpdatePlanMessage>();
        var required = new List<TimelineUpdateArtifactEntryCheck>();
        var forbidden = new List<TimelineUpdateArtifactEntryCheck>();
        var version = new TimelineUpdateArtifactVersionInfo();
        var rootPrefix = string.Empty;
        var entryCount = 0;

        if (!File.Exists(fullPath))
        {
            blockers.Add(new TimelineUpdatePlanMessage
            {
                Code = "artifact_missing",
                Message = $"Artifact was not found: {fullPath}",
            });

            return NewArtifactValidationResponse(
                fullPath,
                current,
                rootPrefix,
                entryCount,
                version,
                required,
                forbidden,
                blockers,
                warnings);
        }

        try
        {
            using var archive = ZipFile.OpenRead(fullPath);
            var entries = archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.FullName))
                .Select(entry => NormalizeZipPath(entry.FullName))
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .ToList();
            entryCount = entries.Count;
            rootPrefix = DetectArtifactRootPrefix(entries);

            foreach (var check in RequiredArtifactEntries())
            {
                var exists = check.Kind.Equals("file", StringComparison.OrdinalIgnoreCase)
                    ? entries.Any(entry => IsSameZipPath(entry, rootPrefix + check.Path))
                    : entries.Any(entry => IsUnderZipPath(entry, rootPrefix + check.Path + "/"));
                required.Add(new TimelineUpdateArtifactEntryCheck
                {
                    Path = check.Path,
                    Kind = check.Kind,
                    Exists = exists,
                    Message = exists ? "Found." : "Missing.",
                });
                if (!exists)
                {
                    blockers.Add(new TimelineUpdatePlanMessage
                    {
                        Code = "required_entry_missing",
                        Message = $"Required artifact entry is missing: {check.Path}",
                    });
                }
            }

            foreach (var check in ForbiddenArtifactEntries())
            {
                var exists = check.Kind.Equals("file", StringComparison.OrdinalIgnoreCase)
                    ? entries.Any(entry => IsSameZipPath(entry, rootPrefix + check.Path))
                    : entries.Any(entry => IsUnderZipPath(entry, rootPrefix + check.Path + "/"));
                forbidden.Add(new TimelineUpdateArtifactEntryCheck
                {
                    Path = check.Path,
                    Kind = check.Kind,
                    Exists = exists,
                    Message = exists ? "Forbidden entry was found." : "Not present.",
                });
                if (exists)
                {
                    blockers.Add(new TimelineUpdatePlanMessage
                    {
                        Code = "forbidden_entry_present",
                        Message = $"Artifact contains local or developer-only content: {check.Path}",
                    });
                }
            }

            var versionEntryName = rootPrefix + "VERSION";
            var versionEntry = archive.Entries.FirstOrDefault(entry =>
                IsSameZipPath(NormalizeZipPath(entry.FullName), versionEntryName));
            if (versionEntry is null)
            {
                blockers.Add(new TimelineUpdatePlanMessage
                {
                    Code = "version_missing",
                    Message = "Artifact VERSION metadata is missing.",
                });
            }
            else
            {
                version = ReadArtifactVersion(versionEntry, blockers);
                if (!string.IsNullOrWhiteSpace(version.RuntimeIdentifier)
                    && !string.IsNullOrWhiteSpace(current.RuntimeIdentifier)
                    && !version.RuntimeIdentifier.Equals(current.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    blockers.Add(new TimelineUpdatePlanMessage
                    {
                        Code = "runtime_mismatch",
                        Message = $"Artifact runtime {version.RuntimeIdentifier} does not match current runtime {current.RuntimeIdentifier}.",
                    });
                }

                if (string.IsNullOrWhiteSpace(version.RuntimeIdentifier))
                {
                    warnings.Add(new TimelineUpdatePlanMessage
                    {
                        Code = "artifact_runtime_missing",
                        Message = "Artifact VERSION metadata does not include runtimeIdentifier.",
                    });
                }
            }
        }
        catch (InvalidDataException ex)
        {
            blockers.Add(new TimelineUpdatePlanMessage
            {
                Code = "artifact_invalid_zip",
                Message = $"Artifact is not a readable ZIP file. {ex.Message}",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            blockers.Add(new TimelineUpdatePlanMessage
            {
                Code = "artifact_unreadable",
                Message = $"Artifact could not be read. {ex.Message}",
            });
        }

        return NewArtifactValidationResponse(
            fullPath,
            current,
            rootPrefix,
            entryCount,
            version,
            required,
            forbidden,
            blockers,
            warnings);
    }

    private static List<TimelineUpdatePathPlan> BuildPreservePlan(string root, string dataRoot)
    {
        return
        [
            NewPathPlan("settings", Path.Combine(root, "settings.json"), "file", willPreserve: true, willReplace: false,
                "Local installation settings and ports."),
            NewPathPlan("data_root", dataRoot, "directory", willPreserve: true, willReplace: false,
                "User input, generated Timeline store, logs, backups, and sub-product managed data."),
            NewPathPlan("store", Path.Combine(dataRoot, "to_timeline"), "directory", willPreserve: true, willReplace: false,
                "Rebuilt Timeline store and derived data."),
            NewPathPlan("work", Path.Combine(dataRoot, "work"), "directory", willPreserve: true, willReplace: false,
                "Runtime work directory. It may contain in-progress or diagnostic data and is not part of the product artifact."),
        ];
    }

    private static List<TimelineUpdatePathPlan> BuildReplacePlan(string root)
    {
        return
        [
            NewPathPlan("launcher", Path.Combine(root, "launcher"), "directory", willPreserve: false, willReplace: true,
                "Timeline CLI launcher binaries."),
            NewPathPlan("launcher_tray", Path.Combine(root, "launcher-tray"), "directory", willPreserve: false, willReplace: true,
                "Resident launcher binaries."),
            NewPathPlan("local_api", Path.Combine(root, "local-api"), "directory", willPreserve: false, willReplace: true,
                "Timeline Local API runtime."),
            NewPathPlan("web", Path.Combine(root, "web"), "directory", willPreserve: false, willReplace: true,
                "Timeline Web runtime used by Docker."),
            NewPathPlan("worker", Path.Combine(root, "worker"), "directory", willPreserve: false, willReplace: true,
                "Timeline Worker runtime used by Docker."),
            NewPathPlan("docker", Path.Combine(root, "docker"), "directory", willPreserve: false, willReplace: true,
                "Docker runtime files owned by the product artifact."),
            NewPathPlan("compose", Path.Combine(root, "docker-compose.yml"), "file", willPreserve: false, willReplace: true,
                "Timeline-owned compose file generated for the product artifact."),
            NewPathPlan("version", Path.Combine(root, "VERSION"), "file", willPreserve: false, willReplace: true,
                "Product version metadata."),
            NewPathPlan("docs", Path.Combine(root, "docs"), "directory", willPreserve: false, willReplace: true,
                "User-facing product documentation shipped with the artifact."),
        ];
    }

    private static List<TimelineUpdateRuntimeResourcePlan> BuildRuntimeResourcePlan()
    {
        return
        [
            new TimelineUpdateRuntimeResourcePlan
            {
                Kind = "docker_containers",
                WillDelete = false,
                WillRecreate = true,
                Message = "Timeline containers may be stopped and recreated after application files are replaced.",
            },
            new TimelineUpdateRuntimeResourcePlan
            {
                Kind = "docker_volumes",
                WillDelete = false,
                WillRecreate = false,
                Message = "Docker volumes are preserved. Shared Ollama data must not be deleted by product update.",
            },
            new TimelineUpdateRuntimeResourcePlan
            {
                Kind = "sub_products",
                WillDelete = false,
                WillRecreate = false,
                Message = "Sub-product application directories are not replaced by Timeline body update.",
            },
        ];
    }

    private static List<TimelineUpdateStepPlan> BuildSteps()
    {
        return
        [
            NewStep(1, "download", "Download the matching built product artifact into a staging directory."),
            NewStep(2, "validate", "Validate archive name, root layout, VERSION metadata, runtime identifier, and required files."),
            NewStep(3, "stop", "Stop Timeline Web, Worker, Local API, and owned containers through the Launcher."),
            NewStep(4, "backup", "Move the current product application files to a rollback directory before replacement."),
            NewStep(5, "replace", "Move validated application files into the Timeline root while preserving settings and data root."),
            NewStep(6, "start", "Start Timeline through the Launcher."),
            NewStep(7, "verify", "Run setup verification and health checks after startup."),
            NewStep(8, "cleanup", "Remove the rollback directory only after verification succeeds."),
        ];
    }

    private static TimelineUpdateArtifactValidationResponse NewArtifactValidationResponse(
        string artifactPath,
        TimelineCurrentVersion current,
        string rootPrefix,
        int entryCount,
        TimelineUpdateArtifactVersionInfo version,
        List<TimelineUpdateArtifactEntryCheck> required,
        List<TimelineUpdateArtifactEntryCheck> forbidden,
        List<TimelineUpdatePlanMessage> blockers,
        List<TimelineUpdatePlanMessage> warnings)
    {
        return new TimelineUpdateArtifactValidationResponse
        {
            ArtifactPath = artifactPath,
            State = blockers.Count == 0 ? "ready" : "blocked",
            Valid = blockers.Count == 0,
            CurrentRuntimeIdentifier = current.RuntimeIdentifier,
            ArtifactRootPrefix = rootPrefix,
            EntryCount = entryCount,
            Version = version,
            Required = required,
            Forbidden = forbidden,
            Blockers = blockers,
            Warnings = warnings,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static TimelineUpdateArtifactVersionInfo ReadArtifactVersion(
        ZipArchiveEntry entry,
        List<TimelineUpdatePlanMessage> blockers)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var payload = JsonNode.Parse(text) as JsonObject;
            if (payload is null)
            {
                blockers.Add(new TimelineUpdatePlanMessage
                {
                    Code = "version_invalid",
                    Message = "Artifact VERSION metadata is not a JSON object.",
                });
                return new TimelineUpdateArtifactVersionInfo();
            }

            return new TimelineUpdateArtifactVersionInfo
            {
                ProductId = GetString(payload, "productId"),
                Version = GetString(payload, "version"),
                Commit = GetString(payload, "commit"),
                Channel = GetString(payload, "channel"),
                RuntimeIdentifier = GetString(payload, "runtimeIdentifier"),
                ContainerRuntimeIdentifier = GetString(payload, "containerRuntimeIdentifier"),
                CreatedAt = GetString(payload, "createdAt"),
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            blockers.Add(new TimelineUpdatePlanMessage
            {
                Code = "version_unreadable",
                Message = $"Artifact VERSION metadata could not be read. {ex.Message}",
            });
            return new TimelineUpdateArtifactVersionInfo();
        }
    }

    private static List<TimelineUpdateArtifactEntryCheck> RequiredArtifactEntries()
    {
        return
        [
            NewArtifactEntryCheck("launcher", "directory"),
            NewArtifactEntryCheck("launcher-tray", "directory"),
            NewArtifactEntryCheck("local-api", "directory"),
            NewArtifactEntryCheck("web", "directory"),
            NewArtifactEntryCheck("worker", "directory"),
            NewArtifactEntryCheck("docker-compose.yml", "file"),
            NewArtifactEntryCheck("VERSION", "file"),
        ];
    }

    private static List<TimelineUpdateArtifactEntryCheck> ForbiddenArtifactEntries()
    {
        return
        [
            NewArtifactEntryCheck(".git", "directory"),
            NewArtifactEntryCheck("data", "directory"),
            NewArtifactEntryCheck("docs-temp", "directory"),
            NewArtifactEntryCheck("scripts-temp", "directory"),
            NewArtifactEntryCheck("source-downloads", "directory"),
            NewArtifactEntryCheck("release", "directory"),
            NewArtifactEntryCheck("settings.json", "file"),
        ];
    }

    private static TimelineUpdateArtifactEntryCheck NewArtifactEntryCheck(string path, string kind)
        => new()
        {
            Path = path,
            Kind = kind,
        };

    private static string DetectArtifactRootPrefix(IReadOnlyList<string> entries)
    {
        var required = new[] { "launcher/", "local-api/", "web/" };
        if (required.All(prefix => entries.Any(entry => IsUnderZipPath(entry, prefix))))
        {
            return string.Empty;
        }

        var firstSegments = entries
            .Select(entry => entry.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var segment in firstSegments)
        {
            var prefix = segment + "/";
            if (required.All(requiredPath => entries.Any(entry => IsUnderZipPath(entry, prefix + requiredPath))))
            {
                return prefix;
            }
        }

        return string.Empty;
    }

    private static string NormalizeZipPath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static bool IsSameZipPath(string left, string right)
        => NormalizeZipPath(left).TrimEnd('/').Equals(NormalizeZipPath(right).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderZipPath(string entry, string prefix)
        => NormalizeZipPath(entry).StartsWith(NormalizeZipPath(prefix), StringComparison.OrdinalIgnoreCase);

    private static string LatestArtifactStatusMessage(string latestVersionStatus, string runtimeIdentifier)
    {
        return latestVersionStatus switch
        {
            "no_release" => "No GitHub Release was found. Source archives are not product update targets.",
            "asset_missing" => $"A GitHub Release exists, but no matching built product artifact was found for runtime {runtimeIdentifier}.",
            "request_failed" => "Latest GitHub Release could not be checked.",
            "" => "Latest release artifact status is empty.",
            _ => $"Latest release artifact status is {latestVersionStatus}.",
        };
    }

    private static TimelineUpdateStepPlan NewStep(int order, string code, string message)
        => new()
        {
            Order = order,
            Code = code,
            Message = message,
        };

    private static TimelineUpdatePathPlan NewPathPlan(
        string id,
        string path,
        string kind,
        bool willPreserve,
        bool willReplace,
        string reason)
    {
        var exists = kind.Equals("file", StringComparison.OrdinalIgnoreCase)
            ? File.Exists(path)
            : Directory.Exists(path);

        return new TimelineUpdatePathPlan
        {
            Id = id,
            Path = path,
            Kind = kind,
            Exists = exists,
            WillPreserve = willPreserve,
            WillReplace = willReplace,
            Reason = reason,
        };
    }

    private static string ResolveDataRoot(string root)
    {
        var settingsPath = Path.Combine(root, "settings.json");
        var dataRoot = "data";
        if (File.Exists(settingsPath))
        {
            try
            {
                var payload = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
                var value = GetString(payload, "dataRoot");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    dataRoot = value;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                dataRoot = "data";
            }
        }

        return Path.GetFullPath(Path.IsPathRooted(dataRoot)
            ? dataRoot
            : Path.Combine(root, dataRoot));
    }

    private static string GetString(JsonObject? source, string name)
    {
        if (source is null)
        {
            return string.Empty;
        }

        foreach (var property in source)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value?.ToString().Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}

public sealed class TimelineUpdatePlanResponse
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("canUpdate")]
    public bool CanUpdate { get; set; }

    [JsonPropertyName("operationOwner")]
    public string OperationOwner { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("timelineRoot")]
    public string TimelineRoot { get; set; } = "";

    [JsonPropertyName("dataRoot")]
    public string DataRoot { get; set; } = "";

    [JsonPropertyName("version")]
    public TimelineVersionStatus Version { get; set; } = new();

    [JsonPropertyName("preserve")]
    public List<TimelineUpdatePathPlan> Preserve { get; set; } = [];

    [JsonPropertyName("replace")]
    public List<TimelineUpdatePathPlan> Replace { get; set; } = [];

    [JsonPropertyName("runtimeResources")]
    public List<TimelineUpdateRuntimeResourcePlan> RuntimeResources { get; set; } = [];

    [JsonPropertyName("steps")]
    public List<TimelineUpdateStepPlan> Steps { get; set; } = [];

    [JsonPropertyName("blockers")]
    public List<TimelineUpdatePlanMessage> Blockers { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<TimelineUpdatePlanMessage> Warnings { get; set; } = [];

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";
}

public sealed class TimelineUpdatePathPlan
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("willPreserve")]
    public bool WillPreserve { get; set; }

    [JsonPropertyName("willReplace")]
    public bool WillReplace { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

public sealed class TimelineUpdateRuntimeResourcePlan
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("willDelete")]
    public bool WillDelete { get; set; }

    [JsonPropertyName("willRecreate")]
    public bool WillRecreate { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class TimelineUpdateStepPlan
{
    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class TimelineUpdatePlanMessage
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class TimelineUpdateArtifactValidationResponse
{
    [JsonPropertyName("artifactPath")]
    public string ArtifactPath { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("currentRuntimeIdentifier")]
    public string CurrentRuntimeIdentifier { get; set; } = "";

    [JsonPropertyName("artifactRootPrefix")]
    public string ArtifactRootPrefix { get; set; } = "";

    [JsonPropertyName("entryCount")]
    public int EntryCount { get; set; }

    [JsonPropertyName("version")]
    public TimelineUpdateArtifactVersionInfo Version { get; set; } = new();

    [JsonPropertyName("required")]
    public List<TimelineUpdateArtifactEntryCheck> Required { get; set; } = [];

    [JsonPropertyName("forbidden")]
    public List<TimelineUpdateArtifactEntryCheck> Forbidden { get; set; } = [];

    [JsonPropertyName("blockers")]
    public List<TimelineUpdatePlanMessage> Blockers { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<TimelineUpdatePlanMessage> Warnings { get; set; } = [];

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";
}

public sealed class TimelineUpdateArtifactVersionInfo
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("commit")]
    public string Commit { get; set; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("runtimeIdentifier")]
    public string RuntimeIdentifier { get; set; } = "";

    [JsonPropertyName("containerRuntimeIdentifier")]
    public string ContainerRuntimeIdentifier { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
}

public sealed class TimelineUpdateArtifactEntryCheck
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
