# Dependency replacement task list

Дата: 2026-08-02
Источник: read-only аудит зависимостей Qwen 3.8 и последующая проверка Codex.
Цель: уменьшить лишние зависимости и код без новых библиотек и без изменения пользовательского поведения.

## Порядок выполнения

| Приоритет | ID | Задача | Режим | Зависит от |
|---:|---|---|---|---|
| 1 | DR-01 | Удалить неиспользуемый `coverlet.collector` | безопасная правка | — |
| 2 | DR-02 | Удалить неактивный `AvaloniaUI.DiagnosticsSupport` | безопасная правка | — |
| 3 | DR-03 | Исправить владельцев Serilog sinks | безопасная правка | — |
| 4 | DR-04 | Заменить старые SHA-шаблоны на .NET 10 BCL | безопасная правка | — |
| 5 | DR-05 | Удалить мёртвый локальный QR encoder | безопасная правка | — |
| 6 | DR-06 | Перевести Android ZXing на `Bind=false` | прототип с проверкой на устройстве | — |
| 7 | DR-07 | Заменить TraceEvent более лёгким монитором процессов | прототип на `windows-brat` | — |
| 8 | DR-08 | Попытаться убрать `System.Management` | условный прототип | результат DR-07 |

DR-01–DR-05 можно выполнять независимо, но каждая задача должна иметь свой worktree и PR. DR-06–DR-08 нельзя объединять с безопасными чистками: результат эксперимента должен быть измерим и легко отклоняем.

## Общие правила для всех задач

- Использовать отдельный worktree от актуального `origin/main`; основной checkout не менять.
- Ветка: `codex/<id>-<short-name>`.
- Перед работой прочитать корневой `AGENTS.md` и zone `CLAUDE.md` затрагиваемых каталогов.
- Для обязательной независимой проверки использовать именно Qwen 3.8, модель `qwen3.8-max-preview`; другую модель не подставлять. Qwen работает read-only и возвращает проверку границ задачи и рисков. Изменения, тесты и Git выполняет Codex.
- Для зон Core/Services, App/ViewModels и Android в начале задачи применить `phase-task-launcher`.
- Не добавлять новые зависимости, абстракции или fallback-реализации без доказанной необходимости.
- Не менять версии несвязанных пакетов, generated-файлы, release metadata и пользовательские настройки.
- Не использовать `--no-verify`, не создавать tag/release и не выполнять deploy или merge.
- После проверки: один сфокусированный commit, немедленный push текущей ветки, draft PR в `main`, затем фактическая проверка CI по правилам репозитория.
- Если критерии приёмки не выполнены, не маскировать результат: оставить отчёт, не предлагать merge.

---

## DR-01 — убрать `coverlet.collector`

### Результат

Удалить лишний test-only collector. Покрытие при необходимости уже может собираться встроенным Microsoft Code Coverage через `Microsoft.NET.Test.Sdk`.

### Файлы

- `VPNRouter.Tests/VPNRouter.Tests.csproj`
- CI/scripts только для повторной проверки отсутствия вызовов Coverlet; менять их не ожидается.

### План

1. Повторно найти `coverlet`, `XPlat Code Coverage`, `CoverletOutput` и `CollectCoverage` во всём репозитории.
2. Если реальных вызовов нет, удалить только `PackageReference` на `coverlet.collector`.
3. Собрать test project и запустить полный тестовый набор.
4. При необходимости один раз проверить встроенный collector командой с `--collect:"Code Coverage;Format=Cobertura"`; generated results не коммитить.

### Приёмка

- В репозитории нет ссылок на Coverlet.
- `dotnet build VPNRouter.Tests/VPNRouter.Tests.csproj -c Release` проходит.
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build` проходит.
- В diff только ожидаемая строка манифеста.

### Готовый промпт

```text
Задача DR-01 для VPNRouter: удалить неиспользуемый coverlet.collector.

