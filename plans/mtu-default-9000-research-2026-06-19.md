# TUN MTU defaults (sing-box / clients) and VPNRouter's 1280 — research note (v2, post-review)

> Revised after independent review
> ([independent-review-server-health-mtu-2026-06-19.md](independent-review-server-health-mtu-2026-06-19.md)),
> which found two factual errors in v1 (9000-as-universal-default; MSS clamping).
> Claims below were re-checked against sing-box **1.13.13** source (the version
> VPNRouter bundles, `build.ps1:48`).

## Question

Почему пользователь видит **TUN MTU = 9000** у sing-box-клиентов, а VPNRouter ставит **1280**?

## Findings (corrected)

### sing-box default TUN MTU is **platform-dependent**, not universally 9000
В sing-box 1.13.13, если `mtu` не задан (`protocol/tun/inbound.go`):
- **Apple NetworkExtension → 4064**
- **Android → 9000**
- **прочие (Windows/Linux) → 65535**

Так что «дефолт 9000» — **только Android**. Почему юзер видит 9000 на десктопе:
**GUI-клиенты (v2rayN, Nekoray, GUI.for.SingBox) обычно сами проставляют 9000** в
генерируемом конфиге, а не полагаются на десктопный дефолт sing-box (65535). То
есть 9000 — выбор клиентов, а не sing-box-десктоп-дефолт.

### Почему вообще большой MTU
TUN — виртуальный интерфейс, не привязан к 1500: больше MTU → крупнее буферы в
юзерспейс → меньше пакетов/syscall'ов → выше throughput, ниже CPU.
- **Почему 9000, а не 65535 (на Android):** часть Android-устройств даёт `ENOBUFS`
  на 65535 → sing-box выбирает 9000 на Android. **Это Android-специфично, не
  обобщать на десктоп.**

### Как большой MTU уживается с реальным ~1500 (механизм — исправлено)
**НЕ MSS-clamping.** Для `stack = "system"` (а VPNRouter всегда эмитит
`Stack = "system"`, `ConfigGenerator.cs:1022`) sing-tun **терминирует локальный TCP
и заново оригинирует отдельный outbound-TCP** к прокси (`stack_system.go`,
`acceptLoop`/`listener.Accept`/`NewConnectionEx`). Поэтому on-wire пакеты
ресегментируются исходящим стеком — это **termination + re-origination**, а не
переписывание MSS в форвардящихся SYN.

### UDP/QUIC
- **GSO неприменим к VPNRouter.** Внутренний GSO sing-box включается только при
  Linux + `gvisor` + нет platform-interface + 0<mtu<49152. VPNRouter — `system`
  stack, плюс exposed `gso`-опция в доке помечена deprecated/не работает. Так что
  GSO **не объясняет** обработку больших UDP в текущем конфиге VPNRouter.
- **QUIC PMTU discovery (подтверждено):** quic-go (sagernet mod) сам ищет path MTU;
  слишком большой initial размер ломает handshake; <1200 невалидно; **дефолтный
  initial = 1280** (`params.go`). RFC 8899.

### VPNRouter MTU (подтверждено)
- default **1280** (`AppSettings.cs:1179`), передаётся в sing-box
  (`ConfigGenerator.cs:1015`), миграция прежнего дефолта **9000 → 1280**
  (`SettingsMigrator.cs:649-651`). Bundle 205004 = 1280; 214717 = кастом 1337.

### «1280 traverses any path» — НЕ абсолют (исправлено)
1280 = минимальный link-MTU IPv6 (RFC 8200 §5: линки с IPv6 обязаны его держать
или давать low-layer фрагментацию). Это **не** универсальная гарантия для любого
IPv4/инкапсулированного/кривого пути. Корректнее:
> 1280 — консервативное значение, существенно снижающее PMTU/фрагментацию-риск и
> совпадающее с минимальным MTU IPv6.

### Связь с EOF-инцидентом — НЕТ доказательств MTU-причины
Обе сессии шли на **1280/1337**, не 9000. EOF — ~100 мс после создания коннекта,
по множеству несвязанных целей; это **несовместимо** со сбоем, требующим крупного
HTTP/2/UDP-пейлоада и PMTU-blackhole. Положительных MTU-улик в bundle нет.

## Recommendation (assessment only)
Опц. perf-research **«поднять дефолт 1280 → 1420/1500»** — **unvalidated,
экспериментально**. Менять дефолт только после контролируемых тестов:
HTTP/1.1 + HTTP/2; крупные up/down; QUIC-able и TCP-only прокси; Win/mac/Linux/
Android; Ethernet/Wi-Fi/PPPoE/mobile/nested-VPN; пути со сломанным ICMP PMTU;
throughput+CPU+reliability на 1280/1420/1500. До этого — **держать 1280** как
production-дефолт, более высокие — advanced/experimental.

Также: код-комментарии про MTU (`AppSettings.cs:1170-1177`,
`SettingsMigrator.cs:635-645`) переоценивают механизм («9000-байтные HTTP/2 ушли
на провод as-is», «1280 проходит любой путь», «подтверждено») → переписать как
evidence-based operational rationale, а не доказанный packet-trace.

## Sources
- sing-box 1.13.13 tun defaults — https://github.com/SagerNet/sing-box/blob/v1.13.13/protocol/tun/inbound.go
- sing-tun system stack — https://github.com/SagerNet/sing-tun/blob/v0.8.10/stack_system.go
- sing-box TUN doc (gso deprecated) — https://sing-box.sagernet.org/configuration/inbound/tun/
- quic-go PMTUD / initial 1280 — local dep `quic-go@...sing-box-mod.4` (interface.go, internal/protocol/params.go); RFC 8899
- RFC 8200 §5 (IPv6 min MTU 1280) — https://www.rfc-editor.org/rfc/rfc8200.html#section-5

## Validation status
v1 claims **9000-universal-default** и **MSS-clamping** — **опровергнуты** ревью и
исправлены выше. Остальное (Android-ENOBUFS, QUIC-PMTUD, VPNRouter-1280, «MTU не
причина EOF») — подтверждено.
