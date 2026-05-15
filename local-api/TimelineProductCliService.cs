using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class TimelineProductCliService
{
    private readonly TimelineSettingsService _settings;
    private readonly TimelineOperationLogService _operations;
    private readonly TimelineLocalApiOptions _options;
    private readonly HttpClient _http;

    public TimelineProductCliService(
        TimelineSettingsService settings,
        TimelineOperationLogService operations,
        TimelineLocalApiOptions options,
        HttpClient http)
    {
        _settings = settings;
        _operations = operations;
        _options = options;
        _http = http;
    }

    public async Task<JsonNode?> InvokeJsonAsync(
        string productId,
        string productName,
        IReadOnlyList<string> cliArgs,
        int timeoutSeconds,
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        var productPath = GetProductPath(productId);
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            throw new InvalidOperationException($"{productName} was not found: {productPath}");
        }

        await AssertProductCliAccessAllowedAsync(productId, productName, productPath, cancellationToken);
        var result = await InvokeCliTextAsync(
            productId,
            productName,
            productPath,
            cliArgs,
            timeoutSeconds,
            parentOperationId,
            cancellationToken);
        return ConvertFromJsonOutput(result.Stdout);
    }

    private async Task<CliProcessResult> InvokeCliTextAsync(
        string productId,
        string productName,
        string productPath,
        IReadOnlyList<string> cliArgs,
        int timeoutSeconds,
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        var cliScript = GetCliScript(productId, productPath);
        var cliBatch = Path.Combine(productPath, "cli.bat");
        string fileName;
        List<string> arguments;

        if (File.Exists(cliScript))
        {
            var invoker = Path.Combine(_options.TimelineProductPath, "scripts", "invoke-product-cli-utf8.ps1");
            if (!File.Exists(invoker))
            {
                throw new InvalidOperationException($"Timeline product CLI UTF-8 invoker was not found: {invoker}");
            }

            fileName = GetPowerShellPath();
            arguments =
            [
                "-NoLogo",
                "-NoProfile",
                "-WindowStyle",
                "Hidden",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                invoker,
                "-ScriptPath",
                cliScript,
            ];
            arguments.AddRange(cliArgs);
        }
        else if (File.Exists(cliBatch))
        {
            fileName = Path.Combine(GetSystemRoot(), "System32", "cmd.exe");
            arguments = ["/d", "/c", cliBatch];
            arguments.AddRange(cliArgs);
        }
        else
        {
            throw new InvalidOperationException($"{productName} CLI launcher was not found. Expected cli.bat or cli.ps1 under: {productPath}");
        }

        var result = await RunLoggedProcessAsync(
            productName,
            fileName,
            arguments,
            productPath,
            timeoutSeconds,
            parentOperationId,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            var jsonMessage = GetJsonOutputErrorMessage(result.Stdout);
            if (string.IsNullOrEmpty(jsonMessage))
            {
                jsonMessage = GetJsonOutputErrorMessage(result.Stderr);
            }

            var message = !string.IsNullOrEmpty(jsonMessage)
                ? jsonMessage
                : !string.IsNullOrWhiteSpace(result.Stderr)
                    ? result.Stderr.Trim()
                    : !string.IsNullOrWhiteSpace(result.Stdout)
                        ? result.Stdout.Trim()
                        : $"exit code {result.ExitCode}";
            throw new InvalidOperationException($"{productName} CLI failed: {message}");
        }

        return result;
    }

    private async Task<CliProcessResult> RunLoggedProcessAsync(
        string productName,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int timeoutSeconds,
        string parentOperationId,
        CancellationToken cancellationToken)
    {
        var operationId = _operations.NewOperationId("cli");
        var commandLine = BuildCommandLine(fileName, arguments);
        var startedAt = DateTimeOffset.Now;
        _operations.WriteOperationEvent(
            operationId,
            "command",
            productName,
            "cli",
            "info",
            "CLI start.",
            commandLine: commandLine,
            parentOperationId: parentOperationId);

        try
        {
            var result = await RunProcessAsync(
                fileName,
                arguments,
                workingDirectory,
                timeoutSeconds,
                GetChildProcessEnvironment(),
                cancellationToken);
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "result",
                productName,
                "cli",
                result.ExitCode == 0 ? "success" : "error",
                result.ExitCode == 0 ? "CLI completed." : "CLI failed.",
                commandLine: commandLine,
                exitCode: result.ExitCode,
                durationMs: durationMs,
                stdout: result.Stdout,
                stderr: result.Stderr,
                parentOperationId: parentOperationId);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var durationMs = (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            _operations.WriteOperationEvent(
                operationId,
                "result",
                productName,
                "cli",
                "error",
                "CLI execution error.",
                commandLine: commandLine,
                durationMs: durationMs,
                stderr: ex.Message,
                parentOperationId: parentOperationId);
            throw;
        }
    }

    private async Task AssertProductCliAccessAllowedAsync(
        string productId,
        string productName,
        string productPath,
        CancellationToken cancellationToken)
    {
        var running = await IsProductHealthRunningAsync(productId, productPath, cancellationToken);
        if (!running)
        {
            throw new InvalidOperationException($"{productName} is not running. Start the product explicitly before accessing this endpoint.");
        }
    }

    private async Task<bool> IsProductHealthRunningAsync(
        string productId,
        string productPath,
        CancellationToken cancellationToken)
    {
        var baseUrl = GetProductHealthBaseUrl(productId, productPath);
        if (string.IsNullOrEmpty(baseUrl))
        {
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var text = await _http.GetStringAsync(baseUrl.TrimEnd('/') + "/health", timeout.Token);
            return TestHealthValue(text);
        }
        catch
        {
            return false;
        }
    }

    private string GetProductPath(string productId)
    {
        foreach (var product in _settings.ReadSettings().ProductRegistry.Products)
        {
            if (product.Id.Equals(productId, StringComparison.OrdinalIgnoreCase))
            {
                return product.Path ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private string GetProductHealthBaseUrl(string productId, string productPath)
    {
        if (string.IsNullOrEmpty(productPath) || !Directory.Exists(productPath))
        {
            return string.Empty;
        }

        var settings = ReadJsonObject(Path.Combine(productPath, "settings.json"));
        var runtime = GetObject(settings, "runtime");

        var apiBaseUrl = GetStringAny(runtime, ["apiBaseUrl", "api_base_url"], string.Empty);
        if (string.IsNullOrEmpty(apiBaseUrl))
        {
            apiBaseUrl = GetStringAny(settings, ["apiBaseUrl", "api_base_url"], string.Empty);
        }
        if (!string.IsNullOrEmpty(apiBaseUrl))
        {
            return apiBaseUrl.TrimEnd('/');
        }

        var hostName = GetStringAny(runtime, ["apiHost", "api_host"], string.Empty);
        if (string.IsNullOrEmpty(hostName))
        {
            hostName = GetStringAny(settings, ["apiHost", "api_host"], string.Empty);
        }
        if (string.IsNullOrEmpty(hostName) || hostName == "0.0.0.0")
        {
            hostName = "127.0.0.1";
        }

        var port = GetApiPort(GetNodeAny(runtime, ["apiPort", "api_port"]));
        if (port <= 0)
        {
            port = GetApiPort(GetNodeAny(settings, ["apiPort", "api_port"]));
        }
        if (port <= 0)
        {
            port = GetDefaultApiPort(productId);
        }
        return port > 0 ? $"http://{hostName}:{port}" : string.Empty;
    }

    private static async Task<CliProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = new UTF8Encoding(false);
        process.StartInfo.StandardErrorEncoding = new UTF8Encoding(false);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        foreach (var pair in environment)
        {
            process.StartInfo.Environment[pair.Key] = pair.Value;
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
            }

            throw new TimeoutException($"{fileName} timed out after {timeoutSeconds} seconds.");
        }

        return new CliProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private Dictionary<string, string> GetChildProcessEnvironment()
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("PATH")
            ?? string.Empty;
        var systemRoot = GetSystemRoot();
        var dockerBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker",
            "Docker",
            "resources",
            "bin");
        var system32 = Path.Combine(systemRoot, "System32");
        var powerShellBin = Path.Combine(system32, "WindowsPowerShell", "v1.0");

        if (File.Exists(Path.Combine(dockerBin, "docker.exe")))
        {
            currentPath = PrependExistingPath(currentPath, dockerBin);
        }
        currentPath = PrependExistingPath(currentPath, system32);
        currentPath = PrependExistingPath(currentPath, powerShellBin);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = currentPath,
            ["Path"] = currentPath,
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL",
            ["DOCKER_CONFIG"] = GetScopedDockerConfigDir(),
        };
    }

    private string GetScopedDockerConfigDir()
    {
        var root = ConvertTimelineText(_options.TimelineProductPath);
        if (string.IsNullOrEmpty(root))
        {
            root = Directory.GetCurrentDirectory();
        }

        var configDir = Path.Combine(root, ".docker", "docker-config");
        var configPath = Path.Combine(configDir, "config.json");
        Directory.CreateDirectory(configDir);
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, "{}", Encoding.ASCII);
        }

        return configDir;
    }

    private static JsonNode? ConvertFromJsonOutput(string text)
    {
        var jsonText = ConvertTimelineText(text);
        var objectStart = jsonText.IndexOf('{', StringComparison.Ordinal);
        var arrayStart = jsonText.IndexOf('[', StringComparison.Ordinal);
        int startIndex;
        int endIndex;
        if (arrayStart >= 0 && (objectStart < 0 || arrayStart < objectStart))
        {
            startIndex = arrayStart;
            endIndex = jsonText.LastIndexOf(']');
        }
        else
        {
            startIndex = objectStart;
            endIndex = jsonText.LastIndexOf('}');
        }

        if (startIndex < 0 || endIndex < startIndex)
        {
            throw new InvalidOperationException("Product CLI did not return JSON.");
        }

        var payload = JsonNode.Parse(jsonText[startIndex..(endIndex + 1)])
            ?? throw new InvalidOperationException("Product CLI did not return JSON.");
        if (payload is JsonObject obj
            && GetNode(obj, "ok") is JsonNode okNode
            && okNode.GetValueKind() == JsonValueKind.False)
        {
            var message = GetJsonErrorMessage(obj);
            throw new InvalidOperationException(string.IsNullOrEmpty(message) ? "Product CLI returned ok=false." : message);
        }

        return payload;
    }

    private static string GetJsonOutputErrorMessage(string text)
    {
        try
        {
            return ConvertFromJsonOutput(text) is JsonObject obj ? GetJsonErrorMessage(obj) : string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ex.Message == "Product CLI did not return JSON." ? string.Empty : ex.Message;
        }
    }

    private static string GetJsonErrorMessage(JsonObject payload)
    {
        var error = GetNode(payload, "error");
        if (error is not null)
        {
            if (error.GetValueKind() == JsonValueKind.String)
            {
                return ConvertNodeToString(error);
            }
            if (error is JsonObject errorObj)
            {
                return GetString(errorObj, "message", string.Empty);
            }
        }

        return GetString(payload, "message", string.Empty);
    }

    private static JsonObject? ReadJsonObject(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetCliScript(string productId, string productPath)
    {
        if (productId.Equals("pc", StringComparison.OrdinalIgnoreCase))
        {
            var primary = Path.Combine(productPath, "timeline-for-pc.ps1");
            if (File.Exists(primary))
            {
                return primary;
            }
        }

        return Path.Combine(productPath, "cli.ps1");
    }

    private static bool TestHealthValue(string value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try
            {
                return TestHealthValue(JsonNode.Parse(text));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TestHealthValue(JsonNode? value)
    {
        if (value is null)
        {
            return false;
        }

        var kind = value.GetValueKind();
        if (kind == JsonValueKind.True)
        {
            return true;
        }
        if (kind == JsonValueKind.False)
        {
            return false;
        }
        if (value is JsonObject obj && GetNodeAny(obj, ["ok", "healthy", "running"]) is JsonNode node)
        {
            return TestHealthValue(node);
        }

        return TestHealthValue(ConvertNodeToString(value));
    }

    private static JsonObject? GetObject(JsonObject? source, string name)
        => GetNode(source, name) as JsonObject;

    private static JsonNode? GetNodeAny(JsonObject? source, string[] names)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is not null)
            {
                return node;
            }
        }

        return null;
    }

    private static JsonNode? GetNode(JsonObject? source, string name)
    {
        if (source is null)
        {
            return null;
        }

        foreach (var property in source)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string GetStringAny(JsonObject? source, string[] names, string fallback)
    {
        foreach (var name in names)
        {
            var node = GetNode(source, name);
            if (node is not null)
            {
                return ConvertNodeToString(node);
            }
        }

        return fallback;
    }

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        return node is null ? fallback : ConvertNodeToString(node);
    }

    private static int GetApiPort(JsonNode? node)
    {
        var text = ConvertNodeToString(node);
        return int.TryParse(text, out var port) && port is >= 1 and <= 65535 ? port : 0;
    }

    private static int GetDefaultApiPort(string productId)
    {
        return productId switch
        {
            "audio" => 19100,
            "windows-codex" => 19200,
            "chatgpt" => 19300,
            "image" => 19400,
            "video" => 19500,
            "pc" => 19600,
            _ => 0,
        };
    }

    private static string BuildCommandLine(string fileName, IEnumerable<string> arguments)
    {
        return string.Join(" ", new[] { fileName }.Concat(arguments).Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        var text = ConvertTimelineText(value);
        if (text.Length == 0)
        {
            return "\"\"";
        }
        if (!text.Any(char.IsWhiteSpace) && !text.Contains('"'))
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string PrependExistingPath(string currentPath, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
        {
            return currentPath;
        }

        var parts = currentPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Any(part => part.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return currentPath;
        }

        return string.IsNullOrEmpty(currentPath) ? candidate : candidate + ";" + currentPath;
    }

    private static string GetPowerShellPath()
    {
        var candidate = Path.Combine(GetSystemRoot(), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : "powershell.exe";
    }

    private static string GetSystemRoot()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            return systemRoot;
        }

        return Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', '/') ?? @"C:\Windows";
    }

    private static string ConvertNodeToString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        try
        {
            return ConvertTimelineText(node.GetValue<object>());
        }
        catch (InvalidOperationException)
        {
            return node.ToJsonString();
        }
    }

    private static string ConvertTimelineText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }

    private sealed record CliProcessResult(int ExitCode, string Stdout, string Stderr);
}
