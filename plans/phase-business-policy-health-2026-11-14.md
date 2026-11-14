# Business: centralized direct exceptions and verified VPN availability

## Task brief

- Original owner request, 2026-11-14: prepare a plan for later Claude Code review. That planning task did not include implementation or deployment.
- Takeover, 2026-11-14: the owner explicitly selected review and refinement of the plan. Continue the existing planning PR, verify the design against current source, record findings and refine delivery/acceptance steps. Preserve product behavior, installed clients and the separate PR #674.
- Review baseline: VPNRouter `4f5cc892c1cce0c5d7fd1f17291a1b13302dcbd6`; vpnctl contract/tree inspected read-only at `cc071d3143886ccf1cd0869011a694ab67882181`. Verification for these edits is source references, scoped documentation diff and exact-head PR CI; runtime gates remain future implementation work.
- Accepted base: `business`, `8fe6c19d9114d1fc7527054c4fe43e4940565c8e`; planning branch: `dsh/business-policy-health-plan-20261114`; PR target: `business`.
- Scope: a protected administration page for shared direct-domain exceptions, trusted client policy synchronization, and an honest per-node VPN availability indicator. Standard behavior must remain unchanged.
- Risk: planning LOW; later policy/probe implementation HIGH because it changes routing, DNS, private provisioning and process lifetimes.
- Rollback: revert the planning commit. Later delivery requires independent policy-content, client-binary and server rollback procedures.

## 1. Что требуется

Администратор добавляет `mkvt24.ru` один раз, и все Business-клиенты этой организации получают исключение без пересборки EXE на каждое изменение. Поддержку загрузки политики и доверенные параметры сервиса потребуется один раз доставить клиентам; старые версии сами эту возможность не получат.

Зелёная точка должна означать свежую успешную передачу тестового трафика через конкретный VPN-узел из текущей сети сотрудника. Открытый TCP-порт и серверная активность не являются доказательством такой доступности. Из сообщения владельца известно, что Sweden недоступен в некоторых регионах, но остаётся зелёным; точная причина блокировки не установлена.

Этот документ предназначен для проверки плана, а не для начала реализации или выпуска. Первоначальная передача на проверку продолжена в этой задаче; фактический статус независимого review указан в разделе 9.

## 2. Подтверждённая текущая архитектура