Создай отдельный worktree от актуального origin/main и ветку codex/dr-01-remove-coverlet. Основной checkout не меняй. Прочитай AGENTS.md и VPNRouter.Tests/CLAUDE.md.

Сначала обязательно запусти read-only проверку именно Qwen 3.8 (qwen3.8-max-preview; без замены моделью): найди все вызовы coverlet, XPlat Code Coverage, CoverletOutput и CollectCoverage и подтверди, что пакет не участвует в CI или локальных scripts. Qwen не должен менять файлы. Codex проверяет вывод и выполняет правку.

Если потребителей нет, удали только PackageReference coverlet.collector из VPNRouter.Tests/VPNRouter.Tests.csproj. Не добавляй новую библиотеку: Microsoft Code Coverage уже доступен через Microsoft.NET.Test.Sdk. Собери test project и запусти все тесты. Generated coverage files не коммить. Не меняй другие версии пакетов.

После зелёной проверки создай один commit, сразу push текущей ветки и draft PR в main; проверь CI. Не используй --no-verify, не создавай release/tag, не выполняй merge.
```

---

## DR-02 — убрать `AvaloniaUI.DiagnosticsSupport`

### Результат

Удалить debug-only пакет, который сейчас не активируется: в приложении нет вызова `AttachDeveloperTools()`.

### Файлы

- `VPNRouter.App/VPNRouter.App.csproj`
- `VPNRouter.App/App.axaml.cs` только для повторной read-only проверки.

### План

1. Проверить отсутствие `AttachDeveloperTools`, `Avalonia.Diagnostics` и связанных условных вызовов.
2. Удалить `AvaloniaUI.DiagnosticsSupport` и устаревший комментарий о якобы автоматическом подключении.
3. Собрать App в Debug и Release.
4. Запустить релевантные тесты без изменения UI.

### Приёмка

- DiagnosticsSupport отсутствует в манифесте и resolved dependencies.
- Debug и Release сборки App проходят.
- Никакой пользовательской функциональности или кода запуска не изменено.

### Готовый промпт

```text
Задача DR-02 для VPNRouter: удалить неактивный AvaloniaUI.DiagnosticsSupport.

Работай только в отдельном worktree от актуального origin/main, ветка codex/dr-02-remove-avalonia-diagnostics. Основной checkout не меняй. Прочитай AGENTS.md и VPNRouter.App/CLAUDE.md.

Сначала вызови Qwen 3.8 строго как qwen3.8-max-preview в read-only режиме. Он должен проверить весь репозиторий на AttachDeveloperTools, Avalonia.Diagnostics и иные способы включения DevTools. Другую модель не использовать; Qwen файлы не меняет.

Если вызовов нет, удали PackageReference AvaloniaUI.DiagnosticsSupport и относящийся к нему неверный комментарий из VPNRouter.App/VPNRouter.App.csproj. Не добавляй замену: отсутствующая функция сейчас не используется. Собери VPNRouter.App в Debug и Release и запусти релевантные тесты.

