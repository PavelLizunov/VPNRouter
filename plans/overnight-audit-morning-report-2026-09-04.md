# VPNRouter: утренний отчёт глубокого аудита

Дата миссии: 2026-09-04. Исходники: `9f8c8b5a8b34f264762294f8a11842b4edab90a9`.
Все номера строк ниже относятся к этому SHA, а не к будущему исправлению.

## 1. Резюме

**Главная проблема — рассогласование между подсистемами, а не отсутствие защит.**
В коде есть точная идентификация sing-box, сериализация lifecycle, ограничения
HTTP, освобождение process handles, typed readiness и разделение Unix/Windows
kill-switch. Однако потребители нередко интерпретируют более слабый сигнал как
более сильную гарантию: порт как владельца процесса; возврат Start как готовность
туннеля; возврат void Restart как применение новой конфигурации; попытку очистки
firewall как подтверждённую очистку.

Выбраны три направления:

1. **A: lifecycle, ownership, fail-closed DNS/firewall.** Здесь ошибка меняет
   маршрут приватных запросов, завершает чужой процесс или блокирует сеть.
2. **B: границы фоновой работы и отмена probes.** Важны пиковые ресурсы больших
   подписок и корректность health evidence, а не косметические аллокации.
3. **C: правдивость состояния UI.** Проверялась связь Connected/Applied/Running с
   реальной гарантией нижнего слоя. Пиксельная адаптивность в этот проход не вошла.

Итог: **12 подтверждённых по исходникам дефектов: 8 P1 и 4 P2; новых P0 не
установлено.** Отдельно — P3 противоречие карты подсистем и measurement-кандидаты.
Отсутствие найденного P0 не является заключением о полной безопасности продукта.

### Граница доказательств

- Прочитаны обязательные документы, история OPEN-DEFECTS, карты Core/Platform/VM,
  контракты CLI/Service и owning tests. Два независимых reviewer исследовали
  network и background/UI; третий adversarial reviewer проверил lifecycle lead.
  Lead повторно открыл исходники всех принятых находок.
- **C# regression tests для новых находок и live VPN/firewall/UI сценарии в этом
  аудите НЕ выполнялись.** Ни один рецепт ниже не выдаётся за выполненный опыт.
- На `harness-test` SDK отсутствует; запуск продукта/сборки здесь запрещён.
  Read-only preflight `linux-worker` подтвердил `debian-xfce`, пользователя tester,
  достаточные ресурсы и отсутствие dotnet. SDK/пакеты не устанавливались.
