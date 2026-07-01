using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Timeline.Launcher.Tray;

public sealed class App : Application
{
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _statusItem;
    private bool _busy;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        _trayIcon = new TrayIcon
        {
            ToolTipText = "Timeline",
            IsVisible = true,
            Menu = BuildMenu()
        };

        base.OnFrameworkInitializationCompleted();
        _ = RefreshStatusAsync();
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        _statusItem = new NativeMenuItem("状態: 確認中")
        {
            IsEnabled = false
        };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        var openItem = new NativeMenuItem("Timelineを開く");
        openItem.Click += async (_, _) => await RunLauncherAsync("open");
        menu.Items.Add(openItem);

        var startItem = new NativeMenuItem("起動");
        startItem.Click += async (_, _) => await RunLauncherAsync("start", "--no-open");
        menu.Items.Add(startItem);

        var stopItem = new NativeMenuItem("停止");
        stopItem.Click += async (_, _) => await RunLauncherAsync("stop");
        menu.Items.Add(stopItem);

        var refreshItem = new NativeMenuItem("状態を更新");
        refreshItem.Click += async (_, _) => await RefreshStatusAsync();
        menu.Items.Add(refreshItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Launcherを終了");
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        return menu;
    }

    private async Task RunLauncherAsync(params string[] arguments)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetStatus("状態: 処理中");
        try
        {
            var result = await TimelineLauncherProcess.RunAsync(arguments, TimeSpan.FromMinutes(30));
            SetStatus(result.ExitCode == 0 ? "状態: 利用できます" : "状態: 対応が必要です");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetStatus("状態: 確認中");
        try
        {
            var result = await TimelineLauncherProcess.RunAsync(["status"], TimeSpan.FromSeconds(30));
            SetStatus(result.ExitCode == 0 ? "状態: 稼働中" : "状態: 対応が必要です");
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetStatus(string text)
    {
        if (_statusItem is not null)
        {
            _statusItem.Header = text;
        }
    }

    private void Shutdown()
    {
        _trayIcon?.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}

internal sealed record LauncherProcessResult(int ExitCode, string Output, string Error);

internal static class TimelineLauncherProcess
{
    public static async Task<LauncherProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var root = ResolveTimelineRoot();
        var startInfo = BuildStartInfo(root, arguments);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new LauncherProcessResult(124, string.Empty, "Timeline Launcher command timed out.");
        }

        return new LauncherProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static ProcessStartInfo BuildStartInfo(string root, IReadOnlyList<string> arguments)
    {
        var launcherExecutable = ResolveLauncherExecutable(root);
        var launcherDll = ResolveLauncherDll(root);
        var launcherProject = Path.Combine(root, "launcher", "Timeline.Launcher.csproj");
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(launcherExecutable)
                ? ResolveDotnetCommand()
                : launcherExecutable,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        };

        if (string.IsNullOrWhiteSpace(launcherExecutable) && !string.IsNullOrWhiteSpace(launcherDll))
        {
            startInfo.ArgumentList.Add(launcherDll);
        }
        else if (string.IsNullOrWhiteSpace(launcherExecutable))
        {
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(launcherProject);
            startInfo.ArgumentList.Add("--");
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(root);
        return startInfo;
    }

    private static string ResolveTimelineRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "docker-compose.yml")) &&
                Directory.Exists(Path.Combine(current.FullName, "launcher")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(sourceRoot, "docker-compose.yml")))
        {
            return sourceRoot;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveLauncherExecutable(string root)
    {
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Timeline.Launcher.exe"
            : "Timeline.Launcher";
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        var candidates = new[]
        {
            Path.Combine(root, "launcher", executableName),
            Path.Combine(root, "launcher", "publish", executableName),
            Path.Combine(root, "launcher", "bin", "Release", "net10.0", executableName),
            Path.Combine(root, "launcher", "bin", "Debug", "net10.0", executableName),
            Path.Combine(root, "launcher", "bin", "Release", "net10.0", runtimeIdentifier, "publish", executableName),
            Path.Combine(root, "launcher", "bin", "Debug", "net10.0", runtimeIdentifier, "publish", executableName),
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static string ResolveLauncherDll(string root)
    {
        var configuration = IsDebugBuild() ? "Debug" : "Release";
        var candidates = new[]
        {
            Path.Combine(root, "launcher", "Timeline.Launcher.dll"),
            Path.Combine(root, "launcher", "bin", configuration, "net10.0", "Timeline.Launcher.dll"),
            Path.Combine(root, "launcher", "bin", "Release", "net10.0", "Timeline.Launcher.dll"),
            Path.Combine(root, "launcher", "bin", "Debug", "net10.0", "Timeline.Launcher.dll")
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static string ResolveDotnetCommand()
    {
        var commandName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
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
}
