# VPNRouter logical context segments audit — 2026-08-03

Status: read-only inventory and context-fit analysis. No product source was
changed. This report extends, and does not replace,
`qwen-context-footprint-and-code-reduction-audit-2026-08-01.md`.

## 1. Verdict

- Largest logical production module: `VPNRouter.Android/AndroidApp` partial
  type plus its view, 20 files, 15,722 lines, 701,442 bytes, approximately
  **194,855 tokens**.
- Second: `VPNRouter.App/ViewModels/MainWindowViewModel`, 14 partial files,
  13,409 lines, 639,719 bytes, approximately **177,706 tokens**.
- Those two modules do not fit whole in a 128k context. Every logical source
  module fits in 256k, but four of five conservatively measured task closures
  do not. Every measured task closure fits comfortably in 1M.
- No new file split is justified solely by context size. Existing feature
  partials are useful. The 102,359-token `MainWindowViewModel.cs` anchor is the
  only individual source file that makes 128k review materially awkward, but
  the already-deferred Banners move would save less than 3%; do not revive it
  only for a context score.
- Use targeted retrieval: feature partial + declarations/callers from the
  anchor + directly relevant models/invariants/tests. Do not load a whole
  project or blanket-ignore the repository.

## 2. Method and scope

Snapshot: base commit `10ef4f70` (`perf(app): pause hidden connection stats
polling (#94)`) on branch `codex/mtu-context-research-2026-08-03`.

Measurement rules:

- files came from `git ls-files`; local user configuration and files outside
  the checkout were not inspected;
- product source is authored `.cs`, `.java`, `.kt`, `.axaml`, and `.xaml`
  under Core/App/CLI/Service/Android, excluding `bin`, `obj`, generated output,
  tests, docs, tooling, and binaries;
- physical lines are UTF-8 text lines; bytes are on-disk file bytes;
- approximate tokens are `ceil(bytes / 3.6)`, matching the 2026-08-01 audit;
- XAML and code-behind are grouped as one view module;
- known partial types are grouped as one logical module:
  `MainWindowViewModel`, `AndroidApp`, `SingBoxManager`, and Core `Strings`;
- the complete 261-module inventory is in Appendix A;
- task closures use conservative source/reference name matching. They are
  upper bounds, not compiler-exact minimal dependency graphs.

`.claude_handoff.md` is absent from this checkout. The audit did not search
outside the checkout because the task explicitly forbids it.

## 3. Independent Qwen worker

Qwen Code 0.21.3, exact model `qwen3.8-max-preview`, received the complete
logical-module inventory, measured closures, prior 2026-08-01 audit,
`refactor-backlog.md`, and `codebase-reduction-and-split-plan.md`. It was
independent of the MTU worker and had no tools or recording.

Exact invocation:

```text
<inventory + prior plans> |
  qwen -p "Analyze the complete inventory and prior plans on stdin. Produce the requested independent audit only; no tool calls." \
    -m qwen3.8-max-preview --safe-mode --approval-mode plan \
    --no-chat-recording --max-tool-calls 0 --max-wall-time 30m \
    --output-format text
```

Worker instruction, exact substantive constraints:

```text
Distinguish files, partial-type logical modules, subsystems and task closures.
Evaluate 128k, 256k and 1M with dependencies/invariants/tests and at least 15%
headroom. Identify only real bad splits, cyclic context needs, oversized
modules and duplication. Reject splitting that does not improve reviewability
or fit. Cross-check the 2026-08-01 audit and refactor backlog without
duplicating them. Do not propose generic layers, interfaces or service
extraction merely to make files smaller.
```

Codex independently reproduced totals and accepted only conclusions supported
by the inventory/current plans. Qwen's blanket statement that every large
class is cohesive is treated as a review judgment, not a fact; the actionable
decision remains measurement/YAGNI-based.

## 4. Repository footprint

### 4.1 Tracked text categories

| Category | Files | Lines | Bytes | Approx tokens |
|---|---:|---:|---:|---:|
| Docs/plans | 482 | 95,146 | 5,921,112 | 1,644,987 |
| Product text | 333 | 119,580 | 5,861,287 | 1,628,307 |
| Tests | 310 | 66,216 | 2,884,869 | 801,498 |
| Tooling | 53 | 8,003 | 380,238 | 105,650 |
| Other text | 26 | 4,212 | 171,076 | 47,532 |

The sum is approximately 4.23M tokens, consistent with the prior audit's
4.23M estimate. Small drift comes from the newer snapshot and category details.

### 4.2 Product projects

| Project | Files | Lines | Bytes | Approx tokens |
|---|---:|---:|---:|---:|
| VPNRouter.Core | 190 | 59,491 | 2,855,068 | 793,174 |
| VPNRouter.App | 73 | 30,579 | 1,600,272 | 444,557 |
| VPNRouter.Android | 47 | 26,627 | 1,287,296 | 357,604 |
| VPNRouter.CLI | 15 | 1,796 | 73,202 | 20,342 |
| VPNRouter.Service | 8 | 1,087 | 45,449 | 12,630 |

Project totals include authored project text beyond the source extensions in
Appendix A, so they should not be compared as exact sums of that appendix.

### 4.3 Logical module distribution

| Threshold | Module count |
|---|---:|
| All production source modules | 261 |
| >8k tokens | 41 |
| >16k | 21 |
| >32k | 5 |
| >64k | 3 |
| >128k | 2 |
| >256k | 0 |

## 5. Largest modules and existing splits

