using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class TimelineOperationLogService
{
    private const int ConsoleLogLimit = 500;

    private readonly TimelineSettingsService _settings;
    private readonly object _consoleLock = new();
    private readonly List<JsonObject> _consoleEntries = [];
    private long _consoleNextId;

    public TimelineOperationLogService(TimelineSettingsService settings)
    {
        _settings = settings;
    }

    public string NewOperationId(string prefix = "operation")
    {
        var safePrefix = GetTimelineZipSafeSegment(prefix);
        if (string.IsNullOrEmpty(safePrefix))
        {
            safePrefix = "operation";
        }

        return $"{safePrefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..(safePrefix.Length + 1 + 15 + 1 + 8)];
    }

    public void WriteOperationEvent(
        string operationId,
        string kind,
        string productName,
        string action,
        string state,
        string message,
        string commandLine = "",
        int? exitCode = null,
        int? durationMs = null,
        string stdout = "",
        string stderr = "",
        JsonNode? details = null,
        string parentOperationId = "")
    {
        var safeOperationId = GetTimelineZipSafeSegment(operationId);
        if (string.IsNullOrEmpty(safeOperationId))
        {
            return;
        }

        try
        {
            var directory = Path.Combine(GetOperationLogRoot(), safeOperationId);
            Directory.CreateDirectory(directory);
            var now = DateTimeOffset.Now.ToString("o");
            var parentId = ConvertTimelineText(parentOperationId);
            var entry = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["operationId"] = operationId,
                ["parentOperationId"] = parentId,
                ["occurredAt"] = now,
                ["kind"] = kind,
                ["productName"] = productName,
                ["action"] = action,
                ["state"] = state,
                ["message"] = message,
                ["commandLine"] = commandLine,
                ["exitCode"] = exitCode,
                ["durationMs"] = durationMs,
                ["stdoutTail"] = GetTextPreview(stdout),
                ["stderrTail"] = GetTextPreview(stderr),
                ["details"] = details?.DeepClone(),
            };

            File.AppendAllText(
                Path.Combine(directory, "events.jsonl"),
                entry.ToJsonString() + Environment.NewLine);

            var summary = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["operationId"] = operationId,
                ["parentOperationId"] = parentId,
                ["kind"] = kind,
                ["productName"] = productName,
                ["action"] = action,
                ["state"] = state,
                ["message"] = message,
                ["commandLine"] = commandLine,
                ["exitCode"] = exitCode,
                ["durationMs"] = durationMs,
                ["updatedAt"] = now,
                ["details"] = details?.DeepClone(),
            };

            File.WriteAllText(Path.Combine(directory, "summary.json"), summary.ToJsonString());
            AddConsoleEntry(
                kind,
                productName,
                action,
                state,
                message,
                commandLine,
                safeOperationId,
                parentId,
                exitCode,
                durationMs,
                stdout,
                stderr);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    public JsonObject GetConsoleLogs(long afterId, int limit)
    {
        lock (_consoleLock)
        {
            var take = Math.Min(Math.Max(1, limit), 300);
            if (afterId > _consoleNextId)
            {
                afterId = 0;
            }

            var entries = _consoleEntries
                .Where(entry => GetLong(entry, "id", 0) > afterId)
                .TakeLast(take)
                .Select(entry => entry.DeepClone())
                .ToList();
            var lastId = entries.Count > 0 ? GetLong(entries[^1] as JsonObject, "id", afterId) : afterId;
            var array = new JsonArray();
            foreach (var entry in entries)
            {
                array.Add(entry);
            }

            return new JsonObject
            {
                ["entries"] = array,
                ["lastId"] = lastId,
                ["count"] = _consoleEntries.Count,
            };
        }
    }

    public JsonObject ClearConsoleLogs()
    {
        lock (_consoleLock)
        {
            _consoleEntries.Clear();
            return new JsonObject
            {
                ["entries"] = new JsonArray(),
                ["lastId"] = _consoleNextId,
                ["count"] = 0,
            };
        }
    }

    public JsonObject GetOperations(int limit)
    {
        var root = GetOperationLogRoot();
        var effectiveLimit = Math.Min(Math.Max(1, limit), 300);
        var directories = Directory.GetDirectories(root)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToList();
        var operations = new JsonArray();

        foreach (var directory in directories.Take(effectiveLimit))
        {
            var summaryPath = Path.Combine(directory.FullName, "summary.json");
            if (!File.Exists(summaryPath))
            {
                continue;
            }

            try
            {
                var summary = JsonNode.Parse(File.ReadAllText(summaryPath)) as JsonObject;
                var converted = ConvertSummary(summary, directory.FullName);
                if (converted is not null)
                {
                    operations.Add(converted);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return new JsonObject
        {
            ["logRoot"] = root,
            ["total"] = directories.Count,
            ["operations"] = operations,
            ["message"] = string.Empty,
        };
    }

    public JsonObject GetOperationDetail(string? operationId)
    {
        var safeOperationId = GetTimelineZipSafeSegment(operationId);
        var directory = Path.Combine(GetOperationLogRoot(), safeOperationId);
        var summaryPath = Path.Combine(directory, "summary.json");
        var eventsPath = Path.Combine(directory, "events.jsonl");

        if (!File.Exists(summaryPath))
        {
            return new JsonObject
            {
                ["available"] = false,
                ["summary"] = null,
                ["events"] = new JsonArray(),
                ["logDirectory"] = directory,
                ["message"] = "Operation log was not found.",
            };
        }

        try
        {
            var summary = JsonNode.Parse(File.ReadAllText(summaryPath)) as JsonObject;
            var events = new JsonArray();
            if (File.Exists(eventsPath))
            {
                foreach (var line in File.ReadLines(eventsPath))
                {
                    var text = line.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    try
                    {
                        var converted = ConvertEvent(JsonNode.Parse(text) as JsonObject);
                        if (converted is not null)
                        {
                            events.Add(converted);
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }

            return new JsonObject
            {
                ["available"] = true,
                ["summary"] = ConvertSummary(summary, directory),
                ["events"] = events,
                ["logDirectory"] = directory,
                ["message"] = string.Empty,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new JsonObject
            {
                ["available"] = false,
                ["summary"] = null,
                ["events"] = new JsonArray(),
                ["logDirectory"] = directory,
                ["message"] = ex.Message,
            };
        }
    }

    private string GetOperationLogRoot()
    {
        var root = Path.Combine(_settings.GetDataRootDirectory(), "logs", "operations");
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private void AddConsoleEntry(
        string kind,
        string productName,
        string action,
        string state,
        string message,
        string commandLine,
        string operationId,
        string parentOperationId,
        int? exitCode,
        int? durationMs,
        string stdout,
        string stderr)
    {
        lock (_consoleLock)
        {
            _consoleNextId++;
            var entry = new JsonObject
            {
                ["id"] = _consoleNextId,
                ["occurredAt"] = DateTimeOffset.Now.ToString("o"),
                ["level"] = ConvertOperationStateToConsoleLevel(state),
                ["kind"] = kind,
                ["productName"] = productName,
                ["action"] = string.IsNullOrEmpty(action) && (kind == "command" || kind == "result") ? "process" : action,
                ["commandLine"] = commandLine,
                ["operationId"] = operationId,
                ["parentOperationId"] = parentOperationId,
                ["exitCode"] = exitCode,
                ["durationMs"] = durationMs,
                ["stdout"] = GetTextPreview(stdout),
                ["stderr"] = GetTextPreview(stderr),
                ["message"] = message,
            };

            _consoleEntries.Add(entry);
            while (_consoleEntries.Count > ConsoleLogLimit)
            {
                _consoleEntries.RemoveAt(0);
            }
        }
    }

    private static string ConvertOperationStateToConsoleLevel(string state)
    {
        return ConvertTimelineText(state).ToLowerInvariant() switch
        {
            "completed" or "success" => "success",
            "failed" or "error" => "error",
            "started" or "queued" or "running" or "info" => "info",
            var value when !string.IsNullOrEmpty(value) => value,
            _ => "info",
        };
    }

    private static JsonObject? ConvertSummary(JsonObject? summary, string logDirectory)
    {
        if (summary is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["operationId"] = GetString(summary, "operationId", string.Empty),
            ["parentOperationId"] = GetString(summary, "parentOperationId", string.Empty),
            ["kind"] = GetString(summary, "kind", string.Empty),
            ["productName"] = GetString(summary, "productName", string.Empty),
            ["action"] = GetString(summary, "action", string.Empty),
            ["state"] = GetString(summary, "state", string.Empty),
            ["message"] = GetString(summary, "message", string.Empty),
            ["commandLine"] = GetString(summary, "commandLine", string.Empty),
            ["exitCode"] = CloneNode(GetNode(summary, "exitCode")),
            ["durationMs"] = CloneNode(GetNode(summary, "durationMs")),
            ["updatedAt"] = GetString(summary, "updatedAt", string.Empty),
            ["details"] = CloneNode(GetNode(summary, "details")),
            ["logDirectory"] = logDirectory,
        };
    }

    private static JsonObject? ConvertEvent(JsonObject? entry)
    {
        if (entry is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["operationId"] = GetString(entry, "operationId", string.Empty),
            ["parentOperationId"] = GetString(entry, "parentOperationId", string.Empty),
            ["occurredAt"] = GetString(entry, "occurredAt", string.Empty),
            ["kind"] = GetString(entry, "kind", string.Empty),
            ["productName"] = GetString(entry, "productName", string.Empty),
            ["action"] = GetString(entry, "action", string.Empty),
            ["state"] = GetString(entry, "state", string.Empty),
            ["message"] = GetString(entry, "message", string.Empty),
            ["commandLine"] = GetString(entry, "commandLine", string.Empty),
            ["exitCode"] = CloneNode(GetNode(entry, "exitCode")),
            ["durationMs"] = CloneNode(GetNode(entry, "durationMs")),
            ["stdoutTail"] = GetString(entry, "stdoutTail", string.Empty),
            ["stderrTail"] = GetString(entry, "stderrTail", string.Empty),
            ["details"] = CloneNode(GetNode(entry, "details")),
        };
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

    private static string GetString(JsonObject? source, string name, string fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return ConvertTimelineText(node.GetValue<object>());
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static long GetLong(JsonObject? source, string name, long fallback)
    {
        var node = GetNode(source, name);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<long>();
            }

            return long.TryParse(ConvertTimelineText(node.GetValue<object>()), out var parsed)
                ? parsed
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static JsonNode? CloneNode(JsonNode? node)
        => node?.DeepClone();

    private static string GetTimelineZipSafeSegment(object? value)
    {
        var text = ConvertTimelineText(value);
        if (string.IsNullOrEmpty(text))
        {
            return "item";
        }

        var safe = System.Text.RegularExpressions.Regex.Replace(text, "[^A-Za-z0-9._-]+", "_").Trim('.', '_', '-');
        if (string.IsNullOrEmpty(safe))
        {
            return "item";
        }

        return safe.Length > 120 ? safe[..120] : safe;
    }

    private static string GetTextPreview(string text)
    {
        var value = ConvertTimelineText(text);
        if (value.Length <= 3000)
        {
            return value;
        }

        return "... (trimmed)\n" + value[^3000..];
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
}
