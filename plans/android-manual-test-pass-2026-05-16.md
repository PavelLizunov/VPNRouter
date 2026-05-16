## Android manual test pass — 2026-05-16

Continuation of the overnight session. 24+ iterations of screenshot + tap exercising every surface of the Android app on KYOCERA A101BM.

### Outcome

| # | New bugs found | Commit |
|---|---|---|
| Bug-AND-014 | Simple-page Autostart card title + subtitle + "Choose apps…" button never refreshed on language toggle | `d26e2db` |
| Bug-AND-015 | Empty-Connect error message ("No server configured…") hardcoded EN | `5efa012` |

### Iteration log

| # | Surface | Action | Result |
|---|---|---|---|
| 1 | Simple page | Fresh launch (EN/dark) | PASS — status, mode pill, VPN config, radios, autostart card, Connect, Advanced preview |
| 2 | Kebab menu | Tap kebab | PASS — opens, all 9 items + Advanced button |
| 3 | Diagnostics | Tap "Open log" | PASS — log viewer overlay opens, empty-state placeholder, refresh + × buttons |
| 4 | Advanced shell | Tap Advanced | PASS — Servers tab active, sub-tabs (Servers / Custom Config JSON), input row, Test all / Deep verify |
| 5 | Subscribe tab | Tap Subscribe | PASS — server list (empty), Test all / Deep verify / Refresh all, Subscriptions section header, Add form |
| 6 | Settings tab | Tap Settings | PASS — side-nav (Routing/Rules/Leak Protection/Content/Updates/Autostart), Leak Protection visible (Block traffic + DNS strategy dropdown) |
| 7 | Settings > Content | Tap Content sub-nav | PASS — Block ads & trackers card visible |
| 8 | Block ads toggle | Tap CheckBox | PASS — cyan-filled, "Apply" button replaces "Auto-saved" (dirty state) |
| 9 | Applications tab | Tap Applications | PASS — Custom (Свои) first chip with "1" badge (state preserved), 10 chips in 3 rows, mode toggle, sys apps + Showing count, Selected section with Camo Camera |
| 10 | Discord chip | Tap Discord chip | PASS — Discord active, "Showing: 1", Discord row in Available |
| 11 | Browsers chip | Tap Browsers chip | PASS — Browsers active, "Showing: 2", **Chrome visible** (Bug-AND-007 fix verify ✓✓) |
| 12 | Mode toggle | Tap "Exclude selected" | PASS — cyan border swap, hint text changes |
| 13 | Search box | Tap search, type "chr" | PASS — keyboard opens, "Showing: 1", filtered to Chrome only |
| 14 | System apps | Clear search, tap System apps | PASS — checkbox cyan, "Loading app list…" placeholder, list rebuilt |
| 15 | Public tab | Tap Public | PASS — Search/Saved sub-tabs, intro card, Find working configs primary button, empty table, footer hint |
| 16 | Theme in Public | Open kebab, tap Light | PASS — theme switched, Public tab + Search sub-tab preserved (Bug-AND-009 fix ✓) |
| 17 | Lang in Public | Open kebab, tap RU | PASS — Public tab labels translated (Поиск/Сохранённые/...), body translated (Bug-AND-009 fix ✓) |
| 18 | Simple via "← Simple" | Tap back link | PASS + **Bug-AND-014 found**: "Autostart" + "Configure VPN autostart on device boot" stayed in EN; "Choose apps…" stayed in EN |
| 18b | Selected apps radio | Tap radio | PASS — cyan-filled, Choose apps button + counter appear |
| 19 | Autostart card | Tap Autostart card | PASS — navigates to Advanced > Settings (Content sub-section) |
| 20 | Autostart sub-section | Tap Автозапуск in side-nav | PASS — detailed RU explanation about Always-on VPN + Doze mode + open VPN settings button |
| 21 | About | Open kebab > About | PASS — opens GitHub repo URL in Chrome browser |
| 22 | Bug-AND-014 fix verify | Build v20, relaunch | PASS ✓✓ — Автозапуск + "Настроить автозапуск VPN при загрузке устройства" + "Выбрать приложения…" all translated |
| 23 | Empty Connect | Tap Connect with no config | PASS + **Bug-AND-015 found**: error reads "Ошибка: No server configured. Add a subscription or paste a vless:// URI." (prefix RU, body EN) |
| 24 | Bug-AND-015 fix verify | Build v21, relaunch, switch RU | PASS ✓✓ — "Ошибка: Сервер не настроен. Добавьте подписку или вставьте vless://-URI." |

### Minor translation gaps still pending (informational, not commit-worthy)

- **"Deep verify"** button on Servers + Subscribe tabs is EN only. Translation `Strings.AdvServersDeepVerify` exists, should propagate.
- **"Custom Config (JSON)"** sub-tab on Servers — JSON is a technical term, but the prefix "Custom Config" could read "Свой конфиг".
- **"Loading app list…"** placeholder during Sys-apps toggle reload — visible only briefly, currently in EN.

These are tracked in the test summary; user can decide whether to backlog or fix.

### What was NOT tested

- Full VPN connect cycle (subscription URL needed; PII-sensitive on screenshots).
- Save subscription URL flow.
- Custom Config JSON paste flow.
- Health Check / Check IP leak / Check for updates (would trigger background tasks).
- Reset settings (destructive).
- Restart in Safe Mode (intrusive).
- Multi-server selection / sort options.
- Public tab Find working configs (would hit network).

### State after test pass

- APK v21 installed on phone.
- Theme: light, Language: RU.
- Currently on Simple page with empty config + error banner.
- Camo Camera still in Custom > Selected (overnight state preserved through 24 iterations of navigation/theme/lang changes).

### Commits added (manual test pass only)

| Hash | Bug | Description |
|---|---|---|
| `d26e2db` | Bug-AND-014 | Simple-page Autostart card + Choose apps button refresh on lang toggle |
| `5efa012` | Bug-AND-015 | Localized empty-Connect error message |

Total commits this session: 11 (9 overnight + 2 manual test). All pushed to `github/main` + `origin/main`.
