using System.Text.Json.Serialization;

public sealed class TimelineRuntimeStatusService
{
    private readonly TimelineWorkerStatusService _worker;
    private readonly TimelineOllamaStatusService _ollama;
    private readonly TimelineProductRuntimeService _products;

    public TimelineRuntimeStatusService(
        TimelineWorkerStatusService worker,
        TimelineOllamaStatusService ollama,
        TimelineProductRuntimeService products)
    {
        _worker = worker;
        _ollama = ollama;
        _products = products;
    }

    public async Task<TimelineRuntimeStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var worker = _worker.GetStatus();
        var ollamaTask = _ollama.GetStatusAsync(null, null, cancellationToken);
        var productsTask = _products.GetOverviewAsync(cancellationToken);

        await Task.WhenAll(ollamaTask, productsTask);

        var ollama = await ollamaTask;
        var products = await productsTask;
        var components = new List<TimelineRuntimeComponentStatusResponse>
        {
            NewComponent(
                "web",
                "Web画面",
                "web",
                available: true,
                state: "running",
                severity: "ok",
                message: "Timeline の画面を表示できています。"),
            NewComponent(
                "local-api",
                "操作機能",
                "local-api",
                available: true,
                state: "running",
                severity: "ok",
                message: "起動、復旧、設定保存に使う補助機能が応答しています。"),
            BuildDockerComponent(worker),
            BuildWorkerComponent(worker),
            BuildOllamaComponent(ollama),
            BuildProductsComponent(products),
        };