| Logical module | Files | Lines | Bytes | Approx tokens | Decision |
|---|---:|---:|---:|---:|---|
| AndroidApp partial + view | 20 | 15,722 | 701,442 | 194,855 | Existing feature partials are good; keep. |
| MainWindowViewModel partial | 14 | 13,409 | 639,719 | 177,706 | Existing feature partials are good; anchor remains a 128k pressure point. |
| Core Localization Strings partial | 7 | 3,497 | 235,697 | 65,475 | Data table, not a behavior closure; keep searchable. |
| NetworkPage view + code-behind | 2 | 2,490 | 149,850 | 41,626 | Large but connected page; no new UserControls for token cosmetics. |
| App Localization Strings | 1 | 1,452 | 119,751 | 33,265 | Data table; keep. |
| ConfigGenerator | 1 | 2,210 | 112,964 | 31,379 | Below every raw window; split only for a real semantic seam, not size. |
| VpnRouterService.java | 1 | 2,080 | 109,281 | 30,356 | Single Android VPN lifecycle; no context-driven Java layering. |
| Android Localization | 1 | 975 | 102,947 | 28,597 | Platform-specific data/API surface; no consolidation. |
| SingBoxManager partial | 6 | 1,883 | 93,913 | 26,090 | Already split by lifecycle concerns. |
| FreeConfigsPageViewModel | 1 | 1,945 | 89,661 | 24,906 | Fits; no size-only split. |
| VpnEngine | 1 | 1,625 | 85,014 | 23,615 | Stateful lifecycle; no size-only split. |
| AndroidStorage | 1 | 1,694 | 83,419 | 23,172 | Fits; existing Android boundary is sufficient. |
| CustomConfigInjector | 1 | 1,807 | 82,718 | 22,978 | Fits; prior split audit already rejected cosmetic extraction. |

### 5.1 MainWindowViewModel partials

| Part | Approx tokens |
|---|---:|
| `MainWindowViewModel.cs` anchor | 102,359 |
| Profiles | 13,619 |
| SimpleMode | 9,523 |
| Settings | 7,172 |
| Wgturn | 6,749 |
| Localization | 6,568 |
| ServerTesting | 5,338 |
| RuntimeStatus | 5,076 |
| Subscriptions | 5,063 |
| FreeConfigs | 4,357 |
| LocalizedLabels | 3,982 |
| AutostartBootstrap | 3,812 |
| ConnStats | 2,822 |
| ThemeAndLogo | 1,266 |

The anchor alone occupies 94% of a 108,800-token 128k working budget. The
right 128k workflow is not to split blindly: load the target feature partial,
search for the fields/properties/callers it uses, and retrieve only those
anchor slices plus direct models/tests.

The existing Banners extraction remains deferred because its conflict-action
write sites cross the connection flow. Its estimated ~40 lines save under 3%
of the anchor. That is not enough context benefit to justify a task by itself.

### 5.2 AndroidApp partials

| Part | Approx tokens |
|---|---:|
| `AndroidApp.axaml.cs` anchor | 33,199 |
| ServerList | 19,358 |
| SubscribePage | 16,974 |
| FreeConfigs | 16,132 |
| PerAppFilter | 15,657 |
| UiBindings | 12,885 |
| AdvancedShell | 10,839 |
| VpnLifecycle | 9,369 |
| Tools | 8,568 |
| ConfigShare | 8,428 |
| Remaining nine feature partials | 3,389..6,772 each |

The whole logical type exceeds 128k, but no individual feature partial plus
the 33k anchor inherently does. The current split improves reviewability;
moving feature partials into generic ViewModels/services would add lifecycle
boundaries without proving a smaller required closure.

## 6. Task closures and context windows

Closures include production files plus tests whose source mentions the relevant
types/features. This intentionally over-selects broad source-hash and
characterization tests.

| Task closure | Production | Matched tests | Combined upper bound |
|---|---:|---:|---:|
| Desktop settings/MTU UI | 375,714 | 126,840 | 502,554 |
| Tunnel lifecycle | 134,949 | 309,360 | 444,309 |
| Android application | 347,775 | 42,034 | 389,809 |
| Configuration/schema | 87,735 | 266,363 | 354,098 |
| Free configs | 94,227 | 61,942 | 156,169 |

Use 15% headroom for prompt, reasoning, and output:

| Window | Working budget | Fit result |
|---|---:|---|
| 128k | 108,800 | 259/261 raw modules fit; AndroidApp and whole MainWindowViewModel do not. None of the five upper-bound task closures fit. |
| 256k | 217,600 | All raw logical modules fit. Only free-configs closure fits whole; other closures need targeted retrieval. |
| 1M | 850,000 | Every measured closure fits; largest uses ~59% of the working budget. Entire product (1.63M) or product+all tests (2.43M) still does not fit and is not a useful task unit. |

### 6.1 Necessary invariants are not all necessary text

- Source-shape/characterization tests must be run when partial structure moves,
  but their full source need not be loaded to review a behavioral change.
- YAML/JSON source-generation registrations and namespace contracts must be
  searched when models move. That does not require loading all schema tests.
- A broad substring closure is a safe upper bound. A review should narrow it
  by symbols/callers and load the failing tests, not every test mentioning a
  common type name.
- Shared partial fields create two-way symbol references between an anchor and
  feature files. This is manageable retrieval coupling, not proof that a new
  class/service boundary is needed.

## 7. Findings and decisions

### C1 — 128k cannot review two whole logical partial types

Confirmed by measurement. This is a tooling/retrieval constraint, not a product
defect. Use symbol-directed slices. Do not claim a 128k full-type review for
AndroidApp or MainWindowViewModel.

### C2 — 256k fits all raw modules but not most full task closures

Confirmed. A 256k reviewer can load a whole large partial type, but must still
select dependencies/tests. Splitting every 20–40k file would not change this.

### C3 — 1M fits every measured practical closure

