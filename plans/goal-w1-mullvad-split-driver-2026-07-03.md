# GOAL: W1 — true OS-level split tunnel on Windows via Mullvad WFP driver

**Родитель:** [`plans/goal-true-split-tunnel-2026-07-03.md`](goal-true-split-tunnel-2026-07-03.md)
(Phase W1). **Research (rationale, читать перед стартом):**
[`plans/true-split-tunnel-research-2026-07-03.md`](true-split-tunnel-research-2026-07-03.md)
— §2.1 driver-контракт, §3 options-table (почему W1), §9 impl-sketch, §10 open-q,
§Sources (подписи проверены локально 2026-07-03). **W0.1 (fast-teardown) уже DONE**
(`28bb4c0b`) — W1 не отменяет W0, они складываются (driver не чинит DNS-через-мёртвый-TUN).

---

## Триггер / что чинит

Сегодня split — это `route.rules` по `process_name` ВНУТРИ sing-box TUN (post-capture):
excluded-приложение всё равно идёт TUN→sing-box→`direct`→NIC. Когда sing-box виснет/
падает — excluded вместе со всеми в чёрной дыре (W0.1 сокращает окно, но не убирает его:
пока идёт detect→kill→relaunch, excluded молчит). **W1 = настоящий OS-level split**:
excluded-потоки биндятся к физ-NIC на ALE_BIND в ядре, ДО того как пакет существует, и
маршрутизируются по собственному default-route NIC'а. Смерть TUN для них невидима **by
construction** — 0 gap, а не "секунды" как в W0.

**Как:** забандлить пребилд-драйвер Mullvad `win-split-tunnel` (WFP callout, KMDF) +
портировать user-mode агента на C#. Драйвер зрелый, фаззится funded-командой; **наш риск
— корректность агента + interop с нашим netsh-kill-switch / Wave-39 DNS-lockdown**, не сам
драйвер.

## Решающие факты (verified, research §2.1 + §Sources)

