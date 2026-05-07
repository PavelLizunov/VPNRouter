# VPNRouter Android — roadmap до полного desktop-platform parity

## Цель

Шипить обновления одновременно на Win + Mac + Linux + **Android** одной командой
`build.ps1 -Version "X.Y.Z" -Upload` или `gh release create`. Same release tag,
same release notes, same versioning scheme. Auto-update flow на каждой
платформе подтянет соответствующий artefact.

## Текущая матрица состояний (post v2.32.0)

| Слой | Win | Mac | Linux | Android |
|---|---|---|---|---|
| **Build** | local `build.ps1` | GHA `build-mac.yml` (cloud Mac runner) | GHA `build-linux.yml` (cloud Ubuntu) | **local manual `dotnet build VPNRouter.Android`** ❌ |
| **Asset в GH Release** | `.zip` + `update.zip` + 2 sha256 | `.dmg` + `.zip` | `.deb` + `.AppImage` + `.tar.gz` + 3 sha256 | **отсутствует** ❌ |
| **CI verify** | T2 update test + T3 integrity gate | build-mac.yml | build-linux.yml | **none** ❌ |
| **Distribution channel** | install.ps1 + auto-update | Homebrew Cask | APT (vpn.ninitux.com) + AppImage direct | **none — пользователь скачивает APK вручную из GH Releases** ❌ |
| **Auto-update** | UpdateChecker → helper.cmd → BoxService | Homebrew `brew upgrade` | APT `apt upgrade` | **AndroidUpdater из AND-AUTOUPDATE chip** ⚠ (есть, но targets nothing — APK не в release'ах) |
| **Versioning** | `AppVersion.Version` baked in | `AppVersion.Version` | `AppVersion.Version` | `AppVersion.Version` ✓ |
| **Самостоятельный self-repair** | SR-1..SR-4 (v2.32) | inherits | inherits | AND-SELF-REPAIR из pool 7 ⚠ (commit'нут но pool 7 backed out из-за Mono crash, ждёт AND-FIX2) |
| **Feature parity (UI)** | reference baseline | reference baseline | reference baseline | **9 из 9 desktop-страниц** ✓ post pool 5+6 (после AND-FIX2 + 7 ещё лучше) |
| **Identical localization** | Strings.cs (RU/EN) | inherits | inherits | Localization.cs (RU/EN, mirrors desktop) ✓ |
| **VPN routing protocols** | VLESS/Hy2/TUIC/etc | inherits | inherits | VLESS+Reality+TCP+Vision ✓, Hy2 ✓, TUIC ✓, gRPC ❌ (server-specific issue) |

**Bottom line**: код-уровень + UX уже близко к парити. Разрыв — в **build/release pipeline**: Android не входит в один и тот же ship cycle с desktop.

## Gap analysis по компонентам

### Build pipeline gap (P0 — без этого "одной командой" невозможно)

**Сейчас**:
- Desktop: `build.ps1 -Version X.Y.Z -Upload` → builds 4 win assets, GHA picks up tag и собирает Mac+Linux в parallel
- Android: только manually локально `dotnet build` + `scp` на Mac + `adb install`

**Need**:
- `.github/workflows/build-android.yml` (mirror `build-linux.yml` pattern)
- Build на ubuntu-latest с .NET 8 SDK + Android workloads
- Sign APK (debug-key OK для now; production: own keystore stored as GHA secret)
- Upload to release: `VPNRouter-vX.Y.Z-android-arm64.apk` + `.sha256`
- Optional: build android-arm + android-x86_64 для эмуляторов

### Release asset gap (P0)

**Сейчас 12 assets** (4 Win + 2 Mac + 6 Linux). После Android-CI должно стать **14** (+ APK + sha256).

Updates:
- `release-strategy.md` "Verification gate" condition (d) меняется с 12 → 14 assets
- `verify-release-integrity.yml` (T3) extends asset list + `*.apk` extraction + AppVersion scan inside Core.dll внутри APK (same UTF-16 trick)
- README install instructions добавляют Android section

### Auto-update channel parity (P1)

**Сейчас**:
- Win UpdateChecker.cs читает `app.update_channel` (stable/prerelease) → GH API filtering
- Android AndroidUpdater из AND-AUTOUPDATE chip имеет ту же логику но shipped в pool 6 (= main today)

**Gap**: AndroidUpdater предполагает что есть APK asset с predictable name pattern. Сейчас pattern не established (release не содержит APK). После build-android.yml — pattern фиксируется как `VPNRouter-vX.Y.Z-android-arm64.apk`.

**Action**: при первом ship'е с Android-CI убедиться что AndroidUpdater download URL pattern совпадает. Если нет — patch UpdateChecker URL template.

### Sign + permission flow (P1)

**Сейчас**: APK подписан debug-keystore'ом dotnet'а. Для prod auto-update users должны trust это публикуя через GH Releases. Android проверяет signature mismatch когда обновление приходит — если другой ключ, отказывает.

**Action для prod auto-update в Android**:
- Создать **prod keystore** (RSA 2048, 50-year validity) — store в `secrets.ANDROID_KEYSTORE_BASE64` + `ANDROID_KEYSTORE_PASSWORD` GHA secrets
- `build-android.yml` берёт keystore из secrets, подписывает APK через `apksigner` (Android SDK build tools)
- **First public release должен использовать этот keystore** — все будущие APKs должны подписываться им же чтобы Android updates работали

### F-Droid / Play Store distribution (P2 — long-term)

**Не критично для MVP** — direct APK install через GH Releases работает для tech-savvy users.

Long-term:
- F-Droid submission: каждый release auto-builds через F-Droid CI на основе их metadata YAML — нужен `metadata/com.ninitux.vpnrouter.yml` + reproducible-build flag
- Play Store: требует $25 dev account + manual upload первой версии + automated upload через `fastlane` для последующих

### Feature parity backlog (P2)

После pool 5+6 + (post-AND-FIX2) pool 7 + AND-PROFILES — feature gap closes. Что остаётся:

- gRPC server-specific issue (Phase 8.5 — server-side, not platform)
- Theme-aware mascot invert уточнение (some edge cases)
- Performance audit pass 2 (handbook §8.3)

Эти не блокируют parity-shipping; они incremental polish.

---

## Phased implementation plan

### Phase A — CI infrastructure (1 task)

**A.1 build-android.yml** — GHA workflow на ubuntu-latest:
1. Setup .NET 8 SDK + Android workload (`dotnet workload install android`)
2. Setup JDK 17 (Temurin) + Android SDK cmdline-tools
3. Restore secrets: `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`
4. Build: `dotnet publish VPNRouter.Android -c Release -p:RuntimeIdentifiers=android-arm64 -p:AndroidSigningKeyStore=$keystore -p:AndroidSigningStorePass=$password -p:AndroidSigningKeyAlias=vpnrouter -p:AndroidSigningKeyPass=$password`
5. Verify APK: `aapt dump badging` shows com.ninitux.vpnrouter + correct version
6. Upload: `gh release upload <tag> path/to/com.ninitux.vpnrouter-Signed.apk` (renamed to `VPNRouter-v$VER-android-arm64.apk`) + `.sha256`

**Trigger**: same as build-mac.yml — `push: tags: v*` + `workflow_dispatch`. Runs in parallel with Mac/Linux.

**Time**: 1-2 ship cycles to stabilize. Initial keystore generation = manual (one-time).

### Phase B — Release strategy update (1 task, docs)

**B.1 plans/vpnrouter-release-strategy.md update**:
- "Verification gate" condition (d): 12 → **14** assets
- Add Android to "what's in a release" matrix
- Document keystore management

**B.2 verify-release-integrity.yml extend**:
- Add APK asset to expected list
- Extract APK → unzip → find Core.dll → UTF-16 scan AppVersion (same as Mac DMG path, soft-warn since AOT trims partial)
- Verify .apk.sha256

### Phase C — Auto-update closure (1 task)

**C.1 AndroidUpdater URL patterns**:
- Update `AndroidUpdater.cs` (already in main from AND-AUTOUPDATE pool 6) — confirm download URL matches `VPNRouter-v{ver}-android-arm64.apk` pattern from Phase A
- Live test: install v2.32.0-r1 APK on phone → bump to r2 via in-app "Check for updates" → verify download + PackageInstaller flow

### Phase D — Documentation + install one-liners (1 task)

**D.1 install instructions**:
- README.md / README.ru.md add Android section
- vpn.ninitux.com landing page добавляет 4-th button "Android APK"
- install scripts: separate `install-android.sh` n/a (sideload only) — direct APK link from website

**D.2 Android handbook §3.5 update**:
- Replace "build locally + scp + adb" workflow with "download from GH Releases or auto-update"
- Keep dev workflow для contributors

### Phase E — F-Droid + Play Store (long-term, optional)

Не входит в parity-shipping — это external distribution channels with their own ceremony.

---

## Predicted timeline

| Phase | Effort | Blocker |
|---|---|---|
| A — build-android.yml CI | 4-6 hours | Keystore generation (1-time, ~30 min on user side) |
| B — Strategy + verify-release-integrity | 2 hours | Phase A merged |
| C — Auto-update URL pattern verify | 2-3 hours | Phase A first ship cycle complete (live download test) |
| D — Docs + install one-liners | 2 hours | Phase A live |
| E — F-Droid / Play | weeks | Not needed for parity |

**Total для parity**: ~10-15 hours work + 1 ship cycle to validate.

## Tasks для следующего pool

1. **AND-CI** — build-android.yml + keystore setup + first APK in GH release
2. **AND-RELEASE-STRATEGY** — verify-release-integrity.yml + release-strategy.md updates
3. **AND-AUTOUPDATE-VALIDATE** — confirm AndroidUpdater download URL pattern + live test
4. **AND-FIX2** — pool 7 Mono crash diagnosis (already spawned)
5. **AND-PROFILES** — last remaining feature gap chip (already spawned)

## Дальше — operational changes

После Phase A+B+C cuts стабильны:
- **Single ship command**: `build.ps1 -Version X.Y.Z -Upload` теперь triggers Mac+Linux+**Android** через tag push + создаёт release с Win locally
- **Auto-update**: works for all 4 platforms identically
- **Test gate**: T2 update test extends to Android (additional CI matrix dimension)
- **Stable cut ceremony**: includes APK live update verification

## Risks + mitigations

| Risk | Mitigation |
|---|---|
| Keystore lost → stuck shipping new APKs (users with old install can't auto-update) | Backup encrypted to multiple secure locations; document rotation procedure |
| Android user installs APK from wrong source (sideload from third party) | Verify install.sh / website downloads use GH Releases canonical URL only; sign warning if signature mismatch on auto-update |
| F-Droid signature differs from our keystore | F-Droid does its own builds + signs with their key — needs separate "F-Droid build" channel в release. Out of scope для MVP. |
| First-time user "Install unknown apps" prompt UX friction | install scripts deeplink to Settings + explain in README |
| AndroidUpdater hits GitHub API rate limit | Use unauthenticated (60/h public) — sufficient. Add ETag caching if needed later. |

---

**Last updated**: 2026-05-07 после v2.32.0 stable cut.

**Active blocker**: AND-FIX2 (Mono crash) — pool 7 work parked until that resolves. AND-PROFILES — last feature-parity chip ждёт запуска.

**Next ship after Phase A/B/C**: первый release с APK будет signal что cycle закрыт.
