# macOS VPN debug — Olga_K (2026-06-04)

## Триггер
User Olga_K (mac, user `lollabunny`) прислала логи (`Z:\olga_k`: singbox.log 849KB,
vpnrouter20260603.log, vpnrouter20260604.log). 4 жалобы:
1. Службы Apple идут в обход ВПН.
2. DNS-запросы мимо туннеля.
3. Что-то рвётся к `1.2.3.4` — ничего не находит, отваливается.
4. Рассинхрон тем — Mac в тёмной, приложение запустилось в светлой.

## Окружение (из логов)
- macOS, `/Applications/VPNRouter.app`, config `~/Library/Application Support/VPNRouter/`,
  sing-box `.../bin/sing-box`, TUN `utun99`, Clash API `127.0.0.1:9090`.
- `config_mode=subscribe`, адрес подписки удалён из репозитория, 5 серверов,
  active `Germany VLESS ~yc.hrustalny` (тоже Iceland тогглила). schema=5.
- Routing: тогглила **split** (98-процессный merged mega-profile
  `Discord_Privacy+Messengers+AI_Tools+Browsers+Streaming+Gaming+Work_Suite+
  Virtualization+Privacy_Shell`, 55 RoutingAppsInclude) ↔ **full-tunnel**
  (0603 log line 47: `Full-tunnel mode — ignoring ActiveProfile`).
- **Process-routing на macOS РАБОТАЕТ**: sing-box `router: found process path:
  …Chrome Helper / /usr/libexec/timed / CategoriesService.xpc` → proxy/direct.

## Root cause по симптомам

### #2 DNS-утечка — ЯДРО проблемы [HIGH]
**Доказательство:** в 849KB sing-box лога **НОЛЬ DNS-запросов**. Все соединения —
к УЖЕ резолвнутым raw-IP (52.92.32.58, 64.233.x, 3.5.69.62…). macOS mDNSResponder
резолвит через системный (en0 → ISP) DNS, МИМО TUN; sing-box видит только
resolved-IP коннекты.
- ConfigGenerator ЭМИТИТ глобальное правило `{Protocol=dns, Action=hijack-dns}`
  (ConfigGenerator.cs:1293) — то есть DNS-hijack сконфигурен, но на macOS он НЕ
  ловит запросы системного резолвера.
- Гипотеза: macOS mDNSResponder шлёт DNS на en0-резолверы напрямую; sing-box
  `auto_route` скорее всего авто-исключает upstream-DNS-IP (loop avoidance), и эти
  53-пакеты обходят utun99 → не хайджекаются. Итог: DNS течёт полностью (и split,
  и full), на DNS-слое ничего не туннелировано.
- **Это macOS DNS-hardening parity gap vs Windows** (`WindowsDnsHardening` есть,
  macOS-эквивалента нет — связано с task #131).
- НУЖНО: `current.json` (ground-truth) — посмотреть `dns.servers`, порядок
  `route.rules`, `tun.auto_route`/`strict_route`, исключён ли upstream-DNS-IP.

### #1 Apple-службы в обход + routed-app IP-split [MED]
- В **split** (98 процессов) Apple/системные службы (`timed`, `CategoriesService`,
  `mDNSResponder`…) НЕ в списке → DIRECT. Для split это by-design, НО (а) user
  ожидает Apple-службы в туннеле, (б) даже ROUTED Chrome идёт DIRECT для части
  Google-IP (64.233.x, 173.194.x), а proxy — для других (gstatic, 3.5.69.62). =
  непоследовательность. Подозрение: `bypass_russian_traffic` / geosite-ru / geoip
  / custom_rules мис-бакетит, либо порядок route-rules.
- НУЖНО `current.json` — увидеть geo/custom route.rules.
- Также: полнота full-tunnel на macOS — роутятся ли Apple-службы в full? (часть
  macOS-служб обходит даже full через систему — проверить в full-окне лога).

### #3 `1.2.3.4:443` [MED — downstream от #2]
- Подтверждено: **Google Chrome Helper** → `1.2.3.4:443`, `dial tcp: i/o timeout`
  (мёртвый endpoint), роутится непоследовательно (direct И proxy).
- `1.2.3.4` — bogus IP. Сильная гипотеза: следствие сломанного DNS — её
  системный/фильтрующий DNS возвращает `1.2.3.4` для (заблокированного?) домена
  (так делают AdGuard/NextDNS/parental-фильтры для блок-листа), Chrome долбится в
  мёртвый IP. Туннельный DNS (через proxy) такого не вернёт. **Чинится #2.**

### #4 Рассинхрон тем [LOW — отдельный UI-баг]
- Подтверждено: app стартует `theme` из конфига (дефолт light), НЕ следует
  системной macOS Dark-теме на старте (0603 lines 31-39 — потом ручной ToggleTheme).
- Root cause: нет «system/auto» темы, детектящей macOS appearance на старте
  (Avalonia PlatformSettings / NSAppearance). Сохранённый/дефолтный `theme: light`
  побеждает.
- Fix: детект OS-appearance на macOS + «follow system» дефолт, применять ДО показа
  окна.

## Нужно от Olga (2 файла + инфо)
1. `~/Library/Application Support/VPNRouter/config.yaml`
2. `~/Library/Application Support/VPNRouter/config/current.json`
```bash
cp ~/Library/Application\ Support/VPNRouter/config.yaml ~/Desktop/
cp ~/Library/Application\ Support/VPNRouter/config/current.json ~/Desktop/
scutil --dns | head -40 > ~/Desktop/scutil-dns.txt   # системный DNS state
```
+ версия VPNRouter (About), версия macOS, ожидает ли она Apple-службы в туннеле
(split vs full).

## Фазы дебага (делаем вместе)
- **Ф1 — подтвердить DNS-leak root cause** (нужен current.json): разобрать
  dns.servers + route.rules + auto_route + DNS-exclusion. Воспроизвести на mac
  build host (`slovn@192.168.0.246`) с её конфигом; `tcpdump -i en0 port 53` →
  доказать утечку; `scutil --dns`.
- **Ф2 — macOS DNS hardening (БОЛЬШОЙ фикс, version bump):** реализовать
  `MacDnsHardening` (parity с `WindowsDnsHardening`) — на connect выставлять
  системный DNS на TUN-резолвер (`networksetup -setdnsservers` / scutil),
  restore на stop; ИЛИ починить sing-box-конфиг чтобы 53 ловился. Verify: ноль
  port-53 на en0 (tcpdump), DNS-строки появляются в sing-box логе.
- **Ф3 — routing consistency (#1):** починить geo/custom-rule split для routed-
  апп; решить split-vs-full UX для Apple-служб; полнота macOS full-tunnel.
- **Ф4 — 1.2.3.4 (#3):** перепроверить после DNS-фикса; вероятно уйдёт.
- **Ф5 — тема (#4):** follow macOS system appearance на старте.

## Версия
Ф2 (macOS DNS hardening) — реальный фичевый gap → **bump до v2.41.0** оправдан
(новая фича/parity), либо узко — в v2.40.x. Тема + routing едут попутно.
Решение по версии — после Ф1 (увидим масштаб по current.json).
```
```
```
