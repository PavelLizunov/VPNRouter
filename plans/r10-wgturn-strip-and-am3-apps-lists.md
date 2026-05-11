# r10 финал — wgturn strip + AM-3 design (Include/Exclude separate lists)

**Triggered** (2026-05-11): user заметил что в Windows ZIP'е v2.32.1-r2
случайно попал `app\bin\wgturn-cli.exe` (10.1 MB) — мой локальный
`build.ps1` запуск сделал bundle step [6/9] потому что репо `wgturn-core`
теперь public и `gh CLI` залогинен. На Mac/Linux артефактах из CI его
НЕТ. Inconsistency → нужно убрать перед cut'ом stable.

Параллельно user поднял design issue: Apps Include/Exclude mode в UI
сейчас разделяет ОДИН список чекбоксов — но логически это должны быть
ДВА независимых списка приложений.

## Part 1 · Cut wgturn-cli from r10 (ACTION)

### Что в r2 не так

`VPNRouter-v2.32.1-r2-win.zip` содержит:
```
app/bin/wgturn-cli.exe   (10.1 MB)
```

В Mac `.zip` / `.dmg` / Linux `.tar.gz` / `.deb` — НЕТ.

### Почему это плохо

1. **Inconsistency между платформами** — Windows-юзер получает 10 MB
   бонусом, который не используется ни одним UI flow в r10.
2. **Несоответствие плану** — wgturn идёт в отдельный feature cycle
   через on-demand download (см. `wgturn-on-demand-download.md`).
3. **Privacy/legal nuance** — третий-party binary без UI disclosure не
   надо bundled в stable installer.

### Action plan для strip

| Шаг | Что | Effort |
|---|---|---|
| 1 | Закомментировать / удалить `[6/9] Bundling wgturn-cli.exe` блок в `build.ps1` | 5 мин |
| 2 | Также удалить ref на `bin\wgturn-cli.exe` из `[8/9]` file list copy | 5 мин |
| 3 | Bump `AppVersion.Version` 2.32.1-r2 → 2.32.1-r3 | 1 мин |
| 4 | Commit + push в оба remote | 2 мин |
| 5 | git tag v2.32.1-r3 + push tag (триггерит Mac+Linux CI заново) | 2 мин |
| 6 | `build.ps1 -Version 2.32.1-r3 -Upload` (чистый Windows ZIP) | 5 мин |
| 7 | `gh release edit v2.32.1-r3 --prerelease --notes` | 2 мин |
| 8 | `gh release delete v2.32.1-r2 --yes` | 1 мин |
| 9 | Verify: ZIP не содержит `wgturn-cli.exe`, 12 assets, CI green | 5 мин |
| 10 | MCP / config.yaml verify повторно (Core unchanged, должно быть identical) | 5 мин |

Total: ~30 мин.

### Acceptance

- [ ] `unzip -l VPNRouter-v2.32.1-r3-win.zip | grep wgturn` → пусто
- [ ] r3 release: 12/14 assets (Android всё ещё missing per Phase 2 known issue)
- [ ] r2 release deleted
- [ ] v2.32.0 stable осталась "Latest"; r3 — Pre-release
- [ ] `verify-release-integrity` CI green на r3
- [ ] Mac DMG + Linux deb/AppImage/tar.gz CI green на r3

### После r3 verify — stable cut

```bash
gh release create v2.32.1 <12-assets> --title "..." --notes "..."
gh release edit v2.32.1 --prerelease=false --latest
gh release delete v2.32.1-r3 --yes
```

(автоматически по user-команде "cut" / "ok" / "promote")

## Part 2 · AM-3 — Include/Exclude separate UI lists (DESIGN)

### Проблема — что user поднял

> "Скорее всего, для них должны быть разные списки приложений, это
> кажется логичным, так как вероятность того что приложение будет
> перенесено из категории в категорию крайне мала"

Текущее состояние (после AM-1+F-B + AM-2 wave-2):

