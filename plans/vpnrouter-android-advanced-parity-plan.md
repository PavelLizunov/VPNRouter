# Android Advanced parity plan — match desktop v2.32.0

**Date**: 2026-05-10
**Trigger**: User confirmed Simple page now matches v2.32.0 desktop. **"Мы не переходили дальше страницы simple. У нас advanced отличается. Нужен план."**
**Reference**: desktop v2.32.0 stable (commit `7d9707b`), captured live from `bin/Release/net8.0/VPNRouter.App.exe` (post-revert build, 2026-05-10).
**Current Android state**: Advanced shell with tab strip from AND-ADV-SHELL + AND-ADV-MIGRATE chips (still in main HEAD). Tab labels + structure differ from desktop.

---

## Top-level structure

### Desktop Advanced (canonical)

```
┌────────────────────────────────────────────────────────────────────┐
│ [VPNRouter] [logo] Virtual Penguin Network        + Simple │ ⋮     │  ← header bar + brand row + toggle
│             • VPN  Zapret  TG                                       │
├────────────────────────────────────────────────────────────────────┤
│ Servers   Subscribe   Settings   Applications   Tools   Public     │  ← 6 main tabs
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│  [tab content — varies, see per-tab section]                       │
│                                                                    │
├────────────────────────────────────────────────────────────────────┤
│ [tab-specific footer row — Test all / inputs / +Add etc.]          │
│ • Not connected                            ▶ Start VPN             │  ← persistent footer
└────────────────────────────────────────────────────────────────────┘
```

Header & footer **stay across all tabs**. The brand row + "+ Simple" toggle + kebab live in the header. The footer row at very bottom always shows connection status (left) + Start VPN / Disconnect (right). Tabs may add their own action row above the persistent footer.

### Android Advanced (current — diverges)

```
┌────────────────────────────────────────────────────────────────────┐
│ Advanced settings                                            ×     │  ← title bar with × close (no Simple toggle)
├────────────────────────────────────────────────────────────────────┤
│ Servers  Subscriptions  Applications  Network  DPI  Telegram  Pub… │  ← 7 tabs (Tools split into DPI + Telegram)
├────────────────────────────────────────────────────────────────────┤
│  [tab content]                                                     │
└────────────────────────────────────────────────────────────────────┘
                                                              ← no footer
```

---

## Top-level divergences (apply to ALL tabs)

| # | Aspect | Desktop v2.32.0 | Android current |
|---|---|---|---|
| **T1** | Header brand row (mascot + title + chips) | Visible on Advanced | Replaced by "Advanced settings" title |
| **T2** | "+ Simple" toggle top-right | Yes (returns to Simple mode) | No (must use × close instead) |
| **T3** | Kebab ⋮ on Advanced | Same kebab as Simple | No (kebab only on Simple page) |
| **T4** | Tab labels | `Servers`, `Subscribe`, `Settings`, `Applications`, `Tools`, `Public` (6) | `Servers`, `Subscriptions`, `Applications`, `Network`, `DPI bypass`, `Telegram`, `Public configs` (7) — 3 renames + Tools split |
| **T5** | Persistent footer | Status text (left) + Start VPN button (right), always visible | Absent |
| **T6** | Tab-specific action row above footer | Yes (per-tab — Test all, +Add, etc.) | Absent |

**Closure for T1-T6**: rebuild Android Advanced shell with header + tab strip + per-tab action row + persistent footer matching desktop chrome. Single chip — `AND-ADV-SHELL-CHROME`.

---

## Per-tab inventory — desktop canonical layouts

### 1. Servers tab

**Desktop**:
```
┌─────────────────────────────────────┐
│ Servers │ Custom Config (JSON)      │  ← sub-tabs
├─────────────────────────────────────┤
│ Server          IP        Ping  Port │  ← table headers
│  ───── (empty list) ─────            │
│                                      │
├─────────────────────────────────────┤
│  VLESS+Reality routes TCP only…      │  ← help text
│ [Test all] [Deep verify] vless://…   │  ← action row
│                  [Remove] [Add Server(s)] │
├─────────────────────────────────────┤
│ • Not connected      ▶ Start VPN     │  ← persistent footer
└─────────────────────────────────────┘
```

