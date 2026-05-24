# Android CI unblock — research + remediation plan (2026-05-25)

## TL;DR

The "wgturn-core" framing in the night-shift brief is outdated. The blocker
on `build-android.yml` since Wave 32 (2026-05-19) is **NuGet restore failure
`NU1102` on a missing Linux Mono runtime pack** — not the wgturn-core
private repo, not libbox.aar. libbox.aar is already provisioned from a
tooling release inside the same repo and works (see `gh release view
tooling-libbox-singbox-1.13.10` — present, prerelease, 11.7 MB asset).
Recommended path: **swap `actions/setup-dotnet@10.0.x` to a pinned
`10.0.100` SDK** (last pre-workload-manifest-bump build); estimated 2-3 h
hands-on plus one CI cycle to confirm. Falls back to vendored workload
manifest if pinning fails.

## 1. Current state

### 1.1 Workflow trigger and gate

`.github/workflows/build-android.yml:54`

```yaml
if: github.event_name == 'workflow_dispatch'
```

Tag pushes (`v*`) match the workflow's `on: push: tags: 'v*'` filter but
the job is gated to manual dispatch only, so every recent tag-push run
records as `conclusion: skipped` (verified — 5 most recent runs all
"skipped" per `gh run list --repo PavelLizunov/VPNRouter --workflow
"Build Android APK" --limit 5`).

### 1.2 Last actually-executed run

Run ID `26212361352` (workflow_dispatch on 2026-05-21 07:37 UTC) =
**failure**. The block-comment at `.github/workflows/build-android.yml:34-54`
explains the gate exists because tag-push runs were turning the commit
status red with no path to green; the workflow stays in the tree only so
NU1102 can be probed manually.

### 1.3 Exact failure

From `gh run view 26212361352 --log-failed`:

```
/home/runner/work/VPNRouter/VPNRouter/VPNRouter.Android/VPNRouter.Android.csproj
  : error NU1102: Unable to find package
    Microsoft.NETCore.App.Runtime.Mono.linux-x64
    with version (= 10.0.8)
  - Found 142 version(s) in nuget.org [ Nearest version: 9.0.0-preview.7.24405.7 ]
  - Found 142 version(s) in dotnet-public  [ Nearest version: 9.0.0-preview.7.24405.7 ]
  - Found  1  version(s) in dotnet-experimental [ Nearest version: 5.0.0-rc.1.20372.2 ]
```

Failure phase: NuGet restore inside `dotnet publish …android-arm64`
(workflow step "dotnet publish (***-arm64, signed)").

### 1.4 What works (do not regress)

- libbox.aar fetch (`build-android.yml:229-259`) from
  `tooling-libbox-singbox-1.13.10` — asset live, prerelease, 11.7 MB,
  sha256 `239c4101…fd69f1a6`.
- Keystore signing (`build-android.yml:163-178`) — both
  `ANDROID_KEYSTORE_BASE64` + `ANDROID_KEYSTORE_PASSWORD` set; step ok.
- Android SDK + workload install (`build-android.yml:108-133`) — API 36
  platform + build-tools 36.0.0 + `dotnet workload install android` all
  succeed.

## 2. Root cause chain

1. `actions/setup-dotnet@v4` with `dotnet-version: 10.0.x`
   (`build-android.yml:67-71`) installs the **latest** .NET 10 SDK
   build available. On 2026-05-21 that was `10.0.300` (".NET 10.0.8")
   — verified live (`api.github.com/repos/dotnet/sdk/releases`).
2. SDK 10.0.300's bundled Android workload manifest pins runtime
   pack version `10.0.8` for `Microsoft.NETCore.App.Runtime.Mono.linux-x64`.
3. nuget.org has versions `[5.0.0 … 8.0.27]` and `9.0.0-preview.*` for
   that exact package id — last public publish was 8.0.27 in late 2024.
   **No 10.0.x version exists on nuget.org for the Linux x64 variant.**
4. Restore fails NU1102 because none of the configured feeds
   (`nuget.org`, `dotnet-public`, `dotnet-experimental` per
   `VPNRouter.Android/NuGet.config:25-35`) carry the requested version.

### 2.1 Why this is surprising

`Microsoft.NETCore.App.Runtime.Mono.android-arm64 = 10.0.8` IS on
nuget.org (verified — versions list ends `10.0.7, 10.0.8`). The Linux
x64 Mono variant is gone because .NET 10 moved Linux to CoreCLR-only;
Mono on Linux x64 isn't a supported runtime any more. The workload
manifest still references the old pack as a metadata/host requirement
even though Android arm64 publishes don't need Mono on the runner.

