using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineIndex
{
    private TimelineExportProductResult? StoreProduct(string productId) =>
        _overview?.Products.FirstOrDefault(product =>
            product.ProductId.Equals(productId, StringComparison.OrdinalIgnoreCase));

    private bool IsInstalledProduct(string productId) =>
        _runtime?.Products.Count > 0
            ? _runtime.Products.Any(product =>
                product.Id.Equals(productId, StringComparison.OrdinalIgnoreCase)
                && product.ProductFound
                && (product.ComposeFound || product.Id.Equals("pc", StringComparison.OrdinalIgnoreCase)))
            : StoreProduct(productId) is { } product
                && (product.Included || product.ItemCount > 0 || product.EventCount > 0);
}
