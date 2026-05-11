# r9 — что можно делать без stas (2026-05-11)

Триггер: stas-specific investigation (`Bug-r9-F`) встал на `⏸ HOLD`. Все
остальные items r9-плана действенны прямо сейчас. Этот файл — короткий
ranked-actionable список + recommended order.

> **Desktop-first приоритет** (user 2026-05-11):
> - Bug-r9-F (stas Custom Config silent leak) — **desktop**
> - Bug-r9-I (Apps tab persist через reboot) — **desktop**
> - wgturn-core integration — **desktop first**, потом Android
>
> Android-only items (Bug-r9-A glyph, Bug-r9-B "контент пропадает"
> на Simple page) могут идти параллельно — они не блокируют desktop
> работу, но в очереди ниже.

## Категория 1 · User-reported bugs которые можно фиксить **сразу**

### A. Bug-r9-I — Apps tab settings не выживают reboot
- **Чёткий repro**: toggle Firefox → Exclude → reboot Windows → проверить.
- **Surfaces understood**: `ApplicationsPage.axaml`, `MainWindowViewModel.SaveSettings`,
  `SettingsLoader`, `VPNRouter.Service/` boot path.
- **Likely fix**: explicit Save bar в ApplicationsPage **или** auto-save
  через `PropertyChanged → SaveSettings()` (как desktop делает в Settings).
- **Effort**: 1-2 ч (1 chip).
- **Verify**: live test (toggle → close app → reopen → toggle persists),
  full E2E reboot test опциональный.
- **Severity**: **P1** — каждый reboot заново настраивать → высокая частота боли.

### B. Bug-r9-H — stale TUN cleanup перед стартом sing-box
- **Symptom**: после crash sing-box оставляет TUN adapter → следующий
  старт падает с "Cannot create a file when that file already exists."
- **Surfaces**: `VPNRouter.Core/Services/TunDiag.cs` (есть post-stop
  диагностика, но не pre-start cleanup), `VpnEngine.StartAsync`.
- **Fix**: pre-start sweep — `netsh interface show interface` найти
  `VPNRouter-TUN` / `sing-box-tun`, удалить через `netsh interface set
  interface ... admin=disabled` или `wintun-uninstall` (если используется
  Wintun). Логировать что подобрал и удалил.
- **Effort**: 2-3 ч (1 chip — требует Windows admin testing).
- **Severity**: P2 — пользователь видит cryptic FATAL после первого
  crash'а, не понимает почему второй start тоже падает.

### C. Bug-r9-E — TUN conflict detection (third-party VPN running)
- **Symptom**: stas пример — параллельно xraycore.exe (v2RayTun)
  держит TUN, sing-box падает.
- **Fix**: pre-flight check на сторонние VPN tool'ы (process scan
  через `Process.GetProcessesByName`): `xraycore.exe`, `wireguard.exe`,
  `openvpn.exe`, `hiddify.exe`, `amneziavpn.exe`, `qv2ray.exe`. Если
  найдено — UI alert: «Обнаружен другой VPN (хх). Остановите его
  перед запуском VPNRouter.»
- **Surfaces**: `VpnEngine.StartAsync` early-validation, новый
  `ConflictingVpnDetector.cs`.
- **Effort**: 2-3 ч (1 chip).
- **Severity**: P2 — частая UX pain.

### D. Bug-r9-G — Zapret winws.exe тихо exit (-1) UX
- **Symptom**: stas пример — `[WRN] Wrapper exited (exit code: -1)` без
  пояснения.
- **Fix**: после `winws.exe exit -1` показать toast/banner с подсказкой
  «winws.exe был остановлен — возможно его блокирует антивирус.
  Добавьте C:\\ProgramData\\VPNRouter\\zapret\\ в исключения AV.»
- **Surfaces**: `Zapret/ZapretManager.cs`, toast surface в MainWindow.
- **Effort**: 1 ч (1 chip).
- **Severity**: P3 — UX clarification, не баг.

### E. Bug-r9-A — значок остановки рендерится плохо
- **Need from user**: скриншот / в каком экране конкретно (Simple
  Connecting / Advanced footer / Stop VPN button).