Сделай один commit, сразу push ветки и draft PR в main, затем проверь CI. Без --no-verify, release, tag, deploy и merge.
```

---

## DR-03 — исправить владельцев Serilog sinks

### Результат

Оставить базовый `Serilog` в Core, а Console/File sinks объявить только в исполняемых проектах, которые реально вызывают `.WriteTo.Console()` или `.WriteTo.File()`.

### Ожидаемая раскладка

| Проект | Console | File |
|---|---:|---:|
| `VPNRouter.App` | да | да |
| `VPNRouter.CLI` | да | да |
| `VPNRouter.Service` | нет | да |
| `VPNRouter.Tools/PoolAggregator` | да | нет |
| `VPNRouter.Android` | нет | нет |
| `VPNRouter.Core` | нет | нет |

Дополнительно удалить прямой `Serilog.Extensions.Logging` из CLI, если повторная проверка подтвердит, что он нужен только транзитивно через уже подключённый hosting stack.

### План

1. Построить фактическую карту всех `WriteTo.Console/File` и direct/transitive package references.
2. Удалить sinks из Core и Android.
3. Добавить прямые ссылки с уже используемыми версиями в App и PoolAggregator; Service и CLI оставить с нужными sinks.
4. Удалить прямой `Serilog.Extensions.Logging` из CLI только после компиляционной проверки.
5. Собрать solution и отдельно App, CLI, Service, PoolAggregator и Android Release.
6. Проверить resolved output: Service не должен получать Console sink только через Core.

### Приёмка

- Каждый sink принадлежит фактическому composition root.
- Никакой logging call не изменён.
- Все затронутые проекты собираются, тесты проходят.
- Не появилось новой версии или нового logging package.

### Готовый промпт

```text
Задача DR-03 для VPNRouter: привести владельцев Serilog sinks к фактическим composition roots.

Создай отдельный worktree от origin/main и ветку codex/dr-03-serilog-sink-ownership. Основной checkout не трогай. Прочитай AGENTS.md и CLAUDE.md затрагиваемых зон; так как задача касается Core и Android, начни с phase-task-launcher.

Обязательная независимая проверка: запусти Qwen 3.8 именно qwen3.8-max-preview в read-only режиме. Пусть он перечислит все WriteTo.Console/File, direct/transitive PackageReference и подтвердит нужный sink для каждого executable. Qwen не редактирует файлы, другую модель не использовать.

Целевая раскладка: App получает прямые Console 6.1.1 и File 5.0.0; CLI сохраняет Console и File; Service сохраняет только File; PoolAggregator получает прямой Console 6.1.1; Android не имеет Console/File; Core не имеет Console/File, но сохраняет базовый Serilog. Удали прямой Serilog.Extensions.Logging 10.0.0 из CLI только если сборка подтверждает, что hosting stack уже предоставляет нужную интеграцию.

Не меняй logging calls и версии остальных пакетов. Собери solution и отдельно App, CLI, Service, PoolAggregator, Android Release; запусти тесты. Проверь resolved output, особенно отсутствие Console sink у Service через Core.

После зелёной проверки: один commit, немедленный push, draft PR, проверка CI. Не используй --no-verify и не создавай release/tag/deploy/merge.
```

---

## DR-04 — заменить старые SHA-шаблоны на .NET 10 BCL

### Результат

Заменить `Create()` + stream hashing + ручной lowercase на статические API .NET 10: `SHA256.HashData`, `SHA256.HashDataAsync`, `SHA1.HashData` и `Convert.ToHexStringLower` там, где сохраняется тот же формат.

### Основные участки

- `VPNRouter.App/ViewModels/MainWindowViewModel.AutostartBootstrap.cs`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs`
- `VPNRouter.Core/Services/SlipstreamManager.cs`
- `VPNRouter.Core/Services/SplitTunnelDriverManager.cs`
- `VPNRouter.Core/Services/TgProxyUpdater.cs`
- `VPNRouter.Core/Services/UpdateChecker.cs`
- `VPNRouter.Core/Services/UpdateSources/SideloadSource.cs`
- `VPNRouter.Core/Services/WgturnUpdater.cs`
- соответствующие тесты и другие найденные тем же поиском SHA-участки.

### План

1. Найти все SHA1/SHA256 call sites и зафиксировать синхронность, формат и lifetime stream.
2. Заменить только механический hashing pattern; сравнение digest и security policy не менять.
3. Использовать `Convert.ToHexStringLower`, если текущий контракт lowercase; uppercase-контракты оставить как есть.
4. Добавить или скорректировать только минимальные checksum tests.
5. Собрать solution и запустить полный тестовый набор.

### Приёмка

- Для прежних test vectors получаются идентичные digest strings.
- Нет ручного `ToLowerInvariant()` после `Convert.ToHexString` для hash output.
- Нет нового crypto-кода или зависимости.
- Поведение download/update verification не изменено.

