using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public static class TimelineInstallPlanService
{
    public static TimelineInstallPlanResponse GetPlan(string timelineRoot)
    {
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(timelineRoot)
            ? Directory.GetCurrentDirectory()
            : timelineRoot);
        var dataRoot = ResolveDataRoot(root);
        var settingsPath = Path.Combine(root, "settings.json");
        var shortcut = TimelineLauncherShortcutService.GetStatus(root);
        var uninstallRegistration = TimelineWindowsUninstallRegistrationService.GetStatus(root);
        var launcherExecutable = TimelineLauncherShortcutService.ResolveLauncherTrayExecutable(root);
        var warnings = BuildWarnings(root, dataRoot, settingsPath, shortcut, launcherExecutable);

        return new TimelineInstallPlanResponse
        {
            ProductId = "timeline",
            ProductName = "Timeline",
            State = shortcut.Supported ? "partially_available" : "planned",
            Mode = "read_only",
            CanExecute = false,
            Platform = GetPlatformName(),
            TimelineRoot = root,
            DataRoot = dataRoot,
            SettingsPath = settingsPath,
            LauncherExecutablePath = launcherExecutable,
            AppEntry = BuildAppEntry(shortcut),
            RegistrationTargets = BuildRegistrationTargets(root, shortcut, uninstallRegistration),
            ArtifactTargets = BuildArtifactTargets(),
            Preserve = BuildPreserveItems(dataRoot, settingsPath),
            Warnings = warnings,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static TimelineInstallPlanRegistration BuildAppEntry(TimelineLauncherShortcutStatus shortcut)
        => new()
        {
            Id = "app_entry",
            Name = GetAppEntryName(),
            Kind = shortcut.Kind,
            Supported = shortcut.Supported,
            Implemented = shortcut.Supported,
            Required = true,
            State = shortcut.State,
            CurrentPath = shortcut.ShortcutPath,
            TargetPath = shortcut.TargetPath,
            CommandLine = TimelineLauncherShortcutService.FormatCommandLine(shortcut),
            Message = shortcut.Message,
        };

    private static List<TimelineInstallPlanRegistration> BuildRegistrationTargets(
        string root,
        TimelineLauncherShortcutStatus shortcut,
        TimelineWindowsUninstallRegistrationStatus uninstallRegistration)
    {
        var appEntry = BuildAppEntry(shortcut);
        return
        [
            appEntry,
            new TimelineInstallPlanRegistration
            {
                Id = "startup_entry",
                Name = "OS起動時にTimelineを起動する登録",
                Kind = GetStartupKind(),
                Supported = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
                Implemented = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
                Required = false,
                State = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? "settings_managed"
                    : "planned",
                CurrentPath = "",
                TargetPath = appEntry.TargetPath,
                CommandLine = appEntry.CommandLine,
                Message = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? "OS起動時の自動起動登録は、Timeline の設定画面と Local API で扱います。インストーラーは同じ登録先を尊重します。"
                    : "OS起動時の自動起動登録は、今後のOS対応で扱います。",
            },
            BuildUninstallEntry(root, uninstallRegistration),
        ];
    }

    private static TimelineInstallPlanRegistration BuildUninstallEntry(
        string root,
        TimelineWindowsUninstallRegistrationStatus uninstallRegistration)
    {
        if (OperatingSystem.IsWindows())
        {
            return new TimelineInstallPlanRegistration
            {
                Id = "uninstall_entry",
                Name = "OS標準のアンインストール入口",
                Kind = uninstallRegistration.Kind,
                Supported = uninstallRegistration.Supported,
                Implemented = uninstallRegistration.Supported,
                Required = true,
                State = uninstallRegistration.State,
                CurrentPath = uninstallRegistration.RegistryKeyPath,
                TargetPath = uninstallRegistration.InstallLocation,
                CommandLine = uninstallRegistration.UninstallString,
                Message = uninstallRegistration.Message + " 現時点のUninstallStringは削除実行ではなく、削除対象を確認するuninstall-planを開きます。",
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return new TimelineInstallPlanRegistration
            {
                Id = "uninstall_entry",
                Name = "macOSのアプリ削除入口",
                Kind = "macos-application-removal-guidance",
                Supported = true,
                Implemented = false,
                Required = true,
                State = "planned",
                CurrentPath = "",
                TargetPath = root,
                CommandLine = "",
                Message = "macOSでは.app配置とユーザーデータ削除を分けて扱います。削除対象の選択はuninstall-planで整理します。",
            };
        }

        return new TimelineInstallPlanRegistration
        {
            Id = "uninstall_entry",
            Name = "OS標準のアンインストール入口",
            Kind = "os-uninstall-entry",
            Supported = false,
            Implemented = false,
            Required = true,
            State = "unsupported",
            CurrentPath = "",
            TargetPath = root,
            CommandLine = "",
            Message = "このOSでのアンインストール入口は、OS別の配布方式で扱います。",
        };
    }

    private static List<TimelineInstallPlanArtifact> BuildArtifactTargets()
    {
        return
        [
            new TimelineInstallPlanArtifact
            {
                Id = "windows_installer",
                Name = "Windows向けインストーラー",
                Kind = "windows-installer",
                Platform = "Windows",
                State = OperatingSystem.IsWindows() ? "target" : "planned",
                Description = "TimelineをOSのアプリとして登録し、スタートメニューとアンインストール入口を作ります。",
            },
            new TimelineInstallPlanArtifact
            {
                Id = "mac_installer",
                Name = "macOS向けインストーラー",
                Kind = "macos-installer",
                Platform = "macOS",
                State = OperatingSystem.IsMacOS() ? "target" : "planned",
                Description = "TimelineをmacOSのアプリとして扱える形にし、メニューバーLauncherを入口にします。",
            },
        ];
    }

    private static List<TimelineInstallPlanItem> BuildPreserveItems(string dataRoot, string settingsPath)
    {
        return
        [
            NewItem("settings", settingsPath, "file", File.Exists(settingsPath), userData: true),
            NewItem("data_root", dataRoot, "directory", Directory.Exists(dataRoot), userData: true),
            NewItem("input_materials", Path.Combine(dataRoot, "input"), "directory", Directory.Exists(Path.Combine(dataRoot, "input")), userData: true),
            NewItem("timeline_store", Path.Combine(dataRoot, "to_timeline"), "directory", Directory.Exists(Path.Combine(dataRoot, "to_timeline")), userData: true),
            NewItem("logs", Path.Combine(dataRoot, "logs"), "directory", Directory.Exists(Path.Combine(dataRoot, "logs")), userData: true),
        ];
    }

    private static List<TimelineInstallPlanMessage> BuildWarnings(
        string root,
        string dataRoot,
        string settingsPath,
        TimelineLauncherShortcutStatus shortcut,
        string launcherExecutable)
    {
        var warnings = new List<TimelineInstallPlanMessage>();
        if (IsPathUnder(dataRoot, root))
        {
            warnings.Add(new TimelineInstallPlanMessage
            {
                Code = "data_inside_app_root",
                Message = "The configured data root is inside the Timeline application root. Installer and uninstaller flows must preserve user data explicitly.",
            });
        }

        if (File.Exists(settingsPath) && IsPathUnder(settingsPath, root))
        {
            warnings.Add(new TimelineInstallPlanMessage
            {
                Code = "settings_inside_app_root",
                Message = "settings.json is currently stored in the Timeline application root. Reinstall and uninstall flows must preserve it when the user keeps settings.",
            });
        }

        if (shortcut.Supported
            && string.IsNullOrWhiteSpace(launcherExecutable)
            && shortcut.TargetPath.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new TimelineInstallPlanMessage
            {
                Code = "developer_launcher_fallback",
                Message = "The app entry currently falls back to dotnet/project execution. A user installer should point to the published resident launcher executable instead.",
            });
        }

        return warnings;
    }

    private static TimelineInstallPlanItem NewItem(
        string id,
        string path,
        string kind,
        bool exists,
        bool userData)
        => new()
        {
            Id = id,
            Path = path,
            Kind = kind,
            Exists = exists,
            UserData = userData,
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

    private static string GetAppEntryName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "スタートメニューのTimelineアプリ入口";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOSのTimelineアプリ入口";
        }

        return "Timelineアプリ入口";
    }

    private static string GetStartupKind()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows-startup-registration";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos-login-item";
        }

        return "os-startup-registration";
    }

    private static string GetPlatformName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        return RuntimeInformation.OSDescription;
    }
}

