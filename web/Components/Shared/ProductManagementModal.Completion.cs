using Microsoft.AspNetCore.Components;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Shared;

public partial class ProductManagementModal
{
    private string ConfirmUninstallLabel =>
        _uninstallRemoveGeneratedData
            ? "データも削除してアンインストールする"
            : "アンインストールする";

    private string UninstallModeTitle
    {
        get
        {
            if (!_uninstallKeepSettings && _uninstallRemoveGeneratedData)
            {
                return "この製品をできるだけ削除";
            }

            if (_uninstallRemoveGeneratedData)
            {
                return "生成済みデータも削除";
            }

            if (!_uninstallKeepSettings)
            {
                return "設定を残さず削除";
            }

            return "アプリだけ削除";
        }
    }

    private string UninstallModeDescription
    {
        get
        {
            if (!_uninstallKeepSettings && _uninstallRemoveGeneratedData)
            {
                return "製品アプリ本体、生成済みデータ、設定を削除対象にします。元ファイルは削除しません。";
            }

            if (_uninstallRemoveGeneratedData)
            {
                return "製品アプリ本体に加えて、Timeline が作成した取り込み結果も削除します。元ファイルは削除しません。";
            }

            if (!_uninstallKeepSettings)
            {
                return "製品アプリ本体を削除し、設定は退避せず残しません。生成済みデータは残します。";
            }

            return "製品アプリ本体だけを削除し、設定は退避、生成済みデータは残します。再インストールしやすい選択です。";
        }
    }

    private string UninstallModePillClass =>
        UninstallNeedsStrongConfirmation
            ? "tfa-status-pill border-red-200 bg-red-50 text-red-800"
            : "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800";

    private string MessageAlertClass =>
        _messageIsSuccess ? "tfa-alert-info border-teal-200 bg-teal-50 text-teal-800" : "tfa-alert-info";

    private string MessageIcon =>
        _messageIsSuccess ? "circle-check" : "circle-info";

    private static string SettingsPlanLabel(ProductUninstallPlan plan)
    {
        if (!plan.Settings.Exists)
        {
            return "設定なし";
        }
        return plan.Settings.WillBackup ? "退避する" : "削除する";
    }

    private static string GeneratedDataPlanLabel(ProductUninstallPlan plan)
    {
        var size = plan.GeneratedData.Where(item => item.WillDelete).Sum(item => item.SizeBytes);
        return plan.RemoveGeneratedData ? $"削除する / {FormatBytes(size)}" : "残す";
    }

    private static string RuntimeDataPlanLabel(ProductUninstallPlan plan)
    {
        if (!plan.RuntimeData.UsesDocker)
        {
            return "対象外";
        }
        if (!plan.RuntimeData.ManagedByTimeline)
        {
            return "未確認";
        }
        var deleteCount = plan.RuntimeData.Resources.Count(item => item.WillDelete);
        var deleteBytes = plan.RuntimeData.Resources.Where(item => item.WillDelete).Sum(item => item.SizeBytes);
        if (deleteCount > 0)
        {
            return $"削除する / {deleteCount} 件 / {FormatBytes(deleteBytes)}";
        }
        if (plan.RuntimeData.Resources.Count > 0)
        {
            return $"残す / {plan.RuntimeData.Resources.Count} 件";
        }
        return plan.RuntimeData.WillDelete
            ? $"削除する / {FormatBytes(plan.RuntimeData.SizeBytes)}"
            : "残す";
    }

    private static string RuntimeResourceKindLabel(string kind) => kind switch
    {
        "docker-project" => "Docker構成",
        "docker-container" => "コンテナ",
        "docker-image" => "イメージ",
        "docker-volume" => "ボリューム",
        "docker-network" => "ネットワーク",
        "local-path" => "ローカル",
        _ => string.IsNullOrWhiteSpace(kind) ? "リソース" : kind,
    };

    private static string RuntimeResourceDisplay(ProductRuntimeResourcePlan resource)
    {
        if (!string.IsNullOrWhiteSpace(resource.Path))
        {
            return resource.Path;
        }
        if (!string.IsNullOrWhiteSpace(resource.Name))
        {
            return resource.Name;
        }
        return "-";
    }

    private static string RuntimeResourceSizeLabel(ProductRuntimeResourcePlan resource)
    {
        if (resource.SizeBytes > 0)
        {
            return FormatBytes(resource.SizeBytes);
        }
        return string.IsNullOrWhiteSpace(resource.Message) ? "容量未確認" : "未確認";
    }

    private static string RuntimeResourceStatusLabel(ProductRuntimeResourcePlan resource)
    {
        if (resource.WillDelete)
        {
            return "削除";
        }
        if (resource.Kind.StartsWith("docker-", StringComparison.OrdinalIgnoreCase))
        {
            return "削除未対応";
        }
        return "残す";
    }

    private static string RuntimeResourceStatusClass(ProductRuntimeResourcePlan resource)
    {
        if (resource.WillDelete)
        {
            return "tfa-status-pill border-red-200 bg-red-50 text-red-800";
        }
        if (resource.Kind.StartsWith("docker-", StringComparison.OrdinalIgnoreCase))
        {
            return "tfa-status-pill border-amber-200 bg-amber-50 text-amber-800";
        }
        return "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700";
    }

    private static string RuntimeResourceMessage(ProductRuntimeResourcePlan resource)
    {
        if (resource.WillDelete)
        {
            return "";
        }
        if (resource.Kind.StartsWith("docker-", StringComparison.OrdinalIgnoreCase))
        {
            return "実行環境側のデータです。現時点では容量を確認せず、削除もしません。";
        }
        return UninstallWarningText(resource.Message);
    }

