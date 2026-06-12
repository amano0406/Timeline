using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed class TimelineStoreExportService
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimelineLocalApiOptions _options;
    private readonly TimelineSettingsService _settings;
    private readonly TimelineStoreService _store;
    private readonly TimelineOperationLogService _operations;

    public TimelineStoreExportService(
        TimelineLocalApiOptions options,
        TimelineSettingsService settings,
        TimelineStoreService store,
        TimelineOperationLogService operations)
    {
        _options = options;
        _settings = settings;
        _store = store;
        _operations = operations;
    }

    public TimelineStoreDownloadResponse CreateDownload()
    {
        var operationId = _operations.NewOperationId("web");
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "web",
            "Timeline",
            "timeline_export_download",
            "started",
            "Web operation started.");

        try
        {
            var result = CreateDownloadCore(operationId);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "Timeline",
                "timeline_export_download",
                "completed",
                "Web operation completed.",
                durationMs: durationMs,
                details: ConvertDownloadResultDetails(result));
            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "web",
                "Timeline",
                "timeline_export_download",
                "failed",
                ex.Message,
                durationMs: durationMs,
                stderr: ex.Message);
            throw;
        }
    }

    private TimelineStoreDownloadResponse CreateDownloadCore(string operationId)
    {
        var overview = _store.GetOverview();
        if (!overview.Available)
        {
            throw new InvalidOperationException("Timeline store has not been rebuilt yet. Rebuild the Timeline store first.");
        }

        var manifestPath = Path.Combine(_settings.GetStoreDirectory(), "manifest.json");
        var manifest = ReadManifest(manifestPath);
        var packagePath = ResolvePackagePath(GetString(manifest, "packagePath", string.Empty));
        if (string.IsNullOrEmpty(packagePath) || !Directory.Exists(packagePath))
        {
            throw new InvalidOperationException("Timeline store package was not found. Rebuild the Timeline store.");
        }

        var downloadRoot = GetExportDownloadRoot();
        var archivePath = Path.Combine(downloadRoot, $"Timeline-store-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var tempArchivePath = archivePath + ".tmp";

        try
        {
            if (File.Exists(tempArchivePath))
            {
                File.Delete(tempArchivePath);
            }

            CreateLlmReadyArchive(packagePath, tempArchivePath, manifest);
            var tempArchive = new FileInfo(tempArchivePath);
            if (tempArchive.Length <= 0)
            {
                throw new InvalidOperationException("Timeline store ZIP was empty. Rebuild the Timeline store.");
            }

            File.Move(tempArchivePath, archivePath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempArchivePath);
            throw;
        }

        var archive = new FileInfo(archivePath);

        var result = new TimelineStoreDownloadResponse
        {
            ArchivePath = archivePath,
            ArchiveSizeBytes = archive.Length,
            ItemCount = GetInt(manifest, "itemCount", 0),
            EventCount = GetInt(manifest, "eventCount", 0),
            Products = GetArray(manifest, "products"),
        };

        _operations.WriteOperationEvent(
            operationId,
            "web",
            "Timeline",
            "timeline_export_archive_created",
            "completed",
            "Timeline archive created.",
            details: ConvertDownloadResultDetails(result));

        return result;
    }

    private void CreateLlmReadyArchive(string packagePath, string archivePath, JsonObject manifest)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var addedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var summaries = ReadSummaryIndex();
        var itemCatalog = ReadItemCatalog(packagePath, summaries);
        var timelineDayCatalog = BuildTimelineDayCatalog(packagePath, itemCatalog);
        var packageStats = new ExportPackageStats
        {
            ItemCount = GetInt(manifest, "itemCount", 0),
            EventCount = GetInt(manifest, "eventCount", 0),
            ProductCount = GetArray(manifest, "products").Count,
            SummaryCount = summaries.Rows.Count,
            CatalogItemCount = itemCatalog.Count,
            TimelineDayCount = timelineDayCatalog.Count,
        };

        AddRawPackageFiles(archive, packagePath, addedEntries);
        AddSummaryFiles(archive, summaries, addedEntries);
        AddCatalogFiles(archive, itemCatalog, timelineDayCatalog, manifest, summaries, addedEntries);
        AddGuideFiles(archive, manifest, packageStats, summaries, addedEntries);
    }

    private static void AddRawPackageFiles(ZipArchive archive, string packagePath, HashSet<string> addedEntries)
    {
        foreach (var filePath in Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeZipEntryName(Path.GetRelativePath(packagePath, filePath));
            if (ShouldSkipPackageEntry(relativePath))
            {
                continue;
            }

            var entryName = relativePath.StartsWith("products/", StringComparison.OrdinalIgnoreCase)
                ? "raw/" + relativePath
                : relativePath;
            AddFileToArchive(archive, filePath, entryName, addedEntries);
        }

        AddTransformedTimelineFile(archive, Path.Combine(packagePath, "timeline", "items.jsonl"), "timeline/items.jsonl", addedEntries);
        AddTransformedTimelineFile(archive, Path.Combine(packagePath, "timeline", "events.jsonl"), "timeline/events.jsonl", addedEntries);
    }

    private static bool ShouldSkipPackageEntry(string relativePath)
    {
        return relativePath.Equals("README.md", StringComparison.OrdinalIgnoreCase)
            || relativePath.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            || relativePath.Equals("timeline/items.jsonl", StringComparison.OrdinalIgnoreCase)
            || relativePath.Equals("timeline/events.jsonl", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("source-downloads/", StringComparison.OrdinalIgnoreCase);
    }

    private void AddSummaryFiles(ZipArchive archive, SummaryIndex summaries, HashSet<string> addedEntries)
    {
        foreach (var row in summaries.Rows.Values)
        {
            var sourcePath = ResolvePackagePath(row.SourcePath);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                continue;
            }

            var product = SanitizePathSegment(row.Product);
            var itemId = SanitizePathSegment(row.ItemId);
            if (string.IsNullOrEmpty(product) || string.IsNullOrEmpty(itemId))
            {
                continue;
            }

            AddFileToArchive(archive, sourcePath, $"items/{product}/{itemId}/summary.json", addedEntries);
        }
    }

    private void AddCatalogFiles(
        ZipArchive archive,
        IReadOnlyDictionary<string, ItemCatalogRow> itemCatalog,
        IReadOnlyDictionary<string, TimelineDayCatalogRow> timelineDayCatalog,
        JsonObject manifest,
        SummaryIndex summaries,
        HashSet<string> addedEntries)
    {
        AddTextEntry(archive, "catalog/README.md", BuildCatalogReadme(), addedEntries);
        AddJsonLineEntry(archive, "catalog/items.jsonl", itemCatalog.Values
            .OrderBy(static item => item.SortStartAt)
            .ThenBy(static item => item.Product, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(static item => item.ToJson()), addedEntries);
        AddJsonLineEntry(archive, "catalog/timeline-days.jsonl", timelineDayCatalog.Values
            .OrderBy(static item => item.Date, StringComparer.Ordinal)
            .Select(static item => item.ToJson()), addedEntries);
        AddTextEntry(archive, "catalog/products.json", BuildProductsJson(manifest), addedEntries);
        AddTextEntry(archive, "catalog/summary-stats.json", BuildSummaryStatsJson(summaries, itemCatalog.Count, timelineDayCatalog.Count), addedEntries);
    }

    private static void AddGuideFiles(
        ZipArchive archive,
        JsonObject manifest,
        ExportPackageStats stats,
        SummaryIndex summaries,
        HashSet<string> addedEntries)
    {
        AddTextEntry(archive, "README.md", BuildReadme(stats), addedEntries);
        AddTextEntry(archive, "entrypoint.json", BuildEntrypointJson(manifest, stats, summaries), addedEntries);
        AddTextEntry(archive, "search-recipes.json", BuildSearchRecipesJson(), addedEntries);
        AddTextEntry(archive, "timeline/README.md", BuildTimelineReadme(), addedEntries);
        AddTextEntry(archive, "raw/README.md", BuildRawReadme(), addedEntries);
        AddTextEntry(archive, "manifest.json", BuildCleanManifestJson(manifest, stats), addedEntries);
    }

    private SummaryIndex ReadSummaryIndex()
    {
        var summaryRoot = GetItemSummaryRoot();
        var indexJsonlPath = Path.Combine(summaryRoot, "index.jsonl");
        var result = new SummaryIndex
        {
            SummaryRoot = summaryRoot,
            IndexJsonlPath = File.Exists(indexJsonlPath) ? indexJsonlPath : string.Empty,
        };

        if (string.IsNullOrEmpty(result.IndexJsonlPath))
        {
            return result;
        }

        foreach (var line in File.ReadLines(result.IndexJsonlPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var node = JsonNode.Parse(line) as JsonObject;
                if (node is null)
                {
                    continue;
                }

                var row = SummaryRow.FromJson(node);
                if (string.IsNullOrWhiteSpace(row.Product) || string.IsNullOrWhiteSpace(row.ItemId))
                {
                    continue;
                }

                result.Rows[BuildItemKey(row.Product, row.ItemId)] = row;
                result.GetProduct(row.Product, row.ProductName).Count++;
            }
            catch (JsonException)
            {
                result.InvalidLineCount++;
            }
        }

        return result;
    }

    private static Dictionary<string, ItemCatalogRow> ReadItemCatalog(string packagePath, SummaryIndex summaries)
    {
        var itemsPath = Path.Combine(packagePath, "timeline", "items.jsonl");
        var result = new Dictionary<string, ItemCatalogRow>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(itemsPath))
        {
            return result;
        }

        foreach (var line in File.ReadLines(itemsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? item;
            try
            {
                item = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (item is null)
            {
                continue;
            }

            var product = GetString(item, "product", "unknown");
            var itemId = GetString(item, "itemId", string.Empty);
            if (string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            summaries.Rows.TryGetValue(BuildItemKey(product, itemId), out var summary);
            var createdAt = GetString(item, "createdAt", string.Empty);
            var updatedAt = GetString(item, "updatedAt", string.Empty);
            var row = new ItemCatalogRow
            {
                ItemId = itemId,
                Product = product,
                ProductName = GetString(item, "productName", product),
                ItemType = GetString(item, "itemType", product),
                Title = GetString(item, "title", itemId),
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                EventCount = GetInt(item, "eventCount", 0),
                SummaryShort = summary?.CompressedSummary ?? summary?.BriefSummary ?? string.Empty,
                SummaryLong = summary?.BriefSummary ?? string.Empty,
                SummaryStatus = summary?.SummaryStatus ?? "not_available",
                SummaryPath = summary is null ? string.Empty : $"items/{SanitizePathSegment(product)}/{SanitizePathSegment(itemId)}/summary.json",
                RawRootPath = $"raw/products/{SanitizePathSegment(product)}/items/{SanitizePathSegment(itemId)}/",
                RawTimelinePath = ConvertPackagePath(GetString(item["sourceRef"] as JsonObject, "timelinePath", string.Empty)),
                RawConvertInfoPath = ConvertPackagePath(GetString(item["sourceRef"] as JsonObject, "convertInfoPath", string.Empty)),
                SortStartAt = ParseSortTime(createdAt) ?? DateTimeOffset.MaxValue,
                Time = BuildItemTime(createdAt, updatedAt),
            };
            result[BuildItemKey(product, itemId)] = row;
        }

        return result;
    }

    private static Dictionary<string, TimelineDayCatalogRow> BuildTimelineDayCatalog(
        string packagePath,
        IReadOnlyDictionary<string, ItemCatalogRow> itemCatalog)
    {
        var result = new Dictionary<string, TimelineDayCatalogRow>(StringComparer.Ordinal);
        var eventsPath = Path.Combine(packagePath, "timeline", "events.jsonl");

        foreach (var item in itemCatalog.Values)
        {
            var date = GetLocalDateKey(item.CreatedAt);
            if (string.IsNullOrEmpty(date))
            {
                continue;
            }

            var day = GetTimelineDay(result, date);
            day.AddItem(item, "item_anchor_on_this_day", item.CreatedAt, item.UpdatedAt, eventCountOnDate: 0);
        }

        if (!File.Exists(eventsPath))
        {
            return result;
        }

        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? evt;
            try
            {
                evt = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (evt is null)
            {
                continue;
            }

            var time = evt["time"] as JsonObject;
            var absoluteStartAt = GetString(time, "absoluteStartAt", string.Empty);
            var date = GetLocalDateKey(absoluteStartAt);
            if (string.IsNullOrEmpty(date))
            {
                continue;
            }

            var product = GetString(evt, "product", "unknown");
            var itemId = GetString(evt, "itemId", string.Empty);
            if (string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            if (!itemCatalog.TryGetValue(BuildItemKey(product, itemId), out var item))
            {
                continue;
            }

            var day = GetTimelineDay(result, date);
            day.AddItem(item, "active_on_this_day", absoluteStartAt, GetString(time, "absoluteEndAt", string.Empty), eventCountOnDate: 1);
            day.AddEventType(GetString(evt, "eventType", "unknown"));
        }

        return result;
    }

    private static TimelineDayCatalogRow GetTimelineDay(Dictionary<string, TimelineDayCatalogRow> days, string date)
    {
        if (!days.TryGetValue(date, out var day))
        {
            day = new TimelineDayCatalogRow { Date = date };
            days[date] = day;
        }

        return day;
    }

    private static JsonObject BuildItemTime(string createdAt, string updatedAt)
    {
        var hasStart = !string.IsNullOrWhiteSpace(createdAt);
        return new JsonObject
        {
            ["startAt"] = hasStart ? createdAt : null,
            ["endAt"] = string.IsNullOrWhiteSpace(updatedAt) ? (hasStart ? createdAt : null) : updatedAt,
            ["placement"] = hasStart ? "absolute_item_anchor" : "unknown",
            ["confidence"] = hasStart ? "item_created_at" : "unknown",
        };
    }

    private static string ConvertPackagePath(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return string.Empty;
        }

        var normalized = NormalizeZipEntryName(packagePath);
        return normalized.StartsWith("products/", StringComparison.OrdinalIgnoreCase)
            ? "raw/" + normalized
            : normalized;
    }

    private string GetItemSummaryRoot()
    {
        return Path.Combine(_settings.GetStoreDirectory(), "derived", "item_summaries");
    }

    private static string BuildReadme(ExportPackageStats stats)
    {
        return $"""
# Timeline LLM Export

このZIPは、Timelineで取り込んだ作業・会話・画面・AI利用履歴をLLMに渡すためのエクスポートです。

## 最初に読む入口

- `entrypoint.json`
  - ZIP全体の構造、読む順番、主要インデックスを定義しています。
- `search-recipes.json`
  - 目的別の探し方の例です。
- `catalog/items.jsonl`
  - 1行1素材の軽量カタログです。まずここで候補を絞ります。
- `catalog/timeline-days.jsonl`
  - 日付単位で素材を探すための索引です。

## 読み方

1. 話題やキーワードで探す場合は `catalog/items.jsonl` を検索します。
2. 日付から探す場合は `catalog/timeline-days.jsonl` を見ます。
3. 候補が見つかったら `items/<product>/<itemId>/summary.json` を読みます。
4. 証跡や詳細が必要な場合は `raw/products/` または `timeline/events.jsonl` を確認します。

## 規模

- 素材: {stats.ItemCount:N0}件
- 時系列イベント: {stats.EventCount:N0}件
- カタログ素材: {stats.CatalogItemCount:N0}件
- 概要: {stats.SummaryCount:N0}件
- 日付索引: {stats.TimelineDayCount:N0}日

## 含めていないもの

- `source-downloads/`
  - サブ製品から取得した元ZIPです。
  - 内容は `raw/products/` に展開済みで、LLM用途では重複するため含めていません。
""";
    }

    private static string BuildCatalogReadme()
    {
        return """
# catalog

LLMが最初に読むための軽量インデックスです。

- `items.jsonl`
  - 1行1素材です。音声、動画、画像、ChatGPTスレッド、Windows Codexスレッドなどを同じ形式で探せます。
- `timeline-days.jsonl`
  - 日付単位の索引です。ある日の作業文脈を探すときに使います。
- `products.json`
  - 製品別の件数と詳細パスです。
- `summary-stats.json`
  - 概要生成済み件数などの統計です。
""";
    }

    private static string BuildTimelineReadme()
    {
        return """
# timeline

Timelineとして統合した素材一覧と時系列イベントです。

- `items.jsonl`
  - 素材単位のメタ情報です。
- `events.jsonl`
  - 詳細な時系列イベントです。巨大なため、最初から読むのではなく、`catalog/items.jsonl` や `catalog/timeline-days.jsonl` で候補を絞ってから使います。

注意:
動画など一部のイベントは、絶対時刻ではなく素材内の相対時刻だけを持つ場合があります。時間の信頼度は `catalog/items.jsonl` 側の `time` を確認してください。
""";
    }

    private static string BuildRawReadme()
    {
        return """
# raw

各サブ製品から取得した詳細データを展開したものです。

- `raw/products/audio/`
- `raw/products/video/`
- `raw/products/image/`
- `raw/products/chatgpt/`
- `raw/products/windows-codex/`
- `raw/products/pc/`

LLMは通常、まず `catalog/` と `items/` を読み、根拠確認が必要になった場合だけ `raw/` に進みます。
""";
    }

    private static string BuildEntrypointJson(JsonObject manifest, ExportPackageStats stats, SummaryIndex summaries)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactType"] = "timeline_llm_package",
            ["generatedAt"] = DateTimeOffset.Now.ToString("O"),
            ["purpose"] = "Timelineで取り込んだ素材を、LLMが素材単位・時間軸・詳細証跡の順に探索できるようにしたZIPです。",
            ["recommendedFlow"] = new JsonArray
            {
                "まず catalog/items.jsonl で素材候補を探す",
                "日付で探す場合は catalog/timeline-days.jsonl を見る",
                "候補が見つかったら items/<product>/<itemId>/summary.json を読む",
                "詳細な発話・OCR・メッセージが必要なら timeline/events.jsonl または raw/products/ を見る",
                "検索方法に迷う場合は search-recipes.json の手順を使う",
            },
            ["primaryIndexes"] = new JsonObject
            {
                ["items"] = "catalog/items.jsonl",
                ["timelineDays"] = "catalog/timeline-days.jsonl",
                ["products"] = "catalog/products.json",
                ["searchRecipes"] = "search-recipes.json",
            },
            ["layout"] = new JsonObject
            {
                ["catalog"] = "探索用の軽量インデックス",
                ["items"] = "素材単位の概要",
                ["timeline"] = "統合済みの素材一覧と詳細イベント",
                ["raw"] = "各サブ製品の展開済み詳細データ",
            },
            ["counts"] = new JsonObject
            {
                ["items"] = stats.ItemCount,
                ["events"] = stats.EventCount,
                ["catalogItems"] = stats.CatalogItemCount,
                ["summaries"] = stats.SummaryCount,
                ["timelineDays"] = stats.TimelineDayCount,
                ["products"] = stats.ProductCount,
            },
            ["summaryProducts"] = BuildSummaryProductJson(summaries),
            ["products"] = BuildSanitizedProducts(manifest),
            ["excludedPaths"] = new JsonArray
            {
                new JsonObject
                {
                    ["path"] = "source-downloads/",
                    ["reason"] = "サブ製品から取得した元ZIP。raw/products/ に展開済みで、LLM用途では重複するため除外。",
                },
            },
        };

        return root.ToJsonString(ExportJsonOptions);
    }

    private static string BuildCleanManifestJson(JsonObject manifest, ExportPackageStats stats)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactType"] = "timeline_llm_package_manifest",
            ["generatedAt"] = DateTimeOffset.Now.ToString("O"),
            ["itemCount"] = stats.ItemCount,
            ["eventCount"] = stats.EventCount,
            ["catalogItemCount"] = stats.CatalogItemCount,
            ["summaryCount"] = stats.SummaryCount,
            ["timelineDayCount"] = stats.TimelineDayCount,
            ["products"] = BuildSanitizedProducts(manifest),
            ["paths"] = new JsonObject
            {
                ["entrypoint"] = "entrypoint.json",
                ["searchRecipes"] = "search-recipes.json",
                ["catalogItems"] = "catalog/items.jsonl",
                ["timelineDays"] = "catalog/timeline-days.jsonl",
                ["timelineItems"] = "timeline/items.jsonl",
                ["timelineEvents"] = "timeline/events.jsonl",
                ["rawProducts"] = "raw/products/",
            },
        };

        return root.ToJsonString(ExportJsonOptions);
    }

    private static string BuildProductsJson(JsonObject manifest)
    {
        return BuildSanitizedProducts(manifest).ToJsonString(ExportJsonOptions);
    }

    private static JsonArray BuildSanitizedProducts(JsonObject manifest)
    {
        var result = new JsonArray();
        foreach (var productNode in GetArray(manifest, "products"))
        {
            if (productNode is not JsonObject product)
            {
                continue;
            }

            var productId = GetString(product, "productId", "unknown");
            result.Add(new JsonObject
            {
                ["productId"] = productId,
                ["displayName"] = GetString(product, "displayName", productId),
                ["included"] = GetString(product, "included", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                ["itemCount"] = GetInt(product, "itemCount", 0),
                ["eventCount"] = GetInt(product, "eventCount", 0),
                ["detailPath"] = $"raw/products/{SanitizePathSegment(productId)}/",
            });
        }

        return result;
    }

    private static string BuildSummaryStatsJson(SummaryIndex summaries, int catalogItemCount, int timelineDayCount)
    {
        var root = new JsonObject
        {
            ["summaryCount"] = summaries.Rows.Count,
            ["invalidLineCount"] = summaries.InvalidLineCount,
            ["catalogItemCount"] = catalogItemCount,
            ["timelineDayCount"] = timelineDayCount,
            ["products"] = BuildSummaryProductJson(summaries),
        };

        return root.ToJsonString(ExportJsonOptions);
    }

    private static JsonArray BuildSummaryProductJson(SummaryIndex summaries)
    {
        var result = new JsonArray();
        foreach (var product in summaries.Products.Values.OrderBy(static item => item.Product))
        {
            result.Add(new JsonObject
            {
                ["product"] = product.Product,
                ["productName"] = product.ProductName,
                ["count"] = product.Count,
            });
        }

        return result;
    }

    private static string BuildSearchRecipesJson()
    {
        var recipes = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "話題から素材を探す",
                ["userIntentExample"] = "Dockerを外す話をしていたスレッドや録画を探したい",
                ["steps"] = new JsonArray
                {
                    "catalog/items.jsonl の title, summaryShort, summaryLong を検索する",
                    "product が chatgpt, windows-codex, audio, video の候補を優先する",
                    "候補の summaryPath を読み、目的に近いか確認する",
                    "根拠が必要なら rawPath または timeline/events.jsonl を確認する",
                },
                ["expectedHit"] = "itemId, product, title, summaryPath, rawPath",
            },
            new JsonObject
            {
                ["name"] = "日付から作業文脈を探す",
                ["userIntentExample"] = "2026-05-13に何をしていたか知りたい",
                ["steps"] = new JsonArray
                {
                    "catalog/timeline-days.jsonl で date=2026-05-13 を探す",
                    "items 配列の title と summaryShort を確認する",
                    "関連しそうな素材の summaryPath を読む",
                    "詳細な時系列が必要な場合だけ timeline/events.jsonl を itemId で絞る",
                },
                ["expectedHit"] = "date, itemCount, products, items",
            },
            new JsonObject
            {
                ["name"] = "音声・動画の発話を探す",
                ["userIntentExample"] = "動画配信プラットフォームについて話していた箇所を探したい",
                ["steps"] = new JsonArray
                {
                    "catalog/items.jsonl で product=audio または product=video を優先して検索する",
                    "summaryShort と summaryLong で候補を絞る",
                    "rawPath の詳細JSONまたは timeline/events.jsonl を itemId で絞る",
                    "動画は相対時刻のみの場合があるため time.placement と time.confidence を確認する",
                },
                ["expectedHit"] = "itemId, title, summaryPath, rawPath, time",
            },
            new JsonObject
            {
                ["name"] = "画像やOCRから探す",
                ["userIntentExample"] = "OCRで特定の文字が取れている画像を探したい",
                ["steps"] = new JsonArray
                {
                    "catalog/items.jsonl で product=image を検索する",
                    "summaryShort と summaryLong を見る",
                    "候補の summaryPath を確認する",
                    "OCR座標や詳細が必要なら rawPath を読む",
                },
                ["expectedHit"] = "画像 itemId, title, summaryPath, rawPath",
            },
            new JsonObject
            {
                ["name"] = "証跡確認をする",
                ["userIntentExample"] = "概要ではなく、元の発話やメッセージを確認したい",
                ["steps"] = new JsonArray
                {
                    "catalog/items.jsonl で itemId を特定する",
                    "timeline/events.jsonl を product と itemId で絞る",
                    "必要なら raw/products/<product>/items/<itemId>/ を確認する",
                },
                ["expectedHit"] = "詳細イベント、元データJSON、変換情報",
            },
        };

        return recipes.ToJsonString(ExportJsonOptions);
    }

    private static void AddFileToArchive(ZipArchive archive, string sourcePath, string entryName, HashSet<string> addedEntries)
    {
        var normalizedEntryName = NormalizeZipEntryName(entryName);
        if (string.IsNullOrWhiteSpace(normalizedEntryName) || !addedEntries.Add(normalizedEntryName))
        {
            return;
        }

        archive.CreateEntryFromFile(sourcePath, normalizedEntryName, CompressionLevel.Optimal);
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string content, HashSet<string> addedEntries)
    {
        var normalizedEntryName = NormalizeZipEntryName(entryName);
        if (string.IsNullOrWhiteSpace(normalizedEntryName) || !addedEntries.Add(normalizedEntryName))
        {
            return;
        }

        var entry = archive.CreateEntry(normalizedEntryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void AddJsonLineEntry(
        ZipArchive archive,
        string entryName,
        IEnumerable<JsonObject> rows,
        HashSet<string> addedEntries)
    {
        var normalizedEntryName = NormalizeZipEntryName(entryName);
        if (string.IsNullOrWhiteSpace(normalizedEntryName) || !addedEntries.Add(normalizedEntryName))
        {
            return;
        }

        var entry = archive.CreateEntry(normalizedEntryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var row in rows)
        {
            writer.WriteLine(row.ToJsonString(JsonLineOptions));
        }
    }

    private static void AddTransformedTimelineFile(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        HashSet<string> addedEntries)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var normalizedEntryName = NormalizeZipEntryName(entryName);
        if (string.IsNullOrWhiteSpace(normalizedEntryName) || !addedEntries.Add(normalizedEntryName))
        {
            return;
        }

        var entry = archive.CreateEntry(normalizedEntryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var line in File.ReadLines(sourcePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var node = JsonNode.Parse(line);
                RewritePackageReferences(node);
                writer.WriteLine(node?.ToJsonString(JsonLineOptions) ?? line);
            }
            catch (JsonException)
            {
                writer.WriteLine(line);
            }
        }
    }

    private static void RewritePackageReferences(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(static property => property.Key).ToArray())
                {
                    var child = obj[key];
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = JsonValue.Create(ConvertPackagePath(text));
                    }
                    else
                    {
                        RewritePackageReferences(child);
                    }
                }

                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var child = array[index];
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[index] = JsonValue.Create(ConvertPackagePath(text));
                    }
                    else
                    {
                        RewritePackageReferences(child);
                    }
                }

                break;
        }
    }

    private string ResolvePackagePath(string packagePath)
    {
        var text = ConvertTimelineText(packagePath);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var localPath = TimelinePathConverter.ConvertTimelineWindowsPath(text, _options);
        if (string.IsNullOrEmpty(localPath))
        {
            localPath = text;
        }

        return Path.GetFullPath(Path.IsPathRooted(localPath)
            ? localPath
            : Path.Combine(_options.TimelineProductPath, localPath));
    }

    private string GetExportDownloadRoot()
    {
        var root = Path.Combine(_settings.GetWorkDirectory(), "downloads", "timeline");
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static JsonObject ReadManifest(string manifestPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
                ?? throw new InvalidOperationException("Timeline store manifest was empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Timeline store could not be read. Rebuild the Timeline store.", ex);
        }
    }

    private static JsonObject ConvertDownloadResultDetails(TimelineStoreDownloadResponse result)
    {
        return new JsonObject
        {
            ["archivePath"] = result.ArchivePath,
            ["archiveSizeBytes"] = result.ArchiveSizeBytes,
            ["itemCount"] = result.ItemCount,
            ["eventCount"] = result.EventCount,
            ["products"] = result.Products.DeepClone(),
        };
    }

    private static JsonArray GetArray(JsonObject? source, string name)
    {
        var node = GetNode(source, name);
        return node is JsonArray array
            ? array.DeepClone().AsArray()
            : [];
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

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return ConvertTimelineText(node.GetValue<object>());
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static int GetInt(JsonObject? source, string name, int fallback)
    {
        var node = GetNode(source, name);
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

            return int.TryParse(ConvertTimelineText(node.GetValue<object>()), out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
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

    private static string NormalizeZipEntryName(string entryName)
    {
        return entryName.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .TrimStart('/');
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : ch);
        }

        return builder.ToString();
    }

    private static string BuildItemKey(string product, string itemId)
    {
        return product + "\u001f" + itemId;
    }

    private static DateTimeOffset? ParseSortTime(string text)
    {
        return DateTimeOffset.TryParse(text, out var parsed)
            ? parsed
            : null;
    }

    private static string GetLocalDateKey(string text)
    {
        return DateTimeOffset.TryParse(text, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd")
            : string.Empty;
    }

    private sealed class ExportPackageStats
    {
        public int ItemCount { get; init; }
        public int EventCount { get; init; }
        public int ProductCount { get; init; }
        public int SummaryCount { get; init; }
        public int CatalogItemCount { get; init; }
        public int TimelineDayCount { get; init; }
    }

    private sealed class SummaryIndex
    {
        public string SummaryRoot { get; init; } = string.Empty;
        public string IndexJsonlPath { get; init; } = string.Empty;
        public int InvalidLineCount { get; set; }
        public Dictionary<string, SummaryRow> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ProductSummaryStats> Products { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ProductSummaryStats GetProduct(string product, string productName)
        {
            if (!Products.TryGetValue(product, out var stats))
            {
                stats = new ProductSummaryStats
                {
                    Product = product,
                    ProductName = string.IsNullOrWhiteSpace(productName) ? product : productName,
                };
                Products[product] = stats;
            }

            return stats;
        }
    }

    private sealed class SummaryRow
    {
        public string Product { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public string ItemId { get; init; } = string.Empty;
        public string ItemType { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string BriefSummary { get; init; } = string.Empty;
        public string CompressedSummary { get; init; } = string.Empty;
        public string SummaryStatus { get; init; } = string.Empty;
        public string SourcePath { get; init; } = string.Empty;

        public static SummaryRow FromJson(JsonObject source)
        {
            return new SummaryRow
            {
                Product = GetString(source, "product", string.Empty),
                ProductName = GetString(source, "productName", string.Empty),
                ItemId = GetString(source, "itemId", string.Empty),
                ItemType = GetString(source, "itemType", string.Empty),
                Title = GetString(source, "title", string.Empty),
                BriefSummary = GetString(source, "briefSummary", string.Empty),
                CompressedSummary = GetString(source, "compressedSummary", string.Empty),
                SummaryStatus = GetString(source, "summaryStatus", string.Empty),
                SourcePath = GetString(source, "path", string.Empty),
            };
        }
    }

    private sealed class ProductSummaryStats
    {
        public string Product { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class ItemCatalogRow
    {
        public string ItemId { get; init; } = string.Empty;
        public string Product { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public string ItemType { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string CreatedAt { get; init; } = string.Empty;
        public string UpdatedAt { get; init; } = string.Empty;
        public int EventCount { get; init; }
        public string SummaryShort { get; init; } = string.Empty;
        public string SummaryLong { get; init; } = string.Empty;
        public string SummaryStatus { get; init; } = string.Empty;
        public string SummaryPath { get; init; } = string.Empty;
        public string RawRootPath { get; init; } = string.Empty;
        public string RawTimelinePath { get; init; } = string.Empty;
        public string RawConvertInfoPath { get; init; } = string.Empty;
        public DateTimeOffset SortStartAt { get; init; }
        public JsonObject Time { get; init; } = [];

        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["itemId"] = ItemId,
                ["product"] = Product,
                ["productName"] = ProductName,
                ["itemType"] = ItemType,
                ["title"] = Title,
                ["time"] = Time.DeepClone(),
                ["eventCount"] = EventCount,
                ["summaryShort"] = SummaryShort,
                ["summaryLong"] = SummaryLong,
                ["summaryStatus"] = SummaryStatus,
                ["paths"] = new JsonObject
                {
                    ["summary"] = string.IsNullOrEmpty(SummaryPath) ? null : SummaryPath,
                    ["rawRoot"] = RawRootPath,
                    ["rawTimeline"] = string.IsNullOrEmpty(RawTimelinePath) ? null : RawTimelinePath,
                    ["rawConvertInfo"] = string.IsNullOrEmpty(RawConvertInfoPath) ? null : RawConvertInfoPath,
                    ["timelineItems"] = "timeline/items.jsonl",
                    ["timelineEvents"] = "timeline/events.jsonl",
                },
            };
        }
    }

    private sealed class TimelineDayCatalogRow
    {
        private readonly Dictionary<string, TimelineDayItemRow> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _eventTypes = new(StringComparer.OrdinalIgnoreCase);

        public string Date { get; init; } = string.Empty;

        public void AddItem(ItemCatalogRow item, string relation, string startAt, string endAt, int eventCountOnDate)
        {
            var key = BuildItemKey(item.Product, item.ItemId);
            if (!_items.TryGetValue(key, out var existing))
            {
                existing = new TimelineDayItemRow
                {
                    ItemId = item.ItemId,
                    Product = item.Product,
                    ProductName = item.ProductName,
                    Title = item.Title,
                    SummaryShort = item.SummaryShort,
                    SummaryPath = item.SummaryPath,
                    RawPath = item.RawRootPath,
                    Relation = relation,
                    FirstAt = startAt,
                    LastAt = string.IsNullOrWhiteSpace(endAt) ? startAt : endAt,
                };
                _items[key] = existing;
            }

            existing.EventCountOnDate += eventCountOnDate;
            existing.Relation = MergeRelation(existing.Relation, relation);
            existing.FirstAt = Earlier(existing.FirstAt, startAt);
            existing.LastAt = Later(existing.LastAt, string.IsNullOrWhiteSpace(endAt) ? startAt : endAt);
        }

        public void AddEventType(string eventType)
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                return;
            }

            _eventTypes[eventType] = _eventTypes.TryGetValue(eventType, out var count) ? count + 1 : 1;
        }

        public JsonObject ToJson()
        {
            var productCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items.Values)
            {
                productCounts[item.Product] = productCounts.TryGetValue(item.Product, out var count) ? count + 1 : 1;
            }

            return new JsonObject
            {
                ["date"] = Date,
                ["itemCount"] = _items.Count,
                ["products"] = new JsonArray(productCounts
                    .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(static item => new JsonObject
                    {
                        ["product"] = item.Key,
                        ["count"] = item.Value,
                    })
                    .ToArray<JsonNode?>()),
                ["eventTypes"] = new JsonArray(_eventTypes
                    .OrderByDescending(static item => item.Value)
                    .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .Select(static item => new JsonObject
                    {
                        ["eventType"] = item.Key,
                        ["count"] = item.Value,
                    })
                    .ToArray<JsonNode?>()),
                ["items"] = new JsonArray(_items.Values
                    .OrderBy(static item => item.FirstAt, StringComparer.Ordinal)
                    .ThenBy(static item => item.Product, StringComparer.OrdinalIgnoreCase)
                    .Select(static item => item.ToJson())
                    .ToArray<JsonNode?>()),
            };
        }

        private static string MergeRelation(string current, string next)
        {
            if (string.IsNullOrEmpty(current) || current == next)
            {
                return next;
            }

            return current.Contains(next, StringComparison.OrdinalIgnoreCase)
                ? current
                : current + "," + next;
        }

        private static string Earlier(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left))
            {
                return right;
            }

            if (string.IsNullOrWhiteSpace(right))
            {
                return left;
            }

            return string.CompareOrdinal(left, right) <= 0 ? left : right;
        }

        private static string Later(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left))
            {
                return right;
            }

            if (string.IsNullOrWhiteSpace(right))
            {
                return left;
            }

            return string.CompareOrdinal(left, right) >= 0 ? left : right;
        }
    }

    private sealed class TimelineDayItemRow
    {
        public string ItemId { get; init; } = string.Empty;
        public string Product { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string SummaryShort { get; init; } = string.Empty;
        public string SummaryPath { get; init; } = string.Empty;
        public string RawPath { get; init; } = string.Empty;
        public string Relation { get; set; } = string.Empty;
        public string FirstAt { get; set; } = string.Empty;
        public string LastAt { get; set; } = string.Empty;
        public int EventCountOnDate { get; set; }

        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["itemId"] = ItemId,
                ["product"] = Product,
                ["productName"] = ProductName,
                ["title"] = Title,
                ["relation"] = Relation,
                ["firstAt"] = FirstAt,
                ["lastAt"] = LastAt,
                ["eventCountOnDate"] = EventCountOnDate,
                ["summaryShort"] = SummaryShort,
                ["paths"] = new JsonObject
                {
                    ["summary"] = string.IsNullOrEmpty(SummaryPath) ? null : SummaryPath,
                    ["raw"] = RawPath,
                },
            };
        }
    }
}

public sealed class TimelineStoreDownloadResponse
{
    [JsonPropertyName("archivePath")]
    public string ArchivePath { get; set; } = "";

    [JsonPropertyName("archiveSizeBytes")]
    public long ArchiveSizeBytes { get; set; }

    [JsonPropertyName("itemCount")]
    public int ItemCount { get; set; }

    [JsonPropertyName("eventCount")]
    public int EventCount { get; set; }

    [JsonPropertyName("products")]
    public JsonArray Products { get; set; } = [];
}
