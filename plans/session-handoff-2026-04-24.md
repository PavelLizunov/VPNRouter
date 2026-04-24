# Session Handoff — 2026-04-24

Саммари всей сегодняшней сессии для следующего compact'ованного сеанса.
**Если ты читаешь это — ты продолжаешь работу, не начинаешь с нуля. Не переобъявляй инфраструктуру, она уже задеплоена.**

---

## 🔴 GOLDEN RULE (must carry across sessions)

**Я делаю ВСЁ сам. User не выполняет ничего вручную.** Дублируется в MEMORY.md.

Возможности:
- `gh` CLI для GitHub (Actions / releases / secrets / dispatch / API)
- `git push` на оба remote — `github` (https) + `origin` (ssh Forgejo через AmneziaWG VPN `10.9.1.1`)
- **SSH на Mac build host**: `slovn@192.168.0.246` (через host AmneziaWG route, `id_ed25519` — same key as Forgejo)
- Elevated Windows cmd через UAC-trigger (`Start-Process -Verb RunAs`) — требует user клика Yes в popup'е. Единственный case когда user что-то делает — UAC "Да/Нет" кнопка.
- PowerShell / Bash для Windows builds
- Локальный VM test (install.ps1 реально установился здесь)

**Исключения из GOLDEN RULE** — только physical interaction:
- UAC подтверждение (user видит окно на экране)
- DNS registrar UI (у меня нет доступа к панели регистратора)
- Hardware-specific test (например real VPN connect требует valid VLESS сервер)

---

## 🏷 Состояние releases на GitHub (актуально на 2026-04-24 ~21:30 UTC)

| Tag | Status | Описание |
|---|---|---|
| **v2.27.2** | **Latest** (stable) | sing-box 1.13.10 upstream + TUN auto-detect + passive diagnostics |
| **v2.28.0-r1** | Pre-release (rolling) | Linux passwordless (POSIX caps) + install.sh one-liner |
| v2.27.0 | stable, historical | Service UX redesign |
| v2.26.1-r1 | prerelease, kept as milestone | TunLock coordination (oldest pinned) |

**Rolling policy**: один prerelease в flight. Stable cut когда user подтвердит работу r1.
Retention cap: ~30 releases.

---

## 🌐 Live инфраструктура (НЕ трогать руками, auto-deployed)

### `vpn.ninitux.com` — brand domain
- CNAME → `pavellizunov.github.io` (в DNSOwl, TTL 3600)
- GitHub Pages custom domain, Let's Encrypt cert auto-renew (exp 2026-07-23)
- `https_enforced: true`

### Endpoints
```
https://vpn.ninitux.com/                  Landing (HTML, 3 platform one-liners)
https://vpn.ninitux.com/install.sh        Linux installer (apt repo setup)
https://vpn.ninitux.com/install.ps1       Windows installer (UAC + download + extract + ARP)
https://vpn.ninitux.com/uninstall.ps1     Windows uninstaller (wired from ARP UninstallString)
https://vpn.ninitux.com/apt/              Signed apt repo (reprepro, GPG)
```

### Homebrew Cask tap
- Repo: `github.com/PavelLizunov/homebrew-vpnrouter`
- Cask: `Casks/vpnrouter.rb` — v2.27.2, sha256 `d078eb94...`
- **postflight strips quarantine + provenance** xattrs (чтобы Gatekeeper не гавкал на unsigned DMG)
- User install: `brew install --cask pavellizunov/vpnrouter/vpnrouter`
- Auto-update: `update-cask.yml` workflow в tap-repo + daily cron safety net
- Cross-repo dispatch: `build-mac.yml` → PAT `HOMEBREW_TAP_DISPATCH_TOKEN` → tap update

### winget manifests
- `packaging/winget/manifests/p/PavelLizunov/VPNRouter/2.27.2/` — готовы к submission
- InstallerType `zip` + NestedInstallerType `portable`, alias `vpnrouter`
- **НЕ submitted** ещё в microsoft/winget-pkgs. Это отдельный task (PR + 1-3 дня review).
- Auto-submit via wingetcreate — TODO, см. `packaging/winget/README.md`

### APT repo
- `https://vpn.ninitux.com/apt/` (same gh-pages branch)
- GPG signed, re-indexed каждый stable release через `publish-apt.yml`
- Prerelease'ы НЕ попадают в apt, но publish-apt всё равно run'ается (install.sh + landing обновляются, apt re-indexing пропускает current .deb)

---

## 🔑 Secrets (хранятся в GitHub репе, в handoff НЕ дублировать)

В `PavelLizunov/VPNRouter` → Settings → Secrets → Actions:
- `HOMEBREW_TAP_DISPATCH_TOKEN` — fine-grained PAT, `contents:write` on `homebrew-vpnrouter`. Нужен для cross-repo dispatch. User установил его 2026-04-24. **Он валидный, tested live. Но user пастил токен в чат — значит в VM image / session logs может остаться. Не критично сейчас, но если будут weird issues — напомни user'у rotate через GitHub settings.**
- `APT_SIGNING_KEY` + `APT_SIGNING_KEY_ID` — для apt repo GPG-signing (давно настроены, работают)