Confirmed for the five measured upper bounds. No logical module needs a
cross-file refactor merely to fit 1M. A whole-repository review remains a
search/retrieval problem, exactly as the 2026-08-01 audit concluded.

### C4 — MainWindowViewModel anchor is the only individual 128k pressure point

Confirmed. It is 102,359 tokens before dependencies. The already-recorded
Banners move is not worth scheduling solely for <3% context reduction. Revisit
only when a concrete behavior change exposes a stable semantic seam that also
reduces required shared state.

### C5 — existing AndroidApp and SingBoxManager splits are good

Confirmed structurally: feature files are review-sized and named by behavior.
No bad partial boundary or new context-driven type extraction was found.

### C6 — no new duplication beyond the existing audits

The 2026-08-01 audit/backlog already records CustomDirectRules dead runtime,
dead schema fields, Android DeepVerify/port copies, and Zapret/TgProxy path
centralization. This audit does not duplicate them as new work.

## 8. Generated, vendor, binary, and evidence separation

No tracked `bin`/`obj` generated C# entered the source inventory. Authored JSON
source-generation context declarations remain source; generated compiler output
does not.

Tracked binary summary, excluded from token estimates:

| Kind | Files | Bytes |
|---|---:|---:|
| PNG | 109 | 21,400,335 |
| DLL | 2 | 3,001,909 |
| EXE | 2 | 2,308,096 |
| TTF | 1 | 1,474,284 |
| JAR | 1 | 607,650 |
| ICNS | 1 | 663,763 |
| ICO | 5 | 391,399 |
| AAR | 1 | 151,839 |

`tools/zapret/` binaries are external upstream artifacts and are not product
source. Large historical screenshots under `plans/test-screenshots-*` are
evidence; the existing 2026-08-01 context-hygiene entry already covers
excluding them from default whole-review bundles. Do not delete or relog them.

## 9. Backlog decision

Recorded in `plans/refactor-backlog.md`:

- no new product refactor is queued for context fit;
- use target partial + anchor symbol slices for 128k/256k work;
- keep the existing tiered whole-review proposal, but do not add blanket
  ignores or a default context bundle until one dry-run measures it;
- MTU comments/example drift is a small coordinated cleanup inside the future
  MTU contract fix, not a separate architecture task.

## 10. Exact prompts for next tasks

### Task 1 — validate a 128k retrieval recipe without refactoring

```text
Using plans/context-logical-segments-audit-2026-08-03.md, run one read-only 128k dry-run on a real MainWindowViewModel.Settings task and one AndroidApp.VpnLifecycle task. Do not edit code.

For each task, begin with only the target partial. Search the symbols it reads/writes, then add exact declarations/callers from the anchor, direct models/services, the nearest focused tests, and repository instructions. Record every included file or line slice, UTF-8 bytes, approximate tokens at bytes/3.6, and why it is necessary. Reserve at least 15% of the 128k window for prompt/reasoning/output. Report missing invariants discovered by an adversarial second pass.

Success means both bundles stay <=108,800 tokens and contain enough context to identify the current behavior and the focused verification command. If either fails, identify the exact semantic seam causing failure; do not propose a split without that evidence.
```

### Task 2 — only when a concrete MVM change already touches banner/conflict logic

```text
Re-evaluate the deferred MainWindowViewModel Banners partial only as part of an already-required behavior change in the same conflict/banner area. Do not create a context-only refactor.

Re-grep every declaration, read, write, command, generated partial hook, and test for PlaceholderPruneNotice and ConflictingVpn warnings. Determine whether a pure move can reduce the 102,359-token anchor without adding a service/interface or moving conflict orchestration across an artificial boundary. Preserve ObservableProperty/RelayCommand generation and source-shape characterization invariants. If the diff is not a pure move or saves less than the current ~40-line estimate, leave the backlog item deferred. If justified, use the normal build/focused-test/review/CI workflow.
```

### Task 3 — implement the existing tiered review profile only after a dry-run

```text
Cross-check plans/context-logical-segments-audit-2026-08-03.md with plans/qwen-context-footprint-and-code-reduction-audit-2026-08-01.md. Build a proposed search-driven Tier 0-3 review manifest, not a blanket .qwenignore.

Tier 0: repository instructions and task prompt. Tier 1: target module/partial and direct symbol declarations. Tier 2: callers, models, platform boundary, invariants, focused tests. Tier 3: historical plans/evidence only when a current claim depends on them. Measure one desktop and one Android dry-run. Keep the profile only if each complete bundle is <=850,000 tokens for 1M and retrieval misses no invariant found by an adversarial grep. Do not delete, move, or ignore tracked evidence globally.
```

## Appendix A — complete logical production-source inventory

Token estimates are approximate and should be compared only at this snapshot.

