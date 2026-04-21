# VPNRouter v2.27 — Service UX + coordination redesign

**Статус**: план, не имплементировано.
**Предшественники**: v2.26.0-r1 (settings watcher + install UX), v2.26.1-r1
(TunLock introspection).
**Почему нужно v2.27**: user-тест v2.26.1-r1 обнажил архитектурный mismatch
— чекбоксы отражают механизмы (service / registry / flag), а не намерения
пользователя. Плюс 4 конкретных бага в flow.

---

## 1 · Наблюдаемые баги (из теста v2.26.1-r1)

### Bug A — VPN «отключается» при установке службы из Advanced
**Шаги:** VPN работает через App → Settings → Автозапуск → ✓ «Включить
фоновую службу».
**Ожидание:** служба устанавливается, App продолжает владеть VPN.
**Факт:** «vpn перешёл в статус disconnected, написала что служба
работает, хотя vpn выключен, пришлось его заново запускать».

**Гипотезы:**
- **A1.** Race в `RuntimeStatusDetector.IsVpnRunning` + `SyncConnected
  WithVpnRuntime`: на миг GetProcessesByName возвращает пустой список
  когда Windows перекидывает handle'ы из-за ETW notifications при
  `sc start`.
- **A2.** `ServiceVm.Refresh()` вызывается в середине `ToggleAutostart
  Async` → `AutostartChecked` отрефрешена до IsInstalled, UI отражает
  «установлена», но в этот момент `StatusMessage` мигает и App-side
  адаптирует состояние через `DetectServiceManagedVpn` (два-сигнальная
  детекция) неверно интерпретирует статус.
- **A3.** Service boot запускает свой ETW session/FirewallManager
  cleanup, который пересекается с App'овыми, дернул `CleanupOrphanedRules`
  на App-стороне → firewall заблокировал active connections.
- **A4.** После `sc start VPNRouter` Windows дёрнул `Dnscache` (зависимость)
  — DnsHardening `dns-hardening-state.json` race, DNS перекочевал, sing-box
  трафик потерял связность на 2-5 сек → App увидел разрыв → автодемотировал
  (есть в SyncConnectedWithVpnRuntime).

**Диагностика завтра:**
- логи `vpnrouter{date}.log` App-side на момент toggle (10 сек до / 10
  сек после), искать `IsConnected=false`, `SyncConnectedWith`, `ETW`,
  `Firewall`, `DnsHardening`.
- логи `vpnrouter{date}.log` Service-side (если существует), искать
  `Service boot`, `TunLock`, `TryMigrate`.

### Bug B — Simple mode чекбокс «Start with Windows» не отражает state после Advanced toggle
**Шаги:** Advanced → ✓ master «Включить фоновую службу» → переключиться
в Simple mode → чекбокс «Запускать с Windows» **не загорелся**.
**Root cause:** `SmpAutostartChecked` инициализируется из
`_settings.App.AutostartVpn` (строка 927), но Advanced master toggle
НЕ устанавливает `AutostartVpn=true` — он только `ServiceVm.
AutostartChecked=true` (service install). Настройка `AutostartVpn`
трогается ТОЛЬКО в Simple (`OnSmpAutostartCheckedChanged`) и в
Advanced sub-card «3 флага компонентов».

### Bug C — Mental model mismatch «что включать — службу или приложение?»
**User quote:** «такеж непонятно что включать службу? или само приложение»

Пользователь думает в категориях «я хочу VPN был на boot / на login».
Текущий UI раскрывает 3 независимых слоя:
1. **Windows Service installed** (`ServiceVm.AutostartChecked`) —
   нужно для pre-login VPN
2. **Service Autostart flags** (`AutostartVpn/Zapret/TgProxy`) —
   говорят сервису ЧТО запустить при boot
3. **HKCU Run key** (`AutostartUi`) — запускает `VPNRouter.App.exe`
   на login (но App на старте VPN **не включает**!)

Три слоя × 6 чекбоксов = UX-кошмар.

### Bug D — Simple ↔ Advanced state не синхронизирован двусторонне
- Simple → Advanced: Simple toggle пишет `AutostartVpn=true` +
  `ServiceVm.AutostartChecked=true`. Advanced увидит обе галки.
- Advanced → Simple: Advanced `ServiceVm.AutostartChecked=true` +
  `AutostartVpn` остаётся false. Simple увидит — выключено. Mismatch.

---

## 2 · Текущая архитектура (полная карта состояний)

