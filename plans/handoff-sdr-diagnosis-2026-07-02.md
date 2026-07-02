# Хендофы: диагностика Dota/SDR (Фаза 1 мастер-плана)

Два независимых хендофа. Оба нужны, чтобы схлопнуть вилку H0/H1/H2. Лучше снимать
**одновременно** (тестер играет — владелец VPS в этот момент пишет tcpdump).

---

## ХЕНДОФ 1 — тестеру (Dota-консоль ×3 транспорта, ~15 мин, без кода)

Цель: получить строку `[SteamNetSockets] SDR RelayNetworkStatus` и «Relays: N valid»
на КАЖДОМ из трёх транспортов — впервые докажем, одинаково ли падает (сейчас это
только на словах).

### Разовая настройка
1. Steam → правой по **Dota 2** → **Свойства** → **Параметры запуска** → вписать:
   ```
   -console -condebug
   ```
   `-condebug` пишет консоль в файл
   `...\Steam\steamapps\common\dota 2 beta\game\dota\console.log` (дописывается —
   между транспортами будем переименовывать).

### Прогон (повторить ТРИЖДЫ: AWG, потом VLESS, потом Hysteria2)
Для каждого транспорта:
1. В VPNRouter подключись этим транспортом (full-tunnel, версия должна быть r10 —
   проверь в «О программе»).
2. Запусти Dota, зайди в аккаунт.
3. Открой **Играть → Найти игру** — там список регионов с «Задержка». Подожди ~60 сек,
   чтобы прошёл замер пингов.
