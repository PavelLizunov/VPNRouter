# VPNRouter v2.27 — Service UX + coordination redesign

**Статус**: в работе. 3 из 4 багов закрыты в `v2.26.3-r1`, остался UX-редизайн (Bug C) и архитектурные подчистки.
**Предшественники**: v2.26.0-r1 (settings watcher + install UX), v2.26.1-r1 (TunLock introspection), v2.26.3-r1 (Bug A + B + D fixes + GUI test harness).
**Почему нужно v2.27**: user-тест v2.26.1-r1 обнажил архитектурный mismatch — чекбоксы отражают механизмы (service / registry / flag), а не намерения пользователя. Плюс 4 конкретных бага; 3 уже исправлены.

---

## 1 · Статус багов

### Bug A — VPN «отключается» при установке службы из Advanced
**Статус: ✅ FIXED in `v2.26.3-r1` (commit `09b4cbf`)**

**Симптом**: пользователь тикает Advanced → «Включить фоновую службу» пока VPN работает → VPN мгновенно уходит в disconnected, при этом UI пишет «служба установлена».

**Real root cause** (найден live-репро в VM-харнесе, **не из 4 гипотез в плане** — пятый путь):
`VPNRouter.Service/Program.cs:35` при каждом boot'е безусловно убивает все `sing-box.exe` процессы как "orphans". App'овский sing-box с активным TUN — тоже попадает под раздачу.

**Event Log — smoking gun:**
```
10:37:39 Found 1 orphan sing-box process(es), killing before startup
10:37:39 Killed orphan sing-box PID 9212   ← App'а, не orphan
```

**Fix**: перед sweep проверяем `TunOwnershipLock.IsOwnedByAnyone()`. Если TUN чей-то, sing-box *не orphan*, пропускаем kill. Service всё равно паркуется в watcher mode (существующее v2.26.1 поведение).

**Verification**: тот же Event Log на v2.26.3-r1:
```
10:47:21 TUN is held by another VPNRouter instance — skipping orphan sing-box cleanup
10:47:22 Service started successfully.
```
VPN остался Connected, user подтвердил.

---

### Bug B — Simple "Start with Windows" не отражает state после Advanced toggle
**Статус: ✅ FIXED in `v2.26.3-r1` (commit `596da47`)**

**Root cause**: `SmpAutostartChecked` был `[ObservableProperty]` field, инициализированным один раз из `AutostartVpn` при загрузке UI. Advanced master toggle флипал только `ServiceVm.AutostartChecked`, `AutostartVpn` не трогал → Simple visually врал.

**Fix**: `SmpAutostartChecked` → computed property:
```csharp
get => ServiceVm.IsInstalled && ServiceVm.IsRunning && _settings.App.AutostartVpn;
```
PropertyChanged re-fires на любом из 3 входов:
- `OnAutostartVpnChanged` → `OnPropertyChanged(nameof(SmpAutostartChecked))`
- `ServiceVm.PropertyChanged` subscription в конструкторе (scoped to IsInstalled/IsRunning)

Setter инкапсулирует всю цепочку install+start + AutostartVpn + optional uninstall с conditional "keep service alive if Zapret/TgProxy still need it".

**Regression covered**: `MainWindowViewModelTests.SmpAutostartChecked_ReactsToAllThreeInputs` в VPNRouter.Tests.

---

### Bug C — Mental model mismatch: «что включать — службу или приложение?»
**Статус: ⏳ PENDING** (остаётся в v2.27 scope, нужен твой design sign-off на mockup §3)

User quote: "непонятно что включать службу? или само приложение"

Три слоя state × 6 чекбоксов:
1. Windows Service installed (`ServiceVm.AutostartChecked`) — нужно для pre-login VPN
2. Service autostart flags (`AutostartVpn/Zapret/TgProxy`) — что запустить при boot
3. HKCU Run key (`AutostartUi`) — запуск App на login (но App не включает VPN!)

**Fix план**: §4.3 — переписать `NetworkPage.axaml` Autostart section в два логических блока по §3 (Target UX).

---

### Bug D — Simple ↔ Advanced state не синхронизирован двусторонне
**Статус: ✅ FIXED in `v2.26.3-r1` (commit `596da47`)** — побочно от B.

