# wgturn-cli — on-demand download (Zapret/TgProxy pattern)

**Status**: research + design proposal — не реализация.
**Trigger** (2026-05-11): user сделал `PavelLizunov/wgturn-core` public,
v0.1.0 released; пожелание — wire по образцу Zapret / TgProxy, чтобы
binary тянулся отдельно по требованию, а не bundled в installer.

## 1 · Goal / Non-goals

### Goal
Заменить «build.ps1 bundle Phase 1» подход (Windows-only, требует
`gh repo clone wgturn-core` локально) на runtime download by demand.
Cross-platform (Win / macOS / Linux), удобный rollout без re-issue
installer'ов.

### Non-goals
- Менять `EmergencyChannelEngine` / `EmergencyChannelManager` (Phase 2
  Core skeleton, commit `655fb6b`) — process lifecycle уже корректный.
- Реализовывать UI для Emergency Channel в этом плане. Тут только
  binary delivery + "is installed" probe + status events.
- Менять API wgturn-cli (`connect-url`, `--vk-link`, …) — это
  contract от upstream `wgturn-core`.

## 2 · Upstream — что доступно

`github.com/PavelLizunov/wgturn-core` release `v0.1.0` (2026-05-11):

| Asset name | Size | OS | Arch | Variant |
|---|---|---|---|---|
| `wgturn-cli-linux-amd64` | 10.0 MB | linux | amd64 | slim |
| `wgturn-cli-linux-arm64` | 9.4 MB | linux | arm64 | slim |
| `wgturn-cli-darwin-amd64` | 10.1 MB | darwin | amd64 | slim |
| `wgturn-cli-darwin-arm64` | 9.4 MB | darwin | arm64 | slim |
| `wgturn-cli-windows-amd64.exe` | 10.3 MB | windows | amd64 | slim |
| `wgturn-cli-embedded-linux-amd64` | 128.8 MB | linux | amd64 | embedded |
| `wgturn-cli-embedded-darwin-amd64` | 112.4 MB | darwin | amd64 | embedded |
| `wgturn-cli-embedded-darwin-arm64` | 107.1 MB | darwin | arm64 | embedded |
| `wgturn-cli-embedded-windows-amd64.exe` | 128.2 MB | windows | amd64 | embedded |

**Naming convention**: `wgturn-cli-[embedded-]{os}-{arch}[.exe]`.

**Slim vs Embedded**: embedded версия упаковывает Chromium headless-shell
(нужен для VK Calls invite handshake в некоторых flows). Slim полагается
на system Chromium / Chrome / Edge.

### Решение по дефолту
- **Default — slim**. ~10 MB, быстрая загрузка. Чаще всего у юзера уже
  стоит Chrome / Edge / Yandex Browser (которые puppeteer-go находит через
  `os.LookupEnv("PUPPETEER_EXECUTABLE_PATH")` + стандартные пути).
- **Fallback — embedded**, если первый запуск slim падает с ошибкой
  «no browser found». UI предложит «дозагрузить полную версию (~120 MB)».

## 3 · Текущее состояние (что есть, что менять)

### Что уже есть
| Файл | Состояние |
|---|---|
| `AppPaths.WgturnCliExePath` | `{DataDir}\bin\wgturn-cli[.exe]` |
| `AppPaths.WgturnCliLogPath` | `{DataDir}\logs\wgturn-cli.log` |
| `EmergencyChannelManager.Start` | бросает `FileNotFoundException` если exe нет (✅ корректно) |
| `EmergencyChannelEngine` | состояние machine, события Started/Crashed |
| `EmergencyChannelConfig` | модель: WgturnUrl, VkLink |
| `build.ps1 [6/9]` | пытается клонить + cross-compile в `tools/wgturn-cli-cache/`, в CI без auth fails (теперь не нужно) |

### Что нужно добавить
| Component | Purpose |
|---|---|
| `WgturnUpdater.cs` | Downloads binary from GitHub releases по OS/arch |
| `WgturnVariant` enum | `Slim` / `Embedded` |
| `AppSettings.WgturnVariant` | persisted user choice |
| Status events для UI | `Downloading`, `Verifying`, `Installed`, `Failed` |
| Path move | `{DataDir}\bin\` → `{DataDir}\wgturn\bin\` (отдельная папка по образцу `zapret/`, `tg-proxy/`) |

### Что нужно убрать
| File / step | Reason |
|---|---|
| `build.ps1 [6/9]` "Bundling wgturn-cli.exe" блок | больше не бандлим — на demand |
| `tools/wgturn-cli-cache/` | dev-only кэш, может остаться gitignored, но не нужен для build |
| `wgturn-cli.exe` upload в Windows ZIP | drop from `[8/9]` zip step |

## 4 · Architecture — `WgturnUpdater.cs`

### Public API (по образцу `ZapretUpdater`)

```csharp
public class WgturnUpdater
{
    private const string Repo = "PavelLizunov/wgturn-core";

