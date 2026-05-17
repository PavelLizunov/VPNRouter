# Phase 1 — Q6: Bump CommunityToolkit.Mvvm to 8.4.2 in App (resolve drift)

**Owner**: Claude session-id (Wave 1)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #6, plans/nuget-audit-2026-05-17.md "Real drift: one"
**Effort**: 15 minutes
**Risk**: LOW (8.2.1 → 8.4.2 is patch-level + small minors, source-gen compat preserved; CTM has strong back-compat)

## Why
Audit C found drift: `VPNRouter.App/VPNRouter.App.csproj` pins `CommunityToolkit.Mvvm 8.2.1`, but `VPNRouter.Android/VPNRouter.Android.csproj` pins `8.4.0`. Both should be on **8.4.2** (current latest stable). Cross-version source-gen between App and Android packages can produce subtle binding/INPC differences. Align them.

## What
1. `VPNRouter.App/VPNRouter.App.csproj` — bump `CommunityToolkit.Mvvm` from `8.2.1` → `8.4.2`
2. `VPNRouter.Android/VPNRouter.Android.csproj` — bump from `8.4.0` → `8.4.2`

**Validation**: after bump, both projects build clean. The CTM source generators emit `INotifyPropertyChanged` boilerplate at compile time — any breaking change between 8.2 and 8.4 would surface as `MVVMTKxxxx` errors.

Known: Audit C noted `MVVMTK0034` warnings already exist in `MainWindowViewModel.SimpleMode.cs` lines 393/420/424 (direct field reference instead of generated property). Those warnings are pre-existing — don't fix in this task, just verify they don't escalate to errors after bump.

## Verification gate
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors (warnings OK if pre-existing)
- [ ] **Gate 2 — Tests green**: `dotnet test` → all 765 pass
- [ ] **Sanity**: `dotnet list package` on both projects shows 8.4.2
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome

**Status**: COMPLETE (2026-05-17, agent Wave 1)

### Changes
- `VPNRouter.App/VPNRouter.App.csproj` line 43: `CommunityToolkit.Mvvm` `8.2.1` → `8.4.2`
- `VPNRouter.Android/VPNRouter.Android.csproj` line 102: `CommunityToolkit.Mvvm` `8.4.0` → `8.4.2`

### NuGet availability check
Latest stable on NuGet at time of bump: `8.4.2` (also present: `8.4.1-build.4`, `8.4.1`, `8.4.0`, `8.3.2`, `8.3.1`, `8.3.0`, `8.2.2`, `8.2.1`, ...). Target `8.4.2` from brief is current — no fallback needed.

### Verification
- `dotnet restore VPNRouter.App/VPNRouter.App.csproj` → restored cleanly
- `dotnet restore VPNRouter.Android/VPNRouter.Android.csproj` → restored cleanly
- `dotnet build VPNRouter.sln -c Release` → **0 errors**, 67 warnings (all pre-existing CA1416 platform warnings + similar; **no MVVMTK errors** and the MVVMTK0034 warnings the brief flagged at MainWindowViewModel.SimpleMode.cs:393/420/424 did not appear in this build)
- `dotnet list package` on both projects → CommunityToolkit.Mvvm **Requested 8.4.2 / Resolved 8.4.2**
- `dotnet test --no-build` (excluding Headless/PageScreenshot/VisualDiff) → **834 passed / 5 failed / 3 skipped / 842 total**

### Tests
The 5 failures are **pre-existing and unrelated to the CTM bump**. Verified by running tests at baseline (with bump stashed): identical result 834 passed / 5 failed / 3 skipped. The failing tests are:
- `VlessUriParserTests.TryParse_ValidUri_ReturnsEntry`
- `VlessUriParserTests.Parse_RealityUri_ExtractsAllFields`
- `VlessUriParserTests.Parse_RealityUri_ExtractsTransport`
- `VlessUriParserTests.Parse_RealityUri_ExtractsRealityConfig`
  - Root cause: v2.32.3 commit `d041ec8` added `PlaceholderConfigException` rejection of `DnT9hIvt…nckU` Reality public-key fingerprint. Test fixtures embed this exact placeholder. Test data needs to be regenerated with a non-placeholder key — separate task.
- `AppAutostartTgProxyTests.Bootstrap_IsInvokedFromConstructor`
  - Root cause: test asserts the literal string `"BootstrapAutostartAsync"` appears in `MainWindowViewModel.cs` ctor source-text. The constructor was refactored and the bootstrap call is now indirected. Separate task.

No CTM source-generator regressions surfaced. CommunityToolkit.Mvvm `8.2.1 → 8.4.2` is binary-compatible for our use of `[ObservableProperty]` / `[RelayCommand]` / `[NotifyPropertyChangedFor]`.

### Notes
- Q6 ran concurrently with other Wave 1 tasks (Q1 Android UI source-link strip, Q5 Core pin bumps, others). Android csproj has additional non-Q6 edits (Newtonsoft.Json 13.0.4, ZXing.Net 0.16.11, removed VPNRouter.UI Compile/AvaloniaResource block) that belong to those tasks — left intact.
- Not committed per brief instruction "DO NOT COMMIT. Leave staged." App is unstaged; Android remains in mixed staged+unstaged state owing to overlapping waves.