---

## 🧪 VM dev state

- Windows 11 x64, VirtualBox
- Путь: `C:\Project\VPNRouter\.claude\worktrees\affectionate-varahamihira-9375b5`
- Branch: `claude/affectionate-varahamihira-9375b5`, tracks `github/main`
- SSH на Mac: работает, зарегистрирован в known_hosts
- **VPNRouter УСТАНОВЛЕН на VM** через install.ps1 (E2E test сегодня в 21:29):
  - `C:\Program Files\VPNRouter\` — full install
  - Start Menu shortcut
  - HKLM Uninstall entry (работает)
  - sing-box внутри = 1.13.10 upstream
- `%ProgramData%\VPNRouter\config.yaml` — сброшен к defaults (пустой)
- `tools/singbox-cache/` — 1.13.10 zip cached для build.ps1

---

## 📝 User-reported bugs — waiting fix (v2.28.1+)

Полный план: `plans/vpnrouter-v2.28-ux-bugfix.md`. Краткая сводка:

### Bug 1 (P0) — subscription add → UI не обновляется
- **Root cause**: `MainWindowViewModel.cs:1917` `AddSubscriptionAsync` — `RebuildSubscriptionPool()` внутри try-блока (не finally), нет auto-switch на Subscribe tab
- **Fix**: 3 строки + fail-safe в finally + auto-switch to tab

### Bug 2 (P1) — Zapret download unreliable
- **Root cause**: `ZapretUpdater.cs` — нет retry, stale partial ZIPs в %TEMP%, race window на двойной клик, cryptic error messages
- **Fix**: exponential-backoff retry (3 attempts) + pre-flight cleanup + SemaphoreSlim + categorized errors

### Bug 3 (P1 UX) — Free Configs page неюзабельная
- **Root cause**: 6 секций + 6-chip dashboard + нет early-stop на Refresh + Deep Verify Stop-then-Start регрессит (не resume'ит)
- **Fix**: Phase A (two-tier UI Simple/Advanced), Phase B (early-stop after N working), Phase C (persistent deep-verify checkpoint)

---

## 🗓 Roadmap (waiting user decision)

User должен выбрать в следующей сессии:

### Option A (мой совет) — staged releases
- **v2.28.1-r1** — Bug 1 + Bug 2 (1 день work). Ship fast, критические smell'ы.
- **v2.28.2-r1** — Free Configs Phase A+B (два-три дня). UX refactor after Phase 1 feedback.
- **v2.28.3-r1** — Free Configs Phase C + regression suite (1-2 дня).

### Option B — big bang
Всё сразу в v2.28.1. Риск: 3-5 дней работы в одном PR, сложно тестировать.

### Option C — discussion first на Free Configs UX
User хочет поговорить про «что оставить на виду» прежде чем я начну двигать XAML.

**Если user говорит «делаем Option A»** — я иду:
1. Re-read `plans/vpnrouter-v2.28-ux-bugfix.md` (он там полный)
2. Начинаю с Bug 1 в `VPNRouter.App/ViewModels/MainWindowViewModel.cs:1917`
3. Затем Bug 2 в `VPNRouter.Core/Services/ZapretUpdater.cs`
4. Bump AppVersion → 2.28.1-r1, commit, tag, build Windows + trigger CI, release prerelease

---

## 📦 Сегодняшние коммиты (важные) — don't lose

```
# Main repo (PavelLizunov/VPNRouter):
e041ec1  fix(install.ps1): Invoke-WebRequest .Content is Byte[] on PS5.1
4f6ce4b  feat(winget): manifests + docs for Microsoft winget-pkgs submission
fe6902c  feat(install): Windows install.ps1 + uninstall.ps1 (symmetric one-liner UX)
6f2f11b  fix(ci): pin GH_TOKEN in 'Trigger Homebrew Cask update' step
91ced56  feat(brew): Homebrew Cask tap + auto-update wiring for macOS install
b174ab3  ci(publish-apt): bake CNAME + vpn.ninitux.com canonical URLs
90a7507  ci(publish-apt): on prerelease, re-index all stable .debs
4665e07  ci(publish-apt): let install.sh + landing page publish on prereleases too
353a21e  v2.28.0-r1: Linux passwordless VPN via POSIX capabilities + install.sh
5970242  docs: README v2.27.2 core-stability bullet
c795f18  v2.27.2 stable: promote rolling candidate
ac4cae3  docs(audit-plan): document HeadlessGuiTests multi-test hang env flake
14ba19a  tools: add live-test-r1.ps1 regression harness
7339d5c  audit: close B1 dangling-adapter hypothesis (live-verified no leak)
d7fe133  cli(start): allow --dry-run without admin rights
36744af  tests: pin ConfigGenerator duplicate-name + gitignore hygiene

# homebrew-vpnrouter tap:
15c8e80  Auto-strip quarantine + provenance xattrs via postflight
dcf8226  fix: refresh v2.27.2 DMG sha256 after re-upload
fffb637  Initial tap: Casks/vpnrouter.rb v2.27.2 + auto-update workflow