    public static string WgturnDir => Path.Combine(AppPaths.DataDir, "wgturn");
    public static string BinDir   => Path.Combine(WgturnDir, "bin");
    public static string CliExePath => Path.Combine(BinDir,
        OperatingSystem.IsWindows() ? "wgturn-cli.exe" : "wgturn-cli");
    public static string VersionFilePath => Path.Combine(WgturnDir, "version.txt");
    public static string VariantFilePath => Path.Combine(WgturnDir, "variant.txt");

    public event Action<string>? StatusChanged;

    public static bool IsInstalled() => File.Exists(CliExePath);
    public static string? GetLocalVersion();
    public static WgturnVariant? GetLocalVariant();

    public async Task<string> DownloadLatestAsync(
        WgturnVariant variant = WgturnVariant.Slim,
        CancellationToken ct = default);

    public async Task<string?> CheckLatestVersionAsync(CancellationToken ct = default);
    // returns: tag_name (e.g. "v0.1.0") or null on network error
}

public enum WgturnVariant { Slim, Embedded }

public sealed class WgturnDownloadException : Exception
{
    public WgturnErrorCategory Category { get; }
    // ...
}

public enum WgturnErrorCategory
{
    GitHubRateLimit, GitHubServerError, Network, Corrupted,
    Invalid, FileSystem, UnsupportedPlatform, Concurrent, Unknown,
}
```

### Implementation skeleton (~250 LOC, на пол-чипа)

```csharp
public async Task<string> DownloadLatestAsync(
    WgturnVariant variant, CancellationToken ct)
{
    if (!await _downloadLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        throw new WgturnDownloadException(
            WgturnErrorCategory.Concurrent,
            "A wgturn-cli download is already in progress.");

    try
    {
        // 1. Detect platform
        var (assetName, expectedSha256) = ResolveAssetForCurrentPlatform(variant);
            // throws WgturnDownloadException(UnsupportedPlatform) на linux-arm32, etc

        // 2. Fetch latest release JSON
        var release = await GetLatestReleaseAsync(ct);
        var tag = release.GetProperty("tag_name").GetString()!;
        var asset = release.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == assetName);
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new WgturnDownloadException(
                WgturnErrorCategory.Invalid,
                $"Asset '{assetName}' not found in release {tag}");
        var downloadUrl = asset.GetProperty("browser_download_url").GetString()!;
        var expectedSize = asset.GetProperty("size").GetInt64();

        // 3. Stream download to temp + verify size
        Directory.CreateDirectory(BinDir);
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"wgturn-cli-{Guid.NewGuid():N}");
        try
        {
            StatusChanged?.Invoke($"Downloading {tag} ({variant}, {assetName})...");
            await DownloadStreamWithProgressAsync(downloadUrl, tempFile, expectedSize, ct);

            // 4. (optional) verify sha256 if we publish .sha256 sidecars
            //    upstream doesn't have them today — TODO ask owner

            // 5. Mark executable (chmod +x on Unix)
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tempFile,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);

            // 6. Atomic move into place
            if (File.Exists(CliExePath)) File.Delete(CliExePath);
            File.Move(tempFile, CliExePath);

            // 7. Persist version + variant
            File.WriteAllText(VersionFilePath, tag);
            File.WriteAllText(VariantFilePath, variant.ToString().ToLowerInvariant());

            _logger.Information(
                "[Wgturn] Installed {Variant} {Tag} ({Size} bytes) to {Path}",
                variant, tag, expectedSize, CliExePath);
            StatusChanged?.Invoke($"Installed {tag}");
            return tag;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
    finally
    {
        _downloadLock.Release();
    }
}

