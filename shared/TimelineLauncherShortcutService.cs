using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;

public static class TimelineLauncherShortcutService
{
    private const string AppName = "Timeline";
    private const string ShortcutFolderName = "Timeline";
    private const string ShortcutFileName = "Timeline.lnk";
    private const string ShortcutKind = "windows-start-menu-shortcut";

    public static TimelineLauncherShortcutStatus GetStatus(string timelineRoot)
    {
        timelineRoot = NormalizeRoot(timelineRoot);
        if (!OperatingSystem.IsWindows())
        {
            return Unsupported(timelineRoot);
        }

        try
        {
            var expected = BuildLauncherCommand(timelineRoot);
            var shortcutPath = GetWindowsShortcutPath();
            if (!File.Exists(shortcutPath))
            {
                return NewStatus(
                    supported: true,
                    registered: false,
                    state: "not_registered",
                    shortcutPath: shortcutPath,
                    targetPath: expected.FileName,
                    arguments: expected.Arguments,
                    workingDirectory: expected.WorkingDirectory,
                    message: "Timeline のアプリ入口はまだ作成されていません。");
            }

            var actual = ReadWindowsShortcut(shortcutPath);
            var targetMatches = ShortcutMatches(actual, expected);
            return NewStatus(
                supported: true,
                registered: true,
                state: targetMatches ? "registered" : "registered_with_different_target",
                shortcutPath: shortcutPath,
                targetPath: actual.TargetPath,
                arguments: actual.Arguments,
                workingDirectory: actual.WorkingDirectory,
                message: targetMatches
                    ? "Timeline のアプリ入口が作成されています。"
                    : "Timeline のアプリ入口はありますが、起動先が現在の構成と異なります。作成し直すと更新されます。");
        }
        catch (Exception ex)
        {
            return NewStatus(
                supported: true,
                registered: false,
                state: "failed",
                shortcutPath: GetWindowsShortcutPathSafe(),
                targetPath: "",
                arguments: "",
                workingDirectory: timelineRoot,
                message: $"Timeline のアプリ入口を確認できませんでした。{ex.Message}");
        }
    }

    public static TimelineLauncherShortcutStatus Install(string timelineRoot)
    {
        timelineRoot = NormalizeRoot(timelineRoot);
        if (!OperatingSystem.IsWindows())
        {
            return Unsupported(timelineRoot);
        }

        try
        {
            var command = BuildLauncherCommand(timelineRoot);
            var shortcutPath = GetWindowsShortcutPath();
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath) ?? "");
            WriteWindowsShortcut(shortcutPath, command);

