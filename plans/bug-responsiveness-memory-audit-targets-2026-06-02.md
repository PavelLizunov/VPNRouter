# VPNRouter: куда смотреть при поиске багов, задержек и утечек ресурсов

**Дата:** 2026-06-02  
**Статус:** рабочая карта аудита, без реализации исправлений  
**Область:** Desktop Avalonia, Android Avalonia, Core, фоновые сервисы, внешние данные

## Зачем нужен этот файл

В проекте уже есть большая regression-suite и отдельные планы по Applications,
Public Configs и границам пользовательского взаимодействия. Следующий полезный
шаг - не искать баги случайными кликами, а проверять наиболее рискованные
контуры под контролируемой нагрузкой.

Этот документ отвечает на четыре вопроса:

1. Какие модули проверять в первую очередь.
2. Где статически уже видны подозрительные места.
3. Какими сценариями отличать функциональный баг, лаг, разовый пик памяти и
   настоящую утечку.
4. Какие метрики снимать до любых оптимизаций.

Связанные документы:

- `plans/interaction-contracts/README.md`
- `plans/user-interaction-boundaries-and-edge-case-verification-framework-2026-06-02.md`
- `plans/interaction-contracts/APP-applications-routing.md`
- `plans/interaction-contracts/FC-public-configs.md`
- `plans/public-configs-pipeline-audit-and-hardening-plan-2026-06-02.md`
- `plans/vpnrouter-memory-research.md`

`plans/vpnrouter-memory-research.md` остается полезным историческим baseline, но
его выводы нельзя переносить механически: интерфейс и пайплайны заметно выросли.

## Главный принцип проверки

Для каждого подозрения сначала фиксируем один из четырех классов:

| Класс | Что это значит | Что делать |
|---|---|---|
| Подтвержденный статический кандидат | В коде виден конкретный незакрытый ресурс, повторная подписка или неограниченная работа | Добавить regression-тест или счетчик, затем исправлять |
| Гипотеза производительности | Код потенциально дорогой, но реальный ущерб зависит от размера данных и устройства | Профилировать на нескольких размерах данных |
| Допустимый разовый пик | После тяжелой операции память выросла, затем стабилизировалась | Зафиксировать бюджет, не маскировать ручным `GC.Collect` без необходимости |
| Утечка или накопление | После одинаковых циклов растут память, handles, threads, подписки, таймеры или дочерние процессы | Искать владельца ресурса и отсутствующий teardown |

Оптимизация без измерения здесь особенно опасна: можно скрыть симптом, добавить
фриз или сломать lifecycle, не устранив источник накопления.

## Первые проверки

Если времени мало, начать с этих семи проверок:

1. **Desktop handle soak:** 100 циклов Connect / Disconnect и 100 открытий
   проверок Public Configs. Снимать `HandleCount`, private bytes и число
   `sing-box` / `winws` процессов.
2. **Android Servers stress:** импортировать 20, 100 и 500 серверов, запустить
   `Test all`, измерить длительность UI-thread блокировок и число перестроений
   строк.
3. **Desktop Public Configs peak:** поиск с живым pool, отдельно измерить
   дочерние verifier-процессы, CPU, private bytes и паузу после завершения.
4. **Cold start Desktop:** сравнить запуск в Simple mode и Advanced mode,
   открыть каждую вкладку по одному разу, снять allocation trace.
5. **Android app picker:** 100, 500 и 2000 приложений, поиск посимвольно,
   переключение `System apps`, поворот экрана и повторное открытие.
6. **Lifecycle recreation:** 50 пересозданий desktop ViewModel / Android
   `AndroidApp`; проверить, что таймеры, статические подписки и bitmap-кеши не
   накапливаются.
7. **External-input bounds:** большие и поврежденные subscription, custom JSON,
   Public Configs pool и cache; проверить bounded work и сохранение
   last-known-good.

## Подтвержденные статические кандидаты

Это не полный список доказанных пользовательских багов. Это места, где код уже
дает конкретную причину для направленной проверки.

### P0. Неосвобожденные `Process` после `GetProcessesByName`

`Process.GetProcessesByName(...)` возвращает массив объектов `Process`.
Каждый объект надо освобождать, даже если нужен только `Length`. Центральный
`RuntimeStatusDetector` уже делает это правильно и имеет
`RuntimeStatusDetectorHandleLeakTests`, но несколько обходных путей остались:

