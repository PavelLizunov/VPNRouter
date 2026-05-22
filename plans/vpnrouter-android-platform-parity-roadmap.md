# VPNRouter Android — roadmap до полного desktop-platform parity

## Цель — две стороны одной задачи

### 1. Shipping parity (один релиз = все платформы)

Шипить обновления одновременно на Win + Mac + Linux + **Android** одной командой
`build.ps1 -Version "X.Y.Z" -Upload` или `gh release create`. Same release tag,
same release notes, same versioning scheme. Auto-update flow на каждой
платформе подтянет соответствующий artefact.

### 2. Development parity (одна правка = две платформы)

Сейчас новая фича = port-once на desktop, потом второй проход для Android. Это
double-cost + drift-risk: после v2.32 cycle мы прошли pool 5+6+7 = ~16
manually-portированных tasks, ~10 000 строк mirror-кода. Если каждая новая
desktop-фича дальше будет требовать второго захода — Android всегда будет
позади на 1-2 минора. Цель: **structural sharing** — одно изменение в
бизнес-логике или ViewModel-уровне работает на обеих платформах без manual
mirror'а.

Конкретно:

- **Core layer** уже shared (✓ post Phase 1) — VPNRouter.Core используется и
  desktop'ом, и Android'ом. Бизнес-логика автоматически parity при правке.
- **ViewModel layer** на desktop живёт в VPNRouter.App. На Android —
  inline в `AndroidApp.axaml.cs` (~5000 строк). Drift-risk высокий.
  План: вынести VM-логику из VPNRouter.App в новый shared
  `VPNRouter.Avalonia.Common` или `VPNRouter.UI` project, чтобы оба
  app-проекта (App + Android) consumed её. Phase G ниже.
- **XAML layer** различается — desktop XAML files vs Android C#-built UI
  (no XAML files on Android; AndroidApp builds widgets programmatically).
  Это структурное различие, не drift — Avalonia Mobile не любит XAML
  files без Heavy ResourceDictionary lookups, поэтому Android ушёл в
  C#-only построение. Long-term можно унифицировать через shared
  `UserControl`-классы в общем project'е.
- **Localization** — `VPNRouter.App/Localization/Strings.cs` (desktop) и
  `VPNRouter.Android/Localization.cs` (Android) сейчас вручную mirror'ятся.
  Phase H: переместить single source-of-truth в Core (или shared UI
  project), оба consumed.

После этих структурных шагов цикл "новая фича" сокращается с **2 заходов** до
**1 захода + автоматическое обновление двух платформ при rebuild'е**.

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

**A.1 build-android.yml** — GHA workflow на ubuntu-latest. Файл создан:
`.github/workflows/build-android.yml` (mirror build-linux.yml shape).

Steps (текущая реализация):
1. Setup .NET 8 SDK + Android workload (`dotnet workload install android`)
2. Setup JDK 17 (Temurin) via `actions/setup-java@v4`
3. Setup Android SDK via `android-actions/setup-android@v3` (cmdline-tools, license accept, ANDROID_HOME export)
4. Decode `ANDROID_KEYSTORE_BASE64` secret → `vpnrouter.keystore`
5. Build: `dotnet publish VPNRouter.Android/VPNRouter.Android.csproj -c Release -p:RuntimeIdentifiers=android-arm64 -p:AndroidSigningKeyStore=<keystore_path> -p:AndroidSigningStorePass=<password> -p:AndroidSigningKeyAlias=vpnrouter -p:AndroidSigningKeyPass=<password>`
6. Locate signed APK via `find` (output path varies by SDK version), rename → `VPNRouter-v$VER-android-arm64.apk`
7. SHA256 sidecar
8. Upload as workflow artifact (always)
9. Upload to GH Release if push-tag OR dispatch with upload flag (skips with warning if release не создан yet)

**Trigger**: same as build-mac.yml — `push: tags: v*` + `workflow_dispatch` (with `version` + `upload_to_release` inputs). Runs in parallel with Mac/Linux.

**Time**: 1-2 ship cycles to stabilize. Initial keystore generation = manual (one-time, см. ниже).

