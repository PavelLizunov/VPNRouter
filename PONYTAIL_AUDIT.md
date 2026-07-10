# Ponytail audit — VPNRouter

> **Execution status (2026-07-01, goal-codex-ponytail-cleanup) — code portion applied.**
> DONE (committed to main, ~-2,580 lines over 4 commits): the dead `FreeConfigs/Stages/`
> pipeline (`b72bb698`, -2,132) · dead converter + enum alias + 2 stale scripts
> (`76de366e`, -383) · `PlaceholderGuard` shim → `PlaceholderDefense` (`3a6e8d56`, -64) ·
> Android `QrCodeDecoder` + `ZXing.Net` dep + `AppIconCache` dead members + `AppEntry.Icon`
> (`71fa3e43`, -150, -1 dep — Android compile-verified on .NET 10). Net -2,719 lines,
> -1 dep, 42 files. Each grep-re-verified dead before cutting; all desktop builds + full
> placeholder/free-config suites green + Android compile clean.
> SKIPPED (ponytail judgment — marginal or risky-for-little): `SubscriptionUserInfo.RemainingBytes`
> (tiny tested DTO API) · `PlaceholderDefense.LayerX`/`ConfigSanityCheck` forwarders
> (security-adjacent, cosmetic) · `MacDnsParsers.LooksLikeIpAddress`→`IPAddress.TryParse`
> (more-permissive in a DNS path = edge-case risk) · `StartCommand.BuildDryRunSources`
> (changes dry-run output) · `AppIconCache` LRU→`ConcurrentDictionary` (behaviour change) ·
> `StatusCard.IsWarn` (never-lit but harmless; churn) · `-GitHubRepo` param + `smoke-update`
> / `check-methodology` trims + `build.ps1` prune-loop (build-script blast-radius, low value).
> DEFERRED to user (housekeeping, not code): the 3.6 GB worktrees + stale ZIPs + 332 plans.

Repo-wide over-engineering scan (2026-07-01), 5 parallel read-only auditors +
artifact pass. Scope: complexity/dead-weight only — **not** bugs/security/perf.
Every finding grounded in a real `path:line`; test seams (`IProcessRunner`,
`IFileSystem`, etc.) and cross-platform/fail-closed logic were verified
load-bearing and are NOT flagged. **Lists only — applies nothing.**

Verdict: the C# is largely lean and already audited (comments cite prior purges).
The weight is concentrated in **(a) dead disk artifacts** and **(b) one dead
Free-Configs "stage pipeline"** that was built, tested, and never wired in.

---

## A. Dead weight on disk (biggest cut, not code)

`delete:` **3.6 GB — `.claude/worktrees/` (3 orphaned agent worktrees)**, idle 4 days, full repo copies. `git worktree remove` each; 2 are 0-ahead of main, `funny-jang-fc7848` has 1 unmerged commit — check it first. `[.claude/worktrees/]`
`delete:` **~216 MB — stale build ZIPs in repo root** (`VPNRouter-{,update-}v2.45.0-r6/r7-win.zip`) + `android-r3-build.log`. Gitignored, superseded by r8. `rm`. `[./]`
`shrink:` **332 `plans/*.md`** — plans/CLAUDE.md says archive after each stable cut; never done. Move pre-v2.44 post-mortems/handoffs to `plans/archive/2026/`. `[plans/]`
`delete:` **`.codex/`** (5 KB) — stray, unreferenced by build/CI/hooks. Confirm not your scratch, then drop. `[.codex/]`

## B. Code — over-engineering (ranked, biggest cut first)