public sealed class TimelineInstallPlanResponse
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

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("timelineRoot")]
    public string TimelineRoot { get; set; } = "";

    [JsonPropertyName("dataRoot")]
    public string DataRoot { get; set; } = "";

    [JsonPropertyName("settingsPath")]
    public string SettingsPath { get; set; } = "";

    [JsonPropertyName("launcherExecutablePath")]
    public string LauncherExecutablePath { get; set; } = "";

    [JsonPropertyName("appEntry")]
    public TimelineInstallPlanRegistration AppEntry { get; set; } = new();

    [JsonPropertyName("registrationTargets")]
    public List<TimelineInstallPlanRegistration> RegistrationTargets { get; set; } = [];

    [JsonPropertyName("artifactTargets")]
    public List<TimelineInstallPlanArtifact> ArtifactTargets { get; set; } = [];

    [JsonPropertyName("preserve")]
    public List<TimelineInstallPlanItem> Preserve { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<TimelineInstallPlanMessage> Warnings { get; set; } = [];

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";
}

public sealed class TimelineInstallPlanRegistration
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("implemented")]
    public bool Implemented { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("currentPath")]
    public string CurrentPath { get; set; } = "";

    [JsonPropertyName("targetPath")]
    public string TargetPath { get; set; } = "";

    [JsonPropertyName("commandLine")]
    public string CommandLine { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class TimelineInstallPlanArtifact
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

public sealed class TimelineInstallPlanItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("userData")]
    public bool UserData { get; set; }
}

public sealed class TimelineInstallPlanMessage
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