#### One-time keystore setup (manual, before first run)

Workflow требует два repo secrets. Pavel генерирует keystore локально и
uploadит как secrets через `gh`. Без этого workflow упадёт на decode step
с явной error message.

```bash
# 1. Generate keystore (one-time, 50-year validity)
# RSA 2048, alias = "vpnrouter" (must match -p:AndroidSigningKeyAlias в workflow).
# Pick a strong password — same value used for store + key.
keytool -genkeypair \
  -keystore vpnrouter.keystore \
  -alias vpnrouter \
  -storepass <PASSWORD> \
  -keypass <PASSWORD> \
  -keyalg RSA \
  -keysize 2048 \
  -validity 18250 \
  -dname "CN=VPNRouter, O=ninitux, C=RU"

# 2. Encode for GHA secret transport
# -w0 = no line wrapping (gh secret set chokes on multi-line base64)
base64 -w0 vpnrouter.keystore > keystore.b64

# 3. Upload as repo secrets (gh CLI must be authenticated)
gh secret set ANDROID_KEYSTORE_BASE64 \
  --repo PavelLizunov/VPNRouter \
  -b "$(cat keystore.b64)"
gh secret set ANDROID_KEYSTORE_PASSWORD \
  --repo PavelLizunov/VPNRouter \
  -b "<PASSWORD>"

# 4. Cleanup transport file (keep keystore + password offline)
rm keystore.b64
```

**КРИТИЧНО — backup keystore + password to multiple secure locations**
(encrypted offline archive + 1Password / Bitwarden / equivalent).
Потеря keystore = невозможность ship'ить updates существующим Android
installs: Android refuses APK с другим signing key для того же
package id (`com.ninitux.vpnrouter`). Users были бы вынуждены делать
clean reinstall (uninstall → install fresh APK), теряя settings +
subscription URLs + saved servers.

После того как secrets проставлены — push любого `v*` tag триггерит
workflow автоматически вместе с Mac + Linux. Первый APK в GH Release
после первого ship cycle.

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

### Phase F — Build hygiene rules (один build = одна команда)

**F.1 stale-obj/ awareness** — pool 7 Mono crash debugging session показал что
быстрые revert-rebuild циклы оставляют corrupted typemap/JCW state в
`VPNRouter.Android/{bin,obj}`. Симптом: `mono_method_get_unmanaged_callers_only_ftnptr`
SIGABRT при init_android_runtime. Фикс: перед спорным rebuild'ом — `rm -rf
bin obj`. Lesson: `plans/v2.32.0-android-pool7-mono-crash-fix.md`.

**F.2 build.ps1 -AndroidAlso flag** — extension чтобы команда
`build.ps1 -Version X.Y.Z -Upload -AndroidAlso` локально билдит APK тоже
(для contributors без GHA secrets). При CI же APK билдится remote через
build-android.yml (Phase A), не нужен local Android workload setup.

### Phase G — Shared ViewModel layer (структурный refactor)

**Самый дорогой и самый важный для long-term development parity.**

**Сейчас**: `VPNRouter.App/ViewModels/MainWindowViewModel.cs` (~5900 строк) +
`VPNRouter.Android/AndroidApp.axaml.cs` (~5500 строк после pool 7) — **две
независимые VM-implementations**, делающие одно и то же. Каждая новая
desktop-фича → второй заход на Android через AND-* task.

**Цель**: extract VM layer в shared project (`VPNRouter.Avalonia.UI` или
аналог). Desktop App + Android App оба consume его. Одна правка → обе
платформы.

Layout после refactor'а:

```
VPNRouter.Core              ← бизнес-логика (already shared) ✓
VPNRouter.Avalonia.UI       ← NEW shared VM-layer + UserControl-уровень
  ├── ViewModels/
  │   ├── MainWindowViewModel.cs (was in App)
  │   ├── SubscribeViewModel.cs
  │   └── ...
  ├── Controls/
  │   ├── StatusCard.axaml + .cs
  │   ├── ChipsRow.axaml + .cs
  │   └── ...
  └── Localization/
      └── Strings.cs (was in App)
