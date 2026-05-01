# v2.30.2 — Servers tab UX + multi-subscription connection sequence bugs

**Reported**: 2026-05-01 (после релиза v2.30.1 stable)
**Status**: queued — нужно воспроизвести + investigate

## Bug 1: Servers page открывается с неправильным подсвеченным sub-tab

### Симптом

После открытия страницы "Серверы":
- Visually выделена sub-tab "Свои конфиги"
- Но open показывает страницу "Серверы" (контент VLESS списка)
- Чтобы видеть серверы, нужно кликнуть по sub-tab'ам туда-сюда

### Гипотеза root cause

`SelectedServerModeIndex` инициализируется не синхронно с
`IsVlessMode`. Posibilities:

- Default value `SelectedServerModeIndex = 0` (VLESS) есть, но при
  load settings возможно `IsVlessMode = false` (если ConfigMode =
  "subscribe" или "custom") → ListBox visual selection остаётся на
  index 1 (Custom) хотя VM считает что 0 (VLESS).
- ИЛИ `OnSelectedServerModeIndexChanged` flips IsVlessMode при
  loading но XAML binding отстаёт.

Pre-r3 было: `_settings.App.ConfigMode = IsSubscribeMode ? "subscribe"
: IsVlessMode ? "generated" : "custom";` — линия 2155-2156:

```csharp
IsSubscribeMode = configMode.Equals("subscribe", ...);
IsVlessMode = !configMode.Equals("custom", ...) && !IsSubscribeMode;
SelectedServerModeIndex = IsVlessMode ? 0 : 1;
```

Если `ConfigMode = "subscribe"` → `IsSubscribeMode=true`,
`IsVlessMode=false`, `SelectedServerModeIndex=1` (Custom!).

Это БАГ — Subscribe mode не должен заставлять Servers tab показывать
Custom sub-tab. На странице "Серверы" sub-tab должен ВСЕГДА начинать
с VLESS index 0 если у юзера нет реального custom config'а.

### Fix idea

```csharp
// Sub-tab follows what's available in the SERVERS list, not what's
// the active connection mode.
//   - Have VLESS rows in Servers list?  → VLESS sub-tab (index 0)
//   - Have only custom configs?         → Custom sub-tab (index 1)
//   - Empty?                            → VLESS sub-tab (default)
SelectedServerModeIndex = (Servers.Count > 0 || CustomConfigs.Count == 0)
    ? 0 : 1;
```

И `OnSelectedTabIndexChanged` для tab "Servers" (Tab 0): убедиться что
sub-tab visually синхронизирован с VM при первой навигации.

---

## Bug 2: Multi-subscription click chain ломает active indicator + tab nav + connection target

### Симптом (флоу пользователя 2026-05-01)

1. Запустил приложение
2. Перешёл на Subscriptions tab
3. Кликнул на ПЕРВУЮ подписку → она подключилась (зелёный индикатор?)
4. Кликнул на ПОСЛЕДНЮЮ подписку → "она вроде тоже подключилась"
5. **Ни один из конфигов не горел** (no active indicator anywhere)
6. Перешёл в Servers tab
7. **Открылась пустая страница**, "Серверы" sub-tab подсвечена
8. Кликнул "Свои конфиги" → обратно на "Серверы" → **только тогда
   увидел свои серверы**
9. Кликнул на любой из серверов → VPN перезапустилось, **но включился
   конфиг из подписки** (не выбранный сервер!)

### Multiple suspected root causes

#### Root cause A: Subscription click → switches engine, no active indicator update

Когда юзер кликает на subscription server:
- `OnSelectedSubscriptionServerChanged` запускает `ReconnectAsync`
- `ReconnectAsync` вызывает `_engine.Stop()` + `StartAsync` с новым
  активным сервером
- НО `ActiveServerChanged?.Invoke` (через `RefreshActiveIndicator`)
  не вызывается? Или вызывается слишком рано (когда ActiveServerAddress
  ещё не обновлён в engine)?

Можно проверить — `_engine.ActiveServerAddress` обновляется в
StartAsync, но `RefreshActiveIndicator` вызывается ПОСЛЕ из
RestoreConnectedStatus (line ~2599). Если порядок неправильный →
индикатор устанавливается на старое значение.

Или: `Vless.ActiveServer` (имя) сбрасывается между Stop и StartAsync
из-за Subscriptions.ActiveSubscriptionServer flow.

#### Root cause B: Servers tab open → empty list → user must click sub-tabs

Это связано с Bug 1. Когда юзер только что был в Subscribe mode,
ConfigMode = "subscribe", IsVlessMode=false, SelectedServerModeIndex=1
(Custom). Поэтому при первом открытии Servers tab визуально
показывается "Custom" подвкладка с пустым списком custom configs.