- Четыре exact-SHA проверки исходной ветки зелёные. После brief-коммита
  `e5291d09f79335212f58d71c7bcb85f26d1999a0` также прошли `test`,
  `characterization-windows`, `go-test-windows`, `grep`: workflows
  [33921346133](https://github.com/PavelLizunov/VPNRouter/actions/runs/33921346133)
  и [33921346077](https://github.com/PavelLizunov/VPNRouter/actions/runs/33921346077).
  Это существующая regression baseline, **не воспроизведение новых дефектов**.
- Web search недоступен (HTTP 401); выводы не опираются на неоткрытые внешние статьи.
- Продуктовый код, настройки инфраструктуры и живые VPN-инсталляции не менялись.
  Документация аудита доставляется через [PR #237](https://github.com/PavelLizunov/VPNRouter/pull/237).
  Ветка наследует documentation-only PR #235 с обязательными картами/миссией;
  audit-owned delta начинается после указанного исходного SHA.

### Приоритеты

| ID | Уровень | Дефект | Оценка исправления |
|---|---|---|---|
| NIGHT-01 | P1 | TgProxy: чужой listener становится целью Kill при Quit/toggle | S–M |
| NIGHT-02 | P1 | Custom WG DNS: созданный proxy detour переписывается в direct | S–M |
| NIGHT-03 | P1 | StrictDns проигрывает процессному smart-DNS правилу | S |
| NIGHT-04 | P1 | Неудачная очистка firewall удаляет recovery marker | S–M |
| NIGHT-05 | P1 | Unix firewall получает старый/пустой/неполный endpoint bypass | M |
| NIGHT-06 | P1 | AutoFailover сохраняет pool и settings прежнего intent | M |
| NIGHT-07 | P1 | Успешный StartTask отменяет Phase B без Connected | M |
| NIGHT-08 | P1 | Apply сообщает успех после отказа exact Stop/Restart | S–M |
| NIGHT-09 | P2 | Smart Connect запускает весь pool без concurrency limit | S |
| NIGHT-10 | P2 | Отмена UDP receive принимается за полученный ответ | S |
| NIGHT-11 | P2 | ConnStats бессрочно показывает прежние live-показатели | S–M |
| NIGHT-12 | P2 | Opt-in health WebSocket запускается без configured secret | S |

S/M — относительная сложность, не обещание сроков. Каждый фикс требует отдельного
regression и проверки затронутых платформ; готовые защиты ослаблять нельзя.

## 2. Реестр дефектов

### NIGHT-01 — P1: Quit может завершить чужой процесс по порту TgProxy

**Место:** `VPNRouter.App/ViewModels/MainWindowViewModel.cs:5991–6003,7015–7030`;
`VPNRouter.Core/Services/TgProxyManager.cs:530–538,560–575,605–650`;
`VPNRouter.Core/Services/RuntimeStatusDetector.cs:109–120`.

**Root cause.** Owned `_tgProxy?.Stop()` и статический `KillAll(port)` — разные
механизмы. Второй получает PID из `netstat -ano` по LISTENING/порту и вызывает
`Process.GetProcessById(pid).Kill(entireProcessTree: true)` без проверки path,
start time, module или owner record. Windows Quit вызывает его даже при null
manager и выключенном TgProxy. Toggle также выбирает ветку Stop по любому
listener. Проверка port conflict внутри Start не помогает: до Start не доходят.

Вторичное проявление того же неверного ownership: при configured secret
`MainWindowViewModel.RuntimeStatus.cs:124–130,154–166` считает чужой listener
Running, включая переход из Failed. Это не доказательство LPE: завершение
возможно только в пределах termination-прав запущенного VPNRouter.

**Воспроизведение:** в изолированной Windows fixture другой безвредный процесс
слушает настроенный TG порт; TgProxy не запускать; вызвать Quit. Предпочтительный
regression использует fake listener table и fake kill sink, не настоящее убийство.
Проверить также toggle и update (`MainWindowViewModel.cs:5872–5889`).

**Фикс:** убрать port-only/legacy-name destructive cleanup из этих callers;
останавливать только доказанный owned handle. Busy port — конфликт, не identity.
Service adoption при необходимости связывать с точным owner/child identity,
используя существующий подход ProcessOwnership, но не считая один `python.exe`
доказательством. Регрессии: foreign/null manager → zero kills, owned positive,
PID reuse negative, все три callers. Риск исправления: не потерять легитимный
service-managed Stop; решить его явным контрактом, не возвращением kill-by-port.

### NIGHT-02 — P1: custom WG/AWG split/include выводит DNS выбранного приложения из VPN

**Место:** `VPNRouter.Core/Services/CustomConfigInjector.cs:150–182,825–890,895–938,1498–1535,212–260`.

**Root cause:** resolver route tag уже поддерживает WG endpoint (`:568–579`),
но `IsLocalDetour` ищет тип только в `outbounds`. Endpoint tag считается unknown,
следовательно local. Последовательность полного Inject:

1. Include-process route указывает на WG endpoint.
2. `InjectDnsRules` при отсутствии remote DNS создаёт `vpnrouter-vpn-dns` с
   `detour=<WG endpoint>` и процессное DNS rule на этот tag.
3. Последующий `StripUnsupportedFeatures` классифицирует endpoint как local и
   меняет **собственный синтезированный** resolver на `detour=dns-direct`.
4. При split/include, StrictDns=false поздний repair `wantRemoteDns` не включён.

**Условия/рецепт:** валидный endpoint-only plain WG custom config, direct и
непустой dns-direct outbounds; существующая `dns.servers` с локальным HTTPS DNS;
явно включённый процесс `Firefox.exe`, split/include, StrictDns=false,
BypassRu=false. DNS-запрос должен атрибутироваться самому выбранному процессу,
а не системному DNS service. После полного Inject проследить
`process DNS rule → server tag → detour`.

Это **DoH privacy leak: resolver видит реальный IP**, а не доказанная передача
plaintext DNS провайдеру. Без DNS section данный путь не срабатывает. Обычный
VLESS outbound определяется корректно; full/StrictDns поздний repair исправляет
синтезированный tag. Поэтому не заявляется утечка для любого WG config.

**Фикс:** единая классификация destination по `outbounds` **и** `endpoints` для
всех DNS helpers, с fail-closed обработкой действительно неизвестного tag.
Проверять итоговый JSON после всех стадий, а не число rules. Companion tests:
WG include, VLESS include, full, StrictDns, явно direct. Исторический resolved
WG route-tag defect в OPEN-DEFECTS — другой этап, его исправление сохранено.

### NIGHT-03 — P1: StrictDns не перекрывает smart process rule

**Место:** `VPNRouter.Core/Services/ConfigGenerator.Dns.cs:32–57,134–171`;
`VPNRouter.Core/Services/LeakProtection.cs:209–235`.

**Root cause:** StrictDns меняет `dns.final` на vpn-dns, но include-rule выбирает
`profile.DnsMode == "smart" ? "local-dns" : "vpn-dns"` независимо от strict.
Специфичное process rule выигрывает у final. `local-dns` — HTTPS с
`detour=dns-direct` (`ConfigGenerator.Dns.cs:67–74`). Validator считает такую
форму допустимой и лишь выдаёт informational warning, не проверяя строгий intent.

**Почему это дефект, а не запрет smart:** `AppConfig.cs:280–294` и публичный
`Localization/Strings.cs:767–769` обещают при StrictDns **весь DNS через VPN**.
Smart без StrictDns — легитимный direct-DoH opt-in и не finding. Также не речь
о намеренном `strictDnsOverride=false` при runtime failover.

**Рецепт:** generated split/include, custom profile dns_mode=smart, выбранный
процесс с собственными DNS-запросами, StrictDns=true, исправный proxy. Сгенерировать
JSON и найти первое совпадающее DNS rule: local-dns вопреки vpn-dns final.
Exclude имеет родственную приоритетную local ветку; основной witness — include,
где и traffic, и strict intent однозначно требуют VPN.

**Фикс:** учитывать effective strictDns при построении специфичных rules:
`strictDns || profile.DnsMode != "smart" ? "vpn-dns" : "local-dns"`.
Для exclude согласовать аналогично; проверить custom exclude `InjectDnsRules`.
Тест-матрица strict on/off × smart/include/exclude + explicit runtime override;
проверять выбранный resolver/detour, не только `dns.final`. Если владелец желает
иной priority, сначала явно изменить обещание UI/контракт, не скрывать исключение.

### NIGHT-04 — P1: failed firewall cleanup теряет единственный durable recovery trigger

**Место:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:155–191,304–317`;
`VPNRouter.Core/Platform/macOS/MacFirewallManager.cs:204–234,441–466`.

**Root cause:** после failed nft/pf удаления marker удаляется безусловно.
В orphan recovery результат уже известен (`ok=false`), но Linux `:312` и macOS
`:462` вызывают `TryDeleteMarker()` раньше ветвления по успеху. Следующий запуск
выходит по отсутствию marker. Обычный Disable также сбрасывает `_loaded` и пишет
«lifted» после helper, игнорирующего результат команды.

**Рецепт:** successful load → marker существует → hard crash без Dispose →
следующий startup делает cleanup при временном sudo denial, команда удаления
не исполняется → marker исчез → восстановить permission → ещё один startup
не повторяет cleanup. nft table остаётся drop; PF anchor остаётся blocking,
если PF активен (после hard crash возможен сохранённый enable reference).

Не нужен спорный timeout, который мог фактически успеть очистить правила.
Обычный Stop может сделать дополнительный DeleteAllRules, поэтому strongest
witness — marker-gated cleanup нового процесса, не утверждение «никаких retries
нигде нет». Фактическая длительность/полный blackout зависят от OS rules.

**Фикс:** helpers возвращают подтверждённый результат; marker, loaded-state и
PF token сохраняются при неудаче. Отсутствие table/anchor проверяется отдельно,
а не приравнивается к любому exit 1. Использовать существующий retain-until-success
подход, не добавлять параллельный recovery daemon. Fake-runner tests: load,
новый instance, failed cleanup сохраняет marker, повтор с успехом очищает,
confirmed-already-absent корректен. Нужен последующий изолированный OS smoke.

### NIGHT-05 — P1: Unix kill-switch bypass не соответствует текущему transport

**Место:** `VPNRouter.Core/Services/StartupPipeline.cs:407–434,455–477,1083–1095,1154–1157`;
`VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs:72–73`;
`VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:105–125,219–273`;
`VPNRouter.Core/Platform/macOS/MacFirewallManager.cs:123–143,346–402`.

**Root cause, freshness:** phase 6 читает `current.json` в CreateBlockRules и
навсегда кэширует `_serverIps`; phase 7 только потом пишет новый config. Первый
connect без файла получает пустую allowlist. При A→B получает A вместо B.
Enable строит правила из кэша. Successful Apply также пропускает phase 6,
оставляя прежний firewall manager (`VpnEngine.cs:803–839`).

**Root cause, completeness:** оба ReadServerIps читают только
`outbounds[].server`. Generated AWG помещает transport в
`endpoints[].peers[].address`, оставляя в outbounds лишь direct/dns-direct
(`ConfigGenerator.Outbounds.cs:217–231`, `ConfigGenerator.OutboundBuilders.cs:277–286`).
Поэтому даже свежий файл AWG не решает проблему. Дополнительно default hostname
resolver отбрасывает AAAA; literal IPv6 обрабатывается, это не общий запрет IPv6.

**Рецепт:** full mode и profile BlockOnVpnFail=true; Create при отсутствии файла,
затем записать public endpoint B, Enable. Либо заранее A и successful Apply B.
Результирующий ruleset не разрешает B. Отдельный fixture — generated AWG peer.
OS smoke должен исключить LAN endpoint и старое matching PF state, способное
замаскировать дефект. На crash Enable вызывается до recovery.

**Влияние ограничено:** transport блокируется, пока реально действует ruleset.
Не утверждается вечный brick: HealthMonitor может снять его по локальному API
или fallback (`HealthMonitor.cs:462–480,967–985`), не по proxied dataplane.
Это также не доказательство автоматической утечки после снятия.

**Фикс:** передавать firewall snapshot **committed** transport endpoints вместо
чтения файла прежней сессии; обновлять после successful Apply, не после failed
candidate. Извлекать outbound servers и WG peer addresses, обе address families.
Простое reread при Enable лечит часть drift, но файл может содержать ещё не
принятый runtime candidate; одной перестановки cold phases недостаточно.
Tests: missing file, A→B cold/hot/forced restart, failed Apply сохраняет A,
AWG peers, literal IPv6 и реальный hostname normalization seam.

### NIGHT-06 — P1: AutoFailover использует settings и pool прежнего пользовательского intent

**Место:** `VPNRouter.Core/Services/VpnEngine.cs:387,728,1651–1673,853–977`;
`VPNRouter.Core/Services/AutoFailoverEngine.cs:35,65,84–119,180–208,239–242,296–325,374–377`.

**Root cause:** `_failover ??=` сохраняет readonly settings и restart closure
первого host, реально выполнившего wiring. Новые Start/Apply создают новый host,
но старый failover не заменяют. ResetCycle очищает только tried set. Lifecycle
gate и session token правильны, однако гарантируют порядок, не актуальность
конфигурации. Guards custom/manual-choice читают тоже старые settings.

**Точный рецепт с учётом aliasing:**

1. Subscribe A, несколько пригодных candidates; выполнить реальный failover,
   чтобы lazy `_failover` точно создался, replacement подтвердился.
2. Сделать save+reload/reconnect с пока неизменным A. VM получает **новый объект**
   settings (`MainWindowViewModel.cs:6808–6810`).
3. Только после этого удалить прежнюю subscription, поменять режим/маршрутизацию
   на B; выполнить Apply либо Stop+Start на том же VpnEngine.
4. В активной B-сессии довести HealthMonitor до FailoverRequested. Старый pool A
   выбирает candidate и restart конфигурации A вместо B.

Healthy start сам по себе не гарантирует wiring. Если редактировать общий A
in-place до reload, удаление может быть видно и failover — потому порядок важен.
На диск сохраняется **не весь A**, а selectors старого candidate в свежий B;
runtime при этом может откатить mode/routing. Это не resurrection после Disconnect.

**Фикс:** engine-owned актуальный failover context с одним snapshot для pool и
restart; менять на новый public intent/успешный Apply, включая callbacks от старого
monitor. Не сбрасывать retry cap на каждом внутреннем restart. `_failover=null`
только в Stop не чинит Apply; новая restart lambda не чинит readonly pool.
Generation-check до restart/публикации/persistence исключает устаревшие callbacks.

**Regression:** production wiring через fake host/runner/store: first wire A,
новый B без A pool → Apply и отдельно Stop+Start → late failover. Проверять
candidate, restart JSON, selectors и B routing; контролировать internal retry cap.
Существующие phase-dispatch/source-shape tests не моделируют два settings objects.

### NIGHT-07 — P1: Phase B исчезает при успешном StartTask, не дождавшись Connected

**Место:** `VPNRouter.App/ViewModels/Internals/TwoPhaseStartCoordinator.cs:203–236`;
`VPNRouter.Core/Services/StartupPipeline.cs:475–490,1177–1193,1225–1244,1312–1326`;
`VPNRouter.App/ViewModels/MainWindowViewModel.Connection.cs:80–100,405–441`.

**Root cause:** Phase B ждёт WhenAny(Connected, startTask, deadline). Clean
completion startTask возвращает StartTaskCompleted, отменяет timer, удаляет
subscriptions. Но production Start намеренно запускает warmup fire-and-forget
и возвращается до него. При обычном допустимом порядке оставшийся setup
завершается раньше первой warmup-проверки. VM лишь await-ит готовый task и
снимает busy; обещанный 20s timeout/Stop больше не существует.

Одновременно OnEngineStatus красит Connected по строке `VPN Router is running`
до readiness, а exhausted warmup публикует строку Connected без typed event.
Runtime poll видит процесс, не восстанавливает потерянный readiness deadline.

**Детерминированный regression-рецепт без сети/sleeps:** startTask=CompletedTask,
subscribeStarted синхронно вызывает handler, Connected не приходит, PhaseB=0.
Started выигрывает Phase A; в Phase B текущий код выбирает startTask раньше
готового deadline и возвращает StartTaskCompleted вместо PhaseBTimeout.
Дополнительно: Started → clean completion → delayed Connected должен оставлять
координатор подписанным до Connected либо **исходного** deadline.

**Фикс:** successful completion после Started не завершает Phase B; faults и
внешняя отмена наблюдаются, оставшийся бюджет не перезапускается. Owned startup
становится green только по typed readiness; service/external adoption — отдельный
контракт. Исправить и coordinator, и status consumer: одного недостаточно.

`MvmTwoPhaseStartTimerTests.cs:175–231` оставляет startTask pending; `:265–295`
проверяет только fault до events. Отсутствует именно production ordering.
Это **не дубль** известного unproxied warmup (OPEN-DEFECTS, MTU-6): там ложный
успех probe, здесь успех probe вообще не требуется.

### NIGHT-08 — P1: failed exact Stop возвращается как successful Apply

**Место:** `VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs:231–258,537–543`;
`VPNRouter.Core/Services/SingBoxManager.cs:294–314`;
`VPNRouter.Core/Services/VpnEngine.cs:825–839`.

**Root cause:** при неподтверждённом Stop manager правильно сохраняет handle/lease,
ставит Failed и не запускает replacement. Но void Restart возвращается нормально.
Apply принимает отсутствие exception за commit, публикует новый fingerprint,
сообщает Applied, запускает candidate driver reconciliation и возвращает true.
Нижний safety gate работает, верхний слой лжёт о его результате.

**Рецепт:** Linux capability-mode old A, stubborn fake owned handle; forced Apply B
с иной routing metadata. Существующий manager test
`VPNRouter.Tests/SingBoxManagerRestartTunLockTests.cs:516–535` уже закрепляет
normal return + Failed + no replacement. Не хватает теста вызывающего engine.

**Фикс:** явный подтверждённый результат reload/restart либо exception при отказе;
не публиковать metadata/driver commit до подтверждения. `IsRunning()` недостаточен:
это может быть старый stubborn process. Regression: Apply false, old metadata,
никакого Applied-success/candidate driver action, old lease retained. Нельзя
лечить false success принудительным убийством без exact identity.

### NIGHT-09 — P2: Smart Connect не ограничивает одновременно открытые probes

**Место:** `VPNRouter.Core/Services/ServerHealthProbe.cs:51–71`;
`VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs:520–535`;
`VPNRouter.Core/Services/SubscriptionFetcher.cs:283–303`;
`VPNRouter.Core/Services/TcpTlsProbe.cs:221–232,526–548`.

**Root cause:** Select(async...) + Task.WhenAll перечисляет весь subscription pool.
Общий deadline ограничивает время cooperative probes, но не их количество.
TCP client создаётся до первого await; тысячи медленных кандидатов дают O(N)
одновременных sockets/CTS/operations. Парсер при >=500 лишь предупреждает,
а несколько enabled subscriptions объединяются без общей concurrency bound.

**Рецепт:** 5000 синтетических server entries и injected probe с active/max counter
и одним barrier. До release barrier текущая реализация входит во все 5000 вызовов.
Не нужны реальные endpoints или load test внешних серверов.

**Фикс:** ограниченное число workers, например Parallel.ForEachAsync с заданным
MaxDegreeOfParallelism и общим deadline; использовать существующий bounded pattern
из ServerTesting, не Task.Run на каждую запись. Tests: peak<=limit, cancelled queue
не начинает новые probes, in-flight освобождаются, result ordering не обещается.

P2 по шкале этой миссии: resource/performance defect. Не доказаны OOM, постоянная
утечка, величина CPU или причинная связь с историческим UDP exhaustion.

### NIGHT-10 — P2: отменённый UDP receive становится Ok/Slow

**Место:** `VPNRouter.Core/Services/TcpTlsProbe.cs:574–607,619–623`;
`VPNRouter.Core/Services/ServerHealthProbe.cs:59–67`;
`VPNRouter.App/ViewModels/MainWindowViewModel.ServerTesting.cs:224–242`.

**Root cause:** WhenAny возвращает receiveTask и при cancellation. Проверяется
только IsFaulted. Canceled task не faulted, поэтому код проходит в комментарий
«Got a reply», хотя результат receive не await-ился и пакета не было. Получаются
Ok/Slow (при достаточном elapsed), которые ServerHealthProbe принимает как Alive.
Batch UI после await не перепроверяет cancel и применяет результат к строке.

**Рецепт для разрешённого worker:** инертный loopback UDP listener, принимающий
пакет без ответа; дождаться pending receive, отменить caller token после >5ms,
но до внутреннего timeout. Ожидается cancellation, а не положительный probe.
Это не спор с намеренной optimistic no-ICMP timeout policy.

**Фикс:** await receiveTask перед success; явная внешняя OperationCanceledException
проходит через DNS inner catch и outer catch, consumers не публикуют результат
отменённой операции. Внутренний deadline — отдельно от caller cancellation.
Tests: pending cancel без Ok/Slow, actual reply positive, documented internal
no-reply behavior, cancel batch не красит строку зелёным.

### NIGHT-11 — P2: ConnStats выдаёт старую скорость за текущую при отказе API

**Место:** `VPNRouter.Core/Services/ClashSingBoxApi.cs:233–276`;
`VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs:74–81,116–158,184–195`.

**Root cause:** API failure и настоящий idle zero представлены одинаковым snapshot.
Zero response заставляет polling выйти без очистки display. Старые bytes/s и
active count остаются без признака stale, пока IsConnected=true; process-based
runtime detection может сохранять это состояние при недоступном API. Аналогично
unresolved auto-selected node оставляет прежний node, хотя комментарий обещает
fallback к generic label. Anti-spike сохранение baseline разумно, но не делает
предыдущий display свежим.

**Рецепт:** два валидных snapshots с ростом counters → видимая скорость → серия
401/timeout либо новый idle core с нулями без перехода IsConnected=false. Строка
остаётся прежней. Node A → unavailable также оставляет A без freshness limit.

**Фикс:** отличать validity от числового zero, маркировать stale/очищать display,
сохраняя последний корректный baseline для расчёта без spikes; valid reset
rebaseline. UI Post привязать к session generation, не одному IsConnected.
Tests: good→failure, good→zero, recovery без spike, node→unknown, late callback
предыдущей сессии. Это UI defect, не доказательство утечки трафика.

### NIGHT-12 — P2: включённая health telemetry не передаёт Clash secret

**Место:** `VPNRouter.Core/Services/VpnEngine.cs:1084–1105`;
`VPNRouter.Core/Services/ClashLogStream.cs:55–65,73–94,128–153`.

**Root cause:** engine создаёт stream без аргумента `secret`, хотя constructor
и BuildLogsUri умеют добавлять WS token. При configured API secret production
wiring отправляет unauthenticated connect и переходит в capped reconnect loop.
Строка «telemetry started» не означает, что WS handshake состоялся.

**Рецепт:** generated desktop config с непустым API secret и opt-in
`VPNROUTER_CONN_HEALTH=1`; инспектировать constructed URI через fake factory либо
использовать изолированный authenticated WS stub. Expected: token передан без
логирования. Current callsite не может его передать.

**Фикс:** передать `secret: settings.SingBox.ClashApiSecret`, сохранив redacted logs;
проверить именно engine wiring, не только unit BuildLogsUri. User-authored custom
controller secret mismatch — уже отдельный известный defect, сюда не включён.
Это **не повторное открытие localhost API без auth**: API защищён, сломан клиент
опционального observer. Уточняет историческое «EVERY consumer» в resolved ledger.

## 3. Что не следует выдавать за новые подтверждённые баги

### P3 / NIGHT-DOC: опасное противоречие карты и реализации

`VPNRouter.Core/Platform/AGENTS.md:39` описывает arming по пустому process list.
Реальная реализация использует explicit `isFullTunnel` (Linux `:86–106`, macOS
`:104–124`, caller `StartupPipeline.cs:1093–1095`). Исторический empty-list
host-drop уже исправлен. Обновить карту на explicit intent, иначе следующий агент
может вернуть старый дефект. Проверка — сверка документа и существующих empty
split-list tests; не требуется изменение product. Карта PowerEventListener также
описывает teardown/reconnect, тогда как actual callbacks делают ProbeNow: считать
исходники authoritative, не строить утверждения о sleep race только по карте.

### NIGHT-MEASURE: owner monitor — кандидат на профиль, не установленная CPU regression

`TunOwnershipLock.cs:174–240` после каждой итерации ждёт 200ms: до примерно пяти
итераций/с, не busy-wait. `FindProcessAtPath` → enumeration
(`ProcessOwnership.cs:477–491,505–547`), затем чтение owner record; обычно два
JSON read/parse за найденный child. Уже опубликованная identity предотвращает
**write**, а не read/enumeration. Это logical operations, не физический disk I/O.
Process arrays освобождаются, loop последовательный. Сначала измерить CPU stacks,
allocations/sec и logical reads на fixed-SHA worker; только после значимого вклада
рассматривать exact-PID steady-state fast path и редкий repair scan. Не ослаблять
identity validation ради предполагаемой экономии.

### Отсечённые/ограниченные гипотезы

- «Start/Stop/Apply вообще не синхронизированы» — неверно, lifecycle gate есть;
  новые findings касаются meanings/results/snapshot, не отсутствующего semaphore.
- «ProcessQuery постоянно течёт handles» — normal пути содержат using/finally;
  новой воспроизводимой утечки не установлено.
- «HealthMonitor повторный Start копит timers» — имеются Stop перед заменой,
  atomic overlap guards, Dispose subscriptions/API. Source-shape tests не заменяют
  soak, но нельзя игнорировать уже существующие cleanup.
- «ConnStats накладывает бесконечные запросы» — `_statsInFlight` и deadline есть.
- «ClashLogStream постоянно течёт sockets» — using websocket и cancellation есть.
  Unbounded fragmented StringBuilder и high-water queues — возможный future
  robustness/measurement scope, не доказанный production OOM в этом аудите.
- «Весь generated dual-stack молча утекает» — не доказано: LeakProtection
  `:182–184` отвергает отсутствующий ipv4_only, то есть часть комбинаций вместо
  утечки завершится validation failure. Raw IPv6/DoH browser bypass требует
  отдельной OS dataplane проверки; вывод «leak-free» не выносится.
- Unix split-mode disarmed, отсутствие sudo grant и deliberate non-strict smart
  DNS — задокументированные ограничения, не новые дефекты.
- Исторические PID reuse/sc.exe/polkit/Wintun/TabControl fixes не переобъявлены
  открытыми без конкретного нового witness. TgProxy показывает оставшуюся
  destructive границу, но не опровергает исправленный sing-box identity path.
- LPE/IPC всей службы, Android libbox/VpnService, 360px visual/decimal bindings,
  sleep/wake OS timelines и 24/7 memory profile не покрыты исчерпывающе.

## 4. Топ-3 действия разработчика утром

1. **Закрыть destructive/privacy ошибки с маленькими regression fixtures:**
   NIGHT-01 (Quit/toggle/update без foreign kills), NIGHT-02 (полный Inject WG
   сохраняет remote DNS), NIGHT-03 (strict override всех matched DNS rules).
   Сначала безопасные fake tests; не воспроизводить убийство сторонней программы.
2. **Сделать Unix firewall cleanup подтверждаемым и endpoint snapshot актуальным:**
   NIGHT-04 перед NIGHT-05. Failed cleanup сохраняет marker; cold/hot/AWG/AAAA
   проходят через committed endpoint contract. Затем isolated Linux/macOS smoke
   с recovery-планом; не ставить rules на control plane.
3. **Восстановить единый контракт intent → launch → readiness → commit:**
   NIGHT-06/07/08. Разные настройки A/B и clean StartTask-before-Connected должны
   входить в behavioural suite. Не чинить это ещё одним polling timer или
   reset-флагом в каждом partial. После этого ограничить probes и исправить
   cancellation (NIGHT-09/10); NIGHT-11/12 — короткие follow-ups.

Все survivors зарегистрированы в `plans/OPEN-DEFECTS.md`. Исправления остаются
рекомендациями: ни один пункт не помечен resolved. Открытые P1 должны участвовать
в существующем stable gate; данный отчёт не разрешает waiver, релиз или merge.
