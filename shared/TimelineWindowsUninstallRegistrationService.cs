using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Microsoft.Win32;

public static class TimelineWindowsUninstallRegistrationService
{
    private const string AppName = "Timeline";
    private const string Publisher = "Amano System Lab";
    private const string RegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Timeline";
    private const string RegistryDisplayPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\Timeline";
    private const string RegistrationKind = "windows-uninstall-registry-entry";

    public static TimelineWindowsUninstallRegistrationStatus GetStatus(string timelineRoot)
    {
        timelineRoot = NormalizeRoot(timelineRoot);
        if (!OperatingSystem.IsWindows())
        {
            return Unsupported(timelineRoot);
        }

        try
        {
            var expected = BuildUninstallCommand(timelineRoot);
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey, writable: false);
            if (key is null)
            {
                return NewStatus(
                    registered: false,
                    state: "not_registered",
                    timelineRoot: timelineRoot,
                    displayName: AppName,
                    displayVersion: ResolveVersion(timelineRoot),
                    installLocation: timelineRoot,
                    displayIcon: ResolveDisplayIcon(timelineRoot),
                    uninstallString: FormatCommandLine(expected),
                    message: "Timeline は Windows のアンインストール一覧にまだ登録されていません。");
            }

            var actual = ReadRegistration(key);
            var matches = RegistrationMatches(actual, expected, timelineRoot);
            return NewStatus(
                registered: true,
                state: matches ? "registered" : "registered_with_different_target",
                timelineRoot: timelineRoot,
                displayName: actual.DisplayName,
                displayVersion: actual.DisplayVersion,
                installLocation: actual.InstallLocation,
                displayIcon: actual.DisplayIcon,
                uninstallString: actual.UninstallString,
                message: matches
                    ? "Timeline は Windows のアンインストール一覧に登録されています。"
                    : "Timeline のアンインストール登録はありますが、現在の配置と異なります。作成し直すと更新されます。");
        }
        catch (Exception ex)
        {
            return NewStatus(
                registered: false,
                state: "failed",
                timelineRoot: timelineRoot,
                displayName: AppName,
                displayVersion: "",
                installLocation: timelineRoot,
                displayIcon: "",
                uninstallString: "",
                message: $"Timeline のアンインストール登録を確認できませんでした。{ex.Message}");
        }
    }

    public static TimelineWindowsUninstallRegistrationStatus Register(string timelineRoot)
    {
        timelineRoot = NormalizeRoot(timelineRoot);
        if (!OperatingSystem.IsWindows())
        {
            return Unsupported(timelineRoot);
        }

        try
        {
            var command = BuildUninstallCommand(timelineRoot);
            var displayVersion = ResolveVersion(timelineRoot);
            var displayIcon = ResolveDisplayIcon(timelineRoot);
            using var key = Registry.CurrentUser.CreateSubKey(RegistrySubKey, writable: true)
                ?? throw new InvalidOperationException("Windows uninstall registry key could not be created.");

            key.SetValue("DisplayName", AppName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", displayVersion, RegistryValueKind.String);
            key.SetValue("Publisher", Publisher, RegistryValueKind.String);
            key.SetValue("InstallLocation", timelineRoot, RegistryValueKind.String);
            key.SetValue("DisplayIcon", displayIcon, RegistryValueKind.String);
            key.SetValue("UninstallString", FormatCommandLine(command), RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            var estimatedSize = EstimateApplicationSizeInKiB(timelineRoot);
            if (estimatedSize > 0)
            {
                key.SetValue("EstimatedSize", estimatedSize, RegistryValueKind.DWord);
            }

            return GetStatus(timelineRoot);
        }
        catch (Exception ex)
        {
            return NewStatus(
                registered: false,
                state: "failed",
                timelineRoot: timelineRoot,
                displayName: AppName,
                displayVersion: "",
                installLocation: timelineRoot,
                displayIcon: "",
                uninstallString: "",
                message: $"Timeline を Windows のアンインストール一覧に登録できませんでした。{ex.Message}");
        }
    }

    public static TimelineWindowsUninstallRegistrationStatus Remove(string timelineRoot)
    {
        timelineRoot = NormalizeRoot(timelineRoot);
        if (!OperatingSystem.IsWindows())
        {
            return Unsupported(timelineRoot);
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistrySubKey, throwOnMissingSubKey: false);
            return GetStatus(timelineRoot);
        }
        catch (Exception ex)
        {
            return NewStatus(
                registered: true,
                state: "failed",
                timelineRoot: timelineRoot,
                displayName: AppName,
                displayVersion: "",
                installLocation: timelineRoot,
                displayIcon: "",
                uninstallString: "",
                message: $"Timeline のアンインストール登録を削除できませんでした。{ex.Message}");
        }
    }

    private static TimelineWindowsUninstallRegistrationStatus Unsupported(string timelineRoot)
        => new()
        {
            Platform = GetPlatformName(),
            Supported = false,
            Registered = false,
            State = "unsupported",
            Kind = "unsupported",
            RegistryKeyPath = "",
            DisplayName = AppName,
            DisplayVersion = "",
            Publisher = Publisher,
            InstallLocation = timelineRoot,
            DisplayIcon = "",
            UninstallString = "",
            Message = "このOSでは Windows のアンインストール登録は利用しません。",
        };

    private static TimelineWindowsUninstallRegistrationStatus NewStatus(
        bool registered,
        string state,
        string timelineRoot,
        string displayName,
        string displayVersion,
        string installLocation,
        string displayIcon,
        string uninstallString,
        string message)
        => new()
        {
            Platform = GetPlatformName(),
            Supported = true,
            Registered = registered,
            State = state,
            Kind = RegistrationKind,
            RegistryKeyPath = RegistryDisplayPath,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? AppName : displayName,
            DisplayVersion = displayVersion,
            Publisher = Publisher,
            InstallLocation = installLocation,
            DisplayIcon = displayIcon,
            UninstallString = uninstallString,
            Message = message,
        };

    [SupportedOSPlatform("windows")]
    private static UninstallRegistrationActual ReadRegistration(RegistryKey key)
        => new(
            DisplayName: Convert.ToString(key.GetValue("DisplayName")) ?? "",
            DisplayVersion: Convert.ToString(key.GetValue("DisplayVersion")) ?? "",
            InstallLocation: Convert.ToString(key.GetValue("InstallLocation")) ?? "",
            DisplayIcon: Convert.ToString(key.GetValue("DisplayIcon")) ?? "",
            UninstallString: Convert.ToString(key.GetValue("UninstallString")) ?? "");

    private static bool RegistrationMatches(
        UninstallRegistrationActual actual,
        LauncherCommand expected,
        string timelineRoot)
    {
        return string.Equals(actual.DisplayName, AppName, StringComparison.Ordinal)
            && PathsEqual(actual.InstallLocation, timelineRoot)
            && string.Equals(
                NormalizeCommandLine(actual.UninstallString),
                NormalizeCommandLine(FormatCommandLine(expected)),
                StringComparison.OrdinalIgnoreCase);
    }

    private static LauncherCommand BuildUninstallCommand(string timelineRoot)
    {
        var executable = ResolveLauncherExecutable(timelineRoot);
        if (!string.IsNullOrWhiteSpace(executable))
        {
            return new LauncherCommand(executable, "uninstall-plan");
        }

        var dll = ResolveLauncherDll(timelineRoot);
        if (!string.IsNullOrWhiteSpace(dll))
        {
            return new LauncherCommand(ResolveDotnetCommand(), $"{QuoteArgument(dll)} uninstall-plan");
        }

        var project = Path.Combine(timelineRoot, "launcher", "Timeline.Launcher.csproj");
        return new LauncherCommand(ResolveDotnetCommand(), $"run --project {QuoteArgument(project)} -- uninstall-plan");
    }

    private static string ResolveLauncherExecutable(string timelineRoot)
    {
        var executableName = OperatingSystem.IsWindows()
            ? "Timeline.Launcher.exe"
            : "Timeline.Launcher";
        var candidates = new[]
        {
            Path.Combine(timelineRoot, "launcher", executableName),
            Path.Combine(timelineRoot, "launcher", "publish", executableName),
            Path.Combine(timelineRoot, "launcher", "bin", "Release", "net10.0", executableName),
            Path.Combine(timelineRoot, "launcher", "bin", "Debug", "net10.0", executableName),
            Path.Combine(timelineRoot, "launcher", "bin", "Release", "net10.0", GetRuntimeIdentifier(), "publish", executableName),
            Path.Combine(timelineRoot, "launcher", "bin", "Debug", "net10.0", GetRuntimeIdentifier(), "publish", executableName),
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static string ResolveLauncherDll(string timelineRoot)
    {
        var candidates = new[]
        {
            Path.Combine(timelineRoot, "launcher", "bin", "Release", "net10.0", "Timeline.Launcher.dll"),
            Path.Combine(timelineRoot, "launcher", "bin", "Debug", "net10.0", "Timeline.Launcher.dll"),
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static string ResolveDisplayIcon(string timelineRoot)
    {
        var trayExecutable = TimelineLauncherShortcutService.ResolveLauncherTrayExecutable(timelineRoot);
        if (!string.IsNullOrWhiteSpace(trayExecutable))
        {
            return trayExecutable;
        }

        return ResolveLauncherExecutable(timelineRoot);
    }

    private static string ResolveVersion(string timelineRoot)
    {
        var versionPath = Path.Combine(timelineRoot, "VERSION");
        if (File.Exists(versionPath))
        {
            var value = File.ReadAllText(versionPath).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return typeof(TimelineWindowsUninstallRegistrationService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static int EstimateApplicationSizeInKiB(string timelineRoot)
    {
        try
        {
            var totalBytes = new DirectoryInfo(timelineRoot)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => !IsPathUnder(file.FullName, Path.Combine(timelineRoot, "data")))
                .Sum(file => file.Length);
            return (int)Math.Min((long)int.MaxValue, Math.Max(1L, totalBytes / 1024));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private static string FormatCommandLine(LauncherCommand command)
        => string.IsNullOrWhiteSpace(command.Arguments)
            ? QuoteArgument(command.FileName)
            : $"{QuoteArgument(command.FileName)} {command.Arguments}";

    private static string ResolveDotnetCommand()
    {
        var commandName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(entry, commandName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return commandName;
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace)
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static string NormalizeCommandLine(string value)
        => value.Trim().Replace("  ", " ", StringComparison.Ordinal);

    private static string NormalizeRoot(string timelineRoot)
        => string.IsNullOrWhiteSpace(timelineRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(timelineRoot);

    private static string GetRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };

        if (OperatingSystem.IsWindows())
        {
            return $"win-{architecture}";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"osx-{architecture}";
        }

        if (OperatingSystem.IsLinux())
        {
            return $"linux-{architecture}";
        }

        return architecture;
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

    private sealed record LauncherCommand(string FileName, string Arguments);

    private sealed record UninstallRegistrationActual(
        string DisplayName,
        string DisplayVersion,
        string InstallLocation,
        string DisplayIcon,
        string UninstallString);
}

public sealed class TimelineWindowsUninstallRegistrationStatus
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("registered")]
    public bool Registered { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("registryKeyPath")]
    public string RegistryKeyPath { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("displayVersion")]
    public string DisplayVersion { get; set; } = "";

    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = "";

    [JsonPropertyName("installLocation")]
    public string InstallLocation { get; set; } = "";

    [JsonPropertyName("displayIcon")]
    public string DisplayIcon { get; set; } = "";

    [JsonPropertyName("uninstallString")]
    public string UninstallString { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
