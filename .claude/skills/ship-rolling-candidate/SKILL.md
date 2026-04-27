---
name: ship-rolling-candidate
description: Ship a rolling release candidate vX.Y.Z-rN. Bumps AppVersion, builds Windows + triggers Mac/Linux CI, applies notes, marks prerelease, deletes previous candidate per rolling-rN policy.
when: Code change closed (plan item done, build/tests green) — ship autonomously без ожидания команды. Triggered automatically после fix-cycle. User intervenes только если direction wrong ("стоп", "hold", "откати").
---

# Ship a rolling release candidate

VPNRouter ships iterations as `vX.Y.Z-r1`, `vX.Y.Z-r2`, etc. Only one
prerelease visible at a time. Stable cut autonomous когда -rN passes
verification gate (см. `cut-stable` skill).

## Pre-flight checks

Before bumping anything:
1. `git status` — clean tree.
2. `dotnet build VPNRouter.sln -c Release` → 0 errors.
3. Run regression tests:
   ```bash
   dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build \
     --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests"
   ```
   Expected: all pass.

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

Когда оба done → проверить asset count = **12** (4 Win + 2 Mac + 6 Linux):
```bash
gh release view vX.Y.Z-rN --repo PavelLizunov/VPNRouter --json assets --jq '.assets | length'
```

## Step 9 — report to user (notification only, не блокирующее)

Кратко:
- ✅ tag, prerelease=true, Latest=PREV, 12 assets
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
  # 1. Скачать все 12 assets локально
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
