# Timeline Settings Unification TODO

Status: settings modal and normal/pro mode integration implemented. Remaining open items depend on future sub-product CLI contracts.

- [x] Create this implementation checklist.
- [x] Remove the old parent product directory / registry parent-directory user-facing concept.
- [x] Replace separate basic settings and product management screens with one settings hub.
- [x] Remove the old standalone product-management page from the navigation and smoke list.
- [x] Remove product start/restart controls from the dashboard.
- [x] Product install/update/uninstall is intentionally deferred. Do not show it as a normal settings task until the operation design is settled.
- [x] Render existing sub-product settings inside the settings hub with a product selector.
- [x] Remove standalone sub-product settings routes from the Blazor router.
- [x] Place the Timeline settings save button before sub-product settings so the save scope is clear.
- [x] Support product-specific settings hub links with the `product` query parameter.
- [x] Replace separate Timeline work/store directory controls with one Timeline save-location control.
- [x] Route old settings/product-management URLs into the settings hub instead of showing stale screens.
- [x] Open Timeline settings as a modal from the sidebar and dashboard product actions.
- [x] Add normal/pro mode switching with a confirmation before entering pro mode.
- [x] Add common AI compute mode storage under Timeline settings.
- [x] Resolve the normal-mode AI compute mode to a concrete product mode when saving supported sub-product settings.
- [x] Keep pro-mode exit simple. Do not show auto-management return as a separate normal-user action.
- [x] Add a save confirmation step that lists changed settings before writing them.
- [x] Confirm before closing the settings modal when unsaved changes exist.
- [x] Add a product-management modal from the sidebar utility area.
- [x] Add product start/stop/restart controls through existing product launchers.
- [x] Expand common Hugging Face token propagation beyond TimelineForAudio when additional AI products expose a compatible CLI contract.
- [ ] Expand model-usage-condition display beyond TimelineForAudio when additional products expose model inventory through CLI.
- [x] Build and run the PowerShell ASCII check.

Notes:

- Sub-product setting controls are reachable inside the settings modal. Direct settings URLs were removed from the Blazor router so the normal user path stays modal-based.
- Product settings buttons open the settings modal with the selected product id instead of navigating to a settings URL.
- Timeline settings now expose a single save location. Internally, Timeline expands it into `work`, `store`, and log locations under that directory.
- Old URLs such as `/timeline/products` and `/audio/settings` are no longer normal UI routes.
- Normal mode hides product installation paths. Product placement is only exposed in pro mode.
- Changing the Timeline save location in normal mode also moves managed Audio/Image generated-data destinations under the new Timeline save location before saving.
- Product package operations are intentionally tracked as a separate future feature, not as remaining work in this settings-unification pass.
- Product management currently shows product existence, placement, runtime state, and start/stop/restart controls. Install/update/uninstall remains deferred and is not shown as a disabled normal-user action.
- Screenshot: `output/playwright/product-management-modal-check.png`
- Screenshot: `output/playwright/verify-product-management-no-install-buttons-8s.png`
- The common AI compute mode is Timeline-level state. Current supported propagation is TimelineForAudio and TimelineForVideo.
- Common Hugging Face token registration now propagates to TimelineForAudio and TimelineForVideo when each product is installed. TimelineForImage, TimelineForWindowsCodex, TimelineForChatGPT, and TimelineForPC do not currently require the shared Hugging Face token path.
- 2026-05-10 CLI model-inventory check:
  - TimelineForAudio: `models list --json` works and remains the source for Hugging Face model usage-condition display.
  - TimelineForImage: `--json models list` works, but the reported models are local components (`pillow`, `tesseract:jpn+eng`) and do not require Hugging Face token guidance.
  - TimelineForVideo: README documents `models list`, but the current local `cli.ps1 models list` and `cli.ps1 models list --json` exit with code 1 and no usable model inventory output in this environment. Do not expand model-usage-condition UI to Video until the product-side CLI contract is confirmed.
  - TimelineForWindowsCodex, TimelineForChatGPT, and TimelineForPC currently do not expose a Hugging Face model-usage-condition contract that needs this UI.
