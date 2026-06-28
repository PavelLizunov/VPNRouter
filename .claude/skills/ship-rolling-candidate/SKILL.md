---
name: ship-rolling-candidate
description: Ship a rolling release candidate vX.Y.Z-rN. Bumps AppVersion, builds Windows + triggers Mac/Linux CI, applies notes, marks prerelease, deletes previous candidate per rolling-rN policy.
when: Code change closed (plan item done, build/tests green) — ship autonomously без ожидания команды. Triggered automatically после fix-cycle. User intervenes только если direction wrong ("стоп", "hold", "откати").
---

# Ship a rolling release candidate

VPNRouter ships iterations as `vX.Y.Z-r1`, `vX.Y.Z-r2`, etc. Only one
prerelease visible at a time. Stable cut **НЕ autonomous** — по явной команде
user после verification gate (см. `cut-stable` skill, rule #6).

## HARD PRECONDITION (added 2026-05-25 after r7..r18 red-CI streak)

**Before doing anything else, verify previous commit's CI is GREEN:**

```bash
powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1
```

Exit 0 → safe to ship. Exit 1/2/3 → STOP, do not bump AppVersion, do
not start the cycle. Investigate the red/in-progress check first.

**Why this rule exists**: night-shift 2026-05-25 shipped r7→r18 (12
candidates) in a row. Tag-level CI on each `vX.Y.Z-rN` tag was green
because Windows binaries built fine. But commit-level CI on every
`push` event was RED throughout — Linux/macOS Avalonia XAML
compilation failed on `LblZapretCacheStatus` binding (introduced in r10
inside `#if PLATFORM_WINDOWS` but referenced by cross-platform XAML).
The bug propagated through 8 follow-up commits because nobody checked
the commits page on main between ships. User caught it via screenshot
of red-X column with 2/6 or 4/6 passing.

The script enforces this check so the AI can't silently skip it. Even
if you "remember to check" 95% of the time, the 5% miss is what
caused the r7→r18 debt. Make the verification a hard gate, not a
mental TODO.

## HARD PRECONDITION 2 — independent review-agent over the diff (added 2026-06-25, audit P0 item 3)

METHODOLOGY §5/§7.1: an independent review-agent is the FIRST blocking layer —
a green build/test gate is NOT "done". This ship path had NO review (self-review
only, §18 self-preference bias), which is exactly how the auto-failover P0
reached stable. Before bumping AppVersion:

```bash
git diff --name-only HEAD~1..HEAD   # (or the staged diff if not yet committed)
git diff HEAD~1..HEAD
```

Spawn an INDEPENDENT reviewer with `docs/REVIEW_AGENT_PROMPT.md` — paste the FULL
diff + invariants verbatim (brief like a new colleague, never "as discussed"):

```
Agent(subagent_type: "general-purpose",
      prompt: <docs/REVIEW_AGENT_PROMPT.md block with the diff pasted in>)
```

- `critical` / `important` → FIX before shipping, then re-review.
- `minor` → opt-in. Real-but-deferred survivors → append to `plans/OPEN-DEFECTS.md`.

**Hotfix short-circuit** (§5): skip ONLY for a true ≤5-line, single-surface
change with no contract/behaviour drift — and say so in the ship report.

## Pre-flight checks

Before bumping anything:
1. `git status` — clean tree.
2. `dotnet build VPNRouter.sln -c Release` → 0 errors.
3. Run regression tests:
   ```bash
   dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build \
     --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"
   ```
   Expected: all pass.
4. **If MainWindowViewModelCharacterizationTests failed with Windows hash drift**:
   bump `PinnedHashWindows` in `VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs` BEFORE Step 2 commit. The actual hash appears in the test output as `Actual: <hex>`.
5. **Visual-diff gate (Windows-only — CI can't run this).** `VisualDiffTests`
   has an `OperatingSystem.IsWindows()` skip guard, so the Linux/macOS CI
   runners never execute the pixel-diff layer. Baseline drift therefore goes
   INVISIBLE in CI — it slipped from v2.37 → v2.38 (DpiBypass/Telegram/Tools
   redesign) and was only caught by a manual run on 2026-06-02. This dev-VM
   pre-flight is the only place the diff actually runs, so it must run here:
   ```bash
   dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build \
     --filter "FullyQualifiedName~VisualDiffTests"
   ```
   Expected: 3/3 pass (DpiBypass / Telegram / Tools). Run it as its OWN
   command (not folded into the step-3 filter) — it's `[AvaloniaFact]` and the
   dispatcher-thread shutdown is cleaner when isolated.
   - **Green** → proceed.
   - **Red AND this ship intentionally restyled those pages** → the baseline is
     stale, not the code. Refresh per `VPNRouter.Tests/CLAUDE.md` "Refresh
     workflow когда страница интенционально меняется": run `PageScreenshotTests`,
     **EYEBALL** the 3 fresh PNGs in `VPNRouter.Tests/screenshots/` (confirm no
     clipped controls / overflow), `copy` each over
     `VPNRouter.Tests/screenshots/baseline/`, re-run this gate until green, and
     stage the refreshed baseline PNGs in THIS ship's Step-2 commit.
   - **Red AND you did NOT touch those pages** → real visual regression
     (control removed, theme inverted, layout shifted >~2 px). STOP and fix the
     code before shipping; do NOT re-pin to hide it.

## Post-push verification (MANDATORY — was the v2.36.0-r6→r9 lesson)

After Step 2 push lands the commit, **the main-branch `dotnet test` job runs
on Linux/macOS** with a DIFFERENT pinned hash (`PinnedHashLinux`). The
Windows-side bump in pre-flight does NOT cover Linux because `MainWindowViewModel`
has `#if PLATFORM_WINDOWS` blocks that change the conditional-stripped surface.

**The recurring failure**: ship rN → Windows hash bumped locally → commit pushed →
GitHub Releases page shows `vX.Y.Z-rN` with all assets green → declare success →
but the commits page on main shows a RED X because Linux test failed with
hash-drift, and that debt accumulates over r5→r6→r7→r8→r9 (real history).

**Fix workflow** — after `git push`:

```bash
# 1. Wait for the dotnet test workflow on the new commit
gh run watch $(gh run list --repo PavelLizunov/VPNRouter --workflow "dotnet test" --limit 1 --json databaseId --jq '.[0].databaseId') --repo PavelLizunov/VPNRouter --exit-status

# 2. If it fails with Linux hash drift, capture the Actual hash:
gh run view <RUN_ID> --repo PavelLizunov/VPNRouter --log-failed 2>&1 | grep "Actual:"

# 3. Bump PinnedHashLinux to that value, commit, push as a follow-up "chore(tests)"
#    commit. THIS COMMIT MUST LAND BEFORE you finalize the release as success.

# 4. Re-verify after the follow-up push:
gh api repos/PavelLizunov/VPNRouter/commits/HEAD/check-runs --jq '.check_runs[] | "\(.conclusion // .status)  \(.name)"' | sort -u
```

This is non-negotiable. The visible state on the commits page is what the user
sees. Green tag assets + red commit-level test = bad ship UX even if functional.

A clean ship has BOTH:
- Tag release: 14 desktop assets (16 if Android is attached), prerelease=true, previous-stable Latest restored.
- Latest main commit: all check-runs green (build, grep, publish, test, test-update, verify).

## Step 1 — bump AppVersion

`VPNRouter.Core/AppVersion.cs`:
```csharp
public const string Version = "X.Y.Z-rN";   // CRITICAL: must include -rN suffix
```
**Suffix MUST match release tag exactly.** v2.25.0-r1→r2 lesson:
бамп без суффикса → update-check возвращает null. См. `CLAUDE.local.md` секция
"Урок от v2.25.0-r1 → r2".

## Step 2 — commit + push to BOTH remotes

```bash
git add <changed files>
git commit -m "feat(vX.Y.Z-rN): <one-line summary>

<details>

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"

git push github HEAD:main
git push origin HEAD:main
```

Если **Forgejo (origin) недоступен** (AmneziaWG VPN флапает) — push в `github` обязательно, retry origin позже.

## Step 3 — Windows build + create release (in background)

```bash
powershell -ExecutionPolicy Bypass -File "C:\Project\VPNRouter\build.ps1" -Version "X.Y.Z-rN" -Upload
```

Это:
- Создаёт тэг `vX.Y.Z-rN` на текущем commit
- Создаёт GitHub release через `gh release create --latest` (build.ps1 ставит `--latest` autoматически — мы потом снимем)
- Уплоадит `VPNRouter-vX.Y.Z-rN-win.zip` + update + 2× sha256

**Запускать в background** через `run_in_background: true` — займёт ~90 секунд.

### sing-box-lx core (AmneziaWG / XHTTP кандидаты — иначе пропусти)

Если кандидат несёт **lx-ядро** (что-либо про `awg://` / AmneziaWG или VLESS
`type=xhttp`) — обычный `build.ps1` выше НЕ подходит: он бандлит upstream
sing-box (AWG/XHTTP выключены fail-closed). Вместо этого:

1. Сначала собрать пропатченный lx-бинарь:
   ```bash
   powershell -ExecutionPolicy Bypass -File tools/build-singbox-lx.ps1
   ```
   Клонит запиненные `Leadaxe/sing-box-lx` + `wireguard-go-awg2-lx`, накладывает
   3 build-time патча (Go-1.26 WSAEFAULT send+recv golang/go#77875 + гейт WARP
   reserved-byte H4 на receive), проверяет Tags `with_awg`/`with_xhttp` + runtime
   handshake-smoke, пишет `publish/sing-box-lx.exe`. Fail-closed: если исходник
   форка сдвинулся — сборка падает (перепроверить патчи).
2. Забандлить через `-SingBoxPath`:
   ```bash
   powershell -File build.ps1 -Version "X.Y.Z-rN" -SingBoxPath "publish/sing-box-lx.exe" -Upload
   ```

lx только в Windows desktop; Mac/Linux/Android — upstream sing-box (AWG/XHTTP
off). AmneziaWG-на-Windows полностью починен в v2.45.0-r6 (verified на
windows-brat). Полный контекст + когда выпиливать патчи: memory
`awg-windows-lx-patches.md` + `plans/OPEN-DEFECTS.md`.

## Step 4 — пока Windows билдится, write release notes

Создать `plans/release-notes-vX.Y.Z-rN.md`:
- Summary: что фикс'нуто (если -r2/r3, mention previous fixes too)
- Test flow для user'а
- Links к commits
- **Никаких emoji в файле**

## Step 5 — Mac + Linux CI auto-triggered

Tag push (от build.ps1) триггерит:
- `Build macOS DMG` workflow
- `Build Linux AppImage + .deb` workflow
- `Publish APT Repository` workflow

Race condition: если Mac CI завершилась ДО того как build.ps1 создал release →
"skipping upload" warning. Re-trigger вручную:
```bash
gh workflow run "Build macOS DMG" --repo PavelLizunov/VPNRouter --ref vX.Y.Z-rN
```

## Step 6 — finalize release (после Windows build done)

```bash
# Mark as prerelease + apply notes
gh release edit vX.Y.Z-rN --repo PavelLizunov/VPNRouter \
  --prerelease \
  --title "VPNRouter vX.Y.Z-rN — <one-line summary>" \
  --notes-file "plans/release-notes-vX.Y.Z-rN.md"

# Restore previous stable as Latest (build.ps1 took it)
gh release edit vX.Y.Z-PREV --repo PavelLizunov/VPNRouter --latest
```

## Step 7 — delete previous -rN per rolling policy

**Только один in-flight prerelease видим за раз.** Если был `-r1` и сейчас
шипим `-r2`:
```bash
gh release delete vX.Y.Z-r1 --yes --repo PavelLizunov/VPNRouter
```
Тэг **НЕ** удаляем — он остаётся в git history.

## Step 8 — wait for Mac + Linux CI

Pass `run_in_background: true`:
```bash
gh run watch <mac-run-id> --repo PavelLizunov/VPNRouter --exit-status
gh run watch <linux-run-id> --repo PavelLizunov/VPNRouter --exit-status
```

Когда оба done → проверить desktop asset count = **14** (4 Win + 4 Mac + 6 Linux):
```bash
gh release view vX.Y.Z-rN --repo PavelLizunov/VPNRouter --json assets --jq '.assets | length'
```

## Step 9 — report to user (notification only, не блокирующее)

Кратко:
- OK: tag, prerelease=true, Latest=PREV, 14 desktop assets
- Recovery shortcut + test flow checklist
- Указать "verification gate зелёная — следующее действие cut-stable когда нет regression reports за ~24h"

## Known gotchas

- **VPN флап во время push в origin** → попробовать `git push origin HEAD:main` после notification "VPN включил".
- **PS 5.1 NumericUpDown null bug** — если фикс касается UI input — обязательно `int?` + `?? fallback` (см. v2.28.3-r4).
- **Mac CI race** — если Mac success но 0 уплоада, re-trigger через workflow_dispatch.
- **Homebrew Cask** — на prerelease НЕ обновляется (correct behaviour). На stable cut — autobump через repository_dispatch.
- **GitHub REST `/releases` listing cache lag** — после `gh release create` + серии `gh release edit` (prerelease flag, notes, --latest restore) запись в DB корректна (`gh release view` работает, прямой URL открывается), но **public listing endpoint** (REST `/releases?per_page=N` — именно его дёргает in-app update check) может зависнуть на 30-60+ минут. Atom feed и HTML release page тоже лагают. **Verify after step 6**:
  ```bash
  gh api "repos/PavelLizunov/VPNRouter/releases?per_page=10" --jq '.[].tag_name' | grep "vX.Y.Z-rN"
  ```
  Если **не виден** через 5 минут после finalize → принудительно invalidate через delete+recreate (тег сохраняем):
  ```bash
  # 1. Скачать все 14 desktop assets локально
  mkdir /tmp/r-assets && cd /tmp/r-assets
  gh release download vX.Y.Z-rN --repo PavelLizunov/VPNRouter

  # 2. Delete release (tag preserved!)
  gh release delete vX.Y.Z-rN --repo PavelLizunov/VPNRouter --yes --cleanup-tag=false

  # 3. Recreate fresh (single create call, no follow-up edits)
  gh release create vX.Y.Z-rN /tmp/r-assets/* \
    --repo PavelLizunov/VPNRouter \
    --target main \
    --prerelease \
    --title "VPNRouter vX.Y.Z-rN — ..." \
    --notes-file plans/release-notes-vX.Y.Z-rN.md
  ```
  Свежая запись индексируется немедленно. См. v2.28.4-r1 incident 2026-04-27.

## NOT to do

- Force-push tag после shipping — поломаешь download links для уже скачавших.
- Skip suffix `-rN` в AppVersion — update-check сломается.
- Push с `--no-verify` или `--no-gpg-sign` без явной команды user'а.
