# GOAL: W1.1-W1.4 — implement the Mullvad split-tunnel driver feature (production)

**Что:** превратить доказанный W1.0-спайк в production true-split под Windows (exclude-режим:
excluded-приложения уходят мимо VPN на физ-NIC и переживают смерть sing-box). Feasibility уже
доказана вживую (W1.0 GATE PASS) — это чистая production-инженерия, НЕ research.

**Читать ПЕРЕД стартом (authoritative):**
- Архитектура/как: [`plans/w1.1-architecture-and-plan-2026-07-04.md`](w1.1-architecture-and-plan-2026-07-04.md) (Fable) — API, event-pump, fail-open §3, pure-extraction §4, wiring §5, тесты §7.
- ABI/spec: [`plans/w1-driver-abi-reference-2026-07-03.md`](w1-driver-abi-reference-2026-07-03.md) — IOCTL/структуры/GUIDs/sublayers (пины).
- Why/scope/W1.0-outcome: [`plans/goal-w1-mullvad-split-driver-2026-07-03.md`](goal-w1-mullvad-split-driver-2026-07-03.md).
- Референс-агент (spike, throwaway, для портирования): scratch `w1-spike/spike/Program.cs` (весь P/Invoke + буферы + engage, ДОКАЗАН live); event-pump — `w1-spike/driver.rs` + `defs/events.h` (спайк pump СКИПНУЛ).

## КРАСНАЯ ЛИНИЯ — fail-open (инвариант на каждой фазе)
**Любой** сбой драйвера (сервис не ставится/не стартует, device не открывается, любой IOCTL/sublayer
падает, pump умер, ABI не совпал, чужой Mullvad-сервис) → **лог + тихий fallback на существующий
post-capture `process_name`→direct routing** (он ОСТАЁТСЯ в каждом сгенерированном конфиге как
defense-in-depth → fallback стоит ноль кода). **Никогда не бричим сеть.** Полный перечень 8 путей —
план §3.

## Scope (не расширять)
Чинит exclude + full-with-exceptions. **Include остаётся post-capture** (не трогаем). Caveats
драйвера (DNS via svchost / localhost-UDP / multicast / UWP) — показываем честно (W1.3), не «чиним».

## Файлы (fewest; всё в `VPNRouter.Core/Services/`, план §1.1)
- new `SplitTunnelDriverProtocol.cs` (**pure static** — IOCTL/GUIDs/буферы/event-parser/`SplitTunnelPolicy`; тестируемое ядро)
- new `SplitTunnelDriverManager.cs` (sealed: SCM, overlapped-handle, sublayers, engage-flow, pump, NetworkChange)
- new `SplitTunnelDriverInterop.cs` (`static Native` P/Invoke + `SafeDeviceHandle`)
- edits: `ProcessImagePath.cs` (+native-form + shared name→path), `VpnEngine.cs` (W1.2 hooks §5),
  `NetworkInterfaceDetector.cs` (+internet-NIC picker), `build.ps1` (W1.4). Все new: `#nullable enable`,
  `[SupportedOSPlatform("windows")]`, sealed, no magic numbers.

## Фазы (жёсткий порядок; каждая = `phase-task-launcher` 6-gate unit)

- [ ] **W1.1-P1 — `SplitTunnelDriverProtocol` + `Interop` + юнит-тесты** (~1д, LOW). Порт спайковых
  builders/P-Invoke в pure+static; golden-vector тесты буферов, DOS→NT path, event-parser, policy-
  decisions (план §4). **Acceptance:** build 0 err; unit-тесты зелёные в CI; ноль live/Windows-зависимостей.
- [ ] **W1.1-P2 — `SplitTunnelDriverManager`** (~1-1.5д, MED — единственный реально новый код). Sealed:
  SCM idempotent + collision-guard, overlapped-IOCTL обвязка (один exclusive handle, `SemaphoreSlim`),
  engage/disengage (Disengage=RESET, сервис не стопаем), sublayers create/delete, crash-sweep,
  fail-open по §3, `NetworkChange` self-subscribe. **Acceptance:** компилится; pure fail-open/engage
  decisions unit-покрыты; construct/Dispose чисто; ещё без live.
- [ ] **W1.1-P3 — event pump + LIVE acceptance** (~1д, MED). §2 bg-поток + классификация. **Acceptance
  (live на windows-brat, harness-гочи §7.2 ОБЯЗАТЕЛЬНЫ):** (a) engage → GET_STATE=ENGAGED → excluded
  curl=`83.97.108.34`, non-excluded=exit-IP → disengage → excluded ведёт себя как non-excluded; pump
  логирует START/STOP_SPLITTING на старт/смерть excluded-процесса. (b) fail-open прогон: битый .sys /
  BFE off / занятый handle → Engage=false, интернет живой, routed sing-box работает.
- [ ] **W1.2 — VpnEngine wiring + lifecycle-тесты + LIVE** (~1-1.5д). §5 хуки (engage после старта
  sing-box, disengage на Stop, IP-change re-register, uninstall). **Acceptance (live):** (c) connect
  exclude→engaged, disconnect→disengaged, смена excluded-списка→re-engage, выдернуть NIC→re-register в
  логе. (d) **W0.1-контракт:** `taskkill /f sing-box` → HealthMonitor recovery, а excluded curl-loop НЕ
  теряет НИ ОДНОГО запроса за всё окно. `bug-hunt` skill после этой фазы (kernel-interop+lifecycle).
- [ ] **W1.3 — UX + verify** (~1д, LOW). "True split" badge в exclude-режиме + caveats на виду. **Acceptance:**
  (e) MCP e2e — exclude Discord → голос активен → `kill sing-box.exe` → голос выживает → скриншоты PASS.
- [ ] **W1.4 — packaging** (~0.5-1д, LOW). Бандл 3 файлов + sha256-gate в `build.ps1` (паттерн sing-box-lx)
  + `packaging/windows/uninstall.ps1` + Mullvad-collision guard. **Acceptance:** (f) свежий ZIP на чистую
  VM → первый connect ставит сервис → uninstall чистит; collision-сценарий обработан.

## Product-решение (не блокирует старт; подтвердить к cut'у)
`ShouldEngage` по умолчанию: **auto-ON** (рекоменд. — фича и есть exclude-режим, fail-open доказан, +
yaml escape-hatch `true_split_driver: auto|off`) ИЛИ opt-in-тумблер (консервативно). План §9.

## Definition of Done (весь W1)
- [ ] exclude Discord → соединение идёт через физ-NIC (не exit-IP); `kill sing-box.exe` → Discord/voice
  НЕ прерывается (0 gap); routed-апы fail-closed; любой сбой драйвера → fallback, сеть НЕ забричена;
  include-режим без регрессии; все существующие suites зелёные.
- [ ] Отгружено `-rN` → CI green → post-ship-mcp-verify (rule #12) → `bug-hunt` clean → (к stable)
  live-update gate. Cut stable — по явной user-команде.

## Cross-refs
Plan §-ссылки выше · W0.1 pure-decision паттерн (`WedgeKillPolicy.cs`, `28bb4c0b`) · ship/cut skills.
Каждая фаза — brief `plans/w1.x-*-brief.md` по `phase-task-launcher references/brief-template.md`.
