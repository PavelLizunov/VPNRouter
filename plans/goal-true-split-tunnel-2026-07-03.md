# GOAL: True OS-level split tunnel — excluded apps survive VPN failure

**Триггер:** в split-режиме excluded-приложения (ходят мимо VPN) падают вместе с VPN,
потому что sing-box TUN (`AutoRoute=true`) захватывает ВЕСЬ трафик, а split — это
`route.rules` по process_name ВНУТРИ туннеля (post-capture). Если sing-box висит/падает —
default-route смотрит в мёртвый TUN → чёрная дыра для всего, включая excluded.

**Research (полный разбор, читать перед стартом):**
[`plans/true-split-tunnel-research-2026-07-03.md`](true-split-tunnel-research-2026-07-03.md)
(Fable-5, 2026-07-03; скачал+проверил подписи драйвера Mullvad локально). Crash-behavior
matrix §1.1; опции W0-W6 §2-3; Linux §5; W0 §7; W1 impl-sketch §9; open questions §10.

**Порядок (ранжирован research'ем §8):** W0 сейчас → W1 (Windows-драйвер) → Linux cgroup →
macOS (пока только W0). Каждая фаза проверяема отдельно, НЕ монолит. Каждая >30 строк /
новая абстракция идёт через `phase-task-launcher` (6-gate lifecycle).

---

## Phase W0 — Windows wedge-kill (ДЕЛАТЬ ПЕРВОЙ; ~40 строк)
Полный дизайн + verified findings: [`plans/w0-hang-detection-design-2026-07-03.md`](w0-hang-detection-design-2026-07-03.md)
(Fable-5, 2026-07-03; поправил stale-research). **Единственная реальная работа — W0.1.**
Grounding-корректировки: crash самозалечивается (адаптер умирает с процессом → ОС
возвращает маршруты); на Windows `IsHealthy()` = только liveness → **hang (живой но не
форвардит) НЕ детектится вообще** → бесконечная чёрная дыра; macOS уже kill'ит hang (его
IsHealthy API-based) — Windows аутлайер.

- [x] **W0.1 Windows wedge-kill — DONE 2026-07-03 (`28bb4c0b`)**: `KillWedgedForRecovery` в
  Lifecycle + wedge-блок в OnHealthTick + pure `WedgeKillPolicy` (+4 теста) + мемоизированный
  Serving() шарится с 2 probe-сайтами. Build 0 errors, 15/15 логик-тестов зелёные (baseline
  ProgramData-фейлы env-only). Ниже — оригинальный план (реализован как описано):
