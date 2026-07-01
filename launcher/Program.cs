using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

var options = LauncherOptions.Parse(args);
var root = TimelinePaths.ResolveRoot(options.Root);
var settings = TimelineSettings.Load(root);
var command = options.Command;

Console.OutputEncoding = System.Text.Encoding.UTF8;

try
{
    return command switch
    {
        "status" => await ShowStatus(root, settings),
        "preflight" => await ShowPreflight(root, settings, options.JsonOutput),
        "verify-setup" or "verify" => await VerifySetup(root, settings, options.JsonOutput),
        "version" => await ShowVersion(root, options.JsonOutput),
        "install-plan" => ShowInstallPlan(root, options.JsonOutput),
        "uninstall-plan" => ShowUninstallPlan(root, options.JsonOutput),
        "update-plan" => await ShowUpdatePlan(root, options.JsonOutput),
        "update-apply-plan" => await ShowUpdateApplyPlan(root, options.ArtifactPath, options.JsonOutput),
        "update-recovery-plan" => await ShowUpdateRecoveryPlan(root, options.ArtifactPath, options.JsonOutput),
        "update-validate" => ShowUpdateArtifactValidation(root, options.ArtifactPath, options.JsonOutput),
        "start" => await RunStart(root, settings, openBrowser: !options.NoOpen),
        "stop" => await RunStop(root, settings),
        "open" => await OpenOrStart(root, settings),
        "shortcut-status" => ShowShortcutStatus(root, options.JsonOutput),
        "shortcut-install" or "install-shortcut" => InstallShortcut(root, options.JsonOutput),
        "shortcut-remove" or "remove-shortcut" => RemoveShortcut(root, options.JsonOutput),
        "help" => ShowHelp(),
        _ => ShowUnknownCommand(command)
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("Timeline Launcher failed.");
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> ShowPreflight(string root, TimelineSettings settings, bool jsonOutput)
{
    var checks = await BuildPreflightChecks(root, settings);
    var exitCode = PreflightExitCode(checks);
    if (jsonOutput)
    {
        PrintPreflightJson(root, settings, checks, exitCode);
    }
    else
    {
        PrintPreflightChecks(checks);
    }

    return exitCode;
}

static async Task<List<PreflightCheck>> BuildPreflightChecks(string root, TimelineSettings settings)
{
    var checks = new List<PreflightCheck>();
    var settingsPath = Path.Combine(root, "settings.json");

    checks.Add(NewInfo("OS", GetPlatformDescription()));
    checks.Add(Directory.Exists(root)
        ? NewOk("Timeline root", root)
        : NewError("Timeline root", $"Directory was not found: {root}"));

    AddRequiredPathCheck(checks, root, "docker-compose.yml", requiredKind: "file");
    AddRequiredPathCheck(checks, root, "launcher", requiredKind: "directory");
    AddRequiredPathCheck(checks, root, "launcher-tray", requiredKind: "directory");
    AddRequiredPathCheck(checks, root, "local-api", requiredKind: "directory");
    AddRequiredPathCheck(checks, root, "web", requiredKind: "directory");

    checks.Add(File.Exists(settingsPath)
        ? NewOk("settings.json", $"Loaded from {settingsPath}")
        : NewWarning("settings.json", "settings.json was not found. Default ports will be used."));

    checks.Add(NewInfo("Configured Web", settings.WebUrl));
    checks.Add(NewInfo("Configured Local API", settings.LocalApiHealthUrl));

    var localApiRuntime = ResolveLocalApiRuntime(root);
    checks.Add(localApiRuntime.Severity switch
    {
        "error" => NewError("Local API runtime", localApiRuntime.Message),
        "warning" => NewWarning("Local API runtime", localApiRuntime.Message),
        _ => NewOk("Local API runtime", localApiRuntime.Message)
    });

    var dotnet = ResolveCommand(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
    if (localApiRuntime.RequiresDotnetCommand)
    {
        checks.Add(string.IsNullOrWhiteSpace(dotnet)
            ? NewError(".NET command", "dotnet command was not found on PATH, but this checkout needs it to run or publish Local API.")
            : NewOk(".NET command", dotnet));
    }
    else
    {
        checks.Add(string.IsNullOrWhiteSpace(dotnet)
            ? NewInfo(".NET command", "Bundled Local API runtime is present. dotnet command is not required for normal startup.")
            : NewInfo(".NET command", dotnet));
    }

    var docker = ResolveDockerCommand();
    checks.Add(string.IsNullOrWhiteSpace(docker)
        ? NewError("Docker command", "Docker command was not found.")
        : NewOk("Docker command", docker));

    var dockerStatus = string.IsNullOrWhiteSpace(docker)
        ? NewDockerProblemStatus(127, "Docker command could not be found.")
        : GetDockerStatus(root);
    checks.Add(dockerStatus.Available
        ? NewOk("Docker Engine", dockerStatus.Message)
        : NewError("Docker Engine", $"{dockerStatus.Message} {dockerStatus.Action}".Trim()));
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        AddWindowsDockerBackendChecks(checks, root);
    }
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        AddMacDockerChecks(checks, docker);
    }

    checks.Add(await IsWebReady(settings.WebHealthUrl)
        ? NewOk("Web health", $"{settings.WebHealthUrl} is responding.")
        : NewWarning("Web health", $"{settings.WebHealthUrl} is not responding. This is acceptable before startup."));

    checks.Add(await IsLocalApiReady(settings.LocalApiHealthUrl)
        ? NewOk("Local API health", $"{settings.LocalApiHealthUrl} is responding.")
        : NewWarning("Local API health", $"{settings.LocalApiHealthUrl} is not responding. This is acceptable before startup."));

    return checks;
}

static async Task<int> VerifySetup(string root, TimelineSettings settings, bool jsonOutput)
{
    var preflightChecks = await BuildPreflightChecks(root, settings);
    var checks = new List<SetupVerificationCheck>();

    foreach (var check in preflightChecks.Where(check => check.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)))
    {
        checks.Add(NewSetupError("prerequisite", check.Name, "failed", check.Message, "先に前提環境を修正してから再確認してください。"));
    }

    var dockerCheck = preflightChecks.FirstOrDefault(check => check.Name.Equals("Docker Engine", StringComparison.OrdinalIgnoreCase));
    if (dockerCheck is not null && !dockerCheck.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
    {
        checks.Add(NewSetupOk("runtime", "Docker", "running", dockerCheck.Message));
    }

    var localApiReady = await IsLocalApiReady(settings.LocalApiHealthUrl);
    checks.Add(localApiReady
        ? NewSetupOk("runtime", "Timeline Local API", "running", "Timeline の操作APIに接続できます。")
        : NewSetupError("runtime", "Timeline Local API", "not_responding", "Timeline の操作APIに接続できません。", "TimelineLauncher start を実行してから再確認してください。"));

    var webReady = await IsWebReady(settings.WebHealthUrl);
    checks.Add(webReady
        ? NewSetupOk("runtime", "Timeline Web", "running", "Timeline の画面に接続できます。")
        : NewSetupError("runtime", "Timeline Web", "not_responding", "Timeline の画面に接続できません。", "TimelineLauncher start を実行してから再確認してください。"));

    RuntimeStatus? runtimeStatus = null;
    if (localApiReady)
    {
        runtimeStatus = await FetchRuntimeStatus(settings.RuntimeStatusUrl);
        if (runtimeStatus is null)
        {
            checks.Add(NewSetupError("runtime", "Timeline runtime status", "unavailable", "Timeline の詳細な起動状態を取得できません。", "Local API を再起動してから再確認してください。"));
        }
        else
        {
            checks.Add(runtimeStatus.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                       runtimeStatus.Severity.Equals("danger", StringComparison.OrdinalIgnoreCase)
                ? NewSetupError("runtime", "Timeline runtime", runtimeStatus.State, runtimeStatus.Message, "表示されたコンポーネントのエラーを確認してください。")
                : runtimeStatus.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase)
                    ? NewSetupWarning("runtime", "Timeline runtime", runtimeStatus.State, runtimeStatus.Message, "必要に応じて警告のある項目を確認してください。")
                    : NewSetupOk("runtime", "Timeline runtime", runtimeStatus.State, runtimeStatus.Message));

            foreach (var component in runtimeStatus.Components)
            {
                if (component.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                    component.Severity.Equals("danger", StringComparison.OrdinalIgnoreCase))
                {
                    checks.Add(NewSetupError("component", component.Label, component.State, component.Message, "この項目を復旧してから再確認してください。"));
                }
                else if (component.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase))
                {
                    checks.Add(NewSetupWarning("component", component.Label, component.State, component.Message, "必要な機能であれば起動または設定を確認してください。"));
                }
                else
                {
                    checks.Add(NewSetupOk("component", component.Label, component.State, component.Message));
                }
            }
        }
    }

    var errorCount = checks.Count(check => check.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
    var warningCount = checks.Count(check => check.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));
    var state = errorCount > 0
        ? "blocked"
        : warningCount > 0
            ? "needs_attention"
            : "ready";
    var exitCode = errorCount > 0 ? 2 : warningCount > 0 ? 1 : 0;
    var message = state switch
    {
        "ready" => "Timeline は利用できる状態です。",
        "needs_attention" => "Timeline は起動していますが、確認した方がよい項目があります。",
        _ => "Timeline はまだ利用できる状態ではありません。表示された項目を修正してください。",
    };

    if (jsonOutput)
    {
        PrintSetupVerificationJson(root, settings, state, message, exitCode, errorCount, warningCount, checks);
    }
    else
    {
        PrintSetupVerification(state, message, checks);
    }

    return exitCode;
}

