using Microsoft.AspNetCore.Components;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Shared;

public partial class ProductManagementModal
{
    private async Task RestartProductAsync(ProductRuntimeRow product)
    {
        _completion = null;
        await RunProductActionAsync(product, "再起動", () => Timeline.RestartProductAsync(product.Id));
    }

    private async Task StartProductAsync(ProductRuntimeRow product)
    {
        _completion = null;
        await RunProductActionAsync(product, "起動", () => Timeline.StartProductAsync(product.Id));
    }

    private async Task StopProductAsync(ProductRuntimeRow product)
    {
        _completion = null;
        await RunProductActionAsync(product, "停止", () => Timeline.StopProductAsync(product.Id));
    }

    private async Task InstallProductAsync(ProductRuntimeRow product)
    {
        var settingsBackupAvailable = product.SettingsBackupAvailable;
        var settingsBackupPath = product.SettingsBackupPath;
        var restoreSettings = !settingsBackupAvailable || InstallRestoreSettings(product);
        _completion = null;
        await RunProductActionAsync(product, "インストール", () => Timeline.InstallProductAsync(
            product.Id,
            new ProductInstallRequest { RestoreSettingsBackup = restoreSettings }));
        if (_messageIsSuccess)
        {
            _completion = BuildInstallCompletion(DisplayName(product), settingsBackupAvailable, restoreSettings, settingsBackupPath);
        }
    }

    private async Task UpdateProductAsync(ProductRuntimeRow product)
    {
        _completion = null;
        await RunProductActionAsync(product, "更新", () => Timeline.UpdateProductAsync(product.Id));
    }

    private async Task RunProductActionAsync(ProductRuntimeRow product, string label, Func<Task<ProductRuntimeRow>> action)
    {
        _actionProductId = product.Id;
        _error = null;
        _message = $"{DisplayName(product)} を{label}しています。";
        _messageIsSuccess = false;
        try
        {
            await action();
            _message = $"{DisplayName(product)} を{label}しました。";
            _messageIsSuccess = true;
            _overview = await Timeline.GetProductRuntimeOverviewAsync();
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
}