- [ ] ~~W0.1 план~~ = детект hang'а + kill-first (превращаем hang в crash,
  дальше весь проверенный crash-путь применяется без изменений). Правки:
  1. `SingBoxManager.Lifecycle.cs`: `+KillWedgedForRecovery() => StopInternal(releaseLock:false)`
     (~10 строк) — убивает дерево, НЕ релизит TUN-lock (ровно состояние после реального
     crash'а; relaunch его не переполучает — только StartWithJson).
  2. `HealthMonitor.cs` (после line 402): +2 поля (`_servingConfirmed` latch, `_wedgeStreak`)
     + const threshold=2. Wedge-блок с мемоизированным `Serving()` (шарится с 2 существующими
     probe-сайтами — lines 418/507, probe уже 1s/per-tick при DnsLeakLockdown). Reset в
     `Start()`/`OnSingBoxCrashed`. На триггер (alive + !serving ≥2 тика, latch armed):
     `KillWedgedForRecovery()` + прямой `OnSingBoxCrashed(this, EventArgs.Empty)` + return.
  3. Safety: **serving-confirmed latch** (арм только после 1-го успешного probe/lifecycle —
     против false-kill в TUN-warmup ~16s + restart-storm на кастомных clash_api-портах).
  4. (опц.) `WedgeKillPolicy` ~12 строк pure → Linux-CI matrix-покрытие.
- **W0.2 (TCP-canary) — ОТКЛОНЁН** (путает dead-server с local-wedge → false-kill; домен G4).
- **W0.3 (DNS-lockdown) — УЖЕ DONE** (v2.42.0 `ReconcileLockdownForHealth` гейтит по флагу, НЕ
  по режиму → exclude-mode покрыт; `StrictDnsIsSoleDriver` — другая фича). Ничего не делать.
- **W0.4 (flap dampening) — УЖЕ ЕСТЬ** (MaxRestartAttempts + backoff + IsRunning-skip; kill-first
  делает skip корректно-false).
- **Тесты:** `+HealthMonitorWedgeKillTests` (recipe `FakeProcessRunner`+`FakeSingBoxApi`); прогнать
  full HealthMonitor+SingBoxManager suites (design §5) — не сломать `HealthMonitorTimerRaceTests`,
  `HealthMonitorRecoveryGapTests`, `HealthMonitorDnsLockdownTests`.
- **Acceptance:** hang-эмуляция (процесс жив, Clash-API мёртв) → после 2 тиков kill → excluded
  оживают (~11-15s strict / ~62-70s normal vs бесконечность); crash по-прежнему самозалечивается;
  routed остаются fail-closed; существующие suites зелёные; паритет с macOS.
- **Риски (accept/guard):** (1) false-kill на >2-tick API-stall → 1 bounded TUN-bounce (latch+
  threshold+1s cap); (2) repeated-wedge ресетит `_restartAttempts` → G4-failover не трипается
  (accepted, как slow-crash-loop; W1 obsolete'ит); (3) memoized-probe short-circuit на 418/507 —
  guard `HealthMonitorDnsLockdownTests`.
- **Риск:** LOW-MED. **Оценка:** ~40 строк + тесты.

### Outcome W0.1 (2026-07-03) — PASS
- **Status:** PASS (реализовано ровно по дизайну; ре-юз проверенного crash-пути).
- **Commits:** `28bb4c0b` (feat) + `88dc46d5` (review cleanup). Пушнуто в оба remote.
- **Files:** `SingBoxManager.Lifecycle.cs` (+`KillWedgedForRecovery`), `HealthMonitor.cs`
  (wedge-блок + 2 поля + мемо `Serving()` + 2 probe-сайта), new `WedgeKillPolicy.cs`,
  new `WedgeKillPolicyTests.cs` (4 pure). ~40 строк прод + 54 теста.
- **Gate 1 build:** 0 errors. **Gate 2 tests:** 15/15 (4 WedgeKillPolicy + 11
  HealthMonitor timer-race/recovery/dns-lockdown); baseline ProgramData-фейлы env-only.
- **Gate 4 self-review:** 2 независимых adversarial-ревьюера на реальном диффе —
  корректность/concurrency + leak-safety/fail-closed. Оба **CLEAN** (ни P0, ни P1).
  Подтверждено на коде: тик сериализован `_onHealthTickInProgress` Interlocked-гейтом;
  `SuppressExitedEvent`+`_stopInProgress` глушат двойной `OnSingBoxCrashed`; kill-switch
  block-rules переживают kill (отдельны от TUN, `EnableBlockRules` синхронно на kill-тике);
  DnsLeakLockdown reconcile'ится fail-open на том же тике (инвариант v2.42.0 цел); routed
  fail-closed; латч ресетится до relaunch. Единственный P2 (оба ревьюера) — dead-store +
  log-arg — пофикшен в `88dc46d5`.
- **Gate 5 MCP:** отложен — wedge (процесс жив, Clash-API мёртв) трудно эмулировать вживую;
  low-value "app launches" verify не делаем. Live-верификация — когда W0.1 поедет в
  кандидате, бандл с дальнейшей true-split работой (НЕ отдельный -rN на один internal tweak
  сразу после v2.45.0 stable).
- **Follow-ups:** нет. Residual risk #2 (repeated-wedge не трипает G4-failover) — accepted,
  не хуже slow-crash-loop, obsolete'ится W1.

## Phase W1 — true split через Mullvad WFP-драйвер (Windows; главная фича; 2-4 нед)
**Что:** забандлить пребилд `mullvad-split-tunnel.sys`+.cat+.inf (GPL-3.0-or-later ИЛИ
MPL-2.0 — с нашим GPL ок; .cat подписан MS WHCP → грузится с Secure Boot; **подписывать
ничего не надо**). Порт user-mode агента на C#. Чинит **exclude-режим** by construction
(excluded-потоки биндятся к физ-NIC, переживают смерть sing-box). Include остаётся
post-capture (индустри-wide у bind-redirect-драйверов).

- [ ] **W1.0 Phase-0 spike** (2-3 дня, throwaway console): `sc create mullvad-split-tunnel
  type=kernel`, открыть `\\.\MULLVADSPLITTUNNEL`, прогнать IOCTL state-machine с хардкодом
  (один excluded notepad.exe, текущий NIC+TUN IP). Verify: notepad идёт через NIC пока
  sing-box жив; убить sing-box → notepad не затронут. Structs/IOCTL — порт из
  `talpid-core/src/split_tunnel/windows/driver.rs` (dual-lic как драйвер). На windows-brat
  (vmid 100) через `testvm-control.ps1`. **GATE: PASS → дальше; FAIL (напр. netsh-block
  interplay, open-q #1) → СТОП, только W0.**
- [ ] **W1.1 `SplitTunnelDriverManager.cs`** (Core): service install/start/stop, device
  handle, state-machine, `RegisterProcesses` (Toolhelp32 + image paths), `SetConfiguration`
  (excluded names → full paths через `ProcessScanner` → NT device paths `QueryDosDevice`),
  `RegisterIpAddresses` из `NetworkInterfaceDetector`, inverted-call event-pump на bg-треде.
  **Fail-open:** любой сбой драйвера → лог + fallback на текущий post-capture (никогда не
  бричим сеть).
- [ ] **W1.2 Lifecycle-wiring** (`VpnEngine.StartAsync`/`Apply`/`Stop`): engage/disengage
  вокруг старта sing-box; IP-change → re-register; uninstall драйвера при деинсталле app.
  W0.3 Wave-39 scoping применяется.
- [ ] **W1.3 UX + docs**: "true split" бейдж в exclude-режиме; оговорки на виду
  (driver DNS / localhost-UDP / multicast / UWP-не-исключается — §2.1). MCP-verify:
  exclude Discord → kill sing-box.exe → Discord voice выживает.
- [ ] **W1.4 Packaging**: +~110 KB (3 файла) в `build.ps1`, версия драйвера pinned+checksummed
  (паттерн как sing-box-lx bundling). Service-collision guard (open-q #4): детектить
  существующий `mullvad-split-tunnel` сервис (если стоит настоящий Mullvad) → reuse или
  refuse с внятным сообщением.
- **Acceptance W1:** exclude Discord → соединение идёт через физ-NIC (не через exit-IP);
  `Kill sing-box.exe` → Discord/voice НЕ прерывается вообще (0 gap); routed-приложения
  fail-closed; fallback при сбое драйвера не бричит сеть.
- **Риск:** MED (драйвер зрелый+fuzzed funded-командой; наш риск — корректность агента +
  interop с netsh). **Оценка:** 2-4 нед.

## Phase L1 — Linux cgroup v2 split (независимо; лучший correctness-per-effort; S-M)
- [ ] cgroup v2 + nftables `socket cgroupv2` → mark `0xf41` → `ip rule` ПЕРЕД правилами
  sing-box (index 9000 / таблица 2022); + одно accept-правило в nft-killswitch (заодно
  смягчает audit P1-6 empty-list hazard). Excluded-приложения биндятся к физ-NIC на
  уровне ядра, переживают смерть sing-box.
- **Acceptance:** exclude-приложение в cgroup → egress через физ-NIC; убить sing-box →
  не затронуто. **Оценка:** S-M.

## Phase M1 — macOS (пока только W0)
- [ ] W0-эквивалент (fast teardown utun). Санкционированный true-split
  (`NETransparentProxyProvider`) — Apple-церемония (entitlements/notarization), someday-item.

## Открытые вопросы (закрыть в W1.0 spike / по ходу) — см. research §10
1. Пробивают ли permit'ы драйвера netsh(MPSSVC)-блоки + Wave-39 DNS-lockdown? (тест №1)
2. Драйвер vs zapret/WinDivert на тех же потоках (winws трогает 80/443 всех) — ожидаемо
   benign (zapret payload-mangling, не routing), нужен 1 тест.
3. IOCTL ABI-стабильность между релизами Mullvad — pin точный commit `driver.rs` под
   версию .sys, re-verify на бампах.
4. Service-collision с настоящим Mullvad (W1.4).
5. Include-mode long game (WinDivert-inverted XL) — сначала собрать сигнал из user-reports.

## Что НЕ делать (research §8, отклонено)
Свой/форкнутый драйвер (signing-экономика: EV-cert $280-580/год + Partner Center),
WinDivert как VPN data-path (signing provenance — наш .sys ре-подписан истёкшим в 2023
CN-сертом + rearchitecture), ProxiFyre/WinpkFilter (commercial redistribution), ждать
sing-box (Windows per-app архитектурно вне upstream-scope).

## Связь
- Research: `plans/true-split-tunnel-research-2026-07-03.md`
- Затрагивает: `HealthMonitor.cs`, `FirewallManager.cs` (W0); `ConfigGenerator.cs`
  BuildInbounds ~1103, `VpnEngine.cs`, новый `SplitTunnelDriverManager.cs` (W1);
  `LinuxFirewallManager.cs` (L1). Kill-switch interop — research §6.
