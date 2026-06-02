# Session handoff — 2026-06-02 (v2.40.0 cycle + audit + docs + Android device probe)

Файл для восстановления после компоновки сессии. Полное «что сделано / что осталось».

## Текущее состояние (на момент записи)

- **Stable: v2.38.2** (Latest, 16 assets вкл. signed Android APK). НЕ менялся эту сессию.
- **In-flight prerelease: v2.40.0-r4** (prerelease, 14 assets, весь CI зелёный). Соакается.
  Объединяет v2.39 (diagnostics-export + adversarial-review) + v2.40 (FC interaction gates).
- **HEAD main: `fd46297`** (docs actualization). Оба remote (github + origin/Forgejo) синхронны.
- **AppVersion.cs: `2.40.0-r4`**. На main после r4 — ещё page-subs fix (cd196d2) + docs (fd46297),
  БЕЗ нового -rN (накапливаем; r4 соакается).
- Цепочка коммитов: `5bca879` v2.40 gates → `cdffb0c` merge → `e44c543` r1 → `3e7bd8f` r2 →
  `c26d089` r3 → `85f9a8f` r4 → `cd196d2` page-subs → `fd46297` docs.

## ЧТО СДЕЛАНО ЭТУ СЕССИЮ

### 1. v2.40.0-r1..r4 отшиплено (все green, MCP-verified, оба remote, без --no-verify)
- **r1 (e44c543)**: объединил never-cut v2.39 + v2.40 FC-гейты + 15 фиксов из adversarial-review
  (`plans/review-since-v2.39-findings-2026-06-02.md`). Headline: H1 custom-JSON DNS-leak
  (fail-CLOSED dns.final + EnsureSynthesizedRemoteDns), M1-M3 diagnostics redaction
  (obfs_password/plugin_opts, URL userinfo, Authorization/Bearer log-token), M4 cancel-no-false-failure,
  M5 scrub-both-routing-lists. v2.40 FC gates: Verified-only Connect/Apply + busy-guard + clamps.
- **r2 (3e7bd8f)**: regression-review follow-up (`plans/regression-review-v2.40.0-r1-followup-2026-06-02.md`).
  Поймал РЕГРЕССИЮ от моего же M5-фикса: ScrubRoutingForApp over-removal → новый survivor-guard
  `RoutingAppListEditor.IsStillRoutedByAnother`. + per-app DNS leak (InjectDnsRules synth для include-split).
- **r3 (c26d089)**: P0 handle-leak sweep (`plans/handle-leak-sweep-v2.40.0-r3-2026-06-02.md`).
  Новый `ProcessQuery` (AnyAlive/CountAlive dispose Process[] в finally); RuntimeStatusDetector делегирует.
  7 утечек починены (6 inline-.Length + 1 Discord). Новый `.githooks/pre-commit` Gate 7. +11 ProcessQueryTests.
- **r4 (85f9a8f)**: Export diagnostics в кебаб «…» (был только в Settings→Updates→Support, недостижим в Simple).

### 2. Audit Этап-1 (из bug-responsiveness-memory-audit-targets) — закрыт
- GetProcessesByName handle-leak sweep (= r3 выше). DoD-чекбокс отмечен.
- page-subscription teardown (cd196d2): ServersPage/SubscribePage отписывают ActiveServerChanged
  на DataContext rebind (_subscribedVm). На main, БЕЗ -rN bump (накапливаем).

### 3. Два adversarial /workflows
- Regression-review v2.39+v2.40 diff → нашёл M5-регрессию (исправлена в r2).
- r3 handle-leak diff review → 0 confirmed regressions (refactor behavior-preserving).

### 4. Stats-doc разобран → 5 задач (#161-165), ничего срочного
- `vpn-connection-user-statistics-product-notes-2026-06-02.md`. Honesty-wording уже корректен в коде
  (Android «Last check Ns ago», не «internet works»). Это feature-программа Phase 1-4.

### 5. Docs actualization (fd46297) — survey+apply workflow по всем CLAUDE.md
- 13 docs surveyed, 44 правки (43 авто + 1 руками). Только устаревшие ФАКТЫ; golden rules не тронуты.
- Committed: 10 tracked CLAUDE.md. Updated in-place (НЕ в git): `.claude_handoff.md`,
  `~/.claude/CLAUDE.md`, `C:/Project/CLAUDE.md` (stale old-arch дубликаты → current multi-platform).

### 6. Android device probe (read-only, через Mac SSH) — КЛЮЧЕВАЯ НАХОДКА
- Mac: `ssh slovn@192.168.0.246`, adb на `/opt/homebrew/bin/adb` (НЕ на PATH non-login shell).
- Device: **KYOCERA A101BM (BALMUDA Phone), Android 12 / SDK 31**, serial 54499112209, authorized.
  Установлен **VPNRouter v2.38.2** (stable).
- **НАХОДКА: battery-opt НЕ exempt** (`dumpsys deviceidle whitelist` пусто для пакета). App Standby
  bucket 10 (ACTIVE сейчас). RUN_ANY_IN_BACKGROUND allow. VPN сейчас НЕ подключён (FGS не запущен).
