# VPNRouter — Memory Footprint Reduction Research

**Date**: 2026-04-20
**Baseline**: v2.20.4 prerelease, UI работает на .NET 8 + Avalonia 11.3.12,
self-contained publish, на Windows ~200–240 MB (reported), на macOS аналогично.
Уже сделано: FreeConfigs lazy-load + IDisposable (v2.20.1).

**Цель**: найти все реалистичные способы сократить RAM-footprint без
регрессий в функциональности. Для каждой техники — что делает, какая
ожидаемая экономия, риски, стоит ли VPNRouter'у применять.

---

## Откуда сейчас набегают 200+ MB (оценка из static audit + веб-данных)

| Составляющая | Оценка | Можно ли сжать |
|---|---|---|
| .NET 8 runtime baseline (self-contained) | ~30–40 MB | Нет без NativeAOT |
| Avalonia + Skia baseline (пустое окно) | ~40–50 MB ([источник](https://github.com/AvaloniaUI/Avalonia/discussions/14633)) | Частично |
| Skia GPU texture cache (default ~28 MB) | ~10–28 MB | Да |
| Skia font cache (typeface × size × matrix) | ~5–20 MB | Да |
| Subscription servers + VLESS lists (при больших подписках) | 5–150 MB | Да (виртуализация) |
| FreeConfigs cache (если открывался таб) | 6–7 MB | Уже lazy |
| ETW process monitor buffers | 2–5 MB | Слабо |
| App-specific VM state, timers, events | 3–8 MB | Точечно |
| ObservableCollections + их bind'ы к UI | 5–30 MB | Да (виртуализация) |
| Avalonia fluent theme dictionaries | 5–10 MB | Минимально |

Нижняя граница для нашего стека — ≈ **80–100 MB** (.NET + Avalonia + Skia
baseline, это физический минимум). Всё выше — наши данные и UI.

---

## Техники (ранжировано по ожидаемому ROI для VPNRouter)

### 1. DATAS + Server GC — **уже может быть неявно, проверить**

.NET 8 добавил Dynamic Adaptation To Application Sizes (DATAS) — новый
режим Server GC, который адаптивно увеличивает/уменьшает число managed
heap'ов. Обычный Server GC выделяет 1 heap на CPU-ядро и увеличивает
memory на 200–300 MB (гигантский overhead для desktop). DATAS меняет
это: [источник](https://maoni0.medium.com/dynamically-adapting-to-application-sizes-2d72fcb6f1ea)
приводит пример где процесс с DATAS в Server GC использовал всего
**48 MB** максимум. [Microsoft blog](https://devblogs.microsoft.com/dotnet/running-with-server-gc-in-a-small-container-scenario-part-1-hard-limit-for-the-gc-heap/)
подтверждает.

**Как включить** (`runtimeconfig.json` или csproj):
```xml
<PropertyGroup>
  <ServerGarbageCollection>true</ServerGarbageCollection>
  <GarbageCollectionAdaptationMode>1</GarbageCollectionAdaptationMode>
</PropertyGroup>
```

**Предполагаемая экономия**: 30–80 MB по сравнению с текущим Workstation GC
(если активные коллекции поместились в меньший working set). Или: ноль,
если Workstation GC уже достаточно экономен для нашего allocation pattern.

**Риск**: на UI-thread-bound desktop приложении Server GC может приводить
к более заметным паузам. DATAS частично компенсирует — делает GC
адаптивным. Но Server GC по-прежнему использует несколько потоков
параллельно, и это не идеально для Avalonia UI (где render-thread и
UI-thread чувствительны к паузам). [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc)
прямо говорит: Workstation GC оптимизирован для desktop.

**Рекомендация**: **НЕ ВКЛЮЧАТЬ** без A/B-теста. Workstation GC для
desktop — правильный дефолт. DATAS для нас вероятнее всего бесполезна
или вредна (мы не server workload).

---

### 2. `DOTNET_GCHeapHardLimit` — **возможно стоит**

Позволяет жёстко ограничить managed heap сверху: ни при каких условиях
GC не пойдёт выше. [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)

```xml
<PropertyGroup>
  <!-- 200 MB cap на managed heap -->
  <GCHeapHardLimit>0x0C800000</GCHeapHardLimit>
</PropertyGroup>
```

Или через env: `DOTNET_GCHeapHardLimit=0x0C800000` (hex — 200 MB).

**Предполагаемая экономия**: 20–50 MB в worst case (когда GC был ленив
и не собирал на границе). Или 0, если мы и так не доходим до cap.

**Риск**: если реальный working set превысит cap, получим
OutOfMemoryException. Для нас cap = 200 MB — рискованно, потому что
большая подписка + Deep Verify легко съедают 100 MB transient
allocation. 300 MB как верхняя граница — безопаснее.

**Рекомендация**: **ПОПРОБОВАТЬ 300 MB cap** в dev-билде + прогнать
Deep Verify + большие подписки. Если не падает — shipping default.
Форсирует GC работать компактнее.

---

### 3. Virtualize server ListBox'ы — **большой ROI, но рефактор**

Сейчас `Servers` и `SubscriptionServers` — `ObservableCollection<ServerViewModel>`
биндятся в `ListBox` напрямую. [Avalonia docs](https://docs.avaloniaui.net/docs/app-development/performance)
говорят: `ListBox` поддерживает `VirtualizingStackPanel` и recycle — но
нужно проверить, что у нас это **реально включено**. Если в XAML задан
обычный `StackPanel` как `ItemsPanel` или `ScrollViewer` обернут не по
правилам — виртуализация выключается.

Пользователь с 500+ серверами в подписке → 500+ `ServerViewModel`
инстансов + их UI control'ов одновременно в визуальном дереве = 30–80 MB.

**Как включить правильно**:
```xml
<ListBox ...>
  <ListBox.ItemsPanel>
    <ItemsPanelTemplate>
      <VirtualizingStackPanel/>
    </ItemsPanelTemplate>
  </ListBox.ItemsPanel>
</ListBox>
```

Плюс: обернуть в `ScrollViewer` НЕ нужно — `ListBox` сам скроллится,
внешний `ScrollViewer` ломает виртуализацию.

**Предполагаемая экономия**: 30–80 MB при больших подписках
(500–2000 серверов). Linear от размера.

**Риск**: теряется scroll-to-selected и ручной highlight активного
сервера — нужно проверить логику `IsActive` после виртуализации
(items пересоздаются при скролле). [Avalonia issue #14304](https://github.com/AvaloniaUI/Avalonia/issues/14304)
упоминает похожие грабли с изображениями.

**Рекомендация**: **ВЫСОКИЙ ПРИОРИТЕТ**. Аудит ServersPage и SubscribePage
XAML — убедиться что виртуализация реально работает. Если нет — фикс.

---

### 4. Периодический `SKGraphics.PurgeAllCaches()` — **простая оптимизация**

[Avalonia discussion #13026](https://github.com/AvaloniaUI/Avalonia/discussions/13026)
показывает реальный кейс: `SKGraphics.PurgeAllCaches()` снял 40 MB
(с 250 → 210 MB) в Avalonia приложении. [SkiaSharp docs](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skfontmanager)
подтверждают — font cache растёт без bound'а.

**Как**:
```csharp
// Вызывать при idle, например в timer:
SkiaSharp.SKGraphics.PurgeAllCaches();
// Или точечнее:
SkiaSharp.SKGraphics.PurgeFontCache();
```

**Предполагаемая экономия**: 10–40 MB после того как пользователь
поработал с UI (font cache заполняется от разных шрифтов/размеров).

**Риск**: следующий рендер будет чуть медленнее (кеш регенерируется).
Визуально не заметно если чистить при неактивном UI (например в tray).

**Рекомендация**: **ДОБАВИТЬ**. Интегрировать в `_runtimeStatusTimer`:
раз в 60 секунд при неактивном окне вызывать `PurgeAllCaches`. Код —
5 строк, риск минимальный.

---

### 5. `SKGraphics.SetFontCacheLimit` — **точечная настройка**

Вместо полной очистки — установить верхнюю границу кеша.

```csharp
// 4 MB bytes, 64 entries
SkiaSharp.SKGraphics.SetFontCacheLimit(4 * 1024 * 1024);
SkiaSharp.SKGraphics.SetFontCacheCountLimit(64);
```

**Экономия**: 3–10 MB (кеш не растёт выше). [Skia API](https://api.skia.org/classSkGraphics.html)
документирует.

**Риск**: при достижении лимита старые typeface'ы выбрасываются →
пересоздаются при следующем использовании. Мы не используем много
шрифтов (системный Inter + mono), так что cap'ы выше достаточно.

**Рекомендация**: **ДОБАВИТЬ** при старте приложения. Одна строка.

---

### 6. `PublishTrimmed=true` — **осторожно с Avalonia**

.NET 8 trimmer удаляет неиспользуемый код при `PublishTrimmed=true`.
Обычно это **только про размер binary**, не runtime RAM. Но: меньше
загруженного кода в память = меньше working set. Реальный выигрыш в
runtime — 5–15 MB.

**Риски для Avalonia**:
- Avalonia активно использует reflection для binding'ов. [Avalonia AOT docs](https://docs.avaloniaui.net/docs/deployment/native-aot)
  говорят что нужно прописать `TrimmerRootAssembly` для `Avalonia.Controls`,
  `Avalonia.Base`, `Avalonia.Markup.Xaml`. Иначе trimming сломает рантайм.
- `CommunityToolkit.Mvvm` 8.3+ — trim-safe. [.NET Blog](https://devblogs.microsoft.com/dotnet/announcing-the-dotnet-community-toolkit-830/)
  подтверждает. Но если у нас старая версия — перед trimming надо
  обновить.
- `YamlDotNet` — не trim-safe (reflection на тип свойств). Нужно
  проверить.

**Рекомендация**: **НЕ ТРОГАТЬ** в этом цикле. Trimming для Avalonia
требует полного прогона всех UI-путей на тестовом билде, а у нас нет
тестов. Риск сломать что-то runtime'ом > ожидаемая экономия.

---

### 7. `PublishReadyToRun=true` — **про startup, не про RAM**

R2R компилирует IL в native ahead-of-time → быстрый старт, НО **больше
RAM** (native код лежит в памяти вместе с IL для корнер-кейсов).
[Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish).

**Рекомендация**: **НЕ ВКЛЮЧАТЬ**. Это trade-off startup vs memory; нам
важнее memory.

---

### 8. Native AOT — **большой выигрыш, но высокий риск**

Полная ahead-of-time compilation. [Avalonia AOT docs](https://docs.avaloniaui.net/docs/deployment/native-aot)
обещают faster startup + **reduced memory footprint**. Типично 20–30%
экономии RAM по сравнению с JIT.

**Что теряется**:
- Любые reflection-based паттерны ломаются. Avalonia требует ручной
  настройки `TrimmerRootAssembly` и `DynamicDependency` атрибутов.
- CommunityToolkit.Mvvm 8.3 — работает, но для генерируемых свойств
  ObservableProperty нужно быть уверенным что source-generator
  успел.
- YamlDotNet — требует TrimmerAttribute'ов или custom converter'ов.
- Некоторые Avalonia controls могут работать нестабильно (control templates).
- `Process.GetProcessesByName` — работает, но нужна платформа-
  специфичная регистрация в AOT compatibility.

**Экономия**: 40–70 MB на нашем объёме.

**Риск**: ВЫСОКИЙ. Требует:
- Полный прогон всех UI-страниц (8 pages × 2 modes × 2 themes × 2 langs = 64 сценария)
- Устранить trim-warnings (их будет ~100+ при первом билде)
- Debug какую-нибудь странную регрессию на macOS

**Рекомендация**: **ОТЛОЖИТЬ до v2.21.x**. Возможный путь, но это
недельный проект, не задача одного релиза. Добавить в backlog.

---

### 9. Lazy-load остальных tab'ов — **ROI низкий**

Мы уже делаем это для FreeConfigs. Другие табы тоже могут:
- Network (сеть + diagnostics)
- Applications (список установленных программ)
- Tools (Zapret + TgProxy)

**Применимо?** — теоретически. Практически — Network и Tools дёшевы
(не грузят большие каталоги), Applications сканирует установленные
программы через registry/`Win32_Product` WMI (медленно, но не много
RAM).

**Экономия**: 2–5 MB.

**Рекомендация**: **ПРОПУСТИТЬ**. Мало выигрыша, риск регрессий.

---

### 10. Дисклеймер про "много dotnet.exe"

Приложение publish-нуто как `--self-contained` — процессы в диспетчере
задач называются `VPNRouter.App.exe` / `VPNRouter.CLI.exe` /
`VPNRouter.Service.exe`, **не** `dotnet.exe`. Если в диспетчере видно
много `dotnet.exe`, это:
- Visual Studio / VS Code (Roslyn, language server, extensions)
- `dotnet build` / `dotnet publish` процессы (долгоживущие билд-сервера)
- Claude Code или другие dev tools

**Не наши**. `OrphanCleanup.KillOrphans()` при старте убивает
забытые экземпляры `VPNRouter.App.exe` + `sing-box.exe`.

---

## Рекомендуемый план действий (по ROI × риск)

### ✅ Quick wins (делать в одном релизе, v2.20.5)

1. **`SKGraphics.SetFontCacheLimit(4 MB, 64)` при старте** — 1 строка,
   3–10 MB экономии, ноль риска.
2. **Periodic `SKGraphics.PurgeAllCaches()`** — раз в 60 сек при
   неактивном окне, через существующий `_runtimeStatusTimer`. 10–40 MB
   экономии после долгой сессии.
3. **Audit виртуализации ListBox'ов** в ServersPage + SubscribePage +
   FreeConfigsPage — убедиться что `VirtualizingStackPanel` реально
   работает (через Visual Studio Visual Tree inspector или Avalonia
   DevTools). Если не работает — фикс XAML. Потенциально 30–80 MB.

### ⚠️ Нужен тест, но потенциально стоит (v2.21.x)

4. **`GCHeapHardLimit=300 MB`** — попробовать в beta-релизе, если не
   падает при Deep Verify + больших подписках — ship.
5. **Полная виртуализация через `TreeDataGrid`** для Free Configs
   displayed list — если текущий ListBox не справляется.

### 🚫 Не стоит трогать

6. Server GC / DATAS — desktop, не server.
7. `PublishReadyToRun` — увеличивает RAM.
8. Native AOT — недельный проект, высокий риск, на будущее.
9. `PublishTrimmed` — сломает Avalonia без аккуратной настройки.

### 🔬 Диагностика перед шагами выше (опционально)

Профилировать реальный heap:
```bash
# 1. Запустить VPNRouter
# 2. Снять baseline:
dotnet-counters monitor --process-id <PID> System.Runtime

# 3. Снять heap dump:
dotnet-gcdump collect -p <PID>

# 4. Открыть в PerfView (Windows) или Visual Studio для анализа
```

[Microsoft guide](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump)
+ [Stefan Geiger tutorial](https://www.stefangeiger.ch/2020/04/21/dotnet-diagnostics-tools-gcdump-vs-dump.html).

Это покажет **реально** где сидит память. Без этого оценки выше — educated
guesses.

---

## Ожидаемый результат если всё из "Quick wins" применить

Baseline: 200–240 MB
После Quick wins: **150–180 MB** (экономия 40–70 MB)

Дополнительно если `GCHeapHardLimit` проходит тест: **130–150 MB**.

Ниже 130 MB в текущей архитектуре без Native AOT не опуститься —
Avalonia + Skia + .NET runtime = physics.

---

## Что делать дальше

Ждать решения пользователя — применять ли v2.20.5 quick wins сразу или
сначала снять baseline через `dotnet-gcdump` чтобы точно знать откуда
сейчас набегает 200 MB.

## Источники

- [Avalonia 10 Performance Tips](https://avaloniaui.net/blog/10-avalonia-performance-tips-to-supercharge-your-app)
- [Avalonia Performance docs](https://docs.avaloniaui.net/docs/app-development/performance)
- [Avalonia Native AOT](https://docs.avaloniaui.net/docs/deployment/native-aot)
- [Avalonia 11.1 changes](https://avaloniaui.net/blog/avalonia-11-1-a-quantum-leap-in-cross-platform-ui-development)
- [Avalonia memory discussion #14633](https://github.com/AvaloniaUI/Avalonia/discussions/14633)
- [Avalonia memory discussion #16251](https://github.com/AvaloniaUI/Avalonia/discussions/16251)
- [Avalonia image memory #14304](https://github.com/AvaloniaUI/Avalonia/issues/14304)
- [Avalonia Skia purge #13026](https://github.com/AvaloniaUI/Avalonia/discussions/13026)
- [.NET 8 DATAS by Maoni](https://maoni0.medium.com/dynamically-adapting-to-application-sizes-2d72fcb6f1ea)
- [GC config settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)
- [Workstation vs Server GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc)
- [GCHeapHardLimit design](https://devblogs.microsoft.com/dotnet/running-with-server-gc-in-a-small-container-scenario-part-1-hard-limit-for-the-gc-heap/)
- [dotnet-gcdump docs](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump)
- [.NET Memory Analysis on Linux](https://www.stefangeiger.ch/2023/10/03/dotnet-memory-analysis-linux.html)
- [Maoni .NET Memory Performance Analysis](https://github.com/Maoni0/mem-doc/blob/master/doc/.NETMemoryPerformanceAnalysis.md)
- [.NET Community Toolkit 8.3 AOT](https://devblogs.microsoft.com/dotnet/announcing-the-dotnet-community-toolkit-830/)
- [Optimize ASP.NET Core with DATAS — Thinktecture](https://www.thinktecture.com/en/net/optimize-asp-net-core-memory-with-datas/)
- [GC.WaitForPendingFinalizers](https://learn.microsoft.com/en-us/dotnet/api/system.gc.waitforpendingfinalizers?view=net-9.0)
- [.NET Memory Analysis — InfoWorld best practices](https://www.infoworld.com/article/2238991/best-practices-to-facilitate-garbage-collection-in-net.html)
- [SkiaSharp font cache](https://api.skia.org/classSkGraphics.html)
- [SKFontManager](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skfontmanager?view=skiasharp-2.88)