```
Пользователь:                    Код:
  "VPN на boot"                    ─┐
                                    ├ ServiceVm.AutostartChecked (bool)
  "VPN on user login"               │    → sc create/delete VPNRouter
                                    │
  "Zapret на boot"                  ├ AppSettings.App.AutostartVpn (bool)
                                    │    → yaml flag, Service reads at boot
  "TgProxy на boot"                 │
                                    ├ AppSettings.App.AutostartZapret (bool)
  "App на login"                    │    → yaml flag
                                    │
                                    ├ AppSettings.App.AutostartTgProxy (bool)
                                    │    → yaml flag
                                    │
                                    └ AppSettings.App.AutostartUi (bool)
                                         → HKCU\...\Run registry key

Runtime state:                        Источник:
  sing-box running?                    ProcessGetProcessesByName + TunLock
  Who owns sing-box?                   TunOwnershipLock.IsOwnedByAnyone
  Service installed?                   sc query (WindowsServiceHelper.IsInstalled)
  Service running?                     sc query ... STATE RUNNING
```

**Сложные состояния** (10+ валидных комбинаций):
| App running? | Service installed? | Service running? | AutostartVpn? | AutostartUi? | User meaning |
|---|---|---|---|---|---|
| Yes | No | — | — | — | Manual, session-only |
| Yes | Yes | No | false | — | Service idle, App in charge |
| Yes | Yes | Yes (watcher) | false | — | Service awake but ceding to App |
| No | Yes | Yes | true | — | VPN runs on boot pre-login |
| Yes | Yes | Yes | true | true | VPN+App auto-start |
| Yes | No | — | — | true | App autostart, VPN manual |

Из этого multi-state пользователь должен был вывести «что мне включать?» — **это невозможно без mental load**.

---

## 3 · Целевая UX-модель (что пользователь видит)

Сложность убирается в два слоя: **«намерение»** на поверхности,
**«механизм»** скрыт.

### Simple mode (одна строчка)
```
[✓] Запускать VPN при старте Windows
    (до входа пользователя, через Windows Service)
```
Чекбокс управляет **всей цепочкой** под капотом:
- install + start service
- set AutostartVpn=true
- set AutostartUi=true (чтобы App тоже запустился и showed status)

Отключение:
- set AutostartVpn=false
- set AutostartUi=false (опционально оставляем если Zapret/TG автостартуют)
- если Zapret=false && TgProxy=false → uninstall service
- иначе → keep service installed (оно нужно для Zapret/TG)

### Advanced mode — два логических блока вместо трёх

```
┌─────────────────────────────────────────────────┐
│  НА СТАРТЕ WINDOWS (до логина)                  │
│                                                 │
│  [✓] Фоновая служба Windows                     │
│      Нужна для auto-start VPN / Zapret / TG     │
│      ДО входа пользователя. Запустится как      │
│      LocalSystem.                               │
│                                                 │
│  └─ Status: ● Running (PID 1234)                │
│     [Restart] [Reinstall]                       │
│                                                 │
│  Что запускать вместе со службой:               │
│    [✓] VPN      (AutostartVpn)                  │
│    [ ] Zapret   (AutostartZapret)               │
│    [ ] Telegram (AutostartTgProxy)              │
└─────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────┐
│  ПРИ ВХОДЕ ПОЛЬЗОВАТЕЛЯ                         │
│                                                 │
│  [ ] Запускать VPNRouter при логине             │
│      Откроет окно приложения после входа.       │
│      НЕ запускает VPN — только открывает UI.    │
└─────────────────────────────────────────────────┘
```

Ключевые принципы:
- **Группировка по «когда происходит»**, не «какой механизм»
- **Чёткое «что делает»** в subtitle, чтобы user понимал разницу
- **Звезда Simple чекбокса** — это «VPN на старте Windows» = master toggle
  под капотом комбинирующий «service + AutostartVpn»

### Синхронизация Simple ↔ Advanced

Simple-чекбокс `SmpAutostartChecked` должен быть computed property:
```csharp
public bool SmpAutostartChecked
{
    get => ServiceVm.IsInstalled
        && ServiceVm.IsRunning
        && _settings.App.AutostartVpn;
    set {
        if (value) {
            ServiceVm.AutostartChecked = true;  // install + start
            _settings.App.AutostartVpn = true;
            _settings.App.AutostartUi = true;   // optional, UX default
            SaveSettings();
        } else {
            _settings.App.AutostartVpn = false;
            // Don't uninstall service if Zapret/TG still want it
            var anyComponent = _settings.App.AutostartZapret
                            || _settings.App.AutostartTgProxy;
            if (!anyComponent)
                ServiceVm.AutostartChecked = false;
            SaveSettings();
        }
    }
}
```

И триггерить PropertyChanged для SmpAutostartChecked при:
- `OnAutostartVpnChanged`
- `ServiceVm.IsInstalledChanged`
- `ServiceVm.IsRunningChanged`

---

## 4 · План реализации (v2.27.0-r1)

### 4.1 · Investigate + fix Bug A (VPN disconnect on service install)

