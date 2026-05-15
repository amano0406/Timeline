using Timeline.Web.Services;

namespace Timeline.Web.Components.Shared;

public partial class ProductManagementModal
{
    private static bool IsInstalled(ProductRuntimeRow product) =>
        product.ProductFound && product.ComposeFound;

    private bool CanStart(ProductRuntimeRow product) =>
        IsActionTarget(product)
        && product.StartFound
        && !IsBusy(product)
        && !IsRuntimeStarted(product);

    private bool CanStop(ProductRuntimeRow product) =>
        IsActionTarget(product)
        && product.StopFound
        && !IsBusy(product)
        && IsRuntimeStarted(product);

    private bool CanRestart(ProductRuntimeRow product) =>
        IsActionTarget(product)
        && product.StartFound
        && !IsBusy(product)
        && IsRuntimeStarted(product);

    private bool IsActionTarget(ProductRuntimeRow product) =>
        IsInstalled(product) && _actionProductId is null;

    private static bool CanModifyProductFiles(ProductRuntimeRow product) =>
        product.AppManagedByTimeline && !product.DestructiveActionsDisabled;

    private bool CanInstall(ProductRuntimeRow product) =>
        !IsInstalled(product)
        && product.AppManagedByTimeline
        && _actionProductId is null
        && !string.IsNullOrWhiteSpace(product.SourceUrl);

    private bool CanUpdate(ProductRuntimeRow product) =>
        IsInstalled(product)
        && CanModifyProductFiles(product)
        && product.UpdateAvailable
        && _actionProductId is null
        && !IsBusy(product)
        && !string.IsNullOrWhiteSpace(product.SourceUrl);

    private bool CanRequestUninstall(ProductRuntimeRow product) =>
        IsInstalled(product)
        && CanModifyProductFiles(product)
        && _actionProductId is null
        && !IsBusy(product);

    private bool CanConfirmUninstall(ProductRuntimeRow product) =>
        CanRequestUninstall(product)
        && !_uninstallPlanLoading
        && _uninstallPlan is not null
        && string.IsNullOrWhiteSpace(_uninstallPlanError);

    private bool IsUninstallPending(ProductRuntimeRow product) =>
        string.Equals(_pendingUninstallProductId, product.Id, StringComparison.OrdinalIgnoreCase);

    private bool IsBusy(ProductRuntimeRow product) =>
        string.Equals(_actionProductId, product.Id, StringComparison.OrdinalIgnoreCase) ||
        RuntimeState(product) is "starting" or "stopping" or "restarting" or "installing" or "updating" or "uninstalling";

    private static bool IsRuntimeStarted(ProductRuntimeRow product) =>
        product.Running || RuntimeState(product) is "running";

    private static string RuntimeState(ProductRuntimeRow product) =>
        product.State.Trim().ToLowerInvariant();

    private static bool ShowProductMessage(ProductRuntimeRow product) =>
        !IsInstalled(product) ||
        !product.AppManagedByTimeline ||
        product.DestructiveActionsDisabled ||
        (RuntimeState(product) is "failed" &&
            !string.IsNullOrWhiteSpace(product.Message));

    private static string ProductMessage(ProductRuntimeRow product)
    {
        if (!product.AppManagedByTimeline)
        {
            if (!product.ProductFound)
            {
                return "開発用の配置が見つかりません。Timeline からのインストールは無効です。";
            }
            return product.StartFound || product.StopFound
                ? "開発用の配置を参照しています。起動と停止はできますが、更新とアンインストールは無効です。"
                : "開発用の配置を参照しています。更新とアンインストールは無効です。";
        }
        if (!product.ProductFound)
        {
            return string.IsNullOrWhiteSpace(product.SourceUrl)
                ? "製品が見つかりません。導入元の設定が必要です。"
                : "製品が見つかりません。インストールできます。";
        }
        if (!product.ComposeFound)
        {
            return "起動に必要なファイルが見つかりません。";
        }
        if (!product.StartFound && !product.StopFound)
        {
            return "起動停止用のスクリプトが見つかりません。";
        }
        if (product.DestructiveActionsDisabled)
        {
            return "開発用の配置を参照しています。起動と停止はできますが、更新とアンインストールは無効です。";
        }
        return product.Message;
    }

