# Результаты аудита тестов роем Gemini: Пакет 19 (True-Split Driver, Slipstream, TUN & Steam Scanner)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **16**
Охвачено тестов: **177**

## Сводная статистика по Пакету 19

- **KEEP (Сохранить без изменений)**: **129** (72.9%) — уникальные низкоуровневые тесты: бинарный протокол драйвера ядра Windows (`SplitTunnelProtocolTests`), детекция и сосуществование с драйверами Amnezia/Mullvad, сайдкар DNS-туннелей `SlipstreamManager` (проверка SHA256-фингерпринта сертификатов), сканер библиотек Steam (`SteamLibraryScanner`), защита от двойного старта туннеля и блокировка разрыва TUN lock.
- **MERGE / SIMPLIFY (Объединить)**: **32** (18.1%) — объединение параметров классификации драйверов в `[Theory]`, слияние парных тестов ABI структур.
- **DROP (Кандидаты на удаление)**: **16** (9.0%) — **весь файл `SingBoxManagerRestartTunHandshakeTests.cs`** (7 тестов — устаревший текстовый поиск по исходникам), 4 теста текстового парсинга в `SingBoxManagerSuppressExitedEventTests`, 3 теста в `SplitTunnelDoubleStartGuardTests` и 2 теста фиктивных локальных DTO.

---

## Детализация по файлам Пакета 19

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `SingBoxManagerRestartTunHandshakeTests.cs` | 7 | 0 | 0 | 7 | **DROP FILE** | 100% устаревшие «source guards» из Wave 38 (поиск подстрок в `.cs` коде). 0 поведенческих тестов. |
| 2 | `SingBoxManagerRestartTunLockTests.cs` | 15 | 13 | 2 | 0 | 100% KEEP | Защита от потери TUN lock при перезапуске на Windows и Linux capability mode. |
| 3 | `SingBoxManagerSuppressExitedEventTests.cs` | 7 | 0 | 2 | 5 | CLEANUP | 4 текстовых скрапера исходников — DROP. 2 теста тестового фейка перенести в `FakeProcessRunnerTests`. |
| 4 | `SingBoxManagerTunOrphanRecoveryTests.cs` | 7 | 5 | 2 | 0 | KEEP | Анализ stderr на наличие сигнатур конфликта TUN адаптеров и автосброс флага сироты. |
| 5 | `SingleInstanceGuardTests.cs` | 2 | 1 | 1 | 0 | KEEP | Защита от повторного запуска приложения (Mutex `WaitOne` и межпроцессная сигнализация). |
| 6 | `SlipstreamManagerProvisioningTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Копирование и распаковка bundled-бинарника slipstream-client при первом запуске (фикс v2.42.0-r13). |
| 7 | `SlipstreamManagerTests.cs` | 17 | 10 | 7 | 0 | KEEP | Сайдкар DNS-туннеля: аргументы Rust, проверка SHA256 фингерпринта PEM-сертификата (защита от MITM). |
| 8 | `SmartConnectPersistenceTests.cs` | 1 | 0 | 1 | 0 | CLEANUP | Проверка порядка сохранения победителя пинга. Перенести в headless `MainWindowViewModelTests`. |
| 9 | `SplitTunnelDirectAppImpactTests.cs` | 11 | 11 | 0 | 0 | 100% KEEP | Проверка того, что прямые приложения не ломаются в режиме split-tunnel и используют DoH / LAN resolver. |
| 10 | `SplitTunnelDoubleStartGuardTests.cs` | 7 | 0 | 4 | 3 | CLEANUP | 3 текстовых grep по коду — DROP. 4 теста порядка вызовов переписать на поведенческие моки. |
| 11 | `SplitTunnelDriverManagerNetChangeTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Предотвращение краша службы Windows из-за `ObjectDisposedException` при смене сети (P1 регрессия). |
| 12 | `SplitTunnelManagerTests.cs` | 14 | 6 | 8 | 0 | KEEP | Защита от конфликтов драйверов ядра: обнаружение чужого драйвера Amnezia/Mullvad и fail-open режим. |
| 13 | `SplitTunnelProtocolTests.cs` | 58 | 55 | 3 | 0 | 100% KEEP | Математический оракул бинарного протокола драйвера ядра Windows: упаковка GUID, NT-пути DOS, защита от переполнения буфера. |
| 14 | `SteamLibraryScannerTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Парсинг библиотек Steam (файлы VDF/ACF), исключение служебных краш-репортеров и устойчивость к файловым блокировкам. |
| 15 | `StorageBlobRecoveryTests.cs` | 8 | 8 | 0 | 0 | 100% KEEP | Самовосстановление и карантин поврежденных блобов настроек без падения приложения. |
| 16 | `Phase4StjRoundTripTests.cs` | 15 | 8 | 4 | 3 | CLEANUP | Сериализация JSON DTO в snake_case. 3 теста локальных тестовых заглушек — DROP. |

---

## Главные выводы аудита Пакета 19

1. **Колоссальная ценность бинарных тестов драйвера (`SplitTunnelProtocolTests`)**:
   58 тестов в `SplitTunnelProtocolTests` формируют золотой стандарт для сетевых драйверов Windows. Они проверяют структуры байт-в-байт, по смещениям и типам, гарантируя, что ядро ОС не получит поврежденную структуру памяти и не вызовет BSOD (синий экран смерти).

2. **Ликвидация файла-балласта `SingBoxManagerRestartTunHandshakeTests.cs`**:
   Все 7 тестов файла представляют собой устаревшие текстовые проверки исходного кода (поиск строк `PreStartCleanup`, `Thread.Sleep`), написанные до появления швов абстракции. Файл подлежит полному удалению (DROP).

3. **Безопасность криптографии DNS-туннелей (`SlipstreamManagerTests`)**:
   Тесты проверяют строгое совпадение отпечатка сертификата (SHA256 fingerprint) перед запуском клиента slipstream, защищая трафик от подмены конечного узла.
