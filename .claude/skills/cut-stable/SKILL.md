---
name: cut-stable
description: Promote a rolling -rN candidate to stable vX.Y.Z (no suffix). Bumps AppVersion, creates fresh tag without suffix, full rebuild + Mac/Linux CI, restores Latest, deletes -rN.
when: User confirms the latest -rN works ("работает", "OK, cut stable", "Делай stable"). NOT before user confirmation.
---

# Cut a stable release

Promotes `vX.Y.Z-rN` to stable `vX.Y.Z` (без суффикса). Per CLAUDE.local.md
"Build / push steps" — обязательно НОВЫЙ тэг без `-rN`, а не просто переброс
prerelease flag.

## Pre-flight

User должен явно подтвердить ("работает", "OK", "cut'ай"). НЕ инициировать
самостоятельно.

## Step 1 — bump AppVersion (drop -rN suffix)

`VPNRouter.Core/AppVersion.cs`:
```csharp
public const string Version = "X.Y.Z";   // no suffix
```

## Step 2 — commit + push

```bash
git add VPNRouter.Core/AppVersion.cs
git commit -m "release: cut vX.Y.Z stable (drop -rN suffix)

User confirmed <r-version> fixes work. Promoting per CLAUDE.local.md
§Release Process step 6.

No code changes since <last-rN-commit-hash>.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"

git push github HEAD:main
git push origin HEAD:main   # retry если VPN down
```

## Step 3 — Windows build (создаст НОВЫЙ tag vX.Y.Z + новый release)

```bash
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "X.Y.Z" -Upload
```

build.ps1 сделает:
- `gh release create vX.Y.Z --latest` (без `--prerelease`)
- Уплоад Windows ZIPs

**Tag `vX.Y.Z` создаётся build.ps1 на текущем commit.** Stable tag — finalный, force-update НЕЛЬЗЯ.

## Step 4 — fetch tag локально + push в Forgejo

```bash
git fetch github --tags         # fetches vX.Y.Z
git push origin vX.Y.Z          # mirror в Forgejo
```

## Step 5 — Mac + Linux CI (auto-triggered tag push)

Wait for both runs. Verify 12 assets:
```bash
gh release view vX.Y.Z --repo PavelLizunov/VPNRouter --json assets --jq '.assets | length'
```
Должно быть **12**.

## Step 6 — write proper stable notes

`plans/release-notes-vX.Y.Z.md` — собирает фиксы со всех `-r1..-rN`:
- Headline P0/P1 fixes
- 4 layers of defense / 9 tests / etc
- Test coverage stats
- Cross-refs к плану

```bash
gh release edit vX.Y.Z --repo PavelLizunov/VPNRouter \
  --title "VPNRouter vX.Y.Z — <one-line headline>" \
  --notes-file "plans/release-notes-vX.Y.Z.md"
```

## Step 7 — delete all -rN prereleases per rolling policy

```bash
gh release delete vX.Y.Z-r1 --yes --repo PavelLizunov/VPNRouter
gh release delete vX.Y.Z-r2 --yes --repo PavelLizunov/VPNRouter
# ... etc для всех -rN
```

**Тэги НЕ удаляем** — `vX.Y.Z-r1`, `vX.Y.Z-r2` остаются в git history.

## Step 8 — verify Homebrew Cask auto-bump

После tag push на stable, `build-mac.yml` Trigger Homebrew Cask step должен
дисптачить `repository_dispatch` к `PavelLizunov/homebrew-vpnrouter`. Tap'овский
`update-cask.yml` должен обновить `Casks/vpnrouter.rb` к новой версии:

```bash
gh api "repos/PavelLizunov/homebrew-vpnrouter/contents/Casks/vpnrouter.rb" \
  --jq '.content' | base64 -d | head -5
```

Должна быть `version "X.Y.Z"` + новый sha256.

Если не обновился — проверить `gh run list --repo PavelLizunov/homebrew-vpnrouter`
для последнего dispatch'а.

## Step 9 — verify APT repo

```bash
curl -sI "https://vpn.ninitux.com/apt/dists/stable/main/binary-amd64/Packages"
```
HTTP/1.1 200 OK ожидается. `publish-apt.yml` workflow должен был добавить новую
.deb в reprepro index.

## Step 10 — update MEMORY.md

В `~/.claude/projects/.../memory/MEMORY.md`:
- "Current stable: vX.Y.Z (DD-MM-YYYY — short summary)"
- "Previous stable: <bumped down>"
- "Next planned: <next roadmap version>"
- One-liner install commands если они изменились

## Known gotchas

- **build.ps1 создаёт tag НА ТЕКУЩЕМ commit** — убедись что commit это AppVersion bump (no suffix), иначе тэг укажет на не то.
- **--latest moves automatically** — когда build.ps1 делает `gh release create --latest`, GitHub сам забирает `--latest` у предыдущего release. **Не нужно** руками снимать.
- **Forgejo может быть недоступен** — git push origin retry'ить; github canonical для release process.
- **AppVersion mismatch с tag** — критическая ошибка. Тэг `v2.28.3` + AppVersion `2.28.3-r6` → SemVer считает stable новее prerelease same-core, но на коде r6 это враньё. Всегда совпадать.

## NOT to do

- Cut stable без user confirmation.
- Force-update stable tag (`git tag -f vX.Y.Z`) после публикации release.
- Skip Homebrew Cask verify — пользователи на macOS застревают на старом cask.
- Удалить тэг `vX.Y.Z-rN` из git — мы только release удаляем.
