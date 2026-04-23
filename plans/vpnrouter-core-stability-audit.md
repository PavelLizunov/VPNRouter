# VPNRouter core-stability audit plan

**Статус**: черновик, по следам v2.27.0 production bugs.
**Триггер**: два user-visible bug'а в v2.27.0 production use:
1. Split↔Full switch не пересобирал TUN routes (hot-reload ≠ restart для structural changes)
2. YouTube/Google traffic уходил direct из-за geoip-ru match на RU edge-cache IPs

Оба — в core функционале (sing-box config regen + bypass routing), оба слип-in из-за предположений вместо тестов. Этот план — что ещё аудитить в том же слое, до того как пользователи выловят.

---

## 1 · Методология

Core-функционал, который реально крутит трафик, делится на 5 подсистем. Для каждой — какие скрытые предположения есть, какие конкретные сценарии могут их сломать, и что именно проверяем.

Принцип: **репро или не баг**. Каждый пункт должен заканчиваться конкретным тестом (CLI / ручной / автоматизированный), который либо подтверждает корректность, либо ловит регрессию.

---

## 2 · Подсистема A: Config regeneration (ApplyAsync vs StartAsync)

### Известные баги
- **A1** ✅ Fixed — split↔full hot-reload silent no-op (v2.27.1-r1)
- **A2** ✅ Fixed — geoip-ru over-matches RU edge caches (v2.27.1-r2)

### Гипотезы для аудита

**A3. Другие structural changes тоже могут silent-fail hot-reload.**
Кандидаты на "structural":
- `tun.interface_name` — если изменилось, sing-box не пересоздаст adapter на hot-reload
- `tun.mtu` — то же
- `tun.auto_route` / `tun.strict_route` — флаги влияют на routing table setup
- `tun.ipv4_address` — если поменялся subnet, таблица роутинга старая остаётся
- `inbounds[*].listen` — port change на hot-reload игнорируется? (мы используем только TUN inbound, низкий риск, но проверить)

**Тест**: изменить каждое поле в config.yaml при работающем VPN, Apply, grep `sing-box` live-config (`curl -s localhost:9090/configs | jq`) и сравнить с файловым current.json. Ожидание — либо оба совпадают, либо force-restart.

**A4. ApplyAsync и StartAsync не идентичны по сгенерированному конфигу.**
Они оба вызывают `ConfigGenerator.Generate(profile, processes, settings)`. Но порядок операций ДО этого разный — StartAsync делает subscription refresh + process scan инициально, ApplyAsync перерезолвит профили но может пропустить что-то.

**Тест**: добавить в `VPNRouter.Tests` тест, который создаёт два идентичных AppSettings, прогоняет `StartAsync` path и `ApplyAsync` path (моки для sing-box), извлекает resulting current.json, diff'ает.

**A5. Mid-session subscription refresh не пересобирает active server.**
`SubscriptionResolver.ResolveAsync` вызывается на старте. Если пользователь в GUI жмёт "Refresh Subscription", новые server'а попадают в `Vless.Servers`, но sing-box outbound уже привязан к старому active server.

**Тест**: поднять VPN на subscription → в GUI нажать Refresh Subscription → проверить что ReloadConfig перегенерил outbound (новый `vless-<active>` тэг должен совпадать с settings.App.ActiveSubscriptionServer).

---

## 3 · Подсистема B: TUN lifecycle

### Гипотезы

**B1. TUN interface остаётся после crash.**
`sing-box.exe` создаёт Windows TAP/Wintun adapter. При crash-recovery мы рестартим sing-box, но сам adapter может остаться в системе (dangling). Следующий start попытается создать adapter с тем же именем — "Cannot create a file when that file already exists" (уже видели в логах v2.26.2-r1 test).

**Что проверить**:
- `netsh interface show interface | grep VPNRouter-TUN` — сколько adapter'ов есть после неожиданного kill.
- Наш код cleanup'а TUN при force-kill (процесс sing-box упал без очистки) — есть ли он? В коде sing-box или у нас?

**Тест**: `taskkill /F /IM sing-box.exe`, запустить VPNRouter снова — создаётся ли duplicate adapter или используется старый?

**B2. TUN routes не чистятся при Stop().**
При штатном Stop sing-box должен снять свои маршруты. Но если он застрял в TerminateProcess (force-kill), Windows routing table может остаться с записями `0.0.0.0/0 via 172.19.0.2`. После полной остановки VPN пользователь может обнаружить что трафик всё ещё «как бы» уходит на несуществующий TUN.

