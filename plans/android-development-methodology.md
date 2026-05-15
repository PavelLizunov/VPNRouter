# VPNRouter Android — Development Methodology

**Версия методологии**: 1.0 (2026-05-12, после v2.32.2 stable cut).
**Maintainer**: Pavel Lizunov + Claude.
**Цель**: фиксировать процесс разработки Android-порта так, чтобы он:
  1. был воспроизводимым (не зависел от того, у кого свежая память),
  2. защищал от «test-fitted-to-fit» антипаттерна,
  3. ловил регрессии производительности и connection-стабильности
     **до** того, как они попадут к пользователю,
  4. использовал MCP-инструменты системно, а не ad-hoc,
  5. **сам себя тестировал** через meta-checks.

> **Правило #0 — Самопроверка**: этот документ описывает методологию,
> которой я сам обязан следовать. Каждый раздел имеет
> «Meta-test» подсекцию, которая описывает как ПРОВЕРИТЬ что я её
> придерживаюсь. Если meta-test для раздела упал — методология
> нарушена, нужно фиксировать процесс, а не код.

---

## 0 · Контекст и цели проекта

### Что есть сегодня — REALITY CHECK 2026-05-15

**Major realignment**: исходная версия этого документа (1.0, 2026-05-12)
была написана на основе устаревшего `MEMORY.md` который говорил «Phase 0
done, Phase 1 next». Реальная проверка проекта показала, что Phase 0,
1, 2 и большая часть 3 уже сделаны:

**Verified via APK install on KYOCERA A101BM (Android 12) 2026-05-15**:
- ✅ APK builds (`dotnet build VPNRouter.Android.csproj` — 0 errors, 3:16)
- ✅ App launches without crash (PID assigned, MainActivity opens)
- ✅ Avalonia UI fully rendered: brand header «Virtual Penguin Network»,
     VPN/Zapret/TG badges, status card, mode picker, VLESS URI input,
     app-selection radio, autostart row, «Подключить» button
- ✅ libbox.aar wired at `VPNRouter.Android/Lib/libbox.aar` (12 MB,
     gitignored, built earlier in session by previous Claude)
- ✅ `VpnRouterService.java` (1196 LOC) — full VPN service impl
- ✅ `AndroidDeepVerifyBox.java` (603 LOC) — libbox-backed Free Configs
     deep verify
- ✅ 9 `AndroidApp.*.cs` partial files covering: AdvancedShell, AutoUpdate,
     ConfigShare, DpiBypass, FreeConfigs, ServerList, SubscribePage, Tools
- ✅ `AndroidConfigBuilder.cs` — generates sing-box config matching Core
- ✅ `AndroidUpdater.cs` — auto-update mechanism (with intent flow)
- ✅ Settings/profiles via shared `VPNRouter.Core` source-link
- ✅ Phase 1.A keystore secret pending (CI skips gracefully per Bug-r10-J)

### Phase state (corrected)

| Phase | Scope | Status |
|---|---|---|
| **Phase 0** | APK builds, scaffold | ✅ DONE |
| **Phase 1** | libbox.aar + VpnService | ✅ DONE (Java-side service, libbox AAR wired) |
| **Phase 2** | Avalonia App.axaml UI port | ✅ DONE (Simple + Advanced shell + sub-pages) |
| **Phase 3** | Settings/profiles parity | ✅ MOSTLY (via shared Core source-link; per-platform AppPaths working) |
| **Phase 3.5** | Per-page polish + Android-specific UX | ⏳ in progress (this is where current iterations happen) |
| **Phase 4** | Battery / lifecycle / Doze / OEM polish | ⏸ pending |
| **Phase 5** | Distribution (Play / F-Droid / APK auto-update) | ⏸ partial (auto-update direct APK works; Play Store not started) |

### Что реально следующее (2026-05-15)

Не «libbox bootstrap» (already done), а итеративная работа:
1. **Bug fixes** — localization mismatches («Windows» текст в Android),
   обработка edge cases per real-device testing
2. **Live VPN connect verification** — paste working subscription, Connect,
   verify traffic flows through tunnel (instrumented end-to-end)
3. **Test coverage audit** — что уже покрыто, что упущено per §3 layers
4. **Performance baselines** — first capture с реального устройства (A101BM)
5. **Phase 4 prep** — Doze mode, foreground service notification polish

### libbox build path (canonical for FUTURE rebuilds)