Simple → Advanced работал (Simple setter писал оба флага). Advanced → Simple ломался: Advanced тикал только `ServiceVm.AutostartChecked`, `AutostartVpn` оставался false → `SmpAutostartChecked` computed = false → Simple off (correct!).

Теперь computed property синхронизирует обе стороны автоматически: любое изменение IsInstalled/IsRunning/AutostartVpn fires PropertyChanged → UI обновляется без ручной синхронизации.

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
  sing-box running?                    Process.GetProcessesByName + TunLock
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

Из этого multi-state пользователь должен был вывести «что мне включать?» — **это невозможно без mental load**. Bug C — остался нерешённый.

---

## 3 · Целевая UX-модель (что пользователь видит)

Сложность убирается в два слоя: **«намерение»** на поверхности, **«механизм»** скрыт.

### Simple mode (одна строчка)
```
[✓] Запускать VPN при старте Windows
    (до входа пользователя, через Windows Service)
```

Bug B fix уже сделал чекбокс computed'ом; setter выполняет всю цепочку:
- install + start service
- set AutostartVpn=true
- set AutostartUi=true (чтобы App тоже запустился и showed status)

Отключение (тоже в setter):
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

### Синхронизация Simple ↔ Advanced ✅ Уже работает

Реализовано в `596da47` (Bug B fix). `SmpAutostartChecked` — computed property, PropertyChanged на любом из 3 входов. UI не требует ручной sync.

---

## 4 · План реализации (осталось сделать)

### 4.1 · Bug A ✅ Done (`09b4cbf`)

### 4.2 · Bug B + D ✅ Done (`596da47`)

### 4.3 · Bug C — Advanced UX redesign (⏳ ближайшая итерация)

**Scope**:
- `VPNRouter.App/Views/Pages/NetworkPage.axaml` — Autostart section rewrite per §3 mockup
- `VPNRouter.App/Localization/Strings.cs` — новые ключи:
  - `AutostartBootSectionTitle` = "На старте Windows (до логина)"
  - `AutostartBootSectionSub` = "Нужна служба Windows для запуска VPN, Zapret или Telegram-прокси до входа пользователя"
  - `AutostartComponentsInfoHint` = "Эти флаги читает служба при boot. Требуется установленная служба."
  - `AutostartLoginSectionTitle` = "При входе пользователя"
  - `AutostartLoginAppDescription` = "Запускает приложение VPNRouter после входа. VPN придётся стартануть вручную или включить «на старте Windows» выше."
- английские соответствия

**Pre-requirements**:
- Design sign-off от user на mockup §3 (оба блока, копирайтинг, hierarchy)
- Screenshot baseline через `PageScreenshotTests.NetworkPage` — будет виден diff до/после

**Deliverable**: `v2.26.4-r1` или прямой выход в `v2.27.0-r1`.

### 4.4 · Bug D ✅ Done (побочно от B)

### 4.5 · Graphical audit

**Дополнительные UI-проверки (сохраняется из оригинального плана):**
1. **Service pill «Running/Stopped»** на master-card — сейчас маленький справа сбоку. Сделать более заметным (подзаголовок master checkbox: "● Running — PID 1234"). Покрыто тестом `PageScreenshotTests.NetworkPage`.
2. **Overlay spinner** когда `ServiceVm.IsBusy=true` (install/uninstall в процессе) + блокировка всех sub-checkboxes (чтобы не триггерили race).
3. **Error feedback**: `ServiceVm.StatusMessage` сейчас показывается `FontStyle="Italic"` маленьким — для ошибок нужен warning-фон (`{DynamicResource WarningSurfaceBrush}` или аналог из Arctic theme).

**Scope**: средний. 100-150 строк XAML + styles.

### 4.6 · Architecture audit — 3 concerns

#### C1. Service-App не знают друг о друге кто что делает

**Решение (полное)**: unified `CoordinationManager` service в Core (см. §6.3 ниже). Держит TUN owner, service state, settings hash, component up/down. Events: `OwnershipChanged`, `SettingsChanged`, `ComponentStarted`.