static async Task<int> OpenOrStart(string root, TimelineSettings settings)
{
    if (!await IsWebReady(settings.WebHealthUrl))
    {
        Console.WriteLine("Timeline is not running. Starting Timeline...");
        var exitCode = await RunStart(root, settings, openBrowser: false);
        if (exitCode != 0)
        {
            return exitCode;
        }
    }

    if (!await WaitForWeb(settings.WebHealthUrl, TimeSpan.FromSeconds(30)))
    {
        Console.Error.WriteLine("Timeline web did not become ready.");
        Console.Error.WriteLine($"Open manually after startup: {settings.WebUrl}");
        return 1;
    }

    Console.WriteLine($"Opening Timeline: {settings.WebUrl}");
    OpenUrl(settings.WebUrl);
    return 0;
}

static async Task<int> ShowStatus(string root, TimelineSettings settings)
{
    var runtimeStatus = await FetchRuntimeStatus(settings.RuntimeStatusUrl);
    if (runtimeStatus is not null)
    {
        Console.WriteLine("Timeline status");
        Console.WriteLine($"  {runtimeStatus.Message}");
        Console.WriteLine($"  state: {runtimeStatus.State}");
        Console.WriteLine();

        foreach (var component in runtimeStatus.Components)
        {
            Console.WriteLine($"- {component.Label}: {component.State}");
            if (!string.IsNullOrWhiteSpace(component.Message))
            {
                Console.WriteLine($"  {component.Message}");
            }
        }

        return runtimeStatus.Severity is "error" ? 2 : 0;
    }

    var localApiReady = await IsLocalApiReady(settings.LocalApiHealthUrl);

    Console.WriteLine("Timeline status");
    Console.WriteLine(localApiReady
        ? "  Timeline runtime status could not be read."
        : "  Timeline local API is not responding.");
    Console.WriteLine();
    var dockerStatus = GetDockerStatus(root);
    Console.WriteLine($"- Web: {(await IsWebReady(settings.WebHealthUrl) ? "running" : "not responding")}");
    Console.WriteLine($"- Local API: {(localApiReady ? "running" : "not responding")}");
    Console.WriteLine($"- Docker: {dockerStatus.State}");
    if (!string.IsNullOrWhiteSpace(dockerStatus.Message))
    {
        Console.WriteLine($"  {dockerStatus.Message}");
    }
    if (!string.IsNullOrWhiteSpace(dockerStatus.Action))
    {
        Console.WriteLine($"  Next action: {dockerStatus.Action}");
    }

    var dockerSummary = dockerStatus.Available ? TryGetDockerSummary(root) : string.Empty;
    if (!string.IsNullOrWhiteSpace(dockerSummary))
    {
        Console.WriteLine();
        Console.WriteLine("Docker containers containing timeline:");
        Console.WriteLine(dockerSummary);
    }

    Console.WriteLine();
    Console.WriteLine("To start Timeline, run: TimelineLauncher start");
    return 2;
}

static async Task<int> ShowVersion(string root, bool jsonOutput)
{
    var status = await TimelineVersionService.GetStatusAsync(root, CancellationToken.None);
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            status,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
    }
    else
    {
        Console.WriteLine("Timeline version");
        Console.WriteLine($"  current: {VersionText(status.CurrentVersion, status.CurrentVersionStatus)}");
        Console.WriteLine($"  commit: {EmptyText(status.CurrentCommit)}");
        Console.WriteLine($"  channel: {EmptyText(status.Channel)}");
        Console.WriteLine($"  runtime: {EmptyText(status.RuntimeIdentifier)}");
        Console.WriteLine($"  artifact: {EmptyText(status.ArtifactKind)}");
        Console.WriteLine($"  source: {EmptyText(status.VersionSource)}");
        Console.WriteLine($"  latest: {VersionText(status.LatestVersion, status.LatestVersionStatus)}");
        if (!string.IsNullOrWhiteSpace(status.LatestVersionMessage))
        {
            Console.WriteLine($"  latest status: {status.LatestVersionMessage}");
        }
        if (!string.IsNullOrWhiteSpace(status.ReleaseArtifactName))
        {
            Console.WriteLine($"  release artifact: {status.ReleaseArtifactName}");
        }
        Console.WriteLine($"  update available: {(status.UpdateAvailable ? "yes" : "no")}");
    }

    return status.CurrentVersionStatus == "ok" && status.LatestVersionStatus is "ok" or "no_release" or "asset_missing"
        ? 0
        : 1;
}

