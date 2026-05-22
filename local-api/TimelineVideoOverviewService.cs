using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class TimelineVideoOverviewService
{
    private static readonly string[] DefaultVideoExtensions =
    [
        ".avi",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp4",
        ".webm",
        ".wmv",
    ];
    private static readonly HashSet<string> KnownSilenceHallucinationPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        NormalizeTranscriptText("ご視聴ありがとうございました"),
        NormalizeTranscriptText("ご視聴ありがとうございます"),
        NormalizeTranscriptText("ありがとうございました"),
        NormalizeTranscriptText("Thank you for watching"),
        NormalizeTranscriptText("Thanks for watching"),
    };
    private const double MinimumTranscriptSpeechOverlapSec = 0.1;
    private const double HighNoSpeechProbability = 0.6;
    private const double LowTranscriptConfidence = -0.6;

    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineLocalApiOptions _options;
    private JsonObject? _hardwareCache;

    public TimelineVideoOverviewService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        TimelineLocalApiOptions options)
    {
        _settings = settings;
        _operations = operations;
        _options = options;
    }

    public JsonObject GetOverview()
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForVideo",
            "video_overview",
            "started",
            "Web operation started.");

        try
        {
            var result = GetOverviewCore();
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForVideo",
                "video_overview",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["productFound"] = GetBool(result, "productFound", false),
                    ["settingsValid"] = GetBool(result, "settingsValid", false),
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
                "TimelineForVideo",
                "video_overview",
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
            "TimelineForVideo",
            "video_files_list",
            "started",
            "Web operation started.");

        try
        {
            var result = GetFilesCore(page, pageSize);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForVideo",
                "video_files_list",
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
                "TimelineForVideo",
                "video_files_list",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    public JsonObject GetFileDetail(string? sourcePath)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForVideo",
            "video_file_detail",
            "started",
            "Web operation started.");

        try
        {
            var result = GetFileDetailCore(sourcePath);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForVideo",
                "video_file_detail",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["available"] = GetBool(result, "available", false),
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
                "TimelineForVideo",
                "video_file_detail",
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
        if (!productFound)
        {
            return new JsonObject
            {
                ["productFound"] = false,
                ["productPath"] = productPath,
                ["settingsValid"] = false,
                ["settings"] = new JsonObject(),
                ["sourceFileCount"] = 0,
                ["itemCount"] = 0,
                ["audioVerbalizationTargetFileCount"] = 0,
                ["audioVerbalizedFileCount"] = 0,
                ["message"] = "TimelineForVideo was not found.",
            };
        }

        try
        {
            var settingsPayload = ReadVideoSettingsPayload();
            var outputPath = GetStringAny(settingsPayload, ["outputRoot", "output_root"], string.Empty);
            var outputLocalPath = ConvertVideoLocalPath(outputPath);
            var hardware = GetHardwareDevices();
            var verbalizationSummary = GetAudioVerbalizationFileSummary();
            return new JsonObject
            {
                ["productFound"] = true,
                ["productPath"] = productPath,
                ["settingsValid"] = true,
                ["settings"] = ConvertSettingsFile(settingsPayload),
                ["sourceFileCount"] = GetSourceFileCount(settingsPayload),
                ["itemCount"] = GetGeneratedItemCount(outputLocalPath),
                ["audioVerbalizationTargetFileCount"] = GetInt(verbalizationSummary, "targetFileCount", 0),
                ["audioVerbalizedFileCount"] = GetInt(verbalizationSummary, "verbalizedFileCount", 0),
                ["cpuDevices"] = CloneArray(hardware, "cpuDevices"),
                ["gpuDevices"] = CloneArray(hardware, "gpuDevices"),
                ["message"] = string.Empty,
            };
        }
        catch (Exception ex)
        {
            var settingsPayload = ReadVideoSettingsPayload();
            return new JsonObject
            {
                ["productFound"] = true,
                ["productPath"] = productPath,
                ["settingsValid"] = false,
                ["settings"] = ConvertSettingsFile(settingsPayload),
                ["sourceFileCount"] = 0,
                ["itemCount"] = 0,
                ["audioVerbalizationTargetFileCount"] = 0,
                ["audioVerbalizedFileCount"] = 0,
                ["cpuDevices"] = new JsonArray(),
                ["gpuDevices"] = new JsonArray(),
                ["message"] = ex.Message,
            };
        }
    }

    private JsonObject GetFilesCore(int page, int pageSize)
    {
        var settingsPayload = ReadVideoSettingsPayload();
        var outputRoot = ConvertVideoLocalPath(GetStringAny(
            settingsPayload,
            ["outputRoot", "output_root"],
            GetManagedVideoDataDirectory()));
        var catalog = GetGeneratedCatalog(outputRoot);
        var sourceRows = GetSourceRowsFromSettings(settingsPayload);

        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Max(1, pageSize);
        var offset = (effectivePage - 1) * effectivePageSize;
        var files = new JsonArray();
        foreach (var row in sourceRows.Skip(offset).Take(effectivePageSize))
        {
            files.Add(ConvertSourceFileRow(row, catalog));
        }

        return new JsonObject
        {
            ["total"] = sourceRows.Count,
            ["pagination"] = NewPagination(effectivePage, effectivePageSize, sourceRows.Count, files.Count),
            ["files"] = files,
        };
    }

    private JsonObject GetFileDetailCore(string? sourcePath)
    {
        var settingsPayload = ReadVideoSettingsPayload();
        var sourceRow = ResolveSourceFile(settingsPayload, sourcePath);
        if (sourceRow is null)
        {
            return new JsonObject
            {
                ["available"] = false,
                ["message"] = "Video source file was not found.",
                ["file"] = null,
                ["videoAvailable"] = false,
                ["timelineAvailable"] = false,
                ["artifacts"] = NewVideoArtifacts(null, null),
                ["activity"] = NewVideoActivity(null),
                ["frames"] = new JsonArray(),
                ["turns"] = new JsonArray(),
                ["audioVerbalization"] = NewAudioVerbalizationStatus(null),
                ["audioVerbalizationResult"] = NewAudioVerbalizationResult(
                    NewAudioVerbalizationStatus(null),
                    new JsonArray()),
            };
        }

        var outputRoot = ConvertVideoLocalPath(GetStringAny(
            settingsPayload,
            ["outputRoot", "output_root"],
            GetManagedVideoDataDirectory()));
        var catalog = GetGeneratedCatalog(outputRoot);
        var catalogRow = FindGeneratedCatalogRow(catalog, sourceRow);
        var file = ConvertSourceFileRow(sourceRow, catalog);
        var turns = GetTranscriptTurns(catalogRow);
        var audioVerbalization = NewAudioVerbalizationStatus(catalogRow);
        ApplyTranscriptTurnCounts(audioVerbalization, turns.Count);
        var videoRecord = !string.IsNullOrEmpty(catalogRow?.VideoRecordPath)
            ? ReadVideoJsonFile(catalogRow.VideoRecordPath)
            : null;

        return new JsonObject
        {
            ["available"] = true,
            ["message"] = string.Empty,
            ["file"] = file,
            ["videoAvailable"] = true,
            ["timelineAvailable"] = catalogRow is not null
                && !string.IsNullOrEmpty(catalogRow.TimelinePath)
                && File.Exists(catalogRow.TimelinePath),
            ["artifacts"] = NewVideoArtifacts(catalogRow, videoRecord),
            ["activity"] = NewVideoActivity(videoRecord),
            ["frames"] = NewVideoFrames(videoRecord),
            ["turns"] = turns,
            ["audioVerbalization"] = audioVerbalization,
            ["audioVerbalizationResult"] = NewAudioVerbalizationResult(catalogRow, audioVerbalization, turns),
        };
    }

    private JsonObject NewVideoArtifacts(VideoCatalogRow? catalogRow, JsonObject? videoRecord)
    {
        var processing = GetObject(videoRecord, "processing");
        var artifacts = GetObject(processing, "artifacts");
        var contactSheetPath = ConvertVideoLocalPath(GetStringAny(
            artifacts,
            ["contact_sheet", "contactSheet"],
            catalogRow is null ? string.Empty : Path.Combine(catalogRow.OutputDirectory, "artifacts", "contact_sheet.jpg")));
        var audioArtifactPath = ConvertVideoLocalPath(GetStringAny(
            artifacts,
            ["audio_artifact", "audioArtifact"],
            catalogRow is null ? string.Empty : Path.Combine(catalogRow.OutputDirectory, "artifacts", "audio", "source_audio.mp3")));
        var framesDir = ConvertVideoLocalPath(GetStringAny(
            artifacts,
            ["frames_dir", "framesDir"],
            catalogRow is null ? string.Empty : Path.Combine(catalogRow.OutputDirectory, "artifacts", "frames")));

        return new JsonObject
        {
            ["contactSheetPath"] = contactSheetPath,
            ["hasContactSheet"] = File.Exists(contactSheetPath),
            ["audioArtifactPath"] = audioArtifactPath,
            ["hasAudioArtifact"] = File.Exists(audioArtifactPath),
            ["framesDirectory"] = framesDir,
        };
    }

    private JsonObject NewVideoActivity(JsonObject? videoRecord)
    {
        var activity = GetObject(videoRecord, "activity");
        return new JsonObject
        {
            ["available"] = GetBool(activity, "available", false),
            ["strategy"] = GetString(activity, "strategy", string.Empty),
            ["activityMapPath"] = ConvertVideoLocalPath(GetStringAny(activity, ["activityMapJson", "activity_map_json"], string.Empty)),
            ["activeSegments"] = GetInt(activity, "activeSegments", 0),
            ["inactiveSegments"] = GetInt(activity, "inactiveSegments", 0),
            ["activeSec"] = GetDoubleAny(activity, ["activeSec", "active_sec"], 0) ?? 0,
            ["inactiveSec"] = GetDoubleAny(activity, ["inactiveSec", "inactive_sec"], 0) ?? 0,
            ["activeRatio"] = GetDoubleAny(activity, ["activeRatio", "active_ratio"], null),
            ["estimatedReductionRatio"] = GetDoubleAny(activity, ["estimatedReductionRatio", "estimated_reduction_ratio"], null),
            ["visualSentinels"] = GetInt(activity, "visualSentinels", 0),
        };
    }

    private JsonArray NewVideoFrames(JsonObject? videoRecord)
    {
        var frames = new JsonArray();
        foreach (var node in GetArray(videoRecord, "frames"))
        {
            if (node is not JsonObject frame)
            {
                continue;
            }

            frames.Add(NewVideoFrame(frame));
        }

        return frames;
    }

    private JsonObject NewVideoFrame(JsonObject frame)
    {
        var ocr = GetObject(frame, "ocr");
        var visual = GetObject(frame, "visual");
        var quality = GetObject(visual, "quality");
        var artifactPath = ConvertVideoLocalPath(GetStringAny(frame, ["artifact_path", "artifactPath"], string.Empty));
        var ocrOverlayPath = ConvertVideoLocalPath(GetStringAny(ocr, ["debug_overlay_path", "debugOverlayPath"], string.Empty));

        return new JsonObject
        {
            ["frameId"] = GetStringAny(frame, ["frame_id", "frameId"], string.Empty),
            ["timeSec"] = GetDoubleAny(frame, ["time_sec", "timeSec"], 0) ?? 0,
            ["artifactPath"] = artifactPath,
            ["hasArtifact"] = File.Exists(artifactPath),
            ["ocrOverlayPath"] = ocrOverlayPath,
            ["hasOcrOverlay"] = File.Exists(ocrOverlayPath),
            ["ocr"] = new JsonObject
            {
                ["hasText"] = GetBool(ocr, "has_text", GetBool(ocr, "hasText", false)),
                ["blockCount"] = GetIntAny(ocr, ["block_count", "blockCount"], 0),
            },
            ["visual"] = new JsonObject
            {
                ["available"] = GetBool(visual, "available", false),
                ["brightness"] = GetDoubleAny(quality, ["brightness"], null),
                ["contrast"] = GetDoubleAny(quality, ["contrast"], null),
                ["brightnessLevel"] = GetStringAny(quality, ["brightness_level", "brightnessLevel"], string.Empty),
                ["contrastLevel"] = GetStringAny(quality, ["contrast_level", "contrastLevel"], string.Empty),
                ["colorPalette"] = NewVideoColorPalette(GetArray(visual, "color_palette").Count > 0
                    ? GetArray(visual, "color_palette")
                    : GetArray(visual, "colorPalette")),
                ["grid"] = NewVideoGrid(GetArray(visual, "grid")),
            },
        };
    }

    private static JsonArray NewVideoColorPalette(JsonArray paletteNodes)
    {
        var palette = new JsonArray();
        foreach (var node in paletteNodes)
        {
            if (node is not JsonObject color)
            {
                continue;
            }

            palette.Add(new JsonObject
            {
                ["hex"] = GetString(color, "hex", string.Empty),
                ["rgb"] = (JsonArray)GetArray(color, "rgb").DeepClone(),
                ["ratio"] = GetDoubleAny(color, ["ratio"], null),
            });
        }

        return palette;
    }

    private static JsonArray NewVideoGrid(JsonArray gridNodes)
    {
        var grid = new JsonArray();
        foreach (var node in gridNodes)
        {
            if (node is not JsonObject cell)
            {
                continue;
            }

            var averageColor = GetObject(cell, "average_color") ?? GetObject(cell, "averageColor");
            grid.Add(new JsonObject
            {
                ["cellId"] = GetStringAny(cell, ["cell_id", "cellId"], string.Empty),
                ["row"] = GetInt(cell, "row", 0),
                ["col"] = GetInt(cell, "col", 0),
                ["bboxNorm"] = (JsonArray)GetArray(cell, "bbox_norm").DeepClone(),
                ["averageColor"] = new JsonObject
                {
                    ["hex"] = GetString(averageColor, "hex", string.Empty),
                    ["rgb"] = (JsonArray)GetArray(averageColor, "rgb").DeepClone(),
                },
            });
        }

        return grid;
    }

    private JsonObject ReadVideoSettingsPayload()
    {
        var path = GetVideoSettingsFilePath();
        if (!File.Exists(path))
        {
            return NewDefaultSettingsPayload();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? NewDefaultSettingsPayload();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return NewDefaultSettingsPayload();
        }
    }

    private JsonObject NewDefaultSettingsPayload()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["inputRoots"] = new JsonArray(),
            ["outputRoot"] = GetManagedVideoDataDirectory(),
            ["huggingFaceToken"] = string.Empty,
            ["computeMode"] = "gpu",
        };
    }

    private JsonObject ConvertSettingsFile(JsonObject payload)
    {
        var inputRoots = new JsonArray();
        var index = 1;
        foreach (var root in GetStringArrayAny(payload, ["inputRoots", "input_roots"]))
        {
            inputRoots.Add(ConvertInputRoot(root, index));
            index++;
        }

        var outputRoot = GetStringAny(
            payload,
            ["outputRoot", "output_root"],
            GetManagedVideoDataDirectory());
        var computeMode = GetStringAny(payload, ["computeMode", "compute_mode"], "gpu").ToLowerInvariant();
        if (computeMode is not ("cpu" or "gpu"))
        {
            computeMode = "gpu";
        }

        var token = GetStringAny(
            payload,
            ["huggingFaceToken", "huggingfaceToken", "token"],
            string.Empty);

        return new JsonObject
        {
            ["settingsPath"] = GetVideoSettingsFilePath(),
            ["inputRoots"] = inputRoots,
            ["outputRoot"] = ConvertDirectoryRoot("output", "Output", outputRoot),
            ["computeMode"] = computeMode,
            ["hasToken"] = !string.IsNullOrEmpty(token.Trim()),
            ["tokenPreview"] = GetTokenPreview(token),
            ["issues"] = new JsonArray(),
        };
    }

    private JsonObject ConvertInputRoot(string path, int index)
    {
        var localPath = ConvertVideoLocalPath(path);
        return new JsonObject
        {
            ["id"] = "input-" + index.ToString(CultureInfo.InvariantCulture),
            ["displayName"] = !string.IsNullOrEmpty(localPath)
                ? Path.GetFileName(localPath.TrimEnd('\\', '/'))
                : "Input " + index.ToString(CultureInfo.InvariantCulture),
            ["path"] = path,
            ["displayPath"] = !string.IsNullOrEmpty(localPath) ? localPath : path,
            ["enabled"] = true,
            ["exists"] = PathExists(localPath),
        };
    }

    private JsonObject ConvertDirectoryRoot(string id, string displayName, string path)
    {
        var localPath = ConvertVideoLocalPath(path);
        return new JsonObject
        {
            ["id"] = id,
            ["displayName"] = displayName,
            ["path"] = path,
            ["displayPath"] = !string.IsNullOrEmpty(localPath) ? localPath : path,
            ["exists"] = PathExists(localPath),
        };
    }

    private int GetSourceFileCount(JsonObject settings)
    {
        var extensions = GetExtensionSet(settings);
        var count = 0;
        foreach (var root in GetStringArrayAny(settings, ["inputRoots", "input_roots"]))
        {
            var rootPath = ConvertVideoLocalPath(root);
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            count += files.Count(file => extensions.Contains(Path.GetExtension(file)));
        }

        return count;
    }

    private static int GetGeneratedItemCount(string outputRoot)
    {
        var itemsRoot = string.IsNullOrEmpty(outputRoot)
            ? string.Empty
            : Path.Combine(outputRoot, "items");
        if (string.IsNullOrEmpty(itemsRoot) || !Directory.Exists(itemsRoot))
        {
            return 0;
        }

        try
        {
            return Directory
                .EnumerateDirectories(itemsRoot)
                .Count(directory => File.Exists(Path.Combine(directory, "timeline.json")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private JsonObject GetAudioVerbalizationFileSummary()
    {
        var summary = new JsonObject
        {
            ["targetFileCount"] = 0,
            ["verbalizedFileCount"] = 0,
        };

        var index = GetVideoAudioTextIndex();
        if (index.Count == 0)
        {
            return summary;
        }

        foreach (var (itemId, turnCount) in index)
        {
            if (string.IsNullOrEmpty(itemId) || turnCount <= 0)
            {
                continue;
            }

            summary["targetFileCount"] = GetInt(summary, "targetFileCount", 0) + 1;
            if (GetAudioVerbalizationState(itemId) == "completed")
            {
                summary["verbalizedFileCount"] = GetInt(summary, "verbalizedFileCount", 0) + 1;
            }
        }

        return summary;
    }

    private Dictionary<string, int> GetVideoAudioTextIndex()
    {
        var eventsPath = Path.Combine(_settings.GetStoreDirectory(), "events.jsonl");
        if (!File.Exists(eventsPath))
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(eventsPath))
        {
            if (!line.Contains("\"product\":\"video\"", StringComparison.Ordinal)
                || (!line.Contains("\"kind\":\"phone_tokens\"", StringComparison.Ordinal)
                    && !line.Contains("\"kind\":\"transcript_text\"", StringComparison.Ordinal)))
            {
                continue;
            }

            var match = Regex.Match(line, "\"itemId\"\\s*:\\s*\"([^\"]+)\"");
            if (!match.Success)
            {
                continue;
            }

            var itemId = Regex.Unescape(match.Groups[1].Value);
            counts[itemId] = counts.TryGetValue(itemId, out var current)
                ? current + 1
                : 1;
        }

        return counts;
    }

    private string GetAudioVerbalizationState(string itemId)
    {
        var resultPath = Path.Combine(GetAudioVerbalizationRoot(), GetZipSafeSegment(itemId), "audio-verbalization.json");
        if (!File.Exists(resultPath))
        {
            return "not_started";
        }

        try
        {
            var payload = JsonNode.Parse(File.ReadAllText(resultPath)) as JsonObject;
            var status = GetObject(payload, "status");
            return GetString(status, "state", string.Empty).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private string GetAudioVerbalizationRoot()
    {
        var path = Path.Combine(_settings.GetStoreDirectory(), "audio-verbalizations");
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private List<VideoSourceRow> GetSourceRowsFromSettings(JsonObject settings)
    {
        var extensions = GetExtensionSet(settings);
        var rootPaths = GetInputRootPaths(settings);
        var rows = new List<VideoSourceRow>();

        foreach (var rootPath in rootPaths)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var path in files)
            {
                var extension = Path.GetExtension(path);
                if (!extensions.Contains(extension))
                {
                    continue;
                }

                FileInfo file;
                try
                {
                    file = new FileInfo(path);
                    if (!file.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    continue;
                }

                var relativePath = GetRelativePathFromRoots(file.FullName, rootPaths);
                rows.Add(new VideoSourceRow(
                    file.FullName,
                    rootPath,
                    relativePath,
                    GetDirectoryFromRelativePath(relativePath),
                    file.Name,
                    extension,
                    file.Length,
                    file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            }
        }

        return rows
            .OrderBy(row => row.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> GetInputRootPaths(JsonObject settings)
    {
        var rootPaths = new List<string>();
        foreach (var root in GetStringArrayAny(settings, ["inputRoots", "input_roots"]))
        {
            var rootPath = ConvertVideoLocalPath(root);
            if (!string.IsNullOrEmpty(rootPath) && Directory.Exists(rootPath))
            {
                rootPaths.Add(Path.GetFullPath(rootPath));
            }
        }

        return rootPaths;
    }

    private VideoSourceRow? ResolveSourceFile(JsonObject settings, string? sourcePath)
    {
        var candidatePath = ConvertVideoLocalPath(sourcePath);
        if (string.IsNullOrEmpty(candidatePath) || !File.Exists(candidatePath))
        {
            return null;
        }

        var extensionSet = GetExtensionSet(settings);
        var extension = Path.GetExtension(candidatePath);
        if (!extensionSet.Contains(extension))
        {
            return null;
        }

        var rootPaths = GetInputRootPaths(settings);
        if (rootPaths.Count == 0)
        {
            return null;
        }

        string resolvedCandidate;
        try
        {
            resolvedCandidate = Path.GetFullPath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }

        var candidateKey = GetNormalizedPathKey(resolvedCandidate);
        var matchedRoot = string.Empty;
        foreach (var rootPath in rootPaths)
        {
            var rootKey = GetNormalizedPathKey(rootPath);
            if (candidateKey.Equals(rootKey, StringComparison.OrdinalIgnoreCase)
                || candidateKey.StartsWith(rootKey + "\\", StringComparison.OrdinalIgnoreCase))
            {
                matchedRoot = rootPath;
                break;
            }
        }

        if (string.IsNullOrEmpty(matchedRoot))
        {
            return null;
        }

        FileInfo file;
        try
        {
            file = new FileInfo(resolvedCandidate);
            if (!file.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }

        var relativePath = GetRelativePathFromRoots(file.FullName, rootPaths);
        return new VideoSourceRow(
            file.FullName,
            matchedRoot,
            relativePath,
            GetDirectoryFromRelativePath(relativePath),
            file.Name,
            extension,
            file.Length,
            file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private VideoGeneratedCatalog GetGeneratedCatalog(string outputRoot)
    {
        var catalog = new VideoGeneratedCatalog();
        var itemsRoot = string.IsNullOrEmpty(outputRoot)
            ? string.Empty
            : Path.Combine(outputRoot, "items");
        if (string.IsNullOrEmpty(itemsRoot) || !Directory.Exists(itemsRoot))
        {
            return catalog;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(itemsRoot).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return catalog;
        }

        foreach (var directory in directories)
        {
            var convertInfoPath = Path.Combine(directory, "convert_info.json");
            var convertInfo = ReadVideoJsonFile(convertInfoPath);
            if (convertInfo is null)
            {
                continue;
            }

            var sourceIdentity = GetObject(convertInfo, "sourceFileIdentity") ?? new JsonObject();
            var itemId = GetStringAny(
                convertInfo,
                ["itemId", "item_id", "recordId", "record_id"],
                Path.GetFileName(directory));
            var sourcePath = ConvertVideoLocalPath(GetStringAny(
                sourceIdentity,
                ["sourcePath", "source_path"],
                string.Empty));
            var inputRoot = ConvertVideoLocalPath(GetStringAny(
                sourceIdentity,
                ["inputRoot", "input_root"],
                string.Empty));
            var sizeBytes = GetLongAny(sourceIdentity, ["sizeBytes", "size_bytes"], 0);
            var relativePath = (!string.IsNullOrEmpty(sourcePath) && !string.IsNullOrEmpty(inputRoot))
                ? GetRelativePathFromRoots(sourcePath, [inputRoot])
                : Path.GetFileName(sourcePath);
            var counts = GetObject(convertInfo, "counts") ?? new JsonObject();
            var audioProcessing = GetObject(convertInfo, "audioProcessing") ?? new JsonObject();
            var sampling = GetObject(convertInfo, "samplingParameters") ?? new JsonObject();
            var frameCount = GetIntAny(counts, ["frames", "frameCount"], 0);
            if (frameCount <= 0)
            {
                frameCount = GetArray(sampling, "timesSec").Count;
            }

            var textBlockCount = GetIntAny(counts, ["ocrTextBlocks", "textBlockCount"], 0);
            var speechCandidateCount = GetIntAny(
                counts,
                ["audioSpeechCandidates", "speechCandidates"],
                GetInt(audioProcessing, "speechCandidates", 0));
            var transcriptionStatus = GetStringAny(
                audioProcessing,
                ["transcriptionStatus", "transcription_status"],
                string.Empty);
            var timelinePath = Path.Combine(directory, "timeline.json");

            var row = new VideoCatalogRow(
                itemId,
                sourcePath,
                inputRoot,
                relativePath,
                sizeBytes,
                directory,
                timelinePath,
                convertInfoPath,
                Path.Combine(directory, "video_record.json"),
                frameCount,
                textBlockCount,
                speechCandidateCount,
                IsTranscriptStatusAvailable(transcriptionStatus),
                GetDurationFromProbe(directory, convertInfo));
            AddCatalogRow(catalog, row);
        }

        return catalog;
    }

    private JsonObject ConvertSourceFileRow(VideoSourceRow sourceRow, VideoGeneratedCatalog catalog)
    {
        var catalogRow = FindGeneratedCatalogRow(catalog, sourceRow);
        var itemId = catalogRow?.ItemId ?? string.Empty;
        var hasTimeline = catalogRow is not null
            && !string.IsNullOrEmpty(catalogRow.TimelinePath)
            && File.Exists(catalogRow.TimelinePath);
        var durationSec = catalogRow?.DurationSec;
        var result = new JsonObject
        {
            ["itemId"] = itemId,
            ["sourceFileIdentity"] = !string.IsNullOrEmpty(itemId) ? "video:" + itemId : sourceRow.SourcePath,
            ["sourcePath"] = sourceRow.SourcePath,
            ["rootPath"] = sourceRow.RootPath,
            ["displayPath"] = sourceRow.SourcePath,
            ["relativePath"] = sourceRow.RelativePath,
            ["directory"] = sourceRow.Directory,
            ["fileName"] = sourceRow.FileName,
            ["extension"] = sourceRow.Extension,
            ["sizeBytes"] = sourceRow.SizeBytes,
            ["modifiedAt"] = sourceRow.ModifiedAt,
            ["status"] = hasTimeline ? "completed" : "unprocessed",
            ["hasTimeline"] = hasTimeline,
            ["frameCount"] = catalogRow?.FrameCount ?? 0,
            ["textBlockCount"] = catalogRow?.TextBlockCount ?? 0,
            ["speechCandidateCount"] = catalogRow?.SpeechCandidateCount ?? 0,
            ["turnCount"] = catalogRow?.SpeechCandidateCount ?? 0,
            ["hasSourceTranscript"] = catalogRow?.HasSourceTranscript ?? false,
            ["audioVerbalization"] = NewAudioVerbalizationStatus(catalogRow),
        };
        result["durationSec"] = durationSec.HasValue ? JsonValue.Create(durationSec.Value) : null;
        return result;
    }

    private JsonArray GetTranscriptTurns(VideoCatalogRow? catalogRow)
    {
        var turns = new JsonArray();
        if (catalogRow is null || string.IsNullOrEmpty(catalogRow.OutputDirectory))
        {
            return turns;
        }

        var audioAnalysisPath = Path.Combine(catalogRow.OutputDirectory, "raw_outputs", "audio_analysis.json");
        var audioAnalysis = ReadVideoJsonFile(audioAnalysisPath);
        var speechActivity = GetObject(audioAnalysis, "speechActivity");
        var speechCandidates = GetArray(speechActivity, "speechCandidates")
            .OfType<JsonObject>()
            .ToList();
        var hasSpeechEvidence = speechActivity is not null;
        var transcription = GetObject(audioAnalysis, "transcription");
        var segments = GetArray(transcription, "segments");
        if (segments.Count == 0)
        {
            return turns;
        }
        var repeatedTexts = segments
            .OfType<JsonObject>()
            .Select(segment => NormalizeTranscriptText(GetString(segment, "text", string.Empty)))
            .Where(text => text.Length > 0)
            .GroupBy(text => text, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 3)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var segmentNode in segments)
        {
            if (segmentNode is not JsonObject segment)
            {
                continue;
            }

            var text = GetString(segment, "text", string.Empty);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            if (ShouldRejectTranscriptSegment(segment, speechCandidates, hasSpeechEvidence, repeatedTexts))
            {
                continue;
            }

            var startSec = GetDoubleAny(segment, ["startSec", "start_sec", "start"], 0) ?? 0;
            var endSec = GetDoubleAny(segment, ["endSec", "end_sec", "end"], startSec) ?? startSec;
            var turn = new JsonObject
            {
                ["index"] = turns.Count + 1,
                ["startSec"] = startSec,
                ["endSec"] = endSec,
                ["absoluteStartAt"] = string.Empty,
                ["absoluteEndAt"] = string.Empty,
                ["speaker"] = GetString(segment, "speaker", string.Empty),
                ["text"] = text,
                ["phoneTokens"] = string.Empty,
                ["unitType"] = "transcript_text",
            };
            var confidence = GetDoubleAny(segment, ["confidence"], null);
            turn["confidence"] = confidence.HasValue ? JsonValue.Create(confidence.Value) : null;
            turns.Add(turn);
        }

        return turns;
    }

    private static bool ShouldRejectTranscriptSegment(
        JsonObject segment,
        List<JsonObject> speechCandidates,
        bool hasSpeechEvidence,
        HashSet<string> repeatedTexts)
    {
        var text = GetString(segment, "text", string.Empty);
        var normalizedText = NormalizeTranscriptText(text);
        var startSec = GetDoubleAny(segment, ["startSec", "start_sec", "start"], 0) ?? 0;
        var endSec = GetDoubleAny(segment, ["endSec", "end_sec", "end"], startSec) ?? startSec;
        var speechOverlap = TranscriptSpeechOverlap(startSec, endSec, speechCandidates);
        var speakerAssignment = GetObject(segment, "speakerAssignment");
        var speakerOverlap = GetDoubleAny(speakerAssignment, ["overlapSec", "overlap_sec"], 0) ?? 0;
        var confidence = GetDoubleAny(segment, ["confidence", "avg_logprob"], null);
        var noSpeechProbability = GetDoubleAny(segment, ["noSpeechProbability", "no_speech_probability", "no_speech_prob"], null);

        if (hasSpeechEvidence && speechOverlap < MinimumTranscriptSpeechOverlapSec)
        {
            return true;
        }

        if (noSpeechProbability.HasValue && noSpeechProbability.Value >= HighNoSpeechProbability)
        {
            return true;
        }

        if (KnownSilenceHallucinationPhrases.Contains(normalizedText)
            && repeatedTexts.Contains(normalizedText)
            && (speechOverlap < MinimumTranscriptSpeechOverlapSec || speakerOverlap <= 0))
        {
            return true;
        }

        return confidence.HasValue
            && confidence.Value < LowTranscriptConfidence
            && speakerOverlap <= 0
            && KnownSilenceHallucinationPhrases.Contains(normalizedText);
    }

    private static double TranscriptSpeechOverlap(
        double startSec,
        double endSec,
        List<JsonObject> speechCandidates)
    {
        if (endSec <= startSec)
        {
            return 0;
        }

        var overlap = 0.0;
        foreach (var candidate in speechCandidates)
        {
            var candidateStart = GetDoubleAny(candidate, ["startSec", "start_sec", "original_start"], 0) ?? 0;
            var candidateEnd = GetDoubleAny(candidate, ["endSec", "end_sec", "original_end"], candidateStart) ?? candidateStart;
            overlap += Math.Max(0, Math.Min(endSec, candidateEnd) - Math.Max(startSec, candidateStart));
        }

        return overlap;
    }

    private static string NormalizeTranscriptText(string value)
        => string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();

    private JsonObject NewAudioVerbalizationStatus(VideoCatalogRow? catalogRow)
    {
        if (catalogRow is null || string.IsNullOrEmpty(catalogRow.ItemId))
        {
            return NewAudioVerbalizationStatusCore(
                false,
                "not_started",
                string.Empty,
                string.Empty,
                0,
                0,
                0,
                string.Empty);
        }

        var stored = ReadAudioVerbalizationStatus(catalogRow.ItemId);
        if (stored is not null)
        {
            return NormalizeAudioVerbalizationStatus(stored, catalogRow);
        }

        var totalTurns = Math.Max(0, catalogRow.SpeechCandidateCount);
        if (catalogRow.HasSourceTranscript && totalTurns > 0)
        {
            return NewAudioVerbalizationStatusCore(
                true,
                "source_transcript",
                catalogRow.ItemId,
                "video:" + catalogRow.ItemId,
                totalTurns,
                totalTurns,
                0,
                string.Empty);
        }

        return NewAudioVerbalizationStatusCore(
            totalTurns > 0,
            "not_started",
            catalogRow.ItemId,
            "video:" + catalogRow.ItemId,
            totalTurns,
            0,
            totalTurns,
            string.Empty);
    }

    private static void ApplyTranscriptTurnCounts(JsonObject status, int turnCount)
    {
        if (turnCount <= 0)
        {
            status["totalTurns"] = 0;
            status["verbalizedTurns"] = 0;
            status["unresolvedTurns"] = 0;
            return;
        }

        var state = GetString(status, "state", string.Empty);
        if (string.IsNullOrEmpty(state)
            || state.Equals("not_started", StringComparison.OrdinalIgnoreCase))
        {
            status["state"] = "source_transcript";
        }

        status["available"] = true;
        status["totalTurns"] = turnCount;
        if (GetString(status, "state", string.Empty).Equals("source_transcript", StringComparison.OrdinalIgnoreCase))
        {
            status["verbalizedTurns"] = turnCount;
            status["unresolvedTurns"] = 0;
            return;
        }

        var verbalizedTurns = Math.Min(turnCount, Math.Max(0, GetInt(status, "verbalizedTurns", 0)));
        status["verbalizedTurns"] = verbalizedTurns;
        status["unresolvedTurns"] = Math.Max(0, turnCount - verbalizedTurns);
    }

    private static JsonObject NewAudioVerbalizationResult(JsonObject status, JsonArray turns)
    {
        var resultTurns = new JsonArray();
        foreach (var turnNode in turns)
        {
            if (turnNode is not JsonObject turn)
            {
                continue;
            }

            var text = GetString(turn, "text", string.Empty);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var index = GetInt(turn, "index", resultTurns.Count + 1);
            var resultTurn = new JsonObject
            {
                ["turnId"] = "turn-" + index.ToString("000000", CultureInfo.InvariantCulture),
                ["index"] = index,
                ["startSec"] = GetDoubleAny(turn, ["startSec", "start_sec"], 0) ?? 0,
                ["endSec"] = GetDoubleAny(turn, ["endSec", "end_sec"], 0) ?? 0,
                ["speaker"] = GetString(turn, "speaker", string.Empty),
                ["text"] = text,
                ["status"] = "source_transcript",
                ["basis"] = NewStringArray(["source_transcript"]),
                ["uncertainTerms"] = new JsonArray(),
            };
            var confidence = GetDoubleAny(turn, ["confidence"], null);
            resultTurn["confidence"] = confidence.HasValue ? JsonValue.Create(confidence.Value) : null;
            resultTurns.Add(resultTurn);
        }

        return new JsonObject
        {
            ["available"] = resultTurns.Count > 0,
            ["status"] = (JsonObject)status.DeepClone(),
            ["turns"] = resultTurns,
            ["chunks"] = new JsonArray(),
            ["message"] = string.Empty,
        };
    }

    private JsonObject? ReadAudioVerbalizationStatus(string itemId)
    {
        var payload = ReadAudioVerbalizationPayload(itemId);
        return GetObject(payload, "status") ?? payload;
    }

    private JsonObject? ReadAudioVerbalizationPayload(string itemId)
    {
        var resultPath = Path.Combine(GetAudioVerbalizationRoot(), GetZipSafeSegment(itemId), "audio-verbalization.json");
        return ReadVideoJsonFile(resultPath);
    }

    private JsonObject NewAudioVerbalizationResult(VideoCatalogRow? catalogRow, JsonObject status, JsonArray sourceTurns)
    {
        if (catalogRow is not null && !string.IsNullOrEmpty(catalogRow.ItemId))
        {
            var payload = ReadAudioVerbalizationPayload(catalogRow.ItemId);
            var storedTurns = GetArray(payload, "turns");
            if (storedTurns.Count > 0)
            {
                var filteredStoredTurns = FilterStoredAudioVerbalizationTurns(storedTurns, sourceTurns);
                return new JsonObject
                {
                    ["available"] = filteredStoredTurns.Count > 0,
                    ["status"] = (JsonObject)status.DeepClone(),
                    ["turns"] = filteredStoredTurns,
                    ["chunks"] = (JsonArray)GetArray(payload, "chunks").DeepClone(),
                    ["message"] = string.Empty,
                };
            }
        }

        return NewAudioVerbalizationResult(status, sourceTurns);
    }

    private static JsonArray FilterStoredAudioVerbalizationTurns(JsonArray storedTurns, JsonArray sourceTurns)
    {
        var sourceIntervals = sourceTurns
            .OfType<JsonObject>()
            .Select(turn =>
            {
                var start = GetDoubleAny(turn, ["startSec", "start_sec"], null);
                var end = GetDoubleAny(turn, ["endSec", "end_sec"], start);
                return new
                {
                    Start = start,
                    End = end,
                    Text = NormalizeTranscriptText(GetString(turn, "text", string.Empty)),
                };
            })
            .Where(turn => turn.Start.HasValue && turn.End.HasValue && turn.End.Value > turn.Start.Value)
            .ToList();

        var result = new JsonArray();
        if (sourceIntervals.Count == 0)
        {
            return result;
        }

        foreach (var storedNode in storedTurns)
        {
            if (storedNode is not JsonObject storedTurn)
            {
                continue;
            }

            var start = GetDoubleAny(storedTurn, ["startSec", "start_sec"], null);
            var end = GetDoubleAny(storedTurn, ["endSec", "end_sec"], start);
            if (!start.HasValue || !end.HasValue || end.Value <= start.Value)
            {
                continue;
            }

            var text = NormalizeTranscriptText(GetString(storedTurn, "text", string.Empty));
            var matchesSource = sourceIntervals.Any(source =>
            {
                var overlap = Math.Max(0, Math.Min(end.Value, source.End!.Value) - Math.Max(start.Value, source.Start!.Value));
                if (overlap >= MinimumTranscriptSpeechOverlapSec)
                {
                    return true;
                }

                return text.Length > 0
                    && source.Text.Equals(text, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(source.Start.Value - start.Value) <= MinimumTranscriptSpeechOverlapSec
                    && Math.Abs(source.End.Value - end.Value) <= MinimumTranscriptSpeechOverlapSec;
            });

            if (matchesSource)
            {
                result.Add(storedTurn.DeepClone());
            }
        }

        return result;
    }

    private static JsonObject NormalizeAudioVerbalizationStatus(JsonObject status, VideoCatalogRow catalogRow)
    {
        var copy = new JsonObject();
        foreach (var property in status)
        {
            copy[property.Key] = property.Value?.DeepClone();
        }

        SetIfMissing(copy, "available", true);
        SetIfMissing(copy, "state", "not_started");
        SetIfMissing(copy, "audioItemId", catalogRow.ItemId);
        SetIfMissing(copy, "sourceFileIdentity", "video:" + catalogRow.ItemId);
        SetIfMissing(copy, "language", "ja-JP");
        SetIfMissing(copy, "model", "qwen3.5:9b");
        SetIfMissing(copy, "signature", string.Empty);
        SetIfMissing(copy, "expectedSignature", string.Empty);
        SetIfMissing(copy, "summarySignature", string.Empty);
        SetIfMissing(copy, "expectedSummarySignature", string.Empty);
        SetIfMissing(copy, "signatureState", string.Empty);
        SetIfMissing(copy, "promptVersion", string.Empty);
        SetIfMissing(copy, "totalTurns", catalogRow.SpeechCandidateCount);
        SetIfMissing(copy, "verbalizedTurns", 0);
        SetIfMissing(copy, "unresolvedTurns", Math.Max(0, catalogRow.SpeechCandidateCount));
        SetIfMissing(copy, "totalChunks", 0);
        SetIfMissing(copy, "completedChunks", 0);
        SetIfMissing(copy, "jobId", string.Empty);
        SetIfMissing(copy, "currentChunkId", string.Empty);
        SetIfMissing(copy, "planPath", string.Empty);
        SetIfMissing(copy, "resultPath", string.Empty);
        SetIfMissing(copy, "startedAt", string.Empty);
        SetIfMissing(copy, "elapsedSec", 0);
        SetIfMissing(copy, "estimatedRemainingSec", 0);
        SetIfMissing(copy, "updatedAt", string.Empty);
        SetIfMissing(copy, "message", string.Empty);
        return copy;
    }

    private static JsonObject NewAudioVerbalizationStatusCore(
        bool available,
        string state,
        string itemId,
        string sourceFileIdentity,
        int totalTurns,
        int verbalizedTurns,
        int unresolvedTurns,
        string message)
    {
        return new JsonObject
        {
            ["available"] = available,
            ["state"] = state,
            ["audioItemId"] = itemId,
            ["sourceFileIdentity"] = sourceFileIdentity,
            ["language"] = "ja-JP",
            ["model"] = "qwen3.5:9b",
            ["signature"] = string.Empty,
            ["expectedSignature"] = string.Empty,
            ["summarySignature"] = string.Empty,
            ["expectedSummarySignature"] = string.Empty,
            ["signatureState"] = string.Empty,
            ["promptVersion"] = string.Empty,
            ["totalTurns"] = totalTurns,
            ["verbalizedTurns"] = verbalizedTurns,
            ["unresolvedTurns"] = unresolvedTurns,
            ["totalChunks"] = 0,
            ["completedChunks"] = 0,
            ["jobId"] = string.Empty,
            ["currentChunkId"] = string.Empty,
            ["planPath"] = string.Empty,
            ["resultPath"] = string.Empty,
            ["startedAt"] = string.Empty,
            ["elapsedSec"] = 0,
            ["estimatedRemainingSec"] = 0,
            ["updatedAt"] = string.Empty,
            ["message"] = message,
        };
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

    private double? GetDurationFromProbe(string itemDirectory, JsonObject convertInfo)
    {
        var probePath = GetOutputFilePath(convertInfo, "ffprobe_raw");
        if (string.IsNullOrEmpty(probePath))
        {
            probePath = Path.Combine(itemDirectory, "raw_outputs", "ffprobe.json");
        }

        var probe = ReadVideoJsonFile(probePath);
        var ffprobe = GetObject(probe, "ffprobe");
        var summary = GetObject(ffprobe, "summary");
        var format = GetObject(summary, "format");
        return GetDoubleAny(format, ["durationSec", "duration_sec"], null);
    }

    private string GetOutputFilePath(JsonObject convertInfo, string kind)
    {
        foreach (var node in GetArray(convertInfo, "outputFiles"))
        {
            if (node is not JsonObject row)
            {
                continue;
            }

            if (!GetString(row, "kind", string.Empty).Equals(kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = ConvertVideoLocalPath(GetString(row, "path", string.Empty));
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static void AddCatalogRow(VideoGeneratedCatalog catalog, VideoCatalogRow row)
    {
        catalog.Rows.Add(row);
        if (!string.IsNullOrEmpty(row.SourcePath))
        {
            catalog.BySourcePath.TryAdd(GetNormalizedPathKey(row.SourcePath), row);
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

    private static VideoCatalogRow? FindGeneratedCatalogRow(
        VideoGeneratedCatalog catalog,
        VideoSourceRow sourceRow)
    {
        var sourceKey = GetNormalizedPathKey(sourceRow.SourcePath);
        if (catalog.BySourcePath.TryGetValue(sourceKey, out var sourceMatch))
        {
            return sourceMatch;
        }

        var relativeSizeKey = NewRelativeSizeKey(sourceRow.RelativePath, sourceRow.SizeBytes);
        return catalog.ByRelativeSize.TryGetValue(relativeSizeKey, out var rows) && rows.Count > 0
            ? rows[0]
            : null;
    }

    private static string NewRelativeSizeKey(string relativePath, long sizeBytes)
        => ConvertTimelineText(relativePath).Replace('/', '\\')
            + "|"
            + sizeBytes.ToString(CultureInfo.InvariantCulture);

    private static string GetNormalizedPathKey(string path)
    {
        var text = ConvertTimelineText(path).Replace('/', '\\').TrimEnd('\\');
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            text = Path.GetFullPath(text).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
        }

        return text.ToLowerInvariant();
    }

    private static string GetRelativePathFromRoots(string path, IEnumerable<string> roots)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var root in roots)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(fullRoot, fullPath);
            }
        }

        return Path.GetFileName(fullPath);
    }

    private static string GetDirectoryFromRelativePath(string relativePath)
    {
        var normalized = ConvertTimelineText(relativePath).Replace('/', '\\');
        var lastSeparator = normalized.LastIndexOf('\\');
        return lastSeparator > 0 ? normalized[..lastSeparator] : string.Empty;
    }

    private static bool IsTranscriptStatusAvailable(string status)
    {
        return ConvertTimelineText(status).ToLowerInvariant() is "ok" or "completed" or "success" or "succeeded";
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

    private HashSet<string> GetExtensionSet(JsonObject settings)
    {
        var configured = GetStringArrayAny(settings, ["videoExtensions", "video_extensions"]).ToList();
        if (configured.Count == 0)
        {
            configured.AddRange(DefaultVideoExtensions);
        }

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in configured)
        {
            var text = ConvertTimelineText(extension);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            extensions.Add(text.StartsWith(".", StringComparison.Ordinal) ? text : "." + text);
        }

        return extensions;
    }

    private string ConvertVideoLocalPath(string? path)
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

    private string GetVideoSettingsFilePath()
    {
        var productPath = GetProductPath();
        var settingsPath = Path.Combine(productPath, "settings.json");
        if (File.Exists(settingsPath))
        {
            return settingsPath;
        }

        return Path.Combine(productPath, "settings.example.json");
    }

    private string GetProductPath()
    {
        foreach (var product in _settings.ReadSettings().ProductRegistry.Products)
        {
            if (product.Id.Equals("video", StringComparison.OrdinalIgnoreCase))
            {
                return product.Path ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private string GetManagedVideoDataDirectory()
    {
        var path = Path.Combine(_settings.GetDataRootDirectory(), "to_text", "video");
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private static JsonArray CloneArray(JsonObject source, string name)
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

    private static JsonObject? GetObject(JsonObject? source, string name)
        => GetNode(source, name) as JsonObject;

    private static JsonArray GetArray(JsonObject? source, string name)
        => GetNode(source, name) as JsonArray ?? new JsonArray();

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
        if (node is null)
        {
            return fallback;
        }

        if (node.GetValueKind() == JsonValueKind.Number)
        {
            try
            {
                return node.GetValue<int>();
            }
            catch (FormatException)
            {
            }
        }

        return int.TryParse(ConvertNodeToString(node), out var parsed)
            ? parsed
            : fallback;
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

            return int.TryParse(ConvertNodeToString(node), out var parsed)
                ? parsed
                : fallback;
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

            return long.TryParse(ConvertNodeToString(node), out var parsed)
                ? parsed
                : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
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

    private static bool GetBool(JsonObject? source, string name, bool fallback)
    {
        var text = GetString(source, name, string.Empty);
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        return text.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static IEnumerable<string> GetStringArrayAny(JsonObject? source, string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetNode(source, name, out var node) || node is not JsonArray array || array.Count == 0)
            {
                continue;
            }

            return array
                .Select(ConvertNodeToString)
                .Where(value => !string.IsNullOrEmpty(value));
        }

        return [];
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

    private static JsonObject? ReadVideoJsonFile(string path)
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

    private static JsonObject SetIfMissing(JsonObject source, string name, JsonNode? value)
    {
        if (!TryGetNode(source, name, out _))
        {
            source[name] = value;
        }

        return source;
    }

    private static JsonObject SetIfMissing(JsonObject source, string name, string value)
        => SetIfMissing(source, name, JsonValue.Create(value));

    private static JsonObject SetIfMissing(JsonObject source, string name, bool value)
        => SetIfMissing(source, name, JsonValue.Create(value));

    private static JsonObject SetIfMissing(JsonObject source, string name, int value)
        => SetIfMissing(source, name, JsonValue.Create(value));

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

    private static bool PathExists(string path)
        => !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

    private sealed class VideoGeneratedCatalog
    {
        public List<VideoCatalogRow> Rows { get; } = [];
        public Dictionary<string, VideoCatalogRow> BySourcePath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<VideoCatalogRow>> ByRelativeSize { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record VideoSourceRow(
        string SourcePath,
        string RootPath,
        string RelativePath,
        string Directory,
        string FileName,
        string Extension,
        long SizeBytes,
        string ModifiedAt);

    private sealed record VideoCatalogRow(
        string ItemId,
        string SourcePath,
        string InputRoot,
        string RelativePath,
        long SizeBytes,
        string OutputDirectory,
        string TimelinePath,
        string ConvertInfoPath,
        string VideoRecordPath,
        int FrameCount,
        int TextBlockCount,
        int SpeechCandidateCount,
        bool HasSourceTranscript,
        double? DurationSec);
}
