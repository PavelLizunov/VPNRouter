# Qwen audit — remaining remediation index (post-P1 survivors)

Date: 2026-07-29
Authoring engine: Qwen Code (documentation-only in this worktree)
Worktree: `C:\Project\VPNRouter-qwen-remaining-briefs-2026-07-29`
Adjudication source of truth:
`C:\Project\VPNRouter-qwen-p00-2026-07-29\plans\qwen-audit-independent-verification-2026-07-28.md`
Prompt pool: `C:\Project\VPNRouter-qwen-audit-2026-07-28\plans\qwen-audit-remediation-prompt-pool-2026-07-28.md`
Adjudicated commit: `b39a28c32fae26838e615b5080d183dc33ee551b` (== this worktree HEAD, == `origin/main`)

This index covers the 22 survivors that remain after the P1 wave, PLUS one
newly-discovered defect (FW-3 → R12) found DURING Qwen R01 implementation review
(see row 23). The P1 wave (13 IDs: UPD-1, UPD-2, FAIL-1, DATA-1, FLOW-1, CLI-1,
CLI-2, AND-1, SUP-1, SEC-1, SEC-2, OBS-1, ZAP-1) is already implemented in draft
PRs #53, #55-#61 on the `codex/qwen-audit-p0X-*-2026-07-29` branches and is NOT
re-covered here. The 5 refuted IDs (AND-2, DATA-2, DATA-5, PERF-2, and the
refuted P1 form of LIFE-1) are NOT covered here.

