using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineSettingsRoutes
{
    [SupplyParameterFromQuery(Name = "product")]
    public string? Product { get; set; }

    protected override void OnInitialized()
    {
        Navigation.LocationChanged += HandleLocationChanged;
    }

    private string RoutePath
    {
        get
        {
            var relativePath = Navigation.ToBaseRelativePath(Navigation.Uri);
            var queryIndex = relativePath.IndexOfAny(['?', '#']);
            return queryIndex >= 0 ? relativePath[..queryIndex] : relativePath;
        }
    }

    private bool IsProductManagementRoute =>
        RoutePath.Equals("timeline/products", StringComparison.OrdinalIgnoreCase);

    private string? ProductId =>
        IsProductManagementRoute ? null : ProductFromRoute();

    private string? ProductFromRoute()
    {
        if (!string.IsNullOrWhiteSpace(Product))
        {
            return Product;
        }

        return RoutePath.ToLowerInvariant() switch
        {
            "audio/settings" => "audio",
            "windows-codex/settings" => "windows-codex",
            "chatgpt/settings" => "chatgpt",
            "image/settings" => "image",
            "video/settings" => "video",
            "pc/settings" => "pc",
            _ => null
        };
    }

    private void HandleLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Navigation.LocationChanged -= HandleLocationChanged;
    }
}
