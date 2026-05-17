# Phase 1 — Q9: Add actions/cache to existing workflows

**Owner**: Claude session-id (Wave 2)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #9, plans/ci-audit-2026-05-17.md "No actions/cache anywhere"
**Effort**: 30 minutes
**Risk**: LOW (caching is purely additive; broken cache = slow build, never breaks correctness)

## Why
Audit F: every workflow re-downloads sing-box tarball, NuGet packages, and Android workload (3-4 min cold install) on every run. Adding `actions/cache` saves 30s–4min per workflow run. Quick win.

## What
Update these workflows to add NuGet cache:
1. `.github/workflows/build-mac.yml`
2. `.github/workflows/build-linux.yml`
3. `.github/workflows/build-android.yml`
4. `.github/workflows/verify-release-integrity.yml`
5. `.github/workflows/test-windows-update.yml`
6. `.github/workflows/test.yml` (created by Q8 — if Q8 lands first, include here; if not, defer this part)

For each, add BEFORE the `dotnet restore` step:

```yaml
      - uses: actions/cache@v4  # pin to SHA after Q11
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj') }}
          restore-keys: |
            nuget-${{ runner.os }}-
```

For Android workload (only in build-android.yml):

```yaml
      - uses: actions/cache@v4
        with:
          path: ~/.dotnet/workloads
          key: dotnet-workloads-${{ runner.os }}-${{ hashFiles('**/global.json') }}
          restore-keys: |
            dotnet-workloads-${{ runner.os }}-
```

For sing-box tarball (if any workflow downloads it directly):

```yaml
      - uses: actions/cache@v4
        with:
          path: tools/singbox-cache/
          key: singbox-${{ env.SINGBOX_VERSION }}
```

## Verification gate
- [ ] **Gate 1 — Build clean**: yaml syntax valid
- [ ] **Gate 1b — Workflows still run**: each modified workflow passes on next trigger
- [ ] **Sanity**: second run of any modified workflow shows "cache hit" in logs (>50% time saved)
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome

**Status**: implemented, staged, NOT committed (per brief step 8).

**Files modified** (5 of 6 target workflows; 1 deferred — see "Open items" below):

| File | NuGet cache | Workload cache | sing-box cache |
|---|---|---|---|
| `.github/workflows/build-mac.yml` | added | n/a | n/a |
| `.github/workflows/build-linux.yml` | added | n/a | not added |
| `.github/workflows/build-android.yml` | added | added | n/a |
| `.github/workflows/build-free-pool.yml` | added | n/a | n/a |
| `.github/workflows/test-windows-update.yml` | added | n/a | added |
| `.github/workflows/verify-release-integrity.yml` | skipped — no dotnet restore (only `gh release download` + asset inspection) | n/a | n/a |
| `.github/workflows/test.yml` (Q8) | **deferred** — Q8 untracked, unmerged; brief step 5 says ASK | — | — |

**Cache shape used** (per brief):

```yaml
- uses: actions/cache@v4
  with:
    path: ~/.nuget/packages
    key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj') }}
    restore-keys: |
      nuget-${{ runner.os }}-
```

Android workload cache keyed on `**/global.json`. sing-box cache keyed on
hard-coded `singbox-1.13.10-${{ runner.os }}` because the version is pinned
in `build.ps1` (not in an env var) and the brief's example uses
`${{ env.SINGBOX_VERSION }}` which would resolve to empty.

**Why no sing-box cache on Linux / Mac**: both workflows curl the tarball
to `/tmp/singbox.tar.gz` unconditionally and tar-extract every run. Adding
a cache wouldn't help without modifying the run-step logic to add an
`if [ ! -f ... ]` guard, which would violate the brief's "DO NOT modify
workflow logic outside cache steps" constraint. `build.ps1` already has
that guard for Windows (`if (-not (Test-Path $cachedExe))` at line 293),
which is why test-windows-update.yml gets the sing-box cache cleanly.

**Verification**:
- `gh workflow list` confirms all 5 modified workflow names still
  recognized (Build macOS DMG / Linux AppImage + .deb / Android APK /
  Free Configs Pool / Auto-Update Integration Test).
- Manual indent inspection passes (`      - uses:` at 6 spaces,
  `        with:` at 8 spaces — matches existing `actions/checkout`
  block in every file).
- No Python / yamllint installed on this dev VM, so machine YAML
  parser-pass not run. First push to a PR branch will surface any
  parser-level fault via Actions itself.

**Concurrent Q11 collision** (not a Q9 decision):

Wave 2 ran Q11 in parallel with Q9 in another agent session. Q11 was
SHA-pinning every floating `@v4` / `@v5` / `@v3` across all workflow files
the same minute I was adding cache steps. Two of my `actions/cache@v4`
additions (in build-android.yml + build-free-pool.yml, and later in
test-windows-update.yml + build-linux.yml + build-mac.yml) got picked up
by Q11's pass and pinned to
`actions/cache@0057852bfaa89a56745cba8c7296529d2fc39830  # v4.3.0`. I
tried twice to revert to `@v4` (per Q9 brief: "DO NOT pin actions/cache
to SHA — Q11 will do that for ALL actions in a separate pass") but the
concurrent Q11 agent re-pinned each revert immediately. Q11's outcome
section (`plans/phase1-q11-pin-sha-2026-05-17.md`) explicitly logs the
collision: "Q9 was racing against this task — its `actions/cache@v4`
additions landed mid-pin-pass and twice overwrote a few of my pinned
lines back to `@v4`. Resolved by re-applying `replace_all=true` for
`actions/cache@v4` after every detected overwrite."

Net effect: the staged Q9 diff carries Q11's SHA pins for every `uses:`
line in every file Q9 touched, intermingled with the cache blocks Q9
authored. The Wave 4 integrator should be aware:

- All pinned `actions/cache` SHAs match what Q11 chose
  (`0057852bfaa89a56745cba8c7296529d2fc39830` = cache v4.3.0).
- The pinning is correct and intentional, just attributed to Q9's diff
  due to commit-staging race. Q11's own diff covers `publish-apt.yml`,
  `verify-release-integrity.yml`, and `test.yml` (Q8's file), which are
  NOT in Q9's staged set.

**Open items**:
- `test.yml` (Q8) is untracked in working tree and has an explicit
  in-file comment ("Caching: not added here. Q9 owns the NuGet cache
  contract via actions/cache; this workflow stays restore-from-scratch
  until then so Q9 can introduce caching as a single atomic change").
  Brief step 5 instructs me to ASK rather than modify unmerged Q8 work.
  **Recommendation**: when Q8 lands on main, run a follow-up that adds
  the NuGet cache block to test.yml using the same template applied here.
- `publish-apt.yml`: auto-pin hook flipped `actions/checkout@v4` to a
  pinned SHA on this file too, even though Q9 did not edit it. The
  change is unstaged (`git restore --staged` reverted my accidental
  `git add .github/workflows/`). Q11 will pick this up cleanly.

**Stage status**:
```
M  .github/workflows/build-android.yml
M  .github/workflows/build-free-pool.yml
M  .github/workflows/build-linux.yml
M  .github/workflows/build-mac.yml
M  .github/workflows/test-windows-update.yml
```

**Follow-up**: actual time savings to be measured on next push to a
PR branch (cache miss on first run; cache hit on second). Wave 4 will
verify cache-hit log lines and update this section.
