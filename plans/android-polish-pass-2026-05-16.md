## Android polish pass — 2026-05-16

Continuation of overnight + manual test pass. **Autonomous polish iteration** picking up backlog items from `android-code-review-2026-05-16.md` plus translation gaps surfaced by manual testing.

### Outcome

5 fixes shipped in commit `e947243`. Total this iteration: **9 new iterations (28-36)** on KYOCERA A101BM.

| ID | What | Commit |
|---|---|---|
| Bug-AND-016 | 3 unilingual strings in Servers tab (Deep verify / Custom Config JSON / Ping) | `e947243` |
| Bug-AND-017 | Settings > Updates prerelease toggle mixed RU/EN ("Получать prerelease обновления") | `e947243` |
| Bug-AND-018 | Settings > Updates version row truncation on 5" (Текущая версия / Проверить обновления too long) | `e947243` |
| Medium-3 | QR temp JPEG cleanup in `OnCreate` | `e947243` |
| Low-1 | `_diagnosticsTimer` dispose in `DetachLifecycleEvents` | `e947243` |

### Iteration log

| # | Surface | Verify | Result |
|---|---|---|---|
| 28 | Servers tab RU | Custom Config / Deep verify / Ping column translations | PASS — all RU: "Свой конфиг (JSON)", "Глубокая проверка", "Пинг" |
| 29 | Settings > Routing | RU translation + layout | PASS — Раздельный / Полный / Российский трафик через VPN-сервер cards all RU; minor truncation on bypass-ru chip title (known, low priority) |
| 30 | Settings > Rules | RU translation | PASS — placeholder text "Кастомные правила маршрутизации пока не подключены на Android. Используй вкладку «Приложения»." Cleanly localized + correctly directs user to Applications tab |
| 31 | Settings > Updates | RU translation | **BUG-AND-017 FOUND**: "Получать prerelease обновления (experimental канал)" — half RU half EN. Plus **Bug-AND-018**: "Текущая версия" + "Проверить обновления" overflow on 5" |
| 32 | RU prerelease toggle | Re-verify after fix | PASS — "Получать пре-релизы (экспериментальный канал)" fully RU |
| 33 | Version row first fix | TextWrapping=Wrap on label | FAIL — "Теку щая верс ия" broken into 4 ugly lines because column too narrow |
| 34 | Version row shorter label | "Версия" + TextTrimming.CharacterEllipsis | PARTIAL — "Вер..." still truncated (button still too wide) |
| 35 | Version row final fix | Shortened RU button "Проверить" | PASS in RU — "Версия 2.32" / "Проверить" both fit cleanly |
| 36 | Version row final fix EN | Shortened EN label "Version" too | PASS in EN — "Version / 2.32.2 / Check for updates" all fit |

### What's STILL pending (full audit)

#### From code review backlog (Phase 4)
- **High-1**: crash log scrub regex — IPs + Reality keys not stripped. Requires real crash to validate fix; backlogged.
- **High-2**: auto-update SHA verification — pipeline change to publish `.sha256` companion + fetch+verify in `AndroidUpdater.BeginInstall`.
- **Medium-1**: `_appPickerSelected` reassignment race — safe today, hazard if future async paths added.
- **Medium-2**: `_advAppsCustomCategories` mutate+iterate without snapshot — same.
- **Low-2**: Play Store policy review for `FOREGROUND_SERVICE_SYSTEM_EXEMPTED`.
- **Low-3**: broad `ConfigChanges` mask aggregates hidden bugs.
- **Info-2**: `libbox.aar` SBOM / signature pinning.

#### From manual test pass coverage gaps (not exercised)
- VPN connect end-to-end on v27 with live subscription (only verified on v15 overnight).
- Sub-section flows: Subscribe > Add/Edit/Delete subscription, Servers > Add/Remove server, Public > Find/Connect.
- Reset settings (destructive — never exercised).
- Restart in Safe Mode (intrusive — never exercised).
- QR code scan (camera permission flow).
- Crash log viewer (no real crash on device).
- Health Check / Check IP leak / Check for updates real fetches.
- Theme switch under VPN connected.
- Language switch under VPN connected.

#### Functionally unimplemented (intentional + parity)
- Tools tab (Zapret + TgProxy) — Windows-only, intentionally hidden.
- Native Cygwin-style DPI bypass — Android uses sing-box `tls_fragment` only.
- Subscription auto-refresh — intentionally manual on Android.
- Multi-profile slots — single active config (subscribe/manual/custom).
- Battery optimization auto-request — info-text only.
- Speed test bandwidth probe — only latency on Android.

### Commit log (this iteration)

| Hash | Description |
|---|---|
| `e947243` | Bug-AND-016/017/018 + Medium-3 + Low-1 polish bundle |

### Full overnight + manual + polish session commits

`6a32a34 → 5b096d5 → 9157c56 → 69acead → 0e38786 → 301b8b4 → c269c1d → bf5385c → d26e2db → 5efa012 → 38a4aee → e947243`

**12 commits**, **18 Android bugs** found + fixed (Bug-AND-006 through Bug-AND-018 + the 5 code review findings).

### Current state

- APK v27 installed on KYOCERA A101BM.
- Theme: light, Language: EN.
- App state: Advanced > Settings > Updates panel.
- Camo Camera still selected in Applications > Custom (persisted from overnight).
- No VPN active.

### Recommended next user-facing actions when user wakes

1. **Verify VPN connect end-to-end with live subscription** — only way to actually test Bug-AND-006 (debug log) + Bug-AND-012 (BlockAds) + BypassRussianTraffic in real traffic.
2. **Decide Play Store path** — `FOREGROUND_SERVICE_SYSTEM_EXEMPTED` permission policy check.
3. **Bump AppVersion + cut a stable** if everything passes — there are 12 fixes worth shipping.
