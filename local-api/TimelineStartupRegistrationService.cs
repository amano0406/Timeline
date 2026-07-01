using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Win32;

public sealed class TimelineStartupRegistrationService
{
    private const string AppName = "Timeline";
    private const string WindowsRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyWindowsStartupScriptName = "Timeline Auto Start.cmd";
    private const string MacLaunchAgentFileName = "com.amanosystemlab.timeline.launcher.plist";

    private readonly TimelineLocalApiOptions _options;

    public TimelineStartupRegistrationService(TimelineLocalApiOptions options)
    {
        _options = options;
    }

    public TimelineStartupRegistrationStatusResponse GetStatus()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsStartupStatus();
        }

        if (OperatingSystem.IsMacOS())
        {
            return GetMacStartupStatus();
        }

        return UnsupportedStatus(
            "このOSでの自動起動登録はまだ実装されていません。設定項目はOS共通ですが、登録方式は今後追加します。");
    }

    public TimelineStartupRegistrationStatusResponse ApplyDesiredState(bool startWithOperatingSystem)
    {
        if (OperatingSystem.IsWindows())
        {
            return startWithOperatingSystem
                ? RegisterWindowsStartupEntry()
                : UnregisterWindowsStartupEntry();
        }

        if (OperatingSystem.IsMacOS())
        {
            return startWithOperatingSystem
                ? RegisterMacStartupEntry()
                : UnregisterMacStartupEntry();
        }

        return startWithOperatingSystem
            ? UnsupportedStatus("このOSでTimelineの自動起動を登録する処理はまだ実装されていません。")
            : UnsupportedStatus("このOSでの自動起動登録は未対応です。");
    }

    [SupportedOSPlatform("windows")]
    private TimelineStartupRegistrationStatusResponse GetWindowsStartupStatus()
    {
        var command = GetWindowsRunValue();
        var legacyScript = GetLegacyWindowsStartupScriptPath();
        var target = string.IsNullOrWhiteSpace(command) ? BuildLauncherCommandLine().DisplayText : command;

        if (!string.IsNullOrWhiteSpace(command))
        {
            return NewStatus(
                supported: true,
                registered: true,
                state: "registered",
                kind: "windows-run",
                target: target,
                message: "OS起動時にTimeline Launcherを起動する設定が登録されています。");
        }

        if (File.Exists(legacyScript))
        {
            return NewStatus(
                supported: true,
                registered: true,
                state: "legacy_registered",
                kind: "legacy-startup-script",
                target: legacyScript,
                message: "古い自動起動ファイルが残っています。設定を保存し直すとC# Launcherの登録へ移行します。");
        }

        return NewStatus(
            supported: true,
            registered: false,
            state: "not_registered",
            kind: "windows-run",
            target: target,
            message: "OS起動時の自動起動は登録されていません。");
    }

    [SupportedOSPlatform("windows")]
    private TimelineStartupRegistrationStatusResponse RegisterWindowsStartupEntry()
    {
        var command = BuildLauncherCommandLine();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKeyPath, writable: true);
            key?.SetValue(AppName, command.CommandLine, RegistryValueKind.String);
            RemoveLegacyWindowsStartupScript();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return NewStatus(
                supported: true,
                registered: false,
                state: "failed",
                kind: "windows-run",
                target: command.DisplayText,
                message: $"OS起動時の自動起動を登録できませんでした。{ex.Message}");
        }

        return NewStatus(
            supported: true,
            registered: true,
            state: "registered",
            kind: "windows-run",
            target: command.DisplayText,
            message: "OS起動時にTimeline Launcherを起動するよう登録しました。");
    }

    [SupportedOSPlatform("windows")]
    private TimelineStartupRegistrationStatusResponse UnregisterWindowsStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKeyPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
            RemoveLegacyWindowsStartupScript();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return NewStatus(
                supported: true,
                registered: true,
                state: "failed",
                kind: "windows-run",
                target: BuildLauncherCommandLine().DisplayText,
                message: $"OS起動時の自動起動を解除できませんでした。{ex.Message}");
        }

        return NewStatus(
            supported: true,
            registered: false,
            state: "not_registered",
            kind: "windows-run",
            target: BuildLauncherCommandLine().DisplayText,
            message: "OS起動時の自動起動を解除しました。");
    }

    private TimelineStartupRegistrationStatusResponse GetMacStartupStatus()
    {
        var plistPath = GetMacLaunchAgentPath();
        return NewStatus(
            supported: true,
            registered: File.Exists(plistPath),
            state: File.Exists(plistPath) ? "registered" : "not_registered",
            kind: "macos-launch-agent",
            target: plistPath,
            message: File.Exists(plistPath)
                ? "OS起動時にTimeline Launcherを起動する設定が登録されています。"
                : "OS起動時の自動起動は登録されていません。");
    }

    private TimelineStartupRegistrationStatusResponse RegisterMacStartupEntry()
    {
        var plistPath = GetMacLaunchAgentPath();
        var command = BuildLauncherCommandLine();
        try
        {
            var directory = Path.GetDirectoryName(plistPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(plistPath, BuildMacLaunchAgentPlist(command.Arguments), Encoding.UTF8);
            TryRunLaunchCtl("bootstrap", plistPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return NewStatus(
                supported: true,
                registered: false,
                state: "failed",
                kind: "macos-launch-agent",
                target: plistPath,
                message: $"OS起動時の自動起動を登録できませんでした。{ex.Message}");
        }

        return NewStatus(
            supported: true,
            registered: true,
            state: "registered",
            kind: "macos-launch-agent",
            target: plistPath,
            message: "OS起動時にTimeline Launcherを起動するよう登録しました。");
    }

    private TimelineStartupRegistrationStatusResponse UnregisterMacStartupEntry()
    {
        var plistPath = GetMacLaunchAgentPath();
        try
        {
            TryRunLaunchCtl("bootout", plistPath);
            if (File.Exists(plistPath))
            {
                File.Delete(plistPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return NewStatus(
                supported: true,
                registered: true,
                state: "failed",
                kind: "macos-launch-agent",
                target: plistPath,
                message: $"OS起動時の自動起動を解除できませんでした。{ex.Message}");
        }

        return NewStatus(
            supported: true,
            registered: false,
            state: "not_registered",
            kind: "macos-launch-agent",
            target: plistPath,
            message: "OS起動時の自動起動を解除しました。");
    }

    [SupportedOSPlatform("windows")]
    private string? GetWindowsRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKeyPath, writable: false);
        return key?.GetValue(AppName) as string;
    }

    [SupportedOSPlatform("windows")]
    private void RemoveLegacyWindowsStartupScript()
    {
        var legacyScript = GetLegacyWindowsStartupScriptPath();
        if (File.Exists(legacyScript))
        {
            File.Delete(legacyScript);
        }
    }

    private static string GetLegacyWindowsStartupScriptPath()
    {
        var startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return Path.Combine(startupDirectory, LegacyWindowsStartupScriptName);
    }

    private static string GetMacLaunchAgentPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "LaunchAgents", MacLaunchAgentFileName);
    }

    private LauncherCommandLine BuildLauncherCommandLine()
    {
        var launcherExecutable = TimelineLauncherShortcutService.ResolveLauncherTrayExecutable(_options.TimelineProductPath);
        if (!string.IsNullOrWhiteSpace(launcherExecutable))
        {
            return LauncherCommandLine.FromArguments([launcherExecutable]);
        }

        var dotnet = ResolveDotnetCommand();
        var launcherDll = ResolveLauncherTrayDll();
        if (!string.IsNullOrWhiteSpace(launcherDll))
        {
            return LauncherCommandLine.FromArguments([dotnet, launcherDll]);
        }

        var launcherProject = Path.Combine(_options.TimelineProductPath, "launcher-tray", "Timeline.Launcher.Tray.csproj");
        return LauncherCommandLine.FromArguments([dotnet, "run", "--project", launcherProject]);
    }

    private string ResolveLauncherTrayDll()
    {
        var candidates = new[]
        {
            Path.Combine(_options.TimelineProductPath, "launcher-tray", "bin", "Release", "net10.0", "Timeline.Launcher.Tray.dll"),
            Path.Combine(_options.TimelineProductPath, "launcher-tray", "bin", "Debug", "net10.0", "Timeline.Launcher.Tray.dll"),
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

    private static string BuildMacLaunchAgentPlist(IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        builder.AppendLine("""<plist version="1.0">""");
        builder.AppendLine("<dict>");
        builder.AppendLine("  <key>Label</key>");
        builder.AppendLine($"  <string>{MacLaunchAgentFileName.Replace(".plist", "", StringComparison.Ordinal)}</string>");
        builder.AppendLine("  <key>ProgramArguments</key>");
        builder.AppendLine("  <array>");
        foreach (var argument in arguments)
        {
            builder.AppendLine($"    <string>{SecurityElement.Escape(argument)}</string>");
        }
        builder.AppendLine("  </array>");
        builder.AppendLine("  <key>RunAtLoad</key>");
        builder.AppendLine("  <true/>");
        builder.AppendLine("  <key>KeepAlive</key>");
        builder.AppendLine("  <false/>");
        builder.AppendLine("</dict>");
        builder.AppendLine("</plist>");
        return builder.ToString();
    }

    private static void TryRunLaunchCtl(string action, string plistPath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var uid = Environment.GetEnvironmentVariable("UID");
            if (string.IsNullOrWhiteSpace(uid))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "launchctl",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(action);
            startInfo.ArgumentList.Add($"gui/{uid}");
            startInfo.ArgumentList.Add(plistPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }
            process.WaitForExit(5000);
        }
        catch
        {
        }
    }

    private TimelineStartupRegistrationStatusResponse UnsupportedStatus(string message)
        => new()
        {
            Platform = GetPlatformName(),
            Supported = false,
            Registered = false,
            State = "unsupported",
            Kind = "os-startup",
            Target = _options.TimelineProductPath,
            Message = message,
        };

    private TimelineStartupRegistrationStatusResponse NewStatus(
        bool supported,
        bool registered,
        string state,
        string kind,
        string target,
        string message)
        => new()
        {
            Platform = GetPlatformName(),
            Supported = supported,
            Registered = registered,
            State = state,
            Kind = kind,
            Target = target,
            Message = message,
        };

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

    private sealed record LauncherCommandLine(string CommandLine, string DisplayText, string[] Arguments)
    {
        public static LauncherCommandLine FromArguments(string[] arguments)
            => new(
                string.Join(" ", arguments.Select(QuoteWindowsArgument)),
                string.Join(" ", arguments.Select(QuoteForDisplay)),
                arguments);

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
    }
}

public sealed class TimelineStartupRegistrationStatusResponse
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

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
