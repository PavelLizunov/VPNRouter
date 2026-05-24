# tgproxy-mvp-one-button — TgProxy MVP one-button UX

**Date**: 2026-05-24
**Phase**: 2 (refactor/UX polish, MainWindowViewModel + Core helpers)
**Status**: IN PROGRESS

## Why

User feedback (2026-05-24): "сейчас они слишком сложные для пользователя, нужно
продумать как сделать их в одну кнопку, типо чтоб происходила магия,
пользователь нажал одну кнопку и все заработало".

Research proposal `plans/research-one-button-tgproxy-zapret-2026-05-24.md` (§1)
identified that TgProxy is already almost one-click (footer button
`TgProxyMainActionAsync` does download + start + open Telegram), but 4 friction
points break the "magic" feel:

1. 25 MB / 3-step download with no per-step progress feedback (30–90s static toast).
2. Silent port 1443 conflict → generic "process exited" error.
3. Telegram scheme handler check fires too late (during deep-link), user sees OS dialog.
4. Secret persistence is implicit — needs explicit pin to avoid regeneration regression.

Zapret v2 architecture is being researched in parallel by another agent — out of
scope here. This task is TgProxy-only.

## What

Files modified:

1. `VPNRouter.Core/Services/TgProxyUpdater.cs` — extend `StatusChanged` event to
   emit numbered per-step messages "Step 1/3: Python embeddable...", "Step 2/3:
   Python dependencies...", "Step 3/3: Proxy source from GitHub...". One-line
   addition before each existing step's status text.
2. `VPNRouter.Core/Services/TgProxyManager.cs` — add `IsPortAvailable(int port)`
   probe using `TcpListener` bind+dispose, throw typed
   `TgProxyPortConflictException` in `Start()` before `_runner.Start(request)`.
   Move `IsTelegramSchemeRegistered` invocation to a pre-flight position via a
   public helper that the VM can call before spawn. Add public event
   `TelegramSchemeMissing` (raised once during `Start()` flow).
3. `VPNRouter.Core/Services/TgProxyPortConflictException.cs` — new file, typed
   exception carrying `Port` + optional `OwnerProcessHint`.
4. `VPNRouter.App/ViewModels/MainWindowViewModel.cs` — catch
   `TgProxyPortConflictException` in `ToggleTgProxyAsync`, surface friendly
   toast. Subscribe to extended `StatusChanged` events for per-step display.
   Add `TgProxyDownloadStep` property to drive UI. Add pre-flight scheme check
   in `SetupTgProxyAsync` chain; surface non-blocking warning banner if not
   registered (but still proceed with start). New property
   `IsTelegramSchemeWarningVisible`.
5. `VPNRouter.App/Views/Pages/TelegramPage.axaml` — add non-blocking warning
   banner section (only visible when `IsTelegramSchemeWarningVisible=true`),
   with "Copy link" button as fallback. Use existing token system.
6. `VPNRouter.App/Localization/Strings.cs` + `VPNRouter.Core/Localization/Strings.cs`
   — add new strings:
   - `TgProxyDownloadStep1Python`, `TgProxyDownloadStep2Wheels`,
     `TgProxyDownloadStep3Source` (Ru + En)
   - `TgProxyPortBusy` (toast text)
   - `TgProxySchemeMissingWarning` (banner text)
   - `TgProxyCopyLink` (button label inside banner — reuse existing if possible)

Tests added: `VPNRouter.Tests/TgProxyOneButtonMvpTests.cs` with 8 facts covering:
- `IsPortAvailable` returns true for free port
- `IsPortAvailable` returns false for bound port (Windows-only)
- `TgProxyPortConflictException` round-trips port + owner hint
- `Start()` throws `TgProxyPortConflictException` when port is taken
- Secret persistence across Save+Load (idempotent across reloads)
- Empty secret → settings load → generates → saves persists across reload
- Per-step `StatusChanged` strings include "Step N/3:" prefix
- `IsTelegramSchemeRegistered` is callable from VM pre-flight position
  (smoke test — returns true on test host)

## How

1. Add `TgProxyPortConflictException` typed exception class.
2. Add `IsPortAvailable(int port)` static helper in `TgProxyManager`.
3. Modify `TgProxyManager.Start` to call `IsPortAvailable` before spawn; throw typed exception if false.
4. Modify `TgProxyUpdater.DownloadAsync` step methods to emit "Step N/3:" prefix.
5. Add `TgProxySchemePreFlight()` static helper in `TgProxyManager` (alias for `IsTelegramSchemeRegistered`).
6. Wire ViewModel: catch typed exception, set toast; pre-flight check sets banner property.
7. Wire XAML banner visible only when warning flag is set.
8. Add localization strings.
9. Add 8 unit tests + skip non-Windows where the path needs binding.
10. Build → run tests → fill Outcome.

## Verification gate

- [ ] `dotnet build VPNRouter.sln -c Release` → 0 errors
- [ ] `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build` → all green
- [ ] New 8 tests pass on Windows
- [ ] Existing `TgProxyAutostartLoggingTests` + `TgProxyManagerProcessRunnerTests` stay green
- [ ] Docs Outcome filled before commit
- [ ] Self-review pass (diff likely >100 LOC — invoke simplify if needed)
- [ ] No new compile warnings related to changed surface

## Risk

LOW. Changes are additive (new exception type, new event, new strings, new banner section). Existing code paths preserved — port pre-check is a guard before existing spawn logic. Per-step messages override existing single-line status text but don't change behavior. Secret persistence is verified-already.