When sing-box version bumps require new AAR — use
`plans/android-phase-1-libbox-build.md` + `tools/build-libbox-aar.sh`.
Key learnings documented there:
- Use sagernet/gomobile fork v0.1.12, NOT upstream golang.org/x/mobile
- Curated tag set: `with_gvisor,with_quic,with_utls,with_wireguard,with_clash_api,badlinkname,tfogo_checklinkname0`
- ldflag: `-checklinkname=0`
- Skip `with_naive_outbound,with_tailscale` (cronet-go NDK 27 lld incompat)
- Pin to Go 1.25.x (1.26+ has unrelated linkname issue)

Existing AAR (`VPNRouter.Android/Lib/libbox.aar`, May 7 2026, sha256
`239c4101...`) works for current sing-box version. Don't rebuild
unless protocol/upstream changes.

### Non-goals (выяснить до начала Phase 1)

- iOS port — не сейчас (Avalonia iOS less mature; отдельный roadmap)
- Wear OS / Auto — не нужно
- Tablets — поддерживаем как side-effect, не оптимизируем целевым

### Meta-test 0: project state freshness

```bash
# Check that this doc references an existing AppVersion + branch + tags.
test -f VPNRouter.Core/AppVersion.cs
grep -q "EnableAndroidTarget" VPNRouter.Android/VPNRouter.Android.csproj || \
  echo "FAIL: phase-0 build flag missing — methodology references stale state"
```

---

## 1 · Принципы разработки

### 1.1 Test-First когда intent ясен

**Правило**: перед тем как писать код, который реализует **clearly-defined
public contract**, сначала пишется test, который ассертит ЭТОТ contract
(не реализацию).

Когда применять:
- Public API класса добавляешь — тест **сначала**.
- Bug-fix регрессионный — тест воспроизводящий баг **сначала**.
- Performance benchmark — baseline-assertion **сначала**.

Когда не применять:
- Spike / exploration — пиши код, потом из него вытаскивай invariants в тесты.
- UI design iteration — снапшоты + visual-diff после стабилизации дизайна.

**Анти-pattern**: «фиксанул баг → добавил тест на ту же строку → коммит». Это
test-fitted-to-fix. Признак: тест ассертит **состояние после фикса**, а не
**ожидаемое поведение из user-story**.

### 1.2 Independent assertions

Каждый тест проверяет **внешне-наблюдаемое поведение** (return value, side
effect, event fired), не **внутреннюю реализацию** (private state, mock call
order, internal flag).

**Признаки fitted-to-fit теста**:
- Использует `[InternalsVisibleTo]` чтобы ассертить private field — ⛔
- Mock с verify count="exactly 1" — ⛔ (детали реализации)
- Тест name матчит имя fix-commit'а а не user-story — ⚠ flag

**Признаки independent теста**:
- Setup строит state как user построил бы его в UI
- Assert проверяет что user **видел бы**
- Не упоминает internal helper method names

### 1.3 Performance budgets first

**Правило**: для каждого performance-sensitive code path (sing-box startup,
config gen, UI render, network probe) есть **baseline file** в `screenshots/`
или `perf-baselines/` который фиксирует:
- 50th / 95th / 99th percentile latency (3 runs minimum)
- Memory delta (MB)
- Battery δ (если применимо)

CI fail если новый код **deteriorates 95th percentile by > 20%** vs baseline.

### 1.4 Documentation cross-ref required

Когда meрж'у chip / merge'у feature ветку, обязательно:
- Ссылка на upstream docs (sagernet/sing-box-for-android, Avalonia, AndroidX)
  в комментариях кода ИЛИ в plan-документе
- Версия upstream'а зафиксирована (commit SHA или release tag)
- Diff с upstream'ом задокументирован если форкаем что-то

### Meta-test 1: process compliance

```bash
# Run before every commit. Failure = methodology violation.

# 1. Every new test class has a category tag (XML doc comment / attribute)
for f in VPNRouter.Tests/*Tests.cs; do
  head -20 "$f" | grep -qE "(Category|Phase|Trait)" || \
    echo "WARN: $f lacks category marker"
done

# 2. Every public class in VPNRouter.Core has at least one test reference
# (CI-grade check, lenient locally)

# 3. Every performance-sensitive method has a baseline (TODO: pin list)
```

---

## 2 · Архитектурные решения (pre-committed)

Эти решения зафиксированы и пересматриваются ТОЛЬКО при явном triggering
event (upstream breaking change, hardware limit hit, etc.).