`delete:` **entire `FreeConfigs/Stages/` pipeline (~1,190 lines) is dead in production.** `IFreeConfigStage`/`Stages` are referenced nowhere in App/CLI/Service/Android (confirmed); its only driver `FreeConfigAggregator.RefreshAsync` has zero production callers — the UI uses `FetchPoolAsync` + `Cache.Load` + events directly. Delete `RefreshAsync`+`RunWithRetryAsync`, `IFreeConfigStage.cs`, `Stages/*.cs`, and the 8 stage-only tests. Nothing replaces it. `[VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs:92; IFreeConfigStage.cs:40; Stages/*.cs]`
`yagni:` **`StageRetryPolicy`+`StageRetry`+`StageContext`+`StageResult`** — a retry/short-circuit/telemetry framework ("Phase 4 will load this from yaml") for the dead pipeline; no override ever configured. Dies with it. `[VPNRouter.Core/Services/FreeConfigs/IFreeConfigStage.cs:94-192]`
`delete:` **`tools/live-test-r1.ps1` (202 lines)** — one-off v2.27.2 repro harness hardcoding a `.claude/worktrees/…` path that no longer exists; run by nothing. `[tools/live-test-r1.ps1:20]`
`yagni:` **`PlaceholderGuard` (62-line forwarder shim)** — its own header says "new code should call `PlaceholderDefense` directly; will be removed." Migration never finished → permanent double API surface. Rename ~9 callers, delete the file. `[VPNRouter.Core/Services/PlaceholderGuard.cs:35-62]`
`delete:` **`tools/build-singbox.ps1` (134 lines)** — superseded: `build.ps1` auto-downloads sing-box inline (329-357) and the *lx* variant is the live one. `[build-singbox.ps1:1]`
`delete:` **`VPNRouter.Android/QrCodeDecoder.cs` (115 lines)** — zero callers; the live QR scan uses `zxing-android-embedded`, and MainActivity notes this managed path was replaced. Kept for a "future paste-image flow" that doesn't exist. `[VPNRouter.Android/QrCodeDecoder.cs:24]`
`native:` **`ZXing.Net` PackageReference** — becomes unused once QrCodeDecoder goes (scanner runs on the `zxing-android-embedded` aar). Drop it. **(-1 dep)** `[VPNRouter.Android/VPNRouter.Android.csproj:151]`
`yagni:` **`PlaceholderDefense` `LayerX_*` sub-classes** — `LayerD.IsPlaceholderEntry`/`Layer6.InspectForDeepVerify` are one-line delegators "kept separate for future divergence." Collapse into the parent's public methods. `[VPNRouter.Core/Services/PlaceholderDefense.cs:356-502]`
`stdlib:` **`AppIconCache` hand-rolls an LRU** (Dictionary+LinkedList+lock) for ~100 stable icons. `ConcurrentDictionary<string,Bitmap>`; the eviction cap isn't load-bearing. `[VPNRouter.Android/AppIconCache.cs:56]`
`yagni:` **`build.ps1` / `build-linux.ps1` `-GitHubRepo` param** — defaulted to `PavelLizunov/VPNRouter`, never overridden by any caller. Inline the constant. `[build.ps1:60; build-linux.ps1:18]`
`yagni:` **`smoke-update.ps1` steps 5-6** — self-admitted dead ("requires a DataDir hook we don't have"); the static AppVersion+ZIP check that runs is already covered by `verify-release-integrity.yml`. Drop the dead branch. `[tools/smoke-update.ps1:145]`
`yagni:` **`check-methodology.sh` warn-only meta-tests #2/#4/#7/#9** — emit `warn`/`pass` only, never `fail`; header claims a pre-push hook that doesn't reference it. Unwired. `[tools/check-methodology.sh:59]`
`delete:` **`ConfigSanityCheck.FindFirstProxyOutbound`/`InspectOutbound`** — back-compat forwarders to `PlaceholderDefense.LayerE_*`; direct calls once the Layers flatten. `[VPNRouter.Core/Services/ConfigSanityCheck.cs:208-234]`
`shrink:` **`StartCommand.BuildDryRunSources` (~27 lines)** — near-duplicate of `ProfileSourceFactory.Create`; dry-run can call the factory. `[VPNRouter.CLI/Commands/StartCommand.cs:317-343]`
`delete:` **`ActionToChipColorConverter` (21 lines)** — 0 XAML/`.cs` refs; superseded by `ActionToTokenBrushConverter`. `[VPNRouter.App/Converters.cs:101-135]`
`delete:` **`BuildId` duplicated byte-identical** in `ParseStage` + `FreeConfigAggregator` (SHA1 `host:port:uuid` → `Convert.ToHexString`). Keep the aggregator copy. (folds into the Stages delete) `[VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs:370]`
`delete:` **`AppEntry.Icon` (raw `Drawable`) field** — written in `AppListLoader`, never read (UI binds `IconBitmap`). Pass `icon` straight to `GetOrConvert`. `[VPNRouter.Android/AppListLoader.cs:54]`
`yagni:` **`StatusCard` `IsWarn` third state** — only ever set `false`; the `_dotWarn` ellipse + `IsWarnProperty` + `SyncDots` branch are a never-lit path. Two-state it. `[VPNRouter.Android/Controls/StatusCard.cs:29]`
`delete:` **`AppIconCache.GetCached`/`Clear`/`Count`** — no `.cs` caller. `[VPNRouter.Android/AppIconCache.cs:124]`
`delete:` **`SubscriptionUserInfo.RemainingBytes`** — computed property, only tests read it. `[VPNRouter.Core/Services/SubscriptionUserInfo.cs:19]`
`stdlib:` **`MacDnsParsers.LooksLikeIpAddress`** hand-rolls IPv4/IPv6 validation → `System.Net.IPAddress.TryParse`. (leave sibling `DeriveDnsTarget` — it needs the `/prefix` split) `[VPNRouter.Core/Platform/Unix/MacDnsParsers.cs:157]`
`delete:` **`SmpInputKind.Vless` obsolete alias** + doc — 0 live callers, all code uses `ServerUri`. `[VPNRouter.App/SimpleInputDetector.cs:20-22]`

## Deliberately NOT flagged (verified load-bearing)

Test-seam interfaces (mockable `I*`); 3-impl `IFirewallManager` + Unix/Win DNS
hardening; sing-box JSON DTOs; the `MainWindowViewModel` partial split + its
pure-static extracted helpers (pinned characterization hash); all pre-commit/
pre-push/CI gates (each cites a specific shipped incident); the 3
`build-singbox-lx.ps1` patches; JNI/adapter bridges; `-SlipstreamPath` (wired
feature); `WindowsServiceHelper` sc.exe wrap (dependency-avoidance = the lazy
choice).

---

**net: code ~-1,960 lines, -1 dep · disk ~-3.8 GB + ~330 plan files.**
The single highest-value code cut is the dead `FreeConfigs/Stages/` pipeline
(~1,190 lines, ~60% of the code total). The rest is small, honest dead code.
Biggest cut of all is 3.6 GB of orphaned git worktrees — housekeeping, not
architecture.