    private static string UninstallWarningText(string? warning)
    {
        var text = (warning ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (text.Contains("outside Timeline-managed products", StringComparison.OrdinalIgnoreCase))
        {
            return "この製品はTimeline管理外の場所にあるため、アプリ本体の削除は無効です。";
        }

        if (text.Contains("no explicit management contract", StringComparison.OrdinalIgnoreCase))
        {
            return "この製品は実行環境データを使いますが、Timeline側に削除対象を特定する契約がないため、実行環境データは削除しません。";
        }

        if (text.Contains("Runtime data management is declared, but resource deletion is not implemented", StringComparison.OrdinalIgnoreCase))
        {
            return "実行環境データの管理対象であることは分かっていますが、削除処理はまだ実装されていないため、ここでは削除しません。";
        }

        if (text.Contains("Local paths can be removed; Docker resource deletion is not implemented", StringComparison.OrdinalIgnoreCase))
        {
            return "ローカルの実行環境データは削除できますが、Dockerリソースの削除は現時点では未対応です。";
        }

        if (text.Contains("No runtime resources were resolved", StringComparison.OrdinalIgnoreCase))
        {
            return "削除対象の実行環境データを特定できなかったため、実行環境データは削除しません。";
        }

        return RuntimeDisplayText.ProductRuntimeMessage(text);
    }

    private static ProductActionCompletion BuildUninstallCompletion(string displayName, ProductUninstallPlan plan)
    {
        var rows = new List<ProductCompletionRow>
        {
            new("削除したアプリ本体", $"{FormatBytes(plan.AppDirectory.SizeBytes)} / {plan.ProductPath}"),
            new("取り込んだデータ", GeneratedDataCompletionLabel(plan)),
            new("実行環境データ", RuntimeDataCompletionLabel(plan)),
            new("設定", SettingsCompletionLabel(plan)),
            new("空き容量の見込み", FormatBytes(plan.TotalDeleteBytes)),
        };

        return new ProductActionCompletion(
            $"{displayName} のアンインストールが完了しました",
            "削除したものと残したものを確認できます。",
            rows,
            plan.Warnings);
    }

    private static ProductActionCompletion BuildInstallCompletion(
        string displayName,
        bool settingsBackupAvailable,
        bool restoreSettings,
        string settingsBackupPath)
    {
        var rows = new List<ProductCompletionRow>
        {
            new("製品アプリ本体", "インストールしました。"),
            new("設定", settingsBackupAvailable
                ? (restoreSettings
                    ? $"退避済み設定を復元しました / {settingsBackupPath}"
                    : "退避済み設定は使わず、新しい設定で開始します。")
                : "新しい設定で開始します。"),
        };

        return new ProductActionCompletion(
            $"{displayName} のインストールが完了しました",
            "製品管理から起動できる状態になりました。",
            rows,
            []);
    }

    private static bool HasSettingsBackupForInstall(ProductRuntimeRow product) =>
        !IsInstalled(product) && product.SettingsBackupAvailable;

    private bool InstallRestoreSettings(ProductRuntimeRow product)
    {
        if (!_installRestoreSettings.TryGetValue(product.Id, out var value))
        {
            value = true;
            _installRestoreSettings[product.Id] = value;
        }
        return value;
    }

    private void SetInstallRestoreSettings(ProductRuntimeRow product, ChangeEventArgs args)
    {
        _installRestoreSettings[product.Id] = args.Value is bool value
            ? value
            : string.Equals(Convert.ToString(args.Value), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string SettingsCompletionLabel(ProductUninstallPlan plan)
    {
        if (!plan.Settings.Exists)
        {
            return "設定ファイルは見つかりませんでした。";
        }
        return plan.Settings.WillBackup
            ? $"退避しました / {plan.Settings.BackupPath}"
            : "退避せず削除しました。";
    }

    private static string GeneratedDataCompletionLabel(ProductUninstallPlan plan)
    {
        if (!plan.RemoveGeneratedData)
        {
            return "残しました。再インストール時に再利用できる可能性があります。";
        }

        var size = plan.GeneratedData.Where(item => item.WillDelete).Sum(item => item.SizeBytes);
        return $"削除しました / {FormatBytes(size)}";
    }

    private static string RuntimeDataCompletionLabel(ProductUninstallPlan plan)
    {
        if (!plan.RuntimeData.UsesDocker)
        {
            return "対象外です。";
        }
        if (!plan.RuntimeData.ManagedByTimeline)
        {
            return "未確認のため削除していません。";
        }
        var deleteCount = plan.RuntimeData.Resources.Count(item => item.WillDelete);
        var deleteBytes = plan.RuntimeData.Resources.Where(item => item.WillDelete).Sum(item => item.SizeBytes);
        if (deleteCount > 0)
        {
            var keepCount = plan.RuntimeData.Resources.Count - deleteCount;
            var keepLabel = keepCount > 0 ? $" / 残した明示リソース {keepCount} 件" : "";
            return $"削除しました / {deleteCount} 件 / {FormatBytes(deleteBytes)}{keepLabel}";
        }
        if (plan.RuntimeData.Resources.Count > 0)
        {
            return $"削除未対応または保持対象のため残しました。明示リソース {plan.RuntimeData.Resources.Count} 件";
        }
        return plan.RuntimeData.WillDelete
            ? $"削除しました / {FormatBytes(plan.RuntimeData.SizeBytes)}"
            : "残しました。";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value:0.##} {units[unit]}";
    }

    private void CloseCompletion()
    {
        _completion = null;
    }

    private sealed record ProductActionCompletion(
        string Title,
        string Message,
        IReadOnlyList<ProductCompletionRow> Rows,
        IReadOnlyList<string> Warnings);

    private sealed record ProductCompletionRow(string Label, string Value);
}