private (string assetName, string? expectedSha256)
    ResolveAssetForCurrentPlatform(WgturnVariant variant)
{
    var (os, arch) = (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
                       RuntimeInformation.OSArchitecture) switch
    {
        (true,  _,    Architecture.X64)   => ("windows", "amd64"),
        (false, true, Architecture.X64)   => ("darwin",  "amd64"),
        (false, true, Architecture.Arm64) => ("darwin",  "arm64"),
        (false, false, Architecture.X64)  => ("linux",   "amd64"),
        (false, false, Architecture.Arm64) => ("linux",  "arm64"),
        _ => throw new WgturnDownloadException(
            WgturnErrorCategory.UnsupportedPlatform,
            $"wgturn-cli is not published for this OS/arch combination."),
    };

    // Linux arm64 embedded not available — fall back to slim with warning?
    var slimOnly = os == "linux" && arch == "arm64";
    var effectiveVariant = (variant == WgturnVariant.Embedded && slimOnly)
        ? WgturnVariant.Slim
        : variant;

    var prefix = effectiveVariant == WgturnVariant.Embedded
        ? "wgturn-cli-embedded"
        : "wgturn-cli";
    var ext = os == "windows" ? ".exe" : "";
    var name = $"{prefix}-{os}-{arch}{ext}";

    // SHA256 sidecars не публикуются upstream'ом сегодня.
    // TODO попросить owner добавить .sha256 файлы для верификации.
    return (name, expectedSha256: null);
}
```

### Concurrency safety
- `SemaphoreSlim _downloadLock = new(1, 1)` как в `ZapretUpdater` — double-click
  Download button даст clean error вместо racing extract.
- Retry policy: 3 попытки с exponential backoff (2s/4s/8s) на transient
  errors (GitHub 5xx, network drops). Применять тот же helper что у Zapret.

## 5 · Path layout

Текущая раскладка (`bin/` → shared sing-box + wgturn):
```
{DataDir}/bin/
  sing-box[.exe]
  wgturn-cli[.exe]   ← Phase 1 bundled
```

Новая раскладка (отдельная папка как `zapret/`, `tg-proxy/`):
```
{DataDir}/
  bin/
    sing-box[.exe]            ← unchanged, sing-box остаётся в bin/
  zapret/                     ← existing, unchanged
    bin/winws.exe
    version.txt
  tg-proxy/                   ← existing, unchanged
    python/
    proxy/
    version.txt
  wgturn/                     ← NEW
    bin/wgturn-cli[.exe]
    version.txt
    variant.txt               ← slim | embedded
  logs/
    wgturn-cli.log            ← unchanged (root logs/)
```

### Migration
- Update `AppPaths.WgturnCliExePath`:
  ```csharp
  public static string WgturnDir => Path.Combine(DataDir, "wgturn");
  public static string WgturnCliExePath => Path.Combine(WgturnDir, "bin",
      OperatingSystem.IsWindows() ? "wgturn-cli.exe" : "wgturn-cli");
  ```
- В `SettingsMigrator.Migrate_3_to_4` (после AM-1 schema bump до v3):
  если `{DataDir}/bin/wgturn-cli[.exe]` существует — переместить в
  новый `{DataDir}/wgturn/bin/`. Если нет — noop. Не нужно ронять
  installer, просто tidy-up.
- Бамп schema_version 3 → 4 для маркировки migration done.

## 6 · UI flow (Settings → Emergency Channel section)

Не реализуем в этом плане, но дизайн заголовков:

```
┌─ Экстренный канал (wgturn-core)            ┐
│                                              │
│  Статус: ⚪ Не установлен                    │
│  [Установить (~10 MB)]   [Подробнее]        │
│                                              │
│  Версия: —                                   │
│                                              │
├─ После установки ────────────────────────────┤
│  Статус: ✓ Установлен v0.1.0 (slim)          │
│  [Обновить]   [Удалить]                      │
│                                              │
│  Полная версия с Chromium (~120 MB):         │
│  [Загрузить полную версию]                   │
│                                              │
│  ⓘ Если slim падает с «no browser found»,    │
│  загрузите embedded версию.                  │
└──────────────────────────────────────────────┘
```

Wire-up аналогично Tools tab → Zapret card:
- VM property `IsWgturnInstalled` (poll on tab open + after download finishes).
- `DownloadWgturnCommand` (slim by default), `DownloadWgturnEmbeddedCommand`,
  `RemoveWgturnCommand`, `UpdateWgturnCommand`.
- Bottom hint: «Используется только если основной VPN-канал заблокирован».

## 7 · Build.ps1 cleanup

Удалить блок `[6/9] Bundling wgturn-cli.exe` целиком:

```diff
 [6/9] Bundling sing-box.exe...
        Bundled upstream sing-box v1.13.10 (42.7 MB)
-       Bundling wgturn-cli.exe...
-       Cloning PavelLizunov/wgturn-core into tools\wgturn-cli-cache\...
-       wgturn-cli.exe SKIPPED (set WGTURN_CORE_DIR or ...)
 [7/9] Building install ZIP...
