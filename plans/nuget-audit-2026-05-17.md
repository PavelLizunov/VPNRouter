# NuGet Package Audit — v2.32.3 baseline (2026-05-17)

Read-only audit. Source data: nuget.org v3 flat-container + registration5
endpoints (versions, vulnerabilities, deprecation flags). Snapshot date
2026-05-17. No `.csproj` files were modified.

## Summary

| Metric | Value |
|---|---|
| Total unique packages | 31 |
| CURRENT (latest stable) | 4 (~13%) |
| MINOR-BEHIND (≤2 minors) | 2 |
| STALE (3+ minors / 1+ major behind) | 25 |
| SECURITY (known CVE on the pinned version) | 0 |
| DEPRECATED (pinned version flagged) | 0 |
| Avalonia drift | 0 (all desktop+Android+headless pinned to 11.3.12) |
| .NET target | net8.0 / net8.0-windows / net8.0-android (LTS until Nov 2026) |

Headline: **no security action required today**, but a ~6-month update lag
has piled up across the Microsoft.Extensions / Avalonia / test stacks. The
v3.0 refactor is the right moment to bring everything forward in one
coordinated bump rather than 31 individual chases.

## Critical updates (security)

**None.** Every pinned version returned `CLEAN` from the
`registration5-gz-semver2/<pkg>/index.json` vulnerabilities scan:

| Package | Pinned | Status |
|---|---|---|
| Newtonsoft.Json | 13.0.3 | clean (13.0.1 was last vulnerable; 13.0.2+ patched GHSA-5crp-9r3c-p9vr) |
| YamlDotNet | 15.1.2 | clean (CVE-fixed in 13.x line) |
| SkiaSharp | 2.88.9 | clean (2.80.x had GHSA-j7hp-h8jx-5ppr; patched) |
| System.Drawing.Common | 8.0.0 | clean (pre-7.0 had GHSA-rxg9-xrhp-64gj) |
| System.Management | 8.0.0 | clean |
| Microsoft.Win32.SystemEvents | 8.0.0 | clean |

74 historical Newtonsoft.Json + 164 YamlDotNet + 32 System.Drawing.Common +
64 SkiaSharp advisories exist on nuget, but **none affect the pinned
versions**. Good hygiene.

## Stale packages (3+ minors / 1+ major behind)

| Package | Current | Latest stable | Behind | Projects | Notes |
|---|---|---|---|---|---|
| Newtonsoft.Json | 13.0.3 | 13.0.4 | 1 patch | Core, CLI, Service, Android | Minor — safe bump |
| Serilog | 3.1.1 | 4.3.1 | 1 major | Core, CLI, Service, Android | API mostly source-compatible, log-level enum was tweaked |
| Serilog.Sinks.Console | 5.0.0 | 6.1.1 | 1 major | Core, CLI, Android | Tracks Serilog 4 |
| Serilog.Sinks.File | 5.0.0 | 7.0.0 | 2 majors | Core, CLI, Service, Android | Tracks Serilog 4 |
| Serilog.Extensions.Logging | 8.0.0 | 10.0.0 | 2 majors | CLI | Tracks .NET 10 / Serilog 4 |
| Serilog.Extensions.Hosting | 8.0.0 | 10.0.0 | 2 majors | Service | Same |
| YamlDotNet | 15.1.2 | 17.1.0 | 2 majors | Core, Android | Schema-affecting changes in 16; need migration check |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.7 | 3.2.2 | 0.5 minor | Core (Win-only) | Small bump, ETW provider fixes |
| System.Management | 8.0.0 | 10.0.8 | 2 majors | Core (Win-only) | Pinned to runtime; **Microsoft recommends to NOT explicitly pin** — SDK transitively pulls correct version |
| Microsoft.Win32.SystemEvents | 8.0.0 | 10.0.8 | 2 majors | Core (Win-only) | Same caveat as above |
| Microsoft.Extensions.Hosting | 8.0.1 | 10.0.8 | 2 majors | Service | Tracks .NET runtime |
| Microsoft.Extensions.Hosting.WindowsServices | 8.0.0 | 10.0.8 | 2 majors | Service | Same |
| Spectre.Console | 0.49.1 | 0.55.2 | 6 minors | CLI | Big jump; check for breaking API changes |
| Spectre.Console.Cli | 0.49.1 | 0.55.0 | 6 minors | CLI | Same |
| Avalonia (+ Desktop / Android / Themes.Fluent / Fonts.Inter / Headless / Headless.XUnit) | 11.3.12 | 12.0.3 | 1 major | App, Android, Tests | Major rewrite of layout/text; significant migration cost |
| Avalonia.Diagnostics | 11.3.12 | 11.3.15 | 3 patches | App | Avalonia.Diagnostics has NOT shipped 12.x yet — stays on 11.x track |
| SkiaSharp | 2.88.9 | 3.119.2 | 1 major | App | 3.x has memory-management API rework; transitive via Avalonia 12 |
| CommunityToolkit.Mvvm | 8.2.1 (App) / 8.4.0 (Android) | 8.4.2 | 0–2 minors | App, Android | Source-generator bug fixes; **DRIFT between projects** |
| Xamarin.AndroidX.Core | 1.13.1.5 | 1.18.0 | 5 minors | Android | Tracks AndroidX 1.18; required for new Android API levels |
| ZXing.Net | 0.16.10 | 0.16.11 | 1 patch | Android | Tiny bump; QR scanner library |
| coverlet.collector | 6.0.0 | 10.0.0 | 4 majors | Tests | Versioning is just synced to .NET — non-breaking |
| Microsoft.NET.Test.Sdk | 17.8.0 | 18.5.1 | 1 major (+ 5 minors) | Tests | Required for VS 17.12+ / .NET 9+ test runner |
| xunit | 2.5.3 | 2.9.3 | 4 minors | Tests | Same line, perf + bug fixes; **xunit v3 exists** and is the future track |
| xunit.runner.visualstudio | 2.5.3 | 3.1.5 | 1 major | Tests | Tracks Test.Sdk 18 |
| System.Drawing.Common | 8.0.0 | 10.0.8 | 2 majors | tools/VpnRouterTestMcp | Win-only test tooling |

