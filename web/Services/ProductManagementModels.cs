namespace Timeline.Web.Services;

public sealed class ProductRuntimeOverview
{
    public List<ProductRuntimeRow> Products { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class ProductRuntimeRow
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string PagePath { get; set; } = "";
    public string SettingsPath { get; set; } = "";
    public string ProductPath { get; set; } = "";
    public string Path { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Version { get; set; } = "";
    public bool AppManagedByTimeline { get; set; }
    public bool DestructiveActionsDisabled { get; set; }
    public string InstalledVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string LatestVersionStatus { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public bool SourceArchiveUpdateAvailable { get; set; }
    public string ReleaseArchiveUrl { get; set; } = "";
    public bool SettingsBackupAvailable { get; set; }
    public string SettingsBackupPath { get; set; } = "";
    public string SettingsBackupAt { get; set; } = "";
    public string CurrentOperatingSystem { get; set; } = "";
    public List<string> SupportedOperatingSystems { get; set; } = [];
    public bool SupportedOnCurrentOperatingSystem { get; set; } = true;
    public string UnsupportedOperatingSystemMessage { get; set; } = "";
    public ProductManifestSummary Manifest { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public bool ProductFound { get; set; }
    public bool ComposeFound { get; set; }
    public bool StartFound { get; set; }
    public bool StopFound { get; set; }
    public string ContainerName { get; set; } = "";
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Running { get; set; }
    public string StartedAt { get; set; } = "";
    public int ExitCode { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ProductManifestSummary
{
    public bool Found { get; set; }
    public int SchemaVersion { get; set; }
    public string ProductId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ProductKind { get; set; } = "";
    public string SettingsFile { get; set; } = "";
    public bool SupportsBasicDefaults { get; set; }
    public bool SupportsProductOverrides { get; set; }
    public ProductCapabilitySummary Capabilities { get; set; } = new();
}

public sealed class ProductCapabilitySummary
{
    public bool FileList { get; set; }
    public bool ItemList { get; set; }
    public bool ItemRefresh { get; set; }
    public bool ItemDownload { get; set; }
    public bool ItemRemove { get; set; }
    public bool ModelList { get; set; }
    public bool Verbalization { get; set; }
}

public sealed class ProductInstallRequest
{
    public bool RestoreSettingsBackup { get; set; } = true;
}

public sealed class ProductUninstallRequest
{
    public bool KeepSettings { get; set; } = true;
    public bool RemoveGeneratedData { get; set; }
}

public sealed class ProductUpdatePlan
{
    public string ProductId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string State { get; set; } = "";
    public string DistributionMode { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public bool SourceArchiveUpdateAvailable { get; set; }
    public string BuiltArtifactStatus { get; set; } = "";
    public string BuiltArtifactMessage { get; set; } = "";
    public string BuiltArtifactVersion { get; set; } = "";
    public string BuiltArtifactName { get; set; } = "";
    public string BuiltArtifactRuntime { get; set; } = "";
    public bool BuiltArtifactUpdateAvailable { get; set; }
    public bool CanUseBuiltArtifactUpdater { get; set; }
    public bool Running { get; set; }
    public bool ProductFound { get; set; }
    public bool ComposeFound { get; set; }
    public bool AppManagedByTimeline { get; set; }
    public List<ProductUpdatePathPlan> Preserve { get; set; } = [];
    public List<ProductUpdatePathPlan> Replace { get; set; } = [];
    public List<ProductUpdateStepPlan> Steps { get; set; } = [];
    public List<ProductUpdatePlanMessage> Blockers { get; set; } = [];
    public List<ProductUpdatePlanMessage> Warnings { get; set; } = [];
    public string GeneratedAt { get; set; } = "";
}

public sealed class ProductUpdatePathPlan
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public bool WillPreserve { get; set; }
    public bool WillReplace { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class ProductUpdateStepPlan
{
    public int Order { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ProductUpdatePlanMessage
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ProductLatestUpdateRequest
{
    public bool Confirm { get; set; }
    public string OperationId { get; set; } = "";
}

public sealed class ProductUninstallPlan
{
    public string ProductId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ProductPath { get; set; } = "";
    public bool KeepSettings { get; set; } = true;
    public bool RemoveGeneratedData { get; set; }
    public long TotalDeleteBytes { get; set; }
    public ProductUninstallPathPlan AppDirectory { get; set; } = new();
    public ProductUninstallSettingsPlan Settings { get; set; } = new();
    public List<ProductUninstallPathPlan> GeneratedData { get; set; } = [];
    public ProductUninstallRuntimeDataPlan RuntimeData { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
}

public sealed class ProductUninstallPathPlan
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public long SizeBytes { get; set; }
    public bool WillDelete { get; set; }
}

public sealed class ProductUninstallSettingsPlan
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public long SizeBytes { get; set; }
    public bool WillBackup { get; set; }
    public string BackupPath { get; set; } = "";
    public bool WillDeleteBackup { get; set; }
}

public sealed class ProductUninstallRuntimeDataPlan
{
    public bool UsesDocker { get; set; }
    public bool ManagedByTimeline { get; set; }
    public bool Exists { get; set; }
    public long SizeBytes { get; set; }
    public bool WillDelete { get; set; }
    public List<ProductRuntimeResourcePlan> Resources { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class ProductRuntimeResourcePlan
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public long SizeBytes { get; set; }
    public bool WillDelete { get; set; }
    public bool ManagedByTimeline { get; set; }
    public string Message { get; set; } = "";
}
