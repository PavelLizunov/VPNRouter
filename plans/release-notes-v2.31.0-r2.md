# VPNRouter v2.31.0-r2 — A11y Pillar (20 UIA Name fixes)

Continues v2.31.0 cycle. r2 closes Pillar 2 — the systemic empty
`AutomationProperties.Name` leak across all CheckBox callsites in
the Avalonia UI.

## Fixes

| ID | Severity | What |
|---|---|---|
| **A11y-1..20** | A11Y | 20 CheckBox callsites across 5 Pages were leaking empty UIA Name to assistive tech (screen readers, automation harnesses, MCP+UIA test scripts). Pre-fix: a screen reader would announce "checkbox, checked" with no context. Post-fix: each CheckBox carries `AutomationProperties.Name="{Binding XLabel}"` bound to the same source as its visible label. |

### Coverage map

| Page | CheckBoxes fixed | Names bind to |
|---|---|---|
| `NetworkPage.axaml` | 15 | BypassRu, CustomRulesAboveToggles, custom rule Value (×2), StrictMode, ForceIpv4, FlushDns, StrictDns, BlockAds, ReceivePrereleases, ServiceMaster, Autostart Vpn/Zapret/TgProxy/Ui |
| `ApplicationsPage.axaml` | 2 | L_EnableWholeGroup, ProcessName |
| `DpiBypassPage.axaml` | 1 | L_AutoUpdateCheckLabel |
| `FreeConfigsPage.axaml` | 1 | L_FcDeepExcludeRu |
| `SubscribePage.axaml` | 1 | per-server Name |

## Pattern

```xml
<!-- before -->
<CheckBox IsChecked="{Binding StrictMode}" .../>
<TextBlock Text="{Binding StrictModeLabel}"/>

<!-- after -->
<CheckBox IsChecked="{Binding StrictMode}"
          AutomationProperties.Name="{Binding StrictModeLabel}" .../>
<TextBlock Text="{Binding StrictModeLabel}"/>
```

Same source binding — the screen reader and the visible label will
always agree even when the user switches RU↔EN at runtime.

## Why explicit-per-callsite (not helper)

Considered a `WrappedContentName` attached property that auto-pulls
text from a child `TextBlock`. Rejected: would need code-behind
AvaloniaProperty registration + Visual tree walk, hides intent. The
explicit attribute is greppable (`AutomationProperties.Name=` finds
all sites in one shot) and self-documenting.

## Verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors
- 25/25 regression tests pass (no new tests — pure XAML attribute
  addition, no logic to unit-test without a headless Avalonia harness)
- Grep audit: `<CheckBox` → 21 hits across `Views/Pages/*.axaml`,
  20 now carry `AutomationProperties.Name`. (1 hit is a docs comment
  in `NetworkPage.axaml:35` — `<CheckBox Theme=...>` example only.)

## Cycle progress

| Pillar | Status |
|---|---|
| 1. Core stability (7+1 items) | r1: 7/8 done (AU-9 deferred) |
| **2. A11y systemic (~20 items)** | r2: 20/20 done |
| 3. ViewModels (4 items) | r3 next |
| 4. UX closure (8 items) | r4 |

## Cross-refs

- `plans/vpnrouter-v2.31.0-roadmap.md` — full v2.31 plan
- `plans/release-notes-v2.31.0-r1.md` — Pillar 1
- `plans/vpnrouter-extended-audit-2026-05-02.md` — 47-finding audit
