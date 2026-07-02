# GOAL (Codex): AmneziaWG — реалтайм-игры (Dota/SDR) + DNS-корректность + AWG-гигиена — большой фикс-пасс

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

- [ ] **1A (P1) DNS-leak lockdown по умолчанию в full-tunnel** (finding B, OPEN-DEFECTS
  строка ~43). Живой AWG-тест: IP=Iceland ок, но DNS-панель browserleaks показала 3
  RU-резолвера (`195.2.238.4/…` Trytek LLC, 300/300) при `dns_leak_lockdown:false` —
  Windows smart multi-homed resolution шлёт :53 мимо туннеля. **Fix (минимум):**
  дефолт `App.DnsLeakLockdown=true` в full-tunnel (firewall port-53 backstop);
  **правильно (если время):** NRPT-правило / блок исходящего :53 на физ-NIC / DoH-
  редирект. Тест: browserleaks DNS-панель показывает ТОЛЬКО exit/vpn-dns.
  — `VPNRouter.Core/Models/AppConfig.cs` (default) + `FirewallManager`/DNS-hardening.
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

## Acceptance (комплексно)
- Dota на **AWG**: регионы показывают пинги (`Relays≥20 valid`), вход в матч + 10 мин без «connection lost».
- **DNS-leak** browserleaks: только exit/vpn-dns, ноль RU-резолверов.
- **Не сломано:** Roblox + браузинг на 1420; VLESS/Hy2 без регрессий; StrictDns действительно «весь DNS через VPN».
- Build+CI зелёные; lx-ядро с `with_awg,with_xhttp` в win.zip; 3 build-патча целы.

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