| Decision | Choice | Rationale |
|---|---|---|
| UI framework | Avalonia 11.3 | Cross-platform parity с desktop, single codebase |
| VPN engine | sing-box via libbox.aar (sagernet) | Production-grade, matches desktop binary |
| Storage | AppPaths.OverrideDataDir → `Context.getFilesDir()` | Already wired в Core |
| Min SDK | API 26 (Android 8.0) | Covers > 95% active devices per Google stats |
| Target SDK | latest stable (35 для 2026) | Required для Play Store |
| Language | C# (NET 8 / mono-android) | Reuse Core |
| Build system | dotnet publish via Android workload | Standard для .NET Android |
| Signing | One keystore, base64 в GitHub Secrets | Phase A roadmap |
| Distribution Phase 1 | Direct APK + auto-update | Quick to ship; Play Store after |
| Auto-update | Shared `UpdateChecker` polling | Reuse desktop infra |
| Logging | Same Serilog → file pipeline | Cross-platform observability |
| Testing | xUnit + custom Android device fixtures | Same harness где можно |

### Meta-test 2: architectural drift

```bash
# When was last review of these decisions?
git log --since="3 months ago" -- plans/android-development-methodology.md | \
  grep -q "Architectural decisions reviewed" || \
  echo "WARN: methodology architecture section not reviewed in 90 days"
```

---

## 3 · Test taxonomy для Android

### 3.1 Unit Tests (Layer C — Core)

Pure logic. Без device dependency, без network, без UI framework.