| Место | Подозрительный путь |
|---|---|
| `VPNRouter.App/ViewModels/MainWindowViewModel.cs:2978` | `DetectServiceManagedVpn()` проверяет `sing-box` через `.Length > 0` |
| `VPNRouter.App/ViewModels/MainWindowViewModel.cs:4331` | `IsZapretRunning()` проверяет `winws` через `.Length > 0` |
| `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1561` | `IsMainVpnActive()` проверяет `sing-box` перед deep verify |
| `VPNRouter.Core/Services/ZapretManager.cs:39` | `IsWinwsRunning()` проверяет `winws` через `.Length > 0` |
| `VPNRouter.Core/Services/ZapretActions.cs:125` | диагностика конфликтующих процессов |
| `VPNRouter.Core/Services/ZapretActions.cs:143` | диагностика `winws` |

Проверка:

- снять `HandleCount` до и после 1, 10, 100 и 500 одинаковых вызовов;
- проверить путь с отсутствующим процессом и путь с одним живым процессом;
- добавить статический grep-check или общий helper, чтобы новые `.Length > 0`
  не появлялись снова.

### P0. Android Servers и Subscriptions полностью перестраивают строки

В Android advanced UI строки серверов создаются программно:

- `VPNRouter.Android/AndroidApp.ServerList.cs:921`
- `VPNRouter.Android/AndroidApp.ServerList.cs:1415`
- `VPNRouter.Android/AndroidApp.SubscribePage.cs:490`
- `VPNRouter.Android/AndroidApp.SubscribePage.cs:803`

После каждого результата probe вызывается полное
`Children.Clear()` + повторное создание всех строк. В `Test all` результаты
приходят многократно, а часть путей дополнительно вызывает финальное
перестроение. При `N` серверах это может дать порядок `O(N^2)` созданий
контролов и заметную очередь работ на UI dispatcher.

Проверка:

- размеры списка: `0`, `1`, `20`, `100`, `500`, `1000`;
- операции: открыть вкладку, выбрать сервер, проверить один сервер,
  `Test all`, отменить `Test all`, сменить сортировку во время проверки;
- измерять: время первого рендера, p95/p99 задержки тапа, число
  `RebuildServerList()` / `RebuildAggregatedServerList()`, allocations,
  dropped frames и память после завершения;
- проверить, не продолжают ли отложенные dispatcher-posts перестраивать уже
  закрытую вкладку.

Вероятное направление будущей доработки: обновлять только измененную строку
или перейти на `ItemsSource` + виртуализацию. До замера не реализовывать.

### P0. Android Public Configs может создать очередь UI-обновлений

`VPNRouter.Android/AndroidFreeConfigsOrchestrator.cs` идет по queue до
достижения target Verified, исчерпания pool или отмены. Фиксированного лимита
проверенных batch нет. Это допустимая продуктовая семантика, но она требует
ресурсного бюджета.

`VPNRouter.Android/AndroidApp.FreeConfigs.cs:1221` публикует каждый найденный
TCP/TLS-кандидат отдельным `Dispatcher.UIThread.Post`. Дедупликация ищет `Id`
линейным проходом по `_fcSearchResults`, а видимый список кандидатов не имеет
явного cap. Если deep verify часто не проходит, количество слабых кандидатов
может стать большим: растут очередь dispatcher, память и стоимость
дедупликации.

Проверка:

- pool: `0`, `200`, `5000`, живой pool, синтетические `45000+`;
- доля TCP/TLS Ok: `0%`, `10%`, `100%`;
- доля deep Verified: `0%`, `1%`, `50%`;
- отмена сразу после fetch, в середине TCP/TLS batch и в середине deep verify;
- измерять число опубликованных строк, размер dispatcher queue, latency кнопки
  Stop, память и время восстановления UI после cancel.

Вероятное направление будущей доработки: `HashSet<string>` для dedupe,
батчевое/throttled обновление UI и отдельный cap видимых weak-кандидатов.

### P1. Повторные подписки desktop-страниц на `ActiveServerChanged`

`VPNRouter.App/Views/Pages/ServersPage.axaml.cs:17` и
`VPNRouter.App/Views/Pages/SubscribePage.axaml.cs:15` подписываются на
`MainWindowViewModel.ActiveServerChanged` при каждом `DataContextChanged`, но
не снимают подписку со старого ViewModel.

