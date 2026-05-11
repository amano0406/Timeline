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
    public string InstalledVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string LatestVersionStatus { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string ReleaseArchiveUrl { get; set; } = "";
    public bool SettingsBackupAvailable { get; set; }
    public string SettingsBackupPath { get; set; } = "";
    public string SettingsBackupAt { get; set; } = "";
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

public sealed class ProductInstallRequest
{
    public bool RestoreSettingsBackup { get; set; } = true;
}

public sealed class ProductUninstallRequest
{
    public bool KeepSettings { get; set; } = true;
    public bool RemoveGeneratedData { get; set; }
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