- `VPNRouter.App/VPNRouter.App.csproj` встраивает `packaging/windows/business-rules.json` как `VPNRouter.Business.Rules`.
- `VPNRouter.App/Services/BusinessBootstrap.cs` читает ресурс при запуске; `BusinessDefaults.Apply` в Core заново устанавливает `CustomRules`. Простая запись скачанных правил в настройки будет перезаписана при следующем запуске.
- `VPNRouter.Core/Services/ConfigGenerator.cs` вызывает `BusinessDefaults.ApplyDns` для Business. Центральная политика должна участвовать в генерации и маршрутов, и DNS; существующие российские/UIS-исключения, приложения и TCP/UDP-выбор сохраняются.
- `VPNRouter.App/Services/BusinessClientConfiguration.cs` берёт доверенные адреса подписки, метаданных, обновлений и поддержки из provisioning. Скачанная подписка и пользовательский кэш не могут задавать новые доверенные адреса.
- `VPNRouter.App/Views/BusinessWindow.axaml.cs` задаёт проверку через `TcpTlsProbe.ProbeTcpOnlyAsync`. В `BusinessServerChoiceViewModel.IsGood` статус TCP `Ok` становится зелёным. Это подтверждённый разрыв между индикацией и ожидаемой проверкой VPN.
- `BusinessServerHealthMonitor` проверяет видимое окно каждые 30 секунд, разрешает проверки при отключённом VPN и стабильном split-подключении, отменяет их при переходах/full-подключении. Эти ограничения нужно пересмотреть для более дорогой проверки, а не копировать автоматически.
- `BusinessNodeMetadataController` и `BusinessNodeSnapshot` отдельно обновляют страну/активность и возраст данных. Строка «данные устарели» на скриншоте относится к метаданным, а не к доказанной доступности VPN.
- `VlessDeepVerifier`, `FreeConfigDeepVerifier` и `DeepVerifyProbe` содержат проверку через отдельный sing-box/SOCKS и HTTPS. Проверить пригодность этих частей для Business; готовой безопасной замены текущему индикатору этим не доказано.
- Минимальное исключение `mkvt24.ru` уже подготовлено отдельно: [PR #674](https://github.com/PavelLizunov/VPNRouter/pull/674), source `862a547e7aa99e0547916e59abe299eed6e3eb90`. Exact-head CI подтверждён: 10 SUCCESS, 3 ожидаемых SKIPPED, без незавершённых/красных checks. Это не означает слияние или доставку в установленный клиент.

## 3. Порядок выполнения

1. Независимо проверить этот план в Claude Code и согласовать замечания. До реализации прочитать актуальные контракты обеих затронутых кодовых баз и обновить исходные SHA.
2. Отдельная небольшая правка `mkvt24.ru` уже имеет зелёный exact-head CI в PR #674. Слияние и доставка требуют отдельной команды владельца.
3. Исправить смысл индикации: отделить ответ порта от подтверждения VPN; затем добавить настоящую проверку узлов и её тесты.
4. Зафиксировать формат, доверие и приоритет центральной политики; реализовать сервер/API и клиентский механизм по этому контракту.
5. Добавить админку, подключить её к проверенному API, выполнить сценарий на двух клиентах и проверить откат.
6. После отдельного разрешения собрать/доставить новую Business-версию и активировать сервис. Сначала пилотная группа, затем все сотрудники.

Каждый этап реализации получает отдельный task brief, небольшой PR в свою принятую ветку и зелёные проверки до следующего блока. Планирование не меняет `AppVersion`, релизный канал, DNS, серверы или приватные установщики.

## 4. Централизованная политика: минимальная версия

### Пользовательский сценарий

Администратор входит в отдельную защищённую страницу, добавляет домен и комментарий, видит предварительный результат и публикует новую ревизию. В первой версии действие только «напрямую»: произвольный sing-box JSON, скрипты, IP-сети, настройки серверов и управление VPN-аккаунтами не предоставляются. Направление «через VPN» можно спроектировать отдельным расширением после проверки приоритетов.

Правило `mkvt24.ru` охватывает сам домен и поддомены. Если администратор вставляет URL, интерфейс явно показывает выделенный домен перед сохранением; сервер принимает только нормализованный домен. Путь, схема, порт и параметры не становятся частью маршрутизации. Не добавлять без подтверждения посторонние домены CDN или фиксированные IP сайта.

### Доверие и формат

- Предлагаемый снимок: `schema`, `policy_id`, монотонная `revision`, время публикации, полный список разрешённых direct-доменов и подпись. Точный wire-формат и алгоритм подписи утвердить при review; не придумывать собственную криптографию.
- Адрес политики, идентификатор организации и публичный ключ проверки приходят только из доверенного provisioning. Адрес нельзя подменить ответом подписки, метаданными, локальным YAML или самой политикой.
- Доставка по HTTPS с обычной проверкой сертификата; ограниченные таймаут, размер, глубина JSON и число правил; запрет неконтролируемых перенаправлений. Стартовые лимиты для review: 64 KiB, 512 доменов, глубина 8, дедлайн 10 секунд.
- Проверять схему, организацию, подпись, уникальность и нормализацию доменов, все границы размеров. Неизвестная схема или одно неверное правило отклоняют весь снимок без частичного применения.
- Запретить regex, wildcard, пустые имена и чрезмерно широкие правила вроде `*`/`.ru`; правило для публичного суффикса также должно отклоняться. Источник проверки публичных суффиксов выбрать до реализации; одной проверки «содержит точку» недостаточно.
- Кэш привязан к доверенному источнику, ключу и организации, записывается атомарно с защищёнными правами. После перезапуска подпись проверяется снова. Повреждённый/чужой кэш не становится доверенным.
- Старые ревизии отклоняются; совпадающая ревизия с другим содержимым отклоняется. Откат публикует прежнее содержимое под новой, большей ревизией. План ротации ключа должен сохранять возможность проверить последний принятый снимок.

### Слияние и применение на клиенте

- Встроенный пресет остаётся базой и первым резервом; центральная политика добавляет direct-исключения. Удаление домена из удалённого списка убирает только удалённое исключение, а не встроенное правило с тем же доменом.
- Утвердить единый эффективный набор до генерации конфига: direct-политика имеет приоритет над совпадающими встроенными proxy-доменами и proxy-приложениями. Пересечения суффиксов видны в предпросмотре и покрыты тестами. Существующие UIS/российские правила и процессные имена не теряются.
- Один и тот же снимок применяется к routing и DNS. Direct-домен использует прямой DNS; недопустимы состояния «маршрут новый, DNS старый» и обратное.
- Первая версия загружает политику при запуске и периодически независимо от открытого окна: предлагается каждые 5 минут с небольшим случайным сдвигом, а также вручную. Долгий запрос не блокирует Connect/Disconnect или интерфейс.
- Отделить принятую ревизию от применённой. При подключённом VPN новый снимок ожидает следующего подключения; показать это явно. В первой версии не перезапускать активные соединения автоматически и не обещать мгновенное изменение существующих сессий.
- Ошибка сети/сервера/подписи сохраняет последний корректный снимок и его возраст. При первом запуске без корректного кэша действует встроенный пресет. Обновление неудачного запроса не делает устаревший снимок свежим.
- Пустой список является только явным подписанным опубликованным состоянием. Пустой HTTP-ответ, 401, 404 или ошибка разбора не очищают исключения.
- Обновление не меняет выбор узла, подписку, employee identity, язык, режим, настройки Standard или локальные отчёты. Временный сбой сервиса политик не мешает обычному запуску VPN с последней корректной конфигурацией.

### Админка и сервер

- Сначала проверить существующий Business backend и репозиторий `vpnctl`: возможность расширить текущую админку/API предпочтительнее второго независимого хранилища. Точная кодовая база, адрес размещения и инфраструктурный доступ пока не выбраны.
- Для пилота достаточно одного выделенного администратора. Пароль хранится как стойкий хеш; браузерная сессия защищена, есть выход, ограничение попыток входа и защита CSRF/XSS. Административный пароль и ключ подписи не попадают в EXE.
- Читающие клиенты не имеют прав публикации. Доступ к корпоративному списку задаётся отдельной read-only идентичностью организации; секреты не передаются в URL и не логируются. Provisioning должен поддержать её безопасную доставку/ротацию.
- Редактирование создаёт черновик; отдельное действие публикует валидированную ревизию целиком. Контроль конкурентного редактирования не даёт второму администратору незаметно затереть изменения.
- Список: домен, комментарий, источник/состояние. История: кто, когда, что изменил, номер ревизии; восстановление предыдущего содержимого создаёт новую ревизию. Предусмотреть резервную копию и восстановление.
- В первой версии не собирать списки посещённых сайтов или историю трафика. Версию политики можно показать локально в клиенте; серверная отметка «применено у всех» потребует отдельного согласованного протокола подтверждений. Без него такого обещания в админке нет.

«Для всех» означает клиентов одной организации с поддержкой политики: онлайн-клиент получает снимок в пределах интервала обновления, офлайн-клиент — после возвращения в сеть. В первой версии применение маршрутов происходит при следующем подключении.

## 5. Проверка реальной работы VPN

### Смысл состояний

- Серый: нет актуальной проверки из текущей сети, она выполняется или безопасно выполнить её нельзя.
- Жёлтый: TCP-порт отвечает, но VPN ещё не подтверждён, либо результат неполный/неоднозначный. При потере свежести прежний зелёный снимается.
- Зелёный: свежий успешный протокольный обмен и ожидаемый HTTPS-ответ через этот узел из текущей сети. Подпись рядом со временем поясняет, что это время VPN-проверки, а не прежний TCP RTT.
- Красный: ограниченная по времени проверка VPN из этой сети завершилась подтверждённой неудачей. Текст не утверждает «заблокирован провайдером»: ошибка узла, учётных данных и маршрута имеют разные причины.

Конкретные переходы при сбое контрольных HTTPS-адресов утвердить отдельно: отказ единственного тестового сайта не доказывает отказ VPN. Серверная активность/страна остаются независимыми метаданными и не перекрашивают результат проверки.

### Проверка узла

1. Делать её на компьютере сотрудника. Проверка с сервера администратора или другого региона не показывает региональную доступность сотрудника.
2. Использовать точную текущую конфигурацию выбранного Business VLESS-узла, включая TLS/Reality, транспорт и связанные параметры. Сохранить весь основной пул и текущую TCP/UDP-логику; успешная TCP/HTTPS-проверка не доказывает работу UDP.
3. Исследовать безопасное переиспользование `VlessDeepVerifier`/`DeepVerifyProbe`. Изолированный вспомогательный sing-box открывает только loopback SOCKS, без TUN, системного прокси, маршрутов и firewall. Нельзя запускать основной VPN для фоновой проверки каждого узла.
4. HTTPS-запрос проходит только через SOCKS проверяемого узла, с нормальной проверкой сертификата и ожидаемого ограниченного ответа. Нельзя незаметно откатиться на прямой HTTP. Предусмотреть независимый резервный контрольный адрес и различение ошибки контроля/узла; исключить скачивание тестов скорости.
5. Исходное соединение к узлу должно идти по физическому пути сотрудника. Не проверять Sweden через уже работающий другой VPN. Если безопасный прямой путь нельзя гарантировать, показать неизвестное состояние/приостановить проверку.
6. Выделенные приватные временные конфиги, ограниченный вывод и очищенные ошибки; никаких URI/ключей/UUID в UI, логах или отчёте о плане. Уничтожать только созданный проверкой процесс по его владению; никогда не выполнять общий kill sing-box.
7. На отмене, таймауте, закрытии окна, выходе приложения и ошибке запуска гарантировать завершение своего процесса и удаление своих файлов. Проверить гонки выделения loopback-порта, готовность именно своего процесса и поздние callbacks.

### Частота, свежесть и состояние

- Не переносить существующий 30-секундный TCP-таймер на дорогую полную проверку. Предлагаемый старт: ручная проверка, при показе списка при отсутствии свежего результата и не чаще одного полного цикла за 5 минут; максимум 2 одновременных проверки, ограниченный общий бюджет.
- Кэш результата привязан к узлу, параметрам подключения и поколению сетевого окружения. Смена Wi-Fi/адаптера, сна/возобновления, подписки или параметров узла инвалидирует результат; свежесть не восстанавливается из старой галочки.
- Бюджеты, TTL и критерии доступности утвердить при review. Старый результат не может остаться бессрочно зелёным; ответ отменённой проверки не обновляет новое окружение.
- Connect/Disconnect имеют приоритет: отменяют фоновые проверки и дожидаются безопасного освобождения принадлежащих им ресурсов в пределах бюджета. Никакого перекрытия старого и нового циклов.
- Не менять узел автоматически по индикатору в этой задаче. Автовыбор/failover и централизованное отключение серверов требуют отдельного поведения и согласования.

## 6. Проверки и критерии приёмки реализации

### Политика и админка

- Два изолированных клиента одной организации получают одну ревизию. Клиент другой организации не принимает этот снимок. Изменения не затрагивают Standard.
- Добавление `mkvt24.ru` даёт direct для корня и поддоменов в split/full даже при proxy-приложении и без географического bypass; похожий `notmkvt24.ru` не совпадает. Маршрут и DNS соответствуют одной ревизии.
- Удаление удалённого исключения, пересечения доменов, пустой опубликованный список и возврат встроенного поведения проверены отдельно. Встроенные UIS/российские исключения и остальные приложения сохранены.
- После рестарта подтверждённый кэш сохраняется и не затирается bootstrap. Повреждённый кэш, неверная подпись/ключ/организация, replay, подмена ревизии, неизвестная схема, слишком большой/медленный/частично неверный ответ и redirect не меняют рабочую политику.
- История/откат, конкурентная публикация, неавторизованное чтение/изменение, rate limit, CSRF/XSS и отсутствие секретов в выводе покрыты тестами и отдельным review.
- Показаны принятая и применённая ревизии; клиент офлайн и клиент с активной сессией не обозначаются как уже обновлённые без доказательства.

### Доступность узлов

- Контрольный сервер принимает TCP, но VLESS-аутентификация или передача payload не работает: индикатор не становится зелёным. Это основной регрессионный сценарий сообщения владельца.
- Успешный протокольный HTTPS-тест, неправильные credentials, TLS/Reality-ошибка, таймаут, отсутствие sing-box, недоступный контрольный адрес и отмена получают различимые корректные состояния.
- Проверить отсутствие прямого HTTP fallback и обхода через другой активный VPN; таймаут общего бюджета; реальную очистку после каждого исхода, в том числе падения/отмены процесса.
- Смена сети/узла, повторное открытие окна, скрытие, сон/возобновление, закрытие, гонки Connect/Disconnect не оставляют старый зелёный и не меняют выбор/настройки.
- RU/EN и узкие размеры 340/420: понятные статусы, dropdown и нижние элементы доступны; время теста не выдаётся за TCP ping; активность и доступность читаются раздельно.
- На WINBRAT после разрешённой доставки выполнить настоящий split/full жизненный цикл и проверить TUN/core cleanup. Дополнительно нужна подтверждённая проверка из затронутой региональной сети сотрудника; один WINBRAT не воспроизводит все регионы.

## 7. Шесть ворот и текущий статус

1. Build — N/A для этого документа; будущая реализация: Release build точного SHA только в GitHub CI или на разрешённом worker после записанного preflight. На workstation/Harness сборок нет.
2. Tests — N/A для prose-only плана; будущая реализация: focused Business/policy/probe tests и полный применимый набор, затем отдельные native-core и live-сценарии. Отдельный PR #674 имеет собственные CI-результаты.
3. Docs — PASS: подготовлен snapshot-based план с текущими source entry points, неизвестными, приёмкой и откатом; пути/внутренние ссылки проверены, исходная проблема записана как BUSINESS-PORT-ONLY-GREEN в ledger. Exact-head CI документационного PR отражается в GitHub, не выдаётся за runtime-проверку новой функциональности.
4. Independent review — BLOCKED: the requested Claude review was attempted during takeover but the provider rejected it before returning findings; see section 9. Coordinator source review is recorded separately and is not independent acceptance. Implementation still requires correctness, security/trust and test/lifecycle review.
5. UI/live — N/A для плана; headless и полный WINBRAT-сценарий обязательны для будущего изменения UI и разрешённой доставки. Реальных региональных проверок эта сессия не выполняла.
6. Integration/characterization — N/A для prose-only плана; реализация должна сохранить Standard/API/подписки/selection/lifecycle и проверить реальную загрузку политики на двух клиентах, а не только парсер.

## 8. Что проверить в Claude Code до реализации

Проверить соответствие описанной архитектуры актуальным исходникам `business`; определить серверную кодовую базу/хостинг и способ безопасной миграции уже выданного provisioning; согласовать схему/подпись/ключи и точный приоритет доменов. Отдельно проверить безопасное переиспользование существующего verifier, прямой сетевой путь, TTL/таймауты, все отмены и доверие к результату HTTPS.

Нужен результат review с возражениями, их severity и ссылками на код; план затем исправляется до начала реализации. Не начинать большой UI/backend рефакторинг, выпуск или деплой только потому, что план получил положительный отзыв.

## Outcome

The initial plan was published at 4f5cc892 in PR #675 to business. The takeover adds four source-confirmed design findings and the implementation boundaries below in the same planning PR. Whitespace, documentation-only scope, seven referenced paths, seven exact source citations and unique ledger dispositions pass the scoped checks. Product source and the separate mkvt24.ru change remain outside this diff. No administration service, policy synchronizer or authenticated Business probe is implemented by this task. Independent Claude review did not run successfully; runtime and regional acceptance are not claimed. Fresh exact-head CI is tracked in PR #675 and reported at handoff; local builds/tests were not run for this documentation change, and PowerShell is unavailable on this host.

## 9. Takeover review and implementation boundaries

This section refines sections 3-8 following the coordinator's source review. It is a proposed implementation contract awaiting independent review, not a completed feature or a claim that all design decisions have been approved. Findings were recorded in `plans/OPEN-DEFECTS.md` before refining this plan.

### 9.1 Initial policy trust and migration

`BusinessProvisioningStore.Resolve` selects the embedded bundle when present and otherwise retains the private saved bundle. The shared updater requires `provisioning: external` and rejects `VPNRouter.Business.Connection` resources. Adding a new parser to a shared EXE will therefore leave previously issued clients without a policy endpoint, organization identity or verification key.

- Use an explicit policy capability in trusted provisioning: organization/policy identity, HTTPS endpoint, read-only credential and allowed verification keys. Missing or invalid optional policy fields disable policy synchronization only; the current VPN configuration must still start. Duplicate policy fields must not silently select a different trust root.
- The first rollout needs a one-time privately configured migration through the existing trusted delivery channel. Preserve the employee's subscription, identity, settings and reports; extend the provisioning contract and packaging tests before issuing that artifact. Subsequent shared updates remain credential-free and retain that provisioning. Never bootstrap keys or credentials from subscription responses, node metadata or the policy response itself.
- Delivery cannot be marked ready until the operator has a concrete migration artifact/procedure for an already-issued client. Record which installation channels it supports and how a failed migration leaves the old provisioning usable. This task does not create or distribute that artifact.
- Test old client/old provisioning, new client/old provisioning, new client/new provisioning, malformed optional fields, interrupted migration and upgrade/rollback/upgrade. Confirm that the shared package contains neither organization credentials nor an embedded connection bundle.

### 9.2 Company administrator versus infrastructure operator

The inspected vpnctl contract at `cc071d3` explicitly describes a single operator with no multi-tenancy/RBAC. Reusing its global operator session for a company administrator would exceed the intended domain-exception capability. The actual deployed Business backend revision and its relation to vpnctl main remain unverified.

- Prefer shared backend infrastructure only if it can expose a separately authenticated policy-only surface. Otherwise use an isolated service boundary; do not add a partially enforced general RBAC system as an incidental part of this feature. Record the repository/revision and hosting decision before backend implementation.
- A company administrator may edit, publish and restore only its assigned policy. The server derives that identity from authentication, never from a client-supplied organization selector alone. A client read credential may only fetch its assigned published policy. Neither identity grants node deployment, inventory, subscription-account, support-upload or infrastructure administration rights.
- Isolate the policy session, cookie name/signing context and CSRF checks from the operator surface. Existing operator authentication is not automatically valid for the company surface or vice versa. Define logout, credential rotation and session revocation semantics; do not promise immediate revocation if the chosen session mechanism cannot provide it.
- Publication commits the immutable signed bytes, revision and redacted audit event atomically. Failed signing, failed audit, concurrency conflicts and backup restore must not expose partial state or reuse a previously issued revision. Restore content by publishing a newer revision. Verify authorization by direct HTTP requests to both surfaces, including cross-policy identifiers; hiding navigation is insufficient.

### 9.3 Immutable policy and durable recovery

`BusinessDefaults.Apply` replaces `CustomRules` at startup, while `ConfigGenerator.Generate` reads those settings to build routing and DNS. Downloading into YAML or modifying live settings from the refresh callback is not an adequate integration boundary.

- Introduce one application-owned policy coordinator, independent of window visibility. Maintain distinct immutable embedded, accepted and last successfully applied snapshots. At connection start, capture the effective rules and revision once; use that capture for both routing and DNS through the full start attempt. A concurrent refresh belongs to the next attempt.
- Build the effective rule set from embedded rules plus the accepted remote direct list, preserving infrastructure protections and process-name casing. Put explicit remote direct suffixes ahead of embedded proxy domains and proxy applications in both route and resolver precedence. Deleting a remote entry restores the embedded behavior. Test overlapping parent/child suffixes in both directions and a domain already embedded as direct.
- A valid signature means accepted content, not successful application. Advance the applied revision only after the generated configuration and connection readiness are confirmed by the owning lifecycle. A generation/start failure retains the previous applied revision and reports a pending/failed revision. If retrying the known last working snapshot, show that fallback explicitly and keep the newer accepted revision pending; never silently discard it.
- Persist a protected acceptance journal containing source/organization identity, key identity, maximum accepted revision, payload digest and the snapshots needed for recovery. Commit acceptance atomically before advertising it. An older server response cannot reduce the floor; an equal revision is idempotent only for identical signed content. Reconnecting with a previously accepted fallback does not reduce that floor.
- Cover restart, concurrent refreshes, disk-full/ACL failures and crash recovery between acceptance steps. Binary rollback must not silently roll back policy trust or the acceptance floor. An administrator restoring an entire older machine backup is outside protection by a local journal alone; recovery must reconcile with the current trusted service before claiming replay protection.
- Before cross-repository implementation, commit one wire specification and shared signed fixtures: exact signed bytes/envelope, fixed supported signature algorithm, key identifier/rotation overlap, strict duplicate-field behavior, integer bounds, time semantics, domain IDNA normalization and a pinned public-suffix source. Do not independently sign reserialized JSON in Rust and verify a different representation in C#.
- Apply the proposed 64 KiB response bound to decoded HTTP content as well as transport handling; cap list size/depth and reject redirects. An HTTP cache hit may update last-contact time, not the signed publication time or the age of a policy. Keep accepted/applied revision and last successful contact distinct in RU/EN diagnostics.

### 9.4 Business probe contract and reuse limits

The existing deep verifier is a source of components, not a drop-in Business probe. `DeepVerifyProbe` uses a single control URL, default redirect handling and a fully buffered body. Its port-ready check proves that something listens, while verifier cleanup does not await child exit. The current window-owned health monitor requests cancellation without exposing an awaited shutdown barrier.

- Prototype a Business-specific probe boundary with a typed result, a monotonic clock, a bounded scheduler and an asynchronous stop/drain operation. Preserve other callers of `VlessDeepVerifier` and their behavior unless a separately tested shared extraction is necessary. Do not surface raw exception strings, credentials or trace bodies in status text.
- Authenticate the loopback SOCKS listener with a per-run random credential, retain the owned process handle and verify it remains alive through readiness and the request. A foreign listener winning the port race must fail authentication and must never produce green. Keep configuration and credentials in a private per-run directory; do not expose a Clash API or use a speed-test download.
- Use normal TLS validation, no redirects, bounded streaming response reads and explicit expected status/body checks for two independently operated control endpoints. Try the fallback through the same checked node only. A valid response through either control may establish HTTPS success; failure of the controls, local runtime startup or DNS setup must remain distinguishable from a demonstrated remote handshake/authentication failure.
- Default classification: successful authenticated HTTPS is green; confirmed node connection/protocol failure is red; ambiguous timeout or control-service failure is inconclusive; cancellation, unsupported transport, missing runtime or unverifiable direct egress is unknown. A TCP response alone remains yellow. Do not infer an ISP block, UDP health or overall site availability from these results.
- Prove the direct physical route for the helper's node connection and its bootstrap DNS path. A `direct` outbound name is not evidence of physical egress. Until route/interface binding is implemented and verified for the shipped core, suspend probes during any active tunnel or uncertain route and clear green. This deliberately does not inherit the existing connected-split permission. Separately verify hostname resolution for control requests; the current helper's direct DoH behavior must not be mistaken for tunnel DNS validation.
- Cancel before a connection transition, then await owned-helper exit and private-file cleanup. Do not hold the dispatcher while draining. Subscription replacement, window recreation, hide, shutdown, suspend/resume and network-generation changes must invalidate late results and drain the old round before starting a new one. Repeated hide/show or monitor reconstruction must not reset the application-level probe budget.
- Suggested budgets for independent review: 15 seconds per node including control fallback, 60 seconds per cycle, at most two helpers, 5 minutes of monotonic freshness and at most one automatic cycle per 5 minutes. Queue entries left untested by the cycle budget stay unknown. Bound manual refreshes too; they cannot bypass concurrency or create overlapping rounds.
- Reserve a bounded cleanup phase and require actual process exit before another helper or Connect starts. If cleanup fails, expose a local error and stop probe scheduling; Disconnect must still tear down the main tunnel. A crash may bypass `finally`, so any next-start recovery must identify only the recorded owned helper using process identity/start time and the private run path, never a broad sing-box kill.
- Acceptance must include a competing SOCKS listener, a helper that ignores cancellation, partial/oversized/redirecting controls, two unavailable controls, hostname DNS failure, stale callbacks, clock changes, repeated window recreation and another active VPN. Run actual helper/process/network cases on a preflighted Windows worker or Windows CI; injected unit tests alone cannot establish ownership, routing or cleanup.

### 9.5 Delivery slices and evidence

| Slice | Dependency and reviewable result | Required evidence before the next slice |
|---|---|---|
| A: indicator semantics | Change only Business TCP-only wording/colors; preserve node selection and metadata | Regression that TCP success never means verified VPN, RU/EN narrow headless render, exact-head CI |
| B: isolated probe | Reviewed physical-egress, control-response and lifecycle contract from 9.4 | Fake-boundary tests plus real owned-process/control tests, lifecycle race tests and Windows core compatibility |
| C: policy contract | Resolve 9.1-9.3 in both repositories before client/server work | Shared positive/negative signature and domain fixtures, concrete trust migration and authority boundaries |
| D: policy API and admin | C; draft/publish/history/restore with policy-only authentication | Independent API spec tests, authorization/CSRF/concurrency/restore checks and backend CI at a pinned SHA |
| E: client policy | C and a test API; app-owned synchronization and immutable connect snapshot | Restart/replay/migration tests, route/DNS precedence in split/full, real bootstrap-to-connect coverage and Business-edition CI |
| F: integration and delivery | B, D and E | Two clients, wrong-organization rejection, pending/applied status, private migration and shared-update compatibility, WINBRAT lifecycle and regional evidence |

Each implementation slice needs its own brief and task PR with green checks, as required by the owning repository. VPNRouter PRs target `business`; the selected backend uses its accepted base. The generic Standard test job is not a substitute for compiling/testing the Business edition. Candidate packaging, distribution and activation are separate delivery work; documentation completion does not establish them.

### 9.6 Review status and outstanding decisions

- Coordinator source review: the four planning gaps above are recorded with source evidence in the ledger and addressed in this proposed plan. The deployed TCP-only indicator remains an open runtime gap. No product changes, worker jobs or regional probes were performed.
- Independent Claude review: attempted once; the provider failed before returning findings with `Your account can't receive replies from Claude right now`, directing the owner to `https://portal.anthropic.com/verify`. No alternative provider was substituted and no independent acceptance is claimed.
- Still required before implementation: independent review; the deployed backend/repository and policy-only authentication choice; the exact signature/normalization/key-rotation specification and migration artifact; verified physical-egress support, control endpoints and final budgets. These are explicit entry gates for the relevant slices, not hidden assumptions to resolve while shipping.
- Initial PR CI evidence: six checks completed successfully at `4f5cc892`; `packaging-arch` reported `in_progress` with a `success` conclusion while its parent run was completed. This inconsistent record is not claimed as an all-green gate. The documentation update must receive fresh exact-head checks.
