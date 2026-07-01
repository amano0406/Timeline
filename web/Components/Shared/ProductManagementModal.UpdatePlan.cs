using Timeline.Web.Services;

namespace Timeline.Web.Components.Shared;

public partial class ProductManagementModal
{
    private ProductUpdatePlan? UpdatePlanFor(ProductRuntimeRow product) =>
        _updatePlans.TryGetValue(product.Id, out var plan) ? plan : null;

    private string UpdatePlanError(ProductRuntimeRow product) =>
        _updatePlanErrors.TryGetValue(product.Id, out var error) ? error : string.Empty;

    private bool IsUpdatePlanLoading(ProductRuntimeRow product) =>
        string.Equals(_updatePlanLoadingProductId, product.Id, StringComparison.OrdinalIgnoreCase);

    private bool CanRequestUpdatePlan(ProductRuntimeRow product) =>
        IsInstalled(product)
        && product.SupportedOnCurrentOperatingSystem
        && _actionProductId is null
        && !IsBusy(product)
        && !IsUpdatePlanLoading(product);

    private bool CanApplyLatestProductUpdate(ProductRuntimeRow product, ProductUpdatePlan plan) =>
        CanRequestUpdatePlan(product)
        && plan.CanUseBuiltArtifactUpdater
        && plan.Blockers.Count == 0;

    private static string UpdatePlanStateLabel(ProductUpdatePlan plan) =>
        plan.State.Trim().ToLowerInvariant() switch
        {
            "built_artifact_ready" => "更新できます",
            "built_artifact_required" => "配布成果物待ち",
            "blocked" => "確認が必要",
            "up_to_date" => "最新です",
            _ => string.IsNullOrWhiteSpace(plan.State) ? "未確認" : plan.State,
        };

    private static string UpdatePlanPillClass(ProductUpdatePlan plan) =>
        plan.State.Trim().ToLowerInvariant() switch
        {
            "built_artifact_ready" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "built_artifact_required" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
            "blocked" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            "up_to_date" => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
            _ => "tfa-status-pill border-line bg-slate-50 text-slate-700",
        };

    private static string BuiltArtifactStatusText(ProductUpdatePlan plan)
    {
        if (plan.BuiltArtifactStatus.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(plan.BuiltArtifactName)
                ? "ビルド済み成果物あり"
                : plan.BuiltArtifactName;
        }

        return string.IsNullOrWhiteSpace(plan.BuiltArtifactMessage)
            ? "ビルド済み成果物はまだ見つかっていません"
            : plan.BuiltArtifactMessage;
    }
}
