using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

public sealed class TimelinePickerService
{
    public Task<JsonObject> PickDirectoryAsync(
        string? title,
        string? initialPath,
        CancellationToken cancellationToken)
    {
        var command = string.Join(Environment.NewLine, new[]
        {
            "Add-Type -AssemblyName System.Windows.Forms",
            "Add-Type -AssemblyName System.Drawing",
            "$dialog = [System.Windows.Forms.FolderBrowserDialog]::new()",
            "$dialog.Description = " + QuotePowerShellString(string.IsNullOrWhiteSpace(title) ? "Select directory" : title),
            "$dialog.ShowNewFolderButton = $true",
            "$initialPath = " + QuotePowerShellString(initialPath),
            "if ($initialPath -and (Test-Path -LiteralPath $initialPath)) { $dialog.SelectedPath = (Resolve-Path -LiteralPath $initialPath).Path }",
            "$owner = [System.Windows.Forms.Form]::new()",
            "$owner.TopMost = $true",
            "$owner.ShowInTaskbar = $false",
            "$owner.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen",
            "$owner.Size = [System.Drawing.Size]::new(1, 1)",
            "try {",
            "  $result = $dialog.ShowDialog($owner)",
            "  if ($result -eq [System.Windows.Forms.DialogResult]::OK) { @{ ok = $true; cancelled = $false; path = $dialog.SelectedPath } | ConvertTo-Json -Compress }",
            "  else { @{ ok = $true; cancelled = $true; path = $null } | ConvertTo-Json -Compress }",
            "}",
            "finally { $dialog.Dispose(); $owner.Dispose() }",
        });
        return RunPickerCommandAsync(command, cancellationToken);
    }

    public Task<JsonObject> PickFileAsync(
        string? title,
        string? initialPath,
        string? filter,
        CancellationToken cancellationToken)
    {
        var command = string.Join(Environment.NewLine, new[]
        {
            "Add-Type -AssemblyName System.Windows.Forms",
            "Add-Type -AssemblyName System.Drawing",
            "$dialog = [System.Windows.Forms.OpenFileDialog]::new()",
            "$dialog.Title = " + QuotePowerShellString(string.IsNullOrWhiteSpace(title) ? "Select file" : title),
            "$dialog.CheckFileExists = $true",
            "$dialog.Multiselect = $false",
            "$dialog.Filter = " + QuotePowerShellString(string.IsNullOrWhiteSpace(filter) ? "All files (*.*)|*.*" : filter),
            "$initialPath = " + QuotePowerShellString(initialPath),
            "if ($initialPath) {",
            "  if (Test-Path -LiteralPath $initialPath -PathType Container) { $dialog.InitialDirectory = (Resolve-Path -LiteralPath $initialPath).Path }",
            "  elseif (Test-Path -LiteralPath $initialPath -PathType Leaf) { $item = Get-Item -LiteralPath $initialPath; $dialog.InitialDirectory = $item.DirectoryName; $dialog.FileName = $item.Name }",
            "}",
            "$owner = [System.Windows.Forms.Form]::new()",
            "$owner.TopMost = $true",
            "$owner.ShowInTaskbar = $false",
            "$owner.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen",
            "$owner.Size = [System.Drawing.Size]::new(1, 1)",
            "try {",
            "  $result = $dialog.ShowDialog($owner)",
            "  if ($result -eq [System.Windows.Forms.DialogResult]::OK) { @{ ok = $true; cancelled = $false; path = $dialog.FileName } | ConvertTo-Json -Compress }",
            "  else { @{ ok = $true; cancelled = $true; path = $null } | ConvertTo-Json -Compress }",
            "}",
            "finally { $dialog.Dispose(); $owner.Dispose() }",
        });
        return RunPickerCommandAsync(command, cancellationToken);
    }

    private static async Task<JsonObject> RunPickerCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        using var process = new Process();
        process.StartInfo.FileName = GetPowerShellPath();
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = new UTF8Encoding(false);
        process.StartInfo.StandardErrorEncoding = new UTF8Encoding(false);
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-STA");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(encoded);
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "File picker failed."
                : stderr.Trim());
        }

        return ParseJsonObject(stdout);
    }

    private static JsonObject ParseJsonObject(string text)
    {
        var value = ConvertTimelineText(text);
        var start = value.IndexOf('{', StringComparison.Ordinal);
        var end = value.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("File picker did not return JSON.");
        }

        return JsonNode.Parse(value[start..(end + 1)]) as JsonObject
            ?? throw new InvalidOperationException("File picker did not return a JSON object.");
    }

    private static string GetPowerShellPath()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var path = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : "powershell.exe";
    }

    private static string QuotePowerShellString(string? value)
        => "'" + ConvertTimelineText(value).Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ConvertTimelineText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }
}