| Layer | Что | Состояние |
|---|---|---|
| Core (AM-1) | `AppSettings.App.RoutingAppsInclude` + `RoutingAppsExclude` (List\<string\>) | ✅ separate lists в YAML |
| Core (AM-1) | `ConfigGenerator` branches: include → `process_name`→proxy / exclude → `process_name`→direct | ✅ читает правильный список |
| UI (AM-2) | Segmented toggle Include/Exclude сверху ApplicationsPage | ✅ переключает `RoutingAppsMode` |
| UI (existing) | Master-detail категории + чекбоксы apps | ⚠️ **бинды на legacy `Profile.Processes` / `CustomApps`**, НЕ на `RoutingAppsInclude/Exclude` |

**Что фактически происходит при переключении mode**:
- Юзер кликает Include → mode=include persists, **но чекбоксы не меняются** (показывают ту же выборку из legacy fields)
- Юзер кликает Exclude → mode=exclude persists, **чекбоксы те же самые**
- При apply / connect:
  - В include mode: `ConfigGenerator` фолбэкается на legacy `resolvedProcessNames`
    (потому что `RoutingAppsInclude` пуст — UI ничего туда не пишет)
  - В exclude mode: то же самое — `ConfigGenerator` использует empty
    `RoutingAppsExclude` → не добавляет process_name rules с outbound=direct
    → `route.final = "proxy"` маршрутизирует **ВСЁ через VPN**, эквивалент
    full tunnel

Это **фактически exclude mode не работает** без explicit population
`RoutingAppsExclude[]`. И include mode работает только через legacy
fallback, не через new fields.

### Design — что должно быть

#### Концептуально

- В include mode чекбоксы = «какие apps идут через VPN»
- В exclude mode чекбоксы = «какие apps НЕ идут через VPN»
- **Два независимых списка selection state** — user может в одном моде выбрать Chrome+Firefox, переключиться в другой mode, выбрать Steam+bank, и обратно — first selection восстановится.
- Apps shown в обоих modes одинаковые (тот же master-detail list of categories + apps) — меняется только **набор checked** apps.

#### Storage (already AM-1 done)

```yaml
app:
  routing_apps_mode: include   # | exclude
  routing_apps_include:        # checked in include mode
    - chrome.exe
    - firefox.exe
  routing_apps_exclude:        # checked in exclude mode
    - steam.exe
    - bank-client.exe
```

#### UI binding rewire

Файлы для рефактора:

- `AppItemViewModel.IsChecked` — сейчас `[ObservableProperty]` с своим
  backing field. Сделать computed-aware:

  ```csharp
  // Старый код:
  [ObservableProperty] private bool _isChecked;

  // Новый — backed by parent VM's RoutingAppsMode:
  public bool IsChecked
  {
      get => _parent.IsAppCheckedInCurrentMode(ProcessName);
      set => _parent.SetAppCheckedInCurrentMode(ProcessName, value);
  }
  ```

- `MainWindowViewModel` (или новый helper):

  ```csharp
  public bool IsAppCheckedInCurrentMode(string processName)
  {
      var list = RoutingAppsMode == "exclude"
          ? _settings.App.RoutingAppsExclude
          : _settings.App.RoutingAppsInclude;
      return list.Contains(processName, StringComparer.OrdinalIgnoreCase);
  }

  public void SetAppCheckedInCurrentMode(string processName, bool checked)
  {
      var list = RoutingAppsMode == "exclude"
          ? _settings.App.RoutingAppsExclude
          : _settings.App.RoutingAppsInclude;

      var idx = list.FindIndex(x => string.Equals(x, processName, StringComparison.OrdinalIgnoreCase));
      if (checked && idx < 0)
          list.Add(processName);
      else if (!checked && idx >= 0)
          list.RemoveAt(idx);

      SaveSettings();
      RefreshAppCheckboxes(); // notify all AppItemViewModel что IsChecked changed
      HasPendingAppChanges = IsConnected;
  }
  ```

- `OnRoutingAppsModeChanged` partial method:

  ```csharp
  partial void OnRoutingAppsModeChanged(string value)
  {
      // ...existing persist code...
      RefreshAppCheckboxes(); // ВАЖНО — после переключения mode перерисовать UI
  }
  ```

- `RefreshAppCheckboxes` — вызывает `OnPropertyChanged(nameof(IsChecked))`
  для каждого AppItemViewModel в master-detail.

#### Migration (AM-3 specific)