В обычном процессе `DataContext` может назначаться редко, поэтому это не
обязательно заметно пользователю сегодня. Но при пересоздании окна,
headless-тестах, смене host-контекста или будущем reload это способно удерживать
старый VM и дублировать `ScrollIntoView`.

Проверка:

- 50 раз заменить `DataContext` страницы между двумя VM;
- вызвать `ActiveServerChanged`;
- проверить число обработчиков, число `ScrollIntoView` и возможность сборки
  старого VM через `WeakReference`.

### P1. `SingBoxManager` регистрирует захватывающий `ProcessExit` lambda

`VPNRouter.Core/Services/SingBoxManager.cs:200` подписывает lambda на
`AppDomain.CurrentDomain.ProcessExit`. Lambda захватывает поля экземпляра.
`Dispose()` освобождает TUN lock, но не может снять анонимную подписку.

При одном manager на жизнь процесса ущерб ограничен. Если manager
пересоздается после lifecycle-ошибок, в тестовом harness или в будущем host
reload, старые экземпляры могут удерживаться до завершения процесса.

Проверка:

- создать и dispose 100 manager-экземпляров с fake runner;
- держать `WeakReference` на экземпляры;
- выполнить full GC в тесте и проверить, какие экземпляры остаются живы;
- отдельно проверить число fallback-вызовов при завершении тестового host.

Вероятное направление будущей доработки: named handler + unsubscribe в
`Dispose()`, либо один статический coordinator без захвата экземпляра.

**Статус (2026-06-02): исправлено.** Анонимная lambda заменена на named
`OnAppDomainProcessExit`, `Dispose()` делает
`AppDomain.CurrentDomain.ProcessExit -= OnAppDomainProcessExit`. Поведение
сохранено (gate на `_disposed` тот же). Покрыто
`SingBoxManagerProcessExitLeakTests` (WeakReference: 25 create/dispose → все
собраны GC + source-pin на unsubscribe). Существующие B1 source-pins
(`SingBoxManagerCleanupPathTests`) не задеты.

### P1. Владелец `HttpClient` у Public Configs не выражен teardown-контрактом

`FreeConfigPoolFetcher`, `FreeConfigFetcher` и связанные агрегаторы создают
свои `HttpClient`. У `FreeConfigPoolFetcher` и `FreeConfigAggregator` нет
`IDisposable`, а desktop VM снимает события агрегатора, но не освобождает его
сетевые ресурсы. На Android orchestrator также создает fetcher.

При одном долгоживущем экземпляре это не авария. При recreation или повторном
создании orchestrator возможны лишние handler-пулы и удержание ресурсов.

Проверка:

- 100 циклов create / fetch-cancel / close / recreate;
- снимать sockets, threads, managed heap и число живых fetcher/orchestrator
  через profiler;
- отдельно проверить Android recreation.

**Ownership-trace (2026-06-02):** инстанцирование —
`FreeConfigsPageViewModel` ctor → один `FreeConfigAggregator` → владеет
`FreeConfigFetcher` + `FreeConfigPoolFetcher` (desktop); `AndroidFreeConfigsOrchestrator`
ctor → свой `FreeConfigPoolFetcher` (Android). Оба `HttpClient` — instance-owned,
живут пока жив владелец. Утечка материализуется ТОЛЬКО при recreation владельца
(пересоздание окна desktop / `AndroidApp`/orchestrator Android), что редко. При
одном долгоживущем экземпляре — не авария (как и сказано выше).