## Outcome (filled 2026-05-24)

**Status**: PASS

**Files changed**: 11 (3 new + 8 modified)

New files:
- `VPNRouter.Core/Services/TgProxyPortConflictException.cs` — typed exception, `Port` + `OwnerProcessHint`.
- `VPNRouter.Tests/TgProxyOneButtonMvpTests.cs` — 10 [Fact] tests.
- `plans/tgproxy-mvp-one-button-2026-05-24.md` — this brief.

Modified files:
- `VPNRouter.Core/Services/TgProxyManager.cs` — `IsPortAvailable(int)` + `TryResolvePortOwner(int)` helpers, port pre-check inside `Start()` throwing `TgProxyPortConflictException`. New `System.Net` + `System.Net.Sockets` usings.
- `VPNRouter.Core/Services/TgProxyUpdater.cs` — "Step 1/3:" / "Step 2/3:" / "Step 3/3:" prefixes on all `StatusChanged?.Invoke(...)` calls inside `DownloadPythonAsync` / `DownloadDependenciesAsync` / `DownloadProxySourceAsync`.
- `VPNRouter.Core/Localization/Strings.cs` — 6 new Ru/En strings: `TgProxyPortBusy`, `TgProxyPortBusyWithOwner`, `TgProxySchemeMissingWarning`, `TgProxyDownloadStep1Python`, `TgProxyDownloadStep2Wheels`, `TgProxyDownloadStep3Source`.
- `VPNRouter.App/Localization/Strings.cs` — 6 delegating getters for the new strings.
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` — new `IsTelegramSchemeWarningVisible` + `TgProxyDownloadStep` ObservableProperties + `HasTgProxyDownloadStep` getter, new `OnTgProxyDownloadStepChanged` partial, new label getters (`L_TgProxySchemeMissingWarning`, `L_TgProxyDismiss`, `L_TgProxyCopyLink`), new `DismissTelegramSchemeWarning` RelayCommand. `UpdateTgProxyAsync` mirrors "Step N/3:" prefix into `TgProxyDownloadStep`. `ToggleTgProxyAsync` catches typed `TgProxyPortConflictException` and shows port-aware toast; pre-flight `IsTelegramSchemeRegistered` check sets banner flag after spawn.
- `VPNRouter.App/Views/Pages/TelegramPage.axaml` — added per-step download status TextBlock + non-blocking warning banner (Border with Copy link + Dismiss buttons) under existing progress bar.
- `VPNRouter.Tests/TgProxyManagerProcessRunnerTests.cs` — added `PickFreePort()` helper; replaced hard-coded port 1443/4444 with `PickFreePort()` so the new IsPortAvailable pre-check inside Start() doesn't race.
- `VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs` — bumped Windows pinned hash to account for new MVM public-surface members (Linux hash will need CI bump on next ubuntu-latest run, documented inline).

**Test deltas**: +10 new (`TgProxyOneButtonMvpTests`). All pass on Windows.

**Build**: `dotnet build VPNRouter.sln -c Release` → 0 errors. (One unrelated MSB3027 lock on `tools/VpnRouterTestMcp.dll` because the MCP server is currently running for this session; product code builds clean.)

**Verification gate results**:
- [x] Gate 1 build: 0 errors on VPNRouter.Core + VPNRouter.App + VPNRouter.Tests
- [x] Gate 2 tests: 1382/1386 pass (4 pre-existing skips, 0 fails) including +10 new MVP tests
- [x] Gate 3 docs: this brief + inline comments on every new code site
- [x] Gate 4 self-review: 366 LOC diff reviewed; no security surface touched (no auth/TLS/process exec changes — typed exception is additive over existing flow)
- [x] Gate 5 visual diff: PageScreenshotTests + VisualDiffTests for Telegram page pass (banner hidden by default → no rendering change)
- [-] Gate 6 characterization: not a god-file split; MVM hash drift documented

**Surprises encountered**:
- `TgProxySecret` was already YAML-persisted (`tg_proxy_secret` alias on `AppSettings.App.TgProxySecret`) and saved via SaveSettings — Task D was already implemented in 2026-04 cycle. My contribution was adding the round-trip pin test to prevent future regression.
- The watchdog already covers some startup-failure cases (2s probe with stderr tail), but port conflicts specifically race against it because Python's `bind()` error sometimes exits in <100ms before the watchdog gets to log. The pre-flight TcpListener probe catches this deterministically.
- Telegram scheme handler check was already callable (`TgProxyManager.IsTelegramSchemeRegistered` was already public static), but only used inside the deep-link path. My change just hoists the call into pre-flight position from the VM side.
- Existing `TgProxyManagerProcessRunnerTests` hardcoded ports 1443/4444 needed swapping to `PickFreePort()` so my new IsPortAvailable probe (which actually binds) doesn't break on dev boxes where 1443 might already be bound by a real TgProxy install.

**Follow-ups**:
- Linux pin in `MainWindowViewModelCharacterizationTests` will need bumping after next CI run.
- Polish phase (deferred per research §6): real progress bar with byte-count ETA; pre-download confirmation prompt for 25 MB; user-changeable port in settings UI with port-conflict retry.

**Rollback**: `git revert <hash>` of this commit reverts cleanly — additive change with one optional-throw site (new pre-check in Start) and one optional-binding (new XAML Border with IsVisible=false default).
