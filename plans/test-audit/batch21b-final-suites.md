# Результаты аудита тестов роем Gemini: Пакет 21B (FakeIP, VpnEngine Orchestration & Zapret Cache)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **15**
Охвачено тестов: **142**

## Сводная статистика по Пакету 21B

- **KEEP (Сохранить без изменений)**: **88** (62.0%) — критические миграции FakeIP под типизированные схемы sing-box 1.12+ (`VpnctlFakeIpMigrationTests`), детекция зависания туннелей (`WedgeKillPolicy`), парсер транскриптов Flowseal и победных стратегий Zapret (`ZapretFlowsealParserTests`), защита от регрессий упаковки релизов и кэш быстрых проб Zapret.
- **MERGE / SIMPLIFY (Объединить)**: **48** (33.8%) — микро-проверки состояний FakeIP в `[Theory]`, 14 предикатных тестов `IsRecentAndReliable` в `ZapretProbeCacheTests`, конструкторные проверки в `VpnEngineOrchestratorTests`.
- **DROP (Кандидаты на удаление)**: **6** (4.2%) — 3 текстовых скрапера исходников в `VpnEngineOrchestratorTests`, 1 тавтология конструктора в `ZapretManagerProcessRunnerTests` и 2 рефлексии интерфейсов в `WindowsDnsHardeningInjectionTests`.

---

## Детализация по файлам Пакета 21B

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `VpnEngineDnsLockdownLifecycleTests.cs` | 5 | 2 | 3 | 0 | CLEANUP | 2 критических теста отложенной блокировки DNS при успешном/неуспешном прогреве — KEEP. 3 теста сплит-драйвера перенести. |
| 2 | `VpnEngineFailoverPhaseDispatchTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Фикс P02 FAIL-1: корректная маршрутизация рестарта через безопасный teardown в post-start фазе. |
| 3 | `VpnEngineOrchestratorTests.cs` | 16 | 6 | 7 | 3 | CLEANUP | Статусы холостого хода, резолв кастомных путей. 3 текстовых grep по C# — DROP. Конструкторные тесты слить в Theory. |
| 4 | `VpnEngineProbeFailoverGateTests.cs` | 5 | 4 | 1 | 0 | KEEP | Защита v2.44.2 P0: прогретый туннель не разрывается при ошибке замера задержки Clash API. |
| 5 | `VpnEngineRemoveExcludedAppsTests.cs` | 8 | 6 | 2 | 0 | KEEP | Исключение приложений из профиля (фикс бага Bug-r9-I): регистронезависимость, суффиксы `.exe`. |
| 6 | `VpnEngineSplitTunnelResolveTests.cs` | 5 | 4 | 1 | 0 | KEEP | Разрешение путей исключенных приложений через системный PATH и запуск драйвера ядра. |
| 7 | `VpnctlFakeIpMigrationTests.cs` | 29 | 16 | 12 | 1 | CLEANUP | Крупный сьют типизированного FakeIP (sing-box 1.12+): предотвращение петель маршрутизации DNS. 12 тестов объединить в Theory. |
| 8 | `VpnctlPackagingCharacterizationTests.cs` | 12 | 8 | 4 | 0 | KEEP | Контроль поставки бинарников: проверка sha256 и восстановление поврежденных архивов из кэша. |
| 9 | `WedgeKillPolicyTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Стейт-машина детекции зависшего sing-box: отсечение ложных срабатываний во время прогрева и перезапуск при зависании. |
| 10 | `WindowsDnsHardeningInjectionTests.cs` | 4 | 2 | 0 | 2 | CLEANUP | 2 теста передачи вызовов через шов `IWindowsDnsHardening` — KEEP. 2 рефлексии интерфейса — DROP. |
| 11 | `XhttpTransportTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Проверка транспорта VLESS XHTTP: автоматическое удаление несовместимого flow `xtls-rprx-vision`. |
| 12 | `YamlStaticContextRoundTripTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Проверка compile-time генератора сериализации YAML (`YamlStaticContext`): защита от потери полей DTO при компиляции. |
| 13 | `ZapretFlowsealParserTests.cs` | 14 | 14 | 0 | 0 | 100% KEEP | Разбор логов тестирования Flowseal: выбор победной стратегии с максимальным числом успешных проверок. |
| 14 | `ZapretManagerProcessRunnerTests.cs` | 7 | 5 | 1 | 1 | CLEANUP | Запуск Zapret в неперенаправленной консоли Cygwin, детекция быстрого краша (<2с). 1 тест конструктора — DROP. |
| 15 | `ZapretProbeCacheTests.cs` | 27 | 10 | 17 | 0 | CLEANUP | Кэш быстрых стратегий Zapret: откат победных стратегий при сбоях. 17 единичных проверок предикатов слить в 2 Theory. |

---

## Главные выводы аудита Пакета 21B

1. **Безопасность миграций FakeIP и DNS (`VpnctlFakeIpMigrationTests`)**:
   Тесты защищают от критической архитектурной ошибки — циклического зацикливания DNS-запросов (DNS routing loop), когда FakeIP резолвер пытается разрешить сам себя через туннель.

2. **Высокая функциональная плотность в Zapret**:
   Парсеры `ZapretFlowsealParserTests` и кэши `ZapretProbeCacheTests` обеспечивают автономный подбор стратегий обхода блокировок без ручного редактирования батников пользователем.

3. **Оптимизация тестов через `[Theory]`**:
   В `ZapretProbeCacheTests` и `VpnctlFakeIpMigrationTests` почти 30 тестов проверяют одну и ту же функцию с разными флагами true/false или null. Объединение их в параметризованные тесты сократит код тестового проекта на ~350 строк без потери охвата.