**Почему не чинится мимоходом / unsupervised:** есть design-развилка и footgun:
(1) `FreeConfigPoolFetcher` требует `AutomaticDecompression=None` (ручной gzip
для `pool.json.gz`), поэтому не может просто шарить `PolicyHttpClient.Shared`;
(2) его ctor принимает inject-able `HttpClientHandler` (test-seam) — static
shared client ломает этот seam; (3) частый `Dispose()` HttpClient → socket
exhaustion (классический .NET footgun), поэтому "добавить IDisposable +
dispose-per-use" неверно. Корректные варианты: **(A)** `private static readonly
HttpClient` на класс (reuse, без churn) с сохранением test-seam через отдельный
internal-ctor; **(B)** IDisposable-цепочка `Aggregator`/`Orchestrator` →
fetchers, с dispose ТОЛЬКО на teardown владельца (VM teardown уже снимает
события — туда же повесить dispose). Решение + выбор A/B принять с user'ом
(нужна валидация lifecycle + test-seam). LOW priority до подтверждённого
recreation-сценария на профайле.

### P1. `AppIconCache` ограничен по количеству, но требует проверки native teardown

`VPNRouter.Android/AppIconCache.cs` ограничивает кеш 200 bitmap-объектами, что
хорошо. Но при eviction, `Clear()` и гонке двойной конвертации ссылка на
`Avalonia.Media.Imaging.Bitmap` удаляется без явного dispose. Нужно проверить,
освобождается ли native память достаточно быстро finalizer-механизмом.

Проверка:

- 20 циклов открыть picker, включить/выключить system apps, прокрутить,
  закрыть picker;
- использовать набор `500+` и `2000+` приложений;
- снимать Java heap, native heap, graphics memory и `AppIconCache.Count`;
- проверить поворот экрана / recreation.

Вероятное направление будущей доработки: явный dispose вытесненных bitmap,
если profiler подтвердит удержание.

## Главные гипотезы по скорости отклика и памяти

### P0. Desktop Public Configs: verifier-процессы и принудительный GC

`VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs` держит
несколько материализованных списков во время поиска (`pool`,
`cachedVerified`, `queue`, slices, in-flight batches). Это уже существенно
лучше старого полного deep-verify всего pool, но живой pool остается большим.

Deep verify масштабирует параллелизм по CPU до `8` процессов. В комментарии
модуля зафиксирована оценка около `50 MB RSS` на временный `sing-box`, то есть
только дочерние процессы способны дать пик около `400 MB`.

После поиска вызывается `ReclaimPostSearchMemory()`:

- LOH compaction;
- два blocking gen-2 GC;
- `GC.WaitForPendingFinalizers()`;
- `SKGraphics.PurgeAllCaches()`.

Если вызов продолжает исполняться на UI context, пользователь может увидеть
заметный фриз ровно в момент завершения или отмены поиска.

Проверка:

- caps verifier concurrency: `1`, `3`, `5`, `8`;
- живой pool и синтетические `5000`, `45000`, `100000`;
- измерять отдельно память главного процесса и дерева дочерних процессов;
- замерить длительность `ReclaimPostSearchMemory()`, max UI stall и задержку
  Stop;
- сравнить устойчивое warm-idle состояние через 30 секунд и 5 минут после
  поиска.

Нужен выбор по данным: ограничение параллелизма не только по CPU, но и по
доступной памяти; перенос тяжелой reclamation-работы из UI thread; отказ от
части ручного GC, если он дает больше лагов, чем пользы.

### P1. Desktop создает все страницы сразу

`VPNRouter.App/Views/MainWindow.axaml:761` создает шесть advanced-страниц в
одном Grid и скрывает неактивные через `IsVisible`. `SimplePage` тоже живет в
том же окне. `ToolsPage` сразу создает DPI bypass, Telegram и Emergency
Channel. `NetworkPage.axaml` уже содержит больше 2200 строк XAML.

Это не баг само по себе, но может увеличивать:

- холодный запуск;
- initial allocations;
- количество bindings;
- стоимость полной localization/theme invalidation;
- удержание тяжелых поддеревьев, которыми пользователь не пользуется.

Проверка:

- холодный старт в Simple mode и Advanced mode;
- baseline до первого клика и после открытия каждой вкладки;
- allocation trace и render timing;
- пробный локальный branch с lazy creation только для измерения, без
  обязательного внедрения.

### P1. Desktop runtime-status poll делает синхронную работу на UI dispatcher

`VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs` запускает
`DispatcherTimer`: активно раз в 2 секунды, в idle адаптивно до 8 секунд.
Проверка процессов централизована и освобождает handles, но все равно требует
системных вызовов. Раз в минуту на том же пути вызывается
`SKGraphics.PurgeAllCaches()`.

Проверка:

- оставить приложение открытым на 20 минут в idle;
- непрерывно измерять click-to-render latency и UI thread stalls;
- сопоставить пики с минутными purge;
- отдельно проверить idle с VPN, Zapret и TgProxy в разных комбинациях.

### P1. Desktop `SaveSettings()` синхронно пересобирает и пишет весь YAML

`VPNRouter.App/ViewModels/MainWindowViewModel.cs:3593` вызывается из многих
UI-обработчиков. Метод:

- делает backup;
- пересобирает subscriptions, servers, custom configs, applications,
  categories и rules;
- парсит custom rules;
- синхронно сериализует и пишет `config.yaml`.

Для обычного профиля это дешево. Для больших subscriptions, categories и rule
lists серия checkbox-изменений может дать лаги и лишние записи на диск.

Проверка:

- наборы `10`, `100`, `1000` servers; `10`, `100`, `1000` custom rules;
  `100`, `1000`, `3000` apps;
- быстро переключить 20 checkbox;
- измерять UI stall, количество save-вызовов, размер YAML и длительность write;
- проверить, не теряется ли последнее изменение при debounce, если он будет
  предложен после замера.

### P1. Android app picker заранее строит `Control` для каждой строки

`VPNRouter.Android/AndroidApp.axaml.cs:3736` создает `List<Control>` для всех
отфильтрованных приложений и назначает его в `ItemsSource`. При каждом символе
поиска строки строятся заново. Даже если сам `ListBox` умеет виртуализацию,
преимущество снижается: контролы уже созданы заранее.

`AppIconCache` ограничивает bitmap-кеш, но PNG-конвертация и создание строк
все равно могут быть дорогими.

Проверка:

- `100`, `500`, `2000`, `5000` приложений;
- быстрый ввод 10 символов, backspace, смена категории, system-app toggle;
- измерять p95 задержки ввода, allocations, native graphics memory и GC;
- проверить, не завершает ли старый async reload работу поверх нового фильтра.

### P1. Public Configs pool bounded по размеру файла, но парсится целиком

`VPNRouter.Core/Services/FreeConfigs/FreeConfigPoolFetcher.cs` уже имеет хорошие
границы:

- compressed cap: `32 MB`;
- expanded cap: `128 MB`;
- temp file;
- parse перед заменой last-known-good;
- atomic overwrite cache.

После распаковки используется `JsonDocument.Parse(stream)`, затем строится
полный `List<FreeConfigEntry>`. Пиковая память включает DOM, строки и итоговый
список. Это не обязательно проблема, но это первая точка для gcdump при
большом pool.

Проверка:

- валидный pool: `1 MB`, живой pool, `64 MB`, `127 MB`;
- превышение expanded cap;
- truncated gzip, malformed JSON, пустой `servers`, duplicate-heavy pool;
- 304 при существующем и отсутствующем cache;
- параллельные fetch на общий `.tmp`.

Если DOM окажется значимой частью пика, рассмотреть streaming parse через
`Utf8JsonReader`.

### P1. `FreeConfigCache.Save()` имеет память и crash-window для проверки

`VPNRouter.Core/Services/FreeConfigs/FreeConfigCache.cs:119` сначала
сериализует весь cache в одну строку, пишет `.tmp`, удаляет старый файл и
перемещает новый. Стоит проверить:

- LOH allocation большой JSON-строки;
- синхронный save на UI-path;
- crash после delete и до move;
- два конкурентных writer на одном `.tmp`;
- сохранение last-known-good при ошибке диска.

### P2. Android diagnostics timer и background lifecycle

`VPNRouter.Android/AndroidApp.VpnLifecycle.cs` уже содержит защиту от прежней
утечки: static subscriber заменяется, прошлый экземпляр отсоединяется, timer
освобождается. Diagnostics timer работает раз в секунду во время соединения, а
health probe раз в 30 секунд читает metadata log-файла.

Нужно не переписывать это вслепую, а подтвердить поведение на устройстве:

- connected idle foreground 30 минут;
- connected background 30 минут;
- disconnect;
- 20 recreation;
- проверить CPU, battery profiler, timer count и отсутствие фоновых UI writes.

### P2. `HealthMonitor.Start()` стоит проверить на повторный вызов

`VPNRouter.Core/Services/HealthMonitor.cs:174` создает новый health timer и
`PowerEventListener`. `Stop()` освобождает их корректно. Но `Start()` сам по
себе не вызывает `Stop()` и не отказывает при уже запущенном monitor.

Если lifecycle гарантирует строго один Start перед Stop, это допустимый
precondition. Если нет, старые timer/listener могут сохраниться.

Проверка:

- `Start -> Start -> Stop`;
- `Start -> crash -> restart -> Stop`;
- resume/unlock storm;
- зафиксировать контракт: либо repeated Start запрещен и защищен guard, либо
  идемпотентен.

**Статус (2026-06-02): исправлено — выбран идемпотентный контракт.** `Start()`
теперь при `_healthTimer != null || _powerListener != null` логирует warning и
вызывает идемпотентный `Stop()` перед ре-инициализацией, чтобы не оставить
осиротевшими старый `Timer` и (важнее) `PowerEventListener` с его подпиской на
Windows `SystemEvents`. Покрыто `HealthMonitorStartIdempotencyTests`
(discriminating: старый listener `_disposed==true` после 2-го Start + source-pin
на guard). Обычный первый Start (оба поля null) не задет.

### P2. Большие внешние ответы и локальные импорты требуют общего лимита

Public Configs pool уже bounded. Несколько соседних путей по-прежнему читают
данные целиком:

- `VPNRouter.Core/Services/SubscriptionFetcher.cs`
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigFetcher.cs`
- `VPNRouter.Core/Services/StartupPipeline.cs`
- `VPNRouter.Core/Services/VpnEngine.cs`
- `VPNRouter.Core/Services/CustomConfigInjector.cs`
- `VPNRouter.Core/Services/ProfileManager.cs`

