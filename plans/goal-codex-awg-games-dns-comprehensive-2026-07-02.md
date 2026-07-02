# GOAL (Codex): AmneziaWG — реалтайм-игры (Dota/SDR) + DNS-корректность + AWG-гигиена — большой фикс-пасс

> **STATUS 2026-07-02 — AUTONOMOUS PHASE CLOSED (по команде user'а):** автономный
> фикс-пасс завершён и отгружен в **v2.45.0-r11**; живая приёмка (Dota + browserleaks)
> честно остаётся за тестером — см. секцию «Внешняя приёмка» в конце (НЕ фабрикуется,
> НЕ часть автономного gate). Разбор ниже:
>
> **STATUS 2026-07-02 (v2.45.0-r11):** DONE — Фаза 0 (MTU endpoint 1420 + r10 comment,
> `b4116041`); **1E** AWG TUN=1420 (`bbcd500d` — fixed the incomplete Phase-0 min-clamp);
> **1B** game-DNS-off-proxy band-aid deleted (`bbcd500d`); **1C** already satisfied by the
> r8 `proxyIsUdpNative` threading; **2A** WSAENOBUFS send-retry in lx-core (`a1a8997d` —
> rebuilt sing-box-lx, handshake-send smoke passes, WSAEFAULT/H4 patches intact).
> DEFERRED (with reason): **1A** DNS-leak lockdown — a blunt default flip would break
> split-tunnel (direct apps need physical-NIC DNS); needs a full-tunnel-scoped fix or NRPT,
> its own live-verify. **1D/1F** LeakProtection-AWG-content / DeepVerify-AWG — marginal
> defense-in-depth (1D already mitigated by parser required-field validation). **3A**
> game-UDP→Hy2 auto-route — needs a Hy2 sibling alongside AWG in one config (infra).
> ACCEPTANCE (Dota-on-AWG works, no DNS leak, Roblox intact) is the tester's live call on r11.


Один комплексный проход по всему AWG-кластеру: подтверждённый root-cause Dota
(WSAENOBUFS), DNS-утечка, path-MTU робастность, и ripe AWG-гигиена из
`OPEN-DEFECTS.md`. **Фазы упорядочены по риску** (C#-безопасное → lx-ядро →
продуктовая фича); каждая проверяема отдельно, это НЕ монолит.

## Контекст (не гадать — всё установлено)
- **Root-cause Dota/SDR подтверждён живьём (2026-07-02):** консоль Dota на AWG
  показала `RelayNetworkStatus: config=OK anyrelay=Failed` «Unable to communicate
  with ANY of 28» = чистый UDP-класс, **НЕ DNS**; WSAENOBUFS сыпет ровно под
  burst; **на Hy2 работает**. Это **H1** — single-socket choke AWG. Полный разбор:
  `plans/sdr-research-realtime-games-nat-2026-07-02.md`,
  `plans/mtu-fragmentation-robustness-2026-07-02.md`,
  `plans/goal-sdr-games-action-plan-2026-07-02.md`. Память `[[sdr-fullcone-nat]]`.
- **Исходники форка читаны** (sing-tun v0.8.10 + `Leadaxe/wireguard-go-awg2-lx@0c0c10b`
  в `C:\Users\x3d_mutant\AppData\Local\Temp\vpnrouter-singbox-lx\sing-box-lx`): нет
  MSS-clamp, system-стек дропает фрагменты (`stack_system.go:571-575`), нет PMTUD.
- **НЕ трогать:** 3 build-патча lx (WSAEFAULT×2 + H4-gate, `awg-windows-lx-patches`);
  BypassRussianTraffic; не credit'ить r10/EIN; не route-исключать Steam (TSPU душит
  direct); не добавлять новый full-cone.

## Фаза 0 — ГОТОВО (commit b4116041)
- [x] MTU `AwgEndpointMtu` 1280→**1420** (откат r8-регресса; SDR шлёт 1328B без PMTUD) + TUN=min(user,1420).
- [x] Комментарий r10 EIN переписан на правду (no-op); OPEN-DEFECTS примирён.

## Фаза 1 — Core/config-gen фиксы (C#, низкий риск, БЕЗ пересборки ядра)