### Готовый промпт

```text
Задача DR-04 для VPNRouter: механически перевести SHA1/SHA256 hashing на статические API .NET 10 BCL без изменения криптографического поведения.

Создай отдельный worktree от origin/main и ветку codex/dr-04-bcl-hashing. Основной checkout не меняй. Прочитай AGENTS.md и zone CLAUDE.md; для Core/Services и App/ViewModels примени phase-task-launcher.

До правок запусти read-only Qwen 3.8 строго как qwen3.8-max-preview. Он должен найти все SHA1/SHA256 call sites, для каждого отметить sync/async, вход stream/bytes, ожидаемый lower/uppercase и проверки digest. Другую модель не использовать; Qwen не меняет файлы.

Codex заменяет только эквивалентные шаблоны на SHA256.HashData, SHA256.HashDataAsync, SHA1.HashData и Convert.ToHexStringLower. Не писать собственную криптографию, не менять алгоритм, формат hash, сравнение, policy или trust boundary. Не вводить helper, если статический BCL-вызов помещается на месте.

Проверь прежние test vectors, добавь максимум один минимальный regression test на реально не покрытый контракт. Собери solution и запусти все тесты, особенно update/download checksum paths.

После зелёной проверки создай один commit, сразу push и draft PR, дождись CI. Без --no-verify, release/tag/deploy/merge.
```

---

## DR-05 — удалить мёртвый локальный QR encoder

### Результат

Удалить примерно 650 строк локальной реализации QR, которая вызывается только тестами и не участвует в production flow.

### Файлы

- удалить `VPNRouter.Core/Services/QrCode.cs`;
- удалить `VPNRouter.Tests/QrCodeTests.cs`;
- убрать QR-only test fragments из `VPNRouter.Tests/ConfigShareDocumentTests.cs`;
- проверить `NOTICE.md` и удалить только ставшую неактуальной атрибуцию, если она существует.

### План

1. Повторно проверить все обращения к `QrCode` и `QrCode.EncodeText` во всём репозитории.
2. Проследить реальный Android/Desktop share flow и подтвердить, что этот класс не используется reflection/source generation.
3. Удалить encoder и тесты, которые тестируют только его самого.
4. Не добавлять другую QR-библиотеку: production-потребителя нет.
5. Собрать solution и запустить все тесты.

### Приёмка

- Нет production-ссылок и мёртвого QR API.
- Реальный import/share/scan flow не изменён.
- Тесты не сохраняют искусственный вызов удалённого компонента.
- Никакая QR-зависимость не добавлена.

### Готовый промпт

```text
Задача DR-05 для VPNRouter: удалить мёртвый локальный QR encoder, не заменяя его новой библиотекой.

Создай отдельный worktree от origin/main и ветку codex/dr-05-remove-dead-qr-encoder. Основной checkout не трогай. Прочитай AGENTS.md, VPNRouter.Core/CLAUDE.md и VPNRouter.Tests/CLAUDE.md; примени phase-task-launcher для Core/Services.

Сначала обязательный read-only проход Qwen 3.8, модель только qwen3.8-max-preview. Пусть он найдёт все compile-time, reflection и generated references к VPNRouter.Core.Services.QrCode и проследит реальный Android/Desktop share flow. Qwen не редактирует файлы; не заменяй его другой моделью.

Если production callers по-прежнему отсутствуют, удали VPNRouter.Core/Services/QrCode.cs и VPNRouter.Tests/QrCodeTests.cs, а из ConfigShareDocumentTests удали только фрагменты, существующие ради вызова этого encoder. Проверь NOTICE.md и меняй его лишь при наличии конкретной ставшей неактуальной атрибуции. Не добавляй новый QR package и не проектируй replacement для отсутствующего потребителя.

Собери solution, запусти полный test suite и проверь реальный share/import код статически. Один commit, немедленный push, draft PR и CI. Без --no-verify, release/tag/deploy/merge.
```