| Module | Files | Lines | Bytes | Approx tokens |
|---|---:|---:|---:|---:|
| VPNRouter.Android/AndroidApp [partial+view] | 20 | 15722 | 701442 | 194855 |
| VPNRouter.Android/AndroidCategoryDefaults.cs | 1 | 272 | 10377 | 2883 |
| VPNRouter.Android/AndroidConfigBuilder.cs | 1 | 505 | 26971 | 7492 |
| VPNRouter.Android/AndroidConfigShare.cs | 1 | 312 | 13409 | 3725 |
| VPNRouter.Android/AndroidDeepVerifyBox.java | 1 | 603 | 28888 | 8025 |
| VPNRouter.Android/AndroidDiagnosticsExporter.cs | 1 | 320 | 14967 | 4158 |
| VPNRouter.Android/AndroidFreeConfigDeepVerifier.cs | 1 | 384 | 17272 | 4798 |
| VPNRouter.Android/AndroidFreeConfigsOrchestrator.cs | 1 | 416 | 18099 | 5028 |
| VPNRouter.Android/AndroidInstallerAdapter.cs | 1 | 100 | 4180 | 1162 |
| VPNRouter.Android/AndroidStorage.cs | 1 | 1694 | 83419 | 23172 |
| VPNRouter.Android/AndroidUpdater.cs | 1 | 252 | 10664 | 2963 |
| VPNRouter.Android/AppIconCache.cs | 1 | 154 | 6097 | 1694 |
| VPNRouter.Android/AppListLoader.cs | 1 | 223 | 9320 | 2589 |
| VPNRouter.Android/AvaloniaToggleNodeInfoProviderPatch.cs | 1 | 130 | 6078 | 1689 |
| VPNRouter.Android/Controls/StatusCard.cs | 1 | 117 | 5803 | 1612 |
| VPNRouter.Android/Json/AndroidJsonContext.cs | 1 | 106 | 5202 | 1445 |
| VPNRouter.Android/Localization.cs | 1 | 975 | 102947 | 28597 |
| VPNRouter.Android/MainActivity.cs | 1 | 1420 | 66849 | 18570 |
| VPNRouter.Android/MainApplication.cs | 1 | 59 | 2458 | 683 |
| VPNRouter.Android/QrScanLauncher.java | 1 | 92 | 4735 | 1316 |
| VPNRouter.Android/SlipstreamNative.java | 1 | 58 | 2569 | 714 |
| VPNRouter.Android/VpnControlReceiver.cs | 1 | 106 | 5376 | 1494 |
| VPNRouter.Android/VpnRouterService.java | 1 | 2080 | 109281 | 30356 |
| VPNRouter.App/App [view+codebehind] | 2 | 377 | 18515 | 5144 |
| VPNRouter.App/Converters.cs | 1 | 194 | 8841 | 2456 |
| VPNRouter.App/Localization/Strings.cs | 1 | 1452 | 119751 | 33265 |
| VPNRouter.App/Program.cs | 1 | 606 | 29944 | 8318 |
| VPNRouter.App/Services/InstallHealthCheck.cs | 1 | 154 | 7186 | 1997 |
| VPNRouter.App/Services/SelfRepair.cs | 1 | 173 | 8063 | 2240 |
| VPNRouter.App/Services/ShellMenuRegistrar.cs | 1 | 211 | 9852 | 2737 |
| VPNRouter.App/Services/ShortcutResolver.cs | 1 | 88 | 3314 | 921 |
| VPNRouter.App/Services/ShortcutSelfHeal.cs | 1 | 100 | 4419 | 1228 |
| VPNRouter.App/Services/SingleInstance.cs | 1 | 444 | 21342 | 5929 |
| VPNRouter.App/Services/SteamLibraryScanner.cs | 1 | 158 | 5940 | 1650 |
| VPNRouter.App/Services/WindowForegroundHelper.cs | 1 | 141 | 5977 | 1661 |
| VPNRouter.App/Services/WindowsServiceHelper.cs | 1 | 260 | 11108 | 3086 |
| VPNRouter.App/SimpleInputDetector.cs | 1 | 64 | 3194 | 888 |
| VPNRouter.App/Styles/Tokens [view+codebehind] | 1 | 275 | 15755 | 4377 |
| VPNRouter.App/ViewLocator.cs | 1 | 37 | 1059 | 295 |
| VPNRouter.App/ViewModels/AppGroupViewModel.cs | 1 | 70 | 3004 | 835 |
| VPNRouter.App/ViewModels/AppItemViewModel.cs | 1 | 99 | 4059 | 1128 |
| VPNRouter.App/ViewModels/AutoSelectStatus.cs | 1 | 35 | 1472 | 409 |
| VPNRouter.App/ViewModels/CustomConfigViewModel.cs | 1 | 45 | 1369 | 381 |
| VPNRouter.App/ViewModels/CustomRuleViewModel.cs | 1 | 117 | 4724 | 1313 |
| VPNRouter.App/ViewModels/FreeConfigs/FreeConfigItemViewModel.cs | 1 | 181 | 9218 | 2561 |
| VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs | 1 | 1945 | 89661 | 24906 |
| VPNRouter.App/ViewModels/Internals/ToolTabAvailability.cs | 1 | 30 | 1706 | 474 |
| VPNRouter.App/ViewModels/Internals/TwoPhaseStartCoordinator.cs | 1 | 239 | 12047 | 3347 |
| VPNRouter.App/ViewModels/MainWindowViewModel [partial] | 14 | 13409 | 639719 | 177706 |
| VPNRouter.App/ViewModels/ServerViewModel.cs | 1 | 645 | 30443 | 8457 |
| VPNRouter.App/ViewModels/ServiceViewModel.cs | 1 | 200 | 6294 | 1749 |
| VPNRouter.App/ViewModels/SubscriptionRefreshDiff.cs | 1 | 30 | 1347 | 375 |
| VPNRouter.App/ViewModels/SubscriptionViewModel.cs | 1 | 105 | 4295 | 1194 |
| VPNRouter.App/ViewModels/UpdateNotificationViewModel.cs | 1 | 296 | 13087 | 3636 |
| VPNRouter.App/ViewModels/ViewModelBase.cs | 1 | 7 | 151 | 42 |
| VPNRouter.App/ViewModels/ZapretStrategyDisplayItem.cs | 1 | 58 | 2715 | 755 |
| VPNRouter.App/Views/AboutWindow [view+codebehind] | 2 | 263 | 11137 | 3095 |
| VPNRouter.App/Views/MainWindow [view+codebehind] | 2 | 953 | 58356 | 16211 |
| VPNRouter.App/Views/Pages/ApplicationsPage [view+codebehind] | 2 | 425 | 22281 | 6190 |
| VPNRouter.App/Views/Pages/DpiBypassPage [view+codebehind] | 2 | 886 | 62516 | 17366 |
| VPNRouter.App/Views/Pages/EmergencyChannelPage [view+codebehind] | 2 | 358 | 22317 | 6200 |
| VPNRouter.App/Views/Pages/FreeConfigsPage [view+codebehind] | 2 | 495 | 33853 | 9405 |
| VPNRouter.App/Views/Pages/NetworkPage [view+codebehind] | 2 | 2490 | 149850 | 41626 |
| VPNRouter.App/Views/Pages/ServersPage [view+codebehind] | 2 | 539 | 29538 | 8206 |
| VPNRouter.App/Views/Pages/SimplePage [view+codebehind] | 2 | 400 | 25729 | 7148 |
| VPNRouter.App/Views/Pages/SubscribePage [view+codebehind] | 2 | 460 | 23843 | 6624 |
| VPNRouter.App/Views/Pages/TelegramPage [view+codebehind] | 2 | 577 | 37028 | 10287 |
| VPNRouter.App/Views/Pages/ToolsPage [view+codebehind] | 2 | 61 | 2840 | 790 |
| VPNRouter.CLI/Commands/DoctorCommand.cs | 1 | 58 | 2103 | 585 |
| VPNRouter.CLI/Commands/ProfilesCommand.cs | 1 | 190 | 7096 | 1972 |
| VPNRouter.CLI/Commands/ServiceCommand.cs | 1 | 187 | 6945 | 1930 |
| VPNRouter.CLI/Commands/StartCommand.cs | 1 | 343 | 15553 | 4321 |
| VPNRouter.CLI/Commands/StatusCommand.cs | 1 | 156 | 5916 | 1644 |
| VPNRouter.CLI/Commands/StopCommand.cs | 1 | 107 | 3489 | 970 |
| VPNRouter.CLI/Commands/TestUpdateCommand.cs | 1 | 201 | 8801 | 2445 |
| VPNRouter.CLI/Helpers/AdminHelper.cs | 1 | 31 | 1296 | 360 |
| VPNRouter.CLI/Helpers/CliJsonContext.cs | 1 | 55 | 2548 | 708 |
| VPNRouter.CLI/Helpers/ProfileSourceFactory.cs | 1 | 46 | 1627 | 452 |
| VPNRouter.CLI/Helpers/StateFile.cs | 1 | 94 | 3862 | 1073 |
| VPNRouter.CLI/Program.cs | 1 | 123 | 5146 | 1430 |
| VPNRouter.CLI/SettingsAwareTypeRegistrar.cs | 1 | 110 | 5153 | 1432 |
| VPNRouter.Core/AppPaths.cs | 1 | 209 | 10336 | 2872 |
| VPNRouter.Core/AppVersion.cs | 1 | 32 | 1861 | 517 |
| VPNRouter.Core/Interfaces/IFirewallManager.cs | 1 | 22 | 1214 | 338 |
| VPNRouter.Core/Interfaces/IProcessMonitor.cs | 1 | 17 | 450 | 125 |
| VPNRouter.Core/Interfaces/IProcessScanner.cs | 1 | 13 | 372 | 104 |
| VPNRouter.Core/Interfaces/IProfileSource.cs | 1 | 15 | 345 | 96 |
| VPNRouter.Core/Json/AppJsonContext.cs | 1 | 162 | 9057 | 2516 |
| VPNRouter.Core/Localization/Strings [partial] | 7 | 3497 | 235697 | 65475 |
| VPNRouter.Core/Models/AppConfig.cs | 1 | 427 | 21827 | 6064 |
| VPNRouter.Core/Models/AppSettings.cs | 1 | 122 | 5737 | 1594 |
| VPNRouter.Core/Models/AppSettingsSane.cs | 1 | 158 | 7511 | 2087 |
| VPNRouter.Core/Models/ConnectionIntent.cs | 1 | 15 | 480 | 134 |
| VPNRouter.Core/Models/CustomCategory.cs | 1 | 17 | 470 | 131 |
| VPNRouter.Core/Models/CustomConfigEntry.cs | 1 | 16 | 596 | 166 |
| VPNRouter.Core/Models/CustomDirectRule.cs | 1 | 52 | 2197 | 611 |
| VPNRouter.Core/Models/CustomRule.cs | 1 | 78 | 3721 | 1034 |
| VPNRouter.Core/Models/EmergencyChannelConfig.cs | 1 | 100 | 4416 | 1227 |
| VPNRouter.Core/Models/EmergencyChannelSettings.cs | 1 | 59 | 2589 | 720 |
| VPNRouter.Core/Models/EngineSettings.cs | 1 | 83 | 3229 | 897 |
| VPNRouter.Core/Models/HealthAdvice.cs | 1 | 25 | 427 | 119 |
| VPNRouter.Core/Models/PerAppFilterMode.cs | 1 | 121 | 5951 | 1654 |
| VPNRouter.Core/Models/ProcessRule.cs | 1 | 19 | 642 | 179 |
| VPNRouter.Core/Models/Profile.cs | 1 | 70 | 3262 | 907 |
| VPNRouter.Core/Models/ProfileSource.cs | 1 | 19 | 498 | 139 |
| VPNRouter.Core/Models/SubscriptionEntry.cs | 1 | 35 | 1236 | 344 |
| VPNRouter.Core/Models/TunSettings.cs | 1 | 123 | 5139 | 1428 |
| VPNRouter.Core/Models/UpdateInfo.cs | 1 | 30 | 1620 | 450 |
| VPNRouter.Core/Models/UserFreeSource.cs | 1 | 23 | 699 | 195 |
| VPNRouter.Core/Models/VlessConfig.cs | 1 | 151 | 6369 | 1770 |
| VPNRouter.Core/Models/VlessServerEntry.cs | 1 | 316 | 15625 | 4341 |
| VPNRouter.Core/Models/VlessTransportConfigs.cs | 1 | 77 | 2945 | 819 |
| VPNRouter.Core/Models/VPNConfig.cs | 1 | 834 | 37240 | 10345 |
| VPNRouter.Core/Models/WgturnEntry.cs | 1 | 49 | 2083 | 579 |
| VPNRouter.Core/Models/WgturnVariant.cs | 1 | 26 | 968 | 269 |
| VPNRouter.Core/Platform/Android/AndroidSingBoxRuntime.cs | 1 | 215 | 8622 | 2395 |
| VPNRouter.Core/Platform/AutostartHelper.cs | 1 | 313 | 13342 | 3707 |
| VPNRouter.Core/Platform/Linux/LinuxDnsHardening.cs | 1 | 266 | 12251 | 3404 |
| VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs | 1 | 346 | 15949 | 4431 |
| VPNRouter.Core/Platform/macOS/MacDnsHardening.cs | 1 | 248 | 10730 | 2981 |
| VPNRouter.Core/Platform/macOS/MacFirewallManager.cs | 1 | 511 | 23825 | 6619 |
| VPNRouter.Core/Platform/macOS/MacProcessMonitor.cs | 1 | 150 | 4838 | 1344 |
| VPNRouter.Core/Platform/macOS/MacProcessScanner.cs | 1 | 206 | 7654 | 2127 |
| VPNRouter.Core/Platform/macOS/NullFirewallManager.cs | 1 | 55 | 1612 | 448 |
| VPNRouter.Core/Platform/PlatformServices.cs | 1 | 154 | 6795 | 1888 |
| VPNRouter.Core/Platform/Unix/MacDnsParsers.cs | 1 | 178 | 7235 | 2010 |
| VPNRouter.Core/Platform/Unix/PsProcessLineParser.cs | 1 | 95 | 4197 | 1166 |
| VPNRouter.Core/Services/AndroidDpiBypassInjector.cs | 1 | 182 | 8274 | 2299 |
| VPNRouter.Core/Services/AndroidStorageSane.cs | 1 | 118 | 5449 | 1514 |
| VPNRouter.Core/Services/AutoFailoverEngine.cs | 1 | 388 | 19261 | 5351 |
| VPNRouter.Core/Services/BuiltInAndroidProfiles.cs | 1 | 154 | 5948 | 1653 |
| VPNRouter.Core/Services/CacheRecovery.cs | 1 | 307 | 13644 | 3790 |
| VPNRouter.Core/Services/CanaryPolicy.cs | 1 | 126 | 6050 | 1681 |
| VPNRouter.Core/Services/CanaryTargets.cs | 1 | 76 | 3300 | 917 |
| VPNRouter.Core/Services/ClashLogStream.cs | 1 | 221 | 9488 | 2636 |
| VPNRouter.Core/Services/ClashSingBoxApi.cs | 1 | 651 | 28814 | 8004 |
| VPNRouter.Core/Services/ClashYamlParser.cs | 1 | 259 | 10885 | 3024 |
| VPNRouter.Core/Services/ConfigGenerator.cs | 1 | 2210 | 112964 | 31379 |
| VPNRouter.Core/Services/ConfigPipeline.cs | 1 | 228 | 12587 | 3497 |
| VPNRouter.Core/Services/ConfigSanityCheck.cs | 1 | 372 | 18178 | 5050 |
| VPNRouter.Core/Services/ConfigShareDocument.cs | 1 | 362 | 14871 | 4131 |
| VPNRouter.Core/Services/ConflictingVpnDetector.cs | 1 | 190 | 8752 | 2432 |
| VPNRouter.Core/Services/ConnectionHealthClassifier.cs | 1 | 243 | 11731 | 3259 |
| VPNRouter.Core/Services/ConnectionHealthState.cs | 1 | 146 | 5444 | 1513 |
| VPNRouter.Core/Services/ConnectionIntentScorer.cs | 1 | 89 | 3263 | 907 |
| VPNRouter.Core/Services/CrashReporter.cs | 1 | 210 | 9892 | 2748 |
| VPNRouter.Core/Services/CustomConfigInjector.cs | 1 | 1807 | 82718 | 22978 |
| VPNRouter.Core/Services/CustomDirectRulesParser.cs | 1 | 195 | 7525 | 2091 |
| VPNRouter.Core/Services/CustomRulesImportExport.cs | 1 | 520 | 21813 | 6060 |
| VPNRouter.Core/Services/CustomRulesParser.cs | 1 | 314 | 12100 | 3362 |
| VPNRouter.Core/Services/DeepVerifyConstants.cs | 1 | 20 | 952 | 265 |
| VPNRouter.Core/Services/DeepVerifyProbe.cs | 1 | 226 | 9773 | 2715 |
| VPNRouter.Core/Services/Diagnostics/DiagnosticsExporter.cs | 1 | 550 | 29532 | 8204 |
| VPNRouter.Core/Services/Diagnostics/DiagnosticsRedactor.cs | 1 | 302 | 15078 | 4189 |
| VPNRouter.Core/Services/DnsFlusher.cs | 1 | 160 | 5992 | 1665 |
| VPNRouter.Core/Services/DnsLockdownPolicy.cs | 1 | 60 | 3027 | 841 |
| VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelEngine.cs | 1 | 218 | 8476 | 2355 |
| VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs | 1 | 352 | 13599 | 3778 |
| VPNRouter.Core/Services/EtwProcessMonitor.cs | 1 | 211 | 8797 | 2444 |
| VPNRouter.Core/Services/FirewallManager.cs | 1 | 1068 | 54121 | 15034 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs | 1 | 303 | 12816 | 3560 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigCache.cs | 1 | 151 | 6177 | 1716 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs | 1 | 420 | 18458 | 5128 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigFetcher.cs | 1 | 151 | 5825 | 1619 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigFreshness.cs | 1 | 228 | 10678 | 2967 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigGeoIp.cs | 1 | 186 | 7205 | 2002 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigKeepPolicy.cs | 1 | 77 | 3832 | 1065 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigModels.cs | 1 | 155 | 6722 | 1868 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigPoolFetcher.cs | 1 | 272 | 12460 | 3462 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigSources.cs | 1 | 131 | 4713 | 1310 |
| VPNRouter.Core/Services/FreeConfigs/FreeConfigTester.cs | 1 | 181 | 8469 | 2353 |
| VPNRouter.Core/Services/GeoDataDownloader.cs | 1 | 202 | 9458 | 2628 |
| VPNRouter.Core/Services/HealthCheck.cs | 1 | 862 | 38050 | 10570 |
| VPNRouter.Core/Services/HealthMonitor.cs | 1 | 1315 | 71270 | 19798 |
| VPNRouter.Core/Services/HostsManager.cs | 1 | 575 | 26216 | 7283 |
| VPNRouter.Core/Services/IFileSystem.cs | 1 | 152 | 6324 | 1757 |
| VPNRouter.Core/Services/IHttpClient.cs | 1 | 199 | 10228 | 2842 |
| VPNRouter.Core/Services/IProcessRunner.cs | 1 | 224 | 11217 | 3116 |
| VPNRouter.Core/Services/ISettingsStore.cs | 1 | 148 | 6639 | 1845 |
| VPNRouter.Core/Services/ISingBoxApi.cs | 1 | 163 | 8759 | 2434 |
| VPNRouter.Core/Services/IUnixDnsHardening.cs | 1 | 75 | 3424 | 952 |
| VPNRouter.Core/Services/IWindowsDnsHardening.cs | 1 | 188 | 8990 | 2498 |
| VPNRouter.Core/Services/LaunchFailureCounter.cs | 1 | 259 | 11099 | 3084 |
| VPNRouter.Core/Services/LeakProtection.cs | 1 | 863 | 43578 | 12105 |
| VPNRouter.Core/Services/LinuxRuntimeEnvironment.cs | 1 | 79 | 2539 | 706 |
| VPNRouter.Core/Services/LockFile.cs | 1 | 238 | 10218 | 2839 |
| VPNRouter.Core/Services/NaivePairing.cs | 1 | 124 | 6121 | 1701 |
| VPNRouter.Core/Services/NetPortUtil.cs | 1 | 25 | 933 | 260 |
| VPNRouter.Core/Services/NetworkInterfaceDetector.cs | 1 | 307 | 13630 | 3787 |
| VPNRouter.Core/Services/OrphanCleanup.cs | 1 | 160 | 8227 | 2286 |
| VPNRouter.Core/Services/PlaceholderDefense.cs | 1 | 533 | 28460 | 7906 |
| VPNRouter.Core/Services/PolicyHttpClient.cs | 1 | 422 | 18128 | 5036 |
| VPNRouter.Core/Services/PowerEventListener.cs | 1 | 162 | 6628 | 1842 |
| VPNRouter.Core/Services/ProcessImagePath.cs | 1 | 253 | 11695 | 3249 |
| VPNRouter.Core/Services/ProcessOwnership.cs | 1 | 743 | 27386 | 7608 |
| VPNRouter.Core/Services/ProcessQuery.cs | 1 | 97 | 3369 | 936 |
| VPNRouter.Core/Services/ProcessRunner.cs | 1 | 331 | 12525 | 3480 |
| VPNRouter.Core/Services/ProcessScanner.cs | 1 | 290 | 12012 | 3337 |
| VPNRouter.Core/Services/ProfileApplication.cs | 1 | 81 | 3266 | 908 |
| VPNRouter.Core/Services/ProfileManager.cs | 1 | 477 | 22476 | 6244 |
| VPNRouter.Core/Services/ProviderKey.cs | 1 | 73 | 3317 | 922 |
| VPNRouter.Core/Services/RealFileSystem.cs | 1 | 160 | 6328 | 1758 |
| VPNRouter.Core/Services/RemoteVersionChecker.cs | 1 | 213 | 8218 | 2283 |
| VPNRouter.Core/Services/ResilientStarter.cs | 1 | 168 | 6764 | 1879 |
| VPNRouter.Core/Services/RoutingAppListEditor.cs | 1 | 159 | 7782 | 2162 |
| VPNRouter.Core/Services/RuleSetCacheManager.cs | 1 | 219 | 10293 | 2860 |
| VPNRouter.Core/Services/RuntimeStatusDetector.cs | 1 | 122 | 5170 | 1437 |
| VPNRouter.Core/Services/SafeMode.cs | 1 | 21 | 868 | 242 |
| VPNRouter.Core/Services/ServerHealthClassifier.cs | 1 | 196 | 9977 | 2772 |
| VPNRouter.Core/Services/ServerHealthPhaseMapper.cs | 1 | 128 | 6839 | 1900 |
| VPNRouter.Core/Services/ServerHealthProbe.cs | 1 | 121 | 5605 | 1557 |
| VPNRouter.Core/Services/ServerHealthStore.cs | 1 | 154 | 6569 | 1825 |
| VPNRouter.Core/Services/ServerUriParser.cs | 1 | 823 | 41244 | 11457 |
| VPNRouter.Core/Services/SettingsLoader.cs | 1 | 778 | 38010 | 10559 |
| VPNRouter.Core/Services/SettingsMigrator.cs | 1 | 712 | 33284 | 9246 |
| VPNRouter.Core/Services/SettingsValidator.cs | 1 | 323 | 12416 | 3449 |
| VPNRouter.Core/Services/SingBoxFeatures.cs | 1 | 161 | 7026 | 1952 |
| VPNRouter.Core/Services/SingBoxManager [partial] | 6 | 1883 | 93913 | 26090 |
| VPNRouter.Core/Services/SlipstreamManager.cs | 1 | 727 | 35117 | 9755 |
| VPNRouter.Core/Services/SplitTunnelDriverInterop.cs | 1 | 291 | 16366 | 4547 |
| VPNRouter.Core/Services/SplitTunnelDriverManager.cs | 1 | 1190 | 58573 | 16271 |
| VPNRouter.Core/Services/SplitTunnelDriverProtocol.cs | 1 | 702 | 40247 | 11180 |
| VPNRouter.Core/Services/StartupPipeline.cs | 1 | 1451 | 70429 | 19564 |
| VPNRouter.Core/Services/StjNodeHelpers.cs | 1 | 96 | 3906 | 1085 |
| VPNRouter.Core/Services/StorageBlobRecovery.cs | 1 | 96 | 4023 | 1118 |
| VPNRouter.Core/Services/StrictDnsFailoverPolicy.cs | 1 | 66 | 3536 | 983 |
| VPNRouter.Core/Services/SubscriptionFetcher.cs | 1 | 349 | 16486 | 4580 |
| VPNRouter.Core/Services/SubscriptionResolver.cs | 1 | 98 | 4608 | 1280 |
| VPNRouter.Core/Services/SubscriptionUserInfo.cs | 1 | 93 | 4158 | 1155 |
| VPNRouter.Core/Services/SuffixMatch.cs | 1 | 34 | 1339 | 372 |
| VPNRouter.Core/Services/TcpTlsProbe.cs | 1 | 630 | 28334 | 7871 |
| VPNRouter.Core/Services/TgProxyManager.cs | 1 | 664 | 27503 | 7640 |
| VPNRouter.Core/Services/TgProxyPortConflictException.cs | 1 | 46 | 2065 | 574 |
| VPNRouter.Core/Services/TgProxyUpdater.cs | 1 | 484 | 22327 | 6202 |
| VPNRouter.Core/Services/TunAdapterDiagnostics.cs | 1 | 886 | 43747 | 12152 |
| VPNRouter.Core/Services/TunnelStateResync.cs | 1 | 82 | 4897 | 1361 |
| VPNRouter.Core/Services/TunOwnershipLock.cs | 1 | 303 | 11511 | 3198 |
| VPNRouter.Core/Services/UdpDegradationDetector.cs | 1 | 60 | 2879 | 800 |
| VPNRouter.Core/Services/UpdateBackup.cs | 1 | 341 | 14632 | 4065 |
| VPNRouter.Core/Services/UpdateChecker.cs | 1 | 1431 | 72656 | 20183 |
| VPNRouter.Core/Services/UpdateSources/GitHubReleaseSource.cs | 1 | 330 | 13542 | 3762 |
| VPNRouter.Core/Services/UpdateSources/IUpdateSource.cs | 1 | 184 | 9093 | 2526 |
| VPNRouter.Core/Services/UpdateSources/SideloadSource.cs | 1 | 296 | 12604 | 3502 |
| VPNRouter.Core/Services/VlessDeepVerifier.cs | 1 | 785 | 37055 | 10294 |
| VPNRouter.Core/Services/VlessServersResolver.cs | 1 | 250 | 13760 | 3823 |
| VPNRouter.Core/Services/VlessUriParser.cs | 1 | 238 | 10790 | 2998 |
| VPNRouter.Core/Services/VpnEngine.cs | 1 | 1625 | 85014 | 23615 |
| VPNRouter.Core/Services/WedgeKillPolicy.cs | 1 | 27 | 1589 | 442 |
| VPNRouter.Core/Services/WgturnDownloadException.cs | 1 | 45 | 1906 | 530 |
| VPNRouter.Core/Services/WgturnUpdater.cs | 1 | 585 | 25946 | 7208 |
| VPNRouter.Core/Services/WindowsDnsHardening.cs | 1 | 509 | 23924 | 6646 |
| VPNRouter.Core/Services/ZapretActions.cs | 1 | 653 | 26489 | 7359 |
| VPNRouter.Core/Services/ZapretAutoStrategy.cs | 1 | 1332 | 63034 | 17510 |
| VPNRouter.Core/Services/ZapretManager.cs | 1 | 377 | 17493 | 4860 |
| VPNRouter.Core/Services/ZapretProbeCache.cs | 1 | 381 | 16681 | 4634 |
| VPNRouter.Core/Services/ZapretUpdater.cs | 1 | 835 | 36056 | 10016 |
| VPNRouter.Core/Yaml/DateTimeOffsetYamlConverter.cs | 1 | 84 | 3610 | 1003 |
| VPNRouter.Core/Yaml/YamlStaticContext.cs | 1 | 110 | 5794 | 1610 |
| VPNRouter.Service/Program.cs | 1 | 184 | 7277 | 2022 |
| VPNRouter.Service/ServiceInstaller.cs | 1 | 263 | 10034 | 2788 |
| VPNRouter.Service/VPNRouterService.cs | 1 | 527 | 23227 | 6452 |
