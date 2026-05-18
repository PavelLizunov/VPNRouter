# Phase 4 — Avalonia 12 modernization (compiled bindings + x:DataType)

**Owner**: Wave 14 single agent
**Roadmap ref**: Avalonia 12 release notes — `https://avaloniaui.net/blog/avalonia-12/` + `https://docs.avaloniaui.net/docs/data-binding/compiled-bindings`
**Effort**: 1 day
**Risk**: MEDIUM (turning runtime binding warnings into compile-time errors surfaces hidden binding bugs — must fix before commit)

## Why

We bumped to Avalonia 12.0.3 in Wave 12 (commit `034baba`), but we left compiled bindings to **implicit default** rather than **explicit opt-in**. Per the Avalonia 12 docs:

- Compiled bindings are now the default (1,867% FPS gain on complex layouts)
- BUT only fully effective when `x:CompileBindings="True"` + `x:DataType` are explicit
- Missing `x:DataType` silently falls back to reflection bindings
- `AvaloniaUseCompiledBindingsByDefault=true` MSBuild property enforces at project level

Audit of our 14 AXAML files:
- **0 files** have `x:CompileBindings="True"` explicit
- **13 of 14 files** have at least 1 `x:DataType` somewhere
- **App.axaml + Tokens.axaml** are style/resource-only (no DataContext bindings — exempt)
- **AboutWindow.axaml** has only 1 `x:DataType` despite multiple bindings — possible silent reflection fallback

The win: turn EVERY binding into a compile-time-verified path. Surfaces hidden binding typos. Locks in the Avalonia 12 perf gain at the project level.

## What

3 sub-tasks:

### 4-AV-1: Project-level enforcement
- Add `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>` to `VPNRouter.App.csproj` `<PropertyGroup>`.

### 4-AV-2: Per-file `x:CompileBindings="True"` + `x:DataType`
- 12 bindable AXAML files (skip App.axaml + Tokens.axaml as style-only):
  - `VPNRouter.App/Views/AboutWindow.axaml` (currently 1 x:DataType — needs root + audit)
  - `VPNRouter.App/Views/MainWindow.axaml` (1 x:DataType — needs root + audit)
  - 10 `VPNRouter.App/Views/Pages/*.axaml` files (each has at least 1 x:DataType)
- Add to root element: `x:CompileBindings="True" x:DataType="vm:<ViewModelType>"`.
- For pages, the root x:DataType is usually `MainWindowViewModel` (the shared DataContext).
- For DataTemplates inside ItemsControls (we have 1 ListBox), add `x:DataType` to each `<DataTemplate>` element.

### 4-AV-3: Fix any compile-time binding errors revealed
- Build immediately after each XAML edit.
- If `Cannot resolve "Foo" on type "VPNRouter.App.ViewModels.MainWindowViewModel"` errors appear:
  - Verify the property exists on the ViewModel (it may be a typo).
  - Verify the property is at the correct partial-class location.
  - If genuinely missing, fix the binding OR add the missing property to the VM.
- DO NOT use `x:CompileBindings="False"` as a workaround — that defeats the purpose. Fix the binding.

## How

**Step 1**: Add the MSBuild property to `VPNRouter.App.csproj`.

**Step 2**: For each of the 12 bindable AXAML files:
1. Read the root element.
2. Find the existing `x:DataType` (most have one already deeper in the file). Identify the ViewModel type.
3. Add to root: `x:CompileBindings="True" x:DataType="vm:<Type>"` where `vm` is `xmlns:vm="clr-namespace:VPNRouter.App.ViewModels"`.
4. Build. Fix errors. Repeat until clean.

**Step 3**: For DataTemplates inside ItemsControls / ListBox:
1. Read MainWindow.axaml's ListBox section (only one we have, lines 735-755).
2. The ListBoxItems use direct `{Binding LblTabXxx}` — already cleanly typed since the parent ListBox inherits root x:DataType.
3. No DataTemplate inside the ListBox to worry about.

**Step 4**: AboutWindow.axaml deep audit:
1. Find all Binding paths.
2. Verify each resolves on AboutWindowViewModel (or whatever the type is).
3. If AboutWindow uses MainWindowViewModel.About sub-state, x:DataType should be the actual context type.

**Step 5**: Build the solution `dotnet build VPNRouter.sln -c Release`. Expected output: 0 errors, 0 binding warnings.