static async Task<int> ShowUpdatePlan(string root, bool jsonOutput)
{
    var plan = await TimelineUpdatePlanService.GetPlanAsync(root, CancellationToken.None);
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            plan,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
    }
    else
    {
        Console.WriteLine("Timeline update plan");
        Console.WriteLine($"  state: {plan.State}");
        Console.WriteLine($"  can update: {(plan.CanUpdate ? "yes" : "no")}");
        Console.WriteLine($"  owner: {plan.OperationOwner}");
        Console.WriteLine($"  mode: {plan.Mode}");
        Console.WriteLine($"  current: {VersionText(plan.Version.CurrentVersion, plan.Version.CurrentVersionStatus)}");
        Console.WriteLine($"  latest: {VersionText(plan.Version.LatestVersion, plan.Version.LatestVersionStatus)}");
        Console.WriteLine($"  artifact: {EmptyText(plan.Version.ArtifactKind)}");
        if (!string.IsNullOrWhiteSpace(plan.Version.ReleaseArtifactName))
        {
            Console.WriteLine($"  release artifact: {plan.Version.ReleaseArtifactName}");
        }
        Console.WriteLine();

        PrintUpdateMessages("Blockers", plan.Blockers);
        PrintUpdateMessages("Warnings", plan.Warnings);

        Console.WriteLine("Preserve:");
        foreach (var row in plan.Preserve)
        {
            Console.WriteLine($"  - {row.Id}: {row.Path}");
        }

        Console.WriteLine();
        Console.WriteLine("Replace:");
        foreach (var row in plan.Replace)
        {
            Console.WriteLine($"  - {row.Id}: {row.Path}");
        }

        Console.WriteLine();
        Console.WriteLine("Steps:");
        foreach (var step in plan.Steps.OrderBy(step => step.Order))
        {
            Console.WriteLine($"  {step.Order}. {step.Code}: {step.Message}");
        }
    }

    return 0;
}

static int ShowUninstallPlan(string root, bool jsonOutput)
{
    var plan = TimelineUninstallPlanService.GetPlan(root);
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            plan,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
    }
    else
    {
        Console.WriteLine("Timeline uninstall plan");
        Console.WriteLine($"  state: {plan.State}");
        Console.WriteLine($"  mode: {plan.Mode}");
        Console.WriteLine($"  can execute: {(plan.CanExecute ? "yes" : "no")}");
        Console.WriteLine($"  root: {plan.TimelineRoot}");
        Console.WriteLine($"  data root: {plan.DataRoot}");
        Console.WriteLine();
        PrintUninstallMessages("Warnings", plan.Warnings);

        foreach (var level in plan.Levels)
        {
            Console.WriteLine($"- {level.Id}: {level.Name}");
            Console.WriteLine($"  destructive: {(level.Destructive ? "yes" : "no")}");
            Console.WriteLine($"  default: {(level.RecommendedDefault ? "yes" : "no")}");
            Console.WriteLine($"  {level.Description}");
            foreach (var item in level.Items.Where(item => item.DefaultDelete))
            {
                Console.WriteLine($"    * {item.Id}: {item.Path}");
            }
        }
    }

    return 0;
}

static int ShowInstallPlan(string root, bool jsonOutput)
{
    var plan = TimelineInstallPlanService.GetPlan(root);
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            plan,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
    }
    else
    {
        Console.WriteLine("Timeline install plan");
        Console.WriteLine($"  state: {plan.State}");
        Console.WriteLine($"  mode: {plan.Mode}");
        Console.WriteLine($"  can execute: {(plan.CanExecute ? "yes" : "no")}");
        Console.WriteLine($"  platform: {plan.Platform}");
        Console.WriteLine($"  root: {plan.TimelineRoot}");
        Console.WriteLine($"  data root: {plan.DataRoot}");
        Console.WriteLine($"  launcher executable: {EmptyText(plan.LauncherExecutablePath)}");
        Console.WriteLine();
        PrintInstallMessages("Warnings", plan.Warnings);

        Console.WriteLine("Application entry:");
        PrintInstallRegistration(plan.AppEntry);

        Console.WriteLine();
        Console.WriteLine("Registration targets:");
        foreach (var target in plan.RegistrationTargets)
        {
            PrintInstallRegistration(target);
        }

        Console.WriteLine();
        Console.WriteLine("Installer artifacts:");
        foreach (var artifact in plan.ArtifactTargets)
        {
            Console.WriteLine($"  - {artifact.Id}: {artifact.Name}");
            Console.WriteLine($"    platform: {artifact.Platform}");
            Console.WriteLine($"    state: {artifact.State}");
            Console.WriteLine($"    {artifact.Description}");
        }

        Console.WriteLine();
        Console.WriteLine("Preserve:");
        foreach (var item in plan.Preserve)
        {
            Console.WriteLine($"  - {item.Id}: {item.Path}");
        }
    }

    return 0;
}

static void PrintInstallRegistration(TimelineInstallPlanRegistration target)
{
    Console.WriteLine($"  - {target.Id}: {target.Name}");
    Console.WriteLine($"    kind: {target.Kind}");
    Console.WriteLine($"    state: {target.State}");
    Console.WriteLine($"    supported: {(target.Supported ? "yes" : "no")}");
    Console.WriteLine($"    implemented: {(target.Implemented ? "yes" : "no")}");
    if (!string.IsNullOrWhiteSpace(target.CurrentPath))
    {
        Console.WriteLine($"    current: {target.CurrentPath}");
    }
    if (!string.IsNullOrWhiteSpace(target.TargetPath))
    {
        Console.WriteLine($"    target: {target.TargetPath}");
    }
    if (!string.IsNullOrWhiteSpace(target.CommandLine))
    {
        Console.WriteLine($"    command: {target.CommandLine}");
    }
    Console.WriteLine($"    {target.Message}");
}

static void PrintInstallMessages(string title, IReadOnlyList<TimelineInstallPlanMessage> messages)
{
    if (messages.Count == 0)
    {
        return;
    }

    Console.WriteLine(title + ":");
    foreach (var message in messages)
    {
        Console.WriteLine($"  - {message.Code}: {message.Message}");
    }
    Console.WriteLine();
}

