using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

public sealed class TimelineStartupRegistrationService
{
    private const string WindowsStartupScriptName = "Timeline Auto Start.cmd";

    private readonly TimelineLocalApiOptions _options;

    public TimelineStartupRegistrationService(TimelineLocalApiOptions options)
    {
        _options = options;
    }

    public TimelineStartupRegistrationStatusResponse GetStatus()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetWindowsStartupStatus();
        }

        return UnsupportedStatus(
            "このOSでの自動起動登録はまだ実装されていません。設定モデルはOS共通のため、macOSなどの登録方式を後から追加できます。");
    }

    public TimelineStartupRegistrationStatusResponse ApplyDesiredState(bool startWithOperatingSystem)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return startWithOperatingSystem
                ? RegisterWindowsStartupEntry()
                : UnregisterWindowsStartupEntry();
        }

        return startWithOperatingSystem
            ? UnsupportedStatus("このOSでTimelineの自動起動を登録する処理はまだ実装されていません。")
            : UnsupportedStatus("このOSでの自動起動登録は未対応です。");
    }

    private TimelineStartupRegistrationStatusResponse GetWindowsStartupStatus()
    {
        var startupScript = GetWindowsStartupScriptPath();
        if (File.Exists(startupScript))
        {
            return NewStatus(
                supported: true,
                registered: true,
                state: "registered",
                target: startupScript,
                message: "OS起動時の自動起動が登録されています。");
        }

        return NewStatus(
            supported: true,
            registered: false,
            state: "not_registered",
            target: startupScript,
            message: "OS起動時の自動起動は登録されていません。");
    }

    private TimelineStartupRegistrationStatusResponse RegisterWindowsStartupEntry()
    {
        var startScript = Path.Combine(_options.TimelineProductPath, "start.ps1");
        if (!File.Exists(startScript))
        {
            return NewStatus(
                supported: true,
                registered: false,
                state: "failed",
                target: startScript,
                message: $"起動スクリプトが見つかりません: {startScript}");
        }

        var startupScript = GetWindowsStartupScriptPath();
        try
        {
            var startupDirectory = Path.GetDirectoryName(startupScript);
            if (!string.IsNullOrWhiteSpace(startupDirectory))
            {
                Directory.CreateDirectory(startupDirectory);
            }

            var content = string.Join(
                Environment.NewLine,
                "@echo off",
                $"start \"\" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{startScript}\" -NoOpen");
            File.WriteAllText(startupScript, content + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NewStatus(
                supported: true,
                registered: false,
                state: "failed",
                target: startupScript,
                message: $"OS起動時の自動起動を登録できませんでした。{ex.Message}");
        }

        return NewStatus(
            supported: true,
            registered: true,
            state: "registered",
            target: startupScript,
            message: "OS起動時にTimelineを自動起動するよう登録しました。");
    }

    private TimelineStartupRegistrationStatusResponse UnregisterWindowsStartupEntry()
    {
        var current = GetWindowsStartupStatus();
        if (!current.Registered)
        {
            return current;
        }

        var startupScript = GetWindowsStartupScriptPath();
        try
        {
            File.Delete(startupScript);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NewStatus(
                supported: true,
                registered: true,
                state: "failed",
                target: startupScript,
                message: $"OS起動時の自動起動を解除できませんでした。{ex.Message}");
        }

        return NewStatus(
            supported: true,
            registered: false,
            state: "not_registered",
            target: startupScript,
            message: "OS起動時の自動起動を解除しました。");
    }

    private static string GetWindowsStartupScriptPath()
    {
        var startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return Path.Combine(startupDirectory, WindowsStartupScriptName);
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
        string target,
        string message)
        => new()
        {
            Platform = GetPlatformName(),
            Supported = supported,
            Registered = registered,
            State = state,
            Kind = "startup-folder",
            Target = target,
            Message = message,
        };

    private static string GetPlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Windows";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macOS";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }

        return RuntimeInformation.OSDescription;
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