**Android current**:
- Has tab strip but Servers tab content is similar table. Missing: Custom Config (JSON) sub-tab + footer action row (Test all / Deep verify / vless input / Remove / Add Server(s)).

**Divergences**: S1 missing **Custom Config (JSON) sub-tab**. S2 missing **footer action row**. S3 column header strip exists ✓.

### 2. Subscribe tab

**Desktop**:
```
┌─────────────────────────────────────┐
│ Server          IP        Ping  Port │  ← server table from active subs
│  No servers / Add a subscription below │
├─────────────────────────────────────┤
│ [Test all] [Deep verify]    [Refresh all] │  ← action row
│                                      │
│ Subscriptions                        │
│ [Name input] [Subscription URL input] [+ Add] │
├─────────────────────────────────────┤
│ • Not connected      ▶ Start VPN     │
└─────────────────────────────────────┘
```

**Divergences**: SU1 different overall layout — desktop shows server-list-from-subs at top + add-row at bottom; Android shows subscription-card list. SU2 missing **footer action row** (Test all / Deep verify / Refresh all). SU3 add-row separate Name + URL inputs (vs single URL on Android).

### 3. Settings tab — **NESTED side-nav** (biggest divergence)

**Desktop**:
```
┌──────────────┬─────────────────────────────┐
│ Routing      │ Routing                      │  ← sub-section header
│ Rules        │ Determines which traffic…   │
│ Leak Prot.   │                              │
│ Content      │ ◉ Split Tunnel               │
│ Updates      │ ○ Full Tunnel                │
│ Autostart    │ ☑ Russian traffic via real IP│
│              │                              │
└──────────────┴─────────────────────────────┘
                                              ▲
                                              ↑ ✓ Auto-saved   [Apply] (right)
                                              • Not connected   ▶ Start VPN
```

**Android current**: flat scrollable list of all settings. No left side-nav.

**Divergences**: ST1 **missing left side-nav** with 6 sub-sections (Routing / Rules / Leak Protection / Content / Updates / Autostart). ST2 missing **"✓ Auto-saved"** badge + **Apply** button in footer. ST3 each sub-section has specific layout (radio-cards on Routing, checkboxes on Leak Protection, etc.) that needs per-section porting.

### 4. Applications tab — **NESTED category sidebar**

**Desktop**:
```
┌──────────────┬─────────────────────────────┐
│ Discord    1 │                              │
│ Messengers 3 │     ← Select a category      │  ← empty pane until category chosen
│ AI tools   3 │                              │
│ Browsers  23 │                              │
│ Work       6 │                              │
│ Streaming  2 │                              │
│ Gaming     3 │                              │
│ Virtual.   9 │                              │
│ Privacy    5 │                              │
│ Custom       │                              │
│              │                              │
│ [Category    │                              │
│  name input] │                              │
│ [+ New cat.] │                              │
└──────────────┴─────────────────────────────┘
                                       • Not connected   ▶ Start VPN
```

**Android current**: flat list of installed apps with checkboxes (similar to per-app picker on Simple page).

**Divergences**: A1 **missing left category sidebar** with 10 built-in categories + per-category counts. A2 missing **+ New category** input + button (custom user categories). A3 missing right-pane category-detail view (when category selected, shows apps in that category). A4 Android currently shows ALL apps as one flat list — desktop's pattern is browse-by-category.

### 5. Tools tab — **DOUBLE nesting** (sub-tabs + side-nav)

**Desktop**:
```
┌───────────────────┬──────────────┐
│ Zapret │ Telegram proxy           │  ← top sub-tabs
├──────────────────────────────────┤
│ Status      │ Status               │  ← side-nav (Zapret) + content
│ Strategy    │ Bypass ISP blocking… │
│ Hosts       │ • Stopped            │
│ Filters     │ ⚠ Windows only…      │
│ Advanced    │ [Run diagnostics]    │
│             │                      │
└─────────────┴──────────────────────┘
                                     ▲
                                     ↑ • Stopped   [Start DPI Bypass]
                                     • Not connected   ▶ Start VPN
```

**Android current**: Tools split into two top-level tabs (DPI bypass + Telegram). No sub-tabs in either. No left side-nav inside Zapret.