static void PrintUninstallMessages(string title, IReadOnlyList<TimelineUninstallPlanMessage> messages)
{
    if (messages.Count == 0)
    {
        return;
    }

    Console.WriteLine(title + ":");
    foreach (var message in messages)
    {
        Console.WriteLine($"  - {message.Code}: {message.Message}");
    }
    Console.WriteLine();
}

static void PrintUpdateMessages(string title, IReadOnlyList<TimelineUpdatePlanMessage> messages)
{
    if (messages.Count == 0)
    {
        return;
    }

    Console.WriteLine(title + ":");
    foreach (var message in messages)
    {
        Console.WriteLine($"  - {message.Code}: {message.Message}");
    }
    Console.WriteLine();
}

static async Task<int> ShowUpdateApplyPlan(string root, string? artifactPath, bool jsonOutput)
{
    var plan = await TimelineUpdatePlanService.GetApplyPlanAsync(root, artifactPath, CancellationToken.None);
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            plan,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
    }
    else
    {
        Console.WriteLine("Timeline update apply plan");
        Console.WriteLine($"  state: {plan.State}");
        Console.WriteLine($"  can apply: {(plan.CanApply ? "yes" : "no")}");
        Console.WriteLine($"  owner: {plan.OperationOwner}");
        Console.WriteLine($"  mode: {plan.Mode}");
        Console.WriteLine($"  update plan state: {plan.UpdatePlanState}");
        Console.WriteLine($"  operation id: {plan.OperationId}");
        Console.WriteLine($"  staging root: {plan.StagingRoot}");
        Console.WriteLine($"  rollback root: {plan.RollbackRoot}");
        Console.WriteLine($"  requires confirmation: {(plan.RequiresConfirmation ? "yes" : "no")} ({plan.ConfirmationParameter})");
        if (plan.ArtifactValidation is not null)
        {
            Console.WriteLine($"  artifact valid: {(plan.ArtifactValidation.Valid ? "yes" : "no")}");
            Console.WriteLine($"  artifact: {plan.ArtifactValidation.ArtifactPath}");
        }
        Console.WriteLine();

        PrintUpdateMessages("Blockers", plan.Blockers);
        PrintUpdateMessages("Warnings", plan.Warnings);

        Console.WriteLine("Steps:");
        foreach (var step in plan.Steps.OrderBy(step => step.Order))
        {
            Console.WriteLine($"  {step.Order}. {step.Code}: {step.Message}");
        }
    }

    return 0;
}

static async Task<int> ShowUpdateRecoveryPlan(string root, string? artifactPath, bool jsonOutput)
{
    var plan = await TimelineUpdatePlanService.GetRecoveryPlanAsync(root, artifactPath, CancellationToken.None);
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            plan,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
    }
    else
    {
        Console.WriteLine("Timeline update recovery plan");
        Console.WriteLine($"  state: {plan.State}");
        Console.WriteLine($"  can prepare rollback: {(plan.CanPrepareRollback ? "yes" : "no")}");
        Console.WriteLine($"  owner: {plan.OperationOwner}");
        Console.WriteLine($"  mode: {plan.Mode}");
        Console.WriteLine($"  update plan state: {plan.UpdatePlanState}");
        Console.WriteLine($"  operation id: {plan.OperationId}");
        Console.WriteLine($"  staging root: {plan.StagingRoot}");
        Console.WriteLine($"  rollback root: {plan.RollbackRoot}");
        Console.WriteLine($"  app backup root: {plan.AppBackupRoot}");
        Console.WriteLine($"  operation log: {plan.OperationLogPath}");
        Console.WriteLine();

        Console.WriteLine("Data loss policy:");
        Console.WriteLine($"  {plan.DataLossPolicy}");
        Console.WriteLine();

        PrintUpdateMessages("Blockers", plan.Blockers);
        PrintUpdateMessages("Warnings", plan.Warnings);

        Console.WriteLine("Backup items:");
        foreach (var item in plan.BackupItems)
        {
            Console.WriteLine($"  - {item.Id}: {item.SourcePath}");
            Console.WriteLine($"    backup: {item.BackupPath}");
            Console.WriteLine($"    action: {item.RestoreAction}");
        }

        Console.WriteLine();
        Console.WriteLine("Failure policies:");
        foreach (var policy in plan.FailurePolicies)
        {
            Console.WriteLine($"  - {policy.Phase}: {policy.NextAction}");
        }

        Console.WriteLine();
        Console.WriteLine("Recovery steps:");
        foreach (var step in plan.RecoverySteps.OrderBy(step => step.Order))
        {
            Console.WriteLine($"  {step.Order}. {step.Code}: {step.Message}");
        }
    }

    return 0;
}

static int ShowUpdateArtifactValidation(string root, string? artifactPath, bool jsonOutput)
{
    if (string.IsNullOrWhiteSpace(artifactPath))
    {
        Console.Error.WriteLine("Update artifact path is required. Use --artifact <zip-path>.");
        return 2;
    }

    var result = TimelineUpdatePlanService.ValidateArtifact(root, artifactPath);
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
    }
    else
    {
        Console.WriteLine("Timeline update artifact validation");
        Console.WriteLine($"  state: {result.State}");
        Console.WriteLine($"  valid: {(result.Valid ? "yes" : "no")}");
        Console.WriteLine($"  structure valid: {(result.StructureValid ? "yes" : "no")}");
        Console.WriteLine($"  runtime compatible: {(result.RuntimeCompatible ? "yes" : "no")}");
        Console.WriteLine($"  artifact: {result.ArtifactPath}");
        Console.WriteLine($"  current runtime: {EmptyText(result.CurrentRuntimeIdentifier)}");
        Console.WriteLine($"  artifact runtime: {EmptyText(result.Version.RuntimeIdentifier)}");
        Console.WriteLine($"  artifact version: {EmptyText(result.Version.Version)}");
        Console.WriteLine($"  root prefix: {EmptyText(result.ArtifactRootPrefix)}");
        Console.WriteLine($"  entries: {result.EntryCount}");
        Console.WriteLine();
        PrintUpdateMessages("Blockers", result.Blockers);
        PrintUpdateMessages("Warnings", result.Warnings);

        Console.WriteLine("Required entries:");
        foreach (var row in result.Required)
        {
            Console.WriteLine($"  - {(row.Exists ? "OK" : "NG")} {row.Path}");
        }

        Console.WriteLine();
        Console.WriteLine("Forbidden entries:");
        foreach (var row in result.Forbidden)
        {
            Console.WriteLine($"  - {(row.Exists ? "NG" : "OK")} {row.Path}");
        }
    }

    return result.Valid ? 0 : 1;
}

static async Task<int> RunStart(string root, TimelineSettings settings, bool openBrowser)
{
    Console.WriteLine("Starting Timeline through the C# launcher runtime...");
    return await TimelineDirectRuntime.StartAsync(root, settings, openBrowser);
}

