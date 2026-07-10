# VPNRouter v2.46.0-r4 subscription review

Date: 2026-07-05
Target: windows-brat, 192.168.0.106, Win10 LTSC
Scope: Subscription screen, especially many-config behavior.

## Test limits

- Tested only on windows-brat.
- Live GUI pixels were not available through WinRM: scheduled interactive screenshot captured a black frame, and `VPNRouter.App.exe` had no usable `MainWindowHandle` from WinRM.
- Visual review below is therefore based on XAML/source. Functional observations are from CLI and `C:\ProgramData\VPNRouter\logs\vpnrouter20260705.log`.
- End state from my run: brat was cleaned. `C:\r4review` was deleted, VPNRouter/sing-box processes were stopped, VPNRouter firewall rules were removed, DNS hardening registry keys were removed, and WAN was back to `83.97.108.34`.

## Findings

### P1 - Stop/status mismatch after recovery

After `start --profile AI_Tools`, `status` showed Running, but later `stop` returned `VPN Router is not running` while `sing-box.exe` and an old `VPNRouter.CLI.exe` process were still present. I cleaned them manually.

Relevant log evidence:

- `vpnrouter20260705.log:1793` - sing-box `FATAL` TUN create/open failure.
- `vpnrouter20260705.log:1847` - TUN-orphan crash signature detected.
- `vpnrouter20260705.log:1902` - HealthMonitor restarted sing-box successfully.
- Repeated pattern later at `2010`, `2064`, `2112`.

Why it matters: user-visible state can say stopped while process state is not clean. That is worse than cosmetic UI trouble.

### P1 - Subscription start works, but recovery UX is scary

Subscription refresh worked:

- `vpnrouter20260705.log:1728` - fetched 19 servers.
- `vpnrouter20260705.log:1729` - subscriptions refreshed: 19 servers.
- `vpnrouter20260705.log:1730` - aggregated 19 servers, active Germany.
- `vpnrouter20260705.log:1789` - TUN ready after 1122 ms.

But roughly 14 seconds later, sing-box hit a TUN-orphan fatal and recovery kicked in. The app recovered, but a user watching logs/status would reasonably think the VPN broke.

Small UX fix: show a clear recovery state such as `Repairing VPN adapter...` / `Reconnecting...` instead of surfacing only fatal-looking churn.

### P2 - CLI emits happy-path logs like errors under WinRM

`profiles list` succeeded, but `[SettingsLoader] Loaded...` was emitted in a way WinRM wrapped as `NativeCommandError`. The table still rendered.

Small fix: keep routine info logs out of stderr/console unless `--verbose` is enabled.

### P2 - Main subscription list is structurally good for many configs

`VPNRouter.App/Views/Pages/SubscribePage.axaml:127` uses a `ListBox` for `SubscriptionServers`.

`VPNRouter.App/Views/Pages/SubscribePage.axaml:140` uses `VirtualizingStackPanel`.

This is the right base for 19+ configs. No need for a bigger architecture here.

### P2 - Bottom subscriptions list will not scale well

`VPNRouter.App/Views/Pages/SubscribePage.axaml:277` uses a `ScrollViewer` with `MaxHeight="130"`.

`VPNRouter.App/Views/Pages/SubscribePage.axaml:282` uses `ItemsControl`, not a virtualized list.

With several subscriptions, this becomes a small scrolling slot under a dominant server table. A simpler UX would be a compact summary row:

`1 subscription · 19 configs · refreshed 18:46 · active Germany`

Then expand details only when needed.

### P2 - The many-config table needs scanning tools, not more rows

The server grid is fixed as `14,*,100,42,40,24` at `VPNRouter.App/Views/Pages/SubscribePage.axaml:110` and repeated in row template at `:147`.

For 19+ configs, the missing affordance is not space; it is prioritization:

- sort by ping/status;
- filter failed/slow only;
- group or badge by protocol/country;
- show current active and best candidate in a sticky summary.

### P3 - Auto-select toggle is buried

`AutoSelectBestServer` is at `VPNRouter.App/Views/Pages/SubscribePage.axaml:254`.

It is functionally important, but visually sits between test actions and subscription management. For a user with many configs, this is core behavior. It should read closer to the server list, e.g. `Auto-pick fastest on next connect`.

### P3 - Icon glyphs are fragile

Refresh/delete/test buttons use literal glyph content:

- `VPNRouter.App/Views/Pages/SubscribePage.axaml:175`
- `VPNRouter.App/Views/Pages/SubscribePage.axaml:338`
- `VPNRouter.App/Views/Pages/SubscribePage.axaml:345`

In PowerShell output these showed as mojibake. The app may render them fine, but this is still brittle for tooling/localization. Prefer a stable icon source or shared icon style.

### P3 - Add-subscription form is cramped

The add form uses `ColumnDefinitions="100,*,Auto"` at `VPNRouter.App/Views/Pages/SubscribePage.axaml:365`.

For Russian labels or provider names, 100 px for name is tight. Minimal fix: use `MinWidth=120` or `140`, keep URL as the flexible column.

## RDP follow-up needed

Need RDP screenshots to judge:

- real spacing/overflow on the Subscription tab after refresh to 19 configs;
- hover row state;
- disabled/loading states during refresh;
- light/dark theme contrast;
- Russian/English clipping.

Important correction: my run cleaned brat at the end. If RDP review is next, r4 must be redeployed to `C:\r4review` first.

## RDP screenshot pass

Inputs: `C:\Project\VPNRouter\rdp-shots\01-servers.png` through `14-overflow-menu.png`, captured 2026-07-05 by Claude Code.

### P1 - Advanced top tab strip clips the last tab at 520 px

Visible in:

- `rdp-shots\01-servers.png`
- `rdp-shots\02-subscription.png`
- `rdp-shots\03-settings.png`

The `Публичные` tab is cut at the right edge. This is especially visible because it is a primary navigation item, not secondary content.

Likely source:

- `VPNRouter.App/Views/MainWindow.axaml:746` - tab strip is inside a horizontal `ScrollViewer` with hidden scrollbar.
- `VPNRouter.App/Views/MainWindow.axaml:762` through `:773` - tabs use fixed padding/font and all live in one horizontal row.

Small fix: move `Публичные` into the overflow menu or reduce tab text to icons/short labels at the 520 px width. Hidden horizontal scroll is technically functional, but discoverability is poor.

### P1 - Subscription bottom row truncates real provider metadata

Visible in `rdp-shots\02-subscription.png`.

The subscription row shows:

`https://ninitux... · 19s · 2026...`

The URL and refresh date are both truncated, while the row also has refresh/delete/arrow controls squeezed at the right. The user cannot answer the basic question "which subscription is this, and when was it refreshed?" without tooltip or guessing.

Related source:

- `VPNRouter.App/Views/Pages/SubscribePage.axaml:277` - bottom subscription list is constrained to `MaxHeight=130`.
- `VPNRouter.App/Views/Pages/SubscribePage.axaml:286` - row layout is `24,*,Auto,Auto,Auto`.
- `VPNRouter.App/Views/Pages/SubscribePage.axaml:308` - metadata is a single `URL · Ns · time` line.

Small fix: make the visible line human-first: `simple · 19 configs · refreshed today 18:46`; keep the raw URL in tooltip/details.

### P2 - Advanced mode is dense; Simple mode is much calmer

Compare:

- `rdp-shots\02-subscription.png`
- `rdp-shots\13-simple-mode.png`

Simple mode has better hierarchy: state card, config card, route choice, one primary CTA. Advanced mode exposes tabs, server table, subscription table, add form, autosave, status, and start button all at once. That is acceptable for a power-user screen, but it needs stronger grouping.

Small fix: in Advanced Subscription, collapse the add-subscription form behind a `+ Add subscription` row/button until the user asks for it. It is currently always consuming vertical space at `VPNRouter.App/Views/Pages/SubscribePage.axaml:362`.

### P2 - Disabled Apply button looks like a dead primary action

Visible across settings shots, e.g.:

- `rdp-shots\07-settings-routing.png`
- `rdp-shots\09-settings-leak-protection.png`
- `rdp-shots\10-settings-content.png`

The footer says `Auto-сохранение`, but a disabled `Применить` button still sits in the bottom-right. This creates a mixed model: "auto-saved, but maybe I need to apply?" On screenshots it reads as stale/dead UI.

Small fix: hide `Применить` when there are no pending changes, or replace with a passive `Saved` state. Keep the button only when there is something to apply.

### P2 - Apps tab has a strong warning, but the main area is empty

Visible in `rdp-shots\04-apps.png`.

The yellow full-tunnel warning is useful. But after it, the whole central pane says only `← Выберите категорию`, and the category input at bottom-left is disabled-looking. For an empty/non-selected state this is too sparse.

Small fix: show a one-line explanation in the main pane: `Choose a category to route apps through VPN. Full tunnel is currently active, so app selection is ignored.` This matches the warning and tells the user why the screen is blank.

### P2 - Overflow menu shot is not cropped to the app window

Visible in `rdp-shots\14-overflow-menu.png`.

The screenshot includes the Windows desktop and `Activate Windows`. Fine for internal review, not okay for public release notes/docs. If these screenshots are reused, crop to the app window.

### P3 - Public configs page has good empty-state copy, but the CTA language conflicts

Visible in `rdp-shots\06-public.png`.

The green panel says it will "download public VLESS configs and check each with a real connection attempt"; the button says `Найти рабочие конфиги`. Later empty state says `Нажмите кнопку выше, чтобы найти конфиги`. This is understandable, but there are three different verbs: download/check/find.

Small fix: use one verb family: `Найти рабочие конфиги` for the button, and `Проверим публичные конфиги реальным подключением` as supporting copy.

### P3 - Settings pages are visually consistent

Positive note from the screenshots: routing, leak protection, content, updates, and autostart all share the same left-nav/card/footer system. The visual language is now coherent enough; the remaining issues are mostly information density and state clarity, not styling churn.

### Still not covered by screenshots

- Hover states.
- Actual loading/progress states.
- Connected VPN state.
- Dark theme.
- English localization.
- Add dialogs.
