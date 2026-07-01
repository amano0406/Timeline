using System.Diagnostics;
using System.Text.Json.Serialization;

public sealed class TimelineRuntimeControlService
{
    private readonly TimelineLocalApiOptions _options;

    public TimelineRuntimeControlService(TimelineLocalApiOptions options)
    {
        _options = options;
    }

    public TimelineRuntimeControlResponse StopTimeline()
    {
        var command = ResolveLauncherStopCommand();
        if (command is null)
        {
            return new TimelineRuntimeControlResponse
            {
                Accepted = false,
                State = "not_available",
                Message = "Timeline Launcher was not found.",
            };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _options.TimelineProductPath,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);

        return new TimelineRuntimeControlResponse
        {
            Accepted = true,
            State = "stopping",
            LauncherPath = command.LauncherPath,
            Message = "Timeline stop was requested through the C# Launcher.",
        };
    }

    private RuntimeCommand? ResolveLauncherStopCommand()
    {
        var dotnet = ResolveDotnetCommand();
        var launcherDll = ResolveLauncherDll();
        if (!string.IsNullOrWhiteSpace(launcherDll))
        {
            return new RuntimeCommand(
                dotnet,
                [launcherDll, "stop", "--root", _options.TimelineProductPath],
                launcherDll);
        }

        var launcherProject = Path.Combine(_options.TimelineProductPath, "launcher", "Timeline.Launcher.csproj");
        if (File.Exists(launcherProject))
        {
            return new RuntimeCommand(
                dotnet,
                ["run", "--project", launcherProject, "--", "stop", "--root", _options.TimelineProductPath],
                launcherProject);
        }

        return null;
    }

    private string ResolveLauncherDll()
    {
        var candidates = new[]
        {
            Path.Combine(_options.TimelineProductPath, "launcher", "bin", "Release", "net10.0", "Timeline.Launcher.dll"),
            Path.Combine(_options.TimelineProductPath, "launcher", "bin", "Debug", "net10.0", "Timeline.Launcher.dll"),
        };

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static string ResolveDotnetCommand()
    {
        var commandName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
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

    private sealed record RuntimeCommand(string FileName, string[] Arguments, string LauncherPath);
}

public sealed class TimelineRuntimeControlResponse
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("launcherPath")]
    public string LauncherPath { get; set; } = "";
}