**Тест**: `route print` до старта, во время работы, после Stop. Diff должен быть пустой после Stop.

**B3. `auto_route: true` + `strict_route: false` — что именно делает и когда?**
У нас `strict_route: false` для безопасности (не отберёт весь трафик безоговорочно). Но `auto_route: true` что-то же делает — docs говорят "set up default route". Проверить: при Split mode наш TUN имеет default route или только specific subnets?

**Тест**: `route print` в split и в full mode — compare.

**B4. Multiple TUN instances (App + Service одновременно).**
TunOwnershipLock prevents, но что если race: App запустился, Service ещё не заметил lock (polling 1 sec), тоже попытался `sc start` → Service process creates второй TUN.

**Тест**: `App start` → через секунду `sc start VPNRouter` в параллель. Смотреть tasklist — сколько sing-box.exe, сколько TUN adapter'ов.

---

## 4 · Подсистема C: DNS

### Гипотезы

**C1. DNS hardening state не всегда восстанавливается.**
`WindowsDnsHardening.Apply` сохраняет предыдущие значения реестра, `Restore` пишет обратно. State file `dns-hardening-state.json`. Уже обрабатывается "stale state on crash" (v2.25.x).

**Edge cases**:
- Что если state file битый (JSON corruption)? Fall back to defaults — но какие defaults? Могут отличаться от реального pre-VPN состояния системы.
- Что если Windows admin задал policy (GPO) которая override'ит наш Restore? Наш `SetValue` успешен, но эффективное значение остаётся "enforced by GPO".

**Тест**: corrupt state file → launch → inspect реестр. + Read-only reg key scenario (HKEY_LOCAL_MACHINE\...\DNSClient policy set read-only).

**C2. `77.88.8.8` (Yandex DNS) direct может быть недоступен или ответить не тем.**
Мы форсим RU domains в 77.88.8.8 через `dns-direct` outbound. Если этот резолвер:
- Заблокирован ISP (редко, но бывает) → RU домены не резолвятся вообще
- Отвечает через censoring middlebox с подмененными ответами → наш RU трафик использует sink-holed IP

**Тест**: установить firewall block на 77.88.8.8:53 во время работы VPN. Что происходит с *.ru доменами? Fallback есть?

**C3. `strict_route: false` + DNS leak.**
TUN не форсит весь трафик. DNS запросы с системного resolver могут идти минуя TUN. Наш `DnsHardening.SetTunMetric` поднимает метрику TUN interface, чтобы system DNS предпочитал TUN. Но это best-effort — не гарантия.

**Тест**: `nslookup google.com` во время VPN. Проверить через Wireshark где физически уходит запрос: через TUN или через физический NIC.

**C4. `dns-direct` outbound + `udp_fragment: true`.**
Мы добавили `udp_fragment: true` к dns-direct (v1.24.3 fix for "empty direct outbound"). Это влияет на поведение при fragmented UDP ответах (большой TXT / DNSSEC). Проверить что RU DNS ответы корректно собираются.

---

## 5 · Подсистема D: sing-box subprocess lifecycle

### Известные баги
- **D1** ✅ Fixed — Service orphan cleanup убивал App's sing-box (v2.27.0-r1)
- **D2** ✅ Fixed — state.json PID sync (v2.26.2-r1)
- **D3** ✅ Fixed — TryHotReload на мёртвом процессе (v2.26.2-r1)

### Гипотезы

**D5. HealthMonitor restart storm под нагрузкой.**
Exponential backoff 5/10/20/40/80s → max 5 restarts. Что после 5-го? Должен "дать пользователю знать" через UI / крэш-репорт. В логах v2.27.0 production уже видим sing-box restarts; сколько пользователь об этом узнал?

**Тест**: форсировать 5 крэшей подряд (curl-kill sing-box каждые 3 сек), проверить UI badge + notification + exit behavior.

**D6. Race: Stop() запустился пока HealthMonitor как раз делает Restart.**
`_restartCts` должен cancel, но в окне между "CTS.Cancel() вернулся" и "await Task.Delay.ContinueWith fires" Restart уже start'нул новый sing-box, который теперь orphan.

**Тест**: `vpnrouter start` → `taskkill -F` sing-box → сразу же `vpnrouter stop` в течение 100мс. Проверить не остался ли zombie sing-box.