---

## DR-06 — Android ZXing `Bind=false`

### Результат

Сохранить Java ZXing libraries внутри APK, но не генерировать ненужные managed C# bindings. По текущему baseline generated bindings занимают около 1.53 MB исходников, а связанный AOT-фрагмент — около 6.75 MB до сжатия / 1.83 MB в APK. Это оценка, а не обещанный выигрыш.

### Файлы

- `VPNRouter.Android/VPNRouter.Android.csproj`
- `VPNRouter.Android/Transforms/Metadata.xml` — удалить только если bindless build докажет, что transform больше не нужен.
- Java bridge и C# JNI boundary — читать и тестировать, без рефакторинга вне необходимости.

### План эксперимента

1. Снять baseline Release: время сборки, размер APK, число/размер generated binding files.
2. Установить `Bind=false` для ZXing AAR и JAR, не меняя `Pack` и Java bridge.
3. Собрать Release. Если `Metadata.xml` больше не применяется, проверить сборку без него отдельным минимальным diff.
4. Повторить измерения одинаковой командой.
5. На физическом Android-устройстве пройти запуск QR scanner, выдачу camera permission, успешное распознавание и передачу результата в приложение.
6. Оставить PR только при успешной runtime-проверке и измеримом выигрыше.

### Приёмка

- В C# по-прежнему нет зависимости от generated `Com.Google.Zxing`/`Com.Journeyapps` types.
- Java bridge загружается без `ClassNotFoundException`, `NoClassDefFoundError`, JNI errors.
- Реальное сканирование QR проходит end-to-end.
- В PR есть before/after числа; нет предположительных процентов.

### Готовый промпт

```text
Задача DR-06 для VPNRouter: проверить и, только при успехе, перевести Android ZXing AAR/JAR на Bind=false.

Создай отдельный worktree от origin/main и ветку codex/dr-06-zxing-bind-false. Основной checkout не меняй. Прочитай AGENTS.md и Android-related instructions, затем примени phase-task-launcher.

Сначала запусти Qwen 3.8 только как qwen3.8-max-preview в строгом read-only режиме. Он должен проверить C# на Com.Google.Zxing/Com.Journeyapps references, Java bridge на фактические Java API, текущие Bind/Pack settings и роль Transforms/Metadata.xml. Qwen ничего не меняет; другую модель не использовать.

Сними воспроизводимый Android Release baseline: время build, APK size, количество и суммарный размер generated binding C# files. Затем поставь Bind=false для существующих ZXing AAR и JAR, сохранив Java packaging и bridge. Metadata.xml удаляй только если build с Bind=false доказывает, что он больше не нужен.

Повтори измерения той же командой. На физическом Android-устройстве проверь end-to-end: запуск scanner, camera permission, распознавание валидного QR и передачу результата приложению; проверь logs на ClassNotFoundException, NoClassDefFoundError и JNI errors. Не публикуй release и не подменяй device test статическим анализом.

Commit/push/draft PR разрешены только если Release build и device flow прошли, а выигрыш измерен. Иначе верни отчёт и не предлагай merge. Без --no-verify, tag/release/deploy/merge.
```

---

## DR-07 — прототип замены TraceEvent

### Результат

Проверить, можно ли убрать `Microsoft.Diagnostics.Tracing.TraceEvent`, используемый одним Windows process monitor, и сократить Windows output примерно на 3.76 MB. Предпочтение: уже подключённый WMI event watcher или простой polling, если он проходит реальный routing scenario.

### Границы

- `VPNRouter.Core/Services/EtwProcessMonitor.cs`
- `VPNRouter.Core/Services/HealthMonitor.cs`
- registration/composition root process monitor
- `VPNRouter.Core/VPNRouter.Core.csproj`
- минимальные тесты monitor lifecycle.

