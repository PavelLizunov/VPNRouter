# Результаты аудита тестов роем Gemini: Пакет 10 (System Seams, OS Infrastructure & CLI Contracts)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **15**
Охвачено тестов: **114**

## Сводная статистика по Пакету 10

- **KEEP (Сохранить без изменений)**: **89** (78.1%) — фундамент стабильности ядра и системных вызовов: `IProcessRunner` и `IHttpClient` контракты, защита от утечек дескрипторов процессов (`ProcessQuery`), безопасные сигналы Unix (`pidfd`), PnP-менеджер Wintun и атомарное сохранение состояния CLI `StateFile`.
- **MERGE / SIMPLIFY (Объединить)**: **20** (17.5%) — тесты парсинга путей процессов, перечисления файлов и фрагментированные проверки аргументов сервиса.
- **DROP (Кандидаты на удаление)**: **5** (4.4%) — 2 тавтологии C# Record/Exception свойств, 1 дубликат резолвера `sc.exe`, 1 тривиальный тест BCL `EventWaitHandle` и 1 тест рефлексии.

---

## Детализация по файлам Пакета 10

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `IProcessRunnerContractTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Базовый оракул запуска процессов: перехват stdout/stderr, отмена через CancellationToken, корректный kill. |
| 2 | `IHttpClientContractTests.cs` | 11 | 11 | 0 | 0 | 100% KEEP | Контракт сетевых политик: таймауты, ретраи при 503, защита от DoS (ограничение размера тел). |
| 3 | `IFileSystemContractTests.cs` | 10 | 8 | 2 | 0 | KEEP | Полноценный контракт абстракции файловой системы: атомарные блокировки, потокобезопасность. |
| 4 | `ProcessQueryTests.cs` | 9 | 9 | 0 | 0 | 100% KEEP | Критическая защита от утечек kernel-хэндлов Windows (заменяет `Process.GetProcessesByName().Length`). |
| 5 | `ProcessOwnershipTests.cs` | 11 | 6 | 5 | 0 | CLEANUP | Защита от перехвата PID и проверка принадлежности исполняемых файлов каталогам. 4 пути объединить. |
| 6 | `ProcessImagePathTests.cs` | 7 | 6 | 1 | 0 | KEEP | Разрешение полных путей запущенных процессов через Win32 API (`QueryFullProcessImageName`). |
| 7 | `ProcessHandleDisposeOrderingTests.cs` | 2 | 1 | 1 | 0 | KEEP | Инвариант `EnableRaisingEvents = false` перед `Kill()` для предотвращения ложных краш-лупов. |
| 8 | `PsProcessLineParserTests.cs` | 5 | 2 | 3 | 0 | CLEANUP | Парсинг вывода Unix `ps` для сплит-туннеля на macOS. 3 одиночных теста объединить в Theory. |
| 9 | `UnixOwnedProcessSignalTests.cs` | 8 | 8 | 0 | 0 | 100% KEEP | Безопасная отправка сигналов дочерним процессам через Linux `pidfd_send_signal` с защитой от переиспользования PID. |
| 10 | `WindowsPnpDeviceManagerTests.cs` | 11 | 11 | 0 | 0 | 100% KEEP | Поиск и удаление адаптеров Wintun по аппаратному ID (`SWD\Wintun`) с защитой от удаления чужих сетевых карт. |
| 11 | `WindowsServiceCommandTests.cs` | 12 | 6 | 5 | 1 | CLEANUP | Формирование команд службы Windows (`sc.exe`): кавычки, защита от path injection. 1 дубликат — DROP. |
| 12 | `ConflictingVpnDetectorTests.cs` | 9 | 7 | 0 | 2 | CLEANUP | Детекция конфликтующих VPN-клиентов. 2 теста свойств рекорда и эксепшена — DROP. |
| 13 | `CliGenerationStateCharacterizationTests.cs` | 7 | 7 | 0 | 0 | 100% KEEP | Fencing поколений процессов CLI, атомарная замена `state.json` через mutex и карантин поврежденных файлов. |
| 14 | `CliStopHandleCharacterizationTests.cs` | 2 | 1 | 0 | 1 | CLEANUP | 1 тест времени жизни Pinned SafeHandle — KEEP. 1 тест стандартного `EventWaitHandle` BCL — DROP. |
| 15 | `P07CliStopSourceGuardTests.cs` | 4 | 3 | 1 | 0 | KEEP | Статический контроль порядка вызова IPC в CLI-командах (Stop/Start/StateFile). |

---

## Главные выводы аудита Пакета 10

1. **Фундаментальная надежность системных абстракций**:
   Тесты в `IProcessRunnerContractTests`, `IHttpClientContractTests`, `IFileSystemContractTests` и `ProcessQueryTests` гарантируют корректность работы низкоуровневых операций во всех подсистемах приложения (исключение утечек памяти, дескрипторов ОС и предотвращение зависаний процессов).

2. **Безопасность процессов на уровне ядра ОС**:
   - `UnixOwnedProcessSignalTests` гарантирует, что сигнал завершения процесса отправляется только целевому процессу через `pidfd`, исключая случайное убийство другого процесса при переиспользовании PID операционной системой.
   - `WindowsPnpDeviceManagerTests` гарантирует строгую фильтрацию по аппаратному идентификатору `SWD\Wintun`, предотвращая повреждение сторонних сетевых карт хоста при очистке адаптеров.

3. **Кандидаты на очистку**:
   5 тестов представляют собой тавтологические проверки стандартного компилятора C# (присвоение параметров конструктора свойствам `ConflictingProcessInfo`) либо стандартных классов .NET BCL (`EventWaitHandle`), не несущих ценности для VPNRouter.
