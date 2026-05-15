using Microsoft.AspNetCore.Components;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Shared;

public partial class ProductManagementModal
{
    private async Task RequestUninstallAsync(ProductRuntimeRow product)
    {
        _pendingUninstallProductId = product.Id;
        _uninstallKeepSettings = true;
        _uninstallRemoveGeneratedData = false;
        _uninstallPlan = null;
        _uninstallPlanError = null;
        _completion = null;
        _message = null;
        _messageIsSuccess = false;
        _error = null;
        await LoadUninstallPlanAsync();
    }

    private void CancelUninstall()
    {
        _pendingUninstallProductId = null;
        _message = null;
        _messageIsSuccess = false;
        _uninstallPlan = null;
        _uninstallPlanError = null;
        _uninstallPlanLoading = false;
    }

    private async Task ConfirmUninstallAsync(ProductRuntimeRow product)
    {
        var request = CurrentUninstallRequest();
        var plan = _uninstallPlan;
        var displayName = DisplayName(product);

        _actionProductId = product.Id;
        _error = null;
        _message = $"{displayName} をアンインストールしています。";
        _messageIsSuccess = false;
        _completion = null;
        try
        {
            await Timeline.UninstallProductAsync(product.Id, request);
            _message = $"{displayName} をアンインストールしました。";
            _messageIsSuccess = true;
            if (plan is not null)
            {
                _completion = BuildUninstallCompletion(displayName, plan);
            }
            _overview = await Timeline.GetProductRuntimeOverviewAsync();
            _pendingUninstallProductId = null;
            _uninstallPlan = null;
            _uninstallPlanError = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _message = null;
            _messageIsSuccess = false;
        }
        finally
        {
            _actionProductId = null;
        }
    }

    private ProductUninstallRequest CurrentUninstallRequest() =>
        new()
        {
            KeepSettings = _uninstallKeepSettings,
            RemoveGeneratedData = _uninstallRemoveGeneratedData,
        };

    private async Task SetUninstallKeepSettingsAsync(ChangeEventArgs args)
    {
        _uninstallKeepSettings = args.Value is bool value ? value : string.Equals(Convert.ToString(args.Value), "true", StringComparison.OrdinalIgnoreCase);
        await LoadUninstallPlanAsync();
    }

    private async Task SetUninstallRemoveGeneratedDataAsync(bool value)
    {
        _uninstallRemoveGeneratedData = value;
        await LoadUninstallPlanAsync();
    }

    private async Task LoadUninstallPlanAsync()
    {
        if (string.IsNullOrWhiteSpace(_pendingUninstallProductId))
        {
            return;
        }

        _uninstallPlanLoading = true;
        _uninstallPlanError = null;
        try
        {
            _uninstallPlan = await Timeline.GetProductUninstallPlanAsync(_pendingUninstallProductId, CurrentUninstallRequest());
        }
        catch (Exception ex)
        {
            _uninstallPlan = null;
            _uninstallPlanError = ex.Message;
        }
        finally
        {
            _uninstallPlanLoading = false;
        }
    }
}