### План эксперимента

1. Зафиксировать, какие события реально потребляются. Текущая гипотеза: product flow использует `ProcessStarted`, а debounce уже равен 5 секундам.
2. Сделать один минимальный кандидат, не параллельную framework-систему:
   - сначала `ManagementEventWatcher` с `Win32_ProcessStartTrace`/`Win32_ProcessStopTrace`, потому что `System.Management` уже подключён;
   - polling рассматривать только если WMI events не проходят надёжность или DR-08 требует убрать WMI.
3. Проверить start/stop/dispose races, недоступность WMI service, короткоживущие процессы и повторный запуск monitor.
4. Проверить end-to-end запуск приложения из routed list на `windows-brat`.
5. Удалить TraceEvent package только после прохождения прототипа; измерить Windows Release output до/после.

### Приёмка

- Routed process обнаруживается в пределах существующего пользовательского ожидания.
- Нет зависших watchers, фоновых threads или утечки handlers после Stop/Dispose.
- Ошибка/отключение WMI не роняет приложение и диагностируется.
- Проверка выполняется только на `windows-brat`, не на dev box.
- TraceEvent и его exclusive runtime files исчезают из output; before/after приложены к PR.

### Готовый промпт

```text
Задача DR-07 для VPNRouter: прототип лёгкой замены Microsoft.Diagnostics.Tracing.TraceEvent в Windows process monitor.

Создай отдельный worktree от origin/main и ветку codex/dr-07-replace-traceevent. Никогда не запускай и не устанавливай VPNRouter на dev box. Прочитай AGENTS.md и VPNRouter.Core/CLAUDE.md, затем примени phase-task-launcher.

Обязательный первый этап: Qwen 3.8 строго qwen3.8-max-preview в read-only режиме. Он должен проследить EtwProcessMonitor от регистрации до всех подписчиков, доказать, какие Started/Stopped events реально влияют на продукт, учесть 5-second debounce HealthMonitor и сравнить WMI event watcher с минимальным polling. Другую модель не использовать; Qwen файлы не меняет.

Codex реализует только один минимальный кандидат. Первый выбор — ManagementEventWatcher с Win32_ProcessStartTrace/Win32_ProcessStopTrace, так как System.Management уже подключён. Не создавай новую abstraction hierarchy: сохрани существующий monitor contract. Обработай Stop/Dispose, повторный Start и недоступность WMI. Удали TraceEvent PackageReference только после компиляционной и runtime-проверки.

Сними Windows Release output до/после. На windows-brat через WinRM проверь: серию запусков, короткоживущий process, stop/dispose race, WMI service failure/recovery и end-to-end старт приложения, которое должно попасть под routing. Локальную машину пользователя не трогай.

Если latency/надёжность не хуже существующего flow и TraceEvent files исчезли, создай один commit, сразу push и draft PR с числами и результатами windows-brat. Иначе не предлагай merge и сохрани отчёт о причине отказа. Без --no-verify, tag/release/deploy/merge.
```

---

## DR-08 — условный прототип удаления `System.Management`

### Когда запускать

Только после DR-07. Если выбранный TraceEvent replacement использует WMI, эту задачу закрыть без реализации: удаление `System.Management` противоречит уже принятому лёгкому решению.

### Текущие WMI-участки

- `VPNRouter.Core/Services/ProcessScanner.cs`
- `VPNRouter.Core/Services/ProcessOwnership.cs`
- `VPNRouter.Core/Services/SplitTunnelDriverManager.cs`

### Возможные уже имеющиеся замены

- Toolhelp snapshot interop для процессов и parent PID.
- существующие SCM helpers; для полного перечисления driver services может понадобиться минимальный `EnumServicesStatusEx` interop.

### План эксперимента