static async Task<int> RunStop(string root, TimelineSettings settings)
{
    Console.WriteLine("Stopping Timeline through the C# launcher runtime...");
    return await TimelineDirectRuntime.StopAsync(root, settings);
}

static int ShowShortcutStatus(string root, bool jsonOutput)
{
    var status = TimelineLauncherShortcutService.GetStatus(root);
    PrintShortcutStatus(status, jsonOutput);
    return status.State.Equals("failed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}

static int InstallShortcut(string root, bool jsonOutput)
{
    var status = TimelineLauncherShortcutService.Install(root);
    PrintShortcutStatus(status, jsonOutput);
    return status.Registered ? 0 : 1;
}

static int RemoveShortcut(string root, bool jsonOutput)
{
    var status = TimelineLauncherShortcutService.Remove(root);
    PrintShortcutStatus(status, jsonOutput);
    return status.State.Equals("failed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}

static void PrintShortcutStatus(TimelineLauncherShortcutStatus status, bool jsonOutput)
{
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            status,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
        return;
    }

    Console.WriteLine("Timeline app entry");
    Console.WriteLine($"  {status.Message}");
    Console.WriteLine($"  platform: {status.Platform}");
    Console.WriteLine($"  state: {status.State}");
    Console.WriteLine($"  registered: {status.Registered}");
    Console.WriteLine($"  kind: {status.Kind}");
    if (!string.IsNullOrWhiteSpace(status.ShortcutPath))
    {
        Console.WriteLine($"  shortcut: {status.ShortcutPath}");
    }
    var commandLine = TimelineLauncherShortcutService.FormatCommandLine(status);
    if (!string.IsNullOrWhiteSpace(commandLine))
    {
        Console.WriteLine($"  target: {commandLine}");
    }
}

static int ShowHelp()
{
    Console.WriteLine("Timeline Launcher");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  TimelineLauncher [open|status|preflight|verify-setup|version|install-plan|uninstall-plan|update-plan|update-apply-plan|update-recovery-plan|update-validate|start|stop|shortcut-status|shortcut-install|shortcut-remove|help] [--no-open] [--json]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  open    Open Timeline. Starts it first when needed.");
    Console.WriteLine("  status  Show Timeline runtime status.");
    Console.WriteLine("  preflight  Check local prerequisites before runtime verification. Use --json for Jira evidence.");
    Console.WriteLine("  verify-setup  Verify that Timeline is usable after setup. Use --json for Jira evidence.");
    Console.WriteLine("  version  Show current Timeline version and latest built artifact status.");
    Console.WriteLine("  install-plan  Show OS registration and installer targets before future installer execution.");
    Console.WriteLine("  uninstall-plan  Show delete levels and preserved data before future uninstall execution.");
    Console.WriteLine("  update-plan  Show the safe Timeline body update plan. Use --json for tooling.");
    Console.WriteLine("  update-apply-plan  Show whether a built product artifact can be applied now. Optional: --artifact <path>.");
    Console.WriteLine("  update-recovery-plan  Show rollback and failure recovery policy. Optional: --artifact <path>.");
    Console.WriteLine("  update-validate  Validate a built product artifact ZIP. Use --artifact <path>.");
    Console.WriteLine("  start   Start Timeline.");
    Console.WriteLine("  stop    Stop Timeline.");
    Console.WriteLine("  shortcut-status   Show the OS app entry status.");
    Console.WriteLine("  shortcut-install  Create or update the OS app entry.");
    Console.WriteLine("  shortcut-remove   Remove the OS app entry.");
    return 0;
}

static int ShowUnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    return ShowHelp() == 0 ? 2 : 1;
}

static async Task<bool> IsWebReady(string url) => await HttpOk(url);

static async Task<bool> IsLocalApiReady(string url) => await HttpOk(url);

static async Task<bool> WaitForWeb(string url, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow.Add(timeout);
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (await IsWebReady(url))
        {
            return true;
        }

        await Task.Delay(1000);
    }

    return false;
}

static async Task<bool> HttpOk(string url)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await client.GetAsync(url);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

static async Task<RuntimeStatus?> FetchRuntimeStatus(string url)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<RuntimeStatus>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch
    {
        return null;
    }
}

static string TryGetDockerSummary(string root)
{
    var docker = ResolveDockerCommand();
    if (string.IsNullOrWhiteSpace(docker))
    {
        return string.Empty;
    }

    var result = RunProcess(root, docker, "ps --format \"{{.Names}}\\t{{.Status}}\"", TimeSpan.FromSeconds(4));
    if (result.ExitCode != 0)
    {
        return string.Empty;
    }

    var lines = result.Output
        .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => line.Contains("timeline", StringComparison.OrdinalIgnoreCase))
        .Take(20)
        .ToArray();

    return lines.Length == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
}

static ProcessResult RunProcess(string root, string fileName, string arguments, TimeSpan timeout)
{
    try
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        process.Start();
        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup only.
            }

            return new ProcessResult(124, string.Empty, "Docker command timed out.");
        }

        return new ProcessResult(
            process.ExitCode,
            process.StandardOutput.ReadToEnd(),
            process.StandardError.ReadToEnd());
    }
    catch (Exception ex)
    {
        return new ProcessResult(127, string.Empty, ex.Message);
    }
}

static DockerStatus GetDockerStatus(string root)
{
    var docker = ResolveDockerCommand();
    if (string.IsNullOrWhiteSpace(docker))
    {
        return NewDockerProblemStatus(127, "Docker command could not be found.");
    }

    var result = RunProcess(root, docker, "info", TimeSpan.FromSeconds(4));
    if (result.ExitCode == 0)
    {
        return new DockerStatus(
            Available: true,
            State: "running",
            Message: "Docker は起動しています。",
            Action: "");
    }

    var details = CombineProcessText(result);
    return NewDockerProblemStatus(result.ExitCode, details);
}

static void AddWindowsDockerBackendChecks(List<PreflightCheck> checks, string root)
{
    checks.Add(ReadWindowsWslStatus(root));
    checks.Add(ReadWindowsHypervisorStatus(root));
}

static void AddMacDockerChecks(List<PreflightCheck> checks, string resolvedDockerCommand)
{
    var dockerAppPath = "/Applications/Docker.app";
    checks.Add(Directory.Exists(dockerAppPath)
        ? NewOk("Mac Docker Desktop", $"{dockerAppPath} was found.")
        : NewWarning("Mac Docker Desktop", "Docker.app was not found in /Applications. Install Docker Desktop or provide a compatible docker CLI."));

    var dockerDesktopCli = "/Applications/Docker.app/Contents/Resources/bin/docker";
    if (File.Exists(dockerDesktopCli))
    {
        checks.Add(NewOk("Mac Docker CLI", dockerDesktopCli));
        return;
    }

    checks.Add(string.IsNullOrWhiteSpace(resolvedDockerCommand)
        ? NewWarning("Mac Docker CLI", "docker command was not found on PATH and Docker Desktop's internal CLI was not found.")
        : NewOk("Mac Docker CLI", resolvedDockerCommand));
}