- [ ] **1A (P1) DNS-leak — DIAGNOSTIC-FIRST, НЕ слепой firewall auto-arm** (finding B).
  Живой AWG-тест: IP=Iceland ок, но browserleaks показал 3 RU-резолвера
  (`195.2.238.4/…` Trytek LLC) при `dns_leak_lockdown:false`.
  **КРИТИЧНО (investigation 2026-07-02):** registry SMHNR-disable (+ParallelAAAA
  +TUN-metric) идёт **безусловно** на каждом connect (`WindowsDnsHardening.Apply:103`,
  НЕ gated на DnsLeakLockdown). Т.е. тестер утёк RU-резолверами **несмотря** на уже
  применённый хирургический SMHNR-фикс → значит либо registry не взлетел на его машине,
  либо механизм утечки НЕ SMHNR. **Единственный оставшийся рычаг — firewall :53-блок**,
  но он (a) override'ит документированное BR-10-решение default-off (ломает full-tunnel
  LAN-DNS-proxy юзеров: dnscrypt/AdGuard Home на sibling-NIC, private-IP резолвер идёт
  direct даже в full-tunnel), (b) НЕ подтверждён что чинит ЭТУ утечку (SMHNR-disable не
  починил). **Слепой auto-arm = blind fix неизвестного механизма + override продукт-
  решения — методология запрещает.** ПРАВИЛЬНЫЙ next step: **tester diagnostic** —
  включить СУЩЕСТВУЮЩИЙ тумблер (Settings → Leak Protection → DNS Leak Lockdown),
  переподключить AWG full-tunnel, re-run browserleaks. Если RU-резолверы исчезли →
  firewall-блок работает → ТОГДА scoped auto-arm (с BR-10 caveat: exempt private-IP :53).
  Если НЕ исчезли → механизм другой (NRPT/DoH-bootstrap/app-direct), нужен свежий diag.
  Только ПОСЛЕ этого — код. (Ниже — прежний спек auto-arm helper'а, валиден лишь если
  diagnostic подтвердит firewall-блок как фикс.) Фича `DnsLeakLockdown`
  УЖЕ есть (Wave-39): TUN-gated arm/lift через `WindowsDnsHardening`, allow-rule на
  TUN-CIDR (`Tun.Ipv4Address` = `172.19.0.1/30`, populated для AWG → tunnel-DNS выживает),
  fail-open на outage. Дефолт `false`. **Precise fix (r12, own live-verify — НЕ бандлить
  в r11 Dota-кандидат: firewall :53-блок при мисфайре замаскирует Dota-тест):**
  1. `AppConfig.EffectiveDnsLeakLockdown` computed: `DnsLeakLockdown || (RoutingMode=="full"
     && !isDnsTunnel)`. BR в full-tunnel — no-op (AppConfig:43 doc), НЕ исключать по нему.
  2. Скоуп СТРОГО `"full"` (не `"exclude"` — там excluded-apps идут direct, нужен физ-:53;
     не `"split"`). `RoutingMode.Equals("full", OrdinalIgnoreCase)` — идиома ConfigGenerator:939.
  3. **Исключить dns-tunnel/slipstream** (VpnEngine:1277 — эмердженси-транспорту нужен
     off-tunnel DNS до резолверов; lockdown его убьёт). Сигнал `isDnsTunnel` сейчас НЕ
     доступен в `WindowsDnsHardening` — пробросить через settings или arm-context.
  4. Consumers на effective: `WindowsDnsHardening.ReconcileLockdownForHealth:184` (arm)
     + `HealthMonitor.cs:505` gate (иначе fail-open lift не re-arm'ится → на outage
     firewall держит :53-блок → юзер теряет интернет). Mac path (VpnEngine:357) —
     оставить raw (opt-in, отдельная платформа/sudoers).
  5. Unit-тесты: truth-table (full→arm, split→no, exclude→no, full+dns-tunnel→no,
     explicit-true→always). Live: browserleaks ноль RU-резолверов + браузинг цел +
     эмердженси-канал (если testable) не сломан.
  — `AppConfig.cs` (helper) + `WindowsDnsHardening.cs:184` + `HealthMonitor.cs:505`.
- [ ] **1B (P2) game-DNS-off-proxy ломает StrictDns** (OPEN-DEFECTS строка ~34).
  `ResolveGameDnsOffProxy` заворачивает roblox/rbxcdn/steam* → local-dns даже при
  `StrictDns=true`, ломая «весь DNS через VPN». Теперь, когда DNS-корень починен
  (plain-UDP) и Dota-провал доказанно НЕ DNS — **этот костыль можно либо удалить
  целиком** (RealtimeGameDnsSuffixes + ResolveGameDnsOffProxy + ветка + тумблер +
  GameDnsOffProxyTests), **либо** как минимум загардить `!strictDns`. Предпочтительно
  удалить (меньше поверхности, единый DNS-путь). — `ConfigGenerator.cs` BuildDns.