4. Открой консоль (клавиша `` ` `` или `~`), впиши и Enter:
   ```
   sdr_dumprelaystatus
   status
   ```
   (если `sdr_dumprelaystatus` не найдена — не страшно, статус и так пишется в лог).
5. Выйди из Dota полностью (чтобы console.log дописался).
6. Переименуй `console.log` → `console-awg.log` (для след. транспорта — `console-vless.log`,
   `console-hy2.log`), иначе логи склеятся.

### Только на прогоне AWG — параллельно
Пока Dota меряет пинги, в PowerShell:
```powershell
Select-String -Pattern "wsasendmsg|WSAENOBUFS|failed to send" `
  "C:\ProgramData\VPNRouter\logs\singbox*.log" | Select-Object -Last 30
```
Если сыпет `wsasendmsg`/`WSAENOBUFS` в минуту теста — это **сразу подтверждает H1-AWG**
(burst душит физический сокет).

### Что прислать
- 3 файла: `console-awg.log`, `console-vless.log`, `console-hy2.log`.
- Вывод PowerShell-грепа с AWG-прогона.
- На что смотреть самому (findstr по каждому логу):
  ```
  findstr /C:"RelayNetworkStatus" /C:"Relays:" /C:"initial_ping_timeout" console-awg.log
  ```
  - `config=Failed` → проблема в DNS/config-fetch (класс H0).
  - `config=OK anyrelay=Failed`, `Relays: 0 valid` → чистый UDP-класс (H1/H2) → нужен tcpdump (хендоф 2).
  - Если на Hy2 ВНЕЗАПНО `Relays: N valid` и пинги есть — это ключ: root cause
    per-transport, и решение = гнать игровой UDP через Hy2.

---

## ХЕНДОФ 2 — в VPS-чат (tcpdump на exit-сервере, root, ~5-10 мин)

> Копировать в чат, который управляет exit-серверами (vpnctl). Контекст там свой —
> ниже всё нужное.

**Контекст.** Exit-VPS `93.95.226.167` (Iceland, 1984 ehf) раздаёт AWG (WireGuard) +
VLESS + Hysteria2. Клиент из РФ играет в Dota через туннель. Dota использует **Steam
Datagram Relay**: шлёт UDP-пробы на сетку Valve-релеев (порты **27015-27200**, плюс
STUN **3478**), чтобы измерить пинг регионов. Симптом: «Задержка: ОШИБКА» на всех
регионах. Вопрос: **где гибнут SDR-пакеты** — уходят ли пробы наружу (eth0), приходят
ли ответы релеев (eth0), возвращаются ли в туннель (wg0).

**Снять во время реального Dota-теста клиента (скоординируйтесь по времени).**

```bash
# 0. имя WG-интерфейса и IP wg-клиента
ip -br a | grep -iE 'wg|awg|amnezia'          # напр. wg0 / awg0
WG=wg0                                          # подставь реальное
CLIENT=10.13.13.2                               # IP клиента в туннеле (из peer AllowedIPs)

# 1. пробы наружу + ответы релеев на ПУБЛИЧНОМ NIC:
timeout 90 tcpdump -ni eth0 '(udp portrange 27015-27200) or (udp port 3478)' -w /tmp/sdr-eth0.pcap &

# 2. возврат ответов В ТУННЕЛЬ (для AWG-прогона):
timeout 90 tcpdump -ni "$WG" '(udp portrange 27015-27200) or (udp port 3478)' -w /tmp/sdr-wg.pcap &

# 3. conntrack-маппинги клиента (во время теста):
conntrack -L -p udp 2>/dev/null | grep "$CLIENT" | head -40   # [UNREPLIED] = ответов нет

wait
# быстрые счётчики:
echo "eth0:"; tcpdump -nr /tmp/sdr-eth0.pcap 2>/dev/null | wc -l
echo "wg:";   tcpdump -nr /tmp/sdr-wg.pcap   2>/dev/null | wc -l
echo "== уник. relay-адреса, куда ушли пробы (eth0 src=наш IP):"
tcpdump -nr /tmp/sdr-eth0.pcap 2>/dev/null | grep " > " | awk '{print $5}' | cut -d. -f1-4 | sort -u | head
echo "== пришло ли ЧТО-ТО обратно (eth0 dst=наш IP):"
tcpdump -nr /tmp/sdr-eth0.pcap 2>/dev/null | awk '{print $3,$5}' | grep -c '27\|3478'
```

**Дополнительно (изоляция H2 и NAT-тип):**
```bash
# ручная проба релея с VPS (тишина НЕ доказывает блок — формат запроса закрыт;
# но ЛЮБОЙ ответ доказывает reachability):
nping --udp -p 27015 155.133.248.85 --data-length 40 -c 3
# NAT-тип с публичного IP exit'а (baseline; ждём EIM+EIF):
stunclient --mode full stunserver2025.stunprotocol.org
# не режет ли FORWARD/firewall исходящий UDP-веер:
nft list ruleset 2>/dev/null | grep -iE 'drop|reject|2701|2702' | head
iptables -L FORWARD -n -v 2>/dev/null | head
# conntrack UDP-таймауты (игра должна keep-alive'ить в эти окна):
sysctl net.netfilter.nf_conntrack_udp_timeout net.netfilter.nf_conntrack_udp_timeout_stream
# признак anti-abuse хостера: rate-limit исходящего UDP к многим /16 на высокие порты
dmesg | grep -iE 'rate|drop|udp' | tail
```

**Матрица чтения (по результатам):**
- Проб НЕТ на eth0 → гибнут на клиенте/в туннеле → **H1** (клиентский data-path,
  НЕ VPS). Клиент смотрит WSAENOBUFS в singbox.log (хендоф 1).
- Пробы уходят, ответов на eth0 НЕТ → **H2-внешняя**: релей/хостер молчит. Тест —
  временно поднять игровой профиль на **втором VPS у другого провайдера**; full-cone
  тут не поможет.
- Ответы на eth0 ЕСТЬ, на wg0 НЕТ → **H2-локальная**: conntrack/firewall exit'а режет
  возврат. Фикс (для AWG-экзита):
  ```bash
  # DNAT игрового UDP-диапазона на wg-клиента (заодно full-cone этому диапазону):
  nft add rule inet nat prerouting iif eth0 udp dport 27015-27200 dnat ip to $CLIENT
  # ИЛИ поставить einat-ebpf (EIM+EIF на TC-хуках, kernel>=5.15, без out-of-tree kmod)
  # проверить, что nf_conntrack_udp_timeout не слишком мал (маппинг умирает раньше keep-alive)
  ```
- Ответы дошли до wg0, а Dota всё равно ERROR → проблема на обратном пути в
  клиентском стеке (не VPS).

**Что прислать обратно:** `/tmp/sdr-eth0.pcap` + `/tmp/sdr-wg.pcap` (или счётчики
выше), вывод conntrack/nping/stunclient, и вердикт — какая строка матрицы. По нему
клиентская сторона применит адресный фикс (MTU уже поднимаем независимо).
