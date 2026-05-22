using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class TimelineImageFileService
{
    private static readonly string[] DefaultImageExtensions =
    [
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif",
        ".bmp",
        ".tif",
        ".tiff",
        ".heic",
    ];

    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;

    public TimelineImageFileService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations)
    {
        _settings = settings;
        _operations = operations;
    }

    public JsonObject GetFiles(int page, int pageSize)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForImage",
            "image_files_list",
            "started",
            "Web operation started.");

        try
        {
            var result = GetFilesCore(page, pageSize);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForImage",
                "image_files_list",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: new JsonObject
                {
                    ["total"] = GetInt(result, "total", 0),
                    ["processedTotal"] = GetInt(result, "processedTotal", 0),
                });
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForImage",
                "image_files_list",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    public JsonObject GetItems(int page, int pageSize)
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForImage",
            "image_items_list",
            "started",
            "Web operation started.");

        try
        {
            var result = GetItemsCore(page, pageSize);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForImage",
                "image_items_list",
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
                "TimelineForImage",
                "image_items_list",
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
            "TimelineForImage",
            "image_file_detail",
            "started",
            "Web operation started.");

        try
        {
            var result = GetFileDetailCore(sourcePath);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForImage",
                "image_file_detail",
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
                "TimelineForImage",
                "image_file_detail",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    public JsonObject GetOverview()
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "TimelineForImage",
            "image_overview",
            "started",
            "Web operation started.");

        try
        {
            var result = GetOverviewCore();
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "TimelineForImage",
                "image_overview",
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
                "TimelineForImage",
                "image_overview",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private JsonObject GetFilesCore(int page, int pageSize)
    {
        var settingsPayload = ReadImageSettingsPayload();
        var outputRoot = GetImageCurrentOutputRoot(settingsPayload);
        var catalog = GetGeneratedCatalog(outputRoot);
        var sourceRows = GetSourceRowsFromSettings(settingsPayload);
        var total = sourceRows.Count;
        var processedTotal = sourceRows.Count(row =>
        {
            var catalogRow = FindGeneratedCatalogRowByRelativeSize(catalog, row.RelativePath, row.SizeBytes);
            return catalogRow is not null
                && ((catalogRow.TimelinePath.Length > 0 && File.Exists(catalogRow.TimelinePath))
                    || (catalogRow.ImageRecordPath.Length > 0 && File.Exists(catalogRow.ImageRecordPath)));
        });

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
            ["total"] = total,
            ["processedTotal"] = processedTotal,
            ["pagination"] = NewPagination(effectivePage, effectivePageSize, total, files.Count),
            ["files"] = files,
        };
    }

    private JsonObject GetItemsCore(int page, int pageSize)
    {
        var settingsPayload = ReadImageSettingsPayload();
        var outputRoot = GetImageCurrentOutputRoot(settingsPayload);
        var catalog = GetGeneratedCatalog(outputRoot);
        var rows = catalog.Rows
            .Where(row => File.Exists(row.TimelinePath) || File.Exists(row.ImageRecordPath))
            .OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Max(1, pageSize);
        var offset = (effectivePage - 1) * effectivePageSize;
        var items = new JsonArray();
        foreach (var row in rows.Skip(offset).Take(effectivePageSize))
        {
            items.Add(ConvertCatalogRow(row));
        }

        return new JsonObject
        {
            ["total"] = rows.Count,
            ["pagination"] = NewPagination(effectivePage, effectivePageSize, rows.Count, items.Count),
            ["items"] = items,
        };
    }

    private JsonObject GetFileDetailCore(string? sourcePath)
    {
        var settingsPayload = ReadImageSettingsPayload();
        var sourceRow = ResolveSourceFile(settingsPayload, sourcePath);
        if (sourceRow is null)
        {
            return new JsonObject
            {
                ["available"] = false,
                ["message"] = "Image source file was not found.",
                ["file"] = null,
                ["imageAvailable"] = false,
                ["imageRecordAvailable"] = false,
                ["timelineAvailable"] = false,
                ["record"] = new JsonObject(),
                ["textBlocks"] = new JsonArray(),
            };
        }

        var outputRoot = GetImageCurrentOutputRoot(settingsPayload);
        var catalog = GetGeneratedCatalog(outputRoot);
        var file = ConvertSourceFileRow(sourceRow, catalog);
        var imageRecordPath = GetString(file, "imageRecordPath", string.Empty);
        var timelinePath = GetString(file, "timelinePath", string.Empty);
        var convertInfoPath = GetString(file, "convertInfoPath", string.Empty);
        var outputDirectory = GetString(file, "outputDirectory", string.Empty);
        var imageRecord = ReadImageJsonFile(imageRecordPath);
        var timeline = ReadImageJsonFile(timelinePath);
        var convertInfo = ReadImageJsonFile(convertInfoPath);

        var record = new JsonObject();
        var visual = new JsonObject();
        var layout = new JsonObject
        {
            ["coordinateSystem"] = string.Empty,
            ["colorPalette"] = new JsonArray(),
            ["grid"] = new JsonArray(),
            ["textRegions"] = new JsonArray(),
            ["spatialRelationCount"] = 0,
        };
        var searchKeywords = new JsonArray();
        var textBlocks = new JsonArray();
        if (imageRecord is not null)
        {
            record = ConvertRecordSummary(imageRecord, timeline, convertInfo);
            visual = ConvertVisualDescription(imageRecord);
            layout = ConvertLayoutSummary(imageRecord);
            searchKeywords = ConvertSearchKeywords(imageRecord);
            var text = GetObject(imageRecord, "text");
            var index = 1;
            foreach (var blockNode in GetArray(text, "blocks"))
            {
                if (blockNode is not JsonObject block)
                {
                    index++;
                    continue;
                }

                var converted = ConvertTextBlock(block, index);
                if (!string.IsNullOrEmpty(GetString(converted, "text", string.Empty)))
                {
                    textBlocks.Add(converted);
                }

                index++;
                if (textBlocks.Count >= 200)
                {
                    break;
                }
            }
        }

        return new JsonObject
        {
            ["available"] = true,
            ["message"] = string.Empty,
            ["file"] = file,
            ["imageAvailable"] = true,
            ["imageRecordAvailable"] = imageRecord is not null,
            ["timelineAvailable"] = timeline is not null,
            ["imageRecordPath"] = imageRecordPath,
            ["timelinePath"] = timelinePath,
            ["convertInfoPath"] = convertInfoPath,
            ["record"] = record,
            ["visual"] = visual,
            ["layout"] = layout,
            ["artifacts"] = ConvertImageArtifacts(outputDirectory, convertInfo),
            ["searchKeywords"] = searchKeywords,
            ["textBlocks"] = textBlocks,
        };
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
                ["latestRefresh"] = new JsonObject(),
                ["message"] = "TimelineForImage was not found.",
            };
        }

        try
        {
            var settingsPayload = ReadImageSettingsPayload();
            var outputPath = GetStringAny(settingsPayload, ["outputRoot", "output_root"], string.Empty);
            var outputLocalPath = ConvertImageLocalPath(outputPath);
            return new JsonObject
            {
                ["productFound"] = true,
                ["productPath"] = productPath,
                ["settingsValid"] = true,
                ["settings"] = ConvertSettingsFile(settingsPayload),
                ["sourceFileCount"] = GetSourceRowsFromSettings(settingsPayload).Count,
                ["itemCount"] = GetGeneratedItemCount(outputLocalPath),
                ["latestRefresh"] = new JsonObject(),
                ["message"] = string.Empty,
            };
        }
        catch (Exception ex)
        {
            var settingsPayload = ReadImageSettingsPayload();
            return new JsonObject
            {
                ["productFound"] = true,
                ["productPath"] = productPath,
                ["settingsValid"] = false,
                ["settings"] = ConvertSettingsFile(settingsPayload),
                ["sourceFileCount"] = 0,
                ["itemCount"] = 0,
                ["latestRefresh"] = new JsonObject(),
                ["message"] = ex.Message,
            };
        }
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
            GetManagedImageDataDirectory());
        return new JsonObject
        {
            ["settingsPath"] = GetImageSettingsFilePath(),
            ["inputRoots"] = inputRoots,
            ["outputRoot"] = ConvertDirectoryRoot("output", "Output", outputRoot),
            ["issues"] = new JsonArray(),
        };
    }

    private JsonObject ConvertInputRoot(string path, int index)
    {
        var localPath = ConvertImageLocalPath(path);
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
        var localPath = ConvertImageLocalPath(path);
        return new JsonObject
        {
            ["id"] = id,
            ["displayName"] = displayName,
            ["path"] = path,
            ["displayPath"] = !string.IsNullOrEmpty(localPath) ? localPath : path,
            ["exists"] = PathExists(localPath),
        };
    }

    private JsonObject ReadImageSettingsPayload()
    {
        var productPath = GetProductPath();
        if (!string.IsNullOrEmpty(productPath))
        {
            var settingsPath = GetImageSettingsFilePath();

            if (File.Exists(settingsPath))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject payload)
                    {
                        return payload;
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["inputRoots"] = new JsonArray(),
            ["outputRoot"] = GetManagedImageDataDirectory(),
        };
    }

    private string GetImageSettingsFilePath()
    {
        var productPath = GetProductPath();
        var settingsPath = Path.Combine(productPath, "settings.json");
        if (File.Exists(settingsPath))
        {
            return settingsPath;
        }

        return Path.Combine(productPath, "settings.example.json");
    }

    private string GetImageCurrentOutputRoot(JsonObject settings)
    {
        var outputRoot = GetStringAny(
            settings,
            ["outputRoot", "output_root"],
            GetManagedImageDataDirectory());
        return ConvertImageLocalPath(outputRoot);
    }

    private HashSet<string> GetExtensionSet(JsonObject settings)
    {
        var configured = GetStringArrayAny(settings, ["imageExtensions", "image_extensions"]).ToList();
        if (configured.Count == 0)
        {
            configured.AddRange(DefaultImageExtensions);
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

    private List<ImageSourceRow> GetSourceRowsFromSettings(JsonObject settings)
    {
        var extensionSet = GetExtensionSet(settings);
        var rootPaths = GetInputRootPaths(settings);

        var rows = new List<ImageSourceRow>();
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

            foreach (var filePath in files)
            {
                var extension = Path.GetExtension(filePath);
                if (!extensionSet.Contains(extension))
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

                var relativePath = GetRelativePathFromRoots(file.FullName, rootPaths);
                rows.Add(new ImageSourceRow(
                    file.FullName,
                    file.Name,
                    relativePath,
                    file.Length,
                    file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            }
        }

        return rows
            .OrderBy(row => row.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    private ImageSourceRow? ResolveSourceFile(JsonObject settings, string? sourcePath)
    {
        var candidatePath = ConvertImageLocalPath(sourcePath);
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

        return new ImageSourceRow(
            file.FullName,
            file.Name,
            GetRelativePathFromRoots(file.FullName, rootPaths),
            file.Length,
            file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private List<string> GetInputRootPaths(JsonObject settings)
    {
        var rootPaths = new List<string>();
        foreach (var root in GetStringArrayAny(settings, ["inputRoots", "input_roots"]))
        {
            var rootPath = ConvertImageLocalPath(root);
            if (!string.IsNullOrEmpty(rootPath) && Directory.Exists(rootPath))
            {
                rootPaths.Add(Path.GetFullPath(rootPath));
            }
        }

        return rootPaths;
    }

    private ImageGeneratedCatalog GetGeneratedCatalog(string outputRoot)
    {
        var catalog = new ImageGeneratedCatalog();
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
            if (!File.Exists(convertInfoPath))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(File.ReadAllText(convertInfoPath)) is not JsonObject payload)
                {
                    continue;
                }

                var source = GetObject(payload, "source") ?? new JsonObject();
                var itemId = GetStringAny(source, ["item_id", "itemId"], Path.GetFileName(directory));
                var sha256 = GetString(source, "sha256", string.Empty).ToLowerInvariant();
                var relativePath = GetStringAny(source, ["relative_path", "relativePath"], string.Empty);
                var sourcePath = ConvertImageLocalPath(GetStringAny(source, ["source_path", "sourcePath"], string.Empty));
                var sourceDisplayName = GetStringAny(
                    source,
                    ["display_name", "displayName"],
                    !string.IsNullOrEmpty(sourcePath) ? Path.GetFileName(sourcePath) : relativePath);
                var modifiedAt = GetStringAny(source, ["modified_at", "modifiedAt"], string.Empty);
                var sizeBytes = GetLongAny(source, ["size_bytes", "sizeBytes"], 0);
                if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(sha256))
                {
                    continue;
                }

                var row = new ImageCatalogRow(
                    itemId,
                    sha256,
                    relativePath,
                    sourcePath,
                    sourceDisplayName,
                    sizeBytes,
                    modifiedAt,
                    directory,
                    Path.Combine(directory, "timeline.json"),
                    convertInfoPath,
                    Path.Combine(directory, "image_record.json"));
                AddCatalogRow(catalog, row);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return catalog;
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

        var count = 0;
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(itemsRoot).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return 0;
        }

        foreach (var directory in directories)
        {
            if (File.Exists(Path.Combine(directory, "timeline.json"))
                || File.Exists(Path.Combine(directory, "image_record.json")))
            {
                count++;
            }
        }

        return count;
    }

    private static void AddCatalogRow(ImageGeneratedCatalog catalog, ImageCatalogRow row)
    {
        catalog.Rows.Add(row);

        if (!string.IsNullOrEmpty(row.RelativePath))
        {
            var key = row.Sha256 + "|" + row.RelativePath;
            catalog.ByKey.TryAdd(key, row);

            var relativeSizeKey = row.RelativePath + "|" + row.SizeBytes.ToString(CultureInfo.InvariantCulture);
            if (!catalog.ByRelativeSize.TryGetValue(relativeSizeKey, out var relativeSizeRows))
            {
                relativeSizeRows = [];
                catalog.ByRelativeSize[relativeSizeKey] = relativeSizeRows;
            }

            relativeSizeRows.Add(row);
        }

        var hashSizeKey = row.Sha256 + "|" + row.SizeBytes.ToString(CultureInfo.InvariantCulture);
        if (!catalog.ByHashSize.TryGetValue(hashSizeKey, out var hashSizeRows))
        {
            hashSizeRows = [];
            catalog.ByHashSize[hashSizeKey] = hashSizeRows;
        }

        hashSizeRows.Add(row);

        if (!catalog.ByHash.TryGetValue(row.Sha256, out var hashRows))
        {
            hashRows = [];
            catalog.ByHash[row.Sha256] = hashRows;
        }

        hashRows.Add(row);
    }

    private static ImageCatalogRow? FindGeneratedCatalogRow(
        ImageGeneratedCatalog catalog,
        string sha256,
        string relativePath,
        long sizeBytes)
    {
        var sha = ConvertTimelineText(sha256).ToLowerInvariant();
        if (string.IsNullOrEmpty(sha))
        {
            return null;
        }

        var key = sha + "|" + relativePath;
        if (!string.IsNullOrEmpty(relativePath) && catalog.ByKey.TryGetValue(key, out var keyRow))
        {
            return keyRow;
        }

        var hashSizeKey = sha + "|" + sizeBytes.ToString(CultureInfo.InvariantCulture);
        if (catalog.ByHashSize.TryGetValue(hashSizeKey, out var hashSizeRows) && hashSizeRows.Count > 0)
        {
            return hashSizeRows[0];
        }

        return catalog.ByHash.TryGetValue(sha, out var hashRows) && hashRows.Count > 0
            ? hashRows[0]
            : null;
    }

    private static ImageCatalogRow? FindGeneratedCatalogRowByRelativeSize(
        ImageGeneratedCatalog catalog,
        string relativePath,
        long sizeBytes)
    {
        var relative = ConvertTimelineText(relativePath);
        if (string.IsNullOrEmpty(relative))
        {
            return null;
        }

        var key = relative + "|" + sizeBytes.ToString(CultureInfo.InvariantCulture);
        return catalog.ByRelativeSize.TryGetValue(key, out var rows) && rows.Count > 0
            ? rows[0]
            : null;
    }

    private JsonObject ConvertSourceFileRow(ImageSourceRow sourceRow, ImageGeneratedCatalog catalog)
    {
        var sha256 = GetFileSha256(sourceRow.SourcePath);
        var catalogRow = FindGeneratedCatalogRow(
            catalog,
            sha256,
            sourceRow.RelativePath,
            sourceRow.SizeBytes);

        var itemId = catalogRow?.ItemId ?? string.Empty;
        var outputDirectory = catalogRow?.OutputDirectory ?? string.Empty;
        var timelinePath = catalogRow?.TimelinePath ?? string.Empty;
        var convertInfoPath = catalogRow?.ConvertInfoPath ?? string.Empty;
        var imageRecordPath = catalogRow?.ImageRecordPath ?? string.Empty;

        return new JsonObject
        {
            ["itemId"] = itemId,
            ["relativePath"] = sourceRow.RelativePath,
            ["sourcePath"] = sourceRow.SourcePath,
            ["sourceDisplayName"] = sourceRow.SourceDisplayName,
            ["sizeBytes"] = sourceRow.SizeBytes,
            ["modifiedAt"] = sourceRow.ModifiedAt,
            ["outputDirectory"] = outputDirectory,
            ["timelinePath"] = timelinePath,
            ["convertInfoPath"] = convertInfoPath,
            ["imageRecordPath"] = imageRecordPath,
            ["hasTimeline"] = !string.IsNullOrEmpty(timelinePath) && File.Exists(timelinePath),
            ["hasImageRecord"] = !string.IsNullOrEmpty(imageRecordPath) && File.Exists(imageRecordPath),
        };
    }

    private static JsonObject ConvertCatalogRow(ImageCatalogRow row)
    {
        return new JsonObject
        {
            ["itemId"] = row.ItemId,
            ["relativePath"] = row.RelativePath,
            ["sourcePath"] = row.SourcePath,
            ["sourceDisplayName"] = row.SourceDisplayName,
            ["sizeBytes"] = row.SizeBytes,
            ["modifiedAt"] = row.ModifiedAt,
            ["outputDirectory"] = row.OutputDirectory,
            ["timelinePath"] = row.TimelinePath,
            ["convertInfoPath"] = row.ConvertInfoPath,
            ["imageRecordPath"] = row.ImageRecordPath,
            ["hasTimeline"] = !string.IsNullOrEmpty(row.TimelinePath) && File.Exists(row.TimelinePath),
            ["hasImageRecord"] = !string.IsNullOrEmpty(row.ImageRecordPath) && File.Exists(row.ImageRecordPath),
        };
    }

    private static JsonObject ConvertTextBlock(JsonObject block, int index)
    {
        var confidence = GetObject(block, "confidence");
        var evidence = GetObject(block, "evidence");
        return new JsonObject
        {
            ["index"] = index,
            ["blockId"] = GetStringAny(block, ["block_id", "blockId"], string.Empty),
            ["text"] = GetString(block, "text", string.Empty),
            ["normalizedText"] = GetStringAny(block, ["normalized_text", "normalizedText"], string.Empty),
            ["role"] = GetString(block, "role", string.Empty),
            ["bboxNorm"] = GetDoubleArray(block, "bbox_norm"),
            ["confidenceScore"] = GetDoubleNode(GetNode(confidence, "score")),
            ["confidenceLevel"] = GetString(confidence, "level", string.Empty),
            ["evidenceChannel"] = GetString(evidence, "channel", string.Empty),
            ["evidenceStage"] = GetString(evidence, "stage", string.Empty),
        };
    }

    private static JsonObject ConvertVisualDescription(JsonObject imageRecord)
    {
        var visual = GetObject(imageRecord, "visual");
        var observations = new JsonArray();
        foreach (var node in GetArray(visual, "observations"))
        {
            var text = node is JsonObject obj
                ? GetStringAny(obj, ["text", "summary", "label"], ConvertNodeToString(node))
                : ConvertNodeToString(node);
            if (!string.IsNullOrWhiteSpace(text))
            {
                observations.Add(text);
            }
        }

        return new JsonObject
        {
            ["caption"] = GetString(visual, "caption", string.Empty),
            ["sceneSummary"] = GetStringAny(visual, ["scene_summary", "sceneSummary"], string.Empty),
            ["observations"] = observations,
        };
    }

    private static JsonObject ConvertLayoutSummary(JsonObject imageRecord)
    {
        var layout = GetObject(imageRecord, "layout");
        var colorPalette = new JsonArray();
        foreach (var node in GetArray(layout, "color_palette"))
        {
            if (node is JsonObject entry)
            {
                colorPalette.Add(ConvertColorPaletteEntry(entry));
            }
        }

        var grid = new JsonArray();
        foreach (var node in GetArray(layout, "grid"))
        {
            if (node is JsonObject cell)
            {
                grid.Add(ConvertGridCell(cell));
            }
        }

        var textRegions = new JsonArray();
        foreach (var node in GetArray(layout, "text_regions"))
        {
            if (node is JsonObject region)
            {
                textRegions.Add(ConvertTextRegion(region));
            }
        }

        return new JsonObject
        {
            ["coordinateSystem"] = GetStringAny(layout, ["coordinate_system", "coordinateSystem"], string.Empty),
            ["colorPalette"] = colorPalette,
            ["grid"] = grid,
            ["textRegions"] = textRegions,
            ["spatialRelationCount"] = GetArray(layout, "spatial_relations").Count,
        };
    }

    private static JsonObject ConvertColorPaletteEntry(JsonObject entry)
    {
        return new JsonObject
        {
            ["hex"] = GetString(entry, "hex", string.Empty),
            ["rgb"] = GetIntArray(entry, "rgb"),
            ["ratio"] = GetDoubleNode(GetNode(entry, "ratio")),
        };
    }

    private static JsonObject ConvertGridCell(JsonObject cell)
    {
        var averageColor = GetObject(cell, "average_color");
        return new JsonObject
        {
            ["cellId"] = GetStringAny(cell, ["cell_id", "cellId"], string.Empty),
            ["row"] = GetInt(cell, "row", 0),
            ["col"] = GetInt(cell, "col", 0),
            ["bboxNorm"] = GetDoubleArray(cell, "bbox_norm"),
            ["averageColor"] = new JsonObject
            {
                ["hex"] = GetString(averageColor, "hex", string.Empty),
                ["rgb"] = GetIntArray(averageColor, "rgb"),
            },
        };
    }

    private static JsonObject ConvertTextRegion(JsonObject region)
    {
        return new JsonObject
        {
            ["blockId"] = GetStringAny(region, ["block_id", "blockId"], string.Empty),
            ["text"] = GetString(region, "text", string.Empty),
            ["bboxNorm"] = GetDoubleArray(region, "bbox_norm"),
            ["zIndex"] = GetIntAny(region, ["z_index", "zIndex"], 0),
        };
    }

    private static JsonArray ConvertSearchKeywords(JsonObject imageRecord)
    {
        var search = GetObject(imageRecord, "search");
        return NewStringArrayUnique(GetStringArray(search, "keywords"));
    }

    private static JsonObject ConvertImageArtifacts(string outputDirectory, JsonObject? convertInfo)
    {
        var outputs = GetObject(convertInfo, "outputs");
        var normalizedImage = ResolveGeneratedOutputPath(
            outputDirectory,
            GetStringAny(outputs, ["normalized_image", "normalizedImage"], "artifacts/normalized_image.jpg"));
        var debugOverlay = ResolveGeneratedOutputPath(
            outputDirectory,
            GetStringAny(outputs, ["debug_overlay", "debugOverlay"], "artifacts/debug_overlay.jpg"));

        return new JsonObject
        {
            ["normalizedImagePath"] = normalizedImage,
            ["debugOverlayPath"] = debugOverlay,
            ["hasNormalizedImage"] = !string.IsNullOrEmpty(normalizedImage) && File.Exists(normalizedImage),
            ["hasDebugOverlay"] = !string.IsNullOrEmpty(debugOverlay) && File.Exists(debugOverlay),
        };
    }

    private static string ResolveGeneratedOutputPath(string outputDirectory, string relativeOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
        {
            return string.Empty;
        }

        try
        {
            var root = Path.GetFullPath(outputDirectory).TrimEnd('\\', '/');
            var candidate = Path.IsPathRooted(relativeOrAbsolutePath)
                ? Path.GetFullPath(relativeOrAbsolutePath)
                : Path.GetFullPath(Path.Combine(root, relativeOrAbsolutePath.Replace("/", "\\")));
            var candidateKey = GetNormalizedPathKey(candidate);
            var rootKey = GetNormalizedPathKey(root);
            return candidateKey.Equals(rootKey, StringComparison.OrdinalIgnoreCase)
                || candidateKey.StartsWith(rootKey + "\\", StringComparison.OrdinalIgnoreCase)
                ? candidate
                : string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static JsonObject ConvertRecordSummary(
        JsonObject imageRecord,
        JsonObject? timeline,
        JsonObject? convertInfo)
    {
        var asset = GetObject(imageRecord, "asset");
        var recordTimeline = GetObject(imageRecord, "timeline");
        var image = GetObject(imageRecord, "image");
        var quality = GetObject(imageRecord, "quality");
        var classification = GetObject(imageRecord, "classification");
        var text = GetObject(imageRecord, "text");
        var review = GetObject(imageRecord, "review");
        var convertSource = GetObject(convertInfo, "source");
        var timelineEvents = GetArray(timeline, "events");
        var firstEvent = timelineEvents.FirstOrDefault() as JsonObject;
        var blocks = GetArray(text, "blocks");

        var warnings = NewStringArrayUnique(
            GetStringArray(review, "warnings")
                .Concat(GetStringArray(quality, "warnings"))
                .Concat(GetStringArray(convertSource, "warnings")));

        var width = GetInt(image, "width", 0);
        if (width <= 0)
        {
            width = GetInt(convertSource, "width", 0);
        }

        var height = GetInt(image, "height", 0);
        if (height <= 0)
        {
            height = GetInt(convertSource, "height", 0);
        }

        var camera = GetObject(image, "camera");
        return new JsonObject
        {
            ["timelineAt"] = GetStringAny(recordTimeline, ["timeline_at", "timelineAt"], GetString(firstEvent, "time", string.Empty)),
            ["capturedAt"] = GetStringAny(recordTimeline, ["captured_at", "capturedAt"], GetString(convertSource, "captured_at", string.Empty)),
            ["modifiedAt"] = GetStringAny(recordTimeline, ["modified_at", "modifiedAt"], GetString(convertSource, "modified_at", string.Empty)),
            ["formatName"] = GetStringAny(asset, ["format_name", "formatName"], GetString(convertSource, "format_name", string.Empty)),
            ["width"] = width,
            ["height"] = height,
            ["orientation"] = GetString(image, "orientation", string.Empty),
            ["cameraMake"] = GetStringAny(camera, ["make"], GetString(convertSource, "camera_make", string.Empty)),
            ["cameraModel"] = GetStringAny(camera, ["model"], GetString(convertSource, "camera_model", string.Empty)),
            ["imageKind"] = GetStringAny(classification, ["image_kind", "imageKind"], string.Empty),
            ["contentTypes"] = NewStringArray(GetStringArrayAny(classification, ["content_types", "contentTypes"])),
            ["hasText"] = GetBoolAny(text, ["has_text", "hasText"], false),
            ["fullText"] = GetStringAny(text, ["full_text", "fullText"], string.Empty),
            ["ocrBlockCount"] = blocks.Count,
            ["brightnessLevel"] = GetStringAny(quality, ["brightness_level", "brightnessLevel"], string.Empty),
            ["contrastLevel"] = GetStringAny(quality, ["contrast_level", "contrastLevel"], string.Empty),
            ["brightness"] = GetDoubleNode(GetNode(quality, "brightness")),
            ["contrast"] = GetDoubleNode(GetNode(quality, "contrast")),
            ["needsReview"] = GetBoolAny(review, ["needs_review", "needsReview"], false),
            ["warnings"] = warnings,
        };
    }

    private static JsonObject? ReadImageJsonFile(string path)
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

    private string ConvertImageLocalPath(string? path)
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

        return Path.IsPathRooted(text) ? text : Path.Combine(productPath, text);
    }

    private string GetProductPath()
    {
        foreach (var product in _settings.ReadSettings().ProductRegistry.Products)
        {
            if (product.Id.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                return product.Path ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private string GetManagedImageDataDirectory()
    {
        var path = Path.Combine(_settings.GetDataRootDirectory(), "to_text", "image");
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private static string GetRelativePathFromRoots(string path, IReadOnlyList<string> rootPaths)
    {
        var resolvedPath = Path.GetFullPath(path);
        foreach (var rootPath in rootPaths)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                continue;
            }

            var resolvedRoot = Path.GetFullPath(rootPath).TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(resolvedRoot))
            {
                continue;
            }

            if (resolvedPath.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileName(resolvedPath);
            }

            var prefix = resolvedRoot + "\\";
            if (resolvedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return resolvedPath[prefix.Length..].Replace('\\', '/');
            }
        }

        return Path.GetFileName(resolvedPath);
    }

    private static string GetNormalizedPathKey(string path)
        => ConvertTimelineText(path).TrimEnd('\\', '/').Replace('/', '\\').ToLowerInvariant();

    private static string GetFileSha256(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool PathExists(string path)
        => !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

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

    private static bool GetBoolAny(JsonObject? source, string[] names, bool fallback)
    {
        foreach (var name in names)
        {
            if (!TryGetNode(source, name, out var node))
            {
                continue;
            }

            if (node?.GetValueKind() == JsonValueKind.True)
            {
                return true;
            }
            if (node?.GetValueKind() == JsonValueKind.False)
            {
                return false;
            }

            var text = ConvertNodeToString(node).ToLowerInvariant();
            return text switch
            {
                "true" or "1" or "yes" or "on" => true,
                "false" or "0" or "no" or "off" => false,
                _ => fallback,
            };
        }

        return fallback;
    }

    private static long GetLongAny(JsonObject? source, string[] names, long fallback)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is null)
            {
                continue;
            }

            if (node.GetValueKind() == JsonValueKind.Number)
            {
                try
                {
                    return node.GetValue<long>();
                }
                catch (FormatException)
                {
                }
            }

            return long.TryParse(ConvertNodeToString(node), out var parsed)
                ? parsed
                : fallback;
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
            var node = GetNode(source, name);
            if (node is null)
            {
                continue;
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

        return fallback;
    }

    private static double? GetDoubleNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node.GetValueKind() == JsonValueKind.Number)
        {
            try
            {
                return node.GetValue<double>();
            }
            catch (FormatException)
            {
            }
        }

        return double.TryParse(ConvertNodeToString(node), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static JsonArray GetDoubleArray(JsonObject? source, string name)
    {
        var result = new JsonArray();
        if (GetNode(source, name) is not JsonArray array)
        {
            return result;
        }

        foreach (var node in array)
        {
            var value = GetDoubleNode(node);
            if (value is double number)
            {
                result.Add(number);
            }
        }

        return result;
    }

    private static JsonArray GetIntArray(JsonObject? source, string name)
    {
        var result = new JsonArray();
        if (GetNode(source, name) is not JsonArray array)
        {
            return result;
        }

        foreach (var node in array)
        {
            if (node is null)
            {
                continue;
            }

            if (node.GetValueKind() == JsonValueKind.Number)
            {
                try
                {
                    result.Add(node.GetValue<int>());
                    continue;
                }
                catch (FormatException)
                {
                }
            }

            if (int.TryParse(ConvertNodeToString(node), out var parsed))
            {
                result.Add(parsed);
            }
        }

        return result;
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

    private static IEnumerable<string> GetStringArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(ConvertNodeToString)
            .Where(value => !string.IsNullOrEmpty(value));
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

    private static JsonArray NewStringArrayUnique(IEnumerable<string> values)
    {
        var array = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (seen.Add(value))
            {
                array.Add(value);
            }
        }

        return array;
    }

    private static List<JsonNode?> GetArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array ? array.ToList() : [];
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

    private sealed record ImageSourceRow(
        string SourcePath,
        string SourceDisplayName,
        string RelativePath,
        long SizeBytes,
        string ModifiedAt);

    private sealed record ImageCatalogRow(
        string ItemId,
        string Sha256,
        string RelativePath,
        string SourcePath,
        string SourceDisplayName,
        long SizeBytes,
        string ModifiedAt,
        string OutputDirectory,
        string TimelinePath,
        string ConvertInfoPath,
        string ImageRecordPath);

    private sealed class ImageGeneratedCatalog
    {
        public List<ImageCatalogRow> Rows { get; } = [];

        public Dictionary<string, ImageCatalogRow> ByKey { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<ImageCatalogRow>> ByRelativeSize { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<ImageCatalogRow>> ByHashSize { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<ImageCatalogRow>> ByHash { get; } = new(StringComparer.Ordinal);
    }
}
