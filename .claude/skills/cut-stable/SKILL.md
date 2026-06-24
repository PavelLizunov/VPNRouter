---
name: cut-stable
description: Promote a rolling -rN candidate to stable vX.Y.Z (no suffix). Bumps AppVersion, creates fresh tag without suffix, full rebuild + Mac/Linux CI, restores Latest, deletes -rN.
when: Latest -rN passed full verification gate (build + tests + Mac/Linux CI green + 14 desktop assets, 16 with Android after Step 5.6 + MCP-verify + **mandatory pre-cut live update gate**) AND user gave explicit "cut" / "ok" / "promote" command (rule 6 в `CLAUDE.md`, lessons v2.31.2 / v2.31.7). Cut НЕ autonomous.
---

# Cut a stable release

Promotes `vX.Y.Z-rN` to stable `vX.Y.Z` (без суффикса). Per CLAUDE.local.md
"Build / push steps" — обязательно НОВЫЙ тэг без `-rN`, а не просто переброс
prerelease flag.

## Pre-flight (verification gate, autonomous)

Все hard чек-боксы зелёные (1-6 ниже + 6.5 open-defect gate):
1. `dotnet build -c Release` → 0 errors
2. Regression tests зелёные (`VlessServersResolverTests`, `ConfigGeneratorEmptyServersGuardTests`, `FreeConfigAggregatorPreserveTests`)
3. Mac CI на последнем -rN — `success`
4. Linux CI на последнем -rN — `success`
5. `gh release view vX.Y.Z-rN --json assets --jq '.assets|length'` → `14` (desktop-only)
   **or `16`** if this candidate's Android APK was already built. The full correct set
   is **16** = 4 Win + 4 Mac + 6 Linux + 2 Android; **14** = desktop-only. Android is
   usually attached at the stable cut (Step 5.6), but a `-rN` may already carry it — so
   accept both 14 and 16 here, just confirm the desktop 14 are all present.
6. **Live update gate PASS** (см. секцию ниже) — обязательная mandatory.
6.5. **Open-defect ledger clear** (audit item 7, 2026-06-25) — `pwsh tools/check-open-p0.ps1`
   exits 0 (нет открытых `- [ ]` P0/P1 в `plans/OPEN-DEFECTS.md`), ИЛИ перезапусти с
   `-Waive '<reason>'` и зафиксируй причину в cut-сообщении. **BLOCKS cut иначе.** Скилл
   `bug-hunt` дописывает survivors в этот ledger — это гейт, который остановил бы
   отложенный auto-failover P0 от попадания в stable (diag 20260624-235243). Tiny /
   config-only cuts — waive с однострочной причиной.
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
     --pattern 'VPNRouter-v*-win.zip' --dir /c/Temp/stable-test
   ```
   The full install ZIP is named `VPNRouter-vX.Y.Z-win.zip`. The `-v*-win.zip` glob
   deliberately excludes the `VPNRouter-update-*-win.zip` lite package and the
   `.sha256` sidecar. (The old `VPNRouter-*-windows-x64.zip` pattern matched **nothing** —
   no asset carries `windows-x64` in its name.)

c) **Extract + launch + initial settle**:
   ```bash
   cd /c/Temp/stable-test
   powershell -Command "Expand-Archive -Path 'VPNRouter-v*-win.zip' -DestinationPath ./extracted -Force"
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