```

И вообще удалить ссылки на `bin\wgturn-cli.exe` из step [8/9] file list,
плюс из `app/bin/` copy шага.

Side-effect: Auto-Update Integration Test перестанет failить на этом
ходу. **Это исправит pre-existing CI issue, который unrelated к r10
code.** Этого можно сделать как самостоятельный chip перед / после
stable cut'а r10.

## 8 · Implementation steps (chip-able)

Можно разбить на 3 chip'а или сделать одним. Estimates:

| Chip | Что | Effort |
|---|---|---|
| **W-1** | `WgturnUpdater.cs` + `WgturnVariant` enum + unit tests | 3-4 ч |
| **W-2** | `AppPaths` move + `SettingsMigrator` v3→v4 + path-migration test | 2 ч |
| **W-3** | `build.ps1` cleanup (drop bundle step) + Auto-Update CI test re-run | 1 ч |
| **W-4** (опционально) | UI section (Settings) + VM commands + localization | 4-5 ч |

W-1 + W-2 + W-3 безопасно делать параллельно с любыми другими работами
(touch разные файлы: Core/Services/, Core/AppPaths.cs, build.ps1). W-4 —
после W-1 (зависит от Updater public API).

## 9 · Testing

### Unit
- `WgturnUpdaterTests`:
  - `ResolvesCorrectAssetForWindowsAmd64Slim()`
  - `ResolvesCorrectAssetForMacArm64Embedded()`
  - `ResolvesCorrectAssetForLinuxArm64FallsBackToSlim()`
  - `ResolvesUnsupportedPlatformThrows()`
  - `DownloadLockPreventsConcurrent()`
  - Mock HttpClient через `IHttpMessageHandler` — фейковые JSON release
    responses + бинарь.

### Integration (manual / CI-skipped)
- `DownloadActuallyFetchesFromGitHubReleaseV010` — `[Trait("Manual",
  "true")]`, не запускается в CI, ставит реальный wgturn-cli v0.1.0.

### Live MCP
После реализации UI:
1. Открыть Settings → Emergency Channel section
2. Нажать «Установить»
3. Verify progress bar + version=v0.1.0 + status «✓ Установлен» в UI
4. Verify файл `{DataDir}/wgturn/bin/wgturn-cli[.exe]` + `version.txt`
5. (опционально) Verify exec permission на Unix.

## 10 · Open questions / TODO

1. **SHA256 sidecars** — upstream `wgturn-core` сейчас публикует binaries без
   .sha256 файлов. Хорошо бы добавить — для integrity check. **Ask owner**
   (он же я, PavelLizunov 😀) дополнить `build-wgturn-cli.yml` чтобы
   эмитить `.sha256` рядом с каждым asset'ом.
2. **Mode выбора variant** — нужен ли у пользователя выбор slim/embedded
   при первой установке, или умолчательно slim + автоматический fallback
   на embedded при `no browser found` ошибке? Я склоняюсь к
   автоматическому — UX проще, +120 MB только если реально нужно.
3. **Linux arm32 / FreeBSD** — upstream не публикует. Reject gracefully
   с понятной ошибкой («wgturn-cli не доступен для вашей платформы»).
4. **Auto-update** — отдельно от main VPN auto-update механизма? Или
   единый периодический check? Я склоняюсь к unified: `UpdateChecker`
   уже polling каждые N часов, мог бы заодно chequer wgturn-core релизы
   и эмитить toast «доступна новая версия wgturn-cli».
5. **Audit упомянуть в README + плане Emergency Channel UI** что binary
   тянется с GitHub (legal/transparency).

## 11 · Связь с другими планами

- `plans/r10-stas-confirmed-and-apps-2mode.md` — НЕ зависит от этого
  плана. r10 stable cut может идти независимо.
- `plans/vpnrouter-wgturn-cli-phase1.md` (memory entry) — **этот план
  заменяет Phase 1 bundling подход целиком**. Phase 1 chip-batch
  (`696486f`, `655fb6b`) остаётся в коде (Core skeleton) — меняется
  только delivery механизм.
- Emergency Channel UI plan — будет следующим (W-4 выше).

## Severity

P2 / infrastructure cleanup. Не блокирует r10 cut. Решает:
- Cross-platform unblock (Mac/Linux/Android получают wgturn-cli).
- Убирает CI failure (`Auto-Update Integration Test`, `Build Android APK`).
- Уменьшает installer ZIP'ы на ~10 MB.

## Recommendation

**Spawn W-1 + W-2 + W-3 параллельно chip'ами** после r10 cut'а (когда
stable v2.32.1 опубликован). UI part (W-4) — отдельный chip позже,
когда дизайн Emergency Channel секции готов.
