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

## Phase W0 — fast-teardown митигация (ДЕЛАТЬ ПЕРВОЙ; дни; без драйвера)
**Цель:** excluded оживают за секунды вместо «до ручного вмешательства». Нужен и ПОСЛЕ W1
(драйвер сам не чинит DNS через мёртвый TUN). Всё в существующих файлах.

- [ ] **W0.1 Hang-case hard-teardown** (`HealthMonitor.cs`): при 2 фейлах probe —
  `Kill(entireProcessTree:true)` ПЕРВЫМ (wintun-адаптер умирает с процессом → ОС
  возвращает NIC-маршруты + DNS), потом backoff/restart. Post-kill assert: адаптер
  `Tun.InterfaceName` исчез из `GetIfTable2`; если zombie — явно снести маршруты
  (`DeleteIpForwardEntry2` P/Invoke или `netsh interface ipv4 delete route`).
- [ ] **W0.2 Cheap TCP-canary** (опц., сокращает detection latency): 2-3s TCP-проба
  через TUN, чтобы ловить hang быстрее probe-интервала.
- [ ] **W0.3 Scope Wave-39 DNS-lockdown** (`FirewallManager.cs:46-70`): в exclude-split
  профилях либо skip DNS-block-правил, либо временный disable в crash-окне (хуки
  `EnableBlockRules`/`DisableBlockRules` уже есть в HealthMonitor lifecycle). Сейчас они
  душат DNS excluded-приложений в down-окне — главный остаточный симптом crash-case.
- [ ] **W0.4 Flap dampening**: в restart-backoff НЕ пересоздавать TUN, пока health-probe
  предыдущей попытки не отработал → excluded видят один gap, не пять.
- **Acceptance W0:** exclude-профиль, убить `sing-box.exe` → excluded-приложение (напр.
  браузер/Discord) восстанавливает сеть+DNS за ~1-2s; hang-кейс (SIGSTOP-эмуляция) →
  восстановление в пределах probe-интервала, не бесконечно. Routed-приложения остаются
  fail-closed. Honesty: hang-window (до detection) W0 полностью не убирает — только W1.
- **Риск:** LOW (правки в existing lifecycle). **Оценка:** дни.

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