User видит "пусто" → кликает "Серверы" sub-tab → срабатывает
OnSelectedServerModeIndexChanged → IsVlessMode=true →
ConfigMode flip…

WAIT! Это и есть второй bug — когда user кликает sub-tab "Серверы"
после subscription, СРАБАТЫВАЕТ SaveSettings → подменяется ConfigMode
на "generated". Хотя у нас ЕСТЬ guard от 2.30.1-r2 — что если sub
ENABLED, оставлять ConfigMode = "subscribe".

Так что user'у guard ПОМОГ: ConfigMode остался "subscribe"… и поэтому
когда он click'ает на server в Servers list → Apply re-runs subscribe
flow → reconnects via subscription server, не via clicked server!

То есть r2 fix СЛИШКОМ агрессивный. Он защищает от случайного flip но
блокирует ЛЕГИТИМНЫЙ switch на manual VLESS.

#### Root cause C: SelectedServer click handler

`OnSelectedServerChanged` (line 4274):
```csharp
if (IsConnected && IsVlessMode && !IsConnecting)
{
    ReconnectAsync(value.DisplayName);
}
```

Гард `IsVlessMode=true` нужен чтобы reconnect фactически переключился
на VLESS server. Если `IsSubscribeMode=true` остался true (после
peeking к subscriptions tab), то условие не выполняется → но user
clicks → возможно ReconnectAsync всё равно срабатывает по другому
триггеру.

Или: `ReconnectAsync` просто читает `_settings.Vless.ActiveServer` /
`App.ActiveSubscriptionServer` — если первый пуст а второй заполнен,
reconnect использует subscription server.

### Fix strategy (нужно validate в repro session)

1. **Bug 1 fix** (sub-tab default): не привязывать sub-tab visually
   к ConfigMode. Servers page всегда открывается с "Серверы" sub-tab
   visible, если в списке есть VLESS entries.

2. **Bug 2 fix** (subscription→server switch):
   - Когда user кликает на server в Servers list, нужно ЯВНО
     переключить ConfigMode на "generated" + IsSubscribeMode=false.
   - r2 guard должен срабатывать ТОЛЬКО при peeking (sub-tab navigate),
     не при ACTUAL connect/reconnect action.
   - Возможно нужен новый flag `_pendingModeSwitch` который
     OnSelectedServerChanged ставит, а SaveSettings уважает.

3. **Active indicator после subscription switch**:
   - `ReconnectAsync` должен вызывать `RefreshActiveIndicator()` в
     finally блоке после `_engine.IsRunning` подтверждается.
   - Или подписаться на engine.StatusChanged event.

## Reproduction checklist

To repro в test session:

- [ ] Start app (clean state, ConfigMode either subscribe или
      generated)
- [ ] Open Servers page (Tab 0)
- [ ] Confirm: какой sub-tab визуально выделен в первый раз?
- [ ] Open Subscriptions (Tab 1) → connect to first sub server
- [ ] Click on a different sub server → confirm reconnect happens
- [ ] Confirm: green indicator на конкретном sub server?
- [ ] Switch to Servers tab → confirm: какой sub-tab? список рендерится?
- [ ] Click on a manual VLESS server → confirm: reconnects via WHICH
      server (subscription's last или manual's)?

Логи проверять:
- `[INF] [VpnEngine] Stopping...` (when reconnect starts)
- `[INF] [VlessServersResolver] ...` (which servers list aggregated)
- `[INF] [VpnEngine] Connected (PID xxx)` (after restart)
- `_settings.App.ConfigMode` value через debugger или log

## Acceptance

- [ ] Servers page open → "Серверы" sub-tab visually selected (if list
      non-empty) ИЛИ "Custom" если только custom configs есть
- [ ] Subscription server click → green indicator на нужном server,
      no UI desync
- [ ] Click manual VLESS in Servers list (после subscription был
      active) → reconnects via THAT manual server, not previous
      subscription
- [ ] Tests: VM-layer test когда headless Avalonia harness ready

## Cross-refs

- `MainWindowViewModel.cs:2154-2159` — initial sub-tab assignment from ConfigMode
- `MainWindowViewModel.cs:2786-2800` — SaveSettings ConfigMode logic с r2 guard
- `MainWindowViewModel.cs:4274-4284` — OnSelectedServerChanged auto-reconnect
- `MainWindowViewModel.cs:2030-2080` — RefreshActiveIndicator (post-r6)
- `plans/release-notes-v2.30.1.md` — что shipped в stable

## Update from user log (z:\vpnrouter20260501.log, v2.30.1 stable)