**Решение (минимальное — уже сделано в v2.26.3-r1)**: Service читает `TunOwnershipLock.IsOwnedByAnyone()` в orphan cleanup + `VPNRouterService.cs` watcher mode. App держит свой state locally. Расхождения между процессами остаются, но Bug A конкретно закрыт.

**Для v2.27 scope**: **оставляем минимальное решение**. CoordinationManager — отдельный большой рефакторинг, не ставит UX-галочки Bug C. См. §6.3 для обоснования "почему не сейчас".

#### C2. AppSettings-flags конкуренция

**Проблема**: App (при SaveSettings) и Service (теоретически — не пишет, но может) могут параллельно писать в `config.yaml`. Нет locking.

**Принципиальное решение**: **App = authoritative writer. Service = read-only + FileSystemWatcher (уже сделано в v2.26.0)**.

**В v2.27 закрыть**:
- Document это в `VPNRouterService.cs` (выше `SettingsLoader.Load` calls) — "read-only path, writes are App's responsibility".
- Проверить что Service нигде не вызывает `SaveSettings()` / `File.WriteAllText` на yaml. Grep'нуть → fix'нуть если найдём.

Scope: 1-2 часа, малый риск.

#### C3. State transitions без undo

**Проблема**: `ToggleAutostartAsync` не имеет undo на полпути. Если install прошёл но start упал → service installed but not running, UI показывает `AutostartChecked=false` (Refresh смотрит IsRunning). Inconsistent state, никакого явного error feedback.

**Решение**: state machine на `ServiceViewModel` с явными переходами:
```
Idle → Installing → Starting → Running
                           ↓
                         Failed (explicit UI: "Сервис установлен но не запустился. [Retry] [Uninstall]")
```

**Scope**: средний. ~200 строк в ServiceViewModel + XAML error surface.

---

## 5 · Test matrix

**Обозначения**: ✅ — покрыто автоматически, 🧪 — руками репро, 📸 — visual через screenshots test, 🔴 — pending

| № | Предусловие | Действие | Ожидание | Покрытие |
|---|-------------|----------|----------|----------|
| 1 | Fresh install, VPN off | Simple: ✓ Start with Windows | Service installs, AutostartVpn=true, AutostartUi=true, SaveSettings | 🧪 ручной |
| 2 | Simple #1 → Advanced | Advanced: Service master=✓, sub-VPN=✓, AutostartUi=✓ | Все флажки загорелись (Bug B fix) | ✅ `SmpAutostartChecked_ReactsToAllThreeInputs` |
| 3 | Advanced: master=✓, all sub=false | Simple переключение | Simple checkbox **off** (потому что AutostartVpn=false) | ✅ тот же тест |
| 4 | App running VPN (manual) | Advanced: master=✓ | VPN остаётся running, Service в watcher mode, No disconnection (Bug A fix) | ✅ Event Log репро + 🧪 user-confirmed в v2.26.3-r1 |
| 5 | Service+VPN running after boot | App launches → minimize | DetectServiceManagedVpn → "Подключено через службу", no duplicate sing-box | 🧪 ручной (нужен reboot) |
| 6 | Advanced: off | Simple | Simple off; Service uninstalled IFF AutostartZapret=false && AutostartTgProxy=false | 🧪 ручной (требует Zapret/TG настроек) |
| 7 | Advanced: only AutostartZapret=true | Simple | Simple OFF; Service still installed (Zapret нуждается) | 🧪 ручной |
| 8 | Rename/move app folder, relaunch | `sc qc VPNRouter` | binPath обновлён (self-heal), Run key тоже обновлён | 🧪 ручной (v2.26.0 feature) |
| 9 | Ctrl+C Service during install | Re-launch App | Service в failed state, UI показывает Retry | 🔴 после §4.6 C3 |
| 10 | FileSystemWatcher under load | SaveSettings() × 5 in 1 sec | Debounce 2с, service ReadAllText без IOError | 🧪 ручной |
| 11 | Bug C flows | Advanced secondary panel | Checkboxes из нового §3 layout работают как ожидается | 📸 + 🧪 после §4.3 |
| 12 | Visual regression на всех pages | `dotnet test` | 9 PNG'ов в screenshots/ без дыр, окно рендерится | ✅ `PageScreenshotTests` |

