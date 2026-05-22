using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class TimelineAudioFileService
{
    private static readonly string[] DefaultAudioExtensions =
    [
        ".mp3",
        ".wav",
        ".m4a",
        ".aac",
        ".flac",
    ];

    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineLocalApiOptions _options;
    private readonly TimelineAudioVerbalizationJobRegistry _audioVerbalizationJobs;
    private JsonObject? _hardwareCache;

    public TimelineAudioFileService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        TimelineLocalApiOptions options,
        TimelineAudioVerbalizationJobRegistry audioVerbalizationJobs)
    {
        _settings = settings;
        _operations = operations;
        _options = options;
        _audioVerbalizationJobs = audioVerbalizationJobs;
    }

    public JsonObject GetOverview()
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForAudio",
            "audio_overview",
            "started",
            "Web operation started.");

        try
        {
            var result = GetOverviewCore();
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForAudio",
                "audio_overview",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["productFound"] = GetBool(result, "productFound", false),
                    ["audioFileCount"] = GetInt(result, "audioFileCount", 0),
                    ["audioItemCount"] = GetInt(result, "audioItemCount", 0),
                });
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForAudio",
                "audio_overview",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    public JsonObject GetFiles(int page, int pageSize)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForAudio",
            "audio_files_list",
            "started",
            "Web operation started.");

        try
        {
            var result = GetFilesCore(page, pageSize);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForAudio",
                "audio_files_list",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["total"] = GetInt(result, "total", 0),
                });
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForAudio",
                "audio_files_list",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    public JsonObject GetFileDetail(string? sourceId, string? relativePath, string localApiBaseUrl)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForAudio",
            "audio_file_detail",
            "started",
            "Web operation started.");

        try
        {
            var result = GetFileDetailCore(sourceId, relativePath, localApiBaseUrl);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForAudio",
                "audio_file_detail",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["available"] = GetBool(result, "available", false),
                    ["timelineAvailable"] = GetBool(result, "timelineAvailable", false),
                    ["message"] = GetString(result, "message", string.Empty),
                });
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForAudio",
                "audio_file_detail",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    public JsonObject GetAudioVerbalizationStatus(
        string? sourceId,
        string? relativePath,
        string localApiBaseUrl)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "Timeline",
            "audio_verbalization_status",
            "started",
            "Web operation started.");

        try
        {
            var detail = GetFileDetailCore(sourceId, relativePath, localApiBaseUrl);
            var status = GetObject(detail, "audioVerbalization") ?? NewAudioVerbalizationStatusObject(
                false,
                "unavailable",
                string.Empty,
                string.Empty,
                "ja-JP",
                "qwen3.5:9b",
                0,
                0,
                0,
                string.Empty,
                string.Empty,
                "Audio verbalization status was not available.");
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "Timeline",
                "audio_verbalization_status",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["available"] = GetBool(status, "available", false),
                    ["state"] = GetString(status, "state", string.Empty),
                });
            return status;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "Timeline",
                "audio_verbalization_status",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    public JsonObject GetAudioVerbalizationResult(
        string? sourceId,
        string? relativePath,
        string localApiBaseUrl)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "Timeline",
            "audio_verbalization_result",
            "started",
            "Web operation started.");

        try
        {
            var detail = GetFileDetailCore(sourceId, relativePath, localApiBaseUrl);
            var status = GetObject(detail, "audioVerbalization") ?? NewAudioVerbalizationStatusObject(
                false,
                "unavailable",
                string.Empty,
                string.Empty,
                "ja-JP",
                "qwen3.5:9b",
                0,
                0,
                0,
                string.Empty,
                string.Empty,
                "Audio verbalization status was not available.");
            var storedResult = GetAudioVerbalizationResultFromStatus(status);
            JsonObject result;
            if (GetBool(storedResult, "available", false)
                && GetArray(storedResult, "turns").Count > 0)
            {
                result = storedResult;
            }
            else if (DetailHasReadableTranscript(detail))
            {
                result = NewSourceTranscriptResultFromDetail(detail, status);
            }
            else
            {
                result = storedResult;
            }

            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "Timeline",
                "audio_verbalization_result",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["available"] = GetBool(result, "available", false),
                    ["turnCount"] = GetArray(result, "turns").Count,
                });
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "Timeline",
                "audio_verbalization_result",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private JsonObject GetOverviewCore()
    {
        var productPath = GetProductPath();
        var productFound = !string.IsNullOrEmpty(productPath) && Directory.Exists(productPath);
        var settings = ReadAudioSettings();
        if (!productFound)
        {
            return new JsonObject
            {
                ["productFound"] = false,
                ["productPath"] = productPath,
                ["hasToken"] = !string.IsNullOrEmpty(settings.HuggingFaceToken),
                ["tokenPreview"] = GetTokenPreview(settings.HuggingFaceToken),
                ["computeMode"] = settings.ComputeMode,
                ["cpuDevices"] = new JsonArray(),
                ["gpuDevices"] = new JsonArray(),
                ["inputRoots"] = ConvertRootArray(settings.InputRoots),
                ["outputRoot"] = settings.OutputRoot is null ? null : ConvertRoot(settings.OutputRoot),
                ["audioFileCount"] = 0,
                ["audioItemCount"] = 0,
                ["audioVerbalizationTargetFileCount"] = 0,
                ["audioVerbalizedFileCount"] = 0,
                ["workerState"] = "unknown",
                ["activeRun"] = null,
                ["restartRequired"] = false,
                ["message"] = "TimelineForAudio was not found.",
            };
        }

        var catalog = GetAudioCatalog(settings);
        var activeRun = GetActiveRun(settings);
        var sourceFileCount = GetSourceRows(settings).Count;
        var audioFileCount = GetInt(activeRun, "itemsTotal", 0);
        if (audioFileCount <= 0)
        {
            audioFileCount = sourceFileCount;
        }

        var verbalizationSummary = GetAudioVerbalizationFileSummary(catalog);
        var hardware = GetHardwareDevices();
        return new JsonObject
        {
            ["productFound"] = true,
            ["productPath"] = productPath,
            ["hasToken"] = !string.IsNullOrEmpty(settings.HuggingFaceToken),
            ["tokenPreview"] = GetTokenPreview(settings.HuggingFaceToken),
            ["computeMode"] = settings.ComputeMode,
            ["cpuDevices"] = CloneArray(hardware, "cpuDevices"),
            ["gpuDevices"] = CloneArray(hardware, "gpuDevices"),
            ["inputRoots"] = ConvertRootArray(settings.InputRoots),
            ["outputRoot"] = settings.OutputRoot is null ? null : ConvertRoot(settings.OutputRoot),
            ["audioFileCount"] = audioFileCount,
            ["audioItemCount"] = catalog.ByIdentity.Count,
            ["audioVerbalizationTargetFileCount"] = GetInt(verbalizationSummary, "targetFileCount", 0),
            ["audioVerbalizedFileCount"] = GetInt(verbalizationSummary, "verbalizedFileCount", 0),
            ["workerState"] = GetWorkerState(activeRun),
            ["activeRun"] = activeRun,
            ["restartRequired"] = false,
            ["message"] = "TimelineForAudio is linked as a local product.",
        };
    }

    private JsonObject GetFilesCore(int page, int pageSize)
    {
        var settings = ReadAudioSettings();
        var catalog = GetAudioCatalog(settings);
        var rows = GetSourceRows(settings)
            .OrderByDescending(row => row.ModifiedAt, StringComparer.Ordinal)
            .ThenByDescending(row => row.SourceFileIdentity, StringComparer.Ordinal)
            .ToList();

        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Max(1, pageSize);
        var offset = (effectivePage - 1) * effectivePageSize;
        var files = new JsonArray();
        foreach (var row in rows.Skip(offset).Take(effectivePageSize))
        {
            files.Add(ConvertSourceFileRow(row, catalog));
        }

        return new JsonObject
        {
            ["total"] = rows.Count,
            ["truncated"] = false,
            ["pagination"] = NewPagination(effectivePage, effectivePageSize, rows.Count, files.Count),
            ["files"] = files,
        };
    }

    private JsonObject GetFileDetailCore(string? sourceId, string? relativePath, string localApiBaseUrl)
    {
        var settings = ReadAudioSettings();
        var source = ResolveSourceFile(settings, sourceId, relativePath);
        if (source is null)
        {
            return new JsonObject
            {
                ["available"] = false,
                ["message"] = "Audio source file was not found.",
                ["file"] = null,
                ["timelineAvailable"] = false,
                ["audioAvailable"] = false,
                ["audioUrl"] = string.Empty,
                ["timelinePath"] = string.Empty,
                ["convertInfoPath"] = string.Empty,
                ["pipelineVersion"] = string.Empty,
                ["unitType"] = string.Empty,
                ["turns"] = new JsonArray(),
                ["audioVerbalization"] = NewAudioVerbalizationStatusObject(
                    false,
                    "unavailable",
                    string.Empty,
                    string.Empty,
                    "ja-JP",
                    "qwen3.5:9b",
                    0,
                    0,
                    0,
                    string.Empty,
                    string.Empty,
                    "Audio source file was not found."),
            };
        }

        var catalog = GetAudioCatalog(settings);
        var catalogRow = FindCatalogRow(catalog, source.Row);
        var timeline = ReadJsonFile(catalogRow?.TimelinePath ?? string.Empty);
        var timelineAvailable = timeline is not null;
        var turns = GetTimelineTurns(timeline);
        var speakerCount = turns
            .OfType<JsonObject>()
            .Select(turn => GetString(turn, "speaker", string.Empty))
            .Where(speaker => !string.IsNullOrEmpty(speaker))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var unitType = turns
            .OfType<JsonObject>()
            .Select(turn => GetString(turn, "unitType", string.Empty))
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;
        var pipeline = GetObject(timeline, "pipeline");
        var sourceNode = GetObject(timeline, "source");
        var convertInfo = ReadJsonFile(catalogRow?.ConvertInfoPath ?? string.Empty);
        var durationSec = GetDoubleAny(
            sourceNode,
            ["duration_sec", "durationSec", "duration_seconds", "durationSeconds"],
            catalogRow?.DurationSec);
        var relativeForUrl = source.Row.RelativePath.Replace('\\', '/');
        var sourceIdForUrl = Uri.EscapeDataString(source.Row.SourceId);
        var pathForUrl = Uri.EscapeDataString(relativeForUrl);
        var audioUrl = localApiBaseUrl.TrimEnd('/')
            + "/products/audio/files/source?sourceId="
            + sourceIdForUrl
            + "&path="
            + pathForUrl;

        var fileRow = new JsonObject
        {
            ["itemId"] = catalogRow?.ItemId ?? source.Row.SourceFileIdentity,
            ["sourceId"] = source.Row.SourceId,
            ["sourceFileIdentity"] = source.Row.SourceFileIdentity,
            ["sourceDisplayName"] = source.Root.DisplayName,
            ["sourceName"] = source.Root.DisplayName,
            ["rootPath"] = source.ResolvedRootPath,
            ["displayPath"] = source.Row.DisplayPath,
            ["relativePath"] = relativeForUrl,
            ["directory"] = GetDirectoryFromRelativePath(relativeForUrl),
            ["fileName"] = source.Row.FileName,
            ["sizeBytes"] = source.Row.SizeBytes,
            ["modifiedAt"] = source.Row.ModifiedAt,
            ["status"] = timelineAvailable ? "completed" : "detected",
            ["durationSec"] = durationSec is null ? null : JsonValue.Create(durationSec.Value),
            ["hasTimeline"] = timelineAvailable,
            ["hasAudio"] = true,
            ["runId"] = catalogRow?.RunId ?? string.Empty,
            ["mediaId"] = catalogRow?.MediaId ?? string.Empty,
            ["turnCount"] = turns.Count,
            ["speakerCount"] = speakerCount,
        };

        return new JsonObject
        {
            ["available"] = true,
            ["message"] = string.Empty,
            ["file"] = fileRow,
            ["timelineAvailable"] = timelineAvailable,
            ["audioAvailable"] = true,
            ["audioUrl"] = audioUrl,
            ["timelinePath"] = catalogRow?.TimelinePath ?? string.Empty,
            ["convertInfoPath"] = catalogRow?.ConvertInfoPath ?? string.Empty,
            ["pipelineVersion"] = GetStringAny(pipeline, ["pipeline_version", "pipelineVersion"], string.Empty),
            ["unitType"] = unitType,
            ["turns"] = turns,
            ["observation"] = NewAudioObservation(convertInfo, timeline, turns),
            ["audioVerbalization"] = NewAudioVerbalizationStatus(fileRow),
        };
    }

    private AudioSettingsSnapshot ReadAudioSettings()
    {
        var path = GetAudioSettingsFilePath();
        var payload = ReadJsonFile(path) ?? NewDefaultSettingsPayload();
        var inputRoots = new List<AudioRootRow>();
        var inputIndex = 1;
        foreach (var rootNode in GetArray(payload, "inputRoots"))
        {
            inputRoots.Add(ConvertRootNode(rootNode, "audio-" + inputIndex.ToString(CultureInfo.InvariantCulture)));
            inputIndex++;
        }

        var outputRoots = new List<AudioRootRow>();
        if (TryGetNode(payload, "outputRoot", out var outputRootNode) && outputRootNode is not null)
        {
            outputRoots.Add(ConvertRootNode(outputRootNode, "master"));
        }
        else
        {
            foreach (var rootNode in GetArray(payload, "outputRoots"))
            {
                outputRoots.Add(ConvertRootNode(rootNode, "master"));
            }
        }

        var extensions = GetStringArray(payload, "audioExtensions").ToList();
        if (extensions.Count == 0)
        {
            extensions.AddRange(DefaultAudioExtensions);
        }

        var computeMode = GetString(payload, "computeMode", "cpu").ToLowerInvariant();
        if (computeMode is not ("cpu" or "gpu"))
        {
            computeMode = "cpu";
        }

        return new AudioSettingsSnapshot(
            inputRoots,
            outputRoots.FirstOrDefault(),
            outputRoots,
            NormalizeExtensions(extensions),
            GetStringAny(payload, ["huggingFaceToken", "huggingfaceToken", "token"], string.Empty),
            computeMode);
    }

    private JsonObject NewDefaultSettingsPayload()
        => new()
        {
            ["schemaVersion"] = 1,
            ["inputRoots"] = new JsonArray(),
            ["outputRoot"] = null,
            ["outputRoots"] = new JsonArray(),
            ["audioExtensions"] = NewStringArray(DefaultAudioExtensions),
            ["huggingFaceToken"] = string.Empty,
            ["computeMode"] = "cpu",
        };

    private List<AudioSourceRow> GetSourceRows(AudioSettingsSnapshot settings)
    {
        var rows = new List<AudioSourceRow>();
        foreach (var root in settings.InputRoots)
        {
            if (!root.Enabled || string.IsNullOrEmpty(root.Path))
            {
                continue;
            }

            var rootPath = ConvertAudioLocalPath(root.Path);
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            {
                continue;
            }

            var resolvedRoot = GetFullPathOrOriginal(rootPath).TrimEnd('\\', '/');
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(resolvedRoot, "*", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var filePath in files)
            {
                if (!settings.AudioExtensions.Contains(Path.GetExtension(filePath)))
                {
                    continue;
                }

                FileInfo file;
                try
                {
                    file = new FileInfo(filePath);
                    if (!file.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    continue;
                }

                var relativePath = GetRelativePath(resolvedRoot, file.FullName);
                var identityRelativePath = relativePath.Replace('\\', '/');
                var sourceId = root.Path;
                rows.Add(new AudioSourceRow(
                    sourceId,
                    sourceId + "::" + identityRelativePath,
                    sourceId,
                    sourceId,
                    root.Path,
                    file.FullName,
                    relativePath,
                    GetDirectoryFromRelativePath(relativePath),
                    file.Name,
                    file.Length,
                    file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            }
        }

        return rows;
    }

    private ResolvedAudioSource? ResolveSourceFile(
        AudioSettingsSnapshot settings,
        string? sourceId,
        string? relativePath)
    {
        var sourceIdText = ConvertTimelineText(sourceId);
        var relativeText = ConvertTimelineText(relativePath)
            .Replace('/', '\\')
            .TrimStart('\\', '/');
        if (string.IsNullOrEmpty(sourceIdText) || string.IsNullOrEmpty(relativeText))
        {
            return null;
        }

        foreach (var root in settings.InputRoots)
        {
            if (!root.Enabled || string.IsNullOrEmpty(root.Path))
            {
                continue;
            }

            var rootMatches = root.Id.Equals(sourceIdText, StringComparison.Ordinal)
                || root.Path.Equals(sourceIdText, StringComparison.Ordinal)
                || GetNormalizedPathKey(root.Path).Equals(GetNormalizedPathKey(sourceIdText), StringComparison.OrdinalIgnoreCase);
            if (!rootMatches)
            {
                continue;
            }

            var rootPath = ConvertAudioLocalPath(root.Path);
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            {
                continue;
            }

            var resolvedRoot = GetFullPathOrOriginal(rootPath).TrimEnd('\\', '/');
            var candidate = Path.Combine(resolvedRoot, relativeText);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var resolvedCandidate = GetFullPathOrOriginal(candidate);
            var rootPrefix = GetNormalizedPathKey(resolvedRoot) + "\\";
            var candidateKey = GetNormalizedPathKey(resolvedCandidate);
            if (!candidateKey.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!settings.AudioExtensions.Contains(Path.GetExtension(resolvedCandidate)))
            {
                continue;
            }

            FileInfo file;
            try
            {
                file = new FileInfo(resolvedCandidate);
                if (!file.Exists)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                continue;
            }

            var relative = GetRelativePath(resolvedRoot, resolvedCandidate).Replace('\\', '/');
            return new ResolvedAudioSource(
                root,
                resolvedRoot,
                new AudioSourceRow(
                    root.Path,
                    root.Path + "::" + relative,
                    root.DisplayName,
                    root.DisplayName,
                    root.Path,
                    file.FullName,
                    relative,
                    GetDirectoryFromRelativePath(relative),
                    file.Name,
                    file.Length,
                    file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        return null;
    }

    private AudioGeneratedCatalog GetAudioCatalog(AudioSettingsSnapshot settings)
    {
        var catalog = new AudioGeneratedCatalog();
        var outputRoot = GetOutputRootPath(settings);
        if (string.IsNullOrEmpty(outputRoot) || !Directory.Exists(outputRoot))
        {
            return catalog;
        }

        ReadCatalogJsonLines(outputRoot, catalog);
        if (catalog.ByIdentity.Count > 0)
        {
            return catalog;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(outputRoot).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return catalog;
        }

        foreach (var directory in directories)
        {
            var convertInfoPath = Path.Combine(directory, "convert_info.json");
            var convertInfo = ReadJsonFile(convertInfoPath);
            if (convertInfo is null)
            {
                continue;
            }

            var source = GetObject(convertInfo, "source");
            var identity = GetStringAny(
                source,
                ["source_file_identity", "sourceFileIdentity"],
                string.Empty);
            if (string.IsNullOrEmpty(identity))
            {
                continue;
            }

            var relativePath = GetStringAny(
                source,
                ["source_relative_path", "sourceRelativePath", "relative_path", "relativePath"],
                string.Empty);
            var sizeBytes = GetLongAny(source, ["size_bytes", "sizeBytes"], 0);
            var timelinePath = ResolveTimelinePath(directory);
            var artifactSummary = GetAudioArtifactSummary(directory, timelinePath);
            var durationSec = GetDoubleAny(source, ["duration_sec", "durationSec", "duration_seconds", "durationSeconds"], null);
            var turnCount = GetInt(artifactSummary, "turnCount", 0);
            var pipeline = GetObject(convertInfo, "pipeline");
            var phone = GetObject(pipeline, "phone_recognition");
            var bulkTurnCount = GetIntAny(phone, ["turn_count", "turnCount"], 0);
            if (turnCount <= 0)
            {
                var diarization = GetObject(pipeline, "speaker_diarization");
                var transcription = GetObject(pipeline, "speech_transcription");
                var counts = GetObject(convertInfo, "counts");
                turnCount = GetIntAny(
                    phone,
                    ["turn_count", "turnCount"],
                    GetIntAny(
                        transcription,
                        ["segment_count", "segmentCount"],
                        GetIntAny(counts, ["transcript_segments", "transcriptSegments", "speaker_turns", "speakerTurns"], 0)));
                if (turnCount <= 0)
                {
                    turnCount = GetIntAny(diarization, ["turn_count", "turnCount"], 0);
                }
            }

            AddCatalogRow(
                catalog,
                new AudioCatalogRow(
                    identity,
                    Path.GetFileName(directory),
                    Path.GetFileName(directory),
                    string.Empty,
                    relativePath,
                    sizeBytes,
                    directory,
                    timelinePath,
                    convertInfoPath,
                    durationSec,
                    GetBool(artifactSummary, "hasTimeline", false),
                    GetBool(artifactSummary, "hasAudio", false),
                    turnCount,
                    bulkTurnCount,
                    GetInt(artifactSummary, "speakerCount", 0)));
        }

        return catalog;
    }

    private void ReadCatalogJsonLines(string outputRoot, AudioGeneratedCatalog catalog)
    {
        var catalogPath = Path.Combine(outputRoot, ".timeline-for-audio", "catalog.jsonl");
        if (!File.Exists(catalogPath))
        {
            return;
        }

        foreach (var line in File.ReadLines(catalogPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? row;
            try
            {
                row = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (row is null)
            {
                continue;
            }

            var identity = GetStringAny(row, ["source_file_identity", "sourceFileIdentity"], string.Empty);
            if (string.IsNullOrEmpty(identity))
            {
                continue;
            }

            var mediaId = GetStringAny(row, ["audio_id", "audioId", "media_id", "mediaId"], string.Empty);
            var mediaDirectory = GetMediaDirectory(outputRoot, row, mediaId);
            var timelinePath = ResolveTimelinePath(mediaDirectory);
            var artifactSummary = GetAudioArtifactSummary(mediaDirectory, timelinePath);
            var relativePath = GetStringAny(row, ["relative_path", "relativePath", "source_relative_path", "sourceRelativePath"], string.Empty);
            var sizeBytes = GetLongAny(row, ["size_bytes", "sizeBytes"], 0);
            AddCatalogRow(
                catalog,
                new AudioCatalogRow(
                    identity,
                    string.IsNullOrEmpty(mediaId) ? identity : mediaId,
                    mediaId,
                    GetStringAny(row, ["run_id", "runId"], string.Empty),
                    relativePath,
                    sizeBytes,
                    mediaDirectory,
                    timelinePath,
                    string.Empty,
                    GetDoubleAny(row, ["duration_sec", "durationSec", "duration_seconds", "durationSeconds"], null),
                    GetBool(artifactSummary, "hasTimeline", false),
                    GetBool(artifactSummary, "hasAudio", false),
                    GetIntAny(row, ["turn_count", "turnCount"], GetInt(artifactSummary, "turnCount", 0)),
                    GetIntAny(row, ["turn_count", "turnCount"], 0),
                    GetIntAny(row, ["speaker_count", "speakerCount"], GetInt(artifactSummary, "speakerCount", 0))));
        }
    }

    private JsonObject ConvertSourceFileRow(AudioSourceRow sourceRow, AudioGeneratedCatalog catalog)
    {
        var catalogRow = FindCatalogRow(catalog, sourceRow);
        var itemId = catalogRow?.ItemId ?? sourceRow.SourceFileIdentity;
        var hasTimeline = catalogRow?.HasTimeline ?? false;
        var row = new JsonObject
        {
            ["itemId"] = itemId,
            ["sourceId"] = sourceRow.SourceId,
            ["sourceFileIdentity"] = sourceRow.SourceFileIdentity,
            ["sourceDisplayName"] = sourceRow.SourceDisplayName,
            ["sourceName"] = sourceRow.SourceName,
            ["rootPath"] = sourceRow.RootPath,
            ["displayPath"] = sourceRow.DisplayPath,
            ["relativePath"] = sourceRow.RelativePath,
            ["directory"] = sourceRow.Directory,
            ["fileName"] = sourceRow.FileName,
            ["sizeBytes"] = sourceRow.SizeBytes,
            ["modifiedAt"] = sourceRow.ModifiedAt,
            ["status"] = hasTimeline ? "completed" : "detected",
            ["durationSec"] = catalogRow?.DurationSec is null
                ? null
                : JsonValue.Create(catalogRow.DurationSec.Value),
            ["hasTimeline"] = hasTimeline,
            ["hasAudio"] = catalogRow?.HasAudio ?? false,
            ["runId"] = catalogRow?.RunId ?? string.Empty,
            ["mediaId"] = catalogRow?.MediaId ?? string.Empty,
            ["turnCount"] = catalogRow?.TurnCount ?? 0,
            ["speakerCount"] = catalogRow?.SpeakerCount ?? 0,
        };
        row["audioVerbalization"] = NewAudioVerbalizationStatus(row);
        return row;
    }

    private static JsonArray GetTimelineTurns(JsonObject? timeline)
    {
        var turns = new JsonArray();
        var index = 1;
        foreach (var turnNode in GetArray(timeline, "turns"))
        {
            if (turnNode is not JsonObject turn)
            {
                index++;
                continue;
            }

            turns.Add(ConvertTimelineTurn(turn, index));
            index++;
        }

        return turns;
    }

    private static JsonObject ConvertTimelineTurn(JsonObject turn, int fallbackIndex)
    {
        return new JsonObject
        {
            ["index"] = GetInt(turn, "index", fallbackIndex),
            ["startSec"] = GetDoubleAny(turn, ["start_sec", "startSec"], 0),
            ["endSec"] = GetDoubleAny(turn, ["end_sec", "endSec"], 0),
            ["absoluteStartAt"] = GetStringAny(turn, ["absolute_start_at", "absoluteStartAt"], string.Empty),
            ["absoluteEndAt"] = GetStringAny(turn, ["absolute_end_at", "absoluteEndAt"], string.Empty),
            ["speaker"] = GetStringAny(turn, ["speaker", "speaker_label", "speakerLabel"], string.Empty),
            ["text"] = GetStringAny(turn, ["text", "transcriptText", "readableText"], string.Empty),
            ["phoneTokens"] = GetStringAny(turn, ["phone_tokens", "phoneTokens", "acoustic_units", "acousticUnits"], string.Empty),
            ["unitType"] = GetStringAny(turn, ["unit_type", "unitType"], string.Empty),
            ["confidence"] = CloneValueOrNull(GetNode(turn, "confidence")),
            ["avgLogprob"] = NumberOrNull(GetDoubleAny(turn, ["avg_logprob", "avgLogprob"], null)),
            ["noSpeechProbability"] = NumberOrNull(GetDoubleAny(turn, ["no_speech_probability", "noSpeechProbability"], null)),
            ["transcriptionSegmentIndex"] = NumberOrNull(GetDoubleAny(turn, ["transcription_segment_index", "transcriptionSegmentIndex"], null)),
        };
    }

    private static JsonObject NewAudioObservation(JsonObject? convertInfo, JsonObject? timeline, JsonArray turns)
    {
        var convertSource = GetObject(convertInfo, "source");
        var timelineSource = GetObject(timeline, "source");
        var source = convertSource ?? timelineSource;
        var convertPipeline = GetObject(convertInfo, "pipeline");
        var timelinePipeline = GetObject(timeline, "pipeline");
        var pipeline = convertPipeline ?? timelinePipeline;
        var counts = GetObject(convertInfo, "counts");
        var speechActivity = GetObject(pipeline, "speech_activity_detection");
        var speakerDiarization = GetObject(pipeline, "speaker_diarization");
        var transcription = GetObject(pipeline, "speech_transcription");
        var turnObjects = turns.OfType<JsonObject>().ToList();

        return new JsonObject
        {
            ["available"] = convertInfo is not null || timeline is not null,
            ["source"] = new JsonObject
            {
                ["fileName"] = GetStringAny(source, ["file_name", "fileName"], string.Empty),
                ["sourceHash"] = GetStringAny(source, ["source_hash", "sourceHash"], string.Empty),
                ["durationSec"] = NumberOrNull(GetDoubleAny(source, ["duration_sec", "durationSec", "duration_seconds", "durationSeconds"], null)),
                ["containerName"] = GetStringAny(source, ["container_name", "containerName"], string.Empty),
                ["extension"] = GetStringAny(source, ["extension"], string.Empty),
                ["audioCodec"] = GetStringAny(source, ["audio_codec", "audioCodec"], string.Empty),
                ["audioChannels"] = NumberOrNull(GetDoubleAny(source, ["audio_channels", "audioChannels"], null)),
                ["audioSampleRate"] = NumberOrNull(GetDoubleAny(source, ["audio_sample_rate", "audioSampleRate"], null)),
                ["bitrate"] = NumberOrNull(GetDoubleAny(source, ["bitrate"], null)),
                ["recordedAt"] = GetStringAny(source, ["recorded_at", "recordedAt"], string.Empty),
                ["recordedAtSource"] = GetStringAny(source, ["recorded_at_source", "recordedAtSource"], string.Empty),
                ["recordedAtTimezone"] = GetStringAny(source, ["recorded_at_timezone", "recordedAtTimezone"], string.Empty),
            },
            ["counts"] = new JsonObject
            {
                ["speechCandidateRanges"] = GetIntAny(counts, ["speech_candidate_ranges", "speechCandidateRanges"], 0),
                ["speakerTurns"] = GetIntAny(counts, ["speaker_turns", "speakerTurns"], GetIntAny(speakerDiarization, ["turn_count", "turnCount"], 0)),
                ["transcriptSegments"] = GetIntAny(counts, ["transcript_segments", "transcriptSegments"], turnObjects.Count),
                ["rawTranscriptSegments"] = GetIntAny(counts, ["raw_transcript_segments", "rawTranscriptSegments"], 0),
                ["rejectedTranscriptSegments"] = GetIntAny(counts, ["rejected_transcript_segments", "rejectedTranscriptSegments"], 0),
                ["timelineTurns"] = turnObjects.Count,
                ["speakerCount"] = turnObjects
                    .Select(turn => GetStringAny(turn, ["speaker"], string.Empty))
                    .Where(speaker => !string.IsNullOrWhiteSpace(speaker))
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
            },
            ["pipeline"] = new JsonObject
            {
                ["pipelineVersion"] = GetStringAny(pipeline, ["pipeline_version", "pipelineVersion"], string.Empty),
                ["generationSignature"] = GetStringAny(pipeline, ["generation_signature", "generationSignature"], string.Empty),
                ["computeMode"] = GetStringAny(pipeline, ["compute_mode", "computeMode"], string.Empty),
                ["speechActivityBackend"] = GetStringAny(speechActivity, ["backend"], string.Empty),
                ["speechActivityModelId"] = GetStringAny(speechActivity, ["model_id", "modelId"], string.Empty),
                ["speechActivityProfile"] = GetStringAny(speechActivity, ["profile"], string.Empty),
                ["speakerBackend"] = GetStringAny(speakerDiarization, ["backend", "speaker_backend", "speakerBackend"], GetStringAny(pipeline, ["speaker_backend", "speakerBackend"], string.Empty)),
                ["speakerModelId"] = GetStringAny(speakerDiarization, ["model_id", "modelId", "speaker_model_id", "speakerModelId"], GetStringAny(pipeline, ["speaker_model_id", "speakerModelId"], string.Empty)),
                ["speakerStatus"] = GetStringAny(speakerDiarization, ["status"], string.Empty),
                ["transcriptionBackend"] = GetStringAny(transcription, ["backend", "transcription_backend", "transcriptionBackend"], GetStringAny(pipeline, ["transcription_backend", "transcriptionBackend"], string.Empty)),
                ["transcriptionModelId"] = GetStringAny(transcription, ["model_id", "modelId", "transcription_model_id", "transcriptionModelId"], GetStringAny(pipeline, ["transcription_model_id", "transcriptionModelId"], string.Empty)),
                ["transcriptionLanguage"] = GetStringAny(transcription, ["language", "transcription_language", "transcriptionLanguage"], GetStringAny(pipeline, ["transcription_language", "transcriptionLanguage"], string.Empty)),
                ["transcriptionDevice"] = GetStringAny(transcription, ["device", "transcription_device", "transcriptionDevice"], GetStringAny(pipeline, ["transcription_device", "transcriptionDevice"], string.Empty)),
                ["transcriptionComputeType"] = GetStringAny(transcription, ["compute_type", "computeType", "transcription_compute_type", "transcriptionComputeType"], GetStringAny(pipeline, ["transcription_compute_type", "transcriptionComputeType"], string.Empty)),
                ["transcriptionStatus"] = GetStringAny(transcription, ["status"], string.Empty),
                ["transcriptionLanguageProbability"] = NumberOrNull(GetDoubleAny(transcription, ["language_probability", "languageProbability"], null)),
            },
            ["speakers"] = NewSpeakerDistribution(turnObjects),
            ["metrics"] = new JsonObject
            {
                ["avgLogprob"] = NewDoubleRange(turnObjects.Select(turn => GetDoubleAny(turn, ["avgLogprob", "avg_logprob"], null))),
                ["noSpeechProbability"] = NewDoubleRange(turnObjects.Select(turn => GetDoubleAny(turn, ["noSpeechProbability", "no_speech_probability"], null))),
                ["confidence"] = NewDoubleRange(turnObjects.Select(turn => GetDoubleAny(turn, ["confidence"], null))),
            },
        };
    }

    private static JsonArray NewSpeakerDistribution(List<JsonObject> turns)
    {
        var array = new JsonArray();
        foreach (var group in turns
            .GroupBy(turn => GetStringAny(turn, ["speaker"], string.Empty))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["speaker"] = string.IsNullOrWhiteSpace(group.Key) ? string.Empty : group.Key,
                ["turnCount"] = group.Count(),
            });
        }

        return array;
    }

    private static JsonObject NewDoubleRange(IEnumerable<double?> values)
    {
        var numbers = values.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        return new JsonObject
        {
            ["count"] = numbers.Count,
            ["min"] = numbers.Count > 0 ? JsonValue.Create(numbers.Min()) : null,
            ["max"] = numbers.Count > 0 ? JsonValue.Create(numbers.Max()) : null,
        };
    }

    private JsonObject NewAudioVerbalizationStatus(JsonObject fileRow)
    {
        var appSettings = _settings.ReadSettings();
        var language = ConvertTimelineText(appSettings.AudioVerbalization.Language);
        if (string.IsNullOrEmpty(language))
        {
            language = "ja-JP";
        }

        var model = ConvertTimelineText(appSettings.AudioVerbalization.Model);
        if (string.IsNullOrEmpty(model))
        {
            model = "qwen3.5:9b";
        }

        var audioItemId = GetString(fileRow, "itemId", string.Empty);
        var sourceFileIdentity = GetString(fileRow, "sourceFileIdentity", string.Empty);
        var totalTurns = GetInt(fileRow, "turnCount", 0);
        if (!GetBool(fileRow, "hasTimeline", false))
        {
            return NewAudioVerbalizationStatusObject(
                false,
                "unavailable",
                audioItemId,
                sourceFileIdentity,
                language,
                model,
                totalTurns,
                0,
                0,
                string.Empty,
                string.Empty,
                "Audio timeline was not available.");
        }

        if (string.IsNullOrEmpty(audioItemId))
        {
            return NewAudioVerbalizationStatusObject(
                false,
                "unavailable",
                string.Empty,
                sourceFileIdentity,
                language,
                model,
                totalTurns,
                0,
                0,
                string.Empty,
                string.Empty,
                "Audio item ID was not available.");
        }

        var directory = GetAudioVerbalizationDirectory(audioItemId);
        var planPath = Path.Combine(directory, "verbalization-plan.json");
        var resultPath = Path.Combine(directory, "audio-verbalization.json");
        if (!File.Exists(resultPath))
        {
            return NewAudioVerbalizationStatusObject(
                true,
                totalTurns > 0 ? "source_transcript" : "not_started",
                audioItemId,
                sourceFileIdentity,
                language,
                model,
                totalTurns,
                0,
                GetAudioVerbalizationPlanChunkCount(planPath),
                planPath,
                resultPath,
                totalTurns > 0 ? "Source transcript text is available for refinement." : string.Empty);
        }

        try
        {
            var payload = ReadJsonFile(resultPath);
            var status = GetObject(payload, "status");
            var resultTurns = GetArray(payload, "turns");
            var resultChunks = GetArray(payload, "chunks");
            var state = GetString(status, "state", "completed").ToLowerInvariant();
            var visibleResultTurns = resultTurns
                .OfType<JsonObject>()
                .Where(turn => !TimelineTranscriptNoiseFilter.IsLikelySilentHallucination(turn))
                .ToList();
            var verbalizedTurns = visibleResultTurns.Count(IsAudioVerbalizationResolvedTurn);
            var plannedTurnCount = GetAudioVerbalizationPlanTurnCount(planPath);
            var displayTotalTurns = plannedTurnCount > 0
                ? plannedTurnCount
                : GetInt(status, "totalTurns", totalTurns);
            var unresolvedTurns = visibleResultTurns.Count - verbalizedTurns;
            unresolvedTurns += Math.Max(0, displayTotalTurns - visibleResultTurns.Count);
            if (state == "completed" && unresolvedTurns > 0)
            {
                state = "needs_review";
            }
            var jobId = GetString(status, "jobId", string.Empty);
            var updatedAt = GetString(status, "updatedAt", string.Empty);
            var activeJob = _audioVerbalizationJobs.IsActive(jobId);
            var lastActivitySec = GetElapsedSinceSec(updatedAt);
            var message = GetString(status, "message", state == "needs_review" ? "Audio verbalization has unresolved turns." : string.Empty);
            if (IsActiveVerbalizationState(state) && !activeJob)
            {
                state = "stalled";
                message = "Audio verbalization appears stopped. The worker is not active for this job.";
            }

            return NewAudioVerbalizationStatusObject(
                true,
                state,
                audioItemId,
                sourceFileIdentity,
                GetString(status, "language", language),
                GetString(status, "model", model),
                displayTotalTurns,
                verbalizedTurns,
                Math.Max(GetAudioVerbalizationPlanChunkCount(planPath), resultChunks.Count),
                planPath,
                resultPath,
                message,
                unresolvedTurns,
                jobId,
                GetString(status, "currentChunkId", string.Empty),
                GetString(status, "startedAt", string.Empty),
                GetElapsedSinceSec(GetString(status, "startedAt", string.Empty)) ?? GetDouble(status, "elapsedSec", 0),
                GetDouble(status, "estimatedRemainingSec", 0),
                updatedAt,
                completedChunks: resultChunks.Count,
                activeJob: activeJob,
                lastActivitySec: lastActivitySec ?? 0);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return NewAudioVerbalizationStatusObject(
                true,
                "unreadable",
                audioItemId,
                sourceFileIdentity,
                language,
                model,
                totalTurns,
                0,
                0,
                planPath,
                resultPath,
                ex.Message);
        }
    }

    private static JsonObject NewAudioVerbalizationStatusObject(
        bool available,
        string state,
        string audioItemId,
        string sourceFileIdentity,
        string language,
        string model,
        int totalTurns,
        int verbalizedTurns,
        int totalChunks,
        string planPath,
        string resultPath,
        string message,
        int unresolvedTurns = 0,
        string jobId = "",
        string currentChunkId = "",
        string startedAt = "",
        double elapsedSec = 0,
        double estimatedRemainingSec = 0,
        string updatedAt = "",
        int completedChunks = 0,
        bool activeJob = false,
        double lastActivitySec = 0)
    {
        var progressPercent = totalChunks > 0
            ? Math.Min(100, Math.Max(0, completedChunks / (double)totalChunks * 100))
            : totalTurns > 0
                ? Math.Min(100, Math.Max(0, verbalizedTurns / (double)totalTurns * 100))
                : 0;

        return new JsonObject
        {
            ["available"] = available,
            ["state"] = state,
            ["audioItemId"] = audioItemId,
            ["sourceFileIdentity"] = sourceFileIdentity,
            ["language"] = language,
            ["model"] = model,
            ["signature"] = string.Empty,
            ["expectedSignature"] = string.Empty,
            ["summarySignature"] = string.Empty,
            ["expectedSummarySignature"] = string.Empty,
            ["signatureState"] = state,
            ["promptVersion"] = string.Empty,
            ["totalTurns"] = totalTurns,
            ["verbalizedTurns"] = verbalizedTurns,
            ["unresolvedTurns"] = unresolvedTurns,
            ["totalChunks"] = totalChunks,
            ["completedChunks"] = completedChunks,
            ["jobId"] = jobId,
            ["currentChunkId"] = currentChunkId,
            ["planPath"] = planPath,
            ["resultPath"] = resultPath,
            ["startedAt"] = startedAt,
            ["elapsedSec"] = elapsedSec,
            ["estimatedRemainingSec"] = estimatedRemainingSec,
            ["lastActivitySec"] = lastActivitySec,
            ["progressPercent"] = progressPercent,
            ["activeJob"] = activeJob,
            ["updatedAt"] = updatedAt,
            ["message"] = message,
        };
    }

    private static JsonObject GetAudioVerbalizationResultFromStatus(JsonObject status)
    {
        var resultPath = GetString(status, "resultPath", string.Empty);
        if (string.IsNullOrEmpty(resultPath) || !File.Exists(resultPath))
        {
            return new JsonObject
            {
                ["available"] = GetBool(status, "available", false),
                ["status"] = status.DeepClone(),
                ["turns"] = new JsonArray(),
                ["chunks"] = new JsonArray(),
                ["message"] = "Audio verbalization result was not found.",
            };
        }

        var payload = ReadJsonFile(resultPath);
        if (payload is null)
        {
            return new JsonObject
            {
                ["available"] = false,
                ["status"] = status.DeepClone(),
                ["turns"] = new JsonArray(),
                ["chunks"] = new JsonArray(),
                ["message"] = "Audio verbalization result could not be read.",
            };
        }

        return new JsonObject
        {
            ["available"] = true,
            ["status"] = status.DeepClone(),
            ["turns"] = CloneTranscriptTurnsWithoutSilentHallucinations(payload),
            ["chunks"] = CloneArray(payload, "chunks"),
            ["message"] = string.Empty,
        };
    }

    private static JsonArray CloneTranscriptTurnsWithoutSilentHallucinations(JsonObject payload)
    {
        var turns = new JsonArray();
        foreach (var node in GetArray(payload, "turns"))
        {
            if (node is not JsonObject turn || TimelineTranscriptNoiseFilter.IsLikelySilentHallucination(turn))
            {
                continue;
            }

            turns.Add(turn.DeepClone());
        }

        return turns;
    }

    private static bool IsAudioVerbalizationResolvedTurn(JsonObject turn)
    {
        var status = GetString(turn, "status", string.Empty).ToLowerInvariant();
        var text = ConvertTimelineText(GetString(turn, "text", string.Empty));
        return status != "unresolved" && !string.IsNullOrEmpty(text);
    }

    private static JsonObject NewSourceTranscriptResultFromDetail(JsonObject detail, JsonObject status)
    {
        var resultTurns = new JsonArray();
        var sequence = 0;
        foreach (var turnNode in GetArray(detail, "turns"))
        {
            if (turnNode is not JsonObject turn)
            {
                sequence++;
                continue;
            }

            var index = GetInt(turn, "index", sequence + 1);
            if (TimelineTranscriptNoiseFilter.IsLikelySilentHallucination(turn))
            {
                sequence++;
                continue;
            }

            var text = GetReadableTranscriptText(turn);
            resultTurns.Add(new JsonObject
            {
                ["turnId"] = "source-transcript:" + index.ToString(CultureInfo.InvariantCulture),
                ["index"] = index,
                ["startSec"] = GetDouble(turn, "startSec", 0),
                ["endSec"] = GetDouble(turn, "endSec", 0),
                ["speaker"] = GetString(turn, "speaker", string.Empty),
                ["text"] = text,
                ["confidence"] = CloneValueOrNull(GetNode(turn, "confidence")),
                ["status"] = string.IsNullOrEmpty(text) ? "unresolved" : "source_transcript",
                ["basis"] = new JsonArray("source_transcript"),
                ["uncertainTerms"] = new JsonArray(),
            });
            sequence++;
        }

        return new JsonObject
        {
            ["available"] = true,
            ["status"] = status.DeepClone(),
            ["turns"] = resultTurns,
            ["chunks"] = new JsonArray(),
            ["message"] = string.Empty,
        };
    }

    private static bool DetailHasReadableTranscript(JsonObject detail)
    {
        foreach (var turnNode in GetArray(detail, "turns"))
        {
            if (turnNode is JsonObject turn && !string.IsNullOrEmpty(GetReadableTranscriptText(turn)))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetReadableTranscriptText(JsonObject turn)
        => TimelineTranscriptNoiseFilter.IsLikelySilentHallucination(turn)
            ? string.Empty
            : GetStringAny(turn, ["text", "transcriptText", "readableText"], string.Empty);

    private JsonObject GetAudioVerbalizationFileSummary(AudioGeneratedCatalog catalog)
    {
        var summary = new JsonObject
        {
            ["targetFileCount"] = 0,
            ["verbalizedFileCount"] = 0,
        };

        foreach (var row in catalog.Rows)
        {
            if (string.IsNullOrEmpty(row.SourceFileIdentity)
                || string.IsNullOrEmpty(row.ItemId)
                || row.TurnCount <= 0)
            {
                continue;
            }

            summary["targetFileCount"] = GetInt(summary, "targetFileCount", 0) + 1;
            var status = NewAudioVerbalizationStatus(new JsonObject
            {
                ["itemId"] = row.ItemId,
                ["sourceFileIdentity"] = row.SourceFileIdentity,
                ["status"] = "completed",
                ["hasTimeline"] = true,
                ["turnCount"] = row.TurnCount,
            });
            if (GetString(status, "state", string.Empty).Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                summary["verbalizedFileCount"] = GetInt(summary, "verbalizedFileCount", 0) + 1;
            }
        }

        return summary;
    }

    private JsonObject? GetActiveRun(AudioSettingsSnapshot settings)
    {
        var outputRoot = GetOutputRootPath(settings);
        if (string.IsNullOrEmpty(outputRoot) || !Directory.Exists(outputRoot))
        {
            return null;
        }

        var rows = new List<(string State, DateTime SortDate, JsonObject Payload)>();
        foreach (var runDirectory in SafeEnumerateDirectories(outputRoot, "run-*"))
        {
            var statusPath = Path.Combine(runDirectory, "status.json");
            var status = ReadJsonFile(statusPath);
            if (status is null)
            {
                continue;
            }

            var state = GetString(status, "state", string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(state))
            {
                continue;
            }

            var itemsTotal = GetIntAny(status, ["items_total", "itemsTotal"], 0);
            var itemsDone = GetIntAny(status, ["items_done", "itemsDone"], 0);
            var progress = GetDoubleAny(status, ["progress_percent", "progressPercent"], null);
            if (progress is null && itemsTotal > 0)
            {
                progress = Math.Round(itemsDone / (double)itemsTotal * 100, 1);
            }

            var payload = new JsonObject
            {
                ["runId"] = GetStringAny(status, ["run_id", "runId"], Path.GetFileName(runDirectory)),
                ["state"] = state,
                ["currentStage"] = GetStringAny(status, ["current_stage", "currentStage"], string.Empty),
                ["message"] = GetString(status, "message", string.Empty),
                ["itemsTotal"] = itemsTotal,
                ["itemsDone"] = itemsDone,
                ["itemsSkipped"] = GetIntAny(status, ["items_skipped", "itemsSkipped"], 0),
                ["itemsFailed"] = GetIntAny(status, ["items_failed", "itemsFailed"], 0),
                ["progressPercent"] = Math.Max(0, Math.Min(100, progress ?? 0)),
                ["processedDurationSec"] = GetDoubleAny(status, ["processed_duration_sec", "processedDurationSec"], 0),
                ["totalDurationSec"] = GetDoubleAny(status, ["total_duration_sec", "totalDurationSec"], 0),
                ["estimatedRemainingSec"] = GetDoubleAny(status, ["estimated_remaining_sec", "estimatedRemainingSec"], 0),
                ["currentItem"] = GetStringAny(status, ["current_item", "currentItem"], string.Empty),
                ["updatedAt"] = GetStringAny(status, ["updated_at", "updatedAt"], string.Empty),
            };

            var sortDate = File.GetLastWriteTimeUtc(statusPath);
            rows.Add((state, sortDate, payload));
        }

        foreach (var state in new[] { "running", "processing", "pending", "queued" })
        {
            var match = rows
                .Where(row => row.State.Equals(state, StringComparison.Ordinal))
                .OrderByDescending(row => row.SortDate)
                .FirstOrDefault();
            if (match.Payload is not null)
            {
                return match.Payload;
            }
        }

        return null;
    }

    private static string GetWorkerState(JsonObject? activeRun)
    {
        var state = GetString(activeRun, "state", string.Empty).ToLowerInvariant();
        return state switch
        {
            "running" or "processing" => "processing",
            "pending" or "queued" => "starting",
            _ => "unknown",
        };
    }

    private JsonObject GetHardwareDevices()
    {
        if (_hardwareCache is not null)
        {
            return (JsonObject)_hardwareCache.DeepClone();
        }

        var payload = new JsonObject
        {
            ["cpuDevices"] = NewStringArray(GetHardwareDeviceNames("Win32_Processor")),
            ["gpuDevices"] = NewStringArray(GetHardwareDeviceNames("Win32_VideoController")),
        };
        _hardwareCache = (JsonObject)payload.DeepClone();
        return payload;
    }

    private static List<string> GetHardwareDeviceNames(string className)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-CimInstance -ClassName "
                    + className
                    + " | ForEach-Object { ([string]$_.Name).Trim() } | Where-Object { $_ } | Select-Object -Unique\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return [];
            }

            var stdout = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10000) || process.ExitCode != 0)
            {
                return [];
            }

            return stdout
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(ConvertTimelineText)
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string GetOutputRootPath(AudioSettingsSnapshot settings)
    {
        var path = settings.OutputRoot?.Path ?? string.Empty;
        return string.IsNullOrEmpty(path) ? string.Empty : ConvertAudioLocalPath(path);
    }

    private static void AddCatalogRow(AudioGeneratedCatalog catalog, AudioCatalogRow row)
    {
        catalog.Rows.Add(row);
        if (!string.IsNullOrEmpty(row.SourceFileIdentity))
        {
            catalog.ByIdentity[row.SourceFileIdentity] = row;
        }

        if (!string.IsNullOrEmpty(row.RelativePath))
        {
            var relativeSizeKey = NewRelativeSizeKey(row.RelativePath, row.SizeBytes);
            if (!catalog.ByRelativeSize.TryGetValue(relativeSizeKey, out var rows))
            {
                rows = [];
                catalog.ByRelativeSize[relativeSizeKey] = rows;
            }

            rows.Add(row);
        }
    }

    private static AudioCatalogRow? FindCatalogRow(AudioGeneratedCatalog catalog, AudioSourceRow sourceRow)
    {
        if (catalog.ByIdentity.TryGetValue(sourceRow.SourceFileIdentity, out var identityMatch))
        {
            return identityMatch;
        }

        var relativeSizeKey = NewRelativeSizeKey(sourceRow.RelativePath, sourceRow.SizeBytes);
        return catalog.ByRelativeSize.TryGetValue(relativeSizeKey, out var rows) && rows.Count > 0
            ? rows[0]
            : null;
    }

    private static string NewRelativeSizeKey(string relativePath, long sizeBytes)
        => ConvertTimelineText(relativePath).Replace('\\', '/')
            + "|"
            + sizeBytes.ToString(CultureInfo.InvariantCulture);

    private static string GetNormalizedPathKey(string path)
        => ConvertTimelineText(path).TrimEnd('\\', '/').Replace('/', '\\').ToLowerInvariant();

    private static JsonObject GetAudioArtifactSummary(string mediaDirectory, string timelinePath)
    {
        var summary = new JsonObject
        {
            ["hasTimeline"] = false,
            ["hasAudio"] = false,
            ["turnCount"] = 0,
            ["speakerCount"] = 0,
        };

        if (string.IsNullOrEmpty(mediaDirectory))
        {
            return summary;
        }

        summary["hasAudio"] = File.Exists(Path.Combine(mediaDirectory, "source", "audio-normalized.wav"));
        if (string.IsNullOrEmpty(timelinePath) || !File.Exists(timelinePath))
        {
            return summary;
        }

        summary["hasTimeline"] = true;
        string raw;
        try
        {
            raw = File.ReadAllText(timelinePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return summary;
        }

        try
        {
            if (JsonNode.Parse(raw) is JsonObject timeline)
            {
                var turns = GetArray(timeline, "turns").OfType<JsonObject>().ToList();
                var turnCount = GetIntAny(timeline, ["turn_count", "turnCount"], turns.Count);
                summary["turnCount"] = turnCount > 0 ? turnCount : turns.Count;

                var speakers = new HashSet<string>(StringComparer.Ordinal);
                foreach (var turn in turns)
                {
                    var speaker = GetStringAny(turn, ["speaker", "speaker_label", "speakerLabel"], string.Empty);
                    if (!string.IsNullOrEmpty(speaker))
                    {
                        speakers.Add(speaker);
                    }
                }

                summary["speakerCount"] = speakers.Count;
                return summary;
            }
        }
        catch (JsonException)
        {
        }

        var turnMatch = Regex.Match(raw, "\"turn_count\"\\s*:\\s*(\\d+)");
        if (turnMatch.Success && int.TryParse(turnMatch.Groups[1].Value, out var regexTurnCount))
        {
            summary["turnCount"] = regexTurnCount;
        }

        var regexSpeakers = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(raw, "\"speaker\"\\s*:\\s*\"([^\"]+)\""))
        {
            regexSpeakers.Add(match.Groups[1].Value);
        }

        summary["speakerCount"] = regexSpeakers.Count;
        return summary;
    }

    private static string ResolveTimelinePath(string mediaDirectory)
    {
        if (string.IsNullOrEmpty(mediaDirectory))
        {
            return string.Empty;
        }

        var nested = Path.Combine(mediaDirectory, "timeline", "speaker-acoustic-units-timeline.json");
        if (File.Exists(nested))
        {
            return nested;
        }

        var direct = Path.Combine(mediaDirectory, "timeline.json");
        return File.Exists(direct) ? direct : string.Empty;
    }

    private static string GetMediaDirectory(string outputRoot, JsonObject row, string mediaId)
    {
        if (string.IsNullOrEmpty(outputRoot) || string.IsNullOrEmpty(mediaId))
        {
            return string.Empty;
        }

        var directMediaDirectory = Path.Combine(outputRoot, mediaId);
        if (Directory.Exists(directMediaDirectory))
        {
            return directMediaDirectory;
        }

        var runId = GetStringAny(row, ["run_id", "runId"], string.Empty);
        if (string.IsNullOrEmpty(runId))
        {
            return string.Empty;
        }

        return Path.Combine(outputRoot, runId, "media", mediaId);
    }

    private string GetAudioVerbalizationDirectory(string audioItemId)
    {
        var path = Path.Combine(_settings.GetStoreDirectory(), "audio-verbalizations", GetZipSafeSegment(audioItemId));
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private static int GetAudioVerbalizationPlanChunkCount(string planPath)
    {
        var plan = ReadJsonFile(planPath);
        return GetArray(plan, "chunks").Count;
    }

    private static int GetAudioVerbalizationPlanTurnCount(string planPath)
    {
        var plan = ReadJsonFile(planPath);
        var total = 0;
        foreach (var chunk in GetArray(plan, "chunks").OfType<JsonObject>())
        {
            var turns = GetArray(chunk, "turns").OfType<JsonObject>().ToList();
            total += turns.Count > 0
                ? turns.Count(turn => !TimelineTranscriptNoiseFilter.IsLikelySilentHallucination(turn))
                : GetInt(chunk, "turnCount", 0);
        }

        return total;
    }

    private static bool IsActiveVerbalizationState(string state)
        => state is "running" or "queued" or "planned" or "starting";

    private static double? GetElapsedSinceSec(string value)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return null;
        }

        return Math.Max(0, (DateTimeOffset.Now - parsed).TotalSeconds);
    }

    private JsonArray ConvertRootArray(IEnumerable<AudioRootRow> roots)
    {
        var array = new JsonArray();
        foreach (var root in roots)
        {
            array.Add(ConvertRoot(root));
        }

        return array;
    }

    private JsonObject ConvertRoot(AudioRootRow root)
        => new()
        {
            ["id"] = root.Id,
            ["displayName"] = root.DisplayName,
            ["path"] = root.Path,
            ["enabled"] = root.Enabled,
        };

    private AudioRootRow ConvertRootNode(JsonNode? node, string fallbackId)
    {
        if (node is JsonObject root)
        {
            var path = GetString(root, "path", string.Empty);
            var id = GetString(root, "id", fallbackId);
            if (string.IsNullOrEmpty(id))
            {
                id = fallbackId;
            }

            var displayName = GetString(root, "displayName", id);
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = id;
            }

            return new AudioRootRow(
                id,
                displayName,
                path,
                GetBool(root, "enabled", true));
        }

        var pathText = ConvertNodeToString(node);
        var idText = string.IsNullOrEmpty(fallbackId) ? pathText : fallbackId;
        var displayNameText = string.IsNullOrEmpty(pathText)
            ? idText
            : Path.GetFileName(pathText.TrimEnd('\\', '/'));
        return new AudioRootRow(idText, displayNameText, pathText, true);
    }

    private HashSet<string> NormalizeExtensions(IEnumerable<string> extensions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in extensions)
        {
            var text = ConvertTimelineText(extension).ToLowerInvariant();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            set.Add(text.StartsWith(".", StringComparison.Ordinal) ? text : "." + text);
        }

        if (set.Count == 0)
        {
            foreach (var extension in DefaultAudioExtensions)
            {
                set.Add(extension);
            }
        }

        return set;
    }

    private string GetAudioSettingsFilePath()
    {
        var productPath = GetProductPath();
        var settingsPath = Path.Combine(productPath, "settings.json");
        if (File.Exists(settingsPath))
        {
            return settingsPath;
        }

        return Path.Combine(productPath, "settings.example.json");
    }

    private string ConvertAudioLocalPath(string? path)
    {
        var text = ConvertTimelineText(path);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var productPath = GetProductPath();
        if (text.Equals("/workspace", StringComparison.OrdinalIgnoreCase))
        {
            return productPath;
        }
        if (text.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(productPath, text["/workspace/".Length..].Replace("/", "\\"));
        }
        if (text.StartsWith("/mnt/c/", StringComparison.OrdinalIgnoreCase))
        {
            return "C:\\" + text[7..].Replace("/", "\\");
        }

        var converted = TimelinePathConverter.ConvertTimelineWindowsPath(text, _options);
        if (!string.IsNullOrEmpty(converted))
        {
            text = converted;
        }

        return Path.IsPathRooted(text) ? text : Path.Combine(productPath, text);
    }

    private string GetProductPath()
    {
        foreach (var product in _settings.ReadSettings().ProductRegistry.Products)
        {
            if (product.Id.Equals("audio", StringComparison.OrdinalIgnoreCase))
            {
                return product.Path ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string GetRelativePath(string rootPath, string filePath)
    {
        try
        {
            return Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            var normalizedRoot = rootPath.TrimEnd('\\', '/');
            if (filePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return filePath[normalizedRoot.Length..].TrimStart('\\', '/').Replace('\\', '/');
            }

            return Path.GetFileName(filePath);
        }
    }

    private static string GetDirectoryFromRelativePath(string relativePath)
    {
        var normalized = ConvertTimelineText(relativePath).Replace('/', '\\');
        var lastSeparator = normalized.LastIndexOf('\\');
        return lastSeparator > 0 ? normalized[..lastSeparator] : string.Empty;
    }

    private static string GetFullPathOrOriginal(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path, string searchPattern)
    {
        try
        {
            return Directory.EnumerateDirectories(path, searchPattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static JsonObject NewPagination(
        int page,
        int pageSize,
        int totalItems,
        int returnedItems)
    {
        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Max(1, pageSize);
        var totalPages = totalItems > 0
            ? (int)Math.Ceiling(totalItems / (double)effectivePageSize)
            : 0;
        var offset = (effectivePage - 1) * effectivePageSize;
        return new JsonObject
        {
            ["mode"] = "page",
            ["page"] = effectivePage,
            ["pageSize"] = effectivePageSize,
            ["totalItems"] = totalItems,
            ["totalPages"] = totalPages,
            ["returnedItems"] = returnedItems,
            ["offset"] = offset,
            ["rangeStart"] = returnedItems > 0 ? offset + 1 : 0,
            ["rangeEnd"] = returnedItems > 0 ? offset + returnedItems : 0,
            ["hasPrevious"] = effectivePage > 1 && totalItems > 0,
            ["hasNext"] = effectivePage < totalPages,
        };
    }

    private static JsonArray CloneArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array ? (JsonArray)array.DeepClone() : new JsonArray();
    }

    private static JsonArray NewStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonObject? ReadJsonFile(string path)
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

    private static JsonNode? CloneValueOrNull(JsonNode? node)
        => node?.DeepClone();

    private static JsonNode? NumberOrNull(double? value)
        => value.HasValue ? JsonValue.Create(value.Value) : null;

    private static JsonObject? GetObject(JsonObject? source, string name)
        => GetNode(source, name) as JsonObject;

    private static JsonNode? GetNode(JsonObject? source, string name)
        => TryGetNode(source, name, out var node) ? node : null;

    private static bool TryGetNode(JsonObject? source, string name, out JsonNode? node)
    {
        node = null;
        if (source is null)
        {
            return false;
        }

        foreach (var property in source)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                node = property.Value;
                return true;
            }
        }

        return false;
    }

    private static List<JsonNode?> GetArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array ? array.ToList() : [];
    }

    private static IEnumerable<string> GetStringArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array
            ? array.Select(ConvertNodeToString).Where(value => !string.IsNullOrEmpty(value))
            : [];
    }

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertNodeToString(node);
    }

    private static string GetStringAny(JsonObject? source, string[] names, string fallback)
    {
        foreach (var name in names)
        {
            if (TryGetNode(source, name, out var node))
            {
                return ConvertNodeToString(node);
            }
        }

        return fallback;
    }

    private static int GetInt(JsonObject? source, string name, int fallback)
    {
        var node = GetNode(source, name);
        return GetIntValue(node, fallback);
    }

    private static int GetIntAny(JsonObject? source, string[] names, int fallback)
    {
        foreach (var name in names)
        {
            if (TryGetNode(source, name, out var node))
            {
                return GetIntValue(node, fallback);
            }
        }

        return fallback;
    }

    private static long GetLongAny(JsonObject? source, string[] names, long fallback)
    {
        foreach (var name in names)
        {
            if (TryGetNode(source, name, out var node))
            {
                return GetLongValue(node, fallback);
            }
        }

        return fallback;
    }

    private static double GetDouble(JsonObject? source, string name, double fallback)
    {
        var node = GetNode(source, name);
        return GetDoubleValue(node, fallback) ?? fallback;
    }

    private static double? GetDoubleAny(JsonObject? source, string[] names, double? fallback)
    {
        foreach (var name in names)
        {
            if (TryGetNode(source, name, out var node))
            {
                return GetDoubleValue(node, fallback);
            }
        }

        return fallback;
    }

    private static bool GetBool(JsonObject? source, string name, bool fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        if (node.GetValueKind() == JsonValueKind.True)
        {
            return true;
        }
        if (node.GetValueKind() == JsonValueKind.False)
        {
            return false;
        }

        return ConvertNodeToString(node).ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static int GetIntValue(JsonNode? node, int fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<int>();
            }

            return int.TryParse(ConvertNodeToString(node), out var parsed) ? parsed : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    private static long GetLongValue(JsonNode? node, long fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<long>();
            }

            return long.TryParse(ConvertNodeToString(node), out var parsed) ? parsed : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    private static long GetLong(JsonObject? source, string name, long fallback)
    {
        var node = GetNode(source, name);
        return GetLongValue(node, fallback);
    }

    private static double? GetDoubleValue(JsonNode? node, double? fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<double>();
            }

            return double.TryParse(
                ConvertNodeToString(node),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
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
            return string.Empty;
        }
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

    private static string GetTokenPreview(string token)
    {
        var value = ConvertTimelineText(token);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        const char bullet = '\u2022';
        if (value.Length <= 8)
        {
            return new string(bullet, value.Length);
        }

        return value[..4] + new string(bullet, Math.Max(4, value.Length - 8)) + value[^4..];
    }

    private static string GetZipSafeSegment(object? value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return "item";
        }

        var safe = Regex.Replace(text, "[^A-Za-z0-9._-]+", "_").Trim('.', '_', '-');
        if (string.IsNullOrEmpty(safe))
        {
            return "item";
        }

        return safe.Length > 120 ? safe[..120] : safe;
    }

    private sealed record AudioSettingsSnapshot(
        List<AudioRootRow> InputRoots,
        AudioRootRow? OutputRoot,
        List<AudioRootRow> OutputRoots,
        HashSet<string> AudioExtensions,
        string HuggingFaceToken,
        string ComputeMode);

    private sealed record AudioRootRow(
        string Id,
        string DisplayName,
        string Path,
        bool Enabled);

    private sealed record AudioSourceRow(
        string SourceId,
        string SourceFileIdentity,
        string SourceDisplayName,
        string SourceName,
        string RootPath,
        string DisplayPath,
        string RelativePath,
        string Directory,
        string FileName,
        long SizeBytes,
        string ModifiedAt);

    private sealed record ResolvedAudioSource(
        AudioRootRow Root,
        string ResolvedRootPath,
        AudioSourceRow Row);

    private sealed record AudioCatalogRow(
        string SourceFileIdentity,
        string ItemId,
        string MediaId,
        string RunId,
        string RelativePath,
        long SizeBytes,
        string MediaDirectory,
        string TimelinePath,
        string ConvertInfoPath,
        double? DurationSec,
        bool HasTimeline,
        bool HasAudio,
        int TurnCount,
        int BulkTurnCount,
        int SpeakerCount);

    private sealed class AudioGeneratedCatalog
    {
        public List<AudioCatalogRow> Rows { get; } = [];

        public Dictionary<string, AudioCatalogRow> ByIdentity { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<AudioCatalogRow>> ByRelativeSize { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
