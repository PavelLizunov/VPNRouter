---
name: ship-rolling-candidate
description: Ship a rolling release candidate vX.Y.Z-rN. Bumps AppVersion, builds Windows + triggers Mac/Linux CI, applies notes, marks prerelease, deletes previous candidate per rolling-rN policy.
when: User finished a code change and says "ship" / "release" / "выпускай" / "выложи" / "бампай" or after their feedback on the previous -rN.
---

# Ship a rolling release candidate

VPNRouter ships iterations as `vX.Y.Z-r1`, `vX.Y.Z-r2`, etc. Only one
prerelease visible at a time. Stable cut only on user confirmation
(see `cut-stable` skill).

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

## Step 9 — report to user

Кратко:
- ✅ tag, prerelease=true, Latest=PREV, 12 assets
- Recovery shortcut + test flow checklist
- Спросить "если ОК → cut stable"

## Known gotchas

- **VPN флап во время push в origin** → попробовать `git push origin HEAD:main` после notification "VPN включил".
- **PS 5.1 NumericUpDown null bug** — если фикс касается UI input — обязательно `int?` + `?? fallback` (см. v2.28.3-r4).
- **Mac CI race** — если Mac success но 0 уплоада, re-trigger через workflow_dispatch.
- **Homebrew Cask** — на prerelease НЕ обновляется (correct behaviour). На stable cut — autobump через repository_dispatch.

## NOT to do

- Force-push tag после shipping — поломаешь download links для уже скачавших.
- Skip suffix `-rN` в AppVersion — update-check сломается.
- Promote -rN → Latest без user confirm.
- Push с `--no-verify` или `--no-gpg-sign` без явной команды user'а.