static PreflightCheck ReadWindowsWslStatus(string root)
{
    var wsl = ResolveWindowsSystemCommand("wsl.exe");
    if (string.IsNullOrWhiteSpace(wsl))
    {
        return NewWarning("Windows WSL", "wsl.exe was not found. Docker Desktop with the WSL2 backend may not work.");
    }

    var result = RunProcess(root, wsl, "--status", TimeSpan.FromSeconds(5));
    var details = CombineProcessText(result).Trim();
    if (result.ExitCode == 0)
    {
        return NewOk("Windows WSL", "wsl.exe responded. WSL can be inspected from this user session.");
    }

    if (LooksLikeWslMissing(details))
    {
        return NewWarning("Windows WSL", "WSL appears to be unavailable. Docker Desktop with the WSL2 backend may require WSL2 setup.");
    }

    return NewWarning("Windows WSL", ShortenDiagnostic(details, $"wsl.exe --status returned exit code {result.ExitCode}."));
}

static PreflightCheck ReadWindowsHypervisorStatus(string root)
{
    var systeminfo = ResolveWindowsSystemCommand("systeminfo.exe");
    if (string.IsNullOrWhiteSpace(systeminfo))
    {
        return NewInfo("Windows Hypervisor", "systeminfo.exe was not found. Hypervisor and virtualization state could not be inspected.");
    }

    var result = RunProcess(root, systeminfo, string.Empty, TimeSpan.FromSeconds(12));
    var details = CombineProcessText(result);
    if (result.ExitCode != 0)
    {
        return NewInfo("Windows Hypervisor", ShortenDiagnostic(details, "Hypervisor and virtualization state could not be inspected."));
    }

    var lines = details.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var hypervisorLine = lines.FirstOrDefault(line =>
        line.Contains("A hypervisor has been detected", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("hypervisor", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(hypervisorLine) &&
        hypervisorLine.Contains("detected", StringComparison.OrdinalIgnoreCase))
    {
        return NewOk("Windows Hypervisor", hypervisorLine);
    }

    var firmwareVirtualizationLine = lines.FirstOrDefault(line =>
        line.Contains("Virtualization Enabled In Firmware", StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(firmwareVirtualizationLine))
    {
        return firmwareVirtualizationLine.Contains("No", StringComparison.OrdinalIgnoreCase)
            ? NewWarning("Windows Hypervisor", "Firmware virtualization appears to be disabled. Docker Desktop / WSL2 may not start.")
            : NewOk("Windows Hypervisor", firmwareVirtualizationLine);
    }

    var virtualizationSecurityLine = lines.FirstOrDefault(line =>
        line.Contains("Virtualization-based security", StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(virtualizationSecurityLine) &&
        virtualizationSecurityLine.Contains("Running", StringComparison.OrdinalIgnoreCase))
    {
        return NewOk("Windows Hypervisor", virtualizationSecurityLine);
    }

    return NewInfo("Windows Hypervisor", "systeminfo did not expose a clear Hyper-V or firmware virtualization line.");
}

static string ResolveWindowsSystemCommand(string commandName)
{
    var resolved = ResolveCommand(commandName);
    if (!string.IsNullOrWhiteSpace(resolved))
    {
        return resolved;
    }

    var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    var candidate = Path.Combine(system32, commandName);
    return File.Exists(candidate) ? candidate : string.Empty;
}

static string ShortenDiagnostic(string details, string fallback)
{
    if (string.IsNullOrWhiteSpace(details))
    {
        return fallback;
    }

    var firstLine = details
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();
    if (string.IsNullOrWhiteSpace(firstLine))
    {
        return fallback;
    }

    return firstLine.Length <= 240 ? firstLine : firstLine[..240] + "...";
}

static DockerStatus NewDockerProblemStatus(int exitCode, string details)
{
    var state = ResolveDockerProblemState(exitCode, details);
    return new DockerStatus(
        Available: false,
        State: state,
        Message: DescribeDockerProblem(exitCode, details),
        Action: ResolveDockerProblemAction(state));
}

static string ResolveDockerProblemState(int exitCode, string details)
{
    if (exitCode == 124)
    {
        return "timeout";
    }

    if (exitCode == 127)
    {
        return "command_missing";
    }

    if (IsWslProblem(details))
    {
        return "wsl_problem";
    }

    if (IsVirtualizationProblem(details))
    {
        return "virtualization_problem";
    }

    if (IsDockerEngineUnavailable(details))
    {
        return "engine_stopped";
    }

    if (IsDockerCommandMissing(details))
    {
        return "command_missing";
    }

    return "unknown";
}

static string DescribeDockerProblem(int exitCode, string details)
{
    if (exitCode == 124)
    {
        return "Docker の状態確認がタイムアウトしました。Docker Desktop が起動途中、または応答していない可能性があります。";
    }

    if (exitCode == 127)
    {
        return "Docker コマンドが見つからない、または実行できません。Docker Desktop のインストールと PATH を確認してください。";
    }

    if (IsDockerEngineUnavailable(details))
    {
        return "Docker Engine が起動していません。Timeline の自動処理を使うには Docker Desktop の起動が必要です。";
    }

    if (IsWslProblem(details))
    {
        return "Docker は WSL2 バックエンドまたは WSL 関連の理由で利用できない可能性があります。";
    }

    if (IsVirtualizationProblem(details))
    {
        return "Docker は Windows の仮想化または Hyper-V 関連の理由で利用できない可能性があります。";
    }

    if (IsDockerCommandMissing(details))
    {
        return "Docker コマンドが見つからない、または実行できません。Docker Desktop のインストールと PATH を確認してください。";
    }

    return "Docker の状態を確認できません。Docker Desktop の状態を確認してください。";
}

static string ResolveDockerProblemAction(string state) => state switch
{
    "command_missing" => "Docker Desktop をインストールするか、docker.exe に PATH が通っている状態にしてから再実行してください。",
    "engine_stopped" => "Docker Desktop を起動してから、TimelineLauncher status または TimelineLauncher open を再実行してください。",
    "wsl_problem" => "Docker Desktop の WSL2 バックエンド設定、WSL2 の状態、Windows の再起動要否を確認してください。",
    "virtualization_problem" => "Windows の仮想化、Hyper-V、Virtual Machine Platform の設定を確認してください。",
    "timeout" => "Docker Desktop の起動が完了するまで待ってから、TimelineLauncher status を再実行してください。",
    _ => "Docker Desktop の状態を確認してから、TimelineLauncher status を再実行してください。",
};

static bool IsDockerCommandMissing(string details)
{
    return details.Contains("docker command could not be started", StringComparison.OrdinalIgnoreCase)
        || details.Contains("The system cannot find the file", StringComparison.OrdinalIgnoreCase)
        || details.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase);
}

static bool IsDockerEngineUnavailable(string details)
{
    return details.Contains("Docker daemon", StringComparison.OrdinalIgnoreCase)
        || details.Contains("dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase)
        || details.Contains("docker engine", StringComparison.OrdinalIgnoreCase)
        || details.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
        || details.Contains("pipe/docker", StringComparison.OrdinalIgnoreCase)
        || details.Contains("docker API", StringComparison.OrdinalIgnoreCase);
}

static bool IsWslProblem(string details)
{
    return details.Contains("WSL", StringComparison.OrdinalIgnoreCase)
        || details.Contains("wsl.exe", StringComparison.OrdinalIgnoreCase)
        || details.Contains("Windows Subsystem for Linux", StringComparison.OrdinalIgnoreCase)
        || details.Contains("Linux kernel", StringComparison.OrdinalIgnoreCase);
}

static bool IsVirtualizationProblem(string details)
{
    return details.Contains("virtualization", StringComparison.OrdinalIgnoreCase)
        || details.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
        || details.Contains("hypervisor", StringComparison.OrdinalIgnoreCase)
        || details.Contains("Virtual Machine Platform", StringComparison.OrdinalIgnoreCase);
}

static bool LooksLikeWslMissing(string details)
{
    return details.Contains("not installed", StringComparison.OrdinalIgnoreCase)
        || details.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
        || details.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || details.Contains("インストール", StringComparison.OrdinalIgnoreCase)
        || details.Contains("見つかりません", StringComparison.OrdinalIgnoreCase);
}

static string CombineProcessText(ProcessResult result)
{
    return string.Join(
        Environment.NewLine,
        new[] { result.Output, result.Error }.Where(text => !string.IsNullOrWhiteSpace(text)));
}

static string ResolveDockerCommand()
{
    var commandName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "docker.exe" : "docker";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var dockerDesktopCli = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker",
            "Docker",
            "resources",
            "bin",
            "docker.exe");
        if (File.Exists(dockerDesktopCli))
        {
            return dockerDesktopCli;
        }
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        var dockerDesktopCli = "/Applications/Docker.app/Contents/Resources/bin/docker";
        if (File.Exists(dockerDesktopCli))
        {
            return dockerDesktopCli;
        }
    }

    return ResolveCommand(commandName);
}

static string ResolveCommand(string commandName)
{
    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var candidate = Path.Combine(entry, commandName);
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return string.Empty;
}

static void AddRequiredPathCheck(List<PreflightCheck> checks, string root, string relativePath, string requiredKind)
{
    var fullPath = Path.Combine(root, relativePath);
    var exists = requiredKind.Equals("file", StringComparison.OrdinalIgnoreCase)
        ? File.Exists(fullPath)
        : Directory.Exists(fullPath);

    checks.Add(exists
        ? NewOk(relativePath, fullPath)
        : NewError(relativePath, $"Required {requiredKind} was not found: {fullPath}"));
}

static LocalApiRuntimeCheck ResolveLocalApiRuntime(string root)
{
    var localApiDirectory = Path.Combine(root, "local-api");
    if (!Directory.Exists(localApiDirectory))
    {
        return new LocalApiRuntimeCheck(
            "error",
            "Local API directory was not found.",
            RequiresDotnetCommand: false);
    }

    var executablePath = Path.Combine(localApiDirectory, LocalApiExecutableFileName());
    if (File.Exists(executablePath))
    {
        return new LocalApiRuntimeCheck(
            "ok",
            $"Bundled executable was found: {executablePath}",
            RequiresDotnetCommand: false);
    }

    var dllPath = Path.Combine(localApiDirectory, "Timeline.LocalApi.dll");
    if (File.Exists(dllPath))
    {
        return new LocalApiRuntimeCheck(
            "ok",
            $"Bundled DLL was found: {dllPath}",
            RequiresDotnetCommand: true);
    }

    var projectPath = Path.Combine(localApiDirectory, "Timeline.LocalApi.csproj");
    if (File.Exists(projectPath))
    {
        return new LocalApiRuntimeCheck(
            "ok",
            $"Development project was found: {projectPath}",
            RequiresDotnetCommand: true);
    }

    return new LocalApiRuntimeCheck(
        "error",
        "Local API executable, DLL, or project file was not found.",
        RequiresDotnetCommand: false);
}

static string LocalApiExecutableFileName()
{
    return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "Timeline.LocalApi.exe"
        : "Timeline.LocalApi";
}

static void PrintPreflightChecks(IReadOnlyList<PreflightCheck> checks)
{
    Console.WriteLine("Timeline preflight");
    Console.WriteLine("  Checks local prerequisites for runtime verification.");
    Console.WriteLine();

    foreach (var check in checks)
    {
        Console.WriteLine($"- [{PreflightSeverityLabel(check.Severity)}] {check.Name}");
        if (!string.IsNullOrWhiteSpace(check.Message))
        {
            Console.WriteLine($"  {check.Message}");
        }
    }

    Console.WriteLine();
    var errors = checks.Count(check => check.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
    var warnings = checks.Count(check => check.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(errors > 0
        ? $"Result: {errors} error(s), {warnings} warning(s). Fix errors before runtime verification."
        : warnings > 0
            ? $"Result: {warnings} warning(s). Runtime verification can continue if these are expected."
            : "Result: all preflight checks passed.");
}

static void PrintPreflightJson(
    string root,
    TimelineSettings settings,
    IReadOnlyList<PreflightCheck> checks,
    int exitCode)
{
    var errors = checks.Count(check => check.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
    var warnings = checks.Count(check => check.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));
    var state = errors > 0
        ? "error"
        : warnings > 0
            ? "warning"
            : "ok";

    var report = new PreflightReport(
        GeneratedAt: DateTimeOffset.UtcNow,
        State: state,
        ExitCode: exitCode,
        ErrorCount: errors,
        WarningCount: warnings,
        Root: root,
        WebUrl: settings.WebUrl,
        LocalApiHealthUrl: settings.LocalApiHealthUrl,
        Checks: checks.ToArray());

    Console.WriteLine(JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
}

static void PrintSetupVerification(
    string state,
    string message,
    IReadOnlyList<SetupVerificationCheck> checks)
{
    Console.WriteLine("Timeline setup verification");
    Console.WriteLine($"  state: {state}");
    Console.WriteLine($"  {message}");
    Console.WriteLine();

    foreach (var check in checks)
    {
        Console.WriteLine($"- [{PreflightSeverityLabel(check.Severity)}] {check.Name}: {check.State}");
        if (!string.IsNullOrWhiteSpace(check.Message))
        {
            Console.WriteLine($"  {check.Message}");
        }
        if (!string.IsNullOrWhiteSpace(check.Action))
        {
            Console.WriteLine($"  Next action: {check.Action}");
        }
    }
}

static void PrintSetupVerificationJson(
    string root,
    TimelineSettings settings,
    string state,
    string message,
    int exitCode,
    int errorCount,
    int warningCount,
    IReadOnlyList<SetupVerificationCheck> checks)
{
    var report = new SetupVerificationReport(
        GeneratedAt: DateTimeOffset.UtcNow,
        State: state,
        Message: message,
        ExitCode: exitCode,
        ErrorCount: errorCount,
        WarningCount: warningCount,
        Root: root,
        WebUrl: settings.WebUrl,
        WebHealthUrl: settings.WebHealthUrl,
        LocalApiHealthUrl: settings.LocalApiHealthUrl,
        RuntimeStatusUrl: settings.RuntimeStatusUrl,
        Checks: checks.ToArray());

    Console.WriteLine(JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
}

static int PreflightExitCode(IEnumerable<PreflightCheck> checks)
{
    if (checks.Any(check => check.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)))
    {
        return 2;
    }

    return checks.Any(check => check.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase))
        ? 1
        : 0;
}

static string PreflightSeverityLabel(string severity) => severity switch
{
    "ok" => "OK",
    "warning" => "WARN",
    "error" => "ERROR",
    _ => "INFO",
};

static string EmptyText(string? value)
    => string.IsNullOrWhiteSpace(value) ? "-" : value;

static string VersionText(string? version, string? status)
{
    if (!string.IsNullOrWhiteSpace(version))
    {
        return version;
    }

    return string.IsNullOrWhiteSpace(status) ? "unknown" : status;
}

static PreflightCheck NewOk(string name, string message) => new("ok", name, message);

static PreflightCheck NewWarning(string name, string message) => new("warning", name, message);

static PreflightCheck NewError(string name, string message) => new("error", name, message);

static PreflightCheck NewInfo(string name, string message) => new("info", name, message);

static SetupVerificationCheck NewSetupOk(string area, string name, string state, string message)
    => new("ok", area, name, state, message, "");

static SetupVerificationCheck NewSetupWarning(string area, string name, string state, string message, string action)
    => new("warning", area, name, state, message, action);

static SetupVerificationCheck NewSetupError(string area, string name, string state, string message, string action)
    => new("error", area, name, state, message, action);

static string GetPlatformDescription()
{
    var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "Windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "macOS"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "Linux"
                : "Unknown";

    return $"{platform} / {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}";
}

static void OpenUrl(string url)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
            return;
        }

        Process.Start("xdg-open", url);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to open browser: {ex.Message}");
    }
}

internal sealed record LauncherOptions(string? Root, string Command, bool NoOpen, bool JsonOutput, string? ArtifactPath)
{
    public static LauncherOptions Parse(string[] args)
    {
        string? root = null;
        string? artifactPath = null;
        var command = "open";
        var noOpen = false;
        var jsonOutput = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg == "--no-open")
            {
                noOpen = true;
                continue;
            }

            if (arg == "--json")
            {
                jsonOutput = true;
                continue;
            }

            if (arg == "--root" && index + 1 < args.Length)
            {
                root = args[++index];
                continue;
            }

            if (arg.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
            {
                root = arg["--root=".Length..];
                continue;
            }

            if ((arg == "--artifact" || arg == "--artifact-path") && index + 1 < args.Length)
            {
                artifactPath = args[++index];
                continue;
            }

            if (arg.StartsWith("--artifact=", StringComparison.OrdinalIgnoreCase))
            {
                artifactPath = arg["--artifact=".Length..];
                continue;
            }

            if (arg.StartsWith("--artifact-path=", StringComparison.OrdinalIgnoreCase))
            {
                artifactPath = arg["--artifact-path=".Length..];
                continue;
            }

            command = arg.Trim().ToLowerInvariant();
        }

        return new LauncherOptions(root, command, noOpen, jsonOutput, artifactPath);
    }
}

