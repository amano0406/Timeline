using System.Text.Json;
using System.Text.Json.Nodes;

var workDirectory = GetConfiguredPath("Timeline__WorkDirectory", "/data/work");
var storeDirectory = GetConfiguredPath("Timeline__StoreDirectory", "/data/store");
var interval = TimeSpan.FromSeconds(5);

Directory.CreateDirectory(workDirectory);
Directory.CreateDirectory(storeDirectory);

var workerDirectory = Path.Combine(workDirectory, "worker");
Directory.CreateDirectory(workerDirectory);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    TryWriteHeartbeat("stopping");
};

Console.WriteLine("Timeline worker started.");
Console.WriteLine($"Work directory: {workDirectory}");
Console.WriteLine($"Store directory: {storeDirectory}");

while (!shutdown.IsCancellationRequested)
{
    TryWriteHeartbeat("running");

    try
    {
        await Task.Delay(interval, shutdown.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

TryWriteHeartbeat("stopped");
Console.WriteLine("Timeline worker stopped.");

void TryWriteHeartbeat(string state)
{
    try
    {
        var heartbeat = BuildHeartbeat(state);
        var path = Path.Combine(workerDirectory, "docker-worker-heartbeat.json");
        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(heartbeat, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to write heartbeat: {ex.Message}");
    }
}

object BuildHeartbeat(string state)
{
    var manifestPath = Path.Combine(storeDirectory, "manifest.json");
    var itemCount = 0;
    var eventCount = 0;
    var rebuildId = "";
    var createdAt = "";
    var storeAvailable = File.Exists(manifestPath)
        && File.Exists(Path.Combine(storeDirectory, "items.jsonl"))
        && File.Exists(Path.Combine(storeDirectory, "events.jsonl"));

    if (File.Exists(manifestPath))
    {
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
            itemCount = GetInt(manifest, "itemCount");
            eventCount = GetInt(manifest, "eventCount");
            rebuildId = GetString(manifest, "rebuildId");
            createdAt = GetString(manifest, "createdAt");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read manifest: {ex.Message}");
        }
    }

    return new
    {
        schemaVersion = 1,
        worker = "timeline-worker",
        state,
        updatedAt = DateTimeOffset.Now.ToString("O"),
        workDirectory,
        storeDirectory,
        storeAvailable,
        rebuildId,
        createdAt,
        itemCount,
        eventCount,
    };
}

static string GetConfiguredPath(string name, string fallback)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        return fallback;
    }

    return value;
}

static int GetInt(JsonObject? source, string name)
{
    if (source is null || !source.TryGetPropertyValue(name, out var value) || value is null)
    {
        return 0;
    }

    try
    {
        return value.GetValueKind() == JsonValueKind.Number
            ? value.GetValue<int>()
            : 0;
    }
    catch
    {
        return 0;
    }
}

static string GetString(JsonObject? source, string name)
{
    if (source is null || !source.TryGetPropertyValue(name, out var value) || value is null)
    {
        return "";
    }

    try
    {
        return value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>() ?? ""
            : "";
    }
    catch
    {
        return "";
    }
}