**D7. `SingBoxManager.Restart` vs `ReloadConfigJson` — hot-reload fallback.**
ReloadConfigJson делает hot-reload, если fails — restart. Restart пересоздаёт process. Что если Clash API на Restart'е ещё не поднялся, и мы делаем следующий ReloadConfig быстро?

---

## 6 · Подсистема E: Routing / Firewall

### Гипотезы

**E1. `block_on_vpn_fail` firewall rules.**
Создаются `VPNRouter_Block_<exe>` через netsh, path-based. Если user переустановит Discord (path меняется) — rules остаются указывать на старый path, новый Discord не блокируется.

**Тест**: создать firewall rule для Discord → удалить Discord → переустановить в другой folder → запустить новую Discord → VPN crash → проверяется leak protection на новом Discord?

**E2. `sing-tun` firewall rule orphan.**
sing-box.exe auto-создаёт Windows Firewall rule "sing-tun (<path to exe>)". При force-kill рулёж остаётся. Мы его не чистим (не наш namespace).

**Влияние**: через многократные крэши накопятся duplicate rules. Несмертельно, но клатер.

**Тест**: force-kill sing-box 10 раз → `netsh advfirewall firewall show rule name=all | grep sing-tun`.

**E3. Split tunnel: VPN down → Discord заблочен → VPN up → Discord unblocked?**
Flow: sing-box crashed → HealthMonitor enables block rules → sing-box restarted → HealthMonitor disables rules. Что если между "disable rules" и "Discord next connection attempt" есть timing window?

**Тест**: crash sing-box во время активной Discord voice-call. Восстановление sing-box → продолжается ли звонок?

---

## 7 · Подсистема F: subscription management

### Гипотезы

**F1** ✅ Fixed — cache wipe on network failure (v2.26.2-r1)

**F2. Subscription refresh пересобирает `Vless.Servers`, но не triggers ConfigReload.**
При Refresh в GUI мы обновляем список, но sing-box'у не говорим. Пользователь жмёт Apply, только тогда пересобирается.

**Тест**: subscribe → connect → менять active server в GUI без Apply → проверить какой реально используется outbound.

**F3. Multi-subscription + duplicate server names.**
Если две подписки содержат сервер с одинаковым `name`, в `Vless.Servers` будут два entry с одним tag. ConfigGenerator создаёт VLESS outbound `vless-<name>` → duplicate JSON-keys → undefined behavior.

**Тест**: настроить две подписки, у каждой "server-1" → посмотреть generated config.

---

## 8 · Подсистема G: multi-process coordination

### Гипотезы

**G1. Config watcher (Service) под нагрузкой.**
Service FileSystemWatcher на config.yaml. При быстром SaveSettings ×5 за 100мс — debounce правильно работает?

**G2. App закрыт → Service продолжает работу → App перезапущен.**
TunLock передаётся Service-у? Или App стартует с новым lock-ом что конфликтует?

**G3. Service stop (sc stop) vs Service crash.**
В обоих случаях sing-box остаётся? Процесс hierarchy.

---

## 9 · Подсистема H: sing-box update path

**H1. Текущая версия 1.13.7-vpnrouter, latest 1.13.10.**
Изменения в 1.13.8-10:
- 1.13.8 — naiveproxy update, fake-ip DNS fix
- 1.13.9 — fixes and improvements
- 1.13.10 — **Fix process searcher failure introduced in 1.13.9**

**Actionable**: пересобрать sing-box с tag 1.13.10 через `build-singbox.ps1`. Process searcher влияет на process_name matching — критично для нашего split tunnel.

**H2. sing-box 1.14.0-alpha активно разрабатывается.**
Следить, но не апгрейдиться до 1.14 stable.

---

## 10 · Приоритизация

**P0 (user-visible сейчас, fix в flight / done)**:
- A1, A2, D1-D3, F1 — all fixed

**P1 (высокая вероятность user-visible, next iteration)**:
- H1 — sing-box 1.13.10 rebuild (process_name fix). Простой pull + rebuild.
- A3 — audit structural changes beyond RoutingMode (TUN interface changes). Low-hanging.
- B1 — TUN adapter cleanup on crash. Often-reported in support requests.
- C3 — DNS leak при strict_route=false. Security concern.

**P2 (медиум риск)**:
- A4 — StartAsync vs ApplyAsync config drift
- D5 — restart storm UX
- F3 — duplicate server names across subscriptions

**P3 (низкий риск, academic)**:
- C4 — udp_fragment quirks
- E2 — sing-tun firewall clutter
- G2 — TunLock handoff

---

## 11 · Execution checklist (для следующей итерации)