**Step 6**: Run tests:
- Scoped: `dotnet test ... --filter "FullyQualifiedName!~Headless&FullyQualifiedName!~PageScreenshot&FullyQualifiedName!~VisualDiff"`
- HeadlessGuiTests, PageScreenshotTests, VisualDiffTests — separately per VPNRouter.Tests/CLAUDE.md
- Characterization hashes should NOT drift (we're not changing public MVM surface)

**Step 7**: MCP verify — launch the binary, click through all 6 tabs, verify no rendering regression. Compiled bindings should render identically; only the perf characteristic improves (microbench could measure).

## Verification gate
- [ ] MSBuild property added
- [ ] 12 AXAML files have root `x:CompileBindings="True"` + correct `x:DataType`
- [ ] Build 0 errors, 0 binding warnings (binding warnings becoming errors is the WHOLE POINT — verify they're all genuine + fixed)
- [ ] Scoped suite green (1088 → 1088, no test count change since this is XAML-only)
- [ ] Headless + PageScreenshot + VisualDiff suites green
- [ ] Characterization hashes (MVM + AndroidApp) unchanged
- [ ] MCP verify: 6 tabs render identical to v2.34.0-r1

## Outcome

**Wave 14 — Status: PASS** (2026-05-18)

### Surprises (good ones)

1. **`AvaloniaUseCompiledBindingsByDefault=true` was already in
   `VPNRouter.App.csproj` line 7** (added by Wave 12 with Avalonia 12.0.3
   bump per the inline comment chain). Sub-task 4-AV-1 was therefore a
   no-op — the property is in the right `<PropertyGroup>` (the one with
   `<TargetFramework>net8.0</TargetFramework>`). No edit needed.
2. **All 12 bindable AXAML files already had a root `x:DataType="vm:MainWindowViewModel"`**
   even before this wave. Combined with the project-level property,
   compiled bindings were already active — so the build was passing
   with 0 binding errors / warnings as our baseline measurement.
3. **All inner `<DataTemplate>`s for ItemTemplates / ContentTemplates already
   had `x:DataType` or `DataType` attributes** except for two cases (see
   findings below). Avalonia accepts both forms; both compile to typed
   bindings.

### Changes staged (12 files, 15 insertions, 2 deletions)

| File | Change |
|---|---|
| `VPNRouter.App/Views/MainWindow.axaml` | +1 line: added `x:CompileBindings="True"` to root Window |
| `VPNRouter.App/Views/AboutWindow.axaml` | +1 line: added `x:CompileBindings="True"` to root Window |
| `VPNRouter.App/Views/Pages/ApplicationsPage.axaml` | +1 line: added `x:CompileBindings="True"` to root UserControl |
| `VPNRouter.App/Views/Pages/DpiBypassPage.axaml` | +1 root + +1 inner `x:DataType="x:String"` on the `ZapretActionOutput` (`ObservableCollection<string>`) ItemTemplate (was `<DataTemplate>` with no type) |
| `VPNRouter.App/Views/Pages/EmergencyChannelPage.axaml` | +1 root + xmlns:models alias + +1 inner `x:DataType="models:WgturnEntry"` on the `WgturnConfigs` ComboBox ItemTemplate (was `<DataTemplate>` with no type) |
| `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml` | +1 line: added `x:CompileBindings="True"` to root UserControl |
| `VPNRouter.App/Views/Pages/NetworkPage.axaml` | +1 line: added `x:CompileBindings="True"` to root UserControl |
| `VPNRouter.App/Views/Pages/ServersPage.axaml` | +1 line: added `x:CompileBindings="True"` to root UserControl |
| `VPNRouter.App/Views/Pages/SimplePage.axaml` | +1 line: added `x:CompileBindings="True"` to root UserControl |
| `VPNRouter.App/Views/Pages/SubscribePage.axaml` | +1 line: added `x:CompileBindings="True"` to root UserControl |
| `VPNRouter.App/Views/Pages/TelegramPage.axaml` | +1 line: added `x:CompileBindings="True"` to root UserControl |
| `VPNRouter.App/Views/Pages/ToolsPage.axaml` | +1 line: added `x:CompileBindings="True"` to root UserControl |

### Latent reflection-binding fallbacks discovered + fixed

Two DataTemplates were silently using reflection bindings (no
`x:DataType` / `DataType` attribute). With the project-level compiled
default they SHOULD have triggered the AVLN3001 warning ("data context
unknown — using reflection"), but Avalonia 12.0.3 is more lenient than
the docs suggest — it didn't emit a warning for these. Now explicitly
typed.

1. **`VPNRouter.App/Views/Pages/DpiBypassPage.axaml:176`** — ItemsControl
   `ItemsSource="{Binding ZapretActionOutput}"` → ItemTemplate was
   `<DataTemplate>` with `Text="{Binding}"`. Source is
   `ObservableCollection<string>`. Now `<DataTemplate x:DataType="x:String">`.

2. **`VPNRouter.App/Views/Pages/EmergencyChannelPage.axaml:153`** —
   ComboBox `ItemsSource="{Binding WgturnConfigs}"` → ItemTemplate was
   `<DataTemplate>` with `Text="{Binding Name}"`. Source is
   `ObservableCollection<WgturnEntry>` (defined in `VPNRouter.Core/Models/WgturnEntry.cs`).
   Added `xmlns:models="using:VPNRouter.Core.Models"` namespace alias
   + `<DataTemplate x:DataType="models:WgturnEntry">`. Verified
   `WgturnEntry.Name` is the same `public string Name` the binding
   already referenced.

### Compile-time binding errors discovered (the gold)

**Zero** — the project compiled cleanly the entire way through. This
means:
- Every existing binding path resolves correctly on its typed DataContext
- All 12 root-level `x:DataType` attributes were already correct
- All previously-typed inner DataTemplates were correctly typed
- The MVM partial-class properties referenced from XAML all exist

**Interpretation**: Wave 12's transition to Avalonia 12.0.3 + the
already-present `AvaloniaUseCompiledBindingsByDefault=true` had ALREADY
been catching binding typos at compile time. This wave's explicit
opt-in is now self-documenting per file and survives any future
removal of the MSBuild flag.

**AboutWindow deep-audit findings**: 8 bindings (`L_AboutTitle`,
`L_AboutBrandName`, `L_AboutTagline`, `L_AboutVersionLabel`,
`L_AboutSingBoxLabel`, `L_AboutCreatorLabel`, `L_AboutRepoLabel`,
`L_AboutCloseBtn`) all resolve to `MainWindowViewModel.Localization.cs`
lines 20–27. The 2 `x:Name`-referenced TextBlocks (`VersionTextBlock`,
`SingBoxTextBlock`) are code-behind populated — no Binding paths, so
exempt from this audit.

**MainWindow ListBox tab strip (lines 736–756)**: 6 `LblTab*`
properties (`LblTabManual`, `LblTabSubscribe`, `LblTabNetwork`,
`LblTabApps`, `LblTabTools`, `LblTabFreeConfigs`) all resolve to
`MainWindowViewModel.cs:2189–2232`. The ListBoxItems inherit the
parent ListBox's DataContext (`MainWindowViewModel`), so the bare-path
bindings resolve correctly.

### Test deltas

| Suite | Pre | Post | Delta |
|---|---|---|---|
| Scoped (non-headless) | 1088 / 4 skipped / 0 failed | 1088 / 4 skipped / 0 failed | 0 |
| HeadlessGuiTests | 8 / 0 failed | 8 / 0 failed | 0 |
| PageScreenshotTests | 19 / 0 failed | 19 / 0 failed | 0 |
| VisualDiffTests | 3 / 0 failed | 3 / 0 failed | 0 |
| CharacterizationTests | 2 / 0 failed (MVM + AndroidApp hashes pinned) | 2 / 0 failed (hashes byte-identical) | 0 |

### Verification gate

- [x] MSBuild property in csproj (was already present from Wave 12)
- [x] 12 bindable XAML files have explicit `x:CompileBindings="True"` + correct `x:DataType` on root
- [x] Build 0 errors, 0 binding warnings (full solution rebuild)
- [x] Characterization hashes (MVM + AndroidApp) byte-identical
- [x] Scoped suite green (1088/1088)
- [x] Headless + PageScreenshot + VisualDiff suites green (8 + 19 + 3)
- [x] MCP verify FLAGGED for integrator (worktree has no UI access; compiled bindings should be visually identical to reflection bindings, only perf characteristic improves)

### Follow-up notes (for integrator)

- The 19 `AVLN5001 'TextBox.Watermark' is obsolete` warnings are
  pre-existing (Avalonia 12 deprecated `Watermark` → `PlaceholderText`)
  and out of scope for this wave. Phase 4B candidate (cosmetic property
  rename across 19 sites).
- All `DataTemplate DataType=...` (without `x:` prefix) in
  `FreeConfigsPage.axaml` (2 sites, line 215 + 352) work identically
  to `x:DataType` in Avalonia. Consider normalising to `x:DataType` in
  a future cosmetic pass for consistency.
- Phase 4B GroupBox migration deferred per brief.

## Follow-up

- **GroupBox** (new Avalonia 12 control) — cosmetic, can replace `Border` wrapped sections in Settings pages. Defer to Phase 4B.
- **Focus Traversal API** — accessibility improvement. Audit keyboard nav after this lands; defer to Phase 4C if there are issues.
- **CompositionAnimation API** — Avalonia 12 has new compositor; potential to migrate the 2-3 animations we have. Profile first to see if it's worth.
- **Android Avalonia 12** — ph4-android-net10 (separate task).
