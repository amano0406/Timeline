using System.Diagnostics;
using System.Runtime.InteropServices;

public static class TimelineAiHardwareProbe
{
    public static bool HasAiGpuDevice()
    {
        foreach (var name in GetVideoControllerNames())
        {
            if (IsAiGpuDevice(name))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsAiGpuDevice(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Contains("nvidia", StringComparison.Ordinal)
            || normalized.Contains("cuda", StringComparison.Ordinal);
    }

    public static List<string> GetVideoControllerNames()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return [];
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-CimInstance -ClassName Win32_VideoController | ForEach-Object { ([string]$_.Name).Trim() } | Where-Object { $_ } | Select-Object -Unique\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return [];
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                TryKillProcess(process);
                return [];
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return [];
            }

            return stdout
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
        }
    }
}