internal sealed record TimelineSettings(int WebPort, int LocalApiPort)
{
    public string WebUrl => $"http://127.0.0.1:{WebPort}";
    public string WebHealthUrl => $"{WebUrl}/api/health";
    public string LocalApiHealthUrl => $"http://127.0.0.1:{LocalApiPort}/health";
    public string RuntimeStatusUrl => $"http://127.0.0.1:{LocalApiPort}/timeline/runtime/status";

    public static TimelineSettings Load(string root)
    {
        var webPort = 19000;
        var localApiPort = 19001;
        var settingsPath = Path.Combine(root, "settings.json");
        if (!File.Exists(settingsPath))
        {
            return new TimelineSettings(webPort, localApiPort);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("runtime", out var runtime))
            {
                webPort = ReadPort(runtime, "webPort", webPort);
                localApiPort = ReadPort(runtime, "localApiPortStart", localApiPort);
            }
        }
        catch
        {
            // Keep defaults when settings cannot be read.
        }

        return new TimelineSettings(webPort, localApiPort);
    }

    private static int ReadPort(JsonElement runtime, string propertyName, int fallback)
    {
        if (!runtime.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}

internal sealed record RuntimeStatus(
    string State,
    string Severity,
    string Message,
    RuntimeComponent[] Components);

internal sealed record RuntimeComponent(
    string Label,
    string State,
    string Severity,
    string Message);

internal sealed record DockerStatus(bool Available, string State, string Message, string Action);

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal sealed record PreflightCheck(string Severity, string Name, string Message);

internal sealed record SetupVerificationCheck(
    string Severity,
    string Area,
    string Name,
    string State,
    string Message,
    string Action);

internal sealed record LocalApiRuntimeCheck(string Severity, string Message, bool RequiresDotnetCommand);

internal sealed record PreflightReport(
    DateTimeOffset GeneratedAt,
    string State,
    int ExitCode,
    int ErrorCount,
    int WarningCount,
    string Root,
    string WebUrl,
    string LocalApiHealthUrl,
    PreflightCheck[] Checks);

internal sealed record SetupVerificationReport(
    DateTimeOffset GeneratedAt,
    string State,
    string Message,
    int ExitCode,
    int ErrorCount,
    int WarningCount,
    string Root,
    string WebUrl,
    string WebHealthUrl,
    string LocalApiHealthUrl,
    string RuntimeStatusUrl,
    SetupVerificationCheck[] Checks);

internal static class TimelinePaths
{
    public static string ResolveRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "docker-compose.yml")) &&
                Directory.Exists(Path.Combine(current.FullName, "local-api")) &&
                Directory.Exists(Path.Combine(current.FullName, "web")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

}