f) **Verify new version installed cleanly.** Important: do NOT verify via
   `(Get-Item VPNRouter.App.exe).VersionInfo.ProductVersion`: the build passes no
   `<Version>` (see `build.ps1` — plain `dotnet publish`, no `-p:Version`), so the PE
   ProductVersion is the MSBuild default **`1.0.0+<gitsha>`** and `VersionInfo.FileVersion`
   is `1.0.0.0` — NEVER the semantic version. Verify instead by any of:
   - **CLI `doctor`** (most direct — prints `AppVersion.Version`, incl. the `-rN` suffix):
     ```bash
     /c/Temp/stable-test/extracted/VPNRouter.CLI.exe doctor   # first line → "Version: <candidate>"
     ```
     Do **not** use `VPNRouter.CLI.exe --version` — that prints the Spectre.Console
     application version (`1.0.0`), not the release version.
   - **InformationalVersion git-SHA flip**: the `+<gitsha>` suffix of the App.exe
     ProductVersion must change from the baseline build's SHA to the candidate commit's
     SHA (proof the new binaries actually landed, even though the `1.0.0` prefix is fixed).
   - **Install receipt** `%ProgramData%\VPNRouter\.update-installed-version`: the updater
     writes it, and the relaunched candidate **consumes (deletes)** it because the running
     version is strictly newer than the recorded version. A receipt left behind ⇒ the new
     version did not take.

   > **Receipt semantics — NOT a bug:** line 2 of `.update-installed-version` is the
   > **PRE-update** version by design (`TryWriteInstallReceipt` records the version you
   > updated *from*; `CheckInstallReceipt` passes only when the running version is
   > *strictly newer*). So right after a `2.42.0 → 2.42.1` update a receipt reading
   > `2.42.0` is CORRECT — it's the baseline, and a clean launch then deletes it. Do not
   > flag this as a failed update.

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
- Version proof after update: `doctor` semver + install receipt consumed +
  InformationalVersion git-SHA flip (NOT PE ProductVersion — it's always `1.0.0+<gitsha>`)
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

## Step 4 — mirror stable tag to Forgejo

Do **not** use `git fetch github` / `git fetch github --tags` here: the GitHub
remote can advertise corrupt `refs/codex/turn-diffs/...` checkpoint refs and
abort full fetches with `fatal: bad object refs/codex/...`.

`build.ps1` should already have created the local stable tag. Verify GitHub's
tag commit via the API, confirm it matches the local tag/current commit, then
push the local tag to Forgejo:

```bash
gh api repos/PavelLizunov/VPNRouter/commits/vX.Y.Z --jq .sha
git rev-parse HEAD
git show-ref --verify refs/tags/vX.Y.Z
git push origin vX.Y.Z          # mirror в Forgejo
```

If the local tag is missing, create it only after the GitHub API SHA matches
the intended `HEAD`.

## Step 5 — Mac + Linux CI + full test suite (auto-triggered tag push)

Wait for both build runs AND the **`dotnet test` job on the stable tag** (audit
item 10: `test.yml` now triggers on `v*` tags — a stable cut on a SHA differing
from the last green -rN previously ran ZERO behavioral tests). The tag's `test`
must be `success` before Step 5.5 / un-draft:
```bash
TAGSHA=$(gh api repos/PavelLizunov/VPNRouter/git/refs/tags/vX.Y.Z --jq .object.sha)
gh api repos/PavelLizunov/VPNRouter/commits/$TAGSHA/check-runs --jq '.check_runs[]|select(.name=="test")|.conclusion'
```
Then verify the desktop assets:
```bash
gh release view vX.Y.Z --repo PavelLizunov/VPNRouter --json assets --jq '.assets | length'
```
**14** desktop assets (4 Win + 4 Mac + 6 Linux). After Step 5.6 (Android) it's
**16**.

## Step 5.6 — build + sign + attach the Android APK (stable only)

The full Android build can't run on hosted CI (NU1102). Build it unsigned
locally (warm NuGet cache) and sign it in CI with the keystore secret:

```bash
# 1. build the UNSIGNED apk on this VM (versionCode derives from the version)
dotnet publish VPNRouter.Android/VPNRouter.Android.csproj -c Release \
  -p:VpnRouterVersion=X.Y.Z -p:EnableAndroidTarget=true \
  -p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME"
# the unsigned apk is com.ninitux.vpnrouter.apk under bin/Release/net*-android*/

# 2. upload it to the release as the UNSIGNED staging asset (the install-page
#    regex VPNRouter-v.*-android\.apk$ deliberately does NOT match -UNSIGNED)
cp .../com.ninitux.vpnrouter.apk publish/android/VPNRouter-vX.Y.Z-android-UNSIGNED.apk
gh release upload vX.Y.Z publish/android/VPNRouter-vX.Y.Z-android-UNSIGNED.apk --clobber

# 3. sign in CI (zipalign + apksigner, no .NET -> no NU1102). It uploads the
#    signed VPNRouter-vX.Y.Z-android.apk + .sha256 and removes the UNSIGNED asset.
gh workflow run "Sign Android APK" -f version=X.Y.Z
gh run watch <run-id> --exit-status

# 4. verify: signed, versionCode preserved, sha256 matches
gh release download vX.Y.Z --pattern "VPNRouter-vX.Y.Z-android.apk*"
"$ANDROID_HOME/build-tools/<v>/apksigner.bat" verify --print-certs VPNRouter-vX.Y.Z-android.apk
"$ANDROID_HOME/build-tools/<v>/aapt2.exe" dump badging VPNRouter-vX.Y.Z-android.apk | grep "^package:"
```

Optional but recommended: `adb install -r` on the attached phone via the Mac
(`ssh slovn@192.168.0.246`, adb at `/opt/homebrew/bin/adb`) and confirm
launch + no crash. After this, the asset count is **16**.

Background: `plans/android-ci-distribution-roadmap-2026-05-31.md`,
`.github/workflows/sign-android.yml`, `build-android.ps1`.

## Step 5.5 — post-publish draft + download-URL gate (mandatory)

**Why this exists**: `verify-release-integrity.yml` runs on every
`release: edited`, so it fires DURING the parallel Mac/Linux uploads. Before
the v2.37.0 fix (2026-05-28) it could hard-fail on a mid-upload orphaned
`.sha256` sidecar and run `gh release edit --draft=true`, drafting the
release. The still-uploading binaries then attached to a DRAFT (getting
`untagged-<hash>` asset URLs), the tagged `download/vX.Y.Z/...` URLs 404'd,
and the Homebrew Cask auto-bump failed 6× on those 404s. The workflow fix
makes that false-positive impossible; **this gate is the belt-and-suspenders
that catches ANY future draft toggle before we walk away from the cut.**

**When**: после Step 5 (assets present), перед Step 6.

a) **Release must be published, not draft**:
   ```bash
   gh api repos/PavelLizunov/VPNRouter/releases/tags/vX.Y.Z --jq '.draft'
   ```
   Must print `false`. If `true` → self-heal + investigate:
   ```bash
   gh release edit vX.Y.Z --repo PavelLizunov/VPNRouter --draft=false --latest
   gh run list --repo PavelLizunov/VPNRouter \
     --workflow="Verify Release Integrity" --limit 5
   ```
   Open the failed run's log — if the cause is anything OTHER than a real
   Windows AppVersion mismatch / sha256 content mismatch, the workflow fix
   regressed; fix before continuing.

b) **Tagged download URLs must resolve (200, not 404)** — the `untagged-<hash>`
   signature surfaces here as a 404 on the canonical tagged path:
   ```bash
   for asset in \
     "VPNRouter-vX.Y.Z-mac.dmg" \
     "VPNRouter-vX.Y.Z-linux-amd64.deb" \
     "VPNRouter-vX.Y.Z-win.zip"; do
     code=$(curl -sIL -o /dev/null -w "%{http_code}" \
       "https://github.com/PavelLizunov/VPNRouter/releases/download/vX.Y.Z/$asset")
     echo "$code  $asset"
   done
   ```
   All must be `200`. A `404` = asset attached while draft (untagged URL);
   un-drafting in (a) republishes and rewrites URLs to the tagged path —
   re-run this loop to confirm 200.

c) **If you had to un-draft in (a)**: the Homebrew Cask bump (Step 8) almost
   certainly 404'd during the draft window. After URLs are 200, re-trigger it
   — re-run `build-mac.yml` (its Trigger Homebrew Cask step re-dispatches) or
   dispatch the tap directly, then verify per Step 8.

Only after **draft=false + all URLs 200** → continue to Step 6.

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