- [ ] **1C (P2) QUIC-reject keying** (OPEN-DEFECTS строка ~37). Сейчас QUIC-reject
  подавляется по `endpoints.Count>0`, а не «активный proxy UDP-native». Работает
  только потому что AWG — единственный тип endpoint сегодня. Перевести на явный
  `proxyIsUdpNative`. — `ConfigGenerator.cs:140`.
- [ ] **1D (P2) LeakProtection AWG-полнота** (OPEN-DEFECTS строки ~33, ~29): (a)
  валидировать содержимое AWG-endpoint (пустой private_key / нет peers / пустой
  address проходят локальную валидацию, падают в sing-box); (b)
  `ValidateOutboundServersScopeAware` не кросс-чекает AWG peer endpoint IP (итерирует
  только `config.Outbounds`; AWG-egress живёт в `config.Endpoints[].Peers[]`).
  Defense-in-depth. — `LeakProtection.cs:284,527`.
- [ ] **1E (P2) path-MTU настройка/гайд** (`mtu-fragmentation-robustness-…`). 1420
  безопасно только при underlay=1500; PPPoE/мобилка/nested → внешний WG-пакет велик,
  а фрагментации-fallback НЕТ (system-стек дропает фрагменты). **Fix:** оставить
  пользовательскую MTU-настройку с гайдом «если игры/крупные загрузки рвутся — снизь
  MTU (мин 1332)»; опц. probe path-MTU при коннекте. Тест: `ping -f -l 1372` через туннель.
- [ ] **(опц.) 1F (P2) DeepVerify AWG/xhttp** (OPEN-DEFECTS строка ~35): нет ветки
  AWG-endpoint и xhttp → ложно фейлит рабочие AWG/XHTTP. Реализовать паритет или
  вернуть явный unsupported. — `VlessDeepVerifier.cs`.

## Фаза 2 — lx-ядро: WSAENOBUFS retry (Go, РИСК, ПЕРЕСБОРКА) — корень Dota-на-AWG

- [ ] **2A (P1, CONFIRMED) retry-on-ENOBUFS в `send()`.** Точка: `conn/bind_std.go`
  Windows-цикл (472-477) — сейчас на любой ошибке `WriteMsgUDP` делает `break` (дроп
  всего батча). ENOBUFS транзиентный → ретрай той же датаграммы с микро-бэкоффом:
  ```go
  oob := msg.OOB; if len(oob) == 0 { oob = nil }
  for a := 0; a < 8; a++ {
      _, _, err = conn.WriteMsgUDP(msg.Buffers[0], oob, msg.Addr.(*net.UDPAddr))
      if err == nil || !errors.Is(err, syscall.Errno(10055)) { break } // 10055=WSAENOBUFS
      time.Sleep(time.Duration(60*(a+1)) * time.Microsecond)
  }
  if err != nil { break }
  ```
  Реализация: расширить `$sendNew` в `tools/build-singbox-lx.ps1` (4-й патч, тот же
  файл что WSAEFAULT-патчи — **не сломать** `$allocNew`/`$clearNew`); добавить импорты
  `errors`,`time` (проверить, есть ли уже; если нет — патч импортов). Пересобрать
  `publish/sing-box-lx.exe`, прогнать runtime-smoke скрипта. **Опц.** поднять SO_SNDBUF
  (сейчас 7MB, `conn/controlfns_windows.go`).
  **Верификация:** компиляция + smoke локально; финал — тест тестера (Dota на AWG:
  `Relays: N valid`, WSAENOBUFS не сыпет / сыпет но пробы доходят).

## Фаза 3 — продуктовая фича: авто-роут game-UDP через Hy2 (config-gen)

- [ ] **3A (P2) на AWG-профиле в full-tunnel гнать game-UDP через Hy2-сиблинг** — как
  уже делается `proxy-udp` для наивных VLESS. Hy2 несёт burst дёшево (sessionID/один
  QUIC) И MTU-робастен (DPLPMTUD + UDP-over-QUIC frag). **Зависимость:** нужен Hy2-сервер
  рядом с AWG в одном конфиге (сейчас Hy2 — отдельный профиль); если инфра-предпосылки
  нет — оставить как «использовать Hy2-профиль для игр» (2C уже чинит AWG в корне).
  — `ConfigGenerator.cs` route + outbound.

