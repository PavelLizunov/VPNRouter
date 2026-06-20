# ChatGPT/Cloudflare — WARP-outbound on the exit server (server-side, backlog)

Status: **backlog / idea** (server-side, not client). Created 2026-06-20. Revisit later.

## Контекст (из live-теста + deep-research 2026-06-20)

Live browser-тест на RU-VM показал: ChatGPT через VPN виснет на Cloudflare
**«Verifying…»**. Research подтвердил первоисточниками: причина — **репутация
exit-IP** (датацентровый/хостинговый ASN: Hetzner/OVH/DO/AWS), НЕ TLS-фингерпринт.
uTLS/Reality на прокси-слое транзитному браузеру не помогает (браузер делает своё
end-to-end TLS, Cloudflare видит его настоящий Chrome-фингерпринт — он проходит;
блочит именно IP). См. `plans/` deep-research отчёт в истории + browser-test
скриншоты (`C:/tmp/chatgpt.png` = Cloudflare challenge).

RU-сквиз: `direct` (RU residential) проходит Cloudflare, но светит RU-IP перед
OpenAI (гео); `proxy` (датацентр) — не-RU, но Cloudflare челленджит. Нужен **не-RU
резидентный/чистый** выход, которого у VPS нет.

## Идея (предложил user)

Пустить **исходящий трафик exit-VPS через Cloudflare WARP** для openai/chatgpt:
`браузер → VLESS/Reality → наш VPS → WARP(wgcf) → ChatGPT`. Egress становится
**WARP-IP Cloudflare** → (а) часто проходит Cloudflare там, где сырой VPS-IP
челленджится, (б) не-RU (снимает гео OpenAI). Закрывает оба блока одним ходом —
самый цитируемый рычаг именно под «ChatGPT через VPN» (research: `vless + WARP`
гайды, репо `X-UI-WARP-GPT-Bypass`).

## Где это живёт

**Сервер, не клиент.** На exit-VPS: `wgcf` генерит WireGuard-конфиг WARP →
sing-box на сервере роутит `domain_suffix: openai.com/chatgpt.com/oaistatic.com/
oaiusercontent.com → outbound warp` (остальное — прямой выход сервера). Клиент
VPNRouter не меняется — подключается как обычно.

## Лимиты WARP (важно учесть)

- **Данные:** free WARP — без жёсткого капа (источники конфликтуют 10ГБ vs unlim;
  Cloudflare явных капов не публикует). Не блокер.
- **Скорость:** free депроритизируется → может тормозить + это +1 хоп. Главный
  практический минус. WARP+ (Argo, в составе Zero Trust) быстрее.
- **Пул IP:** общий ограниченный → WARP-IP **сам со временем может зафлагаться** →
  не вечный фикс, «часто помогает».
- **ToS:** WARP — потребительский клиент; серверный релей — серая зона (за абуз
  могут дерегнуть устройство). Для лёгкого личного — обычно ок.

## Возможный объём работ (когда вернёмся)

1. Гайд/пресет «WARP-outbound для OpenAI на exit-сервере» (wgcf + sample серверный
   sing-box конфиг с `openai→warp` outbound).
2. Опц.: пометка в VPNRouter, какие серверы «ChatGPT-friendly» (прошли Cloudflare).
3. НЕ менять `samples/rules/*` на `openai→direct` — для RU это осмысленно через
   proxy (гео-обход); проблема в IP-репутации, не в направлении роутинга.

## Связь
- Browser-test + deep-research (Cloudflare = IP-reputation, fingerprint-трюки = миф).
- `server-health-failover-backlog-2026-06-19.md` — отдельная тема (надёжность нод),
  не путать: ChatGPT-проблема = exit-IP, а не EOF.