## Avalonia drift check

Desktop App + Android + Headless (Tests) **all pinned to 11.3.12**. No
drift. Single bump to 11.3.15 (patches only) is safe; a 12.x bump should
be a separate v3.0 milestone because of the breaking layout/text rewrite.

Note that **`Avalonia.Diagnostics` still tops out at 11.3.15** — upstream
hasn't shipped a v12 yet. If/when we move the rest of Avalonia.* to 12,
Avalonia.Diagnostics becomes a forced drift until upstream catches up.
Practical impact: zero (Debug-only dependency, already Privatised in
Release builds via `IncludeAssets=None`).

## Drift between projects (real, fix first)

| Package | App | Android | Tests | Action |
|---|---|---|---|---|
| CommunityToolkit.Mvvm | 8.2.1 | 8.4.0 | — | Align to 8.4.2 across both |

This is the only inter-project drift in the audit. Low-risk because the
two projects are not consuming each other's compiled output, but the
source-generator versions diverge and could emit different code paths.

## Modernization candidates

### 1. Newtonsoft.Json → System.Text.Json (HIGH value, LOW urgency)

Used in: Core (4 files), CLI (Service/Profile JSON), Service (state file),
Android (config snapshots).

Approx 70-100 serialize/deserialize sites across the codebase (per
`grep -c JsonConvert.|JsonSerializer.` heuristic — exact count requires
follow-up). `System.Text.Json` ships in-box with .NET 8 (zero new
dependency), is ~2-5× faster, has source-generator support (AOT-friendly
for Android), and `Newtonsoft.Json` is in maintenance-only mode.

Cost: medium — Newtonsoft's `JObject` / `JToken` API is more permissive
than STJ's `JsonNode`; loose-typed sites need careful porting. Free
Configs cache (`FreeConfigCache`), sing-box generated JSON
(`ConfigGenerator.Generate`), CustomConfigInjector all use Newtonsoft
heavily. **Recommend phasing: v3.0 net8 → STJ for new code, leave
existing Newtonsoft sites alone; v3.1 sweep migration.**

### 2. Serilog 3 → 4 (MEDIUM value, MEDIUM urgency)

Used in: Core, CLI, Service, Android. Serilog 4 was the breaking-change
release (April 2024). Most app code keeps working; the breaking changes
are in sinks and enrichers. Since we use 3 standard sinks
(Console / File / Hosting), the migration is bounded.

Cost: low — bump 6 packages together, regression-test logging output.
Pre-requisite for picking up newer `Serilog.Extensions.Hosting` 10.x.