# gh-pages branch (VPNRouter):
30aa15f  Add CNAME for vpn.ninitux.com custom domain
+ публикации install.sh / install.ps1 / uninstall.ps1 / landing
```

**16+ коммитов за день.** Все pushed на обе remote.

---

## 🧷 Что НЕ надо делать при compact restore

1. **Не пере-создавать tap** (`PavelLizunov/homebrew-vpnrouter` уже есть). Просто `git clone` если нужно модифицировать.
2. **Не пере-настраивать DNS** (`vpn.ninitux.com` CNAME уже работает, cert выпущен).
3. **Не пере-генерировать GPG keys** для apt (существуют, в secrets).
4. **Не просить user'а снова создавать `HOMEBREW_TAP_DISPATCH_TOKEN`** (он уже в secrets, tested live).
5. **Не делать UAC trigger без необходимости** — user Mac-focused, UAC popup может пропустить. Если НУЖНО elevated install test — сразу **скажи user'у** словами `UAC окно появится через 1 сек — кликни Yes` + объясни что именно делаешь.

---

## 🔎 Если next session пилит v2.28.1 по Option A

### Bug 1 fix — pseudo-patch

```csharp
// VPNRouter.App/ViewModels/MainWindowViewModel.cs ~line 1917
public async Task AddSubscriptionAsync()
{
    // ... existing validation + entry creation ...

    _settings.App.Subscriptions.Add(entry);
    Subscriptions.Add(svm);

    // NEW: auto-switch to Subscribe tab so user sees the new data landing
    if (!IsSubscribeMode) {
        IsSubscribeMode = true;
        IsVlessMode = false;
        // find the Subscribe tab index (currently hardcoded 1 in codebase?)
        SelectedTabIndex = SubscribeTabIndex;
    }

    try {
        await RefreshSubscriptionAsync(svm);
    } finally {
        // NEW: always rebuild so UI reflects whatever fetched (even if partial)
        RebuildSubscriptionPool();
        SaveSettings();  // might already be in RefreshSubscriptionAsync — check
    }
}
```

+ regression test в `VPNRouter.Tests/ViewModelTests.cs`:
```csharp
[AvaloniaFact]
public async Task AddSubscription_SwitchesTabAndPopulatesUI()
{
    var vm = new MainWindowViewModel();
    vm.SmpInput = "https://mock-subscription-url";
    // Need to mock SubscriptionFetcher or use a local test server... TBD
    await vm.AddSubscriptionAsync();
    Assert.True(vm.IsSubscribeMode);
    Assert.Equal(SubscribeTabIndex, vm.SelectedTabIndex);
}
```

### Bug 2 fix — pseudo-patch

```csharp
// VPNRouter.Core/Services/ZapretUpdater.cs
private static readonly SemaphoreSlim _downloadLock = new(1, 1);

public async Task<DownloadResult> DownloadAndExtractAsync(...)
{
    if (!await _downloadLock.WaitAsync(TimeSpan.Zero))
        throw new InvalidOperationException("DownloadInProgress");

    try {
        CleanStaleTempZips();  // delete tmp*.zip older than 1h in %TEMP%
        return await RetryAsync(() => DownloadAndExtractOnceAsync(...), attempts: 3);
    }
    finally { _downloadLock.Release(); }
}

private async Task<T> RetryAsync<T>(Func<Task<T>> op, int attempts = 3)
{
    for (int i = 0; i < attempts; i++) {
        try { return await op(); }
        catch (Exception ex) when (i < attempts - 1 && IsTransient(ex)) {
            // Categorized message + retry
            _logger?.Information("[ZapretUpdater] Transient error attempt {I}/{N}: {Msg} — retrying in {Ms}ms",
                i+1, attempts, ex.Message, 2000 * (1 << i));
            await Task.Delay(2000 * (1 << i)); // 2s, 4s, 8s
        }
    }
    throw new InvalidOperationException("Retry exhausted");
}

private static bool IsTransient(Exception ex)
{
    if (ex is HttpRequestException hre) {
        // TODO: hre.StatusCode?.Value >= 500 || = 429 || = 403 (rate limit)
    }
    return ex is IOException or TaskCanceledException or HttpRequestException;
}
```

+ UI error categorization в `MainWindowViewModel.cs` (ZapretPage section).

---

## 🧭 Next-session opening move

1. `cat plans/session-handoff-2026-04-24.md` (этот файл)
2. `git log --oneline -5` — подтвердить что все коммиты на месте
3. `gh release list --repo PavelLizunov/VPNRouter --limit 3` — состояние релизов
4. Спросить user'а: **«Какой Option из roadmap? A (staged, рекомендую) / B (big bang) / C (сначала обсудим Free Configs UX)?»**
5. Выполнить выбранный путь.

Если user скажет «Option A, погнали» — первая задача: Bug 1 fix в MainWindowViewModel.cs. Всё готово.

---

**End of handoff.** Я (в compact'ованной сессии) — просто читай. Не переоткрывай tap, не трогай DNS, не пере-создавай ничего из infrastructure. Всё работает.