Для существующих юзеров у которых ещё есть legacy `Profile.Processes` /
`CustomApps`:
- На первой загрузке после AM-3, если `RoutingAppsInclude` пуст И
  `Profile.Processes` (или `CustomApps`) не пуст → seed
  `RoutingAppsInclude` с теми же entries (preserve user's existing
  selection в include mode). `RoutingAppsExclude` остаётся пустым.
- Этот шаг уже частично делается в AM-1 chip's
  `SettingsMigrator.Migrate_2_to_3`, но нужно проверить что это
  работает не только для свежих, но и для тех у кого legacy populated.

#### Edge cases

- **Group master toggle (`AppGroupViewModel.IsChecked`)** — должен
  правильно работать в обоих modes: «все apps в группе routed»
  (include) vs «все apps в группе bypassing» (exclude). Логика
  `(group.Apps.All(a => a.IsChecked))` остается, но `IsChecked` теперь
  mode-aware → работает автоматически.
- **Custom apps add** (`AddCustomAppCommand`) — добавляет в текущий mode
  list автоматически (т.к. set IsChecked = true сразу после Add).
- **Full Tunnel banner** (когда `IsSplitTunnel = false`) — показывается
  как обычно, выбор apps игнорируется по любому mode (route.final=proxy,
  process_name rules не применяются). UI остается disabled-with-banner.
- **Profile reset** — если в будущем будет «reset to profile defaults»
  кнопка, она должна reset'ить `RoutingAppsInclude` к profile.processes
  и оставить `RoutingAppsExclude` пустым.

### Implementation outline

Файлы для редактирования:

| Файл | Что |
|---|---|
| `VPNRouter.App/ViewModels/AppItemViewModel.cs` | `IsChecked` getter/setter мост к MainWindowVM helper |
| `VPNRouter.App/ViewModels/AppGroupViewModel.cs` | `IsChecked` getter/setter аналогично |
| `VPNRouter.App/ViewModels/MainWindowViewModel.cs` | `IsAppCheckedInCurrentMode` + `SetAppCheckedInCurrentMode` + `RefreshAppCheckboxes` |
| `VPNRouter.App/ViewModels/MainWindowViewModel.cs` (partial in SimpleMode.cs?) | `OnRoutingAppsModeChanged` → call `RefreshAppCheckboxes` |
| `VPNRouter.Core/Services/SettingsMigrator.cs` | Verify v2→v3 правильно seeds RoutingAppsInclude из Profile.Processes когда legacy populated |
| Tests | AppGroup/Item ViewModel tests на mode-switching round-trip |

Estimate: 3-4 ч включая tests + MCP verify.

### Chip vs inline

Можно сделать одним chip'ом — пути изменения сфокусированы (1 Core file,
3 App files, тесты). Параллельно с любой другой работой safe — touch
никаких overlapping файлов с Part 1 (которая только build.ps1 + AppVersion).

### Severity / прioритет

**P1** — пользователю обещали 2-mode фичу, в r10 она half-broken:
mode-toggle persists но фактически exclude всегда означает full-tunnel
(empty list → no process_name rules → route.final=proxy).

### Когда делать

**Опция A**: AM-3 в этом же r10 cycle перед stable cut → ship -r4 после
-r3 (wgturn strip) → cut stable v2.32.1 с полноценной 2-mode фичей.

**Опция B**: cut stable v2.32.1 сейчас (с known-issue в release notes
«Exclude mode currently routes all traffic — UI wiring planned for
v2.32.2»), AM-3 идёт в v2.32.2 hotfix.

Я склоняюсь к **опции A** — feature должна быть feature-complete
на stable. +1 chip это ~3-4 ч, ничтожно по сравнению с потерей
доверия от half-working фичи в release notes.

## Recommendation

```
Step 1 (immediate, autonomous): cut wgturn-cli from build.ps1 → ship -r3
Step 2 (await user "ok"):      spawn AM-3 chip → wire UI lists
Step 3 (after AM-3 chip done): ship -r4 → cut stable v2.32.1
```

Wgturn on-demand chips (W-1/W-2/W-3/W-4 из `wgturn-on-demand-download.md`)
идут после v2.32.1 stable как v2.32.2 cycle.