### 2.2 Why the NuGet.config workaround didn't fix it

`VPNRouter.Android/NuGet.config:33-34` adds `dotnet-experimental` +
`dotnet-public`. Neither carries `Microsoft.NETCore.App.Runtime.Mono.linux-x64
10.0.x` — verified by probing both feeds' flat2 endpoint directly.
(`dotnet10` feed at
`pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/`
also returns "Can't find the package".) The pack was simply never
published anywhere public.

### 2.3 Chain summary

```
setup-dotnet @ 10.0.x  → SDK 10.0.300  → workload manifest pins
Mono.linux-x64 = 10.0.8  → pack absent on every public feed  → NU1102.
```

The brief's "wgturn-core" framing is stale. wgturn-core is a desktop-only
dependency (see `MEMORY.md` "wgturn-cli bundled in installer (Phase 1)")
not referenced by `VPNRouter.Android/VPNRouter.Android.csproj` at all
(grep-verified). Phase 7 Wave 32 resolved the libbox.aar provisioning
via tooling-release pattern (`.github/SECRETS.md:21-69`).

## 3. Remediation options

### Option A — pin to SDK 10.0.100 (or earlier .NET 10 GA)

Bump `.github/workflows/build-android.yml:70` from `10.0.x` to a fixed
patch that predates the workload manifest's broken pin.

```yaml
- name: Setup .NET 8 + 10 SDK
  uses: actions/setup-dotnet@v4.3.1
  with:
    dotnet-version: |
      8.0.x
      10.0.100   # .NET 10.0.0 GA — pre-10.0.8 workload bump
```

| Pros | Cons |
|---|---|
| Single-line edit | Forces freeze until upstream fixes their manifest |
| No NuGet hacks | Periodic re-test as new patch versions come out |
| Reproducible across runs | If 10.0.100 manifest had its own bug, fallback needed |
| Predates the broken `Mono.linux-x64 = 10.0.8` pin | |

**Effort: 1 h** (one workflow edit + one manual dispatch CI run).

### Option B — vendor / patch the workload manifest

Write `WorkloadManifestPatches.targets` that rebinds `Mono.linux-x64
10.0.8 → 9.0.0-preview.7.24405.7` before publish.

Pros: keeps latest SDK. Cons: fragile (unsupported API), breaks on
manifest schema bumps, hard to test locally.

**Effort: 6-8 h.** Fallback if Option A fails.

### Option C — bypass NuGet for the missing pack

Pre-download closest `Mono.linux-x64` (9.0.0-preview), point a local
NuGet source at it.

Pros: works without SDK change. Cons: cross-version pack is undefined
behaviour, silent APK breakage possible.

**Effort: 4-6 h.** Not recommended.

### Option D — downgrade to .NET 9 Android workload

Revert `VPNRouter.Android/VPNRouter.Android.csproj:19` from
`net10.0-android36.0` to `net9.0-android35.0`. Undoes Phase 5 Wave 23.

Pros: stable. Cons: loses Avalonia 12 pin, Phase 5 work wasted,
diverges from desktop.

**Effort: 8-12 h.** Strongly discouraged.

### Option E — `dotnet-install.sh` direct

Bypass `setup-dotnet`, invoke install script manually. Same effect as
Option A but loses cache.

**Effort: 2 h.** Worse than A.

## 4. Recommended path

**Option A — pin SDK 10.0.100**, with Option B as a documented fallback
if 10.0.100 also turns out to reference the missing pack.

### 4.1 Why

- Smallest blast radius — one workflow line.
- Predates the broken manifest pin (released ~late April 2026 per
  `dotnet/android` releases timeline: tag `36.1.53` is "Servicing", so
  `36.1.43` or `36.1.30` likely shipped with the pre-bump manifest, and
  those correspond to SDK 10.0.100 / 10.0.103 / 10.0.201).
- Re-evaluation path is obvious — bump back to `10.0.x` after each
  monthly SDK release and probe.
- Matches the existing "Wave 32b" deferred-cleanup philosophy in
  `MEMORY.md` rather than introducing a brand-new vendoring mechanism.

### 4.2 Estimated effort

- Edit workflow YAML: 5 min
- Push branch + dispatch CI: 5 min
- Watch run (full Android build is 8-15 min cold per cache rules at
  `build-android.yml:77-93`): 15 min
- If green, also remove the `if: workflow_dispatch` gate at line 54 so
  tag pushes resume: 5 min
