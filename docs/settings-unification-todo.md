# Timeline Settings Unification TODO

Status: initial settings unification complete

- [x] Create this implementation checklist.
- [x] Remove the old parent product directory / registry parent-directory user-facing concept.
- [x] Replace separate basic settings and product management screens with one settings hub.
- [x] Remove the old standalone product-management page from the navigation and smoke list.
- [x] Remove product start/restart controls from the dashboard.
- [x] Keep product install/update/uninstall in the settings hub as a managed-product operation area, but disable it until the safe confirmation flow is implemented.
- [x] Render existing sub-product settings inside the settings hub with a product selector.
- [x] Remove standalone sub-product settings routes from the Blazor router.
- [x] Place the Timeline settings save button before sub-product settings so the save scope is clear.
- [x] Support product-specific settings hub links with the `product` query parameter.
- [x] Build and run the PowerShell ASCII check.

Notes:

- Sub-product setting controls are now reachable inside the settings hub. The standalone settings routes have been removed from the Blazor router.
- Product settings links use `/timeline/settings?product=<product-id>#product-specific-settings` so the correct product tab opens inside the settings hub.
- Product install/update/uninstall is visible as a prepared operation area, but the buttons are disabled. Actual package/release operations need a separate implementation because they can delete or overwrite local product directories.
- Product package operations are intentionally tracked as a separate future feature, not as remaining work in this settings-unification pass.