| Class pattern | Examples | Location |
|---|---|---|
| `*ParserTests` | URI / config parsing | `VPNRouter.Tests/` (shared) |
| `*ValidatorTests` | Schema, leak protection | shared |
| `*ResolverTests` | VLESS resolution | shared |
| `*MigratorTests` | Settings migration | shared |
| `AndroidStorageSaneTests` | Android-specific paths | shared (#if ANDROID) |

**Coverage gate**: каждый public method из Core должен иметь как минимум
один Unit test, проверяющий happy path + один failure path.

### 3.2 Integration Tests (Layer B — sing-box / libbox bindings)

Real components, no UI. Spawn sing-box process / load libbox AAR. Run on:
- Desktop CI (for libbox parsing of generated config)
- Android emulator (for full bind)

| Test | Что проверяет |
|---|---|
| `LibboxStartShutdown` | libbox.Start + libbox.Stop без leak |
| `ConfigGeneration_AndroidValid` | Сгенерированный JSON проходит libbox validation |
| `ProcessRouting_PackageNameNotProcessName` | На Android process_name это package id, не exe |
| `VpnServiceBindRoundTrip` | VpnService bind + grant + traffic flow |

### 3.3 UI Tests (Layer A — Avalonia headless)

Snapshot + interaction tests. Avalonia headless harness уже есть (`TestAppBuilder.cs`).
Расширяем под Android-specific:

| Test | Что |
|---|---|
| `SimplePage_AndroidNarrow_RenderClean` | Snapshot на 320/360/411 dp widths |
| `TouchPress_StartButton_FiresCommand` | Tap input routing |
| `BackButton_BehaviourOnEachPage` | Android back-button (system-level) |
| `Permissions_VpnPromptFlow` | Dialog appearing на VPN permission grant |

### 3.4 Performance Benchmarks (Layer P)

Continuous baseline + regression detection.

| Benchmark | Baseline (target) | Failure threshold |
|---|---|---|
| `ColdStart_MainActivity_to_FirstPaint` | < 1500 ms (P95) | + 20% |
| `SingBoxStart_ConfigToConnected` | < 3000 ms (P95) | + 30% |
| `ConfigGeneration_500Apps` | < 100 ms | + 25% |
| `Memory_Idle_AfterConnect` | < 80 MB RSS | + 15% |
| `Battery_ConnectedOneHour_Discharge` | < 8% on Pixel 6a | + 25% |

Где хранится baseline:
- `VPNRouter.Tests/perf-baselines/android-*.json`
- Pinned per device class (mid-range / low-end)
- Updated only при intentional perf change (PR + signed-off review)

### 3.5 Connection / network tests (Layer N)

Что проверяет реальный flow.

| Test | Where |
|---|---|
| `RealVlessConnect_TcpPing_Success` | Emulator + real upstream |
| `ConnectionLoss_WifiToCellular_AutoReconnect` | Device-bench harness |
| `Doze_Background_TrafficResumes` | 30-min idle test |
| `BatteryRestrictedMode_OEM_TrafficStillFlows` | Per-OEM matrix (Xiaomi, Samsung) |

Эти **не** running на CI каждый push — это nightly или manual smoke перед release.

### 3.6 Lifecycle / battery tests (Layer L)

| Test | What |
|---|---|
| `ForegroundService_StartStop_NoLeak` | 1000 cycles |
| `AppKillByOOM_RestoreOnRelaunch` | adb am kill → relaunch state |
| `RotationChange_StatePreserved` | ConfigurationChanged handling |
| `Permission_Revoked_Mid-Connection_HandlesGracefully` | adb pm revoke |

### Meta-test 3: test categorization completeness

```bash
# Every test file has [Trait] / [Category] annotation matching one of:
# Unit | Integration | UI | Performance | Network | Lifecycle
EXPECTED=("Unit" "Integration" "UI" "Performance" "Network" "Lifecycle")

for f in VPNRouter.Tests/Android*.cs; do
  category=$(grep -oE "\[Category\(\"([^\"]+)\"\)\]" "$f" | head -1)
  if [ -z "$category" ]; then
    echo "FAIL: $f missing [Category(...)] attribute"
  fi
done
```

---

## 4 · MCP tools usage matrix

Когда какой MCP инструмент использовать. Системно, не ad-hoc.

| MCP tool | Когда использовать | Когда НЕ использовать |
|---|---|---|
| `claude-in-chrome` | Read upstream docs (sing-box, AndroidX, Avalonia), inspect Play Store listings, look up Stack Overflow для конкретных error codes | Не делать через computer-use если есть chrome MCP — dedicated MCP precise + fast |
| `computer-use` | Android Studio / emulator / device-side UI, Task Manager, file picker dialogs | Don't use для web (Chrome MCP), Bash (terminal restricted tier) |
| `Claude_Preview` | Mockup previews если есть HTML wireframes | Real Avalonia UI testing — через snapshot tests |
| `gh CLI` (через Bash) | Releases, secrets, dispatch, API queries | Не пытаться через Chrome — slower |
| `Bash` | Build commands, git, ADB CLI | Не для file ops (Read tool) |
| ADB (via Bash) | Emulator/device side: install, logcat, am, pm, screenshot | Не как primary testing — формальный test harness первичен |
| `WebFetch` | Public docs которые не в Chrome | Не для private repos |

### MCP workflow patterns

**Pattern A: New AndroidX API research**
1. `WebFetch` `https://developer.android.com/reference/...` для API surface
2. `claude-in-chrome` open + verify (some pages JS-rendered)
3. Cite version + URL в commit message

**Pattern B: Performance issue triage**
1. `Bash` adb logcat | grep VPNRouter → tail to find slow event
2. `Bash` adb shell dumpsys cpuinfo / meminfo
3. Pinpoint code path
4. Add benchmark BEFORE fix (Test-First rule 1.1)
5. Fix → benchmark should now pass baseline

**Pattern C: UI bug on real device**
1. `computer-use` Android Studio + USB-debug emulator → see UI
2. `Bash` adb screencap для зафиксированного state
3. Reproduce в headless test (Layer A) если возможно
4. Если headless не воспроизводит — это flag: snapshot тест слепой к этому,
   нужно расширить (Avalonia render diff, device-specific layout)

### Meta-test 4: MCP usage trace

```bash
# Periodic audit: every commit touching VPNRouter.Android should reference
# either a benchmark, an integration test, or a snapshot. Otherwise it's
# "blind code" — high regression risk.

git log --since="1 week ago" --name-only | grep "VPNRouter.Android/" | sort -u | \
  while read f; do
    last_commit=$(git log -1 --format=%H -- "$f")
    git show --stat "$last_commit" | grep -qE "(Test|Bench|Snapshot)" || \
      echo "WARN: $f changed without test ref in commit message $last_commit"
  done
```

---

## 5 · Performance baseline workflow

### 5.1 Capturing initial baseline

При первом deployment phase / при intentional perf change:

```bash
# Run benchmark N=10 times
for i in $(seq 1 10); do
  ./benchmark-cold-start.sh >> raw.log
done

# Compute P50 / P95 / P99
python tools/perf-stats.py raw.log > baseline.json

# Commit
git add VPNRouter.Tests/perf-baselines/android-cold-start.json
git commit -m "perf: capture baseline for cold-start (P95=1420ms on Pixel 6a)"
```

### 5.2 Regression detection в CI

Каждый push на `main` (или PR with Android changes):
- Run benchmark в Android emulator workflow
- Compare to pinned baseline
- Fail if P95 deteriorates > threshold

### 5.3 Updating baseline (intentional)

Только при:
- Hardware shift (new device class added)
- Upstream lib bump (libbox new version)
- Architectural change (e.g. AOT mode change)

PR must include:
- Old + new baseline JSON
- Explanation в commit message
- Reviewer sign-off (sometimes Pavel + Claude both agree)

### Meta-test 5: baseline freshness

```bash
# If baseline file is older than 90 days, audit:
find VPNRouter.Tests/perf-baselines/ -name "*.json" -mtime +90 | \
  while read f; do
    echo "WARN: baseline $f is stale (>90d). Verify still representative."
  done
```

---

## 6 · Anti-fitted-to-fit checklist

Перед merge'м chip с тестами проходишь checklist:

| Check | Verdict |
|---|---|
| Test name reflects **user story / contract** (not «test_added_for_pr_42») | ✓/✗ |
| Setup строит state как user построил бы (UI clicks / API call), не direct private mutation | ✓/✗ |
| Assert проверяет externally-observable behaviour (return value / event / persisted state) | ✓/✗ |
| Test НЕ использует internal helper imports just to peek at state | ✓/✗ |
| Test fails on a fresh clone WITHOUT the fix applied (regression-pin) | ✓/✗ |
| Test НЕ duplicate existing test in same file | ✓/✗ |
| Test файл имеет [Category("...")] attribute | ✓/✗ |
| Test добавлен в `plans/android-test-inventory.md` (или equivalent) | ✓/✗ |

8 checks. Если 6+ pass — accept. Меньше — refactor before merge.

### Meta-test 6: checklist enforcement

PR template:
```
## Tests added
- [ ] Followed test-first if contract was clear
- [ ] Anti-fitted-to-fit checklist 6+/8

## Performance
- [ ] No baseline regression
- [ ] If intentional baseline change — old+new JSON + reviewer sign-off
```

---

## 7 · Phase-by-phase execution plan

### Phase 1 — libbox + VPN service (next)

**Goal**: Android app can connect to a VLESS server using identical sing-box
config as desktop, sustains traffic via VpnService.

| Step | Tests required |
|---|---|
| 1.1 Fetch libbox.aar (or build from source pinned to upstream tag) | `LibboxStartShutdownTests` (Integration) — load + unload без leak |
| 1.2 JNI wrapper for `box::start_with_config` / `stop` | `LibboxStartWithConfig_HappyPath` + `LibboxStartWithConfig_BadJson_Fails` (Integration) |
| 1.3 `VpnRouterService.kt` (Kotlin) implementing `VpnService` | `VpnServiceLifecycleTests` (Lifecycle layer) |
| 1.4 Activity → Service handshake (bound service) | `BindRoundTrip` (Integration) |
| 1.5 First real connect from app | `Smoke_RealVlessConnect` (Network manual) |

Acceptance: real VLESS connect → curl ifconfig.io returns proxy IP.

### Phase 2 — Avalonia UI port

(Detail когда дойдём)

### Phase 3 — Settings parity

### Phase 4 — Battery / lifecycle polish

### Phase 5 — Distribution

### Meta-test 7: phase progress

```bash
# Each phase has acceptance criteria. Verify N (current phase) is done
# before declaring N+1 started.
# Phase status table in this doc must be updated on every phase transition.
```

---

## 8 · Tooling list

Per-developer setup (this VM today + reproducible for new contributors):

### Already on this VM
- .NET 8 SDK ✓
- Temurin 17 JDK (`JAVA_HOME` set) ✓
- Android SDK 34 (cmdline-tools + sdkmanager) (`ANDROID_HOME` set) ✓
- `dotnet workload install android` ✓
- adb in PATH ✓

### To add for Phase 1
- Android emulator image (API 26 + API 35) via `sdkmanager`
- libbox.aar artifact (decision: fetch upstream pre-built OR build from source)
- Optional: physical test device — Pixel 6a connected via ADB-WiFi

### Meta-test 8: toolchain reproducibility

```bash
# Bootstrap script `tools/android-bootstrap.ps1` must install all above.
# Run on fresh VM; if any step fails → fix script, not env.
```

---

## 9 · Documentation update process

Этот документ обновляется когда:
1. Phase transition (1→2, 2→3, etc.) — add lessons learned section
2. Architectural decision changes (rare) — update §2 with reason
3. New test category emerges — add to §3
4. New MCP tool added to harness — update §4
5. Baseline shift > 20% — note context in §5

**Update cadence**: every release that touches Android. If 30 days pass
without an update AND there были Android commits — flag in audit (meta-test 9).

### Meta-test 9: doc freshness

```bash
# Verify doc updated recently (or no Android commits)
android_commits=$(git log --since="30 days ago" --oneline -- VPNRouter.Android/ | wc -l)
doc_updated=$(git log -1 --format=%cd --date=relative plans/android-development-methodology.md)

if [ "$android_commits" -gt 5 ]; then
  echo "Android churn detected ($android_commits commits in 30d). Last doc update: $doc_updated"
fi
```

---

## 10 · Risk register

Pre-identified risks, each with mitigation.

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| libbox.aar API breaks между upstream releases | M | H | Pin upstream commit SHA, не follow main branch |
| Avalonia mobile target lags Android target SDK requirements | M | M | Test on Phase 2 entry, escalate to Avalonia issue tracker if blocking |
| Battery drain unacceptable on low-end devices | M | H | Performance baseline в §5, OEM-specific tests (§3.5) |
| Play Store rejects VPN app (policy) | M | H | Skip Play Store initially; F-Droid + direct APK + winget-style auto-update |
| Signing key lost / leaked | L | Critical | Backed up encrypted (1Password + offline USB), Phase A roadmap |
| OEM-specific battery managers (Xiaomi, Samsung) kill background service | H | M | Documented user workaround + foreground notification + Doze test (§3.5) |
| sing-box upstream switches Reality config shape | L | H | Pin sing-box version, test on bump |
| VPN permission revoked mid-session | M | L | Graceful state — auto-restart prompt |
| Process-name routing не работает на Android (Android sandboxes process names) | H | H | Phase 1 must validate: route by **package name**, not process name. Generate sing-box config accordingly |

### Meta-test 10: risk register completeness

When new risk encountered → add to register. PR that introduces feature
should reference relevant row if applicable.

---

## 11 · Methodology meta-summary

«Если methodology работает» = все 9 meta-tests passing:

| # | Meta-test | Owner |
|---|---|---|
| 0 | Project state references current | Claude (every session start) |
| 1 | Process compliance (test markers, doc cross-refs) | Pre-commit hook |
| 2 | Architectural decisions not drifted | Quarterly review |
| 3 | Test categorization completeness | CI gate |
| 4 | MCP usage traced in commits | Weekly audit |
| 5 | Performance baselines fresh | Quarterly review |
| 6 | Anti-fitted-to-fit checklist enforced | PR template + reviewer |
| 7 | Phase progress documented | Phase transition |
| 8 | Toolchain bootstrap script works on fresh VM | Yearly + new contributor |
| 9 | This doc updated if Android churn detected | Auto-flag |

### Single-file checker

`tools/check-methodology.sh` (TODO Phase 1): runs all 10 meta-tests, exits
non-zero if any fail. Hooked into pre-push.

```bash
#!/usr/bin/env bash
# Phase 1 work item — formalize the meta-tests into executable script.
set -e
echo "[methodology] meta-test 0..."   # phase 0 refs current
echo "[methodology] meta-test 1..."   # process compliance
# ... (10 checks)
```

---

## 12 · How I (Claude) follow this myself

**Rule for me**: at the start of every Android development task this session
or future:

1. **Read this doc** (re-skim §1 + §3 + relevant phase)
2. **Identify which meta-tests apply** to the change I'm about to make
3. **Write test FIRST** if contract is clear (§1.1)
4. **Tag test category** [Trait("Phase1.Integration")] or similar (§3, §3.6)
5. **Reference MCP tool choice** in commit if non-trivial (§4)
6. **Update baseline** ONLY if intentional perf change (§5)
7. **Run meta-test #1 (compliance) before commit** (locally)
8. **Update this doc if** new pattern emerges (§9)

If user asks «следуешь ли ты методологии» — answer with explicit table:
which steps done, which skipped, why. Honest gap-flag faster than silent
debt accumulation (Avalonia App CLAUDE.md rule E2).

---

## Cross-references

- `plans/vpnrouter-android-research.md` — initial Phase 0 research (historical)
- `plans/vpnrouter-android-handbook.md` — toolchain setup details (per memory)
- `plans/vpnrouter-android-platform-parity-roadmap.md` — Phase A keystore + signing
- `plans/r10-test-coverage-audit.md` — desktop test audit (template for Android-side audit)
- `VPNRouter.Tests/CLAUDE.md` — test infrastructure docs

## Changelog для этого документа

| Дата | Версия | Что |
|---|---|---|
| 2026-05-12 | 1.0 | Initial draft (после v2.32.2 stable cut) |