- **Что можно сделать без скриншота**: аудит всех Stop/Disconnect/
  Cancel glyph constants в `Localization/Strings.cs` + проверка
  системного font fallback для каждого (`■`, `⏹`, `✕`, `⨯`).
  Можно proactively заменить редкие Unicode на проверенные (как мы
  делали `◂ → ←`).
- **Effort**: 1 ч аудит + повторный live screenshot pass.
- **Severity**: P3 (косметика).

### F. Bug-r9-B — содержимое страниц пропадает
- **Need from user**: точный repro flow (после чего? какая страница?
  Android или desktop?).
- **Что можно сделать без repro**: code audit `RebuildSimplePageView`
  (Bug #4 fix) на event-subscription leaks. Sequence:
  1. `OnFrameworkInitializationCompleted` подписывает
     `MainActivity.IntentChanged += OnIntentChanged` **раз**.
  2. `RebuildSimplePageView` создаёт новый MainView, поле `_statusCard`
     перепривязывается на новую инстансу.
  3. **Старая инстанса** `_statusCard` всё ещё ссылка из выполненной
     `OnIntentChanged(...)` callback'а если он сейчас в полёте → может
     UI-thread выкинуть NRE.
- **Effort**: 2-3 ч (audit + Defense-in-depth fix).
- **Severity**: P1 если действительно теряет UI state (пока не
  подтверждено).

### G. Bug-r9-C — Free configs логика ≠ PC
- **Need from user**: пример нерабочего конфига (URL / IP / port) для
  cross-platform compare.
- **Что можно сделать без примера**: side-by-side audit
  `VPNRouter.Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs`
  (PC) vs `VPNRouter.Android/AndroidFreeConfigDeepVerifier.cs` (Android,
  Bug #1 chip). Convergence check: одинаковые ли probe URL / timeout
  / success threshold / status mapping.
- **Effort**: 2 ч audit + сравнительный test suite.
- **Severity**: P2.

## Категория 2 · Defense-in-depth (preventive) — без stas

Это **proactive фиксы** для класса бага, который stas нашёл. Не требуют
его файлов для подтверждения — улучшают защиту в целом.

### H. Bug-r9-F-DEFENSIVE — Custom Config Mode silent placeholder leak
Три отдельных под-фикса (можно один chip):

1. **CustomConfigInjector tag-policy** (`VPNRouter.Core/Services/
   CustomConfigInjector.cs:248`): убрать silent fallback
   `tag = ob["tag"]?.ToString() ?? "proxy"`. Если у пользовательского
   custom JSON outbound не имеет `tag` — присваивать **`custom-proxy`**
   (не "proxy"), и логировать WARN: "outbound без tag — ставим
   custom-proxy". Так route rules через `vless-{name}` outbound'ы
   подписочных серверов будут НЕ перекрываться.

2. **LeakProtection check** (`VPNRouter.Core/Services/LeakProtection.cs`):
   walk через все outbounds → если у outbound `type=vless` но его
   `server` IP/hostname **не присутствует** в `vless.servers` ∪
   `subscriptions[*].servers` → **WARNING**: «Outbound `{tag}`
   указывает на `{ip}` который не из ваших подписок и не из VLESS
   серверов. Это может быть placeholder или leak от прошлой конфигурации.»

3. **Simple page outbound-display** (`VPNRouter.App/Views/Pages/SimplePage.axaml`
   + `VPNRouter.Android/AndroidApp.axaml.cs` Simple page builder):
   показывать **имя+IP** активного outbound (вместо просто `subscribe
   · split`). User должен видеть «через сервер X (1.2.3.4:443) ·
   subscribe · split». Так silent-leak становится видимым.

- **Effort**: 3-4 ч (1 chip).
- **Severity**: P0 для security (silent traffic-leak в неизвестный
  сервер — privacy violation).

## Категория 3 · wgturn-core integration groundwork (**desktop first**)

Re-scoped 2026-05-11 — user explicitly directed «внедрения нового ядра
тоже desktop first». Android port отложен до того как desktop работает.

Phase 1 (desktop binary) + Phase 2 (Core service skeleton) НЕ требуют
ответов от user'а на 4 mutex/server/UI/provisioning вопроса. Можно
начать прямо сейчас.

### I. Phase 1 — wgturn-cli.exe в desktop build
- Сборка `cmd/wgturn-cli` в `wgturn-cli.exe` через `go build`:
  ```bash
  cd /tmp/wgturn-core
  GOOS=windows GOARCH=amd64 go build -o wgturn-cli.exe ./cmd/wgturn-cli
  ```
- Mirror того как мы делаем с `sing-box.exe`: положить exe в
  `%ProgramData%\VPNRouter\bin\wgturn-cli.exe` через installer.
  Для VPNRouter `build.ps1`:
  ```powershell
  Copy-Item wgturn-cli.exe -Destination "$publishDir\bin\wgturn-cli.exe"
  ```
- Аналогично можно собрать `linux/amd64` + `darwin/arm64` для Mac/Linux
  builds через CI.
- **Effort**: 1-2 ч (go build + integrate в build.ps1 + проверить запуск
  `wgturn-cli.exe --version`).
- **Risk**: wgturn-core v0.0.1-alpha — может потребовать `make build`
  с дополнительными tags. Сначала verify locally что `go build` работает,
  потом интеграция в наш build pipeline.

### J. Phase 2 — Core service skeleton
- `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelEngine.cs`:
  spawn / lifecycle для `wgturn-cli.exe connect-url <url> --vk-link <link>`
  (mirrors `SingBoxManager.cs`).
- `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs`:
  Process.Start с правильным path / args, StdOut/Err to log file,
  Exited event handler.
- `VPNRouter.Core/Models/EmergencyChannelConfig.cs`: model для
  `wgturn://` URL + VK link.
- `VPNRouter.Core/Models/AppSettings.cs`: новая `EmergencyChannel`
  секция (enabled, wgturn_url, vk_link).
- Unit tests на парсинг URL + lifecycle state transitions.
- **Effort**: 3-4 ч.
- **Dependency**: Phase 1 wgturn-cli.exe собран и доступен.

### Phase 3 — Desktop UI (после Phase 2)
- `VPNRouter.App/Views/Pages/EmergencyChannelPage.axaml`: новая страница
  в MainWindow tabs (после Free / отдельной кнопкой).
- ViewModel: bind на `EmergencyChannelEngine` state, paste-or-scan input
  fields (`wgturn://` URL + VK link), Connect/Disconnect button.
- Bandwidth indicator с подсказкой "~200 KB/s — это нормальный режим
  fallback".
- **Open question**: mutex policy с основным VPN (sing-box) — выяснить
  позже.

### Android Phase 1+2 — отложено
- gomobile .aar + AndroidApp.EmergencyChannel.cs идут после того как
  desktop UI стабилизирован.

## Категория 4 · Verification sweeps по уже-shipped работе

После r8 многое было только spot-tested. Без stas можно сделать
полные sweeps для уже-deployed функций:

### K. TEST-RUN-ALL on r8 (полный sweep)
- TEST-1 (kebab 8 items) — закрыто частично в r2, retry post-r8.
- TEST-2 (Servers tab — Custom Config sub-tab, manual paste, Test all).
- TEST-4 (Settings sub-sections — Routing, Rules note, Leak Protection,
  Content, Updates, Autostart).
- TEST-5 (Applications — теперь redesigned per Bug #2, retest!).
- TEST-6 (Tools — Zapret modes, Telegram intent).
- TEST-7 (Public — Find / Saved / Connect — теперь с deep verify per
  Bug #1).
- TEST-8.11/8.12 (Reboot autostart + Always-on VPN).
- **Effort**: 4-6 ч (1 consolidated chip).
- **Output**: report `plans/test-results-android-r8-full-2026-05-11.md`
  + DEFCT-XX-XX entries reactively.

## Категория 5 · Infrastructure / cleanup

### L. Codify SSH-to-Mac live-test pipeline
- Что есть: ad-hoc bash chains `scp APK → ssh adb install → ssh adb
  input tap → screencap → scp back → Read PNG → uiautomator dump`.
- Цель: `tools/android-live-test.ps1` (Windows-host PowerShell) +
  `tools/android-live-test.sh` (Mac-host bash) с helper-функциями:
  `Install-LatestAPK`, `Tap-At { X Y }`, `Capture-Screen { Name }`,
  `Find-NodeByText { Text }` (uiautomator parse helper).
- **Effort**: 2 ч.
- **Benefit**: следующие test chips запускают наработанный pipeline
  одной командой.

### M. Cleanup stale chip worktrees
- `git worktree list` показывает 60+ worktrees от прошлых chip'ов.
- Многие уже мерджены или dismissed.
- `git worktree prune` + ручная зачистка merged branches.
- **Effort**: 30 мин.

## Рекомендованный порядок (desktop-first, 2026-05-11 update)

**Параллель A1 — desktop user wins** (P1 first, 1-2 дня):
1. **A** Bug-r9-I Apps tab persist через reboot (desktop, **P1**) — самая
   частая жалоба.
2. **H** Bug-r9-F-DEFENSIVE Custom Config silent leak (desktop, **P0
   privacy**) — proactive без stas.

**Параллель A2 — desktop sing-box UX** (P2, parallel with A1):
3. **B** Bug-r9-H stale TUN cleanup pre-start (desktop, P2).
4. **C** Bug-r9-E third-party VPN conflict detection (desktop, P2).
5. **D** Bug-r9-G Zapret winws.exe AV-block UX hint (desktop, P3).

**Параллель B — wgturn-core desktop integration** (long-running):
6. **I** Phase 1: wgturn-cli.exe в build.ps1 + installer.
7. **J** Phase 2: Core service skeleton (`EmergencyChannelEngine`).
8. Phase 3 (после J): Desktop UI tab в MainWindow.

**Параллель C — Android в очереди после desktop** (deprioritized):
9. **E** Bug-r9-A Android stop glyph audit (P3 cosmetic).
10. **F** Bug-r9-B Android "контент пропадает" audit (нужен repro).
11. **G** Bug-r9-C Free configs Android vs PC parity audit.
12. **K** TEST-RUN-ALL r8 Android full sweep.
13. Android wgturn-core (gomobile .aar) — после desktop UI работает.

**Параллель D — infrastructure** (fill-in):
14. **L** Codify SSH live-test pipeline (Mac→Android).
15. **M** Cleanup stale chip worktrees.

## Spawn-able chips сейчас (desktop-first)

1. **chip-A1-1**: Bug-r9-I — Apps tab Save bar + auto-persist (desktop).
2. **chip-A1-2**: Bug-r9-F-DEFENSIVE — 3-in-1 defense-in-depth (CustomConfigInjector
   tag policy + LeakProtection check + Simple page outbound display).
3. **chip-A2-1**: Bug-r9-H — stale TUN pre-start cleanup (desktop).
4. **chip-A2-2**: Bug-r9-E + Bug-r9-G — conflict detection + Zapret UX
   hint (объединить в один chip, общий код в startup pre-flight).
5. **chip-B-1**: wgturn-cli.exe desktop build integration (Phase 1).
6. **chip-B-2**: EmergencyChannelEngine + Manager skeleton (Phase 2) —
   запускать после chip-B-1.

Итого 5-6 параллельных chip'ов на desktop фронт. Android (chips
C-E + K) — после того как desktop стабилен.

## Что НУЖНО от user'а / других людей (blocked items)

⏸ **stas** — current.json + config.yaml для Bug-r9-F confirmation
⏸ **user** — repro flow для Bug-r9-B (содержимое страниц пропадает)
⏸ **user** — пример нерабочего Free config для Bug-r9-C
⏸ **user** — скриншот значка остановки для Bug-r9-A
⏸ **user** — ответы на 4 question'а wgturn integration (mutex / server /
   UI / provisioning) для Phase 3/4

Эти items НЕ блокируют параллели A-F. Параллель C для A/B/G можно
делать static-only до получения repro.