        var severity = ResolveOverallSeverity(components);
        return new TimelineRuntimeStatusResponse
        {
            Available = severity is not "danger",
            State = severity is "ok" ? "running" : "needs_attention",
            Severity = severity,
            Message = OverallMessage(severity),
            UpdatedAt = DateTimeOffset.Now.ToString("o"),
            Components = components,
            Worker = worker,
            Ollama = ollama,
            Products = products,
        };
    }

    private static TimelineRuntimeComponentStatusResponse BuildDockerComponent(TimelineDockerWorkerStatusResponse worker)
    {
        if (worker.State.Equals("docker_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return NewComponent(
                "docker",
                "Docker",
                "docker",
                available: false,
                state: "stopped",
                severity: "danger",
                message: "Docker が起動していません。自動処理を動かす前に復旧が必要です。",
                actionKind: "worker-repair",
                actionLabel: "Docker と自動処理を復旧");
        }

        if (worker.State.Equals("unreadable", StringComparison.OrdinalIgnoreCase))
        {
            return NewComponent(
                "docker",
                "Docker",
                "docker",
                available: false,
                state: "unknown",
                severity: "warning",
                message: "Docker の状態を確認できません。必要に応じて復旧を試してください。",
                actionKind: "worker-repair",
                actionLabel: "復旧を試す");
        }

        return NewComponent(
            "docker",
            "Docker",
            "docker",
            available: true,
            state: "running",
            severity: "ok",
            message: "Docker は Timeline の自動処理から確認できています。");
    }

    private static TimelineRuntimeComponentStatusResponse BuildWorkerComponent(TimelineDockerWorkerStatusResponse worker)
    {
        if (worker.Available && worker.State.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            return NewComponent(
                "worker",
                "自動処理",
                "worker",
                available: true,
                state: "running",
                severity: "ok",
                message: "時間軸の作成に使う自動処理が動いています。");
        }

        var message = string.IsNullOrWhiteSpace(worker.Message)
            ? "自動処理が止まっている可能性があります。"
            : worker.Message;
        return NewComponent(
            "worker",
            "自動処理",
            "worker",
            available: false,
            state: string.IsNullOrWhiteSpace(worker.State) ? "unknown" : worker.State,
            severity: "danger",
            message: message,
            actionKind: "worker-repair",
            actionLabel: "自動処理を復旧");
    }

    private static TimelineRuntimeComponentStatusResponse BuildOllamaComponent(TimelineOllamaStatusResponse ollama)
    {
        if (!ollama.Available)
        {
            return NewComponent(
                "ollama",
                "Ollama / AIモデル",
                "ollama",
                available: false,
                state: "stopped",
                severity: "warning",
                message: "Ollama に接続できません。概要生成や言語化を使う前に確認が必要です。");
        }

        if (!ollama.ModelAvailable)
        {
            return NewComponent(
                "ollama",
                "Ollama / AIモデル",
                "ollama",
                available: true,
                state: "model_missing",
                severity: "warning",
                message: $"Ollama は動いていますが、設定中のモデル {ollama.Model} が見つかりません。");
        }

        return NewComponent(
            "ollama",
            "Ollama / AIモデル",
            "ollama",
            available: true,
            state: "running",
            severity: "ok",
            message: $"Ollama と {ollama.Model} を利用できます。");
    }

    private static TimelineRuntimeComponentStatusResponse BuildProductsComponent(ProductRuntimeOverviewResponse products)
    {
        var supported = products.Products
            .Where(product => product.SupportedOnCurrentOperatingSystem)
            .ToList();
        var installed = supported
            .Where(product => product.ProductFound && (product.ComposeFound || product.Id.Equals("pc", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var running = installed.Count(product => product.Running || product.State.Equals("running", StringComparison.OrdinalIgnoreCase));
        var broken = supported.Count(product => product.ProductFound && !product.ComposeFound && !product.Id.Equals("pc", StringComparison.OrdinalIgnoreCase));

        if (supported.Count == 0)
        {
            return NewComponent(
                "products",
                "サブ製品",
                "products",
                available: false,
                state: "unknown",
                severity: "warning",
                message: "このOSで利用できるサブ製品を確認できません。",
                actionKind: "products",
                actionLabel: "製品管理を開く");
        }

        if (broken > 0)
        {
            return NewComponent(
                "products",
                "サブ製品",
                "products",
                available: false,
                state: "broken",
                severity: "warning",
                message: $"起動に必要なファイルが不足している製品が {broken:N0} 件あります。",
                actionKind: "products",
                actionLabel: "製品管理を開く");
        }

        if (installed.Count == 0)
        {
            return NewComponent(
                "products",
                "サブ製品",
                "products",
                available: false,
                state: "not_installed",
                severity: "warning",
                message: "Timeline で使うサブ製品がまだ準備されていません。",
                actionKind: "products",
                actionLabel: "製品管理を開く");
        }

        var stopped = Math.Max(0, installed.Count - running);
        if (stopped > 0)
        {
            return NewComponent(
                "products",
                "サブ製品",
                "products",
                available: true,
                state: "partial",
                severity: "warning",
                message: $"サブ製品 {installed.Count:N0} 件中 {running:N0} 件が稼働中です。停止中の製品は必要に応じて起動してください。",
                actionKind: "products",
                actionLabel: "起動状態を確認");
        }

        return NewComponent(
            "products",
            "サブ製品",
            "products",
            available: true,
            state: "running",
            severity: "ok",
            message: $"サブ製品 {installed.Count:N0} 件が稼働中です。");
    }

    private static TimelineRuntimeComponentStatusResponse NewComponent(
        string id,
        string label,
        string kind,
        bool available,
        string state,
        string severity,
        string message,
        string actionKind = "",
        string actionLabel = "")
    {
        return new TimelineRuntimeComponentStatusResponse
        {
            Id = id,
            Label = label,
            Kind = kind,
            Available = available,
            State = state,
            Severity = severity,
            Message = message,
            ActionKind = actionKind,
            ActionLabel = actionLabel,
        };
    }

    private static string ResolveOverallSeverity(IEnumerable<TimelineRuntimeComponentStatusResponse> components)
    {
        if (components.Any(component => component.Severity.Equals("danger", StringComparison.OrdinalIgnoreCase)))
        {
            return "danger";
        }
        if (components.Any(component => component.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase)))
        {
            return "warning";
        }
        return "ok";
    }

    private static string OverallMessage(string severity) => severity switch
    {
        "danger" => "Timeline の利用前に復旧が必要な項目があります。",
        "warning" => "Timeline は起動していますが、確認した方がよい項目があります。",
        _ => "Timeline は利用できる状態です。",
    };
}

public sealed class TimelineRuntimeStatusResponse
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";

    [JsonPropertyName("components")]
    public List<TimelineRuntimeComponentStatusResponse> Components { get; set; } = [];

    [JsonPropertyName("worker")]
    public TimelineDockerWorkerStatusResponse Worker { get; set; } = new();

    [JsonPropertyName("ollama")]
    public TimelineOllamaStatusResponse Ollama { get; set; } = new();

    [JsonPropertyName("products")]
    public ProductRuntimeOverviewResponse Products { get; set; } = new();
}

public sealed class TimelineRuntimeComponentStatusResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("actionKind")]
    public string ActionKind { get; set; } = "";

    [JsonPropertyName("actionLabel")]
    public string ActionLabel { get; set; } = "";
}