Проверка:

- ответы и файлы `0 B`, `1 KB`, типичные, `10 MB`, `100 MB`, malformed;
- медленный body, зависший body, cancellation;
- gzip/base64 с большой распаковкой;
- prior-good settings/config не должны стираться при reject.

Нужен общий policy: лимит bytes, лимит decoded bytes, max items, max nesting,
timeout, cancellation и понятная ошибка пользователю.

## Функциональные модули для направленного bug-аудита

### Tier A: безопасность и жизненный цикл VPN

| Модуль | Что проверять |
|---|---|
| `VpnEngine`, `StartupPipeline`, `SingBoxManager` | Start / Stop / Restart / hot reload, ошибки на каждой фазе, TUN ownership, orphan cleanup, late process-exit callback |
| `HealthMonitor`, `PowerEventListener` | crash recovery, backoff, cancel restart, повторный Start, resume/unlock storm, shutdown во время callback |
| `FirewallManager`, DNS lockdown, leak validation | fail-closed, rollback, cleanup orphan rules, service-owned VPN, частичный отказ OS-команды |
| Desktop app + Windows Service + CLI | конкурентные владельцы TUN, устаревший runtime state, запуск UI поверх service, disconnect и повторный connect |

Минимальные последовательности:

- `Connect -> Stop` во время каждой startup-фазы;
- `Connect -> routing mode flip -> Stop`;
- `Connect -> kill sing-box -> auto-restart -> Stop`;
- `Service owns TUN -> open UI -> Connect -> Disconnect`;
- `Sleep -> resume -> network change -> health probe`;
- `app crash -> relaunch -> cleanup -> reconnect`.