1. Пересчитать все WMI usages после merge DR-07.
2. Сопоставить каждый запрос с существующим native interop; не писать общий WMI replacement framework.
3. Особенно проверить semantics конфликтующих VPN drivers и ownership/process tree.
4. Если нужен новый interop, добавить только минимальные сигнатуры и один runnable regression check.
5. Удалить package только если WMI usage равен нулю и `windows-brat` подтверждает все сценарии.
6. Измерить output: ожидаемый выигрыш около 315 KB выбранного runtime, поэтому крупный или рискованный diff автоматически отклонить.

### Приёмка

- Нулевое использование `System.Management`.
- Process ownership/tree и conflicting driver detection эквивалентны текущему поведению.
- Нет собственного сложного парсера или новой зависимости.
- Польза больше стоимости diff; при сомнении пакет сохраняется.

### Готовый промпт

```text
Задача DR-08 для VPNRouter: условно проверить удаление System.Management после завершения DR-07.

Создай отдельный worktree от актуального origin/main только после принятия результата DR-07; ветка codex/dr-08-system-management-prototype. Основной checkout не меняй. Прочитай AGENTS.md и VPNRouter.Core/CLAUDE.md, затем примени phase-task-launcher. Никогда не запускай VPNRouter на dev box.

Сначала Qwen 3.8 строго qwen3.8-max-preview выполняет read-only аудит всех System.Management usages после DR-07. Если новый process monitor использует ManagementEventWatcher, немедленно останови задачу и зафиксируй решение сохранить System.Management. Другую модель не использовать.

Если WMI осталось только в ProcessScanner, ProcessOwnership и SplitTunnelDriverManager, сопоставь запросы с уже имеющимся Toolhelp/SCM interop. Не создавай общий framework. Новый native interop допускается только минимальный, например EnumServicesStatusEx, и только если точно сохраняет semantics process tree/ownership и conflicting VPN driver detection.

Собери и протестируй Windows paths. На windows-brat проверь parent/owner resolution, короткоживущие процессы, detection активного и неактивного конфликтующего driver service, access denied и stop/dispose paths. Удаляй PackageReference лишь при нулевом WMI usage и успешной runtime-проверке. Сними output before/after; ожидаемый выигрыш невелик, поэтому при крупном или рискованном diff сохрани пакет.

Commit/push/draft PR только при полном выполнении критериев. Иначе верни короткий отчёт «сохранять без изменений». Без --no-verify, tag/release/deploy/merge.
```

---

## Не создавать задачи без новых данных

| Кандидат | Решение | Причина |
|---|---|---|
| `Spectre.Console.Cli` → `System.CommandLine` | сохранять | 13 команд, attributes и DI; Spectre всё равно нужен для rendering, миграция добавит пакет и перепишет CLI ради малого выигрыша |
| Serilog → Microsoft.Extensions.Logging | сохранять | Core принимает `Serilog.ILogger`, а стандартного file sink нет; стоимость миграции непропорциональна |
| ручной Win32 interop → CsWin32 | сохранять | runtime почти не уменьшится, а driver/WFP ABI — критичная зона |
| `RuleSetCacheManager` → `PolicyHttpClient` | сохранять | клиент короткоживущий, создаётся только при stale cache и сразу освобождается |
| общий `RetryAsync` helper | не создавать | всего две копии; новая связность не уменьшает зависимости |
| `System.Drawing` в TestMcp → SkiaSharp | сохранять | dev-tool-only, пользовательскую сборку не увеличивает |
| YamlDotNet, Avalonia, SkiaSharp/HarfBuzz, AndroidX, Headless tests | сохранять | это основные product capabilities, не маленькие обёртки |

## Итоговый порядок решений

1. Сначала DR-01–DR-05: короткие независимые PR без новых зависимостей.
2. Затем DR-06: принять только по результату physical-device проверки и измерений.
3. Затем DR-07: принять только по результату `windows-brat` end-to-end.
4. DR-08 запускать лишь если архитектурный итог DR-07 делает удаление WMI возможным; иначе сохранить `System.Management`.