VPNRouter.App               ← desktop-shell: window, page-routing, win-only
                              specifics (HKCU\Run autostart, etc.)
VPNRouter.Android           ← android-shell: VpnService, libbox interop,
                              SharedPreferences, BootReceiver, etc.
```

**Effort**: 30-50 hours real refactor + extensive regression test pass.
Sequential, рискованный. Делать **после** Phase A+B+C закроют shipping
parity (потому что shipping parity — leverage для validating refactor:
если после refactor'а APK всё ещё ship'ится одной командой, значит
не сломали).

### Phase H — Single localization source-of-truth — DONE (2026-05-22 recon)

**Состояние**: оба wrapper'а уже pure pass-through к
`VPNRouter.Core/Localization/Strings.cs`:
- `VPNRouter.App/Localization/Strings.cs` — 593 members, 0 non-pass-through.
- `VPNRouter.Android/Localization.cs` — 884 members, 0 non-pass-through, плюс
  3 bootstrap members (`Ru`, `LoadFromStorage`, `ToggleAndPersist`) которые
  Android-specific и должны остаться.

Core Strings.cs — единственный source of truth (~895 members). Phase 2A
(App pass-through) и параллельный Android pass-through уже завершены до
текущей session'и. Drift-risk закрыт.

Дальнейшая консолидация (например shared `VPNRouter.Avalonia.UI` project
со Strings) — косметика, не add'ит value пока wrapper'ы — pure delegation.
Не приоритет.

---

## Predicted timeline

### Shipping parity (P0 — закрывает первую цель)

| Phase | Effort | Blocker |
|---|---|---|
| A — build-android.yml CI | 4-6 hours | Keystore generation (1-time, ~30 min on user side) |
| B — Strategy + verify-release-integrity | 2 hours | Phase A merged |
| C — Auto-update URL pattern verify | 2-3 hours | Phase A first ship cycle complete |
| D — Docs + install one-liners | 2 hours | Phase A live |

**Subtotal**: ~10-15 hours + 1 ship cycle to validate.

### Development parity (P1 — закрывает вторую цель)

| Phase | Effort | Blocker | Value |
|---|---|---|---|
| F — Build hygiene rules | 1 hour | none | mid (lessons from past mistakes) |
| H — Shared localization | 2-3 hours | none | mid (~3 drift cases prevented per cycle) |
| G — Shared VM layer (big refactor) | 30-50 hours + regression | Phase A+B+C done | **HIGH** — closes 80% of double-port cost |

**Subtotal**: 33-54 hours + 1-2 cycles for regression-bake.

### Optional (P2)

| Phase | Effort | Blocker |
|---|---|---|
| E — F-Droid / Play | weeks | not needed for parity |

**Total для full parity (development + shipping)**: ~50-70 hours work, ~3-4 ship cycles.

## Tasks для следующих pools

### Pool A (shipping parity)
1. **AND-CI** — build-android.yml + keystore setup + first APK in GH release
2. **AND-RELEASE-STRATEGY** — verify-release-integrity.yml + release-strategy.md updates
3. **AND-AUTOUPDATE-VALIDATE** — confirm AndroidUpdater download URL pattern + live test
4. **AND-PROFILES** — last remaining feature gap chip (already spawned, not run)

### Pool B (development parity, after Pool A merges)
5. **AND-LOCALIZATION-MERGE** (Phase H) — single Strings.cs source-of-truth
6. **AND-VM-EXTRACT-PHASE-1** (Phase G part 1) — pull simplest VMs (Status, ConfigRow) из App в shared project. Validate one platform at a time
7. **AND-VM-EXTRACT-PHASE-2** (Phase G part 2) — bulk VM extraction
8. **AND-VM-EXTRACT-PHASE-3** (Phase G part 3) — final cleanup + Android consumes shared VMs

### Pool C (build hygiene)
9. **AND-BUILD-HYGIENE** (Phase F) — `build.ps1 -AndroidAlso` flag + clean-obj-on-revert helper script + handbook §F.1 cross-link

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