## Фаза 4 — тесты + верификация + ship

- [ ] Юнит-тесты: 1B (game-DNS удалён/guarded), 1C (QUIC-reject keying), 1D
  (LeakProtection AWG content), 1E (MTU настройка). `sing-box-lx check` на AWG-конфиге.
- [ ] Регрессия: полный `dotnet test` зелёный; `AwgDnsAndMtuTests` + `VpnDnsBootstrapTests`.
- [ ] Ship `-r11` (build.ps1 `-SingBoxPath publish/sing-box-lx.exe` — не забыть lx-ядро!)
  → 14 assets → тестер: Dota на AWG (`Relays≥20` + 10-мин матч без обрыва) + browserleaks
  DNS (ноль RU-резолверов) + Roblox/браузинг не сломаны на 1420.
- [ ] **Опц. дожать вилку:** tcpdump на exit (handoff 2) формально закрывает H2, но
  WSAENOBUFS-свидетельство уже достаточно для Фазы 2.

## Автономный gate приёмки — ВСЕ MET на r11 (2026-07-02) — ЦИКЛ ЗАКРЫТ

Это единственный gate, достижимый автономным агентом; все пункты зелёные:
- [x] Build+CI зелёные (`a1a8997d` check-runs 0 failures); 14 desktop assets; win.zip URL 200.
- [x] lx-ядро с `with_awg,with_xhttp` в win.zip; 4 build-патча целы (WSAEFAULT×2 + H4 + WSAENOBUFS), handshake-send smoke прошёл.
- [x] r11 binary integrity: `VPNRouter.CLI.exe doctor` → `Version: 2.45.0-r11`.
- [x] AWG-конфиг (1420 MTU + plain-UDP DNS, game-DNS-костыль удалён) проходит РЕАЛЬНЫЙ `sing-box-lx check` (`AwgDnsAndMtuTests` + `VpnDnsBootstrapTests` 10/10).
- [x] StrictDns больше не обходится game-DNS-костылём (ветка + `GameDnsOffProxyTests` удалены; `AwgDnsAndMtuTests` подтверждает единый DNS-путь).
- [x] Install/deploy-path подтверждён на живой тест-VM (r11 zip с GitHub → extract поверх install → `doctor` = 2.45.0-r11).

**Автономная фаза закрыта по команде user'а (2026-07-02).** Весь код отгружён в r11.

## Внешняя приёмка тестером — ВНЕ автономного scope (pending, НЕ фабрикуется)

Поведенческие критерии; закрываются ТОЛЬКО живым тестом из RU через AWG — недостижимы
для автономного агента (нет игры, нет RU-пути, нет AWG-exit creds). Честно остаются pending:
- [ ] **A. Dota на AWG**: регионы показывают пинги (`Relays≥20 valid`), матч 10 мин без «connection lost». ← корневой WSAENOBUFS-эффект подтверждается ТОЛЬКО живым burst'ом.
- [ ] **B. DNS-leak** browserleaks: только exit/vpn-dns, ноль RU-резолверов. ← сначала diagnostic (включить СУЩЕСТВУЮЩИЙ тумблер DnsLeakLockdown → browserleaks), потом код 1A по результату (см. Фаза-1 1A).
- [ ] **Не сломано:** Roblox + браузинг на 1420; VLESS/Hy2 без регрессий. ← Roblox уже работал на 1280; 1420 подтвердить живьём.

Когда будут A/B от тестера → либо stable-cut (по команде user'а), либо точечный DNS-фикс по данным B.

## Порядок / оценка
| Фаза | Риск | Пересборка ядра | Оценка |
|---|---|---|---|
| 0 | — | нет | ГОТОВО |
| 1A DNS-lockdown | low | нет | ключевой P1, отдельно тестируем |
| 1B game-DNS cleanup | low | нет | тривиально + тесты |
| 1C/1D/1E/1F | low | нет | гигиена, батчем |
| 2A WSAENOBUFS | **med-high** | **ДА** | корень Dota; локально не проверить, финал — тестер |
| 3A Hy2-route | med | нет | зависит от Hy2-инфры; опц. |

**Рекомендованный минимум для рабочей Dota-на-AWG:** Фаза 0 (done) + **2A** (retry) +
1A (DNS-lockdown, чтоб не отравлять) + 1E (MTU-гайд). Остальное (1B/1C/1D/1F, 3A) —
гигиена/фича в том же ship'е, раз уж «максимум за раз».
