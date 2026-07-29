# Phase 2 — R12 — Full-tunnel kill-switch intent dropped by synthetic profile

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r12-full-tunnel-killswitch-2026-07-29`
**Base**: `codex/qwen-audit-r01-firewall-wiring-2026-07-29` (R01 branch) — R12's
regression test reuses the capturing `FirewallFactory` fake that R01 adds to
`StartupPipelineTests.cs` (R01 changes it from throwing to capturing). Fallback
base `origin/main` ONLY if R01 has already merged; in that case re-add the
capturing fake locally. See §10.
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R12)
**IDs**: FW-3 (newly discovered during Qwen R01 implementation review; NOT one of
the original 22 P00 survivors)
**Effort**: ~1-2 h
**Risk**: MEDIUM (kill-switch correctness; must NOT over-arm — fail-closed intent
preserved, no new global allow path)
**Blast radius**: `VPNRouter.Core/Services/StartupPipeline.cs` (full-tunnel
profile-resolution branch only), `VPNRouter.Tests/StartupPipelineTests.cs`
(+1 regression test, +1 contrast guard) · ~+25 LOC · runtime: whether the
firewall kill-switch arms in full-tunnel mode
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| FW-3 | (new) | CONFIRMED | P2 | High |

This defect was discovered while reviewing the firewall wiring for R01
(FW-1/FW-2/TEST-1). It is independent of the IPv6 family mismatch (R01) and of
the missing pipeline test (TEST-1), although R12's regression test rides the same
test seam R01 opens.

**Verdict: CONFIRMED.** In full-tunnel mode the pipeline replaces the user's
selected profile with a synthetic `FullTunnel` profile whose `BlockOnVpnFail`
defaults to `false`. The production call that creates the firewall kill-switch
rules is gated on that profile flag, so the user's per-profile kill-switch intent
is silently dropped and the full-tunnel kill-switch is unreachable. On Linux/macOS
this is total: full-tunnel is the ONLY mode their kill-switch can arm in, so it is
fully dead there. On Windows the per-process kill-switch is likewise never created
in full-tunnel because the same gate stays false.

Corrected scope:

- The mechanism is real and reachable through normal desktop usage (select a
  profile that carries `BlockOnVpnFail=true`, then switch Routing Mode to Full
  Tunnel; the selection persists but is discarded).
- There is **no global desktop kill-switch toggle** that could compensate
  (verified: no `BlockOnVpnFail` field on `AppConfig`; no `.axaml` and no
  `VPNRouter.App` code reference `BlockOnVpnFail`). The intent is purely
  per-profile, so the per-profile value is the only legitimate source to honour.
- Android is a DIFFERENT mechanism and is OUT of scope: it stores a global
  `block_on_vpn_fail` bool in `AndroidStorage` and hard-sets `RoutingMode="full"`;
  it does not flow through `StartupPipeline`'s synthetic-profile path.

## 2. Verified current root cause (commit `b39a28c3`)

Call path (all citations verified against this worktree HEAD == `origin/main`):

1. `VPNRouter.Core/Services/StartupPipeline.cs:686-687` — `isFullTunnel` derived
   from `settings.App.RoutingMode`.
2. `StartupPipeline.cs:688` — `var profileName = settings.ActiveProfile;` — the
   user's selected profile name IS still read in full-tunnel mode.
3. `StartupPipeline.cs:702-705` — the full-tunnel branch:
   ```csharp
   _host.Logger?.Information(
       "[StartupPipeline] Full-tunnel mode — ignoring ActiveProfile '{Profile}' and skipping process scan",
       profileName ?? "(empty)");
   activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only" };   // :705
   ```
   `BlockOnVpnFail` is not set → it takes the model default.
4. `VPNRouter.Core/Models/Profile.cs:33` — `public bool BlockOnVpnFail { get; set; } = false;`
   → the synthetic `FullTunnel` profile always has `BlockOnVpnFail == false`.
5. `StartupPipeline.cs:458` — `await DeployAndSetupFirewallPhaseAsync(settings, profile, scanResult, ct);`
   passes that synthetic profile as `profile`.
6. `StartupPipeline.cs:1084` — the production gate:
   ```csharp
   if (profile.BlockOnVpnFail)
   {
       var isFullTunnel = (settings.App.RoutingMode ?? "split")
           .Equals("full", StringComparison.OrdinalIgnoreCase);          // :1090-1091
       firewall.CreateBlockRules(scanResult.ProcessNames, isFullTunnel);  // :1092
       _host.OnStatus("Firewall block rules created (disabled)");
   }
   ```
   Because `profile.BlockOnVpnFail == false` in full-tunnel, `CreateBlockRules`
   is NEVER called → no block rules are created → the kill-switch cannot arm.
7. Platform managers confirm full-tunnel is the only arming mode on Unix:
   - `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:93` — `if (!isFullTunnel) { _armed = false; ... return; }`
   - `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs:108` — same guard.
   So on Linux/macOS the kill-switch arms ONLY when `isFullTunnel==true`, yet the
   `:1084` gate prevents the call from ever being made in that exact mode.
8. `VPNRouter.Core/Services/FirewallManager.cs:218` (Windows) — `_ = isFullTunnel;`
   (Windows blocks per-process and ignores the routing flag), but it is still
   never reached in full-tunnel because the `:1084` gate is upstream of every
   platform impl.
9. Runtime teardown is gated on the same flag — `VPNRouter.Core/Services/VpnEngine.cs:834`
   `if (_activeProfile?.BlockOnVpnFail == true)` — so a synthetic false profile
   also skips rule cleanup symmetrically (consistent, but both sides dead).

Where the user's intent actually lives:

- Per-profile only. Built-in catalogue profiles carry it
  (`VPNRouter.Core/Services/ProfileManager.cs:426` `Discord_Privacy` → true,
  `:440` `Work_Suite` → true, `:473` `Browsers` → false), as do hand-edited
  `profiles.json` entries.
- Merge semantics already define ownership for multi-select:
  `ProfileManager.cs:184` and `:211` — `BlockOnVpnFail = ....Any(p => p.BlockOnVpnFail)`
  (true wins). R12 reuses this exact resolver.
- No global desktop toggle exists (see §1 / §9). `AppConfig` has `StrictMode`
  (faster crash polling) and `DnsLeakLockdown` (DNS-port block) but nothing that
  sets the kill-switch; neither feeds `profile.BlockOnVpnFail`.

## 3. Why

A user who enables the kill-switch on a profile (or relies on a built-in profile
that ships with `BlockOnVpnFail=true`) and then runs Full Tunnel mode gets NO
kill-switch on any platform — the precise configuration where a kill-switch
matters most (all traffic tunnelled; a tunnel drop leaks everything). On
Linux/macOS the kill-switch is additionally full-tunnel-only, so this defect makes
it completely unreachable there. The intent is silently discarded with only an
"ignoring ActiveProfile" log line that does not mention the kill-switch.

## 4. What

Single root-cause fix in the full-tunnel branch of the profile-resolution phase:
carry the effective selected profile's `BlockOnVpnFail` into the synthetic
`FullTunnel` profile. The owner of the intent is `settings.ActiveProfile`
(already read at `:688`), resolved through the SAME tolerant merge the split
branch uses (`ProfileManager.MergeProfilesTolerant`). No new setting, no new
abstraction, no UI change.

```diff
         Profile activeProfile;
         if (isFullTunnel)
         {
             _host.Logger?.Information(
                 "[StartupPipeline] Full-tunnel mode — ignoring ActiveProfile '{Profile}' and skipping process scan",
                 profileName ?? "(empty)");
-            activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only" };
+            // R12 (FW-3): honour the user's per-profile kill-switch intent in
+            // full-tunnel mode. settings.ActiveProfile persists across routing-mode
+            // switches and is the only carrier of BlockOnVpnFail; the synthetic
+            // profile previously defaulted it to false, making the full-tunnel
+            // kill-switch unreachable (DeployAndSetupFirewallPhaseAsync gate at
+            // :1084 stayed false). Empty/unresolvable selection -> false (the
+            // prior behaviour; no over-arming).
+            var blockOnVpnFail = false;
+            if (!string.IsNullOrEmpty(profileName))
+            {
+                var names = profileName.Split(',',
+                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
+                blockOnVpnFail = manager.MergeProfilesTolerant(names, out _)?.BlockOnVpnFail == true;
+            }
+            activeProfile = new Profile
+            {
+                Name = "FullTunnel",
+                DnsMode = "vpn_only",
+                BlockOnVpnFail = blockOnVpnFail
+            };
         }
```

`manager` is already in scope (constructed at `StartupPipeline.cs:670`).
`MergeProfilesTolerant` is the existing tolerant resolver used by the split branch
(`:712`); reusing it keeps multi-profile (`"A,B"`) ownership identical to split
mode (true-wins via `ProfileManager.cs:184`).

### Explicit implementation decision / semantics gate

The code evidence establishes that `settings.ActiveProfile` is the sole per-profile
carrier of `BlockOnVpnFail` and that it persists across routing-mode switches, so
copying it is the smallest fix that honours EXISTING per-profile semantics without
inventing a global setting. This is a product-semantics decision the implementer
must treat as a gate:

- **Decision taken here**: full-tunnel kill-switch intent = the tolerant-merge of
  the persisted `settings.ActiveProfile`. Empty/unresolvable selection → `false`
  (unchanged from today; never over-arm).
- **Genuine ambiguity the owner must confirm (Simple mode).** The pipeline cannot
  tell Simple-mode full-tunnel from Advanced-mode full-tunnel — it only sees
  `settings.ActiveProfile` + `settings.App.RoutingMode`. In Simple mode the design
  comment says full tunnel "ignores the profile field entirely"
  (`MainWindowViewModel.SimpleMode.cs:474`), yet `ActiveProfile` is left holding
  `SimpleSplitProfile` (`:137` =
  `"Discord_Privacy,Messengers,AI_Tools,Browsers,Work_Suite,Streaming,Gaming,Privacy_Shell"`),
  which contains `BlockOnVpnFail=true` profiles (`Discord_Privacy`, `Work_Suite`).
  With this fix, Simple-mode full-tunnel on Linux/macOS will therefore ARM the
  kill-switch — a behaviour change vs today. This is the fail-closed direction (and
  the only way the Linux/macOS kill-switch becomes reachable at all), so it is the
  default recommendation; but because it contradicts the "ignores the profile field
  entirely" Simple-mode comment, the implementer MUST flag it in the PR description
  for explicit owner sign-off. The pipeline layer cannot scope the inheritance to
  Advanced mode only without a new setting (forbidden), so the only alternatives are
  "inherit everywhere" (default) or "WONTFIX + docs note" (owner call).
- **If the owner instead wants an independent full-tunnel kill-switch** (a dedicated
  global toggle), that is a separate feature and a NON-GOAL of R12. Do not add a
  global `AppConfig` field in this package. Flag it as a follow-up only.

## 5. How (ordered minimal steps)

1. Read `StartupPipeline.cs:660-745` (profile-resolution phase) and `:1070-1100`
   (firewall phase) fully; confirm `manager` is in scope at the full-tunnel branch
   and that `MergeProfilesTolerant(names, out _)` is the resolver the split branch
   uses.
2. Apply the §4 diff to the full-tunnel branch ONLY (`:705`). Do not touch the
   `CustomConfig` synthetic (`:734`) or the fallback else (`:738`) — see §8.
3. Add the regression test + contrast guard to `StartupPipelineTests.cs` using the
   capturing `FirewallFactory` fake (from R01; see §10).
4. Static review: confirm no path arms the kill-switch when the effective profile
   has `BlockOnVpnFail=false` or when `ActiveProfile` is empty.

### Tests written

- `StartupPipelineTests.SetupFirewall_FullTunnel_SelectedProfileBlockOnFail_ArmsKillSwitch`
  — ColdStart, `RoutingMode="full"`, `ActiveProfile` = a profile with
  `BlockOnVpnFail=true` (from the test catalogue), capturing fake firewall.
  Asserts `CreateBlockRules` WAS called with `isFullTunnel==true`. **Fails on old
  code** (synthetic profile false → gate at `:1084` skips the call).
- `StartupPipelineTests.SetupFirewall_FullTunnel_NoBlockIntent_DoesNotArm` —
  ColdStart, `RoutingMode="full"`, `ActiveProfile` empty (or a
  `BlockOnVpnFail=false` profile). Asserts `CreateBlockRules` NOT called. Guards
  against over-arming (the fix must NOT turn full-tunnel into always-armed).

### Verification approach

Pipeline fake-capture (no real firewall, no platform mutation). The capturing fake
records whether `CreateBlockRules` was invoked and with what `isFullTunnel`.
Actual execution happens in remote GitHub CI after the orchestrator pushes.

## 6. Affected callers / consumers + invariants

- `StartupPipeline.cs:705` consumers: the synthetic `FullTunnel` profile flows to
  `DeployAndSetupFirewallPhaseAsync` (`:458` → gate `:1084`) and to
  `_host.SetActiveProfile` (`:765`), which later drives `VpnEngine.cs:834`
  teardown cleanup. Invariant: both the arm path (`:1084`) and the cleanup path
  (`VpnEngine.cs:834`) read the SAME flag, so copying it once keeps arm/cleanup
  symmetric.
- `MergeProfilesTolerant` (`ProfileManager.cs:160-189`): unchanged; R12 only adds a
  caller. Invariant: true-wins merge semantics (`:184`) are reused verbatim.
- Split-tunnel path (`:707-728`): untouched — its `BlockOnVpnFail` already comes
  from the resolved/merged profile.
- Invariant to preserve: when the effective selection has `BlockOnVpnFail=false`
  (or none), behaviour is byte-identical to today (no rules created).
- Existing baseline `VpnEngineHotReloadLifecycleTests.cs:550-552` ("FullTunnel
  synthetic profile has BlockOnVpnFail=false → Phase 6 didn't call CreateBlockRules")
  STAYS GREEN by design: that test's settings use `ActiveProfile = ""` (`:206`) with
  `RoutingMode = "full"` (`:163`), which hits the empty-selection → `false` default,
  so `CreateBlockRulesCount == 0` (`:552`) still holds. The implementer should confirm
  this on the CI run and need NOT modify the test; if a future edit gives that test a
  non-empty `BlockOnVpnFail=true` selection, its comment + assertion must be updated to
  the corrected behaviour (never weakened to hide the change).

## 7. Exact expected file list

- `VPNRouter.Core/Services/StartupPipeline.cs` (edit full-tunnel branch `:705`)
- `VPNRouter.Tests/StartupPipelineTests.cs` (+2 tests using the capturing fake)

## 8. Non-goals

- Do NOT add a global `AppConfig` kill-switch toggle or any desktop UI editor
  (none exists today; adding one is a separate feature).
- Do NOT change the `:1084` gate, the `isFullTunnel` derivation (`:1090`), or any
  platform `CreateBlockRules` impl (the IPv6 family work is R01).
- Do NOT modify the `CustomConfig` synthetic profile (`:734`). Observation only:
  custom-config mode also yields `BlockOnVpnFail=false`; whether the selected
  profile's intent should carry there is a separate question with no evidence of
  user impact — out of scope unless proven otherwise.
- Do NOT touch the fallback else synthetic (`:738`) — it is unreachable after the
  `:690` guard (`profileName` empty + not full + not custom throws first).
- Do NOT touch Android (`AndroidStorage`/`AndroidConfigBuilder`) — different
  mechanism, global bool, not via `StartupPipeline`.
- Do NOT apply nftables/PF/netsh anywhere (code-only).

## 9. Security / concurrency / data-loss / platform review

- **Security**: this is a fail-closed correctness fix — it makes a kill-switch
  reachable that the user already asked for. The only risk is over-arming
  (creating block rules the user did NOT request); the contrast guard test
  (`NoBlockIntent_DoesNotArm`) and the empty-selection → false default prevent
  that. No new allow path is introduced.
- **Platform**: no platform impl is changed; the fix is upstream of all three
  firewall managers and merely lets the existing, already-correct per-platform
  arming logic run.
- **Concurrency**: none (profile resolution is single-threaded in the pipeline).
- **Data-loss**: none; no persisted state changes (`settings.ActiveProfile` is
  read, not written, by this fix).

## 10. Dependencies / overlaps

- **R01 (FW-1/FW-2/TEST-1)**: R01 changes `StartupPipelineTests.cs`'s
  `FirewallFactory` from throwing to a capturing fake and adds two pipeline tests.
  R12's tests reuse that capturing fake. → **Base R12 on the R01 branch**
  (`codex/qwen-audit-r01-firewall-wiring-2026-07-29`). If R01 has already merged
  into `origin/main`, base on `origin/main` instead (the capturing fake is then
  present in main). R01 does NOT edit `StartupPipeline.cs` production code (it only
  adds tests), so there is no production-code conflict between R01 and R12; the
  only shared file is `StartupPipelineTests.cs`.
- No P1 draft branch touches `StartupPipeline.cs` or `StartupPipelineTests.cs`.
- No other R-package touches the profile-resolution phase.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [ ] Gate 2 — Tests green (remote CI): the 2 new pipeline tests pass; R01's pipeline tests and all existing firewall/lifecycle tests stay green.
- [ ] Gate 3 — Docs: brief Outcome filled; zone CLAUDE.md unchanged (no architecture change).
- [ ] Gate 4 — Self-review: security-relevant (kill-switch) — static review that no false-intent path arms.
- [ ] Gate 5 — MCP verify: N/A (no UI surface; Core + tests only).
- [ ] Gate 6 — Characterization diff: N/A (not a god-file split), but the `BlockOnVpnFail=false` / empty-selection ruleset behaviour must be unchanged.

## 12. Outcome (PENDING — filled after merge)

**Status**: PENDING
**Commits**: PENDING
**Pushed**: PENDING
**Test deltas**: PENDING
**Files changed**: PENDING
**§4 Simple-mode decision gate resolution**: PENDING (inherit-everywhere default shipped / WONTFIX-by-owner)

**Gate results:**
- [ ] Gate 1: PENDING
- [ ] Gate 2: PENDING
- [ ] Gate 3: PENDING
- [ ] Gate 4: PENDING
- [-] Gate 5: N/A — Core + tests only
- [-] Gate 6: N/A — not a god-file split

**Surprises encountered**: PENDING
**Follow-ups spawned**: PENDING

## 13. Rollback

`git revert <commit>` on the R12 branch, or delete
`codex/qwen-audit-r12-full-tunnel-killswitch-2026-07-29`. The full-tunnel
kill-switch reverts to unreachable (the prior behaviour); no persistent state is
written.

## 14. Self-contained copyable Qwen prompt

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
НЕ вызван (guard от over-arming). Проверь, что baseline
VpnEngineHotReloadLifecycleTests.cs:550-552 (ActiveProfile="") остаётся зелёным.
Flag в PR description: §4 Simple-mode decision gate (Simple full-tunnel теперь
армит kill-switch на Linux/macOS, потому что ActiveProfile держит SimpleSplitProfile
с BlockOnVpnFail=true профилями — нужно явное owner sign-off; альтернатива WONTFIX).
НЕ запускай локальные build/test/app/binary/
service/installer, не применяй nftables/PF/netsh нигде, не скачивай binary, не
делай VM/WinRM/ADB/MCP/live мутаций. Только чтение/поиск/редактирование кода и
запись тестов. Commit/push/CI делает orchestrator. Без release/merge/tag/deploy.
Без emoji. Подготовь diff и заполни секцию Outcome шаблоном PENDING.
```