**Divergences**: TL1 **Tools should be ONE tab** (Tools), not split. TL2 **Zapret/Telegram inside Tools as sub-tabs**, not top-level. TL3 **inside Zapret, side-nav with Status/Strategy/Hosts/Filters/Advanced** (5 sub-sections). TL4 footer has secondary "Start DPI Bypass" button + bottom status row (Stopped/Running). TL5 Telegram proxy has its own layout (likely description + status + ports + start/stop).

**Platform-impossible items** (per user rule, skip on Android):
- Zapret winws.exe execution — not ported (Android uses native sing-box `tls_fragment` outbound). Status/Strategy/Hosts/Filters/Advanced sub-sections may simplify on Android since the engine is different.
- Telegram proxy daemon — not ported on Android.

So Android Tools tab:
- Sub-tabs Zapret / Telegram proxy ✓ (we already have this — they were split into separate tabs, just need to merge into one Tools parent + use sub-tabs)
- Inside Zapret: full Status/Strategy/Hosts/Filters/Advanced doesn't apply (no winws.exe). Show explainer + fall through to mode picker (off / standard / aggressive — already in `AndroidDpiBypassInjector`).
- Inside Telegram proxy: explainer banner only.

### 6. Public tab

**Desktop**:
```
┌─────────────────────────────────────┐
│ ▶ Search │ ★ Saved                    │  ← sub-tabs
├─────────────────────────────────────┤
│ Downloads public VLESS configs…     │  ← description (green banner)
│                                      │
│ [✓✓ Find working configs]            │  ← big green CTA
│                                      │
│ ▾ Settings (collapsible)             │
├─────────────────────────────────────┤
│ Configs                    0 shown   │
│  ───── (empty list) ─────            │
│  Click the button above…             │
├─────────────────────────────────────┤
│ Cache is empty — click 'Refresh'    │
│ Select a row and click Connect…      │
│ [ Connect ]                          │  ← per-tab Connect (selected config)
├─────────────────────────────────────┤
│ • Not connected      ▶ Start VPN     │
└─────────────────────────────────────┘
```

**Divergences**: P1 **missing Search/Saved sub-tabs**. P2 missing **collapsible Settings dropdown** (filters: latency goal, region, etc.). P3 missing **per-tab Connect button** (selects highlighted config from list). P4 footer/help-text styling differs.

---

## Closure plan — phased chips

Plan delivers visual parity in **5 phases** (independent enough to chip per phase). Total effort estimate: **~16-22 hours** spread across 5 chips.

### Phase A — Shell chrome (T1-T6)
**Effort**: 3-4h
**Chip**: `AND-ADV-CHROME`

- Replace title bar "Advanced settings + ×" with header matching desktop (brand row + "+ Simple" link top-right + ⋮ kebab)
- Add persistent footer: status text (left) + Start VPN / Disconnect (right) — visible on every tab
- Tab labels: rename `Subscriptions → Subscribe`, `Network → Settings`, `Public configs → Public`. Merge `DPI bypass + Telegram → Tools` (one tab with sub-tabs inside).
- Tab count goes 7 → 6 (matching desktop).
- Add per-tab action row slot above the persistent footer (each tab fills it independently).

### Phase B — Servers + Subscribe tabs (S1-S3, SU1-SU3)
**Effort**: 3-4h
**Chip**: `AND-ADV-SERVERS-SUBSCRIBE`

- Servers tab: add Custom Config (JSON) sub-tab (mirrors desktop). Add footer action row: Test all / Deep verify / vless input / Remove / Add Server(s).
- Subscribe tab: rebuild as desktop layout — server list at top (from all enabled subs aggregated), action row middle (Test all / Deep verify / Refresh all), Subscriptions section bottom (Name + URL inputs + + Add).

### Phase C — Settings tab side-nav (ST1-ST3)
**Effort**: 4-5h
**Chip**: `AND-ADV-SETTINGS-NAV`

- Add left side-nav with 6 sub-sections: Routing / Rules / Leak Protection / Content / Updates / Autostart
- Each sub-section's content must mirror desktop XAML page (cards + radios + checkboxes per spec)
- Footer: ✓ Auto-saved badge + [Apply] button (when there are pending changes)

