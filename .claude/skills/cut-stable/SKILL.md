---
name: cut-stable
description: Promote a rolling -rN candidate to stable vX.Y.Z (no suffix). Bumps AppVersion, creates fresh tag without suffix, full rebuild + Mac/Linux CI, restores Latest, deletes -rN.
when: Latest -rN passed full verification gate (build + tests + Mac/Linux CI green + 12 assets + MCP-verify + **mandatory pre-cut live update gate**) AND user gave explicit "cut" / "ok" / "promote" command (rule 6 в `CLAUDE.md`, lessons v2.31.2 / v2.31.7). Cut НЕ autonomous.
---

# Cut a stable release

Promotes `vX.Y.Z-rN` to stable `vX.Y.Z` (без суффикса). Per CLAUDE.local.md
"Build / push steps" — обязательно НОВЫЙ тэг без `-rN`, а не просто переброс
prerelease flag.

## Pre-flight (verification gate, autonomous)

Все 6 чек-боксов должны быть зелёные:
1. `dotnet build -c Release` → 0 errors
2. Regression tests зелёные (`VlessServersResolverTests`, `ConfigGeneratorEmptyServersGuardTests`, `FreeConfigAggregatorPreserveTests`)
3. Mac CI на последнем -rN — `success`
4. Linux CI на последнем -rN — `success`
5. `gh release view vX.Y.Z-rN --json assets --jq '.assets|length'` → `12`
6. **Live update gate PASS** (см. секцию ниже) — обязательная mandatory.
7. (soft) No user-reported regressions за ~24h после shipping последнего -rN

Если все зелёные — ждём explicit user команду "cut" / "ok" / "promote" (rule 6
в `CLAUDE.md`). Cut НЕ autonomous. User паузит явной командой "hold stable".

## Mandatory pre-cut live update gate

**Why this exists**: green CI + green tests + MCP-verify of the change itself
do NOT cover the auto-update path. v2.31.2 cut'нулся на all-green и оказался
partial-fix (UI regression поймали только хотфиксом v2.31.3). v2.31.7 cut'нулся
на all-green и его helper.cmd parser bug сломал 100% upgrades — поймали через
~7 дней по user-reports. **Conclusion**: тот же binary который user скачает
со stable должен в чистой среде успешно auto-update'нуться к ТЕКУЩЕМУ кандидату
ПЕРЕД cut'ом. Иначе stable shipping = выпуск broken update path в production.

**When to run**: после verification gate (5 первых чекбоксов) PASS, перед тем
как просить user'а "cut" / "ok" / "promote". Если этот gate упал — НЕ просим
cut, а ship'аем -r(N+1) с фиксом и крутим cycle заново.

**Steps** (выполнять в том же VM/host где работаем над release; **не трогать
prod-инсталляцию VPNRouter**):

a) **Identify previous stable release tag**:
   ```bash
   gh release list --repo PavelLizunov/VPNRouter --exclude-pre-releases --limit 1
   ```
   Запомни tag (e.g. `v2.31.7`). Это baseline для теста.

b) **Download install ZIP в чистый temp dir**:
   ```bash
   rm -rf /c/Temp/stable-test && mkdir -p /c/Temp/stable-test
   gh release download <previous-stable-tag> --repo PavelLizunov/VPNRouter \
     --pattern 'VPNRouter-*-windows-x64.zip' --dir /c/Temp/stable-test
   ```

c) **Extract + launch + initial settle**:
   ```bash
   cd /c/Temp/stable-test
   powershell -Command "Expand-Archive -Path 'VPNRouter-*-windows-x64.zip' -DestinationPath ./extracted -Force"
   powershell -Command "Start-Process -FilePath './extracted/VPNRouter.App.exe'"
   ```
   Wait 30s для App init (settings load, update check spin-up).

d) **Trigger update к ТЕКУЩЕМУ кандидату -rN**. Два пути:
   - **UI path** (preferred — exercises full user flow): MCP click Settings →
     "Проверить обновления" / "Check for updates" → дождаться detection
     -rN кандидата (ensure `Experimental` channel is on, иначе -rN
     неvidible) → нажать "Установить" / "Install".
   - **Programmatic** (fallback если UI broken): дёрнуть update helper
     напрямую через CLI если такая команда есть. Если нет — UI path
     mandatory.

e) **Wait for update flow to complete**. Helper .cmd должен:
   1. Stop running App + Service (если установлен)
   2. xcopy new files over old install
   3. Relaunch App
   Ожидаемое время: 30-90s. Tail update.log:
   ```bash
   tail -f "$LOCALAPPDATA/VPNRouter/Logs/update.log"   # или %ProgramData%/VPNRouter/logs/update.log
   ```

f) **Verify new version installed cleanly**:
   ```bash
   powershell -Command "(Get-Item /c/Temp/stable-test/extracted/VPNRouter.App.exe).VersionInfo.ProductVersion"
   ```
   Должна быть `<candidate-rN-version>` (e.g. `2.31.8-r10`). Также:
   ```bash
   powershell -Command "(Get-Item /c/Temp/stable-test/extracted/VPNRouter.Core.dll).VersionInfo.FileVersion"
   ```
   AppVersion должна совпадать с candidate (правило #5 — string Version
   ВКЛЮЧАЯ -rN суффикс).

g) **30-second smoke**: убедись App работает после update.
   - Если есть test профиль (free configs / saved subscription) — connect
     → status «Подключено» → disconnect.
   - Если нет — минимум: главное окно открывается, нет crash dialog'а,
     status показывает «Готов» / «Ready», нет красных error toast'ов.
   MCP screenshot для визуальной фиксации PASS.

h) **Cleanup**:
   ```bash
   powershell -Command "Get-Process VPNRouter.App,VPNRouter.Service,sing-box -ErrorAction SilentlyContinue | Stop-Process -Force"
   rm -rf /c/Temp/stable-test
   ```

i) **IF ANY STEP FAILS** (download error, install hang, helper crash, version
   mismatch, App не запускается после update, smoke fails):
   - **DO NOT CUT**. Stable cut откладывается.
   - Diagnose root cause (logs: update.log, vpnrouter.log, Event Viewer
     для Service, helper.cmd output).
   - Fix в коде / helper.cmd / install.ps1.
   - Ship `-r(N+1)` через `ship-rolling-candidate` skill.
   - Run этот gate заново на новом -r(N+1).
   - Repeat until PASS.

**Detailed report для user'а** (часть "request cut" сообщения):
- Previous stable tag downloaded: `vX.Y.(Z-1)` (or same Z, prior -rN cut)
- Update path triggered: UI / programmatic
- Helper.cmd exit code + log tail
- ProductVersion + FileVersion после update (proof of successful flip)
- Smoke result: connect/disconnect outcome + screenshot
- PASS / FAIL per step (a..h)

Только после PASS — просим user'а "cut" / "ok" / "promote".

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

- Force-update stable tag (`git tag -f vX.Y.Z`) после публикации release.
- Skip Homebrew Cask verify — пользователи на macOS застревают на старом cask.
- Удалить тэг `vX.Y.Z-rN` из git — мы только release удаляем.