- ВЫВОД: no-doze foundations в коде ЕСТЬ (START_STICKY + FGS systemExempted + wakelock + capability
  запросить exemption), но на реальном девайсе exemption НЕ выдан — потому что запрос спрятан в
  Settings→Reliability кнопке (`OnReliabilityBatteryClicked` в AndroidApp.Permissions.cs). Это и есть
  главный реальный no-doze gap на Android 12.

## ЧТО ОСТАЛОСЬ СДЕЛАТЬ

### Ждёт user-команды
- **Cut v2.40.0 stable** — после soak r4. Verification gate зелёная (14 assets, CI green, MCP PASS,
  live-update gate надо прогнать перед cut). Только по явной команде «cut/ok/promote» (rule #6).

### Android (девайс подключён через Mac SSH — готов к работе)
- **FIX #1 (highest-value, evidence-based): proactive battery-opt prompt.** Promote запрос exemption
  из спрятанной Settings→Reliability кнопки в dismissible banner на главном экране (или на первый Connect)
  когда `IsIgnoringBatteryOptimizations == false`. Reuse существующей `OnReliabilityBatteryClicked` логики.
- **FIX #2: onTaskRemoved hardening** в VpnRouterService.java — defensive re-arm для aggressive-OEM
  swipe-away (Android 12 background-FGS-start restrictions; exemption + onTaskRemoved вместе = robust).
- **Device-verify plan (ИМПАКТНО — физический телефон user'а, НУЖНО ЕГО OK):**
  install APK → prompt появляется → (user tap grant) → re-check `dumpsys deviceidle whitelist` exempt.
  Doze: connect VPN → `adb shell dumpsys deviceidle force-idle` → FGS+tunnel выживают?
  ВАЖНО: connect VPN маршрутизирует реальный трафик телефона + force-idle меняет power state → спросить user.
- Android build pipeline: build в VM (`dotnet build VPNRouter.Android ... /p:EnableAndroidTarget=true
  /p:RunAOTCompilation=false`, libbox.aar в VPNRouter.Android/Lib/) → scp на Mac → adb install.
- Windows-parity фичи (цель user'а «сближение с windows») — отдельно, по списку фич.

### Audit Этап 2-5 (measurement-first, частично нужен девайс)
- Android Servers O(N²) rebuild (`Children.Clear()`+rebuild), Public Configs UI-queue/peak/GC,
  SaveSettings storm. Нужен реальный профайл/soak (Android-часть — девайс).
- HttpClient #3 (FreeConfigPoolFetcher/Fetcher/GeoIp instance HttpClient без IDisposable) — LOW-impact,
  fix зависит от lifecycle (reuse-static vs dispose); нужен ownership-trace перед фиксом, отдельным brief'ом.
- AppIconCache native bitmap teardown — measurement-gated (док: «если profiler подтвердит»).
- SingBoxManager ProcessExit lambda (B1) — sensitive (TUN-lock, открытый `singbox-lifecycle-hardening-v2.36`),
  low-impact (singleton), отдельным фокусным brief'ом, не мимоходом в soak.

### STATS feature (5 задач #161-165) — `vpn-connection-user-statistics-product-notes`
- Phase 1: honest connection card (desktop uptime parity + unified states + route-policy + direct-exceptions).
- Phase 2a: typed Clash API availability (limitation #1 — zero-snapshot-on-error == idle сейчас неразличимы).
- Phase 2b: live counters panel + session identity. Phase 3: diagnostics expansion. Phase 4: on-demand external-IP/HTTPS probe.

### Депы (dependabot PRs на GitHub)
- 4 зелёных (low-risk): #3 GH Actions, #4 nuget minor-patch, #6 Serilog+Console, #7 Serilog+File →
  смержить отдельным батчем с локальным build+test verify.
- 2 красных HOLD: #9 YamlDotNet 15→18 (major, ломает config parsing), #8 его анализатор — нужна миграция/пин.

### Прочее
- 2 untracked audit-дока прошлой сессии: `plans/applications-page-audit-2026-06-01.md`,
  `plans/public-configs-pipeline-audit-and-hardening-plan-2026-06-02.md` — решить, коммитить ли.
- Backlog tasks #131 (P0 fail-closed firewall Linux/macOS), #132 (sign desktop artifacts),
  #135 (Mac/Linux smoke matrix), #139 (rename Mac* services), #140 (Android CI NU1102).

## Ключевые факты для restore
- Mac SSH: `slovn@192.168.0.246` (key id_ed25519, через host AmneziaWG route). adb: `/opt/homebrew/bin/adb`.
  Device: A101BM (BALMUDA), Android 12 / SDK 31, serial 54499112209.
- Push policy: `git push github HEAD:main && git push origin HEAD:main`. Pre-push hook проверяет HEAD^1 CI;
  если merge-коммит не на GitHub → false-block (решали detached-HEAD push родителя first).
- VpnRouterTestMcp.dll lock даёт MSB3021 в pre-commit Gate 1 — harmless (build product OK, только tooling DLL заперт MCP-сервером).
- Stable cut НЕ autonomous (rule #6). Default autonomous до stable. No emoji в code/config/docs (rule #9).
- Финансовый constraint user'а: НЕ предлагать платные опции.
