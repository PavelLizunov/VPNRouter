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
