using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public static class TimelineUninstallPlanService
{
    public static TimelineUninstallPlanResponse GetPlan(string timelineRoot)
    {
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(timelineRoot)
            ? Directory.GetCurrentDirectory()
            : timelineRoot);
        var dataRoot = ResolveDataRoot(root);
        var settingsPath = Path.Combine(root, "settings.json");
        var warnings = new List<TimelineUninstallPlanMessage>();

        if (IsPathUnder(dataRoot, root))
        {
            warnings.Add(new TimelineUninstallPlanMessage
            {
                Code = "data_inside_app_root",
                Message = "Timelineデータがアプリ本体の中にあります。アプリだけ削除する場合は、データを残す処理が必要です。",
            });
        }

        if (File.Exists(settingsPath))
        {
            warnings.Add(new TimelineUninstallPlanMessage
            {
                Code = "settings_inside_app_root",
                Message = "settings.json がアプリ本体の中にあります。再インストール後も設定を使う場合は保持が必要です。",
            });
        }

        return new TimelineUninstallPlanResponse
        {
            ProductId = "timeline",
            ProductName = "Timeline",
            State = "planned",
            Mode = "read_only",
            CanExecute = false,
            RequiresExplicitConfirmation = true,
            TimelineRoot = root,
            DataRoot = dataRoot,
            SettingsPath = settingsPath,
            Levels =
            [
                NewLevel(
                    "app_only",
                    "アプリ本体だけ削除",
                    "Timeline の実行ファイルだけを削除し、設定・素材・生成データ・Dockerリソースは残します。",
                    destructive: false,
                    recommendedDefault: true,
                    items: BuildApplicationItems(root)),
                NewLevel(
                    "app_and_settings",
                    "アプリ本体と設定を削除",
                    "アプリ本体に加えて settings.json を削除します。再インストール時は初期設定からやり直します。",
                    destructive: true,
                    recommendedDefault: false,
                    items: BuildApplicationItems(root).Concat([
                        NewItem("settings", settingsPath, "file", exists: File.Exists(settingsPath), defaultDelete: true, userData: true, shared: false),
                    ]).ToList()),
                NewLevel(
                    "app_and_local_data",
                    "アプリ本体とTimelineデータを削除",
                    "アプリ本体、設定、取り込み素材、生成データ、作業ファイル、操作ログを削除します。",
                    destructive: true,
                    recommendedDefault: false,
                    items: BuildApplicationItems(root).Concat(BuildTimelineDataItems(dataRoot, settingsPath)).ToList()),
                NewLevel(
                    "app_and_runtime_resources",
                    "アプリ本体と実行環境の関連情報を削除",
                    "Timeline のアプリ本体とローカルデータに加えて、Docker関連リソースも削除対象として扱います。共有Ollamaなど他製品と共有される可能性があるものは既定では削除しません。",
                    destructive: true,
                    recommendedDefault: false,
                    items: BuildApplicationItems(root)
                        .Concat(BuildTimelineDataItems(dataRoot, settingsPath))
                        .Concat(BuildRuntimeItems(root))
                        .ToList()),
            ],
            Warnings = warnings,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static List<TimelineUninstallPlanItem> BuildApplicationItems(string root)
    {
        var items = new List<TimelineUninstallPlanItem>
        {
            NewItem("launcher", Path.Combine(root, "launcher"), "directory", Directory.Exists(Path.Combine(root, "launcher")), true, false, false),
            NewItem("launcher_tray", Path.Combine(root, "launcher-tray"), "directory", Directory.Exists(Path.Combine(root, "launcher-tray")), true, false, false),
            NewItem("local_api", Path.Combine(root, "local-api"), "directory", Directory.Exists(Path.Combine(root, "local-api")), true, false, false),
            NewItem("web", Path.Combine(root, "web"), "directory", Directory.Exists(Path.Combine(root, "web")), true, false, false),
            NewItem("worker", Path.Combine(root, "worker"), "directory", Directory.Exists(Path.Combine(root, "worker")), true, false, false),
            NewItem("docker", Path.Combine(root, "docker"), "directory", Directory.Exists(Path.Combine(root, "docker")), true, false, false),
            NewItem("compose", Path.Combine(root, "docker-compose.yml"), "file", File.Exists(Path.Combine(root, "docker-compose.yml")), true, false, false),
            NewItem("version", Path.Combine(root, "VERSION"), "file", File.Exists(Path.Combine(root, "VERSION")), true, false, false),
        };

        return items;
    }

    private static List<TimelineUninstallPlanItem> BuildTimelineDataItems(string dataRoot, string settingsPath)
    {
        return
        [
            NewItem("settings", settingsPath, "file", File.Exists(settingsPath), true, true, false),
            NewItem("data_root", dataRoot, "directory", Directory.Exists(dataRoot), true, true, false),
            NewItem("input_materials", Path.Combine(dataRoot, "input"), "directory", Directory.Exists(Path.Combine(dataRoot, "input")), true, true, false),
            NewItem("generated_text", Path.Combine(dataRoot, "to_text"), "directory", Directory.Exists(Path.Combine(dataRoot, "to_text")), true, true, false),
            NewItem("timeline_store", Path.Combine(dataRoot, "to_timeline"), "directory", Directory.Exists(Path.Combine(dataRoot, "to_timeline")), true, true, false),
            NewItem("work", Path.Combine(dataRoot, "work"), "directory", Directory.Exists(Path.Combine(dataRoot, "work")), true, true, false),
            NewItem("logs", Path.Combine(dataRoot, "logs"), "directory", Directory.Exists(Path.Combine(dataRoot, "logs")), true, true, false),
            NewItem("managed_sub_products", Path.Combine(dataRoot, "products"), "directory", Directory.Exists(Path.Combine(dataRoot, "products")), true, true, false),
        ];
    }

    private static List<TimelineUninstallPlanItem> BuildRuntimeItems(string root)
    {
        return
        [
            NewItem("compose_project", root, "docker-compose-project", Directory.Exists(root), true, false, false),
            NewItem("timeline_volumes", "Docker volumes owned by this Timeline instance", "docker-volume-group", false, true, false, false),
            NewItem("shared_ollama_volume", "Shared Ollama Docker volume", "docker-volume", false, false, false, true),
        ];
    }

    private static TimelineUninstallLevel NewLevel(
        string id,
        string name,
        string description,
        bool destructive,
        bool recommendedDefault,
        List<TimelineUninstallPlanItem> items)
        => new()
        {
            Id = id,
            Name = name,
            Description = description,
            Destructive = destructive,
            RecommendedDefault = recommendedDefault,
            RequiresStrongConfirmation = destructive,
            Items = items,
        };

    private static TimelineUninstallPlanItem NewItem(
        string id,
        string path,
        string kind,
        bool exists,
        bool defaultDelete,
        bool userData,
        bool shared)
        => new()
        {
            Id = id,
            Path = path,
            Kind = kind,
            Exists = exists,
            DefaultDelete = defaultDelete,
            UserData = userData,
            SharedResource = shared,
            Risk = userData
                ? "user_data"
                : shared
                    ? "shared_resource"
                    : "application_file",
        };

    private static string ResolveDataRoot(string root)
    {
        var dataRoot = "data";
        var settingsPath = Path.Combine(root, "settings.json");
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

    private static string GetString(JsonObject? payload, string propertyName)
    {
        if (payload is null || !payload.TryGetPropertyValue(propertyName, out var node))
        {
            return "";
        }

        return node?.GetValue<string>() ?? "";
    }

    private static bool IsPathUnder(string path, string parent)
    {
        var fullPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var fullParent = EnsureTrailingSeparator(Path.GetFullPath(parent));
        return fullPath.StartsWith(
            fullParent,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}

public sealed class TimelineUninstallPlanResponse
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("canExecute")]
    public bool CanExecute { get; set; }

    [JsonPropertyName("requiresExplicitConfirmation")]
    public bool RequiresExplicitConfirmation { get; set; }

    [JsonPropertyName("timelineRoot")]
    public string TimelineRoot { get; set; } = "";

    [JsonPropertyName("dataRoot")]
    public string DataRoot { get; set; } = "";

    [JsonPropertyName("settingsPath")]
    public string SettingsPath { get; set; } = "";

    [JsonPropertyName("levels")]
    public List<TimelineUninstallLevel> Levels { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<TimelineUninstallPlanMessage> Warnings { get; set; } = [];

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";
}

public sealed class TimelineUninstallLevel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("destructive")]
    public bool Destructive { get; set; }

    [JsonPropertyName("recommendedDefault")]
    public bool RecommendedDefault { get; set; }

    [JsonPropertyName("requiresStrongConfirmation")]
    public bool RequiresStrongConfirmation { get; set; }

    [JsonPropertyName("items")]
    public List<TimelineUninstallPlanItem> Items { get; set; } = [];
}

public sealed class TimelineUninstallPlanItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("defaultDelete")]
    public bool DefaultDelete { get; set; }

    [JsonPropertyName("userData")]
    public bool UserData { get; set; }

    [JsonPropertyName("sharedResource")]
    public bool SharedResource { get; set; }

    [JsonPropertyName("risk")]
    public string Risk { get; set; } = "";
}

public sealed class TimelineUninstallPlanMessage
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
