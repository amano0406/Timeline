using Microsoft.AspNetCore.Components;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Shared;

public partial class ProductManagementModal
{
    [Parameter]
    public bool Embedded { get; set; }

    private ProductRuntimeOverview? _overview;
    private bool _loading = true;
    private string? _error;
    private string? _message;
    private bool _messageIsSuccess;
    private string? _actionProductId;
    private string? _pendingUninstallProductId;
    private bool _uninstallKeepSettings = true;
    private bool _uninstallRemoveGeneratedData;
    private bool _uninstallDangerAccepted;
    private bool _uninstallPlanLoading;
    private string? _uninstallPlanError;
    private ProductUninstallPlan? _uninstallPlan;
    private ProductActionCompletion? _completion;
    private readonly Dictionary<string, bool> _installRestoreSettings = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ProductRuntimeRow> Products => _overview?.Products ?? [];
    private ProductRuntimeRow? PendingUninstallProduct =>
        string.IsNullOrWhiteSpace(_pendingUninstallProductId)
            ? null
            : Products.FirstOrDefault(product => string.Equals(product.Id, _pendingUninstallProductId, StringComparison.OrdinalIgnoreCase));

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    public bool IsLoading => _loading;

    public Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            _overview = await Timeline.GetProductRuntimeOverviewAsync();
            if (!string.IsNullOrWhiteSpace(_overview.Message))
            {
                _error = _overview.Message;
            }
        }
        catch (Exception ex)
        {
            _error = RuntimeDisplayText.ProductStatusLoadFailure(ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

}
