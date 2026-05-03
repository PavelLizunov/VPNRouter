# Iter#3 audit findings — full-app pages computer-use review

**Date**: 2026-05-04
**Trigger**: user feedback after r6 — «Не слишком ли сложна страница Zapret? Другие страницы также проверяй. Почему ты пишешь про "Free Configs / Public" как о двух разных страницах?»
**Methodology**: live `mcp__vpnrouter-test__mouse_click` + screenshot on dev binary v2.31.6-r6, every tab + every section.

## 1. Free Configs vs Public — clarification

These are **the same page**, just different naming layers:

| Layer | Name |
|---|---|
| File / class | `FreeConfigsPage.axaml`, `FreeConfigsPageViewModel` |
| Localised tab label (RU) | «Публичные» |
| Localised tab label (EN) | "Public" |

Source: `Strings.cs:523` —
`public static string TabFreeConfigs => Ru ? "Публичные" : "Public";`

Earlier session reports wrote them with a slash separator («Free Configs /
Public») as if they were two pages. They're not — that was a naming-layer
confusion in the report wording. **No code change needed**, just a note for
future status reports to use one name consistently (preferring the
user-visible "Public" / «Публичные»).

## 2. Per-page audit summary

Each page audited via real `mcp__vpnrouter-test__mouse_click` then
screenshot. Findings table:

| Page | Layout | Sub-tabs | Sections / Items | Footer | Verdict |
|---|---|---|---|---|---|
| Servers | List | Servers, Custom Config (JSON) | 7 server rows | Test all + Deep verify + URL form + Add Server(s) | Dense but justifiable — power-user surface |
| Subscribe | List | (none) | 7 server rows + Subscriptions section | Test all + Deep verify + Subscriptions + Add form | Same row-list as Servers — overlap deliberate (one ConfigMode active at a time) |
| Settings | Master-detail | (none) | 6: Routing, Rules, Leak Protection, Content, Updates, Autostart | "✓ Auto-saved" + "Apply" | Production-standard reference. Dense but each section is single-purpose. |
| Applications | Master-detail | (none) | 10 categories (Discord, Messengers, AI tools, Browsers, Work, Streaming, Gaming, Virtualization, Privacy, Custom) | Category form footer | Clean. Sidebar items show counts. |
| Tools → Zapret | Master-detail | Zapret, Telegram proxy | 5: Status, Strategy, Hosts, Filters, Advanced | "● Status" + "Start DPI Bypass" (r6) | 5 sections is moderate — see §3 |
| Tools → Telegram proxy | Single pane | Zapret, Telegram proxy | (no master-detail) | "● Status" + "Start & open Telegram" (r6) | Clean after r5/r6 |
| Public | Master-detail-ish | Search, Saved | Big "Find working configs" CTA + Settings expander + Configs list | "Connect" disabled until row selected | Empty-state focused; Saved sub-tab for later |

Cross-page consistency after r6:

- Master-detail layout: 140 px sidebar + scroll detail — ✅ matches across Settings / Applications / Tools.
- Sidebar selected-item style: blue `AccentBgSubtleBrush` — ✅ matches Settings (default Avalonia) and Tools (after r6 dropped its override).
- Footer pattern: `Padding="14,7,14,8"` + `SurfaceSunkenBrush` bg + `0,1,0,0` divider, status indicator left + compact button right — ✅ matches Settings Apply bar and Tools Start bar (r6).
- Footer button colour:
  - Settings `Apply` — primary blue (it's a save action when there are pending changes).
  - Tools / Zapret `Start DPI Bypass` — primary blue (it's the page's purpose).
  - Tools / TgProxy `Start & open Telegram` — secondary white (per v2.25.6 design intent — don't compete with global Start VPN).
  - This variation is correct UX — primary colour follows the page's primary action surface, not a global "always blue" rule.

## 3. Zapret complexity verdict — moderate, addressed in r7

5 sections is moderate for a DPI bypass tool. Removing sections drops
power-user features. Keeping 5 but adding a 1-line description under each
section header makes the page less intimidating without restructuring:

- **Status**: existing `LblDpiDescription` already serves this role.
- **Strategy** (r7): "DPI bypass technique. If one doesn't work — try another."
- **Hosts** (r7): "Hosts-file overrides: Discord voice + Flowseal list."
- **Filters** (r7): "Which traffic to route through DPI bypass."
- **Advanced** (r7): "Diagnostics and service controls. **Not needed for most users.**"

The Advanced description in particular signals that normal users can skip
the section — addresses the core «not too complex?» concern without
restructuring. The 5-section layout (matching design handoff cell 7) is
preserved for power users who need Hosts/Filters/Advanced.

Verified live in r7: each section now shows its description as TextSecondary
italic under the bold header. Discoverable without visual clutter.

## 4. brat & nini logs — diagnostic

### nini

Logs `2026-05-03` + `2026-05-04`: clean. One subscribe-mode reconnect cycle
each day. VPN comes up, HealthMonitor reports «VPN is up», no crashes.
Profile loaded from default install path. No issues.

### brat

Logs `2026-05-03`: **multiple sing-box crashes followed by recovery gaps**:

| Crash time | Exit code | Next log entry |
|---|---|---|
| 02:56:56 | 1073807364 (STATUS_CONTROL_C_EXIT) | 12:01:39 — **9-hour gap, manual user restart** |
| 12:42:27 | 1073807364 | 13:17:56 — **35-min gap, manual restart** |
| 13:28:33 | 1073807364 | (need to verify) |
| 19:30:48 | -1 | (need to verify) |
| 19:38:56 | -1 | (need to verify) |

After each crash brat's HealthMonitor logs:

```
[ERR] sing-box crashed (exit code: 1073807364)
[INF] Firewall ENABLED N block rules (VPN down — leak protection active)
[WRN] Restarting sing-box (attempt 1/5) in 5000ms
```

…then NOTHING until the user manually relaunches VPNRouter. The
`Restarting in 5000ms` Task.Delay continuation must be lost across power
events (sleep / lock / hibernate) — sing-box dies on console event,
laptop sleeps, app suspended; on wake the periodic timer should rescue
but doesn't.

### Why the v2.31.5 fix may not always rescue

v2.31.5 added `_shouldBeRunning` flag + a new OnHealthTick branch:

```csharp
else if (!isHealthy && _shouldBeRunning && !_isStopping)
{
    _logger.Warning("[HealthMonitor] sing-box dead while user wants VPN up — initiating recovery (intended-running path)");
    AttemptRestart();
}
```

This relies on the periodic OnHealthTick continuing to fire after the
crash. brat's HealthMonitor is configured `check every 30s, max 5 restarts
(strict mode: false)` — so even with the v2.31.5 fix, recovery should
fire at most 30s after a crash. But brat's gaps are 35 min and 9 hours,
suggesting the periodic tick stopped firing entirely — likely because:

1. Windows put the app process into modern standby (S0 low-power) where
   non-real-time timers are throttled or stopped.
2. App was hibernated and `_shouldBeRunning` doesn't survive hibernate
   resume cleanly (timer drift).
3. The Task.Delay(5000) was the only safety net in the path that crashed,
   and once it failed, no further code path tries again.

### Deferred fix candidates (NOT this iteration)

1. **Windows session-state listener** — subscribe to `WTSRegisterSessionNotification`
   for SESSION_LOCK/UNLOCK + `RegisterPowerSettingNotification` for
   sleep/wake events. On wake, force a HealthMonitor probe + recovery if
   `_shouldBeRunning && !sing-box-alive`.

2. **Persistent watchdog** — separate small process that monitors
   VPNRouter.App health, restarts it on crash. Like a Windows Service
   (which we already have but not always installed).

3. **Resume-from-suspend hook** — Avalonia's app lifecycle has
   `OnFrameworkInitializationCompleted`; we could add a hook for
   `Windows.System.Power` events to trigger explicit recovery.

This is non-trivial work and out of scope for the iter#3 r7 polish iteration.
**Document as known-issue carried forward**.

## 5. What's NOT changing in r7

- Zapret 5-section structure (per design handoff cell 7, justified by audit
  data: power-user features deserve their own surfaces).
- Footer styles (already aligned to NetworkPage Apply bar pattern in r6).
- Sidebar selection style (already aligned in r6).
- Subscribe / Servers list overlap (one ConfigMode active at a time —
  intentional, see CLAUDE.md / VPNConfig docs).
- Public / FreeConfigs naming (file names stay internal as `FreeConfigs*`,
  user-visible label stays "Public" / "Публичные").

## 6. r7 ship checklist

- [x] Add 4 new strings (`ZapretSec*Desc`) to `Strings.cs`.
- [x] Add 4 new L_ getters to `MainWindowViewModel.Localization.cs`.
- [x] Insert 4 description TextBlocks in `DpiBypassPage.axaml` (Strategy /
  Hosts / Filters / Advanced).
- [x] Bump `AppVersion` to `2.31.6-r7`.
- [x] `dotnet build -c Release` → 0 errors.
- [x] Regression tests → 20/20 passed.
- [x] Live computer-use verification of all 4 descriptions rendering correctly.
- [ ] Commit + push both remotes.
- [ ] `build.ps1 -Version "2.31.6-r7" -Upload`.
- [ ] Mark prerelease + apply notes.
- [ ] Restore v2.31.5 as Latest.
- [ ] Delete previous `v2.31.6-r6` prerelease.
- [ ] Wait for Mac + Linux + APT CI.
- [ ] Verify 12 assets.

## 7. Future-iteration backlog (deferred)

Carried forward beyond r7:

1. **HealthMonitor recovery on power events** — see §4.
2. **Subscribe vs Servers tab UX** — they show the same 7-row list when
   both modes have data. Could add a clearer visual cue showing which
   mode is active. Low priority.
3. **Dial-down sidebar font weight in Zapret** — `FontWeight=SemiBold` on
   sidebar items makes the sidebar look heavier than necessary. Could
   try `Regular` for non-active items + `SemiBold` only for active.
   Cosmetic, defer.
4. **Audit Public page Search / Saved sub-tabs** — only the empty Saved
   state was screenshotted in this audit. Real usage with cached configs
   should be re-audited in a follow-up.
