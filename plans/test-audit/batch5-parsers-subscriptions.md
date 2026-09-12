# Результаты аудита тестов роем Gemini: Пакет 5 (Subscriptions & Protocol Parsers)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **15**
Охвачено тестов: **139**

## Сводная статистика по Пакету 5

- **KEEP (Сохранить без изменений)**: **106** (76.3%) — критические тесты парсеров всех протоколов (VLESS, Reality, Hysteria2, TUIC, Shadowsocks, AmneziaWG, Slipstream/DNS-tunnel), защиты от плейсхолдеров, подписок и агрегации пулов.
- **MERGE / SIMPLIFY (Объединить)**: **32** (23.0%) — избыточно фрагментированные тесты парсинга полей одного и того же URI, объединяемые в чистые параметризованные тесты `[Theory]`.
- **DROP (Кандидаты на удаление)**: **1** (0.7%) — тест `RemoteVersionChecker_LogsDoNotContainToken`, который делает неизолированный живой сетевой вызов к `api.github.com` и падает в офлайне/без прокси.

---

## Детализация по файлам Пакета 5

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `SubscriptionFetcherParserTests.cs` | 8 | 8 | 0 | 0 | 100% KEEP | Парсинг подписок (plain, base64, JSON wrapper), защита от дубликатов и битых тел. |
| 2 | `SubscriptionFetcherPlaceholderTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Защита от просачивания плейсхолдер-серверов в рабочий пул подписок. |
| 3 | `SubscriptionRefreshDiffTests.cs` | 8 | 8 | 0 | 0 | 100% KEEP | Инвариант G3: вычисление сигнатур серверов для предотвращения лишних реконнектов туннеля при обновлении подписок. |
| 4 | `SubscriptionUrlRedactionTests.cs` | 4 | 3 | 0 | 1 | CLEANUP | 3 теста очистки логов — KEEP. 1 тест (`RemoteVersionChecker...`) делает живой WAN-запрос к GitHub (DROP/FIX). |
| 5 | `SubscriptionUserInfoTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Парсинг квот и сроков действия из заголовка `Subscription-Userinfo` для UI карточек. |
| 6 | `VlessUriParserTests.cs` | 15 | 4 | 11 | 0 | CLEANUP | Высокоценный парсер VLESS/Reality, но сильно фрагментирован (по 1 ассерту на метод). 11 тестов объединить в Theories. |
| 7 | `ServerUriParserTests.cs` | 17 | 17 | 0 | 0 | 100% KEEP | Золотой стандарт тестов: поддержка всех протоколов, Base64/Base64URL, IPv6, портов и очистки секретов в исключениях. |
| 8 | `ServerUriParserDnsTunnelTests.cs` | 23 | 12 | 11 | 0 | CLEANUP | Парсер DNS-tunnel (slipstream), проверка PEM-сертификатов и сенсинелов. 11 однотипных проверок схемы объединить. |
| 9 | `PerformanceShareLinkTests.cs` | 11 | 10 | 1 | 0 | KEEP | Тесты паритета zero-allocation Span-парсера со строковым парсером. 1 дубль insecure TLS объединить. |
| 10 | `ClashYamlParserTests.cs` | 8 | 8 | 0 | 0 | 100% KEEP | Детекция Clash YAML подписок, 8 MB DoS-лимит и декодирование нестандартных Shadowsocks паролей. |
| 11 | `AmneziaWgEndpointTests.cs` | 15 | 11 | 4 | 0 | KEEP | Парсинг и генерация эндпоинтов AmneziaWG (AWG2), валидация ключей с `+` и `/`. 4 дубля валидации объединить. |
| 12 | `AwgJsonContextRoundTripTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Пин исходного генератора `AppJsonContext` против потери полей `AwgConfig` при AOT-сериализации. |
| 13 | `HysteriaBrutalCalibrationTests.cs` | 5 | 0 | 5 | 0 | CLEANUP | Парсинг параметров калибровки Brutal (скорости up/down). Все 5 тестов красиво сворачиваются в 2 `[Theory]`. |
| 14 | `HysteriaSoleProxyTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Защита от регрессии ТСПУ: когда выбран Hy2, весь трафик (включая TCP) идёт через него без расщепления на VLESS. |
| 15 | `VlessServersResolverTests.cs` | 9 | 9 | 0 | 0 | 100% KEEP | Ядро разрешения серверов подписок: предотвращает старт без прокси-аутбаунда (silent leak v2.28.2). |

---

## Главные выводы аудита Пакета 5

1. **Высочайшая функциональная ценность**:
   Пакет 5 показал наивысший процент реальной ценности тестов (более 99% тестов тестируют реальную доменную логику). Здесь практически нет «source guards», в отличие от фаервола и TgProxy.

2. **Опасность живого сетевого запроса в юнит-тесте**:
   В `SubscriptionUrlRedactionTests.cs` метод `RemoteVersionChecker_LogsDoNotContainToken` вызывает реальный `GetLatestTagAsync` без перехвата HTTP-клиента, обращаясь напрямую к серверам GitHub. В изолированном окружении (без интернета или без прокси) этот тест падает и потенциально пишет неотфильтрованный стек ошибки. Он должен быть либо переведён на `FakeHttpClient`, либо удалён.

3. **Возможность оптимизации через `[Theory]`**:
   В файлах `VlessUriParserTests`, `ServerUriParserDnsTunnelTests` и `HysteriaBrutalCalibrationTests` суммарно около 27 тестов являются микро-вариациями одной и той же функции с разными строковыми входными данными. Свертывание их в параметризованные `[Theory]` сократит код тестов на ~400 строк при сохранении 100% тестового охвата.
