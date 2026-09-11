# Результаты аудита тестов роем Gemini: Пакет 8 (Network, DNS, Leak Protection & Custom Rules)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **15**
Охвачено тестов: **148**

## Сводная статистика по Пакету 8

- **KEEP (Сохранить без изменений)**: **106** (71.6%) — ключевые инварианты сетевой безопасности: защита от утечек DNS (LeakProtection), DNS Hardening на Linux/macOS/Windows, генерация правил маршрутизации sing-box и проверка конфликтов подсетей.
- **MERGE / SIMPLIFY (Объединить)**: **29** (19.6%) — одиночные проверки параметров, которые гораздо чище объединяются в параметризованные тесты `[Theory]`.
- **DROP (Кандидаты на удаление)**: **13** (8.8%) — 9 тестов-дубликатов строк истинностных таблиц в `DnsLockdownPolicyTests` и `StrictDnsFailoverPolicyTests`, 1 тест мутации системного DNS хоста и 3 тавтологии в `WindowsDnsHardeningTests`.

---

## Детализация по файлам Пакета 8

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `LeakProtectionTests.cs` | 22 | 20 | 2 | 0 | KEEP | Критический оракул валидации sing-box JSON: отсутствие прокси-аутбаунда, правильность DNS-стратегий. |
| 2 | `LeakProtectionAppSettingsTests.cs` | 12 | 10 | 2 | 0 | KEEP | Предварительная валидация настроек до генерации конфига: защита от F-12 и тихих сбоев. |
| 3 | `LeakProtectionMultiProtocolTests.cs` | 5 | 5 | 0 | 0 | 100% KEEP | Валидация разнородных протоколов (Hysteria2, TUIC, Shadowsocks, VLESS) в группах urltest. |
| 4 | `LeakProtectionScopeAwareTests.cs` | 16 | 14 | 2 | 0 | KEEP | Защита от утечек адресов: строгая сверка IP эндпоинтов с активными подписками. |
| 5 | `LeakProtectionAwgEndpointTests.cs` | 7 | 7 | 0 | 0 | 100% KEEP | Проверка структуры эндпоинтов AmneziaWG (наличие приватных ключей, портов, пиров) до старта sing-box. |
| 6 | `LeakAuditFixTests.cs` | 7 | 6 | 1 | 0 | KEEP | Защита от инверсии роутинга в include-режиме (Gap1) и ложных предупреждений DNS (Gap2). |
| 7 | `DnsFlusherTests.cs` | 10 | 6 | 2 | 2 | CLEANUP | Сброс DNS через `ipconfig /flushdns` и Win32. 1 неизолированный тест (дергает реальный DNS хоста) — DROP. |
| 8 | `DnsIpv4StrategyTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Инвариант G5: принудительный `ipv4_only` для DNS, если у TUN выключен IPv6 (защита от подвисаний AAAA). |
| 9 | `DnsLockdownPolicyTests.cs` | 6 | 1 | 0 | 5 | CLEANUP | Тест `Decide_FullTruthTable` полностью покрывает все 8 состояний стейт-машины. Остальные 5 фактов — дословные дубли его строк (DROP). |
| 10 | `WindowsDnsHardeningTests.cs` | 12 | 4 | 6 | 2 | CLEANUP | Настройка метрик TUN адаптера через `netsh`. 6 микро-тестов аргументов объединить в `[Theory]`, 2 тавтологии — DROP. |
| 11 | `LinuxDnsHardeningTests.cs` | 10 | 10 | 0 | 0 | 100% KEEP | Идеальный сьют: `resolvectl dns`, домен `~.`, сенсинел-файл для восстановления после аварий. |
| 12 | `MacDnsHardeningTests.cs` | 11 | 11 | 0 | 0 | 100% KEEP | Интеграция `networksetup`, `dscacheutil`, сброс mDNSResponder и восстановление исходных DNS. |
| 13 | `StrictDnsFailoverPolicyTests.cs` | 5 | 1 | 0 | 4 | CLEANUP | Тест `Decide_FullTruthTable` исчерпывающе покрывает все переходы. 4 отдельных факта — полные дубли (DROP). |
| 14 | `CustomRulesV2_30_ParserTests.cs` | 17 | 10 | 7 | 0 | CLEANUP | Парсер пользовательских правил: диапазоны портов, префиксы `!`, проверка конфликтов подсетей. |
| 15 | `CustomRulesV2_30_GeneratorTests.cs` | 11 | 6 | 5 | 0 | CLEANUP | Генерация правил sing-box и DNS-зеркалирование для правил блокировки доменов. |

---

## Главные выводы аудита Пакета 8

1. **Дублирование полных таблиц истинности (Full Truth Tables)**:
   В `DnsLockdownPolicyTests` и `StrictDnsFailoverPolicyTests` авторы сначала написали исчерпывающий параметризованный тест `Decide_FullTruthTable` на 8 кейсов, а затем создали ещё 4-5 отдельных методов `[Fact]`, проверяющих абсолютно те же самые строки таблицы с теми же входными и выходными данными. Удаление 9 таких дублей сократит кодовую базу без потери ни одного бита тестового охвата.

2. **Опасность теста `StaticFacade_Flush_NoThrowOnRealRuntime`**:
   В `DnsFlusherTests.cs` тест вызывает реальный системный метод сброса DNS без мокирования на машине, где запускаются тесты. Это неизолированный smoke-тест, загрязняющий системное состояние хоста. Он подлежит удалению (DROP).

3. **Критическая ценность DNS Hardening и LeakProtection**:
   Сьюты для Linux (`resolvectl`), macOS (`networksetup`) и Windows (`netsh`) обеспечивают первоклассную защиту от утечек DNS мимо туннеля (fail-closed / fail-open инварианты). Их сохраняем на 100%.