User submitted production logs from 2026-05-01 running v2.30.1 stable. Key
patterns confirmed:

### r2 guard fires as designed (good news + bad news)

```
12:56:27 [INF] [Settings] Subscription is active — keeping ConfigMode=subscribe
   even though Custom sub-tab is selected (user is peeking, not switching)
14:16:18 [INF] [Settings] Subscription is active — keeping ConfigMode=subscribe
   even though Custom sub-tab is selected (user is peeking, not switching)
```

Это ДОКАЗЫВАЕТ Bug 2 root cause B: r2 guard работает, но он-то и блокирует
легитимный switch. Когда после peek user кликает на manual server в Servers
list, ConfigMode остался `"subscribe"`, и Apply re-runs subscription server.

### TUN adapter "device not ready" loop (NEW finding for v2.30.2)

```
14:15:41 [WRN] [sing-box] FATAL configure tun interface: Cannot create a file
                          when that file already exists.
14:15:42 [INF] HealthMonitor Restarting sing-box (attempt 1/5) in 5000ms
14:15:42 [INF] [TunDiag] SingBoxManager.OnProcessExited: disabled orphaned
               adapter 'VPNRouter-TUN' ← r5 cleanup fired
14:15:44 [INF] netsh shows: Disabled Disconnected Dedicated VPNRouter-TUN
                ← adapter persists в disabled state

14:16:14 [INF] [VpnEngine] Starting sing-box...
14:16:30 [WRN] [sing-box] FATAL configure tun interface:
                          The device is not ready for use.
14:16:30 [ERR] sing-box crashed (exit code: 1)
14:16:31 [WRN] HealthMonitor Restarting sing-box (attempt 1/5) in 5000ms
14:16:36 [INF] sing-box started (PID 8440)        ← finally succeeded после ~22s
14:16:37 [INF] HealthMonitor sing-box restarted successfully
```

Анализ:
- r5 cleanup CORRECTLY disable'ит adapter via netsh после crash.
- Но Windows network stack долго (~22 секунд) держит handle в "Disabled +
  Disconnected" состоянии прежде чем wintun сможет re-create.
- Если user пытается start ВО ВРЕМЯ этого окна — sing-box получает "device
  not ready for use" FATAL.

### Fix idea для TUN adapter (v2.30.2 Bug 3 — new)

В `VpnEngine.StartAsync()` ДО `_singBoxManager.StartAsync()`, добавить
preflight check:

```csharp
// If VPNRouter-TUN exists in disabled state from previous crash cleanup,
// re-enable it before sing-box tries to use it. Otherwise sing-box gets
// "device not ready for use" FATAL.
TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent(
    _logger, "VPNRouter-TUN", "VpnEngine.pre-start");
```

`TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent`:
1. `netsh interface show interface name="VPNRouter-TUN"` → check state
2. If "Disabled" → `netsh interface set interface admin=enabled` → wait ~1s
3. If "Enabled, Disconnected" → ОК, sing-box разберётся
4. If not found → ОК, sing-box создаст fresh
5. If timeout / error — log warning, продолжаем (sing-box попытается, упадёт,
   HealthMonitor перезапустит — текущий fallback)

### Bug 1 (sub-tab init) — log evidence

Log не показывает напрямую state of `SelectedServerModeIndex` в момент load,
но по структуре всех traces user был в subscribe mode большую часть сессии.
Так что при load ConfigMode="subscribe" → IsVlessMode=false →
SelectedServerModeIndex=1 (Custom). Это и есть наша гипотеза.

### Final v2.30.2 scope

- **Bug 1**: Sub-tab init не должен mirror ConfigMode. Вместо этого:
  - Default = 0 ("Серверы") если `Servers.Count > 0` ИЛИ `CustomConfigs.Count == 0`
  - Default = 1 ("Свои конфиги") если `Servers.Count == 0` AND `CustomConfigs.Count > 0`
- **Bug 2A** (active indicator после subscription switch): RefreshActiveIndicator
  должен вызываться в `ReconnectAsync` finally, после `engine.IsRunning` confirmed.
- **Bug 2B** (subscription→manual switch flips ConfigMode): когда user clicks
  server в Servers list под subscribe-mode, ЯВНО switch ConfigMode→"generated"
  и IsSubscribeMode=false. r2 guard сохраняется для случая клика по sub-tab,
  но не для клика по серверу.
- **Bug 3** (NEW): TUN adapter pre-start re-enable.
- **Diagnostic logging**: добавить trace в OnSelectedTabIndexChanged,
  OnSelectedSubscriptionServerChanged, OnSelectedServerModeIndexChanged,
  RefreshActiveIndicator, ReconnectAsync — чтобы следующий repro был
  unambiguous.