            return NewStatus(
                supported: true,
                registered: true,
                state: "registered",
                shortcutPath: shortcutPath,
                targetPath: command.FileName,
                arguments: command.Arguments,
                workingDirectory: command.WorkingDirectory,
                message: "Timeline のアプリ入口を作成しました。");
        }
        catch (Exception ex)
        {
            return NewStatus(
                supported: true,
                registered: false,
                state: "failed",
                shortcutPath: GetWindowsShortcutPathSafe(),
                targetPath: "",
                arguments: "",
                workingDirectory: timelineRoot,
                message: $"Timeline のアプリ入口を作成できませんでした。{ex.Message}");
        }
    }

    public static TimelineLauncherShortcutStatus Remove(string timelineRoot)
    {
        timelineRoot = NormalizeRoot(timelineRoot);
        if (!OperatingSystem.IsWindows())
        {
            return Unsupported(timelineRoot);
        }

        try
        {
            var shortcutPath = GetWindowsShortcutPath();
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }

            var directory = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrWhiteSpace(directory)
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }

            var expected = BuildLauncherCommand(timelineRoot);
            return NewStatus(
                supported: true,
                registered: false,
                state: "not_registered",
                shortcutPath: shortcutPath,
                targetPath: expected.FileName,
                arguments: expected.Arguments,
                workingDirectory: expected.WorkingDirectory,
                message: "Timeline のアプリ入口を削除しました。");
        }
        catch (Exception ex)
        {
            return NewStatus(
                supported: true,
                registered: true,
                state: "failed",
                shortcutPath: GetWindowsShortcutPathSafe(),
                targetPath: "",
                arguments: "",
                workingDirectory: timelineRoot,
                message: $"Timeline のアプリ入口を削除できませんでした。{ex.Message}");
        }
    }

    public static string FormatCommandLine(TimelineLauncherShortcutStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.TargetPath))
        {
            return "";
        }

        return string.IsNullOrWhiteSpace(status.Arguments)
            ? QuoteForDisplay(status.TargetPath)
            : $"{QuoteForDisplay(status.TargetPath)} {status.Arguments}";
    }

    public static string ResolveLauncherTrayExecutable(string timelineRoot)
    {
        var executableName = OperatingSystem.IsWindows()
            ? "Timeline.Launcher.Tray.exe"
            : "Timeline.Launcher.Tray";
        var candidates = new[]
        {
            Path.Combine(timelineRoot, "launcher-tray", executableName),
            Path.Combine(timelineRoot, "launcher-tray", "publish", executableName),
            Path.Combine(timelineRoot, "launcher-tray", "bin", "Release", "net10.0", executableName),
            Path.Combine(timelineRoot, "launcher-tray", "bin", "Debug", "net10.0", executableName),
            Path.Combine(timelineRoot, "launcher-tray", "bin", "Release", "net10.0", GetRuntimeIdentifier(), "publish", executableName),
            Path.Combine(timelineRoot, "launcher-tray", "bin", "Debug", "net10.0", GetRuntimeIdentifier(), "publish", executableName),
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static TimelineLauncherShortcutStatus Unsupported(string timelineRoot)
        => NewStatus(
            supported: false,
            registered: false,
            state: "unsupported",
            shortcutPath: timelineRoot,
            targetPath: "",
            arguments: "",
            workingDirectory: timelineRoot,
            message: "このOSでのアプリ入口作成はまだ実装されていません。");

    private static TimelineLauncherShortcutStatus NewStatus(
        bool supported,
        bool registered,
        string state,
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string message)
        => new()
        {
            Platform = GetPlatformName(),
            Supported = supported,
            Registered = registered,
            State = state,
            Kind = supported ? ShortcutKind : "unsupported",
            ShortcutPath = shortcutPath,
            TargetPath = targetPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Message = message,
        };

    private static LauncherShortcutCommand BuildLauncherCommand(string timelineRoot)
    {
        var launcherExecutable = ResolveLauncherTrayExecutable(timelineRoot);
        if (!string.IsNullOrWhiteSpace(launcherExecutable))
        {
            return new LauncherShortcutCommand(
                FileName: launcherExecutable,
                Arguments: "",
                WorkingDirectory: timelineRoot);
        }

        var dotnet = ResolveDotnetCommand();
        var launcherDll = ResolveLauncherTrayDll(timelineRoot);
        if (!string.IsNullOrWhiteSpace(launcherDll))
        {
            return new LauncherShortcutCommand(
                FileName: dotnet,
                Arguments: QuoteWindowsArgument(launcherDll),
                WorkingDirectory: timelineRoot);
        }

        var launcherProject = Path.Combine(timelineRoot, "launcher-tray", "Timeline.Launcher.Tray.csproj");
        return new LauncherShortcutCommand(
            FileName: dotnet,
            Arguments: $"run --project {QuoteWindowsArgument(launcherProject)}",
            WorkingDirectory: timelineRoot);
    }

    private static string ResolveLauncherTrayDll(string timelineRoot)
    {
        var candidates = new[]
        {
            Path.Combine(timelineRoot, "launcher-tray", "bin", "Release", "net10.0", "Timeline.Launcher.Tray.dll"),
            Path.Combine(timelineRoot, "launcher-tray", "bin", "Debug", "net10.0", "Timeline.Launcher.Tray.dll"),
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

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

    [SupportedOSPlatform("windows")]
    private static string GetWindowsShortcutPath()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        return Path.Combine(programs, ShortcutFolderName, ShortcutFileName);
    }

    private static string GetWindowsShortcutPathSafe()
    {
        try
        {
            return OperatingSystem.IsWindows() ? GetWindowsShortcutPath() : "";
        }
        catch
        {
            return "";
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindowsShortcut(string shortcutPath, LauncherShortcutCommand command)
    {
        var shortcut = CreateWindowsShortcutObject(shortcutPath);
        try
        {
            SetShortcutProperty(shortcut, "TargetPath", command.FileName);
            SetShortcutProperty(shortcut, "Arguments", command.Arguments);
            SetShortcutProperty(shortcut, "WorkingDirectory", command.WorkingDirectory);
            SetShortcutProperty(shortcut, "Description", "Timeline Launcher");
            InvokeShortcutMethod(shortcut, "Save");
        }
        finally
        {
            ReleaseComObject(shortcut);
        }
    }

    [SupportedOSPlatform("windows")]
    private static LauncherShortcutActual ReadWindowsShortcut(string shortcutPath)
    {
        var shortcut = CreateWindowsShortcutObject(shortcutPath);
        try
        {
            return new LauncherShortcutActual(
                TargetPath: GetShortcutProperty(shortcut, "TargetPath"),
                Arguments: GetShortcutProperty(shortcut, "Arguments"),
                WorkingDirectory: GetShortcutProperty(shortcut, "WorkingDirectory"));
        }
        finally
        {
            ReleaseComObject(shortcut);
        }
    }

    [SupportedOSPlatform("windows")]
    private static object CreateWindowsShortcutObject(string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host が利用できないため、ショートカットを作成できません。");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows Script Host を起動できません。");
        try
        {
            return shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: shell,
                    args: [shortcutPath])
                ?? throw new InvalidOperationException("ショートカットを作成できません。");
        }
        finally
        {
            ReleaseComObject(shell);
        }
    }

    private static void SetShortcutProperty(object shortcut, string propertyName, string value)
    {
        shortcut.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target: shortcut,
            args: [value]);
    }

    private static string GetShortcutProperty(object shortcut, string propertyName)
    {
        var value = shortcut.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            binder: null,
            target: shortcut,
            args: null);
        return Convert.ToString(value) ?? "";
    }

    private static void InvokeShortcutMethod(object shortcut, string methodName)
    {
        shortcut.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod,
            binder: null,
            target: shortcut,
            args: null);
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static bool ShortcutMatches(LauncherShortcutActual actual, LauncherShortcutCommand expected)
    {
        return PathsEqual(actual.TargetPath, expected.FileName)
            && string.Equals(NormalizeArguments(actual.Arguments), NormalizeArguments(expected.Arguments), StringComparison.OrdinalIgnoreCase)
            && PathsEqual(actual.WorkingDirectory, expected.WorkingDirectory);
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

    private static string NormalizeArguments(string value) => value.Trim();

    private static string NormalizeRoot(string timelineRoot)
    {
        return string.IsNullOrWhiteSpace(timelineRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(timelineRoot);
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace)
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static string QuoteForDisplay(string value)
        => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

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

    private sealed record LauncherShortcutCommand(string FileName, string Arguments, string WorkingDirectory);

    private sealed record LauncherShortcutActual(string TargetPath, string Arguments, string WorkingDirectory);
}

public sealed class TimelineLauncherShortcutStatus
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

    [JsonPropertyName("shortcutPath")]
    public string ShortcutPath { get; set; } = "";

    [JsonPropertyName("targetPath")]
    public string TargetPath { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";

    [JsonPropertyName("workingDirectory")]
    public string WorkingDirectory { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