---

## 6 · Milestones (пересобранные)

**✅ Done (shipped в v2.26.3-r1):**
- Bug A (TunLock check)
- Bug B + D (computed property)
- GUI test harness (Avalonia.Headless + per-page screenshots + VM regression tests)
- Post-release workflow documented ([plans/ui-testing-workflow.md](./ui-testing-workflow.md))

**🔜 Phase 1 — Bug C (Advanced UX redesign):**
- §4.3: `NetworkPage.axaml` rewrite per §3 mockup
- Новые Strings ключи (ru + en)
- Screenshot diff до/после через `PageScreenshotTests.NetworkPage`
- Ship `v2.26.4-r1` (или сразу `v2.27.0-r1` если ещё что-то зайдёт)

**Phase 2 — UI polish:**
- §4.5: spinner overlay, error surface, PID-pill более заметный
- Можно ship в том же `v2.27.0-r1`

**Phase 3 — architecture podcleanup:**
- §4.6 C2: document App = authoritative writer, grep'нуть Service на write paths (если какие-то нарушают — fix)
- §4.6 C3: state machine на `ServiceViewModel` + UI Failed-state с Retry
- Ship `v2.27.0-r2`

**Phase 4 — опционально (не в v2.27):**
- §4.6 C1 `CoordinationManager` — только если появится потребность (hot-handoff, ещё race'ы между App/Service). См. §6.3 для детального разбора.
- Promote `v2.27.0` stable после недели без фидбэка.

---

## 6.3 · CoordinationManager — что это и почему не сейчас

### Что бы он делал

Unified source-of-truth про cross-process state. Сегодня три процесса (App, CLI, Service) каждый держит локальную копию "VPN жив?", "кто владеет sing-box?", "какой профиль активен?" и синхронизируются через polling + эвристику. CoordinationManager заменит это на push-based state share через `%ProgramData%\VPNRouter\coordination.json` + FileSystemWatcher.

**API эскиз:**
```csharp
public class CoordinationManager : IDisposable
{
    public static CoordinationManager Instance { get; }
    public CoordinationState Current { get; }

    public event Action<CoordinationState, CoordinationState>? StateChanged;
    public event Action<int, int>? OwnershipChanged;  // oldPid, newPid
    public event Action? SettingsChanged;
    public event Action<string>? ComponentStarted;   // "VPN" / "Zapret" / "TgProxy"
    public event Action<string>? ComponentStopped;

    public bool TryUpdateVpnState(bool up, int? singBoxPid, string profile, string server);
    public bool TryUpdateServiceState(bool installed, bool running, int? servicePid);
}
```

**Shared state:**
- TUN owner PID + process name
- Service installed + running + PID
- VPN up + sing-box PID + active profile + active server
- Zapret up, TgProxy up
- Settings hash (для detect'а SettingsChanged без передачи всего объекта)

**Transport**: JSON file + exclusive FileStream writes + FileSystemWatcher notify. Latency ~100-500ms, кросс-сессионно (service session 0 ↔ user session 1), debuggable (`cat coordination.json`).

### Плюсы

1. **Убирает polling** — `ServiceViewModel.Refresh()` сейчас дёргается каждые 30с + на каждый UI event. С coordination — push событие по факту изменения.
2. **Single source of truth** — "VPN up?" имеет один ответ от всех процессов.
3. **Enables hot-handoff** — Service может принять VPN от App без downtime при закрытии окна.
4. **Упрощает `DetectServiceManagedVpn`** — сейчас App угадывает владельца через parent process scan. С coordination — `TunOwnerPid` явный.
5. **Test surface** — `CoordinationManager.InMemoryMock()` для unit-тестов, можно гонять install→start→crash→recover без реального `sc.exe`.

### Минусы

1. **~500-800 строк нового кода** — state model, writer с exclusive-lock, watcher с event coalescing, rebuild-from-live на corruption.
2. **Migration risk** — `RuntimeStatusDetector`, `engine.IsRunning`, `DetectServiceManagedVpn`, `ServiceViewModel.Refresh`, `SmpAutostartChecked` — все callers трогать.
3. **Failure modes** — `coordination.json` corrupted, writer crashed mid-write, watcher skip из-за OS throttling. Каждый edge нужно обработать.
4. **Latency vs complexity tradeoff** — FileSystemWatcher 100-500ms vs named EventWaitHandle + MMF <1ms но ещё 200 строк.
5. **YAGNI** — сегодняшние баги решаются точечно (Bug A = 15 строк). CoordinationManager — general решение для race'ов, которые ещё не проявились.
6. **Debugging distributed state** — stack trace при баге пересекает 3 процесса.

### Когда стоит делать

**Триггеры для v2.28+**:
- Hot-handoff становится фичей (VPN без downtime при App close).
- Ещё 2-3 race'а между App+Service всплывают в user-фидбеке (а не теоретических).
- Нужна Observer mode (вторая GUI инстанция mirror'ит state).

**Сейчас (v2.27)**: минимальное решение через TunLock + FileSystemWatcher на yaml уже покрывает известные симптомы. Не инвестируем в generic coordination layer пока не появится 2-3 конкретные проблемы, которые **только** он решит.

---

## 7 · Risk (обновлено)

**Закрытые риски:**
- ~~Рефакторинг autostart чекбоксов сломает персистированные настройки~~ — Bug B fix бекc-compat: computed property читает те же `AutostartVpn` + ServiceVm fields, никаких migration'ов не требует.
- ~~`SmpAutostartChecked → computed` может спровоцировать infinite PropertyChanged loops~~ — mitigated `_isLoadingUI` guard + regression test.
- ~~Bug A hypothesis selection без логов~~ — закрыто live-репро, настоящая причина не из 4 гипотез.

**Активные риски:**
- **Bug C copy rewrite** — user пишет по-русски, новые Strings требуют ru+en консистентности. Mitigation: pull-request review перед merge.
- **§4.6 C3 state machine** — может enumerовать новые UI states, которые не покрыты screenshot тестами. Mitigation: добавить screenshot variants после каждого state transition.
- **VM test дрейф** — тесты pass на чистой VM, fail если service уже installed (хит v2.26.3-r1 при локальном repro). Mitigation: force known state at test start (уже применено в `SmpAutostartChecked_ReactsToAllThreeInputs`).

---

## 8 · Файлы для чтения перед продолжением

**Для §4.3 (Bug C UX rewrite):**
- `VPNRouter.App/Views/Pages/NetworkPage.axaml` — current Advanced Autostart section (строки 224-330, до моих правок Bug B)
- `VPNRouter.App/Localization/Strings.cs` — patterns для новых ключей
- `VPNRouter.Tests/screenshots/page-network.png` — baseline before redesign

**Для §4.5 (UI polish):**
- `VPNRouter.App/Styles/Tokens.axaml` — Arctic theme, нужные `WarningSurfaceBrush` / `Accent*`

**Для §4.6 C2 (write-path audit):**
- `VPNRouter.Service/VPNRouterService.cs` — grep на `File.WriteAllText|SettingsLoader|yaml|Save`

**Для §4.6 C3 (state machine):**
- `VPNRouter.App/ViewModels/ServiceViewModel.cs` — `ToggleAutostartAsync`

---

## 9 · Вопросы к user (обновлено)

**Закрытые live-тестом v2.26.3-r1:**
1. ~~Какой момент точно был disconnect~~ — ответ: ~3 сек после toggle, когда Service грохнул sing-box через `Program.cs:35`.
2. ~~Слетела ли сеть вообще или только UI~~ — реальный disconnect, sing-box был убит (exit code -1), не cosmetic UI bug.
3. ~~Через какой путь был VPN~~ — Subscribe mode, 6 серверов, active `de-01 443 main-brat` (104.194.156.93:443 Reality).

**Остались:**
4. **Bug C mockup §3** — одобряешь в текущем виде (два блока "На старте Windows" / "При входе пользователя")? Или хочешь другую иерархию / формулировки?
5. **Simple checkbox label** — текущий "Start with Windows" / "Запускать с Windows" — ок? Или надо точнее "Запускать VPN при старте Windows"?
6. **Sub-components (VPN / Zapret / Telegram) в Advanced** — показывать как текущее "Autostart VPN / Autostart Zapret / Autostart TgProxy" или как часть Service section?