- If red, drop to Option B fallback investigation: +4-6 h
- Document the pin + version policy in `.github/workflows/CLAUDE.md` +
  `.github/SECRETS.md` (`.github/SECRETS.md:118-141` "Rotation" section
  is the natural neighbour): 30 min

**Total: 2-3 h** if Option A works on first try.

### 4.3 Why not the obvious "wait for .NET 10 GA + auto-fix"

.NET 10 IS GA (verified — `v10.0.300` / .NET 10.0.8 marked
`prerelease: false`). The `Mono.linux-x64` pack was not just delayed-
publish, it's been **withdrawn** from the .NET 10 publish set (Mono on
Linux x64 isn't a target any more). Waiting won't fix it.

## 5. Concrete next-step actions

### Step 1 — Probe-only run (no commits)

Manually dispatch with current code to re-confirm NU1102 still reproduces:

```bash
gh workflow run "Build Android APK" --repo PavelLizunov/VPNRouter \
  --field version=2.37.0-r3 --field upload_to_release=false
```

If green — remove the `if:` gate, done.

### Step 2 — Apply Option A pin

File `.github/workflows/build-android.yml:67-71`. Replace `10.0.x` with
`10.0.100  # Pinned pre-Mono.linux-x64-10.0.8 manifest; see plans/research-android-ci-unblock-2026-05-25.md`.

### Step 3 — Manual dispatch

Same `gh workflow run` command. Expected fallback: probe SDK versions
`10.0.100, 10.0.103, 10.0.201` until manifest's runtime pack ref
resolves on nuget.org. Probe via:

```bash
curl -s "https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.mono.android-arm64/index.json" \
  | tr ',' '\n' | grep -v preview
```

### Step 4 — Once green, lift the `if:` gate

File `.github/workflows/build-android.yml:54`. Delete the
`if: github.event_name == 'workflow_dispatch'` line and the 20-line
block comment at lines 34-54. Add fresh 5-line comment about the SDK
pin strategy.

### Step 5 — Verify tag-push trigger

Next `-rN` ship should fire the Android workflow automatically. Expected
~14 min to APK + release upload.

### Step 6 — Documentation updates

- `.github/workflows/CLAUDE.md` — replace Wave 32b paragraph.
- `MEMORY.md` Wave 32b entry — mark closed, link this plan.
- `.github/SECRETS.md` — add "SDK pin" section near rotation procedure.

### Step 7 — Backfill APKs (optional)

For each releases lacking an APK (`v2.35.3`, `v2.36.0`, intermediate
`-rN`), dispatch the workflow manually per-version. ~2 min each.

## 6. Risk register

- **10.0.100 manifest has same broken pin** (Med likelihood / High impact)
  — probe nuget.org availability per Step 3 fallback before final pin.
- **Pinned SDK lacks Android 36 support** (Low / High) — verified
  `Microsoft.Android.Sdk.Windows = 36.1.53` is on nuget.org (stable).
- **Future SDK bump silently breaks pin** (Low / Med) — quarterly
  re-evaluation cadence in `.github/SECRETS.md`.
- **Tag-push flood after `if:` removal** (Low / Low) — skip-on-no-keystore
  already at `build-android.yml:163-178`.

## 7. References

- `.github/workflows/build-android.yml` — the workflow
- `.github/workflows/CLAUDE.md` — workflow ownership notes
- `.github/SECRETS.md:21-69` — libbox.aar tooling-release pattern (works)
- `.github/SECRETS.md:171-182` — keystore secret pair (works)
- `VPNRouter.Android/VPNRouter.Android.csproj:19` — net10 + android36 pin
- `VPNRouter.Android/NuGet.config:25-35` — r14 NuGet attempt (insufficient)
- `tools/build-libbox-aar.sh` — local libbox.aar build (no CI change)
- `plans/phase6-ci-android-yml-2026-05-18.md` — Phase 6 original CI plan
- `plans/hotfix-r14-android-nu1102-2026-05-20.md` — superseded by this plan
- `tools/wgturn-cli-cache/wgturn-core/` — desktop-only, unrelated to Android

## 8. Open questions

1. Should workflow also build `android-arm` + `android-x64`? Currently
   arm64 only at `build-android.yml:284`. Follow-up, not urgent.
2. Once auto-runs, should `verify-release-integrity.yml` expect 2 more
   assets (apk + sha256)?
3. Spin up a private NuGet feed hosting a rebuilt `Mono.linux-x64` shim?
   Probably overkill for one missing pack.
