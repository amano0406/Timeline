namespace Timeline.Web.Services;

public sealed class TimelineRuntimeStatus
{
    public bool Available { get; set; }
    public string State { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public List<TimelineRuntimeComponentStatus> Components { get; set; } = [];
    public TimelineDockerWorkerStatus Worker { get; set; } = new();
    public AudioVerbalizationOllamaStatus Ollama { get; set; } = new();
    public ProductRuntimeOverview Products { get; set; } = new();
}

public sealed class TimelineRuntimeComponentStatus
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Available { get; set; }
    public string State { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string ActionKind { get; set; } = "";
    public string ActionLabel { get; set; } = "";
}

public sealed class TimelineRuntimeControlResult
{
    public bool Accepted { get; set; }
    public string State { get; set; } = "";
    public string Message { get; set; } = "";
    public string LauncherPath { get; set; } = "";
}
