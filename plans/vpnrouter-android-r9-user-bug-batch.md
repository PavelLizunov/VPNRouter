# Android r9 — user bug batch (2026-05-11)

## Триггер
После shipа r8 (commit `038efba`, all-fixes APK) пользователь сообщил о новой
порции багов и предоставил логи реального юзера `stas` (`Z:\stas\`):
- `singbox (4).log` (13.7 MB)
- `vpnrouter20260510.log` (166 KB)

Параллельно user указал на следующее направление работы: интеграция страницы
"экстренного канала" через отдельное ядро
[`PavelLizunov/wgturn-core`](https://github.com/PavelLizunov/wgturn-core).

## Симптомы (3 заявленных + ожидаемые ещё)

### Bug-r9-A — значок остановки

**Status**: pending repro.
**User text**: "значек остановки".
**Гипотеза**: на каком-то экране (Simple page Connecting state / Advanced
footer / Stop VPN button) Stop-icon рендерится плохо (не тот символ, не
центрирован, или использует unicode-глиф который не существует в системном
шрифте — аналогично `◂ Simple` который мы починили в r7 заменой на `←`).

**Surfaces для проверки**:
- `VPNRouter.Core/Localization/Strings.cs` — Stop / Disconnect / Cancel
  glyphs (`■`, `⏹`, `✕`, etc.)
- `VPNRouter.Android/AndroidApp.AdvancedShell.cs` — footer Stop button.
- `VPNRouter.Android/AndroidApp.axaml.cs` — Simple page Connect button
  active-state icon (Square `■` in design).
- Advanced shell sticky-footer Stop button (`r6c-simple-connected.png`
  showed it as text-only — design has small `■` glyph next to "Stop").

**Acceptance**:
- [ ] Все Stop / Disconnect / Cancel CTA рендерятся читаемо на KYOCERA
  Android 12.

### Bug-r9-B — содержимое страниц пропадает

**Status**: pending repro.
**User text**: "содержимое страниц пропадает при непонятных обстоятельствах".
**Гипотеза**: после какого-то state change (theme toggle, language toggle,
back-from-permission-dialog, lifecycle pause/resume?) UI становится пустым.
Связано с `RebuildSimplePageView()` который был добавлен в r6 как Bug #4
fix для dark theme live toggle. RebuildSimplePageView assigns new MainView,
но НЕ переподписывает на `MainActivity.IntentChanged` / `TunnelErrorReported`
events которые подключаются в `OnFrameworkInitializationCompleted` через
`view.AttachedToVisualTree` once-only handler. Если rebuild сработает в
момент когда вторая подписка ещё не отписалась — старые контролы могут
получать события и крашить новые.

**Surfaces для проверки**:
- `VPNRouter.Android/AndroidApp.axaml.cs:440-512` — `OnFrameworkInitializationCompleted` +
  event wiring.
- `VPNRouter.Android/AndroidApp.axaml.cs::RebuildSimplePageView` (Bug #4 fix).
- `VPNRouter.Android/AndroidApp.AdvancedShell.cs` — `CloseAdvancedShell` /
  `OpenAdvancedShell` lifecycle.

**Repro plan**:
- Через `stas`'s log поискать exceptions / NREs / "null reference" / "white
  screen" timestamps.
- Воспроизвести флоу: connect → switch theme → connect → switch language →
  reopen Advanced shell → проверить что контент на месте.

**Acceptance**:
- [ ] Theme toggle на любом screen не приводит к пустому content area.
- [ ] Language toggle сохраняет text + layout.
- [ ] Back из VPN permission dialog не теряет state.
- [ ] Reopen Advanced shell после первого OpenAdvancedShell показывает
  ту же вкладку + контент.

### Bug-r9-C — Free tab ищет нерабочие конфиги, логика ≠ PC

**Status**: design-level deviation.
**User text**: "Страница free ищен нерабочие конфиги, то есть логика не =
pc логике страницы free".
**Гипотеза**: Android Free tab делает только TCP+TLS probe, без deep verify
(Bug #1 fix добавил libbox-based deep verify, но PC уже использовал
`FreeConfigDeepVerifier` который делает реальный HTTP round-trip через
SOCKS — может быть, что Android-вариант verify path не запускает то же
самое. И/или: filter logic пропускает «зелёные по TCP+TLS но недоступные
для VLESS handshake» — на PC такие отсеиваются deep verify пасом, на
Android — нет).

**Surfaces для проверки**:
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs` (PC).
- `VPNRouter.Android/AndroidFreeConfigDeepVerifier.cs` (r8 / Bug #1 commit).
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs` — orchestration.
- Compare PC vs Android: какие именно probe stages, в каком порядке, какой
  threshold для "Verified" vs "Working" vs "Suspicious".

**Repro plan**:
1. Открыть Public tab на phone + Find.
2. Скачать тот же pool на PC + Find.
3. Сравнить итоговые списки. Какой % "Verified" на PC vs Android.
4. Взять 5 "Verified-only-on-Android" конфигов + проверить их вручную через
   adb shell / sing-box check на PC.

**Acceptance**:
- [ ] Android Public Find возвращает только реально рабочие конфиги
  (deep-verified через libbox + working through tunnel).
- [ ] Разница с PC < 10% по списку Verified.

### Bug-r9-D ... "и есть множество багов"

**Status**: discovery pending.
Будут добавлены по мере анализа `stas`'s log + дальнейших user reports.

## Логи stas — анализ (2026-05-11)

**Файлы**: `Z:\stas\singbox (4).log` (13.7 MB / 111828 строк) +
`Z:\stas\vpnrouter20260510.log` (166 KB). Из путей в логах видно что
**stas — PC user** (Windows): VPNRouter-install-v**1.24.6** (старая
сборка!), Windows-user `Khunrath`. Android-логов не присылал.

### Найденные критические проблемы

**Bug-r9-E** (NEW from log) — TUN adapter conflict с другим VPN.
- Симптом: `[WRN] [sing-box] [31mFATAL[0m start service: start
  inbound/tun[tun-in]: configure tun interface: Cannot create a file
  when that file already exists.` → `[ERR] sing-box crashed (exit
  code: 1)`.
- Root cause: параллельно запущен **v2RayTun** (`C:\Users\Khunrath\
  AppData\Local\Temp\v2RayTun\xraycore.exe`). Sing-box не может создать
  TUN потому что xraycore уже держит свой.
- **UX-критично**: VPNRouter в этой ситуации даёт cryptic FATAL без
  обьяснения. User не понимает что нужно остановить v2RayTun.
- **Fix**: pre-flight check — detection других VPN tool'ов (по имени
  процесса: xraycore.exe, wireguard.exe, openvpn.exe, hiddify.exe,
  amneziavpn.exe), показывать понятный alert.

**Bug-r9-F** (NEW from log, RECLASSIFIED) — **silent fallback to dead Custom
Config server**.
- Симптом: **все** connections через `outbound/vless[proxy]` тайм-аутят
  на `dial tcp 195.135.255.216:443: i/o timeout`. IP `195.135.255.216`
  **НЕ в его подписке** (user confirmed 2026-05-11).
- В log одновременно видны **2 outbound**:
  1. `outbound/vless[vless-de-01 443 Khunrath]` — его named сервер
     (из подписки, работает).
  2. `outbound/vless[proxy]` — **generic "proxy" tag**, dial-ит чужой IP
     `195.135.255.216:443` (тайм-аутит).
- **Root cause гипотеза**: stas в `ConfigMode=custom` (Custom Config
  Mode) + у него в paste'нутом sing-box JSON прописан **мёртвый сервер
  195.135.255.216**. Source — `CustomConfigInjector.cs:248`:
  `return ob["tag"]?.ToString() ?? "proxy"` — если в его custom JSON
  у outbound нет явного tag, injector ставит `"proxy"`. После этого
  route rules идут через этот "proxy" outbound → весь трафик в дохлый
  сервер. Подписочные серверы (`vless-de-01 ...`) загружены параллельно
  но не используются route rules.
- **Это DEFCT-005-Desktop equivalent** — на Android мы уже фиксили
  placeholder-leak в `MainActivity.cs` (commit 5a771a6), на desktop та же
  proблема в Custom Config Mode pathway.
- **Что нужно для подтверждения** (⏸ **HOLD — ждём пока stas вернётся**, 2026-05-11):
  - `%ProgramData%\VPNRouter\config\current.json` от stas — увидеть точное
    содержимое outbound[proxy].
  - `%ProgramData%\VPNRouter\config.yaml` от stas — verify
    `app.config_mode = "custom"` + `app.custom_config = ...path...`.
  - Когда stas снова на связи → запросить эти 2 файла через user'а →
    подтвердить или опровергнуть гипотезу → spawn fix-chip с 3 defense-
    in-depth изменениями (CustomConfigInjector tag policy +
    LeakProtection check + Simple-page outbound display).
- **Fix paths**:
  - (Defense-in-depth) `CustomConfigInjector` НЕ должен silently тегать
    outbound как "proxy" если route rules уже ссылаются на subscription
    outbound. Лучше использовать `custom-proxy` или явно warn'ить.
  - LeakProtection должна детектировать: outbound name="proxy" но IP не
    в `vless.servers` ∪ `subscriptions[*].servers` → **WARNING/BLOCK**.
  - UX: в Simple page показать актуальный target IP/host + протокол —
    user должен видеть какой сервер используется. Сейчас он видит только
    "subscribe · split" без названия сервера.

**Bug-r9-G** (NEW from log) — Zapret winws.exe сразу exit.
- Симптом: `[WRN] [Zapret] Wrapper exited (exit code: -1)` →
  `winws.exe exited immediately`.
- Возможная причина: AV-блок (антивирус считает winws.exe подозрительным
  и тихо убивает) или missing dependency.
- **Fix**: вывести более ясное сообщение и предложить добавить
  `%ProgramData%\VPNRouter\zapret\` в whitelist AV.

**Bug-r9-I** (NEW from user report 2026-05-11) — **Apps tab настройки не
persist'ятся через перезагрузку Windows**.

- User quote (verbatim):
  > "Привет! Слушай, а есть какая то отдельная кнопка для сохранения
  > настроек? я прост каждый раз когда захожу отправляю фаерфокс в
  > исключения потому что там ру сайты, а когда перезапускаю винду
  > галочка на нем опять стоит"

- Симптом (desktop): user в Applications tab переводит Firefox в
  **Exclude** (через VPN не идёт — чтобы RU сайты работали напрямую).
  После reboot'а Windows checkbox опять стоит в "Include" (через VPN).
  Изменение **исчезает** при перезапуске винды.

- Возможные root causes:
  1. **Auto-save не срабатывает** на toggle — нужен explicit Save
     button. User спрашивает "есть ли отдельная кнопка для сохранения"
     — значит её точно не видит. Если на desktop нет Save bar (или
     она спрятана), его toggle живёт только в-памяти до перезапуска.
  2. **Service vs User app** запись idle race — если VPNRouter Service
     стартует первым на boot и читает старый config.yaml, потом user-app
     открывает Apps tab и user toggle'ит → но Service не реагирует, и
     при следующем boot Service опять стартует с тем же старым config.
  3. **Settings save в неправильное место**: per-user `%AppData%` вместо
     `%ProgramData%` — если LocalSystem service пишет в `%ProgramData%`
     но user-app читает/пишет в `%AppData%`, конфликт.
  4. **Auto-start реверт**: Windows boot → VPNRouter starts → читает
     дефолтный профиль → перекрывает user-кастомизации.

- **Acceptance**:
  - [ ] User добавляет Firefox в Exclude, перезагружает Windows, заходит
        в Apps tab → Firefox по-прежнему в Exclude.
  - [ ] Если auto-save не работает — добавить explicit "Save" bar.
  - [ ] Если конфликт записи Service ↔ User-app — единый source of truth
        в `%ProgramData%\VPNRouter\config.yaml` + проверка timestamp.

- **Investigation steps**:
  1. Воспроизвести: установить v2.32.0 stable, открыть Apps, toggle
     Firefox → Exclude, посмотреть `config.yaml` (timestamp + содержимое).
  2. Reboot Windows.
  3. Открыть Apps снова — проверить state.
  4. Если изменение есть в `config.yaml` но UI показывает Include →
     проблема в load path UI.
  5. Если изменения нет в `config.yaml` → save не сработал.
  6. Проверить есть ли visible "Save" / Apply bar в десктопе.

- **Surfaces**:
  - `VPNRouter.App/ViewModels/MainWindowViewModel.cs`:`SaveSettings` —
    desktop save path.
  - `VPNRouter.App/Views/Pages/ApplicationsPage.axaml` — Apps tab UI.
  - `VPNRouter.Core/SettingsLoader.cs` — read/write config.yaml.
  - `VPNRouter.Service/` — service-mode auto-start read path.

- **Severity**: P1 (high-frequency user pain — каждый reboot заново
  настраивать; раздражает но не блокирует функционал).

**Bug-r9-H** (NEW from log) — stale TUN после crash sing-box.
- После crash'а на TUN init (Bug-r9-E), следующий старт sing-box тоже
  падает с тем же "file already exists" потому что предыдущий процесс
  не успел убрать TUN adapter.
- Текущий `TunDiag.cs` логит cleanup но не делает форсированное
  удаление stale TUN adapter перед next start.
- **Fix**: pre-start TUN cleanup — найти adapter `VPNRouter-TUN` /
  `sing-box-tun`, удалить, потом start. Уже отдельный TODO в core
  audit (`plans/vpnrouter-core-stability-audit.md` §TUN), но user-
  facing severity повышается.

### Что НЕ нашлось в логе stas

- Никаких сетевых evidence для Free Configs pool fetch (он запускал
  только VPN, не Public configs).
- Никаких Android crash'ей — он использует ТОЛЬКО PC версию (v1.24.6).
- Никаких Reality fingerprint mismatch.

## Следующее направление — wgturn-core integration

User указал: интеграция страницы "экстренный канал" через отдельное ядро
[`PavelLizunov/wgturn-core`](https://github.com/PavelLizunov/wgturn-core) —
private repo, cloned for analysis 2026-05-11.

### Что это (из README + CLAUDE.md в репо)

- **Pure-Go embeddable library** (`pkg/wgturn`, `pkg/wgkernel`, `pkg/wgconf`).
- **Survivor-grade emergency tunnel**: tunnels WireGuard UDP traffic через
  публичную TURN-инфраструктуру VK Calls с DTLS 1.2 obfuscation. Цель —
  когда РКН white-list-mode блокирует **всё** (OpenVPN, WireGuard,
  Shadowsocks, xray), VK остаётся reachable как государственно-mandated
  сервис → его TURN относится к разрешённым.
- **Hard cap ~200 KB/s (~1.6 Mbps) на устройство** — VK rate-limit
  anonymous TURN tokens per source IP. Голосовой-класс, не video.
- **Не daily-driver VPN** — для обычного использования основной канал
  (sing-box + VLESS+Reality) лучше. Это именно fallback last-resort.

### Архитектура (из репо)

```
wgturn-core/
├── pkg/wgturn/                 -- Public API: Tunnel, Config, SocketProtector
│   └── provider/vk/           -- VK Calls anonymous TURN creds
├── pkg/wgkernel/              -- Embedded WireGuard userspace (wireguard-go)
├── pkg/wgconf/                -- Парсер #@wgt: метаданных в wg-quick .conf
├── pkg/wgshare/               -- wgturn:// URL profile codec
├── pkg/wgturnsrv/             -- Server side (если хост сами держим)
└── cmd/wgturn-cli/            -- CLI: connect / connect-url / serve / provision-url
```

**Platform integration** через `SocketProtector` interface:
- Linux/Win/macOS: `wgturn.NoopProtector{}`.
- Android: call `VpnService.protect(fd)` через JNI.
- iOS: rely on `NEPacketTunnelProvider`.

**TUN API**:
- `NewSystemTUN(name, mtu)` — Linux/Win/macOS desktop (root required).
- `NewTUNFromFD(fd, mtu)` — Android `VpnService` / iOS `NEPacketTunnelProvider`.

**Currently NOT shipped**: gomobile bindings (.aar / .xcframework) — README
говорит "lands when we test on Android". Эта работа в roadmap, не done.

### Предлагаемая интеграция в VPNRouter

**Phase 1 — discovery + gomobile build chain (1-2 дня)**:
- [ ] Запустить `gomobile bind` против `pkg/wgturn` + `pkg/wgkernel` → получить
  `wgturn.aar`. Требует Go toolchain + gomobile install.
- [ ] Положить .aar в `VPNRouter.Android/Lib/` рядом с `libbox.aar`. Реф в
  `.csproj` через `<AndroidLibrary Include="Lib\wgturn.aar" Bind="false" />`.
- [ ] Создать Java shim (`WgturnService.java`) аналогично `VpnRouterService.java`
  но с регистрацией через `wgturn.AndroidAdapter` (если будет в .aar) или
  ручной wrap VPNService.protect(fd) → передача в Go.

**Phase 2 — Core service layer (1 день)**:
- [ ] `VPNRouter.Core/Services/EmergencyChannel/`:
  - `EmergencyChannelEngine.cs` — lifecycle StartAsync/Stop, mirrors `VpnEngine`.
  - `WgturnConfig.cs` — модель для `wgturn://` URL + VK link.
  - `WgturnConfigStore.cs` — persist в `AppSettings.EmergencyChannel`.
- [ ] Каналы:
  - Desktop: spawn `wgturn-cli.exe connect-url <url> --vk-link <link>` как
    subprocess (similar to sing-box.exe).
  - Android: вызвать через JNI `wgturn.Tunnel.Start(config)`.

**Phase 3 — UI: "Экстренный канал" tab/page (1 день)**:
- [ ] Новая tab в Advanced shell (7-я после Public) ИЛИ отдельная карточка
  в Simple page как secondary CTA.
- [ ] Поля:
  - Input: `wgturn://` profile URL (paste-or-scan-QR).
  - Input: VK call link (separate input — runtime parameter, не в profile URL).
  - Status: Disconnected / Connecting / Connected.
  - Bandwidth display + текст "Ограничение ~200 KB/s — это нормально, это
    fallback на случай white-list."
  - Connect / Disconnect button.
- [ ] Mutex с основным sing-box VPN — два tunnel'а одновременно работать
  через VpnService не могут. Либо переключение, либо явное "running parallel
  с ограниченной маршрутизацией" (этот аспект надо обсудить).

**Phase 4 — Server-side (если своя инфра)**:
- [ ] Развернуть `wgturn-cli serve` где-нибудь рядом с текущим infrastructure
  (Forgejo VPN host 10.9.1.1?).
- [ ] `provision-user.sh` создаст user конфиги, генерирует `wgturn://` URL.
- [ ] Distribution: положить ссылки в `.claude_handoff.md` для теста, потом —
  встроить в .ninitux.com sub.

**Затрагивает**:
- VPNRouter.Core/Services/EmergencyChannel/ — новый namespace.
- VPNRouter.Core/Models/AppSettings.cs — добавить `EmergencyChannel` секцию.
- VPNRouter.Android/AndroidApp.EmergencyChannel.cs — новая partial с UI.
- VPNRouter.Android/Lib/wgturn.aar — новая dep.
- VPNRouter.App/Views/Pages/EmergencyChannelPage.axaml — desktop parity.
- VPNRouter.Service/ — упаковка `wgturn-cli.exe` в installer для PC.

### Открытые вопросы — нужно user ответить

1. **Mutex policy**: главный канал (sing-box VLESS) + Emergency (wgturn)
   одновременно или only-one-at-a-time?
2. **Server-side infra**: сами держим `wgturn-cli serve` или используем
   VK Calls как goal-end через провайдера-fallback (без своего сервера)?
3. **UI placement**: новый tab "Экстренный" в Advanced shell или карточка
   на Simple page (типа "Запасной вариант если основной не работает")?
4. **Per-user provisioning**: ручной обмен `wgturn://` URL или
   self-service (user сам генерирует через scripts/provision-url.sh)?

Ответы определят scope Phase 3 + Phase 4. Phase 1-2 (build chain + Core
service) идут без зависимости от этих ответов.

## Оценка

- Bug-r9-A: 30 мин (icon swap).
- Bug-r9-B: 1-2 ч (repro + state machine fix).
- Bug-r9-C: 2-3 ч (compare PC vs Android verify pipeline, align).
- wgturn-core integration discovery: 2-3 ч (research + planning).
- Implementation wgturn-core: TBD после discovery.

## Связь с другими планами
- `plans/vpnrouter-android-functional-testing-and-polish-plan.md` — основной
  Android testing plan.
- `plans/test-results-android-advanced-r3-2026-05-10.md` — last full test
  sweep. Bug-r9-A / Bug-r9-B / Bug-r9-C не попадали туда т.к. эти симптомы
  user обнаружил после ship'а.