### Tier A: конфиги, подписки и атомарное принятие данных

| Модуль | Что проверять |
|---|---|
| `SettingsLoader`, migrations, watcher | corrupt YAML, частичная запись, duplicate watcher events, reload во время save, safe mode read-only |
| `SubscriptionFetcher`, subscription ViewModels | malformed / huge / empty ответ, placeholder-only, дубликаты, ошибка refresh, сохранение prior-good |
| custom JSON import / injection | неверный JSON, огромный файл, missing path, неподдерживаемые outbounds, секреты в ошибках |
| remote rules, GeoIP, updater assets | bounded download, checksum, atomic replace, stale cache, отмена и повторный запуск |

Для каждого внешнего replacement действует инвариант:

```text
download/read candidate -> bound -> parse -> validate -> persist atomically -> adopt
```

Prior-good нельзя удалять до успешной валидации candidate.

### Tier A: Public Configs

Использовать отдельный контракт `plans/interaction-contracts/FC-public-configs.md`.
Дополнительно прогнать:

- поиск при включенном основном VPN;
- cancel на каждой async-фазе;
- repeated Find / Stop / Find;
- clear saved и remove во время idle;
- пустой cache и corrupt cache;
- pool обновился во время поиска;
- Android recreation во время deep verify;
- сеть пропала и вернулась;
- мало RAM / много CPU cores.