### 3. Avalonia 11 → 12 (HIGH value, HIGH cost, v3.x candidate)

Avalonia 12 is stable as of Oct 2025 (versions 12.0.0 → 12.0.3). Breaking
changes include:
- Text rendering rewrite (font metrics differ → visual-diff baseline
  needs full re-pin).
- Layout invalidation semantics changed (some `Bindings` re-evaluation
  patterns need to be re-checked).
- `Avalonia.Diagnostics` still 11.x (forced drift if we move).
- SkiaSharp 3.x is the transitive dep; our explicit `SkiaSharp 2.88.9`
  pin in `VPNRouter.App.csproj` would need bumping to 3.x.

Cost: high — requires re-baseline of all `screenshots/baseline/*.png` for
`VisualDiffTests`, plus regression sweep across all 14 page snapshot
tests. **Plan as dedicated v3.x milestone.**

### 4. xunit v2 → v3 (LOW value, LOW urgency)

xunit v3 exists and is the long-term track, but v2.9.x is fully supported
and matches our current `Microsoft.NET.Test.Sdk 17.8.0`. v3 requires
`Test.Sdk 18+` and changes the test-discovery model. **Defer unless a v2
feature blocks us.**

### 5. Spectre.Console 0.49 → 0.55 (LOW urgency)

6 minor versions behind, but the public API is highly stable (the 0.x
versioning is misleading — Spectre treats these as patch-style). Bump
when convenient.

## Per-project breakdown

### VPNRouter.Core (net8.0 / net8.0-android, opt-in)
- Newtonsoft.Json 13.0.3 → 13.0.4 (patch)
- Serilog 3.1.1 → 4.3.1 (major)
- Serilog.Sinks.Console 5.0.0 → 6.1.1 (major)
- Serilog.Sinks.File 5.0.0 → 7.0.0 (2 majors)
- YamlDotNet 15.1.2 → 17.1.0 (2 majors)
- Microsoft.Diagnostics.Tracing.TraceEvent 3.1.7 → 3.2.2 (Win-only)
- System.Management 8.0.0 → 10.0.8 (Win-only, transitively managed)
- Microsoft.Win32.SystemEvents 8.0.0 → 10.0.8 (Win-only, transitively managed)

### VPNRouter.App (net8.0)
- Avalonia 11.3.12 → 11.3.15 (patch — safe now) or 12.0.3 (major — v3.x)
- Avalonia.Desktop / Themes.Fluent / Fonts.Inter / Diagnostics — same
- SkiaSharp 2.88.9 → 3.119.2 (major — couple to Avalonia 12)
- CommunityToolkit.Mvvm 8.2.1 → 8.4.2 (minor — **fix App/Android drift**)

### VPNRouter.CLI (net8.0-windows)
- Same Newtonsoft + Serilog stack as Core
- Serilog.Extensions.Logging 8.0.0 → 10.0.0 (2 majors, tied to Serilog 4 + .NET 10)
- Spectre.Console 0.49.1 → 0.55.2 (6 minors)
- Spectre.Console.Cli 0.49.1 → 0.55.0 (6 minors)

### VPNRouter.Service (net8.0-windows)
- Same Newtonsoft + Serilog stack
- Microsoft.Extensions.Hosting 8.0.1 → 10.0.8 (2 majors, .NET runtime track)
- Microsoft.Extensions.Hosting.WindowsServices 8.0.0 → 10.0.8 (same)
- Serilog.Extensions.Hosting 8.0.0 → 10.0.0 (tied to Serilog 4)

### VPNRouter.Android (net8.0-android)
- Mirrors Core's package set + Avalonia.Android 11.3.12 → 12.0.3
- CommunityToolkit.Mvvm 8.4.0 → 8.4.2 (close to current)
- Xamarin.AndroidX.Core 1.13.1.5 → 1.18.0 (5 minors — required for new
  Android target SDK levels)
- ZXing.Net 0.16.10 → 0.16.11 (patch)

### VPNRouter.Tests (net8.0)
- Microsoft.NET.Test.Sdk 17.8.0 → 18.5.1 (1 major + 5 minors)
- xunit 2.5.3 → 2.9.3 (4 minors)
- xunit.runner.visualstudio 2.5.3 → 3.1.5 (major — required by Test.Sdk 18+)
- coverlet.collector 6.0.0 → 10.0.0 (4 majors — version is .NET-synced)
- Avalonia.Headless / Avalonia.Headless.XUnit — see App row