    private static string RuntimeLabel(ProductRuntimeRow product)
    {
        if (!product.ProductFound)
        {
            return "未インストール";
        }
        if (!product.ComposeFound)
        {
            return "不完全";
        }
        return product.State.Trim().ToLowerInvariant() switch
        {
            "installing" => "インストール中",
            "updating" => "更新中",
            "starting" => "起動中",
            "running" => "稼働中",
            "stopping" => "停止中",
            "stopped" => "停止",
            "restarting" => "再起動中",
            "uninstalling" => "削除中",
            "failed" => "異常",
            "ready" => "未起動",
            _ => "導入済み",
        };
    }

    private static string RuntimePillClass(ProductRuntimeRow product)
    {
        if (!product.ProductFound || !product.ComposeFound)
        {
            return "tfa-status-pill border-red-200 bg-red-50 text-red-800";
        }
        return product.State.Trim().ToLowerInvariant() switch
        {
            "installing" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "updating" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "starting" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "running" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "stopping" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "stopped" => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
            "restarting" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "uninstalling" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            "ready" => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
            _ => "tfa-status-pill border-line bg-slate-50 text-slate-700",
        };
    }

    private static string RuntimeIcon(ProductRuntimeRow product)
    {
        if (!product.ProductFound || !product.ComposeFound)
        {
            return "triangle-exclamation";
        }
        return product.State.Trim().ToLowerInvariant() switch
        {
            "installing" => "spinner",
            "updating" => "spinner",
            "starting" => "spinner",
            "running" => "circle-check",
            "stopping" => "spinner",
            "stopped" => "circle-minus",
            "restarting" => "spinner",
            "uninstalling" => "spinner",
            "failed" => "triangle-exclamation",
            "ready" => "circle-minus",
            _ => "circle-info",
        };
    }

    private static string RuntimeDetailLabel(ProductRuntimeRow product)
    {
        if (!IsInstalled(product))
        {
            return "-";
        }
        if (product.DestructiveActionsDisabled)
        {
            return "開発用配置";
        }
        if (product.Running && DateTimeOffset.TryParse(product.StartedAt, out var startedAt))
        {
            return $"稼働 {DurationLabel(DateTimeOffset.Now - startedAt)}";
        }
        return product.State.Trim().ToLowerInvariant() switch
        {
            "ready" => "起動できます",
            "updating" => "更新しています",
            "stopped" => "停止しています",
            "failed" => "前回操作でエラー",
            "" => "状態未取得",
            _ => RuntimeLabel(product),
        };
    }

    private static string DurationLabel(TimeSpan value)
    {
        if (value.TotalMinutes < 1)
        {
            return "1分未満";
        }
        if (value.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)value.TotalMinutes)}分";
        }
        if (value.TotalDays < 1)
        {
            return $"{(int)value.TotalHours}時間{value.Minutes}分";
        }
        return $"{(int)value.TotalDays}日{value.Hours}時間";
    }

    private static string ProductIcon(string productId) => productId switch
    {
        "audio" => "file-audio",
        "windows-codex" => "terminal",
        "chatgpt" => "comments",
        "image" => "image",
        "video" => "video",
        "pc" => "desktop",
        _ => "box",
    };

    private static string DisplayName(ProductRuntimeRow product) =>
        string.IsNullOrWhiteSpace(product.DisplayName) ? product.Id : product.DisplayName;

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string VersionText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未確認" : value;

    private static string LatestVersionText(ProductRuntimeRow product)
    {
        if (!string.IsNullOrWhiteSpace(product.LatestVersion))
        {
            return product.LatestVersion;
        }
        if (!string.IsNullOrWhiteSpace(product.LatestVersionStatus) &&
            !product.LatestVersionStatus.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            return "確認できません";
        }
        return "未確認";
    }
}