### v2.27.2-r1 — SHIPPED 2026-04-23

1. ✅ **sing-box 1.13.10 upstream switch (всё три платформы)**
   - Решили взять **upstream prebuild** вместо custom rebuild. Причины:
     - `with_clash_api` + `with_utls` + `with_quic` — все три tag'а уже default в upstream 1.13+.
     - Custom build добавлял "это наш билд или upstream?" как переменную при диагностике. Убрали.
     - Upstream релизы подписаны, reproducible. Наш — нет.
     - +12MB per platform — acceptable trade-off.
   - Linux: `SINGBOX_VER` в `.github/workflows/build-linux.yml`: 1.13.3 → 1.13.10.
   - Mac: `build-mac.sh` теперь `curl`'ит upstream darwin-arm64 tarball и кладёт в `$APP/Contents/MacOS/` (раньше вообще не бандлилось; комментарий в MainWindowViewModel был устаревший).
   - Win: `build.ps1` auto-downloads upstream Windows zip в `tools/singbox-cache/`. `-SingBoxPath` остался как override для custom билдов. `build-singbox.ps1` переписан с "build from Go source" на "download upstream prebuild".

2. ✅ **A3 audit — TUN structural changes**
   - `VpnEngine.ComputeTunFingerprint(TunSettings)` — хеш из `InterfaceName / Ipv4Address / Ipv6Enabled / Mtu / AutoRoute / StrictRoute / RouteExcludeAddress`. Сортировка excludes order-independent.
   - `TunFingerprint` кэшится в StartAsync, сравнивается в ApplyAsync. Любой mismatch → `forceRestart = true`. Тот же паттерн, что RoutingMode check в v2.27.1-r1.
   - Regression tests: 12 новых xUnit тестов (VpnEngineTunFingerprintTests.cs). `InternalsVisibleTo("VPNRouter.Tests")` позволил protected helper без public surface.

3. ✅ **B1 audit — TUN adapter diagnostics**
   - `TunAdapterDiagnostics.LogAdapterState(logger, context)` — вызывает `netsh interface show interface`, грепает `VPNRouter-TUN` / `sing-box-tun`, пишет в лог.
   - Вызывается из `OrphanCleanup.KillOrphans` (before/after) и `VpnEngine.Stop` (after).
   - **PASSIVE**: никаких delete'ов. Цель — собрать production-логи подтверждающие/опровергающие гипотезу "dangling adapter после kill". Active cleanup добавим когда будет репро.

### Deferred to v2.27.3+

4. **C3 audit — DNS leak with strict_route=false** (P0)
   - Wireshark capture during VPN session, grep :53 traffic.
   - If leak confirmed, options: (a) flip strict_route=true (riskier — breaks LAN), (b) add explicit DNS firewall rule that blocks :53 outside TUN.

5. **A4 — ApplyAsync vs StartAsync config diff** (P1)
   - Ещё один integration test: два идентичных AppSettings, прогнать StartAsync + ApplyAsync, diff current.json. Должен быть byte-identical.

6. **A5 — subscription refresh через mid-session** (P1)
   - Smoke-test repro: poднять VPN на подписке → Refresh Subscription → grep live Clash API config на новый outbound tag.

7. **§4.6 C3 — ServiceViewModel state machine** (P1 UI polish)
   - Idle → Installing → Starting → Running / Failed с Retry button.

---

## 12 · Success criteria

### v2.27.2-r1 status

- ✅ Все три платформы на upstream sing-box 1.13.10
- ✅ TUN structural-change regression auto-detected (no manual forceRestart)
- ✅ 12 новых fingerprint тестов в CI
- ✅ Diagnostic logging на место для сбора B1 данных
- 📋 Документ поддерживается живым: каждый production-bug добавляется в соответствующую подсистему, чтобы накапливать инвариантные знания.

---

## Приложение: быстрые diagnostic commands

```powershell
# Что sing-box сейчас видит
curl -s http://127.0.0.1:9090/configs | ConvertFrom-Json | ConvertTo-Json -Depth 20

# TUN adapter inspection
netsh interface show interface
Get-NetIPConfiguration -InterfaceAlias "VPNRouter-TUN"

# Routing table
route print

# Firewall rules VPNRouter
netsh advfirewall firewall show rule name=all | Select-String "VPNRouter"

# DNS client policy
reg query "HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient"
```

```bash
# sing-box process tree inspection (PowerShell)
Get-Process sing-box | Select-Object Id, StartTime, Path, Parent
```