### Phase D — Applications category sidebar (A1-A4)
**Effort**: 3-4h
**Chip**: `AND-ADV-APPS-CATEGORIES`

- Add left category sidebar: Discord / Messengers / AI tools / Browsers / Work / Streaming / Gaming / Virtualization / Privacy / Custom + per-category counts
- Bottom-left input: Category name + + New category button (user-defined categories)
- Right-pane: "← Select a category" placeholder until category chosen, then show apps in that category with checkboxes

### Phase E — Tools sub-tabs + Public sub-tabs (TL1-TL5, P1-P4)
**Effort**: 3-5h
**Chip**: `AND-ADV-TOOLS-PUBLIC`

- Tools: merge DPI bypass + Telegram into single Tools tab with internal sub-tabs (Zapret / Telegram proxy). Inside Zapret on Android, show simplified content (engine is sing-box native — no winws.exe Status/Strategy/Hosts/Filters/Advanced sub-nav). Telegram proxy shows explainer banner only.
- Public: add Search / Saved sub-tabs. Add collapsible Settings filter dropdown. Add per-tab Connect button (selects highlighted config).

---

## Order of execution

Recommended: **A → B → C → D → E** (shell first, then per-tab in order of complexity).

After each phase: rebuild APK, install on phone, capture composite vs desktop, verify pixel-equivalence on the active tab.

**Phase A is foundation** — gives the chrome (header / footer / tab labels) that the other phases populate. **Phases B-E are independent** of each other once A lands; can be done in any order.

---

## Platform-impossible items (per user rule — show desktop's UI but with explainer)

Items that **show on desktop but not implementable on Android**:

| Tool | Desktop UI | Android approach |
|---|---|---|
| Zapret winws.exe | Tools > Zapret > Status/Strategy/Hosts/Filters/Advanced (5 sub-sections) | Show simplified content: explainer banner ("Android uses sing-box native tls_fragment instead") + mode picker (off / standard / aggressive). NOT remove the tab — keep the sub-tab structure but content differs. |
| Telegram proxy daemon | Tools > Telegram proxy (full controls) | Show explainer banner only ("Telegram proxy on Android: routed via VPN — no separate daemon needed"). |
| Hosts file editing (Discord voice fix) | Tools > Zapret > Hosts | n/a — Android sandbox no admin host edit. Hide sub-section. |
| Wintun adapter diagnostics | Settings > Updates / Autostart secondary panel | n/a — Android VpnService uses system TUN. Hide. |
| Windows Service install | Settings > Autostart sub-section | Replace with **Always-on VPN** OS setting deep-link + BootReceiver toggle. |
| ETW process monitor | n/a (advanced VPN routing only) | n/a |
| Firewall block-on-VPN-fail | Settings > Leak Protection | n/a — Android VpnService routing handles. Hide checkbox. |

Default rule: **mirror desktop UI**. Hide only when truly impossible.

---

## Validation per phase

After each phase, the chip must:
1. `dotnet build VPNRouter.sln -c Release` — 0 errors
2. `dotnet publish VPNRouter.Android` — APK produced
3. Manual install via Mac SSH adb
4. Capture composite for the affected tab(s)
5. Side-by-side image compare desktop ↔ Android — visual structure must match

---

## Notes / lessons applied

- **Don't touch desktop again.** v2.32.0 is canonical reference. Capture-only on desktop side; all writes go to Android files.
- **Live press-by-press verify** — static screenshot diff is insufficient. Phone press-by-press required.
- **Platform-difference rule.** When a feature is technologically impossible on Android, hide gracefully (or substitute with explainer + Android-specific control) — don't show broken/disabled UI.
- **Android Localization.cs is a wrapper** to `VPNRouter.Core.Localization.Strings` (post-F-01). New strings go to Core.

---

## Out of scope (future)

- Phase G — shared `VPNRouter.Avalonia.UI` project (extract VM layer): not part of visual port. Done after visual port ships.
- Phase H — single Strings.cs source-of-truth: technically already done via F-01 wrapper; minor cleanup later.
- Mac/Linux desktop: desktop visual is already same on all 3 OSes via Avalonia. No per-OS divergence.

---

**Status**: plan ready. Awaiting user direction on whether to spawn Phase A chip first or different order.