**Шаг 1.** Инструментирование логов. Добавить Info-level trace:
- в `ServiceVm.ToggleAutostartAsync` — `[ServiceVm] install/start begin/
  end`
- в `RuntimeStatusDetector.IsVpnRunning` — на trace `[Detector] sing-box
  PIDs: {pids}` (раз в 5с не каждый 2с)
- в `VpnEngine.Stop` — `[VpnEngine] Stop called from: {StackTrace.2}`
  чтобы видеть откуда вызов

**Шаг 2.** Воспроизвести локально (user сделает завтра).

**Шаг 3.** По логам определить точную причину (A1/A2/A3/A4).

**Шаг 4.** Фикс:
- A1 (ETW race) → add 3-second grace window в SyncConnectedWithVpnRuntime
  после ServiceVm.IsBusy=true
- A2 (refresh mid-install) → lock ServiceVm.Refresh до окончания
  ToggleAutostartAsync
- A3 (firewall conflict) → gate CleanupOrphanedRules на "service NOT
  in transition"
- A4 (DNS race) → add DnsHardening lock через Mutex аналогично TunLock

### 4.2 · Fix Bug B (Simple toggle state sync)

Преобразовать `SmpAutostartChecked` в computed property (см. выше,
Синхронизация).

Файлы:
- `MainWindowViewModel.SimpleMode.cs` — removed `[ObservableProperty]
  _smpAutostartChecked`, заменить на computed get/set
- Добавить `OnPropertyChanged(nameof(SmpAutostartChecked))` в:
  - `OnAutostartVpnChanged`
  - `ServiceVm.PropertyChanged += (_,e) => if (e.PropertyName in [IsInstalled, IsRunning])
     OnPropertyChanged(nameof(SmpAutostartChecked))`
- UI: SimplePage.axaml — checkbox binding уже correct, just ensure
  two-way mode

### 4.3 · Fix Bug C (mental model alignment) — UX redesign Advanced

Файлы:
- `VPNRouter.App/Views/Pages/NetworkPage.axaml` — Autostart section
  rewrite per target UX (см. раздел 3)
- `VPNRouter.App/Localization/Strings.cs` — новые ключи:
  - `AutostartBootSectionTitle` = "На старте Windows (до логина)"
  - `AutostartBootSectionSub` = "Нужна служба Windows для запуска VPN,
    Zapret или Telegram-прокси до входа пользователя"
  - `AutostartComponentsInfoHint` = "Эти флаги читает служба при boot.
    Требуется установленная служба."
  - `AutostartLoginSectionTitle` = "При входе пользователя"
  - `AutostartLoginAppDescription` = "Запускает приложение VPNRouter
    после входа. VPN придётся стартануть вручную или включить «на
    старте Windows» выше."

### 4.4 · Fix Bug D (bidirectional sync) — part of 4.2

`SmpAutostartChecked` как computed + подписка на ServiceVm.Property
Changed даст двустороннюю синхронизацию:
- Simple ticks → service + flag write → Advanced видит оба checkbox'а
- Advanced ticks service → AutostartVpn не меняется, SmpAutostart
  computed = `service_running && AutostartVpn` → still false → Simple
  показывает выключено (correct!)
- Advanced ticks только AutostartVpn → service not installed →
  SmpAutostart computed = false → Simple выключен (correct!)
- Advanced ticks и service и AutostartVpn → SmpAutostart = true →
  Simple автоматически загорается ✓

### 4.5 · Grafical audit

**Дополнительные UI-проверки:**
1. Service pill «Running/Stopped» на master-card — визуально сейчас
   маленький справа сбоку. Надо сделать более заметным (возможно
   подзаголовок master checkbox: "● Running — PID 1234")
2. Когда `ServiceVm.IsBusy=true` (install в процессе) — overlay spinner
   на master-card + блокировка всех sub-checkboxes
3. Error feedback: `ServiceVm.StatusMessage` сейчас показывается
   FontStyle="Italic" маленьким — для ошибок надо сделать Warning
   фоном

### 4.6 · Architecture audit — 3 concerns

**C1. Service-App не знают друг о друге кто что делает**
- Решение: unified `CoordinationManager` service в Core. Держит:
  - Who owns sing-box (TunLock-backed)
  - Is Service installed+running
  - Current AppSettings
  - Events: `OwnershipChanged`, `SettingsChanged`, `ComponentStarted`
- Both App and Service ref this. Single source of truth.

**C2. AppSettings-flags конкуренция**
- Сейчас App и Service могут писать в config.yaml одновременно
  (App - при SaveSettings, Service - не пишет но может захотеть).
  Нет locking.
- Решение: file-lock protocol вокруг Parse/Save. Или: Service
  read-only, только App пишет. FileSystemWatcher на Service side
  — read only.