- **Драйвер:** [`mullvad/win-split-tunnel`](https://github.com/mullvad/win-split-tunnel),
  лицензия **GPL-3.0-or-later ИЛИ MPL-2.0** (dual, наш выбор). VPNRouter — GPL-3.0 → чисто.
- **Пребилд подписанный бинарь** в
  [`mullvad/mullvadvpn-app-binaries`](https://github.com/mullvad/mullvadvpn-app-binaries)
  `x86_64-pc-windows-msvc/split-tunnel/`: `.sys`+`.inf`+`.cat`(+`.pdb`). Подписи (локально
  2026-07-03): `.sys` = Mullvad VPN AB / DigiCert (до 2027-02-07); **`.cat` = Microsoft
  Windows Hardware Compatibility Publisher** (attestation-signed → грузится на prod
  Win10/11 x64 с Secure Boot, БЕЗ test-signing, БЕЗ покупки серта, БЕЗ Partner Center).
  Редистрибуция unmodified — intended use, подписи остаются валидны (модель как wintun.dll).
- **Контракт (IOCTL state-machine, README):** `STARTED` → `IOCTL_ST_INITIALIZE` →
  `IOCTL_ST_REGISTER_PROCESSES` (снапшот дерева процессов) → `IOCTL_ST_SET_CONFIGURATION`
  (пути excluded-образов) + `IOCTL_ST_REGISTER_IP_ADDRESSES` (tunnel IPv4/IPv6 + internet-NIC
  IPv4/IPv6) → `ENGAGED`. Далее драйвер сам трекает приход/уход процессов в ядре (+propagation
  на дочерние) и шлёт события назад **inverted-call**. Обязанности агента: следить за сменой
  IP интерфейсов и re-register; DOS-путь→device-путь; volume-arrival.
- **Reference-агент (портируем структуры/IOCTL-коды отсюда, dual-lic как драйвер):**
  `talpid-core/src/split_tunnel/windows/{driver,path_monitor,volume_monitor}.rs` в
  [`mullvadvpn-app`](https://github.com/mullvad/mullvadvpn-app).
- **Greenfield native-interop** (проверено — в репо НЕТ ни одного `DeviceIoControl` /
  `QueryDosDevice` / `CreateService` / `OpenSCManager`): весь слой SCM-service-control +
  device-IOCTL + NT-path + inverted-call пишем с нуля. Это основной объём и риск W1.

## ЖЕЛЕЗНЫЙ инвариант: fail-open (никогда не бричим сеть)

**Любой** сбой драйвера (сервис не ставится / device не открывается / IOCTL вернул ошибку /
inverted-call умер / версия .sys не матчит ABI) → **лог + тихий fallback на текущее
post-capture поведение** (sing-box `process_name` rules остаются как есть, как
defense-in-depth). Никогда не блокируем и не теряем трафик из-за драйвера. Это красная линия
на каждой фазе — не "потом добавим".

## Scope (честно, research §2.5)

- **Чинит:** exclude-режим + full-tunnel-with-exceptions (excluded-потоки → физ-NIC, переживают
  смерть sing-box, 0 gap).
- **НЕ чинит include-режим** ("route only listed"): у bind-redirect-драйверов это индустри-wide
  post-capture (инверсия — исключать безграничное большинство). Остаётся на post-capture +
  W0.1-mitigation. Настоящий include — только WinDivert-inverted (research §2.2b, XL) —
  отдельный someday-item по user-signal (open-q #5).
- **Caveats на виду (README limitations, → UX):** (1) DNS excluded-приложений идёт через
  `dnscache`/svchost → driver видит svchost, не приложение → пока туннель жив, DNS excluded
  всё равно в туннеле (тот же root cause что наш текущий per-app-DNS ловит только апы со своим
  UDP/53); (2) localhost-UDP к 127.0.0.1 у excluded ломается (bind-redirect от inaddr_any);
  (3) multicast-приём excluded может ломаться; (4) UWP/Store-апы исключить нельзя.

---

## Phase W1.0 — Phase-0 spike + GATE (2-3 дня, throwaway console) ⟵ ДЕЛАТЬ ПЕРВОЙ

Одноразовый консольный `.exe` (НЕ в Core, throwaway в `tools/spike/` или scratch) — доказать
что связка работает на нашей машине ДО того как вложим 2-4 недели в агента.

- [ ] Скачать pinned `.sys`+`.inf`+`.cat` из `mullvadvpn-app-binaries` (зафиксировать точный
  commit-SHA + sha256 каждого файла — это же станет pinned-версией для W1.4).
- [ ] Портировать из pinned `driver.rs` (тот же commit): IOCTL-коды (`CTL_CODE`), struct-layouts
  для `REGISTER_PROCESSES` / `SET_CONFIGURATION` / `REGISTER_IP_ADDRESSES`, state-enum.
- [ ] `sc create mullvad-split-tunnel type= kernel binPath= <sys>` (или `CreateService`
  P/Invoke) → start → открыть `\\.\MULLVADSPLITTUNNEL` (`CreateFile`) → прогнать IOCTL-цепочку
  с ХАРДКОДОМ: один excluded `notepad.exe` (NT-device-путь через `QueryDosDevice`), текущий
  NIC IPv4/IPv6 + TUN IPv4/IPv6 (руками из `ipconfig`).
- [ ] Inverted-call: поднять bg-поток с pending `DeviceIoControl`, убедиться что события
  прихода/ухода процесса прилетают.
- [ ] **Live-verify на windows-brat** (vmid 100) через `tools/testvm-control.ps1`
  (autonomous, scoped Proxmox token): (a) с запущенным sing-box VPN — трафик notepad идёт
  через NIC (не через exit-IP), routed-апы через туннель; (b) **kill sing-box.exe → notepad
  НЕ затронут вообще** (0 gap — вот весь смысл).

### GATE (go/no-go — закрывает open-q #1,#2)
- **PASS** (exclusion работает + переживает kill + permit'ы драйвера уживаются с нашим
  per-program netsh-block'ом и Wave-39 DNS-lockdown, research open-q #1; zapret/WinDivert на
  80/443 не конфликтует, open-q #2) → строим W1.1+.
- **FAIL** (напр. driver-permit НЕ пробивает MPSSVC-block и excluded всё равно режется, или
  ABI не матчит, или interop с zapret ломает потоки) → **СТОП, остаёмся на W0.1**, документируем
  почему, закрываем W1 как "not viable на нашей архитектуре". Spike-код выкидываем.

**Риск фазы:** это и есть де-риск всего W1. **Оценка:** 2-3 дня.

### Outcome W1.0 (2026-07-03) — PARTIAL: механика доказана, traffic-redirect gate ОТКРЫТ
Живой прогон на windows-brat (vmid 100). Спайк-код + ABI: memory-scratch (throwaway),
пины/структуры зафиксированы в [`w1-driver-abi-reference-2026-07-03.md`](w1-driver-abi-reference-2026-07-03.md).

**Доказано (главные unknowns закрыты):**
- **Драйвер грузится** через bare `CreateService(type=kernel)` + `StartService` (как в Mullvad
  `service.rs`) — подпись принята, **DSE-вопрос снят** (`.sys` несёт MS-attestation
  countersignature; каталог/PnP не нужны). Это был риск №1.
- **Полный IOCTL-протокол агента на C# работает** → `GET_STATE(STARTED)` → RESET → INITIALIZE →
  REGISTER_PROCESSES (реальный снапшот, NT-пути через `QueryFullProcessImageName(NATIVE)`) →
  REGISTER_IP_ADDRESSES → SET_CONFIGURATION → **ENGAGED(4)**. Все ручные буферы приняты.
- **WFP-sublayer prerequisite найден и решён**: `SET_CONFIGURATION`→`EnterEngagedState` требует
  чтобы baseline+dns sublayers УЖЕ существовали (иначе `FWP_E_SUBLAYER_NOT_FOUND`). Их создаёт
  Mullvad `winfw`; мы его не поставляем → **W1.1 сам создаёт 2 sublayer'а** (`FwpmSubLayerAdd0`,
  weights `0xFFFF`/`0xFFFE`). Спайк-`wfp-init` создал их, после чего engage прошёл.

**НЕ доказано — traffic-redirect (open-q #1), gate ОСТАЁТСЯ ОТКРЫТ:**
- Синтетический harness (sing-box full-tunnel `reject`-all + принудительный TUN
  `InterfaceMetric=1`) НЕ смог честно проверить перенаправление: при захвате дефолт-роута TUN'ом
  excluded-curl тоже не дошёл до WAN (exit 28 timeout; non-excluded — exit 6 DNS-fail; разница
  показывает что драйвер на excluded влияет, но connect ушёл в TUN). Причина —
  **Windows по умолчанию weak-host model**: bind excluded-сокета к IP физ-NIC НЕ форсит egress
  через NIC когда TUN выигрывает дефолт-роут. Драйвер у Mullvad-юзеров это преодолевает
  (connect-redirect/interface), значит МОЖЕТ — но с TUN'ом sing-box + нашим auto_route это
  **непроверено**. После смерти sing-box excluded мгновенно ожил (83.97.108.34), т.е. вне
  форсированного захвата путь excluded рабочий.

**Вывод / go-no-go:** механику интеграции де-рискнули (грузится, драйвится, sublayers решены —
большой прогресс), но САМ gate (реально ли excluded уходит мимо VPN при живом туннеле) требует
**теста на РЕАЛЬНОМ full-tunnel** (VPNRouter + подписка → exit-IP contrast), не на синтетике.
Пока open-q #1 открыт — **НЕ коммитим 2-4 недели W1.1**; следующий шаг = один сфокусированный
real-VPN traffic-тест (excluded=реальный IP, non-excluded=exit-IP, + kill-survival). Weak-host
наблюдение — реальный флаг риска для этого теста, не приговор.

---

## Phase W1.1 — `SplitTunnelDriverManager.cs` (Core, агент)

- [ ] Новый sealed-класс в `VPNRouter.Core/Services/` (`[SupportedOSPlatform("windows")]`,
  `#nullable enable`). SCM: install/start/stop/uninstall сервиса (idempotent — reuse если уже
  стоит, см. open-q #4). Device-handle: open/close `\\.\MULLVADSPLITTUNNEL`, `SafeFileHandle`.
- [ ] State-machine `Initialize → RegisterProcesses → SetConfiguration + RegisterIpAddresses →
  Engaged`, + `Disengage`. IOCTL-обёртки поверх `DeviceIoControl` (P/Invoke).
- [ ] `RegisterProcesses`: снапшот дерева (`CreateToolhelp32Snapshot`/`Process32First/Next`) +
  image-пути. `SetConfiguration`: excluded-имена → полные пути (reuse
  [`IProcessScanner`](../VPNRouter.Core/Interfaces/IProcessScanner.cs) / `where`-логика) →
  NT-device-пути (`QueryDosDevice`). `RegisterIpAddresses`: из
  [`NetworkInterfaceDetector`](../VPNRouter.Core/Services/NetworkInterfaceDetector.cs)
  (tunnel + "internet interface" — на multi-NIC агент выбирает правильный).
- [ ] Inverted-call event-pump на bg-треде (`CancellationToken`, `async`), обрабатывает
  process-arrival/departure. **Fail-open**: любой throw → лог + `Disengage` + fallback.
- **Тесты:** IOCTL-buffer-маршалинг (struct→byte[]→struct round-trip) — pure, без драйвера;
  NT-path-конвертация; idempotent-install логика (мокнутый SCM-порог). Живой драйвер — только
  на windows-brat, не в CI.
- **Acceptance:** класс поднимает/опускает драйвер, engage/disengage чисто, при принудительном
  IOCTL-fail → fallback без сетевого сбоя. **Риск:** MED (greenfield interop). **Оценка:** ~1 нед.

## Phase W1.2 — lifecycle-wiring (`VpnEngine`)

- [ ] `VpnEngine.StartAsync`/`Apply`/`Stop`: engage driver ПОСЛЕ старта sing-box (excluded уже
  забиндятся к NIC), disengage в `Stop`. Только когда режим = exclude / full-with-exceptions И
  `SingBoxFeatures`/платформа = Windows-desktop; иначе no-op (include / Android / Linux / mac).
- [ ] IP-change (reuse существующий network-change hook, тот же что HealthMonitor слушает) →
  `RegisterIpAddresses` заново.
- [ ] Uninstall драйвера при деинсталле app (installer-скрипт). W0.1 Wave-39 scoping применяется:
  в exclude-split relax'им DNS-lockdown чтоб excluded имели DNS в down-window (research §6).
- **Тесты:** engage/disengage вызываются на правильных переходах (мок `SplitTunnelDriverManager`);
  no-op в include/не-Windows. **Acceptance:** реальный connect/disconnect на windows-brat — driver
  engage'ится/disengage'ится, IP-change re-register'ится. **Риск:** MED. **Оценка:** ~2-3 дня.

## Phase W1.3 — UX + docs

- [ ] "True split" бейдж в exclude-режиме (когда driver engaged). Caveats на виду (DNS /
  localhost-UDP / multicast / UWP-не-исключается — из Scope выше).
- [ ] **MCP-verify end-to-end** (rule #13 — вести до конечного user-сценария, не "бейдж
  отрендерился"): exclude Discord → голосовой звонок активен → `kill sing-box.exe` → **Discord
  voice выживает без обрыва** → скриншот + лог. Плюс негативный: routed-апы fail-closed при том
  же kill.
- **Acceptance:** бейдж + caveats видны; MCP e2e PASS. **Риск:** LOW. **Оценка:** ~2 дня.

## Phase W1.4 — packaging

- [ ] `build.ps1`: забандлить 3 файла (`.sys`/`.inf`/`.cat`, +~110 KB) по паттерну sing-box-lx
  bundling (`-SingBoxPath` + `.sha256`-sidecar, [`build.ps1:309`](../build.ps1)). Версия драйвера
  **pinned + checksummed** (тот же commit что в W1.0/W1.1).
- [ ] Service-collision guard (open-q #4): если стоит настоящий Mullvad VPN (сервис
  `mullvad-split-tunnel` уже есть) → reuse ИЛИ refuse с внятным сообщением (двое драйвят один
  инстанс = undefined).
- **Acceptance:** свежая установка несёт драйвер, ставит сервис, чистый uninstall. **Риск:** LOW.
  **Оценка:** ~1-2 дня.

---

## Acceptance W1 (весь)
- [ ] exclude Discord → соединение идёт через физ-NIC (не exit-IP); `kill sing-box.exe` →
  Discord/voice НЕ прерывается **вообще** (0 gap, не "секунды").
- [ ] routed-приложения при том же kill — fail-closed (netsh-block держит).
- [ ] любой сбой драйвера → fallback на post-capture, сеть НЕ забричена.
- [ ] include-режим по-прежнему работает (post-capture, без регрессии).
- [ ] существующие suites зелёные; паритет kill-switch/Wave-39 не сломан.

## Open questions (закрыть в W1.0 gate / по ходу) — research §10
1. Пробивают ли permit'ы драйвера netsh(MPSSVC)-block + Wave-39 DNS-lockdown? **← W1.0 gate.**
2. Driver vs zapret/WinDivert на 80/443 тех же потоков — ожидаемо benign (payload-mangling, не
   routing), 1 тест. **← W1.0 gate.**
3. IOCTL ABI-стабильность между релизами Mullvad — pin точный commit `driver.rs` под версию
   `.sys`, re-verify на бампах (W1.1/W1.4).
4. Service-collision с настоящим Mullvad (W1.4).
5. Include-mode long game (WinDivert-inverted XL) — сначала собрать user-signal.
6. Mullvad-драйвер на Windows-on-ARM — бинари есть upstream (aarch64), untested, вне scope до ARM64.

## Что НЕ делать (research §2.6, §8 — отклонено)
Свой/форкнутый драйвер (любой байт-change инвалидирует MS-подпись; EV-cert $280-580/год +
Partner Center attestation per-build), WinDivert как VPN data-path (signing provenance — наш
`.sys` ре-подписан истёкшим в 2023 CN-company сертом + XL rearchitecture),
ProxiFyre/WinpkFilter (commercial redistribution license), ждать sing-box (Windows per-app
архитектурно вне upstream-scope).

## Оценка / риск
**2-4 недели** (W1.0 spike 2-3д → W1.1 агент ~1нед → W1.2 wiring 2-3д → W1.3 UX 2д → W1.4
packaging 1-2д). **Риск MED** — драйвер зрелый, риск в greenfield-агенте + interop. W1.0 gate
отсекает невиабельность за 2-3 дня. Целевой релиз — будущий минор (текущий stable v2.45.0).

## Связь
- Родитель: `plans/goal-true-split-tunnel-2026-07-03.md` · Research:
  `plans/true-split-tunnel-research-2026-07-03.md`
- Затрагивает: новый `SplitTunnelDriverManager.cs`; `VpnEngine.cs` (wiring); `build.ps1`
  (bundling); `NetworkInterfaceDetector.cs` + `IProcessScanner` (reuse); W0.1 Wave-39 scoping.
- Каждая фаза >30 строк / новая абстракция → через `phase-task-launcher` (6-gate lifecycle).