### Tier A: Applications routing

Использовать `plans/interaction-contracts/APP-applications-routing.md`.
Проверять не только страницу, но и путь до generated sing-box config:

- include / exclude и переключение режима с выбранными приложениями;
- пустой список;
- дубликаты с разным регистром;
- `.exe`, имя процесса и путь;
- удаленный executable после добавления;
- дочерний процесс;
- приложение стартовало до VPN и после VPN;
- service-owned VPN;
- 1000+ приложений и быстрый поиск;
- сохранение custom categories;
- Apply, restart и relaunch.

### Tier B: Android UI

| Модуль | Что проверять |
|---|---|
| Server list / subscriptions | большие списки, test-all, cancel, сортировка, repeated rebuild |
| app picker | большие package lists, иконки, поиск, категории, system apps, recreation |
| `AndroidStorage` | большие JSON blobs, synchronous `Commit()`, quarantine, migration, сбой записи |
| static lifecycle events | attach/detach, rotation/recreation, background/foreground, timer cleanup |
| config share / QR | большие payload, malformed payload, prior-good, secret hygiene |

### Tier B: Desktop UI

| Модуль | Что проверять |
|---|---|
| eager pages + Network / Tools | cold start, hidden-tree memory, theme/language switch |
| `SaveSettings()` | checkbox storm, большие списки, диск медленный / read-only |
| servers / subscribe pages | DataContext recreation, selection, `ScrollIntoView`, virtualization |
| runtime status | polling, process handles, minute Skia purge, stale badge |
| update notification / tray / single instance | static events, window recreation, quit cleanup |

### Tier C: вспомогательные подсистемы

- Zapret / TgProxy: process lifetime, handles, stdout/stderr buffers, updater,
  диагностика, многократный start-stop.
- Emergency Channel: сетевые таймауты, process teardown, скрытые фоновые задачи.
- diagnostics exporter: большие логи, redaction, bounded tail, ошибка диска.
- macOS/Linux stop escalation: bounded wait, cancellation, внешние процессы,
  отсутствие зависания UI.

## Матрица нагрузок

Вместо одного "реалистичного" набора данных использовать одинаковую лестницу:

| Ось | Малый | Типичный | Большой | Враждебный |
|---|---:|---:|---:|---:|
| Servers | 0-1 | 20 | 500 | 2000 |
| Subscriptions | 0-1 | 5 | 50 | 200 с дубликатами |
| Apps | 0-10 | 150 | 2000 | 5000 |
| Custom rules | 0-1 | 50 | 1000 | 10000 malformed/mixed |
| Public Configs pool | 0-200 | live pool | 45000+ | 127 MB expanded |
| Saved Public Configs | 0 | 10 | 300 | duplicate/stale/corrupt |
| Повторы lifecycle | 1 | 10 | 100 | 500 с fault injection |

Отдельно комбинировать:

- медленный диск;
- read-only диск;
- плохую сеть;
- отмену;
- закрытие окна;
- Android background / recreation;
- service-owned VPN;
- недостаток памяти;
- много CPU cores.

## Что измерять

### Desktop

Снимать минимум:

- UI action latency: p50 / p95 / p99;
- cold launch и first-render;
- CPU average и пики;
- managed heap, LOH, private bytes, working set;
- gen-2 GC count и pause duration;
- `HandleCount`, threads;
- число дочерних `sing-box`, `winws`, `python`;
- размер очередей и коллекций feature-модуля.

Базовая PowerShell-сводка:

```powershell
Get-Process VPNRouter.App,sing-box,winws,python -ErrorAction SilentlyContinue |
  Select-Object ProcessName, Id, CPU, WorkingSet64, PrivateMemorySize64, HandleCount,
    @{Name='Threads';Expression={$_.Threads.Count}}
```

Для managed-диагностики:

```powershell
dotnet-counters monitor --process-id <PID> System.Runtime
dotnet-gcdump collect --process-id <PID>
dotnet-trace collect --process-id <PID>
```

### Android

Снимать минимум:

- frame timing / jank;
- Java heap, native heap, graphics memory;
- CPU и battery profiler;
- GC frequency;
- число bitmap / visual rows;
- время реакции Stop / Back;
- память до и после rotation/recreation;
- фоновые timers и продолжение работы после закрытия вкладки.

Базовые команды:

```bash
adb shell dumpsys meminfo <package>
adb shell dumpsys gfxinfo <package>
adb shell top -H -p <PID>
```

## Как отличать пик памяти от утечки

Для каждого тяжелого сценария использовать одинаковый протокол:

1. Запустить приложение и дать ему прогреться 2 минуты.
2. Снять baseline.
3. Выполнить один цикл операции.
4. Снять peak.
5. Подождать 30 секунд и 5 минут.
6. Повторить цикл 10 и 100 раз.
7. Сравнить warm-idle после каждого блока.
8. Снять gcdump / profiler snapshot, если baseline ползет вверх.

Сигналы утечки:

- warm-idle память монотонно растет после одинаковых циклов;
- `HandleCount` растет ступеньками;
- остаются дочерние процессы;
- растет число timer, event subscriber, bitmap или control;
- после закрытия feature-страницы ее VM остается достижимым;
- cancel не останавливает рост ресурсов;
- GC уменьшает managed heap, но native/private bytes продолжают расти.

## Предлагаемые стартовые бюджеты

Это не окончательные SLA. Сначала сверить на слабом Windows-устройстве и
бюджетном Android, затем закрепить реалистичные значения.

| Метрика | Стартовый ориентир |
|---|---|
| Обычный тап / переключение вкладки | p95 до `100 ms`, без пауз более `250 ms` |
| Реакция UI на Stop / Cancel | визуальная реакция до `250 ms`, фактическая отмена bounded контрактом операции |
| Desktop idle CPU | обычно менее `1%` в прогретом idle |
| Handles после 100 одинаковых циклов | без монотонного роста |
| Threads после 100 одинаковых циклов | возвращаются к baseline с небольшим стабильным допуском |
| Warm-idle память после 10 циклов | стабилизируется; рост после следующих циклов не монотонный |
| Android background | нет фонового перестроения UI и необъяснимого постоянного CPU |
| Большие внешние данные | reject bounded по bytes/items/time, prior-good сохранен |

## Порядок аудита

### Этап 1. Быстрые ресурсные дефекты

- sweep всех `GetProcessesByName`;
- тесты handles;
- подписки desktop-страниц;
- `SingBoxManager.ProcessExit`;
- Public Configs ownership `HttpClient`;
- bitmap teardown Android.

### Этап 2. Android latency

- instrument rebuild counters;
- Servers / Subscribe `Test all`;
- app picker;
- Public Configs weak-candidate storm;
- background и recreation.

### Этап 3. Desktop latency и memory

- eager page construction;
- runtime-status purge;
- `SaveSettings()` storm;
- Public Configs process-tree peak и post-search GC;
- theme/language при больших списках.

### Этап 4. Fault injection и bounded work

- большие / malformed network bodies;
- cache crash-window;
- параллельные temp writers;
- slow disk;
- cancel на каждой фазе;
- prior-good после ошибки.

### Этап 5. Контракты и regression-suite

Для каждого подтвержденного дефекта:

1. Зафиксировать feature contract или дополнить существующий.
2. Добавить тест на самый дешевый достаточный слой `L0-L5`.
3. Добавить метрику, если дефект проявляется только в soak.
4. Исправить.
5. Повторить boundary-сценарий и соседние действия.
6. На release пройти полный пользовательский flow до видимого результата.

## Definition of done для этого направления

- [x] Нет продуктовых `GetProcessesByName(...).Length` без освобождения объектов.
  (v2.40.0-r3 — `ProcessQuery` helper + 7 sites fixed + Gate 7 grep-guard;
  see `plans/handle-leak-sweep-v2.40.0-r3-2026-06-02.md`.)
- [ ] Android server test не создает квадратичную UI-нагрузку на больших списках.
- [ ] Public Configs имеет измеренный peak budget для desktop и Android.
- [ ] Stop / Cancel Public Configs остается отзывчивым при неблагоприятных данных.
- [ ] Desktop cold start измерен; решение по lazy pages принято по данным.
- [ ] `SaveSettings()` измерен на больших профилях.
- [ ] Desktop VM / pages и Android recreation проходят leak soak.
- [ ] Bitmap cache проверен по native memory.
- [ ] Внешние body/file пути имеют явные bounds или зафиксированный backlog.
- [ ] Для Tier A модулей существуют interaction contracts или записана очередь их добавления.