### tools/VpnRouterTestMcp (net8.0-windows)
- System.Drawing.Common 8.0.0 → 10.0.8 (2 majors, Win-only test tooling)

### VPNRouter.Tools/PoolAggregator
- No direct PackageReferences (only ProjectReference to Core).

### VPNRouter.UI
- No `.csproj`. Source-linked into `VPNRouter.Android.csproj` via
  `<Compile Include="..\VPNRouter.UI\**\*.cs">`. No packages to audit.

## .NET runtime track

- Currently on **.NET 8 (LTS, support until Nov 2026)**.
- **.NET 9 (STS, ends May 2026)** — skip; STS is end-of-life before .NET 10 LTS.
- **.NET 10 (LTS, releases Nov 2026)** — recommended target for v3.x.

Many of the "2 majors behind" Microsoft.* packages reflect that the
`Microsoft.Extensions.*` and `System.*` lines version-stamp with the .NET
runtime (8.x → 9.x → 10.x). Pinning the explicit 8.0.0 doesn't gain us
anything because the SDK picks the correct runtime version anyway —
**recommend removing the explicit `System.Management 8.0.0` and
`Microsoft.Win32.SystemEvents 8.0.0` pins** in `VPNRouter.Core.csproj`
and letting the SDK manage them transitively (or at minimum, bump the
Microsoft.Extensions.Hosting set to match the chosen .NET runtime).

## Recommendations for v3.0 refactor (prioritized)

### P0 — fix before next stable cut (low risk, easy)

1. **Align CommunityToolkit.Mvvm to 8.4.2 in App + Android** — close the
   only real inter-project drift.
2. **Bump Avalonia.Diagnostics to 11.3.15** in App — patches only,
   Debug-only dependency.
3. **Bump Newtonsoft.Json to 13.0.4** everywhere — patch only.
4. **Bump TraceEvent to 3.2.2 in Core** — patch fix.
5. **Bump ZXing.Net to 0.16.11 in Android** — patch fix.

### P1 — coordinated bump in v3.0 (medium risk)

6. **Serilog 3 → 4 sweep** (Core / CLI / Service / Android + all sinks +
   Serilog.Extensions.* to 10.0.0). Single PR. Regression-test logging.
7. **YamlDotNet 15 → 17** (Core, Android) — verify YAML round-trip on
   `config.yaml` migration paths (we have `SettingsMigrator`).
8. **Spectre.Console 0.49 → 0.55** (CLI) — verify Spectre CLI command
   bindings still resolve.
9. **Xamarin.AndroidX.Core 1.13 → 1.18** (Android) — required for
   future Android SDK 35+ targets.
10. **Test stack alignment**: Test.Sdk 17.8 → 18.5.1, xunit 2.5.3 →
    2.9.3, runner 2.5.3 → 3.1.5, coverlet 6 → 10. One PR. Re-run all
    headless / visual-diff tests.

### P2 — v3.x dedicated milestones (high risk / high value)

11. **Avalonia 11 → 12 migration** (App + Android + Headless + Tests).
    Couple SkiaSharp 2.88 → 3.119. Re-baseline all `VisualDiffTests`.
    Plan as own roadmap document.
12. **Newtonsoft.Json → System.Text.Json** sweep — phased migration over
    multiple releases. Start with new-code-only policy; touch existing
    sites only when a serializer-related bug is being fixed in that area.

### P3 — defer

13. **xunit 2 → 3 + Test.Sdk 18 → 19** — only when v3 brings a feature we
    need.
14. **Remove explicit System.Management / Microsoft.Win32.SystemEvents
    pins** — net8 SDK already pulls them; pinning is a no-op that just
    looks stale on every audit.

## Acknowledgements / methodology

- nuget.org `v3-flatcontainer/<pkg>/index.json` — enumerate versions.
- nuget.org `v3/registration5-gz-semver2/<pkg>/index.json` — vulnerability
  + deprecation flags. `severity:1=Low / 2=Moderate / 3=High / 4=Critical`.
- All queries successful; **no rate-limit / API errors encountered**.
- Audit script: 31 packages queried in ~30 s using PowerShell
  `Invoke-RestMethod`. Re-runnable for future audits.