- **Принципиальное решение**: App = authoritative writer. Service =
  reader. Никогда не пишет.

**C3. State transitions без undo**
- ToggleAutostartAsync не имеет undo на полпути. Если install прошёл
  но start упал → service installed but not running + UI показывает
  `AutostartChecked=false` (потому что Refresh смотрит IsRunning).
  Inconsistent state.
- Решение: state machine на ServiceVm с явными переходами (Idle →
  Installing → Starting → Running / Failed). На Failed состоянии
  явная UI-ошибка с кнопкой Retry.

---

## 5 · Test matrix (воспроизвести завтра)

| № | Предусловие | Действие | Ожидание |
|---|-------------|----------|----------|
| 1 | Fresh install, VPN off | Simple: ✓ Start with Windows | Service installs, AutostartVpn=true, AutostartUi=true, SaveSettings |
| 2 | Simple #1 → Advanced | Advanced: Service master=✓, sub-VPN=✓, AutostartUi=✓ | Все флажки загорелись (Bug B fix) |
| 3 | Advanced: master=✓, all sub=false | Simple переключение | Simple checkbox **off** (потому что AutostartVpn=false) |
| 4 | App running VPN (manual) | Advanced: master=✓ | VPN остаётся running, Service в watcher mode, No disconnection (Bug A fix) |
| 5 | Service+VPN running after boot | App launches → minimize | DetectServiceManagedVpn → "Подключено через службу", no duplicate sing-box |
| 6 | Advanced: off | Simple | Simple off; Service uninstalled IFF AutostartZapret=false && AutostartTgProxy=false |
| 7 | Advanced: only AutostartZapret=true | Simple | Simple OFF; Service still installed (Zapret нуждается) |
| 8 | Rename/move app folder, relaunch | `sc qc VPNRouter` | binPath обновлён (self-heal), Run key тоже обновлён |
| 9 | Ctrl+C Service during install | Re-launch App | Service в failed state, UI показывает Retry |
| 10 | FileSystemWatcher under load | SaveSettings() × 5 in 1 sec | Debounce 2с, service ReadAllText без IOError |

---

## 6 · Milestones

**Day 1 (завтра):**
- 4.1 Investigate Bug A (logs + repro)
- 4.2 Bug B fix (SmpAutostartChecked computed)
- 4.3 Bug C UX rewrite (Advanced section в NetworkPage)
- Build + ship v2.27.0-r1

**Day 2:**
- 4.5 Graphical audit (spinner overlay, error surfacing)
- 4.6 C1 CoordinationManager (если scope позволяет — иначе отложить)
- Test matrix #1-10 верификация
- Ship v2.27.0-r2 / promote v2.27.0 stable

**Day 3 (оптс.):**
- C2 + C3 state machine refactor

---

## 7 · Risk

- **Большой рефакторинг Autostart чекбоксов** может сломать существующие
  персистированные настройки. Митигация: migration в SettingsLoader.
  Parse — если `schema_version < 2`, mapping старых флагов в новые.
- **SmpAutostartChecked → computed property** может спровоцировать
  infinite PropertyChanged loops если setter вызывает OnPropertyChanged
  который триггерит setter. Митигация: `_isLoadingUI` guard + тесты.
- **Bug A hypothesis selection** без логов — spiral of guessing. Если
  завтра logs не покажут причину, fallback: «после install service
  откладываем Refresh + DetectServiceManagedVpn на 5 сек» — мажет
  проблему без root-cause fix, но лучше чем ничего.

---

## 8 · Файлы для чтения перед стартом завтра

- `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs` — как
  SmpAutostart wired сейчас (строки 408-430)
- `VPNRouter.App/ViewModels/ServiceViewModel.cs` — install flow
- `VPNRouter.App/Services/WindowsServiceHelper.cs` — sc commands
- `VPNRouter.Service/VPNRouterService.cs` — service boot flow
- `VPNRouter.App/Views/Pages/NetworkPage.axaml` — current Advanced
  Autostart section (строки 224-330)
- `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs` —
  SyncConnectedWithVpnRuntime (возможный source Bug A)
- `C:\ProgramData\VPNRouter\logs\vpnrouter{date}.log` — user's repro
  логи на момент toggle

---

## 9 · Вопросы к user перед началом v2.27 работы

1. **Какой момент точно был disconnect** — сразу на toggle, через 2 сек,
   через 10 сек? (поможет сузить A1/A2/A3/A4)
2. **Слетела ли сеть вообще** (IP-leak) или только UI показал
   disconnected но трафик шёл? (различает реальный down от UI-bug)
3. **Перед toggle — App владел VPN через какой путь** (Manual,
   Subscribe, Custom Config)?
4. **Simple mode checkbox** — что именно пишет: «Запускать с Windows»
   или «Autostart VPN»? (влияет на copy в redesign)