**R12 provenance**: FW-3 was NOT part of the original P00 audit survivor set. It
was discovered on 2026-07-29 while tracing the firewall wiring for R01
(FW-1/FW-2/TEST-1): the full-tunnel branch of `StartupPipeline` replaces the
selected profile with a synthetic `FullTunnel` profile whose `BlockOnVpnFail`
defaults false, dropping the user's per-profile kill-switch intent. It is tracked
here for completeness and sequenced right after R01 (it reuses R01's test seam).

---

## 1. Scope invariant (23 = 19 P2 + 4 P3)

| # | ID | Final severity | Verdict | R-package | Brief |
|---:|---|---|---|---|---|
| 1 | FW-1 | P2 | PARTIALLY_CONFIRMED | R01 | phase2-audit-r01-firewall-wiring |
| 2 | FW-2 | P2 | PARTIALLY_CONFIRMED | R01 | phase2-audit-r01-firewall-wiring |
| 3 | TEST-1 | P2 | CONFIRMED | R01 | phase2-audit-r01-firewall-wiring |
| 4 | CFG-1 | P2 | CONFIRMED | R02 | phase2-audit-r02-config-protocol |
| 5 | CFG-2 | P2 | CONFIRMED | R02 | phase2-audit-r02-config-protocol |
| 6 | PROTO-1 | P2 | CONFIRMED | R02 | phase2-audit-r02-config-protocol |
| 7 | DATA-3 | P2 | CONFIRMED | R03 | phase2-audit-r03-data-network |
| 8 | DATA-4 | P2 | CONFIRMED | R03 | phase2-audit-r03-data-network |
| 9 | DATA-6 | P2 | CONFIRMED | R03 | phase2-audit-r03-data-network |
| 10 | NET-1 | P2 | CONFIRMED | R03 | phase2-audit-r03-data-network |
| 11 | UI-2 | P2 | CONFIRMED | R04 | phase2-audit-r04-ui-layout |
| 12 | PKG-1 | P2 | CONFIRMED | R05 | phase2-audit-r05-packaging-supply |
| 13 | SUP-2 | P2 | CONFIRMED | R05 | phase2-audit-r05-packaging-supply |
| 14 | SUP-4 | P2 | CONFIRMED | R05 | phase2-audit-r05-packaging-supply |
| 15 | SEC-3 | P2 | CONFIRMED | R06 | phase2-audit-r06-security-diagnostics |
| 16 | OBS-2 | P2 | PARTIALLY_CONFIRMED | R06 | phase2-audit-r06-security-diagnostics |
| 17 | ZAP-2 | P2 | CONFIRMED | R07 | phase2-audit-r07-updater-resources |
| 18 | ZAP-3 | P2 | CONFIRMED | R07 | phase2-audit-r07-updater-resources |
| 19 | LIFE-1 | P3 | REFUTED (residual only) | R08 | phase3-audit-r08-lifecycle-hygiene |
| 20 | UI-1 | P3 | CONFIRMED | R09 | phase3-audit-r09-localization |
| 21 | SUP-3 | P3 | CONFIRMED | R10 | phase3-audit-r10-signing-action-pins |
| 22 | PERF-1 | P3 | PARTIALLY_CONFIRMED | R11 | phase3-audit-r11-etw-disposal |
| 23 | FW-3 | P2 | CONFIRMED | R12 | phase2-audit-r12-full-tunnel-killswitch |

Coverage invariant:

```text
Expected survivors:            22  (original P00 survivor set)
Newly-discovered (R01 review):  1  (FW-3 -> R12, row 23; NOT a P00 survivor)
Total tracked here:            23
P2 tracked:                    19  (rows 1-18 + row 23)
P3 tracked:                     4  (rows 19-22)
IDs with no R-package:          0
IDs with multiple R-packages:   0
P1-wave IDs included here:      0  (UPD-1/2, FAIL-1, DATA-1, FLOW-1, CLI-1/2, AND-1, SUP-1, SEC-1/2, OBS-1, ZAP-1)
Refuted IDs given a fix brief:  0  (AND-2, DATA-2, DATA-5, PERF-2 fully refuted; LIFE-1 P1 form refuted -> P3 residual only)
```

---

## 2. Ordering and dependencies

### 2.1 Base-branch map (verified against `git diff --stat origin/main...<branch>`)

| R-package | Base branch | Reason |
|---|---|---|
| R01 | `origin/main` | LinuxFirewallManager.cs / MacFirewallManager.cs / StartupPipelineTests.cs are not touched by any P1 branch. |
| R02 | `origin/main` | ConfigGenerator.cs / CustomConfigInjector.cs / VlessDeepVerifier.cs are not touched by any P1 branch. |
| R03 | `origin/main` | SettingsMigrator.cs / FreeConfigAggregator.cs / FreeConfigs/FreeConfigCache.cs / PolicyHttpClient.cs are not touched by any P1 branch. See caution §2.3 (NET-1 / SubscriptionFetcher.cs proximity to P09). |
| R04 | `origin/main` | INSPECTED: P06 (`codex/qwen-audit-p06-smart-connect-persistence-2026-07-29`, FLOW-1) touched ONLY `MainWindowViewModel.SimpleMode.cs` (+6) and `SmartConnectPersistenceTests.cs`. It did NOT touch `NetworkPage.axaml`. UI-2 lives entirely in `Views/Pages/NetworkPage.axaml`. No overlap -> base is `origin/main`, not the P06 branch. |
| R05 | `codex/qwen-audit-p08-appimagetool-pin-v2-2026-07-29` | MANDATED: SUP-2 shares `.github/workflows/build-linux.yml`; P08-v2 modified that file (+16/-2). R05 must build on P08-v2 to avoid a conflicting edit. PKG-1/SUP-4 touch `build-mac.sh` (not touched by P08-v2) but ride the same branch for a single supply-chain PR. |
| R06 | `codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29` | MANDATED: OBS-2 shares `VPNRouter.Core/Services/CrashReporter.cs`; P09 modified that file (+7). R06 must build on P09. SEC-3 touches `EmergencyChannelManager.cs` (not touched by P09) but rides the same branch for a single security/diagnostics PR. |
| R07 | `origin/main` | INSPECTED: P10 (`codex/qwen-audit-p10-zapret-atomicity-2026-07-29`, ZAP-1) touched ONLY `ZapretUpdater.cs`. ZAP-2 lives in `EmergencyChannel/EmergencyChannelManager.cs` + `EmergencyChannelEngine.cs`; ZAP-3 lives in `WgturnUpdater.cs`. No file overlap -> base is `origin/main`. |
| R08 | `origin/main` | LIFE-1 residual is confined to `TunOwnershipLock.cs`. P02 (`codex/qwen-audit-p02-failover-wiring-2026-07-29`) touched ONLY `VpnEngine.cs` (+92/-45). `SingBoxManager.cs` (a caller) is not modified by P02. No overlap -> base is `origin/main`. |
| R09 | `origin/main` | `MainWindow.axaml` update button is not touched by any P1 branch. |
| R10 | `origin/main` | `.github/workflows/sign-windows.yml` is not touched by any P1 branch. |
| R11 | `codex/qwen-audit-p02-failover-wiring-2026-07-29` | MANDATED: PERF-1 disposes the ETW monitor at `VpnEngine.cs:832` (`_etw?.Stop()`) / `:845` (`_etw = null`). P02 rewrote that exact teardown region of `VpnEngine.cs` (+92/-45). R11 must build on P02 to avoid a conflicting edit and to test against the post-FAIL-1 teardown. |
| R12 | `codex/qwen-audit-r01-firewall-wiring-2026-07-29` (R01 branch) | RECOMMENDED: R12's regression test reuses the capturing `FirewallFactory` fake that R01 adds to `StartupPipelineTests.cs` (R01 flips it from throwing to capturing). R01 does NOT edit `StartupPipeline.cs` production code, so the only shared file is `StartupPipelineTests.cs`; basing on R01 avoids a textual conflict and supplies the fake. Fallback `origin/main` ONLY if R01 has already merged (the fake is then in main). |

### 2.2 Execution order

P2 packages first (R01-R07 + R12), then P3 packages (R08-R11). Within P2 the
order follows the prompt pool §22 recommended PR order, adapted to survivors:

1. **R03** (DATA-3/4/6, NET-1) — data-safety, no dependency.
2. **R01** (FW-1/FW-2, TEST-1) — kill-switch correctness + its missing test.
   2a. **R12** (FW-3) — full-tunnel kill-switch intent; immediately after R01
   (reuses R01's capturing `FirewallFactory` fake; same test file).
3. **R02** (CFG-1/CFG-2/PROTO-1) — config/protocol parity.
4. **R06** (SEC-3/OBS-2) — security/diagnostics (after P09 merges).
5. **R05** (PKG-1/SUP-2/SUP-4) — supply-chain pins (after P08-v2 merges).
6. **R07** (ZAP-2/ZAP-3) — updater atomicity / resource ownership.
7. **R04** (UI-2) — narrow UI layout.

P3:

8. **R11** (PERF-1) — after P02 merges (shared VpnEngine.cs).
9. **R08** (LIFE-1 residual) — optional hygiene; may be closed with no code if
   the residual is judged not worth a change (see brief).
10. **R09** (UI-1) — localization one-liner.
11. **R10** (SUP-3) — SHA-pin signing actions when the workflow is enrolled.

### 2.3 Cross-package merge cautions

- **R03 / NET-1 vs P09 (SEC-1):** both touch the subscription intake area.
  NET-1's fix is in `PolicyHttpClient.cs` (bounded streaming); SEC-1's P09 fix
  is in `SubscriptionFetcher.cs` (URL redaction). Different files, but if both
  are in flight, rebase R03 onto the merged P09 before pushing to avoid a
  textual conflict in the `SubscriptionFetcher.cs:66-85` intake region.
- **R06 / OBS-2 vs P09 (OBS-1):** both edit `CrashReporter.cs`. R06 is based on
  the P09 branch precisely so the two edits compose; do not re-base R06 onto
  `origin/main` while P09 is unmerged.
- **R11 / PERF-1 vs P02 (FAIL-1):** both edit the `VpnEngine` teardown block.
  R11 is based on the P02 branch; verify the `_etw` lines still read
  `try { _etw?.Stop(); } catch { }` / `_etw = null;` after the P02 rewrite
  before editing.
- **R05 / SUP-2 vs P08-v2 (SUP-1):** both edit `build-linux.yml`. R05 is based
  on P08-v2; add the sing-box/libcronet digest step near, but not on top of,
  the appimagetool pin block.
- **R12 / FW-3 vs R01 (TEST-1):** both add tests to `StartupPipelineTests.cs`.
  R12 is based on the R01 branch so it inherits R01's capturing `FirewallFactory`
  fake; do not re-base R12 onto `origin/main` while R01 is unmerged (the fake
  would be absent and R12's tests would hit the throwing factory). R12's only
  production edit is `StartupPipeline.cs:705`, which R01 does not touch.

### 2.4 Shared-root-cause reuse (fix once)

- **Atomic replace pattern** (DATA-6, ZAP-3, and the P1 DATA-1 fix): the
  canonical `File.Move(tmp, path, overwrite:true)` already exists at
  `FreeConfigPoolFetcher.cs:140`. DATA-6 and ZAP-3 must reuse it, not invent a
  new helper.
- **Bounded tail read** (OBS-2): reuse the bounded reverse-seek already
  implemented in `Diagnostics/DiagnosticsExporter.cs:525-540` (`TailLines`,
  `MaxTailReadBytes` 12 MB). Do not write a second tail reader.
- **Bounded HTTP read** (NET-1): mirror the bounded decompression already in
  `FreeConfigPoolFetcher.cs:37-38,115,131,179-181`.
- **DNS-strategy decision** (CFG-1): reuse the single generator decision
  (`ConfigGenerator.cs:995`) inside the injector; do not fork a second rule.
- **Process-ownership / argument safety** (SEC-3): prefer
  `ProcessStartInfo.ArgumentList` over a hand-quoted `Arguments` string; this
  is the same class of fix the codebase already documents in
  `SelfRepair.cs:122-126`.

---

## 3. Per-brief copy prompts

Each prompt is self-contained: it names the brief, the IDs, the base branch, the
execution constraint, and the deliverable. Paste verbatim into a fresh Qwen Code
session pointed at the implementation worktree.

### R01 — firewall wiring + kill-switch test

```text
Выполни brief plans/phase2-audit-r01-firewall-wiring-2026-07-29.md через Qwen
Code. IDs: FW-1, FW-2, TEST-1 (все P2). Base branch: origin/main. Сначала
прочитай brief целиком, AGENTS.md, plans/CLAUDE.md, VPNRouter.Core/CLAUDE.md и
VPNRouter.Tests/CLAUDE.md. Исправь IPv6 family mismatch только в pure ruleset
builders (LinuxFirewallManager.BuildRuleset -> ip6 daddr; MacFirewallManager
BuildRuleset -> inet6), не ослабляя default drop и не превращая hostname
resolution failure в allow-all; добавь executable pipeline-level regression test
для RoutingMode -> isFullTunnel -> CreateBlockRules (TEST-1). Переиспользуй
существующие helpers; без speculative abstractions. Напиши тесты, которые падают
на старом поведении. НЕ запускай локальные build/test/app/binary/service/
installer, не применяй nftables/PF нигде, не скачивай binary, не делай
VM/WinRM/ADB/MCP/live мутаций. Только чтение/поиск/редактирование кода и запись
тестов. Commit/push/CI делает orchestrator. Без release/merge/tag/deploy. Без
emoji. Подготовь diff и заполни секцию Outcome шаблоном PENDING.
```

### R12 — full-tunnel kill-switch intent (FW-3, found during R01 review)

```text
Выполни brief plans/phase2-audit-r12-full-tunnel-killswitch-2026-07-29.md через
Qwen Code. ID: FW-3 (P2, CONFIRMED; найден при Qwen R01 implementation review).
Base branch: codex/qwen-audit-r01-firewall-wiring-2026-07-29 (R12 переиспользует
capturing FirewallFactory fake из R01; если R01 уже merged — base origin/main).
Сначала прочитай brief целиком, AGENTS.md, plans/CLAUDE.md,
VPNRouter.Core/CLAUDE.md и VPNRouter.Tests/CLAUDE.md. Root-cause fix только в
full-tunnel ветке profile-resolution (StartupPipeline.cs:705): скопируй
BlockOnVpnFail из effective selected profile (settings.ActiveProfile через
существующий ProfileManager.MergeProfilesTolerant, true-wins) в синтетический
FullTunnel profile, чтобы kill-switch gate (StartupPipeline.cs:1084) мог сработать
в full-tunnel. Пустой/неразрешимый ActiveProfile -> false (без over-arming,
поведение не меняется). НЕ добавляй глобальный AppConfig toggle / UI editor; НЕ
трогай gate :1084, isFullTunnel derivation, platform CreateBlockRules impl
(IPv6 — это R01), CustomConfig synthetic (:734) и Android. Переиспользуй
существующие helpers; без speculative abstractions. Напиши 2 теста на capturing
fake: full-tunnel + profile BlockOnVpnFail=true -> CreateBlockRules вызван с
isFullTunnel=true (падает на старом коде); full-tunnel + нет block intent ->
НЕ вызван (guard от over-arming). НЕ запускай локальные build/test/app/binary/
service/installer, не применяй nftables/PF/netsh нигде, не скачивай binary, не
делай VM/WinRM/ADB/MCP/live мутаций. Только чтение/поиск/редактирование кода и
запись тестов. Commit/push/CI делает orchestrator. Без release/merge/tag/deploy.
Без emoji. Подготовь diff и заполни секцию Outcome шаблоном PENDING.
```

### R02 — config / protocol parity

```text
Выполни brief plans/phase2-audit-r02-config-protocol-2026-07-29.md через Qwen
Code. IDs: CFG-1, CFG-2, PROTO-1 (все P2). Base branch: origin/main. Сначала
прочитай brief целиком, AGENTS.md, plans/CLAUDE.md и VPNRouter.Core/CLAUDE.md.
CFG-1: переиспользуй одну DNS-strategy decision (ConfigGenerator.cs:995) в
CustomConfigInjector, чтобы !Ipv6Enabled тоже давал ipv4_only на include-split
path, не перезаписывая user-authored strategy без contract. CFG-2: сделай
injected urltest tag collision-free (или переиспользуй существующий urltest) и
добавь duplicate/reserved-tag проверку в Validate. PROTO-1: добавь typed
UnsupportedByVerifier short-circuit для dns-tunnel в VlessDeepVerifier, не
маркируя unsupported-verifier результат как blocked server. Без новых
abstractions/dependencies. Напиши тесты, падающие на старом поведении. НЕ
запускай локальные build/test/sing-box/binary, не делай live мутаций. Только
чтение/поиск/редактирование и запись тестов. Commit/push/CI делает orchestrator.
Без release/merge/tag/deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```

### R03 — data safety + network bound

```text
Выполни brief plans/phase2-audit-r03-data-network-2026-07-29.md через Qwen Code.
IDs: DATA-3, DATA-4, DATA-6, NET-1 (все P2). Base branch: origin/main (см.
caution: при наличии merged P09/SEC-1 сделай rebase до push). Сначала прочитай
brief целиком, AGENTS.md, plans/CLAUDE.md и VPNRouter.Core/CLAUDE.md. DATA-3:
сохрани explicitly-selected MTU 1280 в SettingsMigrator (не переписывай
custom-set значение). DATA-4: дедуплицируй remote IDs до ToDictionary в
FreeConfigAggregator (переиспользуй byId.ContainsKey защиту). DATA-6: замени
delete-then-move на File.Move(tmp, path, overwrite:true) в FreeConfigCache
(переиспользуй паттерн из FreeConfigPoolFetcher.cs:140). NET-1: ограничь
response size в PolicyHttpClient bounded streaming read (mirror
FreeConfigPoolFetcher bounded decompression). Без новых filesystem/HTTP
abstractions. Напиши тесты, падающие на старом поведении. НЕ запускай локальные
build/test/app/binary, не делай live мутаций. Только чтение/поиск/редактирование
и запись тестов. Commit/push/CI делает orchestrator. Без release/merge/tag/
deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```

### R04 — narrow UI layout

```text
Выполни brief plans/phase2-audit-r04-ui-layout-2026-07-29.md через Qwen Code.
ID: UI-2 (P2). Base branch: origin/main (P06/FLOW-1 не трогал NetworkPage.axaml
— overlap отсутствует). Сначала прочитай brief целиком, AGENTS.md,
plans/CLAUDE.md и VPNRouter.App/CLAUDE.md. Сделай read-mode rule row в
Views/Pages/NetworkPage.axaml responsive так, чтобы value и delete (✕) оставались
видимы/достижимы при MinWidth=360; переиспользуй существующий IsRulesNarrow
narrow-template паттерн; не добавляй horizontal scrolling как маскировку
clipping; сохрани keyboard-accessible delete. Запусти skill audit-overflow-fix
для UI scope. Напиши минимальный narrow-layout contract test. НЕ запускай
локальные build/Avalonia app/binary, не делай live/MCP мутаций. Только
чтение/поиск/редактирование и запись тестов. Commit/push/CI делает orchestrator.
Без release/merge/tag/deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```

### R05 — packaging / supply chain

```text
Выполни brief plans/phase2-audit-r05-packaging-supply-2026-07-29.md через Qwen
Code. IDs: PKG-1, SUP-2, SUP-4 (все P2). Base branch:
codex/qwen-audit-p08-appimagetool-pin-v2-2026-07-29 (SUP-2 делит
.github/workflows/build-linux.yml с P08-v2). Сначала прочитай brief целиком,
AGENTS.md, plans/CLAUDE.md, packaging/CLAUDE.md и .github/workflows/CLAUDE.md.
PKG-1: вычисли ARCH из build target (uname -m -> arm64/amd64) до wgturn branch в
build-mac.sh. SUP-2: pin + verify SHA256 sing-box/libcronet archive до extraction
в build-linux.yml (fail-closed). SUP-4: pin wgturn-core на commit/tag и assert до
bundling в build-mac.sh. Переиспользуй существующий native dependency manifest /
fail-closed checksum паттерн из P08-v2. НЕ скачивай и не исполняй непроверенные
binary, НЕ запускай локальные build/shell validation scripts. Только
чтение/поиск/редактирование; shell/YAML синтаксис проверяй статическим осмотром.
Commit/push/CI делает orchestrator (Linux+Mac CI после push). Без release/merge/
tag/deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```

### R06 — security / diagnostics

```text
Выполни brief plans/phase2-audit-r06-security-diagnostics-2026-07-29.md через
Qwen Code. IDs: SEC-3, OBS-2 (SEC-3 P2, OBS-2 P2 PARTIALLY_CONFIRMED). Base
branch: codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29 (OBS-2 делит
VPNRouter.Core/Services/CrashReporter.cs с P09). Сначала прочитай brief целиком,
AGENTS.md, plans/CLAUDE.md и VPNRouter.Core/CLAUDE.md. SEC-3: используй
ProcessStartInfo.ArgumentList (не вручную quoted Arguments string) в
EmergencyChannelManager, чтобы WgturnUrl/VkLink с кавычками не инжектировали
аргументы wgturn-cli. OBS-2: замени CrashReporter File.ReadAllLines на bounded
reverse-seek tail, переиспользуя DiagnosticsExporter.TailLines паттерн
(MaxTailReadBytes 12 MB); НЕ трогай DiagnosticsExporter.TailLines (он уже
bounded — sub-citation в аудите ошибочна). Напиши тесты, падающие на старом
поведении. НЕ запускай локальные build/test/app/binary/installer, не меняй ACL,
не делай live мутаций. Только чтение/поиск/редактирование и запись тестов.
Commit/push/CI делает orchestrator. Без release/merge/tag/deploy. Без emoji.
Заполни Outcome шаблоном PENDING.
```

### R07 — updater atomicity / resource ownership

```text
Выполни brief plans/phase2-audit-r07-updater-resources-2026-07-29.md через Qwen
Code. IDs: ZAP-2, ZAP-3 (оба P2). Base branch: origin/main (P10/ZAP-1 трогал
только ZapretUpdater.cs — overlap отсутствует). Сначала прочитай brief целиком,
AGENTS.md, plans/CLAUDE.md и VPNRouter.Core/CLAUDE.md. ZAP-2: dispose+null
предыдущий Process в EmergencyChannelManager.LaunchProcess до перезаписи и
dispose crashed manager в EmergencyChannelEngine.OnManagerCrashed. ZAP-3:
stage-and-atomic-replace wgturn-cli (File.Move(tmp, path, overwrite:true)) без
destructive delete-first; сохрани recovery copy до успеха; не удаляй единственную
copy в finally. Переиспользуй существующий atomic-replace паттерн
(FreeConfigPoolFetcher.cs:140). Напиши failure-injection тесты, падающие на старом
поведении. НЕ запускай локальные build/test/app/binary/service, не делай live
Zapret/Wgturn update. Только чтение/поиск/редактирование и запись тестов.
Commit/push/CI делает orchestrator. Без release/merge/tag/deploy. Без emoji.
Заполни Outcome шаблоном PENDING.
```

### R08 — lifecycle hygiene (LIFE-1 residual only)

```text
Выполни brief plans/phase3-audit-r08-lifecycle-hygiene-2026-07-29.md через Qwen
Code. ID: LIFE-1 (P3, residual handle-churn ONLY). Base branch: origin/main.
ВАЖНО: исходный P1 claim (semaphore block / cross-process brick) ОПРОВЕРГНУТ —
НЕ исправляй его. Сначала прочитай brief целиком, AGENTS.md, plans/CLAUDE.md и
VPNRouter.Core/CLAUDE.md. Scope строго ограничен P3 handle-churn: сделай
TunOwnershipLock Dispose/TryAcquire re-arm согласованным, чтобы пересозданный
после dispose handle отслеживался и освобождался. Если residual признан не
стоящим изменения — закрой brief с обоснованием без кода. Не создавай второй
lifecycle coordinator. Напиши дешёвый lifecycle тест (два acquire/stop/dispose
cycle) только если он действительно полезен. НЕ запускай локальные
build/test/app/binary/service, не делай live мутаций. Только чтение/поиск/
редактирование и запись тестов. Commit/push/CI делает orchestrator. Без
release/merge/tag/deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```

### R09 — localization (UI-1)

```text
Выполни brief plans/phase3-audit-r09-localization-2026-07-29.md через Qwen Code.
ID: UI-1 (P3). Base branch: origin/main. Сначала прочитай brief целиком,
AGENTS.md, plans/CLAUDE.md, VPNRouter.App/CLAUDE.md и
VPNRouter.Core/Localization/Strings.cs. Привяжи Content кнопки обновления в
Views/MainWindow.axaml:712-713 к существующей локализованной строке UpdateButton
(Strings.cs:773 / App Strings.cs:439) вместо hardcoded "↓ Update". Используй
существующую localization infrastructure; не добавляй новый ресурс. Напиши
минимальный RU/EN binding/source contract test. НЕ запускай локальные
build/Avalonia app/binary, не делай live/MCP мутаций. Только чтение/поиск/
редактирование и запись тестов. Commit/push/CI делает orchestrator. Без
release/merge/tag/deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```

### R10 — signing action pins (SUP-3)

```text
Выполни brief plans/phase3-audit-r10-signing-action-pins-2026-07-29.md через
Qwen Code. ID: SUP-3 (P3). Base branch: origin/main. Сначала прочитай brief
целиком, AGENTS.md, plans/CLAUDE.md и .github/workflows/CLAUDE.md. SHA-pin два
mutable actions (actions/upload-artifact@v4 и
signpath/github-action-submit-signing-request@v1) в
.github/workflows/sign-windows.yml на полные commit SHA, чтобы ни одного
unpinned uses: не осталось. Сохрани manual-only workflow_dispatch trigger и
"Guard - secrets present" fail-closed. Задокументируй update procedure для
пинов в commit message. НЕ запускай локальные build/actionlint/parse, не делай
live мутаций; YAML проверяй статическим осмотром. Только чтение/поиск/
редактирование. Commit/push/CI делает orchestrator. Без release/merge/tag/
deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```

### R11 — ETW disposal (PERF-1 residual only)

```text
Выполни brief plans/phase3-audit-r11-etw-disposal-2026-07-29.md через Qwen Code.
ID: PERF-1 (P3, PARTIALLY_CONFIRMED). Base branch:
codex/qwen-audit-p02-failover-wiring-2026-07-29 (PERF-1 трогает VpnEngine.cs
teardown, который P02 переписал). ВАЖНО: heavy TraceEventSession leak claim
ОПРОВЕРГНУТ (TraceEventSession освобождается через using var session) — НЕ
исправляй его. Scope строго ограничен dispose'ом ManualResetEventSlim/monitor:
вызывай Dispose (не только Stop) на ETW monitor при connect teardown в VpnEngine
(_etw?.Stop() -> Dispose), чтобы _sessionReady SafeWaitHandle не ждал finalizer.
Проверь, что строки _etw ещё читаются как try { _etw?.Stop(); } catch { } /
_etw = null; после P02 rewrite. Напиши тест: несколько connect/disconnect cycles
dispose'ят каждый monitor. НЕ запускай локальные build/test/app/binary/service,
не делай live мутаций. Только чтение/поиск/редактирование и запись тестов.
Commit/push/CI делает orchestrator. Без release/merge/tag/deploy. Без emoji.
Заполни Outcome шаблоном PENDING.
```

---

## 4. Global execution constraint (owner-authoritative)

Every R-package is executed through Qwen Code under the code-only mode defined
in the prompt pool §0. Qwen MAY read/search/edit code and write tests. Qwen MUST
NOT run local builds/tests/apps/binaries/services/installers, restore packages,
download third-party binaries, or perform VM/WinRM/ADB/MCP/live/platform
mutations. The orchestrator commits, pushes, and validates only in remote GitHub
CI. No release / merge / tag / deploy. No emoji in any artifact.

## 5. Brief structure contract

Every brief in this set follows the phase-task-launcher brief template
(`.agents/skills/phase-task-launcher/references/brief-template.md`) extended with
the audit-specific sections required by the orchestrator:

1. Header (Owner / Branch / Base / Roadmap ref / Effort / Risk / Blast radius / Rollback).
2. Final P00 verdict / severity / confidence + corrected scope.
3. Verified current root cause (file:line + caller evidence) at commit `b39a28c3`.
4. Why / What / How (ordered minimal steps).
5. Affected callers/consumers + invariants to preserve.
6. Exact expected file list + explicit non-goals.
7. Direct regression tests that fail when the old defect is restored.
8. Security / concurrency / data-loss / platform review (where applicable).
9. Dependencies / overlaps with P1 draft branches and other R packages.
10. Remote-only verification gates + PENDING Outcome template.
11. Rollback.
12. Self-contained copyable Qwen prompt.
