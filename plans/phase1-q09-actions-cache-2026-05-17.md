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
*(filled by agent after impl)*

**Follow-up**: actual time savings measured on second run, documented in Outcome.
